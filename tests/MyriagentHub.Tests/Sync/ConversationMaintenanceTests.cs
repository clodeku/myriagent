using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Media;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Sync;

/// <summary>對話資料的背景維護（WP4.2 計畫步 5；D31 的起始值、D40 的被拒事件保留期；2026-10-25 被取代的 conv.item 事件）</summary>
public sealed class ConversationMaintenanceTests
{
    private const string Device = "d_0000000000000000000000000A";

    private const long DayMs = 86_400_000;

    [Fact]
    public async Task 三十一天前的對話被拒事件刪掉_30天內與第3階段的不動()
    {
        await using var host = await HubTestHost.StartAsync();
        var now = host.Clock.NowMs;
        await InsertRejectedAsync(host, [
            (1, Conv, now - 31 * DayMs),
            (2, Conv, now - 30 * DayMs + 60_000),
            (3, Conv, now - 30 * DayMs),
            (4, null, now - 100 * DayMs),
            (5, OtherConv, now - 45 * DayMs),
        ]);

        var deleted = await Maintenance(host).PurgeRejectedAsync(CancellationToken.None);

        deleted.Should().Equal(2);
        Remaining(host).Should().Equal(2, 3, 4);
    }

    [Fact]
    public async Task 保留天數0_不刪()
    {
        await using var host = await HubTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Hub:Conversations:RejectedRetentionDays"] = "0" });
        await InsertRejectedAsync(host, [(1, Conv, host.Clock.NowMs - 400 * DayMs)]);

        (await Maintenance(host).PurgeRejectedAsync(CancellationToken.None)).Should().BeEmpty();

        Remaining(host).Should().Equal(1);
    }

    [Fact]
    public async Task 每個寫入工作最多1000列()
    {
        await using var host = await HubTestHost.StartAsync();
        var now = host.Clock.NowMs;
        // 2500 列過期的、每 10 列夾一列還在保留期內的（刪除的 rowid 範圍裡有不該刪的列）
        var rows = Enumerable.Range(1, 2750).Select(i => ((long)i, (string?)Conv, i % 11 == 0 ? now - DayMs : now - 40 * DayMs)).ToList();
        rows.Count(r => r.Item3 < now - 30 * DayMs).Should().Be(2500);
        await InsertRejectedAsync(host, rows);

        var deleted = await Maintenance(host).PurgeRejectedAsync(CancellationToken.None);

        deleted.Should().Equal(1000, 1000, 500);
        Remaining(host).Should().Equal(rows.Where(r => r.Item3 >= now - 30 * DayMs).Select(r => r.Item1));
    }

    [Fact]
    public async Task 啟動前的對話事件_背景補進營運計數_之後照常累加()
    {
        var dir = TempDir.Create();
        try
        {
            long events;
            long bytes;
            long maxSeq;
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                await ConversationEventLogTests.AddFixedDeviceAsync(host, Device);
                await ConversationEventLogTests.Append(host, Device,
                    ConversationEventLogTests.Ev(1, K.Created, Meta()), ConversationEventLogTests.Ev(2, K.Item, Item()),
                    ConversationEventLogTests.Ev(3, K.Item, Item("第二則", id: "item2", ord: 2)), ConversationEventLogTests.Ev(4, K.Queue, Queue("排隊")));
                // 第 3 階段的事件不算
                await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c =>
                    c.Execute("INSERT INTO events(stream, cls, author, kind, payload, created_at) VALUES('catalog', 'catalog', 'server', 'prefs.set', '{}', 0)"));
                events = EventLogTests.Count(host, "SELECT COUNT(*) FROM events WHERE cls IN ('meta', 'item')");
                bytes = EventLogTests.Count(host, "SELECT SUM(LENGTH(CAST(payload AS BLOB))) FROM events WHERE cls IN ('meta', 'item')");
                maxSeq = EventLogTests.Count(host, "SELECT MAX(seq) FROM events WHERE cls IN ('meta', 'item')");
                events.Should().Be(4);
            }

            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                await Maintenance(host).Baselined.WaitAsync(TimeSpan.FromSeconds(30));
                var counters = host.Services.GetRequiredService<ConversationCounters>();

                var counts = counters.Snapshot();

                (counts.ConversationEvents, counts.ConversationEventBytes, counts.EventsBaselined).Should().Be((events, bytes, true));
                counts.Devices.Should().ContainSingle().Which.Should().Be(new ConversationDeviceCounts(Device, 1, 0, 2, maxSeq, host.Clock.NowMs));
                counters.StartSeq.Should().Be(maxSeq + 1, "啟動時的最大 seq（含第 3 階段的那一筆）");

