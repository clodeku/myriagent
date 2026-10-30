using Myriagent.Sync;

namespace MyriagentHub.Realtime;

/// <summary><see cref="DeviceStatsBoard.Publish"/> 的結果：<paramref name="Accepted"/>＝收下（太密的不收、照樣記 watch），<paramref name="Watched"/>＝現在有別人在看</summary>
internal readonly record struct StatsPublishOutcome(bool Accepted, bool Watched);

/// <summary>
/// 一份看板（每台一筆、依 deviceId 排序）與 MyriagentHub 主機的負載（還沒取樣是 null）；<paramref name="Version"/> 每次發布＋1，同一份內容的版本相同
/// </summary>
internal sealed record DeviceStatsSnapshot(long Version, IReadOnlyList<DeviceStatsEntry> Entries, HubStatsEntry? Hub = null);

/// <summary>
/// 各電腦即時狀態的看板（stat1；docs/sync-protocol.md 14.8）：每條已證明的裝置連線最新的一筆，只在記憶體（伺服器重啟就沒了，裝置 2～10 秒內會再送）。
/// 不寫資料庫、不稽核：給人看的即時資料，可丟。
/// <list type="bullet">
/// <item>同一條連線兩次至少隔 <see cref="DeviceStatsRules.MinPublishGapMs"/>，更密的不收（照樣記 watch、回 watched）。</item>
/// <item>連線斷了（<see cref="Remove"/>）或 <see cref="DeviceStatsRules.StaleAfterMs"/> 沒送（<see cref="ExpireStale"/>）就拿掉。</item>
/// <item><see cref="Snapshot"/> 每台取最後收下的那條連線（重連時新舊兩條可能同時在）；<c>ageMs</c> 以伺服器的單調時鐘算，不比較任何電腦的時鐘。</item>
/// <item>「有人在看」＝其他電腦最後一次送的 watch 是 true，或有瀏覽器開著電腦狀態（<see cref="BrowserWatching"/>）。</item>
/// </list>
/// 內部加鎖：hub 的呼叫執行緒、轉送迴圈與連線事件共用；<see cref="Changed"/> 在鎖外呼叫
/// </summary>
internal sealed class DeviceStatsBoard(TimeProvider time)
{
    private readonly object gate = new();
    private readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    private long seq;
    private long changes;
    private DeviceStatsSnapshot? published;

    /// <summary>有沒有瀏覽器連線開著電腦狀態（<c>AddHubStats</c> 接上瀏覽器連線；沒有瀏覽器閘道的主機是 false）。在鎖外呼叫</summary>
    public Func<bool> BrowserWatching { get; set; } = static () => false;

    /// <summary>看板的內容或看的人變了（任何執行緒；處理常式只喚醒，不可阻塞）</summary>
    public event Action? Changed;

    /// <summary>轉送迴圈發布了新的一份（<see cref="SetPublished"/> 之後、任何執行緒；瀏覽器閘道訂閱來喚醒自己，不可阻塞）</summary>
    public event Action? SnapshotPublished;

    /// <summary>轉送迴圈最後發布的一份（最多每秒一份；還沒有是 null）。任何執行緒都能讀</summary>
    public DeviceStatsSnapshot? Published => Volatile.Read(ref published);

