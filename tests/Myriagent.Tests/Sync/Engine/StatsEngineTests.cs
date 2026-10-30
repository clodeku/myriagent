using System.Collections.Concurrent;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 各電腦即時狀態的引擎（sync-protocol 14.8）：有 port 才宣告 stat1；協商出來時交握完成就送一次，之後照伺服器的 watched 與這台的面板決定 2 秒或 10 秒；
/// 往返時間回報給 port、下一次帶上；StatsUpdate 驗過整份交出，連線斷了交一份空的、往返時間清掉；沒協商出 stat1 不送
/// </summary>
public sealed class StatsEngineTests
{
    private static readonly DeviceStats Sample = new(20, 2L << 30, 8L << 30, [new DiskStats("C:", 10L << 30, 100L << 30)], 500, 100, Cores: 4);

    private static FakeStatsPort UseStatsPort(EngineHarness h, bool serverStat)
    {
        var port = new FakeStatsPort(h.Host);
        h.HostPort = port;
        h.Server.Features = serverStat ? [DeviceFeatureRules.Stat1] : [];
        return port;
    }

    [Fact]
    public async Task 宣告stat1_交握完成就送一次_沒人在看10秒一次_有人在看2秒一次()
    {
        await using var h = new EngineHarness();
        var port = UseStatsPort(h, serverStat: true);
        await h.StartAsync();

        h.Server.Hellos.Last().Features.Should().Equal(DeviceFeatureRules.Stat1);
        await CommandEngineTests.WaitUntilAsync(() => h.Server.StatsPublishes.Count == 1, "交握完成就送");
        var first = h.Server.StatsPublishes.Single();
        first.Stats.RttMs.Should().BeNull("還沒量過");
        first.Watch.Should().BeFalse();
        await CommandEngineTests.WaitUntilAsync(() => port.Rtts.Count == 1, "回報往返時間");
        port.Rtts.Single().Should().NotBeNull();

        await h.AdvanceAsync(9);
        h.Server.StatsPublishes.Should().HaveCount(1, "沒人在看：10 秒一次");
        await h.AdvanceAsync(1);
        await CommandEngineTests.WaitUntilAsync(() => h.Server.StatsPublishes.Count == 2, "第 10 秒");
        h.Server.StatsPublishes.Last().Stats.RttMs.Should().NotBeNull("帶上上一次量到的往返時間");

        h.Server.StatsWatched = true;
        await h.AdvanceAsync(10);
        await CommandEngineTests.WaitUntilAsync(() => h.Server.StatsPublishes.Count == 3, "第 20 秒");
        await h.AdvanceAsync(2);
        await CommandEngineTests.WaitUntilAsync(() => h.Server.StatsPublishes.Count == 4, "有人在看：2 秒一次");
    }

    [Fact]
    public async Task 這台的面板打開_立刻送一次watch_true()
    {
        await using var h = new EngineHarness();
        var port = UseStatsPort(h, serverStat: true);
        await h.StartAsync();
        await CommandEngineTests.WaitUntilAsync(() => h.Server.StatsPublishes.Count == 1, "交握完成就送");
        await h.IdleAsync();

        port.Watching = true;
        h.Engine.StatsWatchChanged();
        await CommandEngineTests.WaitUntilAsync(() => h.Server.StatsPublishes.Count == 2, "面板打開");
        h.Server.StatsPublishes.Last().Watch.Should().BeTrue();
        await h.AdvanceAsync(2);
        await CommandEngineTests.WaitUntilAsync(() => h.Server.StatsPublishes.Count == 3, "自己的面板開著也 2 秒一次（延遲要新）");
    }

    [Fact]
    public async Task StatsUpdate驗過交出_格式不合格略過_斷線交一份空的()
    {
        await using var h = new EngineHarness();
        var port = UseStatsPort(h, serverStat: true);
        await h.StartAsync();
        await CommandEngineTests.WaitUntilAsync(() => h.Server.StatsPublishes.Count == 1, "交握完成就送");

        var other = SyncIds.NewDeviceId();
        h.Server.Current!.Events.OnStatsUpdate(new StatsUpdate([new DeviceStatsEntry(other, 3, 400, Sample)],
            new HubStatsEntry("HUB-PC", 1, 100, Sample, 2, 1)));
        await CommandEngineTests.WaitUntilAsync(() => port.Updates.Count == 1, "交出");
        port.Updates.Single().Devices.Single().DeviceId.Should().Be(other);
        port.Updates.Single().Hub!.Name.Should().Be("HUB-PC");

        h.Server.Current!.Events.OnStatsUpdate(new StatsUpdate([new DeviceStatsEntry("d_bad", 3, 400, Sample)]));
        await h.IdleAsync();
        port.Updates.Should().HaveCount(1, "格式不合格整則略過");

        h.Server.Current!.Drop();
        await CommandEngineTests.WaitUntilAsync(() => port.Updates.Count == 2, "斷線交一份空的");
        port.Updates.Last().Devices.Should().BeEmpty();
        port.Updates.Last().Hub.Should().BeNull();
        port.Rtts.Last().Should().BeNull("往返時間清掉");
    }

    [Fact]
    public async Task 伺服器沒有stat1_不送()
    {
        await using var h = new EngineHarness();
        var port = UseStatsPort(h, serverStat: false);
        await h.StartAsync();
        await h.AdvanceAsync(12);

        h.Server.Hellos.Last().Features.Should().Equal(DeviceFeatureRules.Stat1);
        h.Server.StatsPublishes.Should().BeEmpty();
        port.Rtts.Should().BeEmpty();
    }

    [Fact]
    public async Task 沒有port_不宣告stat1()
    {
        await using var h = new EngineHarness();
        h.Server.Features = [DeviceFeatureRules.Stat1];
        await h.StartAsync();

        h.Server.Hellos.Last().Features.Should().BeNull();
        h.Server.StatsPublishes.Should().BeEmpty();
    }

    /// <summary>測試用的宿主：包一個 <see cref="FakeHostPort"/>，另外實作 <see cref="IDeviceStatsPort"/></summary>
    private sealed class FakeStatsPort(FakeHostPort inner) : ISyncHostPort, IDeviceStatsPort
    {
        public ConcurrentQueue<StatsUpdate> Updates { get; } = new();

        public ConcurrentQueue<int?> Rtts { get; } = new();

        public bool Watching { get; set; }

        public DeviceStats? CurrentStats() => Sample;

        public void DeliverStats(StatsUpdate update) => Updates.Enqueue(update);

        public void ReportRtt(int? rttMs) => Rtts.Enqueue(rttMs);

        public long UiAliveAt => inner.UiAliveAt;

        public Task RunOnUiAsync(Action action, CancellationToken ct) => inner.RunOnUiAsync(action, ct);

        public void PublishStatus(SyncStatusSnapshot status) => inner.PublishStatus(status);

        public void PublishPollSnapshot(UsagePollSnapshot snapshot) => inner.PublishPollSnapshot(snapshot);

        public void PingUi() => inner.PingUi();
    }
}
