using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Accounts;

/// <summary>
/// 隔離帳戶共用系統預設帳戶的設定（SharedCustomizations）：來源與帳戶都在暫存資料夾，絕不碰真的 ~/.claude、~/.codex。
/// junction 的目標都在同一個暫存資料夾裡，收尾時一起刪
/// </summary>
public sealed class SharedCustomizationsTests : IDisposable
{
    private readonly TempDirectory temp = new("shared-customizations");
    private readonly string claudeSource;
    private readonly string codexSource;
    private readonly string agySource;
    private readonly string grokSource;
    private readonly string claudeJson;
    private readonly string crossPool;
    private readonly SharedCustomizations shared;

    public SharedCustomizationsTests()
    {
        claudeSource = temp.CreateDirectory(@"home\.claude");
        codexSource = temp.CreateDirectory(@"home\.codex");
        agySource = temp.CreateDirectory(@"home\.gemini\antigravity-cli");
        grokSource = temp.CreateDirectory(@"home\.grok");
        claudeJson = temp.Combine("home", ".claude.json");
        crossPool = temp.Combine("cross-pool");
        shared = new SharedCustomizations(
            provider => provider switch
            {
                AgentProvider.Claude => claudeSource,
                AgentProvider.Codex => codexSource,
                AgentProvider.Antigravity => agySource,
                AgentProvider.Grok => grokSource,
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            },
            () => claudeJson,
            () => crossPool);
    }

    public void Dispose() => temp.Dispose();

    private SharedAccount Claude(string id = "a1b2c3d4e5f6") => new(id, "工作用", AgentProvider.Claude, temp.CreateDirectory($@"accounts\{id}\claude"));

    private SharedAccount Codex(string id = "c0dexc0dex99") => new(id, "Codex 團隊", AgentProvider.Codex, temp.CreateDirectory($@"accounts\{id}\codex"));

    private SharedAccount Grok(string id = "680680680abc") => new(id, "Grok 團隊", AgentProvider.Grok, temp.CreateDirectory($@"accounts\{id}\grok"));

    private string Repo(string name = "app")
    {
        temp.CreateDirectory($@"repo\{name}\.git");
        return temp.CreateDirectory($@"repo\{name}\src");
    }

    private static SharedCustomizationState? State(SharedAccount account)
        => JsonFile.Read<SharedCustomizationState>(Path.Combine(account.ConfigDirectory, SharedCustomizationRules.StateFolderName, "state.json"));

