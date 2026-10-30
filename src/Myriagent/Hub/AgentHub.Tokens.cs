using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Sync;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 側欄帳戶卡的 token 計數：會話回報的增量（AgentTokenUsageEvent）依對話的帳戶記進 TokenTallyStore（本機日期），同一筆也記進每週額度的 5% 分段
/// （AgentHub.UsageSegments.cs）。每一筆用量只由會話發一次：一輪結束時不再以這一輪的用量補差額（會把中樞已提早收尾的那一輪再記一次，見 AgentTokenUsageEvent）。
/// 網頁收 tokens 訊息（docs/web-protocol.md），跟其他更新一起每 50 毫秒合併；
/// 同一個時間點通知同步（ISyncBridge.OnTokensChanged，tok1），引擎自己取複本（TokenTallySnapshot）上傳給瀏覽器版
/// </summary>
internal sealed partial class AgentHub
{
    private readonly TokenTallyStore tokenTally = new(AppPaths.TokenTallyFile);
    private bool tokensDirty;

    /// <summary>這台的計數沒變、只是要重送 tokens（其他電腦的計數或目錄的連結變了）：不通知同步</summary>
    private bool tokensViewDirty;

    /// <summary>上次送出的 tokens 訊息的日期（跨日時重送，今天的數字歸零）</summary>
    private string tokensDay = TokenTallyRules.DayKey(DateTime.Now);

    /// <summary>記 token 用的時間（本機日期決定記在哪一天；測試可換）</summary>
    internal Func<DateTime> TokenClock { get; set; } = () => DateTime.Now;

    private void OnTokenUsage(ConversationState c, AgentTokenUsageEvent tokens)
    {
        var delta = tokens.Delta;
        c.TurnTokens = TokenTallyRules.Add(c.TurnTokens, delta);
        RecordTokens(c.Meta.AccountId, delta);
        // 不知道模型（舊的會話、CLI 沒回報）時用對話選的模型（「預設」是 null：記成未知的模型）
        RecordSegmentTokens(c.Meta.AccountId, tokens.Model ?? UsageSegmentRules.ModelOfConversation(c.Meta.Model), delta, tokens.CacheWriteTokens);
        // 輸入框下方那一行在一輪進行中就跟著跳（TurnTokenRules）：只重送給開著的檢視，和其他標頭變動一起合併送出
        if (c.Running && !TokenTallyRules.IsZero(delta))
            MarkHeaderViewDirty(c);
    }

    /// <summary>一輪結束：這一輪即時記到的清掉（下一輪從 0 累加）。不補記：每一筆用量都已由會話以 AgentTokenUsageEvent 發過</summary>
    private static void CloseTurnTokens(ConversationState c) => c.TurnTokens = null;

    private void RecordTokens(string accountId, AgentUsage delta)
    {
        if (TokenTallyRules.IsZero(delta))
            return;
        tokenTally.Add(accountId, TokenClock(), delta);
        tokensDirty = true;
        ScheduleFlush();
    }

    /// <summary>維護計時器（30 秒）：跨日時重送一次，並把有變動的計數寫回磁碟</summary>
    private void TickTokens(DateTime now)
    {
        if (TokenTallyRules.DayKey(now) != tokensDay)
        {
            tokensDirty = true;
            ScheduleFlush();
        }
        tokenTally.Flush();
        usageSegments.Flush();
    }

    private void FlushTokens()
    {
        if (!tokensDirty && !tokensViewDirty)
            return;
        var changed = tokensDirty;
        tokensDirty = false;
        tokensViewDirty = false;
        TryPost(views.Values.ToList(), RenderTokensMessage);
        if (changed)
            sync?.OnTokensChanged();
    }

    /// <summary>〔同步〕其他電腦的計數或目錄的連結變了（協調者呼叫）：重送 tokens，不通知同步（這台的計數沒變）</summary>
    internal void RefreshTokens()
    {
        tokensViewDirty = true;
        ScheduleFlush();
    }

    /// <summary>計數的複本（本機帳戶 × 本機日期；同步引擎上傳用，任何執行緒）</summary>
    internal List<LocalTokenTally> TokenTallySnapshot() => tokenTally.Snapshot();

    private JObject RenderTokensMessage() => new() { ["type"] = "tokens", ["tokens"] = RenderTokens() };

    /// <summary>
    /// 各本機帳戶的 token 計數（docs/web-protocol.md 的 Tokens）：今天、近 7 天、近 30 天（含今天），各是輸入（含快取）、快取讀取、輸出。
    /// 〔同步〕收到過其他電腦的計數時，連結到帳戶目錄的帳戶改成所有電腦的合計、加 here（這台今天的），remoteOnly 的帳戶也列（ISyncBridge.DecorateTokens）
    /// </summary>
    internal JObject RenderTokens()
    {
        var now = TokenClock();
        tokensDay = TokenTallyRules.DayKey(now);
        var list = new JObject();
        foreach (var account in accounts.Accounts)
        {
            list[account.Id] = new JObject
            {
                ["today"] = RenderTokenCount(tokenTally.Sum(account.Id, now, 1)),
                ["week"] = RenderTokenCount(tokenTally.Sum(account.Id, now, 7)),
                ["month"] = RenderTokenCount(tokenTally.Sum(account.Id, now, TokenTallyRules.RetentionDays)),
            };
        }
        var tokens = new JObject { ["day"] = tokensDay, ["accounts"] = list };
        sync?.DecorateTokens(tokens, now);
        return tokens;
    }

    private static JObject RenderTokenCount(AgentUsage usage)
        => new() { ["input"] = usage.InputTokens, ["cached"] = usage.CachedInputTokens, ["output"] = usage.OutputTokens };
}
