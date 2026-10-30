using System.Text.RegularExpressions;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Rendering;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 閘道的對話訊息與瀏覽器指令（WP4.2 計畫步 10；R4、R21～R29，D13、D22～D25、D28、D30、D38、D39、D42、K4）：以 <see cref="ConversationHarness"/>
/// （真的主機＋測試驅動的閘道）逐輪驗證；端到端的一則以真的主機、瀏覽器 hub 與 EventLog 驗證
/// </summary>
public sealed class BrowserConversationTests
{
    private static readonly string C1 = ConversationHarness.ConversationId(1);

    private static readonly string C2 = ConversationHarness.ConversationId(2);

    private static readonly string C3 = ConversationHarness.ConversationId(3);

    // ───── 零台發布裝置 ─────

    [Fact]
    public async Task 沒有對話時_init的conversations空_open不存在的id回invalid並送conversationClosed()
    {
        await using var h = await ConversationHarness.StartAsync();
        var (a, init) = await h.ReadyAsync("b1");
        init["conversations"].Should().BeOfType<JArray>().Which.Should().BeEmpty();

        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().Be(BrowserErrorCodes.Invalid);
        var closed = await a.NextAsync();
        closed.ShouldBeJson(($$"""{"type":"conversationClosed","conversationId":"{{C1}}"}"""));
        await h.RoundAsync();
        await a.AssertNothingAsync("沒有對話：清單沒變不重送，也沒有別的");
        a.Connection.OpenConversationIds().Should().BeEmpty();
    }

    // ───── 清單（D22） ─────

    [Fact]
    public async Task init帶清單_group依連線時區()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync("辦公室電腦");
        var (midnight, noon) = Zones(h.Time.GetUtcNow());
        await h.CreateAsync(device, C1, "第一個", updatedAt: h.Time.NowMs - 60_000);

        var a = ReadyWithState(h, "b1", midnight);
        var b = ReadyWithState(h, "b2", noon);
        await h.RunAsync();

