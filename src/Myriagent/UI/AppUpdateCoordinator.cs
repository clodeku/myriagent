using System.Globalization;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.UI;

/// <summary>協調器需要的宿主能力（AppController 實作；測試換成假的）</summary>
internal interface IAppUpdateShell
{
    bool AnyWindowForeground { get; }

    /// <summary>主視窗藏著（關到系統匣、還沒顯示過或最小化）：交棒時記進 resume.json，新版照樣不顯示（設計 9.3）</summary>
    bool MainWindowHidden { get; }

    TimeSpan SinceLastInput { get; }

    bool LoginInProgress { get; }

    bool CliUpdating { get; }

    IReadOnlyList<ResumePopout> CapturePopouts();

    void OpenPopouts(IReadOnlyList<ResumePopout> popouts);

    void Notify(string title, string message);

    int? StartHelper(string executable, IReadOnlyList<string> arguments);

    /// <summary>放棄交棒時結束還沒寫 .armed 的幫手（它在舊 app 結束前什麼都還沒改，隨時結束都安全）</summary>
    void StopHelper(int pid);

    /// <summary>交棒後結束 app：不問、不換版（幫手會換）、keepDrafts 列出的草稿資料夾不刪</summary>
    void ExitForUpdate(IReadOnlySet<string> keepDrafts);

    /// <summary>「安裝到這台電腦」之後：照一般結束的流程收尾，結束後啟動安裝好的那份</summary>
    void RestartInto(string executable);

    /// <summary>把主視窗叫到前面（較新的版本請這一份交棒時：使用者剛開了新版，正在看）</summary>
    void ShowMain();

    /// <summary>
    /// 交棒給較新的版本（新版自動接手舊版）：先標成正在結束（不再回應喚醒，新版才會等這一份結束後接手）、啟動新版，
    /// 啟動不了回 false（不結束，呼叫端就地接續）；啟動了就照 ExitForUpdate 收尾結束
    /// </summary>
    bool HandOver(string executable, IReadOnlyList<string> arguments, IReadOnlySet<string> keepDrafts);
}

internal sealed record AppUpdateTimings(TimeSpan FirstCheck, TimeSpan CheckInterval, TimeSpan Tick, TimeSpan IdleThreshold, TimeSpan ArmedTimeout)
{
    /// <summary>開著 Remote Control 的對話多久沒有動靜才算沒人從手機或 claude.ai 在用（AppUpdateRestartRules.RemoteIdleThreshold）</summary>
    public TimeSpan RemoteIdleThreshold { get; init; } = AppUpdateRestartRules.RemoteIdleThreshold;

    /// <summary>開著 Remote Control 的對話在等回應或有工具在執行時，多久沒有動靜就不再算在用（AppUpdateRestartRules.RemoteWaitThreshold）</summary>
    public TimeSpan RemoteWaitThreshold { get; init; } = AppUpdateRestartRules.RemoteWaitThreshold;

    public static AppUpdateTimings Normal { get; } = new(TimeSpan.FromMinutes(1), AppUpdateRestartRules.CheckInterval, TimeSpan.FromSeconds(15),
        AppUpdateRestartRules.IdleThreshold, AppUpdateRestartRules.ArmedTimeout);

    /// <summary>端到端測試（MYRIAGENT_UPDATE_E2E_FAST=1）：3 秒檢查一次、不等使用者閒置（Remote Control 的對話也不等沒有動靜；端到端不開對話）</summary>
    public static AppUpdateTimings E2E { get; } = new(TimeSpan.FromSeconds(1), AppUpdateRestartRules.E2ECheckInterval, TimeSpan.FromSeconds(1),
        TimeSpan.Zero, AppUpdateRestartRules.ArmedTimeout) { RemoteIdleThreshold = TimeSpan.Zero, RemoteWaitThreshold = TimeSpan.Zero };
}

/// <summary>
/// 串起 AppUpdateManager、中樞與換版幫手（設計 9 節）：已備妥而且使用者沒在用 → 中樞排空 → 草稿快照 → 寫 resume.json → 以舊版 exe 啟動幫手 →
/// 看到 .armed 才結束；排空超過 30 分鐘就放棄（就地接續、1 小時後再試），幫手啟動不了或 10 秒沒回應也放棄（結束幫手、6 小時後再試），
/// 使用者取消就 6 小時內不自動套用；幫手放棄後以 --after-update-failed 重開、備妥還在時同樣 6 小時後再試。
/// 只在 UI 執行緒使用（manager 的事件排回建立時的同步內容，也就是 UI 執行緒）
/// </summary>
internal sealed class AppUpdateCoordinator : IAppUpdateCommands, IDisposable
{
    private enum Stage
    {
        Waiting,
        Draining,
        HandingOff,
        Exiting,
    }

    private readonly AgentHub hub;
    private readonly AppUpdateManager manager;
    private readonly IAppUpdateShell shell;
    private readonly InstallLayout layout;
    private readonly string dataRoot;
    private readonly string? managedRelease;
    private readonly bool usesDefaultData;
    private readonly AppUpdateTimings timings;
    private readonly Func<DateTime> clock;
    private readonly SynchronizationContext? ui;
    private readonly Action managerChanged;
    private Stage stage;

    /// <summary>
    /// 第幾次排空（DrainAsync 開始時加一）。放棄之後舊排空的 await 還會晚一步回來（屏障以 RunContinuationsAsynchronously 完成），
    /// 這時新的排空可能已經開始、階段又是 Draining：只看階段會把新的那次當成自己的而放棄掉，所以 await 之後一律比對世代
    /// </summary>
    private int drainGeneration;

