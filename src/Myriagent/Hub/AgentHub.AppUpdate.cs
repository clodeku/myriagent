using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>app 更新排空的結果（全部代理種類合計）</summary>
/// <param name="Closed">關掉的閒置程序數</param>
/// <param name="Held">停下、等新版接續的對話數</param>
/// <param name="Busy">還沒停下的對話數（Ready=false 時）</param>
/// <param name="Reason">沒有就緒的原因（CLI 正在更新、放棄的原因）</param>
internal sealed record AppUpdatePreparation(bool Ready, int Closed, int Held, int Busy, string? Reason = null);

/// <summary>啟動時交給中樞的接續工作（AppController 讀 resume.json 後、主視窗就緒前設定）</summary>
/// <param name="AppTo">現在執行的 app 版本（提示文字用）</param>
/// <param name="RolledBack">新版啟動失敗、由退回的舊版接續（--after-update-failed）</param>
internal sealed record PendingAppResume(AppResumeFile File, ResumeValidity Validity, string? AppTo, bool RolledBack);

/// <summary>接續的結果（系統匣通知用）</summary>
/// <param name="Continued">真的送出了接續訊息的對話數</param>
/// <param name="NotContinued">沒有自動接續的對話數（加了提示、標頭給「繼續」的；等分批期間使用者已經自己送出或刪掉的也算）</param>
/// <param name="DeferredSent">排空時被延後、這時送出的訊息數</param>
internal sealed record AppResumeSummary(int Continued, int NotContinued, int DeferredSent, bool RolledBack, string? AppTo);

/// <summary>網頁的 app 更新指令（AppUpdateCoordinator 實作；中樞只轉送）</summary>
internal interface IAppUpdateCommands
{
    void Check();

    void RestartNow();

    /// <summary>「強制更新」：不等背景工作開始排空（排空中按就改成強制），滿 10 分鐘還沒停下的照立即套用停下</summary>
    void ForceRestart();

    void CancelRestart();

    void InstallHere();
}

/// <summary>
/// app 自我更新（設計 9.2～9.5）：排空重用 CLI 更新屏障（AgentHub.Restart.cs），每種代理一個標成 AppUpdate 的屏障；
/// 與 CLI 更新的差別只在政策表（等你回應視為安全點）、背景工作寬限 10 分鐘後立即套用、標頭狀態 appUpdate，以及停下後不在這個程序接續
/// </summary>
internal sealed partial class AgentHub
{
    /// <summary>
    /// 參與 app 更新排空的代理種類：全部（app 結束時每一種的程序都會被關掉，支援 RestartAsync 的代理停下後寫進 resume.json 由新版自動接續）
    /// </summary>
    internal static IReadOnlyList<AgentProvider> AppUpdateProviders => AgentProviders.All;

    /// <summary>app 更新排空中（PrepareForAppUpdateAsync 之後、AbortAppUpdate 或 app 結束之前）</summary>
    internal bool AppUpdateDraining => barriers.Values.Any(b => b.AppUpdate);

    /// <summary>app 更新排空中每處理一步（又關掉或停下一個對話）：協調器據此重寫 resume.json（UI 執行緒）</summary>
    internal event Action? AppDrainProgress;

    /// <summary>app 更新排空中還沒停下的對話數（橫幅的「等 2 個對話到安全點」）</summary>
    internal int AppUpdateWaitingCount => AppUpdateProviders.Sum(p => barriers.TryGetValue(p, out var b) && b.AppUpdate ? CountBarrier(p).Busy : 0);

    /// <summary>
    /// 這台有對話的背景工作還在跑（同排空看的 SituationOf：程序還在、會話回報 HasLiveBackgroundWork——CLI 追蹤的 run_in_background、
    /// Codex 讓出的指令…）：app 重啟會停掉它們。協調器據此不開始 app 更新、排空中出現就放棄這次（AppRestartBlocker.BackgroundWork，
    /// 2026-10-28 使用者要求桌面版換版一直等到背景工作都完成）。對話另開的背景程式（AgentHub.BackgroundPrograms）不算：它們和 CLI 脫鉤，app 重啟不影響
    /// </summary>
    internal bool BackgroundWorkRunning
        => conversations.Values.Any(c => !c.IsReplica && c.Session is { HasLiveBackgroundWork: true } && LiveProcess(c) && IsLive(c));

