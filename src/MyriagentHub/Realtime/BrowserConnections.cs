using MyriagentHub.Sync;
using Microsoft.Extensions.Options;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>
/// 一條瀏覽器 hub 連線（設計 14.6）。可變狀態由 hub 的呼叫執行緒、閘道迴圈與工作階段監看共用：一律經方法、在 <c>gate</c> 鎖內讀寫；
/// 中斷連線（<see cref="Abort"/>）在鎖外呼叫，可以從任何執行緒呼叫、可以重複呼叫。
/// 伺服器沒有 UI 執行緒（「狀態只在 UI 執行緒」是桌面的規則），這裡以鎖保護。
/// 鎖的順序固定是連線的 <c>gate</c> → <see cref="ClientOutbox"/> 的鎖（<see cref="EnqueueInit"/>、<see cref="TryEnqueueToast"/>）：
/// 送出佇列不回呼連線、喚醒的續體非同步執行，<see cref="Abort"/> 放開連線鎖之後才碰送出佇列，所以不會反向取鎖。
/// 宣告成 partial：第 4 階段的對話檢視狀態在 BrowserConnection.Conversations.cs（WP4.2）。
/// </summary>
internal sealed partial class BrowserConnection
{
    private readonly object gate = new();
    private readonly Action abort;
    private readonly Action<BrowserConnection>? stateChanged;
    private readonly Dictionary<string, long> lastToastAt = new(StringComparer.Ordinal);
    private readonly HashSet<string> deniedSettings = new(StringComparer.Ordinal);
    private readonly Dictionary<BrowserParts, long> seen = [];
    private readonly List<long> overflows = [];
    private BrowserClientState? state;
    private long lastActivityMs;
    private bool activitySinceSweep;
    private string? abortReason;
    private string? pendingInitMode;
    private string lastInitMode = "main";
    private bool initSent;
    private long collaboratorsSeen;

    /// <param name="stateChanged">ClientState 或 <c>visible</c> 指令的清單換了之後（鎖外）呼叫：<see cref="BrowserConnections.StateChanged"/></param>
    /// <param name="collaboratorId">協作人的連線：協作人的 Identity 使用者 id（多人協作，2026-10-30）；null＝擁有者</param>
    internal BrowserConnection(string connectionId, string sessionId, string? ip, Action abort, ClientOutbox outbox, Action<BrowserConnection>? stateChanged = null,
        string? collaboratorId = null)
    {
        ConnectionId = connectionId;
        SessionId = sessionId;
        Ip = ip;
        CollaboratorId = collaboratorId;
        this.abort = abort;
        Outbox = outbox;
        this.stateChanged = stateChanged;
    }

    public string ConnectionId { get; }

    /// <summary>擁有者工作階段的 id（HubClaims.SessionId；key 的雜湊，不是 cookie 裡的 key）</summary>
    public string SessionId { get; }

    /// <summary>連線時的用戶端 IP（稽核與續期用）</summary>
    public string? Ip { get; }

    /// <summary>
    /// 協作人的連線（docs/plans/2026-10-30-collaborators.md）：協作人的 Identity 使用者 id。null＝擁有者。協作人只看得到、只能操作分享給他的對話，
    /// 閘道的各部分（帳戶、電腦、用量、排程）不給他
    /// </summary>
    public string? CollaboratorId { get; }

    /// <summary>協作人的連線（<see cref="CollaboratorId"/> 不是 null）</summary>
    public bool IsCollaborator => CollaboratorId is not null;

    public ClientOutbox Outbox { get; }

    /// <summary>送出幫浦（<see cref="ClientOutbox.PumpAsync"/>）</summary>
    public Task Pump { get; internal set; } = Task.CompletedTask;

    /// <summary>最後一次 ClientState（第 3 階段只驗證並保存；第 4 階段的觀看追蹤訂閱 <see cref="BrowserConnections.StateChanged"/>）</summary>
    public BrowserClientState? State
    {
        get
        {
            lock (gate)
                return state;
        }
    }

