using Myriagent.Agents;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>logical_accounts 的一列（未刪除）</summary>
internal sealed record AccountViewRow(string Id, string Provider, string Label, string Color, int SortOrder,
    string? ExpectedEmail, string? PlanHint, long CreatedAt);

/// <summary>account_links 的一列（「邏輯帳戶 × 電腦」的一格）；JSON 欄位解析失敗的 <see cref="Sessions"/> 是 null</summary>
internal sealed record LinkViewRow(string LogicalAccountId, string DeviceId, string Status, string? StatusText, string? Email, string? Plan,
    long? AccessExpiresAt, long? LoginExpiresAt, bool LoginBusy, bool IdentityOk, LinkSessions? Sessions, long? CheckedAt);

/// <summary>一台已配對的電腦：hub.db 的裝置資料＋sync.db 的 device_streams＋目前在不在線</summary>
internal sealed record DeviceViewRow(DeviceInfo Info, string? StreamName, string? StreamAppVersion, long? LastHelloAt, long? LastHeartbeatAt,
    DevicePolicy? Policy, bool Online);

/// <summary>用量歷史的一點（usage_points 的 at、percent）</summary>
internal sealed record UsagePoint(long At, double Percent);

/// <summary>
/// 瀏覽器模式的宿主訊息內容（web-protocol 第三節 AccountView、AccountDeviceView、Usage、DeviceView、DeviceCliView、Settings、ProviderView；
/// 第六節）。輸入是 sync.db 與裝置目錄的資料列，裡面的文字都來自裝置、一律當成不可信：輸出逐欄組成（白名單），
/// 不把裝置送來的 JSON 原樣轉出，也絕不帶權杖、本機路徑、Windows 使用者名稱、本機帳戶 id、身分鍵、組織 id（設計 0.3、6.1）。
/// 一律以 JObject／JArray 組，沒有值的欄位寫 null（同桌面 AgentHub.Render）。
/// 嚴重度與降採樣照抄桌面（MyriagentHub 不能參考桌面組件），測試用同一組向量。
/// </summary>
internal static class AccountViewRules
{
    public const long HourMs = 3_600_000;

    /// <summary>走勢線取最近 7 天（同桌面 UsageHistoryStore.ViewSpan）</summary>
    public const long HistorySpanMs = 7 * 24 * HourMs;

    /// <summary>走勢線最多 168 點（同桌面 UsageHistoryStore.MaxViewPoints）</summary>
    public const int MaxHistoryPoints = 168;

    public const int MaxCliCount = 100_000;

    public const int MaxCliVersionLength = 64;

    public const string AbsentStatus = "absent";

    public const string DefaultTheme = "dark";

