using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaruCast;

static class UpdateChecks
{
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static object Asset(string tag, string name, byte[] data) => new { name, state = "uploaded", size = data.Length,
        digest = "sha256:" + Hash(data), browser_download_url = AppUpdateService.AssetUrl(tag, name).AbsoluteUri };
    private static object Release(string tag, bool draft = false, bool installed = false) => new
    {
        tag_name = tag, draft, prerelease = true, published_at = "2026-11-13T00:00:00Z",
        assets = new[] { Asset(tag, installed ? AppUpdateService.SetupName : AppUpdateService.ZipName, new byte[10]),
            Asset(tag, (installed ? AppUpdateService.SetupName : AppUpdateService.ZipName) + ".sha256", new byte[88]) }
    };
    private static JsonDocument Releases(params object[] releases) => JsonDocument.Parse(JsonSerializer.Serialize(releases));
    internal static Dictionary<string, byte[]> Payload(string label) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["SaruCast.exe"] = Encoding.UTF8.GetBytes("exe-" + label),
        ["SaruCast.dll"] = File.ReadAllBytes(typeof(SaruCast.Program).Assembly.Location),
        ["docs/notes.txt"] = Encoding.UTF8.GetBytes(label)
    };
    internal static byte[] Package(Dictionary<string, byte[]> entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var entry in entries) { using var output = archive.CreateEntry(entry.Key).Open(); output.Write(entry.Value); }
            using var writer = new StreamWriter(archive.CreateEntry("SHA256SUMS.txt").Open(), new UTF8Encoding(false));
            foreach (var entry in entries) writer.WriteLine(Hash(entry.Value) + "  " + entry.Key);
        }
        return buffer.ToArray();
    }
    internal static void WritePayload(string root, Dictionary<string, byte[]> entries)
    {
        Directory.CreateDirectory(root);
        foreach (var entry in entries) { string path = UpdateFiles.Under(root, entry.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, entry.Value); }
        File.WriteAllLines(Path.Combine(root, "SHA256SUMS.txt"), entries.Select(e => Hash(e.Value) + "  " + e.Key));
    }
    private static async Task RejectAsync(Func<Task> action, string name, Action<bool, string> check)
    {
        try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException) { check(true, name); return; }
        throw new Exception("Expected rejection: " + name);
    }
    public static async Task RunAsync(Action<bool, string> check)
    {
        using (var json = Releases(Release("v0.1.3"), Release("v0.1.4"), Release("v0.1.8", draft: true), Release("v0.1.5-beta.1")))
            check(AppUpdateService.SelectRelease(json.RootElement, new(0,1,3,0), false)?.Version == new Version(0,1,4), "Updater selects newer published numeric preview and ignores drafts / unsupported tags");
        using (var json = Releases(Release("v0.1.4")))
            check(AppUpdateService.SelectRelease(json.RootElement, new(0,1,4), false) == null && AppUpdateService.SelectRelease(json.RootElement, new(0,1,5), false) == null,
                "Updater never offers current version or downgrade");
        using (var json = Releases(Release("v0.1.5", installed: true), Release("v0.1.4")))
            check(AppUpdateService.SelectRelease(json.RootElement, new(0,1,3), true)?.Package.Name == AppUpdateService.SetupName, "Installed distribution selects setup and matching checksum");
        string hostile = JsonSerializer.Serialize(new[] { Release("v0.1.5") }).Replace("https://github.com/", "https://example.com/");
        using (var json = JsonDocument.Parse(hostile)) check(AppUpdateService.SelectRelease(json.RootElement, new(0,1,3), false) == null, "Updater rejects release assets outside fixed repository download URLs");
        check(!AppUpdateService.TryVersion("v0.1.4/../../bad", out _) && !AppUpdateService.TryVersion("v1.0.0+bad", out _), "Updater rejects ambiguous version tags");
        byte[] zip = Package(Payload("new")); string digest = Hash(zip);
        check(AppUpdateService.ParseChecksum(digest + "  " + AppUpdateService.ZipName + "\r\n", AppUpdateService.ZipName) == digest, "SHA-256 sidecar accepts published format");
        await RejectAsync(() => { AppUpdateService.ParseChecksum(digest + "  wrong.zip", AppUpdateService.ZipName); return Task.CompletedTask; }, "SHA-256 sidecar must name exact update asset", check);

        string root = Path.Combine(Paths.Root, "update-unit"); Directory.CreateDirectory(root);
        foreach (string unsafePath in new[] { "../outside", "/absolute", "C:/escape", "dir/../../outside", "NUL.txt", "a/CON", "name. ", "a//b" })
            await RejectAsync(() => { UpdateFiles.Under(root, unsafePath); return Task.CompletedTask; }, "Reject update path: " + unsafePath, check);
        string goodZip = Path.Combine(root, "good.zip"); await File.WriteAllBytesAsync(goodZip, zip);
        await UpdateFiles.ExtractAndVerifyAsync(goodZip, Path.Combine(root, "extracted"), default);
        check(File.ReadAllText(Path.Combine(root, "extracted", "docs", "notes.txt")) == "new", "Update ZIP extraction verifies complete manifest");
        File.AppendAllText(Path.Combine(root, "extracted", "docs", "notes.txt"), "tampered");
        await RejectAsync(() => UpdateFiles.VerifyAsync(Path.Combine(root, "extracted"), default), "Changed update file is rejected before installation", check);
        string malicious = Path.Combine(root, "slip.zip");
        using (var archive = ZipFile.Open(malicious, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open()); writer.Write("bad"); }
        await RejectAsync(() => UpdateFiles.ExtractAndVerifyAsync(malicious, Path.Combine(root, "slip"), default), "ZIP traversal cannot write outside staging directory", check);
        check(!File.Exists(Path.Combine(root, "outside.txt")), "Traversal rejection preserves outside files");
        string duplicate = Path.Combine(root, "duplicate.zip");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create)) { archive.CreateEntry("SaruCast.exe"); archive.CreateEntry("sarucast.EXE"); }
        await RejectAsync(() => UpdateFiles.ExtractAndVerifyAsync(duplicate, Path.Combine(root, "duplicate"), default), "Case-insensitive ZIP name collisions are rejected", check);

        byte[] sha = Encoding.UTF8.GetBytes(digest + "  " + AppUpdateService.ZipName + "\n");
        var release = new AppRelease(AppUpdateService.CurrentVersion, "v" + AppUpdateService.CurrentVersion,
            new(AppUpdateService.ZipName, AppUpdateService.AssetUrl("v" + AppUpdateService.CurrentVersion, AppUpdateService.ZipName), zip.Length, "sha256:" + digest),
            new(AppUpdateService.ZipName + ".sha256", AppUpdateService.AssetUrl("v" + AppUpdateService.CurrentVersion, AppUpdateService.ZipName + ".sha256"), sha.Length, "sha256:" + Hash(sha)));
        using (var service = new AppUpdateService(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sha256") ? sha : zip) })))
        {
            var prepared = await service.DownloadAsync(release, false, null, default);
            check(File.Exists(Path.Combine(prepared.Root, "payload", "SaruCast.dll")), "Downloader verifies sidecar, GitHub digest, byte length and extracted files");
            UpdateFiles.DeleteStage(prepared.Root);
        }
        using (var service = new AppUpdateService(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))))
            await RejectAsync(() => service.CheckAsync(new(0,1,3), false, default), "GitHub rate limit is reported without changing app", check);
        using (var service = new AppUpdateService(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sha256") ? sha : zip[..^1]) })))
            await RejectAsync(() => service.DownloadAsync(release, false, null, default), "Truncated update download is rejected and cleaned up", check);
        using (var service = new AppUpdateService(new Handler(_ => throw new OperationCanceledException())))
            await RejectAsync(() => service.DownloadAsync(release, false, null, default), "Cancelled update download leaves application untouched", check);
        byte[] changedZip = (byte[])zip.Clone(); changedZip[0] ^= 1;
        using (var service = new AppUpdateService(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sha256") ? sha : changedZip) })))
            await RejectAsync(() => service.DownloadAsync(release, false, null, default), "Same-length corrupted download fails SHA-256 before extraction", check);
        check(Directory.GetDirectories(Path.Combine(Paths.Root, "updates")).Length == 0, "Failed and cancelled downloads leave no staged package");

        string target = Path.Combine(root, "portable"), stage = Path.Combine(root, "stage"); Directory.CreateDirectory(stage);
        var old = Payload("old"); old["obsolete.dll"] = "obsolete"u8.ToArray(); WritePayload(target, old);
        File.WriteAllText(Path.Combine(target, "user-notes.txt"), "keep user data");
        var next = Payload("new"); next["added.dll"] = "added"u8.ToArray(); WritePayload(Path.Combine(stage, "payload"), next);
        var plan = new UpdatePlan(1, stage, target, goodZip, digest, AppUpdateService.CurrentVersion.ToString(), AppUpdateService.CurrentVersion.ToString(), false, int.MaxValue, 1, false);
        await UpdateApplier.ApplyPortableAsync(plan);
        check(File.ReadAllText(Path.Combine(target, "docs", "notes.txt")) == "new" && !File.Exists(Path.Combine(target, "obsolete.dll")) && File.Exists(Path.Combine(target, "added.dll")) && File.ReadAllText(Path.Combine(target, "user-notes.txt")) == "keep user data",
            "Portable update replaces owned files, removes obsolete files and preserves user data");
        string rollbackTarget = Path.Combine(root, "rollback"), rollbackStage = Path.Combine(root, "rollback-stage");
        WritePayload(rollbackTarget, old); WritePayload(Path.Combine(rollbackStage, "payload"), next);
        var rollbackPlan = plan with { Target = rollbackTarget, Root = rollbackStage };
        await RejectAsync(() => UpdateApplier.ApplyPortableAsync(rollbackPlan, count => { if (count == 3) throw new IOException("injected write failure"); }), "Interrupted file replacement reports failure", check);
        check(old.All(e => File.ReadAllBytes(UpdateFiles.Under(rollbackTarget, e.Key)).SequenceEqual(e.Value)) && !File.Exists(Path.Combine(rollbackTarget, "added.dll")), "Portable update failure restores all replaced files and original manifest");
        string lateTarget = Path.Combine(root, "late-rollback"), lateStage = Path.Combine(root, "late-rollback-stage");
        WritePayload(lateTarget, old); WritePayload(Path.Combine(lateStage, "payload"), next);
        byte[] oldManifest = File.ReadAllBytes(Path.Combine(lateTarget, "SHA256SUMS.txt"));
        await RejectAsync(() => UpdateApplier.ApplyPortableAsync(plan with { Target = lateTarget, Root = lateStage }, count => { if (count == next.Count + 1) throw new IOException("failure after manifest replacement"); }), "Late update failure is reported after replacing manifest", check);
        check(File.ReadAllBytes(Path.Combine(lateTarget, "SHA256SUMS.txt")).SequenceEqual(oldManifest) && !File.Exists(Path.Combine(lateTarget, "added.dll")) && old.All(e => File.ReadAllBytes(UpdateFiles.Under(lateTarget, e.Key)).SequenceEqual(e.Value)),
            "Late rollback restores original manifest and removes newly created files");
        string lockedStage = Path.Combine(root, "locked-stage"); WritePayload(Path.Combine(lockedStage, "payload"), next);
        using (File.Open(Path.Combine(rollbackTarget, "SaruCast.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await RejectAsync(() => UpdateApplier.ApplyPortableAsync(rollbackPlan with { Root = lockedStage }), "Another running instance blocks update before modification", check);
        File.WriteAllText(Path.Combine(rollbackTarget, "added.dll"), "user-owned");
        string collisionStage = Path.Combine(root, "collision-stage"); WritePayload(Path.Combine(collisionStage, "payload"), next);
        await RejectAsync(() => UpdateApplier.ApplyPortableAsync(rollbackPlan with { Root = collisionStage }), "Update does not overwrite a user file with a newly added filename", check);
        check(File.ReadAllText(Path.Combine(rollbackTarget, "added.dll")) == "user-owned", "User filename collision preserves original file");
        await RejectAsync(() => { UpdateFiles.DeleteStage(root); return Task.CompletedTask; }, "Updater cleanup refuses non-owned directories", check);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