    private static bool IsLinkTo(string link, string target)
        => Junction.GetTarget(link) is { } actual
           && string.Equals(Path.GetFullPath(actual).TrimEnd('\\'), Path.GetFullPath(target).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Prepare_連結skills與資料夾_同步CLAUDE_md_回傳共用的記憶資料夾與MCP()
    {
        temp.WriteFile(@"home\.claude\skills\alpha\SKILL.md", "alpha");
        temp.CreateDirectory(@"home\.claude\skills\.hidden");
        temp.WriteFile(@"home\.claude\skills\synced\x\SKILL.md", "來源的 synced 也不連");
        temp.WriteFile(@"home\.claude\agents\reviewer.md", "reviewer");
        temp.WriteFile(@"home\.claude\CLAUDE.md", "# 共用規則");
        var work = Repo();
        File.WriteAllText(claudeJson, new JObject
        {
            ["oauthAccount"] = new JObject { ["emailAddress"] = "me@example.com" },
            ["mcpServers"] = JObject.Parse("""{ "higgsfield": { "type": "http", "url": "h" }, "meta": { "type": "http", "url": "m" } }"""),
            ["projects"] = new JObject { [temp.Combine("repo", "app").Replace('\\', '/')] = JObject.Parse("""{ "mcpServers": { "db": { "command": "sql" } } }""") },
        }.ToString());
        var account = Claude();
        temp.WriteFile($@"accounts\{account.Id}\claude\skills\synced\org\SKILL.md", "claude.ai 同步的");
        File.WriteAllText(Path.Combine(account.ConfigDirectory, ".claude.json"), """{ "mcpServers": { "higgsfield": { "type": "http", "url": "mine" } } }""");

        var launch = shared.Prepare(account, work);

        IsLinkTo(Path.Combine(account.ConfigDirectory, "skills", "alpha"), Path.Combine(claudeSource, "skills", "alpha")).Should().BeTrue();
        File.ReadAllText(Path.Combine(account.ConfigDirectory, "skills", "alpha", "SKILL.md")).Should().Be("alpha");
        Junction.IsJunction(Path.Combine(account.ConfigDirectory, "skills", "synced")).Should().BeFalse("帳戶自己從 claude.ai 同步的留著");
        Directory.Exists(Path.Combine(account.ConfigDirectory, "skills", ".hidden")).Should().BeFalse();
        IsLinkTo(Path.Combine(account.ConfigDirectory, "agents"), Path.Combine(claudeSource, "agents")).Should().BeTrue();
        Directory.Exists(Path.Combine(account.ConfigDirectory, "commands")).Should().BeFalse("來源沒有就不建");
        File.ReadAllText(Path.Combine(account.ConfigDirectory, "CLAUDE.md")).Should().Be("# 共用規則");

        launch.ClaudeMemoryDirectory.Should().Be(Path.Combine(claudeSource, "projects", ClaudeStreamRules.ProjectFolderName(temp.Combine("repo", "app")), "memory"),
            "工作資料夾在 repo 的子資料夾，記憶照 CLI 算在 repo 根目錄");
        launch.CodexConfigOverrides.Should().BeEmpty();
        var mcp = JObject.Parse(File.ReadAllText(launch.ClaudeMcpConfigFile!));
        ((JObject)mcp["mcpServers"]!).Properties().Select(p => p.Name).Should().Equal(new[] { "meta", "db" }, "帳戶自己有 higgsfield");
        Path.GetDirectoryName(launch.ClaudeMcpConfigFile).Should().Be(Path.Combine(account.ConfigDirectory, SharedCustomizationRules.StateFolderName),
            "裡面可能有 MCP 的金鑰，放在帳戶的設定資料夾");
        State(account)!.Active.Should().BeTrue();

        var again = shared.Prepare(account, work);
        again.ClaudeMcpConfigFile.Should().Be(launch.ClaudeMcpConfigFile, "內容一樣用同一個檔");
    }

    [Fact]
    public void Prepare_帳戶原有的skill與agents合併進來源並備份_關掉共用時移回()
    {
        var account = Claude();
        temp.WriteFile($@"accounts\{account.Id}\claude\skills\own\SKILL.md", "帳戶自己的 skill");
        temp.WriteFile($@"accounts\{account.Id}\claude\skills\both\SKILL.md", "帳戶的版本");
        temp.WriteFile(@"home\.claude\skills\both\SKILL.md", "來源的版本");
        temp.WriteFile($@"accounts\{account.Id}\claude\agents\mine.md", "我的子代理");
        temp.WriteFile($@"accounts\{account.Id}\claude\agents\same.md", "帳戶的 same");
        temp.WriteFile(@"home\.claude\agents\same.md", "來源的 same");
        temp.CreateDirectory($@"accounts\{account.Id}\claude\commands");
        temp.WriteFile(@"home\.claude\commands\deploy.md", "deploy");
        File.WriteAllText(claudeJson, """{ "mcpServers": { "meta": { "type": "http", "url": "m" } } }""");

        var launch = shared.Prepare(account, Repo());

        File.ReadAllText(Path.Combine(claudeSource, "skills", "own", "SKILL.md")).Should().Be("帳戶自己的 skill");
        IsLinkTo(Path.Combine(account.ConfigDirectory, "skills", "own"), Path.Combine(claudeSource, "skills", "own")).Should().BeTrue();
        Junction.IsJunction(Path.Combine(account.ConfigDirectory, "skills", "both")).Should().BeFalse("兩邊都有同名的，帳戶自己的優先");
        File.ReadAllText(Path.Combine(claudeSource, "skills", "both", "SKILL.md")).Should().Be("來源的版本");
        File.ReadAllText(Path.Combine(claudeSource, "agents", "mine.md")).Should().Be("我的子代理");
        File.ReadAllText(Path.Combine(claudeSource, "agents", "same.md")).Should().Be("來源的 same", "來源的不覆蓋");
        File.ReadAllText(Path.Combine(claudeSource, "agents", SharedCustomizationRules.RenamedForAccount("same.md", account.Id))).Should().Be("帳戶的 same");
        IsLinkTo(Path.Combine(account.ConfigDirectory, "agents"), Path.Combine(claudeSource, "agents")).Should().BeTrue();
        IsLinkTo(Path.Combine(account.ConfigDirectory, "commands"), Path.Combine(claudeSource, "commands")).Should().BeTrue("空的資料夾直接換成連結");
        var backup = Path.Combine(account.ConfigDirectory, SharedCustomizationRules.StateFolderName, "backup");
        File.Exists(Path.Combine(backup, "skills", "own", "SKILL.md")).Should().BeTrue();
        File.Exists(Path.Combine(backup, "agents", "mine.md")).Should().BeTrue();

        shared.Unshare(AgentProvider.Claude, account.ConfigDirectory);

        foreach (var name in new[] { @"skills\own", "agents", "commands" })
            Junction.IsJunction(Path.Combine(account.ConfigDirectory, name)).Should().BeFalse(name);
        File.ReadAllText(Path.Combine(account.ConfigDirectory, "skills", "own", "SKILL.md")).Should().Be("帳戶自己的 skill", "原件移回來");
        File.ReadAllText(Path.Combine(account.ConfigDirectory, "agents", "same.md")).Should().Be("帳戶的 same");
        Directory.Exists(Path.Combine(account.ConfigDirectory, "commands")).Should().BeFalse("原本是空的，沒有備份");
        File.ReadAllText(Path.Combine(account.ConfigDirectory, "skills", "both", "SKILL.md")).Should().Be("帳戶的版本");
        File.Exists(launch.ClaudeMcpConfigFile!).Should().BeFalse("關掉共用就刪掉 MCP 設定檔");
        File.Exists(Path.Combine(claudeSource, "agents", "mine.md")).Should().BeTrue("合併進來源的留著（其他帳戶在用）");
        State(account)!.Active.Should().BeFalse();
    }

    [Fact]
    public void Prepare_全域指示雙向同步_兩邊都改過以來源為準並備份帳戶()
    {
        var account = Claude();
        var work = Repo();
        var sourceFile = Path.Combine(claudeSource, "CLAUDE.md");
        var accountFile = Path.Combine(account.ConfigDirectory, "CLAUDE.md");
        File.WriteAllText(sourceFile, "A");

        shared.Prepare(account, work);
        File.ReadAllText(accountFile).Should().Be("A");

        File.WriteAllText(accountFile, "B（在帳戶的對話裡改的）");
        shared.Prepare(account, work);
        File.ReadAllText(sourceFile).Should().Be("B（在帳戶的對話裡改的）", "只有帳戶改過：寫回來源");

        File.WriteAllText(sourceFile, "C");
        File.WriteAllText(accountFile, "D");
        shared.Prepare(account, work);
        File.ReadAllText(sourceFile).Should().Be("C");
        File.ReadAllText(accountFile).Should().Be("C");
        Directory.GetFiles(Path.Combine(account.ConfigDirectory, SharedCustomizationRules.StateFolderName, "backup"), "CLAUDE.md.*")
            .Select(File.ReadAllText).Should().Equal("D");

        File.Delete(sourceFile);
        shared.Prepare(account, work);
        File.Exists(accountFile).Should().BeFalse("來源刪掉、帳戶沒改過：跟著刪");
    }

    [Fact]
    public void Prepare_第一次共用_帳戶原有的全域指示合併進來源()
    {
        var account = Claude();
        File.WriteAllText(Path.Combine(claudeSource, "CLAUDE.md"), "# 共用");
        File.WriteAllText(Path.Combine(account.ConfigDirectory, "CLAUDE.md"), "帳戶自己的規則");

        shared.Prepare(account, Repo());

        var merged = File.ReadAllText(Path.Combine(claudeSource, "CLAUDE.md"));
        merged.Should().StartWith("# 共用\n").And.Contain("帳戶「工作用」").And.EndWith("帳戶自己的規則\n");
        File.ReadAllText(Path.Combine(account.ConfigDirectory, "CLAUDE.md")).Should().Be(merged);
    }

    [Fact]
    public void Prepare_第一次共用把帳戶的記憶合併進來源_之後不再合併_重新打開只合併之後改過的()
    {
        var account = Claude();
        var folder = "C--repo-app";
        var memory = temp.CreateDirectory($@"accounts\{account.Id}\claude\projects\{folder}\memory");
        temp.WriteFile($@"accounts\{account.Id}\claude\projects\{folder}\a.jsonl", "逐字稿不搬");
        File.WriteAllText(Path.Combine(memory, "MEMORY.md"), "- [甲](a.md) — 帳戶的\n- [共同](c.md) — 兩邊都有\n");
        File.WriteAllText(Path.Combine(memory, "a.md"), "帳戶的甲");
        File.WriteAllText(Path.Combine(memory, "c.md"), "帳戶的共同");
        var target = temp.CreateDirectory($@"home\.claude\projects\{folder}\memory");
        File.WriteAllText(Path.Combine(target, "MEMORY.md"), "- [共同](c.md) — 兩邊都有\n");
        File.WriteAllText(Path.Combine(target, "c.md"), "來源的共同");

        shared.Prepare(account, Repo());

        File.ReadAllText(Path.Combine(target, "a.md")).Should().Be("帳戶的甲");
        File.ReadAllText(Path.Combine(target, "c.md")).Should().Be("來源的共同", "來源的不覆蓋");
        var renamed = SharedCustomizationRules.RenamedForAccount("c.md", account.Id);
        File.ReadAllText(Path.Combine(target, renamed)).Should().Be("帳戶的共同");
        File.ReadAllText(Path.Combine(target, "MEMORY.md")).Replace("\r\n", "\n").Should().Be(
            "- [共同](c.md) — 兩邊都有\n"
            + "- [甲](a.md) — 帳戶的\n"
            + SharedCustomizationRules.IndexLineForRenamed(renamed, "c.md", "工作用") + "\n");
        File.Exists(Path.Combine(claudeSource, "projects", folder, "a.jsonl")).Should().BeFalse("只合併 memory\\");
        File.Exists(Path.Combine(memory, "a.md")).Should().BeTrue("帳戶的原件不動");

        File.WriteAllText(Path.Combine(memory, "b.md"), "共用期間不會寫到這裡，就算有也不再合併");
        shared.Prepare(account, Repo());
        File.Exists(Path.Combine(target, "b.md")).Should().BeFalse();

        shared.Unshare(AgentProvider.Claude, account.ConfigDirectory);
        File.WriteAllText(Path.Combine(memory, "d.md"), "關掉共用期間寫的");
        File.SetLastWriteTimeUtc(Path.Combine(memory, "d.md"), DateTime.UtcNow.AddMinutes(1));
        File.WriteAllText(Path.Combine(target, "a.md"), "來源後來改了");
        shared.Prepare(account, Repo());
        File.ReadAllText(Path.Combine(target, "d.md")).Should().Be("關掉共用期間寫的");
        File.ReadAllText(Path.Combine(target, "a.md")).Should().Be("來源後來改了", "上次合併過、之後沒改的不再合併");
        File.Exists(Path.Combine(target, SharedCustomizationRules.RenamedForAccount("a.md", account.Id))).Should().BeFalse();
    }

    [Fact]
    public void Prepare_來源刪掉的skill_帳戶的連結跟著拿掉()
    {
        temp.WriteFile(@"home\.claude\skills\gone\SKILL.md", "x");
        var account = Claude();
        shared.Prepare(account, Repo());
        Junction.IsJunction(Path.Combine(account.ConfigDirectory, "skills", "gone")).Should().BeTrue();

        Directory.Delete(Path.Combine(claudeSource, "skills", "gone"), recursive: true);
        shared.Prepare(account, Repo());

        Directory.Exists(Path.Combine(account.ConfigDirectory, "skills", "gone")).Should().BeFalse();
    }

    [Fact]
    public void 刪除帳戶資料夾不會走進連結刪到來源()
    {
        temp.WriteFile(@"home\.claude\skills\alpha\SKILL.md", "alpha");
        temp.WriteFile(@"home\.claude\agents\reviewer.md", "reviewer");
        var account = Claude();
        shared.Prepare(account, Repo());

        DirectoryRemover.TryDelete(Path.GetDirectoryName(account.ConfigDirectory)!).Should().BeTrue();

        File.ReadAllText(Path.Combine(claudeSource, "skills", "alpha", "SKILL.md")).Should().Be("alpha");
        File.ReadAllText(Path.Combine(claudeSource, "agents", "reviewer.md")).Should().Be("reviewer");
    }

    [Fact]
    public void Prepare_Codex_同步AGENTS_md_連結prompts與skills_MCP轉成設定覆寫()
    {
        temp.WriteFile(@"home\.codex\AGENTS.md", string.Empty);
        temp.WriteFile(@"home\.codex\prompts\review.md", "review");
        temp.WriteFile(@"home\.codex\skills\video\SKILL.md", "video");
        temp.WriteFile(@"home\.codex\skills\.system\x\SKILL.md", "內建的");
        temp.WriteFile(@"home\.codex\config.toml", """
            model = "gpt-6"
            [mcp_servers.node_repl]
            command = "node"
            [mcp_servers.mine]
            command = "source-version"
            """);
        var account = Codex();
        temp.WriteFile($@"accounts\{account.Id}\codex\AGENTS.md", "帳戶的 Codex 規則");
        temp.WriteFile($@"accounts\{account.Id}\codex\config.toml", """
            [mcp_servers.mine]
            command = "account-version"
            """);

        var launch = shared.Prepare(account, Repo());

        File.ReadAllText(Path.Combine(codexSource, "AGENTS.md")).Should().Be("帳戶的 Codex 規則", "來源是 0 位元組：當成沒有內容，拿帳戶的");
        IsLinkTo(Path.Combine(account.ConfigDirectory, "prompts"), Path.Combine(codexSource, "prompts")).Should().BeTrue();
        IsLinkTo(Path.Combine(account.ConfigDirectory, "skills", "video"), Path.Combine(codexSource, "skills", "video")).Should().BeTrue();
        Directory.Exists(Path.Combine(account.ConfigDirectory, "skills", ".system")).Should().BeFalse("Codex 內建的 skill 各自安裝");
        launch.CodexConfigOverrides.Should().Equal("mcp_servers.node_repl.command=\"node\"");
        launch.ClaudeMemoryDirectory.Should().BeNull();
        launch.ClaudeMcpConfigFile.Should().BeNull();
    }

    [Fact]
    public void Prepare_來源就是帳戶自己的資料夾時什麼都不做()
    {
        var self = new SharedAccount("s", "自己", AgentProvider.Claude, claudeSource);
        temp.WriteFile(@"home\.claude\skills\alpha\SKILL.md", "alpha");

        shared.Prepare(self, Repo()).Should().BeSameAs(AgentSharedLaunch.None);
        Directory.Exists(Path.Combine(claudeSource, SharedCustomizationRules.StateFolderName)).Should().BeFalse();
    }

    [Fact]
    public void Snapshot_只給要共用的帳戶()
    {
        var account = new AgentAccount { Id = "a1", Label = "工作用", Provider = AgentProvider.Claude, ConfigDirectory = temp.Root };
        SharedCustomizations.Snapshot(account).Should().Be(new SharedAccount("a1", "工作用", AgentProvider.Claude, temp.Root));
        account.ShareCustomizations = false;
        SharedCustomizations.Snapshot(account).Should().BeNull();
        SharedCustomizations.Snapshot(new AgentAccount { Provider = AgentProvider.Claude }).Should().BeNull();
    }

    [Fact]
    public void Prepare_Grok隔離帳戶_同步AGENTS_md並連結rules與skills()
    {
        temp.WriteFile(@"home\.grok\AGENTS.md", "# Grok 共用規則");
        temp.WriteFile(@"home\.grok\rules\style.md", "繁體中文");
        temp.WriteFile(@"home\.grok\skills\search\SKILL.md", "search-skill");
        var account = Grok();

        var launch = shared.Prepare(account, Repo());

        launch.Should().BeSameAs(AgentSharedLaunch.None);
        File.ReadAllText(Path.Combine(account.ConfigDirectory, "AGENTS.md")).Should().Be("# Grok 共用規則");
        IsLinkTo(Path.Combine(account.ConfigDirectory, "rules"), Path.Combine(grokSource, "rules")).Should().BeTrue();
        IsLinkTo(Path.Combine(account.ConfigDirectory, "skills", "search"), Path.Combine(grokSource, "skills", "search")).Should().BeTrue();

        shared.Unshare(AgentProvider.Grok, account.ConfigDirectory);
        Junction.IsJunction(Path.Combine(account.ConfigDirectory, "rules")).Should().BeFalse();
        Junction.IsJunction(Path.Combine(account.ConfigDirectory, "skills", "search")).Should().BeFalse();
    }

    [Fact]
    public void SyncCrossCli_跨四家CLI雙向同步全域指示與Skills_Grok開Claude相容時不重複寫入並可完整還原()
    {
        temp.WriteFile(@"home\.claude\CLAUDE.md", "# Claude 規則");
        temp.WriteFile(@"home\.claude\skills\claude-skill\SKILL.md", "from-claude");
        temp.WriteFile(@"home\.gemini\GEMINI.md", "# Gemini 規則");
        temp.WriteFile(@"home\.gemini\config\skills\agy-skill\SKILL.md", "from-agy");
        temp.WriteFile(@"home\.gemini\config\skills\plan\SKILL.md", "內建保留詞不共用");
        temp.WriteFile(@"home\.grok\AGENTS.md", "# Grok 原本規則");
        temp.WriteFile(@"home\.grok\skills\grok-skill\SKILL.md", "from-grok");

        shared.SyncCrossCli();

        var claudeInst = File.ReadAllText(Path.Combine(claudeSource, "CLAUDE.md"));
        var codexInst = File.ReadAllText(Path.Combine(codexSource, "AGENTS.md"));
        var geminiInst = File.ReadAllText(temp.Combine("home", ".gemini", "GEMINI.md"));
        claudeInst.Should().Contain("# Claude 規則").And.Contain("# Gemini 規則").And.Contain("# Grok 原本規則");
        codexInst.Should().Be(claudeInst);
        geminiInst.Should().Be(claudeInst);

        // Grok 預設開著 Claude 相容（自動讀 ~/.claude），原檔已吸收進共用池並備份移開，不重複寫回 ~/.grok
        File.Exists(Path.Combine(grokSource, "AGENTS.md")).Should().BeFalse();
        Directory.Exists(Path.Combine(grokSource, "skills", "claude-skill")).Should().BeFalse();

        // Claude、Codex、Antigravity 都擁有三家合併後的 skills（但 Antigravity 內建保留詞 plan 不散播）
        foreach (var skillName in new[] { "claude-skill", "agy-skill", "grok-skill" })
        {
            File.Exists(Path.Combine(claudeSource, "skills", skillName, "SKILL.md")).Should().BeTrue(skillName);
            File.Exists(Path.Combine(codexSource, "skills", skillName, "SKILL.md")).Should().BeTrue(skillName);
            File.Exists(temp.Combine("home", ".gemini", "config", "skills", skillName, "SKILL.md")).Should().BeTrue(skillName);
        }
        Directory.Exists(Path.Combine(claudeSource, "skills", "plan")).Should().BeFalse("Antigravity 保留詞不共用");

        // 在 Antigravity 修改 GEMINI.md，下次同步自動推回 Claude 與 Codex
        File.WriteAllText(temp.Combine("home", ".gemini", "GEMINI.md"), "# 從 Antigravity 更新的全域指示\n");
        shared.SyncCrossCli();
        File.ReadAllText(Path.Combine(claudeSource, "CLAUDE.md")).Should().Be("# 從 Antigravity 更新的全域指示\n");
        File.ReadAllText(Path.Combine(codexSource, "AGENTS.md")).Should().Be("# 從 Antigravity 更新的全域指示\n");

        // 單邊誤刪一家指示檔（其餘兩家仍在）：應從共用池補回，絕不能誤刪其餘三家的指示檔
        File.Delete(temp.Combine("home", ".gemini", "GEMINI.md"));
        shared.SyncCrossCli();
        File.ReadAllText(temp.Combine("home", ".gemini", "GEMINI.md")).Should().Be("# 從 Antigravity 更新的全域指示\n");
        File.ReadAllText(Path.Combine(claudeSource, "CLAUDE.md")).Should().Be("# 從 Antigravity 更新的全域指示\n");

        // 關閉跨 CLI 共用：移除跨 CLI junction，還原四家各自原本的全域指示與 skills，並刪掉同步期間為 Codex 新建的 AGENTS.md
        shared.UnshareCrossCli();
        File.ReadAllText(Path.Combine(claudeSource, "CLAUDE.md")).Should().Be("# Claude 規則");
        File.ReadAllText(temp.Combine("home", ".gemini", "GEMINI.md")).Should().Be("# Gemini 規則");
        File.Exists(Path.Combine(codexSource, "AGENTS.md")).Should().BeFalse("Codex 原本沒有 AGENTS.md，關閉時應清除");
        File.ReadAllText(Path.Combine(claudeSource, "skills", "claude-skill", "SKILL.md")).Should().Be("from-claude");
        Junction.IsJunction(Path.Combine(claudeSource, "skills", "claude-skill")).Should().BeFalse();
        Directory.Exists(Path.Combine(claudeSource, "skills", "agy-skill")).Should().BeFalse();
        File.ReadAllText(Path.Combine(grokSource, "AGENTS.md")).Should().Be("# Grok 原本規則");
        File.ReadAllText(Path.Combine(grokSource, "skills", "grok-skill", "SKILL.md")).Should().Be("from-grok");

        // 關閉期間重新編輯後再次開啟跨 CLI 共用：不應殘留上次已清掉的舊池文字，且同名實體 skill 在多家都會轉成 junction
        File.WriteAllText(Path.Combine(claudeSource, "CLAUDE.md"), "# 新一輪 Claude 規則");
        temp.WriteFile(@"home\.claude\skills\same-skill\SKILL.md", "claude-v1");
        temp.WriteFile(@"home\.gemini\config\skills\same-skill\SKILL.md", "agy-v1");
        shared.SyncCrossCli();
        File.ReadAllText(Path.Combine(claudeSource, "CLAUDE.md")).Should().NotContain("從 Antigravity 更新的全域指示");
        Junction.IsJunction(Path.Combine(claudeSource, "skills", "same-skill")).Should().BeTrue();
        Junction.IsJunction(temp.Combine("home", ".gemini", "config", "skills", "same-skill")).Should().BeTrue();
    }
}
