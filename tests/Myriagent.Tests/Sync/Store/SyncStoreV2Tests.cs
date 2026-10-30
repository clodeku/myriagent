using System.Globalization;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Rules;
using Myriagent.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace Myriagent.Tests.Sync.Store;

/// <summary>
/// sync.db 結構 v2（WP4.1 步 11，計畫 5.11、D55）：只加表與索引——v1 的檔開啟時補上、v1 各表的結構（sqlite_master 的 sql 文字）與資料不變、
/// min_reader 仍是 1（回滾到 v1 的程式照樣開）；CreateV2 重跑不出錯；outbox 帶對話的事件寫進 conversation_id／epoch，合併鍵與對話不符丟 ArgumentException
/// </summary>
public sealed class SyncStoreV2Tests : IDisposable
{
    private const long PairedAt = 1791500000000;

    private static readonly ServerInstance Server = new("srv-instance-1", 0);

    private static readonly string ConversationId = new('c', 32);

    private static readonly string OtherConversation = new('d', 32);

    private static readonly string[] V2Tables =
        ["conversation_cursors", "leases", "replica_state", "publish_state", "backfill_state", "commands_seen", "remote_audit", "media_refs_local"];

    private readonly TempDirectory temp = new("syncstore-v2");
    private readonly TestDevice self = TestDevice.Create("OFFICE-PC");

    private string DbPath => temp.Combine("sync", "sync.db");

    public void Dispose() => temp.Dispose();

    private SyncStore Create() => SyncStore.Create(DbPath, self.DeviceId, PairedAt, Server, new TrustBundle(3, [self.Entry()]));

    private SyncStore Reopen() => SyncStore.Open(DbPath, self.DeviceId);

    private static OutboxAppend Conv(string kind, string json, string? key, long epoch = 1, string? conversationId = null)
    {
        var id = conversationId ?? ConversationId;
        return new OutboxAppend(SyncStreams.Conversation(id), kind, json, key, id, epoch);
    }

    private static OutboxAppend Plain(int n, string? key = null) =>
        new(SyncStreams.Catalog, SyncEventKinds.AccountUpdate, SyncJson.Serialize(new { n }), key);

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

