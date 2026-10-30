using Myriagent.Agents;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>對話框燈號（web-protocol 的 activity）的純規則：取樣間隔、程序樹與計數器的增量、硬碟扣掉管線、訊息的形狀</summary>
public class ActivityRulesTests
{
    private static ProcessStamp P(int id, long start = 100) => new(id, start);

    [Fact]
    public void 間隔_有對話在忙250毫秒_只量網路500毫秒()
    {
        ActivityRules.IntervalMs(true).Should().Be(250);
        ActivityRules.IntervalMs(false).Should().Be(500);
    }

    [Fact]
    public void 程序表_沒有表或新的根程序時重列_否則滿3秒才重列()
    {
        ActivityRules.RefreshProcessTable(hasTable: false, lastMs: 0, nowMs: 10, newRoot: false).Should().BeTrue("還沒有表");
        ActivityRules.RefreshProcessTable(true, 1_000, 1_000 + 250, newRoot: true).Should().BeTrue("剛開始執行的對話要馬上找得到子孫");
        ActivityRules.RefreshProcessTable(true, 1_000, 1_000 + 2_999, false).Should().BeFalse("沿用上次的表");
        ActivityRules.RefreshProcessTable(true, 1_000, 1_000 + ActivityRules.ProcessTableRefreshMs, false).Should().BeTrue("滿 3 秒");
    }

    [Fact]
    public void 程序樹增量_還沒取基準時一律0_之後同一個程序算差_新出現的整個算進來()
    {
        var empty = new Dictionary<ProcessStamp, ulong>();
        var first = new Dictionary<ProcessStamp, ulong> { [P(1)] = 5_000_000, [P(2)] = 300 };
        ActivityRules.TreeDelta(empty, first, primed: false).Should().Be(0, "剛開始量：程序啟動以來的量不當成一陣暴衝");

        var second = new Dictionary<ProcessStamp, ulong> { [P(1)] = 5_000_400, [P(2)] = 300, [P(3)] = 2_000 };
        ActivityRules.TreeDelta(first, second, primed: true).Should().Be(400 + 0 + 2_000, "根程序多 400、工具沒變、新的工具整個算");

        var reused = new Dictionary<ProcessStamp, ulong> { [P(1)] = 5_000_400, [P(2, start: 999)] = 50 };
        ActivityRules.TreeDelta(second, reused, primed: true).Should().Be(50, "PID 2 被別的程序重用（啟動時間不同）：當成新程序");

        var shrunk = new Dictionary<ProcessStamp, ulong> { [P(1)] = 10 };
        ActivityRules.TreeDelta(second, shrunk, primed: true).Should().Be(0, "計數器變小不算負的");
    }

    [Fact]
    public void 計數器增量_沒有上一個值或變小時0()
    {
        ActivityRules.CounterDelta((long?)null, 500).Should().Be(0);
        ActivityRules.CounterDelta(100L, 500).Should().Be(400);
        ActivityRules.CounterDelta(600L, 500).Should().Be(0, "換了程序（計數器重新開始）");
        ActivityRules.CounterDelta((ulong?)null, 9UL).Should().Be(0);
        ActivityRules.CounterDelta(10UL, 4_010UL).Should().Be(4_000);
        ActivityRules.CounterDelta(10UL, 3UL).Should().Be(0, "網卡換了或計數器重設");
        ActivityRules.CounterDelta(0UL, ulong.MaxValue).Should().Be(long.MaxValue, "不溢位");
    }

    [Fact]
    public void 硬碟_程序樹的增量扣掉管線_不小於0()
    {
        ActivityRules.DiskDelta(5_000, 1_200).Should().Be(3_800);
        ActivityRules.DiskDelta(800, 1_200).Should().Be(0, "那段時間只有管線（模型串流）");
        ActivityRules.DiskDelta(0, 0).Should().Be(0);
    }

