using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 背景工作（Claude 的 run_in_background 指令、背景子代理；Codex 一輪結束時還在跑的指令）：代理這一輪結束了、背景工作還在跑時，對話仍算在進行——
/// 畫面以「等背景工作回報」的樣貌顯示（標頭與清單的 background，web-protocol），系統匣與結束確認也算它。
/// 背景工作完成後 Claude 的 CLI 自己開一輪（AgentIdleTurnStartedEvent）；Codex 不會，由這裡隱藏送出接續訊息（AgentBackgroundCommandFinishedEvent）。
/// 接續的那一輪照一般的執行中顯示
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>會話最近一次回報的背景工作（AgentBackgroundWorkEvent）與回報它的會話：換了會話（或會話已沒有背景工作）就不算</summary>
        public AgentBackgroundWorkEvent? BackgroundWork;
        public IAgentSession? BackgroundWorkSession;

        /// <summary>
        /// BackgroundWork 各工具的開始時間（同順序，BackgroundWorkRules.Starts）與這個會話開始有背景工作的時間（對不到工具的只能用它）：
        /// 標頭的 background.startedAt，網頁顯示已經跑了多久。只在記憶體（app 重開時背景工作已被停掉）
        /// </summary>
        public List<DateTime> BackgroundStarts = new();
        public DateTime? BackgroundSince;

        /// <summary>Codex 的背景指令的工具卡（一輪結束時還在跑，留在執行中，之後的 AgentToolCompletedEvent 才結束它）</summary>
        public readonly Dictionary<string, AgentChatItem> BackgroundToolItems = new(StringComparer.Ordinal);

        /// <summary>跑完、還沒接續的背景指令（執行中跑完的等這一輪結束再接續）</summary>
        public readonly List<AgentBackgroundCommandFinishedEvent> PendingBackgroundFinished = new();

        /// <summary>從使用者上一次送出起，背景指令跑完自動接續了幾次（上限 BackgroundWorkRules.MaxAutoContinues）</summary>
        public int BackgroundContinues;

        /// <summary>
        /// CLI 換版停掉了這個對話的背景工作、還沒送出的確認訊息（AgentHub.Restart.cs 的 QueueBackgroundCheck）：CLI 更新完、對話閒置時送；
        /// 這段期間開始了別的一輪就丟掉（HookTurnStarted）
        /// </summary>
        public BackgroundCheckRequest? PendingBackgroundCheck;
    }

    /// <summary>換版停掉了背景工作之後要隱藏送出的確認訊息與它的提示</summary>
    private sealed record BackgroundCheckRequest(ContinuationContext Context, string Notice);

    /// <summary>代理這一輪已結束、背景工作還在跑的對話數（系統匣、結束確認）</summary>
    public int BackgroundWaitingCount => conversations.Values.Count(c => !c.Running && BackgroundOf(c) != null);

    /// <summary>
    /// 會話只在背景工作的清單變了才發（已去重），一律重送標頭與清單。不能拿「改變前」的畫面比對：工作數變成 0 時會話的
    /// HasLiveBackgroundWork 已經是 false，改變前算出來也是沒有背景工作，畫面就一直停在等背景工作回報
    /// </summary>
    private void OnBackgroundWork(ConversationState c, AgentBackgroundWorkEvent ev)
    {
        // 開始時間：同一個會話上一次回報就有的沿用，新的用工具卡建立的時間（換了會話：舊程序的背景工作都已結束，從頭算）
        var now = DateTime.Now;
        var previous = ReferenceEquals(c.BackgroundWorkSession, c.Session) ? c.BackgroundWork : null;
        c.BackgroundStarts = BackgroundWorkRules.Starts(previous?.Tools ?? Array.Empty<AgentBackgroundTool>(),
            previous != null ? c.BackgroundStarts : Array.Empty<DateTime>(), ev.Tools, now, i => BackgroundCardStart(c, ev, i));
        c.BackgroundSince = ev.Count < 1 ? null : previous is { Count: > 0 } && c.BackgroundSince is { } since ? since : now;
        c.BackgroundWork = ev;
        c.BackgroundWorkSession = c.Session;
        MarkHeaderDirty(c);
        MarkListDirty();
        UpdateTray();
        // running.json 的快照跟著背景工作的清單（等背景工作的對話：背景工作都沒了就刪，AgentHub.InterruptedTurns.cs）
        RefreshRunMarker(c);
    }

    /// <summary>
    /// 這個對話目前的背景工作；沒有回 null。只認目前的會話、而且會話仍回報有背景工作的
    /// （會話被換掉或關掉的路徑很多，不必每一處都記得清掉）
    /// </summary>
    private static AgentBackgroundWorkEvent? BackgroundOf(ConversationState c)
        => c.BackgroundWork is { Count: > 0 } ev && c.Session != null && ReferenceEquals(c.BackgroundWorkSession, c.Session) && c.Session.HasLiveBackgroundWork
            ? ev
            : null;

    /// <summary>標頭的 background（docs/web-protocol.md 的 Header.background）：沒有背景工作是 null</summary>
    private static JObject? RenderBackground(ConversationState c)
        => BackgroundOf(c) is { } ev ? BackgroundWorkRules.View(ev.Count, ev.Tools, c.BackgroundStarts, c.BackgroundSince) : null;

    /// <summary>
    /// 第幾個背景工作的工具卡建立的時間（BackgroundWorkRules.Starts 的 cardStart）：會話帶了和工具同順序的卡片 id 才對得到（Codex；
    /// Grok 每個都有 id 時），這一輪的卡片或之前幾輪留在執行中的背景指令卡片；對不到是 null
    /// </summary>
    private static DateTime? BackgroundCardStart(ConversationState c, AgentBackgroundWorkEvent ev, int index)
        => ev.ToolIds is { } ids && ids.Count == ev.Tools.Count && index < ids.Count
           && (c.ToolItems.TryGetValue(ids[index], out var item) || c.BackgroundToolItems.TryGetValue(ids[index], out item))
            ? item.CreatedAt
            : null;

    /// <summary>
    /// 一輪正常結束時（CompleteTurn 收尾工具卡之前）：會話回報還在跑的背景指令，它們的卡片留在執行中、改由 BackgroundToolItems 對照，
    /// 之後跑完的 AgentToolCompletedEvent 才結束它們。前幾輪就留下來、現在還在跑的也照樣留著（背景指令跑著時使用者又送了一則：
    /// 以前那一輪結束就把它們標成完成）。回傳要留著的卡片
    /// </summary>
    private HashSet<AgentChatItem> KeepBackgroundToolCards(ConversationState c, AgentTurnOutcome outcome)
    {
        var kept = new HashSet<AgentChatItem>();
        if (outcome != AgentTurnOutcome.Completed || BackgroundOf(c)?.ToolIds is not { Count: > 0 } ids)
            return kept;
        foreach (var id in ids)
        {
            if (c.ToolItems.TryGetValue(id, out var item) && item.Status == AgentItemStatus.Running)
            {
                c.BackgroundToolItems[id] = item;
                kept.Add(item);
            }
            else if (c.BackgroundToolItems.TryGetValue(id, out var earlier) && earlier.Status == AgentItemStatus.Running)
            {
                kept.Add(earlier);
            }
        }
        return kept;
    }

    /// <summary>工具結束：這一輪的卡片，或一輪結束後還在跑的背景指令的卡片（取用後移除）</summary>
    private static bool TryTakeToolItem(ConversationState c, string toolId, out AgentChatItem item)
        => c.ToolItems.TryGetValue(toolId, out item!) || c.BackgroundToolItems.Remove(toolId, out item!);

    /// <summary>
    /// Codex 的背景指令跑完（Codex 不會自己開一輪）：閒置時隱藏送出接續訊息；執行中記下來，等這一輪正常結束再接續（CompleteTurn）。
    /// 草稿、副本、使用者停止的不處理
    /// </summary>
    private void OnBackgroundCommandFinished(ConversationState c, AgentBackgroundCommandFinishedEvent ev)
    {
        // 使用者按了停止的（AgentHub.TaskStop.cs）：不叫代理接著處理（停止時已加了提示）
        if (c.IsDraft || c.IsReplica || ev.StoppedByUser)
            return;
        c.PendingBackgroundFinished.Add(ev);
        if (!c.Running)
            ContinueAfterBackground(c);
    }

    /// <summary>
    /// 有跑完、還沒接續的背景指令就隱藏送出接續訊息（提示「背景指令已結束：…，代理接著處理」）；回傳有沒有送出。
    /// 排隊的訊息暫停中（停止或失敗之後）、或從使用者上一次送出起已自動接續 MaxAutoContinues 次時不送，只加提示說明；
    /// 為了等提問的回答而停下、還沒回答時不送也不加提示（跑完的留著，之後再接續）
    /// </summary>
    private bool ContinueAfterBackground(ConversationState c)
    {
        if (c.PendingBackgroundFinished.Count == 0)
            return false;
        // 這一輪為了等提問的回答而停下、使用者還沒回答：先不接續（代理會不等答案就接著做），留到回答送出的那一輪結束後
        if (c.PausedQuestion is { Status: AgentItemStatus.Waiting })
            return false;
        var finished = c.PendingBackgroundFinished.ToList();
        c.PendingBackgroundFinished.Clear();
        bool queuePaused = c.Queue.Count > 0;
        if (queuePaused || c.BackgroundContinues >= BackgroundWorkRules.MaxAutoContinues)
        {
            Journal("bg.notContinued", c, new JObject
            {
                ["why"] = queuePaused ? "queuePaused" : "limit",
                ["continues"] = c.BackgroundContinues,
                ["finished"] = new JArray(finished.Select(f => BackgroundJournalRules.Clip(f.Title))),
            });
            AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = BackgroundWorkRules.NotContinuedNotice(finished, queuePaused), NoticeKind = AgentNoticeKind.BackgroundTask });
            SaveConversation(c, saveItems: true);
            return false;
        }
        c.BackgroundContinues++;
        var stillRunning = BackgroundOf(c)?.Tools ?? Array.Empty<AgentBackgroundTool>();
        Log.Info($"對話 {c.Meta.Id} 的背景指令結束（{finished.Count} 個），隱藏送出接續訊息（第 {c.BackgroundContinues} 次）");
        Journal("bg.continue", c, new JObject
        {
            ["continues"] = c.BackgroundContinues,
            ["finished"] = new JArray(finished.Select(f => BackgroundJournalRules.Clip(f.Title))),
            ["stillRunning"] = stillRunning.Count > 0 ? BackgroundJournalRules.Tools(stillRunning) : null,
        });
        _ = SendCoreAsync(c, BackgroundWorkRules.ContinuationText(finished, stillRunning), new List<AgentAttachment>(),
            hiddenNotice: BackgroundWorkRules.ContinuationNotice(finished), hiddenNoticeKind: AgentNoticeKind.BackgroundTask);
        return true;
    }
}