    /// <summary>
    /// 有沒有開著 Claude Remote Control 的對話可能正從 claude.ai 或 Claude App 在用（設計 9.1 條件 1；AppUpdateRestartRules.IsRemoteInUse）：
    /// idleThreshold 內有動靜，或在等回應、有工具在執行（同 SituationOf 的判斷）而且 waitThreshold 內有動靜。
    /// 桌面沒人正是手機最可能在用的時候，協調器這時不自動開始排空、橫幅說明原因（UI 執行緒）
    /// </summary>
    internal bool RemoteControlInUse(DateTime now, TimeSpan idleThreshold, TimeSpan waitThreshold)
        => conversations.Values.Any(c => !c.IsDraft && c.Session is { HasRemoteControl: true }
                                         && AppUpdateRestartRules.IsRemoteInUse(remoteControl: true, AwaitsUser(c),
                                             toolRunning: (c.Restart.SafePoint?.ToolRunning ?? false) || ForegroundRunningTools(c).Any(),
                                             c.LastActivity, now, idleThreshold, waitThreshold));

    private JObject? appUpdateState;

    /// <summary>
    /// 使用者按了「強制更新」的時間（ForceAppUpdate；排空開始或放棄時清掉）：滿 AppUpdateRestartRules.ForceStopAfter，
    /// 還沒停下的對話一律照「立即套用」停下（ApplyNowForAppOverdue）
    /// </summary>
    private DateTime? appUpdateForcedAt;

    /// <summary>強制更新的排空中，還沒停下的對話會在這個時間被照「立即套用」停下（沒有強制時 null；網頁顯示幾點起強制停下）</summary>
    internal DateTime? AppUpdateForceAt => appUpdateForcedAt is { } at && AppUpdateDraining ? at + AppUpdateRestartRules.ForceStopAfter : null;

    /// <summary>
    /// 「強制更新」（2026-10-28 使用者要求）：這次排空不等背景工作結束——對話照常陸續到安全點停下，按下後滿
    /// AppUpdateRestartRules.ForceStopAfter（10 分鐘）還沒停下的（在等背景工作、前景的指令、你的回應）一律照「立即套用」停下，
    /// 停掉的背景工作記進 running.json，新版馬上請代理確認。排空中才呼叫（協調器在開始排空之後、或排空中按下時）；重複呼叫不重設時間
    /// </summary>
    internal void ForceAppUpdate()
    {
        if (!AppUpdateDraining || appUpdateForcedAt != null)
            return;
        appUpdateForcedAt = RestartClock();
        Log.Info($"強制更新：{AppUpdateRestartRules.ForceStopAfter.TotalMinutes:0} 分鐘後還沒停下的對話照立即套用停下");
        JournalApp("update.force", new JObject { ["stopAfterSec"] = AppUpdateRestartRules.ForceStopAfter.TotalSeconds });
    }

    /// <summary>網頁的 app 更新指令交給誰（AppUpdateCoordinator 建立時設定；沒有時忽略）</summary>
    internal IAppUpdateCommands? AppUpdateCommands { get; set; }

    /// <summary>app 更新的狀態（設定頁區塊與橫幅；docs/web-protocol.md 的 AppUpdateView）：送給所有視窗，init 帶最新的</summary>
    internal void SetAppUpdateState(JObject state)
    {
        appUpdateState = state;
        PostToAll(new JObject { ["type"] = "appUpdate", ["state"] = state.DeepClone() });
    }

    /// <summary>這種代理的屏障是 app 更新的（不是 CLI 更新的）：標頭與提示改說「Clode Myriagent 正在更新」</summary>
    private bool IsAppUpdateBarrier(AgentProvider provider) => barriers.TryGetValue(provider, out var barrier) && barrier.AppUpdate;

