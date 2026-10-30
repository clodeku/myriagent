using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Myriagent.Hub;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Rules;
using Myriagent.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Store;

public sealed class SyncStoreTests : IDisposable
{
    private static readonly ServerInstance Server = new("srv-instance-1", 0);

    private readonly TempDirectory temp = new("syncstore");
    private readonly TestDevice self = TestDevice.Create("OFFICE-PC");

    private string DbPath => temp.Combine("sync", "sync.db");

    public void Dispose() => temp.Dispose();

    private SyncStore Create() => SyncStore.Create(DbPath, self.DeviceId, 1791500000000, Server, new TrustBundle(3, [self.Entry()]));

    private SyncStore Reopen() => SyncStore.Open(DbPath, self.DeviceId);

    private static OutboxAppend Link(string localId, string kind = SyncEventKinds.LinkUpsert, string label = "x") =>
        new(SyncStreams.Catalog, kind, SyncJson.Serialize(new { localAccountId = localId, label }), CatalogSyncRules.LinkCoalesceKey(localId));

    private static OutboxAppend Plain(int n) => new(SyncStreams.Catalog, SyncEventKinds.AccountUpdate, SyncJson.Serialize(new { n }), null);

    /// <summary>直接改檔（store 已關）：連線字串一樣只從 SyncStore.ConnectionString 來</summary>
    private void Raw(string sql)
    {
        using var connection = new SqliteConnection(SyncStore.ConnectionString(DbPath, SyncStoreOpenMode.Open));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void 連線字串_Pooling為False_open是ReadWrite_create是ReadWriteCreate()
    {
        var open = new SqliteConnectionStringBuilder(SyncStore.ConnectionString(@"C:\x\sync.db", SyncStoreOpenMode.Open));
        var create = new SqliteConnectionStringBuilder(SyncStore.ConnectionString(@"C:\x\sync.db", SyncStoreOpenMode.Create));

        open.Pooling.Should().BeFalse();
        create.Pooling.Should().BeFalse();
        open.Mode.Should().Be(SqliteOpenMode.ReadWrite);
        create.Mode.Should().Be(SqliteOpenMode.ReadWriteCreate);
        open.DataSource.Should().Be(@"C:\x\sync.db");
    }

    /// <summary>
    /// 不用連線池就沒有池可清（Microsoft.Data.Sqlite 10.0.12 的池會讓兩條連線共用同一條原生連線，dotnet/efcore#39008）：
    /// 以中繼資料掃描桌面組件（同 MyriagentHub.Tests 的 DataTests），兩個清池方法都不准呼叫，而且確實有設定 Pooling
    /// </summary>
    [Fact]
    public void 組件掃描_桌面不呼叫ClearPool與ClearAllPools_有設定Pooling()
    {
        using var stream = File.OpenRead(typeof(AgentHub).Assembly.Location);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var calls = md.MemberReferences.Select(h => md.GetMemberReference(h))
            .Where(m => m.Parent.Kind == HandleKind.TypeReference)
            .Select(m => (Type: md.GetTypeReference((TypeReferenceHandle)m.Parent), Name: md.GetString(m.Name)))
            .Select(m => md.GetString(m.Type.Namespace) + "." + md.GetString(m.Type.Name) + "::" + m.Name)
            .ToList();

        calls.Should().Contain("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder::set_Pooling", "確認掃到的是桌面組件（SyncStore 設 Pooling=False）");
        calls.Should().NotContain("Microsoft.Data.Sqlite.SqliteConnection::ClearAllPools");
        calls.Should().NotContain("Microsoft.Data.Sqlite.SqliteConnection::ClearPool");
        calls.Where(c => c.StartsWith("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder::.ctor", StringComparison.Ordinal))
            .Should().NotBeEmpty("連線字串由 SyncStore.ConnectionString 組");
    }

    [Fact]
    public void PRAGMA_WAL_FULL_busy_timeout3000()
    {
        using (var store = Create())
        {
            store.Pragma("journal_mode").Should().Be("wal");
            store.Pragma("synchronous").Should().Be("2", "FULL");
            store.Pragma("busy_timeout").Should().Be("3000");
            store.Pragma("user_version").Should().Be("4");
        }

        using var reopened = Reopen();
        reopened.Pragma("journal_mode").Should().Be("wal");
        reopened.Pragma("synchronous").Should().Be("2");
        reopened.Pragma("busy_timeout").Should().Be("3000");
    }

    [Fact]
    public void Create之後的狀態()
    {
        using var store = Create();

        store.NextDeviceSeq.Should().Be(1);
        store.SeenServer.Should().Be(Server);
        store.IsReconciling.Should().BeFalse();
        store.ReconcileThroughSeq.Should().BeNull();
        store.GetKv(SyncStoreSchema.DeviceId).Should().Be(self.DeviceId);
        store.GetKv(SyncStoreSchema.PairedAt).Should().Be("1791500000000");
        store.LoadRegistry().Rev.Should().Be(3);
        store.LoadRegistry().Entries.Should().Equal(self.Entry());
        store.LoadOutbox().Should().BeEmpty();
        store.LoadCursors().Should().BeEmpty();
        store.CountPending().Should().Be(0);
    }

    [Fact]
    public void AppendMany_deviceSeq連續_重開後接續()
    {
        using (var store = Create())
        {
            store.AppendMany([Plain(1), Plain(2), Plain(3)], 100).Select(e => e.DeviceSeq).Should().Equal(1, 2, 3);
            store.AppendMany([Plain(4)], 200).Single().DeviceSeq.Should().Be(4);
            store.NextDeviceSeq.Should().Be(5);
            store.DeleteOutbox([1, 2]);
            store.AppendMany([Plain(5)], 300).Single().DeviceSeq.Should().Be(5, "修剪過的 outbox 不影響序號（next_device_seq 存在 kv）");
        }

        using var reopened = Reopen();
        reopened.NextDeviceSeq.Should().Be(6);
        reopened.AppendMany([Plain(6)], 400).Single().DeviceSeq.Should().Be(6);
        reopened.LoadOutbox().Select(e => e.DeviceSeq).Should().Equal(3, 4, 5, 6);
    }

    [Fact]
    public void AppendMany_中途丟例外_什麼都沒寫()
    {
        using var store = Create();
        store.AppendMany([Plain(1)], 100);
        var calls = 0;
        store.FaultInjection = point =>
        {
            if (point == "AppendMany:item" && ++calls == 2)
                throw new IOException("模擬寫入失敗");
        };

        FluentActions.Invoking(() => store.AppendMany([Plain(2), Plain(3)], 200)).Should().Throw<IOException>();

        store.FaultInjection = null;
        store.NextDeviceSeq.Should().Be(2);
        store.LoadOutbox().Select(e => e.DeviceSeq).Should().Equal(1);
        store.AppendMany([Plain(2)], 300).Single().DeviceSeq.Should().Be(2);
    }

    [Fact]
    public void 同合併鍵的Pending_link_upsert被link_removed取代_kind也換_deviceSeq不變()
    {
        using var store = Create();
        var first = store.AppendMany([Link("acc-1", label: "舊")], 100).Single();

        var second = store.AppendMany([Link("acc-1", SyncEventKinds.LinkRemoved)], 200).Single();

        second.DeviceSeq.Should().Be(first.DeviceSeq);
        var rows = store.LoadOutbox();
        rows.Should().ContainSingle();
        rows[0].Kind.Should().Be(SyncEventKinds.LinkRemoved);
        rows[0].PayloadSha256.Should().Be(second.PayloadSha256).And.NotBe(first.PayloadSha256);
        rows[0].CreatedAt.Should().Be(100, "建立時間不變");
        rows[0].State.Should().Be(OutboxState.Pending);
        store.NextDeviceSeq.Should().Be(2);
    }

    [Fact]
    public void 同合併鍵的Pending_link_removed被link_upsert取代_kind也換()
    {
        using var store = Create();
        store.AppendMany([Link("acc-1", SyncEventKinds.LinkRemoved)], 100);

        store.AppendMany([Link("acc-1", label: "新")], 200);

        var row = store.LoadOutbox().Single();
        row.Kind.Should().Be(SyncEventKinds.LinkUpsert);
        JObject.Parse(row.Payload).Value<string>("label").Should().Be("新");
        row.PayloadSha256.Should().Be(PayloadRules.Sha256Hex(row.Payload));
    }

    [Fact]
    public void 同合併鍵但已Sent_另開新列()
    {
        using var store = Create();
        var first = store.AppendMany([Link("acc-1", label: "舊")], 100).Single();
        OutboxRules.CanCoalesceInto(first).Should().BeTrue();
        store.SaveOutbox([OutboxRules.MarkSent(first)]);

        var second = store.AppendMany([Link("acc-1", label: "新")], 200).Single();

        second.DeviceSeq.Should().Be(2);
        var rows = store.LoadOutbox();
        rows.Select(r => r.State).Should().Equal(OutboxState.Sent, OutboxState.Pending);
        JObject.Parse(rows[0].Payload).Value<string>("label").Should().Be("舊", "送出過的列內容凍結");
        store.AppendMany([Link("acc-1", label: "更新")], 300).Single().DeviceSeq.Should().Be(2, "新開的那列還是 Pending，可以合併");
    }

    [Fact]
    public void 同一批裡同合併鍵_後面的取代前面的()
    {
        using var store = Create();

        var written = store.AppendMany([Link("acc-1", label: "一"), Plain(9), Link("acc-1", label: "二")], 100);

        written.Select(e => e.DeviceSeq).Should().Equal(1, 2, 1);
        store.LoadOutbox().Should().HaveCount(2);
        JObject.Parse(store.LoadOutbox()[0].Payload).Value<string>("label").Should().Be("二");
    }

    [Fact]
    public void Dispose後重開_未確認的列都在_內容逐位元組相同()
    {
        IReadOnlyList<OutboxEntry> before;
        var big = new string('長', 9000);
        using (var store = Create())
        {
            store.AppendMany([Link("acc-1"), Plain(2), new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.CliState, SyncJson.Serialize(new { big }), CatalogSyncRules.CliCoalesceKey)], 100);
            var rows = store.LoadOutbox();
            store.SaveOutbox([rows[0] with { State = OutboxState.Acked, ServerSeq = 77, AckedAt = 150 }, OutboxRules.MarkSent(rows[1]),
                rows[2] with { State = OutboxState.Rejected, RejectReason = "conflict", AckedAt = 160 }]);
            before = store.LoadOutbox();
        }

        File.Exists(DbPath + "-wal").Should().BeFalse("最後一條連線關閉時做檢查點並刪掉 -wal");
        using var reopened = Reopen();
        reopened.LoadOutbox().Should().Equal(before);
        before[2].Enc.Should().Be(PayloadRules.EncGzip, "8 KB 以上的 payload 以 gzip 存");
        PayloadRules.TryDecode(before[2].Enc, before[2].Payload, 1 << 20, out var json).Should().BeTrue();
        PayloadRules.Sha256Hex(json).Should().Be(before[2].PayloadSha256);
        reopened.LoadOutbox()[0].ServerSeq.Should().Be(77);
        reopened.LoadOutbox()[2].RejectReason.Should().Be("conflict");
    }

