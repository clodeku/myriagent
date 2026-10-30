using Myriagent.Agents;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 一行摘要（Protocol 的 TextRules.FirstLine）：工具卡、核准卡、拒絕說明共用，桌面與伺服器必須得到同一個字串。
/// 規則照抄自 ClaudeStreamRules.FirstLine（現在改成轉呼叫這裡），這些案例把現有行為固定下來
/// </summary>
public class TextRulesTests
{
    [Theory]
    [InlineData("\r\n  \n  第一行  \n第二行", "第一行")]
    [InlineData("npm test", "npm test")]
    [InlineData("  \t前後的空白與定位字元\t  ", "前後的空白與定位字元")]
    [InlineData("   \n\t\n", "")]
    [InlineData("", "")]
    [InlineData("第一行\r第二行", "第一行第二行")]
    public void FirstLine_取第一個不是空白的行並去掉頭尾空白(string text, string expected)
        => TextRules.FirstLine(text).Should().Be(expected, "單獨的 \\r 不算換行（直接刪掉），只有 \\n 分行");

    [Fact]
    public void FirstLine_全形空白也算空白()
    {
        var fullWidth = ((char)0x3000).ToString();
        TextRules.FirstLine($"{fullWidth}\n{fullWidth}全形空白也去掉{fullWidth}\n第二行").Should().Be("全形空白也去掉");
    }

    [Fact]
    public void FirstLine_Null回空字串()
        => TextRules.FirstLine(null).Should().BeEmpty();

    [Fact]
    public void FirstLine_超過200字截斷加刪節號_剛好200字不截斷()
    {
        TextRules.FirstLine(new string('字', 250)).Should().Be(new string('字', 200) + "…");
        TextRules.FirstLine(new string('字', 201)).Should().Be(new string('字', 200) + "…");
        TextRules.FirstLine(new string('字', 200)).Should().Be(new string('字', 200));
        TextRules.FirstLine("  " + new string('a', 200) + "  \n下一行").Should().Be(new string('a', 200), "先去掉空白再看長度");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n  \n  第一行  \n第二行")]
    [InlineData("第一行\r第二行")]
    [InlineData("dotnet test Myriagent.slnx -c Release\n輸出…")]
    public void ClaudeStreamRules的FirstLine_轉呼叫同一個規則(string? text)
        => ClaudeStreamRules.FirstLine(text).Should().Be(TextRules.FirstLine(text));

    [Fact]
    public void ClaudeStreamRules的FirstLine_長文字也相同()
    {
        var text = new string('字', 500);
        ClaudeStreamRules.FirstLine(text).Should().Be(TextRules.FirstLine(text));
    }
}
