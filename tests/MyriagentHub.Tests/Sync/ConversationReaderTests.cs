using System.Globalization;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.ConversationEventLogTests;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 對話物化表的唯讀讀取（WP4.2 計畫步 8；D10、D11、D20、D38）：清單的排除與排序與上限、讀取時以目前的 account_links 對照帳戶、
/// loadOlder 的分頁、交易看到的最大 seq、讀不懂的欄位給預設而且記錄不帶內容。以真的 EventLog 寫入（同步驟 3 的測試），再以讀取端讀
/// </summary>
public sealed class ConversationReaderTests
{
    [Fact]
    public async Task 已刪除不在清單()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta("留著"), Id(1)), Ev(2, K.Created, Meta("刪掉"), Id(2)), Ev(3, K.Item, Item(), Id(2)),
            Ev(4, K.Deleted, new JObject(), Id(2)));
        var reader = Reader(host);

        reader.List().Rows.Select(r => r.ConversationId).Should().Equal(Id(1));
        reader.ReadHeader(Id(2)).Header.Should().BeNull("已刪除的對話沒有標頭");
        reader.ReadSnapshot(Id(2)).Should().BeNull();
        reader.ReadItem(Id(2), "item1").Item.Should().BeNull();
        reader.ReadHeader(Id(1)).Header!.Title.Should().Be("留著");
        reader.ReadHeader(Id(3)).Header.Should().BeNull("不存在的對話");
        reader.ReadSnapshot(Id(3)).Should().BeNull();
    }

    [Fact]
    public async Task 排序依顯示時間再依seq()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, MetaAt(1_700_000_001_000), Id(1)), Ev(2, K.Created, MetaAt(1_700_000_002_000), Id(2)),
            Ev(3, K.Created, MetaAt(1_700_000_002_000), Id(3)));
        var reader = Reader(host);

        reader.List().Rows.Select(r => r.ConversationId).Should().Equal([Id(3), Id(2), Id(1)], "顯示時間相同的，seq 大的在前");

        // 項目不改顯示時間、只改 last_seq：同一個時間的兩個對話換順序，時間早的照樣在後面
        await Append(host, a, Ev(4, K.Item, Item(), Id(2)));
        var rows = reader.List().Rows;
        rows.Select(r => r.ConversationId).Should().Equal(Id(2), Id(3), Id(1));
        rows[0].UpdatedAt.Should().Be(1_700_000_002_000);
        rows.Should().OnlyContain(r => r.OwnerDeviceId == a && r.Provider == Myriagent.Agents.AgentProvider.Claude && r.Title == "標題");
    }

    [Fact]
    public async Task 清單1000筆上限()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        const int total = ConversationViewRules.MaxListItems + 1;
        for (var start = 1; start <= total; start += 500)
        {
            var events = Enumerable.Range(start, Math.Min(500, total - start + 1))
                .Select(i => Ev(i, K.Created, MetaAt(1_700_000_000_000 + i), Id(i)))
                .ToArray();
            (await Append(host, a, events)).Ack.Results.Should().OnlyContain(r => r.Seq != null);
        }
        var reader = Reader(host);

        var rows = reader.List().Rows;
        rows.Should().HaveCount(1000);
        rows[0].ConversationId.Should().Be(Id(total), "最新的在前");
        rows[^1].ConversationId.Should().Be(Id(2));
        rows.Select(r => r.ConversationId).Should().NotContain(Id(1), "最舊的那一筆超過上限");
        reader.List(limit: 3).Rows.Select(r => r.ConversationId).Should().Equal(Id(total), Id(total - 1), Id(total - 2));
        FluentActions.Invoking(() => reader.List(limit: 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task 重新連結帳戶後accountId跟著變()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await AppendCatalog(host, a, EventLogTests.Ev(1, SyncEventKinds.LinkUpsert, EventLogTests.Link("acc-1", "claude:org1:a@example.com")));
        var first = Text(host, $"SELECT logical_account_id FROM account_links WHERE device_id = '{a}' AND local_account_id = 'acc-1'");
        await Append(host, a, Ev(2, K.Created, Meta()));
        var reader = Reader(host);
        reader.List().Rows.Single().AccountId.Should().Be(first);
        reader.ReadHeader(Conv).Header!.AccountId.Should().Be(first);

        // 拿掉連結：讀取時對不到（標頭的 accountMissing）
        await AppendCatalog(host, a, EventLogTests.Ev(3, SyncEventKinds.LinkRemoved, new LinkRemovedPayload("acc-1")));
        reader.List().Rows.Single().AccountId.Should().BeNull();
        reader.ReadHeader(Conv).Header!.AccountId.Should().BeNull();

        // 連到另一個身分：讀取時照目前的對應，不是物化時寫下的值（D11）
        await AppendCatalog(host, a, EventLogTests.Ev(4, SyncEventKinds.LinkUpsert, EventLogTests.Link("acc-1", "claude:org2:b@example.com")));
        var second = Text(host, $"SELECT logical_account_id FROM account_links WHERE device_id = '{a}' AND local_account_id = 'acc-1'");
        second.Should().NotBe(first);
        reader.List().Rows.Single().AccountId.Should().Be(second);
        reader.ReadHeader(Conv).Header!.AccountId.Should().Be(second);
        reader.ReadSnapshot(Conv)!.Header.AccountId.Should().Be(second);
        Text(host, $"SELECT logical_account_id FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(first, "物化時的值不變");

        // 邏輯帳戶被刪掉（擁有者在網頁刪的）：也對不到
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(context =>
            context.Execute("UPDATE logical_accounts SET deleted_at = 1 WHERE logical_account_id = $id", ("$id", second)));
        reader.List().Rows.Single().AccountId.Should().BeNull();
        reader.ReadHeader(Conv).Header!.AccountId.Should().BeNull();
    }

    [Fact]
    public async Task 別台裝置的同名本機帳戶_不算這個對話的帳戶()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var b = await EventLogTests.AddDeviceAsync(host);
        await AppendCatalog(host, b, EventLogTests.Ev(1, SyncEventKinds.LinkUpsert, EventLogTests.Link("acc-1", "claude:org1:a@example.com")));
        await Append(host, a, Ev(1, K.Created, Meta()));

        var reader = Reader(host);
        reader.List().Rows.Single().AccountId.Should().BeNull("對照的是 (擁有者裝置, 本機帳戶)");
        reader.ReadHeader(Conv).Header!.AccountId.Should().BeNull();
    }

    [Fact]
    public async Task loadOlder依ord分頁_找不到beforeId回最後一頁()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        // 依 ord 由大到小送：分頁看的是 ord，不是送達的順序
        var events = new List<SyncEvent> { Ev(1, K.Created, Meta()) };
        for (var ord = 10; ord >= 1; ord--)
            events.Add(Ev(events.Count + 1, K.Item, Item("第 " + ord + " 筆", id: "item" + ord, ord: ord)));
        events.Add(Ev(events.Count + 1, K.ItemRemoved, Removal("item5")));
        events.Add(Ev(events.Count + 1, K.Created, Meta(), OtherConv));
        events.Add(Ev(events.Count + 1, K.Item, Item("別的對話", id: "item3", ord: 3), OtherConv));
        await Append(host, a, events.ToArray());
        var reader = Reader(host);

        Ids(reader.ReadPage(Conv, null, 3).Page).Should().Equal("item8", "item9", "item10");
        reader.ReadPage(Conv, null, 3).Page.HasMore.Should().BeTrue();
        Ids(reader.ReadPage(Conv, "item8", 3).Page).Should().Equal(["item4", "item6", "item7"], "移除的不在頁裡");
        Ids(reader.ReadPage(Conv, "item5", 3).Page).Should().Equal(["item2", "item3", "item4"], "移除的那一筆照它的 ord 分頁");
        var tail = reader.ReadPage(Conv, "item2", 3).Page;
        Ids(tail).Should().Equal("item1");
        tail.HasMore.Should().BeFalse();
        reader.ReadPage(Conv, "item1", 3).Page.Items.Should().BeEmpty();
        reader.ReadPage(Conv, "item1", 3).Page.HasMore.Should().BeFalse();
        Ids(reader.ReadPage(Conv, "item4", 3).Page).Should().Equal("item1", "item2", "item3");
        reader.ReadPage(Conv, "item4", 3).Page.HasMore.Should().BeFalse("正好三筆，前面沒有了");

        foreach (var missing in new[] { "nope", "item11" })
        {
            var last = reader.ReadPage(Conv, missing, 3).Page;
            Ids(last).Should().Equal(["item8", "item9", "item10"], "找不到 beforeId 回最後一頁");
            last.HasMore.Should().BeTrue();
        }
        Ids(reader.ReadPage(Conv, null, 100).Page).Should().Equal("item1", "item2", "item3", "item4", "item6", "item7", "item8", "item9", "item10");
        reader.ReadPage(Conv, null, 100).Page.HasMore.Should().BeFalse();
        Ids(reader.ReadPage(OtherConv, null, 3).Page).Should().Equal("item3");
        reader.ReadPage(Id(9), null, 3).Page.Items.Should().BeEmpty("不存在的對話");

        var item = reader.ReadPage(Conv, null, 1).Page.Items.Single();
        item.Should().BeEquivalentTo(new { ItemId = "item10", Ord = 10L, Kind = "Assistant", Status = "Done", Final = true, Enc = PayloadRules.EncJson, ReceivedAt = host.Clock.NowMs });
        item.Body.Should().Be(Text(host, $"SELECT body FROM items WHERE conversation_id = '{Conv}' AND item_id = 'item10'"));
        item.LastSeq.Should().Be(EventLogTests.Count(host, $"SELECT last_seq FROM items WHERE conversation_id = '{Conv}' AND item_id = 'item10'"));
        FluentActions.Invoking(() => reader.ReadPage(Conv, null, 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ReadItem_沒移除的才有_收到的時間是最後一筆事件的()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item("第一版")), Ev(3, K.Item, Item("留著", id: "item2", ord: 2)));
        var first = host.Clock.NowMs;
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await Append(host, a, Ev(4, K.Item, Item("第二版", ver: 2)), Ev(5, K.ItemRemoved, Removal("item2")));
        var reader = Reader(host);

        var item = reader.ReadItem(Conv, "item1").Item!;
        Decode(item.Enc, item.Body).Should().Contain("第二版");
        item.ReceivedAt.Should().Be(first + 300_000, "最後一筆 conv.item 收到的時間");
        reader.ReadItem(Conv, "item2").Item.Should().BeNull("已移除");
        reader.ReadItem(Conv, "item9").Item.Should().BeNull();
        reader.ReadItem(OtherConv, "item1").Item.Should().BeNull();
    }

    [Fact]
    public async Task 標頭與輸入區照物化表()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var meta = Meta("改過的標題");
        meta["permissionMode"] = "Plan";
        meta["effort"] = "high";
        meta["customFolder"] = true;
        meta["workingDirectory"] = "C:/repo/proj";
        meta["models"] = JArray.Parse("""[{"value":"opus","label":"Opus","description":"複雜工作","efforts":["low","high"]},{"value":"haiku","label":"Haiku"}]""");
        var queue = Queue("接著跑測試", "再整理");
        queue["more"] = 3;
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Meta, meta), Ev(3, K.Turn, Turn(ConversationTurnPhases.Completed)), Ev(4, K.Queue, queue));
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await Append(host, a, Ev(5, K.Turn, Turn(ConversationTurnPhases.Started)), Ev(6, K.Item, Item()));
        var reader = Reader(host);

        var header = reader.ReadHeader(Conv).Header!;
        header.Should().BeEquivalentTo(new
        {
            ConversationId = Conv,
            OwnerDeviceId = a,
            Title = "改過的標題",
            Model = "opus",
            Effort = "high",
            PermissionMode = "Plan",
            CustomFolder = true,
            WorkingDirectory = "C:/repo/proj",
            Running = true,
            StatusText = (string?)null,
            TurnStartedAt = host.Clock.NowMs,
            LeaseState = ConversationLeaseStates.Owned,
            SyncPolicy = ConversationSyncPolicies.Full,
            Epoch = 1L,
        });
        header.LastTurn!.Usage.Should().Be(new ConversationTurnUsage(10, 5, 0), "上一輪結束的用量");
        header.Models!.Select(m => (m.Value, m.Label, m.Description, Efforts: string.Join(",", m.Efforts ?? [])))
            .Should().Equal(("opus", "Opus", "複雜工作", "low,high"), ("haiku", "Haiku", null, ""));
        header.LastSeq.Should().Be(EventLogTests.Count(host, $"SELECT last_seq FROM conversations WHERE conversation_id = '{Conv}'"));

        var runtime = reader.ReadRuntime(Conv).Runtime!;
        runtime.Queue.Should().Equal(new ConversationQueueEntry("q0", "接著跑測試"), new ConversationQueueEntry("q1", "再整理"));
        runtime.More.Should().Be(3);
        runtime.Busy.Should().BeFalse();
        reader.ReadRuntime(OtherConv).Runtime.Should().BeNull();

        var snapshot = reader.ReadSnapshot(Conv)!;
        snapshot.Header.Should().BeEquivalentTo(header);
        snapshot.Page.Items.Select(i => i.ItemId).Should().Equal("item1");
        snapshot.Page.HasMore.Should().BeFalse();
        snapshot.Runtime.Should().BeEquivalentTo(runtime);

        // recovering 與顯示名稱
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(context =>
        {
            context.Execute("UPDATE conversations SET lease_state = 'recovering' WHERE conversation_id = $c", ("$c", Conv));
            context.Execute("INSERT INTO device_streams(device_id, name) VALUES($d, '工作電腦') ON CONFLICT(device_id) DO UPDATE SET name = excluded.name", ("$d", a));
        });
        reader.ReadHeader(Conv).Header!.LeaseState.Should().Be(ConversationLeaseStates.Recovering);
        reader.ReadSnapshot(Conv)!.DeviceNames.Should().Contain(a, "工作電腦");
        reader.List().DeviceNames.Should().Contain(a, "工作電腦");
    }

    [Fact]
    public async Task 回傳交易看到的最大seq()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var reader = Reader(host);
        reader.List().Seq.Should().Be(MaxSeq(host));

        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Queue, Queue("一")));
        var high = MaxSeq(host);
        high.Should().BeGreaterThan(0);
        Seqs(reader).Should().OnlyContain(s => s == high);

        // 別的串流的事件也算（閘道只推比它新的變更）
        await AppendCatalog(host, a, EventLogTests.Ev(4, SyncEventKinds.LinkUpsert, EventLogTests.Link("acc-1", "claude:org1:a@example.com")));
        var next = MaxSeq(host);
        next.Should().BeGreaterThan(high);
        Seqs(reader).Should().OnlyContain(s => s == next);
    }

    [Fact]
    public async Task provider不認得的列跳過_讀不懂的欄位給預設_記錄只有欄位與id()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta("好的"), Id(1)), Ev(2, K.Created, Meta("壞的"), Id(2)), Ev(3, K.Created, Meta("中繼壞了"), Id(3)),
            Ev(4, K.Queue, Queue("排隊"), Id(3)), Ev(5, K.Turn, Turn(ConversationTurnPhases.Completed), Id(3)));
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(context =>
        {
            context.Execute("UPDATE conversations SET provider = 'Foo' WHERE conversation_id = $c", ("$c", Id(2)));
            context.Execute("UPDATE conversations SET meta_json = '{秘密內容', last_turn = '[秘密內容' WHERE conversation_id = $c", ("$c", Id(3)));
            context.Execute("UPDATE conversation_runtime SET queue_json = '{\"entries\":3,\"秘密內容\":1}' WHERE conversation_id = $c", ("$c", Id(3)));
        });
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));
        var reader = new ConversationReader(host.Services.GetRequiredService<SyncWriter>(), factory.CreateLogger<ConversationReader>());

        reader.List().Rows.Select(r => r.ConversationId).Should().Equal([Id(3), Id(1)], "provider 不認得的那一列略過");
        reader.List();
        reader.ReadHeader(Id(2)).Header.Should().BeNull();
        var broken = reader.ReadHeader(Id(3)).Header!;
        broken.Should().BeEquivalentTo(new { Title = "中繼壞了", Model = (string?)null, AccountId = (string?)null, CustomFolder = false, LastTurn = (ConversationTurnPayload?)null });
        var runtime = reader.ReadRuntime(Id(3)).Runtime!;
        runtime.Queue.Should().BeEmpty();
        runtime.More.Should().Be(0);

        var logs = capture.Entries.Where(e => e.Category.Contains(nameof(ConversationReader), StringComparison.Ordinal)).ToList();
        logs.Should().HaveCount(4, "provider、meta_json、last_turn、queue_json 各一次（同一個對話的同一欄只記一次）");
        logs.Should().OnlyContain(e => e.Level == LogLevel.Warning);
        capture.AllText().Should().NotContain("秘密內容").And.Contain(Id(2)).And.Contain(Id(3)).And.Contain("meta_json");
    }

    [Fact]
    public void 記錄去重最多1000個鍵()
    {
        ConversationReader.MaxLoggedKeys.Should().Be(1000);
    }

    [Fact]
    public void 讀取一律經OpenRead_不自己開連線_也不寫()
    {
        // sync.db 一律不用連線池、連線只由 SqliteSetup 產生（CLAUDE.md、DataTests）：讀取端只准經 SyncWriter.OpenRead（唯讀、Pooling=False）
        var calls = IlCalls.Of(typeof(ConversationReader));
        calls.Should().Contain(m => m.DeclaringType == typeof(SyncWriter) && m.Name == nameof(SyncWriter.OpenRead), "確認掃得到呼叫");
        calls.Where(m => m.DeclaringType == typeof(SyncWriter)).Select(m => m.Name).Distinct().Should().Equal(nameof(SyncWriter.OpenRead));
        calls.Should().NotContain(m => m is System.Reflection.ConstructorInfo && typeof(Microsoft.Data.Sqlite.SqliteConnection).IsAssignableFrom(m.DeclaringType),
            "不自己 new 連線");
        calls.Should().NotContain(m => m.DeclaringType == typeof(SqliteSetup), "不自己組連線字串");
        calls.Should().NotContain(m => m.DeclaringType == typeof(Microsoft.Data.Sqlite.SqliteConnection) && m.Name == nameof(Microsoft.Data.Sqlite.SqliteConnection.Open));
    }

    // ───── 工具 ─────

    private static ConversationReader Reader(HubTestHost host) =>
        new(host.Services.GetRequiredService<SyncWriter>(), NullLogger<ConversationReader>.Instance);

    private static string Id(int n) => n.ToString("x32", CultureInfo.InvariantCulture);

    private static JObject MetaAt(long updatedAt)
    {
        var meta = Meta();
        meta["updatedAt"] = updatedAt;
        return meta;
    }

    private static Task<BatchOutcome> AppendCatalog(HubTestHost host, string deviceId, SyncEvent e) =>
        EventLogTests.AppendAsync(host, deviceId, EventLogTests.Batch(e));

    private static IEnumerable<string> Ids(ConversationPage page) => page.Items.Select(i => i.ItemId);

    private static long MaxSeq(HubTestHost host) => EventLogTests.Count(host, "SELECT IFNULL(MAX(seq), 0) FROM events");

    private static long[] Seqs(ConversationReader reader) =>
    [
        reader.List().Seq,
        reader.ReadHeader(Conv).Seq,
        reader.ReadPage(Conv, null, 80).Seq,
        reader.ReadItem(Conv, "item1").Seq,
        reader.ReadRuntime(Conv).Seq,
        reader.ReadSnapshot(Conv)?.Seq ?? reader.List().Seq,
    ];
}
