using System.Text.Json;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Audit;

/// <summary>要寫進稽核的一筆事件。detail 絕不放訊息原文、權杖、cookie、密碼、驗證碼（8.14）</summary>
internal sealed record AuditEntry(
    string ActorType,
    string? ActorId,
    string? Ip,
    string Action,
    string? Target,
    string Outcome,
    object? Detail = null);

internal static class AuditActors
{
    public const string Owner = "owner";
    public const string Device = "device";
    public const string System = "system";
    public const string Anonymous = "anonymous";
    public const string Admin = "admin";

    /// <summary>協作人（多人協作，2026-10-30）：actor_id 是工作階段的短 id</summary>
    public const string Collaborator = "collaborator";

    /// <summary>部署（runner 帳戶寫進 admin-inbox\deploy\ 的要求）</summary>
    public const string Deploy = "deploy";
}

internal static class AuditOutcomes
{
    public const string Ok = "ok";
    public const string Denied = "denied";
    public const string Error = "error";
}

/// <summary>稽核動作名稱（集中一處，查詢與告警才對得上）</summary>
internal static class AuditActions
{
    public const string OwnerCreated = "owner.created";
    public const string PasswordChanged = "owner.password_changed";
    public const string Login = "auth.login";
    public const string StepUp = "auth.step_up";
    public const string Logout = "auth.logout";
    public const string SessionRevoked = "session.revoked";
    public const string OwnerReset = "owner.reset";
    public const string AdminCommand = "admin.command";
    public const string Backup = "backup";
    public const string Restore = "restore";
    public const string Certificate = "tls.certificate";
    public const string SelfTest = "isolation.selftest";
    public const string VersionChanged = "deploy.version";
    public const string AuditPruned = "audit.pruned";
    public const string Drain = "server.drain";
    public const string HubIdentityRotated = "hub_identity.rotated";
    public const string PairStarted = "pair.started";
    public const string PairApproved = "pair.approved";
    public const string PairDenied = "pair.denied";
    public const string PairCompleted = "pair.completed";
    public const string DeviceRevoked = "device.revoked";

    /// <summary>擁有者把已撤銷的電腦從清單移除（設計 14.22；與 hub.db 的 removed_at 同一個交易）</summary>
    public const string DeviceRemoved = "device.removed";
    public const string DeviceHello = "device.hello";
    public const string DeviceTokenRotated = "device.token_rotated";
    public const string SyncSeqRegression = "sync.seq_regression";
    public const string SyncResync = "sync.resync";

    /// <summary>對話事件被拒（每台裝置每個時間窗彙總一筆，detail 只有各原因、各種類的筆數；WP4.2 D8）</summary>
    public const string SyncConversationRejected = "sync.conversation_rejected";

    /// <summary>窗內只有再遮蔽後接受的對話事件（第二道 DLP 命中；同上彙總，答 5）</summary>
    public const string SyncConversationRemasked = "sync.conversation_remasked";

    public const string AccountLinked = "account.linked";

    // 擁有者在瀏覽器上的修改（WP3.6）：detail 只記改了哪些欄位或筆數，不記名稱原文；prefs.set 記鍵與值（外觀偏好不是祕密）
    public const string PrefsSet = "prefs.set";
    public const string AccountUpdated = "account.updated";
    public const string AccountReordered = "account.reordered";
    public const string AccountDeleted = "account.deleted";
    public const string DeviceRenamed = "device.renamed";

    // 多人協作（2026-10-30）：擁有者管理協作人與對話的分享；detail 不記密碼
    public const string CollaboratorCreated = "collaborator.created";
    public const string CollaboratorUpdated = "collaborator.updated";
    public const string CollaboratorDeleted = "collaborator.deleted";
    public const string CollaboratorShared = "collaborator.shared";

    /// <summary>瀏覽器的遠端指令（WP4.5 D29）：每個指令一筆，ok＝已簽發寫列、denied＝伺服器立即拒絕；detail 只有遮蔽後的指令，不記原文與雜湊</summary>
    public const string CommandIssued = "command.issued";

    // 桌面版發佈（U2）：只記收件成功與撤回；拒收不寫稽核（原因寫服務記錄，回執帶 ASCII 代碼，U2 D19）、下載不寫稽核（U2 D21）
    public const string AppReleaseIngested = "app.release.ingested";
    public const string AppReleaseWithdrawn = "app.release.withdrawn";
}

/// <summary>稽核錨點：保留期清除舊列後，第一列應有的 prev_hash</summary>
internal sealed record AuditAnchor(long Seq, string PrevHash);

/// <summary>hub.db.audit 的寫入、查詢、驗證與保留期清除（只增不改；寫入經單一寫入者串列化，雜湊鏈不會分岔）</summary>
internal sealed class AuditLog(IServiceScopeFactory scopes, HubWriteLock writeLock, TimeProvider time, ILogger<AuditLog> log)
{
    public const int MaxDetailChars = 2000;

    private volatile AuditVerification? lastVerification;

    /// <summary>最後一次驗證的結果（啟動與每日驗證；ops/status 與健康狀態用）</summary>
    public AuditVerification? LastVerification => lastVerification;

