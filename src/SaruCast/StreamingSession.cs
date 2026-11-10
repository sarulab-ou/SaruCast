namespace SaruCast;

internal interface IStreamingConnection : IAsyncDisposable
{
    Task<Exception> Failure { get; }
    Task<string> ResetRequested { get; }
    event Action<string>? Status;
    event Action<string>? Activity;
    Task StartAsync(CastDevice device, DisplayInfo display, Settings settings, CancellationToken ct, bool testPattern = false);
    long FramesSent { get; }
    long FeedbackCount { get; }
    long CheckpointChanges { get; }
    long RetransmittedPackets { get; }
    long AudioFramesSent { get; }
    long AudioFeedbackCount { get; }
    long AudioSkippedFrames { get; }
    long DroppedVideoFrames { get; }
    int AudioPeakPcm16 { get; }
    int Width { get; }
    int Height { get; }
    (long WorkingSet, double CpuSeconds) EncoderMetrics();
}

// A buffer reset replaces the encoder AND receiver session, including audio clocks,
// UDP queues, retransmission caches and encryption keys. Ordinary errors still stop.
public sealed class StreamingSession : IAsyncDisposable
{
    private readonly Func<IStreamingConnection> create;
    private readonly CancellationTokenSource life = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private IStreamingConnection? current;
    private Task? run, dispose;
    private Exception? cleanupFailure;
    private long frames, feedback, checkpoints, retries, audioFrames, audioFeedback, audioSkipped, dropped;
    private double encoderCpu;
    private int peak, resets, width, height;
    public StreamingSession() : this(() => new StreamingConnection()) { }
    internal StreamingSession(Func<IStreamingConnection> create) => this.create = create;
    public event Action<string>? Status;
    public event Action<string>? Activity;
    public Task<Exception> Failure => failure.Task;
    public long FramesSent { get { lock (gate) return frames + (current?.FramesSent ?? 0); } }
    public long FeedbackCount { get { lock (gate) return feedback + (current?.FeedbackCount ?? 0); } }
    public long CheckpointChanges { get { lock (gate) return checkpoints + (current?.CheckpointChanges ?? 0); } }
    public long RetransmittedPackets { get { lock (gate) return retries + (current?.RetransmittedPackets ?? 0); } }
    public long AudioFramesSent { get { lock (gate) return audioFrames + (current?.AudioFramesSent ?? 0); } }
    public long AudioFeedbackCount { get { lock (gate) return audioFeedback + (current?.AudioFeedbackCount ?? 0); } }
    public long AudioSkippedFrames { get { lock (gate) return audioSkipped + (current?.AudioSkippedFrames ?? 0); } }
    public long DroppedVideoFrames { get { lock (gate) return dropped + (current?.DroppedVideoFrames ?? 0); } }
    public int AudioPeakPcm16 { get { lock (gate) return Math.Max(peak, current?.AudioPeakPcm16 ?? 0); } }
    public int Width { get { lock (gate) return current?.Width ?? width; } }
    public int Height { get { lock (gate) return current?.Height ?? height; } }
    public int BufferResetCount => Volatile.Read(ref resets);
    public (long WorkingSet, double CpuSeconds) EncoderMetrics()
    { lock (gate) { var active = current?.EncoderMetrics() ?? (0L, 0.0); return (active.Item1, encoderCpu + active.Item2); } }
    private void WriteActivity(string message) { Log.Write(message); Activity?.Invoke(message); }
    public async Task StartAsync(CastDevice device, DisplayInfo display, Settings settings, CancellationToken ct, bool testPattern = false)
    {
        lock (gate)
        {
            if (run != null || dispose != null) throw new InvalidOperationException("この配信は開始済み、または終了済みです。");
            run = Task.Run(() => RunAsync(device, display, settings, ct, testPattern));
        }
        await ready.Task.WaitAsync(ct);
    }
    private async Task RunAsync(CastDevice device, DisplayInfo display, Settings settings, CancellationToken ct, bool testPattern)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, life.Token);
        var token = linked.Token;
        Queue<long> recentResets = new();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var connection = create();
                lock (gate) current = connection;
                connection.Status += message => { if (!token.IsCancellationRequested) Status?.Invoke(message); };
                connection.Activity += WriteActivity;
                string reason;
                try
                {
                    await connection.StartAsync(device, display, settings, token, testPattern);
                    lock (gate) { width = connection.Width; height = connection.Height; }
                    ready.TrySetResult();
                    if (BufferResetCount > 0) WriteActivity("バッファクリア後の配信を再開しました（テレビの映像受信を確認）。");
                    await Task.WhenAny(connection.Failure, connection.ResetRequested).WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                    if (connection.Failure.IsCompleted) throw await connection.Failure;
                    reason = await connection.ResetRequested;
                }
                finally
                {
                    // Sample CPU before killing the process; keep diagnostic totals monotonic.
                    var metrics = connection.EncoderMetrics();
                    try { await connection.DisposeAsync(); }
                    catch (Exception e) { cleanupFailure = e; throw; }
                    finally
                    {
                        lock (gate)
                        {
                            frames += connection.FramesSent; feedback += connection.FeedbackCount; checkpoints += connection.CheckpointChanges;
                            retries += connection.RetransmittedPackets; audioFrames += connection.AudioFramesSent; audioFeedback += connection.AudioFeedbackCount;
                            audioSkipped += connection.AudioSkippedFrames; dropped += connection.DroppedVideoFrames; peak = Math.Max(peak, connection.AudioPeakPcm16);
                            encoderCpu += metrics.CpuSeconds; current = null;
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                long now = Environment.TickCount64;
                while (recentResets.TryPeek(out long oldest) && now - oldest > 60000) recentResets.Dequeue();
                if (recentResets.Count >= 3) throw new System.IO.IOException("遅延の増加が繰り返されたため停止しました。15fpsまたは720pへ下げて再開始してください。");
                recentResets.Enqueue(now); Interlocked.Increment(ref resets);
                WriteActivity($"バッファをクリアしました（{reason}）。映像・音声の配信を作り直します。累計{BufferResetCount}回。");
                Status?.Invoke("バッファクリア中… テレビへ再接続しています。");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ready.TrySetCanceled(token); }
        catch (Exception e) { ready.TrySetException(e); failure.TrySetResult(e); Log.Error("Streaming stopped", e); }
    }
    public ValueTask DisposeAsync()
    { lock (gate) return new(dispose ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        life.Cancel();
        try { if (run != null) await run; }
        finally { life.Dispose(); }
        if (cleanupFailure != null) throw cleanupFailure;
    }
}
