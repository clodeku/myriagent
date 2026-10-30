using System.Security.Claims;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Auth;

/// <summary>
/// 伺服器端工作階段（ITicketStore）：cookie 只放 256 位元的隨機 key，資料庫只存 key 的 SHA-256 與以 Data Protection 加密的票證。
/// 閒置 12 小時（滑動）、絕對 7 天、可列出與撤銷，撤銷立即生效（每個請求都查資料庫）。每次登入發新 key（防固定工作階段），登出刪列。
/// </summary>
internal sealed class SqliteTicketStore(
    IServiceScopeFactory scopes,
    IDataProtectionProvider dataProtection,
    TimeProvider time,
    IOptions<HubOptions> options,
    SessionEvents events,
    CollaboratorDirectory collaborators,
    ILogger<SqliteTicketStore> log) : ITicketStore
{
    // 用途字串是改名（MyriagentHub → MyriagentHub，2026-10-30）之前的值：改了資料庫裡的票證都解不開
    private readonly IDataProtector protector = dataProtection.CreateProtector("MyriagentHub.BrowserSessions.v1");

    private SessionSettings Settings => options.Value.Session;

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, null!, CancellationToken.None);

    public Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken) => StoreAsync(ticket, null!, cancellationToken);

    public async Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var key = SessionRules.NewKey();
        var now = Now;
        var verified = ticket.Properties.Items.TryGetValue(SessionItems.UserVerified, out var uv) && uv == "1";
        // 協作人的工作階段（多人協作）：閒置 30 天、絕對 180 天，記下是哪一位（撤銷時以它找）
        var collaborator = ticket.Principal.FindFirst(HubClaims.Collaborator)?.Value;
        var times = collaborator is null ? SessionRules.New(now, verified, Settings) : SessionRules.NewCollaborator(now, Settings);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        db.BrowserSessions.Add(new BrowserSessionRecord
        {
            KeyHash = SessionRules.HashKey(key),
            Ticket = Protect(ticket),
            CreatedAt = times.CreatedAt,
            LastSeenAt = times.LastSeenAt,
            IdleExpiresAt = times.IdleExpiresAt,
            AbsoluteExpiresAt = times.AbsoluteExpiresAt,
            LastUvAt = times.LastUvAt,
            IpFirst = httpContext?.ClientIp(),
            IpLast = httpContext?.ClientIp(),
            UserAgent = httpContext?.UserAgentSummary(),
            UserId = collaborator,
        });
        await db.SaveChangesAsync(cancellationToken);
        return key;
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket) => RenewAsync(key, ticket, null!, CancellationToken.None);

    public Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken) => RenewAsync(key, ticket, null!, cancellationToken);

    public async Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var hash = SessionRules.HashKey(key);
        var row = await db.BrowserSessions.FirstOrDefaultAsync(s => s.KeyHash == hash, cancellationToken);
        if (row is null)
            return;
        row.Ticket = Protect(StripSessionClaim(ticket));
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveAsync(key, null!, CancellationToken.None);

    public Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken) => RetrieveAsync(key, null!, cancellationToken);

    public async Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 128)
            return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var hash = SessionRules.HashKey(key);
        var row = await db.BrowserSessions.FirstOrDefaultAsync(s => s.KeyHash == hash, cancellationToken);
        if (row is null)
            return null;
        var now = Now;
        var times = ToTimes(row);
        if (!SessionRules.IsValid(times, now))
            return null;

        AuthenticationTicket? ticket;
        try
        {
            ticket = TicketSerializer.Default.Deserialize(protector.Unprotect(row.Ticket));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or InvalidOperationException)
        {
            // Data Protection 金鑰遺失（換機重建）時票證解不開：當成登出
            log.LogWarning("工作階段 {Session} 的票證解不開，視為無效", SessionRules.ShortId(SessionRules.SessionId(hash)));
            return null;
        }
        if (ticket is null)
            return null;
        // 只有擁有者帳號 admin 與名冊裡啟用中的協作人的工作階段有效：第 2 階段的 owner 帳號（留著給部署回滾）在上一版登入的工作階段，在這一版當成沒有；
        // 協作人的票證要和這一列記的是同一位（多人協作）
        if (!IsAcceptedPrincipal(ticket.Principal, row.UserId))
            return null;

        if (SessionRules.ShouldTouch(times, now))
        {
            var touched = SessionRules.Touch(times, now, SessionRules.IdleMsFor(row.UserId is not null, Settings));
            row.LastSeenAt = touched.LastSeenAt;
            row.IdleExpiresAt = touched.IdleExpiresAt;
            row.IpLast = httpContext?.ClientIp() ?? row.IpLast;
            await db.SaveChangesAsync(cancellationToken);
        }

        if (ticket.Principal.Identity is ClaimsIdentity identity && !identity.HasClaim(c => c.Type == HubClaims.SessionId))
            identity.AddClaim(new Claim(HubClaims.SessionId, SessionRules.SessionId(hash)));
        return ticket;
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, null!, CancellationToken.None);

    public Task RemoveAsync(string key, CancellationToken cancellationToken) => RemoveAsync(key, null!, cancellationToken);

    public async Task RemoveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var hash = SessionRules.HashKey(key);
        var row = await db.BrowserSessions.FirstOrDefaultAsync(s => s.KeyHash == hash, cancellationToken);
        if (row is null)
            return;
        db.BrowserSessions.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        // 登出與登入換 key 都要讓這個工作階段的即時連線斷掉（第 3 階段的瀏覽器 hub 訂閱）
        events.RaiseRevoked(SessionRules.SessionId(hash));
    }

    /// <summary>
    /// hub 上的活動延長閒置期限（長連線不會再經過 <c>RetrieveAsync</c>）：
    /// 工作階段不存在、已撤銷或已無效回 false（不寫）；有效時回 true，<see cref="SessionRules.ShouldTouch"/> 成立才寫，
    /// 只更新 LastSeenAt、IdleExpiresAt、IpLast（同 RetrieveAsync 的續期；不改結構、不動其他欄位）
    /// </summary>
    public async Task<bool> TouchAsync(string sessionId, string? ip, CancellationToken cancellationToken = default)
    {
        var hash = SessionRules.ParseSessionId(sessionId);
        if (hash is null)
            return false;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var row = await db.BrowserSessions.FirstOrDefaultAsync(s => s.KeyHash == hash, cancellationToken);
        if (row is null)
            return false;
        var now = Now;
        var times = ToTimes(row);
        if (!SessionRules.IsValid(times, now))
            return false;
        if (SessionRules.ShouldTouch(times, now))
        {
            var touched = SessionRules.Touch(times, now, SessionRules.IdleMsFor(row.UserId is not null, Settings));
            row.LastSeenAt = touched.LastSeenAt;
            row.IdleExpiresAt = touched.IdleExpiresAt;
            row.IpLast = ip ?? row.IpLast;
            await db.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    /// <summary>
    /// 票證的身分收不收：擁有者（帳號 admin、不帶協作人的 claim、這一列沒記使用者）或協作人（claim 的 id 等於這一列記的、名冊裡啟用中）
    /// </summary>
    private bool IsAcceptedPrincipal(ClaimsPrincipal principal, string? rowUserId)
    {
        var collaborator = principal.FindFirst(HubClaims.Collaborator)?.Value;
        if (collaborator is null)
            return rowUserId is null && string.Equals(principal.Identity?.Name, OwnerAccount.UserName, StringComparison.Ordinal);
        return string.Equals(collaborator, rowUserId, StringComparison.Ordinal) && collaborators.IsActive(collaborator);
    }

    internal static SessionTimes ToTimes(BrowserSessionRecord row) =>
        new(row.CreatedAt, row.LastSeenAt, row.IdleExpiresAt, row.AbsoluteExpiresAt, row.LastUvAt, row.RevokedAt);

    private byte[] Protect(AuthenticationTicket ticket) => protector.Protect(TicketSerializer.Default.Serialize(StripSessionClaim(ticket)));

    /// <summary>存回去的票證不帶伺服器加的 sid claim（取出時才加）；複製一份處理，不改到這個請求正在用的身分</summary>
    private static AuthenticationTicket StripSessionClaim(AuthenticationTicket ticket)
    {
        if (!ticket.Principal.HasClaim(c => c.Type == HubClaims.SessionId))
            return ticket;
        var principal = ticket.Principal.Clone();
        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.FindAll(HubClaims.SessionId).ToList())
                identity.RemoveClaim(claim);
        }
        return new AuthenticationTicket(principal, ticket.Properties, ticket.AuthenticationScheme);
    }
}