    /// <summary>寫入一筆稽核；失敗只記錄、不往外丟（稽核寫不進去不應該讓登入之類的動作跟著失敗）</summary>
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        try
        {
            await writeLock.RunAsync(async () =>
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
                await AppendAsync(db, entry, time.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "稽核寫入失敗：{Action} {Outcome}", entry.Action, entry.Outcome);
        }
    }

    /// <summary>直接對指定的 DbContext 附加一列（呼叫端負責持有寫入鎖；還原指令在服務外使用）</summary>
    public static async Task AppendAsync(HubDbContext db, AuditEntry entry, long at, CancellationToken cancellationToken = default)
    {
        var prev = await db.Audit.AsNoTracking().OrderByDescending(a => a.Seq).Select(a => a.Hash).FirstOrDefaultAsync(cancellationToken)
            ?? AuditRules.GenesisHash();
        var detail = entry.Detail is null ? null : JsonSerializer.Serialize(entry.Detail, KvStore.Json);
        if (detail is { Length: > MaxDetailChars })
            detail = detail[..MaxDetailChars];
        var content = new AuditContent(at, entry.ActorType, entry.ActorId, entry.Ip, entry.Action, entry.Target, entry.Outcome, detail);
        db.Audit.Add(new AuditRecord
        {
            At = content.At,
            ActorType = content.ActorType,
            ActorId = content.ActorId,
            Ip = content.Ip,
            Action = content.Action,
            Target = content.Target,
            Outcome = content.Outcome,
            Detail = content.Detail,
            PrevHash = prev,
            Hash = AuditRules.ComputeHash(prev, content),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>逐批讀出整條鏈並驗證</summary>
    public async Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var result = await VerifyAsync(db, time.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
        lastVerification = result;
        if (!result.Ok)
            log.LogCritical("稽核雜湊鏈不一致：seq {Seq}，{Reason}", result.BrokenAtSeq, result.Reason);
        return result;
    }

    public static async Task<AuditVerification> VerifyAsync(HubDbContext db, long now, CancellationToken cancellationToken = default)
    {
        var anchor = await KvStore.GetAsync<AuditAnchor>(db, KvKeys.AuditAnchor, cancellationToken);
        var rows = new List<AuditChainRow>();
        long after = 0;
        const int batch = 2000;
        while (true)
        {
            var page = await db.Audit.AsNoTracking().Where(a => a.Seq > after).OrderBy(a => a.Seq).Take(batch).ToListAsync(cancellationToken);
            rows.AddRange(page.Select(a => new AuditChainRow(a.Seq,
                new AuditContent(a.At, a.ActorType, a.ActorId, a.Ip, a.Action, a.Target, a.Outcome, a.Detail), a.PrevHash, a.Hash)));
            if (page.Count < batch)
                break;
            after = page[^1].Seq;
        }
        var anchorHash = anchor is not null && rows.Count > 0 && rows[0].Seq == anchor.Seq
            ? Convert.FromBase64String(anchor.PrevHash)
            : AuditRules.GenesisHash();
        return AuditRules.Verify(rows, anchorHash, now);
    }

    /// <summary>最近的稽核（新到舊），給網頁設定頁查詢</summary>
    public async Task<IReadOnlyList<AuditRecord>> ListAsync(long? beforeSeq, int limit, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var query = db.Audit.AsNoTracking();
        if (beforeSeq is { } before)
            query = query.Where(a => a.Seq < before);
        return await query.OrderByDescending(a => a.Seq).Take(Math.Clamp(limit, 1, 200)).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// 刪除超過保留期（預設 400 天）的列，但至少留最後一列；把第一個保留列的 prev_hash 記成錨點，之後的驗證從錨點接起。
    /// </summary>
    public async Task<int> PruneAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        var cutoff = time.GetUtcNow().AddDays(-retentionDays).ToUnixTimeMilliseconds();
        var removed = await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var maxSeq = await db.Audit.MaxAsync(a => (long?)a.Seq, cancellationToken) ?? 0;
            var count = await db.Audit.Where(a => a.At < cutoff && a.Seq < maxSeq).ExecuteDeleteAsync(cancellationToken);
            if (count > 0)
            {
                var first = await db.Audit.AsNoTracking().OrderBy(a => a.Seq).Select(a => new { a.Seq, a.PrevHash }).FirstAsync(cancellationToken);
                await KvStore.SetAsync(db, KvKeys.AuditAnchor, new AuditAnchor(first.Seq, Convert.ToBase64String(first.PrevHash)), cancellationToken);
            }
            return count;
        }, cancellationToken);
        if (removed > 0)
            await WriteAsync(new AuditEntry(AuditActors.System, null, null, AuditActions.AuditPruned, null, AuditOutcomes.Ok, new { removed, retentionDays }), cancellationToken);
        return removed;
    }
}

internal static class AuditServiceCollectionExtensions
{
    public static IServiceCollection AddHubAudit(this IServiceCollection services)
    {
        services.AddSingleton<AuditLog>();
        services.AddSingleton<IStartupTask>(sp => new StartupTask(StartupOrder.Audit, "稽核雜湊鏈驗證", async ct => await sp.GetRequiredService<AuditLog>().VerifyAsync(ct)));
        return services;
    }
}
