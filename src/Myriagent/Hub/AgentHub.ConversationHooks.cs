using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Hub;

/// <summary>掛鉤呼叫當下的對話（中樞的 ConversationState 不外露）</summary>
/// <param name="Meta">對話的中繼資料（中樞持有的那一份，只在 UI 執行緒讀）</param>
/// <param name="IsDraft">還沒送出第一則的草稿（只在記憶體，不存檔）</param>
/// <param name="IsReplica">其他電腦的對話在這台的副本（D41）</param>
/// <param name="Running">正在執行一輪</param>
internal readonly record struct ConversationHookInfo(AgentConversation Meta, bool IsDraft, bool IsReplica, bool Running);

/// <summary>
/// 對話變化的接收者（設計 14.11 D47；WP4.3 的發布以 <c>AttachConversationSink</c> 掛上，照 AttachSync 的模式）。每個方法對應中樞的一個掛鉤點，
/// 只在 UI 執行緒呼叫、要很快回來（文字串流時每個片段都會呼叫 <see cref="OnItemChanged"/>）；同一個變化可能呼叫不只一次，接收者要能合併。
/// 丟出的例外由中樞記錄後吞掉，不影響本機的流程。沒有掛上時中樞一切照舊
/// </summary>
internal interface IConversationChangeSink
{
    /// <summary>項目新增或改變（MarkItemDirty：沒有任何檢視開著這個對話時也呼叫）</summary>
    void OnItemChanged(ConversationHookInfo conversation, AgentChatItem item);

    /// <summary>項目被移除（RemoveItem）</summary>
    void OnItemRemoved(ConversationHookInfo conversation, AgentChatItem item);

    /// <summary>meta.json 存好了（SaveConversation；<paramref name="itemsSaved"/>＝items.json 也存好了）。存檔失敗不呼叫</summary>
    void OnConversationSaved(ConversationHookInfo conversation, bool itemsSaved);

    /// <summary>對話要被刪除（Delete：從中樞移除之前，資料夾還在）</summary>
    void OnConversationDeleting(ConversationHookInfo conversation);

    /// <summary>標頭可能改變（MarkHeaderDirty：狀態文字、模式、模型、資料夾、帳戶…）</summary>
    void OnHeaderChanged(ConversationHookInfo conversation);

    /// <summary>各代理執行中的對話數重算了（UpdateRunningCounts：某個對話開始或停止執行）</summary>
    void OnRunningCountsChanged();

    /// <summary>佇列、待送附件或待送的拒絕附言改變（PersistQueueIfChanged 發現內容和上次寫進 queue.json 的不同；寫入失敗時之後會再呼叫）</summary>
    void OnQueueChanged(ConversationHookInfo conversation);

    /// <summary>輸入區重送（PostComposer：佇列、附件、處理中的提示）</summary>
    void OnComposerChanged(ConversationHookInfo conversation);

    /// <summary>
    /// 一輪開始：送出（<paramref name="startedByCli"/> 是 false）或 CLI 自己開始的一輪（自動接續；對話已在執行中時——換版接續被吞掉的那一輪——
    /// 不再呼叫）。呼叫時 <see cref="ConversationHookInfo.Running"/> 已是 true
    /// </summary>
    void OnTurnStarted(ConversationHookInfo conversation, bool startedByCli);

    /// <summary>
    /// 一輪結束。每個 <see cref="OnTurnStarted"/> 之後一定有一個：CompleteTurn（換版重開期間舊程序被吞掉的中斷不算）、送出失敗（Failed）、
    /// 本機收尾（移除帳戶，Interrupted）、換版不接續或重開中按停止（Interrupted）、送出被換版退回（Interrupted，訊息回到佇列）。
    /// 呼叫時 <see cref="ConversationHookInfo.Running"/> 已是 false。<paramref name="outcome"/> 是中樞採用的結果（使用者拒絕核准而結束的算 Completed）；
    /// <paramref name="usage"/> 是那一輪的用量（CLI 有回報時；本機收尾的是 null）
    /// </summary>
    void OnTurnCompleted(ConversationHookInfo conversation, AgentTurnOutcome outcome, AgentUsage? usage);

