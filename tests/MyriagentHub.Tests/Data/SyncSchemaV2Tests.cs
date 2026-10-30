using MyriagentHub.Data;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Tests.Data;

/// <summary>
/// 伺服器 sync.db 結構 v2（WP4.1 步 12，D54；設計 3.2 照 14.11 改寫）：只加第 4 階段的表與索引，v1 的物件一個字都不改；
/// v1 的檔升級後資料與伺服器身分不變（v1 的檔直接升到目前版本：v3 的未讀表見 <see cref="SyncSchemaV3Tests"/>，v4 的 device_app 見 <see cref="SyncSchemaV4Tests"/>）；
/// 比程式新的檔不動（回滾到舊版程式時資料庫不還原）
/// </summary>
public sealed class SyncSchemaV2Tests : IDisposable
{
    private static readonly string[] V1Tables =
        ["server_meta", "device_streams", "events", "rejected_events", "logical_accounts", "account_links", "usage_latest", "usage_points", "usage_pollers", "device_cli", "prefs"];

    private static readonly string[] V2Indexes = ["ix_conv_updated", "ix_items_order", "ix_cmd_target", "ix_media_refs_conv"];

    private readonly string dir = TempDir.Create();

    public void Dispose() => TempDir.TryDelete(dir);

    [Fact]
    public void v1的檔升級_補上v2的表與索引_v1的物件逐字不變_資料與伺服器身分不變()
    {
        var path = Path.Combine(dir, "sync.db");
        using (var c = SqliteSetup.Open(path))
            CreateGenuineV1(c);
        Dictionary<string, string?> v1Objects;
        using (var c = SqliteSetup.Open(path))
            v1Objects = Objects(c);
        v1Objects.Keys.Should().Contain(V1Tables);

        using (var c = SqliteSetup.Open(path))
        {
            SyncSchema.Migrate(c).Should().Be(1);
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='instance_id'").Should().Be("0123456789abcdef0123456789abcdef", "不換伺服器身分");
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='restore_generation'").Should().Be("3");
            SqliteSetup.Scalar(c, "SELECT COUNT(*) FROM events").Should().Be(2L);
            SqliteSetup.Scalar(c, "SELECT payload FROM events WHERE seq = 2").Should().Be("{\"b\":2}");
            SqliteSetup.Scalar(c, "SELECT name FROM device_streams WHERE device_id = 'd_1'").Should().Be("客廳電腦");

            var after = Objects(c);
            foreach (var (name, sql) in v1Objects)
                after[name].Should().Be(sql, "v1 的物件 " + name + " 的結構逐字不變");
            after.Keys.Except(v1Objects.Keys).Should().BeEquivalentTo(
                SyncSchema.V2Tables.Concat(V2Indexes).Concat(SyncSchema.V3Tables).Concat(SyncSchema.V4Tables).Concat(SyncSchema.V5Tables).Concat(SyncSchema.V6Tables).Concat(SyncSchema.V7Tables)
                    .Concat(SyncSchema.V6Indexes).Concat(SyncSchema.CommandIndexNames).Concat(AutoIndexes(c)),
                "只多 v2 的表與索引（以及 v3 的未讀表，WP4.2 步 12A；v4 的 device_app，app 自我更新 U2；v5 的 token_tallies，tok1；v6 的日誌與交接，第 5 階段 WP5.1；commands 不升版本的維護索引，WP4.5 合併前審查 R1）");
            foreach (var table in SyncSchema.V2Tables)
                SqliteSetup.Scalar(c, $"SELECT COUNT(*) FROM {table}").Should().Be(0L, table + " 是空的（WP4.1 不寫）");
        }
    }

    [Fact]
    public void 新建的檔是目前版本_再遷移一次什麼都不變()
    {
        var path = Path.Combine(dir, "sync.db");
        using var c = SqliteSetup.Open(path);
        SyncSchema.Migrate(c).Should().Be(0);
        SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var objects = Objects(c);
        objects.Keys.Should().Contain(V1Tables).And.Contain(SyncSchema.V2Tables).And.Contain(V2Indexes);

        SyncSchema.Migrate(c).Should().Be(SyncSchema.CurrentVersion);
        SyncSchema.Migrate(c).Should().Be(SyncSchema.CurrentVersion);
        Objects(c).Should().BeEquivalentTo(objects);
    }

    [Fact]
    public void 比程式新的檔不動_不降版本也不補表()
    {
        var path = Path.Combine(dir, "sync.db");
        using var c = SqliteSetup.Open(path);
        CreateGenuineV1(c);
        SqliteSetup.Exec(c, "UPDATE server_meta SET value = '9' WHERE key = 'schema_version'");
        var before = Objects(c);

        SyncSchema.Migrate(c).Should().Be(9);
        SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be("9");
        Objects(c).Should().BeEquivalentTo(before);
    }

