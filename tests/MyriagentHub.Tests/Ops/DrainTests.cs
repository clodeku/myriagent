using System.Globalization;
using System.Net;
using System.Text.Json;
using MyriagentHub.Admin;
using MyriagentHub.Audit;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Tests.Ops;

/// <summary>
/// 排空的期限（docs/hub-setup.md 第 11 節）：部署收件匣的 drain 在 Hub:Deploy:DrainMaxSeconds 後、服務仍在執行（不在停止中）就自動解除
/// （安裝腳本的停止要求沒送到服務時不會一直排空到重啟）；管理收件匣根目錄（擁有者）的 drain 永不過期；擁有者排空中收到部署的 drain 不降級；
/// undrain 清掉任何排空。狀態是不可變快照（讀的人不會看到各欄位更新到一半，這由型別保證、不必測）；要測的是判斷與替換
/// 在同一把鎖裡：到期解除與新的 drain 同時發生時不會解除掉剛下的擁有者排空或新的部署期限，Changed 在鎖裡依狀態順序同步觸發
/// （Barrier 競爭測試：鎖正確時每輪結果都一樣，拿掉鎖或把判斷移到鎖外就會有幾輪錯）。
/// </summary>
public sealed class DrainTests
{
    private const long T0 = 1_760_000_000_000;

    private static readonly Dictionary<string, string?> SixtySeconds = new() { ["Hub:Deploy:DrainMaxSeconds"] = "60" };