/// <summary>工作階段撤銷的通知點（第 3 階段的瀏覽器 hub 訂閱它來中斷該工作階段的 SignalR 連線）</summary>
internal sealed class SessionEvents
{
    public event Action<string>? Revoked;

    public void RaiseRevoked(string sessionId) => Revoked?.Invoke(sessionId);
}

internal sealed record SessionInfo(
    string Id,
    long CreatedAt,
    long LastSeenAt,
    long IdleExpiresAt,
    long AbsoluteExpiresAt,
    long LastUvAt,
    string? IpFirst,
    string? IpLast,
    string? UserAgent,
    bool Current);

/// <summary>工作階段的列出、撤銷、近期驗證標記與清理</summary>
internal sealed class SessionService(IServiceScopeFactory scopes, TimeProvider time, SessionEvents events, ILogger<SessionService> log)
{
    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<IReadOnlyList<SessionInfo>> ListActiveAsync(string? currentSessionId, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var now = Now;
        // 只列擁有者自己的（協作人的工作階段由協作人的管理撤銷）
        var rows = await db.BrowserSessions.AsNoTracking()
            .Where(s => s.RevokedAt == null && s.IdleExpiresAt > now && s.AbsoluteExpiresAt > now && s.UserId == null)
            .ToListAsync(cancellationToken);
        return rows
            .OrderByDescending(s => s.LastSeenAt)
            .Select(s =>
            {
                var id = SessionRules.SessionId(s.KeyHash);
                return new SessionInfo(id, s.CreatedAt, s.LastSeenAt, s.IdleExpiresAt, s.AbsoluteExpiresAt, s.LastUvAt, s.IpFirst, s.IpLast, s.UserAgent,
                    id == currentSessionId);
            })
            .ToList();
    }

