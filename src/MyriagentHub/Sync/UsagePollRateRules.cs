namespace MyriagentHub.Sync;

/// <summary><see cref="UsagePollsSummary"/> 的一個邏輯帳戶（只有 id，不含 email）</summary>
internal sealed record UsagePollCount(string LogicalAccountId, int Count);

/// <summary>ops/status 的 <c>usagePollsPerHour</c>：各帳戶最近一小時的次數（依 id 排序、0 次的不列）與其中最大的</summary>
internal sealed record UsagePollsSummary(int Max, IReadOnlyList<UsagePollCount> Accounts);

/// <summary>
/// S12 的伺服器指標 <c>usagePollsPerHour</c>（設計 1.3、14.10）：每個邏輯帳戶最近一小時<b>被採用的帶 term 用量上傳</b>次數。
/// 伺服器看不到裝置對官方端點的查詢；負責查詢的那台每查一次就帶 term 上傳一次，所以拿被採用的帶 term 上傳當查詢次數的代理值。
/// 不算：不帶 term 的上傳（對話中的 rate limit 事件，不是查詢）、沒被採用的（舊 term、不是負責的那台、沒有連結）、
/// 裝置查了卻沒上傳的（上傳失敗、伺服器連不上）——裝置本機的查詢次數仍以長情境 <c>UsageAssignmentTests</c> 為準。
/// 計數只在記憶體（<see cref="UsagePollStats"/>），服務重新啟動就歸零。
/// </summary>
internal static class UsagePollRateRules
{
    /// <summary>滑動窗：一小時</summary>
    public const long WindowMs = 3_600_000;

    /// <summary>每個帳戶最多記這麼多筆（每秒一次持續一小時）；再多就丟最舊的，次數以這個上限計</summary>
    public const int MaxSamplesPerAccount = 3600;

    /// <summary>還在窗內：距 <paramref name="now"/> 不到一小時（剛好一小時的算過期；時鐘倒退、比 now 晚的照算）</summary>
    public static bool InWindow(long at, long now) => now - at < WindowMs;

    /// <summary>去掉過期的（照原順序）</summary>
    public static IReadOnlyList<long> Prune(IReadOnlyList<long> samples, long now)
    {
        var kept = new List<long>(samples.Count);
        foreach (var at in samples)
        {
            if (InWindow(at, now))
                kept.Add(at);
        }

        return kept;
    }

    /// <summary>記一次採用：去掉過期的再加上 <paramref name="now"/>；超過 <see cref="MaxSamplesPerAccount"/> 就丟最舊的</summary>
    public static IReadOnlyList<long> Add(IReadOnlyList<long> samples, long now)
    {
        var kept = Prune(samples, now);
        var skip = Math.Max(0, kept.Count + 1 - MaxSamplesPerAccount);
        var next = new List<long>(kept.Count + 1 - skip);
        for (var i = skip; i < kept.Count; i++)
            next.Add(kept[i]);
        next.Add(now);
        return next;
    }

    /// <summary>彙整：每個帳戶窗內的次數（0 次的不列、依 id 以序數排序）與最大值（沒有任何帳戶時 0）</summary>
    public static UsagePollsSummary Summarize(IReadOnlyDictionary<string, IReadOnlyList<long>> samples, long now)
    {
        var accounts = samples
            .Select(pair => new UsagePollCount(pair.Key, pair.Value.Count(at => InWindow(at, now))))
            .Where(c => c.Count > 0)
            .OrderBy(c => c.LogicalAccountId, StringComparer.Ordinal)
            .ToList();
        return new UsagePollsSummary(accounts.Count == 0 ? 0 : accounts.Max(c => c.Count), accounts);
    }
}
