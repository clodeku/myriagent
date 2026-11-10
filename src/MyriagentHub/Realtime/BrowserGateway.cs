using MyriagentHub.Hosting;
using MyriagentHub.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器閘道（設計 4.9.2、14.6；web-protocol 第六節第 8 點）：扮演瀏覽器網頁的「宿主」，送 <c>init</c> 與整份取代的
/// <c>accounts</c>／<c>devices</c>／<c>cli</c>／<c>settings</c>／<c>tokens</c>。
/// <list type="bullet">
/// <item>單一背景迴圈；事件處理常式只記髒並喚醒（<see cref="MarkDirty"/>）。</item>
/// <item>最多每 PushIntervalMs 讀一次快照（<see cref="BrowserPushRules.ReadDue"/>，以單調時鐘量）；沒有變動通知時每 RefreshSeconds 保底重讀；沒有連線時不讀。</item>
/// <item>快照分五部分，序列化後與上一份比對：有變的部分版本＋1、推送訊息只組一次（各連線共用同一份字串）；沒變不送。</item>
/// <item><c>init</c> 由迴圈送出（屏障），一定帶目前最新的各部分版本；送 init 之前那條連線不送其他訊息：標成送過 init 與 init 入列在連線鎖裡
/// 一起完成，toast 也在同一把鎖裡檢查「已送過 init」再入列（<see cref="SendToast"/>），所以看得到已送過 init 時 init 一定已在佇列裡。
/// 屏障保留還排著的 toast、移到 init 後面（toast 不是快照的一部分）；已經出列的管不到——溢出清空佇列之後、標回「還沒送過」之前入列的 toast，
/// 可能比 resync 的 init 先到（那時網頁已經有狀態）。</item>
/// <item>送出佇列溢出：清空並重送 init（第 3 階段的 resync），60 秒內第 3 次就以 <c>slow_client</c> 中斷。</item>
/// <item>讀取失敗：記錯誤、沿用上一份快照、髒旗標放回，以 1、2、5、10 秒退避。</item>
/// </list>
/// 宣告成 partial：第 4 階段（WP4.2）的對話訊息在 BrowserGateway.Conversations.cs（設計 13.4）；<paramref name="views"/> 是 null（單元測試的
/// GatewayHarness）時不送任何對話訊息，init 的 <c>conversations</c> 是空的，與第 3 階段相同。<paramref name="overlay"/> 是即時疊加層（步 11；只在有
/// <paramref name="views"/> 時用），閘道是它唯一的消費者。<paramref name="drafts"/> 是伺服器草稿（WP4.6 步 7；BrowserGateway.Drafts.cs）：
/// 第一次送出建立的新對話出現時，以 <c>conversationClosed.replacedBy</c> 換掉草稿。<paramref name="stats"/> 是各電腦即時狀態的看板
/// （stat1；BrowserGateway.Stats.cs）：null 時不送 <c>deviceStats</c>。<paramref name="environment"/>：只用來判斷開發用的調整（<see cref="BrowserTuningRules"/>）；null＝不送
/// </summary>
internal sealed partial class BrowserGateway(
    IBrowserSnapshotSource source,
    BrowserConnections connections,
    IOptions<HubOptions> options,
    TimeProvider time,
    ILogger<BrowserGateway> log,
    ConversationViews? views = null,
    LiveOverlay? overlay = null,
    BrowserDrafts? drafts = null,
    DeviceStatsBoard? stats = null,
    IHostEnvironment? environment = null,
    MyriagentHub.Auth.CollaboratorDirectory? collaborators = null,
    ComposerDraftStore? composerDrafts = null) : BackgroundService
{
    public const string SlowClientReason = "slow_client";

    /// <summary>推送的部分（送出順序）</summary>
    internal static readonly IReadOnlyList<BrowserParts> PushParts = [BrowserParts.Accounts, BrowserParts.Devices, BrowserParts.DevicesCli, BrowserParts.Settings, BrowserParts.Tokens,
        BrowserParts.Schedules];

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly object runGate = new();
    private readonly Dictionary<BrowserParts, long> versions = PushParts.ToDictionary(p => p, _ => 0L);
    private readonly Dictionary<BrowserParts, ClientOutboxMessage> pushes = [];
    private readonly Dictionary<string, (string Versions, ClientOutboxMessage Message)> inits = new(StringComparer.Ordinal);

    /// <summary>init 的 remote.reconnectGraceMs：只在 Development、Testing 而且有設定時才有（端到端縮短「重新連線中」的等待）</summary>
    private readonly int? reconnectGraceMs = BrowserTuningRules.ReconnectGraceMs(environment?.EnvironmentName, options.Value.Browser.ReconnectGraceMs);
    private int dirty;
    private BrowserSnapshotData? snapshot;
    private IReadOnlyList<DraftAccount> draftAccounts = [];
    private long? lastReadAt;
    private long? retryAt;
    private int failures;

    /// <summary>目前各部分的版本（測試用）</summary>
    internal IReadOnlyDictionary<BrowserParts, long> Versions
    {
        get
        {
            lock (runGate)
                return new Dictionary<BrowserParts, long>(versions);
        }
    }

    /// <summary>
    /// 伺服器草稿可以用的帳戶（WP4.2 步 13）：最後一份帳戶快照的邏輯帳戶 id 與代理種類（依顯示順序，同網頁的帳戶清單）；還沒讀過快照時是空的。任何執行緒都能讀
    /// </summary>
    public IReadOnlyList<DraftAccount> DraftAccounts => Volatile.Read(ref draftAccounts);

    /// <summary>只記髒＋喚醒（事件處理常式在寫入者或 SignalR 的執行緒上，不可阻塞）</summary>
    public void MarkDirty(BrowserParts parts)
    {
        if (parts == BrowserParts.None)
            return;
        Interlocked.Or(ref dirty, (int)parts);
        Wake();
    }

    /// <summary>這條連線要一則 init（ready 的回應；由迴圈送出）</summary>
    public void RequestInit(BrowserConnection connection, string mode)
    {
        connection.RequestInit(mode);
        Wake();
    }

    /// <summary>
    /// 迴圈之外送一則 toast 給一條連線（指令被拒，D17）：這條連線已送過 init、同一 cmd 10 秒內沒送過才入列，檢查與入列在連線鎖裡一起做
    /// （<see cref="BrowserConnection.TryEnqueueToast"/>；迴圈也在同一把鎖裡標成送過 init 並把 init 入列），所以 toast 不會比這條連線的 init 先送出。
    /// 與推送同一套溢出處理——溢出就排重送 init 並喚醒迴圈，60 秒內第 3 次就中斷。這裡只碰連線自己的狀態（各自加鎖），不碰迴圈的快照與版本
    /// </summary>
    public void SendToast(BrowserConnection connection, string cmd, ClientOutboxMessage toast)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(toast);
        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (connection.TryEnqueueToast(cmd, nowMs, toast) is { } result && HandleResult(connection, result, nowMs).Resync)
            Wake();
    }

    /// <summary>一輪（背景迴圈呼叫；單元測試直接驅動）：回傳「最晚多久後要再醒」（Timeout.InfiniteTimeSpan＝等到喚醒）</summary>
    internal Task<TimeSpan> RunOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (runGate)
            return Task.FromResult(RunOnce());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && stoppingToken.IsCancellationRequested))
            {
                // 每一輪的最外層：閘道的錯誤不讓伺服器停機（.NET 預設 BackgroundService 丟出例外就停主機）
                log.LogError(ex, "瀏覽器閘道的一輪失敗");
                delay = Backoff[0];
            }
            try
            {
                if (delay != TimeSpan.Zero)
                    await wake.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// 停機：取消迴圈並等它結束（base），之後每條連線的送出佇列停收，幫浦送完已排的就結束（連線本身由 ApplicationStopping 的中斷與 SignalR 關閉）。
    /// 寫在這裡而不是 ExecuteAsync 的 finally：迴圈還沒開始跑就被停掉時（BackgroundService 以 Task.Run 啟動 ExecuteAsync），finally 不會執行
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.StopAsync(cancellationToken);
        }
        finally
        {
            foreach (var connection in connections.Snapshot())
                connection.Outbox.Complete();
        }
    }

    public override void Dispose()
    {
        wake.Dispose();
        base.Dispose();
    }

    private TimeSpan RunOnce()
    {
        var settings = options.Value.Browser;
        var pushInterval = TimeSpan.FromMilliseconds(Math.Max(1, settings.PushIntervalMs));
        var refresh = TimeSpan.FromSeconds(Math.Max(1, settings.RefreshSeconds));
        var all = connections.Snapshot();
        if (all.Count == 0)
        {
            DiscardConversations();
            return Timeout.InfiniteTimeSpan;
        }

        var now = time.GetTimestamp();
        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        var backingOff = retryAt is { } retry && now < retry;
        if (!backingOff && BrowserPushRules.ReadDue(Since(lastReadAt, now), Volatile.Read(ref dirty) != 0, snapshot is not null, pushInterval, refresh))
            Read(now, nowMs);

        var resync = false;
        if (snapshot is not null)
        {
            foreach (var connection in all)
                resync |= Deliver(connection, nowMs);
        }
        // 多人協作：擁有者的 collaborators、協作人開著但不再分享的對話（init 之後、對話訊息之前）
        resync |= RunCollaborators(all, nowMs);
        // 對話訊息在 init 與各部分之後（WP4.2 步 10）：init 已經帶了清單與清掉的對話狀態
        var conversations = RunConversations(all, now, nowMs, ref resync);
        // 電腦狀態（stat1）：開著面板的連線，在 init 之後
        resync |= RunStats(all, nowMs);

        if (resync)
            return TimeSpan.Zero;
        TimeSpan delay;
        if (retryAt is { } next)
        {
            delay = Positive(time.GetElapsedTime(now, next));
        }
        else
        {
            var since = Since(lastReadAt, now) ?? TimeSpan.Zero;
            delay = Positive((Volatile.Read(ref dirty) != 0 ? pushInterval : refresh) - since);
        }
        return Earlier(delay, conversations);
    }

    /// <summary>讀快照、比對、更新版本與推送訊息；失敗時髒旗標放回並退避</summary>
    private void Read(long now, long nowMs)
    {
        var taken = (BrowserParts)Interlocked.Exchange(ref dirty, 0);
        var parts = snapshot is null || taken == BrowserParts.None ? BrowserParts.All : taken;
        BrowserSnapshotData next;
        try
        {
            next = source.Read(parts, snapshot, nowMs);
        }
        catch (Exception ex)
        {
            Interlocked.Or(ref dirty, (int)taken);
            var delay = Backoff[Math.Min(failures, Backoff.Length - 1)];
            failures++;
            retryAt = now + (long)(delay.TotalSeconds * time.TimestampFrequency);
            log.LogError(ex, "瀏覽器閘道讀不到快照（第 {Count} 次），{Delay} 秒後再試；沿用上一份", failures, delay.TotalSeconds);
            return;
        }
        failures = 0;
        retryAt = null;
        lastReadAt = now;
        var previous = snapshot;
        var devicesVersion = versions[BrowserParts.Devices];
        Update(BrowserParts.Accounts, previous?.AccountsJson, next.AccountsJson, json => "{\"type\":\"accounts\",\"accounts\":" + json + "}");
        // 草稿的帳戶（步 13）：帳戶快照變了才重新解析
        if (previous is null || !string.Equals(previous.AccountsJson, next.AccountsJson, StringComparison.Ordinal))
            Volatile.Write(ref draftAccounts, BrowserDraftRules.AccountsOf(next.AccountsJson));
        Update(BrowserParts.Devices, previous?.DevicesJson, next.DevicesJson, json => "{\"type\":\"devices\",\"devices\":" + json + "}");
        // 裝置目錄變了（改名、撤銷）：對話清單與開著的標頭帶裝置名稱，跟著重送（WP4.2 步 10）
        if (previous is not null && versions[BrowserParts.Devices] != devicesVersion)
            MarkAllConversationDevices();
        Update(BrowserParts.DevicesCli, previous?.DevicesCliJson, next.DevicesCliJson, json => "{\"type\":\"cli\",\"cli\":{},\"devicesCli\":" + json + "}");
        Update(BrowserParts.Settings, previous?.SettingsJson, next.SettingsJson, json => "{\"type\":\"settings\",\"settings\":" + json + "}");
        Update(BrowserParts.Tokens, previous?.TokensJson, next.TokensJson, json => "{\"type\":\"tokens\",\"tokens\":" + json + "}");
        Update(BrowserParts.Schedules, previous?.SchedulesJson, next.SchedulesJson, json => "{\"type\":\"schedules\",\"schedules\":" + json + "}");
        snapshot = next;
    }

    private void Update(BrowserParts part, string? before, string after, Func<string, string> message)
    {
        if (before is not null && string.Equals(before, after, StringComparison.Ordinal))
            return;
        versions[part]++;
        var json = message(after);
        pushes[part] = new ClientOutboxMessage(TypeOf(part), json);
    }

    /// <summary>一條連線：該送 init 就送（屏障）；已送過 init 的送版本比它看過的新的部分。溢出要重送 init 時回 true</summary>
    private bool Deliver(BrowserConnection connection, long nowMs)
    {
        if (connection.TryTakePendingInit(out var mode))
        {
            // 標成已送過 init 與 init 入列在連線鎖裡一起完成（WP3.7 步驟 1、合併前審查）：init 一入列，送出幫浦隨時送出、網頁收到就可能送指令，
            // 這時指令一定看得到「已送過 init」（D17）；反過來，指令看得到「已送過 init」時 init 一定已在佇列裡，toast 排在它後面。
            // init 先在鎖外組好（快取沒命中時要組整份 JSON）
            if (connection.IsCollaborator)
            {
                // 協作人（多人協作）：自己的 init，沒有 control、之後也不推帳戶、電腦這些部分
                var (collaboratorInit, collaboratorList) = CollaboratorInitMessage(mode, connection, nowMs);
                var collaboratorResync = HandleResult(connection, connection.EnqueueInit(collaboratorInit, versions), nowMs).Resync;
                if (collaboratorList is not null)
                    connection.MarkConversationListSent(collaboratorList);
                return collaboratorResync;
            }
            var (collaboratorsState, collaboratorsSeen) = OwnerCollaboratorsForInit();
            var (init, list) = InitMessage(mode, connection, nowMs, collaboratorsState, collaboratorsSeen);
            var resync = HandleResult(connection, connection.EnqueueInit(init, versions), nowMs).Resync;
            connection.MarkCollaboratorsSeen(collaboratorsSeen);
            // init 入列時清掉了上一份清單的標記（D42）；它帶的清單也算送過（WP4.2 步 10）
            if (list is not null)
                connection.MarkConversationListSent(list);
            resync |= EnqueueControl(connection, nowMs);
            // 輸入框的草稿（draft1）：整份的跟在 init 後面
            resync |= EnqueueComposerDrafts(connection, nowMs);
            return resync;
        }
        if (!connection.InitSent || connection.IsCollaborator)
            return false;
        foreach (var part in PushParts)
        {
            var version = versions[part];
            if (version <= connection.SeenVersion(part) || !pushes.TryGetValue(part, out var push))
                continue;
            var (outcome, resync) = Enqueue(connection, push, nowMs);
            if (!outcome)
                return resync;
            connection.MarkSeen(part, version);
        }
        return false;
    }

    /// <summary>入列並處理結果（<see cref="HandleResult"/>）</summary>
    private (bool Queued, bool Resync) Enqueue(BrowserConnection connection, ClientOutboxMessage message, long nowMs) =>
        HandleResult(connection, connection.Outbox.Enqueue(message), nowMs);

    /// <summary>
    /// 入列的結果：溢出時依 60 秒內的次數重送 init（resync＝true）或以 slow_client 中斷。回傳 (有沒有排進去, 要不要立刻再跑一輪)。
    /// 在連線鎖之外呼叫（記溢出、標回還沒送過 init 都要取連線鎖，中斷會碰送出佇列）
    /// </summary>
    private (bool Queued, bool Resync) HandleResult(BrowserConnection connection, ClientOutboxResult result, long nowMs)
    {
        if (result != ClientOutboxResult.Overflow)
            return (true, false);
        views?.Counters.OutboxOverflow();
        switch (connection.RecordOverflow(nowMs))
        {
            case OverflowAction.Resync:
                log.LogWarning("瀏覽器連線的送出佇列溢出，清空並重送 init：{Connection}", connection.ConnectionId);
                connection.ResetForResync();
                return (false, true);
            case OverflowAction.Abort:
                log.LogWarning("瀏覽器連線 60 秒內第 {Count} 次溢出，中斷：{Connection}", BrowserLimitRules.OverflowsBeforeAbort, connection.ConnectionId);
                try
                {
                    connection.Abort(SlowClientReason);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "中斷瀏覽器連線失敗：{Connection}", connection.ConnectionId);
                }
                return (false, false);
            default:
                throw new ArgumentOutOfRangeException(nameof(result), "未知的溢出處理");
        }
    }

    /// <summary>
    /// init（web-protocol 第六節第 8 點）：每個 mode、每組版本只組一次。有對話儲存時 <c>conversations</c> 是這條連線的時區、這一刻的清單
    /// （WP4.2 步 10；同一份清單、同一個時區與當地日期的連線共用同一份 init），連同清單的標記一起回傳
    /// </summary>
    private (ClientOutboxMessage Message, ConversationListMark? List) InitMessage(string mode, BrowserConnection connection, long nowMs,
        JToken? collaboratorsState = null, long collaboratorsSeen = 0)
    {
        var key = string.Join(",", PushParts.Select(p => versions[p])) + "|c" + collaboratorsSeen;
        ConversationListView? list = null;
        ConversationListMark? mark = null;
        if (views is not null)
        {
            (list, mark) = InitConversationList(connection, nowMs);
            key += "|" + list.Key;
        }
        if (inits.TryGetValue(mode, out var cached) && cached.Versions == key)
            return (cached.Message, mark);
        var data = snapshot!;
        var init = new JObject
        {
            ["type"] = ClientOutboxRules.InitType,
            ["mode"] = mode,
            ["popoutConversationId"] = null,
            ["app"] = new JObject { ["version"] = BuildInfo.Current.Version, ["build"] = BuildInfo.Current.Build, ["commitAt"] = BuildInfo.Current.CommitAtMs },
            ["settings"] = new JRaw(data.SettingsJson),
            ["accounts"] = new JRaw(data.AccountsJson),
            ["conversations"] = list is null ? new JArray() : new JRaw(list.ItemsJson),
            ["cli"] = new JObject(),
            ["providers"] = AccountViewRules.Providers(),
            ["devicesCli"] = new JRaw(data.DevicesCliJson),
            ["tokens"] = new JRaw(data.TokensJson),
            // 各電腦的排程（sched1）：沒有來源時是 null（網頁不顯示排程頁）
            ["schedules"] = new JRaw(data.SchedulesJson),
            ["layout"] = null,
            ["remote"] = new JObject
            {
                ["mode"] = "browser",
                ["deviceId"] = null,
                ["devices"] = new JRaw(data.DevicesJson),
                ["sync"] = null,
            },
            // 多人協作：擁有者看的協作人清單與分享（沒有名冊是 null）；協作人的 init 另外組（BrowserGateway.Collaborators.cs）
            ["collaborators"] = collaboratorsState ?? JValue.CreateNull(),
            ["viewer"] = new JObject { ["role"] = "owner" },
        };
        if (reconnectGraceMs is { } grace)
            init["remote"]!["reconnectGraceMs"] = grace;
        var message = new ClientOutboxMessage(ClientOutboxRules.InitType, init.ToString(Formatting.None));
        inits[mode] = (key, message);
        return (message, mark);
    }

    private void Wake()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // 已經排了一次喚醒
        }
        catch (ObjectDisposedException)
        {
            // 停機中
        }
    }

    private TimeSpan? Since(long? at, long now) => at is { } start ? time.GetElapsedTime(start, now) : null;

    private static TimeSpan Positive(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;

    private static string TypeOf(BrowserParts part) => part switch
    {
        BrowserParts.Accounts => "accounts",
        BrowserParts.Devices => "devices",
        BrowserParts.DevicesCli => "cli",
        BrowserParts.Settings => "settings",
        BrowserParts.Tokens => "tokens",
        BrowserParts.Schedules => "schedules",
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, "不是單一的推送部分"),
    };
}

