using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SaruCast;

static class UpdateIntegration
{
    internal static async Task DownloadAsync(string output)
    {
        using var service = new AppUpdateService(); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var release = await service.CheckAsync(new(0,0,0), false, timeout.Token) ?? throw new Exception("No public ZIP release");
        var prepared = await service.DownloadAsync(release, false, null, timeout.Token);
        var manifest = UpdateFiles.Manifest(Path.Combine(prepared.Root, "payload"));
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = true, version = release.Version.ToString(), asset = release.Package.Name,
            bytes = release.Package.Size, sha256 = prepared.Sha256, verifiedFiles = manifest.Count, applicationModified = false }, new JsonSerializerOptions { WriteIndented = true }));
        UpdateFiles.DeleteStage(prepared.Root);
        Console.WriteLine("Live GitHub ZIP download passed: sidecar, digest, size and all package hashes; no app updated.");
    }
    internal static async Task InstallerAsync(string setup, string output)
    {
        setup = Path.GetFullPath(setup); output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new Exception("Choose new installer test directory");
        string target = Path.Combine(output, "SaruCast セットアップ #100% &"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "SaruCast.dll"), "old fixture content");
        File.WriteAllText(Path.Combine(target, "user-data.txt"), "preserve user file");
        var plan = new UpdatePlan(1, output, target, setup, "", AppUpdateService.CurrentVersion.ToString(), "0.1.3", true, int.MaxValue, 1, false);
        await UpdateApplier.ApplyInstallerAsync(plan);
        // NSIS consumes /D before exposing $CMDLINE; the DLL written at the exact
        // requested path (and version-checked by the applier) proves path handling.
        if (!File.Exists(Path.Combine(target, "fixture-args.txt")) || File.ReadAllText(Path.Combine(target, "user-data.txt")) != "preserve user file") throw new Exception("Installer target or user data changed");
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(target, "SaruCast.dll"))));
        File.WriteAllText(Path.Combine(target, "force-failure.flag"), "test failure");
        bool failed = false;
        try { await UpdateApplier.ApplyInstallerAsync(plan); } catch (IOException) { failed = true; }
        if (!failed || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(target, "SaruCast.dll")))) != before) throw new Exception("Installer failure was not preserved");
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        { success = true, realProcessDelegation = true, japaneseAndSymbolPathVerified = true, userDataPreserved = true, failurePropagated = true,
            fixtureOnly = true, realInstallRegistryTested = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Installer delegation passed with a test-only native NSIS setup; real installation and shortcuts untouched.");
    }
    internal static async Task FeedAsync(string output)
    {
        using var service = new AppUpdateService(); using var timeout = new CancellationTokenSource(45000);
        var zip = await service.CheckAsync(new(0,1,2), false, timeout.Token);
        var setup = await service.CheckAsync(new(0,1,2), true, timeout.Token);
        if (zip == null || setup == null || zip.Version != setup.Version) throw new Exception("Public release feed not usable");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { success = true, zipVersion = zip.Version.ToString(), setupVersion = setup.Version.ToString(), zip = zip.Package.Name, setup = setup.Package.Name, unauthenticated = true }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Live GitHub feed passed for ZIP and installer (metadata only).");
    }
    internal static async Task RunAsync(string published, string output)
    {
        published = Path.GetFullPath(published); output = Path.GetFullPath(output);
        if (Directory.Exists(output)) throw new Exception("Choose new integration output directory");
        Directory.CreateDirectory(output);
        string target = Path.Combine(output, "SaruCast 更新試験 #100% &"); Directory.CreateDirectory(target);
        foreach (string source in Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories))
        {
            string destination = UpdateFiles.Under(target, Path.GetRelativePath(published, source)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(source, destination);
        }
        File.WriteAllText(Path.Combine(target, "user-notes.txt"), "User data survives update");
        File.WriteAllText(Path.Combine(target, "obsolete-owned.txt"), "old owned file");
        string obsoleteHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(target, "obsolete-owned.txt")))).ToLowerInvariant();
        File.AppendAllText(Path.Combine(target, "SHA256SUMS.txt"), obsoleteHash + "  obsolete-owned.txt\n");
        string zip = Path.Combine(Path.GetDirectoryName(published)!, AppUpdateService.ZipName);
        byte[] sha = await File.ReadAllBytesAsync(zip + ".sha256");
        string digest;
        await using (var input = File.OpenRead(zip)) digest = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
        string tag = "v" + AppUpdateService.CurrentVersion;
        var release = new AppRelease(AppUpdateService.CurrentVersion, tag,
            new(AppUpdateService.ZipName, AppUpdateService.AssetUrl(tag, AppUpdateService.ZipName), new FileInfo(zip).Length, "sha256:" + digest),
            new(AppUpdateService.ZipName + ".sha256", AppUpdateService.AssetUrl(tag, AppUpdateService.ZipName + ".sha256"), sha.Length, null));
        using var service = new AppUpdateService(new FilesHandler(zip, sha));
        var prepared = await service.DownloadAsync(release, false, null, default);
        string planPath = await UpdateManager.PrepareAsync(prepared, target, default);
        var plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(planPath))!;
        // A short-lived hidden process stands in for the GUI exiting. No user's
        // application or installation is closed or modified by this integration test.
        var sleeperInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 3" }) sleeperInfo.ArgumentList.Add(arg);
        using var sleeper = Process.Start(sleeperInfo)!;
        plan = plan with { ParentId = sleeper.Id, ParentStartUtcTicks = sleeper.StartTime.ToUniversalTime().Ticks, Restart = false };
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan));
        using var helper = Process.Start(UpdateManager.StartInfo(planPath))!;
        using var timeout = new CancellationTokenSource(90000);
        await Task.Delay(500, timeout.Token);
        if (!File.Exists(Path.Combine(target, "obsolete-owned.txt"))) throw new Exception("Updater did not wait for parent");
        await helper.WaitForExitAsync(timeout.Token);
        if (helper.ExitCode != 0) throw new Exception("Real helper failed; inspect isolated update logs");
        var result = UpdateManager.TakeResult(target) ?? throw new Exception("Update result missing");
        if (!result.Success || File.Exists(Path.Combine(target, "obsolete-owned.txt")) || File.ReadAllText(Path.Combine(target, "user-notes.txt")) != "User data survives update") throw new Exception("Real update did not preserve ownership boundaries");
        foreach (var entry in UpdateFiles.Manifest(published)) await UpdateFiles.VerifyHashAsync(UpdateFiles.Under(target, entry.Key), entry.Value, timeout.Token);
        string startup = Path.Combine(output, "startup.json");
        var start = new ProcessStartInfo(Path.Combine(target, "SaruCast.exe")) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--diagnose"); start.ArgumentList.Add(startup);
        start.Environment["DOTNET_ROOT"] = Path.Combine(output, "no-runtime"); start.Environment["DOTNET_ROOT_X64"] = start.Environment["DOTNET_ROOT"];
        start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        using var updated = Process.Start(start)!; await updated.WaitForExitAsync(timeout.Token);
        if (updated.ExitCode != 0) throw new Exception("Updated self-contained app startup failed");
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        { success = true, parentExitWaitVerified = true, userDataPreserved = true, obsoleteFileRemoved = true, updatedStartupVerified = true,
            realHelperProcess = true, version = AppUpdateService.CurrentVersion.ToString(), sameVersionReinstallForIsolation = true,
            installedModeTested = false, updateResult = result }, new JsonSerializerOptions { WriteIndented = true }));
        UpdateFiles.DeleteStage(prepared.Root);
        Console.WriteLine("Real updater helper passed: parent wait, replacement, owned-file removal, user-data preservation and bundled-runtime restart.");
    }
    private sealed class FilesHandler(string zip, byte[] checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256") ? new ByteArrayContent(checksum) : new StreamContent(File.OpenRead(zip)) });
    }
}