    [Fact]
    public void 規則_部署的排空到期才過期_再來一次重設期限_擁有者的不過期也不降級_undrain清除()
    {
        var deploy = DrainRules.Drain(DrainSnapshot.None, DrainSource.Deploy, T0, 30, 300, "drain-1");
        deploy.Should().Be(new DrainSnapshot(true, DrainSource.Deploy, T0, T0 + 300_000, 30, "drain-1"));
        DrainRules.IsExpired(deploy, T0 + 299_999).Should().BeFalse();
        DrainRules.IsExpired(deploy, T0 + 300_000).Should().BeTrue("期限當下就算到期");
        DrainRules.ShouldAutoUndrain(deploy, T0 + 300_000, stopping: false).Should().BeTrue();
        DrainRules.ShouldAutoUndrain(deploy, T0 + 300_000, stopping: true).Should().BeFalse("停止中不解除，維持排空到程序結束");

        var again = DrainRules.Drain(deploy, DrainSource.Deploy, T0 + 200_000, 45, 300, "drain-2");
        again.ExpiresAt.Should().Be(T0 + 500_000, "部署排空中再來一次部署的 drain：重設期限");
        again.Since.Should().Be(T0, "一直在排空：開始時間保留最早的");
        again.RequestId.Should().Be("drain-2");
        again.RetryAfterSec.Should().Be(45);

        var admin = DrainRules.Drain(DrainSnapshot.None, DrainSource.Admin, T0, 30, 300, "a-1");
        admin.ExpiresAt.Should().BeNull();
        DrainRules.IsExpired(admin, long.MaxValue).Should().BeFalse("擁有者的排空永不過期");
        DrainRules.Drain(admin, DrainSource.Deploy, T0 + 1_000, 30, 300, "drain-3").Should().BeSameAs(admin, "擁有者排空中收到部署的 drain：維持擁有者的排空、不加期限");

        var upgraded = DrainRules.Drain(deploy, DrainSource.Admin, T0 + 1_000, 30, 300, "a-2");
        upgraded.Source.Should().Be(DrainSource.Admin, "部署排空中收到擁有者的 drain：換成擁有者的");
        upgraded.ExpiresAt.Should().BeNull();
        upgraded.Since.Should().Be(T0);

        DrainRules.Undrain().Should().Be(DrainSnapshot.None);
        DrainRules.IsExpired(DrainSnapshot.None, long.MaxValue).Should().BeFalse();
        DrainRules.SourceName(DrainSource.Admin).Should().Be(AuditActors.Admin, "與稽核的 actor 同名");
        DrainRules.SourceName(DrainSource.Deploy).Should().Be(AuditActors.Deploy);
        DrainRules.SourceName(null).Should().BeNull();
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(300, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    [InlineData(0, false)]
    [InlineData(-300, false)]
    public void 設定值驗證_DrainMaxSeconds只收60到3600(int seconds, bool ok)
    {
        var validate = () => DrainRules.ValidateDeployDrainMaxSeconds(seconds);
        if (ok)
            validate.Should().NotThrow().Which.Should().Be(seconds);
        else
            validate.Should().Throw<InvalidOperationException>().WithMessage("*Hub:Deploy:DrainMaxSeconds*60*3600*");
    }

    [Fact]
    public async Task 設定值不合法_服務啟動失敗_admin_preflight也擋下()
    {
        var dir = TempDir.Create();
        var work = TempDir.Create("myriagenthub-preflight-temp");
        try
        {
            var start = () => HubTestHost.StartAsync(dataDir: dir, settings: new Dictionary<string, string?> { ["Hub:Deploy:DrainMaxSeconds"] = "30" });
            (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Hub:Deploy:DrainMaxSeconds*：30");

            var paths = new HubPaths(dir);
            Directory.CreateDirectory(paths.ConfigDir);
            await File.WriteAllTextAsync(Path.Combine(paths.ConfigDir, "appsettings.Production.json"),
                "{\"Hub\":{\"ServerDomain\":\"hub.example.com\",\"Deploy\":{\"DrainMaxSeconds\":4000}}}");
            var result = await Preflight.RunAsync(paths, work, CancellationToken.None);
            result.Ok.Should().BeFalse("部署前就擋下：新版讀到不合法的設定");
            var check = result.Checks.Single(c => c.Name == "config.deploy_drain_max_seconds");
            check.Ok.Should().BeFalse();
            check.Detail.Should().Contain("4000");
        }
        finally
        {
            TempDir.TryDelete(work);
            TempDir.TryDelete(dir);
        }
    }

    /// <summary>
    /// 到期解除（DrainExpiryService 每秒一次）與擁有者的 drain 同時發生：「判斷到期」與「換成沒有排空」要在同一把鎖裡，
    /// 否則到期判斷看到的是舊的部署排空，接著把擁有者剛換上的排空當成到期解除掉。鎖正確時不論誰先，結果都是擁有者的排空。
    /// </summary>
    [Fact]
    public async Task DrainState_到期解除與擁有者的drain同時發生_擁有者的排空一定留著()
    {
        var state = new DrainState();
        var lost = await RaceAsync(
            reset: () =>
            {
                state.Undrain();
                state.Drain(DrainSource.Deploy, T0, 30, 60, "deploy");
            },
            first: () => state.ExpireIfDue(T0 + 120_000, stopping: false),
            second: () => state.Drain(DrainSource.Admin, T0 + 1, 30, 60, "admin"),
            ok: () => state.Current is { Draining: true, Source: DrainSource.Admin, ExpiresAt: null, RequestId: "admin" });
        lost.Should().Be(0, $"{RaceRounds} 輪裡擁有者的排空被到期解除蓋掉的輪數");
    }

    /// <summary>
    /// 到期解除與新的部署 drain 同時發生（舊的部署排空已過期，新的部署又來 drain）：新的期限不能被舊的到期判斷清掉。
    /// 鎖正確時：先解除再 drain，或先重設期限、到期判斷就看到還沒到期——兩種結果都是新的部署排空與新的期限。
    /// </summary>
    [Fact]
    public async Task DrainState_到期解除與新的部署drain同時發生_新的期限不會被舊的到期判斷清掉()
    {
        const long Now = T0 + 120_000;
        var state = new DrainState();
        var lost = await RaceAsync(
            reset: () =>
            {
                state.Undrain();
                state.Drain(DrainSource.Deploy, T0, 30, 60, "deploy-old");
            },
            first: () => state.ExpireIfDue(Now, stopping: false),
            second: () => state.Drain(DrainSource.Deploy, Now, 30, 60, "deploy-new"),
            ok: () => state.Current is { Draining: true, Source: DrainSource.Deploy, ExpiresAt: Now + 60_000, RequestId: "deploy-new" });
        lost.Should().Be(0, $"{RaceRounds} 輪裡新的部署排空被舊的到期判斷解除的輪數");
    }

    /// <summary>
    /// Changed 的約定（第 3 階段的廣播靠它）：在變更的呼叫裡同步觸發（呼叫回來之前、同一條執行緒、還在狀態鎖裡），
    /// 處理器讀 <see cref="DrainState.Current"/> 看到的就是剛換上的狀態；快照真的變了才觸發（期限重設、來源改變也算）。
    /// </summary>
    [Fact]
    public void DrainState_Changed在變更的呼叫裡同步觸發_處理器看到剛換上的狀態_沒變就不觸發()
    {
        var state = new DrainState();
        var calls = new List<(bool Draining, DrainSnapshot Seen, int Thread)>();
        state.Changed += draining => calls.Add((draining, state.Current, Environment.CurrentManagedThreadId));
        var me = Environment.CurrentManagedThreadId;

        var deploy = state.Drain(DrainSource.Deploy, T0, 30, 60, "d-1");
        calls.Should().Equal([(true, deploy, me)], "進入排空：呼叫回來之前已在同一條執行緒觸發，處理器讀到的就是新狀態");
        var reset = state.Drain(DrainSource.Deploy, T0 + 10_000, 30, 60, "d-2");
        var owner = state.Drain(DrainSource.Admin, T0 + 20_000, 30, 60, "a-1");
        calls.Skip(1).Should().Equal([(true, reset, me), (true, owner, me)], "期限重設、換成擁有者的排空都算變更");

        state.Drain(DrainSource.Deploy, T0 + 30_000, 30, 60, "d-3").Should().BeSameAs(owner);
        state.ExpireIfDue(long.MaxValue, stopping: false).Should().BeNull();
        calls.Should().HaveCount(3, "擁有者排空中的部署 drain、擁有者的排空不會到期：狀態沒變就不觸發");

        state.Undrain();
        state.Undrain();
        calls.Skip(3).Should().Equal([(false, DrainSnapshot.None, me)], "第二次 undrain 已經沒有排空：不觸發");

        var again = state.Drain(DrainSource.Deploy, T0, 30, 60, "d-4");
        state.ExpireIfDue(T0 + 60_000, stopping: true).Should().BeNull("停止中不解除");
        state.ExpireIfDue(T0 + 60_000, stopping: false).Should().Be(again);
        calls.Skip(4).Should().Equal([(true, again, me), (false, DrainSnapshot.None, me)], "到期解除也在呼叫裡同步觸發");
    }

    /// <summary>
    /// 同時變更時通知的順序就是狀態的順序（Changed 在狀態鎖裡觸發）：undrain 與擁有者的 drain 同時發生，不論誰先，
    /// 最後一次通知都是最後的狀態。通知在鎖外送出時，兩邊的通知可能顛倒，廣播就停在錯的狀態。
    /// </summary>
    [Fact]
    public async Task DrainState_同時變更時通知的順序就是狀態的順序_最後一次通知就是最後的狀態()
    {
        var state = new DrainState();
        var notified = new object();
        bool? last = null;
        state.Changed += draining =>
        {
            lock (notified)
                last = draining;
        };
        var wrong = await RaceAsync(
            reset: () =>
            {
                state.Undrain();
                state.Drain(DrainSource.Deploy, T0, 30, 60, "deploy");
                lock (notified)
                    last = null;
            },
            first: () => state.Undrain(),
            second: () => state.Drain(DrainSource.Admin, T0 + 1, 30, 60, "admin"),
            ok: () =>
            {
                lock (notified)
                    return last == state.IsDraining;
            });
        wrong.Should().Be(0, $"{RaceRounds} 輪裡最後一次通知與最後的狀態不同的輪數");
    }

    [Fact]
    public async Task 部署的排空到期自動解除_稽核記system_readyz恢復_觸發Changed()
    {
        await using var host = await HubTestHost.StartAsync(settings: SixtySeconds);
        var drain = host.Services.GetRequiredService<DrainState>();
        var changes = new List<bool>();
        drain.Changed += value =>
        {
            lock (changes)
                changes.Add(value);
        };

        var id = "drain-0.2.0-1a2b3c4-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var ack = await DeployDrainAsync(host, id);
        ack.GetProperty("ok").GetBoolean().Should().BeTrue();
        ack.GetProperty("message").GetString().Should().Contain("60 秒後");
        var draining = drain.Current;
        draining.Source.Should().Be(DrainSource.Deploy);
        draining.ExpiresAt.Should().Be(host.Clock.NowMs + 60_000);

        using var local = host.CreateClient(ip: "127.0.0.1", cookies: false);
        using (var ready = await local.GetAsync("/readyz"))
        {
            ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var json = JsonDocument.Parse(await ready.Content.ReadAsStringAsync()).RootElement;
            json.GetProperty("draining").GetBoolean().Should().BeTrue();
            var info = json.GetProperty("drain");
            info.GetProperty("source").GetString().Should().Be("deploy");
            info.GetProperty("expiresAt").GetInt64().Should().Be(draining.ExpiresAt!.Value);
            info.GetProperty("since").GetInt64().Should().Be(draining.Since!.Value);
            info.GetProperty("requestId").GetString().Should().Be(id);
        }

        host.Clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(DrainExpiryService.CheckInterval * 2.5);
        drain.IsDraining.Should().BeTrue("還沒到期");

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => Task.FromResult(!drain.IsDraining), "到期後下一次檢查（1 秒內）就解除");
        using (var ready = await local.GetAsync("/readyz"))
        {
            ready.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = JsonDocument.Parse(await ready.Content.ReadAsStringAsync()).RootElement;
            json.GetProperty("draining").GetBoolean().Should().BeFalse();
            json.GetProperty("drain").ValueKind.Should().Be(JsonValueKind.Null);
        }
        lock (changes)
            changes.Should().Equal([true, false], "進入排空、自動解除各觸發一次 Changed");

        var auditLog = host.Services.GetRequiredService<AuditLog>();
        await WaitUntilAsync(async () => (await auditLog.ListAsync(null, 50)).Any(a => a.Action == AuditActions.Drain), "解除之後寫稽核");
        var record = (await auditLog.ListAsync(null, 50)).Single(a => a.Action == AuditActions.Drain);
        record.ActorType.Should().Be(AuditActors.System);
        record.Target.Should().Be("undrain");
        record.Outcome.Should().Be(AuditOutcomes.Ok);
        record.Detail.Should().Contain("deploy_drain_expired").And.Contain(id);
        (await auditLog.VerifyAsync()).Ok.Should().BeTrue();
    }

    [Fact]
    public async Task 擁有者的排空不過期_其間部署的drain不降級也不加期限_undrain才解除()
    {
        await using var host = await HubTestHost.StartAsync(settings: SixtySeconds);
        var drain = host.Services.GetRequiredService<DrainState>();
        (await AdminCommandLine.RunAsync(["drain", "--data", host.DataDir, "--wait", "20"], TextWriter.Null)).Should().Be(AdminExitCodes.Ok);
        var owner = drain.Current;
        owner.Source.Should().Be(DrainSource.Admin);
        owner.ExpiresAt.Should().BeNull();

        var ack = await DeployDrainAsync(host, "drain-0.2.0-1a2b3c4-owner");
        ack.GetProperty("ok").GetBoolean().Should().BeTrue("部署照樣拿到回覆，可以停服務");
        ack.GetProperty("message").GetString().Should().Contain("擁有者的排空進行中");
        drain.Current.Should().Be(owner, "維持擁有者的排空：不降級成部署的、不加期限");

        host.Clock.Advance(TimeSpan.FromHours(2));
        await Task.Delay(DrainExpiryService.CheckInterval * 2.5);
        drain.IsDraining.Should().BeTrue("擁有者的排空永不過期");
        using (var local = host.CreateClient(ip: "127.0.0.1", cookies: false))
        using (var ready = await local.GetAsync("/readyz"))
        {
            ready.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var info = JsonDocument.Parse(await ready.Content.ReadAsStringAsync()).RootElement.GetProperty("drain");
            info.GetProperty("source").GetString().Should().Be("admin");
            info.GetProperty("expiresAt").ValueKind.Should().Be(JsonValueKind.Null);
        }

        (await AdminCommandLine.RunAsync(["undrain", "--data", host.DataDir, "--wait", "20"], TextWriter.Null)).Should().Be(AdminExitCodes.Ok);
        drain.Current.Should().Be(DrainSnapshot.None);
        (await host.Services.GetRequiredService<AuditLog>().ListAsync(null, 50)).Should().NotContain(a => a.Action == AuditActions.Drain, "沒有自動解除");
    }

    [Fact]
    public async Task 部署的排空中undrain_清掉排空_之後不會再自動解除()
    {
        await using var host = await HubTestHost.StartAsync(settings: SixtySeconds);
        var drain = host.Services.GetRequiredService<DrainState>();
        await DeployDrainAsync(host, "drain-0.2.0-1a2b3c4-undrain");
        drain.Current.Source.Should().Be(DrainSource.Deploy);

        (await AdminCommandLine.RunAsync(["undrain", "--data", host.DataDir, "--wait", "20"], TextWriter.Null)).Should().Be(AdminExitCodes.Ok);
        drain.Current.Should().Be(DrainSnapshot.None, "undrain 清掉任何排空（含期限）");

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(DrainExpiryService.CheckInterval * 2.5);
        drain.Current.Should().Be(DrainSnapshot.None);
        (await host.Services.GetRequiredService<AuditLog>().ListAsync(null, 50)).Should().NotContain(a => a.Action == AuditActions.Drain);
    }

    [Fact]
    public async Task 停止中不自動解除_不在停止中才解除_只解除一次()
    {
        await using var host = await HubTestHost.StartAsync();
        var auditLog = host.Services.GetRequiredService<AuditLog>();
        var clock = new ManualTimeProvider();
        var state = new DrainState();
        var options = Options.Create(new HubOptions());
        state.Drain(DrainSource.Deploy, clock.NowMs, 30, options.Value.Deploy.DrainMaxSeconds, "drain-stopping");
        clock.Advance(TimeSpan.FromSeconds(options.Value.Deploy.DrainMaxSeconds + 1));

        var stopping = new FakeLifetime();
        stopping.StopApplication();
        using (var service = new DrainExpiryService(state, auditLog, clock, options, stopping, NullLogger<DrainExpiryService>.Instance))
            (await service.CheckAsync()).Should().BeFalse("ApplicationStopping 之後不解除");
        state.IsDraining.Should().BeTrue();

        using (var service = new DrainExpiryService(state, auditLog, clock, options, new FakeLifetime(), NullLogger<DrainExpiryService>.Instance))
        {
            (await service.CheckAsync()).Should().BeTrue("到期、服務在執行、不在停止中");
            (await service.CheckAsync()).Should().BeFalse("已經解除，不再重複");
        }
        state.Current.Should().Be(DrainSnapshot.None);
        (await auditLog.ListAsync(null, 50)).Count(a => a.Action == AuditActions.Drain).Should().Be(1);
    }

    /// <summary>每個競爭測試跑的輪數：鎖正確時每一輪的結果都一樣，輪數只影響抓到錯誤的機率</summary>
    private const int RaceRounds = 20_000;

    /// <summary>
    /// 兩條執行緒每一輪由 Barrier 同時放行，各做一個操作（之前各自隨機自旋一小段，讓先後與交錯每輪不同）；Barrier 的階段後動作
    /// （兩邊都做完、下一輪還沒放行）檢查結果並重設狀態。回傳結果不對的輪數：鎖正確時兩個操作不論誰先結果都一樣，一定是 0；
    /// 判斷與替換不在同一把鎖裡時，總有幾輪會在中間被另一邊插隊。
    /// </summary>
    private static async Task<int> RaceAsync(Action reset, Action first, Action second, Func<bool> ok)
    {
        var bad = 0;
        reset();
        using var barrier = new Barrier(2, b =>
        {
            // 第 0 階段是兩條執行緒一起開始，還沒做任何操作
            if (b.CurrentPhaseNumber > 0 && !ok())
                bad++;
            reset();
        });
        // 一邊的操作丟例外就不會再到 Barrier：另一邊等有上限，測試以例外失敗而不是一直卡住
        void Sync()
        {
            if (!barrier.SignalAndWait(RaceSyncTimeout))
                throw new TimeoutException($"另一條執行緒 {RaceSyncTimeout.TotalSeconds:0} 秒沒有到 Barrier（它的操作丟了例外？）");
        }

        Task Run(Action operation, int seed) => Task.Factory.StartNew(() =>
        {
            var random = new Random(seed);
            Sync();
            for (var i = 0; i < RaceRounds; i++)
            {
                Thread.SpinWait(random.Next(RaceSpin));
                operation();
                Sync();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await Task.WhenAll(Run(first, 1), Run(second, 2));
        return bad;
    }

    /// <summary>競爭測試每一輪等另一條執行緒的上限（正常一輪是微秒級）</summary>
    private static readonly TimeSpan RaceSyncTimeout = TimeSpan.FromSeconds(10);

    /// <summary>每輪操作前隨機自旋的上限（SpinWait 的次數）：與一次加鎖操作的時間相當，兩邊才會常常交錯</summary>
    private const int RaceSpin = 16;

    /// <summary>以安裝腳本的格式寫部署收件匣的 drain，等 status\drain.json 回同一個 id</summary>
    private static async Task<JsonElement> DeployDrainAsync(HubTestHost host, string id)
    {
        DeployInterfaceTests.WriteRequest(host.Paths.AdminInboxDeployDir, id, DeployInterfaceTests.PowerShellDrainRequest(id, "0.2.0-1a2b3c4", DateTimeOffset.UtcNow));
        return await DeployInterfaceTests.WaitForJsonAsync(host.Paths.DrainAckFile, j => j.GetProperty("id").GetString() == id);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string because)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
                return;
            await Task.Delay(50);
        }
        (await condition()).Should().BeTrue(because + "（等了 10 秒）");
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => stopping.Cancel();
    }
}
