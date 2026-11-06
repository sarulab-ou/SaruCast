using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using RikRealization.ClassicCast.Protocol.Streaming;

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
    public const string Receiver = "urn:x-cast:com.google.cast.receiver";
    public const string Connection = "urn:x-cast:com.google.cast.tp.connection", Heartbeat = "urn:x-cast:com.google.cast.tp.heartbeat";
    public const string StreamingNamespace = "urn:x-cast:com.google.cast.webrtc";
    public const string MirroringAppId = "0F5096E8";
    private readonly TcpClient tcp = new(AddressFamily.InterNetwork);
    private SslStream? ssl;
    private readonly string sender = "sender-" + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource life = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> streamingPending = new();
    private readonly TaskCompletionSource<Exception> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? reader, heartbeat;
    private long lastReceive = Environment.TickCount64;
    private int requestId;
    private int seqNum;
    private string? transport, session;
    private bool appRunning;
    public IPAddress LocalAddress => ((IPEndPoint)tcp.Client.LocalEndPoint!).Address;
    public Task<Exception> Failure => failure.Task;
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
        reader = Task.Run(ReadLoopAsync); heartbeat = Task.Run(HeartbeatAsync);
        await SendAsync("receiver-0", Connection, new { type = "CONNECT", origin = new { } }, ct);
        var status = await RequestAsync("receiver-0", Receiver, new() { ["type"] = "GET_STATUS" }, ct);
        if (status.GetProperty("type").GetString() != "RECEIVER_STATUS") throw new IOException("Google Cast機器の応答を確認できません。");
    }
    public async Task<StreamAnswer> LaunchStreamingAsync(StreamOffer offer, CancellationToken ct)
    {
        var response = await RequestAsync("receiver-0", Receiver, new() { ["type"] = "LAUNCH", ["appId"] = MirroringAppId }, ct, 25);
        if (!response.TryGetProperty("status", out var status) || !status.TryGetProperty("applications", out var apps))
            throw new IOException("テレビのミラーリング機能を起動できません。");
        foreach (var app in apps.EnumerateArray())
            if (app.TryGetProperty("appId", out var id) && id.GetString() == MirroringAppId)
            { transport = app.GetProperty("transportId").GetString(); session = app.GetProperty("sessionId").GetString(); break; }
        if (transport == null) throw new IOException("テレビに対応するミラーリング機能がありません。");
        await SendAsync(transport, Connection, new { type = "CONNECT", userAgent = "SaruCast", connType = 0, origin = new { } }, ct);
        appRunning = true;
        int seq = Interlocked.Increment(ref seqNum);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        streamingPending[seq] = completion;
        try
        {
            using var json = JsonDocument.Parse(offer.ToJson(seq.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await SendAsync(transport, StreamingNamespace, json.RootElement, ct);
            var reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            if (!reply.TryGetProperty("type", out var type) || type.GetString() != "ANSWER") throw new IOException("ミラーリングの応答形式を確認できません。");
            var answer = StreamAnswer.Parse(reply);
            if (answer.UdpPort is < 1 or > 65535 || answer.SendIndexes.Count == 0 || answer.SendIndexes.Count != answer.Ssrcs.Count || answer.SendIndexes.Distinct().Count() != answer.SendIndexes.Count)
                throw new IOException("テレビからのメディア条件が不正です。");
            if (answer.SendIndexes.Any(index => !offer.Streams.Any(s => s.Index == index))) throw new IOException("テレビが未提案の形式を選択しました。");
            Log.Write($"Streaming ANSWER accepted streams={answer.SendIndexes.Count} (media transfer not yet verified)");
            return answer;
        }
        finally { streamingPending.TryRemove(seq, out _); }
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
                if (frame.Namespace == Receiver && frame.Source == "receiver-0" && root.TryGetProperty("requestId", out var rid) && rid.TryGetInt32(out int id) && pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(root.Clone());
                if (frame.Namespace == StreamingNamespace && frame.Source == transport && root.TryGetProperty("seqNum", out var sn) && sn.TryGetInt32(out int seq) && streamingPending.TryGetValue(seq, out var result)) result.TrySetResult(root.Clone());
                if (frame.Namespace == Receiver && appRunning && type == "RECEIVER_STATUS" && root.TryGetProperty("status", out var rs))
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
        failure.TrySetResult(e);
        foreach (var entry in pending.Values) entry.TrySetException(e);
        foreach (var entry in streamingPending.Values) entry.TrySetException(e);
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
