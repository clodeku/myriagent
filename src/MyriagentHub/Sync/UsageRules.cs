using Myriagent.Security;
using Myriagent.Sync;

namespace MyriagentHub.Sync;

/// <summary>某台裝置對某個邏輯帳戶的查詢資格（account_links 的一格＋連線狀態）</summary>
/// <param name="LastHeartbeatAt">伺服器收到這台最後一次心跳（或證明身分）的伺服器時間；不在線為 null</param>
internal sealed record PollerCandidate(
    string DeviceId,
    string Status,
    bool IdentityOk,
    long? AccessExpiresAt,
    int RunningSessions,
    bool Online,
    long? LastHeartbeatAt);

/// <summary>
/// 共享用量的純函式規則（sync-protocol 6.4）：每個邏輯帳戶由伺服器指派一台查官方用量。
/// 候選：連結正常（ready）、身分相符、在線、60 秒內有心跳、Claude 的存取權杖還有 5 分鐘以上（每台都不到 5 分鐘時不看權杖）；
/// 優先：有該帳戶執行中對話的 → 上一任 → deviceId 最小（序數比較，結果穩定）。
/// </summary>
internal static class UsageRules
{
    public const long HeartbeatTimeoutMs = 60_000;

    public const long AccessTokenMarginMs = 5 * 60_000;

    /// <summary>usage_points 的保留期</summary>
    public const long PointsRetentionMs = 14L * 24 * 60 * 60 * 1000;

    /// <summary>不帶 term 的（對話中的 rate limit 事件）每台每帳戶的最短間隔：裝置端是 60 秒，留 10 秒給網路抖動</summary>
    public const long SessionPublishMinIntervalMs = 50_000;

    public const int MaxWindows = 8;

    public const int MaxWindowKeyLength = 32;

    public const double MaxPercent = 1000;

    public const string SourceApi = "api";

    public const string SourceSession = "session";

    /// <summary>連結正常（account_links.status）：才能被指派查用量、才能上傳</summary>
    public const string LinkReady = "ready";

    public static bool IsCandidate(PollerCandidate candidate, string provider, long now) =>
        IsReachable(candidate, now)
        // 存取權杖快到期的那台查詢時會先被 CLI 換權杖（我們絕不自己 refresh）：交給別台；不知道到期時間的不排除
        && (provider != CatalogRules.Claude || candidate.AccessExpiresAt is not { } expires || expires - now >= AccessTokenMarginMs);

    /// <summary>除了存取權杖的期限以外都合格：連結正常、身分相符、在線、60 秒內有心跳</summary>
    public static bool IsReachable(PollerCandidate candidate, long now) =>
        candidate.Status == LinkReady
        && candidate.IdentityOk
        && candidate.Online
        && candidate.LastHeartbeatAt is { } beat && now - beat <= HeartbeatTimeoutMs;

    /// <summary>
    /// 該由誰負責查詢（連 <see cref="IsReachable"/> 的都沒有才回 null）。<paramref name="current"/> 是目前負責的（空字串或 null＝沒有人）。
    /// 每台的 Claude 存取權杖都快到期（閒置一陣子就會這樣）時，仍從 <see cref="IsReachable"/> 的裡面指派一台：那台查詢回「權杖已過期」時
    /// 會讓 CLI 代查並換發（桌面的 ClaudeTokenRecoveryRules），換發後就回到合格。不指派的話沒有一台會查、也沒有一台會換發，
    /// 用量一直停住到有人開對話（2026-10-28 夜裡 example-user 的每週額度停在 100%，隔天重置了也沒更新）
    /// </summary>
    public static string? Choose(IReadOnlyList<PollerCandidate> candidates, string? current, string provider, long now)
    {
        var eligible = candidates.Where(c => IsCandidate(c, provider, now)).ToList();
        if (eligible.Count == 0)
            eligible = candidates.Where(c => IsReachable(c, now)).ToList();
        if (eligible.Count == 0)
            return null;
        var running = eligible.Where(c => c.RunningSessions > 0).ToList();
        var pool = running.Count > 0 ? running : eligible;
        if (!string.IsNullOrEmpty(current) && pool.Any(c => c.DeviceId == current))
            return current;
        return pool.Select(c => c.DeviceId).Min(StringComparer.Ordinal);
    }

    /// <summary>
    /// 裝置上傳的用量快照格式檢查（不可信）：來源與 term 相符（帶 term＝負責查詢的那台查官方端點、不帶＝對話中的 rate limit 事件）、
    /// 時間窗數量與欄位長度有上限、百分比是有限值
    /// </summary>
    public static string? Validate(UsagePublish? publish)
    {
        if (publish is null || !SyncIds.IsLogicalAccountId(publish.LogicalAccountId) || publish.Usage is not { } usage)
            return "格式不正確";
        if (publish.Term is { } term && term < 1)
            return "term 不正確";
        if (usage.Source != (publish.Term is null ? SourceSession : SourceApi))
            return "source 與 term 不相符";
        if (usage.FetchedAt <= 0 || usage.Windows is null || usage.Windows.Count > MaxWindows)
            return "時間窗不正確";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in usage.Windows)
        {
            if (w is null || w.Key is not { Length: > 0 and <= MaxWindowKeyLength } key
                || key.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") >= 0 || !keys.Add(key))
                return "時間窗的鍵不正確";
            if (LabelRules.Clean(w.Label, CatalogRules.MaxLabelLength) != w.Label)
                return "時間窗的名稱不正確";
            if (!double.IsFinite(w.Percent) || w.Percent < 0 || w.Percent > MaxPercent || w.ResetsAt is <= 0)
                return "時間窗的數值不正確";
        }
        if (!CatalogRules.IsOptionalText(usage.Credits, CatalogRules.MaxShortTextLength) || !CatalogRules.IsOptionalText(usage.Note, CatalogRules.MaxTextLength)
            || !CatalogRules.IsOptionalText(usage.Error, CatalogRules.MaxTextLength))
            return "文字欄位太長或含控制字元";
        return null;
    }
}
