using System.Threading.Channels;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Microsoft.Extensions.Options;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>疊加層裡的一格（閘道迴圈用）：即時更新、收下的先後與渲染結果的快取（同一則只渲染一次，各連線共用）</summary>
internal sealed class LiveSlot(LiveEntry entry, long age, long receivedAt)
{
    public LiveEntry Entry { get; } = entry;

    /// <summary>收下的先後（淘汰最舊的用）</summary>
    public long Age { get; } = age;

    /// <summary>伺服器收下它的時間（Unix 毫秒；項目沒有 createdAt 時當成建立時間，同持久項目的 ReceivedAt）</summary>
    public long ReceivedAt { get; } = receivedAt;

    /// <summary>渲染好的 Item JSON（閘道第一次要送時才渲染；只在閘道迴圈裡讀寫）</summary>
    public string? Rendered { get; set; }

    /// <summary>
    /// 這一格取代的同一筆項目（同一個 ord、同一個 id）最後渲染過的版本與它的 <see cref="Age"/>：<c>itemPatch</c> 的基底（patch1）。
    /// 被取代的那格沒渲染過就沿用它的基底；算過增量就放掉 <see cref="PrevRendered"/>（只在閘道迴圈裡讀寫）
    /// </summary>
    public string? PrevRendered { get; set; }

    public long? PrevAge { get; set; }

    /// <summary>已經算過 <c>itemPatch</c>（每格最多算一次、各連線共用）</summary>
    public bool PatchComputed { get; set; }

    /// <summary>算好的 <c>itemPatch</c>；null＝沒有基底、不值得或算不出（送整筆）</summary>
    public ClientOutboxMessage? PatchMessage { get; set; }
}

/// <summary>疊加層這一輪的結果：收下（要推給觀看者）的與被拿掉（要改推持久版本）的</summary>
internal sealed record LiveDrain(IReadOnlyList<LiveSlot> Accepted, IReadOnlyList<LiveEntry> Removed);

/// <summary>一筆持久變更對疊加層的影響（<see cref="LiveOverlay.OnDurable"/>）：被拿掉的疊加與仍然有效（持久版本推過之後要再推一次）的</summary>
internal sealed record LiveDurableEffect(IReadOnlyList<LiveEntry> Removed, IReadOnlyList<LiveSlot> Kept)
{
    public static LiveDurableEffect None { get; } = new([], []);
}

/// <summary>
/// 伺服器的即時疊加層（WP4.2 計畫步 11；D27、提案 C4、K5）：裝置 hub 的 <see cref="LiveRelayIntake"/> 檢查過的即時更新以 <see cref="Offer"/> 放進有界的佇列
/// （滿了丟最舊的、計入 <c>liveDropped.rate</c>），閘道迴圈每一輪以 <see cref="Drain"/> 取走、照 <see cref="LiveOverlayRules.Decide"/> 收進以 (對話, ord) 為鍵的疊加，
/// 再推給開著那個對話的瀏覽器（D24：渲染只在閘道迴圈）。
/// <list type="bullet">
/// <item>持久版本（<see cref="OnDurable"/>，閘道取走變更時）：status 不是 Running（包括 Waiting）就清掉那一格、不比 ver（提案 C4 的 a）；仍是 Running 的檢查點
/// 只有在「提交時這條連線是那台最新的已 Prove 連線」而且 ver 不比疊加舊時才作廢它（D27 ③）。持久版本的標記只為有觀看者或有疊加的對話保留，之後的
/// 即時更新在 final 時丟掉（b）。<c>conv.itemRemoved</c>、政策降級的清除、刪除清掉對應的格。</item>
/// <item>裝置連線（<see cref="ConnectionRegistry"/> 的事件，在 SignalR 的執行緒上）：<c>ConnectionRemoved</c> 清掉那條連線的疊加（c）、<c>ConnectionProven</c>
/// 清掉同一台 Order 較小的連線的疊加（只更新的連線 Prove 不算，<see cref="DeviceConnection.IsProvenSyncing"/>）；被清掉的記下來，閘道下一輪改推持久版本（<see cref="TakeRemoved"/>）。</item>
/// <item>上限：每個對話 <c>OverlayMaxOrdsPerConversation</c> 個 ord、全部 <c>OverlayMaxBytes</c>，超過淘汰最舊的（<see cref="LiveOverlayRules.Evict"/>）。</item>
/// </list>
/// 狀態以一把鎖保護（閘道迴圈與連線事件、WP4.5／第 5 階段的 <see cref="RemoveConversation"/> 都會碰；都很快）。
/// </summary>
internal sealed class LiveOverlay : IHostedService
{
    /// <summary>還沒被閘道取走的即時更新最多幾則（每則 ≤64 KB；滿了丟最舊的）</summary>
    public const int MaxQueued = 512;

