using System.Globalization;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Rules;
using Myriagent.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace Myriagent.Tests.Sync.Store;

/// <summary>
/// 桌面 sync.db 結構 v4（第 5 階段計畫 5.7、4.3 第 5 點；WP5.1）：只加 log_state、handoffs_local、path_cache 與一個索引——v3 的檔開啟時補上、
/// v1～v3 物件的 SQL 逐字不變、min_reader 仍是 1（前一版照樣能開）；升級失敗時照舊版結構開啟（ConversationTablesReady 是 false），重試成功才是 true；
/// 欄位照計畫；leases 的新狀態值
/// </summary>
public sealed class SyncStoreV4Tests : IDisposable
{
    private const long PairedAt = 1791500000000;

    private static readonly ServerInstance Server = new("srv-instance-1", 0);

    private readonly TempDirectory temp = new("syncstore-v4");
    private readonly TestDevice self = TestDevice.Create("OFFICE-PC");

    private string DbPath => temp.Combine("sync", "sync.db");

    public void Dispose() => temp.Dispose();

    private SyncStore Create() => SyncStore.Create(DbPath, self.DeviceId, PairedAt, Server, new TrustBundle(3, [self.Entry()]));

    private SyncStore Reopen() => SyncStore.Open(DbPath, self.DeviceId);

    private static IEnumerable<string> V4Objects => SyncStoreSchema.V4Tables.Concat(SyncStoreSchema.V4Indexes);

    private SqliteConnection Connect(SyncStoreOpenMode mode)
    {
        var connection = new SqliteConnection(SyncStore.ConnectionString(DbPath, mode));
        connection.Open();
        return connection;
    }

