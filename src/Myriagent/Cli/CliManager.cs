using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Cli;

/// <summary>一個 CLI 的狀態快照（給畫面顯示）</summary>
internal sealed record CliState(
    AgentProvider Provider,
    CliInstallation? Installation,
    string? Version,
    string? Latest,
    bool Busy,
    string? Progress,
    DateTime? LastCheckedAt,
    string? Error)
{
    /// <summary>
    /// 最近一次安裝／更新失敗（或執行了卻沒換版）的原因：下一次動作開始時清掉；
    /// 查版本時發現版本變了或已是最新版也清掉（CliUpdateRules.KeepActionError）
    /// </summary>
    public string? ActionError { get; init; }

    /// <summary>Version 是查哪一個執行檔得到的（完整判定時設定；與 Installation 不同＝還沒查過現在這個執行檔）</summary>
    public string? VersionCheckedFor { get; init; }

    /// <summary>最近一次查最新版本失敗的原因（離線、registry 被擋）；Latest 仍是上次查到的值</summary>
    public string? LatestError { get; init; }

    /// <summary>最近一次檢查（查本機版本與最新版）完成的時間，查不到最新版也更新；LastCheckedAt 只記查到最新版的時間</summary>
    public DateTime? CheckedAt { get; init; }

    /// <summary>已安裝、已穩定的原生執行檔身分（偵測流程以 CliManager.PublishVersionChange 寫入）；null＝不明或未安裝</summary>
    public CliBinaryIdentity? Identity { get; init; }

    /// <summary>安裝正在變動（偵測到換檔、npm 正在替換套件，或 app 自己正在換檔）：這段期間不啟動新的 CLI 程序，送出排進佇列</summary>
    public bool InstallChanging { get; init; }

    /// <summary>安裝損壞的說明（版本來源互相矛盾，例如 npm rollback 留下的混版）；null＝正常。損壞時不重開任何對話</summary>
    public string? InstallBrokenReason { get; init; }

    /// <summary>最近一次已穩定、真的換了身分的變化（CliVersionChange.IsVersionSwitch）；介面的「最近一次換版」</summary>
    public CliVersionChange? LastChange { get; init; }

    /// <summary>app 觸發的更新進行到哪一步（npm 屏障的進度）；None＝沒有在更新</summary>
    public CliUpdatePhase UpdatePhase { get; init; }

    /// <summary>這次 app 觸發的更新是什麼時候開始的（UpdatePhase 不是 None 時才有；CLI 卡的「已等待 N 分鐘」）</summary>
    public DateTime? UpdateStartedAt { get; init; }

    public bool InstallBroken => InstallBrokenReason != null;

    /// <summary>npm 正在替換套件檔案（UpdatePhase＝Installing 且是 npm 安裝）：這時結束 app 會讓 CLI 安裝不完整，結束前要提示</summary>
    public bool NpmInstalling => UpdatePhase == CliUpdatePhase.Installing && Installation?.Kind == CliInstallKind.Npm;

    public bool Installed => Installation != null && Version != null;

    /// <summary>找到執行檔但還沒查過它的版本（啟動後第一次檢查完成前、app 開著時才裝好的）：還不能斷定有沒有安裝</summary>
    public bool Checking => Installation != null && !string.Equals(VersionCheckedFor, Installation.DisplayPath, StringComparison.OrdinalIgnoreCase);

    public bool UpdateAvailable => Installed && CliVersionRules.IsNewer(Latest, Version);

    /// <summary>npm 正在替換套件（舊版先被改名退役、新版下載解壓完才放好）：這段期間啟動 CLI 會失敗</summary>
    public bool ReplacingFiles => Busy && Installation?.Kind == CliInstallKind.Npm;
}

/// <summary>執行 CLI 子指令（CliRunner.RunAsync 的簽章；測試換成假的）</summary>
internal delegate Task<CliRunResult> CliCommandRunner(AgentLauncher launcher, IReadOnlyList<string> arguments,
    Action<IDictionary<string, string?>>? configureEnvironment, TimeSpan timeout, bool killWithApp, Action<int>? started, CancellationToken cancellationToken);

/// <summary>CliManager 的相依與時間（測試換掉：不碰這台電腦真的 CLI、npm、程序與網路）</summary>
internal sealed record CliManagerOptions
{
    /// <summary>找 CLI（預設 CliLocator.Locate：Claude 原生安裝、PATH、npm 前綴、app 自管的 Codex）</summary>
    public Func<AgentProvider, CliInstallation?> Locate { get; init; } = CliLocator.Locate;

    /// <summary>找 npm.cmd（預設 CliLocator.LocateNpm）</summary>
    public Func<string?> LocateNpm { get; init; } = CliLocator.LocateNpm;

    /// <summary>找 winget.exe（更新 WinGet 安裝的 CLI；預設 CliLocator.LocateWinget）</summary>
    public Func<string?> LocateWinget { get; init; } = CliLocator.LocateWinget;

    /// <summary>執行子指令（--version、claude update、cmd /c npm.cmd）</summary>
    public CliCommandRunner Run { get; init; } = (launcher, arguments, environment, timeout, killWithApp, started, cancellationToken)
        => CliRunner.RunAsync(launcher, arguments, environment, timeout, cancellationToken, killWithApp: killWithApp, started: started);

    /// <summary>CliWatcher 的設定（環境變數、檢查程式、去抖時間）；null＝預設（真的環境變數與程序查詢、實際的常數）</summary>
    public CliWatcherOptions? Watcher { get; init; }

    /// <summary>app 工作物件裡還在執行的程序（含 node 開的 codex.exe）</summary>
    public Func<IReadOnlyList<int>> AppProcessIds { get; init; } = ChildProcessJob.GetProcessIds;

    /// <summary>程序在 app 的工作物件裡（更新來源是不是 app 自己）</summary>
    public Func<int, bool> IsAppProcess { get; init; } = ChildProcessJob.Contains;

    /// <summary>程序的執行檔路徑（QueryFullProcessImageName；執行中的資料夾被 npm 改名退役時仍是原路徑）</summary>
    public Func<int, string?> ProcessImagePath { get; init; } = ProcessImage.TryGetPath;

    /// <summary>這台電腦所有程序的 PID（殘留刪不掉時找出不在工作物件裡、正在使用 npm 安裝的程序）</summary>
    public Func<IReadOnlyList<int>> AllProcessIds { get; init; } = ListAllProcessIds;

    /// <summary>啟動時在背景向 npm registry 查最新版（測試關掉：不連網）</summary>
    public bool FetchLatestOnStart { get; init; } = true;

    /// <summary>多久向 npm registry 查一次最新版（並決定要不要自動更新）</summary>
    public TimeSpan LatestCheckInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>第一次自動更新檢查（避開啟動時帳戶檢查自己起的 codex／claude 程序）</summary>
    public TimeSpan FirstAutoUpdateDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>自動更新延後（有對話在跑、有程序在用安裝、版本鎖被占住）時，多久後再試</summary>
    public TimeSpan DeferredRetry { get; init; } = CliUpdateRules.LockBusyRetry;

