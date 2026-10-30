using System.Diagnostics;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Tests.Data;

/// <summary>hub.db：遷移與模型一致、Identity Version3、PRAGMA（WAL、synchronous=FULL）、不用連線池、殘留遷移鎖、單一寫入者</summary>
public sealed class DataTests
{
    [Fact]
    public void 模型與遷移一致_沒有待產生的遷移()
    {
        var dir = TempDir.Create();
        try
        {
            using var services = HubDbContextFactory.CreateServices(Path.Combine(dir, "db", "hub.db"));
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            db.Database.HasPendingModelChanges().Should().BeFalse("改了模型就要用 dotnet ef migrations add 產生遷移（見 src/MyriagentHub/README.md）");
            db.Model.FindEntityType(typeof(Microsoft.AspNetCore.Identity.IdentityUserPasskey<string>)).Should().NotBeNull("Identity Version3 才有 passkey 資料表");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    /// <summary>
    /// 一律不用連線池（dotnet/efcore#39008：池內的原生連線會被兩個 SqliteConnection 共用，一個流程的寫入可能被另一個流程的交易回滾；
    /// 見 SqliteSetup）：SqliteSetup 產生的讀寫與唯讀連線字串、服務與 admin 指令裡 EF DbContext 的連線字串都是 Pooling=False。
    /// </summary>
    [Fact]
    public async Task 連線字串一律Pooling_False_讀寫唯讀_服務與admin的EF都是()
    {
        var readWrite = new SqliteConnectionStringBuilder(SqliteSetup.ConnectionString(@"C:\x\hub.db"));
        readWrite.Pooling.Should().BeFalse();
        readWrite.Mode.Should().Be(SqliteOpenMode.ReadWriteCreate);
        var readOnly = new SqliteConnectionStringBuilder(SqliteSetup.ConnectionString(@"C:\x\hub.db", readOnly: true, defaultTimeoutSeconds: 60));
        readOnly.Pooling.Should().BeFalse();
        readOnly.Mode.Should().Be(SqliteOpenMode.ReadOnly);
        readOnly.DefaultTimeout.Should().Be(60);

        await using (var host = await HubTestHost.StartAsync())
        {
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var builder = new SqliteConnectionStringBuilder(db.Database.GetConnectionString());
            builder.Pooling.Should().BeFalse("服務的 EF DbContext（AddHubData 的 UseSqlite）");
            builder.DataSource.Should().Be(host.Paths.HubDb);
        }

        var dir = TempDir.Create();
        try
        {
            using var services = HubDbContextFactory.CreateServices(Path.Combine(dir, "db", "hub.db"));
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).Pooling.Should().BeFalse("admin restore／preflight 用的 HubDbContextFactory");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    /// <summary>
    /// 不用池：連線 Dispose 就真的關檔。WAL 模式下最後一條連線關閉時 SQLite 做檢查點並刪掉 -wal、-shm，
    /// 主檔可以馬上搬走（還原、部署前的停機複製、測試刪暫存資料夾都靠這個，不必再清池）。
    /// </summary>
    [Fact]
    public void 連線Dispose就關檔_最後一條關閉時刪掉WAL與SHM_主檔可以馬上搬走()
    {
        var dir = TempDir.Create();
        try
        {
            var path = Path.Combine(dir, "probe.db");
            using (var connection = SqliteSetup.Open(path))
            {
                SqliteSetup.Scalar(connection, "PRAGMA journal_mode=WAL").Should().Be("wal");
                SqliteSetup.Exec(connection, "CREATE TABLE t(v INTEGER); INSERT INTO t VALUES (1);");
                File.Exists(path + "-wal").Should().BeTrue("連線開著時有 WAL");
            }
            File.Exists(path + "-wal").Should().BeFalse("最後一條連線關閉時檢查點後刪掉 WAL");
            File.Exists(path + "-shm").Should().BeFalse();

            var moved = Path.Combine(dir, "moved.db");
            File.Move(path, moved);
            using var check = SqliteSetup.Open(moved);
            SqliteSetup.Scalar(check, "SELECT COUNT(*) FROM t").Should().Be(1L, "已提交的資料在主檔裡");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    /// <summary>
    /// 服務執行期間 HubDatabase 開著保活連線：操作的連線關閉都不是最後一條，-wal、-shm 不會每次重建、也不會每次關閉就做檢查點；
    /// 結束時先關保活連線再做 wal_checkpoint(TRUNCATE)，最後一條連線關閉時刪掉 -wal、-shm，資料都在主檔。
    /// </summary>
    [Fact]
    public async Task 服務執行中有保活連線_閒置時WAL也在_結束時併回主檔並刪掉()
    {
        var dir = TempDir.Create();
        try
        {
            var paths = new HubPaths(dir);
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                host.Services.GetRequiredService<HubDatabase>().KeepAliveOpen.Should().BeTrue();
                await host.Services.GetRequiredService<KvStore>().SetAsync("keepalive_probe", 7);
                File.Exists(paths.HubDb + "-wal").Should().BeTrue("保活連線開著：剛才那條連線關閉時不是最後一條，WAL 沒被刪");
                File.Exists(paths.HubDb + "-shm").Should().BeTrue();
                new FileInfo(paths.HubDb + "-wal").Length.Should().BeGreaterThan(0, "剛提交的交易還在 WAL 裡（沒有每次關閉就做檢查點）");
            }
            File.Exists(paths.HubDb + "-wal").Should().BeFalse("結束時先關保活連線，檢查點那條是最後一條，關閉時刪掉 WAL");
            File.Exists(paths.HubDb + "-shm").Should().BeFalse();
            using var check = SqliteSetup.Open(paths.HubDb);
            SqliteSetup.Scalar(check, "SELECT value FROM kv WHERE key = 'keepalive_probe'").Should().Be("7", "已提交的資料併回主檔");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 啟動後資料庫是WAL_每條連線synchronous_FULL與外鍵()
    {
        await using var host = await HubTestHost.StartAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            Scalar(connection, "PRAGMA journal_mode").Should().Be("wal");
            Scalar(connection, "PRAGMA synchronous").Should().Be("2", "FULL：提交就是已落盤");
            Scalar(connection, "PRAGMA foreign_keys").Should().Be("1");
            Scalar(connection, "PRAGMA busy_timeout").Should().Be("5000");
            Scalar(connection, "PRAGMA mmap_size").Should().Be("0");
            Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'AspNetUserPasskeys'").Should().Be("1");
            Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('devices') WHERE name = 'token_hash'").Should().Be("1", "自己的表用 snake_case");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task 遷移途中被殺留下的鎖_下次啟動清掉後照常啟動()
    {
        var dir = TempDir.Create();
        try
        {
            await using (await HubTestHost.StartAsync(dataDir: dir))
            {
            }
            var hubDb = new HubPaths(dir).HubDb;
            using (var connection = SqliteSetup.Open(hubDb))
            {
                SqliteSetup.Exec(connection, "CREATE TABLE IF NOT EXISTS __EFMigrationsLock (Id INTEGER NOT NULL PRIMARY KEY, Timestamp TEXT NOT NULL)");
                SqliteSetup.Exec(connection, "INSERT INTO __EFMigrationsLock (Id, Timestamp) VALUES (1, '2026-10-11')");
            }
            var sw = Stopwatch.StartNew();
            await using (await HubTestHost.StartAsync(dataDir: dir))
            {
                sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "不會卡在等鎖");
            }
            using var check = SqliteSetup.Open(hubDb);
            SqliteSetup.Scalar(check, "SELECT COUNT(*) FROM __EFMigrationsLock").Should().Be(0L);
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 單一寫入者_同時只有一段寫入_同一流程內可重入()
    {
        var gate = new HubWriteLock();
        var active = 0;
        var max = 0;
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => gate.RunAsync(async () =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref max, now);
            await Task.Delay(1);
            await gate.RunAsync(() => Task.CompletedTask); // 重入不會自己卡死
            Interlocked.Decrement(ref active);
        }))));
        max.Should().Be(1);
    }

    [Fact]
    public async Task SaveChanges在別人持有寫入鎖時等待_持有者內部的SaveChanges不會卡死()
    {
        await using var host = await HubTestHost.StartAsync();
        var gate = host.Services.GetRequiredService<HubWriteLock>();
        var kv = host.Services.GetRequiredService<KvStore>();
        var order = new List<string>();
        // 非同步延續：不讓測試本身的程式碼跑在持有鎖的那個非同步流程裡
        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = gate.RunAsync(async () =>
        {
            holderEntered.SetResult();
            await kv.SetAsync("test_inner", 1); // 持有鎖時的 SaveChanges 不重複取鎖
            await release.Task;
            lock (order)
                order.Add("holder-done");
        });
        await holderEntered.Task;
        var writer = Task.Run(async () =>
        {
            await kv.SetAsync("test_outer", 2);
            lock (order)
                order.Add("writer-done");
        });
        await Task.Delay(200);
        lock (order)
            order.Should().BeEmpty("另一個寫入在等鎖");
        release.SetResult();
        await Task.WhenAll(holder, writer).WaitAsync(TimeSpan.FromSeconds(10));
        order.Should().Equal("holder-done", "writer-done");
        (await kv.GetAsync<int>("test_outer")).Should().Be(2);
    }

    /// <summary>
    /// 不用連線池就沒有池可清：Microsoft.Data.Sqlite 10.0.12 的連線在 Activate 時先標記使用中、後設擁有者，這中間被
    /// ClearPool／ClearAllPools（或另一個取用者）掃到會被當成外洩連線回收（平行測試曾因此 ObjectDisposedException: SQLitePCL.sqlite3，
    /// 上游 dotnet/efcore#39008）。以中繼資料掃描伺服器組件，兩個清池方法都不准呼叫——要清池就代表有人又開了連線池。
    /// </summary>
    [Fact]
    public void 伺服器組件不呼叫SqliteConnection的清池方法()
    {
        using var stream = File.OpenRead(typeof(SqliteSetup).Assembly.Location);
        using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
        var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var calls = md.MemberReferences.Select(h => md.GetMemberReference(h))
            .Where(m => m.Parent.Kind == System.Reflection.Metadata.HandleKind.TypeReference)
            .Select(m => (Type: md.GetTypeReference((System.Reflection.Metadata.TypeReferenceHandle)m.Parent), Name: md.GetString(m.Name)))
            .Select(m => md.GetString(m.Type.Namespace) + "." + md.GetString(m.Type.Name) + "::" + m.Name)
            .ToList();
        calls.Should().Contain("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder::set_Pooling", "確認掃到的是伺服器組件的成員參考（SqliteSetup 設 Pooling=False）");
        calls.Should().NotContain("Microsoft.Data.Sqlite.SqliteConnection::ClearAllPools");
        calls.Should().NotContain("Microsoft.Data.Sqlite.SqliteConnection::ClearPool");
    }

    private static string? Scalar(System.Data.Common.DbConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
