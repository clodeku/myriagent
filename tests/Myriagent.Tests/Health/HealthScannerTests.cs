using Myriagent.Agents;
using Myriagent.Health;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Health;

/// <summary>
/// 健康度的掃描（暫存資料夾當使用者資料夾，絕不碰真的 ~/.claude、~/.codex）：Claude 的主檔、匯入、規則、記憶、skill、外掛、MCP、設定檔；
/// Codex 的 AGENTS.override.md 與 config.toml；Grok 的 Claude 相容；Antigravity 的 GEMINI.md；電腦層級的指示檔清單
/// </summary>
public sealed class HealthScannerTests : IDisposable
{
    private readonly TempDirectory temp = new("health");

    public void Dispose() => temp.Dispose();

    private string Home => temp.Root;

    private HealthAccountInput Claude(string? shared = null, bool safeMode = false)
        => new("acc-claude", "Claude", AgentProvider.Claude, true, temp.Combine(".claude"), safeMode, shared, temp.Combine(".claude.json"));

    private HealthAccountInput Codex(string id = "acc-codex", bool isDefault = true, string? dir = null, string? shared = null)
        => new(id, "Codex 帳戶", AgentProvider.Codex, isDefault, dir ?? temp.Combine(".codex"), false, shared, null);

    private HealthAccountInput Grok(string? dir = null)
        => new("acc-grok", "Grok", AgentProvider.Grok, dir == null, dir ?? temp.Combine(".grok"), false, null, null);

    private HealthAccountInput Antigravity(bool safeMode = false)
        => new("local-antigravity", "Antigravity", AgentProvider.Antigravity, true, temp.Combine(".gemini", "antigravity-cli"), safeMode, null, null);

    private HealthScanResult Scan(params HealthAccountInput[] accounts)
        => new HealthScanner(new HealthMachine(Home)).Scan(accounts, CancellationToken.None);