    /// <summary>
    /// 較新的版本請這一份交棒（新版自動接手舊版）：排空後以 --handed-over 啟動它，不經換版幫手。
    /// <paramref name="SelfRestart"/>：這個程序的 .NET 已被移除，交棒給同一版自己（重新啟動 app，新程序用新的 .NET；BeginRuntimeRestart）
    /// </summary>
    private sealed record HandoverTarget(string Executable, string Release, bool SelfRestart = false);

    /// <summary>進行中的交棒（排空、交棒中）；null＝這次是 app 更新或沒有在排空</summary>
    private HandoverTarget? handover;

    /// <summary>以 --handed-over 啟動（較舊的實例交棒後開的）：通知寫「已切換到」</summary>
    private bool startedAfterHandover;

    private string? token;
    private DateTime drainStartedAt;
    private DateTime? snoozedUntil;
    private DateTime? retryAfter;
    private bool userRequested;

    /// <summary>使用者按了「強制更新」（ForceRestart）：不等背景工作開始排空、排空中出現背景工作也不放棄，中樞滿 10 分鐘照立即套用停下</summary>
    private bool forced;
    private bool installing;
    private string? startupToken;

    /// <summary>--after-update 的 token：只有這種啟動有幫手在等健康訊號</summary>
    private string? afterUpdateToken;

    /// <summary>以 --after-update-failed 啟動（幫手放棄或回滾之後啟動的舊版）</summary>
    private bool startedAfterFailedUpdate;

    private AppResumeFile? startupResume;

    /// <summary>啟動時 resume.json 在但讀不到：這次沒有照它接續，接續送出後也不刪它（留給下次啟動給「繼續」）</summary>
    private bool startupResumeUnreadable;

    private bool healthy;

    /// <summary>交棒中啟動、還沒寫 .armed 的幫手：放棄這次或使用者結束 app 時要先結束它</summary>
    private int? helperPid;

    /// <summary>resume.json 該刪卻沒刪掉（被別的程式開著）：等待階段的 Tick 再刪，不然下次一般啟動會把已接續的對話當成上次中斷</summary>
    private bool resumeDeletePending;

    /// <summary>最近一次送給網頁的 blocker（Tick 發現它變了就重送）</summary>
    private string? publishedBlocker;

    /// <summary>背景活動日誌最後記下的階段與阻擋原因（變了才記 update.state）</summary>
    private (Stage Stage, string? Blocker)? journaledState;

    private System.Threading.Timer? timer;
    private CancellationTokenSource? drainCancel;
    private bool disposed;

    /// <param name="managedRelease">從受管理的安裝執行時的 release；開發版或還沒安裝為 null（不自動換版）</param>
    /// <param name="usesDefaultData">資料夾是預設的（沒有 --data、MYRIAGENT_AGENT_DATA、MYRIAGENT_AGENT_PROBE*）；不是就不提供「安裝到這台電腦」</param>
    public AppUpdateCoordinator(AgentHub hub, AppUpdateManager manager, IAppUpdateShell shell, InstallLayout layout, string dataRoot,
        string? managedRelease, bool usesDefaultData, AppUpdateTimings timings, Func<DateTime>? clock = null)
    {
        this.hub = hub;
        this.manager = manager;
        this.shell = shell;
        this.layout = layout;
        this.dataRoot = dataRoot;
        this.managedRelease = managedRelease;
        this.usesDefaultData = usesDefaultData;
        this.timings = timings;
        this.clock = clock ?? (() => DateTime.Now);
        ui = SynchronizationContext.Current;
        managerChanged = () => Post(Publish);
        hub.AppUpdateCommands = this;
        hub.AppDrainProgress += WriteResume;
        hub.MainViewReady += OnMainViewReady;
        hub.AppResumeDispatched += OnResumeDispatched;
        manager.Changed += managerChanged;
    }

    public bool Draining => stage is Stage.Draining or Stage.HandingOff;

    /// <summary>啟動時 resume.json 讀不到（被別的程式不共享讀取地開著）最多再等這麼久</summary>
    public static readonly TimeSpan StartupResumeRetry = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 啟動時（建立中樞之前）讀 resume.json：AppController 據此決定草稿殘留的清理（ResumeIntentRules.DraftLeftoversToDelete）。
    /// 讀不到時重試（最多 StartupResumeRetry），仍讀不到回 Unreadable
    /// </summary>
    public static StartupResume LoadStartupResume(string dataRoot, TimeSpan? retryFor = null)
        => ResumeFileStore.LoadAtStartup(dataRoot, retryFor ?? StartupResumeRetry);

