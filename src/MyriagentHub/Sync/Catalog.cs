using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>套用一筆裝置寫的目錄事件的結果：null＝接受；否則是 BatchAck 的 rejected 原因</summary>
internal readonly record struct CatalogOutcome(string? Rejected)
{
    public static CatalogOutcome Accepted => default;

    public static CatalogOutcome Invalid => new(SyncErrorCodes.Invalid);

    public static CatalogOutcome Conflict => new(CatalogRejections.Conflict);
}

internal static class CatalogRejections
{
    /// <summary>樂觀並行的版本不符（sync-protocol 6.2）：裝置不重送，之後拉到的伺服器事件會蓋回</summary>
    public const string Conflict = "conflict";
}

/// <summary>
/// 帳戶目錄的物化（sync-protocol 第 6 節）：在 <see cref="SyncWriter"/> 的交易裡，與觸發它的事件一起提交——任何時刻讀到的
/// logical_accounts、account_links、prefs、device_cli、device_app 都對應某個 seq 前綴。結果一律以伺服器寫的事件發布（account.upsert、device.upsert、prefs.set…），
/// 裝置只套用伺服器寫的種類。瀏覽器閘道（WP3.6）的修改走同一組方法（不帶 baseVersion＝擁有者直接套用）。
/// </summary>
internal sealed class Catalog(DeviceDirectory devices, IDevicePresence presence, ISyncNotifier notifier, AuditLog audit)
{
    /// <summary>套用裝置寫的事件（種類已確認是 <see cref="SyncEventKinds.IsDeviceAuthored"/>、payload 已解碼並比對過雜湊）</summary>
    public CatalogOutcome ApplyDeviceEvent(SyncWriteContext context, string deviceId, string kind, string json)
    {
        try
        {
            return kind switch
            {
                SyncEventKinds.LinkUpsert => LinkUpsert(context, deviceId, SyncJson.Deserialize<LinkUpsertPayload>(json)),
                SyncEventKinds.LinkRemoved => LinkRemoved(context, deviceId, SyncJson.Deserialize<LinkRemovedPayload>(json)),
                SyncEventKinds.AccountUpdate => AccountUpdate(context, SyncJson.Deserialize<AccountUpdatePayload>(json)),
                SyncEventKinds.PrefsUpdate => PrefsUpdate(context, deviceId, SyncJson.Deserialize<PrefsUpdatePayload>(json)),
                SyncEventKinds.DeviceUpdate => DeviceUpdate(context, SyncJson.Deserialize<DeviceUpdatePayload>(json)),
                SyncEventKinds.CliState => CliState(context, deviceId, SyncJson.Deserialize<CliStatePayload>(json)),
                SyncEventKinds.AppState => AppState(context, deviceId, SyncJson.Deserialize<AppStatePayload>(json)),
                _ => CatalogOutcome.Invalid,
            };
        }
        catch (JsonException)
        {
            // 不是物件、型別不符、深度超過上限
            return CatalogOutcome.Invalid;
        }
    }

    /// <summary>
    /// 改邏輯帳戶的名稱、顏色、順序（裝置的 account.update 帶 <paramref name="baseVersion"/>；瀏覽器不帶，直接套用）。
    /// 帳戶不存在回 invalid；已刪除或版本不符回 conflict。
    /// </summary>
    public CatalogOutcome UpdateAccount(SyncWriteContext context, string logicalAccountId, long? baseVersion, string? label, string? color, int? sortOrder)
    {
        var row = ReadAccount(context, logicalAccountId);
        if (row is null)
            return CatalogOutcome.Invalid;
        if (row.DeletedAt is not null || (baseVersion is { } expected && expected != row.Version))
            return CatalogOutcome.Conflict;
        string? cleanLabel = null;
        if (label is not null && (cleanLabel = CatalogRules.CleanLabel(label)) is null)
            return CatalogOutcome.Invalid;
        if ((color is not null && !CatalogRules.IsColor(color)) || (sortOrder is { } order && !CatalogRules.IsSortOrder(order)))
            return CatalogOutcome.Invalid;
        context.Execute(
            "UPDATE logical_accounts SET label = COALESCE($label, label), color = COALESCE($color, color), sort_order = COALESCE($order, sort_order), version = version + 1 "
            + "WHERE logical_account_id = $id",
            ("$label", cleanLabel), ("$color", color), ("$order", sortOrder), ("$id", logicalAccountId));
        EmitAccount(context, logicalAccountId);
        return CatalogOutcome.Accepted;
    }

