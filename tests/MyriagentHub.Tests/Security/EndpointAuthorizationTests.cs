using System.Net;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;

namespace MyriagentHub.Tests.Security;

/// <summary>SEC-01：除匿名允許清單外，所有端點都要認證；列舉 EndpointDataSource 斷言，再逐一以未認證請求確認 401</summary>
public sealed class EndpointAuthorizationTests
{
    /// <summary>
    /// 匿名允許清單（8.3）：靜態網頁（不是端點）、/healthz、帳號密碼登入（設計 14.4；限流與退避）、
    /// 裝置配對的 start／token（第 3 階段；限流、deviceCode 只存雜湊），
    /// 以及其他未對應路徑一律回 404 的 catch-all（MapFallback，沒有方法限制；列舉時當成 GET）
    /// </summary>
    private static readonly HashSet<string> AnonymousAllowList =
    [
        "GET /{**path}",
        "GET /healthz",
        "POST /api/v1/auth/login",
        "POST /api/v1/pair/start",
        "POST /api/v1/pair/token",
    ];

    [Fact]
    public async Task 列舉所有端點_只有允許清單可以匿名_其他都有授權政策()
    {
        await using var host = await HubTestHost.StartAsync();
        var endpoints = Endpoints(host);
        endpoints.Should().NotBeEmpty();

        var anonymous = endpoints.Where(e => e.AllowAnonymous).Select(e => e.Key).ToHashSet();
        anonymous.Should().BeEquivalentTo(AnonymousAllowList, "匿名端點必須正好是允許清單（多了是漏洞、少了是清單過期）");
        foreach (var endpoint in endpoints.Where(e => !e.AllowAnonymous))
            endpoint.HasAuthorize.Should().BeTrue($"{endpoint.Key} 沒有授權政策");
    }

    [Fact]
    public async Task 未帶憑證逐一請求每個非匿名端點_一律401()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        foreach (var endpoint in Endpoints(host).Where(e => !e.AllowAnonymous))
        {
            var path = "/" + string.Join('/', endpoint.Pattern.Trim('/').Split('/').Select(s => s.StartsWith('{') ? "x" : s));
            foreach (var method in endpoint.Methods)
            {
                using var response = await client.SendAsync(new HttpMethod(method), path, method == "GET" ? null : new { });
                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {path}");
            }
        }
    }

    [Theory]
    [InlineData("/api/v1/nope")]
    [InlineData("/api/v2/devices")]
    [InlineData("/hubs/v1/device/negotiate")]
    [InlineData("/m/abc")]
    [InlineData("/t/abc")]
    [InlineData("/f/abc")]
    public async Task 未對應的受保護路徑_匿名一律401不洩漏端點是否存在(string path)
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("GET", "/favicon.ico")]
    [InlineData("GET", "/robots.txt")]
    [InlineData("GET", "/no-such-page")]
    [InlineData("GET", "/healthz/extra")]
    [InlineData("GET", "/apple-touch-icon.png")]
    [InlineData("POST", "/no-such-page")]
    public async Task 其他未對應路徑_不論是否登入一律404(string method, string path)
    {
        await using var owner = await OwnerHost.StartAsync();
        using var anonymous = owner.Host.CreateClient(cookies: false);
        using (var response = await anonymous.SendAsync(new HttpMethod(method), path, method == "POST" ? new { } : null))
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"匿名 {method} {path}");

        using var client = owner.Host.CreateClient();
        using (var login = await OwnerFlows.LoginAsync(client))
            login.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var response = await client.SendAsync(new HttpMethod(method), path, method == "POST" ? new { } : null))
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"登入後 {method} {path}");
    }

    [Fact]
    public async Task 靜態登入頁與healthz不需要登入()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        using (var page = await client.GetAsync("/"))
        {
            page.StatusCode.Should().Be(HttpStatusCode.OK);
            (await page.Content.ReadAsStringAsync()).Should().Contain("<meta name=\"myriagenthub-page\" content=\"auth\">");
        }
        using (var health = await client.GetAsync("/healthz"))
            health.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record EndpointInfo(string Pattern, IReadOnlyList<string> Methods, bool AllowAnonymous, bool HasAuthorize)
    {
        public string Key => string.Join(",", Methods) + " " + Pattern;
    }

    private static List<EndpointInfo> Endpoints(HubTestHost host)
    {
        var source = host.Services.GetRequiredService<EndpointDataSource>();
        return source.Endpoints.OfType<RouteEndpoint>()
            .Select(e => new EndpointInfo(
                "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/'),
                e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.ToList() ?? ["GET"],
                e.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0))
            .ToList();
    }
}
