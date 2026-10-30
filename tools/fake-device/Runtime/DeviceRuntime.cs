using System.Security.Cryptography;
using System.Text;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.RemoteExec;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace FakeDevice;

/// <summary>
/// 劇本與測試用的假帳戶：Claude、Codex、Grok 只建隔離帳戶（設定資料夾在資料根底下，絕不是 ConfigDirectory=null 的系統預設帳戶）。
/// Antigravity 沒有隔離帳戶（app 的也只有本機預設帳戶），建的是同樣的形狀（ConfigDirectory=null）：它的登入在 Windows 認證管理員、app 不讀任何憑證，
/// 假裝置又不執行任何 CLI，所以不碰這台電腦的 agy（DeviceRuntime.NewAntigravityAccount）
/// </summary>
/// <param name="Alias">劇本裡的名字：32 個十六進位字元就直接當帳戶 id，否則由它算出固定的 id（每次執行都一樣）</param>
internal sealed record FakeAccountSpec(string Alias, AgentProvider Provider, string? Label, string? Color, string? Email, string? OrgId, string? Plan);

/// <summary>一台假裝置的設定</summary>
internal sealed record DeviceRuntimeOptions
{
    /// <summary>這台的資料根（sync\、accounts\、conversations\）。<see cref="Persist"/> 時必須就是 AppPaths.Root</summary>
    public required string DataRoot { get; init; }

    public string MachineName { get; init; } = "FAKE-DEVICE";

    public string WindowsUser { get; init; } = "fake";

    /// <summary>MYRIAGENT_SYNC_DEV_TRUST：連迴路位址的伺服器時接受驗不過的憑證（本機的 Testing MyriagentHub 是自簽憑證）</summary>
    public bool DevTrust { get; init; }

    /// <summary>MYRIAGENT_SYNC_SERVER：配對與連線都改連這台（開發模式）</summary>
    public Uri? ServerOverride { get; init; }

    public string AppVersion { get; init; } = DeviceRuntime.DefaultAppVersion;

    /// <summary>
    /// 帳戶清單、設定與假 CLI 狀態存在資料根（工具：可以和真的 app 共用資料根）。false＝只在記憶體（同一個程序跑多台：AppPaths 是全域的，
    /// 帳戶清單改成唯讀、不送 link.removed）
    /// </summary>
    public bool Persist { get; init; } = true;

    /// <summary>啟動時另外放進去的帳戶（不存檔模式用；存檔模式由劇本的 addAccount 加）</summary>
    public IReadOnlyList<FakeAccountSpec> Accounts { get; init; } = [];

    /// <summary>配對時建裝置金鑰（測試的記憶體金鑰）；null＝CNG（使用者的金鑰存放區，解除配對時刪）</summary>
    public Func<string, bool, IDeviceSigner>? KeyFactory { get; init; }

    /// <summary>建引擎（測試換簽章者與退避步階）；null＝正式的 SyncEngineLoader.Create（<see cref="EphemeralKey"/>、<see cref="MeasureTransport"/> 時經 <see cref="TransportMeter"/>）</summary>
    public Func<SyncEngineCreateArgs, ISyncEngine>? EngineFactory { get; init; }

    /// <summary>
    /// 只在記憶體的裝置金鑰（WP4.9 步 8，<c>session --ephemeral-key</c>）：配對時建 <see cref="EphemeralDeviceKey"/>、引擎也用它，不碰 CNG 的金鑰存放區。
    /// 程序結束金鑰就沒了：只給同一個程序裡配對、跑、解除配對的用法。<see cref="KeyFactory"/>、<see cref="EngineFactory"/> 有給時照給的
    /// </summary>
    public bool EphemeralKey { get; init; }

    /// <summary>引擎的傳輸層包一層 <see cref="TimingTransport"/>（status 的 batchAck 與 liveRelay.sent；<see cref="EngineFactory"/> 有給時照給的）</summary>
    public bool MeasureTransport { get; init; }

    /// <summary>
    /// 假 Codex app-server 的情境（WP4.9 步 13，<c>--fake-codex &lt;情境&gt;</c>；<see cref="FakeCodexAppServerScript.Scenarios"/>）：帳戶管理員找 Codex 的 CLI 時
    /// 交出跑 <see cref="FakeCodexAppServerScript"/> 的假啟動器（<see cref="FakeLauncherRules"/>；資料根要在 TEMP 底下）。null＝照舊找不到任何 CLI
    /// </summary>
    public string? FakeCodex { get; init; }

