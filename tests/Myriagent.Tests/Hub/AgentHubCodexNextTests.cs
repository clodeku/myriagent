using Myriagent.Agents;
using Myriagent.Cli;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

public sealed class AgentHubCodexNextTests
{
    [Fact]
    public async Task 表單驗證失敗仍能重填_不先消耗請求()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Codex);
        session.OnValidateQuestion = (_, a) => a["n"][0] == "2" ? null : "請填 2";
        h.Raise(session, new AgentQuestionEvent("form", [new AgentQuestion { Id = "n", Text = "數量" }], false, Source: "MCP：測試"));
        var item = h.Items(id).Single(i => i.Kind == AgentItemKind.Question);
        h.Command(h.Main, "answer", new { conversationId = id, itemId = item.Id, answers = new { n = new[] { "a" } } });
        await h.WaitUntilAsync(() => h.Items(id).Single(i => i.Id == item.Id).Output == "請填 2", "驗證錯誤留在卡片");
        item.Status.Should().Be(AgentItemStatus.Waiting);
        session.Answers.Count.Should().Be(0);
        h.Command(h.Main, "answer", new { conversationId = id, itemId = item.Id, answers = new { n = new[] { "2" } } });
        await h.WaitUntilAsync(() => session.Answers.Count == 1, "修正後送出");
        item.Status.Should().Be(AgentItemStatus.Done);
        item.Output.Should().BeNull();
    }

    [Fact]
    public async Task 規劃內容與差異各自更新_模式保存到下一輪()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Codex);
        h.Raise(session, new AgentCodexPlanEvent("plan", "草稿", false));
        h.Raise(session, new AgentCodexPlanEvent("plan", "正式計畫", true));
        h.Raise(session, new AgentTurnDiffEvent("t", "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-a\n+b"));
        h.CompleteTurn(session);
        h.Items(id).Single(i => i.ToolKind == "plan").Text.Should().Be("正式計畫");
        h.Items(id).Single(i => i.ToolName == "整輪變更彙整").Status.Should().Be(AgentItemStatus.Done);
        session.OnCodexControl = (_, _) => Task.FromResult(new JObject { ["mode"] = "plan" });
        h.Command(h.Main, "codexControl", new { conversationId = id, requestId = "mode", action = "mode", mode = "plan" });
        await h.Main.WaitForAsync("codexControl");
        h.Command(h.Main, "send", new { conversationId = id, text = "先規劃" });
        await h.WaitUntilAsync(() => session.Sent.Count == 2, "下一輪帶模式");
        session.Sent.All[^1].Settings.CodexMode.Should().Be("plan");
    }

    [Fact]
    public async Task 診斷使用所選帳戶的環境_不送模型請求()
    {
        using var h = new HubHarness();
        Dictionary<string, string?>? environment = null;
        h.Ui.Invoke(() =>
        {
            h.Accounts.ResolveLauncher = _ => new AgentLauncher("fake", []);
            h.Hub.RunCodexDoctor = (_, args, configure, _, _) =>
            {
                args.Should().Equal("doctor", "--json", "--network-only");
                environment = new() { ["OPENAI_API_KEY"] = "不應繼承" };
                configure(environment);
                return Task.FromResult(new CliRunResult(0, """{"schemaVersion":1,"overallStatus":"ok","checks":{}}""", "", false));
            };
        });
        h.Command(h.Main, "codexDoctor", new { accountId = h.Codex.Id, requestId = "doctor", networkOnly = true });
        var reply = await h.Main.WaitForAsync("codexDoctor");
        reply.Value<string>("error").Should().BeNull();
        environment!["CODEX_HOME"].Should().Be(h.Codex.ConfigDirectory);
        environment.Should().NotContainKey("OPENAI_API_KEY");
    }
}