    /// <summary>轉送迴圈發布新的一份（<see cref="DeviceStatsFanout"/>；版本要比上一份大）</summary>
    public void SetPublished(DeviceStatsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref published, snapshot);
        SnapshotPublished?.Invoke();
    }

    /// <summary>每次內容改變＋1（收下一筆、拿掉一台、有連線開始看）：轉送迴圈據此決定要不要再發布</summary>
    public long Changes
    {
        get
        {
            lock (gate)
                return changes;
        }
    }

    /// <summary>
    /// 一條連線送來一筆（呼叫端已檢查格式與功能）。收下時配新的 seq；watch 從 false 變 true 也算改變（那條連線要立刻收到一份）
    /// </summary>
    public StatsPublishOutcome Publish(string connectionId, string deviceId, DeviceStats stats, bool watch)
    {
        ArgumentNullException.ThrowIfNull(stats);
        var now = NowMs();
        bool accepted;
        bool changed;
        bool othersWatching;
        lock (gate)
        {
            if (!slots.TryGetValue(connectionId, out var slot))
            {
                slot = new Slot(deviceId);
                slots[connectionId] = slot;
            }
            var startedWatching = watch && !slot.Watch;
            slot.Watch = watch;
            accepted = slot.Stats is null || now - slot.ReceivedAt >= DeviceStatsRules.MinPublishGapMs;
            if (accepted)
            {
                slot.Stats = stats;
                slot.ReceivedAt = now;
                slot.Seq = ++seq;
            }
            changed = accepted || startedWatching;
            if (changed)
                changes++;
            othersWatching = slots.Values.Any(s => s.Watch && !string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal));
        }
        if (changed)
            Changed?.Invoke();
        return new StatsPublishOutcome(accepted, othersWatching || BrowserWatching());
    }

    /// <summary>有沒有人在看：任何一條連線最後一次送的 watch 是 true，或有瀏覽器開著電腦狀態（轉送迴圈據此決定要不要取樣主機）</summary>
    public bool AnyWatching()
    {
        bool devices;
        lock (gate)
            devices = slots.Values.Any(s => s.Watch);
        return devices || BrowserWatching();
    }

    /// <summary>看的人變了（瀏覽器開關面板）：只喚醒轉送迴圈（主機的負載要開始或停止取樣），內容沒變不算改變</summary>
    public void WatchersChanged() => Changed?.Invoke();

    /// <summary>連線斷了：拿掉它的那一筆（有的話通知）</summary>
    public void Remove(string connectionId)
    {
        bool removed;
        lock (gate)
        {
            removed = slots.Remove(connectionId);
            if (removed)
                changes++;
        }
        if (removed)
            Changed?.Invoke();
    }

    /// <summary>拿掉 <see cref="DeviceStatsRules.StaleAfterMs"/> 沒送的連線（轉送迴圈定期呼叫）；有拿掉就通知</summary>
    public void ExpireStale()
    {
        var now = NowMs();
        int removed;
        lock (gate)
        {
            var stale = slots.Where(p => p.Value.Stats is null || now - p.Value.ReceivedAt > DeviceStatsRules.StaleAfterMs).Select(p => p.Key).ToList();
            foreach (var id in stale)
                slots.Remove(id);
            removed = stale.Count;
            if (removed > 0)
                changes++;
        }
        if (removed > 0)
            Changed?.Invoke();
    }

    /// <summary>每台一筆（最後收下的那條連線）、依 deviceId 排序、最多 <see cref="DeviceStatsRules.MaxDevices"/> 台；ageMs 是到現在的經過時間</summary>
    public IReadOnlyList<DeviceStatsEntry> Snapshot()
    {
        var now = NowMs();
        lock (gate)
        {
            return slots.Values
                .Where(s => s.Stats is not null)
                .GroupBy(s => s.DeviceId, StringComparer.Ordinal)
                .Select(g => g.MaxBy(s => s.Seq)!)
                .OrderBy(s => s.DeviceId, StringComparer.Ordinal)
                .Take(DeviceStatsRules.MaxDevices)
                .Select(s => new DeviceStatsEntry(s.DeviceId, s.Seq, Math.Max(0, now - s.ReceivedAt), s.Stats!))
                .ToList();
        }
    }

    /// <summary>最後一次送的 watch 是 true 的裝置連線</summary>
    public IReadOnlyList<string> WatchingConnections()
    {
        lock (gate)
            return slots.Where(p => p.Value.Watch).Select(p => p.Key).ToList();
    }

    private long NowMs() => time.GetTimestamp() * 1000 / time.TimestampFrequency;

    private sealed class Slot(string deviceId)
    {
        public string DeviceId { get; } = deviceId;

        public DeviceStats? Stats { get; set; }

        /// <summary>收下的時間（單調時鐘的毫秒）</summary>
        public long ReceivedAt { get; set; }

        public long Seq { get; set; }

        public bool Watch { get; set; }
    }
}
