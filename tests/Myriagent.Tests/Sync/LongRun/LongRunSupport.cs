using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Tests.Sync.WithHub;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>長情境（<c>MYRIAGENT_SYNC_IT=1</c>）共用的常數</summary>
internal static class LongRun
{
    public const string Gate = "MYRIAGENT_SYNC_IT";

    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
}

/// <summary>
/// 一台假裝置（tools/fake-device 的 <see cref="DeviceRuntime"/>：照 AppController 的接法組出真的中樞、帳戶管理員、同步協調者與引擎，
/// 不碰使用者的帳戶、不執行 CLI、不連外網），先以 <see cref="DevicePairing"/> 配對到 MyriagentHub 子程序（記憶體金鑰）再啟動。
/// 同一個程序裡可以有好幾台：不存檔（AppPaths 是全域的），帳戶清單唯讀
/// </summary>
internal sealed class LongRunDevice : IAsyncDisposable
{
    private LongRunDevice(DeviceDir dir, PairedDevice paired, DeviceRuntime runtime, string name)
    {
        Dir = dir;
        Paired = paired;
        Runtime = runtime;
        Name = name;
    }

    public DeviceDir Dir { get; }

    public PairedDevice Paired { get; }

    public DeviceRuntime Runtime { get; }

    public string Name { get; }

    public string DeviceId => Paired.DeviceId;

    /// <summary>已經正常停止（之後不能再看引擎）</summary>
    public bool Stopped { get; private set; }

    public static async Task<LongRunDevice> PairAndStartAsync(
        MyriagentHubProcess hub,
        OwnerClient owner,
        string name,
        IReadOnlyList<FakeAccountSpec> accounts,
        SyncEngineOptions? options = null,
        Func<Myriagent.RemoteDesk.IDeskScreen>? deskScreen = null)
    {
        var (dir, paired) = await PairAsync(hub, owner, name);
        return await StartPairedAsync(dir, paired, name, accounts, options, deskScreen: deskScreen);
    }

    /// <summary>只配對（記憶體金鑰）、還不啟動：配對之後、啟動之前要先改資料根（例如灌舊對話、改 sync.db 的 kv）時用</summary>
    public static async Task<(DeviceDir Dir, PairedDevice Paired)> PairAsync(MyriagentHubProcess hub, OwnerClient owner, string name)
    {
        var dir = new DeviceDir();
        try
        {
            return (dir, await DevicePairing.PairAsync(hub, owner, dir.Paths, deviceName: name));
        }
        catch
        {
            dir.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 以配對好的資料根啟動（失敗時刪掉資料根）。<paramref name="engineFactory"/>：自己建引擎（例如包一層量時間的傳輸層）；
    /// null＝正式的 SyncEngineLoader.Create（換上記憶體金鑰與 <paramref name="options"/>）
    /// </summary>
    public static async Task<LongRunDevice> StartPairedAsync(
        DeviceDir dir,
        PairedDevice paired,
        string name,
        IReadOnlyList<FakeAccountSpec> accounts,
        SyncEngineOptions? options = null,
        Func<SyncEngineCreateArgs, ISyncEngine>? engineFactory = null,
        Myriagent.RemoteExec.IRemoteExecConfirmer? remoteExecConfirmer = null,
        Func<Myriagent.RemoteDesk.IDeskScreen>? deskScreen = null)
    {
        try
        {
            var runtime = await DeviceRuntime.StartAsync(new DeviceRuntimeOptions
            {
                DataRoot = dir.Root,
                Persist = false,
                DevTrust = true,
                MachineName = "TEST-PC",
                WindowsUser = "tester",
                AppVersion = DevicePairing.AppVersion,
                Accounts = accounts,
                EngineFactory = engineFactory ?? (args => SyncEngineLoader.Create(args with { SignerFactory = paired.SignerFactory, Options = options })),
                RemoteExecConfirmer = remoteExecConfirmer,
                DeskScreen = deskScreen,
            });
            return new LongRunDevice(dir, paired, runtime, name);
        }
        catch
        {
            dir.Dispose();
            throw;
        }
    }

    /// <summary>未配對的一台（單機基準）</summary>
    public static Task<DeviceRuntime> StartUnpairedAsync(string dataRoot, IReadOnlyList<FakeAccountSpec> accounts) =>
        DeviceRuntime.StartAsync(new DeviceRuntimeOptions { DataRoot = dataRoot, Persist = false, Accounts = accounts });

    public static FakeAccountSpec Account(string alias, AgentProvider provider, string email, string? orgId = null) =>
        new(alias, provider, $"{provider} {alias}", null, email, orgId, provider == AgentProvider.Claude ? "max" : "pro");

    public async Task<SyncEngine> EngineAsync() => await Runtime.EngineAsync() ?? throw new InvalidOperationException($"{Name} 沒有引擎");

    public async Task<T> Inspect<T>(Func<SyncEngine.EngineView, T> read) => await (await EngineAsync()).InvokeAsync(read);

    /// <summary>每 50 毫秒看一次引擎，直到條件成立；逾時丟出（附上目前的狀態）</summary>
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
                throw new XunitException($"{Name}：{timeout.TotalSeconds:0} 秒內沒有等到「{because}」：狀態 {state}（{reason}）");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>正常停止（停引擎、關連線；伺服器因此立刻重算用量指派）</summary>
    public async Task StopAsync()
    {
        Stopped = true;
        await Runtime.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Stopped = true;
        var errors = Runtime.Host.Errors;
        await Runtime.DisposeAsync();
        Dir.Dispose();
        errors.Should().BeEmpty($"{Name} 的 UI 執行緒上不該有沒接住的例外");
    }
}

/// <summary>伺服器上某台裝置寫的事件剛好是 deviceSeq 1..N 各一筆、內容等於本機 outbox（引擎不是 HubEngine 時用）</summary>
internal static class ExactlyOnce
{
    public static async Task AssertAsync(HubDb db, SyncEngine engine, string deviceId)
    {
        var outbox = await engine.InvokeAsync(v => v.Outbox);
        outbox.Should().OnlyContain(e => e.State == OutboxState.Acked, "本機 outbox 全部確認");
        var events = db.DeviceEvents(deviceId);
        events.Select(e => e.DeviceSeq).Should().Equal(Enumerable.Range(1, outbox.Count).Select(i => (long?)i), "deviceSeq 1..N 各一筆（0 遺失、0 重複）");
        db.Rejected(deviceId).Should().Be(0);
        var local = outbox.ToDictionary(e => e.DeviceSeq, e => e.PayloadSha256);
        foreach (var e in events)
            Convert.ToHexStringLower(e.PayloadSha256 ?? []).Should().Be(local[e.DeviceSeq!.Value].ToLowerInvariant(), $"deviceSeq {e.DeviceSeq} 的內容和本機 outbox 相同");
    }

    /// <summary>等本機 outbox 全部 Acked、伺服器的筆數等於 outbox</summary>
    public static async Task WaitDrainedAsync(HubDb db, SyncEngine engine, string deviceId, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var outbox = await engine.InvokeAsync(v => v.Outbox);
            var stored = db.DeviceEvents(deviceId).Count;
            if (outbox.Count > 0 && outbox.All(e => e.State == OutboxState.Acked) && stored == outbox.Count)
                return;
            if (watch.Elapsed > timeout)
            {
                var states = string.Join("、", outbox.GroupBy(e => e.State).Select(g => $"{g.Key} {g.Count()}"));
                throw new XunitException($"{timeout.TotalSeconds:0} 秒內沒有補完：伺服器 {stored} 筆，outbox {outbox.Count} 列（{states}）");
            }

            await Task.Delay(200);
        }
    }
}

/// <summary>串流中強制結束伺服器的編排（S8 與隨機 soak 共用）</summary>
internal static class KillRounds
{
    public const string CoalesceKey = "cli.soak";

