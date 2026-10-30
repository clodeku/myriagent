using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Tests.Security;

/// <summary>
/// 擁有者帳號密碼登入（設計 14.4）：首次啟動建立 admin／預設密碼、登入與登出、改密碼（撤銷其他工作階段）、再驗證（近期驗證）、
/// 第 2 階段 passkey 版的擁有者在啟動時轉換；稽核與記錄絕不含密碼
/// </summary>
public sealed class PasswordLoginTests
{
    private const string NewPassword = "new-password-123";

    [Fact]
    public async Task 首次啟動建立admin_預設密碼登入_錯誤密碼與未知帳號401_帳號大小寫與前後空白不拘_登出後401()
    {
        await using var host = await HubTestHost.StartAsync();
        using (var scope = host.Services.CreateScope())
        {
            var users = await scope.ServiceProvider.GetRequiredService<HubDbContext>().Users.AsNoTracking().ToListAsync();
            users.Should().ContainSingle().Which.UserName.Should().Be(OwnerAccount.UserName);
            users[0].PasswordHash.Should().NotBeNullOrEmpty().And.NotContain(OwnerAccount.DefaultPassword, "只存雜湊");
        }
        using var client = host.CreateClient();
        await ExpectAsync(await OwnerFlows.LoginAsync(client, "wrong-password"), HttpStatusCode.Unauthorized, "login_failed");
        await ExpectAsync(await OwnerFlows.LoginAsync(client, userName: "root"), HttpStatusCode.Unauthorized, "login_failed");
        await ExpectAsync(await OwnerFlows.LoginAsync(client, new string('x', PasswordRules.MaxLength + 1)), HttpStatusCode.Unauthorized, "login_failed");
        using (var me = await client.GetAsync("/api/v1/auth/me"))
            me.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "失敗的登入不發 cookie");

        using (var login = await OwnerFlows.LoginAsync(client, userName: " ADMIN "))
            login.StatusCode.Should().Be(HttpStatusCode.OK);
        var info = await client.GetJsonAsync("/api/v1/auth/me");
        info.GetProperty("name").GetString().Should().Be(OwnerAccount.UserName);
        info.GetProperty("session").GetProperty("recentAuth").GetBoolean().Should().BeTrue("登入時剛輸入過密碼");

