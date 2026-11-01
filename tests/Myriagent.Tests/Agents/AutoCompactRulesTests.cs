using Myriagent.Agents;

namespace Myriagent.Tests.Agents;

/// <summary>設定頁各家的自動壓縮上限（AutoCompactRules；網頁的 settingsrules.parseTokens 同一套解析）</summary>
public class AutoCompactRulesTests
{
    [Theory]
    [InlineData("400k", 400_000L)]
    [InlineData(" 400K ", 400_000L)]
    [InlineData("400,000", 400_000L)]
    [InlineData("0.4M", 400_000L)]
    [InlineData("1.5m", 1_500_000L)]
    [InlineData("４００ｋ", 400_000L)]
    [InlineData("123456", 123_456L)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("Default", null)]
    [InlineData(null, null)]
    public void TryParse_K與M_千分位_全形_空白是CLI預設(string? text, long? expected)
    {
        AutoCompactRules.TryParse(text, out var tokens).Should().BeTrue();
        tokens.Should().Be(expected);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5k")]
    [InlineData("4e5")]
    [InlineData("1.2.3k")]
    [InlineData("k")]
    [InlineData("400kb")]
    [InlineData("2000000000")]
    public void TryParse_看不懂的不收(string text)
        => AutoCompactRules.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void TryNormalize_夾進範圍_只有Claude與Codex支援()
    {
        AutoCompactRules.TryNormalize(AgentProvider.Claude, "400k", out var claude).Should().BeTrue();
        claude.Should().Be(400_000);
        AutoCompactRules.TryNormalize(AgentProvider.Claude, "5M", out var high).Should().BeTrue();
        high.Should().Be(967_000, "Claude 的視窗最多 100 萬，門檻是視窗減 33,000");
        AutoCompactRules.TryNormalize(AgentProvider.Claude, "10k", out var low).Should().BeTrue();
        low.Should().Be(67_000, "Claude 的視窗最少 10 萬");
        AutoCompactRules.TryNormalize(AgentProvider.Codex, "400k", out var codex).Should().BeTrue();
        codex.Should().Be(400_000);
        AutoCompactRules.TryNormalize(AgentProvider.Codex, "", out var cleared).Should().BeTrue();
        cleared.Should().BeNull();

        AutoCompactRules.TryNormalize(AgentProvider.Grok, "400k", out _).Should().BeFalse();
        AutoCompactRules.TryNormalize(AgentProvider.Antigravity, "400k", out _).Should().BeFalse();
        AutoCompactRules.TryNormalize(AgentProvider.Claude, "abc", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("70", 70)]
    [InlineData(" 70% ", 70)]
    [InlineData("70 %", 70)]
    [InlineData("７０％", 70)]
    [InlineData("", null)]
    [InlineData("default", null)]
    [InlineData(null, null)]
    public void TryParsePercent_整數可帶百分號_空白是CLI預設(string? text, int? expected)
    {
        AutoCompactRules.TryParsePercent(text, out var percent).Should().BeTrue();
        percent.Should().Be(expected);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("70.5")]
    [InlineData("%")]
    [InlineData("1e2")]
    [InlineData("70k")]
    [InlineData("12345")]
    public void TryParsePercent_看不懂的不收(string text)
        => AutoCompactRules.TryParsePercent(text, out _).Should().BeFalse();

    [Fact]
    public void TryNormalizePercent_只有Grok_夾進20到95()
    {
        AutoCompactRules.TryNormalizePercent(AgentProvider.Grok, "70%", out var grok).Should().BeTrue();
        grok.Should().Be(70);
        AutoCompactRules.TryNormalizePercent(AgentProvider.Grok, "5", out var low).Should().BeTrue();
        low.Should().Be(20, "系統提示就約佔視窗的 9%，太低每一輪都會壓縮");
        AutoCompactRules.TryNormalizePercent(AgentProvider.Grok, "100", out var high).Should().BeTrue();
        high.Should().Be(95);
        AutoCompactRules.TryNormalizePercent(AgentProvider.Grok, "", out var cleared).Should().BeTrue();
        cleared.Should().BeNull();
        AutoCompactRules.TryNormalizePercent(AgentProvider.Claude, "70", out _).Should().BeFalse();
        AutoCompactRules.TryNormalizePercent(AgentProvider.Codex, "70", out _).Should().BeFalse();
        AutoCompactRules.TryNormalizePercent(AgentProvider.Antigravity, "70", out _).Should().BeFalse();
        AutoCompactRules.PercentForProvider(AgentProvider.Claude, 70).Should().BeNull();
        AutoCompactRules.PercentForProvider(AgentProvider.Grok, 10).Should().Be(20);
    }

    [Fact]
    public void 每一家都有明確的決定()
    {
        foreach (var provider in AgentProviders.All)
        {
            _ = AutoCompactRules.RangeOf(provider);
            _ = AutoCompactRules.PercentRangeOf(provider);
        }
        AutoCompactRules.ForProvider(AgentProvider.Grok, 400_000).Should().BeNull();
        AutoCompactRules.ForProvider(AgentProvider.Claude, 5_000_000).Should().Be(967_000);
        AutoCompactRules.ClaudeWindowValue(400_000).Should().Be("433000");
        AutoCompactRules.ClaudeWindowValue(null).Should().BeNull();
        AutoCompactRules.CodexOverride(400_000).Should().Be("model_auto_compact_token_limit=400000");
    }

    [Fact]
    public void Grok啟動時帶百分比的環境變數_改了要重開程序()
    {
        AgentSessionSettings Settings(int? percent) => new("s-1", false, new AgentLauncher("grok.exe", Array.Empty<string>()), "acc", env => env["GROK_HOME"] = "h",
            null, null, null, AgentPermissionMode.Ask, @"C:\w", @"C:\c", "說明", true, AutoCompactPercent: percent);

        var env = new Dictionary<string, string?>();
        GrokAgentSession.ApplyLaunchEnvironment(env, Settings(70));
        env["GROK_AUTO_COMPACT_THRESHOLD_PERCENT"].Should().Be("70");
        env["GROK_HOME"].Should().Be("h", "帳戶環境照舊");

        var none = new Dictionary<string, string?>();
        GrokAgentSession.ApplyLaunchEnvironment(none, Settings(null));
        none.Should().NotContainKey("GROK_AUTO_COMPACT_THRESHOLD_PERCENT", "沒設定時照 CLI 預設");

        GrokAgentSession.Signature(Settings(70)).Should().NotBe(GrokAgentSession.Signature(Settings(null)));
    }
}