    /// <summary>每個對話最多記幾筆持久版本的標記（超過丟 ord 最小的；只有最近的項目還會收到即時更新）</summary>
    public const int MaxMarksPerConversation = 256;

    private readonly object sync = new();
    private readonly Dictionary<(string ConversationId, long Ord), LiveSlot> slots = [];
    private readonly Dictionary<string, Dictionary<long, DurableMark>> marks = new(StringComparer.Ordinal);
    private readonly List<LiveEntry> removed = [];
    private readonly Channel<LiveEntry> incoming;
    private readonly ConnectionRegistry registry;
    private readonly ConversationDirectoryCache directory;
    private readonly ConversationCounters counters;
    private readonly IOptions<HubOptions> options;
    private long bytes;
    private long age;

    public LiveOverlay(ConnectionRegistry registry, ConversationDirectoryCache directory, ConversationCounters counters, IOptions<HubOptions> options)
    {
        this.registry = registry;
        this.directory = directory;
        this.counters = counters;
        this.options = options;
        incoming = Channel.CreateBounded<LiveEntry>(new BoundedChannelOptions(MaxQueued)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        }, _ => counters.LiveDropped(LiveDropReason.Rate));
    }

    /// <summary>有新的即時更新、或有疊加被連線事件清掉（處理者只能喚醒閘道：在 SignalR 的執行緒上觸發）</summary>
    public event Action? Changed;

    /// <summary>閘道這一輪要不要處理疊加層</summary>
    public bool HasPending
    {
        get
        {
            if (incoming.Reader.Count > 0)
                return true;
            lock (sync)
                return removed.Count > 0;
        }
    }

    /// <summary>目前疊加的格數（測試與營運用）</summary>
    public int Count
    {
        get
        {
            lock (sync)
                return slots.Count;
        }
    }

    /// <summary>目前疊加的位元組（UTF-8 的 item；ops/status 的 overlayBytes）</summary>
    public long Bytes
    {
        get
        {
            lock (sync)
                return bytes;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        registry.ConnectionRemoved += OnConnectionRemoved;
        registry.ConnectionProven += OnConnectionProven;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        registry.ConnectionRemoved -= OnConnectionRemoved;
        registry.ConnectionProven -= OnConnectionProven;
        return Task.CompletedTask;
    }

    /// <summary>放進一則（裝置 hub 的呼叫執行緒；不等、不阻塞）</summary>
    public void Offer(LiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        incoming.Writer.TryWrite(entry);
        Changed?.Invoke();
    }

    /// <summary>
    /// 閘道迴圈：取走排著的即時更新，逐則決定收不收（連線已斷、對話已不能即時更新〔刪除、降級、換了擁有者或世代〕的丟掉），超過上限淘汰最舊的。
    /// 回傳這一輪收下、還留著的格，與被淘汰的（之前收下的；要改推持久版本）
    /// </summary>
    public LiveDrain Drain(long nowMs)
    {
        var accepted = new List<LiveSlot>();
        var evictedEntries = new List<LiveEntry>();
        var settings = options.Value.Conversations;
        lock (sync)
        {
            while (incoming.Reader.TryRead(out var entry))
            {
                if (DropReason(entry) is { } reason)
                {
                    counters.LiveDropped(reason);
                    continue;
                }
                var key = (entry.ConversationId, entry.Ord);
                slots.TryGetValue(key, out var existing);
                var verdict = LiveOverlayRules.Decide(existing?.Entry, Mark(entry.ConversationId, entry.Ord), entry);
                switch (verdict)
                {
                    case LiveVerdict.Accept:
                        var slot = new LiveSlot(entry, ++age, nowMs);
                        if (existing is not null)
                        {
                            bytes -= existing.Entry.Bytes;
                            if (string.Equals(existing.Entry.ItemId, entry.ItemId, StringComparison.Ordinal))
                                (slot.PrevRendered, slot.PrevAge) = existing.Rendered is { } rendered ? (rendered, existing.Age) : (existing.PrevRendered, existing.PrevAge);
                        }
                        slots[key] = slot;
                        bytes += entry.Bytes;
                        accepted.Add(slot);
                        break;
                    case LiveVerdict.DropFinal:
                        counters.LiveDropped(LiveDropReason.Final);
                        break;
                    case LiveVerdict.DropOlderConnection:
                    case LiveVerdict.DropNotNewer:
                    case LiveVerdict.DropSuperseded:
                        counters.LiveDropped(LiveDropReason.Stale);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(nowMs), verdict, "未知的即時更新處置");
                }
            }

            var all = slots.Values.OrderBy(s => s.Age).Select(s => s.Entry).ToList();
            foreach (var entry in LiveOverlayRules.Evict(all, settings.OverlayMaxOrdsPerConversation, settings.OverlayMaxBytes))
            {
                RemoveSlotLocked((entry.ConversationId, entry.Ord));
                evictedEntries.Add(entry);
            }
            accepted.RemoveAll(s => !slots.TryGetValue((s.Entry.ConversationId, s.Entry.Ord), out var current) || !ReferenceEquals(current, s));
            counters.SetOverlayBytes(bytes);
        }
        return new LiveDrain(accepted, evictedEntries);
    }

    /// <summary>
    /// 閘道取走一筆持久變更時（依提交順序；<paramref name="watched"/>＝這個對話現在有人開著或看得到）：
    /// <list type="bullet">
    /// <item><c>Item</c>：記下持久版本的標記（有觀看者或有疊加時）；status 不是 Running 就清掉那一格（提案 C4 的 a），仍是 Running 時照 D27 ③ 判斷作廢，沒作廢的回傳在 Kept
    /// （閘道推過持久版本之後要再推一次疊加）。</item>
    /// <item><c>ItemRemoved</c>：清掉這個項目的格。</item>
    /// <item><c>Purged</c>、<c>Deleted</c>：清掉這個對話全部的格與標記。</item>
    /// <item><c>Turn</c> 的 completed（一輪結束）：清掉這個對話全部的格（標記保留）。</item>
    /// </list>
    /// </summary>
    public LiveDurableEffect OnDurable(ConversationChange change, bool watched)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (sync)
        {
            switch (change.Kind)
            {
                case ConversationChangeKind.Item when change.Ord is { } ord:
                {
                    var key = (change.ConversationId, ord);
                    var hasSlot = slots.TryGetValue(key, out var slot);
                    var mark = new DurableMark(change.Ver ?? 0, change.Final, LatestProvenOrder(change.Author));
                    if (hasSlot || watched || marks.ContainsKey(change.ConversationId))
                        SetMarkLocked(change.ConversationId, ord, mark);
                    if (!hasSlot)
                        return LiveDurableEffect.None;
                    if (LiveOverlayRules.ClearedByDurable(change.Status, mark, slot!.Entry))
                    {
                        RemoveSlotLocked(key);
                        counters.SetOverlayBytes(bytes);
                        return new LiveDurableEffect([slot!.Entry], []);
                    }
                    return new LiveDurableEffect([], [slot]);
                }
                case ConversationChangeKind.ItemRemoved when change.ItemId is { } itemId:
                {
                    var gone = slots.Values.Where(s => s.Entry.ConversationId == change.ConversationId && s.Entry.ItemId == itemId).Select(s => s.Entry).ToList();
                    foreach (var entry in gone)
                        RemoveSlotLocked((entry.ConversationId, entry.Ord));
                    counters.SetOverlayBytes(bytes);
                    return gone.Count == 0 ? LiveDurableEffect.None : new LiveDurableEffect(gone, []);
                }
                case ConversationChangeKind.Purged:
                case ConversationChangeKind.Deleted:
                    return new LiveDurableEffect(RemoveConversationLocked(change.ConversationId), []);
                case ConversationChangeKind.Turn when string.Equals(change.TurnPhase, ConversationTurnPhases.Completed, StringComparison.Ordinal):
                {
                    // 一輪結束（D27）：這一輪的項目都已經有持久版本，剩下的疊加不會再更新；清掉格、保留持久版本的標記
                    var gone = slots.Values.Where(s => s.Entry.ConversationId == change.ConversationId).Select(s => s.Entry).ToList();
                    foreach (var entry in gone)
                        RemoveSlotLocked((entry.ConversationId, entry.Ord));
                    counters.SetOverlayBytes(bytes);
                    return gone.Count == 0 ? LiveDurableEffect.None : new LiveDurableEffect(gone, []);
                }
                default:
                    return LiveDurableEffect.None;
            }
        }
    }

    /// <summary>
    /// open 的快照之後（閘道迴圈）：以快照讀到的項目補上持久版本的標記（不知道是哪條連線提交的，記 0、不比 ver），final 的格清掉。
    /// 回傳被清掉的（其他開著的連線要改推持久版本）
    /// </summary>
    public IReadOnlyList<LiveEntry> ReconcileOpen(string conversationId, IEnumerable<(long Ord, bool Final)> page)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(page);
        var gone = new List<LiveEntry>();
        lock (sync)
        {
            foreach (var (ord, final) in page)
            {
                // 快照不比已記的標記舊（閘道先處理變更才讀快照）：final 不同時以快照為準；相同時保留已記的（含提交時的連線）
                var known = Mark(conversationId, ord);
                SetMarkLocked(conversationId, ord, known is not null && known.Final == final ? known : new DurableMark(known?.Ver ?? 0, final, 0));
                if (final && slots.TryGetValue((conversationId, ord), out var slot))
                {
                    RemoveSlotLocked((conversationId, ord));
                    gone.Add(slot.Entry);
                }
            }
            counters.SetOverlayBytes(bytes);
        }
        return gone;
    }

    /// <summary>這個對話目前的格（依 ord；open 之後推給那條連線）</summary>
    public IReadOnlyList<LiveSlot> SlotsOf(string conversationId)
    {
        lock (sync)
            return slots.Values.Where(s => s.Entry.ConversationId == conversationId).OrderBy(s => s.Entry.Ord).ToList();
    }

    /// <summary>(對話, ord) 現在有這個項目的疊加（被清掉之後同一輪又收下了新的一則：不必改推持久版本）</summary>
    public bool Covers(string conversationId, long ord, string itemId)
    {
        lock (sync)
            return slots.TryGetValue((conversationId, ord), out var slot) && string.Equals(slot.Entry.ItemId, itemId, StringComparison.Ordinal);
    }

    /// <summary>這一格還在疊加層裡（閘道推之前確認：同一輪後面的變更可能已經清掉它）</summary>
    public bool IsCurrent(LiveSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        lock (sync)
            return slots.TryGetValue((slot.Entry.ConversationId, slot.Entry.Ord), out var current) && ReferenceEquals(current, slot);
    }

    /// <summary>取走被連線事件、<see cref="RemoveConversation"/> 清掉的格（閘道迴圈：改推持久版本）</summary>
    public IReadOnlyList<LiveEntry> TakeRemoved()
    {
        lock (sync)
        {
            if (removed.Count == 0)
                return [];
            var taken = removed.ToList();
            removed.Clear();
            return taken;
        }
    }

    /// <summary>
    /// 清掉一個對話全部的疊加與標記（WP4.5 的交接、第 5 階段的 epoch 改變；6.1）：被清掉的閘道下一輪改推持久版本
    /// </summary>
    public void RemoveConversation(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        lock (sync)
            removed.AddRange(RemoveConversationLocked(conversationId));
        Changed?.Invoke();
    }

    /// <summary>
    /// 整份重讀時（變更通知溢出、上一輪處理失敗）：清掉全部的疊加與標記——漏掉的持久變更可能已經作廢它們；開著的對話由閘道重新 open，即時更新下一則再補上
    /// </summary>
    public void Clear()
    {
        lock (sync)
        {
            slots.Clear();
            marks.Clear();
            removed.Clear();
            bytes = 0;
            counters.SetOverlayBytes(0);
        }
    }

    /// <summary>
    /// 只保留有觀看者或有疊加的對話的持久版本標記（閘道每一輪處理完之後；<paramref name="watched"/> 是現在開著或看得到的對話）
    /// </summary>
    public void PruneMarks(IReadOnlySet<string> watched)
    {
        ArgumentNullException.ThrowIfNull(watched);
        lock (sync)
        {
            var withSlots = slots.Keys.Select(k => k.ConversationId).ToHashSet(StringComparer.Ordinal);
            foreach (var conversationId in marks.Keys.Where(id => !watched.Contains(id) && !withSlots.Contains(id)).ToList())
                marks.Remove(conversationId);
        }
    }

    /// <summary>持久版本的標記（測試用）</summary>
    internal DurableMark? MarkOf(string conversationId, long ord)
    {
        lock (sync)
            return Mark(conversationId, ord);
    }

    /// <summary>有幾個對話記著持久版本的標記（測試用）</summary>
    internal int MarkedConversations
    {
        get
        {
            lock (sync)
                return marks.Count;
        }
    }

    /// <summary>
    /// 這台裝置目前最新的已 Prove 完整連線的 Order（不在線是 0：沒有連線的 Order 是 0，所以不會拿 ver 比）。
    /// 只更新的連線不算（<see cref="DeviceConnection.IsProvenSyncing"/>）：它不送即時更新，較新的它不讓較舊的完整連線過時
    /// </summary>
    internal long LatestProvenOrder(string deviceId) =>
        registry.ConnectionsOf(deviceId).Where(c => c.IsProvenSyncing).Select(c => c.Order).DefaultIfEmpty(0).Max();

    /// <summary>
    /// 取走時再看一次（收件之後、閘道取走之前可能變了）：送出它的連線已經不在、或同一台已有 Order 較大的完整連線 Prove 了（同 <c>ConnectionProven</c>
    /// 清掉較舊連線的疊加；只更新的連線不算）→ stale；對話已刪除、政策不是 full → policy；不認得、換了擁有者或世代 → notOwner
    /// </summary>
    private string? DropReason(LiveEntry entry)
    {
        var connections = registry.ConnectionsOf(entry.DeviceId);
        if (!connections.Any(c => c.Order == entry.ConnectionOrder) || connections.Any(c => c.IsProvenSyncing && c.Order > entry.ConnectionOrder))
            return LiveDropReason.Stale;
        if (!directory.TryGet(entry.ConversationId, out var conversation) || conversation.OwnerDeviceId != entry.DeviceId || conversation.Epoch != entry.Epoch)
            return LiveDropReason.NotOwner;
        if (conversation.Deleted || conversation.SyncPolicy != ConversationSyncPolicies.Full)
            return LiveDropReason.Policy;
        return null;
    }

    private void OnConnectionRemoved(DeviceConnection connection) =>
        RemoveWhere(e => e.DeviceId == connection.DeviceId && e.ConnectionOrder == connection.Order);

    private void OnConnectionProven(DeviceConnection connection)
    {
        // 只更新的連線不取代同一台的完整連線（反向重疊：完整連線還在，較新的只更新連線 Prove 了）
        if (connection.UpdateOnly)
            return;
        RemoveWhere(e => e.DeviceId == connection.DeviceId && e.ConnectionOrder < connection.Order);
    }

    private void RemoveWhere(Func<LiveEntry, bool> predicate)
    {
        var any = false;
        lock (sync)
        {
            foreach (var slot in slots.Values.Where(s => predicate(s.Entry)).ToList())
            {
                RemoveSlotLocked((slot.Entry.ConversationId, slot.Entry.Ord));
                removed.Add(slot.Entry);
                any = true;
            }
            if (any)
                counters.SetOverlayBytes(bytes);
        }
        if (any)
            Changed?.Invoke();
    }

    private List<LiveEntry> RemoveConversationLocked(string conversationId)
    {
        var gone = slots.Values.Where(s => s.Entry.ConversationId == conversationId).Select(s => s.Entry).ToList();
        foreach (var entry in gone)
            RemoveSlotLocked((entry.ConversationId, entry.Ord));
        marks.Remove(conversationId);
        counters.SetOverlayBytes(bytes);
        return gone;
    }

    private void RemoveSlotLocked((string ConversationId, long Ord) key)
    {
        if (slots.Remove(key, out var slot))
            bytes -= slot.Entry.Bytes;
    }

    private DurableMark? Mark(string conversationId, long ord) =>
        marks.TryGetValue(conversationId, out var perConversation) && perConversation.TryGetValue(ord, out var mark) ? mark : null;

    private void SetMarkLocked(string conversationId, long ord, DurableMark mark)
    {
        if (!marks.TryGetValue(conversationId, out var perConversation))
            marks[conversationId] = perConversation = [];
        perConversation[ord] = mark;
        if (perConversation.Count > MaxMarksPerConversation)
            perConversation.Remove(perConversation.Keys.Min());
    }
}