    [Fact]
    public void CommitPull_套用目錄與游標()
    {
        using var store = Create();
        var page = new[]
        {
            PullReduceRulesTests.Event(5, SyncEventKinds.AccountUpsert, PullReduceRulesTests.Upsert("la_A", "甲")),
            PullReduceRulesTests.Event(6, SyncEventKinds.DeviceUpsert, new DeviceUpsertPayload(self.DeviceId, "OFFICE-PC", null, null, "software", "ABCD-EFGH", 1, null, true, null, null)),
            PullReduceRulesTests.Event(7, SyncEventKinds.DeviceRevoked, new DeviceRevokedPayload("d_OTHER", 9, "retired")),
            PullReduceRulesTests.Event(8, SyncEventKinds.PrefsSet, new PrefsSetPayload("theme", "light", 4)),
        };

        store.CommitPull(PullReduceRules.Reduce(page, PullReduceRules.MinServerPayloadBytes), PullReduceRules.AdvanceCursors(store.LoadCursors(), page));

        var catalog = store.LoadCatalog();
        catalog.Accounts["la_A"].Label.Should().Be("甲");
        catalog.Devices[self.DeviceId].Name.Should().Be("OFFICE-PC");
        catalog.Devices.Should().NotContainKey("d_OTHER", "只有撤銷、沒有 device.upsert");
        catalog.Revoked["d_OTHER"].Reason.Should().Be("retired");
        ((string?)catalog.Prefs["theme"].Value).Should().Be("light");
        store.LoadCursors()["catalog"].Should().Be(8);

        var later = new[]
        {
            PullReduceRulesTests.Event(9, SyncEventKinds.AccountDeleted, new AccountDeletedPayload("la_A", 2)),
            PullReduceRulesTests.Event(10, SyncEventKinds.DeviceUpsert, new DeviceUpsertPayload("d_OTHER", "OLD", null, null, "tpm", "WXYZ-WXYZ", 1, null, false, null, null)),
        };
        store.CommitPull(PullReduceRules.Reduce(later, PullReduceRules.MinServerPayloadBytes), PullReduceRules.AdvanceCursors(store.LoadCursors(), later));

        catalog = store.LoadCatalog();
        catalog.Accounts.Should().BeEmpty();
        catalog.Devices["d_OTHER"].Name.Should().Be("OLD");
        catalog.Revoked.Should().ContainKey("d_OTHER", "後到的 device.upsert 不清掉撤銷");
    }

