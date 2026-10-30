using System.Threading.Channels;
using Myriagent.Sync;
using MyriagentHub.Hosting;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Data;

/// <summary>一個寫入工作在 sync.db 交易裡的環境（同一個交易的各工作共用連線與交易，各自在自己的 SAVEPOINT 裡）</summary>
internal sealed class SyncWriteContext
{
    private List<Action>? afterCommit;

    internal SyncWriteContext(SqliteConnection connection, SqliteTransaction transaction, long now)
    {
        Connection = connection;
        Transaction = transaction;
        Now = now;
    }

    public SqliteConnection Connection { get; }

    public SqliteTransaction Transaction { get; }

    /// <summary>交易開始時的伺服器時間（Unix 毫秒；只供顯示與到期判斷，排序只看 seq）</summary>
    public long Now { get; }

    public SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = Connection.CreateCommand();
        cmd.Transaction = Transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(sql, parameters);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    /// <summary>交易提交（fsync）之後才做的事（通知、確認）；這個工作被回滾時一併丟掉。在寫入執行緒上執行，必須很快、不可阻塞</summary>
    public void AfterCommit(Action action) => (afterCommit ??= []).Add(action);

    /// <summary>目前登記了幾個提交後的動作（工作內自己開 SAVEPOINT 時記下，回滾到它時以 <see cref="DiscardAfterCommit"/> 丟掉之後登記的）</summary>
    public int AfterCommitMark => afterCommit?.Count ?? 0;

    public void DiscardAfterCommit(int mark)
    {
        if (afterCommit is not null && afterCommit.Count > mark)
            afterCommit.RemoveRange(mark, afterCommit.Count - mark);
    }

    internal List<Action>? TakeAfterCommit() => afterCommit;
}

/// <summary>
/// sync.db 的單一寫入者（設計 3.2）：只有這一條寫入連線，所有寫入經 <see cref="Channel{T}"/> 串列化到專用的執行緒；
/// 已在佇列裡的工作（最多 <see cref="MaxItemsPerTransaction"/> 個）合併成一個交易（group commit：前一個交易 fsync 的期間排進來的一起提交，
/// 不為了湊批次而多等）。每個工作在自己的 SAVEPOINT 裡，失敗只回滾它自己；交易提交（<c>synchronous=FULL</c>）之後才完成工作的 Task——
/// 呼叫端拿到結果時資料一定已經在磁碟上（BatchAck 就是這樣保證「確認了就不會丟」）。
/// <para>
/// 讀取各自以 <see cref="OpenRead"/> 開短連線（連線不用池，見 <see cref="SqliteSetup"/>）；讀取交易保持短，否則 WAL 會一直長大。
/// 寫入連線一直開著，兼任保活連線（-wal、-shm 不會在每個操作後重建）；每 5 分鐘 <c>wal_checkpoint(TRUNCATE)</c>，結束時先做檢查點再關。
/// </para>
/// </summary>
internal sealed class SyncWriter(HubPaths paths, TimeProvider time, ILogger<SyncWriter> log) : IDisposable
{
    public const int MaxItemsPerTransaction = 200;

    public static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(5);

    private readonly Channel<WorkItem> queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
    private SqliteConnection? connection;
    private Thread? thread;
    private ITimer? checkpointTimer;
    private ServerInstance? instance;
    private int stopped;

    /// <summary>這個資料庫的身分（建庫時的 instanceId、還原世代）；還原只在服務停止時做，執行期間不變</summary>
    public ServerInstance Instance => instance ?? throw new InvalidOperationException("sync.db 尚未初始化");

