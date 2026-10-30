using System.Net;
using System.Net.Http.Headers;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Tests.Auth;

/// <summary>裝置權杖骨架（第 3 階段才有配對）：只收 Authorization 標頭、每次查資料庫、撤銷立即生效、輪換後舊權杖保留 24 小時</summary>
public sealed class DeviceTokenTests
{
    [Fact]
    public void 權杖格式與標頭解析()
    {
        var token = DeviceTokenRules.NewToken();
        token.Should().StartWith("chd_").And.HaveLength(4 + 43);
        DeviceTokenRules.IsWellFormed(token).Should().BeTrue();
        DeviceTokenRules.IsWellFormed(token + "x").Should().BeFalse();
        DeviceTokenRules.IsWellFormed("cha_" + token[4..]).Should().BeFalse();
        DeviceTokenRules.IsWellFormed("chd_" + new string('+', 43)).Should().BeFalse("只能是 base64url 字元");

        DeviceTokenRules.TryReadBearer(["Bearer " + token], out var read).Should().BeTrue();
        read.Should().Be(token);
        DeviceTokenRules.TryReadBearer(["bearer " + token], out _).Should().BeTrue();
        DeviceTokenRules.TryReadBearer(["Basic abc"], out _).Should().BeFalse();
        DeviceTokenRules.TryReadBearer(["Bearer a b"], out _).Should().BeFalse();
        DeviceTokenRules.TryReadBearer(["Bearer " + token, "Bearer " + token], out _).Should().BeFalse("多個 Authorization 標頭不認");
        DeviceTokenRules.TryReadBearer([], out _).Should().BeFalse();
    }

    [Fact]
    public void 權杖比對_舊權杖24小時內有效_撤銷後一律無效()
    {
        var current = DeviceTokenRules.NewToken();
        var previous = DeviceTokenRules.NewToken();
        var device = Device(current);
        device.PrevTokenHash = DeviceTokenRules.Hash(previous);
        device.PrevTokenExpires = 2_000;
        DeviceTokenRules.Matches(device, DeviceTokenRules.Hash(current), 1_000).Should().BeTrue();
        DeviceTokenRules.Matches(device, DeviceTokenRules.Hash(previous), 1_999).Should().BeTrue();
        DeviceTokenRules.Matches(device, DeviceTokenRules.Hash(previous), 2_000).Should().BeFalse();
        DeviceTokenRules.Matches(device, DeviceTokenRules.Hash(DeviceTokenRules.NewToken()), 1_000).Should().BeFalse();
        device.RevokedAt = 1;
        DeviceTokenRules.Matches(device, DeviceTokenRules.Hash(current), 1_000).Should().BeFalse();
    }

    [Fact]
    public async Task 有效權杖可讀readyz_撤銷立即401_查詢字串的權杖不收()
    {
        await using var host = await HubTestHost.StartAsync();
        var token = DeviceTokenRules.NewToken();
        await AddDeviceAsync(host, Device(token));
        using var client = host.CreateClient(cookies: false);

        (await GetAsync(client, "/readyz", token)).Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, "/readyz?access_token=" + token, null)).Should().Be(HttpStatusCode.Unauthorized, "不收 access_token 查詢參數");
        (await GetAsync(client, "/readyz", DeviceTokenRules.NewToken())).Should().Be(HttpStatusCode.Unauthorized);
        (await GetAsync(client, "/readyz", "chd_short")).Should().Be(HttpStatusCode.Unauthorized);
        (await GetAsync(client, "/api/v1/auth/me", token)).Should().Be(HttpStatusCode.Unauthorized, "裝置權杖不能當擁有者");

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var device = await db.Devices.SingleAsync();
            device.LastSeenAt.Should().NotBeNull("記下最後使用時間");
            device.LastIp.Should().Be(TestIpStartupFilter.DefaultIp);
            device.RevokedAt = host.Clock.NowMs;
            device.RevokedReason = "retired";
            await db.SaveChangesAsync();
        }
        (await GetAsync(client, "/readyz", token)).Should().Be(HttpStatusCode.Unauthorized, "撤銷立即生效（每次查資料庫，不是 JWT）");
    }

    [Fact]
    public async Task 輪換後舊權杖保留24小時()
    {
        await using var host = await HubTestHost.StartAsync();
        var current = DeviceTokenRules.NewToken();
        var previous = DeviceTokenRules.NewToken();
        var device = Device(current);
        device.PrevTokenHash = DeviceTokenRules.Hash(previous);
        device.PrevTokenExpires = host.Clock.NowMs + 24 * 3_600_000L;
        await AddDeviceAsync(host, device);
        using var client = host.CreateClient(cookies: false);
        (await GetAsync(client, "/readyz", previous)).Should().Be(HttpStatusCode.OK);
        host.Clock.Advance(TimeSpan.FromHours(24));
        (await GetAsync(client, "/readyz", previous)).Should().Be(HttpStatusCode.Unauthorized);
        (await GetAsync(client, "/readyz", current)).Should().Be(HttpStatusCode.OK);
    }

    private static DeviceRecord Device(string token) => new()
    {
        Id = "d_" + Guid.NewGuid().ToString("N")[..26].ToUpperInvariant(),
        Name = "測試電腦",
        TokenHash = DeviceTokenRules.Hash(token),
        DevicePubkey = [1, 2, 3],
        KeyStorage = "software",
        CreatedAt = 1,
    };

    private static async Task AddDeviceAsync(HubTestHost host, DeviceRecord device)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        db.Devices.Add(device);
        await db.SaveChangesAsync();
    }

    private static async Task<HttpStatusCode> GetAsync(HubClient client, string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.Http.SendAsync(request);
        return response.StatusCode;
    }
}