    [Fact]
    public void v2各表的欄位_media_refs不存路徑_commands有瀏覽器id遮蔽後的指令與內容摘要_conversations有deleted_by與transcript沒有delete_proof()
    {
        using var c = SqliteSetup.Open(Path.Combine(dir, "sync.db"));
        SyncSchema.Migrate(c);

        Columns(c, "conversations").Should().Equal(
            "conversation_id", "provider", "logical_account_id", "origin_device_id", "owner_device_id", "epoch", "lease_state", "meta_json", "header_json", "title",
            "created_at", "updated_at", "last_seq", "item_count", "max_ord", "running", "waiting", "status_text", "turn_started_at", "last_turn", "sync_policy",
            "transcript", "forked_from", "fork_point_seq", "deleted_at", "deleted_by");
        Columns(c, "items").Should().Equal(
            "conversation_id", "item_id", "ord", "last_seq", "epoch", "ver", "kind", "status", "final", "origin_device_id", "body", "enc", "removed");
        Columns(c, "conversation_runtime").Should().Equal("conversation_id", "queue_json", "pending_attachments_json", "busy", "busy_text", "version");
        Columns(c, "commands").Should().Equal(
            "command_id", "browser_command_id", "issuer", "conversation_id", "target_device_id", "target_epoch", "cmd", "cmd_json", "tier", "full_access",
            "content_sha256", "durable", "unsigned_body", "state", "result_json", "created_at", "delivered_at", "done_at", "expires_at");
        Columns(c, "media_refs").Should().Equal("device_id", "ref", "conversation_id", "purpose", "thumb_blob", "created_at", "last_used_at");
        Columns(c, "blobs").Should().Equal("sha256", "size", "content_type", "kind", "created_at", "last_ref_at");
        Columns(c, "blob_refs").Should().Equal("sha256", "ref_kind", "ref_id");

        // media_refs 的主鍵是 (device_id, ref)：不同裝置可以發同一個 ref
        SqliteSetup.Exec(c, "INSERT INTO media_refs(device_id, ref, conversation_id, purpose, created_at, last_used_at) VALUES('d_1', 'R', 'c', 'media', 1, 1)");
        SqliteSetup.Exec(c, "INSERT INTO media_refs(device_id, ref, conversation_id, purpose, created_at, last_used_at) VALUES('d_2', 'R', 'c', 'media', 1, 1)");
        FluentActions.Invoking(() => SqliteSetup.Exec(c, "INSERT INTO media_refs(device_id, ref, conversation_id, purpose, created_at, last_used_at) VALUES('d_1', 'R', 'c', 'thumb', 1, 1)"))
            .Should().Throw<SqliteException>();
        // 預設值：對話的政策與租約狀態
        SqliteSetup.Exec(c, "INSERT INTO conversations(conversation_id, provider, origin_device_id, epoch, meta_json, title, created_at, updated_at, last_seq) VALUES('c', 'Claude', 'd_1', 1, '{}', 't', 1, 1, 1)");
        SqliteSetup.Scalar(c, "SELECT sync_policy || ',' || lease_state || ',' || running || ',' || IFNULL(deleted_by, '-') FROM conversations").Should().Be("full,owned,0,-");
    }

    [Fact]
    public async Task 寫入者開啟v1的檔_升級成目前版本()
    {
        var dataDir = TempDir.Create();
        try
        {
            var dbDir = Path.Combine(dataDir, "db");
            Directory.CreateDirectory(dbDir);
            using (var c = SqliteSetup.Open(Path.Combine(dbDir, "sync.db")))
                CreateGenuineV1(c);
            await using (var host = await HubTestHost.StartAsync(dataDir: dataDir, deleteOnDispose: false))
            {
                host.Services.GetRequiredService<SyncWriter>().Instance.InstanceId.Should().Be("0123456789abcdef0123456789abcdef");
                using var read = host.Services.GetRequiredService<SyncWriter>().OpenRead();
                SqliteSetup.Scalar(read, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='commands'").Should().Be(1L);
                SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='device_app'").Should().Be(1L);
            }
        }
        finally
        {
            TempDir.TryDelete(dataDir);
        }
    }

    /// <summary>照 v1 的程式建的檔（v1 的結構、server_meta 的三個鍵）加一點資料</summary>
    private static void CreateGenuineV1(SqliteConnection c)
    {
        SqliteSetup.Exec(c, SyncSchema.V1);
        SqliteSetup.Exec(c, """
            INSERT INTO server_meta(key, value) VALUES('instance_id', '0123456789abcdef0123456789abcdef');
            INSERT INTO server_meta(key, value) VALUES('restore_generation', '3');
            INSERT INTO server_meta(key, value) VALUES('schema_version', '1');
            INSERT INTO device_streams(device_id, name) VALUES('d_1', '客廳電腦');
            INSERT INTO events(stream, cls, author, kind, payload, created_at) VALUES('catalog', 'catalog', 'server', 'device.upsert', '{"a":1}', 1);
            INSERT INTO events(stream, cls, author, kind, payload, created_at) VALUES('catalog', 'catalog', 'server', 'device.upsert', '{"b":2}', 2);
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

    /// <summary>v2（與 v3～v5）的表的主鍵自動索引（sqlite_autoindex_…）</summary>
    private static IEnumerable<string> AutoIndexes(SqliteConnection c)
    {
        var names = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, tbl_name FROM sqlite_master WHERE type='index' AND name LIKE 'sqlite_autoindex_%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (SyncSchema.V2Tables.Contains(reader.GetString(1)) || SyncSchema.V3Tables.Contains(reader.GetString(1)) || SyncSchema.V4Tables.Contains(reader.GetString(1))
                || SyncSchema.V5Tables.Contains(reader.GetString(1)) || SyncSchema.V6Tables.Contains(reader.GetString(1)) || SyncSchema.V7Tables.Contains(reader.GetString(1)))
                names.Add(reader.GetString(0));
        }
        return names;
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