    public bool IsRunning => thread is not null && Volatile.Read(ref stopped) == 0;

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(paths.DbDir);
        var opened = SqliteSetup.Open(paths.SyncDb);
        try
        {
            var mode = SqliteSetup.Scalar(opened, "PRAGMA journal_mode=WAL")?.ToString();
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"sync.db 無法切換成 WAL（目前 {mode}）");
            var from = SyncSchema.Migrate(opened);
            if (from == 0)
                log.LogInformation("sync.db 已建立（結構版本 {Version}）", SyncSchema.CurrentVersion);
            else if (from < SyncSchema.CurrentVersion)
                log.LogInformation("sync.db 結構版本 {From} → {To}", from, SyncSchema.CurrentVersion);
            else if (from > SyncSchema.CurrentVersion)
                log.LogWarning("sync.db 的結構版本 {Version} 比程式新（可能是回滾到舊版）：只往前加欄位，舊版照樣可用", from);
            instance = SyncSchema.ReadInstance(opened);
        }
        catch
        {
            opened.Dispose();
            throw;
        }

        connection = opened;
        thread = new Thread(Loop) { IsBackground = true, Name = "sync.db writer" };
        thread.Start();
        checkpointTimer = time.CreateTimer(_ => queue.Writer.TryWrite(new CheckpointItem()), null, CheckpointInterval, CheckpointInterval);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 在寫入交易裡執行 <paramref name="work"/>（同步、要快：整個伺服器的寫入都排在同一條執行緒上），交易提交之後才完成。
    /// 工作丟出例外時只回滾它自己（SAVEPOINT），例外原樣傳給呼叫端；開始執行之後就不理會取消（不留半套狀態）。
    /// </summary>
    public Task<T> WriteAsync<T>(Func<SyncWriteContext, T> work, CancellationToken cancellationToken = default)
    {
        var item = new WorkItem<T>(work, cancellationToken);
        if (!queue.Writer.TryWrite(item))
            throw new InvalidOperationException("sync.db 已關閉");
        return item.Task;
    }

    public Task WriteAsync(Action<SyncWriteContext> work, CancellationToken cancellationToken = default) =>
        WriteAsync(context =>
        {
            work(context);
            return true;
        }, cancellationToken);

    /// <summary>開一條唯讀連線（呼叫端負責 Dispose；讀取交易保持短）</summary>
    public SqliteConnection OpenRead() => SqliteSetup.Open(paths.SyncDb, readOnly: true);

    /// <summary>排一次 wal_checkpoint(TRUNCATE)（定時器與測試用），做完才完成</summary>
    public Task CheckpointAsync()
    {
        var item = new CheckpointItem();
        if (!queue.Writer.TryWrite(item))
            throw new InvalidOperationException("sync.db 已關閉");
        return item.Task;
    }

    /// <summary>停止接受新的寫入、做完佇列裡的、做檢查點並關閉連線（服務結束時；可以重複呼叫）</summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0)
            return;
        checkpointTimer?.Dispose();
        queue.Writer.TryComplete();
        if (thread is not null && !thread.Join(TimeSpan.FromSeconds(20)))
            log.LogError("sync.db 寫入執行緒 20 秒內沒有結束");
        var current = Interlocked.Exchange(ref connection, null);
        if (current is null)
            return;
        try
        {
            SqliteSetup.Exec(current, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        catch (SqliteException ex)
        {
            log.LogWarning(ex, "sync.db 結束前的 WAL 檢查點失敗（下次開啟時 SQLite 會自動復原）");
        }
        finally
        {
            current.Dispose();
        }
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        var reader = queue.Reader;
        var batch = new List<WorkItem>(MaxItemsPerTransaction);
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            batch.Clear();
            while (batch.Count < MaxItemsPerTransaction && reader.TryRead(out var item))
            {
                if (item is CheckpointItem checkpoint)
                {
                    // 檢查點不能在交易裡做：先把已收的提交掉
                    if (batch.Count > 0)
                    {
                        RunTransaction(batch);
                        batch.Clear();
                    }
                    RunCheckpoint(checkpoint);
                    continue;
                }
                if (item.Token.IsCancellationRequested)
                {
                    item.Cancel();
                    continue;
                }
                batch.Add(item);
            }
            if (batch.Count > 0)
                RunTransaction(batch);
        }
    }

    private void RunTransaction(List<WorkItem> batch)
    {
        var db = connection!;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var committed = new List<(WorkItem Item, List<Action>? After)>(batch.Count);
        SqliteTransaction tx;
        try
        {
            tx = db.BeginTransaction();
        }
        catch (Exception ex)
        {
            log.LogError(ex, "sync.db 無法開始交易");
            foreach (var item in batch)
                item.Fail(ex);
            return;
        }

        using (tx)
        {
            for (var i = 0; i < batch.Count; i++)
            {
                var item = batch[i];
                var context = new SyncWriteContext(db, tx, now);
                try
                {
                    Exec(db, tx, "SAVEPOINT w");
                    item.Run(context);
                    Exec(db, tx, "RELEASE w");
                    committed.Add((item, context.TakeAfterCommit()));
                }
                catch (Exception ex)
                {
                    try
                    {
                        Exec(db, tx, "ROLLBACK TO w");
                        Exec(db, tx, "RELEASE w");
                    }
                    catch (Exception rollback)
                    {
                        // 交易本身壞了（例如磁碟錯誤讓 SQLite 自動回滾）：這一批全部失敗
                        log.LogError(rollback, "sync.db 交易無法回滾到儲存點，整批 {Count} 個寫入失敗", batch.Count);
                        foreach (var (done, _) in committed)
                            done.Fail(ex);
                        for (var j = i; j < batch.Count; j++)
                            batch[j].Fail(ex);
                        return;
                    }
                    item.Fail(ex);
                }
            }

            try
            {
                tx.Commit();
            }
            catch (Exception ex)
            {
                log.LogError(ex, "sync.db 交易提交失敗（{Count} 個寫入）", committed.Count);
                foreach (var (done, _) in committed)
                    done.Fail(ex);
                return;
            }
        }

        foreach (var (item, after) in committed)
        {
            if (after is not null)
            {
                foreach (var action in after)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "sync.db 提交後的動作失敗");
                    }
                }
            }
            item.Complete();
        }
    }

    private void RunCheckpoint(CheckpointItem item)
    {
        try
        {
            // TRUNCATE 遇到開著的讀取交易（備份的 VACUUM INTO、長的拉取）會等到 busy_timeout（5 秒），期間所有同步寫入都停：
            // 定時的這一次不等（busy_timeout 0），做不完就是 busy、下一次再做；結束時（Stop）照樣等
            SqliteSetup.Exec(connection!, "PRAGMA busy_timeout=0");
            try
            {
                SqliteSetup.Exec(connection!, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
            finally
            {
                SqliteSetup.Exec(connection!, "PRAGMA busy_timeout=" + SqliteSetup.BusyTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            item.Complete();
        }
        catch (SqliteException ex)
        {
            log.LogWarning(ex, "sync.db 定時 WAL 檢查點失敗");
            item.Fail(ex);
        }
    }

    private static void Exec(SqliteConnection db, SqliteTransaction tx, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private abstract class WorkItem
    {
        public CancellationToken Token { get; init; }

        public abstract void Run(SyncWriteContext context);

        public abstract void Complete();

        public abstract void Fail(Exception ex);

        public abstract void Cancel();
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<SyncWriteContext, T> work;
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private T? result;

        public WorkItem(Func<SyncWriteContext, T> work, CancellationToken token)
        {
            this.work = work;
            Token = token;
        }

        public Task<T> Task => completion.Task;

        public override void Run(SyncWriteContext context) => result = work(context);

        public override void Complete() => completion.TrySetResult(result!);

        public override void Fail(Exception ex) => completion.TrySetException(ex);

        public override void Cancel() => completion.TrySetCanceled(Token);
    }

    private sealed class CheckpointItem : WorkItem
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => completion.Task;

        public override void Run(SyncWriteContext context) => throw new InvalidOperationException("檢查點不在交易裡執行");

        public override void Complete() => completion.TrySetResult();

        public override void Fail(Exception ex) => completion.TrySetException(ex);

        public override void Cancel() => completion.TrySetCanceled();
    }
}
