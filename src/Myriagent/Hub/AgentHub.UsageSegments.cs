using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 儀表板的「每週額度每 5% 的 token」（UsageSegmentRules、UsageSegmentStore；web-protocol 的 usageSegments）：
/// AccountManager 每存下一份用量快照（UsageObserved）就看每週額度窗有沒有跨過下一個 5% 的邊界，跨過就把這一段各模型用掉的 token 結成一筆、
/// 附加到 usage-segments\records.jsonl；會話的每一筆 token 增量（AgentTokenUsageEvent）加到這個帳戶正在記的每一段。
/// 只送給這台的主視窗（彈出視窗、同步鏡像不送；瀏覽器版不支援）
/// </summary>
internal sealed partial class AgentHub
{
    private readonly UsageSegmentStore usageSegments = new(AppPaths.UsageSegmentsDirectory);

    /// <summary>要過分段紀錄的檢視：結了新的一筆時主動重送</summary>
    private readonly HashSet<string> usageSegmentViewIds = new(StringComparer.Ordinal);

    /// <summary>畫面上最多列幾筆紀錄（彙總照樣算全部）</summary>
    internal const int UsageSegmentRecentLimit = 200;

    /// <summary>分段紀錄用的時間（測試可換）</summary>
    internal Func<DateTimeOffset> SegmentClock { get; set; } = () => DateTimeOffset.Now;

    private void RecordSegmentTokens(string accountId, string? model, AgentUsage delta, long cacheWrite)
        => usageSegments.AddTokens(accountId, model, delta, cacheWrite);

    private void OnUsageObserved(string accountId, UsageSnapshot snapshot)
    {
        if (disposed || accounts.Find(accountId) is not { } account)
            return;
        var closed = usageSegments.Observe(account, snapshot, SegmentClock());
        if (closed.Count == 0)
            return;
        foreach (var r in closed)
        {
            var tokens = UsageSegmentRules.Total(r.Models);
            Log.Info($"每週額度 {r.FromBucket:0}%→{r.ToBucket:0}%：{account.Label} {r.WindowLabel}（{r.StartPercent:0.#}%→{r.EndPercent:0.#}%）"
                     + $" 輸入 {tokens.Input:N0}、輸出 {tokens.Output:N0}{(r.Complete ? string.Empty : "（不完整的一段）")}");
        }
        PostUsageSegments();
    }

    /// <summary>usageSegments 指令（切到儀表板、每分鐘）：回這個檢視，之後結了新的一筆也主動送給它</summary>
    private void RequestUsageSegments(IAgentView view)
    {
        if (view.Id == SyncViewId || view.IsPopout)
            return;
        usageSegmentViewIds.Add(view.Id);
        TryPost(new[] { view }, RenderUsageSegmentsMessage);
    }

    private void PostUsageSegments()
    {
        var targets = views.Values.Where(v => usageSegmentViewIds.Contains(v.Id) && v.Id != SyncViewId && !v.IsPopout).ToList();
        if (targets.Count > 0)
            TryPost(targets, RenderUsageSegmentsMessage);
    }

    private JObject RenderUsageSegmentsMessage() => new() { ["type"] = "usageSegments", ["usageSegments"] = RenderUsageSegments() };

