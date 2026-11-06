using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SaruCast;

public sealed record DnsRecord(string Name, int Type, byte[] Data, string? Target = null, int Port = 0, uint Ttl = 120);
public static class Mdns
{
    public const string Service = "_googlecast._tcp.local";
    public static byte[] Query(string name, int type = 12)
    {
        using var ms = new MemoryStream(); ms.Write(new byte[12]);
        foreach (string label in name.TrimEnd('.').Split('.'))
        { byte[] bytes = Encoding.UTF8.GetBytes(label); if (bytes.Length is < 1 or > 63) throw new ArgumentException(); ms.WriteByte((byte)bytes.Length); ms.Write(bytes); }
        ms.WriteByte(0); ms.WriteByte((byte)(type >> 8)); ms.WriteByte((byte)type); ms.Write([0, 1]);
        var result = ms.ToArray(); result[5] = 1; return result;
    }
    private static string Name(byte[] data, ref int pos)
    {
        var labels = new List<string>(); int at = pos, end = -1, hops = 0, total = 0;
        while (true)
        {
            if (at >= data.Length || ++hops > 128) throw new InvalidDataException();
            int len = data[at++];
            if (len == 0) { pos = end < 0 ? at : end; return string.Join('.', labels); }
            if ((len & 0xc0) == 0xc0)
            {
                if (at >= data.Length) throw new InvalidDataException();
                int ptr = ((len & 63) << 8) | data[at++]; if (end < 0) end = at; at = ptr; continue;
            }
            if (len > 63 || at + len > data.Length || (total += len + 1) > 255) throw new InvalidDataException();
            labels.Add(Encoding.UTF8.GetString(data, at, len)); at += len;
        }
    }
    public static IReadOnlyList<DnsRecord> Parse(byte[] data)
    {
        if (data.Length < 12) throw new InvalidDataException();
        int q = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4)), n = 0;
        for (int i = 6; i <= 10; i += 2) n += BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(i));
        if (q > 100 || n > 500) throw new InvalidDataException();
        int pos = 12; var result = new List<DnsRecord>();
        for (int i = 0; i < q; i++) { Name(data, ref pos); pos += 4; if (pos > data.Length) throw new InvalidDataException(); }
        for (int i = 0; i < n; i++)
        {
            string name = Name(data, ref pos); if (pos + 10 > data.Length) throw new InvalidDataException();
            int type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
            int cls = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2)) & 0x7fff;
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 4));
            int len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 8)); pos += 10;
            int end = pos + len; if (end > data.Length) throw new InvalidDataException();
            string? target = null; int port = 0;
            if (type == 12) { int p = pos; target = Name(data, ref p); if (p > end) throw new InvalidDataException(); }
            if (type == 33)
            { if (len < 7) throw new InvalidDataException(); port = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 4)); int p = pos + 6; target = Name(data, ref p); if (p > end) throw new InvalidDataException(); }
            if (cls == 1) result.Add(new(name, type, data.AsSpan(pos, len).ToArray(), target, port, ttl)); pos = end;
        }
        return result;
    }
    public static IReadOnlyList<CastDevice> Resolve(IEnumerable<DnsRecord> source)
    {
        var records = source.Where(r => r.Ttl > 0).ToArray(); var result = new List<CastDevice>();
        foreach (var ptr in records.Where(r => r.Type == 12 && r.Name.Equals(Service, StringComparison.OrdinalIgnoreCase)))
        {
            var srv = records.LastOrDefault(r => r.Type == 33 && r.Name.Equals(ptr.Target, StringComparison.OrdinalIgnoreCase));
            if (srv?.Target == null || srv.Port == 0) continue;
            var addr = records.LastOrDefault(r => r.Type == 1 && r.Name.Equals(srv.Target, StringComparison.OrdinalIgnoreCase) && r.Data.Length == 4);
            if (addr == null) continue;
            string friendly = ptr.Target?.Split('.')[0] ?? "Google Cast";
            var txt = records.LastOrDefault(r => r.Type == 16 && r.Name.Equals(ptr.Target, StringComparison.OrdinalIgnoreCase));
            if (txt != null)
            {
                for (int i = 0; i < txt.Data.Length;)
                { int len = txt.Data[i++]; if (i + len > txt.Data.Length) break; string s = Encoding.UTF8.GetString(txt.Data, i, len); i += len; if (s.StartsWith("fn=")) friendly = s[3..]; }
            }
            result.Add(new(friendly, new IPAddress(addr.Data).ToString(), srv.Port));
        }
        return result.DistinctBy(d => (d.Address, d.Port)).OrderBy(d => d.Name).ToArray();
    }
    public static async Task<IReadOnlyList<CastDevice>> DiscoverAsync(CancellationToken ct, int milliseconds = 6500)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(milliseconds);
        var records = new List<DnsRecord>(); object gate = new();
        var addresses = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address).Distinct();
        await Task.WhenAll(addresses.Select(async addr =>
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(addr, 0));
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                var endpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
                var asked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                async Task Ask(string name, int type)
                { if (asked.Count < 128 && asked.Add(name + "/" + type)) await udp.SendAsync(Query(name, type), endpoint, stop.Token); }
                await Ask(Service, 12);
                while (!stop.IsCancellationRequested)
                {
                    var packet = await udp.ReceiveAsync(stop.Token); IReadOnlyList<DnsRecord> added;
                    try { added = Parse(packet.Buffer); } catch (InvalidDataException) { continue; }
                    lock (gate) { if (records.Count < 5000) records.AddRange(added); }
                    foreach (var r in added)
                    {
                        if (r.Type == 12 && r.Name.Equals(Service, StringComparison.OrdinalIgnoreCase) && r.Target != null) { await Ask(r.Target, 33); await Ask(r.Target, 16); }
                        if (r.Type == 33 && r.Name.EndsWith("." + Service, StringComparison.OrdinalIgnoreCase) && r.Target != null) await Ask(r.Target, 1);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException e) { Log.Write($"mDNS NIC error {e.SocketErrorCode}"); }
        }));
        ct.ThrowIfCancellationRequested(); return Resolve(records);
    }
}
