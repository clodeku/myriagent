using Myriagent.Agents;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>Claude 的 MCP 狀態、操作與旁支提問（回應的形狀取自 2.1.310 實錄，tools/probes/claude_cli3_probe.py）</summary>
public class ClaudeMcpRulesTests
{
    [Fact]
    public void McpStatus_讀名稱狀態錯誤傳輸方式來源與工具數()
    {
        var response = JObject.Parse("""
            {"mcpServers":[
              {"name":"good","status":"connected","serverInfo":{"name":"probe-good","version":"1.0"},"config":{"type":"stdio","command":"python"},"transport":"stdio","scope":"dynamic","source":"dynamic","tools":[{"name":"echo"},{"name":"ask_form"}]},
              {"name":"broken","status":"failed","error":"Connection closed","config":{"type":"stdio","command":"x"},"transport":"stdio","scope":"dynamic"},
              {"name":"oauth","status":"needs-auth","config":{"type":"http","url":"http://127.0.0.1/mcp"},"source":"user"},
              {"status":"connected"}
            ]}
            """);
        ClaudeMcpRules.ParseStatus(response).Should().Equal(
            new AgentMcpServer("good", "connected", null, "stdio", "dynamic", 2),
            new AgentMcpServer("broken", "failed", "Connection closed", "stdio", "dynamic", null),
            new AgentMcpServer("oauth", "needs-auth", null, "http", "user", null));
        // 外掛的 scope 是 dynamic、source 才是 plugin：來源先看 source
        ClaudeMcpRules.ParseStatus(JObject.Parse("""{"mcpServers":[{"name":"plugin:p:p","status":"connected","scope":"dynamic","source":"plugin","transport":"stdio"}]}"""))!
            .Single().Source.Should().Be("plugin");
        ClaudeMcpRules.ParseStatus(new JObject()).Should().BeNull("舊版不認得 mcp_status");
        ClaudeMcpRules.AnyPending(new[] { new AgentMcpServer("a", "pending", null, null, null, null) }).Should().BeTrue();
    }

    [Fact]
    public void 操作與狀態的請求_照SDK的欄位名稱()
    {
        Request(ClaudeMcpRules.BuildStatusRequest("mcp-1")).Should().Be("""{"subtype":"mcp_status"}""");
        Request(ClaudeMcpRules.BuildActionRequest("r", "good", AgentMcpAction.Reconnect)).Should().Be("""{"subtype":"mcp_reconnect","serverName":"good"}""");
        Request(ClaudeMcpRules.BuildActionRequest("r", "oauth", AgentMcpAction.Authenticate)).Should().Be("""{"subtype":"mcp_authenticate","serverName":"oauth"}""");
        Request(ClaudeMcpRules.BuildSideQuestionRequest("btw-1", "現在在做什麼？")).Should().Be("""{"subtype":"side_question","question":"現在在做什麼？"}""");
    }

    [Fact]
    public void 授權網址只收http與https_旁支提問的答案()
    {
        ClaudeMcpRules.ParseAuthUrl(JObject.Parse("""{"authUrl":"http://127.0.0.1:62612/authorize?response_type=code&state=x","requiresUserAction":true,"callbackPort":45621}"""))
            .Should().Be("http://127.0.0.1:62612/authorize?response_type=code&state=x");
        ClaudeMcpRules.ParseAuthUrl(JObject.Parse("""{"authUrl":"file:///C:/evil"}""")).Should().BeNull();
        ClaudeMcpRules.ParseAuthUrl(JObject.Parse("""{"requiresUserAction":false}""")).Should().BeNull();

        ClaudeMcpRules.ParseSideAnswer(JObject.Parse("""{"response":"12","synthetic":false}""")).Should().Be("12");
        ClaudeMcpRules.ParseSideAnswer(JObject.Parse("""{"response":null}""")).Should().BeNull();
    }

    [Fact]
    public void 儀表板的帳戶MCP_什麼時候重查_輸出的形狀_動作的參數()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0);
        AccountMcpRules.ShouldCheck(null, now, force: false).Should().BeTrue("沒查過");
        var state = new AccountMcpState { CheckedAt = now.AddMinutes(-4) };
        AccountMcpRules.ShouldCheck(state, now, force: false).Should().BeFalse("5 分鐘內查過");
        AccountMcpRules.ShouldCheck(state, now, force: true).Should().BeTrue();
        AccountMcpRules.ShouldCheck(new AccountMcpState { CheckedAt = now.AddMinutes(-5) }, now, force: false).Should().BeTrue();
        AccountMcpRules.ShouldCheck(new AccountMcpState { Checking = true }, now, force: true).Should().BeFalse("正在查");
        AccountMcpRules.ShouldCheck(new AccountMcpState { Authorizing = "x" }, now, force: true).Should().BeFalse("正在授權");

        AccountMcpRules.RenderAccount(null, safeMode: true, _ => 0).ToString(Newtonsoft.Json.Formatting.None).Should().Be(
            """{"checking":false,"checkedAt":null,"error":null,"authorizing":null,"safeMode":true,"servers":null}""");
        var done = new AccountMcpState
        {
            CheckedAt = now,
            Authorizing = "notion",
            Error = "line1\nline2",
            Servers = new[] { new AgentMcpServer("github", "connected", null, "http", "user", 12), new AgentMcpServer("broken", "failed", "Connection\nclosed", null, null, null) },
        };
        AccountMcpRules.RenderAccount(done, safeMode: false, _ => 1792800000000).ToString(Newtonsoft.Json.Formatting.None).Should().Be(
            """{"checking":false,"checkedAt":1792800000000,"error":"line1 line2","authorizing":"notion","safeMode":false,"servers":[{"name":"github","status":"connected","source":"user","transport":"http","tools":12},{"name":"broken","status":"failed","error":"Connection closed"}]}""");

        AccountMcpRules.ParseAction("authenticate").Should().Be(AgentMcpAction.Authenticate);
        AccountMcpRules.ParseAction("reconnect").Should().Be(AgentMcpAction.Reconnect);
        AccountMcpRules.ParseAction("disable").Should().BeNull("停用是逐資料夾的設定，儀表板不做");
        AccountMcpRules.AwaitingAuth("needs-auth").Should().BeTrue();
        AccountMcpRules.AwaitingAuth("connected").Should().BeFalse();
    }

    private static string Request(string line) => JObject.Parse(line)["request"]!.ToString(Newtonsoft.Json.Formatting.None);
}