    private static void Exec(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void Raw(string sql)
    {
        using var connection = Connect(SyncStoreOpenMode.Open);
        Exec(connection, null, sql);
    }

    private Dictionary<string, string?> Schema()
    {
        using var connection = Connect(SyncStoreOpenMode.Open);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, sql FROM sqlite_master WHERE type IN ('table', 'index') ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (reader.Read())
            result[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        return result;
    }

    private List<string> Columns(string table)
    {
        using var connection = Connect(SyncStoreOpenMode.Open);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>照 v3 程式（SchemaVersion 3，WP4.3）的 Create 建的檔：CreateV1～V3、user_version 3、同一組 kv 與名冊，另放一列 publish_meta</summary>
    private void CreateV3File()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var connection = Connect(SyncStoreOpenMode.Create);
        Exec(connection, null, SyncStoreSchema.ConnectionPragmas);
        using var tx = connection.BeginTransaction();
        Exec(connection, tx, SyncStoreSchema.CreateV1);
        Exec(connection, tx, SyncStoreSchema.CreateV2);
        Exec(connection, tx, SyncStoreSchema.CreateV3);
        Exec(connection, tx, "PRAGMA user_version = 3;");
        var kv = new (string Key, string Value)[]
        {
            (SyncStoreSchema.SchemaMinReader, "1"),
            (SyncStoreSchema.DeviceId, self.DeviceId),
            (SyncStoreSchema.PairedAt, PairedAt.ToString(CultureInfo.InvariantCulture)),
            (SyncStoreSchema.NextDeviceSeq, "1"),
            (SyncStoreSchema.SeenInstanceId, Server.InstanceId),
            (SyncStoreSchema.SeenRestoreGeneration, "0"),
            (SyncStoreSchema.Reconcile, "0"),
            (SyncStoreSchema.RegistryRev, "3"),
        };
        foreach (var (key, value) in kv)
            Exec(connection, tx, "INSERT INTO kv (key, value) VALUES ($k, $v)", ("$k", key), ("$v", value));
        Exec(connection, tx, "INSERT INTO trust_registry (device_id, json) VALUES ($id, $json)", ("$id", self.DeviceId), ("$json", SyncJson.Serialize(self.Entry())));
        Exec(connection, tx, "INSERT INTO publish_meta (conversation_id, created_written) VALUES ($id, 1)", ("$id", new string('a', 32)));
        tx.Commit();
    }

    [Fact]
    public void v3的檔開啟時補上v4_v1到v3物件的SQL逐字不變_min_reader仍是1()
    {
        CreateV3File();
        var before = Schema();
        before.Keys.Should().NotContain(SyncStoreSchema.V4Tables);

        using (var store = Reopen())
        {
            store.Pragma("user_version").Should().Be("4");
            store.GetKv(SyncStoreSchema.SchemaMinReader).Should().Be("1", "只加表：v1～v3 的程式照樣能開（回滾不必還原）");
            store.ConversationTablesReady.Should().BeTrue();
            store.LoadPublishMeta().Should().ContainKey(new string('a', 32), "v3 的資料不動");
        }

        var after = Schema();
        foreach (var (name, sql) in before)
            after.Should().ContainKey(name).WhoseValue.Should().Be(sql, $"v1～v3 的 {name} 結構逐字不變");
        after.Keys.Except(before.Keys).Where(n => !n.StartsWith("sqlite_", StringComparison.Ordinal)).Should().BeEquivalentTo(V4Objects, "只多 v4 的表與索引");
    }

    [Fact]
    public void 新建的檔是v4()
    {
        using (var store = Create())
        {
            store.Pragma("user_version").Should().Be("4");
            store.GetKv(SyncStoreSchema.SchemaMinReader).Should().Be("1");
            store.ConversationTablesReady.Should().BeTrue();
        }

        Schema().Keys.Should().Contain(V4Objects);
        SyncStore.SchemaVersion.Should().Be(4);
        SyncStore.MinReaderVersion.Should().Be(1, "計畫 4.3 第 5 點");
    }

    [Fact]
    public void v4的表_欄位照計畫5點7()
    {
        Create().Dispose();

        SyncStoreSchema.V4Tables.Should().Equal("log_state", "handoffs_local", "path_cache");
        SyncStoreSchema.V4Indexes.Should().Equal("ix_log_state_conversation");
        Columns("log_state").Should().Equal(
            "log_key", "log_id", "conversation_id", "kind", "local_path", "is_main", "gen", "acked_length", "acked_chain", "head_hash", "tail_hash",
            "last_size", "last_write", "held", "updated_at");
        Columns("handoffs_local").Should().Equal("handoff_id", "conversation_id", "role", "state", "detail_json", "deadline_at", "updated_at");
        Columns("path_cache").Should().Equal("repo_key", "local_root", "source", "confirmed", "updated_at");
        Columns("leases").Should().Equal(["conversation_id", "owner_device_id", "epoch", "state", "updated_at"], "v2 的表，第 5 階段開始寫");
        Columns("backfill_state").Should().Contain("logs_done_at");
    }

    [Fact]
    public void CreateV4重跑不改結構()
    {
        Create().Dispose();
        var created = Schema();

        Raw(SyncStoreSchema.CreateV4);
        Raw(SyncStoreSchema.CreateV4);
        Reopen().Dispose();

        Schema().Should().Equal(created);
    }

    [Fact]
    public void v4缺了表或索引_開啟時補上()
    {
        Create().Dispose();
        Raw("DROP INDEX ix_log_state_conversation; DROP TABLE path_cache;");

        using (var store = Reopen())
            store.ConversationTablesReady.Should().BeTrue();

        Schema().Keys.Should().Contain(["path_cache", "ix_log_state_conversation"], "user_version 夠新也要 v4 的表與索引都在才算是這一版");
    }

    [Fact]
    public void 升級到v4失敗_整個交易回滾_照v3開啟_重試成功才算好()
    {
        CreateV3File();

        using var store = SyncStore.Open(DbPath, self.DeviceId, point =>
        {
            if (point == "UpgradeSchema:v4")
                throw new SqliteException("模擬：資料庫被鎖", 5);
        });

        store.ConversationTablesReady.Should().BeFalse("v4 的表不在：log1 的條件不成立");
        store.Pragma("user_version").Should().Be("3", "整個升級交易回滾");
        Schema().Keys.Should().NotContain(SyncStoreSchema.V4Tables);

        store.FaultInjection = null;
        store.RetryUpgrade().Should().BeTrue();
        store.ConversationTablesReady.Should().BeTrue();
        store.Pragma("user_version").Should().Be("4");
        Schema().Keys.Should().Contain(V4Objects);
    }

    [Fact]
    public void leases的新狀態與hand1的游標歸零鍵()
    {
        LocalLeaseStates.All.Should().Equal("handoffOut", "handoffIn", "importFailed", "needsFolder", "fenced");
        SyncStoreSchema.Hand1Replicas.Should().Be("hand1_replicas");
    }

    [Fact]
    public void 最後送進outbox的epoch_沒有列是null()
    {
        var wire = new string('b', 32);
        using var store = Create();
        store.LastConversationEpoch(wire).Should().BeNull();

        var created = new OutboxAppend(SyncStreams.Conversation(wire), ConversationEventKinds.Created, "{}", null, wire, 1);
        var meta = new OutboxAppend(SyncStreams.Conversation(wire), ConversationEventKinds.Meta, "{}", "meta:" + wire, wire, 3);
        store.AppendMany([created, meta], 100);
        store.LastConversationEpoch(wire).Should().Be(3, "deviceSeq 最大的那一筆");
        store.LastConversationEpoch(new string('c', 32)).Should().BeNull("別的對話");
    }
}
