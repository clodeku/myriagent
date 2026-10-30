using System.Net;
using System.Text.Json;
using MyriagentHub.Admin;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using MyriagentHub.Auth;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Ops;

/// <summary>每日備份（VACUUM INTO＋integrity_check＋manifest）與 admin restore（hub.db）</summary>
public sealed class RestoreTests
{
    [Fact]
    public async Task 備份產生一致快照與manifest_筆數與SHA256可核對()
    {
        await using var owner = await OwnerHost.StartAsync();
        var backup = owner.Host.Services.GetRequiredService<BackupService>();
        var result = await backup.RunAsync("test", scheduled: false);
        result.Ok.Should().BeTrue(result.Error);

        var manifest = BackupService.ReadManifest(result.Folder!)!;
        var hub = manifest.Files.Single(f => f.Name == "hub.db");
        hub.Integrity.Should().Be("ok");
        hub.Sha256.Should().Be(BackupService.Sha256File(Path.Combine(result.Folder!, "hub.db")));
        hub.Tables["AspNetUsers"].Should().Be(1, "首次啟動建立擁有者帳號 admin");
        hub.Tables.Should().ContainKey("audit");
        // 停用的資料表留到下一版的遷移才移除（部署回滾相容），一律是空的
        hub.Tables["AspNetUserPasskeys"].Should().Be(0);
        hub.Tables["owner_key_log"].Should().Be(0);
        var sync = manifest.Files.Single(f => f.Name == "sync.db");
        sync.Integrity.Should().Be("ok");
        sync.Sha256.Should().Be(BackupService.Sha256File(Path.Combine(result.Folder!, "sync.db")));
        sync.Tables.Should().ContainKeys("events", "server_meta", "logical_accounts");
        // WP4.7 D18：blob 不備份，只多一份清單（不在 manifest 的 Files，還原不放回）
        Directory.GetFiles(result.Folder!).Select(Path.GetFileName).Should().BeEquivalentTo("hub.db", "sync.db", "manifest.json", BackupService.BlobInventoryFile);
        backup.LastState!.LastSuccessAt.Should().NotBeNull();
    }

