using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

public class CodexControlRulesTests
{
    [Fact]
    public void 速度以目前模型回報的原生id為準_不把Fast文字當參數()
    {
        var catalog = JObject.Parse("""{"data":[{"model":"a","isDefault":true,"serviceTiers":[{"id":"priority","name":"Fast","description":"faster"}]},{"model":"b","serviceTiers":[]}]}""");
        var tiers = CodexControlRules.SpeedTiers(catalog, "default");
        CodexControlRules.AllowsTier(tiers, "priority").Should().BeTrue();
        CodexControlRules.AllowsTier(tiers, "fast").Should().BeFalse();
        CodexControlRules.AllowsTier(CodexControlRules.SpeedTiers(catalog, "b"), "priority").Should().BeFalse();
        CodexControlRules.AllowsTier(tiers, "default").Should().BeTrue();
    }

    [Theory]
    [InlineData("/compact", "thread/compact/start", null)]
    [InlineData("/review", "review/start", "uncommittedChanges")]
    [InlineData("/review commit a123456", "review/start", "commit")]
    [InlineData("/review base main", "review/start", "baseBranch")]
    [InlineData("/review 檢查取消流程", "review/start", "custom")]
    public void 操作使用專用RPC(string text, string method, string? target)
    {
        var request = CodexControlRules.SpecialTurn(text, "thread")!.Value;
        request.Method.Should().Be(method);
        request.Params.Value<string>("threadId").Should().Be("thread");
        (request.Params["target"] as JObject)?.Value<string>("type").Should().Be(target);
    }

    [Theory]
    [InlineData("/review commit main; Remove-Item C:/")]
    [InlineData("/review base -bad")]
    public void 不接受無效的審查參數(string text)
        => FluentActions.Invoking(() => CodexControlRules.SpecialTurn(text, "t")).Should().Throw<ArgumentException>();

    [Fact]
    public void 目標預算與使用者來源有驗證_暫停不覆寫目標()
    {
        var p = CodexControlRules.GoalParams("t", "goalPause", new JObject());
        p.Value<string>("status").Should().Be("paused");
        p.Value<string>("origin").Should().Be("user");
        p.Property("objective").Should().BeNull();
        FluentActions.Invoking(() => CodexControlRules.GoalParams("t", "goalSet", new JObject { ["objective"] = "完成測試", ["tokenBudget"] = -1 }))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => CodexControlRules.GoalParams("t", "goalSet", new JObject { ["objective"] = new string('中', 4001) }))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MCP授權狀態與連線錯誤分開_空工具清單也可能連線成功()
    {
        CodexControlRules.McpServer(JObject.Parse("""{"name":"oauth","authStatus":"notLoggedIn","tools":{}}""")).Status.Should().Be("needs-auth");
        CodexControlRules.McpServer(JObject.Parse("""{"name":"broken","authStatus":"unsupported","toolsError":"failed","tools":{}}""")).Status.Should().Be("failed");
        CodexControlRules.McpServer(JObject.Parse("""{"name":"empty","authStatus":"unsupported","tools":{}}""")).Status.Should().Be("connected");
        CodexControlRules.McpServer(JObject.Parse("""{"name":"pending","runtimeStatus":"notStarted"}""")).Status.Should().Be("pending");
        CodexControlRules.McpServer(JObject.Parse("""{"name":"auth","runtimeStatus":"authenticationRequired","authStatus":"unknown"}""")).Status.Should().Be("needs-auth");
        CodexControlRules.McpServer(JObject.Parse("""{"name":"off","runtimeStatus":"disabled","authStatus":"notLoggedIn"}""")).Status.Should().Be("disabled");
    }

    [Fact]
    public void 子代理文字保留在自己的卡片_不結束主代理_完整文字不重複()
    {
        var parser = new CodexEventParser();
        var start = parser.Parse("""{"method":"item/started","params":{"threadId":"main","item":{"id":"s","type":"subAgentActivity","kind":"started","agentThreadId":"child","agentPath":"/root/check","model":"a","reasoningEffort":"high"}}}""", "main");
        var tool = start.Events.OfType<AgentToolStartedEvent>().Single();
        tool.Title.Should().Contain("/root/check").And.Contain("high");
        var delta = parser.Parse("""{"method":"item/agentMessage/delta","params":{"threadId":"child","itemId":"m","delta":"檢查完成"}}""", "main");
        delta.Events.Should().ContainSingle(e => e is AgentToolOutputEvent);
        delta.Events.OfType<AgentTextDeltaEvent>().Should().BeEmpty();
        parser.Parse("""{"method":"item/completed","params":{"threadId":"child","item":{"type":"agentMessage","id":"m","text":"檢查完成"}}}""", "main");
        var finish = parser.Parse("""{"method":"turn/completed","params":{"threadId":"child","turn":{"status":"completed"}}}""", "main");
        finish.TurnCompleted.Should().BeNull();
        finish.Events.OfType<AgentToolCompletedEvent>().Single().Output.Should().Be("檢查完成");
        var duplicate = parser.Parse("""{"method":"item/started","params":{"threadId":"main","item":{"id":"s2","type":"subAgentActivity","kind":"completed","agentThreadId":"child","agentPath":"/root/check"}}}""", "main");
        duplicate.Events.OfType<AgentToolCompletedEvent>().Should().BeEmpty();
    }

    [Fact]
    public void 目標事件保留預算與用量_清除另外通知()
    {
        var parser = new CodexEventParser();
        var result = parser.Parse("""{"method":"thread/goal/updated","params":{"threadId":"main","goal":{"objective":"完成","status":"paused","tokensUsed":12,"tokenBudget":100}}}""", "main");
        result.Events.OfType<AgentCodexGoalEvent>().Single().Goal!.Value<long>("tokensUsed").Should().Be(12);
        parser.GoalActive.Should().BeFalse();
        parser.Parse("""{"method":"thread/goal/cleared","params":{"threadId":"main"}}""", "main").Events
            .OfType<AgentCodexGoalEvent>().Single().Goal.Should().BeNull();
    }
}
