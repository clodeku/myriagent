using System.Globalization;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Data;

/// <summary>
/// 伺服器 sync.db 結構 v3（WP4.2 步 12A；答 6、D46）：只加一張 <c>conversation_unread</c>，v1、v2 的物件與資料一個字都不改；
/// 比程式新的檔不動、不改 schema_version（回滾到 <c>1724718</c> 時它開 v3 的檔就是這條路，F48）；還原 v2 的備份之後升到 v3、未讀是空的
/// </summary>
public sealed class SyncSchemaV3Tests : IDisposable
{
    private readonly string dir = TempDir.Create();

    public void Dispose() => TempDir.TryDelete(dir);

    [Fact]
    public void 從v2升到v3_只多一張表()
    {
        var path = Path.Combine(dir, "sync.db");
        using (var c = SqliteSetup.Open(path))
            CreateGenuineV2(c);
        Dictionary<string, string?> before;
        using (var c = SqliteSetup.Open(path))
            before = Objects(c);

        using (var c = SqliteSetup.Open(path))
        {
            SyncSchema.Migrate(c).Should().Be(2);
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture));
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='instance_id'").Should().Be("0123456789abcdef0123456789abcdef", "不換伺服器身分");
            SqliteSetup.Scalar(c, "SELECT title FROM conversations WHERE conversation_id = 'c1'").Should().Be("舊對話", "v2 的資料不動");
            SqliteSetup.Scalar(c, "SELECT COUNT(*) FROM events").Should().Be(1L);