    public const int DefaultFontSize = 14;

    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal) { "checking", "ready", "notLoggedIn", "apiKey", "notInstalled", "error" };

    private static readonly HashSet<string> InstallKinds = new(StringComparer.Ordinal) { "npm", "native", "winget", "managed", "other" };

    /// <summary>嚴重度：&lt;50 ok、50～80 warn、80～95 high、≥95 或已鎖定 max（抄桌面 UsageRules.Severity）</summary>
    public static string Severity(double percent, bool locked)
    {
        if (locked || percent >= 95)
            return "max";
        if (percent >= 80)
            return "high";
        if (percent >= 50)
            return "warn";
        return "ok";
    }

    /// <summary>走勢點：最近 7 天、每小時一點（該小時最後一個值，保留原本的時間）、最多最後 168 點（抄桌面 UsageHistoryStore.Downsample）</summary>
    public static IReadOnlyList<UsagePoint> Downsample(IEnumerable<UsagePoint> points, long nowMs)
    {
        var from = nowMs - HistorySpanMs;
        var buckets = new SortedDictionary<long, UsagePoint>();
        foreach (var p in points)
        {
            if (p.At < from)
                continue;
            buckets[Bucket(p.At)] = p;
        }
        var result = buckets.Values.ToList();
        return result.Count <= MaxHistoryPoints ? result : result.Skip(result.Count - MaxHistoryPoints).ToList();
    }

    /// <summary>
    /// 增量版的降採樣：每小時（floor(at / 1 小時)）只留該小時最晚的一點（同一個 at 後到的取代先到的），
    /// 再丟掉代表點早於 now − 7 天的桶。時間遞增地分批餵進來，結果與一次 <see cref="Downsample"/> 全部的點相同
    /// </summary>
    public static void MergeHourly(SortedDictionary<long, UsagePoint> buckets, IEnumerable<UsagePoint> points, long nowMs)
    {
        foreach (var p in points)
        {
            var bucket = Bucket(p.At);
            if (!buckets.TryGetValue(bucket, out var existing) || p.At >= existing.At)
                buckets[bucket] = p;
        }
        var from = nowMs - HistorySpanMs;
        List<long>? expired = null;
        foreach (var (bucket, point) in buckets)
        {
            if (point.At >= from)
                break;
            (expired ??= []).Add(bucket);
        }
        if (expired is not null)
        {
            foreach (var bucket in expired)
                buckets.Remove(bucket);
        }
    }

    /// <summary>桶裡的走勢點：代表點不早於 now − 7 天的，最多最後 168 點（時間遞增）</summary>
    public static IReadOnlyList<UsagePoint> FromBuckets(SortedDictionary<long, UsagePoint> buckets, long nowMs)
    {
        var from = nowMs - HistorySpanMs;
        var result = buckets.Values.Where(p => p.At >= from).ToList();
        return result.Count <= MaxHistoryPoints ? result : result.Skip(result.Count - MaxHistoryPoints).ToList();
    }

    /// <summary>
    /// 會同步到伺服器目錄的 CLI（CatalogRules.IsProvider）：四種都同步（Grok、Antigravity 自 2026-10-22，設計 2026-10-22-grok-antigravity-sync；
    /// 計畫待決事項 Q6 的答案）。桌面只在協商出 prov1 時送後兩種，舊桌面的目錄與對話只有 Claude、Codex。
    /// 新的 AgentProvider 在這裡丟例外，逼著決定它同不同步
    /// </summary>
    public static bool IsSyncedProvider(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => true,
        AgentProvider.Codex => true,
        AgentProvider.Antigravity => true,
        AgentProvider.Grok => true,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "未知的代理種類：決定它在瀏覽器模式同不同步"),
    };

    /// <summary>
    /// 能不能另外建隔離的帳戶（同桌面 <c>AccountRules.SupportsIsolatedAccounts</c>，MyriagentHub 不能參考桌面組件）：
    /// Antigravity 的登入在 Windows 認證管理員、一台一個，只有本機預設帳戶
    /// </summary>
    public static bool SupportsIsolatedAccounts(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude or AgentProvider.Codex or AgentProvider.Grok => true,
        AgentProvider.Antigravity => false,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "未知的代理種類：決定它能不能建隔離帳戶"),
    };

    /// <summary>瀏覽器模式的 ProviderView：AgentProviders.All 的順序、只列同步的；精簡模式只能在各台本機改，safeMode 一律 false</summary>
    public static JArray Providers() =>
        new(AgentProviders.All.Where(IsSyncedProvider).Select(p => new JObject
        {
            ["key"] = p.ToString(),
            ["name"] = AgentProviders.ProductName(p),
            ["isolatedAccounts"] = SupportsIsolatedAccounts(p),
            ["safeMode"] = false,
        }));

    /// <summary>web-protocol AccountView.status 的六個值以外一律 error（裝置送來的狀態不可信）</summary>
    public static string SafeStatus(string? status) => status is not null && Statuses.Contains(status) ? status : "error";

    /// <summary>
    /// 帳戶在各台的狀態彙總（設計 14.6）：任一台 ready 而且身分相符就是 ready（不論在不在線——離線的那台仍是登入的）；
    /// 否則依 checking＞error（含 ready 但身分不符、不認得的狀態）＞apiKey＞notLoggedIn＞notInstalled；沒有連結是 notLoggedIn。
    /// 不依各台的 checkedAt（電腦的時鐘）選。
    /// </summary>
    public static (string Status, string StatusText) Aggregate(IReadOnlyList<LinkViewRow> links, Func<string, bool> online)
    {
        if (links.Count == 0)
            return ("notLoggedIn", "沒有電腦使用這個帳戶");
        var ready = links.Where(l => SafeStatus(l.Status) == "ready" && l.IdentityOk).ToList();
        if (ready.Count > 0)
            return ("ready", $"{ready.Count} 台已登入（{ready.Count(l => online(l.DeviceId))} 台在線）");

        var classes = links.Select(Classify).ToList();
        if (classes.Contains("checking"))
            return ("checking", "檢查中");
        if (classes.Contains("error"))
            return ("error", $"{classes.Count(c => c == "error")} 台有問題");
        if (classes.Contains("apiKey"))
            return ("apiKey", "API Key 登入");
        if (classes.Contains("notLoggedIn"))
            return ("notLoggedIn", "沒有電腦登入");
        return ("notInstalled", "未安裝 CLI");

        static string Classify(LinkViewRow link) => SafeStatus(link.Status) switch
        {
            "ready" => "error",   // 到這裡的 ready 一定是身分不符
            var s => s,
        };
    }

    /// <summary>
    /// 瀏覽器形式的 AccountView：id＝logicalId、不是本機帳戶（isSystemDefault、remoteOnly、safeMode 為 false，configDirectory 為 null）；
    /// devices 是每台未撤銷電腦的一格（沒有連結的是 absent）。<paramref name="pollerDeviceId"/> 是 usage_pollers 的負責裝置（空字串＝沒有人）
    /// </summary>
    public static JObject Account(AccountViewRow account, IReadOnlyList<LinkViewRow> links, SharedUsage? usage,
        IReadOnlyDictionary<string, IReadOnlyList<UsagePoint>> history, string? pollerDeviceId, IReadOnlyList<DeviceViewRow> devices)
    {
        var active = ActiveDevices(devices);
        var activeIds = active.Select(d => d.Info.Id).ToHashSet(StringComparer.Ordinal);
        var onlineIds = active.Where(d => d.Online).Select(d => d.Info.Id).ToHashSet(StringComparer.Ordinal);
        // 已撤銷（撤銷時連結已解除，這裡再擋一次）或不認得的裝置的連結不算
        // 每台一格（同一台重複的列只取第一筆），依 deviceId 排序
        var mine = links
            .Where(l => l.LogicalAccountId == account.Id && activeIds.Contains(l.DeviceId))
            .OrderBy(l => l.DeviceId, StringComparer.Ordinal)
            .GroupBy(l => l.DeviceId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        var byDevice = mine.ToDictionary(l => l.DeviceId, StringComparer.Ordinal);
        var (status, statusText) = Aggregate(mine, onlineIds.Contains);
        var poller = string.IsNullOrEmpty(pollerDeviceId) ? null : pollerDeviceId;
        var identity = mine.Where(l => l.IdentityOk).ToList();

        var cells = new JArray();
        foreach (var device in active)
        {
            var name = DisplayName(device);
            var isPoller = poller is not null && device.Info.Id == poller;
            if (byDevice.TryGetValue(device.Info.Id, out var link))
            {
                cells.Add(new JObject
                {
                    ["deviceId"] = device.Info.Id,
                    ["deviceName"] = name,
                    ["self"] = false,
                    ["status"] = SafeStatus(link.Status),
                    ["statusText"] = S(link.StatusText),
                    ["email"] = S(link.Email),
                    ["plan"] = S(link.Plan),
                    ["accessExpiresAt"] = link.AccessExpiresAt,
                    ["loginExpiresAt"] = link.LoginExpiresAt,
                    ["identityOk"] = link.IdentityOk,
                    ["usagePoller"] = isPoller,
                    ["online"] = device.Online,
                    ["checkedAt"] = link.CheckedAt,
                });
            }
            else
            {
                cells.Add(new JObject
                {
                    ["deviceId"] = device.Info.Id,
                    ["deviceName"] = name,
                    ["self"] = false,
                    ["status"] = AbsentStatus,
                    ["statusText"] = "尚未加入",
                    ["email"] = null,
                    ["plan"] = null,
                    ["accessExpiresAt"] = null,
                    ["loginExpiresAt"] = null,
                    ["identityOk"] = true,
                    ["usagePoller"] = isPoller,
                    ["online"] = device.Online,
                    ["checkedAt"] = null,
                });
            }
        }

        return new JObject
        {
            ["id"] = account.Id,
            ["provider"] = account.Provider,
            ["label"] = Label(account),
            ["color"] = account.Color,
            ["isSystemDefault"] = false,
            ["configDirectory"] = null,
            ["email"] = S(account.ExpectedEmail ?? identity.Select(l => l.Email).FirstOrDefault(e => e is not null)),
            ["plan"] = S(account.PlanHint ?? identity.Select(l => l.Plan).FirstOrDefault(p => p is not null)),
            ["status"] = status,
            ["statusText"] = statusText,
            ["safeMode"] = false,
            ["loginBusy"] = mine.Any(l => l.LoginBusy),
            ["usage"] = Usage(usage, history),
            // 只算在線的電腦（2026-10-23）：離線的那台最後回報的數字停在當時（睡眠、當機時可能還是執行中），同對話清單的離線投影不算執行中；
            // 桌面的帳戶卡加總其他電腦時是同一個規則（SyncViewRules.OtherSessions）
            ["sessions"] = new JObject
            {
                ["open"] = mine.Where(l => onlineIds.Contains(l.DeviceId)).Sum(l => (long)Math.Max(0, l.Sessions?.Open ?? 0)),
                ["running"] = mine.Where(l => onlineIds.Contains(l.DeviceId)).Sum(l => (long)Math.Max(0, l.Sessions?.Running ?? 0)),
                ["waiting"] = mine.Where(l => onlineIds.Contains(l.DeviceId)).Sum(l => (long)Math.Max(0, l.Sessions?.Waiting ?? 0)),
                ["background"] = mine.Where(l => onlineIds.Contains(l.DeviceId)).Sum(l => (long)Math.Max(0, l.Sessions?.Background ?? 0)),
                // 這一輪結束了、但代理開的程式脫離 CLI 還在跑的對話數（背景程式，2026-10-24）
                ["programs"] = mine.Where(l => onlineIds.Contains(l.DeviceId)).Sum(l => (long)Math.Max(0, l.Sessions?.Programs ?? 0)),
                // 執行中的子代理數（個數，2026-10-28）
                ["subagents"] = mine.Where(l => onlineIds.Contains(l.DeviceId)).Sum(l => (long)Math.Max(0, l.Sessions?.Subagents ?? 0)),
            },
            ["logicalId"] = account.Id,
            ["remoteOnly"] = false,
            ["devices"] = cells,
        };
    }

    /// <summary>
    /// Usage：source 只放行 api、session；每個時間窗帶嚴重度與走勢點（最多 168 點，<paramref name="history"/> 以伺服器存的、跳脫後的時間窗 key 為鍵）；
    /// 送出的 key 是還原後的鍵（<see cref="UsageWindowKeyRules.DecodeAll"/>，與桌面相同：<c>weekly_cOpus</c>→<c>weekly:Opus</c>，web-protocol 第三節 UsageWindow）。
    /// 解碼不是一對一：還原後的鍵等於這份用量裡另一個原本的鍵時（只有不是這個實作送的鍵才會）送原本的鍵，送出的 key 兩兩不同（網頁拿 key 當時間窗的識別）。
    /// 沒有 key 的窗略過；沒有 usage_latest 回 null
    /// </summary>
    public static JObject? Usage(SharedUsage? usage, IReadOnlyDictionary<string, IReadOnlyList<UsagePoint>> history)
    {
        if (usage is null)
            return null;
        var windows = new JArray();
        var list = (usage.Windows ?? []).Where(w => w?.Key is not null).ToList();
        var keys = UsageWindowKeyRules.DecodeAll(list.Select(w => w.Key).ToList());
        for (var i = 0; i < list.Count; i++)
        {
            var w = list[i];
            var points = history.TryGetValue(w.Key, out var stored) ? stored : Array.Empty<UsagePoint>();
            var start = Math.Max(0, points.Count - MaxHistoryPoints);
            windows.Add(new JObject
            {
                ["key"] = keys[i],
                ["label"] = w.Label,
                ["percent"] = Finite(w.Percent),
                ["resetsAt"] = w.ResetsAt,
                ["severity"] = Severity(Finite(w.Percent), w.Locked),
                ["history"] = new JArray(points.Skip(start).Select(p => new JArray(p.At, Finite(p.Percent)))),
            });
        }
        return new JObject
        {
            ["fetchedAt"] = usage.FetchedAt > 0 ? usage.FetchedAt : null,
            ["stale"] = usage.Stale,
            ["source"] = S(usage.Source is "api" or "session" ? usage.Source : null),
            ["error"] = S(usage.Error),
            ["windows"] = windows,
            ["credits"] = S(usage.Credits),
            ["note"] = S(usage.Note),
        };
    }

    /// <summary>DeviceView 清單：未撤銷的依 CreatedAt、Id（Ordinal），已撤銷的排最後（同順序）</summary>
    public static JArray Devices(IEnumerable<DeviceViewRow> devices) =>
        new(Ordered(devices).Select(Device));

    /// <summary>
    /// DeviceView：name 是顯示名稱（device_streams.name，沒有時用配對時的名稱）、signedName 是配對時的名稱；
    /// keyStorage、remoteControlLevel、撤銷原因走白名單；windowsUser 不送；verified 一律 null（瀏覽器不驗）
    /// </summary>
    public static JObject Device(DeviceViewRow device)
    {
        var info = device.Info;
        var level = device.Policy?.RemoteControlLevel;
        long? lastSeen = (device.LastHelloAt, device.LastHeartbeatAt) switch
        {
            ({ } hello, { } beat) => Math.Max(hello, beat),
            ({ } hello, null) => hello,
            (null, var beat) => beat,
        };
        return new JObject
        {
            ["id"] = info.Id,
            ["name"] = DisplayName(device),
            ["signedName"] = info.Name,
            ["self"] = false,
            ["machineName"] = S(info.MachineName),
            ["appVersion"] = S(device.StreamAppVersion ?? info.AppVersion),
            ["fingerprint"] = info.Fingerprint,
            ["keyStorage"] = info.KeyStorage is "tpm" or "software" ? info.KeyStorage : "software",
            ["online"] = device.Online && !info.Revoked,
            ["lastSeenAt"] = lastSeen,
            ["remoteControlLevel"] = level is "Off" or "View" or "Control" ? level : "Off",
            ["allowRemoteFullAccess"] = device.Policy?.AllowRemoteFullAccess ?? false,
            ["verified"] = null,
            ["revoked"] = info.RevokedAt is { } at
                ? new JObject
                {
                    ["at"] = at,
                    ["reason"] = S(info.RevokedReason is "retired" or "compromised" ? info.RevokedReason : null),
                }
                : null,
        };
    }

    /// <summary>
    /// devicesCli：<c>{deviceId: {provider: DeviceCliView}}</c>，只含未撤銷的電腦（<see cref="Devices"/> 的順序）與同步的 CLI（AgentProviders.All 的順序）；
    /// 沒有就是 <c>{}</c>
    /// </summary>
    public static JObject DevicesCli(IEnumerable<DeviceViewRow> devices, IEnumerable<(string DeviceId, AgentProvider Provider, DeviceCliView View)> cli)
    {
        var byDevice = cli
            .Where(c => IsSyncedProvider(c.Provider) && c.View is not null)
            .GroupBy(c => c.DeviceId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var result = new JObject();
        foreach (var device in ActiveDevices(devices))
        {
            if (!byDevice.TryGetValue(device.Info.Id, out var entries))
                continue;
            var providers = new JObject();
            foreach (var provider in AgentProviders.All)
            {
                if (entries.FirstOrDefault(e => e.Provider == provider) is { View: not null } entry)
                    providers[provider.ToString()] = DeviceCli(entry.View);
            }
            if (providers.Count > 0)
                result[device.Info.Id] = providers;
        }
        return result;
    }

    /// <summary>DeviceCliView 逐欄白名單（不把 cli.state 的 JSON 原樣轉出，設計 4.9.2）：字串重新檢查長度與控制字元、installKind 只放行五種、計數夾在 0～100,000</summary>
    public static JObject DeviceCli(DeviceCliView view) => new()
    {
        ["installed"] = view.Installed,
        ["version"] = S(Text(view.Version, MaxCliVersionLength)),
        ["installKind"] = S(view.InstallKind is { } kind && InstallKinds.Contains(kind) ? kind : null),
        ["latest"] = S(Text(view.Latest, MaxCliVersionLength)),
        ["updateAvailable"] = view.UpdateAvailable,
        ["runningOnOldVersion"] = Math.Clamp(view.RunningOnOldVersion, 0, MaxCliCount),
        ["restartPending"] = Math.Clamp(view.RestartPending, 0, MaxCliCount),
        ["installBroken"] = S(Text(view.InstallBroken, CatalogRules.MaxTextLength)),
        ["checkedAt"] = view.CheckedAt,
    };

    /// <summary>瀏覽器模式的 Settings：只有四個漫遊鍵；沒有或不合格（PrefsRules）的給預設（同桌面 AppSettings 的預設值）</summary>
    public static JObject Settings(IReadOnlyDictionary<string, JToken> prefs) => new()
    {
        ["theme"] = Pref(prefs, "theme", DefaultTheme),
        ["fontSize"] = Pref(prefs, "fontSize", DefaultFontSize),
        ["enterToSend"] = Pref(prefs, "enterToSend", true),
        ["notifications"] = Pref(prefs, "notifications", true),
    };

    /// <summary>floor(at / 1 小時)（整數運算；桌面以 double 相除後截斷，正的時間結果相同）</summary>
    private static long Bucket(long at) => at >= 0 ? at / HourMs : -((-at + HourMs - 1) / HourMs);

    /// <summary>可為 null 的字串：null 寫成 JSON null 的節點（JValue(string) 收到 null 時型別是 String，比對時與 null 不相等）</summary>
    private static JToken S(string? value) => value is null ? JValue.CreateNull() : new JValue(value);

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    private static string? Text(string? text, int maxLength) =>
        text is not null && CatalogRules.IsOptionalText(text, maxLength) ? text : null;

    private static JToken Pref(IReadOnlyDictionary<string, JToken> prefs, string key, JToken fallback) =>
        prefs.TryGetValue(key, out var value) && PrefsRules.IsValid(key, value) ? value.DeepClone() : fallback;

    private static string DisplayName(DeviceViewRow device) =>
        string.IsNullOrWhiteSpace(device.StreamName) ? device.Info.Name : device.StreamName;

    private static string Label(AccountViewRow account)
    {
        if (!string.IsNullOrWhiteSpace(account.Label))
            return account.Label;
        if (!string.IsNullOrWhiteSpace(account.ExpectedEmail))
            return account.ExpectedEmail;
        return Enum.TryParse<AgentProvider>(account.Provider, ignoreCase: false, out var provider) && Enum.IsDefined(provider)
            ? AgentProviders.ProductName(provider)
            : account.Provider;
    }

    private static IEnumerable<DeviceViewRow> Ordered(IEnumerable<DeviceViewRow> devices) =>
        devices
            .OrderBy(d => d.Info.Revoked)
            .ThenBy(d => d.Info.CreatedAt)
            .ThenBy(d => d.Info.Id, StringComparer.Ordinal);

    private static List<DeviceViewRow> ActiveDevices(IEnumerable<DeviceViewRow> devices) =>
        Ordered(devices).Where(d => !d.Info.Revoked).ToList();
}
