using System.Security.Claims;
using MyriagentHub.Auth;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace MyriagentHub.Tests.Auth;

/// <summary>
/// DeviceAccess 政策（WP4.1 D56；sync-protocol 14.3）：第 4 階段裝置取 blob 與媒體轉送的端點只收存取權杖 cha_，長效的 chd_ 只能用在裝置 hub。
/// WP4.1 只立政策、沒有端點用它（D52）：以真的認證處理常式認證權杖、再以授權服務評估政策
/// </summary>
public sealed class DeviceAccessPolicyTests
{
    private const string DevicePath = "/api/v1/device/blobs/x";

    [Fact]
    public async Task 只收存取權杖_長效裝置權杖與擁有者cookie不行()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        var access = host.Services.GetRequiredService<AccessTokenStore>().Issue(device.Id).AccessToken;

        var accessUser = await AuthenticateAsync(host, DevicePath, access);
        accessUser.Should().NotBeNull("存取權杖在 /api/v1/device/* 認證得過");
        (await AuthorizeAsync(host, accessUser!, HubPolicies.DeviceAccess)).Should().BeTrue();

        var deviceUser = await AuthenticateAsync(host, DevicePath, device.Token);
        deviceUser.Should().NotBeNull("chd_ 照樣認證得過（裝置 hub 要用）");
        (await AuthorizeAsync(host, deviceUser!, HubPolicies.DeviceAccess)).Should().BeFalse("chd_ 不能取 blob 與媒體");
        (await AuthorizeAsync(host, deviceUser!, HubPolicies.Device)).Should().BeTrue("既有的 Device 政策不變");

        var owner = new ClaimsPrincipal(new ClaimsIdentity([new Claim(HubClaims.SessionId, "sid")], IdentityConstants.ApplicationScheme));
        (await AuthorizeAsync(host, owner, HubPolicies.DeviceAccess)).Should().BeFalse("擁有者的 cookie 不行");
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity([new Claim(HubClaims.TokenKind, HubTokenKinds.Access)]));
        (await AuthorizeAsync(host, unauthenticated, HubPolicies.DeviceAccess)).Should().BeFalse("沒有認證過的身分");

        (await AuthenticateAsync(host, "/api/v1/trust/x", access)).Should().BeNull("存取權杖只認裝置端點（照舊）");
    }

    private static async Task<ClaimsPrincipal?> AuthenticateAsync(HubTestHost host, string path, string bearer)
    {
        using var scope = host.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("localhost");
        http.Request.Path = path;
        http.Request.Headers.Authorization = "Bearer " + bearer;
        var result = await http.AuthenticateAsync(HubAuthSchemes.Device);
        return result.Succeeded ? result.Principal : null;
    }

    private static async Task<bool> AuthorizeAsync(HubTestHost host, ClaimsPrincipal user, string policy)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, null, policy)).Succeeded;
    }
}
