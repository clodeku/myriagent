using System.Collections.Concurrent;
using System.Threading.Channels;
using Myriagent.Sync;
using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 一條已證明身分的裝置連線：上行狀態（對帳模式）與自己的批次佇列——hub 方法只入列、立即返回，
/// 佇列依到達順序交給 <see cref="EventLog"/>，提交之後才送 BatchAck（sync-protocol 1.1、5.1）。
/// <paramref name="features"/> 是這條連線 Hello 時協商出的功能（sync-protocol 14.1；沒宣告是空的）；<paramref name="envelopeConn"/> 是這條連線
/// Hello 回應的 nonceS（信封的 conn，sync-protocol 14.4），<paramref name="helloHubSpki"/> 是 Hello 時 hubSig 所用的伺服器身分公鑰；
/// <paramref name="updateOnly"/>＝只更新的連線（sync-protocol 1.4）：不啟動批次佇列、不進對帳、不當用量查詢的候選
/// </summary>
internal sealed class DeviceConnection(string connectionId, string deviceId, Action abort, UpstreamContext upstream, IReadOnlyList<string>? features = null,
    string? envelopeConn = null, string? helloHubSpki = null, bool updateOnly = false)
{
    /// <summary>每條連線最多排幾批（裝置最多 4 批在途；超過是用戶端的錯）</summary>
    public const int MaxQueuedBatches = 8;

    private volatile UpstreamContext upstream = upstream;

    private volatile bool proven;

    private long order;

    public string ConnectionId { get; } = connectionId;

    public string DeviceId { get; } = deviceId;

    /// <summary>這條連線協商出的功能（裝置與伺服器宣告的交集，依裝置宣告的順序）；第 3 階段的裝置與 WP4.1 是空的</summary>
    public IReadOnlyList<string> Features { get; } = features ?? [];

    /// <summary>
    /// 信封 body 的 <c>conn</c>（sync-protocol 14.4）：這條連線 Hello 回應的 nonceS——每條連線在 Hello 新產生一次、在 hubSig 的範圍內，
    /// 裝置在 HelloResponse 看得到。<see cref="ConnectionId"/>（SignalR 配的）只拿來路由：裝置略過 negotiate，拿不到它
    /// </summary>
    public string? EnvelopeConn { get; } = envelopeConn;

    /// <summary>Hello 時 hubSig 所用的伺服器身分公鑰（SPKI 的 base64url；不知道是 null）</summary>
    public string? HelloHubSpki { get; } = helloHubSpki;

    /// <summary>Hello 時版本閘門判成只更新（app 太舊而宣告了 update1，sync-protocol 1.4）：算在線、撤銷時一起中斷，但不是用量查詢的候選</summary>
    public bool UpdateOnly { get; } = updateOnly;

    /// <summary>
    /// 能不能對這條連線簽信封（sync-protocol 14.4）：要有 <see cref="EnvelopeConn"/>，而且 Hello 時的身分公鑰等於目前的
    /// （<paramref name="currentHubSpki"/>）。身分金鑰輪換之後，裝置還釘著舊公鑰，送出的信封必然驗章失敗、被算成竄改；不簽，改中斷這條連線
    /// （裝置重連時依輪換聲明改釘）
    /// </summary>
    public bool CanSignEnvelopes(string currentHubSpki)
        => EnvelopeConn is not null && HelloHubSpki is not null && string.Equals(HelloHubSpki, currentHubSpki, StringComparison.Ordinal);

    /// <summary>登記的先後（<see cref="ConnectionRegistry.Add"/> 時配發，越大越新；同一台有兩條連線時以最新的為準）</summary>
    public long Order
    {
        get => Interlocked.Read(ref order);
        internal set => Interlocked.Exchange(ref order, value);
    }

    /// <summary>Prove 已完成（<see cref="ConnectionRegistry.MarkProven"/>）：之後才對它送第 4 階段的訊息</summary>
    public bool IsProven => proven;

    /// <summary>
    /// 已 Prove 的完整連線：第 4 階段判斷「這台最新的連線」（疊加層的新舊、快照請求的對象）只看它。只更新的連線不同步、不送即時更新也不收快照請求，
    /// 同一台較舊的完整連線還在時（換版退回、舊版重開的反向重疊）不能讓它把那條連線當成過時的
    /// </summary>
    public bool IsProvenSyncing => proven && !UpdateOnly;

    public UpstreamContext Upstream
    {
        get => upstream;
        set => upstream = value;
    }

    public Channel<Batch> Batches { get; } = Channel.CreateBounded<Batch>(new BoundedChannelOptions(MaxQueuedBatches)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    /// <summary>處理批次佇列的工作（連線斷了照樣把排進來的做完：寫入冪等，確認送不到時裝置 Prove 時對齊）；只更新的連線一直是已完成的空工作</summary>
    public Task Pump { get; set; } = Task.CompletedTask;

    public void Abort() => abort();

    /// <summary>
    /// 可以對這條連線送（或從它收）這個裝置 hub 方法嗎（sync-protocol 14.1、14.3、14.6）：第 3 階段的方法一律可以；第 4 階段的要這條連線協商出
    /// <see cref="DeviceFeatureRules.RequiredFor"/> 的功能。伺服器送第 4 階段的訊息之前一律先問它；不是 <see cref="DeviceHubMethods"/> 的名稱丟例外
    /// </summary>
    public bool Allows(string method) => DeviceFeatureRules.RequiredFor(method) is not { } feature || DeviceFeatureRules.Has(Features, feature);

    internal void SetProven() => proven = true;
}

/// <summary>
/// 裝置的連線登記（同一台可能同時有兩條連線：重連時舊的還沒斷）：已證明身分的連線才登記、才算在線。
/// 心跳時間是伺服器收到的時間（不看裝置的時鐘）；證明身分時算一次。
/// 只更新的連線（sync-protocol 1.4）也登記：<see cref="IsOnline"/> 算它，<see cref="IsSyncing"/> 不算。
/// 第 4 階段的接縫（WP4.1 D51）：<see cref="ConnectionProven"/>、<see cref="ConnectionRemoved"/> 兩個連線事件與 <see cref="FeaturesOf"/>。
/// 兩個事件在呼叫端的執行緒同步觸發（要很快、不可阻塞），處理者丟出的例外記錄後吞掉，不影響其他處理者與登記本身（沒有 logger 時只吞掉）
/// </summary>
internal sealed class ConnectionRegistry(ILogger<ConnectionRegistry>? log = null) : IDevicePresence
{
    private readonly ConcurrentDictionary<string, DeviceConnection> connections = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, long> heartbeats = new(StringComparer.Ordinal);

    private long order;

    /// <summary>上線或離線（裝置 id）：用量指派要重算</summary>
    public event Action<string>? PresenceChanged;

    /// <summary>
    /// 一條連線完成 Prove（<see cref="MarkProven"/>：DeviceHub 標成已證明之後呼叫，這時 ProveResponse 還沒送到裝置）：
    /// 第 4 階段要在連上之後送的（durable 指令的重簽、觀看清單）從這裡排。裝置在交握完成前收到的第 4 階段訊息會暫存
    /// （sync-protocol 14.4 的早到緩衝，指令最多 16 則），處理者不要在這裡同步送
    /// </summary>
    public event Action<DeviceConnection>? ConnectionProven;

    /// <summary>一條連線從登記移除（斷線、Prove 失敗、撤銷中斷）；在 <see cref="PresenceChanged"/> 之前觸發</summary>
    public event Action<DeviceConnection>? ConnectionRemoved;

    public bool IsOnline(string deviceId) => connections.Values.Any(c => c.DeviceId == deviceId);

    /// <summary>在線的電腦數（同一台的新舊連線算一台；MyriagentHub 主機卡片的連線數，stat1）</summary>
    public int OnlineDeviceCount => connections.Values.Select(c => c.DeviceId).Distinct(StringComparer.Ordinal).Count();

    /// <summary>
    /// 這台有完整同步的連線（至少一條不是只更新的；換版重啟交疊時，新舊其中一條是完整的就算）。用量查詢的候選看這個：
    /// 只更新的連線的 UsagePublish 被 hub 的關卡擋下，指派給它的帳戶就沒人查
    /// </summary>
    public bool IsSyncing(string deviceId) => connections.Values.Any(c => c.DeviceId == deviceId && !c.UpdateOnly);

    /// <summary>這台在線，而且每一條連線都是只更新的（版本頁顯示用）</summary>
    public bool IsUpdateOnly(string deviceId)
    {
        var mine = ConnectionsOf(deviceId);
        return mine.Count > 0 && mine.All(c => c.UpdateOnly);
    }

    public DeviceConnection? Get(string connectionId) => connections.TryGetValue(connectionId, out var connection) ? connection : null;

    public IReadOnlyList<DeviceConnection> ConnectionsOf(string deviceId) => connections.Values.Where(c => c.DeviceId == deviceId).ToList();

    /// <summary>
    /// 這台目前在線的連線協商出的功能（device.upsert 的 <c>features</c>，sync-protocol 14.1）：同時有兩條連線時取最新登記的那條；不在線是空的
    /// </summary>
    public IReadOnlyList<string> FeaturesOf(string deviceId) =>
        connections.Values.Where(c => c.DeviceId == deviceId).MaxBy(c => c.Order)?.Features ?? [];

    public void Add(DeviceConnection connection, long now)
    {
        connection.Order = Interlocked.Increment(ref order);
        connections[connection.ConnectionId] = connection;
        heartbeats[connection.DeviceId] = now;
        PresenceChanged?.Invoke(connection.DeviceId);
    }

    /// <summary>這條連線完成 Prove：標成已證明並觸發 <see cref="ConnectionProven"/>（只在第一次；不是登記中的這個物件時什麼都不做）</summary>
    public void MarkProven(DeviceConnection connection)
    {
        if (connection.IsProven || !connections.TryGetValue(connection.ConnectionId, out var registered) || !ReferenceEquals(registered, connection))
            return;
        connection.SetProven();
        Raise(ConnectionProven, connection, nameof(ConnectionProven));
    }

    /// <summary>移除連線；回傳它（沒登記過為 null）</summary>
    public DeviceConnection? Remove(string connectionId)
    {
        if (!connections.TryRemove(connectionId, out var connection))
            return null;
        if (!IsOnline(connection.DeviceId))
            heartbeats.TryRemove(connection.DeviceId, out _);
        Raise(ConnectionRemoved, connection, nameof(ConnectionRemoved));
        PresenceChanged?.Invoke(connection.DeviceId);
        return connection;
    }

    public void Heartbeat(string deviceId, long now)
    {
        if (IsOnline(deviceId))
            heartbeats[deviceId] = now;
    }

    /// <summary>最後一次心跳的伺服器時間；不在線為 null</summary>
    public long? LastHeartbeat(string deviceId) => IsOnline(deviceId) && heartbeats.TryGetValue(deviceId, out var at) ? at : null;

    /// <summary>中斷這台的所有連線（撤銷時；只更新的連線也一樣）</summary>
    public void AbortDevice(string deviceId)
    {
        foreach (var connection in ConnectionsOf(deviceId))
            connection.Abort();
    }

    /// <summary>
    /// 中斷所有裝置的所有連線（伺服器身分金鑰輪換之後，sync-protocol 14.4）：連線上的裝置釘著舊公鑰，重連時 Hello 帶輪換聲明、自動改釘
    /// </summary>
    public void AbortAll()
    {
        foreach (var connection in connections.Values.ToList())
            connection.Abort();
    }

    /// <summary>逐一呼叫處理者：一個丟例外不影響其他的（記錄後吞掉）</summary>
    private void Raise(Action<DeviceConnection>? handlers, DeviceConnection connection, string name)
    {
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<DeviceConnection>>())
        {
            try
            {
                handler(connection);
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "裝置連線事件 {Event} 的處理者出錯：{DeviceId} {Connection}", name, connection.DeviceId, connection.ConnectionId);
            }
        }
    }
}
