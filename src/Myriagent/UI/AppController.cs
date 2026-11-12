using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Background;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Files;
using Myriagent.Git;
using Myriagent.Health;
using Myriagent.Hub;
using Myriagent.Launch;
using Myriagent.RemoteExec;
using Myriagent.Speech;
using Myriagent.SystemStatus;
using Myriagent.Sync;
using Myriagent.Update;
using Myriagent.Voice;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace Myriagent.UI;

/// <summary>
/// app 的生命週期與 WinForms 外殼：建立各服務（CLI、帳戶、中樞、縮圖）、主視窗、彈出視窗、系統匣；實作中樞需要的宿主能力。
/// 關閉主視窗時（設定允許）縮到系統匣、對話在背景繼續；從系統匣「結束」才真正離開。
/// </summary>
internal sealed class AppController : ApplicationContext, IAgentHost, IAppUpdateShell, IWindowHost
{
    private readonly SynchronizationContext ui;
    private readonly HttpClient http;
    private readonly NotifyIcon tray;

    /// <summary>主視窗工作列按鈕的進度與疊加數字（執行中綠色流動、等你回應琥珀色；TaskbarRules）</summary>
    private readonly TaskbarProgress taskbar = new();
    private readonly TrayIconBadge trayBadge = new();
    private readonly List<PopoutForm> popouts = new();
    private readonly MainForm mainForm;

    /// <summary>主視窗以外另外開的完整視窗（多視窗，像 Chrome）；設定在 settings.json 的 Windows</summary>
    private readonly List<MainForm> extraWindows = new();

    /// <summary>上次結束時開著的另外的視窗已經開回來了（開機自動啟動藏在系統匣時，等主視窗第一次顯示才開）</summary>
    private bool extraWindowsRestored;
    private readonly AppStartup appStartup;
    private readonly AppUpdateManager updateManager;
    private readonly AppUpdateCoordinator updateCoordinator;
    private string? lastNotifiedConversation;

    /// <summary>交棒中啟動的換版幫手：留著控制代碼（不釋放），放棄時以它結束幫手，PID 不會被別的程序重用</summary>
    private readonly UpdateHelperProcess updateHelper = new();

    /// <summary>這台電腦的狀態（電腦狀態面板與 stat1；有視窗開著面板時才取樣）</summary>
    private readonly SystemMonitor systemMonitor = new();

    /// <summary>語音通知（Windows 內建語音；第一次要念時才建立聲音引擎）</summary>
    private readonly VoiceAnnouncer voice = new(() => SapiSpeechEngine.Create());

    /// <summary>輸入區語音訊息的本機辨識（faster-whisper 的常駐程序，第一則語音才啟動，閒置 10 分鐘結束）</summary>
    private readonly WhisperVoiceTranscriber voiceTranscriber = new(Path.Combine(AppPaths.Root, "voice"));

    /// <summary>背景活動日誌（logs\background\，docs/background-journal.md）：子代理、背景工作、背景程式與換版排空的經過</summary>
    private readonly BackgroundJournalFile backgroundJournal = new(AppPaths.BackgroundJournalDirectory);

    /// <summary>「安裝到這台電腦」之後，結束時要啟動的安裝好的那份（使用者在結束確認框按「否」就清掉）</summary>
    private string? launchAfterExit;

    /// <summary>最近一則系統匣通知（陸續到的 CLI 換版彙總合併成一則用）；顯示其他氣球提示後清掉</summary>
    private TrayNotice? lastTrayNotice;
    private bool trayHintShown;
    private bool exitPromptOpen;

    /// <summary>延後啟動同步的計時器（第一次 ViewLoaded 後 2.5 秒、備援 10 秒）；結束時停掉</summary>
    private readonly List<System.Windows.Forms.Timer> syncStartTimers = new();
    private bool syncEventsAttached;

