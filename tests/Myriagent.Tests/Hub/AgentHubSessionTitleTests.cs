using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 對話標題與代理工作階段名稱雙向同步（Claude 的 rename_session／session_title_changed：claude.ai／Claude App 上的 Remote Control 工作階段）：
/// app 改名送給會話、啟動的設定帶標題；代理回報在別處改的名稱就改標題（存檔、不送回代理），自己改名的回送不把標題改回去
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubSessionTitleTests
{
    [Fact]
    public void 第一則訊息的設定帶對話標題_改名送給會話_之後的送出也帶新標題()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理這個月的報表");
        s.Sent.All[0].Settings.Title.Should().Be("整理這個月的報表", "程序啟動時以它命名工作階段");
        h.CompleteTurn(s);

        h.Command(h.Main, "rename", new { conversationId = id, title = "月報" });

        s.Renames.All.Should().Equal("月報");
        h.Send(h.Main, id, "下一則");
        s.Sent.All[1].Settings.Title.Should().Be("月報");
    }

    [Fact]
    public void 草稿改名_沒有會話_不送()
    {
        using var h = new HubHarness();
        var id = h.NewConversation(h.Main, h.Claude);

        h.Command(h.Main, "rename", new { conversationId = id, title = "還沒送出的對話" });

        h.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task 在ClaudeApp改了名稱_對話標題跟著改並存檔_標頭與清單更新_不送回代理()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentSessionTitleEvent("手機上改的名稱"));

        h.Conversation(id)!.Title.Should().Be("手機上改的名稱");
        h.Store.LoadConversations().Single(c => c.Id == id).Title.Should().Be("手機上改的名稱", "存檔（同步也看 meta）");
        await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("title") == "手機上改的名稱", mark, conversationId: id);
        await h.Main.WaitForAsync("conversations", m => m["items"]!.Any(c => c.Value<string>("title") == "手機上改的名稱"), mark);
        s.Renames.All.Should().BeEmpty("名稱本來就是代理那邊的");
    }

    [Fact]
    public void 代理回報的名稱和標題相同_什麼都不改()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);
        var updatedAt = h.Conversation(id)!.UpdatedAt;

        h.Raise(s, new AgentSessionTitleEvent("整理報表"));

        h.Conversation(id)!.Title.Should().Be("整理報表");
        h.Conversation(id)!.UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public void 連續改名_代理依序回送較早的名稱_標題不會被改回去()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);

        h.Command(h.Main, "rename", new { conversationId = id, title = "第一次改" });
        h.Command(h.Main, "rename", new { conversationId = id, title = "第二次改" });
        s.Renames.All.Should().Equal("第一次改", "第二次改");

        h.Raise(s, new AgentSessionTitleEvent("第一次改"));
        h.Conversation(id)!.Title.Should().Be("第二次改", "第一次的回送晚到：不能把標題改回去");
        h.Raise(s, new AgentSessionTitleEvent("第二次改"));
        h.Conversation(id)!.Title.Should().Be("第二次改");

        // 之後在手機上改回第一次的名稱：不是回送了，照改
        h.Raise(s, new AgentSessionTitleEvent("第一次改"));
        h.Conversation(id)!.Title.Should().Be("第一次改");
    }

    [Fact]
    public void Claude清除對話之後_新的工作階段照對話標題命名()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.CompleteTurn(s);

        h.Send(h.Main, id, "/clear");
        h.CompleteTurn(s);

        s.Renames.All.Should().Equal("整理報表");
    }
}
