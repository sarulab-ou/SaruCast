namespace SaruCast;

public sealed class CastSession : IAsyncDisposable
{
    private readonly StreamingSession streaming = new();
    private readonly CancellationTokenSource stop = new();
    private readonly object disposeGate = new();
    private Task? monitor, disposeTask;
    public event Action<string>? Status;
    public event Action<string>? Disconnected;
    public async Task StartAsync(CastDevice television, DisplayInfo display, Settings settings, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        streaming.Status += value => Status?.Invoke(value);
        await streaming.StartAsync(television, display, settings, linked.Token);
        monitor = MonitorAsync();
    }
    private async Task MonitorAsync()
    {
        try
        {
            var error = await streaming.Failure.WaitAsync(stop.Token);
            if (!stop.IsCancellationRequested) Disconnected?.Invoke(error.Message);
        }
        catch (OperationCanceledException) { }
    }
    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new(disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        stop.Cancel();
        try { await streaming.DisposeAsync(); }
        finally { if (monitor != null) await monitor; stop.Dispose(); }
    }
}
