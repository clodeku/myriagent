using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>回補的進度與因磁碟保護暫停的時間（WP4.3 計畫 D24；給 WP4.6 顯示）</summary>
public sealed class BackfillProgressRulesTests
{
    [Fact]
    public void 全部完成是null()
    {
        BackfillProgressRules.Progress(0, 0).Should().BeNull();
        BackfillProgressRules.Progress(-1, -5).Should().BeNull("負數當成 0");
        BackfillProgressRules.Progress(3, 1_000).Should().Be(new SyncBackfillProgress(3, 1_000));
        BackfillProgressRules.Progress(0, 10).Should().Be(new SyncBackfillProgress(0, 10), "估計的位元組還有剩：照實輸出");
    }

    [Fact]
    public void 位元組不為負()
    {
        BackfillProgressRules.Progress(2, -100).Should().Be(new SyncBackfillProgress(2, 0));
        BackfillProgressRules.Progress(-2, 100).Should().Be(new SyncBackfillProgress(0, 100));
    }

    [Fact]
    public void 沒有暫停過_維持null()
    {
        BackfillProgressRules.Advance(null, BackfillHold.None, 1_000).Should().BeNull();
    }

    [Fact]
    public void 暫停時間累計_恢復後保留總長()
    {
        var paused = BackfillProgressRules.Advance(null, BackfillHold.LowDisk, 1_000);
        paused.Should().Be(new BackfillPause(BackfillHold.LowDisk, 1_000, 0));

        BackfillProgressRules.Advance(paused, BackfillHold.LowDisk, 5_000).Should().BeSameAs(paused, "暫停中：不變");

        var resumed = BackfillProgressRules.Advance(paused, BackfillHold.None, 6_500);
        resumed.Should().Be(new BackfillPause(BackfillHold.LowDisk, null, 5_500));
        BackfillProgressRules.Advance(resumed, BackfillHold.None, 9_000).Should().BeSameAs(resumed, "沒有在暫停：總長保留");

        var again = BackfillProgressRules.Advance(resumed, BackfillHold.RetainedBytes, 10_000);
        again.Should().Be(new BackfillPause(BackfillHold.RetainedBytes, 10_000, 5_500));
        BackfillProgressRules.Advance(again, BackfillHold.None, 10_250).Should().Be(new BackfillPause(BackfillHold.RetainedBytes, null, 5_750), "第二次的長度累加進總長");

        BackfillProgressRules.Advance(new BackfillPause(BackfillHold.LowDisk, 2_000, 0), BackfillHold.None, 1_000)
            .Should().Be(new BackfillPause(BackfillHold.LowDisk, null, 0), "時間倒退時不減");
    }

    [Fact]
    public void 原因改變時不重設起點()
    {
        var paused = BackfillProgressRules.Advance(null, BackfillHold.RetainedRows, 100);

        var changed = BackfillProgressRules.Advance(paused, BackfillHold.LowDisk, 900);

        changed.Should().Be(new BackfillPause(BackfillHold.LowDisk, 100, 0));
        BackfillProgressRules.Advance(changed, BackfillHold.None, 1_100)!.TotalMs.Should().Be(1_000, "從第一次開始暫停算起");
    }
}
