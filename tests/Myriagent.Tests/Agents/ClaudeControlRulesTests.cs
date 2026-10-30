using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>Claude 的 get_task_output、cancel_async_message、rewind_files（回應的形狀取自 2.1.310 探針 tools/probes/claude_cli6_probe.py）</summary>
public class ClaudeControlRulesTests
{
    [Fact]
    public void TaskOutput_請求與回應_超過上限只留尾端()
    {
        var request = JObject.Parse(ClaudeControlRules.BuildTaskOutputRequest("out0", "bv1d7xyjl"));
        request["request"]!["subtype"]!.ToString().Should().Be("get_task_output");
        request["request"]!["task_id"]!.ToString().Should().Be("bv1d7xyjl");

        var output = ClaudeControlRules.ParseTaskOutput(JObject.Parse("""{ "output": "\r\nPing 127.0.0.1\r\n", "total_bytes": 272, "truncated": false }"""));
        output.Should().Be(new AgentTaskOutput("\r\nPing 127.0.0.1\r\n", 272, false, null));

        var big = new string('a', ClaudeControlRules.MaxTaskOutputChars) + "END";
        var tail = ClaudeControlRules.ParseTaskOutput(new JObject { ["output"] = big, ["total_bytes"] = big.Length });
        tail.Output!.Should().EndWith("END").And.HaveLength(ClaudeControlRules.MaxTaskOutputChars);
        tail.Truncated.Should().BeTrue();
        ClaudeControlRules.ParseTaskOutput(null).Output.Should().BeEmpty();
    }

    [Fact]
    public void HasTaskOutput_只有指令與Monitor()
    {
        ClaudeStreamRules.HasTaskOutput("local_bash", "bash").Should().BeTrue();
        ClaudeStreamRules.HasTaskOutput("monitor_mcp", null).Should().BeTrue();
        ClaudeStreamRules.HasTaskOutput("local_agent", "agent").Should().BeFalse();
        ClaudeStreamRules.HasTaskOutput(null, "bash").Should().BeTrue("CLI 沒給種類：看工具卡");
        ClaudeStreamRules.HasTaskOutput(null, "agent").Should().BeFalse();
    }

    [Fact]
    public void CancelAsyncMessage_請求與回應()
    {
        var request = JObject.Parse(ClaudeControlRules.BuildCancelAsyncMessageRequest("cancel-x", "u1"));
        request["request"]!["subtype"]!.ToString().Should().Be("cancel_async_message");
        request["request"]!["message_uuid"]!.ToString().Should().Be("u1");
        ClaudeControlRules.ParseCancelled(JObject.Parse("""{ "cancelled": true }""")).Should().BeTrue();
        ClaudeControlRules.ParseCancelled(JObject.Parse("""{ "cancelled": false }""")).Should().BeFalse();
        ClaudeControlRules.ParseCancelled(null).Should().BeFalse();
    }

    [Fact]
    public void RewindFiles_請求與回應()
    {
        var request = JObject.Parse(ClaudeControlRules.BuildRewindFilesRequest("dry", "u2", dryRun: true));
        request["request"]!["subtype"]!.ToString().Should().Be("rewind_files");
        request["request"]!["user_message_id"]!.ToString().Should().Be("u2");
        request["request"]!.Value<bool>("dry_run").Should().BeTrue();

        var dry = ClaudeControlRules.ParseRewind(JObject.Parse("""{ "canRewind": true, "filesChanged": ["C:\\w\\a.txt", "C:\\w\\b.txt"], "insertions": 1, "deletions": 2 }"""));
        dry.CanRewind.Should().BeTrue();
        dry.Files.Should().Equal(@"C:\w\a.txt", @"C:\w\b.txt");
        dry.Insertions.Should().Be(1);
        dry.Deletions.Should().Be(2);
        dry.Error.Should().BeNull();
        ClaudeControlRules.ParseRewind(JObject.Parse("""{ "canRewind": true, "skippedLinks": 0 }""")).Files.Should().BeEmpty();
        var off = ClaudeControlRules.ParseRewind(JObject.Parse("""{ "canRewind": false, "error": "File rewinding is not enabled." }"""));
        off.CanRewind.Should().BeFalse();
        off.Error.Should().Be("File rewinding is not enabled.");
        ClaudeControlRules.ParseRewind(null).Error.Should().NotBeNull();
    }

    [Fact]
    public void 產生標題_請求不寫回工作階段_回應去掉空白()
    {
        var request = JObject.Parse(ClaudeControlRules.BuildGenerateTitleRequest("t1", "幫我修登入"));
        request["request_id"]!.ToString().Should().Be("t1");
        request["request"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"subtype":"generate_session_title","description":"幫我修登入","persist":false}""");
        ClaudeControlRules.ParseGeneratedTitle(JObject.Parse("""{"title":"  修正登入流程 "}""")).Should().Be("修正登入流程");
        ClaudeControlRules.ParseGeneratedTitle(JObject.Parse("""{"title":" "}""")).Should().BeNull();
        ClaudeControlRules.ParseGeneratedTitle(JObject.Parse("""{"title":null}""")).Should().BeNull();
        ClaudeControlRules.ParseGeneratedTitle(null).Should().BeNull();
    }
}
