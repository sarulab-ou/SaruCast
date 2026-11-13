using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SaruCast;

internal sealed record UpdatePlan(int Format, string Root, string Target, string Asset, string Sha256, string NewVersion,
    string OldVersion, bool Installed, int ParentId, long ParentStartUtcTicks, bool Restart);
internal sealed record UpdateResult(bool Success, string Target, string Version, string Message, string Stage, bool RolledBack);

internal static class UpdateManager
{
    internal static bool IsInstalled(string directory)
    {
        string marker = Path.Combine(directory, "SaruCast.install.ini");
        if (!File.Exists(marker)) return false;
        UpdateFiles.NoLinks(marker);
        if (!File.ReadLines(marker).Any(l => l.Trim() == "Product=SaruCast-PerUser-Install-v1")) throw new IOException("インストール情報を確認できません。セットアップで手動更新してください。");
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\SaruCast");
        if (key?.GetValue("InstallDir") is not string value || !SamePath(value, directory)) throw new IOException("インストール先の登録が一致しません。セットアップで手動更新してください。");
        return true;
    }
    internal static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    internal static void EnsureWritable(string directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new IOException("自動更新は通常権限で使ってください。管理者として起動したSaruCastを終了し、通常どおり起動してから更新してください。");
        UpdateFiles.NoLinks(directory);
        if (!File.Exists(Path.Combine(directory, "SaruCast.exe")) || !File.Exists(Path.Combine(directory, "SHA256SUMS.txt"))) throw new IOException("自動更新はZIP版またはインストーラー版から起動して使ってください。");
        string probe = Path.Combine(directory, ".SaruCast-update-" + Guid.NewGuid().ToString("N"));
        try { using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new IOException("アプリのフォルダーへ書き込めません。書き込みできる場所へZIPを展開するか、セットアップで手動更新してください。", e); }
    }
    internal static async Task<string> PrepareAsync(PreparedUpdate update, string target, CancellationToken ct)
    {
        EnsureWritable(target);
        if (IsInstalled(target) != update.Installed) throw new IOException("更新形式を確認できません。もう一度更新を確認してください。");
        var owned = UpdateFiles.Manifest(target);
        if (!update.Installed && UpdateFiles.FileVersion(Path.Combine(update.Root, "payload", "SaruCast.dll")) != update.Release.Version) throw new InvalidDataException("更新ZIPのアプリのバージョンが一致しません。");
        string runner = Path.Combine(update.Root, "runner"); Directory.CreateDirectory(runner);
        // The running version supplies the helper and its bundled runtime. This does
        // not depend on a future release implementing a private command-line protocol.
        foreach (var entry in owned.Where(e => e.Key.Equals("SaruCast.exe", StringComparison.OrdinalIgnoreCase) || e.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || (!e.Key.Contains('/') && e.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))))
        {
            ct.ThrowIfCancellationRequested(); string source = UpdateFiles.Under(target, entry.Key);
            await UpdateFiles.VerifyHashAsync(source, entry.Value, ct);
            string destination = UpdateFiles.Under(runner, entry.Key); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, false);
        }
        foreach (string name in new[] { "SaruCast.exe", "SaruCast.dll", "SaruCast.deps.json", "SaruCast.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll" })
            if (!File.Exists(Path.Combine(runner, name))) throw new InvalidDataException("同梱ランタイムが不足しているため更新できません。");
        using var self = Process.GetCurrentProcess();
        var plan = new UpdatePlan(1, Path.GetFullPath(update.Root), Path.GetFullPath(target), update.Asset, update.Sha256,
            update.Release.Version.ToString(), UpdateFiles.FileVersion(Path.Combine(target, "SaruCast.dll")).ToString(), update.Installed,
            self.Id, self.StartTime.ToUniversalTime().Ticks, true);
        string path = Path.Combine(update.Root, "plan.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(plan), ct); return path;
    }
    internal static void Launch(string plan)
    {
        using var process = Process.Start(StartInfo(plan)) ?? throw new IOException("更新処理を起動できません。");
    }
    internal static ProcessStartInfo StartInfo(string plan)
    {
        string exe = Path.Combine(Path.GetDirectoryName(plan)!, "runner", "SaruCast.exe");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        info.ArgumentList.Add("--apply-update"); info.ArgumentList.Add(plan);
        return info;
    }
    internal static string ResultPath => Path.Combine(Paths.Root, "updates", "last-result.json");
    internal static UpdateResult? TakeResult(string target)
    {
        try
        {
            if (!File.Exists(ResultPath)) return null;
            UpdateFiles.NoLinks(ResultPath);
            var result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(ResultPath));
            if (result == null || !SamePath(target, result.Target)) return null;
            File.Delete(ResultPath); return result;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { Log.Error("Read update result failed", e); return null; }
    }
    internal static async Task CleanupAsync(UpdateResult result)
    {
        // The helper may still be exiting when the new window appears.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(2000);
            try { UpdateFiles.DeleteStage(result.Stage); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            catch (InvalidDataException) { return; }
        }
    }
}

