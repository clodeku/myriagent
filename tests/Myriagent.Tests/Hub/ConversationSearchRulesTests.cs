using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>側欄對話搜尋（標題與內容）的規則：關鍵字的切法、每個關鍵字都要在標題或內容、片段怎麼截</summary>
public sealed class ConversationSearchRulesTests
{
    [Fact]
    public void 關鍵字_以空白分開_不分大小寫去重複_最多八個()
    {
        ConversationSearchRules.Terms(null).Should().BeEmpty();
        ConversationSearchRules.Terms("  \t\n　").Should().BeEmpty("全形空白也算空白");
        ConversationSearchRules.Terms(" 部署　失敗\nHUB-PC ").Should().Equal("部署", "失敗", "HUB-PC");
        ConversationSearchRules.Terms("API api Api key").Should().Equal(new[] { "API", "key" }, "留第一次出現的寫法");
        ConversationSearchRules.Terms(string.Join(' ', Enumerable.Range(1, 12).Select(i => "t" + i))).Should().HaveCount(ConversationSearchRules.MaxTerms);
    }

    [Fact]
    public void 關鍵字_整串先截到上限()
    {
        var query = new string('a', ConversationSearchRules.MaxQueryLength) + " 後面";
        ConversationSearchRules.Terms(query).Should().Equal(new string('a', ConversationSearchRules.MaxQueryLength));
    }

    [Theory]
    [InlineData("User", true)]
    [InlineData("Assistant", true)]
    [InlineData("PlanApproval", true)]
    [InlineData("Thinking", false)]
    [InlineData("Tool", false)]
    [InlineData("Notice", false)]
    [InlineData("Error", false)]
    [InlineData("Question", false)]
    public void 內容只看使用者訊息_代理回覆與計畫(string kind, bool searchable)
        => ConversationSearchRules.IsSearchable(Enum.Parse<AgentItemKind>(kind)).Should().Be(searchable);

    [Fact]
    public void 只有標題符合_片段是null()
    {
        var match = ConversationSearchRules.Match("部署 MyriagentHub", ["你好", "沒有關鍵字"], ConversationSearchRules.Terms("myriagenthub"));
        match.Should().NotBeNull();
        match!.Snippet.Should().BeNull();
    }

    [Fact]
    public void 內容符合_片段取依序第一則的最早符合處()
    {
        var match = ConversationSearchRules.Match("別的標題", ["第一則沒有", "這裡有 B 也有 A", "後面也有 A"], ConversationSearchRules.Terms("a b"));
        match!.Snippet.Should().Be("這裡有 B 也有 A", "第二則先出現 B");
    }

    [Fact]
    public void 每個關鍵字都要在標題或內容_可以分散()
    {
        var terms = ConversationSearchRules.Terms("部署 失敗");
        ConversationSearchRules.Match("部署到 HUB-PC", ["結果失敗了"], terms)!.Snippet.Should().Be("結果失敗了", "標題有一個、內容有另一個");
        ConversationSearchRules.Match("部署到 HUB-PC", ["成功"], terms).Should().BeNull("失敗哪裡都沒有");
        ConversationSearchRules.Match(null, ["先部署", "再看失敗"], terms)!.Snippet.Should().Be("先部署");
        ConversationSearchRules.Match("標題", [], terms).Should().BeNull();
        ConversationSearchRules.Match("標題", ["內容"], []).Should().BeNull("沒有關鍵字不算符合（網頁那時不搜）");
    }

    [Fact]
    public void 不分大小寫()
        => ConversationSearchRules.Match(null, ["Run DOTNET build"], ConversationSearchRules.Terms("dotnet"))!.Snippet.Should().Be("Run DOTNET build");

    [Fact]
    public void 片段_空白與換行併成一個空格_兩端截掉時加刪節號()
    {
        var before = new string('前', 40);
        var after = new string('後', 120);
        var text = before + "\r\n\r\n  關鍵字\n\n" + after;
        var snippet = ConversationSearchRules.Snippet(text, text.IndexOf("關鍵字", StringComparison.Ordinal), 3);
        snippet.Should().StartWith("…").And.EndWith("…");
        snippet.Should().Contain(" 關鍵字 後", "換行與空白併成一個空格");
        var body = snippet.Trim('…');
        body.IndexOf("關鍵字", StringComparison.Ordinal).Should().Be(ConversationSearchRules.SnippetBefore, "前面留滿上限（含併成的空格）");
        (body.Length - body.IndexOf("關鍵字", StringComparison.Ordinal)).Should().Be(ConversationSearchRules.SnippetAfter);
    }

    [Fact]
    public void 片段_沒截到就不加刪節號_截掉的只有空白也不加()
    {
        ConversationSearchRules.Snippet("短短的關鍵字句子", 3, 3).Should().Be("短短的關鍵字句子");
        ConversationSearchRules.Snippet("   \n關鍵字\n\n   ", 4, 3).Should().Be("關鍵字");
        var spaces = new string(' ', 1000);
        ConversationSearchRules.Snippet(spaces + "關鍵字" + spaces, 1000, 3).Should().Be("關鍵字", "視窗外只有空白");
        ConversationSearchRules.Snippet("前" + spaces + "關鍵字", 1001, 3).Should().Be("…關鍵字", "視窗外有字");
    }

    [Fact]
    public void 片段_關鍵字比上限長時整個留著()
    {
        var term = new string('字', 150);
        var text = "前面 " + term + " 後面";
        ConversationSearchRules.Snippet(text, 3, term.Length).Should().Be("前面 " + term + "…");
    }

    [Fact]
    public void 片段_不從代理字元對中間切()
    {
        var emoji = "😀";
        // 前段 61 字取最後 24 字時從低位代理開始、後段 123 字取 80 字時停在高位代理：兩端都要退一字
        var text = string.Concat(Enumerable.Repeat(emoji, 30)) + "x關鍵字" + string.Concat(Enumerable.Repeat(emoji, 60));
        var snippet = ConversationSearchRules.Snippet(text, text.IndexOf("關鍵字", StringComparison.Ordinal), 3);
        snippet.Trim('…').Should().NotContain("�");
        foreach (var (ch, i) in snippet.Select((c, i) => (c, i)))
        {
            if (char.IsHighSurrogate(ch))
                char.IsLowSurrogate(snippet[i + 1]).Should().BeTrue();
            if (char.IsLowSurrogate(ch))
                char.IsHighSurrogate(snippet[i - 1]).Should().BeTrue();
        }
    }
}
