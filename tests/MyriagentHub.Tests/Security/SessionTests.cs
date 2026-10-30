using System.Net;
using System.Text;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Tests.Security;

/// <summary>SEC-04：cookie 旗標；伺服器端工作階段閒置 12 小時、絕對 7 天、撤銷即生效；每次登入換 key；登出刪列</summary>
public sealed class SessionTests
{
    [Fact]
    public async Task Cookie是Host前綴_Secure_HttpOnly_SameSiteStrict_沒有Domain()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var client = owner.Host.CreateClient();
        using (var login = await OwnerFlows.LoginAsync(client))
            login.StatusCode.Should().Be(HttpStatusCode.OK);
        var setCookie = client.Cookies!.SetCookieHeaders.Single(h => h.StartsWith("__Host-myriagenthub=", StringComparison.Ordinal));
        var parts = setCookie.Split(';').Select(p => p.Trim().ToLowerInvariant()).ToList();
        parts.Should().Contain("secure").And.Contain("httponly").And.Contain("samesite=strict").And.Contain("path=/");
        parts.Should().NotContain(p => p.StartsWith("domain=", StringComparison.Ordinal));
        parts.Should().Contain(p => p.StartsWith("expires=", StringComparison.Ordinal), "持久 cookie：7 天內免登入");

