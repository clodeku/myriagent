using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyriagentHub.Tests.Sync;

/// <summary>sync.db 的結構與單一寫入者（設計 3.2）：合併交易、每個工作各自回滾、提交後才完成、結束時檢查點</summary>
public sealed class SyncWriterTests : IDisposable
{
    private readonly string dir = TempDir.Create();

    public void Dispose() => TempDir.TryDelete(dir);

    [Fact]
    public async Task 建庫_WAL與PRAGMA_伺服器身分是128位元亂數_重開不變()
    {
        string instanceId;
        using (var writer = await StartAsync())
        {
            instanceId = writer.Instance.InstanceId;
            instanceId.Should().MatchRegex("^[0-9a-f]{32}$");
            writer.Instance.RestoreGeneration.Should().Be(0);
            using var read = writer.OpenRead();
            SqliteSetup.Scalar(read, "PRAGMA journal_mode")!.ToString().Should().Be("wal");
            SqliteSetup.Scalar(read, "PRAGMA synchronous").Should().Be(2L, "FULL：確認了就在磁碟上");
            SqliteSetup.Scalar(read, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be("7", "WP4.1 起是 v2（第 4 階段的表），WP4.2 步 12A 起是 v3（網頁未讀），app 自我更新 U2 起是 v4（device_app），tok1 起是 v5（token_tallies），第 5 階段 WP5.1 起是 v6（日誌與交接），排程頁 S3 起是 v7（device_schedules）");
            foreach (var table in new[] { "device_streams", "events", "rejected_events", "logical_accounts", "account_links", "usage_latest", "usage_points", "usage_pollers", "device_cli", "prefs", "device_app" })
                SqliteSetup.Scalar(read, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'").Should().Be(1L, table);
        }

        using (var again = await StartAsync())
            again.Instance.InstanceId.Should().Be(instanceId, "結構已存在時不重建、不換身分");
    }

    [Fact]
    public async Task 同時排的寫入都完成_一個工作丟例外只回滾它自己()
    {
        using var writer = await StartAsync();
        await writer.WriteAsync(c => c.Execute("CREATE TABLE t(v INTEGER)"));

        var tasks = Enumerable.Range(1, 50).Select(i => writer.WriteAsync(c =>
        {
            c.Execute("INSERT INTO t(v) VALUES($v)", ("$v", i));
            if (i % 10 == 0)
                throw new InvalidOperationException("壞的工作 " + i);
            return i;
        })).ToList();
        foreach (var (task, i) in tasks.Select((t, i) => (t, i + 1)))
        {
            if (i % 10 == 0)
                await FluentActions.Awaiting(() => task).Should().ThrowAsync<InvalidOperationException>().WithMessage("壞的工作 " + i);
            else
                (await task).Should().Be(i);
        }

        using var read = writer.OpenRead();
        SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM t").Should().Be(45L);
        SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM t WHERE v % 10 = 0").Should().Be(0L, "丟例外的工作的寫入一併回滾");
    }

    [Fact]
    public async Task 提交之後才完成_提交後的動作先於完成_被回滾的工作的動作不執行()
    {
        using var writer = await StartAsync();
        await writer.WriteAsync(c => c.Execute("CREATE TABLE t(v INTEGER)"));
        var observed = new List<long>();
        var rolledBackRan = false;

        var ok = writer.WriteAsync(c =>
        {
            c.Execute("INSERT INTO t(v) VALUES(1)");
            c.AfterCommit(() =>
            {
                // 另一條連線已看得到：交易已提交
                using var read = writer.OpenRead();
                observed.Add((long)SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM t")!);
            });
            return 1;
        });
        var bad = writer.WriteAsync<int>(c =>
        {
            c.AfterCommit(() => rolledBackRan = true);
            throw new InvalidOperationException("回滾");
        });

        (await ok).Should().Be(1);
        observed.Should().Equal(1L);
        await FluentActions.Awaiting(() => bad).Should().ThrowAsync<InvalidOperationException>();
        rolledBackRan.Should().BeFalse();
    }

    [Fact]
    public async Task 唯讀連線不能寫()
    {
        using var writer = await StartAsync();
        using var read = writer.OpenRead();
        FluentActions.Invoking(() => SqliteSetup.Exec(read, "INSERT INTO server_meta(key, value) VALUES('x', 'y')"))
            .Should().Throw<SqliteException>();
    }

    [Fact]
    public async Task 停止後不接受寫入_WAL併回主檔()
    {
        var writer = await StartAsync();
        await writer.WriteAsync(c => c.Execute("CREATE TABLE t(v INTEGER)"));
        await writer.WriteAsync(c => c.Execute("INSERT INTO t(v) VALUES(7)"));
        await writer.CheckpointAsync();
        writer.Stop();
        writer.Stop();

        FluentActions.Invoking(() => { _ = writer.WriteAsync(c => c.Execute("INSERT INTO t(v) VALUES(8)")); }).Should().Throw<InvalidOperationException>();
        var paths = new HubPaths(dir);
        File.Exists(paths.SyncDb + "-wal").Should().BeFalse("最後一條連線關閉時 SQLite 做檢查點並刪掉 -wal");
        using var read = SqliteSetup.Open(paths.SyncDb, readOnly: true);
        SqliteSetup.Scalar(read, "SELECT v FROM t").Should().Be(7L);
    }

    [Fact]
    public async Task 開始執行前就取消的工作不執行()
    {
        using var writer = await StartAsync();
        await writer.WriteAsync(c => c.Execute("CREATE TABLE t(v INTEGER)"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = writer.WriteAsync(c => c.Execute("INSERT INTO t(v) VALUES(1)"), cts.Token);
        await FluentActions.Awaiting(() => cancelled).Should().ThrowAsync<OperationCanceledException>();
        using var read = writer.OpenRead();
        SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM t").Should().Be(0L);
    }

    private async Task<SyncWriter> StartAsync()
    {
        var writer = new SyncWriter(new HubPaths(dir), TimeProvider.System, NullLogger<SyncWriter>.Instance);
        await writer.InitializeAsync(CancellationToken.None);
        return writer;
    }
}