    /// <summary>最後一次通過速率檢查的 hub 呼叫的時間（Unix 毫秒）；還沒有呼叫過是 0</summary>
    public long LastActivityMs
    {
        get
        {
            lock (gate)
                return lastActivityMs;
        }
    }

    /// <summary>第一次中斷時的原因（send_timeout、slow_client、session_expired…）；沒有中斷過是 null</summary>
    public string? AbortReason
    {
        get
        {
            lock (gate)
                return abortReason;
        }
    }

    /// <summary>這條連線已經送過 init（之後才推送各部分、才送 toast）</summary>
    public bool InitSent
    {
        get
        {
            lock (gate)
                return initSent;
        }
    }

    /// <summary>記下 ClientState，之後在鎖外通知 <see cref="BrowserConnections.StateChanged"/></summary>
    public void SetState(BrowserClientState value)
    {
        lock (gate)
            state = value;
        stateChanged?.Invoke(this);
    }

    /// <summary>網頁送了 ready：記下要送的 init 的 mode（溢出重送 init 時沿用最後一次的 mode）</summary>
    public void RequestInit(string mode)
    {
        lock (gate)
        {
            pendingInitMode = mode;
            lastInitMode = mode;
        }
    }

    /// <summary>取走待送的 init（閘道迴圈）</summary>
    public bool TryTakePendingInit([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string mode)
    {
        lock (gate)
        {
            mode = pendingInitMode;
            pendingInitMode = null;
            return mode is not null;
        }
    }

    /// <summary>
    /// 把 init 入列並標成已送過 init（閘道迴圈，見 BrowserGateway.Deliver）：兩件事在連線鎖裡一起做，<see cref="TryEnqueueToast"/> 也在同一把鎖裡
    /// 檢查再入列，所以看得到「已送過 init」時 init 一定已在佇列裡，toast 一定排在它後面。它帶的各部分版本都算看過了。
    /// init 一律排得進去（屏障先清空、不算溢出），所以先標、不看入列的結果。同一把鎖裡清掉對話的檢視狀態（WP4.2 D42：網頁收到 init 會重新 open 畫面上的分頁）
    /// </summary>
    public ClientOutboxResult EnqueueInit(ClientOutboxMessage init, IReadOnlyDictionary<BrowserParts, long> versions)
    {
        lock (gate)
        {
            initSent = true;
            seen.Clear();
            collaboratorsSeen = 0;
            foreach (var (part, version) in versions)
                seen[part] = version;
            ResetConversationViewLocked();
            ResetStatsLocked();
            return Outbox.Enqueue(init);
        }
    }

    /// <summary>擁有者的連線看過的協作人狀態版本（<c>collaborators</c>，多人協作；init 帶的那一份算看過）</summary>
    public long CollaboratorsSeen
    {
        get
        {
            lock (gate)
                return collaboratorsSeen;
        }
    }

    public void MarkCollaboratorsSeen(long version)
    {
        lock (gate)
            collaboratorsSeen = Math.Max(collaboratorsSeen, version);
    }

    /// <summary>這條連線看過的這個部分的版本（沒看過是 0）</summary>
    public long SeenVersion(BrowserParts part)
    {
        lock (gate)
            return seen.TryGetValue(part, out var version) ? version : 0;
    }

    public void MarkSeen(BrowserParts part, long version)
    {
        lock (gate)
            seen[part] = Math.Max(version, seen.TryGetValue(part, out var current) ? current : 0);
    }

    /// <summary>溢出後的 resync：當成還沒送過 init，下一輪以最後一次的 mode 重送（送出前不推送其他訊息）</summary>
    public void ResetForResync()
    {
        lock (gate)
        {
            initSent = false;
            pendingInitMode = lastInitMode;
        }
    }

    /// <summary>記下一次溢出，回傳要重送 init 還是中斷（60 秒內第 3 次中斷）；只保留 60 秒內的紀錄</summary>
    public OverflowAction RecordOverflow(long nowMs)
    {
        lock (gate)
        {
            var action = BrowserLimitRules.OnOverflow(overflows, nowMs);
            overflows.Add(nowMs);
            overflows.RemoveAll(at => nowMs - at >= BrowserLimitRules.OverflowWindowMs);
            return action;
        }
    }

    /// <summary>hub 上有一次呼叫（工作階段監看依此決定要不要延長閒置期限）</summary>
    public void Touch(long nowMs)
    {
        lock (gate)
        {
            lastActivityMs = nowMs;
            activitySinceSweep = true;
        }
    }

    /// <summary>上一次取走以來有沒有活動（取走後清掉）</summary>
    public bool TakeActivity()
    {
        lock (gate)
        {
            var active = activitySinceSweep;
            activitySinceSweep = false;
            return active;
        }
    }

    /// <summary>
    /// 這個 cmd 被拒時的 toast（D17）：這條連線已送過 init、同一 cmd 10 秒內沒送過才入列並記下時間，回入列的結果；不送回 null。
    /// 檢查與入列在連線鎖裡一起做（與 <see cref="EnqueueInit"/> 同一把鎖），toast 不會比這條連線的 init 先入列。
    /// cmd 只會是認得的名稱，紀錄的大小有上限
    /// </summary>
    public ClientOutboxResult? TryEnqueueToast(string cmd, long nowMs, ClientOutboxMessage toast)
    {
        lock (gate)
        {
            if (!initSent)
                return null;
            long? last = lastToastAt.TryGetValue(cmd, out var at) ? at : null;
            if (!BrowserCommandRules.ShouldToast(last, nowMs))
                return null;
            lastToastAt[cmd] = nowMs;
            return Outbox.Enqueue(toast);
        }
    }

    /// <summary>toast 紀錄裡有幾個 cmd（測試用：只會是認得的名稱與 <see cref="BrowserCommandRules.SyncWildcardToast"/>）</summary>
    internal int ToastKeys
    {
        get
        {
            lock (gate)
                return lastToastAt.Count;
        }
    }

    /// <summary>這條連線第一次送這個桌面專屬的設定鍵時回 true（只稽核一次，不讓惡意分頁灌稽核）</summary>
    public bool MarkDeniedSetting(string key)
    {
        lock (gate)
            return deniedSettings.Add(key);
    }

    /// <summary>中斷連線：停收並清空送出佇列、呼叫 HubCallerContext.Abort。abort 委派丟出的例外原樣往外丟</summary>
    public void Abort(string reason)
    {
        lock (gate)
            abortReason ??= reason;
        Outbox.Complete();
        Outbox.Clear();
        abort();
    }
}

/// <summary>連線登記的結果</summary>
internal enum AdmitOutcome
{
    Admitted,

