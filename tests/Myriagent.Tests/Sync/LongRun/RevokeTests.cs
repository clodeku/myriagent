using System.Diagnostics;
using Myriagent.Sync;
using Myriagent.Tests.Sync.WithHub;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>
/// SEC-13 的裝置端、離線撤銷那一條路：伺服器停機時把裝置標成撤銷（hub.db 的 devices.revoked_at／revoked_reason），啟動後裝置的權杖檢查失敗（401），
/// 引擎停止（Unauthorized），不再重連。在線時經擁有者的撤銷端點撤銷、量「≤1 秒斷線」的是 WithHub 的
/// <see cref="WithHub.RevokeEndpointTests"/>（預設的 dotnet test 就跑）
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class RevokeTests(ITestOutputHelper output)
{
    private const string Reason = "對 MyriagentHub 子程序的長情境：停機撤銷裝置";

    [EnvFact(LongRun.Gate, Reason)]
    public async Task 停機改devices_revoked_at_啟動後401_Stopped()
    {
        await using var hub = await MyriagentHubProcess.StartAsync();
        var db = new HubDb(hub.DataDir);
        using var dir = new DeviceDir();
        PairedDevice paired;
        using (var owner = await OwnerClient.LoginAsync(hub))
            paired = await DevicePairing.PairAsync(hub, owner, dir.Paths);
        await using var engine = new HubEngine(paired, HubEngine.FastBackoff(0.2, 0.5, 1));
        engine.Start();
        await engine.WaitForStateAsync(SyncEngineState.Live, LongRun.Wait);

        await hub.StopAsync();
        db.Revoke(paired.DeviceId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var watch = Stopwatch.StartNew();
        await hub.LaunchAsync();
        await engine.WaitUntilAsync(v => v.State == SyncEngineState.Stopped, LongRun.Wait, "撤銷後停止");
        output.WriteLine($"伺服器就緒後 {watch.Elapsed.TotalSeconds:0.0} 秒停止");

        (await engine.Inspect(v => v.StopReason)).Should().Be(SyncStopReason.Unauthorized);
        var generation = await engine.Inspect(v => v.ConnectionGeneration);
        await Task.Delay(TimeSpan.FromSeconds(2));
        (await engine.Inspect(v => (v.State, v.ConnectionGeneration))).Should().Be((SyncEngineState.Stopped, generation), "停止後不再重連（退避步階 0.2～1 秒）");
        engine.Host.LastStatus!.StopReason.Should().Be(SyncStopReason.Unauthorized, "宿主收到停止的原因（網頁顯示要重新配對）");
    }
}
