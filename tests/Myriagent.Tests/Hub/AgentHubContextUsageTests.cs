using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 上下文用量提示（Header.context）：會話的 AgentContextUsageEvent 以 ContextUsageRules 合併後放進標頭（用量與視窗都知道才有）；
/// 壓縮、/clear、換帳戶之後清掉用量，等下一次呼叫
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubContextUsageTests
{
    private static JToken? Context(JObject message) => message["header"]?["context"];

    private static bool HasContext(JObject message) => Context(message) is JObject;

    private static bool NoContext(JObject message) => Context(message) is { Type: JTokenType.Null };

    [Fact]
    public async Task 用量與視窗都知道才放進標頭_嚴重度以自動壓縮門檻為分母()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");

        var mark = h.Main.Mark();
        h.Raise(s, new AgentContextUsageEvent(22_067, null));
        await Task.Delay(120);
        h.Main.Messages("header", mark, id).Should().OnlyContain(m => NoContext(m), "視窗還不知道（第一則 result 之前）：不顯示");

        h.Raise(s, new AgentContextUsageEvent(null, 200_000));
        var first = (JObject)Context(await h.Main.WaitForAsync("header", HasContext, mark, conversationId: id))!;
        first.Value<long>("used").Should().Be(22_067);
        first.Value<long>("window").Should().Be(200_000);
        first.Value<double>("percent").Should().Be(11.0);
        first["autoCompactAt"]!.Type.Should().Be(JTokenType.Null);
        first.Value<string>("severity").Should().Be("ok");

        mark = h.Main.Mark();
        h.Raise(s, new AgentContextUsageEvent(null, 200_000, 167_000));
        h.Raise(s, new AgentContextUsageEvent(160_000, null));
        var near = (JObject)Context(await h.Main.WaitForAsync("header", m => Context(m)?.Value<string>("severity") == "max", mark, conversationId: id))!;
        near.Value<long>("autoCompactAt").Should().Be(167_000);
        near.Value<double>("percent").Should().Be(80.0, "百分比以視窗為分母");

        // 同樣的值再來一次：標頭不重送
        mark = h.Main.Mark();
        h.Raise(s, new AgentContextUsageEvent(160_000, 200_000));
        await Task.Delay(150);
        h.Main.Messages("header", mark, id).Should().BeEmpty();
    }

    [Fact]
    public async Task 壓縮與clear之後清掉用量_換帳戶也清掉()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "整理報表");
        h.Raise(s, new AgentContextUsageEvent(22_113, 1_000_000, 967_000));
        h.CompleteTurn(s);

        var mark = h.Main.Mark();
        h.Raise(s, new AgentContextUsageEvent(null, null, Compacted: true));
        await h.Main.WaitForAsync("header", NoContext, mark, conversationId: id);
        mark = h.Main.Mark();
        h.Raise(s, new AgentContextUsageEvent(23_214, null));
        var back = (JObject)Context(await h.Main.WaitForAsync("header", HasContext, mark, conversationId: id))!;
        back.Value<long>("autoCompactAt").Should().Be(967_000, "視窗與門檻留著，只有用量等下一次呼叫");

        // Claude 的 /clear 交給 CLI：正常結束、換成新的 session 之後清掉
        h.Send(h.Main, id, "/clear");
        h.Raise(s, new AgentSessionStartedEvent("cleared-session", null, null));
        mark = h.Main.Mark();
        h.CompleteTurn(s);
        await h.Main.WaitForAsync("header", m => NoContext(m) && !m["header"]!.Value<bool>("running"), mark, conversationId: id);

        h.Send(h.Main, id, "下一則");
        h.Raise(s, new AgentContextUsageEvent(22_500, 1_000_000));
        h.CompleteTurn(s);
        mark = h.Main.Mark();
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Codex.Id });
        // setAccount 的回覆是整份 conversation（web-protocol 第一節）
        var switched = await h.Main.WaitForAsync("conversation", m => m["header"]!.Value<string>("provider") == "Codex", mark, conversationId: id);
        NoContext(switched).Should().BeTrue("新帳戶開新的 CLI 對話：上下文從前文節錄重新算");
    }
}