    /// <summary>超過連線上限（沒有登記）</summary>
    TooManyConnections,

    /// <summary>登記之後再查一次，工作階段已失效（已移除並中斷）</summary>
    SessionInvalid,

    /// <summary>停機已開始（<see cref="BrowserConnections.Shutdown"/> 之後），不再登記（沒有登記）</summary>
    ShuttingDown,
}

/// <summary>
/// 瀏覽器 hub 的連線登記：連線上限（每個工作階段、全部）、每個工作階段一個權杖桶（Cmd 與 ClientState 共用）、
/// 依工作階段中斷（撤銷、期限到了）與全部中斷（停機）。登記表與權杖桶在 <c>gate</c> 鎖內；中斷在鎖外。
/// 權杖桶不隨最後一條連線移除（關掉再重連不會拿到新的滿桶），由工作階段監看每一輪以 <see cref="PruneIdleBuckets"/> 丟掉沒有連線而且補滿的。
/// 第 4 階段的接縫（WP4.1 D51）：<see cref="StateChanged"/>、<see cref="Removed"/> 兩個連線事件（WP4.2 步 13 另加 <see cref="Added"/>），在呼叫端的執行緒、任何鎖之外同步觸發
/// （要很快、不可阻塞）；處理者丟出的例外記錄後吞掉，不影響其他處理者與呼叫端。
/// </summary>
internal sealed class BrowserConnections(IOptions<HubOptions> options, ILogger<BrowserConnections> log)
{
    /// <summary>停機時中斷連線的原因</summary>
    public const string ShutdownReason = "shutdown";

