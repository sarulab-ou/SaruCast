using System.Diagnostics;
using System.IO;
using System.Net;

namespace SaruCast;

public sealed class CastSession : IAsyncDisposable
{
    private CastClient? client;
    private Encoder? encoder;
    private AudioLoopback? audio;
    private HlsServer? server;
    private readonly CancellationTokenSource stop = new();
    private Task? monitor;
    private readonly object disposeGate = new();
    private Task? disposeTask;
    public event Action<string>? Status;
    public event Action<string>? Disconnected;
    public async Task StartAsync(CastDevice television, DisplayInfo display, Settings settings, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(100)); var token = linked.Token;
        var time = Stopwatch.StartNew();
        client = new(); Status?.Invoke("接続中…"); await client.ConnectAsync(television, token);
        if (settings.Audio) { audio = new(); audio.Start(); }
        encoder = new(); Status?.Invoke("映像と音声を準備中…"); await encoder.StartAsync(display, settings, audio?.PipeName, token);
        server = new(client.LocalAddress, IPAddress.Parse(television.Address), encoder.DirectoryPath);
        Status?.Invoke("テレビの再生を待っています…");
        try { await client.LaunchAndLoadAsync(server.Url, token); }
        catch (TimeoutException) when (server.PlaylistRequests == 0)
        {
            throw new IOException("テレビが映像を取得できません。現在のネットワークでSaruCastの受信が許可されているか確認してください。", null);
        }
        if (server.SegmentRequests == 0)
        {
            for (int i = 0; i < 50 && server.SegmentRequests == 0; i++) await Task.Delay(100, token);
            if (server.SegmentRequests == 0) throw new IOException("テレビから映像を取得できません。Windowsのプライベートネットワークの許可を確認してください。");
        }
        Log.Write($"Receiver PLAYING and segment fetched in {time.Elapsed.TotalSeconds:F2}s (not glass-to-glass latency)");
        Status?.Invoke($"配信中（テレビが再生状態を通知） · {encoder.Width}×{encoder.Height} / 30fps");
        monitor = MonitorAsync();
    }
    private async Task MonitorAsync()
    {
        Task<Exception>[] failures = audio == null ? [client!.Failure, encoder!.Failure] : [client!.Failure, encoder!.Failure, audio.Failure];
        try
        {
            Task wait = Task.WhenAny(failures);
            await wait.WaitAsync(stop.Token);
            if (!stop.IsCancellationRequested)
            {
                Exception e = await await Task.WhenAny(failures);
                Log.Write($"Session failed {e.GetType().Name}"); Disconnected?.Invoke(e.Message);
            }
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
        List<Exception> errors = [];
        foreach (IAsyncDisposable? resource in new IAsyncDisposable?[] { server, client, audio, encoder })
        {
            if (resource == null) continue;
            try { await resource.DisposeAsync(); }
            catch (Exception error) { Log.Error("Cleanup failed", error); errors.Add(error); }
        }
        if (monitor != null) await monitor;
        stop.Dispose();
        Log.Write("Session stopped; capture, audio, sender, process and HTTP closed");
        if (errors.Count > 0) throw new AggregateException("停止処理の一部に失敗しました。診断ログを確認してください。", errors);
    }
}
