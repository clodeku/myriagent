using Myriagent.Agents;
using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 問卷的補充說明（2026-11-05 使用者要求：單選題選了選項之後，還能另外補一段說明）：解析網頁的 notes、併進答案，
/// 各家的作答格式（Claude、Grok 的 annotations.notes；Codex 併進答案），遠端以遮蔽後的題目 id 找回原題
/// </summary>
public sealed class QuestionNoteRulesTests
{
    private static readonly List<AgentQuestion> Questions =
    [
        new() { Id = "要用哪種顏色？", Text = "要用哪種顏色？", Options = [new AgentQuestionOption { Label = "紅色" }, new AgentQuestionOption { Label = "藍色" }] },
        new() { Id = "pwd", Text = "密碼", IsSecret = true },
        new() { Id = "數量", Text = "數量" },
    ];

    [Fact]
    public void Parse_只收這張卡的題目_密碼類不收_字串或陣列_去空白_截斷()
    {
        var notes = QuestionNoteRules.Parse(Questions, JObject.Parse("""
            {"要用哪種顏色？":["  要深一點的紅 ", "", "像磚頭那種"],"pwd":["不該留下"],"不認得":["x"],"數量":"  "}
            """));
        notes.Should().BeEquivalentTo(new Dictionary<string, string> { ["要用哪種顏色？"] = "要深一點的紅\n像磚頭那種" });

        QuestionNoteRules.Parse(Questions, null).Should().BeEmpty();
        QuestionNoteRules.Parse(Questions, new JObject { ["要用哪種顏色？"] = new string('長', QuestionNoteRules.MaxLength + 50) })
            ["要用哪種顏色？"].Length.Should().Be(QuestionNoteRules.MaxLength);
    }

    [Fact]
    public void Merge_補充接在有答案的題目後面_沒有答案的不算回答()
    {
        var answers = new Dictionary<string, IReadOnlyList<string>> { ["要用哪種顏色？"] = ["紅色"] };
        var merged = QuestionNoteRules.Merge(answers, new Dictionary<string, string> { ["要用哪種顏色？"] = "深一點", ["數量"] = "三個" });

        merged.Keys.Should().Equal("要用哪種顏色？");
        merged["要用哪種顏色？"].Should().Equal("紅色", QuestionNoteRules.DisplayPrefix + "深一點");
        QuestionNoteRules.Merge(answers, null)["要用哪種顏色？"].Should().Equal("紅色");
    }

    [Fact]
    public void Claude_補充放進annotations的notes_沒有補充不帶()
    {
        var input = JObject.Parse("""{"questions":[{"question":"要用哪種顏色？","options":[{"label":"紅色"},{"label":"藍色"}]}]}""");
        var answers = new Dictionary<string, IReadOnlyList<string>> { ["要用哪種顏色？"] = ["紅色"] };

        // 實測 2.1.316（tools/probes/claude_question_notes_probe.py）：CLI 交給模型「"要用哪種顏色？"="紅色" notes: …」
        var updated = ClaudeStreamRules.BuildQuestionAnswerInput(input, answers, new Dictionary<string, string> { ["要用哪種顏色？"] = " 深一點 ", ["沒回答的題"] = "x" });
        updated["answers"]!["要用哪種顏色？"]!.ToString().Should().Be("紅色");
        updated["annotations"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"要用哪種顏色？":{"notes":"深一點"}}""");

        ClaudeStreamRules.BuildQuestionAnswerInput(input, answers)["annotations"].Should().BeNull();
    }

    [Fact]
    public void Grok_補充接在自填的notes後面()
    {
        var questions = new List<AgentQuestion>
        {
            new() { Id = "色", Text = "色", Options = [new AgentQuestionOption { Label = "紅" }, new AgentQuestionOption { Label = "藍" }] },
        };
        GrokAcpRules.BuildQuestionAnswer(questions, new Dictionary<string, IReadOnlyList<string>> { ["色"] = ["紅"] }, new Dictionary<string, string> { ["色"] = "深一點" })
            .Should().BeEquivalentTo(JObject.Parse("""{"outcome":"accepted","answers":{"色":["紅"]},"annotations":{"色":{"notes":"深一點"}}}"""));
        GrokAcpRules.BuildQuestionAnswer(questions, new Dictionary<string, IReadOnlyList<string>> { ["色"] = ["紫"] }, new Dictionary<string, string> { ["色"] = "淡紫" })
            .Should().BeEquivalentTo(JObject.Parse("""{"outcome":"accepted","answers":{"色":["Other"]},"annotations":{"色":{"notes":"紫\n淡紫"}}}"""));
    }

    [Fact]
    public void Codex_沒有備註欄位_併成答案多一筆()
    {
        var merged = QuestionNoteRules.Merge(new Dictionary<string, IReadOnlyList<string>> { ["q1"] = ["A"] }, new Dictionary<string, string> { ["q1"] = "要快" });
        CodexAppServerRules.BuildUserInputResponse(merged).ToString(Newtonsoft.Json.Formatting.None)
            .Should().Be("{\"answers\":{\"q1\":{\"answers\":[\"A\",\"" + QuestionNoteRules.DisplayPrefix + "要快\"]}}}");
    }

    [Fact]
    public void 遠端_notes以遮蔽後的題目id找回原題_不認得或形狀不對回false_沒帶是空的()
    {
        var card = new AgentChatItem { Kind = AgentItemKind.Question, Questions = Questions };
        RemoteCardRules.TryMapNotes(card, null, out var none).Should().BeTrue();
        none!.Count.Should().Be(0);

        RemoteCardRules.TryMapNotes(card, JObject.Parse("""{"要用哪種顏色？":["深一點"]}"""), out var mapped).Should().BeTrue();
        mapped!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"要用哪種顏色？":["深一點"]}""");

