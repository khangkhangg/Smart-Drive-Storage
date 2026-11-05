using System.Net;
using System.Text;
using Miu.Platform.Portable;

namespace Miu.Platform.Tests;

public class ShareDiscoveryTests
{
    [Fact]
    public void Mdns_query_asks_for_smb_services()
    {
        var q = ShareDiscovery.BuildMdnsQuery();
        q[5].Should().Be(1); // one question
        Encoding.ASCII.GetString(q, 12, q.Length - 12).Should().Contain("_smb").And.Contain("_tcp").And.Contain("local");
        q[^4..].Should().Equal(0, 12, 0, 1); // PTR, IN
    }

    [Fact]
    public void Mdns_reply_with_ptr_srv_and_address_gives_name_and_ip()
    {
        var p = new List<byte> { 0, 0, 0x84, 0, 0, 0, 0, 3, 0, 0, 0, 0 }; // response, 3 answers
        // PTR _smb._tcp.local -> "Family NAS._smb._tcp.local" (compressed to offset 12)
        Name(p, "_smb._tcp.local");
        p.AddRange([0, 12, 0, 1, 0, 0, 0, 120]);
        var rdata = new List<byte>();
        rdata.Add(10); rdata.AddRange(Encoding.ASCII.GetBytes("Family NAS")); rdata.AddRange([0xC0, 12]);
        p.AddRange([0, (byte)rdata.Count]); p.AddRange(rdata);
        // SRV Family NAS._smb._tcp.local -> nas.local:445
        var instanceAt = p.Count;
        p.Add(10); p.AddRange(Encoding.ASCII.GetBytes("Family NAS")); p.AddRange([0xC0, 12]);
        p.AddRange([0, 33, 0, 1, 0, 0, 0, 120]);
        var srv = new List<byte> { 0, 0, 0, 0, 1, 0xBD };
        Name(srv, "nas.local");
        p.AddRange([0, (byte)srv.Count]); p.AddRange(srv);
        // A nas.local -> 192.168.1.20
        Name(p, "nas.local");
        p.AddRange([0, 1, 0x80, 1, 0, 0, 0, 120, 0, 4, 192, 168, 1, 20]);

        var d = ShareDiscovery.ParseMdns(p.ToArray(), IPAddress.Parse("192.168.1.99")).Single();
        d.Should().Be(new DiscoveredDevice("Family NAS", "192.168.1.20", "Bonjour"));
    }

    [Fact]
    public void Mdns_garbage_and_queries_are_ignored()
    {
        ShareDiscovery.ParseMdns(ShareDiscovery.BuildMdnsQuery(), IPAddress.Loopback).Should().BeEmpty();
        ShareDiscovery.ParseMdns([1, 2, 3], IPAddress.Loopback).Should().BeEmpty();
    }

    [Fact]
    public void Wsd_probe_match_for_a_computer()
    {
        ShareDiscovery.BuildWsdProbe(Guid.Empty).Should().Contain("Probe").And.Contain("pub:Computer");
        var xml = """
            <soap:Envelope xmlns:soap="http://www.w3.org/2003/05/soap-envelope" xmlns:wsd="http://schemas.xmlsoap.org/ws/2005/04/discovery">
              <soap:Body><wsd:ProbeMatches>
                <wsd:ProbeMatch><wsd:Types>wsdp:Device pub:Computer pub:KHANG-PC/Workgroup:WORKGROUP</wsd:Types><wsd:XAddrs>http://192.168.1.31:5357/abc</wsd:XAddrs></wsd:ProbeMatch>
                <wsd:ProbeMatch><wsd:Types>wsdp:Device wprt:PrintDeviceType</wsd:Types><wsd:XAddrs>http://192.168.1.40/</wsd:XAddrs></wsd:ProbeMatch>
              </wsd:ProbeMatches></soap:Body>
            </soap:Envelope>
            """;
        ShareDiscovery.ParseWsd(xml, IPAddress.Parse("192.168.1.31")).Should().Equal(new DiscoveredDevice("KHANG-PC", "192.168.1.31", "Windows network"));
        ShareDiscovery.ParseWsd("<not xml", IPAddress.Loopback).Should().BeEmpty();
    }

    private static void Name(List<byte> b, string name)
    {
        foreach (var l in name.Split('.')) { b.Add((byte)l.Length); b.AddRange(Encoding.ASCII.GetBytes(l)); }
        b.Add(0);
    }

    [Fact]
    public void Sweep_covers_the_local_network_but_never_more_than_256_addresses()
    {
        var hosts = ShareDiscovery.Hosts(IPAddress.Parse("192.168.1.20"), 24).ToList();
        hosts.Should().HaveCount(253); // .1 to .254, without this computer
        hosts.Should().NotContain(IPAddress.Parse("192.168.1.20")).And.Contain(IPAddress.Parse("192.168.1.1"));
        ShareDiscovery.Hosts(IPAddress.Parse("10.0.5.9"), 8).Should().HaveCount(253); // a /8 is narrowed to the /24 around us
        ShareDiscovery.IsPrivate(IPAddress.Parse("8.8.8.8")).Should().BeFalse();
        ShareDiscovery.IsPrivate(IPAddress.Parse("172.20.1.1")).Should().BeTrue();
    }

    [Fact]
    public async Task Sweep_lists_machines_with_file_sharing_by_name()
    {
        var found = await ShareDiscovery.SweepAsync([(IPAddress.Parse("192.168.1.20"), 24)], TimeSpan.FromMilliseconds(10), CancellationToken.None,
            probe: (a, _, _) => Task.FromResult(a.ToString() is "192.168.1.50" or "192.168.1.7"),
            name: a => Task.FromResult<string?>(a.ToString() == "192.168.1.50" ? "pi" : null));
        found.Select(d => (d.Name, d.Address)).Should().Equal(("192.168.1.7", "192.168.1.7"), ("pi", "192.168.1.50"));
    }
}