    /// <summary>一條連線的 ClientState 或 <c>visible</c> 指令的清單換了（開著的對話、可見、焦點、畫面上的對話；WP4.2 的觀看追蹤）</summary>
    public event Action<BrowserConnection>? StateChanged;

    /// <summary>一條連線從登記移除（斷線、登記時工作階段已失效）</summary>
    public event Action<BrowserConnection>? Removed;

    /// <summary>
    /// 一條連線登記成功（WP4.2 步 13：伺服器草稿以它知道工作階段在寬限內重新連上）：登記之後、<see cref="AdmitAsync"/> 查工作階段之前觸發，
    /// 查到已失效時接著觸發 <see cref="Removed"/>
    /// </summary>
    public event Action<BrowserConnection>? Added;

    private readonly object gate = new();
    private readonly Dictionary<string, BrowserConnection> connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TokenBucket> buckets = new(StringComparer.Ordinal);
    private bool shuttingDown;

    public int Count
    {
        get
        {
            lock (gate)
                return connections.Count;
        }
    }

    /// <summary>停機已開始（之後不再登記新連線）</summary>
    public bool IsShuttingDown
    {
        get
        {
            lock (gate)
                return shuttingDown;
        }
    }

    /// <summary>超過上限或停機已開始時回 null（不登記）；成功才登記並啟動送出幫浦（單則逾時 SendTimeoutSeconds 就中斷）</summary>
    public BrowserConnection? TryAdd(string connectionId, string sessionId, string? ip, Action abort, Func<string, CancellationToken, Task> send,
        string? collaboratorId = null) =>
        Register(connectionId, sessionId, ip, abort, send, collaboratorId, out _);

    private BrowserConnection? Register(string connectionId, string sessionId, string? ip, Action abort, Func<string, CancellationToken, Task> send,
        string? collaboratorId, out AdmitOutcome refusal)
    {
        var settings = options.Value.Browser;
        BrowserConnection connection;
        lock (gate)
        {
            if (shuttingDown)
            {
                refusal = AdmitOutcome.ShuttingDown;
                return null;
            }
            var sessionCount = connections.Values.Count(c => c.SessionId == sessionId);
            if (connections.ContainsKey(connectionId)
                || !BrowserLimitRules.CanAdmit(sessionCount, connections.Count, settings.MaxConnectionsPerSession, settings.MaxConnections))
            {
                refusal = AdmitOutcome.TooManyConnections;
                return null;
            }
            connection = new BrowserConnection(connectionId, sessionId, ip, abort, new ClientOutbox(settings.OutboxMaxMessages, settings.OutboxMaxBytes),
                changed => Raise(StateChanged, changed, nameof(StateChanged)), collaboratorId);
            connections[connectionId] = connection;
        }
        Raise(Added, connection, nameof(Added));
        refusal = AdmitOutcome.Admitted;
        connection.Pump = ClientOutbox.PumpAsync(connection.Outbox, send, TimeSpan.FromSeconds(settings.SendTimeoutSeconds), reason =>
        {
            log.LogWarning("瀏覽器連線送出失敗，中斷：{Connection} {Reason}", connectionId, reason);
            connection.Abort(reason);
        }, CancellationToken.None);
        return connection;
    }

