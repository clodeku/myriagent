using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 日誌的識別（第 5 階段計畫 5.6、WP5.1）：各種 logId 的組成與解析、logKey、由 logId 決定的種類、要求 LF 的種類、
/// 相對路徑的正規化（非 ASCII 照收；..、:、根路徑、Windows 保留名稱與結尾的點或空白一律拒絕——接手端拿它接在目標資料夾之下）
/// </summary>
public sealed class LogIdRulesTests
{
    private const string Sid = "ed703f02-0092-409f-907d-3e6ab753ef6c";

    private const string Thread = "0199a213-81c0-7800-8aa1-bbab2a035a53";

    private const string Conv = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void 各種logId的組成_解析回同樣的部分_種類由logId決定()
    {
        var cases = new (string LogId, string Kind, string Owner, string? Rel)[]
        {
            (LogIdRules.ClaudeMain(Sid), LogKinds.ClaudeMain, Sid, null),
            (LogIdRules.ClaudeCompanion(Sid, @"subagents\agent-a1b2.jsonl"), LogKinds.ClaudeSub, Sid, "subagents/agent-a1b2.jsonl"),
            (LogIdRules.ClaudeCompanion(Sid, "subagents/agent-a1b2.meta.json"), LogKinds.ClaudeFile, Sid, "subagents/agent-a1b2.meta.json"),
            (LogIdRules.ClaudeCompanion(Sid, @"tool-results\toolu_01ABC.txt"), LogKinds.ClaudeFile, Sid, "tool-results/toolu_01ABC.txt"),
            (LogIdRules.CodexRollout(Thread), LogKinds.CodexRollout, Thread, null),
            (LogIdRules.Attachment(Conv, @".model\frame-01.png"), LogKinds.Attachment, Conv, ".model/frame-01.png"),
            (LogIdRules.Workspace(Conv, @"src\報表.xlsx"), LogKinds.Workspace, Conv, "src/報表.xlsx"),
        };

        cases.Select(c => c.LogId).Should().Equal(
            "cl:" + Sid,
            "cl:" + Sid + "/subagents/agent-a1b2.jsonl",
            "cl:" + Sid + "/subagents/agent-a1b2.meta.json",
            "cl:" + Sid + "/tool-results/toolu_01ABC.txt",
            "cx:" + Thread,
            "fa:" + Conv + "/.model/frame-01.png",
            "fw:" + Conv + "/src/報表.xlsx");
        foreach (var c in cases)
        {
            LogIdRules.TryParse(c.LogId, out var parts).Should().BeTrue(c.LogId);
            parts!.Should().Be(new LogIdParts(c.Kind, c.Owner, c.Rel), c.LogId);
            LogIdRules.KindOf(c.LogId).Should().Be(c.Kind);
            LogKinds.IsKnown(c.Kind).Should().BeTrue();
        }
    }

    [Fact]
    public void logKey是logId的UTF8的SHA256小寫十六進位()
    {
        LogIdRules.LogKey("cl:" + Sid).Should().Be("a359ce3001baeaf28cf930a3a0fbcd7ff06bd329bcdde7d20f40c5124f320aef", "Python hashlib 算的");
        LogIdRules.LogKey(LogIdRules.Attachment(Conv, "截圖 1.png")).Should().Be("ecaef3de074bf23b0f794c8e9355feb8b00cb7498e9a44a6024fabdf4995a2f4", "非 ASCII 以 UTF-8");
        LogIdRules.IsLogKey(LogIdRules.LogKey("cl:" + Sid)).Should().BeTrue();
        LogIdRules.IsLogKey(LogIdRules.LogKey("cl:" + Sid).ToUpperInvariant()).Should().BeFalse("只收小寫");
        LogIdRules.IsLogKey(new string('a', 63)).Should().BeFalse();
        LogIdRules.IsLogKey(null).Should().BeFalse();
    }

