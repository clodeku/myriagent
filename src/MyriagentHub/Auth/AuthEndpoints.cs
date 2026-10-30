using System.Security.Claims;
using MyriagentHub.Audit;
using MyriagentHub.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Auth;

internal sealed record LoginBody(string? UserName, string? Password);

internal sealed record PasswordBody(string? Password);

internal sealed record ChangePasswordBody(string? CurrentPassword, string? NewPassword);

/// <summary>
/// 擁有者的 API（全部在 /api/v1 底下；回應一律 JSON、Cache-Control: no-store）：匿名只有 auth/login（速率限制＋退避），
/// 其他都要擁有者 cookie，敏感操作另要求近期驗證（最近幾分鐘內輸入過密碼，設計 14.4）。
/// 密碼驗證（登入、再驗證、改密碼的目前密碼）一律經 <see cref="AnonymousAuthGuard"/> 退避：未登入以 IP 分區、已登入以工作階段分區，
/// 偷到 cookie 的人也不能拿再驗證端點猜密碼。
/// </summary>
internal static class AuthEndpoints
{
    public static void MapHubAuth(this WebApplication app)
    {
        app.MapPost("/api/v1/auth/login", LoginAsync).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Login);

        // 擁有者與協作人都能用的（多人協作）：自己是誰、登出、改自己的密碼
        var browser = app.MapGroup("/api/v1").RequireAuthorization(HubPolicies.Browser);
        browser.MapGet("/auth/me", MeAsync);
        browser.MapPost("/auth/logout", LogoutAsync);
        browser.MapPost("/auth/password", ChangePasswordAsync).RequireRateLimiting(RateLimitPolicies.Login);