    /// <summary>
    /// 把啟動時讀到的 resume.json 交給中樞（主視窗第一次 ready 時執行）：--after-update／--after-update-failed 的 token 相符才接續，
    /// 一般啟動讀到的（上次排空到一半結束）全部給「繼續」。帶 token 啟動卻沒有檔案時照樣排一次（只為了系統匣的「已更新」通知）；
    /// 檔案在但讀不到時也一樣，只是不刪它、通知說明這次沒有接續。
    /// 要在 Start 之前呼叫：以 --after-update-failed 啟動時，這裡依啟動當下幫手留下的狀態決定要不要冷卻
    /// </summary>
    public void ScheduleStartupResume(StartupOptions options, StartupResume startup)
    {
        var file = startup.File;
        startupToken = options.AfterUpdateToken ?? options.AfterUpdateFailedToken ?? options.HandedOverToken;
        afterUpdateToken = options.AfterUpdateToken;
        startedAfterHandover = options.HandedOverToken != null;
        startedAfterFailedUpdate = options.AfterUpdateFailedToken != null;
        startupResume = file;
        startupResumeUnreadable = startup.Unreadable;
        if (startedAfterFailedUpdate)
        {
            // 幫手沒有消耗這次的備妥（Aborted、OldProcessHung、Invalid，或回滾時寫狀態失敗）：Staged 還在，馬上再排空多半又失敗。
            // 照設計第 12 節「仍失敗放棄這次，6 小時後再試」（retryAfter 只在記憶體，新程序要自己補上）。
            // 一定要看啟動當下的狀態（Start 與第一次檢查都還沒開始；AppController 剛建立 manager 時讀過，這裡再讀一次，不依賴呼叫端的時機），
            // 不能等主視窗就緒才看：那時第一次檢查（E2E 在 1 秒後）可能已經重新備妥閘門回滾清掉的那一版，那是新的備妥，不是幫手沒消耗的。
            // 健康閘門失敗的回滾已由 OnGateFailed 清掉 Staged，不設冷卻（同一版第二次閘門失敗才列壞版的流程照舊）。
            // 這裡不送狀態（AppController 在主視窗建立之前呼叫，送狀態會讀 AnyWindowForeground）：Start 與主視窗就緒時都會送
            manager.RefreshFromState();
            if (manager.Status.StagedRelease is { } pending)
            {
                retryAfter = clock() + AppUpdateRestartRules.RetryAfterHelperFailure;
                Log.Info($"上次換版到 {pending} 沒有完成，{AppUpdateRestartRules.RetryAfterHelperFailure.TotalHours:F0} 小時後再自動套用");
            }
        }
        if (file == null && startupToken == null)
            return;
        var resume = file ?? new AppResumeFile();
        var validity = ResumeIntentRules.Validate(file, startupToken, DateTime.UtcNow);
        hub.ScheduleAppResume(new PendingAppResume(resume, validity, AppVersionInfo.VersionText, RolledBack: startedAfterFailedUpdate));
    }

    public void Start()
    {
        manager.Start(timings.FirstCheck, timings.CheckInterval);
        timer = new System.Threading.Timer(_ => Post(Tick), null, timings.Tick, timings.Tick);
        Publish();
    }

    // ───────────────────────────── 網頁指令 ─────────────────────────────

    public void Check() => _ = manager.CheckAsync();

    /// <summary>伺服器說有新版時上一次因此檢查的時間（Environment.TickCount64；UI 執行緒）</summary>
    private long? lastAnnouncedCheck;

    /// <summary>
    /// 伺服器說有新版（U3：Hello 的 latestApp、ServerNotice{appRelease}；UI 執行緒）：比這台新、或還沒裝的就提早檢查一次。
    /// 重連時每次 Hello 都會帶 latestApp，所以照 <see cref="AppUpdateRetryRules.ShouldCheckOnAnnounce"/> 每 10 分鐘最多一次
    /// </summary>
    public void CheckSoon(string release)
    {
        var now = Environment.TickCount64;
        if (!AppUpdateRetryRules.ShouldCheckOnAnnounce(release, AppVersionInfo.Current, manager.Status.StagedRelease, lastAnnouncedCheck, now))
            return;
        lastAnnouncedCheck = now;
        Log.Info($"伺服器說有新版 {release}：提早檢查");
        _ = manager.CheckAsync();
    }

    public void RestartNow()
    {
        userRequested = true;
        Tick();
    }

    /// <summary>
    /// 「強制更新」（2026-10-28 使用者要求）：還沒開始排空時不等背景工作、也不等使用者閒置就開始（CLI 更新、登入、安裝這類系統工作照樣先等）；
    /// 排空中按下就改成強制。對話照常陸續到安全點停下，按下後滿 AppUpdateRestartRules.ForceStopAfter 還沒停下的由中樞照立即套用停下
    /// （AgentHub.ForceAppUpdate）。交棒（使用者開了新版）不提供
    /// </summary>
    public void ForceRestart()
    {
        if (handover is { SelfRestart: false })
            return;
        if (stage == Stage.Draining)
        {
            if (forced)
                return;
            forced = true;
            Log.Info("排空中改成強制更新");
            hub.ForceAppUpdate();
            Publish();
            return;
        }
        if (stage != Stage.Waiting)
            return;
        forced = true;
        userRequested = true;
        Tick();
        Publish();
    }

    /// <summary>
    /// 排空中：放棄這次（已停下的就地接續），6 小時內不自動套用；還沒開始：6 小時內不自動套用。
    /// 交棒的排空只放棄這次交棒，不影響 app 自動更新
    /// </summary>
    public void CancelRestart()
    {
        if (handover is { SelfRestart: false } && stage == Stage.Draining)
        {
            Abort("已取消切換到新開的版本", retryDelay: null);
            return;
        }
        snoozedUntil = clock() + AppUpdateRestartRules.SnoozeAfterCancel;
        userRequested = false;
        forced = false;
        if (stage == Stage.Draining)
            Abort(handover is { SelfRestart: true } ? "已取消這次重新啟動" : "已取消這次更新", retryDelay: null);
        Publish();
    }

    public void InstallHere() => _ = InstallHereAsync();

    private async Task InstallHereAsync()
    {
        if (installing || !CanInstallHere())
            return;
        installing = true;
        Publish();
        try
        {
            var exe = await Task.Run(() => SelfInstaller.Install(AppContext.BaseDirectory, layout, AppVersionInfo.Current, registerShell: !UpdateEnvironment.IsE2E));
            shell.RestartInto(exe);
        }
        catch (Exception ex)
        {
            Log.Error("安裝到這台電腦失敗", ex);
            shell.Notify("Clode Myriagent", $"安裝失敗：{ex.Message}");
        }
        finally
        {
            installing = false;
            Publish();
        }
    }