    /// <param name="startup">從程序啟動起算的計時（實機探針量啟動時間用）</param>
    /// <param name="appStartup">命令列與安裝佈局（app 自我更新用）</param>
    public AppController(Stopwatch startup, AppStartup appStartup)
    {
        this.appStartup = appStartup;
        // 建立任何控制項之前就裝好 WinForms 同步內容：帳戶初始化的 await 要回到 UI 執行緒（中樞與帳戶狀態只在 UI 執行緒讀寫）
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        ui = SynchronizationContext.Current!;
        AgentWebView.PrewarmEnvironment();
        Settings = AppSettings.Load();
        http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        { Timeout = TimeSpan.FromMinutes(10) };
        // 版本一律讀 InformationalVersion（AppVersionInfo，設計 6.1）：User-Agent 與同步的 Hello 帶同一個版本
        var version = AppVersionInfo.VersionText;
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Myriagent", version));

        Thumbnails = new ThumbnailService();
        AgentHub? hubRef = null;
        // 實機量測的「不找 CLI」模式（ProbeRules）：不找、不執行這台電腦的任何 CLI，帳戶一律未安裝
        Cli = ProbeRules.NoCliRequested()
            ? new CliManager(http, provider => hubRef?.HasRunningSessions(provider) ?? false, () => false, startBackgroundChecks: false, ProbeRules.NoCliOptions())
            : new CliManager(http, provider => hubRef?.HasRunningSessions(provider) ?? false, () => Settings.AutoUpdateCli);
        Accounts = new AccountManager(BeginInvoke, Cli, http, id => hubRef?.RunningForAccount(id) ?? 0);
        // 閒置的 Claude 帳戶權杖過期時讓 CLI 換發（設定頁可關）；實機探針一律不換發（不替真實帳戶花額度）
        bool probing = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ProbeRules.ProbeVariable));
        Accounts.ClaudeTokenRefreshEnabled = () => !probing && Settings.ClaudeIdleTokenRefresh;
        Accounts.ExtraClaudeUsageSources = AccountManager.SystemDefaultClaudeUsageSources;
        // 隔離帳戶共用這台系統預設設定資料夾的全域指示、記憶、MCP、skills（帳戶選單可個別關掉）；實機探針不碰真的 ~/.claude、~/.codex
        Accounts.Shared = probing ? null : new SharedCustomizations();
        var store = new ConversationStore(AppPaths.ConversationsDirectory);
        // 上次沒有正常結束（登出、關機、當掉）時沒送出的草稿留下的附件複本：趁還沒有任何草稿時找出來，之後在背景刪掉。
        // app 更新前沒送出的草稿（resume.json 列出的）不是殘留：新版要還原它們的附件；resume.json 被別的程式開著、讀不到時（重試幾秒仍不行）這次一個都不刪
        var startupResume = AppUpdateCoordinator.LoadStartupResume(AppPaths.Root);
        var draftLeftovers = ResumeIntentRules.DraftLeftoversToDelete(store.FindDraftLeftovers(), startupResume);
        Hub = new AgentHub(store, this, Accounts, Cli, Settings);
        hubRef = Hub;
        // 背景活動日誌：先接上，之後的掛鉤（背景程式、換版、接續）一開始就記得到
        Hub.AttachBackgroundJournal(backgroundJournal);
        Hub.JournalApp("app.start", new Newtonsoft.Json.Linq.JObject
        {
            ["release"] = AppVersionInfo.Current.Release,
            ["build"] = AppVersionInfo.Current.Build,
            ["afterUpdate"] = startupResume != null,
            ["dataRoot"] = AppPaths.Root,
        });
        Hub.AttachSystemMonitor(systemMonitor);
        // 對話工作資料夾的 git 狀態（只讀，不 fetch；標頭資料夾晶片旁的 git 晶片）
        Hub.AttachGitStatus(new GitStatusReader());
        Hub.AttachGitTurnReader(new GitTurnReader());
        // 對話開出、這一輪結束後還在跑的背景程式（帳戶卡的標示；每 15 秒與每一輪結束時列一次程序，只讀）
        Hub.AttachBackgroundPrograms(BackgroundProgramMonitor.CreateDefault());
        // 執行列：工作資料夾是視窗程式專案時，輸入區上方的啟動、暫停、停止、重新建置並執行（執行的程式和 app 脫鉤，記在 launch\runs.json、重啟後接回）
        Hub.AttachLaunch(new LaunchScanner(), new LaunchProcessStarter(), Path.Combine(AppPaths.Root, "launch"));
        // 對話框的燈號：這個對話的資料流與硬碟（CLI 程序樹的 I/O）、整台電腦的網路，連動真實的計數器
        Hub.AttachActivity(new ActivitySampler());
        // 儀表板的健康度：各帳戶每次對話都會載入的指示、記憶、skill、MCP，與電腦層級的指示檔要不要合併（只讀）
        Hub.AttachHealth(new HealthScanner(new HealthMachine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))));
        // 排程頁：這台的 Windows 排程（工作排程器的 COM，只讀；快速操作才改），紀錄在 <資料根目錄>\schedules\
        Hub.AttachSchedules(new Myriagent.Schedules.TaskSchedulerReader(new[] { Path.Combine(AppPaths.Root, "conversations") }), Path.Combine(AppPaths.Root, "schedules"));
        // 排程的回溯（S2）：各帳戶的 CLI 對話紀錄裡建立排程的指令（只讀、背景低優先）
        Hub.AttachScheduleTranscripts(Hub.DefaultTranscriptRoots);
        // 輸入區的語音訊息：錄音在這台以 faster-whisper 轉成逐字稿，連錄音檔交給代理（不呼叫 Windows 的語音輸入）
        Hub.AttachVoice(voiceTranscriber);
        // 回覆裡顯示的圖片與影片在對話資料夾留一份（attachments\.kept\）：原檔之後被刪或被覆寫，對話裡照樣看得到當時那一張
        Hub.AttachMediaKeeper();
        // 上次下載或匯入網頁上傳途中被關閉（當機、重開機）留下的暫存：同步還沒接上、沒有任何上傳在下載，在背景清掉（WP4.7 合併前審查 R6）
        _ = Task.Run(() => AgentHub.CleanStaleUploadTemp());
        Accounts.Initialize();
        if (Settings.ShareCrossCliCustomizations && Accounts.Shared is { } bootShared)
            _ = Task.Run(() => bootShared.SyncCrossCli());
        // 多電腦同步：建構子不做 I/O，延後到主視窗載入之後才 Start（未配對時不建引擎、不載入 SignalR 與 SQLite）
        Sync = new SyncCoordinator(Hub, Accounts, this, Settings, SyncPaths.FromAppPaths(),
            SyncCoordinatorOptions.FromEnvironment(version), TimeProvider.System);
        Hub.AttachSync(Sync);
        // 遠端執行指令（docs/sync-protocol.md 14.11）：被操作時執行其他電腦的代理下的指令（高風險的在這台問人）；
        // 發起時以本機管道收對話裡 myriagent-remote 的要求（管道名稱與權杖經對話 CLI 的環境變數給，myriagent-remote.exe 的資料夾放到 PATH 最前面）
        remoteExec = new RemoteExecService(Sync, new RemoteExecConfirmer(this), Path.Combine(Path.GetTempPath(), "myriagent-remote-exec"),
            RemoteExecPipeRules.PipeName(AppPaths.Root), ConversationTitleAsync);
        Hub.AttachRemoteExec(remoteExec.Runner);
        // 正在和其他電腦傳檔案時 app 自我更新不自動重啟（AppRestartBlocker.FileTransfer）
        Hub.AttachFileTransferActivity(() => Myriagent.Sync.FileTransferActivity.Active);
        Sync.AttachRemoteExec(remoteExec);
        var remoteTool = Path.Combine(AppContext.BaseDirectory, RemoteExecHubRules.ToolFileName);
        var remoteSync = Sync;
        Hub.AttachRemoteExecIssuer(new RemoteExecIssuerInfo(remoteExec.PipeName, remoteExec.TokenFor,
            File.Exists(remoteTool) ? Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) : null, () => remoteSync.IsPairedSnapshot));
        // 定時訊息（myriagent-schedule，AgentHub.TimedMessages.cs）：同一條管道、同一個對話權杖，在 UI 執行緒處理
        remoteExec.TimedHandler = (conversationId, args) => InvokeOnUiAsync(() => Hub.HandleTimedCli(conversationId, args));
        Hub.TimedToolAvailable = File.Exists(Path.Combine(AppContext.BaseDirectory, TimedHubRules.ToolFileName));
        remoteExec.StartPipe();
        // 遠端桌面（docs/sync-protocol.md 14.16）：被看時以 GDI 擷取、SendInput 注入，主螢幕上方顯示提示列（不出現在傳出去的畫面）
        Hub.AttachRemoteDesk(Sync, () => new Myriagent.RemoteDesk.DxgiDeskScreen(), (text, disconnect) => RemoteDeskBanner.ShowFor(text, disconnect));
        Sync.AttachDesk(Hub.DeskEndpoint);
        Accounts.UsagePollPolicy = Sync;
        Sync.StatusChanged += OnSyncStatusChanged;
        // 升級後第一次啟動：補上 Antigravity、Grok 的本機預設帳戶（各只補一次；帳戶清單這次唯讀時下次再補）
        if (!Settings.AntigravityAccountAdded && Accounts.EnsureSystemDefault(AgentProvider.Antigravity))
        {
            Settings.AntigravityAccountAdded = true;
            Settings.Save();
        }
        if (!Settings.GrokAccountAdded && Accounts.EnsureSystemDefault(AgentProvider.Grok))
        {
            Settings.GrokAccountAdded = true;
            Settings.Save();
        }
        // app 自我更新：已配對的電腦向伺服器的發佈來源（U3 的 HubReleaseSource，裝置的存取權杖）檢查新版；update-e2e 照舊用本機假來源（設計 8.2）
        var managed = appStartup.ManagedRelease;
        var sync = Sync;
        updateManager = new AppUpdateManager(appStartup.Layout, AppVersionInfo.Current,
            UpdateEnvironment.E2ESource is { } e2eSource ? new FileReleaseSource(e2eSource) : new HubReleaseSource(() => sync.ReleaseHttp),
            () => InstallLayoutRules.Evaluate(managed != null, appStartup.UsesDefaultData, Settings.AutoUpdateApp, paired: sync.IsPairedSnapshot,
                hasTestSource: UpdateEnvironment.E2ESource != null, e2e: UpdateEnvironment.IsE2E),
            ReleaseKeys.Trusted(allowTestKey: UpdateEnvironment.IsE2E), new RegistryRuntimeProbe());
        updateCoordinator = new AppUpdateCoordinator(Hub, updateManager, this, appStartup.Layout, AppPaths.Root, managed, appStartup.UsesDefaultData,
            UpdateEnvironment.E2EFast ? AppUpdateTimings.E2E : AppUpdateTimings.Normal);
        updateCoordinator.ScheduleStartupResume(appStartup.Options, startupResume);
        // 開機時自動啟動（設定頁）：只有受管理的安裝、預設資料夾能設定；登錄指向 current，換版不用改
        Hub.AutoStart = new AutoStartRegistration(appStartup.Layout.CurrentExecutable,
            AutoStartRules.IsAvailable(managed != null, appStartup.UsesDefaultData, UpdateEnvironment.IsE2E));
        // U3：伺服器說有新版（Hello 的 latestApp、ServerNotice{appRelease}）就提早檢查（每 10 分鐘最多一次，設計 7.4）；
        // 這台的更新狀態以 app.state 回報（設計 7.8，內容沒變不送）
        sync.AppReleaseAnnounced += updateCoordinator.CheckSoon;
        updateCoordinator.AppStateChanged += sync.OfferAppState;
        if (draftLeftovers.Count > 0)
            _ = Task.Run(() => DeleteDraftLeftovers(store, draftLeftovers));

        tray = new NotifyIcon
        {
            Icon = trayBadge.Base,
            Text = "Clode Myriagent",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };
        tray.DoubleClick += (_, _) => ShowMain();
        // 點通知：對話的通知回到那個對話；沒有對話的（CLI 換版彙總、背景執行的提示）只開主視窗
        tray.BalloonTipClicked += (_, _) => ShowConversation(lastNotifiedConversation);

        mainForm = new MainForm(this);
        MainForm = mainForm;
        // 多視窗：開新視窗、拖曳分頁放開時看游標在哪個視窗（AgentHub.Windows.cs）
        Hub.AttachWindows(this);
        ProbeRunner.AttachIfRequested(this, mainForm, startup);
        // 同步在主視窗第一次載入 2.5 秒後才啟動；一直沒有載入（WebView 初始化失敗）時 10 秒後也啟動（Start 只有第一次有效）
        mainForm.ViewLoaded += OnFirstViewLoaded;
        ScheduleSyncStart(SyncStartupRules.FallbackDelay);
        // 網路恢復、從睡眠喚醒：請同步引擎立刻重試（離線退避中才有作用；事件在背景執行緒觸發，協調者自己轉回 UI 執行緒）
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        syncEventsAttached = true;
        // Windows 切換深淺色時，主題「跟隨系統」的標題列與底色跟著換（網頁自己以 prefers-color-scheme 切換）
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        // 沒有人看得到畫面時（鎖定、螢幕關閉）網頁不畫（ScreenPresenceRules）。探針模式不接：探針要截圖，跑探針時電腦常是鎖著的
        // （MYRIAGENT_AGENT_PROBE_PRESENCE=1 時照接：在真的 WebView2 裡驗證鎖定時停畫、頁面照樣載入）
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MYRIAGENT_AGENT_PROBE"))
            || Environment.GetEnvironmentVariable("MYRIAGENT_AGENT_PROBE_PRESENCE") == "1")
        {
            screenPresence = new ScreenPresence(mainForm, () => Hub.RemoteDeskActive);
            screenPresence.Changed += AgentWebView.SetScreenUnseen;
            Hub.RemoteDeskActiveChanged = screenPresence.Refresh;
            if (lastDisplayState is { } display)
                screenPresence.OnDisplayState(display);
            AgentWebView.SetScreenUnseen(screenPresence.Unseen);
        }
        // Start() 同步送出一次狀態（網頁狀態的 blocker 讀 AnyWindowForeground）：主視窗要先建立
        updateCoordinator.Start();
        // WebView2 環境已在最前面預熱；介面初始化排到訊息迴圈開始後才執行：缺少執行階段時環境工作會同步失敗，
        // 錯誤處理（提示後 ExitImmediately 關閉主視窗）若在建構子裡就跑完，接著的 Show() 與 Application.Run 會碰到已釋放的主視窗而當掉
        BeginInvoke(mainForm.StartInitialization);
        // 換版幫手啟動的這一次、交棒前主視窗藏著（系統匣、最小化）：照樣藏著（設計 9.3），使用者不在時不把視窗叫到面前。
        // Application.Run 顯示主視窗的那一次由 MainForm 擋下；訊息迴圈開始後（排在 Application.Run 的顯示之後）恢復，之後系統匣與再次啟動照常叫得出來
        if (ResumeIntentRules.StartMainHidden(startupResume.File, appStartup.Options.AfterUpdateToken ?? appStartup.Options.AfterUpdateFailedToken))
        {
            Log.Info("換版前主視窗藏著：這次啟動不顯示主視窗");
            mainForm.KeepHiddenOnStart();
            BeginInvoke(mainForm.StopKeepingHidden);
            // 另外開的視窗照換版前的樣子開回來（看得到的照樣看得到、最小化的照樣最小化；不搶焦點）
            BeginInvoke(RestoreExtraWindows);
        }
        else if (AutoStartRules.StartHidden(appStartup.Options.AutoStart, Settings.CloseToTray))
        {
            // 登入 Windows 時自動啟動：只留系統匣圖示，點圖示或再開一次 app 才顯示（同上，訊息迴圈開始後恢復）
            Log.Info("開機時自動啟動：主視窗藏在系統匣");
            mainForm.KeepHiddenOnStart();
            BeginInvoke(mainForm.StopKeepingHidden);
        }
        else
        {
            mainForm.Show();
            BeginInvoke(RestoreExtraWindows);
        }
    }

    public AppSettings Settings { get; }

    public CliManager Cli { get; }

    public AccountManager Accounts { get; }

    public AgentHub Hub { get; }

    public ThumbnailService Thumbnails { get; }

    /// <summary>多電腦同步的協調者（未配對時也有：配對指令、用量查詢閘門）</summary>
    public SyncCoordinator Sync { get; }

    /// <summary>遠端執行指令的桌面端（被操作與發起）</summary>
    private readonly RemoteExecService remoteExec;

    /// <summary>對話標題（myriagent-remote 的管道在背景執行緒問；回 UI 執行緒讀）</summary>
    /// <summary>在 UI 執行緒執行、等結果（管道的要求：中樞的狀態只在 UI 執行緒）</summary>
    private Task<T> InvokeOnUiAsync<T>(Func<T> func)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() =>
        {
            try
            {
                done.TrySetResult(func());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    private Task<string?> ConversationTitleAsync(string conversationId)
    {
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() =>
        {
            try
            {
                done.TrySetResult(Hub.ConversationTitle(conversationId));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    public bool Exiting { get; private set; }

    public bool IsDark => Settings.Theme switch
    {
        "light" => false,
        "system" => NativeMethods.IsSystemDarkTheme(),
        _ => true,
    };

    /// <summary>系統匣選單：每次打開時重建，列出每個帳戶目前的用量（不必開視窗就能快速監看）</summary>
    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            menu.Items.Add("開啟 Clode Myriagent", null, (_, _) => ShowMain());
            menu.Items.Add("開新視窗", null, (_, _) => OpenNewWindow());
            menu.Items.Add("帳戶儀表板", null, (_, _) => ShowDashboard());
            menu.Items.Add(new ToolStripSeparator());
            foreach (var account in Accounts.Accounts)
            {
                var usage = Accounts.GetUsage(account.Id);
                var windows = usage?.Windows.Take(3).Select(w => $"{w.Label} {w.Percent:0}%").ToList() ?? new List<string>();
                var state = Accounts.GetState(account.Id);
                var text = windows.Count > 0
                    ? $"{account.Label}：{string.Join("・", windows)}{(usage!.Stale ? "（可能過時）" : string.Empty)}"
                    : $"{account.Label}：{state.StatusText}";
                // 點了開啟帳戶儀表板。不能設成停用：停用的項目一律畫成灰字，用量警示的紅字會被蓋掉
                var item = new ToolStripMenuItem(text, null, (_, _) => ShowDashboard());
                if (usage?.Windows.Any(w => w.Percent >= 80 || w.Locked) == true)
                    item.ForeColor = Color.FromArgb(0xD9, 0x4B, 0x3F);
                menu.Items.Add(item);
            }
            int running = Hub.RunningCount;
            if (running > 0)
                menu.Items.Add(new ToolStripMenuItem($"執行中的對話：{running}") { Enabled = false });
            int background = Hub.BackgroundWaitingCount;
            if (background > 0)
                menu.Items.Add(new ToolStripMenuItem($"等背景工作回報的對話：{background}") { Enabled = false });
            // 同步：狀態、暫停／繼續同步、暫停／恢復遠端操作（未配對時沒有）
            var syncItems = TraySyncRules.Items(Sync.Status, Sync.IsPaired, Settings.Sync.Paused, Settings.Sync.RemotePaused, Sync.Backfill, Sync.BackfillPaused);
            if (syncItems.Count > 0)
            {
                menu.Items.Add(new ToolStripSeparator());
                foreach (var syncItem in syncItems)
                {
                    var action = syncItem.Action;
                    EventHandler? onClick = syncItem.Enabled ? (_, _) => OnTraySyncAction(action) : null;
                    menu.Items.Add(new ToolStripMenuItem(syncItem.Text, null, onClick) { Enabled = syncItem.Enabled });
                }
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("結束", null, (_, _) => _ = ExitAsync());
        };
        menu.Items.Add("開啟 Clode Myriagent");   // 讓選單第一次也會觸發 Opening（空選單不會打開）
        return menu;
    }

    private void OnTraySyncAction(TraySyncAction action)
    {
        if (Exiting)
            return;
        switch (action)
        {
            case TraySyncAction.None:
                break;
            case TraySyncAction.PauseSync:
                Sync.SetPaused(true);
                break;
            case TraySyncAction.ResumeSync:
                Sync.SetPaused(false);
                break;
            case TraySyncAction.PauseRemote:
                Sync.SetRemotePaused(true);
                break;
            case TraySyncAction.ResumeRemote:
                Sync.SetRemotePaused(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "不認得的系統匣同步項目");
        }
    }

    // ───────────────────────────── 同步的延後啟動與網路事件 ─────────────────────────────

    /// <summary>主視窗每次載入都會觸發 ViewLoaded：只有第一次排同步的啟動</summary>
    private void OnFirstViewLoaded()
    {
        mainForm.ViewLoaded -= OnFirstViewLoaded;
        ScheduleSyncStart(SyncStartupRules.StartDelay);
    }

    private void ScheduleSyncStart(TimeSpan delay)
    {
        var timer = new System.Windows.Forms.Timer { Interval = (int)delay.TotalMilliseconds };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            syncStartTimers.Remove(timer);
            timer.Dispose();
            if (!Exiting)
                Sync.Start();
        };
        syncStartTimers.Add(timer);
        timer.Start();
    }

    private void StopSyncStartTimers()
    {
        foreach (var timer in syncStartTimers)
        {
            timer.Stop();
            timer.Dispose();
        }
        syncStartTimers.Clear();
    }

    private ScreenPresence? screenPresence;
    private int? lastDisplayState;

    private void DisposeScreenPresence()
    {
        Hub.RemoteDeskActiveChanged = null;
        screenPresence?.Dispose();
        screenPresence = null;
    }

    /// <summary>主視窗收到螢幕開關的通知（MainForm.WndProc；ScreenPresence 還沒建立時先記著）</summary>
    internal void OnDisplayState(int state)
    {
        lastDisplayState = state;
        screenPresence?.OnDisplayState(state);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Sync.OnSystemResumed();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable)
            Sync.OnNetworkChanged();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Sync.OnNetworkChanged();

    private void DetachSyncEvents()
    {
        if (!syncEventsAttached)
            return;
        syncEventsAttached = false;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
    }

    /// <summary>同步狀態改變（UI 執行緒）：系統匣提示文字的同步段</summary>
    private void OnSyncStatusChanged()
    {
        if (!Exiting)
            Hub.SetSyncTrayText(TraySyncRules.TrayText(Sync.Status, Sync.IsPaired, Sync.Backfill));
    }

    /// <summary>另一個實例啟動時被叫醒：把主視窗帶到前面（正在結束時不再顯示：中樞已停止，視窗不會有反應）</summary>
    public void ShowMain()
    {
        if (Exiting || mainForm.IsDisposed)
            return;
        if (!mainForm.Visible)
            mainForm.Show();
        if (mainForm.WindowState == FormWindowState.Minimized)
            NativeMethods.Restore(mainForm.Handle);   // 回到最小化前的狀態（原本最大化就還原成最大化）
        mainForm.Activate();
        RestoreExtraWindows();
    }

    /// <summary>
    /// 再開一次 app（另一個程序送來「開新視窗」）：主視窗藏在系統匣時叫出主視窗，否則像 Chrome 一樣開一個新視窗
    /// </summary>
    public void OpenWindowFromLaunch()
    {
        if (Exiting || mainForm.IsDisposed)
            return;
        if (ShellRules.LaunchAgainOpensWindow(mainForm.Visible))
            OpenNewWindow();
        else
            ShowMain();
    }

    // ───────────────────────────── 多視窗 ─────────────────────────────

    /// <summary>所有完整的視窗（主視窗在最前面；不含彈出視窗）</summary>
    private IEnumerable<MainForm> AllWindows() => extraWindows.Prepend(mainForm).Where(f => !f.IsDisposed);

    /// <summary>開一個新的完整視窗（系統匣「開新視窗」、再開一次 app）：從目前在前面的視窗錯開一點</summary>
    private void OpenNewWindow()
    {
        var anchor = AllWindows().FirstOrDefault(f => Form.ActiveForm == f) ?? AllWindows().FirstOrDefault(f => f.Visible);
        ((IWindowHost)this).OpenWindow(WindowRules.NewKey(), anchor?.View.Id, atCursor: false);
    }

    /// <summary>上次結束時開著的另外的視窗開回來（只做一次；開機自動啟動藏在系統匣時等主視窗第一次顯示）</summary>
    private void RestoreExtraWindows()
    {
        if (extraWindowsRestored || Exiting)
            return;
        extraWindowsRestored = true;
        foreach (var entry in Settings.Windows.ToList())
            ShowExtraWindow(entry, restoring: true);
    }

    private MainForm ShowExtraWindow(AppWindowState entry, bool restoring)
    {
        var form = new MainForm(this, entry, showInactive: restoring);
        extraWindows.Add(form);
        form.FormClosed += (_, _) => extraWindows.Remove(form);
        if (restoring && entry.Minimized)
            form.WindowState = FormWindowState.Minimized;
        form.Show();
        return form;
    }

    /// <summary>除了這個視窗以外，還有看得到（沒有藏起來）的完整視窗</summary>
    public bool OtherWindowsVisible(MainForm except) => AllWindows().Any(f => f != except && f.Visible);

    /// <summary>使用者關掉了另外開的視窗：從設定移除（下次不再開回來）</summary>
    public void ForgetWindow(MainForm form)
    {
        if (form.Extra is not { } entry)
            return;
        Settings.Windows.RemoveAll(w => w.Key == entry.Key);
        Settings.Save();
    }

    /// <summary>結束 app 等原因關閉另外開的視窗：記下位置（藏起來之後就不再讀，ShutdownAgentsAsync 在藏之前先記）</summary>
    public void RememberWindow(MainForm form)
    {
        if (form.Extra is not { } entry || !form.Visible)
            return;
        entry.Placement = form.CapturePlacement();
        entry.Minimized = form.WindowState == FormWindowState.Minimized;
    }

    bool IWindowHost.OpenWindow(string key, string? sourceViewId, bool atCursor)
    {
        if (Exiting || mainForm.IsDisposed)
            return false;
        var source = (sourceViewId == null ? null : FormOfView(sourceViewId)) ?? mainForm;
        var sourceBounds = source.WindowState == FormWindowState.Normal ? source.Bounds : source.RestoreBounds;
        var bounds = ShellRules.NewWindowBounds(sourceBounds, atCursor ? Cursor.Position : null, source.DeviceDpi,
            Screen.AllScreens.Select(s => s.WorkingArea).ToList());
        var entry = new AppWindowState
        {
            Key = key,
            Placement = new WindowPlacementInfo { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height },
        };
        Settings.Windows.Add(entry);
        Settings.Save();
        var form = ShowExtraWindow(entry, restoring: false);
        form.Activate();
        Log.Info($"開新視窗（{(atCursor ? "拖出分頁" : "錯開")}，共 {AllWindows().Count()} 個）");
        return true;
    }

    TabDropTarget IWindowHost.LocateTabDrop(string sourceViewId)
    {
        // 按 Esc 取消拖曳也會以「沒有人接住」結束：這時不開新視窗
        if (Exiting || NativeMethods.IsEscapeDown())
            return new TabDropTarget(TabDropKind.None);
        var root = NativeMethods.RootWindowAt(Cursor.Position);
        if (root == IntPtr.Zero)
            return new TabDropTarget(TabDropKind.NewWindow);
        var window = AllWindows().FirstOrDefault(f => f.IsHandleCreated && f.Handle == root && f.Visible);
        if (window != null)
            return window.View.Id == sourceViewId ? new TabDropTarget(TabDropKind.None) : new TabDropTarget(TabDropKind.Window, window.View.Id);
        // 這個程序的其他視窗（彈出視窗、對話框、選單）：不處理；其他程式與桌面：開新視窗
        return NativeMethods.ProcessIdOfWindow(root) == Environment.ProcessId
            ? new TabDropTarget(TabDropKind.None)
            : new TabDropTarget(TabDropKind.NewWindow);
    }

    void IWindowHost.ActivateWindow(string viewId)
    {
        if (FormOfView(viewId) is { } form && !form.IsDisposed)
        {
            if (!form.Visible)
                form.Show();
            RestoreAndActivate(form);
        }
    }

    void IWindowHost.CloseWindow(string viewId)
    {
        var form = extraWindows.FirstOrDefault(f => !f.IsDisposed && f.View.Id == viewId);
        form?.Close();
    }

    /// <summary>各視窗最後的外框狀態（鍵是 view id；中樞只在有變的時候送，換主題後照它重設）</summary>
    private Dictionary<string, WindowBorder> windowBorders = new(StringComparer.Ordinal);

    void IWindowHost.UpdateWindowBorders(IReadOnlyDictionary<string, WindowBorder> borders)
    {
        windowBorders = new Dictionary<string, WindowBorder>(borders, StringComparer.Ordinal);
        ApplyWindowBorders();
    }

    /// <summary>照各視窗的外框狀態設顏色（主視窗、另外開的視窗、彈出視窗；沒有狀態的回到主題的灰）</summary>
    private void ApplyWindowBorders()
    {
        bool dark = IsDark;
        var forms = AllWindows().Select(f => ((Form)f, f.View.Id)).Concat(popouts.Select(p => ((Form)p, p.View.Id)));
        foreach (var (form, viewId) in forms.ToList())
        {
            if (form.IsDisposed || !form.IsHandleCreated)
                continue;
            var border = windowBorders.TryGetValue(viewId, out var b) ? b : WindowBorder.None;
            var (r, g, bl) = WindowBorderRules.Rgb(border, dark);
            NativeMethods.SetBorderColor(form.Handle, r, g, bl);
        }
    }

    /// <summary>主視窗切到帳戶儀表板（系統匣選單）</summary>
    private void ShowDashboard()
    {
        ShowMain();
        mainForm.View.PostToWeb(new JObject { ["type"] = "showView", ["view"] = "dashboard" });
    }

    /// <summary>點系統通知：對話開在彈出視窗或另外開的視窗就把那個視窗帶到前面，否則在主視窗切到（或開啟）這個對話</summary>
    private void ShowConversation(string? conversationId)
    {
        var popout = conversationId == null ? null : popouts.FirstOrDefault(p => p.ConversationId == conversationId && !p.IsDisposed);
        if (popout != null)
        {
            RestoreAndActivate(popout);
            return;
        }
        if (conversationId != null && !Exiting)
        {
            var showing = Hub.ViewIdsOf(conversationId);
            var window = AllWindows().FirstOrDefault(f => f.Visible && showing.Contains(f.View.Id));
            if (window != null)
            {
                RestoreAndActivate(window);
                window.View.PostToWeb(new JObject { ["type"] = "focusConversation", ["conversationId"] = conversationId });
                return;
            }
        }
        ShowMain();
        if (conversationId != null)
            mainForm.View.PostToWeb(new JObject { ["type"] = "focusConversation", ["conversationId"] = conversationId });
    }

    /// <summary>把視窗帶到前面：Activate 不會還原最小化的視窗，先以 SW_RESTORE 還原（最小化前是最大化就回到最大化）</summary>
    private static void RestoreAndActivate(Form form)
    {
        if (form.WindowState == FormWindowState.Minimized)
            NativeMethods.Restore(form.Handle);
        form.Activate();
    }

    public void ShowTrayHintOnce()
    {
        if (trayHintShown)
            return;
        trayHintShown = true;
        var running = Hub.RunningCount;
        // 這則提示蓋掉了前一則通知：點它只開主視窗，之後的 CLI 彙總也不再和前一則合併
        lastTrayNotice = null;
        lastNotifiedConversation = null;
        tray.ShowBalloonTip(4000, "Clode Myriagent 仍在背景執行",
            running > 0 ? $"還有 {running} 個對話在執行；雙擊系統匣圖示可回到視窗" : "雙擊系統匣圖示可回到視窗；在選單按「結束」才會完全關閉", ToolTipIcon.None);
    }

    // ───────────────────────────── IAgentHost ─────────────────────────────

    public void BeginInvoke(Action action) => ui.Post(_ =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error("UI 執行緒工作失敗", ex);
        }
    }, null);

    private Form OwnerOf(IAgentView view) => FormOfView(view.Id) ?? mainForm;

    public IReadOnlyList<string> PickFiles(IAgentView view)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "選擇要附加的檔案",
            Multiselect = true,
            Filter = "所有檔案 (*.*)|*.*|圖片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.tif;*.tiff|影片|*.mp4;*.mov;*.m4v;*.avi;*.mkv;*.webm;*.wmv|"
                     + "文件|*.pdf;*.doc;*.docx;*.xls;*.xlsx;*.csv;*.ppt;*.pptx;*.txt;*.md;*.json;*.xml",
        };
        return dialog.ShowDialog(OwnerOf(view)) == DialogResult.OK ? dialog.FileNames : Array.Empty<string>();
    }

    public string? PickFolder(IAgentView view, string? initialDirectory)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "選擇代理的工作資料夾（代理會在這個資料夾讀寫檔案）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        // 起始資料夾存不存在在背景確認、最多等 2 秒（網路磁碟離線時 Directory.Exists 會卡到 SMB 逾時）；確認不了就不指定
        if (!string.IsNullOrWhiteSpace(initialDirectory) && PathProbe.Check(() => Directory.Exists(initialDirectory), PathProbe.ShortTimeout) == true)
            dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(OwnerOf(view)) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    public string? PickSaveFile(IAgentView view, string fileName)
    {
        var extension = Path.GetExtension(fileName);
        using var dialog = new SaveFileDialog
        {
            Title = "另存新檔",
            FileName = fileName,
            // 預設存到「下載」；這台沒有時由對話框自己決定
            InitialDirectory = KnownDownloadsFolder() ?? string.Empty,
            Filter = string.IsNullOrEmpty(extension) ? "所有檔案 (*.*)|*.*" : $"{extension.TrimStart('.').ToUpperInvariant()} 檔案 (*{extension})|*{extension}|所有檔案 (*.*)|*.*",
            AddExtension = !string.IsNullOrEmpty(extension),
            DefaultExt = extension.TrimStart('.'),
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(OwnerOf(view)) == DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>使用者的「下載」資料夾（%USERPROFILE%\Downloads）；不存在回 null</summary>
    private static string? KnownDownloadsFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile))
            return null;
        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads) ? downloads : null;
    }

    public void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(mainForm, $"無法開啟：{ex.Message}", "Clode Myriagent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void RevealInExplorer(string path) => _ = RevealInExplorerAsync(path);

    /// <summary>
    /// 檔案就選取、資料夾就開啟。是不是檔案在背景確認（網路磁碟離線時 File.Exists 會卡到 SMB 逾時，不能卡住 UI 執行緒）；
    /// 確認不了就用 /select（只選取，不會開啟或執行檔案）
    /// </summary>
    private async Task RevealInExplorerAsync(string path)
    {
        var isFile = await PathProbe.CheckAsync(() => File.Exists(path), PathProbe.ShortTimeout);
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.Arguments = isFile != false ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(mainForm, $"無法開啟檔案總管：{ex.Message}", "Clode Myriagent", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            Log.Warn($"複製失敗：{ex.Message}");
        }
    }

    public bool CopyFile(string path)
    {
        try
        {
            var data = new DataObject();
            data.SetFileDropList([path]);
            // 貼上時是複製、不是搬移（DROPEFFECT_COPY）
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            Log.Warn($"複製檔案失敗：{ex.Message}");
            return false;
        }
    }

    public void OpenPopout(string conversationId) => OpenPopout(conversationId, null);

    /// <param name="bounds">app 更新前的位置與大小（x, y, w, h）；不在任何螢幕的工作區內就用預設位置</param>
    public void OpenPopout(string conversationId, int[]? bounds)
    {
        var existing = popouts.FirstOrDefault(p => p.ConversationId == conversationId && !p.IsDisposed);
        if (existing != null)
        {
            RestoreAndActivate(existing);
            return;
        }
        var form = new PopoutForm(this, conversationId);
        if (AppControllerRules.RestorableBounds(bounds, Screen.AllScreens.Select(s => s.WorkingArea)) is { } rect)
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Bounds = rect;
        }
        popouts.Add(form);
        form.Show();
    }

    public void OnPopoutClosed(PopoutForm form) => popouts.Remove(form);

    public void ConversationDeleted(string conversationId)
    {
        foreach (var popout in popouts.Where(p => p.ConversationId == conversationId).ToList())
            popout.Close();
    }

    /// <summary>顯示這個 view 的視窗（主視窗、另外開的視窗或彈出視窗）；view 所在的視窗已關閉時回 null</summary>
    private Form? FormOfView(string viewId)
    {
        if (AllWindows().FirstOrDefault(f => f.View.Id == viewId) is { } window)
            return window;
        return popouts.FirstOrDefault(p => !p.IsDisposed && p.View.Id == viewId);
    }

    /// <summary>使用者正在操作的視窗：看得到、沒有最小化，而且是前景視窗</summary>
    private static bool IsInForeground(Form form)
        => !form.IsDisposed && form.Visible && form.WindowState != FormWindowState.Minimized && Form.ActiveForm == form;

    public bool IsViewForeground(string viewId) => FormOfView(viewId) is { } form && IsInForeground(form);

    /// <summary>
    /// 系統通知。對話的通知點了回到那個對話；〔熱更新〕中樞在一波 CLI 換版都處理完後送的彙總（conversationId 為 null，
    /// 不逐一通知各對話）點了開主視窗，同時到的幾則（Claude Code 與 Codex 一起更新）合併成一則（AppControllerRules.NextTrayNotice）
    /// </summary>
    public void Notify(string title, string message, string? conversationId)
    {
        var notice = AppControllerRules.NextTrayNotice(lastTrayNotice, title, message, conversationId, DateTime.UtcNow);
        lastTrayNotice = notice;
        lastNotifiedConversation = notice.ConversationId;
        // 氣球提示的內容不能是空字串（ShowBalloonTip 會丟例外）
        tray.ShowBalloonTip((int)AppControllerRules.BalloonDuration.TotalMilliseconds, notice.Title,
            notice.Message.Length == 0 ? " " : notice.Message, ToolTipIcon.None);
    }

    public void RequestAttention(IReadOnlyCollection<string> viewIds)
    {
        // 藏到系統匣的視窗沒有工作列按鈕，閃了也看不到：只閃看得到的；沒有任何看得到的視窗顯示這個對話就閃主視窗
        var targets = viewIds.Select(FormOfView).OfType<Form>().Where(f => f.Visible).ToList();
        if (targets.Count == 0 && AllWindows().FirstOrDefault(f => f.Visible) is { } fallback)
            targets.Add(fallback);   // 主視窗優先；主視窗藏起來時閃另外開的視窗
        foreach (var form in targets)
        {
            if (form.IsHandleCreated && !IsInForeground(form))
                NativeMethods.Flash(form.Handle);
        }
    }

    public void Speak(string text, VoiceTuning tuning, Action? started) => voice.Speak(text, started == null ? null : () => BeginInvoke(started), tuning);

    public void StopSpeaking() => voice.Stop();

    public void ApplyTheme(string theme)
    {
        bool dark = IsDark;
        foreach (var window in AllWindows().ToList())
            window.ApplyTheme(dark);
        foreach (var popout in popouts.ToList())
            popout.ApplyTheme(dark);
        // 換主題時標題列連外框一起設回主題的灰：狀態的外框再蓋回去
        ApplyWindowBorders();
    }

    /// <summary>Windows 切換深淺色（WM_SETTINGCHANGE「ImmersiveColorSet」，類別是 General）：主題跟隨系統時重新套用</summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && Settings.Theme == "system" && !Exiting && !mainForm.IsDisposed)
            ApplyTheme(Settings.Theme);
    }

    public void UpdateTray(string tooltip, int running, int waiting)
    {
        try
        {
            tray.Text = tooltip;
        }
        catch (ArgumentException)
        {
            tray.Text = "Clode Myriagent";
        }
        // 系統匣圖示右下角的狀態點（縮到系統匣時看不到工作列按鈕）：狀態變了才換圖示
        trayBadge.Apply(tray, TrayBadgeRules.Decide(running, waiting));
        // 建構期間中樞就可能呼叫（主視窗還沒建）：那時只記系統匣的文字
        if (mainForm is { IsDisposed: false })
            taskbar.Apply(mainForm, TaskbarRules.Decide(running, waiting, Settings.VisualEffects));
    }

    /// <summary>主視窗的工作列按鈕重建了（顯示出來、Explorer 重開）：照最後的狀態重設進度與疊加數字</summary>
    public void ReapplyTaskbar()
    {
        if (mainForm is { IsDisposed: false })
            taskbar.Reapply(mainForm);
    }

    // ───────────────────────────── app 自我更新（IAppUpdateShell）─────────────────────────────

    /// <summary>前景視窗屬於這個程序（主視窗、彈出視窗，也包括選檔、選資料夾、結束確認這些原生對話框與系統匣選單）</summary>
    bool IAppUpdateShell.AnyWindowForeground
    {
        get
        {
            var (processId, visible, minimized) = NativeMethods.ForegroundWindowInfo();
            return AppControllerRules.IsAppForeground(processId, visible, minimized, Environment.ProcessId);
        }
    }

    bool IAppUpdateShell.MainWindowHidden => mainForm.IsDisposed || !mainForm.Visible || mainForm.WindowState == FormWindowState.Minimized;

    TimeSpan IAppUpdateShell.SinceLastInput => NativeMethods.IdleTime();

    bool IAppUpdateShell.LoginInProgress => Accounts.HasActiveLogin;

    bool IAppUpdateShell.CliUpdating => AgentHub.AppUpdateProviders.Any(p => AppUpdateRestartRules.IsCliUpdating(Cli.Get(p)));

    IReadOnlyList<ResumePopout> IAppUpdateShell.CapturePopouts()
        => popouts.Where(p => !p.IsDisposed).Select(p =>
        {
            var b = p.WindowState == FormWindowState.Normal ? p.Bounds : p.RestoreBounds;
            return new ResumePopout(p.ConversationId, new[] { b.X, b.Y, b.Width, b.Height });
        }).ToList();

    void IAppUpdateShell.OpenPopouts(IReadOnlyList<ResumePopout> list)
    {
        foreach (var p in list)
        {
            if (Hub.GetConversation(p.ConversationId) != null)
                OpenPopout(p.ConversationId, p.Bounds);
        }
    }

    void IAppUpdateShell.Notify(string title, string message) => Notify(title, message, conversationId: null);

    int? IAppUpdateShell.StartHelper(string executable, IReadOnlyList<string> arguments) => updateHelper.Start(executable, arguments);

    void IAppUpdateShell.StopHelper(int pid) => updateHelper.Stop(pid);

    void IAppUpdateShell.ExitForUpdate(IReadOnlySet<string> keepDrafts) => _ = ExitForUpdateAsync(keepDrafts);

    void IAppUpdateShell.RestartInto(string executable)
    {
        launchAfterExit = executable;
        _ = ExitAsync();
    }

    /// <summary>
    /// 單一實例的監聽執行緒：較新的版本請這一份交棒（新版自動接手舊版）。請求合格才接下（回 true，新版就結束），
    /// 排到 UI 執行緒由更新協調器排空與交棒；不合格回 false，新版改用一般的喚醒
    /// </summary>
    public bool AcceptHandover()
    {
        var request = InstanceHandover.TakeRequest(AppPaths.Root);
        if (InstanceHandoverRules.CheckRequest(request, AppVersionInfo.Current.Version, DateTime.UtcNow, File.Exists) is { } problem)
        {
            Log.Info($"不接交棒請求：{problem}");
            return false;
        }
        Log.Info($"接下交棒請求：{request!.Release}（{request.Executable}）");
        BeginInvoke(() => updateCoordinator.BeginHandover(request.Executable, request.Release));
        return true;
    }

    /// <summary>
    /// 交棒給較新的版本：先標成正在結束（不再回應喚醒：新版叫不醒這一份，會等它結束後接手），啟動新版；
    /// 啟動不了就取消結束、回 false（協調器就地接續）。啟動了就收尾代理程序並結束
    /// </summary>
    bool IAppUpdateShell.HandOver(string executable, IReadOnlyList<string> arguments, IReadOnlySet<string> keepDrafts)
    {
        if (Exiting)
            return false;
        Exiting = true;
        var process = UpdateHelperProcess.StartProcess(executable, arguments);
        if (process == null)
        {
            Exiting = false;
            return false;
        }
        process.Dispose();
        _ = HandOverExitAsync(keepDrafts);
        return true;
    }

    private async Task HandOverExitAsync(IReadOnlySet<string> keepDrafts)
    {
        InstanceHandover.ClearRunning(AppPaths.Root);
        await ShutdownAgentsAsync(keepDrafts);
        ExitImmediately();
    }

    /// <summary>交棒給換版幫手後結束：不問（使用者不在、對話都已停下）、代理程序收尾、草稿資料夾照 keepDrafts 保留</summary>
    private async Task ExitForUpdateAsync(IReadOnlySet<string> keepDrafts)
    {
        if (Exiting)
            return;
        Exiting = true;
        await ShutdownAgentsAsync(keepDrafts);
        ExitImmediately();
    }

    // ───────────────────────────── 結束 ─────────────────────────────

    /// <summary>
    /// 結束 app：有執行中的對話、或 CLI 正在安裝／更新（〔熱更新〕npm 正在換檔時明講會讓 CLI 安裝不完整）時先確認，
    /// 代理程序正常收尾（最多 8 秒）。確認框開著時更新照常推進：按「是」時情況變嚴重了（例如排空完成、npm 開始換檔）就以新的警告再問一次。
    /// 確定結束後先取消還沒開始換檔的 CLI 更新（不再啟動 npm），再讓中樞收尾
    /// </summary>
    public async Task ExitAsync()
    {
        if (Exiting || exitPromptOpen)
            return;
        int running = Hub.RunningCount;
        var states = AgentProviders.All.Select(Cli.Get).ToList();
        var warnings = AppControllerRules.ExitWarnings(running, states, Hub.BackgroundWaitingCount);
        while (warnings.Count > 0)
        {
            ShowMain();
            exitPromptOpen = true;   // 確認框開著時系統匣「結束」再按一次不會跳第二個
            try
            {
                if (MessageBox.Show(mainForm, $"{string.Join("\n", warnings)}\n\n確定要結束嗎？", "Clode Myriagent",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    launchAfterExit = null;   // 不結束：「安裝到這台電腦」之後也不在之後的某次結束時突然啟動安裝好的那份
                    return;
                }
            }
            finally
            {
                exitPromptOpen = false;
            }
            var nowRunning = Hub.RunningCount;
            var nowStates = AgentProviders.All.Select(Cli.Get).ToList();
            if (!AppControllerRules.NeedsExitReconfirm(running, states, nowRunning, nowStates))
                break;
            running = nowRunning;
            states = nowStates;
            warnings = AppControllerRules.ExitWarnings(running, states, Hub.BackgroundWaitingCount);
        }

        Exiting = true;
        // 有已備妥的 app 新版就在結束時換版（排空中也是：不再等，resume.json 留給下次啟動給「繼續」）
        var exitAction = updateCoordinator.PrepareExit(windowsShuttingDown: false);
        await ShutdownAgentsAsync(keepDrafts: null);
        if (exitAction == AppExitAction.ApplyOnExit)
            updateCoordinator.LaunchExitHelper();
        if (launchAfterExit != null)
            UpdateHelperProcess.StartProcess(launchAfterExit, Array.Empty<string>())?.Dispose();
        ExitImmediately();
    }

    /// <summary>
    /// 確定結束之後的收尾（Exiting 已設定）：還在排空或等其他程序結束的 CLI 更新到此為止（「結束會取消這次更新」），
    /// 藏起視窗與系統匣圖示，代理程序正常收尾最多 8 秒。keepDrafts 列出的草稿資料夾不刪（app 更新）
    /// </summary>
    private async Task ShutdownAgentsAsync(IReadOnlySet<string>? keepDrafts)
    {
        // 中樞收尾最多 8 秒，這段期間不能讓 npm 開始換檔
        Cli.BeginShutdown();
        tray.Visible = false;   // 收尾期間（最多 8 秒）不能再從系統匣叫出已停止運作的視窗
        if (mainForm.Visible)
            Settings.Window = mainForm.CapturePlacement();
        mainForm.Hide();
        // 另外開的視窗：記下位置與是否最小化（下次啟動開回來），再藏起來
        foreach (var window in extraWindows.ToList())
        {
            RememberWindow(window);
            window.Hide();
        }
        foreach (var popout in popouts.ToList())
            popout.Hide();
        StopSyncStartTimers();
        try
        {
            // 同步先停（最多 3 秒，逾時就取消）：之後中樞收尾時的帳戶與 CLI 變化不再發布
            await Sync.StopAsync(SyncStartupRules.ExitStopTimeout).WaitAsync(SyncStartupRules.ExitStopGuard);
        }
        catch (Exception ex)
        {
            Log.Warn($"停止同步：{ex.Message}");
        }
        try
        {
            await Hub.ShutdownAsync(keepDrafts).WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            Log.Warn($"關閉代理程序：{ex.Message}");
        }
    }

    /// <summary>立即結束（初始化失敗、Windows 登出）</summary>
    public void ExitImmediately(bool closeMainForm = true)
    {
        if (Exiting && !closeMainForm)
            return;
        Exiting = true;
        StopSyncStartTimers();
        DetachSyncEvents();
        // 遠端執行指令：停止這台正在執行的（結束碼盡量送出）、不再收 myriagent-remote 的連線
        remoteExec.Dispose();
        // 只取消、不等：訊息迴圈結束後 BeginInvoke 排的工作不會執行，引擎以取消權杖自己結束
        Sync.Dispose();
        // 另外開的視窗的位置與是否最小化（看得到的才記；ShutdownAgentsAsync 藏起來之前已記過）要在存檔之前
        foreach (var window in extraWindows.ToList())
            RememberWindow(window);
        Settings.Save();
        updateCoordinator.Dispose();
        updateManager.Dispose();
        updateHelper.Dispose();   // 只放開控制代碼，不結束幫手
        Hub.Dispose();
        // 中樞收尾時記的最後幾筆（app.exit）要落盤：換版前最重要
        backgroundJournal.Dispose();
        systemMonitor.Dispose();
        voice.Dispose();
        voiceTranscriber.Dispose();
        Accounts.Dispose();
        Cli.Dispose();
        Thumbnails.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        DisposeScreenPresence();
        tray.Visible = false;
        tray.Dispose();
        taskbar.Dispose();
        trayBadge.Dispose();
        foreach (var popout in popouts.ToList())
            popout.Close();
        // 另外開的視窗：Exiting 已設定，關閉時留著設定（ExtraClosingAction.Keep）
        foreach (var window in extraWindows.ToList())
        {
            if (!window.IsDisposed)
                window.Close();
        }
        if (closeMainForm && !mainForm.IsDisposed)
            mainForm.Close();
        ExitThread();
    }

    /// <summary>刪掉上次沒送出的草稿留下的資料夾（背景執行緒；刪不乾淨的下次啟動再試）</summary>
    private static void DeleteDraftLeftovers(ConversationStore store, IReadOnlyList<string> conversationIds)
    {
        int deleted = 0;
        foreach (var id in conversationIds)
        {
            if (store.DeleteConversation(id))
                deleted++;
        }
        Log.Info($"清除上次沒送出的草稿資料夾：{deleted}／{conversationIds.Count}");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            DisposeScreenPresence();
            DetachSyncEvents();
            StopSyncStartTimers();
            tray.Dispose();
            http.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Program 交給 AppController 的啟動資訊（app 自我更新）</summary>
/// <param name="ManagedRelease">從受管理的安裝執行時的 release；開發版為 null</param>
/// <param name="UsesDefaultData">資料夾是預設的 %LOCALAPPDATA%\Myriagent（沒有 --data、MYRIAGENT_AGENT_DATA）</param>
internal sealed record AppStartup(StartupOptions Options, InstallLayout Layout, string? ManagedRelease, bool UsesDefaultData);
