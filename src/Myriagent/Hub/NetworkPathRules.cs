using System.Net;
using System.Net.Sockets;

namespace Myriagent.Hub;

/// <summary>網路路徑的拒絕原因（只進記錄；給遠端的說明見 <see cref="NetworkPathRules.Explain"/>）</summary>
internal static class NetworkPathReasons
{
    /// <summary>格式不合格（不是 <c>\\電腦\分享\…</c>、有 <c>..</c>、替代資料流、不合法的字元、太長）</summary>
    public const string Invalid = "invalid";

    /// <summary>主機名稱解析不出位址</summary>
    public const string Unresolved = "unresolved";

    /// <summary>主機有任何一個位址不在區域網路（存取時 Windows 會把登入雜湊送過去）</summary>
    public const string NotLan = "not_lan";

    /// <summary>主機就是這台（經網路路徑會繞過本機的禁止清單）</summary>
    public const string Self = "self";

    /// <summary>IPC$ 這類不是資料夾的分享</summary>
    public const string Share = "share";

    /// <summary>檔名在拒絕清單（憑證、金鑰）</summary>
    public const string Name = "name";

    /// <summary>網路逾時</summary>
    public const string Timeout = "timeout";

    /// <summary>不存在、讀不到</summary>
    public const string NotFound = "not_found";
}

/// <summary>
/// 電腦狀態面板的檔案傳輸裡的網路芳鄰（2026-11-09 擁有者要求：遠端也要能看到網路芳鄰的路徑）：<c>\\電腦\分享\…</c> 與對應的網路磁碟機。
/// 存取網路路徑時 Windows 會自動把這台登入的 NTLM 雜湊送給對方，所以只收區域網路的主機（解析出的位址全在私人網段）；
/// 主機是這台自己的一律不收（<c>\\localhost\c$</c> 會繞過本機的禁止清單）。<c>\\</c> 是「網路」這一層（列區域網路的電腦）。純函式
/// </summary>
internal static class NetworkPathRules
{
    /// <summary>「網路」這一層的路徑</summary>
    public const string NetworkRoot = @"\\";

    /// <summary>網路路徑的長度上限（同本機）</summary>
    public const int MaxPathLength = 32_000;

    private static readonly char[] InvalidChars = ['<', '>', '"', '|', '?', '*', ':', '/'];

    public static bool IsNetworkRoot(string? path) => path == NetworkRoot;

    /// <summary>UNC 的樣子（<c>\\</c> 開頭、不是 <c>\\?\</c>、<c>\\.\</c> 這類裝置路徑）；格式合不合格看 <see cref="TryParse"/></summary>
    public static bool LooksUnc(string? path)
        => path is { Length: > 2 } && path.StartsWith(NetworkRoot, StringComparison.Ordinal) && path[2] is not ('?' or '.' or '\\');

    /// <summary>
    /// 拆開 <c>\\主機[\分享[\其他…]]</c>：主機是英數字、<c>-</c>、<c>_</c>、<c>.</c>（IPv4 也算）；每一段不能是 <c>.</c>、<c>..</c>、不能有控制字元、
    /// <c>:</c>（替代資料流）與其他不合法的字元、不能以點或空白結尾。結尾的反斜線去掉
    /// </summary>
    public static bool TryParse(string? path, out string host, out string? share, out IReadOnlyList<string> rest)
    {
        host = string.Empty;
        share = null;
        rest = [];
        if (!LooksUnc(path) || path!.Length > MaxPathLength)
            return false;
        var parts = path[2..].TrimEnd('\\').Split('\\');
        if (parts.Length == 0 || parts.Any(p => p.Length == 0))
            return false;
        host = parts[0];
        if (!IsHostName(host))
            return false;
        foreach (var part in parts.Skip(1))
        {
            if (part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => char.IsControl(c) || Array.IndexOf(InvalidChars, c) >= 0))
                return false;
        }