    [Fact]
    public void Claude_主檔匯入規則記憶skill外掛MCP()
    {
        temp.WriteFile(@".claude\CLAUDE.md", "# 全域\n\n- 用繁體中文\n- 細節見 @docs/a.md 與 @~/notes/b.md 以及 @docs/gone.md\n<!-- 註解不算 -->\n");
        temp.WriteFile(@".claude\docs\a.md", "A 文件，再匯入 @c.md\n");
        temp.WriteFile(@".claude\docs\c.md", "C 文件\n");
        temp.WriteFile(@"notes\b.md", "B 筆記\n");
        temp.WriteFile(@".claude\rules\always.md", "每次都載入的規則\n");
        temp.WriteFile(@".claude\rules\ts\scoped.md", "---\npaths:\n  - \"src/**/*.ts\"\n---\n只在 TS 檔載入\n");
        temp.WriteFile(@".claude\projects\C--repo-a\memory\MEMORY.md", string.Concat(Enumerable.Repeat("- 一條記憶\n", 210)));
        temp.WriteFile(@".claude\projects\C--repo-b\memory\MEMORY.md", "- 一條\n");
        temp.CreateDirectory(@".claude\projects\C--repo-c");
        temp.WriteFile(@".claude\skills\mine\SKILL.md", "---\nname: mine\ndescription: 我的 skill\n---\n內容\n");
        temp.WriteFile(@".claude\skills\.trash\old\SKILL.md", "---\nname: old\n---\n");
        temp.WriteFile(@".claude\skills\synced\cloud\SKILL.md", "---\nname: cloud\ndescription: 同步下來的\n---\n");
        temp.WriteFile(@".claude\commands\deploy.md", "---\ndescription: 部署\n---\n");
        temp.WriteFile(@".claude\agents\reviewer.md", "---\nname: reviewer\ndescription: 審查\n---\n");
        var plugin = temp.CreateDirectory(@"plugins\cache\market\p1\1.0.0");
        temp.WriteFile(@"plugins\cache\market\p1\1.0.0\skills\s1\SKILL.md", "---\nname: s1\ndescription: 外掛的 skill\n---\n");
        temp.WriteFile(@"plugins\cache\market\p1\1.0.0\agents\a1.md", "---\nname: a1\n---\n");
        temp.WriteFile(@"plugins\cache\market\p1\1.0.0\.mcp.json", "{ \"browser\": { \"command\": \"npx\" } }");
        temp.WriteFile(@"plugins\cache\market\p1\1.0.0\hooks\hooks.json", "{}");
        temp.WriteFile(@".claude\plugins\installed_plugins.json",
            "{ \"version\": 2, \"plugins\": { \"p1@market\": [ { \"installPath\": " + Newtonsoft.Json.JsonConvert.ToString(plugin) + " } ] } }");
        temp.WriteFile(@".claude\settings.json", "{ \"enabledPlugins\": { \"p1@market\": true, \"off@market\": false, \"gone@market\": true } }");
        temp.WriteFile(".claude.json", "{ \"mcpServers\": { \"higgsfield\": {}, \"meta\": {} }, \"projects\": {} }");

        var scan = Scan(Claude()).Accounts.Single();
        scan.MainInstructionPath.Should().Be(temp.Combine(".claude", "CLAUDE.md"));
        scan.Instructions.Select(f => (Path.GetFileName(f.Path), f.Kind)).Should().Equal(
            ("CLAUDE.md", "main"), ("a.md", "import"), ("c.md", "import"), ("b.md", "import"), ("always.md", "rule"));
        scan.Instructions[0].Measure.Lines.Should().Be(4, "HTML 註解那一行不算");
        scan.MissingImports.Should().Equal("docs/gone.md");
        scan.ScopedRules.Should().Be(1);
        scan.MemoryIndexes!.Select(m => (m.Project, m.Lines)).Should().BeEquivalentTo(new[] { ("C--repo-a", 210), ("C--repo-b", 1) });
        scan.Skills.Should().Be(4, "mine、cloud、deploy 指令、外掛的 s1（.trash 不算）");
        scan.PluginSkills.Should().Be(1);
        scan.Agents.Should().Be(2);
        scan.SkillListingChars.Should().BeGreaterThan(0);
        scan.McpServers.Should().Equal("higgsfield", "meta", "browser（p1）");
        scan.Plugins!.Select(p => (p.Id, p.Missing, p.Mcp, p.Hooks)).Should().Equal(("p1@market", false, true, true), ("gone@market", true, false, false));
        scan.SettingsError.Should().BeNull();
        scan.StateFileBytes.Should().BeGreaterThan(0);

        var health = HealthRules.Evaluate(scan);
        health.Items.Single(i => i.Key == "memory").Level.Should().Be(HealthLevel.Bad, "210 行超過 200 行");
    }

    [Fact]
    public void Claude_設定檔讀不懂_共用設定時記憶看來源()
    {
        var source = temp.CreateDirectory("source-claude");
        temp.WriteFile(@"source-claude\projects\C--x\memory\MEMORY.md", "- 來源的記憶\n");
        temp.WriteFile(@"iso\settings.json", "{ 壞掉");
        temp.WriteFile(@"iso\projects\C--own\memory\MEMORY.md", "- 自己的\n");
        var input = new HealthAccountInput("acc-iso", "隔離", AgentProvider.Claude, false, temp.Combine("iso"), false, source, null);
        var scan = Scan(input).Accounts.Single();
        scan.SettingsError.Should().NotBeNullOrEmpty();
        scan.MemoryIndexes!.Single().Project.Should().Be("C--x");
        scan.Instructions.Should().BeEmpty();
        HealthRules.Evaluate(scan).Level.Should().Be(HealthLevel.Bad);
    }

    [Fact]
    public void 匯入循環不會無限追()
    {
        temp.WriteFile(@".claude\CLAUDE.md", "@x.md\n");
        temp.WriteFile(@".claude\x.md", "@y.md\n");
        temp.WriteFile(@".claude\y.md", "@x.md 與 @CLAUDE.md\n");
        Scan(Claude()).Accounts.Single().Instructions.Should().HaveCount(3);
    }

