using Myriagent.Agents;
using Myriagent.Health;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Health;

/// <summary>
/// 健康度用 Claude CLI 自己回報的清單（get_skills_dialog、get_hooks_listing、get_memory_dialog；形狀取自 2.1.310 的探針）：
/// skill 清單照 CLI 的數字、hooks 一項、記憶那一項寫出自動記憶的開關
/// </summary>
public sealed class ClaudeCliInventoryTests
{
    private static AccountScan Scan(AgentProvider provider = AgentProvider.Claude, bool safeMode = false)
        => new() { Account = new HealthAccountInput("a1", "主帳戶", provider, true, @"C:\h\.claude", safeMode, null, null) };

    private static JObject Skills(params (string Name, int Tokens, string Source, bool Advertised)[] skills)
        => new() { ["skills"] = new JArray(skills.Select(s => new JObject { ["name"] = s.Name, ["tokens"] = s.Tokens, ["source"] = s.Source, ["advertised"] = s.Advertised })) };

    private static JObject Hooks(params (string Event, string Source, string? Plugin)[] hooks)
        => new()
        {
            ["hooks"] = new JArray(hooks.Select(h =>
            {
                var o = new JObject { ["event"] = h.Event, ["source"] = h.Source };
                if (h.Plugin != null)
                    o["pluginName"] = h.Plugin;
                return o;
            })),
        };

    private static HealthItem Item(AccountHealth h, string key) => h.Items.Single(i => i.Key == key);

    [Fact]
    public void 解析三個回應()
    {
        var cli = ClaudeCliInventory.Parse(
            Skills(("pdf", 120, "plugin", true), ("", 5, "user", true), ("old", 0, "user", false)),
            Hooks(("PreToolUse", "pluginHook", "autopilot"), ("", "x", null)),
            new JObject { ["auto_memory"] = new JObject { ["enabled"] = true }, ["auto_dream"] = new JObject { ["enabled"] = false } });
        cli.Should().NotBeNull();
        cli!.Skills.Should().Equal(new CliSkill("pdf", 120, "plugin", true), new CliSkill("old", 0, "user", false));
        cli.Hooks.Should().Equal(new CliHook("PreToolUse", "pluginHook", "autopilot"));
        cli.AutoMemory.Should().BeTrue();
        cli.AutoDream.Should().BeFalse();
    }

    [Fact]
    public void 三個都沒拿到是null_缺一個照樣解析()
    {
        ClaudeCliInventory.Parse(null, null, null).Should().BeNull();
        ClaudeCliInventory.Parse(new JObject(), new JObject { ["hooks"] = "x" }, new JObject()).Should().BeNull();
        var hooksOnly = ClaudeCliInventory.Parse(null, Hooks(), null);
        hooksOnly!.Hooks.Should().BeEmpty();
        hooksOnly.Skills.Should().BeNull();
    }

    [Fact]
    public void skill清單照CLI的數字_在預算內是良好()
    {
        var scan = Scan();
        scan.Skills = 99;
        scan.SkillListingTokens = 50_000;
        var cli = ClaudeCliInventory.Parse(Skills(("a", 700, "user", true), ("b", 600, "claude.ai sync", true), ("c", 300, "plugin", true), ("d", 0, "plugin", false)), null, null);
        var h = HealthRules.Evaluate(scan, cli);
        var item = Item(h, "skills");
        item.Level.Should().Be(HealthLevel.Ok);
        item.Value.Should().Be("3 個・清單 約 1,600 token");
        item.Detail.Should().Contain("1 個是外掛帶來的").And.Contain("1 個是 claude.ai 同步過來的").And.Contain("另有 1 個沒有列給 Claude");
        h.Load.Should().Contain("約 1,600 token skill 清單", "負擔用 CLI 的數字、不用自己掃的");
    }

    [Fact]
    public void skill清單超過預算是偏大_超過1M的預算另外寫()
    {
        var over = HealthRules.Evaluate(Scan(), ClaudeCliInventory.Parse(Skills(("a", 3_000, "plugin", true)), null, null));
        Item(over, "skills").Level.Should().Be(HealthLevel.Warn);
        Item(over, "skills").Headline.Should().Be("skill 清單偏大");
        Item(over, "skills").Detail.Should().Contain("skillOverrides");

        var huge = HealthRules.Evaluate(Scan(), ClaudeCliInventory.Parse(Skills(("a", 12_000, "plugin", true)), null, null));
        Item(huge, "skills").Headline.Should().Be("skill 清單超過預算");
    }

    [Fact]
    public void hooks_每次呼叫工具要跑很多時提醒()
    {
        var few = HealthRules.Evaluate(Scan(), ClaudeCliInventory.Parse(null, Hooks(("SessionStart", "userSettings", null), ("PreToolUse", "pluginHook", "p1")), null));
        var item = Item(few, "hooks");
        item.Level.Should().Be(HealthLevel.Info);
        item.Value.Should().Be("2 個・每次呼叫工具 1 個");
        item.Detail.Should().Contain("外掛帶來的：p1（1）");

        var many = Enumerable.Range(0, HealthRules.HooksPerToolWarn).Select(i => ("PostToolUse", "pluginHook", (string?)"p2")).ToArray();
        var heavy = Item(HealthRules.Evaluate(Scan(), ClaudeCliInventory.Parse(null, Hooks(many), null)), "hooks");
        heavy.Level.Should().Be(HealthLevel.Warn);
        heavy.Headline.Should().Be("每次呼叫工具要跑很多 hook");

        Item(HealthRules.Evaluate(Scan(), ClaudeCliInventory.Parse(null, Hooks(), null)), "hooks").Value.Should().Be("沒有");
    }

    [Fact]
    public void 記憶那一項寫出自動記憶的開關()
    {
        var scan = Scan();
        scan.MemoryIndexes = new List<MemoryIndexStat>();
        var cli = ClaudeCliInventory.Parse(null, null, new JObject { ["auto_memory"] = new JObject { ["enabled"] = true }, ["auto_dream"] = new JObject { ["enabled"] = false } });
        Item(HealthRules.Evaluate(scan, cli), "memory").Detail.Should().EndWith("自動記憶開著、auto dream 關著（Claude CLI 回報）。");
    }

    [Fact]
    public void 其他代理與精簡模式不用CLI的清單()
    {
        var cli = ClaudeCliInventory.Parse(Skills(("a", 3_000, "plugin", true)), Hooks(("PreToolUse", "x", null)), null);
        var codex = HealthRules.Evaluate(Scan(AgentProvider.Codex), cli);
        codex.Items.Should().NotContain(i => i.Key == "hooks" || i.Key == "skills");
        var safe = HealthRules.Evaluate(Scan(safeMode: true), cli);
        safe.Items.Should().NotContain(i => i.Key == "hooks");
    }
}
