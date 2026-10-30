using Myriagent.Core;
using Myriagent.Security;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>引擎發布給宿主的目錄檢視（sync.db 的目錄快取與名冊；宿主組 devices、AccountView 的同步欄位、devicesCli）</summary>
/// <param name="LocalToLogical">本機帳戶 id → 邏輯帳戶 id（account.upsert 的 links 裡 deviceId 是自己的那幾格）</param>
/// <param name="Revoked">device.revoked（名冊之外的撤銷來源）</param>
internal sealed record SyncCatalogView(
    string SelfDeviceId,
    IReadOnlyDictionary<string, AccountUpsertPayload> Accounts,
    IReadOnlyDictionary<string, DeviceUpsertPayload> Devices,
    IReadOnlyList<DeviceRegistryEntry> Registry,
    IReadOnlyDictionary<string, string> LocalToLogical,
    IReadOnlyDictionary<string, DeviceRevokedPayload> Revoked);

/// <summary>
/// SyncStatus 的第 4 階段選用欄位（web-protocol 的 SyncStatus〔遠端〕；WP4.1 步 10 加上之後 <see cref="SyncViewRules"/> 凍結，只經這裡擴充）。
/// 每一欄 null＝不輸出（和 WP4.1 之前逐鍵相同）
/// </summary>
/// <param name="NotifyRemoteActions"><c>local.notifyRemoteActions</c>（WP4.6 起；D8，SyncSettings.NotifyRemoteActions）：只在也給了本機設定時輸出</param>
/// <param name="Backfill"><c>backfill</c>（WP4.3 起；D42）：舊對話回補到伺服器的進度，沒有在回補時給 null</param>
/// <param name="RemoteSupported"><c>remoteSupported</c>（WP4.4 起）：這台的程式能執行遠端指令</param>
/// <param name="PeerLive">桌面遠端操作（sync-protocol 14.7）：這台目前能不能對其他電腦下指令（web-protocol 的 <c>sync.peer</c>）；未配對是 null（不寫）</param>
/// <param name="AllowRemoteExec">這台的「允許其他電腦的代理在這台執行指令」（docs/sync-protocol.md 14.11；接得住遠端執行指令的宿主才有）</param>
internal sealed record SyncStatusExtras(bool? NotifyRemoteActions = null, SyncBackfillProgress? Backfill = null, bool? RemoteSupported = null, bool? PeerLive = null,
    bool? AllowRemoteExec = null);

/// <summary>回補的進度（web-protocol 的 <c>sync.backfill</c>；D42、設計 14.2 回補規則 3）</summary>
internal sealed record SyncBackfillProgress(int ConversationsLeft, long BytesLeft);

/// <summary>同步功能的開關</summary>
internal static class SyncFeatures
{
    /// <summary>
    /// D13：目錄有、這台沒有的邏輯帳戶（remoteOnly）放進 accounts，畫成「這台未登入」的卡片（可「在這台登入」）。
    /// WP3.5 的網頁支援在它的步驟 11，開關在 WP3.4 的「在這台登入」（accountAdd {logicalAccountId}）合併後打開，見設計 14.8
    /// </summary>
    public static bool RemoteOnlyAccounts => true;
}

/// <summary>同步的網頁檢視（web-protocol 的 Remote、DeviceView、SyncStatus、AccountView 的〔同步〕欄位、AccountDeviceView、DeviceCliView）</summary>
internal static class SyncViewRules
{
    public const string RemoteOnlyStatusText = "這台未登入";

