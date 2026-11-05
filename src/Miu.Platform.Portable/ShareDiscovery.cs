using System.Buffers.Binary;
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

    /// <param name="sweep">Also try every address on your local network for file sharing (port 445). Finds machines
    /// that don't announce themselves, like a Raspberry Pi running Samba.</param>
    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(TimeSpan listen, CancellationToken ct, bool sweep = true)
    {
        var found = new Dictionary<string, DiscoveredDevice>();
        void Add(DiscoveredDevice d)
        {
            lock (found)
                if (!found.TryGetValue(d.Address, out var old) || old.Via != "Bonjour" && d.Via == "Bonjour") found[d.Address] = d;
        }
        var swept = sweep ? SweepAsync(LocalNetworks(), TimeSpan.FromMilliseconds(600), ct) : Task.FromResult<IReadOnlyList<DiscoveredDevice>>([]);
        await Task.WhenAll(
            ListenAsync(MdnsGroup, BuildMdnsQuery(), (bytes, from) => { foreach (var d in ParseMdns(bytes, from)) Add(d); }, listen, ct),
            ListenAsync(WsdGroup, Encoding.UTF8.GetBytes(BuildWsdProbe(Guid.NewGuid())),
                (bytes, from) => { foreach (var d in ParseWsd(Encoding.UTF8.GetString(bytes), from)) Add(d); }, listen, ct));
        foreach (var d in await swept)
            lock (found)
                found.TryAdd(d.Address, d); // a device that also announced itself keeps its announced name
        return found.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- Network sweep (port 445) ----------

    /// <summary>This computer's own IPv4 networks, private ranges only (never sweeps the internet).</summary>
    public static IReadOnlyList<(IPAddress Address, int Prefix)> LocalNetworks()
    {
        var list = new List<(IPAddress, int)>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up
                    || ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(ua.Address))
                        list.Add((ua.Address, ua.PrefixLength));
            }
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException) { }
        return list;
    }

    public static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>
    /// The other addresses on a network. Big networks are narrowed to the 256 addresses around this computer, so a
    /// sweep stays quick and polite.
    /// </summary>
    public static IEnumerable<IPAddress> Hosts(IPAddress self, int prefix)
    {
        prefix = Math.Clamp(prefix, 24, 30);
        var ip = BinaryPrimitives.ReadUInt32BigEndian(self.GetAddressBytes());
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = ip & mask;
        var broadcast = network | ~mask;
        for (var h = network + 1; h < broadcast; h++)
        {
            if (h == ip) continue;
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, h);
            yield return new IPAddress(bytes);
        }
    }

    /// <summary>Tries port 445 on every address (64 at a time), then looks up names for the ones that answer.</summary>
    public static async Task<IReadOnlyList<DiscoveredDevice>> SweepAsync(IEnumerable<(IPAddress Address, int Prefix)> networks, TimeSpan timeout,
        CancellationToken ct, Func<IPAddress, TimeSpan, CancellationToken, Task<bool>>? probe = null, Func<IPAddress, Task<string?>>? name = null)
    {
        probe ??= HasFileSharingAsync;
        name ??= NameOfAsync;
        var targets = networks.SelectMany(n => Hosts(n.Address, n.Prefix)).Distinct().ToList();
        var open = new System.Collections.Concurrent.ConcurrentBag<IPAddress>();
        using var gate = new SemaphoreSlim(64);
        await Task.WhenAll(targets.Select(async a =>
        {
            await gate.WaitAsync(ct);
            try { if (await probe(a, timeout, ct)) open.Add(a); }
            catch (Exception ex) when (ex is SocketException or IOException) { }
            finally { gate.Release(); }
        }));
        var found = await Task.WhenAll(open.Select(async a =>
            new DiscoveredDevice(await name(a) ?? a.ToString(), a.ToString(), "File sharing (SMB) answered on this address")));
        return found.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<bool> HasFileSharingAsync(IPAddress a, TimeSpan timeout, CancellationToken ct)
    {
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(timeout);
        try { await tcp.ConnectAsync(a, 445, window.Token); return tcp.Connected; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    /// <summary>The computer's name (DNS, or NetBIOS/LLMNR on Windows), first part only: "pi", not "pi.lan".</summary>
    private static async Task<string?> NameOfAsync(IPAddress a)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(a);
            if (await Task.WhenAny(lookup, Task.Delay(1500)) != lookup) return null;
            var host = (await lookup).HostName;
            return string.IsNullOrEmpty(host) || host == a.ToString() ? null : host.Split('.')[0];
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException) { return null; }
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
