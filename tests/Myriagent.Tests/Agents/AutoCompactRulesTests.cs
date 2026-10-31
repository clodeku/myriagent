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

    [Fact]
    public void 每一家都有明確的決定()
    {
        foreach (var provider in AgentProviders.All)
            _ = AutoCompactRules.RangeOf(provider);
        AutoCompactRules.ForProvider(AgentProvider.Grok, 400_000).Should().BeNull();
        AutoCompactRules.ForProvider(AgentProvider.Claude, 5_000_000).Should().Be(967_000);
        AutoCompactRules.ClaudeWindowValue(400_000).Should().Be("433000");
        AutoCompactRules.ClaudeWindowValue(null).Should().BeNull();
        AutoCompactRules.CodexOverride(400_000).Should().Be("model_auto_compact_token_limit=400000");
    }
}
