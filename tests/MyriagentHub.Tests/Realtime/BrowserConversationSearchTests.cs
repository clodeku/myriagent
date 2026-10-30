using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 瀏覽器版側欄的對話搜尋（web-protocol 的 searchConversations／conversationSearch，2026-10-23）：伺服器搜清單裡每個對話的標題與
/// 使用者訊息、代理回覆、計畫確認的原文；只回給發起的連線；依 last_seq 快取，沒變的對話不重讀；刪除、移除的不算；形狀不對回 invalid。
/// 以 <see cref="ConversationHarness"/>（真的主機＋測試驅動的閘道）驗證，另有一則以真的主機與瀏覽器 hub 驗證 DI 的接線
/// </summary>
public sealed class BrowserConversationSearchTests
{
    private static readonly string C1 = ConversationHarness.ConversationId(1);

    private static readonly string C2 = ConversationHarness.ConversationId(2);

    private static readonly string C3 = ConversationHarness.ConversationId(3);

    private static JObject Item(string text, string id, long ord, string kind = "Assistant")
    {
        var item = ConversationSamples.Item(text, id: id, ord: ord);
        item["kind"] = kind;
        return item;
    }

    /// <summary>送 searchConversations、等這一次的搜尋做完，收下回覆</summary>
    private static async Task<JObject> SearchAsync(ConversationHarness h, SendRecorder recorder, string query, string requestId = "cs1")
    {
        (await h.CmdAsync(recorder, "searchConversations", new { requestId, query })).Should().BeNull();
        await h.Search.LastRun;
        var reply = await recorder.NextAsync();
        reply["type"]!.Value<string>().Should().Be("conversationSearch");
        reply["requestId"]!.Value<string>().Should().Be(requestId);
        return reply;
    }

    private static Dictionary<string, string?> Hits(JObject reply)
        => ((JArray)reply["hits"]!).ToDictionary(x => x.Value<string>("conversationId")!, x => x.Value<string>("snippet"));

