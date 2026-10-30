using Myriagent.Health;

namespace Myriagent.Tests.Health;

/// <summary>健康度的文字規則：粗估 token、去掉 HTML 註解、@ 匯入、YAML 前置資料、TOML 的表頭與布林值</summary>
public sealed class HealthTextRulesTests
{
    [Fact]
    public void 粗估token_中文一字一個_其他四個字元一個()
    {
        HealthTextRules.EstimateTokens(null).Should().Be(0);
        HealthTextRules.EstimateTokens("abcd").Should().Be(1);
        HealthTextRules.EstimateTokens("abcde").Should().Be(2, "無條件進位");
        HealthTextRules.EstimateTokens("一律使用繁體中文").Should().Be(8);
        HealthTextRules.EstimateTokens("使用 git").Should().Be(2 + 1);
        HealthTextRules.EstimateTokens(char.ConvertFromUtf32(0x20BB7)).Should().Be(1, "代理對算一個字");
    }

    [Fact]
    public void 行數與字元數()
    {
        HealthTextRules.CountLines("").Should().Be(0);
        HealthTextRules.CountLines("a").Should().Be(1);
        HealthTextRules.CountLines("a\n").Should().Be(1);
        HealthTextRules.CountLines("a\nb").Should().Be(2);
        HealthTextRules.CountChars("ab" + char.ConvertFromUtf32(0x20BB7)).Should().Be(3);
    }

    [Fact]
    public void 去掉HTML註解_程式碼區塊裡的保留()
    {
        var text = "前\n<!-- 維護者備註\n第二行 -->\n後 <!-- 行內 --> 尾\n```\n<!-- 範例 -->\n```\n";
        var stripped = HealthTextRules.StripHtmlComments(text);
        stripped.Should().Be("前\n後  尾\n```\n<!-- 範例 -->\n```\n");
        HealthTextRules.Measure(text, 999).Should().Be(new TextMeasure(999, HealthTextRules.CountChars(stripped), 5, HealthTextRules.EstimateTokens(stripped)));
    }

    [Fact]
    public void 匯入_程式碼與行內程式碼裡的不算_去掉結尾標點()
    {
        var text = string.Join("\n",
            "見 @docs/a.md。",
            "也見 @~/notes/b.md 與 (@./c.md)",
            "信箱 me@example.com 不算、`@inline.md` 不算",
            "@某人 沒有路徑的樣子不算",
            "```",
            "@code/d.md",
            "```",
            "網址 @https://x.test/a.md 不算");
        HealthTextRules.FindImports(text).Should().Equal("docs/a.md", "~/notes/b.md", "./c.md");
    }

    [Fact]
    public void 匯入路徑的解析()
    {
        var home = Path.Combine(Path.GetTempPath(), "home");
        var file = Path.Combine(home, ".claude", "CLAUDE.md");
        HealthTextRules.ResolveImport("~/x/y.md", file, home).Should().Be(Path.GetFullPath(Path.Combine(home, "x", "y.md")));
        HealthTextRules.ResolveImport("docs/a.md", file, home).Should().Be(Path.GetFullPath(Path.Combine(home, ".claude", "docs", "a.md")));
        HealthTextRules.ResolveImport(@"C:\abs\z.md", file, home).Should().Be(@"C:\abs\z.md");
        HealthTextRules.ResolveImport("~", file, home).Should().BeNull();
    }

    [Fact]
    public void 前置資料_多行描述與清單()
    {
        var text = "---\nname: my-skill\ndescription: >\n  第一行\n  第二行\nwhen_to_use: \"做 X 時\"\npaths:\n  - \"src/**/*.ts\"\n---\n# 內容\n";
        var fm = HealthTextRules.ParseFrontMatter(text);
        HealthTextRules.FrontMatterText(fm, "name").Should().Be("my-skill");
        HealthTextRules.FrontMatterText(fm, "description").Should().Be("第一行 第二行");
        HealthTextRules.FrontMatterText(fm, "when_to_use").Should().Be("做 X 時");
        HealthTextRules.HasPathScope(fm).Should().BeTrue();
        HealthTextRules.SkillListingChars("my-skill", fm, 1536).Should().Be("my-skill".Length + "第一行 第二行 做 X 時".Length);
        HealthTextRules.SkillListingChars("s", fm, 3).Should().Be(1 + 3, "描述有上限");

        HealthTextRules.ParseFrontMatter("# 沒有前置資料").Should().BeEmpty();
        HealthTextRules.ParseFrontMatter("---\nname: x\n沒有結尾").Should().BeEmpty();
        HealthTextRules.HasPathScope(HealthTextRules.ParseFrontMatter("---\npaths: []\n---\n")).Should().BeFalse();
    }

    [Fact]
    public void 數字的顯示()
    {
        HealthTextRules.FormatTokens(850).Should().Be("約 850 token");
        HealthTextRules.FormatTokens(3200).Should().Be("約 3,200 token");
        HealthTextRules.FormatTokens(12_550).Should().Be("約 1.3 萬 token");
        HealthTextRules.FormatBytes(820).Should().Be("820 B");
        HealthTextRules.FormatBytes(25_190).Should().Be("24.6 KB");
        HealthTextRules.FormatBytes(3L * 1024 * 1024).Should().Be("3 MB");
    }

    [Fact]
    public void TOML_表頭名稱與布林值()
    {
        var toml = string.Join("\n",
            "model = \"gpt\"  # [mcp_servers.fake] 註解裡的不算",
            "[mcp_servers.context7]",
            "command = \"npx\"",
            "[mcp_servers.context7.env]",
            "KEY = \"v\"",
            "[mcp_servers.\"a.b\"]",
            "[[mcp_servers_list]]",
            "[claude_compat]",
            "imported = true # 已匯入",
            "[other]",
            "imported = false");
        HealthTomlRules.TableNames(toml, "mcp_servers").Should().Equal("context7", "a.b");
        HealthTomlRules.BoolValue(toml, "claude_compat", "imported").Should().BeTrue();
        HealthTomlRules.BoolValue("claude_compat.imported = false", "claude_compat", "imported").Should().BeFalse();
        HealthTomlRules.BoolValue("[claude_compat]\nimported = \"yes\"", "claude_compat", "imported").Should().BeNull();
        HealthTomlRules.BoolValue("", "claude_compat", "imported").Should().BeNull();
    }
}
