using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 設定頁「模型設定」的純函式規則：每家一個預設模型與預設權限模式、每個模型一個預設推理強度；新對話一律套用，對話內修改不外溢；
/// 舊版「沿用上次選擇」的升級遷移
/// </summary>
public class ConversationDefaultsRulesTests
{
    private static readonly string[] Five = { "low", "medium", "high", "xhigh", "max" };

    private static readonly List<AgentModelOption> Models = new()
    {
        new("default", "預設", null, Five),
        new("opus", "Opus", null, Five),
        new("claude-opus-4-6", "Opus 4.6", null, new[] { "low", "medium", "high", "max" }),
        new("claude-haiku-4-5", "Haiku 4.5", null, Array.Empty<string>()),
    };

    private static ProviderConversationDefaults With(params (string Model, string Effort)[] efforts)
    {
        var d = new ProviderConversationDefaults();
        foreach (var (model, effort) in efforts)
            d.Efforts[model] = effort;
        return d;
    }

    [Fact]
    public void ModelKey_沒指定模型是default()
    {
        ConversationDefaultsRules.ModelKey(null).Should().Be("default");
        ConversationDefaultsRules.ModelKey("  ").Should().Be("default");
        ConversationDefaultsRules.ModelKey(" opus ").Should().Be("opus");
    }

    [Fact]
    public void ModelFor_沿用上次選擇用帳戶記住的_指定的模型帳戶有才用_default或沒有是帳戶預設()
    {
        ConversationDefaultsRules.ModelFor(null, "sonnet", Models).Should().Be("sonnet");
        ConversationDefaultsRules.ModelFor(new ProviderConversationDefaults(), "sonnet", Models).Should().Be("sonnet", "沒設定＝沿用上次選擇");
        ConversationDefaultsRules.ModelFor(new ProviderConversationDefaults(), null, Models).Should().BeNull();
        ConversationDefaultsRules.ModelFor(new ProviderConversationDefaults { Model = " " }, "sonnet", Models).Should().Be("sonnet");

        ConversationDefaultsRules.ModelFor(new ProviderConversationDefaults { Model = "opus" }, "sonnet", Models).Should().Be("opus", "指定了就不看上次選的");
        ConversationDefaultsRules.ModelFor(new ProviderConversationDefaults { Model = "default" }, "sonnet", Models).Should().BeNull("帳戶預設模型");
        ConversationDefaultsRules.ModelFor(new ProviderConversationDefaults { Model = "claude-opus-9" }, "sonnet", Models)
            .Should().BeNull("這個帳戶沒有這個模型：用帳戶預設模型，不是上次選的");
        ConversationDefaultsRules.ModelFor(new ProviderConversationDefaults { Model = "claude-opus-9" }, null, Array.Empty<AgentModelOption>())
            .Should().Be("claude-opus-9", "還不知道模型清單時照設定值");
    }

    [Fact]
    public void EffortFor_有設定且模型支援用設定值_沒設定或不支援是CLI預設()
    {
        var d = With(("default", "high"), ("opus", "max"), ("claude-opus-4-6", "xhigh"), ("claude-haiku-4-5", "low"));

        ConversationDefaultsRules.EffortFor(d, null, Models).Should().Be("high", "沒指定模型＝default 那一列");
        ConversationDefaultsRules.EffortFor(d, "opus", Models).Should().Be("max");
        ConversationDefaultsRules.EffortFor(d, "claude-opus-4-6", Models).Should().BeNull("4.6 沒有 xhigh（手改設定檔或模型清單變了）");
        ConversationDefaultsRules.EffortFor(d, "claude-haiku-4-5", Models).Should().BeNull("不支援推理強度的模型");
        ConversationDefaultsRules.EffortFor(d, "sonnet", Models).Should().BeNull("sonnet 沒有設定值");
        ConversationDefaultsRules.EffortFor(null, "opus", Models).Should().BeNull();
        ConversationDefaultsRules.EffortFor(d, "opus", Array.Empty<AgentModelOption>()).Should().Be("max", "還不知道模型清單時照設定值");
    }

    [Fact]
    public void EffortAfterModelChange_新模型有設定換成設定值_沒設定沿用目前_不支援就清掉()
    {
        var d = With(("opus", "max"));

        ConversationDefaultsRules.EffortAfterModelChange(d, "low", "opus", Models).Should().Be("max");
        ConversationDefaultsRules.EffortAfterModelChange(d, "xhigh", null, Models).Should().Be("xhigh", "default 沒有設定值：沿用目前的");
        ConversationDefaultsRules.EffortAfterModelChange(d, "xhigh", "claude-opus-4-6", Models).Should().BeNull("4.6 不支援 xhigh");
        ConversationDefaultsRules.EffortAfterModelChange(null, "medium", "claude-opus-4-6", Models).Should().Be("medium");
    }

