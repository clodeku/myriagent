using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>閒置時釋放、活動時預熱 DLP 樣式的判斷（WP4.9 步 5）：邊界與每一個阻擋條件</summary>
public sealed class DlpCacheTrimRulesTests
{
    private static readonly TimeSpan Quiet = DlpCacheTrimRules.QuietPeriod;

    [Fact]
    public void 靜置期是20秒()
        => Quiet.Should().Be(TimeSpan.FromSeconds(20));

    [Fact]
    public void ShouldTrim_剛好靜置期為真_少1tick為假()
    {
        DlpCacheTrimRules.ShouldTrim(Quiet, reconcileIdle: true, runningTurns: 0, watchedConversations: 0, compiled: true).Should().BeTrue();
        DlpCacheTrimRules.ShouldTrim(Quiet - TimeSpan.FromTicks(1), reconcileIdle: true, runningTurns: 0, watchedConversations: 0, compiled: true).Should().BeFalse();
        DlpCacheTrimRules.ShouldTrim(TimeSpan.FromHours(1), reconcileIdle: true, runningTurns: 0, watchedConversations: 0, compiled: true).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, 0, 0, true, "重整進行中或還有排著的")]
    [InlineData(true, 1, 0, true, "有執行中的回合")]
    [InlineData(true, 0, 1, true, "有人在看")]
    [InlineData(true, 0, 0, false, "已經釋放過")]
    public void ShouldTrim_任一阻擋條件成立就不釋放(bool reconcileIdle, int runningTurns, int watched, bool compiled, string because)
        => DlpCacheTrimRules.ShouldTrim(Quiet * 2, reconcileIdle, runningTurns, watched, compiled).Should().BeFalse(because);

    [Theory]
    [InlineData(1, 0, false, true)]
    [InlineData(0, 1, false, true)]
    [InlineData(3, 2, false, true)]
    [InlineData(1, 1, true, false)]
    [InlineData(0, 0, false, false)]
    [InlineData(0, 0, true, false)]
    public void ShouldPrewarm_有回合或有人看而且沒編譯才為真(int runningTurns, int watched, bool compiled, bool expected)
        => DlpCacheTrimRules.ShouldPrewarm(runningTurns, watched, compiled).Should().Be(expected);

    [Fact]
    public void RecheckDelay_計時器早到半毫秒_只補剩下的加一個刻度()
    {
        // 系統計時器比單調時鐘早到（合併前審查 THR-3）：不再整整等 20 秒，釋放不會晚到約 40 秒
        var delay = DlpCacheTrimRules.RecheckDelay(Quiet - TimeSpan.FromMilliseconds(0.5), reconcileIdle: true, runningTurns: 0, watchedConversations: 0);
        delay.Should().Be(TimeSpan.FromMilliseconds(16.5));
        DlpCacheTrimRules.RecheckSlack.Should().Be(TimeSpan.FromMilliseconds(16));
        DlpCacheTrimRules.ShouldTrim(Quiet - TimeSpan.FromMilliseconds(0.5) + delay, reconcileIdle: true, runningTurns: 0, watchedConversations: 0, compiled: true)
            .Should().BeTrue("補的時間到了就滿靜置期");
    }

    [Theory]
    [InlineData(false, 0, 0, "重整中")]
    [InlineData(true, 1, 0, "有執行中的回合")]
    [InlineData(true, 0, 1, "有人在看")]
    public void RecheckDelay_有阻擋條件時等一個靜置期(bool reconcileIdle, int runningTurns, int watched, string because)
        => DlpCacheTrimRules.RecheckDelay(Quiet - TimeSpan.FromMilliseconds(0.5), reconcileIdle, runningTurns, watched).Should().Be(Quiet, because);

    [Fact]
    public void RecheckDelay_已經滿靜置期_等一個靜置期()
        => DlpCacheTrimRules.RecheckDelay(Quiet, reconcileIdle: true, runningTurns: 0, watchedConversations: 0).Should().Be(Quiet);

    // ── 步 6b：啟動之後的那一次積極回收、閒置期間的再壓縮 ──

    private static readonly TimeSpan Hour = DlpCacheTrimRules.RecompactInterval;

    private const long Growth = DlpCacheTrimRules.RecompactGrowthBytes;

    [Fact]
    public void 再壓縮的門檻是16MB_最短間隔一小時_滿了之後每10分鐘看一次()
    {
        Growth.Should().Be(16L * 1024 * 1024);
        Hour.Should().Be(TimeSpan.FromHours(1));
        DlpCacheTrimRules.RecompactCheckPeriod.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void ShouldCompact_啟動之後還沒壓縮過_靜置滿就做_少1tick不做()
    {
        // 已配對但連不上：樣式從沒建過，ShouldTrim 永遠不成立；只要閒置就做一次（已提交的空間長了多少不看）
        DlpCacheTrimRules.ShouldCompact(Quiet, reconcileIdle: true, runningTurns: 0, watchedConversations: 0, sinceCompaction: null, committedGrowthBytes: 0).Should().BeTrue();
        DlpCacheTrimRules.ShouldCompact(Quiet - TimeSpan.FromTicks(1), reconcileIdle: true, runningTurns: 0, watchedConversations: 0, sinceCompaction: null, committedGrowthBytes: 0).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, 0, 0, "重整進行中或還有排著的")]
    [InlineData(true, 1, 0, "有執行中的回合")]
    [InlineData(true, 0, 1, "有人在看")]
    public void ShouldCompact_任一阻擋條件成立就不做_啟動那一次與再壓縮都一樣(bool reconcileIdle, int runningTurns, int watched, string because)
    {
        DlpCacheTrimRules.ShouldCompact(Quiet * 2, reconcileIdle, runningTurns, watched, sinceCompaction: null, committedGrowthBytes: 0).Should().BeFalse(because);
        DlpCacheTrimRules.ShouldCompact(Quiet * 2, reconcileIdle, runningTurns, watched, sinceCompaction: Hour * 3, committedGrowthBytes: Growth * 4).Should().BeFalse(because);
    }

    [Fact]
    public void ShouldCompact_再壓縮_長滿門檻而且滿一小時才做()
    {
        bool Compact(TimeSpan since, long growth) => DlpCacheTrimRules.ShouldCompact(Quiet, reconcileIdle: true, runningTurns: 0, watchedConversations: 0, since, growth);

        Compact(Hour, Growth).Should().BeTrue("剛好一小時、剛好 16 MB");
        Compact(Hour, Growth - 1).Should().BeFalse("少 1 位元組");
        Compact(Hour - TimeSpan.FromTicks(1), Growth * 4).Should().BeFalse("長得再多，一小時內最多一次");
        Compact(Hour * 5, 0).Should().BeFalse("沒長就不做");
        Compact(Hour * 5, -Growth).Should().BeFalse("比上一次還少");
    }

    [Theory]
    [InlineData(false, 0, 0, "重整中")]
    [InlineData(true, 1, 0, "有執行中的回合")]
    [InlineData(true, 0, 1, "有人在看")]
    public void NextCheck_有阻擋條件時等一個靜置期(bool reconcileIdle, int runningTurns, int watched, string because)
        => DlpCacheTrimRules.NextCheck(Quiet * 2, reconcileIdle, runningTurns, watched, sinceCompaction: TimeSpan.FromMinutes(5)).Should().Be(Quiet, because);

    [Fact]
    public void NextCheck_只差靜置時間_只補剩下的加一個刻度()
        => DlpCacheTrimRules.NextCheck(Quiet - TimeSpan.FromMilliseconds(0.5), reconcileIdle: true, runningTurns: 0, watchedConversations: 0, sinceCompaction: TimeSpan.FromMinutes(5))
            .Should().Be(TimeSpan.FromMilliseconds(16.5));

    [Fact]
    public void NextCheck_閒置但還不到一小時_等到滿一小時加一個刻度()
    {
        DlpCacheTrimRules.NextCheck(Quiet, reconcileIdle: true, runningTurns: 0, watchedConversations: 0, sinceCompaction: TimeSpan.FromMinutes(15))
            .Should().Be(TimeSpan.FromMinutes(45) + DlpCacheTrimRules.RecheckSlack);

        // 一小時的計時器比單調時鐘早到半毫秒：只再等 16.5 ms，不是白看一次再等 10 分鐘
        var early = Hour - TimeSpan.FromMilliseconds(0.5);
        var delay = DlpCacheTrimRules.NextCheck(Quiet * 100, reconcileIdle: true, runningTurns: 0, watchedConversations: 0, sinceCompaction: early);
        delay.Should().Be(TimeSpan.FromMilliseconds(16.5));
        DlpCacheTrimRules.ShouldCompact(Quiet * 100, reconcileIdle: true, runningTurns: 0, watchedConversations: 0, early + delay, Growth).Should().BeTrue("補的時間到了就滿一小時");
    }

    [Fact]
    public void NextCheck_滿一小時但還沒長到門檻_每10分鐘看一次()
    {
        DlpCacheTrimRules.NextCheck(Quiet, reconcileIdle: true, runningTurns: 0, watchedConversations: 0, sinceCompaction: Hour).Should().Be(DlpCacheTrimRules.RecompactCheckPeriod);
        DlpCacheTrimRules.NextCheck(Quiet, reconcileIdle: true, runningTurns: 0, watchedConversations: 0, sinceCompaction: Hour * 7).Should().Be(DlpCacheTrimRules.RecompactCheckPeriod);
    }

    [Fact]
    public void NextCheck_閒置而且還沒壓縮過_等一個靜置期()
        => DlpCacheTrimRules.NextCheck(Quiet, reconcileIdle: true, runningTurns: 0, watchedConversations: 0, sinceCompaction: null).Should().Be(Quiet);

    [Theory]
    [InlineData(10, 25, 25, "積極回收之後的配置：上一次 GC 的已提交看不到")]
    [InlineData(44, 30, 44, "一般的 GC 留著的空間被後來的配置重用")]
    [InlineData(12, 12, 12, "剛做完積極回收")]
    public void CommittedEstimate_上一次GC的已提交與目前的配置取大(long lastGcMb, long totalMb, long expectedMb, string because)
        => DlpCacheTrimRules.CommittedEstimate(lastGcMb << 20, totalMb << 20).Should().Be(expectedMb << 20, because);
}
