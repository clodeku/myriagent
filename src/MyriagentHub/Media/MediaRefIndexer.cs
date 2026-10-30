using System.Globalization;
using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Media;

/// <summary>
/// 媒體參照的索引（WP4.7 計畫 D6、D8）：從 <c>events</c> 依 <c>seq &gt; 游標</c> 增量維護 <c>media_refs</c>（項目的附件與代理產出媒體）
/// 與項目欄位 blob 的 <c>blob_refs(sha, 'item', '{conv}:{itemId}')</c>。契約裁定是「從項目來、不在渲染時補」：渲染器只照參照組網址，
/// 媒體端點每次請求都查這張表（<see cref="MediaLookup"/>）。
/// <list type="bullet">
/// <item>游標存在 <c>server_meta</c> 的 <see cref="CursorKey"/>（和那一頁的寫入同一個交易），另記 <see cref="InstanceKey"/>（伺服器實例＋還原世代）：
/// 不同（還原、換資料庫）或沒有游標（第一次部署）就從物化的 <c>items</c> 分頁重建。</item>
/// <item>以 <see cref="ConversationChangeFeed.Posted"/> 喚醒（只喚醒，不呼叫 <c>TryDrain</c>：閘道是唯一的消費者），另外每 30 秒輪詢。</item>
/// <item>讀用唯讀短連線（一頁一個讀取交易），寫經 <see cref="SyncWriter"/> 一頁一個交易；冪等（重做一頁結果相同）。</item>
/// <item>在 <c>ApplicationStarted</c> 之後才開始：重建不擋部署的 60 秒健康閘門。</item>
/// <item>刪參照時一併移除縮圖的引用（<c>blob_refs(thumb_blob, 'thumb', key)</c>）、更新 <c>blobs.last_ref_at</c>（D41：只在引用移除時寫），
/// 提交之後刪 <c>cache\thumbs\</c> 的檔。blob 實體由每日維護依保留窗刪（D8、D19）。</item>
/// </list>
/// </summary>
internal sealed class MediaRefIndexer : BackgroundService
{
    /// <summary><c>server_meta</c>：已處理到的 <c>events.seq</c></summary>
    public const string CursorKey = "media_index_seq";

    /// <summary><c>server_meta</c>：游標屬於哪個資料庫（<c>{instanceId}/{restoreGeneration}</c>）</summary>
    public const string InstanceKey = "media_index_instance";

    /// <summary>一頁最多幾筆事件或項目（寫入一頁一個交易）</summary>
    public const int PageSize = 200;

    /// <summary>一頁 payload 合計的上限（字數；事件的 payload 是 gzip＋base64 時遠小於原文，原文每筆 ≤128 KB）</summary>
    public const int PageChars = 4 * 1024 * 1024;

    /// <summary>重建與對帳一次處理幾列 <c>media_refs</c>／<c>blob_refs</c></summary>
    public const int ReconcileBatch = 500;

    /// <summary>沒有喚醒時的輪詢間隔</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private static readonly string[] Kinds =
    [
        ConversationEventKinds.Created, ConversationEventKinds.Meta, ConversationEventKinds.Deleted, ConversationEventKinds.Item, ConversationEventKinds.ItemRemoved,
    ];

    private readonly SyncWriter writer;
    private readonly BlobStore blobs;
    private readonly ThumbCache thumbs;
    private readonly IOptions<HubOptions> options;
    private readonly IHostApplicationLifetime lifetime;
    private readonly TimeProvider time;
    private readonly ILogger<MediaRefIndexer> log;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource progress = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long cursor = -1;
    private long processed;
    private int rebuilding;
    private int rebuilds;
    private int reconcileRequested;
    private int reconciles;

    public MediaRefIndexer(SyncWriter writer, BlobStore blobs, ThumbCache thumbs, ConversationChangeFeed feed, IOptions<HubOptions> options,
        IHostApplicationLifetime lifetime, TimeProvider time, ILogger<MediaRefIndexer> log)
    {
        this.writer = writer;
        this.blobs = blobs;
        this.thumbs = thumbs;
        this.options = options;
        this.lifetime = lifetime;
        this.time = time;
        this.log = log;
        // 在寫入執行緒上觸發：只喚醒
        feed.Posted += () => Volatile.Read(ref wake).TrySetResult();
    }

