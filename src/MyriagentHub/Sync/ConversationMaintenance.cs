using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Sync;

/// <summary>
/// 對話資料的背景維護（WP4.2 計畫步 5；D31、D40、答 7）：
/// <list type="bullet">
/// <item>啟動後補上營運計數的起始值：啟動前就在 sync.db 的對話事件（seq ≤ <see cref="ConversationCounters.StartSeq"/>）的筆數、位元組、每台作者的最大 seq 與最後時間，
/// 以唯讀短交易分段查詢（不佔寫入者）。之後的由 AfterCommit 累加，兩邊不重疊。</item>
/// <item>每天一次（啟動後 10 分鐘第一次）刪掉 <c>created_at</c> 早於 <c>RejectedRetentionDays</c>（30）天的對話被拒事件（<c>conversation_id</c> 不是 null）；
/// 每個寫入工作最多 <see cref="DeleteBatch"/> 列，要刪的列以唯讀連線找（不在寫入者裡掃整張表）。第 3 階段的被拒事件不動；<c>RejectedRetentionDays=0</c> 時不刪。
/// 遠大於桌面 outbox 保留的 24 小時，之後再送那些 deviceSeq 會被判成 Stale。</item>
/// <item>同一個每日工作接著刪被新版本取代的 <c>conv.item</c> 事件（2026-10-25，<see cref="CompactSupersededAsync"/>；sync.db 的成長大多是它）：
/// 一筆事件不是它那筆項目物化的最新版本（<c>seq</c> 不在那個對話的 <c>items.last_seq</c> 裡）、<c>created_at</c> 早於 <c>SupersededRetentionDays</c>（7）天、
/// 媒體參照索引已經處理過（<c>seq</c> ≤ <c>server_meta.media_index_seq</c>）就刪。物化表、Pull 與重建因此都只少了舊版本：
/// 每筆項目最新的那一筆事件一定留著（<c>ConversationReader</c> 以 <c>last_seq</c> 對 events 取收到時間、副本與重建照 seq 重播都收斂到同一份），
/// 被降級或刪除而清掉內容的對話沒有 <c>items</c> 列，它的 <c>conv.item</c> 事件也一起刪（Pull 本來就不回、重建也不放回）。
/// meta 類、<c>conv.itemRemoved</c>、<c>conv.queue</c> 與第 3 階段的事件不動；正在重建物化表的對話（<see cref="ConversationMaterializer.IsRebuilding"/>）這次略過。</item>
/// </list>
/// </summary>
internal sealed class ConversationMaintenance(SyncWriter writer, ConversationCounters counters, ConversationMaterializer materializer, IOptions<HubOptions> options,
    TimeProvider time, ILogger<ConversationMaintenance> log) : BackgroundService
{
    /// <summary>每個寫入工作最多刪幾列被拒事件</summary>
    public const int DeleteBatch = 1000;

    /// <summary>起始值的分段查詢每段最多讀幾筆事件</summary>
    public const int BaselineChunk = 10_000;

    public static readonly TimeSpan FirstPurgeDelay = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan PurgeInterval = TimeSpan.FromDays(1);

    private const long DayMs = 86_400_000;

    /// <summary>
    /// 一個對話（<c>$s</c>＝串流、<c>$c</c>＝對話）被取代、超過保留期（<c>$h</c>）、媒體索引處理過的 <c>conv.item</c> 事件；
    /// 找的唯讀查詢與寫入者裡的刪除共用同一個條件（寫入者裡重新判斷：找到之後才到的新版本、物化表的變化都以刪除當下為準）
    /// </summary>
    private const string SupersededWhere =
        "stream = $s AND kind = '" + ConversationEventKinds.Item + "' AND created_at < $h "
        + "AND seq <= IFNULL((SELECT CAST(value AS INTEGER) FROM server_meta WHERE key = '" + Media.MediaRefIndexer.CursorKey + "'), 0) "
        + "AND seq NOT IN (SELECT last_seq FROM items WHERE conversation_id = $c)";

    private readonly TaskCompletionSource baselined = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>營運計數的起始值補完了（測試用；失敗也算完成，只記警告）</summary>
    public Task Baselined => baselined.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await BaselineAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "對話事件的營運計數起始值沒有補完（ops/status 的 conversationEvents 只算啟動之後的）");
        }
        finally
        {
            baselined.TrySetResult();
        }

        try
        {
            await Task.Delay(FirstPurgeDelay, time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var deleted = await PurgeRejectedAsync(stoppingToken);
                    if (deleted.Count > 0)
                        log.LogInformation("刪掉超過保留期的對話被拒事件 {Count} 列", deleted.Sum());
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    log.LogWarning(ex, "刪除超過保留期的對話被拒事件失敗，明天再試");
                }
                try
                {
                    var compacted = await CompactSupersededAsync(stoppingToken);
                    if (compacted.Count > 0)
                        log.LogInformation("刪掉被新版本取代、超過保留期的對話項目事件 {Count} 列", compacted.Sum());
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !stoppingToken.IsCancellationRequested)
                {
                    log.LogWarning(ex, "刪除被新版本取代的對話項目事件失敗，明天再試");
                }
                await Task.Delay(PurgeInterval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 服務停止
        }
    }

    /// <summary>補上啟動前的對話事件的計數（每個類別依 seq 分段，每段一個唯讀短交易）</summary>
    internal async Task BaselineAsync(CancellationToken cancellationToken)
    {
        var start = counters.StartSeq;
        foreach (var cls in new[] { SyncClasses.Meta, SyncClasses.Item })
        {
            var after = 0L;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (rows, last, events, bytes, perDevice) = ReadBaselineChunk(cls, after, start);
                if (rows > 0)
                    counters.AddBaseline(events, bytes, perDevice.Select(kv => (kv.Key, kv.Value.MaxSeq, kv.Value.LastAt)));
                if (rows < BaselineChunk)
                    break;
                after = last;
                await Task.Yield();
            }
        }
        counters.BaselineDone();
    }

    /// <summary>
    /// 刪一輪超過保留期的對話被拒事件；回傳每個寫入工作刪了幾列（測試用）。保留天數是 0 時不刪
    /// </summary>
    internal async Task<IReadOnlyList<int>> PurgeRejectedAsync(CancellationToken cancellationToken)
    {
        var days = options.Value.Conversations.RejectedRetentionDays;
        var deleted = new List<int>();
        if (days <= 0)
            return deleted;
        var cutoff = time.GetUtcNow().ToUnixTimeMilliseconds() - days * DayMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (found, first, last) = FindExpired(cutoff);
            if (found == 0)
                break;
            // 唯讀連線找到的前 DeleteBatch 列：rowid 介於 first 與 last 之間、符合條件的就是它們（之後新增的列 created_at 是現在，不會符合）
            var n = await writer.WriteAsync(context => context.Execute(
                "DELETE FROM rejected_events WHERE rowid BETWEEN $first AND $last AND conversation_id IS NOT NULL AND created_at < $cutoff",
                ("$first", first), ("$last", last), ("$cutoff", cutoff)), cancellationToken);
            deleted.Add(n);
            if (found < DeleteBatch)
                break;
        }
        return deleted;
    }

    /// <summary>
    /// 刪一輪被新版本取代、超過保留期的 <c>conv.item</c> 事件（見類別說明）；回傳每個寫入工作刪了幾列（測試用）。保留天數是 0 時不刪。
    /// 一個對話一個對話做，每個寫入工作最多 <see cref="DeleteBatch"/> 列，要刪的列以唯讀連線找
    /// </summary>
    internal async Task<IReadOnlyList<int>> CompactSupersededAsync(CancellationToken cancellationToken)
    {
        var days = options.Value.Conversations.SupersededRetentionDays;
        var deleted = new List<int>();
        if (days <= 0)
            return deleted;
        var horizon = time.GetUtcNow().ToUnixTimeMilliseconds() - days * DayMs;
        foreach (var conversationId in ReadConversationIds())
        {
            var stream = SyncStreams.Conversation(conversationId);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (found, first, last) = FindSuperseded(stream, conversationId, horizon);
                if (found == 0)
                    break;
                // 重建中的對話 items 只放回了一部分，以它判斷會刪掉還沒重播的版本：這次略過（只在寫入執行緒上問）
                var n = await writer.WriteAsync(context => materializer.IsRebuilding(conversationId)
                    ? 0
                    : context.Execute("DELETE FROM events WHERE seq BETWEEN $first AND $last AND " + SupersededWhere,
                        ("$first", first), ("$last", last), ("$s", stream), ("$c", conversationId), ("$h", horizon)), cancellationToken);
                if (n > 0)
                    deleted.Add(n);
                if (n == 0 || found < DeleteBatch)
                    break;
            }
        }
        return deleted;
    }

    private List<string> ReadConversationIds()
    {
        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT conversation_id FROM conversations ORDER BY conversation_id";
        using var reader = cmd.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private (int Found, long First, long Last) FindSuperseded(string stream, string conversationId, long horizon)
    {
        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT seq FROM events WHERE " + SupersededWhere + " ORDER BY seq LIMIT $limit";
        cmd.Parameters.AddWithValue("$s", stream);
        cmd.Parameters.AddWithValue("$c", conversationId);
        cmd.Parameters.AddWithValue("$h", horizon);
        cmd.Parameters.AddWithValue("$limit", DeleteBatch);
        using var reader = cmd.ExecuteReader();
        var found = 0;
        long first = 0;
        long last = 0;
        while (reader.Read())
        {
            var seq = reader.GetInt64(0);
            if (found == 0)
                first = seq;
            last = seq;
            found++;
        }
        return (found, first, last);
    }

    private (int Found, long First, long Last) FindExpired(long cutoff)
    {
        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rowid FROM rejected_events WHERE conversation_id IS NOT NULL AND created_at < $cutoff ORDER BY rowid LIMIT $limit";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        cmd.Parameters.AddWithValue("$limit", DeleteBatch);
        using var reader = cmd.ExecuteReader();
        var found = 0;
        long first = 0;
        long last = 0;
        while (reader.Read())
        {
            var rowid = reader.GetInt64(0);
            if (found == 0)
                first = rowid;
            last = rowid;
            found++;
        }
        return (found, first, last);
    }

    private (int Rows, long Last, long Events, long Bytes, Dictionary<string, (long MaxSeq, long LastAt)> PerDevice) ReadBaselineChunk(string cls, long after, long start)
    {
        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT seq, author, LENGTH(CAST(payload AS BLOB)), created_at FROM events WHERE cls = $cls AND seq > $after AND seq <= $start ORDER BY seq LIMIT $limit";
        cmd.Parameters.AddWithValue("$cls", cls);
        cmd.Parameters.AddWithValue("$after", after);
        cmd.Parameters.AddWithValue("$start", start);
        cmd.Parameters.AddWithValue("$limit", BaselineChunk);
        using var reader = cmd.ExecuteReader();
        var rows = 0;
        long last = after;
        long bytes = 0;
        var perDevice = new Dictionary<string, (long MaxSeq, long LastAt)>(StringComparer.Ordinal);
        while (reader.Read())
        {
            rows++;
            last = reader.GetInt64(0);
            var author = reader.GetString(1);
            bytes += reader.GetInt64(2);
            var at = reader.GetInt64(3);
            perDevice[author] = perDevice.TryGetValue(author, out var d) ? (Math.Max(d.MaxSeq, last), Math.Max(d.LastAt, at)) : (last, at);
        }
        return (rows, last, rows, bytes, perDevice);
    }
}