    [Fact]
    public void Codex_override有內容才用_config的MCP()
    {
        temp.WriteFile(@".codex\AGENTS.md", "- 一般指示\n");
        temp.WriteFile(@".codex\AGENTS.override.md", "  \n");
        temp.WriteFile(@".codex\config.toml", "[mcp_servers.context7]\ncommand = \"npx\"\n[mcp_servers.context7.env]\nA = \"1\"\n");
        temp.WriteFile(@".codex\skills\.system\builtin\SKILL.md", "---\nname: builtin\n---\n");
        temp.WriteFile(@".codex\skills\mine\SKILL.md", "---\nname: mine\ndescription: 我的\n---\n");
        var scan = Scan(Codex()).Accounts.Single();
        scan.MainInstructionPath.Should().Be(temp.Combine(".codex", "AGENTS.md"), "override 只有空白，不算");
        scan.Instructions.Single().Kind.Should().Be("main");
        scan.McpServers.Should().Equal("context7");
        scan.Skills.Should().Be(1);

        temp.WriteFile(@".codex\AGENTS.override.md", "- 覆寫\n");
        Scan(Codex()).Accounts.Single().MainInstructionPath.Should().Be(temp.Combine(".codex", "AGENTS.override.md"));
    }

    [Fact]
    public void Grok_Claude相容沒關時一併讀_claude()
    {
        temp.WriteFile(@".claude\CLAUDE.md", "- 共通\n");
        temp.WriteFile(@".grok\AGENTS.md", "- Grok 自己的\n");
        temp.WriteFile(@".grok\rules\r.md", "- 規則\n");
        var on = Scan(Grok()).Accounts.Single();
        on.Instructions.Select(f => f.Kind).Should().Equal("main", "rule", "compat");

        var iso = temp.CreateDirectory("grok-iso");
        temp.WriteFile(@"grok-iso\config.toml", "[claude_compat]\nimported = true\n");
        Scan(Grok(iso)).Accounts.Single().Instructions.Should().BeEmpty("隔離帳戶關掉了相容，自己也沒有指示");
    }

    [Fact]
    public void Antigravity_讀上一層的GEMINI與config完整擴充並支援精簡模式與外掛覆寫()
    {
        temp.WriteFile(@".gemini\GEMINI.md", "所有回覆都用繁體中文\n");
        var minimal = Scan(Antigravity()).Accounts.Single();
        minimal.Instructions.Single().Path.Should().Be(temp.Combine(".gemini", "GEMINI.md"));
        minimal.Skills.Should().Be(0);

        temp.WriteFile(@".gemini\config\AGENTS.md", "- 遵守架構邊界\n");
        temp.WriteFile(@".gemini\config\rules\always.md", "每次都載入的規則\n");
        temp.WriteFile(@".gemini\config\rules\on-demand.md", "---\ntrigger: model_decision\ndescription: 只在需要時載入\n---\n內容\n");
        temp.WriteFile(@".gemini\config\rules\globbed.md", "---\nglobs:\n  - \"src/**/*.cs\"\n---\nC# 規則\n");
        temp.WriteFile(@".gemini\config\skills\deploy\SKILL.md", "---\nname: deploy\ndescription: 部署腳本\n---\n");
        temp.WriteFile(@".gemini\config\plugins\p-active\plugin.json", """{"name":"ActivePlugin"}""");
        temp.WriteFile(@".gemini\config\plugins\p-active\skills\lint\SKILL.md", "---\nname: lint\ndescription: 檢查\n---\n");
        temp.WriteFile(@".gemini\config\plugins\p-active\mcp_config.json", """{"mcpServers":{"sqlite":{"command":"uvx"}}}""");
        temp.WriteFile(@".gemini\config\plugins\p-active\hooks.json", "{}");
        temp.WriteFile(@".gemini\config\plugins\p-off\plugin.json", """{"name":"OffPlugin"}""");
        temp.WriteFile(@".gemini\config\plugins\p-off\skills\ignored\SKILL.md", "---\nname: ignored\n---\n");
        temp.WriteFile(@".gemini\antigravity-cli\config.json", """{"plugins":{"p-off":{"enabled":false}}}""");
        temp.WriteFile(@".gemini\config\mcp_config.json", """{"mcpServers":{"github":{"command":"npx"}}}""");

        var full = Scan(Antigravity()).Accounts.Single();
        full.Instructions.Select(f => (Path.GetFileName(f.Path), f.Kind)).Should().Equal(
            ("GEMINI.md", "main"), ("AGENTS.md", "main"), ("always.md", "rule"));
        full.ScopedRules.Should().Be(2, "trigger: model_decision 與 globs 都算按需觸發規則");
        full.Skills.Should().Be(2, "deploy 與已啟用外掛的 lint（停用外掛 p-off 不算）");
        full.PluginSkills.Should().Be(1);
        full.Plugins!.Select(p => (p.Id, p.Skills, p.Mcp, p.Hooks)).Should().Equal(("ActivePlugin", 1, true, true));
        full.McpServers.Should().Equal("github", "sqlite（ActivePlugin）");
        full.SettingsError.Should().BeNull();

        var safeHealth = HealthRules.Evaluate(Scan(Antigravity(safeMode: true)).Accounts.Single());
        safeHealth.Items.Single(i => i.Key == "skills").Detail.Should().Contain("--disable-slash-commands");
    }

