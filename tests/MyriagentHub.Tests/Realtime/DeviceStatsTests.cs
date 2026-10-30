using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.TestSupport;
using MyriagentHub.Sync;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 各電腦的即時狀態（sync-protocol 14.8，stat1）：看板（只在記憶體）的收件間隔、「有人在看」、每台取最後收下的、斷線與太久沒送就拿掉；
/// 轉送迴圈只送給在看、協商出 stat1 的連線，同一份不重送，有人在看時取樣主機；裝置 hub 的 StatsPublish 與瀏覽器的 monitor／deviceStats 端到端
/// </summary>
public sealed class DeviceStatsTests
{
    private static readonly DeviceStats Sample = new(12.5, 4L << 30, 16L << 30, [new DiskStats("C:", 100L << 30, 500L << 30)], 2_000, 300, RttMs: 30, Cores: 8);

    [Fact]
    public void 看板_同一條連線兩次至少隔1秒_收下才配新的seq()
    {
        var clock = new StepClock();
        var board = new DeviceStatsBoard(clock);
        var changed = 0;
        board.Changed += () => changed++;

        board.Publish("c1", "d_A", Sample, watch: false).Accepted.Should().BeTrue();
        clock.Advance(999);
        board.Publish("c1", "d_A", Sample with { Cpu = 50 }, watch: false).Accepted.Should().BeFalse("不到 1 秒");
        board.Snapshot().Single().Stats.Cpu.Should().Be(12.5);
        changed.Should().Be(1, "沒收下的不算改變");
        clock.Advance(1);
        board.Publish("c1", "d_A", Sample with { Cpu = 50 }, watch: false).Accepted.Should().BeTrue();
        var entry = board.Snapshot().Single();
        entry.Stats.Cpu.Should().Be(50);
        entry.Seq.Should().Be(2);
        clock.Advance(1_500);
        board.Snapshot().Single().AgeMs.Should().Be(1_500, "ageMs 是收下到現在的經過時間（伺服器的單調時鐘）");
    }

    [Fact]
    public void 看板_有人在看是其他電腦的watch或開著面板的瀏覽器_自己的不算()
    {
        var clock = new StepClock();
        var browser = false;
        var board = new DeviceStatsBoard(clock) { BrowserWatching = () => browser };
        var changed = 0;
        board.Changed += () => changed++;

        board.Publish("cA", "d_A", Sample, watch: true).Watched.Should().BeFalse("只有自己在看");
        board.AnyWatching().Should().BeTrue();
        board.Publish("cB", "d_B", Sample, watch: false).Watched.Should().BeTrue("A 在看");
        clock.Advance(1_000);
        changed = 0;
        board.Publish("cA", "d_A", Sample, watch: false).Watched.Should().BeFalse("B 沒在看");
        board.WatchingConnections().Should().BeEmpty();
        board.AnyWatching().Should().BeFalse();

        browser = true;
        board.Publish("cA", "d_A", Sample, watch: false).Watched.Should().BeTrue("瀏覽器開著面板");
        board.AnyWatching().Should().BeTrue();

        // 太密的不收，但照樣記 watch；從不看變成看也算改變（那條連線要立刻收到一份）
        browser = false;
        board.Publish("cB", "d_B", Sample, watch: false).Accepted.Should().BeTrue("距上一次已經 1 秒");
        changed = 0;
        board.Publish("cB", "d_B", Sample, watch: true).Accepted.Should().BeFalse();
        board.WatchingConnections().Should().Equal("cB");
        changed.Should().Be(1);
    }

    [Fact]
    public void 看板_同一台兩條連線取最後收下的_斷線與太久沒送就拿掉()
    {
        var clock = new StepClock();
        var board = new DeviceStatsBoard(clock);
        board.Publish("old", "d_B", Sample with { Cpu = 1 }, watch: false);
        clock.Advance(10);
        board.Publish("new", "d_B", Sample with { Cpu = 2 }, watch: false);
        board.Publish("cA", "d_A", Sample with { Cpu = 3 }, watch: false);
        board.Snapshot().Select(e => (e.DeviceId, e.Stats.Cpu)).Should().Equal(("d_A", 3.0), ("d_B", 2.0));

        board.Remove("new");
        board.Snapshot().Select(e => (e.DeviceId, e.Stats.Cpu)).Should().Equal(new[] { ("d_A", 3.0), ("d_B", 1.0) }, "新的那條斷了，舊的還在");

        clock.Advance(DeviceStatsRules.StaleAfterMs);
        board.Publish("cA", "d_A", Sample, watch: false);
        var changed = 0;
        board.Changed += () => changed++;
        board.ExpireStale();
        changed.Should().Be(1);
        board.Snapshot().Select(e => e.DeviceId).Should().Equal("d_A");
        board.ExpireStale();
        changed.Should().Be(1, "沒有可拿的不通知");
    }