    /// <summary>
    /// SyncStatus（web-protocol）＋ deviceId。<paramref name="local"/>＝這台的本機同步設定（只有桌面；WP3.4 D14）：給了就加
    /// <c>local {paused, remotePaused, remoteControlLevel, allowRemoteFullAccess}</c>，設定頁不必等伺服器的回音。
    /// <c>pairing.openable</c>＝配對網址合格、可以直接在瀏覽器開啟（<see cref="IsOpenablePairingUri"/>，D13）。
    /// <paramref name="extras"/>＝第 4 階段的選用欄位（<see cref="SyncStatusExtras"/>；null 或欄位是 null 時不輸出）
    /// </summary>
    public static JObject StatusView(SyncStatusSnapshot s, SyncSettings? local = null, SyncStatusExtras? extras = null)
    {
        var view = StatusCore(s);
        if (local is not null)
        {
            var localView = new JObject
            {
                ["paused"] = local.Paused,
                ["remotePaused"] = local.RemotePaused,
                ["remoteControlLevel"] = local.RemoteControlLevel,
                ["allowRemoteFullAccess"] = local.AllowRemoteFullAccess,
            };
            if (extras?.NotifyRemoteActions is { } notify)
                localView["notifyRemoteActions"] = notify;
            if (extras?.AllowRemoteExec is { } allowExec)
                localView["allowRemoteExec"] = allowExec;
            view["local"] = localView;
        }

        if (extras?.Backfill is { } backfill)
            view["backfill"] = new JObject { ["conversationsLeft"] = backfill.ConversationsLeft, ["bytesLeft"] = backfill.BytesLeft };
        if (extras?.RemoteSupported is { } supported)
            view["remoteSupported"] = supported;
        if (extras?.PeerLive is { } peer)
            view["peer"] = peer;
        return view;
    }

