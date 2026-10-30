using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>Antigravity 工作資料夾專案層級自訂項（.agents/、_agents/、.agent/）與自訂代理掃描測試</summary>
public sealed class AntigravityWorkspaceScannerTests
{
    [Fact]
    public void DiscoverWorkspaceCommands_掃描三種根目錄的Skills與Workflows_排除停用斜線指令並擷取參數提示()
    {
        using var temp = new TempDirectory("agy-ws-cmds");
        var work = temp.CreateDirectory("repo");

        temp.WriteFile(@"repo\.agents\skills\code-review\SKILL.md",
            "---\nname: code-review\ndescription: 執行專案程式碼審查\nargument-hint: <PR編號或路徑>\n---\n內容");
        temp.WriteFile(@"repo\.agents\skills\internal-helper\SKILL.md",
            "---\nname: internal-helper\ndescription: 只給模型背景呼叫\ndisable-slash-command: true\n---\n內容");
        temp.WriteFile(@"repo\_agents\workflows\deploy-staging.md",
            "---\ndescription: 部署至測試環境\nargument_hint: [版本標籤]\n---\n步驟");
        temp.WriteFile(@"repo\.agent\workflows\hidden-wf.md",
            "---\nname: hidden-wf\nuser-invocable: false\n---\n步驟");
        temp.WriteFile(@"repo\.agent\skills\db-migrate\SKILL.md",
            "---\ndescription: 資料庫遷移檢查\n---\n內容");

        var commands = AntigravityWorkspaceScanner.DiscoverWorkspaceCommands(work);
        commands.Select(c => (c.Name, c.Description, c.ArgumentHint)).Should().Equal(
            ("code-review", "執行專案程式碼審查", "<PR編號或路徑>"),
            ("deploy-staging", "部署至測試環境", "[版本標籤]"),
            ("db-migrate", "資料庫遷移檢查", null));
        commands.Select(c => c.Name).Should().NotContain(new[] { "internal-helper", "hidden-wf" });

        // 與全域指令合併：保留內建工作流說明、工作資料夾指令不重複附加
        var merged = AntigravityWorkspaceScanner.MergeCommands(
            AntigravityStreamRules.BuiltinWorkflowCommands,
            commands.Append(new AgentSlashCommand("plan", "工作區同名plan不覆蓋內建說明", null)).ToList());
        merged.Count(c => c.Name == "plan").Should().Be(1);
        merged.Single(c => c.Name == "plan").Description.Should().Be(
            AntigravityStreamRules.BuiltinWorkflowCommands.Single(c => c.Name == "plan").Description);
        merged.Select(c => c.Name).Should().Contain(new[] { "code-review", "deploy-staging", "db-migrate" });
    }

    [Fact]
    public void DiscoverCustomAgents_依工作區_全域設定_已啟用外掛順序掃描並去重()
    {
        using var temp = new TempDirectory("agy-ws-agents");
        var work = temp.CreateDirectory("repo");
        var agyDir = temp.CreateDirectory(@".gemini\antigravity-cli");

        // 工作資料夾優先順序最高，且略過 README.md 與 default
        temp.WriteFile(@"repo\.agents\agents\README.md", "# Agents 說明");
        temp.WriteFile(@"repo\.agents\agents\default.md", "---\ndescription: 不能覆蓋內建 default\n---");
        temp.WriteFile(@"repo\.agents\agents\security-auditor.md",
            "---\nname: security-auditor\nlabel: 資安稽核員\ndescription: 工作區專屬資安審查代理\n---\n系統提示");

        // 全域設定同名時不覆蓋工作區，新名稱則補上
        temp.WriteFile(@".gemini\config\agents\security-auditor.md",
            "---\nname: security-auditor\nlabel: 全域資安\ndescription: 全域版不應覆蓋工作區\n---");
        temp.WriteFile(@".gemini\config\agents\architect.md",
            "---\ntitle: 架構師\ndescription: 跨專案系統架構設計\n---");

        // 外掛：啟用的掃描、停用的略過
        temp.WriteFile(@".gemini\config\plugins\team-pack\plugin.json", """{"name":"team-pack"}""");
        temp.WriteFile(@".gemini\config\plugins\team-pack\agents\qa-tester.md",
            "---\ndescription: 自動化測試專家\n---");
        temp.WriteFile(@".gemini\config\plugins\off-pack\plugin.json", """{"name":"off-pack","disabled":true}""");
        temp.WriteFile(@".gemini\config\plugins\off-pack\agents\ghost-agent.md",
            "---\ndescription: 已停用外掛的代理\n---");

        var scanned = AntigravityWorkspaceScanner.Scan(work, agyDir);
        scanned.Agents.Select(a => (a.Name, a.Label, a.Description)).Should().Equal(
            ("security-auditor", "資安稽核員", "工作區專屬資安審查代理"),
            ("architect", "架構師", "跨專案系統架構設計"),
            ("qa-tester", "qa-tester", "自動化測試專家"));
        scanned.Agents.Select(a => a.Name).Should().NotContain(new[] { "ghost-agent", "README", "default" });

        AntigravityWorkspaceScanner.Same(scanned, AntigravityWorkspaceScanner.Scan(work, agyDir)).Should().BeTrue();
        AntigravityWorkspaceScanner.Same(scanned, AntigravityWorkspaceCustomizations.Empty).Should().BeFalse();

        // 子資料夾往上找 .git 倉庫根目錄、支援 _agent 與 agents/<subdir>/agent.md、並排除 subagent／disabled
        temp.CreateDirectory(@"repo\.git");
        var nestedSubDir = temp.CreateDirectory(@"repo\src\module\sub");
        temp.WriteFile(@"repo\_agent\workflows\root-wf.md", "---\ndescription: 倉庫根目錄工作流\n---\n");
        temp.WriteFile(@"repo\_agent\agents\nested-architect\agent.md", "---\nlabel: 巢狀架構師\ndescription: 子目錄格式代理\n---\n");
        temp.WriteFile(@"repo\.agents\agents\sub-only.md", "---\nmode: subagent\ndescription: 內部子代理不可選\n---\n");
        temp.WriteFile(@"repo\.agents\agents\off-agent.md", "---\ndisabled: true\ndescription: 已停用代理\n---\n");

        var fromSub = AntigravityWorkspaceScanner.Scan(nestedSubDir, agyDir);
        fromSub.Commands.Select(c => c.Name).Should().Contain("root-wf");
        fromSub.Agents.Select(a => a.Name).Should().Contain("nested-architect")
            .And.NotContain(new[] { "sub-only", "off-agent" });
    }
}
