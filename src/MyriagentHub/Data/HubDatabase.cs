using MyriagentHub.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MyriagentHub.Data;

internal static class HubDataServiceCollectionExtensions
{
    /// <summary>hub.db（EF Core＋SQLite）、單一寫入者、kv；Identity 的結構版本在 AddHubAuth 設定</summary>
    public static IServiceCollection AddHubData(this IServiceCollection services)
    {
        services.AddSingleton<HubWriteLock>();
        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddSingleton<SingleWriterInterceptor>();
        services.AddDbContext<HubDbContext>((sp, o) => o
            .UseSqlite(SqliteSetup.ConnectionString(sp.GetRequiredService<HubPaths>().HubDb))
            .AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>(), sp.GetRequiredService<SingleWriterInterceptor>()));
        services.AddSingleton<KvStore>();
        services.AddSingleton<HubDatabase>();
        services.AddSingleton<IStartupTask>(sp => new StartupTask(StartupOrder.Database, "資料庫遷移", ct => sp.GetRequiredService<HubDatabase>().InitializeAsync(ct)));
        services.AddHostedService<DatabaseShutdown>();
        return services;
    }
}

/// <summary>所有服務都停止之後（9.12）：把 WAL 併回主檔並截斷，下次啟動與部署的停機複製都是乾淨的單一檔案</summary>
internal sealed class DatabaseShutdown(HubDatabase database, ILogger<DatabaseShutdown> log) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        try
        {
            database.Checkpoint();
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            log.LogWarning(ex, "結束前的 WAL 檢查點失敗（下次開啟時 SQLite 會自動復原）");
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// hub.db 的建立與遷移（服務開始聽連接埠之前執行），以及服務執行期間的保活連線：連線不用池（見 <see cref="SqliteSetup"/>），
/// 沒有任何連線開著時，每個操作的連線關閉都是「最後一條」——SQLite 每次都做檢查點（synchronous=FULL 要 fsync 主檔）
/// 並刪掉 -wal、-shm，下一次再重建。保活連線開好之後不再做任何查詢（不持有讀取交易，不擋檢查點），讓 -wal、-shm 一直在，
/// 檢查點回到 WAL 滿 1000 頁才自動做（量測見 tools/hub-probe 的 SqliteBench B5）；服務結束時 <see cref="Checkpoint"/> 先關它。
/// </summary>
internal sealed class HubDatabase(HubPaths paths, IServiceScopeFactory scopes, ILogger<HubDatabase> log) : IDisposable
{
    private SqliteConnection? keepAlive;

    /// <summary>保活連線是否開著（遷移完成後開、服務結束時關）</summary>
    public bool KeepAliveOpen => Volatile.Read(ref keepAlive) is not null;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.DbDir);
        var removed = ClearStaleMigrationLock(paths.HubDb);
        if (removed > 0)
            log.LogWarning("hub.db 有殘留的 __EFMigrationsLock（上次遷移途中被中止），已清除 {Count} 列後重新遷移", removed);

        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
                log.LogInformation("hub.db 套用遷移：{Migrations}", string.Join(", ", pending));
            await db.Database.MigrateAsync(cancellationToken);
            var unknown = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Except(db.Database.GetMigrations()).ToList();
            if (unknown.Count > 0)
                log.LogWarning("hub.db 有本版不認得的遷移（資料庫比程式新，可能是回滾到舊版）：{Migrations}", string.Join(", ", unknown));
        }

        var connection = SqliteSetup.Open(paths.HubDb);
        try
        {
            var mode = SqliteSetup.Scalar(connection, "PRAGMA journal_mode=WAL")?.ToString();
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"hub.db 無法切換成 WAL（目前 {mode}）");
            // 讀過一次才持有主檔的共用鎖（WAL 模式下連線開著就一直持有）：之後別的連線關閉都不是最後一條
            SqliteSetup.Scalar(connection, "SELECT COUNT(*) FROM sqlite_master");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        Interlocked.Exchange(ref keepAlive, connection)?.Dispose();
    }

    /// <summary>
    /// 服務結束時：先關保活連線，再 PRAGMA wal_checkpoint(TRUNCATE)。其他連線都已關閉時這條就是最後一條，
    /// 關閉時 SQLite 另外刪掉 -wal、-shm；還有別的連線開著時至少把 WAL 併回主檔並截斷。
    /// </summary>
    public void Checkpoint()
    {
        CloseKeepAlive();
        if (!File.Exists(paths.HubDb))
            return;
        using var connection = SqliteSetup.Open(paths.HubDb);
        SqliteSetup.Exec(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
    }

    /// <summary>主機釋放時（含啟動失敗、沒有走到結束檢查點）一定關掉保活連線</summary>
    public void Dispose() => CloseKeepAlive();

    private void CloseKeepAlive() => Interlocked.Exchange(ref keepAlive, null)?.Dispose();

    /// <summary>
    /// 程序在遷移途中被殺會留下 __EFMigrationsLock，之後的遷移會一直等。服務持有 service.lock（同一資料根只有一個實例），
    /// 所以啟動時看到的鎖一定是殘留的；遷移本身在交易裡，被中止的那次已經回滾，可以安全重跑。
    /// </summary>
    public static int ClearStaleMigrationLock(string dbPath)
    {
        if (!File.Exists(dbPath))
            return 0;
        using var connection = SqliteSetup.Open(dbPath);
        var exists = Convert.ToInt64(SqliteSetup.Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock'")) > 0;
        if (!exists)
            return 0;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM __EFMigrationsLock";
        return cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// 不經主機建立 HubDbContext（admin restore／preflight、EF 設計階段工具）：與服務相同的 Identity Version3 模型、PRAGMA 與單一寫入者。
/// </summary>
internal static class HubDbContextFactory
{
    public static ServiceProvider CreateServices(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new HubPaths(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(dbPath)))!));
        services.AddSingleton<HubWriteLock>();
        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddSingleton<SingleWriterInterceptor>();
        services.AddDbContext<HubDbContext>((sp, o) => o
            .UseSqlite(SqliteSetup.ConnectionString(dbPath))
            .AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>(), sp.GetRequiredService<SingleWriterInterceptor>()));
        services.AddIdentityCore<IdentityUser>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<HubDbContext>();
        return services.BuildServiceProvider();
    }
}

/// <summary>EF 設計階段（dotnet ef migrations add）建立 HubDbContext 的方式；說明見 src/MyriagentHub/README.md</summary>
internal sealed class HubDbContextDesignFactory : IDesignTimeDbContextFactory<HubDbContext>
{
    public HubDbContext CreateDbContext(string[] args)
    {
        var services = HubDbContextFactory.CreateServices(Path.Combine(Path.GetTempPath(), "myriagenthub-design", "db", "hub.db"));
        return services.CreateScope().ServiceProvider.GetRequiredService<HubDbContext>();
    }
}
