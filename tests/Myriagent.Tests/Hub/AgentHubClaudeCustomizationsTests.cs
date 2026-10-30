using Myriagent.Agents;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// Claude 對話的主代理（setAgent）、輸出風格（setOutputStyle）與設定頁的備援模型（claudeFallbackModel）：
/// 清單來自 CLI 的 initialize（AgentCustomizationsEvent），啟動參數經 AgentSessionSettings 帶給會話
/// </summary>
public class AgentHubClaudeCustomizationsTests
{
    private static readonly AgentCustomizationsEvent Reported = new(
        [new AgentNamedOption("Explore", "探索"), new AgentNamedOption("probe-agent", "探針代理")],
        ["default", "Concise", "Explanatory"]);

    [Fact]
    public void CLI回報的清單存成帳戶的_標頭帶給這個帳戶的對話()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.Raise(session, Reported);

        h.OnUi(() => h.Settings.ClaudeAgents[h.Claude.Id].Select(a => a.Value).ToList()).Should().Equal("Explore", "probe-agent");
        h.OnUi(() => h.Settings.OutputStyles[h.Claude.Id]).Should().Equal("default", "Concise", "Explanatory");

        var other = h.NewConversation(h.Main, h.Claude);
        var header = (JObject)h.Main.Last("conversation", other)!["header"]!;
        header.Value<string>("agent").Should().Be("default");
        header["agents"]!.Select(a => a.Value<string>("value")).Should().Equal("default", "Explore", "probe-agent");
        header.Value<string>("outputStyle").Should().BeNull();
        header["outputStyles"]!.Values<string>().Should().Equal("default", "Concise", "Explanatory");

