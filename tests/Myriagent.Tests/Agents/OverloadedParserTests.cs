using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// API 過載（529 Overloaded）而失敗的一輪標成 AgentTurnCompletedEvent.ServerBusy（中樞等一下自動重試）：
/// CLI 合成的錯誤訊息或 result 本身帶 529 都算；其他 API 錯誤、代理在一般回覆裡提到 529、被停止的一輪不算
/// </summary>
public class OverloadedParserTests
{
    /// <summary>2.1.310 實錄（tools/probes/claude_api_error_probe.py：假的 Messages API 一律回 529；uuid、時間、閘道網址換掉）</summary>
    private const string OverloadedMessage =
        """{"type":"assistant","message":{"id":"8a1c0f3e-0d4e-4f0e-9d39-4b3f6a2c1e55","container":null,"model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"content":[{"type":"text","text":"API Error: 529 Overloaded. This is a server-side issue, usually temporary — try again in a moment. If it persists, check https://status.claude.com."}],"context_management":null},"parent_tool_use_id":null,"session_id":"s","uuid":"u1","timestamp":"2026-10-24T01:02:05.311Z","error":"server_error","is_api_error_message":true,"api_error_status":529}""";

    private const string OverloadedResult =
        """{"type":"result","subtype":"success","is_error":true,"duration_ms":63,"duration_api_ms":0,"num_turns":1,"stop_reason":"stop_sequence","terminal_reason":"api_error","api_error_status":529,"result":"API Error: 529 Overloaded. This is a server-side issue, usually temporary — try again in a moment. If it persists, check https://status.claude.com.","session_id":"s","uuid":"u2"}""";

    private const string BareErrorResult = """{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"s"}""";

    [Fact]
    public void 合成的529訊息與result_這一輪失敗且標成過載_文字照常顯示()
    {
        var parser = new ClaudeStreamParser();
        var synthetic = parser.Parse(OverloadedMessage);
        synthetic.Events.OfType<AgentTextCompletedEvent>().Should().ContainSingle("錯誤文字照常顯示成回覆");
        synthetic.Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty("過載不是額度用完");

        var result = parser.Parse(OverloadedResult);
        result.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Failed);
        result.TurnCompleted.ServerBusy.Should().Be(AgentServerBusy.Overloaded);
        result.TurnCompleted.ErrorMessage.Should().StartWith("API Error: 529 Overloaded.");

