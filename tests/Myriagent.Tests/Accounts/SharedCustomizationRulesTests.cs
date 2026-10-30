using Myriagent.Accounts;
using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Accounts;

/// <summary>隔離帳戶共用系統預設帳戶設定的純規則（SharedCustomizationRules）</summary>
public class SharedCustomizationRulesTests
{
    private static AgentAccount Isolated(AgentProvider provider = AgentProvider.Claude)
        => new() { Id = "a1", Provider = provider, ConfigDirectory = @"C:\data\accounts\a1\claude" };

    [Fact]
    public void IsActive_只有隔離帳戶_支援的CLI_沒關掉_不在精簡模式()
    {
        SharedCustomizationRules.IsActive(Isolated()).Should().BeTrue("預設就共用");
        SharedCustomizationRules.IsActive(Isolated(AgentProvider.Codex)).Should().BeTrue();
        SharedCustomizationRules.IsActive(Isolated(AgentProvider.Grok)).Should().BeTrue("Grok 隔離帳戶與預設 ~/.grok 共用");
        SharedCustomizationRules.IsActive(new AgentAccount { Provider = AgentProvider.Claude }).Should().BeFalse("系統預設帳戶本身就是來源");
        SharedCustomizationRules.IsActive(Isolated(AgentProvider.Antigravity)).Should().BeFalse("Antigravity 沒有隔離帳戶");
        var off = Isolated();
        off.ShareCustomizations = false;
        SharedCustomizationRules.IsActive(off).Should().BeFalse();
        var safe = Isolated();
        safe.SafeMode = true;
        SharedCustomizationRules.IsActive(safe).Should().BeFalse("精簡模式本來就不載入這些");
    }

    [Fact]
    public void 每種CLI都有明確的分類()
    {
        foreach (var provider in AgentProviders.All)
        {
            var supported = AccountRules.SupportsSharedCustomizations(provider);
            SharedCustomizationRules.LinkedDirectories(provider).Should().NotBeNull();
            if (supported)
                SharedCustomizationRules.InstructionFileName(provider).Should().EndWith(".md");
            else
                FluentActions.Invoking(() => SharedCustomizationRules.InstructionFileName(provider)).Should().Throw<ArgumentException>();
        }
        SharedCustomizationRules.InstructionFileName(AgentProvider.Claude).Should().Be("CLAUDE.md");
        SharedCustomizationRules.InstructionFileName(AgentProvider.Codex).Should().Be("AGENTS.md");
        SharedCustomizationRules.InstructionFileName(AgentProvider.Grok).Should().Be("AGENTS.md");
        SharedCustomizationRules.LinkedDirectories(AgentProvider.Claude).Should().Equal("agents", "commands", "output-styles");
        SharedCustomizationRules.LinkedDirectories(AgentProvider.Codex).Should().Equal("prompts");
        SharedCustomizationRules.LinkedDirectories(AgentProvider.Grok).Should().Equal("rules");
    }

    [Theory]
    [InlineData("Claude", "my-skill", true)]
    [InlineData("Claude", "synced", false)]
    [InlineData("Claude", "Synced", false)]
    [InlineData("Codex", "synced", true)]
    [InlineData("Codex", ".system", false)]
    [InlineData("Grok", "my-skill", true)]
    [InlineData("Antigravity", "my-skill", true)]
    [InlineData("Antigravity", "plan", false)]
    [InlineData("Antigravity", "GOAL", false)]
    [InlineData("Claude", ".tmp", false)]
    [InlineData("Claude", " ", false)]
    public void IsSharedSkill_點開頭與Claude的synced與Antigravity內建工作流不共用(string provider, string name, bool expected)
        => SharedCustomizationRules.IsSharedSkill(Enum.Parse<AgentProvider>(provider), name).Should().Be(expected);

    [Fact]
    public void CrossCliPaths與GrokClaudeCompat規則()
    {
        SharedCustomizationRules.CrossCliInstructionPath(AgentProvider.Claude, @"C:\home\.claude").Should().Be(@"C:\home\.claude\CLAUDE.md");
        SharedCustomizationRules.CrossCliInstructionPath(AgentProvider.Codex, @"C:\home\.codex").Should().Be(@"C:\home\.codex\AGENTS.md");
        SharedCustomizationRules.CrossCliInstructionPath(AgentProvider.Grok, @"C:\home\.grok").Should().Be(@"C:\home\.grok\AGENTS.md");
        SharedCustomizationRules.CrossCliInstructionPath(AgentProvider.Antigravity, @"C:\home\.gemini\antigravity-cli").Should().Be(@"C:\home\.gemini\GEMINI.md");

        SharedCustomizationRules.CrossCliSkillsDirectory(AgentProvider.Antigravity, @"C:\home\.gemini\antigravity-cli")
            .Should().Be(@"C:\home\.gemini\config\skills");
        SharedCustomizationRules.CrossCliSkillsDirectory(AgentProvider.Grok, @"C:\home\.grok")
            .Should().Be(@"C:\home\.grok\skills");

        SharedCustomizationRules.IsGrokClaudeCompatEnabled(null).Should().BeTrue();
        SharedCustomizationRules.IsGrokClaudeCompatEnabled("[claude_compat]\nimported = false\n").Should().BeTrue();
        SharedCustomizationRules.IsGrokClaudeCompatEnabled("[claude_compat]\nimported = true\n").Should().BeFalse();
    }

