using MyriagentHub.Realtime;

namespace MyriagentHub.Tests.Realtime;

/// <summary>瀏覽器連線的速率、連線上限、溢出與推送節流規則（設計 8.5、14.6；計畫 D7、D9、D10）</summary>
public sealed class BrowserLimitRulesTests
{
    private const long T0 = 1_791_580_000_000;

    [Fact]
    public void TokenBucket突發50後每秒補20()
    {
        var bucket = new TokenBucket(20, 50, T0);
        for (var i = 0; i < 50; i++)
            bucket.TryTake(T0).Should().BeTrue($"突發第 {i + 1} 次");
        bucket.TryTake(T0).Should().BeFalse("第 51 次");

        for (var i = 0; i < 20; i++)
            bucket.TryTake(T0 + 1000).Should().BeTrue($"一秒後第 {i + 1} 次");
        bucket.TryTake(T0 + 1000).Should().BeFalse("一秒只補 20");

        bucket.TryTake(T0 + 1050).Should().BeTrue("50 ms 補 1 個");
        bucket.TryTake(T0 + 1050).Should().BeFalse();

        // 閒置很久也最多補到 50
        var count = 0;
        while (bucket.TryTake(T0 + 3_600_000))
            count++;
        count.Should().Be(50);
    }

    [Fact]
    public void TokenBucket時間倒退不補也不丟例外()
    {
        var bucket = new TokenBucket(20, 50, T0);
        for (var i = 0; i < 50; i++)
            bucket.TryTake(T0);
        bucket.Invoking(b => b.TryTake(T0 - 60_000)).Should().NotThrow();
        bucket.TryTake(T0 - 60_000).Should().BeFalse("倒退不補");
        bucket.TryTake(T0 - 59_000).Should().BeTrue("以倒退後的時間當起點，一秒後照常補");
    }

    [Fact]
    public void TokenBucket補滿才算滿()
    {
        var bucket = new TokenBucket(20, 50, T0);
        bucket.IsFull(T0).Should().BeTrue("新建的桶是滿的");
        bucket.TryTake(T0).Should().BeTrue();
        bucket.IsFull(T0).Should().BeFalse("用掉一個");
        bucket.IsFull(T0 + 49).Should().BeFalse("50 ms 才補 1 個");
        bucket.IsFull(T0 + 50).Should().BeTrue();
        bucket.IsFull(T0 - 60_000).Should().BeFalse("時間倒退當成沒有經過");

        for (var i = 0; i < 49; i++)
            bucket.TryTake(T0);
        bucket.IsFull(T0 + 2_499).Should().BeFalse("50 個要 2.5 秒才補滿");
        bucket.IsFull(T0 + 2_500).Should().BeTrue();
        bucket.TryTake(T0 + 2_500).Should().BeTrue("IsFull 不改狀態：照常補、照常拿");
    }

    /// <summary>web-protocol 第六節第 11 點寫進契約的上限（主機測試為了跑得快會覆寫成小的值，預設值在這裡固定）</summary>
    [Fact]
    public void 上限的預設值照契約()
    {
        var options = new MyriagentHub.Hosting.HubOptions();
        options.Browser.MaxConnections.Should().Be(32, "web-protocol 第六節第 11 點：全部最多 32 條");
        options.Browser.MaxConnectionsPerSession.Should().Be(8, "web-protocol 第六節第 11 點：每個工作階段最多 8 條");
        options.RateLimits.BrowserHubAnonymousPerMinute.Should().Be(30, "web-protocol 第六節第 11 點：未登入的 negotiate 每個 IP 每分鐘 30 次");
        options.RateLimits.BrowserHubPerSessionPerMinute.Should().Be(60, "web-protocol 第六節第 11 點：已登入的每個工作階段每分鐘 60 次");
        options.RateLimits.BrowserCmdBurst.Should().Be(50, "web-protocol 第六節第 11 點：Cmd 與 ClientState 突發 50");
        options.RateLimits.BrowserCmdPerSecond.Should().Be(20, "web-protocol 第六節第 11 點：每秒 20 次");
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(7, 20, true)]
    [InlineData(8, 20, false)]
    [InlineData(0, 31, true)]
    [InlineData(0, 32, false)]
    [InlineData(8, 32, false)]
    public void 連線上限每個工作階段8全部32(int session, int total, bool expected) =>
        BrowserLimitRules.CanAdmit(session, total, 8, 32).Should().Be(expected);

    [Fact]
    public void 六十秒內第三次溢出才中斷()
    {
        BrowserLimitRules.OnOverflow([], T0).Should().Be(OverflowAction.Resync, "第 1 次");
        BrowserLimitRules.OnOverflow([T0 - 30_000], T0).Should().Be(OverflowAction.Resync, "第 2 次");
        BrowserLimitRules.OnOverflow([T0 - 50_000, T0 - 10_000], T0).Should().Be(OverflowAction.Abort, "60 秒內第 3 次");
    }

    [Fact]
    public void 超過六十秒的溢出不算()
    {
        BrowserLimitRules.OnOverflow([T0 - 60_000, T0 - 10_000], T0).Should().Be(OverflowAction.Resync, "剛好 60 秒前的不算");
        BrowserLimitRules.OnOverflow([T0 - 120_000, T0 - 90_000, T0 - 61_000], T0).Should().Be(OverflowAction.Resync);
        BrowserLimitRules.OnOverflow([T0 - 120_000, T0 - 59_999, T0 - 1], T0).Should().Be(OverflowAction.Abort);
    }

    [Fact]
    public void ReadDue()
    {
        var push = TimeSpan.FromMilliseconds(500);
        var refresh = TimeSpan.FromSeconds(15);

        // 沒有快照：一律讀
        BrowserPushRules.ReadDue(null, dirty: false, hasSnapshot: false, push, refresh).Should().BeTrue();
        BrowserPushRules.ReadDue(TimeSpan.FromMilliseconds(10), dirty: false, hasSnapshot: false, push, refresh).Should().BeTrue();

        // 節流中：有髒旗標也不讀
        BrowserPushRules.ReadDue(TimeSpan.FromMilliseconds(499), dirty: true, hasSnapshot: true, push, refresh).Should().BeFalse();

        // 有髒旗標、過了節流：讀；沒有髒旗標：不讀
        BrowserPushRules.ReadDue(TimeSpan.FromMilliseconds(500), dirty: true, hasSnapshot: true, push, refresh).Should().BeTrue();
        BrowserPushRules.ReadDue(TimeSpan.FromSeconds(14.9), dirty: false, hasSnapshot: true, push, refresh).Should().BeFalse();

        // 保底重讀
        BrowserPushRules.ReadDue(TimeSpan.FromSeconds(15), dirty: false, hasSnapshot: true, push, refresh).Should().BeTrue();
    }
}
