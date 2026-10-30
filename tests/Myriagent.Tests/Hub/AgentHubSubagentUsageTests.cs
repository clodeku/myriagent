using Myriagent.Agents;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// Claude 子代理的即時用量（AgentHub.Subagents.cs，web-protocol 的 Header.subagents）：tool_use_id 對到 Agent 工具卡、以項目 id 放在標頭；
/// 一輪結束後還在跑的子代理照樣對得到；最多留 MaxSubagentUsages 個（最近更新的）
/// </summary>
public class AgentHubSubagentUsageTests
{
    private static async Task<JObject?> SubagentsAsync(HubHarness h, string id, int mark, Func<JObject, bool> until)
    {
        var message = await h.Main.WaitForAsync("header", m => m["header"]?["subagents"] is JObject s && until(s), mark, conversationId: id);
        return (JObject)message["header"]!["subagents"]!;
    }

    [Fact]
    public async Task 子代理的用量畫在它的工具卡上_結束後帶最後的數字()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude, "找出所有呼叫點");
        h.Raise(s, new AgentToolStartedEvent("toolu_A", "Agent", "agent", "找呼叫點", "{}"));
        var card = h.Items(id).Single(i => i.Kind == AgentItemKind.Tool);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentSubagentUsageEvent(new SubagentUsage("toolu_A", 15_500, 2, 3_000, "Grep", false)));

        var subagents = await SubagentsAsync(h, id, mark, o => o[card.Id] != null);
        subagents[card.Id]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"tokens":15500,"tools":2,"ms":3000,"last":"Grep","done":false}""");

        h.CompleteTurn(s);
        mark = h.Main.Mark();
        h.Raise(s, new AgentSubagentUsageEvent(new SubagentUsage("toolu_A", 20_000, 3, 9_000, null, true)));
        subagents = await SubagentsAsync(h, id, mark, o => o[card.Id]?.Value<bool>("done") == true);
        subagents[card.Id]!.Value<long>("tokens").Should().Be(20_000, "一輪結束之後對到的還是同一張卡");
    }

    [Fact]
    public async Task 對不到工具卡的不記()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Raise(s, new AgentSubagentUsageEvent(new SubagentUsage("toolu_X", 1, 1, 1, null, false)));
        await Task.Delay(300);   // 標頭的合併計時器（50 ms）

        h.Main.Messages("header", mark, id).Should().NotContain(m => m["header"]!["subagents"] != null);
    }

    [Fact]
    public async Task 最多留最近更新的幾個()
    {
        using var h = new HubHarness();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        var count = AgentHub.MaxSubagentUsages + 1;
        for (var i = 0; i < count; i++)
            h.Raise(s, new AgentToolStartedEvent("toolu_" + i, "Agent", "agent", "子代理 " + i, "{}"));
        var cards = h.Items(id).Where(i => i.Kind == AgentItemKind.Tool).Select(i => i.Id).ToList();
        cards.Should().HaveCount(count);

        for (var i = 0; i < count; i++)
            h.Raise(s, new AgentSubagentUsageEvent(new SubagentUsage("toolu_" + i, i, 1, 1, null, false)));
        // 第 31 個進來時丟掉最早的（toolu_0）；第二早的又更新了一次：排到最後
        var mark = h.Main.Mark();
        h.Raise(s, new AgentSubagentUsageEvent(new SubagentUsage("toolu_1", 999, 2, 2, null, false)));

        var subagents = await SubagentsAsync(h, id, mark, o => o[cards[1]]?.Value<long>("tokens") == 999);
        subagents.Count.Should().Be(AgentHub.MaxSubagentUsages);
        subagents[cards[0]].Should().BeNull("最早更新的被丟掉");
        subagents.Properties().Last().Name.Should().Be(cards[1], "剛更新的排在最後");
    }
}
