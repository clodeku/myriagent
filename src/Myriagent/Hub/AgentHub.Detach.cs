using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// app 更新時不關還在等背景工作的對話的 CLI 程序（設計 docs/plans/2026-11-26-agent-host.md 3.4，H1）：排空碰到「閒置、背景工作在跑」
/// 而且會話放得開（IAgentSession.CanDetachProcess：經代理主機開的、在靜止點）的對話，不等背景工作、也不停掉它——把程序放開留在主機，
/// 狀態寫進 resume.json 的 Attached；新版 app 啟動後（放棄這次更新時就地）以同一個程序接回，背景工作沒有被動到。
/// 任何一步做不到都退回原本的做法：放不開的照舊等背景工作；接不回去的結束那個程序，照 running.json 請代理確認背景工作做到哪裡
/// </summary>
internal sealed partial class AgentHub
{
    /// <summary>啟動後的接回已處理（第一次主視窗就緒時執行一次；網頁重新載入不再做——那時主機手上放開的程序可能是這次排空的）</summary>
    private bool attachDetachedDone;

    /// <summary>app 更新排空放開、還沒接回的程序數（協調器交棒後，宿主據此決定代理主機要留著還是結束）</summary>
    internal int DetachedProcessCount => conversations.Values.Count(c => c.Restart.Plan is { Detached: not null });

    /// <summary>這個對話的背景工作撐得過 app 更新：閒置、會話現在放得開。不必為它擋住更新、也不必等它</summary>
    private static bool CanKeepBackgroundAcrossUpdate(ConversationState c)
        => !c.Running && c.Session is { CanDetachProcess: true };

    /// <summary>排空時這個對話改成放開程序（EvaluateRestart 在等背景工作之前問）</summary>
    private static bool ShouldDetachForAppUpdate(ConversationState c, RestartPlan plan, CliRestartSituation situation)
        => plan is { AppUpdate: true, Drain: true, CannotDetach: false, Detached: null }
           && situation == CliRestartSituation.IdleWithBackground && CanKeepBackgroundAcrossUpdate(c);

    private void StartRestartDetach(ConversationState c, RestartPlan plan)
    {
        var session = c.Session!;
        plan.InFlight = true;
        plan.Restarting = false;
        plan.Waiting = null;
        // 接不回去時新版要知道哪些背景工作沒了：先把 running.json 記成 app 停掉的（接回成功再改回平常的等背景工作），
        // 之後到有結果為止不重寫（會話沒有程序時寫的快照沒有背景工作的清單）
        MarkBackgroundStoppedByApp(c);
        c.RunMarkerFrozen = true;
        MarkHeaderDirty(c);
        MarkCliDirty();
        Log.Info($"app 更新：放開對話 {c.Meta.Id} 的程序（背景工作照跑，新版接回）");
        Journal("restart.detach", c, new JObject
        {
            ["bg"] = BackgroundOf(c) is { Tools.Count: > 0 } work ? BackgroundJournalRules.Tools(work.Tools) : null,
            ["bgCount"] = BackgroundOf(c)?.Count,
        });
        _ = DetachForRestartAsync(c, plan, session, c.Restart.Instance);
    }

