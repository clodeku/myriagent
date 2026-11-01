using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>模型選單的「預設」實際是哪個模型（DefaultModelRules；網頁的 settingsrules.modelText 顯示成「預設（Opus 5.5）」）</summary>
public class DefaultModelRulesTests
{
    private static readonly string[] Efforts = { "low", "high" };

    private static AgentModelOption M(string value, string label, string? description = null) => new(value, label, description, Efforts);

    [Fact]
    public void Claude_預設的說明以模型名稱開頭()
    {
        var models = new[]
        {
            M("default", "Default (recommended)", "Opus 5.5 · Best for everyday, complex tasks"),
            M("opus", "Opus 5.5"), M("fable", "Fable 5.1"), M("sonnet", "Sonnet 5.5"),
        };
        DefaultModelRules.ResolvedLabel(AgentProvider.Claude, models).Should().Be("Opus 5.5");
        DefaultModelRules.ResolvedLabel(AgentProvider.Claude, new[] { M("default", "預設（建議）", "帳戶預設模型"), M("opus", "Opus") })
            .Should().BeNull("CLI 還沒回報清單前的內建說明不含模型名稱");
    }

    [Fact]
    public void Grok_取說明裡的目前模型_同位置取最長的名稱()
    {
        var models = new[]
        {
            M("default", "預設", "grok 的預設模型（目前是 Grok 4.7 Fast）"),
            M("grok-4.7", "Grok 4.7"), M("grok-4.7-build-fast", "Grok 4.7 Fast"),
        };
        DefaultModelRules.ResolvedLabel(AgentProvider.Grok, models).Should().Be("Grok 4.7 Fast");
    }

    [Fact]
    public void Codex_設定檔的model優先_沒有才是清單第一個的isDefault()
    {
        var models = new[] { M("gpt-6.1-sol", "GPT-6.1-Sol"), M("gpt-6-astra", "GPT-6-Astra") };
        DefaultModelRules.ResolvedLabel(AgentProvider.Codex, models, "gpt-6-astra").Should().Be("GPT-6-Astra", "0.166.0 實測：沒指定模型時用 config.toml 的 model");
        DefaultModelRules.ResolvedLabel(AgentProvider.Codex, models, "gpt-9-new").Should().Be("gpt-9-new", "清單沒有的照 id 顯示");
        DefaultModelRules.ResolvedLabel(AgentProvider.Codex, models).Should().Be("GPT-6.1-Sol");
        DefaultModelRules.ResolvedLabel(AgentProvider.Codex, new[] { M("default", "預設", "Codex 設定檔的預設模型") }).Should().BeNull();
    }

    [Fact]
    public void Antigravity_開始前不知道()
        => DefaultModelRules.ResolvedLabel(AgentProvider.Antigravity, new[] { M("default", "預設", "agy 設定的預設模型"), M("gemini-4-argon", "Gemini 4 Argon") })
            .Should().BeNull();

    [Fact]
    public void 每一家都有明確的決定()
    {
        foreach (var provider in AgentProviders.All)
            _ = DefaultModelRules.ResolvedLabel(provider, Array.Empty<AgentModelOption>());
    }

    [Theory]
    [InlineData("model = \"gpt-6-astra\"\nmodel_reasoning_effort = \"high\"\n", "gpt-6-astra")]
    [InlineData("# 註解\nmodel='o9'  # 行尾註解\n", "o9")]
    [InlineData("approval_policy = \"never\"\n[profiles.fast]\nmodel = \"gpt-6.1-sol\"\n", null)]
    [InlineData("model_provider = \"openai\"\n", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseCodexConfiguredModel_只看最上層的model(string? toml, string? expected)
        => DefaultModelRules.ParseCodexConfiguredModel(toml?.Replace("\n", "\r\n")).Should().Be(expected);
}
