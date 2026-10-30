using MyriagentHub.Data;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Tests.Data;

/// <summary>
/// 伺服器 sync.db 結構 v6（第 5 階段計畫 5.7、4.3 第 1 點；WP5.1）：只加 logs、log_chunks、log_generations、handoffs 與兩個索引，v1～v5 的物件一個字都不改；
/// 正式站從 v5 升上來，資料與伺服器身分不變。回滾到 v5 的程式開 v6 的檔＝比程式新的檔：什麼都不動、只記警告（同 SyncSchemaV4Tests 的「比程式新」，
/// 這裡以 v6 的檔加上 schema_version 7 重現同一條路）。欄位與 ux_handoffs_active 的唯一性照計畫
/// </summary>
public sealed class SyncSchemaV6Tests : IDisposable
{
    private const string InstanceId = "0123456789abcdef0123456789abcdef";

    private readonly string dir = TempDir.Create();

    public void Dispose() => TempDir.TryDelete(dir);

    [Fact]
    public void v5的檔升級_補上v6的表與索引_原有物件逐字不變()
    {
        var path = Path.Combine(dir, "sync.db");
        using (var c = SqliteSetup.Open(path))
        {
            CreateGenuineV5(c);
            SqliteSetup.Exec(c, "INSERT INTO token_tallies(device_id, logical_account_id, day, input, cached, output, updated_at) VALUES('d_1', 'la_1', '2026-10-23', 1, 0, 1, 1)");
        }
        Dictionary<string, string?> v5Objects;
        using (var c = SqliteSetup.Open(path))
            v5Objects = Objects(c);
        v5Objects.Keys.Should().Contain(SyncSchema.V5Tables).And.NotContain(SyncSchema.V6Tables);

        using (var c = SqliteSetup.Open(path))
        {
            SyncSchema.Migrate(c).Should().Be(5);
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='instance_id'").Should().Be(InstanceId, "不換伺服器身分");
            SqliteSetup.Scalar(c, "SELECT input FROM token_tallies WHERE device_id = 'd_1'").Should().Be(1L, "v5 的資料不動");

            var after = Objects(c);
            foreach (var (name, sql) in v5Objects)
                after[name].Should().Be(sql, "v1～v5 的物件 " + name + " 的結構逐字不變");
            after.Keys.Except(v5Objects.Keys).Should().BeEquivalentTo(SyncSchema.V6Tables.Concat(SyncSchema.V6Indexes).Concat(AutoIndexes(c, SyncSchema.V6Tables))
                    .Concat(SyncSchema.V7Tables).Concat(AutoIndexes(c, SyncSchema.V7Tables)),
                "只多 v6、v7、v8 的表、索引與主鍵（UNIQUE）的自動索引（升到目前的版本）");
            foreach (var table in SyncSchema.V6Tables)
                SqliteSetup.Scalar(c, $"SELECT COUNT(*) FROM {table}").Should().Be(0L, table + " 是空的（WP5.1 不寫）");
        }
    }

    [Fact]
    public void 比程式新的v6之後的檔不動_同回滾到v5的程式開v6的檔()
    {
        using var c = SqliteSetup.Open(Path.Combine(dir, "sync.db"));
        SyncSchema.Migrate(c);
        SqliteSetup.Exec(c, "CREATE TABLE future_table(x INTEGER); UPDATE server_meta SET value = '9' WHERE key = 'schema_version'");
        var before = Objects(c);

        SyncSchema.Migrate(c).Should().Be(9);
        SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be("9", "不降版本");
        Objects(c).Should().BeEquivalentTo(before);
    }