    /// <summary>
    /// 提供與執行「安裝到這台電腦」的共同條件（網頁的 canInstall 與 appInstallHere 指令都只看這裡）：SelfInstaller.CanOfferInstall，而且用預設資料夾。
    /// --data、MYRIAGENT_AGENT_DATA、MYRIAGENT_AGENT_PROBE* 的實例一律不提供：裝好的那份會改開預設資料夾，
    /// 開發建置也會被裝進真的 Programs 資料夾（git 裡的建置 IsDev 一律是 false，擋不住）
    /// </summary>
    private bool CanInstallHere() => usesDefaultData && SelfInstaller.CanOfferInstall(managedRelease, AppVersionInfo.Current, layout);

    // ───────────────────────────── 新版自動接手舊版 ─────────────────────────────

    /// <summary>
    /// 較新的版本請這一份交棒（單一實例的監聽執行緒驗過請求、排到 UI 執行緒；InstanceHandoverRules）：主視窗叫到前面（使用者剛開了新版），
    /// 照 app 更新的排空流程停下對話、寫 resume.json，再以 --handed-over 啟動新版並結束。正在套用更新或安裝時不交棒、通知說明
    /// </summary>
    public void BeginHandover(string executable, string release)
    {
        if (disposed)
            return;
        shell.ShowMain();
        var version = ResumeIntentRules.VersionOfRelease(release) ?? release;
        if (stage != Stage.Waiting || installing)
        {
            Log.Info($"不交棒給 {release}：{(installing ? "正在安裝到這台電腦" : "正在套用更新")}");
            shell.Notify("Clode Myriagent", $"沒有切換到新開的 {version}：{(installing ? "正在安裝到這台電腦" : "正在套用更新")}，完成後再開一次");
            return;
        }
        handover = new HandoverTarget(executable, release);
        userRequested = false;
        _ = DrainAsync(staged: null);
    }

    /// <summary>resume.json 的 From：換版是受管理的 release；交棒時開發版也算（build 是 dev）</summary>
    private string? ResumeFrom() => handover != null ? managedRelease ?? AppVersionInfo.Current.Release : managedRelease;

    /// <summary>resume.json 的 To：交棒是新版的 release，換版是已備妥的 release</summary>
    private string? ResumeTo(string? staged) => handover?.Release ?? staged;

    // ───────────────────────────── 排空與交棒 ─────────────────────────────

    /// <summary>計時器（UI 執行緒）：等待中檢查能不能開始；排空中檢查 35 分鐘上限與有沒有對話開始了背景工作</summary>
    internal void Tick()
    {
        if (disposed)
            return;
        switch (stage)
        {
            case Stage.Waiting:
                if (resumeDeletePending)
                    resumeDeletePending = !ResumeFileStore.Delete(dataRoot);
                // 啟動接續還沒交給中樞（主視窗第一次 ready 才交）時不開始：WebView2 可能比第一次 Tick（Normal 15 秒、E2E 1 秒）慢就緒。
                // 就緒前開始的排空會用新 token 覆寫 resume.json，就緒時舊的接續送出又把它刪掉（OnResumeDispatched），
                // 舊接續的提示也會加在屏障還開著、正被停下的對話上；就緒前結束的啟動也不會清掉閃退計數（MarkHealthy 在就緒時才寫）
                if (!healthy && (startupToken != null || startupResume != null))
                    break;
                var status = manager.Status;
                if (AppUpdateRestartRules.CanBegin(Situation(status, userRequested), timings.IdleThreshold))
                {
                    if (RuntimeRestartWanted(status))
                        BeginRuntimeRestart();
                    else
                        _ = DrainAsync(status.StagedRelease!);
                    break;
                }
                // 阻擋原因會隨時間改變（冷卻到期、CLI 開始或結束更新、登入視窗開關、Remote Control 的對話有沒有動靜），沒有別的事件會重送：
                // 變了就重送，橫幅才不會停在舊的說明
                if (DisplayBlocker() != publishedBlocker)
                    Publish();
                break;
            case Stage.Draining:
                if (AppUpdateRestartRules.DrainExpired(drainStartedAt, clock()))
                    Abort($"排空超過 {AppUpdateRestartRules.DrainLimit.TotalMinutes:0} 分鐘", AppUpdateRestartRules.RetryAfterAbort);
                else if (!forced && handover is not { SelfRestart: false } && hub.BackgroundWorkRunning)
                    AbortForBackground();
                break;
        }
    }

    /// <param name="staged">要套用的已備妥 release；交棒（handover 不是 null）時是 null</param>
    private async Task DrainAsync(string? staged)
    {
        stage = Stage.Draining;
        var generation = ++drainGeneration;
        try
        {
            token = StartupOptions.NewToken();
            drainStartedAt = clock();
            drainCancel = new CancellationTokenSource();
            Log.Info(handover switch
            {
                { SelfRestart: true } => "重新啟動 app（這台的 .NET 已更新）：排空對話",
                { } target => $"切換到新開的 {target.Release}（{target.Executable}）：排空對話",
                _ => $"開始套用新版 {staged}：排空對話",
            });
            resumeDeletePending = false;   // 這次排空會用新的 token 覆寫 resume.json，不能再把它刪掉
            WriteResume();
            Publish();
            var preparing = hub.PrepareForAppUpdateAsync(drainCancel.Token);
            if (forced)
                hub.ForceAppUpdate();   // 屏障在 PrepareForAppUpdateAsync 同步的部分就建好了
            var preparation = await preparing;
            if (!IsCurrentDrain(generation, Stage.Draining))
                return;   // 已放棄、已取消或 app 正在結束（之後可能已經開始了新的一次）
            if (!preparation.Ready)
            {
                Abort(preparation.Reason ?? "沒有辦法停下所有對話", AppUpdateRestartRules.RetryAfterAbort);
                return;
            }
            await HandOffAsync(staged, generation);
        }
        catch (Exception ex)
        {
            // 沒預料到的例外不能讓階段停在排空或交棒（屏障一直不收、送出一直排隊、橫幅一直顯示重新啟動中）
            Log.Error("套用新版失敗", ex);
            if (generation == drainGeneration && stage is Stage.Draining or Stage.HandingOff)
                Abort($"套用新版失敗：{ex.Message}", AppUpdateRestartRules.RetryAfterAbort);
        }
    }

