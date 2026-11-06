using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SaruCast;
using Encoder = SaruCast.Encoder;

string testData = Path.Combine(Path.GetTempPath(), "SaruCast-test-data-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("SARUCAST_DATA_ROOT", testData);
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
var frame = new CastFrame("source", "destination", CastClient.Receiver, "{\"type\":\"日本語\"}");
Check(CastWire.Decode(CastWire.Encode(frame).AsSpan(4)) == frame, "Cast protobuf UTF-8 round trip");
Reject(() => CastWire.Decode([18, 127, 1]), "Reject truncated protobuf");
Reject(() => CastWire.Decode([255, 255, 255, 255, 255, 255]), "Reject overlong protobuf varint");
try { await CastWire.ReadAsync(new MemoryStream(new byte[] { 0x7f, 0xff, 0xff, 0xff }), default); throw new Exception(); } catch (InvalidDataException) { Check(true, "Reject oversized Cast frame"); }
Check(Encoder.Size(1280, 720, 1080) == (1280, 720), "No upscaling");
Check(Encoder.Size(1920, 1280, 1080) == (1620, 1080), "Preserve aspect ratio");
Check(Encoder.Size(3840, 2160, 720) == (1280, 720), "720p downscale");
var argsTest = Encoder.Arguments(new("display", 2, 1, -1920, 0, 1920, 1080), new(), "test path", null);
Check(argsTest.Contains("d3d11va=cap:2") && argsTest.Any(a => a.Contains("output_idx=1")), "DXGI adapter and output mapping");
Check(argsTest.Contains("delete_segments+independent_segments+temp_file+omit_endlist"), "Live HLS rolling lifecycle");
Check(Mdns.Query(Mdns.Service)[5] == 1, "DNS query question count");
Reject(() => Mdns.Parse([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xc0, 12, 0, 12, 0, 1]), "Reject DNS compression cycle");
var records = new[] { new DnsRecord(Mdns.Service, 12, [], "tv." + Mdns.Service), new DnsRecord("tv." + Mdns.Service, 33, [], "tv.local", 8009), new DnsRecord("tv.local", 1, [192,168,1,5]), new DnsRecord("tv." + Mdns.Service, 16, [6,102,110,61,84,86,49]) };
Check(Mdns.Resolve(records).Single() == new CastDevice("TV1", "192.168.1.5"), "DNS-SD resolve friendly name and endpoint");
Check(Mdns.Resolve(records.Select(r => r with { Ttl = 0 })).Count == 0, "DNS goodbye ignored");

string dir = Path.Combine(Path.GetTempPath(), "SaruCast-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
try
{
    File.WriteAllText(Path.Combine(dir, "index.m3u8"), "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nseg000001.ts\n");
    File.WriteAllText(Path.Combine(dir, "master.m3u8"), "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=3000000\nindex.m3u8\n");
    File.WriteAllBytes(Path.Combine(dir, "seg000001.ts"), [0x47, 0x40, 0, 1]);
    await using (var http = new HlsServer(IPAddress.Loopback, IPAddress.Loopback, dir))
    {
        using var client = new HttpClient();
        var response = await client.GetAsync(http.Url);
        Check(response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/vnd.apple.mpegurl" && response.Headers.GetValues("Access-Control-Allow-Origin").Single() == "*", "HLS MIME and CORS");
        string segmentUrl = http.Url.Replace("master.m3u8", "seg000001.ts");
        Check((await client.GetByteArrayAsync(segmentUrl)).Length == 4 && http.SegmentRequests == 1, "TV segment request accounting");
        Check((await client.GetAsync(http.Url.Replace("master.m3u8", "settings.json"))).StatusCode == HttpStatusCode.NotFound, "HTTP file allowlist");
        var uri = new Uri(http.Url);
        Check((await client.GetAsync($"http://127.0.0.1:{uri.Port}/incorrect/index.m3u8")).StatusCode == HttpStatusCode.NotFound, "Reject incorrect session secret");
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, http.Url));
        Check(head.IsSuccessStatusCode && (await head.Content.ReadAsByteArrayAsync()).Length == 0, "HTTP HEAD");
        using var options = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, http.Url)); Check(options.StatusCode == HttpStatusCode.NoContent, "HTTP OPTIONS");
        using var post = await client.PostAsync(http.Url, new StringContent("")); Check(post.StatusCode == HttpStatusCode.MethodNotAllowed, "Reject unsupported method");
        await using (var mock = new MockReceiver())
        {
            await using (var cast = new CastClient())
            {
                using var timeout = new CancellationTokenSource(15000);
                await cast.ConnectAsync(new("mock", "127.0.0.1", mock.Port), timeout.Token);
                Check(cast.LocalAddress.AddressFamily == AddressFamily.InterNetwork, "Cast route exposes IPv4 for TV HTTP server");
                await cast.LaunchAndLoadAsync(http.Url, timeout.Token);
                Check(cast.PlayerState == "PLAYING" && http.SegmentRequests == 2, "Mock TLS Cast CONNECT / LAUNCH / LOAD / HLS fetch / PLAYING");
            }
            Check(await mock.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(2)), "STOP targets launched session");
        }
        await using (var mock = new MockReceiver(rejectLoad: true))
        await using (var cast = new CastClient())
        {
            using var timeout = new CancellationTokenSource(15000);
            await cast.ConnectAsync(new("mock", "127.0.0.1", mock.Port), timeout.Token);
            bool rejected = false;
            try { await cast.LaunchAndLoadAsync(http.Url, timeout.Token); } catch (IOException) { rejected = true; }
            Check(rejected, "Receiver LOAD_FAILED is never success");
        }
    }
    var closed = new HlsServer(IPAddress.Loopback, IPAddress.Loopback, dir); string closedUrl = closed.Url; await closed.DisposeAsync();
    using var after = new HttpClient { Timeout = TimeSpan.FromSeconds(1) }; bool refused = false;
    try { await after.GetAsync(closedUrl); } catch (HttpRequestException) { refused = true; } catch (OperationCanceledException) { refused = true; }
    Check(refused, "Stop closes HTTP endpoint");
}
finally { Directory.Delete(dir, true); }
await ClosingChecks.RunAsync(false); Check(true, "WPF close before any connection completes without reentrant Close");
await ClosingChecks.RunAsync(true); Check(true, "WPF repeated close requests share shutdown completion");
var neverStarted = new CastSession();
await neverStarted.DisposeAsync(); await neverStarted.DisposeAsync();
Check(true, "Session cleanup can be awaited repeatedly");
Directory.Delete(testData, true);
Console.WriteLine($"{passed} checks passed. Mock receiver is not a real television test.");