    /// <summary>
    /// 要使用者注意（NotifyAttention）。<paramref name="kind"/> 是 <c>ConversationAttentionKinds</c> 的名稱（question、approval、plan、completed；
    /// 其他提醒是 null），<paramref name="itemId"/> 是那張卡；<paramref name="speech"/> 是語音通知念的句子（<see cref="VoiceLine.Silent"/>＝不念；
    /// 不管這台的語音開關，網頁版有自己的開關）
    /// </summary>
    void OnAttention(ConversationHookInfo conversation, string? kind, string? itemId, VoiceLine speech);
}

// 對話變化的掛鉤（設計 14.11 D47）：呼叫點在 AgentHub.cs、AgentHub.Render.cs、AgentHub.Events.cs、AgentHub.Restart.cs，一律經這裡的包裝。
// 擁有者：WP4.3（本檔）。包裝不用 lambda（沒有接收者時不配置閉包：文字串流的每個片段都會經過 HookItemChanged）
internal sealed partial class AgentHub
{
    private IConversationChangeSink? conversationSink;

    /// <summary>
    /// 掛上對話變化的接收者（WP4.3；一次只有一個，再掛一次取代；null＝拿掉）。換了就要重新準備 Ord、之前推來的觀看集合不再算數（AgentHub.Publish.cs）、
    /// 舊引擎的複本移除（AgentHub.Replica.cs），
    /// 被換掉的接收者是 <see cref="IDisposable"/> 就釋放（發布者停掉計時器、之後什麼都不做；計畫步 7，D4）。同一個再掛一次什麼都不做
    /// </summary>
    internal void AttachConversationSink(IConversationChangeSink? sink)
    {
        if (ReferenceEquals(conversationSink, sink))
            return;
        var old = conversationSink;
        conversationSink = sink;
        ResetOrdsReady();
        ResetRemoteWatched();
        OnReplicaSinkChanged();
        try
        {
            (old as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(IDisposable.Dispose), ex);
        }
    }

    /// <summary>目前掛著的接收者（UI 執行緒；測試與探針確認協調者掛上、拿掉）</summary>
    internal IConversationChangeSink? ConversationSink => conversationSink;

    private static ConversationHookInfo HookInfo(ConversationState c) => new(c.Meta, c.IsDraft, c.IsReplica, c.Running);

    private static void LogSinkError(string hook, Exception ex) => Log.Error($"對話變化的接收者在 {hook} 出錯", ex);

