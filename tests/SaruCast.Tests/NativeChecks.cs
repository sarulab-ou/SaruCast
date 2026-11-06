using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Concentus;
using SaruCast;
using RikRealization.ClassicCast.Protocol.Rtp;
using RikRealization.ClassicCast.Protocol.Streaming;
using RikRealization.ClassicCast.Protocol.Media;
using RikRealization.ClassicCast.Media;

static class NativeChecks
{
    internal static StreamOffer Offer() => new() { Streams = [new CastVideoStream { Index = 0, Ssrc = 42, RtpPayloadType = 96, RtpTimebase = 90000, AesKey = new byte[16], AesIvMask = new byte[16], MaxFrameRate = "15/1" }] };
    internal static byte[] Feedback(uint ssrc, byte checkpoint, bool nack = false)
    {
        byte[] b = new byte[nack ? 24 : 20]; b[0] = 0x8f; b[1] = 206;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)(b.Length / 4 - 1));
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), ssrc);
        "CAST"u8.CopyTo(b.AsSpan(12)); b[16] = checkpoint; b[17] = nack ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(18), 200);
        if (nack) { b[20] = 0; b[21] = 0; b[22] = 0; b[23] = 2; } // frame 0, packets 0 and 2
        return b;
    }
    public static async Task RunAsync(Action<bool,string> check)
    {
        using var crypto = new FrameCrypto(new byte[16], new byte[16]);
        var cipher = crypto.Encrypt(0, new byte[32]);
        check(Convert.ToHexString(cipher) == "66E94BD4EF8A2C3B884CFA59CA342B2E58E2FCCEFA7E3061367F1D57A4E7455A", "AES-CTR known zero-key blocks");
        check(!crypto.Encrypt(256, new byte[32]).SequenceEqual(cipher), "Full frame ID affects nonce across 8-bit wrap");
        byte[] data = Enumerable.Range(0, 4000).Select(i => (byte)i).ToArray();
        check(crypto.Decrypt(29, crypto.Encrypt(29, data)).SequenceEqual(data), "Whole access unit encryption round trip");
        var packetizer = new CastRtpPacketizer(96, 42, 1200);
        var frame = new EncodedFrame { FrameId = 256, ReferencedFrameId = 255, IsKeyFrame = false, RtpTimestamp = 90000, Data = data };
        int count = packetizer.ComputePacketCount(data.Length);
        var packets = Enumerable.Range(0, count).Select(i => packetizer.GeneratePacket(frame, data, i, count)).ToArray();
        check(packets.All(p => p.Length <= 1200) && packets.SelectMany(p => p.Skip(19)).SequenceEqual(data), "MTU-safe packetization reassembles complete frame");
        check(packets[0][13] == 0 && packets[0][18] == 255 && (packets[^1][1] & 128) != 0 && (packets[0][1] & 128) == 0, "Frame wrap, reference and final-packet marker");
        var f = CastRtcpParser.Parse(Feedback(42, 9, true));
        check(f.MediaSsrc == 42 && f.CheckpointFrameId == 9 && f.PlayoutDelay?.TotalMilliseconds == 200 && f.Nacks.SequenceEqual(new[] { new PacketNack(0,0), new PacketNack(0,2) }), "Parse Cast ACK and NACK bit mask");
        check(!CastRtcpParser.Parse(new byte[] { 0x8f,206,0,20,1 }).HasFeedback, "Truncated feedback cannot acknowledge media");
        var sr = RtcpSenderReport.Build(42, DateTimeOffset.UnixEpoch.AddMilliseconds(500), 90000, 4, 800);
        check(BinaryPrimitives.ReadUInt32BigEndian(sr.AsSpan(8)) == 2208988800 && BinaryPrimitives.ReadUInt32BigEndian(sr.AsSpan(12)) == 0x80000000 && BinaryPrimitives.ReadUInt32BigEndian(sr.AsSpan(16)) == 90000, "NTP to RTP sender report mapping");
        using var offerJson = JsonDocument.Parse(Offer().ToJson("7"));
        check(offerJson.RootElement.GetProperty("seqNum").GetInt32() == 7 && offerJson.RootElement.GetProperty("offer").GetProperty("supportedStreams").GetArrayLength() == 1, "Video-only native OFFER has integer sequence");
        var splitter = new H264StreamSplitter();
        byte[] annex = [0,0,0,1,9,0xf0,0,0,1,0x65,0x80,0x11,0,0,0,1,9,0xf0,0,0,1,0x41,0x80,0x22,0,0,0,1,9,0xf0,0,0,1,0x41,0x80,0x33];
        List<H264AccessUnit> units = [];
        foreach (byte value in annex) units.AddRange(splitter.Push(new[] { value }));
        check(units.Count == 2 && units[0].IsKeyFrame && !units[1].IsKeyFrame, "Fragmented Annex-B yields complete access units");
        using var opus = new OpusAudioEncoder(48000, 2, 128000, 960);
        using var decoder = OpusCodecFactory.CreateDecoder(48000, 2);
        check(!OpusCodecFactory.AttemptToUseNativeLibrary && decoder.GetType().FullName == "Concentus.Structs.OpusDecoder", "Opus uses bundled managed implementation without native probing");
        short[] pcm = Enumerable.Range(0,1920).Select(i => (short)(5000 * Math.Sin(2 * Math.PI * 440 * (i / 2) / 48000))).ToArray();
        var encoded = opus.Encode(pcm); short[] decoded = new short[1920];
        int samples = decoder.Decode(encoded.AsSpan(), decoded.AsSpan(), 960, false);
        check(samples == 960 && decoded.Any(s => Math.Abs((int)s) > 50), "Managed Opus 20ms stereo audio decodes");
        await UdpAsync(crypto, data, check);
        foreach (bool invalid in new[] { false, true })
        {
            await using var mock = new NativeReceiver(invalid);
            await using (var cast = new CastClient())
            {
                using var timeout = new CancellationTokenSource(10000);
                await cast.ConnectAsync(new("mock", "127.0.0.1", mock.Port), timeout.Token);
                bool rejected = false;
                try { var answer = await cast.LaunchStreamingAsync(Offer(), timeout.Token); check(answer.UdpPort == 23456 && answer.SendIndexes.SequenceEqual(new[] { 0 }), "TLS native LAUNCH / CONNECT / OFFER / ANSWER"); }
                catch (IOException) { rejected = true; }
                check(rejected == invalid, invalid ? "Invalid receiver UDP port rejected" : "Native answer accepted without media LOAD");
            }
            check(await mock.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3)), "Native STOP targets launched session");
        }
    }
    private static async Task UdpAsync(FrameCrypto crypto, byte[] data, Action<bool,string> check)
    {
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await using var transport = new CastStreamingTransport((IPEndPoint)peer.Client.LocalEndPoint!, IPAddress.Loopback);
        var video = transport.AddStream(42, 96, crypto); var audio = transport.AddStream(43, 127, crypto);
        var videoAck = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var audioAck = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        video.FeedbackReceived += f => { if (f.CheckpointFrameId is { } c) videoAck.TrySetResult(c); };
        audio.FeedbackReceived += f => { if (f.CheckpointFrameId is { } c) audioAck.TrySetResult(c); };
        transport.StartReceiving(); using var timeout = new CancellationTokenSource(5000);
        await video.SendFrameAsync(new EncodedFrame { FrameId = 0, ReferencedFrameId = 0, IsKeyFrame = true, RtpTimestamp = 0, Data = data }, timeout.Token);
        List<byte[]> received = [];
        for (int i=0; i<4; i++) received.Add((await peer.ReceiveAsync(timeout.Token)).Buffer);
        check(crypto.Decrypt(0, received.SelectMany(p => p.Skip(19)).ToArray()).SequenceEqual(data), "UDP payload decrypts to original frame");
        byte[] compound = Feedback(42, 4, true).Concat(Feedback(43, 8)).ToArray();
        await peer.SendAsync(compound, transport.LocalEndPoint, timeout.Token);
        check(await videoAck.Task.WaitAsync(timeout.Token) == 4 && await audioAck.Task.WaitAsync(timeout.Token) == 8, "Compound RTCP routes video and audio independently");
        var retry0 = (await peer.ReceiveAsync(timeout.Token)).Buffer; var retry2 = (await peer.ReceiveAsync(timeout.Token)).Buffer;
        check(retry0.SequenceEqual(received[0]) && retry2.SequenceEqual(received[2]) && video.RetransmittedPackets == 2, "NACK retransmits only requested encrypted packets");
        await Task.Delay(2100, timeout.Token);
        await video.RetransmitAsync(new[] { new PacketNack(0, 0) }, timeout.Token);
        check(video.RetransmittedPackets == 2, "Expired retransmission cache does not replay stale frame");
    }
}