    /// <summary>啟動後第一輪（重建或接著游標追到最新）做完了（失敗也算，之後照常重試）</summary>
    public Task Ready => ready.Task;

    /// <summary>已處理到的 seq（還沒載入是 -1）</summary>
    public long Cursor => Interlocked.Read(ref cursor);

    /// <summary>正在從 <c>items</c> 重建</summary>
    public bool IsRebuilding => Volatile.Read(ref rebuilding) == 1;

    /// <summary>這次啟動以來增量處理的對話事件數（建立、meta、刪除、項目、移除；探針與測試用）</summary>
    public long Processed => Interlocked.Read(ref processed);

    /// <summary>這次啟動以來做完的重建次數（探針與測試用）</summary>
    public int Rebuilds => Volatile.Read(ref rebuilds);

    /// <summary>這次啟動以來因 <see cref="RequestReconcile"/> 做完的對帳次數（測試用）</summary>
    public int Reconciles => Volatile.Read(ref reconciles);

    /// <summary>
    /// 移除電腦之後（設計 14.22）：伺服器刪的對話沒有 conv.deleted 事件、增量看不到，請索引以重建後的同一套對帳刪掉它們的參照與項目的 blob 引用
    /// （在索引自己的迴圈上做；失敗下一輪再試）
    /// </summary>
    public void RequestReconcile()
    {
        Interlocked.Exchange(ref reconcileRequested, 1);
        Volatile.Read(ref wake).TrySetResult();
    }

    /// <summary>測試注入點：重建每一頁寫完之後呼叫（參數是第幾頁，從 1 起）</summary>
    internal Func<int, CancellationToken, Task>? RebuildPageHookForTest { get; set; }

    /// <summary>測試注入點：增量的每一頁寫入之前呼叫（模擬索引落後）</summary>
    internal Func<CancellationToken, Task>? DrainPageHookForTest { get; set; }

    /// <summary>
    /// 等索引追上呼叫當下最新的 seq（最多 <paramref name="timeout"/>）：查不到參照時先等它（D7、R2）。追上回 true；逾時、重建中沒做完回 false
    /// </summary>
    public async Task<bool> CatchUpAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        long target;
        try
        {
            target = ReadMaxSeq();
        }
        catch (SqliteException ex)
        {
            log.LogWarning(ex, "讀不到最新的事件序號，不等媒體參照的索引");
            return false;
        }

