using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Sync;

/// <summary>
/// UI 端的對話發布者（WP4.3 計畫步 7，D1、D2、D6、D21、D33）：中樞的對話變化接收者，同時是引擎要 UI 做事的入口（<see cref="IConversationUiPort"/>）。
/// <list type="bullet">
/// <item>掛鉤裡只記髒（每個對話一份「項目 id → 項目的參照」，O(1)，不複製、不序列化，成本與文字長度無關）。</item>
/// <item>合併後在 UI 執行緒 flush：以注入的 <see cref="TimeProvider"/> 排一個計時器，回呼經 <see cref="IConversationPublishSource.BeginInvoke"/> 回 UI；
/// 被觀看的對話、或有要立刻持久的變化 50 ms，沒被觀看而且只有執行中項目的串流（或標頭）1 秒（<see cref="PublishTimingRules.FlushDelayMs"/>）。
/// flush 才複製（<see cref="ChatItemCopyRules"/>）放進盒子。</item>
/// <item>標記（一輪開始、結束、提醒、刪除）當下先把這個對話待送的項目放進盒子再放標記；一輪開始與結束之後接一筆中繼資料；
/// 一輪失敗另放 failed 提醒；沒有 started 的 completed 照放（D33）。刪除時丟掉這個對話沒送的變化，不論項目有沒有載入都放刪除的標記。</item>
/// <item>草稿與副本一律略過。中樞要的複本（<see cref="IReplicaRequester"/>）放進盒子，引擎交來的複本變化轉給中樞（WP4.3 計畫步 15）。</item>
/// </list>
/// 狀態只在 UI 執行緒改（引擎呼叫的 <see cref="IConversationUiPort"/> 方法一律經 BeginInvoke）；釋放之後（換了或拿掉接收者）什麼都不做。
/// 不參考 SignalR、SQLite 或引擎的實作型別（協調者在未配對的路徑上也看得到這個型別，S6）
/// </summary>
internal sealed class ConversationPublisher : IConversationChangeSink, IConversationUiPort, IReplicaRequester, IRemoteFileTransferSource, IReplicaMediaSourceProvider, IDisposable
{
    /// <summary>宣告 conv1 的必要 DLP 版本（步 4 的 F10；D38）：引擎只在 CredentialLeakRules.Version 不低於它時宣告 conv1、live1</summary>
    public const int RequiredDlpVersion = 2;

    private static readonly IReadOnlySet<string> NoneWatched = new HashSet<string>(StringComparer.Ordinal);

    private readonly IConversationPublishSource hub;
    private readonly ISyncEngine engine;
    private readonly ConversationChangeBox box;
    private readonly TimeProvider time;
    private readonly Action flushDue;

    // ── 以下只在 UI 執行緒 ──
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private IReadOnlySet<string> watched = NoneWatched;
    private ITimer? timer;
    private long? timerDueMs;
    private bool disposed;

    /// <summary>一個對話還沒交給盒子的變化</summary>
    private sealed class Pending
    {
        /// <summary>項目 id 依第一次變髒的順序</summary>
        public readonly List<string> ItemOrder = new();

        /// <summary>項目 id → 最新的參照（null＝被移除）</summary>
        public readonly Dictionary<string, AgentChatItem?> Items = new(StringComparer.Ordinal);

        public bool Meta;
        public bool Queue;

        /// <summary>待送的有要立刻持久的變化（<see cref="PublishTimingRules.FlushDelayMs"/>；false＝只有執行中項目的串流或標頭的變化，沒被觀看時可以等 1 秒）</summary>
        public bool Urgent;

        /// <summary>最晚什麼時候 flush（時鐘毫秒）</summary>
        public long DueMs = long.MaxValue;
    }

    private ConversationPublisher(IConversationPublishSource hub, ISyncEngine engine, ConversationChangeBox box, TimeProvider time)
    {
        this.hub = hub;
        this.engine = engine;
        this.box = box;
        this.time = time;
        flushDue = FlushDue;
    }

