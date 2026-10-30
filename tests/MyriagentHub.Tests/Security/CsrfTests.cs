using System.Net;
using MyriagentHub.Auth;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Security;

/// <summary>SEC-05：CSRF 與跨站 WebSocket：缺 X-Myriagent-Csrf、Origin 不符一律 403（在認證之前就擋）</summary>
public sealed class CsrfTests
{
    [Theory]
    [InlineData(true, null, HttpStatusCode.Forbidden)]
    [InlineData(false, HubTestHost.Origin, HttpStatusCode.Forbidden)]
    [InlineData(true, "https://evil.example", HttpStatusCode.Forbidden)]
    [InlineData(true, "null", HttpStatusCode.Forbidden)]
    [InlineData(true, "https://localhost.evil.example", HttpStatusCode.Forbidden)]
    [InlineData(true, "http://localhost", HttpStatusCode.Forbidden)]
    [InlineData(true, "https://localhost:8443", HttpStatusCode.Forbidden)]
    [InlineData(true, HubTestHost.Origin, HttpStatusCode.OK)]
    [InlineData(true, "https://LOCALHOST:443", HttpStatusCode.OK)]
    public async Task 已登入擁有者的改狀態請求_缺CSRF標頭或Origin不符一律403(bool csrf, string? origin, HttpStatusCode expected)
    {
        await using var owner = await OwnerHost.StartAsync();
        using var client = owner.Host.CreateClient();
        using (var login = await OwnerFlows.LoginAsync(client))
            login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var response = await client.PostAsync("/api/v1/auth/stepup", new { password = OwnerAccount.DefaultPassword }, csrf, origin);
        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.Forbidden)
            (await response.Content.ReadAsStringAsync()).Should().Contain("forbidden_origin");
    }

    [Fact]
    public async Task 匿名端點也一樣檢查_表單送不出自訂標頭()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        var credentials = new { userName = OwnerAccount.UserName, password = OwnerAccount.DefaultPassword };
        using (var noHeader = await client.PostAsync("/api/v1/auth/login", credentials, csrf: false))
            noHeader.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using (var crossSite = await client.PostAsync("/api/v1/auth/login", credentials, origin: "https://evil.example"))
            crossSite.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using (var me = await client.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "被擋下的登入沒有發 cookie");
    }

    [Theory]
    [InlineData("/api/v1/devices/{0}/revoke")]
    [InlineData("/api/v1/devices/{0}/remove")]
    [InlineData("/api/v1/accounts/{0}/delete")]
    public async Task 新端點缺CSRF標頭回403(string template)
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        using var response = await client.PostAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, IdFor(template)), new { reason = "retired" }, csrf: false);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("forbidden_origin", "在認證與近期驗證之前就擋");
    }

    [Theory]
    [InlineData("/api/v1/devices/{0}/revoke", "https://evil.example")]
    [InlineData("/api/v1/devices/{0}/revoke", null)]
    [InlineData("/api/v1/devices/{0}/remove", "https://evil.example")]
    [InlineData("/api/v1/devices/{0}/remove", null)]
    [InlineData("/api/v1/accounts/{0}/delete", "https://evil.example")]
    [InlineData("/api/v1/accounts/{0}/delete", null)]
    public async Task 新端點跨站回403(string template, string? origin)
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        using var response = await client.PostAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, IdFor(template)), new { reason = "retired" }, origin: origin);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("forbidden_origin");
    }

    [Fact]
    public async Task GET不需要CSRF標頭()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        using var response = await client.GetAsync("/api/v1/auth/me");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/hubs/v1/browser/negotiate?negotiateVersion=1", "https://evil.example")]
    [InlineData("/hubs/v1/browser/negotiate?negotiateVersion=1", null)]
    [InlineData("/hubs/v1/browser", "https://evil.example")]
    [InlineData("/hubs/v1/browser", null)]
    [InlineData("/hubs/v1/device/negotiate", "https://evil.example")]
    public async Task 跨站的hub協商與WebSocket升級被拒(string path, string? origin)
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(path.Contains("negotiate") ? HttpMethod.Post : HttpMethod.Get, path);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        if (!path.Contains("negotiate"))
        {
            request.Headers.Add("Connection", "Upgrade");
            request.Headers.Add("Upgrade", "websocket");
            request.Headers.Add("Sec-WebSocket-Version", "13");
            request.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        }
        using var response = await client.Http.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task 同源已登入的瀏覽器hub_negotiate成功()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/hubs/v1/browser/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", HubTestHost.Origin);
        using var response = await client.Http.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "上面的跨站案例被拒是因為 Origin，不是因為沒有登入或端點不存在");
        (await response.Content.ReadAsStringAsync()).Should().Contain("connectionToken");
    }

    [Fact]
    public async Task 裝置hub沒有Origin不擋_交給認證處理()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        using var response = await client.Http.PostAsync("/hubs/v1/device/negotiate", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, ".NET 用戶端不送 Origin；沒有權杖就是 401");
    }

    /// <summary>格式合法、但不存在的 id（被 403 擋下時不會走到查詢；萬一走到也只會是 404，不會是 403）</summary>
    private static string IdFor(string template) =>
        template.Contains("/devices/", StringComparison.Ordinal) ? Myriagent.Sync.SyncIds.NewDeviceId() : Myriagent.Sync.SyncIds.NewLogicalAccountId();

    [Fact]
    public void Origin規則()
    {
        const string self = "https://hub.example.com";
        OriginRules.Check("/api/v1/x", "POST", self, "1", false, self).Should().BeNull();
        OriginRules.Check("/api/v1/x", "POST", "https://hub.example.com:443", "1", false, self).Should().BeNull();
        OriginRules.Check("/api/v1/x", "POST", self, "", false, self).Should().Be("api-csrf-header");
        OriginRules.Check("/api/v1/x", "POST", self, "true", false, self).Should().Be("api-csrf-header");
        OriginRules.Check("/api/v1/x", "DELETE", "https://evil.example", "1", false, self).Should().Be("api-origin");
        OriginRules.Check("/api/v1/x", "PUT", null, "1", false, self).Should().Be("api-origin");
        OriginRules.Check("/api/v1/x", "GET", null, null, false, self).Should().BeNull("GET 一律不改狀態");
        OriginRules.Check("/api/v1/pair/start", "POST", null, null, true, self).Should().BeNull("標記 CsrfExempt 的端點（不用 cookie）");
        OriginRules.Check("/auth.js", "POST", "https://evil.example", null, false, self).Should().BeNull("不是 /api 也不是 /hubs");
        OriginRules.Check("/hubs/v1/browser/negotiate", "POST", null, null, false, self).Should().Be("browser-hub-origin");
        OriginRules.Check("/hubs/v1/browser", "GET", self, null, false, self).Should().BeNull();
        OriginRules.Check("/hubs/v1/device", "GET", null, null, false, self).Should().BeNull();
        OriginRules.Check("/hubs/v1/device", "GET", "https://evil.example", null, false, self).Should().Be("hub-origin");
        OriginRules.IsSameOrigin("https://hub.example.com/", self).Should().BeTrue();
        OriginRules.IsSameOrigin("https://hub.example.com/path", self).Should().BeFalse();
        OriginRules.IsSameOrigin("https://user@hub.example.com", self).Should().BeFalse();
    }
}
