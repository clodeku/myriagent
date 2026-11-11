using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>標頭的 defaultEffort（2026-11-12）：照帳戶記下的模型清單算這個對話選「預設」強度時實際用的強度；模型清單每一項帶 defaultEffort</summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubDefaultEffortTests
{
    [Fact]
    public async Task 標頭帶這個對話的實際預設強度_換模型跟著變()
    {
        var conversation = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "t", SessionId = Guid.NewGuid().ToString() };
        using var h = new HubHarness(seedStore: store => store.SaveConversation(conversation));
        h.Ui.Invoke(() => h.Settings.Models[h.Claude.Id] =
        [
            new AgentModelOption("default", "Default", null, ["low", "medium", "high"], "medium"),
            new AgentModelOption("claude-opus-4-7", "Opus 4.7", null, ["low", "high", "xhigh"], "xhigh"),
        ]);
        var mark = h.Main.Mark();
        h.Command(h.Main, "open", new { conversationId = conversation.Id });
        var header = (JObject)(await h.Main.WaitForAsync("conversation", since: mark))["header"]!;
        header.Value<string>("defaultEffort").Should().Be("medium");
        ((JArray)header["models"]!).Cast<JObject>().First(m => m.Value<string>("value") == "claude-opus-4-7").Value<string>("defaultEffort").Should().Be("xhigh");

        mark = h.Main.Mark();
        h.Command(h.Main, "setOption", new { conversationId = conversation.Id, key = "model", value = "claude-opus-4-7" });
        var next = await h.Main.WaitForAsync("header", m => m["header"]?.Value<string>("model") == "claude-opus-4-7", since: mark, timeoutMs: 4000);
        next["header"]!.Value<string>("defaultEffort").Should().Be("xhigh");
    }
}
