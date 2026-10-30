using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 額度用完（AgentQuotaExhaustedEvent，自動換帳戶用）：Claude 合成的「You've hit your session limit」與被拒的 rate_limit_event、
/// Codex 的 codexErrorInfo usageLimitExceeded。暫時性的 429、子代理、被停止的一輪不算；一輪最多一次、在 TurnCompleted 之前
/// </summary>
public class QuotaExhaustedParserTests
{
    /// <summary>
    /// 2.1.299 逐字稿實錄的合成訊息（頂層欄位是逐字稿的駝峰形式；stream-json 實際是蛇形、沒有 quotaLimits，見下面的 StreamJson*。session_id、uuid 換掉）
    /// </summary>
    private const string SessionLimitMessage =
        """{"type":"assistant","message":{"id":"f4e56385-3bf5-45fc-abc3-16684ed31d49","model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"content":[{"type":"text","text":"You've hit your session limit · resets 6:10am (Asia/Taipei)"}]},"parent_tool_use_id":null,"session_id":"s","uuid":"u1","error":"rate_limit","isApiErrorMessage":true,"apiErrorStatus":429,"apiError":"usage_limit_reached","apiErrorParams":{"rate_limit_info":{"status":"rejected","resetsAt":1792015800,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false}},"quotaLimits":{"status":"rejected","resetsAt":1792015800,"unifiedRateLimitFallbackAvailable":false,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false}}""";

    private const string ErrorResult =
        """{"type":"result","subtype":"success","is_error":true,"result":"You've hit your session limit · resets 6:10am (Asia/Taipei)","session_id":"s"}""";

    private const string SuccessResult = """{"type":"result","subtype":"success","is_error":false,"result":"OK","session_id":"s"}""";

    // ── 2.1.310 的 stream-json 實錄（tools/probes/claude_api_error_probe.py；uuid、時間換掉）──

    /// <summary>usage-limit：假的 API 回 429＋訂閱額度被拒的標頭（five_hour、重置 1792818000）</summary>
    private const string StreamJsonQuotaEvent =
        """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1792818000,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false},"uuid":"e1","session_id":"s"}""";

    private const string StreamJsonQuotaMessage =
        """{"type":"assistant","message":{"id":"m1","container":null,"model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"content":[{"type":"text","text":"You've hit your session limit · resets 1pm (Asia/Taipei)"}],"context_management":null},"parent_tool_use_id":null,"session_id":"s","uuid":"u1","timestamp":"2026-10-24T02:22:31.581Z","error":"rate_limit","is_api_error_message":true,"api_error_status":429,"api_error":"usage_limit_reached","api_error_params":{"rate_limit_info":{"status":"rejected","resetsAt":1792818000,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false}}}""";

    private const string StreamJsonQuotaResult =
        """{"type":"result","subtype":"success","is_error":true,"duration_ms":57,"duration_api_ms":0,"num_turns":1,"stop_reason":"stop_sequence","terminal_reason":"api_error","api_error_status":429,"api_error":"usage_limit_reached","result":"You've hit your session limit · resets 1pm (Asia/Taipei)","session_id":"s","uuid":"r1"}""";

    /// <summary>rate-limit：假的 API 回 429、沒有額度被拒的標頭（訂閱帳戶的暫時性 429）</summary>
    private const string StreamJsonTransientEvent =
        """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","isUsingOverage":false},"uuid":"e2","session_id":"s"}""";

    private const string StreamJsonTransientMessage =
        """{"type":"assistant","message":{"id":"m2","container":null,"model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"content":[{"type":"text","text":"API Error: Server is temporarily limiting requests (not your usage limit) · Rate limit exceeded"}],"context_management":null},"parent_tool_use_id":null,"session_id":"s","uuid":"u2","timestamp":"2026-10-24T02:22:50.529Z","error":"rate_limit","is_api_error_message":true,"api_error_status":429}""";