            var after = Objects(c);
            foreach (var (name, sql) in before)
                after[name].Should().Be(sql, "v1、v2 的物件 " + name + " 的結構逐字不變");
            after.Keys.Except(before.Keys).Should().BeEquivalentTo(
                new[]
                {
                    "conversation_unread", "sqlite_autoindex_conversation_unread_1", "device_app", "sqlite_autoindex_device_app_1",
                    "token_tallies", "sqlite_autoindex_token_tallies_1",
                    "logs", "sqlite_autoindex_logs_1", "ix_logs_conversation", "log_chunks", "sqlite_autoindex_log_chunks_1",
                    "log_generations", "sqlite_autoindex_log_generations_1", "handoffs", "sqlite_autoindex_handoffs_1", "sqlite_autoindex_handoffs_2",
                    "ux_handoffs_active", "device_schedules", "sqlite_autoindex_device_schedules_1",
                }.Concat(SyncSchema.CommandIndexNames),
                "只多 v3 的一張表（與它的主鍵索引），以及 commands 不升版本的維護索引（WP4.5 合併前審查 R1）；"
                + "v2 的檔直接升到目前版本，app 自我更新 U2 的 v4 再多 device_app、tok1 的 v5 再多 token_tallies（與它們的主鍵索引），見 SyncSchemaV4Tests、SyncSchemaV5Tests");
            SyncSchema.V3Tables.Should().Equal("conversation_unread");
            Columns(c, "conversation_unread").Should().Equal("conversation_id", "since_seq", "since_at");
            SqliteSetup.Scalar(c, "SELECT COUNT(*) FROM conversation_unread").Should().Be(0L, "升級之後未讀是空的");
            FluentActions.Invoking(() => SqliteSetup.Exec(c, "INSERT INTO conversation_unread(conversation_id, since_seq) VALUES('c1', 1)"))
                .Should().Throw<SqliteException>("since_at 是 NOT NULL");
        }
    }

    [Fact]
    public void v3再開_不動()
    {
        var path = Path.Combine(dir, "sync.db");
        using var c = SqliteSetup.Open(path);
        SyncSchema.Migrate(c).Should().Be(0);
        SqliteSetup.Exec(c, "INSERT INTO conversation_unread(conversation_id, since_seq, since_at) VALUES('c1', 7, 70)");
        var objects = Objects(c);
        objects.Keys.Should().Contain(SyncSchema.V2Tables).And.Contain(SyncSchema.V3Tables);

        SyncSchema.Migrate(c).Should().Be(SyncSchema.CurrentVersion);
        SyncSchema.Migrate(c).Should().Be(SyncSchema.CurrentVersion);
        Objects(c).Should().BeEquivalentTo(objects);
        SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture));
        SqliteSetup.Scalar(c, "SELECT since_seq || ',' || since_at FROM conversation_unread WHERE conversation_id = 'c1'").Should().Be("7,70", "再開不清資料");
    }

    [Fact]
    public void 比程式新的v9_不動不改schema_version()
    {
        // 真的 v9（目前版本再加之後的東西）：一個物件都不動
        var newer = Path.Combine(dir, "v9.db");
        using (var c = SqliteSetup.Open(newer))
        {
            SyncSchema.Migrate(c);
            SqliteSetup.Exec(c, "CREATE TABLE future_table(x INTEGER); UPDATE server_meta SET value = '9' WHERE key = 'schema_version'");
            var before = Objects(c);
            SyncSchema.Migrate(c).Should().Be(9);
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be("9");
            Objects(c).Should().BeEquivalentTo(before);
        }

        // 只標著 6 的檔（沒有 v3～v5 的表）：比程式新就不補表——舊程式開新檔時同樣什麼都不做（F48）
        var labelled = Path.Combine(dir, "labelled.db");
        using (var c = SqliteSetup.Open(labelled))
        {
            CreateGenuineV2(c);
            SqliteSetup.Exec(c, "UPDATE server_meta SET value = '9' WHERE key = 'schema_version'");
            var before = Objects(c);
            SyncSchema.Migrate(c).Should().Be(9);
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be("9");
            Objects(c).Should().BeEquivalentTo(before);
            Objects(c).Keys.Should().NotContain("conversation_unread");
        }
    }

    [Fact]
    public async Task 還原v2的備份_升到v3()
    {
        const string device = "d_0000000000000000000000000A";
        var data = TempDir.Create();
        try
        {
            string folder;
            await using (var host = await HubTestHost.StartAsync(dataDir: data, deleteOnDispose: false))
            {
                await ConversationEventLogTests.AddFixedDeviceAsync(host, device);
                await ConversationEventLogTests.Append(host, device, ConversationEventLogTests.Ev(1, K.Created, Meta()),
                    ConversationEventLogTests.Ev(2, K.Attention, new JObject { ["kind"] = "completed" }));
                var result = await host.Services.GetRequiredService<BackupService>().RunAsync("test", scheduled: false);
                result.Ok.Should().BeTrue(result.Error);
                folder = result.Folder!;
            }

            // 把備份改成 v2 的程式做的那一份（沒有未讀表、schema_version 是 2），manifest 的雜湊跟著改
            var backupDb = Path.Combine(folder, "sync.db");
            using (var c = BackupService.OpenUnpooled(backupDb))
            {
                SqliteSetup.Exec(c, "DROP TABLE conversation_unread; UPDATE server_meta SET value = '2' WHERE key = 'schema_version'");
                SqliteSetup.Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'conversation_unread'").Should().Be(0L);
            }
            var manifest = BackupService.ReadManifest(folder)!;
            manifest = manifest with
            {
                Files = manifest.Files.Select(f => f.Name == "sync.db" ? f with { Sha256 = BackupService.Sha256File(backupDb), Bytes = new FileInfo(backupDb).Length } : f).ToList(),
            };
            BackupService.WriteJsonAtomically(Path.Combine(folder, BackupService.ManifestFile), manifest);

            RestoreService.Restore(new HubPaths(data), folder, TimeProvider.System);
            using (var c = SqliteSetup.Open(new HubPaths(data).SyncDb, readOnly: true))
                SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be("2", "還原放回的是 v2 的檔，啟動時才升級");

            await using (var host = await HubTestHost.StartAsync(dataDir: data, deleteOnDispose: false))
            {
                using var read = host.Services.GetRequiredService<SyncWriter>().OpenRead();
                SqliteSetup.Scalar(read, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(CultureInfo.InvariantCulture));
                SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM conversation_unread").Should().Be(0L, "v2 的備份沒有未讀：升級之後是空的");
                SqliteSetup.Scalar(read, $"SELECT lease_state FROM conversations WHERE conversation_id = '{Conv}'").Should().Be("recovering");
                host.Services.GetRequiredService<ConversationUnread>().Count.Should().Be(0);
                host.Services.GetRequiredService<ConversationCounters>().Snapshot().Unread.Should().Be(0);
            }
        }
        finally
        {
            TempDir.TryDelete(data);
        }
    }

    /// <summary>照 v2 的程式建的檔（v1＋v2 的結構、server_meta 的三個鍵、schema_version 2）加一點資料</summary>
    internal static void CreateGenuineV2(SqliteConnection c)
    {
        SqliteSetup.Exec(c, SyncSchema.V1);
        SqliteSetup.Exec(c, SyncSchema.V2);
        SqliteSetup.Exec(c, """
            INSERT INTO server_meta(key, value) VALUES('instance_id', '0123456789abcdef0123456789abcdef');
            INSERT INTO server_meta(key, value) VALUES('restore_generation', '0');
            INSERT INTO server_meta(key, value) VALUES('schema_version', '2');
            INSERT INTO events(stream, cls, author, kind, payload, created_at) VALUES('catalog', 'catalog', 'server', 'device.upsert', '{"a":1}', 1);
            INSERT INTO conversations(conversation_id, provider, origin_device_id, owner_device_id, epoch, meta_json, title, created_at, updated_at, last_seq)
              VALUES('c1', 'Claude', 'd_1', 'd_1', 1, '{}', '舊對話', 1, 1, 1);
            """);
    }

    /// <summary>sqlite_master 的每個物件（名稱 → 建立的 SQL；自動索引是 null）</summary>
    private static Dictionary<string, string?> Objects(SqliteConnection c)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_sequence'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return result;
    }

    private static List<string> Columns(SqliteConnection c, string table)
    {
        var names = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(1));
        return names;
    }
}
