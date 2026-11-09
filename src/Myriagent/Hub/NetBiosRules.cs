using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Myriagent.Hub;

/// <summary>NetBIOS 節點狀態查詢（NBSTAT，RFC 1002 4.2.17）的回覆：伺服器名稱（有檔案分享服務 &lt;20&gt; 的 unique 名稱）與工作站名稱（&lt;00&gt;）</summary>
internal sealed record NetBiosNodeStatus(string? ServerName, string? WorkstationName);

/// <summary>
/// 網路芳鄰找電腦（電腦狀態面板的檔案傳輸；2026-11-09 實機：舊式的網路芳鄰列舉在現在的 Windows 回 6118、什麼都拿不到）：
/// 對這台所在網段逐一送 NetBIOS 節點狀態查詢（UDP 137），回覆裡有檔案分享服務（&lt;20&gt;）的才算。這裡是封包的組法、解析與要查哪些位址。純函式
/// </summary>
internal static class NetBiosRules
{
    /// <summary>NetBIOS 名稱服務的埠</summary>
    public const int Port = 137;

    /// <summary>一次最多查幾個位址（大的網段只查這台所在的 /24）</summary>
    public const int MaxTargets = 1024;

    /// <summary>網段的前綴比這個小（範圍太大）時只查這台所在的 /24</summary>
    public const int MinSweepPrefix = 22;

