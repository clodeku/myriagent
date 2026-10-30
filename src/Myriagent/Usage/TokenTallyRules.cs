using System.Globalization;
using Myriagent.Agents;

namespace Myriagent.Usage;

/// <summary>
/// 側欄帳戶卡的 token 計數（只供顯示）：各家 CLI 每次 API 呼叫回報的用量換成增量（AgentTokenUsageEvent），中樞依帳戶、依本機日期累加。
/// 數字一律來自 CLI 回報的真實用量：算不出增量時寧可少算（例如 Claude 程序的第一輪沒有 modelUsage 的基準），不推估、不重複計算。
/// 慣例同 AgentUsage：輸入含快取（Claude 是 input＋cache_creation＋cache_read），Cached 是其中的快取讀取，總數＝輸入＋輸出
/// </summary>
internal static class TokenTallyRules
{
    /// <summary>存檔保留的天數（今天算一天）</summary>
    public const int RetentionDays = 30;

    public static readonly AgentUsage Zero = new(0, 0, 0, null);

    public static long Total(AgentUsage? usage) => usage == null ? 0 : usage.InputTokens + usage.OutputTokens;

    public static bool IsZero(AgentUsage? usage)
        => usage == null || (usage.InputTokens == 0 && usage.OutputTokens == 0 && usage.CachedInputTokens == 0);

    /// <summary>兩份用量相加（null 當成 0；CostUsd 不加，一律 null）</summary>
    public static AgentUsage Add(AgentUsage? a, AgentUsage? b)
        => new((a?.InputTokens ?? 0) + (b?.InputTokens ?? 0), (a?.OutputTokens ?? 0) + (b?.OutputTokens ?? 0),
            (a?.CachedInputTokens ?? 0) + (b?.CachedInputTokens ?? 0), null);

    /// <summary>
    /// 累計值從 <paramref name="previous"/> 變成 <paramref name="current"/> 的增量：逐欄相減、負的當 0
    /// （同一則訊息的用量再回報一次是 0；累計值只會變大，變小代表換了基準，不倒扣）
    /// </summary>
    public static AgentUsage Increment(AgentUsage? previous, AgentUsage current)
        => new(Math.Max(0, current.InputTokens - (previous?.InputTokens ?? 0)),
            Math.Max(0, current.OutputTokens - (previous?.OutputTokens ?? 0)),
            Math.Max(0, current.CachedInputTokens - (previous?.CachedInputTokens ?? 0)), null);

    /// <summary>
    /// 一輪結束時要補記的量：這一輪的用量（AgentTurnCompletedEvent.Usage）比這一輪已記的增量多出的部分（逐欄、負的當 0）。
    /// 有即時增量的 CLI 補 0；Grok 只在結束時有用量，整份補上
    /// </summary>
    public static AgentUsage TopUp(AgentUsage? turnUsage, AgentUsage? counted)
        => turnUsage == null ? Zero : Increment(counted, turnUsage);

    /// <summary>
    /// Codex 的 thread/tokenUsage/updated → 增量。total 是整個 thread 的累計（換程序 thread/resume 時會先送一則恢復的累計）、last 只是最後一次 API 呼叫：
    /// <list type="bullet">
    /// <item>這個 thread 有上一則的累計：total 的差。</item>
    /// <item>第一次看到這個 thread：通知屬於正在進行的這一輪（<paramref name="countFirst"/>）才記 last；不是（續接時恢復的累計）只當基準、記 0。</item>
    /// <item>沒有 total（舊版）：屬於這一輪就記 last。</item>
    /// </list>
    /// </summary>
    public static AgentUsage CodexIncrement(AgentUsage? previousTotal, AgentUsage? total, AgentUsage? last, bool countFirst)
    {
        if (total == null)
            return countFirst && last != null ? last with { CostUsd = null } : Zero;
        if (previousTotal != null)
            return Increment(previousTotal, total);
        return countFirst && last != null ? last with { CostUsd = null } : Zero;
    }

    /// <summary>本機日期的鍵（yyyy-MM-dd）</summary>
    public static string DayKey(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>從 <paramref name="today"/> 往回 <paramref name="days"/> 天（含今天）的日期鍵</summary>
    public static IEnumerable<string> DayKeys(DateTime today, int days)
    {
        for (int i = 0; i < days; i++)
            yield return DayKey(today.Date.AddDays(-i));
    }

    /// <summary>超過保留天數的日期鍵（比今天晚的不刪：使用者把時鐘往回調時，往後的資料留著）</summary>
    public static List<string> ExpiredDays(IEnumerable<string> keys, DateTime today)
    {
        var keep = DayKeys(today, RetentionDays).ToHashSet(StringComparer.Ordinal);
        var todayKey = DayKey(today);
        return keys.Where(k => !keep.Contains(k) && string.CompareOrdinal(k, todayKey) < 0).ToList();
    }
}