    private const string StreamJsonTransientResult =
        """{"type":"result","subtype":"success","is_error":true,"duration_ms":52,"duration_api_ms":0,"num_turns":1,"stop_reason":"stop_sequence","terminal_reason":"api_error","api_error_status":429,"result":"API Error: Server is temporarily limiting requests (not your usage limit) · Rate limit exceeded","session_id":"s","uuid":"r2"}""";

    [Fact]
    public void StreamJson實錄的額度用完_發一次_帶重置時間_事件先提示()
    {
        var parser = new ClaudeStreamParser();
        var ev = parser.Parse(StreamJsonQuotaEvent);
        ev.Events.OfType<AgentNoticeEvent>().Should().ContainSingle().Which.Message.Should().StartWith("Claude 訂閱額度已用完，");

        var message = parser.Parse(StreamJsonQuotaMessage);
        message.Events.OfType<AgentQuotaExhaustedEvent>().Should().ContainSingle()
            .Which.Should().Be(new AgentQuotaExhaustedEvent(AgentProvider.Claude, DateTimeOffset.FromUnixTimeSeconds(1792818000)));

        var result = parser.Parse(StreamJsonQuotaResult);
        result.Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty("一輪只發一次");
        result.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Failed);
    }

    [Fact]
    public void StreamJson的合成訊息單獨就認得_不必靠前面的RateLimitEvent()
    {
        // 以前只讀逐字稿的駝峰欄位（apiError、apiErrorParams、quotaLimits），stream-json 的蛇形欄位讀不到，全靠前面的 rate_limit_event
        var parser = new ClaudeStreamParser();
        parser.Parse(StreamJsonQuotaMessage).Events.OfType<AgentQuotaExhaustedEvent>().Should().ContainSingle()
            .Which.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1792818000), "重置時間取自 api_error_params.rate_limit_info");
        ClaudeStreamRules.IsUsageLimitMessage(JObject.Parse(StreamJsonQuotaMessage), rejectedThisTurn: false).Should().BeTrue();

        // 只有 result 帶 api_error 也算（重置時間不明）
        var resultOnly = new ClaudeStreamParser().Parse(StreamJsonQuotaResult);
        resultOnly.Events.OfType<AgentQuotaExhaustedEvent>().Should().ContainSingle().Which.ResetsAt.Should().BeNull();
    }

    [Fact]
    public void StreamJson實錄的暫時性429_不提示額度用完_不算額度用完()
    {
        var parser = new ClaudeStreamParser();
        var ev = parser.Parse(StreamJsonTransientEvent);
        ev.Events.OfType<AgentRateLimitEvent>().Should().ContainSingle("額度資訊照常交給用量");
        ev.Events.OfType<AgentNoticeEvent>().Should().BeEmpty("「not your usage limit」：不能說訂閱額度已用完");
        parser.Parse(StreamJsonTransientMessage).Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();
        var result = parser.Parse(StreamJsonTransientResult);
        result.Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty("不能自動換帳戶");
        result.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Failed);
    }

    [Fact]
    public void 額度被拒之後碰到CLI說明是別的原因的429_不算額度用完()
    {
        // 例如要額外用量才能用的模型（api_error model_requires_usage_credits）：CLI 已經說了原因，result 的退路也不算
        var parser = new ClaudeStreamParser();
        parser.Parse(StreamJsonQuotaEvent);
        var other = JObject.Parse(StreamJsonQuotaMessage);
        other["api_error"] = "model_requires_usage_credits";
        other.Remove("api_error_params");
        parser.Parse(other.ToString(Newtonsoft.Json.Formatting.None)).Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();
        parser.Parse(StreamJsonTransientResult).Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();

        // 暫時性的 rejected 事件不取消同一輪先前的額度被拒
        var again = new ClaudeStreamParser();
        again.Parse(StreamJsonQuotaEvent);
        again.Parse(StreamJsonTransientEvent);
        again.Parse(BareFailure).Events.OfType<AgentQuotaExhaustedEvent>().Should().ContainSingle()
            .Which.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1792818000));
    }

    private const string BareFailure = """{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"s"}""";

    [Fact]
    public void Claude合成的額度用完訊息_發一次_帶重置時間_在TurnCompleted之前()
    {
        var parser = new ClaudeStreamParser();
        var synthetic = parser.Parse(SessionLimitMessage);
        synthetic.Events.OfType<AgentQuotaExhaustedEvent>().Should().ContainSingle()
            .Which.Should().Be(new AgentQuotaExhaustedEvent(AgentProvider.Claude, DateTimeOffset.FromUnixTimeSeconds(1792015800)));
        synthetic.Events.OfType<AgentTextCompletedEvent>().Should().ContainSingle("文字照常顯示成回覆");

        var result = parser.Parse(ErrorResult);
        result.Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty("一輪只發一次");
        result.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Failed);

        // 下一輪重新計算：沒有額度訊號的失敗不算
        parser.Parse(ErrorResult).Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();
    }

    [Fact]
    public void Claude暫時性的429_不算額度用完()
    {
        var parser = new ClaudeStreamParser();
        var transient = JObject.Parse(SessionLimitMessage);
        transient.Remove("apiError");
        transient.Remove("apiErrorParams");
        transient.Remove("quotaLimits");
        transient["apiErrorIsTransient"] = true;
        parser.Parse(transient.ToString(Newtonsoft.Json.Formatting.None)).Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();
        parser.Parse(ErrorResult).Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();

        // 沒有任何額度標記的 rate_limit 也不算（例如 1M context 要額外用量以外的 429）
        var bare = JObject.Parse(SessionLimitMessage);
        bare.Remove("apiError");
        bare.Remove("apiErrorParams");
        bare.Remove("quotaLimits");
        parser.Parse(bare.ToString(Newtonsoft.Json.Formatting.None)).Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();

        // 被拒的 rate_limit_event 之後，沒有標記的 rate_limit 才算；但標成暫時性的一律不算
        parser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1792015800,"rateLimitType":"five_hour"}}""");
        parser.Parse(transient.ToString(Newtonsoft.Json.Formatting.None)).Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();
        parser.Parse(bare.ToString(Newtonsoft.Json.Formatting.None)).Events.OfType<AgentQuotaExhaustedEvent>().Should().ContainSingle();
    }

    [Fact]
    public void Claude只有被拒的RateLimitEvent_接著錯誤結束才算_正常結束或又回報可用都不算()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1792015800,"rateLimitType":"five_hour"}}""")
            .Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty("被拒的事件本身不算：可能接著改用額外用量");
        var failed = parser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"s"}""");
        failed.Events.OfType<AgentQuotaExhaustedEvent>().Should().ContainSingle()
            .Which.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1792015800));
        failed.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Failed);

        // 正常結束：不算
        parser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","rateLimitType":"seven_day_opus"}}""");
        parser.Parse(SuccessResult).Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();

        // 被拒之後又回報可以用：先前的被拒不算
        parser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1792015800}}""");
        parser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1792015800}}""");
        parser.Parse(ErrorResult).Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();
    }

    [Fact]
    public void Claude被停止的一輪與子代理的合成訊息_不算()
    {
        var parser = new ClaudeStreamParser();
        var sub = JObject.Parse(SessionLimitMessage);
        sub["parent_tool_use_id"] = "toolu_task";
        parser.Parse(sub.ToString(Newtonsoft.Json.Formatting.None)).Events.Should().BeEmpty("子代理的訊息不處理（主對話接著會自己碰到上限）");

        parser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1792015800}}""");
        parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]},"parent_tool_use_id":null}""");
        var stopped = parser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"s"}""");
        stopped.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        stopped.Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"message":"x","codexErrorInfo":"usageLimitExceeded"}""", true)]
    [InlineData("""{"message":"x","codexErrorInfo":{"usageLimitExceeded":{}}}""", true)]
    [InlineData("""{"message":"x","codexErrorInfo":"rateLimitExceeded"}""", false)]
    [InlineData("""{"message":"x","codexErrorInfo":{"httpConnectionFailed":{"httpStatusCode":429}}}""", false)]
    [InlineData("""{"message":"You've hit your usage limit.","codexErrorInfo":null}""", false)]
    [InlineData("""{"message":"You've hit your usage limit."}""", false)]
    public void Codex的IsUsageLimitError只認usageLimitExceeded(string error, bool expected)
    {
        CodexAppServerRules.IsUsageLimitError(JObject.Parse(error)).Should().Be(expected);
        CodexAppServerRules.IsUsageLimitError(null).Should().BeFalse();
        CodexAppServerRules.IsUsageLimitError(JValue.CreateNull()).Should().BeFalse();
    }

    [Fact]
    public void Codex的error通知帶usageLimitExceeded_隨這一輪的結束發一次()
    {
        var parser = new CodexEventParser();
        parser.Parse("""{"method":"turn/started","params":{"threadId":"t","turn":{"id":"u1","status":"inProgress"}}}""", "t");
        parser.Parse("""{"method":"error","params":{"error":{"message":"You've hit your usage limit.","codexErrorInfo":"usageLimitExceeded"},"willRetry":false,"threadId":"t","turnId":"u1"}}""", "t")
            .Events.Should().BeEmpty("由這一輪的結束回報");
        var completed = parser.Parse("""{"method":"turn/completed","params":{"threadId":"t","turn":{"id":"u1","status":"failed","error":{"message":"You've hit your usage limit.","codexErrorInfo":"usageLimitExceeded"}}}}""", "t");
        completed.Events.OfType<AgentQuotaExhaustedEvent>().Should().ContainSingle().Which.Should().Be(new AgentQuotaExhaustedEvent(AgentProvider.Codex, null));
        completed.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Failed);

        // 下一輪重新計算：沒有 codexErrorInfo 的失敗不算
        parser.Parse("""{"method":"turn/started","params":{"threadId":"t","turn":{"id":"u2","status":"inProgress"}}}""", "t");
        parser.Parse("""{"method":"turn/completed","params":{"threadId":"t","turn":{"id":"u2","status":"failed","error":{"message":"boom"}}}}""", "t")
            .Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();
    }

    [Fact]
    public void Codex只有turn的error帶usageLimitExceeded也算_被停止的一輪與子代理不算()
    {
        var parser = new CodexEventParser();
        parser.Parse("""{"method":"turn/started","params":{"threadId":"t","turn":{"id":"u1","status":"inProgress"}}}""", "t");
        parser.Parse("""{"method":"turn/completed","params":{"threadId":"t","turn":{"id":"u1","status":"failed","error":{"message":"limit","codexErrorInfo":"usageLimitExceeded"}}}}""", "t")
            .Events.OfType<AgentQuotaExhaustedEvent>().Should().ContainSingle();

        // 子代理的 thread：不是主對話的錯誤
        parser.Parse("""{"method":"turn/started","params":{"threadId":"t","turn":{"id":"u2","status":"inProgress"}}}""", "t");
        parser.Parse("""{"method":"error","params":{"error":{"message":"limit","codexErrorInfo":"usageLimitExceeded"},"willRetry":false,"threadId":"sub","turnId":"x"}}""", "t")
            .Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty("子代理錯誤只顯示在卡片，不能切換主對話帳戶");
        parser.Parse("""{"method":"turn/completed","params":{"threadId":"t","turn":{"id":"u2","status":"completed"}}}""", "t")
            .Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();

        // 被停止的一輪
        parser.Parse("""{"method":"turn/started","params":{"threadId":"t","turn":{"id":"u3","status":"inProgress"}}}""", "t");
        parser.Parse("""{"method":"error","params":{"error":{"message":"limit","codexErrorInfo":"usageLimitExceeded"},"willRetry":false,"threadId":"t","turnId":"u3"}}""", "t");
        parser.Parse("""{"method":"turn/completed","params":{"threadId":"t","turn":{"id":"u3","status":"interrupted"}}}""", "t")
            .Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();
    }
}