    [Fact]
    public void 要求LF的只有只附加的jsonl()
    {
        LogIdRules.RequiresLf(LogKinds.ClaudeMain).Should().BeTrue();
        LogIdRules.RequiresLf(LogKinds.ClaudeSub).Should().BeTrue();
        LogIdRules.RequiresLf(LogKinds.CodexRollout).Should().BeTrue();
        LogIdRules.RequiresLf(LogKinds.ClaudeFile).Should().BeFalse("tool-results 寫一次、meta.json 會改寫：整檔");
        LogIdRules.RequiresLf(LogKinds.Attachment).Should().BeFalse();
        LogIdRules.RequiresLf(LogKinds.Workspace).Should().BeFalse();
        LogIdRules.RequiresLf("future-kind").Should().BeFalse();
        LogKinds.All.Should().Equal("claude-main", "claude-sub", "claude-file", "codex-rollout", "attachment", "workspace");
        LogKinds.All.Where(LogKinds.IsCliRecord).Should().Equal("claude-main", "claude-sub", "claude-file", "codex-rollout");
    }

    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData(@"src\a.txt", "src/a.txt")]
    [InlineData("截圖 2026-10-23.png", "截圖 2026-10-23.png")]
    [InlineData(@"資料夾\子資料夾\😀.md", "資料夾/子資料夾/😀.md")]
    [InlineData(".model/frame.png", ".model/frame.png")]
    [InlineData("a..b", "a..b")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("com10", "com10")]
    public void 相對路徑的正規化_合格的(string input, string expected) => LogIdRules.NormalizeRelativePath(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(@"..\a.txt")]
    [InlineData("a/../b")]
    [InlineData("./a")]
    [InlineData("/a.txt")]
    [InlineData(@"\a.txt")]
    [InlineData(@"\\server\share\a")]
    [InlineData(@"C:\a.txt")]
    [InlineData("C:a.txt")]
    [InlineData("a.txt:stream")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("a.")]
    [InlineData("a ")]
    [InlineData("dir./a")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("sub/NUL")]
    [InlineData("LPT1.log")]
    [InlineData("COM9")]
    [InlineData("a<b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a\"b")]
    [InlineData("a\tb")]
    [InlineData("a\0b")]
    public void 相對路徑的正規化_拒絕的(string? input) => LogIdRules.NormalizeRelativePath(input).Should().BeNull();

    [Fact]
    public void 相對路徑_落單的代理字元與太長的拒絕()
    {
        LogIdRules.NormalizeRelativePath("a" + (char)0xD800 + "b").Should().BeNull("落單的高代理字元");
        LogIdRules.NormalizeRelativePath("a" + (char)0xDC00 + "b").Should().BeNull("落單的低代理字元");
        LogIdRules.NormalizeRelativePath(new string('a', LogIdRules.MaxRelativePathLength)).Should().NotBeNull();
        LogIdRules.NormalizeRelativePath(new string('a', LogIdRules.MaxRelativePathLength + 1)).Should().BeNull();
    }

    [Theory]
    [InlineData("cl:")]
    [InlineData("cl:" + Sid + "/")]
    [InlineData("cl:" + Sid + "/../x")]
    [InlineData("cl:" + Sid + "\\subagents\\a.jsonl")]
    [InlineData("cl:" + Sid + "/subagents\\a.jsonl")]
    [InlineData("cl:../x")]
    [InlineData("cl:a.b")]
    [InlineData("cx:" + Thread + "/x")]
    [InlineData("fa:" + Conv)]
    [InlineData("fa:" + Conv + "/")]
    [InlineData("fa:0123456789ABCDEF0123456789ABCDEF/a.png")]
    [InlineData("fa:0123/a.png")]
    [InlineData("fw:" + Conv + "/C:/a")]
    [InlineData("xx:" + Sid)]
    [InlineData("CL:" + Sid)]
    [InlineData("")]
    [InlineData(null)]
    public void 不合格的logId(string? logId)
    {
        LogIdRules.TryParse(logId, out var parts).Should().BeFalse(logId);
        parts.Should().BeNull();
        LogIdRules.KindOf(logId).Should().BeNull();
        LogIdRules.IsValid(logId).Should().BeFalse();
    }

    [Fact]
    public void session_id與thread_id只能是英數字連字號底線()
    {
        LogIdRules.IsSessionId(Sid).Should().BeTrue();
        LogIdRules.IsSessionId("abc_DEF-123").Should().BeTrue();
        LogIdRules.IsSessionId(new string('a', LogIdRules.MaxSessionIdLength)).Should().BeTrue();
        LogIdRules.IsSessionId(new string('a', LogIdRules.MaxSessionIdLength + 1)).Should().BeFalse();
        LogIdRules.IsSessionId("a.b").Should().BeFalse("句點不行：接手端拿它組 <sid>.jsonl 與 <sid>\\ 資料夾");
        LogIdRules.IsSessionId("..").Should().BeFalse();
        LogIdRules.IsSessionId("a/b").Should().BeFalse();
        LogIdRules.IsSessionId(string.Empty).Should().BeFalse();
        LogIdRules.IsSessionId(null).Should().BeFalse();
    }

    [Fact]
    public void 組成時參數不合格丟例外_不會組出不合格的logId()
    {
        FluentActions.Invoking(() => LogIdRules.ClaudeMain("../x")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => LogIdRules.ClaudeCompanion(Sid, @"..\..\secrets.txt")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => LogIdRules.CodexRollout("a b")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => LogIdRules.Attachment(Conv.ToUpperInvariant(), "a.png")).Should().Throw<ArgumentException>("對話 id 只收線上格式");
        FluentActions.Invoking(() => LogIdRules.Workspace(Conv, "C:/Windows/a.dll")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => LogIdRules.LogKey(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void logId長度上限()
    {
        var longest = LogIdRules.Attachment(Conv, new string('a', LogIdRules.MaxRelativePathLength));
        longest.Length.Should().BeLessThanOrEqualTo(LogIdRules.MaxLogIdLength);
        LogIdRules.IsValid(longest).Should().BeTrue();
        LogIdRules.IsValid("cl:" + new string('a', LogIdRules.MaxLogIdLength)).Should().BeFalse();
    }
}
