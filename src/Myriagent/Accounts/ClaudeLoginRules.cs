using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>claude_authenticate 的回應：本機回呼的網址（這台的瀏覽器用）、手動的網址（登入後頁面顯示授權碼，貼回來）與這次流程的 state</summary>
internal sealed record ClaudeLoginFlow(Uri AutomaticUrl, Uri ManualUrl, string State);

/// <summary>
/// Claude 的 app 內登入（2026-10-26 起取代主控台的 <c>claude auth login</c>；做不到時退回）：在不送訊息的程序裡 claude_authenticate 拿到兩個網址，
/// 這台登入就開瀏覽器到本機回呼的網址、等 claude_oauth_wait_for_completion；遠端（瀏覽器版、手機）登入把手動的網址給網頁，
/// 使用者在手機登入後把頁面上的授權碼貼回來，這台以 claude_oauth_callback 交給 CLI。錯的授權碼兩個請求都回錯誤、這次流程就結束了。
/// 實測見 docs/protocol-notes.md「Claude：CLI 代查用量、控制請求登入…」（tools/probes/claude_cli7_probe.py --mode login）
/// </summary>
internal static class ClaudeLoginRules
{
    /// <summary>claude_authenticate（claude.ai 訂閱登入）</summary>
    public static JObject BuildAuthenticateRequest() => new() { ["subtype"] = "claude_authenticate", ["loginWithClaudeAi"] = true };

    /// <summary>等登入完成（本機回呼或貼回的授權碼）</summary>
    public static JObject BuildWaitRequest() => new() { ["subtype"] = "claude_oauth_wait_for_completion" };

    /// <summary>手動流程：把使用者貼回來的授權碼交給 CLI</summary>
    public static JObject BuildCallbackRequest(string code, string state) => new()
    {
        ["subtype"] = "claude_oauth_callback",
        ["authorizationCode"] = code,
        ["state"] = state,
    };

    /// <summary>
    /// claude_authenticate 的回應 → 兩個網址與 state。網址要是 https 的 claude.com／claude.ai（<see cref="RemoteLoginUrlRules.IsAllowedClaudeLoginUrl"/>），
    /// 兩個網址的 state 要相同；不合格回 null
    /// </summary>
    public static ClaudeLoginFlow? ParseAuthenticate(JObject? response)
    {
        var automatic = response?["automaticUrl"] is JValue { Type: JTokenType.String } a ? a.ToString() : null;
        var manual = response?["manualUrl"] is JValue { Type: JTokenType.String } m ? m.ToString() : null;
        if (!RemoteLoginUrlRules.IsAllowedClaudeLoginUrl(automatic) || !RemoteLoginUrlRules.IsAllowedClaudeLoginUrl(manual))
            return null;
        var autoUri = new Uri(automatic!);
        var manualUri = new Uri(manual!);
        var state = QueryValue(manualUri, "state");
        if (string.IsNullOrEmpty(state) || state != QueryValue(autoUri, "state"))
            return null;
        return new ClaudeLoginFlow(autoUri, manualUri, state);
    }

    /// <summary>
    /// 使用者貼回來的授權碼：頁面顯示的是「授權碼#state」（只貼前半也可以）；去掉空白。state 和這次的不同、授權碼是空的或太長回 null
    /// </summary>
    public static string? ParseCode(string? pasted, string expectedState)
    {
        var text = pasted?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxCodeLength)
            return null;
        var hash = text.IndexOf('#');
        var code = (hash >= 0 ? text[..hash] : text).Trim();
        if (hash >= 0 && text[(hash + 1)..].Trim() is { Length: > 0 } state && state != expectedState)
            return null;
        return code.Length == 0 || code.Any(char.IsWhiteSpace) ? null : code;
    }

    public const int MaxCodeLength = 2048;

    /// <summary>CLI 的錯誤 → 給人看的說明</summary>
    public static string DescribeError(string? error)
    {
        var e = error?.Trim() ?? string.Empty;
        if (e.Contains("status code 400", StringComparison.OrdinalIgnoreCase) || e.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
            return "授權碼不對或已經過期，請重新登入";
        if (e.Contains("No active claude_authenticate flow", StringComparison.OrdinalIgnoreCase))
            return "這次登入已經結束，請重新登入";
        return e.Length == 0 ? "登入沒有完成" : "登入沒有完成：" + e;
    }

    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0 && Uri.UnescapeDataString(part[..eq]) == key)
                return Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }
}
