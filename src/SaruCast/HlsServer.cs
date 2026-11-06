using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SaruCast;

public sealed partial class HlsServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly string root;
    private readonly IPAddress peer;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim slots = new(12, 12);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> clients = new();
    private readonly Task accept;
    private long requests, playlists;
    public long SegmentRequests => Interlocked.Read(ref requests);
    public long PlaylistRequests => Interlocked.Read(ref playlists);
    public string Url => $"http://{((IPEndPoint)listener.LocalEndpoint).Address}:{((IPEndPoint)listener.LocalEndpoint).Port}/{token}/master.m3u8";
    [GeneratedRegex(@"^seg[0-9]{6,12}\.ts$", RegexOptions.CultureInvariant)] private static partial Regex SegmentName();
    public HlsServer(IPAddress local, IPAddress television, string directory)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException();
        root = directory; peer = television; listener = new(local, 0); listener.Start(12); accept = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                if (!((IPEndPoint)tcp.Client.RemoteEndPoint!).Address.Equals(peer) || !slots.Wait(0)) { tcp.Dispose(); continue; }
                Task task = ServeAsync(tcp); clients[task] = 0;
                _ = task.ContinueWith(t => { slots.Release(); clients.TryRemove(t, out _); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { } catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    private async Task ServeAsync(TcpClient tcp)
    {
        using (tcp)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(8)); var ct = timeout.Token;
            try
            {
                var stream = tcp.GetStream(); byte[] header = new byte[8192]; int count = 0;
                while (count < header.Length)
                {
                    int n = await stream.ReadAsync(header.AsMemory(count, 1), ct); if (n == 0) return; count += n;
                    if (count >= 4 && header[count - 4] == 13 && header[count - 3] == 10 && header[count - 2] == 13 && header[count - 1] == 10) break;
                }
                if (count == header.Length) { await Reply(stream, 431, "text/plain", [], false, ct); return; }
                string first = Encoding.ASCII.GetString(header, 0, count).Split("\r\n")[0]; string[] parts = first.Split(' ');
                if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.")) { await Reply(stream, 400, "text/plain", [], false, ct); return; }
                string method = parts[0], path = parts[1].Split('?')[0];
                string prefix = "/" + token + "/";
                string name = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : "";
                bool playlist = name is "index.m3u8" or "master.m3u8";
                if (!playlist && !SegmentName().IsMatch(name)) { await Reply(stream, 404, "text/plain", [], false, ct); return; }
                if (method == "OPTIONS") { await Reply(stream, 204, "text/plain", [], false, ct); return; }
                if (method is not ("GET" or "HEAD")) { await Reply(stream, 405, "text/plain", [], false, ct); return; }
                byte[] bytes;
                try
                {
                    using var file = new FileStream(Path.Combine(root, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (file.Length > 32 * 1024 * 1024) throw new IOException();
                    bytes = new byte[(int)file.Length]; await file.ReadExactlyAsync(bytes, ct);
                }
                catch (IOException) { await Reply(stream, 404, "text/plain", [], false, ct); return; }
                if (!playlist && method == "GET") Interlocked.Increment(ref requests);
                if (playlist && method == "GET") Interlocked.Increment(ref playlists);
                await Reply(stream, 200, playlist ? "application/vnd.apple.mpegurl" : "video/mp2t", bytes, method == "HEAD", ct);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or SocketException) { }
        }
    }
    private static async Task Reply(Stream s, int code, string mime, byte[] body, bool head, CancellationToken ct)
    {
        string reason = code switch { 200 => "OK", 204 => "No Content", 404 => "Not Found", 405 => "Method Not Allowed", 431 => "Request Header Fields Too Large", _ => "Bad Request" };
        string header = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {mime}\r\nContent-Length: {body.Length}\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, HEAD, OPTIONS\r\nAccess-Control-Allow-Headers: Range, Content-Type\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(header), ct); if (!head) await s.WriteAsync(body, ct);
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); await accept;
        while (!clients.IsEmpty) { await Task.WhenAll(clients.Keys.ToArray()); await Task.Yield(); }
        Log.Write($"HTTP closed playlists={PlaylistRequests} segments={SegmentRequests}");
        stop.Dispose(); slots.Dispose();
    }
}