        if (Caught(target))
            return true;
        Volatile.Read(ref wake).TrySetResult();
        using var deadline = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        while (!Caught(target))
        {
            var pulse = Volatile.Read(ref progress);
            if (Caught(target))
                break;
            try
            {
                await pulse.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await WaitForStartAsync(stoppingToken))
            return;

        var initialized = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var signal = ResetWake();
            try
            {
                if (!initialized)
                {
                    await InitializeAsync(stoppingToken);
                    initialized = true;
                }

                await DrainAsync(stoppingToken);
                await ReconcileIfRequestedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (!writer.IsRunning)
            {
                log.LogDebug(ex, "sync.db 已關閉，媒體參照的索引停止");
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "媒體參照的索引這一輪失敗（游標 {Cursor}），稍後再試", Cursor);
            }
            finally
            {
                ready.TrySetResult();
            }

            try
            {
                await Task.WhenAny(signal.Task, Task.Delay(PollInterval, time, stoppingToken));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>讀游標；沒有游標或資料庫換了（還原世代、實例）就重建</summary>
    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var expected = InstanceText(writer.Instance);
        string? storedCursor;
        string? storedInstance;
        using (var connection = writer.OpenRead())
        {
            storedCursor = Meta(connection, CursorKey);
            storedInstance = Meta(connection, InstanceKey);
        }

        if (storedCursor is not null && storedInstance == expected
            && long.TryParse(storedCursor, NumberStyles.None, CultureInfo.InvariantCulture, out var stored))
        {
            Interlocked.Exchange(ref cursor, stored);
            Pulse();
            return;
        }

        if (storedCursor is null)
            log.LogInformation("媒體參照的索引第一次建立，從 items 重建");
        else
            log.LogInformation("資料庫換了（{Stored} → {Expected}），媒體參照的索引從 items 重建", storedInstance, expected);
        await RebuildAsync(expected, cancellationToken);
    }

    /// <summary>增量：一頁一頁處理 seq &gt; 游標的對話事件，直到追上</summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var from = Cursor;
            var (events, next) = ReadEvents(from);
            if (events.Count == 0 && next == from)
                return;

            var ops = Plan(events.Select(e => (e.ConversationId, e.Author, e.Kind, e.Enc, e.Payload, e.CreatedAt)).ToList());
            if (DrainPageHookForTest is { } hook)
                await hook(cancellationToken);
            var dropped = await writer.WriteAsync(c =>
            {
                var keys = Apply(c, ops);
                SetMeta(c, CursorKey, next.ToString(CultureInfo.InvariantCulture));
                return keys;
            }, CancellationToken.None);
            DropThumbCache(dropped);
            Interlocked.Add(ref processed, events.Count);
            Interlocked.Exchange(ref cursor, next);
            Pulse();
        }
    }

    /// <summary>
    /// 從物化的 <c>items</c>（完整同步、沒刪除的對話裡還在的項目）分頁重建，再對帳拿掉不該在的參照與引用，最後把游標設成開始時的最新 seq
    /// （之後的事件照增量重做，冪等）
    /// </summary>
    private async Task RebuildAsync(string instance, CancellationToken cancellationToken)
    {
        Volatile.Write(ref rebuilding, 1);
        try
        {
            var start = ReadMaxSeq();
            var after = (Conversation: string.Empty, Item: string.Empty);
            var page = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (items, more) = ReadItems(after);
                if (items.Count == 0)
                    break;
                var ops = Plan(items.Select(i => (i.ConversationId, i.Author, ConversationEventKinds.Item, i.Enc, i.Body, i.CreatedAt)).ToList());
                var dropped = await writer.WriteAsync(c => Apply(c, ops), CancellationToken.None);
                DropThumbCache(dropped);
                after = (items[^1].ConversationId, items[^1].ItemId);
                page++;
                if (RebuildPageHookForTest is { } hook)
                    await hook(page, cancellationToken);
                if (!more)
                    break;
            }

            await ReconcileRefsAsync(cancellationToken);
            await ReconcileItemBlobRefsAsync(cancellationToken);
            await writer.WriteAsync(c =>
            {
                SetMeta(c, CursorKey, start.ToString(CultureInfo.InvariantCulture));
                SetMeta(c, InstanceKey, instance);
            }, CancellationToken.None);
            Interlocked.Exchange(ref cursor, start);
            Interlocked.Increment(ref rebuilds);
            log.LogInformation("媒體參照的索引重建完成：{Pages} 頁、游標 {Cursor}", page, start);
        }
        finally
        {
            Volatile.Write(ref rebuilding, 0);
            Pulse();
        }
    }

    /// <summary><see cref="RequestReconcile"/> 之後的對帳（失敗時放回旗標，下一輪再做）</summary>
    private async Task ReconcileIfRequestedAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref reconcileRequested, 0) == 0)
            return;
        try
        {
            await ReconcileRefsAsync(cancellationToken);
            await ReconcileItemBlobRefsAsync(cancellationToken);
        }
        catch
        {
            Interlocked.Exchange(ref reconcileRequested, 1);
            throw;
        }

        Interlocked.Increment(ref reconciles);
        Pulse();
    }

    /// <summary>重建之後：對話看不到了（刪除、不是 full）的參照，以及項目已不在的 <c>item:*</c> 參照，一批一批刪</summary>
    private async Task ReconcileRefsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stale = new List<(string Device, string Ref, string? Thumb)>();
            using (var connection = writer.OpenRead())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT m.device_id, m.ref, m.thumb_blob FROM media_refs m "
                    + "WHERE NOT EXISTS (SELECT 1 FROM conversations c WHERE c.conversation_id = m.conversation_id AND c.deleted_at IS NULL AND c.sync_policy = 'full') "
                    + "OR (m.purpose LIKE 'item:%' AND NOT EXISTS (SELECT 1 FROM items i WHERE i.conversation_id = m.conversation_id "
                    + "AND i.item_id = substr(m.purpose, 6) AND i.removed = 0)) LIMIT $limit";
                cmd.Parameters.AddWithValue("$limit", ReconcileBatch);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    stale.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }

            if (stale.Count == 0)
                return;
            var dropped = await writer.WriteAsync(c =>
            {
                var keys = new List<string>();
                foreach (var (device, mediaRef, thumb) in stale)
                    DropRef(c, device, mediaRef, thumb, keys);
                return keys;
            }, CancellationToken.None);
            DropThumbCache(dropped);
            if (stale.Count < ReconcileBatch)
                return;
        }
    }

    /// <summary>重建之後：項目已不在（或對話看不到了）的項目欄位 blob 引用，一批一批刪</summary>
    private async Task ReconcileItemBlobRefsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stale = new List<(string Sha, string RefId)>();
            using (var connection = writer.OpenRead())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT r.sha256, r.ref_id FROM blob_refs r WHERE r.ref_kind = $kind AND NOT EXISTS (SELECT 1 FROM items i "
                    + "JOIN conversations c ON c.conversation_id = i.conversation_id WHERE i.conversation_id = substr(r.ref_id, 1, 32) "
                    + "AND i.item_id = substr(r.ref_id, 34) AND i.removed = 0 AND c.deleted_at IS NULL AND c.sync_policy = 'full') LIMIT $limit";
                cmd.Parameters.AddWithValue("$kind", BlobRefKinds.Item);
                cmd.Parameters.AddWithValue("$limit", ReconcileBatch);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    stale.Add((reader.GetString(0), reader.GetString(1)));
            }

            if (stale.Count == 0)
                return;
            await writer.WriteAsync(c =>
            {
                foreach (var (sha, refId) in stale)
                    RemoveItemBlobRef(c, sha, refId);
            }, CancellationToken.None);
            if (stale.Count < ReconcileBatch)
                return;
        }
    }

    /// <summary>
    /// 一頁事件（或重建的項目）→ 要寫的動作。在索引的執行緒上做：解碼 payload、讀附件或媒體欄位的 blob、算出項目欄位 blob 引用的增減
    /// （這一頁碰到的對話的現有引用先一次讀出來，之後在記憶體依序更新：索引是項目引用唯一的寫入者）
    /// </summary>
    private List<IndexOp> Plan(IReadOnlyList<(string ConversationId, string Author, string Kind, string Enc, string Payload, long CreatedAt)> entries)
    {
        var ops = new List<IndexOp>();
        if (entries.Count == 0)
            return ops;
        var itemBlobs = ReadItemBlobRefs(entries.Select(e => e.ConversationId).Distinct(StringComparer.Ordinal).ToList());
        foreach (var (conversationId, author, kind, enc, payload, createdAt) in entries)
        {
            JObject? json = null;
            if (kind is not ConversationEventKinds.Deleted)
            {
                json = Decode(enc, payload);
                if (json is null)
                {
                    log.LogWarning("媒體參照的索引略過一筆解不開的 {Kind}（對話 {Conversation}）", kind, conversationId);
                    continue;
                }
            }

            var action = MediaRefIndexRules.ForEvent(kind, json?["syncPolicy"] is JValue { Type: JTokenType.String, Value: string policy } ? policy : null);
            switch (action)
            {
                case MediaIndexAction.Upsert:
                {
                    if (MediaRefIndexRules.ItemId(json!) is not { } itemId)
                        continue;
                    var rows = MediaRefIndexRules.FromItemPayload(author, conversationId, json!, createdAt, ReadSmallBlob);
                    var refId = MediaRefIndexRules.ItemBlobRefId(conversationId, itemId);
                    var now = MediaRefIndexRules.BlobShas(json!).ToHashSet(StringComparer.Ordinal);
                    var before = itemBlobs.GetValueOrDefault(refId) ?? [];
                    itemBlobs[refId] = now;
                    ops.Add(new UpsertOp(conversationId, itemId, author, rows, before.Except(now).ToList(), now.Except(before).ToList()));
                    break;
                }
                case MediaIndexAction.RemoveItem:
                {
                    if (MediaRefIndexRules.ItemId(json!) is not { } itemId)
                        continue;
                    var refId = MediaRefIndexRules.ItemBlobRefId(conversationId, itemId);
                    var before = itemBlobs.GetValueOrDefault(refId) ?? [];
                    itemBlobs.Remove(refId);
                    ops.Add(new RemoveItemOp(conversationId, itemId, before.Select(sha => (sha, refId)).ToList()));
                    break;
                }
                case MediaIndexAction.PurgeConversation:
                {
                    var prefix = conversationId + MediaRefIndexRules.ItemRefSeparator;
                    var removed = new List<(string Sha, string RefId)>();
                    foreach (var refId in itemBlobs.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                    {
                        removed.AddRange(itemBlobs[refId].Select(sha => (sha, refId)));
                        itemBlobs.Remove(refId);
                    }

                    ops.Add(new PurgeOp(conversationId, removed));
                    break;
                }
                case MediaIndexAction.Ignore:
                    break;
                default:
                    throw new InvalidOperationException("不認得的索引動作：" + action);
            }
        }

        return ops;
    }

    /// <summary>寫入者的交易裡套用一頁的動作；回傳被刪的參照的鍵（提交之後刪縮圖快取）</summary>
    private static List<string> Apply(SyncWriteContext c, List<IndexOp> ops)
    {
        var dropped = new List<string>();
        foreach (var op in ops)
        {
            switch (op)
            {
                case UpsertOp upsert:
                {
                    var purpose = MediaRefIndexRules.ItemPurpose(upsert.ItemId);
                    var keep = upsert.Rows.Select(r => (r.DeviceId, r.Ref)).ToHashSet();
                    foreach (var (device, mediaRef, thumb) in RefsOf(c, upsert.ConversationId, purpose))
                    {
                        if (!keep.Contains((device, mediaRef)))
                            DropRef(c, device, mediaRef, thumb, dropped);
                    }

                    foreach (var row in upsert.Rows)
                    {
                        c.Execute(
                            "INSERT INTO media_refs(device_id, ref, conversation_id, purpose, thumb_blob, created_at, last_used_at) VALUES($d, $r, $c, $p, NULL, $at, $at) "
                            + "ON CONFLICT(device_id, ref) DO UPDATE SET conversation_id = excluded.conversation_id, purpose = excluded.purpose "
                            + "WHERE media_refs.conversation_id <> excluded.conversation_id OR media_refs.purpose <> excluded.purpose",
                            ("$d", row.DeviceId), ("$r", row.Ref), ("$c", row.ConversationId), ("$p", row.Purpose), ("$at", row.CreatedAt));
                    }

                    var refId = MediaRefIndexRules.ItemBlobRefId(upsert.ConversationId, upsert.ItemId);
                    foreach (var sha in upsert.RemoveBlobs)
                        RemoveItemBlobRef(c, sha, refId);
                    foreach (var sha in upsert.AddBlobs)
                    {
                        c.Execute("INSERT OR IGNORE INTO blob_refs(sha256, ref_kind, ref_id) VALUES($s, $k, $id)",
                            ("$s", sha), ("$k", BlobRefKinds.Item), ("$id", refId));
                    }

                    break;
                }
                case RemoveItemOp remove:
                    foreach (var (device, mediaRef, thumb) in RefsOf(c, remove.ConversationId, MediaRefIndexRules.ItemPurpose(remove.ItemId)))
                        DropRef(c, device, mediaRef, thumb, dropped);
                    foreach (var (sha, refId) in remove.RemoveBlobs)
                        RemoveItemBlobRef(c, sha, refId);
                    break;
                case PurgeOp purge:
                    foreach (var (device, mediaRef, thumb) in RefsOf(c, purge.ConversationId, purpose: null))
                        DropRef(c, device, mediaRef, thumb, dropped);
                    foreach (var (sha, refId) in purge.RemoveBlobs)
                        RemoveItemBlobRef(c, sha, refId);
                    break;
                default:
                    throw new InvalidOperationException("不認得的索引動作：" + op.GetType().Name);
            }
        }

        return dropped;
    }

    /// <summary>對話（<paramref name="purpose"/> 是 null＝全部用途）的參照</summary>
    private static List<(string Device, string Ref, string? Thumb)> RefsOf(SyncWriteContext c, string conversationId, string? purpose)
    {
        using var cmd = purpose is null
            ? c.Command("SELECT device_id, ref, thumb_blob FROM media_refs WHERE conversation_id = $c", ("$c", conversationId))
            : c.Command("SELECT device_id, ref, thumb_blob FROM media_refs WHERE conversation_id = $c AND purpose = $p", ("$c", conversationId), ("$p", purpose));
        using var reader = cmd.ExecuteReader();
        var refs = new List<(string, string, string?)>();
        while (reader.Read())
            refs.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        return refs;
    }

    /// <summary>刪一個參照：列、縮圖的引用（並更新那個 blob 的 <c>last_ref_at</c>）；鍵記進 <paramref name="dropped"/></summary>
    private static void DropRef(SyncWriteContext c, string device, string mediaRef, string? thumb, List<string> dropped)
    {
        c.Execute("DELETE FROM media_refs WHERE device_id = $d AND ref = $r", ("$d", device), ("$r", mediaRef));
        if (!SyncIds.IsDeviceId(device) || !ConversationEventRules.IsMediaRef(mediaRef))
            return;
        var key = MediaKeyRules.Format(device, mediaRef);
        if (thumb is not null)
        {
            if (c.Execute("DELETE FROM blob_refs WHERE sha256 = $s AND ref_kind = $k AND ref_id = $id", ("$s", thumb), ("$k", BlobRefKinds.Thumb), ("$id", key)) > 0)
                c.Execute("UPDATE blobs SET last_ref_at = $now WHERE sha256 = $s", ("$now", c.Now), ("$s", thumb));
        }

        dropped.Add(key);
    }

    private static void RemoveItemBlobRef(SyncWriteContext c, string sha, string refId)
    {
        if (c.Execute("DELETE FROM blob_refs WHERE sha256 = $s AND ref_kind = $k AND ref_id = $id", ("$s", sha), ("$k", BlobRefKinds.Item), ("$id", refId)) > 0)
            c.Execute("UPDATE blobs SET last_ref_at = $now WHERE sha256 = $s", ("$now", c.Now), ("$s", sha));
    }

    private void DropThumbCache(List<string> keys)
    {
        foreach (var key in keys)
            thumbs.Delete(key);
    }

    /// <summary>
    /// 一頁對話事件（seq &gt; <paramref name="after"/>）與下一個游標：同一個讀取交易裡先取最新的 seq，事件只取到它為止；
    /// 頁滿（則數或位元組）時游標是最後一筆的 seq，否則是那個最新的 seq（中間不相干的事件一併跳過）
    /// </summary>
    private (List<EventRow> Events, long Next) ReadEvents(long after)
    {
        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        long max;
        using (var top = connection.CreateCommand())
        {
            top.Transaction = tx;
            top.CommandText = "SELECT IFNULL(MAX(seq), 0) FROM events";
            max = (long)top.ExecuteScalar()!;
        }

        var events = new List<EventRow>();
        if (max <= after)
            return (events, Math.Max(after, 0));
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT seq, conversation_id, author, kind, payload, enc, created_at FROM events "
                          + "WHERE seq > $after AND seq <= $max AND conversation_id IS NOT NULL AND kind IN ($k0, $k1, $k2, $k3, $k4) ORDER BY seq LIMIT $limit";
        cmd.Parameters.AddWithValue("$after", after);
        cmd.Parameters.AddWithValue("$max", max);
        for (var i = 0; i < Kinds.Length; i++)
            cmd.Parameters.AddWithValue("$k" + i.ToString(CultureInfo.InvariantCulture), Kinds[i]);
        cmd.Parameters.AddWithValue("$limit", PageSize);
        using var reader = cmd.ExecuteReader();
        long chars = 0;
        while (reader.Read())
        {
            var row = new EventRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt64(6));
            events.Add(row);
            chars += row.Payload.Length;
            if (chars >= PageChars)
                return (events, row.Seq);
        }

        return (events, events.Count == PageSize ? events[^1].Seq : max);
    }

    /// <summary>重建的一頁項目（依對話、項目 id 排序，接在 <paramref name="after"/> 之後）；<c>More</c>＝頁滿（則數或位元組），還有下一頁</summary>
    private (List<ItemRow> Items, bool More) ReadItems((string Conversation, string Item) after)
    {
        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT i.conversation_id, i.item_id, i.origin_device_id, i.body, i.enc, IFNULL(e.created_at, $now) FROM items i "
            + "JOIN conversations c ON c.conversation_id = i.conversation_id LEFT JOIN events e ON e.seq = i.last_seq "
            + "WHERE i.removed = 0 AND c.deleted_at IS NULL AND c.sync_policy = 'full' AND (i.conversation_id, i.item_id) > ($c, $i) "
            + "ORDER BY i.conversation_id, i.item_id LIMIT $limit";
        cmd.Parameters.AddWithValue("$now", time.GetUtcNow().ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$c", after.Conversation);
        cmd.Parameters.AddWithValue("$i", after.Item);
        cmd.Parameters.AddWithValue("$limit", PageSize);
        using var reader = cmd.ExecuteReader();
        var items = new List<ItemRow>();
        long chars = 0;
        while (reader.Read())
        {
            var row = new ItemRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5));
            items.Add(row);
            chars += row.Body.Length;
            if (chars >= PageChars)
                return (items, true);
        }

        return (items, items.Count == PageSize);
    }

    /// <summary>這些對話現有的項目欄位 blob 引用：<c>{conv}:{itemId}</c> → SHA-256 的集合（唯讀；<c>blob_refs</c> 沒有 ref_id 的索引，一頁只掃一次）</summary>
    private Dictionary<string, HashSet<string>> ReadItemBlobRefs(IReadOnlyList<string> conversationIds)
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (conversationIds.Count == 0)
            return map;
        var wanted = conversationIds.ToHashSet(StringComparer.Ordinal);
        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sha256, ref_id FROM blob_refs WHERE ref_kind = $k";
        cmd.Parameters.AddWithValue("$k", BlobRefKinds.Item);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var refId = reader.GetString(1);
            if (MediaRefIndexRules.ConversationOfItemBlobRef(refId) is not { } conversation || !wanted.Contains(conversation))
                continue;
            if (!map.TryGetValue(refId, out var set))
                map[refId] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(reader.GetString(0));
        }

        return map;
    }

    /// <summary>附件或媒體欄位的 blob（≤ <see cref="MediaOptions.MaxItemBlobBytes"/>）；讀不到、太大回 null</summary>
    private byte[]? ReadSmallBlob(ConversationBlobRef blobRef)
    {
        if (blobRef.Length > options.Value.Media.MaxItemBlobBytes)
            return null;
        try
        {
            using var stream = blobs.OpenRead(blobRef.Blob);
            if (stream is null || stream.Length != blobRef.Length)
                return null;
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return data;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "媒體參照的索引讀不到 blob：{Sha}", blobRef.Blob);
            return null;
        }
    }

    private static JObject? Decode(string enc, string payload)
    {
        if (!PayloadRules.TryDecode(enc, payload, ConversationEventRules.MaxPayloadBytes, out var json))
            return null;
        return ConversationEventRules.TryParseStrict(json, out var token) ? token as JObject : null;
    }

    private long ReadMaxSeq()
    {
        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT IFNULL(MAX(seq), 0) FROM events";
        return (long)cmd.ExecuteScalar()!;
    }

    private bool Caught(long target) => !IsRebuilding && Cursor >= target;

    private TaskCompletionSource ResetWake()
    {
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref wake, fresh);
        return fresh;
    }

    private void Pulse() => Interlocked.Exchange(ref progress, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private async Task<bool> WaitForStartAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var cancel = stoppingToken.Register(() => started.TrySetCanceled());
        try
        {
            await started.Task;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string InstanceText(ServerInstance instance) =>
        instance.InstanceId + "/" + instance.RestoreGeneration.ToString(CultureInfo.InvariantCulture);

    private static string? Meta(SqliteConnection connection, string key)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM server_meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(SyncWriteContext c, string key, string value) =>
        c.Execute("INSERT INTO server_meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

    private sealed record EventRow(long Seq, string ConversationId, string Author, string Kind, string Payload, string Enc, long CreatedAt);

    private sealed record ItemRow(string ConversationId, string ItemId, string Author, string Body, string Enc, long CreatedAt);

    private abstract record IndexOp;

    /// <param name="RemoveBlobs">這一筆不再引用的項目欄位 blob</param>
    /// <param name="AddBlobs">這一筆新引用的項目欄位 blob</param>
    private sealed record UpsertOp(string ConversationId, string ItemId, string DeviceId, IReadOnlyList<MediaRefRow> Rows,
        IReadOnlyList<string> RemoveBlobs, IReadOnlyList<string> AddBlobs) : IndexOp;

    private sealed record RemoveItemOp(string ConversationId, string ItemId, IReadOnlyList<(string Sha, string RefId)> RemoveBlobs) : IndexOp;

    private sealed record PurgeOp(string ConversationId, IReadOnlyList<(string Sha, string RefId)> RemoveBlobs) : IndexOp;
}
