using Myriagent.Diagnostics;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Protocol.Diagnostics;

/// <summary>重複事件的記錄節流（2026-10-23：一張被擋的圖兩天記了上千行、卻看不出是哪個檔）</summary>
public class LogThrottleTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    [Fact]
    public void 同一個鍵在窗口內只記第一次_過了窗口帶回略過的次數()
    {
        var clock = new ManualTimeProvider();
        var throttle = new LogThrottle(Window, clock);

        throttle.ShouldLog("a", out var first).Should().BeTrue();
        first.Should().Be(0);
        for (var i = 0; i < 5; i++)
        {
            throttle.ShouldLog("a", out var skipped).Should().BeFalse();
            skipped.Should().Be(0);
        }

        clock.Advance(Window - TimeSpan.FromSeconds(1));
        throttle.ShouldLog("a", out _).Should().BeFalse("還在窗口內");

        clock.Advance(TimeSpan.FromSeconds(1));
        throttle.ShouldLog("a", out var suppressed).Should().BeTrue();
        suppressed.Should().Be(6);

        throttle.ShouldLog("a", out _).Should().BeFalse("窗口從這次記錄重新算");
        clock.Advance(Window);
        throttle.ShouldLog("a", out suppressed).Should().BeTrue();
        suppressed.Should().Be(1, "上一次記錄之後只略過一次");
    }

    [Fact]
    public void 不同的鍵各自計算()
    {
        var throttle = new LogThrottle(Window, new ManualTimeProvider());
        throttle.ShouldLog("a", out _).Should().BeTrue();
        throttle.ShouldLog("b", out _).Should().BeTrue();
        throttle.ShouldLog("A", out _).Should().BeTrue("鍵分大小寫");
        throttle.ShouldLog("a", out _).Should().BeFalse();
    }

    [Fact]
    public void 鍵滿了先清過期的_還是滿就整個清空()
    {
        var clock = new ManualTimeProvider();
        var throttle = new LogThrottle(Window, clock, maxKeys: 3);
        throttle.ShouldLog("old", out _).Should().BeTrue();
        clock.Advance(Window);
        throttle.ShouldLog("b", out _).Should().BeTrue();
        throttle.ShouldLog("c", out _).Should().BeTrue();

        throttle.ShouldLog("d", out _).Should().BeTrue();
        throttle.Count.Should().Be(3, "過期的 old 被清掉");
        throttle.ShouldLog("b", out _).Should().BeFalse("沒過期的留著");

        throttle.ShouldLog("e", out _).Should().BeTrue();
        throttle.Count.Should().Be(1, "都沒過期時整個清空，只留新的");
        throttle.ShouldLog("b", out _).Should().BeTrue("清空之後寧可多記");
    }

    [Fact]
    public void SuppressedNote_沒有略過是空字串()
    {
        LogThrottle.SuppressedNote(0).Should().BeEmpty();
        LogThrottle.SuppressedNote(3).Should().Be("（上次記錄之後另有 3 次相同）");
    }

    [Fact]
    public void 參數不合格丟例外()
    {
        var clock = new ManualTimeProvider();
        FluentActions.Invoking(() => new LogThrottle(TimeSpan.Zero, clock)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new LogThrottle(Window, clock, maxKeys: 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new LogThrottle(Window, null!)).Should().Throw<ArgumentNullException>();
    }
}
