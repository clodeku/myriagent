namespace MyriagentHub.Sync;

/// <summary>
/// <c>usagePollsPerHour</c> 的計數（<see cref="UsagePollRateRules"/>）：只在記憶體，服務重新啟動就歸零。
/// 寫入執行緒在帶 term 的上傳提交之後記（<see cref="UsageCoordinator"/>），ops/status 從要求的執行緒讀，所以加鎖。
/// </summary>
internal sealed class UsagePollStats
{
    private readonly object gate = new();
    private readonly Dictionary<string, IReadOnlyList<long>> samples = new(StringComparer.Ordinal);

    public void Record(string logicalAccountId, long now)
    {
        lock (gate)
            samples[logicalAccountId] = UsagePollRateRules.Add(samples.GetValueOrDefault(logicalAccountId) ?? [], now);
    }

    /// <summary>目前一小時窗的彙整；順便丟掉過期的紀錄（帳戶一小時沒有上傳就不再佔記憶體）</summary>
    public UsagePollsSummary Snapshot(long now)
    {
        lock (gate)
        {
            foreach (var id in samples.Keys.ToList())
            {
                var kept = UsagePollRateRules.Prune(samples[id], now);
                if (kept.Count == 0)
                    samples.Remove(id);
                else
                    samples[id] = kept;
            }

            return UsagePollRateRules.Summarize(samples, now);
        }
    }
}