    // ───── 全域指示的雙向同步 ─────

    [Fact]
    public void 第一次共用_帳戶沒有內容就拿來源的_來源沒有就拿帳戶的_兩邊都有就合併()
    {
        SharedCustomizationRules.DecideInstructionSync("共用", null, null).Should().Be(InstructionSyncAction.CopyToAccount);
        SharedCustomizationRules.DecideInstructionSync("共用", "  \n", null).Should().Be(InstructionSyncAction.CopyToAccount, "空白當成沒有");
        SharedCustomizationRules.DecideInstructionSync(null, "帳戶的", null).Should().Be(InstructionSyncAction.CopyToSource);
        SharedCustomizationRules.DecideInstructionSync("", "帳戶的", null).Should().Be(InstructionSyncAction.CopyToSource, "Codex 預設留一個 0 位元組的 AGENTS.md");
        SharedCustomizationRules.DecideInstructionSync("共用", "帳戶的", null).Should().Be(InstructionSyncAction.MergeIntoSource);
        SharedCustomizationRules.DecideInstructionSync("一樣", "一樣", null).Should().Be(InstructionSyncAction.None);
        SharedCustomizationRules.DecideInstructionSync("一樣\r\n", "一樣\n", null).Should().Be(InstructionSyncAction.None, "換行不同不算改過");
        SharedCustomizationRules.DecideInstructionSync(null, null, null).Should().Be(InstructionSyncAction.None);
    }

    [Fact]
    public void 之後_只有一邊改過就以那邊為準_兩邊都改過以來源為準並備份帳戶()
    {
        var last = SharedCustomizationRules.SyncedHash("舊");
        SharedCustomizationRules.DecideInstructionSync("來源改了", "舊", last).Should().Be(InstructionSyncAction.CopyToAccount);
        SharedCustomizationRules.DecideInstructionSync("舊", "帳戶改了", last).Should().Be(InstructionSyncAction.CopyToSource);
        SharedCustomizationRules.DecideInstructionSync("來源改了", "帳戶改了", last).Should().Be(InstructionSyncAction.KeepSourceBackupAccount);
        SharedCustomizationRules.DecideInstructionSync(null, "舊", last).Should().Be(InstructionSyncAction.DeleteAccount, "來源被刪掉、帳戶沒改過");
        SharedCustomizationRules.DecideInstructionSync("舊", null, last).Should().Be(InstructionSyncAction.CopyToAccount, "帳戶的檔被刪掉不會刪來源，改從來源補回");
        SharedCustomizationRules.DecideInstructionSync("來源改了", null, last).Should().Be(InstructionSyncAction.CopyToAccount);
        SharedCustomizationRules.DecideInstructionSync("新的", null, SharedCustomizationRules.BlankHash).Should().Be(InstructionSyncAction.CopyToAccount,
            "上次兩邊都沒有、來源後來寫了");
        SharedCustomizationRules.DecideInstructionSync(null, "新的", SharedCustomizationRules.BlankHash).Should().Be(InstructionSyncAction.CopyToSource);
    }

    [Fact]
    public void MergeInstructions_帳戶的內容附加在來源後面並標明來源帳戶()
    {
        var merged = SharedCustomizationRules.MergeInstructions("# 共用\r\n規則一\r\n\r\n", "\n帳戶規則\n", "工作用");

        merged.Should().Be("# 共用\n規則一\n\n<!-- 以下來自帳戶「工作用」：Clode Myriagent 開始共用設定時合併進來 -->\n帳戶規則\n");
        SharedCustomizationRules.DecideInstructionSync(merged, merged, SharedCustomizationRules.SyncedHash(merged))
            .Should().Be(InstructionSyncAction.None, "合併後兩邊一樣");
    }

    // ───── 記憶的合併 ─────