        var owner = app.MapGroup("/api/v1").RequireAuthorization(HubPolicies.Owner);
        owner.MapPost("/auth/stepup", StepUpAsync).RequireRateLimiting(RateLimitPolicies.Login);
        owner.MapGet("/sessions", ListSessionsAsync);
        owner.MapPost("/sessions/{id}/revoke", RevokeSessionAsync).RequireRecentAuth();
        owner.MapGet("/audit", ListAuditAsync).RequireRecentAuth();
    }

    // ───── 登入、登出、近期驗證、改密碼 ─────

    private static async Task<IResult> LoginAsync(
        LoginBody body,
        SignInManager<IdentityUser> signIn,
        UserManager<IdentityUser> users,
        AuditLog audit,
        AnonymousAuthGuard guard,
        ITicketStore sessions,
        CollaboratorDirectory collaborators,
        TimeProvider time,
        IOptions<HubOptions> options,
        HttpContext http)
    {
        var partition = PasswordPartition(http);
        if (guard.TryEnter(partition) is { } retryAfter)
            return TooManyAttempts(http, retryAfter);
        var name = PasswordRules.CleanUserName(body.UserName);
        var user = name is not null && PasswordRules.IsCandidate(body.Password) ? await users.FindByNameAsync(name) : null;
        // 協作人（多人協作）：名冊裡啟用中的才收；停用的回同樣的 login_failed（不洩漏帳號存在）
        var collaborator = user is not null && !OwnerAccount.IsOwner(user) ? collaborators.Find(user.Id) : null;
        if (user is null || (!OwnerAccount.IsOwner(user) && collaborator is not { Disabled: false }) || !await users.CheckPasswordAsync(user, body.Password!))
        {
            guard.RecordFailure(partition);
            // 不記輸入的帳號：常有人把密碼打進帳號欄
            await audit.WriteAsync(new AuditEntry(AuditActors.Anonymous, null, http.ClientIp(), AuditActions.Login, null, AuditOutcomes.Denied, new { ua = http.UserAgentSummary() }));
            return Error(StatusCodes.Status401Unauthorized, "login_failed");
        }
        guard.RecordSuccess(partition);
        if (collaborator is not null)
        {
            await SignInCollaboratorAsync(signIn, user, time, options.Value.Session);
            await SessionRotation.RetireOldSessionAsync(http, sessions);
            await collaborators.MarkLoginAsync(user.Id, CancellationToken.None);
            await audit.WriteAsync(new AuditEntry(AuditActors.Collaborator, null, http.ClientIp(), AuditActions.Login, user.Id, AuditOutcomes.Ok, new { ua = http.UserAgentSummary() }));
            return Results.Ok(new { ok = true, role = Roles.Collaborator });
        }
        await SignInOwnerAsync(signIn, user);
        await SessionRotation.RetireOldSessionAsync(http, sessions);
        await audit.WriteAsync(new AuditEntry(AuditActors.Owner, null, http.ClientIp(), AuditActions.Login, OwnerAccount.UserName, AuditOutcomes.Ok, new { ua = http.UserAgentSummary() }));
        return Results.Ok(new { ok = true, role = Roles.Owner });
    }

    private static async Task<IResult> LogoutAsync(SignInManager<IdentityUser> signIn, AuditLog audit, HttpContext http)
    {
        var sid = http.SessionId();
        var actor = http.User.CollaboratorId() is null ? AuditActors.Owner : AuditActors.Collaborator;
        await signIn.SignOutAsync();
        await audit.WriteAsync(new AuditEntry(actor, ShortSid(sid), http.ClientIp(), AuditActions.Logout, null, AuditOutcomes.Ok));
        return Results.Ok(new { ok = true });
    }

    /// <summary>再輸入一次密碼：記下近期驗證（敏感操作回 403 step_up_required 時網頁呼叫）</summary>
    private static async Task<IResult> StepUpAsync(
        PasswordBody body,
        UserManager<IdentityUser> users,
        SessionService sessions,
        AuditLog audit,
        AnonymousAuthGuard guard,
        IOptions<HubOptions> options,
        HttpContext http)
    {
        var sid = http.SessionId();
        var partition = PasswordPartition(http);
        if (guard.TryEnter(partition) is { } retryAfter)
            return TooManyAttempts(http, retryAfter);
        var owner = await users.GetUserAsync(http.User);
        if (owner is null || sid is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthenticated");
        if (!PasswordRules.IsCandidate(body.Password) || !await users.CheckPasswordAsync(owner, body.Password!))
        {
            guard.RecordFailure(partition);
            await audit.WriteAsync(new AuditEntry(AuditActors.Owner, ShortSid(sid), http.ClientIp(), AuditActions.StepUp, null, AuditOutcomes.Denied));
            return Error(StatusCodes.Status403Forbidden, "password_failed");
        }
        guard.RecordSuccess(partition);
        var uvAt = await sessions.MarkUserVerifiedAsync(sid, http.RequestAborted);
        if (uvAt is null)
            return Error(StatusCodes.Status401Unauthorized, "session_revoked");
        await audit.WriteAsync(new AuditEntry(AuditActors.Owner, ShortSid(sid), http.ClientIp(), AuditActions.StepUp, null, AuditOutcomes.Ok));
        return Results.Ok(new { ok = true, recentAuthUntil = SessionRules.RecentAuthUntil(uvAt.Value, options.Value.Session.RecentAuthMinutes) });
    }

    /// <summary>改密碼：要目前的密碼；成功後撤銷其他所有瀏覽器工作階段（這個保留，並算一次近期驗證）</summary>
    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordBody body,
        UserManager<IdentityUser> users,
        SessionService sessions,
        AuditLog audit,
        AnonymousAuthGuard guard,
        OwnerAccountService owners,
        ILoggerFactory loggers,
        HttpContext http)
    {
        var sid = http.SessionId();
        var partition = PasswordPartition(http);
        if (guard.TryEnter(partition) is { } retryAfter)
            return TooManyAttempts(http, retryAfter);
        var owner = await users.GetUserAsync(http.User);
        if (owner is null || sid is null)
            return Error(StatusCodes.Status401Unauthorized, "unauthenticated");
        // 協作人改自己的密碼（多人協作）：成功後只撤銷他自己的其他工作階段
        var collaboratorId = http.User.CollaboratorId();
        var actor = collaboratorId is null ? AuditActors.Owner : AuditActors.Collaborator;
        var target = collaboratorId ?? OwnerAccount.UserName;
        if (!PasswordRules.IsCandidate(body.CurrentPassword) || !await users.CheckPasswordAsync(owner, body.CurrentPassword!))
        {
            guard.RecordFailure(partition);
            await audit.WriteAsync(new AuditEntry(actor, ShortSid(sid), http.ClientIp(), AuditActions.PasswordChanged, target, AuditOutcomes.Denied, new { reason = "current_password" }));
            return Error(StatusCodes.Status403Forbidden, "password_failed");
        }
        guard.RecordSuccess(partition);
        if (PasswordRules.CheckNew(body.NewPassword, body.CurrentPassword) is { } problem)
            return Error(StatusCodes.Status400BadRequest, problem);
        var changed = await users.ChangePasswordAsync(owner, body.CurrentPassword!, body.NewPassword!);
        if (!changed.Succeeded)
        {
            loggers.CreateLogger(typeof(AuthEndpoints)).LogWarning("改密碼被 Identity 拒絕：{Codes}", string.Join("; ", changed.Errors.Select(e => e.Code)));
            return Error(StatusCodes.Status400BadRequest, "password_rejected");
        }
        // 是不是改成預設密碼（WP4.5 D25：以字串比對，不必再算雜湊；改回預設是允許的，只警告）
        if (collaboratorId is null)
            owners.OnPasswordChanged(string.Equals(body.NewPassword, OwnerAccount.DefaultPassword, StringComparison.Ordinal));
        // 密碼已經換了：之後的步驟不帶請求的取消權杖（斷線時其他工作階段也一定要撤銷：改密碼常是懷疑 cookie 外流）
        await sessions.MarkUserVerifiedAsync(sid, CancellationToken.None);
        var revoked = collaboratorId is null
            ? await sessions.RevokeAllExceptAsync(sid, CancellationToken.None)
            : await sessions.RevokeUserExceptAsync(collaboratorId, sid, CancellationToken.None);
        await audit.WriteAsync(new AuditEntry(actor, ShortSid(sid), http.ClientIp(), AuditActions.PasswordChanged, target, AuditOutcomes.Ok, new { revokedSessions = revoked }), CancellationToken.None);
        return Results.Ok(new { ok = true, revokedSessions = revoked });
    }

    private static async Task<IResult> MeAsync(UserManager<IdentityUser> users, SessionService sessions, CollaboratorDirectory collaborators, TimeProvider time,
        IOptions<HubOptions> options, HttpContext http)
    {
        if (await users.GetUserAsync(http.User) is not { } owner)
            return Error(StatusCodes.Status401Unauthorized, "unauthenticated");
        var session = await sessions.FindAsync(http.SessionId(), http.RequestAborted);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var minutes = options.Value.Session.RecentAuthMinutes;
        var collaborator = collaborators.Find(http.User.CollaboratorId());
        return Results.Ok(new
        {
            name = owner.UserName,
            // 多人協作：owner／collaborator；協作人另帶顯示名稱（登入頁照 role 導向）
            role = collaborator is null ? Roles.Owner : Roles.Collaborator,
            displayName = collaborator?.DisplayName,
            serverDomain = options.Value.ServerDomain,
            session = session is null ? null : new
            {
                id = http.SessionId(),
                createdAt = session.CreatedAt,
                lastUvAt = session.LastUvAt,
                recentAuth = SessionRules.HasRecentAuth(session.LastUvAt, now, minutes),
                recentAuthUntil = SessionRules.RecentAuthUntil(session.LastUvAt, minutes),
                idleExpiresAt = session.IdleExpiresAt,
                absoluteExpiresAt = session.AbsoluteExpiresAt,
            },
        });
    }

    // ───── 工作階段 ─────

    private static async Task<IResult> ListSessionsAsync(SessionService sessions, HttpContext http) =>
        Results.Ok(new { sessions = await sessions.ListActiveAsync(http.SessionId(), http.RequestAborted) });

    private static async Task<IResult> RevokeSessionAsync(string id, SessionService sessions, AuditLog audit, HttpContext http)
    {
        var current = http.SessionId();
        if (!await sessions.RevokeAsync(id, http.RequestAborted))
            return Error(StatusCodes.Status404NotFound, "session_not_found");
        if (id == current)
            await http.SignOutAsync(IdentityConstants.ApplicationScheme);
        await audit.WriteAsync(new AuditEntry(AuditActors.Owner, ShortSid(current), http.ClientIp(), AuditActions.SessionRevoked, SessionRules.ShortId(id), AuditOutcomes.Ok, new { self = id == current }));
        return Results.Ok(new { ok = true, self = id == current });
    }

    // ───── 稽核 ─────

    private static async Task<IResult> ListAuditAsync(long? before, int? limit, AuditLog audit, HttpContext http)
    {
        var rows = await audit.ListAsync(before, limit ?? 50, http.RequestAborted);
        return Results.Ok(new
        {
            chain = audit.LastVerification,
            entries = rows.Select(r => new
            {
                seq = r.Seq,
                at = r.At,
                actorType = r.ActorType,
                actorId = r.ActorId,
                ip = r.Ip,
                action = r.Action,
                target = r.Target,
                outcome = r.Outcome,
                detail = r.Detail,
            }),
        });
    }

    // ───── 共用 ─────

    /// <summary>登入：持久 cookie（7 天內免登入、閒置 12 小時要重登），伺服器端工作階段記下「剛輸入過密碼」</summary>
    private static Task SignInOwnerAsync(SignInManager<IdentityUser> signIn, IdentityUser owner)
    {
        var properties = new AuthenticationProperties { IsPersistent = true, AllowRefresh = false };
        properties.Items[SessionItems.UserVerified] = "1";
        return signIn.SignInAsync(owner, properties, "pwd");
    }

    /// <summary>
    /// 協作人登入（多人協作）：持久 cookie、180 天到期（伺服器端閒置 30 天才失效，SessionRules.NewCollaborator），票證帶協作人的 id（取回時對名冊檢查）
    /// </summary>
    private static Task SignInCollaboratorAsync(SignInManager<IdentityUser> signIn, IdentityUser user, TimeProvider time, SessionSettings session)
    {
        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            AllowRefresh = false,
            ExpiresUtc = time.GetUtcNow().AddDays(session.CollaboratorAbsoluteDays),
        };
        properties.Items[SessionItems.UserVerified] = "1";
        return signIn.SignInWithClaimsAsync(user, properties, [new Claim(HubClaims.Collaborator, user.Id), new Claim("amr", "pwd")]);
    }

    /// <summary>登入的角色（登入與 auth/me 的 <c>role</c>）</summary>
    internal static class Roles
    {
        public const string Owner = "owner";

        public const string Collaborator = "collaborator";
    }

    /// <summary>密碼嘗試的退避分區：已登入以工作階段、未登入以 IP（IPv6 /64）</summary>
    private static string PasswordPartition(HttpContext http) => http.SessionId() is { } sid ? "sid:" + sid : http.ClientPartition();

    private static IResult TooManyAttempts(HttpContext http, int retryAfter)
    {
        http.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Error(StatusCodes.Status429TooManyRequests, "rate_limited");
    }

    private static string? ShortSid(string? sid) => sid is null ? null : SessionRules.ShortId(sid);

    internal static IResult Error(int status, string code) => Results.Json(new { error = code }, statusCode: status);
}