/// <summary>
/// 事件 → <see cref="BrowserGateway.MarkDirty"/>：處理常式在寫入者（提交之後）或 SignalR 的執行緒上，只記髒、不可阻塞。
/// 目錄類（帳戶、裝置、偏好、CLI）→ 全部；用量 → accounts；對話的 meta、item 類 → 不重讀快照（WP4.2 D13：由對話的變更通知處理）；
/// 其他類別保守地當全部；裝置上下線 → accounts＋devices，並讓閘道重送它的對話的標頭（D39）。
/// 對話的變更通知（<paramref name="feed"/>）、即時疊加層有新的即時更新（<paramref name="overlay"/>，步 11）、ClientState 換了（時區、看得到、未讀的看見，步 12A）
/// 與各電腦即時狀態的看板發布了新的一份（<paramref name="stats"/>，stat1）只喚醒閘道。
/// 裝置送了 token 計數（<paramref name="tokens"/>，tok1）→ tokens。
/// 停機（ApplicationStopping，早於各服務的 StopAsync 與 Kestrel 停止）→ <see cref="BrowserConnections.Shutdown"/>：不再登記新連線、中斷全部連線並停掉送出幫浦
/// （SignalR 停機時也會關連線，這裡明確收尾，停機不必等送出逾時）
/// </summary>
internal sealed class BrowserGatewayWiring(
    SyncNotifier notifier,
    ConnectionRegistry registry,
    BrowserGateway gateway,
    BrowserConnections connections,
    IHostApplicationLifetime lifetime,
    ILogger<BrowserGatewayWiring> log,
    ConversationChangeFeed? feed = null,
    LiveOverlay? overlay = null,
    DeviceStatsBoard? stats = null,
    TokenTallies? tokens = null,
    DeviceScheduleStore? schedules = null,
    ComposerDraftStore? composerDrafts = null) : IHostedService
{
    private CancellationTokenRegistration stopping;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        notifier.EventCommitted += OnCommitted;
        registry.PresenceChanged += OnPresenceChanged;
        connections.StateChanged += OnStateChanged;
        if (feed is not null)
            feed.Posted += OnConversationPosted;
        if (overlay is not null)
            overlay.Changed += OnConversationPosted;
        if (stats is not null)
            stats.SnapshotPublished += gateway.WakeStats;
        if (tokens is not null)
            tokens.Changed += OnTokensChanged;
        if (schedules is not null)
            schedules.Changed += OnSchedulesChanged;
        if (composerDrafts is not null)
            composerDrafts.Changed += gateway.OnComposerDraftChanged;
        stopping = lifetime.ApplicationStopping.Register(OnStopping);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        notifier.EventCommitted -= OnCommitted;
        registry.PresenceChanged -= OnPresenceChanged;
        connections.StateChanged -= OnStateChanged;
        if (feed is not null)
            feed.Posted -= OnConversationPosted;
        if (overlay is not null)
            overlay.Changed -= OnConversationPosted;
        if (stats is not null)
            stats.SnapshotPublished -= gateway.WakeStats;
        if (tokens is not null)
            tokens.Changed -= OnTokensChanged;
        if (schedules is not null)
            schedules.Changed -= OnSchedulesChanged;
        if (composerDrafts is not null)
            composerDrafts.Changed -= gateway.OnComposerDraftChanged;
        stopping.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>ApplicationStopping 的處理常式（在 StopApplication 的執行緒上同步呼叫）：例外一律記錄後吞掉，不打斷其他的停機處理常式</summary>
    internal void OnStopping()
    {
        try
        {
            connections.Shutdown();
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "停機時中斷瀏覽器連線失敗");
        }
    }

    internal static BrowserParts PartsFor(string cls) => cls switch
    {
        Myriagent.Sync.SyncClasses.Catalog => BrowserParts.All,
        Myriagent.Sync.SyncClasses.Usage => BrowserParts.Accounts,
        Myriagent.Sync.SyncClasses.Meta or Myriagent.Sync.SyncClasses.Item => BrowserParts.None,
        _ => BrowserParts.All,
    };

    private void OnCommitted(string cls, long seq) => gateway.MarkDirty(PartsFor(cls));

    private void OnPresenceChanged(string deviceId)
    {
        gateway.MarkDirty(BrowserParts.Accounts | BrowserParts.Devices);
        gateway.MarkConversationDevice(deviceId);
    }

    /// <summary>ClientState 或 visible 指令的清單換了：時區、「看得到」與未讀的「看見」都要重算（步 12A）</summary>
    private void OnStateChanged(BrowserConnection connection) => gateway.ViewersChanged();

    private void OnConversationPosted() => gateway.WakeConversations();

    /// <summary>裝置送了 token 計數（tok1）：只重讀 tokens</summary>
    private void OnTokensChanged() => gateway.MarkDirty(BrowserParts.Tokens);

    /// <summary>裝置送了排程的摘要（sched1）：只重讀 schedules</summary>
    private void OnSchedulesChanged() => gateway.MarkDirty(BrowserParts.Schedules);
}