    /// <summary>每個引擎每 10 毫秒追加兩筆 cli.state：一筆不合併、一筆用同一個合併鍵（還沒送的就原地換內容）</summary>
    public static async Task GenerateAsync(HubEngine engine, int index, long[] counts, CancellationToken ct)
    {
        var current = engine.Current;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = Interlocked.Increment(ref counts[index]);
                Backlog.Append(current, n, 1);
                Backlog.Append(current, -n, 1, CoalesceKey);
                await Task.Delay(10, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// 強制結束（整個程序樹）後立刻以同一個資料根與埠重啟；等每個引擎都以新的連線回到 Live（重啟後 30 秒內）。
    /// <paramref name="suspendFirst"/>：先暫停伺服器，等每個引擎都有批次在途才強制結束；否則等任一個有批次在途（最多 3 秒）。回傳說明文字
    /// </summary>
    public static async Task<string> KillAndRestartAsync(MyriagentHubProcess hub, IReadOnlyList<HubEngine> engines, int round, bool suspendFirst)
    {
        var generations = new List<int>();
        foreach (var engine in engines)
            generations.Add(await engine.Inspect(v => v.ConnectionGeneration));
        if (suspendFirst)
            hub.Suspend();
        var inFlight = false;
        var waiting = Stopwatch.StartNew();
        while (!inFlight && waiting.Elapsed < TimeSpan.FromSeconds(3))
        {
            var counts = new List<int>();
            foreach (var engine in engines)
                counts.Add(await engine.Inspect(v => v.InFlight));
            inFlight = suspendFirst ? counts.All(c => c > 0) : counts.Any(c => c > 0);
        }

        if (suspendFirst)
            inFlight.Should().BeTrue("伺服器暫停時每個引擎都送出了批次（產生事件的工作沒停）");

        var watch = Stopwatch.StartNew();
        await hub.KillAsync();
        var killed = watch.Elapsed;
        await hub.LaunchAsync();
        var ready = watch.Elapsed;
        ready.Should().BeLessThan(TimeSpan.FromSeconds(10), "強制結束後立刻重啟（啟動到 /healthz 就緒）");
        var restartedAt = Stopwatch.StartNew();
        for (var i = 0; i < engines.Count; i++)
        {
            var before = generations[i];
            await engines[i].WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration > before, LongRun.Wait, $"第 {round} 次強制結束後以新的連線回到 Live");
        }

        restartedAt.Elapsed.Should().BeLessThan(LongRun.Wait);
        return $"第 {round} 次強制結束（{(suspendFirst ? "先暫停、" : "")}{(inFlight ? "有" : "沒有")}批次在途）：{killed.TotalSeconds:0.0} 秒結束、{ready.TotalSeconds:0.0} 秒就緒，就緒後 {restartedAt.Elapsed.TotalSeconds:0.0} 秒全部回到 Live";
    }

    public static async Task AllLiveAsync(IEnumerable<HubEngine> engines, string when)
    {
        foreach (var engine in engines)
            await engine.WaitUntilAsync(v => v.State == SyncEngineState.Live, LongRun.Wait, $"{when}回到 Live");
    }
}
