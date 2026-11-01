using System.Net;
using Myriagent.Media;
using MyriagentHub.Hosting;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.Http;

namespace MyriagentHub.Tests.Security;

/// <summary>媒體路徑的 CSP（WP4.7 計畫 D9；設計 4.10:1062、SEC-23 的伺服器端保證）：依路徑決定，端點不能自己放寬</summary>
public sealed class SecurityHeaderRulesTests
{
    private static readonly string Web = SecurityHeaderRules.ContentSecurityPolicy("https://localhost");

    [Theory]
    [InlineData("/m/x")]
    [InlineData("/t/x")]
    [InlineData("/f/x")]
    [InlineData("/m")]
    [InlineData("/M/x")]
    [InlineData("/api/v1/device/media/x")]
    [InlineData("/API/V1/DEVICE/MEDIA/x")]
    public void 媒體路徑是媒體CSP(string path)
        => SecurityHeaderRules.ContentSecurityPolicyFor(new PathString(path), Web).Should().Be(MediaHeaderRules.MediaCsp);

    [Theory]
    [InlineData("/")]
    [InlineData("/app/")]
    [InlineData("/mx")]
    [InlineData("/media")]
    [InlineData("/favicon.ico")]
    [InlineData("/thumbs/x")]
    [InlineData("/api/v1/device/blobs/x")]
    [InlineData("/api/v1/device/mediax")]
    [InlineData("/api/v1/uploads/x")]
    public void 其他路徑照網頁的CSP(string path)
        => SecurityHeaderRules.ContentSecurityPolicyFor(new PathString(path), Web).Should().Be(Web);

    [Theory]
    [InlineData("/f/x", "application/pdf", "SAMEORIGIN")]
    [InlineData("/f/x", "Application/PDF; charset=binary", "SAMEORIGIN")]
    [InlineData("/m/x", "application/pdf", "SAMEORIGIN")]
    [InlineData("/f/x", "application/octet-stream", "DENY")]
    [InlineData("/f/x", "text/plain; charset=utf-8", "DENY")]
    [InlineData("/f/x", null, "DENY")]
    [InlineData("/app/x.pdf", "application/pdf", "DENY")]
    [InlineData("/", "text/html", "DENY")]
    public void 只有媒體端點回的PDF准同源框住(string path, string? contentType, string expected)
        => SecurityHeaderRules.FrameOptionsFor(new PathString(path), contentType).Should().Be(expected);

    [Fact]
    public void 媒體CSP不載入任何東西也不執行腳本()
    {
        MediaHeaderRules.MediaCsp.Should().Be("default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox");
        MediaHeaderRules.MediaCsp.Should().NotContain("script-src");
    }

    [Theory]
    [InlineData("/m/x")]
    [InlineData("/t/x")]
    [InlineData("/f/x")]
    [InlineData("/api/v1/device/media/x")]
    public async Task 媒體路徑的回應帶媒體CSP與nosniff_匿名也是(string path)
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.GetValues("Content-Security-Policy").Single().Should().Be(MediaHeaderRules.MediaCsp);
        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        response.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
    }
}
