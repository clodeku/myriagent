using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// CLI 熱更新的重開協調（設計 7.4、7.7、7.8 的中樞部分；契約見 docs/plans/2026-10-10-hot-restart-contract.md）。
/// CLI 換版（CliManager.VersionChanged）時，還在跑舊身分的對話依政策表（CliRestartRules.Decide）處理：閒置的立即關程序（不送接續）、
/// 執行中的在安全點以新版重開並自動接續（IAgentSession.RestartAsync）、工具執行中或等你回應的等到下一個安全點；
/// app 要換 npm 版的檔案時經更新屏障（ICliUpdateParticipant）排空，更新完成後重開停下的對話。
/// 所有狀態只在 UI 執行緒改：CliManager 的事件與屏障呼叫都在背景執行緒，一律 BeginInvoke 回來
/// </summary>
internal sealed partial class AgentHub
{
    /// <summary>對話的換版狀態（ConversationState.Restart）</summary>
    private sealed class RestartTracking
    {
        /// <summary>事件流上的目前程序實例（AgentProcessStartedEvent）：新卡片寫這個實例；不讀 session.ProcessInstance（事件非同步送達，讀屬性可能拿到下一個程序的）</summary>
        public string? Instance;

        /// <summary>目前程序啟動用的執行檔身分；null＝不明（身分不明不構成重開的理由）</summary>
        public CliBinaryIdentity? InstanceIdentity;

        /// <summary>目前程序回報的版本（AgentRunningVersionEvent）</summary>
        public string? RunningVersion;

        /// <summary>最近一次的安全點旗標（新程序開始時清掉）：只用來分辨不在安全點的原因，要不要重開以會話的屬性為準</summary>
        public AgentSafePointChangedEvent? SafePoint;

        /// <summary>目前的程序已結束或被中樞關掉、還沒有新程序（測試替身的 HasLiveProcess 不會跟著變，以這個為準）</summary>
        public bool ProcessGone;

        /// <summary>排定的換版（沒有要處理的為 null）</summary>
        public RestartPlan? Plan;

        /// <summary>
        /// 換版處理完後留在標頭的結果（continued／notContinued／failed）；下一次送出時清掉。可以按「繼續」的沒有接續另外存成
        /// continue.json（app 再重新啟動時還原，AgentHub.ContinueOffers.cs）：每次指派都記成動過，MarkHeaderDirty 與 app 結束時對到檔案
        /// </summary>
        public RestartResultView? Result
        {
            get => result;
            set
            {
                result = value;
                ResultTouched = true;
            }
        }

        private RestartResultView? result;

        /// <summary>Result 指派過、還沒對到 continue.json</summary>
        public bool ResultTouched;

        /// <summary>CLI 更新期間或這個對話換版進行中被延後、之後要先送的訊息（在佇列裡）</summary>
        public QueuedMessage? DeferredSend;

        /// <summary>SendAsync 因為會話正在重開而連續被拒的次數（AgentRestartInProgressException；送出成功歸零）</summary>
        public int RejectedSends;

        public void ResetProcess()
        {
            Instance = null;
            InstanceIdentity = null;
            RunningVersion = null;
            SafePoint = null;
            ProcessGone = false;
        }
    }

    /// <summary>一個對話排定的換版（一般換版或更新屏障的排空）</summary>
    private sealed class RestartPlan
    {
        public RestartPlan(int wave, DateTime since)
        {
            Wave = wave;
            Since = since;
        }

        /// <summary>屬於哪一波換版（系統匣彙總、CLI 卡的 restartDone）</summary>
        public int Wave;

        /// <summary>開始等待的時間（背景工作最多等 CliRestartRules.BackgroundWaitLimit）</summary>
        public DateTime Since;

        /// <summary>更新屏障的排空（npm 換檔前）：到安全點停下、不重開，等 UpdateFinished</summary>
        public bool Drain;

        /// <summary>app 自我更新的排空（設計 9.2）：等你回應視為安全點、停下後由新版 app 接續（不在這個程序重開）</summary>
        public bool AppUpdate;

        /// <summary>開始 app 更新排空時的 app 版本：放棄時就地接續的訊息說明「為了套用自身的更新而停下」（ContinuationContext.AppFrom/AppTo）</summary>
        public string? AppVersion;

        /// <summary>
        /// app 更新排空關掉或停下這個程序時它開著 Claude Remote Control：新版啟動後（放棄這次時就地）沒有自動接續的就重開程序、
        /// 只開 Remote Control 接回遠端工作階段（resume.json 的 RemoteControl；設計 9.2、9.5），手機上不必等桌面再送出一則訊息
        /// </summary>
        public bool RemoteControl;

        /// <summary>使用者按了「立即套用」或屏障的「現在中斷」：不等安全點</summary>
        public bool ApplyNow;

        /// <summary>會話回過 NotSupported（或新設定建不起來）：退回「這一輪結束後關程序」</summary>
        public bool CannotRestart;

        /// <summary>會話剛回 Deferred（不在安全點）：等下一個安全點事件、工具結束、使用者回應或維護計時器再試，不連續重試</summary>
        public bool Deferred;

        /// <summary>要換到的身分（一般換版是 CLI 目前的身分；屏障是 UpdateFinished 給的）</summary>
        public CliBinaryIdentity? Target;

        /// <summary>這個對話的程序原本跑的版本（不明為 null）</summary>
        public string? FromVersion;

        /// <summary>目前在等什麼（標頭的 state）；null＝不顯示（例如閒置的正在關）</summary>
        public CliRestartState? Waiting;

        /// <summary>在等的工具或卡片（一行摘要）</summary>
        public string? WaitingSubject;

        /// <summary>看過這個對話有存活的背景工作：結束後 CLI 會自己開一輪告訴模型（E-11），要等它安靜下來再關</summary>
        public bool SawBackground;

        /// <summary>CloseAsync 或 RestartAsync 進行中</summary>
        public bool InFlight;

        /// <summary>進行中的是 RestartAsync（不是 CloseAsync）</summary>
        public bool Restarting;

        /// <summary>進行中的 RestartAsync 是屏障的停下（Settings=null）</summary>
        public bool StopOnly;

        /// <summary>
        /// 會話開始了接續的一輪（AgentTurnStartedByCliEvent）：c.Running 屬於它。Claude 新程序一啟動就發（CLI 還不一定會接續），
        /// 所以「已用新版自動接續」要等 ContinuationConfirmed
        /// </summary>
        public bool ContinuationStarted;

        /// <summary>
        /// 確定已用新版接續：RestartAsync 回 RestartedAndContinued（Claude 會話以 command_lifecycle started、這一輪的活動或
        /// resume_reason=myriagent_cli_update 的 result 判定 CLI 已接續；Codex 是接續訊息的 turn/start 已被接受）。標頭這時才顯示 continued、加 Notice
        /// </summary>
        public bool ContinuationConfirmed;

        public bool ContinuedNoticeAdded;

        /// <summary>同時啟動中的程序已達上限，等空出來（CliRestartRules.MaxConcurrentStarts）</summary>
        public bool WantsSlot;

        /// <summary>屏障停下了執行中的這一輪（Stopped），等 UpdateFinished 後重開接續</summary>
        public bool Held;

        /// <summary>屏障：已關掉或停下（或本來就沒有程序）</summary>
        public bool Drained;

        /// <summary>UpdateFinished 後要重開接續停下的那一輪</summary>
        public bool ResumeHeld;

        /// <summary>
        /// 屏障的停下（RestartAsync Settings=null）還在進行時 UpdateFinished 就先到了（例如停下途中按了「取消更新」）：
        /// 停下有了結果時照這次更新的結果接續或收尾——屏障已經結束，之後不會再有人來重開這一輪
        /// </summary>
        public CliUpdateCompletion? FinishedUpdate;

        /// <summary>這一輪被打斷（程序被停下）的時間：超過 1 小時不自動接續</summary>
        public DateTime? InterruptedAt;

        /// <summary>重開時被打斷的工具、還在等使用者回應的請求、有沒有背景工作（接續訊息用；屏障停下時記下，更新後接續時用）</summary>
        public List<AgentInterruptedTool> InterruptedTools = new();

        public List<string> PendingRequests = new();

        public bool HadBackground;

        /// <summary>
        /// CLI 換版停下了只在等背景工作的對話（閒置、背景工作還在跑；等滿上限或使用者按了立即套用）：重開或更新結束後隱藏送出確認訊息，
        /// 請代理確認背景工作做到哪裡、需要的話重新執行（app 更新另由 running.json 接續，AgentHub.InterruptedTurns.cs）
        /// </summary>
        public bool BackgroundCheck;

        /// <summary>BackgroundCheck 時停掉的背景工作（「執行指令：npm test」）</summary>
        public List<string> BackgroundTasks = new();

        public CliRiskMatch? Risk;

        public CliNoContinueReason? NoContinueReason;

        public ContinuationContext? Continuation;

        /// <summary>進行中（或最後一次）的重開要求</summary>
        public AgentRestartRequest? Request;
    }

    /// <summary>換版處理完的結果（標頭）；Continuation＝按「繼續」時接續訊息的內容</summary>
    private sealed record RestartResultView(CliRestartState State, string? From, string? To, string Detail, bool CanContinue, ContinuationContext? Continuation);

    /// <summary>標頭 restart 欄位的內容；Since＝開始等待的時間（排定換版的時候，只有還在等的狀態才有）</summary>
    private sealed record HeaderRestartInfo(CliRestartState State, string? From, string? To, string Detail, bool CanApplyNow, bool CanContinue, DateTime? Since = null);

    /// <summary>更新屏障（PrepareForUpdateAsync 到 UpdateFinished 之間）：這段期間不為這種 CLI 啟動新程序，送出排進佇列</summary>
    private sealed class UpdateBarrier
    {
        public UpdateBarrier(AgentProvider provider, CliUpdateUrgency urgency, DateTime startedAt, int wave, TaskCompletionSource<CliUpdatePreparation> done)
        {
            Provider = provider;
            Urgency = urgency;
            StartedAt = startedAt;
            Wave = wave;
            Done = done;
        }

        public AgentProvider Provider { get; }

        public CliUpdateUrgency Urgency;

        public DateTime StartedAt { get; }

        public int Wave { get; }

        public TaskCompletionSource<CliUpdatePreparation> Done { get; }

        public CancellationTokenRegistration Registration;

        public int ClosedIdle;

        /// <summary>排空超過 DrainGrace：CLI 卡問使用者要等還是現在中斷</summary>
        public bool Overdue;

        /// <summary>還在排空（PrepareForUpdateAsync 還沒回覆）</summary>
        public bool Draining => !Done.Task.IsCompleted;

        /// <summary>app 自我更新的屏障（AgentHub.AppUpdate.cs）：CLI 卡不顯示、CLI 的 PrepareForUpdateAsync 遇到它回未就緒</summary>
        public bool AppUpdate { get; init; }
    }

    private enum WaveOutcome
    {
        Continued,
        Restarted,
        NotContinued,
        Failed,
    }

    /// <summary>一波換版（一次 VersionChanged 或一次更新屏障）：處理完送一則系統匣彙總</summary>
    private sealed class RestartWave
    {
        public RestartWave(int id, string? toVersion)
        {
            Id = id;
            ToVersion = toVersion;
        }

        public int Id { get; }

        public string? ToVersion;

        public int Continued;

        public int Restarted;

        public int NotContinued;

        public int Failed;

        public bool Summarized;

        /// <summary>屏障沒有換到新版（失敗、沒換版、取消）：不送彙總</summary>
        public bool Silent;

        /// <summary>app 更新排空的一波：CLI 卡的 restartDone 不顯示（關掉、停下與放棄後的就地接續都不是 CLI 換版）</summary>
        public bool AppUpdate;

        public int Total => Continued + Restarted + NotContinued + Failed;
    }

    private readonly Dictionary<AgentProvider, UpdateBarrier> barriers = new();
    private readonly Dictionary<AgentProvider, RestartWave> waves = new();
    private readonly Dictionary<AgentProvider, int> restartsInFlight = new();
    private int lastWaveId;

    /// <summary>重開協調的時鐘（背景工作的提示、排空寬限、中斷多久）；測試可換</summary>
    internal Func<DateTime> RestartClock { get; set; } = () => DateTime.Now;

    /// <summary>背景工作結束後，對話要安靜多久才關程序（CLI 會自己開一輪告訴模型背景工作結束了）；測試可縮短</summary>
    internal TimeSpan BackgroundSettle { get; set; } = TimeSpan.FromSeconds(10);

    private IEnumerable<ConversationState> ConversationsOf(AgentProvider provider)
        => conversations.Values.Where(c => c.Meta.Provider == provider && !c.IsDraft);

    private bool IsLive(ConversationState c)
        => !c.IsDraft && conversations.TryGetValue(c.Meta.Id, out var live) && ReferenceEquals(live, c);

    private static bool LiveProcess(ConversationState c) => c.Session is { HasLiveProcess: true } && !c.Restart.ProcessGone;

    /// <summary>程序還活著、但跑的不是目前安裝的身分（只用 CliBinaryIdentity.SameOrUnknown：身分不明不算）</summary>
    private bool IsOnOldVersion(ConversationState c)
        => !c.IsDraft && LiveProcess(c) && cli.GetIdentity(c.Meta.Provider) is { } current
           && !CliBinaryIdentity.SameOrUnknown(c.Restart.InstanceIdentity, current);

    private static string? CurrentVersionOf(ConversationState c) => c.Restart.RunningVersion ?? c.Restart.InstanceIdentity?.Version;

    private int RestartsInFlight(AgentProvider provider) => restartsInFlight.TryGetValue(provider, out var count) ? count : 0;

    private void MarkCliDirty()
    {
        cliDirty = true;
        ScheduleFlush();
    }

    // ───────────────────────────── CLI 的變化 ─────────────────────────────

    /// <summary>重開協調最後處理過的 CLI 身分（OnCliVersionChanged）：CliManager 先觸發 Changed 再觸發 VersionChanged，身分不同＝換版通知還在路上</summary>
    private readonly Dictionary<AgentProvider, CliBinaryIdentity?> handledIdentity = new();

    /// <summary>CLI 狀態改變（背景執行緒觸發後 BeginInvoke 回來）：更新 CLI 卡；安裝不再變動（npm 換完檔）就送出延後的訊息</summary>
    private void OnCliStateChanged()
    {
        cliDirty = true;
        ScheduleFlush();
        if (disposed)
            return;
        foreach (var provider in AgentProviders.All)
        {
            // 換版通知還沒處理：等它先關掉跑舊版的程序，延後的送出再以新版啟動
            handledIdentity.TryGetValue(provider, out var handled);
            if (Equals(cli.GetIdentity(provider), handled))
                ResumeDeferredSends(provider);
        }
    }