    [Fact]
    public async Task 搜內容與標題_回片段_只給發起的連線()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1, title: "部署問題");
        await h.ItemAsync(device, C1, Item("上次部署到 HUB-PC 失敗了，幫我看 log", "u1", 1, "User"));
        await h.ItemAsync(device, C1, Item("原因是憑證過期", "a1", 2));
        await h.CreateAsync(device, C2, title: "別的事");
        await h.ItemAsync(device, C2, Item("隨便聊聊", "u1", 1, "User"));
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");

        var reply = await SearchAsync(h, a, "HUB-PC", "cs7");
        reply.ShouldBeJson($$"""{"type":"conversationSearch","requestId":"cs7","query":"HUB-PC","hits":[{"conversationId":"{{C1}}","snippet":"上次部署到 HUB-PC 失敗了，幫我看 log"}]}""");
        Hits(await SearchAsync(h, a, "憑證 部署")).Should().ContainKey(C1).WhoseValue.Should().StartWith("上次部署", "片段取第一則含任一關鍵字的");
        Hits(await SearchAsync(h, a, "別的")).Should().Equal(new Dictionary<string, string?> { [C2] = null }, "只有標題符合時片段是 null");
        Hits(await SearchAsync(h, a, "不存在的字")).Should().BeEmpty();
        await b.AssertNothingAsync("只給發起的連線");
        a.Connection.OpenConversationIds().Should().BeEmpty("搜尋不必先 open");
    }

    [Fact]
    public async Task 只搜使用者訊息_代理回覆與計畫確認()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1, title: "標題");
        await h.ItemAsync(device, C1, Item("秘密字", "t1", 1, "Thinking"));
        await h.ItemAsync(device, C1, Item("秘密字", "n1", 2, "Notice"));
        await h.ItemAsync(device, C1, Item("秘密字", "e1", 3, "Error"));
        await h.ItemAsync(device, C1, Item("計畫內容", "p1", 4, "PlanApproval"));
        var (a, _) = await h.ReadyAsync("b1");

        Hits(await SearchAsync(h, a, "秘密字")).Should().BeEmpty("思考、提示與錯誤不算內容");
        Hits(await SearchAsync(h, a, "計畫內容")).Should().ContainKey(C1, "計畫確認的計畫算內容");
    }

    [Fact]
    public async Task 沒變的對話不重讀_新項目_移除_刪除照實反映()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1, title: "一");
        await h.ItemAsync(device, C1, Item("第一版", "a1", 1));
        await h.CreateAsync(device, C2, title: "二");
        await h.ItemAsync(device, C2, Item("另一個對話", "a1", 1));
        var (a, _) = await h.ReadyAsync("b1");

        Hits(await SearchAsync(h, a, "第一版")).Should().ContainKey(C1);
        h.Search.ConversationReads.Should().Be(2, "第一次讀清單裡的每個對話");
        Hits(await SearchAsync(h, a, "第一")).Should().ContainKey(C1);
        h.Search.ConversationReads.Should().Be(2, "last_seq 沒變，用快取");

        await h.ItemAsync(device, C1, Item("第二則補上的內容", "a2", 2));
        Hits(await SearchAsync(h, a, "補上")).Should().ContainKey(C1);
        h.Search.ConversationReads.Should().Be(3, "只重讀變了的那個對話");

        await h.PublishAsync(device, (K.ItemRemoved, ConversationEventLogTests.Removal("a2"), C1));
        Hits(await SearchAsync(h, a, "補上")).Should().BeEmpty("移除的項目不算");

        await h.PublishAsync(device, (K.Deleted, new JObject(), C2));
        Hits(await SearchAsync(h, a, "另一個")).Should().BeEmpty("刪除的對話不在清單");
        h.Search.CachedCount.Should().Be(1, "不在清單的對話從快取丟掉");
    }

    [Fact]
    public async Task 政策不是full的對話只比標題()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C3, title: "只有標題的對話", policy: ConversationSyncPolicies.Metadata);
        var (a, _) = await h.ReadyAsync("b1");

        Hits(await SearchAsync(h, a, "只有標題")).Should().Equal(new Dictionary<string, string?> { [C3] = null });
    }

    [Fact]
    public async Task 形狀不對回invalid_空查詢立刻回空的結果()
    {
        await using var h = await ConversationHarness.StartAsync();
        var (a, _) = await h.ReadyAsync("b1");

        (await h.CmdAsync(a, "searchConversations", new { query = "x" })).Should().Be(BrowserErrorCodes.Invalid, "沒有 requestId");
        (await h.CmdAsync(a, "searchConversations", new { requestId = "", query = "x" })).Should().Be(BrowserErrorCodes.Invalid, "requestId 是空字串");
        (await h.CmdAsync(a, "searchConversations", new { requestId = new string('r', BrowserConversationSearch.MaxRequestIdLength + 1), query = "x" }))
            .Should().Be(BrowserErrorCodes.Invalid, "requestId 太長");
        (await h.CmdAsync(a, "searchConversations", new { requestId = "cs1" })).Should().Be(BrowserErrorCodes.Invalid, "沒有 query");
        (await h.CmdAsync(a, "searchConversations", new { requestId = "cs1", query = 3 })).Should().Be(BrowserErrorCodes.Invalid, "query 不是字串");
        await a.AssertNothingAsync();

        (await h.CmdAsync(a, "searchConversations", new { requestId = "cs2", query = "   " })).Should().BeNull();
        (await a.NextAsync()).ShouldBeJson("""{"type":"conversationSearch","requestId":"cs2","query":"   ","hits":[]}""");
        h.Search.ConversationReads.Should().Be(0, "沒有關鍵字不讀資料庫");
    }

    [Fact]
    public async Task 同一條連線連續搜尋_最後一次一定有回覆_其他的至多一次()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        for (var n = 1; n <= 5; n++)
        {
            var id = ConversationHarness.ConversationId(n);
            await h.CreateAsync(device, id, title: "對話" + n);
            await h.ItemAsync(device, id, Item("關鍵字 " + n, "a1", 1));
        }
        var (a, _) = await h.ReadyAsync("b1");

        for (var i = 0; i < 5; i++)
            (await h.CmdAsync(a, "searchConversations", new { requestId = "r" + i, query = "關鍵字" })).Should().BeNull();
        await h.Search.LastRun;
        var replies = new List<JObject>();
        while (true)
        {
            var reply = await a.NextAsync();
            replies.Add(reply);
            if (reply["requestId"]!.Value<string>() == "r4")
                break;
        }
        Hits(replies[^1]).Should().HaveCount(5);
        replies.Select(r => r["requestId"]!.Value<string>()).Should().OnlyHaveUniqueItems("被取代的不回或至多回一次");
        await a.AssertNothingAsync();
    }

    // ───── 端到端（真的主機、瀏覽器 hub）：DI 有接上、分類照伺服器處理 ─────

    [Fact]
    public async Task 端到端_瀏覽器hub送searchConversations_收到conversationSearch()
    {
        var now = DateTimeOffset.UtcNow;
        await using var host = await HubTestHost.StartAsync(clock: new ManualTimeProvider(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero)));
        var device = await EventLogTests.AddDeviceAsync(host);
        (await ConversationEventLogTests.Append(host, device,
            ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta("端到端的對話")),
            ConversationEventLogTests.Ev(2, K.Item, Item("伺服器上的內容可以搜到", "u1", 1, "User")))).Ack.Results.Should().OnlyContain(r => r.Rejected == null);

        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        (await owner.Hub.CmdAsync(new { cmd = "searchConversations", requestId = "cs1", query = "內容 可以" }))["accepted"]!.Value<bool>().Should().BeTrue();
        var reply = await owner.Hub.WaitForAsync("conversationSearch");
        reply.ShouldBeJson($$"""{"type":"conversationSearch","requestId":"cs1","query":"內容 可以","hits":[{"conversationId":"{{ConversationSamples.Conv}}","snippet":"伺服器上的內容可以搜到"}]}""");
    }
}