    /// <summary>
    /// 協調者建立引擎之後呼叫（UI 執行緒）：引擎支援對話發布（<see cref="IConversationEngine"/> 而且有盒子）才建立，並把自己設成盒子的
    /// <see cref="ConversationChangeBox.UiPort"/>；否則回 null（中樞不掛接收者，一切照舊）
    /// </summary>
    public static ConversationPublisher? TryCreate(IConversationPublishSource hub, ISyncEngine engine, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(time);
        if (engine is not IConversationEngine { Conversations: { } box })
            return null;
        var publisher = new ConversationPublisher(hub, engine, box, time);
        box.ConversationsRoot = hub.ConversationsRoot;
        box.UiPort = publisher;
        // WP4.7：媒體轉送問中樞的入口，與發布者同生命週期
        box.MediaScope = hub as IMediaScopeSource;
        return publisher;
    }

    /// <summary>回補的進度（UI 執行緒；給 WP4.6）。null＝沒有要回補的</summary>
    public SyncBackfillProgress? Progress { get; private set; }

    /// <summary>因磁碟保護上限暫停回補的時間（D24；UI 執行緒，給 WP4.6）</summary>
    public BackfillPause? Pause { get; private set; }

    /// <summary><see cref="Progress"/> 或 <see cref="Pause"/> 改變（UI 執行緒）</summary>
    public event Action? ProgressChanged;

    /// <summary>已釋放（換了或拿掉接收者）</summary>
    public bool IsDisposed => disposed;

    /// <summary>WP4.7：中樞的遠端檔案指令要用的傳輸（引擎設在盒子上；釋放之後是 null）</summary>
    public IRemoteFileTransfer? RemoteFileTransfer => Volatile.Read(ref disposed) ? null : box.RemoteFiles;

    /// <summary>WP4.7 步 12：複本的媒體來源（引擎設在盒子上；釋放之後是 null）</summary>
    public IReplicaMediaSource? ReplicaMediaSource => Volatile.Read(ref disposed) ? null : box.ReplicaMedia;

    // ───────────────────────────── 掛鉤（UI 執行緒；只記髒） ─────────────────────────────

    public void OnItemChanged(ConversationHookInfo conversation, AgentChatItem item)
    {
        if (Skips(conversation))
            return;
        MarkItem(conversation.Meta.Id, item.Id, item, urgent: item.Status != AgentItemStatus.Running);
    }

    public void OnItemRemoved(ConversationHookInfo conversation, AgentChatItem item)
    {
        if (Skips(conversation))
            return;
        MarkItem(conversation.Meta.Id, item.Id, null, urgent: true);
    }

    public void OnConversationSaved(ConversationHookInfo conversation, bool itemsSaved)
    {
        if (Skips(conversation))
            return;
        // 存檔帶到的中繼資料（標題、模型、同步政策…）要立刻送：政策降級在伺服器上要立即生效（D25）
        var p = Touch(conversation.Meta.Id, urgent: true);
        p.Meta = true;
    }

    public void OnConversationDeleting(ConversationHookInfo conversation)
    {
        if (disposed || conversation.IsDraft || conversation.IsReplica)
            return;
        var id = conversation.Meta.Id;
        pending.Remove(id);
        Put(new DeletingChange(id, ConversationWire.EpochOf(conversation.Meta)));
    }

    public void OnHeaderChanged(ConversationHookInfo conversation)
    {
        if (Skips(conversation))
            return;
        // 標頭常常只是狀態文字（不上線）；執行狀態另由一輪開始與結束的標記立刻帶到，CLI 自己接續的一輪（沒有 started，F6）由這裡在合併後帶到
        var p = Touch(conversation.Meta.Id, urgent: false);
        p.Meta = true;
    }

