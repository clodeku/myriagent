using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

public class AgentHubCodexControlsTests
{
    [Fact]
    public async Task 控制尚未回覆時不送訊息_失敗回覆後恢復可操作()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Codex);
        h.CompleteTurn(session);
        var pending = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnCodexControl = (_, _) => pending.Task;
        h.Command(h.Main, "codexControl", new { conversationId = id, requestId = "one", action = "inspect" });
        await h.WaitUntilAsync(() => session.CodexControls.Count == 1, "讀取已開始");
        var mark = h.Main.Mark();
        h.Command(h.Main, "send", new { conversationId = id, text = "先別送" });
        await h.Main.WaitForAsync("sendRejected", since: mark);
        session.Sent.Count.Should().Be(1);
        pending.SetException(new InvalidOperationException("測試失敗"));
        (await h.Main.WaitForAsync("codexControl", since: mark)).Value<string>("error").Should().Be("測試失敗");
        h.Command(h.Main, "send", new { conversationId = id, text = "現在可以" });
        await h.WaitUntilAsync(() => session.Sent.Count == 2, "失敗後送出恢復");
    }

    [Fact]
    public async Task 技能與速度保存並帶進下一輪_目標事件更新標頭()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Codex);
        h.CompleteTurn(session);
        session.OnCodexControl = (action, args) => Task.FromResult(action == "speed"
            ? JObject.Parse("""{"serviceTier":"priority"}""")
            : JObject.Parse("""{"selectedSkills":[{"name":"test","path":"C:/test/SKILL.md"}]}"""));
        foreach (var action in new[] { "speed", "skillSelect" })
        {
            var mark = h.Main.Mark();
            h.Command(h.Main, "codexControl", new { conversationId = id, requestId = action, action });
            (await h.Main.WaitForAsync("codexControl", since: mark)).Value<string>("error").Should().BeNull();
        }
        h.Conversation(id)!.CodexServiceTier.Should().Be("priority");
        h.Conversation(id)!.CodexSkills!.Single().Name.Should().Be("test");
        h.Command(h.Main, "send", new { conversationId = id, text = "下一輪" });
        await h.WaitUntilAsync(() => session.Sent.Count == 2, "設定帶進下一輪");
        session.Sent.All[^1].Settings.CodexServiceTier.Should().Be("priority");
        session.Sent.All[^1].Settings.CodexSkills!.Single().Path.Should().Be("C:/test/SKILL.md");
        var next = h.Main.Mark();
        h.Raise(session, new AgentCodexGoalEvent(JObject.Parse("""{"objective":"完成測試","status":"paused","tokensUsed":123,"tokenBudget":1000}""")));
        var header = await h.Main.WaitForAsync("header", m => m["header"]?["codex"]?["goal"] is JObject goal && goal.Value<string>("status") == "paused", next);
        header["header"]!["codex"]!["goal"]!.Value<int>("tokensUsed").Should().Be(123);
    }

    [Fact]
    public async Task 執行中不啟動目標_但可以暫停_失敗不改已存設定()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Codex);
        var mark = h.Main.Mark();
        h.Command(h.Main, "codexControl", new { conversationId = id, requestId = "start", action = "goalSet", objective = "測試" });
        (await h.Main.WaitForAsync("codexControl", since: mark)).Value<string>("error").Should().Contain("停止");
        session.CodexControls.All.Should().BeEmpty();
        mark = h.Main.Mark();
        h.Command(h.Main, "codexControl", new { conversationId = id, requestId = "pause", action = "goalPause" });
        (await h.Main.WaitForAsync("codexControl", since: mark)).Value<string>("error").Should().BeNull();
        session.CodexControls.All.Should().ContainSingle(x => x.Action == "goalPause");
        session.OnCodexControl = (_, _) => Task.FromException<JObject>(new InvalidOperationException("目前模型不支援"));
        mark = h.Main.Mark();
        h.Command(h.Main, "codexControl", new { conversationId = id, requestId = "bad", action = "speed", serviceTier = "bad" });
        (await h.Main.WaitForAsync("codexControl", since: mark)).Value<string>("error").Should().Be("目前模型不支援");
        h.Conversation(id)!.CodexServiceTier.Should().BeNull();
    }
}
