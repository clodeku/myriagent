using Myriagent.Agents;
using Myriagent.Health;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Health;

/// <summary>帳戶健康度的門檻：全域指示、自動記憶索引、skill 清單、MCP、外掛、設定檔；整體等級、摘要與 JSON</summary>
public sealed class HealthRulesTests
{
    private static AccountScan Scan(AgentProvider provider = AgentProvider.Claude, bool safeMode = false, string? shared = null)
        => new() { Account = new HealthAccountInput("a1", "主帳戶", provider, true, @"C:\h\.claude", safeMode, shared, null) };

    private static InstructionFileStat File(string name, int lines, int tokens, int chars = 1000, long bytes = 1000, string kind = "main")
        => new(@"C:\h\.claude\" + name, kind, new TextMeasure(bytes, chars, lines, tokens), null);

    private static HealthItem Item(AccountHealth h, string key) => h.Items.Single(i => i.Key == key);

    [Fact]
    public void 小的全域指示是良好()
    {
        var s = Scan();
        s.Instructions.Add(File("CLAUDE.md", 80, 1_200));
        var h = HealthRules.Evaluate(s);
        h.Level.Should().Be(HealthLevel.Ok);
        h.Summary.Should().Be("良好");
        h.CanTrim.Should().BeFalse();
        h.Load.Should().Be("約 1,200 token 指示");
        Item(h, "instructions").Value.Should().Be("80 行・約 1,200 token");
    }

    [Fact]
    public void 超過200行或5000token是偏大_可以精簡()
    {
        var lines = Scan();
        lines.Instructions.Add(File("CLAUDE.md", 201, 3_000));
        var h = HealthRules.Evaluate(lines);
        h.Level.Should().Be(HealthLevel.Warn);
        h.Summary.Should().Be("CLAUDE.md 偏大");
        h.CanTrim.Should().BeTrue();
        Item(h, "instructions").Detail.Should().Contain("200 行以內");

        var tokens = Scan();
        tokens.Instructions.Add(File("CLAUDE.md", 84, 12_500));
        tokens.Instructions.Add(File("a.md", 10, 100, kind: "import"));
        var t = HealthRules.Evaluate(tokens);
        t.Level.Should().Be(HealthLevel.Warn);
        var item = Item(t, "instructions");
        item.Value.Should().Be("94 行・約 1.3 萬 token・2 個檔");
        item.Detail.Should().Contain("約占 200K 脈絡的 6%").And.Contain("~/.claude/rules/");
        item.Files.Select(f => f.Label).Should().Equal("主檔　CLAUDE.md", "匯入　a.md");
    }

    [Fact]
    public void 超過15000token或單檔40000字元是過大()
    {
        var big = Scan();
        big.Instructions.Add(File("CLAUDE.md", 150, 15_001));
        HealthRules.Evaluate(big).Level.Should().Be(HealthLevel.Bad);

        var chars = Scan();
        chars.Instructions.Add(File("CLAUDE.md", 150, 4_000, chars: 40_001));
        var h = HealthRules.Evaluate(chars);
        h.Level.Should().Be(HealthLevel.Bad);
        h.Summary.Should().Be("CLAUDE.md 過大");
        Item(h, "instructions").Files[0].Level.Should().Be(HealthLevel.Bad);

        // 40,000 字元的門檻只對 Claude；Codex 只看 token 與行數
        var codex = Scan(AgentProvider.Codex);
        codex.Instructions.Add(File("AGENTS.md", 150, 4_000, chars: 40_001));
        HealthRules.Evaluate(codex).Level.Should().Be(HealthLevel.Ok);
    }

    [Fact]
    public void Antigravity單檔超過24000位元組會被截斷()
    {
        var s = Scan(AgentProvider.Antigravity);
        s.Instructions.Add(File("GEMINI.md", 100, 3_000, bytes: 24_001));
        var h = HealthRules.Evaluate(s);
        h.Level.Should().Be(HealthLevel.Bad);
        h.Summary.Should().Be("GEMINI.md 過大");
        Item(h, "instructions").Detail.Should().Contain("截掉");
    }

    [Fact]
    public void 沒有指示與精簡模式是說明_不拉低整體()
    {
        var none = Scan(AgentProvider.Codex);
        none.MainInstructionPath = @"C:\h\.codex\AGENTS.md";
        var h = HealthRules.Evaluate(none);
        h.Level.Should().Be(HealthLevel.Ok);
        Item(h, "instructions").Level.Should().Be(HealthLevel.Info);
        Item(h, "instructions").Files.Single().Note.Should().Be("不存在");

        var safe = Scan(safeMode: true);
        safe.Instructions.Add(File("CLAUDE.md", 900, 30_000));
        var s = HealthRules.Evaluate(safe);
        s.Level.Should().Be(HealthLevel.Ok, "精簡模式不載入");
        s.Load.Should().BeNull();
        Item(s, "instructions").Value.Should().Be("精簡模式不載入");
    }

    [Fact]
    public void 匯入找不到是注意()
    {
        var s = Scan();
        s.Instructions.Add(File("CLAUDE.md", 10, 100));
        s.MissingImports.Add("docs/gone.md");
        var h = HealthRules.Evaluate(s);
        h.Level.Should().Be(HealthLevel.Warn);
        h.Summary.Should().Be("匯入的檔案找不到");
        Item(h, "instructions").Detail.Should().Contain("docs/gone.md");
    }

    [Fact]
    public void 記憶索引_超過上限是問題_接近是注意()
    {
        var s = Scan(shared: @"C:\h\.claude");
        s.MemoryIndexes = new List<MemoryIndexStat>
        {
            new("C--repo-a", @"C:\a\MEMORY.md", 120, 24_104),
            new("C--repo-b", @"C:\b\MEMORY.md", 30, 2_000),
        };
        var near = HealthRules.Evaluate(s);
        near.Level.Should().Be(HealthLevel.Warn, "24,104 位元組到 25KB 的九成以上");
        Item(near, "memory").Value.Should().Be("2 個專案・1 個接近上限");
        Item(near, "memory").Detail.Should().Contain("共用系統預設帳戶的記憶");
        Item(near, "memory").Files.Single().Label.Should().Be("C--repo-a");
        near.CanTrimMemory.Should().BeTrue("接近上限也可以用代理整理");
        near.CanTrim.Should().BeFalse("全域指示沒問題");

        s.MemoryIndexes.Add(new MemoryIndexStat("C--repo-c", @"C:\c\MEMORY.md", 201, 9_000));
        var over = HealthRules.Evaluate(s);
        over.Level.Should().Be(HealthLevel.Bad);
        over.Summary.Should().Be("記憶索引超過上限");
        Item(over, "memory").Files.Select(f => f.Label).Should().Equal("C--repo-c", "C--repo-a");
        over.CanTrimMemory.Should().BeTrue();

        var small = Scan();
        small.MemoryIndexes = new List<MemoryIndexStat> { new("C--repo-b", @"C:\b\MEMORY.md", 179, 22_499) };
        HealthRules.Evaluate(small).CanTrimMemory.Should().BeFalse("還不到九成");

        var empty = Scan();
        empty.MemoryIndexes = new List<MemoryIndexStat>();
        Item(HealthRules.Evaluate(empty), "memory").Value.Should().Be("還沒有");
        HealthRules.Evaluate(empty).CanTrimMemory.Should().BeFalse();
        HealthRules.Evaluate(Scan(AgentProvider.Codex)).CanTrimMemory.Should().BeFalse("沒有自動記憶的代理");
    }

    [Fact]
    public void 記憶索引的上限判斷_行數或大小任一個()
    {
        HealthRules.MemoryOverLimit(new MemoryIndexStat("p", "m", 201, 100)).Should().BeTrue();
        HealthRules.MemoryOverLimit(new MemoryIndexStat("p", "m", 200, 25_000)).Should().BeFalse("剛好在上限上還載得進來");
        HealthRules.MemoryOverLimit(new MemoryIndexStat("p", "m", 10, 25_001)).Should().BeTrue();
        HealthRules.MemoryNearLimit(new MemoryIndexStat("p", "m", 180, 100)).Should().BeTrue();
        HealthRules.MemoryNearLimit(new MemoryIndexStat("p", "m", 10, 22_500)).Should().BeTrue();
        HealthRules.MemoryNearLimit(new MemoryIndexStat("p", "m", 201, 100)).Should().BeFalse("超過的不算接近");
        HealthRules.MemoryNeedsTrim(new MemoryIndexStat("p", "m", 179, 22_499)).Should().BeFalse();
    }

    [Fact]
    public void skill清單超過預算是注意_Codex只是說明()
    {
        var s = Scan();
        s.Skills = 74;
        s.PluginSkills = 71;
        s.Agents = 6;
        s.SkillListingChars = 25_390;
        s.SkillListingTokens = 6_300;
        var h = HealthRules.Evaluate(s);
        h.Level.Should().Be(HealthLevel.Warn);
        h.Summary.Should().Be("skill 清單偏大");
        h.Load.Should().Be("約 6,300 token skill 清單");
        Item(h, "skills").Value.Should().Be("74 個・清單 25,390 字元・6 個子代理");
        Item(h, "skills").Detail.Should().Contain("71 個是外掛帶來的").And.Contain("skillOverrides").And.Contain("1M 脈絡（約 40,000 字元）還在範圍內");
        s.SkillListingChars = 40_001;
        HealthRules.Evaluate(s).Summary.Should().Be("skill 清單超過預算");

        var codex = Scan(AgentProvider.Codex);
        codex.Skills = 50;
        codex.SkillListingChars = 30_000;
        Item(HealthRules.Evaluate(codex), "skills").Level.Should().Be(HealthLevel.Info);
    }

    [Fact]
    public void MCP十個以上是注意_外掛啟用卻沒安裝是注意()
    {
        var s = Scan();
        s.McpServers = Enumerable.Range(1, 10).Select(i => "m" + i).ToList();
        s.Plugins = new List<PluginStat>
        {
            new("autopilot@autopilot", 30, 0, 4, false, true, false),
            new("gone@market", 0, 0, 0, false, false, true),
        };
        var h = HealthRules.Evaluate(s);
        h.Level.Should().Be(HealthLevel.Warn);
        h.Summary.Should().Be("MCP 伺服器偏多、有啟用卻沒安裝的外掛");
        Item(h, "plugins").Detail.Should().Contain("autopilot@autopilot（34）").And.Contain("gone@market");
    }

    [Fact]
    public void 設定檔讀不懂是問題_claude_json太大是注意()
    {
        var s = Scan();
        s.SettingsPath = @"C:\h\.claude\settings.json";
        s.SettingsError = "Unexpected character";
        s.StateFilePath = @"C:\h\.claude.json";
        s.StateFileBytes = 6L * 1024 * 1024;
        var h = HealthRules.Evaluate(s);
        h.Level.Should().Be(HealthLevel.Bad);
        h.Summary.Should().Be("settings.json 讀不懂");
        var item = Item(h, "settings");
        item.Detail.Should().Contain("Unexpected character").And.Contain("6 MB");
        item.Files.Select(f => f.Level).Should().Equal(HealthLevel.Bad, HealthLevel.Warn);
    }

    [Fact]
    public void JSON的形狀()
    {
        var s = Scan();
        s.Instructions.Add(new InstructionFileStat(@"C:\h\.claude\CLAUDE.md", "main", new TextMeasure(10, 10, 300, 100), @"C:\dot\CLAUDE.md"));
        var json = HealthRules.Render(HealthRules.Evaluate(s));
        json.Value<string>("level").Should().Be("warn");
        json.Value<bool>("canTrim").Should().BeTrue();
        json.Value<bool>("canTrimMemory").Should().BeFalse();
        var item = (JObject)json["items"]![0]!;
        item.Value<string>("key").Should().Be("instructions");
        var file = (JObject)item["files"]![0]!;
        file.Value<string>("target").Should().Be(@"C:\dot\CLAUDE.md");
        file.Value<string>("level").Should().Be("warn");
    }
}
