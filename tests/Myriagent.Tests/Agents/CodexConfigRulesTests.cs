using Myriagent.Agents;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 精簡模式要停用 config.toml 的每個 MCP 伺服器（codex-cli 0.160.1 接受的名稱規則是 ^[a-zA-Z0-9_:@/.-]+$）。
/// 名稱抓漏了，伺服器在精簡模式照樣啟動；抓到不存在的名稱更糟：等於設定裡多一個沒有 command 的伺服器，Codex 會整個起不來。
/// </summary>
public class CodexConfigRulesTests
{
    [Fact]
    public void ParseMcpServerNames_引號名稱可含點冒號小老鼠斜線()
    {
        var toml = """
            [mcp_servers."docs.example"]
            command = "docs.exe"
            [mcp_servers.'org/tool']
            command = "tool.exe"
            [mcp_servers."team:helper@v2"]
            url = "https://example.com/mcp"
            """;

        CodexConfigRules.ParseMcpServerNames(toml).Should().Equal("docs.example", "org/tool", "team:helper@v2");
    }

    [Fact]
    public void ParseMcpServerNames_McpServers表內的行內表與點號鍵()
    {
        var toml = """
            model = "gpt-6-astra"

            [mcp_servers]
            inline_one = { command = "one.exe", args = ["--a"] }
            inline_two.command = "two.exe"
            inline_two.args = ["--b"]
            "quoted.three" = { url = "https://example.com" }

            [features]
            js_repl = false
            not_a_server = true
            """;

        CodexConfigRules.ParseMcpServerNames(toml).Should().Equal("inline_one", "inline_two", "quoted.three");
    }

    [Fact]
    public void ParseMcpServerNames_根層的點號鍵()
    {
        var toml = """
            mcp_servers.root_one.command = "one.exe"
            mcp_servers . "root.two" . url = "https://example.com"
            model = "x"
            """;

        CodexConfigRules.ParseMcpServerNames(toml).Should().Equal("root_one", "root.two");
    }

    [Fact]
    public void ParseMcpServerNames_多行字串與跨行陣列裡的內容不算設定鍵()
    {
        var toml = """"
            developer_instructions = """
            [mcp_servers.fake_header]
            fake_key = 1
            """
            note = '''
            [mcp_servers.fake_literal]
            '''
            [mcp_servers]
            real.args = [
              "x = 1",
              "[mcp_servers.fake_in_array]",
            ]
            real.command = "real.exe"
            real.description = """第一行
            bogus = 2
            結尾 \""" 還沒結束
            """
            after = { command = "after.exe" }
            """";

        CodexConfigRules.ParseMcpServerNames(toml).Should().Equal(new[] { "real", "after" },
            "多行字串、跨行陣列裡看起來像標頭或鍵的文字都不是設定");
    }

    [Fact]
    public void ParseMcpServerNames_陣列表與其他表裡的鍵不算()
    {
        var toml = """
            [[mcp_servers]]
            name = "array_table"
            [profiles.work]
            mcp_servers = "x"
            [projects.'c:\repo\a']
            trust_level = "trusted"
            """;

        CodexConfigRules.ParseMcpServerNames(toml).Should().BeEmpty();
    }

    [Fact]
    public void ParseMcpServerNames_同名只算一次_Crlf換行也認得()
    {
        var toml = "[mcp_servers.a]\r\ncommand = \"a.exe\"\r\n[mcp_servers.a.env]\r\nX = \"1\"\r\n[mcp_servers]\r\na.args = []\r\nb = { command = \"b.exe\" } # 行尾註解\r\n";

        CodexConfigRules.ParseMcpServerNames(toml).Should().Equal("a", "b");
    }

    // ───── 共用設定：來源的 MCP 伺服器轉成 -c 覆寫（實測 codex-cli 0.163.0 的 mcp list 照這些覆寫列出伺服器） ─────

    [Fact]
    public void BuildSharedMcpOverrides_表頭子表行內表與跨行的值()
    {
        var toml = "model = \"gpt-6\"\n\n[mcp_servers.node_repl]\ncommand = \"node\" # 註解\nargs = [\n  \"-e\",\n  \"1\",\n]\n\n"
                   + "[mcp_servers.node_repl.env]\n\"API-KEY\" = \"secret\"\n\n"
                   + "[mcp_servers]\ninline = { command = \"npx\", args = [\"-y\", \"x\"] }\ndotted.command = \"d.exe\"\n\n[features]\njs_repl = false\n";

        CodexConfigRules.BuildSharedMcpOverrides(toml, Array.Empty<string>()).Should().Equal(
            "mcp_servers.node_repl.command=\"node\" # 註解",
            "mcp_servers.node_repl.args=[\n  \"-e\",\n  \"1\",\n]",
            "mcp_servers.node_repl.env.API-KEY=\"secret\"",
            "mcp_servers.inline={ command = \"npx\", args = [\"-y\", \"x\"] }",
            "mcp_servers.dotted.command=\"d.exe\"");
    }

    [Fact]
    public void BuildSharedMcpOverrides_帳戶自己有的名稱不給()
    {
        var toml = "[mcp_servers.mine]\r\ncommand = \"source\"\r\n[mcp_servers.other]\r\ncommand = \"other\"\r\n";

        CodexConfigRules.BuildSharedMcpOverrides(toml, new[] { "mine" }).Should().Equal("mcp_servers.other.command=\"other\"");
    }

    [Fact]
    public void BuildSharedMcpOverrides_名稱或鍵含點號_用到陣列表_值沒結束的伺服器整個略過()
    {
        var toml = "[mcp_servers.\"docs.example\"]\ncommand = \"docs.exe\"\n"
                   + "[mcp_servers.arr]\ncommand = \"arr.exe\"\n[[mcp_servers.arr.tools]]\nname = \"x\"\n"
                   + "[mcp_servers.dotkey]\ncommand = \"k.exe\"\n[mcp_servers.dotkey.env]\n\"A.B\" = \"1\"\n"
                   + "[mcp_servers.good]\ncommand = \"good.exe\"\n"
                   + "[mcp_servers.cut]\nargs = [\n  \"unterminated\"\n";

        CodexConfigRules.BuildSharedMcpOverrides(toml, Array.Empty<string>()).Should().Equal(new[] { "mcp_servers.good.command=\"good.exe\"" },
            "-c 的鍵只用「.」切開；只給一部分等於少了 command，Codex 會整個起不來");
        CodexConfigRules.BuildSharedMcpOverrides(null, Array.Empty<string>()).Should().BeEmpty();
    }
}