sealed class MockReceiver : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task run;
    private readonly bool rejectLoad;
    public TaskCompletionSource<bool> Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public MockReceiver(bool rejectLoad = false) { this.rejectLoad = rejectLoad; listener.Start(); run = RunAsync(); }
    private async Task RunAsync()
    {
        try
        {
            using var rsa = RSA.Create(2048); var req = new CertificateRequest("CN=localhost", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var schannelCert = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
            using var tcp = await listener.AcceptTcpClientAsync(stop.Token); using var ssl = new SslStream(tcp.GetStream());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = schannelCert }, stop.Token);
            while (!stop.IsCancellationRequested)
            {
                var frame = await CastWire.ReadAsync(ssl, stop.Token); using var doc = JsonDocument.Parse(frame.Payload); var root = doc.RootElement;
                string? type = root.GetProperty("type").GetString(); int id = root.TryGetProperty("requestId", out var rid) ? rid.GetInt32() : 0;
                object? reply = null;
                if (type is "GET_STATUS" or "LAUNCH") reply = new { type = "RECEIVER_STATUS", requestId = id, status = new { applications = new[] { new { appId = CastClient.AppId, transportId = "transport-test", sessionId = "session-test" } } } };
                if (type == "PING") reply = new { type = "PONG" };
                if (type == "LOAD")
                {
                    var media = root.GetProperty("media");
                    if (media.GetProperty("hlsSegmentFormat").GetString() != "ts" || media.GetProperty("hlsVideoSegmentFormat").GetString() != "mpeg2_ts") throw new Exception("Incorrect HLS wire enum values");
                    await ssl.WriteAsync(CastWire.Encode(new(frame.Destination, frame.Source, frame.Namespace, JsonSerializer.Serialize(new { type = "MEDIA_STATUS", status = new[] { new { mediaSessionId = 1, playerState = "IDLE", media = new { contentId = media.GetProperty("contentId").GetString() } } } }))), stop.Token);
                    await ssl.WriteAsync(CastWire.Encode(new(frame.Destination, frame.Source, frame.Namespace, "{\"type\":\"MEDIA_STATUS\",\"status\":[{\"mediaSessionId\":99,\"playerState\":\"PLAYING\",\"media\":{\"contentId\":\"previous-content\"}}]}")), stop.Token);
                    if (rejectLoad) reply = new { type = "LOAD_FAILED", requestId = id };
                    else
                    {
                        string url = root.GetProperty("media").GetProperty("contentId").GetString()!;
                        using var http = new HttpClient(); await http.GetStringAsync(url); await http.GetStringAsync(url.Replace("master.m3u8", "index.m3u8")); await http.GetByteArrayAsync(url.Replace("master.m3u8", "seg000001.ts"));
                        reply = new { type = "MEDIA_STATUS", requestId = id, status = new[] { new { mediaSessionId = 1, playerState = "PLAYING", media = new { contentId = url } } } };
                    }
                }
                if (type == "STOP") { Stopped.TrySetResult(root.GetProperty("sessionId").GetString() == "session-test"); return; }
                if (reply != null)
                {
                    byte[] encoded = CastWire.Encode(new(frame.Destination, frame.Source, frame.Namespace, JsonSerializer.Serialize(reply)));
                    // Exercise partial TCP/TLS reads instead of relying on packet boundaries.
                    for (int i = 0; i < encoded.Length; i += 3) await ssl.WriteAsync(encoded.AsMemory(i, Math.Min(3, encoded.Length - i)), stop.Token);
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await run; stop.Dispose(); }
}
