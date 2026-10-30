using System.Diagnostics;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// 對 MyriagentHub 子程序跑的正式引擎（<see cref="SyncEngineLoader.Create"/>：SignalR 傳輸、系統時鐘、sync.db）。
/// 每次 <see cref="Start"/> 都從 device.json 重新讀裝置資料（引擎改釘與換權杖都寫在那裡，同 app 的下一次啟動）
/// </summary>
internal sealed class HubEngine : IAsyncDisposable
{
    public HubEngine(PairedDevice paired, SyncEngineOptions? options = null, Func<DeviceFileData, IDeviceSigner?>? signerFactory = null, Uri? serverOverride = null)
    {
        Paths = paired.Paths;
        ServerOverride = serverOverride;
        SignerFactory = signerFactory ?? paired.SignerFactory;
        Options = options;
        Ui = new UiThread();
        Host = new FakeHostPort(Ui, TimeProvider.System);
        Applier = new RecordingApplier(Ui);
    }

    public SyncPaths Paths { get; }

    public Func<DeviceFileData, IDeviceSigner?> SignerFactory { get; }

    public SyncEngineOptions? Options { get; }

    /// <summary>MYRIAGENT_SYNC_SERVER：連線改連這裡（中間人測試的 TLS 代理）</summary>
    public Uri? ServerOverride { get; }

    public UiThread Ui { get; }

    public FakeHostPort Host { get; }

    /// <summary>引擎用的宿主（null＝<see cref="Host"/>）；app 自我更新 U3 的情境換成包著它的 FakeReleasePort（宣告 update1）。Start 之前設定</summary>
    public ISyncHostPort? HostPort { get; set; }

    public RecordingApplier Applier { get; }

    public SyncEngine? Engine { get; private set; }

    public SyncEngine Current => Engine ?? throw new InvalidOperationException("引擎還沒啟動");

    /// <summary>短的退避步階（秒）與固定抖動 0.5（延遲剛好是步階）</summary>
    public static SyncEngineOptions FastBackoff(params double[] steps) =>
        new(steps, () => 0.5, SyncTimingRules.HeartbeatInterval, SyncTimingRules.AckTimeout, SyncTimingRules.BatchInterval);

    public DeviceFileData SavedDevice() => DeviceFile.Load(Paths.DeviceFile).Data ?? throw new InvalidOperationException("device.json 讀不到");

    public void Start()
    {
        if (Engine is not null)
            throw new InvalidOperationException("引擎已經啟動過（先 StopAsync）");
        Engine = (SyncEngine)SyncEngineLoader.Create(new SyncEngineCreateArgs(
            Paths,
            SavedDevice(),
            HostPort ?? Host,
            Applier,
            new DevicePolicy("none", false),
            DevTrustEnv: true,
            ServerOverride,
            DevicePairing.AppVersion,
            "TEST-PC",
            "tester",
            SignerFactory,
            Options));
        Engine.Start();
    }

    public async Task StopAsync()
    {
        if (Engine is null)
            return;
        await Engine.StopAsync(TimeSpan.FromSeconds(10));
        Engine = null;
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        Start();
    }

    public Task<T> Inspect<T>(Func<SyncEngine.EngineView, T> read) => Current.InvokeAsync(read);

    public Task<SyncEngineState> StateAsync() => Inspect(v => v.State);

    /// <summary>每 50 毫秒看一次，直到條件成立；逾時丟出（附上目前的狀態與最後一個狀態快照）</summary>
    public async Task WaitUntilAsync(Func<SyncEngine.EngineView, bool> condition, TimeSpan timeout, string because)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (await Inspect(condition))
                return;
            if (watch.Elapsed > timeout)
            {
                var (state, reason) = await Inspect(v => (v.State, v.StopReason));
                throw new XunitException($"{timeout.TotalSeconds:0} 秒內沒有等到「{because}」：狀態 {state}（{reason}），最後的狀態快照 {Host.LastStatus}");
            }

            await Task.Delay(50);
        }
    }

    public Task WaitForStateAsync(SyncEngineState expected, TimeSpan? timeout = null) =>
        WaitUntilAsync(v => v.State == expected, timeout ?? TimeSpan.FromSeconds(20), $"狀態 {expected}");

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Ui.Errors.Should().BeEmpty("UI 執行緒上不該有沒接住的例外");
        Ui.Dispose();
    }
}
