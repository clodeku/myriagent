using Myriagent.Agents;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 持久與即時的時機（WP4.3 計畫 D21；設計 4.4）：離開 Running 與 Waiting 立刻持久、Running 每 10 秒一筆檢查點；
/// 即時更新只給 Running、條件成立、而且照每個對話的頻率上限
/// </summary>
public sealed class PublishTimingRulesTests
{
    private const long Now = 1_000_000;

    private static readonly AgentItemStatus[] Final =
        [AgentItemStatus.Done, AgentItemStatus.Failed, AgentItemStatus.Declined, AgentItemStatus.Cancelled];

    [Fact]
    public void 常數逐字釘住()
    {
        PublishTimingRules.WatchedFlushMs.Should().Be(50);
        PublishTimingRules.UnwatchedRunningFlushMs.Should().Be(1_000);
        PublishTimingRules.CheckpointIntervalMs.Should().Be(10_000);
        PublishTimingRules.MetaSlice.Should().Be(50);
        PublishTimingRules.ItemsPage.Should().Be(500);
    }

    [Fact]
    public void Running_9999毫秒不持久_10000毫秒持久()
    {
        const long last = 5_000;

        var before = PublishTimingRules.Decide(AgentItemStatus.Running, AgentItemStatus.Running, last, last + 9_999, false, null, 4);
        before.Should().Be(new ItemPublishDecision(false, false, last + 10_000), "還沒到檢查點，到期時再判斷");

        var at = PublishTimingRules.Decide(AgentItemStatus.Running, AgentItemStatus.Running, last, last + 10_000, false, null, 4);
        at.Should().Be(new ItemPublishDecision(true, false, null));

        PublishTimingRules.Decide(AgentItemStatus.Running, AgentItemStatus.Running, last, last + 60_000, false, null, 4)
            .Durable.Should().BeTrue("超過很久也是持久");
    }

    [Fact]
    public void Running_第一次或從別的狀態回到Running_立即持久()
    {
        PublishTimingRules.Decide(AgentItemStatus.Running, null, null, Now, false, null, 4)
            .Should().Be(new ItemPublishDecision(true, false, null), "還沒寫過持久版本");
        PublishTimingRules.Decide(AgentItemStatus.Running, AgentItemStatus.Running, null, Now, false, null, 4)
            .Durable.Should().BeTrue("沒有上一次的時間");
        PublishTimingRules.Decide(AgentItemStatus.Running, AgentItemStatus.Waiting, Now - 1, Now, false, null, 4)
            .Durable.Should().BeTrue("狀態改變");
        PublishTimingRules.Decide(AgentItemStatus.Running, AgentItemStatus.Done, Now - 1, Now, false, null, 4)
            .Durable.Should().BeTrue("狀態改變");
    }

    [Fact]
    public void 離開Running_立即持久()
    {
        foreach (var status in Final)
        {
            PublishTimingRules.Decide(status, AgentItemStatus.Running, Now - 1, Now, true, null, 4)
                .Should().Be(new ItemPublishDecision(true, false, null), $"{status}：立刻持久、不送即時（即時只給 Running）");
            PublishTimingRules.Decide(status, status, Now - 1, Now, true, Now - 1, 4)
                .Should().Be(new ItemPublishDecision(true, false, null), $"{status} 之後又改：照樣持久（內容有沒有變由摘要判斷）");
        }
    }

    [Fact]
    public void Waiting_立即持久()
    {
        PublishTimingRules.Decide(AgentItemStatus.Waiting, AgentItemStatus.Running, Now - 1, Now, true, null, 4)
            .Should().Be(new ItemPublishDecision(true, false, null), "等待回應的卡片要讓之後才連上的觀看者也看得到");
        PublishTimingRules.Decide(AgentItemStatus.Waiting, null, null, Now, true, null, 4)
            .Should().Be(new ItemPublishDecision(true, false, null));
    }

    /// <summary>
    /// 五個條件：Running、可以送即時（liveAllowed）、頻率上限 &gt;0、距上一則即時已滿間隔、檢查點到期。
    /// 持久＝不是 Running 或檢查點到期；即時＝Running、liveAllowed、上限 &gt;0、已滿間隔（與持久互不影響）；
    /// 下次判斷＝檢查點沒到期時的到期時間，與被頻率擋下時的最早時間，取早的
    /// </summary>
    [Fact]
    public void 即時_五個條件的32種組合表()
    {
        var rows = 0;
        foreach (var running in new[] { false, true })
        foreach (var liveAllowed in new[] { false, true })
        foreach (var hzPositive in new[] { false, true })
        foreach (var intervalPassed in new[] { false, true })
        foreach (var checkpointDue in new[] { false, true })
        {
            var status = running ? AgentItemStatus.Running : AgentItemStatus.Done;
            var maxHz = hzPositive ? 4 : 0;
            var lastLive = intervalPassed ? Now - 250 : Now - 100;
            var lastDurable = checkpointDue ? Now - 10_000 : Now - 5_000;

            var decision = PublishTimingRules.Decide(status, AgentItemStatus.Running, lastDurable, Now, liveAllowed, lastLive, maxHz);

            var durable = !running || checkpointDue;
            var live = running && liveAllowed && hzPositive && intervalPassed;
            long? recheck = durable ? null : Now + 5_000;
            if (running && liveAllowed && hzPositive && !intervalPassed)
                recheck = Math.Min(recheck ?? long.MaxValue, Now + 150);
            decision.Should().Be(new ItemPublishDecision(durable, live, recheck),
                $"running={running} liveAllowed={liveAllowed} hz>0={hzPositive} 已滿間隔={intervalPassed} 檢查點到期={checkpointDue}");
            rows++;
        }

        rows.Should().Be(32);
    }

    [Fact]
    public void 沒送過即時_第一則立即()
    {
        PublishTimingRules.Decide(AgentItemStatus.Running, AgentItemStatus.Running, Now - 1, Now, true, null, 4)
            .Should().Be(new ItemPublishDecision(false, true, Now - 1 + 10_000));
    }

    [Fact]
    public void Allows邊界_4Hz是250毫秒()
    {
        PublishTimingRules.LiveIntervalMs(4).Should().Be(250);

        PublishTimingRules.Decide(AgentItemStatus.Running, AgentItemStatus.Running, Now, Now, true, Now - 249, 4)
            .Should().Be(new ItemPublishDecision(false, false, Now + 1), "還差 1 毫秒；被擋下的即時更新比檢查點早到期");
        PublishTimingRules.Decide(AgentItemStatus.Running, AgentItemStatus.Running, Now, Now, true, Now - 250, 4)
            .Should().Be(new ItemPublishDecision(false, true, Now + 10_000));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(1000)]
    [InlineData(1500)]
    public void 間隔與LiveRules的Allows一致(int hz)
    {
        var interval = PublishTimingRules.LiveIntervalMs(hz);

        LiveRules.Allows(Now, Now + interval - 1, hz).Should().BeFalse();
        LiveRules.Allows(Now, Now + interval, hz).Should().BeTrue();
    }

    [Fact]
    public void 間隔_上限不是正數丟例外()
    {
        FluentActions.Invoking(() => PublishTimingRules.LiveIntervalMs(0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => PublishTimingRules.LiveIntervalMs(-1)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(true, true, 50)]
    [InlineData(true, false, 50)]
    [InlineData(false, true, 50)]
    [InlineData(false, false, 1_000)]
    public void flush延遲_被看或有要立刻持久的50毫秒_其餘1秒(bool watched, bool urgent, int expected)
        => PublishTimingRules.FlushDelayMs(watched, urgent).Should().Be(expected);
}