    /// <summary>
    /// 遠端執行指令（docs/sync-protocol.md 14.11）：給了就接上 <see cref="RemoteExecService"/>（宣告 exec1、被操作時真的執行指令、高風險的問它；
    /// 本機管道照資料根命名，myriagent-remote 可以連）。null＝不接（照舊不宣告 exec1）
    /// </summary>
    public IRemoteExecConfirmer? RemoteExecConfirmer { get; init; }
}

/// <summary>看不到的主視窗：中樞的指令要有 view；記下最近的訊息（除錯用）、toast 與每個對話最後的輸入區狀態</summary>
internal sealed class HeadlessView(string id) : IAgentView
{
    private readonly Queue<JObject> recent = new();

    public string Id { get; } = id;

    public bool IsPopout => false;

    /// <summary>收到的錯誤 toast（level＝error；UI 執行緒）</summary>
    public List<string> Toasts { get; } = [];

    /// <summary>對話 id → 最後一則 composer 訊息的 composer（附件、處理中；UI 執行緒。chat 等附件處理完才送出）</summary>
    public Dictionary<string, JObject> Composers { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<JObject> Recent => recent.ToList();

    public void PostToWeb(JObject message)
    {
        if (message.Value<string>("type") == "toast" && message.Value<string>("level") == "error" && message.Value<string>("text") is { } text)
            Toasts.Add(text);
        if (message.Value<string>("type") == "composer" && message.Value<string>("conversationId") is { } conversationId && message["composer"] is JObject composer)
            Composers[conversationId] = composer;
        recent.Enqueue(message);
        while (recent.Count > 50)
            recent.Dequeue();
    }
}

/// <summary>
/// 一台假裝置：照 AppController 的接法建中樞、帳戶管理員與同步協調者（同一套 AgentHub、AccountManager、SyncCoordinator、SyncEngine），但
/// <list type="bullet">
/// <item>不呼叫 <see cref="AccountManager.Initialize"/>（全新的資料根會建立指向真 ~/.claude、~/.codex 的系統預設帳戶並查詢它們）：
/// 帳戶以 Seed 放入，只放設定資料夾在資料根底下的隔離帳戶。</item>
/// <item>CLI 管理員不找這台電腦的 CLI、不跑背景檢查、拒絕執行任何指令；帳戶管理員拿不到任何 CLI（ResolveLauncher 一律 null；只有
/// <see cref="DeviceRuntimeOptions.FakeCodex"/> 時 Codex 拿到假 app-server，DeviceRuntime.FakeCodex.cs）、開瀏覽器只計數；
/// 會話是 <see cref="ScriptedSession"/>。</item>
/// <item>整個 HttpClient 是 <see cref="FakeUsageEndpoint"/>：Claude 的用量走真的閘門與 api 路徑，但端點是假的，不連外網。</item>
/// </list>
/// 狀態只在 <see cref="Host"/> 的 UI 執行緒上動（<see cref="OnUi{T}"/>）。對話的操作（chat、回補狀態）在 DeviceRuntime.Chat.cs
/// </summary>
internal sealed partial class DeviceRuntime : IAsyncDisposable
{
    public const string DefaultAppVersion = "0.1.0";

    public const string ViewId = "fake-device";

    /// <summary>假 CLI 狀態的存檔（資料根底下；真的 app 不讀它）</summary>
    public const string CliStateFile = "fake-device-cli.json";

    private readonly DeviceRuntimeOptions options;
    private readonly Dictionary<AgentProvider, CliInstallation?> fakeInstalls = new();
    private readonly object installGate = new();
    private readonly Dictionary<AgentProvider, string?> cliVersions = new();
    private readonly EphemeralDeviceKey? ephemeralKey;
    private AgentLauncher? fakeCodexLauncher;
    private int refusedCli;
    private int openedBrowser;
    private bool stopped;

    private DeviceRuntime(DeviceRuntimeOptions options)
    {
        this.options = options;
        ephemeralKey = options.EphemeralKey ? new EphemeralDeviceKey() : null;
        Host = new HeadlessHost($"fake-device UI（{Path.GetFileName(options.DataRoot.TrimEnd('\\', '/'))}）");
        Usage = new FakeUsageEndpoint();
        Http = new HttpClient(Usage, disposeHandler: false);
        Paths = new SyncPaths(Path.Combine(options.DataRoot, "sync"));
        View = new HeadlessView(ViewId);
    }

    public HeadlessHost Host { get; }

    public FakeUsageEndpoint Usage { get; }

    public HttpClient Http { get; }

