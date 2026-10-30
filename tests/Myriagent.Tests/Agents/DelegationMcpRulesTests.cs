using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

public sealed class DelegationMcpRulesTests
{
    private const string Pipe = "myriagent-delegation-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void 帳戶提及以穩定身分去重且不誤認檔案或信箱()
    {
        DelegationMcpRules.MentionedAccounts("@src/main.cs @\"目錄 檔案\" a@b.com @[Codex](account:acc-codex) @[改名](account:acc-codex) @[Claude](account:other)")
            .Should().Equal("acc-codex", "other");
        DelegationMcpRules.MentionedAccounts("@[檔案](file:a) @[壞的](account:../outside) @Codex").Should().BeEmpty();
    }

    [Fact]
    public void 兩個CLI都只取得目前對話的MCP且不修改帳戶設定()
    {
        var claude = DelegationMcpRules.ClaudeConfig(Pipe);
        claude["mcpServers"]![DelegationMcpRules.ServerName]!["args"]!.Values<string>().Should().Equal("--delegation-mcp", Pipe);
        claude["mcpServers"]![DelegationMcpRules.ServerName]!.Value<string>("command").Should().EndWith(".exe");
        var codex = CodexAppServerRules.BuildServerArguments(true, []);
        DelegationMcpRules.AddCodexArguments(codex, Pipe);
        codex.Should().Contain($"mcp_servers.{DelegationMcpRules.ServerName}.enabled=true");
        codex.Should().Contain($"mcp_servers.{DelegationMcpRules.ServerName}.tool_timeout_sec=14400", "Codex 的 MCP 工具預設 60 秒逾時，委派要等子對話做完");
        codex.Should().Contain(s => s.Contains(Pipe));
        DelegationMcpRules.IsPipeName(Pipe).Should().BeTrue();
        DelegationMcpRules.IsPipeName("../other").Should().BeFalse();
    }

    [Fact]
    public void Claude帶MCP設定時放行兩個委派工具_沒有委派時不帶()
    {
        var plain = new ClaudeLaunchOptions("s-1", false, AgentPermissionMode.Ask, null, null, "說明", [], true);
        ClaudeStreamRules.BuildArguments(plain).Should().NotContain("--mcp-config").And.NotContain(DelegationMcpRules.ClaudeAllowedTools);

        var args = ClaudeStreamRules.BuildArguments(plain with { AllowedTools = ["Bash(npm test:*)"], DelegationMcpPipeName = Pipe });
        args.Should().ContainInOrder("--allowedTools", "Bash(npm test:*)", DelegationMcpRules.ClaudeAllowedTools[0], DelegationMcpRules.ClaudeAllowedTools[1], "--disallowedTools");
        var config = JObject.Parse(args[args.IndexOf("--mcp-config") + 1]);
        config["mcpServers"]![DelegationMcpRules.ServerName]!["args"]!.Values<string>().Should().Equal("--delegation-mcp", Pipe);

        // 共用設定的 MCP 檔也要帶時：同一個 --mcp-config 之後兩個值，下一個參數是旗標（它會吃掉後面所有不是旗標的參數）
        var both = ClaudeStreamRules.BuildArguments(plain with { McpConfigFile = @"C:\cfg\.myriagent-shared\mcp-1.json", DelegationMcpPipeName = Pipe });
        both.Count(a => a == "--mcp-config").Should().Be(1);
        var at = both.IndexOf("--mcp-config");
        both[at + 1].Should().Be(@"C:\cfg\.myriagent-shared\mcp-1.json");
        JObject.Parse(both[at + 2])["mcpServers"]![DelegationMcpRules.ServerName].Should().NotBeNull();
        both[at + 3].Should().StartWith("--");

        // 精簡模式不載入 MCP：不掛、也不放行
        ClaudeStreamRules.BuildArguments(plain with { UseLocalCustomizations = false, DelegationMcpPipeName = Pipe })
            .Should().NotContain("--mcp-config").And.NotContain(DelegationMcpRules.ClaudeAllowedTools);
    }

    [Fact]
    public void 管道名稱從無到有要重開程序_Claude與Codex一致()
    {
        static AgentSessionSettings Settings(string? pipe) => new("s-1", false, new AgentLauncher(@"C:\bin\claude.exe", []), "a1|C:\\cfg", _ => { }, null,
            null, null, AgentPermissionMode.Ask, @"C:\w", @"C:\conv", "說明", true, DelegationMcpPipeName: pipe);
        ClaudeAgentSession.NeedsRestart(Settings(null), Settings(Pipe)).Should().BeTrue("第一次 @ 帳戶，舊程序沒有 MCP 工具");
        ClaudeAgentSession.NeedsRestart(Settings(Pipe), Settings(Pipe)).Should().BeFalse();
        CodexAgentSession.Signature(Settings(null)).Should().NotBe(CodexAgentSession.Signature(Settings(Pipe)));
    }

    [Fact]
    public void 提示文字_這則有提及就交代對象_沒有就只在使用者要求時委派()
    {
        var now = DelegationMcpRules.DelegationHint([("acc-codex", "工作 Codex", "Codex")]);
        now.Should().Contain("「工作 Codex」（Codex，accountId：acc-codex）").And.Contain("delegate_task").And.StartWith("\n\n<myriagent_account_delegation>");
        var later = DelegationMcpRules.DelegationHint([]);
        later.Should().Contain("只有使用者要求").And.NotContain("accountId：");
    }

    [Fact]
    public void 回傳結果只取最後一輪的助理文字_太長保留結尾()
    {
        DelegationMcpRules.TurnAnswer([(true, false, "第一輪"), (false, true, "舊回答"), (true, false, "第二輪"), (false, false, "工具輸出"), (false, true, " 新回答 "), (false, true, "  "), (false, true, "結論")])
            .Should().Be(("新回答\n\n結論", false));
        var (text, truncated) = DelegationMcpRules.TurnAnswer([(true, false, "工作"), (false, true, new string('a', DelegationMcpRules.MaxTextLength) + "結尾")]);
        truncated.Should().BeTrue();
        text.Should().HaveLength(DelegationMcpRules.MaxTextLength).And.EndWith("結尾");
    }

    [Fact]
    public void JSONRPC字串欄位遇到null或其他型別不丟例外()
    {
        var o = JObject.Parse("""{"a":"x","b":null,"c":{"d":1},"e":3}""");
        DelegationMcpRules.StringField(o, "a").Should().Be("x");
        DelegationMcpRules.StringField(o, "b").Should().BeNull();
        DelegationMcpRules.StringField(o, "c").Should().BeNull();
        DelegationMcpRules.StringField(o, "e").Should().BeNull();
        DelegationMcpRules.StringField(o["b"], "x").Should().BeNull("params 是 JSON null");
        DelegationMcpRules.StringField(null, "x").Should().BeNull();
    }

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2099-01-01", "2024-11-05")]
    [InlineData(null, "2024-11-05")]
    public void MCP協定版本照client要求的已知版本(string? requested, string expected)
        => DelegationMcpRules.NegotiateProtocol(requested).Should().Be(expected);
}