                var outcome = await ConversationEventLogTests.Append(host, Device, ConversationEventLogTests.Ev(5, K.Item, Item("啟動之後", id: "item3", ord: 3)));
                var after = counters.Snapshot();
                after.ConversationEvents.Should().Be(events + 1, "啟動之後的由 AfterCommit 累加，不重複");
                after.Devices.Single().MaxSeq.Should().Be(outcome.Ack.Results[0].Seq!.Value);
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 沒有對話事件_起始值是0_也標成補完()
    {
        await using var host = await HubTestHost.StartAsync();

        await Maintenance(host).Baselined.WaitAsync(TimeSpan.FromSeconds(30));

        var counts = host.Services.GetRequiredService<ConversationCounters>().Snapshot();
        (counts.ConversationEvents, counts.ConversationEventBytes, counts.EventsBaselined, counts.Devices.Count).Should().Be((0L, 0L, true, 0));
    }

    [Fact]
    public async Task 被取代超過7天的項目事件刪掉_最新版本_7天內的_其他種類都留著()
    {
        await using var host = await HubTestHost.StartAsync();
        await ConversationEventLogTests.AddFixedDeviceAsync(host, Device);
        await ConversationEventLogTests.Append(host, Device,
            ConversationEventLogTests.Ev(1, K.Created, Meta()),
            ConversationEventLogTests.Ev(2, K.Item, Item("一", "Running", id: "item1", ord: 1, ver: 1)),
            ConversationEventLogTests.Ev(3, K.Item, Item("一二", "Running", id: "item1", ord: 1, ver: 2)),
            ConversationEventLogTests.Ev(4, K.Item, Item("第二則", id: "item2", ord: 2)),
            ConversationEventLogTests.Ev(5, K.Item, Item("一二三", id: "item1", ord: 1, ver: 3)),
            ConversationEventLogTests.Ev(6, K.Meta, Meta("改名")),
            ConversationEventLogTests.Ev(7, K.Item, Item("要移除的", id: "item3", ord: 3)),
            ConversationEventLogTests.Ev(8, K.ItemRemoved, ConversationEventLogTests.Removal("item3")),
            ConversationEventLogTests.Ev(9, K.Queue, Queue("排隊")),
            ConversationEventLogTests.Ev(10, K.Item, Item("舊的", "Running", id: "item4", ord: 4, ver: 1)),
            ConversationEventLogTests.Ev(11, K.Item, Item("新的", id: "item4", ord: 4, ver: 2)),
            ConversationEventLogTests.Ev(12, K.Item, Item("七天內", "Running", id: "item5", ord: 5, ver: 1)),
            ConversationEventLogTests.Ev(13, K.Item, Item("七天內的新版", id: "item5", ord: 5, ver: 2)));
        await AgeAsync(host, 8 * DayMs, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
        await AgeAsync(host, 6 * DayMs, 12);
        await CatchUpMediaIndexAsync(host);
        var items = LiveItems(host);

        var deleted = await Maintenance(host).CompactSupersededAsync(CancellationToken.None);

        deleted.Should().Equal(4);
        DeviceSeqs(host).Should().Equal([1, 4, 5, 6, 8, 9, 11, 12, 13],
            "item1 的前兩版、移除了的 item3、item4 的舊版（新版在 7 天內也算被取代）刪掉；item5 的舊版還在 7 天內");
        LiveItems(host).Should().BeEquivalentTo(items, "物化的內容不變");
        (await Maintenance(host).CompactSupersededAsync(CancellationToken.None)).Should().BeEmpty("再做一次沒有可刪的");
    }

    [Fact]
    public async Task 媒體參照索引還沒處理的不刪()
    {
        await using var host = await HubTestHost.StartAsync();
        await ConversationEventLogTests.AddFixedDeviceAsync(host, Device);
        var outcome = await ConversationEventLogTests.Append(host, Device,
            ConversationEventLogTests.Ev(1, K.Created, Meta()),
            ConversationEventLogTests.Ev(2, K.Item, Item("一", "Running", ver: 1)),
            ConversationEventLogTests.Ev(3, K.Item, Item("一二", "Running", ver: 2)),
            ConversationEventLogTests.Ev(4, K.Item, Item("一二三", ver: 3)));
        await AgeAsync(host, 8 * DayMs, 1, 2, 3, 4);
        await CatchUpMediaIndexAsync(host);
        await SetMediaCursorAsync(host, outcome.Ack.Results[1].Seq!.Value);

        (await Maintenance(host).CompactSupersededAsync(CancellationToken.None)).Should().Equal(1);

        DeviceSeqs(host).Should().Equal([1, 3, 4], "游標之後的第 2 版留到索引處理過再刪");
    }

    [Fact]
    public async Task 刪除了的對話_項目事件都刪_meta類留著()
    {
        await using var host = await HubTestHost.StartAsync();
        await ConversationEventLogTests.AddFixedDeviceAsync(host, Device);
        await ConversationEventLogTests.Append(host, Device,
            ConversationEventLogTests.Ev(1, K.Created, Meta()),
            ConversationEventLogTests.Ev(2, K.Item, Item("一", id: "item1", ord: 1)),
            ConversationEventLogTests.Ev(3, K.Item, Item("二", id: "item2", ord: 2)),
            ConversationEventLogTests.Ev(4, K.Deleted, new JObject()),
            ConversationEventLogTests.Ev(5, K.Created, Meta("另一個"), OtherConv),
            ConversationEventLogTests.Ev(6, K.Item, Item("還在", id: "item1", ord: 1), OtherConv));
        await AgeAsync(host, 8 * DayMs, 1, 2, 3, 4, 5, 6);
        await CatchUpMediaIndexAsync(host);

        (await Maintenance(host).CompactSupersededAsync(CancellationToken.None)).Should().Equal(2);

        DeviceSeqs(host).Should().Equal([1, 4, 5, 6], "刪除時已清掉物化的內容（Pull 也不回）；另一個對話的最新版本留著");
    }

    [Fact]
    public async Task 清理之後重建物化表_內容和清理前相同()
    {
        await using var host = await HubTestHost.StartAsync();
        await ConversationEventLogTests.AddFixedDeviceAsync(host, Device);
        var events = new List<SyncEvent> { ConversationEventLogTests.Ev(1, K.Created, Meta()) };
        long deviceSeq = 2;
        for (var v = 1; v <= 5; v++)
        {
            for (var i = 1; i <= 4; i++)
                events.Add(ConversationEventLogTests.Ev(deviceSeq++, K.Item, Item($"第 {i} 則第 {v} 版", v < 5 ? "Running" : "Done", id: "item" + i, ord: i, ver: v)));
        }
        events.Add(ConversationEventLogTests.Ev(deviceSeq++, K.ItemRemoved, ConversationEventLogTests.Removal("item4")));
        events.Add(ConversationEventLogTests.Ev(deviceSeq++, K.Queue, Queue("排隊")));
        await ConversationEventLogTests.Append(host, Device, [.. events]);
        await AgeAsync(host, 8 * DayMs, [.. Enumerable.Range(1, (int)deviceSeq - 1).Select(i => (long)i)]);
        await CatchUpMediaIndexAsync(host);
        var items = LiveItems(host);
        var queue = ConversationEventLogTests.Text(host, "SELECT queue_json FROM conversation_runtime");

        (await Maintenance(host).CompactSupersededAsync(CancellationToken.None)).Sum().Should().Be(3 * 4 + 5, "每則留最後一版；移除了的 item4 五版全部");
        await host.Services.GetRequiredService<ConversationRebuild>().RebuildAsync("測試", CancellationToken.None);

        LiveItems(host).Should().BeEquivalentTo(items, "每筆項目最新的那一筆事件都在，重播收斂到同一份");
        ConversationEventLogTests.Text(host, "SELECT queue_json FROM conversation_runtime").Should().Be(queue);
        EventLogTests.Count(host, "SELECT item_count FROM conversations").Should().Be(3);
    }

    [Fact]
    public async Task 正在重建的對話略過()
    {
        await using var host = await HubTestHost.StartAsync();
        await ConversationEventLogTests.AddFixedDeviceAsync(host, Device);
        await ConversationEventLogTests.Append(host, Device,
            ConversationEventLogTests.Ev(1, K.Created, Meta()),
            ConversationEventLogTests.Ev(2, K.Item, Item("一", "Running", ver: 1)),
            ConversationEventLogTests.Ev(3, K.Item, Item("一二", ver: 2)));
        await AgeAsync(host, 8 * DayMs, 1, 2, 3);
        await CatchUpMediaIndexAsync(host);
        var writer = host.Services.GetRequiredService<SyncWriter>();
        var materializer = host.Services.GetRequiredService<ConversationMaterializer>();
        // 重建的第一塊清掉了 items、還沒重播：以 items 判斷會把最新版本也當成被取代
        await writer.WriteAsync(c => materializer.RebuildStart(c, Conv));

        (await Maintenance(host).CompactSupersededAsync(CancellationToken.None)).Should().BeEmpty();

        DeviceSeqs(host).Should().Equal(1, 2, 3);
        await writer.WriteAsync(c =>
        {
            materializer.RebuildReset(c);
            return 0;
        });
    }

    [Fact]
    public async Task 被取代的保留天數0_不刪()
    {
        await using var host = await HubTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Hub:Conversations:SupersededRetentionDays"] = "0" });
        await ConversationEventLogTests.AddFixedDeviceAsync(host, Device);
        await ConversationEventLogTests.Append(host, Device,
            ConversationEventLogTests.Ev(1, K.Created, Meta()),
            ConversationEventLogTests.Ev(2, K.Item, Item("一", "Running", ver: 1)),
            ConversationEventLogTests.Ev(3, K.Item, Item("一二", ver: 2)));
        await AgeAsync(host, 400 * DayMs, 1, 2, 3);
        await CatchUpMediaIndexAsync(host);

        (await Maintenance(host).CompactSupersededAsync(CancellationToken.None)).Should().BeEmpty();

        DeviceSeqs(host).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task 被取代的項目事件_每個寫入工作最多1000列()
    {
        await using var host = await HubTestHost.StartAsync();
        await ConversationEventLogTests.AddFixedDeviceAsync(host, Device);
        await ConversationEventLogTests.Append(host, Device, ConversationEventLogTests.Ev(1, K.Created, Meta()));
        const int versions = 2203;
        for (var start = 0; start < versions; start += 100)
        {
            var batch = Enumerable.Range(start + 1, Math.Min(100, versions - start))
                .Select(v => ConversationEventLogTests.Ev(v + 1, K.Item, Item("第 " + v + " 版", v < versions ? "Running" : "Done", ver: v)))
                .ToArray();
            await ConversationEventLogTests.Append(host, Device, batch);
        }
        await AgeAsync(host, 8 * DayMs, [.. Enumerable.Range(1, versions + 1).Select(i => (long)i)]);
        await CatchUpMediaIndexAsync(host);

        (await Maintenance(host).CompactSupersededAsync(CancellationToken.None)).Should().Equal(1000, 1000, 202);

        DeviceSeqs(host).Should().Equal(1, versions + 1);
    }

    private static ConversationMaintenance Maintenance(HubTestHost host) => host.Services.GetRequiredService<ConversationMaintenance>();

    /// <summary>把這台裝置這些 deviceSeq 的事件的收到時間往前推 <paramref name="byMs"/></summary>
    private static Task AgeAsync(HubTestHost host, long byMs, params long[] deviceSeqs) =>
        host.Services.GetRequiredService<SyncWriter>().WriteAsync(c =>
        {
            foreach (var deviceSeq in deviceSeqs)
                c.Execute("UPDATE events SET created_at = created_at - $by WHERE author = $a AND device_seq = $d", ("$by", byMs), ("$a", Device), ("$d", deviceSeq));
            return 0;
        });

    private static async Task CatchUpMediaIndexAsync(HubTestHost host) =>
        (await host.Services.GetRequiredService<MediaRefIndexer>().CatchUpAsync(TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30)))
            .Should().BeTrue("媒體參照的索引追上最新的事件");

    private static Task SetMediaCursorAsync(HubTestHost host, long seq) =>
        host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute(
            "UPDATE server_meta SET value = $v WHERE key = $k", ("$v", seq.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("$k", MediaRefIndexer.CursorKey)));

    private static List<long> DeviceSeqs(HubTestHost host) =>
        ConversationEventLogTests.Rows(host, "SELECT device_seq FROM events WHERE author = '" + Device + "' ORDER BY device_seq").Select(r => (long)r[0]!).ToList();

    private static List<(string Conversation, string Item, long Seq, string Body)> LiveItems(HubTestHost host) =>
        ConversationEventLogTests.Rows(host, "SELECT conversation_id, item_id, last_seq, body FROM items WHERE removed = 0 ORDER BY conversation_id, item_id")
            .Select(r => ((string)r[0]!, (string)r[1]!, (long)r[2]!, (string)r[3]!)).ToList();

    private static Task InsertRejectedAsync(HubTestHost host, IReadOnlyList<(long DeviceSeq, string? Conversation, long CreatedAt)> rows) =>
        host.Services.GetRequiredService<SyncWriter>().WriteAsync(c =>
        {
            foreach (var (deviceSeq, conversation, createdAt) in rows)
            {
                c.Execute(
                    "INSERT INTO rejected_events(author, device_seq, payload_sha256, stream, conversation_id, epoch, kind, payload, enc, reason, created_at) "
                    + "VALUES($a, $d, $sha, $stream, $c, 1, $kind, '{}', 'json', 'not_owner', $at)",
                    ("$a", Device), ("$d", deviceSeq), ("$sha", new byte[32]), ("$stream", conversation is null ? SyncStreams.Catalog : SyncStreams.Conversation(conversation)),
                    ("$c", conversation), ("$kind", conversation is null ? "prefs.update" : K.Item), ("$at", createdAt));
            }
        });

    private static List<long> Remaining(HubTestHost host) =>
        ConversationEventLogTests.Rows(host, "SELECT device_seq FROM rejected_events ORDER BY device_seq").Select(r => (long)r[0]!).ToList();
}
