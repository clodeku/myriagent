using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 代理主動通知使用者（PushNotification）、安全機制換模型（model_refusal_fallback、model_refusal_no_fallback）、撤回的回覆（retracted_message_uuids、supersedes）、
/// CLI 的說明（informational）：ClaudeEventRules 與 ClaudeStreamParser 的接線。形狀取自 Agent SDK 0.3.332 的型別與實際的逐字稿
/// </summary>
public sealed class ClaudeRefusalAndNotifyTests
{
    private static JObject J(string json) => JObject.Parse(json);

    private static List<AgentEvent> Parse(ClaudeStreamParser parser, params string[] lines) => lines.SelectMany(l => parser.Parse(l).Events).ToList();

    private static string ToolUse(string id, string message, string parent = "null")
        => "{\"type\":\"assistant\",\"uuid\":\"u-" + id + "\",\"message\":{\"id\":\"msg_" + id + "\",\"content\":[{\"type\":\"tool_use\",\"id\":\"" + id
           + "\",\"name\":\"PushNotification\",\"input\":{\"message\":\"" + message + "\",\"status\":\"proactive\"}}]},\"parent_tool_use_id\":" + parent + "}";

    [Fact]
    public void 通知的內容_空白併成一個_太長截短_沒有內容不通知()
    {
        ClaudeEventRules.NotifyMessage(J("""{"message":"  網站要人工驗證：\n 請到店 1  的 Chrome ","status":"proactive"}""")).Should().Be("網站要人工驗證： 請到店 1 的 Chrome");
        ClaudeEventRules.NotifyMessage(new JObject { ["message"] = new string('字', 600) }).Should().Be(new string('字', ClaudeEventRules.MaxNotifyLength) + "…");
        var pair = new string('字', ClaudeEventRules.MaxNotifyLength - 1) + char.ConvertFromUtf32(0x1F600) + "x";
        ClaudeEventRules.NotifyMessage(new JObject { ["message"] = pair }).Should().Be(new string('字', ClaudeEventRules.MaxNotifyLength - 1) + "…", "不切開代理字元對");
        ClaudeEventRules.NotifyMessage(J("""{"message":"  "}""")).Should().BeNull();
        ClaudeEventRules.NotifyMessage(J("""{"message":5}""")).Should().BeNull();
        ClaudeEventRules.NotifyMessage(null).Should().BeNull();
    }

    [Fact]
    public void 主對話的PushNotification_畫工具卡並通知一次_串流與assistant各看到一次也只通知一次()
    {
        var parser = new ClaudeStreamParser();
        var events = Parse(parser, ToolUse("toolu_P", "請到店 1 完成驗證"), ToolUse("toolu_P", "請到店 1 完成驗證"));
        events.OfType<AgentNotifyUserEvent>().Should().Equal(new AgentNotifyUserEvent("請到店 1 完成驗證"));
        var card = events.OfType<AgentToolStartedEvent>().Single();
        (card.ToolName, card.Title, card.Detail).Should().Be(("通知使用者", "請到店 1 完成驗證", (string?)null));
        events.FindIndex(e => e is AgentToolStartedEvent).Should().BeLessThan(events.FindIndex(e => e is AgentNotifyUserEvent));
        Parse(parser, ToolUse("toolu_Q", "第二則")).OfType<AgentNotifyUserEvent>().Should().ContainSingle();
    }

    [Fact]
    public void 子代理的PushNotification_也通知_背景子代理的卡片已經有結果也一樣()
    {
        var parser = new ClaudeStreamParser();
        Parse(parser, ToolUse("toolu_S", "子代理要你看", "\"toolu_agent\"")).Should().Equal(new AgentNotifyUserEvent("子代理要你看"));
        Parse(parser, ToolUse("toolu_S", "子代理要你看", "\"toolu_agent\"")).Should().BeEmpty("同一個工具只通知一次");
    }

    [Fact]
    public void 安全機制換模型_整個對話_只有子代理_回到原本的_沒有接手的()
    {
        var session = ClaudeEventRules.RefusalFallback(J("""{"trigger":"refusal","direction":"retry","scope":"session","original_model":"claude-fable-5-2","fallback_model":"claude-opus-4-8","api_refusal_category":"cyber","content":"x"}"""));
        (session.OriginalModel, session.FallbackModel, session.SessionSwitched, session.Reverted).Should().Be(("claude-fable-5-2", "claude-opus-4-8", true, false));
        session.Notice.Should().StartWith("Claude 的安全機制攔下了這次回覆（類別：資安），改用 Opus 4.8 接著回答（原本是 Fable 5.2）。").And.Contain("換回");

        ClaudeEventRules.RefusalFallback(J("""{"original_model":"a","fallback_model":"b"}""")).SessionSwitched.Should().BeTrue("舊版沒有 scope，當成整個對話");
        var local = ClaudeEventRules.RefusalFallback(J("""{"direction":"retry","scope":"local","original_model":"claude-fable-5-2","fallback_model":"claude-opus-4-8","api_refusal_category":"new-kind"}"""));
        local.SessionSwitched.Should().BeFalse();
        local.Notice.Should().Contain("子代理或旁支提問").And.Contain("（類別：new-kind）").And.Contain("對話本身的模型沒有變");

        var reverted = ClaudeEventRules.RefusalFallback(J("""{"direction":"revert","original_model":"claude-fable-5-2","fallback_model":"claude-opus-4-8"}"""));
        (reverted.SessionSwitched, reverted.Reverted, reverted.FallbackModel).Should().Be((false, true, (string?)null));
        reverted.Notice.Should().Be("Claude 已換回原本的模型 Fable 5.2");

        var none = ClaudeEventRules.RefusalWithoutFallback(J("""{"original_model":"claude-fable-5-2","api_refusal_category":null}"""));
        (none.SessionSwitched, none.Reverted, none.FallbackModel).Should().Be((false, false, (string?)null));
        none.Notice.Should().Contain("沒有可以接手的模型");
    }

