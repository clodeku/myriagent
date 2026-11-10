using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Speech;
using Myriagent.Sync;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

internal sealed partial class AgentHub
{
    // ───────────────────────────── 會話事件 ─────────────────────────────

    /// <summary>這次執行已提示過 Remote Control 開不起來的帳戶</summary>
    private readonly HashSet<string> remoteControlNoticeAccounts = new(StringComparer.Ordinal);

    private void OnSessionEvent(ConversationState c, AgentEvent ev)
    {
        c.LastActivity = DateTime.Now;
        EnsureItems(c);
        // 背景活動日誌：背景工作、子代理、程序的開始與結束（改狀態之前：要和上一次回報的比；AgentHub.Journal.cs）
        JournalSessionEvent(c, ev);
        // 帳戶卡的子代理數：Claude 在背景跑的子代理（local_agent 工作）的開始與結束（AgentHub.SubagentCount.cs）
        TrackSubagentTasks(c, ev);

        switch (ev)
        {
            case AgentSessionStartedEvent started:
                if (!string.IsNullOrWhiteSpace(started.SessionId))
                {
                    bool changed = c.Meta.SessionId != started.SessionId || !c.Meta.SessionStarted || c.Meta.SessionAccountId != c.Meta.AccountId;
                    // 回溯的分支（--resume-session-at＋--fork-session）開始了：新的 session 就是分支，之後照常續接它（AgentHub.Rewind.cs）
                    if (c.Meta.ResumeAt != null && c.Meta.SessionId != started.SessionId)
                        c.Meta.ResumeAt = null;
                    // 延伸出來的對話從原對話逐字稿的副本分支好了：之後照常續接自己的 session，副本不再需要（AgentHub.Fork.cs）
                    if (c.Meta.ForkSourceTranscript != null)
                    {
                        if (c.Meta.ResumeAt != null)
                            c.Meta.ResumeAt = null;
                        ForgetForkSource(c);
                        changed = true;
                    }
                    if (changed) c.CodexGoal = null;
                    c.Meta.SessionId = started.SessionId;
                    c.Meta.SessionStarted = true;
                    c.Meta.SessionAccountId = c.Meta.AccountId;
                    if (changed && !c.IsDraft)
                        SaveConversation(c, saveItems: false);
                }
                break;

            case AgentSessionTitleEvent title:
                OnAgentSessionTitle(c, title.Title);
                break;

            case AgentTextDeltaEvent delta:
            {
                var item = GetBlockItem(c, delta.BlockId, AgentItemKind.Assistant);
                item.Text += delta.Delta;
                item.Status = AgentItemStatus.Running;
                SetStatus(c, "回覆中…");
                MarkItemDirty(c, item);
                break;
            }

            case AgentTextCompletedEvent completed:
            {
                if (!c.BlockItems.TryGetValue(completed.BlockId, out var item) && string.IsNullOrWhiteSpace(completed.Text))
                    break;
                item ??= GetBlockItem(c, completed.BlockId, AgentItemKind.Assistant);
                if (!string.IsNullOrEmpty(completed.Text))
                    item.Text = completed.Text;
                if (string.IsNullOrWhiteSpace(item.Text))
                {
                    RemoveItem(c, item);
                    break;
                }
                item.Status = AgentItemStatus.Done;
                foreach (var media in AgentHubRules.FindMediaToShow(item.Text, fileExists, RelativeMediaBases(c, item.Text), GetWorkingDirectory(c)))
                {
                    if (!item.MediaPaths.Contains(media, StringComparer.OrdinalIgnoreCase))
                        item.MediaPaths.Add(media);
                }
                MarkItemDirty(c, item);
                // 回覆完成：要顯示的媒體（含 Markdown 引用的本機圖片）在背景留一份（AgentHub.MediaKeep.cs）
                KeepMediaLater(c, item, markdown: true);
                break;
            }

            case AgentThinkingDeltaEvent thinking:
            {
                var item = GetBlockItem(c, thinking.BlockId, AgentItemKind.Thinking);
                item.Text += thinking.Delta;
                item.Status = AgentItemStatus.Running;
                SetStatus(c, "思考中…");
                MarkItemDirty(c, item);
                break;
            }

            case AgentToolStartedEvent tool:
            {
                FinishThinking(c);
                var item = new AgentChatItem
                {
                    Kind = AgentItemKind.Tool,
                    Status = AgentItemStatus.Running,
                    ToolName = tool.ToolName,
                    ToolKind = tool.ToolKind,
                    Title = tool.Title,
                    Text = tool.ArtifactMarkdown ?? string.Empty,
                    Detail = tool.Detail,
                    FilePath = tool.FilePath,
                    Diff = tool.Diff?.ToList(),
                };
                c.ToolItems[tool.ToolId] = item;
                AddItem(c, item);
                SetStatus(c, $"{tool.ToolName}：{ClaudeStreamRules.FirstLine(tool.Title)}");
                // 開始高風險操作（部署、推送）：立刻記進 running.json，這時當掉也不會在下次啟動自動接續而重做
                OnRunMarkerToolChanged(c, item, started: true);
                // 指令碰到 Windows 排程：記下這個對話，讀到的變更算它做的（AgentHub.Schedules.cs）
                OnScheduleToolStarted(c, item);
                // 子代理開始：帳戶卡的子代理數（AgentHub.SubagentCount.cs）
                OnSubagentToolChanged(c, item);
                break;
            }

            case AgentToolOutputEvent output:
                // 一輪結束後還在跑的背景指令（Codex）的輸出也接在它的卡片上（「詳細」看得到）
                if (c.ToolItems.TryGetValue(output.ToolId, out var outputItem) || c.BackgroundToolItems.TryGetValue(output.ToolId, out outputItem))
                {
                    outputItem.Output = AgentHubRules.AppendToolOutput(outputItem.Output, output.Delta);
                    MarkItemDirty(c, outputItem);
                }
                break;

            case AgentToolCompletedEvent done:
                // 一輪結束後才跑完的背景指令（Codex）也找得到它的卡片
                if (TryTakeToolItem(c, done.ToolId, out var toolItem))
                {
                    toolItem.Status = done.Status;
                    // 還在等核准就被重開取消（Claude 停下舊程序時把畫了卡、還沒有結果的工具都結束成「被中斷、結果未知」）：它根本沒有執行
                    bool notApproved = IsUnapproved(c, toolItem) && done.Status == AgentItemStatus.Cancelled
                                       && done.Output == CliRestartRules.InterruptedToolOutput(c.Meta.Provider);
                    ForgetUnapproved(c, toolItem);
                    if (notApproved)
                        toolItem.Output = CliRestartRules.NotApprovedToolOutput;
                    else if (done.Output != null)
                        toolItem.Output = AgentHubRules.TrimToolOutput(done.Output);
                    if (done.Status == AgentItemStatus.Done && !string.IsNullOrEmpty(done.ArtifactMarkdown))
                        toolItem.Text = done.ArtifactMarkdown;
                    foreach (var media in done.MediaPaths.Where(p => AgentAttachmentRules.IsLocalDrivePath(p) && fileExists(p)))
                    {
                        if (!toolItem.MediaPaths.Contains(media, StringComparer.OrdinalIgnoreCase))
                            toolItem.MediaPaths.Add(media);
                    }
                    MarkItemDirty(c, toolItem);
                    KeepMediaLater(c, toolItem, markdown: false);
                    OnRunMarkerToolChanged(c, toolItem, started: false);
                    OnSubagentToolChanged(c, toolItem);
                }
                SetStatus(c, "思考中…");
                // 工具結束：可能到了安全點（等著換版的對話重新檢查）
                RecheckRestart(c);
                break;

            case AgentQuestionEvent question:
            {
                FinishThinking(c);
                var item = new AgentChatItem
                {
                    Kind = AgentItemKind.Question,
                    Status = AgentItemStatus.Waiting,
                    // 不是代理自己問的（MCP 伺服器的 elicitation）：卡片標頭寫出來源
                    ToolName = question.Source,
                    Detail = question.Detail,
                    FilePath = question.FilePath,
                    Questions = question.Questions.ToList(),
                    RequestKey = question.RequestKey,
                    AnswerAsMessage = question.AnswerAsMessage,
                    ProcessInstance = c.Restart.Instance,
                };
                // 卡片摘要 rd1（遠端回應時比對，D36；卡片的內容建立後不再改）
                item.RequestDigest = RequestDigestRules.Compute(item);
                c.RequestItems[question.RequestKey] = item;
                AddItem(c, item);
                SetStatus(c, "等待你回答問題");
                var questionText = question.Questions.FirstOrDefault()?.Text;
                NotifyAttention(c, "代理有問題想問你", questionText, ConversationAttentionKinds.Question, item.Id,
                    VoiceNotifyRules.Line(full => VoiceNotifyRules.Question(c.Meta.Title, full ? questionText : null)));
                if (question.AnswerAsMessage)
                    PauseForAsyncQuestion(c, item);
                break;
            }

            case AgentApprovalEvent approval:
            {
                FinishThinking(c);
                var item = new AgentChatItem
                {
                    Kind = approval.IsPlan ? AgentItemKind.PlanApproval : AgentItemKind.Approval,
                    Status = AgentItemStatus.Waiting,
                    ToolName = approval.ToolName,
                    ToolKind = approval.ToolKind,
                    Title = approval.Title,
                    Detail = approval.Detail,
                    FilePath = approval.FilePath,
                    Diff = approval.Diff?.ToList(),
                    CanAllowForSession = approval.CanAllowForSession,
                    AllowSessionLabel = approval.AllowSessionLabel,
                    RequestKey = approval.RequestKey,
                    ProcessInstance = c.Restart.Instance,
                };
                // 核准卡的遠端風險（WP4.4 的 ClassifyRemoteRisk；沒分類是 null，電腦端當成非一般，D9）與卡片摘要 rd1（D36）
                if (!approval.IsPlan)
                {
                    AgentRemoteRisk? risk = null;
                    ClassifyRemoteRisk(c.Meta, item, ref risk);
                    item.Risk = risk;
                }
                item.RequestDigest = RequestDigestRules.Compute(item);
                c.RequestItems[approval.RequestKey] = item;
                // 核准對到已畫好的執行中工具卡：還沒核准＝還沒執行（執行到一半才要求核准的除外）
                if (!approval.ToolStarted && approval.ToolId is { } approvalToolId && c.ToolItems.TryGetValue(approvalToolId, out var pendingTool)
                    && pendingTool.Status == AgentItemStatus.Running)
                    c.UnapprovedTools[approval.RequestKey] = pendingTool;
                AddItem(c, item);
                // 切換時請求可能還在事件佇列裡；以現在的模式處理，避免漏掉晚到的核准或再發等待通知。
                if (TryAutoApprove(c, item))
                    break;
                SetStatus(c, approval.IsPlan ? "等待你確認計畫" : "等待你核准");
                NotifyAttention(c, approval.IsPlan ? "代理的計畫等你確認" : "代理要執行操作，等你核准", $"{approval.ToolName}：{approval.Title}",
                    approval.IsPlan ? ConversationAttentionKinds.Plan : ConversationAttentionKinds.Approval, item.Id,
                    VoiceNotifyRules.Line(full => approval.IsPlan
                        ? VoiceNotifyRules.Plan(c.Meta.Title, full ? approval.Detail : null)
                        : VoiceNotifyRules.Approval(c.Meta.Title, approval.ToolName, full ? approval.Detail : null, full ? approval.FilePath : null)));
                break;
            }

            case AgentRequestResolvedEvent resolved:
                if (c.RequestItems.TryGetValue(resolved.RequestKey, out var resolvedItem) && resolvedItem.Status == AgentItemStatus.Waiting)
                {
                    resolvedItem.Status = AgentItemStatus.Cancelled;
                    MarkItemDirty(c, resolvedItem);
                }
                break;

            case AgentAutoApproveEvent autoApprove:
                if (c.RequestItems.TryGetValue(autoApprove.RequestKey, out var autoItem))
                    TryAutoApprove(c, autoItem);
                break;

            case AgentCodexPlanEvent proposed:
            {
                var key = "codex-plan:" + proposed.ItemId;
                if (!c.BlockItems.TryGetValue(key, out var card))
                {
                    card = new AgentChatItem { Kind = AgentItemKind.Tool, ToolKind = "plan", ToolName = "規劃內容", Title = "Codex 的執行計畫" };
                    c.BlockItems[key] = card;
                    AddItem(c, card);
                }
                card.Text = proposed.Completed ? proposed.Text : card.Text + proposed.Text;
                card.Status = proposed.Completed ? AgentItemStatus.Done : AgentItemStatus.Running;
                MarkItemDirty(c, card);
                MarkHeaderDirty(c);
                break;
            }

            case AgentTurnDiffEvent changes:
                UpdateTurnDiff(c, changes);
                break;

            case AgentPlanEvent plan:
            {
                if (c.PlanItem == null || !c.Items.Contains(c.PlanItem))
                {
                    c.PlanItem = new AgentChatItem { Kind = AgentItemKind.Plan, Status = AgentItemStatus.Running };
                    AddItem(c, c.PlanItem);
                }
                c.PlanItem.Text = plan.Explanation ?? string.Empty;
                c.PlanItem.PlanSteps = plan.Steps.ToList();
                c.PlanItem.Status = plan.Steps.Count > 0 && plan.Steps.All(s => s.Status == "completed") ? AgentItemStatus.Done : AgentItemStatus.Running;
                MarkItemDirty(c, c.PlanItem);
                break;
            }

            case AgentMediaEvent media:
                if (AgentAttachmentRules.IsLocalDrivePath(media.Path) && fileExists(media.Path))
                {
                    var mediaItem = new AgentChatItem { Kind = AgentItemKind.Assistant, MediaPaths = new List<string> { media.Path } };
                    AddItem(c, mediaItem);
                    KeepMediaLater(c, mediaItem, markdown: false);
                }
                break;

            case AgentTurnCompletedEvent turn:
                CompleteTurn(c, turn);
                break;

            case AgentTokenUsageEvent tokens:
                OnTokenUsage(c, tokens);
                break;

            case AgentContextUsageEvent context:
            {
                var next = ContextUsageRules.Apply(c.ContextUsage, context);
                if (next != c.ContextUsage)
                {
                    c.ContextUsage = next;
                    MarkHeaderDirty(c);
                }
                break;
            }

            case AgentColdResumeQuotaEvent coldResume:
                // 快取過期後整份重送的額度成本（送出前的快取提醒用；記在 meta：app 重開之後回到閒置的對話照樣提醒）
                if (c.Meta.ColdResume != coldResume.Quota)
                {
                    c.Meta.ColdResume = coldResume.Quota;
                    MarkHeaderDirty(c);
                    if (!c.IsReplica && !c.IsDraft)
                        SaveConversation(c, saveItems: false);
                }
                break;

            case AgentRateLimitEvent rate:
                accounts.ReportSessionRateLimit(c.Meta.AccountId, rate.Provider, rate.Raw);
                break;

            case AgentQuotaExhaustedEvent quota:
                // 額度用完：這一輪結束時決定要不要換帳戶繼續（AgentHub.AccountSwitch.cs）
                OnQuotaExhausted(c, quota);
                break;

            case AgentSubagentUsageEvent subagent:
                OnSubagentUsage(c, subagent.Usage);
                break;

            case AgentNoticeEvent notice:
                AddItem(c, new AgentChatItem { Kind = notice.IsError ? AgentItemKind.Error : AgentItemKind.Notice, Text = notice.Message });
                break;

            // 每次啟動 CLI 程序都會回報一次，內容通常和上次一樣：沒變就不寫設定檔（UI 執行緒上約 100 KB 並落盤）、不重送標頭
            case AgentModelsEvent models:
                if (!UpdateModels(c.Meta.AccountId, models.Models))
                    break;
                settings.Save();
                foreach (var other in conversations.Values.Where(x => x.Meta.AccountId == c.Meta.AccountId && x.ViewIds.Count > 0))
                    MarkHeaderDirty(other);
                break;

            case AgentCommandsEvent commands:
                if (ConversationDefaultsRules.SameCommands(settings.Commands.GetValueOrDefault(c.Meta.AccountId), commands.Commands))
                    break;
                settings.Commands[c.Meta.AccountId] = commands.Commands.ToList();
                settings.Save();
                foreach (var other in conversations.Values.Where(x => x.Meta.AccountId == c.Meta.AccountId && x.ViewIds.Count > 0))
                    MarkHeaderDirty(other);
                break;

            case AgentCustomizationsEvent customizations:
                OnClaudeCustomizations(c, customizations);
                break;

            case AgentCodexGoalEvent codexGoal:
                c.CodexGoal = codexGoal.Goal?.DeepClone() as JObject;
                MarkHeaderDirty(c);
                break;
            case AgentGoalEvent goal:
                if (!string.Equals(c.Goal, goal.Condition, StringComparison.Ordinal))
                {
                    c.Goal = goal.Condition;
                    MarkHeaderDirty(c);
                }
                break;

            case AgentAccountInfoEvent info:
                accounts.UpdateAccountInfo(c.Meta.AccountId, info.Email, info.Plan);
                break;

            case AgentRemoteControlEvent remote:
                // 記下遠端工作階段：程序重開（換模型、閒置後再送、app 重開）時接回同一個
                if (c.Meta.RemoteControlSessionId != remote.BridgeSessionId)
                {
                    c.Meta.RemoteControlSessionId = remote.BridgeSessionId;
                    if (!c.IsDraft)
                        SaveConversation(c, saveItems: false);
                }
                break;

            case AgentRemoteControlFailedEvent failed:
                // 每個帳戶（這次執行）只在第一個對話提示：帳戶或組織不支援時，不讓每個對話都多一則
                if (remoteControlNoticeAccounts.Add(c.Meta.AccountId))
                    AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = failed.Message });
                else
                    Log.Info($"Remote Control（{c.Meta.AccountId}）：{failed.Message}");
                break;

