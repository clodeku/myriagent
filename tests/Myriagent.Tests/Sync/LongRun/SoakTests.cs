using Myriagent.Sync;
using Myriagent.Tests.Sync.WithHub;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>
/// S8 的加強版：三個引擎持續產生事件（一半不合併、一半同一個合併鍵），以固定種子決定三輪強制結束伺服器的間隔與是否先暫停（讓批次一定在途），
/// 每輪立刻重啟。種子與每輪的間隔印在輸出（重現用）。最後每個作者 deviceSeq 1..N 各一筆、雜湊等於本機 outbox、沒有停止、沒有批次跳號
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class SoakTests(ITestOutputHelper output)
{
    private const string Reason = "對 MyriagentHub 子程序的長情境：隨機三輪強制結束伺服器（約 1 分鐘）";

    /// <summary>
    /// 固定種子（要換情境就改這裡，並把新的種子記進計畫附錄 B）。20261023：等 2.2 秒先暫停、5.1 秒直接、3.0 秒先暫停——
    /// 兩種強制結束都有（20261016 三輪都是先暫停）
    /// </summary>
    private const int Seed = 20261023;

    [EnvFact(LongRun.Gate, Reason)]
    public async Task 隨機三輪Kill_固定種子()
    {
        var random = new Random(Seed);
        var rounds = Enumerable.Range(1, 3).Select(i => (Round: i, Delay: TimeSpan.FromMilliseconds(random.Next(2000, 8000)), Suspend: random.Next(2) == 0)).ToList();
        output.WriteLine($"種子 {Seed}：{string.Join("；", rounds.Select(r => $"第 {r.Round} 輪 等 {r.Delay.TotalSeconds:0.0} 秒、{(r.Suspend ? "先暫停" : "直接")}強制結束"))}");

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
                    engines.Add(new HubEngine(await DevicePairing.PairAsync(hub, owner, dir.Paths, deviceName: $"soak 電腦 {i + 1}")));
            }

            foreach (var engine in engines)
                engine.Start();
            await KillRounds.AllLiveAsync(engines, "開始前");

            using var stop = new CancellationTokenSource();
            var counts = new long[engines.Count];
            var generators = engines.Select((e, i) => KillRounds.GenerateAsync(e, i, counts, stop.Token)).ToList();
            foreach (var (round, delay, suspend) in rounds)
            {
                await Task.Delay(delay);
                output.WriteLine(await KillRounds.KillAndRestartAsync(hub, engines, round, suspend));
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
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
