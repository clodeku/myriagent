using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 瀏覽器 hub 的傳輸、認證與上限（設計 14.6；web-protocol 第六節第 3、4、6、11 點）：只收擁有者 cookie、只開 WebSockets、
/// 不開 stateful reconnect、連線上限（每個工作階段 8、全部 32）、Cmd 與 ClientState 的速率（以工作階段分區）、negotiate 的速率、
/// 單則 128 KB（WP4.5 D32）、方法允許清單
/// </summary>
public sealed class BrowserHubTests
{
    [Fact]
    public async Task 未登入連不上瀏覽器hub()
    {
        await using var host = await HubTestHost.StartAsync();
        using var anonymous = host.CreateClient(cookies: false);
        using (var negotiate = await NegotiateAsync(anonymous))
            negotiate.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await FluentActions.Awaiting(() => BrowserHubClient.ConnectAsync(host, null)).Should().ThrowAsync<Exception>("沒有 cookie 的升級是 401");
        host.Services.GetRequiredService<BrowserConnections>().Count.Should().Be(0);
    }

    [Fact]
    public async Task 裝置權杖連不上瀏覽器hub()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        using var client = host.CreateClient(cookies: false);
        using (var request = new HttpRequestMessage(HttpMethod.Post, SyncProtocol.BrowserHubPath + "/negotiate?negotiateVersion=1"))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.Token);
            request.Headers.Add("Origin", HubTestHost.Origin);
            using var negotiate = await client.Http.SendAsync(request);
            negotiate.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "Owner 政策只用 cookie 方案（設計 8.3）");
        }
        await FluentActions.Awaiting(() => BrowserHubClient.ConnectAsync(host, null, bearer: device.Token)).Should().ThrowAsync<Exception>();
        host.Services.GetRequiredService<BrowserConnections>().Count.Should().Be(0);
    }

    [Fact]
    public async Task 同源且已登入可以連線()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        owner.Hub.Connection.State.Should().Be(HubConnectionState.Connected);
        var connections = host.Services.GetRequiredService<BrowserConnections>();
        connections.Count.Should().Be(1);
        connections.Snapshot().Single().Ip.Should().Be(TestIpStartupFilter.DefaultIp);
        (await owner.Hub.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task negotiate只提供WebSockets且不開stateful_reconnect()
    {
        await using var host = await HubTestHost.StartAsync();
        using var owner = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(owner);
        using var response = await NegotiateAsync(owner, "&useStatefulReconnect=true");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var json = JsonDocument.Parse(text);
        json.RootElement.GetProperty("availableTransports").EnumerateArray().Select(t => t.GetProperty("transport").GetString())
            .Should().Equal(["WebSockets"]);
        (json.RootElement.TryGetProperty("useStatefulReconnect", out var stateful) && stateful.GetBoolean()).Should().BeFalse(text);
    }

    [Fact]
    public async Task hub與傳輸設定()
    {
        var hub = new Microsoft.AspNetCore.SignalR.HubOptions<BrowserHub>();
        BrowserRegistration.ConfigureHub(hub);
        AssertHub(hub);

        var dispatcher = new HttpConnectionDispatcherOptions();
        BrowserRegistration.ConfigureDispatcher(dispatcher);
        AssertDispatcher(dispatcher);

        // 主機裡實際生效的設定（AddHubOptions 與 MapHub 真的套用了）
        await using var host = await HubTestHost.StartAsync();
        AssertHub(host.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.SignalR.HubOptions<BrowserHub>>>().Value);
        var mapped = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(SyncProtocol.BrowserHubPath, StringComparison.Ordinal) == true)
            .Select(e => e.Metadata.GetMetadata<HttpConnectionDispatcherOptions>())
            .OfType<HttpConnectionDispatcherOptions>()
            .ToList();
        mapped.Should().NotBeEmpty("negotiate 端點帶著實際的傳輸設定");
        foreach (var options in mapped)
            AssertDispatcher(options);

        static void AssertHub(Microsoft.AspNetCore.SignalR.HubOptions<BrowserHub> o)
        {
            o.MaximumReceiveMessageSize.Should().Be(128 * 1024, "WP4.5 D32；transport-hub.js 的 MAX_FRAME_BYTES 同值");
            o.MaximumParallelInvocationsPerClient.Should().Be(1);
            o.KeepAliveInterval.Should().Be(TimeSpan.FromSeconds(15));
            o.ClientTimeoutInterval.Should().Be(TimeSpan.FromSeconds(30));
            o.EnableDetailedErrors.Should().BeFalse();
            o.SupportedProtocols.Should().Equal(["json"]);
        }

        static void AssertDispatcher(HttpConnectionDispatcherOptions o)
        {
            o.Transports.Should().Be(HttpTransportType.WebSockets);
            o.AllowStatefulReconnects.Should().BeFalse();
            o.CloseOnAuthenticationExpiration.Should().BeFalse("以系統時鐘比對、與注入的 TimeProvider 不一致（設計 14.6）");
            o.ApplicationMaxBufferSize.Should().Be(128 * 1024);
            o.TransportMaxBufferSize.Should().Be(128 * 1024);
        }
    }

    [Fact]
    public async Task 每個工作階段最多8條連線()
    {
        await using var host = await HubTestHost.StartAsync();
        using var owner = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(owner);
        var open = new List<BrowserHubClient>();
        try
        {
            for (var i = 0; i < 8; i++)
                open.Add(await BrowserHubClient.ConnectAsync(host, owner));
            var connections = host.Services.GetRequiredService<BrowserConnections>();
            connections.Count.Should().Be(8);

            await ExpectTooManyConnectionsAsync(host, owner);
            connections.Count.Should().Be(8);

            await open[0].DisposeAsync();
            open.RemoveAt(0);
            await WaitUntilAsync(() => connections.Count == 7);
            open.Add(await BrowserHubClient.ConnectAsync(host, owner));
            (await open[^1].CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue("關掉一條之後可以再連");
        }
        finally
        {
            foreach (var client in open)
                await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task 全部最多32條連線()
    {
        await using var host = await HubTestHost.StartAsync(settings: Settings(("Hub:Browser:MaxConnections", "3")));
        await using var first = await LoginAndConnectAsync(host);
        await using var second = await LoginAndConnectAsync(host, ip: "198.51.100.20");
        await using var third = await BrowserHubClient.ConnectAsync(host, first.Http);
        await ExpectTooManyConnectionsAsync(host, second.Http);
        host.Services.GetRequiredService<BrowserConnections>().Count.Should().Be(3);
    }

    [Fact]
    public async Task Cmd超過突發上限回rate_limited()
    {
        await using var host = await HubTestHost.StartAsync(settings: Settings(("Hub:RateLimits:Enabled", "true")));
        await using var owner = await LoginAndConnectAsync(host);
        var commandId = SyncIds.NewUlid(host.Clock.NowMs);
        for (var i = 0; i < 50; i++)
            (await owner.Hub.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue($"第 {i + 1} 次在突發上限內");
        var limited = await owner.Hub.CmdAsync(new { cmd = "layout" }, commandId);
        limited["accepted"]!.Value<bool>().Should().BeFalse();
        limited["error"]!.Value<string>().Should().Be(BrowserErrorCodes.RateLimited);
        limited["commandId"]!.Value<string>().Should().Be(commandId, "commandId 照樣帶回");
        await FluentActions.Awaiting(() => owner.Hub.Connection.InvokeAsync(BrowserHubMethods.ClientState, ValidState()))
            .Should().ThrowAsync<HubException>().WithMessage("*rate_limited*", "ClientState 與 Cmd 共用額度");

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 20; i++)
            (await owner.Hub.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue($"一秒後補 20 次（第 {i + 1} 次）");
        (await owner.Hub.CmdAsync(new { cmd = "layout" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.RateLimited);
        owner.Hub.Connection.State.Should().Be(HubConnectionState.Connected, "限流不中斷連線");
    }

    [Fact]
    public async Task 另一個工作階段不受影響()
    {
        await using var host = await HubTestHost.StartAsync(settings: Settings(("Hub:RateLimits:Enabled", "true")));
        await using var busy = await LoginAndConnectAsync(host);
        await using var other = await LoginAndConnectAsync(host);
        for (var i = 0; i < 50; i++)
            await busy.Hub.CmdAsync(new { cmd = "layout" });
        (await busy.Hub.CmdAsync(new { cmd = "layout" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.RateLimited);
        for (var i = 0; i < 50; i++)
            (await other.Hub.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue("以工作階段分區，不以 IP");
    }

    [Fact]
    public async Task RateLimits停用時不限制Cmd()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        for (var i = 0; i < 120; i++)
            (await owner.Hub.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task negotiate速率匿名以IP已登入以工作階段()
    {
        await using var host = await HubTestHost.StartAsync(settings: Settings(
            ("Hub:RateLimits:Enabled", "true"),
            ("Hub:RateLimits:BrowserHubAnonymousPerMinute", "2")));
        const string ip = "198.51.100.30";
        using var anonymous = host.CreateClient(ip: ip, cookies: false);
        for (var i = 0; i < 2; i++)
        {
            using var response = await NegotiateAsync(anonymous);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        using (var limited = await NegotiateAsync(anonymous))
        {
            limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            limited.Headers.Contains("Retry-After").Should().BeTrue();
            (await limited.Content.ReadAsStringAsync()).Should().Contain("\"error\":\"rate_limited\"");
        }

        using var owner = host.CreateClient(ip: ip);
        await OwnerFlows.LoginOwnerAsync(owner);
        using (var signedIn = await NegotiateAsync(owner))
            signedIn.StatusCode.Should().Be(HttpStatusCode.OK, "已登入的以工作階段分區，不吃同一個 IP 的匿名額度");
        await using var hub = await BrowserHubClient.ConnectAsync(host, owner);
        hub.Connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task negotiate已登入以工作階段分區的額度()
    {
        await using var host = await HubTestHost.StartAsync(settings: Settings(
            ("Hub:RateLimits:Enabled", "true"),
            ("Hub:RateLimits:BrowserHubPerSessionPerMinute", "2")));
        const string ip = "198.51.100.41";
        using var owner = host.CreateClient(ip: ip);
        await OwnerFlows.LoginOwnerAsync(owner);
        for (var i = 0; i < 2; i++)
        {
            using var response = await NegotiateAsync(owner);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"第 {i + 1} 次在額度內");
        }
        using (var limited = await NegotiateAsync(owner))
        {
            limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "已登入的工作階段也有上限（不是不限）");
            limited.Headers.Contains("Retry-After").Should().BeTrue();
            (await limited.Content.ReadAsStringAsync()).Should().Contain("\"error\":\"rate_limited\"");
        }

        using var other = host.CreateClient(ip: ip);
        await OwnerFlows.LoginOwnerAsync(other);
        using (var fresh = await NegotiateAsync(other))
            fresh.StatusCode.Should().Be(HttpStatusCode.OK, "以工作階段分區、不以 IP：同一個 IP 的另一個工作階段照常");
    }

    /// <summary>
    /// 權杖桶不隨最後一條連線丟掉（web-protocol 第六節第 11 點的突發上限）：關掉再重連沿用同一個桶；
    /// 沒有連線而且補滿之後，工作階段監看的巡檢才丟掉它（補滿的桶等同新建的）
    /// </summary>
    [Fact]
    public async Task 關掉連線再重連不會拿到新的滿桶()
    {
        await using var host = await HubTestHost.StartAsync(settings: Settings(("Hub:RateLimits:Enabled", "true"), ("Hub:Browser:SessionSweepSeconds", "3600")));
        using var owner = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(owner);
        var connections = host.Services.GetRequiredService<BrowserConnections>();
        var monitor = host.Services.GetRequiredService<BrowserSessionMonitor>();
        await using (var first = await BrowserHubClient.ConnectAsync(host, owner))
        {
            for (var i = 0; i < 50; i++)
                (await first.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue($"第 {i + 1} 次在突發上限內");
            (await first.CmdAsync(new { cmd = "layout" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.RateLimited);
        }
        await WaitUntilAsync(() => connections.Count == 0);
        connections.BucketCount.Should().Be(1, "最後一條連線移除時不丟桶");

        await using (var second = await BrowserHubClient.ConnectAsync(host, owner))
            (await second.CmdAsync(new { cmd = "layout" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.RateLimited, "重連沿用同一個桶，不是新的滿桶");
        await WaitUntilAsync(() => connections.Count == 0);

        await monitor.SweepAsync();
        connections.BucketCount.Should().Be(1, "還沒補滿的不丟");
        host.Clock.Advance(TimeSpan.FromSeconds(3));
        await monitor.SweepAsync();
        connections.BucketCount.Should().Be(0, "沒有連線而且補滿的在巡檢時丟掉");

        await using var third = await BrowserHubClient.ConnectAsync(host, owner);
        for (var i = 0; i < 50; i++)
            (await third.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue($"丟掉的桶等同新建的（第 {i + 1} 次）");
        (await third.CmdAsync(new { cmd = "layout" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.RateLimited);
    }

    [Fact]
    public async Task 單則128KB以內的Cmd框照收_超過就關閉連線()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        // 上限內的 send：16,000 個要跳脫成六個字元的控制字元，呼叫框約 96 KB（超過舊的 64 KB，WP4.5 D32）
        var text = new string((char)1, RemoteCommandShapeRules.MaxAgentTextLength);
        Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(text)).Should().BeGreaterThan(96_000, "夾具：跳脫後的字串本身就超過 64 KB");
        // 網頁一律帶 commandId；c1 不是線上對話 id，指令路由照舊回 not_available（WP4.5 步 6）
        var within = await owner.Hub.CmdAsync(new { cmd = "send", conversationId = "c1", text }, commandId: "01J9Q0000000000000000000AD");
        within["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable, "框照收、照分類回（c1 不是線上對話 id）");
        owner.Hub.Connection.State.Should().Be(HubConnectionState.Connected);
        (await owner.Hub.CmdAsync(new { cmd = "layout" }))["accepted"]!.Value<bool>().Should().BeTrue("同一條連線照常可用");

        try
        {
            await owner.Hub.CmdAsync(new { cmd = "layout", layout = new string('a', 140 * 1024) });
        }
        catch (Exception)
        {
            // 連線被關掉時呼叫失敗
        }
        (await Task.WhenAny(owner.Hub.Closed, Task.Delay(5000))).Should().Be(owner.Hub.Closed, "超過 MaximumReceiveMessageSize 的訊息讓伺服器關掉整條連線");
        await WaitUntilAsync(() => host.Services.GetRequiredService<BrowserConnections>().Count == 0);
    }

    [Fact]
    public async Task 不准呼叫的方法被拒()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        await FluentActions.Awaiting(() => owner.Hub.Connection.InvokeAsync<object>("Foo")).Should().ThrowAsync<HubException>();
        await FluentActions.Awaiting(() => owner.Hub.Connection.InvokeAsync<object>("OnConnectedAsync")).Should().ThrowAsync<HubException>();
        owner.Hub.Connection.State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task ClientState不合格丟invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var bad = ValidState();
        bad["visible"] = "true";
        await FluentActions.Awaiting(() => owner.Hub.Connection.InvokeAsync(BrowserHubMethods.ClientState, bad))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid*");
        var connection = host.Services.GetRequiredService<BrowserConnections>().Snapshot().Single();
        connection.State.Should().BeNull("不合格的不保存");

        await owner.Hub.Connection.InvokeAsync(BrowserHubMethods.ClientState, ValidState());
        connection.State.Should().NotBeNull();
        connection.State!.TzOffsetMinutes.Should().Be(-480);
        connection.State.OpenConversationIds.Should().Equal(["c1"]);
    }

    [Fact]
    public async Task ready回accepted()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var commandId = SyncIds.NewUlid(host.Clock.NowMs);
        var result = await owner.Hub.CmdAsync(new { cmd = "ready", mode = "main" }, commandId);
        result["accepted"]!.Value<bool>().Should().BeTrue();
        result["commandId"]!.Value<string>().Should().Be(commandId);
        result.ContainsKey("error").Should().BeFalse("null 欄位省略");
        (await owner.Hub.WaitForAsync("init"))["mode"]!.Value<string>().Should().Be("main", "init 由閘道的迴圈送出");
    }

    [Fact]
    public async Task 指令照分類回代碼()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        (await owner.Hub.CmdAsync(new { cmd = "setting", key = "theme", value = "light" }))["accepted"]!.Value<bool>().Should().BeTrue("伺服器處理的修改");
        (await owner.Hub.CmdAsync(new { cmd = "takeover", conversationId = "x" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable, "第 5 階段的指令");
        (await owner.Hub.CmdAsync(new { cmd = "openFolder" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.DesktopOnly);
        (await owner.Hub.CmdAsync(new { cmd = "Ready" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid);
    }

    // ───── 工具 ─────

    private static async Task<HttpResponseMessage> NegotiateAsync(HubClient client, string query = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SyncProtocol.BrowserHubPath + "/negotiate?negotiateVersion=1" + query);
        request.Headers.Add("Origin", HubTestHost.Origin);
        return await client.Http.SendAsync(request);
    }

    /// <summary>超過上限的連線：連線建立後立刻以 HubException: too_many_connections 關閉（或在交握途中就收到關閉）</summary>
    private static async Task ExpectTooManyConnectionsAsync(HubTestHost host, HubClient owner)
    {
        BrowserHubClient? rejected = null;
        try
        {
            rejected = await BrowserHubClient.ConnectAsync(host, owner);
        }
        catch (Exception ex)
        {
            ex.ToString().Should().Contain(BrowserErrorCodes.TooManyConnections);
            return;
        }
        await using (rejected)
        {
            (await Task.WhenAny(rejected.Closed, Task.Delay(5000))).Should().Be(rejected.Closed, "超過上限的連線被關閉");
            var error = await rejected.Closed;
            error.Should().NotBeNull();
            error!.Message.Should().Contain(BrowserErrorCodes.TooManyConnections);
        }
    }

    private static JObject ValidState() => new()
    {
        ["visible"] = true,
        ["focused"] = false,
        ["openConversationIds"] = new JArray("c1"),
        ["tzOffsetMinutes"] = -480,
    };
}