    [Theory]
    [InlineData("claude-fable-5-2", "Fable 5.2")]
    [InlineData("claude-opus-4-8[1m]", "Opus 4.8")]
    [InlineData("claude-haiku-5-5-20261001", "Haiku 5.5")]
    [InlineData("claude-sonnet-6", "Sonnet 6")]
    [InlineData("opus", "opus")]
    [InlineData(" us.anthropic.claude-opus-5 ", "us.anthropic.claude-opus-5")]
    [InlineData(null, "原本的模型")]
    public void 模型的顯示名稱(string? id, string expected) => ClaudeEventRules.ModelLabel(id).Should().Be(expected);

    [Fact]
    public void 同一個模型_不看視窗後綴與大小寫()
    {
        ClaudeEventRules.SameModel("claude-opus-4-8[1m]", "Claude-Opus-4-8").Should().BeTrue();
        ClaudeEventRules.SameModel("claude-opus-4-8", "claude-fable-5-2").Should().BeFalse();
        ClaudeEventRules.SameModel(null, null).Should().BeTrue();
    }

    [Fact]
    public void 說明_notice_warning_suggestion才顯示()
    {
        ClaudeEventRules.Informational(J("""{"content":"Hook blocked it","level":"warning"}""")).Should().Be("Claude 說明：Hook blocked it");
        ClaudeEventRules.Informational(J("""{"content":"x","level":"notice"}""")).Should().NotBeNull();
        ClaudeEventRules.Informational(J("""{"content":"x","level":"info"}""")).Should().BeNull("只在終端機的逐字稿模式顯示");
        ClaudeEventRules.Informational(J("""{"level":"warning"}""")).Should().BeNull();
        new ClaudeStreamParser().Parse("""{"type":"system","subtype":"informational","content":"stopped","level":"notice","uuid":"u"}""").Events
            .Should().Equal(new AgentNoticeEvent("Claude 說明：stopped", IsError: false));
    }

    private static string Event(string inner) => "{\"type\":\"stream_event\",\"event\":" + inner + ",\"parent_tool_use_id\":null}";

    private static string Assistant(string messageId, string uuid, string block)
        => "{\"type\":\"assistant\",\"uuid\":\"" + uuid + "\",\"message\":{\"id\":\"" + messageId + "\",\"content\":[" + block + "]},\"parent_tool_use_id\":null}";

    /// <summary>一則訊息的串流：思考與文字兩個區塊，完整的 assistant 各一則（帶 uuid，比 content_block_stop 先到）</summary>
    private static string[] Streamed(string messageId, string model) => new[]
    {
        Event("{\"type\":\"message_start\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"" + model + "\"}}"),
        Event("""{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}"""),
        Event("""{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"想"}}"""),
        Assistant(messageId, messageId + "-t", """{"type":"thinking","thinking":"想"}"""),
        Event("""{"type":"content_block_stop","index":0}"""),
        Event("""{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""),
        Event("""{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"說"}}"""),
        Assistant(messageId, messageId + "-x", """{"type":"text","text":"說"}"""),
        Event("""{"type":"content_block_stop","index":1}"""),
    };

    [Fact]
    public void 撤回的訊息_依uuid找到畫面的思考與文字_不認得的略過_一輪結束就清掉()
    {
        var parser = new ClaudeStreamParser();
        Parse(parser, Streamed("msg_A", "claude-fable-5-2")).OfType<AgentReplyModelEvent>().Should().Equal(new AgentReplyModelEvent("claude-fable-5-2"));
        var events = Parse(parser, """{"type":"system","subtype":"model_refusal_fallback","scope":"session","original_model":"claude-fable-5-2","fallback_model":"claude-opus-4-8","retracted_message_uuids":["msg_A-t","msg_A-x","nope"]}""");
        events.OfType<AgentBlocksRetractedEvent>().Single().BlockIds.Should().Equal("msg_A:0", "msg_A:1");
        events.FindIndex(e => e is AgentBlocksRetractedEvent).Should().BeLessThan(events.FindIndex(e => e is AgentModelRefusalEvent));
        Parse(parser, Streamed("msg_B", "claude-opus-4-8")).OfType<AgentReplyModelEvent>().Should().Equal(new AgentReplyModelEvent("claude-opus-4-8"));
        Parse(parser, Streamed("msg_C", "claude-opus-4-8")).OfType<AgentReplyModelEvent>().Should().BeEmpty("和上一則同一個模型不發");

        // 這則取代了前一則（工具呼叫讀不懂而重答）
        Parse(parser, """{"type":"assistant","uuid":"msg_D-x","supersedes":["msg_C-x"],"message":{"id":"msg_D","content":[{"type":"text","text":"重答"}]},"parent_tool_use_id":null}""")
            .OfType<AgentBlocksRetractedEvent>().Single().BlockIds.Should().Equal("msg_C:1");

        Parse(parser, """{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"s"}""");
        Parse(parser, """{"type":"system","subtype":"model_refusal_fallback","fallback_model":"b","retracted_message_uuids":["msg_B-x"]}""")
            .OfType<AgentBlocksRetractedEvent>().Should().BeEmpty("前一輪的不再撤回");
    }

    [Fact]
    public void 子代理的串流_不影響主對話的模型()
    {
        new ClaudeStreamParser().Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_S","model":"claude-haiku-5-5"}},"parent_tool_use_id":"toolu_A"}""")
            .Events.Should().BeEmpty();
    }
}