    [Fact]
    public void PermissionFor_沒有設定是每步詢問_Codex換成最接近的模式()
    {
        ConversationDefaultsRules.PermissionFor(AgentProvider.Claude, null).Should().Be(AgentPermissionMode.Ask);
        ConversationDefaultsRules.PermissionFor(AgentProvider.Claude, new ProviderConversationDefaults { PermissionMode = AgentPermissionMode.Plan })
            .Should().Be(AgentPermissionMode.Plan);
        ConversationDefaultsRules.PermissionFor(AgentProvider.Codex, new ProviderConversationDefaults { PermissionMode = AgentPermissionMode.AcceptEdits })
            .Should().Be(AgentPermissionMode.Auto);
        ConversationDefaultsRules.PermissionFor(AgentProvider.Codex, new ProviderConversationDefaults { PermissionMode = AgentPermissionMode.FullAccess })
            .Should().Be(AgentPermissionMode.FullAccess);
    }

    [Fact]
    public void Migrate_每家取上次用的帳戶_否則依帳戶順序第一個有記錄的_推理強度不搬()
    {
        var accounts = new[] { ("c1", AgentProvider.Claude), ("x1", AgentProvider.Codex), ("c2", AgentProvider.Claude), ("x2", AgentProvider.Codex) };
        var previous = new Dictionary<string, AccountDefaults>
        {
            ["c1"] = new() { Model = "opus", PermissionMode = AgentPermissionMode.Plan },
            ["c2"] = new() { PermissionMode = AgentPermissionMode.Auto },
            ["x2"] = new() { PermissionMode = AgentPermissionMode.FullAccess },
            ["x1"] = new() { Model = "gpt-6" },   // 沒記權限模式：跳過
            ["gone"] = new() { PermissionMode = AgentPermissionMode.FullAccess },   // 已移除的帳戶
        };

        var migrated = ConversationDefaultsRules.Migrate(accounts, previous, lastAccountId: "c2");

        migrated.Keys.Should().BeEquivalentTo("Claude", "Codex", "Antigravity", "Grok");
        migrated["Claude"].PermissionMode.Should().Be(AgentPermissionMode.Auto, "上次用的帳戶 c2 是 Claude");
        migrated["Codex"].PermissionMode.Should().Be(AgentPermissionMode.FullAccess, "上次用的不是 Codex：依帳戶順序第一個有記錄的 x2");
        migrated.Values.Should().OnlyContain(d => d.Efforts.Count == 0, "舊的推理強度是整個帳戶共用、不分模型，不搬");

        var empty = ConversationDefaultsRules.Migrate(accounts, new Dictionary<string, AccountDefaults>(), lastAccountId: null);
        empty["Claude"].PermissionMode.Should().Be(AgentPermissionMode.Ask);
        empty["Codex"].PermissionMode.Should().Be(AgentPermissionMode.Ask);
        empty["Antigravity"].PermissionMode.Should().Be(AgentPermissionMode.AcceptEdits, "Antigravity 不能在對話中詢問，沒有每步詢問");
        empty["Grok"].PermissionMode.Should().Be(AgentPermissionMode.Ask, "Grok 走 ACP 核准請求，五種模式都有");

        ConversationDefaultsRules.Migrate(accounts, new Dictionary<string, AccountDefaults> { ["x1"] = new() { PermissionMode = AgentPermissionMode.Plan } }, "x1")
            ["Codex"].PermissionMode.Should().Be(AgentPermissionMode.Ask, "Codex 沒有規劃模式，換成最接近的");
    }

    [Fact]
    public void ProviderModels_各帳戶回報的聯集_依帳戶順序去重_都沒回報用內建清單()
    {
        var a = new List<AgentModelOption> { new("default", "預設", null, Five), new("opus", "Opus", null, Five) };
        var b = new List<AgentModelOption> { new("opus", "Opus（另一個帳戶）", null, new[] { "low" }), new("sonnet", "Sonnet", null, Five) };
        var fallback = new List<AgentModelOption> { new("default", "內建", null, Five) };

        var merged = ConversationDefaultsRules.ProviderModels(new[] { a, new List<AgentModelOption>(), b }, fallback);
        merged.Select(m => m.Value).Should().Equal("default", "opus", "sonnet");
        merged.Single(m => m.Value == "opus").Label.Should().Be("Opus", "同一個模型取第一個帳戶回報的");

        ConversationDefaultsRules.ProviderModels(new[] { new List<AgentModelOption>() }, fallback).Should().Equal(fallback);
        ConversationDefaultsRules.ProviderModels(Array.Empty<List<AgentModelOption>>(), fallback).Should().Equal(fallback);
    }

    [Fact]
    public void SameModels_逐項比較內容()
    {
        var a = new List<AgentModelOption> { new("opus", "Opus", null, new[] { "low", "max" }) };
        ConversationDefaultsRules.SameModels(a, new List<AgentModelOption> { new("opus", "Opus", null, new[] { "low", "max" }) }).Should().BeTrue("強度清單是不同的陣列實體，內容相同");
        ConversationDefaultsRules.SameModels(a, new List<AgentModelOption> { new("opus", "Opus", null, new[] { "low" }) }).Should().BeFalse();
        ConversationDefaultsRules.SameModels(a, new List<AgentModelOption> { new("opus", "Opus 5", null, new[] { "low", "max" }) }).Should().BeFalse();
        ConversationDefaultsRules.SameModels(null, a).Should().BeFalse();
        ConversationDefaultsRules.SameModels(null, new List<AgentModelOption>()).Should().BeTrue();
    }

