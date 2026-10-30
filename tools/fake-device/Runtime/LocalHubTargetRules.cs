using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FakeDevice;

/// <summary>連線之後看到的伺服器（<see cref="LocalHubTargetRules.IsProductionTarget"/> 的輸入）</summary>
/// <param name="Environment">/readyz 的 environment（service＝Windows 服務、console＝主控台）</param>
/// <param name="Names">憑證與 /readyz 的 tls 名稱（主體、顯示名稱、SAN）</param>
/// <param name="Issuers">憑證與 /readyz 的 tls 簽發者</param>
internal sealed record LocalHubTargetFacts(string? Environment, IReadOnlyList<string> Names, IReadOnlyList<string> Issuers);

/// <summary>
/// 正式站守門（WP4.9 計畫 D18；tools/hub-e2e/lib/guard-rules.mjs 是同一套規則，測試表 tools/hub-e2e/test/guard-cases.json 兩邊共用）：
/// 工具只連自己在這台電腦啟動的 MyriagentHub。連線之前只收 https 加迴路主機名稱、拒絕任何 example.com；連上之後再看 /readyz 與憑證，
/// 正式服務（environment 是 service）、憑證名稱含 hub.example.com、或簽發者是公開 CA 任一成立就拒絕——只看主機名稱擋不住
/// 「ssh -L 本機埠:127.0.0.1:443」的轉送（/readyz 對本機迴路開放，轉送過去會以迴路身分讀到正式站）。只用 BCL（hub-probe 也連結這個檔）
/// </summary>
internal static partial class LocalHubTargetRules
{
    /// <summary>正式站的網域（任何主機名稱含它就拒絕）</summary>
    public const string ProductionDomain = "example.com";

    /// <summary>正式站的主機名稱（憑證與 /readyz 的 tls 名稱）</summary>
    public const string ProductionHost = "hub.example.com";

    /// <summary>只收這三個主機名稱（Uri.Host 的寫法；IPv6 帶方括號）</summary>
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.Ordinal) { "127.0.0.1", "localhost", "[::1]" };

    /// <summary>連線之前：可以連就回 null，否則回原因（只收 https、主機是迴路、沒有帳號密碼；任何含 example.com 的主機一律拒絕）</summary>
    public static string? CheckUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "不是完整的網址";
        if (uri.Scheme != Uri.UriSchemeHttps)
            return "只收 https";
        if (uri.UserInfo.Length > 0)
            return "網址不能帶帳號密碼";
        var host = uri.Host.ToLowerInvariant();
        if (host.Contains(ProductionDomain, StringComparison.Ordinal))
            return $"不連 {ProductionDomain}（工具只連這台電腦自己啟動的 MyriagentHub）";
        return LoopbackHosts.Contains(host) ? null : "只收迴路位址（127.0.0.1、localhost、[::1]）";
    }

    public static bool IsAllowedServerUrl(string? url) => CheckUrl(url) is null;

    /// <summary>連上之後：是不是正式站（是就拒絕）</summary>
    public static bool IsProductionTarget(LocalHubTargetFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Environment == "service")
            return true;
        if (facts.Names.Any(n => n.Contains(ProductionHost, StringComparison.OrdinalIgnoreCase)))
            return true;
        return facts.Issuers.Any(i => PublicCa().IsMatch(i));
    }

    /// <summary>連上之後：可以用就回 null，否則回原因</summary>
    public static string? CheckTarget(LocalHubTargetFacts facts) =>
        IsProductionTarget(facts) ? "這台伺服器看起來是正式服務（Windows 服務、hub.example.com 的憑證或公開 CA 簽發）：工具只連這台電腦自己啟動的 MyriagentHub" : null;

    /// <summary>
    /// 由 /readyz 的內容（JSON 文字）與連線看到的憑證（JSON 文字：<c>{subject, name, dnsNames, subjectaltname, issuer}</c> 任一子集，guard-cases.json 的寫法）取事實
    /// </summary>
    public static LocalHubTargetFacts FactsFromJson(string? readyzJson, string? tlsJson)
    {
        using var readyz = Parse(readyzJson);
        using var tls = Parse(tlsJson);
        var names = new List<string>();
        var issuers = new List<string>();
        AddTls(tls?.RootElement, names, issuers);
        AddTls(Property(readyz?.RootElement, "tls"), names, issuers);
        return new LocalHubTargetFacts(EnvironmentOf(readyz), names, issuers);
    }

    /// <summary>由 /readyz 的內容與連線看到的憑證取事實（憑證的主體、DNS 名稱與簽發者）</summary>
    public static LocalHubTargetFacts FactsFromCertificate(string? readyzJson, X509Certificate2? certificate)
    {
        using var readyz = Parse(readyzJson);
        var names = new List<string>();
        var issuers = new List<string>();
        if (certificate is not null)
        {
            names.Add(certificate.Subject);
            names.AddRange(DnsNames(certificate));
            issuers.Add(certificate.Issuer);
        }

        AddTls(Property(readyz?.RootElement, "tls"), names, issuers);
        return new LocalHubTargetFacts(EnvironmentOf(readyz), names, issuers);
    }

    /// <summary>憑證的 SAN 裡的 DNS 名稱</summary>
    public static IReadOnlyList<string> DnsNames(X509Certificate2 certificate)
    {
        var result = new List<string>();
        foreach (var extension in certificate.Extensions)
        {
            if (extension.Oid?.Value != "2.5.29.17")
                continue;
            var san = extension as X509SubjectAlternativeNameExtension ?? new X509SubjectAlternativeNameExtension(extension.RawData, extension.Critical);
            result.AddRange(san.EnumerateDnsNames());
        }

        return result;
    }

    // 公開 CA 的簽發者名稱（Let's Encrypt 的中繼憑證 R10～R14、E5～E9、ISRG，與常見的商業 CA）；本機的自簽與 Pebble 都不在裡面。和 guard-rules.mjs 的 PUBLIC_CA 相同
    [GeneratedRegex(@"Let'?s Encrypt|\bISRG\b|\bCN=(?:R1\d|E\d+|YE\d|YR\d)\b|DigiCert|Sectigo|GlobalSign|ZeroSSL|Google Trust|GoDaddy|Entrust|Amazon|Microsoft Azure|TWCA|Chunghwa",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublicCa();

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? EnvironmentOf(JsonDocument? readyz) =>
        Property(readyz?.RootElement, "environment") is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    private static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value) ? value : null;

    /// <summary>tls 區塊：subject、name、dnsNames、subjectaltname 算名稱，issuer 算簽發者（字串、字串陣列或物件都收；物件以 JSON 文字比）</summary>
    private static void AddTls(JsonElement? tls, List<string> names, List<string> issuers)
    {
        if (tls is not { ValueKind: JsonValueKind.Object } block)
            return;
        foreach (var key in new[] { "subject", "name", "dnsNames", "subjectaltname" })
        {
            if (block.TryGetProperty(key, out var value))
                Texts(value, names);
        }

        if (block.TryGetProperty("issuer", out var issuer))
            Texts(issuer, issuers);
    }

    private static void Texts(JsonElement value, List<string> into)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                into.Add(value.GetString()!);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    Texts(item, into);
                break;
            case JsonValueKind.Object:
                into.Add(value.GetRawText());
                break;
        }
    }
}
