using Myriagent.Accounts;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>協調者的環境設定（環境變數只在這裡讀）</summary>
/// <param name="DevTrustEnv">MYRIAGENT_SYNC_DEV_TRUST=1：連迴路位址的伺服器時接受驗不過的憑證（開發用）</param>
/// <param name="ServerOverride">MYRIAGENT_SYNC_SERVER：配對與連線都改連這台（開發模式，sync.detail 會加註）</param>
/// <param name="TryTpm">配對時先試 TPM（MYRIAGENT_SYNC_NO_TPM=1 時 false）</param>
internal sealed record SyncCoordinatorOptions(bool DevTrustEnv, Uri? ServerOverride, bool TryTpm, string AppVersion, string? MachineName, string? WindowsUser)
{
    public static SyncCoordinatorOptions FromEnvironment(string appVersion) =>
        FromVariables(Environment.GetEnvironmentVariable, appVersion, Environment.MachineName, Environment.UserName);

    internal static SyncCoordinatorOptions FromVariables(Func<string, string?> env, string appVersion, string? machineName, string? windowsUser)
    {
        var overrideText = env("MYRIAGENT_SYNC_SERVER");
        var serverOverride = DevTrustRules.ValidateServerUrl(overrideText);
        if (!string.IsNullOrWhiteSpace(overrideText) && serverOverride is null)
            Log.Warn("MYRIAGENT_SYNC_SERVER 不是合格的 https 網址：忽略");
        return new SyncCoordinatorOptions(env("MYRIAGENT_SYNC_DEV_TRUST") == "1", serverOverride, env("MYRIAGENT_SYNC_NO_TPM") != "1", appVersion, machineName, windowsUser);
    }
}

/// <summary>
/// 宿主端的同步協調者（UI 執行緒；計畫第 12 步）：web-protocol 的 sync* 指令與 deviceRename、sync／devices 訊息、AccountView 的〔同步〕欄位，
/// 引擎的宿主（<see cref="ISyncHostPort"/>）與 UI 套用端（<see cref="ISyncUiApplier"/>）、用量查詢閘門。
/// <list type="bullet">
/// <item>建構子不做 I/O；<see cref="Start"/> 只有第一次有效：背景執行 PairingRecovery 與 DeviceFile.Load，回到 UI 執行緒後已配對才建引擎、橋與鏡像。</item>
/// <item>未配對時只用 HttpClient（配對）、CNG（裝置金鑰）與檔案（S6）：引擎只經 <see cref="SyncEngineLoader"/> 的載入邊界建立。</item>
/// <item>每個引擎拿自己那一代的宿主（<see cref="EnginePort"/>）：解除配對後舊引擎晚到的狀態、用量快照與套用一律丟掉。</item>
/// <item>伺服器的值經 AccountManager／AgentHub 的套用方法寫回，不經 HandleCommand（防回音）。</item>
/// </list>
/// </summary>
internal sealed partial class SyncCoordinator : ISyncBridge, IUsagePollPolicy, ISyncUiApplier, ISyncHostPort, IDisposable
{
    private readonly AgentHub hub;
    private readonly AccountManager accounts;
    private readonly IAgentHost host;
    private readonly AppSettings settings;
    private readonly SyncPaths paths;
    private readonly SyncCoordinatorOptions options;
    private readonly TimeProvider time;
    private readonly Func<SyncEngineCreateArgs, ISyncEngine> engineFactory;
    private readonly Func<SyncPaths, PairTokenResponse, DeviceFileData, PairingCommitResult> pairingCommit;
    private readonly HttpMessageHandler? pairingHandler;
    private readonly Func<string, bool, IDeviceSigner>? keyFactory;
    private readonly UsagePollGate gate;
    private readonly object gateLock = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 引擎的世代：解除配對時加一，舊引擎的回呼一律丟掉（Volatile；改的時候在 gateLock 裡）
    private int generation;
    private long uiAliveAt;

    // ── 以下只在 UI 執行緒 ──
    private bool startRequested;
    private bool loaded;
    // 讀完 device.json 之前送過 init（syncStarting:true）：讀完確定沒有配對時要送一則 sync:null 結束網頁的「啟動中」（F4）
    private bool startingAnnounced;
    private bool deviceFileUnreadable;
    private bool disposed;
    private bool unpairing;
    private ISyncEngine? engine;
    private DeviceFileData? device;
    private HubBridge? bridge;
    private SyncMirrorView? mirror;
    private SyncStatusSnapshot? status;
    private SyncCatalogView? catalogView;