    public SyncPaths Paths { get; }

    public HeadlessView View { get; }

    public CliManager Cli { get; private set; } = null!;

    public AccountManager Accounts { get; private set; } = null!;

    public AppSettings Settings { get; private set; } = null!;

    public AgentHub Hub { get; private set; } = null!;

    public SyncCoordinator Sync { get; private set; } = null!;

    /// <summary>遠端執行指令的桌面端（<see cref="DeviceRuntimeOptions.RemoteExecConfirmer"/> 有給時才有）</summary>
    public RemoteExecService? RemoteExec { get; private set; }

    public string DataRoot => options.DataRoot;

    /// <summary>引擎的 BatchAck 延遲與送出的即時更新（<see cref="DeviceRuntimeOptions.MeasureTransport"/> 或 <see cref="DeviceRuntimeOptions.EphemeralKey"/> 時才有引擎經過它）</summary>
    public TransportMeter Meter { get; } = new();

    /// <summary>拒絕執行 CLI 指令的次數（CLI 管理員的 Run；任何次數都表示有程式想跑 CLI）</summary>
    public int RefusedCli => Volatile.Read(ref refusedCli);

    /// <summary>裝置金鑰只在記憶體（<see cref="DeviceRuntimeOptions.EphemeralKey"/>）</summary>
    public bool UsesEphemeralKey => ephemeralKey is not null;

    /// <summary>這台所有會話收到的呼叫次數（WP4.9 步 15；status 的 session）</summary>
    public SessionCalls SessionCalls { get; } = new();

    /// <summary>建好、掛上同步協調者並 Start，等它讀完 device.json（已配對時引擎也建好了）</summary>
    public static async Task<DeviceRuntime> StartAsync(DeviceRuntimeOptions options, TimeSpan? startTimeout = null)
    {
        if (options.Persist && !SamePath(AppPaths.Root, options.DataRoot))
            throw new InvalidOperationException($"存檔模式的資料根必須是 AppPaths.Root（{AppPaths.Root}），不是 {options.DataRoot}");
        // 假 app-server 的情境與資料根在建任何資料夾之前檢查（資料根不在 TEMP 底下時什麼都不建）
        if (CheckFakeCodexOptions(options) is { } fakeCodexProblem)
            throw new DeviceRuntimeException(fakeCodexProblem);
        Directory.CreateDirectory(options.DataRoot);
        var runtime = new DeviceRuntime(options);
        try
        {
            runtime.PrepareFakeCodex();
            await runtime.Host.InvokeAsync(runtime.Build);
            await runtime.Sync.WhenStarted.WaitAsync(startTimeout ?? TimeSpan.FromSeconds(20));
        }
        catch
        {
            await runtime.DisposeAsync();
            throw;
        }

        return runtime;
    }

