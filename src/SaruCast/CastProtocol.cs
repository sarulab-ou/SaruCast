using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace SaruCast;

public sealed record CastFrame(string Source, string Destination, string Namespace, string Payload);
public static class CastWire
{
    public static byte[] Encode(CastFrame frame)
    {
        using var body = new MemoryStream();
        body.Write([8, 0]); // protocol_version CASTV2_1_0
        Field(body, 2, frame.Source); Field(body, 3, frame.Destination); Field(body, 4, frame.Namespace);
        body.Write([40, 0]); Field(body, 6, frame.Payload); // STRING payload
        var data = body.ToArray();
        byte[] packet = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet, data.Length); data.CopyTo(packet, 4); return packet;
    }
    private static void Field(Stream stream, int number, string value)
    {
        Varint(stream, (uint)(number * 8 + 2)); var bytes = Encoding.UTF8.GetBytes(value); Varint(stream, (uint)bytes.Length); stream.Write(bytes);
    }
    private static void Varint(Stream stream, uint value)
    {
        while (value >= 128) { stream.WriteByte((byte)(value | 128)); value >>= 7; } stream.WriteByte((byte)value);
    }
    private static uint ReadVar(ReadOnlySpan<byte> data, ref int pos)
    {
        uint value = 0;
        for (int shift = 0; shift <= 28 && pos < data.Length; shift += 7)
        {
            byte b = data[pos++]; value |= (uint)(b & 127) << shift; if (b < 128) return value;
        }
        throw new InvalidDataException("不正なCastフレームです。");
    }
    public static CastFrame Decode(ReadOnlySpan<byte> data)
    {
        string src = "", dest = "", ns = "", payload = ""; int pos = 0;
        while (pos < data.Length)
        {
            uint tag = ReadVar(data, ref pos); int field = (int)(tag >> 3), wire = (int)(tag & 7);
            if (field == 0) throw new InvalidDataException();
            if (wire == 0) { ReadVar(data, ref pos); continue; }
            if (wire == 2)
            {
                uint len = ReadVar(data, ref pos); if (len > data.Length - pos) throw new InvalidDataException();
                var v = data.Slice(pos, (int)len); pos += (int)len;
                if (field is 2 or 3 or 4 or 6)
                {
                    string s = Encoding.UTF8.GetString(v);
                    switch (field) { case 2: src = s; break; case 3: dest = s; break; case 4: ns = s; break; case 6: payload = s; break; }
                }
                continue;
            }
            int skip = wire switch { 1 => 8, 5 => 4, _ => throw new InvalidDataException() };
            if (pos + skip > data.Length) throw new InvalidDataException(); pos += skip;
        }
        if (src.Length == 0 || dest.Length == 0 || ns.Length == 0 || payload.Length == 0) throw new InvalidDataException();
        return new(src, dest, ns, payload);
    }
    public static async Task<CastFrame> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        int len = BinaryPrimitives.ReadInt32BigEndian(header);
        if (len is < 1 or > 1048576) throw new InvalidDataException("Castフレームの長さが不正です。");
        byte[] data = new byte[len]; await stream.ReadExactlyAsync(data, ct); return Decode(data);
    }
}

public sealed class CastClient : IAsyncDisposable
{
    public const string Receiver = "urn:x-cast:com.google.cast.receiver", Media = "urn:x-cast:com.google.cast.media";
    public const string Connection = "urn:x-cast:com.google.cast.tp.connection", Heartbeat = "urn:x-cast:com.google.cast.tp.heartbeat";
    public const string AppId = "CC1AD845";
    private readonly TcpClient tcp = new(AddressFamily.InterNetwork);
    private SslStream? ssl;
    private readonly string sender = "sender-" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource life = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly TaskCompletionSource playing = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? reader, heartbeat;
    private long lastReceive = Environment.TickCount64;
    private int requestId;
    private string? transport, session;
    private string? expectedContent;
    private long? mediaSession;
    private bool loaded;
    public IPAddress LocalAddress => ((IPEndPoint)tcp.Client.LocalEndPoint!).Address;
    public Task<Exception> Failure => failure.Task;
    public string PlayerState { get; private set; } = "UNKNOWN";
    public double? ReceiverVolume { get; private set; }
    public bool? ReceiverMuted { get; private set; }