    /// <summary>
    /// remoteOnly 卡片（目錄有、這台沒有連結的邏輯帳戶）的共享用量，鍵是邏輯帳戶（WP3.4 D15：只放記憶體，重新啟動後等下一筆 usage.snapshot；
    /// 伺服器換實例時引擎以 reset 清掉、解除配對時清空）。UI 執行緒
    /// </summary>
    private readonly Dictionary<string, SharedUsage> remoteUsage = new(StringComparer.Ordinal);
    private PairingState? pairing;
    private JArray? pendingDevices;
    private string? lastDevicesText;
    private DateTimeOffset? lastDevicesSentAt;
    private ITimer? devicesTimer;

    /// <param name="engineFactory">預設 <see cref="SyncEngineLoader.Create"/>（載入邊界）</param>
    /// <param name="pairingCommit">預設 <see cref="SyncEngineLoader.CompletePairing"/>（載入邊界）</param>
    /// <param name="pairingHandler">配對用的 HTTP（測試的假伺服器）；null＝正式的 HttpClient</param>
    /// <param name="keyFactory">配對時建裝置金鑰（測試用記憶體金鑰）；null＝CNG</param>
    public SyncCoordinator(AgentHub hub, AccountManager accounts, IAgentHost host, AppSettings settings, SyncPaths paths,
        SyncCoordinatorOptions options, TimeProvider time,
        Func<SyncEngineCreateArgs, ISyncEngine>? engineFactory = null,
        Func<SyncPaths, PairTokenResponse, DeviceFileData, PairingCommitResult>? pairingCommit = null,
        HttpMessageHandler? pairingHandler = null,
        Func<string, bool, IDeviceSigner>? keyFactory = null)
    {
        this.hub = hub;
        this.accounts = accounts;
        this.host = host;
        this.settings = settings;
        this.paths = paths;
        this.options = options;
        this.time = time;
        this.engineFactory = engineFactory ?? SyncEngineLoader.Create;
        this.pairingCommit = pairingCommit ?? SyncEngineLoader.CompletePairing;
        this.pairingHandler = pairingHandler;
        this.keyFactory = keyFactory;
        gate = new UsagePollGate(time);
        // 讀完配對資料、引擎交來複本清單之前，網頁還原版面時先留著其他電腦的對話分頁（AgentHub.ReplicasPending）
        hub.ExpectReplicas();
    }

    /// <summary>目前的同步狀態（配對中是配對的進度；未配對為 null）。UI 執行緒</summary>
    public SyncStatusSnapshot? Status => CurrentStatus();

    /// <summary>已配對（引擎在跑）。UI 執行緒</summary>
    public bool IsPaired => engine is not null;

    /// <summary>
    /// init.syncStarting：延後啟動還沒讀完 device.json（AppController 在主視窗載入 2.5 秒後才 Start）。這段期間已配對的電腦 init.remote
    /// 也是 null，網頁不能把它當成未配對。UI 執行緒
    /// </summary>
    public bool IsStarting => !loaded && !disposed;

    /// <summary>同步狀態改變（UI 執行緒；系統匣用）</summary>
    public event Action? StatusChanged;

    /// <summary>延後啟動做完了（讀完 device.json、已配對時建好引擎；測試與探針用）</summary>
    internal Task WhenStarted => started.Task;

    /// <summary>目前的引擎（測試用）</summary>
    internal ISyncEngine? Engine => engine;

    /// <summary>目前的引擎送出成功的心跳數（實機探針用；未配對為 null）</summary>
    internal long? HeartbeatsSent => engine?.HeartbeatsSent;

    /// <summary>remoteOnly 合成卡片的開關（測試接縫；預設照 SyncFeatures.RemoteOnlyAccounts，由 WP3.5 打開）</summary>
    internal Func<bool> RemoteOnlyEnabled { get; init; } = () => SyncFeatures.RemoteOnlyAccounts;

    // ───────────────────────────── 生命週期 ─────────────────────────────