    [Fact]
    public void CommitPull_中途丟例外_快取與游標都沒變()
    {
        using var store = Create();
        var first = new[] { PullReduceRulesTests.Event(5, SyncEventKinds.AccountUpsert, PullReduceRulesTests.Upsert("la_A", "甲")) };
        store.CommitPull(PullReduceRules.Reduce(first, PullReduceRules.MinServerPayloadBytes), PullReduceRules.AdvanceCursors(store.LoadCursors(), first));
        var page = new[]
        {
            PullReduceRulesTests.Event(9, SyncEventKinds.AccountUpsert, PullReduceRulesTests.Upsert("la_A", "乙")),
            PullReduceRulesTests.Event(10, SyncEventKinds.PrefsSet, new PrefsSetPayload("theme", "light", 4)),
        };

        foreach (var point in new[] { "CommitPull:accounts", "CommitPull:catalog", "CommitPull:cursors" })
        {
            store.FaultInjection = p =>
            {
                if (p == point)
                    throw new IOException(point);
            };
            FluentActions.Invoking(() => store.CommitPull(PullReduceRules.Reduce(page, PullReduceRules.MinServerPayloadBytes), PullReduceRules.AdvanceCursors(store.LoadCursors(), page)))
                .Should().Throw<IOException>();
            store.FaultInjection = null;

            store.LoadCatalog().Accounts["la_A"].Label.Should().Be("甲", point);
            store.LoadCatalog().Prefs.Should().BeEmpty(point);
            store.LoadCursors()["catalog"].Should().Be(5, point);
        }
    }

