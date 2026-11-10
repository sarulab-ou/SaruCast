using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SaruCast;
using RikRealization.ClassicCast.Protocol.Rtp;

// Opt-in real encoder + TLS + encrypted A/V UDP test, exclusively on loopback.
static class RecoveryIntegration
{
    public static async Task RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        using var timeout = new CancellationTokenSource(30000);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var sourceCert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var cert = X509CertificateLoader.LoadPkcs12(sourceCert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
        var media = new ConcurrentDictionary<uint, Media>();
        int offers = 0, stops = 0;
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> logs = [];
        var server = Task.Run(async () =>
        {
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
                    using var ssl = new SslStream(tcp.GetStream());
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, timeout.Token);
                    while (!timeout.IsCancellationRequested)
                    {
                        var wire = await CastWire.ReadAsync(ssl, timeout.Token);
                        using var document = JsonDocument.Parse(wire.Payload); var json = document.RootElement;
                        string? type = json.GetProperty("type").GetString();
                        int id = json.TryGetProperty("requestId", out var value) ? value.GetInt32() : 0;
                        object? reply = null;
                        if (type is "GET_STATUS" or "LAUNCH") reply = new { type = "RECEIVER_STATUS", requestId = id, status = new { applications = new[] { new { appId = CastClient.MirroringAppId, transportId = "local-transport", sessionId = "local-session" } } } };
                        if (type == "PING") reply = new { type = "PONG" };
                        if (type == "OFFER")
                        {
                            int generation = Interlocked.Increment(ref offers);
                            var streams = json.GetProperty("offer").GetProperty("supportedStreams").EnumerateArray().ToArray();
                            foreach (var stream in streams)
                            {
                                uint ssrc = stream.GetProperty("ssrc").GetUInt32();
                                var state = new Media(generation, stream.GetProperty("type").GetString() == "video_source",
                                    Convert.FromHexString(stream.GetProperty("aesKey").GetString()!), Convert.FromHexString(stream.GetProperty("aesIvMask").GetString()!), directory);
                                if (!media.TryAdd(ssrc, state)) throw new Exception("SSRC reused after reset");
                            }
                            reply = new { type = "ANSWER", seqNum = json.GetProperty("seqNum").GetInt32(), result = "ok", answer = new { udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port, sendIndexes = new[] { 0, 1 }, ssrcs = new[] { 200U, 201U } } };
                        }
                        if (type == "STOP") { Interlocked.Increment(ref stops); break; }
                        if (reply != null) await ssl.WriteAsync(CastWire.Encode(new(type == "OFFER" ? "local-transport" : "receiver-0", wire.Source, wire.Namespace, JsonSerializer.Serialize(reply))), timeout.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        var receiver = Task.Run(async () =>
        {
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var packet = await udp.ReceiveAsync(timeout.Token); byte[] bytes = packet.Buffer;
                    if (bytes.Length < 19 || RtcpSenderReport.IsRtcp(bytes)) continue;
                    uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(8));
                    if (!media.TryGetValue(ssrc, out var stream) || !stream.Accept(bytes)) continue;
                    byte[] feedback = NativeChecks.Feedback(ssrc, bytes[13]);
                    // Induce receiver-side latency without changing the network or TV.
                    if (stream.Video && stream.Generation == 1 && stream.Clock.Elapsed.TotalSeconds > 5)
                        BinaryPrimitives.WriteUInt16BigEndian(feedback.AsSpan(18), 2200);
                    await udp.SendAsync(feedback, packet.RemoteEndPoint, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        try
        {
            await using (var session = new StreamingSession())
            {
                session.Activity += text => { lock (logs) logs.Add(text); if (text.Contains("配信を再開")) resumed.TrySetResult(); };
                await session.StartAsync(new("loopback", "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port), new("pattern", 0, 0, 0, 0, 1280, 720), new Settings { Audio = true, FrameRate = 30, Height = 720 }, timeout.Token, true);
                await Task.WhenAny(resumed.Task, session.Failure, server, receiver).WaitAsync(timeout.Token);
                if (!resumed.Task.IsCompletedSuccessfully) throw new Exception("Local streaming failed before reset completed");
                await Task.Delay(2000, timeout.Token);
                if (session.Failure.IsCompleted || session.BufferResetCount != 1 || offers != 2 || stops != 1)
                    throw new Exception("Expected exactly one successful media-session reset");
            }
            await Task.Delay(100, timeout.Token);
            if (stops != 2 || media.Count != 4 || media.Values.Any(s => s.Frames < 20)) throw new Exception("Video/audio did not resume or cleanly stop");
            foreach (var stream in media.Values) stream.Flush();
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
            { success = true, offers, stops, streams = media.Values.Select(s => new { s.Generation, s.Video, s.Frames, s.FirstWasKeyFrame }), logs,
                physicalTelevisionTest = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Local real-encoder TLS/UDP A/V recovery passed; two OFFERs and STOPs, fresh streams, resumed media.");
        }
        finally
        {
            timeout.Cancel(); listener.Stop();
            await Task.WhenAll(server, receiver);
            foreach (var stream in media.Values) stream.Dispose();
        }
    }
    private sealed class Media : IDisposable
    {
        public readonly int Generation;
        public readonly bool Video;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public long Frames;
        public bool FirstWasKeyFrame;
        private readonly FrameCrypto crypto;
        private readonly FileStream? file;
        private readonly SortedDictionary<ushort, byte[]> parts = [];
        private long full = -1;
        public Media(int generation, bool video, byte[] key, byte[] mask, string directory)
        { Generation = generation; Video = video; crypto = new(key, mask); if (video) file = File.Create(Path.Combine(directory, $"video-{generation}.h264")); }
        public bool Accept(byte[] packet)
        {
            long id = full < 0 ? packet[13] : full + (byte)(packet[13] - full);
            if (id != full) { full = id; parts.Clear(); }
            parts[BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(14))] = packet[19..];
            if ((packet[1] & 128) == 0 || parts.Count != BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(16)) + 1) return false;
            byte[] plain = crypto.Decrypt((uint)full, parts.Values.SelectMany(p => p).ToArray());
            if (Video)
            {
                if (Frames == 0) { FirstWasKeyFrame = (packet[12] & 128) != 0; if (!FirstWasKeyFrame) throw new Exception("New stream did not start with IDR"); }
                file!.Write(plain);
            }
            Frames++; return true;
        }
        public void Flush() => file?.Flush();
        public void Dispose() { file?.Dispose(); crypto.Dispose(); }
    }
}