    /// <summary>延後啟動（AppController 在主視窗載入 2.5 秒後呼叫）：只有第一次有效</summary>
    public void Start()
    {
        if (startRequested || disposed)
            return;
        startRequested = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        (DeviceFileStatus Status, DeviceFileData? Data) result;
        try
        {
            result = await Task.Run(() =>
            {
                PairingRecovery.Cleanup(paths, Log.Info);
                return DeviceFile.Load(paths.DeviceFile);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("讀取配對資料失敗：這次不啟動同步", ex);
            result = (DeviceFileStatus.Unreadable, null);
        }

        host.BeginInvoke(() => OnLoaded(result.Status, result.Data));
    }

    private void OnLoaded(DeviceFileStatus fileStatus, DeviceFileData? data)
    {
        try
        {
            if (disposed)
                return;
            loaded = true;
            switch (fileStatus)
            {
                case DeviceFileStatus.Ok:
                    CreateEngine(data!);
                    RemindRemoteControlOnStartup();
                    break;
                case DeviceFileStatus.Missing:
                    break;
                case DeviceFileStatus.Corrupt:
                    Log.Warn("device.json 損毀：當成未配對（可以重新配對）");
                    break;
                case DeviceFileStatus.Unreadable:
                    deviceFileUnreadable = true;
                    Log.Warn("device.json 讀不到：這次不啟動同步、也不接受配對（請重新開啟 app）");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(fileStatus), fileStatus, "不認得的 device.json 狀態");
            }

            // 啟動中送過 init、讀完確定沒有配對（沒有 device.json、損毀、讀不到、建引擎失敗）：送一則 sync:null 讓網頁結束「啟動中」。
            // 已配對時 CreateEngine 已經送了狀態；啟動中沒送過 init 的照舊不送（未配對時不送 sync）
            if (startingAnnounced && engine is null && pairing is null)
                hub.PostSync(null);
            startingAnnounced = false;
            // 沒有引擎（未配對、損毀、讀不到、建引擎失敗）就不會有其他電腦的對話：網頁留著等的分頁收掉
            if (engine is null)
                hub.SettleReplicas();
        }
        finally
        {
            started.TrySetResult();
        }
    }

    /// <summary>正常停止（app 結束）：取消配對、停引擎（逾時就取消）</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        pairing?.Cts.Cancel();
        if (engine is { } e)
            await e.StopAsync(timeout);
    }

    /// <summary>立即結束：只取消、不等</summary>
    public void Cancel()
    {
        pairing?.Cts.Cancel();
        engine?.Cancel();
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Cancel();
        devicesTimer?.Dispose();
        devicesTimer = null;
    }

    // ───────────────────────────── 系統匣與 AppController ─────────────────────────────

    /// <summary>暫停／繼續同步（存進設定；暫停時不連線，outbox 照常累積）</summary>
    public void SetPaused(bool paused)
    {
        settings.Sync.Paused = paused;
        settings.Save();
        engine?.SetPaused(paused);
        PostLocalChange();
    }

    /// <summary>暫停／恢復遠端操作：Hello 回報的政策改變（引擎重連）</summary>
    public void SetRemotePaused(bool paused)
    {
        settings.Sync.RemotePaused = paused;
        settings.Save();
        engine?.UpdatePolicy(SyncCoordinatorRules.HelloPolicy(settings.Sync));
        PostLocalChange();
    }

    public void RetryNow() => engine?.RetryNow();

    /// <summary>網路恢復、從睡眠喚醒（任何執行緒）：回到 UI 執行緒再請引擎立刻重試</summary>
    public void OnNetworkChanged() => host.BeginInvoke(() =>
    {
        if (!disposed)
            engine?.RetryNow();
    });

    // ───────────────────────────── ISyncBridge（中樞的掛鉤） ─────────────────────────────

    public bool HandleCommand(IAgentView view, string type, JObject msg)
    {
        if (disposed || view is null || msg is null || view is SyncMirrorView || view.Id == AgentHub.SyncViewId)
            return false;
        if (HandleLocalViewCommand(view, type, msg))
            return true;
        switch (type)
        {
            case "syncPair":
                Pair(view, msg);
                return true;
            case "syncCancelPair":
                CancelPairing();
                return true;
            case "syncUnpair":
                _ = UnpairAsync();
                return true;
            case "syncPause":
                if (msg["paused"] is JValue { Type: JTokenType.Boolean, Value: bool paused })
                    SetPaused(paused);
                return true;
            case "syncRetry":
                RetryNow();
                return true;
            case "syncConfirm":
                if (msg.Value<string>("kind") == "hubIdentity" && msg["fingerprint"] is JValue { Type: JTokenType.String, Value: string fingerprint })
                    engine?.ConfirmHubIdentity(fingerprint);
                return true;
            case "syncSetPolicy":
                SetPolicy(msg);
                return true;
            case "deviceRename":
                RenameDevice(msg);
                return true;
            case "syncSetConversationPolicy":
                // 這個對話的同步政策（WP4.3 計畫 D35；檢查與存檔在中樞）
                hub.SetConversationSyncPolicy(
                    msg["conversationId"] is JValue { Type: JTokenType.String, Value: string conversationId } ? conversationId : null,
                    msg["policy"] is JValue { Type: JTokenType.String, Value: string policy } ? policy : null);
                return true;
            default:
                return false;
        }
    }