    /// <summary>
    /// 開始 app 更新排空：每種代理開一個 AppUpdate 屏障，閒置的關掉、執行中的到安全點停下、等你回應的立刻停下、背景工作最多等 10 分鐘。
    /// 全部停下才回 Ready=true；CLI 正在更新（屏障、安裝變動、換檔）時什麼都不停，回 Ready=false。
    /// 建立屏障途中丟例外：已建立的屏障走放棄的路徑收回（AbortAppUpdate，已停下的就地接續），回 Ready=false 並帶原因
    /// </summary>
    internal Task<AppUpdatePreparation> PrepareForAppUpdateAsync(CancellationToken cancellationToken)
    {
        if (disposed)
            return Task.FromResult(new AppUpdatePreparation(Ready: true, 0, 0, 0));
        foreach (var provider in AppUpdateProviders)
        {
            if (IsAppUpdateBarrier(provider))
                return Task.FromResult(new AppUpdatePreparation(Ready: false, 0, 0, 0, "已經在排空"));
            if (IsCliUpdateBlocking(provider))
                return Task.FromResult(new AppUpdatePreparation(Ready: false, 0, 0, 0, $"{CliRestartRules.ProductName(provider)} CLI 正在更新"));
        }

        appUpdateForcedAt = null;
        var tasks = new List<Task<CliUpdatePreparation>>();
        try
        {
            foreach (var provider in AppUpdateProviders)
            {
                var done = new TaskCompletionSource<CliUpdatePreparation>(TaskCreationOptions.RunContinuationsAsynchronously);
                BeginUpdateBarrier(provider, CliUpdateUrgency.Normal, cancellationToken, done, appUpdate: true);
                tasks.Add(done.Task);
            }
        }
        catch (Exception ex)
        {
            // 同 CLI 路徑（PrepareForUpdateAsync）記錯誤；第一種代理的屏障不能留到 30 分鐘逾時才清掉
            Log.Error("app 更新前排空對話失敗", ex);
            var reason = $"app 更新前排空對話失敗：{ex.Message}";
            try
            {
                AbortAppUpdate(reason);
            }
            catch (Exception abortError)
            {
                Log.Error("收回 app 更新的屏障失敗", abortError);
            }
            return Task.FromResult(new AppUpdatePreparation(Ready: false, 0, 0, 0, reason));
        }
        Log.Info("app 更新前排空對話");
        // 背景活動日誌：開始排空時每個有程序的對話在做什麼（之後的 restart.wait／restart.snapshot 記等了多久、等誰）
        JournalApp("update.drain", new JObject
        {
            ["conversations"] = new JArray(conversations.Values.Where(c => !c.IsReplica && !c.IsDraft && c.Session != null && (c.Session.HasLiveProcess || c.Running))
                .Select(c => new JObject
                {
                    ["conv"] = c.Meta.Id,
                    ["title"] = BackgroundJournalRules.Clip(c.Meta.Title, 60),
                    ["running"] = c.Running,
                    ["waitingUser"] = c.WaitingCount > 0 ? true : null,
                    ["remoteControl"] = c.Session!.HasRemoteControl ? true : null,
                    ["bgCount"] = BackgroundOf(c)?.Count,
                    ["bg"] = BackgroundOf(c) is { Tools.Count: > 0 } work ? BackgroundJournalRules.Tools(work.Tools) : null,
                    ["programs"] = ShownPrograms(c) is { Count: > 0 } programs ? ProgramList(programs) : null,
                })),
        });
        return CombineAppPreparationsAsync(tasks);
    }

    private static async Task<AppUpdatePreparation> CombineAppPreparationsAsync(List<Task<CliUpdatePreparation>> tasks)
    {
        try
        {
            var results = await Task.WhenAll(tasks);
            return new AppUpdatePreparation(
                results.All(r => r.Ready),
                results.Sum(r => r.ClosedIdle),
                results.Sum(r => r.Held),
                results.Sum(r => r.Busy),
                results.Select(r => r.Reason).FirstOrDefault(r => r != null));
        }
        catch (OperationCanceledException)
        {
            return new AppUpdatePreparation(Ready: false, 0, 0, 0, "已取消");
        }
    }

    /// <summary>
    /// 放棄這次 app 更新（排空超過 30 分鐘、使用者取消、幫手沒有回應）：還沒停下的不再停（照常跑），
    /// 已停下的以目前的 CLI 就地重開並接續（同 CLI 更新被取消：UpdateFinished 帶目前的身分；新程序照樣開 Remote Control）。
    /// 被關掉或停下而沒有接續的、開著 Remote Control 的程序就地重開接回遠端工作階段（不送訊息）。
    /// 還在排空的 PrepareForAppUpdateAsync 以 Ready=false、Reason=reason 結束（取消權杖先被取消、而且取消在這之前就處理了的，原因是「已取消」）
    /// </summary>
    internal void AbortAppUpdate(string reason)
    {
        appUpdateForcedAt = null;
        // FinishUpdateBarrier 會清掉這些標記：先記下來（停下的這一輪會就地接續，接回時看到在執行或有程序就略過）
        var now = DateTime.Now;
        var reattach = conversations.Values.Where(c => !c.IsDraft && c.Restart.Plan is { AppUpdate: true, RemoteControl: true }
            && AgentHubRules.ShouldReattachRemoteControl(c.LastActivity, now)).ToList();
        // 等背景工作、排空逾時被停掉背景工作的：沒有停下待接續的一輪（屏障不會就地接續它），照 running.json 就地接續
        var stoppedBackground = conversations.Values.Where(c => c.RunMarkerStoppedByApp).ToList();
        foreach (var provider in AppUpdateProviders)
        {
            if (!barriers.TryGetValue(provider, out var barrier) || !barrier.AppUpdate)
                continue;
            var (held, busy) = CountBarrier(provider);
            barrier.Done.TrySetResult(new CliUpdatePreparation(Ready: false, barrier.ClosedIdle, held, busy, reason));
            FinishUpdateBarrier(new CliUpdateCompletion(provider, CliUpdateOutcome.Cancelled, cli.GetIdentity(provider)));
        }
        var perProvider = new Dictionary<AgentProvider, int>();
        ContinueBackgroundStoppedByApp(stoppedBackground, perProvider);
        ReattachRemoteControls(reattach.Where(c => !stoppedBackground.Contains(c)), perProvider);
        Log.Info($"放棄這次 app 更新：{reason}");
        JournalApp("update.abort", new JObject
        {
            ["reason"] = BackgroundJournalRules.Clip(reason),
            ["continueStoppedBackground"] = stoppedBackground.Count > 0 ? new JArray(stoppedBackground.Select(c => c.Meta.Id)) : null,
            ["reattachRemoteControl"] = reattach.Count > 0 ? reattach.Count : null,
        });
    }

