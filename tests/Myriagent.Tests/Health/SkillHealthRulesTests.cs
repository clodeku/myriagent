using Myriagent.Agents;
using Myriagent.Health;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Health;

/// <summary>儀表板逐一列出的 skill：用途摘要、各項健康度檢查、CLI 清單與掃描結果的對應、排序、等級與 JSON</summary>
public sealed class SkillHealthRulesTests
{
    private static AccountScan Scan(AgentProvider provider = AgentProvider.Claude)
        => new() { Account = new HealthAccountInput("a1", "主帳戶", provider, true, @"C:\h\.claude", false, null, null) };

    private static SkillStat Stat(string name, string description, string source = "user", string? plugin = null, string kind = "skill",
        bool frontMatter = true, int bodyLines = 20, string? declared = null, string whenToUse = "", bool readable = true, params string[] missing)
        => new(name, kind, source, plugin, @"C:\h\.claude\skills\" + name + @"\SKILL.md", readable, frontMatter, declared ?? name, description, whenToUse, bodyLines, missing);

    private static SkillView One(AccountScan s, IReadOnlyList<CliSkill>? cli = null) => SkillHealthRules.Evaluate(s, cli).Single();

    [Theory]
    [InlineData("Create new skills, modify existing skills, and measure performance. Use when users want to create a skill.", "Create new skills, modify existing skills, and measure performance.")]
    [InlineData("用 影片生成服務 製作廣告短影片的完整流程：企劃、定稿。只要使用者提到 影片生成服務 都要用。", "用 影片生成服務 製作廣告短影片的完整流程：企劃、定稿。")]
    [InlineData("Handles files, e.g. PDFs and images. Use often.", "Handles files, e.g. PDFs and images.")]
    [InlineData("  多行\n  的描述  ", "多行 的描述")]
    [InlineData("", "")]
    public void 用途取描述的第一句(string description, string purpose)
        => SkillHealthRules.Purpose(description).Should().Be(purpose);

    [Fact]
    public void 用途太長就截斷()
    {
        var p = SkillHealthRules.Purpose(new string('字', 300));
        p.Should().HaveLength(SkillHealthRules.PurposeMaxChars).And.EndWith("…");
    }

    [Fact]
    public void 良好的skill沒有發現()
    {
        var s = Scan();
        s.SkillList.Add(Stat("deploy-site", "Deploy the static site to IIS and verify it with curl. Use after pushing to main."));
        var v = One(s);
        v.Level.Should().Be(HealthLevel.Ok);
        v.Issues.Should().BeEmpty();
        v.Purpose.Should().Be("Deploy the static site to IIS and verify it with curl.");
        v.Tokens.Should().BeGreaterThan(0, "沒有 CLI 時粗估");
        v.Listed.Should().BeTrue();
    }

    [Fact]
    public void 沒有描述與沒有前置資料是問題_指令沒有描述只是說明()
    {
        var s = Scan();
        s.SkillList.Add(Stat("bare", "", frontMatter: false));
        var v = One(s);
        v.Level.Should().Be(HealthLevel.Bad);
        v.Issues.Select(i => i.Text).Should().Contain(t => t.Contains("前置資料")).And.Contain(t => t.Contains("沒有描述"));

        var c = Scan();
        c.SkillList.Add(Stat("deploy", "", kind: "command", frontMatter: false));
        var cmd = One(c);
        cmd.Level.Should().Be(HealthLevel.Ok, "指令的前置資料可有可無");
        cmd.Issues.Single().Level.Should().Be(HealthLevel.Info);
        cmd.Label.Should().Be("/deploy");
    }

    [Fact]
    public void 描述太短_太長_本文太長_連結不存在是注意()
    {
        var s = Scan();
        s.SkillList.Add(Stat("short-one", "Test Plan"));
        One(s).Issues.Single().Text.Should().Contain("描述太短");

        var l = Scan();
        l.SkillList.Add(Stat("long-one", new string('a', 1_400), whenToUse: new string('b', 200)));
        var lv = One(l);
        lv.Issues.Single().Text.Should().Contain("超過 1,536 字");
        lv.Description.Should().EndWith("…").And.HaveLength(SkillHealthRules.DescriptionCap + 1);

        var b = Scan();
        b.SkillList.Add(Stat("big-body", "A skill with a very long body that explains everything in detail.", bodyLines: 812, missing: ["references/gone.md"]));
        var bv = One(b);
        bv.Level.Should().Be(HealthLevel.Warn);
        bv.Issues.Select(i => i.Text).Should().Contain(t => t.Contains("812 行")).And.Contain(t => t.Contains("references/gone.md"));
    }

    [Fact]
    public void 名稱不合規範只是說明_只看使用者層()
    {
        SkillHealthRules.ValidName("my-skill-2").Should().BeTrue();
        SkillHealthRules.ValidName("My_Skill").Should().BeFalse();
        SkillHealthRules.ValidName(new string('a', 65)).Should().BeFalse();
        var s = Scan();
        s.SkillList.Add(Stat("my", "Does useful things for the user in every project.", declared: "My Skill"));
        var v = One(s);
        v.Level.Should().Be(HealthLevel.Ok);
        v.Issues.Single().Level.Should().Be(HealthLevel.Info);
    }

    [Fact]
    public void CLI的清單為準_對回掃描到的檔_停用的不列()
    {
        var s = Scan();
        s.SkillList.Add(Stat("mine", "我的 skill，整理每天的訂單異常並寫成報告。"));
        s.SkillList.Add(Stat("brutalist-skill", "Raw mechanical interfaces fusing Swiss typographic print.", source: "plugin", plugin: "taste-skill", declared: "industrial-brutalist-ui"));
        s.SyncedSkills["docx"] = Stat("docx", "Use this skill whenever the user wants Word documents.", source: "synced");
        var cli = new List<CliSkill>
        {
            new("mine", 40, "user", true),
            new("taste-skill:brutalist-skill", 105, "plugin", true, "taste-skill:industrial-brutalist-ui"),
            new("anthropic-skills:docx", 319, "claude.ai sync", true, "docx", "Use this skill whenever…", "on", "docx"),
            new("cloud-only", 30, "claude.ai sync", true, null, "Only the CLI knows this one and it has a description."),
            new("off-one", 10, "user", false, null, "停用的", "off"),
        };
        var list = SkillHealthRules.Evaluate(s, cli);
        list.Select(v => v.Name).Should().BeEquivalentTo("mine", "taste-skill:brutalist-skill", "anthropic-skills:docx", "cloud-only");
        SkillHealthRules.DisabledCount(cli).Should().Be(1);

        var plugin = list.Single(v => v.Source == "plugin");
        plugin.Label.Should().Be("taste-skill:industrial-brutalist-ui");
        plugin.Plugin.Should().Be("taste-skill");
        plugin.Path.Should().EndWith(@"brutalist-skill\SKILL.md");
        plugin.Tokens.Should().Be(105, "token 數用 CLI 算的");

        var docx = list.Single(v => v.Name == "anthropic-skills:docx");
        docx.Source.Should().Be("synced");
        docx.Path.Should().NotBeNull("claude.ai 同步的 skill 從 synced 資料夾對回來");
        docx.Description.Should().Be("Use this skill whenever the user wants Word documents.", "有檔時用檔裡的描述");

        var cloud = list.Single(v => v.Name == "cloud-only");
        cloud.Path.Should().BeNull();
        cloud.Description.Should().StartWith("Only the CLI");

        list.Select(v => v.Source).Should().Equal("user", "synced", "synced", "plugin");
    }

    [Fact]
    public void CLI讀到的描述和檔案不同_或YAML寫法容易讀錯是注意()
    {
        var s = Scan();
        s.SkillList.Add(Stat("test-plan", "Test planning and baseline management. Not for: TDD.", source: "plugin", plugin: "workflow")
            with { FrontMatterProblem = "description 的值沒加引號、裡面有「: 」" });
        var v = One(s, [new CliSkill("workflow:test-plan", 12, "plugin", true, null, "Test Plan Guide")]);
        v.Issues.Single().Text.Should().Contain("CLI 讀到的描述是「Test Plan Guide」").And.Contain("用引號包起來");
        v.Description.Should().StartWith("Test planning", "顯示檔案寫的");

        var same = Scan();
        same.SkillList.Add(Stat("mine", "Deploy the static site to IIS and verify it.") with { FrontMatterProblem = "x" });
        var w = One(same, [new CliSkill("mine", 20, "user", true, null, "Deploy the static site to IIS and verify it.")]);
        w.Issues.Single().Text.Should().StartWith("前置資料的 YAML 寫法容易讀錯");

        var truncated = Scan();
        truncated.SkillList.Add(Stat("mine", "Deploy the static site to IIS and verify it."));
        One(truncated, [new CliSkill("mine", 20, "user", true, null, "Deploy the static…")]).Issues.Should().BeEmpty("CLI 截短的不算不同");
    }

    [Theory]
    [InlineData("---\nname: a\ndescription: Do things. Not for: other things.\n---\n", true)]
    [InlineData("---\nname: a\ndescription: \"Do things. Not for: other things.\"\n---\n", false)]
    [InlineData("---\nname: a\ndescription: >\n  Not for: x\n---\n", false)]
    [InlineData("---\nname: a\ndescription: Use C# #1 tips\n---\n", true)]
    [InlineData("---\nname: a\ndescription: plain words\nallowed-tools: Bash(git: *)\n---\n", false)]
    [InlineData("no front matter: here", false)]
    public void 前置資料的YAML寫法(string text, bool problem)
        => (HealthTextRules.FrontMatterProblem(text) != null).Should().Be(problem);

    [Fact]
    public void 同名重複_與沒有列給模型()
    {
        var s = Scan();
        var cli = new List<CliSkill>
        {
            new("anthropic-skills:skill-creator", 117, "claude.ai sync", true, null, "Create new skills and measure performance.", "on", "skill-creator"),
            new("skill-creator:skill-creator", 116, "plugin", true, null, "Create new skills and measure performance."),
            new("codex:helper", 35, "plugin", false, null, "Internal helper contract for calling the runtime."),
        };
        var list = SkillHealthRules.Evaluate(s, cli);
        list.Where(v => v.Name.EndsWith("skill-creator")).Should().OnlyContain(v => v.Level == HealthLevel.Warn && v.Issues.Any(i => i.Text.Contains("同名")));
        var hidden = list.Single(v => v.Name == "codex:helper");
        hidden.Listed.Should().BeFalse();
        hidden.Level.Should().Be(HealthLevel.Ok);
        hidden.Issues.Single().Text.Should().Contain("沒有列給模型");
        list[0].Level.Should().Be(HealthLevel.Warn, "有問題的排在前面");
    }

    [Fact]
    public void 使用者自己的skill有問題才拉高等級_外掛的只列出()
    {
        var plugin = Scan();
        plugin.Skills = 1;
        plugin.PluginSkills = 1;
        plugin.SkillListingChars = 100;
        plugin.SkillList.Add(Stat("tiny", "Test Plan", source: "plugin", plugin: "workflow"));
        var item = HealthRules.Evaluate(plugin).Items.Single(i => i.Key == "skills");
        item.Level.Should().Be(HealthLevel.Ok);
        item.Skills!.Single().Level.Should().Be(HealthLevel.Warn);
        item.Detail.Should().Contain("1 個需要注意").And.Contain("不計入等級");

        var mine = Scan();
        mine.Skills = 1;
        mine.SkillListingChars = 100;
        mine.SkillList.Add(Stat("bare", "", frontMatter: false));
        var h = HealthRules.Evaluate(mine);
        h.Items.Single(i => i.Key == "skills").Level.Should().Be(HealthLevel.Bad);
        h.Summary.Should().Be("skill「bare」有問題");
    }

    [Fact]
    public void JSON只有skills那一項帶清單()
    {
        var s = Scan();
        s.Skills = 1;
        s.SkillList.Add(Stat("deploy-site", "Deploy the static site to IIS and verify it with curl.", missing: ["a.md"]));
        var json = HealthRules.Render(HealthRules.Evaluate(s));
        var items = json["items"]!.OfType<JObject>().ToList();
        items.Single(i => i.Value<string>("key") == "instructions").ContainsKey("skills").Should().BeFalse();
        var skill = (JObject)items.Single(i => i.Value<string>("key") == "skills")["skills"]![0]!;
        skill.Value<string>("name").Should().Be("deploy-site");
        skill.Value<string>("label").Should().Be("deploy-site");
        skill.Value<string>("kind").Should().Be("skill");
        skill.Value<string>("source").Should().Be("user");
        skill.Value<string>("purpose").Should().Be("Deploy the static site to IIS and verify it with curl.");
        skill.Value<string>("level").Should().Be("warn");
        skill.Value<bool>("listed").Should().BeTrue();
        skill.Value<string>("path").Should().EndWith("SKILL.md");
        ((JObject)skill["issues"]![0]!).Value<string>("level").Should().Be("warn");
    }
}