        var listA = (await a.NextAsync())["conversations"]!.Should().BeOfType<JArray>().Subject;
        var summary = listA.Should().ContainSingle().Subject;
        summary["id"]!.Value<string>().Should().Be(C1);
        summary["title"]!.Value<string>().Should().Be("第一個");
        summary["group"]!.Value<string>().Should().Be("昨天", "這條連線的當地時間剛過午夜");
        summary["deviceId"]!.Value<string>().Should().Be(device);
        summary["deviceName"]!.Value<string>().Should().Be("辦公室電腦");
        summary["deviceOnline"]!.Value<bool>().Should().BeFalse();
        (await b.NextAsync())["conversations"]![0]!["group"]!.Value<string>().Should().Be("今天", "這條連線的當地時間是中午");
    }

    [Fact]
    public async Task ClientState換時區_重送清單()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var (midnight, noon) = Zones(h.Time.GetUtcNow());
        await h.CreateAsync(device, C1, updatedAt: h.Time.NowMs - 60_000);
        var a = ReadyWithState(h, "b1", noon);
        await h.RunAsync();
        (await a.NextAsync())["conversations"]![0]!["group"]!.Value<string>().Should().Be("今天");

        h.SetState(a, true, [], midnight);
        await h.RunAsync();
        var list = await a.NextAsync();
        list["type"]!.Value<string>().Should().Be("conversations");
        list["items"]![0]!["group"]!.Value<string>().Should().Be("昨天");

        h.SetState(a, true, [], noon);
        await h.RunAsync();
        (await a.NextAsync())["items"]![0]!["group"]!.Value<string>().Should().Be("今天");

        // 換到分組一樣的時區：只更新標記、不送
        var nearNoon = noon + 60 <= 840 ? noon + 60 : noon - 60;
        h.SetState(a, true, [], nearNoon);
        await h.RunAsync();
        await a.AssertNothingAsync("分組沒變");
        a.Connection.ConversationList!.TzOffsetMinutes.Should().Be(nearNoon);
    }

    [Fact]
    public async Task 跨當地午夜_重送一次()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var (_, noon) = Zones(h.Time.GetUtcNow());
        await h.CreateAsync(device, C1, updatedAt: h.Time.NowMs - 60_000);
        var a = ReadyWithState(h, "b1", noon);
        var wait = await h.RunAsync();
        (await a.NextAsync())["conversations"]![0]!["group"]!.Value<string>().Should().Be("今天");
        wait.Should().BeLessThanOrEqualTo(TimeSpan.FromHours(12), "迴圈到當地午夜要醒來重算");

        h.Time.Advance(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
        await h.RunAsync();
        (await a.NextAsync())["items"]![0]!["group"]!.Value<string>().Should().Be("昨天");
        await h.RunAsync();
        await h.RoundAsync();
        await a.AssertNothingAsync("只重送一次");
    }

    [Fact]
    public async Task 清單內容沒變_不重送()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        var (a, _) = await h.ReadyAsync("b1");

        await h.ItemAsync(device, C1, ConversationSamples.Item("新的一則", id: "item2", ord: 2));
        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta(), C1));
        await h.RoundAsync();
        await a.AssertNothingAsync("項目不影響清單；meta 的內容一樣，雜湊沒變");

        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta("改名"), C1));
        await h.RoundAsync();
        var list = await a.NextAsync();
        list["type"]!.Value<string>().Should().Be("conversations");
        list["items"]![0]!["title"]!.Value<string>().Should().Be("改名");
    }

    [Fact]
    public async Task 沒有瀏覽器連線時_取走並丟掉變更通知_之後的init帶最新清單()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1, "舊標題");
        var (_, init) = await h.ReadyAsync("b1");
        init["conversations"]![0]!["title"]!.Value<string>().Should().Be("舊標題");
        h.Connections.Remove("b1").Should().NotBeNull();

        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta("新標題"), C1));
        h.Feed.Pending.Should().BeGreaterThan(0);
        await h.RunAsync();
        h.Feed.Pending.Should().Be(0, "沒有人開著任何對話：取走丟掉，不留著佔記憶體（最多 ChangeFeedMaxBytes）");

        var (_, again) = await h.ReadyAsync("b2");
        again["conversations"]![0]!["title"]!.Value<string>().Should().Be("新標題", "丟掉變更時清單的快取一併作廢");
    }

    // ───── 開啟與分頁（D20、D38） ─────

    [Fact]
    public async Task open_最後80筆_hasMore_paneId是null_draft是null_帶composer()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync("家裡的電腦");
        await h.SeedAsync(device, C1, 85, "八十五則");
        await h.PublishAsync(device, (K.Queue, ConversationSamples.Queue("排第一", "排第二"), C1));
        var (a, _) = await h.ReadyAsync("b1");

        var conversation = await h.OpenAsync(a, C1);

        conversation["conversationId"]!.Value<string>().Should().Be(C1);
        conversation["paneId"]!.Type.Should().Be(JTokenType.Null);
        conversation["draft"]!.Type.Should().Be(JTokenType.Null);
        conversation["hasMore"]!.Value<bool>().Should().BeTrue();
        var items = conversation["items"]!.Select(i => i["id"]!.Value<string>()).ToList();
        items.Should().HaveCount(80);
        (items[0], items[^1]).Should().Be(("item6", "item85"));
        conversation["items"]!.Select(i => i["seq"]!.Value<long>()).Should().BeInAscendingOrder("seq 是 ord");
        var header = conversation["header"]!;
        header["title"]!.Value<string>().Should().Be("八十五則");
        header["deviceName"]!.Value<string>().Should().Be("家裡的電腦");
        header["status"]!.Value<string>().Should().Be(ConversationViewRules.OfflineStatus);
        conversation["composer"]!["queue"]!.Select(q => q["text"]!.Value<string>()).Should().Equal("排第一", "排第二");
        a.Connection.TryGetOpenConversation(C1, out var snapshot).Should().BeTrue();
        snapshot.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task loadOlder_beforeId之前80筆()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 170);
        var (a, _) = await h.ReadyAsync("b1");
        (await h.OpenAsync(a, C1))["items"]![0]!["id"]!.Value<string>().Should().Be("item91");

        (await h.CmdAsync(a, "loadOlder", new { conversationId = C1, beforeId = "item91" })).Should().BeNull();
        await h.RoundAsync();
        var older = await a.NextAsync();
        older["type"]!.Value<string>().Should().Be("olderItems");
        older["conversationId"]!.Value<string>().Should().Be(C1);
        older["beforeId"]!.Value<string>().Should().Be("item91", "原樣送回請求的 beforeId（合併前審查 LIVE-2）");
        older["items"]!.Select(i => i["id"]!.Value<string>()).Should().Equal(Enumerable.Range(11, 80).Select(i => "item" + i));
        older["hasMore"]!.Value<bool>().Should().BeTrue();

        (await h.CmdAsync(a, "loadOlder", new { conversationId = C1, beforeId = "item11" })).Should().BeNull();
        await h.RoundAsync();
        var first = await a.NextAsync();
        first["beforeId"]!.Value<string>().Should().Be("item11");
        first["items"]!.Select(i => i["id"]!.Value<string>()).Should().Equal(Enumerable.Range(1, 10).Select(i => "item" + i));
        first["hasMore"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task loadOlder_找不到beforeId回最後一頁()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 90);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        (await h.CmdAsync(a, "loadOlder", new { conversationId = C1, beforeId = "nope" })).Should().BeNull();
        await h.RoundAsync();
        var older = await a.NextAsync();
        older["items"]!.Select(i => i["id"]!.Value<string>()).Should().Equal(Enumerable.Range(11, 80).Select(i => "item" + i));
        older["hasMore"]!.Value<bool>().Should().BeTrue();

        (await h.CmdAsync(a, "loadOlder", new { conversationId = C1 })).Should().Be(BrowserErrorCodes.Invalid, "beforeId 是必填的項目 id");
        (await h.CmdAsync(a, "loadOlder", new { conversationId = C2, beforeId = "item1" })).Should().Be(BrowserErrorCodes.Invalid, "對話不存在");
        (await a.NextAsync())["type"]!.Value<string>().Should().Be("conversationClosed");
    }

    [Fact]
    public async Task open不存在的對話_invalid並送conversationClosed()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        await h.SeedAsync(device, C2, 1);
        await h.PublishAsync(device, (K.Deleted, new JObject(), C2));
        var (a, _) = await h.ReadyAsync("b1");

        foreach (var missing in new[] { ConversationHarness.ConversationId(9), C2 })
        {
            (await h.CmdAsync(a, "open", new { conversationId = missing })).Should().Be(BrowserErrorCodes.Invalid, "不存在、已刪除");
            (await a.NextAsync()).ShouldBeJson(($$"""{"type":"conversationClosed","conversationId":"{{missing}}"}"""));
        }
        // 不是對話 id 形狀的、close 與沒帶清單的 visible：照第 3 階段接受、不處理
        (await h.CmdAsync(a, "open", new { conversationId = "c1" })).Should().BeNull();
        (await h.CmdAsync(a, "close", new { conversationId = "c1" })).Should().BeNull();
        (await h.CmdAsync(a, "visible", new { visible = true })).Should().BeNull();
        (await h.CmdAsync(a, "loadOlder", new { conversationId = "c1" })).Should().BeNull();
        await h.RoundAsync();
        await a.AssertNothingAsync();
    }

    [Fact]
    public async Task open與提交同時發生_最後狀態等於持久最新()
    {
        // 網頁端照收到的順序套用：conversation 整份取代、item 整筆取代（以 id）；連續提交同一個項目的同時 open，最後一定是持久最新的版本
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        long version = 1;
        for (var round = 0; round < 50; round++)
        {
            var (tab, _) = await h.ReadyAsync("r" + round);
            var first = version + 1;
            var writer = Task.Run(async () =>
            {
                for (var v = first; v < first + 200; v++)
                    await h.ItemAsync(device, C1, ConversationSamples.Item("v" + v, id: "item1", ord: 1, ver: v));
            });
            (await h.CmdAsync(tab, "open", new { conversationId = C1 })).Should().BeNull();
            while (!writer.IsCompleted)
                await h.RoundAsync();
            await writer;
            version = first + 199;
            for (var i = 0; i < 3; i++)
                await h.RoundAsync();

            string? html = null;
            foreach (var message in await tab.TakeAsync(0))
            {
                switch (message["type"]!.Value<string>())
                {
                    case "conversation":
                        html = message["items"]!.Single(i => i["id"]!.Value<string>() == "item1")["html"]!.Value<string>();
                        break;
                    case "item" when html is not null:
                        html = message["item"]!["html"]!.Value<string>();
                        break;
                }
            }
            html.Should().Contain(">v" + version + "<", $"第 {round} 輪的最後狀態是持久最新的版本");
            (await h.CmdAsync(tab, "close", new { conversationId = C1 })).Should().BeNull();
            h.Connections.Remove(tab.Connection.ConnectionId);
        }
    }

    [Fact]
    public async Task 每一輪最多處理8個open()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var ids = Enumerable.Range(1, 10).Select(ConversationHarness.ConversationId).ToList();
        foreach (var id in ids)
            await h.SeedAsync(device, id, 1);
        var (a, _) = await h.ReadyAsync("b1");

        foreach (var id in ids)
            (await h.CmdAsync(a, "open", new { conversationId = id })).Should().BeNull();
        await h.RoundAsync();
        var firstRound = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "conversation").ToList();
        firstRound.Should().HaveCount(8);
        a.Connection.PendingConversationRequests.Should().Be(2);
        await h.RoundAsync();
        var secondRound = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "conversation").ToList();
        firstRound.Concat(secondRound).Select(m => m["conversationId"]!.Value<string>()).Should().Equal(ids, "依到達順序");
    }

    [Fact]
    public async Task 待處理的請求排滿了_回rate_limited_同樣的請求不重複記()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        var (a, _) = await h.ReadyAsync("b1");

        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().BeNull();
        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().BeNull();
        a.Connection.PendingConversationRequests.Should().Be(1, "同樣的請求已經在等");
        for (var i = 1; i < BrowserConnection.MaxPendingConversationRequests; i++)
            (await h.CmdAsync(a, "loadOlder", new { conversationId = C1, beforeId = "item" + i })).Should().BeNull();
        (await h.CmdAsync(a, "loadOlder", new { conversationId = C1, beforeId = "item999" })).Should().Be(BrowserErrorCodes.RateLimited);
        a.Connection.PendingConversationRequests.Should().Be(BrowserConnection.MaxPendingConversationRequests);
    }

    [Fact]
    public async Task 渲染快取_不超過上限_重開不變()
    {
        const int max = 12_000;
        await using var h = await ConversationHarness.StartAsync(o => o.Conversations.RenderCacheMaxBytes = max);
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 40, text: i => i + new string('長', 600));
        var (a, _) = await h.ReadyAsync("b1");

        var first = await h.OpenAsync(a, C1);
        first["items"]!.Should().HaveCount(40);
        h.Views.RenderCacheBytes.Should().BeInRange(1, max, "LRU：超過上限就丟最久沒用的");
        h.Views.RenderCacheCount.Should().BeInRange(1, 39, "40 筆放不下");

        (await h.CmdAsync(a, "close", new { conversationId = C1 })).Should().BeNull();
        var again = await h.OpenAsync(a, C1);
        again["items"]!.ToString().Should().Be(first["items"]!.ToString(), "同一個版本渲染的結果相同（快取命中或重新渲染都一樣）");
        h.Views.RenderCacheBytes.Should().BeLessThanOrEqualTo(max);
    }

    // ───── 增量 ─────

    [Fact]
    public async Task item只給開著的連線_close之後不再送()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");
        await h.OpenAsync(a, C1);

        await h.ItemAsync(device, C1, ConversationSamples.Item("第二則", id: "item2", ord: 2));
        await h.RoundAsync();
        var item = await a.NextAsync();
        item["type"]!.Value<string>().Should().Be("item");
        item["conversationId"]!.Value<string>().Should().Be(C1);
        item["item"]!["id"]!.Value<string>().Should().Be("item2");
        item["item"]!["html"]!.Value<string>().Should().Contain("第二則");
        await b.AssertNothingAsync("沒開這個對話");

        (await h.CmdAsync(a, "close", new { conversationId = C1 })).Should().BeNull();
        await h.ItemAsync(device, C1, ConversationSamples.Item("第三則", id: "item3", ord: 3));
        await h.PublishAsync(device, (K.ItemRemoved, ConversationEventLogTests.Removal("item2"), C1), (K.Queue, ConversationSamples.Queue("排"), C1));
        await h.RoundAsync();
        await a.AssertNothingAsync("close 之後不再送");
    }

    [Fact]
    public async Task 同一筆50ms內改三次_只送最後一版()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        for (var v = 2; v <= 4; v++)
            await h.ItemAsync(device, C1, ConversationSamples.Item("版本" + v, id: "item1", ord: 1, ver: v));
        h.Time.Advance(TimeSpan.FromMilliseconds(10));
        await h.RunAsync();
        await a.AssertNothingAsync("距離上一輪不到 50 ms");
        h.Time.Advance(TimeSpan.FromMilliseconds(40));
        await h.RunAsync();
        var items = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "item").ToList();
        items.Should().ContainSingle().Which["item"]!["html"]!.Value<string>().Should().Contain("版本4");
    }

    [Fact]
    public async Task 快照之前的變更晚到_不推給開著的連線_比快照新的照推()
    {
        // D38：變更在提交之後才放進通知，可能比 open 的快照晚到；seq 不比快照新的已經在快照裡，再推會讓畫面倒回舊版。
        // 以直接放進通知的變更模擬晚到（快照的交易看到了它、閘道這一輪才取到）
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 2);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        a.Connection.TryGetOpenConversation(C1, out var snapshot).Should().BeTrue();

        h.Feed.Post(new ConversationChange(C1, ConversationChangeKind.Item, snapshot, device, ItemId: "item1", Ord: 1,
            Body: ConversationSamples.Item("快照之前的舊版", id: "item1", ord: 1).ToString(Newtonsoft.Json.Formatting.None)));
        h.Feed.Post(new ConversationChange(C1, ConversationChangeKind.Meta, snapshot, device));
        h.Feed.Post(new ConversationChange(C1, ConversationChangeKind.Queue, snapshot, device));
        await h.RoundAsync();
        await a.AssertNothingAsync("項目、標頭、輸入區都不比快照新");

        // 同一輪裡晚到的舊變更與比快照新的變更：只推新的
        h.Feed.Post(new ConversationChange(C1, ConversationChangeKind.Item, snapshot, device, ItemId: "item1", Ord: 1,
            Body: ConversationSamples.Item("快照之前的舊版", id: "item1", ord: 1).ToString(Newtonsoft.Json.Formatting.None)));
        h.Feed.Post(new ConversationChange(C1, ConversationChangeKind.Item, snapshot + 1, device, ItemId: "item2", Ord: 2,
            Body: ConversationSamples.Item("快照之後的", id: "item2", ord: 2).ToString(Newtonsoft.Json.Formatting.None)));
        h.Feed.Post(new ConversationChange(C1, ConversationChangeKind.Meta, snapshot + 1, device));
        h.Feed.Post(new ConversationChange(C1, ConversationChangeKind.Queue, snapshot + 1, device));
        await h.RoundAsync();
        var messages = await a.TakeAsync(3);
        messages.Select(m => m["type"]!.Value<string>()).Should().Equal("header", "item", "composer");
        messages[1]["item"]!["id"]!.Value<string>().Should().Be("item2");
        messages[1]["item"]!["html"]!.Value<string>().Should().Contain("快照之後的");
        await a.AssertNothingAsync("item1 的舊版不推");
    }

    [Fact]
    public async Task 政策降級的通知晚到_快照比清除新的連線不必resync()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        a.Connection.TryGetOpenConversation(C1, out var snapshot).Should().BeTrue();

        // 快照讀到的已經是清除之後的內容
        h.Feed.Post(new ConversationChange(C1, ConversationChangeKind.Purged, snapshot, device));
        await h.RoundAsync();
        await a.AssertNothingAsync("清除不比快照新");
        a.Connection.TryGetOpenConversation(C1, out _).Should().BeTrue();

        h.Feed.Post(new ConversationChange(C1, ConversationChangeKind.Purged, snapshot + 1, device));
        await h.RoundAsync();
        (await a.NextAsync()).ShouldBeJson(($$"""{"type":"resync","conversationId":"{{C1}}"}"""));
        a.Connection.TryGetOpenConversation(C1, out _).Should().BeFalse();
    }

    [Fact]
    public async Task 標題改了_開著的連線收到新的header與清單()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1, "原本的標題");
        var (a, _) = await h.ReadyAsync("b1");
        (await h.OpenAsync(a, C1))["header"]!["title"]!.Value<string>().Should().Be("原本的標題");

        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta("新的標題"), C1));
        await h.RoundAsync();
        var messages = await a.TakeAsync(2);
        messages.Single(m => m["type"]!.Value<string>() == "header")["header"]!["title"]!.Value<string>().Should().Be("新的標題", "標頭的快取跟著作廢");
        messages.Single(m => m["type"]!.Value<string>() == "conversations")["items"]![0]!["title"]!.Value<string>().Should().Be("新的標題");
    }

    [Fact]
    public async Task 增量的項目沒有createdAt_時間是伺服器收到的時間_同open()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        var (a, _) = await h.ReadyAsync("b1");
        var first = EventLogTests.Count(h.Host, "SELECT MAX(created_at) FROM events WHERE kind = 'conv.item'");
        (await h.OpenAsync(a, C1))["items"]![0]!["time"]!.Value<long>().Should().Be(first, "open 讀 items：收到的時間是最後一筆事件的 created_at");

        h.AdvanceClocks(TimeSpan.FromMinutes(5));
        await h.ItemAsync(device, C1, ConversationSamples.Item("後來的一則", id: "item2", ord: 2));
        var received = EventLogTests.Count(h.Host, "SELECT MAX(created_at) FROM events WHERE kind = 'conv.item'");
        received.Should().Be(first + 300_000);
        await h.RoundAsync();
        var pushed = await a.NextAsync();
        pushed["type"]!.Value<string>().Should().Be("item");
        pushed["item"]!["time"]!.Value<long>().Should().Be(received, "以變更附的 body 渲染時同樣用收到的時間");
    }

    [Fact]
    public async Task 刪除_開著的連線收到conversationClosed_清單少一筆()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 2);
        await h.SeedAsync(device, C2, 1);
        var (a, init) = await h.ReadyAsync("b1");
        init["conversations"]!.Should().HaveCount(2);
        await h.OpenAsync(a, C1);

        await h.PublishAsync(device, (K.Deleted, new JObject(), C1));
        await h.RoundAsync();
        var messages = await a.TakeAsync(2);
        messages[0].ShouldBeJson(($$"""{"type":"conversationClosed","conversationId":"{{C1}}"}"""));
        messages[1]["type"]!.Value<string>().Should().Be("conversations");
        messages[1]["items"]!.Select(i => i["id"]!.Value<string>()).Should().Equal(C2);
        a.Connection.TryGetOpenConversation(C1, out _).Should().BeFalse();
    }

    [Fact]
    public async Task 政策降級_開著的連線收到resync_重開後只剩標題與狀態()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 3, "要降級的");
        var (a, _) = await h.ReadyAsync("b1");
        (await h.OpenAsync(a, C1))["items"]!.Should().HaveCount(3);

        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta("要降級的", ConversationSyncPolicies.Metadata), C1));
        await h.RoundAsync();
        var messages = await a.TakeAsync(0);
        messages.Select(m => m["type"]!.Value<string>()).Should().Equal("resync");
        messages[0]["conversationId"]!.Value<string>().Should().Be(C1);
        a.Connection.TryGetOpenConversation(C1, out _).Should().BeFalse("網頁重新 open 之前不推增量");

        var reopened = await h.OpenAsync(a, C1);
        reopened["items"]!.Should().BeEmpty();
        reopened["hasMore"]!.Value<bool>().Should().BeFalse();
        reopened["header"]!["title"]!.Value<string>().Should().Be("要降級的");
    }

    [Fact]
    public async Task 變更通知溢出_整份重送()
    {
        await using var h = await ConversationHarness.StartAsync(settings: Settings(("Hub:Conversations:ChangeFeedMaxEntries", "3")));
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        // 一批 6 筆：前 3 筆的變更進得去，後面（含改名）因為通知滿了被丟掉
        await h.PublishAsync(device,
            (K.Item, ConversationSamples.Item("2", id: "item2", ord: 2), C1), (K.Item, ConversationSamples.Item("3", id: "item3", ord: 3), C1),
            (K.Item, ConversationSamples.Item("4", id: "item4", ord: 4), C1), (K.Item, ConversationSamples.Item("5", id: "item5", ord: 5), C1),
            (K.Meta, ConversationSamples.Meta("改名"), C1));
        await h.RoundAsync();
        var messages = await a.TakeAsync(0);
        var snapshot = messages.Last(m => m["type"]!.Value<string>() == "conversation");
        snapshot["items"]!.Select(i => i["id"]!.Value<string>()).Should().Equal("item1", "item2", "item3", "item4", "item5");
        snapshot["header"]!["title"]!.Value<string>().Should().Be("改名");
        messages.Single(m => m["type"]!.Value<string>() == "conversations")["items"]![0]!["title"]!.Value<string>().Should().Be("改名");
    }

    // ───── 上線狀態（D39） ─────

    [Fact]
    public async Task 擁有者斷線_開著的連線收到header的status是電腦離線_清單的deviceOnline變false()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        var online = h.Online(device);
        var (a, init) = await h.ReadyAsync("b1");
        init["conversations"]![0]!["deviceOnline"]!.Value<bool>().Should().BeTrue();
        var header = (await h.OpenAsync(a, C1))["header"]!;
        header["deviceOnline"]!.Value<bool>().Should().BeTrue();
        header["status"]!.Type.Should().Be(JTokenType.Null);

        h.Offline(online);
        await h.RoundAsync();
        var messages = await a.TakeAsync(0);
        var pushed = messages.Single(m => m["type"]!.Value<string>() == "header");
        pushed["header"]!["status"]!.Value<string>().Should().Be(ConversationViewRules.OfflineStatus);
        pushed["header"]!["deviceOnline"]!.Value<bool>().Should().BeFalse();
        messages.Should().Contain(m => m["type"]!.Value<string>() == "composer", "D39：標頭與輸入區一起重送");
        messages.Single(m => m["type"]!.Value<string>() == "conversations")["items"]![0]!["deviceOnline"]!.Value<bool>().Should().BeFalse();

        h.Online(device);
        await h.RoundAsync();
        (await a.TakeAsync(0)).Single(m => m["type"]!.Value<string>() == "header")["header"]!["deviceOnline"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task 裝置改名_開著的連線重送header_清單重讀()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync("舊的名字");
        await h.SeedAsync(device, C1, 1);
        var (a, _) = await h.ReadyAsync("b1");
        (await h.OpenAsync(a, C1))["header"]!["deviceName"]!.Value<string>().Should().Be("舊的名字");

        var services = h.Host.Services;
        var catalog = services.GetRequiredService<Catalog>();
        (await services.GetRequiredService<MyriagentHub.Data.SyncWriter>().WriteAsync(context => catalog.RenameDevice(context, device, "新的名字")))
            .Rejected.Should().BeNull();
        // 測試閘道的帳戶與裝置快照來自假的來源：換掉 devices 那一部分，閘道讀到新版本（同正式的 AccountViewBuilder 讀到改名）
        h.Source.Current = h.Source.Current with { DevicesJson = """[{"renamed":true}]""" };
        h.Gateway.MarkDirty(BrowserParts.Devices);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.RunAsync();

        var messages = await a.TakeAsync(4);
        messages.Select(m => m["type"]!.Value<string>()).Should().Equal("devices", "header", "composer", "conversations");
        messages[1]["header"]!["deviceName"]!.Value<string>().Should().Be("新的名字");
        messages[3]["items"]![0]!["deviceName"]!.Value<string>().Should().Be("新的名字", "清單重讀（名稱在 device_streams）");
    }

    // ───── init（D42） ─────

    [Fact]
    public async Task 溢出重送init之後_不再推增量給沒有重新open的對話()
    {
        await using var h = await ConversationHarness.StartAsync(o => o.Browser.OutboxMaxMessages = 5);
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        // 送出卡住時排進 8 則 notify（還沒送 ClientState：看不到這個對話）而溢出。增量太多時改成重開、不會溢出（合併前審查 F1），所以用 notify
        a.Block();
        await h.PublishAsync(device, Enumerable.Range(0, 8).Select(_ => (K.Attention, (JToken)new JObject { ["kind"] = "completed" }, C1)).ToArray());
        (await h.RoundAsync()).Should().Be(TimeSpan.Zero, "溢出：立刻再跑一輪送 init");
        a.Connection.InitSent.Should().BeFalse();
        await h.RunAsync();
        a.Connection.InitSent.Should().BeTrue();
        a.Connection.OpenConversationIds().Should().BeEmpty("D42：init 清掉開著的對話");
        a.Release();

        await h.ItemAsync(device, C1, ConversationSamples.Item("init 之後", id: "item20", ord: 20));
        await h.RoundAsync();
        await h.RoundAsync();
        var messages = await a.TakeAsync(0);
        var afterInit = messages.SkipWhile(m => m["type"]!.Value<string>() != "init").ToList();
        afterInit.Should().NotBeEmpty();
        afterInit.Skip(1).Should().NotContain(m => m["type"]!.Value<string>() == "item", "網頁還沒重新 open");
        h.Host.Services.GetRequiredService<ConversationCounters>().Snapshot().OutboxOverflows.Should().Be(1);
    }

    // ───── 通知與剪貼簿（D28、D30） ─────

    [Fact]
    public async Task notify_給看不到的連線_看得到的不送_plan變approval_在init之後()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync("通知的電腦");
        await h.SeedAsync(device, C1, 1, "要核准的");
        var (seeing, _) = await h.ReadyAsync("seeing");
        h.SetState(seeing, true, [C1]);
        var (hidden, _) = await h.ReadyAsync("hidden");
        h.SetState(hidden, false, [C1]);
        var (shown, _) = await h.ReadyAsync("shown");
        h.SetState(shown, true, []);
        (await h.CmdAsync(shown, "visible", new { conversationIds = new[] { C1, "d01J9Q0000000000000000000AA" } })).Should().BeNull();
        var (other, _) = await h.ReadyAsync("other");
        h.SetState(other, true, [C2]);
        var waiting = h.Add("waiting");

        await h.PublishAsync(device, (K.Attention, new JObject { ["kind"] = "plan" }, C1));
        await h.RoundAsync();

        var expected = $$"""{"type":"notify","conversationId":"{{C1}}","kind":"approval","title":"要核准的","deviceName":"通知的電腦"}""";
        (await hidden.NextAsync()).ShouldBeJson(expected);
        AssertSpeak(await hidden.NextAsync(), "「要核准的」的計畫等你確認");
        (await other.NextAsync()).ShouldBeJson(expected);
        AssertSpeak(await other.NextAsync(), "「要核准的」的計畫等你確認");
        AssertSpeak(await seeing.NextAsync(), "「要核准的」的計畫等你確認");
        await seeing.AssertNothingAsync("openConversationIds 裡有它：不收 notify，語音照念（同桌面）");
        AssertSpeak(await shown.NextAsync(), "「要核准的」的計畫等你確認");
        await shown.AssertNothingAsync("visible 指令的清單裡有它");
        await waiting.AssertNothingAsync("還沒送過 init");

        h.Gateway.RequestInit(waiting.Connection, "main");
        await h.RunAsync();
        (await waiting.NextAsync())["type"]!.Value<string>().Should().Be("init");
        await waiting.AssertNothingAsync("notify 不會在 init 之前排進去，之後也不補");
    }

    /// <summary>下一則 notify 或 speak（跳過清單、標頭這些同一輪的其他更新）</summary>
    private static async Task<JObject> NextNoticeAsync(SendRecorder client)
    {
        while (true)
        {
            var message = await client.NextAsync();
            if (message["type"]!.Value<string>() is "notify" or "speak")
                return message;
        }
    }

    private static void AssertSpeak(JObject message, string text)
    {
        message["type"]!.Value<string>().Should().Be("speak");
        message["conversationId"]!.Value<string>().Should().Be(C1);
        message["id"]!.Type.Should().Be(JTokenType.Integer, "事件的 seq（分頁之間去重）");
        message["text"]!.Value<string>().Should().Be(text);
    }

    [Fact]
    public async Task speak_擁有者帶了句子就照念_空字串不念但notify照送_沒帶就組只有標題的句子()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync("通知的電腦");
        await h.SeedAsync(device, C1, 1, "整理報表");
        var (b1, _) = await h.ReadyAsync("b1");
        h.SetState(b1, false, []);

        await h.PublishAsync(device, (K.Attention, new JObject { ["kind"] = "completed", ["speech"] = "「整理報表」已完成：月報已經匯出成 Excel" }, C1));
        await h.RoundAsync();
        (await NextNoticeAsync(b1))["type"]!.Value<string>().Should().Be("notify");
        AssertSpeak(await NextNoticeAsync(b1), "「整理報表」已完成：月報已經匯出成 Excel");

        await h.PublishAsync(device, (K.Attention, new JObject { ["kind"] = "completed", ["speech"] = "" }, C1));
        await h.RoundAsync();
        (await NextNoticeAsync(b1))["type"]!.Value<string>().Should().Be("notify");
        await b1.AssertNothingAsync("空字串＝背景工作還在跑，不念");

        await h.PublishAsync(device, (K.Attention, new JObject { ["kind"] = "failed" }, C1));
        await h.RoundAsync();
        (await NextNoticeAsync(b1))["type"]!.Value<string>().Should().Be("notify");
        AssertSpeak(await NextNoticeAsync(b1), "「整理報表」執行失敗");
    }

    [Fact]
    public async Task copyItem_clipboard只給發起的連線()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        await h.ItemAsync(device, C1, ConversationSamples.Item("原文 **粗體**\n\n- 一\n- 二", id: "item1", ord: 1));
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");

        (await h.CmdAsync(a, "copyItem", new { conversationId = C1, itemId = "item1" })).Should().BeNull();
        (await a.NextAsync()).ShouldBeJson((
            $$"""{"type":"clipboard","conversationId":"{{C1}}","itemId":"item1","text":"原文 **粗體**\n\n- 一\n- 二"}"""));
        await b.AssertNothingAsync("只給發起的連線");
        a.Connection.OpenConversationIds().Should().BeEmpty("copyItem 不必先 open");
    }

    [Fact]
    public async Task copyItem_不是Assistant_invalid()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var user = ConversationSamples.Item("使用者的話", id: "user1", ord: 1);
        user["kind"] = "User";
        await h.ItemAsync(device, C1, user);
        var (a, _) = await h.ReadyAsync("b1");

        (await h.CmdAsync(a, "copyItem", new { conversationId = C1, itemId = "user1" })).Should().Be(BrowserErrorCodes.Invalid, "只收 Assistant");
        (await h.CmdAsync(a, "copyItem", new { conversationId = C1, itemId = "nope" })).Should().Be(BrowserErrorCodes.Invalid, "項目不存在");
        (await h.CmdAsync(a, "copyItem", new { conversationId = C2, itemId = "user1" })).Should().Be(BrowserErrorCodes.Invalid, "對話不存在");
        (await h.CmdAsync(a, "copyItem", new { conversationId = "c1", itemId = "user1" })).Should().Be(BrowserErrorCodes.Invalid, "不是對話 id");
        (await h.CmdAsync(a, "copyItem", new { conversationId = C1 })).Should().Be(BrowserErrorCodes.Invalid, "沒有 itemId");
        await a.AssertNothingAsync();
    }

    // ───── 端到端（真的主機、瀏覽器 hub、EventLog） ─────

    [Fact]
    public async Task 裝置發布到瀏覽器收到item_沒有path_沒有script_沒有on屬性()
    {
        var now = DateTimeOffset.UtcNow;
        await using var host = await HubTestHost.StartAsync(clock: new ManualTimeProvider(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero)));
        var device = await EventLogTests.AddDeviceAsync(host);
        var corpus = new[] { 0, 40, 80, 120, 160 }.Select(i => ItemSanitizerXssCorpus.All[i]).ToList();
        var events = new List<SyncEvent> { ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta()) };
        for (var i = 0; i < corpus.Count; i++)
            events.Add(ConversationEventLogTests.Ev(i + 2, K.Item, ConversationSamples.Item(corpus[i].Html + "\n\n[本機](C:/secret/a.txt)", id: "item" + (i + 1), ord: i + 1)));
        (await ConversationEventLogTests.Append(host, device, [.. events])).Ack.Results.Should().OnlyContain(r => r.Rejected == null);

        await using var owner = await LoginAndConnectAsync(host);
        var init = await owner.Hub.ReadyAsync();
        init["conversations"]!.Single()["id"]!.Value<string>().Should().Be(ConversationSamples.Conv);
        (await owner.Hub.CmdAsync(new { cmd = "open", conversationId = ConversationSamples.Conv }))["accepted"]!.Value<bool>().Should().BeTrue();
        var conversation = await owner.Hub.WaitForAsync("conversation");
        conversation["items"]!.Should().HaveCount(corpus.Count);
        foreach (var item in conversation["items"]!)
            AssertSafeItem((JObject)item, item["id"]!.Value<string>()!);

        (await ConversationEventLogTests.Append(host, device,
            ConversationEventLogTests.Ev(corpus.Count + 2, K.Item, ConversationSamples.Item(corpus[0].Html, id: "item9", ord: 9)))).Ack.Results.Should().OnlyContain(r => r.Rejected == null);
        var pushed = await owner.Hub.WaitForAsync("item");
        pushed["conversationId"]!.Value<string>().Should().Be(ConversationSamples.Conv);
        AssertSafeItem((JObject)pushed["item"]!, "item9");
    }

    // ───── 慢用戶端（K4） ─────

    [Fact]
    public async Task 慢用戶端溢出重送init_其他連線照常()
    {
        await using var h = await ConversationHarness.StartAsync(o => o.Browser.OutboxMaxMessages = 8);
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        var (slow, _) = await h.ReadyAsync("slow");
        var (fast, _) = await h.ReadyAsync("fast");
        await h.OpenAsync(fast, C1);
        h.SetState(fast, visible: true, [C1]);
        await h.RunAsync();
        await fast.TakeAsync(0);

        slow.Hang();
        // 每一輪 4 則項目與 4 則注意事件（佇列上限 8）：開著、看得到的 fast 每一輪收完 4 則 item（一半以內，不改成重開），不會因為送出幫浦慢一點就溢出；
        // 卡住的 slow 看不到這個對話、每一輪收 4 則 notify，第三輪溢出（增量太多時改成重開、不會溢出，合併前審查 F1，所以用 notify）
        for (var round = 0; round < 3; round++)
        {
            var ids = Enumerable.Range(2 + round * 4, 4).ToList();
            await h.PublishAsync(device, ids.Select(i => (K.Item, (JToken)ConversationSamples.Item("第 " + i + " 則", id: "item" + i, ord: i), C1))
                .Concat(Enumerable.Range(0, 4).Select(_ => (K.Attention, (JToken)new JObject { ["kind"] = "completed", ["speech"] = "" }, C1))).ToArray());   // 不念：只算 notify
            await h.RoundAsync();
            (await fast.TakeAsync(4)).Select(m => m["item"]!["id"]!.Value<string>()).Should().Equal(ids.Select(i => "item" + i), $"第 {round} 輪：其他連線照常收到");
        }
        await h.RunAsync();
        slow.Connection.InitSent.Should().BeTrue("溢出之後重送了 init");
        slow.Connection.OpenConversationIds().Should().BeEmpty();
        slow.Aborts.Should().Be(0, "第一次溢出是重送 init，不是中斷");
        fast.Connection.InitSent.Should().BeTrue();
        fast.Aborts.Should().Be(0);
        await fast.AssertNothingAsync();
        h.Host.Services.GetRequiredService<ConversationCounters>().Snapshot().OutboxOverflows.Should().Be(1, "只有卡住的那條溢出");
    }

    [Fact]
    public async Task 重開多個大對話不會反覆溢出()
    {
        // K4：送出佇列 400 KB、一頁最多 150 KB；四個大對話同時重開（init 之後網頁重新 open 畫面上的分頁），送出卡住時一輪最多排到上限一半
        await using var h = await ConversationHarness.StartAsync(o =>
        {
            o.Browser.OutboxMaxBytes = 400_000;
            o.Conversations.PageMaxBytes = 150_000;
        });
        var device = await h.AddDeviceAsync();
        var ids = Enumerable.Range(1, 4).Select(ConversationHarness.ConversationId).ToList();
        foreach (var id in ids)
            await h.SeedAsync(device, id, 40, text: i => i + " " + new string('長', 4000));
        var (a, _) = await h.ReadyAsync("b1");

        a.Block();
        foreach (var id in ids)
            (await h.CmdAsync(a, "open", new { conversationId = id })).Should().BeNull();
        for (var i = 0; i < 5; i++)
        {
            await h.RoundAsync();
            a.Connection.InitSent.Should().BeTrue($"第 {i} 輪沒有溢出");
            a.Connection.Outbox.Bytes.Should().BeLessThanOrEqualTo(400_000);
        }
        a.Connection.PendingConversationRequests.Should().BeGreaterThan(0, "送出卡住時留到下一輪");
        a.Release();

        var received = new List<string>();
        for (var i = 0; i < 50 && received.Count < ids.Count; i++)
        {
            await h.RoundAsync();
            received.AddRange((await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "conversation").Select(m => m["conversationId"]!.Value<string>()!));
        }
        received.Should().Equal(ids);
        h.Host.Services.GetRequiredService<ConversationCounters>().Snapshot().OutboxOverflows.Should().Be(0);
        a.Connection.InitSent.Should().BeTrue();
    }

    [Fact]
    public async Task 快照本身超過佇列上限一半_排進去會溢出的留到下一輪()
    {
        // 合併前審查 F2：K4 只看佇列有沒有超過上限的一半，但一份快照另有標頭、輸入區（佇列最多 50 則 × 600 字）與外框，本身就可能超過一半：
        // 佇列排著 B（不到一半）時 C 照樣讀、照樣入列 → 溢出 → 重送 init → 網頁依同樣順序重開 → 再溢出
        await using var h = await ConversationHarness.StartAsync(o =>
        {
            o.Browser.OutboxMaxBytes = 400_000;
            o.Conversations.PageMaxBytes = 200_000;
        });
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        await h.SeedAsync(device, C2, 35, text: i => i + " " + new string('長', 1500));
        await h.SeedAsync(device, C3, 60, text: i => i + " " + new string('長', 1500));
        await h.PublishAsync(device, (K.Queue, ConversationSamples.Queue([.. Enumerable.Range(0, 50).Select(i => i + new string('排', 590))]), C3));
        var sizeB = h.Views.Snapshot(C2).Message.Bytes;
        var sizeC = h.Views.Snapshot(C3).Message.Bytes;
        sizeB.Should().BeLessThanOrEqualTo(200_000, "前提：B 排著時佇列不到一半（K4 不擋 C）");
        (sizeB + sizeC).Should().BeGreaterThan(400_000, "前提：B 排著時 C 排進去會溢出");
        var (a, _) = await h.ReadyAsync("b1");

        // 送出卡住在 A 的快照上（已出列，不算在佇列裡）
        a.Block();
        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().BeNull();
        await h.RoundAsync();
        for (var waited = 0; waited < 5000 && a.Connection.Outbox.Count > 0; waited += 10)
            await Task.Delay(10);
        a.Connection.Outbox.Count.Should().Be(0, "前提：A 的快照已出列");
        (await h.CmdAsync(a, "open", new { conversationId = C2 })).Should().BeNull();
        (await h.CmdAsync(a, "open", new { conversationId = C3 })).Should().BeNull();
        for (var i = 0; i < 3; i++)
        {
            await h.RoundAsync();
            a.Connection.InitSent.Should().BeTrue($"第 {i} 輪沒有溢出");
            a.Connection.Outbox.Bytes.Should().BeLessThanOrEqualTo(400_000);
        }
        a.Connection.PendingConversationRequests.Should().Be(1, "C 留到佇列放得下");
        a.Release();

        var received = new List<string>();
        for (var i = 0; i < 50 && received.Count < 3; i++)
        {
            await h.RoundAsync();
            received.AddRange((await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "conversation").Select(m => m["conversationId"]!.Value<string>()!));
        }
        received.Should().Equal(C1, C2, C3);
        h.Host.Services.GetRequiredService<ConversationCounters>().Snapshot().OutboxOverflows.Should().Be(0);
    }

    [Fact]
    public async Task 一輪超過一頁的增量_改成重開_送一份新的快照()
    {
        // 合併前審查 F1：整份重新發布、改回 full、回補時開著的對話一輪就有幾百筆；逐筆渲染入列只會塞滿送出佇列（網頁也會丟掉比已載入第一筆早的）
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.SeedAsync(device, C1, 1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        var rendered = h.Views.RenderCacheCount;

        for (var start = 2; start <= 201; start += 20)
            await h.PublishAsync(device, Enumerable.Range(start, 20).Select(i => (K.Item, (JToken)ConversationSamples.Item("第 " + i + " 則", id: "item" + i, ord: i), C1)).ToArray());
        await h.RoundAsync();

        var messages = await a.TakeAsync(0);
        messages.Select(m => m["type"]!.Value<string>()).Should().NotContain("item").And.Contain("conversation");
        var snapshot = messages.Last(m => m["type"]!.Value<string>() == "conversation");
        snapshot["items"]!.Select(i => i["id"]!.Value<string>()).Should().Equal(Enumerable.Range(122, 80).Select(i => "item" + i), "最後一頁");
        snapshot["hasMore"]!.Value<bool>().Should().BeTrue();
        (h.Views.RenderCacheCount - rendered).Should().BeLessThanOrEqualTo(ConversationViewRules.PageItems, "只渲染快照的一頁，不逐筆渲染 200 筆");
        a.Connection.TryGetOpenConversation(C1, out var seq).Should().BeTrue();
        seq.Should().Be(EventLogTests.Count(h.Host, "SELECT MAX(seq) FROM events"), "重開完成寫回真正的快照 seq");

        // 之後照常推增量
        await h.ItemAsync(device, C1, ConversationSamples.Item("之後的", id: "item300", ord: 300));
        await h.RoundAsync();
        (await a.TakeAsync(0)).Select(m => m["type"]!.Value<string>()).Should().Equal("item");
    }

    [Fact]
    public async Task 慢用戶端開著的對話被灌進大量增量_不溢出_最後收斂到持久最新版本()
    {
        // 合併前審查 F1：分幾輪灌進 2,000 筆（每輪 50 筆、不到一頁），送出卡住時佇列排到上限的一半就改成重開（排一個 open、增量先擋掉），
        // 重開受 K4 與 WouldOverflow 管：不溢出、不重送 init、不以 slow_client 中斷
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        a.Block();
        for (var start = 1; start <= 2_000; start += 50)
        {
            await h.PublishAsync(device, Enumerable.Range(start, 50)
                .Select(i => (K.Item, (JToken)ConversationSamples.Item("第 " + i + " 則 " + new string('長', 200), id: "item" + i, ord: i), C1)).ToArray());
            await h.RoundAsync();
            a.Connection.InitSent.Should().BeTrue($"從第 {start} 筆起的那一輪沒有溢出");
            a.Connection.Outbox.Count.Should().BeLessThanOrEqualTo(h.Options.Value.Browser.OutboxMaxMessages / 2 + 1);
        }
        a.Release();
        for (var i = 0; i < 20; i++)
        {
            await h.RoundAsync();
            if (a.Connection.PendingConversationRequests == 0 && a.Connection.Outbox.Count == 0)
                break;
        }
        var messages = await a.TakeAsync(0);
        h.Counters.Snapshot().OutboxOverflows.Should().Be(0);
        a.Aborts.Should().Be(0);
        a.Connection.InitSent.Should().BeTrue();

        // 網頁的狀態：最後一份 conversation 之後依序套用 item（同 web-protocol：同 id 整筆取代），最後 80 筆要等於重新 open 的持久最新版本
        var last = messages.FindLastIndex(m => m["type"]!.Value<string>() == "conversation");
        last.Should().BeGreaterThanOrEqualTo(0, "至少重開過一次");
        var view = messages[last]["items"]!.ToDictionary(i => i["id"]!.Value<string>()!, i => i["seq"]!.Value<long>());
        foreach (var m in messages.Skip(last + 1).Where(m => m["type"]!.Value<string>() == "item"))
            view[m["item"]!["id"]!.Value<string>()!] = m["item"]!["seq"]!.Value<long>();
        var (b, _) = await h.ReadyAsync("b2");
        var fresh = await h.OpenAsync(b, C1);
        view.OrderBy(p => p.Value).TakeLast(80).Select(p => p.Key).Should().Equal(fresh["items"]!.Select(i => i["id"]!.Value<string>()));
        fresh["items"]!.Last()["id"]!.Value<string>().Should().Be("item2000");
    }

    // ───── 工具 ─────

    /// <summary>
    /// 兩個時區（ClientState 的 tzOffsetMinutes）：一個讓這個整點正好是當地午夜（剛過午夜一分鐘前的更新算「昨天」）、一個讓它是當地中午（算「今天」）。
    /// 都在 ±840 分鐘內
    /// </summary>
    private static (int Midnight, int Noon) Zones(DateTimeOffset now)
    {
        var hour = now.UtcDateTime.Hour;
        var midnight = hour <= 14 ? hour * 60 : (hour - 24) * 60;
        var noon = midnight >= 0 ? midnight - 720 : midnight + 720;
        return (midnight, noon);
    }

    private static SendRecorder ReadyWithState(ConversationHarness h, string connectionId, int tzOffsetMinutes)
    {
        var recorder = h.Add(connectionId);
        h.SetState(recorder, true, [], tzOffsetMinutes);
        h.Gateway.RequestInit(recorder.Connection, "main");
        return recorder;
    }

    private static void AssertSafeItem(JObject item, string because)
    {
        item.Descendants().OfType<JProperty>().Where(p => string.Equals(p.Name, "path", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty(because);
        var html = item["html"]?.Value<string>() ?? string.Empty;
        SafeHtmlChecker.AssertSafe(html, because);
        Regex.IsMatch(html, "<script", RegexOptions.IgnoreCase).Should().BeFalse(because);
        Regex.IsMatch(html, @"<[^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase).Should().BeFalse(because);
    }
}
