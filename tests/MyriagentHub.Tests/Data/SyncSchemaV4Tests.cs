using MyriagentHub.Data;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Tests.Data;

/// <summary>
/// 伺服器 sync.db 結構 v4（app 自我更新 U2，規格 7.8；sync-protocol 6.6）：只加 <c>device_app</c>，v1～v3 的物件一個字都不改；
/// 正式站從 v3 升上來（WP4.2 起正式站是 v3），升級後資料與伺服器身分不變；比程式新的檔不動。服務回滾到 v3 的程式（<c>efea2f3</c>）時
/// from 比它的版本新，V3、CommandIndexes、SetMeta 都不執行，資料庫不還原
/// </summary>
public sealed class SyncSchemaV4Tests : IDisposable
{
    private const string InstanceId = "0123456789abcdef0123456789abcdef";

    private readonly string dir = TempDir.Create();

    public void Dispose() => TempDir.TryDelete(dir);

    [Fact]
    public void v3的檔升級_補上device_app_原有物件逐字不變()
    {
        var path = Path.Combine(dir, "sync.db");
        using (var c = SqliteSetup.Open(path))
        {
            CreateGenuineV3(c);
            SqliteSetup.Exec(c, "INSERT INTO conversations(conversation_id, provider, origin_device_id, epoch, meta_json, title, created_at, updated_at, last_seq) "
                + "VALUES('c', 'Claude', 'd_1', 1, '{}', '第一個對話', 1, 1, 1)");
            SqliteSetup.Exec(c, "INSERT INTO conversation_unread(conversation_id, since_seq, since_at) VALUES('c', 1, 1)");
        }
        Dictionary<string, string?> v3Objects;
        using (var c = SqliteSetup.Open(path))
            v3Objects = Objects(c);
        v3Objects.Keys.Should().Contain(SyncSchema.V2Tables).And.Contain(SyncSchema.V3Tables).And.NotContain(SyncSchema.V4Tables);

        using (var c = SqliteSetup.Open(path))
        {
            SyncSchema.Migrate(c).Should().Be(3);
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='instance_id'").Should().Be(InstanceId, "不換伺服器身分");
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='restore_generation'").Should().Be("3");
            SqliteSetup.Scalar(c, "SELECT COUNT(*) FROM events").Should().Be(2L);
            SqliteSetup.Scalar(c, "SELECT payload FROM events WHERE seq = 2").Should().Be("{\"b\":2}");
            SqliteSetup.Scalar(c, "SELECT app_version FROM device_streams WHERE device_id = 'd_1'").Should().Be("0.2.100");
            SqliteSetup.Scalar(c, "SELECT title FROM conversations WHERE conversation_id = 'c'").Should().Be("第一個對話", "v2 的資料也不動");
            SqliteSetup.Scalar(c, "SELECT since_seq || ',' || since_at FROM conversation_unread WHERE conversation_id = 'c'").Should().Be("1,1", "v3 的資料也不動");

            var after = Objects(c);
            foreach (var (name, sql) in v3Objects)
                after[name].Should().Be(sql, "v1～v3 的物件 " + name + " 的結構逐字不變");
            after.Keys.Except(v3Objects.Keys).Should().BeEquivalentTo(
                SyncSchema.V4Tables.Concat(SyncSchema.V5Tables).Concat(SyncSchema.V6Tables).Concat(SyncSchema.V6Indexes).Concat(SyncSchema.V7Tables)
                    .Concat(AutoIndexes(c, [.. SyncSchema.V4Tables, .. SyncSchema.V5Tables, .. SyncSchema.V6Tables, .. SyncSchema.V7Tables])).Concat(SyncSchema.CommandIndexNames),
                "只多 v4 的表（以及之後 v5 的 token_tallies）與它們的主鍵索引，以及 commands 不升版本的維護索引（R1）");
            SqliteSetup.Scalar(c, "SELECT COUNT(*) FROM device_app").Should().Be(0L, "升級不寫資料：app.state 由裝置送");
        }
    }

    [Fact]
    public void 新建的檔就是目前的版本_再遷移一次什麼都不變()
    {
        using var c = SqliteSetup.Open(Path.Combine(dir, "sync.db"));
        SyncSchema.Migrate(c).Should().Be(0);
        SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var objects = Objects(c);
        objects.Keys.Should().Contain(SyncSchema.V2Tables).And.Contain(SyncSchema.V3Tables).And.Contain(SyncSchema.V4Tables);

        SyncSchema.Migrate(c).Should().Be(SyncSchema.CurrentVersion);
        SyncSchema.Migrate(c).Should().Be(SyncSchema.CurrentVersion);
        Objects(c).Should().BeEquivalentTo(objects);
    }

    [Fact]
    public void 比程式新的檔不動()
    {
        using var c = SqliteSetup.Open(Path.Combine(dir, "sync.db"));
        CreateGenuineV3(c);
        SqliteSetup.Exec(c, SyncSchema.V4);
        SqliteSetup.Exec(c, "UPDATE server_meta SET value = '9' WHERE key = 'schema_version'");
        var before = Objects(c);

        SyncSchema.Migrate(c).Should().Be(9);
        SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be("9", "不降版本");
        Objects(c).Should().BeEquivalentTo(before, "也不補表與索引：比程式新的檔由更新的程式負責");
    }

