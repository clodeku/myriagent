using System.Net;
using MyriagentHub.Auth;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Security;

/// <summary>
/// SEC-07：分區速率限制（匿名以 IP 分區）與密碼驗證的退避：未登入的登入以 IP 分區、已登入後的再驗證與改密碼以工作階段分區；
/// 已認證的流量不受 IP 限制與退避影響
/// </summary>
public sealed class RateLimitTests
{
    [Fact]
    public async Task 登入端點每IP每分鐘10次_其他IP不受影響()
    {
        await using var owner = await OwnerHost.StartAsync(new Dictionary<string, string?> { ["Hub:RateLimits:Enabled"] = "true" });
        using var attacker = owner.Host.CreateClient(ip: "198.51.100.1");
        for (var i = 0; i < 10; i++)
        {
            using var wrong = await OwnerFlows.LoginAsync(attacker, "wrong-password-" + i);
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        using (var limited = await OwnerFlows.LoginAsync(attacker))
        {
            limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "正確的密碼也要等");
            limited.Headers.Contains("Retry-After").Should().BeTrue();
            (await limited.Content.ReadAsStringAsync()).Should().Contain("rate_limited");
        }

        using var other = owner.Host.CreateClient(ip: "198.51.100.2");
        using var ok = await OwnerFlows.LoginAsync(other);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "別的 IP 不受影響");
    }

    [Fact]
    public async Task 再驗證與改密碼的每分鐘額度以工作階段分區_同一個IP的匿名猜測用不掉()
    {
        await using var owner = await OwnerHost.StartAsync(new Dictionary<string, string?>
        {
            ["Hub:RateLimits:Enabled"] = "true",
            ["Hub:RateLimits:AnonymousPerMinute"] = "1000",
        });
        const string ip = "198.51.100.8";
        using var signedIn = owner.Host.CreateClient(ip: ip);
        await OwnerFlows.LoginOwnerAsync(signedIn);

        using var attacker = owner.Host.CreateClient(ip: ip, cookies: false);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 10; i++)
        {
            using var guess = await OwnerFlows.LoginAsync(attacker, "guess-" + i);
            codes.Add(guess.StatusCode);
        }
        codes.Should().Contain(HttpStatusCode.TooManyRequests, "這個 IP 的登入額度（10 次，含擁有者那一次）用完了");

        using (var stepUp = await OwnerFlows.StepUpAsync(signedIn))
            stepUp.StatusCode.Should().Be(HttpStatusCode.OK, "已登入的再驗證不吃 IP 的額度");
        using (var change = await OwnerFlows.ChangePasswordAsync(signedIn, OwnerAccount.DefaultPassword, "new-password-1"))
            change.StatusCode.Should().Be(HttpStatusCode.OK);

        // 工作階段自己的額度照樣是每分鐘 10 次（上面已用 2 次）
        var mine = new List<HttpStatusCode>();
        for (var i = 0; i < 9; i++)
        {
            using var again = await OwnerFlows.StepUpAsync(signedIn, "new-password-1");
            mine.Add(again.StatusCode);
        }
        mine.Take(8).Should().OnlyContain(c => c == HttpStatusCode.OK);
        mine[8].Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task 連續20次登入失敗後該IP退避15分鐘_已登入的擁有者在同一個IP照常使用()
    {
        await using var owner = await OwnerHost.StartAsync(new Dictionary<string, string?>
        {
            ["Hub:RateLimits:Enabled"] = "true",
            ["Hub:RateLimits:LoginPerMinute"] = "1000",
            ["Hub:RateLimits:AnonymousPerMinute"] = "1000",
        });
        const string homeIp = "198.51.100.4";
        using var signedIn = owner.Host.CreateClient(ip: homeIp);
        using (var login = await OwnerFlows.LoginAsync(signedIn))
            login.StatusCode.Should().Be(HttpStatusCode.OK);

        using var attacker = owner.Host.CreateClient(ip: homeIp);
        for (var i = 0; i < 20; i++)
        {
            using var failed = await OwnerFlows.LoginAsync(attacker, "guess-" + i);
            failed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        using (var blocked = await OwnerFlows.LoginAsync(attacker))
        {
            blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "退避期間正確的密碼也不放行（不洩漏猜對了沒）");
            int.Parse(blocked.Headers.GetValues("Retry-After").Single()).Should().BeInRange(1, 15 * 60);
        }

        // 已認證的流量不以 IP 封鎖（家中 NAT 後的所有電腦共用一個 IP）
        using (var me = await signedIn.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var ops = await signedIn.GetAsync("/api/v1/ops/status"))
            ops.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var stepUp = await OwnerFlows.StepUpAsync(signedIn))
            stepUp.StatusCode.Should().Be(HttpStatusCode.OK, "已登入的再驗證以工作階段分區");

        // 別的 IP 照常可以登入
        using (var other = owner.Host.CreateClient(ip: "198.51.100.5"))
        using (var login = await OwnerFlows.LoginAsync(other))
            login.StatusCode.Should().Be(HttpStatusCode.OK);

        // 15 分鐘後解除
        owner.Host.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        using var released = await OwnerFlows.LoginAsync(attacker);
        released.StatusCode.Should().Be(HttpStatusCode.OK);

        var guard = owner.Host.Services.GetRequiredService<AnonymousAuthGuard>();
        guard.GlobalFailuresLastHour.Should().Be(20);
        var status = await signedIn.GetJsonAsync("/api/v1/ops/status");
        status.GetProperty("alerts").EnumerateArray().Select(a => a.GetString()).Should().Contain("login_failures");
    }

    [Fact]
    public async Task 偷到cookie的人用再驗證或改密碼猜密碼_同一個工作階段連錯20次就退避_擁有者其他瀏覽器不受影響()
    {
        await using var owner = await OwnerHost.StartAsync(new Dictionary<string, string?>
        {
            ["Hub:RateLimits:Enabled"] = "true",
            ["Hub:RateLimits:LoginPerMinute"] = "1000",
        });
        const string ip = "198.51.100.7";
        using var stolen = owner.Host.CreateClient(ip: ip);
        await OwnerFlows.LoginOwnerAsync(stolen);
        for (var i = 0; i < 10; i++)
        {
            using var wrong = await OwnerFlows.StepUpAsync(stolen, "guess-" + i);
            wrong.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using var change = await OwnerFlows.ChangePasswordAsync(stolen, "guess-" + i, "new-password-1");
            change.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        using (var blocked = await OwnerFlows.StepUpAsync(stolen))
            blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "再驗證與改密碼共用同一個工作階段的退避");
        using (var blockedChange = await OwnerFlows.ChangePasswordAsync(stolen, OwnerAccount.DefaultPassword, "new-password-1"))
            blockedChange.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        using var mine = owner.Host.CreateClient(ip: ip);
        await OwnerFlows.LoginOwnerAsync(mine);
        using (var stepUp = await OwnerFlows.StepUpAsync(mine))
            stepUp.StatusCode.Should().Be(HttpStatusCode.OK, "別的工作階段（同一個 IP）不受影響");
        owner.Host.Services.GetRequiredService<AnonymousAuthGuard>().GlobalFailuresLastHour.Should().Be(20, "已登入後的猜測也算進全域失敗告警");
    }

    [Fact]
    public async Task 未認證請求總量以IP限流_已認證不受限()
    {
        await using var owner = await OwnerHost.StartAsync(new Dictionary<string, string?>
        {
            ["Hub:RateLimits:Enabled"] = "true",
            ["Hub:RateLimits:AnonymousPerMinute"] = "5",
            ["Hub:RateLimits:LoginPerMinute"] = "100",
        });
        const string ip = "198.51.100.6";
        using var signedIn = owner.Host.CreateClient(ip: ip);
        using (var login = await OwnerFlows.LoginAsync(signedIn))
            login.StatusCode.Should().Be(HttpStatusCode.OK);

        using var anonymous = owner.Host.CreateClient(ip: ip, cookies: false);
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var response = await anonymous.GetAsync("/healthz");
            codes.Add(response.StatusCode);
        }
        codes.Should().Contain(HttpStatusCode.TooManyRequests);
        for (var i = 0; i < 10; i++)
        {
            using var me = await signedIn.GetAsync("/api/v1/auth/me");
            me.StatusCode.Should().Be(HttpStatusCode.OK, "已認證的請求不受未認證總量限制");
        }
    }

    [Fact]
    public void 退避規則與分區鍵()
    {
        var state = BackoffState.Empty;
        for (var i = 0; i < 19; i++)
            state = AuthBackoffRules.RecordFailure(state, 1_000, 20, 900_000);
        AuthBackoffRules.IsBlocked(state, 1_000).Should().BeFalse();
        state = AuthBackoffRules.RecordFailure(state, 1_000, 20, 900_000);
        AuthBackoffRules.IsBlocked(state, 1_000).Should().BeTrue();
        AuthBackoffRules.IsBlocked(state, 1_000 + 900_000).Should().BeFalse();
        AuthBackoffRules.RetryAfterSeconds(state, 1_000).Should().Be(900);
        AuthBackoffRules.RecordSuccess(state with { ConsecutiveFailures = 7 }).ConsecutiveFailures.Should().Be(0);

        var attempts = BackoffState.Empty;
        for (var i = 0; i < 3; i++)
            attempts = AuthBackoffRules.TryAttempt(attempts, 10_000 + i, 3).State;
        AuthBackoffRules.TryAttempt(attempts, 10_010, 3).Allowed.Should().BeFalse();
        AuthBackoffRules.TryAttempt(attempts, 10_000 + AuthBackoffRules.HourMs, 3).Allowed.Should().BeTrue("一小時前的嘗試不算");

        ClientKeyRules.PartitionKey(IPAddress.Parse("203.0.113.9")).Should().Be("203.0.113.9");
        ClientKeyRules.PartitionKey(IPAddress.Parse("::ffff:203.0.113.9")).Should().Be("203.0.113.9");
        ClientKeyRules.PartitionKey(IPAddress.Parse("2001:db8:1:2:3:4:5:6")).Should().Be("2001:db8:1:2::/64");
        ClientKeyRules.PartitionKey(IPAddress.Parse("2001:db8:1:2:ffff::1")).Should().Be("2001:db8:1:2::/64", "同一個 /64 視為同一個來源");
        ClientKeyRules.PartitionKey(null).Should().Be("unknown");
    }
}
