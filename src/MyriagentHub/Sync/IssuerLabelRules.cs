using Myriagent.Security;

namespace MyriagentHub.Sync;

/// <summary>
/// 信封的簽發者標籤（WP4.5 計畫 D11；sync-protocol 14.4 的 <c>issuer.label</c>）：由工作階段的 User-Agent 認出瀏覽器與作業系統，
/// 例如「Chrome・Windows」。只會是下面兩張表的名稱組合，一定通過 <see cref="CommandEnvelope.IsValidIssuerLabel"/>；
/// 認不出瀏覽器時是 <see cref="Fallback"/>。UA 可以偽造，標籤只供顯示（伺服器可信任，擁有者 2026-10-19 指示）
/// </summary>
internal static class IssuerLabelRules
{
    public const string Fallback = "瀏覽器";

    private const string Separator = "・";

    /// <summary>
    /// 依序比對（Ordinal）：Edge、Opera 的 UA 也含 <c>Chrome/</c>，Chrome 的也含 <c>Safari/</c>，所以先認它們
    /// </summary>
    private static readonly (string Token, string Name)[] Browsers =
    [
        ("Edg/", "Edge"),
        ("EdgA/", "Edge"),
        ("EdgiOS/", "Edge"),
        ("OPR/", "Opera"),
        ("Chrome/", "Chrome"),
        ("CriOS/", "Chrome"),
        ("Firefox/", "Firefox"),
        ("FxiOS/", "Firefox"),
        ("Safari/", "Safari"),
    ];

    /// <summary>依序比對（Ordinal）：iOS 的 UA 含 <c>like Mac OS X</c>、Android 的含 <c>Linux</c>，所以先認它們</summary>
    private static readonly (string Token, string Name)[] Systems =
    [
        ("iPhone", "iOS"),
        ("iPad", "iOS"),
        ("iPod", "iOS"),
        ("Android", "Android"),
        ("Windows", "Windows"),
        ("Macintosh", "macOS"),
        ("Mac OS X", "macOS"),
        ("Linux", "Linux"),
    ];

    /// <summary>瀏覽器・作業系統；認不出作業系統時只有瀏覽器名稱；null、空字串與認不出瀏覽器時是 <see cref="Fallback"/></summary>
    public static string Label(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent) || First(Browsers, userAgent) is not { } browser)
            return Fallback;
        var label = First(Systems, userAgent) is { } system ? browser + Separator + system : browser;
        return CommandEnvelope.IsValidIssuerLabel(label) ? label : Fallback;
    }

    private static string? First((string Token, string Name)[] table, string userAgent)
    {
        foreach (var (token, name) in table)
        {
            if (userAgent.Contains(token, StringComparison.Ordinal))
                return name;
        }

        return null;
    }
}
