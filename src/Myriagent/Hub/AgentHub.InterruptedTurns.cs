using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// app 結束時還在執行的一輪，下次啟動自動接續（設計 docs/plans/2026-10-23-interrupted-turn-resume.md）。
/// 每個執行中的對話在資料夾裡有一份 running.json（RunningTurnStore）：一輪開始時寫、結束或使用者按停止時刪，執行期間每 30 秒（維護）、
/// 開始或結束高風險操作時與 app 結束時更新快照（正在執行的工具、等回應的請求、背景工作）。app 正常結束、當掉、被強制結束或電腦重新開機，
/// 它都留在磁碟上；下次啟動第一次主視窗就緒時（帳戶已載入），照 InterruptedTurnRules 以隱藏送出接續（同 app 更新後的接續），
/// 不自動接續的加提示、標頭給「繼續」。app 更新排空停下的對話由 resume.json 處理（AgentHub.AppUpdate.cs），這裡略過。
/// 這一輪正常結束時背景工作還在跑（代理回一句「等測試跑完」就結束了這一輪）也算在進行：running.json 改寫成等背景工作的快照，
/// 背景工作自己結束、換了會話才刪；app 結束、當掉或 app 更新排空逾時停掉了背景工作，下次啟動請代理確認它們做到哪裡、接著做
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>啟動時讀到的 running.json（上次程式結束時這一輪還在執行）：第一次主視窗就緒時處理後清掉；之前就開始了新的一輪也清掉</summary>
        public RunningTurnRecord? InterruptedTurn;

        /// <summary>running.json 可能在磁碟上（這個程序寫過、或啟動時就有）：結束一輪時才需要刪</summary>
        public bool RunMarkerOnDisk;

        /// <summary>寫進磁碟的 running.json 記著高風險操作：那個操作結束時要重寫快照（否則當掉後誤判成高風險、不自動接續）</summary>
        public bool RunMarkerRisky;

        /// <summary>這一輪是連續第幾次自動接續（寫進 running.json；一輪開始時取用 PendingAutoResumes）</summary>
        public int RunAutoResumes;

        /// <summary>下一輪是重新啟動後的自動接續：開始時記成 RunAutoResumes（送出自動接續前設定）</summary>
        public int PendingAutoResumes;

        /// <summary>上次寫進 running.json 的內容指紋（不含時間）與寫入時間：維護只在內容變了或太久沒寫時才重寫</summary>
        public string? RunMarkerFingerprint;

        public DateTime RunMarkerWrittenAt;

        /// <summary>
        /// 上一輪正常結束時背景工作還在跑：running.json 是等背景工作的快照（RunningTurnRecord.WaitingBackground）。
        /// 一輪開始時清掉；背景工作都沒了（自己跑完、換了會話）時連同檔案一起清掉，除非是 app 停掉的（RunMarkerStoppedByApp）
        /// </summary>
        public bool RunMarkerWaitingBackground;

        /// <summary>
        /// 等背景工作的對話被 app 更新排空逾時照立即套用停下（背景工作跟著停）：快照記成 app 正常結束的、背景工作沒了也不刪，
        /// 留給新版接續；放棄這次更新時就地接續（AbortAppUpdate）
        /// </summary>
        public bool RunMarkerStoppedByApp;
    }

    /// <summary>
    /// 維護時內容沒變也重寫 running.json 的間隔（更新「最後確認還在執行」的時間）：當掉後判斷中斷多久（1 小時的界線）最多差這麼多，
    /// 換來執行中不每 30 秒寫一次檔
    /// </summary>
    internal static readonly TimeSpan RunMarkerRefresh = TimeSpan.FromMinutes(5);

    /// <summary>啟動後的接續已處理（第一次主視窗就緒時執行一次）</summary>
    private bool interruptedResumeDone;

    /// <summary>重新啟動後接續的結果：自動接續與沒有自動接續的對話數（測試與記錄用）</summary>
    internal event Action<(int Continued, int NotContinued)>? InterruptedResumeDispatched;

    /// <summary>建構中樞時讀 running.json（上次程式結束時還在執行的一輪）；讀不到的檔案留著不動、這次不接續</summary>
    private void LoadRunMarker(ConversationState c)
    {
        var (status, record) = RunningTurnStore.TryLoad(store.GetConversationDirectory(c.Meta.Id));
        c.RunMarkerOnDisk = status is JsonReadStatus.Ok or JsonReadStatus.Unreadable;
        c.InterruptedTurn = record;
    }

    /// <summary>
    /// 這個對話現在該不該有 running.json：在執行、使用者沒按停止；或上一輪正常結束、還在等存活的背景工作回報。
    /// 草稿與其他電腦的對話（副本）沒有；已從中樞移除的（刪除中）不寫，否則會重建資料夾
    /// </summary>
    private bool WantsRunMarker(ConversationState c)
        => (c.Running ? !c.StopRequested : c.RunMarkerWaitingBackground && BackgroundOf(c) != null) && !c.IsReplica && IsLive(c);

    /// <summary>一輪開始（HookTurnStarted）：上次中斷的那一輪不再接續（使用者或代理已經開始新的一輪），寫 running.json</summary>
    private void OnRunMarkerTurnStarted(ConversationState c)
    {
        c.InterruptedTurn = null;
        c.RunAutoResumes = c.PendingAutoResumes;
        c.PendingAutoResumes = 0;
        c.RunMarkerWaitingBackground = false;
        c.RunMarkerStoppedByApp = false;
        WriteRunMarker(c, closedByApp: false);
    }

    /// <summary>
    /// 一輪結束（HookTurnCompleted）：正常結束而背景工作還在跑的，running.json 改寫成等背景工作的快照（app 結束或當掉時背景工作跟著停，
    /// 下次啟動請代理確認後接著做）；其他的（停止、失敗、沒有背景工作）刪掉。app 結束途中（disposed 之後）什麼都不動
    /// </summary>
    private void OnRunMarkerTurnCompleted(ConversationState c, AgentTurnOutcome outcome)
    {
        if (disposed)
            return;
        c.RunMarkerWaitingBackground = outcome == AgentTurnOutcome.Completed && !c.StopRequested && BackgroundOf(c) != null;
        c.RunMarkerStoppedByApp = false;
        if (WantsRunMarker(c))
            WriteRunMarker(c, closedByApp: false);
        else
            DeleteRunMarker(c);
    }

    /// <summary>
    /// 維護與背景工作的清單變了（AgentHub.Background.cs）時：執行中與等背景工作的更新快照（內容變了或太久沒寫才寫）。
    /// 等背景工作而背景工作都沒了的刪掉——自己跑完的 CLI 會接著自己開一輪（HookTurnStarted 再寫），換了會話的是使用者的操作；
    /// app 更新停掉的留著給新版接續
    /// </summary>
    private void RefreshRunMarker(ConversationState c)
    {
        if (disposed)
            return;
        if (!c.Running && c.RunMarkerWaitingBackground && BackgroundOf(c) == null)
        {
            if (c.RunMarkerStoppedByApp)
                return;
            c.RunMarkerWaitingBackground = false;
            DeleteRunMarker(c);
            return;
        }
        WriteRunMarker(c, closedByApp: false, onlyIfChanged: true);
    }

    /// <summary>
    /// app 更新排空逾時、照立即套用停下只在等背景工作的對話之前（AgentHub.Restart.cs 的 BuildRestartRequest）：快照記成 app 正常結束的，
    /// 停下時背景工作跟著沒了也不刪，新版照 running.json 接續（等背景工作的對話沒有停下待接續的一輪，不進 resume.json）
    /// </summary>
    private void MarkBackgroundStoppedByApp(ConversationState c)
    {
        if (c.Running || !WantsRunMarker(c))
            return;
        c.RunMarkerStoppedByApp = true;
        WriteRunMarker(c, closedByApp: true);
    }

    /// <summary>
    /// 寫 running.json 的快照（一輪開始、維護、高風險操作開始或結束、app 結束時）。不看 disposed：app 結束時正是最後一次寫入的機會。
    /// onlyIfChanged（維護）：內容（不含時間）和上次寫的一樣、而且寫過不到 RunMarkerRefresh 就不寫。
    /// 寫不了只記警告（這一輪照常執行，只是 app 結束後不會自動接續）
    /// </summary>
    private void WriteRunMarker(ConversationState c, bool closedByApp, bool onlyIfChanged = false)
    {
        if (!WantsRunMarker(c))
            return;
        var now = DateTime.Now;
        // WantsRunMarker 成立而沒在執行：上一輪已回覆完、在等背景工作（沒有執行中的工具與等回應的請求）
        bool waitingBackground = !c.Running;
        bool background = c.Session?.HasLiveBackgroundWork ?? false;
        var risk = (waitingBackground ? null : FindRunningToolRisk(c)) ?? (background ? FindBackgroundToolRisk(c) : null);
        var record = new RunningTurnRecord
        {
            StartedAt = UnixMilliseconds(c.TurnStartedAt ?? now),
            SeenAt = UnixMilliseconds(now),
            // app 更新停掉的背景工作：之後的快照照樣記成 app 正常結束的（停下之前維護可能再寫一次）
            ClosedByApp = closedByApp || c.RunMarkerStoppedByApp,
            InterruptedTools = c.ItemsLoaded && !waitingBackground ? RunningTools(c) : new List<AgentInterruptedTool>(),
            PendingRequests = c.ItemsLoaded && !waitingBackground ? PendingRequests(c) : new List<string>(),
            BackgroundWork = background,
            WaitingBackground = waitingBackground,
            BackgroundTasks = background ? BackgroundWorkRules.Labels(c.Session?.LiveBackgroundTools ?? Array.Empty<AgentBackgroundTool>()) : new List<string>(),
            RiskOperation = risk?.Operation,
            CliVersion = CurrentVersionOf(c) ?? c.Meta.LastCliVersion,
            AutoResumes = c.RunAutoResumes,
        };
        var fingerprint = Fingerprint(record);
        if (onlyIfChanged && c.RunMarkerOnDisk && fingerprint == c.RunMarkerFingerprint && now - c.RunMarkerWrittenAt < RunMarkerRefresh)
            return;
        try
        {
            RunningTurnStore.Save(store.GetConversationDirectory(c.Meta.Id), record);
            c.RunMarkerOnDisk = true;
            c.RunMarkerRisky = risk != null;
            c.RunMarkerFingerprint = fingerprint;
            c.RunMarkerWrittenAt = now;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"寫入執行中標記失敗（{c.Meta.Id}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 這一輪結束、使用者按了停止、對話被刪除或接續已處理：刪 running.json。app 結束途中（disposed 之後）不刪：
    /// 代理程序被關掉時晚到的收尾不能讓下次啟動以為這一輪已經完成
    /// </summary>
    private void DeleteRunMarker(ConversationState c)
    {
        if (disposed)
            return;
        DeleteRunMarkerFile(c);
    }

    /// <summary>快照內容的指紋（不含時間：最後確認的時間、工具已執行多久）</summary>
    private static string Fingerprint(RunningTurnRecord r)
        => string.Join("|", r.StartedAt, r.ClosedByApp, r.BackgroundWork, r.WaitingBackground, r.RiskOperation, r.CliVersion, r.AutoResumes,
            string.Join("\u001f", r.InterruptedTools.Select(t => t.ToolName + "\u001e" + t.Title)), string.Join("\u001f", r.PendingRequests),
            string.Join("\u001f", r.BackgroundTasks));

    private void DeleteRunMarkerFile(ConversationState c)
    {
        if (!c.RunMarkerOnDisk || c.IsReplica)
            return;
        try
        {
            RunningTurnStore.Delete(store.GetConversationDirectory(c.Meta.Id));
            c.RunMarkerOnDisk = false;
            c.RunMarkerRisky = false;
            c.RunMarkerFingerprint = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"刪除執行中標記失敗（{c.Meta.Id}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 工具開始或結束：開始的是高風險操作、或記著高風險操作的那個結束了，立刻重寫快照（不等維護）。
    /// 當掉時最後的快照才不會漏掉正在執行的部署、推送（漏掉會自動接續而重做一次）
    /// </summary>
    private void OnRunMarkerToolChanged(ConversationState c, AgentChatItem tool, bool started)
    {
        if (!WantsRunMarker(c))
            return;
        if (started
                ? CliRestartRules.FindRisk(tool.ToolName, tool.ToolKind, tool.Title, tool.Detail, settings.CliRestartRiskExtra, settings.CliRestartRiskDisabled) != null
                : c.RunMarkerRisky)
            WriteRunMarker(c, closedByApp: false);
    }

    /// <summary>
    /// app 結束（ShutdownAsync、Dispose）或 Windows 詢問能不能登出關機：執行中與等背景工作的對話寫最後的快照、按了停止的刪掉。
    /// closedByApp＝真的要結束了（Windows 的詢問可能被取消，照執行中的快照寫）。等背景工作而背景工作已經沒了的（app 更新停掉的）不動
    /// </summary>
    private void WriteRunMarkersOnExit(bool closedByApp)
    {
        // 背景活動日誌：結束會停掉的執行中的一輪、背景工作、背景程式、子代理（AgentHub.Journal.cs）
        JournalAppExit(closedByApp);
        foreach (var c in conversations.Values.Where(c => c.Running || c.RunMarkerWaitingBackground).ToList())
        {
            if (c.Running && c.StopRequested)
                DeleteRunMarkerFile(c);
            else
                WriteRunMarker(c, closedByApp);
        }
    }

    /// <summary>
    /// 第一次主視窗就緒（app 更新的接續之後）：上次結束時還在執行的對話，可以自動接續的照同時重開上限分批以隱藏送出接續訊息
    /// （同 app 更新後的接續：Claude 以 --resume、Codex 以 thread/resume 啟動新程序），其他的加提示、標頭給「繼續」。
    /// handledByAppResume 是 resume.json 列出的對話（app 更新排空停下的，由那邊接續或給「繼續」）：這裡只刪標記。
    /// 只執行一次；每個對話各自隔離例外（只記 log），不擋主視窗的就緒
    /// </summary>
    private void RunInterruptedTurnResume(IReadOnlySet<string> handledByAppResume)
    {
        if (interruptedResumeDone || disposed)
            return;
        interruptedResumeDone = true;
        var now = DateTime.UtcNow;
        var perProvider = new Dictionary<AgentProvider, int>();
        var starts = new List<Task<InterruptedStart>>();
        int notContinued = 0;
        foreach (var c in conversations.Values.Where(c => c.InterruptedTurn != null).ToList())
        {
            try
            {
                var record = c.InterruptedTurn!;
                c.InterruptedTurn = null;
                if (c.Running)
                    continue;   // 已經開始了新的一輪（running.json 是那一輪的）
                if (c.IsDraft || c.IsReplica || handledByAppResume.Contains(c.Meta.Id))
                {
                    DeleteRunMarker(c);
                    continue;
                }
                EnsureItems(c);
                var context = InterruptedTurnRules.Context(c.Meta.Provider, record, cli.GetIdentity(c.Meta.Provider)?.Version);
                if (DispatchInterruptedTurn(c, record, context, now, perProvider, abandonedUpdate: false) is { } start)
                    starts.Add(start);
                else
                    notContinued++;
            }
            catch (Exception ex)
            {
                Log.Error($"重新啟動後接續對話 {c.Meta.Id} 失敗", ex);
            }
        }
        if (starts.Count > 0 || notContinued > 0)
            _ = ReportInterruptedResumeAsync(starts, notContinued);
    }

    /// <summary>
    /// 一個上次中斷的對話：可以自動接續的照同時重開上限分批排定接續（perProvider 是同一種代理前面已排的數目），回傳那個送出；
    /// 不自動接續的加提示、標頭給「繼續」，回傳 null。abandonedUpdate：放棄 app 更新時就地接續被排空停掉的背景工作（提示的說法不同）
    /// </summary>
    private Task<InterruptedStart>? DispatchInterruptedTurn(ConversationState c, RunningTurnRecord record, ContinuationContext context, DateTime nowUtc,
        Dictionary<AgentProvider, int> perProvider, bool abandonedUpdate)
    {
        if (InterruptedTurnRules.WhyNotContinue(record, nowUtc) is { } reason)
        {
            MarkInterruptedNotContinued(c, InterruptedTurnRules.DescribeNotContinued(record, reason, markdown: true, abandonedUpdate),
                InterruptedTurnRules.DescribeNotContinued(record, reason, markdown: false, abandonedUpdate), CliRestartRules.CanContinueManually(reason), context);
            return null;
        }
        int index = perProvider.TryGetValue(c.Meta.Provider, out var n) ? n : 0;
        perProvider[c.Meta.Provider] = index + 1;
        return StartInterruptedContinuationAsync(c, record, context, ResumeIntentRules.StartDelay(index, c.Meta.Provider), abandonedUpdate);
    }

    /// <summary>
    /// 放棄這次 app 更新（AbortAppUpdate，FinishUpdateBarrier 之前收集的 targets）：排空逾時被停掉背景工作的、等背景工作的對話沒有停下待接續的一輪
    /// （不會被屏障就地接續），這裡照 running.json 就地接續——隱藏送出請代理確認背景工作做到哪裡、接著做（第一句說 app 為了更新停下了 CLI）。
    /// 停下沒有生效、背景工作還在跑的照常等它回報
    /// </summary>
    private void ContinueBackgroundStoppedByApp(IEnumerable<ConversationState> targets, Dictionary<AgentProvider, int> perProvider)
    {
        var version = AppVersionInfo.VersionText;
        var now = DateTime.UtcNow;
        foreach (var c in targets)
        {
            try
            {
                c.RunMarkerStoppedByApp = false;
                if (disposed || c.Running || !IsLive(c) || BackgroundOf(c) != null)
                    continue;
                c.RunMarkerWaitingBackground = false;
                // 停下前寫的快照（讀不到就照現況補一份：背景工作已經沒了，說不出是哪些）；接續的一輪開始時（HookTurnStarted）重寫
                var record = RunningTurnStore.TryLoad(store.GetConversationDirectory(c.Meta.Id)).Record
                             ?? new RunningTurnRecord
                             {
                                 StartedAt = UnixMilliseconds(DateTime.Now),
                                 SeenAt = UnixMilliseconds(DateTime.Now),
                                 ClosedByApp = true,
                                 BackgroundWork = true,
                                 WaitingBackground = true,
                                 CliVersion = CurrentVersionOf(c) ?? c.Meta.LastCliVersion,
                                 AutoResumes = c.RunAutoResumes,
                             };
                EnsureItems(c);
                var context = InterruptedTurnRules.Context(c.Meta.Provider, record, cli.GetIdentity(c.Meta.Provider)?.Version)
                    with { AppRestart = null, AppFrom = version, AppTo = version };
                Log.Info($"放棄 app 更新：對話 {c.Meta.Id} 的背景工作在排空時被停掉，就地接續");
                _ = DispatchInterruptedTurn(c, record, context, now, perProvider, abandonedUpdate: true);
            }
            catch (Exception ex)
            {
                Log.Error($"放棄 app 更新後接續對話 {c.Meta.Id} 被停掉的背景工作失敗", ex);
            }
        }
    }

    /// <summary>resume.json 列出的對話（RunPendingResume 取走 pendingResume 之前呼叫）；沒有排定接續時是空的。不丟例外</summary>
    private HashSet<string> AppResumeConversationIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in pendingResume?.File?.Conversations ?? Enumerable.Empty<ResumeConversation>())
        {
            if (!string.IsNullOrEmpty(entry?.Id))
                ids.Add(entry.Id);
        }
        return ids;
    }

    /// <summary>
    /// 等分批的時間到了就送出接續（只等開始送出）；回傳是否送出了接續。這段期間使用者已經自己送出（新的一輪）、刪掉對話的不接續；
    /// CLI 正在更新時（這時啟動 CLI 一定失敗，隱藏送出又不排進佇列）改成沒有接續、標頭給「繼續」
    /// </summary>
    private async Task<InterruptedStart> StartInterruptedContinuationAsync(ConversationState c, RunningTurnRecord record, ContinuationContext context, TimeSpan delay,
        bool abandonedUpdate = false)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
        if (disposed || c.Running || !IsLive(c))
            return InterruptedStart.Skipped;
        if (IsCliUpdateBlocking(c.Meta.Provider))
        {
            Log.Info($"對話 {c.Meta.Id} 重新啟動後沒有自動接續：{CliRestartRules.ProductName(c.Meta.Provider)} CLI 正在更新");
            var text = InterruptedTurnRules.DescribeCliUpdating(record, c.Meta.Provider, abandonedUpdate);
            MarkInterruptedNotContinued(c, text, text, canContinue: true, context);
            return InterruptedStart.NotContinued;
        }
        Log.Info($"對話 {c.Meta.Id} 上次{(record.WaitingBackground ? "在等背景工作" : "還在執行")}時被中斷（{(abandonedUpdate ? "放棄的 app 更新" : record.ClosedByApp ? "app 結束" : "app 沒有正常結束")}），自動接續（第 {record.AutoResumes + 1} 次）");
        c.PendingAutoResumes = record.AutoResumes + 1;
        Journal("resume.turn", c, new JObject
        {
            ["result"] = "continued",
            ["waitingBackground"] = record.WaitingBackground ? true : null,
            ["closedByApp"] = record.ClosedByApp,
            ["abandonedUpdate"] = abandonedUpdate ? true : null,
            ["autoResumes"] = record.AutoResumes + 1,
            ["bg"] = record.BackgroundTasks is { Count: > 0 } tasks ? new JArray(tasks.Select(t => BackgroundJournalRules.Clip(t))) : null,
        });
        _ = SendCoreAsync(c, ContinuationRules.Build(context), new List<AgentAttachment>(),
            InterruptedTurnRules.ContinuedNotice(record, abandonedUpdate), hiddenNoticeKind: AgentNoticeKind.AppUpdate);
        return InterruptedStart.Sent;
    }

    /// <summary>
    /// 沒有自動接續的對話：加提示（Markdown）、標頭給「繼續」（純文字的說明；按鈕的資料是這次從 running.json 重建的接續內容）、
    /// 刪掉 running.json（決定好了，下次啟動不再處理）。沒有顯示在任何畫面上就標未讀
    /// </summary>
    private void MarkInterruptedNotContinued(ConversationState c, string notice, string detail, bool canContinue, ContinuationContext context)
    {
        Journal("resume.turn", c, new JObject { ["result"] = "notContinued", ["detail"] = BackgroundJournalRules.Clip(detail), ["canContinue"] = canContinue });
        AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = notice, NoticeKind = AgentNoticeKind.AppUpdate });
        c.Restart.Result = new RestartResultView(CliRestartState.NotContinued, null, null, detail, canContinue, context);
        if (c.VisibleViewIds.Count == 0)
            c.Unread = true;
        DeleteRunMarker(c);
        SaveConversation(c, saveItems: true);
        MarkHeaderDirty(c);
        MarkListDirty();
    }

    /// <summary>
    /// 所有接續都開始送出（或改成沒有接續）之後通知一次：自動接續數只算真的送出的；分批期間使用者自己送出、刪掉的
    /// 不算沒接續（已經有人處理），送出時 CLI 正在更新而改成沒接續的算
    /// </summary>
    private async Task ReportInterruptedResumeAsync(List<Task<InterruptedStart>> starts, int notContinued)
    {
        int continued = 0;
        foreach (var start in starts)
        {
            InterruptedStart result;
            try
            {
                result = await start;
            }
            catch (Exception ex)
            {
                Log.Warn($"重新啟動後接續的送出有失敗：{ex.Message}");
                result = InterruptedStart.NotContinued;
            }
            if (result == InterruptedStart.Sent)
                continued++;
            else if (result == InterruptedStart.NotContinued)
                notContinued++;
        }
        Log.Info($"重新啟動後接續上次中斷的對話：自動接續 {continued}、沒有接續 {notContinued}");
        if (disposed)
            return;
        if (InterruptedTurnRules.Summary(continued, notContinued) is { } summary)
            host.Notify("Clode Myriagent", summary, null);
        InterruptedResumeDispatched?.Invoke((continued, notContinued));
    }

    private enum InterruptedStart
    {
        Sent,
        NotContinued,

        /// <summary>分批期間已經有人處理（使用者自己送出、刪掉對話、app 正在結束）</summary>
        Skipped,
    }

    private static long UnixMilliseconds(DateTime time) => new DateTimeOffset(time).ToUnixTimeMilliseconds();

    /// <summary>測試模擬當掉：之後什麼都不做、什麼都不寫（不存檔、不寫 running.json 的最後快照、不關程序），只停掉計時器</summary>
    internal void AbandonForTest()
    {
        disposed = true;
        maintenanceTimer.Dispose();
        CancelUpdateBarriers();
    }
}
