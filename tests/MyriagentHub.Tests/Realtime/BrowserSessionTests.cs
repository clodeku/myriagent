using System.Net;
using MyriagentHub.Auth;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 瀏覽器 hub 連線的工作階段監看（設計 8.2.2、14.6；SEC-04）：撤銷、登出、改密碼、重新登入、reset-owner 時 1 秒內中斷；
/// 撤銷落在「認證完成」與「登記」之間也不漏；巡檢中斷閒置與絕對期限到了的；hub 上的活動延長閒置期限。
/// 巡檢一律由測試手動呼叫（背景巡檢設成每小時一次，不與測試同時跑）
/// </summary>
public sealed class BrowserSessionTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task 登出後1秒內中斷連線()
    {
        await using var host = await StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        using (var logout = await owner.Http.PostAsync("/api/v1/auth/logout"))
            logout.StatusCode.Should().Be(HttpStatusCode.OK);
        await ClosedWithinAsync(owner.Hub, Second);
        await WaitUntilAsync(() => Connections(host).Count == 0);
    }

    [Fact]
    public async Task 撤銷工作階段後1秒內中斷連線()
    {
        await using var host = await StartAsync();
        await using var phone = await LoginAndConnectAsync(host, ip: "198.51.100.31");
        await using var laptop = await LoginAndConnectAsync(host, ip: "198.51.100.32");
        var phoneSession = SessionOf(host, "198.51.100.31");
        using (var stepUp = await OwnerFlows.StepUpAsync(laptop.Http))
            stepUp.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var revoke = await laptop.Http.PostAsync($"/api/v1/sessions/{phoneSession}/revoke"))
            revoke.StatusCode.Should().Be(HttpStatusCode.OK);
        await ClosedWithinAsync(phone.Hub, Second);
        laptop.Hub.Connection.State.Should().Be(HubConnectionState.Connected, "撤銷的是另一個工作階段");
        (await laptop.Hub.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task 改密碼後其他工作階段的連線中斷自己這條保留()
    {
        await using var host = await StartAsync();
        await using var mine = await LoginAndConnectAsync(host, ip: "198.51.100.33");
        await using var other = await LoginAndConnectAsync(host, ip: "198.51.100.34");
        await using var otherTab = await BrowserHubClient.ConnectAsync(host, other.Http);
        using (var change = await OwnerFlows.ChangePasswordAsync(mine.Http, OwnerAccount.DefaultPassword, "changed-password-1"))
            change.StatusCode.Should().Be(HttpStatusCode.OK);
        await ClosedWithinAsync(other.Hub, Second);
        await ClosedWithinAsync(otherTab, Second);
        mine.Hub.Connection.State.Should().Be(HubConnectionState.Connected);
        (await mine.Hub.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task 重新登入後舊工作階段的連線中斷()
    {
        await using var host = await StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var before = SessionOf(host, TestIpStartupFilter.DefaultIp);
        await OwnerFlows.LoginOwnerAsync(owner.Http);
        await ClosedWithinAsync(owner.Hub, Second);

        await using var again = await BrowserHubClient.ConnectAsync(host, owner.Http);
        (await again.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue("新的 cookie 照常連線");
        SessionOf(host, TestIpStartupFilter.DefaultIp).Should().NotBe(before);
    }

    [Fact]
    public async Task 撤銷所有工作階段後全部中斷()
    {
        await using var host = await StartAsync();
        await using var first = await LoginAndConnectAsync(host, ip: "198.51.100.35");
        await using var second = await LoginAndConnectAsync(host, ip: "198.51.100.36");
        (await host.Services.GetRequiredService<SessionService>().RevokeAllAsync()).Should().Be(2, "reset-owner 與還原走這條");
        await ClosedWithinAsync(first.Hub, Second);
        await ClosedWithinAsync(second.Hub, Second);
        await WaitUntilAsync(() => Connections(host).Count == 0);
    }

    [Fact]
    public async Task 撤銷落在認證與登記之間也會中斷()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        var sid = await CurrentSessionAsync(client);
        var connections = Connections(host);

        var monitor = host.Services.GetRequiredService<BrowserSessionMonitor>();
        // 撤銷的事件先到：這時還沒有登記，事件落空
        monitor.OnRevoked(sid);
        // 登記之後再查一次，看到已撤銷。查詢時要已經登記（D8 靠的就是這個順序；委派裡不能直接斷言：例外會被 AdmitAsync 吞掉、當成無效）
        var aborted = 0;
        var registeredAtCheck = false;
        var (outcome, connection) = await connections.AdmitAsync("race-1", sid, null, () => aborted++, (_, _) => Task.CompletedTask,
            _ =>
            {
                registeredAtCheck = connections.Get("race-1") is not null;
                return Task.FromResult(false);
            }, CancellationToken.None);
        registeredAtCheck.Should().BeTrue("先登記、再查工作階段（D8）");
        outcome.Should().Be(AdmitOutcome.SessionInvalid);
        connection.Should().BeNull();
        aborted.Should().Be(1);
        connections.Get("race-1").Should().BeNull();
        connections.Count.Should().Be(0);

        // 查詢本身失敗也不收
        var (failed, _) = await connections.AdmitAsync("race-2", sid, null, () => aborted++, (_, _) => Task.CompletedTask,
            _ => throw new InvalidOperationException("讀不到 hub.db"), CancellationToken.None);
        failed.Should().Be(AdmitOutcome.SessionInvalid);
        connections.Count.Should().Be(0);

        // 對照：有效的工作階段照常登記
        var (ok, admitted) = await connections.AdmitAsync("race-3", sid, null, () => aborted++, (_, _) => Task.CompletedTask,
            ct => host.Services.GetRequiredService<BrowserSessionMonitor>().IsSessionValidAsync(sid, ct), CancellationToken.None);
        ok.Should().Be(AdmitOutcome.Admitted);
        admitted.Should().NotBeNull();
        connections.Remove("race-3");

        // 查詢之後才到的撤銷：那時已登記，由事件中斷
        var abortedBefore = aborted;
        var (late, lateConnection) = await connections.AdmitAsync("race-4", sid, null, () => aborted++, (_, _) => Task.CompletedTask,
            _ =>
            {
                monitor.OnRevoked(sid);
                return Task.FromResult(true);
            }, CancellationToken.None);
        late.Should().Be(AdmitOutcome.Admitted);
        aborted.Should().Be(abortedBefore + 1, "查詢時已登記：查詢之後才到的撤銷事件找得到這條連線");
        lateConnection!.AbortReason.Should().Be(BrowserSessionMonitor.RevokedReason);
        connections.Remove("race-4");
    }

    /// <summary>
    /// 主機層的接線：BrowserHub 傳給 AdmitAsync 的是真的工作階段檢查。撤銷落在 cookie 驗證完成之後、登記之前（以只掛在瀏覽器 hub 上的篩選器在
    /// OnConnectedAsync 之前撤銷：資料庫已改、事件因為還沒登記而落空），只有登記之後那次查詢擋得下
    /// </summary>
    [Fact]
    public async Task 撤銷落在認證與登記之間時主機中斷那條連線()
    {
        await using var host = await HubTestHost.StartAsync(settings: Settings(("Hub:Browser:SessionSweepSeconds", "3600")),
            services: s => s.Configure<HubOptions<BrowserHub>>(o => o.AddFilter(new RevokeOnConnect())));
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);

        await using var hub = await BrowserHubClient.ConnectAsync(host, client);
        await ClosedWithinAsync(hub, Second);
        hub.ServerConnection.Should().BeNull("登記之後查到已撤銷：移除並中斷");
        await WaitUntilAsync(() => Connections(host).Count == 0);
    }

    [Fact]
    public async Task OnRevoked的例外不外洩()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        var sid = await CurrentSessionAsync(client);
        var connections = Connections(host);
        connections.TryAdd("broken-1", sid, null, () => throw new InvalidOperationException("abort 失敗"), (_, _) => Task.CompletedTask)
            .Should().NotBeNull();
        connections.Invoking(c => c.AbortSession(sid, "test")).Should().Throw<AggregateException>("AbortSession 把例外往外丟");

        var monitor = host.Services.GetRequiredService<BrowserSessionMonitor>();
        monitor.Invoking(m => m.OnRevoked(sid)).Should().NotThrow();
        // 登出時 SqliteTicketStore.RemoveAsync 發事件沒有保護：處理常式的例外不能讓登出失敗
        using (var logout = await client.PostAsync("/api/v1/auth/logout"))
            logout.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var me = await client.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "登出照常完成");
        connections.Remove("broken-1");
    }

    [Fact]
    public async Task 閒置逾時由巡檢中斷()
    {
        await using var host = await StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var monitor = host.Services.GetRequiredService<BrowserSessionMonitor>();
        var connection = Connections(host).Snapshot().Single();
        (await monitor.SweepAsync()).Should().Be(0, "還沒到期");
        host.Clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
        (await monitor.SweepAsync()).Should().Be(1);
        await ClosedWithinAsync(owner.Hub, TimeSpan.FromSeconds(5));
        connection.AbortReason.Should().Be(BrowserSessionMonitor.ExpiredReason);
    }

    [Fact]
    public async Task hub活動會延長閒置期限()
    {
        await using var host = await StartAsync();
        await using var owner = await LoginAndConnectAsync(host, ip: "198.51.100.37");
        var sid = SessionOf(host, "198.51.100.37");
        var sessions = host.Services.GetRequiredService<SessionService>();
        var monitor = host.Services.GetRequiredService<BrowserSessionMonitor>();
        var before = (await sessions.FindAsync(sid))!;

        host.Clock.Advance(TimeSpan.FromHours(11));
        (await owner.Hub.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue();
        (await monitor.SweepAsync()).Should().Be(0);
        var after = (await sessions.FindAsync(sid))!;
        after.IdleExpiresAt.Should().BeGreaterThan(before.IdleExpiresAt);
        after.IdleExpiresAt.Should().Be(host.Clock.NowMs + TimeSpan.FromHours(12).Ticks / TimeSpan.TicksPerMillisecond);
        after.LastSeenAt.Should().Be(host.Clock.NowMs);
        after.IpLast.Should().Be("198.51.100.37");

        host.Clock.Advance(TimeSpan.FromHours(2));
        (await monitor.SweepAsync()).Should().Be(0, "登入後 13 小時，但 2 小時前有活動");
        owner.Hub.Connection.State.Should().Be(HubConnectionState.Connected);
        owner.Hub.Closed.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task 沒有活動不延長()
    {
        await using var host = await StartAsync();
        await using var owner = await LoginAndConnectAsync(host, ip: "198.51.100.38");
        var sid = SessionOf(host, "198.51.100.38");
        var sessions = host.Services.GetRequiredService<SessionService>();
        var before = (await sessions.FindAsync(sid))!;
        host.Clock.Advance(TimeSpan.FromHours(11));
        (await host.Services.GetRequiredService<BrowserSessionMonitor>().SweepAsync()).Should().Be(0);
        var after = (await sessions.FindAsync(sid))!;
        after.IdleExpiresAt.Should().Be(before.IdleExpiresAt, "開著連線但沒有呼叫不算活動");
        after.LastSeenAt.Should().Be(before.LastSeenAt);
    }

    [Fact]
    public async Task 絕對期限由巡檢中斷()
    {
        await using var host = await StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var monitor = host.Services.GetRequiredService<BrowserSessionMonitor>();
        var elapsed = TimeSpan.Zero;
        var absolute = TimeSpan.FromDays(7);
        while (true)
        {
            host.Clock.Advance(TimeSpan.FromHours(11));
            elapsed += TimeSpan.FromHours(11);
            (await owner.Hub.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue();
            var aborted = await monitor.SweepAsync();
            if (elapsed < absolute)
            {
                aborted.Should().Be(0, $"登入後 {elapsed.TotalHours} 小時、一直有活動");
                continue;
            }
            aborted.Should().Be(1, "超過 7 天的絕對期限，活動也不能延長");
            break;
        }
        await ClosedWithinAsync(owner.Hub, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TouchAsync對已撤銷的工作階段回false且不寫()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        var sid = await CurrentSessionAsync(client);
        var sessions = host.Services.GetRequiredService<SessionService>();
        var tickets = host.Services.GetRequiredService<SqliteTicketStore>();

        host.Clock.Advance(TimeSpan.FromMinutes(2));
        (await tickets.TouchAsync(sid, "198.51.100.39")).Should().BeTrue("有效的工作階段");
        var touched = (await sessions.FindAsync(sid))!;
        touched.LastSeenAt.Should().Be(host.Clock.NowMs);

        (await sessions.RevokeAsync(sid)).Should().BeTrue();
        host.Clock.Advance(TimeSpan.FromMinutes(2));
        (await tickets.TouchAsync(sid, "198.51.100.40")).Should().BeFalse();
        var after = (await sessions.FindAsync(sid))!;
        after.LastSeenAt.Should().Be(touched.LastSeenAt, "已撤銷的不寫");
        after.IdleExpiresAt.Should().Be(touched.IdleExpiresAt);
        after.IpLast.Should().Be("198.51.100.39");

        (await tickets.TouchAsync("not-a-session", null)).Should().BeFalse();
        (await tickets.TouchAsync(SessionRules.SessionId(new byte[32]), null)).Should().BeFalse("不存在");
    }

    // ───── 工具 ─────

    private static Task<HubTestHost> StartAsync() =>
        HubTestHost.StartAsync(settings: Settings(("Hub:Browser:SessionSweepSeconds", "3600")));

    private static BrowserConnections Connections(HubTestHost host) => host.Services.GetRequiredService<BrowserConnections>();

    /// <summary>以連線時的 IP 找到那條連線的工作階段 id</summary>
    private static string SessionOf(HubTestHost host, string ip) =>
        Connections(host).Snapshot().First(c => c.Ip == ip).SessionId;

    private static async Task<string> CurrentSessionAsync(HubClient client) =>
        (await client.GetJsonAsync("/api/v1/auth/me")).GetProperty("session").GetProperty("id").GetString()!;

    /// <summary>在 BrowserHub.OnConnectedAsync 之前撤銷這條連線的工作階段（cookie 已驗證過）</summary>
    private sealed class RevokeOnConnect : IHubFilter
    {
        public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            await context.ServiceProvider.GetRequiredService<SessionService>().RevokeAsync(BrowserCaller.SessionId(context.Context.User)!);
            await next(context);
        }
    }

    private static async Task ClosedWithinAsync(BrowserHubClient hub, TimeSpan within)
    {
        var winner = await Task.WhenAny(hub.Closed, Task.Delay(within));
        winner.Should().Be(hub.Closed, $"連線要在 {within.TotalMilliseconds} ms 內被中斷");
    }
}