    public void OnRunningCountsChanged()
    {
        // 每個對話的執行狀態另由一輪開始與結束、標頭改變帶到（這裡不知道是哪個對話）
    }

    public void OnQueueChanged(ConversationHookInfo conversation)
    {
        if (Skips(conversation))
            return;
        var p = Touch(conversation.Meta.Id, urgent: true);
        p.Queue = true;
    }

    public void OnComposerChanged(ConversationHookInfo conversation)
    {
        // 佇列的內容改變另有 OnQueueChanged；待送的附件與處理中的提示不上線
    }

    public void OnTurnStarted(ConversationHookInfo conversation, bool startedByCli)
    {
        if (Skips(conversation))
            return;
        var id = conversation.Meta.Id;
        FlushItemsNow(id);
        Put(new TurnStartedChange(id, startedByCli));
        PutMetaNow(id);
    }

    public void OnTurnCompleted(ConversationHookInfo conversation, AgentTurnOutcome outcome, AgentUsage? usage)
    {
        if (Skips(conversation))
            return;
        var id = conversation.Meta.Id;
        FlushItemsNow(id);
        Put(new TurnCompletedChange(id, outcome, usage));
        if (outcome == AgentTurnOutcome.Failed)
            Put(new AttentionChange(id, ConversationAttentionKinds.Failed, null));
        PutMetaNow(id);
    }

    public void OnAttention(ConversationHookInfo conversation, string? kind, string? itemId, VoiceLine speech)
    {
        if (Skips(conversation))
            return;
        var id = conversation.Meta.Id;
        FlushItemsNow(id);
        Put(new AttentionChange(id, kind, itemId, speech));
    }

    private bool Skips(ConversationHookInfo conversation) => disposed || conversation.IsDraft || conversation.IsReplica;

    // ───────────────────────────── 記髒與 flush（UI 執行緒） ─────────────────────────────

    private void MarkItem(string id, string itemId, AgentChatItem? item, bool urgent)
    {
        var p = Touch(id, urgent);
        if (!p.Items.ContainsKey(itemId))
            p.ItemOrder.Add(itemId);
        p.Items[itemId] = item;
    }

    /// <summary>取（或建）這個對話的待送變化，依這筆變化重算最晚的 flush 時間並排計時器（已經排得更早就不動：最晚 flush 的時間只會提前）</summary>
    private Pending Touch(string id, bool urgent)
    {
        if (!pending.TryGetValue(id, out var p))
        {
            p = new Pending();
            pending[id] = p;
        }

        p.Urgent |= urgent;
        var now = NowMs();
        var due = now + PublishTimingRules.FlushDelayMs(watched.Contains(id), p.Urgent);
        if (due < p.DueMs)
        {
            p.DueMs = due;
            Arm(due, now);
        }

        return p;
    }

