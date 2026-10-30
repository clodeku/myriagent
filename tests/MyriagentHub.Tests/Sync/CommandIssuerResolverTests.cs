using System.Net;
using Myriagent.Security;
using MyriagentHub.Auth;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Sync;

/// <summary>遠端指令信封的發起者資訊（WP4.5 計畫 D10、D11、D25）：issuer 的標籤來自工作階段的 UA，auth 是最後一次輸入密碼的時間與預設密碼旗標</summary>
public sealed class CommandIssuerResolverTests
{
    private const string ChromeWindows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    private const string SafariIPhone = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";

    [Fact]
    public async Task 登入後uvAt是登入時間_標籤來自UA()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        client.Http.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeWindows);
        host.Clock.Advance(TimeSpan.FromMinutes(3));
        var loginAt = host.Clock.NowMs;
        await OwnerFlows.LoginOwnerAsync(client);
        host.Clock.Advance(TimeSpan.FromSeconds(30));

        var facts = await Resolver(host).ResolveAsync(await SessionIdAsync(client), CancellationToken.None);
        facts.Issuer.Should().Be(new CommandIssuer(CommandIssuerKinds.Browser, "Chrome・Windows"));
        CommandEnvelope.IsValidIssuerLabel(facts.Issuer.Label).Should().BeTrue();
        facts.Auth.Should().Be(new CommandAuth(loginAt, DefaultPassword: true), "登入時剛輸入過密碼；首次啟動是預設密碼");

        using var phone = host.CreateClient();
        phone.Http.DefaultRequestHeaders.UserAgent.ParseAdd(SafariIPhone);
        await OwnerFlows.LoginOwnerAsync(phone);
        (await Resolver(host).ResolveAsync(await SessionIdAsync(phone), CancellationToken.None)).Issuer.Label.Should().Be("Safari・iOS", "每個工作階段各自的 UA");
    }

    [Fact]
    public async Task stepup之後uvAt更新()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        var sessionId = await SessionIdAsync(client);
        var loginAt = host.Clock.NowMs;
        (await Resolver(host).ResolveAsync(sessionId, CancellationToken.None)).Auth.UvAt.Should().Be(loginAt);

        host.Clock.Advance(TimeSpan.FromMinutes(10));
        using (var stepUp = await OwnerFlows.StepUpAsync(client))
            stepUp.StatusCode.Should().Be(HttpStatusCode.OK);
        var facts = await Resolver(host).ResolveAsync(sessionId, CancellationToken.None);
        facts.Auth.UvAt.Should().Be(host.Clock.NowMs, "再驗證之後立刻看得到（不快取）");
        facts.Issuer.Label.Should().Be(IssuerLabelRules.Fallback, "測試用戶端沒帶 UA");

        // 預設密碼旗標跟著擁有者的狀態
        using (var changed = await OwnerFlows.ChangePasswordAsync(client, OwnerAccount.DefaultPassword, "new-password-123"))
            changed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Resolver(host).ResolveAsync(sessionId, CancellationToken.None)).Auth.DefaultPassword.Should().BeFalse();
    }

    [Fact]
    public async Task 工作階段不存在_uvAt是null_標籤是瀏覽器()
    {
        await using var host = await HubTestHost.StartAsync();
        var expected = new CommandIssuerFacts(new CommandIssuer(CommandIssuerKinds.Browser, IssuerLabelRules.Fallback), new CommandAuth(null, DefaultPassword: true));
        foreach (var id in new[] { "", "not-a-session", new string('A', 43) })
            (await Resolver(host).ResolveAsync(id, CancellationToken.None)).Should().Be(expected, id);

        // 登出（列被刪掉）之後的工作階段當成不存在
        using var client = host.CreateClient();
        client.Http.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeWindows);
        await OwnerFlows.LoginOwnerAsync(client);
        var sessionId = await SessionIdAsync(client);
        (await Resolver(host).ResolveAsync(sessionId, CancellationToken.None)).Auth.UvAt.Should().NotBeNull("正向對照：登出之前有");
        using (var logout = await client.PostAsync("/api/v1/auth/logout"))
            logout.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Resolver(host).ResolveAsync(sessionId, CancellationToken.None)).Should().Be(expected);

        // 撤銷（列還在、帶 RevokedAt）之後也一樣
        using var revoked = host.CreateClient();
        revoked.Http.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeWindows);
        await OwnerFlows.LoginOwnerAsync(revoked);
        var revokedId = await SessionIdAsync(revoked);
        (await Resolver(host).ResolveAsync(revokedId, CancellationToken.None)).Issuer.Label.Should().Be("Chrome・Windows", "正向對照：撤銷之前有");
        (await host.Services.GetRequiredService<SessionService>().RevokeAsync(revokedId)).Should().BeTrue();
        (await host.Services.GetRequiredService<SessionService>().FindAsync(revokedId))!.RevokedAt.Should().NotBeNull("夾具：撤銷的列還在");
        (await Resolver(host).ResolveAsync(revokedId, CancellationToken.None)).Should().Be(expected);
    }

    private static CommandIssuerResolver Resolver(HubTestHost host) =>
        new(host.Services.GetRequiredService<SessionService>(), host.Services.GetRequiredService<OwnerAccountService>(), host.Services.GetRequiredService<DeviceDirectory>());

    private static async Task<string> SessionIdAsync(HubClient client) =>
        (await client.GetJsonAsync("/api/v1/auth/me")).GetProperty("session").GetProperty("id").GetString()!;
}