        RemoteCardRules.TryMapNotes(card, JObject.Parse("""{"不認得":["x"]}"""), out _).Should().BeFalse();
        RemoteCardRules.TryMapNotes(card, JObject.Parse("""{"要用哪種顏色？":"不是陣列"}"""), out _).Should().BeFalse();
        RemoteCardRules.TryMapNotes(card, JObject.Parse("""{"要用哪種顏色？":[1]}"""), out _).Should().BeFalse();
    }

    [Fact]
    public void 給舊版桌面_notes併成答案多一筆_原本的指令不變_沒答的題目不併()
    {
        // 舊版的桌面不認得 notes，瀏覽器版帶補充說明的回答會被當成格式錯誤
        var command = JObject.Parse("""{"cmd":"answer","conversationId":"0123456789abcdef0123456789abcdef","itemId":"i1","answers":{"題一":["A"],"題二":["B"]},"notes":{"題一":["  開 9 個 chrome  "],"題三":["沒答的題"]},"requestDigest":"rd1:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}""");
        var folded = QuestionNoteRules.FoldNotesIntoAnswers(command);

        folded["notes"].Should().BeNull();
        folded["answers"]!["題一"]!.Values<string>().Should().Equal("A", "補充說明：開 9 個 chrome");
        folded["answers"]!["題二"]!.Values<string>().Should().Equal("B");
        folded["answers"]!["題三"].Should().BeNull("補充不能單獨當成回答");
        folded["requestDigest"]!.Value<string>().Should().Be("rd1:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        command["notes"].Should().NotBeNull("原本的指令不變");
        RemoteCommandShapeRules.Check(folded).Should().BeNull("併完的形狀照樣合格");

        var plain = JObject.Parse("""{"cmd":"answer","itemId":"i1","answers":{"題一":["A"]}}""");
        QuestionNoteRules.FoldNotesIntoAnswers(plain).Should().BeSameAs(plain);
    }

    [Fact]
    public void 給舊版桌面_併的時候守住每題答案數與長度上限()
    {
        var full = new JArray(Enumerable.Range(0, RemoteCommandShapeRules.MaxAnswerValues).Select(n => "v" + n));
        var command = new JObject
        {
            ["cmd"] = "answer", ["itemId"] = "i1",
            ["answers"] = new JObject { ["滿的"] = full, ["長的"] = new JArray("A") },
            ["notes"] = new JObject { ["滿的"] = new JArray("x"), ["長的"] = new JArray(new string('字', RemoteCommandShapeRules.MaxTextLength)) },
        };
        var folded = QuestionNoteRules.FoldNotesIntoAnswers(command);
        ((JArray)folded["answers"]!["滿的"]!).Count.Should().Be(RemoteCommandShapeRules.MaxAnswerValues);
        folded["answers"]!["長的"]!.Values<string>().Last()!.Length.Should().Be(RemoteCommandShapeRules.MaxTextLength);
    }

    [Theory]
    [InlineData("0.2.10", true)]
    [InlineData("0.2.3", true)]
    [InlineData("0.1.0", true)]
    [InlineData("0.2.11", false)]
    [InlineData("0.2.18", false)]
    [InlineData(null, false)]
    [InlineData("dev", false)]
    public void 哪些版本認不得notes(string? version, bool lacks)
        => QuestionNoteRules.DeviceLacksNotes(version).Should().Be(lacks);
}
