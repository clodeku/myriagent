using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 重整與回補的排程（WP4.3 計畫 D24；答 3：不限速，只讓新的事件優先）：保留上限只是磁碟保護，不是節流
/// </summary>
public sealed class ReconcileQueueRulesTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static ReconcileCandidate C(string id, ReconcileReason reason, long updatedAt) => new(id, reason, updatedAt, 100);

    [Fact]
    public void 常數逐字釘住()
    {
        ReconcileQueueRules.RoundMaxRows.Should().Be(500);
        ReconcileQueueRules.RoundMaxBytes.Should().Be(2L * 1024 * 1024);
        ReconcileQueueRules.RetainedMaxRows.Should().Be(1_000_000);
        ReconcileQueueRules.RetainedMaxBytes.Should().Be(4 * GiB);
        ReconcileQueueRules.MinFreeDiskBytes.Should().Be(2 * GiB);
    }

    [Fact]
    public void 插隊與還原優先_其次重整_再來回補()
    {
        var ordered = ReconcileQueueRules.Order(
        [
            C("backfill-new", ReconcileReason.Backfill, 900),
            C("changed", ReconcileReason.Changed, 100),
            C("restore", ReconcileReason.Restore, 50),
            C("redo", ReconcileReason.Redo, 200),
            C("requested-old", ReconcileReason.Requested, 1),
            C("backfill-old", ReconcileReason.Backfill, 10),
        ]);

        ordered.Select(c => c.WireId).Should().Equal("requested-old", "restore", "redo", "changed", "backfill-new", "backfill-old");
    }

    [Fact]
    public void 同一類新到舊_平手比id()
    {
        var ordered = ReconcileQueueRules.Order(
        [
            C("b", ReconcileReason.Backfill, 5),
            C("c", ReconcileReason.Backfill, 9),
            C("a", ReconcileReason.Backfill, 5),
            C("B", ReconcileReason.Backfill, 5),
            C("x", ReconcileReason.Redo, 1),
            C("y", ReconcileReason.Changed, 2),
        ]);

        ordered.Select(c => c.WireId).Should().Equal(new[] { "y", "x", "c", "B", "a", "b" }, "Redo 與 Changed 同一類；平手依序數比 id（大寫在前）");
    }

    [Fact]
    public void 同一個對話只留排最前面的一筆()
    {
        var ordered = ReconcileQueueRules.Order(
        [
            C("a", ReconcileReason.Backfill, 1),
            C("a", ReconcileReason.Requested, 1),
            C("b", ReconcileReason.Redo, 3),
            C("b", ReconcileReason.Redo, 2),
        ]);

        ordered.Should().Equal(C("a", ReconcileReason.Requested, 1), C("b", ReconcileReason.Redo, 3));
        ReconcileQueueRules.Order([]).Should().BeEmpty();
    }

    [Fact]
    public void 一般事件待送時不開始()
    {
        ReconcileQueueRules.MayStartRound(0, 0, 0, long.MaxValue).Should().BeTrue();
        ReconcileQueueRules.MayStartRound(1, 0, 0, long.MaxValue).Should().BeFalse("新的事件優先");
        ReconcileQueueRules.MayStartRound(500, 0, 0, long.MaxValue).Should().BeFalse();
        ReconcileQueueRules.MayStartRound(0, ReconcileQueueRules.RetainedMaxRows + 1, 0, long.MaxValue).Should().BeFalse("磁碟保護暫停");
        ReconcileQueueRules.MayStartRound(0, 0, 4 * GiB + 1, long.MaxValue).Should().BeFalse();
        ReconcileQueueRules.MayStartRound(0, 0, 0, 2 * GiB - 1).Should().BeFalse();
    }

    [Fact]
    public void Hold_列數_位元組_磁碟各兩個邊界點()
    {
        ReconcileQueueRules.Hold(ReconcileQueueRules.RetainedMaxRows, 0, long.MaxValue).Should().Be(BackfillHold.None);
        ReconcileQueueRules.Hold(ReconcileQueueRules.RetainedMaxRows + 1, 0, long.MaxValue).Should().Be(BackfillHold.RetainedRows);

        ReconcileQueueRules.Hold(0, 4 * GiB, long.MaxValue).Should().Be(BackfillHold.None);
        ReconcileQueueRules.Hold(0, 4 * GiB + 1, long.MaxValue).Should().Be(BackfillHold.RetainedBytes);

        ReconcileQueueRules.Hold(0, 0, 2 * GiB).Should().Be(BackfillHold.None);
        ReconcileQueueRules.Hold(0, 0, 2 * GiB - 1).Should().Be(BackfillHold.LowDisk);

        ReconcileQueueRules.Hold(ReconcileQueueRules.RetainedMaxRows + 1, 4 * GiB + 1, 0).Should().Be(BackfillHold.RetainedRows, "依列數、位元組、磁碟的順序回報第一個");
        ReconcileQueueRules.Hold(0, 4 * GiB + 1, 0).Should().Be(BackfillHold.RetainedBytes);
    }

    [Fact]
    public void 保留上限不是節流_超過256MB照常回補()
    {
        // 原本的上限（50,000 列、256 MB）配上 24 小時保留，等於每天只能回補 256 MB：已拿掉
        ReconcileQueueRules.MayStartRound(0, 50_001, 256L * 1024 * 1024 + 1, 100 * GiB).Should().BeTrue();
        ReconcileQueueRules.MayStartRound(0, 900_000, 3 * GiB, 3 * GiB).Should().BeTrue("本機有對話執行中也不看（規則沒有這個參數）");
    }

    [Fact]
    public void 不認得的原因丟例外()
    {
        var act = () => ReconcileQueueRules.Order([C("a", (ReconcileReason)99, 1)]);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
