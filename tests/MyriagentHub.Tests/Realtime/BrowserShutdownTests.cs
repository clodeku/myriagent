using System.Diagnostics;
using System.Net;
using MyriagentHub.Data;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 瀏覽器閘道的停機與韌性（計畫步驟 11；設計 14.6）：停機在時限內乾淨收尾（ApplicationStopping 中斷全部連線、不再登記新連線、
/// 送出幫浦結束）；寫入者停止後 Cmd 回 unavailable、讀取照常；一條連線送出卡住不拖累其他連線；
/// 閘道與巡檢的每一輪丟例外都不讓主機停止（Hub 主機可能同時跑著其他重要服務，.NET 預設 BackgroundService 丟出例外就停主機）
/// </summary>
public sealed class BrowserShutdownTests
{
    private const string AccountsA = """[{"id":"la_a","label":"A"}]""";

    private const string AccountsB = """[{"id":"la_b","label":"B"}]""";

    // ───── 停機 ─────

    [Fact]
    public async Task 停機時5秒內結束且瀏覽器連線被關閉()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        await using var tab = await BrowserHubClient.ConnectAsync(host, owner.Http);
        await owner.Hub.ReadyAsync();
        var servers = new[] { owner.Hub.ServerConnection!, tab.ServerConnection! };
        var connections = host.Services.GetRequiredService<BrowserConnections>();

