using Myriagent.Agents;

namespace Myriagent.Usage;

/// <summary>一段期間裡某個模型用掉的 token（慣例同 AgentUsage：Input 含快取讀取與快取寫入，Cached＝快取讀取，CacheWrite＝快取寫入〔只有 Claude〕）</summary>
internal sealed class SegmentTokens
{
    public long Input { get; set; }

    public long Cached { get; set; }

    public long CacheWrite { get; set; }

    public long Output { get; set; }

    public SegmentTokens Clone() => new() { Input = Input, Cached = Cached, CacheWrite = CacheWrite, Output = Output };
}

/// <summary>
/// 一個帳戶的一個每週額度窗目前在記的這一段（open.json；UsageSegmentRules）：從使用率跨過某個 5% 的邊界起，累加這個帳戶在這台 app 用掉的 token，
/// 到跨過下一個邊界時結成一筆 <see cref="UsageSegmentRecord"/>
/// </summary>
internal sealed class UsageSegmentOpen
{
    public string AccountId { get; set; } = string.Empty;

    public string WindowKey { get; set; } = string.Empty;

    public string WindowLabel { get; set; } = string.Empty;

    /// <summary>這一段的起點邊界（0、5、10…）</summary>
    public double Bucket { get; set; }

    /// <summary>這一段開始時看到的使用率（跨過邊界的那次觀察；可能比邊界多一點）</summary>
    public double StartPercent { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    /// <summary>
    /// 這一段是從「看到跨過邊界」（或額度重置後的 0%）開始的：結成的紀錄是完整的一段。
    /// 開始追蹤時（第一次看到這個額度窗、額度重置後第一次看到就已經不是 0%）已在一段的中間，是 false
    /// </summary>
    public bool Complete { get; set; }

    /// <summary>這一週的重置時間（看到的；換週判斷用）</summary>
    public DateTimeOffset? ResetsAt { get; set; }

    public double LastPercent { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>這一段到目前為止各模型用掉的 token</summary>
    public Dictionary<string, SegmentTokens> Models { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>結好的一段（records.jsonl 一行一筆；欄位只加不改，舊版不讀這個檔）</summary>
internal sealed class UsageSegmentRecord
{
    public string Id { get; set; } = string.Empty;

    public string AccountId { get; set; } = string.Empty;

    public string AccountLabel { get; set; } = string.Empty;

    /// <summary>代理種類的名稱（Claude、Codex、Antigravity、Grok）</summary>
    public string Provider { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string WindowKey { get; set; } = string.Empty;

    public string WindowLabel { get; set; } = string.Empty;

    /// <summary>起點邊界（例 15）</summary>
    public double FromBucket { get; set; }

    /// <summary>終點邊界（例 20；一次跳過兩個邊界時是 25）</summary>
    public double ToBucket { get; set; }

    /// <summary>開始時看到的使用率</summary>
    public double StartPercent { get; set; }

    /// <summary>結束（跨過終點邊界）時看到的使用率</summary>
    public double EndPercent { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset EndedAt { get; set; }

    public DateTimeOffset? ResetsAt { get; set; }

    /// <summary>從跨過起點邊界開始記的完整一段（false：開始追蹤時已在這一段的中間，token 只算到一部分）</summary>
    public bool Complete { get; set; }

    /// <summary>各模型用掉的 token</summary>
    public Dictionary<string, SegmentTokens> Models { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 每週額度每 5% 的 token 分段紀錄的純規則（用途：比較不同帳戶、不同模型「每 1% 的每週額度能用多少 token」是不是同一個基準）。
/// <list type="bullet">
/// <item>只看每週的額度窗（<see cref="IsWeekly"/>：Claude 的每週與每週・模型、Codex 10080 分鐘的、Antigravity 與 Grok 標成每週的）。</item>
/// <item>使用率由 AccountManager 每次存下的快照觀察（定期查詢、對話中的 rate_limit_event／rateLimits/updated）；跨過下一個 5% 的邊界（<see cref="BucketOf"/>）就結一筆，
/// 記開始與結束時看到的使用率（分析時用實際的差，不假設剛好 5%）。一次跳過好幾個邊界時結成一筆、終點是最後跨過的邊界。</item>
/// <item>換週（重置時間往後移超過一天、或使用率掉了超過 3 個百分點）：進行中的那段丟掉（跨週的 token 不能算進任何一週的 5%），從新的這週重新開始。</item>
/// <item>token 是這個帳戶在這台 app 經手的對話（AgentTokenUsageEvent）。同一個帳戶在其他電腦、終端機的 CLI、claude.ai 用掉的額度也會讓使用率上升，但 token 不在這裡：
/// 那種段落每 1% 的 token 會偏少，紀錄本身看不出來（畫面與文件寫明）。</item>
/// </list>
/// </summary>
internal static class UsageSegmentRules
{
    /// <summary>一段的寬度（百分點）</summary>
    public const double Step = 5;

    /// <summary>使用率比上次看到的低這麼多以上就當成換週（不同來源的四捨五入可能差一點點）</summary>
    public const double ResetDrop = 3;

    /// <summary>重置時間往後移超過這麼久就當成換週（同一週的重置時間在不同來源之間可能差幾分鐘）</summary>
    public static readonly TimeSpan ResetShift = TimeSpan.FromDays(1);

    /// <summary>不知道模型時記成這個名稱</summary>
    public const string UnknownModel = "（未知的模型）";

    /// <summary>每週的額度窗：key 是 weekly／weekly:模型（Claude）、結尾是 :10080（Codex），或名稱以「每週」開頭（UsageRules 產生的名稱；Antigravity、Grok）</summary>
    public static bool IsWeekly(UsageWindow window)
        => window.Key == UsageRules.WeeklyKey
           || window.Key.StartsWith(UsageRules.WeeklyKey + ":", StringComparison.Ordinal)
           || window.Key.EndsWith(":10080", StringComparison.Ordinal)
           || window.Label.StartsWith("每週", StringComparison.Ordinal);

    /// <summary>使用率所在那一段的起點邊界：0～4.99→0、5～9.99→5…，100（用滿）→100</summary>
    public static double BucketOf(double percent)
    {
        var p = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
        return Math.Floor(p / Step + 1e-9) * Step;
    }

    /// <summary>open.json 與記憶體裡的鍵</summary>
    public static string KeyOf(string accountId, string windowKey) => accountId + "|" + windowKey;

    /// <summary>
    /// 看到一個每週額度窗的使用率：回傳之後要留著的那一段，以及結好的一筆（沒有跨過邊界是 null）。
    /// <paramref name="open"/> 是 null（第一次看到）時從這裡開始記：使用率是 0 才算完整的一段。
    /// </summary>
    public static (UsageSegmentOpen Next, UsageSegmentRecord? Closed) Observe(UsageSegmentOpen? open, string accountId, UsageWindow window,
        DateTimeOffset now, Func<UsageSegmentOpen, UsageWindow, DateTimeOffset, UsageSegmentRecord> closeRecord)
    {
        var percent = double.IsFinite(window.Percent) ? Math.Clamp(window.Percent, 0, 100) : 0;
        if (open == null || IsNewWeek(open, window, percent))
            return (Start(accountId, window, percent, now, complete: percent <= 0, resetsAt: window.ResetsAt), null);

        var bucket = BucketOf(percent);
        if (bucket > open.Bucket)
        {
            var record = closeRecord(open, window, now);
            record.FromBucket = open.Bucket;
            record.ToBucket = bucket;
            record.StartPercent = open.StartPercent;
            record.EndPercent = percent;
            record.StartedAt = open.StartedAt;
            record.EndedAt = now;
            record.ResetsAt = window.ResetsAt ?? open.ResetsAt;
            record.Complete = open.Complete;
            record.WindowKey = open.WindowKey;
            record.WindowLabel = string.IsNullOrEmpty(open.WindowLabel) ? window.Label : open.WindowLabel;
            record.Models = open.Models.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.Ordinal);
            return (Start(accountId, window, percent, now, complete: true, resetsAt: window.ResetsAt ?? open.ResetsAt), record);
        }

        open.LastPercent = percent;
        open.LastSeenAt = now;
        if (window.ResetsAt is { } resets)
            open.ResetsAt = resets;
        if (!string.IsNullOrEmpty(window.Label))
            open.WindowLabel = window.Label;
        return (open, null);
    }

    /// <summary>換週：重置時間往後移超過一天，或使用率比上次看到的低超過 3 個百分點</summary>
    public static bool IsNewWeek(UsageSegmentOpen open, UsageWindow window, double percent)
    {
        if (window.ResetsAt is { } resets && open.ResetsAt is { } before && resets - before > ResetShift)
            return true;
        return percent < open.LastPercent - ResetDrop;
    }

    private static UsageSegmentOpen Start(string accountId, UsageWindow window, double percent, DateTimeOffset now, bool complete, DateTimeOffset? resetsAt)
        => new()
        {
            AccountId = accountId,
            WindowKey = window.Key,
            WindowLabel = window.Label,
            Bucket = BucketOf(percent),
            StartPercent = percent,
            StartedAt = now,
            Complete = complete,
            ResetsAt = resetsAt,
            LastPercent = percent,
            LastSeenAt = now,
        };

    /// <summary>把一筆增量加到這一段的這個模型（負的當 0；Input 含快取，CacheWrite 是其中的快取寫入）</summary>
    public static void AddTokens(UsageSegmentOpen open, string? model, AgentUsage delta, long cacheWrite)
    {
        var name = string.IsNullOrWhiteSpace(model) ? UnknownModel : model.Trim();
        if (!open.Models.TryGetValue(name, out var tokens))
        {
            tokens = new SegmentTokens();
            open.Models[name] = tokens;
        }
        tokens.Input += Math.Max(0, delta.InputTokens);
        tokens.Cached += Math.Max(0, delta.CachedInputTokens);
        tokens.CacheWrite += Math.Max(0, cacheWrite);
        tokens.Output += Math.Max(0, delta.OutputTokens);
    }

    /// <summary>各模型相加</summary>
    public static SegmentTokens Total(IReadOnlyDictionary<string, SegmentTokens> models)
    {
        var total = new SegmentTokens();
        foreach (var t in models.Values)
            Accumulate(total, t);
        return total;
    }

    private static void Accumulate(SegmentTokens into, SegmentTokens add)
    {
        into.Input += add.Input;
        into.Cached += add.Cached;
        into.CacheWrite += add.CacheWrite;
        into.Output += add.Output;
    }

    /// <summary>會話沒說是哪個模型時用對話選的模型；沒選（null、空的、「default」＝帳戶預設）回 null（記成未知的模型）</summary>
    public static string? ModelOfConversation(string? model)
        => string.IsNullOrWhiteSpace(model) || string.Equals(model, "default", StringComparison.OrdinalIgnoreCase) ? null : model;

    /// <summary>一筆紀錄算成某個模型的：輸入＋輸出佔這一段九成以上的那個模型（其他模型只是零星的子代理、標題等）；沒有就是混用</summary>
    public const double DominantShare = 0.9;

    public const string MixedModel = "（混用）";

    /// <summary>這一段主要是哪個模型（<see cref="DominantShare"/>）；沒有 token 的是未知的模型</summary>
    public static string DominantModel(IReadOnlyDictionary<string, SegmentTokens> models)
    {
        long total = 0;
        string? best = null;
        long bestWeight = -1;
        foreach (var (name, t) in models.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var weight = t.Input + t.Output;
            total += weight;
            if (weight > bestWeight)
            {
                best = name;
                bestWeight = weight;
            }
        }
        if (total <= 0 || best == null)
            return UnknownModel;
        return bestWeight >= total * DominantShare ? best : MixedModel;
    }

    /// <summary>
    /// 彙總（儀表板的表）：只算完整的段落（<see cref="UsageSegmentRecord.Complete"/>）、使用率有前進的；依帳戶 × 額度窗 × 主要模型分組，
    /// 每 5% 的 token＝這組的 token 合計 × 5 ÷ 這組實際前進的百分點合計（不假設每筆剛好 5%），另給各筆每 5% 的輸入＋輸出的最小與最大值看差異
    /// </summary>
    public static List<UsageSegmentSummary> Summarize(IEnumerable<UsageSegmentRecord> records)
    {
        var groups = new Dictionary<(string, string, string), UsageSegmentSummary>();
        foreach (var r in records)
        {
            var span = r.EndPercent - r.StartPercent;
            if (!r.Complete || !(span > 0))
                continue;
            var model = DominantModel(r.Models);
            var key = (r.AccountId, r.WindowKey, model);
            var tokens = Total(r.Models);
            var per5 = (tokens.Input + tokens.Output) * Step / span;
            if (!groups.TryGetValue(key, out var g))
            {
                g = new UsageSegmentSummary { AccountId = r.AccountId, WindowKey = r.WindowKey, Model = model, MinPer5 = per5, MaxPer5 = per5 };
                groups[key] = g;
            }
            // 名稱以最新的一筆為準（帳戶可能改過名）
            if (r.EndedAt >= g.LastAt)
            {
                g.LastAt = r.EndedAt;
                g.AccountLabel = r.AccountLabel;
                g.Provider = r.Provider;
                g.WindowLabel = r.WindowLabel;
            }
            g.Records++;
            g.Percent += span;
            Accumulate(g.Tokens, tokens);
            g.MinPer5 = Math.Min(g.MinPer5, per5);
            g.MaxPer5 = Math.Max(g.MaxPer5, per5);
        }
        return groups.Values
            .OrderBy(g => g.AccountLabel, StringComparer.Ordinal)
            .ThenBy(g => g.AccountId, StringComparer.Ordinal)
            .ThenBy(g => g.WindowKey, StringComparer.Ordinal)
            .ThenBy(g => g.Model, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>每 5% 的 token（合計 × 5 ÷ 實際前進的百分點）</summary>
    public static long Per5(long tokens, double percent) => percent > 0 ? (long)Math.Round(tokens * Step / percent) : 0;
}

/// <summary>儀表板的一列彙總（UsageSegmentRules.Summarize）</summary>
internal sealed class UsageSegmentSummary
{
    public string AccountId { get; set; } = string.Empty;

    public string AccountLabel { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string WindowKey { get; set; } = string.Empty;

    public string WindowLabel { get; set; } = string.Empty;

    /// <summary>主要模型（或混用）</summary>
    public string Model { get; set; } = string.Empty;

    public int Records { get; set; }

    /// <summary>實際前進的百分點合計</summary>
    public double Percent { get; set; }

    public SegmentTokens Tokens { get; set; } = new();

    /// <summary>各筆每 5% 的輸入＋輸出：最小、最大</summary>
    public double MinPer5 { get; set; }

    public double MaxPer5 { get; set; }

    public DateTimeOffset LastAt { get; set; }
}
