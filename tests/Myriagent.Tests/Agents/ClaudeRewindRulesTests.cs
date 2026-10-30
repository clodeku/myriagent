using Myriagent.Agents;

namespace Myriagent.Tests.Agents;

/// <summary>回溯對話的分支點（逐字稿的形狀取自 2.1.310 探針 tools/probes/claude_cli6_probe.py --mode rewind2）</summary>
public class ClaudeRewindRulesTests
{
    private static string Entry(string type, string uuid, string? parent, string? subtype = null)
        => "{\"type\":\"" + type + "\",\"uuid\":\"" + uuid + "\",\"parentUuid\":" + (parent == null ? "null" : "\"" + parent + "\"")
           + (subtype == null ? string.Empty : ",\"subtype\":\"" + subtype + "\"") + ",\"sessionId\":\"s\"}";

    private static readonly string[] Transcript =
    [
        "{\"type\":\"queue-operation\",\"operation\":\"enqueue\"}",
        Entry("user", "u1", null),
        Entry("attachment", "x1", "u1"),
        Entry("assistant", "a1", "x1"),
        Entry("assistant", "a1b", "a1"),
        Entry("user", "u2", "a1b"),
        Entry("assistant", "a2", "u2"),
        Entry("system", "s3", "a2", "local_command"),
        Entry("user", "u3", "s3"),
        Entry("system", "cb", null, "compact_boundary"),
        Entry("user", "u4", "cb"),
        "不是 JSON",
    ];

    [Fact]
    public void 沿parentUuid找最近的assistant_第一則開新的session()
    {
        ClaudeRewindRules.Find(Transcript, "u2").Should().Be(new ClaudeRewindPoint(ClaudeRewindPointKind.At, "a1b"));
        ClaudeRewindRules.Find(Transcript, "u3").Should().Be(new ClaudeRewindPoint(ClaudeRewindPointKind.At, "a2"), "中間隔著斜線指令的 system 也往前找");
        ClaudeRewindRules.Find(Transcript, "u1").Kind.Should().Be(ClaudeRewindPointKind.First);
    }

    [Fact]
    public void 找不到_不是使用者訊息_剛好在壓縮之後都不能回溯()
    {
        ClaudeRewindRules.Find(Transcript, "missing").Kind.Should().Be(ClaudeRewindPointKind.NotFound);
        ClaudeRewindRules.Find(Transcript, "a2").Kind.Should().Be(ClaudeRewindPointKind.NotFound);
        ClaudeRewindRules.Find(Transcript, "u4").Kind.Should().Be(ClaudeRewindPointKind.NotFound);
        ClaudeRewindRules.Find([Entry("user", "u9", "gone")], "u9").Kind.Should().Be(ClaudeRewindPointKind.NotFound, "前一則不在逐字稿裡");
        ClaudeRewindRules.Find([Entry("user", "p", "q"), Entry("attachment", "q", "p")], "p").Kind.Should().Be(ClaudeRewindPointKind.NotFound, "循環不會卡住");
        ClaudeRewindRules.ReadLines(null).Should().BeEmpty();
    }

    [Fact]
    public void DescribeFilesError_常見的錯誤改成說明()
    {
        ClaudeRewindRules.DescribeFilesError("File rewinding is not enabled.").Should().Contain("沒有開檔案還原點");
        ClaudeRewindRules.DescribeFilesError("No file checkpoint found for this message.").Should().Contain("沒有檔案還原點");
        ClaudeRewindRules.DescribeFilesError("其他").Should().Be("其他");
    }
}