    [Fact]
    public async Task 轉送_只送在看而且協商出stat1的已證明連線_同一份不重送_有人在看才取樣主機()
    {
        var clock = new StepClock();
        var board = new DeviceStatsBoard(clock);
        var registry = new ConnectionRegistry();
        var sender = new RecordingDeviceSender();
        var fanout = new DeviceStatsFanout(board, registry, sender, new HubStatsSampler(NullLogger<HubStatsSampler>.Instance), clock, NullLogger<DeviceStatsFanout>.Instance);
        var watcher = Connect(registry, "cW", SyncIds.NewDeviceId(), [DeviceFeatureRules.Stat1]);
        var legacy = Connect(registry, "cL", SyncIds.NewDeviceId(), []);

        await fanout.RunOnceAsync(CancellationToken.None);
        board.Published!.Hub.Should().BeNull("沒人在看時不取樣主機");
        sender.All.Should().BeEmpty();

        board.Publish(legacy.ConnectionId, legacy.DeviceId, Sample, watch: true);
        board.Publish(watcher.ConnectionId, watcher.DeviceId, Sample with { Cpu = 70 }, watch: true);
        await fanout.RunOnceAsync(CancellationToken.None);
        var sent = sender.All.Where(s => s.Method == DeviceHubMethods.StatsUpdate).ToList();
        sent.Select(s => s.ConnectionId).Should().Equal(new[] { "cW" }, "沒協商出 stat1 的連線不送");
        var update = (StatsUpdate)sent.Single().Message;
        update.Devices.Select(d => d.DeviceId).Should().Equal(new[] { legacy.DeviceId, watcher.DeviceId }.Order(StringComparer.Ordinal), "依 deviceId 排序");
        update.Hub.Should().NotBeNull("有人在看：主機取樣了");
        update.Hub!.Seq.Should().Be(1);
        update.Hub.Devices.Should().Be(2, "在線的電腦數");
        DeviceStatsRules.CheckUpdate(update).Should().BeNull("伺服器自己的取樣也要合格");

        await fanout.RunOnceAsync(CancellationToken.None);
        sender.All.Count(s => s.Method == DeviceHubMethods.StatsUpdate).Should().Be(1, "沒有改變、主機還不到 2 秒：不重送");

        clock.Advance(2_000);
        await fanout.RunOnceAsync(CancellationToken.None);
        var again = sender.All.Where(s => s.Method == DeviceHubMethods.StatsUpdate).ToList();
        again.Should().HaveCount(2, "主機重新取樣是新的一份");
        ((StatsUpdate)again[1].Message).Hub!.Seq.Should().Be(2);

        registry.Remove(watcher.ConnectionId);
        board.Remove(watcher.ConnectionId);
        clock.Advance(2_000);
        await fanout.RunOnceAsync(CancellationToken.None);
        sender.All.Count(s => s.Method == DeviceHubMethods.StatsUpdate).Should().Be(2, "在看的連線斷了");
    }

