using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 側欄的對話搜尋（web-protocol 的 searchConversations／conversationSearch）：沒開過的對話讀 items.json（只讀、依修改時間快取）、
/// 開過的用記憶體裡最新的內容、只搜清單列的對話、損毀的檔不動、回覆只給發出的視窗
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubSearchTests
{
    private static AgentConversation Conversation(string title, DateTime updatedAt) => new()
    {
        AccountId = "acc-claude",
        Provider = AgentProvider.Claude,
        Title = title,
        SessionId = Guid.NewGuid().ToString(),
        SessionStarted = true,
        UpdatedAt = updatedAt,
    };

    private static AgentChatItem Item(AgentItemKind kind, string text, string? output = null)
        => new() { Kind = kind, Text = text, Output = output };

    private static void Seed(ConversationStore store, AgentConversation conversation, params AgentChatItem[] items)
    {
        store.SaveConversation(conversation);
        store.SaveItems(conversation.Id, items);
    }

    private static async Task<JObject> SearchAsync(HubHarness h, string query, FakeView? view = null, string? requestId = null)
    {
        view ??= h.Main;
        requestId ??= "cs-" + Guid.NewGuid().ToString("N");
        var mark = view.Mark();
        h.Command(view, "searchConversations", new { requestId, query });
        return await view.WaitForAsync("conversationSearch", m => m.Value<string>("requestId") == requestId, mark, timeoutMs: 5000);
    }

    private static Dictionary<string, string?> Hits(JObject reply)
        => ((JArray)reply["hits"]!).ToDictionary(x => x.Value<string>("conversationId")!, x => x.Value<string>("snippet"));

    [Fact]
    public async Task 沒開過的對話_讀紀錄檔搜內容_回片段與原樣的requestId和query()
    {
        var deploy = Conversation("部署問題", new DateTime(2026, 10, 20, 9, 0, 0));
        var other = Conversation("別的事", new DateTime(2026, 10, 21, 9, 0, 0));
        using var h = new HubHarness(seedStore: store =>
        {
            Seed(store, deploy, Item(AgentItemKind.User, "上次部署到 HUB-PC 失敗了，幫我看 log"), Item(AgentItemKind.Assistant, "原因是憑證過期"));
            Seed(store, other, Item(AgentItemKind.User, "隨便聊聊"));
        });

        var reply = await SearchAsync(h, "HUB-PC", requestId: "cs7");
        reply.Value<string>("requestId").Should().Be("cs7");
        reply.Value<string>("query").Should().Be("HUB-PC");
        Hits(reply).Should().Equal(new Dictionary<string, string?> { [deploy.Id] = "上次部署到 HUB-PC 失敗了，幫我看 log" });

        Hits(await SearchAsync(h, "憑證 部署")).Should().ContainKey(deploy.Id).WhoseValue.Should().StartWith("上次部署", "片段取第一則含任一關鍵字的");
        Hits(await SearchAsync(h, "別的")).Should().Equal(new Dictionary<string, string?> { [other.Id] = null }, "只有標題符合時片段是 null");
        h.Items(deploy.Id).Should().NotBeEmpty("對照：中樞讀得到這個對話（搜尋本身不載入）");
    }

    [Fact]
    public async Task 工具輸出_思考與提示不算內容()
    {
        var c = Conversation("標題", DateTime.Now);
        using var h = new HubHarness(seedStore: store => Seed(store, c,
            Item(AgentItemKind.Tool, "秘密字", output: "秘密字"),
            Item(AgentItemKind.Thinking, "秘密字"),
            Item(AgentItemKind.Notice, "秘密字"),
            Item(AgentItemKind.Error, "秘密字"),
            Item(AgentItemKind.PlanApproval, "計畫內容")));

        Hits(await SearchAsync(h, "秘密字")).Should().BeEmpty();
        Hits(await SearchAsync(h, "計畫內容")).Should().ContainKey(c.Id, "計畫確認的計畫算內容");
    }

    [Fact]
    public async Task 沒變的紀錄檔不重讀_改了才重讀()
    {
        var c = Conversation("標題", DateTime.Now);
        using var h = new HubHarness(seedStore: store => Seed(store, c, Item(AgentItemKind.User, "第一版")));

        Hits(await SearchAsync(h, "第一版")).Should().ContainKey(c.Id);
        var reads = h.OnUi(() => h.Hub.SearchIndex.FileReads);
        reads.Should().Be(1);
        Hits(await SearchAsync(h, "第一")).Should().ContainKey(c.Id);
        h.OnUi(() => h.Hub.SearchIndex.FileReads).Should().Be(reads, "修改時間與大小沒變，用快取");

        // 別的程式改了檔（例如另一個版本的 app）：修改時間或大小變了就重讀
        h.Store.SaveItems(c.Id, [Item(AgentItemKind.User, "第二版的內容")]);
        File.SetLastWriteTimeUtc(h.Store.GetItemsPath(c.Id), DateTime.UtcNow.AddMinutes(1));
        Hits(await SearchAsync(h, "第一版")).Should().BeEmpty();
        Hits(await SearchAsync(h, "第二版")).Should().ContainKey(c.Id);
        h.OnUi(() => h.Hub.SearchIndex.FileReads).Should().Be(reads + 1);
    }

    [Fact]
    public async Task 開著的對話_用記憶體裡最新的內容()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude, "請幫我整理報表");
        h.Raise(session, new AgentTextCompletedEvent("msg-1", "整理好了，總計三百筆"));
        h.CompleteTurn(session);

        var hits = Hits(await SearchAsync(h, "三百筆"));
        hits.Should().ContainKey(id).WhoseValue.Should().Be("整理好了，總計三百筆");
        Hits(await SearchAsync(h, "整理 報表")).Should().ContainKey(id).WhoseValue.Should().Be("請幫我整理報表");
        h.OnUi(() => h.Hub.SearchIndex.FileReads).Should().Be(0, "載入過的對話不讀檔");
    }

    [Fact]
    public async Task 草稿不搜_空查詢回空結果()
    {
        using var h = new HubHarness();
        var draft = h.NewConversation(h.Main, h.Claude);
        h.Conversation(draft).Should().NotBeNull();

        Hits(await SearchAsync(h, "新對話")).Should().NotContainKey(draft, "草稿不在清單裡");
        var empty = await SearchAsync(h, "   ");
        Hits(empty).Should().BeEmpty();
    }

    [Fact]
    public async Task 損毀的紀錄檔_只比標題_原檔不改名()
    {
        var c = Conversation("壞掉的對話", DateTime.Now);
        using var h = new HubHarness(seedStore: store =>
        {
            store.SaveConversation(c);
            Directory.CreateDirectory(store.GetConversationDirectory(c.Id));
            File.WriteAllText(store.GetItemsPath(c.Id), "[ { \"Kind\": \"User\", \"Text\": \"半");
        });

        Hits(await SearchAsync(h, "半")).Should().BeEmpty();
        Hits(await SearchAsync(h, "壞掉")).Should().Equal(new Dictionary<string, string?> { [c.Id] = null });
        File.Exists(h.Store.GetItemsPath(c.Id)).Should().BeTrue("搜尋只讀，不把損毀檔改名成 .corrupt-*");
        Directory.GetFiles(h.Store.GetConversationDirectory(c.Id), "*.corrupt-*").Should().BeEmpty();
    }

    [Fact]
    public async Task 回覆只給發出的視窗_刪除的對話不再出現()
    {
        var c = Conversation("標題", DateTime.Now);
        using var h = new HubHarness(seedStore: store => Seed(store, c, Item(AgentItemKind.User, "找得到我")));
        var other = h.AddView("second");
        var mark = other.Mark();

        Hits(await SearchAsync(h, "找得到")).Should().ContainKey(c.Id);
        other.Messages("conversationSearch", mark).Should().BeEmpty();
        h.OnUi(() => h.Hub.SearchIndex.CachedCount).Should().Be(1);

        h.Command(h.Main, "delete", new { conversationId = c.Id });
        await h.WaitUntilAsync(() => h.Conversation(c.Id) == null, "對話刪除");
        Hits(await SearchAsync(h, "找得到")).Should().BeEmpty();
        h.OnUi(() => h.Hub.SearchIndex.CachedCount).Should().Be(0, "不在清單的對話從快取丟掉");
    }

    [Fact]
    public async Task 同一個視窗連續搜尋_最後一次一定有回覆()
    {
        var conversations = Enumerable.Range(0, 30).Select(i => Conversation("對話" + i, DateTime.Now.AddMinutes(-i))).ToList();
        using var h = new HubHarness(seedStore: store =>
        {
            foreach (var c in conversations)
                Seed(store, c, Enumerable.Range(0, 50).Select(k => Item(AgentItemKind.Assistant, $"第 {k} 段，關鍵字 {k % 7}")).ToArray());
        });

        var mark = h.Main.Mark();
        h.Ui.Invoke(() =>
        {
            for (var i = 0; i < 5; i++)
                h.Hub.HandleCommand(h.Main, HubHarness.MakeCommand("searchConversations", new { requestId = "r" + i, query = "關鍵字 " + i }));
        });
        var last = await h.Main.WaitForAsync("conversationSearch", m => m.Value<string>("requestId") == "r4", mark, timeoutMs: 5000);
        Hits(last).Should().HaveCount(30);
        await h.Ui.DrainAsync();
        h.Main.Messages("conversationSearch", mark).Select(m => m.Value<string>("requestId")).Should().OnlyHaveUniqueItems("被取代的不回覆或至多回一次");
    }
}