    /// <summary>漫遊偏好（只接受 <see cref="PrefsRules"/> 的白名單；裝置帶 baseVersion，瀏覽器不帶）</summary>
    public CatalogOutcome SetPreference(SyncWriteContext context, string updatedBy, string? key, JToken? value, long? baseVersion)
    {
        if (!PrefsRules.IsValid(key, value))
            return CatalogOutcome.Invalid;
        var current = context.Scalar("SELECT version FROM prefs WHERE key = $k", ("$k", key)) is long v ? v : 0;
        if (baseVersion is { } expected && expected != current)
            return CatalogOutcome.Conflict;
        var next = current + 1;
        context.Execute(
            "INSERT INTO prefs(key, value_json, version, updated_at, updated_by) VALUES($k, $v, $ver, $now, $by) "
            + "ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json, version = excluded.version, updated_at = excluded.updated_at, updated_by = excluded.updated_by",
            ("$k", key), ("$v", value!.ToString(Formatting.None)), ("$ver", next), ("$now", context.Now), ("$by", updatedBy));
        SyncEventStore.InsertServer(context, notifier, SyncEventKinds.PrefsSet, new PrefsSetPayload(key!, value, next));
        return CatalogOutcome.Accepted;
    }

    /// <summary>改裝置的顯示名稱（名冊裡配對時的名稱不變）</summary>
    public CatalogOutcome RenameDevice(SyncWriteContext context, string deviceId, string? name)
    {
        var target = devices.Get(deviceId);
        var clean = PairingRules.CleanDeviceName(name);
        if (target is null || target.Revoked || clean is null)
            return CatalogOutcome.Invalid;
        EnsureStream(context, deviceId);
        context.Execute("UPDATE device_streams SET name = $name WHERE device_id = $id", ("$name", clean), ("$id", deviceId));
        EmitDevice(context, deviceId);
        return CatalogOutcome.Accepted;
    }

    /// <summary>從目錄刪除邏輯帳戶（瀏覽器，WP3.6）：各台解除連結、本機帳戶保留；發布 account.deleted</summary>
    public CatalogOutcome DeleteAccount(SyncWriteContext context, string logicalAccountId)
    {
        var row = ReadAccount(context, logicalAccountId);
        if (row is null || row.DeletedAt is not null)
            return CatalogOutcome.Invalid;
        context.Execute("UPDATE logical_accounts SET deleted_at = $now, version = version + 1 WHERE logical_account_id = $id", ("$now", context.Now), ("$id", logicalAccountId));
        context.Execute("DELETE FROM account_links WHERE logical_account_id = $id", ("$id", logicalAccountId));
        SyncEventStore.InsertServer(context, notifier, SyncEventKinds.AccountDeleted, new AccountDeletedPayload(logicalAccountId, row.Version + 1));
        return CatalogOutcome.Accepted;
    }

