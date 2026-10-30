using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Sync;

/// <summary>
/// 對話發布者（UI 執行緒）交給引擎的一筆變化（WP4.3 計畫 D2）。分兩類：
/// <list type="bullet">
/// <item>變更類（算進 <see cref="ConversationChangeBox.MaxChanges"/>）：項目、中繼資料、佇列的快照，一輪開始與結束、提醒、刪除的標記，以及溢出時的
/// <see cref="NeedsSnapshot"/>。</item>
/// <item>快照回應（不算上限，引擎一次只要一頁）：<see cref="MetaBatch"/>、<see cref="ItemsPageChange"/>、<see cref="ServerDeletedWritten"/>。</item>
/// </list>
/// 快照的內容都是 UI 執行緒複製好的（<see cref="ChatItemCopyRules"/>），引擎只讀
/// </summary>
internal abstract record ConversationChange(string? ConversationId);

/// <summary>項目新增或改變（UI 合併後的複本）</summary>
internal sealed record ItemChanged(string ConversationId, AgentChatItem Item) : ConversationChange(ConversationId);

/// <summary>項目被移除（與 <see cref="ItemChanged"/> 共用一個槽，最新的勝）</summary>
internal sealed record ItemRemovedChange(string ConversationId, string ItemId) : ConversationChange(ConversationId);

/// <summary>中繼資料改變（存檔、標頭、一輪開始與結束之後）</summary>
internal sealed record MetaChanged(string ConversationId, PublishMetaSnapshot Snapshot) : ConversationChange(ConversationId);

/// <summary>
/// 佇列改變；<paramref name="Resync"/>＝引擎要的快照（<see cref="IConversationUiPort.RequestQueue"/>：整份重新發布、改回 full 時）。
/// 送不送一律照摘要比對：伺服器上確定沒有佇列時摘要記成空佇列的，空的不送；摘要不明時空的也送一次（第二輪合併前審查 F3）
/// </summary>
internal sealed record QueueChanged(string ConversationId, PublishQueueSnapshot Snapshot, bool Resync = false) : ConversationChange(ConversationId);

/// <summary>標記：一輪開始（CLI 自己開始的是 <paramref name="StartedByCli"/>）</summary>
internal sealed record TurnStartedChange(string ConversationId, bool StartedByCli) : ConversationChange(ConversationId);

/// <summary>標記：一輪結束（沒有 started 的 completed 照樣有，D33）</summary>
internal sealed record TurnCompletedChange(string ConversationId, AgentTurnOutcome Outcome, AgentUsage? Usage) : ConversationChange(ConversationId);

/// <summary>標記：要使用者注意（<paramref name="Kind"/> 是 ConversationAttentionKinds 的名稱，其他提醒是 null；一輪失敗時發布者另放 failed）</summary>
/// <param name="Speech">語音通知念的句子（null＝不帶，伺服器自己組只有標題的句子；<see cref="VoiceLine.Silent"/>＝不念）</param>
internal sealed record AttentionChange(string ConversationId, string? Kind, string? ItemId, VoiceLine? Speech = null) : ConversationChange(ConversationId);

/// <summary>
/// 標記：對話被刪除（不論項目有沒有載入；之後這個對話的變化都不算數）。<paramref name="Epoch"/> 是刪除當下對話的 epoch（<see cref="ConversationWire.EpochOf"/>）：
/// 沒有中繼資料快照時 conv.deleted 以它送（第 5 階段接手後 epoch 不是 1，寫死 1 會被伺服器 fenced；計畫 6、WP5.1）
/// </summary>
internal sealed record DeletingChange(string ConversationId, long Epoch = ConversationIds.DefaultEpoch) : ConversationChange(ConversationId);

/// <summary>盒子溢出：這個對話之前的變更類已丟掉，要整份重取快照（設計 2.3）</summary>
internal sealed record NeedsSnapshot(string ConversationId) : ConversationChange(ConversationId);

/// <summary>
/// 快照回應：一批中繼資料（<see cref="IConversationUiPort.RequestMeta"/>）；重整的第一批附完整的對話 id 集合，其他批是 null。
/// <paramref name="Refresh"/>＝這一批是重整（<c>RequestMeta(null)</c>）的；<paramref name="Complete"/>＝重整的最後一批（清單完整，步 12 才排內容，D24）。
/// <paramref name="Requested"/>＝這一批要的對話 id（<see cref="Metas"/> 裡沒有的＝中樞沒有它；送出的閘門以它判斷，WP4.3 第二輪合併前審查 F1）；null＝不知道
/// </summary>
internal sealed record MetaBatch(IReadOnlyList<PublishMetaSnapshot> Metas, IReadOnlyList<string>? AllIds, bool Refresh = false, bool Complete = false,
    IReadOnlyList<string>? Requested = null)
    : ConversationChange((string?)null);

