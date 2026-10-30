using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// Claude 一輪中的系統事件（ClaudeEventRules 與 ClaudeStreamParser 的接線）：備援模型、權限擋下、CLI 通知、存進記憶、子代理用量。
/// 形狀取自 2.1.310 的探針（tools/probes/claude_cli7_probe.py --mode events；memory_saved 在 -p 沒有實測到，照執行檔說明的欄位）
/// </summary>
public sealed class ClaudeEventRulesTests
{
    private static JObject J(string json) => JObject.Parse(json);

    [Fact]
    public void 備援模型_寫出主模型_原因與接手的模型()
    {
        ClaudeEventRules.ModelFallback(J("""{"type":"system","subtype":"model_fallback","trigger":"model_not_found","original_model":"claude-nope","fallback_model":"claude-sonnet-5-5"}"""))
            .Should().Be("主模型 claude-nope（找不到這個模型），Claude 改用備援模型 claude-sonnet-5-5 接手");
        ClaudeEventRules.ModelFallback(J("""{"trigger":"overloaded","fallback_model":"claude-haiku-5-5","content":"Switched to fallback."}"""))
            .Should().Be("主模型（過載），Claude 改用備援模型 claude-haiku-5-5 接手：Switched to fallback.");
        ClaudeEventRules.ModelFallback(J("""{"original_model":"a"}""")).Should().Be("主模型不能用，Claude 改用備援模型接手");
    }

    [Fact]
    public void 權限擋下_依來源寫出誰擋的_帶說明()
    {
        ClaudeEventRules.PermissionDenied(J("""{"subtype":"permission_denied","tool_name":"Bash","decision_reason_type":"subcommandResults","message":"Permission to use Bash with command rm -rf x has been denied."}"""))
            .Should().Be("權限規則擋下了「Bash」：Permission to use Bash with command rm -rf x has been denied.");
        ClaudeEventRules.PermissionDenied(J("""{"tool_name":"Write","decision_reason_type":"hook","decision_reason":"blocked by policy"}"""))
            .Should().Be("hook 設定擋下了「Write」：blocked by policy");
        ClaudeEventRules.PermissionDenied(J("""{"decision_reason_type":"weird"}""")).Should().Be("Claude 擋下了「工具」");
    }

    [Fact]
    public void 通知_沒有文字不顯示_color是error當錯誤()
    {
        ClaudeEventRules.Notification(J("""{"key":"k","priority":"high"}""")).Should().BeNull();
        ClaudeEventRules.Notification(J("""{"key":"fast","text":"Fast mode credits ran out","color":"warning"}"""))
            .Should().Be(("Claude 通知：Fast mode credits ran out", false));
        ClaudeEventRules.Notification(J("""{"text":"Model blocked by policy","color":"error"}"""))!.Value.IsError.Should().BeTrue();
    }

    [Fact]
    public void 存進記憶_只列檔名_去重_最多五個()
    {
        ClaudeEventRules.MemorySaved(J("""{"writtenPaths":["C:\\m\\a.md","C:\\m\\A.md","/x/b.md","c.md","d.md","e.md","f.md"]}"""))
            .Should().Be("Claude 已存進記憶：a.md、b.md、c.md、d.md、e.md");
        ClaudeEventRules.MemorySaved(J("""{"writtenPaths":[]}""")).Should().Be("Claude 已把這件事存進記憶");
        ClaudeEventRules.MemorySaved(J("""{}""")).Should().Be("Claude 已把這件事存進記憶");
    }

    [Fact]
    public void 子代理用量_要有tool_use_id與usage_結束時沒有最近的工具()
    {
        var progress = ClaudeEventRules.Subagent(J("""{"subtype":"task_progress","task_id":"t1","tool_use_id":"toolu_A","usage":{"total_tokens":15500,"tool_uses":2,"duration_ms":3200},"last_tool_name":"Read"}"""), done: false);
        progress.Should().Be(new SubagentUsage("toolu_A", 15_500, 2, 3_200, "Read", false));
        ClaudeEventRules.UsageLabel(progress!).Should().Be("15.5K tokens・2 次工具");

        ClaudeEventRules.Subagent(J("""{"tool_use_id":"toolu_A","usage":{"total_tokens":20000,"tool_uses":3,"duration_ms":9000},"last_tool_name":"Read"}"""), done: true)
            .Should().Be(new SubagentUsage("toolu_A", 20_000, 3, 9_000, null, true));
        ClaudeEventRules.Subagent(J("""{"task_id":"b1","usage":{"total_tokens":1}}"""), done: false).Should().BeNull("背景指令沒有 tool_use_id");
        ClaudeEventRules.Subagent(J("""{"tool_use_id":"toolu_A"}"""), done: false).Should().BeNull();
        ClaudeEventRules.Subagent(J("""{"tool_use_id":"toolu_A","usage":{"total_tokens":-5,"tool_uses":"x"}}"""), done: false)!.TotalTokens.Should().Be(0);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1_000, "1K")]
    [InlineData(15_540, "15.5K")]
    [InlineData(2_340_000, "2.3M")]
    public void token數的寫法(long tokens, string expected) => ClaudeEventRules.FormatTokens(tokens).Should().Be(expected);

    [Fact]
    public void 解析器_系統事件變成提示與子代理用量()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"system","subtype":"model_fallback","trigger":"model_not_found","original_model":"x","fallback_model":"y"}""")
            .Events.OfType<AgentNoticeEvent>().Should().ContainSingle().Which.IsError.Should().BeFalse();
        parser.Parse("""{"type":"system","subtype":"permission_denied","tool_name":"Bash","decision_reason_type":"rule","message":"denied"}""")
            .Events.OfType<AgentNoticeEvent>().Single().Message.Should().Be("權限規則擋下了「Bash」：denied");
        parser.Parse("""{"type":"system","subtype":"notification","key":"k","text":"Hi","color":"error"}""")
            .Events.OfType<AgentNoticeEvent>().Single().IsError.Should().BeTrue();
        parser.Parse("""{"type":"system","subtype":"notification","key":"k"}""").Events.Should().BeEmpty();
        parser.Parse("""{"type":"system","subtype":"memory_saved","writtenPaths":["C:\\m\\x.md"]}""")
            .Events.OfType<AgentNoticeEvent>().Single().Message.Should().Be("Claude 已存進記憶：x.md");

        var step = parser.Parse("""{"type":"system","subtype":"task_progress","task_id":"t1","tool_use_id":"toolu_A","usage":{"total_tokens":100,"tool_uses":1,"duration_ms":10},"last_tool_name":"Glob"}""");
        step.Events.OfType<AgentSubagentUsageEvent>().Single().Usage.Should().Be(new SubagentUsage("toolu_A", 100, 1, 10, "Glob", false));
    }

    [Fact]
    public void 解析器_子代理結束的通知帶最後的用量()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"system","subtype":"task_started","task_id":"t1","tool_use_id":"toolu_A","task_type":"local_agent"}""");
        var done = parser.Parse("""{"type":"system","subtype":"task_notification","task_id":"t1","tool_use_id":"toolu_A","status":"completed","usage":{"total_tokens":4200,"tool_uses":5,"duration_ms":8000}}""");
        done.Events.OfType<AgentSubagentUsageEvent>().Single().Usage.Should().Be(new SubagentUsage("toolu_A", 4_200, 5, 8_000, null, true));
    }
}
