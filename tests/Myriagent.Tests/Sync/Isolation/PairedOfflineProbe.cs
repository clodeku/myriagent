using System.Diagnostics;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.Sync.Pairing;
using Myriagent.Tests.Sync.WithHub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.Isolation;

/// <summary>
/// 子程序探針的 <c>paired-offline</c> 情境（<see cref="IsolationProbeProgram"/>）：已配對、伺服器連不上。
/// 經 <see cref="SyncEngineLoader.Create"/> 建正式的引擎（SignalR 傳輸、sync.db），等它連不上而進入退避再正常停止
/// </summary>
internal static class PairedOfflineProbe
{
    /// <summary>測試端：在探針的資料根做出已配對的狀態（偽造的核准回應、記憶體金鑰）</summary>
    public static void Prepare(string root)
    {
        var paths = new SyncPaths(Path.Combine(root, "sync"));
        Directory.CreateDirectory(paths.Directory);
        using var signer = new InMemoryDeviceSigner();
        var deviceId = SyncIds.NewDeviceId();
        var response = PairingTestData.Approval(deviceId, signer.PublicSpki);
        var device = PairingTestData.Device(deviceId, devicePubKey: signer.PublicSpki, hubSpki: response.HubIdentitySpki, token: response.DeviceToken);
        SyncEngineLoader.CompletePairing(paths, response, device);
        File.WriteAllText(Path.Combine(root, IsolationProbeProgram.PairedOfflineKeyFile), signer.ExportPkcs8());
    }

    /// <summary>探針端（子程序裡；AppPaths 已改到資料根）</summary>
    public static void Run(string data)
    {
        var paths = SyncPaths.FromAppPaths();
        var device = DeviceFile.Load(paths.DeviceFile).Data ?? throw new InvalidOperationException("資料根沒有 device.json");
        using var key = InMemoryDeviceSigner.FromPkcs8(File.ReadAllText(Path.Combine(data, IsolationProbeProgram.PairedOfflineKeyFile)));
        using var ui = new UiThread();
        var host = new FakeHostPort(ui, TimeProvider.System);
        var applier = new RecordingApplier(ui);
        var engine = (SyncEngine)SyncEngineLoader.Create(new SyncEngineCreateArgs(
            paths,
            device,
            host,
            applier,
            new DevicePolicy("none", false),
            DevTrustEnv: false,
            new Uri(IsolationProbeProgram.PairedOfflineServer),
            DevicePairing.AppVersion,
            null,
            null,
            _ => key.Clone(),
            HubEngine.FastBackoff(30)));
        engine.Start();
        try
        {
            var watch = Stopwatch.StartNew();
            while (engine.InvokeAsync(v => v.State).GetAwaiter().GetResult() != SyncEngineState.Backoff)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(20))
                    throw new TimeoutException($"20 秒內沒有進入退避：{engine.InvokeAsync(v => (v.State, v.StopReason)).GetAwaiter().GetResult()}");
                Thread.Sleep(50);
            }
        }
        finally
        {
            engine.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }
}
