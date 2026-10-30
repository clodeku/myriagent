using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Rules;
using Myriagent.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>量 GC 記憶體的測試循序執行：xUnit 先跑完所有可平行的集合才跑這種集合，量到的增量不會混進別的測試的配置</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OutboxMemoryCollection
{
    public const string Name = "outbox 骨架的記憶體量測（循序執行）";
}

/// <summary>
/// outbox 瘦身（WP4.3 計畫 D31、步 9）：已完成的對話列在記憶體只留骨架、要重送時從 sync.db 讀回（送出的位元組與原本相同）；
/// 第 3 階段的列不變；寫入失敗不換骨架；確認只處理在途的尾段（工作量不跟著保留的列數長）；修剪之後照常確認；
/// 骨架每 10 萬筆的記憶體增量；保留中的對話列數與位元組、磁碟剩餘空間（D24 磁碟保護的輸入）
/// </summary>
[Collection(OutboxMemoryCollection.Name)]
public sealed class OutboxSlimTests(ITestOutputHelper output)
{
    private const long PairedAt = 1791500000000;

    private const string ConvA = "0123456789abcdef0123456789abcdef";

    private const string ConvB = "fedcba9876543210fedcba9876543210";

    // ── 引擎 ──

    [Fact]
    public async Task 第3階段的列不變成骨架_已完成的對話列換成骨架_共用字串()
    {
        await using var h = new EngineHarness();
        h.Server.Materialize = e => e.ConversationId == ConvA && e.Payload.Contains("\"i2\"", StringComparison.Ordinal) ? ConversationRejectCodes.Fenced : null;
        await h.StartAsync();
        h.Append(2);
        AppendItems(h, ConvA, 1, 3);
        AppendItems(h, ConvB, 1, 2);
        await h.AdvanceAsync(1);
        h.Server.Batches.SelectMany(b => b.Events).Should().HaveCount(7);
        var sent = h.Server.Batches.SelectMany(b => b.Events).ToDictionary(e => e.DeviceSeq);

        var outbox = await h.Inspect(v => v.Outbox);
        CheckShape(outbox, sent);

        // 重開：從 sync.db 載入時直接是骨架（LoadOutboxSlim），第 3 階段的列逐欄相同
        var catalogBefore = outbox.Where(e => e.ConversationId is null).ToList();
        await h.RestartAsync();
        var reloaded = await h.Inspect(v => v.Outbox);
        CheckShape(reloaded, sent);
        reloaded.Where(e => e.ConversationId is null).Should().Equal(catalogBefore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 還原後resync_骨架讀回payload_送出的位元組與原本相同(bool restart)
    {
        await using var h = new EngineHarness();
        h.Server.Limits = SyncLimits.Default with { MaxBatchEvents = 2 };
        await h.StartAsync();
        AppendItems(h, ConvA, 1, 4, padding: 3000);
        h.Append(1);
        AppendItems(h, ConvB, 1, 2);
        await h.AdvanceAsync(2);
        var original = h.Server.Accepted;
        original.Should().HaveCount(7);
        if (restart)
            await h.RestartAsync();
        (await h.Inspect(v => v.Outbox)).Where(e => e.ConversationId is not null).Should().OnlyContain(e => OutboxSliceRules.IsSkeleton(e));

        h.Server.Restore(nextDeviceSeqAtBackup: 1, highAtBackup: 0);
        var before = h.Server.Batches.Count;
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(3);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        var resent = h.Server.Batches.Skip(before).SelectMany(b => b.Events).ToList();
        resent.Select(e => e.DeviceSeq).Should().Equal(1, 2, 3, 4, 5, 6, 7);
        resent.Should().Equal(original, "骨架從 sync.db 讀回：種類、串流、編碼、payload 與雜湊都和原本送的逐位元組相同");
        h.Server.Batches.Skip(before).Should().OnlyContain(b => b.Events.Count <= 2, "每批最多讀回一批的筆數");
        h.Server.Accepted.Should().Equal(original);
        h.Server.DoubleApplied.Should().Be(0);
        (await h.Inspect(v => v.Outbox)).Where(e => e.ConversationId is not null).Should().OnlyContain(e => OutboxSliceRules.IsSkeleton(e) && e.State == OutboxState.Acked,
            "重送確認後又換回骨架");
    }

    [Fact]
    public async Task 骨架讀不回_斷線重連後再試_沒有送出空的內容()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        AppendItems(h, ConvA, 1, 3);
        await h.AdvanceAsync(1);
        var original = h.Server.Accepted;
        h.Server.Restore(nextDeviceSeqAtBackup: 1, highAtBackup: 0);
        await h.Inspect(v =>
        {
            v.Store!.FaultInjection = p =>
            {
                if (p == "LoadOutboxPayloads")
                    throw new SqliteException("模擬：資料庫被鎖", 5);
            };
            return 0;
        });
        var before = h.Server.Batches.Count;
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1.5);

        h.Server.Batches.Count.Should().Be(before, "讀不回內容就不送（R19：照本機失敗斷線）");
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);