            case AgentRemoteUserMessageEvent remoteMessage:
                OnRemoteUserMessage(c, remoteMessage);
                break;

            case AgentPermissionModeChangedEvent mode:
                if (c.Meta.PermissionMode != mode.Mode)
                {
                    c.Meta.PermissionMode = mode.Mode;
                    SaveConversation(c, saveItems: false);
                    MarkHeaderDirty(c);
                }
                break;

            case AgentProcessExitedEvent exited:
                // 程序結束：下一則訊息會自動重開並續接；執行中結束已由 TurnCompleted 回報。那個實例的等待卡片作廢（AgentHub.Restart.cs）
                OnProcessExited(c, exited);
                ForgetGoal(c);
                break;

            // ── CLI 熱更新（AgentHub.Restart.cs）──
            case AgentProcessStartedEvent processStarted:
                OnProcessStarted(c, processStarted);
                ForgetGoal(c);
                break;

            case AgentRunningVersionEvent runningVersion:
                OnRunningVersion(c, runningVersion);
                break;

            case AgentSafePointChangedEvent safePoint:
                OnSafePointChanged(c, safePoint);
                break;

            case AgentBackgroundWorkEvent background:
                OnBackgroundWork(c, background);
                break;

            case AgentBackgroundCommandFinishedEvent backgroundFinished:
                OnBackgroundCommandFinished(c, backgroundFinished);
                break;