    [Fact]
    public void device_app的欄位()
    {
        using var c = SqliteSetup.Open(Path.Combine(dir, "sync.db"));
        SyncSchema.Migrate(c);

        TableInfo(c, "device_app").Should().Equal(("device_id", "TEXT", true), ("state_json", "TEXT", false), ("updated_at", "INTEGER", false));
        // 每台一列：主鍵擋重複，INSERT OR REPLACE 取代舊的（Catalog.AppState 的寫法）
        SqliteSetup.Exec(c, "INSERT INTO device_app(device_id, state_json, updated_at) VALUES('d_1', '{}', 1)");
        FluentActions.Invoking(() => SqliteSetup.Exec(c, "INSERT INTO device_app(device_id, state_json, updated_at) VALUES('d_1', '{}', 2)"))
            .Should().Throw<SqliteException>();
        FluentActions.Invoking(() => SqliteSetup.Exec(c, "INSERT INTO device_app(device_id, state_json, updated_at) VALUES('d_2', NULL, 2)"))
            .Should().Throw<SqliteException>("state_json 不可為 NULL");
        SqliteSetup.Exec(c, "INSERT OR REPLACE INTO device_app(device_id, state_json, updated_at) VALUES('d_1', '{\"phase\":\"idle\"}', 3)");
        SqliteSetup.Scalar(c, "SELECT COUNT(*) FROM device_app").Should().Be(1L);
        SqliteSetup.Scalar(c, "SELECT state_json || ',' || updated_at FROM device_app WHERE device_id = 'd_1'").Should().Be("{\"phase\":\"idle\"},3");
    }

    [Fact]
    public async Task 寫入者開啟v3的檔_升級到目前的版本()
    {
        var dataDir = TempDir.Create();
        try
        {
            var dbDir = Path.Combine(dataDir, "db");
            Directory.CreateDirectory(dbDir);
            using (var c = SqliteSetup.Open(Path.Combine(dbDir, "sync.db")))
                CreateGenuineV3(c);
            await using (var host = await HubTestHost.StartAsync(dataDir: dataDir, deleteOnDispose: false))
            {
                host.Services.GetRequiredService<SyncWriter>().Instance.InstanceId.Should().Be(InstanceId, "升級不重建、不換身分");
                using var read = host.Services.GetRequiredService<SyncWriter>().OpenRead();
                SqliteSetup.Scalar(read, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
                SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='device_app'").Should().Be(1L);
                SqliteSetup.Scalar(read, "SELECT app_version FROM device_streams WHERE device_id = 'd_1'").Should().Be("0.2.100", "既有的資料不動");
            }
        }
        finally
        {
            TempDir.TryDelete(dataDir);
        }
    }

    [Fact]
    public async Task 寫入者開啟比程式新的檔_照常啟動_不降版本()
    {
        var dataDir = TempDir.Create();
        try
        {
            var dbDir = Path.Combine(dataDir, "db");
            Directory.CreateDirectory(dbDir);
            // 更新的程式建的檔：v1～v4 的結構全有、版本號比程式新（服務回滾到舊版時，舊程式開到的就是這種檔）；
            // v3 的 conversation_unread 給啟動步驟「對話未讀」讀
            using (var c = SqliteSetup.Open(Path.Combine(dbDir, "sync.db")))
            {
                CreateGenuineV3(c);
                SqliteSetup.Exec(c, SyncSchema.V4);
                SqliteSetup.Exec(c, "UPDATE server_meta SET value = '9' WHERE key = 'schema_version'");
            }
            await using (var host = await HubTestHost.StartAsync(dataDir: dataDir, deleteOnDispose: false))
            {
                host.Services.GetRequiredService<SyncWriter>().Instance.InstanceId.Should().Be(InstanceId, "不丟例外、不重建、不換身分（SyncWriter 只記警告）");
                using var read = host.Services.GetRequiredService<SyncWriter>().OpenRead();
                SqliteSetup.Scalar(read, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be("9", "不降版本");
                SqliteSetup.Scalar(read, "SELECT app_version FROM device_streams WHERE device_id = 'd_1'").Should().Be("0.2.100", "既有的資料不動");
            }
        }
        finally
        {
            TempDir.TryDelete(dataDir);
        }
    }

    /// <summary>
    /// 照 v3 的程式（<c>5538705</c>）建的檔：v1～v3 的結構、server_meta 的三個鍵加一點資料。沒有 commands 的維護索引（<c>5538705</c> 還沒有它們），
    /// 也不放 conversations 的列（啟動主機的測試也用它：之後讀 conversations 的程式不會碰到不完整的列）
    /// </summary>
    private static void CreateGenuineV3(SqliteConnection c)
    {
        SqliteSetup.Exec(c, SyncSchema.V1);
        SqliteSetup.Exec(c, SyncSchema.V2);
        SqliteSetup.Exec(c, SyncSchema.V3);
        SqliteSetup.Exec(c, """
            INSERT INTO server_meta(key, value) VALUES('instance_id', '0123456789abcdef0123456789abcdef');
            INSERT INTO server_meta(key, value) VALUES('restore_generation', '3');
            INSERT INTO server_meta(key, value) VALUES('schema_version', '3');
            INSERT INTO device_streams(device_id, name, app_version) VALUES('d_1', '客廳電腦', '0.2.100');
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

    /// <summary>指定的表的主鍵自動索引（sqlite_autoindex_…）</summary>
    private static List<string> AutoIndexes(SqliteConnection c, IReadOnlyList<string> tables)
    {
        var names = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name, tbl_name FROM sqlite_master WHERE type='index' AND name LIKE 'sqlite_autoindex_%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (tables.Contains(reader.GetString(1)))
                names.Add(reader.GetString(0));
        }
        return names;
    }

    /// <summary>每個欄位的名稱、宣告的型別、是不是主鍵（PRAGMA table_info）</summary>
    private static List<(string Name, string Type, bool PrimaryKey)> TableInfo(SqliteConnection c, string table)
    {
        var columns = new List<(string, string, bool)>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add((reader.GetString(1), reader.GetString(2), reader.GetInt64(5) > 0));
        return columns;
    }
}