    private void HookItemChanged(ConversationState c, AgentChatItem item)
    {
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnItemChanged(HookInfo(c), item);
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnItemChanged), ex);
        }
    }

    private void HookItemRemoved(ConversationState c, AgentChatItem item)
    {
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnItemRemoved(HookInfo(c), item);
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnItemRemoved), ex);
        }
    }

    private void HookConversationSaved(ConversationState c, bool itemsSaved)
    {
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnConversationSaved(HookInfo(c), itemsSaved);
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnConversationSaved), ex);
        }
    }

    private void HookConversationDeleting(ConversationState c)
    {
        // 刪掉的是子對話：委派以未完成回報；是主對話：關掉它的委派管道（AgentHub.Delegation.cs）
        OnDelegationDeleting(c);
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnConversationDeleting(HookInfo(c));
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnConversationDeleting), ex);
        }
    }

    private void HookHeaderChanged(ConversationState c)
    {
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnHeaderChanged(HookInfo(c));
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnHeaderChanged), ex);
        }
    }

    private void HookRunningCountsChanged()
    {
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnRunningCountsChanged();
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnRunningCountsChanged), ex);
        }
    }

    private void HookQueueChanged(ConversationState c)
    {
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnQueueChanged(HookInfo(c));
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnQueueChanged), ex);
        }
    }

    private void HookComposerChanged(ConversationState c)
    {
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnComposerChanged(HookInfo(c));
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnComposerChanged), ex);
        }
    }

    private void HookTurnStarted(ConversationState c, bool startedByCli)
    {
        // 開始了一輪（使用者送出、接續、CLI 自己開的）：不再等額度重置（AgentHub.QuotaWait.cs）
        ForgetQuotaWait(c);
        // 背景活動日誌（AgentHub.Journal.cs）
        JournalTurnStarted(c, startedByCli);
        // running.json 跟著這一對掛鉤走（每個開始之後一定有一個結束；AgentHub.InterruptedTurns.cs，和接收者無關）
        OnRunMarkerTurnStarted(c);
        // 這一輪開出的程式要等它結束才算背景程式（AgentHub.BackgroundPrograms.cs）
        OnProgramsTurnStarted(c, startedByCli);
        // 執行中手動換帳戶只對選的那一輪有效：沒在它結束時換掉（例如收尾的路徑沒取用）就作廢，不留到之後的一輪
        c.PendingAccountSwitch = null;
        // API 過載後排定的自動重試：已經開始了別的一輪（使用者送出、CLI 自己開的、重試本身），不再送（AgentHub.OverloadRetry.cs）
        c.OverloadRetryTicket = 0;
        // CLI 換版停掉背景工作後還沒送出的確認：已經開始了別的一輪（使用者送出、CLI 自己開的、確認本身），不再送（AgentHub.Restart.cs）
        c.PendingBackgroundCheck = null;
        // 為了等提問的回答而停下的狀態：開始了下一輪（回答、略過或使用者另外送出）就結束（AgentHub.Events.cs 的 PauseForAsyncQuestion）
        c.PausingForQuestion = false;
        c.PausedQuestion = null;
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnTurnStarted(HookInfo(c), startedByCli);
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnTurnStarted), ex);
        }
    }

    private void HookTurnCompleted(ConversationState c, AgentTurnOutcome outcome, AgentUsage? usage)
    {
        // 插進這一輪的那則已經讀走或作廢：不能再取消（AgentHub.SteerCancel.cs）
        ForgetSteer(c);
        // 背景活動日誌：結果與還在跑的背景工作、背景程式（AgentHub.Journal.cs）
        JournalTurnCompleted(c, outcome);
        // 沒經過 CompleteTurn 的結束（停止、失敗、換版收尾）：這一輪即時記到的 token 當成上一輪（TurnTokenRules.Final），
        // 並清掉，下一輪從 0 累加（CompleteTurn 在這之前已經收過，這裡是 null）
        if (c.TurnTokens != null)
        {
            if (TurnTokenRules.Final(null, c.TurnTokens) is { } counted)
                c.LastUsage = counted;
            CloseTurnTokens(c);
        }
        // 工作資料夾的 git 狀態跟著重讀（AgentHub.Git.cs；和接收者無關）
        OnGitTurnCompleted(c);
        // 執行列重新偵測專案（代理可能剛建立了專案；AgentHub.Launch.cs）
        OnLaunchTurnCompleted(c);
        // 背景工作還在跑的留著 running.json（改寫成等背景工作的快照），其他的刪掉
        OnRunMarkerTurnCompleted(c, outcome);
        // 記下這一輪結束的時間、背景程式立刻重掃（代理可能剛開了程式；AgentHub.BackgroundPrograms.cs）
        OnProgramsTurnCompleted(c);
        // 這一輪的指令碰到 Windows 排程：立刻重讀，變更記成這個對話做的（AgentHub.Schedules.cs）
        OnSchedulesTurnCompleted(c);
        // 定時訊息送出的那一輪：記下結果（AgentHub.TimedMessages.cs）
        OnTimedTurnCompleted(c, outcome);
        // 委派的子對話這一輪結束：把結果交回主 AI；主對話結束：停掉還沒完成的子工作（AgentHub.Delegation.cs）
        OnDelegationTurnCompleted(c, outcome);
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnTurnCompleted(HookInfo(c), outcome, usage);
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnTurnCompleted), ex);
        }
    }

    private void HookAttention(ConversationState c, string? kind, string? itemId, VoiceLine speech)
    {
        if (conversationSink is not { } sink)
            return;
        try
        {
            sink.OnAttention(HookInfo(c), kind, itemId, speech);
        }
        catch (Exception ex)
        {
            LogSinkError(nameof(sink.OnAttention), ex);
        }
    }
}