    [Fact]
    public void PlanMemoryFile_來源沒有就複製_相同略過_索引附加_其他另存()
    {
        SharedCustomizationRules.PlanMemoryFile("notes.md", targetExists: false, sameContent: false).Should().Be(MemoryMergeAction.Copy);
        SharedCustomizationRules.PlanMemoryFile("notes.md", targetExists: true, sameContent: true).Should().Be(MemoryMergeAction.Skip);
        SharedCustomizationRules.PlanMemoryFile("MEMORY.md", targetExists: true, sameContent: false).Should().Be(MemoryMergeAction.AppendIndex);
        SharedCustomizationRules.PlanMemoryFile("memory.md", targetExists: true, sameContent: false).Should().Be(MemoryMergeAction.AppendIndex);
        SharedCustomizationRules.PlanMemoryFile(@"logs\MEMORY.md", targetExists: true, sameContent: false).Should().Be(MemoryMergeAction.CopyRenamed,
            "只有最上層的 MEMORY.md 是索引");
        SharedCustomizationRules.PlanMemoryFile("notes.md", targetExists: true, sameContent: false).Should().Be(MemoryMergeAction.CopyRenamed);
    }

    [Fact]
    public void RenamedForAccount_副檔名前加帳戶id前8碼()
    {
        SharedCustomizationRules.RenamedForAccount("notes.md", "0a1b2c3d4e5f40718293a4b5c6d7e8f9").Should().Be("notes.0a1b2c3d.md");
        SharedCustomizationRules.RenamedForAccount(@"logs\a.md", "x1").Should().Be(@"logs\a.x1.md");
    }

    [Fact]
    public void AppendIndexLines_只加來源沒有的行()
    {
        var source = "- [甲](a.md) — 說明\n- [乙](b.md) — 說明\n";
        var account = "- [乙](b.md) — 說明\r\n\r\n  - [丙](c.md) — 說明  \r\n- [甲](a.md) — 說明\n";

        SharedCustomizationRules.AppendIndexLines(source, account)
            .Should().Be("- [甲](a.md) — 說明\n- [乙](b.md) — 說明\n  - [丙](c.md) — 說明\n");
        SharedCustomizationRules.AppendIndexLines(source, "- [甲](a.md) — 說明").Should().BeSameAs(source, "沒有要加的回原文");
        SharedCustomizationRules.AppendIndexLines("", "- [甲](a.md)").Should().Be("- [甲](a.md)\n", "來源還沒有索引");
    }

    [Fact]
    public void IndexLineForRenamed_照CLI索引的格式()
        => SharedCustomizationRules.IndexLineForRenamed(@"logs\notes.x1.md", @"logs\notes.md", "工作用")
            .Should().Be("- [notes（帳戶「工作用」的版本）](logs/notes.x1.md) — 開始共用設定時兩邊內容不同，另存一份");

    // ───── 自動記憶的專案根目錄（同 CLI 2.1.308 的 defaultKey） ─────

    private sealed class FakeFs
    {
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string Root(string workingDirectory)
            => SharedCustomizationRules.MemoryProjectRoot(workingDirectory, Directories.Contains, p => Files.GetValueOrDefault(p));
    }

