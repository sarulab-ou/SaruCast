using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SaruCast;

internal sealed record UpdateAsset(string Name, Uri Url, long Size, string? Digest);
internal sealed record AppRelease(Version Version, string Tag, UpdateAsset Package, UpdateAsset Checksum);
internal sealed record PreparedUpdate(string Root, string Asset, string Sha256, AppRelease Release, bool Installed);

internal sealed class AppUpdateService : IDisposable
{
    internal const string Repository = "sarulab-ou/SaruCast";
    internal const string ZipName = "SaruCast-win-x64.zip";
    internal const string SetupName = "SaruCast-Setup-win-x64.exe";
    private readonly HttpClient client;
    internal static Version CurrentVersion => Normalize(typeof(Program).Assembly.GetName().Version!);
    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
    internal AppUpdateService(HttpMessageHandler? handler = null)
    {
        client = handler == null ? new HttpClient() : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SaruCast/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }
    internal static bool TryVersion(string? tag, out Version version)
    {
        version = new(0, 0, 0);
        if (tag == null || !Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$", RegexOptions.CultureInvariant)) return false;
        return Version.TryParse(tag.TrimStart('v'), out version!);
    }
    internal static Uri AssetUrl(string tag, string name) => new($"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{name}");
    internal static AppRelease? SelectRelease(JsonElement releases, Version current, bool installed)
    {
        if (releases.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHubの更新情報の形式が不正です。");
        var candidates = new List<AppRelease>();
        foreach (var item in releases.EnumerateArray())
        {
            if (item.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (!item.TryGetProperty("published_at", out var published) || published.ValueKind != JsonValueKind.String) continue;
            if (!item.TryGetProperty("tag_name", out var tagValue) || !TryVersion(tagValue.GetString(), out var version) || version <= Normalize(current)) continue;
            if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) continue;
            string tag = tagValue.GetString()!; string name = installed ? SetupName : ZipName;
            UpdateAsset? Find(string wanted)
            {
                var matches = assets.EnumerateArray().Where(a => a.TryGetProperty("name", out var n) && n.GetString() == wanted).ToArray();
                if (matches.Length != 1) return null;
                var asset = matches[0];
                if (!asset.TryGetProperty("state", out var state) || state.GetString() != "uploaded" || !asset.TryGetProperty("size", out var length) || !length.TryGetInt64(out long size)) return null;
                if (size <= 0 || size > (wanted.EndsWith(".sha256", StringComparison.Ordinal) ? 4096 : 512L * 1024 * 1024)) return null;
                if (!asset.TryGetProperty("browser_download_url", out var address) || !Uri.TryCreate(address.GetString(), UriKind.Absolute, out var uri) || uri != AssetUrl(tag, wanted)) return null;
                string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (digest != null && !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)) return null;
                return new(wanted, uri, size, digest);
            }
            if (Find(name) is { } package && Find(name + ".sha256") is { } checksum) candidates.Add(new(version, tag, package, checksum));
        }
        // Current releases are previews with numeric version tags; include them too.
        return candidates.OrderByDescending(c => c.Version).FirstOrDefault();
    }
    internal async Task<AppRelease?> CheckAsync(Version current, bool installed, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429) throw new IOException("GitHubの更新確認回数の上限です。時間をおいて再確認してください。");
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("GitHubの公開リリースを取得できません。");
        response.EnsureSuccessStatusCode();
        await using var data = await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] bytes = await ReadBoundedAsync(data, 4 * 1024 * 1024, timeout.Token);
        using var json = JsonDocument.Parse(bytes);
        return SelectRelease(json.RootElement, current, installed);
    }
    internal async Task<PreparedUpdate> DownloadAsync(AppRelease release, bool installed, IProgress<int>? progress, CancellationToken ct)
    {
        string root = Path.Combine(Paths.Root, "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(10));
            byte[] checksum = await FetchBytesAsync(release.Checksum, 4096, timeout.Token);
            string expected = ParseChecksum(Encoding.UTF8.GetString(checksum), release.Package.Name);
            if (release.Package.Digest is { } digest && !digest[7..].Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("GitHubの更新ファイル情報とSHA-256が一致しません。");
            string path = Path.Combine(root, release.Package.Name);
            using var response = await client.GetAsync(release.Package.Url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != release.Package.Size) throw new InvalidDataException("更新ファイルのサイズが一致しません。");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[65536]; long count = 0; int lastPercent = -1;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, timeout.Token); if (read == 0) break;
                    count += read; if (count > release.Package.Size) throw new InvalidDataException("更新ファイルが想定サイズを超えました。");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    int percent = (int)(count * 100 / release.Package.Size);
                    if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                }
                if (count != release.Package.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新ファイルのSHA-256が一致しません。元のアプリは変更していません。");
            }
            if (!installed) await UpdateFiles.ExtractAndVerifyAsync(path, Path.Combine(root, "payload"), timeout.Token);
            else if (UpdateFiles.FileVersion(path) != release.Version) throw new InvalidDataException("セットアップのバージョンが更新情報と一致しません。");
            return new(root, path, expected, release, installed);
        }
        catch { UpdateFiles.DeleteStage(root); throw; }
    }
    private async Task<byte[]> FetchBytesAsync(UpdateAsset asset, int limit, CancellationToken ct)
    {
        using var response = await client.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        byte[] result = await ReadBoundedAsync(stream, limit, ct);
        if (result.LongLength != asset.Size) throw new InvalidDataException("更新確認ファイルのサイズが一致しません。");
        if (asset.Digest is { } digest && !Convert.ToHexString(SHA256.HashData(result)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新確認ファイルのSHA-256が一致しません。");
        return result;
    }
    internal static string ParseChecksum(string text, string name)
    {
        var match = Regex.Match(text.Trim().TrimStart('\uFEFF'), @"\A([0-9a-fA-F]{64})  " + Regex.Escape(name) + @"\z", RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException("更新用SHA-256ファイルの形式が不正です。");
        return match.Groups[1].Value.ToLowerInvariant();
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream input, int limit, CancellationToken ct)
    {
        using var output = new MemoryStream(); byte[] buffer = new byte[16384];
        while (true) { int count = await input.ReadAsync(buffer, ct); if (count == 0) break; if (output.Length + count > limit) throw new InvalidDataException("更新情報のサイズが上限を超えました。"); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    public void Dispose() => client.Dispose();
}