sealed class NativeReceiver : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task run;
    private readonly bool invalid;
    private readonly bool holdOffer;
    public TaskCompletionSource Offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public NativeReceiver(bool invalid, bool holdOffer = false) { this.invalid = invalid; this.holdOffer = holdOffer; listener.Start(); run = RunAsync(); }
    private async Task RunAsync()
    {
        try
        {
            using var rsa = RSA.Create(2048); var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var schannelCert = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
            using var tcp = await listener.AcceptTcpClientAsync(stop.Token); using var ssl = new SslStream(tcp.GetStream());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = schannelCert }, stop.Token);
            while (!stop.IsCancellationRequested)
            {
                var frame = await CastWire.ReadAsync(ssl, stop.Token); using var doc = JsonDocument.Parse(frame.Payload); var root = doc.RootElement;
                string? type = root.GetProperty("type").GetString(); int id = root.TryGetProperty("requestId", out var rid) ? rid.GetInt32() : 0;
                object? reply = null;
                if (type is "GET_STATUS" or "LAUNCH")
                {
                    if (type == "LAUNCH" && root.GetProperty("appId").GetString() != CastClient.MirroringAppId) throw new Exception("Wrong app ID");
                    reply = new { type = "RECEIVER_STATUS", requestId = id, status = new { applications = new[] { new { appId = CastClient.MirroringAppId, transportId = "transport-test", sessionId = "session-test" } } } };
                }
                if (type == "PING") reply = new { type = "PONG" };
                if (type == "LOAD") throw new Exception("HLS LOAD must never be sent");
                if (type == "OFFER")
                {
                    Offered.TrySetResult();
                    if (!holdOffer) reply = new { type = "ANSWER", seqNum = root.GetProperty("seqNum").GetInt32(), result = "ok", answer = new { udpPort = invalid ? 0 : 23456, sendIndexes = new[] { 0 }, ssrcs = new[] { 200U } } };
                }
                if (type == "STOP") { Stopped.TrySetResult(root.GetProperty("sessionId").GetString() == "session-test"); return; }
                if (reply != null)
                {
                    byte[] encoded = CastWire.Encode(new(type == "OFFER" ? "transport-test" : "receiver-0", frame.Source, frame.Namespace, JsonSerializer.Serialize(reply)));
                    for (int i=0; i<encoded.Length; i+=3) await ssl.WriteAsync(encoded.AsMemory(i, Math.Min(3, encoded.Length-i)), stop.Token);
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await run; stop.Dispose(); }
}
