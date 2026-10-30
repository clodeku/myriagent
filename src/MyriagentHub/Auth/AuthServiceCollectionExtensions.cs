using System.Net;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace MyriagentHub.Auth;

internal static class AuthServiceCollectionExtensions
{
    /// <summary>擁有者 cookie 或裝置權杖（協作人的 cookie 不算）</summary>
    internal static bool IsOwnerOrDevice(System.Security.Claims.ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && (user.FindFirst(HubClaims.DeviceId) is not null || OwnerAccount.IsOwnerPrincipal(user));

    /// <summary>
    /// 認證與授權（8.2、設計 14.4）：Identity 10（Version3 結構）、單一擁有者帳號 admin 以密碼登入（PBKDF2，Identity 預設的雜湊）、
    /// 伺服器端工作階段（__Host- cookie、SameSite=Strict、閒置 12 小時／絕對 7 天）、裝置權杖（只收 Authorization 標頭）、
    /// 所有端點預設要求認證（FallbackPolicy），匿名的只有允許清單上的端點。
    /// </summary>
    public static IServiceCollection AddHubAuth(this IServiceCollection services, HubOptions options, HubPaths paths)
    {
        // Data Protection 金鑰：固定放資料根、以 DPAPI LocalMachine 加密（原型是明文，啟動會警告）
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(paths.DataProtectionKeysDir))
            .ProtectKeysWithDpapi(protectToLocalMachine: true)
            // 改名（MyriagentHub → MyriagentHub，2026-10-30）之前的值：金鑰環以它隔離，改了既有的 cookie 與工作階段票證都解不開
            .SetApplicationName("MyriagentHub");

        services.AddIdentity<IdentityUser, IdentityRole>(o =>
            {
                o.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
                o.User.RequireUniqueEmail = false;
                o.SignIn.RequireConfirmedAccount = false;
                o.SignIn.RequireConfirmedEmail = false;
                // 只看長度（預設密碼 changeme2026 只有小寫與數字）；上限在雜湊前另外擋（PasswordRules.MaxLength）
                o.Password.RequiredLength = PasswordRules.MinLength;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Password.RequiredUniqueChars = 1;
                // 不用 Identity 的帳戶鎖定：只有一個帳戶，攻擊者可以藉此把擁有者鎖在門外；改以 IP／工作階段分區退避（AnonymousAuthGuard）
                o.Lockout.AllowedForNewUsers = false;
            })
            .AddEntityFrameworkStores<HubDbContext>();

        services.AddSingleton<SessionEvents>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<SqliteTicketStore>();
        services.AddSingleton<ITicketStore>(sp => sp.GetRequiredService<SqliteTicketStore>());

        var session = options.Session;
        services.ConfigureApplicationCookie(o =>
        {
            // 改名（MyriagentHub → MyriagentHub，2026-10-30）之前的值：瀏覽器存著這個名稱的 cookie，改了所有瀏覽器都要重新登入
            o.Cookie.Name = "__Host-myriagenthub";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.Path = "/";
            o.Cookie.Domain = null;
            o.ExpireTimeSpan = TimeSpan.FromDays(session.AbsoluteDays);
            o.SlidingExpiration = false;
            // API 沒有登入頁：未登入 401、權限不足 403（不轉址）
            o.Events.OnRedirectToLogin = c =>
            {
                c.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = c =>
            {
                c.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToLogout = c =>
            {
                c.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToReturnUrl = c =>
            {
                c.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            };
            // 不用 Identity 的安全戳記週期驗證：它會重新登入、換一個新的工作階段 key（遺失建立時間與近期驗證）；
            // 撤銷由伺服器端工作階段直接處理（每個請求都查資料庫）
            o.Events.OnValidatePrincipal = _ => Task.CompletedTask;
        });
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<ITicketStore>((o, store) => o.SessionStore = store);

        // Identity 的其他 cookie（外部登入、兩步驟驗證；目前都沒用到）一樣只走 HTTPS、SameSite=Strict、__Host- 前綴
        foreach (var (scheme, suffix) in new[]
                 {
                     (IdentityConstants.ExternalScheme, "external"),
                     (IdentityConstants.TwoFactorUserIdScheme, "ceremony"),
                     (IdentityConstants.TwoFactorRememberMeScheme, "remember"),
                 })
        {
            services.Configure<CookieAuthenticationOptions>(scheme, o =>
            {
                o.Cookie.Name = "__Host-myriagenthub-" + suffix;
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Strict;
                o.Cookie.Path = "/";
                o.Cookie.Domain = null;
            });
        }

        services.AddSingleton<AccessTokenStore>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, DeviceTokenHandler>(HubAuthSchemes.Device, null)
            .AddPolicyScheme(HubAuthSchemes.Selector, "Myriagent Hub", o =>
                o.ForwardDefaultSelector = context => context.Request.Headers.Authorization.Count > 0 ? HubAuthSchemes.Device : IdentityConstants.ApplicationScheme);
        services.Configure<AuthenticationOptions>(o =>
        {
            o.DefaultAuthenticateScheme = HubAuthSchemes.Selector;
            o.DefaultChallengeScheme = HubAuthSchemes.Selector;
            o.DefaultForbidScheme = HubAuthSchemes.Selector;
        });

        services.AddAuthorization(o =>
        {
            // 擁有者只收帳號 admin（多人協作之後 cookie 也可能是協作人的：協作人只過 Browser 政策）
            o.AddPolicy(HubPolicies.Owner, p => p.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser()
                .RequireAssertion(c => OwnerAccount.IsOwnerPrincipal(c.User)));
            o.AddPolicy(HubPolicies.Browser, p => p.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme).RequireAuthenticatedUser()
                .RequireAssertion(c => OwnerAccount.IsOwnerPrincipal(c.User) || c.User.CollaboratorId() is not null));
            o.AddPolicy(HubPolicies.Device, p => p.AddAuthenticationSchemes(HubAuthSchemes.Device).RequireAuthenticatedUser());
            o.AddPolicy(HubPolicies.DeviceAccess, p => p.AddAuthenticationSchemes(HubAuthSchemes.Device).RequireAuthenticatedUser()
                .RequireClaim(HubClaims.TokenKind, HubTokenKinds.Access));
            o.AddPolicy(HubPolicies.OpsRead, p => p.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, HubAuthSchemes.Device).AddRequirements(new OpsReadRequirement()));
            o.AddPolicy(HubPolicies.Authenticated, p => p.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, HubAuthSchemes.Device).RequireAuthenticatedUser()
                .RequireAssertion(c => IsOwnerOrDevice(c.User)));
            // 沒有標記的端點一律要求認證（匿名端點必須明確 AllowAnonymous，SEC-01 測試列舉檢查）；協作人不算
            o.FallbackPolicy = new AuthorizationPolicyBuilder(IdentityConstants.ApplicationScheme, HubAuthSchemes.Device).RequireAuthenticatedUser()
                .RequireAssertion(c => IsOwnerOrDevice(c.User)).Build();
        });
        services.AddSingleton<IAuthorizationHandler, OpsReadHandler>();

        services.AddSingleton<AnonymousAuthGuard>();
        services.AddSingleton<OwnerAccountService>();
        services.AddSingleton<CollaboratorDirectory>();
        services.AddSingleton<IStartupTask>(sp => new StartupTask(StartupOrder.Collaborators, "協作人名冊", ct => sp.GetRequiredService<CollaboratorDirectory>().InitializeAsync(ct)));
        services.AddSingleton<IStartupTask>(sp => new StartupTask(StartupOrder.OwnerAccount, "擁有者帳號", ct => sp.GetRequiredService<OwnerAccountService>().InitializeAsync(ct)));
        return services;
    }
}

/// <summary>/readyz：擁有者、裝置，或本機迴路（部署腳本在 Hub 主機上以 127.0.0.1 檢查）</summary>
internal sealed class OpsReadRequirement : IAuthorizationRequirement
{
}

internal sealed class OpsReadHandler : AuthorizationHandler<OpsReadRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OpsReadRequirement requirement)
    {
        if (AuthServiceCollectionExtensions.IsOwnerOrDevice(context.User) || (context.Resource is HttpContext http && IsLoopback(http.Connection.RemoteIpAddress)))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }

    /// <summary>雙堆疊監聽時 IPv4 用戶端是「IPv4 對應的 IPv6」位址，先轉回 IPv4 再判斷</summary>
    public static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
