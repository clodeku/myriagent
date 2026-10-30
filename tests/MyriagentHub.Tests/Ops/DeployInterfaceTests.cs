using System.Diagnostics;
using System.Globalization;
using System.Net;
using ServiceBase = System.ServiceProcess.ServiceBase;
using System.Text.Json;
using MyriagentHub.Admin;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Tests.Ops;

/// <summary>
/// 部署腳本與服務的介面（docs/hub-setup.md 第 11 節，tools/deploy/Install-MyriagentHubRelease.ps1 照它寫）：
/// admin-inbox\deploy\ 只接受 drain 並以 status\drain.json 回 id；admin preflight 以只讀 db／config、只能寫 %TEMP% 的身分執行；
/// SCM 停止時 30 秒內結束；記憶體保險絲以 ServiceBase.ExitCode 非 0 結束。
/// </summary>
public sealed class DeployInterfaceTests
{
    /// <summary>
    /// Install-MyriagentHubRelease.ps1 在 Windows PowerShell 5.1 以 ConvertTo-Json 寫出的 drain 要求（照抄 5.1 的格式：CRLF、四個空白縮排、
    /// 冒號後兩個空白；requestedAt 是 (Get-Date).ToUniversalTime().ToString('o')；id 是 drain-&lt;release&gt;-&lt;yyyyMMddHHmmss&gt;）
    /// </summary>
    internal static string PowerShellDrainRequest(string id, string release, DateTimeOffset requestedAt, string command = "drain") =>
        "{\r\n"
        + "    \"command\":  \"" + command + "\",\r\n"
        + "    \"id\":  \"" + id + "\",\r\n"
        + "    \"release\":  \"" + release + "\",\r\n"
        + "    \"retryAfterSec\":  30,\r\n"
        + "    \"requestedAt\":  \"" + requestedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) + "\"\r\n"
        + "}";

    [Fact]
    public void 部署收件匣的要求格式_安裝腳本寫的drain解析得出來_其他指令與壞格式不接受()
    {
        var now = new DateTimeOffset(2026, 10, 11, 7, 20, 0, TimeSpan.Zero).AddTicks(1234567);
        var request = AdminInboxRules.ParseDeploy(PowerShellDrainRequest("drain-0.2.0-1a2b3c4-20261011072000", "0.2.0-1a2b3c4", now), out var error);
        error.Should().BeNull();
        request.Should().NotBeNull();
        request!.Id.Should().Be("drain-0.2.0-1a2b3c4-20261011072000", "id 含 release 的「.」");
        request.Release.Should().Be("0.2.0-1a2b3c4");
        request.RetryAfterSec.Should().Be(30);
        request.RequestedAt.Should().Be(now.ToUnixTimeMilliseconds());

        AdminInboxRules.ParseDeploy("{\"COMMAND\":\"Drain\",\"Id\":\"x-1\",\"requestedAt\":1760000000000}", out error)!.RequestedAt.Should().Be(1760000000000, "屬性名稱不分大小寫、時間也可以是 Unix 毫秒");
        AdminInboxRules.ParseDeploy("{\"command\":\"drain\",\"id\":\"x-1\",\"requestedAt\":1,\"retryAfterSec\":99999}", out _)!.RetryAfterSec.Should().Be(3600);

        foreach (var command in new[] { "reset-owner", "owner-reset", "stop", "undrain", "cert-renew", "backup-now", "selftest", "verify-audit", "rm -rf" })
        {
            AdminInboxRules.ParseDeploy(PowerShellDrainRequest("x-1", "r", now, command), out error).Should().BeNull(command);
            error.Should().Contain("只接受 drain");
        }
        AdminInboxRules.ParseDeploy(PowerShellDrainRequest("..\\..\\x", "r", now), out error).Should().BeNull("id 不能有路徑字元");
        AdminInboxRules.ParseDeploy(PowerShellDrainRequest("a..b", "r", now), out error).Should().BeNull();
        AdminInboxRules.ParseDeploy("{\"command\":\"drain\",\"id\":\"x-1\"}", out error).Should().BeNull("一定要有 requestedAt（過期判斷）");
        AdminInboxRules.ParseDeploy("{\"command\":\"drain\",\"id\":\"x-1\",\"requestedAt\":\"昨天\"}", out error).Should().BeNull();
        AdminInboxRules.ParseDeploy("[1,2]", out error).Should().BeNull();
        AdminInboxRules.ParseDeploy("not json", out error).Should().BeNull();
        AdminInboxRules.IsValidDeployId(new string('a', 129)).Should().BeFalse();
    }

    [Fact]
    public async Task deploy收件匣的drain_回status_drain_json的id_readyz回503_稽核記部署()
    {
        await using var owner = await OwnerHost.StartAsync();
        var paths = owner.Host.Paths;
        var id = "drain-0.2.0-1a2b3c4-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        WriteRequest(paths.AdminInboxDeployDir, id, PowerShellDrainRequest(id, "0.2.0-1a2b3c4", DateTimeOffset.UtcNow));

        var ack = await WaitForJsonAsync(paths.DrainAckFile, j => j.GetProperty("id").GetString() == id);
        ack.GetProperty("ok").GetBoolean().Should().BeTrue();
        ack.GetProperty("state").GetString().Should().Be("draining");
        ack.GetProperty("release").GetString().Should().Be("0.2.0-1a2b3c4");
        ack.GetProperty("retryAfterSec").GetInt32().Should().Be(30);
        Directory.GetFiles(paths.AdminInboxDeployDir).Should().BeEmpty("處理完的要求刪除");
        Directory.GetFiles(paths.AdminResultsDir).Should().BeEmpty("部署的 drain 不寫管理結果檔（runner 讀不到那裡）");

        var drain = owner.Host.Services.GetRequiredService<DrainState>();
        drain.IsDraining.Should().BeTrue();
        drain.RetryAfterSec.Should().Be(30);
        drain.Current.Source.Should().Be(DrainSource.Deploy);
        drain.Current.ExpiresAt.Should().Be(owner.Host.Clock.NowMs + DrainRules.DefaultDeployDrainMaxSeconds * 1000L, "部署的排空有期限（預設 300 秒）");
        drain.Current.RequestId.Should().Be(id);
        ack.GetProperty("message").GetString().Should().Contain("300 秒後");
        using (var local = owner.Host.CreateClient(ip: "127.0.0.1", cookies: false))
        using (var ready = await local.GetAsync("/readyz"))
            ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var audit = await owner.Host.Services.GetRequiredService<AuditLog>().ListAsync(null, 50);
        audit.Should().Contain(a => a.ActorType == AuditActors.Deploy && a.Action == AuditActions.AdminCommand && a.Target == "drain" && a.Outcome == AuditOutcomes.Ok);
    }

    [Theory]
    [InlineData("reset-owner")]
    [InlineData("stop")]
    [InlineData("undrain")]
    [InlineData("cert-renew")]
    public async Task deploy收件匣只接受drain_其他指令不執行_記稽核後刪除(string command)
    {
        await using var owner = await OwnerHost.StartAsync();
        var paths = owner.Host.Paths;
        var stopping = false;
        owner.Host.App.Lifetime.ApplicationStopping.Register(() => stopping = true);
        var id = "deploy-" + command;
        WriteRequest(paths.AdminInboxDeployDir, id, PowerShellDrainRequest(id, "0.2.0-1a2b3c4", DateTimeOffset.UtcNow, command));

        await WaitUntilAsync(() => Directory.GetFiles(paths.AdminInboxDeployDir).Length == 0);
        var audit = await owner.Host.Services.GetRequiredService<AuditLog>().ListAsync(null, 50);
        audit.Should().Contain(a => a.ActorType == AuditActors.Deploy && a.Target == "deploy-inbox" && a.Outcome == AuditOutcomes.Denied);
        audit.Should().NotContain(a => a.Action == AuditActions.AdminCommand && a.Target == command, "沒有執行");
        File.Exists(paths.DrainAckFile).Should().BeFalse("不是 drain 就不回覆");
        audit.Should().NotContain(a => a.Action == AuditActions.OwnerReset, "reset-owner 沒有執行");
        stopping.Should().BeFalse("stop 沒有執行");
        owner.Host.Services.GetRequiredService<DrainState>().IsDraining.Should().BeFalse();
    }

    /// <summary>
    /// 部署排空的期限（Hub:Deploy:DrainMaxSeconds）與安裝腳本的預算是同一個介面的兩邊：安裝腳本 drain 之後最多等 DrainWaitSec、
    /// 停服務最多等 StopTimeoutSec，期限的下限要超過這整段時間，正常部署才不會在停好之前就被自動解除。
    /// 停服務失敗（stop-failed）的訊息依服務狀態而不同，由 tools/deploy/Test-MyriagentHubDeploy.ps1 實際執行安裝腳本檢查
    /// （S13：停止要求沒送到、Running → 會自己解除；S14：StopPending → 不會解除、要管理員啟動）；這裡比對腳本全文抓不到訊息被改掉
    /// （同一句也出現在註解與介面說明裡）。
    /// </summary>
    [Fact]
    public void 部署排空的期限遠大於安裝腳本drain之後到停好服務的預算()
    {
        var script = File.ReadAllText(RepoFiles.Combine("tools", "deploy", "Install-MyriagentHubRelease.ps1"));
        int Default(string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(script, @"\[int\]\$" + name + @"\s*=\s*(\d+)");
            match.Success.Should().BeTrue($"安裝腳本有參數 {name} 的預設值");
            return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        var budget = Default("DrainWaitSec") + Default("StopTimeoutSec");
        budget.Should().Be(40, "安裝腳本的預設是 DrainWaitSec 10＋StopTimeoutSec 30；改了要重新檢查期限的上下限");
        DrainRules.MinDeployDrainMaxSeconds.Should().BeGreaterThan(budget, "設定允許的最小值也要超過 drain 之後到停好服務的整段時間");
        DrainRules.DefaultDeployDrainMaxSeconds.Should().BeGreaterThanOrEqualTo(5 * budget, "預設值遠大於（至少 5 倍）");
        new HubOptions().Deploy.DrainMaxSeconds.Should().Be(DrainRules.DefaultDeployDrainMaxSeconds);
    }

    /// <summary>
    /// 安裝腳本等不到 drain 的回覆（收件匣逐一處理，前面有慢的管理指令時就來不及）照樣停服務，要求可能留在收件匣。新版在 10 分鐘內
    /// 啟動時不能執行它：那是寫給上一個程序的，執行了新版一啟動就排空（/readyz 503），而健康閘門不看排空，部署照樣回報成功。
    /// </summary>
    [Fact]
    public async Task deploy收件匣裡本程序啟動之前寫的drain_是給上一個程序的_不執行()
    {
        const long At = 1_760_000_000_000;
        var request = new DeployRequest("drain-x", "0.2.0-1a2b3c4", 30, At);
        AdminInboxRules.IsFromEarlierInstance(request, At + 1).Should().BeTrue();
        AdminInboxRules.IsFromEarlierInstance(request, At).Should().BeFalse("同一毫秒寫的算這個程序的");
        AdminInboxRules.IsFromEarlierInstance(request, At - 1).Should().BeFalse();

        var dir = TempDir.Create();
        try
        {
            var paths = new HubPaths(dir);
            Directory.CreateDirectory(paths.AdminInboxDeployDir);
            var stale = "drain-0.2.0-1a2b3c4-stale";
            WriteRequest(paths.AdminInboxDeployDir, stale, PowerShellDrainRequest(stale, "0.2.0-1a2b3c4", DateTimeOffset.UtcNow.AddSeconds(-30)));
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                await WaitUntilAsync(() => Directory.GetFiles(paths.AdminInboxDeployDir).Length == 0);
                var drain = host.Services.GetRequiredService<DrainState>();
                drain.IsDraining.Should().BeFalse("寫給上一個程序的 drain（還在 10 分鐘內）不讓剛啟動的新版排空");
                File.Exists(paths.DrainAckFile).Should().BeFalse("不回覆：寫它的安裝腳本早就不等了");

                var fresh = "drain-0.2.0-1a2b3c4-fresh";
                WriteRequest(paths.AdminInboxDeployDir, fresh, PowerShellDrainRequest(fresh, "0.2.0-1a2b3c4", DateTimeOffset.UtcNow));
                var ack = await WaitForJsonAsync(paths.DrainAckFile, j => j.GetProperty("id").GetString() == fresh);
                ack.GetProperty("ok").GetBoolean().Should().BeTrue("本程序啟動之後寫的照樣執行");
                drain.Current.RequestId.Should().Be(fresh);
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task deploy收件匣放太久的drain作廢_不排空()
    {
        await using var host = await HubTestHost.StartAsync();
        WriteRequest(host.Paths.AdminInboxDeployDir, "drain-old", PowerShellDrainRequest("drain-old", "0.2.0-1a2b3c4", DateTimeOffset.UtcNow.AddMinutes(-30)));
        await WaitUntilAsync(() => Directory.GetFiles(host.Paths.AdminInboxDeployDir).Length == 0);
        File.Exists(host.Paths.DrainAckFile).Should().BeFalse();
        host.Services.GetRequiredService<DrainState>().IsDraining.Should().BeFalse();
    }

    [Fact]
    public async Task preflight_服務停止後_只有db與config讀取權_只寫暫存資料夾也能跑()
    {
        var dir = TempDir.Create();
        var work = TempDir.Create("myriagenthub-preflight-temp");
        try
        {
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                using var client = host.CreateClient();
                await OwnerFlows.LoginOwnerAsync(client);
            }
            var paths = new HubPaths(dir);
            File.Exists(paths.HubDb + "-wal").Should().BeFalse("正常停止時 WAL 併回主檔並刪除：唯讀連線開不了，要用 immutable");
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigDir, "appsettings.Production.json"), "{\"Hub\":{\"ServerDomain\":\"hub.example.com\",\"HttpsPort\":443}}");
            var before = Snapshot(dir);

            PreflightResult result;
            using (ReadOnlyAccess.Apply(dir))
            {
                ReadOnlyAccess.CanWrite(paths.DbDir).Should().BeFalse("模擬 runner 帳戶：資料根寫不進去");
                ReadOnlyAccess.CanWrite(paths.TmpDir).Should().BeFalse();
                result = await Preflight.RunAsync(paths, work, CancellationToken.None);
            }

            result.Ok.Should().BeTrue(Describe(result));
            result.Checks.Single(c => c.Name == "db.migrate_copy").Detail.Should().Contain("snapshot=immutable");
            result.Checks.Single(c => c.Name == "temp.writable").Detail.Should().StartWith(work);
            result.Checks.Single(c => c.Name == "config.server_domain").Detail.Should().Be("hub.example.com", "讀得到資料根的 config");
            result.Build.Should().Be(BuildInfo.Current.Build);
            Snapshot(dir).Should().Equal(before, "preflight 不寫資料根（連暫存檔都不放）");
            Directory.GetFileSystemEntries(work).Should().BeEmpty("暫存副本用完即刪");
        }
        finally
        {
            TempDir.TryDelete(work);
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task preflight_服務執行中_另一個程序以只讀身分執行admin_preflight_讀得到WAL裡的交易()
    {
        var dir = TempDir.Create();
        var childTemp = TempDir.Create("myriagenthub-preflight-temp");
        try
        {
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                await host.Services.GetRequiredService<KvStore>().SetAsync("preflight_probe", 42);
                // 連線不用池，但服務開著保活連線（HubDatabase）：操作的連線關閉都不是最後一條，WAL 一直在
                File.Exists(host.Paths.HubDb + "-wal").Should().BeTrue("服務執行中：WAL 開著");
                new FileInfo(host.Paths.HubDb + "-wal").Length.Should().BeGreaterThan(0, "WAL 裡有還沒併回主檔的交易");

                int exitCode;
                string stdout;
                string stderr;
                using (ReadOnlyAccess.Apply(dir))
                    (exitCode, stdout, stderr) = await RunHubExeAsync(["admin", "preflight", "--data", dir], childTemp);

                exitCode.Should().Be(AdminExitCodes.Ok, stdout + stderr);
                var json = JsonDocument.Parse(stdout).RootElement;
                json.GetProperty("ok").GetBoolean().Should().BeTrue(stdout);
                var migrate = json.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "db.migrate_copy");
                migrate.GetProperty("detail").GetString().Should().Contain("snapshot=readonly").And.Contain("integrity=ok");
                json.GetProperty("pendingMigrations").GetArrayLength().Should().Be(0, "服務啟動時已套用所有遷移，副本也是");
                Directory.GetFileSystemEntries(childTemp).Should().BeEmpty("子程序只用自己的 %TEMP%，用完即刪");
            }
        }
        finally
        {
            TempDir.TryDelete(childTemp);
            TempDir.TryDelete(dir);
        }
    }

    /// <summary>
    /// immutable 讀取不上鎖：沒有 -wal 時（服務已停止，或剛好在啟動、結束，保活連線還沒開或已關）服務仍可能在讀取途中
    /// 開連線、寫入、關閉（最後一條關閉時做檢查點改到主檔）。讀前讀後比對主檔的 SHA-256，不一致就重來，拿到寫入之後的一致資料。
    /// </summary>
    [Fact]
    public void preflight的immutable快照_讀取途中主檔被改就重來()
    {
        var dir = TempDir.Create();
        var work = TempDir.Create("myriagenthub-preflight-temp");
        try
        {
            var db = CreateWalDatabaseWithoutWal(dir);
            var target = Path.Combine(work, "copy.db");
            var reads = 0;
            var access = ReadOnlyAccess.Apply(dir);
            string method;
            try
            {
                method = Preflight.SnapshotReadOnly(db, target, (attempt, read) =>
                {
                    read();
                    reads++;
                    if (attempt != 1)
                        return;
                    // 第一次 immutable 讀完、比對之前：「服務」開連線寫一筆再關閉（檢查點把它寫進主檔、刪掉 WAL）
                    access.Dispose();
                    using (var service = SqliteSetup.Open(db))
                        SqliteSetup.Exec(service, "INSERT INTO t(v) VALUES (2);");
                    access = ReadOnlyAccess.Apply(dir);
                });
            }
            finally
            {
                access.Dispose();
            }

            method.Should().Be("immutable,attempts=2");
            reads.Should().Be(2);
            using var copy = SqliteSetup.Open(target);
            SqliteSetup.Scalar(copy, "SELECT COUNT(*) FROM t").Should().Be(2L, "重來的那一次讀到寫入之後的一致資料");
        }
        finally
        {
            TempDir.TryDelete(work);
            TempDir.TryDelete(dir);
        }
    }

    /// <summary>
    /// 讀取途中主檔被改時 SQLite 可能讀到前後不一致的頁面而丟出例外（database disk image is malformed），根本走不到讀完之後的比對。
    /// immutable 讀取失敗也要先比對主檔：變了就照樣重來，不能以「資料庫損毀」擋下部署。
    /// </summary>
    [Fact]
    public void preflight的immutable快照_讀取途中主檔被改而讀取失敗_照樣重來()
    {
        var dir = TempDir.Create();
        var work = TempDir.Create("myriagenthub-preflight-temp");
        try
        {
            var db = CreateWalDatabaseWithoutWal(dir);
            var target = Path.Combine(work, "copy.db");
            var reads = 0;
            var access = ReadOnlyAccess.Apply(dir);
            string method;
            try
            {
                method = Preflight.SnapshotReadOnly(db, target, (attempt, read) =>
                {
                    reads++;
                    if (attempt != 1)
                    {
                        read();
                        return;
                    }
                    // 第一次 immutable 讀取途中：「服務」開連線寫一筆再關閉（檢查點改到主檔），讀取因此失敗
                    access.Dispose();
                    using (var service = SqliteSetup.Open(db))
                        SqliteSetup.Exec(service, "INSERT INTO t(v) VALUES (2);");
                    access = ReadOnlyAccess.Apply(dir);
                    throw new SqliteException("database disk image is malformed", SqliteCorrupt);
                });
            }
            finally
            {
                access.Dispose();
            }

            method.Should().Be("immutable,attempts=2", "主檔變了：讀取失敗是讀到一半被改，不是損毀");
            reads.Should().Be(2);
            using var copy = SqliteSetup.Open(target);
            SqliteSetup.Scalar(copy, "SELECT COUNT(*) FROM t").Should().Be(2L);
        }
        finally
        {
            TempDir.TryDelete(work);
            TempDir.TryDelete(dir);
        }
    }

    /// <summary>主檔沒變、也沒有 WAL 而 immutable 讀取失敗：資料庫真的損毀，照實丟出 SQLite 的例外（不重來、不包成「一直被改」）</summary>
    [Fact]
    public void preflight的immutable快照_主檔沒變而讀取失敗_是真的損毀_照實回報不重來()
    {
        var dir = TempDir.Create();
        var work = TempDir.Create("myriagenthub-preflight-temp");
        try
        {
            var db = CreateWalDatabaseWithoutWal(dir, rows: 200);
            long pageSize;
            long pages;
            using (var connection = SqliteSetup.Open(db))
            {
                pageSize = (long)SqliteSetup.Scalar(connection, "PRAGMA page_size")!;
                pages = (long)SqliteSetup.Scalar(connection, "PRAGMA page_count")!;
            }
            pages.Should().BeGreaterThan(3, "表 t 要跨好幾頁：第 3 頁是它的資料頁");
            using (var file = new FileStream(db, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                // 第 3 頁開頭的 B-tree 頁型別改成不存在的值
                file.Position = 2 * pageSize;
                file.WriteByte(0xFF);
            }
            var target = Path.Combine(work, "copy.db");
            var reads = 0;
            SqliteException failure;
            using (ReadOnlyAccess.Apply(dir))
            {
                var snapshot = () => Preflight.SnapshotReadOnly(db, target, (_, read) =>
                {
                    reads++;
                    read();
                });
                failure = snapshot.Should().Throw<SqliteException>().Which;
            }

            failure.SqliteErrorCode.Should().Be(SqliteCorrupt, failure.Message);
            reads.Should().Be(1, "主檔沒變：不重來");
        }
        finally
        {
            TempDir.TryDelete(work);
            TempDir.TryDelete(dir);
        }
    }

    /// <summary>
    /// 只比雜湊不夠：服務在讀取途中啟動、開了保活連線並把交易寫進 WAL 時主檔不變，immutable 快照會漏掉那些已提交的交易。
    /// 讀完要確認仍然沒有 WAL，有就重來；這時 -wal、-shm 都在，唯讀連線開得了，讀得到 WAL 裡的交易。
    /// </summary>
    [Fact]
    public void preflight的immutable快照_讀取途中服務把交易寫進WAL_主檔沒變也重來_改走readonly()
    {
        var dir = TempDir.Create();
        var work = TempDir.Create("myriagenthub-preflight-temp");
        SqliteConnection? service = null;
        try
        {
            var db = CreateWalDatabaseWithoutWal(dir);
            var mainBefore = BackupService.Sha256File(db);
            var target = Path.Combine(work, "copy.db");
            var reads = 0;
            var access = ReadOnlyAccess.Apply(dir);
            string method;
            try
            {
                method = Preflight.SnapshotReadOnly(db, target, (attempt, read) =>
                {
                    read();
                    reads++;
                    if (attempt != 1)
                        return;
                    // 第一次 immutable 讀完、比對之前：「服務」啟動、開著連線寫一筆（交易在 WAL 裡，主檔不變）
                    access.Dispose();
                    service = SqliteSetup.Open(db);
                    SqliteSetup.Exec(service, "INSERT INTO t(v) VALUES (2);");
                    access = ReadOnlyAccess.Apply(dir);
                });
            }
            finally
            {
                access.Dispose();
            }

            BackupService.Sha256File(db).Should().Be(mainBefore, "交易還在 WAL：主檔一個位元組都沒變，只比雜湊抓不到");
            new FileInfo(db + "-wal").Length.Should().BeGreaterThan(0);
            reads.Should().Be(1, "immutable 只讀了一次，第二次改走唯讀連線");
            method.Should().Be("readonly");
            using var copy = SqliteSetup.Open(target);
            SqliteSetup.Scalar(copy, "SELECT COUNT(*) FROM t").Should().Be(2L, "讀得到 WAL 裡已提交的交易");
        }
        finally
        {
            service?.Dispose();
            TempDir.TryDelete(work);
            TempDir.TryDelete(dir);
        }
    }

    /// <summary>SQLITE_CORRUPT（database disk image is malformed）</summary>
    private const int SqliteCorrupt = 11;

    /// <summary>建一個 WAL 模式、沒有連線開著（所以沒有 -wal、-shm）的資料庫：表 t 有 <paramref name="rows"/> 筆</summary>
    private static string CreateWalDatabaseWithoutWal(string dir, int rows = 1)
    {
        var db = Path.Combine(dir, "hub.db");
        using (var connection = SqliteSetup.Open(db))
        {
            SqliteSetup.Exec(connection, "PRAGMA journal_mode=WAL; CREATE TABLE t(v INTEGER, pad BLOB);");
            SqliteSetup.Exec(connection, $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {rows.ToString(CultureInfo.InvariantCulture)}) INSERT INTO t SELECT i, randomblob(200) FROM n;");
        }
        File.Exists(db + "-wal").Should().BeFalse("沒有連線開著：最後一條關閉時已做完檢查點並刪掉 WAL");
        return db;
    }

    [Fact]
    public async Task SCM停止時30秒內優雅結束_主機最多等25秒_WAL併回主檔()
    {
        HostingRules.ShutdownTimeout.Should().BeLessThan(HostingRules.ServiceStopBudget, "留時間給程序本身結束");
        var dir = TempDir.Create();
        try
        {
            var host = await HubTestHost.StartAsync(dataDir: dir);
            host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout.Should().Be(HostingRules.ShutdownTimeout);
            await host.Services.GetRequiredService<KvStore>().SetAsync("stop_probe", 1);
            var sw = Stopwatch.StartNew();
            await host.DisposeAsync();
            sw.Elapsed.Should().BeLessThan(HostingRules.ShutdownTimeout);
            var paths = new HubPaths(dir);
            JsonDocument.Parse(await File.ReadAllTextAsync(paths.RunningFile)).RootElement.GetProperty("state").GetString().Should().Be("stopped");
            (File.Exists(paths.HubDb + "-wal") ? new FileInfo(paths.HubDb + "-wal").Length : 0).Should().Be(0);
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 記憶體保險絲以ServiceBase_ExitCode非0結束_停不下來才直接結束程序()
    {
        MemoryFuse.ExitCode.Should().NotBe(0);
        using var service = new FakeServiceLifetime();
        using var services = new ServiceCollection().AddSingleton<IHostLifetime>(service).BuildServiceProvider();
        var lifetime = new FakeApplicationLifetime();
        var hardExit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await MemoryFuse.RequestFailureExit(lifetime, services, NullLogger.Instance, MemoryFuse.ExitCode, TimeSpan.FromMilliseconds(50), code => hardExit.TrySetResult(code));
            service.ExitCode.Should().Be(MemoryFuse.ExitCode, "以服務執行時 SCM 只看服務回報的結束碼（配合 sc.exe failureflag MyriagentHub 1 重啟）");
            Environment.ExitCode.Should().Be(MemoryFuse.ExitCode, "主控台執行時 Main 回傳它");
            lifetime.StopRequested.Should().BeTrue("先正常停止");
            (await hardExit.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(MemoryFuse.ExitCode, "停不下來才直接結束程序，結束碼一樣非 0");
        }
        finally
        {
            Environment.ExitCode = 0;
        }
    }

    internal static void WriteRequest(string dir, string id, string json)
    {
        // 與安裝腳本相同：先寫 .tmp 再改名，服務不會讀到寫一半的檔
        var temp = Path.Combine(dir, id + ".tmp");
        File.WriteAllText(temp, json);
        File.Move(temp, Path.Combine(dir, id + ".json"));
    }

    internal static async Task<JsonElement> WaitForJsonAsync(string path, Func<JsonElement, bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
                    if (condition(json))
                        return json;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("等不到 " + path);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(50);
        condition().Should().BeTrue("10 秒內應該處理完");
    }

    /// <summary>資料根裡每個檔的相對路徑、大小與最後寫入時間（判斷有沒有被寫過）</summary>
    private static List<string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => new FileInfo(f))
            .Select(f => $"{Path.GetRelativePath(dir, f.FullName)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}")
            .Order(StringComparer.Ordinal)
            .ToList();

    private static string Describe(PreflightResult result) =>
        string.Join("; ", result.Checks.Select(c => $"{c.Name}={(c.Ok ? "ok" : "FAIL")}:{c.Detail}"));

    /// <summary>以子程序執行測試輸出資料夾裡的 MyriagentHub.exe（另一個程序＝另一組 SQLite 共用記憶體，才會走唯讀 -shm 的路）</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunHubExeAsync(string[] args, string tempDir)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "MyriagentHub.exe");
        File.Exists(exe).Should().BeTrue($"測試輸出資料夾要有 MyriagentHub.exe：{exe}");
        var start = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = tempDir,
        };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        start.Environment["TEMP"] = tempDir;
        start.Environment["TMP"] = tempDir;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("MyriagentHub.exe " + string.Join(' ', args) + " 120 秒沒有結束");
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    private sealed class FakeServiceLifetime : ServiceBase, IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeApplicationLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopRequested = true;
    }
}