    [Fact]
    public async Task 停機後還原_資料回到快照_restore_generation加1_工作階段全部撤銷_稽核記一筆()
    {
        var dir = TempDir.Create();
        try
        {
            const string afterBackup = "changed-after-backup";
            string folder;
            string cookie;
            await using (var owner = await StartOwnerAsync(dir))
            {
                using var client = owner.Host.CreateClient();
                using (var login = await OwnerFlows.LoginAsync(client))
                    login.StatusCode.Should().Be(HttpStatusCode.OK);
                cookie = client.Cookies!.Container.GetCookieHeader(new Uri("https://localhost"));
                var result = await owner.Host.Services.GetRequiredService<BackupService>().RunAsync("test", scheduled: false);
                result.Ok.Should().BeTrue();
                folder = result.Folder!;

                // 備份之後才發生的事：改密碼（還原後應該回到備份時的密碼）
                using (var changed = await OwnerFlows.ChangePasswordAsync(client, OwnerAccount.DefaultPassword, afterBackup))
                    changed.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            var restored = RestoreService.Restore(new HubPaths(dir), folder, TimeProvider.System);
            restored.Generation.Should().Be(1);
            restored.RevokedSessions.Should().BeGreaterThan(0);
            File.Exists(Path.Combine(restored.PreRestoreFolder, "hub.db")).Should().BeTrue("被換掉的資料庫留在 pre-restore");

            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                var generation = await host.Services.GetRequiredService<KvStore>().GetAsync<long>(KvKeys.RestoreGeneration);
                generation.Should().Be(1);
                (await host.Services.GetRequiredService<AuditLog>().VerifyAsync()).Ok.Should().BeTrue("還原時附加的稽核列接得上雜湊鏈");
                var last = await CountAsync(host, db => db.Audit.OrderByDescending(a => a.Seq).Select(a => a.Action).FirstAsync());
                last.Should().BeOneOf(AuditActions.Restore, AuditActions.VersionChanged);
                (await CountAsync(host, db => db.Audit.CountAsync(a => a.Action == AuditActions.Restore))).Should().Be(1);

                // 還原會讓之後才撤銷的工作階段復活，所以全部撤銷：舊 cookie 一律 401
                using var replay = host.CreateClient(cookies: false);
                replay.Http.DefaultRequestHeaders.Add("Cookie", cookie);
                using var me = await replay.GetAsync("/api/v1/auth/me");
                me.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

                using var browser = host.CreateClient();
                using (var newer = await OwnerFlows.LoginAsync(browser, afterBackup))
                    newer.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "資料回到快照：備份之後改的密碼不見了");
                using (var older = await OwnerFlows.LoginAsync(browser))
                    older.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            // 第二次還原：generation 再加 1
            RestoreService.Restore(new HubPaths(dir), folder, TimeProvider.System).Generation.Should().Be(2);
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 還原後sync_db同一個世代_新事件的seq跳過10億_每台裝置標成要對帳()
    {
        var dir = TempDir.Create();
        try
        {
            string folder;
            string instanceId;
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                var writer = host.Services.GetRequiredService<SyncWriter>();
                instanceId = writer.Instance.InstanceId;
                await writer.WriteAsync(c =>
                {
                    c.Execute("INSERT INTO device_streams(device_id, next_device_seq) VALUES('d_A', 5)");
                    c.Execute("INSERT INTO events(stream, cls, author, kind, payload, created_at) VALUES('catalog', 'catalog', 'server', 'prefs.set', '{}', 0)");
                });
                var result = await host.Services.GetRequiredService<BackupService>().RunAsync("test", scheduled: false);
                result.Ok.Should().BeTrue(result.Error);
                folder = result.Folder!;
                // 備份之後才發出的事件：還原後它的 seq 不能被新事件重用
                await writer.WriteAsync(c => c.Execute("INSERT INTO events(stream, cls, author, kind, payload, created_at) VALUES('catalog', 'catalog', 'server', 'prefs.set', '{}', 0)"));
            }

            var restored = RestoreService.Restore(new HubPaths(dir), folder, TimeProvider.System);
            restored.Files.Should().Contain("sync.db");
            restored.SyncSeqFloor.Should().Be(2 + SyncSchema.RestoreSeqJump, "起點取備份與還原前線上那份（發到 2）較大者再加 10 億");

            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                var writer = host.Services.GetRequiredService<SyncWriter>();
                writer.Instance.Should().Be(new Myriagent.Sync.ServerInstance(instanceId, restored.Generation), "資料庫是同一個（instanceId 不變），世代與 hub.db 相同");
                var seq = await writer.WriteAsync(c =>
                {
                    c.Execute("INSERT INTO events(stream, cls, author, kind, payload, created_at) VALUES('catalog', 'catalog', 'server', 'prefs.set', '{}', 0)");
                    return (long)c.Scalar("SELECT last_insert_rowid()")!;
                });
                seq.Should().Be(restored.SyncSeqFloor + 1);
                using var read = writer.OpenRead();
                SqliteSetup.Scalar(read, "SELECT needs_resync FROM device_streams WHERE device_id = 'd_A'").Should().Be(1L);
                SqliteSetup.Scalar(read, "SELECT next_device_seq FROM device_streams WHERE device_id = 'd_A'").Should().Be(5L);
            }

            // 同一份備份再還原一次：上一次還原之後發過的 seq（起點＋1）也不能被重用
            var again = RestoreService.Restore(new HubPaths(dir), folder, TimeProvider.System);
            again.SyncSeqFloor.Should().Be(restored.SyncSeqFloor + 1 + SyncSchema.RestoreSeqJump);
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 服務執行中拒絕還原_admin指令結束碼3()
    {
        await using var owner = await OwnerHost.StartAsync();
        var result = await owner.Host.Services.GetRequiredService<BackupService>().RunAsync("test", scheduled: false);
        var act = () => RestoreService.Restore(owner.Host.Paths, result.Folder!, TimeProvider.System);
        act.Should().Throw<RestoreException>().Which.Code.Should().Be("service_running");

        using var output = new StringWriter();
        var code = await AdminCommandLine.RunAsync(["restore", "--data", owner.Host.DataDir, "--from", result.Folder!], output);
        code.Should().Be(AdminExitCodes.ServiceRunning);
        output.ToString().Should().Contain("service_running");
    }

    [Fact]
    public async Task 備份被竄改或不完整時拒絕還原_線上資料庫不動()
    {
        var dir = TempDir.Create();
        try
        {
            string folder;
            await using (var owner = await StartOwnerAsync(dir))
                folder = (await owner.Host.Services.GetRequiredService<BackupService>().RunAsync("test", scheduled: false)).Folder!;
            var paths = new HubPaths(dir);
            var before = BackupService.Sha256File(paths.HubDb);

            var copy = Path.Combine(dir, "tampered");
            Directory.CreateDirectory(copy);
            File.Copy(Path.Combine(folder, "manifest.json"), Path.Combine(copy, "manifest.json"));
            var bytes = await File.ReadAllBytesAsync(Path.Combine(folder, "hub.db"));
            bytes[^100] ^= 0xFF;
            await File.WriteAllBytesAsync(Path.Combine(copy, "hub.db"), bytes);
            var tampered = () => RestoreService.Restore(paths, copy, TimeProvider.System);
            tampered.Should().Throw<RestoreException>().Which.Code.Should().Be("hash_mismatch");

            File.Delete(Path.Combine(copy, "manifest.json"));
            var incomplete = () => RestoreService.Restore(paths, copy, TimeProvider.System);
            incomplete.Should().Throw<RestoreException>().Which.Code.Should().Be("manifest_missing");

            BackupService.Sha256File(paths.HubDb).Should().Be(before, "驗證失敗時線上資料庫一個位元組都不動");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task admin_restore指令以JSON回報結果()
    {
        var dir = TempDir.Create();
        try
        {
            string folder;
            await using (var owner = await StartOwnerAsync(dir))
                folder = (await owner.Host.Services.GetRequiredService<BackupService>().RunAsync("test", scheduled: false)).Folder!;
            using var output = new StringWriter();
            var code = await AdminCommandLine.RunAsync(["restore", "--data", dir, "--from", folder], output);
            code.Should().Be(AdminExitCodes.Ok, output.ToString());
            var json = JsonDocument.Parse(output.ToString()).RootElement;
            json.GetProperty("ok").GetBoolean().Should().BeTrue();
            json.GetProperty("result").GetProperty("generation").GetInt64().Should().Be(1);
            output.ToString().Should().MatchRegex("^[\\x00-\\x7F]*$", "輸出只有 ASCII（經 ssh 的 cp950 主控台不會亂碼）");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    // ───── 第 4 階段：對話標成 recovering（WP4.2 步 5、D16） ─────

    [Fact]
    public void 還原v1備份_沒有conversations表_不丟例外()
    {
        var dir = TempDir.Create();
        try
        {
            var path = Path.Combine(dir, "sync.db");
            using (var connection = SqliteSetup.Open(path))
            {
                SqliteSetup.Exec(connection, SyncSchema.V1);
                SqliteSetup.Exec(connection, "INSERT INTO device_streams(device_id, next_device_seq) VALUES('d_A', 3)");
            }

            var floor = RestoreService.MarkSyncRestored(path, 4);

            floor.Should().Be(SyncSchema.RestoreSeqJump);
            using var read = SqliteSetup.Open(path, readOnly: true);
            SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'conversations'").Should().Be(0L, "v1 的檔不建第 4 階段的表");
            SqliteSetup.Scalar(read, "SELECT needs_resync FROM device_streams").Should().Be(1L);
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 還原後未刪的對話都是recovering_已刪的不變()
    {
        const string device = "d_0000000000000000000000000A";
        var dir = TempDir.Create();
        try
        {
            string folder;
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                await ConversationEventLogTests.AddFixedDeviceAsync(host, device);
                await ConversationEventLogTests.Append(host, device,
                    ConversationEventLogTests.Ev(1, K.Created, Meta()), ConversationEventLogTests.Ev(2, K.Item, Item()),
                    ConversationEventLogTests.Ev(3, K.Created, Meta("另一個"), conversationId: OtherConv), ConversationEventLogTests.Ev(4, K.Deleted, new JObject(), conversationId: OtherConv));
                var result = await host.Services.GetRequiredService<BackupService>().RunAsync("test", scheduled: false);
                result.Ok.Should().BeTrue(result.Error);
                folder = result.Folder!;
            }

            RestoreService.Restore(new HubPaths(dir), folder, TimeProvider.System);

            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                string LeaseState(string id) => ConversationEventLogTests.Text(host, $"SELECT lease_state FROM conversations WHERE conversation_id = '{id}'");
                LeaseState(Conv).Should().Be("recovering");
                LeaseState(OtherConv).Should().Be("owned", "已刪除的墓碑不動");
                var directory = host.Services.GetRequiredService<ConversationDirectoryCache>();
                directory.TryGet(Conv, out var entry).Should().BeTrue();
                entry!.Recovering.Should().BeTrue();
                var counts = host.Services.GetRequiredService<ConversationCounters>().Snapshot();
                counts.Recovering.Should().Be(1);
                counts.Devices.Single(d => d.DeviceId == device).Recovering.Should().Be(1);

                // 擁有者在對帳模式之外送新的 created：解除（D16）
                await ConversationEventLogTests.Append(host, device, ConversationEventLogTests.Ev(5, K.Created, Meta("還原之後")));
                LeaseState(Conv).Should().Be("owned");
                directory.TryGet(Conv, out entry).Should().BeTrue();
                entry!.Recovering.Should().BeFalse();
                host.Services.GetRequiredService<ConversationCounters>().Snapshot().Recovering.Should().Be(0);
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    private static async Task<OwnerHost> StartOwnerAsync(string dir) => new(await HubTestHost.StartAsync(dataDir: dir));

    private static async Task<T> CountAsync<T>(HubTestHost host, Func<HubDbContext, Task<T>> query)
    {
        using var scope = host.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<HubDbContext>());
    }
}