    public void OnAccountUpdated(string accountId)
    {
        if (bridge is null || accounts.Find(accountId) is not { } account)
            return;
        bridge.Edit(new SyncSignal.AccountEdited(account.Id, account.Label, account.Color));
    }

    public void OnAccountsReordered() => bridge?.Edit(new SyncSignal.AccountsReordered(accounts.Accounts.Select(a => a.Id).ToList()));

    public void OnRoamingSettingChanged(string key, JToken value) => bridge?.Edit(new SyncSignal.PrefChanged(key, value.DeepClone()));

    public JObject? InitRemote()
    {
        if (IsStarting)
            startingAnnounced = true;
        return CurrentStatus() is { } s ? SyncViewRules.InitRemote(s, catalogView, settings.Sync, RemoteExtras()) : null;
    }

    public JObject? DevicesCli()
    {
        if (engine is null)
            return null;
        return catalogView is null ? new JObject() : SyncViewRules.DevicesCli(catalogView);
    }

    public void DecorateAccounts(JArray list)
    {
        if (engine is null || catalogView is not { } view)
            return;
        foreach (var item in list.OfType<JObject>())
            SyncViewRules.Decorate(item, view);
        if (!RemoteOnlyEnabled())
            return;
        foreach (var account in RemoteOnlyAccounts(view))
            list.Add(SyncViewRules.RemoteOnlyAccount(account, remoteUsage.GetValueOrDefault(account.LogicalAccountId), view));
    }

