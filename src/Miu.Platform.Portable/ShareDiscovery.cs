using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Miu.Platform.Portable;

/// <param name="Via">"Bonjour" (mDNS) or "Windows network" (WS-Discovery).</param>
public sealed record DiscoveredDevice(string Name, string Address, string Via);

/// <summary>
/// Finds computers and NAS boxes that share files on the local network: mDNS/Bonjour (<c>_smb._tcp.local</c>, used by
/// Macs, Linux and most NAS) and WS-Discovery (Windows PCs). Read-only: it only sends the standard query packets.
/// </summary>
public static class ShareDiscovery
{
    private const string SmbService = "_smb._tcp.local";
    private static readonly IPEndPoint MdnsGroup = new(IPAddress.Parse("224.0.0.251"), 5353);
    private static readonly IPEndPoint WsdGroup = new(IPAddress.Parse("239.255.255.250"), 3702);

    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan listen, CancellationToken ct)
    {
        var found = new Dictionary<string, DiscoveredDevice>();
        void Add(DiscoveredDevice d)
        {
            lock (found)
                if (!found.TryGetValue(d.Address, out var old) || old.Via != "Bonjour" && d.Via == "Bonjour") found[d.Address] = d;
        }
        await Task.WhenAll(
            ListenAsync(MdnsGroup, BuildMdnsQuery(), (bytes, from) => { foreach (var d in ParseMdns(bytes, from)) Add(d); }, listen, ct),
            ListenAsync(WsdGroup, Encoding.UTF8.GetBytes(BuildWsdProbe(Guid.NewGuid())),
                (bytes, from) => { foreach (var d in ParseWsd(Encoding.UTF8.GetString(bytes), from)) Add(d); }, listen, ct));
        return found.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task ListenAsync(IPEndPoint group, byte[] query, Action<byte[], IPAddress> onReply, TimeSpan listen, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            await udp.SendAsync(query, group, ct);
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(listen);
            while (true)
            {
                var r = await udp.ReceiveAsync(window.Token);
                try { onReply(r.Buffer, r.RemoteEndPoint.Address); } catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException or System.Xml.XmlException) { }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { } // the listening window ended
        catch (SocketException) { } // no network, or multicast blocked
    }

    // ---------- mDNS ----------

    public static byte[] BuildMdnsQuery()
    {
        var b = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }; // id 0, flags 0, 1 question
        WriteName(b, SmbService);
        b.AddRange([0, 12, 0, 1]); // PTR, IN
        return b.ToArray();
    }

    public static IEnumerable<DiscoveredDevice> ParseMdns(byte[] p, IPAddress from)
    {
        if (p.Length < 12 || (p[2] & 0x80) == 0) yield break; // must be a response
        int qd = U16(p, 4), an = U16(p, 6), ns = U16(p, 8), ar = U16(p, 10);
        var pos = 12;
        for (var i = 0; i < qd; i++) { ReadName(p, ref pos); pos += 4; }
        var instances = new List<string>();
        var srv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // instance -> host
        var addr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // host -> IPv4
        for (var i = 0; i < an + ns + ar; i++)
        {
            var name = ReadName(p, ref pos);
            int type = U16(p, pos), len = U16(p, pos + 8);
            var data = pos + 10;
            if (data + len > p.Length) yield break;
            switch (type)
            {
                case 12 when name.Equals(SmbService, StringComparison.OrdinalIgnoreCase):
                    var at = data;
                    instances.Add(ReadName(p, ref at));
                    break;
                case 33:
                    var t = data + 6;
                    srv[name] = ReadName(p, ref t);
                    break;
                case 1 when len == 4:
                    addr[name] = new IPAddress(p.AsSpan(data, 4)).ToString();
                    break;
            }
            pos = data + len;
        }
        foreach (var inst in instances.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var label = inst.EndsWith("." + SmbService, StringComparison.OrdinalIgnoreCase) ? inst[..^(SmbService.Length + 1)] : inst.Split('.')[0];
            var ip = srv.TryGetValue(inst, out var host) && addr.TryGetValue(host, out var a) ? a : from.ToString();
            yield return new DiscoveredDevice(label, ip, "Bonjour");
        }
    }

    private static void WriteName(List<byte> b, string name)
    {
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            b.Add((byte)bytes.Length);
            b.AddRange(bytes);
        }
        b.Add(0);
    }

    private static string ReadName(byte[] p, ref int pos)
    {
        var labels = new List<string>();
        var jumped = false;
        var at = pos;
        for (var hops = 0; hops < 64; hops++)
        {
            int len = p[at];
            if (len == 0) { at++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                var target = ((len & 0x3F) << 8) | p[at + 1];
                if (!jumped) pos = at + 2;
                jumped = true;
                at = target;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(p, at + 1, len));
            at += len + 1;
        }
        if (!jumped) pos = at;
        return string.Join('.', labels);
    }

    private static int U16(byte[] p, int at) => (p[at] << 8) | p[at + 1];

    // ---------- WS-Discovery ----------

    public static string BuildWsdProbe(Guid id) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope" xmlns:wsa="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:wsd="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:wsdp="http://schemas.xmlsoap.org/ws/2006/02/devprof" xmlns:pub="http://schemas.microsoft.com/windows/pub/2005/07">
          <soap:Header>
            <wsa:To>urn:schemas-xmlsoap-org:ws:2005:04:discovery</wsa:To>
            <wsa:Action>http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</wsa:Action>
            <wsa:MessageID>urn:uuid:{id}</wsa:MessageID>
          </soap:Header>
          <soap:Body><wsd:Probe><wsd:Types>wsdp:Device pub:Computer</wsd:Types></wsd:Probe></soap:Body>
        </soap:Envelope>
        """;

    public static IEnumerable<DiscoveredDevice> ParseWsd(string xml, IPAddress from)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { yield break; }
        foreach (var match in doc.Descendants().Where(e => e.Name.LocalName == "ProbeMatch"))
        {
            var types = match.Elements().FirstOrDefault(e => e.Name.LocalName == "Types")?.Value ?? "";
            if (!types.Contains("Computer", StringComparison.OrdinalIgnoreCase)) continue; // printers, scanners…
            var name = types.Split(' ').Select(t => t[(t.IndexOf(':') + 1)..]).FirstOrDefault(t => t.Contains('/')) is { } computer
                ? computer.Split('/')[0] // pub:Computer "NAME/Workgroup:WORKGROUP"
                : null;
            var xaddr = match.Elements().FirstOrDefault(e => e.Name.LocalName == "XAddrs")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var host = xaddr is not null && Uri.TryCreate(xaddr, UriKind.Absolute, out var u) ? u.Host : from.ToString();
            if (host.StartsWith('[')) host = from.ToString(); // prefer the IPv4 sender over a link-local IPv6
            yield return new DiscoveredDevice(name ?? host, host, "Windows network");
        }
    }
}
