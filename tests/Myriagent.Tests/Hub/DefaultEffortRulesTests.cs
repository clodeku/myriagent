using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 強度選「預設」時實際用的強度（2026-11-12 使用者要求；實測 tools/probes/default_effort_probe.py）：各 CLI 的來源、沿用、
/// 這個對話的實際預設強度、同步給其他電腦與瀏覽器版
/// </summary>
public sealed class DefaultEffortRulesTests
{
    private static AgentModelOption M(string value, string? defaultEffort, params string[] efforts) => new(value, value, null, efforts, defaultEffort);

    [Fact]
    public void Codex的模型清單帶defaultReasoningEffort()
    {
        var result = JObject.Parse("""
            {"data":[
              {"model":"gpt-6.1-sol","displayName":"Sol","isDefault":true,"defaultReasoningEffort":"low","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"high"}]},
              {"model":"gpt-6-luna","displayName":"Luna","defaultReasoningEffort":null,"supportedReasoningEfforts":[{"reasoningEffort":"medium"}]}
            ]}
            """);
        var models = CodexAppServerRules.ParseModels(result);
        models.Select(m => (m.Value, m.DefaultEffort)).Should().Equal(("gpt-6.1-sol", "low"), ("gpt-6-luna", (string?)null));
    }

    [Fact]
    public void Claude的get_settings讀applied的effort()
    {
        ClaudeStreamRules.ParseAppliedEffort(JObject.Parse("""{"applied":{"model":"claude-fable-5-2","effort":"medium"}}""")).Should().Be("medium");
        ClaudeStreamRules.ParseAppliedEffort(JObject.Parse("""{"applied":{"effort":null}}""")).Should().BeNull("不支援強度的模型");
        ClaudeStreamRules.ParseAppliedEffort(null).Should().BeNull();
    }

    [Fact]
    public void Codex設定檔的model_reasoning_effort_只看第一個表格之前()
    {
        DefaultModelRules.ParseCodexConfiguredEffort("model = \"gpt-6\"\nmodel_reasoning_effort = \"high\" # 註解\n").Should().Be("high");
        DefaultModelRules.ParseCodexConfiguredEffort("model_reasoning_effort = 'xhigh'").Should().Be("xhigh");
        DefaultModelRules.ParseCodexConfiguredEffort("[profiles.fast]\nmodel_reasoning_effort = \"low\"").Should().BeNull();
        DefaultModelRules.ParseCodexConfiguredEffort(null).Should().BeNull();
    }

    [Fact]
    public void 會話回報的清單沒有預設強度時沿用探查到的_有的照新的()
    {
        IReadOnlyList<AgentModelOption> stored = [M("default", "medium", "low", "medium"), M("opus", "high", "low", "high")];
        var reported = ConversationDefaultsRules.CarryDefaultEfforts(stored, [M("default", null, "low", "medium"), M("opus", "xhigh", "low"), M("new", null, "low")]);
        reported.Select(m => m.DefaultEffort).Should().Equal("medium", "xhigh", null);
        ConversationDefaultsRules.CarryDefaultEfforts(null, [M("a", null)]).Single().DefaultEffort.Should().BeNull();
        ConversationDefaultsRules.SameModels(stored, [M("default", "medium", "low", "medium"), M("opus", "low", "low", "high")]).Should().BeFalse("預設強度變了也算不同");
    }

    [Fact]
    public void 還不知道預設強度的模型_只看有強度可選的()
    {
        ConversationDefaultsRules.MissingDefaultEfforts([M("a", "medium", "low"), M("haiku", null)]).Should().BeFalse("沒有強度可選的不必知道");
        ConversationDefaultsRules.MissingDefaultEfforts([M("a", null, "low")]).Should().BeTrue();
        ConversationDefaultsRules.MissingDefaultEfforts(null).Should().BeFalse();
    }

    [Fact]
    public void 對話的實際預設強度_設定檔優先_否則照目前的模型()
    {
        IReadOnlyList<AgentModelOption> models = [M("gpt-6.1-sol", "low", "low"), M("gpt-6-luna", "medium", "medium")];
        ConversationDefaultsRules.EffectiveDefaultEffort("gpt-6-luna", models, null, null).Should().Be("medium");
        ConversationDefaultsRules.EffectiveDefaultEffort(null, models, null, null).Should().Be("low", "預設模型＝清單第一個（Codex 的 isDefault 排最前）");
        ConversationDefaultsRules.EffectiveDefaultEffort("default", models, "gpt-6-luna", null).Should().Be("medium", "設定檔指定的模型");
        ConversationDefaultsRules.EffectiveDefaultEffort("gpt-6-luna", models, null, "xhigh").Should().Be("xhigh", "config.toml 的 model_reasoning_effort");
        ConversationDefaultsRules.EffectiveDefaultEffort("unknown", models, null, null).Should().BeNull();
        IReadOnlyList<AgentModelOption> claude = [M("default", "medium", "low"), M("claude-opus-4-7", "xhigh", "low")];
        ConversationDefaultsRules.EffectiveDefaultEffort(null, claude, null, null).Should().Be("medium", "Claude 的清單有「預設」那一項");
        ConversationDefaultsRules.EffectiveDefaultEffort("claude-opus-4-7", claude, null, null).Should().Be("xhigh");
    }

    [Fact]
    public void 同步給其他電腦的模型清單帶預設強度_沒有就省略()
    {
        var meta = new AgentConversation { AccountId = "acc-claude", Provider = AgentProvider.Claude, Title = "t" };
        var payload = ConversationWire.Meta(meta, "claude", running: false, waiting: false, models: [M("default", "medium", "low"), M("haiku", null)]);
        payload.Models!.Select(m => m.DefaultEffort).Should().Equal("medium", null);
        var json = SyncJson.Serialize(payload);
        json.Should().Contain("\"defaultEffort\":\"medium\"");
        json.Split("defaultEffort").Length.Should().Be(2, "沒有的不帶");
        ReplicaRules.Models(payload.Models)!.Select(m => m.DefaultEffort).Should().Equal("medium", null);
    }
}