    public async Task<BrowserSessionRecord?> FindAsync(string? sessionId, CancellationToken cancellationToken = default)
    {
        var hash = SessionRules.ParseSessionId(sessionId);
        if (hash is null)
            return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await db.BrowserSessions.AsNoTracking().FirstOrDefaultAsync(s => s.KeyHash == hash, cancellationToken);
    }

    /// <summary>撤銷一個工作階段（立即生效）並通知訂閱者</summary>
    public async Task<bool> RevokeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var hash = SessionRules.ParseSessionId(sessionId);
        if (hash is null)
            return false;
        var now = Now;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var count = await RevokeWhereAsync(db, db.BrowserSessions.Where(s => s.KeyHash == hash && s.RevokedAt == null), now, cancellationToken);
        return count > 0;
    }

    /// <summary>撤銷所有工作階段（擁有者重設、還原）</summary>
    public async Task<int> RevokeAllAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await RevokeWhereAsync(db, db.BrowserSessions.Where(s => s.RevokedAt == null), Now, cancellationToken);
    }

    /// <summary>撤銷擁有者目前這個以外的所有工作階段（擁有者改密碼之後；協作人的不動）</summary>
    public async Task<int> RevokeAllExceptAsync(string keepSessionId, CancellationToken cancellationToken = default)
    {
        var keep = SessionRules.ParseSessionId(keepSessionId) ?? [];
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await RevokeWhereAsync(db, db.BrowserSessions.Where(s => s.RevokedAt == null && s.KeyHash != keep && s.UserId == null), Now, cancellationToken);
    }

    /// <summary>撤銷一位協作人的所有工作階段（刪除、停用、重設密碼；多人協作）</summary>
    public async Task<int> RevokeUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await RevokeWhereAsync(db, db.BrowserSessions.Where(s => s.RevokedAt == null && s.UserId == userId), Now, cancellationToken);
    }

    /// <summary>撤銷一位協作人目前這個以外的工作階段（協作人自己改密碼之後）</summary>
    public async Task<int> RevokeUserExceptAsync(string userId, string keepSessionId, CancellationToken cancellationToken = default)
    {
        var keep = SessionRules.ParseSessionId(keepSessionId) ?? [];
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await RevokeWhereAsync(db, db.BrowserSessions.Where(s => s.RevokedAt == null && s.UserId == userId && s.KeyHash != keep), Now, cancellationToken);
    }

    /// <summary>剛輸入過一次密碼：記下近期使用者驗證時間</summary>
    public async Task<long?> MarkUserVerifiedAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var hash = SessionRules.ParseSessionId(sessionId);
        if (hash is null)
            return null;
        var now = Now;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var row = await db.BrowserSessions.FirstOrDefaultAsync(s => s.KeyHash == hash && s.RevokedAt == null, cancellationToken);
        if (row is null)
            return null;
        row.LastUvAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return now;
    }

    /// <summary>刪除已過期或已撤銷超過 7 天的列（每日維護）</summary>
    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = Now - 7 * 86_400_000L;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var expired = await db.BrowserSessions
            .Where(s => (s.RevokedAt != null && s.RevokedAt < cutoff) || s.AbsoluteExpiresAt < cutoff || s.IdleExpiresAt < cutoff)
            .ToListAsync(cancellationToken);
        if (expired.Count == 0)
            return 0;
        db.BrowserSessions.RemoveRange(expired);
        await db.SaveChangesAsync(cancellationToken);
        return expired.Count;
    }

    private async Task<int> RevokeWhereAsync(HubDbContext db, IQueryable<BrowserSessionRecord> query, long now, CancellationToken cancellationToken)
    {
        var rows = await query.ToListAsync(cancellationToken);
        foreach (var row in rows)
            row.RevokedAt = now;
        if (rows.Count > 0)
            await db.SaveChangesAsync(cancellationToken);
        foreach (var row in rows)
        {
            var id = SessionRules.SessionId(row.KeyHash);
            try
            {
                events.RaiseRevoked(id);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "工作階段撤銷通知失敗：{Session}", SessionRules.ShortId(id));
            }
        }
        return rows.Count;
    }
}
