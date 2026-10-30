using MyriagentHub.Data;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Tests.Data;

/// <summary>
/// <c>commands</c> 的維護索引（WP4.5 合併前審查 R1；<see cref="SyncSchema.CommandIndexes"/>）：每秒的結果期限掃描、每 60 秒的 durable 7 天期限與 30 天清理
/// 都走索引、不整表掃描；目前版本的檔缺這兩個索引時開啟補上，不改 <c>schema_version</c>（<c>5538705</c> 建的 v3 檔沒有它們）
/// </summary>
public sealed class CommandIndexTests : IDisposable
{
    private readonly string dir = TempDir.Create();

    public void Dispose() => TempDir.TryDelete(dir);

    [Fact]
    public void 維護查詢走索引_不整表掃描()
    {
        using var c = SqliteSetup.Open(Path.Combine(dir, "sync.db"));
        SyncSchema.Migrate(c);
        const long now = 1_800_000_000_000;

        using (var cmd = CommandStore.DueDeadlinesCommand(c, now))
            Plan(cmd).Should().NotContain(d => d.Contains("SCAN commands", StringComparison.Ordinal), "結果期限每秒掃一次")
                .And.Contain(d => d.Contains("USING INDEX ix_cmd_state_expires", StringComparison.Ordinal));
        using (var cmd = CommandStore.DueDurableExpiriesCommand(c, now))
            Plan(cmd).Should().NotContain(d => d.Contains("SCAN commands", StringComparison.Ordinal), "durable 的 7 天期限每 60 秒掃一次")
                .And.Contain(d => d.Contains("USING INDEX ix_cmd_state_expires", StringComparison.Ordinal));
        using (var cmd = CommandStore.PruneCandidatesCommand(c, now, CommandStateRules.PruneBatch))
            Plan(cmd).Should().NotContain(d => d.Contains("SCAN commands", StringComparison.Ordinal), "30 天清理")
                .And.Contain(d => d.Contains("USING INDEX ix_cmd_done", StringComparison.Ordinal));
    }

    [Fact]
    public void 正式站的v3檔沒有維護索引_開啟時補上_不改schema_version_其他物件不動()
    {
        var path = Path.Combine(dir, "sync.db");
        using var c = SqliteSetup.Open(path);
        SyncSchema.Migrate(c);
        // 目前版本的檔缺這兩個索引（5538705 建的 v3 檔就是這樣：同樣的表、沒有這兩個索引；v3 的檔升到 v4 的那條路見 SyncSchemaV4Tests）
        SqliteSetup.Exec(c, "DROP INDEX ix_cmd_state_expires; DROP INDEX ix_cmd_done");
        SqliteSetup.Exec(c, "INSERT INTO commands(command_id, issuer, target_device_id, cmd, cmd_json, tier, content_sha256, state, created_at, expires_at) "
            + "VALUES('c1', '{}', 'd_1', 'stop', '{}', 1, 'x', 'delivered', 1, 2)");
        var before = Objects(c);
        before.Keys.Should().NotContain(SyncSchema.CommandIndexNames);

        SyncSchema.Migrate(c).Should().Be(SyncSchema.CurrentVersion);
        SqliteSetup.Scalar(c, "SELECT value FROM server_meta WHERE key='schema_version'").Should().Be(SyncSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), "只加索引，不升版本");
        var after = Objects(c);
        after.Keys.Except(before.Keys).Should().BeEquivalentTo(SyncSchema.CommandIndexNames);
        foreach (var (name, sql) in before)
            after[name].Should().Be(sql, name + " 不動");
        SqliteSetup.Scalar(c, "SELECT COUNT(*) FROM commands").Should().Be(1L, "資料不動");

        // 再開一次什麼都不變（IF NOT EXISTS）
        SyncSchema.Migrate(c).Should().Be(SyncSchema.CurrentVersion);
        Objects(c).Should().BeEquivalentTo(after);
    }

    /// <summary>EXPLAIN QUERY PLAN 的每一步（detail 欄）</summary>
    private static List<string> Plan(SqliteCommand cmd)
    {
        cmd.CommandText = "EXPLAIN QUERY PLAN " + cmd.CommandText;
        var details = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            details.Add(reader.GetString(3));
        details.Should().NotBeEmpty();
        return details;
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
}