    [Fact]
    public void BeginServerChange_游標歸零_清快取與published_seen_server不變_reconcile為1()
    {
        using var store = Create();
        var page = new[] { PullReduceRulesTests.Event(5, SyncEventKinds.AccountUpsert, PullReduceRulesTests.Upsert("la_A", "甲")) };
        store.CommitPull(PullReduceRules.Reduce(page, PullReduceRules.MinServerPayloadBytes), new Dictionary<string, long> { ["catalog"] = 5, ["usage"] = 9 });
        store.SetPublished("acc-1", new PublishedLink("digest", "codex:me@example.com"));
        store.SetDetached("acc-2", "la_B", 1);
        store.AppendMany([Plain(1), Plain(2)], 100);
        var restored = new ServerInstance("srv-instance-1", 1);

        store.BeginServerChange(restored);

        store.LoadCursors().Should().BeEmpty("游標歸 0");
        store.LoadCatalog().Accounts.Should().BeEmpty();
        store.LoadPublished().Should().BeEmpty("所有 link.upsert 以新的 deviceSeq 重送");
        store.LoadDetached().Should().ContainKey("acc-2");
        store.SeenServer.Should().Be(Server, "對帳完成才改");
        store.IsReconciling.Should().BeTrue();
        store.ReconcileThroughSeq.Should().Be(2);
        store.LoadOutbox().Should().HaveCount(2, "對帳期間不修剪、也不清 outbox");

        using var reopened = Reopen_After(store);
        reopened.SeenServer.Should().Be(Server, "中途當掉：下次 Hello 仍帶舊的 seen_server");
        reopened.IsReconciling.Should().BeTrue();
        reopened.ReconcileThroughSeq.Should().Be(2);
    }