    /// <summary>CLI 安裝的變化（CliManager.VersionChanged，BeginInvoke 回來）</summary>
    private void OnCliVersionChanged(CliVersionChange change)
    {
        if (disposed)
            return;
        MarkCliDirty();
        var provider = change.Provider;
        handledIdentity[provider] = cli.GetIdentity(provider);
        // 安裝正在變動：新的送出排進佇列（IsCliUpdateBlocking），等穩定
        if (!change.Stable)
            return;
        if (change.InstallBroken)
        {
            // 安裝損壞：不重開任何對話（CLI 卡顯示原因與「重新安裝」）；延後的送出照常送（失敗會在對話裡說明）
            Log.Warn($"{provider} CLI 安裝損壞，不重開對話：{change.Detail}");
            foreach (var c in ConversationsOf(provider).Where(c => c.Restart.Plan != null))
                MarkHeaderDirty(c);
            ResumeDeferredSends(provider);
            return;
        }
        if (change.New == null)
        {
            // CLI 不在了：沒有可以換的版本，排定的換版取消（之後的送出照現行回報「未安裝」）
            foreach (var c in ConversationsOf(provider).Where(c => c.Restart.Plan is { Drain: false, InFlight: false, Held: false, ResumeHeld: false }).ToList())
                DropPlan(c);
            CheckWave(provider);
            ResumeDeferredSends(provider);
            return;
        }
        if (change.IsVersionSwitch)
            Log.Info($"{provider} CLI 換版：{change.FromVersion ?? "不明"} → {change.ToVersion}（{(change.Origin == CliChangeOrigin.App ? "app" : "外部")}）");
        // 先處理還在跑舊版的程序（閒置的關掉），延後的送出再以新版啟動
        EnrollOldVersion(provider, change.New, change);
        ResumeDeferredSends(provider);
    }

    /// <summary>還在跑舊身分的對話排定換版並依政策處理（執行中的先）；屏障期間只記下新身分，等 UpdateFinished</summary>
    private void EnrollOldVersion(AgentProvider provider, CliBinaryIdentity target, CliVersionChange? change)
    {
        if (barriers.ContainsKey(provider))
            return;
        var affected = ConversationsOf(provider)
            .Where(c => c.Session != null && (IsOnOldVersion(c) || c.Restart.Plan is { Drain: false }))
            .OrderByDescending(c => c.Running)
            .ToList();
        if (affected.Count == 0 && change?.IsVersionSwitch != true)
        {
            CheckWave(provider);
            return;
        }
        var wave = change != null ? StartWave(provider, target.Version) : CurrentWave(provider, target.Version);
        var now = RestartClock();
        foreach (var c in affected)
        {
            var plan = c.Restart.Plan ??= new RestartPlan(wave.Id, now);
            plan.Wave = wave.Id;
            plan.Target = target;
            plan.FromVersion ??= CurrentVersionOf(c) ?? change?.FromVersion;
            plan.Deferred = false;
            EvaluateRestart(c);
        }
        CheckWave(provider);
        MarkCliDirty();
    }

    /// <summary>重開政策改變：還在跑舊版的對話依新政策重新處理（例如由「下次送出」改成「安全點」）</summary>
    private void OnRestartPolicyChanged()
    {
        MarkCliDirty();
        foreach (var provider in AgentProviders.All)
        {
            foreach (var c in ConversationsOf(provider))
                MarkHeaderDirty(c);
            if (cli.GetIdentity(provider) is { } current)
                EnrollOldVersion(provider, current, change: null);
        }
    }

    /// <summary>設定頁「CLI 更新後」：只接受三個值；回 false＝值不對，不存檔</summary>
    private bool ChangeRestartPolicy(string? value)
    {
        if (CliRestartRules.ParsePolicy(value) is not { } chosen)
            return false;
        bool changed = settings.CliRestartPolicy != chosen;
        settings.CliRestartPolicy = chosen;
        if (changed)
            OnRestartPolicyChanged();
        return true;
    }

    // ───────────────────────────── 政策 ─────────────────────────────

    /// <summary>對話在政策表的哪一列：要不要重開以會話的屬性為準（快照），不在安全點的原因參考最近的安全點事件與畫面卡片</summary>
    private CliRestartSituation SituationOf(ConversationState c)
    {
        var session = c.Session;
        if (session == null || !LiveProcess(c))
            return CliRestartSituation.NoProcess;
        var flags = c.Restart.SafePoint;
        bool background = session.HasLiveBackgroundWork;
        // 還沒建立 session（Claude 的 system/init、Codex 的 thread id 還沒到）的一輪不能以 --resume／thread/resume 接續
        bool atSafePoint = session.IsAtSafePoint && (c.Meta.SessionStarted || !c.Running);
        bool waiting = (flags?.WaitingForUser ?? false) || c.WaitingCount > 0;
        bool tool = (flags?.ToolRunning ?? false) || ForegroundRunningTools(c).Any();
        return CliRestartRules.Classify(true, c.Running, atSafePoint, tool, waiting, background);
    }

    /// <summary>
    /// 執行中的工具卡，不含背景工作的（會話回報的 AgentBackgroundWorkEvent.ToolIds：Codex 這一輪讓出給模型、留在終端機裡繼續跑的指令，
    /// 卡片在跑完前都是執行中，模型卻沒有在等它——當成在等的工具會一直等到這一輪結束）
    /// </summary>
    private static IEnumerable<AgentChatItem> ForegroundRunningTools(ConversationState c)
    {
        var background = BackgroundOf(c)?.ToolIds is { Count: > 0 } ids
            ? ids.Select(id => c.ToolItems.TryGetValue(id, out var card) ? card : null).OfType<AgentChatItem>().ToHashSet()
            : null;
        return c.Items.Where(i => i.Kind == AgentItemKind.Tool && i.Status == AgentItemStatus.Running && background?.Contains(i) != true);
    }

    /// <summary>
    /// 依政策表處理這個對話排定的換版（換版、安全點事件、工具結束、使用者回應、這一輪結束、計時器都會呼叫；沒有排定的就不做事）
    /// </summary>
    private void EvaluateRestart(ConversationState c)
    {
        var r = c.Restart;
        var plan = r.Plan;
        if (plan == null || disposed || plan.InFlight || plan.Held || plan.Drained)
            return;
        if (!IsLive(c) || c.Session == null)
        {
            DropPlan(c);
            return;
        }

        var provider = c.Meta.Provider;
        if (plan.ResumeHeld)
        {
            // 屏障停下的那一輪：更新完成後以新身分重開並接續（沒有程序，不看安全點；會話剛回 Deferred 就等計時器再試）
            if (!plan.Deferred)
                StartRestartCall(c, plan, stopOnly: false);
            return;
        }

        if (!plan.Drain)
        {
            // 屏障期間只記下，停下的對話等 UpdateFinished 才重開
            if (barriers.ContainsKey(provider))
                return;
            var state = cli.Get(provider);
            if (state.Identity is not { } current)
            {
                DropPlan(c);
                return;
            }
            // 安裝損壞或正在變動：先不重開，等下一次穩定
            if (state.InstallBroken || state.InstallChanging)
                return;
            plan.Target = current;
            if (!LiveProcess(c) || CliBinaryIdentity.SameOrUnknown(r.InstanceIdentity, current))
            {
                DropPlan(c);
                return;
            }
        }

        var situation = SituationOf(c);
        if (situation is CliRestartSituation.IdleWithBackground or CliRestartSituation.BackgroundRunning)
        {
            plan.SawBackground = true;
            // 背景工作不論跑多久都不能一直擋住換版（2026-10-24 使用者要求）：從排定換版起最多等 BackgroundWaitLimit，超過就照立即套用停下，
            // 重開後請代理確認背景工作（CLI 換版：BackgroundCheck；app 更新：running.json）。維護計時器每 30 秒經 RecheckRestart 走到這裡，
            // 前景的工具一結束（只剩背景工作）也馬上走到這裡
            if (!plan.ApplyNow && CliRestartRules.BackgroundWaitExpired(plan.Since, RestartClock()))
            {
                Log.Info($"對話 {c.Meta.Id} 等背景工作已滿 {CliRestartRules.BackgroundWaitLimit.TotalMinutes:0} 分鐘，照立即套用停下");
                JournalRestartExpired(c, plan);
                plan.ApplyNow = true;
                plan.Deferred = false;
            }
        }
        var action = CliRestartRules.Decide(settings.CliRestartPolicy, situation, plan.Drain, plan.ApplyNow, canRestart: !plan.CannotRestart,
            remoteControl: c.Session.HasRemoteControl, appUpdate: plan.AppUpdate);

        // 背景工作剛結束：CLI 會自己開一輪告訴模型（E-11），等對話安靜下來再關
        if (action == CliRestartAction.Close && plan.SawBackground && !plan.ApplyNow)
        {
            var quiet = DateTime.Now - c.LastActivity;
            if (quiet < BackgroundSettle)
            {
                SetWaiting(c, plan, CliRestartState.WaitingBackground, null);
                ScheduleRestartCheck(c, BackgroundSettle - quiet);
                return;
            }
        }

        switch (action)
        {
            case CliRestartAction.None:
                if (plan.Drain)
                {
                    plan.Drained = true;
                    plan.Waiting = null;
                    MarkHeaderDirty(c);
                    CheckBarrier(provider);
                }
                else
                {
                    DropPlan(c);
                }
                break;
            case CliRestartAction.NextSend:
                // 不主動處理：標頭依「還在跑舊版」顯示 nextSend，下次送出時以新身分重開
                DropPlan(c);
                break;
            case CliRestartAction.Close:
                StartRestartClose(c, plan);
                break;
            case CliRestartAction.Restart:
            case CliRestartAction.Stop:
                if (plan.Deferred)
                {
                    // 會話剛說不在安全點：等事件再試，不連續重試
                    var reason = WaitingReason(c);
                    SetWaiting(c, plan, reason, WaitingSubject(c, reason == CliRestartState.WaitingUser ? CliRestartAction.WaitUser : CliRestartAction.WaitTool));
                    break;
                }
                StartRestartCall(c, plan, stopOnly: action == CliRestartAction.Stop);
                break;
            default:
                SetWaiting(c, plan, CliRestartRules.WaitingStateOf(action)!.Value, WaitingSubject(c, action));
                break;
        }
    }

    /// <summary>不在安全點時在等什麼（會話回 Deferred 時顯示用）</summary>
    private CliRestartState WaitingReason(ConversationState c) => SituationOf(c) switch
    {
        CliRestartSituation.WaitingForUser => CliRestartState.WaitingUser,
        CliRestartSituation.BackgroundRunning or CliRestartSituation.IdleWithBackground => CliRestartState.WaitingBackground,
        _ => CliRestartState.WaitingTool,
    };

    /// <summary>在等的工具或卡片的一行摘要（標頭與 CLI 卡的 detail）</summary>
    private static string? WaitingSubject(ConversationState c, CliRestartAction action)
    {
        switch (action)
        {
            case CliRestartAction.WaitTool:
            {
                var tool = ForegroundRunningTools(c).LastOrDefault();
                return tool == null ? null : CliRestartRules.DescribeOperation(tool.ToolName, tool.Title);
            }
            case CliRestartAction.WaitUser:
            {
                var card = c.Items.LastOrDefault(i => i.Status == AgentItemStatus.Waiting && !i.AnswerAsMessage
                                                      && i.Kind is AgentItemKind.Approval or AgentItemKind.Question or AgentItemKind.PlanApproval);
                return card == null ? null : DescribeRequest(card);
            }
            default:
                return null;
        }
    }

    /// <summary>等使用者回應的卡片的說明（接續訊息與標頭）：「執行指令：git push」「問題：要怎麼彙總？」「計畫確認」</summary>
    private static string DescribeRequest(AgentChatItem card) => card.Kind switch
    {
        AgentItemKind.Question => "問題：" + ClaudeStreamRules.FirstLine(card.Questions.FirstOrDefault()?.Text),
        AgentItemKind.PlanApproval => "計畫確認",
        _ => string.IsNullOrWhiteSpace(card.Title) ? card.ToolName ?? "核准" : $"{card.ToolName ?? "核准"}：{ClaudeStreamRules.FirstLine(card.Title)}",
    };

    private void SetWaiting(ConversationState c, RestartPlan plan, CliRestartState state, string? subject)
    {
        if (plan.Waiting == state && plan.WaitingSubject == subject)
            return;
        plan.Waiting = state;
        plan.WaitingSubject = subject;
        MarkHeaderDirty(c);
        MarkCliDirty();
        JournalRestartWaiting(c, plan);
    }

    /// <summary>立即重開回 Deferred 後多久主動再試一次（不必等安全點事件）；測試可縮短</summary>
    internal TimeSpan DeferredRetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>立即重開回 Deferred：DeferredRetryDelay 後這個換版還在、還是 Deferred 就重新檢查（安全點事件、工具結束、維護計時器也會觸發，先到先做）</summary>
    private void ScheduleDeferredRetry(ConversationState c, RestartPlan plan)
    {
        var id = c.Meta.Id;
        _ = Task.Delay(DeferredRetryDelay).ContinueWith(_ => host.BeginInvoke(() =>
        {
            if (!disposed && conversations.TryGetValue(id, out var live) && ReferenceEquals(live, c)
                && ReferenceEquals(c.Restart.Plan, plan) && plan is { Deferred: true, InFlight: false })
                RecheckRestart(c);
        }), TaskScheduler.Default);
    }