    /// <summary>發布邏輯帳戶整筆＋各電腦矩陣（account.upsert）</summary>
    public long EmitAccount(SyncWriteContext context, string logicalAccountId)
    {
        var row = ReadAccount(context, logicalAccountId) ?? throw new InvalidOperationException("沒有這個邏輯帳戶：" + logicalAccountId);
        var poller = context.Scalar("SELECT device_id FROM usage_pollers WHERE logical_account_id = $id", ("$id", logicalAccountId)) as string;
        var links = new List<AccountLinkView>();
        using (var cmd = context.Command(
            "SELECT device_id, local_account_id, config_kind, status, status_text, email, org_id, plan, access_expires_at, login_expires_at, login_busy, identity_ok, sessions_json, checked_at "
            + "FROM account_links WHERE logical_account_id = $id ORDER BY device_id", ("$id", logicalAccountId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var deviceId = reader.GetString(0);
                links.Add(new AccountLinkView(
                    deviceId,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    NullableString(reader, 4),
                    NullableString(reader, 5),
                    NullableString(reader, 6),
                    NullableString(reader, 7),
                    NullableLong(reader, 8),
                    NullableLong(reader, 9),
                    reader.GetInt64(10) != 0,
                    reader.GetInt64(11) != 0,
                    NullableString(reader, 12) is { } sessions ? SyncJson.Deserialize<LinkSessions>(sessions) : null,
                    NullableLong(reader, 13),
                    deviceId == poller));
            }
        }
        var payload = new AccountUpsertPayload(row.Id, row.Provider, row.IdentityKey, row.Label, row.Color, row.SortOrder, row.ExpectedEmail, row.ExpectedOrgId, row.PlanHint, row.Version, links);
        return SyncEventStore.InsertServer(context, notifier, SyncEventKinds.AccountUpsert, payload);
    }

    /// <summary>
    /// 發布裝置（device.upsert）：不變資料取自 <see cref="DeviceDirectory"/>，顯示名稱、版本、政策、CLI 狀態與 app 的更新狀態取自 sync.db，上線狀態與協商出的功能取自連線登記
    /// （功能是空的時候不寫 <c>features</c>，與第 3 階段逐位元組相同，sync-protocol 14.1；沒回報過 app.state 時不寫 <c>app</c>，sync-protocol 6.6）。
    /// 讀不出來的 CLI 或 app 狀態當成沒有：每次 Hello 都經過這裡，不能因為一列壞掉就擋住交握
    /// </summary>
    public long EmitDevice(SyncWriteContext context, string deviceId)
    {
        var info = devices.Get(deviceId) ?? throw new InvalidOperationException("沒有這台裝置：" + deviceId);
        string? name = null;
        string? appVersion = null;
        long? lastSeen = null;
        DevicePolicy? policy = null;
        using (var cmd = context.Command("SELECT name, app_version, last_hello_at, last_heartbeat_at, policy_json FROM device_streams WHERE device_id = $id", ("$id", deviceId)))
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                name = NullableString(reader, 0);
                appVersion = NullableString(reader, 1);
                lastSeen = Max(NullableLong(reader, 2), NullableLong(reader, 3));
                policy = NullableString(reader, 4) is { } json ? SafeDeserialize<DevicePolicy>(json) : null;
            }
        }
        var cli = new Dictionary<string, DeviceCliView>(StringComparer.Ordinal);
        using (var cmd = context.Command("SELECT provider, state_json FROM device_cli WHERE device_id = $id ORDER BY provider", ("$id", deviceId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (SafeDeserialize<DeviceCliView>(reader.GetString(1)) is { } view)
                    cli[reader.GetString(0)] = view;
            }
        }
        var app = context.Scalar("SELECT state_json FROM device_app WHERE device_id = $id", ("$id", deviceId)) is string appJson
            ? SafeDeserialize<AppStatePayload>(appJson)
            : null;
        var features = presence.FeaturesOf(deviceId);
        var payload = new DeviceUpsertPayload(info.Id, name ?? info.Name, info.MachineName, appVersion ?? info.AppVersion, info.KeyStorage, info.Fingerprint,
            info.CreatedAt, lastSeen, presence.IsOnline(deviceId), policy, cli.Count > 0 ? cli : null, features.Count > 0 ? features : null, app);
        return SyncEventStore.InsertServer(context, notifier, SyncEventKinds.DeviceUpsert, payload);
    }