        var watch = Stopwatch.StartNew();
        await host.App.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        var closed = Task.WhenAll(owner.Hub.Closed, tab.Closed);
        (await Task.WhenAny(closed, Task.Delay(5000))).Should().Be(closed, "瀏覽器連線都被關閉");
        var pumps = Task.WhenAll(servers.Select(s => s.Pump));
        (await Task.WhenAny(pumps, Task.Delay(5000))).Should().Be(pumps, "送出幫浦都結束");
        connections.IsShuttingDown.Should().BeTrue("ApplicationStopping 之後不再登記新連線");
    }

    [Fact]
    public async Task ApplicationStopping時中斷全部連線且不再登記新連線()
    {
        var h = new GatewayHarness();
        var a = await h.ReadyAsync("c1", "s1");
        var b = h.Add("c2", "s2");
        var broken = h.Connections.TryAdd("c3", "s3", null, () => throw new InvalidOperationException("abort 失敗"), (_, _) => Task.CompletedTask)!;
        using var lifetime = new FakeLifetime();
        var wiring = new BrowserGatewayWiring(new SyncNotifier(), new ConnectionRegistry(), h.Gateway, h.Connections, lifetime,
            NullLogger<BrowserGatewayWiring>.Instance);
        await wiring.StartAsync(CancellationToken.None);

        lifetime.Invoking(l => l.StopApplication()).Should().NotThrow("一條連線中斷失敗不打斷其他的停機處理常式");
        foreach (var connection in new[] { a.Connection, b.Connection, broken })
            connection.AbortReason.Should().Be(BrowserConnections.ShutdownReason);
        a.Aborts.Should().Be(1);
        b.Aborts.Should().Be(1);
        var pumps = Task.WhenAll(a.Connection.Pump, b.Connection.Pump, broken.Pump);
        (await Task.WhenAny(pumps, Task.Delay(5000))).Should().Be(pumps, "中斷時停收送出佇列，幫浦隨即結束");
        h.Connections.IsShuttingDown.Should().BeTrue();

        // 停機期間（Kestrel 停止收新連線之前）立刻重連進來的分頁不登記，也不查工作階段
        h.Connections.TryAdd("c4", "s1", null, () => { }, (_, _) => Task.CompletedTask).Should().BeNull();
        var checkedSession = false;
        var (outcome, admitted) = await h.Connections.AdmitAsync("c5", "s1", null, () => { }, (_, _) => Task.CompletedTask, _ =>
        {
            checkedSession = true;
            return Task.FromResult(true);
        }, CancellationToken.None);
        outcome.Should().Be(AdmitOutcome.ShuttingDown);
        admitted.Should().BeNull();
        checkedSession.Should().BeFalse();
        h.Connections.Get("c4").Should().BeNull();
        h.Connections.Get("c5").Should().BeNull();

        await wiring.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 閘道停止時每條連線的送出佇列停收(bool loopRan)
    {
        var h = new GatewayHarness();
        var a = await h.ReadyAsync("c1");
        await h.Gateway.StartAsync(CancellationToken.None);
        try
        {
            if (loopRan)
            {
                // 等背景迴圈真的跑過一輪（讀了一次快照）；false 時立刻停止，迴圈可能還沒開始跑
                Change(h, AccountsA);
                await WaitUntilAsync(() => h.Source.Calls >= 2, because: "背景迴圈讀了快照");
                (await a.NextAsync())["type"]!.Value<string>().Should().Be("accounts");
            }
        }
        finally
        {
            await h.Gateway.StopAsync(CancellationToken.None);
        }
        h.Gateway.ExecuteTask!.IsCompleted.Should().BeTrue();
        (await Task.WhenAny(a.Connection.Pump, Task.Delay(5000))).Should().Be(a.Connection.Pump, "送出幫浦結束");
        a.Connection.Outbox.Enqueue(new ClientOutboxMessage("toast", "{}"));
        a.Connection.Outbox.Count.Should().Be(0, "停收");
        a.Connection.AbortReason.Should().BeNull("佇列停收不中斷連線（連線由 ApplicationStopping 與 SignalR 關閉）");
        h.Gateway.Dispose();
    }

    // ───── 寫入者停止 ─────

    [Fact]
    public async Task 寫入者停止後Cmd回unavailable且ready仍可用()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        host.Services.GetRequiredService<SyncWriter>().Stop();

        var result = await owner.Hub.CmdAsync(new { cmd = "setting", key = "theme", value = "light" });
        result["accepted"]!.Value<bool>().Should().BeFalse();
        result["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Unavailable);

        // 讀取各自開唯讀連線，寫入者停了照樣讀得到
        var data = host.Services.GetRequiredService<AccountViewBuilder>().Read(BrowserParts.All, null, host.Clock.NowMs);
        JObject.Parse(data.SettingsJson)["theme"]!.Value<string>().Should().Be("dark");
        var init = await owner.Hub.ReadyAsync();
        init["settings"]!["theme"]!.Value<string>().Should().Be("dark", "被拒的修改沒有寫入");
        owner.Hub.Closed.IsCompleted.Should().BeFalse("連線保持");
        (await owner.Hub.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue();
    }

    // ───── 慢用戶端 ─────

    [Fact]
    public async Task 一條連線送出卡住不影響其他連線()
    {
        var h = new GatewayHarness(o => o.Browser.SendTimeoutSeconds = 1);
        var stuck = h.Add("c1", "s1");
        stuck.Hang();
        var other = h.Add("c2", "s2");
        var watch = Stopwatch.StartNew();
        h.Gateway.RequestInit(stuck.Connection, "main");
        h.Gateway.RequestInit(other.Connection, "main");
        await h.RunAsync();
        (await other.NextAsync())["type"]!.Value<string>().Should().Be("init");

        Change(h, AccountsA);
        await h.RunAsync();
        (await other.NextAsync())["accounts"]!.ToString(Formatting.None).Should().Be(AccountsA, "卡住的那條不拖累其他連線");

        await WaitUntilAsync(() => stuck.Connection.AbortReason is not null, because: "卡住的那條在送出逾時後被中斷");
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900), "SendTimeoutSeconds（1 秒）之後才中斷");
        stuck.Connection.AbortReason.Should().Be("send_timeout");
        stuck.Aborts.Should().Be(1);
        (await Task.WhenAny(stuck.Connection.Pump, Task.Delay(5000))).Should().Be(stuck.Connection.Pump, "卡住的幫浦結束");

        Change(h, AccountsB);
        await h.RunAsync();
        (await other.NextAsync())["accounts"]!.ToString(Formatting.None).Should().Be(AccountsB, "中斷之後其他連線照常");
        other.Connection.AbortReason.Should().BeNull();
        other.Aborts.Should().Be(0);
    }

    // ───── 背景迴圈的例外 ─────

    [Fact]
    public async Task 閘道一輪丟例外不會讓主機停止()
    {
        var source = new FakeSnapshotSource { Throw = true };
        await using var host = await HubTestHost.StartAsync(services: s => s.AddSingleton<IBrowserSnapshotSource>(source));
        var gateway = host.Services.GetRequiredService<BrowserGateway>();
        await using var owner = await LoginAndConnectAsync(host);
        (await owner.Hub.CmdAsync(new { cmd = "ready", mode = "main" }))["accepted"]!.Value<bool>().Should().BeTrue();

        await WaitUntilAsync(() => source.Calls >= 2, because: "讀取失敗後退避重試");
        gateway.ExecuteTask!.IsCompleted.Should().BeFalse("閘道的迴圈沒有結束");
        host.App.Lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse("主機沒有停止");
        using (var health = await owner.Http.GetAsync("/healthz"))
            health.StatusCode.Should().Be(HttpStatusCode.OK);
        owner.Hub.Drain().Should().NotContain(m => (string?)m["type"] == "init", "還沒有快照");
        owner.Hub.Closed.IsCompleted.Should().BeFalse();

        source.Throw = false;
        var init = await owner.Hub.WaitForAsync("init", timeoutMs: 15_000);
        init["remote"]!["mode"]!.Value<string>().Should().Be("browser", "恢復後照常送 init");
        gateway.ExecuteTask.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task 閘道一輪在讀快照之外丟例外由最外層攔截()
    {
        var h = new GatewayHarness();
        var a = h.Add("c1");
        h.Time.FailWith = new InvalidOperationException("假的時鐘故障");
        await h.Gateway.StartAsync(CancellationToken.None);
        try
        {
            h.Gateway.RequestInit(a.Connection, "main");
            await WaitUntilAsync(() => h.Time.Failures >= 1, because: "背景迴圈跑了一輪而且丟出");
            await h.Gateway.Awaiting(g => g.RunOnceAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>("這一輪不經讀快照的退避，直接丟出");
            h.Gateway.ExecuteTask!.IsCompleted.Should().BeFalse("每一輪的例外由最外層攔截，迴圈繼續");
            h.Source.Calls.Should().Be(0);

            h.Time.FailWith = null;
            (await a.NextAsync())["type"]!.Value<string>().Should().Be("init", "退避 1 秒後的下一輪恢復");
            h.Gateway.ExecuteTask.IsCompleted.Should().BeFalse();
        }
        finally
        {
            await h.Gateway.StopAsync(CancellationToken.None);
            h.Gateway.Dispose();
        }
    }

    [Fact]
    public async Task 巡檢一輪丟例外不會讓主機停止()
    {
        // 背景巡檢每秒一輪（真實時間）；讀取失敗以「hub.db 暫時沒有 browser_sessions 表」造成（真的 SqliteException，不改正式程式）
        await using var host = await HubTestHost.StartAsync(settings: Settings(("Hub:Browser:SessionSweepSeconds", "1")));
        await using var owner = await LoginAndConnectAsync(host);
        var monitor = host.Services.GetRequiredService<BrowserSessionMonitor>();
        var server = owner.Hub.ServerConnection!;
        const string hidden = "browser_sessions_hidden";

        RenameTable(host, "browser_sessions", hidden);
        try
        {
            await monitor.Awaiting(m => m.SweepAsync()).Should().ThrowAsync<SqliteException>("這段期間巡檢讀不到工作階段");
            // 閒置到期：巡檢一恢復就會中斷；讀取失敗的那幾輪不做判斷
            host.Clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
            await Task.Delay(TimeSpan.FromMilliseconds(2500));
            monitor.ExecuteTask!.IsCompleted.Should().BeFalse("巡檢的迴圈沒有結束");
            host.App.Lifetime.ApplicationStopping.IsCancellationRequested.Should().BeFalse("主機沒有停止");
            owner.Hub.Closed.IsCompleted.Should().BeFalse("讀取失敗的那一輪不中斷連線（下一輪再試）");
        }
        finally
        {
            RenameTable(host, hidden, "browser_sessions");
        }

        (await Task.WhenAny(owner.Hub.Closed, Task.Delay(5000))).Should().Be(owner.Hub.Closed, "恢復後的下一輪巡檢中斷閒置到期的連線");
        server.AbortReason.Should().Be(BrowserSessionMonitor.ExpiredReason);
        monitor.ExecuteTask.IsCompleted.Should().BeFalse();
    }

    // ───── 工具 ─────

    private static void Change(GatewayHarness h, string accountsJson)
    {
        h.Source.Current = h.Source.Current with { AccountsJson = accountsJson };
        h.Gateway.MarkDirty(BrowserParts.Accounts);
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
    }

    /// <summary>直接改 hub.db 的表名（測試主機的資料根；連線字串一律由 SqliteSetup 產生）</summary>
    private static void RenameTable(HubTestHost host, string from, string to)
    {
        using var connection = SqliteSetup.Open(host.Paths.HubDb);
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE \"{from}\" RENAME TO \"{to}\"";
        command.ExecuteNonQuery();
    }

    /// <summary>可以手動觸發 ApplicationStopping 的主機生命週期</summary>
    private sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => stopping.Cancel();

        public void Dispose() => stopping.Dispose();
    }
}