    [Fact]
    public void MemoryProjectRoot_git子資料夾換回repo根目錄_不在git就是工作資料夾()
    {
        var fs = new FakeFs();
        fs.Directories.Add(@"C:\repo\app\.git");

        fs.Root(@"C:\repo\app\src\web").Should().Be(@"C:\repo\app");
        fs.Root(@"C:\repo\app\").Should().Be(@"C:\repo\app");
        fs.Root(@"C:\work\notes").Should().Be(@"C:\work\notes");
    }

    [Fact]
    public void MemoryProjectRoot_worktree換回主repo()
    {
        var fs = new FakeFs();
        fs.Directories.Add(@"C:\repo\app\.git");
        fs.Files[@"C:\repo\app-wt\.git"] = "gitdir: C:/repo/app/.git/worktrees/app-wt\n";
        fs.Files[@"C:\repo\app\.git\worktrees\app-wt\commondir"] = "../..\n";
        fs.Files[@"C:\repo\app\.git\worktrees\app-wt\gitdir"] = "C:/repo/app-wt/.git\n";

        fs.Root(@"C:\repo\app-wt\src").Should().Be(@"C:\repo\app", "實測 CLI 的記憶資料夾：worktree 和主 repo 共用 C--repo-app");
    }

    [Fact]
    public void MemoryProjectRoot_worktree的任何一項對不上就照一般repo()
    {
        var fs = new FakeFs();
        fs.Files[@"C:\repo\sub\.git"] = "gitdir: ../app/.git/modules/sub";
        fs.Root(@"C:\repo\sub").Should().Be(@"C:\repo\sub", "子模組沒有 commondir");

        fs.Files[@"C:\repo\wt\.git"] = "gitdir: C:/repo/app/.git/worktrees/wt";
        fs.Files[@"C:\repo\app\.git\worktrees\wt\commondir"] = "../..";
        fs.Files[@"C:\repo\app\.git\worktrees\wt\gitdir"] = "C:/repo/elsewhere/.git";
        fs.Root(@"C:\repo\wt").Should().Be(@"C:\repo\wt", "gitdir 沒有反指回這個 worktree（同 CLI 的檢查）");

        fs.Files[@"C:\repo\bad\.git"] = "not a git file";
        fs.Root(@"C:\repo\bad").Should().Be(@"C:\repo\bad");

        fs.Files[@"C:\repo\bare-wt\.git"] = "gitdir: C:/repo/bare.git/worktrees/bare-wt";
        fs.Files[@"C:\repo\bare.git\worktrees\bare-wt\commondir"] = "../..";
        fs.Files[@"C:\repo\bare.git\worktrees\bare-wt\gitdir"] = "C:/repo/bare-wt/.git";
        fs.Root(@"C:\repo\bare-wt").Should().Be(@"C:\repo\bare-wt", "bare repo 沒有主要的工作資料夾");
    }

    [Fact]
    public void SharedMemoryDirectory_來源設定資料夾的同一個專案資料夾()
        => SharedCustomizationRules.SharedMemoryDirectory(@"C:\Users\me\.claude", @"C:\repo\myriagent")
            .Should().Be(@"C:\Users\me\.claude\projects\C--repo-myriagent\memory");

    // ───── Claude 的 MCP ─────

    [Fact]
    public void ClaudeProjectKeys_正斜線_根目錄與工作資料夾各一()
    {
        SharedCustomizationRules.ClaudeProjectKeys(@"C:\repo\app", @"C:\repo\app\src").Should().Equal("C:/repo/app", "C:/repo/app/src");
        SharedCustomizationRules.ClaudeProjectKeys(@"C:\repo\app", @"C:\repo\app\").Should().Equal("C:/repo/app");
    }

    [Fact]
    public void ReadClaudeMcpServers_讀user層與這個專案的local層_略過其他欄位()
    {
        var json = """
            {
              "numStartups": 5,
              "oauthAccount": { "emailAddress": "me@example.com" },
              "projects": {
                "C:/repo/other": { "mcpServers": { "other": { "command": "x" } } },
                "c:\\repo\\app": { "allowedTools": [], "mcpServers": { "db": { "command": "root" }, "pw": { "command": "pw" } } },
                "C:/repo/app/src": { "mcpServers": { "db": { "command": "cwd" } } }
              },
              "mcpServers": { "higgsfield": { "type": "http", "url": "https://example.com/mcp" } }
            }
            """;

        var (user, project) = SharedCustomizationRules.ReadClaudeMcpServers(new StringReader(json), new[] { "C:/repo/app", "C:/repo/app/src" });

        user!.Properties().Select(p => p.Name).Should().Equal("higgsfield");
        project!["db"]!["command"]!.ToString().Should().Be("cwd", "工作資料夾的鍵排在後面、蓋過根目錄的同名");
        project["pw"]!["command"]!.ToString().Should().Be("pw");
        project["other"].Should().BeNull();
    }

    [Fact]
    public void ReadClaudeMcpServers_讀不懂回null()
    {
        SharedCustomizationRules.ReadClaudeMcpServers(new StringReader("{\"mcpServers\":"), Array.Empty<string>()).Should().Be(((JObject?)null, (JObject?)null));
        SharedCustomizationRules.ReadClaudeMcpServers(new StringReader("[]"), Array.Empty<string>()).Should().Be(((JObject?)null, (JObject?)null));
    }

    [Fact]
    public void BuildClaudeMcpConfig_帳戶自己有的名稱不給_local蓋過user_沒有可給回null()
    {
        var user = JObject.Parse("""{ "higgsfield": { "type": "http", "url": "u" }, "meta": { "type": "http", "url": "m" }, "bad": 5 }""");
        var project = JObject.Parse("""{ "meta": { "command": "local" }, "db": { "command": "db" } }""");

        var config = SharedCustomizationRules.BuildClaudeMcpConfig(user, project, new[] { "higgsfield" })!;

        var servers = (JObject)config["mcpServers"]!;
        servers.Properties().Select(p => p.Name).Should().Equal("meta", "db");
        servers["meta"]!["command"]!.ToString().Should().Be("local");
        SharedCustomizationRules.BuildClaudeMcpConfig(user, null, new[] { "higgsfield", "meta" }).Should().BeNull("剩下的不是物件");
        SharedCustomizationRules.BuildClaudeMcpConfig(null, null, Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void ServerNames_user層與local層的名稱()
        => SharedCustomizationRules.ServerNames(JObject.Parse("""{"a":{},"b":{}}"""), JObject.Parse("""{"b":{},"c":{}}"""))
            .Should().Equal("a", "b", "c");
}