    [Fact]
    public void v6的欄位照計畫5點7()
    {
        using var c = SqliteSetup.Open(Path.Combine(dir, "sync.db"));
        SyncSchema.Migrate(c);
        SyncSchema.CurrentVersion.Should().Be(7, "v7 只加 device_schedules（sched1）");
        SyncSchema.V6Tables.Should().Equal("logs", "log_chunks", "log_generations", "handoffs");
        SyncSchema.V6Indexes.Should().Equal("ix_logs_conversation", "ux_handoffs_active");

        Columns(c, "logs").Should().Equal(
            "log_key", "log_id", "conversation_id", "kind", "is_main", "gen", "length", "chain", "line_count", "total", "rel_path", "ref_id",
            "writer_device_id", "writer_epoch", "writer_cli_version", "full_sha256", "full_sha256_length", "damaged_at", "updated_at");
        Columns(c, "log_chunks").Should().Equal("log_key", "gen", "offset", "length", "chain_after", "epoch", "device_id", "created_at");
        Columns(c, "log_generations").Should().Equal("log_key", "gen", "length", "chain", "reason", "superseded_at");
        Columns(c, "handoffs").Should().Equal(
            "handoff_id", "command_id", "conversation_id", "kind", "from_device_id", "to_device_id", "from_epoch", "to_epoch", "mode", "auto_continue",
            "initiator_kind", "initiator_id", "state", "requested_at", "deadline_at", "state_at", "final_device_seq", "ready_json", "fence_json", "error",
            "committed_seq", "import_state", "import_error", "updated_at");

        // log_chunks 的 offset 是 SQLite 的關鍵字，當欄位名稱照樣能用（含排序與條件）
        SqliteSetup.Exec(c, "INSERT INTO log_chunks(log_key, gen, offset, length, chain_after, epoch, device_id, created_at) VALUES('k', 1, 0, 10, x'00', 1, 'd_1', 1)");
        SqliteSetup.Exec(c, "INSERT INTO log_chunks(log_key, gen, offset, length, chain_after, epoch, device_id, created_at) VALUES('k', 1, 10, 5, x'01', 1, 'd_1', 2)");
        SqliteSetup.Scalar(c, "SELECT SUM(length) FROM log_chunks WHERE log_key = 'k' AND offset >= 0").Should().Be(15L);
        SqliteSetup.Scalar(c, "SELECT offset FROM log_chunks WHERE log_key = 'k' ORDER BY offset DESC LIMIT 1").Should().Be(10L);
        FluentActions.Invoking(() => SqliteSetup.Exec(c, "INSERT INTO log_chunks(log_key, gen, offset, length, chain_after, epoch, device_id, created_at) VALUES('k', 1, 10, 7, x'02', 1, 'd_1', 3)"))
            .Should().Throw<SqliteException>("同一個 (log_key, gen, offset) 只有一段");
    }

    [Fact]
    public void 同一個對話最多一筆進行中的交接_commandId唯一()
    {
        using var c = SqliteSetup.Open(Path.Combine(dir, "sync.db"));
        SyncSchema.Migrate(c);
        void Insert(string id, string command, string state) => SqliteSetup.Exec(c, $"""
            INSERT INTO handoffs(handoff_id, command_id, conversation_id, kind, from_device_id, to_device_id, from_epoch, auto_continue, initiator_kind,
              state, requested_at, deadline_at, state_at, updated_at)
            VALUES('{id}', '{command}', 'c1', 'transfer', 'd_A', 'd_B', 1, 0, 'browser', '{state}', 1, 2, 1, 1)
            """);

        Insert("h1", "cmd1", "cancelled");
        Insert("h2", "cmd2", "expired");
        Insert("h3", "cmd3", "requested");
        foreach (var active in new[] { "requested", "accepted", "draining", "ready" })
        {
            FluentActions.Invoking(() => Insert("h-" + active, "cmd-" + active, active))
                .Should().Throw<SqliteException>($"已有一筆進行中：{active} 也算進行中");
        }

        Insert("h4", "cmd4", "committed");
        FluentActions.Invoking(() => Insert("h5", "cmd1", "aborted")).Should().Throw<SqliteException>("command_id 唯一（重送去重）");
        SqliteSetup.Exec(c, "UPDATE handoffs SET state = 'committed' WHERE handoff_id = 'h3'");
        Insert("h6", "cmd6", "accepted");
        SqliteSetup.Scalar(c, "SELECT COUNT(*) FROM handoffs WHERE conversation_id = 'c1'").Should().Be(5L);
    }

    /// <summary>照 v5 的程式建的檔：v1～v5 的結構、commands 的維護索引、server_meta 的三個鍵</summary>
    private static void CreateGenuineV5(SqliteConnection c)
    {
        SqliteSetup.Exec(c, SyncSchema.V1);
        SqliteSetup.Exec(c, SyncSchema.V2);
        SqliteSetup.Exec(c, SyncSchema.V3);
        SqliteSetup.Exec(c, SyncSchema.V4);
        SqliteSetup.Exec(c, SyncSchema.V5);
        SqliteSetup.Exec(c, SyncSchema.CommandIndexes);
        SqliteSetup.Exec(c, $"""
            INSERT INTO server_meta(key, value) VALUES('instance_id', '{InstanceId}');
            INSERT INTO server_meta(key, value) VALUES('restore_generation', '0');
            INSERT INTO server_meta(key, value) VALUES('schema_version', '5');
            """);
    }

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

    private static List<string> Columns(SqliteConnection c, string table)
    {
        var columns = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }
}