    /// <summary>
    /// 登記一條連線（設計 14.6）：1. <see cref="TryAdd"/>（超過上限回 TooManyConnections、停機已開始回 ShuttingDown，都不查工作階段）；
    /// 2. 登記之後才呼叫 <paramref name="sessionStillValid"/>——撤銷一律先改資料庫、再發事件（SqliteTicketStore.RemoveAsync、SessionService.RevokeWhereAsync），
    /// 所以落在「cookie 驗證完成」與「登記」之間的撤銷不是被事件抓到（已登記）、就是被這次查詢看到（同 DeviceHub.Prove 的作法）；3. 已失效（或查詢失敗）就移除並中斷
    /// </summary>
    public async Task<(AdmitOutcome Outcome, BrowserConnection? Connection)> AdmitAsync(string connectionId, string sessionId, string? ip, Action abort,
        Func<string, CancellationToken, Task> send, Func<CancellationToken, Task<bool>> sessionStillValid, CancellationToken cancellationToken,
        string? collaboratorId = null)
    {
        if (Register(connectionId, sessionId, ip, abort, send, collaboratorId, out var refusal) is not { } connection)
            return (refusal, null);
        bool valid;
        try
        {
            valid = await sessionStillValid(cancellationToken);
        }
        catch (Exception ex)
        {
            // 查不到就不收（寧可讓用戶端重連）
            log.LogWarning(ex, "瀏覽器連線登記時查不到工作階段，中斷：{Connection}", connectionId);
            valid = false;
        }
        if (valid)
            return (AdmitOutcome.Admitted, connection);
        Remove(connectionId);
        try
        {
            connection.Abort("session_invalid");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "中斷瀏覽器連線失敗：{Connection}", connectionId);
        }
        return (AdmitOutcome.SessionInvalid, null);
    }

    /// <summary>
    /// 移除並停收它的送出佇列；回傳移除的連線（沒登記過為 null）。這個工作階段沒有連線了也不丟它的權杖桶：
    /// 丟掉的話關掉唯一的連線、重連就拿到一個新的滿桶，突發上限形同虛設（桶由 <see cref="PruneIdleBuckets"/> 在補滿之後才丟）
    /// </summary>
    public BrowserConnection? Remove(string connectionId)
    {
        BrowserConnection? connection;
        lock (gate)
        {
            if (!connections.Remove(connectionId, out connection))
                return null;
        }
        connection.Outbox.Complete();
        connection.Outbox.Clear();
        Raise(Removed, connection, nameof(Removed));
        return connection;
    }

    public BrowserConnection? Get(string connectionId)
    {
        lock (gate)
            return connections.TryGetValue(connectionId, out var connection) ? connection : null;
    }

    public IReadOnlyList<BrowserConnection> Snapshot()
    {
        lock (gate)
            return connections.Values.ToList();
    }

    /// <summary>有沒有連線開著電腦狀態（stat1 的「有人在看」；先取快照再在集合鎖外問各連線）</summary>
    public bool AnyMonitoring() => Snapshot().Any(c => c.Monitoring);

