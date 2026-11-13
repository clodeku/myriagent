using System.Globalization;
using Myriagent.Core;
using Myriagent.Security;
using Myriagent.Usage;

namespace Myriagent.Sync;

/// <summary>引擎的計時與退避（測試注入較短的退避步階、固定的抖動）</summary>
/// <param name="Jitter">0～1 的亂數（退避的 ±20%）</param>
/// <param name="Phase5">第 5 階段的開關；null＝<see cref="SyncPhase5Switches.Default"/>（正式組建兩個都關）</param>
internal sealed record SyncEngineOptions(
    IReadOnlyList<double> BackoffSteps,
    Func<double> Jitter,
    TimeSpan HeartbeatInterval,
    TimeSpan AckTimeout,
    TimeSpan BatchInterval,
    SyncPhase5Switches? Phase5 = null)
{
    public static SyncEngineOptions Default { get; } = new(
        SyncBackoffRules.DefaultStepsSeconds,
        Random.Shared.NextDouble,
        SyncTimingRules.HeartbeatInterval,
        SyncTimingRules.AckTimeout,
        SyncTimingRules.BatchInterval);
}

/// <param name="SignerFactory">
/// 開裝置金鑰：找不到回 null → Stopped(KeyMissing)；打不開就丟例外，依 <see cref="DeviceKeyErrorRules"/> 分類（暫時性的退避重試、
/// 不存在的停止）。之前暫時打不開、或簽章時暫時性失敗而關掉的，下次 Connect 再呼叫一次。引擎停止時 Dispose 它
/// </param>
/// <param name="OpenStore">(路徑, deviceId) → 開好的 sync.db（失敗丟 <see cref="SyncStoreException"/>）；在引擎執行緒上呼叫</param>
/// <param name="ServerOverride">MYRIAGENT_SYNC_SERVER 覆寫的伺服器（開發模式）；null＝連配對時的伺服器</param>
/// <param name="BootId">每個程序一個（同一程序重連時不變）</param>
/// <param name="Conversations">
/// 對話發布的盒子（WP4.3 計畫 D3；正式的 <see cref="SyncEngineLoader.Create"/> 一律帶）。null＝這個引擎不發布對話：協調者不掛發布者、
/// 對話的登記點直接返回、Hello 不宣告 conv1／live1（WP4.1 的契約測試照原樣）
/// </param>
internal sealed record SyncEngineArgs(
    SyncPaths Paths,
    DeviceFileData Device,
    Func<DeviceFileData, IDeviceSigner?> SignerFactory,
    Func<string, string, SyncStore> OpenStore,
    ISyncTransport Transport,
    ISyncHostPort Host,
    ISyncUiApplier Applier,
    TimeProvider Time,
    SyncEngineOptions Options,
    DevicePolicy Policy,
    Uri? ServerOverride,
    bool DevTrustEnv,
    string AppVersion,
    string BootId,
    string? MachineName,
    string? WindowsUser,
    ConversationChangeBox? Conversations = null);

/// <summary>
/// 桌面的同步引擎（設計 4、sync-protocol 3～5、9、10；計畫 D10～D15）：
/// <list type="bullet">
/// <item>專用執行緒 MyriagentSync（單執行緒 SynchronizationContext）：引擎的狀態只在這條執行緒改、不加鎖；SQLite 的同步呼叫（含 fsync）也只在這裡。
/// 對外的方法只把工作排進來就返回。</item>
/// <item>連線：自己的重連狀態機（不用 SignalR 的自動重連），每次 new 一條連線（連線世代）；Hello／Prove 雙向簽章，伺服器身分照釘選驗。</item>
/// <item>outbox：先寫進 sync.db 才送；選進批次的列先標在途、寫回之後才 PublishBatch；BatchAck 依送出世代處理；在途批次最多照 Hello 的上限。</item>
/// <item>拉取：每頁化簡 → 在 UI 執行緒套用 → 套用完才提交游標與目錄快取。</item>
/// <item>計時器只有：心跳（Live）、BatchAck 逾時（有在途批次時）、送出延遲、退避、狀態尾端送出——閒置 Live 時只剩心跳。</item>
/// </list>
/// 計時一律用注入的 <see cref="TimeProvider"/>（測試撥快）；排序從不看時鐘。
/// </summary>
internal sealed partial class SyncEngine : ISyncEngine, IConversationEngine
{
    private static readonly IReadOnlyDictionary<string, long> NoCursors = new Dictionary<string, long>();

