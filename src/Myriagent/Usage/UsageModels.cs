namespace Myriagent.Usage;

/// <summary>一個額度時間窗（例如 5 小時、每週、每週・某模型）的使用率</summary>
internal sealed record UsageWindow(string Key, string Label, double Percent, DateTimeOffset? ResetsAt, bool Locked = false);

/// <summary>
/// 帳戶的用量快照。<see cref="ResetCredits"/> 是 Codex 可用的免費重置次數（<c>rateLimitResetCredits.availableCount</c>，
/// 只有官方用量端點帶；不知道為 null），<see cref="ResetCreditExpiresAt"/> 是其中最早到期的那一次
/// </summary>
internal sealed record UsageSnapshot(
    DateTimeOffset FetchedAt,
    string Source,
    IReadOnlyList<UsageWindow> Windows,
    string? Credits,
    string? Note,
    string? Error,
    bool Stale,
    int? ResetCredits = null,
    DateTimeOffset? ResetCreditExpiresAt = null)
{
    public static UsageSnapshot Empty(string? error = null)
        => new(DateTimeOffset.MinValue, "cache", Array.Empty<UsageWindow>(), null, null, error, true);
}
