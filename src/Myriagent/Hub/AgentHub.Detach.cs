using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// app 更新時不關還在工作的對話的 CLI 程序（設計 docs/plans/2026-11-26-agent-host.md 3.4 與第 10 節；H1、H2）：排空碰到會話放得開
/// （IAgentSession.CanDetachProcess：經代理主機開的）的對話——閒置而背景工作在跑的（H1）、一輪還在執行的（H2）——不等它、也不停掉它：
/// 先記成「交棒時放開」（RestartPlan.DetachAtHandoff，這段時間對話照常執行、照常顯示），等其他對話都停好了，
/// 排空的最後一步才一起把程序放開留在主機（<see cref="DetachAtHandoffAsync"/>），狀態寫進 resume.json 的 Attached；
/// 新版 app 啟動後（放棄這次更新時就地）以同一個程序接回，背景工作與那一輪都沒有被動到。
/// 任何一步做不到都退回原本的做法：放不開的照舊到安全點停下或等背景工作；接不回去的結束那個程序，照 running.json 接續
/// </summary>
internal sealed partial class AgentHub
{
    /// <summary>啟動後的接回已處理（第一次主視窗就緒時執行一次；網頁重新載入不再做——那時主機手上放開的程序可能是這次排空的）</summary>
    private bool attachDetachedDone;

    /// <summary>app 更新排空放開、還沒接回的程序數（協調器交棒後，宿主據此決定代理主機要留著還是結束）</summary>
    internal int DetachedProcessCount => conversations.Values.Count(c => c.Restart.Plan is { Detached: not null });

