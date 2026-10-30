using Myriagent.Sync;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.ConversationEventLogTests;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using K = Myriagent.Sync.ConversationEventKinds;
using P = Myriagent.Sync.ConversationSyncPolicies;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 政策降級與刪除的清除、Pull 過濾（WP4.2 計畫步 4；D7、D17、D34、答 4、P6、P10）：
/// full 降成 metadata／paused 時伺服器清掉物化的內容（不只是隱藏），刪除也清；Pull 只看對話目前的狀態，不回已刪除或不是 full 的對話的 item 類事件
/// </summary>
public sealed class ConversationPolicyTests
{
    // ───── 清除 ─────

    [Fact]
    public async Task full改metadata_items與runtime清空_max_ord不變()
    {
        await using var host = await HubTestHost.StartAsync(withoutConversationGateway: true);
        var a = await EventLogTests.AddDeviceAsync(host);
        var feed = host.Services.GetRequiredService<ConversationChangeFeed>();
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Item, Item(id: "item2", ord: 5)), Ev(4, K.Queue, Queue("甲")),
            Ev(5, K.Item, Item(), conversationId: OtherConv));
        Drain(feed);

        var outcome = await Append(host, a, Ev(6, K.Meta, Meta("只同步標題", P.Metadata)));

        var seq = outcome.Ack.Results.Single().Seq!.Value;
        EventLogTests.Count(host, $"SELECT COUNT(*) FROM items WHERE conversation_id = '{Conv}'").Should().Be(0, "答 4：清除，不只是隱藏");
        EventLogTests.Count(host, $"SELECT COUNT(*) FROM conversation_runtime WHERE conversation_id = '{Conv}'").Should().Be(0);
        Rows(host, $"SELECT item_count, max_ord, sync_policy, title, last_seq FROM conversations WHERE conversation_id = '{Conv}'").Single()
            .Should().Equal(0L, 5L, P.Metadata, "只同步標題", seq);
        EventLogTests.Count(host, $"SELECT COUNT(*) FROM items WHERE conversation_id = '{OtherConv}'").Should().Be(0, "沒有那個對話的列");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM events WHERE kind = 'conv.item'").Should().Be(2, "原始事件留到之後的保留期清理");
        Drain(feed).Select(c => (c.Kind, c.Seq)).Should().Equal((ConversationChangeKind.Meta, seq), (ConversationChangeKind.Purged, seq));
        var cache = host.Services.GetRequiredService<ConversationDirectoryCache>();
        cache.TryGet(Conv, out var entry).Should().BeTrue();
        (entry!.ItemCount, entry.SyncPolicy).Should().Be((0, P.Metadata));
        host.Services.GetRequiredService<ConversationCounters>().Snapshot().Items.Should().Be(0);
    }

    [Fact]
    public async Task full改paused_items與runtime清空()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Queue, Queue("甲")));

        await Append(host, a, Ev(4, K.Meta, Meta("暫停", P.Paused)));

        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(0, "含桌面的 DLP 自動暫停");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM conversation_runtime").Should().Be(0);
        EventLogTests.Count(host, $"SELECT item_count FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(0);
    }

    [Fact]
    public async Task metadata改paused與改回full_不清除也不送Purged()
    {
        await using var host = await HubTestHost.StartAsync(withoutConversationGateway: true);
        var a = await EventLogTests.AddDeviceAsync(host);
        var feed = host.Services.GetRequiredService<ConversationChangeFeed>();
        await Append(host, a, Ev(1, K.Created, Meta(policy: P.Metadata)));

        await Append(host, a, Ev(2, K.Meta, Meta(policy: P.Paused)), Ev(3, K.Meta, Meta(policy: P.Full)), Ev(4, K.Item, Item()));

        Drain(feed).Select(c => c.Kind).Should().Equal(ConversationChangeKind.Meta, ConversationChangeKind.Meta, ConversationChangeKind.Meta, ConversationChangeKind.Item);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(1);
    }

    [Fact]
    public async Task deleted_清除items與runtime_保留列()
    {
        await using var host = await HubTestHost.StartAsync(withoutConversationGateway: true);
        var a = await EventLogTests.AddDeviceAsync(host);
        var feed = host.Services.GetRequiredService<ConversationChangeFeed>();
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Item, Item(id: "item2", ord: 4)), Ev(4, K.Queue, Queue("甲")));
        Drain(feed);

        var outcome = await Append(host, a, Ev(5, K.Deleted, new JObject()));

        var seq = outcome.Ack.Results.Single().Seq!.Value;
        Rows(host, $"SELECT deleted_at IS NOT NULL, deleted_by, item_count, max_ord, last_seq FROM conversations WHERE conversation_id = '{Conv}'").Single()
            .Should().Equal(1L, a, 0L, 4L, seq);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(0);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM conversation_runtime").Should().Be(0);
        Drain(feed).Select(c => c.Kind).Should().Equal([ConversationChangeKind.Deleted], "Deleted 本身就表示內容已清");
        var counts = host.Services.GetRequiredService<ConversationCounters>().Snapshot();
        (counts.Count, counts.Deleted, counts.Items).Should().Be((0L, 1L, 0L));
    }

    [Fact]
    public async Task deleted之後一律回deleted()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Deleted, new JObject()));

        var outcome = await Append(host, a, Ev(4, K.Created, Meta()), Ev(5, K.Meta, Meta(policy: P.Full)), Ev(6, K.Item, Item()), Ev(7, K.Queue, Queue("甲")));

        outcome.Ack.Results.Should().OnlyContain(r => r.Rejected == ConversationRejectCodes.Deleted, "不能再改回同步（答 4）");
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(0);
        EventLogTests.Count(host, $"SELECT deleted_at IS NOT NULL FROM conversations WHERE conversation_id = '{Conv}'").Should().Be(1);
        host.Services.GetRequiredService<IConversationDirectory>().Find(Conv).Should().BeNull();
    }

    [Fact]
    public async Task 改回full之後再送item_upsert且removed歸0()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item("第一版")), Ev(3, K.ItemRemoved, Removal("item1")), Ev(4, K.Item, Item(id: "item2", ord: 2)));
        EventLogTests.Count(host, "SELECT removed FROM items WHERE item_id = 'item1'").Should().Be(1);
        await Append(host, a, Ev(5, K.Meta, Meta(policy: P.Metadata)));

        var outcome = await Append(host, a, Ev(6, K.Meta, Meta(policy: P.Full)), Ev(7, K.Item, Item("重送")));

        outcome.Ack.Results.Should().OnlyContain(r => r.Seq != null);
        var row = Rows(host, $"SELECT removed, last_seq, enc, body FROM items WHERE conversation_id = '{Conv}' AND item_id = 'item1'").Single();
        row[0].Should().Be(0L);
        row[1].Should().Be(outcome.Ack.Results[1].Seq!.Value);
        Decode((string)row[2]!, (string)row[3]!).Should().Contain("重送");
        Rows(host, $"SELECT item_count, max_ord FROM conversations WHERE conversation_id = '{Conv}'").Single().Should().Equal(1L, 2L);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM items").Should().Be(1, "item2 被清掉了，要等擁有者重送");
    }

    // ───── Pull 過濾 ─────

    [Fact]
    public async Task 降級後Pull不回item事件_meta照回()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var log = host.Services.GetRequiredService<EventLog>();
        var before = await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Queue, Queue("甲")), Ev(4, K.Turn, Turn("started")),
            Ev(5, K.Created, Meta(), conversationId: OtherConv), Ev(6, K.Item, Item(), conversationId: OtherConv));
        var seqs = before.Ack.Results.Select(r => r.Seq!.Value).ToArray();

        var downgrade = await Append(host, a, Ev(7, K.Meta, Meta(policy: P.Metadata)));
        var down = downgrade.Ack.Results.Single().Seq!.Value;

        log.Pull(Stream(Conv)).Events.Select(e => e.Seq).Should().Equal([seqs[0], seqs[3], down], "stream 游標：item 類不回，meta 類照回");
        log.Pull(Cls(SyncClasses.Item)).Events.Select(e => e.Seq).Should().Equal([seqs[5]], "cls 游標：只剩 full 的對話");
        log.Pull(Cls(SyncClasses.Meta)).Events.Select(e => e.Seq).Should().Equal(seqs[0], seqs[3], seqs[4], down);
        log.Pull(Stream(OtherConv)).Events.Select(e => e.Seq).Should().Equal(seqs[4], seqs[5]);
    }

    [Fact]
    public async Task 刪除後Pull不回item事件_刪除事件照回()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var log = host.Services.GetRequiredService<EventLog>();
        var outcome = await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.ItemRemoved, Removal("item1")), Ev(4, K.Attention, new JObject { ["kind"] = "completed" }),
            Ev(5, K.Deleted, new JObject()));
        var seqs = outcome.Ack.Results.Select(r => r.Seq!.Value).ToArray();

        log.Pull(Stream(Conv)).Events.Select(e => (e.Seq, e.Kind)).Should().Equal((seqs[0], K.Created), (seqs[3], K.Attention), (seqs[4], K.Deleted));
        log.Pull(Cls(SyncClasses.Item)).Events.Should().BeEmpty();
    }

    [Fact]
    public async Task 改回full之後_新舊都照回_副本依seq收斂到擁有者的內容()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var log = host.Services.GetRequiredService<EventLog>();
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item("a1", id: "a")), Ev(3, K.Item, Item("b1", id: "b", ord: 2)));

        // 一直在拉的副本與游標停在降級前的副本：降級前都拉到 a、b
        var live = new Replica();
        var stopped = new Replica();
        live.PullFrom(log, Conv);
        stopped.PullFrom(log, Conv);
        live.Items.Should().Equal(new Dictionary<string, string> { ["a"] = "a1", ["b"] = "b1" });

        await Append(host, a, Ev(4, K.Meta, Meta(policy: P.Metadata)));
        live.PullFrom(log, Conv);
        live.Items.Should().BeEmpty("看到非 full 的 meta 就丟掉 replica_items（P10）");
        var midway = new Replica();
        midway.PullFrom(log, Conv);
        midway.Items.Should().BeEmpty("降級期間從頭拉的副本只拿到 meta 類");
        midway.Pulled.Should().OnlyContain(k => k == K.Created || k == K.Meta);

        // 改回 full：擁有者整份重送（a′、b 已不在的 itemRemoved、新的 c）
        await Append(host, a, Ev(5, K.Meta, Meta(policy: P.Full)), Ev(6, K.Item, Item("a2", id: "a")), Ev(7, K.ItemRemoved, Removal("b")), Ev(8, K.Item, Item("c1", id: "c", ord: 3)));

        var owner = new Dictionary<string, string> { ["a"] = "a2", ["c"] = "c1" };
        var fresh = new Replica();
        fresh.PullFrom(log, Conv);
        fresh.Pulled.Should().Contain(K.Item, "改回 full 之後舊的 item 類事件也拉得到（不加 purge_seq）");
        foreach (var replica in new[] { live, stopped, fresh })
        {
            replica.PullFrom(log, Conv);
            replica.Items.Should().Equal(owner);
        }
        Rows(host, $"SELECT item_id, removed FROM items WHERE conversation_id = '{Conv}' ORDER BY item_id").Select(r => ((string)r[0]!, (long)r[1]!))
            .Should().Equal(("a", 0L), ("c", 0L));
    }

    [Fact]
    public async Task 游標停在最後一筆回傳的事件_more照實()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var log = host.Services.GetRequiredService<EventLog>();
        // 依 seq：x1（看得到）、y1、y2（之後降級，看不到）、x2（看得到）、y3（看不到）
        var outcome = await Append(host, a, Ev(1, K.Created, Meta(), conversationId: OtherConv), Ev(2, K.Created, Meta()), Ev(3, K.Item, Item(id: "x1"), conversationId: OtherConv),
            Ev(4, K.Item, Item(id: "y1")), Ev(5, K.Item, Item(id: "y2")), Ev(6, K.Item, Item(id: "x2"), conversationId: OtherConv), Ev(7, K.Item, Item(id: "y3")));
        await Append(host, a, Ev(8, K.Meta, Meta(policy: P.Paused)));
        var x1 = outcome.Ack.Results[2].Seq!.Value;
        var x2 = outcome.Ack.Results[5].Seq!.Value;

        var first = log.Pull(Cls(SyncClasses.Item, maxEvents: 1));
        first.Events.Select(e => e.Seq).Should().Equal([x1]);
        first.More.Should().BeTrue("x2 還沒拉");
        var second = log.Pull(Cls(SyncClasses.Item, after: x1, maxEvents: 1));
        second.Events.Select(e => e.Seq).Should().Equal([x2], "跳過被過濾的 y1、y2，不打轉");
        second.More.Should().BeFalse("最後的 y3 被過濾：more 照實是 false");
        var third = log.Pull(Cls(SyncClasses.Item, after: x2));
        third.Events.Should().BeEmpty("下一次拉取不重複回事件");
        third.More.Should().BeFalse();
    }

    [Fact]
    public async Task 第3階段類別的Pull不受對話過濾影響()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var log = host.Services.GetRequiredService<EventLog>();
        var baseline = log.Pull(Cls(SyncClasses.Catalog)).Events.Select(e => e.Seq).ToList();
        await Append(host, a, Ev(1, K.Created, Meta()), Ev(2, K.Item, Item()), Ev(3, K.Meta, Meta(policy: P.Metadata)), Ev(4, K.Created, Meta(), conversationId: OtherConv),
            Ev(5, K.Deleted, new JObject(), conversationId: OtherConv));
        await EventLogTests.AppendAsync(host, a, EventLogTests.Batch(EventLogTests.Ev(6, SyncEventKinds.LinkUpsert, EventLogTests.Link("acc-1", "claude:org1:a@example.com"))));

        var catalog = log.Pull(Cls(SyncClasses.Catalog)).Events;

        catalog.Select(e => e.Seq).Should().StartWith(baseline);
        catalog.Should().Contain(e => e.Kind == SyncEventKinds.LinkUpsert).And.OnlyContain(e => e.Cls == SyncClasses.Catalog && e.ConversationId == null);
    }

    [Fact]
    public async Task Pull的結果與Hidden的判斷一致()
    {
        // SQL 的過濾與 ConversationPullRules.Hidden 是同一個判斷：四種狀態的對話 × 兩種類別
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var log = host.Services.GetRequiredService<EventLog>();
        var ids = new[] { new string('1', 32), new string('2', 32), new string('3', 32), new string('4', 32) };
        var events = new List<SyncEvent>();
        foreach (var id in ids)
        {
            events.Add(Ev(events.Count + 1, K.Created, Meta(), conversationId: id));
            events.Add(Ev(events.Count + 1, K.Item, Item(), conversationId: id));
            events.Add(Ev(events.Count + 1, K.Queue, Queue("甲"), conversationId: id));
        }
        events.Add(Ev(events.Count + 1, K.Meta, Meta(policy: P.Metadata), conversationId: ids[1]));
        events.Add(Ev(events.Count + 1, K.Meta, Meta(policy: P.Paused), conversationId: ids[2]));
        events.Add(Ev(events.Count + 1, K.Deleted, new JObject(), conversationId: ids[3]));
        (await Append(host, a, [.. events])).Ack.Results.Should().OnlyContain(r => r.Seq != null);
        var state = Rows(host, "SELECT conversation_id, sync_policy, deleted_at IS NOT NULL FROM conversations")
            .ToDictionary(r => (string)r[0]!, r => ((string)r[1]!, (long)r[2]! == 1));

        var all = Rows(host, "SELECT seq, cls, conversation_id FROM events WHERE conversation_id IS NOT NULL ORDER BY seq");
        var expected = all.Where(r => !ConversationPullRules.Hidden((string)r[1]!, state[(string)r[2]!].Item1, state[(string)r[2]!].Item2)).Select(r => (long)r[0]!).ToList();

        var pulled = log.Pull(Cls(SyncClasses.Item)).Events.Concat(log.Pull(Cls(SyncClasses.Meta)).Events).Select(e => e.Seq).Order().ToList();
        pulled.Should().Equal(expected);
        expected.Count.Should().BeLessThan(all.Count, "有被過濾的");
    }

    [Theory]
    [InlineData(SyncClasses.Item, P.Full, false, false)]
    [InlineData(SyncClasses.Item, P.Metadata, false, true)]
    [InlineData(SyncClasses.Item, P.Paused, false, true)]
    [InlineData(SyncClasses.Item, P.LocalOnly, false, true)]
    [InlineData(SyncClasses.Item, "bogus", false, true)]
    [InlineData(SyncClasses.Item, P.Full, true, true)]
    [InlineData(SyncClasses.Item, null, false, false)]
    [InlineData(SyncClasses.Meta, P.Metadata, false, false)]
    [InlineData(SyncClasses.Meta, P.Full, true, false)]
    [InlineData(SyncClasses.Catalog, P.Paused, true, false)]
    [InlineData(SyncClasses.Usage, null, false, false)]
    public void Hidden_只有item類而且已刪除或不是full(string cls, string? policy, bool deleted, bool hidden) =>
        ConversationPullRules.Hidden(cls, policy, deleted).Should().Be(hidden);

    // ───── 工具 ─────

    private static PullRequest Stream(string conversationId, long after = 0) =>
        new([new PullCursor(after, Stream: SyncStreams.Conversation(conversationId))], 100, 1_000_000);

    private static PullRequest Cls(string cls, long after = 0, int maxEvents = 100) => new([new PullCursor(after, Cls: cls)], maxEvents, 1_000_000);

    /// <summary>
    /// 照 WP4.3 的副本規則依 seq 套用拉到的事件（P10）：meta 的政策不是 full 就丟掉項目、conv.deleted 全部丟；item upsert、itemRemoved 移除
    /// </summary>
    private sealed class Replica
    {
        private long after;

        public Dictionary<string, string> Items { get; } = new(StringComparer.Ordinal);

        public List<string> Pulled { get; } = [];

        public void PullFrom(EventLog log, string conversationId)
        {
            while (true)
            {
                var page = log.Pull(Stream(conversationId, after));
                foreach (var e in page.Events)
                {
                    Pulled.Add(e.Kind);
                    var payload = JObject.Parse(Decode(e.Enc, e.Payload));
                    switch (e.Kind)
                    {
                        case K.Created or K.Meta when (string?)payload["syncPolicy"] != P.Full:
                        case K.Deleted:
                            Items.Clear();
                            break;
                        case K.Item:
                            Items[(string)payload["id"]!] = (string)payload["text"]!;
                            break;
                        case K.ItemRemoved:
                            Items.Remove((string)payload["id"]!);
                            break;
                    }
                    after = e.Seq;
                }
                if (!page.More)
                    return;
            }
        }
    }
}