        using (var logout = await client.PostAsync("/api/v1/auth/logout"))
            logout.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var after = await client.GetAsync("/api/v1/auth/me"))
            after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var audit = await host.Services.GetRequiredService<AuditLog>().ListAsync(null, 100);
        audit.Should().ContainSingle(a => a.Action == AuditActions.OwnerCreated && a.ActorType == AuditActors.System);
        audit.Count(a => a.Action == AuditActions.Login && a.Outcome == AuditOutcomes.Denied).Should().Be(3);
        audit.Should().Contain(a => a.Action == AuditActions.Login && a.Outcome == AuditOutcomes.Ok);
        AssertNoSecrets(audit, OwnerAccount.DefaultPassword, "wrong-password", "root");
    }

    [Fact]
    public async Task 改密碼_要目前的密碼_新密碼規則_成功後其他工作階段撤銷_這個保留_重啟後仍是新密碼()
    {
        var dir = TempDir.Create();
        try
        {
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                using var laptop = host.CreateClient();
                using var phone = host.CreateClient();
                await OwnerFlows.LoginOwnerAsync(laptop);
                await OwnerFlows.LoginOwnerAsync(phone);

                await ExpectAsync(await OwnerFlows.ChangePasswordAsync(laptop, "wrong-current", NewPassword), HttpStatusCode.Forbidden, "password_failed");
                await ExpectAsync(await OwnerFlows.ChangePasswordAsync(laptop, OwnerAccount.DefaultPassword, "short"), HttpStatusCode.BadRequest, "password_too_short");
                await ExpectAsync(await OwnerFlows.ChangePasswordAsync(laptop, OwnerAccount.DefaultPassword, new string('x', 129)), HttpStatusCode.BadRequest, "password_too_long");
                await ExpectAsync(await OwnerFlows.ChangePasswordAsync(laptop, OwnerAccount.DefaultPassword, "tab\tin-password"), HttpStatusCode.BadRequest, "password_invalid");
                await ExpectAsync(await OwnerFlows.ChangePasswordAsync(laptop, OwnerAccount.DefaultPassword, OwnerAccount.DefaultPassword), HttpStatusCode.BadRequest, "password_unchanged");

                var changed = await laptop.PostJsonAsync("/api/v1/auth/password", new { currentPassword = OwnerAccount.DefaultPassword, newPassword = NewPassword });
                changed.GetProperty("revokedSessions").GetInt32().Should().Be(1);
                using (var mine = await laptop.GetAsync("/api/v1/auth/me"))
                    mine.StatusCode.Should().Be(HttpStatusCode.OK, "改密碼的這個工作階段保留");
                using (var other = await phone.GetAsync("/api/v1/auth/me"))
                    other.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "其他瀏覽器登出");
                await ExpectAsync(await OwnerFlows.LoginAsync(phone), HttpStatusCode.Unauthorized, "login_failed");

                var audit = await host.Services.GetRequiredService<AuditLog>().ListAsync(null, 100);
                audit.Should().ContainSingle(a => a.Action == AuditActions.PasswordChanged && a.Outcome == AuditOutcomes.Ok);
                audit.Should().ContainSingle(a => a.Action == AuditActions.PasswordChanged && a.Outcome == AuditOutcomes.Denied);
                AssertNoSecrets(audit, OwnerAccount.DefaultPassword, NewPassword, "wrong-current", "short");
            }

            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                using var client = host.CreateClient();
                await ExpectAsync(await OwnerFlows.LoginAsync(client), HttpStatusCode.Unauthorized, "login_failed");
                using (var login = await OwnerFlows.LoginAsync(client, NewPassword))
                    login.StatusCode.Should().Be(HttpStatusCode.OK, "重啟不會把密碼改回預設");
                (await host.Services.GetRequiredService<AuditLog>().ListAsync(null, 100)).Count(a => a.Action == AuditActions.OwnerCreated).Should().Be(1, "帳號只建立一次");
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 近期驗證_登入超過5分鐘要再輸入密碼_密碼錯403_正確後放行()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        using (var fresh = await client.GetAsync("/api/v1/audit"))
            fresh.StatusCode.Should().Be(HttpStatusCode.OK, "剛登入就是近期驗證");

        host.Clock.Advance(TimeSpan.FromMinutes(6));
        await ExpectAsync(await client.GetAsync("/api/v1/audit"), HttpStatusCode.Forbidden, "step_up_required");
        (await client.GetJsonAsync("/api/v1/auth/me")).GetProperty("session").GetProperty("recentAuth").GetBoolean().Should().BeFalse();
        await ExpectAsync(await OwnerFlows.StepUpAsync(client, "wrong-password"), HttpStatusCode.Forbidden, "password_failed");
        await ExpectAsync(await client.GetAsync("/api/v1/audit"), HttpStatusCode.Forbidden, "step_up_required");

        var stepUp = await client.PostJsonAsync("/api/v1/auth/stepup", new { password = OwnerAccount.DefaultPassword });
        stepUp.GetProperty("recentAuthUntil").GetInt64().Should().Be(host.Clock.NowMs + 5 * 60_000);
        var audit = await client.GetJsonAsync("/api/v1/audit?limit=100");
        var actions = audit.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        actions.Should().Contain(AuditActions.StepUp).And.Contain(AuditActions.Login).And.Contain(AuditActions.OwnerCreated);
        audit.ToString().Should().NotContain(OwnerAccount.DefaultPassword).And.NotContain("wrong-password");
    }

    [Fact]
    public async Task 第2階段passkey版的擁有者_另建admin_舊帳號passkey與設定碼狀態都不動_舊帳號的工作階段無效()
    {
        var dir = TempDir.Create();
        try
        {
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                using var scope = host.Services.CreateScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
                var owner = (await users.FindByNameAsync(OwnerAccount.UserName))!;
                // 回到第 2 階段的樣子：只有沒有密碼、有一把 passkey 的 owner 帳號；kv 與資料根留著設定碼的狀態
                (await users.RemovePasswordAsync(owner)).Succeeded.Should().BeTrue();
                (await users.SetUserNameAsync(owner, OwnerAccount.LegacyUserName)).Succeeded.Should().BeTrue();
                var passkey = new UserPasskeyInfo(RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(77), DateTimeOffset.UtcNow, 1, null, true, false, false, [], []);
                (await users.AddOrUpdatePasskeyAsync(owner, passkey)).Succeeded.Should().BeTrue();
                var kv = host.Services.GetRequiredService<KvStore>();
                await kv.SetAsync(KvKeys.LegacySetupState, new { codeHash = "x" });
                await kv.SetAsync(KvKeys.LegacyOwnerKeyResetPending, true);
                Directory.CreateDirectory(host.Paths.LegacySetupDir);
                await File.WriteAllTextAsync(Path.Combine(host.Paths.LegacySetupDir, "setup-code.txt"), "AAAA-BBBB-CCCC\n");
            }

            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                // 部署失敗的自動回滾不還原資料庫：上一版要照樣能以 owner 的 passkey 登入，所以全部不動
                using (var scope = host.Services.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
                    (await db.Users.AsNoTracking().Select(u => u.UserName).ToListAsync()).Should().BeEquivalentTo(OwnerAccount.LegacyUserName, OwnerAccount.UserName);
                    (await db.UserPasskeys.CountAsync()).Should().Be(1);
                }
                var kv = host.Services.GetRequiredService<KvStore>();
                (await kv.GetAsync<object>(KvKeys.LegacySetupState)).Should().NotBeNull();
                (await kv.GetAsync<bool>(KvKeys.LegacyOwnerKeyResetPending)).Should().BeTrue();
                File.Exists(Path.Combine(host.Paths.LegacySetupDir, "setup-code.txt")).Should().BeTrue();

                using var client = host.CreateClient();
                await ExpectAsync(await OwnerFlows.LoginAsync(client, userName: OwnerAccount.LegacyUserName), HttpStatusCode.Unauthorized, "login_failed");
                using (var login = await OwnerFlows.LoginAsync(client))
                    login.StatusCode.Should().Be(HttpStatusCode.OK);
                var created = (await host.Services.GetRequiredService<AuditLog>().ListAsync(null, 100)).Where(a => a.Action == AuditActions.OwnerCreated).ToList();
                created.Should().HaveCount(2);
                created[0].Detail.Should().Contain("\"legacyOwner\":true");

                // 上一版以 owner 登入的工作階段（回滾後再部署時還在），在這一版當成沒有
                var store = host.Services.GetRequiredService<SqliteTicketStore>();
                var legacyKey = await store.StoreAsync(Ticket(OwnerAccount.LegacyUserName));
                (await store.RetrieveAsync(legacyKey)).Should().BeNull();
                var ownerKey = await store.StoreAsync(Ticket(OwnerAccount.UserName));
                (await store.RetrieveAsync(ownerKey)).Should().NotBeNull("對照：admin 的工作階段照常");
            }

            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
                (await host.Services.GetRequiredService<AuditLog>().ListAsync(null, 100)).Count(a => a.Action == AuditActions.OwnerCreated).Should().Be(2, "admin 已經有了，重啟不再建立");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    private static AuthenticationTicket Ticket(string userName) =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, userName)], IdentityConstants.ApplicationScheme)), IdentityConstants.ApplicationScheme);

    [Fact]
    public async Task 敏感操作端點都標記了近期驗證()
    {
        await using var host = await HubTestHost.StartAsync();
        var source = host.Services.GetRequiredService<EndpointDataSource>();
        var recent = source.Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<RequiresRecentAuthAttribute>() is not null)
            .Select(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single() ?? "?") + " " + e.RoutePattern.RawText)
            .ToList();
        recent.Should().BeEquivalentTo(
            "POST /api/v1/sessions/{id}/revoke",
            "GET /api/v1/audit",
            "POST /api/v1/pair/approve",
            "POST /api/v1/devices/{id}/revoke",
            "POST /api/v1/devices/{id}/remove",
            "POST /api/v1/accounts/{id}/delete");
    }

    [Fact]
    public void 密碼與帳號規則()
    {
        PasswordRules.IsCandidate(OwnerAccount.DefaultPassword).Should().BeTrue();
        PasswordRules.IsCandidate(null).Should().BeFalse();
        PasswordRules.IsCandidate("").Should().BeFalse();
        PasswordRules.IsCandidate(new string('x', PasswordRules.MaxLength)).Should().BeTrue();
        PasswordRules.IsCandidate(new string('x', PasswordRules.MaxLength + 1)).Should().BeFalse("超長的不拿去雜湊");

        PasswordRules.CheckNew(OwnerAccount.DefaultPassword, "other-password").Should().BeNull("預設密碼本身符合規則（8 字、只看長度）");
        PasswordRules.CheckNew("1234567", null).Should().Be("password_too_short");
        PasswordRules.CheckNew(null, null).Should().Be("password_too_short");
        PasswordRules.CheckNew(new string('x', 129), null).Should().Be("password_too_long");
        PasswordRules.CheckNew("line" + (char)10 + "break-pw", null).Should().Be("password_invalid");
        PasswordRules.CheckNew("same-password", "same-password").Should().Be("password_unchanged");
        PasswordRules.CheckNew("密碼有中文也可以", null).Should().BeNull();
        PasswordRules.CheckNew("  spaces  ", null).Should().BeNull("空白照原樣算（不修剪）");

        PasswordRules.CleanUserName(" admin ").Should().Be("admin");
        PasswordRules.CleanUserName("   ").Should().BeNull();
        PasswordRules.CleanUserName(new string('a', 65)).Should().BeNull();
    }

    private static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(status, text);
            System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("error").GetString().Should().Be(code);
        }
    }

    /// <summary>稽核的任何欄位都不能出現用過的密碼或輸入的帳號（常有人把密碼打進帳號欄）</summary>
    private static void AssertNoSecrets(IEnumerable<AuditRecord> audit, params string[] secrets)
    {
        foreach (var row in audit)
        {
            var text = string.Join("|", row.Action, row.Target, row.Detail, row.ActorId, row.Ip);
            foreach (var secret in secrets)
                text.Should().NotContain(secret, row.Action);
        }
    }
}
