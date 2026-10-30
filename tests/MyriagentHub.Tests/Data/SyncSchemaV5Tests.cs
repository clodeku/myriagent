using MyriagentHub.Data;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Tests.Data;

/// <summary>
/// 伺服器 sync.db 結構 v5（各帳戶的 token 計數，tok1；sync-protocol 14.9）：只加 <c>token_tallies</c>，v1～v4 的物件一個字都不改；
/// 正式站從 v4 升上來，升級後資料與伺服器身分不變。服務回滾到 v4 的程式時 from 比它的版本新，什麼都不動、資料庫不還原
/// </summary>
public sealed class SyncSchemaV5Tests : IDisposable
{
    private const string InstanceId = "0123456789abcdef0123456789abcdef";

    private readonly string dir = TempDir.Create();

    public void Dispose() => TempDir.TryDelete(dir);

    [Fact]
    public void v4的檔升級_補上token_tallies_原有物件逐字不變()
    {
        var path = Path.Combine(dir, "sync.db");
        using (var c = SqliteSetup.Open(path))
        {
            CreateGenuineV4(c);
            SqliteSetup.Exec(c, "INSERT INTO device_app(device_id, state_json, updated_at) VALUES('d_1', '{\"phase\":\"idle\"}', 5)");
        }
        Dictionary<string, string?> v4Objects;
        using (var c = SqliteSetup.Open(path))
            v4Objects = Objects(c);
        v4Objects.Keys.Should().Contain(SyncSchema.V4Tables).And.NotContain(SyncSchema.V5Tables);

        using (var c = SqliteSetup.Open(path))
        {
            SyncSchema.Migrate(c).Should().Be(4);
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='instance_id'").Should().Be(InstanceId, "不換伺服器身分");
            SqliteSetup.Scalar(c, "SELECT state_json FROM device_app WHERE device_id = 'd_1'").Should().Be("{\"phase\":\"idle\"}", "v4 的資料不動");

            var after = Objects(c);
            foreach (var (name, sql) in v4Objects)
                after[name].Should().Be(sql, "v1～v4 的物件 " + name + " 的結構逐字不變");
            after.Keys.Except(v4Objects.Keys).Should().BeEquivalentTo(
                SyncSchema.V5Tables.Concat(AutoIndexes(c, SyncSchema.V5Tables)).Concat(SyncSchema.V6Tables).Concat(SyncSchema.V6Indexes).Concat(AutoIndexes(c, SyncSchema.V6Tables))
                    .Concat(SyncSchema.V7Tables).Concat(AutoIndexes(c, SyncSchema.V7Tables)),
                "只多 v5、v6、v7、v8 的表與它們的索引（升到目前的版本）");
            SqliteSetup.Scalar(c, "SELECT COUNT(*) FROM token_tallies").Should().Be(0L, "升級不寫資料：計數由裝置送");
        }
    }

    [Fact]
    public void token_tallies的欄位_每台每帳戶每天一列()
    {
        using var c = SqliteSetup.Open(Path.Combine(dir, "sync.db"));
        SyncSchema.Migrate(c);
        SyncSchema.V5Tables.Should().Equal("token_tallies");

        TableInfo(c, "token_tallies").Should().Equal(
            ("device_id", "TEXT", true), ("logical_account_id", "TEXT", true), ("day", "TEXT", true),
            ("input", "INTEGER", false), ("cached", "INTEGER", false), ("output", "INTEGER", false), ("updated_at", "INTEGER", false));
        const string Insert = "INSERT INTO token_tallies(device_id, logical_account_id, day, input, cached, output, updated_at) ";
        SqliteSetup.Exec(c, Insert + "VALUES('d_1', 'la_1', '2026-10-23', 1, 0, 1, 1)");
        SqliteSetup.Exec(c, Insert + "VALUES('d_2', 'la_1', '2026-10-23', 1, 0, 1, 1)");
        SqliteSetup.Exec(c, Insert + "VALUES('d_1', 'la_1', '2026-10-22', 1, 0, 1, 1)");
        FluentActions.Invoking(() => SqliteSetup.Exec(c, Insert + "VALUES('d_1', 'la_1', '2026-10-23', 2, 0, 2, 2)"))
            .Should().Throw<SqliteException>("同一台、同一個帳戶、同一天只有一列");
        FluentActions.Invoking(() => SqliteSetup.Exec(c, Insert + "VALUES('d_3', 'la_1', '2026-10-23', NULL, 0, 2, 2)"))
            .Should().Throw<SqliteException>("數字不可為 NULL");
        SqliteSetup.Exec(c, "INSERT OR REPLACE INTO token_tallies(device_id, logical_account_id, day, input, cached, output, updated_at) VALUES('d_1', 'la_1', '2026-10-23', 5, 1, 2, 3)");
        SqliteSetup.Scalar(c, "SELECT COUNT(*) FROM token_tallies").Should().Be(3L);
        SqliteSetup.Scalar(c, "SELECT input || ',' || cached || ',' || output FROM token_tallies WHERE device_id = 'd_1' AND day = '2026-10-23'").Should().Be("5,1,2");
    }

    /// <summary>照 v4 的程式建的檔：v1～v4 的結構、commands 的維護索引、server_meta 的三個鍵</summary>
    private static void CreateGenuineV4(SqliteConnection c)
    {
        SqliteSetup.Exec(c, SyncSchema.V1);
        SqliteSetup.Exec(c, SyncSchema.V2);
        SqliteSetup.Exec(c, SyncSchema.V3);
        SqliteSetup.Exec(c, SyncSchema.V4);
        SqliteSetup.Exec(c, SyncSchema.CommandIndexes);
        SqliteSetup.Exec(c, """
            INSERT INTO server_meta(key, value) VALUES('instance_id', '0123456789abcdef0123456789abcdef');
            INSERT INTO server_meta(key, value) VALUES('restore_generation', '0');
            INSERT INTO server_meta(key, value) VALUES('schema_version', '4');
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
