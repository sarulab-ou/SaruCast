using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SaruCast;

// Opt-in local test: generated pattern only; never connects to a television.
static class EncoderSoak
{
    public static async Task RunAsync(string[] args)
    {
        string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        int seconds = args.Length > 2 ? int.Parse(args[2]) : 660;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 30));
        await using var encoder = new StreamingVideoEncoder();
        List<object> activity = []; encoder.Frames.Cleared += message => { lock (activity) activity.Add(new { elapsed = Environment.TickCount64, message }); };
        encoder.Start(new("local pattern", 0, 0, 0, 0, 1920, 1080), new Settings { FrameRate = 30, Height = 1080, Audio = false }, true);
        using var video = File.Create(Path.Combine(output, "recovered.h264"));
        var clock = Stopwatch.StartNew();
        long count = 0, maxAge = 0, lastSequence = -1; int stalls = 0; double maxLag = 0;
        List<object> samples = [];
        async Task Write(bool success)
        {
            object[] messages; lock (activity) messages = activity.ToArray();
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            { success, seconds = clock.Elapsed.TotalSeconds, count, dropped = encoder.Frames.Dropped, stalls, maxDequeuedAgeMs = maxAge,
                maximumEncoderTimelineLagMs = maxLag, samples, activity = messages, physicalTelevisionTest = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
        await foreach (var frame in encoder.Frames.ReadAllAsync(timeout.Token))
        {
            if (frame.Sequence != lastSequence + 1 && !frame.Unit.IsKeyFrame) throw new Exception("Broken P-frame chain after buffer clear");
            lastSequence = frame.Sequence;
            maxAge = Math.Max(maxAge, Environment.TickCount64 - frame.ReceivedAt);
            maxLag = Math.Max(maxLag, encoder.LagMilliseconds);
            count++;
            // Store a small sample across the first two induced clears for real decoding.
            if (clock.Elapsed.TotalSeconds < 45) await video.WriteAsync(frame.Unit.Data, timeout.Token);
            if (clock.Elapsed.TotalSeconds >= seconds) break;
            if (clock.Elapsed.TotalSeconds >= 15 + stalls * 30)
            {
                stalls++;
                // Simulate a sender unable to consume for 1.5 s; producer must keep reading.
                await Task.Delay(1500, timeout.Token);
                samples.Add(new { seconds = clock.Elapsed.TotalSeconds, frames = count, dropped = encoder.Frames.Dropped,
                    lagMs = encoder.LagMilliseconds, encoderWorkingSet = encoder.Metrics().WorkingSet });
                await Write(false);
            }
        }
        await video.FlushAsync();
        if (stalls < 1 || encoder.Frames.Dropped < 30 || maxLag > 1000) throw new Exception("Local encoder did not stay current under sender stalls");
        await Write(true);
        Console.WriteLine($"Local 30fps encoder soak passed: {clock.Elapsed.TotalSeconds:F1}s, {count} frames, {encoder.Frames.Dropped} dropped, {stalls} induced stalls, max lag {maxLag:F0}ms");
    }
}