    /// <summary>
    /// 照 v1 程式（SchemaVersion 1）的 Create 建的檔：只有 CreateV1、user_version 1、同一組 kv 與名冊；另放一筆 outbox 與一個游標，
    /// 確認升級不動 v1 的資料
    /// </summary>
    private void CreateV1File()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        using var connection = Connect(SyncStoreOpenMode.Create);
        Exec(connection, null, SyncStoreSchema.ConnectionPragmas);
        using var tx = connection.BeginTransaction();
        Exec(connection, tx, SyncStoreSchema.CreateV1);
        Exec(connection, tx, "PRAGMA user_version = 1;");
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
        tx.Commit();
    }

    /// <summary>sqlite_master 的每個物件（表、索引；名稱 → 建立的 sql 文字）</summary>
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

    // ── 結構 ──

    [Fact]
    public void v1的檔_開啟時補上v2的表_v1各表的結構與資料不變_min_reader仍是1()
    {
        CreateV1File();
        var before = Schema();
        before.Keys.Should().NotContain(V2Tables, "照 v1 建的檔還沒有 v2 的表");

        using (var store = Reopen())
        {
            store.Pragma("user_version").Should().Be("4", "目前的版本（v4：第 5 階段的表）");
            store.GetKv(SyncStoreSchema.SchemaMinReader).Should().Be("1", "只加表，v1 的程式照樣能開（D55）");
            store.NextDeviceSeq.Should().Be(2);
            store.LoadOutbox().Should().ContainSingle().Which.Should().Match<OutboxEntry>(e => e.DeviceSeq == 1 && e.State == OutboxState.Pending && e.ConversationId == null);
            store.LoadCursors().Should().Equal(new Dictionary<string, long> { [SyncClasses.Catalog] = 5 });
        }

        var after = Schema();
        foreach (var (name, sql) in before)
            after.Should().ContainKey(name).WhoseValue.Should().Be(sql, $"v1 的 {name} 結構不變");
        after.Keys.Should().Contain(V2Tables);
        after.Keys.Except(before.Keys).Where(n => !n.StartsWith("sqlite_", StringComparison.Ordinal))
            .Should().BeEquivalentTo(V2Tables.Concat(["ix_commands_seen_at", "ix_remote_audit_at", "ix_media_refs_local_conversation"])
                .Concat(SyncStoreSchema.V3Tables).Concat(SyncStoreSchema.V3Indexes).Concat(SyncStoreSchema.V4Tables).Concat(SyncStoreSchema.V4Indexes),
                "只多 v2（與 v3、v4）的表與索引");
    }

    [Fact]
    public void 新建的檔就是v2_CreateV2與開啟重跑都不出錯也不改結構()
    {
        using (var store = Create())
        {
            store.Pragma("user_version").Should().Be("4");
            store.GetKv(SyncStoreSchema.SchemaMinReader).Should().Be("1");
        }

        var created = Schema();
        created.Keys.Should().Contain(V2Tables);

        Raw(SyncStoreSchema.CreateV2);
        Raw(SyncStoreSchema.CreateV2);
        Reopen().Dispose();
        using (var reopened = Reopen())
            reopened.Pragma("user_version").Should().Be("4");

        Schema().Should().Equal(created);
    }

    [Fact]
    public void 較新版程式開過的檔_user_version不降回來()
    {
        Create().Dispose();
        Raw("PRAGMA user_version = 5;");

        using var store = Reopen();

        store.Pragma("user_version").Should().Be("5", "user_version 取 max(現值, 4)");
        store.GetKv(SyncStoreSchema.SchemaMinReader).Should().Be("1");
    }

    /// <summary>另一條連線拿著寫入鎖（BEGIN IMMEDIATE＋一筆寫入）</summary>
    private (SqliteConnection Connection, SqliteTransaction Transaction) HoldWriteLock()
    {
        var connection = Connect(SyncStoreOpenMode.Open);
        var tx = connection.BeginTransaction();
        Exec(connection, tx, "UPDATE kv SET value = value WHERE key = $k", ("$k", SyncStoreSchema.PairedAt));
        return (connection, tx);
    }

    [Fact]
    public void 另一條連線拿著寫入鎖_v1的檔照常開啟_這次不升級_下次開啟再補上()
    {
        CreateV1File();
        var (holder, tx) = HoldWriteLock();
        try
        {
            using var store = Reopen();
            store.Pragma("user_version").Should().Be("1", "升級要寫入鎖：這次照 v1 開啟（WP4.1 沒有程式讀寫 v2 的表），不算損毀");
            store.LoadOutbox().Should().ContainSingle();
        }
        finally
        {
            tx.Rollback();
            tx.Dispose();
            holder.Dispose();
        }

        using (var reopened = Reopen())
            reopened.Pragma("user_version").Should().Be("4", "下次開啟補上");
        Schema().Keys.Should().Contain(V2Tables);
    }

    [Fact]
    public void 另一條連線拿著寫入鎖_已是v2的檔不開寫入交易_照常開啟()
    {
        Create().Dispose();
        var (holder, tx) = HoldWriteLock();
        try
        {
            using var store = Reopen();
            store.Pragma("user_version").Should().Be("4");
        }
        finally
        {
            tx.Rollback();
            tx.Dispose();
            holder.Dispose();
        }
    }

    [Fact]
    public void v2缺了索引_開啟時補上()
    {
        Create().Dispose();
        Raw("DROP INDEX ix_remote_audit_at;");

        Reopen().Dispose();

        Schema().Keys.Should().Contain("ix_remote_audit_at", "表與索引都在才算已是這一版");
    }

    [Fact]
    public void 暫時性的SQLite錯誤()
    {
        foreach (var code in new[] { 5, 6, 8, 10, 13, 14 })
            SyncStore.TransientSqliteErrorCode(new SqliteException("x", code)).Should().Be(code);
        SyncStore.TransientSqliteErrorCode(new SqliteException("x", 11)).Should().BeNull("CORRUPT 是損毀");
        SyncStore.TransientSqliteErrorCode(new SqliteException("x", 26)).Should().BeNull("NOTADB 是損毀");
        SyncStore.TransientSqliteErrorCode(new SqliteException("x", 1)).Should().BeNull();
        SyncStore.TransientSqliteErrorCode(new IOException("x")).Should().BeNull();
    }

    [Fact]
    public void v2的表_欄位照D55凍結()
    {
        Create().Dispose();

        Columns("conversation_cursors").Should().Equal("stream", "seq");
        Columns("leases").Should().Equal("conversation_id", "owner_device_id", "epoch", "state", "updated_at");
        Columns("replica_state").Should().Equal("conversation_id", "snapshot_seq", "last_seq");
        Columns("publish_state").Should().Equal("conversation_id", "acked_seq", "needs_redo");
        Columns("backfill_state").Should().Equal("conversation_id", "items_done_at", "logs_done_at", "bytes_left");
        Columns("commands_seen").Should().Equal("command_id", "content_sha256", "state", "result_json", "at");
        Columns("remote_audit").Should().Equal(
            "seq", "at", "command_id", "cmd", "conversation_id", "declared_tier", "tier", "full_access", "policy", "paused", "default_password",
            "iat", "created", "step", "result", "code", "detail", "count", "body_masked", "signed_sha256", "sig");
        Columns("media_refs_local").Should().Equal("ref", "conversation_id", "item_id", "attachment_id", "media_path", "created_at");
    }

    // ── outbox 的對話與世代 ──

    [Fact]
    public void 對話事件_conversationId與epoch寫進outbox_讀回與送出都帶著()
    {
        using (var store = Create())
        {
            var written = store.AppendMany([Plain(1), Conv(ConversationEventKinds.Meta, "{}", "meta:" + ConversationId, epoch: 2)], 100);

            written[0].ConversationId.Should().BeNull();
            written[0].Epoch.Should().BeNull();
            written[1].Should().Match<OutboxEntry>(e => e.ConversationId == ConversationId && e.Epoch == 2 && e.Stream == SyncStreams.Conversation(ConversationId));
        }

        using var reopened = Reopen();
        var loaded = reopened.LoadOutbox();
        loaded.Select(e => (e.ConversationId, e.Epoch)).Should().Equal((null, null), (ConversationId, 2L));
        var sent = loaded[1].ToEvent();
        sent.ConversationId.Should().Be(ConversationId);
        sent.Epoch.Should().Be(2);
        loaded[0].ToEvent().ConversationId.Should().BeNull("第 3 階段的事件照舊沒有對話");
    }

    [Fact]
    public void 同一個對話的合併鍵_原地取代_epoch跟著新內容()
    {
        using var store = Create();
        var key = $"item:{ConversationId}:item-1";
        var first = store.AppendMany([Conv(ConversationEventKinds.Item, """{"v":1}""", key)], 100).Single();

        var second = store.AppendMany([Conv(ConversationEventKinds.Item, """{"v":2}""", key, epoch: 3)], 200).Single();

        second.DeviceSeq.Should().Be(first.DeviceSeq);
        second.CreatedAt.Should().Be(100);
        store.NextDeviceSeq.Should().Be(2);
        store.LoadOutbox().Should().ContainSingle().Which.Should().Match<OutboxEntry>(e => e.Epoch == 3 && e.ConversationId == ConversationId);
    }

    [Fact]
    public void 合併鍵與對話不符_丟ArgumentException_整批不寫()
    {
        using var store = Create();

        var act = () => store.AppendMany([Plain(1), Conv(ConversationEventKinds.Meta, "{}", "meta:" + OtherConversation)], 100);

        act.Should().Throw<ArgumentException>().WithMessage("*合併鍵與對話不符*");
        store.LoadOutbox().Should().BeEmpty("前面合格的那筆也不寫");
        store.NextDeviceSeq.Should().Be(1);
        store.GetKv(SyncStoreSchema.NextDeviceSeq).Should().Be("1");
    }

    [Fact]
    public void 合併鍵的待送列屬於另一個對話_或一邊沒有對話_丟ArgumentException_交易回滾()
    {
        using var store = Create();
        var conversationKey = "meta:" + ConversationId;
        var catalogKey = $"item:{OtherConversation}:x";
        store.AppendMany([Conv(ConversationEventKinds.Meta, """{"v":1}""", conversationKey), Plain(2, catalogKey)], 100);
        var before = store.LoadOutbox();

        // 第 3 階段的事件拿了對話的鍵：會蓋掉對話的待送列
        FluentActions.Invoking(() => store.AppendMany([Plain(3), Plain(4, conversationKey)], 200))
            .Should().Throw<ArgumentException>().WithMessage("*屬於另一個對話*");
        // 對話的事件撞上第 3 階段的待送列（鍵的格式合格）
        FluentActions.Invoking(() => store.AppendMany([Conv(ConversationEventKinds.Item, "{}", catalogKey, conversationId: OtherConversation)], 300))
            .Should().Throw<ArgumentException>().WithMessage("*屬於另一個對話*");

        store.LoadOutbox().Should().BeEquivalentTo(before, "兩次都整批回滾");
        store.NextDeviceSeq.Should().Be(3);
        store.AppendMany([Plain(5)], 400).Single().DeviceSeq.Should().Be(3, "deviceSeq 沒有被吃掉");
    }

    [Fact]
    public void 帶對話的外框不合格_丟ArgumentException_什麼都不寫()
    {
        using var store = Create();
        var cases = new (string Why, OutboxAppend Item)[]
        {
            ("沒有 epoch", Conv(ConversationEventKinds.Meta, "{}", null) with { Epoch = null }),
            ("epoch 0", Conv(ConversationEventKinds.Meta, "{}", null, epoch: 0)),
            ("串流不是 c:{id}", Conv(ConversationEventKinds.Meta, "{}", null) with { Stream = SyncStreams.Catalog }),
            ("別的對話的串流", Conv(ConversationEventKinds.Meta, "{}", null) with { Stream = SyncStreams.Conversation(OtherConversation) }),
            ("id 大寫", Conv(ConversationEventKinds.Meta, "{}", null, conversationId: new string('C', 32))),
            ("不是對話種類", Conv(SyncEventKinds.AccountUpdate, "{}", null)),
            ("沒有對話卻帶 epoch", Plain(1) with { Epoch = 1 }),
            ("對話種類卻沒有對話", Conv(ConversationEventKinds.Meta, "{}", null) with { ConversationId = null, Epoch = null }),
        };

        foreach (var (why, item) in cases)
            FluentActions.Invoking(() => store.AppendMany([item], 100)).Should().Throw<ArgumentException>(why);

        store.LoadOutbox().Should().BeEmpty();
        store.NextDeviceSeq.Should().Be(1);
    }
}
