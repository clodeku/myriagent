using System.Globalization;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Rules;
using Myriagent.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace Myriagent.Tests.Sync.Store;

/// <summary>
/// 桌面 sync.db 結構 v3（WP4.3 計畫步 8、D11、D22）：只加表與索引——v1、v2 的檔開啟時補上、v1 與 v2 物件的 sql 文字逐字不變、min_reader 仍是 1；
/// 升級失敗時 ConversationTablesReady 是 false、照舊版結構開啟，RetryUpgrade 成功後才是 true；伺服器換實例在同一個交易清掉對話的狀態，
/// 保留 Ord 與墓碑（墓碑記 needs_redo）；PruneConversation 只刪那個對話、墓碑留下；以及 v3 表的讀寫
/// </summary>
public sealed class SyncStoreV3Tests : IDisposable
{
    private const long PairedAt = 1791500000000;

    private static readonly ServerInstance Server = new("srv-instance-1", 0);

    private static readonly string A = new('a', 32);

    private static readonly string B = new('b', 32);

    private static readonly string C = new('c', 32);

    private static readonly string R = new('e', 32);

    private static readonly string Sha1 = new('1', 64);

    private static readonly string Sha2 = new('2', 64);

    private readonly TempDirectory temp = new("syncstore-v3");
    private readonly TestDevice self = TestDevice.Create("OFFICE-PC");

    private string DbPath => temp.Combine("sync", "sync.db");

    public void Dispose() => temp.Dispose();

    private SyncStore Create() => SyncStore.Create(DbPath, self.DeviceId, PairedAt, Server, new TrustBundle(3, [self.Entry()]));

    private SyncStore Reopen() => SyncStore.Open(DbPath, self.DeviceId);

    private static Action<string> FailAt(string point) => p =>
    {
        if (p == point)
            throw new SqliteException("模擬：資料庫被鎖", 5);
    };