    /// <summary>
    /// 配對網址可以給「在瀏覽器開啟」（D13；網址文字一律照常顯示）：https、沒有帳號密碼、沒有查詢字串、路徑是 <c>/</c>、片段以 <c>#pair</c> 開頭，
    /// 主機與埠和顯示的伺服器相同（主機不分大小寫）；兩者都是迴路主機時也算，不比埠（本機開發時伺服器以 PublicOrigin 產生網址，和連線的位址不同）
    /// </summary>
    public static bool IsOpenablePairingUri(string? uri, string? server)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var target) || !Uri.TryCreate(server, UriKind.Absolute, out var origin))
            return false;
        if (target.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(target.UserInfo))
            return false;
        if (target.AbsolutePath != "/" || !string.IsNullOrEmpty(target.Query) || !target.Fragment.StartsWith("#pair", StringComparison.Ordinal))
            return false;
        if (string.Equals(target.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase) && target.Port == origin.Port)
            return true;
        return DevTrustRules.IsLoopback(target) && DevTrustRules.IsLoopback(origin);
    }

    private static JObject StatusCore(SyncStatusSnapshot s) => new()
    {
        ["state"] = SyncStatusRules.WebState(s.State, s.Pairing is not null),
        ["detail"] = s.Detail,
        ["server"] = s.Server,
        ["pending"] = s.Pending,
        ["lastSyncAt"] = s.LastSyncAt,
        ["clockSkewMs"] = s.ClockSkewMs,
        ["deviceId"] = s.DeviceId,
        ["deviceFingerprint"] = s.DeviceFingerprint,
        ["hubFingerprint"] = s.HubFingerprint,
        ["pairing"] = s.Pairing is { } p
            ? new JObject
            {
                ["userCode"] = p.UserCode,
                ["verificationUri"] = p.VerificationUri,
                ["verificationUriComplete"] = p.VerificationUriComplete,
                ["expiresAt"] = p.ExpiresAt,
                ["error"] = p.Error,
                ["openable"] = IsOpenablePairingUri(p.VerificationUriComplete, s.Server),
            }
            : null,
        ["confirm"] = s.ConfirmFingerprint is { } fingerprint
            ? new JObject { ["kind"] = "hubIdentity", ["fingerprint"] = fingerprint }
            : null,
    };

    /// <summary>
    /// 所有已知的電腦（device.upsert 與名冊的聯集，不含已移除的：<see cref="DeviceRemovalRules"/>）：已撤銷的排最後，其餘依名稱、id 排序。
    /// name 取 device.upsert（沒有用名冊的）、signedName 取名冊、verified＝在已採用的名冊裡而且格式合格
    /// </summary>
    public static JArray DevicesView(SyncCatalogView view) => new(OrderedDevices(view).Select(d => DeviceView(d, view)));

    /// <summary>AccountView 的〔同步〕欄位：logicalId、remoteOnly＝false、devices、otherSessions（沒連結的兩個都是 null）</summary>
    public static void Decorate(JObject accountView, SyncCatalogView view)
    {
        var localId = accountView.Value<string>("id");
        var logicalId = localId is not null && view.LocalToLogical.TryGetValue(localId, out var mapped) ? mapped : null;
        var account = logicalId is not null && view.Accounts.TryGetValue(logicalId, out var found) ? found : null;
        accountView["logicalId"] = logicalId;
        accountView["remoteOnly"] = false;
        accountView["devices"] = account is null ? null : AccountDevices(account, view);
        accountView["otherSessions"] = account is null ? null : OtherSessions(account, view);
    }

    /// <summary>
    /// 某台電腦的本機帳戶連結到的邏輯帳戶（account.upsert 的 links）：其他電腦的對話（副本）只帶擁有者那台的本機帳戶 id，
    /// 標頭的帳戶晶片與換帳戶選單要的是邏輯帳戶。沒有連結回 null；同一台的同一個本機帳戶萬一連到兩個邏輯帳戶，取目錄順序在前的
    /// </summary>
    public static string? LinkedLogical(SyncCatalogView view, string? deviceId, string? localAccountId)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(localAccountId))
            return null;
        return view.Accounts.Values
            .Where(a => a is not null && (a.Links ?? []).Any(l => l is not null && l.DeviceId == deviceId && l.LocalAccountId == localAccountId))
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.LogicalAccountId, StringComparer.Ordinal)
            .FirstOrDefault()?.LogicalAccountId;
    }

    /// <summary>
    /// 其他電腦上這個帳戶的對話數合計（web-protocol AccountView 的 otherSessions；側欄帳戶卡加上這台的 sessions 顯示所有電腦的執行中、等你回應）：
    /// 目錄裡各台連結的 sessions（那台最後一次 link.upsert 回報的），不算這台、撤銷或移除的、離線的（離線的那台數字停在當時，同伺服器的帳戶彙總與對話清單），
    /// 同一台有兩格時只算一格（同 <see cref="AccountDevices"/> 的挑法）
    /// </summary>
    public static JObject OtherSessions(AccountUpsertPayload account, SyncCatalogView view)
    {
        long open = 0, running = 0, waiting = 0, background = 0, programs = 0, subagents = 0;
        foreach (var device in OrderedDevices(view))
        {
            if (device.Revoked || device.Id == view.SelfDeviceId || device.Upsert?.Online != true)
                continue;
            var sessions = LinkOf(account, device.Id)?.Sessions;
            open += Math.Max(0, sessions?.Open ?? 0);
            running += Math.Max(0, sessions?.Running ?? 0);
            waiting += Math.Max(0, sessions?.Waiting ?? 0);
            background += Math.Max(0, sessions?.Background ?? 0);
            programs += Math.Max(0, sessions?.Programs ?? 0);
            subagents += Math.Max(0, sessions?.Subagents ?? 0);
        }

        return new JObject { ["open"] = open, ["running"] = running, ["waiting"] = waiting, ["background"] = background, ["programs"] = programs, ["subagents"] = subagents };
    }

    /// <summary>目錄有、這台沒有本機帳戶的邏輯帳戶（D13：SyncFeatures.RemoteOnlyAccounts 打開前宿主不送）</summary>
    public static JObject RemoteOnlyAccount(AccountUpsertPayload account, SharedUsage? usage, SyncCatalogView view) => new()
    {
        ["id"] = account.LogicalAccountId,
        ["provider"] = account.Provider,
        ["label"] = account.Label,
        ["color"] = account.Color,
        ["isSystemDefault"] = false,
        ["configDirectory"] = null,
        ["email"] = account.ExpectedEmail,
        ["plan"] = account.PlanHint,
        ["status"] = "notLoggedIn",
        ["statusText"] = RemoteOnlyStatusText,
        ["safeMode"] = false,
        ["loginBusy"] = false,
        ["usage"] = usage is null ? null : UsageView(usage),
        ["sessions"] = new JObject { ["open"] = 0, ["running"] = 0, ["waiting"] = 0, ["background"] = 0, ["programs"] = 0, ["subagents"] = 0 },
        ["logicalId"] = account.LogicalAccountId,
        ["remoteOnly"] = true,
        ["devices"] = AccountDevices(account, view),
        ["otherSessions"] = OtherSessions(account, view),
    };

    /// <summary>各電腦的 CLI 狀態：<c>{ "d_…": { "Claude": DeviceCliView, "Codex": … } }</c>（已撤銷、沒有回報的不列）</summary>
    public static JObject DevicesCli(SyncCatalogView view)
    {
        var result = new JObject();
        foreach (var device in OrderedDevices(view))
        {
            if (device.Revoked || device.Upsert?.Cli is not { Count: > 0 } cli)
                continue;
            var providers = new JObject();
            foreach (var (provider, state) in cli)
            {
                if (state is not null)
                    providers[provider] = CliView(state);
            }

            result[device.Id] = providers;
        }

        return result;
    }

    /// <summary>init.remote：桌面配對中（還沒有目錄）也帶 sync；<paramref name="local"/>、<paramref name="extras"/> 同 <see cref="StatusView"/></summary>
    public static JObject InitRemote(SyncStatusSnapshot status, SyncCatalogView? view, SyncSettings? local = null, SyncStatusExtras? extras = null) => new()
    {
        ["mode"] = "desktop",
        ["deviceId"] = status.DeviceId,
        ["devices"] = view is null ? new JArray() : DevicesView(view),
        ["sync"] = StatusView(status, local, extras),
    };

    /// <summary>
    /// 共享用量（usage.snapshot）的 Usage 檢視：source 一律 shared、沒有歷史。key 以 <see cref="UsageWindowKeyRules.DecodeAll"/> 一起還原
    /// （網頁拿 key 當時間窗的識別；還原後撞名時送原本的鍵，與瀏覽器閘道相同）
    /// </summary>
    public static JObject UsageView(SharedUsage usage)
    {
        var list = (usage.Windows ?? []).Where(w => w is not null).ToList();
        var keys = UsageWindowKeyRules.DecodeAll(list.Where(w => w.Key is not null).Select(w => w.Key).ToList());
        var windows = new JArray();
        var next = 0;
        foreach (var w in list)
        {
            windows.Add(new JObject
            {
                ["key"] = w.Key is null ? null : keys[next++],
                ["label"] = w.Label,
                ["percent"] = w.Percent,
                ["resetsAt"] = w.ResetsAt,
                ["severity"] = Myriagent.Usage.UsageRules.Severity(w.Percent, w.Locked),
                ["history"] = new JArray(),
            });
        }

        return new JObject
        {
            ["fetchedAt"] = usage.FetchedAt > 0 ? usage.FetchedAt : null,
            ["stale"] = usage.Stale,
            ["source"] = "shared",
            ["error"] = usage.Error,
            ["windows"] = windows,
            ["credits"] = usage.Credits,
            ["note"] = usage.Note,
        };
    }

    // ── 組裝 ──

    private sealed record DeviceRow(string Id, DeviceUpsertPayload? Upsert, DeviceRegistryEntry? Registered, DeviceRevokedPayload? RevokedEvent)
    {
        public bool Revoked => Registered?.RevokedAt is not null || RevokedEvent is not null;

        public string Name => Upsert?.Name ?? Registered?.Name ?? Id;
    }

    private static IEnumerable<DeviceRow> OrderedDevices(SyncCatalogView view)
    {
        var registry = new Dictionary<string, DeviceRegistryEntry>(StringComparer.Ordinal);
        foreach (var entry in view.Registry)
        {
            if (entry is not null)
                registry.TryAdd(entry.DeviceId, entry);
        }

        var removed = DeviceRemovalRules.Removed(view);
        return view.Devices.Keys.Concat(registry.Keys)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !removed.Contains(id))
            .Select(id => new DeviceRow(
                id,
                view.Devices.TryGetValue(id, out var upsert) ? upsert : null,
                registry.TryGetValue(id, out var registered) ? registered : null,
                view.Revoked.TryGetValue(id, out var revoked) ? revoked : null))
            .OrderBy(d => d.Revoked)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static JObject DeviceView(DeviceRow d, SyncCatalogView view)
    {
        var up = d.Upsert;
        var reg = d.Registered;
        JToken? revoked = reg?.RevokedAt is { } at
            ? new JObject { ["at"] = at, ["reason"] = reg.RevokedReason }
            : d.RevokedEvent is { } ev
                ? new JObject { ["at"] = ev.RevokedAt, ["reason"] = ev.Reason }
                : null;
        return new JObject
        {
            ["id"] = d.Id,
            ["name"] = d.Name,
            ["signedName"] = reg?.Name,
            ["self"] = d.Id == view.SelfDeviceId,
            ["machineName"] = up?.MachineName,
            ["appVersion"] = up?.AppVersion,
            ["fingerprint"] = up?.Fingerprint ?? (reg is not null && P256KeyRules.IsValidSpki(reg.DevicePubKey) ? P256KeyRules.Fingerprint(reg.DevicePubKey) : null),
            ["keyStorage"] = up?.KeyStorage,
            ["online"] = up?.Online ?? false,
            ["lastSeenAt"] = up?.LastSeenAt,
            ["remoteControlLevel"] = up?.Policy?.RemoteControlLevel ?? "Off",
            ["allowRemoteFullAccess"] = up?.Policy?.AllowRemoteFullAccess ?? false,
            ["allowRemoteExec"] = up?.Policy?.AllowRemoteExec ?? false,
            ["verified"] = reg is not null && DeviceRegistryRules.IsValid(reg),
            ["revoked"] = revoked,
        };
    }

    /// <summary>帳戶 × 電腦矩陣的這一列：每一台沒撤銷的電腦一格，沒有 link 的那台 status 為 absent</summary>
    private static JArray AccountDevices(AccountUpsertPayload account, SyncCatalogView view)
    {
        var result = new JArray();
        foreach (var device in OrderedDevices(view))
        {
            if (device.Revoked)
                continue;
            var link = LinkOf(account, device.Id);
            result.Add(new JObject
            {
                ["deviceId"] = device.Id,
                ["deviceName"] = device.Name,
                ["self"] = device.Id == view.SelfDeviceId,
                ["status"] = link?.Status ?? "absent",
                ["statusText"] = link?.StatusText,
                ["email"] = link?.Email,
                ["plan"] = link?.Plan,
                ["accessExpiresAt"] = link?.AccessExpiresAt,
                ["loginExpiresAt"] = link?.LoginExpiresAt,
                ["identityOk"] = link?.IdentityOk ?? false,
                ["usagePoller"] = link?.UsagePoller ?? false,
                ["online"] = device.Upsert?.Online ?? false,
                ["checkedAt"] = link?.CheckedAt,
            });
        }

        return result;
    }

    /// <summary>這台在矩陣裡的那一格：同一台有兩格時取 ready 的、再依本機帳戶 id</summary>
    private static AccountLinkView? LinkOf(AccountUpsertPayload account, string deviceId)
        => (account.Links ?? [])
            .Where(l => l is not null && l.DeviceId == deviceId)
            .OrderBy(l => l.Status == "ready" ? 0 : 1)
            .ThenBy(l => l.LocalAccountId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static JObject CliView(DeviceCliView v) => new()
    {
        ["installed"] = v.Installed,
        ["version"] = v.Version,
        ["installKind"] = v.InstallKind,
        ["latest"] = v.Latest,
        ["updateAvailable"] = v.UpdateAvailable,
        ["runningOnOldVersion"] = v.RunningOnOldVersion,
        ["restartPending"] = v.RestartPending,
        ["installBroken"] = v.InstallBroken,
        ["checkedAt"] = v.CheckedAt,
    };
}