    /// <summary>撤銷裝置（瀏覽器的近期驗證動作）：解除它的帳戶連結、發布 device.revoked 與受影響帳戶的 account.upsert</summary>
    public void RevokeDevice(SyncWriteContext context, string deviceId, long revokedAt, string reason)
    {
        var affected = new List<string>();
        using (var cmd = context.Command("SELECT logical_account_id FROM account_links WHERE device_id = $id", ("$id", deviceId)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                affected.Add(reader.GetString(0));
        }
        context.Execute("DELETE FROM account_links WHERE device_id = $id", ("$id", deviceId));
        SyncEventStore.InsertServer(context, notifier, SyncEventKinds.DeviceRevoked, new DeviceRevokedPayload(deviceId, revokedAt, reason));
        foreach (var logicalAccountId in affected)
            EmitAccount(context, logicalAccountId);
    }

    /// <summary>裝置的串流列（配對核准時建立；舊資料或還原後缺的補上）</summary>
    public static void EnsureStream(SyncWriteContext context, string deviceId) =>
        context.Execute("INSERT INTO device_streams(device_id) VALUES($id) ON CONFLICT(device_id) DO NOTHING", ("$id", deviceId));

    // ───── 裝置寫的事件 ─────

    /// <summary>
    /// 本機帳戶的狀態摘要（sync-protocol 6.1）。對應的邏輯帳戶依序取：這個本機帳戶已有的連結 → 裝置認定的 logicalAccountId（「在這台登入」目錄裡的帳戶）
    /// → identityKey 相同的邏輯帳戶 → 以這台的名稱與顏色新建（identityKey 是 null 時不新建）。連結後身分不同（這台改登別的帳號）標 identityOk=false。
    /// </summary>
    private CatalogOutcome LinkUpsert(SyncWriteContext context, string deviceId, LinkUpsertPayload? p)
    {
        if (CatalogRules.ValidateLink(p) is not null)
            return CatalogOutcome.Invalid;
        var existing = context.Scalar("SELECT logical_account_id FROM account_links WHERE device_id = $d AND local_account_id = $l", ("$d", deviceId), ("$l", p!.LocalAccountId)) as string;
        var account = existing is not null ? ReadAccount(context, existing) : null;
        if (account is { DeletedAt: not null })
            account = null;
        if (account is null && p.LogicalAccountId is not null && ReadAccount(context, p.LogicalAccountId) is { DeletedAt: null } claimed && claimed.Provider == p.Provider)
            account = claimed;
        var created = false;
        if (account is null && p.IdentityKey is not null)
        {
            account = ReadAccountByIdentity(context, p.IdentityKey);
            if (account is { DeletedAt: not null })
            {
                // 被刪除的邏輯帳戶又有裝置回報同一個身分：恢復它（identity_key 唯一，不能另建）
                context.Execute("UPDATE logical_accounts SET deleted_at = NULL, version = version + 1 WHERE logical_account_id = $id", ("$id", account.Id));
                account = ReadAccount(context, account.Id);
            }
            if (account is null)
            {
                var id = SyncIds.NewLogicalAccountId();
                context.Execute(
                    "INSERT INTO logical_accounts(logical_account_id, provider, identity_key, label, color, sort_order, expected_email, expected_org_id, plan_hint, version, created_at) "
                    + "VALUES($id, $provider, $key, $label, $color, $order, $email, $org, $plan, 1, $now)",
                    ("$id", id), ("$provider", p.Provider), ("$key", p.IdentityKey), ("$label", CatalogRules.CleanLabel(p.Label)), ("$color", p.Color),
                    ("$order", p.SortOrder), ("$email", CatalogRules.CleanText(p.Email, CatalogRules.MaxEmailLength)),
                    ("$org", CatalogRules.CleanText(p.OrgId, CatalogRules.MaxShortTextLength)), ("$plan", CatalogRules.CleanText(p.Plan, CatalogRules.MaxShortTextLength)), ("$now", context.Now));
                account = ReadAccount(context, id);
                created = true;
            }
        }
        if (account is null)
            return CatalogOutcome.Accepted;

        if (existing is not null && existing != account.Id)
        {
            context.Execute("DELETE FROM account_links WHERE device_id = $d AND local_account_id = $l", ("$d", deviceId), ("$l", p.LocalAccountId));
            if (ReadAccount(context, existing) is { DeletedAt: null })
                EmitAccount(context, existing);
        }
        var firstLink = context.Scalar("SELECT COUNT(*) FROM account_links WHERE logical_account_id = $id AND device_id = $d", ("$id", account.Id), ("$d", deviceId)) is 0L;
        var identityOk = p.IdentityKey is null || p.IdentityKey == account.IdentityKey;
        context.Execute(
            "INSERT INTO account_links(logical_account_id, device_id, local_account_id, config_kind, status, status_text, email, org_id, plan, access_expires_at, login_expires_at, "
            + "login_busy, identity_ok, sessions_json, checked_at, version) "
            + "VALUES($id, $d, $l, $kind, $status, $text, $email, $org, $plan, $access, $login, $busy, $ok, $sessions, $checked, 1) "
            + "ON CONFLICT(logical_account_id, device_id) DO UPDATE SET local_account_id = excluded.local_account_id, config_kind = excluded.config_kind, "
            + "status = excluded.status, status_text = excluded.status_text, email = excluded.email, org_id = excluded.org_id, plan = excluded.plan, "
            + "access_expires_at = excluded.access_expires_at, login_expires_at = excluded.login_expires_at, login_busy = excluded.login_busy, "
            + "identity_ok = excluded.identity_ok, sessions_json = excluded.sessions_json, checked_at = excluded.checked_at, version = account_links.version + 1",
            ("$id", account.Id), ("$d", deviceId), ("$l", p.LocalAccountId), ("$kind", p.ConfigKind), ("$status", p.Status),
            ("$text", CatalogRules.CleanText(p.StatusText, CatalogRules.MaxTextLength)), ("$email", CatalogRules.CleanText(p.Email, CatalogRules.MaxEmailLength)),
            ("$org", CatalogRules.CleanText(p.OrgId, CatalogRules.MaxShortTextLength)), ("$plan", CatalogRules.CleanText(p.Plan, CatalogRules.MaxShortTextLength)),
            ("$access", p.AccessExpiresAt), ("$login", p.LoginExpiresAt), ("$busy", p.LoginBusy ? 1 : 0),
            ("$ok", identityOk ? 1 : 0), ("$sessions", SyncJson.Serialize(p.Sessions)), ("$checked", p.CheckedAt));
        EmitAccount(context, account.Id);
        if (firstLink)
        {
            var linkedId = account.Id;
            context.AuditAfterCommit(audit, new AuditEntry(AuditActors.Device, deviceId, null, AuditActions.AccountLinked, linkedId, AuditOutcomes.Ok,
                new { localAccountId = p.LocalAccountId, created, identityOk }));
        }
        return CatalogOutcome.Accepted;
    }

    private CatalogOutcome LinkRemoved(SyncWriteContext context, string deviceId, LinkRemovedPayload? p)
    {
        if (p is null || !CatalogRules.IsLocalAccountId(p.LocalAccountId))
            return CatalogOutcome.Invalid;
        var existing = context.Scalar("SELECT logical_account_id FROM account_links WHERE device_id = $d AND local_account_id = $l", ("$d", deviceId), ("$l", p.LocalAccountId)) as string;
        if (existing is null)
            return CatalogOutcome.Accepted;
        context.Execute("DELETE FROM account_links WHERE device_id = $d AND local_account_id = $l", ("$d", deviceId), ("$l", p.LocalAccountId));
        if (ReadAccount(context, existing) is { DeletedAt: null })
            EmitAccount(context, existing);
        return CatalogOutcome.Accepted;
    }

    private CatalogOutcome AccountUpdate(SyncWriteContext context, AccountUpdatePayload? p) =>
        p is null || !SyncIds.IsLogicalAccountId(p.LogicalAccountId)
            ? CatalogOutcome.Invalid
            : UpdateAccount(context, p.LogicalAccountId, p.BaseVersion, p.Label, p.Color, p.SortOrder);

    private CatalogOutcome PrefsUpdate(SyncWriteContext context, string deviceId, PrefsUpdatePayload? p) =>
        p is null ? CatalogOutcome.Invalid : SetPreference(context, deviceId, p.Key, p.Value, p.BaseVersion);

    private CatalogOutcome DeviceUpdate(SyncWriteContext context, DeviceUpdatePayload? p) =>
        p is null || !SyncIds.IsDeviceId(p.DeviceId) ? CatalogOutcome.Invalid : RenameDevice(context, p.DeviceId, p.Name);

    private CatalogOutcome CliState(SyncWriteContext context, string deviceId, CliStatePayload? p)
    {
        if (CatalogRules.ValidateCli(p) is not null)
            return CatalogOutcome.Invalid;
        context.Execute("DELETE FROM device_cli WHERE device_id = $d", ("$d", deviceId));
        foreach (var (provider, view) in p!.Providers)
        {
            context.Execute("INSERT INTO device_cli(device_id, provider, state_json, updated_at) VALUES($d, $p, $json, $now)",
                ("$d", deviceId), ("$p", provider), ("$json", SyncJson.Serialize(view)), ("$now", context.Now));
        }
        EnsureStream(context, deviceId);
        EmitDevice(context, deviceId);
        return CatalogOutcome.Accepted;
    }

    /// <summary>
    /// 這台 app 的更新狀態（app 自我更新規格 7.8，sync-protocol 6.6）：存進 device_app（每台一列，新的取代舊的）再發布 device.upsert。存的是整理過的內容：
    /// lastError 去掉格式字元（同目錄的其他顯示文字，<see cref="CatalogRules.CleanText"/>），state_json 由伺服器重新序列化，裝置多送的欄位不轉存；
    /// updated_at 是伺服器收到的時間（不是裝置的 checkedAt）
    /// </summary>
    private CatalogOutcome AppState(SyncWriteContext context, string deviceId, AppStatePayload? p)
    {
        if (p is null || CatalogRules.ValidateApp(p) is not null)
            return CatalogOutcome.Invalid;
        var stored = p with { LastError = CatalogRules.CleanText(p.LastError, CatalogRules.MaxTextLength) };
        context.Execute("INSERT OR REPLACE INTO device_app(device_id, state_json, updated_at) VALUES($d, $json, $now)",
            ("$d", deviceId), ("$json", SyncJson.Serialize(stored)), ("$now", context.Now));
        EnsureStream(context, deviceId);
        EmitDevice(context, deviceId);
        return CatalogOutcome.Accepted;
    }

    // ───── 讀取 ─────

    private sealed record AccountRow(
        string Id,
        string Provider,
        string IdentityKey,
        string Label,
        string Color,
        int SortOrder,
        string? ExpectedEmail,
        string? ExpectedOrgId,
        string? PlanHint,
        long Version,
        long? DeletedAt);

    private const string AccountColumns =
        "logical_account_id, provider, identity_key, label, color, sort_order, expected_email, expected_org_id, plan_hint, version, deleted_at";

    private static AccountRow? ReadAccount(SyncWriteContext context, string logicalAccountId) =>
        ReadAccountWhere(context, "logical_account_id = $v", logicalAccountId);

    private static AccountRow? ReadAccountByIdentity(SyncWriteContext context, string identityKey) =>
        ReadAccountWhere(context, "identity_key = $v", identityKey);

    private static AccountRow? ReadAccountWhere(SyncWriteContext context, string where, string value)
    {
        using var cmd = context.Command($"SELECT {AccountColumns} FROM logical_accounts WHERE {where}", ("$v", value));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new AccountRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
            NullableString(reader, 6), NullableString(reader, 7), NullableString(reader, 8), reader.GetInt64(9), NullableLong(reader, 10));
    }

    private static string? NullableString(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    private static long? NullableLong(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);

    private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static T? SafeDeserialize<T>(string json) where T : class
    {
        try
        {
            return SyncJson.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
