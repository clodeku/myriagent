using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using MyriagentHub.Auth;

namespace MyriagentHub.Tests.Auth;

/// <summary>2026-11-07 資安檢查修正的小地方：登入路徑的結尾斜線、裝置名稱叫 admin</summary>
public sealed class SecurityReviewTests
{
    private static HttpRequest Request(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context.Request;
    }

    [Theory]
    [InlineData("/api/v1/auth/login", true)]
    [InlineData("/api/v1/auth/login/", true)]
    [InlineData("/API/v1/auth/LOGIN//", true)]
    [InlineData("/api/v1/auth/logout", false)]
    [InlineData("/api/v1/auth/login/x", false)]
    [InlineData("/", false)]
    public void 登入路徑_結尾的斜線也認得_才會換新的工作階段key(string path, bool expected)
    {
        SessionRotation.IsSignInRequest(Request("POST", path)).Should().Be(expected);
        SessionRotation.IsSignInRequest(Request("GET", path)).Should().BeFalse();
    }

    [Fact]
    public void 名稱叫admin的裝置權杖身分_不算擁有者()
    {
        var owner = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, OwnerAccount.UserName)], "Identity.Application"));
        var device = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, OwnerAccount.UserName), new Claim(HubClaims.DeviceId, "dev-1")], HubAuthSchemes.Device));
        var collaborator = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, OwnerAccount.UserName), new Claim(HubClaims.Collaborator, "u1")], "Identity.Application"));

        OwnerAccount.IsOwnerPrincipal(owner).Should().BeTrue();
        OwnerAccount.IsOwnerPrincipal(device).Should().BeFalse();
        OwnerAccount.IsOwnerPrincipal(collaborator).Should().BeFalse();
        OwnerAccount.IsOwnerPrincipal(null).Should().BeFalse();
    }
}
