using System.Collections.Concurrent;
using Myriagent.Sync;

namespace MyriagentHub.Sync;

/// <summary>一台裝置的對話計數（ops/status 的 <c>conversations.devices[]</c>，觀察回補用；WP4.2 計畫 D31）</summary>
internal sealed record ConversationDeviceCounts(string DeviceId, long Owned, long Recovering, long Items, long MaxSeq, long LastEventAt);

/// <summary>對話的營運計數快照（只有計數，不含對話 id 與標題：<c>/readyz</c> 以裝置身分也讀得到，D31）</summary>
/// <param name="LiveDropped">被丟掉的即時更新，依原因（<see cref="ConversationCounters.LiveDropReasons"/>；步 11）</param>
/// <param name="EventsBaselined">啟動前就在 sync.db 的對話事件已經背景補進 <see cref="ConversationEvents"/>／<see cref="ConversationEventBytes"/></param>
internal sealed record ConversationCountsSnapshot(
    long Count,
    long Deleted,
    long Recovering,
    long Items,
    IReadOnlyDictionary<string, long> Rejected,
    long RemaskedEvents,
    long ConversationEvents,
    long ConversationEventBytes,
    IReadOnlyList<ConversationDeviceCounts> Devices,
    long Unread = 0,
    long OverlayBytes = 0,
    IReadOnlyDictionary<string, long>? LiveDropped = null,
    long OutboxOverflows = 0,
    long RemaskedLive = 0,
    bool EventsBaselined = false);

/// <summary>被丟掉的即時更新的原因（ops/status 的 <c>liveDropped</c> 的鍵，D31；WP4.2 步 11）</summary>
internal static class LiveDropReason
{
    /// <summary>超過速率（每台、每個對話的權杖桶），或排著等閘道取走的太多（丟最舊的）</summary>
    public const string Rate = "rate";

    /// <summary>命中第二道 DLP、遮蔽之後仍命中或不合格（D44）</summary>
    public const string Dlp = "dlp";

    /// <summary>連線沒協商出 live1、不認得的對話、不是擁有者、世代不符、裝置已撤銷</summary>
    public const string NotOwner = "notOwner";

    /// <summary>對話已刪除或政策不是 full（答 4）</summary>
    public const string Policy = "policy";

    /// <summary>持久版本已經 final（D27 ①）</summary>
    public const string Final = "final";

    /// <summary>不合格（外框或 item）、來自較舊的連線、ver 沒有比較新、被持久版本作廢、送出它的連線已經斷了</summary>
    public const string Stale = "stale";
}

/// <summary>
/// 對話的營運計數（WP4.2 計畫 D31）：全部取自記憶體、以 <see cref="Interlocked"/> 累加，status 本身不查資料庫。
/// 目錄的數量（對話、已刪除、recovering、項目數，各擁有者的分項）由 <see cref="ConversationDirectoryCache"/> 在載入與每次提交之後以 <see cref="Track"/> 維護；
/// 被拒、再遮蔽、事件筆數與位元組由 <see cref="ConversationMaterializer"/> 在 AfterCommit 累加（被回滾的不算）。
/// </summary>
internal sealed class ConversationCounters
{
    /// <summary>對話事件會被拒的原因（rejected 的鍵；不含 dlp：伺服器遮蔽後接受、不回它，答 5）</summary>
    public static IReadOnlyList<string> RejectReasons { get; } =
        [ConversationRejectCodes.NotOwner, ConversationRejectCodes.Fenced, ConversationRejectCodes.Deleted, SyncErrorCodes.Invalid, SyncErrorCodes.TooLarge];

    /// <summary>即時更新被丟掉的原因（liveDropped 的鍵；步 11 的 LiveRelay 與疊加層累加，各原因見 <see cref="LiveDropReason"/>）</summary>
    public static IReadOnlyList<string> LiveDropReasons { get; } =
        [LiveDropReason.Rate, LiveDropReason.Dlp, LiveDropReason.NotOwner, LiveDropReason.Policy, LiveDropReason.Final, LiveDropReason.Stale];

    private readonly ConcurrentDictionary<string, DeviceCounter> devices = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, long> rejected = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, long> liveDropped = new(StringComparer.Ordinal);

    private long startSeq;

    private int baselined;

    private long unread;

    private long overlayBytes;

    private long outboxOverflows;

    private long remaskedLive;

    private long count;

    private long deleted;

    private long recovering;

    private long items;

    private long remaskedEvents;

    private long conversationEvents;

    private long conversationEventBytes;

    /// <summary>目錄的一列換掉時調整數量（<paramref name="before"/>／<paramref name="after"/> 是 null＝沒有這一列）；只在載入與 AfterCommit 呼叫</summary>
    public void Track(ConversationEntry? before, ConversationEntry? after)
    {
        Contribute(before, -1);
        Contribute(after, 1);
    }

    /// <summary>一筆被接受的對話事件（AfterCommit）：筆數、存下的 payload 位元組、作者的最大 seq 與最後時間</summary>
    public void Accepted(string deviceId, long seq, long bytes, long at, bool remasked)
    {
        Interlocked.Increment(ref conversationEvents);
        Interlocked.Add(ref conversationEventBytes, bytes);
        if (remasked)
            Interlocked.Increment(ref remaskedEvents);
        var device = Device(deviceId);
        Max(ref device.MaxSeq, seq);
        Max(ref device.LastEventAt, at);
    }