/// <summary>快照回應：一頁項目（<see cref="IConversationUiPort.RequestItems"/>）；<paramref name="Page"/> 是 null＝項目沒載入、找不到、草稿或副本</summary>
internal sealed record ItemsPageChange(string ConversationId, long AfterOrd, PublishItemsPage? Page) : ConversationChange(ConversationId);

/// <summary>快照回應：伺服器刪除的旁檔寫好了沒有（<see cref="IConversationUiPort.MarkServerDeleted"/>；D25 只有 <paramref name="Ok"/> 才送 conv.deleted）</summary>
internal sealed record ServerDeletedWritten(string ConversationId, bool Ok) : ConversationChange(ConversationId);

/// <summary>
/// 中樞要其他電腦的對話（唯讀複本，WP4.3 計畫步 14、15，D26）：<paramref name="ConversationId"/> 是 null＝整份複本清單；不是 null＝打開了這個複本，
/// 要它存著的項目並開始拉它的串流。結果以 <see cref="IConversationUiPort.ApplyReplicaDelta"/> 回到中樞。和快照回應一樣不算上限
/// </summary>
internal sealed record ReplicaRequested(string? ConversationId) : ConversationChange(ConversationId);

/// <summary>
/// 中樞不再顯示這個複本（沒有任何視窗開著它；<paramref name="ConversationId"/> 是 null＝全部，例如關掉顯示）：引擎不再拉它的串流，
/// 游標與存著的項目留著（下次打開時整份交給中樞、從游標接著拉；合併前審查 R3）。和 <see cref="ReplicaRequested"/> 一樣不算上限
/// </summary>
internal sealed record ReplicaClosed(string? ConversationId) : ConversationChange(ConversationId);

/// <summary>盒子從空變成非空時喚醒引擎的訊號（經收件匣；引擎收到後取空盒子）</summary>
internal sealed record ConversationWake : SyncSignal
{
    public static ConversationWake Instance { get; } = new();
}

/// <summary>支援對話發布的引擎（協調者以 <c>ConversationPublisher.TryCreate</c> 判斷要不要掛上發布者）</summary>
internal interface IConversationEngine
{
    /// <summary>這個引擎的盒子；null＝不發布對話（測試的引擎、WP4.1 的契約測試，D3）</summary>
    ConversationChangeBox? Conversations { get; }
}

/// <summary>
/// 引擎要 UI 做的事（發布者實作；引擎執行緒呼叫，一律經 IConversationPublishSource.BeginInvoke 回 UI 執行緒做，結果以快照回應放回盒子）
/// </summary>
internal interface IConversationUiPort
{
    /// <summary>
    /// 要中繼資料：<paramref name="ids"/> 是 null＝重整，先在同一次 UI 呼叫取完所有對話 id，再每批 50 個（D12）；重整的每一批標
    /// <see cref="MetaBatch.Refresh"/>、最後一批標 <see cref="MetaBatch.Complete"/>（沒有對話時是一批空的）
    /// </summary>
    void RequestMeta(IReadOnlyList<string>? ids);

    /// <summary>要一頁項目（依 Ord；<paramref name="take"/> 最多 <see cref="PublishTimingRules.ItemsPage"/>）</summary>
    void RequestItems(string conversationId, long afterOrd, int take, OrdHint? hint);

    /// <summary>其他地方看得到的對話（整份取代；D19、D21）</summary>
    void SetWatched(IReadOnlySet<string> ids);

    /// <summary>伺服器以 dlp 擋下 item 類事件：改成 paused（D14）</summary>
    void PauseForDlp(string conversationId);

    /// <summary>寫伺服器刪除的旁檔，結果以 <see cref="ServerDeletedWritten"/> 放回盒子（D25）</summary>
    void MarkServerDeleted(string conversationId);

    /// <summary>要這個對話的佇列快照，以 <see cref="QueueChanged"/>（<c>Resync</c>）放回盒子（整份重新發布、改回 full 時重送佇列，WP4.3 合併前審查 F2、第二輪 F3）</summary>
    void RequestQueue(string conversationId);

    /// <summary>彙總提示（D14、D32）</summary>
    void Notify(PublishNoticeKind kind, int count);

    /// <summary>回補的進度與因磁碟保護上限暫停的時間（D24；WP4.6 顯示）</summary>
    void ReportProgress(SyncBackfillProgress? progress, BackfillPause? pause);