    [Fact]
    public void CompleteServerChange_才寫seen_server()
    {
        var restored = new ServerInstance("srv-instance-1", 1);
        using (var store = Create())
        {
            store.BeginServerChange(restored);
            store.SeenServer.Should().Be(Server);

            store.CompleteServerChange();

            store.SeenServer.Should().Be(restored);
            store.IsReconciling.Should().BeFalse();
            store.ReconcileThroughSeq.Should().BeNull();
            FluentActions.Invoking(store.CompleteServerChange).Should().Throw<InvalidOperationException>("沒有進行中的變更");
        }

        using var reopened = Reopen();
        reopened.SeenServer.Should().Be(restored);
        reopened.IsReconciling.Should().BeFalse();
    }

    [Fact]
    public void 檔案不見_Missing_不建新檔()
    {
        var act = () => SyncStore.Open(DbPath, self.DeviceId);

        act.Should().Throw<SyncStoreException>().Which.Failure.Should().Be(SyncStoreFailure.Missing);
        File.Exists(DbPath).Should().BeFalse("不會建出空檔");
    }

    [Fact]
    public void Create_檔案已存在_丟例外()
    {
        Create().Dispose();

        FluentActions.Invoking(Create).Should().Throw<IOException>();
        using var reopened = Reopen();
        reopened.GetKv(SyncStoreSchema.DeviceId).Should().Be(self.DeviceId, "既有的檔不受影響");
    }

    [Fact]
    public void 未來版本_schema_min_reader較大_TooNew()
    {
        Create().Dispose();
        Raw("UPDATE kv SET value = '5' WHERE key = 'schema_min_reader'; PRAGMA user_version = 5;");

        FluentActions.Invoking(Reopen).Should().Throw<SyncStoreException>().Which.Failure.Should().Be(SyncStoreFailure.TooNew);
    }

    [Fact]
    public void 加了欄位的新版但min_reader為1_本版可開()
    {
        using (var store = Create())
            store.AppendMany([Plain(1)], 100);
        Raw("ALTER TABLE outbox ADD COLUMN future_column TEXT; CREATE TABLE future_table (x INTEGER); PRAGMA user_version = 5;");

        using var reopened = Reopen();

        reopened.LoadOutbox().Should().ContainSingle();
        reopened.AppendMany([Plain(2), Link("acc-1")], 200).Select(e => e.DeviceSeq).Should().Equal(2, 3);
        reopened.SaveOutbox([OutboxRules.MarkSent(reopened.LoadOutbox()[0])]);
        reopened.CountPending().Should().Be(3);
    }

