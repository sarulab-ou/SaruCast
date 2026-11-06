using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SaruCast;
public static class Diagnostics
{
    // Explicit opt-in CLI, never automatically captures or starts sharing at GUI startup.
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2 || args[0] != "--diagnose") return 2;
        string output = Path.GetFullPath(args[1]);
        var results = new Dictionary<string, object?> { ["utc"] = DateTimeOffset.UtcNow, ["os"] = Environment.OSVersion.ToString(), ["runtime"] = Environment.Version.ToString(), ["televisionPlaybackVerified"] = false };
        int exit = 0;
        try
        {
            var displays = Displays.List(); results["displays"] = displays;
            if (args.Contains("--discover")) results["devices"] = await Mdns.DiscoverAsync(CancellationToken.None);
            int probeIndex = Array.IndexOf(args, "--probe-ip");
            if (probeIndex >= 0)
            {
                using var probeTimeout = new CancellationTokenSource(10000);
                await using var probe = new CastClient(); await probe.ConnectAsync(new("診断用接続先", args[probeIndex + 1]), probeTimeout.Token);
                results["receiverVolume"] = probe.ReceiverVolume; results["receiverMuted"] = probe.ReceiverMuted;
            }
            int pos = Array.IndexOf(args, "--capture-seconds");
            if (pos >= 0)
            {
                int duration = Math.Clamp(int.Parse(args[pos + 1]), 4, 1800);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(duration + 110));
                var options = new Settings { Audio = args.Contains("--audio"), Hardware = !args.Contains("--software"), Gdi = args.Contains("--gdi"), Height = args.Contains("--720p") ? 720 : 1080 };
                using var tone = args.Contains("--tone") ? Tone() : null;
                tone?.PlayLooping();
                int castIndex = Array.IndexOf(args, "--cast-ip");
                await using var cast = castIndex >= 0 ? new CastClient() : null;
                if (cast != null) await cast.ConnectAsync(new("診断用接続先", args[castIndex + 1]), timeout.Token);
                if (cast != null) { results["receiverVolume"] = cast.ReceiverVolume; results["receiverMuted"] = cast.ReceiverMuted; }
                await using var audio = options.Audio ? new AudioLoopback() : null;
                audio?.Start(); await using var encoder = new Encoder();
                var time = Stopwatch.StartNew();
                await encoder.StartAsync(displays.First(), options, audio?.PipeName, timeout.Token, args.Contains("--pattern"));
                results["encoderReadySeconds"] = time.Elapsed.TotalSeconds;
                await using var server = cast == null ? null : new HlsServer(cast.LocalAddress, System.Net.IPAddress.Parse(args[castIndex + 1]), encoder.DirectoryPath);
                if (cast != null)
                {
                    try { await cast.LaunchAndLoadAsync(server!.Url, timeout.Token); }
                    finally { results["receiverSegmentRequests"] = server!.SegmentRequests; results["receiverPlaylistRequests"] = server.PlaylistRequests; }
                    results["receiverPlayingAfterSeconds"] = time.Elapsed.TotalSeconds;
                }
                long before = Process.GetCurrentProcess().WorkingSet64;
                var encoderBefore = encoder.Metrics(); var interval = Stopwatch.StartNew();
                await Task.Delay(TimeSpan.FromSeconds(duration), timeout.Token);
                if (encoder.Failure.IsCompleted) throw await encoder.Failure;
                if (audio?.Failure.IsCompleted == true) throw await audio.Failure;
                if (cast?.Failure.IsCompleted == true) throw await cast.Failure;
                if (cast != null) { results["receiverPlayerState"] = cast.PlayerState; results["receiverSegmentRequests"] = server!.SegmentRequests; }
                var encoderAfter = encoder.Metrics();
                results["encoderWorkingSetBefore"] = encoderBefore.WorkingSet;
                results["encoderWorkingSetAfter"] = encoderAfter.WorkingSet;
                results["encoderCpuPercentTotalMachine"] = 100 * (encoderAfter.CpuSeconds - encoderBefore.CpuSeconds) / interval.Elapsed.TotalSeconds / Environment.ProcessorCount;
                results["logicalProcessors"] = Environment.ProcessorCount;
                results["audioPeakPcm16"] = audio?.PeakPcm16;
                if (args.Contains("--tone") && audio?.PeakPcm16 < 100) throw new IOException("WASAPI test tone not captured");
                var index = File.ReadAllLines(Path.Combine(encoder.DirectoryPath, "index.m3u8"));
                string segment = index.First(l => l.EndsWith(".ts"));
                string probe = Path.Combine(AppContext.BaseDirectory, "tools", "ffprobe.exe");
                var info = new ProcessStartInfo(probe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", Path.Combine(encoder.DirectoryPath, segment) }) info.ArgumentList.Add(arg);
                using var p = Process.Start(info)!; string json = await p.StandardOutput.ReadToEndAsync(timeout.Token); string errors = await p.StandardError.ReadToEndAsync(timeout.Token); await p.WaitForExitAsync(timeout.Token);
                if (p.ExitCode != 0) throw new IOException("ffprobe failed: " + errors);
                using var doc = JsonDocument.Parse(json);
                var streams = doc.RootElement.GetProperty("streams");
                if (!streams.EnumerateArray().Any(s => s.GetProperty("codec_name").GetString() == "h264")) throw new IOException("H.264 stream missing");
                if (options.Audio && !streams.EnumerateArray().Any(s => s.GetProperty("codec_name").GetString() == "aac")) throw new IOException("AAC stream missing");
                // Exclude local username/session directory from published diagnostic data.
                results["streams"] = streams.Clone();
                results["playlist"] = index;
                results["segmentBytes"] = new FileInfo(Path.Combine(encoder.DirectoryPath, segment)).Length;
                results["segmentCountOnDisk"] = Directory.GetFiles(encoder.DirectoryPath, "*.ts").Length;
                results["workingSetBefore"] = before; results["workingSetAfter"] = Process.GetCurrentProcess().WorkingSet64;
                results["captureSecondsAfterReady"] = duration; results["settings"] = options;
                var decodeInfo = new ProcessStartInfo(Paths.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (string arg in new[] { "-hide_banner", "-i", Path.Combine(encoder.DirectoryPath, segment), "-an", "-vf", "signalstats,metadata=print:key=lavfi.signalstats.YAVG", "-frames:v", "3", "-f", "null", "-" }) decodeInfo.ArgumentList.Add(arg);
                using var decode = Process.Start(decodeInfo)!;
                Task<string> decodeErrors = decode.StandardError.ReadToEndAsync(timeout.Token);
                await decode.StandardOutput.ReadToEndAsync(timeout.Token); await decode.WaitForExitAsync(timeout.Token);
                string decoded = await decodeErrors;
                var luma = Regex.Matches(decoded, @"lavfi\.signalstats\.YAVG=([\d.]+)").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (decode.ExitCode != 0 || luma.Length == 0) throw new IOException("Encoded video decode check failed");
                results["decodedLumaMeans"] = luma;
                results["decodedVideoNotAllBlack"] = luma.Any(v => v > 18);
                tone?.Stop();
            }
            results["success"] = true;
        }
        catch (Exception e) { results["success"] = false; results["error"] = e.Message; results["exception"] = e.GetType().Name; exit = 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return exit;
    }
    private static System.Media.SoundPlayer Tone()
    {
        var data = new MemoryStream(); using (var w = new BinaryWriter(data, System.Text.Encoding.ASCII, true))
        {
            int samples = 48000;
            w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(48000); w.Write(96000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(samples * 2);
            for (int i = 0; i < samples; i++) w.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 3000));
        }
        data.Position = 0; return new System.Media.SoundPlayer(data);
    }
}
