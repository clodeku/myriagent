namespace Myriagent.Remote;

/// <summary>
/// 遠端登入（accountLogin 的 deviceCode）驗證網址的主機白名單（契約 D18）：Codex 的 <c>auth.openai.com</c>、Grok 的 <c>accounts.x.ai</c>
/// （設計 2026-10-22-grok-antigravity-sync 的 P3，tools/probes/grok/login_url_probe.py 實測）。
/// 伺服器轉給瀏覽器之前檢查（WP4.5），電腦送出之前也用同一份（WP4.8）
/// </summary>
internal static class RemoteLoginUrlRules
{
    public static IReadOnlySet<string> VerificationHosts { get; } = new HashSet<string>(StringComparer.Ordinal) { "auth.openai.com", "accounts.x.ai" };

    /// <summary>Claude 的 app 內登入網址（claude_authenticate 給的 manualUrl、automaticUrl；2026-10-26）的主機白名單</summary>
    public static IReadOnlySet<string> ClaudeLoginHosts { get; } = new HashSet<string>(StringComparer.Ordinal) { "claude.com", "claude.ai" };

    /// <summary>Claude 的登入網址：https、沒有帳密、預設埠、主機在 <see cref="ClaudeLoginHosts"/>（遠端登入給網頁的 loginUrl，伺服器與電腦用同一份）</summary>
    public static bool IsAllowedClaudeLoginUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort && ClaudeLoginHosts.Contains(uri.IdnHost.ToLowerInvariant());

    /// <summary>https、沒有帳密、預設埠、主機（小寫）在白名單裡</summary>
    public static bool IsAllowedVerificationUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && uri.IsDefaultPort && VerificationHosts.Contains(uri.IdnHost.ToLowerInvariant());
}