    [Fact]
    public void 訊息_只放有動靜的對話_網路收送都是0不放_什麼都沒有時不送()
    {
        ActivityRules.Render([("c1", 0, 0, null, null)], (0, 0), 250).Should().BeNull();
        ActivityRules.Render([], null, 250).Should().BeNull();

        var m = ActivityRules.Render([("c1", 500, 0, null, null), ("c2", 0, 0, null, null), ("c3", 0, 4_096, null, null)], null, 251)!;
        m.Value<string>("type").Should().Be("activity");
        m.Value<int>("ms").Should().Be(251);
        m["net"]!.Type.Should().Be(JTokenType.Null);
        var items = (JArray)m["conversations"]!;
        items.Select(i => i.Value<string>("id")).Should().Equal("c1", "c3");
        items[0]!.Value<long>("data").Should().Be(500);
        items[1]!.Value<long>("disk").Should().Be(4_096);
        items[0]!["cpu"].Should().BeNull("沒量程序樹的對話不帶 cpu");
        items[0]!["mem"].Should().BeNull("也不帶 mem");

        var netOnly = ActivityRules.Render([("c1", 0, 0, null, null)], (2_048, 0), 0)!;
        ((JArray)netOnly["conversations"]!).Should().BeEmpty();
        netOnly["net"]!.Value<long>("rx").Should().Be(2_048);
        netOnly.Value<int>("ms").Should().Be(1, "至少 1 毫秒（網頁拿來除）");
    }

    [Fact]
    public void 訊息_量了程序樹的對話一律放_CPU是0也放_波形要畫平的()
    {
        var m = ActivityRules.Render([("c1", 0, 0, 0.0, 1_000), ("c2", 0, 0, null, null), ("c3", 10, 0, 12.5, 2_000), ("c4", 0, 0, null, 3_000)], null, 250)!;
        var items = (JArray)m["conversations"]!;
        items.Select(i => i.Value<string>("id")).Should().Equal("c1", "c3", "c4");
        items[0]!.Value<double>("cpu").Should().Be(0);
        items[1]!.Value<double>("cpu").Should().Be(12.5);
        items[1]!.Value<long>("mem").Should().Be(2_000);
        items[2]!["cpu"].Should().BeNull("取基準那次只有記憶體");
        items[2]!.Value<long>("mem").Should().Be(3_000);
    }

    [Fact]
    public void CPU時間增量_同一個程序算差_這段時間才啟動的整個算_更早啟動的依比例攤()
    {
        const long at = 1_000_000, now = at + 2_500_000; // 這段時間 0.25 秒（100 奈秒為單位）
        var empty = new Dictionary<ProcessStamp, ulong>();
        var first = new Dictionary<ProcessStamp, ulong> { [P(1, start: 0)] = 9_000_000 };
        ActivityRules.TreeCpuDelta(empty, first, primed: false, at, now).Should().Be(0, "剛開始量");

        var second = new Dictionary<ProcessStamp, ulong>
        {
            [P(1, start: 0)] = 9_500_000,                 // 根程序多用了 0.05 秒
            [P(2, start: at + 1_000_000)] = 300_000,      // 這段時間才啟動：整個算
            [P(3, start: now - 10_000_000)] = 8_000_000,  // 1 秒前就啟動、晚了才看到（程序表 3 秒才重列）：只算這 0.25 秒佔的 1/4
        };
        ActivityRules.TreeCpuDelta(first, second, primed: true, at, now).Should().Be(500_000 + 300_000 + 2_000_000);

        var shrunk = new Dictionary<ProcessStamp, ulong> { [P(1, start: 0)] = 10 };
        ActivityRules.TreeCpuDelta(first, shrunk, primed: true, at, now).Should().Be(0, "計數器變小不算負的");
    }

    [Fact]
    public void CPU使用率_以整台電腦為分母_0到100_取一位小數()
    {
        // 0.25 秒裡用掉 0.25 秒的 CPU＝一顆核心滿載；8 顆邏輯處理器的電腦是 12.5%
        ActivityRules.CpuPercent(2_500_000, 2_500_000, 8).Should().Be(12.5);
        ActivityRules.CpuPercent(2_500_000 * 8, 2_500_000, 8).Should().Be(100);
        ActivityRules.CpuPercent(2_500_000 * 20, 2_500_000, 8).Should().Be(100, "不超過 100（量測誤差）");
        ActivityRules.CpuPercent(1_234, 2_500_000, 8).Should().Be(0.0, "0.006% 取一位小數是 0");
        ActivityRules.CpuPercent(0, 2_500_000, 8).Should().Be(0);
        ActivityRules.CpuPercent(100, 0, 8).Should().Be(0, "經過時間不合理");
        ActivityRules.CpuPercent(100, 2_500_000, 0).Should().Be(0);
    }
}
