using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SaruCast;

public static class StreamingDiagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2) return 2;
        string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var result = new Dictionary<string, object?> { ["utc"] = DateTimeOffset.UtcNow, ["transport"] = "Cast Streaming", ["televisionObservedPlaybackVerified"] = false };
        List<object> activity = [];
        Task Write()
        {
            lock (activity) result["activity"] = activity.ToArray();
            return File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        int exit = 0;
        try
        {
            int seconds = Math.Clamp(int.Parse(Arg("--seconds") ?? "20"), 5, 1800);
            int fps = int.Parse(Arg("--fps") ?? "15");
            if (fps is not (5 or 15 or 30)) throw new ArgumentException("--fps must be 5, 15 or 30");
            var settings = new Settings { Audio = args.Contains("--audio"), Hardware = !args.Contains("--software"), Gdi = args.Contains("--gdi"), FrameRate = fps, Height = args.Contains("--1080p") ? 1080 : 720 };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 60));
            await using var session = new StreamingSession();
            session.Activity += message => { lock (activity) activity.Add(new { utc = DateTimeOffset.UtcNow, message }); };
            var clock = Stopwatch.StartNew();
            await session.StartAsync(new("診断用接続先", Arg("--ip") ?? throw new ArgumentException("--ip is required")), Displays.List().First(), settings, timeout.Token, args.Contains("--pattern"));
            result["firstMediaAcknowledgedAfterSeconds"] = clock.Elapsed.TotalSeconds;
            result["width"] = session.Width; result["height"] = session.Height; result["fps"] = settings.FrameRate;
            result["hardware"] = settings.Hardware; result["audioEnabled"] = settings.Audio;
            result["videoTargetKbps"] = Encoder.VideoBitrateKbps(settings);
            using var self = Process.GetCurrentProcess();
            var before = session.EncoderMetrics(); double cpuBefore = self.TotalProcessorTime.TotalSeconds;
            List<object> samples = []; result["samples"] = samples;
            var interval = Stopwatch.StartNew();
            while (interval.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, seconds - interval.Elapsed.TotalSeconds)), timeout.Token);
                if (session.Failure.IsCompleted) throw await session.Failure;
                self.Refresh(); var metrics = session.EncoderMetrics();
                result["framesSent"] = session.FramesSent; result["receiverFeedback"] = session.FeedbackCount; result["checkpointChanges"] = session.CheckpointChanges;
                result["retransmittedPackets"] = session.RetransmittedPackets;
                result["audioFramesSent"] = session.AudioFramesSent; result["audioReceiverFeedback"] = session.AudioFeedbackCount;
                result["audioSkippedFrames"] = session.AudioSkippedFrames; result["audioPeakPcm16"] = session.AudioPeakPcm16;
                result["bufferResets"] = session.BufferResetCount; result["droppedVideoFrames"] = session.DroppedVideoFrames;
                result["elapsedSeconds"] = interval.Elapsed.TotalSeconds;
                samples.Add(new { seconds = interval.Elapsed.TotalSeconds, encoderWorkingSet = metrics.WorkingSet, appWorkingSet = self.WorkingSet64, framesSent = session.FramesSent, audioFramesSent = session.AudioFramesSent });
                result["encoderCpuPercentTotalMachine"] = 100 * (metrics.CpuSeconds - before.CpuSeconds) / interval.Elapsed.TotalSeconds / Environment.ProcessorCount;
                result["appCpuPercentTotalMachine"] = 100 * (self.TotalProcessorTime.TotalSeconds - cpuBefore) / interval.Elapsed.TotalSeconds / Environment.ProcessorCount;
                result["logicalProcessors"] = Environment.ProcessorCount;
                await Write();
            }
            result["success"] = true;
        }
        catch (Exception e) { result["success"] = false; result["error"] = e.Message; Log.Error("Native diagnostic failed", e); exit = 1; }
        await Write(); return exit;
    }
}
