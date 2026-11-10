using SaruCast;
using System.IO;
using System.Diagnostics;
using RikRealization.ClassicCast.Protocol.Media;

static class LatencyChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using (var pacer = new PacketPacer())
        {
            var elapsed = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) await pacer.WaitAsync(default);
            Console.WriteLine($"High-resolution packet pacing: 100 waits = {elapsed.Elapsed.TotalMilliseconds:F1}ms");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await pacer.WaitAsync(cancelled.Token); throw new Exception("Pacer ignored cancellation"); }
            catch (OperationCanceledException) { }
            check(true, "Windows high-resolution packet pacer completes and respects cancellation");
        }
        var timeline = new EncoderTimeline();
        var monitor = new StreamingLatencyMonitor();
        // Ten minutes at 30fps, including all 8-bit frame-ID wraps, with prompt ACKs.
        for (long id = 0; id < 18000; id++)
        {
            long now = 10000 + id * 1000 / 30;
            timeline.Update(id * 1000000 / 30, now);
            monitor.Sent(id, now); monitor.Feedback((byte)id, 200, now);
            if (monitor.Check(now, now - 10000, timeline.LagMilliseconds(now)) != null) throw new Exception("Healthy 30fps reset");
        }
        check(true, "Ten simulated minutes at 30fps do not accumulate latency or reset on checkpoint wraps");
        timeline.Update(600000000, 610000);
        check(monitor.Check(611100, 601100, timeline.LagMilliseconds(611100)) == null
            && monitor.Check(611700, 601700, timeline.LagMilliseconds(611700))?.Contains("エンコーダー") == true,
            "Encoder timeline drift requests reset after sustained one-second backlog");
        var waiting = new StreamingLatencyMonitor(); waiting.Sent(0, 10000);
        waiting.Feedback(255, 200, 10000);
        check(waiting.Check(11100, 6000, 0) == null && waiting.Check(11700, 6700, 0)?.Contains("受信確認") == true,
            "Initial checkpoint 255 cannot falsely acknowledge frame zero; stalled receipt resets");
        var tv = new StreamingLatencyMonitor();
        tv.Feedback(null, 2200, 20000);
        check(tv.Check(20000, 6000, 0) == null && tv.Check(20600, 6600, 0)?.Contains("テレビ") == true,
            "Receiver-reported multi-second playout buffer requests reset");
        tv.Feedback(null, 200, 20700);
        check(tv.Check(20700, 6700, 0) == null, "Transient latency clears without restarting");

        var queue = new LiveVideoBuffer(); int notices = 0; queue.Cleared += _ => notices++;
        H264AccessUnit Unit(bool key) => new(new byte[] { key ? (byte)0x65 : (byte)0x41 }, key);
        for (int i = 0; i < 300; i++) queue.Add(Unit(i % 30 == 0), Environment.TickCount64);
        check(queue.Dropped == 296 && notices == 1, "Slow sender never blocks encoder and queued video stays bounded to four frames");
        using var timeout = new CancellationTokenSource(3000);
        await using var reader = queue.ReadAllAsync(timeout.Token).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        await Task.Yield(); queue.Add(Unit(true), Environment.TickCount64);
        check(await next && reader.Current.Sequence == 300 && reader.Current.Unit.IsKeyFrame && queue.Dropped == 300,
            "Overflow discards dependent P frames and resumes at next IDR");
        queue.Add(Unit(false), Environment.TickCount64 - 1000);
        queue.Add(Unit(false), Environment.TickCount64);
        queue.Add(Unit(true), Environment.TickCount64);
        check(await reader.MoveNextAsync() && reader.Current.Sequence == 303 && reader.Current.Unit.IsKeyFrame,
            "Old queued frame is discarded with its dependent frames even below capacity");
        queue.Complete();

        var connections = new List<FakeConnection>();
        using var created = new SemaphoreSlim(0);
        await using (var session = new StreamingSession(() =>
        {
            var connection = new FakeConnection(); lock (connections) connections.Add(connection); created.Release(); return connection;
        }))
        {
            List<string> logs = []; session.Activity += message => { lock (logs) logs.Add(message); };
            var settings = new Settings { FrameRate = 30, Audio = true };
            await session.StartAsync(new("mock", "127.0.0.1"), new("mock", 0, 0, 0, 0, 1920, 1080), settings, timeout.Token);
            await created.WaitAsync(timeout.Token);
            var first = connections[0]; first.Reset.TrySetResult("テレビの再生バッファ 2秒");
            await created.WaitAsync(timeout.Token);
            var second = connections[1]; await second.Started.Task.WaitAsync(timeout.Token);
            check(first.Disposed && second.Settings?.FrameRate == 30 && second.Settings.Audio && session.BufferResetCount == 1,
                "Buffer reset disposes old media session before recreating video and audio with same settings");
            check(!session.Failure.IsCompleted && session.FramesSent == 20 && logs.Any(s => s.Contains("クリア")),
                "Automatic recovery logs clear and preserves cumulative diagnostics");
            await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
            second.Reset.TrySetResult("late feedback"); await Task.Yield();
            check(second.Disposed && connections.Count == 2, "Stop during recovery cannot restart casting");
        }
        var blocked = new FakeConnection { BlockStart = true };
        await using (var session = new StreamingSession(() => blocked))
        {
            var startup = session.StartAsync(new("mock", "127.0.0.1"), new("mock", 0, 0, 0, 0, 1, 1), new(), default);
            await blocked.Started.Task.WaitAsync(timeout.Token);
            await session.DisposeAsync();
            try { await startup; throw new Exception("Startup was not cancelled"); } catch (OperationCanceledException) { }
            check(blocked.Disposed, "Closing during automatic session startup cancels and cleans resources");
        }
        var firstReset = new FakeConnection(); var reconnecting = new FakeConnection { BlockStart = true }; int generation = 0;
        await using (var session = new StreamingSession(() => generation++ == 0 ? firstReset : reconnecting))
        {
            await session.StartAsync(new("mock", "127.0.0.1"), new("mock", 0, 0, 0, 0, 1, 1), new(), default);
            firstReset.Reset.TrySetResult("test reset");
            await reconnecting.Started.Task.WaitAsync(timeout.Token);
            await session.DisposeAsync();
            check(reconnecting.Disposed && generation == 2 && !session.Failure.IsCompleted,
                "User stop during buffer-clear reconnection cancels pending start without a third session");
        }
        var failed = new FakeConnection(); int attempts = 0;
        await using (var session = new StreamingSession(() => { attempts++; return failed; }))
        {
            await session.StartAsync(new("mock", "127.0.0.1"), new("mock", 0, 0, 0, 0, 1, 1), new(), default);
            failed.Error.TrySetResult(new IOException("network failed"));
            check((await session.Failure.WaitAsync(timeout.Token)).Message == "network failed" && attempts == 1 && failed.Disposed,
                "Ordinary connection failure stops instead of automatically resuming sharing");
        }
        var excessive = new List<FakeConnection>();
        using var another = new SemaphoreSlim(0);
        await using (var session = new StreamingSession(() => { var c = new FakeConnection(); lock (excessive) excessive.Add(c); another.Release(); return c; }))
        {
            await session.StartAsync(new("mock", "127.0.0.1"), new("mock", 0, 0, 0, 0, 1, 1), new(), default);
            for (int i = 0; i < 4; i++)
            {
                await another.WaitAsync(timeout.Token);
                excessive[i].Reset.TrySetResult("persistent congestion");
            }
            check((await session.Failure.WaitAsync(timeout.Token)).Message.Contains("15fps") && excessive.Count == 4 && session.BufferResetCount == 3,
                "Repeated buffering stops after three automatic clears per minute instead of a reconnect loop");
        }
        var badCleanup = new FakeConnection { FailCleanup = true };
        var cleanupSession = new StreamingSession(() => badCleanup);
        await cleanupSession.StartAsync(new("mock", "127.0.0.1"), new("mock", 0, 0, 0, 0, 1, 1), new(), default);
        try { await cleanupSession.DisposeAsync(); throw new Exception("Cleanup failure was hidden"); }
        catch (IOException) { check(cleanupSession.Failure.IsCompleted, "Cleanup failure remains observable instead of reporting successful diagnostics"); }
    }
    private sealed class FakeConnection : IStreamingConnection
    {
        public TaskCompletionSource<string> Reset = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception> Error = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Exception> Failure => Error.Task;
        public Task<string> ResetRequested => Reset.Task;
        public event Action<string>? Status { add { } remove { } }
        public event Action<string>? Activity { add { } remove { } }
        public bool Disposed, BlockStart, FailCleanup;
        public Settings? Settings;
        public async Task StartAsync(CastDevice device, DisplayInfo display, Settings settings, CancellationToken ct, bool testPattern = false)
        { Settings = settings; Started.TrySetResult(); if (BlockStart) await Task.Delay(Timeout.Infinite, ct); }
        public long FramesSent => 10;
        public long FeedbackCount => 10;
        public long CheckpointChanges => 10;
        public long RetransmittedPackets => 0;
        public long AudioFramesSent => 10;
        public long AudioFeedbackCount => 10;
        public long AudioSkippedFrames => 0;
        public long DroppedVideoFrames => 0;
        public int AudioPeakPcm16 => 0;
        public int Width => 1920;
        public int Height => 1080;
        public (long WorkingSet, double CpuSeconds) EncoderMetrics() => (10, 1);
        public ValueTask DisposeAsync() { Disposed = true; if (FailCleanup) throw new IOException("cleanup failed"); return ValueTask.CompletedTask; }
    }
}