    private static readonly IReadOnlyDictionary<string, UsageAssignment> NoAssignments = new Dictionary<string, UsageAssignment>();

    private static readonly IReadOnlyDictionary<string, UsageLinkState> NoLinks = new Dictionary<string, UsageLinkState>();

    /// <summary><see cref="BackoffHistory"/> 只留最後這麼多筆（測試與探針用；長時間離線時每 16 秒一筆，不能無上限增長）</summary>
    internal const int MaxBackoffHistory = 64;

    private readonly SyncEngineArgs args;
    private readonly SyncEngineThread thread;
    private readonly SyncInbox inbox = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeProvider time;
    private readonly SyncEngineOptions options;
    private readonly Uri pairedServer;
    private readonly Uri server;
    private readonly bool devMode;
    private readonly object historyGate = new();
    private readonly List<TimeSpan> backoffHistory = [];
    private int started;
    private long heartbeatsSent;

    // ── 以下只在引擎執行緒讀寫 ──
    private bool opened;
    private bool stopping;
    private bool paused;
    private SyncStore? store;
    private DeviceState? device;
    private TrustStore? trust;
    private CatalogSync? catalog;
    private IDeviceSigner? signer;
    private DeviceKeyStreak keyStreak = DeviceKeyStreak.None;
    private string? deviceToken;
    private List<OutboxEntry> outbox = [];
    private IReadOnlyDictionary<string, long> cursors = NoCursors;
    private IReadOnlyDictionary<string, UsageAssignment> assignments = NoAssignments;
    private DevicePolicy policy;
    private SyncEngineState state = SyncEngineState.Idle;
    private SyncStopReason stopReason = SyncStopReason.None;
    private SyncFailure? lastFailure;
    private string? noticeDetail;
    private string? pullWarning;
    private long? clockSkewMs;
    private long? lastSyncAt;
    private string? candidateSpki;
    private string? confirmFingerprint;
    private SyncConnection? current;
    private int connectionGeneration;
    private int sendGeneration;
    private readonly Dictionary<string, InFlightBatch> inFlight = new(StringComparer.Ordinal);
    private SyncLimits limits = SyncLimits.Default;
    private int attempt;
    private DateTimeOffset? liveSince;
    private DateTimeOffset? notBefore;
    private DateTimeOffset? backoffDue;
    private DateTimeOffset? disconnectedSince;
    private DateTimeOffset? lastBatchAt;
    private ITimer? heartbeatTimer;
    private ITimer? ackTimer;
    private ITimer? sendTimer;
    private ITimer? backoffTimer;
    private ITimer? statusTimer;
    // 計時器的代號：取消或重排時加一，已經排進引擎執行緒的舊回呼看到代號不同就不做
    private int statusToken;
    private int sendToken;
    private int ackToken;
    private int backoffToken;
    private SyncStatusSnapshot? lastStatus;
    private DateTimeOffset lastStatusAt;
    private int busy;

    public SyncEngine(SyncEngineArgs args)
    {
        this.args = args;
        time = args.Time;
        options = args.Options;
        policy = args.Policy;
        pairedServer = new Uri(args.Device.ServerUrl);
        server = args.ServerOverride ?? pairedServer;
        devMode = args.ServerOverride is not null;
        thread = new SyncEngineThread("MyriagentSync");
    }

    /// <summary>送出的一批（以 batchId 對應 BatchAck；記下送出世代）</summary>
    private sealed record InFlightBatch(int Generation, long FirstDeviceSeq, long LastDeviceSeq);

    /// <summary>對話發布的盒子（WP4.3 計畫 D3；null＝不發布對話）。建立後不變，任何執行緒都可以讀</summary>
    public ConversationChangeBox? Conversations => args.Conversations;

