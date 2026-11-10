using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using RikRealization.ClassicCast.Protocol.Rtp;
using RikRealization.ClassicCast.Protocol.Streaming;
using RikRealization.ClassicCast.Media;

namespace SaruCast;

internal sealed class StreamingConnection : IStreamingConnection
{
    private readonly CancellationTokenSource life = new();
    private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource receiving = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch clock = new();
    private readonly StreamingLatencyMonitor latency = new();
    private readonly TaskCompletionSource<string> reset = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset origin;
    private CastClient? client;
    private CastStreamingTransport? transport;
    private CastRtpStream? video;
    private FrameCrypto? videoCrypto;
    private CastRtpStream? audio;
    private FrameCrypto? audioCrypto;
    private StreamingAudioSource? audioSource;
    private OpusAudioEncoder? opus;
    private Task? audioPump;
    private uint audioTimestampBase;
    private long audioFrames, audioFeedback, audioSkippedFrames, lastFeedbackAt;
    private bool testTone;
    private StreamOffer? offer;
    private StreamingVideoEncoder? encoder;
    private Task? videoPump, reports, watch, latencyWatch;
    private long frames, feedback, checkpoints;
    private int lastCheckpoint = -1;
    private long lastCheckpointAt;
    private readonly object disposeGate = new();
    private Task? disposeTask;
    public Task<Exception> Failure => failure.Task;
    public Task<string> ResetRequested => reset.Task;
    public long DroppedVideoFrames => encoder?.Frames.Dropped ?? 0;
    public long FramesSent => Interlocked.Read(ref frames);
    public long FeedbackCount => Interlocked.Read(ref feedback);
    public long CheckpointChanges => Interlocked.Read(ref checkpoints);
    public long RetransmittedPackets => video?.RetransmittedPackets ?? 0;
    public long AudioFramesSent => Interlocked.Read(ref audioFrames);
    public long AudioFeedbackCount => Interlocked.Read(ref audioFeedback);
    public long AudioSkippedFrames => Interlocked.Read(ref audioSkippedFrames);
    public int AudioPeakPcm16 => audioSource?.PeakPcm16 ?? 0;
    public int Width => encoder?.Width ?? 0;
    public int Height => encoder?.Height ?? 0;
    public event Action<string>? Status;
    public event Action<string>? Activity;
    public async Task StartAsync(CastDevice device, DisplayInfo display, Settings settings, CancellationToken ct, bool testPattern = false)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(life.Token, ct); startup.CancelAfter(45000);
        var token = startup.Token;
        client = new(); Status?.Invoke("テレビに接続中…"); await client.ConnectAsync(device, token);
        var size = Encoder.Size(display.Width, display.Height, settings.Height);
        var spec = new CastVideoStream { Index = 0, Ssrc = CastSsrc.Next(), Codec = CastVideoCodec.H264, RtpPayloadType = RtpPayloadTypes.VideoAndroidHack, RtpTimebase = 90000, TargetDelay = TimeSpan.FromMilliseconds(200), AesKey = RandomNumberGenerator.GetBytes(16), AesIvMask = RandomNumberGenerator.GetBytes(16), Profile = "baseline", Level = "4.1", MaxFrameRate = $"{Settings.NormalizeFrameRate(settings.FrameRate)}/1", MaxBitRate = Encoder.VideoBitrateKbps(settings) * 1000, Resolutions = [new(size.Width, size.Height)] };
        List<CastStream> specs = [spec];
        CastAudioStream? audioSpec = settings.Audio ? new CastAudioStream { Index = 1, Ssrc = CastSsrc.Next(), Codec = CastAudioCodec.Opus, RtpPayloadType = RtpPayloadTypes.AudioAndroidHack, RtpTimebase = 48000, TargetDelay = spec.TargetDelay, AesKey = RandomNumberGenerator.GetBytes(16), AesIvMask = RandomNumberGenerator.GetBytes(16), BitRate = 128000, Channels = 2 } : null;
        if (audioSpec != null) specs.Add(audioSpec);
        offer = new StreamOffer { Streams = specs };
        Status?.Invoke("テレビのミラーリング機能を準備中…");
        var answer = await client.LaunchStreamingAsync(offer, token);
        if (!answer.SendIndexes.Contains(0)) throw new IOException("テレビがH.264映像を受け付けませんでした。");
        if (audioSpec != null && !answer.SendIndexes.Contains(1)) throw new IOException("テレビがPC音声の形式を受け付けませんでした。");
        transport = new(new IPEndPoint(IPAddress.Parse(device.Address), answer.UdpPort), client.LocalAddress);
        videoCrypto = new(spec.AesKey, spec.AesIvMask); video = transport.AddStream(spec.Ssrc, spec.RtpPayloadType, videoCrypto);
        video.FeedbackReceived += OnFeedback;
        if (audioSpec != null)
        {
            audioCrypto = new(audioSpec.AesKey, audioSpec.AesIvMask); audio = transport.AddStream(audioSpec.Ssrc, audioSpec.RtpPayloadType, audioCrypto);
            audio.FeedbackReceived += _ => { Interlocked.Increment(ref audioFeedback); };
            opus = new(48000, 2, audioSpec.BitRate, 960);
            testTone = testPattern;
            if (!testTone) audioSource = new();
        }
        transport.StartReceiving();
        clock.Start(); origin = DateTimeOffset.UtcNow;
        encoder = new();
        encoder.Frames.Cleared += message => Activity?.Invoke(message);
        encoder.Start(display, settings, testPattern);
        if (audio != null) { audioSource?.Start(); audioTimestampBase = (uint)(clock.Elapsed.TotalSeconds * 48000); audioPump = Task.Run(AudioAsync); }
        videoPump = Task.Run(VideoAsync); reports = Task.Run(ReportsAsync); watch = Task.Run(WatchAsync);
        latencyWatch = Task.Run(LatencyAsync);
        Status?.Invoke("映像を送信中・テレビの受信確認を待っています…");
        try { await receiving.Task.WaitAsync(TimeSpan.FromSeconds(15), token); }
        catch (OperationCanceledException) when (failure.Task.IsCompletedSuccessfully && !ct.IsCancellationRequested)
        { throw await failure.Task; }
        Status?.Invoke($"配信中（テレビが映像受信を通知） · {Width}×{Height} / {Settings.NormalizeFrameRate(settings.FrameRate)}fps");
    }
    private void OnFeedback(CastReceiverFeedback value)
    {
        latency.Feedback(value.CheckpointFrameId, value.PlayoutDelay?.TotalMilliseconds, Environment.TickCount64);
        Interlocked.Increment(ref feedback);
        Interlocked.Exchange(ref lastFeedbackAt, Environment.TickCount64);
        if (value.CheckpointFrameId is { } cp && !(cp == 255 && FramesSent < 255))
        {
            if (Interlocked.Exchange(ref lastCheckpoint, cp) != cp)
            { Interlocked.Increment(ref checkpoints); Interlocked.Exchange(ref lastCheckpointAt, Environment.TickCount64); }
            if (CheckpointChanges >= 2 && FramesSent >= 3) receiving.TrySetResult();
        }
    }
    private async Task AudioAsync()
    {
        uint frameId = 0; long sampleOffset = 0; long nextDeviceCheck = 1000;
        var started = Stopwatch.StartNew(); using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(life.Token))
            {
                if (started.ElapsedMilliseconds >= nextDeviceCheck) { audioSource?.ValidateDevice(); nextDeviceCheck += 1000; }
                // Discard stale PCM after a scheduling stall instead of building a growing queue.
                while (!testTone && started.Elapsed.TotalSeconds - sampleOffset / 48000.0 > 0.1)
                { audioSource!.ReadFrame(); sampleOffset += 960; Interlocked.Increment(ref audioSkippedFrames); }
                while ((sampleOffset + 960) / 48000.0 <= started.Elapsed.TotalSeconds)
                {
                short[] pcm;
                if (testTone)
                {
                    pcm = new short[1920];
                    for (int i = 0; i < 960; i++) { short value = (short)(3000 * Math.Sin(2 * Math.PI * 440 * (sampleOffset + i) / 48000)); pcm[2 * i] = pcm[2 * i + 1] = value; }
                }
                else pcm = audioSource!.ReadFrame();
                byte[] encoded = opus!.Encode(pcm);
                await audio!.SendFrameAsync(new EncodedFrame { FrameId = frameId, ReferencedFrameId = frameId, IsKeyFrame = true, RtpTimestamp = audioTimestampBase + (uint)sampleOffset, Data = encoded }, life.Token);
                frameId++; sampleOffset += 960; Interlocked.Increment(ref audioFrames);
                }
            }
        }
        catch (Exception e) { if (!life.IsCancellationRequested) Fail(e); }
    }
    private async Task VideoAsync()
    {
        uint frameId = 0;
        try
        {
            await foreach (var frame in encoder!.Frames.ReadAllAsync(life.Token))
            {
                var unit = frame.Unit;
                if (frameId == 0 && !unit.IsKeyFrame) continue;
                // Preserve pipe arrival time; UDP waiting must not make old media look new.
                double ageSeconds = (Environment.TickCount64 - frame.ReceivedAt) / 1000.0;
                uint timestamp = (uint)Math.Max(0, (clock.Elapsed.TotalSeconds - ageSeconds - 1.0 / encoder.FrameRate) * 90000);
                latency.Sent(frameId, Environment.TickCount64);
                await video!.SendFrameAsync(new EncodedFrame { FrameId = frameId, ReferencedFrameId = unit.IsKeyFrame ? frameId : frameId - 1, IsKeyFrame = unit.IsKeyFrame, RtpTimestamp = timestamp, Data = unit.Data }, life.Token);
                frameId++; Interlocked.Increment(ref frames);
            }
        }
        catch (Exception e) { if (!life.IsCancellationRequested) Fail(e); }
    }
    private async Task ReportsAsync()
    {
        try
        {
            while (!life.IsCancellationRequested)
            {
                var elapsed = clock.Elapsed;
                await video!.SendSenderReportAsync((uint)(elapsed.TotalSeconds * 90000), origin + elapsed, life.Token);
                if (audio != null) await audio.SendSenderReportAsync((uint)(elapsed.TotalSeconds * 48000), origin + elapsed, life.Token);
                long last = Interlocked.Read(ref lastFeedbackAt);
                if (last != 0 && Environment.TickCount64 - last > 8000) throw new IOException("テレビへの映像送信が途切れました。ネットワークを確認してください。");
                long checkpointAt = Interlocked.Read(ref lastCheckpointAt);
                if (checkpointAt != 0 && Environment.TickCount64 - checkpointAt > 8000) throw new IOException("テレビの映像受信が進んでいません。停止して再接続してください。");
                await Task.Delay(250, life.Token);
            }
        }
        catch (Exception e) { if (!life.IsCancellationRequested) Fail(e); }
    }
    private async Task WatchAsync()
    {
        try { var error = await client!.Failure.WaitAsync(life.Token); Fail(error); } catch (OperationCanceledException) { }
    }
    private async Task LatencyAsync()
    {
        try
        {
            while (!life.IsCancellationRequested)
            {
                if (latency.Check(Environment.TickCount64, clock.ElapsedMilliseconds, encoder!.LagMilliseconds) is { } reason)
                    reset.TrySetResult(reason);
                await Task.Delay(100, life.Token);
            }
        }
        catch (OperationCanceledException) when (life.IsCancellationRequested) { }
        catch (Exception e) { if (!life.IsCancellationRequested) Fail(e); }
    }
    private void Fail(Exception error) { failure.TrySetResult(error); receiving.TrySetException(error); life.Cancel(); }
    public (long WorkingSet, double CpuSeconds) EncoderMetrics() => encoder?.Metrics() ?? (0, 0);
    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new(disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        life.Cancel();
        List<Exception> errors = [];
        async Task Cleanup(Func<Task> action) { try { await action(); } catch (Exception e) { errors.Add(e); Log.Error("Native cleanup failed", e); } }
        if (encoder != null) await Cleanup(async () => await encoder.DisposeAsync());
        await Cleanup(() => Task.WhenAll(videoPump ?? Task.CompletedTask, audioPump ?? Task.CompletedTask, reports ?? Task.CompletedTask, watch ?? Task.CompletedTask, latencyWatch ?? Task.CompletedTask));
        if (transport != null) await Cleanup(async () => await transport.DisposeAsync());
        foreach (IDisposable? resource in new IDisposable?[] { videoCrypto, audioCrypto, opus, audioSource })
            if (resource != null) await Cleanup(() => { resource.Dispose(); return Task.CompletedTask; });
        if (client != null) await Cleanup(async () => await client.DisposeAsync());
        if (offer != null) foreach (var stream in offer.Streams) { CryptographicOperations.ZeroMemory(stream.AesKey); CryptographicOperations.ZeroMemory(stream.AesIvMask); }
        life.Dispose();
        Log.Write($"Native session stopped frames={FramesSent} feedback={FeedbackCount} checkpoints={CheckpointChanges}");
        if (errors.Count > 0) throw new AggregateException("停止処理の一部に失敗しました。診断ログを確認してください。", errors);
    }
}
