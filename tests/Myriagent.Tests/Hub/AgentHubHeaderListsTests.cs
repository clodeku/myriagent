using Myriagent.Agents;
using Myriagent.Hub;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Myriagent.Tests.Hub;

/// <summary>標頭瘦身（HeaderListRules）：指令與模型清單沒變時，標頭更新不帶這兩個欄位；快照一律整份</summary>
public sealed class AgentHubHeaderListsTests
{
    [Fact]
    public void 規則_指紋看兩份清單_沒有和空陣列不同_省略只去掉清單()
    {
        var header = new JObject
        {
            ["id"] = "c1",
            ["title"] = "標題",
            ["models"] = new JArray(new JObject { ["value"] = "default" }),
            ["commands"] = new JArray(new JObject { ["name"] = "clear" }),
            ["agents"] = new JArray(new JObject { ["value"] = "reviewer" }),
            ["running"] = true,
        };
        var sig = HeaderListRules.Signature(header);

        HeaderListRules.Signature((JObject)header.DeepClone()).Should().Be(sig);
        var renamed = (JObject)header.DeepClone();
        renamed["title"] = "別的標題";
        HeaderListRules.Signature(renamed).Should().Be(sig, "只看三份清單");
        var changed = (JObject)header.DeepClone();
        ((JArray)changed["commands"]!).Add(new JObject { ["name"] = "compact" });
        HeaderListRules.Signature(changed).Should().NotBe(sig);
        var agentChanged = (JObject)header.DeepClone();
        ((JArray)agentChanged["agents"]!).Add(new JObject { ["value"] = "architect" });
        HeaderListRules.Signature(agentChanged).Should().NotBe(sig);
        var empty = (JObject)header.DeepClone();
        empty["commands"] = new JArray();
        var missing = (JObject)header.DeepClone();
        missing.Remove("commands");
        HeaderListRules.Signature(empty).Should().NotBe(HeaderListRules.Signature(missing));

        HeaderListRules.CanOmit(sig, sig).Should().BeTrue();
        HeaderListRules.CanOmit(sig, null).Should().BeFalse("沒送過整份的 view 一律整份");
        HeaderListRules.CanOmit(sig, HeaderListRules.Signature(changed)).Should().BeFalse();

        var slim = HeaderListRules.WithoutLists(header);
        slim.Properties().Select(p => p.Name).Should().Equal("id", "title", "running");
        header.ContainsKey("commands").Should().BeTrue("原本的標頭不變");
        header.ContainsKey("models").Should().BeTrue();
        header.ContainsKey("agents").Should().BeTrue();
    }

    [Fact]
    public async Task 標頭更新_清單沒變就省略_變了帶整份_快照與新開的視窗一律整份()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.Raise(s, new AgentCommandsEvent(new[] { new AgentSlashCommand("compact", "壓縮對話", "") }));
        await RenameAsync(h, h.Main, id, "第一次");

        // 清單沒變：之後的標頭更新省略兩份清單
        var slim = await RenameAsync(h, h.Main, id, "第二次");
        slim.ContainsKey("commands").Should().BeFalse();
        slim.ContainsKey("models").Should().BeFalse();
        slim.Value<string>("title").Should().Be("第二次");

        // 清單變了：下一則標頭帶整份
        var mark = h.Main.Mark();
        h.Raise(s, new AgentCommandsEvent(new[] { new AgentSlashCommand("compact", "壓縮對話", ""), new AgentSlashCommand("review", "審查", "") }));
        var full = (await h.Main.WaitForAsync("header", m => m["header"] is JObject hd && hd.ContainsKey("commands"), mark, conversationId: id))["header"]!;
        full["commands"]!.Select(c => c.Value<string>("name")).Should().Equal("compact", "review", "clear", "btw");
        full["models"].Should().NotBeNull();

        // 另一個視窗：open 的快照整份，之後兩個視窗各自判斷
        var popout = h.AddPopout("popout-1", id);
        h.Command(popout, "open", new { conversationId = id });
        popout.Last("conversation", id)!["header"]!["commands"]!.Select(c => c.Value<string>("name")).Should().Equal("compact", "review", "clear", "btw");
        var mainMark = h.Main.Mark();
        var popSlim = await RenameAsync(h, popout, id, "第三次");
        popSlim.ContainsKey("commands").Should().BeFalse("快照之後清單沒變");
        var mainSlim = (await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("title") == "第三次", mainMark, conversationId: id))["header"]!;
        ((JObject)mainSlim).ContainsKey("commands").Should().BeFalse();

        // 關掉再開：重新拿到整份快照；關掉期間沒收到的標頭不影響
        h.Command(h.Main, "close", new { conversationId = id });
        h.Raise(s, new AgentModelsEvent(new[] { new AgentModelOption("haiku", "Haiku 5.5", null, new[] { "low" }) }));
        h.Command(h.Main, "open", new { conversationId = id });
        h.Main.Last("conversation", id)!["header"]!["models"]!.Select(m => m.Value<string>("value")).Should().Equal("default", "haiku");
        var afterReopen = await RenameAsync(h, h.Main, id, "第四次");
        afterReopen.ContainsKey("models").Should().BeFalse("快照已帶了新的模型清單");
    }

    private static async Task<JObject> RenameAsync(HubHarness h, FakeView view, string id, string title)
    {
        var mark = view.Mark();
        h.Command(view, "rename", new { conversationId = id, title });
        return (JObject)(await view.WaitForAsync("header", m => m["header"]!.Value<string>("title") == title, mark, conversationId: id))["header"]!;
    }
}