    /// <summary>
    /// 其他電腦的對話（唯讀複本，D26、D27）的變化交給中樞。<b>例外：在 UI 執行緒呼叫</b>（拉取那一頁的 UI 套用裡、或引擎經 RunOnUiAsync），
    /// 實作直接改中樞、不再排隊；丟出的例外讓那一頁的游標不前進（下次重拉）
    /// </summary>
    void ApplyReplicaDelta(ReplicaDelta delta);
}

/// <summary>
/// UI 執行緒→引擎的對話變化盒子（WP4.3 計畫 D2；有鎖，Put 在 UI 執行緒、TakeAll 在引擎執行緒）。不經 SyncInbox（它的修改類是無上限的 FIFO）：
/// <list type="bullet">
/// <item>變更類以「對話＋種類＋項目」為槽、只留最新、位置取第一次出現的；槽只在兩個標記之間有效（標記之後同一個項目另開一槽，
/// 不會把標記之後的狀態排到標記前面，D5、D6）。標記本身不合併。</item>
/// <item>變更類最多 <see cref="MaxChanges"/> 筆：滿了之後再來一筆新的（不能併進既有的槽），丟掉那個對話的變更類、改記一筆
/// <see cref="NeedsSnapshot"/>，之後那個對話的變更類一律不收，直到引擎取走（設計 2.3）。刪除的標記例外：一定收（溢出的對話各最多多一筆）。</item>
/// <item>快照回應不算上限。</item>
/// </list>
/// <see cref="Put"/> 回傳「放之前是空的」：呼叫端只在這時以 <see cref="ConversationWake"/> 喚醒引擎（多筆變化合成一次喚醒）
/// </summary>
internal sealed class ConversationChangeBox
{
    /// <summary>變更類的上限（設計 2.3 的 10,000）</summary>
    public const int MaxChanges = 10_000;

    private readonly object gate = new();

    // 依放進來的順序；被溢出丟掉的位置是 null（槽的索引不變），TakeAll 時略過
    private readonly List<Entry?> entries = new();
    private readonly Dictionary<SlotKey, int> slots = new();

    // 每個對話放過幾個標記（槽的世代：標記之後另開一槽）
    private readonly Dictionary<string, int> segments = new(StringComparer.Ordinal);
    private readonly HashSet<string> overflowed = new(StringComparer.Ordinal);
    private int counted;
    private int live;
    private IConversationUiPort? uiPort;
    private string? conversationsRoot;
    private IMediaScopeSource? mediaScope;
    private IRemoteFileTransfer? remoteFiles;
    private IReplicaMediaSource? replicaMedia;

    private readonly record struct Entry(ConversationChange Change, SlotKey? Slot, bool Counted);

    private readonly record struct SlotKey(string ConversationId, int Segment, int Kind, string? ItemId);

    private const int ItemSlot = 0;
    private const int MetaSlot = 1;
    private const int QueueSlot = 2;

    /// <summary>引擎要 UI 做事的入口（發布者掛上時設定、釋放時清掉；任何執行緒讀）</summary>
    public IConversationUiPort? UiPort
    {
        get => Volatile.Read(ref uiPort);
        set => Volatile.Write(ref uiPort, value);
    }

    /// <summary>對話資料夾的根目錄（發布者掛上時設定；引擎在執行緒集區讀未載入的對話，D10）</summary>
    public string? ConversationsRoot
    {
        get => Volatile.Read(ref conversationsRoot);
        set => Volatile.Write(ref conversationsRoot, value);
    }

    /// <summary>媒體轉送問中樞的入口（WP4.7 步 8；發布者掛上時設定、釋放時清掉；任何執行緒讀，只在 UI 執行緒呼叫它的方法）</summary>
    public IMediaScopeSource? MediaScope
    {
        get => Volatile.Read(ref mediaScope);
        set => Volatile.Write(ref mediaScope, value);
    }

    /// <summary>
    /// 中樞的遠端檔案指令要用的傳輸（WP4.7 步 11；引擎在第一次協商出 media1 時設定、停止時清掉；任何執行緒讀寫）。
    /// 發布者經 <see cref="IRemoteFileTransferSource"/> 交給中樞
    /// </summary>
    public IRemoteFileTransfer? RemoteFiles
    {
        get => Volatile.Read(ref remoteFiles);
        set => Volatile.Write(ref remoteFiles, value);
    }