    /// <summary>--version 的時限</summary>
    public TimeSpan VersionTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>npm 換檔前等 app 自己這種 CLI 的程序（帳戶查詢的短命 app-server、剛關的程序）結束的上限（7.3.1：進行中的最多 30 秒）</summary>
    public TimeSpan AppProcessExitWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>更新後驗證時，安裝還在變動（外部的 npm、版本鎖）最多等多久，之後照當時的樣子判定</summary>
    public TimeSpan ActivityWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>app 自己的更新結束後，多久內偵測到的變化仍算 app 觸發的（watcher 去抖之後才宣告）</summary>
    public TimeSpan OwnUpdateGrace { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// --version 失敗（逾時、起不來、結束碼不是 0）而判定安裝損壞之後，多久自動再完整判定一次（之後每次加倍）：開機剛登入、防毒正在掃描剛換上的
    /// 大檔案時第一次判定可能兩次都失敗，檔案沒變就不會有新的訊號，損壞會一直掛著
    /// </summary>
    public TimeSpan VersionFailureRecheck { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>--version 連續失敗時最多自動再判定幾次（之後等檔案變動、立即檢查或重開 app）</summary>
    public int VersionFailureRechecks { get; init; } = 3;

    /// <summary>開啟 Claude 官方安裝程式的視窗（PowerShell 執行 install.ps1）；測試換成假的安裝程式</summary>
    public Func<Process> StartClaudeInstaller { get; init; } = () => StartOfficialInstaller(CliManager.ClaudeInstallCommand);

    /// <summary>開啟 Antigravity CLI 官方安裝程式的視窗（PowerShell 執行 install.ps1）；測試換成假的安裝程式</summary>
    public Func<Process> StartAntigravityInstaller { get; init; } = () => StartOfficialInstaller(CliManager.AntigravityInstallCommand);

    /// <summary>開啟 Grok Build 官方安裝程式的視窗（PowerShell 執行 install.ps1）；測試換成假的安裝程式</summary>
    public Func<Process> StartGrokInstaller { get; init; } = () => StartOfficialInstaller(CliManager.GrokInstallCommand);

    /// <summary>向官方查最新版（npm registry 或 agy 的 manifest）的 GET；null＝用 CliManager 自己的 HttpClient。測試換成假的回應</summary>
    public Func<string, CancellationToken, Task<string>>? FetchText { get; init; }

    /// <summary>官方安裝指令（CliManager.ClaudeInstallCommand／AntigravityInstallCommand）開在一個看得到的 PowerShell 視窗</summary>
    private static Process StartOfficialInstaller(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);
        return Process.Start(psi) ?? throw new InvalidOperationException("無法開啟安裝視窗");
    }

    private static IReadOnlyList<int> ListAllProcessIds()
    {
        var processes = Process.GetProcesses();
        try
        {
            return processes.Select(p => p.Id).ToList();
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }
}

/// <summary>
/// 管理兩個 CLI：找執行檔、偵測換版（CliWatcher）、向 npm registry 查最新版、安裝、更新、自動更新。
/// <para>
/// 版本偵測（設計 7.2）：中樞接上更新屏障（UpdateParticipant）時開始監看——啟動時第一次讀到的身分以 Old: null 發布，
/// 之後檔案事件與 60 秒輪詢的訊號依序處理：Changing＝安裝開始變動（Stable=false）；Settled＝完整判定（含 --version）後發布已穩定的新身分、
/// 安裝損壞或 CLI 被移除。CliState.Identity 只經 PublishVersionChange 改（中樞以它判斷換版通知處理了沒有）。
/// 向 npm registry 查最新版每 6 小時一次（1 分鐘後第一次，同時決定自動更新）。
/// </para>
/// <para>
/// 更新（設計 7.3）：Claude 原生與其他安裝用 CLI 自己的 update（換檔用改名，不需要屏障）；app 自管的 Codex 換資料夾（改名，不需要屏障）；
/// npm 版走 7.3.1 的全體屏障（PrepareForUpdateAsync → 等程序結束、清殘留 → cmd /c npm.cmd install -g → 三方一致驗證 → 發布 → UpdateFinished）。
/// 狀態改變時觸發 Changed（背景執行緒）；自己加鎖，事件都不在鎖內觸發。
/// </para>
/// </summary>
internal sealed class CliManager : IDisposable
{
    private static IReadOnlyList<AgentProvider> Providers => AgentProviders.All;
    private static readonly string[] VersionArguments = { "--version" };
    private static readonly string[] UpdateArguments = { "update" };

    /// <summary>判定時發現執行檔讀不到身分（剛被換掉）時，最多再查幾次</summary>
    private const int IdentityReadAttempts = 3;

    /// <summary>受管 Codex 下載：多久沒收到資料就放棄（連線斷了卻沒收到 RST 時讀取會永遠等下去）</summary>
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>受管 Codex 下載的整體上限</summary>
    private static readonly TimeSpan DownloadTimeLimit = TimeSpan.FromHours(1);

    /// <summary>app 自己的更新開始前一點點就看到的事件也算（訊號的時間與更新開始的時間在不同執行緒取得）</summary>
    private static readonly TimeSpan OwnUpdateSlack = TimeSpan.FromSeconds(5);

    /// <summary>向 WinGet 查新版（winget list）的時限：會先更新來源索引，網路慢時要好幾秒</summary>
    private static readonly TimeSpan WingetQueryTimeout = TimeSpan.FromMinutes(2);

    private readonly object gate = new();
    private readonly Dictionary<AgentProvider, CliState> states = new();
    private readonly HttpClient http;
    private readonly Func<AgentProvider, bool> hasRunningSessions;
    private readonly Func<bool> autoUpdateEnabled;
    private readonly bool backgroundChecks;
    private readonly CliManagerOptions options;
    private readonly CliInstallInspector inspector;
    private readonly CliWatcher? watcher;
    private readonly System.Threading.Timer timer;

    /// <summary>app 的生命週期：結束時取消（進行中的屏障排空、等程序結束、--version）；npm 與 claude update 本身不取消</summary>
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>每種 CLI 的安裝變化依序處理（watcher 的訊號、更新後的驗證、立即檢查）：同一種 CLI 同一時間只有一個判定在跑</summary>
    private readonly Dictionary<AgentProvider, Task> chains = new();

    /// <summary>每種 CLI 收到的 watcher 訊號序號：處理 Settled 時後面已經有新訊號＝快照過時，交給後面的</summary>
    private readonly Dictionary<AgentProvider, long> signalSequence = new();

    /// <summary>app 自己觸發的更新的起訖時間（npm 檔名沒有 PID：這段期間偵測到的變化算 app 的）</summary>
    private readonly Dictionary<AgentProvider, (DateTime StartedUtc, DateTime? EndedUtc)> ownUpdates = new();

    /// <summary>
    /// app 自己這一次更新的程序（claude update、cmd／npm 與它們的子孫）的 PID：暫存檔名與版本鎖帶的更新者 PID 是它們＝app 觸發的。
    /// 每次更新開始時清掉，只在更新期間（與結束後一小段時間）採信：PID 會被重複使用
    /// </summary>
    private readonly Dictionary<AgentProvider, HashSet<int>> ownProcessIds = new();

    /// <summary>更新指令執行完卻沒換版時的最新版本：同一版不再自動重試（stable 頻道、由 winget 管理…再跑也一樣）</summary>
    private readonly Dictionary<AgentProvider, string?> noEffectLatest = new();

    /// <summary>要在下次定期檢查時重試更新的 CLI（claude update 遇到版本鎖被占住：手動按的也照樣重試）</summary>
    private readonly HashSet<AgentProvider> retryRequested = new();

    /// <summary>已回報過的「程序回報的版本與啟動身分不同」（每個身分與版本只重新定位一次）</summary>
    private readonly HashSet<string> reportedMismatches = new(StringComparer.Ordinal);

    /// <summary>--version 連續失敗的次數（以 gate 保護；成功就移除）：決定還要不要自動再判定（ScheduleVersionFailureRecheck）</summary>
    private readonly Dictionary<AgentProvider, int> versionFailures = new();

    /// <summary>已對更新屏障的參與者呼叫過 PrepareForUpdateAsync、還沒呼叫 UpdateFinished 的 CLI（以 gate 保護）</summary>
    private readonly HashSet<AgentProvider> prepared = new();

    private ICliUpdateParticipant? participant;
    private int watching;
    private int disposed;

    /// <summary>狀態改變（在背景執行緒觸發）</summary>
    public event Action? Changed;

    /// <summary>
    /// CLI 安裝的變化（換版、開始變動、損壞、移除；見 CliVersionChange）。在背景執行緒觸發（偵測流程的執行緒，不在 gate 鎖內）；
    /// 觸發前狀態已更新（Get／GetIdentity 讀得到新身分）、Changed 也已觸發。訂閱端（中樞）一律 BeginInvoke 回 UI 執行緒處理
    /// </summary>
    public event Action<CliVersionChange>? VersionChanged;

    /// <summary>
    /// 更新屏障的參與者（中樞建立時設定成自己；取代原本的 ReleaseIdleSessions 委派）：換掉 npm 版的檔案前呼叫
    /// PrepareForUpdateAsync，更新結束後呼叫 UpdateFinished（兩者都在背景執行緒）。null＝沒有中樞（測試）。
    /// 設定（中樞已訂閱 Changed 與 VersionChanged）時開始監看 CLI 安裝：啟動時第一次讀到的身分（Old: null）一定送得到中樞，
    /// 也一定在中樞能啟動任何對話程序之前就開始讀
    /// </summary>
    public ICliUpdateParticipant? UpdateParticipant
    {
        get => Volatile.Read(ref participant);
        set
        {
            Volatile.Write(ref participant, value);
            if (value != null)
                StartWatching();
        }
    }

    /// <param name="startBackgroundChecks">false＝不監看、不排定背景檢查（測試用：不讀這台電腦真的 CLI、不連網）</param>
    public CliManager(HttpClient http, Func<AgentProvider, bool> hasRunningSessions, Func<bool> autoUpdateEnabled, bool startBackgroundChecks = true)
        : this(http, hasRunningSessions, autoUpdateEnabled, startBackgroundChecks, new CliManagerOptions())
    {
    }

    /// <summary>測試用：換掉相依（找 CLI、跑指令、watcher 的環境與時間、程序查詢）</summary>
    internal CliManager(HttpClient http, Func<AgentProvider, bool> hasRunningSessions, Func<bool> autoUpdateEnabled, bool startBackgroundChecks, CliManagerOptions options)
    {
        this.http = http;
        this.hasRunningSessions = hasRunningSessions;
        this.autoUpdateEnabled = autoUpdateEnabled;
        this.options = options;
        backgroundChecks = startBackgroundChecks;
        var watcherOptions = options.Watcher ?? new CliWatcherOptions();
        inspector = watcherOptions.Inspector ?? new CliInstallInspector(watcherOptions.Environment ?? Environment.GetEnvironmentVariable);
        foreach (var provider in Providers)
        {
            states[provider] = new CliState(provider, LocateSafely(provider), null, null, false, null, null, null);
            chains[provider] = Task.CompletedTask;
            signalSequence[provider] = 0;
        }
        if (startBackgroundChecks)
        {
            watcher = new CliWatcher(WatcherInstallation, watcherOptions with { Inspector = inspector });
            watcher.Signal += OnWatchSignal;
            // 本機版本由 watcher 的第一次檢查負責（中樞接上屏障時開始）；這裡只清掉換下來的受管 Codex、向 registry 查最新版
            _ = Task.Run(InitialCheckAsync);
            timer = new System.Threading.Timer(_ => _ = PeriodicAsync(verifyLocal: false), null, options.FirstAutoUpdateDelay, options.LatestCheckInterval);
        }
        else
        {
            timer = new System.Threading.Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        }
    }

    public CliState Get(AgentProvider provider)
    {
        lock (gate)
            return states[provider];
    }

    /// <summary>目前已穩定的執行檔身分（只讀狀態、不碰磁碟，任何執行緒都可呼叫）；中樞每次送出時放進 AgentSessionSettings.Cli</summary>
    public CliBinaryIdentity? GetIdentity(AgentProvider provider) => Get(provider).Identity;

    // ───────────────────────────── 發布安裝變化 ─────────────────────────────

    /// <summary>
    /// 宣告一次安裝變化：偵測流程在「安裝已穩定」、判定損壞或偵測到開始變動時呼叫（測試也用它模擬換版）。
    /// 先套用到狀態（ApplyVersionChange）並觸發 Changed，再觸發 VersionChanged；都在呼叫端的執行緒、不在鎖內
    /// </summary>
    internal void PublishVersionChange(CliVersionChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Update(change.Provider, s => ApplyVersionChange(s, change));
        RaiseVersionChanged(change);
    }

    /// <summary>
    /// 純函式：安裝變化套用到狀態。變動中只設 InstallChanging；損壞時記原因（身分維持最後可用的）；
    /// 已穩定時換上新身分、清掉變動與損壞，Version 跟著新身分的版本（CLI 卡立即顯示新版）；真的換了身分才更新 LastChange
    /// </summary>
    internal static CliState ApplyVersionChange(CliState state, CliVersionChange change)
    {
        if (!change.Stable)
            return state with { InstallChanging = true };
        if (change.InstallBroken)
            return state with { InstallChanging = false, InstallBrokenReason = change.Detail ?? "CLI 安裝不完整，請重新安裝" };
        return state with
        {
            Identity = change.New,
            InstallChanging = false,
            InstallBrokenReason = null,
            LastChange = change.IsVersionSwitch ? change : state.LastChange,
            Version = change.New?.Version ?? state.Version,
        };
    }

    /// <summary>
    /// 偵測流程的發布：與 PublishVersionChange 相同的順序（套用 → Changed → VersionChanged），另外在同一次狀態更新裡寫入判定的附帶資訊
    /// （安裝位置、顯示的版本、查過的執行檔）。狀態沒有任何改變就什麼都不觸發；只有附帶資訊改變（身分、變動、損壞、最近換版都沒變）只觸發 Changed
    /// </summary>
    private void Commit(CliVersionChange change, Func<CliState, CliState> also)
    {
        CliState before;
        CliState after;
        lock (gate)
        {
            before = states[change.Provider];
            after = also(ApplyVersionChange(before, change));
            if (after.Equals(before))
                return;
            states[change.Provider] = after;
        }
        bool contract = !Equals(before.Identity, after.Identity)
                        || !string.Equals(before.Identity?.Version, after.Identity?.Version, StringComparison.Ordinal)
                        || before.InstallChanging != after.InstallChanging
                        || !string.Equals(before.InstallBrokenReason, after.InstallBrokenReason, StringComparison.Ordinal)
                        || !Equals(before.LastChange, after.LastChange);
        RaiseChanged();
        if (contract)
            RaiseVersionChanged(change);
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"CLI 狀態通知失敗：{ex.Message}");
        }
    }

    private void RaiseVersionChanged(CliVersionChange change)
    {
        try
        {
            VersionChanged?.Invoke(change);
        }
        catch (Exception ex)
        {
            Log.Warn($"CLI 換版通知失敗：{ex.Message}");
        }
    }

    /// <summary>改狀態並通知（測試也用它模擬更新中的狀態）</summary>
    internal void Update(AgentProvider provider, Func<CliState, CliState> change)
    {
        lock (gate)
            states[provider] = change(states[provider]);
        RaiseChanged();
    }

    // ───────────────────────────── 找執行檔 ─────────────────────────────

    /// <summary>
    /// 目前可用的啟動方式（沒安裝回 null）。在 UI 執行緒呼叫（每次送出都會用到）：記住的安裝實際執行的檔案（原生執行檔、啟動器、
    /// npm 版 Codex 的 codex.js）都還在就直接用，不在才重新掃 PATH（掃描略過網路上的目錄：UNC 與網路磁碟機，不會卡在網路逾時）
    /// </summary>
    public AgentLauncher? GetLauncher(AgentProvider provider)
    {
        var cached = Get(provider).Installation;
        if (cached != null && LauncherFilesExist(cached, File.Exists))
            return cached.Launcher;
        return Relocate(provider, resetWatcher: true)?.Launcher;
    }

    /// <summary>這個安裝實際要執行的檔案都在：啟動器、原生執行檔、啟動器參數裡的 .js 入口（npm 版 Codex 的 codex.js）</summary>
    internal static bool LauncherFilesExist(CliInstallation installation, Func<string, bool> fileExists)
        => fileExists(installation.Launcher.FileName)
           && (installation.NativeExecutable == null || fileExists(installation.NativeExecutable))
           && installation.Launcher.PrefixArguments.Where(a => Path.IsPathRooted(a) && a.EndsWith(".js", StringComparison.OrdinalIgnoreCase)).All(fileExists);