    [Fact]
    public void 檔案損毀_Corrupt()
    {
        using (var store = Create())
            store.AppendMany(Enumerable.Range(0, 200).Select(Plain).ToList(), 100);
        var bytes = File.ReadAllBytes(DbPath);
        bytes.Length.Should().BeGreaterThan(3 * 4096);
        for (var i = 4096; i < 3 * 4096; i++)
            bytes[i] = 0xA5;
        File.WriteAllBytes(DbPath, bytes);

        FluentActions.Invoking(Reopen).Should().Throw<SyncStoreException>().Which.Failure.Should().Be(SyncStoreFailure.Corrupt);

        var header = File.ReadAllBytes(DbPath);
        for (var i = 0; i < 100; i++)
            header[i] = 0x5A;
        File.WriteAllBytes(DbPath, header);
        FluentActions.Invoking(Reopen).Should().Throw<SyncStoreException>().Which.Failure.Should().Be(SyncStoreFailure.Corrupt, "不是 SQLite 檔");
    }

    [Fact]
    public void 空的SQLite檔_Corrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        File.WriteAllBytes(DbPath, []);

        FluentActions.Invoking(Reopen).Should().Throw<SyncStoreException>().Which.Failure.Should().Be(SyncStoreFailure.Corrupt);
    }

    [Fact]
    public void device_id不符_DeviceMismatch()
    {
        Create().Dispose();

        var act = () => SyncStore.Open(DbPath, TestDevice.Create("其他").DeviceId);

        act.Should().Throw<SyncStoreException>().Which.Failure.Should().Be(SyncStoreFailure.DeviceMismatch);
    }

    [Fact]
    public void CountPending_含Pending_Retry_Sent()
    {
        using var store = Create();
        var rows = store.AppendMany([Plain(1), Plain(2), Plain(3), Plain(4), Plain(5)], 100);

        store.SaveOutbox([
            rows[1] with { State = OutboxState.Retry },
            rows[2] with { State = OutboxState.Sent },
            rows[3] with { State = OutboxState.Acked, ServerSeq = 4, AckedAt = 200 },
            rows[4] with { State = OutboxState.Rejected, RejectReason = "conflict", AckedAt = 200 },
        ]);

        store.CountPending().Should().Be(3);
        store.LoadOutbox().Select(e => e.State).Should().Equal(OutboxState.Pending, OutboxState.Retry, OutboxState.Sent, OutboxState.Acked, OutboxState.Rejected);
        FluentActions.Invoking(() => store.SaveOutbox([rows[0] with { DeviceSeq = 999 }])).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void 名冊_published_detached_kv()
    {
        var other = TestDevice.Create("HOME-PC");
        using (var store = Create())
        {
            store.SaveRegistry(1, [self.Entry(), other.Entry()]);
            store.SetPublished("acc-1", new PublishedLink("d1", null));
            store.SetPublished("acc-2", new PublishedLink("d2", "codex:me@example.com"));
            store.SetPublished("acc-1", new PublishedLink("d1b", "claude:x"));
            store.SetPublished("acc-2", null);
            store.SetDetached("acc-3", "la_C", 5);
            store.SetDetached("acc-4", "la_D", 6);
            store.ClearDetached("la_D");
            store.SetKv(SyncStoreSchema.LastSyncAt, "123");
        }

        using var reopened = Reopen();
        var (rev, entries) = reopened.LoadRegistry();
        rev.Should().Be(1, "版本較舊也整份取代");
        entries.Should().BeEquivalentTo([self.Entry(), other.Entry()]);
        reopened.LoadPublished().Should().Equal(new Dictionary<string, PublishedLink> { ["acc-1"] = new("d1b", "claude:x") });
        reopened.LoadDetached().Should().Equal(new Dictionary<string, string> { ["acc-3"] = "la_C" });
        reopened.GetKv(SyncStoreSchema.LastSyncAt).Should().Be("123");
        reopened.GetKv("missing").Should().BeNull();
    }

    [Fact]
    public void Dispose之後呼叫丟例外()
    {
        var store = Create();
        store.Dispose();
        store.Dispose();

        FluentActions.Invoking(() => store.AppendMany([Plain(1)], 1)).Should().Throw<ObjectDisposedException>();
    }

    private SyncStore Reopen_After(SyncStore store)
    {
        store.Dispose();
        return Reopen();
    }
}