    [Fact]
    public void SameCommands_逐項比較內容與順序_沒記過的算不同()
    {
        var stored = new List<AgentSlashCommand> { new("review", "審查", null), new("init", null, "[路徑]") };
        ConversationDefaultsRules.SameCommands(stored, new[] { new AgentSlashCommand("review", "審查", null), new AgentSlashCommand("init", null, "[路徑]") })
            .Should().BeTrue("不同的實體、內容相同");
        ConversationDefaultsRules.SameCommands(stored, new[] { new AgentSlashCommand("init", null, "[路徑]"), new AgentSlashCommand("review", "審查", null) })
            .Should().BeFalse("順序不同（選單照回報的順序列）");
        ConversationDefaultsRules.SameCommands(stored, new[] { new AgentSlashCommand("review", "審查程式碼", null), new AgentSlashCommand("init", null, "[路徑]") })
            .Should().BeFalse();
        ConversationDefaultsRules.SameCommands(null, Array.Empty<AgentSlashCommand>()).Should().BeFalse("沒記過要存一次空清單");
    }

    [Fact]
    public void TrySetPermission_只收這一家支援的模式()
    {
        var claude = new ProviderConversationDefaults();
        ConversationDefaultsRules.TrySetPermission(claude, AgentProvider.Claude, "Plan").Should().BeTrue();
        claude.PermissionMode.Should().Be(AgentPermissionMode.Plan);

        var codex = new ProviderConversationDefaults { PermissionMode = AgentPermissionMode.Auto };
        ConversationDefaultsRules.TrySetPermission(codex, AgentProvider.Codex, "Plan").Should().BeFalse("Codex 沒有規劃模式");
        ConversationDefaultsRules.TrySetPermission(codex, AgentProvider.Codex, "不存在").Should().BeFalse();
        ConversationDefaultsRules.TrySetPermission(codex, AgentProvider.Codex, null).Should().BeFalse();
        codex.PermissionMode.Should().Be(AgentPermissionMode.Auto);
        ConversationDefaultsRules.TrySetPermission(codex, AgentProvider.Codex, "FullAccess").Should().BeTrue();
        codex.PermissionMode.Should().Be(AgentPermissionMode.FullAccess);
    }

    [Fact]
    public void TrySetModel_空白是沿用上次選擇_不在這一家的清單就拒絕()
    {
        var d = new ProviderConversationDefaults();
        ConversationDefaultsRules.TrySetModel(d, " opus ", Models).Should().BeTrue();
        d.Model.Should().Be("opus");
        ConversationDefaultsRules.TrySetModel(d, "default", Models).Should().BeTrue("帳戶預設模型也是一個選擇");
        d.Model.Should().Be("default");

        ConversationDefaultsRules.TrySetModel(d, "gpt-6", Models).Should().BeFalse("不在這一家的模型清單");
        ConversationDefaultsRules.TrySetModel(d, "Opus", Models).Should().BeFalse("value 分大小寫");
        d.Model.Should().Be("default");

        ConversationDefaultsRules.TrySetModel(d, "", Models).Should().BeTrue("空白＝沿用上次選擇");
        d.Model.Should().BeNull();
        d.Model = "opus";
        ConversationDefaultsRules.TrySetModel(d, null, Models).Should().BeTrue();
        d.Model.Should().BeNull();
    }

    [Fact]
    public void TrySetEffort_default清除_模型或強度不在清單就拒絕()
    {
        var d = new ProviderConversationDefaults();
        ConversationDefaultsRules.TrySetEffort(d, "opus", "max", Models).Should().BeTrue();
        ConversationDefaultsRules.TrySetEffort(d, null, "high", Models).Should().BeTrue("沒指定模型＝default 那一列");
        d.Efforts.Should().BeEquivalentTo(new Dictionary<string, string> { ["opus"] = "max", ["default"] = "high" });

        ConversationDefaultsRules.TrySetEffort(d, "claude-opus-4-6", "xhigh", Models).Should().BeFalse("4.6 沒有 xhigh");
        ConversationDefaultsRules.TrySetEffort(d, "claude-haiku-4-5", "low", Models).Should().BeFalse("不支援推理強度");
        ConversationDefaultsRules.TrySetEffort(d, "gpt-6", "low", Models).Should().BeFalse("不在這一家的模型清單");
        d.Efforts.Should().HaveCount(2);

        ConversationDefaultsRules.TrySetEffort(d, "opus", "default", Models).Should().BeTrue("default＝清除，回到 CLI 預設");
        ConversationDefaultsRules.TrySetEffort(d, "default", null, Models).Should().BeTrue();
        d.Efforts.Should().BeEmpty();
    }
}