    /// <summary>
    /// 重新找 CLI 並更新狀態的安裝位置；位置變了就請 watcher 重建監看（resetWatcher；watcher 自己呼叫時不用）。
    /// app 自己在更新、或安裝正在變動時不重找（npm 從退役到解壓完成之間 codex.js 不在，找到的結果不可靠），沿用目前的；
    /// force＝app 自己剛裝好（安裝方式可能改變），一定重找。
    /// 回傳目前的安裝（沒變時回原本的物件：判定結果與狀態才比得出「沒有改變」）
    /// </summary>
    private CliInstallation? Relocate(AgentProvider provider, bool resetWatcher, bool force = false)
    {
        var state = Get(provider);
        if (!force && (state.UpdatePhase != CliUpdatePhase.None || state.InstallChanging))
            return state.Installation;
        var found = LocateSafely(provider);
        CliInstallation? current;
        bool changed;
        lock (gate)
        {
            var now = states[provider];
            if (!force && (now.UpdatePhase != CliUpdatePhase.None || now.InstallChanging))
                return now.Installation;
            changed = !SameInstallation(now.Installation, found);
            if (changed)
                states[provider] = now with { Installation = found };
            current = changed ? found : now.Installation;
        }
        if (changed)
        {
            Log.Info($"{CliInstallStabilityRules.DisplayName(provider)} 的安裝位置改變：{found?.DisplayPath ?? "找不到"}");
            RaiseChanged();
            if (resetWatcher && Volatile.Read(ref watching) != 0)
                watcher?.Reset(provider);
        }
        return current;
    }

    private CliInstallation? LocateSafely(AgentProvider provider)
    {
        try
        {
            return options.Locate(provider);
        }
        catch (Exception ex)
        {
            Log.Warn($"找 {provider} CLI 失敗：{ex.Message}");
            return Get(provider).Installation;
        }
    }

    private static bool SameInstallation(CliInstallation? a, CliInstallation? b)
        => a == null || b == null
            ? a == null && b == null
            : a.Provider == b.Provider && a.Kind == b.Kind
              && string.Equals(a.DisplayPath, b.DisplayPath, StringComparison.OrdinalIgnoreCase)
              && string.Equals(a.NativeExecutable, b.NativeExecutable, StringComparison.OrdinalIgnoreCase)
              && string.Equals(a.Launcher.FileName, b.Launcher.FileName, StringComparison.OrdinalIgnoreCase)
              && a.Launcher.PrefixArguments.SequenceEqual(b.Launcher.PrefixArguments, StringComparer.OrdinalIgnoreCase);

    // ───────────────────────────── 版本偵測（CliWatcher） ─────────────────────────────

