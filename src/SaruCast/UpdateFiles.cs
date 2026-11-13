using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SaruCast;

internal static class UpdateFiles
{
    internal static string Under(string root, string relative)
    {
        string normalized = relative.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Split('/').Any(s => s.Length == 0 || s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ') || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Regex.IsMatch(s, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) throw new InvalidDataException("更新ファイルのパスが不正です。");
        string path = Path.GetFullPath(Path.Combine(root, normalized));
        if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新ファイルがアプリの外を参照しています。");
        NoLinks(path); return path;
    }
    internal static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンクを含むフォルダーは自動更新できません。ZIP版で手動更新してください。");
    }
    internal static Dictionary<string, string> Manifest(string root)
    {
        string path = Under(root, "SHA256SUMS.txt");
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("更新ファイル一覧が大きすぎます。");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(path))
        {
            var match = Regex.Match(line, @"^([0-9a-fA-F]{64})  (.+)$", RegexOptions.CultureInvariant);
            if (!match.Success) throw new InvalidDataException("更新ファイル一覧の形式が不正です。");
            string name = match.Groups[2].Value.Replace('\\', '/'); Under(root, name);
            if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) || !result.TryAdd(name, match.Groups[1].Value)) throw new InvalidDataException("更新ファイル一覧に重複があります。");
            if (result.Count > 4096) throw new InvalidDataException("更新ファイル数が上限を超えました。");
        }
        if (!result.ContainsKey("SaruCast.exe") || !result.ContainsKey("SaruCast.dll")) throw new InvalidDataException("更新ZIPにSaruCastがありません。");
        return result;
    }
    internal static async Task VerifyAsync(string root, CancellationToken ct)
    {
        var entries = Manifest(root);
        var actual = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Where(p => !p.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (actual.Length != entries.Count || actual.Any(p => !entries.ContainsKey(p))) throw new InvalidDataException("ZIP内のファイルと更新ファイル一覧が一致しません。");
        foreach (var entry in entries) await VerifyHashAsync(Under(root, entry.Key), entry.Value, ct);
    }
    internal static async Task VerifyHashAsync(string path, string expected, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (!Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新ファイルのSHA-256が一致しません。");
    }
    internal static async Task ExtractAndVerifyAsync(string zip, string destination, CancellationToken ct)
    {
        if (Directory.Exists(destination)) throw new IOException("更新展開先が既に存在します。");
        Directory.CreateDirectory(destination); NoLinks(destination);
        using var archive = ZipFile.OpenRead(zip);
        long total = 0; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (archive.Entries.Count > 8192) throw new InvalidDataException("更新ZIPのファイル数が上限を超えました。");
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            string relative = entry.FullName.Replace('\\', '/'); bool directory = relative.EndsWith('/');
            string path = Under(destination, directory ? relative.TrimEnd('/') : relative);
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || !names.Add(relative.TrimEnd('/'))) throw new InvalidDataException("更新ZIPにリンクまたは重複パスがあります。");
            if (directory) { Directory.CreateDirectory(path); continue; }
            total = checked(total + entry.Length); if (total > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("更新ZIPの展開サイズが上限を超えました。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open(); await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await input.CopyToAsync(output, ct);
            if (output.Length != entry.Length) throw new InvalidDataException("更新ZIPが途中で切れています。");
        }
        await VerifyAsync(destination, ct);
    }
    internal static Version FileVersion(string path)
    {
        string? raw = FileVersionInfo.GetVersionInfo(path).FileVersion;
        if (!Version.TryParse(raw, out var version)) throw new InvalidDataException("アプリのバージョンを読み取れません。");
        return AppUpdateService.Normalize(version);
    }
    internal static void DeleteStage(string root)
    {
        string updates = Path.GetFullPath(Path.Combine(Paths.Root, "updates"));
        root = Path.GetFullPath(root);
        if (!string.Equals(Path.GetDirectoryName(root), updates, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(root), "N", out _)) throw new InvalidDataException("更新用フォルダーの場所が不正です。");
        NoLinks(root);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
