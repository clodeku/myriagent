using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// Claude 對話的主代理（--agent）與輸出風格（outputStyle）、設定頁的備援模型（--fallback-model）：值的正規化與檢查。
/// 清單是各帳戶上次 CLI 在 initialize 回報的（AppSettings.ClaudeAgents、OutputStyles），實測見 docs/protocol-notes.md「Claude：回溯、插入的訊息取消、背景工作的輸出、主代理與輸出風格、備援模型」
/// </summary>
internal static class ClaudeCustomizationRules
{
    /// <summary>主代理：null、空字串、"default"、內建的 "claude" 都是預設主代理（不帶 --agent）</summary>
    public static string? NormalizeAgent(string? agent)
    {
        var a = agent?.Trim();
        if (string.IsNullOrEmpty(a) || a.Equals("default", StringComparison.OrdinalIgnoreCase) || a == ClaudeStreamRules.DefaultMainAgent)
            return null;
        return a;
    }

    /// <summary>輸出風格：null、空字串＝照帳戶的設定（不帶 outputStyle）；其他原樣（風格名稱分大小寫，CLI 的內建是 default、Explanatory…）</summary>
    public static string? NormalizeStyle(string? style)
    {
        var s = style?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>
    /// 啟動時要帶的值：沒選、名稱不合格時不帶；這個帳戶回報過清單而清單裡沒有它時也不帶（換到另一個帳戶、外掛被移除——
    /// 帶了不存在的名稱 CLI 怎麼處理沒有保證，寧可用預設）。還沒回報過清單的帳戶照帶
    /// </summary>
    public static string? AgentForLaunch(string? agent, IReadOnlyList<AgentNamedOption>? known)
    {
        var a = NormalizeAgent(agent);
        if (a == null || !ClaudeStreamRules.IsValidAgentName(a))
            return null;
        return known is { Count: > 0 } && !known.Any(x => x.Value == a) ? null : a;
    }

    /// <summary>同 <see cref="AgentForLaunch"/>：輸出風格</summary>
    public static string? StyleForLaunch(string? style, IReadOnlyList<string>? known)
    {
        var s = NormalizeStyle(style);
        if (s == null || s.Length > 100)
            return null;
        return known is { Count: > 0 } && !known.Contains(s, StringComparer.Ordinal) ? null : s;
    }

    /// <summary>兩份主代理清單相同（不必存檔、不必重送標頭）</summary>
    public static bool SameAgents(IReadOnlyList<AgentNamedOption>? a, IReadOnlyList<AgentNamedOption>? b)
        => (a?.Count ?? 0) == (b?.Count ?? 0) && (a == null || a.SequenceEqual(b!));

    /// <summary>兩份輸出風格清單相同</summary>
    public static bool SameStyles(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        => (a?.Count ?? 0) == (b?.Count ?? 0) && (a == null || a.SequenceEqual(b!, StringComparer.Ordinal));

    /// <summary>
    /// 設定頁的備援模型：空字串、"none"、"default" 是不用；其他要是設定頁列出的 Claude 模型之一（不在清單裡的不收，回 false）
    /// </summary>
    public static bool TryNormalizeFallback(string? value, IEnumerable<string> models, out string? fallback)
    {
        var v = value?.Trim();
        fallback = null;
        if (string.IsNullOrEmpty(v) || v.Equals("none", StringComparison.OrdinalIgnoreCase) || v.Equals("default", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!models.Contains(v, StringComparer.Ordinal))
            return false;
        fallback = v;
        return true;
    }
}