    /// <summary>計時器排到 <paramref name="due"/>（已經排得更早就不動）</summary>
    private void Arm(long due, long now)
    {
        if (timerDueMs is { } scheduled && scheduled <= due)
            return;
        timerDueMs = due;
        timer ??= time.CreateTimer(OnTimer, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        timer.Change(TimeSpan.FromMilliseconds(Math.Max(0, due - now)), Timeout.InfiniteTimeSpan);
    }

    /// <summary>計時器到期（計時器的執行緒）：回 UI 執行緒 flush</summary>
    private static void OnTimer(object? state)
    {
        var publisher = (ConversationPublisher)state!;
        if (!Volatile.Read(ref publisher.disposed))
            publisher.hub.BeginInvoke(publisher.flushDue);
    }

    /// <summary>把到期的對話交給盒子（UI 執行緒），再排下一個最早的</summary>
    private void FlushDue()
    {
        if (disposed)
            return;
        timerDueMs = null;
        var now = NowMs();
        List<string>? due = null;
        foreach (var (id, p) in pending)
        {
            if (p.DueMs <= now)
                (due ??= new List<string>()).Add(id);
        }

        if (due != null)
        {
            var metaIds = new List<string>();
            foreach (var id in due)
            {
                var p = pending[id];
                pending.Remove(id);
                PutItems(id, p);
                if (p.Queue)
                    PutQueue(id);
                if (p.Meta)
                    metaIds.Add(id);
            }

            PutMetas(metaIds);
        }

        var next = long.MaxValue;
        foreach (var p in pending.Values)
            next = Math.Min(next, p.DueMs);
        if (next == long.MaxValue)
            timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        else
            Arm(next, now);
    }

    /// <summary>標記之前：這個對話待送的項目現在就交給盒子（中繼資料與佇列照原本的時間）</summary>
    private void FlushItemsNow(string id)
    {
        if (!pending.TryGetValue(id, out var p) || p.ItemOrder.Count == 0)
            return;
        PutItems(id, p);
        p.ItemOrder.Clear();
        p.Items.Clear();
        if (!p.Meta && !p.Queue)
            pending.Remove(id);
    }

    /// <summary>標記之後的中繼資料（一輪開始、結束）：現在快照，之前記的中繼資料髒不必再送</summary>
    private void PutMetaNow(string id)
    {
        if (pending.TryGetValue(id, out var p))
        {
            p.Meta = false;
            if (p.ItemOrder.Count == 0 && !p.Queue)
                pending.Remove(id);
        }

        PutMetas([id]);
    }

    private void PutItems(string id, Pending p)
    {
        foreach (var itemId in p.ItemOrder)
        {
            Put(p.Items[itemId] is { } item
                ? new ItemChanged(id, ChatItemCopyRules.Copy(item))
                : new ItemRemovedChange(id, itemId));
        }
    }

    private void PutQueue(string id)
    {
        if (hub.SnapshotQueue(id) is { } queue)
            Put(new QueueChanged(id, queue));
    }

    /// <summary>中繼資料快照（每次最多 MetaSlice 個；找不到的對話略過）</summary>
    private void PutMetas(List<string> ids)
    {
        for (var start = 0; start < ids.Count; start += PublishTimingRules.MetaSlice)
        {
            var slice = ids.GetRange(start, Math.Min(PublishTimingRules.MetaSlice, ids.Count - start));
            foreach (var meta in hub.SnapshotMeta(slice))
            {
                if (!meta.IsDraft && !meta.IsReplica)
                    Put(new MetaChanged(meta.Meta.Id, meta));
            }
        }
    }

    /// <summary>放進盒子；盒子原本是空的就喚醒引擎</summary>
    private void Put(ConversationChange change)
    {
        if (box.Put(change))
            engine.Post(ConversationWake.Instance);
    }

    private long NowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();

    // ───────────────────────────── 引擎要 UI 做的事（引擎執行緒呼叫；一律經 BeginInvoke） ─────────────────────────────

    public void RequestMeta(IReadOnlyList<string>? ids)
    {
        if (ids == null)
        {
            hub.BeginInvoke(() =>
            {
                if (disposed)
                    return;
                var all = hub.ConversationIds();
                if (all.Count == 0)
                {
                    Put(new MetaBatch([], all, Refresh: true, Complete: true));
                    return;
                }

                QueueMetaSlices(all, all);
            });
            return;
        }

        var copy = ids.ToList();
        if (copy.Count > 0)
            QueueMetaSlices(copy, null);
    }

    /// <summary>
    /// 每 MetaSlice 個對話一次 UI 呼叫；<paramref name="allIds"/> 放在第一批（不是 null＝重整：每一批標 Refresh、最後一批標 Complete）
    /// </summary>
    private void QueueMetaSlices(IReadOnlyList<string> ids, IReadOnlyList<string>? allIds)
    {
        var refresh = allIds is not null;
        for (var start = 0; start < ids.Count; start += PublishTimingRules.MetaSlice)
        {
            var slice = ids.Skip(start).Take(PublishTimingRules.MetaSlice).ToList();
            var first = start == 0 ? allIds : null;
            var last = start + PublishTimingRules.MetaSlice >= ids.Count;
            hub.BeginInvoke(() =>
            {
                if (!disposed)
                    Put(new MetaBatch(hub.SnapshotMeta(slice), first, refresh, refresh && last, slice));
            });
        }
    }

    public void RequestItems(string conversationId, long afterOrd, int take, OrdHint? hint)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        var size = Math.Clamp(take, 1, PublishTimingRules.ItemsPage);
        hub.BeginInvoke(() =>
        {
            if (!disposed)
                Put(new ItemsPageChange(conversationId, afterOrd, hub.SnapshotItems(conversationId, afterOrd, size, hint)));
        });
    }