    /// <summary>目錄有、這台沒有本機帳戶的邏輯帳戶（remoteOnly 卡），依目錄的順序</summary>
    private IEnumerable<AccountUpsertPayload> RemoteOnlyAccounts(SyncCatalogView view)
    {
        var linked = view.LocalToLogical.Values.ToHashSet(StringComparer.Ordinal);
        // 「在這台登入」宣告了、伺服器還沒連結的邏輯帳戶也不列 remoteOnly 卡：這台已經有它的本機卡片
        foreach (var claimed in accounts.Accounts.Select(a => a.LogicalAccountId).OfType<string>())
            linked.Add(claimed);
        return view.Accounts.Values
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.LogicalAccountId, StringComparer.Ordinal)
            .Where(a => !linked.Contains(a.LogicalAccountId))
            .ToList();
    }

    public CatalogAccountTemplate? ClaimCatalogAccount(string? logicalAccountId, out string failure)
    {
        var view = disposed ? null : catalogView;
        AccountUpsertPayload? entry = null;
        if (logicalAccountId is not null)
            view?.Accounts.TryGetValue(logicalAccountId, out entry);
        var provider = SyncProviderRules.FromWireName(entry?.Provider);
        var linkedHere = logicalAccountId is not null && view is not null && view.LocalToLogical.Values.Contains(logicalAccountId, StringComparer.Ordinal);
        var claimedHere = logicalAccountId is not null && accounts.Accounts.Any(a => string.Equals(a.LogicalAccountId, logicalAccountId, StringComparison.Ordinal));
        var check = AccountIdentityRules.CheckCatalogClaim(logicalAccountId, !disposed && IsPaired, provider, linkedHere, claimedHere);
        if (check != CatalogClaimCheck.Ok || entry is null || provider is not { } kind)
        {
            failure = AccountIdentityRules.DescribeClaimFailure(check == CatalogClaimCheck.Ok ? CatalogClaimCheck.NotInCatalog : check);
            return null;
        }

        failure = string.Empty;
        return new CatalogAccountTemplate(entry.LogicalAccountId, kind, entry.Label, entry.Color, entry.ExpectedEmail);
    }

    public string? ExpectedEmail(string localAccountId)
    {
        if (disposed || engine is null || catalogView is not { } view || accounts.Find(localAccountId) is not { } account)
            return null;
        var logical = view.LocalToLogical.TryGetValue(localAccountId, out var mapped) ? mapped : account.LogicalAccountId;
        return logical is not null && view.Accounts.TryGetValue(logical, out var entry) && SyncProviderRules.FromWireName(entry.Provider) == account.Provider
            ? entry.ExpectedEmail
            : null;
    }

    public string? ReplicaLogicalAccount(string? ownerDeviceId, string? ownerAccountId)
        => disposed || engine is null || catalogView is not { } view ? null : SyncViewRules.LinkedLogical(view, ownerDeviceId, ownerAccountId);

    // ───────────────────────────── IUsagePollPolicy ─────────────────────────────

    /// <summary>UI 執行緒：照引擎最後發布的快照（Volatile）判斷；未配對時一律查</summary>
    public bool ShouldPoll(AgentAccount account) => gate.ShouldPoll(account);

    // ───────────────────────────── ISyncHostPort／ISyncUiApplier（目前這一代） ─────────────────────────────

    public Task RunOnUiAsync(Action action, CancellationToken ct) => RunOnUi(Volatile.Read(ref generation), action, ct);

    public void PublishStatus(SyncStatusSnapshot snapshot) => OnEngineStatus(Volatile.Read(ref generation), snapshot);

    public void PublishPollSnapshot(UsagePollSnapshot snapshot) => OnEnginePoll(Volatile.Read(ref generation), snapshot);

    public void PingUi() => host.BeginInvoke(() => Volatile.Write(ref uiAliveAt, time.GetUtcNow().ToUnixTimeMilliseconds()));

    public long UiAliveAt => Volatile.Read(ref uiAliveAt);

    /// <summary>
    /// 套用一頁目錄（UI 執行緒，經 RunOnUiAsync）：名稱與顏色、順序、漫遊偏好依序套用，存下檢視；devices 有變就節流送出，
    /// 帳戶卡與 cli 重送一次（〔同步〕欄位與 devicesCli）
    /// </summary>
    public void ApplyCatalog(CatalogApply apply)
    {
        foreach (var (localAccountId, label, color) in apply.Appearance)
            accounts.ApplyCatalogAppearance(localAccountId, label, color);
        if (apply.LocalOrder is { } order)
            accounts.ApplyCatalogOrder(order);
        foreach (var (key, value) in apply.Prefs)
            hub.ApplyRoamingSetting(key, value);
        catalogView = apply.View;
        // remoteOnly 的共享用量只留這台沒連結、目錄裡還有的邏輯帳戶：連結期間的新快照改走 ApplySharedUsage，這裡的值會停在連結之前；
        // 之後解除連結時要等下一筆，不拿那筆舊值頂替（同一個 UI 回呼裡 ApplyRemoteUsage 在後面，它的資料也是照這個檢視算的）
        var linkedHere = apply.View.LocalToLogical.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var stale in remoteUsage.Keys.Where(k => linkedHere.Contains(k) || !apply.View.Accounts.ContainsKey(k)).ToList())
            remoteUsage.Remove(stale);
        QueueDevices(SyncViewRules.DevicesView(apply.View));
        hub.RefreshSyncDecorations();
    }

    public void ApplySharedUsage(IReadOnlyList<SharedUsageApply> items) =>
        accounts.ApplySharedUsage(items.Select(i => (i.LocalAccountId, i.Snapshot, i.PointTime, i.ReplaceCurrent)).ToList());

    /// <summary>
    /// remoteOnly 卡片的共享用量（UI 執行緒，經 RunOnUiAsync）：reset 先清掉（伺服器換了實例），再以每個邏輯帳戶最新的一筆取代；
    /// 有變化而且 remoteOnly 卡片打開時重送帳戶卡
    /// </summary>
    public void ApplyRemoteUsage(IReadOnlyList<RemoteUsageApply> items, bool reset)
    {
        var changed = false;
        if (reset && remoteUsage.Count > 0)
        {
            remoteUsage.Clear();
            changed = true;
        }

        foreach (var item in items)
        {
            remoteUsage[item.LogicalAccountId] = item.Usage;
            changed = true;
        }

        if (changed && RemoteOnlyEnabled())
            hub.RefreshSyncDecorations();
    }

    /// <summary>測試用：某個邏輯帳戶目前的 remoteOnly 共享用量（UI 執行緒）</summary>
    internal SharedUsage? RemoteUsageOf(string logicalAccountId) => remoteUsage.GetValueOrDefault(logicalAccountId);

    private Task RunOnUi(int gen, Action action, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested)
        {
            done.TrySetCanceled(ct);
            return done.Task;
        }

        var registration = ct.Register(() => done.TrySetCanceled(ct));
        host.BeginInvoke(() =>
        {
            registration.Dispose();
            if (disposed || ct.IsCancellationRequested || gen != Volatile.Read(ref generation))
            {
                done.TrySetCanceled(ct.IsCancellationRequested ? ct : CancellationToken.None);
                return;
            }

            try
            {
                action();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    private void OnEngineStatus(int gen, SyncStatusSnapshot snapshot) => host.BeginInvoke(() =>
    {
        if (disposed || engine is null || gen != Volatile.Read(ref generation))
            return;
        status = snapshot;
        PostStatus();
    });

    private void OnEnginePoll(int gen, UsagePollSnapshot snapshot)
    {
        lock (gateLock)
        {
            if (gen == generation)
                gate.Publish(snapshot);
        }
    }

    /// <summary>一個引擎的宿主：只轉給同一代的協調者狀態（解除配對後晚到的一律丟掉）</summary>
    private sealed partial class EnginePort(SyncCoordinator owner, int gen) : ISyncHostPort, ISyncUiApplier
    {
        public Task RunOnUiAsync(Action action, CancellationToken ct) => owner.RunOnUi(gen, action, ct);

        public void PublishStatus(SyncStatusSnapshot status) => owner.OnEngineStatus(gen, status);

        public void PublishPollSnapshot(UsagePollSnapshot snapshot) => owner.OnEnginePoll(gen, snapshot);

        public void PingUi() => owner.PingUi();

        public long UiAliveAt => owner.UiAliveAt;

        // 在 RunOnUiAsync 裡（UI 執行緒、已確認是同一代）呼叫
        public void ApplyCatalog(CatalogApply apply) => owner.ApplyCatalog(apply);

        public void ApplySharedUsage(IReadOnlyList<SharedUsageApply> items) => owner.ApplySharedUsage(items);

        public void ApplyRemoteUsage(IReadOnlyList<RemoteUsageApply> items, bool reset) => owner.ApplyRemoteUsage(items, reset);
    }

    // ───────────────────────────── 引擎 ─────────────────────────────

    private void CreateEngine(DeviceFileData data)
    {
        var gen = Volatile.Read(ref generation);
        var port = new EnginePort(this, gen);
        var args = new SyncEngineCreateArgs(
            paths,
            data,
            port,
            port,
            SyncCoordinatorRules.HelloPolicy(settings.Sync),
            options.DevTrustEnv,
            options.ServerOverride,
            options.AppVersion,
            options.MachineName,
            options.WindowsUser);
        ISyncEngine created;
        try
        {
            created = engineFactory(args);
        }
        catch (Exception ex)
        {
            Log.Error("建立同步引擎失敗", ex);
            return;
        }

        engine = created;
        device = data;
        status = SyncCoordinatorRules.Initial(data, options.ServerOverride);
        bridge = new HubBridge(created);
        // app.state（U3，SyncCoordinator.Releases.cs）：配對之前就有的更新狀態現在交出去
        if (lastAppState is { } appState)
            bridge.OfferApp(appState);
        mirror = new SyncMirrorView(accounts, bridge);
        // 暫停要在 Start 之前排進去：引擎開好 sync.db 後直接進 Paused，不先連一次
        if (settings.Sync.Paused)
            created.SetPaused(true);
        hub.AttachSyncView(mirror);
        hub.AttachConversationSink(AttachPublisher(ConversationPublisher.TryCreate(hub, created, time)));
        // 桌面遠端操作（SyncCoordinator.Peer.cs）：引擎交握完成、協商出 peer1 才回報能送
        peerLive = null;
        hub.AttachPeerSink(this);
        created.Start();
        PostStatus();
        hub.RefreshSyncDecorations();
    }

    // ───────────────────────────── 配對 ─────────────────────────────

    private sealed class PairingState(CancellationTokenSource cts, Uri pairedServer)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public Uri PairedServer { get; } = pairedServer;

        public PairingStatus Status { get; set; } = SyncCoordinatorRules.PairingStarting;

        public string? DeviceFingerprint { get; set; }

        public bool Running { get; set; } = true;
    }

    /// <summary>配對進度（在 PairingSession 的同步內容上呼叫，正式環境是 UI 執行緒）：一律經 BeginInvoke 回到 UI 執行緒再改狀態</summary>
    private sealed class PairingProgress(SyncCoordinator owner, PairingState state) : IPairingProgress
    {
        public void Started(PairStartResponse response, string deviceFingerprint, long expiresAt) => owner.host.BeginInvoke(() =>
        {
            state.Status = new PairingStatus(response.UserCode, response.VerificationUri, response.VerificationUriComplete, expiresAt, null);
            state.DeviceFingerprint = deviceFingerprint;
            owner.OnPairingProgress(state);
        });

        public void Failed(string code, string text) => owner.host.BeginInvoke(() =>
        {
            state.Status = state.Status with { Error = text };
            state.Running = false;
            owner.OnPairingProgress(state);
        });
    }

    private void Pair(IAgentView view, JObject msg)
    {
        if (engine is not null || pairing is { Running: true } || unpairing)
            return;
        if (!loaded)
        {
            hub.SyncToast("同步元件還在啟動，請稍後再試", view);
            return;
        }

        if (deviceFileUnreadable)
        {
            hub.SyncToast("配對資料（device.json）暫時讀不到，請重新開啟 app 後再配對", view);
            return;
        }

        if (SyncCoordinatorRules.PairServer(msg["server"]) is not { } pairedServer)
        {
            hub.SyncToast("請填你的 Hub 網址（例如 https://hub.example.com）：只接受 https、不能帶路徑、查詢字串或帳號密碼", view);
            return;
        }

        var name = msg["deviceName"] is JValue { Type: JTokenType.String, Value: string text } ? text : options.MachineName ?? string.Empty;
        pairing?.Cts.Dispose();
        var state = new PairingState(new CancellationTokenSource(), pairedServer);
        pairing = state;
        var request = new PairingRequest(
            options.ServerOverride ?? pairedServer,
            pairedServer,
            name,
            options.MachineName,
            options.WindowsUser,
            options.DevTrustEnv,
            options.TryTpm,
            paths,
            options.AppVersion);
        PostStatus();
        _ = RunPairingAsync(state, request);
    }

    private async Task RunPairingAsync(PairingState state, PairingRequest request)
    {
        PairingCommitResult result;
        try
        {
            result = await PairingSession.RunAsync(request, new PairingProgress(this, state), time, pairingCommit, state.Cts.Token, keyFactory, pairingHandler)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            host.BeginInvoke(() => OnPairingCanceled(state));
            return;
        }
        catch (PairingException ex)
        {
            Log.Info($"配對失敗（{ex.Code}）");
            host.BeginInvoke(() => OnPairingEnded(state));
            return;
        }
        catch (Exception ex)
        {
            Log.Error("配對失敗", ex);
            host.BeginInvoke(() =>
            {
                state.Status = state.Status with { Error = PairingFlowRules.ErrorText(PairingCodes.CommitFailed) };
                OnPairingEnded(state);
            });
            return;
        }

        host.BeginInvoke(() => OnPaired(state, result));
    }

    private void OnPairingProgress(PairingState state)
    {
        if (pairing == state && !disposed)
            PostStatus();
    }

    /// <summary>失敗：錯誤留在 sync.pairing.error，直到 syncCancelPair 或下一次 syncPair</summary>
    private void OnPairingEnded(PairingState state)
    {
        state.Running = false;
        OnPairingProgress(state);
    }

    private void OnPairingCanceled(PairingState state)
    {
        state.Running = false;
        if (pairing != state)
            return;
        pairing = null;
        state.Cts.Dispose();
        PostStatus();
    }

    /// <summary>device.json 已寫好＝已配對（期間按了取消也一樣，提交點已經過了）：建引擎</summary>
    private void OnPaired(PairingState state, PairingCommitResult result)
    {
        state.Running = false;
        if (pairing == state)
            pairing = null;
        state.Cts.Dispose();
        if (disposed)
            return;
        Log.Info($"配對完成：{result.Device.DeviceId}");
        CreateEngine(result.Device);
    }

    private void CancelPairing()
    {
        if (pairing is not { } state)
            return;
        pairing = null;
        if (state.Running)
        {
            // PairingSession 收到取消後刪金鑰與 pending 檔（在它自己的 finally 裡）
            state.Cts.Cancel();
        }
        else
        {
            state.Cts.Dispose();
        }

        PostStatus();
    }

    // ───────────────────────────── 解除配對 ─────────────────────────────

    private async Task UnpairAsync()
    {
        if (engine is not { } stopping || unpairing)
            return;
        unpairing = true;
        var old = device!;
        lock (gateLock)
        {
            generation++;
            gate.Publish(null);
        }

        engine = null;
        device = null;
        bridge = null;
        mirror = null;
        status = null;
        catalogView = null;
        remoteUsage.Clear();
        hub.DetachSyncView();
        // 各電腦的即時狀態（SyncCoordinator.Stats.cs）：其他電腦與往返時間清空
        ClearStats();
        // 其他電腦的 token 計數（SyncCoordinator.Tokens.cs）
        ClearOtherTokens();
        ClearOtherSchedules();
        hub.AttachConversationSink(AttachPublisher(null));
        hub.AttachPeerSink(null);
        peerLive = null;
        await ExportBeforeUnpairAsync(stopping, old).ConfigureAwait(false);
        try
        {
            await stopping.StopAsync(SyncCoordinatorRules.UnpairStopTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"解除配對：停止同步引擎失敗：{ex.Message}");
        }

        try
        {
            await Task.Run(() => DeletePairing(old)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"解除配對：刪除配對資料失敗：{ex.Message}");
        }

        host.BeginInvoke(() =>
        {
            unpairing = false;
            pendingDevices = null;
            lastDevicesText = null;
            Log.Info($"已解除配對：{old.DeviceId}");
            hub.PostSync(null);
            hub.PostDevices(new JArray());
            hub.ApplyCollaborators(null);
            hub.RefreshSyncDecorations();
            StatusChanged?.Invoke();
        });
    }

    /// <summary>
    /// 刪除這台的配對資料（背景執行緒）：金鑰、pending 檔、sync.db（含 -wal／-shm），device.json 最後刪
    /// （中途當掉時 device.json 還在：下次啟動引擎回報金鑰或資料庫不見，使用者再解除一次）
    /// </summary>
    private void DeletePairing(DeviceFileData old)
    {
        try
        {
            if (DeviceKey.Delete(old.KeyName, old.KeyStorage))
                Log.Info("已刪除裝置金鑰");
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"刪不掉裝置金鑰：{ex.Message}");
        }

        foreach (var file in new[] { paths.PendingKeyFile, paths.DatabaseFile, paths.DatabaseFile + "-wal", paths.DatabaseFile + "-shm", paths.DeviceFile })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"刪不掉 {Path.GetFileName(file)}：{ex.Message}");
            }
        }
    }

    // ───────────────────────────── 其他指令 ─────────────────────────────

    private void SetPolicy(JObject msg)
    {
        if (SyncCoordinatorRules.ParsePolicy(msg, settings.Sync) is not { } policy)
        {
            Log.Info("syncSetPolicy 的參數不合格：忽略");
            return;
        }

        if (SyncCoordinatorRules.ParseNotifyRemoteActions(msg, settings.Sync) is not { } notify)
        {
            Log.Info("syncSetPolicy 的 notifyRemoteActions 不合格：忽略");
            return;
        }

        if (SyncCoordinatorRules.ParseAllowRemoteExec(msg, settings.Sync) is not { } allowExec)
        {
            Log.Info("syncSetPolicy 的 allowRemoteExec 不合格：忽略");
            return;
        }

        settings.Sync.RemoteControlLevel = policy.Level;
        settings.Sync.AllowRemoteFullAccess = policy.AllowFullAccess;
        settings.Sync.AllowRemoteExec = allowExec;
        settings.Sync.NotifyRemoteActions = notify;
        settings.Save();
        engine?.UpdatePolicy(SyncCoordinatorRules.HelloPolicy(settings.Sync));
        PostLocalChange();
    }

    private void RenameDevice(JObject msg)
    {
        if (bridge is null)
            return;
        if (msg["deviceId"] is not JValue { Type: JTokenType.String, Value: string deviceId } || !SyncIds.IsDeviceId(deviceId))
            return;
        if (msg["name"] is not JValue { Type: JTokenType.String, Value: string name } || PairingRules.CleanDeviceName(name) is not { } clean)
            return;
        bridge.Edit(new SyncSignal.DeviceRenamed(deviceId, clean));
    }

    // ───────────────────────────── 輸出 ─────────────────────────────

    private SyncStatusSnapshot? CurrentStatus()
    {
        if (pairing is { } p)
            return SyncCoordinatorRules.Pairing(p.PairedServer, options.ServerOverride, p.Status, p.DeviceFingerprint);
        return engine is null ? null : status;
    }

    private void PostStatus()
    {
        hub.PostSync(CurrentStatus() is { } s ? SyncViewRules.StatusView(s, settings.Sync, RemoteExtras()) : null);
        StatusChanged?.Invoke();
    }

    /// <summary>暫停、遠端暫停、政策改了（D14）：有狀態時立刻重送 sync（local 是新值）；未配對時不送 sync:null</summary>
    private void PostLocalChange()
    {
        if (!disposed && CurrentStatus() is not null)
            PostStatus();
    }

    /// <summary>devices：和上次送的相同就不送；距上次未滿 500 毫秒時排到滿 500 毫秒再送最新的一份</summary>
    private void QueueDevices(JArray devices)
    {
        pendingDevices = devices;
        if (devicesTimer is not null)
            return;
        if (SyncCoordinatorRules.DevicesDelay(lastDevicesSentAt, time.GetUtcNow()) is not { } delay)
        {
            SendDevices();
            return;
        }

        devicesTimer = time.CreateTimer(_ => host.BeginInvoke(OnDevicesTimer), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void OnDevicesTimer()
    {
        devicesTimer?.Dispose();
        devicesTimer = null;
        if (!disposed && engine is not null)
            SendDevices();
    }

    private void SendDevices()
    {
        if (pendingDevices is not { } devices)
            return;
        pendingDevices = null;
        var text = devices.ToString(Formatting.None);
        if (text == lastDevicesText)
            return;
        lastDevicesText = text;
        lastDevicesSentAt = time.GetUtcNow();
        hub.PostDevices(devices);
    }
}