    /// <summary>帳戶 id：別名是 32 個十六進位字元就照用（app 建的帳戶 id 就是這種格式），否則取 SHA-256("fake-device:"+別名) 的前 32 字</summary>
    public static string AccountId(string alias)
    {
        if (alias.Length == 32 && alias.All(Uri.IsHexDigit))
            return alias.ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("fake-device:" + alias)))[..32];
    }

    /// <summary>
    /// 標記這個資料根已經補過每一種本機預設帳戶（AppController 啟動時對 XxxAccountAdded 是 false 的種類呼叫 EnsureSystemDefault）：
    /// 真的 app 用這個資料根時才不會建立指向這台電腦真正登入的 Antigravity、Grok 帳戶並執行真的 agy、grok。
    /// 新的代理種類加了 XxxAccountAdded 時要加在這裡（FakeDeviceSettingsTests 以反射檢查所有名稱以 AccountAdded 結尾的屬性）
    /// </summary>
    internal static void MarkLocalDefaultsAdded(AppSettings settings)
    {
        settings.AntigravityAccountAdded = true;
        settings.GrokAccountAdded = true;
    }

    private void Build()
    {
        Settings = options.Persist ? AppSettings.Load() : new AppSettings();
        // 這個資料根之後也可能給真的 app 用（--data）：不自動更新 CLI、不補任何本機預設帳戶
        Settings.AutoUpdateCli = false;
        MarkLocalDefaultsAdded(Settings);
        if (options.Persist)
            Settings.Save();

        LoadCliState();
        Cli = new CliManager(Http, _ => false, () => false, startBackgroundChecks: false, new CliManagerOptions
        {
            Locate = LocateFake,
            LocateNpm = () => null,
            Run = RefuseCli,
            FetchLatestOnStart = false,
            AppProcessIds = () => [],
            IsAppProcess = _ => false,
            ProcessImagePath = _ => null,
            AllProcessIds = () => [],
            StartClaudeInstaller = () => throw new InvalidOperationException("fake-device 不安裝 CLI"),
        });
        AgentHub? hubRef = null;
        Accounts = new AccountManager(Host.BeginInvoke, Cli, Http, id => hubRef?.RunningForAccount(id) ?? 0)
        {
            // 沒有 --fake-codex 時 fakeCodexLauncher 是 null：每種代理都找不到 CLI（照舊）
            ResolveLauncher = provider => FakeLauncherRules.Resolve(provider, fakeCodexLauncher),
            // 一律不開瀏覽器（Codex 的一般登入拿到網址時會開；假裝置只計數）
            OpenBrowser = _ => Interlocked.Increment(ref openedBrowser),
        };
        var store = new ConversationStore(Path.Combine(options.DataRoot, "conversations"));
        Hub = new AgentHub(store, Host, Accounts, Cli, Settings, provider => new ScriptedSession(provider, Host.BeginInvoke, TakePendingTurn, SessionCalls));
        LoadConversationAliases();
        hubRef = Hub;
        foreach (var (provider, version) in cliVersions)
            PublishCli(provider, version);

        var seeded = options.Persist ? LoadAccounts() : [];
        seeded.AddRange(options.Accounts.Select(NewAccount));
        Accounts.Seed(seeded, AccountStatus.Ready);
        foreach (var account in seeded)
            ApplyRuntimeIdentity(account);
        if (!options.Persist)
            Accounts.SeedListReadOnly();

        var syncOptions = new SyncCoordinatorOptions(options.DevTrust, options.ServerOverride, TryTpm: false, options.AppVersion, options.MachineName, options.WindowsUser);
        // 記憶體金鑰：配對與每個引擎各拿一份複本（引擎停止時 Dispose 它拿到的）；量傳輸層：經 TransportMeter 包一層
        var keyFactory = options.KeyFactory;
        Func<DeviceFileData, IDeviceSigner?>? signerFactory = null;
        if (ephemeralKey is { } key)
        {
            keyFactory ??= (_, _) => key.Clone();
            signerFactory = _ => key.Clone();
        }

        var engineFactory = options.EngineFactory
                            ?? (options.MeasureTransport || signerFactory is not null ? Meter.EngineFactory(signerFactory) : null);
        Sync = new SyncCoordinator(Hub, Accounts, Host, Settings, Paths, syncOptions, TimeProvider.System,
            engineFactory, pairingCommit: null, pairingHandler: null, keyFactory: keyFactory);
        Hub.AttachSync(Sync);
        if (options.RemoteExecConfirmer is { } confirmer)
        {
            RemoteExec = new RemoteExecService(Sync, confirmer, Path.Combine(options.DataRoot, "remote-exec"), RemoteExecPipeRules.PipeName(options.DataRoot),
                id => Host.InvokeAsync(() => Hub.ConversationTitle(id)), isElevated: () => false);
            Hub.AttachRemoteExec(RemoteExec.Runner);
            Sync.AttachRemoteExec(RemoteExec);
            RemoteExec.StartPipe();
        }

        Accounts.UsagePollPolicy = Sync;
        Hub.AttachView(View);
        Hub.HandleCommand(View, new JObject { ["cmd"] = "ready" });
        Sync.Start();
    }

    // ───────────────────────────── UI 執行緒 ─────────────────────────────

    public Task<T> OnUi<T>(Func<T> func) => Host.InvokeAsync(func);

    public Task OnUi(Action action) => Host.InvokeAsync(action);

    /// <summary>網頁送指令（在 UI 執行緒交給中樞）</summary>
    public Task CommandAsync(string cmd, JObject? args = null) => Host.InvokeAsync(() =>
    {
        var command = args is null ? new JObject() : (JObject)args.DeepClone();
        command["cmd"] = cmd;
        Hub.HandleCommand(View, command);
    });

    public Task<SyncStatusSnapshot?> StatusAsync() => OnUi(() => Sync.Status);

    public Task<bool> IsPairedAsync() => OnUi(() => Sync.IsPaired);

    /// <summary>目前的正式引擎（未配對為 null）</summary>
    public Task<SyncEngine?> EngineAsync() => OnUi(() => Sync.Engine as SyncEngine);

    /// <summary>每 100 毫秒在 UI 執行緒上看一次，直到條件成立；逾時回 false</summary>
    public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await OnUi(condition))
                return true;
            if (DateTime.UtcNow > deadline)
                return false;
            await Task.Delay(100);
        }
    }

    /// <summary>
    /// 等 outbox 清空：引擎在 Live、沒有在途的批次、每一列都已確認或被拒（被拒的另外計數），連續 0.5 秒都是這樣。
    /// 回傳（是否清空、這時的列數、被拒的列數）
    /// </summary>
    public async Task<(bool Drained, int Rows, int Rejected)> WaitDrainedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        DateTime? quietSince = null;
        while (true)
        {
            if (await EngineAsync() is not { } engine)
                return (false, 0, 0);
            var (state, inFlight, rows, open, rejected) = await engine.InvokeAsync(v => (
                v.State,
                v.InFlight,
                v.Outbox.Count,
                v.Outbox.Count(e => e.State is not (OutboxState.Acked or OutboxState.Rejected)),
                v.Outbox.Count(e => e.State == OutboxState.Rejected)));
            var quiet = state == SyncEngineState.Live && inFlight == 0 && open == 0;
            if (quiet)
            {
                quietSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - quietSince >= TimeSpan.FromMilliseconds(500))
                    return (true, rows, rejected);
            }
            else
            {
                quietSince = null;
            }

            if (DateTime.UtcNow > deadline)
                return (false, rows, rejected);
            await Task.Delay(100);
        }
    }

    // ───────────────────────────── 帳戶 ─────────────────────────────

    /// <summary>加一個隔離帳戶（劇本的 addAccount）：已經有同 id 的就只更新 email、orgId、方案</summary>
    public Task<AgentAccount> AddAccountAsync(FakeAccountSpec spec) => OnUi(() =>
    {
        var id = AccountId(spec.Alias);
        if (Accounts.Find(id) is { } existing)
        {
            existing.Email = spec.Email ?? existing.Email;
            existing.Plan = spec.Plan ?? existing.Plan;
            if (existing.Provider == AgentProvider.Claude)
                existing.LastIdentity = ClaudeIdentity(existing, spec.OrgId ?? existing.LastIdentity?.OrgId);
            ApplyRuntimeIdentity(existing);
            SaveAccounts();
            Hub.RefreshSyncDecorations();
            return existing;
        }

        var account = NewAccount(spec);
        Accounts.Seed([account], AccountStatus.Ready);
        ApplyRuntimeIdentity(account);
        SaveAccounts();
        // Seed 不通知：讓中樞重送帳戶卡（鏡像因此看到新的帳戶）
        Hub.RefreshSyncDecorations();
        return account;
    });

    public Task<AgentAccount?> FindAccountAsync(string alias) => OnUi(() => Accounts.Find(AccountId(alias)));

    /// <summary>
    /// 模擬一次「到了本機的輪詢時間」：照用量查詢閘門判斷要不要查（S12），要查才查並計數。
    /// Claude 走真的 RefreshAsync（閘門、api 路徑、假端點）；Codex 沒有假的 app-server，閘門放行時在端點計一次、直接放入 api 快照並請中樞重送帳戶卡
    /// （之後的鏡像→引擎上傳路徑與真的相同）。回傳這次有沒有查
    /// </summary>
    public async Task<bool> PollUsageAsync(string alias, IReadOnlyDictionary<string, double> windows)
    {
        var id = AccountId(alias);
        var account = await OnUi(() => Accounts.Find(id)) ?? throw new InvalidOperationException($"沒有帳戶 {alias}");
        switch (account.Provider)
        {
            case AgentProvider.Claude:
            {
                Usage.SetClaudeUsage(id, windows, DateTimeOffset.UtcNow);
                var before = Usage.Requests(id);
                await Host.InvokeAsync(() => Accounts.RefreshAsync(id, includeStatus: false));
                return Usage.Requests(id) > before;
            }
            case AgentProvider.Codex:
                return await OnUi(() =>
                {
                    if (Accounts.UsagePollPolicy?.ShouldPoll(account) == false)
                        return false;
                    Usage.CountPoll(id);
                    var now = DateTimeOffset.UtcNow;
                    var raw = FakeUsageEndpoint.CodexRateLimitsJson(windows, now);
                    var (parsed, credits) = UsageRules.ParseCodexRateLimits(raw);
                    Accounts.SeedUsage(id, new UsageSnapshot(now, UsageSyncRules.SourceApi, parsed, credits, null, null, false));
                    Hub.RefreshSyncDecorations();
                    return true;
                });
            case AgentProvider.Antigravity:
                throw new InvalidOperationException("Antigravity 沒有可查的用量");
            case AgentProvider.Grok:
                throw new InvalidOperationException("假裝置沒有模擬 Grok 的額度（_x.ai/billing）");
            default:
                throw new ArgumentOutOfRangeException(nameof(alias), account.Provider, "不認得的代理種類");
        }
    }

    /// <summary>對話中回報的額度（Claude 的 rate_limit_event、Codex 的 rateLimits/updated）：來源 session</summary>
    public async Task ReportSessionUsageAsync(string alias, IReadOnlyDictionary<string, double> windows)
    {
        var id = AccountId(alias);
        await OnUi(() =>
        {
            var account = Accounts.Find(id) ?? throw new InvalidOperationException($"沒有帳戶 {alias}");
            var now = DateTimeOffset.UtcNow;
            var raw = account.Provider switch
            {
                AgentProvider.Claude => ClaudeRateLimitEvent(windows, now),
                AgentProvider.Codex => FakeUsageEndpoint.CodexRateLimitsJson(windows, now),
                AgentProvider.Antigravity => throw new InvalidOperationException("Antigravity 的對話不回報額度"),
                AgentProvider.Grok => throw new InvalidOperationException("假裝置沒有模擬 Grok 對話中的額度"),
                _ => throw new ArgumentOutOfRangeException(nameof(alias), account.Provider, "不認得的代理種類"),
            };
            Accounts.ReportSessionRateLimit(id, account.Provider, raw);
        });
    }

    // ───────────────────────────── CLI 狀態 ─────────────────────────────

    /// <summary>假的 CLI 安裝（cli.state 的內容）：version＝null 表示沒裝。不會有任何程序被執行</summary>
    public Task SetCliAsync(AgentProvider provider, string? version) => OnUi(() =>
    {
        PublishCli(provider, version);
        cliVersions[provider] = version;
        SaveCliState();
    });

    private void PublishCli(AgentProvider provider, string? version)
    {
        var exe = Path.Combine(options.DataRoot, "fake-cli", provider.ToString().ToLowerInvariant() + ".exe");
        var kind = FakeInstallKind(provider);
        lock (installGate)
        {
            fakeInstalls[provider] = version is null
                ? null
                : new CliInstallation(provider, new AgentLauncher(exe, []), kind, exe) { NativeExecutable = exe };
        }

        // 重新找安裝（LocateFake），再宣告已穩定的版本：中樞的 cli 訊息因此帶 installed 與 version
        _ = Cli.GetLauncher(provider);
        if (version is not null)
        {
            var identity = new CliBinaryIdentity(provider, kind, exe, version, 0, 0, 0, DateTime.UtcNow);
            Cli.PublishVersionChange(new CliVersionChange(provider, Cli.GetIdentity(provider), identity, CliChangeOrigin.External, Stable: true, InstallBroken: false, Detail: null));
        }
    }

    /// <summary>假安裝的種類（照 app 認得的真實安裝：Claude 原生、Codex npm、agy 與 grok 是官方安裝程式＝Other）</summary>
    private static CliInstallKind FakeInstallKind(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => CliInstallKind.Native,
        AgentProvider.Codex => CliInstallKind.Npm,
        AgentProvider.Antigravity or AgentProvider.Grok => CliInstallKind.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "不認得的代理種類"),
    };

    private CliInstallation? LocateFake(AgentProvider provider)
    {
        lock (installGate)
            return fakeInstalls.GetValueOrDefault(provider);
    }

    private Task<CliRunResult> RefuseCli(AgentLauncher launcher, IReadOnlyList<string> arguments, Action<IDictionary<string, string?>>? environment,
        TimeSpan timeout, bool killWithApp, Action<int>? started, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref refusedCli);
        throw new InvalidOperationException("fake-device 不執行任何 CLI 指令");
    }

    private void LoadCliState()
    {
        if (!options.Persist)
            return;
        var state = JsonFile.Read<Dictionary<string, string?>>(Path.Combine(options.DataRoot, CliStateFile));
        foreach (var (name, version) in state ?? [])
        {
            // 四種都讀回（2026-10-22 起都同步；Grok、Antigravity 送不送由引擎依 prov1 決定）
            if (Enum.TryParse<AgentProvider>(name, out var provider) && AgentProviders.All.Contains(provider))
                cliVersions[provider] = version;
        }
    }

    private void SaveCliState()
    {
        if (options.Persist)
            JsonFile.Write(Path.Combine(options.DataRoot, CliStateFile), cliVersions.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value));
    }

    // ───────────────────────────── 帳戶的建立與存檔 ─────────────────────────────

    private AgentAccount NewAccount(FakeAccountSpec spec)
    {
        var id = AccountId(spec.Alias);
        if (spec.Provider == AgentProvider.Antigravity)
            return NewAntigravityAccount(spec, id);
        if (!AccountRules.SupportsIsolatedAccounts(spec.Provider))
            throw new InvalidOperationException($"{spec.Provider} 沒有隔離帳戶（只有本機預設帳戶）：假裝置不建");
        var folder = spec.Provider switch
        {
            AgentProvider.Claude => "claude",
            AgentProvider.Codex => "codex",
            // Grok 自 2026-10-22 也同步（設計 2026-10-22-grok-antigravity-sync；GROK_HOME＝accounts\{id}\grok）
            AgentProvider.Grok => "grok",
            _ => throw new ArgumentOutOfRangeException(nameof(spec), spec.Provider, "沒有隔離帳戶的代理種類"),
        };
        var account = new AgentAccount
        {
            Id = id,
            Provider = spec.Provider,
            Label = string.IsNullOrWhiteSpace(spec.Label) ? $"假 {spec.Provider} 帳戶" : spec.Label.Trim(),
            Color = AccountRules.NormalizeColor(spec.Color) ?? AccountRules.SystemDefaultAppearance(spec.Provider).Color,
            ConfigDirectory = Path.Combine(options.DataRoot, "accounts", id, folder),
            Email = spec.Email,
            Plan = spec.Plan,
        };
        Directory.CreateDirectory(account.ConfigDirectory);
        if (spec.Provider == AgentProvider.Claude)
        {
            WriteFakeCredentials(account);
            account.LastIdentity = ClaudeIdentity(account, spec.OrgId);
        }

        return account;
    }

    /// <summary>
    /// Antigravity 的假帳戶：和 app 的一樣是本機預設帳戶的形狀（ConfigDirectory=null；agy 一台只有一個登入，沒有隔離帳戶），id 照別名算（不是 app 固定的
    /// local-antigravity：載入時以此分辨，<see cref="IsFakeAntigravity"/>）。信箱就是 app 從 agy 記錄讀到的身分（身分鍵 antigravity:{信箱}）。假裝置不執行任何 CLI、
    /// ResolveLauncher 對它一律 null（FakeLauncherRules），不碰這台電腦的 agy；但這個資料根之後不要給真的 exe 用（真的 exe 會對它跑這台電腦的 agy 查登入狀態，README）
    /// </summary>
    private static AgentAccount NewAntigravityAccount(FakeAccountSpec spec, string id) => new()
    {
        Id = id,
        Provider = AgentProvider.Antigravity,
        Label = string.IsNullOrWhiteSpace(spec.Label) ? "假 Antigravity 帳戶" : spec.Label.Trim(),
        Color = AccountRules.NormalizeColor(spec.Color) ?? AccountRules.SystemDefaultAppearance(AgentProvider.Antigravity).Color,
        ConfigDirectory = null,
        Email = spec.Email,
        Plan = spec.Plan,
    };

    /// <summary>假的 .credentials.json：假權杖（<see cref="FakeUsageEndpoint.TokenFor"/>）、一年後到期，不是任何真的憑證</summary>
    private static void WriteFakeCredentials(AgentAccount account)
    {
        var oauth = new JObject
        {
            ["accessToken"] = FakeUsageEndpoint.TokenFor(account.Id),
            ["refreshToken"] = "fake-device-refresh",
            ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(365).ToUnixTimeMilliseconds(),
            ["subscriptionType"] = "max",
        };
        File.WriteAllText(AccountRules.GetClaudeCredentialFile(account), new JObject { ["claudeAiOauth"] = oauth }.ToString());
    }

    /// <summary>身分快取（和 claude auth status 成功後存的同一種）：真的 app 用這個資料根啟動時，憑證檔沒變就不跑 auth status</summary>
    private static AccountIdentityCache? ClaudeIdentity(AgentAccount account, string? orgId) =>
        AccountRules.BuildIdentityCache(AccountStatus.Ready, account.Email, account.Plan,
            AccountManager.ReadCredentialInfo(AccountRules.GetClaudeCredentialFile(account)), DateTime.Now, orgId);

    /// <summary>這次執行的身分（orgId、憑證到期、檢查時間）：同步的 link.upsert 取這些</summary>
    private void ApplyRuntimeIdentity(AgentAccount account)
    {
        var state = Accounts.GetState(account.Id);
        state.OrgId = account.LastIdentity?.OrgId;
        state.AccessExpiresAt = account.LastIdentity?.ExpiresAt;
        state.CheckedAt = DateTime.Now;
    }

    /// <summary>
    /// accounts.json 裡設定資料夾在這個資料根的隔離帳戶（其他的一律不放：系統預設帳戶會碰使用者真的 ~/.claude、~/.codex），加上假裝置建的 Antigravity 帳戶
    /// （<see cref="IsFakeAntigravity"/>）
    /// </summary>
    private List<AgentAccount> LoadAccounts()
    {
        var (status, saved, _) = JsonFile.TryRead<List<AgentAccount>>(AppPaths.AccountsFile);
        if (status != JsonReadStatus.Ok || saved is null)
            return [];
        var root = Path.GetFullPath(Path.Combine(options.DataRoot, "accounts")).TrimEnd('\\') + "\\";
        var result = new List<AgentAccount>();
        foreach (var account in saved.OrderBy(a => a.Order))
        {
            if (IsFakeAntigravity(account))
            {
                result.Add(account);
                continue;
            }

            var under = account.ConfigDirectory is { } dir && Path.GetFullPath(dir).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            if (account.IsSystemDefault || !under || !AccountRules.SupportsIsolatedAccounts(account.Provider))
            {
                Log.Warn($"fake-device：略過不是這個資料根的隔離帳戶「{account.Label}」");
                continue;
            }

            result.Add(account);
        }

        return result;
    }

    /// <summary>
    /// 假裝置建的 Antigravity 帳戶（<see cref="NewAntigravityAccount"/>）：本機預設帳戶的形狀、id 是 32 碼十六進位（別名算的；app 建的是固定的 local-antigravity）。
    /// 從真實資料根複製來的 Antigravity 帳戶（固定 id）不載入，同其他系統預設帳戶
    /// </summary>
    internal static bool IsFakeAntigravity(AgentAccount account)
        => account.Provider == AgentProvider.Antigravity && account.IsSystemDefault
           && account.Id.Length == 32 && account.Id.All(Uri.IsHexDigit)
           && account.Id != AccountRules.SystemDefaultId(AgentProvider.Antigravity);

    /// <summary>照 AccountManager.Save 的格式寫 accounts.json（Seed 不存檔；之後的改名、重排、套用由 AccountManager 自己存）</summary>
    private void SaveAccounts()
    {
        if (!options.Persist)
            return;
        var list = Accounts.Accounts.ToList();
        for (var i = 0; i < list.Count; i++)
            list[i].Order = i;
        JsonFile.Write(AppPaths.AccountsFile, list);
    }

    /// <summary>Claude 對話中的 rate_limit_event（UsageRules.ParseClaudeRateLimitEvent 讀的 unifiedWindows；utilization 是 0～1）</summary>
    private static JObject ClaudeRateLimitEvent(IReadOnlyDictionary<string, double> windows, DateTimeOffset now)
    {
        var unified = new JObject();
        foreach (var (key, percent) in windows)
        {
            var (name, resets) = key switch
            {
                UsageRules.SessionKey => ("five_hour", now.AddHours(5)),
                UsageRules.WeeklyKey => ("seven_day", now.AddDays(7)),
                _ => throw new ArgumentException($"對話中的額度只有 session、weekly：{key}", nameof(windows)),
            };
            unified[name] = new JObject { ["utilization"] = percent / 100, ["resetsAt"] = resets.ToUnixTimeSeconds() };
        }

        return new JObject { ["status"] = "allowed", ["unifiedWindows"] = unified };
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // ───────────────────────────── 結束 ─────────────────────────────

    /// <summary>正常停止：停同步引擎（關連線與 sync.db）、收掉中樞與帳戶管理員</summary>
    public async Task StopAsync()
    {
        if (stopped)
            return;
        stopped = true;
        RemoteExec?.Dispose();
        try
        {
            if (Sync is not null)
                await Host.InvokeAsync(() => Sync.StopAsync(TimeSpan.FromSeconds(5))).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log.Warn($"fake-device：停止同步失敗：{ex.Message}");
        }

        try
        {
            await Host.InvokeAsync(() =>
            {
                Sync?.Dispose();
                Hub?.Dispose();
                Accounts?.Dispose();
                Cli?.Dispose();
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"fake-device：結束時釋放失敗：{ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Host.Dispose();
        Http.Dispose();
        Usage.Dispose();
        ephemeralKey?.Dispose();
    }
}