        // 其他 Identity cookie（如果有）也一樣
        foreach (var other in client.Cookies.SetCookieHeaders.Where(h => h.StartsWith("__Host-myriagenthub-", StringComparison.Ordinal)))
            other.ToLowerInvariant().Should().Contain("secure").And.Contain("samesite=strict").And.Contain("httponly");
        client.Cookies.SetCookieHeaders.Should().OnlyContain(h => h.StartsWith("__Host-", StringComparison.Ordinal), "所有 cookie 都要 __Host- 前綴");
    }

    [Fact]
    public async Task Cookie只放隨機key_資料庫只存雜湊與加密的票證()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var client = owner.Host.CreateClient();
        using (var login = await OwnerFlows.LoginAsync(client))
            login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = owner.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var rows = await db.BrowserSessions.AsNoTracking().ToListAsync();
        rows.Should().NotBeEmpty();
        foreach (var row in rows)
        {
            row.KeyHash.Should().HaveCount(32);
            Encoding.UTF8.GetString(row.Ticket).Should().NotContain("owner", "票證以 Data Protection 加密");
        }
    }

    [Fact]
    public async Task 閒置12小時後失效_持續使用則延長()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var client = owner.Host.CreateClient();
        using (var login = await OwnerFlows.LoginAsync(client))
            login.StatusCode.Should().Be(HttpStatusCode.OK);

        owner.Host.Clock.Advance(TimeSpan.FromHours(11));
        (await StatusAsync(client)).Should().Be(HttpStatusCode.OK, "11 小時內有使用");
        owner.Host.Clock.Advance(TimeSpan.FromHours(11));
        (await StatusAsync(client)).Should().Be(HttpStatusCode.OK, "上次使用後 11 小時，閒置期限已滑動延長");
        owner.Host.Clock.Advance(TimeSpan.FromHours(12) + TimeSpan.FromMinutes(1));
        (await StatusAsync(client)).Should().Be(HttpStatusCode.Unauthorized, "閒置超過 12 小時");
    }

    [Fact]
    public async Task 絕對期限7天_持續使用也要重新登入()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var client = owner.Host.CreateClient();
        using (var login = await OwnerFlows.LoginAsync(client))
            login.StatusCode.Should().Be(HttpStatusCode.OK);
        for (var hour = 6; hour < 7 * 24; hour += 6)
        {
            owner.Host.Clock.Advance(TimeSpan.FromHours(6));
            (await StatusAsync(client)).Should().Be(HttpStatusCode.OK, $"第 {hour} 小時，每 6 小時用一次");
        }
        owner.Host.Clock.Advance(TimeSpan.FromHours(6) + TimeSpan.FromMinutes(1));
        (await StatusAsync(client)).Should().Be(HttpStatusCode.Unauthorized, "滿 7 天");
    }

    [Fact]
    public async Task 撤銷其他瀏覽器的工作階段_立即401並發出撤銷通知()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var phone = owner.Host.CreateClient(ip: "198.51.100.7");
        using var laptop = owner.Host.CreateClient();
        using (var a = await OwnerFlows.LoginAsync(phone))
            a.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var b = await OwnerFlows.LoginAsync(laptop))
            b.StatusCode.Should().Be(HttpStatusCode.OK);

        var revoked = new List<string>();
        owner.Host.Services.GetRequiredService<SessionEvents>().Revoked += revoked.Add;
        var sessions = await laptop.GetJsonAsync("/api/v1/sessions");
        var phoneSession = sessions.GetProperty("sessions").EnumerateArray().Single(s => s.GetProperty("ipFirst").GetString() == "198.51.100.7");
        var id = phoneSession.GetProperty("id").GetString()!;

        using (var revoke = await laptop.PostAsync($"/api/v1/sessions/{id}/revoke"))
            revoke.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusAsync(phone)).Should().Be(HttpStatusCode.Unauthorized);
        (await StatusAsync(laptop)).Should().Be(HttpStatusCode.OK);
        revoked.Should().Equal(new[] { id }, "第 3 階段的瀏覽器 hub 靠這個通知中斷該工作階段的連線");
    }

    [Fact]
    public async Task 登出刪除工作階段_舊cookie重放無效()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var client = owner.Host.CreateClient();
        using (var login = await OwnerFlows.LoginAsync(client))
            login.StatusCode.Should().Be(HttpStatusCode.OK);
        var stolen = client.Cookies!.Container.GetCookieHeader(new Uri("https://localhost"));
        using (var logout = await client.PostAsync("/api/v1/auth/logout"))
            logout.StatusCode.Should().Be(HttpStatusCode.OK);

        using var attacker = owner.Host.CreateClient(cookies: false);
        attacker.Http.DefaultRequestHeaders.Add("Cookie", stolen);
        using var replay = await attacker.GetAsync("/api/v1/auth/me");
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task 每次登入發新key_舊的key作廢()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var client = owner.Host.CreateClient();
        using (var first = await OwnerFlows.LoginAsync(client))
            first.StatusCode.Should().Be(HttpStatusCode.OK);
        var before = client.Cookies!.Container.GetCookieHeader(new Uri("https://localhost"));
        using (var second = await OwnerFlows.LoginAsync(client))
            second.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = client.Cookies.Container.GetCookieHeader(new Uri("https://localhost"));
        after.Should().NotBe(before);

        using var old = owner.Host.CreateClient(cookies: false);
        old.Http.DefaultRequestHeaders.Add("Cookie", before);
        using var response = await old.GetAsync("/api/v1/auth/me");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void 工作階段規則()
    {
        var s = new MyriagentHub.Hosting.SessionSettings();
        var t = SessionRules.New(1_000, userVerified: true, s);
        SessionRules.IsValid(t, 1_000 + SessionRules.IdleMs(s) - 1).Should().BeTrue();
        SessionRules.IsValid(t, 1_000 + SessionRules.IdleMs(s)).Should().BeFalse();
        var touched = SessionRules.Touch(t, 1_000 + SessionRules.AbsoluteMs(s) - 10, s);
        touched.IdleExpiresAt.Should().Be(t.AbsoluteExpiresAt, "閒置期限不超過絕對期限");
        SessionRules.IsValid(t with { RevokedAt = 5 }, 2_000).Should().BeFalse();
        SessionRules.HasRecentAuth(1_000, 1_000 + 5 * 60_000, 5).Should().BeTrue();
        SessionRules.HasRecentAuth(1_000, 1_000 + 5 * 60_000 + 1, 5).Should().BeFalse();
        SessionRules.HasRecentAuth(0, 1_000, 5).Should().BeFalse("沒有做過使用者驗證");
        SessionRules.HasRecentAuth(10_000, 1_000, 5).Should().BeFalse("時鐘倒退時不算");
        SessionRules.New(1_000, userVerified: false, s).LastUvAt.Should().Be(0);
        var id = SessionRules.SessionId(new byte[32]);
        SessionRules.ParseSessionId(id).Should().HaveCount(32);
        SessionRules.ParseSessionId("短").Should().BeNull();
        SessionRules.ParseSessionId(new string('A', 200)).Should().BeNull();
    }

    private static async Task<HttpStatusCode> StatusAsync(HubClient client)
    {
        using var response = await client.GetAsync("/api/v1/auth/me");
        return response.StatusCode;
    }
}