        // 下一輪重新計算：沒有過載標記的失敗不算
        parser.Parse(BareErrorResult).TurnCompleted!.ServerBusy.Should().Be(AgentServerBusy.None);
    }

    [Fact]
    public void 只有result帶529_或只有合成訊息_都算()
    {
        new ClaudeStreamParser().Parse(OverloadedResult).TurnCompleted!.ServerBusy.Should().Be(AgentServerBusy.Overloaded);

        var parser = new ClaudeStreamParser();
        parser.Parse(OverloadedMessage);
        parser.Parse(BareErrorResult).TurnCompleted!.ServerBusy.Should().Be(AgentServerBusy.Overloaded, "合成訊息之後的失敗");
    }

    [Fact]
    public void 連續529的說法沒有狀態碼_以文字認()
    {
        var result = JObject.Parse(OverloadedResult);
        result.Remove("api_error_status");
        result["result"] = "API Error: Repeated 529 Overloaded errors. The API is at capacity — this is usually temporary. Try again in a moment.";
        new ClaudeStreamParser().Parse(result.ToString(Newtonsoft.Json.Formatting.None)).TurnCompleted!.ServerBusy.Should().Be(AgentServerBusy.Overloaded);

        var message = JObject.Parse(OverloadedMessage);
        message.Remove("api_error_status");
        message["error"] = "overloaded";
        ClaudeStreamRules.IsOverloadedApiError(message).Should().BeTrue("錯誤分類 overloaded");
    }

    [Fact]
    public void 其他API錯誤不算()
    {
        var parser = new ClaudeStreamParser();
        var internalError = JObject.Parse(OverloadedMessage);
        internalError["api_error_status"] = 500;
        ((JArray)internalError["message"]!["content"]!)[0]!["text"] = "API Error: 500 Internal server error. This is a server-side issue, usually temporary — try again in a moment.";
        parser.Parse(internalError.ToString(Newtonsoft.Json.Formatting.None));
        var result = JObject.Parse(OverloadedResult);
        result["api_error_status"] = 500;
        result["result"] = "API Error: 500 Internal server error. This is a server-side issue, usually temporary — try again in a moment.";
        parser.Parse(result.ToString(Newtonsoft.Json.Formatting.None)).TurnCompleted!.ServerBusy.Should().Be(AgentServerBusy.None);

        var rateLimit = JObject.Parse(OverloadedResult);
        rateLimit["api_error_status"] = 429;
        rateLimit["result"] = "Request rejected (429) · this may be a temporary capacity issue";
        new ClaudeStreamParser().Parse(rateLimit.ToString(Newtonsoft.Json.Formatting.None)).TurnCompleted!.ServerBusy.Should().Be(AgentServerBusy.None);
    }

    [Fact]
    public void 代理在一般回覆裡提到529_不算()
    {
        var parser = new ClaudeStreamParser();
        var normal = JObject.Parse(OverloadedMessage);
        normal.Remove("error");
        normal.Remove("is_api_error_message");
        normal.Remove("api_error_status");
        normal["message"]!["model"] = "claude-opus-5-5";
        parser.Parse(normal.ToString(Newtonsoft.Json.Formatting.None));
        parser.Parse(BareErrorResult).TurnCompleted!.ServerBusy.Should().Be(AgentServerBusy.None);

        // 正常結束的 result 文字提到 529 也不算
        var success = JObject.Parse(OverloadedResult);
        success.Remove("api_error_status");
        success["is_error"] = false;
        ClaudeStreamRules.IsOverloadedApiError(success).Should().BeFalse();
        new ClaudeStreamParser().Parse(success.ToString(Newtonsoft.Json.Formatting.None)).TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Completed);
    }

    [Fact]
    public void 被停止的一輪不算過載()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse(OverloadedMessage);
        parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]},"parent_tool_use_id":null,"session_id":"s"}""");
        var result = parser.Parse(BareErrorResult).TurnCompleted!;
        result.Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        result.ServerBusy.Should().Be(AgentServerBusy.None);
    }

    [Fact]
    public void 子代理的529不算()
    {
        var parser = new ClaudeStreamParser();
        var sub = JObject.Parse(OverloadedMessage);
        sub["parent_tool_use_id"] = "toolu_1";
        parser.Parse(sub.ToString(Newtonsoft.Json.Formatting.None));
        parser.Parse(BareErrorResult).TurnCompleted!.ServerBusy.Should().Be(AgentServerBusy.None, "子代理自己的錯誤交給主對話處理");
    }

    [Fact]
    public void 逐字稿形式的欄位也認()
    {
        // 逐字稿（~/.claude/projects/*.jsonl）的實錄：error server_error、isApiErrorMessage、apiErrorStatus 529（2.1.259）
        var transcript = JObject.Parse(OverloadedMessage);
        transcript.Remove("is_api_error_message");
        transcript.Remove("api_error_status");
        transcript["isApiErrorMessage"] = true;
        transcript["apiErrorStatus"] = 529;
        ClaudeStreamRules.IsOverloadedApiError(transcript).Should().BeTrue();

        transcript.Remove("apiErrorStatus");
        ClaudeStreamRules.IsOverloadedApiError(transcript).Should().BeTrue("只有 isApiErrorMessage 時看文字");
        ClaudeStreamRules.IsOverloadedApiError(null).Should().BeFalse();
    }
}