    public async Task ConnectAsync(CastDevice device, CancellationToken ct)
    {
        if (!IPAddress.TryParse(device.Address, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("テレビのIPv4アドレスを指定してください。");
        await tcp.ConnectAsync(address, device.Port, ct);
        tcp.NoDelay = true;
        // Cast devices use self-signed certificates. Device-auth trust validation is not implemented.
        ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = device.Address, EnabledSslProtocols = SslProtocols.None }, ct);
        reader = ReadLoopAsync(); heartbeat = HeartbeatAsync();
        await SendAsync("receiver-0", Connection, new { type = "CONNECT", origin = new { } }, ct);
        var status = await RequestAsync("receiver-0", Receiver, new() { ["type"] = "GET_STATUS" }, ct);
        if (status.GetProperty("type").GetString() != "RECEIVER_STATUS") throw new IOException("Google Cast機器の応答を確認できません。");
    }
    public async Task LaunchAndLoadAsync(string url, CancellationToken ct)
    {
        expectedContent = url;
        var response = await RequestAsync("receiver-0", Receiver, new() { ["type"] = "LAUNCH", ["appId"] = AppId }, ct, 25);
        if (!response.TryGetProperty("status", out var status) || !status.TryGetProperty("applications", out var apps))
            throw new IOException("テレビの標準プレーヤーを起動できません。");
        foreach (var app in apps.EnumerateArray())
            if (app.GetProperty("appId").GetString() == AppId)
            { transport = app.GetProperty("transportId").GetString(); session = app.GetProperty("sessionId").GetString(); break; }
        if (transport == null) throw new IOException("テレビに標準プレーヤーがありません。");
        await SendAsync(transport, Connection, new { type = "CONNECT", origin = new { } }, ct);
        loaded = true;
        await RequestAsync(transport, Media, new()
        {
            ["type"] = "LOAD", ["autoplay"] = true, ["sessionId"] = session, ["currentTime"] = 0,
            ["media"] = new { contentId = url, contentType = "application/x-mpegURL", streamType = "LIVE", hlsSegmentFormat = "ts", hlsVideoSegmentFormat = "mpeg2_ts", metadata = new { metadataType = 0, title = "SaruCast" } }
        }, ct, 30);
        await playing.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
    }
    private async Task<JsonElement> RequestAsync(string dest, string ns, Dictionary<string, object?> value, CancellationToken ct, int timeout = 10)
    {
        int id = Interlocked.Increment(ref requestId); value["requestId"] = id;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); pending[id] = tcs;
        try
        {
            await SendAsync(dest, ns, value, ct);
            var r = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(timeout), ct);
            string? type = r.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type is "LAUNCH_ERROR" or "LOAD_FAILED" or "INVALID_REQUEST" or "LOAD_CANCELLED") throw new IOException("テレビが再生要求を受け付けませんでした。");
            return r;
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task SendAsync(string dest, string ns, object value, CancellationToken ct)
    {
        byte[] bytes = CastWire.Encode(new(sender, dest, ns, JsonSerializer.Serialize(value)));
        await writeLock.WaitAsync(ct);
        try { await ssl!.WriteAsync(bytes, ct); } finally { writeLock.Release(); }
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (!life.IsCancellationRequested)
            {
                var frame = await CastWire.ReadAsync(ssl!, life.Token);
                if (frame.Destination != sender && frame.Destination != "*") continue;
                lastReceive = Environment.TickCount64;
                using var doc = JsonDocument.Parse(frame.Payload); var root = doc.RootElement;
                string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (frame.Namespace == Receiver && type == "RECEIVER_STATUS" && root.TryGetProperty("status", out var receiverStatus) && receiverStatus.TryGetProperty("volume", out var volume))
                {
                    if (volume.TryGetProperty("level", out var level) && level.TryGetDouble(out double value)) ReceiverVolume = value;
                    if (volume.TryGetProperty("muted", out var muted) && muted.ValueKind is JsonValueKind.True or JsonValueKind.False) ReceiverMuted = muted.GetBoolean();
                }
                if (type is "MEDIA_STATUS" or "LOAD_FAILED" or "INVALID_REQUEST" or "LOAD_CANCELLED" or "RECEIVER_STATUS")
                    Log.Write($"Cast received {type} requestId={(root.TryGetProperty("requestId", out var traceId) && traceId.TryGetInt32(out int trace) ? trace : -1)}");
                if (frame.Namespace == Heartbeat && type == "PING") await SendAsync(frame.Source, Heartbeat, new { type = "PONG" }, life.Token);
                if (frame.Namespace == Connection && type == "CLOSE") throw new IOException("テレビとの接続が閉じられました。");
                if (root.TryGetProperty("requestId", out var rid) && rid.TryGetInt32(out int id) && pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(root.Clone());
                if (frame.Namespace == Media && frame.Source == transport && type == "MEDIA_STATUS" && root.TryGetProperty("status", out var statuses))
                    foreach (var state in statuses.EnumerateArray())
                    {
                        // CONNECT emits an initial IDLE (and sometimes status for previous content).
                        // Only the content we loaded, or its confirmed media session, controls this sender.
                        bool current = state.TryGetProperty("media", out var media) && media.TryGetProperty("contentId", out var content) && content.GetString() == expectedContent;
                        long? mediaId = state.TryGetProperty("mediaSessionId", out var ms) && ms.TryGetInt64(out long mid) ? mid : null;
                        if (!current && (mediaSession == null || mediaId != mediaSession)) continue;
                        if (current && mediaId != null) mediaSession = mediaId;
                        PlayerState = state.TryGetProperty("playerState", out var s) ? s.GetString() ?? "UNKNOWN" : "UNKNOWN";
                        if (PlayerState is "IDLE" or "BUFFERING" or "PLAYING" or "PAUSED")
                        {
                            string? reason = state.TryGetProperty("idleReason", out var why) ? why.GetString() : null;
                            double? position = state.TryGetProperty("currentTime", out var time) && time.TryGetDouble(out double seconds) ? seconds : null;
                            Log.Write($"Receiver state={PlayerState} positionSeconds={position:F3} idleReason={(reason is "ERROR" or "CANCELLED" or "INTERRUPTED" or "FINISHED" ? reason : "none")}");
                        }
                        if (PlayerState == "PLAYING" && loaded) playing.TrySetResult();
                        string? idle = state.TryGetProperty("idleReason", out var idleValue) ? idleValue.GetString() : null;
                        if (loaded && PlayerState == "IDLE" && (playing.Task.IsCompletedSuccessfully || idle is "ERROR" or "CANCELLED" or "FINISHED" or "INTERRUPTED"))
                            throw new IOException(idle == "ERROR" ? "テレビで映像を読み込めません。ネットワーク許可と画質設定を確認してください。" : "テレビ側で再生が終了しました。");
                    }
                if (frame.Namespace == Receiver && loaded && type == "RECEIVER_STATUS" && root.TryGetProperty("status", out var rs))
                {
                    bool active = rs.TryGetProperty("applications", out var list) && list.EnumerateArray().Any(a => a.TryGetProperty("sessionId", out var sid) && sid.GetString() == session);
                    if (!active) throw new IOException("テレビ側でキャストが終了しました。");
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or JsonException or OperationCanceledException or InvalidOperationException)
        { if (!life.IsCancellationRequested) Fail(e); }
    }
    private void Fail(Exception e)
    {
        failure.TrySetResult(e); playing.TrySetException(e);
        foreach (var entry in pending.Values) entry.TrySetException(e);
        life.Cancel(); tcp.Dispose();
    }
    private async Task HeartbeatAsync()
    {
        try
        {
            while (!life.IsCancellationRequested)
            {
                await Task.Delay(5000, life.Token);
                if (Environment.TickCount64 - lastReceive > 20000) throw new IOException("テレビの応答がありません。電源とネットワークを確認してください。");
                await SendAsync("receiver-0", Heartbeat, new { type = "PING" }, life.Token);
                if (loaded && transport != null) await RequestAsync(transport, Media, new() { ["type"] = "GET_STATUS" }, life.Token);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or TimeoutException or ObjectDisposedException)
        { if (!life.IsCancellationRequested) Fail(e); }
    }
    public async ValueTask DisposeAsync()
    {
        if (session != null && !life.IsCancellationRequested)
        {
            using var stop = new CancellationTokenSource(1500);
            try { await SendAsync("receiver-0", Receiver, new { type = "STOP", sessionId = session, requestId = Interlocked.Increment(ref requestId) }, stop.Token); }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        life.Cancel(); tcp.Dispose(); ssl?.Dispose();
        try { await Task.WhenAll(reader ?? Task.CompletedTask, heartbeat ?? Task.CompletedTask); } catch (Exception) { }
        life.Dispose(); writeLock.Dispose();
    }
}