    private bool IsCurrentDrain(int generation, Stage expected) => generation == drainGeneration && stage == expected;

    /// <summary>
    /// 排空中有對話開始了背景工作（開始時沒有：AppRestartBlocker.BackgroundWork 擋著）：放棄這次、已停下的就地接續，等背景工作都結束再來
    /// （2026-10-28 使用者要求桌面版換版一直等到背景工作都完成。以前排空等它最多 30 分鐘、滿了停掉，先停下的對話這段時間一直停著不動；
    /// 2026-10-28 00:14 三個執行中的對話被停下、空等另外五個對話的背景工作，使用者只好取消）。
    /// 按過「立即重啟」的照樣記著：背景工作都結束就馬上再開始。交棒（使用者開了新版）不在這裡：照中樞的規則等背景工作
    /// </summary>
    private void AbortForBackground()
    {
        var requested = userRequested;
        Abort("排空中有對話開始了背景工作，等背景工作都結束後再重啟", AppUpdateRestartRules.RetryAfterBackground);
        userRequested = requested;
    }

    private async Task HandOffAsync(string? staged, int generation)
    {
        stage = Stage.HandingOff;
        var handoffToken = token!;
        Publish();
        var drafts = await hub.RequestDraftSnapshotAsync(AppUpdateRestartRules.DraftSnapshotTimeout);
        if (!IsCurrentDrain(generation, Stage.HandingOff))
            return;
        var resumeUi = hub.BuildResumeUi(drafts);
        resumeUi.Popouts.AddRange(shell.CapturePopouts());
        // 藏在系統匣或最小化時新版照樣藏著：使用者不在，換版不能把視窗叫出來（交棒是使用者剛開了新版，一律顯示）
        resumeUi.MainHidden = handover == null && shell.MainWindowHidden;
        var file = hub.BuildAppResume(handoffToken, ResumeFrom()!, ResumeTo(staged));
        file.Ui = resumeUi;
        // 背景活動日誌：交棒給新版之前，每個停下的對話怎麼停的、新版會不會接續（resume.json 的內容）
        hub.JournalApp("update.handoff", new JObject
        {
            ["from"] = file.From,
            ["to"] = file.To,
            ["drainSec"] = Math.Round((clock() - drainStartedAt).TotalSeconds, 1),
            ["deferredSends"] = file.DeferredSends.Count,
            ["conversations"] = new JArray(file.Conversations.Select(c => new JObject
            {
                ["conv"] = c.Id,
                ["mode"] = c.Mode.ToString(),
                ["whyNot"] = c.WhyNot?.ToString(),
                ["backgroundStopped"] = c.BackgroundWorkStopped,
                ["interrupted"] = c.InterruptedTools.Count,
                ["pending"] = c.PendingRequests.Count,
                ["risk"] = c.RiskOperation,
            })),
        });
        try
        {
            ResumeFileStore.Save(dataRoot, file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 新版不知道要接續哪些對話：不換版，就地接續（設計第 12 節）
            Abort($"resume.json 寫不進去：{ex.Message}", AppUpdateRestartRules.RetryAfterAbort);
            return;
        }

        var keepDrafts = resumeUi.NewConversations.Select(d => d.DraftId).ToHashSet(StringComparer.Ordinal);
        if (handover is { } target)
        {
            // 交棒：不經換版幫手，直接啟動新版（它等這一份結束、放開 mutex 後接手，照 resume.json 接續）
            stage = Stage.Exiting;
            Log.Info(target.SelfRestart ? $"以 {target.Executable} 重新啟動，結束這一份" : $"交棒給 {target.Release}，結束這一份");
            if (shell.HandOver(target.Executable, InstanceHandoverRules.RelaunchArguments(handoffToken, usesDefaultData ? null : dataRoot), keepDrafts))
                return;
            stage = Stage.HandingOff;
            Abort($"沒有辦法啟動 {target.Executable}", retryDelay: null);
            return;
        }

        helperPid = shell.StartHelper(layout.Executable(managedRelease!), HelperArguments(staged!, handoffToken, noRelaunch: false));
        if (helperPid == null)
        {
            Abort("沒有辦法啟動換版幫手", AppUpdateRestartRules.RetryAfterHelperFailure);
            return;
        }
        var deadline = DateTime.UtcNow + timings.ArmedTimeout;
        while (!File.Exists(layout.ArmedFile(handoffToken)))
        {
            if (!IsCurrentDrain(generation, Stage.HandingOff))
                return;   // app 正在結束（PrepareExit 已結束這個幫手）
            if (DateTime.UtcNow >= deadline)
            {
                Abort("換版幫手沒有回應", AppUpdateRestartRules.RetryAfterHelperFailure);   // Abort 先結束幫手，再就地接續
                return;
            }
            await Task.Delay(100);
        }
        helperPid = null;   // 幫手已就緒：換版交給它
        stage = Stage.Exiting;
        Log.Info($"換版幫手已就緒，結束以套用 {staged}");
        shell.ExitForUpdate(keepDrafts);
    }

    /// <summary>
    /// 放棄這次：還沒寫 .armed 的幫手先結束（晚到的幫手會在等舊程序 60 秒後強制結束 app、以「新版啟動失敗」重開舊版，
    /// 而 resume.json 已刪、剛就地接續的對話跟著被結束），已停下的對話就地接續、刪掉 resume.json
    /// </summary>
    /// <param name="retryDelay">多久之後再自動開始（null＝不設：使用者取消另有 6 小時的暫停）</param>
    private void Abort(string reason, TimeSpan? retryDelay)
    {
        if (handover is { SelfRestart: true })
        {
            // 重新啟動（.NET 已被移除）沒有做成：照 app 更新的冷卻再試（使用者取消另有 6 小時的暫停），不另外通知。
            // 啟動不了執行檔這類沒帶冷卻的也等 1 小時：不然閒置時每次 Tick 都再排空一次
            Log.Info($"放棄這次重新啟動：{reason}");
            handover = null;
            retryDelay ??= AppUpdateRestartRules.RetryAfterAbort;
        }
        else if (handover is { } target)
        {
            // 新版已經結束（它把這一份叫醒交棒）：沒換成時告訴使用者，要用新版得再開一次
            Log.Info($"放棄切換到 {target.Release}：{reason}");
            shell.Notify("Clode Myriagent", $"沒有切換到新開的 {ResumeIntentRules.VersionOfRelease(target.Release)}：{reason}");
            handover = null;
            retryDelay = null;   // 交棒不影響 app 自動更新的排程
        }
        else
        {
            Log.Info($"放棄這次 app 更新：{reason}");
        }
        StopWaitingHelper();
        stage = Stage.Waiting;
        userRequested = false;
        forced = false;
        drainCancel?.Cancel();
        hub.AbortAppUpdate(reason);
        if (!ResumeFileStore.Delete(dataRoot))
            resumeDeletePending = true;
        if (token != null)
        {
            try
            {
                File.Delete(layout.ArmedFile(token));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"刪 .armed 失敗：{ex.Message}");
            }
        }
        token = null;
        if (retryDelay is { } delay)
            retryAfter = clock() + delay;
        Publish();
    }