            case AgentTurnStartedByCliEvent autoTurn:
                OnTurnStartedByCli(c, autoTurn);
                break;

            case AgentIdleTurnStartedEvent idleTurn:
                OnIdleTurnStarted(c, idleTurn);
                break;
        }
        EvaluatePendingAccountSwitch(c);
    }

    /// <summary>
    /// 閒置時 CLI 自己開了一輪（背景工作完成的通知、Codex 持續目標的下一輪等）：對話改成執行中——轉圈、可以停止、app 送出的插入或排進佇列、
    /// 換版等它結束，這一輪照常以 TurnCompleted 結束。已在執行中（送出比這個事件先到，會話已讓它接手）就不動
    /// </summary>
    private void OnIdleTurnStarted(ConversationState c, AgentIdleTurnStartedEvent ev)
    {
        if (c.Running || c.IsReplica)
            return;
        StartAutoTurn(c, ev.AfterBackgroundTask ? BackgroundTaskTurnStatus : ev.ForThreadGoal ? ThreadGoalTurnStatus : "思考中…");
        HookTurnStarted(c, startedByCli: true);
    }

    /// <summary>
    /// 代理在執行中送出非同步提問（Codex 的 request_user_input_async：CLI 立刻回「已收到」，模型會接著做下去）：停下這一輪等使用者回答
    /// （2026-10-28 使用者要求；AgentHubRules.PausesForAsyncQuestion）。不是使用者按停止：結束時不加「已停止」、佇列不暫停，
    /// 答案照舊當作下一則訊息送出。換版重開途中不停（重開本來就會停下這一輪再接續）
    /// </summary>
    private void PauseForAsyncQuestion(ConversationState c, AgentChatItem question)
    {
        if (!c.Running || c.StopRequested || c.IsReplica || c.Session == null || !AgentHubRules.PausesForAsyncQuestion(c.Meta.Provider) || IsRestartInFlight(c))
            return;
        c.PausedQuestion = question;
        if (c.PausingForQuestion)
            return;
        c.PausingForQuestion = true;
        Log.Info($"對話 {c.Meta.Id} 的代理送出非同步提問，停下這一輪等使用者回答");
        Journal("turn.pauseForQuestion", c, new JObject { ["question"] = BackgroundJournalRules.Clip(question.Questions.FirstOrDefault()?.Text) });
        _ = InterruptForQuestionAsync(c, c.Session);
    }

    private async Task InterruptForQuestionAsync(ConversationState c, IAgentSession session)
    {
        try
        {
            await session.InterruptAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"為了等回答停下這一輪失敗：{ex.Message}");
        }
    }

    private void CompleteTurn(ConversationState c, AgentTurnCompletedEvent turn)
    {
        // 交給 CLI 的 /clear：正常結束才算清掉了（CLI 已換新的 session，由 AgentSessionStartedEvent 記下）；失敗或停止就照舊顯示
        bool cleared = c.ClearTurn && turn.Outcome == AgentTurnOutcome.Completed;
        c.ClearTurn = false;
        // 換版重開期間舊程序的這一輪結束：不加「已停止」、佇列不暫停，由重開的結果接續或收尾
        if (SwallowTurnEndDuringRestart(c, turn))
            return;
        FinishTurnDiff(c);

        // Codex 的拒絕會讓這一輪以 interrupted 結束：使用者沒按停止就不算「已停止」，拒絕附的說明與排隊訊息照常接著送
        bool endedByDeny = turn.Outcome == AgentTurnOutcome.Interrupted && c.DeniedThisTurn && !c.StopRequested;
        // 為了等非同步提問的回答而停下的（PauseForAsyncQuestion）：也不算「已停止」，作答（排進佇列的答案）照常接著送
        bool pausedForQuestion = turn.Outcome == AgentTurnOutcome.Interrupted && c.PausingForQuestion && !c.StopRequested;
        c.PausingForQuestion = false;
        var outcome = endedByDeny || pausedForQuestion ? AgentTurnOutcome.Completed : turn.Outcome;
        // 執行中手動選了另一個帳戶：立即換的（這一輪是為了換帳戶停下的）結束時換過去、從中斷處接著做；
        // 這一輪結束時才換的照一般的結束收尾再換（只有原帳戶額度用完而失敗時接著做，AccountSwitchRules.ContinuesAfterUserSwitch）
        var userSwitch = TakeUserAccountSwitch(c);
        bool quotaExhausted = c.QuotaExhausted != null && !c.StopRequested && settings.AutoSwitchAccountOnLimit;
        // 額度用完：決定要不要換到還有額度的帳戶繼續（取用並清掉這一輪的 QuotaExhausted；使用者按了停止或自己選了帳戶就不換）
        var accountSwitch = PlanAccountSwitch(c, turn.Outcome, c.StopRequested || userSwitch != null);
        bool continueOnChosen = userSwitch != null && AccountSwitchRules.ContinuesAfterUserSwitch(userSwitch.AtTurnEnd, turn.Outcome, quotaExhausted);
        // 換帳戶接著做（或立即換、停止還沒生效就做完了）：佇列不暫停、拒絕的說明排進佇列；這一輪結束時才換、不接續的照一般的結束
        bool switching = accountSwitch?.Account != null || userSwitch is { AtTurnEnd: false } || continueOnChosen;
        // API 伺服器暫時無法服務（Claude 529、Antigravity 503）而失敗：等一下自動重試（AgentHub.OverloadRetry.cs）；按了停止、這一輪結束時要換帳戶的不重試
        var (overloadRetry, overloadGaveUp) = PlanOverloadRetry(c, turn, c.StopRequested, switching || userSwitch != null);
        c.StopRequested = false;
        c.DeniedThisTurn = false;

        // Codex 的背景指令（這一輪正常結束時還在跑）：卡片留在執行中，之後跑完才結束
        var backgroundCards = KeepBackgroundToolCards(c, turn.Outcome);
        foreach (var item in c.Items.Where(i => i.Status == AgentItemStatus.Running && !backgroundCards.Contains(i)).ToList())
        {
            item.Status = item.Kind == AgentItemKind.Tool && turn.Outcome != AgentTurnOutcome.Completed
                ? AgentItemStatus.Cancelled
                : AgentItemStatus.Done;
            if (item.Kind is AgentItemKind.Assistant or AgentItemKind.Thinking && string.IsNullOrWhiteSpace(item.Text) && item.MediaPaths.Count == 0)
            {
                RemoveItem(c, item);
                continue;
            }
            MarkItemDirty(c, item);
        }

        foreach (var item in c.RequestItems.Values.Where(i => i.Status == AgentItemStatus.Waiting && !i.AnswerAsMessage).ToList())
        {
            item.Status = AgentItemStatus.Cancelled;
            MarkItemDirty(c, item);
        }

        // Codex 拒絕時附的說明：正常結束就接著送（下面）；失敗時排到佇列最前面、跟著佇列暫停，按送出才繼續；使用者按停止就不送
        var feedback = AgentHubRules.BuildDenyFeedbackMessage(c.PendingFeedback);
        var feedbackOrigin = DenyFeedbackRules.OriginOf(c.PendingFeedback);
        c.PendingFeedback.Clear();
        // 自動換帳戶時一樣：說明排在接續之後（接續的一輪正常結束後照佇列送）
        if (feedback != null && (turn.Outcome == AgentTurnOutcome.Failed || switching))
        {
            c.Queue.Insert(0, new QueuedMessage { Text = feedback, RemoteOrigin = feedbackOrigin });
            feedback = null;
        }

        switch (turn.Outcome)
        {
            case AgentTurnOutcome.Failed:
                AddItem(c, new AgentChatItem { Kind = AgentItemKind.Error, Text = string.IsNullOrWhiteSpace(turn.ErrorMessage) ? "代理執行失敗" : turn.ErrorMessage! });
                if (accountSwitch?.NoCandidateNotice is { } failedNoCandidate)
                    AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = failedNoCandidate });
                if (overloadRetry is { } scheduledAttempt)
                    AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = OverloadRetryRules.ScheduledNotice(turn.ServerBusy, scheduledAttempt, GetOverloadRetryDelay(turn.ServerBusy)), NoticeKind = AgentNoticeKind.OverloadRetry });
                else if (overloadGaveUp)
                    AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = OverloadRetryRules.GaveUpNotice(turn.ServerBusy) });
                // 自動換帳戶、API 暫時無法服務自動重試時佇列不暫停：接續（重試）的一輪正常結束後照常接著送
                if (c.Queue.Count > 0 && !switching && overloadRetry == null)
                    AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = "排隊的訊息已暫停，按送出繼續" });
                break;
            case AgentTurnOutcome.Interrupted when pausedForQuestion:
                // 提問卡寫著「回答會當作下一則訊息送出」，畫面上就是在等使用者回答
                break;
            case AgentTurnOutcome.Interrupted when endedByDeny:
                if (feedback == null)
                    AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = "你拒絕了這個操作，這一輪已結束" });
                break;
            case AgentTurnOutcome.Interrupted:
                // 為了換帳戶停下的：不算「已停止」（換過去之後加換帳戶的提示、佇列照常接著送）；這一輪結束時才換的是使用者按了停止，照常
                // Claude 按停止不砍背景工作（initialize 的 perTaskStopAffordance）：還有背景工作在跑時說明它們繼續執行
                if (userSwitch is not { AtTurnEnd: false })
                    AddItem(c, new AgentChatItem
                    {
                        Kind = AgentItemKind.Notice,
                        Text = BackgroundWorkRules.StoppedTurnNotice(c.Queue.Count > 0,
                            c.Meta.Provider == AgentProvider.Claude && BackgroundOf(c) is { } left ? left.Count : 0),
                    });
                break;
            default:
                if (accountSwitch?.NoCandidateNotice is { } noCandidate)
                    AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = noCandidate });
                break;
        }
        // 額度用完、沒有帳戶可以接手：等重置後自動接著做（AgentHub.QuotaWait.cs；使用者自己選了帳戶時不等）
        if (accountSwitch is { Account: null, WaitUntil: { } waitUntil } && userSwitch == null)
            ScheduleQuotaWait(c, waitUntil);
        // 畫面藏起這則之前的（含 /clear 那則使用者訊息），排在後面的訊息照常接著送。CLI 換成的新 session 沒有名稱：照對話標題命名
        if (cleared)
        {
            AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = AgentHubRules.ClearNoticeText, NoticeKind = AgentNoticeKind.Clear });
            RenameAgentSession(c);
            // CLI 換成新的 session：之前的上下文用量不再適用，等下一次呼叫
            c.ContextUsage = null;
        }

        // 上一輪的 token 數：CLI 沒回報（停止、失敗）時用這一輪即時記到的（TurnTokenRules.Final），畫面不會跳回更早一輪
        if (TurnTokenRules.Final(turn.Usage, c.TurnTokens) is { } finalUsage)
            c.LastUsage = finalUsage;
        CloseTurnTokens(c);
        c.Running = false;
        c.StatusText = string.Empty;
        // 掛鉤在 Running 改回 false 之後（接收者看到的是已結束），接著送的下一輪之前
        HookTurnCompleted(c, outcome, turn.Usage, continues: switching || overloadRetry != null);
        c.BlockItems.Clear();
        c.ToolItems.Clear();
        c.UnapprovedTools.Clear();
        foreach (var key in c.RequestItems.Where(kv => !kv.Value.AnswerAsMessage).Select(kv => kv.Key).ToList())
            c.RequestItems.Remove(key);
        c.PlanItem = null;
        c.Meta.UpdatedAt = DateTime.Now;
        // 沒有顯示在任何畫面上（藏在其他分頁後面、分頁已關、切到儀表板）：完成、失敗或停止都標未讀
        if (c.VisibleViewIds.Count == 0)
            c.Unread = true;
        SaveConversation(c, saveItems: true);
        UpdateRunningCounts();
        MarkHeaderDirty(c);
        MarkListDirty();
        accounts.ReportTurnCompleted(c.Meta.AccountId);
        // 代理可能在這一輪新增或調整了工作資料夾的 .agents/ Skills／Workflows／Agents：背景重掃一次
        RequestAntigravityWorkspaceScan(c, force: true);
        // CLI 熱更新：自動接續的狀態清掉；等著換版的對話現在處理（閒置的關程序，下一則訊息用新版）
        OnTurnEndedForRestart(c);

        // 執行中手動換帳戶：立即換的這一輪被停下（或失敗）就換過去並隱藏送出接續訊息；停止還沒生效這一輪就自己做完了，只換帳戶（同沒在執行時），
        // 排隊的訊息照常在新帳戶送。這一輪結束時才換的只換帳戶（正常結束時排隊的訊息在新帳戶送，停止與失敗照常暫停佇列），
        // 原帳戶額度用完而失敗時才換過去接著做
        if (userSwitch != null)
        {
            if (continueOnChosen)
            {
                ContinueOnChosenAccount(c, userSwitch);
                return;
            }
            if (accounts.Find(userSwitch.AccountId) is { } chosen)
            {
                // 這一輪結束了、背景工作還在跑（換帳戶會關掉程序、背景工作跟著停；2026-10-27 實例：兩個背景擷取在換帳戶時被停掉，代理不知道）：
                // 這一輪結束時才換的等背景工作都結束、代理處理完結果再換（AgentHub.AccountSwitch.cs）；立即換的（停止還沒生效就做完了）
                // 照換，隱藏送出接續訊息告訴新帳戶哪些背景工作被停掉了（排隊的訊息等接續的一輪結束後照常送）
                if (c.Session?.HasLiveBackgroundWork == true)
                {
                    if (userSwitch.AtTurnEnd)
                    {
                        DeferAccountSwitch(c, chosen, userSwitch.RememberAccount);
                    }
                    else
                    {
                        SwitchAccountStoppingBackground(c, chosen, userSwitch.RememberAccount);
                        return;
                    }
                }
                else
                {
                    ApplyChosenAccount(c, chosen, userSwitch.RememberAccount);
                }
            }
        }
        // 額度用完、有帳戶可以接手：換過去並隱藏送出接續訊息（佇列與拒絕的說明等接續的一輪正常結束後照常送）
        else if (switching)
        {
            ContinueOnAnotherAccount(c, accountSwitch!);
            return;
        }

        // 接著送（只在這一輪正常結束時自動送）：Codex 拒絕時附的說明優先，再來是排隊的訊息
        if (feedback != null && outcome == AgentTurnOutcome.Completed)
        {
            _ = SendCoreAsync(c, feedback, new List<AgentAttachment>(), remoteOrigin: feedbackOrigin, returnHome: false);
            return;
        }
        if (outcome == AgentTurnOutcome.Completed && c.Queue.Count > 0)
        {
            var next = c.Queue[0];
            if (ReferenceEquals(next, c.SteeringMessage))
            {
                // 這則正在插進剛結束的這一輪（SteerAsync 還沒回來）：插進去了就不能再送一次，交給 SteerQueuedAsync 有了結果再接著送
                c.SendQueueAfterSteer = true;
                PostComposer(c);
                return;
            }
            c.Queue.RemoveAt(0);
            PostComposer(c);
            _ = SendCoreAsync(c, next.Text, next.Attachments, answerFor: next.AnswerFor, displayText: next.DisplayText, remoteOrigin: next.RemoteOrigin);
            return;
        }
        // 這一輪進行中跑完的 Codex 背景指令：現在接續（停止、失敗的這一輪不接續——使用者不要它接著做，或要先看錯誤；
        // Codex 的持續目標還沒完成時 codex 馬上自己開下一輪、結果已在它的上下文裡，不另外送，免得兩邊同時開一輪）
        if (outcome == AgentTurnOutcome.Completed && !turn.ContinuesOnItsOwn)
        {
            if (ContinueAfterBackground(c))
            {
                PostComposer(c);
                return;
            }
        }
        else
        {
            c.PendingBackgroundFinished.Clear();
        }

        PostComposer(c);
        // API 暫時無法服務：排定自動重試；不念失敗、不要求注意（每分鐘念一次「失敗」太吵），重試到上限才照一般的失敗通知
        if (overloadRetry is { } retryAttempt)
        {
            ScheduleOverloadRetry(c, turn.ServerBusy, retryAttempt);
            return;
        }
        // 清除不是代理的回覆：不通知完成
        if (cleared)
            return;
        if (turn.Outcome == AgentTurnOutcome.Completed)
        {
            // Codex 的持續目標還沒完成：codex 接著自己開下一輪（AgentIdleTurnStartedEvent），還沒真的完成，不念、不通知。
            // 模型標記目標完成的那一輪（ContinuesOnItsOwn 是 false）才通知
            if (!turn.ContinuesOnItsOwn)
                NotifyAttention(c, BackgroundWorkRules.CompletedTitle(BackgroundOf(c) != null), c.Meta.Title, ConversationAttentionKinds.Completed, speech: CompletionSpeech(c));
        }
        else
        {
            host.RequestAttention(c.ViewIds);
            // 使用者按停止（或拒絕操作）結束的那一輪不念
            if (turn.Outcome == AgentTurnOutcome.Failed)
                AnnounceAttention(c, VoiceNotifyRules.Failed(c.Meta.Title, turn.ErrorMessage));   // 網頁版念伺服器以 conv.turn 帶出的失敗組的句子
        }
    }

    private AgentChatItem GetBlockItem(ConversationState c, string blockId, AgentItemKind kind)
    {
        if (c.BlockItems.TryGetValue(blockId, out var existing))
            return existing;
        if (kind == AgentItemKind.Assistant)
            FinishThinking(c);
        var item = new AgentChatItem { Kind = kind, Status = AgentItemStatus.Running };
        c.BlockItems[blockId] = item;
        AddItem(c, item);
        return item;
    }

    /// <summary>開始回覆或呼叫工具時，前面的思考區塊就算結束</summary>
    private void FinishThinking(ConversationState c)
    {
        foreach (var item in c.BlockItems.Values.Where(i => i.Kind == AgentItemKind.Thinking && i.Status == AgentItemStatus.Running))
        {
            item.Status = AgentItemStatus.Done;
            MarkItemDirty(c, item);
        }
    }

    /// <summary>
    /// 從 claude.ai 或 Claude App 送來的訊息：加成使用者訊息（標示來源）。開始新的一輪時對話標成執行中——
    /// 在 app 送出的訊息排進佇列等這一輪結束、停止鈕可以停下它，結束時照常存檔、標未讀
    /// </summary>
    private void OnRemoteUserMessage(ConversationState c, AgentRemoteUserMessageEvent ev)
    {
        AddItem(c, new AgentChatItem
        {
            Kind = AgentItemKind.User,
            Text = AgentHubRules.DescribeRemoteUserMessage(ev.Text, ev.ImageCount, ev.FileCount),
            Source = AgentItemSource.Remote,
        });
        if (ev.StartsTurn)
            StartAutoTurn(c, "思考中…");
        c.Meta.UpdatedAt = DateTime.Now;
        if (!c.IsDraft)
            SaveConversation(c, saveItems: true);
        MarkListDirty();
    }

    private void SetStatus(ConversationState c, string text)
    {
        if (!c.Running || c.StatusText == text)
            return;
        c.StatusText = text;
        MarkHeaderDirty(c);
    }

    /// <summary>
    /// 需要使用者注意：對話沒顯示在前景視窗的畫面上就標未讀、送系統通知、閃工作列。
    /// 要有一個顯示它（作用中分頁，見 visible）的 view 所在的視窗在前景才算看到：分頁藏在後面、
    /// 使用者在別的視窗操作、主視窗藏到系統匣或最小化，都算沒看到。
    /// <paramref name="kind"/>／<paramref name="itemId"/> 只給同步的掛鉤（ConversationAttentionKinds；其他提醒是 null）。
    /// <paramref name="speech"/>＝語音通知念的句子（VoiceNotifyRules；null 或 Silent＝不念、也不閃）：看得到也閃、也念（並排窗格開著時看得到不等於正在看）；
    /// 這台念完整版，同步掛鉤拿到兩個版本（網頁版依對話的同步政策念其中一個）
    /// </summary>
    private void NotifyAttention(ConversationState c, string title, string? detail, string? kind = null, string? itemId = null, VoiceLine? speech = null)
    {
        if (speech is { IsSilent: false })
            AnnounceAttention(c, speech.Full);
        bool seen = c.VisibleViewIds.Any(host.IsViewForeground);
        if (!seen)
            IsSeenRemotely(c.Meta.Id, ref seen);
        HookAttention(c, kind, itemId, speech ?? VoiceLine.Silent);
        if (!seen)
            c.Unread = true;
        MarkListDirty();
        MarkAccountsDirty();
        if (seen)
            return;
        host.RequestAttention(c.ViewIds);
        if (settings.Notifications)
        {
            var account = accounts.Find(c.Meta.AccountId);
            var message = string.IsNullOrWhiteSpace(detail) ? c.Meta.Title : ClaudeStreamRules.FirstLine(detail);
            host.Notify($"{title}（{account?.Label ?? c.Meta.Provider.ToString()}）", message, c.Meta.Id);
        }
    }

    /// <summary>
    /// 要你回來看的事（完成、失敗、提問、核准、計畫）：每個視窗的這個對話馬上開始閃，閃到使用者點它為止（flash；不管語音通知開不開，網頁自己記著閃到點了）；
    /// 語音通知開著時再念這句，聲音開始出來時另送帶 speaking 的 flash（還在閃的那個對話的標頭另外閃三下，一聽到就找得到是哪個對話）。
    /// app 結束途中關掉程序造成的結束不閃、不念
    /// </summary>
    private void AnnounceAttention(ConversationState c, string text)
    {
        if (disposed)
            return;
        var conversationId = c.Meta.Id;
        FlashConversation(conversationId, speaking: false);
        if (settings.VoiceNotifications)
            host.Speak(text, VoiceTuningSetting, () => FlashConversation(conversationId, speaking: true));
    }

    /// <summary>設定的語速與音調（正規化過；設定檔被手改成範圍外的值也照範圍念）</summary>
    private VoiceTuning VoiceTuningSetting
        => new(SpeechAudioRules.NormalizeSpeed(settings.VoiceSpeed), SpeechAudioRules.NormalizePitch(settings.VoicePitch));

    /// <summary>
    /// 要所有視窗閃這個對話（web-protocol 的 flash；只有桌面）：<paramref name="speaking"/> 是 false＝開始一直閃到使用者點它，
    /// true＝語音通知開始念它（還在閃的標頭另外閃三下，沒在閃的不動）
    /// </summary>
    private void FlashConversation(string conversationId, bool speaking)
    {
        if (disposed)
            return;
        var message = new JObject { ["type"] = "flash", ["conversationId"] = conversationId };
        if (speaking)
            message["speaking"] = true;
        PostToAll(message);
    }

    /// <summary>
    /// 這一輪正常結束時念的句子。背景工作還在跑時（代理開了背景指令或子代理，回一句「等待測試結果」就結束這一輪）不念：
    /// 還沒真的完成，背景工作結束後 CLI 自己接著開的那一輪（AgentIdleTurnStartedEvent）結束時才念完成
    /// </summary>
    private static VoiceLine CompletionSpeech(ConversationState c)
    {
        if (c.Session?.HasLiveBackgroundWork == true)
            return VoiceLine.Silent;
        var reply = LastReplyText(c);
        return VoiceNotifyRules.Line(full => VoiceNotifyRules.Completed(c.Meta.Title, full ? reply : null));
    }

    /// <summary>這一輪代理最後一則有文字的回覆（從尾端往前找，碰到使用者的訊息就停）；沒有回 null</summary>
    private static string? LastReplyText(ConversationState c)
    {
        for (var i = c.Items.Count - 1; i >= 0; i--)
        {
            var item = c.Items[i];
            if (item.Kind == AgentItemKind.User)
                return null;
            if (item.Kind == AgentItemKind.Assistant && !string.IsNullOrWhiteSpace(item.Text))
                return item.Text;
        }
        return null;
    }

    private void AddItem(ConversationState c, AgentChatItem item)
    {
        var items = EnsureItems(c);
        item.Seq = ++c.LastSeq;
        items.Add(item);
        AssignOrd(c, item);
        MarkItemDirty(c, item);
    }

    private void RemoveItem(ConversationState c, AgentChatItem item)
    {
        c.Items.Remove(item);
        c.ItemsUnsaved = true;
        if (dirtyItems.TryGetValue(c.Meta.Id, out var dirty))
            dirty.Remove(item.Id);
        foreach (var view in ViewsOf(c))
            Post(view, new JObject { ["type"] = "removeItem", ["conversationId"] = c.Meta.Id, ["id"] = item.Id });
        HookItemRemoved(c, item);
    }
}
