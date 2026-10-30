using System.Text.Json;
using Myriagent.Sync;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using MyriagentHub.Tests.Media;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Tests.Ops;

/// <summary>
/// 閒置修剪（S9 的閒置記憶體，設計 14.20）：服務以撥快的計時器與假的 GC（<see cref="FakeHeap"/>）測什麼時候回收；
/// 活動的接線（最外層的中介程式、hub 的全域過濾器、ops/status 的計數）以測試主機測
/// </summary>
public sealed class IdleTrimTests
{
    private static readonly TimeSpan Step = IdleTrimRules.CheckPeriod;

    private const long Mb = 1L << 20;

    [Fact]
    public async Task 啟動之後靜置滿才回收一次_釋放樣式_之後同一段閒置不再做()
    {
        var (time, _, heap, service) = Start();
        heap.DlpCompiled = true;

        Advance(time, Step * 2);
        heap.Compactions.Should().Be(0, "啟動也算活動：20 秒還不到靜置期");

        Advance(time, Step);
        heap.Compactions.Should().Be(1, "靜置 30 秒");
        heap.Releases.Should().Be(1, "回收前先釋放已編譯的 DLP 樣式");
        heap.DlpCompiled.Should().BeFalse();
        service.Trims.Should().Be(1);

        Advance(time, Step * 30);
        heap.Compactions.Should().Be(1, "同一段閒置裡、已提交的空間沒長：不再回收");
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 有請求在處理時不回收_結束之後靜置滿才做()
    {
        var (time, activity, heap, service) = Start();
        activity.Begin();
        Advance(time, Step * 12);
        heap.Compactions.Should().Be(0, "轉送或上傳還在處理（兩分鐘）");

        activity.End();
        Advance(time, Step * 2);
        heap.Compactions.Should().Be(0, "結束也算活動：還不到靜置期");
        Advance(time, Step);
        heap.Compactions.Should().Be(1);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 新的活動之後_已提交長了就再回收_只有樣式的等滿10分鐘_只有短請求的不做()
    {
        var (time, activity, heap, service) = Start();
        Advance(time, Step * 3);
        heap.Compactions.Should().Be(1, "啟動之後那一次（第 30 秒）");

        // 只有短請求（版本檢查）：沒有樣式、已提交沒長
        activity.Note();
        Advance(time, Step * 10);
        heap.Compactions.Should().Be(1, "沒有東西要修剪");

        // 對話經過（樣式建了），距上一次回收才 2 分多：靜置滿也先不做，閒置一直持續到滿 10 分鐘才做
        activity.Note();
        heap.DlpCompiled = true;
        Advance(time, Step * 3);
        heap.Compactions.Should().Be(1, "只有樣式、距上一次回收不到 10 分鐘");
        Advance(time, TimeSpan.FromMinutes(10) - Step * 14);
        heap.Compactions.Should().Be(1, "第 620 秒的檢查：距上一次回收（第 30 秒）9 分 50 秒");
        Advance(time, Step);
        heap.Compactions.Should().Be(2, "第 630 秒：距上一次回收滿 10 分鐘");
        heap.Releases.Should().Be(1);

        // 媒體轉送（已提交多了 100 MB，沒有樣式）：剛回收過也立即做
        activity.Note();
        heap.Committed += 100 * Mb;
        Advance(time, Step * 3);
        heap.Compactions.Should().Be(3);
        heap.Releases.Should().Be(1, "沒有樣式就不釋放");
        service.Trims.Should().Be(3);
        await service.StopAsync(CancellationToken.None);
    }

    /// <summary>平常使用：回合之間各停一下（各自都靜置超過 30 秒、相隔 2 分鐘），只有樣式要修剪時兩段閒置只回收一次</summary>
    [Fact]
    public async Task 兩段相隔2分鐘的短閒置_只有樣式時只修剪一次()
    {
        var (time, activity, heap, service) = Start();
        Advance(time, Step * 3);
        heap.Compactions.Should().Be(1, "啟動之後那一次");
        Advance(time, TimeSpan.FromMinutes(10));

        // 第一個回合之後停 40 秒：距上一次回收已超過 10 分鐘，修剪
        activity.Note();
        heap.DlpCompiled = true;
        Advance(time, Step * 4);
        heap.Compactions.Should().Be(2);
        heap.Releases.Should().Be(1);

        // 2 分鐘後第二個回合（樣式重建了），之後也停 40 秒：不再修剪
        Advance(time, TimeSpan.FromMinutes(2) - Step * 4);
        activity.Note();
        heap.DlpCompiled = true;
        Advance(time, Step * 4);
        heap.Compactions.Should().Be(2, "兩段短閒置只回收一次");
        heap.Releases.Should().Be(1);

        // 第三個回合馬上又開始（閒置結束）
        activity.Note();
        Advance(time, Step);
        heap.Compactions.Should().Be(2);
        service.Trims.Should().Be(2);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 同一段閒置裡_已提交長回來16MB而且滿一小時才再回收()
    {
        var (time, _, heap, service) = Start();
        Advance(time, Step * 3);
        heap.Compactions.Should().Be(1);

        // 回收在 30 秒那一次檢查；之後心跳的配置讓已提交的空間長回來 16 MB
        heap.Committed += IdleTrimRules.RecompactGrowthBytes;
        Advance(time, TimeSpan.FromMinutes(59) + Step * 5);
        heap.Compactions.Should().Be(1, "距上一次回收 59 分 50 秒：還不到一小時");

        Advance(time, Step);
        heap.Compactions.Should().Be(2, "滿一小時、已提交多了 16 MB");

        Advance(time, TimeSpan.FromHours(2));
        Advance(time, Step);
        heap.Compactions.Should().Be(2, "回收之後沒有再長");
        service.Trims.Should().Be(2);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task 關掉時不排計時器_停止之後不再檢查()
    {
        var (time, _, heap, service) = Start(enabled: false);
        time.Pending.Should().Be(0);
        Advance(time, Step * 10);
        heap.Compactions.Should().Be(0);
        await service.StopAsync(CancellationToken.None);

        var (time2, _, heap2, service2) = Start();
        time2.Pending.Should().Be(1);
        await service2.StopAsync(CancellationToken.None);
        time2.Pending.Should().Be(0, "停止時取消計時器");
        service2.Check();
        heap2.Compactions.Should().Be(0, "停止之後到期的回呼直接丟掉");
    }

    [Fact]
    public async Task 中介程式_一般請求處理中算在InFlight_健康檢查與hub路徑不算()
    {
        var activity = new HubActivity(TimeProvider.System);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new HubActivityMiddleware(_ => gate.Task, activity);

        var version = activity.Version;
        var running = middleware.InvokeAsync(Context("/api/v1/uploads/u1"));
        activity.InFlight.Should().Be(1, "上傳還在收");
        activity.Version.Should().BeGreaterThan(version);
        gate.SetResult();
        await running;
        activity.InFlight.Should().Be(0);

        foreach (var path in new[] { "/healthz", "/readyz", "/hubs/v1/device" })
        {
            version = activity.Version;
            await new HubActivityMiddleware(_ => Task.CompletedTask, activity).InvokeAsync(Context(path));
            activity.Version.Should().Be(version, path);
        }

        var failing = new HubActivityMiddleware(_ => throw new InvalidOperationException("壞掉的端點"), activity);
        await FluentActions.Awaiting(() => failing.InvokeAsync(Context("/api/v1/x"))).Should().ThrowAsync<InvalidOperationException>();
        activity.InFlight.Should().Be(0, "例外也要 End");
    }

    [Fact]
    public async Task hub過濾器_心跳不算_其他呼叫在處理中算在InFlight()
    {
        var activity = new HubActivity(TimeProvider.System);
        var filter = new HubActivityFilter(activity);

        var version = activity.Version;
        (await filter.InvokeMethodAsync(Invocation(nameof(FakeHub.Heartbeat)), _ => ValueTask.FromResult<object?>("ok"))).Should().Be("ok");
        activity.Version.Should().Be(version, "心跳不算活動");

        var gate = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = filter.InvokeMethodAsync(Invocation(nameof(FakeHub.PublishBatch)), _ => new ValueTask<object?>(gate.Task));
        activity.InFlight.Should().Be(1);
        activity.Version.Should().BeGreaterThan(version);
        gate.SetResult("done");
        (await running).Should().Be("done");
        activity.InFlight.Should().Be(0);
    }

    /// <summary>接線：最外層的中介程式與 hub 的全域過濾器記活動、ops/status 有 idleTrims 與 gcCommittedMb</summary>
    [Fact]
    public async Task 測試主機_請求與裝置的交握算活動_健康檢查與心跳不算_ops_status有計數()
    {
        await using var host = await HubTestHost.StartAsync();
        var activity = host.Services.GetRequiredService<HubActivity>();
        using var client = host.CreateClient();

        var version = activity.Version;
        using (var health = await client.GetAsync("/healthz"))
            health.EnsureSuccessStatusCode();
        activity.Version.Should().Be(version, "/healthz 不算");

        await OwnerFlows.LoginOwnerAsync(client);
        activity.Version.Should().BeGreaterThan(version, "登入是一般的請求");
        activity.InFlight.Should().Be(0);

        using var device = await TestDevice.AddAsync(host);
        version = activity.Version;
        await using var hub = await DeviceHubClient.ConnectAsync(host, device.Token);
        activity.Version.Should().Be(version, "WebSocket 連線（/hubs）不算");

        await hub.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
        activity.Version.Should().BeGreaterThan(version, "Hello／Prove 經全域過濾器記成活動");
        activity.InFlight.Should().Be(0);

        version = activity.Version;
        await hub.Connection.InvokeAsync(DeviceHubMethods.Heartbeat, new Heartbeat(host.Clock.NowMs, [], []));
        activity.Version.Should().Be(version, "心跳（已證明、真的進到方法）不算");

        var status = await client.GetJsonAsync("/api/v1/ops/status");
        var process = status.GetProperty("process");
        process.GetProperty("idleTrims").GetInt32().Should().Be(0, "測試主機關掉閒置修剪");
        process.GetProperty("gcCommittedMb").ValueKind.Should().Be(JsonValueKind.Number);
    }

    /// <summary>撥快計時器的時鐘、活動、假的 GC 與服務（已啟動）</summary>
    private static (ManualTimerProvider Time, HubActivity Activity, FakeHeap Heap, IdleTrimService Service) Start(bool enabled = true)
    {
        var time = new ManualTimerProvider();
        var activity = new HubActivity(time);
        var heap = new FakeHeap();
        var options = Options.Create(new HubOptions { IdleTrim = new IdleTrimSettings { Enabled = enabled } });
        var service = new IdleTrimService(activity, heap, time, options, NullLogger<IdleTrimService>.Instance);
        service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return (time, activity, heap, service);
    }

    /// <summary>撥快（大步也只觸發一次到期的計時器；每一步的檢查都排下一次）</summary>
    private static void Advance(ManualTimerProvider time, TimeSpan by)
    {
        while (by > TimeSpan.Zero)
        {
            var step = by < Step ? by : Step;
            time.Advance(step);
            by -= step;
        }
    }

    private static DefaultHttpContext Context(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        return context;
    }

    private static HubInvocationContext Invocation(string method) =>
        new(new FakeCallerContext(), new ServiceCollection().BuildServiceProvider(), new FakeHub(), typeof(FakeHub).GetMethod(method)!, []);

    /// <summary>假的 GC：回收之後已提交的空間回到 <see cref="AfterCompaction"/></summary>
    private sealed class FakeHeap : IHubHeapPort
    {
        public long AfterCompaction { get; set; } = 40 * Mb;

        public long Committed { get; set; } = 120 * Mb;

        public int Compactions { get; private set; }

        public int Releases { get; private set; }

        public bool DlpCompiled { get; set; }

        public long CommittedBytes() => Committed;

        public void ReleaseDlp()
        {
            Releases++;
            DlpCompiled = false;
        }

        public void Compact()
        {
            Compactions++;
            Committed = AfterCompaction;
        }
    }

    /// <summary>過濾器測試用的 hub：方法名稱是 HubInvocationContext.HubMethodName 的來源</summary>
    private sealed class FakeHub : Hub
    {
        public void Heartbeat()
        {
        }

        public void PublishBatch()
        {
        }
    }

    private sealed class FakeCallerContext : HubCallerContext
    {
        public override string ConnectionId => "c1";

        public override string? UserIdentifier => null;

        public override System.Security.Claims.ClaimsPrincipal? User => null;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }
}