    [Fact]
    public void 電腦層級的指示檔_共用設定的隔離帳戶不列_Grok指向Claude()
    {
        temp.WriteFile(@".claude\CLAUDE.md", "- 用繁體中文\n- 部署後要 curl 驗證\n");
        temp.WriteFile(@".codex\AGENTS.md", "");
        temp.WriteFile(@".gemini\GEMINI.md", "- 用繁體中文\n");
        var isoCodex = temp.CreateDirectory("codex-iso");
        temp.WriteFile(@"codex-iso\AGENTS.md", "- 自己的\n");
        var result = Scan(
            Claude(),
            Codex(),
            Codex("acc-codex-shared", isDefault: false, dir: isoCodex, shared: temp.Combine(".codex")),
            Codex("acc-codex-own", isDefault: false, dir: temp.CreateDirectory("codex-own")),
            Grok(),
            Antigravity());
        result.Docs.Select(d => (d.Provider, d.Label)).Should().Equal(
            (AgentProvider.Claude, "Claude Code"),
            (AgentProvider.Codex, "Codex"),
            (AgentProvider.Codex, "Codex・Codex 帳戶"),
            (AgentProvider.Antigravity, "Antigravity"),
            (AgentProvider.Grok, "Grok Build"));
        result.Docs[1].Exists.Should().BeTrue();
        result.Docs[1].Text.Should().BeEmpty();
        result.Docs[2].Exists.Should().BeFalse();
        result.Docs[3].MaxBytes.Should().Be(HealthRules.AntigravityFileMaxBytes);
        result.Docs[4].AlsoLoadsKey.Should().Be(result.Docs[0].Key);

        var report = InstructionCompareRules.Compare(result.Docs);
        report.Verdict.Should().Be(InstructionVerdict.NeedsSync);
        report.Docs.Single(d => d.Doc.Provider == AgentProvider.Grok).Missing.Should().BeEmpty("Grok 經相容讀得到 CLAUDE.md");
    }

    [Fact]
    public void 符號連結的實際檔案()
    {
        var target = temp.WriteFile(@"dotfiles\CLAUDE.md", "- 真的內容\n");
        var link = temp.Combine(".claude", "CLAUDE.md");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 沒有建立符號連結的權限（沒開開發人員模式）：略過
            return;
        }
        var scan = Scan(Claude()).Accounts.Single();
        scan.Instructions.Single().LinkTarget.Should().Be(target);
        scan.Instructions.Single().Measure.Bytes.Should().Be(new FileInfo(target).Length, "連結本身的大小是 0，要取實際檔案的");
        Scan(Claude()).Docs.Single().LinkTarget.Should().Be(target);
    }
}