    /// <summary>一筆被拒的對話事件（AfterCommit）</summary>
    public void Rejected(string reason) => rejected.AddOrUpdate(reason, 1, (_, n) => n + 1);

    /// <summary>啟動時 sync.db 的最大 seq（目錄載入時記下）：之後被接受的對話事件都比它大，它之前的由 <see cref="AddBaseline"/> 補</summary>
    public long StartSeq => Interlocked.Read(ref startSeq);

    public void SetStartSeq(long seq) => Interlocked.Exchange(ref startSeq, seq);

    /// <summary>
    /// 補上啟動前就在 sync.db 的一段對話事件（seq ≤ <see cref="StartSeq"/>，背景分段查詢）：筆數、存下的 payload 位元組、每台作者的最大 seq 與最後時間
    /// </summary>
    public void AddBaseline(long events, long bytes, IEnumerable<(string DeviceId, long MaxSeq, long LastEventAt)> perDevice)
    {
        Interlocked.Add(ref conversationEvents, events);
        Interlocked.Add(ref conversationEventBytes, bytes);
        foreach (var (deviceId, maxSeq, lastAt) in perDevice)
        {
            var device = Device(deviceId);
            Max(ref device.MaxSeq, maxSeq);
            Max(ref device.LastEventAt, lastAt);
        }
    }

    /// <summary>啟動前的對話事件都補完了</summary>
    public void BaselineDone() => Interlocked.Exchange(ref baselined, 1);

    /// <summary>未讀的對話數（步 12A 的未讀集合改變時設）</summary>
    public void SetUnread(long value) => Interlocked.Exchange(ref unread, value);

    /// <summary>即時疊加層目前的位元組（步 11）</summary>
    public void SetOverlayBytes(long value) => Interlocked.Exchange(ref overlayBytes, value);

    /// <summary>一則即時更新被丟掉（步 11；<paramref name="reason"/> 是 <see cref="LiveDropReasons"/> 之一）</summary>
    public void LiveDropped(string reason) => liveDropped.AddOrUpdate(reason, 1, (_, n) => n + 1);

    /// <summary>一則即時更新再遮蔽後轉送（步 11）</summary>
    public void RemaskedLiveRelay() => Interlocked.Increment(ref remaskedLive);

    /// <summary>瀏覽器的送出佇列溢出一次（步 9、10）</summary>
    public void OutboxOverflow() => Interlocked.Increment(ref outboxOverflows);

    public ConversationCountsSnapshot Snapshot()
    {
        var reasons = RejectReasons.ToDictionary(r => r, r => rejected.TryGetValue(r, out var n) ? n : 0, StringComparer.Ordinal);
        foreach (var (reason, n) in rejected)
            reasons.TryAdd(reason, n);
        var dropped = LiveDropReasons.ToDictionary(r => r, r => liveDropped.TryGetValue(r, out var n) ? n : 0, StringComparer.Ordinal);
        var list = devices
            .Select(d => new ConversationDeviceCounts(d.Key, Interlocked.Read(ref d.Value.Owned), Interlocked.Read(ref d.Value.Recovering),
                Interlocked.Read(ref d.Value.Items), Interlocked.Read(ref d.Value.MaxSeq), Interlocked.Read(ref d.Value.LastEventAt)))
            .OrderBy(d => d.DeviceId, StringComparer.Ordinal)
            .ToList();
        return new ConversationCountsSnapshot(Interlocked.Read(ref count), Interlocked.Read(ref deleted), Interlocked.Read(ref recovering), Interlocked.Read(ref items),
            reasons, Interlocked.Read(ref remaskedEvents), Interlocked.Read(ref conversationEvents), Interlocked.Read(ref conversationEventBytes), list,
            Interlocked.Read(ref unread), Interlocked.Read(ref overlayBytes), dropped, Interlocked.Read(ref outboxOverflows), Interlocked.Read(ref remaskedLive),
            Volatile.Read(ref baselined) == 1);
    }

    private void Contribute(ConversationEntry? entry, int sign)
    {
        if (entry is null)
            return;
        if (entry.Deleted)
        {
            Interlocked.Add(ref deleted, sign);
            return;
        }
        Interlocked.Add(ref count, sign);
        Interlocked.Add(ref items, sign * (long)entry.ItemCount);
        if (entry.Recovering)
            Interlocked.Add(ref recovering, sign);
        if (entry.OwnerDeviceId is not { } owner)
            return;
        var device = Device(owner);
        Interlocked.Add(ref device.Owned, sign);
        Interlocked.Add(ref device.Items, sign * (long)entry.ItemCount);
        if (entry.Recovering)
            Interlocked.Add(ref device.Recovering, sign);
    }

    private DeviceCounter Device(string deviceId) => devices.GetOrAdd(deviceId, _ => new DeviceCounter());

    private static void Max(ref long target, long value)
    {
        var current = Interlocked.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
                return;
            current = seen;
        }
    }

    private sealed class DeviceCounter
    {
        public long Owned;

        public long Recovering;

        public long Items;

        public long MaxSeq;

        public long LastEventAt;
    }
}