    /// <summary>開始監看 CLI 安裝（UpdateParticipant 設定時；沒有背景檢查時不啟動）。之後每 60 秒便宜地輪詢一次，取代原本每 6 小時跑 --version 的本機檢查</summary>
    internal void StartWatching()
    {
        if (watcher == null || Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref watching, 1) != 0)
            return;
        watcher.Start();
    }

    /// <summary>watcher 取得目前的安裝（Start、Reset、每次輪詢；背景執行緒）：順便重新找一次，安裝位置變了 watcher 會自己重建監看</summary>
    private CliInstallation? WatcherInstallation(AgentProvider provider) => Relocate(provider, resetWatcher: false);

    /// <summary>watcher 的訊號（背景執行緒、依序、同一時間只有一個）：不能阻塞，排進這種 CLI 的佇列依序處理</summary>
    private void OnWatchSignal(CliWatchSignal signal)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        long sequence;
        lock (gate)
            sequence = ++signalSequence[signal.Provider];
        _ = EnqueueAsync(signal.Provider, async () =>
        {
            await HandleSignalAsync(signal, sequence);
            return true;
        }, false);
    }

    /// <summary>排進這種 CLI 的佇列（同一種 CLI 依序、不同 CLI 平行）；工作失敗或 app 結束時回 fallback</summary>
    private Task<T> EnqueueAsync<T>(AgentProvider provider, Func<Task<T>> work, T fallback)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            chains[provider] = chains[provider].ContinueWith(async _ =>
            {
                try
                {
                    done.TrySetResult(Volatile.Read(ref disposed) != 0 ? fallback : await work());
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    done.TrySetResult(fallback);
                }
                catch (Exception ex)
                {
                    Log.Warn($"處理 {CliInstallStabilityRules.DisplayName(provider)} 的安裝變化失敗：{ex.Message}");
                    done.TrySetResult(fallback);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
        return done.Task;
    }

    /// <summary>
    /// 一個 watcher 訊號：Changing → 發布「安裝正在變動」（Stable=false）；Settled → 完整判定後發布（後面已有新訊號就略過：快照過時）
    /// </summary>
    private async Task HandleSignalAsync(CliWatchSignal signal, long sequence)
    {
        var provider = signal.Provider;
        if (signal.Kind == CliWatchSignalKind.Changing)
        {
            var state = Get(provider);
            if (state.InstallChanging)
                return;
            var origin = ResolveOrigin(provider, signal.UpdaterProcessId, signal.AtUtc);
            Commit(new CliVersionChange(provider, state.Identity, null, origin, Stable: false, InstallBroken: false, signal.Detail) { DetectedAtUtc = signal.AtUtc },
                s => s);
            return;
        }

        bool stale;
        lock (gate)
            stale = signalSequence[provider] != sequence;
        if (stale)
        {
            Log.Info($"{CliInstallStabilityRules.DisplayName(provider)} 的安裝之後又有變化，略過這次的判定（{signal.Trigger}）");
            return;
        }
        await VerifyNowAsync(provider, signal.Installation, signal.Snapshot, signal.UpdaterProcessId, signal.AtUtc, TimeSpan.Zero, sequence);
    }

    /// <summary>判定期間這種 CLI 又收到 watcher 的新訊號（又開始變動、或下一次宣告）：這次的快照已經過時</summary>
    private bool IsStale(AgentProvider provider, long? sequence)
    {
        if (sequence is not long expected)
            return false;
        lock (gate)
            return signalSequence[provider] != expected;
    }

    /// <summary>一次判定的結果；Inconclusive＝判定期間安裝又變了（或還在變動），沒有發布</summary>
    private sealed record CliVerification(CliInstallVerdict Verdict, CliBinaryIdentity? Identity, bool Inconclusive = false)
    {
        public bool Usable => !Inconclusive && Verdict.Condition == CliInstallCondition.Stable && Identity != null;

        public bool Broken => !Inconclusive && Verdict.Condition == CliInstallCondition.Broken;

        public static CliVerification Pending(CliInstallVerdict verdict) => new(verdict, null, Inconclusive: true);
    }

    /// <summary>排進這種 CLI 的佇列做一次完整判定（更新後的驗證、立即檢查、程序回報的版本對不上）</summary>
    private Task<CliVerification> VerifyAsync(AgentProvider provider, CliInstallation? installation, TimeSpan waitForActivity)
    {
        var fallback = CliVerification.Pending(new CliInstallVerdict(CliInstallCondition.Changing, null, "app 正在結束"));
        return EnqueueAsync(provider, () => VerifyNowAsync(provider, installation, null, null, DateTime.UtcNow, waitForActivity, signalSequence: null), fallback);
    }

    /// <summary>
    /// 完整判定並發布（設計 7.2.3；在這種 CLI 的佇列裡執行）：Evaluate(快照, null) 是 Stable 才跑 --version → Evaluate(快照, 輸出) →
    /// CliBinaryIdentityRules.TryRead → BuildVersionChange → 有變化才發布。還在變動時：watcher 宣告的（等滿 15 分鐘）或更新流程等滿 waitForActivity
    /// 之後照當時的樣子判定，其他（立即檢查）交給 watcher。這種 CLI 第一次讀到身分（啟動、剛裝好）時先以不跑程序的判定發布（Old: null），
    /// --version 確認後再更新——中樞在那之前就要知道身分，之後換版才能把啟動的程序認成舊版。
    /// 判定期間執行檔又被換掉（--version 跑的是新檔、快照是舊檔，會被誤判成混版）或 watcher 又有新訊號（signalSequence）：
    /// watcher 的判定不發布（交給後面的訊號），更新流程與立即檢查重新讀一次
    /// </summary>
    private async Task<CliVerification> VerifyNowAsync(AgentProvider provider, CliInstallation? installation, CliInstallSnapshot? snapshot,
        int? updaterPid, DateTime atUtc, TimeSpan waitForActivity, long? signalSequence)
    {
        bool fromSignal = signalSequence != null;
        var name = CliInstallStabilityRules.DisplayName(provider);
        if (installation == null)
            return Apply(provider, null, null, new CliInstallVerdict(CliInstallCondition.Missing, null, $"找不到 {name}"), null, updaterPid, atUtc);

        for (int attempt = 1; ; attempt++)
        {
            snapshot ??= inspector.Inspect(provider, installation, DateTime.UtcNow);
            var verdict = CliInstallStabilityRules.Evaluate(snapshot, null);
            var waited = Stopwatch.StartNew();
            while (verdict.Condition == CliInstallCondition.Changing && !fromSignal && waited.Elapsed < waitForActivity)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
                snapshot = inspector.Inspect(provider, installation, DateTime.UtcNow);
                verdict = CliInstallStabilityRules.Evaluate(snapshot, null);
            }
            if (verdict.Condition == CliInstallCondition.Changing)
            {
                if (!fromSignal && waitForActivity <= TimeSpan.Zero)
                    return CliVerification.Pending(verdict);
                // watcher 等滿 15 分鐘仍在變動（卡住的鎖、npm）或更新流程等滿：照當時的樣子判定，不讓新啟動一直暫緩
                snapshot = WithoutActivity(snapshot);
                verdict = CliInstallStabilityRules.Evaluate(snapshot, null);
            }

            if (verdict.Condition == CliInstallCondition.Stable && GetIdentity(provider) == null && !IsStale(provider, signalSequence))
            {
                var early = CliBinaryIdentityRules.TryRead(installation, verdict.Version!);
                if (early != null && SameStamp(early, snapshot.Executable))
                    Apply(provider, installation, snapshot, verdict, early, updaterPid, atUtc);
            }

            bool versionFailed = false;
            if (verdict.Condition == CliInstallCondition.Stable)
            {
                var (output, failure) = await RunVersionAsync(provider, installation);
                versionFailed = failure != null;
                if (!versionFailed)
                {
                    lock (gate)
                        versionFailures.Remove(provider);
                }
                verdict = failure != null
                    ? new CliInstallVerdict(CliInstallCondition.Broken, null, failure)
                    : CliInstallStabilityRules.Evaluate(snapshot, output);
            }

            bool unchanged = !IsStale(provider, signalSequence)
                             && Nullable.Equals(CliBinaryIdentityRules.TryReadStamp(snapshot.NativeExecutable), snapshot.Executable);
            if (unchanged && verdict.Condition != CliInstallCondition.Stable)
            {
                var applied = Apply(provider, installation, snapshot, verdict, null, updaterPid, atUtc);
                // --version 失敗可能只是暫時的（逾時、防毒正在掃描）：檔案沒變就不會再有訊號，排定稍後自動再判定
                if (versionFailed)
                    ScheduleVersionFailureRecheck(provider);
                return applied;
            }

            var identity = unchanged ? CliBinaryIdentityRules.TryRead(installation, verdict.Version!) : null;
            if (identity != null && SameStamp(identity, snapshot.Executable))
                return Apply(provider, installation, snapshot, verdict, identity, updaterPid, atUtc);

            // 判定期間執行檔又被換掉（或剛好讀不到）、watcher 又有新訊號：watcher 會再宣告一次；更新流程與立即檢查重新讀一次
            Log.Info($"{name} 的安裝在判定期間又變了（第 {attempt} 次）");
            if (fromSignal)
                return CliVerification.Pending(verdict);
            if (attempt >= IdentityReadAttempts)
            {
                var broken = new CliInstallVerdict(CliInstallCondition.Broken, null, $"無法讀取 {name} 執行檔的身分（{installation.NativeExecutable}），請重新安裝");
                return Apply(provider, installation, snapshot, broken, null, updaterPid, atUtc);
            }
            snapshot = null;
            await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
        }
    }

    /// <summary>
    /// 跑 --version（完整判定的一部分；app 跑的 claude 子指令一律帶 DISABLE_AUTOUPDATER=1）。結束碼不是 0、起不來、逾時都算失敗，
    /// 回說明而不是輸出（錯誤訊息裡的數字不能被當成版本）；失敗先等 1 秒再試一次（防毒掃描剛換上的大檔案時第一次可能逾時）
    /// </summary>
    private async Task<(string? Output, string? Failure)> RunVersionAsync(AgentProvider provider, CliInstallation installation)
    {
        for (int attempt = 0; ; attempt++)
        {
            var result = await options.Run(installation.Launcher, VersionArguments, CommandEnvironment, options.VersionTimeout, true, null, lifetime.Token);
            if (result.ExitCode == 0 && !result.TimedOut)
                return (result.Stdout, null);
            if (attempt >= 1)
            {
                var name = CliInstallStabilityRules.DisplayName(provider);
                return (null, result.TimedOut
                    ? $"{name} 的 --version 執行逾時，請重新安裝"
                    : $"無法執行 {installation.DisplayPath}（結束碼 {result.ExitCode}）：{FirstLine(result.Combined)}，請重新安裝");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
        }
    }

    /// <summary>
    /// --version 失敗而判定損壞之後：排定稍後自動再完整判定一次（VersionFailureRecheck，之後每次加倍，連續失敗最多 VersionFailureRechecks 次）。
    /// 那時已經不是損壞（檔案變動、立即檢查、--version 成功過）就不判定；--version 成功時計數歸零
    /// </summary>
    private void ScheduleVersionFailureRecheck(AgentProvider provider)
    {
        int attempt;
        lock (gate)
        {
            attempt = versionFailures.TryGetValue(provider, out var count) ? count + 1 : 1;
            versionFailures[provider] = attempt;
        }
        if (attempt > options.VersionFailureRechecks || Volatile.Read(ref disposed) != 0)
            return;
        var delay = TimeSpan.FromTicks(options.VersionFailureRecheck.Ticks * (1L << Math.Min(attempt - 1, 10)));
        var name = CliInstallStabilityRules.DisplayName(provider);
        Log.Info($"{name} 的 --version 失敗，{delay.TotalSeconds:0.#} 秒後自動再判定一次（第 {attempt} 次）");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, lifetime.Token);
                if (!Get(provider).InstallBroken)
                    return;
                await VerifyAsync(provider, Relocate(provider, resetWatcher: true), TimeSpan.Zero);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"自動重新判定 {name} 失敗：{ex.Message}");
            }
        });
    }

    /// <summary>app 跑的 CLI 子指令（--version、claude update、npm）的環境：剝掉 API Key 與父 Claude session 的變數，另帶 DISABLE_AUTOUPDATER=1</summary>
    private static void CommandEnvironment(IDictionary<string, string?> env) => AccountRules.ApplyCommandEnvironment(null, env);

    /// <summary>
    /// 判定結果 → CliVersionChange（BuildVersionChange；Old＝目前的身分，啟動時是 null）→ 發布，並在同一次更新寫入安裝位置、顯示的版本
    /// （已穩定＝新身分的版本；損壞＝讀得到的版本，CLI 卡仍算已安裝、顯示損壞的說明；不見了＝null）與查過的執行檔
    /// </summary>
    private CliVerification Apply(AgentProvider provider, CliInstallation? installation, CliInstallSnapshot? snapshot, CliInstallVerdict verdict,
        CliBinaryIdentity? identity, int? updaterPid, DateTime atUtc)
    {
        var origin = ResolveOrigin(provider, updaterPid, atUtc);
        var change = CliInstallStabilityRules.BuildVersionChange(provider, GetIdentity(provider), verdict, identity, origin, atUtc);
        if (change == null)
            return CliVerification.Pending(verdict);
        if (change.IsVersionSwitch)
            Log.Info($"{CliInstallStabilityRules.DisplayName(provider)} 換版：{change.FromVersion} → {change.ToVersion}（{(origin == CliChangeOrigin.App ? "app" : "外部")}）");
        else if (change.InstallBroken)
            Log.Warn($"{CliInstallStabilityRules.DisplayName(provider)} 安裝損壞：{change.Detail}");
        Commit(change, s =>
        {
            var version = verdict.Condition switch
            {
                CliInstallCondition.Stable => identity!.Version,
                CliInstallCondition.Broken => s.Version ?? verdict.Version ?? snapshot?.PackageVersion ?? snapshot?.FileVersion ?? snapshot?.VendorVersion,
                CliInstallCondition.Missing => null,
                _ => s.Version,
            };
            var next = s with
            {
                Installation = installation,
                Version = version,
                VersionCheckedFor = installation?.DisplayPath,
                Error = null,
            };
            return next with
            {
                // 換了版或已是最新版：上次安裝／更新失敗的原因已經過時（同 CliUpdateRules.KeepActionError）
                ActionError = !change.IsVersionSwitch && CliUpdateRules.KeepActionError(s.ActionError, s.Version, next.Version, next.Installed, next.UpdateAvailable)
                    ? s.ActionError
                    : null,
            };
        });
        return new CliVerification(verdict, identity);
    }

    /// <summary>去掉進行中的更新跡象（版本鎖、.new／.tmp 暫存檔、npm 程序）：等太久之後照當時的樣子判定</summary>
    private static CliInstallSnapshot WithoutActivity(CliInstallSnapshot snapshot)
    {
        var exeName = string.IsNullOrEmpty(snapshot.NativeExecutable) ? string.Empty : Path.GetFileName(snapshot.NativeExecutable);
        return snapshot with
        {
            Locks = Array.Empty<ClaudeLockObservation>(),
            NpmProcesses = Array.Empty<CliNpmProcess>(),
            VersionEntries = Array.Empty<string>(),
            ExecutableEntries = snapshot.ExecutableEntries
                .Where(e => CliInstallStabilityRules.ParseExecutableMark(exeName, e) is not { Kind: CliUpdaterMarkKind.StagedExecutable })
                .ToList(),
        };
    }

    /// <summary>讀到的身分是不是快照裡的那一份檔案（判定期間沒有又被換掉）</summary>
    private static bool SameStamp(CliBinaryIdentity identity, CliFileStamp? stamp)
        => stamp is { } s && s.VolumeSerial == identity.VolumeSerial && s.FileId == identity.FileId && s.Size == identity.Size
           && s.LastWriteUtc.Ticks == identity.LastWriteUtc.Ticks;

    // ───────────────────────────── 誰觸發的（7.2.4） ─────────────────────────────

    private void BeginOwnUpdate(AgentProvider provider)
    {
        lock (gate)
        {
            ownUpdates[provider] = (DateTime.UtcNow, null);
            ownProcessIds[provider] = new HashSet<int>();
        }
    }

    private void EndOwnUpdate(AgentProvider provider)
    {
        lock (gate)
        {
            if (ownUpdates.TryGetValue(provider, out var update))
                ownUpdates[provider] = (update.StartedUtc, DateTime.UtcNow);
        }
    }

    private void AddOwnProcess(AgentProvider provider, int processId)
    {
        lock (gate)
        {
            if (!ownProcessIds.TryGetValue(provider, out var set))
                ownProcessIds[provider] = set = new HashSet<int>();
            set.Add(processId);
        }
    }

    /// <summary>
    /// 誰觸發的（只影響介面文字）：更新者 PID 是 app 自己的更新程序（claude update、npm 與它們的子孫）或在 app 的工作物件裡＝App；
    /// npm 的檔名沒有 PID，就看 app 當時是不是正在跑自己的更新（含結束後的一小段時間：watcher 去抖之後才宣告）
    /// </summary>
    private CliChangeOrigin ResolveOrigin(AgentProvider provider, int? updaterPid, DateTime atUtc)
    {
        bool inProgress;
        HashSet<int> own;
        lock (gate)
        {
            inProgress = ownUpdates.TryGetValue(provider, out var update)
                         && atUtc >= update.StartedUtc - OwnUpdateSlack
                         && (update.EndedUtc == null || atUtc <= update.EndedUtc.Value + options.OwnUpdateGrace);
            own = inProgress && ownProcessIds.TryGetValue(provider, out var set) ? set.ToHashSet() : new HashSet<int>();
        }
        return CliInstallStabilityRules.ResolveOrigin(updaterPid, inProgress, pid => own.Contains(pid) || options.IsAppProcess(pid));
    }

    /// <summary>更新程序執行期間，每半秒把它的子孫（npm 的 node、postinstall）記成 app 自己的程序；回傳停止記錄用的取消來源</summary>
    private CancellationTokenSource TrackDescendants(AgentProvider provider, int rootProcessId)
    {
        AddOwnProcess(provider, rootProcessId);
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var process in ProcessTreeSnapshot.Capture(rootProcessId).Processes)
                    AddOwnProcess(provider, process.Id);
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
        return stop;
    }

    /// <summary>
    /// 對話程序回報的版本與它啟動用的身分不同（中樞的 AgentRunningVersionEvent；例如啟動器還指向舊檔）：重新定位並重新判定一次——
    /// 找到別的身分就發布，中樞照一般換版把還在跑舊身分的對話（含這一個）重開；還是同一份檔案就只記錄，不再重試。每個身分與版本只做一次
    /// </summary>
    internal void ReportRunningVersion(AgentProvider provider, CliBinaryIdentity? startedWith, string? reportedVersion)
    {
        if (startedWith == null || string.IsNullOrWhiteSpace(reportedVersion) || CliVersionRules.SameRelease(startedWith.Version, reportedVersion)
            || Volatile.Read(ref disposed) != 0)
            return;
        var key = $"{provider}|{startedWith.NativeExecutable}|{startedWith.FileId:x32}|{reportedVersion}";
        lock (gate)
        {
            if (!reportedMismatches.Add(key))
                return;
        }
        var name = CliInstallStabilityRules.DisplayName(provider);
        Log.Warn($"{name} 程序回報 {reportedVersion}，與啟動用的身分 {startedWith.Version} 不同（啟動器可能還指向舊檔）：重新定位後再判定一次");
        _ = Task.Run(async () =>
        {
            try
            {
                var installation = Relocate(provider, resetWatcher: true);
                var verification = await VerifyAsync(provider, installation, TimeSpan.Zero);
                if (verification.Usable && verification.Identity!.Equals(startedWith))
                    Log.Warn($"重新定位後仍是同一份 {name} 執行檔（{startedWith}），程序卻回報 {reportedVersion}：不再重試");
            }
            catch (Exception ex)
            {
                Log.Warn($"重新定位 {name} 失敗：{ex.Message}");
            }
        });
    }

    // ───────────────────────────── 最新版與定期檢查 ─────────────────────────────

    /// <summary>啟動時：清掉之前換下來的受管 Codex 舊版、查兩個 CLI 的最新版（本機版本由 watcher 的第一次檢查負責）</summary>
    private async Task InitialCheckAsync()
    {
        CleanupRetiredManagedCodex();
        if (options.FetchLatestOnStart)
            await Task.WhenAll(Providers.Select(RefreshLatestSafelyAsync));
    }

    private async Task RefreshLatestSafelyAsync(AgentProvider provider)
    {
        try
        {
            await RefreshLatestAsync(provider);
        }
        catch (Exception ex)
        {
            Log.Warn($"查詢 {provider} CLI 最新版失敗：{ex.Message}");
        }
    }

    /// <summary>每 6 小時（與「立即檢查」）：查最新版（立即檢查另外完整判定本機安裝），有新版且開了自動更新就更新</summary>
    private async Task PeriodicAsync(bool verifyLocal)
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        foreach (var provider in Providers)
        {
            try
            {
                if (verifyLocal)
                    await CheckAsync(provider);
                else
                    await RefreshLatestAsync(provider);
                var state = Get(provider);
                bool retry;
                lock (gate)
                    retry = retryRequested.Remove(provider);
                if (state.UpdateAvailable && (autoUpdateEnabled() || retry) && !state.Busy && !DeclinedUpdate(provider, state.Latest))
                {
                    Log.Info($"自動更新 {provider} CLI：{state.Version} → {state.Latest}");
                    await UpdateAsync(provider, automatic: true);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"檢查 {provider} CLI 失敗：{ex.Message}");
            }
        }
    }

    private void RetryLater()
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        try
        {
            timer.Change(options.DeferredRetry, options.LatestCheckInterval);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>上次更新到這個最新版時，指令沒報錯卻沒換版：自動更新不再重試同一版</summary>
    private bool DeclinedUpdate(AgentProvider provider, string? latest)
    {
        lock (gate)
            return latest != null && noEffectLatest.TryGetValue(provider, out var declined) && declined == latest;
    }

    /// <summary>立即檢查兩個 CLI（使用者按「立即檢查」）：完整判定本機安裝、查最新版，有新版且開了自動更新就更新</summary>
    public Task CheckAllAsync() => PeriodicAsync(verifyLocal: true);

    /// <summary>
    /// 查一種 CLI：重新找安裝、完整判定（含 --version；結果經 PublishVersionChange 發布）、向 npm registry 查最新版。
    /// 安裝正在變動時不判定（交給 watcher 安靜下來後宣告），只查最新版
    /// </summary>
    public async Task CheckAsync(AgentProvider provider)
    {
        var installation = Relocate(provider, resetWatcher: true);
        await VerifyAsync(provider, installation, TimeSpan.Zero);
        await RefreshLatestAsync(provider);
    }

    /// <summary>向 npm registry 查最新版並記下（CheckedAt 不論成功與否都更新：網頁的「立即檢查」靠它判斷完成）</summary>
    private async Task RefreshLatestAsync(AgentProvider provider)
    {
        var (latest, latestError) = await FetchLatestAsync(provider);
        var now = DateTime.Now;
        Update(provider, s => CliUpdateRules.AfterLatestCheck(s, latest, latestError, now));
    }

    /// <summary>向官方查最新版（Claude、Codex 查 npm registry；agy 查官方安裝指令用的 manifest）；失敗回（null, 給使用者看的原因）</summary>
    private async Task<(string? Latest, string? Error)> FetchLatestAsync(AgentProvider provider)
    {
        var (url, parse) = LatestSourceOf(provider);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var json = options.FetchText != null ? await options.FetchText(url, cts.Token) : await http.GetStringAsync(url, cts.Token);
            var latest = parse(json);
            return latest != null ? (latest, null) : (null, "無法查詢最新版本：回應看不懂");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Warn($"查詢 {provider} 最新版本失敗（{url}）：{ex.Message}");
            return (null, ex is TaskCanceledException ? "無法查詢最新版本：連線逾時" : $"無法查詢最新版本：{ex.Message}");
        }
    }

    /// <summary>npm 版的套件名稱（agy 沒有 npm 版，不會走到 npm 的流程）</summary>
    private static string PackageOf(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => CliLocator.ClaudeNpmPackage,
        AgentProvider.Codex => CliLocator.CodexNpmPackage,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "這種 CLI 沒有 npm 版"),
    };

    /// <summary>這種 CLI 的最新版從哪裡查、怎麼解讀</summary>
    private static (string Url, Func<string, string?> Parse) LatestSourceOf(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => ($"https://registry.npmjs.org/{CliLocator.ClaudeNpmPackage}/latest", CliVersionRules.ParseNpmLatest),
        AgentProvider.Codex => ($"https://registry.npmjs.org/{CliLocator.CodexNpmPackage}/latest", CliVersionRules.ParseNpmLatest),
        AgentProvider.Antigravity => (AntigravityManifestUrl, CliVersionRules.ParseAntigravityManifest),
        AgentProvider.Grok => (GrokStableChannelUrl, CliVersionRules.ParseVersion),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>grok 的 stable 頻道指標（純文字版本號，例「1.0.52」；install.ps1 與 grok update 也是讀它）</summary>
    internal const string GrokStableChannelUrl = "https://x.ai/cli/stable";

    /// <summary>
    /// agy 官方安裝指令（install.ps1）讀的 Windows x64 manifest：{"version","url","sha512"}。
    /// ARM64 的 manifest 是 windows_arm64.json，版本號相同，查最新版只看 x64 的
    /// </summary>
    internal const string AntigravityManifestUrl = "https://antigravity-cli-auto-updater-974169037036.us-central1.run.app/manifests/windows_amd64.json";

    // ───────────────────────────── 更新與安裝（7.3） ─────────────────────────────

    /// <summary>一次 app 觸發的更新（或安裝）：結果、給中樞的身分、錯誤與說明</summary>
    private sealed class UpdateRun
    {
        public UpdateRun(AgentProvider provider, bool automatic, CliState before)
        {
            Provider = provider;
            Automatic = automatic;
            Before = before;
        }

        public AgentProvider Provider { get; }

        public bool Automatic { get; }

        /// <summary>開始前的狀態（身分、版本）</summary>
        public CliState Before { get; }

        public CliUpdateOutcome Outcome { get; private set; } = CliUpdateOutcome.Cancelled;

        /// <summary>換檔後驗證過（Verified）的身分；損壞或不見了為 null</summary>
        public CliBinaryIdentity? Identity { get; private set; }

        /// <summary>換檔後做過完整判定：UpdateFinished 帶 Identity；沒做過（沒換檔、驗證不出結果）帶目前的身分</summary>
        public bool Verified { get; private set; }

        /// <summary>給使用者看的錯誤（CliState.ActionError）</summary>
        public string? Error { get; private set; }

        /// <summary>沒換版時 CLI 自己的說明（「沒有效果」的原因）</summary>
        public string? Note { get; set; }

        /// <summary>自動更新遇到有對話在跑、有程序在用：安靜地延後</summary>
        public bool Deferred { get; private set; }

        /// <summary>claude update 遇到版本鎖被占住：30 分鐘後再試</summary>
        public bool RetryLater { get; set; }

        public void Defer()
        {
            Outcome = CliUpdateOutcome.Cancelled;
            Deferred = true;
        }

        public void Cancel(string? reason)
        {
            Outcome = CliUpdateOutcome.Cancelled;
            Error = reason;
        }

        public void Fail(string error)
        {
            Outcome = CliUpdateOutcome.Failed;
            Error = error;
        }

        /// <summary>換檔後的判定：可用＝依身分有沒有變分成 Updated／Unchanged（指令報錯就是 Failed，但身分照樣是可用的那一份）；損壞或不見了＝Failed、沒有可用的身分</summary>
        public void Conclude(CliVerification verification, string? error)
        {
            if (verification.Inconclusive)
            {
                Outcome = error != null ? CliUpdateOutcome.Failed : CliUpdateOutcome.Unchanged;
                Error = error;
                return;
            }
            Verified = true;
            if (verification.Usable)
            {
                Identity = verification.Identity;
                bool switched = Before.Identity != null
                    ? !Before.Identity.Equals(Identity)
                    : !string.Equals(Before.Version, Identity!.Version, StringComparison.Ordinal);
                Outcome = error != null ? CliUpdateOutcome.Failed : switched ? CliUpdateOutcome.Updated : CliUpdateOutcome.Unchanged;
                Error = error;
                return;
            }
            Identity = null;
            Outcome = CliUpdateOutcome.Failed;
            Error = error ?? verification.Verdict.Detail ?? "更新後沒有可用的版本";
        }
    }

    /// <summary>開始一個安裝／更新動作：同一種 CLI 同時只有一個（已在進行就回 false）；清掉上次的失敗原因</summary>
    private bool TryBeginAction(AgentProvider provider, string progress, out CliState before)
    {
        lock (gate)
        {
            before = states[provider];
            if (before.Busy)
                return false;
            states[provider] = before with { Busy = true, Progress = progress, ActionError = null };
        }
        RaiseChanged();
        return true;
    }

    /// <summary>更新進行到哪一步（CliView.update.phase、結束前的提示）；第一次離開 None 時記下開始時間</summary>
    private void SetPhase(AgentProvider provider, CliUpdatePhase phase, string? progress)
        => Update(provider, s => s with
        {
            UpdatePhase = phase,
            UpdateStartedAt = s.UpdateStartedAt ?? DateTime.Now,
            Progress = progress ?? s.Progress,
        });

    /// <summary>
    /// 更新到最新版（依安裝方式，7.3）；失敗把原因放進狀態的 ActionError。automatic＝背景自動更新：有對話在跑、有程序在用就安靜地延後。
    /// 呼叫過屏障的 PrepareForUpdateAsync 就一定（finally）呼叫 UpdateFinished；有換版時先發布（VersionChanged）再 UpdateFinished
    /// </summary>
    public async Task UpdateAsync(AgentProvider provider, bool automatic = false)
    {
        var installation = Get(provider).Installation ?? Relocate(provider, resetWatcher: true);
        if (installation == null)
        {
            await InstallAsync(provider);
            return;
        }
        if (!TryBeginAction(provider, "更新中…", out var before))
            return;

        var run = new UpdateRun(provider, automatic, before);
        try
        {
            BeginOwnUpdate(provider);
            switch (CliUpdateRules.RouteFor(installation))
            {
                case CliUpdateRoute.NpmBarrier:
                    await RunNpmUpdateAsync(run, installation);
                    break;
                case CliUpdateRoute.ManagedSwap:
                    await RunManagedUpdateAsync(run, installation);
                    break;
                case CliUpdateRoute.WingetBarrier:
                    await RunWingetUpdateAsync(run, installation, installation.Winget!);
                    break;
                case CliUpdateRoute.SelfUpdate:
                    await RunSelfUpdateAsync(run, installation);
                    break;
                default:
                    throw new InvalidOperationException($"沒有處理的更新方式：{CliUpdateRules.RouteFor(installation)}");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            run.Cancel(null);
        }
        catch (Exception ex)
        {
            run.Fail($"更新失敗：{ex.Message}");
        }
        finally
        {
            EndOwnUpdate(provider);
            EndAction(run);
            FinishPreparedUpdate(run);
        }
        await AfterUpdateAsync(run);
    }

    /// <summary>
    /// npm 版（7.3.1 的全體屏障）：Draining（中樞排空：自動＝Background、手動＝Normal，不設時限，只有 app 結束會取消）→
    /// WaitingForProcesses（app 自己這種 CLI 的程序都結束、清 .codex-*／.claude-code-* 殘留；刪不掉就列出不在工作物件裡的程序並中止）→
    /// Installing（cmd /c npm.cmd install -g，不進工作物件、不設時限）→ Verifying（三方一致；不一致重跑一次 npm，仍不一致＝安裝損壞）
    /// </summary>
    private async Task RunNpmUpdateAsync(UpdateRun run, CliInstallation installation)
    {
        var provider = run.Provider;
        var name = CliInstallStabilityRules.DisplayName(provider);
        var prefix = CliLocator.GetNpmPrefix(installation) ?? Path.GetDirectoryName(installation.DisplayPath)!;
        var roots = NpmRoots(provider, prefix);

        // 第 0 步（排空之前，還沒打擾任何對話）：別人正在改這個安裝（終端機的 npm i -g、互動式 claude 的背景更新）就不更新——
        // 刪殘留會刪掉對方 npm 回滾要用的退役資料夾，兩個 npm 同時改同一個套件，失敗的那個回滾後 CLI 可能從電腦上消失
        if (AbortIfInstallBusy(run, installation, "排空前"))
            return;
        if (run.Automatic && FindForeignProcesses(roots) is { Count: > 0 } early)
        {
            // 自動更新不打擾：終端機或 VS Code 開著的這種 CLI 會讓 npm 留下刪不掉的殘留。不必等排空（Background 會先關掉所有閒置的對話程序）就知道
            Log.Info($"{name} 還有 app 以外的程序在使用（PID {string.Join("、", early.Select(p => p.ProcessId))}），自動更新延後（沒有關掉任何對話）");
            run.Defer();
            return;
        }

        if (!await DrainAndWaitForProcessesAsync(run, roots))
            return;
        // 排空可能等了好幾分鐘：清殘留、跑 npm 之前再確認一次沒有別人在改（不能讓第二個 npm 啟動；npm 自己的改名遇到撞名也會刪掉對方的備份）
        if (AbortIfInstallBusy(run, installation, "換檔前"))
            return;
        var stuck = CleanNpmResidue(provider, prefix);
        var foreign = stuck.Count > 0 || run.Automatic ? FindForeignProcesses(roots) : Array.Empty<CliProcessUse>();
        if (stuck.Count > 0)
        {
            var message = CliUpdateRules.DescribeForeignProcesses(provider, foreign, stuck);
            Log.Warn(message);
            if (run.Automatic)
                run.Defer();
            else
                run.Cancel(message);
            return;
        }
        if (run.Automatic && foreign.Count > 0)
        {
            // 自動更新不打擾：終端機或 VS Code 開著的這種 CLI 會讓 npm 留下刪不掉的殘留（下一次更新就會失敗）
            Log.Info($"{name} 還有 app 以外的程序在使用（PID {string.Join("、", foreign.Select(p => p.ProcessId))}），自動更新延後");
            run.Defer();
            return;
        }

        var npm = options.LocateNpm();
        if (npm == null)
        {
            run.Fail("找不到 npm，無法更新 npm 安裝的 CLI");
            return;
        }
        lifetime.Token.ThrowIfCancellationRequested();
        SetPhase(provider, CliUpdatePhase.Installing, "npm 正在安裝新版…");
        var result = await RunNpmAsync(provider, npm, PackageOf(provider));
        SetPhase(provider, CliUpdatePhase.Verifying, "驗證新版…");
        installation = RelocateAfterNpm(provider, installation);
        var verification = await VerifyAfterUpdateAsync(provider, installation);
        if (verification.Broken)
        {
            Log.Warn($"npm 更新後 {name} 的安裝不一致（{verification.Verdict.Detail}），重新執行一次 npm");
            SetPhase(provider, CliUpdatePhase.Installing, "安裝不一致，重新執行 npm…");
            result = await RunNpmAsync(provider, npm, PackageOf(provider));
            SetPhase(provider, CliUpdatePhase.Verifying, "驗證新版…");
            installation = RelocateAfterNpm(provider, installation);
            verification = await VerifyAfterUpdateAsync(provider, installation);
        }
        run.Note = CliUpdateRules.SummarizeOutput(result.Stdout, result.Stderr);
        var npmVerdict = CliUpdateRules.EvaluateNpm(result.ExitCode, result.Stdout, result.Stderr);
        run.Conclude(verification, npmVerdict.Succeeded ? null : $"npm 更新失敗：{npmVerdict.Error}");
        // 7.3.1 第 6 步：舊程序都結束後刪掉這次的殘留（免得下次撞名）；還有程序在用的留到下次更新前再清
        if (verification.Usable)
            CleanNpmResidue(provider, prefix);
    }

    /// <summary>
    /// 這個安裝正被別人改（另一個 npm 正在安裝或更新全域套件、持有中的版本鎖、剛放的暫存檔）就不更新：手動的說明原因並中止，自動的安靜地延後。
    /// 只在 app 自己的 npm 還沒開始時呼叫（之後看到的 npm 就是自己的）；回 true＝已中止
    /// </summary>
    private bool AbortIfInstallBusy(UpdateRun run, CliInstallation installation, string stage)
    {
        var verdict = CliInstallStabilityRules.Evaluate(inspector.Inspect(run.Provider, installation, DateTime.UtcNow), null);
        if (verdict.Condition != CliInstallCondition.Changing)
            return false;
        var message = CliUpdateRules.DescribeInstallBusy(run.Provider, verdict.Detail);
        Log.Info($"{message}（{stage}）");
        if (run.Automatic)
            run.Defer();
        else
            run.Cancel(message);
        return true;
    }

    /// <summary>
    /// 全體屏障的前兩段：Draining（中樞排空：自動＝Background、手動＝Normal，不設時限，只有 app 結束會取消）→
    /// WaitingForProcesses（app 自己執行檔在 roots 底下的程序都結束，最多 AppProcessExitWait）。
    /// 回 false＝這次不更新（已延後或取消，原因記在 run）；app 正在結束時丟 OperationCanceledException
    /// </summary>
    private async Task<bool> DrainAndWaitForProcessesAsync(UpdateRun run, IReadOnlyList<string> roots)
    {
        var provider = run.Provider;
        var name = CliInstallStabilityRules.DisplayName(provider);
        SetPhase(provider, CliUpdatePhase.Draining, "等對話停下…");
        if (UpdateParticipant is { } barrier)
        {
            lock (gate)
                prepared.Add(provider);
            CliUpdatePreparation preparation;
            try
            {
                preparation = await barrier.PrepareForUpdateAsync(provider, run.Automatic ? CliUpdateUrgency.Background : CliUpdateUrgency.Normal, lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                // app 結束（中樞取消屏障）：不能往下換檔
                Log.Info($"{name} 更新前的排空被取消，這次不更新");
                run.Cancel(null);
                return false;
            }
            var (decision, reason) = CliUpdateRules.DecidePreparation(preparation, run.Automatic, provider);
            if (decision == CliBarrierDecision.Defer)
            {
                run.Defer();
                return false;
            }
            if (decision == CliBarrierDecision.Abort)
            {
                Log.Info($"{name} 這次不更新：{reason}");
                run.Cancel(reason);
                return false;
            }
        }

        SetPhase(provider, CliUpdatePhase.WaitingForProcesses, "等其他程序結束…");
        var remaining = await WaitForAppProcessesAsync(roots, options.AppProcessExitWait);
        // app 正在結束（BeginShutdown）：還沒換檔就不換（結束確認框說的「結束會取消這次更新」）
        lifetime.Token.ThrowIfCancellationRequested();
        if (remaining.Count > 0)
        {
            var message = $"還有 {remaining.Count} 個 app 自己的 {name} 程序（帳戶查詢或登入）沒有結束，這次沒有更新；請稍後再試";
            Log.Warn($"{message}：PID {string.Join("、", remaining)}");
            if (run.Automatic)
                run.Defer();
            else
                run.Cancel(message);
            return false;
        }
        return true;
    }

    /// <summary>
    /// npm 換完檔後重新找安裝（同 ConcludeInstallAsync）：換版可能改了原生執行檔的位置（npm 版 Claude 2.1.113 起套件裡沒有 cli.js、改成 bin\claude.exe；
    /// Codex 的平台套件從套件自己的 vendor 改到巢狀的套件），拿更新前的安裝去驗證會看到已經不在的檔案、誤判安裝損壞。
    /// 只採用同一個 npm 前綴的結果：PATH 上另有別的安裝時，驗證的仍是這次更新的這一份
    /// </summary>
    private CliInstallation RelocateAfterNpm(AgentProvider provider, CliInstallation before)
    {
        var found = Relocate(provider, resetWatcher: true, force: true);
        return found is { Kind: CliInstallKind.Npm } && string.Equals(found.DisplayPath, before.DisplayPath, StringComparison.OrdinalIgnoreCase) ? found : before;
    }

    /// <summary>app 自管的 Codex：下載驗證後舊資料夾改名再放新版（執行中的 exe 所在資料夾可以改名），不需要屏障</summary>
    private async Task RunManagedUpdateAsync(UpdateRun run, CliInstallation installation)
    {
        var provider = run.Provider;
        if (run.Automatic && hasRunningSessions(provider))
        {
            // 自動更新不打擾執行中的對話（換版會在安全點重開它們）：等閒下來再說
            run.Defer();
            return;
        }
        SetPhase(provider, CliUpdatePhase.Installing, "下載中…");
        await InstallManagedCodexAsync(p => Update(provider, s => s with { Progress = p }));
        SetPhase(provider, CliUpdatePhase.Verifying, "驗證新版…");
        run.Note = "GitHub 上的 Windows 版還沒有新版";
        run.Conclude(await VerifyAfterUpdateAsync(provider, installation), null);
    }

    /// <summary>
    /// WinGet 的 portable 套件（例 xAI.GrokBuild）：CLI 自己的 update 不換檔、只叫人改跑 winget，winget 換檔時先刪舊執行檔、執行中的會讓它失敗，所以走屏障。
    /// 打擾任何對話之前先排除做不到的：裝給所有使用者（要系統管理員，app 不提權）、找不到 winget、WinGet 上還沒有新版（社群套件庫常比官方晚幾天）、
    /// app 以外還有程序在用（終端機、系統管理員終端機開的、背景的 leader）。之後 Draining → WaitingForProcesses → 再確認一次沒有別人在用 →
    /// Installing（winget upgrade，不進工作物件、不設時限，同 npm）→ Verifying。自動更新遇到做不到的安靜地略過（還沒有新版、沒有 winget）或延後（有程序在用）
    /// </summary>
    private async Task RunWingetUpdateAsync(UpdateRun run, CliInstallation installation, WingetPackage package)
    {
        var provider = run.Provider;
        var name = CliInstallStabilityRules.DisplayName(provider);
        if (package.MachineScope)
        {
            Log.Info($"{name} 是以 WinGet 安裝給所有使用者（{package.Directory}），app 不提權更新");
            if (run.Automatic)
                run.Cancel(null);
            else
                run.Fail(CliWingetRules.DescribeMachineScope(provider, package));
            return;
        }
        var winget = options.LocateWinget();
        if (winget == null)
        {
            Log.Warn($"找不到 winget，無法更新 WinGet 安裝的 {name}");
            if (run.Automatic)
                run.Cancel(null);
            else
                run.Fail(CliWingetRules.DescribeMissingWinget(provider));
            return;
        }
        var roots = new[] { package.Directory };
        // 自動更新先做便宜的檢查：終端機開著的 grok（背景的 leader 可能一掛好幾天）會讓每 30 分鐘的重試都白問一次 WinGet
        if (run.Automatic && StopIfForeignProcesses(run, roots, "查詢前"))
            return;

        Update(provider, s => s with { Progress = "向 WinGet 查詢新版…" });
        var listed = await options.Run(new AgentLauncher(winget, Array.Empty<string>()), CliWingetRules.ListUpgradeArguments(package.Id),
            AccountRules.ApplyToolEnvironment, WingetQueryTimeout, true, null, lifetime.Token);
        var check = listed.TimedOut
            ? new WingetUpgradeCheck(false, null, $"向 WinGet 查詢新版逾時（{WingetQueryTimeout.TotalSeconds:0} 秒）")
            : CliWingetRules.ParseUpgradeCheck(listed.ExitCode, listed.Stdout, listed.Stderr, package.Id);
        if (check.Error != null)
        {
            Log.Warn($"{name}：{check.Error}");
            if (run.Automatic)
                run.Cancel(null);
            else
                run.Fail(check.Error);
            return;
        }
        if (!check.Available)
        {
            // 不排空任何對話：WinGet 沒有新版，跑 winget upgrade 也只會回「找不到可用的升級」
            var reason = CliWingetRules.DescribeNotYetOnWinget(provider, run.Before.Version, run.Before.Latest);
            Log.Info($"{name}：{reason}");
            run.Cancel(run.Automatic ? null : reason);
            return;
        }

        if (StopIfForeignProcesses(run, roots, "排空前") || !await DrainAndWaitForProcessesAsync(run, roots))
            return;
        // 排空可能等了好幾分鐘：跑 winget 之前再確認一次（使用者可能在終端機又開了一個）
        if (StopIfForeignProcesses(run, roots, "換檔前"))
            return;

        lifetime.Token.ThrowIfCancellationRequested();
        SetPhase(provider, CliUpdatePhase.Installing, check.Version != null ? $"WinGet 正在更新到 {check.Version}…" : "WinGet 正在更新…");
        CancellationTokenSource? tracking = null;
        CliRunResult result;
        try
        {
            result = await options.Run(new AgentLauncher(winget, Array.Empty<string>()), CliWingetRules.UpgradeArguments(package.Id),
                AccountRules.ApplyToolEnvironment, Timeout.InfiniteTimeSpan, false, pid => tracking = TrackDescendants(provider, pid), CancellationToken.None);
        }
        finally
        {
            tracking?.Cancel();
        }
        SetPhase(provider, CliUpdatePhase.Verifying, "驗證新版…");
        var verification = await VerifyAfterUpdateAsync(provider, installation);
        var verdict = CliWingetRules.EvaluateUpgrade(provider, result.ExitCode, result.Stdout, result.Stderr, package.Id);
        if (verdict.NotApplicable)
            run.Note = CliWingetRules.DescribeNotYetOnWinget(provider, run.Before.Version, run.Before.Latest);
        run.Conclude(verification, verdict.Error);
    }

    /// <summary>app 以外還有程序在執行這些資料夾裡的執行檔（WinGet 換不了執行中的檔案）：手動的說明並中止，自動的安靜地延後；回 true＝已中止</summary>
    private bool StopIfForeignProcesses(UpdateRun run, IReadOnlyList<string> roots, string stage)
    {
        var foreign = FindForeignProcesses(roots);
        if (foreign.Count == 0)
            return false;
        var message = CliWingetRules.DescribeForeignProcesses(run.Provider, foreign);
        Log.Info($"{message}（{stage}，{string.Join("、", foreign.Select(p => $"PID {p.ProcessId} {p.ImagePath}"))}）");
        if (run.Automatic)
            run.Defer();
        else
            run.Cancel(message);
        return true;
    }

    /// <summary>
    /// CLI 自己的 update（Claude 原生安裝與其他安裝方式）：換檔用改名，執行中的舊程序照常回應，不需要屏障。
    /// 不進工作物件、不設時限；解析輸出（「Another Claude process」＝30 分鐘後再試），最後以 FileVersionInfo 驗證
    /// </summary>
    private async Task RunSelfUpdateAsync(UpdateRun run, CliInstallation installation)
    {
        var provider = run.Provider;
        if (run.Automatic && hasRunningSessions(provider))
        {
            run.Defer();
            return;
        }
        SetPhase(provider, CliUpdatePhase.Installing, "更新中…");
        var exe = installation.NativeExecutable ?? installation.Launcher.FileName;
        string? versionBefore = provider == AgentProvider.Claude ? CliBinaryIdentityRules.ReadClaudeFileVersion(exe) : run.Before.Version;
        var result = await options.Run(installation.Launcher, UpdateArguments, CommandEnvironment, Timeout.InfiniteTimeSpan, false,
            pid => AddOwnProcess(provider, pid), CancellationToken.None);
        SetPhase(provider, CliUpdatePhase.Verifying, "驗證新版…");
        var verification = await VerifyAfterUpdateAsync(provider, installation);
        string? versionAfter = provider == AgentProvider.Claude ? CliBinaryIdentityRules.ReadClaudeFileVersion(exe) : verification.Identity?.Version;
        var parsed = provider == AgentProvider.Claude
            ? CliUpdateRules.ParseClaudeUpdate(result.ExitCode, result.Stdout, result.Stderr)
            : result.ExitCode == 0
                ? new ClaudeUpdateResult(ClaudeUpdateKind.Declined, null, null, null, CliUpdateRules.SummarizeOutput(result.Stdout, result.Stderr))
                : new ClaudeUpdateResult(ClaudeUpdateKind.Failed, null, null, null, CliUpdateRules.DescribeFailure(result.Stdout, result.Stderr));
        var judgement = CliUpdateRules.JudgeSelfUpdate(parsed, versionBefore, versionAfter);
        run.Note = judgement.Note;
        run.RetryLater = judgement.RetryLater;
        run.Conclude(verification, judgement.Error);
    }

    /// <summary>換檔後的驗證：排在這種 CLI 的佇列，安裝還在變動（外部的 npm、版本鎖）最多等 ActivityWait；驗證不出結果再試兩次</summary>
    private async Task<CliVerification> VerifyAfterUpdateAsync(AgentProvider provider, CliInstallation installation)
    {
        CliVerification verification = CliVerification.Pending(new CliInstallVerdict(CliInstallCondition.Changing, null, null));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            verification = await VerifyAsync(provider, installation, options.ActivityWait);
            if (!verification.Inconclusive || lifetime.IsCancellationRequested)
                break;
            await Task.Delay(TimeSpan.FromSeconds(2), lifetime.Token);
        }
        return verification;
    }

    /// <summary>
    /// 安裝 CLI：Claude 開 PowerShell 視窗跑官方安裝指令（等視窗關閉、看結束碼）；
    /// Codex 有 npm 就 npm install -g（已經有 npm 版＝重新安裝，與更新一樣走屏障），沒有就自 GitHub 官方 release 下載整包解壓到 app 資料夾。
    /// 裝好後重新找安裝並完整判定（發布新身分）
    /// </summary>
    public async Task InstallAsync(AgentProvider provider)
    {
        if (!TryBeginAction(provider, "安裝中…", out var before))
            return;
        var run = new UpdateRun(provider, automatic: false, before);
        try
        {
            BeginOwnUpdate(provider);
            if (provider is AgentProvider.Claude or AgentProvider.Antigravity or AgentProvider.Grok)
            {
                Update(provider, s => s with { Progress = "已開啟安裝視窗，完成後關閉視窗即可" });
                using var process = provider switch
                {
                    AgentProvider.Claude => options.StartClaudeInstaller(),
                    AgentProvider.Antigravity => options.StartAntigravityInstaller(),
                    _ => options.StartGrokInstaller(),
                };
                // 安裝視窗與它開的 claude install（agy install）都是 app 自己的更新程序：換檔留下的 .new.<pid>、版本鎖帶的是它們的 PID，
                // watcher 比安裝視窗先宣告換版時，來源才不會被當成「外部更新」
                var tracking = TrackDescendants(provider, process.Id);
                try
                {
                    await process.WaitForExitAsync();
                }
                finally
                {
                    tracking.Cancel();
                }
                if (process.ExitCode != 0)
                    run.Fail($"{AgentProviders.ProductName(provider)} 安裝失敗（結束碼 {process.ExitCode}），原因請看安裝視窗裡的訊息");
                else
                    await ConcludeInstallAsync(run);
            }
            else if (provider == AgentProvider.Codex)
            {
                var npm = options.LocateNpm();
                var existing = Get(provider).Installation ?? Relocate(provider, resetWatcher: true);
                if (npm != null && existing is { Kind: CliInstallKind.Npm })
                {
                    // 已經有 npm 版（只是執行不了或安裝不一致）：與更新一樣，先排空再換檔，否則 npm 會 EBUSY
                    await RunNpmUpdateAsync(run, existing);
                }
                else if (npm != null)
                {
                    SetPhase(provider, CliUpdatePhase.Installing, "npm 正在安裝…");
                    var result = await RunNpmAsync(provider, npm, CliLocator.CodexNpmPackage);
                    var verdict = CliUpdateRules.EvaluateNpm(result.ExitCode, result.Stdout, result.Stderr);
                    if (!verdict.Succeeded)
                        run.Fail($"npm 安裝失敗：{verdict.Error}");
                    else
                        await ConcludeInstallAsync(run);
                }
                else
                {
                    SetPhase(provider, CliUpdatePhase.Installing, "下載中…");
                    await InstallManagedCodexAsync(p => Update(provider, s => s with { Progress = p }));
                    await ConcludeInstallAsync(run);
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            run.Cancel(null);
        }
        catch (Exception ex)
        {
            run.Fail($"安裝失敗：{ex.Message}");
        }
        finally
        {
            EndOwnUpdate(provider);
            EndAction(run);
            FinishPreparedUpdate(run);
        }

        // 安裝指令沒報錯卻還是找不到執行檔（例如 npm 的全域資料夾不在 PATH 上）：也要告訴使用者，不能安靜地回到「未安裝」
        if (run.Error == null && run.Outcome != CliUpdateOutcome.Cancelled && Get(provider).Installation == null)
        {
            var missing = provider switch
            {
                AgentProvider.Claude => "Claude Code 安裝沒有完成（找不到 claude.exe），請查看安裝視窗裡的訊息",
                AgentProvider.Codex => "Codex 安裝指令已結束，但找不到 codex（npm 的全域資料夾可能不在 PATH 上）",
                AgentProvider.Antigravity => @"Antigravity 安裝沒有完成（%LOCALAPPDATA%\agy\bin 找不到 agy.exe），請查看安裝視窗裡的訊息",
                AgentProvider.Grok => @"Grok Build 安裝沒有完成（%USERPROFILE%\.grok\bin 找不到 grok.exe），請查看安裝視窗裡的訊息",
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
            };
            Update(provider, s => s with { ActionError = missing });
        }
        await RefreshLatestSafelyAsync(provider);
    }

    /// <summary>新裝好的 CLI：重新找安裝位置（可能是新的安裝方式）並完整判定</summary>
    private async Task ConcludeInstallAsync(UpdateRun run)
    {
        var provider = run.Provider;
        SetPhase(provider, CliUpdatePhase.Verifying, "驗證新版…");
        var installation = Relocate(provider, resetWatcher: true, force: true);
        if (installation == null)
        {
            run.Conclude(new CliVerification(new CliInstallVerdict(CliInstallCondition.Missing, null, null), null), null);
            return;
        }
        run.Conclude(await VerifyAfterUpdateAsync(provider, installation), null);
    }

    /// <summary>動作結束：回到沒有在更新的狀態（中樞的「npm 正在換檔」看 Busy，所以要在 UpdateFinished 之前），記下要給使用者看的原因</summary>
    private void EndAction(UpdateRun run)
    {
        var error = run.Error;
        if (error != null)
            Log.Warn($"{run.Provider} CLI {(run.Automatic ? "自動" : string.Empty)}更新失敗：{error}");
        Update(run.Provider, s => s with
        {
            Busy = false,
            Progress = null,
            UpdatePhase = CliUpdatePhase.None,
            UpdateStartedAt = null,
            ActionError = error ?? (run.RetryLater && !run.Automatic ? run.Note : null),
        });
    }

    /// <summary>
    /// 這次更新呼叫過 PrepareForUpdateAsync 時，通知參與者更新已結束（成功、沒換版、失敗、取消都要通知）；沒呼叫過就什麼都不做。
    /// Identity＝換檔後驗證過的身分（損壞、不見了為 null：停下的對話不重開、以提示收尾）；沒有換檔或驗證不出結果時是目前的身分
    /// （沒換版、取消、失敗都以仍可用的版本接續停下的對話）
    /// </summary>
    private void FinishPreparedUpdate(UpdateRun run)
    {
        bool wasPrepared;
        lock (gate)
            wasPrepared = prepared.Remove(run.Provider);
        if (!wasPrepared || UpdateParticipant is not { } barrier)
            return;
        var identity = run.Verified ? run.Identity : GetIdentity(run.Provider);
        try
        {
            barrier.UpdateFinished(new CliUpdateCompletion(run.Provider, run.Outcome, identity, run.Error));
        }
        catch (Exception ex)
        {
            Log.Warn($"通知 {run.Provider} CLI 更新結束失敗：{ex.Message}");
        }
    }

    /// <summary>
    /// 更新之後（UpdateFinished 已送出）：延後的排定再試；查最新版；指令沒報錯、版本卻沒變（stable 頻道、由 winget 管理…）就說明原因、
    /// 同一版不再自動重試
    /// </summary>
    private async Task AfterUpdateAsync(UpdateRun run)
    {
        var provider = run.Provider;
        if (run.Deferred)
        {
            Log.Info($"{provider} CLI 有對話在跑或有程序在用安裝，自動更新延後");
            RetryLater();
            return;
        }
        if (run.RetryLater)
        {
            lock (gate)
                retryRequested.Add(provider);
            Log.Info($"{provider} CLI：{run.Note}");
            RetryLater();
            return;
        }
        if (run.Outcome == CliUpdateOutcome.Cancelled)
            return;
        await RefreshLatestSafelyAsync(provider);
        var after = Get(provider);
        if (run.Error == null && CliUpdateRules.IsNoEffectUpdate(run.Before.Version, after.Version, after.UpdateAvailable))
        {
            var reason = $"更新指令已執行，但版本仍是 {after.Version}：{run.Note ?? "CLI 沒有說明原因"}";
            lock (gate)
                noEffectLatest[provider] = after.Latest;
            Update(provider, s => s with { ActionError = reason });
            Log.Warn($"{provider} CLI 更新沒有換版：{reason}");
        }
    }

    /// <summary>
    /// Claude 官方安裝指令：下載失敗或安裝程式報錯（它設了 $ErrorActionPreference = Stop，錯誤會直接中止）時，
    /// 視窗停下來等使用者看完並以結束碼 1 結束；成功的話 5 秒後自動關閉
    /// </summary>
    internal const string ClaudeInstallCommand =
        "try { irm https://claude.ai/install.ps1 -ErrorAction Stop | iex } "
        + "catch { Write-Host $_ -ForegroundColor Red; Read-Host '安裝失敗，按 Enter 關閉視窗'; exit 1 }; "
        + "Start-Sleep -Seconds 5";

    /// <summary>
    /// Antigravity CLI 官方安裝指令（https://antigravity.google/cli/install.ps1：讀 manifest、驗 SHA-512、放到 %LOCALAPPDATA%\agy\bin、
    /// 跑 agy install 設定 PATH）。腳本失敗時 exit 1，`iex` 裡的 exit 會結束整個 PowerShell，所以另外檢查執行檔在不在；成功的話 5 秒後自動關閉
    /// </summary>
    internal const string AntigravityInstallCommand =
        "try { irm https://antigravity.google/cli/install.ps1 -ErrorAction Stop | iex } "
        + "catch { Write-Host $_ -ForegroundColor Red; Read-Host '安裝失敗，按 Enter 關閉視窗'; exit 1 }; "
        + "if (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'agy\\bin\\agy.exe'))) { Read-Host '安裝沒有完成，按 Enter 關閉視窗'; exit 1 }; "
        + "Start-Sleep -Seconds 5";

    /// <summary>
    /// Grok Build 官方安裝指令（https://x.ai/cli/install.ps1：從 x.ai/cli 下載最新 stable 版放到 %USERPROFILE%\.grok\bin、
    /// 加進使用者 PATH、在 ~/.grok/config.toml 記 installer = "internal"，之後以 grok update 直接更新到最新版）。
    /// 腳本失敗時 exit 1，`iex` 裡的 exit 會結束整個 PowerShell，所以另外檢查執行檔在不在；成功的話 5 秒後自動關閉
    /// </summary>
    internal const string GrokInstallCommand =
        "Import-Module Microsoft.PowerShell.Utility -ErrorAction SilentlyContinue; "
        + "try { irm https://x.ai/cli/install.ps1 -ErrorAction Stop | iex } "
        + "catch { Write-Host $_ -ForegroundColor Red; Read-Host '安裝失敗，按 Enter 關閉視窗'; exit 1 }; "
        + "if (-not (Test-Path (Join-Path $env:USERPROFILE '.grok\\bin\\grok.exe'))) { Read-Host '安裝沒有完成，按 Enter 關閉視窗'; exit 1 }; "
        + "Start-Sleep -Seconds 5";

    // ───────────────────────────── npm（7.3.1） ─────────────────────────────

    /// <summary>
    /// 跑使用者實際會用的 npm：cmd /d /c call "npm.cmd" install -g 套件@latest（npm.cmd 會改用前綴底下較新的 npm）。
    /// 不加入 app 的工作物件、也不設時限：npm 先把舊版改名退役、下載解壓完新版才放好，半途被結束（逾時、結束 app）不會還原，
    /// CLI 就從這台電腦消失了；npm 自己有網路逾時，失敗時會自己還原。執行期間把它的子孫記成 app 自己的程序（判斷換版來源）
    /// </summary>
    private async Task<CliRunResult> RunNpmAsync(AgentProvider provider, string npmCmd, string package)
    {
        var cmd = new AgentLauncher(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Array.Empty<string>());
        CancellationTokenSource? tracking = null;
        try
        {
            return await options.Run(cmd, CliUpdateRules.NpmInstallArguments(npmCmd, package), AccountRules.ApplyToolEnvironment, Timeout.InfiniteTimeSpan,
                false, pid => tracking = TrackDescendants(provider, pid), CancellationToken.None);
        }
        finally
        {
            tracking?.Cancel();
        }
    }

    /// <summary>npm 版要確認、清理的資料夾：前綴底下這種 CLI 的 scope（node_modules\@openai 或 @anthropic-ai），前綴是連結時連同實際位置（nvm-windows 的 nodejs）</summary>
    private static IReadOnlyList<string> NpmRoots(AgentProvider provider, string prefix)
    {
        var roots = new List<string> { CliUpdateRules.NpmScopeDirectory(provider, prefix) };
        try
        {
            if (new DirectoryInfo(prefix).ResolveLinkTarget(returnFinalTarget: true) is { } target)
                roots.Add(CliUpdateRules.NpmScopeDirectory(provider, target.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return roots;
    }

    /// <summary>等 app 自己（工作物件裡，含 node 開的 codex.exe）執行檔在這些資料夾底下的程序都結束，最多 wait；回傳還沒結束的 PID</summary>
    private async Task<IReadOnlyList<int>> WaitForAppProcessesAsync(IReadOnlyList<string> roots, TimeSpan wait)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var remaining = options.AppProcessIds().Where(pid => CliUpdateRules.IsUnderAny(options.ProcessImagePath(pid), roots)).ToList();
            if (remaining.Count == 0 || waited.Elapsed >= wait)
                return remaining;
            await Task.Delay(TimeSpan.FromMilliseconds(250), lifetime.Token);
        }
    }

    /// <summary>不在 app 工作物件裡、執行檔在這些資料夾底下的程序（QueryFullProcessImageName：上層資料夾被 npm 退役改名時仍是原路徑）</summary>
    private IReadOnlyList<CliProcessUse> FindForeignProcesses(IReadOnlyList<string> roots)
    {
        var app = options.AppProcessIds().ToHashSet();
        var processes = options.AllProcessIds().Where(pid => pid != Environment.ProcessId).Select(pid => (pid, options.ProcessImagePath(pid), app.Contains(pid)));
        return CliUpdateRules.FindProcessesUnder(processes, roots).Where(p => !p.InAppJob).ToList();
    }

    /// <summary>刪掉 npm 退役留下的殘留（.codex-*、.claude-code-*）；回傳刪不掉的（有程序還在執行裡面的舊版）</summary>
    private static IReadOnlyList<string> CleanNpmResidue(AgentProvider provider, string prefix)
    {
        var scope = CliUpdateRules.NpmScopeDirectory(provider, prefix);
        var stuck = new List<string>();
        try
        {
            if (!Directory.Exists(scope))
                return stuck;
            foreach (var dir in Directory.EnumerateDirectories(scope).Where(d => CliUpdateRules.IsNpmResidue(provider, Path.GetFileName(d))).ToList())
            {
                if (DirectoryRemover.TryDelete(dir, attempts: 3, retryDelayMs: 200))
                    Log.Info($"已刪除 npm 更新留下的殘留 {dir}");
                else
                    stuck.Add(dir);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"清理 npm 殘留失敗：{ex.Message}");
        }
        return stuck;
    }

    // ───────────────────────────── app 自管的 Codex ─────────────────────────────

    /// <summary>
    /// 自 OpenAI 官方 GitHub 下載最新 Windows 版、比對 SHA-256、整包解壓到 app 資料夾，主程式改名 codex.exe
    /// （沿用 SampleShop 實測可用的配置：同目錄的輔助程式必須一起保留）
    /// </summary>
    private async Task InstallManagedCodexAsync(Action<string> progress)
    {
        var (assetName, mainExe) = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => ("codex-x86_64-pc-windows-msvc.exe.zip", "codex-x86_64-pc-windows-msvc.exe"),
            Architecture.Arm64 => ("codex-aarch64-pc-windows-msvc.exe.zip", "codex-aarch64-pc-windows-msvc.exe"),
            _ => throw new PlatformNotSupportedException($"Codex 沒有提供 {RuntimeInformation.OSArchitecture} 架構的 Windows 版本"),
        };

        progress("取得最新版本資訊…");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/openai/codex/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request);
        var releaseJson = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"讀取 Codex 最新版本資訊失敗（HTTP {(int)response.StatusCode}）");

        var asset = (JObject.Parse(releaseJson)["assets"] as JArray ?? new JArray()).OfType<JObject>()
            .FirstOrDefault(a => a.Value<string>("name") == assetName)
            ?? throw new InvalidOperationException($"最新版本找不到 {assetName}");
        var url = asset.Value<string>("browser_download_url") ?? throw new InvalidOperationException("沒有下載網址");
        var digest = asset.Value<string>("digest");
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("最新版本沒有提供 SHA-256，無法驗證，已放棄安裝");
        var expected = digest[7..].Trim().ToLowerInvariant();

        var downloadDir = Path.Combine(AppPaths.CacheDirectory, "codex-download");
        Directory.CreateDirectory(downloadDir);
        var zipPath = Path.Combine(downloadDir, assetName);
        var installDir = AppPaths.ManagedCodexDirectory;
        var stagingDir = installDir + ".new";
        CleanupRetiredManagedCodex();
        // 每收到資料就重新計時：連線斷了卻沒收到 RST（闔上筆電、換 Wi-Fi）時讀取會永遠等下去，HttpClient.Timeout 只管到收到標頭為止
        using var overall = new CancellationTokenSource(DownloadTimeLimit);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        stall.CancelAfter(DownloadStallTimeout);
        try
        {
            using (var download = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token))
            {
                download.EnsureSuccessStatusCode();
                long total = download.Content.Headers.ContentLength ?? asset.Value<long?>("size") ?? 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var source = await download.Content.ReadAsStreamAsync(stall.Token))
                await using (var target = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    int read;
                    var lastReport = DateTime.MinValue;
                    while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
                    {
                        stall.CancelAfter(DownloadStallTimeout);
                        await target.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                        hash.AppendData(buffer, 0, read);
                        done += read;
                        if (DateTime.Now - lastReport > TimeSpan.FromMilliseconds(300))
                        {
                            lastReport = DateTime.Now;
                            progress(total > 0 ? $"下載中 {done * 100 / total}%（{done / 1048576.0:0}／{total / 1048576.0:0} MB）" : $"下載中（{done / 1048576.0:0} MB）");
                        }
                    }
                }

                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (actual != expected)
                    throw new InvalidDataException($"下載的檔案 SHA-256 不符（預期 {expected}，實際 {actual}），已放棄安裝");
            }

            progress("解壓縮中…");
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            ZipFile.ExtractToDirectory(zipPath, stagingDir);
            var exe = Path.Combine(stagingDir, mainExe);
            if (!File.Exists(exe))
                throw new InvalidDataException($"安裝檔內找不到 {mainExe}");
            File.Move(exe, Path.Combine(stagingDir, "codex.exe"));

            // 舊版整個資料夾先改名移開再放新版：執行中的 codex.exe 不能刪、但所在的資料夾可以改名
            // （直接 Directory.Delete 會先刪光同目錄的輔助程式才失敗，留下只剩主程式的殘缺安裝）
            string? retired = null;
            if (Directory.Exists(installDir))
            {
                retired = $"{installDir}.old-{DateTime.Now:yyyyMMddHHmmssfff}";
                try
                {
                    Directory.Move(installDir, retired);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("舊版 Codex 正在使用中，請先停止 Codex 對話後再更新", ex);
                }
            }
            try
            {
                Directory.Move(stagingDir, installDir);
            }
            catch (Exception ex) when (retired != null && ex is IOException or UnauthorizedAccessException)
            {
                // 新版放不進去：把舊版放回原位，至少還能用
                try
                {
                    Directory.Move(retired, installDir);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"還原舊版 Codex 失敗：{restore.Message}");
                }
                throw;
            }
            // 舊版還在執行時刪不乾淨，剩下的下次啟動或下次更新再清
            if (retired != null)
                TryDelete(() => Directory.Delete(retired, recursive: true));
        }
        catch (OperationCanceledException) when (stall.IsCancellationRequested)
        {
            throw new TimeoutException(overall.IsCancellationRequested
                ? $"下載 Codex 超過 {DownloadTimeLimit.TotalMinutes:0} 分鐘還沒完成，已放棄"
                : $"下載 Codex 時超過 {DownloadStallTimeout.TotalSeconds:0} 秒沒有收到資料（網路可能斷了），已放棄");
        }
        finally
        {
            TryDelete(() => File.Delete(zipPath));
            TryDelete(() =>
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
            });
        }
    }

    /// <summary>清掉之前更新換下來的受管 Codex 舊版（codex.old-時間；換版當下還在執行、刪不乾淨的留到這裡）</summary>
    private static void CleanupRetiredManagedCodex()
    {
        var installDir = AppPaths.ManagedCodexDirectory;
        var parent = Path.GetDirectoryName(installDir);
        try
        {
            if (parent == null || !Directory.Exists(parent))
                return;
            foreach (var dir in Directory.GetDirectories(parent, Path.GetFileName(installDir) + ".old-*"))
                TryDelete(() => Directory.Delete(dir, recursive: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string FirstLine(string? text)
        => (text ?? string.Empty).Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "原因不明";

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// app 要結束了（結束確認之後、中樞收尾之前呼叫）：還沒開始換檔的更新一律取消（排空、等程序結束——不會再啟動 npm），
    /// 進行中的判定與查詢也停下；已經在跑的 npm 與 claude update 不中斷（不在工作物件裡，照常跑完）
    /// </summary>
    public void BeginShutdown()
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        lifetime.Cancel();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        // 進行中的屏障排空、等程序結束、--version 都停下（npm 與 claude update 本身不在工作物件裡，照常跑完）
        lifetime.Cancel();
        timer.Dispose();
        watcher?.Dispose();
    }
}