    // ── 對外介面（任何執行緒；只排工作） ──

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            return;
        thread.Start();
        thread.Post(Startup);
    }

    public void Post(SyncSignal signal)
    {
        if (inbox.Post(signal))
            thread.Post(ProcessInbox);
    }

    public void SetPaused(bool value) => thread.Post(() => OnSetPaused(value));

    public void RetryNow() => thread.Post(OnRetryNow);

    public void ConfirmHubIdentity(string fingerprint) => thread.Post(() => OnConfirmHubIdentity(fingerprint));

    public void UpdatePolicy(DevicePolicy value) => thread.Post(() => OnUpdatePolicy(value));

    public async Task StopAsync(TimeSpan timeout)
    {
        if (Volatile.Read(ref started) == 0)
        {
            thread.Complete();
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.Post(() => Spawn(async () =>
        {
            try
            {
                await ShutdownAsync();
            }
            finally
            {
                done.TrySetResult();
            }
        }));
        var finished = await Task.WhenAny(done.Task, thread.Exited, Task.Delay(timeout));
        if (finished != done.Task && !thread.Exited.IsCompleted)
        {
            Log.Warn("同步引擎沒有在時限內停止：直接取消");
            Cancel();
        }

        thread.Complete();
        if (await Task.WhenAny(thread.Exited, Task.Delay(TimeSpan.FromSeconds(5))) != thread.Exited)
            Log.Warn("同步引擎的執行緒 5 秒內沒有結束");
    }

    public void Cancel()
    {
        try
        {
            lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        thread.Post(() =>
        {
            stopping = true;
            CancelTimers();
            var conn = current;
            current = null;
            conn?.Cancel();
            CloseResources();
        });
        thread.Complete();
    }

    public ValueTask DisposeAsync() => new(StopAsync(TimeSpan.FromSeconds(5)));

    public long HeartbeatsSent => Interlocked.Read(ref heartbeatsSent);

    // ── 測試用 ──

    /// <summary>引擎執行緒結束了</summary>
    internal Task Exited => thread.Exited;

    internal int ThreadId => thread.ManagedThreadId;

    /// <summary>每次退避的延遲（依序；只留最後 <see cref="MaxBackoffHistory"/> 筆）</summary>
    internal IReadOnlyList<TimeSpan> BackoffHistory
    {
        get
        {
            lock (historyGate)
                return backoffHistory.ToList();
        }
    }

    /// <summary>在引擎執行緒上讀狀態（只給測試）</summary>
    internal Task<T> InvokeAsync<T>(Func<EngineView, T> read)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.Post(() =>
        {
            try
            {
                result.TrySetResult(read(new EngineView(this)));
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });
        return result.Task;
    }

    /// <summary>收件匣空了、沒有進行中的操作、引擎執行緒沒有排著的工作（測試等待用）</summary>
    internal async Task WhenIdleAsync(int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (thread.Exited.IsCompleted)
                return;
            var probe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            // 探針本身也算在 Pending 裡
            thread.Post(() => probe.TrySetResult(busy == 0 && inbox.Count == 0 && thread.Pending <= 1));
            var finished = await Task.WhenAny(probe.Task, thread.Exited, Task.Delay(timeoutMs));
            if (finished == probe.Task && probe.Task.Result)
                return;
            if (thread.Exited.IsCompleted)
                return;
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"同步引擎 {timeoutMs} ms 內沒有閒下來（進行中 {Volatile.Read(ref busy)}）");
            await Task.Delay(1);
        }
    }

    /// <summary>引擎內部狀態的唯讀檢視（只在引擎執行緒上用）</summary>
    internal readonly struct EngineView(SyncEngine engine)
    {
        public SyncEngineState State => engine.state;

        public SyncStopReason StopReason => engine.stopReason;

        public IReadOnlyList<OutboxEntry> Outbox => engine.outbox.ToList();

        /// <summary>找第一個未完成的列與確認的尾段走過的 outbox 列數（WP4.3 步 9：不跟著保留的列數長）</summary>
        public long OutboxWork => engine.outboxWork;

        public IReadOnlyDictionary<string, long> Cursors => engine.cursors;

        public IReadOnlyDictionary<string, UsageAssignment> Assignments => engine.assignments;

        public int InFlight => engine.inFlight.Count;

        public int SendGeneration => engine.sendGeneration;

        public int ConnectionGeneration => engine.connectionGeneration;

        public SyncLimits Limits => engine.limits;

        public bool Reconciling => engine.store?.IsReconciling ?? false;

        public ServerInstance? SeenServer => engine.store?.SeenServer;

        public string? DeviceToken => engine.deviceToken;

        public long RegistryRev => engine.trust?.RegistryRev ?? 0;

        /// <summary>目前的裝置名冊（還原後名冊整份取代的長情境測試看它）</summary>
        public IReadOnlyList<DeviceRegistryEntry> Registry => engine.trust?.Registry ?? [];

        public CatalogCache? Catalog => engine.catalog?.Cache;

        public IReadOnlyDictionary<string, string>? Detached => engine.catalog?.Detached;

        public SyncStore? Store => engine.store;

        /// <summary>這一連串金鑰失敗的紀錄（簽章成功就歸零）</summary>
        public DeviceKeyStreak KeyStreak => engine.keyStreak;

        public bool HasSigner => engine.signer is not null;

        /// <summary>目前這條連線協商出的功能（沒有連線、或沒協商出任何功能時是空的）</summary>
        public IReadOnlyList<string> Features => engine.current?.Features ?? [];

        /// <summary>目前這條連線 Hello 宣告的功能（null＝沒宣告）</summary>
        public IReadOnlyList<string>? DeclaredFeatures => engine.current?.DeclaredFeatures;

        /// <summary>目前這條連線信封的 conn（Hello 回應的 nonceS；伺服器身分驗過之前、沒有連線時是 null）</summary>
        public string? EnvelopeConn => engine.current?.EnvelopeConn;

        /// <summary>追著的執行中項目數（WP4.3 步 11）</summary>
        public int RunningItems => engine.runningItems.Count;

        /// <summary>執行中項目的檢查點與即時更新的計時器開著（WP4.3 步 11：閒置時沒有）</summary>
        public bool HasRunningTimer => engine.runningTimer is not null;

        /// <summary>這條連線最後一次 WatchChanged 留下的對話（線上 id；WP4.3 步 11）</summary>
        public IReadOnlySet<string> ServerWatch => new HashSet<string>(engine.serverWatch, StringComparer.Ordinal);

        /// <summary>重整佇列（依處理順序；WP4.3 步 12）</summary>
        public IReadOnlyList<ReconcileCandidate> ReconcileQueue => ReconcileQueueRules.Order(engine.reconcileQueue.Values);

        /// <summary>正在處理的對話（線上 id；沒有是 null）</summary>
        public string? ActiveReconcile => engine.activeJob?.Wire;

        /// <summary>重整的中繼資料還沒回完</summary>
        public bool Refreshing => engine.refreshing;

        /// <summary>投影 conv.item 的次數（含權杖 DLP 的 MaskItem；步 12 看「檔案沒變就不投影」）</summary>
        public int ProjectedItems => engine.projector.ItemCalls;

        /// <summary>即時更新做了遮蔽與序列化的次數（一定放不下的不做，合併前審查 T1）</summary>
        public int LiveMasked => engine.projector.LiveMasked;

        /// <summary>太大而不送的即時更新數</summary>
        public int LiveTooLarge => engine.projector.LiveTooLarge;

        /// <summary>排著要上傳的縮圖數（WP4.7 步 9）</summary>
        public int QueuedThumbnails => engine.thumbs?.Count ?? 0;

        /// <summary>磁碟保護暫停的時間（步 12）</summary>
        public BackfillPause? BackfillPause => engine.backfillPause;

        /// <summary>磁碟保護暫停的再檢查計時器開著</summary>
        public bool HasHoldTimer => engine.holdTimer is not null;

        /// <summary>引擎端的複本（顯示中的串流等；步 14）</summary>
        public ConversationReplicas Replicas => engine.replicas;

        /// <summary>有一頁複本已排進 UI、還沒寫進 sync.db（這段期間中樞的要求延後；合併前審查 R2）</summary>
        public bool ReplicaPagePending => engine.replicaPage is { Planned: true, Settled: false };

        /// <summary>延後處理的中樞要求筆數（合併前審查 R2）</summary>
        public int DeferredReplicaRequests => engine.deferredReplicaRequests?.Count ?? 0;
    }

    // ── 啟動 ──

    private void Startup()
    {
        var d = args.Device;
        try
        {
            store = args.OpenStore(args.Paths.DatabaseFile, d.DeviceId);
        }
        catch (SyncStoreException ex)
        {
            Log.Warn($"同步資料庫打不開（{ex.Failure}）：{ex.Message}");
            opened = true;
            Stop(ex.Failure switch
            {
                SyncStoreFailure.Corrupt => SyncStopReason.StoreCorrupt,
                SyncStoreFailure.TooNew => SyncStopReason.StoreTooNew,
                SyncStoreFailure.DeviceMismatch => SyncStopReason.StoreMismatch,
                SyncStoreFailure.Missing => SyncStopReason.StoreMissing,
                _ => throw new ArgumentOutOfRangeException(nameof(ex), ex.Failure, "不認得的同步資料庫錯誤"),
            });
            return;
        }
        catch (Exception ex)
        {
            Log.Error("同步資料庫打不開", ex);
            opened = true;
            StopForStoreError(ex);
            return;
        }

        try
        {
            device = new DeviceState(args.Paths, d);
            trust = new TrustStore(device, store);
            RefreshDeskTrust();
            // 已完成的對話列直接載成骨架（不讀 payload，WP4.3 計畫 D31）；第 3 階段的列照原樣
            outbox = store.LoadOutboxSlim(ShareOutboxString).ToList();
            outboxDoneHint = 0;
            cursors = store.LoadCursors();
            catalog = new CatalogSync(d.DeviceId, store.LoadCatalog(), store.LoadDetached());
            lastSyncAt = long.TryParse(store.GetKv(SyncStoreSchema.LastSyncAt), NumberStyles.Integer, CultureInfo.InvariantCulture, out var at) ? at : null;
        }
        catch (Exception ex)
        {
            Log.Error("讀取同步資料庫失敗", ex);
            opened = true;
            CloseResources();
            StopForStoreError(ex);
            return;
        }

        // 任何寫入之前：中間跑過舊版、權杖 DLP 換版、保留中的被拒列（WP4.3 計畫 D36、D38；SyncEngine.ConversationReconcile.cs）
        OnStoreOpenedForConversations();
        opened = true;
        // 離線啟動也有裝置與目錄資料：先以快取組一次檢視交給 UI
        ApplyViewToUi();

        deviceToken = DeviceSecrets.Unprotect(d.ProtectedDeviceToken);
        if (deviceToken is null)
        {
            Stop(SyncStopReason.TokenUnreadable);
            return;
        }

        // 金鑰不存在就停止；暫時打不開（TPM 忙碌、TBS 還沒起來）照常開好佇列、收本機的事件，退避後由 Connect 再開
        var key = OpenSigner(out var keyFailure);
        if (key == KeyOpen.Stopped)
            return;

        PublishPoll();
        ProcessInbox();
        if (state == SyncEngineState.Stopped)
            return;
        // 啟動做完也算一次閒置釋放的活動：連不上時樣式從沒建過，靜置期之後照樣做那一次積極回收（WP4.9 步 6b，SyncEngine.DlpTrim.cs）
        NoteDlpActivity();
        if (paused)
            SetState(SyncEngineState.Paused);
        else if (key == KeyOpen.Unavailable)
            EnterBackoff(keyFailure);
        else
            Connect();
    }

    // ── 裝置金鑰 ──

    /// <summary><see cref="OpenSigner"/> 的結果</summary>
    private enum KeyOpen
    {
        Ready,

        /// <summary>暫時打不開：呼叫端以 out 的失敗原因退避</summary>
        Unavailable,

        /// <summary>已經 Stop(KeyMissing)</summary>
        Stopped,
    }

    /// <summary>
    /// 開裝置金鑰（已開就不做）。回 null 或公鑰與 device.json 不同 → Stop(KeyMissing)；丟例外 → <see cref="OnKeyError"/> 分類
    /// </summary>
    private KeyOpen OpenSigner(out SyncFailure? failure)
    {
        failure = null;
        if (signer is not null)
            return KeyOpen.Ready;
        var d = device?.Current ?? args.Device;
        IDeviceSigner? opened;
        try
        {
            opened = args.SignerFactory(d);
        }
        catch (Exception ex)
        {
            return OnKeyError("開不了裝置金鑰", ex, out failure);
        }

        if (opened is null || opened.PublicSpki != d.DevicePubKey)
        {
            Log.Warn(opened is null ? "找不到裝置金鑰" : "裝置金鑰的公鑰與 device.json 記的不同");
            opened?.Dispose();
            Stop(SyncStopReason.KeyMissing);
            return KeyOpen.Stopped;
        }

        signer = opened;
        return KeyOpen.Ready;
    }

    /// <summary>
    /// 開金鑰或簽章失敗（<see cref="DeviceKeyErrorRules"/>）：不存在、或不認得的錯誤持續到門檻 → Stop(KeyMissing) 回 Stopped；
    /// 其他回 Unavailable 與要顯示的原因（呼叫端退避）
    /// </summary>
    private KeyOpen OnKeyError(string what, Exception error, out SyncFailure? failure)
    {
        var kind = DeviceKeyErrorRules.Classify(error);
        var (streak, action) = DeviceKeyErrorRules.OnFailure(keyStreak, kind, time.GetUtcNow());
        keyStreak = streak;
        var code = DeviceKeyErrorRules.Hex(error.HResult);
        if (action == DeviceKeyAction.Stop)
        {
            failure = null;
            Log.Warn($"{what}（{code}，{kind}，不認得的錯誤連續 {streak.Unknown} 次）：停止同步：{error.Message}");
            // 不認得的錯誤停下來的：狀態說明照實際的錯誤，不說「找不到」
            if (kind != DeviceKeyErrorKind.Missing)
                lastFailure = DeviceKeyErrorRules.Lost(error.HResult);
            Stop(SyncStopReason.KeyMissing);
            return KeyOpen.Stopped;
        }

        failure = DeviceKeyErrorRules.Unavailable(error.HResult);
        var message = $"{what}（{code}，{kind}，這一連串第 {streak.Failures} 次）：稍後重試：{error.Message}";
        // 長時間的 TPM 鎖定每 16 秒一次：只有第一次記成警告
        if (streak.Failures == 1)
            Log.Warn(message);
        else
            Log.Info(message);
        return KeyOpen.Unavailable;
    }

    // ── 控制 ──

    private void OnSetPaused(bool value)
    {
        if (paused == value)
            return;
        paused = value;
        if (!opened || store is null || stopping)
            return;
        if (value)
        {
            if (state is SyncEngineState.Stopped or SyncEngineState.NeedsConfirmation)
                return;
            if (current is { } conn)
                Drop(conn);
            CancelBackoff();
            SetState(SyncEngineState.Paused);
        }
        else if (state == SyncEngineState.Paused)
        {
            attempt = 0;
            Connect();
        }
    }

    private void OnRetryNow()
    {
        if (state != SyncEngineState.Backoff || stopping)
            return;
        var now = time.GetUtcNow();
        if (notBefore is { } at && at > now)
        {
            // ServerNotice{shutdown} 指定的時間之前不重連：改排在那個時間
            ScheduleBackoff(at - now);
            UpdateStatus();
            return;
        }

        Connect();
    }

    private void OnConfirmHubIdentity(string fingerprint)
    {
        if (state != SyncEngineState.NeedsConfirmation || candidateSpki is null || confirmFingerprint is null || trust is null)
            return;
        if (NormalizeFingerprint(fingerprint) != NormalizeFingerprint(confirmFingerprint))
        {
            Log.Warn("確認的伺服器指紋與候選的不同：忽略");
            return;
        }

        if (!trust.TryRepin(candidateSpki))
        {
            Log.Warn("改釘新的伺服器身分失敗（device.json 存檔失敗）：維持待確認");
            return;
        }

        RefreshDeskTrust();

        Log.Info($"使用者在本機確認了新的伺服器身分：{confirmFingerprint}");
        candidateSpki = null;
        confirmFingerprint = null;
        attempt = 0;
        state = SyncEngineState.Idle;
        Connect();
    }

    private void OnUpdatePolicy(DevicePolicy value)
    {
        policy = value;
        if (!opened || store is null || stopping)
            return;
        // 新的 Hello 才帶得到：連著的就重連；退避中的下次自然帶上
        if (current is { } conn)
        {
            Drop(conn);
            Connect();
        }
    }

    private static string NormalizeFingerprint(string? text) =>
        new((text ?? string.Empty).Where(c => c is not ('-' or ' ')).Select(char.ToUpperInvariant).ToArray());

    // ── 停止 ──

    private async Task ShutdownAsync()
    {
        if (stopping)
            return;
        stopping = true;
        CancelTimers();
        var conn = current;
        current = null;
        if (conn is not null)
        {
            var session = conn.Session;
            conn.Cancel();
            if (session is not null)
                await DisposeQuietlyAsync(session);
        }

        CloseResources();
    }

    private void CloseResources()
    {
        CloseMedia();
        try
        {
            store?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"關閉同步資料庫失敗：{ex.Message}");
        }

        store = null;
        signer?.Dispose();
        signer = null;
    }

    private static async Task DisposeQuietlyAsync(ISyncSession session)
    {
        try
        {
            await session.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Info($"關閉同步連線時發生錯誤（忽略）：{ex.Message}");
        }
    }

    /// <summary>
    /// 啟動時打不開或讀不了 sync.db：暫時性的 SQLite 錯誤（<see cref="SyncStore.TransientSqliteErrorCode"/>）不算損毀——停下來但不提示解除配對
    /// （解除配對會刪掉 sync.db 與還沒被確認的 outbox），重新開啟 app 再試；其他的照舊算損毀
    /// </summary>
    private void StopForStoreError(Exception ex)
    {
        if (SyncStore.TransientSqliteErrorCode(ex) is { } code)
        {
            lastFailure = new SyncFailure(SyncFailureKind.Transient, "sqlite-" + code.ToString(CultureInfo.InvariantCulture),
                $"本機同步資料庫暫時打不開（SQLite {code}：檔案被占用、唯讀或磁碟已滿），同步已停止；排除後重新開啟 app 再試，不需要重新配對");
            Stop(SyncStopReason.None);
            return;
        }

        Stop(SyncStopReason.StoreCorrupt);
    }

    private void Stop(SyncStopReason reason)
    {
        if (current is { } conn)
            Drop(conn);
        CancelBackoff();
        stopReason = reason;
        Log.Warn($"同步已停止：{reason}");
        SetState(SyncEngineState.Stopped);
    }

    // ── 狀態 ──

    private void SetState(SyncEngineState next)
    {
        state = next;
        PublishPoll();
        UpdateStatus();
    }

    private void PublishPoll()
    {
        try
        {
            args.Host.PublishPollSnapshot(new UsagePollSnapshot(true, state, disconnectedSince, catalog?.LinkStates() ?? NoLinks, assignments));
        }
        catch (Exception ex)
        {
            Log.Error("發布用量查詢快照失敗", ex);
        }
    }

    private SyncStatusSnapshot BuildStatus()
    {
        var now = time.GetUtcNow();
        TimeSpan? retryIn = state == SyncEngineState.Backoff && backoffDue is { } due ? due - now : null;
        var detail = SyncStatusRules.Detail(state, stopReason, lastFailure, retryIn, devMode);
        var parts = new[] { detail, noticeDetail, pullWarning }.Where(p => !string.IsNullOrEmpty(p)).ToList();
        var d = device?.Current ?? args.Device;
        return new SyncStatusSnapshot(
            state,
            state == SyncEngineState.Stopped ? stopReason : SyncStopReason.None,
            parts.Count == 0 ? null : string.Join("；", parts),
            server.GetLeftPart(UriPartial.Authority),
            CountNotDone(),
            lastSyncAt,
            clockSkewMs,
            d.DeviceId,
            d.DeviceFingerprint,
            d.HubFingerprint,
            null,
            state == SyncEngineState.NeedsConfirmation ? confirmFingerprint : null,
            devMode);
    }

    /// <summary>狀態改了：依 <see cref="SyncStatusRules.Decide"/> 立刻送或排尾端送出（只有待送筆數變時 500 ms 內合併）</summary>
    private void UpdateStatus()
    {
        var next = BuildStatus();
        var now = time.GetUtcNow();
        switch (SyncStatusRules.Decide(lastStatus, next, lastStatusAt, now))
        {
            case StatusPublish.Now:
                CancelTimer(ref statusTimer);
                statusToken++;
                SendStatus(next, now);
                break;
            case StatusPublish.At at:
                if (statusTimer is null)
                {
                    var token = ++statusToken;
                    statusTimer = CreateTimer(at.Time - now, () =>
                    {
                        if (token != statusToken)
                            return;
                        CancelTimer(ref statusTimer);
                        var latest = BuildStatus();
                        if (latest != lastStatus)
                            SendStatus(latest, time.GetUtcNow());
                    });
                }

                break;
            case StatusPublish.Skip:
                CancelTimer(ref statusTimer);
                statusToken++;
                break;
            default:
                throw new InvalidOperationException("不認得的狀態送出判斷");
        }
    }

    private void SendStatus(SyncStatusSnapshot status, DateTimeOffset now)
    {
        lastStatus = status;
        lastStatusAt = now;
        try
        {
            args.Host.PublishStatus(status);
        }
        catch (Exception ex)
        {
            Log.Error("發布同步狀態失敗", ex);
        }
    }

    // ── 計時器（回呼一律排回引擎執行緒） ──

    private ITimer CreateTimer(TimeSpan due, Action action) =>
        time.CreateTimer(_ => thread.Post(action), null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);

    private static void CancelTimer(ref ITimer? timer)
    {
        timer?.Dispose();
        timer = null;
    }

    private void CancelTimers()
    {
        CancelTimer(ref heartbeatTimer);
        CancelTimer(ref statsTimer);
        CancelTimer(ref ackTimer);
        CancelTimer(ref sendTimer);
        CancelTimer(ref backoffTimer);
        CancelTimer(ref statusTimer);
        statusToken++;
        sendToken++;
        ackToken++;
        backoffToken++;
        // WP4.3 的計時器：停止之後不留著（回呼抓著整個舊引擎，補送 created 的最長要 1 小時才觸發；合併前審查 T4）
        CancelTimer(ref runningTimer);
        runningTimerDue = null;
        CancelTimer(ref createdRetryTimer);
        CancelTimer(ref noticeTimer);
        CancelTimer(ref holdTimer);
        // WP4.4 的時鐘計時器（同上，最長 6 小時才觸發；合併前審查 T1）
        CancelCommandTimers();
        // WP4.9 步 6b 的閒置釋放與再壓縮（同上，最長 1 小時才觸發）
        CancelDlpTrimTimer();
    }

    // ── 背景工作（引擎執行緒上的 async；進行中的數量給 WhenIdleAsync） ──

    private void Spawn(Func<Task> work, SyncConnection? conn = null)
    {
        busy++;
        _ = RunTrackedAsync(work, conn);
    }

    private async Task RunTrackedAsync(Func<Task> work, SyncConnection? conn)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            // 斷線或停止時取消：正常結束
        }
        catch (Exception ex)
        {
            Log.Error("同步引擎的工作失敗", ex);
            if (conn is not null)
                Fail(conn, LocalFailure);
        }
        finally
        {
            busy--;
        }
    }

    private long NowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();
}
