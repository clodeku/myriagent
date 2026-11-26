using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 登入失效而失敗的一輪發 AgentLoginLostEvent（中樞照額度用完的做法換帳戶繼續，2026-11-27 使用者要求）：CLI 合成的 authentication_failed 訊息
/// 或失敗的 result 是要重新登入的那幾句才算；Cloudflare 擋下的 403、暫時的網路錯誤、換發時的暫時狀況、代理在一般回覆裡提到、子代理、被停止的一輪不算
/// </summary>
public class LoginLostParserTests
{
    private const string TimeLimitText =
        "Failed to authenticate: this login reached its time limit and cannot be renewed. Run `claude auth login` to sign in again, or use `claude setup-token` and set CLAUDE_CODE_OAUTH_TOKEN for unattended jobs";

    /// <summary>
    /// stream-json 形狀的合成訊息（欄位照 OverloadedParserTests 的 2.1.310 實錄改成 authentication_failed；
    /// 逐字稿實錄〔2.1.328，2026-11-19〕是 error authentication_failed、isApiErrorMessage、沒有狀態碼、model &lt;synthetic&gt;）
    /// </summary>
    private static string Synthetic(string text, string error = "authentication_failed")
        => new JObject
        {
            ["type"] = "assistant",
            ["message"] = new JObject
            {
                ["id"] = "m1",
                ["model"] = "<synthetic>",
                ["role"] = "assistant",
                ["stop_reason"] = "stop_sequence",
                ["type"] = "message",
                ["usage"] = new JObject { ["input_tokens"] = 0, ["output_tokens"] = 0 },
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = text }),
            },
            ["parent_tool_use_id"] = null,
            ["session_id"] = "s",
            ["uuid"] = "u1",
            ["error"] = error,
            ["is_api_error_message"] = true,
        }.ToString(Newtonsoft.Json.Formatting.None);

    private static string FailedResult(string text)
        => new JObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = true,
            ["num_turns"] = 1,
            ["terminal_reason"] = "api_error",
            ["result"] = text,
            ["session_id"] = "s",
            ["uuid"] = "r1",
        }.ToString(Newtonsoft.Json.Formatting.None);

    private const string BareErrorResult = """{"type":"result","subtype":"error_during_execution","is_error":true,"session_id":"s"}""";

    /// <summary>2.1.332 實錄（2026-11-27：空的設定資料夾、沒有登入跑 claude -p 的 stream-json，不用額度；session、uuid、時間換掉，usage 的細項省略）</summary>
    private const string RecordedNotLoggedIn =
        """{"type":"assistant","message":{"id":"m1","container":null,"model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"content":[{"type":"text","text":"Not logged in · Please run /login"}],"context_management":null},"parent_tool_use_id":null,"session_id":"s","uuid":"u","timestamp":"2026-11-27T00:00:00.000Z","error":"authentication_failed","is_api_error_message":true}""";

    private const string RecordedNotLoggedInResult =
        """{"duration_api_ms":0,"stop_reason":"stop_sequence","session_id":"s","total_cost_usd":0,"modelUsage":{},"permission_denials":[],"terminal_reason":"api_error","is_error":true,"num_turns":1,"subtype":"success","api_error_status":null,"result":"Not logged in · Please run /login","type":"result","duration_ms":72,"uuid":"u"}""";

    [Fact]
    public void 實錄_沒有登入的CLI_合成訊息與result()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse(RecordedNotLoggedIn).Events.OfType<AgentLoginLostEvent>().Should().ContainSingle().Which.TimeLimit.Should().BeFalse();
        var result = parser.Parse(RecordedNotLoggedInResult);
        result.Events.OfType<AgentLoginLostEvent>().Should().BeEmpty("一輪只發一次");
        result.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Failed);
        result.TurnCompleted.ErrorMessage.Should().Be("Not logged in · Please run /login");

        new ClaudeStreamParser().Parse(RecordedNotLoggedInResult).Events.OfType<AgentLoginLostEvent>().Should().ContainSingle("只有 result 也認");
    }

    [Fact]
    public void 登入到了使用期限_合成訊息與result_一輪只發一次_文字照常顯示()
    {
        var parser = new ClaudeStreamParser();
        var synthetic = parser.Parse(Synthetic(TimeLimitText));
        synthetic.Events.OfType<AgentLoginLostEvent>().Should().ContainSingle().Which.Should().Be(new AgentLoginLostEvent(AgentProvider.Claude, TimeLimit: true));
        synthetic.Events.OfType<AgentTextCompletedEvent>().Should().ContainSingle("錯誤文字照常顯示成回覆");
        synthetic.Events.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();

        var result = parser.Parse(FailedResult(TimeLimitText));
        result.Events.OfType<AgentLoginLostEvent>().Should().BeEmpty("合成訊息已經發過");
        result.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Failed);
        result.TurnCompleted.ErrorMessage.Should().Be(TimeLimitText);
        result.TurnCompleted.ServerBusy.Should().Be(AgentServerBusy.None, "不是過載，不自動重試");

        // 下一輪重新計算：沒有登入失效的失敗不算
        parser.Parse(BareErrorResult).Events.OfType<AgentLoginLostEvent>().Should().BeEmpty();
    }

    [Fact]
    public void 只有result是要重新登入的那幾句_也算()
    {
        var events = new ClaudeStreamParser().Parse(FailedResult("Not logged in · Please run /login")).Events;
        events.OfType<AgentLoginLostEvent>().Should().ContainSingle().Which.TimeLimit.Should().BeFalse();

        // 正常結束的 result 提到這幾句不算
        var success = JObject.Parse(FailedResult(TimeLimitText));
        success["is_error"] = false;
        ClaudeStreamRules.LoginLostOf(success).Should().BeNull();
        new ClaudeStreamParser().Parse(success.ToString(Newtonsoft.Json.Formatting.None)).Events.OfType<AgentLoginLostEvent>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(TimeLimitText, true)]
    [InlineData("Failed to authenticate: this login reached its time limit · Sign in again to continue", true)]
    [InlineData("Login reached its time limit · Please run /login", true)]
    [InlineData("Login expired · Please run /login", false)]
    [InlineData("Login expired · Run /login to sign in again, or re-authenticate your Anthropic profile", false)]
    [InlineData("OAuth token revoked · Please run /login", false)]
    [InlineData("Not logged in · Please run /login", false)]
    [InlineData("Authentication required · Sign in again to continue", false)]
    public void CLI要人重新登入的說法(string text, bool timeLimit)
    {
        ClaudeStreamRules.LoginLostText(text).Should().Be(timeLimit);
        ClaudeStreamRules.LoginLostOf(JObject.Parse(Synthetic(text))).Should().Be(timeLimit);
        ClaudeStreamRules.LoginLostOf(JObject.Parse(FailedResult(text))).Should().Be(timeLimit);
    }

    [Theory]
    [InlineData("Failed to authenticate. API Error: 403 Attention Required! | Cloudflare")]
    [InlineData("Request blocked before it reached Anthropic's API (HTTP 403) · the network this request came from isn't allowed · signing in again won't change this")]
    [InlineData("Authentication error · This may be a temporary network issue, please try again")]
    [InlineData("Could not refresh your login because another Claude Code process is refreshing it (or exited mid-refresh) · Try again in a minute")]
    [InlineData("Could not refresh your login: the disk that holds the Claude Code config directory (~/.claude by default), or your disk quota on it, is full")]
    [InlineData("Invalid API key · Fix external API key")]
    [InlineData("Your organization has disabled API key authentication · Run /login to sign in with your claude.ai account")]
    [InlineData("")]
    public void 重新登入沒有用或暫時的錯誤_不算(string text)
    {
        ClaudeStreamRules.LoginLostText(text).Should().BeNull();
        var parser = new ClaudeStreamParser();
        parser.Parse(Synthetic(text)).Events.OfType<AgentLoginLostEvent>().Should().BeEmpty();
        parser.Parse(FailedResult(text)).Events.OfType<AgentLoginLostEvent>().Should().BeEmpty();
    }

    [Fact]
    public void 不是API錯誤訊息_或錯誤分類不同_不算()
    {
        // 代理在一般回覆裡提到（沒有 error、不是 API 錯誤訊息）
        var normal = JObject.Parse(Synthetic(TimeLimitText));
        normal.Remove("error");
        normal.Remove("is_api_error_message");
        normal["message"]!["model"] = "claude-opus-5-5";
        ClaudeStreamRules.LoginLostOf(normal).Should().BeNull();

        // 是 API 錯誤訊息但分類不是 authentication_failed
        ClaudeStreamRules.LoginLostOf(JObject.Parse(Synthetic(TimeLimitText, error: "unknown"))).Should().BeNull();

        var parser = new ClaudeStreamParser();
        parser.Parse(normal.ToString(Newtonsoft.Json.Formatting.None)).Events.OfType<AgentLoginLostEvent>().Should().BeEmpty();
        parser.Parse(BareErrorResult).Events.OfType<AgentLoginLostEvent>().Should().BeEmpty();
        ClaudeStreamRules.LoginLostOf(null).Should().BeNull();
    }

    [Fact]
    public void 逐字稿形式的欄位也認()
    {
        // 逐字稿（~/.claude/projects/*.jsonl）的實錄：error authentication_failed、isApiErrorMessage（2.1.328）
        var transcript = JObject.Parse(Synthetic(TimeLimitText));
        transcript.Remove("is_api_error_message");
        transcript["isApiErrorMessage"] = true;
        ClaudeStreamRules.LoginLostOf(transcript).Should().BeTrue();

        transcript.Remove("isApiErrorMessage");
        ClaudeStreamRules.LoginLostOf(transcript).Should().BeNull("要是 API 錯誤訊息");
    }

    [Fact]
    public void 被停止的一輪不從result補發()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]},"parent_tool_use_id":null,"session_id":"s"}""");
        var result = parser.Parse(FailedResult("Not logged in · Please run /login"));
        result.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        result.Events.OfType<AgentLoginLostEvent>().Should().BeEmpty();
    }

    [Fact]
    public void 子代理的登入失效不算()
    {
        var parser = new ClaudeStreamParser();
        var sub = JObject.Parse(Synthetic(TimeLimitText));
        sub["parent_tool_use_id"] = "toolu_1";
        parser.Parse(sub.ToString(Newtonsoft.Json.Formatting.None)).Events.OfType<AgentLoginLostEvent>().Should().BeEmpty("子代理自己的錯誤交給主對話處理");
        parser.Parse(BareErrorResult).Events.OfType<AgentLoginLostEvent>().Should().BeEmpty();
    }
}
