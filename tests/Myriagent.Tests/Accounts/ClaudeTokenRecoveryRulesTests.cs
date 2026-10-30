using Myriagent.Usage;
using Myriagent.Accounts;
using Myriagent.Cli;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Accounts;

/// <summary>Claude 權杖過期後的用量恢復：借用同身分的登入、讓 CLI 換發權杖的守門、參數、結果判讀與說明文字</summary>
public class ClaudeTokenRecoveryRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 3, 0, 0, TimeSpan.Zero);
    private const string Org = "11111111-0000-4000-8000-000000000001";

    private static ClaudeUsageCredentialSource Source(string label, string? email = "me@example.com", string? org = Org, TimeSpan? life = null, bool hasToken = true)
        => new($@"C:\x\{label}\.credentials.json", label, email, org, life is { } l ? Now + l : null, hasToken);

    // ───── 借用同身分的登入 ─────

    [Fact]
    public void 借用_email與org都相同而且權杖還有效_取到期最晚的()
    {
        var sources = new[] { Source("a", life: TimeSpan.FromHours(1)), Source("b", life: TimeSpan.FromHours(6)), Source("c", life: TimeSpan.FromHours(3)) };

        ClaudeTokenRecoveryRules.PickBorrowSource("me@example.com", Org, sources, Now)!.Label.Should().Be("b");
    }

    [Fact]
    public void 借用_email不分大小寫與前後空白_org不分大小寫()
    {
        var sources = new[] { Source("a", email: " ME@Example.com ", org: Org.ToUpperInvariant(), life: TimeSpan.FromHours(1)) };

        ClaudeTokenRecoveryRules.PickBorrowSource("me@example.com", Org, sources, Now)!.Label.Should().Be("a");
    }

    [Fact]
    public void 借用_同email不同org不借_可能是同一個人的另一個組織()
    {
        var sources = new[] { Source("a", org: "other-org", life: TimeSpan.FromHours(1)) };

        ClaudeTokenRecoveryRules.PickBorrowSource("me@example.com", Org, sources, Now).Should().BeNull();
    }

    [Fact]
    public void 借用_不同email或來源的身分不明不借()
    {
        var sources = new[]
        {
            Source("other", email: "other@example.com", life: TimeSpan.FromHours(1)),
            Source("noEmail", email: null, life: TimeSpan.FromHours(1)),
            Source("noOrg", org: null, life: TimeSpan.FromHours(1)),
        };

        ClaudeTokenRecoveryRules.PickBorrowSource("me@example.com", Org, sources, Now).Should().BeNull();
    }

    [Theory]
    [InlineData(null, Org)]
    [InlineData("  ", Org)]
    [InlineData("me@example.com", null)]
    [InlineData("me@example.com", "")]
    public void 借用_自己的email或org不明一律不借(string? email, string? org)
    {
        var sources = new[] { Source("a", life: TimeSpan.FromHours(1)) };

        ClaudeTokenRecoveryRules.PickBorrowSource(email, org, sources, Now).Should().BeNull();
    }

    [Fact]
    public void 借用_權杖過期_五分鐘內到期_沒有到期時間_沒有權杖都不借()
    {
        var sources = new[]
        {
            Source("expired", life: TimeSpan.FromMinutes(-1)),
            Source("soon", life: TimeSpan.FromMinutes(4)),
            Source("noExpiry", life: null),
            Source("empty", life: TimeSpan.FromHours(2), hasToken: false),
        };

        ClaudeTokenRecoveryRules.PickBorrowSource("me@example.com", Org, sources, Now).Should().BeNull();
    }

    // ───── 讓 CLI 換發的守門 ─────

    private static ClaudeTokenRefreshBlock Can(bool enabled = true, bool installed = true, AccountStatus status = AccountStatus.Ready, bool conservative = false,
        bool loginActive = false, bool cliPaused = false, int runningTurns = 0, bool inFlight = false, ClaudeTokenState token = ClaudeTokenState.Expiring,
        bool hasRefresh = true, DateTimeOffset? lastAttempt = null)
        => ClaudeTokenRecoveryRules.CanRefreshWithCli(enabled, installed, status, conservative, loginActive, cliPaused, runningTurns, inFlight, token, hasRefresh, lastAttempt, Now);

    [Fact]
    public void 換發_閒置已登入_權杖過期_有refreshToken_可以()
        => Can().Should().Be(ClaudeTokenRefreshBlock.None);

    [Fact]
    public void 換發_各種不能跑的情況()
    {
        Can(enabled: false).Should().Be(ClaudeTokenRefreshBlock.Disabled);
        Can(installed: false).Should().Be(ClaudeTokenRefreshBlock.NotInstalled);
        Can(status: AccountStatus.NotLoggedIn).Should().Be(ClaudeTokenRefreshBlock.NotReady);
        Can(status: AccountStatus.ApiKey).Should().Be(ClaudeTokenRefreshBlock.NotReady, "API Key 沒有 OAuth 權杖可換發");
        Can(status: AccountStatus.Error).Should().Be(ClaudeTokenRefreshBlock.NotReady);
        Can(conservative: true).Should().Be(ClaudeTokenRefreshBlock.Conservative);
        Can(loginActive: true).Should().Be(ClaudeTokenRefreshBlock.LoginActive);
        Can(cliPaused: true).Should().Be(ClaudeTokenRefreshBlock.CliPaused);
        Can(runningTurns: 1).Should().Be(ClaudeTokenRefreshBlock.TurnRunning);
        Can(inFlight: true).Should().Be(ClaudeTokenRefreshBlock.InFlight);
        Can(token: ClaudeTokenState.Fresh).Should().Be(ClaudeTokenRefreshBlock.NotExpired);
        Can(token: ClaudeTokenState.Empty).Should().Be(ClaudeTokenRefreshBlock.NotExpired, "空權杖＝登入已失效，換發不了");
        Can(token: ClaudeTokenState.MissingExpiry).Should().Be(ClaudeTokenRefreshBlock.NotExpired);
        Can(token: ClaudeTokenState.Unreadable).Should().Be(ClaudeTokenRefreshBlock.NotExpired);
        Can(hasRefresh: false).Should().Be(ClaudeTokenRefreshBlock.NoRefreshToken);
    }

    [Fact]
    public void 換發_距上一次嘗試不到一小時不跑_滿一小時可以()
    {
        Can(lastAttempt: Now - TimeSpan.FromMinutes(59)).Should().Be(ClaudeTokenRefreshBlock.TooSoon);
        Can(lastAttempt: Now - TimeSpan.FromHours(1)).Should().Be(ClaudeTokenRefreshBlock.None);
    }

    [Fact]
    public void 換發_設定關掉時優先回Disabled()
        => Can(enabled: false, installed: false, status: AccountStatus.NotLoggedIn, runningTurns: 2).Should().Be(ClaudeTokenRefreshBlock.Disabled);

    [Theory]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","refreshToken":"r","expiresAt":1}}""", true)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","refreshToken":"","expiresAt":1}}""", false)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","expiresAt":1}}""", false)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","refreshToken":null}}""", false)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","refreshToken":5}}""", false)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","refreshToken":"r",""", false)]
    [InlineData(null, false)]
    public void 換發_有沒有refreshToken(string? json, bool expected)
        => ClaudeTokenRecoveryRules.HasRefreshToken(json).Should().Be(expected);

    // ───── CLI 代查用量（get_usage） ─────

    [Fact]
    public void 代查用量_請求是get_usage_回應的rate_limits照用量端點解析()
    {
        ClaudeTokenRecoveryRules.BuildUsageRequest().Value<string>("subtype").Should().Be("get_usage");
        // 形狀取自 2.1.310 實測（tools/probes/claude_cli7_probe.py --mode usage）
        var response = JObject.Parse("""
            { "session": { "total_cost_usd": 0 }, "subscription_type": "max", "rate_limits_available": true,
              "rate_limits": { "five_hour": { "utilization": 12, "resets_at": "2026-10-26T07:19:59.911371+00:00" },
                               "seven_day": { "utilization": 89, "resets_at": "2026-10-28T21:59:59.911394+00:00" },
                               "seven_day_opus": null, "extra_usage": { "is_enabled": false } } }
            """);
        var limits = ClaudeTokenRecoveryRules.ParseCliUsage(response);
        limits.Should().NotBeNull();
        var parsed = UsageRules.ParseClaudeUsage(limits);
        parsed.Should().NotBeNull();
        parsed!.Windows.Select(w => w.Percent).Should().Contain(new[] { 12d, 89d });

        ClaudeTokenRecoveryRules.ParseCliUsage(JObject.Parse("""{ "rate_limits_available": false, "rate_limits": {} }""")).Should().BeNull();
        ClaudeTokenRecoveryRules.ParseCliUsage(new JObject()).Should().BeNull();
        ClaudeTokenRecoveryRules.ParseCliUsage(null).Should().BeNull();
    }

    // ───── 結果判讀 ─────

    private static CliRunResult Run(int exit, string stdout = "", string stderr = "", bool timedOut = false) => new(exit, stdout, stderr, timedOut);

    [Fact]
    public void 結果_權杖變新鮮就是換發成功_不看結束碼()
    {
        ClaudeTokenRecoveryRules.EvaluateRefresh(ClaudeTokenState.Fresh, Run(0)).Should().Be((ClaudeTokenRefreshResult.Refreshed, (string?)null));
        ClaudeTokenRecoveryRules.EvaluateRefresh(ClaudeTokenState.Fresh, Run(1, """{"type":"result","is_error":true,"result":"limit reached"}"""))
            .Result.Should().Be(ClaudeTokenRefreshResult.Refreshed, "額度用完時權杖在呼叫之前就換好了");
    }

    [Fact]
    public void 結果_權杖被清成空字串是登入已失效()
        => ClaudeTokenRecoveryRules.EvaluateRefresh(ClaudeTokenState.Empty, Run(1, stderr: "Failed to authenticate: OAuth session expired and could not be refreshed"))
            .Result.Should().Be(ClaudeTokenRefreshResult.LoginLost);

    [Fact]
    public void 結果_沒換成_說明逾時_stderr第一行_錯誤結果文字()
    {
        ClaudeTokenRecoveryRules.EvaluateRefresh(ClaudeTokenState.Expiring, Run(-1, timedOut: true)).Should().Be((ClaudeTokenRefreshResult.Failed, "執行逾時"));
        ClaudeTokenRecoveryRules.EvaluateRefresh(ClaudeTokenState.Expiring, Run(1, stderr: "\n  network error: ECONNRESET\nmore"))
            .Should().Be((ClaudeTokenRefreshResult.Failed, "結束碼 1：network error: ECONNRESET"));
        ClaudeTokenRecoveryRules.EvaluateRefresh(ClaudeTokenState.Expiring, Run(1, stdout: "noise\n{\"type\":\"result\",\"is_error\":true,\"result\":\"API Error: 500\"}"))
            .Should().Be((ClaudeTokenRefreshResult.Failed, "結束碼 1：API Error: 500"));
        ClaudeTokenRecoveryRules.EvaluateRefresh(ClaudeTokenState.Expiring, Run(0))
            .Should().Be((ClaudeTokenRefreshResult.Failed, "結束碼 0，權杖沒有更新"));
        ClaudeTokenRecoveryRules.EvaluateRefresh(ClaudeTokenState.None, Run(0, stdout: "{\"type\":\"result\",\"is_error\":false,\"result\":\"OK\"}"))
            .Should().Be((ClaudeTokenRefreshResult.Failed, "結束碼 0，權杖沒有更新"), "回覆成功的 JSON 不是原因（例如 CLI 把憑證移到 Windows 認證管理員）");
    }

    [Fact]
    public void 結果_很長的錯誤截到160字()
    {
        var detail = ClaudeTokenRecoveryRules.EvaluateRefresh(ClaudeTokenState.Expiring, Run(2, stderr: new string('x', 400))).Detail!;

        detail.Should().Be("結束碼 2：" + new string('x', 160) + "…");
    }

    // ───── 說明文字 ─────

    [Fact]
    public void 說明_依狀況說明接下來會怎樣()
    {
        ClaudeTokenRecoveryRules.DescribeExpired(ClaudeTokenRefreshBlock.None, null, null, Now).Should().Be("登入權杖已過期，正在讓 Claude CLI 換發權杖…");
        ClaudeTokenRecoveryRules.DescribeExpired(ClaudeTokenRefreshBlock.InFlight, null, null, Now).Should().Be("登入權杖已過期，正在讓 Claude CLI 換發權杖…");
        ClaudeTokenRecoveryRules.DescribeExpired(ClaudeTokenRefreshBlock.TooSoon, "執行逾時", Now.AddMinutes(25), Now)
            .Should().Be("登入權杖已過期，Claude CLI 換發沒有成功（執行逾時）；25 分鐘後再試");
        ClaudeTokenRecoveryRules.DescribeExpired(ClaudeTokenRefreshBlock.TooSoon, null, Now.AddMinutes(90), Now)
            .Should().Be("登入權杖已過期；約 2 小時後再讓 Claude CLI 換發");
        ClaudeTokenRecoveryRules.DescribeExpired(ClaudeTokenRefreshBlock.TooSoon, null, Now.AddMinutes(-1), Now)
            .Should().Be("登入權杖已過期；稍後再讓 Claude CLI 換發");
        ClaudeTokenRecoveryRules.DescribeExpired(ClaudeTokenRefreshBlock.Disabled, null, null, Now).Should().Be("登入權杖已過期，下次使用這個帳戶時會自動更新");
        ClaudeTokenRecoveryRules.DescribeExpired(ClaudeTokenRefreshBlock.CliPaused, null, null, Now).Should().Be("登入權杖已過期，Claude CLI 更新完會再換發權杖");
        ClaudeTokenRecoveryRules.DescribeExpired(ClaudeTokenRefreshBlock.TurnRunning, null, null, Now).Should().Be("登入權杖已過期，對話中 CLI 會自動換發");
        ClaudeTokenRecoveryRules.DescribeExpired(ClaudeTokenRefreshBlock.NoRefreshToken, null, null, Now).Should().Contain("請重新登入");
    }
}