        var codex = h.NewConversation(h.Main, h.Codex);
        var codexHeader = (JObject)h.Main.Last("conversation", codex)!["header"]!;
        codexHeader.ContainsKey("outputStyles").Should().BeFalse("只有 Claude");
        codexHeader.ContainsKey("agent").Should().BeFalse();
    }

    [Fact]
    public void 選主代理與輸出風格_下一次送出帶給會話_風格立即套用到程序()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.Raise(session, Reported);
        h.CompleteTurn(session);

        h.Command(h.Main, "setAgent", new { conversationId = id, agent = "probe-agent" });
        h.Command(h.Main, "setOutputStyle", new { conversationId = id, style = "Concise" });
        h.Conversation(id)!.Agent.Should().Be("probe-agent");
        h.Conversation(id)!.OutputStyle.Should().Be("Concise");
        session.StyleChanges.All.Should().Equal("Concise");

        h.Send(h.Main, id, "第二則");
        var settings = session.Sent.All[^1].Settings;
        settings.Agent.Should().Be("probe-agent");
        settings.OutputStyle.Should().Be("Concise");

        // 不在清單裡的不收
        var mark = h.Main.Mark();
        h.Command(h.Main, "setAgent", new { conversationId = id, agent = "missing-agent" });
        h.Command(h.Main, "setOutputStyle", new { conversationId = id, style = "Pirate" });
        h.Conversation(id)!.Agent.Should().Be("probe-agent");
        h.Conversation(id)!.OutputStyle.Should().Be("Concise");
        h.Main.Messages("toast", mark).Should().HaveCount(2);

        // 回到預設：照帳戶設定、不另外對程序下指令（下次啟動才準）
        h.CompleteTurn(session);
        h.Command(h.Main, "setAgent", new { conversationId = id, agent = "default" });
        h.Command(h.Main, "setOutputStyle", new { conversationId = id, style = (string?)null });
        h.Conversation(id)!.Agent.Should().BeNull();
        h.Conversation(id)!.OutputStyle.Should().BeNull();
        session.StyleChanges.All.Should().Equal("Concise");
        h.Send(h.Main, id, "第三則");
        session.Sent.All[^1].Settings.Agent.Should().BeNull();
        session.Sent.All[^1].Settings.OutputStyle.Should().BeNull();
    }

    [Fact]
    public void 換到沒有這個代理的帳戶_啟動不帶_換到另一家清掉()
    {
        using var h = new HubHarness();
        var (id, session) = h.StartConversation(h.Main, h.Claude);
        h.Raise(session, Reported);
        h.CompleteTurn(session);
        h.Command(h.Main, "setAgent", new { conversationId = id, agent = "probe-agent" });
        h.Command(h.Main, "setOutputStyle", new { conversationId = id, style = "Explanatory" });
        h.OnUi(() =>
        {
            h.Settings.ClaudeAgents[h.Claude2.Id] = [new AgentNamedOption("Explore", null)];
            h.Settings.OutputStyles[h.Claude2.Id] = ["default", "Explanatory"];
            return 0;
        });

        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Claude2.Id });
        h.Conversation(id)!.Agent.Should().Be("probe-agent", "同一家換帳戶照留（換回來還在）");
        h.Send(h.Main, id, "換帳戶之後");
        var settings = h.Sessions[^1].Sent.All[^1].Settings;
        settings.Agent.Should().BeNull("新帳戶沒有回報這個代理：不帶");
        settings.OutputStyle.Should().Be("Explanatory");

        h.CompleteTurn(h.Sessions[^1]);
        h.Command(h.Main, "setAccount", new { conversationId = id, accountId = h.Codex.Id });
        h.Conversation(id)!.Agent.Should().BeNull();
        h.Conversation(id)!.OutputStyle.Should().BeNull();
    }

    [Fact]
    public void 備援模型_設定頁的清單裡才收_送出時帶給Claude()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "setting", new { key = "claudeFallbackModel", value = "sonnet" });
        h.OnUi(() => h.Settings.ClaudeFallbackModel).Should().Be("sonnet");
        h.Main.Last("settings")!["settings"]!.Value<string>("claudeFallbackModel").Should().Be("sonnet");

        h.Command(h.Main, "setting", new { key = "claudeFallbackModel", value = "not-a-model" });
        h.OnUi(() => h.Settings.ClaudeFallbackModel).Should().Be("sonnet", "不在清單裡的不收");

        var (_, session) = h.StartConversation(h.Main, h.Claude);
        session.Sent.All[^1].Settings.FallbackModel.Should().Be("sonnet");
        var (_, codex) = h.StartConversation(h.Main, h.Codex);
        codex.Sent.All[^1].Settings.FallbackModel.Should().BeNull("只有 Claude");

        h.Command(h.Main, "setting", new { key = "claudeFallbackModel", value = "" });
        h.OnUi(() => h.Settings.ClaudeFallbackModel).Should().BeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("default", null)]
    [InlineData("claude", null)]
    [InlineData(" probe-agent ", "probe-agent")]
    public void NormalizeAgent(string? input, string? expected) => ClaudeCustomizationRules.NormalizeAgent(input).Should().Be(expected);

    [Fact]
    public void ForLaunch_帳戶回報過清單時只帶清單裡的()
    {
        ClaudeCustomizationRules.AgentForLaunch("x", null).Should().Be("x", "還沒回報過照帶");
        ClaudeCustomizationRules.AgentForLaunch("x", [new AgentNamedOption("y", null)]).Should().BeNull();
        ClaudeCustomizationRules.AgentForLaunch("bad name", null).Should().BeNull();
        ClaudeCustomizationRules.StyleForLaunch("Concise", ["default", "Concise"]).Should().Be("Concise");
        ClaudeCustomizationRules.StyleForLaunch("Pirate", ["default"]).Should().BeNull();
        ClaudeCustomizationRules.StyleForLaunch(" ", null).Should().BeNull();
        ClaudeCustomizationRules.TryNormalizeFallback("none", ["sonnet"], out var none).Should().BeTrue();
        none.Should().BeNull();
        ClaudeCustomizationRules.TryNormalizeFallback("opus", ["sonnet"], out _).Should().BeFalse();
    }
}
