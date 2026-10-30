using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 儀表板的 MCP（AgentHub.AccountMcp.cs）：以帳戶為單位，每個 Claude 與 Codex 帳戶開一個檢查用的會話（中立的資料夾、不送訊息），問完就關；
/// 授權時讓它開著、問到連上為止，之後對這個帳戶開著的對話送重新連線
/// </summary>
public class AgentHubAccountMcpTests
{
    private static readonly AgentMcpServer[] Servers =
    {
        new("github", "connected", null, "http", "user", 12),
        new("notion", "needs-auth", null, "http", "claudeai", null),
    };

    private static JObject Entry(JObject message, string accountId) => (JObject)message["mcp"]!["accounts"]![accountId]!;

    private static bool Idle(JObject message, params string[] accountIds)
        => accountIds.All(id => message["mcp"]!["accounts"]![id] is JObject e && !e.Value<bool>("checking") && e.Value<string>("authorizing") == null);

    private sealed class Flag
    {
        private volatile bool value;

        public bool Value
        {
            get => value;
            set => this.value = value;
        }
    }

    [Fact]
    public async Task mcpCheck_每個Claude與Codex帳戶開一個檢查用的程序_問完就關_5分鐘內不重查_force才重查()
    {
        using var h = new HubHarness();
        h.OnSessionCreated = s => s.McpStatus = () => new AgentMcpStatusResult(Servers, null);

        var mark = h.Main.Mark();
        h.Command(h.Main, "mcpCheck");
        var message = await h.Main.WaitForAsync("mcp", m => Idle(m, h.Claude.Id, h.Claude2.Id, h.Codex.Id), mark);
        ((JObject)message["mcp"]!["accounts"]!).Properties().Select(p => p.Name).Should().BeEquivalentTo(new[] { h.Claude.Id, h.Claude2.Id, h.Codex.Id }, "Claude 與 Codex 都有 MCP");
        Entry(message, h.Claude.Id)["servers"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be(
            """[{"name":"github","status":"connected","source":"user","transport":"http","tools":12},{"name":"notion","status":"needs-auth","source":"claudeai","transport":"http"}]""");
        Entry(message, h.Claude.Id).Value<long?>("checkedAt").Should().NotBeNull();

        var inspectors = h.Sessions.ToList();
        inspectors.Should().HaveCount(3, "每個 Claude 與 Codex 帳戶一個檢查用的會話");
        inspectors.Should().OnlyContain(s => s.Closes == 1 && s.Disposed, "問完就關");
        var settings = inspectors[0].McpStatusSettings.All.Single();
        settings.SessionStarted.Should().BeFalse();
        Path.GetFileName(settings.WorkingDirectory).Should().Be("mcp-check", "中立的資料夾（沒有專案層的 .mcp.json）");
        settings.Instructions.Should().BeEmpty();

        mark = h.Main.Mark();
        h.Command(h.Main, "mcpCheck");
        await h.Main.WaitForAsync("mcp", since: mark);
        h.Sessions.Should().HaveCount(3, "5 分鐘內查過，只重送結果");

        mark = h.Main.Mark();
        h.Command(h.Main, "mcpCheck", new { force = true });
        await h.Main.WaitForAsync("mcp", m => Idle(m, h.Claude.Id, h.Claude2.Id, h.Codex.Id), mark);
        h.Sessions.Should().HaveCount(6, "重新檢查一律重查");
    }

    [Fact]
    public async Task 查不到_留下原因_精簡模式的帳戶不查()
    {
        using var h = new HubHarness();
        h.Ui.Invoke(() => h.Claude2.SafeMode = true);
        h.OnSessionCreated = s => s.McpStatus = () => new AgentMcpStatusResult(null, "這個版本的 Claude 不回報 MCP 狀態");

        var mark = h.Main.Mark();
        h.Command(h.Main, "mcpCheck");
        var message = await h.Main.WaitForAsync("mcp", m => Idle(m, h.Claude.Id), mark);
        Entry(message, h.Claude.Id).Value<string>("error").Should().Be("這個版本的 Claude 不回報 MCP 狀態");
        Entry(message, h.Claude.Id)["servers"]!.Type.Should().Be(JTokenType.Null, "還沒查到過");
        Entry(message, h.Claude2.Id).Value<bool>("safeMode").Should().BeTrue();
        h.Sessions.Should().HaveCount(2, "Claude 的精簡模式不查，另一個 Claude 與 Codex 都查");
    }

    [Fact]
    public async Task 授權_在瀏覽器開授權頁_問到連上就停_這個帳戶開著的對話一起重新連線()
    {
        using var h = new HubHarness();
        h.Hub.McpAuthPollInterval = TimeSpan.FromMilliseconds(20);
        var authed = new Flag();
        h.OnSessionCreated = s =>
        {
            s.McpStatus = () => new AgentMcpStatusResult(new[] { new AgentMcpServer("notion", authed.Value ? "connected" : "needs-auth", null, "http", "claudeai", authed.Value ? 3 : null) }, null);
            s.OnMcpAction = (_, action) => action == AgentMcpAction.Authenticate
                ? new AgentMcpActionResult(true, null, "http://127.0.0.1:9/authorize?state=x")
                : new AgentMcpActionResult(true, null);
        };
        var (_, conversation) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(conversation);
        var (_, otherAccount) = h.StartConversation(h.Main, h.Claude2);
        h.CompleteTurn(otherAccount);
        h.Command(h.Main, "mcpCheck");
        await h.Main.WaitForAsync("mcp", m => Idle(m, h.Claude.Id, h.Claude2.Id, h.Codex.Id));

        var before = h.Sessions.Count;
        var mark = h.Main.Mark();
        h.Command(h.Main, "mcpAction", new { accountId = h.Claude.Id, server = "notion", action = "authenticate" });
        await h.Main.WaitForAsync("toast", m => m.Value<string>("text") == "已在瀏覽器開啟 MCP 伺服器「notion」的授權頁，完成後這裡會自動更新", mark);
        h.Host.Opened.All.Should().Contain("http://127.0.0.1:9/authorize?state=x");
        (await h.Main.WaitForAsync("mcp", since: mark)).Let(m => Entry(m, h.Claude.Id).Value<string>("authorizing")).Should().Be("notion");

        authed.Value = true;
        var done = await h.Main.WaitForAsync("toast", m => m.Value<string>("text")!.Contains("已授權"), mark, timeoutMs: 5000);
        done.Value<string>("text").Should().Be("MCP 伺服器「notion」已授權，開著的 1 個對話已重新連線");
        conversation.McpActions.All.Should().Equal(("notion", AgentMcpAction.Reconnect));
        otherAccount.McpActions.All.Should().BeEmpty("別的帳戶的對話不動");
        var inspector = h.Sessions[before];
        inspector.McpActions.All.Should().Equal(("notion", AgentMcpAction.Authenticate));
        var last = await h.Main.WaitForAsync("mcp", m => Idle(m, h.Claude.Id), mark);
        Entry(last, h.Claude.Id)["servers"]![0]!.Value<string>("status").Should().Be("connected");
        await WaitUntil(() => inspector.Closes == 1, "授權完就關掉檢查用的程序");
    }

    [Fact]
    public async Task 重新連線還是連不上_提示原因_不認得的伺服器與動作不做()
    {
        using var h = new HubHarness();
        h.OnSessionCreated = s =>
        {
            s.McpStatus = () => new AgentMcpStatusResult(new[] { new AgentMcpServer("broken", "failed", "Connection closed", "stdio", "user", null) }, null);
            s.OnMcpAction = (_, _) => new AgentMcpActionResult(false, "Connection closed");
        };
        h.Command(h.Main, "mcpCheck");
        await h.Main.WaitForAsync("mcp", m => Idle(m, h.Claude.Id));
        var before = h.Sessions.Count;

        var mark = h.Main.Mark();
        h.Command(h.Main, "mcpAction", new { accountId = h.Claude.Id, server = "broken", action = "reconnect" });
        (await h.Main.WaitForAsync("toast", since: mark)).Value<string>("text").Should().Be("MCP 伺服器「broken」還是連不上：Connection closed");

        mark = h.Main.Mark();
        h.Command(h.Main, "mcpAction", new { accountId = h.Claude.Id, server = "other", action = "reconnect" });
        (await h.Main.WaitForAsync("toast", since: mark)).Value<string>("text").Should().Be("找不到這個 MCP 伺服器，請先重新檢查");
        h.Command(h.Main, "mcpAction", new { accountId = h.Claude.Id, server = "broken", action = "disable" });
        h.Command(h.Main, "mcpAction", new { accountId = h.Codex.Id, server = "broken", action = "disable" });
        h.Sessions.Should().HaveCount(before + 1, "只有第一個重新連線開了檢查用的程序");
    }

    private static async Task WaitUntil(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("沒有等到：" + because);
            await Task.Delay(20);
        }
    }
}

internal static class AccountMcpTestExtensions
{
    public static TResult Let<T, TResult>(this T value, Func<T, TResult> map) => map(value);
}
