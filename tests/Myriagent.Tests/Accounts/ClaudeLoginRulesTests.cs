using Myriagent.Accounts;
using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Accounts;

/// <summary>
/// Claude 的 app 內登入（ClaudeLoginRules）：claude_authenticate 的兩個網址與 state、貼回來的授權碼、錯誤說明；
/// 遠端登入的第一則 accountLogin（UrlShown 帶 loginUrl）。網址形狀取自 2.1.310 的探針（tools/probes/claude_cli7_probe.py --mode login）
/// </summary>
public sealed class ClaudeLoginRulesTests
{
    private const string State = "Xy-9_abc";
    private const string Manual = "https://claude.com/cai/oauth/authorize?code=true&client_id=9d1c&response_type=code&redirect_uri=https%3A%2F%2Fplatform.claude.com%2Foauth%2Fcode%2Fcallback&scope=user%3Ainference&code_challenge=c1&code_challenge_method=S256&state=" + State;
    private const string Automatic = "https://claude.com/cai/oauth/authorize?code=true&client_id=9d1c&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A53123%2Fcallback&scope=user%3Ainference&code_challenge=c1&code_challenge_method=S256&state=" + State;

    [Fact]
    public void 請求的形狀()
    {
        ClaudeLoginRules.BuildAuthenticateRequest().ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"subtype":"claude_authenticate","loginWithClaudeAi":true}""");
        ClaudeLoginRules.BuildWaitRequest().ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"subtype":"claude_oauth_wait_for_completion"}""");
        ClaudeLoginRules.BuildCallbackRequest("abc", State).ToString(Newtonsoft.Json.Formatting.None)
            .Should().Be("""{"subtype":"claude_oauth_callback","authorizationCode":"abc","state":"Xy-9_abc"}""");
    }

    [Fact]
    public void 兩個網址都是claude網域_state相同_才接受()
    {
        var flow = ClaudeLoginRules.ParseAuthenticate(new JObject { ["manualUrl"] = Manual, ["automaticUrl"] = Automatic });
        flow.Should().NotBeNull();
        flow!.State.Should().Be(State);
        flow.ManualUrl.AbsoluteUri.Should().Be(Manual);
        flow.AutomaticUrl.AbsoluteUri.Should().Be(Automatic);

        ClaudeLoginRules.ParseAuthenticate(new JObject { ["manualUrl"] = Manual.Replace("claude.com", "evil.example"), ["automaticUrl"] = Automatic }).Should().BeNull();
        ClaudeLoginRules.ParseAuthenticate(new JObject { ["manualUrl"] = Manual.Replace("https:", "http:"), ["automaticUrl"] = Automatic }).Should().BeNull();
        ClaudeLoginRules.ParseAuthenticate(new JObject { ["manualUrl"] = Manual, ["automaticUrl"] = Automatic.Replace(State, "other") }).Should().BeNull("state 不同");
        ClaudeLoginRules.ParseAuthenticate(new JObject { ["manualUrl"] = Manual.Replace("&state=" + State, string.Empty), ["automaticUrl"] = Automatic }).Should().BeNull("沒有 state");
        ClaudeLoginRules.ParseAuthenticate(new JObject { ["manualUrl"] = Manual }).Should().BeNull();
        ClaudeLoginRules.ParseAuthenticate(new JObject { ["manualUrl"] = 5, ["automaticUrl"] = Automatic }).Should().BeNull();
        ClaudeLoginRules.ParseAuthenticate(null).Should().BeNull();
    }

    [Theory]
    [InlineData("abc123", "abc123")]
    [InlineData("  abc123#Xy-9_abc \n", "abc123")]
    [InlineData("abc123#", "abc123")]
    [InlineData("abc123#other-state", null)]
    [InlineData("abc 123", null)]
    [InlineData("#Xy-9_abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 貼回來的授權碼(string? pasted, string? expected) => ClaudeLoginRules.ParseCode(pasted, State).Should().Be(expected);

    [Fact]
    public void 授權碼太長不收() => ClaudeLoginRules.ParseCode(new string('a', ClaudeLoginRules.MaxCodeLength + 1), State).Should().BeNull();

    [Fact]
    public void 錯誤說明()
    {
        ClaudeLoginRules.DescribeError("Request failed with status code 400").Should().Be("授權碼不對或已經過期，請重新登入");
        ClaudeLoginRules.DescribeError("No active claude_authenticate flow").Should().Be("這次登入已經結束，請重新登入");
        ClaudeLoginRules.DescribeError(null).Should().Be("登入沒有完成");
        ClaudeLoginRules.DescribeError("boom").Should().Be("登入沒有完成：boom");
    }

    [Theory]
    [InlineData("https://claude.com/cai/oauth/authorize?state=x", true)]
    [InlineData("https://claude.ai/oauth/authorize?state=x", true)]
    [InlineData("https://CLAUDE.COM/x", true)]
    [InlineData("http://claude.com/x", false)]
    [InlineData("https://claude.com:8443/x", false)]
    [InlineData("https://user@claude.com/x", false)]
    [InlineData("https://claude.com.evil.example/x", false)]
    [InlineData("https://platform.claude.com/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData(null, false)]
    public void 允許的登入網址(string? url, bool allowed) => RemoteLoginUrlRules.IsAllowedClaudeLoginUrl(url).Should().Be(allowed);

    [Fact]
    public void 遠端登入的第一則帶loginUrl()
    {
        var output = RemoteAccountRules.LoginOutput("la_1", new RemoteLoginStart(RemoteLoginMilestone.UrlShown, LoginUrl: new Uri(Manual)), DateTimeOffset.UtcNow);
        output["loginUrl"]!.ToString().Should().Be(Manual);
        output["message"]!.ToString().Should().Be(RemoteAccountRules.UrlShownMessage);
        output["done"]!.Value<bool>().Should().BeFalse();
        output["deviceCode"].Should().BeNull("Claude 不是裝置碼");
        FluentActions.Invoking(() => RemoteAccountRules.LoginOutput("la_1", new RemoteLoginStart(RemoteLoginMilestone.UrlShown), DateTimeOffset.UtcNow))
            .Should().Throw<ArgumentException>();
    }
}
