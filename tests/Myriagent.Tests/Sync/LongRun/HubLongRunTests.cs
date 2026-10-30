using System.Diagnostics;
using Myriagent.Sync;
using Myriagent.Tests.Sync.WithHub;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>
/// 第 9 步的長情境（<c>MYRIAGENT_SYNC_IT=1</c>）：實際的退避步階（S5 的實時版）與串流中強制結束伺服器（S8）。
/// 每個測試自己的 MyriagentHub（S8 刻意強制結束；伺服器來不及刪的 TLS 私鑰檔由 MyriagentHubProcess.KillAsync 刪掉，結束時斷言沒有剩下，D5）
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class HubLongRunTests(ITestOutputHelper output)
{
    private const string Gate = "MYRIAGENT_SYNC_IT";

    private const string Reason = "對 MyriagentHub 子程序的長情境：實際退避、強制結束伺服器（數分鐘）";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [EnvFact(Gate, Reason)]
    public async Task OutboxDrain_實際退避_離線2分鐘_恢復後30秒內補完_0重複()
    {
        const int Count = 1000;
        await using var hub = await MyriagentHubProcess.StartAsync();
        using var owner = await OwnerClient.LoginAsync(hub);
        var db = new HubDb(hub.DataDir);
        using var dir = new DeviceDir();
        var paired = await DevicePairing.PairAsync(hub, owner, dir.Paths);
        await using var engine = new HubEngine(paired);
        engine.Start();
        await engine.WaitForStateAsync(SyncEngineState.Live, Wait);

        await hub.StopAsync();
        var offline = Stopwatch.StartNew();
        await engine.WaitForStateAsync(SyncEngineState.Backoff, Wait);
        Backlog.Append(engine.Current, 1, Count);
        await engine.WaitUntilAsync(v => v.Outbox.Count == Count, Wait, "1,000 筆都進了 outbox");
        var remaining = TimeSpan.FromMinutes(2) - offline.Elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);
        db.DeviceEvents(paired.DeviceId).Should().BeEmpty();

        await hub.LaunchAsync();
        output.WriteLine($"離線 {offline.Elapsed.TotalSeconds:0} 秒；退避 {string.Join(", ", engine.Current.BackoffHistory.Select(d => d.TotalSeconds.ToString("0.0")))} 秒");
        var elapsed = await Backlog.WaitDrainedAsync(db, engine, paired.DeviceId, Count, Wait);
        var sinceLaunch = Stopwatch.GetElapsedTime(hub.LaunchedAt);
        output.WriteLine($"補完 {Count} 筆：從 /healthz 就緒起 {elapsed.TotalSeconds:0.0} 秒、從啟動子程序起 {sinceLaunch.TotalSeconds:0.0} 秒");

        sinceLaunch.Should().BeLessThan(TimeSpan.FromSeconds(30), "恢復後 30 秒內補完（從啟動子程序起算，比從 /healthz 就緒起算保守）");
        await Backlog.AssertExactlyOnceAsync(db, engine, paired.DeviceId, Count);
        hub.ForcedKill.Should().BeFalse();
    }

    /// <summary>
    /// S8：同一個程序裡三個引擎（各自的 SyncPaths、預設退避）持續產生事件，串流中強制結束伺服器兩次（相隔 10 秒、立刻重啟）。
    /// 第 1 次先暫停伺服器、等三個引擎都有批次在途（送出了、伺服器沒處理）才強制結束：重連後要靠 Prove 的 retry 改寫重送那幾批；
    /// 第 2 次照實際的時機（有批次在途時）直接強制結束。事件一半不合併、一半同一個合併鍵（送出前沒凍結內容，在途時被合併改掉就會和伺服器收到的不同）。
    /// 每次重啟後 30 秒內三個都回到 Live；最後每個作者 deviceSeq 1..N 各一筆、雜湊等於本機 outbox、outbox 全部 Acked，
    /// 沒有引擎停止、沒有任何批次跳號（Prove 對齊之後第一批就接得上）
    /// </summary>
    [EnvFact(Gate, Reason)]
    public async Task DeviceSoak_三個引擎串流中Kill伺服器兩次_0遺失0重複()
    {
        var orphansBefore = UserKeyStore.FilesCreatedByTestHub();
        List<string> orphans = [];
        var logMark = LogScan.Mark();
        var hub = await MyriagentHubProcess.StartAsync();
        var dirs = Enumerable.Range(0, 3).Select(_ => new DeviceDir()).ToList();
        var engines = new List<HubEngine>();
        try
        {
            var db = new HubDb(hub.DataDir);
            using (var owner = await OwnerClient.LoginAsync(hub))
            {
                foreach (var (dir, i) in dirs.Select((d, i) => (d, i)))
                    engines.Add(new HubEngine(await DevicePairing.PairAsync(hub, owner, dir.Paths, deviceName: $"測試電腦 {i + 1}")));
            }

            foreach (var engine in engines)
                engine.Start();
            await KillRounds.AllLiveAsync(engines, "開始前");

            using var stop = new CancellationTokenSource();
            var counts = new long[engines.Count];
            var generators = engines.Select((e, i) => KillRounds.GenerateAsync(e, i, counts, stop.Token)).ToList();

            await Task.Delay(TimeSpan.FromSeconds(4));
            var firstKill = Stopwatch.StartNew();
            output.WriteLine(await KillRounds.KillAndRestartAsync(hub, engines, 1, suspendFirst: true));
            var untilSecond = TimeSpan.FromSeconds(10) - firstKill.Elapsed;
            if (untilSecond > TimeSpan.Zero)
                await Task.Delay(untilSecond);
            output.WriteLine(await KillRounds.KillAndRestartAsync(hub, engines, 2, suspendFirst: false));
            await Task.Delay(TimeSpan.FromSeconds(4));

            await stop.CancelAsync();
            await Task.WhenAll(generators);
            for (var i = 0; i < engines.Count; i++)
            {
                counts[i].Should().BeGreaterThan(100, "產生事件的工作真的在跑");
                var deviceId = engines[i].SavedDevice().DeviceId;
                var (elapsed, n) = await Backlog.WaitDrainedAsync(db, engines[i], deviceId, TimeSpan.FromSeconds(60));
                n.Should().BeGreaterThan((int)counts[i], "不合併的那一半每筆一列");
                await Backlog.AssertExactlyOnceAsync(db, engines[i], deviceId, n);
                engines[i].Host.Statuses.Select(s => s.Status.State).Should().NotContain(SyncEngineState.Stopped, $"引擎 {i + 1} 沒有停止");
                output.WriteLine($"引擎 {i + 1}：{n} 筆，停止產生後 {elapsed.TotalSeconds:0.0} 秒補完，連線 {await engines[i].Inspect(v => v.ConnectionGeneration)} 次");
            }

            // Prove 的 retry 改寫把在途的列對齊伺服器的 nextDeviceSeqExpected：重連後送出的第一批就接得上，不靠 gap 補救
            var gaps = LogScan.Since(logMark).Split((char)10).Count(l => l.Contains("批次跳號（gap）", StringComparison.Ordinal));
            gaps.Should().Be(0, "強制結束再重連後沒有任何批次跳號");
        }
        finally
        {
            foreach (var engine in engines)
                await engine.DisposeAsync();
            await hub.DisposeAsync();
            foreach (var dir in dirs)
                dir.Dispose();
            orphans = UserKeyStore.FilesCreatedByTestHub().Except(orphansBefore).ToList();
            // 強制結束時伺服器來不及刪的 TLS 私鑰檔（D5）由 MyriagentHubProcess.KillAsync 刪掉：應該一個都不剩
            output.WriteLine($"孤兒金鑰檔（本程序的 MyriagentHub 建、強制結束留下）：{orphans.Count} 個 {string.Join(", ", orphans)}");
        }

        orphans.Should().BeEmpty("強制結束留下的 TLS 私鑰檔由 MyriagentHubProcess.KillAsync 刪掉（只在測試本身通過時檢查，不蓋掉原本的失敗）");
    }
}