    /// <summary>usageSegments 訊息的內容（web-protocol 的 UsageSegments）：彙總、正在記的各段、最近的紀錄（新的在前）</summary>
    internal JObject RenderUsageSegments()
    {
        var records = usageSegments.Records();
        var labels = accounts.Accounts.ToDictionary(a => a.Id, a => a, StringComparer.Ordinal);
        var summary = new JArray();
        foreach (var g in UsageSegmentRules.Summarize(records))
        {
            summary.Add(new JObject
            {
                ["accountId"] = g.AccountId,
                ["accountLabel"] = labels.TryGetValue(g.AccountId, out var a) ? a.Label : g.AccountLabel,
                ["provider"] = g.Provider,
                ["windowKey"] = g.WindowKey,
                ["windowLabel"] = g.WindowLabel,
                ["model"] = g.Model,
                ["records"] = g.Records,
                ["percent"] = Math.Round(g.Percent, 2),
                ["tokens"] = RenderSegmentTokens(g.Tokens),
                ["per5"] = new JObject
                {
                    ["input"] = UsageSegmentRules.Per5(g.Tokens.Input, g.Percent),
                    ["cached"] = UsageSegmentRules.Per5(g.Tokens.Cached, g.Percent),
                    ["cacheWrite"] = UsageSegmentRules.Per5(g.Tokens.CacheWrite, g.Percent),
                    ["output"] = UsageSegmentRules.Per5(g.Tokens.Output, g.Percent),
                },
                ["minPer5"] = (long)Math.Round(g.MinPer5),
                ["maxPer5"] = (long)Math.Round(g.MaxPer5),
                ["lastAt"] = g.LastAt.ToUnixTimeMilliseconds(),
            });
        }
        var open = new JArray();
        foreach (var s in usageSegments.OpenSegments()
                     .Where(s => labels.ContainsKey(s.AccountId))
                     .OrderBy(s => labels[s.AccountId].Order).ThenBy(s => s.WindowKey, StringComparer.Ordinal))
        {
            var account = labels[s.AccountId];
            open.Add(new JObject
            {
                ["accountId"] = s.AccountId,
                ["accountLabel"] = account.Label,
                ["provider"] = account.Provider.ToString(),
                ["windowKey"] = s.WindowKey,
                ["windowLabel"] = s.WindowLabel,
                ["bucket"] = s.Bucket,
                ["startPercent"] = s.StartPercent,
                ["lastPercent"] = s.LastPercent,
                ["startedAt"] = s.StartedAt.ToUnixTimeMilliseconds(),
                ["lastSeenAt"] = s.LastSeenAt.ToUnixTimeMilliseconds(),
                ["complete"] = s.Complete,
                ["models"] = RenderSegmentModels(s.Models),
            });
        }
        var recent = new JArray();
        foreach (var r in records.Reverse().Take(UsageSegmentRecentLimit))
        {
            recent.Add(new JObject
            {
                ["id"] = r.Id,
                ["accountId"] = r.AccountId,
                ["accountLabel"] = labels.TryGetValue(r.AccountId, out var a) ? a.Label : r.AccountLabel,
                ["provider"] = r.Provider,
                ["windowKey"] = r.WindowKey,
                ["windowLabel"] = r.WindowLabel,
                ["fromBucket"] = r.FromBucket,
                ["toBucket"] = r.ToBucket,
                ["startPercent"] = r.StartPercent,
                ["endPercent"] = r.EndPercent,
                ["startedAt"] = r.StartedAt.ToUnixTimeMilliseconds(),
                ["endedAt"] = r.EndedAt.ToUnixTimeMilliseconds(),
                ["complete"] = r.Complete,
                ["model"] = UsageSegmentRules.DominantModel(r.Models),
                ["models"] = RenderSegmentModels(r.Models),
            });
        }
        return new JObject
        {
            ["path"] = usageSegments.RecordsPath,
            ["total"] = records.Count,
            ["summary"] = summary,
            ["open"] = open,
            ["records"] = recent,
        };
    }

    private static JObject RenderSegmentTokens(SegmentTokens t) => new()
    {
        ["input"] = t.Input,
        ["cached"] = t.Cached,
        ["cacheWrite"] = t.CacheWrite,
        ["output"] = t.Output,
    };

    /// <summary>各模型（輸入＋輸出多的在前）</summary>
    private static JArray RenderSegmentModels(IReadOnlyDictionary<string, SegmentTokens> models)
    {
        var list = new JArray();
        foreach (var (name, t) in models.OrderByDescending(kv => kv.Value.Input + kv.Value.Output).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var o = RenderSegmentTokens(t);
            o.AddFirst(new JProperty("model", name));
            list.Add(o);
        }
        return list;
    }
}
