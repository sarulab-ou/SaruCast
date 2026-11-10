using System.Runtime.CompilerServices;
using System.Threading.Channels;
using RikRealization.ClassicCast.Protocol.Media;

[assembly: InternalsVisibleTo("SaruCast.Tests")]
namespace SaruCast;

internal sealed record TimedVideoFrame(H264AccessUnit Unit, long Sequence, long ReceivedAt);

// Keep draining the encoder even while UDP is slow. A missing P frame invalidates
// its dependants: resume only at an IDR, never send an arbitrary newer P frame.
internal sealed class LiveVideoBuffer
{
    private long sequence, dropped;
    private int clearing;
    private readonly Channel<TimedVideoFrame> queue;
    public event Action<string>? Cleared;
    public long Dropped => Interlocked.Read(ref dropped);
    public LiveVideoBuffer()
    {
        queue = Channel.CreateBounded<TimedVideoFrame>(new BoundedChannelOptions(4)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest }, _ => Drop());
    }
    private void Drop()
    {
        Interlocked.Increment(ref dropped);
        if (Interlocked.Exchange(ref clearing, 1) == 0)
            Cleared?.Invoke("送信待ちバッファをクリアしました。古い映像を破棄し、次のキーフレームから再開します。");
    }
    public void Add(H264AccessUnit unit, long now) => queue.Writer.TryWrite(new(unit, sequence++, now));
    public void Complete(Exception? error = null) => queue.Writer.TryComplete(error);
    public async IAsyncEnumerable<TimedVideoFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        long previous = -1; bool needsKeyFrame = true;
        await foreach (var frame in queue.Reader.ReadAllAsync(ct))
        {
            if (frame.Sequence != previous + 1) needsKeyFrame = true;
            previous = frame.Sequence;
            if (Environment.TickCount64 - frame.ReceivedAt > 250)
            { needsKeyFrame = true; Drop(); continue; }
            if (needsKeyFrame && !frame.Unit.IsKeyFrame) { Drop(); continue; }
            needsKeyFrame = false;
            Interlocked.Exchange(ref clearing, 0);
            yield return frame;
        }
    }
}

// All times are monotonic milliseconds. Checkpoints acknowledge receipt, not display.
internal sealed class StreamingLatencyMonitor
{
    private readonly object gate = new();
    private readonly SortedDictionary<long, long> pending = [];
    private long latest = -1, acknowledged = -1, highSince = -1;
    private long delayAt, delayMs;
    public void Sent(long id, long now)
    {
        lock (gate)
        {
            latest = id; pending[id] = now;
            // An 8-bit checkpoint cannot identify older generations unambiguously.
            // The watchdog normally resets well before this bounded fallback.
            while (pending.Count > 128) pending.Remove(pending.First().Key);
        }
    }
    public void Feedback(byte? checkpoint, double? playoutDelayMs, long now)
    {
        lock (gate)
        {
            if (playoutDelayMs is { } delay) { delayAt = now; delayMs = (long)delay; }
            if (checkpoint is not { } cp || latest < 0) return;
            long full = latest - (byte)(latest - cp);
            if (full <= acknowledged) return;
            acknowledged = full;
            foreach (long id in pending.Keys.TakeWhile(id => id <= full).ToArray()) pending.Remove(id);
        }
    }
    public string? Check(long now, long sessionAge, double encoderLagMs)
    {
        lock (gate)
        {
            if (sessionAge < 5000) { highSince = -1; return null; }
            long receiptLag = pending.Count == 0 ? 0 : now - pending.First().Value;
            long playout = now - delayAt <= 1000 ? delayMs : 0;
            string? reason = encoderLagMs > 1000 ? $"エンコーダーの処理遅れ 約{encoderLagMs / 1000:F1}秒"
                : receiptLag > 1000 ? $"映像の受信確認待ち 約{receiptLag / 1000.0:F1}秒"
                : playout > 1000 ? $"テレビが通知した再生バッファ 約{playout / 1000.0:F1}秒" : null;
            if (reason == null) { highSince = -1; return null; }
            if (highSince < 0) highSince = now;
            return now - highSince >= 500 ? reason : null;
        }
    }
}

internal sealed class EncoderTimeline
{
    private readonly object gate = new();
    private long firstAt = -1, firstPts, lastPts;
    public void Update(long microseconds, long now)
    {
        lock (gate)
        {
            if (firstAt < 0) { firstAt = now; firstPts = microseconds; }
            lastPts = Math.Max(lastPts, microseconds);
        }
    }
    public double LagMilliseconds(long now)
    { lock (gate) return firstAt < 0 ? 0 : Math.Max(0, now - firstAt - (lastPts - firstPts) / 1000.0); }
}
