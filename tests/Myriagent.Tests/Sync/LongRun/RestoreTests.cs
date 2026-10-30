using System.Collections.Concurrent;
using System.Diagnostics;
using Myriagent.Agents;
using Myriagent.Sync;
using Myriagent.Tests.Sync.WithHub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Sync.LongRun;

/// <summary>
/// 伺服器還原（設計 10.2、D15）：<c>admin backup-now</c> → 再寫 → <c>admin stop</c> → <c>admin restore --from</c> → 啟動。
/// 裝置看到同一個實例的新還原世代：游標歸零、目錄快取清空、以對帳模式重送 outbox 保留的列，link.upsert／cli.state 以新的 deviceSeq 重送，
/// 對帳完成後才更新 seen_server
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class RestoreTests(ITestOutputHelper output)
{
    private const string Reason = "對 MyriagentHub 子程序的長情境：備份、停機還原、裝置對帳（約 1 分鐘）";

    private const int BeforeBackup = 500;

    private const int AfterBackup = 500;

    [EnvFact(LongRun.Gate, Reason)]
    public async Task backup_now後再寫_restore_裝置對帳重送_0遺失0重複()
    {
        var logMark = LogScan.Mark();
        await using var hub = await MyriagentHubProcess.StartAsync();
        var db = new HubDb(hub.DataDir);
        LongRunDevice device;
        using (var owner = await OwnerClient.LoginAsync(hub))
        {
            device = await LongRunDevice.PairAndStartAsync(hub, owner, "還原測試電腦",
                [LongRunDevice.Account("restore-codex", AgentProvider.Codex, "restore@example.test")], HubEngine.FastBackoff(0.2, 0.5, 1));
        }

        await using var _ = device;
        var engine = await device.EngineAsync();
        await device.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Outbox.Count > 0, LongRun.Wait, "Live、帳戶與 CLI 狀態已排進 outbox");
        Backlog.Append(engine, 1, BeforeBackup);
        await ExactlyOnce.WaitDrainedAsync(db, engine, device.DeviceId, LongRun.Wait);
        var seenBefore = await device.Inspect(v => v.SeenServer);
        seenBefore.Should().NotBeNull();
        db.ServerMeta("restore_generation").Should().Be("0");

        var (backupCode, backupText, backupError) = await hub.RunAdminAsync("backup-now", "--wait", "60");
        backupCode.Should().Be(0, $"備份要成功：{backupText}{backupError}");
        var folder = JObject.Parse(backupText).SelectToken("data.folder")?.Value<string>();
        folder.Should().NotBeNullOrEmpty();

        // 備份之後才寫的：還原後伺服器沒有這些，要靠裝置的對帳重送
        Backlog.Append(engine, BeforeBackup + 1, AfterBackup);
        await ExactlyOnce.WaitDrainedAsync(db, engine, device.DeviceId, LongRun.Wait);
        var lastBefore = (await device.Inspect(v => v.Outbox)).Max(e => e.DeviceSeq);
        var cursorBefore = await device.Inspect(v => v.Cursors.GetValueOrDefault(SyncClasses.Catalog));
        output.WriteLine($"還原前：deviceSeq 到 {lastBefore}、目錄游標 {cursorBefore}、備份 {folder}");

        await hub.StopAsync();
        await device.WaitUntilAsync(v => v.State == SyncEngineState.Backoff, LongRun.Wait, "伺服器停止後退避");
        var (restoreCode, restoreText, restoreError) = await hub.RunAdminAsync("restore", "--from", folder!);
        restoreCode.Should().Be(0, $"還原要成功（服務已停止）：{restoreText}{restoreError}");
        db.DeviceEvents(device.DeviceId).Max(e => e.DeviceSeq).Should().BeLessThanOrEqualTo(lastBefore - AfterBackup, "伺服器回到備份時的狀態：備份之後的事件不見了（陽性對照）");

        // 對帳期間：Reconciling 為真時 seen_server 一定還是舊的
        var samples = new ConcurrentQueue<(bool Reconciling, long? Generation, long Cursor, SyncEngineState State)>();
        using var stopSampling = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stopSampling.IsCancellationRequested)
            {
                samples.Enqueue(await engine.InvokeAsync(v => (v.Reconciling, v.SeenServer?.RestoreGeneration, v.Cursors.GetValueOrDefault(SyncClasses.Catalog), v.State)));
                await Task.Delay(5);
            }
        });

        var launched = Stopwatch.StartNew();
        await hub.LaunchAsync();
        await device.WaitUntilAsync(v => v.State == SyncEngineState.Live && !v.Reconciling && v.SeenServer?.RestoreGeneration == 1, LongRun.Wait, "對帳完成、Live");
        var reconciled = launched.Elapsed;
        await stopSampling.CancelAsync();
        await sampler;
        await ExactlyOnce.WaitDrainedAsync(db, engine, device.DeviceId, LongRun.Wait);

        // restoreGeneration 改變
        db.ServerMeta("restore_generation").Should().Be("1");
        var seenAfter = await device.Inspect(v => v.SeenServer);
        seenAfter.Should().Be(seenBefore! with { RestoreGeneration = 1 }, "同一個實例、新的還原世代");

        // seen_server 對帳完成後才更新
        samples.Should().Contain(s => s.Reconciling, "取樣有落在對帳期間（陽性對照）");
        samples.Where(s => s.Reconciling).Should().OnlyContain(s => s.Generation == seenBefore!.RestoreGeneration, "對帳還沒完成時 seen_server 仍是舊的");

        // 游標歸零、重拉完成
        var recent = LogScan.Since(logMark);
        recent.Should().Contain("重設游標與目錄快取，開始對帳");
        recent.Should().Contain("伺服器換實例後的對帳完成");
        // 伺服器發的事件（每筆裝置事件之後的 device.upsert 等）有 200 ms 的合併通知：等游標追上伺服器的最後一筆
        var (cursorAfter, serverMax) = (0L, 0L);
        var catchUp = Stopwatch.StartNew();
        while (catchUp.Elapsed < LongRun.Wait)
        {
            serverMax = db.MaxSeq(SyncClasses.Catalog);
            cursorAfter = await device.Inspect(v => v.Cursors.GetValueOrDefault(SyncClasses.Catalog));
            if (cursorAfter == serverMax)
                break;
            await Task.Delay(100);
        }

        cursorAfter.Should().Be(serverMax, "重拉到伺服器的最後一筆");
        serverMax.Should().BeGreaterThan(1_000_000_000L, "還原後的新事件 seq 跳過 10 億");
        var sawReset = samples.Any(s => s.Cursor < cursorBefore);
        output.WriteLine($"還原後啟動到對帳完成 {reconciled.TotalSeconds:0.0} 秒；取樣 {samples.Count} 筆、對帳中 {samples.Count(s => s.Reconciling)} 筆，取樣{(sawReset ? "有" : "沒有")}看到歸零後的游標；游標 {cursorBefore} → {cursorAfter}");
        (await device.Inspect(v => v.Catalog?.Accounts.Count ?? 0)).Should().BeGreaterThan(0, "目錄快取清空後重拉回來");

        // 每個作者 1..N 各一筆，link.upsert、cli.state 以新的 deviceSeq 重送
        await ExactlyOnce.AssertAsync(db, engine, device.DeviceId);
        var events = db.DeviceEvents(device.DeviceId);
        var resent = events.Where(e => e.DeviceSeq > lastBefore).Select(e => e.Kind).ToList();
        resent.Should().Contain(SyncEventKinds.LinkUpsert).And.Contain(SyncEventKinds.CliState);
        output.WriteLine($"伺服器上這台 deviceSeq 1..{events.Count} 各一筆；還原後以新的 deviceSeq 重送 {string.Join("、", resent.GroupBy(k => k).Select(g => $"{g.Key} × {g.Count()}"))}");
    }

    /// <summary>A 在備份前配對、B 在備份後配對；還原後 A 的名冊版本變小仍整份取代（不含 B），B 的 Hello 得到 401、Stopped(Unauthorized)</summary>
    [EnvFact(LongRun.Gate, Reason)]
    public async Task 還原到新裝置配對之前的備份_舊裝置整份換名冊_新裝置401停止()
    {
        await using var hub = await MyriagentHubProcess.StartAsync();
        using var dirA = new DeviceDir();
        using var dirB = new DeviceDir();
        HubEngine a;
        HubEngine b;
        string folder;
        using (var owner = await OwnerClient.LoginAsync(hub))
        {
            a = new HubEngine(await DevicePairing.PairAsync(hub, owner, dirA.Paths, deviceName: "備份前的電腦"), HubEngine.FastBackoff(0.2, 0.5, 1));
            a.Start();
            await a.WaitForStateAsync(SyncEngineState.Live, LongRun.Wait);

            var (code, text, error) = await hub.RunAdminAsync("backup-now", "--wait", "60");
            code.Should().Be(0, $"備份要成功：{text}{error}");
            folder = JObject.Parse(text).SelectToken("data.folder")!.Value<string>()!;

            b = new HubEngine(await DevicePairing.PairAsync(hub, owner, dirB.Paths, deviceName: "備份後的電腦"), HubEngine.FastBackoff(0.2, 0.5, 1));
        }

        await using var _a = a;
        await using var _b = b;
        b.Start();
        await b.WaitForStateAsync(SyncEngineState.Live, LongRun.Wait);
        var idA = a.SavedDevice().DeviceId;
        var idB = b.SavedDevice().DeviceId;
        await a.WaitUntilAsync(v => v.Registry.Any(r => r.DeviceId == idB), LongRun.Wait, "A 的名冊有 B（TrustChanged → GetTrust）");
        var revBefore = await a.Inspect(v => v.RegistryRev);

        await hub.StopAsync();
        var (restoreCode, restoreText, restoreError) = await hub.RunAdminAsync("restore", "--from", folder);
        restoreCode.Should().Be(0, $"還原要成功：{restoreText}{restoreError}");
        await hub.LaunchAsync();

        await a.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.RegistryRev < revBefore, LongRun.Wait, "A 回到 Live、名冊換成備份時的版本");
        var (revAfter, registry) = await a.Inspect(v => (v.RegistryRev, v.Registry));
        registry.Select(r => r.DeviceId).Should().Contain(idA).And.NotContain(idB, "名冊整份取代：備份之後才配對的 B 不在裡面");
        output.WriteLine($"A 的名冊版本 {revBefore} → {revAfter}（{registry.Count} 台）");

        await b.WaitUntilAsync(v => v.State == SyncEngineState.Stopped, LongRun.Wait, "B 停止");
        (await b.Inspect(v => v.StopReason)).Should().Be(SyncStopReason.Unauthorized, "B 不在還原後的 devices 裡：權杖無效（401）");
    }
}