    private async Task DetachForRestartAsync(ConversationState c, RestartPlan plan, IAgentSession session, string? instance)
    {
        JObject? state = null;
        try
        {
            state = await session.DetachProcessAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"放開對話 {c.Meta.Id} 的程序失敗：{ex.Message}");
        }
        plan.InFlight = false;
        if (disposed)
            return;
        var provider = c.Meta.Provider;
        if (IsLive(c) && ReferenceEquals(c.Session, session) && ReferenceEquals(c.Restart.Plan, plan))
        {
            Journal("restart.detached", c, new JObject { ["ok"] = state != null });
            if (state != null)
            {
                plan.Detached = state;
                MarkProcessGone(c, instance);
                RecordOutcome(provider, plan, WaveOutcome.Restarted);
                if (plan.Drain)
                {
                    FinishPlan(c, plan);
                }
                else
                {
                    // 放開的途中這次更新已經放棄（屏障結束了）：馬上接回
                    plan.Detached = null;
                    _ = ReattachDetachedAsync(c, state, afterAppRestart: false);
                }
            }
            else
            {
                // 放不開（剛好不在靜止點、主機不支援）：照原本的規則等背景工作
                plan.CannotDetach = true;
                c.RunMarkerFrozen = false;
                c.RunMarkerStoppedByApp = false;
                WriteRunMarker(c, closedByApp: false);
                EvaluateRestart(c);
            }
        }
        else if (state != null)
        {
            DiscardDetached(state);   // 對話已刪除或換了會話：那個程序沒有人要了
        }
        AfterRestartStep(provider);
        ResumeSendAfterRestart(c);
        // 放開的途中跑完的背景指令（那時送不出接續訊息，ContinueAfterBackground 先留著）：沒放開、程序還是這個會話的，現在接續
        if (state == null && !disposed && IsLive(c) && !c.Running)
            ContinueAfterBackground(c);
    }

    /// <summary>
    /// 接回放開的程序（新版啟動後、或放棄這次更新時就地）。成功：背景工作照舊在跑，running.json 改回平常的等背景工作。
    /// 失敗：結束留在主機的那個程序，背景工作等於被停掉——照 running.json 隱藏送出，請代理確認做到哪裡、接著做
    /// </summary>
    private async Task ReattachDetachedAsync(ConversationState c, JObject state, bool afterAppRestart, IReadOnlyDictionary<string, string>? cards = null)
    {
        bool ok = false;
        c.Restart.Attaching = true;
        var restoredCards = new List<(string ToolId, AgentChatItem Item)>();
        try
        {
            if (processHost != null && IsLive(c) && !c.IsReplica)
            {
                // 會話一接上就會補送放開期間的輸出（可能有背景指令的結束）：卡片先對回來
                restoredCards = RestoreDetachedCards(c, cards);
                var session = EnsureSession(c);
                ok = await session.AttachProcessAsync(state, BuildSessionSettings(c, historyRecap: null) with { ProcessHost = processHost });
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"接回對話 {c.Meta.Id} 的程序失敗：{ex.Message}");
        }
        c.Restart.Attaching = false;
        c.RunMarkerFrozen = false;
        if (disposed)
            return;
        if (!IsLive(c))
        {
            if (!ok)
                DiscardDetached(state);
            return;
        }
        Log.Info(ok ? $"已接回對話 {c.Meta.Id} 的程序（背景工作沒有中斷）" : $"對話 {c.Meta.Id} 的程序接不回去，照背景工作被停掉處理");
        Journal("restart.attach", c, new JObject { ["ok"] = ok, ["afterAppRestart"] = afterAppRestart });
        if (ok)
        {
            c.InterruptedTurn = null;
            c.RunMarkerStoppedByApp = false;
            if (!c.Running)
            {
                // 背景工作的清單由會話接回時的事件帶回來（可能還在路上）：先照會話現在說的定，清單到了 RefreshRunMarker 會補寫
                c.RunMarkerWaitingBackground = c.Session is { HasLiveBackgroundWork: true };
                if (c.RunMarkerWaitingBackground)
                    WriteRunMarker(c, closedByApp: false);
                else
                    DeleteRunMarker(c);
            }
            MarkHeaderDirty(c);
            ResumeSendAfterRestart(c);   // 接回途中的送出（DeferSendDuringRestart）：現在用接回的程序送
            // 放開期間跑完的背景指令（接上時補送進來的）：現在請代理接著處理
            if (!c.Running)
                ContinueAfterBackground(c);
            return;
        }

        // 接不回去：先前改回執行中的卡片照舊算被中斷
        foreach (var (toolId, item) in restoredCards)
        {
            c.BackgroundToolItems.Remove(toolId);
            if (item.Status == AgentItemStatus.Running)
            {
                item.Status = AgentItemStatus.Cancelled;
                MarkItemDirty(c, item);
            }
        }
        c.PendingBackgroundFinished.Clear();
        DiscardDetached(state);
        // 背景工作沒了：中樞記著的清單清掉（會話放開時刻意沒有回報）
        if (BackgroundOf(c) != null)
            OnBackgroundWork(c, new AgentBackgroundWorkEvent(0, Array.Empty<AgentBackgroundTool>()));
        if (c.Running)
            return;
        if (c.Restart.DeferredSend != null)
        {
            // 使用者有訊息等著送出：以它為準，不另外送「確認背景工作」的接續訊息（兩則會搶同一輪）。
            // 新程序以 --resume 接續，代理從逐字稿看得到之前開的背景工作沒有回報結果
            c.InterruptedTurn = null;
            DeleteRunMarker(c);
            ResumeSendAfterRestart(c);
            return;
        }
        if (afterAppRestart)
        {
            // 啟動時讀到的 running.json（放開之前記成 app 停掉的）：照「上次結束時停掉了背景工作」接續
            if (c.InterruptedTurn is { } record)
            {
                c.InterruptedTurn = null;
                var context = InterruptedTurnRules.Context(c.Meta.Provider, record, cli.GetIdentity(c.Meta.Provider)?.Version);
                _ = DispatchInterruptedTurn(c, record, context, DateTime.UtcNow, new Dictionary<AgentProvider, int>(), abandonedUpdate: false);
            }
        }
        else
        {
            c.RunMarkerStoppedByApp = true;
            ContinueBackgroundStoppedByApp(new[] { c }, new Dictionary<AgentProvider, int>());
        }
    }

    /// <summary>
    /// 新版啟動後（主視窗第一次就緒、接續之前）：resume.json 的 Attached 逐一接回；正在接回的對話（RestartState.Attaching）
    /// 「上次中斷的一輪」先不處理、送出先排隊。全部有結果之後，主機手上沒人認領的程序一律結束（上一版沒放開乾淨的、接回失敗的）
    /// </summary>
    private void BeginAttachDetached()
    {
        if (attachDetachedDone || disposed)
            return;
        attachDetachedDone = true;
        var entries = pendingResume?.File?.Attached;
        var tasks = new List<Task>();
        if (entries == null || entries.Count == 0)
        {
            // 沒有要接回的（一般啟動、上一版沒有放開程序）：上一個 app 留下的主機手上若還有程序，都是沒人要的
            _ = DiscardUnclaimedAfterAsync(tasks);
            return;
        }
        foreach (var entry in entries)
        {
            if (entry?.State is not { } state || string.IsNullOrEmpty(entry.Id))
                continue;
            if (!conversations.TryGetValue(entry.Id, out var c) || c.IsDraft || c.IsReplica || c.Running)
            {
                DiscardDetached(state);
                continue;
            }
            tasks.Add(ReattachDetachedAsync(c, state, afterAppRestart: true, entry.Cards));
        }
        _ = DiscardUnclaimedAfterAsync(tasks);
    }

    /// <summary>放開時還在執行中的背景指令卡片（工具 id → 項目 id），寫進 resume.json</summary>
    private static Dictionary<string, string> DetachedCards(ConversationState c)
        => c.BackgroundToolItems.Where(kv => kv.Value.Status == AgentItemStatus.Running).ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.Ordinal);

    /// <summary>
    /// 新版接回之前：resume.json 記的背景指令卡片（載入紀錄時被算成中斷的）改回執行中、對回工具 id。回傳改回來的（接不回去時再改回中斷）
    /// </summary>
    private List<(string ToolId, AgentChatItem Item)> RestoreDetachedCards(ConversationState c, IReadOnlyDictionary<string, string>? cards)
    {
        var restored = new List<(string, AgentChatItem)>();
        if (cards == null || cards.Count == 0)
            return restored;
        EnsureItems(c);
        foreach (var (toolId, itemId) in cards)
        {
            if (string.IsNullOrEmpty(toolId) || c.BackgroundToolItems.ContainsKey(toolId))
                continue;
            var item = c.Items.LastOrDefault(i => i.Id == itemId && i.Kind == AgentItemKind.Tool && i.Status == AgentItemStatus.Cancelled);
            if (item == null)
                continue;
            item.Status = AgentItemStatus.Running;
            c.BackgroundToolItems[toolId] = item;
            MarkItemDirty(c, item);
            restored.Add((toolId, item));
        }
        return restored;
    }

    private async Task DiscardUnclaimedAfterAsync(List<Task> attaches)
    {
        try
        {
            await Task.WhenAll(attaches);
        }
        catch (Exception ex)
        {
            Log.Warn($"接回程序有失敗：{ex.Message}");
        }
        var host = processHost;
        if (host == null || disposed)
            return;
        try
        {
            await Task.Run(host.DiscardUnclaimed);
        }
        catch (Exception ex)
        {
            Log.Warn($"清掉代理主機上沒有人接回的程序失敗：{ex.Message}");
        }
    }

    /// <summary>結束一個沒有人要接回的放開的程序（照狀態裡的代號與 PID 向主機確認是它才結束）</summary>
    private void DiscardDetached(JObject state)
    {
        try
        {
            var host = processHost;
            if (host != null && state["Process"] is JObject process && process.ToObject<AgentProcessDetachState>() is { } detached)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        host.Discard(detached);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"結束沒有人接回的程序失敗：{ex.Message}");
                    }
                });
            }
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or InvalidCastException or FormatException)
        {
        }
    }
}
