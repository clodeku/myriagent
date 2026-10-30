using System.Net.NetworkInformation;
using Myriagent.Security;

namespace Myriagent.Sync;

/// <summary>電腦狀態取樣（<see cref="DeviceStatsSampler"/>，桌面與伺服器共用）的純函式規則：哪些網卡算進流量、磁碟與 CPU 名稱怎麼整理</summary>
internal static class DeviceStatsSampleRules
{
    /// <summary>
    /// 描述裡有這些字的網卡不算（虛擬交換器、VPN、虛擬機的網卡）：主機的流量經過它們時實體網卡也算到一次，加進來會重複
    /// </summary>
    private static readonly string[] VirtualMarkers =
        ["Hyper-V", "Virtual", "VMware", "VirtualBox", "TAP-", "Wintun", "WireGuard", "Tailscale", "ZeroTier", "WAN Miniport", "Loopback", "Npcap", "Bluetooth"];

    /// <summary>
    /// 同一張網卡只算一次：.NET 的 NetworkInterface 把網卡的輕量篩選器（「…-WFP Native MAC Layer LightWeight Filter-0000」、「…-QoS Packet Scheduler-0000」）
    /// 各列成一張，型別、狀態與計數器都和本體一樣（2026-10-24 實測一張 Realtek 被算成四張，面板的流量是實際的四倍）。篩選器和本體共用實體位址，
    /// 依位址只留第一張；沒有實體位址的（撥接、行動網路）各自算
    /// </summary>
    public static IEnumerable<T> OncePerAddress<T>(IEnumerable<T> nics, Func<T, string?> address)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nic in nics)
        {
            var mac = address(nic);
            if (string.IsNullOrEmpty(mac) || seen.Add(mac))
                yield return nic;
        }
    }

    /// <summary>
    /// 一張網卡的流量要不要算進這台的上下行：啟用中、實體的乙太網路或無線網路（含行動網路），描述不是虛擬網卡
    /// </summary>
    public static bool CountsTraffic(NetworkInterfaceType type, OperationalStatus status, string? description)
    {
        if (status != OperationalStatus.Up)
            return false;
        if (type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 or NetworkInterfaceType.Ppp))
            return false;
        return description is null || !VirtualMarkers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>磁碟機代號：<c>C:\</c> → <c>C:</c>；清理後不合格（<see cref="DeviceStatsRules.Check"/> 會擋）回 null</summary>
    public static string? DiskName(string? rootName)
    {
        var name = rootName?.TrimEnd('\\', '/');
        return LabelRules.Clean(name, DeviceStatsRules.MaxDiskNameLength) is { } clean && clean == name ? clean : null;
    }

    /// <summary>CPU 名稱：去掉多餘空白（登錄的值常有連續空白與尾端空白），清理、截斷；空的回 null</summary>
    public static string? CpuName(string? raw) => DisplayName(raw, DeviceStatsRules.MaxCpuNameLength);

    /// <summary>硬體名稱（CPU、顯示卡）：連續空白併成一個、清理、截斷到 <paramref name="maxLength"/>；空的回 null</summary>
    public static string? DisplayName(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var collapsed = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length > maxLength)
            collapsed = collapsed[..maxLength];
        return LabelRules.Clean(collapsed, maxLength);
    }
}
