using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 臨時對話（AgentHub.QuickChat.cs；web-protocol 的 quickChat、quickChatEnd、speakItem）：開的是臨時的草稿（標頭 ephemeral、標題「臨時對話」、
/// 不改新對話預設的帳戶），送出後不列在清單、localOnly；朗讀交給宿主念；關掉就刪掉（不進回收區）；上次沒刪掉的在啟動時刪掉
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubQuickChatTests
{
    private const string Dock = "quick:dock";

    private static JObject Open(HubHarness h, string? accountId = null)
    {
        var mark = h.Main.Mark();
        h.Command(h.Main, "quickChat", new { paneId = Dock, accountId });
        var conv = h.Main.Messages("conversation", mark).Single();
        conv.Value<string>("paneId").Should().Be(Dock, "網頁以面板的記號認出要嵌進臨時對話的面板");
        return conv;
    }

    [Fact]
    public async Task 開的是臨時草稿_送出後不列在清單_localOnly_不改新對話預設的帳戶()
    {
        using var h = new HubHarness();
        h.Ui.Invoke(() => h.Settings.LastAccountId = h.Claude2.Id);
        var conv = Open(h, h.Claude.Id);
        var id = conv.Value<string>("conversationId")!;
        var header = (JObject)conv["header"]!;
        header.Value<bool>("isDraft").Should().BeTrue();
        header.Value<bool>("ephemeral").Should().BeTrue();
        header.Value<string>("title").Should().Be(QuickChatRules.Title);
        header.Value<string>("accountId").Should().Be(h.Claude.Id);
        h.Ui.Invoke(() => h.Settings.LastAccountId).Should().Be(h.Claude2.Id, "臨時對話不改一般新對話預設的帳戶");

        var before = h.Sessions.Count;
        h.Send(h.Main, id, "今天適合穿什麼？");
        await h.WaitUntilAsync(() => h.Sessions.Count == before + 1 && h.Sessions[^1].Sent.All.Count == 1, "送出");
        var meta = h.Conversation(id)!;
        meta.Ephemeral.Should().BeTrue();
        meta.SyncPolicy.Should().Be(ConversationSyncPolicies.LocalOnly, "桌面開的臨時對話不上伺服器");
        meta.WorkingDirectory.Should().BeNull("用對話自己的工作區，不選資料夾");
        await h.Ui.DrainAsync();
        var lists = h.Main.Messages("conversations");
        lists.Should().NotBeEmpty();
        ((JArray)lists[^1]["items"]!).Select(i => i.Value<string>("id")).Should().NotContain(id, "臨時對話不列在對話清單");
        var smark = h.Main.Mark();
        h.Command(h.Main, "searchConversations", new { requestId = "s1", query = "適合穿" });
        var found = await h.Main.WaitForAsync("conversationSearch", since: smark, timeoutMs: 5000);
        ((JArray)found["hits"]!).Select(x => x.Value<string>("conversationId")).Should().NotContain(id, "也不在搜尋裡");
    }

    [Fact]
    public async Task 朗讀交給宿主念這則的純文字()
    {
        using var h = new HubHarness();
        var id = Open(h).Value<string>("conversationId")!;
        var before = h.Sessions.Count;
        h.Send(h.Main, id, "**粗體**的問題\n```js\nconsole.log(1)\n```\n看 https://example.com");
        await h.WaitUntilAsync(() => h.Sessions.Count == before + 1 && h.Sessions[^1].Sent.All.Count == 1, "送出");
        var userItem = await h.Main.WaitForAsync("item", m => m["item"]?.Value<string>("kind") == "User", conversationId: id);
        var itemId = userItem["item"]!.Value<string>("id")!;

        h.Command(h.Main, "speakItem", new { conversationId = id, itemId });

        await h.WaitUntilAsync(() => h.Host.Spoken.All.Count == 1, "念出來");
        var spoken = h.Host.Spoken.All[0];
        spoken.Should().Contain("粗體的問題").And.Contain(QuickChatRules.CodeBlockSpoken).And.Contain("連結");
        spoken.Should().NotContain("console.log").And.NotContain("**").And.NotContain("https://");
        h.Host.SpeechStops.Should().Be(1, "先停下正在念的");
    }

    [Fact]
    public async Task 關掉就刪掉_不進回收區_一般對話不收()
    {
        using var h = new HubHarness();
        var id = Open(h).Value<string>("conversationId")!;
        var before = h.Sessions.Count;
        h.Send(h.Main, id, "問一下");
        await h.WaitUntilAsync(() => h.Sessions.Count == before + 1 && h.Sessions[^1].Sent.All.Count == 1, "送出");
        var dir = h.Store.GetConversationDirectory(id);
        Directory.Exists(dir).Should().BeTrue("送出後存檔了");

        var mark = h.Main.Mark();
        h.Command(h.Main, "quickChatEnd", new { conversationId = id });

        h.Conversation(id).Should().BeNull();
        h.Main.Messages("conversationClosed", mark).Select(m => m.Value<string>("conversationId")).Should().Contain(id);
        await h.WaitUntilAsync(() => !Directory.Exists(dir), "資料夾刪掉");
        Directory.Exists(Path.Combine(h.Store.RootDirectory, "..", "conversations-trash", id)).Should().BeFalse("不進回收區");

        // 一般對話送 quickChatEnd 不理
        var normal = new AgentConversation { AccountId = h.Claude.Id, Provider = AgentProvider.Claude, Title = "一般", SessionStarted = true, SessionId = Guid.NewGuid().ToString() };
        using var h2 = new HubHarness(seedStore: store => store.SaveConversation(normal));
        h2.Command(h2.Main, "quickChatEnd", new { conversationId = normal.Id });
        h2.Conversation(normal.Id).Should().NotBeNull();
    }

    [Fact]
    public void 還沒送出的草稿關掉就丟掉()
    {
        using var h = new HubHarness();
        var id = Open(h).Value<string>("conversationId")!;
        h.Command(h.Main, "quickChatEnd", new { conversationId = id });
        h.Conversation(id).Should().BeNull();
        Directory.Exists(h.Store.GetConversationDirectory(id)).Should().BeFalse("草稿沒有存過檔");
    }

    [Fact]
    public async Task 上次沒刪掉的臨時對話_啟動時不載入並刪掉()
    {
        var leftover = new AgentConversation
        {
            AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = QuickChatRules.Title, SessionStarted = true,
            SessionId = Guid.NewGuid().ToString(), Ephemeral = true, SyncPolicy = ConversationSyncPolicies.LocalOnly,
        };
        var normal = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "留著", SessionStarted = true, SessionId = Guid.NewGuid().ToString() };
        using var h = new HubHarness(seedStore: store =>
        {
            store.SaveConversation(leftover);
            store.SaveConversation(normal);
        });
        h.Conversation(leftover.Id).Should().BeNull();
        h.Conversation(normal.Id).Should().NotBeNull();
        await h.WaitUntilAsync(() => !Directory.Exists(h.Store.GetConversationDirectory(leftover.Id)), "啟動時刪掉");
    }
}
