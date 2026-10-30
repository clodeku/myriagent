using Myriagent.Sync;
using Myriagent.SystemStatus;

namespace Myriagent.Tests.SystemStatus;

/// <summary>這台電腦的狀態來源（電腦狀態面板、stat1）：打開時立刻取第一次、之後每秒一次，關掉就停；同步引擎要的夠新就沿用，不夠新就現在取</summary>
public sealed class SystemMonitorTests
{
    [Fact]
    public async Task 打開立刻取樣_每秒一次_關掉就停_Current夠新就沿用()
    {
        var calls = 0;
        using var monitor = new SystemMonitor(() => new DeviceStats(Interlocked.Increment(ref calls), 1, 2, [], 0, 0));
        var sampled = new List<long>();
        monitor.Sampled += s =>
        {
            lock (sampled)
                sampled.Add(s.Seq);
        };
        monitor.Latest.Should().BeNull("還沒打開不取樣");

        monitor.Active = true;
        await WaitUntilAsync(() => Count(sampled) >= 1, "打開時立刻取第一次");
        monitor.Latest!.Seq.Should().Be(1);
        monitor.Current(TimeSpan.FromSeconds(5)).Seq.Should().Be(monitor.Latest.Seq, "夠新就沿用、不另外取樣");
        await WaitUntilAsync(() => Count(sampled) >= 2, "之後每秒一次", 3_000);

        monitor.Active = false;
        await Task.Delay(200);
        var stopped = Count(sampled);
        await Task.Delay(1_500);
        Count(sampled).Should().Be(stopped, "關掉之後不再取樣");

        var before = monitor.Latest!.Seq;
        monitor.Current(TimeSpan.Zero).Seq.Should().Be(before + 1, "不夠新就現在取一次");
        Count(sampled).Should().Be(stopped, "引擎要的那一次不觸發 Sampled");
    }

    private static int Count(List<long> list)
    {
        lock (list)
            return list.Count;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 2_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(because);
            await Task.Delay(20);
        }
    }
}