        share = parts.Length > 1 ? parts[1] : null;
        rest = parts.Skip(2).ToList();
        return true;
    }

    /// <summary>主機名稱：1～253 個英數字、<c>-</c>、<c>_</c>、<c>.</c>，不以點開頭或結尾</summary>
    public static bool IsHostName(string? host)
        => host is { Length: > 0 and <= 253 } && host[0] != '.' && host[^1] != '.'
           && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>整理過的路徑（去掉結尾的反斜線）；不合格回 null</summary>
    public static string? Normalize(string? path)
    {
        if (IsNetworkRoot(path))
            return NetworkRoot;
        if (!TryParse(path, out var host, out var share, out var rest))
            return null;
        return NetworkRoot + host + (share is null ? string.Empty : @"\" + share) + string.Concat(rest.Select(r => @"\" + r));
    }

    /// <summary>上一層：<c>\\主機\分享\a</c> → <c>\\主機\分享</c> → <c>\\主機</c> → <c>\\</c>（「網路」）→ null（磁碟與常用資料夾）</summary>
    public static string? Parent(string? path)
    {
        if (IsNetworkRoot(path) || Normalize(path) is not { } normalized)
            return null;
        var cut = normalized.LastIndexOf('\\');
        return cut <= 1 ? NetworkRoot : normalized[..cut];
    }

    /// <summary>不是資料夾的分享（IPC$）與印表機的分享不給</summary>
    public static bool IsBlockedShare(string? share) => string.Equals(share, "IPC$", StringComparison.OrdinalIgnoreCase) || string.Equals(share, "print$", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 主機能不能連：這台的電腦名稱、<c>localhost</c>、任何一個位址是迴路或這台自己的位址 → <see cref="NetworkPathReasons.Self"/>；
    /// 解析不出位址 → <see cref="NetworkPathReasons.Unresolved"/>；任何一個位址不在區域網路 → <see cref="NetworkPathReasons.NotLan"/>；都過了回 null
    /// </summary>
    public static string? HostVerdict(string host, IReadOnlyList<IPAddress> resolved, IReadOnlyCollection<IPAddress> localAddresses, string? machineName)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(localAddresses);
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(machineName) && (string.Equals(host, machineName, StringComparison.OrdinalIgnoreCase)
                                                       || host.StartsWith(machineName + ".", StringComparison.OrdinalIgnoreCase)))
            return NetworkPathReasons.Self;
        if (resolved.Count == 0)
            return NetworkPathReasons.Unresolved;
        var local = localAddresses.Select(Unmap).ToHashSet();
        if (resolved.Select(Unmap).Any(ip => IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || local.Contains(ip)))
            return NetworkPathReasons.Self;
        return resolved.All(IsLanAddress) ? null : NetworkPathReasons.NotLan;
    }

    /// <summary>
    /// 區域網路的位址：IPv4 的 10/8、172.16/12、192.168/16、169.254/16（自動配置）、100.64/10（Tailscale 這類的 CGNAT）；
    /// IPv6 的 fe80::/10（連結本機）、fc00::/7（ULA）。IPv4 對應的 IPv6 照 IPv4 判斷
    /// </summary>
    public static bool IsLanAddress(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        ip = Unmap(ip);
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                   || b[0] == 172 && b[1] >= 16 && b[1] <= 31
                   || b[0] == 192 && b[1] == 168
                   || b[0] == 169 && b[1] == 254
                   || b[0] == 100 && b[1] >= 64 && b[1] <= 127;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0xfe && (b[1] & 0xc0) == 0x80 || (b[0] & 0xfe) == 0xfc;
        }

        return false;
    }

    private static IPAddress Unmap(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>「網路」這一層沒有找到電腦時的說明：掃描了哪些網段（判斷是不是掃錯網卡），以及直接輸入路徑的提示</summary>
    public static string NoComputersMessage(string? summary)
        => "沒有找到網路上的電腦" + (string.IsNullOrWhiteSpace(summary) ? string.Empty : "（" + summary + "）")
           + "：NAS 或另一個網段的電腦請在路徑列直接輸入 \\\\電腦名稱、\\\\IP 位址 或 \\\\電腦名稱\\分享名稱";

    /// <summary>給遠端看的說明</summary>
    public static string Explain(string reason) => reason switch
    {
        NetworkPathReasons.NotLan => "只能存取區域網路裡的電腦（這台電腦會把登入資訊送給對方，網際網路上的主機一律不連）",
        NetworkPathReasons.Self => "這是那台電腦自己的分享，請直接瀏覽它的磁碟",
        NetworkPathReasons.Unresolved => "找不到這台網路上的電腦（名稱打錯，或那台不在同一個網路）",
        NetworkPathReasons.Share => "這個分享不是資料夾",
        NetworkPathReasons.Timeout => "網路上的電腦沒有回應（逾時）",
        NetworkPathReasons.Name => "這個檔案不能給其他電腦存取（憑證、金鑰類的檔名）",
        NetworkPathReasons.NotFound => "這個網路路徑不存在或讀不到（可能要先在那台登入過這個分享）",
        _ => "網路路徑的格式不正確（要像 \\\\電腦名稱\\分享名稱）",
    };
}
