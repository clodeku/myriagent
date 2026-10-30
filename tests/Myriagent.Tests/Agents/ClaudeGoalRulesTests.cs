using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>/goal 的 goal_status（逐字稿的形狀取自 Claude Code 2.1.310 探針 tools/probes/claude_cli6_probe.py --mode goal）</summary>
public class ClaudeGoalRulesTests
{
    private static string Status(bool met, string condition, string time, bool? failed = null)
        => "{\"parentUuid\":\"p\",\"type\":\"attachment\",\"attachment\":{\"type\":\"goal_status\",\"met\":" + (met ? "true" : "false")
           + (failed is { } f ? ",\"failed\":" + (f ? "true" : "false") : string.Empty)
           + ",\"sentinel\":true,\"condition\":\"" + condition + "\"},\"timestamp\":\"" + time + "\",\"uuid\":\"u\"}";

    private static readonly DateTimeOffset Since = DateTimeOffset.Parse("2026-10-25T03:00:00Z");

    [Fact]
    public void 最後一筆還沒達成是進行中的目標_達成或失敗是沒有()
    {
        var active = Status(false, "九個檔案", "2026-10-25T03:01:00.000Z");
        ClaudeGoalRules.LastGoal(["{\"type\":\"user\"}", active], Since).Should().Be("九個檔案");
        ClaudeGoalRules.LastGoal([active, Status(true, "九個檔案", "2026-10-25T03:02:00.000Z")], Since).Should().BeEmpty("/goal clear 之後 met 是 true");
        ClaudeGoalRules.LastGoal([Status(false, "x", "2026-10-25T03:02:00.000Z", failed: true)], Since).Should().BeEmpty();
    }

    [Fact]
    public void 程序啟動之前寫的不算_沒有任何一筆回null()
    {
        ClaudeGoalRules.LastGoal([Status(false, "舊程序的目標", "2026-10-25T02:59:00.000Z")], Since).Should().BeNull("--resume 開的新程序沒有之前的目標");
        ClaudeGoalRules.LastGoal(["{\"type\":\"assistant\"}", "不是 JSON \"goal_status\""], Since).Should().BeNull();
        ClaudeGoalRules.LastGoal([Status(false, "沒有時間", "")], Since).Should().Be("沒有時間", "讀不出時間的照算");
    }

    [Fact]
    public void ReadTail_只讀尾端的完整行_檔案被寫著也讀得到()
    {
        using var temp = new TempDirectory("goal");
        var path = temp.Combine("t.jsonl");
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(0, 100).Select(i => "line" + i.ToString("D3"))) + "\n");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var tail = ClaudeGoalRules.ReadTail(path, maxBytes: 20);
            tail.Should().Equal("line098", "line099");
        }
        ClaudeGoalRules.ReadTail(path).Should().HaveCount(100);
        ClaudeGoalRules.ReadTail(temp.Combine("missing.jsonl")).Should().BeEmpty();
    }
}
