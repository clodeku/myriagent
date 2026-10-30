using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Auth;

/// <summary>名冊裡的一位協作人（記憶體裡的快照；不可變，改動時整筆換掉）</summary>
internal sealed record CollaboratorEntry(string Id, string UserName, string DisplayName, long CreatedAt, long? DisabledAt, long? LastLoginAt, JObject Prefs)
{
    public bool Disabled => DisabledAt is not null;
}

/// <summary>名冊改了什麼（<see cref="CollaboratorDirectory.Changed"/>）：<see cref="Users"/> 是受影響的協作人（分享增減、介面設定、停用、刪除）</summary>
internal sealed record CollaboratorChange(bool List, bool Shares, bool Prefs, IReadOnlyList<string> Users, IReadOnlyList<string> Revoked);

/// <summary>管理動作的發起者（稽核用）：擁有者的瀏覽器工作階段或替擁有者送的桌面</summary>
internal sealed record CollaboratorActor(string Type, string? Id, string? Ip);

/// <summary>
/// 協作人的名冊（docs/plans/2026-10-30-collaborators.md 2.1）：帳號密碼在 Identity（hub.db 的 AspNetUsers），顯示名稱、停用、個人介面設定在 <c>collaborators</c>、
/// 分享在 <c>collaborator_shares</c>。啟動時整份載入記憶體，之後所有改動都經這裡（hub.db 的單一寫入者鎖內寫入、提交之後才換記憶體），
/// 票證、瀏覽器閘道、媒體端點只讀記憶體（任何執行緒；以 <see cref="gate"/> 保護）。
/// 停用、刪除、重設密碼時撤銷那位的所有工作階段（<see cref="SessionService.RevokeUserAsync"/>，連帶中斷他的即時連線），之後觸發 <see cref="Changed"/>
/// </summary>
internal sealed class CollaboratorDirectory(
    IServiceScopeFactory scopes,
    HubWriteLock writeLock,
    SessionService sessions,
    AuditLog audit,
    TimeProvider time,
    ILogger<CollaboratorDirectory> log)
{
    private readonly object gate = new();
    private Dictionary<string, CollaboratorEntry> byId = new(StringComparer.Ordinal);
    private Dictionary<string, HashSet<string>> sharesByConversation = new(StringComparer.Ordinal);
    private Dictionary<string, HashSet<string>> sharesByUser = new(StringComparer.Ordinal);
    private long version;

    /// <summary>名冊的版本（每次改動＋1；閘道以它判斷協作人的清單、擁有者的 <c>collaborators</c> 要不要重組）</summary>
    public long Version => Interlocked.Read(ref version);

    /// <summary>
    /// 提交之後（鎖外）同步呼叫：閘道與裝置 hub 的推送只記髒並喚醒，不做久的事；丟出的例外記錄後吞掉（資料已經改了，不能讓請求失敗）
    /// </summary>
    public event Action<CollaboratorChange>? Changed;

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var records = await db.Collaborators.AsNoTracking().ToListAsync(cancellationToken);
            var ids = records.Select(r => r.UserId).ToList();
            var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);
            var shares = await db.CollaboratorShares.AsNoTracking().ToListAsync(cancellationToken);
            var entries = new Dictionary<string, CollaboratorEntry>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                // Identity 那一列不在（只可能是手動改資料庫）：這位協作人登不進來，不列
                if (!names.TryGetValue(record.UserId, out var userName) || userName is null)
                {
                    log.LogWarning("協作人 {UserId} 沒有對應的 Identity 帳號，略過", record.UserId);
                    continue;
                }
                entries[record.UserId] = ToEntry(record, userName);
            }
            lock (gate)
            {
                byId = entries;
                (sharesByConversation, sharesByUser) = BuildShares(shares.Where(s => entries.ContainsKey(s.UserId)));
            }
            if (entries.Count > 0)
                log.LogInformation("載入 {Count} 位協作人、{Shares} 筆對話分享", entries.Count, shares.Count);
        }, cancellationToken);

    // ───── 讀（記憶體） ─────

    public CollaboratorEntry? Find(string? userId)
    {
        if (userId is null)
            return null;
        lock (gate)
            return byId.GetValueOrDefault(userId);
    }

    /// <summary>以帳號找（不分大小寫，同 <see cref="CollaboratorRules.CleanUserName"/>）</summary>
    public CollaboratorEntry? FindByUserName(string? userName)
    {
        var name = userName?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name))
            return null;
        lock (gate)
            return byId.Values.FirstOrDefault(e => string.Equals(e.UserName, name, StringComparison.Ordinal));
    }

    /// <summary>還在名冊裡而且沒停用（票證、瀏覽器 hub 的連線每次都看它）</summary>
    public bool IsActive(string? userId) => Find(userId) is { Disabled: false };

    public IReadOnlyList<CollaboratorEntry> List()
    {
        lock (gate)
            return byId.Values.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 這條連線看不看得到這個對話：<paramref name="collaboratorId"/> 是 null（擁有者）一律看得到；協作人要啟用中而且這個對話分享給他
    /// </summary>
    public bool CanSee(string? collaboratorId, string conversationId)
    {
        if (collaboratorId is null)
            return true;
        lock (gate)
        {
            return byId.TryGetValue(collaboratorId, out var entry) && !entry.Disabled
                && sharesByUser.TryGetValue(collaboratorId, out var shared) && shared.Contains(conversationId);
        }
    }

    /// <summary>分享給這位協作人的對話（線上的 id）；沒有是空集合</summary>
    public IReadOnlySet<string> SharedConversations(string collaboratorId)
    {
        lock (gate)
            return sharesByUser.TryGetValue(collaboratorId, out var shared) ? shared.ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>這個對話分享給誰（依名冊的順序）</summary>
    public IReadOnlyList<string> SharedWith(string conversationId)
    {
        lock (gate)
        {
            if (!sharesByConversation.TryGetValue(conversationId, out var users))
                return [];
            return byId.Values.Where(e => users.Contains(e.Id)).OrderBy(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal).Select(e => e.Id).ToList();
        }
    }

    /// <summary>給擁有者看的整份狀態（<paramref name="online"/>：目前有瀏覽器連線的協作人）</summary>
    public CollaboratorsState State(IReadOnlySet<string> online)
    {
        lock (gate)
        {
            var ordered = byId.Values.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
            var list = ordered.Select(e => new CollaboratorView(e.Id, e.UserName, e.DisplayName, e.Disabled, e.CreatedAt, e.LastLoginAt, online.Contains(e.Id))).ToList();
            var shares = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var (conversation, users) in sharesByConversation)
            {
                var ids = ordered.Where(e => users.Contains(e.Id)).Select(e => e.Id).ToList();
                if (ids.Count > 0)
                    shares[conversation] = ids;
            }
            return new CollaboratorsState(list, shares);
        }
    }

    /// <summary>協作人的介面設定（init 的 <c>settings</c>）：預設值蓋上他存過的</summary>
    public JObject SettingsOf(string collaboratorId)
    {
        var prefs = new Dictionary<string, JToken>(StringComparer.Ordinal);
        if (Find(collaboratorId) is { } entry)
        {
            foreach (var (key, value) in entry.Prefs)
            {
                if (value is not null)
                    prefs[key] = value;
            }
        }
        return MyriagentHub.Sync.AccountViewRules.Settings(prefs);
    }

    // ───── 寫 ─────

    /// <summary>新增：帳號、顯示名稱、密碼都照規則；成功回新的 id</summary>
    public async Task<CollabCmdResult> CreateAsync(string? userName, string? displayName, string? password, CollaboratorActor actor, CancellationToken cancellationToken)
    {
        var name = CollaboratorRules.CleanUserName(userName);
        var display = CollaboratorRules.CleanDisplayName(displayName);
        if (name is null || display is null)
            return Fail(CollaboratorCodes.Invalid);
        if (PasswordCode(password) is { } passwordProblem)
            return Fail(passwordProblem);
        var now = Now;
        string? created = null;
        string? code = null;
        await writeLock.RunAsync(async () =>
        {
            lock (gate)
            {
                if (byId.Count >= CollaboratorRules.MaxCollaborators)
                {
                    code = CollaboratorCodes.TooMany;
                    return;
                }
            }
            using var scope = scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            if (await users.FindByNameAsync(name) is not null)
            {
                code = CollaboratorCodes.UserNameTaken;
                return;
            }
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var user = new IdentityUser(name);
            var result = await users.CreateAsync(user, password!);
            if (!result.Succeeded)
            {
                log.LogWarning("新增協作人被 Identity 拒絕：{Codes}", string.Join("; ", result.Errors.Select(e => e.Code)));
                code = result.Errors.Any(e => e.Code is "DuplicateUserName") ? CollaboratorCodes.UserNameTaken : CollaboratorCodes.Invalid;
                return;
            }
            var record = new CollaboratorRecord { UserId = user.Id, DisplayName = display, CreatedAt = now, UpdatedAt = now };
            db.Collaborators.Add(record);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            lock (gate)
                byId = new Dictionary<string, CollaboratorEntry>(byId, StringComparer.Ordinal) { [user.Id] = ToEntry(record, name) };
            created = user.Id;
        }, cancellationToken);
        if (created is null)
            return Fail(code ?? CollaboratorCodes.Unavailable);
        await audit.WriteAsync(new AuditEntry(actor.Type, actor.Id, actor.Ip, AuditActions.CollaboratorCreated, created, AuditOutcomes.Ok, new { userName = name }), CancellationToken.None);
        Raise(new CollaboratorChange(true, false, false, [created], []));
        return new CollabCmdResult(true, UserId: created);
    }

    /// <summary>修改：顯示名稱、停用與否、新密碼（有帶的才改）。停用或重設密碼時撤銷他所有的工作階段</summary>
    public async Task<CollabCmdResult> UpdateAsync(string? userId, string? displayName, bool? disabled, string? password, CollaboratorActor actor,
        CancellationToken cancellationToken)
    {
        if (Find(userId) is not { } current)
            return Fail(CollaboratorCodes.NotFound);
        string? display = null;
        if (displayName is not null && (display = CollaboratorRules.CleanDisplayName(displayName)) is null)
            return Fail(CollaboratorCodes.Invalid);
        if (password is not null && PasswordCode(password) is { } passwordProblem)
            return Fail(passwordProblem);
        var now = Now;
        var fields = new List<string>(3);
        var revoke = false;
        CollaboratorEntry? updated = null;
        await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var record = await db.Collaborators.FirstOrDefaultAsync(c => c.UserId == current.Id, cancellationToken);
            var user = await users.FindByIdAsync(current.Id);
            if (record is null || user is null)
                return;
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            if (display is not null && display != record.DisplayName)
            {
                record.DisplayName = display;
                fields.Add("displayName");
            }
            if (disabled is { } off && off != (record.DisabledAt is not null))
            {
                record.DisabledAt = off ? now : null;
                fields.Add("disabled");
                revoke |= off;
            }
            if (password is not null)
            {
                user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
                var stamped = await users.UpdateSecurityStampAsync(user);
                if (!stamped.Succeeded)
                    throw new InvalidOperationException("重設協作人密碼失敗：" + string.Join("; ", stamped.Errors.Select(e => e.Code)));
                fields.Add("password");
                revoke = true;
            }
            record.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            updated = ToEntry(record, current.UserName) with { Prefs = current.Prefs };
            lock (gate)
                byId = new Dictionary<string, CollaboratorEntry>(byId, StringComparer.Ordinal) { [current.Id] = updated };
        }, cancellationToken);
        if (updated is null)
            return Fail(CollaboratorCodes.NotFound);
        var revoked = revoke ? await sessions.RevokeUserAsync(current.Id, CancellationToken.None) : 0;
        await audit.WriteAsync(new AuditEntry(actor.Type, actor.Id, actor.Ip, AuditActions.CollaboratorUpdated, current.Id, AuditOutcomes.Ok,
            new { fields, revokedSessions = revoked }), CancellationToken.None);
        Raise(new CollaboratorChange(true, disabled is not null, false, [current.Id], revoke ? [current.Id] : []));
        return new CollabCmdResult(true, UserId: current.Id);
    }

    /// <summary>刪除：分享、名冊、Identity 帳號同一個交易刪掉，再撤銷他的工作階段</summary>
    public async Task<CollabCmdResult> DeleteAsync(string? userId, CollaboratorActor actor, CancellationToken cancellationToken)
    {
        if (Find(userId) is not { } current)
            return Fail(CollaboratorCodes.NotFound);
        var deleted = false;
        await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.CollaboratorShares.Where(s => s.UserId == current.Id).ExecuteDeleteAsync(cancellationToken);
            await db.Collaborators.Where(c => c.UserId == current.Id).ExecuteDeleteAsync(cancellationToken);
            if (await users.FindByIdAsync(current.Id) is { } user)
            {
                var result = await users.DeleteAsync(user);
                if (!result.Succeeded)
                    throw new InvalidOperationException("刪除協作人失敗：" + string.Join("; ", result.Errors.Select(e => e.Code)));
            }
            await tx.CommitAsync(cancellationToken);
            lock (gate)
            {
                var next = new Dictionary<string, CollaboratorEntry>(byId, StringComparer.Ordinal);
                next.Remove(current.Id);
                byId = next;
                (sharesByConversation, sharesByUser) = BuildShares(Flatten().Where(s => s.UserId != current.Id));
            }
            deleted = true;
        }, cancellationToken);
        if (!deleted)
            return Fail(CollaboratorCodes.Unavailable);
        var revoked = await sessions.RevokeUserAsync(current.Id, CancellationToken.None);
        await audit.WriteAsync(new AuditEntry(actor.Type, actor.Id, actor.Ip, AuditActions.CollaboratorDeleted, current.Id, AuditOutcomes.Ok,
            new { userName = current.UserName, revokedSessions = revoked }), CancellationToken.None);
        Raise(new CollaboratorChange(true, true, false, [current.Id], [current.Id]));
        return new CollabCmdResult(true, UserId: current.Id);
    }

    /// <summary>
    /// 對話分享給誰（整份取代）：<paramref name="conversationId"/> 要是線上的對話 id（存不存在由呼叫端先查對話目錄）、<paramref name="userIds"/> 要都在名冊裡、
    /// 最多 <see cref="CollaboratorRules.MaxSharesPerConversation"/> 位（重複的去掉）
    /// </summary>
    public async Task<CollabCmdResult> ShareAsync(string? conversationId, IReadOnlyList<string>? userIds, CollaboratorActor actor, CancellationToken cancellationToken)
    {
        if (conversationId is null || !ConversationIds.IsWireId(conversationId) || userIds is null)
            return Fail(CollaboratorCodes.Invalid);
        var wanted = userIds.Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count > CollaboratorRules.MaxSharesPerConversation || wanted.Any(id => Find(id) is null))
            return Fail(CollaboratorCodes.Invalid);
        var now = Now;
        IReadOnlyList<string> affected = [];
        await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var existing = await db.CollaboratorShares.Where(s => s.ConversationId == conversationId).ToListAsync(cancellationToken);
            var before = existing.Select(s => s.UserId).ToHashSet(StringComparer.Ordinal);
            var after = wanted.Where(id => Find(id) is not null).ToHashSet(StringComparer.Ordinal);
            db.CollaboratorShares.RemoveRange(existing.Where(s => !after.Contains(s.UserId)));
            foreach (var id in after.Where(id => !before.Contains(id)))
                db.CollaboratorShares.Add(new CollaboratorShareRecord { ConversationId = conversationId, UserId = id, GrantedAt = now });
            await db.SaveChangesAsync(cancellationToken);
            affected = before.Union(after).Where(id => before.Contains(id) != after.Contains(id)).ToList();
            lock (gate)
            {
                var rows = Flatten().Where(s => s.ConversationId != conversationId)
                    .Concat(after.Select(id => new CollaboratorShareRecord { ConversationId = conversationId, UserId = id, GrantedAt = now }));
                (sharesByConversation, sharesByUser) = BuildShares(rows);
            }
        }, cancellationToken);
        await audit.WriteAsync(new AuditEntry(actor.Type, actor.Id, actor.Ip, AuditActions.CollaboratorShared, conversationId, AuditOutcomes.Ok,
            new { count = wanted.Count, changed = affected.Count }), CancellationToken.None);
        if (affected.Count > 0)
            Raise(new CollaboratorChange(false, true, false, affected, []));
        return new CollabCmdResult(true);
    }

    /// <summary>協作人改自己的一個介面設定（只收 <see cref="PrefsRules"/> 的四個鍵）；回 false＝不合格或不在名冊</summary>
    public async Task<bool> SetPrefAsync(string collaboratorId, string key, JToken value, CancellationToken cancellationToken)
    {
        if (!PrefsRules.IsValid(key, value) || Find(collaboratorId) is null)
            return false;
        var saved = false;
        await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var record = await db.Collaborators.FirstOrDefaultAsync(c => c.UserId == collaboratorId, cancellationToken);
            if (record is null || Find(collaboratorId) is not { } current)
                return;
            var prefs = (JObject)current.Prefs.DeepClone();
            prefs[key] = value.DeepClone();
            record.PrefsJson = prefs.ToString(Formatting.None);
            await db.SaveChangesAsync(cancellationToken);
            lock (gate)
                byId = new Dictionary<string, CollaboratorEntry>(byId, StringComparer.Ordinal) { [collaboratorId] = current with { Prefs = prefs } };
            saved = true;
        }, cancellationToken);
        if (saved)
            Raise(new CollaboratorChange(false, false, true, [collaboratorId], []));
        return saved;
    }

    /// <summary>登入成功：記下時間（失敗只記錄，不讓登入失敗）</summary>
    public async Task MarkLoginAsync(string collaboratorId, CancellationToken cancellationToken)
    {
        var now = Now;
        try
        {
            await writeLock.RunAsync(async () =>
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
                var record = await db.Collaborators.FirstOrDefaultAsync(c => c.UserId == collaboratorId, cancellationToken);
                if (record is null || Find(collaboratorId) is not { } current)
                    return;
                record.LastLoginAt = now;
                await db.SaveChangesAsync(cancellationToken);
                lock (gate)
                    byId = new Dictionary<string, CollaboratorEntry>(byId, StringComparer.Ordinal) { [collaboratorId] = current with { LastLoginAt = now } };
            }, cancellationToken);
            Raise(new CollaboratorChange(true, false, false, [collaboratorId], []));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "記下協作人的登入時間失敗：{UserId}", collaboratorId);
        }
    }

    // ───── 共用 ─────

    private static string? PasswordCode(string? password) => PasswordRules.CheckNew(password, null) switch
    {
        null => null,
        "password_too_short" => CollaboratorCodes.PasswordTooShort,
        "password_too_long" => CollaboratorCodes.PasswordTooLong,
        _ => CollaboratorCodes.PasswordInvalid,
    };

    private static CollabCmdResult Fail(string code) => new(false, code);

    private static CollaboratorEntry ToEntry(CollaboratorRecord record, string userName) =>
        new(record.UserId, userName, record.DisplayName, record.CreatedAt, record.DisabledAt, record.LastLoginAt, ParsePrefs(record.PrefsJson));

    private static JObject ParsePrefs(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new JObject();
        try
        {
            return JToken.Parse(json) as JObject ?? new JObject();
        }
        catch (JsonException)
        {
            return new JObject();
        }
    }

    /// <summary>目前記憶體裡的分享（在 <see cref="gate"/> 裡呼叫）</summary>
    private IEnumerable<CollaboratorShareRecord> Flatten() =>
        sharesByConversation.SelectMany(p => p.Value.Select(user => new CollaboratorShareRecord { ConversationId = p.Key, UserId = user })).ToList();

    private static (Dictionary<string, HashSet<string>> ByConversation, Dictionary<string, HashSet<string>> ByUser) BuildShares(IEnumerable<CollaboratorShareRecord> rows)
    {
        var byConversation = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byUser = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!byConversation.TryGetValue(row.ConversationId, out var users))
                byConversation[row.ConversationId] = users = new HashSet<string>(StringComparer.Ordinal);
            users.Add(row.UserId);
            if (!byUser.TryGetValue(row.UserId, out var conversations))
                byUser[row.UserId] = conversations = new HashSet<string>(StringComparer.Ordinal);
            conversations.Add(row.ConversationId);
        }
        return (byConversation, byUser);
    }

    private void Raise(CollaboratorChange change)
    {
        Interlocked.Increment(ref version);
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((Action<CollaboratorChange>)handler)(change);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "協作人名冊改變的處理者失敗");
            }
        }
    }
}