    private SqliteConnection Connect(SyncStoreOpenMode mode, string? path = null)
    {
        var connection = new SqliteConnection(SyncStore.ConnectionString(path ?? DbPath, mode));
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

    private void Raw(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Connect(SyncStoreOpenMode.Open);
        Exec(connection, null, sql, parameters);
    }

    /// <summary>一個查詢的每一列（欄位以 | 串成一個字串，NULL 寫成 ∅），依整列排序</summary>
    private List<string> Rows(string sql)
    {
        using var connection = Connect(SyncStoreOpenMode.Open);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var fields = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                fields[i] = reader.IsDBNull(i) ? "∅" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            rows.Add(string.Join("|", fields));
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>sqlite_master 的每個表與索引（名稱 → 建立的 sql 文字）</summary>
    private Dictionary<string, string?> Schema(string? path = null)
    {
        using var connection = Connect(SyncStoreOpenMode.Open, path);
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

    /// <summary>
    /// 照 v2 程式（SchemaVersion 2，WP4.1）的 Create 建的檔：CreateV1＋CreateV2、user_version 2、同一組 kv 與名冊；
    /// 另放一筆 outbox、一個游標、一列 publish_state，確認升級不動舊的資料。<paramref name="version"/> 是 1 時只有 v1（照 v1 程式）
    /// </summary>
    private void CreateOldFile(int version = 2)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var connection = Connect(SyncStoreOpenMode.Create);
        Exec(connection, null, SyncStoreSchema.ConnectionPragmas);
        using var tx = connection.BeginTransaction();
        Exec(connection, tx, SyncStoreSchema.CreateV1);
        if (version >= 2)
            Exec(connection, tx, SyncStoreSchema.CreateV2);
        Exec(connection, tx, $"PRAGMA user_version = {version};");
        var kv = new (string Key, string Value)[]
        {
            (SyncStoreSchema.SchemaMinReader, "1"),
            (SyncStoreSchema.DeviceId, self.DeviceId),
            (SyncStoreSchema.PairedAt, PairedAt.ToString(CultureInfo.InvariantCulture)),
            (SyncStoreSchema.NextDeviceSeq, "2"),
            (SyncStoreSchema.SeenInstanceId, Server.InstanceId),
            (SyncStoreSchema.SeenRestoreGeneration, "0"),
            (SyncStoreSchema.Reconcile, "0"),
            (SyncStoreSchema.RegistryRev, "3"),
        };
        foreach (var (key, value) in kv)
            Exec(connection, tx, "INSERT INTO kv (key, value) VALUES ($k, $v)", ("$k", key), ("$v", value));
        Exec(connection, tx, "INSERT INTO trust_registry (device_id, json) VALUES ($id, $json)", ("$id", self.DeviceId), ("$json", SyncJson.Serialize(self.Entry())));
        var json = SyncJson.Serialize(new { n = 1 });
        var (enc, payload) = PayloadRules.Encode(json);
        Exec(connection, tx, """
            INSERT INTO outbox (device_seq, stream, kind, enc, payload, payload_sha256, coalesce_key, state, created_at)
            VALUES (1, $stream, $kind, $enc, $payload, $sha, NULL, 'pending', 100)
            """, ("$stream", SyncStreams.Catalog), ("$kind", SyncEventKinds.AccountUpdate), ("$enc", enc), ("$payload", payload), ("$sha", PayloadRules.Sha256Hex(json)));
        Exec(connection, tx, "INSERT INTO cursors (cls, seq) VALUES ($c, 5)", ("$c", SyncClasses.Catalog));
        if (version >= 2)
            Exec(connection, tx, "INSERT INTO publish_state (conversation_id, acked_seq, needs_redo) VALUES ($id, 7, 1)", ("$id", A));
        tx.Commit();
    }

    private static IEnumerable<string> V3Objects => SyncStoreSchema.V3Tables.Concat(SyncStoreSchema.V3Indexes);

    // ── 結構 ──

    [Fact]
    public void v2的檔開啟時補上v3_v1與v2物件的SQL逐字不變()
    {
        CreateOldFile();
        var before = Schema();
        before.Keys.Should().NotContain(SyncStoreSchema.V3Tables, "照 v2 建的檔還沒有 v3 的表");

        using (var store = Reopen())
        {
            store.Pragma("user_version").Should().Be("4");
            store.GetKv(SyncStoreSchema.SchemaMinReader).Should().Be("1", "只加表，v1、v2 的程式照樣能開");
            store.ConversationTablesReady.Should().BeTrue();
            store.NextDeviceSeq.Should().Be(2);
            store.LoadOutbox().Should().ContainSingle().Which.DeviceSeq.Should().Be(1);
            store.LoadCursors().Should().Equal(new Dictionary<string, long> { [SyncClasses.Catalog] = 5 });
            store.LoadNeedsRedo().Should().Equal(A);
        }

        var after = Schema();
        foreach (var (name, sql) in before)
            after.Should().ContainKey(name).WhoseValue.Should().Be(sql, $"v1、v2 的 {name} 結構逐字不變");
        after.Keys.Except(before.Keys).Where(n => !n.StartsWith("sqlite_", StringComparison.Ordinal))
            .Should().BeEquivalentTo(V3Objects.Concat(SyncStoreSchema.V4Tables).Concat(SyncStoreSchema.V4Indexes), "只多 v3（與 v4）的表與索引");
        Rows("SELECT conversation_id, acked_seq, needs_redo FROM publish_state").Should().Equal([$"{A}|7|1"], "v2 的資料不動");
    }

    [Fact]
    public void v1的檔開啟時一次補上v2與v3()
    {
        CreateOldFile(version: 1);

        using (var store = Reopen())
        {
            store.Pragma("user_version").Should().Be("4");
            store.ConversationTablesReady.Should().BeTrue();
        }

        Schema().Keys.Should().Contain(SyncStoreSchema.V2Tables).And.Contain(V3Objects);
    }

    [Fact]
    public void 新建的檔是v3_min_reader是1()
    {
        using (var store = Create())
        {
            store.Pragma("user_version").Should().Be("4");
            store.GetKv(SyncStoreSchema.SchemaMinReader).Should().Be("1");
            store.ConversationTablesReady.Should().BeTrue();
        }

        Schema().Keys.Should().Contain(SyncStoreSchema.V2Tables).And.Contain(V3Objects);
        using var reopened = Reopen();
        reopened.ConversationTablesReady.Should().BeTrue();
    }

    [Fact]
    public void v3的表_欄位照D11()
    {
        Create().Dispose();

        Columns("publish_meta").Should().Equal(
            "conversation_id", "created_written", "created_acked", "policy", "meta_sha256", "queue_sha256", "items_file_sha256",
            "blocked", "blocked_at", "deleted_at", "created_retries", "retry_at", "ever_created");
        Columns("published_items").Should().Equal("conversation_id", "item_id", "ord", "src_sha256", "wire_sha256", "truncated", "removed");
        Columns("replica_meta").Should().Equal("conversation_id", "owner_device_id", "epoch", "meta_json", "deleted", "seq");
        Columns("replica_items").Should().Equal("conversation_id", "item_id", "ord", "payload", "seq");
    }

    [Fact]
    public void CreateV3重跑不改結構()
    {
        Create().Dispose();
        var created = Schema();

        Raw(SyncStoreSchema.CreateV3);
        Raw(SyncStoreSchema.CreateV3);
        Reopen().Dispose();

        Schema().Should().Equal(created);
    }

    [Fact]
    public void user_version不降回來()
    {
        Create().Dispose();
        Raw("PRAGMA user_version = 7;");

        using var store = Reopen();

        store.Pragma("user_version").Should().Be("7", "較新版程式開過的檔：user_version 取 max(現值, 4)");
        store.ConversationTablesReady.Should().BeTrue("表都在");
    }

    [Fact]
    public void v3缺了表或索引_開啟時補上()
    {
        Create().Dispose();
        Raw("DROP INDEX ix_replica_items_ord; DROP TABLE published_items;");

        using (var store = Reopen())
            store.ConversationTablesReady.Should().BeTrue();

        Schema().Keys.Should().Contain(["published_items", "ix_replica_items_ord"], "user_version 夠新也要表與索引都在才算是這一版");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void 升級失敗_ConversationTablesReady為false_換伺服器實例不丟例外(int version)
    {
        CreateOldFile(version);

        using var store = SyncStore.Open(DbPath, self.DeviceId, FailAt("UpgradeSchema:v3"));

        store.ConversationTablesReady.Should().BeFalse("升級失敗：照舊版結構開啟，不發布對話");
        store.Pragma("user_version").Should().Be(version.ToString(CultureInfo.InvariantCulture), "整個升級交易回滾");
        Schema().Keys.Should().NotContain(SyncStoreSchema.V3Tables);
        store.LoadOutbox().Should().ContainSingle("第 3 階段照常");

        FluentActions.Invoking(() => store.BeginServerChange(Server with { RestoreGeneration = 1 })).Should().NotThrow("只對存在的表做");
        store.IsReconciling.Should().BeTrue();
        store.LoadCursors().Should().BeEmpty();
        if (version >= 2)
            Rows("SELECT conversation_id FROM publish_state").Should().BeEmpty("v2 的表在就照樣清");
    }

    [Fact]
    public void RetryUpgrade成功後為true()
    {
        CreateOldFile();
        using var store = SyncStore.Open(DbPath, self.DeviceId, FailAt("UpgradeSchema:v3"));
        store.ConversationTablesReady.Should().BeFalse();

        store.RetryUpgrade().Should().BeFalse("還是失敗：回 false、不丟例外");
        store.ConversationTablesReady.Should().BeFalse();

        store.FaultInjection = null;
        store.RetryUpgrade().Should().BeTrue();

        store.ConversationTablesReady.Should().BeTrue();
        store.Pragma("user_version").Should().Be("4");
        Schema().Keys.Should().Contain(V3Objects);
        store.LoadPublishMeta().Should().BeEmpty("v3 的表可以用了");
    }

    [Fact]
    public void RetryUpgrade_已是這一版_不開寫入交易()
    {
        Create().Dispose();
        using var store = Reopen();
        using var holder = Connect(SyncStoreOpenMode.Open);
        using var tx = holder.BeginTransaction();
        Exec(holder, tx, "UPDATE kv SET value = value WHERE key = $k", ("$k", SyncStoreSchema.PairedAt));

        store.RetryUpgrade().Should().BeTrue("另一條連線拿著寫入鎖也照樣回 true");
        tx.Rollback();
    }

    [Fact]
    public void 回滾到v2的程式照樣能開()
    {
        using (var store = Create())
        {
            store.UpsertPublishMeta(new PublishMetaRow(A, CreatedWritten: true));
            store.AppendMany([new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.AccountUpdate, SyncJson.Serialize(new { n = 1 }), null)], 100);
        }

        // v1、v2 物件的 sql 文字與只跑 CreateV1＋CreateV2 的檔逐字相同（v3 沒動到舊的物件）
        var reference = temp.Combine("reference.db");
        using (var connection = Connect(SyncStoreOpenMode.Create, reference))
        {
            Exec(connection, null, SyncStoreSchema.CreateV1);
            Exec(connection, null, SyncStoreSchema.CreateV2);
        }

        var old = Schema(reference);
        var current = Schema();
        foreach (var (name, sql) in old)
            current.Should().ContainKey(name).WhoseValue.Should().Be(sql, $"{name} 和 v2 程式建的一樣");

        // v2 的程式開檔時做的事：min_reader 不大於 2；v2 的表與索引都在、user_version ≥ 2 就不升級；寫入只用 v1 的欄位
        using var v2 = Connect(SyncStoreOpenMode.Open);
        using (var cmd = v2.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM kv WHERE key = 'schema_min_reader'";
            int.Parse((string)cmd.ExecuteScalar()!, CultureInfo.InvariantCulture).Should().BeLessThanOrEqualTo(2);
        }

        using (var cmd = v2.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture).Should().BeGreaterThanOrEqualTo(2);
        }

        var wanted = SyncStoreSchema.V2Tables.Concat(SyncStoreSchema.V2Indexes).ToList();
        current.Keys.Should().Contain(wanted, "v2 程式的 IsSchemaCurrent 成立：不開寫入交易");
        using (var tx = v2.BeginTransaction())
        {
            Exec(v2, tx, SyncStoreSchema.CreateV2);
            Exec(v2, tx, "UPDATE outbox SET state = 'sent' WHERE device_seq = 1");
            Exec(v2, tx, """
                INSERT INTO outbox (device_seq, stream, kind, enc, payload, payload_sha256, coalesce_key, state, created_at, conversation_id, epoch)
                VALUES (2, 'catalog', 'account.update', 'json', '{}', $sha, NULL, 'pending', 200, NULL, NULL)
                """, ("$sha", PayloadRules.Sha256Hex("{}")));
            tx.Commit();
        }

        Rows("SELECT conversation_id, created_written FROM publish_meta").Should().Equal([$"{A}|1"], "v3 的資料不受影響");
        using var reopened = Reopen();
        reopened.LoadOutbox().Select(e => e.DeviceSeq).Should().Equal(1, 2);
    }

    [Fact]
    public void conv_armed預設沒有()
    {
        using var store = Create();

        store.GetKv(SyncStoreSchema.ConvArmed).Should().BeNull("預設 false：上一次進 Live 沒有 conv1（D13）");
        store.GetKv(SyncStoreSchema.PublisherDlpVersion).Should().BeNull();
        store.GetKv(SyncStoreSchema.PublisherNextDeviceSeq).Should().BeNull();
        store.GetKv(SyncStoreSchema.PublishServer).Should().BeNull();
        store.LoadPublishMeta().Should().BeEmpty();
        store.LoadNeedsRedo().Should().BeEmpty();
        store.GetConversationCursors().Should().BeEmpty();
    }

    // ── 伺服器換實例 ──

    /// <summary>每張對話表的內容（換實例前後比對）</summary>
    private Dictionary<string, List<string>> ConversationRows() => new(StringComparer.Ordinal)
    {
        ["publish_meta"] = Rows("SELECT * FROM publish_meta"),
        ["published_items"] = Rows("SELECT * FROM published_items"),
        ["replica_meta"] = Rows("SELECT * FROM replica_meta"),
        ["replica_items"] = Rows("SELECT * FROM replica_items"),
        ["conversation_cursors"] = Rows("SELECT * FROM conversation_cursors"),
        ["publish_state"] = Rows("SELECT * FROM publish_state"),
        ["backfill_state"] = Rows("SELECT * FROM backfill_state"),
        ["replica_state"] = Rows("SELECT * FROM replica_state"),
        ["leases"] = Rows("SELECT * FROM leases"),
    };

    private void SeedConversations(SyncStore store)
    {
        store.UpsertPublishMeta(new PublishMetaRow(A, true, true, ConversationSyncPolicies.Full, Sha1, Sha2, Sha1, "bug", 50, null, 2, 60, EverCreated: true));
        store.UpsertPublishMeta(new PublishMetaRow(B, true, false, ConversationSyncPolicies.Metadata, Sha1, null, Sha2, ConversationRejectCodes.Deleted, 70, 80, 1, 90));
        Raw("""
            INSERT INTO published_items (conversation_id, item_id, ord, src_sha256, wire_sha256, truncated, removed) VALUES
              ($a, 'i1', 1, $s1, $s2, 0, 0), ($a, 'i2', 5, $s1, $s2, 1, 0), ($a, 'i3', 9, NULL, NULL, 0, 1), ($b, 'i1', 3, $s1, $s2, 0, 0)
            """, ("$a", A), ("$b", B), ("$s1", Sha1), ("$s2", Sha2));
        store.SetConversationCursors(new Dictionary<string, long> { [SyncClasses.Meta] = 11, [SyncStreams.Conversation(R)] = 12 });
        store.UpsertReplicaMeta(new ReplicaMetaRow(R, "d_OTHER", 1, "{}", false, 12));
        store.UpsertReplicaItem(new ReplicaItemRow(R, "r1", 1, "{}", 12));
        store.SetNeedsRedo(A, false);
        store.MarkBackfilled(A, 100, 0);
        Raw("INSERT INTO replica_state (conversation_id, snapshot_seq, last_seq) VALUES ($r, 1, 12)", ("$r", R));
        Raw("INSERT INTO leases (conversation_id, owner_device_id, epoch, state, updated_at) VALUES ($a, 'd_SELF', 1, 'owned', 5)", ("$a", A));
    }

    [Fact]
    public void BeginServerChange_同一個交易清空對話表_保留Ord與墓碑()
    {
        using var store = Create();
        SeedConversations(store);
        var before = ConversationRows();
        var restored = Server with { RestoreGeneration = 1 };

        store.FaultInjection = FailAt("BeginServerChange:cleared");
        FluentActions.Invoking(() => store.BeginServerChange(restored)).Should().Throw<SqliteException>();
        store.FaultInjection = null;
        ConversationRows().Should().BeEquivalentTo(before, "交易回滾：列都還在");
        store.IsReconciling.Should().BeFalse();

        store.BeginServerChange(restored);

        Rows("SELECT conversation_id, item_id, ord, src_sha256, wire_sha256, truncated, removed FROM published_items").Should().Equal(
            $"{A}|i1|1|∅|∅|0|0", $"{A}|i2|5|∅|∅|1|0", $"{A}|i3|9|∅|∅|0|0", $"{B}|i1|3|∅|∅|0|0");
        var meta = store.LoadPublishMeta();
        meta[A].Should().Be(new PublishMetaRow(A, EverCreated: true), "沒有墓碑的整列重設；曾經建立過保留（還原的伺服器上可能還有它，合併前審查 F3）");
        meta[B].Should().Be(new PublishMetaRow(B, DeletedAt: 80), "墓碑只留 deleted_at");
        store.LoadNeedsRedo().Should().Equal([B], "墓碑記 needs_redo（補送 conv.deleted）；其他的整份重新發布");
        store.GetConversationCursors().Should().BeEmpty();
        store.LoadReplicaMeta().Should().BeEmpty();
        store.LoadReplicaItems(R).Should().BeEmpty();
        store.LoadBackfilled().Should().BeEmpty();
        Rows("SELECT * FROM replica_state").Should().BeEmpty();
        Rows("SELECT conversation_id FROM leases").Should().Equal([A], "租約不動");
        store.IsReconciling.Should().BeTrue();
    }

    // ── published_items 與 Ord ──

    [Fact]
    public void published_items依對話分開()
    {
        using var store = Create();
        SeedConversations(store);

        store.LoadPublishedItems(A).Select(r => (r.ItemId, r.Ord, r.Truncated, r.Removed)).Should().Equal(("i1", 1L, false, false), ("i2", 5L, true, false), ("i3", 9L, false, true));
        store.LoadPublishedItems(B).Should().ContainSingle().Which.Should().Be(new PublishedItemRow("i1", 3, Sha1, Sha2, false, false));
        store.LoadPublishedItems(C).Should().BeEmpty();

        var hint = store.OrdHintOf(A)!;
        hint.PublishedOrds.Should().Equal(new Dictionary<string, long> { ["i1"] = 1, ["i2"] = 5, ["i3"] = 9 });
        hint.PublishedMax.Should().Be(9, "已移除的項目也算（Ord 不重用）");
        store.OrdHintOf(B)!.PublishedMax.Should().Be(3);
        store.OrdHintOf(C).Should().BeNull("沒發布過");

        store.ForgetDigests(A, "i1");
        store.LoadPublishedItems(A)[0].Should().Be(new PublishedItemRow("i1", 1, null, null, false, false));
        store.LoadPublishedItems(A)[1].SrcSha256.Should().Be(Sha1, "只清指定的項目");
        store.LoadPublishMeta()[A].ItemsFileSha256.Should().BeNull("items.json 的捷徑一起清");
        store.LoadPublishMeta()[B].ItemsFileSha256.Should().Be(Sha2, "另一個對話不動");

        store.ForgetDigests(A, null);
        store.LoadPublishedItems(A).Should().OnlyContain(r => r.SrcSha256 == null && r.WireSha256 == null);
        store.LoadPublishedItems(A).Select(r => r.Ord).Should().Equal([1L, 5L, 9L], "列與 Ord 保留");
        store.LoadPublishedItems(B).Single().SrcSha256.Should().Be(Sha1, "同一個項目 id 在另一個對話不受影響");

        store.MarkRemoved(B, "i1").Should().BeTrue();
        store.LoadPublishedItems(B).Single().Should().Be(new PublishedItemRow("i1", 3, null, null, false, true));
        store.MarkRemoved(B, "nope").Should().BeFalse("沒發布過的項目");
        store.LoadPublishedItems(A).Single(r => r.ItemId == "i1").Removed.Should().BeFalse();
    }

    [Fact]
    public void ForgetDigests_中途丟例外_都沒清()
    {
        using var store = Create();
        SeedConversations(store);
        store.FaultInjection = FailAt("ForgetDigests:commit");

        FluentActions.Invoking(() => store.ForgetDigests(A, null)).Should().Throw<SqliteException>();

        store.FaultInjection = null;
        store.LoadPublishedItems(A)[0].SrcSha256.Should().Be(Sha1);
        store.LoadPublishMeta()[A].ItemsFileSha256.Should().Be(Sha1);
    }

    // ── 修剪 ──

    [Fact]
    public void PruneConversation只刪那個對話_墓碑留下()
    {
        using var store = Create();
        SeedConversations(store);
        store.SetNeedsRedo(B, true);
        store.SetConversationCursors(new Dictionary<string, long> { [SyncStreams.Conversation(A)] = 3, [SyncStreams.Conversation(B)] = 4 });
        store.UpsertReplicaMeta(new ReplicaMetaRow(C, "d_OTHER", 1, "{}", false, 2));
        var rRows = Rows($"SELECT * FROM replica_items WHERE conversation_id = '{R}'");

        store.FaultInjection = FailAt("PruneConversation:commit");
        FluentActions.Invoking(() => store.PruneConversation(B)).Should().Throw<SqliteException>();
        store.FaultInjection = null;
        store.LoadPublishedItems(B).Should().ContainSingle("交易回滾");

        store.PruneConversation(B);

        var meta = store.LoadPublishMeta();
        meta[B].Should().Be(new PublishMetaRow(B, DeletedAt: 80), "墓碑只留 deleted_at");
        store.LoadNeedsRedo().Should().Contain(B, "還沒補送的 conv.deleted 留著");
        store.LoadPublishedItems(B).Should().BeEmpty();
        store.GetConversationCursors().Should().NotContainKey(SyncStreams.Conversation(B));
        meta[A].CreatedWritten.Should().BeTrue("別的對話不動");
        store.LoadPublishedItems(A).Should().HaveCount(3);

        store.PruneConversation(A);

        store.LoadPublishMeta().Should().NotContainKey(A, "沒有墓碑：整列刪掉");
        store.LoadPublishedItems(A).Should().BeEmpty();
        Rows($"SELECT * FROM publish_state WHERE conversation_id = '{A}'").Should().BeEmpty();
        store.LoadBackfilled().Should().NotContainKey(A);
        Rows($"SELECT * FROM leases WHERE conversation_id = '{A}'").Should().BeEmpty();
        store.GetConversationCursors().Should().Equal(new Dictionary<string, long> { [SyncClasses.Meta] = 11, [SyncStreams.Conversation(R)] = 12 });

        store.PruneConversation(R);
        store.LoadReplicaMeta().Keys.Should().Equal([C], "複本 R 刪掉、C 不動");
        rRows.Should().NotBeEmpty();
        Rows($"SELECT * FROM replica_items WHERE conversation_id = '{R}'").Should().BeEmpty();
        Rows("SELECT * FROM replica_state").Should().BeEmpty();
    }

    // ── publish_meta、publish_state、backfill_state、游標、複本 ──

    [Fact]
    public void publish_meta整列寫入與讀回_封鎖與解除_墓碑保留第一次的時間()
    {
        using (var store = Create())
        {
            var row = new PublishMetaRow(A, true, true, ConversationSyncPolicies.Paused, Sha1, Sha2, Sha1, null, null, null, 3, 99, EverCreated: true);
            store.UpsertPublishMeta(row);
            store.LoadPublishMeta()[A].Should().Be(row);
            store.UpsertPublishMeta(row with { CreatedAcked = false, Policy = null, RetryAt = null });
            store.LoadPublishMeta()[A].Should().Be(row with { CreatedAcked = false, Policy = null, RetryAt = null }, "整列換掉");

            store.SetBlocked(A, "bug", 10);
            store.SetBlocked(B, ConversationRejectCodes.NotOwner, 11);
            store.SetBlocked(C, ConversationRejectCodes.Fenced, 12);
            store.LoadPublishMeta()[A].Should().Match<PublishMetaRow>(r => r.Blocked == "bug" && r.BlockedAt == 10 && r.MetaSha256 == Sha1, "其他欄位不動");
            store.LoadPublishMeta()[B].Should().Be(new PublishMetaRow(B, Blocked: ConversationRejectCodes.NotOwner, BlockedAt: 11), "沒有列就新增");

            store.ClearBlocks("bug").Should().Be(1);
            store.LoadPublishMeta()[A].Blocked.Should().BeNull();
            store.LoadPublishMeta()[A].BlockedAt.Should().BeNull();
            store.LoadPublishMeta()[B].Blocked.Should().Be(ConversationRejectCodes.NotOwner, "只解除指定原因的");
            store.ClearBlocks(null).Should().Be(2);
            store.LoadPublishMeta().Values.Should().OnlyContain(r => r.Blocked == null);

            store.SetDeleted(C, 200);
            store.SetDeleted(C, 300);
            store.SetDeleted(R, 400);
        }

        using var reopened = Reopen();
        reopened.LoadPublishMeta()[C].DeletedAt.Should().Be(200, "保留第一次的時間");
        reopened.LoadPublishMeta()[R].Should().Be(new PublishMetaRow(R, DeletedAt: 400));
        reopened.LoadPublishMeta()[A].CreatedRetries.Should().Be(3);
    }

    [Fact]
    public void needs_redo與回補完成_讀寫()
    {
        using var store = Create();

        store.SetNeedsRedo(A, true);
        store.SetNeedsRedo(B, true);
        store.SetNeedsRedo(B, false);
        store.MarkBackfilled(A, 100, 5);
        store.MarkBackfilled(A, 150, 0);
        store.MarkBackfilled(C, 120, null);

        store.LoadNeedsRedo().Should().BeEquivalentTo([A]);
        store.LoadBackfilled().Should().Equal(new Dictionary<string, long> { [A] = 150, [C] = 120 });
        Rows("SELECT conversation_id, acked_seq, needs_redo FROM publish_state").Should().Equal($"{A}|0|1", $"{B}|0|0");
        Rows("SELECT conversation_id, items_done_at, logs_done_at, bytes_left FROM backfill_state").Should().Equal($"{A}|150|∅|0", $"{C}|120|∅|∅");
    }

    [Fact]
    public void 對話游標_讀寫_只動列到的_第3階段的游標分開()
    {
        using (var store = Create())
        {
            store.SetConversationCursors(new Dictionary<string, long> { [SyncClasses.Meta] = 5, [SyncStreams.Conversation(R)] = 7 });
            store.SetConversationCursors(new Dictionary<string, long> { [SyncClasses.Meta] = 9 });
            store.SetConversationCursors(new Dictionary<string, long>());
        }

        using var reopened = Reopen();
        reopened.GetConversationCursors().Should().Equal(new Dictionary<string, long> { [SyncClasses.Meta] = 9, [SyncStreams.Conversation(R)] = 7 });
        reopened.LoadCursors().Should().BeEmpty("v1 的 cursors 只放第 3 階段的類別");

        reopened.FaultInjection = FailAt("SetConversationCursors:commit");
        FluentActions.Invoking(() => reopened.SetConversationCursors(new Dictionary<string, long> { [SyncClasses.Meta] = 99 })).Should().Throw<SqliteException>();
        reopened.FaultInjection = null;
        reopened.GetConversationCursors()[SyncClasses.Meta].Should().Be(9);
    }

    [Fact]
    public void 複本_meta與項目的讀寫_丟項目留meta_丟整個複本()
    {
        using var store = Create();
        store.UpsertReplicaMeta(new ReplicaMetaRow(R, "d_OTHER", 1, """{"title":"一"}""", false, 3));
        store.UpsertReplicaMeta(new ReplicaMetaRow(R, "d_OTHER", 2, """{"title":"二"}""", false, 4));
        store.UpsertReplicaMeta(new ReplicaMetaRow(C, "d_THIRD", 1, "{}", false, 5));
        store.UpsertReplicaItem(new ReplicaItemRow(R, "i2", 2, """{"v":1}""", 6));
        store.UpsertReplicaItem(new ReplicaItemRow(R, "i1", 1, """{"v":1}""", 7));
        store.UpsertReplicaItem(new ReplicaItemRow(R, "i2", 2, """{"v":2}""", 8));
        store.UpsertReplicaItem(new ReplicaItemRow(C, "i1", 1, "{}", 9));

        store.LoadReplicaMeta()[R].Should().Be(new ReplicaMetaRow(R, "d_OTHER", 2, """{"title":"二"}""", false, 4));
        store.LoadReplicaItems(R).Should().Equal(new ReplicaItemRow(R, "i1", 1, """{"v":1}""", 7), new ReplicaItemRow(R, "i2", 2, """{"v":2}""", 8));

        store.DeleteReplicaItem(R, "i1");
        store.LoadReplicaItems(R).Select(i => i.ItemId).Should().Equal("i2");

        store.DropReplicaItems(R);
        store.LoadReplicaItems(R).Should().BeEmpty();
        store.LoadReplicaMeta().Should().ContainKey(R, "政策不是 full：留 meta");

        store.FaultInjection = FailAt("DropReplica:commit");
        FluentActions.Invoking(() => store.DropReplica(C)).Should().Throw<SqliteException>();
        store.FaultInjection = null;
        store.LoadReplicaItems(C).Should().ContainSingle("交易回滾");

        store.DropReplica(C);
        store.LoadReplicaMeta().Keys.Should().Equal([R]);
        store.LoadReplicaItems(C).Should().BeEmpty();
    }
}
