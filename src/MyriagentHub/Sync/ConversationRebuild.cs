using System.Globalization;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Sync;

/// <summary>重建的測試注入點（正式登記的是空的；測試以 services 換掉）</summary>
internal sealed class ConversationRebuildHooks
{
    /// <summary>每一塊提交之後（不在寫入執行緒上）：對話 id 與這一塊的結果</summary>
    public Func<string, RebuildStep, Task>? AfterChunk { get; set; }
}

/// <summary>
/// 物化表的背景重建（WP4.2 計畫步 5；D14）：啟動時（<see cref="Check"/>）讀 <c>server_meta</c> 的 <see cref="ConversationMaterializer.VersionKey"/>
/// 與有沒有對話的資料（都是 O(1)），重建本身在背景，不擋健康閘門。存的值和 <see cref="ConversationMaterializer.Version"/> 不相等（不論哪邊比較大）而且有對話的資料時，依對話逐一分塊重建
/// （<see cref="ConversationMaterializer.RebuildStart"/>、<see cref="ConversationMaterializer.RebuildNext"/>，每塊一個寫入工作），全部做完才寫版本鍵；
/// 沒有資料時只寫版本鍵。任何一塊失敗就放棄進行中的重建（對話都標回「等著重建」）、一分鐘後從頭再做；中途停止服務，下次啟動從頭再做（冪等）。
/// </summary>
internal sealed class ConversationRebuild(SyncWriter writer, ConversationMaterializer materializer, ConversationDirectoryCache directory,
    IOptions<HubOptions> options, ConversationRebuildHooks hooks, TimeProvider time, ILogger<ConversationRebuild> log) : BackgroundService
{
    /// <summary>重建失敗之後多久再試</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    private readonly TaskCompletionSource firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly SemaphoreSlim running = new(1, 1);

    /// <summary>啟動時的檢查結果（<see cref="Check"/>）：存的版本、要不要重建</summary>
    private (string? Stored, bool Needed)? startup;

    /// <summary>啟動後的第一次檢查（含需要時的重建）做完（測試用）；失敗之後重試成功才完成</summary>
    public Task FirstRun => firstRun.Task;

    /// <summary>
    /// 啟動時（開始聽連接埠之前、目錄載入之後；<c>IStartupTask</c>）讀版本鍵與有沒有對話的資料：都是 O(1)，決定背景要不要重建。
    /// 在啟動時決定、不在背景決定：服務開始收事件之後才看，會把剛收的第一筆當成「既有的資料」而多做一次重建
    /// </summary>
    public void Check()
    {
        var (stored, hasConversations) = ReadState();
        startup = (stored, ConversationRebuildRules.NeedsRebuild(stored, ConversationMaterializer.Version, hasConversations));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (startup is null)
            Check();
        var (stored, needed) = startup!.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (needed)
                    await RebuildAsync(stored, stoppingToken);
                else if (stored != CurrentVersion)
                    await writer.WriteAsync(context => WriteVersion(context), stoppingToken);
                firstRun.TrySetResult();
                return;
            }
            catch (Exception ex) when (stoppingToken.IsCancellationRequested && ex is OperationCanceledException or InvalidOperationException)
            {
                // 服務停止（寫入者已關閉）：下次啟動從頭再做
                firstRun.TrySetCanceled(stoppingToken);
                return;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "對話物化表的重建失敗，{Minutes} 分鐘後從頭再做", RetryDelay.TotalMinutes);
                try
                {
                    await Task.Delay(RetryDelay, time, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    firstRun.TrySetCanceled(stoppingToken);
                    return;
                }
            }
        }
        firstRun.TrySetCanceled(stoppingToken);
    }

    /// <summary>
    /// 依對話逐一分塊重建、全部做完寫版本鍵（同時只跑一個）；<paramref name="stored"/> 只用來記錄。測試也直接呼叫它（不看版本一律重建）
    /// </summary>
    internal async Task RebuildAsync(string? stored, CancellationToken cancellationToken)
    {
        await running.WaitAsync(cancellationToken);
        try
        {
            var ids = ListConversations();
            log.LogInformation("對話物化表要重建（存的版本 {Stored}、程式 {Current}）：{Count} 個對話", stored ?? "（沒有）", CurrentVersion, ids.Count);
            directory.MarkRebuildPending(ids);
            try
            {
                var chunk = options.Value.Conversations.RebuildChunkEvents;
                foreach (var id in ids)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var step = await writer.WriteAsync(context => materializer.RebuildStart(context, id), cancellationToken);
                    await AfterChunkAsync(id, step);
                    while (!step.Finished)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        step = await writer.WriteAsync(context => materializer.RebuildNext(context, id, chunk), cancellationToken);
                        await AfterChunkAsync(id, step);
                    }
                }
                await writer.WriteAsync(context => WriteVersion(context), cancellationToken);
            }
            catch
            {
                // 放棄進行中的重建：之後的 item 類事件照常物化，下一次從頭再做（版本鍵沒寫）。
                // 已經清掉內容的對話在重做之前是不完整的：全部標回「等著重建」（顯示「伺服器正在整理這個對話」），不是清掉標記
                try
                {
                    await writer.WriteAsync(context => materializer.RebuildReset(context), CancellationToken.None);
                }
                catch (Exception reset) when (reset is InvalidOperationException or OperationCanceledException)
                {
                    // 寫入者已關閉：程序要結束了，記憶體裡的進度跟著消失
                }
                directory.ClearRebuild();
                directory.MarkRebuildPending(ids.Where(id => directory.TryGet(id, out _)));
                throw;
            }
            directory.ClearRebuild();
            log.LogInformation("對話物化表重建完成：{Count} 個對話", ids.Count);
        }
        finally
        {
            running.Release();
        }
    }

    private static string CurrentVersion => ConversationMaterializer.Version.ToString(CultureInfo.InvariantCulture);

    private async Task AfterChunkAsync(string conversationId, RebuildStep step)
    {
        if (hooks.AfterChunk is { } hook)
            await hook(conversationId, step);
    }

    /// <summary>存的版本與有沒有任何對話的資料（唯讀交易；都是 O(1) 的查詢）</summary>
    private (string? Stored, bool HasConversations) ReadState()
    {
        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        using var version = connection.CreateCommand();
        version.Transaction = tx;
        version.CommandText = "SELECT value FROM server_meta WHERE key = $k";
        version.Parameters.AddWithValue("$k", ConversationMaterializer.VersionKey);
        var stored = version.ExecuteScalar() as string;
        using var has = connection.CreateCommand();
        has.Transaction = tx;
        has.CommandText = "SELECT EXISTS(SELECT 1 FROM conversations) OR EXISTS(SELECT 1 FROM events WHERE stream >= 'c:' AND stream < 'c;')";
        return (stored, Convert.ToInt64(has.ExecuteScalar(), CultureInfo.InvariantCulture) != 0);
    }

    /// <summary>要重建的對話：<c>conversations</c> 表的（依更新時間由新到舊），再加上只出現在 events 的（依 id）</summary>
    private List<string> ListConversations()
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT conversation_id FROM conversations ORDER BY updated_at DESC, conversation_id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (seen.Add(id))
                    ids.Add(id);
            }
        }
        using (var cmd = connection.CreateCommand())
        {
            // 串流索引 ix_events_stream 的範圍掃描：'c;' 是 'c:' 之後的下一個字元
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT DISTINCT stream FROM events WHERE stream >= 'c:' AND stream < 'c;' ORDER BY stream";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0)[2..];
                if (ConversationIds.IsWireId(id) && seen.Add(id))
                    ids.Add(id);
            }
        }
        return ids;
    }

    private static void WriteVersion(SyncWriteContext context) =>
        context.Execute("INSERT INTO server_meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            ("$k", ConversationMaterializer.VersionKey), ("$v", CurrentVersion));
}