        await h.Inspect(v =>
        {
            v.Store!.FaultInjection = null;
            return 0;
        });
        await h.AdvanceAsync(5);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Server.Batches.Skip(before).SelectMany(b => b.Events).Should().Equal(original);
        h.Server.Batches.SelectMany(b => b.Events).Should().OnlyContain(e => e.Payload.Length > 0 && e.PayloadSha256.Length == 64);
    }

    [Fact]
    public async Task 寫入失敗不換骨架()
    {
        await using var h = new EngineHarness();
        await h.StartAsync();
        h.Server.HoldAcks = true;
        AppendItems(h, ConvA, 1, 3);
        await h.AdvanceAsync(1);
        h.Server.Current!.HeldAcks.Should().Be(1);
        await h.Inspect(v =>
        {
            v.Store!.FaultInjection = p =>
            {
                if (p == "SaveOutbox:item")
                    throw new SqliteException("模擬：資料庫被鎖", 5);
            };
            return 0;
        });

        h.Server.Current.ReleaseAcks();
        await h.IdleAsync();

        var outbox = await h.Inspect(v => v.Outbox);
        outbox.Should().HaveCount(3).And.OnlyContain(e => e.State == OutboxState.Sent && !OutboxSliceRules.IsSkeleton(e) && e.Payload.Length > 0,
            "確認寫不進 sync.db：記憶體照舊（不標成已提交、不換骨架）");
        RawStates(h).Should().Equal(["sent", "sent", "sent"]);
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);

        // 恢復之後重連：Prove 說伺服器都有了，標成已提交、換成骨架
        await h.Inspect(v =>
        {
            v.Store!.FaultInjection = null;
            return 0;
        });
        h.Server.HoldAcks = false;
        await h.AdvanceAsync(5);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        (await h.Inspect(v => v.Outbox)).Should().OnlyContain(e => e.State == OutboxState.Acked && OutboxSliceRules.IsSkeleton(e));
        RawStates(h).Should().Equal(["acked", "acked", "acked"]);
        h.Server.Accepted.Should().HaveCount(3);
    }

    [Fact]
    public async Task 確認的工作量不跟著保留的列數長()
    {
        const int retained = 20_000;
        await using var h = new EngineHarness();
        InsertAckedRows(h.Paths.DatabaseFile, 1, retained, conversations: 50, padding: 20, now: h.Time.NowMs);
        h.Server.NextDeviceSeq = retained + 1;
        h.Server.Limits = SyncLimits.Default with { MaxBatchEvents = 2 };
        await h.StartAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        (await h.Inspect(v => v.Outbox.Count)).Should().Be(retained);
        var before = await h.Inspect(v => v.OutboxWork);

        AppendItems(h, ConvA, 1, 10);
        await h.AdvanceAsync(2);

        h.Server.Accepted.Select(e => e.DeviceSeq).Should().Equal(Enumerable.Range(retained + 1, 10).Select(i => (long)i));
        var work = await h.Inspect(v => v.OutboxWork) - before;
        output.WriteLine($"保留 {retained:N0} 列、送出並確認 10 筆（5 批）：走過 {work} 列");
        work.Should().BeLessThan(500, "確認只處理在途的尾段、第一個未完成的列從上次的位置起找");
        (await h.Inspect(v => v.Outbox)).Should().HaveCount(retained + 10).And.OnlyContain(e => e.State == OutboxState.Acked && OutboxSliceRules.IsSkeleton(e));
        h.Host.LastStatus!.Pending.Should().Be(0);
    }

    [Fact]
    public async Task 修剪之後_在途的列照樣確認_待送的照送()
    {
        await using var h = new EngineHarness(heartbeat: TimeSpan.FromHours(1));
        await h.StartAsync();
        AppendItems(h, ConvA, 1, 3);
        await h.AdvanceAsync(1);
        await h.AdvanceAsync(TimeSpan.FromHours(25));
        // 兩批各一列、確認都扣著；第一批的確認讓最前面 3 列（已提交滿 24 小時）被修剪，第二批之後才確認
        h.Server.HoldAcks = true;
        AppendItems(h, ConvA, 4, 1);
        await h.AdvanceAsync(0.2);
        AppendItems(h, ConvA, 5, 1);
        await h.AdvanceAsync(0.2);
        h.Server.Current!.HeldAcks.Should().Be(2);

        h.Server.Current.ReleaseOneAck();
        await h.AdvanceAsync(1);
        (await h.Inspect(v => v.Outbox)).Select(e => e.DeviceSeq).Should().Equal(4, 5);
        h.Host.LastStatus!.Pending.Should().Be(1, "第 5 列還在途");

        h.Server.Current.ReleaseOneAck();
        await h.IdleAsync();
        h.Server.HoldAcks = false;
        AppendItems(h, ConvA, 6, 1);
        await h.AdvanceAsync(1);

        (await h.Inspect(v => v.Outbox)).Should().OnlyContain(e => e.State == OutboxState.Acked);
        (await h.Inspect(v => v.Outbox)).Select(e => e.DeviceSeq).Should().Equal(4, 5, 6);
        h.Server.Accepted.Select(e => e.DeviceSeq).Should().Equal(1, 2, 3, 4, 5, 6);
        h.Host.LastStatus!.Pending.Should().Be(0);
    }

    // ── sync.db ──

    [Fact]
    public void LoadOutboxSlim_已完成的對話列直接是骨架_其他列照原樣_讀回完整內容()
    {
        using var temp = new TempDirectory("outbox-slim");
        using var store = CreateStore(temp);
        var written = store.AppendMany(
        [
            new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.CliState, """{"providers":{}}""", null),
            ItemAppend(ConvA, 1),
            ItemAppend(ConvA, 2),
            ItemAppend(ConvA, 3),
            new OutboxAppend(SyncStreams.Conversation(ConvB), ConversationEventKinds.Meta, """{"title":"二"}""", $"meta:{ConvB}", ConvB, 1),
            ItemAppend(ConvA, 4),
        ], PairedAt);
        store.SaveOutbox(
        [
            written[0] with { State = OutboxState.Acked, ServerSeq = 10, AckedAt = PairedAt },
            written[1] with { State = OutboxState.Acked, ServerSeq = 11, AckedAt = PairedAt },
            written[2] with { State = OutboxState.Rejected, RejectReason = ConversationRejectCodes.Fenced, AckedAt = PairedAt },
            written[3] with { State = OutboxState.Sent },
            written[4] with { State = OutboxState.Retry },
        ]);
        var full = store.LoadOutbox();
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);

        var slim = store.LoadOutboxSlim(s => Share(pool, s));

        slim.Should().HaveCount(full.Count);
        for (var i = 0; i < full.Count; i++)
            slim[i].Should().Be(OutboxSliceRules.CanSkeleton(full[i]) ? OutboxSliceRules.Skeleton(full[i]) : full[i], $"第 {i + 1} 列");
        slim[0].Payload.Should().NotBeEmpty("第 3 階段的列照原樣");
        slim[1].CoalesceKey.Should().BeNull();
        slim[2].CoalesceKey.Should().Be($"item:{ConvA}:i2", "被拒的留合併鍵");
        slim[3].Payload.Should().NotBeEmpty("在途的列照原樣");
        slim[4].Payload.Should().NotBeEmpty("要重送的列照原樣");
        slim[2].ConversationId.Should().BeSameAs(slim[1].ConversationId);
        slim[2].Stream.Should().BeSameAs(slim[1].Stream);

        store.LoadOutboxPayloads([written[2].DeviceSeq, written[1].DeviceSeq]).Should().Equal([full[2], full[1]], "依要求的順序");
        store.LoadOutboxPayloads([]).Should().BeEmpty();
        FluentActions.Invoking(() => store.LoadOutboxPayloads([written[1].DeviceSeq, 999])).Should().Throw<InvalidOperationException>();
        store.LoadOutbox().Should().Equal(full, "LoadOutbox 不變");
    }

    [Fact]
    public void 保留的對話列數與位元組_隨寫入與刪除增減_和重新掃的相同()
    {
        using var temp = new TempDirectory("outbox-stats");
        var path = temp.Combine("sync", "sync.db");
        using (var store = CreateStore(temp))
        {
            store.RetainedConversationStats().Should().Be((0, 0L));
            var big = new string('長', 6000);
            var written = store.AppendMany(
            [
                ItemAppend(ConvA, 1),
                new OutboxAppend(SyncStreams.Conversation(ConvA), ConversationEventKinds.Item, $$"""{"id":"big","text":"{{big}}"}""", $"item:{ConvA}:big", ConvA, 1),
                new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.CliState, """{"providers":{"x":1}}""", null),
                new OutboxAppend(SyncStreams.Conversation(ConvB), ConversationEventKinds.Meta, """{"title":"短"}""", $"meta:{ConvB}", ConvB, 1),
            ], PairedAt);
            written[1].Enc.Should().Be(PayloadRules.EncGzip, "大的 payload 壓縮成 base64（位元組照存的算）");
            store.RetainedConversationStats().Should().Be(Fresh(path));
            store.RetainedConversationStats().Rows.Should().Be(3, "第 3 階段的列不算");
            store.RetainedConversationStats().Bytes.Should().Be(new[] { written[0], written[1], written[3] }.Sum(e => (long)Encoding.UTF8.GetByteCount(e.Payload)));

            // 合併：同一個對話的待送列原地取代，列數不變、位元組換成新的
            store.AppendMany([new OutboxAppend(SyncStreams.Conversation(ConvB), ConversationEventKinds.Meta, """{"title":"長一點的標題：中文字"}""", $"meta:{ConvB}", ConvB, 1)], PairedAt);
            store.RetainedConversationStats().Should().Be(Fresh(path));
            store.RetainedConversationStats().Rows.Should().Be(3);

            // 寫入失敗：不增減
            store.FaultInjection = p =>
            {
                if (p == "AppendMany:commit")
                    throw new SqliteException("模擬：磁碟滿", 13);
            };
            FluentActions.Invoking(() => store.AppendMany([ItemAppend(ConvA, 9)], PairedAt)).Should().Throw<SqliteException>();
            store.FaultInjection = null;
            store.RetainedConversationStats().Should().Be(Fresh(path));

            // 修剪：對話列與第 3 階段的列都刪
            store.DeleteOutbox([written[0].DeviceSeq, written[2].DeviceSeq, 999]);
            store.RetainedConversationStats().Should().Be(Fresh(path));
            store.RetainedConversationStats().Rows.Should().Be(2);
        }

        // 重開：第一次問時重新掃
        using var reopened = SyncStore.Open(path, Self.DeviceId);
        reopened.RetainedConversationStats().Should().Be(Fresh(path));
    }

    [Fact]
    public void FreeDiskBytes讀不到_回最大值_只警告一次()
    {
        using var temp = new TempDirectory("outbox-disk");
        using var store = CreateStore(temp);
        store.FreeDiskBytes().Should().BeInRange(1, long.MaxValue - 1, "暫存資料夾所在的磁碟查得到");
        store.FreeDiskWarnings.Should().Be(0);

        var probed = new List<string>();
        Exception[] failures = [new IOException("模擬：磁碟不見"), new UnauthorizedAccessException("模擬：權限"), new ArgumentException("模擬：不是磁碟根目錄")];
        store.FreeDiskProbeForTest = path =>
        {
            probed.Add(path);
            throw failures[probed.Count - 1];
        };

        store.FreeDiskBytes().Should().Be(long.MaxValue, "查不到就當成空間足夠（不因為查不到就停回補）");
        store.FreeDiskBytes().Should().Be(long.MaxValue);
        store.FreeDiskBytes().Should().Be(long.MaxValue);

        store.FreeDiskWarnings.Should().Be(1, "只警告第一次");
        probed.Should().Equal([Path.GetFullPath(store.Path), Path.GetFullPath(store.Path), Path.GetFullPath(store.Path)], "每次都重新查（之後可能查得到）");
    }

    [Fact]
    public void 測試接縫有值時取代實際值()
    {
        using var temp = new TempDirectory("outbox-seams");
        using var store = CreateStore(temp);
        store.AppendMany([ItemAppend(ConvA, 1)], PairedAt);
        store.FreeDiskProbeForTest = _ => throw new IOException("模擬");

        store.RetainedStatsForTest = (1_000_001, 5L << 30);
        store.FreeDiskBytesForTest = 1L << 30;

        store.RetainedConversationStats().Should().Be((1_000_001, 5L << 30));
        store.FreeDiskBytes().Should().Be(1L << 30);
        store.FreeDiskWarnings.Should().Be(0, "有接縫的值就不查");

        store.RetainedStatsForTest = null;
        store.FreeDiskBytesForTest = null;
        store.FreeDiskProbeForTest = null;
        store.RetainedConversationStats().Rows.Should().Be(1, "拿掉接縫就是實際的值");
        store.FreeDiskBytes().Should().BeLessThan(long.MaxValue);
    }

    // ── 記憶體 ──

    [Fact]
    public void 骨架每10萬筆記憶體增量不超過16MB()
    {
        var (segments, ackPathTotal) = SkeletonMemory.Measure(200_000, output);

        segments.Should().HaveCount(2);
        segments.Should().OnlyContain(b => b <= SkeletonMemory.BudgetPer100k, "每 10 萬筆已完成的對話列的骨架 ≤16 MB（D31）");
        ackPathTotal.Should().BeLessThanOrEqualTo(2 * SkeletonMemory.BudgetPer100k, "確認時換成的骨架放進照常成長的清單，合計也在預算內");
    }

    // ── 共用 ──

    private static readonly TestDevice Self = TestDevice.Create("OFFICE-PC");

    private static SyncStore CreateStore(TempDirectory temp)
    {
        var path = temp.Combine("sync", "sync.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return SyncStore.Create(path, Self.DeviceId, PairedAt, new ServerInstance("srv-instance-1", 0), new TrustBundle(1, [Self.Entry()]));
    }

    private static void CheckShape(IReadOnlyList<OutboxEntry> outbox, IReadOnlyDictionary<long, SyncEvent> sent)
    {
        outbox.Should().HaveCount(7);
        foreach (var entry in outbox.Where(e => e.ConversationId is null))
        {
            OutboxSliceRules.IsSkeleton(entry).Should().BeFalse();
            entry.Payload.Should().Be(sent[entry.DeviceSeq].Payload, "第 3 階段的列不變成骨架");
            entry.PayloadSha256.Should().Be(sent[entry.DeviceSeq].PayloadSha256);
        }

        var conversation = outbox.Where(e => e.ConversationId is not null).ToList();
        conversation.Should().HaveCount(5).And.OnlyContain(e => OutboxSliceRules.IsSkeleton(e) && e.IsDone);
        var rejected = conversation.Single(e => e.State == OutboxState.Rejected);
        rejected.CoalesceKey.Should().Be($"item:{ConvA}:i2", "被拒的留合併鍵");
        rejected.RejectReason.Should().BeSameAs(ConversationRejectCodes.Fenced);
        conversation.Where(e => e.State == OutboxState.Acked).Should().OnlyContain(e => e.CoalesceKey == null && e.ServerSeq != null);
        var a = conversation.Where(e => e.ConversationId == ConvA).ToList();
        a.Should().HaveCount(3);
        a.Should().OnlyContain(e => ReferenceEquals(e.ConversationId, a[0].ConversationId) && ReferenceEquals(e.Stream, a[0].Stream), "同一個對話的列共用一份字串");
        conversation.Should().OnlyContain(e => ReferenceEquals(e.Kind, ConversationEventKinds.Item));
    }

    private static void AppendItems(EngineHarness h, string conversationId, int from, int count, int padding = 40)
    {
        for (var n = from; n < from + count; n++)
            h.Engine.Post(new SyncSignal.Append(ItemAppend(conversationId, n, padding)));
    }

    /// <summary>conv.item（項目 id 各不相同、不合併）；串流與對話 id 每次都是新的字串（看得出引擎有沒有共用）</summary>
    private static OutboxAppend ItemAppend(string conversationId, int n, int padding = 40) =>
        new(SyncStreams.Conversation(conversationId), ConversationEventKinds.Item, $$"""{"id":"i{{n}}","ord":{{n}},"text":"{{new string('字', padding)}}"}""",
            $"item:{conversationId}:i{n}", new string(conversationId.ToCharArray()), 1);

    internal static string Share(Dictionary<string, string> pool, string value)
    {
        if (pool.TryGetValue(value, out var shared))
            return shared;
        pool[value] = value;
        return value;
    }

    private static (int Rows, long Bytes) Fresh(string path)
    {
        using var connection = new SqliteConnection(SyncStore.ConnectionString(path, SyncStoreOpenMode.Open));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(length(CAST(payload AS BLOB))), 0) FROM outbox WHERE conversation_id IS NOT NULL";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return ((int)reader.GetInt64(0), reader.GetInt64(1));
    }

    private static List<string> RawStates(EngineHarness h)
    {
        using var connection = new SqliteConnection(SyncStore.ConnectionString(h.Paths.DatabaseFile, SyncStoreOpenMode.Open));
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT state FROM outbox ORDER BY device_seq";
        using var reader = cmd.ExecuteReader();
        var states = new List<string>();
        while (reader.Read())
            states.Add(reader.GetString(0));
        return states;
    }

    /// <summary>
    /// 直接以 SQL 寫進已提交的對話列（deviceSeq 從 <paramref name="firstSeq"/> 起連續 <paramref name="count"/> 筆，平均分到 <paramref name="conversations"/> 個對話）
    /// 並把 next_device_seq 接到最後；一個交易
    /// </summary>
    internal static void InsertAckedRows(string dbPath, long firstSeq, int count, int conversations, int padding, long now)
    {
        using var connection = new SqliteConnection(SyncStore.ConnectionString(dbPath, SyncStoreOpenMode.Open));
        connection.Open();
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO outbox (device_seq, stream, kind, enc, payload, payload_sha256, coalesce_key, state, created_at, server_seq, acked_at, conversation_id, epoch)
                VALUES ($seq, $stream, $kind, $enc, $payload, $sha, $key, 'acked', $at, $serverSeq, $at, $conversation, 1)
                """;
            var seq = cmd.Parameters.Add("$seq", SqliteType.Integer);
            var stream = cmd.Parameters.Add("$stream", SqliteType.Text);
            cmd.Parameters.AddWithValue("$kind", ConversationEventKinds.Item);
            cmd.Parameters.AddWithValue("$enc", PayloadRules.EncJson);
            var payload = cmd.Parameters.Add("$payload", SqliteType.Text);
            var sha = cmd.Parameters.Add("$sha", SqliteType.Text);
            var key = cmd.Parameters.Add("$key", SqliteType.Text);
            cmd.Parameters.AddWithValue("$at", now);
            var serverSeq = cmd.Parameters.Add("$serverSeq", SqliteType.Integer);
            var conversation = cmd.Parameters.Add("$conversation", SqliteType.Text);
            var text = new string('字', padding);
            for (var i = 0; i < count; i++)
            {
                var deviceSeq = firstSeq + i;
                var id = (deviceSeq % conversations).ToString("x32", CultureInfo.InvariantCulture);
                var json = $$"""{"id":"i{{deviceSeq}}","ord":{{deviceSeq}},"text":"{{text}}"}""";
                seq.Value = deviceSeq;
                stream.Value = SyncStreams.Conversation(id);
                payload.Value = json;
                sha.Value = PayloadRules.Sha256Hex(json);
                key.Value = $"item:{id}:i{deviceSeq}";
                serverSeq.Value = 100_000_000 + deviceSeq;
                conversation.Value = id;
                cmd.ExecuteNonQuery();
            }
        }

        using (var kv = connection.CreateCommand())
        {
            kv.Transaction = tx;
            kv.CommandText = "UPDATE kv SET value = $value WHERE key = $key";
            kv.Parameters.AddWithValue("$value", (firstSeq + count).ToString(CultureInfo.InvariantCulture));
            kv.Parameters.AddWithValue("$key", SyncStoreSchema.NextDeviceSeq);
            kv.ExecuteNonQuery();
        }

        tx.Commit();
    }
}

/// <summary>
/// 骨架的記憶體量測（步 9 的預設測試量到 20 萬筆，長情境量到 <see cref="ReconcileQueueRules.RetainedMaxRows"/>）：
/// 每多 10 萬筆已提交的對話列就從 sync.db 以 <see cref="SyncStore.LoadOutboxSlim"/> 整份載入一次（清單照筆數配置，同引擎載入時），
/// <c>GC.GetTotalMemory(true)</c> 前後比；另外量確認的路徑（完整的列換成骨架、放進照常成長的清單）的合計
/// </summary>
internal static class SkeletonMemory
{
    public const int SegmentRows = 100_000;

    /// <summary>每 10 萬筆的預算 16 MB（這個 repo 的 MB 一律是 1024×1024：AgentRules 把 5×1024×1024 位元組顯示成「5 MB」）</summary>
    public const long BudgetPer100k = 16L * 1024 * 1024;

    private const long PairedAt = 1791500000000;

    /// <summary>回傳每一段 10 萬筆的增量（位元組）與確認路徑的合計</summary>
    public static (IReadOnlyList<long> Segments, long AckPathTotal) Measure(int totalRows, ITestOutputHelper output)
    {
        using var temp = new TempDirectory("outbox-skeleton");
        var self = TestDevice.Create("OFFICE-PC");
        var path = temp.Combine("sync", "sync.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var segments = new List<long>();
        using (var store = SyncStore.Create(path, self.DeviceId, PairedAt, new ServerInstance("srv-instance-1", 0), new TrustBundle(1, [self.Entry()])))
        {
            var baseline = Settle();
            long previous = 0;
            for (var inserted = 0; inserted < totalRows; inserted += SegmentRows)
            {
                OutboxSlimTests.InsertAckedRows(path, inserted + 1, SegmentRows, conversations: 300, padding: 30, now: PairedAt);
                var (used, count) = MeasureLoad(store, baseline);
                count.Should().Be(inserted + SegmentRows);
                var segment = used - previous;
                output.WriteLine($"載入 {count:N0} 筆骨架：合計 {Mb(used)}，這 10 萬筆 {Mb(segment)}（每筆 {segment / (double)SegmentRows:0.0} 位元組）");
                segments.Add(segment);
                previous = used;
            }
        }

        var ackPathTotal = MeasureAckPath(totalRows, output);
        return (segments, ackPathTotal);
    }

    // 載入、量測與檢查各在自己的（不內嵌的）方法裡：方法返回後它的暫存（LoadOutboxSlim 的中間清單、FluentAssertions 的物件）
    // 就沒有人參照，下一段量的時候不會把上一份也算進去

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (long Used, int Count) MeasureLoad(SyncStore store, long baseline)
    {
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        var loaded = LoadExact(store, pool);
        var used = Settle() - baseline;
        CheckSkeletons(loaded);
        GC.KeepAlive(pool);
        return (used, loaded.Count);
    }

    /// <summary>同引擎載入：LoadOutboxSlim 的結果複製成照筆數配置的清單</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<OutboxEntry> LoadExact(SyncStore store, Dictionary<string, string> pool) =>
        store.LoadOutboxSlim(s => OutboxSlimTests.Share(pool, s)).ToList();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckSkeletons(List<OutboxEntry> entries) => entries.Should().OnlyContain(e => OutboxSliceRules.IsSkeleton(e));

    /// <summary>確認的路徑：每一列先是完整的（各自的字串），換成骨架（共用字串）後放進預設成長的清單；量清單與骨架的合計</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureAckPath(int rows, ITestOutputHelper output)
    {
        var baseline = Settle();
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        var live = BuildAckPath(rows, pool);
        var used = Settle() - baseline;
        var capacity = live.Capacity;
        CheckSkeletons(live);
        GC.KeepAlive(pool);
        output.WriteLine($"確認的路徑 {rows:N0} 筆（清單容量 {capacity:N0}）：合計 {Mb(used)}，平均每 10 萬筆 {Mb(used * SegmentRows / rows)}");
        return used;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<OutboxEntry> BuildAckPath(int rows, Dictionary<string, string> pool)
    {
        var live = new List<OutboxEntry>();
        var text = new string('字', 30);
        Func<string, string> share = s => OutboxSlimTests.Share(pool, s);
        for (var i = 1; i <= rows; i++)
        {
            var id = (i % 300).ToString("x32", CultureInfo.InvariantCulture);
            var json = $$"""{"id":"i{{i}}","ord":{{i}},"text":"{{text}}"}""";
            var full = new OutboxEntry(i, SyncStreams.Conversation(id), new string(ConversationEventKinds.Item.ToCharArray()), new string(PayloadRules.EncJson.ToCharArray()),
                json, PayloadRules.Sha256Hex(json), $"item:{id}:i{i}", OutboxState.Acked, PairedAt, 100_000_000 + i, null, PairedAt, id, 1);
            live.Add(OutboxSliceRules.Skeleton(full, share));
        }

        return live;
    }

    private static long Settle()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(true);
    }

    private static string Mb(long bytes) => $"{bytes / 1048576.0:0.00} MB";
}
