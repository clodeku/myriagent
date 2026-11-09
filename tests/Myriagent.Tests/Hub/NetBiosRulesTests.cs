using System.Buffers.Binary;
using System.Net;
using System.Text;
using Myriagent.Hub;
using Xunit.Abstractions;

namespace Myriagent.Tests.Hub;

/// <summary>網路芳鄰找電腦的 NetBIOS 節點狀態查詢（NetBiosRules）：封包的組法、回覆的解析、要查的位址</summary>
public sealed class NetBiosRulesTests(ITestOutputHelper output)
{
    [Fact]
    public void 查詢封包_名稱是星號的編碼_類型是NBSTAT()
    {
        var packet = NetBiosRules.NodeStatusQuery(0x1234);
        packet.Length.Should().Be(50);
        BinaryPrimitives.ReadUInt16BigEndian(packet).Should().Be(0x1234);
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4)).Should().Be(1, "QDCOUNT");
        packet[12].Should().Be(0x20);
        Encoding.ASCII.GetString(packet, 13, 32).Should().Be("CK" + new string('A', 30));
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(46)).Should().Be(0x21);
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(48)).Should().Be(1);
    }

    /// <summary>組一則回覆：名稱表依序是（名稱、後綴、是不是群組）</summary>
    private static byte[] Reply(params (string Name, byte Suffix, bool Group)[] names)
    {
        var query = NetBiosRules.NodeStatusQuery(7);
        var body = new List<byte>();
        body.AddRange(query.AsSpan(12, 34).ToArray());   // 名稱
        body.AddRange([0x00, 0x21, 0x00, 0x01, 0, 0, 0, 0]);   // TYPE、CLASS、TTL
        var rdata = new List<byte> { (byte)names.Length };
        foreach (var (name, suffix, group) in names)
        {
            rdata.AddRange(Encoding.ASCII.GetBytes(name.PadRight(15)));
            rdata.Add(suffix);
            rdata.AddRange(group ? new byte[] { 0x84, 0x00 } : new byte[] { 0x04, 0x00 });
        }

        rdata.AddRange(new byte[46]);   // 統計
        body.Add((byte)(rdata.Count >> 8));
        body.Add((byte)rdata.Count);
        body.AddRange(rdata);
        var header = new byte[] { 0, 7, 0x84, 0x00, 0, 0, 0, 1, 0, 0, 0, 0 };
        return [.. header, .. body];
    }

    [Fact]
    public void 解析回覆_有檔案分享服務才有伺服器名稱_群組名稱略過()
    {
        var nas = NetBiosRules.ParseNodeStatus(Reply(("NAS", 0x00, false), ("WORKGROUP", 0x00, true), ("NAS", 0x20, false)));
        nas.Should().Be(new NetBiosNodeStatus("NAS", "NAS"));
        var noShare = NetBiosRules.ParseNodeStatus(Reply(("LAPTOP", 0x00, false), ("WORKGROUP", 0x1E, true)));
        noShare.Should().Be(new NetBiosNodeStatus(null, "LAPTOP"));
        NetBiosRules.ParseNodeStatus(new byte[5]).Should().BeNull();
        NetBiosRules.ParseNodeStatus(NetBiosRules.NodeStatusQuery(1)).Should().BeNull("查詢不是回覆");
    }

    [Fact]
    public void 要查的位址_私人網段的主機_大網段只查所在的24_不含自己()
    {
        var small = NetBiosRules.SweepTargets([(IPAddress.Parse("192.168.88.88"), 24)]);
        small.Count.Should().Be(253);
        small.Should().NotContain(IPAddress.Parse("192.168.88.88")).And.NotContain(IPAddress.Parse("192.168.88.0")).And.NotContain(IPAddress.Parse("192.168.88.255"));
        NetBiosRules.SweepTargets([(IPAddress.Parse("10.1.2.3"), 8)]).Should().HaveCount(253).And.OnlyContain(ip => ip.ToString().StartsWith("10.1.2.", StringComparison.Ordinal));
        NetBiosRules.SweepTargets([(IPAddress.Parse("172.31.128.1"), 22)]).Should().HaveCount(1021);
        NetBiosRules.SweepTargets([(IPAddress.Parse("8.8.8.8"), 24), (IPAddress.Parse("169.254.3.4"), 16)]).Should().BeEmpty("網際網路與自動配置的網段不查");
        NetBiosRules.SweepTargets([(IPAddress.Parse("192.168.1.5"), 24), (IPAddress.Parse("192.168.1.6"), 24)]).Should().HaveCount(252, "同一個網段不重複");
    }

    [Fact]
    public void 掃描的網卡_有閘道的實體網卡優先_虛擬網卡只在沒有實體網卡時才掃()
    {
        var lan = new DiscoveryAdapter(IPAddress.Parse("192.168.1.20"), 24, true, "乙太網路", "Realtek PCIe GbE Family Controller");
        var wifi = new DiscoveryAdapter(IPAddress.Parse("192.168.50.3"), 24, false, "Wi-Fi", "Intel(R) Wi-Fi 6E AX211");
        var hyperv = new DiscoveryAdapter(IPAddress.Parse("172.28.16.1"), 20, false, "vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter");
        var wsl = new DiscoveryAdapter(IPAddress.Parse("172.20.0.1"), 20, false, "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter #2");
        var vpn = new DiscoveryAdapter(IPAddress.Parse("10.8.0.2"), 24, true, "Tailscale", "Tailscale Tunnel");

        NetBiosRules.SweepAdapters([hyperv, wsl, wifi, lan, vpn]).Should().Equal((lan.Address, 24), (wifi.Address, 24));
        NetBiosRules.SweepAdapters([hyperv, wsl]).Should().HaveCount(2, "沒有實體網卡時照樣掃虛擬的");
        NetBiosRules.IsVirtualAdapter("vEthernet (Default Switch)", null).Should().BeTrue();
        NetBiosRules.IsVirtualAdapter("乙太網路", "Intel(R) Ethernet Connection").Should().BeFalse();
    }

    [Fact]
    public void 掃描摘要與找不到電腦的說明()
    {
        NetBiosRules.SweepSummary([(IPAddress.Parse("192.168.1.20"), 24), (IPAddress.Parse("10.1.2.3"), 8)], 506, 0)
            .Should().Be("掃描了 192.168.1.0/24、10.1.2.0/24（506 個位址），找到 0 台");
        NetBiosRules.SweepSummary([(IPAddress.Parse("8.8.8.8"), 24)], 0, 0).Should().Be("這台沒有區域網路的網卡可以掃描");
        NetworkPathRules.NoComputersMessage("掃描了 192.168.1.0/24（254 個位址），找到 0 台")
            .Should().StartWith("沒有找到網路上的電腦（掃描了 192.168.1.0/24").And.Contain("IP 位址");
        NetworkPathRules.NoComputersMessage(null).Should().StartWith("沒有找到網路上的電腦：");
    }

    /// <summary>真的在這台的區域網路找（MYRIAGENT_NET_IT=1 才跑；只送查詢、不連任何分享）</summary>
    [Fact]
    public async Task 真的找區域網路上的電腦()
    {
        if (Environment.GetEnvironmentVariable("MYRIAGENT_NET_IT") != "1")
            return;
        var started = DateTime.UtcNow;
        var found = await NetworkDiscovery.FindAsync(CancellationToken.None);
        output.WriteLine($"{(DateTime.UtcNow - started).TotalMilliseconds:0} ms：" + string.Join("、", found) + "；" + NetworkDiscovery.LastSummary);
        var listed = DateTime.UtcNow;
        var computers = new NetworkShares().Computers();
        output.WriteLine($"整個「網路」這一層 {(DateTime.UtcNow - listed).TotalMilliseconds:0} ms：" + string.Join("、", computers));
        (DateTime.UtcNow - listed).Should().BeLessThan(NetworkPathGuard.DefaultTimeout, "不能讓整個「網路」逾時");
        found.Should().NotContain(n => string.Equals(n, Environment.MachineName, StringComparison.OrdinalIgnoreCase));
    }
}