    private void StopWaitingHelper()
    {
        if (helperPid is not { } pid)
            return;
        helperPid = null;
        shell.StopHelper(pid);
    }

    /// <summary>換版幫手的命令列（設計 8.5）：--apply-update --to &lt;新&gt; --wait-pid &lt;自己&gt; --token &lt;token&gt; [--no-relaunch]</summary>
    private static string[] HelperArguments(string staged, string helperToken, bool noRelaunch)
    {
        var args = new List<string> { "--apply-update", "--to", staged, "--wait-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--token", helperToken };
        if (noRelaunch)
            args.Add("--no-relaunch");
        return args.ToArray();
    }

    /// <summary>排空中每處理一步就重寫 resume.json：排空到一半當掉時，下次一般啟動才知道哪些對話被中斷</summary>
    private void WriteResume()
    {
        if (token == null || stage != Stage.Draining || ResumeFrom() is not { } from)
            return;
        try
        {
            ResumeFileStore.Save(dataRoot, hub.BuildAppResume(token, from, ResumeTo(manager.Status.StagedRelease)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"寫 resume.json 失敗：{ex.Message}");
        }
        Publish();
    }

    // ───────────────────────────── 結束 ─────────────────────────────

    /// <summary>
    /// 使用者結束 app（系統匣「結束」）：不再開始或繼續排空（中樞收尾會結束屏障），有備妥的版本就在結束時換版。
    /// 交棒中、幫手還沒寫 .armed 時先結束它，改由結束時換版的幫手只切換、不重新啟動：兩個幫手同時切換 current 會互相干擾，
    /// 先啟動的那個還會在使用者要結束時把 app 重新開起來
    /// </summary>
    public AppExitAction PrepareExit(bool windowsShuttingDown)
    {
        var previous = stage;
        stage = Stage.Exiting;
        drainCancel?.Cancel();
        StopWaitingHelper();
        if (previous == Stage.Exiting)
            return AppExitAction.Plain;   // 已經交棒給幫手（它會換版）或已經在結束：不再啟動第二個幫手
        var status = manager.Status;
        // 檢查或下載較新版的期間 Phase 是 Checking／Downloading，StagedRelease 仍是完整備妥的版本：結束時照樣套用（設計 8.5「有已備妥的版本」、Review Focus 2）；
        // 關掉自動更新、以 --data 執行（啟用條件不成立，含檢查途中才關掉的）就不套用
        var staged = status.StagedRelease != null && status.Phase != AppUpdatePhase.Disabled && managedRelease != null && manager.IsEnabled;
        return AppUpdateRestartRules.OnExit(staged, windowsShuttingDown);
    }

    /// <summary>結束時換版：幫手只切換 current、不重新啟動（--no-relaunch）</summary>
    public void LaunchExitHelper()
    {
        if (manager.Status.StagedRelease is not { } staged || managedRelease == null)
            return;
        shell.StartHelper(layout.Executable(managedRelease), HelperArguments(staged, StartupOptions.NewToken(), noRelaunch: true));
        Log.Info($"結束時換版到 {staged}");
    }

    // ───────────────────────────── 啟動後 ─────────────────────────────

    private void OnMainViewReady()
    {
        if (healthy)
            return;
        healthy = true;
        // 健康訊號檔只給 --after-update：以 --after-update-failed 啟動時幫手已經結束，寫了只會留下孤兒檔
        if (managedRelease != null)
            LaunchGuard.MarkHealthy(layout, managedRelease, afterUpdateToken);
        if (startupResume is { } file && file.Ui.Popouts.Count > 0)
            shell.OpenPopouts(file.Ui.Popouts);
        manager.RefreshFromState();
        // init.appUpdate 在這之前送出，可能是舊的：送一次最新狀態（以 --after-update-failed 啟動時的冷卻已在 ScheduleStartupResume 依啟動當下的狀態決定，
        // 這裡不再依目前的 StagedRelease 判斷：它可能是第一次檢查重新備妥的，不是幫手沒消耗的那份）
        Publish();
        // 閃退保護退回之後的第一次啟動：沒有 token，只看 updater-state 的最近結果
        if (startupToken == null && manager.Status is { LastResult: { } result, LastResultAt: { } at } && clock() - at < TimeSpan.FromMinutes(10))
            shell.Notify("Clode Myriagent", result);
    }

    private void OnResumeDispatched(AppResumeSummary summary)
    {
        // 啟動時讀不到的 resume.json 不刪（JsonFile 的約定：讀不到的檔不可覆寫）：這次沒有照它接續，
        // 留給下次啟動以 token 不符給「繼續」、還原草稿（啟動清理這次也沒刪它列的草稿資料夾）
        if (!startupResumeUnreadable && !ResumeFileStore.Delete(dataRoot))
            resumeDeletePending = true;
        if (startupToken == null)
            return;
        var version = summary.AppTo ?? AppVersionInfo.VersionText;
        // 退回時說明哪一版啟動失敗（設計 8.5 第 6 步「新版 X 啟動失敗，已退回 Y」）；沒有 resume.json 就不知道是哪一版
        // 交棒給同一版（.NET 被移除後重新啟動）：寫「已重新啟動」
        var head = startedAfterHandover ? startupResume is { } resumed && resumed.From != null && resumed.From == resumed.To ? $"已重新啟動 {version}" : $"已切換到 {version}"
            : !summary.RolledBack ? $"已更新到 {version}"
            : startupResume?.To is { } failed ? $"新版 {ResumeIntentRules.VersionOfRelease(failed)} 啟動失敗，已退回 {version}"
            : $"新版啟動失敗，已退回 {version}";
        var tail = summary.Continued > 0 ? $"；{summary.Continued} 個對話已自動接續" : string.Empty;
        if (startupResumeUnreadable)
            tail += $"；{ResumeIntentRules.UnreadableResumeNotice}";
        shell.Notify("Clode Myriagent", head + tail);
    }

    // ───────────────────────────── 網頁狀態 ─────────────────────────────

    private void Publish()
    {
        if (disposed)
            return;
        var state = RenderState();
        publishedBlocker = state.Value<string>("blocker");
        JournalState();
        hub.SetAppUpdateState(state);
        AppStateChanged?.Invoke(AppState());
    }

    /// <summary>
    /// 背景活動日誌（docs/background-journal.md）：階段或阻擋原因變了才記 update.state——換版為什麼一直沒有開始（誰在擋、擋了多久）從前後兩筆看得出來
    /// </summary>
    private void JournalState()
    {
        if (journaledState is { } last && last.Stage == stage && last.Blocker == publishedBlocker)
            return;
        journaledState = (stage, publishedBlocker);
        hub.JournalApp("update.state", new JObject
        {
            ["stage"] = stage.ToString(),
            ["blocker"] = publishedBlocker,
            ["staged"] = manager.Status.StagedRelease,
            ["userRequested"] = userRequested ? true : null,
            ["forced"] = forced ? true : null,
        });
    }

    /// <summary>app.state（U3，設計 7.8）：每次發布網頁狀態時一併算出（UI 執行緒）；內容沒變的由同步那邊擋下（HubBridge）</summary>
    public event Action<AppStatePayload>? AppStateChanged;

    /// <summary>還沒檢查過時 app.state 的 checkedAt（這次啟動的時間，內容才不會每次都變）</summary>
    private readonly long startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private AppStatePayload AppState()
    {
        string? stagePhase = stage switch
        {
            Stage.Draining => "draining",
            Stage.HandingOff or Stage.Exiting => "restarting",
            _ => installing ? "installing" : null,
        };
        return AppStateRules.Build(stagePhase, manager.Status, publishedBlocker != null, AppVersionInfo.Current, startedAtMs);
    }

    /// <summary>AppUpdateView（docs/web-protocol.md）</summary>
    private JObject RenderState()
    {
        var status = manager.Status;
        var identity = AppVersionInfo.Current;
        string phase = stage switch
        {
            Stage.Draining => "draining",
            Stage.HandingOff or Stage.Exiting => "restarting",
            _ => installing ? "installing" : status.Phase.ToString().ToLowerInvariant(),
        };
        return new JObject
        {
            ["version"] = identity.Version.ToString(),
            ["build"] = identity.IsDev ? null : identity.Build,
            ["display"] = AppVersionInfo.Display,
            ["commitAt"] = AppVersionInfo.CommitAtMs,
            ["managed"] = managedRelease != null,
            ["canInstall"] = CanInstallHere(),
            ["phase"] = phase,
            ["available"] = ResumeIntentRules.VersionOfRelease(status.AvailableRelease),
            ["staged"] = ResumeIntentRules.VersionOfRelease(status.StagedRelease),
            ["downloaded"] = status.DownloadedBytes,
            ["total"] = status.TotalBytes,
            ["detail"] = status.Detail,
            ["blocker"] = DisplayBlocker(),
            ["waiting"] = Draining ? hub.AppUpdateWaitingCount : 0,
            // 強制更新：網頁說明幾點起還沒停下的會被強制停下（排空中才有）
            ["forced"] = forced && Draining ? true : null,
            ["forceAt"] = Draining && hub.AppUpdateForceAt is { } forceAt ? new DateTimeOffset(forceAt).ToUnixTimeMilliseconds() : null,
            // 重新啟動成同一版不算「切換到新開的」：網頁照 app 更新的說法（正在套用更新、正在重新啟動以套用更新）
            ["handoverTo"] = handover is { SelfRestart: false } ? ResumeIntentRules.VersionOfRelease(handover.Release) : null,
            ["lastCheckAt"] = status.LastCheckAt is { } check ? new DateTimeOffset(check).ToUnixTimeMilliseconds() : null,
            ["lastResult"] = status.LastResult,
            ["lastResultAt"] = status.LastResultAt is { } at ? new DateTimeOffset(at).ToUnixTimeMilliseconds() : null,
        };
    }

    /// <summary>
    /// 設計 9.1 的處境（Tick 判斷能不能開始、橫幅說明阻擋原因共用）。
    /// 檢查或下載較新版的期間 Phase 是 Checking／Downloading：刻意等它結束再排空。這時先套用已備妥的 A，換版之後 B 要重新下載
    /// （幫手的 OnSwitched 會清掉 Staged），還要再排空、重啟一次；B 失敗或被擋時 SetProblem 會改回 Staged，所以延後的時間有上限。
    /// 啟用條件再確認一次（Phase 已反映它；剛關掉自動更新、重新檢查還沒改 Phase 的那一刻也不開始）。
    /// 桌面沒人時使用者可能正在手機上用 Remote Control：開著 Remote Control 的對話在用也算使用者在用（設計 9.1 條件 1），
    /// 只在有要套用的版本時才看（沒有時它和更新無關，橫幅的原因也不必跟著它一直重送）。執行列正在建置時也不自動開始（結束 app 會中斷建置）；
    /// 有 Debug 模式執行中的程式時也不自動開始（它是 app 的子程序，重啟會一起結束它；Release 模式的和 app 脫鉤，不算）
    /// </summary>
    private AppRestartSituation Situation(AppUpdateStatus status, bool requested)
    {
        var now = clock();
        // 有要套用的：已備妥的新版，或這個程序的 .NET 已被移除、要重新啟動（RuntimeRestartWanted；阻擋與閒置的條件同 app 更新）
        bool staged = (status.Phase == AppUpdatePhase.Staged && status.StagedRelease != null && managedRelease != null && manager.IsEnabled)
                      || RuntimeRestartWanted(status);
        return new AppRestartSituation(staged, shell.AnyWindowForeground, shell.SinceLastInput, shell.CliUpdating, shell.LoginInProgress, installing,
            now, snoozedUntil, retryAfter, requested,
            RemoteInUse: staged && hub.RemoteControlInUse(now, timings.RemoteIdleThreshold, timings.RemoteWaitThreshold),
            LaunchBuilding: staged && hub.LaunchBuilding,
            LaunchDebugging: staged && hub.LaunchDebugging,
            RemoteExecRunning: staged && hub.RemoteExecRunning,
            BackgroundWork: staged && hub.BackgroundWorkRunning,
            Forced: forced);
    }

    /// <summary>
    /// 這個程序的 .NET 已被移除（.NET 更新刪掉了啟動時用的修補版，2026-10-27）：之後第一次用到還沒載入的組件就失敗，新版也解壓不了。
    /// 受管理的安裝、開著自動更新、沒有已備妥的版本（有的話照常換版：換版幫手是新的程序）時，閒置時重新啟動 app（交棒給同一版自己）
    /// </summary>
    private bool RuntimeRestartWanted(AppUpdateStatus status)
        => managedRelease != null && manager.IsEnabled && status.StagedRelease == null && status.Phase != AppUpdatePhase.Disabled
           && manager.RuntimeRemoved != null;

    /// <summary>
    /// 重新啟動成同一版（RuntimeRestartWanted）：照交棒的流程排空對話、寫 resume.json，以 --handed-over 啟動目前這一版的執行檔後結束；
    /// 新程序用新的 .NET，照 resume.json 接續、之後照常檢查與套用新版
    /// </summary>
    private void BeginRuntimeRestart()
    {
        Log.Info($"這台的 .NET 已更新（啟動時用的 {manager.RuntimeRemoved} 已被移除）：閒置中，重新啟動 app");
        handover = new HandoverTarget(layout.Executable(managedRelease!), managedRelease!, SelfRestart: true);
        userRequested = false;
        _ = DrainAsync(staged: null);
    }

    /// <summary>橫幅與設定頁說明的阻擋原因（null＝沒有）：不算「立即重啟」，冷卻中照樣說明</summary>
    private string? DisplayBlocker()
    {
        var blocker = AppUpdateRestartRules.Blocker(Situation(manager.Status, requested: false));
        return blocker == AppRestartBlocker.None ? null : AppUpdateRestartRules.DescribeBlocker(blocker);
    }

    private void Post(Action action)
    {
        if (ui != null)
            ui.Post(_ => action(), null);
        else
            action();
    }

    /// <summary>app 結束時（AppController.ExitImmediately）：停止計時器與事件；交棒中還沒寫 .armed 的幫手一併結束（登出、初始化失敗時不換版）</summary>
    public void Dispose()
    {
        if (disposed)
            return;
        StopWaitingHelper();
        stage = Stage.Exiting;
        disposed = true;
        timer?.Dispose();
        drainCancel?.Cancel();
        manager.Changed -= managerChanged;
        hub.AppDrainProgress -= WriteResume;
        hub.MainViewReady -= OnMainViewReady;
        hub.AppResumeDispatched -= OnResumeDispatched;
        if (ReferenceEquals(hub.AppUpdateCommands, this))
            hub.AppUpdateCommands = null;
    }
}