    private void ScheduleRestartCheck(ConversationState c, TimeSpan delay)
    {
        var id = c.Meta.Id;
        _ = Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay).ContinueWith(_ => host.BeginInvoke(() =>
        {
            if (!disposed && conversations.TryGetValue(id, out var live) && ReferenceEquals(live, c))
                EvaluateRestart(c);
        }), TaskScheduler.Default);
    }

    private void DropPlan(ConversationState c)
    {
        if (c.Restart.Plan == null)
            return;
        c.Restart.Plan = null;
        MarkHeaderDirty(c);
        MarkCliDirty();
    }

    /// <summary>處理完一個對話：屏障的留著（標成已排空，等 UpdateFinished），一般換版的移除</summary>
    private void FinishPlan(ConversationState c, RestartPlan plan)
    {
        if (plan.Drain)
        {
            plan.Drained = true;
            plan.Waiting = null;
        }
        else if (ReferenceEquals(c.Restart.Plan, plan))
        {
            c.Restart.Plan = null;
        }
        MarkHeaderDirty(c);
        MarkCliDirty();
    }

    /// <summary>重開或關閉做完一步：屏障是否已排空、空出來的啟動名額、這一波是否都處理完</summary>
    private void AfterRestartStep(AgentProvider provider)
    {
        CheckBarrier(provider);
        PumpRestarts(provider);
        CheckWave(provider);
        MarkCliDirty();
    }

    // ───────────────────────────── 關閉與重開 ─────────────────────────────

    /// <summary>閒置的對話：關掉舊程序（保留會話物件，「本對話都允許」的快取還在），下次送出自然以新版續接，不送接續訊息</summary>
    private void StartRestartClose(ConversationState c, RestartPlan plan)
    {
        var session = c.Session!;
        // app 更新的排空照常關開著 Remote Control 的閒置程序（app 要結束，程序留不住）：記下來，新版重開程序接回
        if (plan.AppUpdate && session.HasRemoteControl)
            plan.RemoteControl = true;
        plan.InFlight = true;
        plan.Restarting = false;
        plan.Waiting = null;
        MarkHeaderDirty(c);
        MarkCliDirty();
        _ = CloseForRestartAsync(c, plan, session, c.Restart.Instance);
    }

    private async Task CloseForRestartAsync(ConversationState c, RestartPlan plan, IAgentSession session, string? instance)
    {
        try
        {
            await session.CloseAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"換版時關閉閒置的對話程序失敗：{ex.Message}");
        }
        plan.InFlight = false;
        if (disposed)
            return;
        var provider = c.Meta.Provider;
        if (IsLive(c) && ReferenceEquals(c.Session, session))
        {
            MarkProcessGone(c, instance);
            RecordOutcome(provider, plan, WaveOutcome.Restarted);
            if (plan.Drain && barriers.TryGetValue(provider, out var barrier) && barrier.Wave == plan.Wave)
                barrier.ClosedIdle++;
            FinishPlan(c, plan);
        }
        AfterRestartStep(provider);
        // 關閉期間的送出（DeferSendDuringRestart）：現在以新版啟動
        ResumeSendAfterRestart(c);
    }

    /// <summary>目前的程序已不在（中樞關掉、停下、或它自己結束）；instance 不是目前的實例（之後又開了新程序）就不動</summary>
    private static void MarkProcessGone(ConversationState c, string? instance)
    {
        var r = c.Restart;
        if (instance == null || r.Instance == null || r.Instance == instance)
            r.ProcessGone = true;
    }

    /// <summary>以 RestartAsync 重開（或屏障的停下）：同時啟動中的程序有上限，執行中的先</summary>
    private void StartRestartCall(ConversationState c, RestartPlan plan, bool stopOnly)
    {
        var provider = c.Meta.Provider;
        if (RestartsInFlight(provider) >= CliRestartRules.MaxConcurrentStarts(provider))
        {
            plan.WantsSlot = true;
            SetWaiting(c, plan, stopOnly ? StoppedState(plan) : CliRestartState.Restarting, null);
            return;
        }
        plan.WantsSlot = false;
        bool wasRunning = c.Running;
        if (wasRunning && !plan.ResumeHeld)
            plan.InterruptedAt = RestartClock();

        AgentRestartRequest request;
        try
        {
            request = BuildRestartRequest(c, plan, stopOnly);
        }
        catch (Exception ex)
        {
            // 帳戶被移除、資料夾建不起來：沒辦法以新設定重開。停下的那一輪以提示收尾，執行中的照舊跑完（結束時再關程序）
            Log.Warn($"換版時建立對話設定失敗：{ex.Message}");
            if (plan.ResumeHeld)
            {
                plan.ResumeHeld = false;
                WrapUpInterruptedTurn(c, CliRestartRules.FailedNotice(ex.Message, c.Queue.Count > 0), cliNotice: true,
                    FailedView(c, plan, plan.Target?.Version, ex.Message));
                RecordOutcome(provider, plan, WaveOutcome.Failed);
                FinishPlan(c, plan);
                AfterRestartStep(provider);
                return;
            }
            plan.CannotRestart = true;
            EvaluateRestart(c);
            return;
        }

        restartsInFlight[provider] = RestartsInFlight(provider) + 1;
        // app 更新停下開著 Remote Control 的程序：沒有自動接續的（閒置但有背景工作、不接續的這一輪）由新版重開程序接回
        if (plan.AppUpdate && c.Session!.HasRemoteControl)
            plan.RemoteControl = true;
        plan.InFlight = true;
        plan.Restarting = true;
        plan.StopOnly = stopOnly;
        plan.ContinuationStarted = false;
        plan.ContinuationConfirmed = false;
        if (!stopOnly)
            plan.ContinuedNoticeAdded = false;
        plan.Request = request;
        plan.Waiting = stopOnly ? StoppedState(plan) : CliRestartState.Restarting;
        if (c.Running)
            c.StatusText = stopOnly ? CliRestartRules.DescribeState(StoppedState(plan), provider, null) : CliRestartRules.RestartingStatus;
        MarkHeaderDirty(c);
        MarkCliDirty();
        Log.Info($"以新版 CLI {(stopOnly ? "停下" : "重開")}對話 {c.Meta.Id}（{request.Mode}，{request.FromVersion ?? "不明"} → {request.ToVersion ?? "不明"}，自動接續 {request.AutoContinue}）");
        JournalRestartCall(c, plan, request, stopOnly);
        _ = RunRestartAsync(c, plan, c.Session!, request, wasRunning);
    }

    /// <summary>組重開要求：被打斷的工具與請求（立即套用時）、不自動接續的情況、接續訊息、新程序的設定（停下時為 null）</summary>
    private AgentRestartRequest BuildRestartRequest(ConversationState c, RestartPlan plan, bool stopOnly)
    {
        var provider = c.Meta.Provider;
        var now = RestartClock();
        var target = plan.Target ?? cli.GetIdentity(provider);
        bool resumingHeld = plan.ResumeHeld;
        if (!resumingHeld)
        {
            // 安全點重開沒有被打斷的工具；立即套用與 app 更新記下正在執行的工具與還沒回應的請求（屏障停下的，接續時再用）
            bool record = plan.ApplyNow || plan.AppUpdate;
            plan.InterruptedTools = record ? RunningTools(c) : new List<AgentInterruptedTool>();
            plan.PendingRequests = record ? PendingRequests(c) : new List<string>();
            plan.HadBackground = c.Session?.HasLiveBackgroundWork ?? false;
            // 立即套用也會停掉背景工作（Claude 的 run_in_background 一啟動工具卡就是完成，看卡片看不到）：背景跑的部署、發布被打斷，同前景的一樣不自動接續
            plan.Risk = record ? FindRunningToolRisk(c) ?? (plan.HadBackground ? FindBackgroundToolRisk(c) : null) : null;
            // CLI 換版停下只在等背景工作的對話：這一輪早就結束了、不會接續，重開或更新結束後另外請代理確認背景工作（QueueBackgroundCheck）
            plan.BackgroundCheck = !plan.AppUpdate && !c.Running && plan.HadBackground;
            plan.BackgroundTasks = plan.BackgroundCheck
                ? BackgroundWorkRules.Labels(c.Session?.LiveBackgroundTools ?? Array.Empty<AgentBackgroundTool>())
                : new List<string>();
        }
        plan.NoContinueReason = CliRestartRules.WhyNotContinue(c.StopRequested, c.Running, plan.InterruptedAt, now,
            previousTurnFailed: false, continuationFailed: false, riskyTool: plan.Risk != null);
        plan.Continuation = new ContinuationContext(provider, plan.FromVersion, target?.Version, plan.InterruptedTools, plan.PendingRequests, plan.HadBackground,
            AppFrom: plan.AppVersion, AppTo: plan.AppVersion);

        AgentSessionSettings? sessionSettings = null;
        if (!stopOnly)
            sessionSettings = BuildSessionSettings(c, historyRecap: null) with { Cli = target };
        // 等你回應時 CLI 不在安全點（Claude 的權限請求、Codex 的核准都在一輪中途）：app 更新照立即套用停下，不然會話只會一直回 Deferred。
        // 開著 Remote Control 的不在這裡停（政策表讓它等回應，滿 10 分鐘才以 ApplyNow 停下）
        bool immediate = plan.ApplyNow || resumingHeld
                         || (plan.AppUpdate && !(c.Session?.HasRemoteControl ?? false) && SituationOf(c) == CliRestartSituation.WaitingForUser);
        // app 更新排空逾時照立即套用停下只在等背景工作的對話：running.json 記成 app 停掉的，新版照它接續（AgentHub.InterruptedTurns.cs）
        if (plan.AppUpdate && immediate && !resumingHeld)
            MarkBackgroundStoppedByApp(c);
        return new AgentRestartRequest(
            plan.AppUpdate ? AgentRestartReason.AppUpdate : plan.ApplyNow ? AgentRestartReason.UserApplyNow : AgentRestartReason.CliUpdated,
            immediate ? AgentRestartMode.Immediate : AgentRestartMode.SafePoint,
            plan.FromVersion,
            target?.Version,
            AutoContinue: plan.NoContinueReason == null,
            ContinuationRules.Build(plan.Continuation),
            plan.InterruptedTools,
            sessionSettings);
    }

    /// <summary>正在執行、重開會打斷的工具：還在等核准的不算（還沒執行，記在 PendingRequests）</summary>
    private static List<AgentInterruptedTool> RunningTools(ConversationState c)
    {
        var now = DateTime.Now;
        return ExecutingTools(c)
            .Select(i => new AgentInterruptedTool(i.ToolName ?? "工具", i.Title, now - i.CreatedAt))
            .ToList();
    }

    /// <summary>執行中的工具卡，不含還在等核准的（它們代表還沒核准、還沒執行的操作）</summary>
    private static IEnumerable<AgentChatItem> ExecutingTools(ConversationState c)
        => c.Items.Where(i => i.Kind == AgentItemKind.Tool && i.Status == AgentItemStatus.Running && !IsUnapproved(c, i));

    /// <summary>這張工具卡還在等核准（AgentApprovalEvent 的 ToolId 對到它，使用者還沒回應）</summary>
    private static bool IsUnapproved(ConversationState c, AgentChatItem tool) => c.UnapprovedTools.Values.Any(t => ReferenceEquals(t, tool));

    private static void ForgetUnapproved(ConversationState c, AgentChatItem tool)
    {
        foreach (var key in c.UnapprovedTools.Where(kv => ReferenceEquals(kv.Value, tool)).Select(kv => kv.Key).ToList())
            c.UnapprovedTools.Remove(key);
    }

    private static List<string> PendingRequests(ConversationState c)
        => c.Items.Where(i => i.Status == AgentItemStatus.Waiting && !i.AnswerAsMessage && i.Kind is AgentItemKind.Approval or AgentItemKind.Question or AgentItemKind.PlanApproval)
            .Select(DescribeRequest)
            .ToList();

    /// <summary>被打斷的工具裡有沒有高風險操作：還在等核准的不算（沒有執行，結果不是未知）</summary>
    private CliRiskMatch? FindRunningToolRisk(ConversationState c)
    {
        foreach (var tool in ExecutingTools(c))
        {
            if (CliRestartRules.FindRisk(tool.ToolName, tool.ToolKind, tool.Title, tool.Detail, settings.CliRestartRiskExtra, settings.CliRestartRiskDisabled) is { } risk)
                return risk;
        }
        return null;
    }

    /// <summary>存活的背景工作裡有沒有高風險操作（會話對到的開始它的工具；設計 7.7 的規則同前景工具）</summary>
    private CliRiskMatch? FindBackgroundToolRisk(ConversationState c)
    {
        foreach (var tool in c.Session?.LiveBackgroundTools ?? Array.Empty<AgentBackgroundTool>())
        {
            if (CliRestartRules.FindRisk(tool.ToolName, tool.ToolKind, tool.Title, tool.Detail, settings.CliRestartRiskExtra, settings.CliRestartRiskDisabled) is { } risk)
                return risk;
        }
        return null;
    }

    private async Task RunRestartAsync(ConversationState c, RestartPlan plan, IAgentSession session, AgentRestartRequest request, bool wasRunning)
    {
        var provider = c.Meta.Provider;
        AgentRestartResult result;
        try
        {
            result = await session.RestartAsync(request);
        }
        catch (Exception ex)
        {
            result = new AgentRestartResult(AgentRestartOutcome.Failed, Error: ex.Message);
        }
        finally
        {
            restartsInFlight[provider] = Math.Max(0, RestartsInFlight(provider) - 1);
        }
        plan.InFlight = false;
        plan.Restarting = false;
        if (disposed)
            return;
        if (IsLive(c) && ReferenceEquals(c.Session, session) && ReferenceEquals(c.Restart.Plan, plan))
            HandleRestartResult(c, plan, request, result, wasRunning);
        else
            Log.Info($"換版重開結束時對話已刪除或換了會話（{result.Outcome}），不處理結果");
        AfterRestartStep(provider);
        // 重開期間的送出（DeferSendDuringRestart）：重開有了結果才送（接續的一輪還在跑就留在佇列，這一輪結束後接著送）
        ResumeSendAfterRestart(c);
    }

    private void HandleRestartResult(ConversationState c, RestartPlan plan, AgentRestartRequest request, AgentRestartResult result, bool wasRunning)
    {
        var r = c.Restart;
        var provider = c.Meta.Provider;
        Log.Info($"換版重開對話 {c.Meta.Id} 的結果：{result.Outcome}{(result.Error == null ? string.Empty : "（" + result.Error + "）")}");
        Journal("restart.result", c, new JObject
        {
            ["outcome"] = result.Outcome.ToString(),
            ["error"] = BackgroundJournalRules.Clip(result.Error),
            ["wasRunning"] = wasRunning,
            ["appUpdate"] = plan.AppUpdate ? true : null,
        });
        // 被停下的舊程序的卡片作廢（新程序的 AgentProcessStartedEvent 也會作廢；直接殺的 Codex 沒有撤回核准的通知）
        if (result.PreviousProcessInstance != null)
            InvalidateWaitingCards(c, i => i.ProcessInstance == result.PreviousProcessInstance);

        // 屏障停下的那一輪：重開要求有了結果（會話沒有在安全點的問題，Deferred 只會是重入）就不再是「待重開」
        bool resumingHeld = plan.ResumeHeld;
        if (result.Outcome != AgentRestartOutcome.Deferred)
            plan.ResumeHeld = false;
        if (resumingHeld && result.Outcome == AgentRestartOutcome.NotSupported)
            result = new AgentRestartResult(AgentRestartOutcome.Failed, result.PreviousProcessInstance, result.ProcessInstance, result.Error);

        // 會話開始過接續的一輪（AgentTurnStartedByCliEvent）就算這一輪：沒有接續、失敗時它一樣要在本機收尾（會話不會再回報它的結束）
        bool turnActive = c.Running && (wasRunning || plan.ContinuationStarted);
        switch (result.Outcome)
        {
            case AgentRestartOutcome.RestartedAndContinued:
                // 會話沒發 AgentTurnStartedByCliEvent 就現在開始；已經開始（接續的一輪甚至已經做完）的不再開始
                if (!plan.ContinuationStarted)
                    StartAutoTurn(c, CliRestartRules.ContinuedStatus);
                ConfirmContinuation(c, plan, request);
                RecordOutcome(provider, plan, WaveOutcome.Continued);
                if (plan.Drain)
                {
                    // 重開途中開始了更新屏障：新程序跑的還是要被換掉的檔案，照屏障排空（到安全點停下）
                    plan.Waiting = null;
                    EvaluateRestart(c);
                }
                else
                {
                    FinishPlan(c, plan);
                }
                break;

            case AgentRestartOutcome.Restarted:
                if (result.ProcessInstance == null)
                    MarkProcessGone(c, result.PreviousProcessInstance);
                // 被打斷的那一輪（或開始了、CLI 卻沒有接續的那一輪）還算執行中：會話已不在執行，不會再回報它的結束——在本機收尾。
                // 這一輪在中斷生效前自己結束的（Codex 照常回報 TurnCompleted）已經不在執行中，只算關了程序
                if (turnActive)
                {
                    WrapUpNotContinued(c, plan, request);
                    RecordOutcome(provider, plan, WaveOutcome.NotContinued);
                }
                else
                {
                    RecordOutcome(provider, plan, WaveOutcome.Restarted);
                    if (plan.Drain && barriers.TryGetValue(provider, out var closedBarrier) && closedBarrier.Wave == plan.Wave)
                        closedBarrier.ClosedIdle++;
                }
                FinishPlan(c, plan);
                break;

            case AgentRestartOutcome.Stopped:
                MarkProcessGone(c, result.PreviousProcessInstance);
                if (wasRunning && c.Running && !c.StopRequested)
                {
                    // 屏障停下了執行中的這一輪：不是「已停止」、佇列不暫停，等 UpdateFinished 後以新版接續
                    plan.Held = true;
                    plan.Drained = true;
                    plan.Waiting = StoppedState(plan);
                    FinalizeInterruptedItems(c);
                    InvalidateWaitingCards(c, _ => true);
                    c.StatusText = CliRestartRules.DescribeState(StoppedState(plan), provider, null);
                    SaveConversation(c, saveItems: true);
                    MarkHeaderDirty(c);
                    // UpdateFinished 比停下的結果先到（停下途中按了「取消更新」）：屏障已經結束，現在就照它的結果接續或收尾。
                    // 停下途中又開始了下一次屏障（Drain 又成立）就留著，等那一次的 UpdateFinished
                    if (plan.FinishedUpdate is { } finished && !plan.Drain)
                    {
                        plan.FinishedUpdate = null;
                        plan.Drained = false;
                        plan.Waiting = null;
                        ResumeHeldAfterUpdate(c, plan, finished);
                    }
                }
                else
                {
                    if (wasRunning && c.Running)
                    {
                        WrapUpInterruptedTurn(c, c.Queue.Count > 0 ? "已停止這一輪（排隊的訊息暫停，按送出繼續）" : "已停止這一輪", cliNotice: false, view: null);
                        RecordOutcome(provider, plan, WaveOutcome.NotContinued);
                    }
                    else
                    {
                        RecordOutcome(provider, plan, WaveOutcome.Restarted);
                        if (plan.Drain && barriers.TryGetValue(provider, out var barrier) && barrier.Wave == plan.Wave)
                            barrier.ClosedIdle++;
                    }
                    FinishPlan(c, plan);
                }
                break;

            case AgentRestartOutcome.Deferred:
                // 不在安全點：等下一個安全點事件（或工具結束、使用者回應、維護計時器）再試
                plan.Deferred = true;
                plan.Waiting = null;
                EvaluateRestart(c);
                // 立即重開也可能回 Deferred（Claude：剛送出的訊息 CLI 還沒接手，會話最多等 10 秒）：不必等安全點，稍後主動再試
                if (request.Mode == AgentRestartMode.Immediate)
                    ScheduleDeferredRetry(c, plan);
                break;

            case AgentRestartOutcome.Failed:
                MarkProcessGone(c, result.PreviousProcessInstance);
                if (turnActive)
                    WrapUpInterruptedTurn(c, CliRestartRules.FailedNotice(result.Error, c.Queue.Count > 0), cliNotice: true, FailedView(c, plan, request.ToVersion, result.Error));
                else
                    r.Result = FailedView(c, plan, request.ToVersion, result.Error);
                RecordOutcome(provider, plan, WaveOutcome.Failed);
                FinishPlan(c, plan);
                break;

            default:
                // 會話還不支援以新版重開：退回「這一輪結束後關程序」
                plan.CannotRestart = true;
                plan.Waiting = null;
                EvaluateRestart(c);
                break;
        }
        // 停下了只在等背景工作的對話（閒置）：這一輪早就結束、沒有接續，另外請代理確認背景工作
        if (plan.BackgroundCheck && result.Outcome is AgentRestartOutcome.Restarted or AgentRestartOutcome.Stopped && !c.Running)
            QueueBackgroundCheck(c, plan, request);
        MarkHeaderDirty(c);
    }

    /// <summary>
    /// CLI 換版停掉了只在等背景工作的對話的背景工作（BackgroundCheck）：以新版開始後馬上隱藏送出確認訊息（ContinuationRules 的 WaitingBackground：
    /// 背景工作在重啟時停止、結果未知、不會再有完成通知，請先確認做到哪裡、可安全重跑才重跑）。被停掉的是高風險操作（部署、發布、git push）時
    /// 不自動送，標頭給「繼續」（同被打斷的高風險工具）。更新屏障還沒結束時先記著，屏障結束或維護時送出（TrySendBackgroundCheck）
    /// </summary>
    private void QueueBackgroundCheck(ConversationState c, RestartPlan plan, AgentRestartRequest request)
    {
        plan.BackgroundCheck = false;
        var provider = c.Meta.Provider;
        var context = new ContinuationContext(provider, plan.FromVersion, request.ToVersion, Array.Empty<AgentInterruptedTool>(), Array.Empty<string>(),
            BackgroundWorkStopped: true, WaitingBackground: true, BackgroundTasks: plan.BackgroundTasks.ToList());
        Journal("bg.check", c, new JObject
        {
            ["phase"] = plan.Risk != null ? "notChecked" : "queued",
            ["risk"] = plan.Risk?.RuleId,
            ["bg"] = plan.BackgroundTasks.Count > 0 ? new JArray(plan.BackgroundTasks.Select(t => BackgroundJournalRules.Clip(t))) : null,
        });
        if (plan.Risk is { } risk)
        {
            Log.Info($"對話 {c.Meta.Id} 換版停掉的背景工作是高風險操作（{risk.RuleId}），不自動請代理確認");
            c.Restart.Result = new RestartResultView(CliRestartState.NotContinued, plan.FromVersion, request.ToVersion,
                CliRestartRules.DescribeState(CliRestartState.NotContinued, provider, request.ToVersion,
                    CliRestartRules.DescribeNoContinue(CliNoContinueReason.RiskyTool, risk.Operation), fromVersion: plan.FromVersion),
                CanContinue: true, context);
            AddCliNotice(c, CliRestartRules.BackgroundNotCheckedNotice(request.ToVersion, risk.Operation));
            SaveConversation(c, saveItems: true);
            return;
        }
        c.PendingBackgroundCheck = new BackgroundCheckRequest(context, CliRestartRules.BackgroundCheckNotice(request.ToVersion));
        TrySendBackgroundCheck(c);
    }

    /// <summary>
    /// 送出記著的背景工作確認：CLI 還在更新、這個對話正在換版或在執行時再等（屏障結束、維護計時器會再試）；對話已刪除、app 正在結束就丟掉。
    /// 等待期間開始了別的一輪（使用者送出、CLI 自己開一輪）時 HookTurnStarted 已經丟掉它
    /// </summary>
    private void TrySendBackgroundCheck(ConversationState c)
    {
        if (c.PendingBackgroundCheck is not { } pending)
            return;
        if (disposed || !IsLive(c) || c.IsReplica)
        {
            c.PendingBackgroundCheck = null;
            return;
        }
        if (c.Running || IsCliUpdateBlocking(c.Meta.Provider) || IsRestartInFlight(c))
            return;
        c.PendingBackgroundCheck = null;
        Log.Info($"對話 {c.Meta.Id} 換版停掉了背景工作，隱藏送出確認訊息");
        Journal("bg.check", c, new JObject { ["phase"] = "sent" });
        _ = SendCoreAsync(c, ContinuationRules.Build(pending.Context), new List<AgentAttachment>(), pending.Notice, hiddenNoticeKind: AgentNoticeKind.CliUpdate);
    }

    private RestartResultView FailedView(ConversationState c, RestartPlan plan, string? toVersion, string? error)
        => new(CliRestartState.Failed, plan.FromVersion, toVersion,
            CliRestartRules.DescribeState(CliRestartState.Failed, c.Meta.Provider, toVersion, error), CanContinue: false, Continuation: null);

    /// <summary>不是由送出開始的一輪開始了（自動接續）：對話不在執行中就改成執行中；status＝狀態文字</summary>
    private void StartAutoTurn(ConversationState c, string status)
    {
        if (!c.Running)
        {
            var now = DateTime.Now;
            c.Running = true;
            c.StopRequested = false;
            c.DeniedThisTurn = false;
            c.QuotaExhausted = null;
            c.TurnStartedAt = now;
            c.LastActivity = now;
            c.Meta.UpdatedAt = now;
            UpdateRunningCounts();
            MarkListDirty();
            accounts.ReportActivity(c.Meta.AccountId);
        }
        c.StatusText = status;
        MarkHeaderDirty(c);
    }

    /// <summary>
    /// 確定已用新版接續（RestartAsync 回 RestartedAndContinued）：加「已用新版自動接續」的 Notice（只加一次）；
    /// 接續的一輪還在跑就改狀態文字、標頭顯示 continued（這一輪結束時清掉）。接續的一輪在這之前就做完的，只補 Notice
    /// </summary>
    private void ConfirmContinuation(ConversationState c, RestartPlan plan, AgentRestartRequest? request)
    {
        plan.ContinuationConfirmed = true;
        var toVersion = request?.ToVersion ?? plan.Target?.Version;
        bool appAbandoned = AppUpdateAbandoned(plan);
        if (!plan.ContinuedNoticeAdded)
        {
            plan.ContinuedNoticeAdded = true;
            AddCliNotice(c, CliRestartRules.ContinuedNotice(c.Meta.Provider, toVersion, plan.InterruptedTools, plan.FromVersion, appAbandoned));
        }
        if (c.Running)
        {
            if (c.StatusText is CliRestartRules.RestartingStatus or SendingStatus || string.IsNullOrEmpty(c.StatusText))
                c.StatusText = CliRestartRules.ContinuedStatus;
            c.Restart.Result = new RestartResultView(CliRestartState.Continued, plan.FromVersion, toVersion,
                CliRestartRules.DescribeState(CliRestartState.Continued, c.Meta.Provider, toVersion, fromVersion: plan.FromVersion, appUpdateAbandoned: appAbandoned),
                CanContinue: false, Continuation: null);
        }
        MarkHeaderDirty(c);
    }

    /// <summary>
    /// 放棄 app 更新後就地重開這一輪（排空停下的保留 AppVersion、屏障已結束）：不是 CLI 換版，標頭與提示不講「已用新版」
    /// </summary>
    private static bool AppUpdateAbandoned(RestartPlan plan) => plan.AppVersion != null && !plan.AppUpdate;

    private void AddCliNotice(ConversationState c, string text)
    {
        AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = text, NoticeKind = AgentNoticeKind.CliUpdate });
        SaveConversation(c, saveItems: true);
    }

    /// <summary>沒有接續的一輪收尾：使用者按了停止就照停止的樣子；其他原因加 CLI 換版的提示，標頭給「繼續」</summary>
    private void WrapUpNotContinued(ConversationState c, RestartPlan plan, AgentRestartRequest request)
    {
        var reason = c.StopRequested ? CliNoContinueReason.UserStopped : plan.NoContinueReason ?? CliNoContinueReason.Unknown;
        if (reason == CliNoContinueReason.UserStopped)
        {
            WrapUpInterruptedTurn(c, c.Queue.Count > 0 ? "已停止這一輪（排隊的訊息暫停，按送出繼續）" : "已停止這一輪", cliNotice: false, view: null);
            return;
        }
        var operation = reason == CliNoContinueReason.RiskyTool ? plan.Risk?.Operation : null;
        bool appAbandoned = AppUpdateAbandoned(plan);
        var view = new RestartResultView(CliRestartState.NotContinued, plan.FromVersion, request.ToVersion,
            CliRestartRules.DescribeState(CliRestartState.NotContinued, c.Meta.Provider, request.ToVersion, CliRestartRules.DescribeNoContinue(reason, operation),
                fromVersion: plan.FromVersion, appUpdateAbandoned: appAbandoned),
            CliRestartRules.CanContinueManually(reason), plan.Continuation);
        WrapUpInterruptedTurn(c, CliRestartRules.NotContinuedNotice(request.ToVersion, reason, operation, c.Queue.Count > 0, plan.FromVersion, appAbandoned), cliNotice: true, view);
    }

    /// <summary>
    /// 被打斷、沒有接續的一輪在本機收尾（舊程序已停，CLI 不會再回報 TurnCompleted）：與 CompleteTurn 相同的清理，佇列暫停（按送出才接著送）。
    /// 這一輪是為了執行中換帳戶停下的（CompleteTurn 同）：不加「已停止」，收尾之後換過去、從中斷處接著做；
    /// 選了這一輪結束時才換的照一般的收尾，之後只換帳戶
    /// </summary>
    private void WrapUpInterruptedTurn(ConversationState c, string notice, bool cliNotice, RestartResultView? view)
    {
        var userSwitch = TakeUserAccountSwitch(c);
        bool stoppedForSwitch = userSwitch is { AtTurnEnd: false };
        EnsureItems(c);
        FinalizeInterruptedItems(c);
        InvalidateWaitingCards(c, _ => true);
        foreach (var item in c.Items.Where(i => i.Kind == AgentItemKind.Plan && i.Status == AgentItemStatus.Running).ToList())
        {
            item.Status = AgentItemStatus.Done;
            MarkItemDirty(c, item);
        }
        // Codex 拒絕時附的說明不能丟：排到佇列最前面（跟著佇列暫停），按送出才送
        var feedback = AgentHubRules.BuildDenyFeedbackMessage(c.PendingFeedback);
        var feedbackOrigin = DenyFeedbackRules.OriginOf(c.PendingFeedback);
        c.PendingFeedback.Clear();
        if (feedback != null && !c.StopRequested)
            c.Queue.Insert(0, new QueuedMessage { Text = feedback, RemoteOrigin = feedbackOrigin });

        if (!stoppedForSwitch || cliNotice)
            AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = notice, NoticeKind = cliNotice ? AgentNoticeKind.CliUpdate : null });
        var wasRunning = c.Running;
        c.Running = false;
        c.StatusText = string.Empty;
        if (wasRunning)
            HookTurnCompleted(c, AgentTurnOutcome.Interrupted, null);
        c.StopRequested = false;
        c.DeniedThisTurn = false;
        c.BlockItems.Clear();
        c.ToolItems.Clear();
        foreach (var key in c.RequestItems.Where(kv => !kv.Value.AnswerAsMessage).Select(kv => kv.Key).ToList())
            c.RequestItems.Remove(key);
        c.PlanItem = null;
        c.Meta.UpdatedAt = DateTime.Now;
        c.Restart.Result = view;
        if (c.VisibleViewIds.Count == 0)
            c.Unread = true;
        SaveConversation(c, saveItems: true);
        UpdateRunningCounts();
        MarkHeaderDirty(c);
        MarkListDirty();
        PostComposer(c);
        accounts.ReportTurnCompleted(c.Meta.AccountId);
        host.RequestAttention(c.ViewIds);
        if (stoppedForSwitch)
            ContinueOnChosenAccount(c, userSwitch!);
        else if (userSwitch != null && accounts.Find(userSwitch.AccountId) is { } chosen)
            ApplyChosenAccount(c, chosen, userSwitch.RememberAccount);
    }

    /// <summary>
    /// 舊程序停下（或換了新程序）時，它串流到一半的項目：空的回覆移除、文字標為已中斷（接續的一輪會從頭重寫，不和它接在一起）、
    /// 執行中的工具標為已停止並註明是重開中斷的、結果未知（會話沒送工具結束的：Codex 直接結束程序、Claude 舊程序的輸出沒讀完）；
    /// 還在等核准的工具（Codex 沒核准的指令不會有 item/completed）註明沒有核准、沒有執行；待辦清單留給接續的一輪更新
    /// </summary>
    private void FinalizeInterruptedItems(ConversationState c)
    {
        foreach (var item in c.Items.Where(i => i.Status == AgentItemStatus.Running && i.Kind != AgentItemKind.Plan).ToList())
        {
            if (item.Kind is AgentItemKind.Assistant or AgentItemKind.Thinking && string.IsNullOrWhiteSpace(item.Text) && item.MediaPaths.Count == 0)
            {
                RemoveItem(c, item);
                continue;
            }
            item.Status = item.Kind is AgentItemKind.Tool or AgentItemKind.Assistant ? AgentItemStatus.Cancelled : AgentItemStatus.Done;
            if (item.Kind == AgentItemKind.Tool)
            {
                item.Output = IsUnapproved(c, item)
                    ? CliRestartRules.NotApprovedToolOutput
                    : AgentHubRules.TrimToolOutput(CliRestartRules.AppendInterruptedNote(item.Output, c.Meta.Provider));
            }
            MarkItemDirty(c, item);
        }
        c.BlockItems.Clear();
        c.ToolItems.Clear();
        c.UnapprovedTools.Clear();
    }

    /// <summary>產生卡片的程序已不在：還在等待的卡片（核准、問卷、計畫確認；Codex 的非同步提問除外）改為已失效，絕不能把回覆送給新程序</summary>
    private void InvalidateWaitingCards(ConversationState c, Func<AgentChatItem, bool> predicate)
    {
        bool changed = false;
        foreach (var item in c.Items.Where(i => i.Status == AgentItemStatus.Waiting && !i.AnswerAsMessage
                                                && i.Kind is AgentItemKind.Approval or AgentItemKind.Question or AgentItemKind.PlanApproval
                                                && predicate(i)).ToList())
        {
            InvalidateCard(c, item);
            changed = true;
        }
        if (changed)
            SaveConversation(c, saveItems: true);
    }

    private void InvalidateCard(ConversationState c, AgentChatItem item)
    {
        item.Status = AgentItemStatus.Cancelled;
        item.Output = CliRestartRules.StaleCardOutput;
        MarkItemDirty(c, item);
        foreach (var key in c.RequestItems.Where(kv => ReferenceEquals(kv.Value, item)).Select(kv => kv.Key).ToList())
            c.RequestItems.Remove(key);
    }

    /// <summary>空出啟動名額：等名額的對話依「執行中的先、等最久的先」接著重開</summary>
    private void PumpRestarts(AgentProvider provider)
    {
        var waiting = ConversationsOf(provider)
            .Where(c => c.Restart.Plan is { WantsSlot: true, InFlight: false })
            .OrderByDescending(c => c.Running)
            .ThenBy(c => c.Restart.Plan!.Since)
            .ToList();
        foreach (var c in waiting)
        {
            if (RestartsInFlight(provider) >= CliRestartRules.MaxConcurrentStarts(provider))
                break;
            c.Restart.Plan!.WantsSlot = false;
            EvaluateRestart(c);
        }
    }

    // ───────────────────────────── 會話事件 ─────────────────────────────

    /// <summary>會話啟動了新程序：記下事件流上的目前實例與身分，舊實例的等待卡片作廢；還在跑舊身分（送出前才建立的設定）就依政策處理</summary>
    private void OnProcessStarted(ConversationState c, AgentProcessStartedEvent ev)
    {
        var r = c.Restart;
        var provider = c.Meta.Provider;
        var current = cli.GetIdentity(provider);
        // CLI 卡的「還在跑舊版」可能改變：舊的或新的程序不是目前安裝的身分
        bool affectsCli = r.Plan != null || !CliBinaryIdentity.SameOrUnknown(r.InstanceIdentity, current) || !CliBinaryIdentity.SameOrUnknown(ev.Cli, current);
        var previous = r.Instance;
        bool changed = !string.Equals(previous, ev.ProcessInstance, StringComparison.Ordinal);
        r.Instance = ev.ProcessInstance;
        r.InstanceIdentity = ev.Cli;
        r.RunningVersion = null;
        r.SafePoint = null;
        r.ProcessGone = false;
        if (changed)
        {
            if (previous != null)
                FinalizeInterruptedItems(c);
            InvalidateWaitingCards(c, i => !string.Equals(i.ProcessInstance, ev.ProcessInstance, StringComparison.Ordinal));
        }

        if (barriers.TryGetValue(provider, out var barrier))
        {
            // 屏障期間不該有新程序（送出都排進佇列）；萬一有，也要排空
            if (barrier.Draining && r.Plan is not { InFlight: true })
            {
                var plan = r.Plan ??= new RestartPlan(barrier.Wave, RestartClock());
                if (!plan.Held)
                {
                    plan.Drain = true;
                    plan.Drained = false;
                    // app 更新的屏障照 app 的規則停下（等回應視為安全點、停下後由新版 app 接續），同屏障開始時排定的對話
                    plan.AppUpdate = barrier.AppUpdate;
                    plan.AppVersion = barrier.AppUpdate ? AppVersionInfo.VersionText : null;
                    EvaluateRestart(c);
                }
            }
        }
        else
        {
            // 還在跑舊身分（送出前才建立的設定）：依政策處理；已排定的重新檢查（換成新身分的就不用了）
            if (r.Plan == null && !c.IsDraft && current != null && !CliBinaryIdentity.SameOrUnknown(ev.Cli, current))
                r.Plan = new RestartPlan(CurrentWave(provider, current.Version).Id, RestartClock()) { Target = current, FromVersion = ev.Cli?.Version };
            if (r.Plan is { InFlight: false })
                EvaluateRestart(c);
        }
        MarkHeaderDirty(c);
        if (affectsCli)
            MarkCliDirty();
    }

    private void OnProcessExited(ConversationState c, AgentProcessExitedEvent ev)
    {
        bool affectsCli = c.Restart.Plan != null || IsOnOldVersion(c);
        // Codex 被直接殺時沒有撤回核准的通知：那個程序的等待卡片在這裡作廢
        if (ev.ProcessInstance != null)
        {
            InvalidateWaitingCards(c, i => string.Equals(i.ProcessInstance, ev.ProcessInstance, StringComparison.Ordinal));
            if (string.Equals(ev.ProcessInstance, c.Restart.Instance, StringComparison.Ordinal))
                c.Restart.ProcessGone = true;
        }
        if (c.Restart.Plan != null)
        {
            EvaluateRestart(c);
            CheckBarrier(c.Meta.Provider);
            MarkHeaderDirty(c);
        }
        if (affectsCli)
            MarkCliDirty();
    }

    private void OnRunningVersion(ConversationState c, AgentRunningVersionEvent ev)
    {
        var r = c.Restart;
        if (r.Instance != null && !string.Equals(ev.ProcessInstance, r.Instance, StringComparison.Ordinal))
            return;   // 舊程序晚到的回報
        r.RunningVersion = ev.Version;
        if (c.Meta.LastCliVersion != ev.Version)
        {
            c.Meta.LastCliVersion = ev.Version;
            SaveConversation(c, saveItems: false);
        }
        // 程序回報的版本與啟動用的身分不同（啟動器可能還指向舊檔）：CliManager 重新定位後再判定一次，換到別的身分就照一般換版重開（7.2.4）
        if (r.InstanceIdentity is { } started && !string.Equals(started.Version, ev.Version, StringComparison.Ordinal))
            cli.ReportRunningVersion(c.Meta.Provider, started, ev.Version);
        MarkHeaderDirty(c);
    }

    private void OnSafePointChanged(ConversationState c, AgentSafePointChangedEvent ev)
    {
        c.Restart.SafePoint = ev;
        RecheckRestart(c);
    }

    /// <summary>可能到了安全點（工具結束、使用者回應、安全點事件）：排定換版的對話重新檢查</summary>
    private void RecheckRestart(ConversationState c)
    {
        if (c.Restart.Plan is not { } plan)
            return;
        plan.Deferred = false;
        EvaluateRestart(c);
        MarkHeaderDirty(c);
    }

    /// <summary>
    /// 會話開始了不是由送出開始的一輪（自動接續）：對話改成（或維持）執行中。重開中的「已用新版自動接續」要等 RestartAsync 回
    /// RestartedAndContinued 才確認——Claude 新程序一啟動就發這個事件，CLI 這時還不一定會接續（沒接續時會話回 Restarted）
    /// </summary>
    private void OnTurnStartedByCli(ConversationState c, AgentTurnStartedByCliEvent ev)
    {
        // 換版接續時舊的一輪被 SwallowTurnEndDuringRestart 吞掉、對話仍在執行中：接續的一輪與它算同一輪，不再發一次「開始」
        var wasRunning = c.Running;
        if (c.Restart.Plan is { InFlight: true, Restarting: true } plan)
        {
            plan.ContinuationStarted = true;
            StartAutoTurn(c, plan.ContinuationConfirmed ? CliRestartRules.ContinuedStatus : CliRestartRules.RestartingStatus);
            MarkCliDirty();
        }
        else
        {
            StartAutoTurn(c, CliRestartRules.ContinuedStatus);
        }

        if (!wasRunning)
            HookTurnStarted(c, startedByCli: true);
    }

    /// <summary>
    /// 重開期間舊程序回報這一輪被中斷（契約上會話不發被打斷那一輪的 TurnCompleted，萬一發了）：不加「已停止」、佇列不暫停，
    /// 這一輪由重開的結果接續或收尾。Completed／Failed 照常處理：那是這一輪在中斷生效前自己結束的（Codex 照常回報、
    /// Claude 舊程序在卸下前送出的 result），失敗的原因要讓使用者看到、佇列照失敗的規則暫停；之後的重開結果只算關了程序
    /// </summary>
    private bool SwallowTurnEndDuringRestart(ConversationState c, AgentTurnCompletedEvent turn)
    {
        if (c.Restart.Plan is not { } plan || c.StopRequested || turn.Outcome != AgentTurnOutcome.Interrupted || c.DeniedThisTurn)
            return false;
        bool restarting = plan is { InFlight: true, Restarting: true, ContinuationStarted: false };
        if (!restarting && !plan.Held)
            return false;
        Log.Info($"對話 {c.Meta.Id} 重開期間收到舊程序的一輪結束（{turn.Outcome}），不當成這一輪結束");
        return true;
    }

    /// <summary>這一輪結束（CompleteTurn 清理之後、接著送之前）：自動接續的狀態清掉，排定換版的現在處理（閒置的關程序）</summary>
    private void OnTurnEndedForRestart(ConversationState c)
    {
        var r = c.Restart;
        if (r.Result is { State: CliRestartState.Continued })
        {
            r.Result = null;
            MarkHeaderDirty(c);
        }
        if (r.Plan != null)
        {
            RecheckRestart(c);
            MarkCliDirty();
        }
    }

    /// <summary>使用者對屏障停下（等待 CLI 更新）的對話按停止：會話已沒有程序、不會回報 TurnCompleted，在本機收尾</summary>
    private void StopTurnHeldForCliUpdate(ConversationState c)
    {
        if (c.Restart.Plan is not { Held: true } plan || !c.Running)
            return;
        plan.Held = false;
        WrapUpInterruptedTurn(c, c.Queue.Count > 0 ? "已停止這一輪（排隊的訊息暫停，按送出繼續）" : "已停止這一輪", cliNotice: false, view: null);
        RecordOutcome(c.Meta.Provider, plan, WaveOutcome.NotContinued);
        FinishPlan(c, plan);
        AfterRestartStep(c.Meta.Provider);
    }

    /// <summary>回應卡片前比對實例：產生卡片的程序已不是事件流上的目前程序（兩邊都已知且不同）就作廢、不轉送</summary>
    private bool RejectStaleCard(ConversationState c, AgentChatItem item)
    {
        var current = c.Restart.Instance;
        if (item.AnswerAsMessage || item.ProcessInstance == null || current == null || string.Equals(item.ProcessInstance, current, StringComparison.Ordinal))
            return false;
        InvalidateCard(c, item);
        SaveConversation(c, saveItems: true);
        Toast("這張卡已失效（CLI 已重新啟動），代理會視需要重新提出");
        return true;
    }

    // ───────────────────────────── 送出 ─────────────────────────────

    /// <summary>這種 CLI 正在更新（屏障、安裝正在變動、npm 正在換檔）：不啟動新程序，送出排進佇列</summary>
    private bool IsCliUpdateBlocking(AgentProvider provider)
    {
        var state = cli.Get(provider);
        return barriers.ContainsKey(provider) || state.InstallChanging || state.ReplacingFiles;
    }

    /// <summary>送出被延後時告訴使用者誰正在更新：app 更新排空中是 Clode Myriagent，否則是這種 CLI</summary>
    private string UpdatingSubject(AgentProvider provider)
        => IsAppUpdateBarrier(provider) ? "Clode Myriagent 正在更新" : $"{CliRestartRules.ProductName(provider)} CLI 正在更新";

    /// <summary>
    /// 網頁的送出遇到 CLI 更新：排進佇列、更新完成後自動送出（在檢查帳戶狀態之前：npm 換檔期間帳戶查詢可能暫時把 CLI 看成未安裝）。
    /// 回 true＝已處理。<paramref name="origin"/> 是這次送出的來源（WP4.4 計畫 D19、D20：遠端的是那個指令的，本機的是 null 或佇列那則的）
    /// </summary>
    private bool QueueSendDuringCliUpdate(IAgentView view, ConversationState c, AgentProvider provider, string text, List<AgentAttachment> attachments, QueuedMessage? fromQueue,
        RemoteOrigin? origin, string? displayText = null)
    {
        if (!IsCliUpdateBlocking(provider))
            return false;
        var subject = UpdatingSubject(provider);
        if (fromQueue != null)
        {
            // 佇列暫停時的空白送出：更新完成後從第一則接著送（照這次送出的來源）
            if (!c.Running)
                c.Restart.DeferredSend ??= WithSendOrigin(c, fromQueue, origin);
            MarkHeaderDirty(c);
            Toast($"{subject}，佇列的訊息會在更新完成後送出", view: view);
            return true;
        }
        if (attachments.Count > 0)
        {
            foreach (var attachment in attachments)
                c.PendingAttachments.Remove(attachment);
        }
        DeferSend(c, new QueuedMessage { Text = text, Attachments = attachments, RemoteOrigin = origin, DisplayText = displayText });
        Toast($"{subject}，訊息已排進佇列，更新完成後自動送出", view: view);
        return true;
    }

    /// <summary>送出的最後一關（SendCoreAsync 開頭；佇列接著送、拒絕的說明、非同步提問的答案都經過這裡）：CLI 正在更新就延後</summary>
    private bool DeferSendForCliUpdate(ConversationState c, string text, List<AgentAttachment> attachments, bool hidden, AgentChatItem? answerFor,
        string? displayText, RemoteOrigin? remoteOrigin)
    {
        if (hidden || !IsCliUpdateBlocking(c.Meta.Provider))
            return false;
        DeferSend(c, new QueuedMessage { Text = text, Attachments = attachments, AnswerFor = answerFor, DisplayText = displayText, RemoteOrigin = remoteOrigin });
        return true;
    }

    private void DeferSend(ConversationState c, QueuedMessage message)
    {
        // 草稿的第一則被延後：先轉成正式對話（存檔、出現在清單）。仍是草稿的話，關掉分頁、關掉彈出視窗或重新載入時會被當成
        // 沒送出的草稿丟掉，排隊的訊息與附件就一起不見了
        PromoteDraft(c, message.DisplayText ?? message.Text, message.Attachments);
        var r = c.Restart;
        if (c.Running || (r.DeferredSend != null && c.Queue.Contains(r.DeferredSend)))
        {
            // 這一輪結束（或延後的那則送出）後照佇列順序送
            c.Queue.Add(message);
        }
        else
        {
            // 閒置時送出的：更新完成後第一個送（佇列暫停中的照舊排在後面）
            c.Queue.Insert(0, message);
            r.DeferredSend = message;
        }
        PostComposer(c);
        MarkHeaderDirty(c);
    }

    /// <summary>草稿轉成正式對話（標題取第一則訊息、存檔、出現在對話清單）；已經是正式對話就不做事</summary>
    private void PromoteDraft(ConversationState c, string text, List<AgentAttachment> attachments)
    {
        if (!c.IsDraft)
            return;
        var now = DateTime.Now;
        c.IsDraft = false;
        c.Meta.Title = AgentHubRules.MakeTitle(text, attachments);
        c.Meta.CreatedAt = now;
        c.Meta.UpdatedAt = now;
        SaveConversation(c, saveItems: true);
        MarkListDirty();
        MarkHeaderDirty(c);
    }

    /// <summary>CLI 不再更新：延後的訊息送出（含草稿的第一則：延後時已轉成正式對話）；還在換版的對話等換版做完（ResumeSendAfterRestart）</summary>
    private void ResumeDeferredSends(AgentProvider provider)
    {
        if (disposed || IsCliUpdateBlocking(provider))
            return;
        foreach (var c in conversations.Values.Where(c => c.Meta.Provider == provider && c.Restart.DeferredSend != null && !IsRestartInFlight(c)).ToList())
        {
            var message = c.Restart.DeferredSend;
            if (message == null)
                continue;
            c.Restart.DeferredSend = null;
            MarkHeaderDirty(c);
            if (c.Running || !c.Queue.Remove(message))
                continue;
            PostComposer(c);
            _ = SendCoreAsync(c, message.Text, message.Attachments, answerFor: message.AnswerFor, displayText: message.DisplayText, remoteOrigin: message.RemoteOrigin);
        }
    }

    /// <summary>連續被拒幾次（中樞不知道的重開）之後不再自動重送，留在佇列最前面等使用者按送出</summary>
    private const int MaxRejectedSendRetries = 5;

    /// <summary>會話拒絕送出（正在重開）而中樞沒有排定的換版時，多久之後再試一次；測試可縮短</summary>
    internal TimeSpan RejectedSendRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>這個對話的換版步驟（關掉舊程序或 RestartAsync）進行中：不能呼叫 SendAsync</summary>
    private static bool IsRestartInFlight(ConversationState c) => c.Restart.Plan is { InFlight: true };

    /// <summary>
    /// 送出的最後一關（SendCoreAsync 開頭，CLI 更新的延後之後）：這個對話正在換版就不呼叫 SendAsync——Codex 會話會丟
    /// AgentRestartInProgressException，Claude 會話會等重開結束（接續的一輪開始了就變成「代理還在處理上一則訊息」）。
    /// 一般送出排進佇列（閒置時排最前面），換版做完後照常送出（ResumeSendAfterRestart）；隱藏送出（「繼續」）不排隊，請使用者稍候再按。
    /// 回 true＝已處理
    /// </summary>
    private bool DeferSendDuringRestart(ConversationState c, string text, List<AgentAttachment> attachments, bool hidden, AgentChatItem? answerFor,
        string? displayText, RemoteOrigin? remoteOrigin)
    {
        if (!IsRestartInFlight(c))
            return false;
        if (hidden)
        {
            Toast("正在以新版 CLI 重新啟動這個對話，請稍候再按「繼續」");
            MarkHeaderDirty(c);
            return true;
        }
        Log.Info($"對話 {c.Meta.Id} 正在以新版 CLI 重開，送出排進佇列，重開做完後照常送出");
        DeferSend(c, new QueuedMessage { Text = text, Attachments = attachments, AnswerFor = answerFor, DisplayText = displayText, RemoteOrigin = remoteOrigin });
        return true;
    }

    /// <summary>
    /// 換版步驟做完（關掉舊程序或 RestartAsync 有了結果）：換版期間延後的送出照常送出；
    /// 又排定了下一步（例如重開途中開始了更新屏障）、CLI 正在更新、或這一輪在執行中（接續的一輪）就繼續等
    /// </summary>
    private void ResumeSendAfterRestart(ConversationState c)
    {
        var message = c.Restart.DeferredSend;
        if (message == null || disposed || IsRestartInFlight(c) || IsCliUpdateBlocking(c.Meta.Provider)
            || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
            return;
        c.Restart.DeferredSend = null;
        MarkHeaderDirty(c);
        // 接續的一輪在執行中：留在佇列最前面插進這一輪，插不進去就等這一輪結束後照常接著送
        if (c.Running)
        {
            _ = SteerQueuedAsync(c);
            return;
        }
        if (!c.Queue.Remove(message))
            return;
        PostComposer(c);
        _ = SendCoreAsync(c, message.Text, message.Attachments, answerFor: message.AnswerFor, displayText: message.DisplayText, remoteOrigin: message.RemoteOrigin);
    }

    /// <summary>
    /// SendAsync 因為會話正在重開而拒絕（AgentRestartInProgressException）：不是這一輪失敗。撤掉剛加的項目、對話回到閒置，
    /// 訊息放回佇列最前面，換版做完後照常送出；中樞沒有排定的換版（不知道這次重開）時稍後再試，連續被拒 MaxRejectedSendRetries 次就留在佇列等使用者按送出。
    /// 隱藏送出（「繼續」）不排隊：還原標頭的「繼續」，請使用者稍候再按
    /// </summary>
    private void RequeueSendRejectedByRestart(ConversationState c, AgentChatItem sentItem, string text, List<AgentAttachment> attachments, bool hidden,
        RestartResultView? previousResult, AgentChatItem? answerFor, string? displayText, RemoteOrigin? remoteOrigin)
    {
        var r = c.Restart;
        r.RejectedSends++;
        Log.Warn($"對話 {c.Meta.Id} 的會話正在以新版 CLI 重開，送出被拒（第 {r.RejectedSends} 次）：訊息放回佇列，不當成這一輪失敗");
        if (c.Items.Contains(sentItem))
            RemoveItem(c, sentItem);
        var wasRunning = c.Running;
        c.Running = false;
        c.StatusText = string.Empty;
        if (wasRunning)
            HookTurnCompleted(c, AgentTurnOutcome.Interrupted, null);
        c.Meta.UpdatedAt = DateTime.Now;
        if (hidden)
        {
            r.Result ??= previousResult;
            Toast("正在以新版 CLI 重新啟動這個對話，請稍候再按「繼續」");
        }
        else
        {
            var message = new QueuedMessage { Text = text, Attachments = attachments, AnswerFor = answerFor, DisplayText = displayText, RemoteOrigin = remoteOrigin };
            c.Queue.Insert(0, message);
            r.DeferredSend = message;
            if (!IsRestartInFlight(c))
            {
                if (r.RejectedSends <= MaxRejectedSendRetries)
                {
                    ScheduleSendRetry(c, message);
                }
                else
                {
                    r.DeferredSend = null;
                    Toast("這個對話正在以新版 CLI 重新啟動，訊息已放回佇列；請稍候再按送出", isError: true);
                }
            }
        }
        SaveConversation(c, saveItems: true);
        UpdateRunningCounts();
        PostComposer(c);
        MarkHeaderDirty(c);
        MarkListDirty();
    }

    private void ScheduleSendRetry(ConversationState c, QueuedMessage message)
    {
        var id = c.Meta.Id;
        _ = Task.Delay(RejectedSendRetryDelay).ContinueWith(_ => host.BeginInvoke(() =>
        {
            if (!disposed && conversations.TryGetValue(id, out var live) && ReferenceEquals(live, c) && ReferenceEquals(c.Restart.DeferredSend, message))
                ResumeSendAfterRestart(c);
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// 開始送出（SendCoreAsync）：換版後留在標頭的結果清掉；排定的換版交給這次送出（設定帶目前的身分，會話自己以新版重開程序）——
    /// 背景工作還在跑時例外：這一輪先沿用舊程序，等背景工作結束後再換
    /// </summary>
    private void OnSendStartingForRestart(ConversationState c)
    {
        var r = c.Restart;
        if (r.DeferredSend != null && !c.Queue.Contains(r.DeferredSend))
            r.DeferredSend = null;
        if (r.Result != null)
        {
            r.Result = null;
            MarkHeaderDirty(c);
        }
        if (r.Plan is { InFlight: false, Held: false, Drain: false, ResumeHeld: false } && !KeepsOldProcess(c))
            DropPlan(c);
    }

    /// <summary>
    /// 這次送出要沿用還在跑舊版的程序：有存活的背景工作（撐不過程序重開）。不看有沒有排定的換版——「下次送出」政策不排定，
    /// 以前因此在送出時重開程序、把背景工作（dev server、長時間的測試）一起殺掉；背景工作結束後的第一次送出才換新版
    /// </summary>
    private bool KeepsOldProcess(ConversationState c)
        => c.Session is { HasLiveBackgroundWork: true } && c.Restart.InstanceIdentity != null && IsOnOldVersion(c);

    /// <summary>送出時要用的 CLI 身分（AgentSessionSettings.Cli）：目前安裝的；背景工作還在跑、還在跑舊版的對話沿用程序的身分（不重開）</summary>
    private CliBinaryIdentity? SessionCliIdentity(ConversationState c, AgentProvider provider)
        => KeepsOldProcess(c) ? c.Restart.InstanceIdentity : cli.GetIdentity(provider);

    // ───────────────────────────── 網頁指令 ─────────────────────────────

    /// <summary>
    /// 「立即套用 CLI 更新」：指定對話（標頭的按鈕）或這種 CLI 所有待重開、還在跑舊版的對話（CLI 卡）；屏障排空中＝「現在中斷」。沒有可套用的就忽略。
    /// app 更新的屏障不受這兩個按鈕影響（網頁舊畫面的短暫空檔按下）：app 更新由橫幅控制、不給逐一立即套用，
    /// 不指定對話的不能把所有對話改成立即停下，指定對話的也不能讓那個對話不等安全點就停下（設計 9.2：工具在跑要等到安全點）
    /// </summary>
    private void ApplyCliUpdateNow(ConversationState? c, AgentProvider? provider)
    {
        if (c != null)
        {
            ApplyNowFor(c);
            return;
        }
        if (provider is not { } p)
            return;
        if (IsAppUpdateBarrier(p))
        {
            Log.Info($"app 更新排空中，忽略 {p} CLI 的立即套用");
            return;
        }
        if (barriers.TryGetValue(p, out var barrier))
        {
            if (!barrier.Draining)
                return;
            barrier.Urgency = CliUpdateUrgency.Immediate;
            foreach (var conversation in ConversationsOf(p).Where(x => x.Restart.Plan is { Drain: true, Held: false, Drained: false }).OrderByDescending(x => x.Running).ToList())
            {
                var plan = conversation.Restart.Plan!;
                plan.ApplyNow = true;
                plan.Deferred = false;
                EvaluateRestart(conversation);
            }
            MarkCliDirty();
            return;
        }
        foreach (var conversation in ConversationsOf(p).Where(x => x.Restart.Plan != null || IsOnOldVersion(x)).OrderByDescending(x => x.Running).ToList())
            ApplyNowFor(conversation);
    }

    private void ApplyNowFor(ConversationState c)
    {
        if (c.IsDraft || c.Session == null)
            return;
        var provider = c.Meta.Provider;
        if (IsAppUpdateBarrier(provider))
        {
            Log.Info($"app 更新排空中，忽略 {provider} 對話的立即套用");
            return;
        }
        var plan = c.Restart.Plan;
        if (plan == null)
        {
            if (barriers.ContainsKey(provider) || !IsOnOldVersion(c) || cli.GetIdentity(provider) is not { } current)
                return;
            plan = c.Restart.Plan = new RestartPlan(CurrentWave(provider, current.Version).Id, RestartClock())
            {
                Target = current,
                FromVersion = CurrentVersionOf(c),
            };
        }
        if (plan.InFlight || plan.Held || plan.Drained || plan.ResumeHeld)
            return;
        plan.ApplyNow = true;
        plan.Deferred = false;
        EvaluateRestart(c);
        MarkCliDirty();
    }

    /// <summary>「繼續」：換版後沒有自動接續的對話，送出接續訊息（不顯示成使用者訊息，改加一則 Notice）</summary>
    private void ContinueAfterCliUpdate(IAgentView view, ConversationState? c)
    {
        if (c == null || c.IsDraft || c.Running || c.Restart.Result is not { State: CliRestartState.NotContinued, CanContinue: true } result)
            return;
        var provider = c.Meta.Provider;
        if (IsCliUpdateBlocking(provider))
        {
            Toast($"{UpdatingSubject(provider)}，請等更新完成後再按「繼續」", view: view);
            return;
        }
        var context = (result.Continuation ?? new ContinuationContext(provider, result.From, result.To, Array.Empty<AgentInterruptedTool>(), Array.Empty<string>(), false))
            with { UserRequested = true };
        // 標頭的結果由送出清掉（OnSendStartingForRestart）；換版還在進行、送出被拒時「繼續」照樣留著。
        // app 更新後沒有接續的（resume.json 重建的結果帶 AppTo）：提示說明是 app 更新，不是 CLI；
        // app 重新啟動後沒有接續的（running.json 重建的結果帶 AppRestart）說明是重新啟動
        var notice = context.AppRestart != null ? InterruptedTurnRules.ManualContinueNotice
            : context.AppTo != null ? ResumeIntentRules.ManualContinueNotice
            : CliRestartRules.ManualContinueNotice;
        bool app = context.AppRestart != null || context.AppTo != null;
        _ = SendCoreAsync(c, ContinuationRules.Build(context), new List<AgentAttachment>(), notice,
            hiddenNoticeKind: app ? AgentNoticeKind.AppUpdate : AgentNoticeKind.CliUpdate);
    }

    /// <summary>
    /// 「取消更新」：屏障排空中才有效（換檔已開始就忽略——npm 半途不能中斷）；已停下的對話等 UpdateFinished 以目前的版本重開接續。
    /// app 更新的屏障由橫幅取消（AbortAppUpdate），CLI 卡的按鈕不能以「已取消更新」結束它
    /// </summary>
    private void CancelCliUpdate(AgentProvider provider)
    {
        if (IsAppUpdateBarrier(provider))
        {
            Log.Info($"app 更新排空中，忽略 {provider} CLI 的取消更新");
            return;
        }
        if (!barriers.TryGetValue(provider, out var barrier) || !barrier.Draining)
            return;
        var (held, busy) = CountBarrier(provider);
        barrier.Done.TrySetResult(new CliUpdatePreparation(Ready: false, barrier.ClosedIdle, held, busy, "已取消更新"));
        StopDraining(barrier);
        Log.Info($"使用者取消 {provider} CLI 更新");
    }

    // ───────────────────────────── 更新屏障（ICliUpdateParticipant）─────────────────────────────

    /// <summary>
    /// CLI 更新前的屏障（CliManager 在背景執行緒呼叫）：依緊急程度排空這種 CLI 的對話程序——
    /// Background＝只在全部閒置時關掉（有執行中、等你回應或有背景工作的就什麼都不停，回 Ready=false）；
    /// Normal＝閒置的立刻關，執行中的到安全點停下（等待 CLI 更新），等你回應的等回應後的安全點，超過 DrainGrace 在 CLI 卡問使用者；
    /// Immediate＝不等安全點。全部停下才回 Ready=true。從呼叫起到 UpdateFinished，送出排進佇列
    /// </summary>
    Task<CliUpdatePreparation> ICliUpdateParticipant.PrepareForUpdateAsync(AgentProvider provider, CliUpdateUrgency urgency, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<CliUpdatePreparation>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeginInvoke(() =>
        {
            try
            {
                if (IsAppUpdateBarrier(provider))
                {
                    // app 正在更新：CLI 晚點再更新（CliManager 會照 Ready=false 延後；之後的 UpdateFinished 不能結束 app 的屏障）
                    done.TrySetResult(new CliUpdatePreparation(Ready: false, ClosedIdle: 0, Held: 0, Busy: 0, "Clode Myriagent 正在更新"));
                    return;
                }
                BeginUpdateBarrier(provider, urgency, cancellationToken, done);
            }
            catch (Exception ex)
            {
                Log.Error("CLI 更新前排空對話失敗", ex);
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    /// <summary>CLI 更新結束（CliManager 在背景執行緒呼叫）：解除屏障，停下的對話以新身分重開並接續，延後的送出照常處理</summary>
    void ICliUpdateParticipant.UpdateFinished(CliUpdateCompletion completion)
        => host.BeginInvoke(() =>
        {
            try
            {
                if (IsAppUpdateBarrier(completion.Provider))
                    return;
                FinishUpdateBarrier(completion);
            }
            catch (Exception ex)
            {
                Log.Error("CLI 更新結束後接續對話失敗", ex);
            }
        });

    /// <param name="appUpdate">app 自我更新的屏障（PrepareForAppUpdateAsync）：政策表照 app 更新、標頭顯示 appUpdate、CLI 卡不顯示</param>
    private void BeginUpdateBarrier(AgentProvider provider, CliUpdateUrgency urgency, CancellationToken cancellationToken, TaskCompletionSource<CliUpdatePreparation> done,
        bool appUpdate = false)
    {
        if (disposed)
        {
            done.TrySetResult(new CliUpdatePreparation(Ready: true, ClosedIdle: 0, Held: 0, Busy: 0));
            return;
        }
        if (cancellationToken.IsCancellationRequested)
        {
            done.TrySetCanceled(cancellationToken);
            return;
        }
        if (barriers.Remove(provider, out var previous))
        {
            // 上一次沒有收到 UpdateFinished（不應該發生）：作廢
            previous.Registration.Dispose();
            previous.Done.TrySetCanceled();
        }

        var now = RestartClock();
        var wave = StartWave(provider, toVersion: null);
        wave.AppUpdate = appUpdate;
        var barrier = new UpdateBarrier(provider, urgency, now, wave.Id, done) { AppUpdate = appUpdate };
        barriers[provider] = barrier;
        barrier.Registration = cancellationToken.Register(() => host.BeginInvoke(() =>
        {
            if (barriers.TryGetValue(provider, out var current) && ReferenceEquals(current, barrier) && barrier.Draining)
            {
                done.TrySetCanceled(cancellationToken);
                StopDraining(barrier);
            }
        }));
        MarkCliDirty();
        Log.Info($"{provider} {(appUpdate ? "app" : "CLI")} 更新前排空對話（{urgency}）");
        JournalApp("barrier.begin", new JObject { ["provider"] = provider.ToString(), ["appUpdate"] = appUpdate, ["urgency"] = urgency.ToString() });

        var mine = ConversationsOf(provider).Where(c => c.Session != null).OrderByDescending(c => c.Running).ToList();
        if (urgency == CliUpdateUrgency.Background)
        {
            // 自動更新不打擾：有任何對話在執行、等你回應或有背景工作，就什麼都不停
            int busy = mine.Count(c => c.Restart.Plan is { InFlight: true } || SituationOf(c) is not (CliRestartSituation.NoProcess or CliRestartSituation.Idle));
            if (busy > 0)
            {
                done.TrySetResult(new CliUpdatePreparation(Ready: false, ClosedIdle: 0, Held: 0, Busy: busy));
                return;
            }
        }

        foreach (var c in mine)
        {
            var plan = c.Restart.Plan ??= new RestartPlan(wave.Id, now);
            plan.Wave = wave.Id;
            plan.Drain = true;
            plan.Drained = false;
            plan.Deferred = false;
            plan.FinishedUpdate = null;   // 上一次屏障的結果作廢：停下的等這一次的 UpdateFinished
            plan.Since = now;
            plan.ApplyNow |= urgency == CliUpdateUrgency.Immediate;
            plan.FromVersion ??= CurrentVersionOf(c);
            plan.AppUpdate = appUpdate;
            plan.AppVersion = appUpdate ? AppVersionInfo.VersionText : null;
            plan.RemoteControl = false;   // 這一次排空關掉或停下時才記
            EvaluateRestart(c);
        }
        CheckBarrier(provider);
    }

    /// <summary>屏障裡已停下的對話數、還留著程序的對話數</summary>
    private (int Held, int Busy) CountBarrier(AgentProvider provider)
    {
        int held = 0, busy = 0;
        foreach (var c in ConversationsOf(provider).Where(c => c.Session != null))
        {
            var plan = c.Restart.Plan;
            if (plan is { Held: true })
                held++;
            else if (plan is { InFlight: true } || (plan is not { Drained: true } && LiveProcess(c)))
                busy++;
        }
        return (held, busy);
    }

    /// <summary>屏障排空中：這種 CLI 的對話程序都已關掉或停下就回 Ready=true</summary>
    private void CheckBarrier(AgentProvider provider)
    {
        if (!barriers.TryGetValue(provider, out var barrier) || !barrier.Draining)
            return;
        if (barrier.AppUpdate)
            AppDrainProgress?.Invoke();
        var (held, busy) = CountBarrier(provider);
        if (busy > 0)
            return;
        barrier.Done.TrySetResult(new CliUpdatePreparation(Ready: true, barrier.ClosedIdle, held, Busy: 0));
        MarkCliDirty();
        Log.Info($"{provider} {(barrier.AppUpdate ? "app" : "CLI")} 更新前的對話都已停下（關閉 {barrier.ClosedIdle}、停下 {held}）");
        JournalApp("barrier.ready", new JObject
        {
            ["provider"] = provider.ToString(),
            ["appUpdate"] = barrier.AppUpdate,
            ["closedIdle"] = barrier.ClosedIdle,
            ["held"] = held,
            ["sec"] = BackgroundJournalRules.Seconds(RestartClock() - barrier.StartedAt),
        });
    }

    /// <summary>停止排空（取消更新、CliManager 放棄等待）：還沒停下的對話不再停（舊程序照常跑），已停下的保持停下等 UpdateFinished</summary>
    private void StopDraining(UpdateBarrier barrier)
    {
        foreach (var c in ConversationsOf(barrier.Provider))
        {
            if (c.Restart.Plan is not { Drain: true, Held: false, InFlight: false } plan)
                continue;
            plan.Drain = false;
            plan.AppUpdate = false;
            plan.AppVersion = null;
            plan.ApplyNow = false;
            plan.Drained = false;
            plan.Waiting = null;
            MarkHeaderDirty(c);
        }
        MarkCliDirty();
    }

    private void FinishUpdateBarrier(CliUpdateCompletion completion)
    {
        if (disposed)
            return;
        var provider = completion.Provider;
        if (barriers.Remove(provider, out var barrier))
        {
            barrier.Registration.Dispose();
            barrier.Done.TrySetCanceled();
        }
        var identity = completion.Identity;
        RestartWave? wave = barrier != null && waves.TryGetValue(provider, out var w) && w.Id == barrier.Wave ? w : null;
        if (wave != null)
        {
            wave.ToVersion = identity?.Version;
            wave.Silent = completion.Outcome != CliUpdateOutcome.Updated;
        }
        Log.Info($"{provider} CLI 更新結束（{completion.Outcome}）：{identity?.Version ?? "沒有可用的版本"}{(completion.Error == null ? string.Empty : "，" + completion.Error)}");
        JournalApp("barrier.end", new JObject
        {
            ["provider"] = provider.ToString(),
            ["appUpdate"] = barrier?.AppUpdate,
            ["outcome"] = completion.Outcome.ToString(),
            ["version"] = identity?.Version,
            ["error"] = BackgroundJournalRules.Clip(completion.Error),
            ["sec"] = barrier != null ? BackgroundJournalRules.Seconds(RestartClock() - barrier.StartedAt) : null,
        });

        foreach (var c in ConversationsOf(provider).OrderByDescending(c => c.Running).ToList())
        {
            if (c.Restart.Plan is not { Drain: true } plan)
                continue;
            plan.Drain = false;
            plan.AppUpdate = false;
            // 已停下的（或停下還在進行的）保留 AppVersion：放棄 app 更新時就地接續的訊息要用
            if (!plan.Held && !plan.InFlight)
                plan.AppVersion = null;
            plan.ApplyNow = false;
            plan.Drained = false;
            plan.Waiting = null;
            if (plan is { InFlight: true, StopOnly: true })
            {
                // 停下還在進行（例如停下途中按了「取消更新」，UpdateFinished 先到）：停下有了結果時照這次的結果接續或收尾（HandleRestartResult）
                plan.FinishedUpdate = completion;
                continue;
            }
            if (!plan.Held)
                continue;
            ResumeHeldAfterUpdate(c, plan, completion);
        }
        MarkCliDirty();
        ResumeDeferredSends(provider);
        // 排空停掉了背景工作的閒置對話：更新結束了，馬上請代理確認背景工作
        foreach (var c in ConversationsOf(provider).Where(c => c.PendingBackgroundCheck != null).ToList())
            TrySendBackgroundCheck(c);
        // 排空被取消、還在跑舊版的對話依一般換版處理；本來就沒有程序或已關掉的移除
        if (identity != null)
            EnrollOldVersion(provider, identity, change: null);
        foreach (var c in ConversationsOf(provider).Where(c => c.Restart.Plan is { InFlight: false, Held: false, ResumeHeld: false, Drain: false } && !IsOnOldVersion(c)).ToList())
            DropPlan(c);
        CheckWave(provider);
    }

    /// <summary>
    /// 屏障停下的那一輪在更新結束後：有可用的版本（沒換版、取消、失敗也是目前仍可用的版本）就以它重開並接續；
    /// 更新後沒有可用的版本（不明、未安裝、損壞）就不重開，那一輪以提示收尾、佇列暫停
    /// </summary>
    private void ResumeHeldAfterUpdate(ConversationState c, RestartPlan plan, CliUpdateCompletion completion)
    {
        var provider = c.Meta.Provider;
        plan.Held = false;
        if (completion.Identity is not { } identity)
        {
            var error = completion.Error ?? cli.Get(provider).InstallBrokenReason;
            WrapUpInterruptedTurn(c, CliRestartRules.UpdateUnavailableNotice(provider, error, c.Queue.Count > 0), cliNotice: true,
                FailedView(c, plan, null, error ?? $"{CliRestartRules.ProductName(provider)} 更新後沒有可用的版本"));
            RecordOutcome(provider, plan, WaveOutcome.Failed);
            c.Restart.Plan = null;
            MarkHeaderDirty(c);
            MarkCliDirty();
            return;
        }
        plan.Target = identity;
        plan.ResumeHeld = true;
        EvaluateRestart(c);
    }

    /// <summary>app 結束：還在等的屏障結束（CliManager 不會永遠等下去）</summary>
    private void CancelUpdateBarriers()
    {
        foreach (var barrier in barriers.Values)
        {
            barrier.Registration.Dispose();
            barrier.Done.TrySetCanceled();
        }
        barriers.Clear();
    }

    // ───────────────────────────── 一波換版的彙總 ─────────────────────────────

    private RestartWave StartWave(AgentProvider provider, string? toVersion)
    {
        var wave = new RestartWave(++lastWaveId, toVersion);
        waves[provider] = wave;
        MarkCliDirty();
        return wave;
    }

    /// <summary>這一波還沒處理完就沿用（晚到的對話算同一波），否則開新的一波</summary>
    private RestartWave CurrentWave(AgentProvider provider, string? toVersion)
        => waves.TryGetValue(provider, out var wave) && !wave.Summarized ? wave : StartWave(provider, toVersion);

    private void RecordOutcome(AgentProvider provider, RestartPlan plan, WaveOutcome outcome)
    {
        if (!waves.TryGetValue(provider, out var wave) || wave.Id != plan.Wave)
            return;
        switch (outcome)
        {
            case WaveOutcome.Continued:
                wave.Continued++;
                break;
            case WaveOutcome.Restarted:
                wave.Restarted++;
                break;
            case WaveOutcome.NotContinued:
                wave.NotContinued++;
                break;
            default:
                wave.Failed++;
                break;
        }
        MarkCliDirty();
    }

    /// <summary>這一波的對話都處理完：送一則系統匣彙總（不逐一跳；通知關掉、沒有處理任何對話、屏障沒換版時不送）</summary>
    private void CheckWave(AgentProvider provider)
    {
        if (!waves.TryGetValue(provider, out var wave) || wave.Summarized || barriers.ContainsKey(provider))
            return;
        if (conversations.Values.Any(c => c.Restart.Plan is { } plan && plan.Wave == wave.Id))
            return;
        wave.Summarized = true;
        if (wave.Total == 0 || wave.Silent || !settings.Notifications)
            return;
        host.Notify(CliRestartRules.WaveSummaryTitle(provider, wave.ToVersion),
            CliRestartRules.WaveSummaryMessage(wave.Continued, wave.Restarted, wave.NotContinued, wave.Failed), conversationId: null);
    }

    // ───────────────────────────── 維護 ─────────────────────────────

    /// <summary>維護計時器（每 30 秒）：漏掉的事件補檢查（含背景工作等滿上限，EvaluateRestart）、還沒送出的背景工作確認、排空寬限</summary>
    private void TickRestarts()
    {
        foreach (var c in conversations.Values.Where(c => c.PendingAccountSwitch is { AtTurnEnd: true }).ToList())
            EvaluatePendingAccountSwitch(c);
        foreach (var c in conversations.Values.Where(c => c.PendingBackgroundCheck != null).ToList())
            TrySendBackgroundCheck(c);
        if (barriers.Count == 0 && !conversations.Values.Any(c => c.Restart.Plan != null))
            return;
        var now = RestartClock();
        foreach (var c in conversations.Values.Where(c => c.Restart.Plan != null).ToList())
            RecheckRestart(c);
        foreach (var barrier in barriers.Values.ToList())
        {
            if (barrier.AppUpdate)
            {
                ApplyNowForAppOverdue(barrier, now);
                continue;
            }
            if (barrier.Draining && !barrier.Overdue && now - barrier.StartedAt >= CliRestartRules.DrainGrace)
            {
                barrier.Overdue = true;
                Log.Info($"{barrier.Provider} CLI 更新排空超過 {CliRestartRules.DrainGrace.TotalMinutes:0} 分鐘");
                MarkCliDirty();
            }
        }
        foreach (var provider in AgentProviders.All)
            AfterRestartStep(provider);
    }

    /// <summary>
    /// 對話被刪除、換帳戶、帳戶被移除後：關掉了會話的那些對話（released：換了帳戶的、屬於被移除帳戶的）清掉換版狀態，屏障與這一波重新檢查。
    /// 只清這些，不清所有沒有會話的對話：app 更新後沒有接續的對話不開程序，標頭的「繼續」就靠它的 Restart.Result
    /// （resume.json 已刪；清掉時 continue.json 也跟著刪，AgentHub.ContinueOffers.cs），刪掉別的對話不能讓它默默失效
    /// </summary>
    private void RefreshRestarts(IEnumerable<ConversationState> released)
    {
        foreach (var c in released)
        {
            bool had = c.Restart.Plan != null || c.Restart.Result != null;
            c.Restart.ResetProcess();
            c.Restart.Plan = null;
            c.Restart.Result = null;
            if (had && IsLive(c))
                MarkHeaderDirty(c);
        }
        foreach (var provider in AgentProviders.All)
            AfterRestartStep(provider);
    }

    // ───────────────────────────── 渲染 ─────────────────────────────

    /// <summary>測試：這種 CLI 目前的 CliView（含〔熱更新〕欄位；UI 執行緒）</summary>
    internal JObject RenderCliViewForTest(AgentProvider provider) => (JObject)RenderCli()[provider.ToString()]!;

    /// <summary>測試：對話標頭的 restart 欄位（UI 執行緒）</summary>
    internal JToken RenderHeaderRestartForTest(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) ? RenderHeaderRestart(c) : JValue.CreateNull();

    /// <summary>標頭的狀態：排定的換版、處理完的結果、等 CLI 更新的送出、或「下次送出時才用新版」</summary>
    private static CliRestartState? PlanState(RestartPlan plan)
    {
        var stopped = StoppedState(plan);
        if (plan.Held)
            return stopped;
        if (plan.InFlight)
            return plan.Restarting ? plan.ContinuationConfirmed ? CliRestartState.Continued : plan.StopOnly ? stopped : CliRestartState.Restarting : null;
        if (plan.Drained)
            return null;
        return plan.Waiting;
    }

    /// <summary>屏障停下（或排隊等停下）的對話在等什麼：app 更新是 appUpdate，CLI 更新是 waitingUpdate</summary>
    private static CliRestartState StoppedState(RestartPlan plan) => plan.AppUpdate ? CliRestartState.AppUpdate : CliRestartState.WaitingUpdate;

    private HeaderRestartInfo? HeaderRestartView(ConversationState c)
    {
        var r = c.Restart;
        var provider = c.Meta.Provider;
        var current = cli.GetIdentity(provider);
        if (r.Plan is { } plan && PlanState(plan) is { } state)
        {
            // app 更新的狀態不帶版本：標頭的「舊版 → 新版」講的是 CLI，app 更新時 CLI 沒有換版
            var to = plan.Drain || plan.AppUpdate ? null : plan.Request?.ToVersion ?? plan.Target?.Version ?? current?.Version;
            var from = plan.AppUpdate ? null : plan.FromVersion;
            var subject = state is CliRestartState.WaitingTool or CliRestartState.WaitingUser ? plan.WaitingSubject : null;
            var detail = plan.Drain && state is not (CliRestartState.WaitingUpdate or CliRestartState.Restarting or CliRestartState.AppUpdate)
                ? plan.AppUpdate ? CliRestartRules.DescribeAppDrainWaiting(state, subject) : DescribeDrainWaiting(state, provider, subject)
                : CliRestartRules.DescribeState(state, provider, to, subject);
            // app 更新由橫幅控制（立即重啟、取消這次），標頭不給逐一立即套用
            bool canApplyNow = !plan.AppUpdate && !plan.InFlight && !plan.Held
                && (plan.Drain ? state is not CliRestartState.WaitingUpdate and not CliRestartState.Restarting : CliRestartRules.CanApplyNow(state));
            return new HeaderRestartInfo(state, from, to, detail, canApplyNow, CanContinue: false, CliRestartRules.ShowsWaitTime(state) ? plan.Since : null);
        }
        if (r.Result is { } result)
            return new HeaderRestartInfo(result.State, result.From, result.To, result.Detail, CanApplyNow: false, CanContinue: result.CanContinue && !c.Running);
        if (r.DeferredSend != null && IsCliUpdateBlocking(provider))
        {
            // 延後的送出在等誰：app 更新排空中是 app，否則是這種 CLI
            var waitingFor = IsAppUpdateBarrier(provider) ? CliRestartState.AppUpdate : CliRestartState.WaitingUpdate;
            return new HeaderRestartInfo(waitingFor, null, null, CliRestartRules.DescribeState(waitingFor, provider, null), false, false);
        }
        if (settings.CliRestartPolicy == CliRestartPolicy.NextSend && IsOnOldVersion(c))
            return new HeaderRestartInfo(CliRestartState.NextSend, CurrentVersionOf(c), current?.Version,
                CliRestartRules.DescribeState(CliRestartState.NextSend, provider, current?.Version), false, false);
        return null;
    }

    /// <summary>屏障排空中還在等的對話（版本還沒換）：「等目前的指令（npm test）完成後停下，讓 Codex 更新」</summary>
    private static string DescribeDrainWaiting(CliRestartState state, AgentProvider provider, string? subject)
        => CliRestartRules.DescribeDrainWaiting(state, CliRestartRules.ProductName(provider), subject);

    /// <summary>Header.restart（docs/web-protocol.md 的 HeaderRestart）；沒有換版要處理為 null</summary>
    private JToken RenderHeaderRestart(ConversationState c)
    {
        if (HeaderRestartView(c) is not { } info)
            return JValue.CreateNull();
        // 版本沒變（CLI 更新取消、失敗或沒換版，放棄 app 更新後就地接續）：不送「2.1.310 → 2.1.310」，網頁就不顯示版本晶片
        bool sameVersion = CliRestartRules.SameVersion(info.From, info.To);
        return new JObject
        {
            ["state"] = CliRestartRules.ToProtocol(info.State),
            ["from"] = sameVersion ? null : info.From,
            ["to"] = sameVersion ? null : info.To,
            ["detail"] = info.Detail,
            ["canApplyNow"] = info.CanApplyNow,
            ["canContinue"] = info.CanContinue,
            ["since"] = ToUnixMs(info.Since),
        };
    }

    /// <summary>CliView 的〔熱更新〕欄位：安裝狀態取自 CliState，換版彙總與屏障進度取自重開協調</summary>
    private void AddCliRestartFields(JObject view, AgentProvider provider, CliState state)
    {
        view["changing"] = state.InstallChanging || state.ReplacingFiles;
        view["installBroken"] = state.InstallBrokenReason;
        view["lastChange"] = state.LastChange is { } change
            ? new JObject
            {
                ["from"] = change.FromVersion,
                ["to"] = change.ToVersion,
                ["origin"] = CliRestartRules.ToProtocol(change.Origin),
                ["at"] = ToUnixMs(change.DetectedAtUtc),
            }
            : null;
        view["update"] = RenderUpdateProgress(provider, state);
        view["restartPolicy"] = CliRestartRules.ToProtocol(settings.CliRestartPolicy);

        var mine = ConversationsOf(provider).ToList();
        view["runningOnOldVersion"] = mine.Count(IsOnOldVersion);
        var waiting = new JArray();
        foreach (var c in mine)
        {
            // app 更新的排空不列在 CLI 卡（由 app 的更新橫幅顯示）
            if (c.Restart.Plan is not { AppUpdate: false } plan || PlanState(plan) is not { } planState || !CliRestartRules.IsPending(planState))
                continue;
            waiting.Add(new JObject
            {
                ["conversationId"] = c.Meta.Id,
                ["state"] = CliRestartRules.ToProtocol(planState),
                ["detail"] = planState is CliRestartState.WaitingTool or CliRestartState.WaitingUser ? plan.WaitingSubject : null,
            });
        }
        view["restartPending"] = waiting.Count;
        view["restartWaiting"] = waiting;
        view["restartDone"] = waves.TryGetValue(provider, out var wave) && !wave.AppUpdate
            ? new JObject
            {
                ["continued"] = wave.Continued,
                ["restarted"] = wave.Restarted,
                ["notContinued"] = wave.NotContinued,
                ["failed"] = wave.Failed,
            }
            : null;
    }

    /// <summary>CliView.update：app 觸發的更新進度（npm 屏障）；沒有在更新為 null</summary>
    private JObject? RenderUpdateProgress(AgentProvider provider, CliState state)
    {
        barriers.TryGetValue(provider, out var barrier);
        // app 更新的屏障不是 CLI 的更新：CLI 卡不顯示它的排空
        if (barrier is { AppUpdate: true })
            barrier = null;
        if (state.UpdatePhase == CliUpdatePhase.None && barrier == null)
            return null;
        // CliManager 還沒標出階段（屏障剛開始、或 Prepare 已回覆而換檔還沒開始）：依屏障是否還在排空
        var phase = CliRestartRules.ToProtocol(state.UpdatePhase) ?? (barrier is { Draining: true } ? "draining" : "installing");
        int held = 0, waiting = 0;
        foreach (var c in ConversationsOf(provider))
        {
            if (c.Restart.Plan is { AppUpdate: true })
                continue;
            if (c.Restart.Plan is { Held: true })
                held++;
            else if (c.Restart.Plan is { Drain: true, Drained: false })
                waiting++;
        }
        return new JObject
        {
            ["phase"] = phase,
            ["held"] = held,
            ["waiting"] = waiting,
            ["overdue"] = barrier is { Overdue: true, Draining: true } && waiting > 0,
            ["startedAt"] = ToUnixMs(state.UpdateStartedAt ?? barrier?.StartedAt),
        };
    }
}
