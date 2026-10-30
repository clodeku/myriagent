using System.Security.Cryptography;
using System.Text;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Trust;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Pairing;

/// <summary>配對與名冊操作的失敗：<see cref="Code"/> 回給用戶端（JSON 的 error），<see cref="Status"/> 是 HTTP 狀態碼</summary>
internal sealed class PairingException(int status, string code, string? message = null, int? interval = null) : Exception(message ?? code)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    /// <summary>slow_down 時的新間隔（秒）</summary>
    public int? Interval { get; } = interval;
}

/// <summary>核准頁的內容（字串一律 textContent 顯示）</summary>
internal sealed record PendingPairing(
    string DeviceName,
    string? MachineName,
    string? WindowsUser,
    string KeyStorage,
    string? AppVersion,
    string? Ip,
    long CreatedAt,
    long ExpiresAt,
    string Fingerprint);

/// <summary>
/// 裝置配對（仿 RFC 8628，sync-protocol 2）與裝置名冊（7.4）：
/// <list type="bullet">
/// <item>start／token（裝置，匿名）：配對碼 10 分鐘、待核准最多 5 筆、deviceCode 只存 SHA-256 且只能換一次、輪詢太快回 slow_down。</item>
/// <item>pending／approve（擁有者，核准要近期驗證過密碼）：建 devices 列、名冊版本＋1、通知各裝置；
/// 伺服器是授權者，名冊不另外簽署（設計 14.4）。權杖在裝置下一次輪詢時才產生（只出現這一次）。</item>
/// <item>撤銷、移除已撤銷的（瀏覽器閘道以近期驗證呼叫）。</item>
/// </list>
/// hub.db 的「先讀再寫」都在 <see cref="HubWriteLock"/> 裡。
/// </summary>
internal sealed class PairingService(
    IServiceScopeFactory scopes,
    HubWriteLock writeLock,
    TrustService trust,
    HubIdentity identity,
    SyncWriter writer,
    Catalog catalog,
    DeviceDirectory directory,
    ConnectionRegistry connections,
    AccessTokenStore accessTokens,
    AuditLog audit,
    ConversationMaterializer materializer,
    Media.MediaRefIndexer media,
    IOptions<HubOptions> options,
    TimeProvider time)
{
    public async Task<PairStartResponse> StartAsync(PairStartRequest? request, string? ip, string? userAgent, CancellationToken cancellationToken = default)
    {
        if (PairingServerRules.CheckStart(request, options.Value.Sync.MinAppVersion) is { } error)
            throw new PairingException(StatusCodes.Status400BadRequest, error);
        var now = Now();
        var deviceCode = PairingRules.NewDeviceCode();
        var (id, userCode) = await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            await db.PairingRequests.Where(p => p.ExpiresAt < now - PairingServerRules.RetentionMs).ExecuteDeleteAsync(cancellationToken);
            var open = await db.PairingRequests.AsNoTracking()
                .Where(p => p.ExpiresAt > now && p.ApprovedAt == null && p.DeniedAt == null && p.ConsumedAt == null)
                .Select(p => p.UserCode).ToListAsync(cancellationToken);
            if (open.Count >= PairingRules.MaxPending)
                throw new PairingException(StatusCodes.Status400BadRequest, PairingErrors.TooManyPending);
            // 撤銷過的裝置不能用同一把金鑰回來；配對過的金鑰也不能再配一次（重新配對的裝置一律產生新金鑰）
            Base64UrlRules.TryDecode(request!.DevicePubKey, out var pubkey, P256KeyRules.MaxSpkiBytes);
            if (await db.Devices.AnyAsync(d => d.DevicePubkey == pubkey, cancellationToken))
                throw new PairingException(StatusCodes.Status400BadRequest, PairingErrors.InvalidRequest, "這把裝置金鑰已經配對過");
            string code;
            do
            {
                code = PairingRules.NewUserCode();
            }
            while (open.Contains(code));
            var record = new PairingRequestRecord
            {
                Id = SyncIds.NewUlid(now),
                DeviceCodeHash = HashDeviceCode(deviceCode),
                UserCode = code,
                DeviceName = PairingRules.CleanDeviceName(request.DeviceName)!,
                // 核准頁與裝置清單會顯示：去掉雙向覆寫等格式字元（不拒絕，見 CatalogRules.IsOptionalText）
                MachineName = CatalogRules.CleanText(request.MachineName, PairingServerRules.MaxTextLength),
                WindowsUser = CatalogRules.CleanText(request.WindowsUser, PairingServerRules.MaxTextLength),
                DevicePubkey = pubkey,
                KeyStorage = request.KeyStorage,
                RemoteIp = ip,
                UserAgent = userAgent,
                AppVersion = request.AppVersion,
                ProtocolVersion = request.ProtocolVersion,
                CreatedAt = now,
                ExpiresAt = now + PairingRules.ExpiresInSeconds * 1000L,
                IntervalSeconds = PairingRules.IntervalSeconds,
            };
            db.PairingRequests.Add(record);
            await db.SaveChangesAsync(cancellationToken);
            return (record.Id, code);
        }, cancellationToken);

        // 稽核不放配對碼與 deviceCode（驗證碼類）
        await audit.WriteAsync(new AuditEntry(AuditActors.Anonymous, null, ip, AuditActions.PairStarted, id, AuditOutcomes.Ok,
            new { deviceName = PairingRules.CleanDeviceName(request!.DeviceName), keyStorage = request.KeyStorage, appVersion = request.AppVersion }), cancellationToken);
        var origin = HostingRules.PublicOrigin(options.Value);
        return new PairStartResponse(deviceCode, userCode, origin + "/#pair", origin + "/#pair=" + userCode, PairingRules.ExpiresInSeconds, PairingRules.IntervalSeconds);
    }

    /// <summary>裝置輪詢；核准過就產生裝置權杖（只出現這一次）並附上完整的信任資料</summary>
    public async Task<PairTokenResponse> TokenAsync(PairTokenRequest? request, CancellationToken cancellationToken = default)
    {
        if (!PairingRules.IsDeviceCode(request?.DeviceCode))
            throw new PairingException(StatusCodes.Status400BadRequest, PairingErrors.InvalidRequest);
        var now = Now();
        var hash = HashDeviceCode(request!.DeviceCode);
        var (deviceId, token, requestId) = await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var record = await db.PairingRequests.FirstOrDefaultAsync(p => p.DeviceCodeHash == hash, cancellationToken);
            var verdict = PairingServerRules.Poll(record, now);
            if (record is not null && verdict is PollVerdict.SlowDown or PollVerdict.Pending)
            {
                if (verdict == PollVerdict.SlowDown)
                    record.IntervalSeconds += PairingRules.SlowDownIncrementSeconds;
                record.LastPollAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }
            switch (verdict)
            {
                case PollVerdict.SlowDown:
                    throw new PairingException(StatusCodes.Status400BadRequest, PairingErrors.SlowDown, interval: record!.IntervalSeconds);
                case PollVerdict.Pending:
                    throw new PairingException(StatusCodes.Status400BadRequest, PairingErrors.AuthorizationPending);
                case PollVerdict.AccessDenied:
                    throw new PairingException(StatusCodes.Status400BadRequest, PairingErrors.AccessDenied);
                case PollVerdict.Expired:
                    throw new PairingException(StatusCodes.Status400BadRequest, PairingErrors.ExpiredToken);
                case PollVerdict.InvalidGrant:
                    throw new PairingException(StatusCodes.Status400BadRequest, PairingErrors.InvalidGrant);
            }
            var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == record!.DeviceId, cancellationToken);
            if (device is null || device.RevokedAt is not null)
                throw new PairingException(StatusCodes.Status400BadRequest, PairingErrors.InvalidGrant);
            var fresh = DeviceTokenRules.NewToken();
            device.TokenHash = DeviceTokenRules.Hash(fresh);
            device.TokenIssuedAt = now;
            record!.ConsumedAt = now;
            record.LastPollAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return (device.Id, fresh, record.Id);
        }, cancellationToken);

        var bundle = await trust.GetBundleAsync(-1, cancellationToken);
        await audit.WriteAsync(new AuditEntry(AuditActors.Device, deviceId, null, AuditActions.PairCompleted, requestId, AuditOutcomes.Ok), cancellationToken);
        return new PairTokenResponse(deviceId, token, identity.Spki, writer.Instance, bundle);
    }

    public async Task<PendingPairing> PendingAsync(string? userCode, CancellationToken cancellationToken = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        var record = await FindPendingAsync(db, userCode, Now(), cancellationToken);
        var spki = Base64UrlRules.Encode(record.DevicePubkey);
        return new PendingPairing(record.DeviceName, record.MachineName, record.WindowsUser, record.KeyStorage, record.AppVersion, record.RemoteIp,
            record.CreatedAt, record.ExpiresAt, P256KeyRules.Fingerprint(spki));
    }

    /// <summary>核准（端點要求近期驗證）：建 devices 列、名冊版本＋1，之後通知各裝置</summary>
    public async Task ApproveAsync(string? userCode, string? sessionId, string? ip, CancellationToken cancellationToken = default)
    {
        var now = Now();
        var (device, requestId) = await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var record = await FindPendingAsync(db, userCode, now, cancellationToken);
            // 同一把金鑰的兩筆配對請求不能都被核准（開始時只比對已配對的；撤銷過的金鑰也不能回來）
            if (await db.Devices.AnyAsync(d => d.DevicePubkey == record.DevicePubkey, cancellationToken))
                throw new PairingException(StatusCodes.Status400BadRequest, PairingErrors.InvalidRequest, "這把裝置金鑰已經配對過");
            // 名冊含撤銷的裝置（不含已移除的），超過上限時裝置整份不採用（之後的撤銷也看不到）：到了就不再核准
            if (await db.Devices.CountAsync(d => d.RemovedAt == null, cancellationToken) >= DeviceRegistryRules.MaxDevices)
                throw new PairingException(StatusCodes.Status409Conflict, PairingErrors.TooManyDevices, "配對過的裝置（含已撤銷、不含已移除的）已達上限");
            record.DeviceId ??= SyncIds.NewDeviceId();
            var created = new DeviceRecord
            {
                Id = record.DeviceId,
                Name = record.DeviceName,
                MachineName = record.MachineName,
                WindowsUser = record.WindowsUser,
                // 權杖在裝置下一次輪詢時才產生；在那之前這一列沒有任何能用的權杖
                TokenHash = SHA256.HashData(RandomNumberGenerator.GetBytes(32)),
                DevicePubkey = record.DevicePubkey,
                KeyStorage = record.KeyStorage,
                AppVersion = record.AppVersion,
                ProtocolVersion = record.ProtocolVersion,
                CreatedAt = now,
            };
            db.Devices.Add(created);
            record.ApprovedAt = now;
            record.ApprovedSession = sessionId;
            await TrustService.NextRegistryRevAsync(db, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return (created, record.Id);
        }, cancellationToken);

        directory.Put(DeviceInfo.From(device));
        await writer.WriteAsync(context =>
        {
            Catalog.EnsureStream(context, device.Id);
            catalog.EmitDevice(context, device.Id);
        }, cancellationToken);
        await trust.PublishAsync(cancellationToken);
        // 稽核不放配對碼（它在待核准期間是換權杖的憑據之一）
        await audit.WriteAsync(new AuditEntry(AuditActors.Owner, sessionId, ip, AuditActions.PairApproved, device.Id, AuditOutcomes.Ok,
            new { requestId, deviceName = device.Name }), cancellationToken);
    }

    public async Task DenyAsync(string? userCode, string? sessionId, string? ip, CancellationToken cancellationToken = default)
    {
        var requestId = await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var record = await FindPendingAsync(db, userCode, Now(), cancellationToken);
            record.DeniedAt = Now();
            await db.SaveChangesAsync(cancellationToken);
            return record.Id;
        }, cancellationToken);
        await audit.WriteAsync(new AuditEntry(AuditActors.Owner, sessionId, ip, AuditActions.PairDenied, requestId, AuditOutcomes.Ok), cancellationToken);
    }

    /// <summary>
    /// 撤銷裝置（瀏覽器閘道以近期驗證呼叫）：hub.db 標撤銷、名冊版本＋1、寫稽核 device.revoked（同一個交易），之後作廢存取權杖、中斷連線、
    /// 通知在線的裝置、解除它的帳戶連結並發布 device.revoked。<paramref name="cancellationToken"/> 只管 hub.db 那一段（提交前取消＝什麼都沒寫，可以重試）；
    /// 提交之後的步驟一律做完，不理會取消（用戶端一斷線就跳過的話，sync.db 那一半就漏了）。已撤銷的回 409，回之前先補做之前那次沒做完的
    /// （<see cref="RevocationCompletion"/>；啟動時也會補）。
    /// <paramref name="reason"/>：<c>retired</c>（退役，之前送出的內容仍可信）或 <c>compromised</c>（疑似遭入侵）
    /// </summary>
    public async Task RevokeAsync(string deviceId, string reason, string? sessionId, string? ip, CancellationToken cancellationToken = default)
    {
        if (!DeviceRegistryRules.IsRevokedReason(reason))
            throw new PairingException(StatusCodes.Status400BadRequest, PairingErrors.InvalidRequest);
        var now = Now();
        var (device, alreadyRevoked) = await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var row = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
                ?? throw new PairingException(StatusCodes.Status404NotFound, "not_found");
            if (row.RevokedAt is not null)
                return (row, true);
            row.RevokedAt = now;
            row.RevokedReason = reason;
            row.PrevTokenHash = null;
            row.PrevTokenExpires = null;
            await TrustService.NextRegistryRevAsync(db, cancellationToken);
            // 稽核與撤銷同一次 SaveChanges（同一個 hub.db 交易；寫入鎖已持有）：撤銷生效就一定有稽核
            await AuditLog.AppendAsync(db, new AuditEntry(AuditActors.Owner, sessionId, ip, AuditActions.DeviceRevoked, deviceId, AuditOutcomes.Ok, new { reason }),
                now, cancellationToken);
            return (row, false);
        }, cancellationToken);

        // hub.db 已提交：以下都是冪等的，重送（已撤銷）時再做一次，補上之前那次可能沒做完的
        var info = DeviceInfo.From(device);
        directory.Put(info);
        accessTokens.RevokeDevice(deviceId);
        connections.AbortDevice(deviceId);
        // 名冊只看 hub.db：先通知在線的裝置，不受 sync.db 那一半成敗影響
        if (!alreadyRevoked)
            await trust.PublishAsync(CancellationToken.None);
        var completed = await writer.WriteAsync(context => RevocationCompletion.Complete(context, catalog, [info]), CancellationToken.None);
        if (!alreadyRevoked)
            return;
        // 之前那次沒做完 sync.db 那一半（也可能沒通知到）：補做之後照樣回 409
        if (completed.Count > 0)
            await trust.PublishAsync(CancellationToken.None);
        throw new PairingException(StatusCodes.Status409Conflict, "already_revoked");
    }

    /// <summary>
    /// 移除已撤銷的電腦（瀏覽器閘道以近期驗證呼叫；設計 14.22）：hub.db 標 <c>removed_at</c>、名冊版本＋1、寫稽核 device.removed（同一個交易），
    /// 之後通知在線的裝置（新名冊不再列它），在 sync.db 補做撤銷沒做完的那一半、刪掉它擁有的對話（<see cref="ConversationMaterializer.DeleteOwnedBy"/>）
    /// 並重發它的 device.upsert（瀏覽器與各台據此重讀目錄），最後請媒體參照的索引對帳。列留在 hub.db：公鑰照樣不能再配對、稽核對得上。
    /// 不存在 404；還沒撤銷 409 <c>not_revoked</c>；已移除的先補做 sync.db 那一半再回 409 <c>already_removed</c>（啟動時也會補）。
    /// <paramref name="cancellationToken"/> 只管 hub.db 那一段，提交之後的步驟一律做完（同 <see cref="RevokeAsync"/>）。回傳這次刪掉的對話數
    /// </summary>
    public async Task<int> RemoveAsync(string deviceId, string? sessionId, string? ip, CancellationToken cancellationToken = default)
    {
        var now = Now();
        var (device, alreadyRemoved) = await writeLock.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            var row = await db.Devices.FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken)
                ?? throw new PairingException(StatusCodes.Status404NotFound, "not_found");
            if (row.RevokedAt is null)
                throw new PairingException(StatusCodes.Status409Conflict, "not_revoked");
            if (row.RemovedAt is not null)
                return (row, true);
            row.RemovedAt = now;
            await TrustService.NextRegistryRevAsync(db, cancellationToken);
            await AuditLog.AppendAsync(db, new AuditEntry(AuditActors.Owner, sessionId, ip, AuditActions.DeviceRemoved, deviceId, AuditOutcomes.Ok), now, cancellationToken);
            return (row, false);
        }, cancellationToken);

        // hub.db 已提交：以下都是冪等的，重送（已移除）時再做一次，補上之前那次可能沒做完的
        var info = DeviceInfo.From(device);
        directory.Put(info);
        if (!alreadyRemoved)
            await trust.PublishAsync(CancellationToken.None);
        var deleted = await CompleteRemovalAsync([info], announce: !alreadyRemoved, CancellationToken.None);
        if (alreadyRemoved)
            throw new PairingException(StatusCodes.Status409Conflict, "already_removed");
        return deleted;
    }

    /// <summary>
    /// 移除的 sync.db 那一半（<paramref name="removed"/> 都是已移除的；移除本身與重送共用，啟動時的補做在 RealtimeRegistration、只刪對話）：
    /// 撤銷沒做完的先補（解除帳戶連結、device.revoked），刪掉它們擁有、還沒刪除的對話。<paramref name="announce"/>（剛移除）或這台有刪到對話時
    /// 重發它的 device.upsert（重送而什麼都沒補的不發事件）；有刪到時請媒體參照的索引對帳。回傳這次刪掉的對話數
    /// </summary>
    private async Task<int> CompleteRemovalAsync(IReadOnlyList<DeviceInfo> removed, bool announce, CancellationToken cancellationToken)
    {
        if (removed.Count == 0)
            return 0;
        var deleted = await writer.WriteAsync(context =>
        {
            RevocationCompletion.Complete(context, catalog, removed);
            var count = 0;
            foreach (var device in removed)
            {
                var conversations = materializer.DeleteOwnedBy(context, device.Id).Count;
                count += conversations;
                if (!announce && conversations == 0)
                    continue;
                // 瀏覽器的電腦頁與各台的目錄在 catalog 類的事件之後重讀（名冊另由 TrustChanged 通知）
                Catalog.EnsureStream(context, device.Id);
                catalog.EmitDevice(context, device.Id);
            }
            return count;
        }, cancellationToken);
        if (deleted > 0)
            media.RequestReconcile();
        return deleted;
    }

    // ───── 內部 ─────

    private long Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    private static byte[] HashDeviceCode(string deviceCode) => SHA256.HashData(Encoding.UTF8.GetBytes(deviceCode));

    private static async Task<PairingRequestRecord> FindPendingAsync(HubDbContext db, string? userCode, long now, CancellationToken cancellationToken)
    {
        var code = PairingRules.NormalizeUserCode(userCode) ?? throw new PairingException(StatusCodes.Status404NotFound, "not_found");
        var records = await db.PairingRequests.Where(p => p.UserCode == code).ToListAsync(cancellationToken);
        return records.FirstOrDefault(r => PairingServerRules.IsPending(r, now)) ?? throw new PairingException(StatusCodes.Status404NotFound, "not_found");
    }
}