    /// <summary>排空的最後一步多久看一次（還有對話沒停好、會話一時放不開時）；測試可縮短</summary>
    internal TimeSpan HandoffPoll { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>其他對話都停好了、記成「交棒時放開」的會話卻一時放不開（控制請求在途之類）：等這麼久還不行就照原本的規則處理它；測試可縮短</summary>
    internal TimeSpan DetachReadyWait { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>開始放開之後多久要全部有結果（放開的對話這段時間畫面不會動）：超過就放棄這次更新、就地接回；測試可縮短</summary>
    internal TimeSpan HandoffDetachLimit { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>每開始或放棄一次 app 更新的排空就加一：上一次排空還在等的最後一步（DetachAtHandoffAsync）據此知道自己作廢了</summary>
    private int appDrainGeneration;

    /// <summary>
    /// 這個對話的程序會留過這次 app 更新（已記成交棒時放開、正在放開、已放開），或現在開始排空的話會：不必為它的背景工作擋住更新、也不必等它
    /// </summary>
    private bool KeepsProcessAcrossUpdate(ConversationState c)
        => c.Restart.Plan is { DetachAtHandoff: true } or { Detaching: true } or { Detached: not null }
           // 這次排空試過、放不開的（CannotDetach）照原本的規則等背景工作：不算
           || (c.Restart.Plan is not { CannotDetach: true } && CanDetachForAppUpdate(c, SituationOf(c), marked: false));

    /// <summary>
    /// app 更新時這個對話可以放開程序、不必停下：閒置而背景工作在跑，或一輪還在執行（等你回應的不算：會話帶著等回應的請求放不開，
    /// 照原本的規則停下、新版請代理重新提出）。marked＝已經記成交棒時放開：會話一時說放不開（控制請求在途）不改變主意，交棒時再看
    /// </summary>
    private bool CanDetachForAppUpdate(ConversationState c, CliRestartSituation situation, bool marked)
    {
        if (situation is not (CliRestartSituation.IdleWithBackground or CliRestartSituation.AtSafePoint
            or CliRestartSituation.ToolRunning or CliRestartSituation.BackgroundRunning))
            return false;
        if (processHost == null || c.Session is not { } session || c.IsReplica || c.IsDraft || c.Meta.Ephemeral == true)
            return false;
        // 委派（@ 其他 AI 帳戶）的管道綁著這個 app：主對話、子對話都不放
        if (c.Meta.ParentConversationId != null || c.Meta.DelegatedAccountIds is { Count: > 0 })
            return false;
        if (c.Running && !RunningTurnCanDetach(c))
            return false;
        return marked || session.CanDetachProcess;
    }

    /// <summary>執行中的一輪在中樞這邊放得開：會話已建立（接不回去時要能以 --resume／thread/resume 接續）、沒有正在停下或換掉這一輪的事</summary>
    private static bool RunningTurnCanDetach(ConversationState c)
        => c.Meta.SessionStarted && !c.StopRequested && !c.PausingForQuestion && c.SteeringMessage == null && c.PendingAccountSwitch == null
           && c.WaitingCount == 0 && !c.Rewinding && !c.MovingToBackground && !c.BackgroundStopping;

    /// <summary>
    /// 排空時這個對話改成交棒時放開（EvaluateRestart 在照政策表處理之前問）：回 true＝已處理（記下了、不停也不等）。
    /// 已經記下的每次都重看：情況變了（開始等你回應、這一輪結束而且沒有背景工作、程序沒了）就拿掉記號，回 false 照原本的規則
    /// </summary>
    private bool KeepForHandoff(ConversationState c, RestartPlan plan, CliRestartSituation situation)
    {
        if (plan is not { AppUpdate: true, Drain: true, CannotDetach: false, Detached: null })
            return false;
        if (!CanDetachForAppUpdate(c, situation, plan.DetachAtHandoff))
        {
            if (plan.DetachAtHandoff)
            {
                plan.DetachAtHandoff = false;
                Journal("restart.keep", c, new JObject { ["keep"] = false, ["situation"] = situation.ToString() });
                MarkHeaderDirty(c);
            }
            return false;
        }
        if (!plan.DetachAtHandoff)
        {
            plan.DetachAtHandoff = true;
            plan.Waiting = null;
            plan.Deferred = false;
            Log.Info($"app 更新：對話 {c.Meta.Id} 的程序交棒時放開（{(c.Running ? "這一輪照常執行" : "背景工作照跑")}，新版接回）");
            Journal("restart.keep", c, new JObject
            {
                ["keep"] = true,
                ["running"] = c.Running,
                ["situation"] = situation.ToString(),
                ["bgCount"] = BackgroundOf(c)?.Count,
            });
            MarkHeaderDirty(c);
            MarkCliDirty();
        }
        return true;
    }

    /// <summary>記成交棒時放開、還沒放開的對話</summary>
    private List<ConversationState> KeptForHandoff()
        => conversations.Values.Where(c => c.Restart.Plan is { AppUpdate: true, Drain: true, DetachAtHandoff: true, InFlight: false, Detached: null }).ToList();

    /// <summary>
    /// 排空的最後一步（每種代理的屏障都回報停好之後）：記成交棒時放開的對話一起放開。回 null＝都放開了、沒有還在跑的程序；否則是沒做成的原因
    /// （協調器據此放棄這次更新，已放開的就地接回）。
    /// 還有對話沒停好（記號被拿掉、改照原本的規則的）就等；都停好了而會話一時放不開的等 DetachReadyWait，再不行照原本的規則處理那一個
    /// （強制更新時照立即套用停下）；放開途中失敗的同樣處理。開始放開之後 HandoffDetachLimit 內要全部有結果
    /// </summary>
    private async Task<string?> DetachAtHandoffAsync(int generation)
    {
        long? notReadySince = null, detachStarted = null;
        int detached = 0;
        while (true)
        {
            if (disposed)
                return "app 正在結束";
            if (generation != appDrainGeneration || !AppUpdateDraining)
                return "已取消";
            // 情況變了的（開始等你回應、這一輪結束了…）在這裡改回原本的規則
            foreach (var c in KeptForHandoff())
                EvaluateRestart(c);
            var kept = KeptForHandoff();
            int busy = AppUpdateWaitingCount;
            if (busy == 0 && kept.Count == 0)
            {
                if (detached > 0)
                    JournalApp("update.detached", new JObject { ["count"] = DetachedProcessCount, ["started"] = detached });
                return null;
            }
            long now = Environment.TickCount64;
            if (detachStarted is { } started && now - started > HandoffDetachLimit.TotalMilliseconds)
                return "放開對話的程序花了太久";
            if (busy > 0)
            {
                notReadySince = null;
            }
            else
            {
                var notReady = kept.Where(c => c.Session is not { CanDetachProcess: true }).ToList();
                if (notReady.Count == 0)
                {
                    detachStarted ??= now;
                    notReadySince = null;
                    foreach (var c in kept)
                    {
                        detached++;
                        StartRestartDetach(c, c.Restart.Plan!);
                    }
                }
                else
                {
                    notReadySince ??= now;
                    if (now - notReadySince >= DetachReadyWait.TotalMilliseconds)
                    {
                        foreach (var c in notReady)
                            GiveUpDetach(c, c.Restart.Plan!, "會話一直放不開");
                        notReadySince = null;
                    }
                }
            }
            // 開始放開之後看得勤一點（放開通常幾十毫秒就有結果，放開的對話這段時間畫面不會動）
            var poll = detachStarted != null && HandoffPoll > HandoffFastPoll ? HandoffFastPoll : HandoffPoll;
            await Task.Delay(poll);
        }
    }

    private static readonly TimeSpan HandoffFastPoll = TimeSpan.FromMilliseconds(40);


    /// <summary>這次排空不放開這個對話了：照原本的規則（到安全點停下、等背景工作；強制更新時照立即套用停下）</summary>
    private void GiveUpDetach(ConversationState c, RestartPlan plan, string reason)
    {
        plan.DetachAtHandoff = false;
        plan.CannotDetach = true;
        if (appUpdateForcedAt != null)
            plan.ApplyNow = true;
        Log.Info($"app 更新：對話 {c.Meta.Id} 的程序放不開（{reason}），照原本的規則處理");
        Journal("restart.keep", c, new JObject { ["keep"] = false, ["reason"] = reason });
        EvaluateRestart(c);
    }

    private void StartRestartDetach(ConversationState c, RestartPlan plan)
    {
        var session = c.Session!;
        plan.InFlight = true;
        plan.Detaching = true;
        plan.Restarting = false;
        plan.Waiting = null;
        // 接不回去時新版要知道這一輪（或哪些背景工作）沒了：先把 running.json 記成 app 停掉的（接回成功再改回平常的），
        // 之後到有結果為止不重寫（會話沒有程序時寫的快照沒有背景工作的清單）
        if (c.Running)
            WriteRunMarker(c, closedByApp: true);
        else
            MarkBackgroundStoppedByApp(c);
        c.RunMarkerFrozen = true;
        MarkHeaderDirty(c);
        MarkCliDirty();
        Log.Info($"app 更新：放開對話 {c.Meta.Id} 的程序（{(c.Running ? "這一輪" : "背景工作")}照跑，新版接回）");
        Journal("restart.detach", c, new JObject
        {
            ["running"] = c.Running ? true : null,
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
        plan.Detaching = false;
        plan.DetachAtHandoff = false;
        if (disposed)
            return;
        var provider = c.Meta.Provider;
        if (IsLive(c) && ReferenceEquals(c.Session, session) && ReferenceEquals(c.Restart.Plan, plan))
        {
            Journal("restart.detached", c, new JObject { ["ok"] = state != null, ["running"] = c.Running ? true : null });
            if (state != null)
            {
                // 會話在放開之前發的事件都處理完了：這一輪在中樞的狀態現在記下（新版接回之前照它還原）
                var turn = c.Running ? CaptureTurn(c) : null;
                MarkProcessGone(c, instance);
                RecordOutcome(provider, plan, WaveOutcome.Restarted);
                if (plan.Drain)
                {
                    plan.Detached = state;
                    plan.Turn = turn;
                    if (c.Running)
                        SetStatus(c, DetachedStatus);
                    FinishPlan(c, plan);
                }
                else
                {
                    // 放開的途中這次更新已經放棄（屏障結束了）：馬上接回
                    _ = ReattachDetachedAsync(c, state, afterAppRestart: false, turn: turn);
                }
            }
            else
            {
                // 放不開（剛好不在靜止點、主機不支援）：照原本的規則（到安全點停下、等背景工作）
                c.RunMarkerFrozen = false;
                c.RunMarkerStoppedByApp = false;
                WriteRunMarker(c, closedByApp: false);
                if (plan.Drain)
                    GiveUpDetach(c, plan, "放開失敗");
                else
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
    /// 接回放開的程序（新版啟動後、或放棄這次更新時就地）。成功：背景工作與那一輪照舊在跑，running.json 改回平常的。
    /// 失敗：結束留在主機的那個程序，那一輪與背景工作等於被停掉——照 running.json 隱藏送出，請代理確認做到哪裡、接著做。
    /// turn＝放開時還在執行的一輪在中樞的狀態（新版據此還原；就地接回時中樞的狀態原封不動，只拿它放回狀態列的文字）
    /// </summary>
    private async Task ReattachDetachedAsync(ConversationState c, JObject state, bool afterAppRestart, IReadOnlyDictionary<string, string>? cards = null,
        ResumeTurn? turn = null)
    {
        bool ok = false;
        c.Restart.Attaching = true;
        var restoredCards = new List<(string ToolId, AgentChatItem Item)>();
        // 接的是還在執行的一輪
        bool turnAttached = false;
        try
        {
            if (processHost != null && IsLive(c) && !c.IsReplica)
            {
                // 會話一接上就會補送放開期間的輸出：中樞要先回到放開當下的樣子（新版：這一輪的對照表與執行中的項目；等背景工作的：背景指令的卡片）
                if (afterAppRestart && turn != null && !c.Running)
                    RestoreTurn(c, turn);
                else if (!c.Running)
                    restoredCards = RestoreDetachedCards(c, cards);
                turnAttached = c.Running;
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
        Log.Info(ok
            ? $"已接回對話 {c.Meta.Id} 的程序（{(turnAttached ? "這一輪" : "背景工作")}沒有中斷）"
            : $"對話 {c.Meta.Id} 的程序接不回去，照{(turnAttached ? "這一輪被中斷" : "背景工作被停掉")}處理");
        Journal("restart.attach", c, new JObject { ["ok"] = ok, ["afterAppRestart"] = afterAppRestart, ["running"] = turnAttached ? true : null });
        if (ok)
        {
            c.InterruptedTurn = null;
            c.RunMarkerStoppedByApp = false;
            if (c.Running)
            {
                // 這一輪照舊在執行：狀態列放回放開之前的文字，running.json 改回平常的（之後 app 當掉才算被中斷）
                if (c.StatusText == DetachedStatus)
                    SetStatus(c, turn?.Status is { Length: > 0 } status && status != DetachedStatus ? status : "思考中…");
                WriteRunMarker(c, closedByApp: false);
            }
            else
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

        // 接不回去、那一輪還算執行中：跟著程序沒了，就地收尾，照 running.json（放開之前記成 app 停掉的）當成被中斷的一輪接續
        RunningTurnRecord? lostTurn = null;
        bool turnLost = turnAttached && c.Running;
        if (turnLost)
        {
            lostTurn = afterAppRestart ? c.InterruptedTurn : RunningTurnStore.TryLoad(store.GetConversationDirectory(c.Meta.Id)).Record;
            EndLostTurn(c, hooked: !afterAppRestart);
        }
        // 先前改回執行中的卡片照舊算被中斷
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
        else if (turnLost)
        {
            // 放棄更新、就地接回失敗：照放開之前寫的快照接續（讀不到就照現況補一份）
            var version = AppVersionInfo.VersionText;
            var record = lostTurn ?? new RunningTurnRecord
            {
                StartedAt = UnixMilliseconds(DateTime.Now),
                SeenAt = UnixMilliseconds(DateTime.Now),
                ClosedByApp = true,
                CliVersion = CurrentVersionOf(c) ?? c.Meta.LastCliVersion,
                AutoResumes = c.RunAutoResumes,
            };
            var context = InterruptedTurnRules.Context(c.Meta.Provider, record, cli.GetIdentity(c.Meta.Provider)?.Version)
                with { AppRestart = null, AppFrom = version, AppTo = version };
            _ = DispatchInterruptedTurn(c, record, context, DateTime.UtcNow, new Dictionary<AgentProvider, int>(), abandonedUpdate: true);
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
            tasks.Add(ReattachDetachedAsync(c, state, afterAppRestart: true, entry.Cards, entry.Turn));
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