    /// <summary>測試：把對話的最後動靜與最後更新往前推（並存 meta.json），模擬很久沒用的對話</summary>
    internal void AgeConversationForTest(string conversationId, TimeSpan age)
    {
        if (!conversations.TryGetValue(conversationId, out var c))
            return;
        c.LastActivity = DateTime.Now - age;
        c.Meta.UpdatedAt = DateTime.Now - age;
        SaveConversation(c, saveItems: false);
    }

    /// <summary>
    /// 目前的 resume.json 內容（設計 9.3）：app 更新屏障裡已停下的對話（排空中途也可以呼叫——協調器每處理一步就重寫，
    /// 排空途中當掉時下次一般啟動才知道哪些對話被中斷）與排空期間被延後的送出；Ui 由協調器補
    /// </summary>
    internal AppResumeFile BuildAppResume(string token, string from, string? to)
    {
        var file = new AppResumeFile
        {
            Token = token,
            From = from,
            To = to,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        foreach (var c in conversations.Values)
        {
            if (c.Restart.Plan is { AppUpdate: true, Held: true } plan)
            {
                file.Conversations.Add(new ResumeConversation
                {
                    Id = c.Meta.Id,
                    Provider = c.Meta.Provider,
                    AccountId = c.Meta.AccountId,
                    SessionId = c.Meta.SessionId,
                    Mode = plan.NoContinueReason == null ? ResumeMode.Continue : ResumeMode.None,
                    WhyNot = plan.NoContinueReason,
                    StoppedAt = plan.InterruptedAt is { } at ? new DateTimeOffset(at).ToUnixTimeMilliseconds() : null,
                    InterruptedTools = plan.InterruptedTools.ToList(),
                    PendingRequests = plan.PendingRequests.ToList(),
                    BackgroundWorkStopped = plan.HadBackground,
                    RiskOperation = plan.Risk?.Operation,
                    CliVersion = plan.FromVersion,
                });
            }
            if (c.Restart.DeferredSend != null && !c.IsDraft)
                file.DeferredSends.Add(c.Meta.Id);
            // 排空關掉或停下時開著 Remote Control 的：新版沒有自動接續、也沒有延後送出的就重開程序接回遠端工作階段。
            // 太久沒動靜的不記（接回之後也只是等著被回收，AgentHubRules.ShouldReattachRemoteControl）
            if (c.Restart.Plan is { AppUpdate: true, RemoteControl: true } && !c.IsDraft && AgentHubRules.ShouldReattachRemoteControl(c.LastActivity, DateTime.Now))
                file.RemoteControl.Add(c.Meta.Id);
        }
        return file;
    }

    // ───────────────────────────── 草稿快照（設計 9.3）─────────────────────────────

    private sealed class DraftSnapshotRequest
    {
        public required string Id { get; init; }

        /// <summary>還沒回覆的視窗</summary>
        public required HashSet<string> Waiting { get; init; }

        /// <summary>已收到的輸入框文字（對話 id → 文字）</summary>
        public Dictionary<string, string> Drafts { get; } = new(StringComparer.Ordinal);

        public TaskCompletionSource<IReadOnlyList<ResumeDraft>> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ResumeDraft> Collected() => Drafts.Select(kv => new ResumeDraft(kv.Key, kv.Value)).ToList();
    }

    private DraftSnapshotRequest? draftRequest;

    /// <summary>app 更新後要填回輸入框的文字（對話 id → 文字）：PostConversation 送一次就清掉</summary>
    private readonly Dictionary<string, string> composerDrafts = new(StringComparer.Ordinal);

    /// <summary>
    /// 向所有視窗要輸入框的文字（設計 9.3）：收齊或時限到就回傳已收到的（逾時不丟例外、不擋更新）。
    /// 同一個對話在多個視窗都有字時留最長的
    /// </summary>
    internal async Task<IReadOnlyList<ResumeDraft>> RequestDraftSnapshotAsync(TimeSpan timeout)
    {
        draftRequest?.Done.TrySetResult(Array.Empty<ResumeDraft>());
        var request = new DraftSnapshotRequest { Id = Guid.NewGuid().ToString("N"), Waiting = new HashSet<string>(views.Keys, StringComparer.Ordinal) };
        draftRequest = request;
        if (request.Waiting.Count == 0)
            request.Done.TrySetResult(Array.Empty<ResumeDraft>());
        else
            PostToAll(new JObject { ["type"] = "draftSnapshotRequest", ["requestId"] = request.Id });
        var finished = await Task.WhenAny(request.Done.Task, Task.Delay(timeout));
        if (ReferenceEquals(draftRequest, request))
            draftRequest = null;
        return finished == request.Done.Task ? await request.Done.Task : request.Collected();
    }

    private void OnDraftSnapshot(IAgentView view, string? requestId, JArray? drafts)
    {
        var request = draftRequest;
        if (request == null || requestId != request.Id || !request.Waiting.Remove(view.Id))
            return;
        foreach (var d in drafts?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            var id = d.Value<string>("conversationId");
            var text = d.Value<string>("text");
            if (string.IsNullOrEmpty(id) || string.IsNullOrWhiteSpace(text) || !conversations.ContainsKey(id))
                continue;
            if (!request.Drafts.TryGetValue(id, out var existing) || text.Length > existing.Length)
                request.Drafts[id] = text;
        }
        if (request.Waiting.Count == 0)
            request.Done.TrySetResult(request.Collected());
    }

    /// <summary>
    /// resume.json 的 ui（彈出視窗由協調器補）：正式對話的輸入框文字；草稿對話有字或有附件的整個保留
    /// （沿用原本的 id 與資料夾，附件留在那裡）
    /// </summary>
    internal ResumeUi BuildResumeUi(IReadOnlyList<ResumeDraft> drafts)
    {
        var ui = new ResumeUi();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in drafts)
            texts[d.ConversationId] = d.Text;
        foreach (var c in conversations.Values)
        {
            texts.TryGetValue(c.Meta.Id, out var text);
            if (!c.IsDraft)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    ui.Drafts.Add(new ResumeDraft(c.Meta.Id, text));
                continue;
            }
            if (string.IsNullOrWhiteSpace(text) && c.PendingAttachments.Count == 0)
                continue;
            ui.NewConversations.Add(new ResumeNewConversation(c.Meta.Id, c.Meta.AccountId, c.Meta.WorkingDirectory, text ?? string.Empty, c.PendingAttachments.ToList()));
        }
        return ui;
    }

    // ───────────────────────────── 新版啟動後的接續（設計 9.5）─────────────────────────────

    private PendingAppResume? pendingResume;

    /// <summary>主視窗的網頁就緒（每次都觸發，在接續送出之後）：協調器以第一次當新版的健康訊號</summary>
    internal event Action? MainViewReady;

    /// <summary>接續與延後的送出都已開始（只觸發一次）：協調器刪 resume.json、送系統匣通知</summary>
    internal event Action<AppResumeSummary>? AppResumeDispatched;

    internal void ScheduleAppResume(PendingAppResume pending) => pendingResume = pending;

    /// <summary>
    /// 新版啟動後的接續（設計 9.5）：mode=continue 而且 resume.json 有效的，照同時重開上限分批以隱藏送出接續訊息
    /// （Claude 以 --resume、Codex 以 thread/resume 啟動新程序，同「繼續」按鈕）；其他列出的對話加提示、標頭給「繼續」。
    /// 排空時被延後的送出（佇列第一則，queue.json 已還原）接著送出。草稿對話（送 init 之前由 RestorePendingDrafts 建好）先送到主視窗。
    /// 只執行一次（網頁重新載入不會再接續）。還原草稿與接續各自隔離例外（只記 log、不往外丟）：接續處理到一半出錯時，
    /// 已經排入的接續照常進行，也照樣通知「接續已處理」（協調器刪 resume.json，下次一般啟動不會再報一次）
    /// </summary>
    private void RunPendingResume(IAgentView main, IReadOnlyList<ConversationState> restoredDrafts)
    {
        var pending = pendingResume;
        if (pending == null)
            return;
        pendingResume = null;
        var file = pending.File;
        try
        {
            foreach (var draft in restoredDrafts)
                PostConversation(main, draft, paneId: null);
            if (restoredDrafts.Count > 0)
                MarkAccountsDirty();
        }
        catch (Exception ex)
        {
            Log.Error("app 更新後送出還原的草稿對話失敗", ex);
        }

        int notContinued = 0, deferredSent = 0;
        var starts = new List<Task<bool>>();
        try
        {
            var appFrom = ResumeIntentRules.VersionOfRelease(file.From);
            var perProvider = new Dictionary<AgentProvider, int>();
            var continuing = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in file.Conversations)
            {
                if (!conversations.TryGetValue(entry.Id, out var c) || c.IsDraft)
                    continue;
                EnsureItems(c);
                var context = new ContinuationContext(c.Meta.Provider, entry.CliVersion, cli.GetIdentity(c.Meta.Provider)?.Version,
                    entry.InterruptedTools, entry.PendingRequests, entry.BackgroundWorkStopped, AppFrom: appFrom, AppTo: pending.AppTo);
                if (ResumeIntentRules.ShouldContinue(entry, pending.Validity) && !c.Running)
                {
                    int index = perProvider.TryGetValue(c.Meta.Provider, out var n) ? n : 0;
                    perProvider[c.Meta.Provider] = index + 1;
                    continuing.Add(c.Meta.Id);
                    starts.Add(StartContinuationAsync(c, entry, context, ResumeIntentRules.StartDelay(index, c.Meta.Provider), pending));
                    continue;
                }
                MarkNotContinuedAfterAppUpdate(c, entry, context, pending);
                notContinued++;
            }

            if (pending.Validity == ResumeValidity.Ok)
            {
                var sentDeferred = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in file.DeferredSends)
                {
                    if (continuing.Contains(id) || !conversations.TryGetValue(id, out var c) || c.IsDraft || c.Running || c.Queue.Count == 0)
                        continue;
                    var next = c.Queue[0];
                    c.Queue.RemoveAt(0);
                    PostComposer(c);
                    _ = SendCoreAsync(c, next.Text, next.Attachments, displayText: next.DisplayText, remoteOrigin: next.RemoteOrigin);
                    sentDeferred.Add(id);
                    deferredSent++;
                }

                // 排空時開著 Remote Control 的對話：自動接續與延後的送出都會啟動程序（照樣開 Remote Control），其他的重開程序接回遠端工作階段，
                // 照同時重開上限接在接續後面分批（設計 9.5）
                var reattach = file.RemoteControl
                    .Where(id => !continuing.Contains(id) && !sentDeferred.Contains(id))
                    .Select(id => conversations.TryGetValue(id, out var c) ? c : null)
                    .OfType<ConversationState>()
                    // 舊版寫的 resume.json 不篩（換到這一版的第一次會列出全部）：程序在新版重新載入、最後動靜的時間不在了，以對話的最後更新時間篩
                    .Where(c => !c.IsDraft && !c.Running && AgentHubRules.ShouldReattachRemoteControl(c.Meta.UpdatedAt, DateTime.Now))
                    .ToList();
                ReattachRemoteControls(reattach, perProvider);
            }
        }
        catch (Exception ex)
        {
            Log.Error("app 更新後的接續處理到一半失敗（已排入的接續照常進行）", ex);
        }

        _ = RaiseResumeDispatchedAsync(starts, notContinued, deferredSent, pending);
    }

    /// <summary>
    /// app 更新後還原草稿（主視窗第一次就緒、送 init 之前；pendingResume 由緊接著的 RunPendingResume 取走）：
    /// 回傳建好的草稿對話，init 的 restoredDrafts 列出、RunPendingResume 在 init 之後送出它們的 conversation。
    /// 草稿與接續無關：token 不符或太舊也還原；還原失敗只記 log、回傳已建好的，不影響 init 與接續
    /// </summary>
    private List<ConversationState> RestorePendingDrafts(IAgentView main)
    {
        var restored = new List<ConversationState>();
        if (pendingResume is not { } pending)
            return restored;
        try
        {
            RestoreResumeUi(main, pending.File.Ui, restored);
        }
        catch (Exception ex)
        {
            Log.Error("app 更新後還原草稿失敗", ex);
        }
        return restored;
    }

    /// <summary>
    /// 還原草稿（在主視窗）：輸入框文字等那個對話開啟時一起送（conversation 的 draft）；
    /// 草稿對話沿用原本的 id、資料夾與還在的附件，帳戶已移除的不還原（資料夾留給下次啟動清理）。建好的草稿對話加進 restored
    /// </summary>
    private void RestoreResumeUi(IAgentView main, ResumeUi ui, List<ConversationState> restored)
    {
        foreach (var d in ui.Drafts)
        {
            if (conversations.ContainsKey(d.ConversationId))
                composerDrafts[d.ConversationId] = d.Text;
        }
        foreach (var d in ui.NewConversations)
        {
            // id 要是草稿資料夾的格式（同啟動清理的 FindDraftLeftovers）
            if (!Guid.TryParseExact(d.DraftId, "N", out _) || conversations.ContainsKey(d.DraftId) || accounts.Find(d.AccountId) is not { } account)
                continue;
            var state = CreateDraft(main, account, restoring: d);
            state.PendingAttachments.AddRange(d.Attachments.Where(a => fileExists(a.StoredPath)));
            if (!string.IsNullOrWhiteSpace(d.Text))
                composerDrafts[d.DraftId] = d.Text;
            restored.Add(state);
        }
    }

    private string? TakeComposerDraft(string conversationId)
        => composerDrafts.Remove(conversationId, out var text) ? text : null;

    /// <summary>
    /// 等分批的時間到了就送出接續（只等開始送出：隱藏的提示已加進對話並存檔，不等這一輪完成）；回傳是否送出了接續。
    /// CLI 正在更新時（這時啟動 CLI 一定失敗，隱藏送出又不排進佇列）改成沒有接續、標頭給「繼續」，同「繼續」按鈕不在 CLI 更新時送出
    /// </summary>
    private async Task<bool> StartContinuationAsync(ConversationState c, ResumeConversation entry, ContinuationContext context, TimeSpan delay, PendingAppResume pending)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
        if (disposed || c.Running || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
            return false;
        if (IsCliUpdateBlocking(c.Meta.Provider))
        {
            Log.Info($"對話 {c.Meta.Id} 在 app 更新後沒有自動接續：{CliRestartRules.ProductName(c.Meta.Provider)} CLI 正在更新");
            MarkNotContinuedAfterAppUpdate(c, entry, context, pending);
            return false;
        }
        Journal("resume.app", c, new JObject
        {
            ["result"] = "continued",
            ["mode"] = entry.Mode.ToString(),
            ["backgroundStopped"] = entry.BackgroundWorkStopped ? true : null,
            ["interrupted"] = entry.InterruptedTools is { Count: > 0 } tools ? new JArray(tools.Select(t => BackgroundJournalRules.Clip(t.Title ?? t.ToolName))) : null,
        });
        _ = SendCoreAsync(c, ContinuationRules.Build(context), new List<AgentAttachment>(),
            ResumeIntentRules.ContinuedNotice(pending.AppTo, pending.RolledBack), hiddenNoticeKind: AgentNoticeKind.AppUpdate);
        return true;
    }

    /// <summary>
    /// 沒有自動接續的對話：加提示（Markdown，操作摘要跳脫）、標頭給「繼續」（純文字的說明）。
    /// 按鈕的資料就是這次從 resume.json 重建的接續內容（設計 9.5 第 4 步，不依賴上一個程序記憶體裡的 Restart.Result）
    /// </summary>
    private void MarkNotContinuedAfterAppUpdate(ConversationState c, ResumeConversation entry, ContinuationContext context, PendingAppResume pending)
    {
        var reason = ResumeIntentRules.ReasonOf(entry, pending.Validity);
        var notice = ResumeIntentRules.DescribeNotContinued(entry, pending.Validity, pending.AppTo, pending.RolledBack, markdown: true);
        var detail = ResumeIntentRules.DescribeNotContinued(entry, pending.Validity, pending.AppTo, pending.RolledBack, markdown: false);
        Journal("resume.app", c, new JObject
        {
            ["result"] = "notContinued",
            ["reason"] = reason.ToString(),
            ["detail"] = BackgroundJournalRules.Clip(detail),
            ["backgroundStopped"] = entry.BackgroundWorkStopped ? true : null,
        });
        AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = notice, NoticeKind = AgentNoticeKind.AppUpdate });
        // 不帶版本：標頭的「舊版 → 新版」是 CLI 的，app 更新時 CLI 沒有換版
        c.Restart.Result = new RestartResultView(CliRestartState.NotContinued, null, null, detail,
            CliRestartRules.CanContinueManually(reason), context);
        // 同 CLI 換版沒有接續的一輪（WrapUpInterruptedTurn）：沒有顯示在任何畫面上就標未讀
        if (c.VisibleViewIds.Count == 0)
            c.Unread = true;
        SaveConversation(c, saveItems: true);
        MarkHeaderDirty(c);
        MarkListDirty();
    }

    /// <summary>所有接續都開始送出（或改成沒有接續）之後通知一次；自動接續數只算真的送出的</summary>
    private async Task RaiseResumeDispatchedAsync(List<Task<bool>> starts, int notContinued, int deferredSent, PendingAppResume pending)
    {
        int continued = 0;
        foreach (var start in starts)
        {
            bool sent;
            try
            {
                sent = await start;
            }
            catch (Exception ex)
            {
                Log.Warn($"app 更新後接續的送出有失敗：{ex.Message}");
                sent = false;
            }
            if (sent)
                continued++;
            else
                notContinued++;
        }
        Log.Info($"app 更新後接續：自動接續 {continued}、沒有接續 {notContinued}、延後的送出 {deferredSent}（resume.json {pending.Validity}）");
        if (!disposed)
            AppResumeDispatched?.Invoke(new AppResumeSummary(continued, notContinued, deferredSent, pending.RolledBack, pending.AppTo));
    }

    /// <summary>
    /// 維護計時器：app 更新排空超過 10 分鐘，開著 Remote Control、還在等你回應的對話照「立即套用」停下
    /// （設計 9.2；沒回應的請求記下、接續後由代理重新提出）。等背景工作的另外最多等 CliRestartRules.BackgroundWaitLimit（30 分鐘，
    /// EvaluateRestart；2026-10-24 使用者要求，以前也是 10 分鐘），停下時 running.json 記成 app 停掉的，新版馬上請代理確認背景工作。
    /// 「強制更新」（ForceAppUpdate）按下後滿 AppUpdateRestartRules.ForceStopAfter：還沒停下的一律照「立即套用」停下（不只等回應的）
    /// </summary>
    private void ApplyNowForAppOverdue(UpdateBarrier barrier, DateTime now)
    {
        if (!barrier.AppUpdate || !barrier.Draining)
            return;
        var forced = appUpdateForcedAt is { } forcedAt && now - forcedAt >= AppUpdateRestartRules.ForceStopAfter;
        if (!forced && now - barrier.StartedAt < CliRestartRules.DrainGrace)
            return;
        barrier.Overdue = true;
        foreach (var c in ConversationsOf(barrier.Provider).ToList())
        {
            if (c.Restart.Plan is not { AppUpdate: true, Drain: true, Held: false, Drained: false, InFlight: false } plan
                || plan.ApplyNow || (!forced && plan.Waiting != CliRestartState.WaitingUser))
                continue;
            if (forced)
                JournalRestartForced(c, plan);
            plan.ApplyNow = true;
            plan.Deferred = false;
            EvaluateRestart(c);
        }
    }

    // ───────────────────────────── Remote Control 的接回（設計 9.2、9.5）─────────────────────────────

    /// <summary>每次接回前最多等多久，讓還在關的舊程序結束（放棄更新時，排空關閒置程序的動作可能還沒做完）</summary>
    internal TimeSpan ReattachCloseWait { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>排空關掉或停下的、開著 Remote Control 的對話重開程序接回：照同時重開上限分批（perProvider 是同一種代理前面已排的數目）</summary>
    private void ReattachRemoteControls(IEnumerable<ConversationState> targets, Dictionary<AgentProvider, int> perProvider)
    {
        int count = 0;
        foreach (var c in targets)
        {
            int index = perProvider.TryGetValue(c.Meta.Provider, out var n) ? n : 0;
            perProvider[c.Meta.Provider] = index + 1;
            _ = ReattachRemoteControlAsync(c, ResumeIntentRules.StartDelay(index, c.Meta.Provider));
            count++;
        }
        if (count > 0)
            Log.Info($"重開 {count} 個對話的程序接回 Remote Control");
    }

    /// <summary>
    /// 重開程序、只開 Remote Control 接回遠端工作階段（IAgentSession.ReattachRemoteControlAsync，不送訊息）：等分批的時間到了，
    /// 之前關程序的動作還沒做完就等它（最多 ReattachCloseWait）；對話還在、沒有在執行、沒有程序、設定還開著 Remote Control、
    /// CLI 沒有在更新才做。失敗只記 log：下次送出照常啟動程序並接回
    /// </summary>
    private async Task ReattachRemoteControlAsync(ConversationState c, TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
        var deadline = DateTime.UtcNow + ReattachCloseWait;
        while (!disposed && c.Restart.Plan is { InFlight: true } && DateTime.UtcNow < deadline)
            await Task.Delay(200);
        if (disposed || !IsLive(c) || c.Running || LiveProcess(c) || c.Restart.Plan is { InFlight: true }
            || IsCliUpdateBlocking(c.Meta.Provider) || RemoteControlOptions(c) == null)
            return;
        try
        {
            var session = EnsureSession(c);
            if (await session.ReattachRemoteControlAsync(BuildSessionSettings(c, historyRecap: null)))
                Log.Info($"已重開對話 {c.Meta.Id} 的程序、接回 Remote Control");
        }
        catch (Exception ex)
        {
            Log.Warn($"重開對話 {c.Meta.Id} 的程序接回 Remote Control 失敗：{ex.Message}");
        }
    }
}
