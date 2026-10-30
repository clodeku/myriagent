using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MyriagentHub.Data;

/// <summary>
/// SQLite 連線設定（3.2）：WAL、synchronous=FULL（提交＝已落盤；NORMAL 在停電時可能回滾已提交的交易）、
/// busy_timeout、外鍵、16 MB 快取、不用 mmap。journal_mode 寫在檔案裡（建庫時設一次），其他每條連線都要設。
/// 伺服器所有的 SQLite 連線字串（EF 的 UseSqlite、備份、還原、preflight）都由 <see cref="ConnectionString"/> 產生。
/// <para>
/// <b>一律不用連線池（Pooling=False）</b>：Microsoft.Data.Sqlite 的連線池會讓同時開啟的兩個 <c>SqliteConnection</c>
/// 拿到同一條池內的原生連線——<c>SqliteConnectionInternal.Activate</c> 先公開「使用中」旗標（<c>_active = true</c>）、
/// 後設擁有者（<c>_outerConnection.SetTarget</c>），競爭的取用者在這中間把這條暫時沒有擁有者的連線當成外洩回收、
/// 交給另一個還活著的連線。兩個流程共用一條原生連線時，獨立的交易會失敗（cannot start a transaction within a
/// transaction），更糟的是一個流程的寫入可能被另一個流程的交易一起回滾，違反「伺服器確認後的持久事件一筆都不能丟」。
/// 上游 dotnet/efcore#39008，修正是 #39012（2026-09-18 併進 release/10.0），上游給的替代做法就是 Pooling=False；
/// 已比對 v10.0.12 tag 的 <c>src/Microsoft.Data.Sqlite.Core/SqliteConnectionInternal.cs</c>，<c>Activate</c> 仍是先設
/// <c>_active</c> 再設擁有者，而 NuGet 上 10.x 最新就是 10.0.12。
/// </para>
/// <para>
/// 不用池之後連線 Dispose 就真的關檔（搬檔、刪檔、檢查點之前不必再清池，也就沒有 ClearPool／ClearAllPools 可呼叫；
/// <c>DataTests</c> 以中繼資料掃描組件）。WAL 模式下最後一條連線關閉時 SQLite 會做檢查點並刪掉 -wal、-shm：
/// 沒有任何連線開著時每個操作都要重建 -wal、-shm、關閉時做檢查點（synchronous=FULL 要 fsync 主檔），所以服務執行期間
/// <see cref="HubDatabase"/> 開著一條保活連線，結束時先關它再做檢查點。每次開關多一次 sqlite3_open／close 與 PRAGMA 的成本、
/// 保活連線省下的部分見 tools/hub-probe 的 SqliteBench B5。沒有 -wal 的時候（服務已停止、正在啟動或結束），
/// <see cref="MyriagentHub.Admin.Preflight.SnapshotReadOnly(string, string)"/> 的 immutable 讀取要防主檔在讀取途中被改。
/// </para>
/// <para>
/// 只有升級到含 #39012 的套件版本（先確認新版 <c>Activate</c> 是先設擁有者、後標記使用中）並用 SqliteBench 量過，才考慮改回連線池。
/// </para>
/// </summary>
internal static class SqliteSetup
{
    public const int BusyTimeoutMs = 5000;

    public const string PerConnectionPragmas =
        "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA cache_size=-16384; PRAGMA mmap_size=0;";

    /// <summary>連線字串：一律 Pooling=False（原因見類別說明）；<paramref name="dataSource"/> 也可以是 <c>file:</c> URI（immutable 讀取）</summary>
    public static string ConnectionString(string dataSource, bool readOnly = false, int defaultTimeoutSeconds = 30) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = defaultTimeoutSeconds,
        }.ToString();

    /// <summary>開一條套用了所有 PRAGMA 的連線（備份、還原、自檢等不經 EF 的地方用；不用連線池，Dispose 就關檔）</summary>
    public static SqliteConnection Open(string path, bool readOnly = false, int defaultTimeoutSeconds = 30)
    {
        var connection = new SqliteConnection(ConnectionString(path, readOnly, defaultTimeoutSeconds));
        connection.Open();
        ApplyPragmas(connection);
        return connection;
    }

    public static void ApplyPragmas(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = PerConnectionPragmas;
        cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>EF 的每條連線開啟後套用 PRAGMA</summary>
internal sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        SqliteSetup.ApplyPragmas(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        SqliteSetup.ApplyPragmas(connection);
        return Task.CompletedTask;
    }
}

/// <summary>
/// hub.db 的單一寫入者：同一個程序內所有寫入串列化（SaveChanges 自動取得；需要「先讀再寫」的地方用 <see cref="RunAsync{T}"/>
/// 包住整段，例如稽核雜湊鏈要讀上一列的雜湊再插入）。同一個非同步流程內可重入。
/// </summary>
internal sealed class HubWriteLock
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AsyncLocal<bool> held = new();

    /// <summary>目前的非同步流程是否已持有寫入鎖</summary>
    public bool IsHeldByCurrentFlow => held.Value;

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken = default)
    {
        if (held.Value)
            return await work();
        await gate.WaitAsync(cancellationToken);
        try
        {
            held.Value = true;
            return await work();
        }
        finally
        {
            held.Value = false;
            gate.Release();
        }
    }

    public Task RunAsync(Func<Task> work, CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            await work();
            return true;
        }, cancellationToken);

    internal Task EnterAsync(CancellationToken cancellationToken) => gate.WaitAsync(cancellationToken);

    internal void Enter() => gate.Wait();

    internal void Exit() => gate.Release();
}

/// <summary>沒有在 <see cref="HubWriteLock.RunAsync{T}"/> 裡的 SaveChanges：存檔前取得寫入鎖、存完（成功、失敗或取消）釋放</summary>
internal sealed class SingleWriterInterceptor(HubWriteLock writeLock) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, object> owners = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context && !writeLock.IsHeldByCurrentFlow)
        {
            writeLock.Enter();
            owners.AddOrUpdate(context, this);
        }
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && !writeLock.IsHeldByCurrentFlow)
        {
            await writeLock.EnterAsync(cancellationToken);
            owners.AddOrUpdate(context, this);
        }
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Release(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Release(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Release(eventData.Context);

    public override Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Release(eventData.Context);
        return Task.CompletedTask;
    }

    private void Release(DbContext? context)
    {
        if (context is not null && owners.Remove(context))
            writeLock.Exit();
    }
}