    /// <summary>只在目前是 <paramref name="expected"/> 時清掉（停止的舊引擎不清新引擎設的）</summary>
    public void ClearRemoteFiles(IRemoteFileTransfer expected) => Interlocked.CompareExchange(ref remoteFiles, null, expected);

    /// <summary>
    /// 複本（其他電腦的對話）的圖片、影片與縮圖的來源（WP4.7 步 12，D38；引擎在第一次協商出 media1 時設定、停止時清掉；任何執行緒讀寫）。
    /// 發布者經 <see cref="IReplicaMediaSourceProvider"/> 交給中樞與 AgentWebView
    /// </summary>
    public IReplicaMediaSource? ReplicaMedia
    {
        get => Volatile.Read(ref replicaMedia);
        set => Volatile.Write(ref replicaMedia, value);
    }

    /// <summary>只在目前是 <paramref name="expected"/> 時清掉（停止的舊引擎不清新引擎設的）</summary>
    public void ClearReplicaMedia(IReplicaMediaSource expected) => Interlocked.CompareExchange(ref replicaMedia, null, expected);

    /// <summary>盒子裡的筆數（兩類合計）</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return live;
        }
    }

    /// <summary>變更類的筆數（算上限的）</summary>
    public int ChangeCount
    {
        get
        {
            lock (gate)
                return counted;
        }
    }

    /// <summary>放進一筆；回傳放之前盒子是不是空的（是的話呼叫端要喚醒引擎）</summary>
    public bool Put(ConversationChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (gate)
        {
            var wasEmpty = live == 0;
            if (!IsCounted(change))
            {
                Append(change, null, counted: false);
                return wasEmpty;
            }

            var id = change.ConversationId ?? throw new ArgumentException("變更類一定有對話 id", nameof(change));
            if (overflowed.Contains(id))
            {
                // 溢出過：之後這個對話要整份重取快照，變更類都不必留；刪除例外（之後沒有快照可取）
                if (change is DeletingChange)
                    Append(change, null, counted: true);
                return wasEmpty;
            }

            var segment = segments.GetValueOrDefault(id);
            var slot = SlotOf(change, id, segment);
            if (slot is { } key && slots.TryGetValue(key, out var index))
            {
                entries[index] = new Entry(change, key, Counted: true);
                return wasEmpty;
            }

            if (counted >= MaxChanges && change is not DeletingChange)
            {
                Overflow(id);
                return wasEmpty;
            }

            Append(change, slot, counted: true);
            if (IsMarker(change))
                segments[id] = segment + 1;
            return wasEmpty;
        }
    }

    /// <summary>一次取完（放進來的順序；引擎執行緒）</summary>
    public IReadOnlyList<ConversationChange> TakeAll()
    {
        lock (gate)
        {
            var all = new List<ConversationChange>(live);
            foreach (var entry in entries)
            {
                if (entry is { } e)
                    all.Add(e.Change);
            }

            entries.Clear();
            slots.Clear();
            segments.Clear();
            overflowed.Clear();
            counted = 0;
            live = 0;
            return all;
        }
    }

    private void Append(ConversationChange change, SlotKey? slot, bool counted)
    {
        if (slot is { } key)
            slots[key] = entries.Count;
        entries.Add(new Entry(change, slot, counted));
        live++;
        if (counted)
            this.counted++;
    }

    /// <summary>滿了：丟掉這個對話的變更類（刪除的標記除外），改記一筆 NeedsSnapshot</summary>
    private void Overflow(string id)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is not { Counted: true } e || !string.Equals(e.Change.ConversationId, id, StringComparison.Ordinal) || e.Change is DeletingChange)
                continue;
            if (e.Slot is { } key)
                slots.Remove(key);
            entries[i] = null;
            live--;
            counted--;
        }

        overflowed.Add(id);
        Append(new NeedsSnapshot(id), null, counted: true);
    }

    private static bool IsCounted(ConversationChange change) => change is not (MetaBatch or ItemsPageChange or ServerDeletedWritten or ReplicaRequested or ReplicaClosed);

    private static bool IsMarker(ConversationChange change) => change is TurnStartedChange or TurnCompletedChange or AttentionChange or DeletingChange;

    private static SlotKey? SlotOf(ConversationChange change, string id, int segment) => change switch
    {
        ItemChanged c => new SlotKey(id, segment, ItemSlot, c.Item.Id),
        ItemRemovedChange c => new SlotKey(id, segment, ItemSlot, c.ItemId),
        MetaChanged => new SlotKey(id, segment, MetaSlot, null),
        QueueChanged => new SlotKey(id, segment, QueueSlot, null),
        _ => null,
    };
}