    public void SetWatched(IReadOnlySet<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var copy = new HashSet<string>(ids, StringComparer.Ordinal);
        hub.BeginInvoke(() =>
        {
            if (disposed)
                return;
            watched = copy;
            hub.SetWatched(copy);
            // 剛被看的對話：待送的變化改成被觀看的延遲
            var now = NowMs();
            foreach (var (id, p) in pending)
            {
                if (!copy.Contains(id))
                    continue;
                var due = now + PublishTimingRules.FlushDelayMs(watched: true, p.Urgent);
                if (due < p.DueMs)
                {
                    p.DueMs = due;
                    Arm(due, now);
                }
            }
        });
    }

    public void PauseForDlp(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        hub.BeginInvoke(() =>
        {
            if (!disposed)
                hub.PauseForDlp(conversationId);
        });
    }

    public void MarkServerDeleted(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        hub.BeginInvoke(() =>
        {
            if (!disposed)
                Put(new ServerDeletedWritten(conversationId, hub.MarkServerDeleted(conversationId, NowMs())));
        });
    }

    public void RequestQueue(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        hub.BeginInvoke(() =>
        {
            if (!disposed && hub.SnapshotQueue(conversationId) is { } queue)
                Put(new QueueChanged(conversationId, queue, Resync: true));
        });
    }

    public void Notify(PublishNoticeKind kind, int count)
    {
        hub.BeginInvoke(() =>
        {
            if (!disposed && count > 0)
                hub.ShowPublishNotice(kind, count);
        });
    }

    public void ReportProgress(SyncBackfillProgress? progress, BackfillPause? pause)
    {
        hub.BeginInvoke(() =>
        {
            if (disposed || (Equals(Progress, progress) && Equals(Pause, pause)))
                return;
            Progress = progress;
            Pause = pause;
            ProgressChanged?.Invoke();
        });
    }

    public void ApplyReplicaDelta(ReplicaDelta delta)
    {
        // UI 執行緒（拉取那一頁的 UI 套用裡、或引擎的 RunOnUiAsync）：轉給中樞；中樞的例外照常丟回引擎（那一頁不提交、游標不前進）
        ArgumentNullException.ThrowIfNull(delta);
        if (!disposed)
            hub.ApplyReplicaDelta(delta);
    }

    // ───────────────────────────── 中樞要的複本（UI 執行緒，IReplicaRequester） ─────────────────────────────

    public void RequestReplica(string? conversationId)
    {
        if (!disposed)
            Put(new ReplicaRequested(conversationId));
    }

    public void CloseReplica(string? conversationId)
    {
        if (!disposed)
            Put(new ReplicaClosed(conversationId));
    }

    // ───────────────────────────── 釋放（UI 執行緒：中樞換了或拿掉接收者時） ─────────────────────────────

    public void Dispose()
    {
        if (disposed)
            return;
        Volatile.Write(ref disposed, true);
        timer?.Dispose();
        timer = null;
        timerDueMs = null;
        pending.Clear();
        if (ReferenceEquals(box.UiPort, this))
        {
            box.UiPort = null;
            box.MediaScope = null;
        }
    }
}
