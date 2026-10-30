using Myriagent.Security;

namespace Myriagent.Tests.Protocol.Security;

/// <summary>
/// 以 Hello 的 serverTime 加單調時鐘推出伺服器時間的上下界（設計 14.11 D4、docs/sync-protocol.md 14.4）：
/// 下界從收到回應算、上界從送出 Hello 算；往返超過 30 秒不可用；時鐘倒退、頻率不合理、溢位一律不可用（不丟例外）。
/// </summary>
public sealed class ServerClockRulesTests
{
    private const long ServerTime = 1_791_580_000_000;

    /// <summary>每毫秒一個刻度，讀起來就是毫秒</summary>
    private const long Ms = 1_000;

    [Fact]
    public void 下界從收到回應算_上界從送出Hello算()
    {
        var clock = ServerClockRules.HelloClock(ServerTime, sentTimestamp: 10_000, receivedTimestamp: 10_200, Ms);

        clock.Should().Be(new ServerClock(ServerTime, 10_000, 10_200, Ms));
        ServerClockRules.RttMs(clock).Should().Be(200);
        ServerClockRules.Bounds(clock, 10_200).Should().Be(new ServerTimeBounds(ServerTime, ServerTime + 200));
        ServerClockRules.Bounds(clock, 15_200).Should().Be(new ServerTimeBounds(ServerTime + 5_000, ServerTime + 5_200));
    }

    [Fact]
    public void 毫秒以外的頻率_下界捨去上界進位()
    {
        const long frequency = 10_000_000; // Stopwatch 在 Windows 的常見頻率
        var clock = ServerClockRules.HelloClock(ServerTime, 0, 15_000, frequency); // 往返 1.5 毫秒

        ServerClockRules.RttMs(clock).Should().Be(2, "無條件進位");
        ServerClockRules.Bounds(clock, 15_000 + 9_999).Should().Be(new ServerTimeBounds(ServerTime + 0, ServerTime + 3), "下界 0.9999 毫秒捨去、上界 2.4999 毫秒進位");
        ServerClockRules.Bounds(clock, 15_000 + 10_000).Should().Be(new ServerTimeBounds(ServerTime + 1, ServerTime + 3));
        ServerClockRules.Bounds(clock, 20_000).Should().Be(new ServerTimeBounds(ServerTime + 0, ServerTime + 2), "上界剛好 2 毫秒不多進");
    }

    [Fact]
    public void 往返剛好30秒可用_多一毫秒不可用()
    {
        ServerClockRules.MaxRttMs.Should().Be(30_000);
        var clock = ServerClockRules.HelloClock(ServerTime, 0, 30_000, Ms);

        clock.Should().NotBeNull();
        ServerClockRules.Bounds(clock, 30_000).Should().Be(new ServerTimeBounds(ServerTime, ServerTime + 30_000));
        ServerClockRules.HelloClock(ServerTime, 0, 30_001, Ms).Should().BeNull();
        ServerClockRules.HelloClock(ServerTime, 0, 300_000_001, 10_000_000).Should().BeNull("30.0000001 秒進位成 30,001 毫秒");
    }

    [Fact]
    public void 不可用_收到早於送出_頻率不是正數_現在早於收到_沒有時鐘()
    {
        ServerClockRules.HelloClock(ServerTime, 100, 99, Ms).Should().BeNull();
        ServerClockRules.HelloClock(ServerTime, 0, 1, 0).Should().BeNull();
        ServerClockRules.HelloClock(ServerTime, 0, 1, -1).Should().BeNull();

        var clock = ServerClockRules.HelloClock(ServerTime, 0, 100, Ms);
        ServerClockRules.Bounds(clock, 99).Should().BeNull("單調時鐘不會倒退；倒退就當成不可用");
        ServerClockRules.Bounds(null, 100).Should().BeNull();

        // 直接建出來的不合格時鐘（沒經過 HelloClock）也不可用
        ServerClockRules.Bounds(new ServerClock(ServerTime, 0, 30_001, Ms), 40_000).Should().BeNull();
        ServerClockRules.Bounds(new ServerClock(ServerTime, 0, 100, 0), 100).Should().BeNull();
        ServerClockRules.RttMs(new ServerClock(ServerTime, 5, 4, Ms)).Should().BeNull();
    }

    [Fact]
    public void 極端值不溢位_算不出來就不可用()
    {
        var clock = ServerClockRules.HelloClock(long.MaxValue - 10, long.MinValue, long.MinValue + 5, Ms);
        clock.Should().NotBeNull();
        ServerClockRules.Bounds(clock, long.MinValue + 5).Should().Be(new ServerTimeBounds(long.MaxValue - 10, long.MaxValue - 5));
        ServerClockRules.Bounds(clock, long.MinValue + 100).Should().BeNull("上界超出 long");
        ServerClockRules.Bounds(clock, long.MaxValue).Should().BeNull();

        var slow = ServerClockRules.HelloClock(0, 0, 1, 1); // 每秒一個刻度：往返 1 秒
        ServerClockRules.Bounds(slow, long.MaxValue).Should().BeNull("刻度乘 1000 超出 long");
        ServerClockRules.HelloClock(0, long.MinValue, long.MaxValue, Ms).Should().BeNull("往返太長");
    }
}