    /// <summary>目前有連線的工作階段</summary>
    public IReadOnlyCollection<string> ActiveSessions()
    {
        lock (gate)
            return connections.Values.Select(c => c.SessionId).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Cmd 與 ClientState 共用的速率（每個工作階段一個權杖桶，設計 8.5）；RateLimits:Enabled=false 時一律放行。
    /// 沒有連線的工作階段不建桶（呼叫與移除同時發生時，不留下沒人會清的桶）
    /// </summary>
    public bool TryTakeToken(string sessionId, long nowMs)
    {
        var limits = options.Value.RateLimits;
        if (!limits.Enabled)
            return true;
        lock (gate)
        {
            if (!buckets.TryGetValue(sessionId, out var bucket))
            {
                if (!connections.Values.Any(c => c.SessionId == sessionId))
                    return false;
                buckets[sessionId] = bucket = new TokenBucket(limits.BrowserCmdPerSecond, Math.Max(1, limits.BrowserCmdBurst), nowMs);
            }
            return bucket.TryTake(nowMs);
        }
    }

    /// <summary>
    /// 丟掉沒有連線而且已經補滿的權杖桶（工作階段監看每一輪呼叫）；回傳丟掉幾個。補滿的桶等同新建的，這時丟掉不會讓人多拿到權杖；
    /// 還沒補滿的留著（工作階段一直沒有連線也會在補滿後的下一輪丟掉）
    /// </summary>
    public int PruneIdleBuckets(long nowMs)
    {
        lock (gate)
        {
            var idle = buckets.Where(b => b.Value.IsFull(nowMs) && !connections.Values.Any(c => c.SessionId == b.Key)).Select(b => b.Key).ToList();
            foreach (var sessionId in idle)
                buckets.Remove(sessionId);
            return idle.Count;
        }
    }

    /// <summary>目前有幾個權杖桶（測試用）</summary>
    internal int BucketCount
    {
        get
        {
            lock (gate)
                return buckets.Count;
        }
    }

    /// <summary>中斷這個工作階段的所有連線；回傳中斷的條數。某一條的 abort 丟例外時其他照樣中斷，最後以 AggregateException 丟出</summary>
    public int AbortSession(string sessionId, string reason)
    {
        List<BrowserConnection> targets;
        lock (gate)
            targets = connections.Values.Where(c => c.SessionId == sessionId).ToList();
        return AbortEach(targets, reason);
    }

    /// <summary>中斷這位協作人的所有連線（刪除、停用、重設密碼；多人協作）；回傳中斷的條數</summary>
    public int AbortCollaborator(string collaboratorId, string reason)
    {
        List<BrowserConnection> targets;
        lock (gate)
            targets = connections.Values.Where(c => c.CollaboratorId == collaboratorId).ToList();
        return AbortEach(targets, reason);
    }

    /// <summary>目前有連線的協作人（給擁有者看的清單的「在線」）</summary>
    public IReadOnlySet<string> OnlineCollaborators()
    {
        lock (gate)
            return connections.Values.Where(c => c.CollaboratorId is not null).Select(c => c.CollaboratorId!).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>中斷全部連線；回傳中斷的條數</summary>
    public int AbortAll(string reason) => AbortEach(Snapshot(), reason);

    /// <summary>
    /// 停機（ApplicationStopping）：先標記不再登記新連線，再以 <see cref="ShutdownReason"/> 中斷全部連線（停收並清空送出佇列，幫浦隨即結束）。
    /// 標記在前：被中斷的分頁會立刻自動重連，停機期間（Kestrel 停止收新連線之前）連進來的不登記，不讓停機等到逾時。可以重複呼叫
    /// </summary>
    public int Shutdown()
    {
        lock (gate)
            shuttingDown = true;
        return AbortAll(ShutdownReason);
    }

    private int AbortEach(IReadOnlyList<BrowserConnection> targets, string reason)
    {
        List<Exception>? errors = null;
        foreach (var connection in targets)
        {
            try
            {
                connection.Abort(reason);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }
        if (targets.Count > 0)
            log.LogInformation("中斷 {Count} 條瀏覽器連線：{Reason}", targets.Count, reason);
        if (errors is not null)
            throw new AggregateException("中斷瀏覽器連線失敗", errors);
        return targets.Count;
    }

    /// <summary>逐一呼叫處理者：一個丟例外不影響其他的（記錄後吞掉）</summary>
    private void Raise(Action<BrowserConnection>? handlers, BrowserConnection connection, string name)
    {
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<BrowserConnection>>())
        {
            try
            {
                handler(connection);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "瀏覽器連線事件 {Event} 的處理者出錯：{Connection}", name, connection.ConnectionId);
            }
        }
    }
}
