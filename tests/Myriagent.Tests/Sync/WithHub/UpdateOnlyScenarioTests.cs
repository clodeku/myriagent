using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// app 自我更新 U3 第二步對 MyriagentHub 子程序（sync-protocol 1.4）：配對之後伺服器把 <c>Hub:Sync:MinAppVersion</c> 調到比這台高，
/// 宣告 update1 的桌面拿到只更新的連線（心跳不被擋、存取權杖能取發佈端點）；沒宣告的照舊回 protocol，U3 起不停止、一小時後重試
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class UpdateOnlyScenarioTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    /// <summary>心跳 1 秒：幾秒內就看得出心跳有沒有被 update_required 擋下（被擋會斷線重連、連線世代增加）</summary>
    private static readonly SyncEngineOptions Fast = new([0.2, 0.5, 1], () => 0.5, TimeSpan.FromSeconds(1), SyncTimingRules.AckTimeout, SyncTimingRules.BatchInterval);

    [Fact]
    public async Task 版本低於MinAppVersion_宣告update1的拿到只更新連線_沒宣告的一小時後重試()
    {
        await using var hub = await MyriagentHubProcess.StartAsync();
        using var owner = await OwnerClient.LoginAsync(hub);
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(hub, owner, dir.Paths);
        await hub.StopAsync();
        hub.ExtraSettings["Hub:Sync:MinAppVersion"] = "99.0.0";
        await hub.LaunchAsync();

        await using (var engine = new HubEngine(paired, Fast))
        {
            engine.HostPort = new FakeReleasePort(engine.Host);
            engine.Start();
            await engine.WaitUntilAsync(v => v.State == SyncEngineState.UpdateOnly, Wait, "app 版本低於 MinAppVersion、宣告了 update1：只更新");
            (await engine.Inspect(v => v.Features)).Should().Equal(DeviceFeatureRules.Update1);
            engine.Current.AccessTokenForTest.Should().StartWith(SyncProtocol.AccessTokenPrefix, "只更新的連線照樣拿到存取權杖");
            var generation = await engine.Inspect(v => v.ConnectionGeneration);

            using var timeout = new CancellationTokenSource(Wait);
            (await new HubReleaseSource(() => engine.Current.ReleaseHttp).GetLatestAsync(timeout.Token))
                .Should().BeNull("發佈端點能用（伺服器還沒有任何發佈：204）");

            await Task.Delay(TimeSpan.FromSeconds(3));
            (await engine.StateAsync()).Should().Be(SyncEngineState.UpdateOnly);
            (await engine.Inspect(v => v.ConnectionGeneration)).Should().Be(generation, "心跳沒有被 update_required 擋下而斷線");
        }

        await using (var old = new HubEngine(paired, Fast))
        {
            old.Start();
            await old.WaitUntilAsync(v => v.State == SyncEngineState.Backoff, Wait, "沒宣告 update1：protocol");
            old.Current.BackoffHistory.Should().Equal(new[] { SyncBackoffRules.ProtocolRetry }, "U3 起 protocol 不停止，一小時後重試");
        }
    }
}
