using System.Collections.Concurrent;
using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Tests.Sync.WithHub;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>
/// S12（用量指派互斥，sync-protocol 6.4）：三台假裝置（tools/fake-device 的 DeviceRuntime）各有一個同一身分的帳戶，連同一個 MyriagentHub。
/// 每台以同樣的節拍模擬「到了本機的輪詢時間」，用量查詢閘門（ShouldPoll）放行才查並計數；Claude 走真的 RefreshAsync 與假的官方端點，
/// Codex 在閘門放行時放入 api 快照（fake-device 的 B109）。斷言：每個 term 至多一台 On（只計 Live 的裝置）、伺服器採用的帶 term 上傳都來自
/// 當時的負責者、正常停止負責的那台後 15＋2 秒內另一台接手、查詢次數總和不超過同一套模擬在未配對時的單機基準
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class UsageAssignmentTests(ITestOutputHelper output)
{
    private const string Reason = "對 MyriagentHub 子程序的長情境：三台假裝置的用量指派（約 1 分鐘）";

    /// <summary>模擬的本機輪詢節拍（三台同一個節拍；單機基準也是）</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);

    private const int TicksBeforeStop = 12;

    private const int TicksAfterStop = 40;

    /// <summary>設計：連線關閉立即重算，定期重算每 15 秒（UsageCoordinator.Interval）；再給 2 秒的餘裕</summary>
    private static readonly TimeSpan TakeoverLimit = TimeSpan.FromSeconds(17);

    [EnvFact(LongRun.Gate, Reason)]
    public Task 同一Codex身分三台在線() => RunAsync(AgentProvider.Codex, "s12-codex@example.test", orgId: null);

    /// <summary>Claude 要有 orgId 才連結（沒有 orgId 的 Claude 帳戶不連結，S12 對它不成立）</summary>
    [EnvFact(LongRun.Gate, Reason)]
    public Task 同一Claude身分三台在線_有orgId() => RunAsync(AgentProvider.Claude, "s12-claude@example.test", orgId: "org-s12");

    private async Task RunAsync(AgentProvider provider, string email, string? orgId)
    {
        var totalTicks = TicksBeforeStop + TicksAfterStop;
        var baseline = await BaselineAsync(provider, email, orgId, totalTicks);
        output.WriteLine($"單機基準（未配對、{totalTicks} 拍 × {Tick.TotalMilliseconds:0} ms）：查詢 {baseline} 次");
        baseline.Should().Be(totalTicks, "未配對時閘門一律放行（陽性對照：模擬真的會查）");

        await using var hub = await MyriagentHubProcess.StartAsync();
        using var owner = await OwnerClient.LoginAsync(hub);
        var db = new HubDb(hub.DataDir);
        var devices = new List<LongRunDevice>();
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var alias = $"s12-{provider}-{i + 1}";
                devices.Add(await LongRunDevice.PairAndStartAsync(hub, owner, $"S12 電腦 {i + 1}",
                    [LongRunDevice.Account(alias, provider, email, orgId)], HubEngine.FastBackoff(0.2, 0.5, 1)));
            }

            var identityKey = provider switch
            {
                AgentProvider.Claude => $"claude:{orgId}:{email}",
                AgentProvider.Codex => $"codex:{email}",
                _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "S12 只測 Claude、Codex"),
            };
            var logicalId = await WaitForAsync(() => db.LogicalAccountId(identityKey), "伺服器建立了這個身分的邏輯帳戶");
            await WaitForAsync(() => db.ReadyLinks(logicalId).Count == 3 ? "ok" : null, "三台都連結到同一個邏輯帳戶（ready、身分相符）");
            // 伺服器只對負責查詢的那台（與換人時被換下的那台）送 UsageAssign：其他台沒有這個帳戶的指派（Unknown），Live 時一樣不查
            foreach (var device in devices)
                await device.WaitUntilAsync(v => v.State == SyncEngineState.Live, LongRun.Wait, "Live");

            var samples = new ConcurrentQueue<(TimeSpan At, string Device, long Term, bool On)>();
            var clock = Stopwatch.StartNew();
            using var stopSampling = new CancellationTokenSource();
            var sampler = SampleAsync(devices, logicalId, clock, samples, stopSampling.Token);
            await WaitForAsync(() => OnDevices(samples, clock.Elapsed - TimeSpan.FromMilliseconds(300)).Count == 1 ? "ok" : null, "穩定後恰好一台是 On");
            // 穩定狀態還要每台自己的目錄裡有這台的連結：還沒拉到自己的連結時閘門照設計放行（「這個帳戶沒有連結→查」，離線優先），
            // 那不是互斥要量的。等到恰好一台的閘門放行、而且就是 On 的那台
            var onDevice = OnDevices(samples, clock.Elapsed - TimeSpan.FromMilliseconds(300)).Single().Device;
            var gateWatch = Stopwatch.StartNew();
            while (true)
            {
                var open = new List<string>();
                for (var i = 0; i < devices.Count; i++)
                {
                    var id = DeviceRuntime.AccountId($"s12-{provider}-{i + 1}");
                    var runtime = devices[i].Runtime;
                    if (await runtime.OnUi(() => runtime.Accounts.UsagePollPolicy?.ShouldPoll(runtime.Accounts.Find(id)!) ?? true))
                        open.Add(devices[i].DeviceId);
                }

                if (open.Count == 1 && open[0] == onDevice)
                    break;
                if (gateWatch.Elapsed > LongRun.Wait)
                    throw new Xunit.Sdk.XunitException($"{LongRun.Wait.TotalSeconds:0} 秒內閘門沒有穩定成只有 On 的那台放行：{string.Join("、", open.Select(d => Name(devices, d)))}");
                await Task.Delay(100);
            }

            output.WriteLine($"穩定（只有 {Name(devices, onDevice)} 的閘門放行）：配對後 {clock.Elapsed.TotalSeconds:0.0} 秒");

            var polls = new int[devices.Count];
            async Task TickAllAsync(int tick)
            {
                var windows = new Dictionary<string, double> { [UsageRulesKeys.Session] = tick % 100, [UsageRulesKeys.Weekly] = 10 + tick % 50 };
                await Task.WhenAll(devices.Select(async (d, i) =>
                {
                    if (d.Stopped)
                        return;
                    if (await d.Runtime.PollUsageAsync($"s12-{provider}-{i + 1}", windows))
                        Interlocked.Increment(ref polls[i]);
                }));
            }

            for (var tick = 0; tick < TicksBeforeStop; tick++)
            {
                await TickAllAsync(tick);
                await Task.Delay(Tick);
            }

            var first = OnDevices(samples, clock.Elapsed - TimeSpan.FromMilliseconds(300)).Single();
            var firstDevice = devices.Single(d => d.DeviceId == first.Device);
            var firstTerm = first.Term;
            output.WriteLine($"負責查詢的是 {firstDevice.Name}（term {firstTerm}）：正常停止它");
            await firstDevice.StopAsync();
            var stoppedAt = clock.Elapsed;

            TimeSpan? takeoverAt = null;
            for (var tick = TicksBeforeStop; tick < totalTicks; tick++)
            {
                await TickAllAsync(tick);
                takeoverAt ??= samples.Where(s => s.At > stoppedAt && s.On && s.Term > firstTerm && s.Device != firstDevice.DeviceId).Select(s => (TimeSpan?)s.At).FirstOrDefault();
                await Task.Delay(Tick);
            }

            await stopSampling.CancelAsync();
            await sampler;

            // 1. 每個 term 至多一台 On（只計 Live 的裝置的認定：斷線的那台收不到 off）
            var onByTerm = samples.Where(s => s.On).GroupBy(s => s.Term).ToDictionary(g => g.Key, g => g.Select(s => s.Device).Distinct().ToList());
            foreach (var (term, owners) in onByTerm)
                owners.Should().ContainSingle($"term {term} 只有一台認為自己負責查詢（{string.Join("、", owners)}）");
            var concurrent = samples.Where(s => s.On).GroupBy(s => (s.At.Ticks / TimeSpan.FromMilliseconds(100).Ticks)).Where(g => g.Select(s => s.Device).Distinct().Count() > 1).ToList();
            output.WriteLine($"取樣 {samples.Count} 筆；同一個 100 ms 內有兩台 On 的時段 {concurrent.Count} 個（term 交接的瞬間，舊的那台已停止）");

            // 2. 正常停止 On 的那台後 15＋2 秒內另一台接手
            takeoverAt.Should().NotBeNull("另一台接手（新的 term、On）");
            var takeover = takeoverAt!.Value - stoppedAt;
            output.WriteLine($"停止後 {takeover.TotalSeconds:0.00} 秒另一台轉成 On");
            takeover.Should().BeLessThan(TakeoverLimit);

            // 3. 伺服器採用的帶 term 上傳都來自當時的負責者
            var adopted = db.UsageSnapshots(logicalId).Where(u => u.Term is not null).ToList();
            adopted.Should().NotBeEmpty("負責的那台有上傳（陽性對照）");
            foreach (var (seq, source, term, kind) in adopted)
            {
                kind.Should().Be(UsageSyncRules.SourceApi);
                onByTerm.Should().ContainKey(term!.Value, $"seq {seq} 的 term {term} 有被取樣到");
                onByTerm[term.Value].Should().Equal([source], $"seq {seq}（term {term}）來自當時的負責者");
            }

            adopted.Select(u => u.SourceDeviceId).Distinct().Should().HaveCount(2, "停止前後各由一台上傳");
            output.WriteLine($"伺服器採用的帶 term 上傳 {adopted.Count} 筆：{string.Join("、", adopted.GroupBy(u => (u.SourceDeviceId, u.Term)).Select(g => $"{Name(devices, g.Key.SourceDeviceId)} term {g.Key.Term} × {g.Count()}"))}");

            // 4. 查詢次數總和 ≤ 單機基準
            var total = devices.Select((d, i) => d.Runtime.Usage.Requests(DeviceRuntime.AccountId($"s12-{provider}-{i + 1}"))).Sum();
            output.WriteLine($"查詢次數：{string.Join("、", devices.Select((d, i) => $"{d.Name} {polls[i]}"))}，總和 {total}（基準 {baseline}）");
            total.Should().Be(polls.Sum(), "端點的計數與閘門放行的次數相同");
            total.Should().BeLessThanOrEqualTo(baseline, "三台合計不超過單機");
            total.Should().BeGreaterThan(0);
        }
        finally
        {
            foreach (var device in devices)
                await device.DisposeAsync();
        }
    }

    /// <summary>同一套模擬在未配對時的單機查詢次數</summary>
    private static async Task<int> BaselineAsync(AgentProvider provider, string email, string? orgId, int ticks)
    {
        using var dir = new TempDirectory("s12-baseline");
        const string alias = "s12-baseline";
        await using var single = await LongRunDevice.StartUnpairedAsync(dir.Root, [LongRunDevice.Account(alias, provider, email, orgId)]);
        for (var tick = 0; tick < ticks; tick++)
        {
            await single.PollUsageAsync(alias, new Dictionary<string, double> { [UsageRulesKeys.Session] = tick % 100, [UsageRulesKeys.Weekly] = 10 });
            await Task.Delay(Tick);
        }

        return single.Usage.Requests(DeviceRuntime.AccountId(alias));
    }

    /// <summary>每 100 ms 記下每台還在跑的裝置在 Live 時對這個邏輯帳戶的指派</summary>
    private static async Task SampleAsync(List<LongRunDevice> devices, string logicalId, Stopwatch clock,
        ConcurrentQueue<(TimeSpan, string, long, bool)> samples, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var device in devices.Where(d => !d.Stopped).ToList())
            {
                try
                {
                    var (state, assignment) = await device.Inspect(v => (v.State, v.Assignments.GetValueOrDefault(logicalId)));
                    if (state == SyncEngineState.Live && assignment is not null)
                        samples.Enqueue((clock.Elapsed, device.DeviceId, assignment.Term, assignment.On));
                }
                catch (Exception) when (device.Stopped)
                {
                    // 取樣時剛好在停止
                }
            }

            try
            {
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>某個時間點之後的取樣裡，各台最後一筆是 On 的</summary>
    private static List<(string Device, long Term)> OnDevices(IEnumerable<(TimeSpan At, string Device, long Term, bool On)> samples, TimeSpan since) =>
        samples.Where(s => s.At >= since).GroupBy(s => s.Device).Select(g => g.Last()).Where(s => s.On).Select(s => (s.Device, s.Term)).ToList();

    private static string Name(List<LongRunDevice> devices, string deviceId) => devices.FirstOrDefault(d => d.DeviceId == deviceId)?.Name ?? deviceId;

    private static async Task<string> WaitForAsync(Func<string?> probe, string because)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (probe() is { } value)
                return value;
            if (watch.Elapsed > LongRun.Wait)
                throw new Xunit.Sdk.XunitException($"{LongRun.Wait.TotalSeconds:0} 秒內沒有等到「{because}」");
            await Task.Delay(100);
        }
    }

    /// <summary>fake-device 劇本的時間窗鍵（UsageRules.SessionKey、WeeklyKey）</summary>
    private static class UsageRulesKeys
    {
        public const string Session = Myriagent.Usage.UsageRules.SessionKey;

        public const string Weekly = Myriagent.Usage.UsageRules.WeeklyKey;
    }
}