    [Fact]
    public async Task 端到端_StatsPublish回watched_在看的電腦收到StatsUpdate_瀏覽器monitor收到deviceStats()
    {
        await using var host = await HubTestHost.StartAsync();
        using var a = await TestDevice.AddAsync(host, "電腦甲");
        using var b = await TestDevice.AddAsync(host, "電腦乙");
        await using var ca = await DeviceHubClient.ConnectAsync(host, a.Token);
        await using var cb = await DeviceHubClient.ConnectAsync(host, b.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        await ca.HandshakeAsync(a, observed, features: [DeviceFeatureRules.Stat1]);
        await cb.HandshakeAsync(b, observed, features: [DeviceFeatureRules.Stat1]);

        (await ca.Connection.InvokeAsync<StatsPublishResult>(DeviceHubMethods.StatsPublish, new StatsPublish(Sample, Watch: false))).Watched.Should().BeFalse();
        (await cb.Connection.InvokeAsync<StatsPublishResult>(DeviceHubMethods.StatsPublish, new StatsPublish(Sample with { Cpu = 88 }, Watch: true))).Watched.Should().BeFalse("只有自己在看");
        var update = await cb.WaitForAsync<StatsUpdate>(u => u.Devices.Count == 2 && u.Hub is not null, 10_000);
        update.Devices.Single(d => d.DeviceId == a.Id).Stats.Cpu.Should().Be(12.5);
        update.Hub!.Name.Should().NotBeNullOrEmpty();
        update.Hub.Stats.MemTotal.Should().BeGreaterThan(0, "主機的負載是真的取樣");
        (await ca.Connection.InvokeAsync<StatsPublishResult>(DeviceHubMethods.StatsPublish, new StatsPublish(Sample, Watch: false))).Watched.Should().BeTrue("乙在看");
        await FluentActions.Invoking(() => ca.Connection.InvokeAsync<StatsPublishResult>(DeviceHubMethods.StatsPublish, new StatsPublish(Sample with { Cpu = 101 }, Watch: false)))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid*");

        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        (await owner.Hub.CmdAsync(new { cmd = "monitor", on = "yes" }))["error"]!.Value<string>().Should().Be("invalid", "on 要是布林");
        (await owner.Hub.CmdAsync(new { cmd = "monitor", on = true }))["accepted"]!.Value<bool>().Should().BeTrue();
        // 主機每 2 秒重新取樣（乙在看）：連線數是取樣當時的，等到算進這個瀏覽器的那一份
        var stats = await owner.Hub.WaitForAsync("deviceStats", m => m["devices"] is JArray { Count: 2 } && m["hub"] is JObject hub && hub.Value<int>("browsers") >= 1, 10_000);
        var first = (JObject)((JArray)stats["devices"]!).Single(d => d.Value<string>("id") == a.Id);
        first.Value<bool>("self").Should().BeFalse();
        first.Value<double>("cpu").Should().Be(12.5);
        stats["hub"]!.Value<int>("devices").Should().Be(2);
        ((JObject)stats["hub"]!).Should().NotContainKey("rttMs");

        (await owner.Hub.CmdAsync(new { cmd = "monitor", on = false }))["accepted"]!.Value<bool>().Should().BeTrue();
        // 關掉之前已經排進送出佇列的那一則可能晚一點才到
        await Task.Delay(300);
        owner.Hub.Drain();
        await cb.Connection.InvokeAsync<StatsPublishResult>(DeviceHubMethods.StatsPublish, new StatsPublish(Sample with { Cpu = 5 }, Watch: true));
        await owner.Hub.AssertNoMessageAsync("deviceStats", 1_500);
    }

    [Fact]
    public async Task 沒協商出stat1的連線呼叫StatsPublish_invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        using var a = await TestDevice.AddAsync(host);
        await using var ca = await DeviceHubClient.ConnectAsync(host, a.Token);
        await ca.HandshakeAsync(a, DeviceHubClient.ObservedSpki(host), features: [DeviceFeatureRules.Conv1]);
        await FluentActions.Invoking(() => ca.Connection.InvokeAsync<StatsPublishResult>(DeviceHubMethods.StatsPublish, new StatsPublish(Sample, Watch: true)))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid*");
    }

    private static DeviceConnection Connect(ConnectionRegistry registry, string connectionId, string deviceId, IReadOnlyList<string> features)
    {
        var connection = new DeviceConnection(connectionId, deviceId, () => { }, new UpstreamContext(deviceId, null, null), features);
        registry.Add(connection, 0);
        registry.MarkProven(connection);
        return connection;
    }

    /// <summary>單調時鐘一毫秒一格、手動前進（看板與轉送只用 GetTimestamp）</summary>
    private sealed class StepClock : TimeProvider
    {
        private long now = 1_000_000;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref now);

        public void Advance(long ms) => Interlocked.Add(ref now, ms);
    }
}