internal static class UpdateApplier
{
    internal static async Task<int> RunAsync(string planPath)
    {
        UpdatePlan? plan = null; bool parentExited = false;
        try
        {
            plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath)) ?? throw new InvalidDataException("更新計画がありません。");
            ValidatePlan(plan, planPath);
            await WaitForParentAsync(plan); parentExited = true;
            Directory.CreateDirectory(Path.GetDirectoryName(UpdateManager.ResultPath)!);
            string lockName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(plan.Target).ToUpperInvariant())));
            using var gate = new FileStream(Path.Combine(Path.GetDirectoryName(UpdateManager.ResultPath)!, lockName + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (UpdateFiles.FileVersion(Path.Combine(plan.Target, "SaruCast.dll")).ToString() != plan.OldVersion) throw new IOException("別の更新によりアプリが変更されました。もう一度確認してください。");
            await UpdateFiles.VerifyHashAsync(plan.Asset, plan.Sha256, default);
            if (plan.Installed) await ApplyInstallerAsync(plan);
            else await ApplyPortableAsync(plan);
            await WriteResultAsync(new(true, plan.Target, plan.NewVersion, $"SaruCast {plan.NewVersion} に更新しました。", plan.Root, false));
            if (plan.Restart) Restart(plan.Target);
            return 0;
        }
        catch (Exception e)
        {
            Log.Error("Update apply failed", e);
            if (plan != null && parentExited)
            {
                bool restored = e is PortableUpdateException p && p.RolledBack;
                string message = restored ? "更新できなかったため、元のアプリに戻しました。" : "更新を適用できませんでした。診断ログを確認して手動更新してください。";
                try { await WriteResultAsync(new(false, plan.Target, plan.NewVersion, message, plan.Root, restored)); } catch (IOException) { }
                if (plan.Restart) try { Restart(plan.Target); } catch (Exception restart) { Log.Error("Restart after update failed", restart); }
            }
            return 1;
        }
    }
    internal static void ValidatePlan(UpdatePlan plan, string path)
    {
        if (plan.Format != 1 || !AppUpdateService.TryVersion(plan.NewVersion, out var next) || !AppUpdateService.TryVersion(plan.OldVersion, out var previous) || next < previous || plan.ParentId <= 0 || plan.ParentId == Environment.ProcessId || plan.ParentStartUtcTicks <= 0) throw new InvalidDataException("更新計画の形式が不正です。");
        string updates = Path.GetFullPath(Path.Combine(Paths.Root, "updates"));
        if (!UpdateManager.SamePath(Path.GetDirectoryName(plan.Root)!, updates) || !Guid.TryParseExact(Path.GetFileName(plan.Root), "N", out _)
            || !UpdateManager.SamePath(path, Path.Combine(plan.Root, "plan.json")) || !UpdateManager.SamePath(AppContext.BaseDirectory, Path.Combine(plan.Root, "runner"))) throw new InvalidDataException("更新処理の場所が不正です。");
        if (!UpdateManager.SamePath(plan.Asset, Path.Combine(plan.Root, plan.Installed ? AppUpdateService.SetupName : AppUpdateService.ZipName)) || !RegexHash(plan.Sha256)) throw new InvalidDataException("更新ファイルの場所が不正です。");
        if (UpdateManager.SamePath(plan.Target, plan.Root) || Path.GetFullPath(plan.Root).StartsWith(Path.GetFullPath(plan.Target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(plan.Target).StartsWith(Path.GetFullPath(plan.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("アプリと更新フォルダーが重なっています。");
        UpdateFiles.NoLinks(plan.Root); UpdateFiles.NoLinks(plan.Target);
        UpdateManager.EnsureWritable(plan.Target);
        if (UpdateManager.IsInstalled(plan.Target) != plan.Installed) throw new IOException("更新形式が一致しません。");
    }
    private static bool RegexHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);
    private static async Task WaitForParentAsync(UpdatePlan plan)
    {
        Process parent;
        try { parent = Process.GetProcessById(plan.ParentId); } catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartUtcTicks) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await parent.WaitForExitAsync(timeout.Token);
        }
    }
    internal static async Task ApplyPortableAsync(UpdatePlan plan, Action<int>? afterReplace = null)
    {
        string payload = Path.Combine(plan.Root, "payload"), backup = Path.Combine(plan.Root, "backup");
        await UpdateFiles.VerifyAsync(payload, default);
        if (UpdateFiles.FileVersion(Path.Combine(payload, "SaruCast.dll")).ToString() != plan.NewVersion) throw new InvalidDataException("更新アプリのバージョンが一致しません。");
        var previous = UpdateFiles.Manifest(plan.Target); var next = UpdateFiles.Manifest(payload);
        string[] names = previous.Keys.Union(next.Keys, StringComparer.OrdinalIgnoreCase).Append("SHA256SUMS.txt").ToArray();
        var existed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Detect locked files and collisions before altering any installed file.
        foreach (string name in names)
        {
            string target = UpdateFiles.Under(plan.Target, name);
            if (Directory.Exists(target)) throw new IOException("更新先に同名のフォルダーがあります。");
            if (!File.Exists(target)) continue;
            if (!previous.ContainsKey(name) && name != "SHA256SUMS.txt") throw new IOException("更新先に利用者の同名ファイルがあります。名前を変更してから更新してください。");
            using (File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            string save = UpdateFiles.Under(backup, name); Directory.CreateDirectory(Path.GetDirectoryName(save)!); File.Copy(target, save, false); existed.Add(name);
        }
        var touched = new List<string>();
        try
        {
            int count = 0;
            foreach (string name in next.Keys.Append("SHA256SUMS.txt"))
            {
                string target = UpdateFiles.Under(plan.Target, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                touched.Add(name); File.Copy(UpdateFiles.Under(payload, name), target, true); afterReplace?.Invoke(++count);
            }
            foreach (string name in previous.Keys.Except(next.Keys, StringComparer.OrdinalIgnoreCase))
            { touched.Add(name); File.Delete(UpdateFiles.Under(plan.Target, name)); }
            foreach (var entry in next) await UpdateFiles.VerifyHashAsync(UpdateFiles.Under(plan.Target, entry.Key), entry.Value, default);
            if (UpdateFiles.FileVersion(Path.Combine(plan.Target, "SaruCast.dll")).ToString() != plan.NewVersion) throw new IOException("更新後のアプリを確認できません。");
        }
        catch (Exception e)
        {
            bool restored = true;
            foreach (string name in touched.Distinct(StringComparer.OrdinalIgnoreCase).Reverse())
                try { string target = UpdateFiles.Under(plan.Target, name); if (existed.Contains(name)) File.Copy(UpdateFiles.Under(backup, name), target, true); else if (File.Exists(target)) File.Delete(target); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException) { restored = false; Log.Error("Update rollback failed", rollback); }
            throw new PortableUpdateException(restored, e);
        }
    }
    internal static async Task ApplyInstallerAsync(UpdatePlan plan)
    {
        if (UpdateFiles.FileVersion(plan.Asset).ToString() != plan.NewVersion) throw new InvalidDataException("セットアップのバージョンが一致しません。");
        // NSIS requires /D to be the last unquoted remainder. No shell is involved;
        // Windows paths cannot contain a quote, and &/% are ordinary characters here.
        var info = new ProcessStartInfo(plan.Asset) { UseShellExecute = false, CreateNoWindow = true, Arguments = "/S /D=" + Path.GetFullPath(plan.Target) };
        using var setup = Process.Start(info) ?? throw new IOException("セットアップを起動できません。");
        await setup.WaitForExitAsync();
        if (setup.ExitCode != 0 || UpdateFiles.FileVersion(Path.Combine(plan.Target, "SaruCast.dll")).ToString() != plan.NewVersion) throw new IOException("セットアップによる更新を完了できませんでした。");
    }
    private static async Task WriteResultAsync(UpdateResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UpdateManager.ResultPath)!); UpdateFiles.NoLinks(UpdateManager.ResultPath);
        string temporary = UpdateManager.ResultPath + "." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(result)); File.Move(temporary, UpdateManager.ResultPath, true);
    }
    private static void Restart(string target)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(target, "SaruCast.exe")) { UseShellExecute = false, WorkingDirectory = target });
        if (process == null) throw new IOException("更新後のアプリを起動できません。");
    }
    private sealed class PortableUpdateException(bool rolledBack, Exception inner) : IOException("更新ファイルの適用に失敗しました。", inner)
    { public bool RolledBack { get; } = rolledBack; }
}
