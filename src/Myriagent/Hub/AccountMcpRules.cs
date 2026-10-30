using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>一個 Claude 帳戶的 MCP 狀態（儀表板；只在記憶體）</summary>
internal sealed class AccountMcpState
{
    /// <summary>上次查到的伺服器（null＝還沒查到過）</summary>
    public IReadOnlyList<AgentMcpServer>? Servers;

    public DateTime? CheckedAt;

    public bool Checking;

    /// <summary>上次查詢失敗的原因（查到了就清掉；之前的 Servers 照留）</summary>
    public string? Error;

    /// <summary>正在授權的伺服器（開著瀏覽器等使用者完成）；null＝沒有</summary>
    public string? Authorizing;
}

/// <summary>
/// 儀表板的 MCP（web-protocol 的 mcp／mcpCheck／mcpAction，2026-10-24 使用者要求「以帳戶為單位放在儀表板」）：
/// 設定（使用者層、外掛、claude.ai 連接器）與授權的權杖都在帳戶的設定資料夾，所以以帳戶為單位看與授權；停用是逐資料夾的設定（不做）
/// </summary>
internal static class AccountMcpRules
{
    /// <summary>不帶 force 的 mcpCheck 在這段時間內查過就不重查</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

    /// <summary>一個帳戶最多列幾個伺服器</summary>
    public const int MaxServers = 60;

    /// <summary>錯誤原因留多少字</summary>
    public const int MaxErrorLength = 300;

    /// <summary>要不要（重新）查：正在查、正在授權時不查；force 一律查；否則沒查過或超過 MinInterval 才查</summary>
    public static bool ShouldCheck(AccountMcpState? state, DateTime now, bool force)
        => state == null || (!state.Checking && state.Authorizing == null && (force || state.CheckedAt == null || now - state.CheckedAt.Value >= MinInterval));

    /// <summary>
    /// 一個帳戶的 mcp 項目：{checking, checkedAt, error, authorizing, safeMode, servers: [{name, status, source?, transport?, tools?, error?}] | null}。
    /// servers 是 null＝還沒查到過；精簡模式不載入 MCP（safeMode true、不查）
    /// </summary>
    public static JObject RenderAccount(AccountMcpState? state, bool safeMode, Func<DateTime, long> toUnixMs)
    {
        ArgumentNullException.ThrowIfNull(toUnixMs);
        return new JObject
        {
            ["checking"] = state?.Checking == true,
            ["checkedAt"] = state?.CheckedAt is { } at ? toUnixMs(at) : null,
            ["error"] = state?.Error is { } error ? ClaudeStreamRules.OneLine(error, MaxErrorLength) : null,
            ["authorizing"] = state?.Authorizing,
            ["safeMode"] = safeMode,
            ["servers"] = state?.Servers is { } servers ? new JArray(servers.Take(MaxServers).Select(RenderServer)) : null,
        };
    }

    private static JObject RenderServer(AgentMcpServer s)
    {
        var o = new JObject { ["name"] = s.Name, ["status"] = s.Status };
        if (s.Source != null)
            o["source"] = s.Source;
        if (s.Transport != null)
            o["transport"] = s.Transport;
        if (s.Tools != null)
            o["tools"] = s.Tools;
        if (!string.IsNullOrWhiteSpace(s.Error))
            o["error"] = ClaudeStreamRules.OneLine(s.Error, MaxErrorLength);
        return o;
    }

    /// <summary>mcpAction 的 action（authenticate、reconnect）；不認得回 null</summary>
    public static AgentMcpAction? ParseAction(string? action) => action switch
    {
        "authenticate" => AgentMcpAction.Authenticate,
        "reconnect" => AgentMcpAction.Reconnect,
        _ => null,
    };

    /// <summary>還在等使用者授權（或剛授權完、還在連線）</summary>
    public static bool AwaitingAuth(string? status) => status is "needs-auth" or "pending";
}