    /// <summary>節點狀態查詢：名稱是「*」（RFC 1002 的萬用名稱），QTYPE NBSTAT（0x21）、QCLASS IN</summary>
    public static byte[] NodeStatusQuery(ushort transactionId)
    {
        var packet = new byte[50];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), transactionId);
        // 旗標 0、QDCOUNT 1，其他 0
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), 1);
        packet[12] = 0x20;
        var name = new byte[16];
        name[0] = (byte)'*';
        for (var i = 0; i < 16; i++)
        {
            packet[13 + i * 2] = (byte)('A' + (name[i] >> 4));
            packet[14 + i * 2] = (byte)('A' + (name[i] & 0x0F));
        }

        packet[45] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(46), 0x0021);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(48), 0x0001);
        return packet;
    }

    /// <summary>解析節點狀態的回覆（不合格回 null）：名稱表裡 unique 的 &lt;20&gt; 是伺服器名稱、&lt;00&gt; 是工作站名稱（去掉結尾的空白）</summary>
    public static NetBiosNodeStatus? ParseNodeStatus(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12 || (packet[2] & 0x80) == 0 || BinaryPrimitives.ReadUInt16BigEndian(packet[6..]) == 0)
            return null;
        var offset = 12;
        // 回覆的名稱（一般是 34 位元組的編碼名稱；也接受壓縮指標）
        while (offset < packet.Length)
        {
            var length = packet[offset];
            if (length == 0)
            {
                offset++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                offset += 2;
                break;
            }

            offset += length + 1;
        }

        // TYPE、CLASS、TTL、RDLENGTH
        if (offset + 10 > packet.Length || BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]) != 0x0021)
            return null;
        var rdLength = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
        offset += 10;
        if (offset + rdLength > packet.Length || rdLength < 1)
            return null;
        var count = packet[offset];
        offset++;
        string? server = null;
        string? workstation = null;
        for (var i = 0; i < count && offset + 18 <= packet.Length; i++, offset += 18)
        {
            var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 16)..]);
            if ((flags & 0x8000) != 0)
                continue;   // 群組名稱（工作群組、網域）
            var name = Encoding.ASCII.GetString(packet.Slice(offset, 15)).TrimEnd(' ', '\0');
            if (!NetworkPathRules.IsHostName(name))
                continue;
            switch (packet[offset + 15])
            {
                case 0x20:
                    server ??= name;
                    break;
                case 0x00:
                    workstation ??= name;
                    break;
            }
        }

        return new NetBiosNodeStatus(server, workstation);
    }

    /// <summary>
    /// 要掃描的網卡（2026-11-10 實機：「網路」一直逾時）：Hyper-V、WSL、VirtualBox、VMware、VPN 這類虛擬網卡的網段先被掃、
    /// 用掉位址的上限，真正的區域網路反而沒掃到。有 IPv4 預設閘道的實體網卡排最前面，其他實體網卡其次，虛擬網卡只在沒有實體網卡時才掃
    /// </summary>
    public static IReadOnlyList<(IPAddress Address, int PrefixLength)> SweepAdapters(IEnumerable<DiscoveryAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var list = adapters.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
        var physical = list.Where(a => !IsVirtualAdapter(a.Name, a.Description)).ToList();
        var chosen = physical.Count > 0 ? physical : list;
        return chosen.OrderByDescending(a => a.HasGateway).Select(a => (a.Address, a.PrefixLength)).ToList();
    }

    /// <summary>給使用者看的掃描摘要：「掃描了 192.168.1.0/24（254 個位址），找到 0 台」</summary>
    public static string SweepSummary(IReadOnlyList<(IPAddress Address, int PrefixLength)> adapters, int targets, int found)
    {
        var nets = adapters.Where(a => NetworkPathRules.IsLanAddress(a.Address) && a.Address.GetAddressBytes()[0] != 169 && a.PrefixLength is >= 1 and <= 30)
            .Select(a =>
            {
                var prefix = a.PrefixLength < MinSweepPrefix ? 24 : a.PrefixLength;
                var value = BinaryPrimitives.ReadUInt32BigEndian(a.Address.GetAddressBytes());
                var network = value & (uint.MaxValue << (32 - prefix));
                var buffer = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(buffer, network);
                return new IPAddress(buffer) + "/" + prefix;
            })
            .Distinct().ToList();
        return nets.Count == 0
            ? "這台沒有區域網路的網卡可以掃描"
            : "掃描了 " + string.Join("、", nets) + "（" + targets + " 個位址），找到 " + found + " 台";
    }

    private static readonly string[] VirtualMarkers =
        ["Hyper-V", "vEthernet", "WSL", "VirtualBox", "VMware", "Virtual Adapter", "Virtual Ethernet", "TAP-", "Tailscale", "ZeroTier", "WireGuard", "OpenVPN",
            "Npcap", "Loopback", "Bluetooth", "Docker"];

    /// <summary>虛擬網卡（名稱或描述含已知的虛擬化、VPN 字樣）</summary>
    public static bool IsVirtualAdapter(string? name, string? description)
        => VirtualMarkers.Any(m => (name ?? string.Empty).Contains(m, StringComparison.OrdinalIgnoreCase)
                                   || (description ?? string.Empty).Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 要查的位址：這台每個 IPv4 網段（私人網段、不是自動配置 169.254）裡的主機位址，前綴小於 <see cref="MinSweepPrefix"/> 的只查這台所在的 /24；
    /// 不含網路位址、廣播位址與這台自己；合計最多 <see cref="MaxTargets"/> 個
    /// </summary>
    public static IReadOnlyList<IPAddress> SweepTargets(IEnumerable<(IPAddress Address, int PrefixLength)> local)
    {
        ArgumentNullException.ThrowIfNull(local);
        var mine = local.Where(l => l.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
        var own = mine.Select(l => l.Address).ToHashSet();
        var result = new List<IPAddress>();
        var seen = new HashSet<uint>();
        foreach (var (address, prefixLength) in mine)
        {
            var bytes = address.GetAddressBytes();
            if (!NetworkPathRules.IsLanAddress(address) || bytes[0] == 169 || prefixLength is < 1 or > 30)
                continue;
            var prefix = prefixLength < MinSweepPrefix ? 24 : prefixLength;
            var value = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var network = value & mask;
            var broadcast = network | ~mask;
            for (var host = network + 1; host < broadcast && result.Count < MaxTargets; host++)
            {
                if (!seen.Add(host))
                    continue;
                var buffer = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(buffer, host);
                var ip = new IPAddress(buffer);
                if (!own.Contains(ip))
                    result.Add(ip);
            }
        }

        return result;
    }
}

/// <summary>一張網卡的 IPv4 位址（找網路上的電腦用）</summary>
internal sealed record DiscoveryAdapter(IPAddress Address, int PrefixLength, bool HasGateway, string? Name, string? Description);
