using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Myriagent.Usage;

/// <summary>
/// 用量的純函式規則：解析 Claude 官方用量端點、Claude 對話的 rate_limit_event、Codex 的 rateLimits、Antigravity 的 -p /usage、Grok 的 _x.ai/billing；
/// 合併、推算重置、嚴重度。格式實測見 docs/protocol-notes.md。
/// 時間窗的 key：Claude 為 session／weekly／weekly:模型名，Codex 為 {limitId}:{分鐘數}，Antigravity 為 agy-{bucketId}，Grok 為 grok-credits。
/// </summary>
internal static class UsageRules
{
    public const string SessionKey = "session";
    public const string WeeklyKey = "weekly";
    public const string AntigravityKeyPrefix = "agy-";

    /// <summary>嚴重度：&lt;50 ok、50～80 warn、80～95 high、≥95 或已鎖定 max</summary>
    public static string Severity(double percent, bool locked = false)
    {
        if (locked || percent >= 95)
            return "max";
        if (percent >= 80)
            return "high";
        if (percent >= 50)
            return "warn";
        return "ok";
    }

    /// <summary>
    /// Claude GET /api/oauth/usage → 時間窗。優先用通用的 limits 清單（session／weekly_all／weekly_scoped＋模型名）；
    /// 沒有 limits 時退回 five_hour／seven_day／seven_day_opus／seven_day_sonnet 這幾個具名欄位（utilization 為 0～100）。
    /// </summary>
    public static (List<UsageWindow> Windows, string? Note) ParseClaudeUsage(JObject? json)
    {
        var windows = new List<UsageWindow>();
        if (json == null)
            return (windows, null);

        if (json["limits"] is JArray limits && limits.Count > 0)
        {
            foreach (var limit in limits.OfType<JObject>())
            {
                var kind = limit.Value<string>("kind") ?? string.Empty;
                var percent = limit.Value<double?>("percent");
                if (!percent.HasValue)
                    continue;
                var model = (limit["scope"] as JObject)?["model"] is JObject m ? m.Value<string>("display_name") : null;
                var (key, label) = kind switch
                {
                    "session" => (SessionKey, "5 小時"),
                    "weekly_all" => (WeeklyKey, "每週"),
                    "weekly_scoped" when !string.IsNullOrWhiteSpace(model) => ($"{WeeklyKey}:{model}", $"每週・{model}"),
                    _ => (kind, kind),
                };
                var locked = string.Equals(limit.Value<string>("severity"), "blocked", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(limit.Value<string>("severity"), "exceeded", StringComparison.OrdinalIgnoreCase);
                if (windows.All(w => w.Key != key))
                    windows.Add(new UsageWindow(key, label, Clamp(percent.Value), ParseTime(limit["resets_at"]), locked));
            }
        }

        if (windows.Count == 0)
        {
            AddNamed(windows, json["five_hour"] as JObject, SessionKey, "5 小時");
            AddNamed(windows, json["seven_day"] as JObject, WeeklyKey, "每週");
            AddNamed(windows, json["seven_day_opus"] as JObject, $"{WeeklyKey}:Opus", "每週・Opus");
            AddNamed(windows, json["seven_day_sonnet"] as JObject, $"{WeeklyKey}:Sonnet", "每週・Sonnet");
        }

        string? note = null;
        if (json["extra_usage"] is JObject extra && (extra.Value<bool?>("is_enabled") ?? false))
        {
            var used = extra.Value<double?>("used_credits");
            var limit = extra.Value<double?>("monthly_limit");
            note = used.HasValue && limit.HasValue ? $"已啟用額外用量（本月 {used:0.##}／{limit:0.##}）" : "已啟用額外用量";
        }

        return (Order(windows), note);
    }

    private static void AddNamed(List<UsageWindow> windows, JObject? window, string key, string label)
    {
        var utilization = window?.Value<double?>("utilization");
        if (utilization.HasValue)
            windows.Add(new UsageWindow(key, label, Clamp(utilization.Value), ParseTime(window!["resets_at"])));
    }

    /// <summary>
    /// Claude 的 rate_limit_info 是訂閱額度被拒：status 是 rejected，而且帶 rateLimitType 或 resetsAt（是哪個時間窗、何時重置）。
    /// 訂閱帳戶碰到沒有額度標頭的 429 時，CLI 一樣先送 status rejected 的 rate_limit_event，但兩個都沒帶，接著的錯誤是
    /// 「Server is temporarily limiting requests (not your usage limit)」——那是暫時性的，不是額度用完
    /// （2.1.310 實測，tools/probes/claude_api_error_probe.py rate-limit；docs/protocol-notes.md「額度用完時的訊號」）
    /// </summary>
    public static bool IsClaudeQuotaRejection(JObject? info)
        => info != null
           && string.Equals(info.Value<string>("status"), "rejected", StringComparison.OrdinalIgnoreCase)
           && (info["rateLimitType"] is JValue { Type: JTokenType.String } type && !string.IsNullOrWhiteSpace((string?)type)
               || info["resetsAt"] is JValue { Type: JTokenType.Integer or JTokenType.Float });

    /// <summary>
    /// Claude 對話的 rate_limit_event.rate_limit_info → 時間窗更新（unifiedWindows 的 utilization 是 0～1、resetsAt 是秒）。
    /// 額度被拒（IsClaudeQuotaRejection）時把 rateLimitType 對應的時間窗標為鎖定：five_hour（或沒帶）→ 5 小時、seven_day → 每週、
    /// seven_day_opus／seven_day_sonnet → 每週・Opus／Sonnet（不是整個每週）；其他類型不猜是哪個時間窗。
    /// 被鎖的時間窗沒附使用率時（模型的每週上限、事件沒帶 unifiedWindows）補一個 100% 已鎖定的時間窗。
    /// 被拒但沒帶類型與重置時間的（暫時性的 429）不鎖
    /// </summary>
    public static List<UsageWindow> ParseClaudeRateLimitEvent(JObject? info)
    {
        var result = new List<UsageWindow>();
        if (info == null)
            return result;
        var windows = info["unifiedWindows"] as JObject;
        string? lockedKey = null, lockedLabel = null;
        if (IsClaudeQuotaRejection(info))
        {
            (lockedKey, lockedLabel) = info.Value<string>("rateLimitType") switch
            {
                null or "five_hour" => (SessionKey, "5 小時"),
                "seven_day" => (WeeklyKey, "每週"),
                "seven_day_opus" => ($"{WeeklyKey}:Opus", "每週・Opus"),
                "seven_day_sonnet" => ($"{WeeklyKey}:Sonnet", "每週・Sonnet"),
                _ => ((string?)null, (string?)null),   // 其他類型（例如 overage）不猜是哪個時間窗
            };
        }
        AddEventWindow(result, windows?["five_hour"] as JObject, SessionKey, "5 小時", lockedKey == SessionKey);
        AddEventWindow(result, windows?["seven_day"] as JObject, WeeklyKey, "每週", lockedKey == WeeklyKey);
        if (lockedKey != null && result.All(w => w.Key != lockedKey))
        {
            var resets = info.Value<long?>("resetsAt");
            result.Add(new UsageWindow(lockedKey, lockedLabel!, 100, resets.HasValue ? DateTimeOffset.FromUnixTimeSeconds(resets.Value) : null, Locked: true));
        }
        return result;
    }

    private static void AddEventWindow(List<UsageWindow> result, JObject? window, string key, string label, bool locked)
    {
        var utilization = window?.Value<double?>("utilization");
        if (!utilization.HasValue)
            return;
        var resets = window!.Value<long?>("resetsAt");
        result.Add(new UsageWindow(key, label, Clamp(utilization.Value * 100), resets.HasValue ? DateTimeOffset.FromUnixTimeSeconds(resets.Value) : null, locked));
    }

    /// <summary>
    /// Codex account/rateLimits/read 的結果（或 rateLimits/updated 的 rateLimits）→ 時間窗與點數餘額。
    /// 有 rateLimitsByLimitId 就逐桶列出（多桶時名稱加上 limitName）；primary／secondary 依時間窗長度命名。
    /// 已達上限：用滿的時間窗；額度桶回報 rateLimitReachedType 卻沒有時間窗用滿（工作區點數用完、管理員設的上限）時整個桶都算；
    /// ordinaryUsageAllowed=false（後端不准用方案內含的額度，不能從百分比或重置時間推斷已恢復）時全部都算。
    /// </summary>
    public static (List<UsageWindow> Windows, string? Credits) ParseCodexRateLimits(JObject? readResult)
    {
        var windows = new List<UsageWindow>();
        if (readResult == null)
            return (windows, null);

        bool blocked = readResult.Value<bool?>("ordinaryUsageAllowed") == false;
        string? credits = null;
        foreach (var bucket in CodexBuckets(readResult))
        {
            var limitId = bucket.Value<string>("limitId") ?? "codex";
            var limitName = bucket.Value<string>("limitName");
            bool reached = !string.IsNullOrEmpty(bucket.Value<string>("rateLimitReachedType"));
            var bucketWindows = new List<UsageWindow>();
            foreach (var slot in new[] { "primary", "secondary" })
            {
                if (bucket[slot] is not JObject window)
                    continue;
                var used = window.Value<double?>("usedPercent");
                if (!used.HasValue)
                    continue;
                var minutes = window.Value<long?>("windowDurationMins");
                // 有額度桶名稱就一律加上（不論這份資料有幾個桶）：對話中的 rateLimits/updated 常只帶一個桶，
                // 名稱若隨桶數改變，合併後畫面會出現兩個同名的「每週」
                var label = DescribeWindow(minutes);
                if (!string.IsNullOrWhiteSpace(limitName))
                    label = $"{label}・{limitName}";
                var resets = window.Value<long?>("resetsAt");
                bucketWindows.Add(new UsageWindow($"{limitId}:{minutes?.ToString(CultureInfo.InvariantCulture) ?? slot}", label, Clamp(used.Value),
                    resets.HasValue ? DateTimeOffset.FromUnixTimeSeconds(resets.Value) : null, blocked || (reached && used.Value >= 100)));
            }
            if (reached && !bucketWindows.Any(w => w.Locked))
                bucketWindows = bucketWindows.Select(w => w with { Locked = true }).ToList();
            windows.AddRange(bucketWindows);

            if (credits == null && bucket["credits"] is JObject c && !(c.Value<bool?>("unlimited") ?? false) && (c.Value<bool?>("hasCredits") ?? false))
            {
                var balance = c.Value<string>("balance");
                if (double.TryParse(balance, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                    credits = b.ToString("#,0.##", CultureInfo.InvariantCulture);
            }
        }

        return (Order(windows), credits);
    }

    /// <summary>額度桶：rateLimitsByLimitId 逐桶；沒有就用 rateLimits；rateLimits/updated 直接給一個桶的快照</summary>
    private static List<JObject> CodexBuckets(JObject readResult)
    {
        var buckets = new List<JObject>();
        if (readResult["rateLimitsByLimitId"] is JObject byId && byId.Count > 0)
            buckets.AddRange(byId.Properties().Select(p => p.Value).OfType<JObject>());
        else if (readResult["rateLimits"] is JObject single)
            buckets.Add(single);
        else if (readResult["primary"] != null || readResult["secondary"] != null)
            buckets.Add(readResult);   // rateLimits/updated 直接給快照
        return buckets;
    }

    /// <summary>看得懂的 Codex 用量回應（至少有一個額度桶；桶裡沒有時間窗是合法的，例如沒有用量上限的方案）</summary>
    public static bool HasCodexRateLimits(JObject? readResult) => readResult != null && CodexBuckets(readResult).Count > 0;

    /// <summary>
    /// Codex 額度被擋的原因（畫面上的說明）：額度桶的 rateLimitReachedType（工作區點數用完、管理員設的上限…），
    /// 或 ordinaryUsageAllowed=false；沒被擋回 null
    /// </summary>
    public static string? DescribeCodexBlock(JObject? readResult)
    {
        if (readResult == null)
            return null;
        var reasons = new List<string>();
        foreach (var bucket in CodexBuckets(readResult))
        {
            var type = bucket.Value<string>("rateLimitReachedType");
            if (string.IsNullOrEmpty(type))
                continue;
            var reason = type switch
            {
                "workspace_owner_credits_depleted" => "工作區的點數已用完",
                "workspace_member_credits_depleted" => "你在工作區的點數額度已用完",
                "workspace_owner_usage_limit_reached" => "工作區已達用量上限",
                "workspace_member_usage_limit_reached" => "已達工作區管理員設定的用量上限",
                _ => "已達用量上限",
            };
            var limitName = bucket.Value<string>("limitName");
            reasons.Add(string.IsNullOrWhiteSpace(limitName) ? reason : $"{limitName}：{reason}");
        }
        if (reasons.Count == 0 && readResult.Value<bool?>("ordinaryUsageAllowed") == false)
            reasons.Add("Codex 回報目前不能使用方案內含的額度");
        return reasons.Count == 0 ? null : string.Join("；", reasons.Distinct());
    }

    /// <summary>Grok 額度時間窗的 key（只有一個額度窗：方案內含的點數，每週或每月重置）</summary>
    public const string GrokCreditsKey = "grok-credits";

    /// <summary>
    /// Antigravity 的 agy -p /usage --output-format json（command.data）→ 時間窗與附註。
    /// 實測 1.4.6 有兩個群組（Gemini Models、Claude and GPT models），各含 weekly 與 5h 兩個 bucket：
    /// remaining_fraction 為 0～1 的剩餘比例（Go struct 標 omitempty，剩餘 0 時可能省略，視為 0＝已用 100% 且鎖定）；
    /// 純計次的 remaining_amount 剩餘 &gt;0 記進附註、≤0 視為 100% 已鎖定；disabled=true 的桶略過。看不懂（沒有 groups）回 null
    /// </summary>
    public static (List<UsageWindow> Windows, string? Note)? ParseAntigravityUsage(JObject? data)
    {
        if (data?["groups"] is not JArray groups)
            return null;

        var windows = new List<UsageWindow>();
        var notes = new List<string>();
        bool sawEnabledBucket = false;
        int groupIndex = 0;
        foreach (var group in groups.OfType<JObject>())
        {
            groupIndex++;
            var groupLabel = SimplifyAntigravityGroup(group.Value<string>("name"));
            if (group["buckets"] is not JArray buckets)
                continue;
            int bucketIndex = 0;
            foreach (var bucket in buckets.OfType<JObject>())
            {
                bucketIndex++;
                if (bucket.Value<bool?>("disabled") == true)
                    continue;
                sawEnabledBucket = true;

                var rawWindow = bucket.Value<string>("window");
                var key = NormalizeAntigravityKey(bucket.Value<string>("id"), groupIndex, bucketIndex, rawWindow);
                if (windows.Any(w => w.Key == key))
                    continue;
                var windowLabel = DescribeAntigravityWindow(rawWindow, bucket.Value<string>("name"));
                var label = string.IsNullOrEmpty(groupLabel) ? windowLabel : $"{windowLabel}・{groupLabel}";
                var resets = ParseTime(bucket["reset_time"]);

                if (bucket["remaining_fraction"] != null && bucket.Value<double?>("remaining_fraction") is { } fraction)
                {
                    if (!double.IsFinite(fraction))
                        continue;
                    var used = (1.0 - Math.Clamp(fraction, 0.0, 1.0)) * 100.0;
                    var clamped = Clamp(used);
                    windows.Add(new UsageWindow(key, label, clamped, resets, Locked: fraction <= 0 || clamped >= 100));
                }
                else if (bucket["remaining_amount"] != null && bucket.Value<long?>("remaining_amount") is { } amount)
                {
                    if (amount <= 0)
                        windows.Add(new UsageWindow(key, label, 100, resets, Locked: true));
                    else
                        notes.Add($"{label} 剩餘 {amount.ToString("#,0", CultureInfo.InvariantCulture)} 次");
                }
                else if (!string.IsNullOrWhiteSpace(bucket.Value<string>("id")) && (!string.IsNullOrWhiteSpace(rawWindow) || resets.HasValue))
                {
                    // remaining_fraction 是 Go 的 float64/float32 ,omitempty：用滿剩餘 0 時欄位會省略
                    windows.Add(new UsageWindow(key, label, 100, resets, Locked: true));
                }
            }
        }

        if (sawEnabledBucket && windows.Count == 0 && notes.Count == 0)
            return null;
        return (Order(windows), notes.Count == 0 ? null : string.Join("；", notes));
    }

    private static string NormalizeAntigravityKey(string? rawId, int groupIndex, int bucketIndex, string? rawWindow)
    {
        var fallback = $"g{groupIndex}-{(!string.IsNullOrWhiteSpace(rawWindow) ? rawWindow : $"b{bucketIndex}")}";
        var source = string.IsNullOrWhiteSpace(rawId) ? fallback : rawId.Trim();
        if (source.StartsWith(AntigravityKeyPrefix, StringComparison.OrdinalIgnoreCase))
            source = source[AntigravityKeyPrefix.Length..];
        var cleaned = new System.Text.StringBuilder(source.Length);
        foreach (var ch in source)
        {
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                cleaned.Append(char.ToLowerInvariant(ch));
            else if (cleaned.Length > 0 && cleaned[^1] != '-')
                cleaned.Append('-');
        }
        while (cleaned.Length > 0 && cleaned[^1] == '-')
            cleaned.Length--;
        var body = cleaned.Length > 0 ? cleaned.ToString() : $"g{groupIndex}-b{bucketIndex}";
        var maxBody = 32 - AntigravityKeyPrefix.Length;
        if (body.Length > maxBody)
            body = body[..maxBody].TrimEnd('-');
        return AntigravityKeyPrefix + body;
    }

    private static string? SimplifyAntigravityGroup(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var trimmed = name.Trim();
        if (trimmed.Equals("Gemini Models", StringComparison.OrdinalIgnoreCase))
            return "Gemini";
        if (trimmed.Equals("Claude and GPT models", StringComparison.OrdinalIgnoreCase))
            return "Claude/GPT";
        if (trimmed.EndsWith(" models", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^" models".Length].Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    private static string DescribeAntigravityWindow(string? window, string? bucketName)
    {
        var w = window?.Trim().ToLowerInvariant();
        switch (w)
        {
            case "5h":
                return "5 小時";
            case "weekly" or "7d" or "1w":
                return "每週";
            case "daily" or "24h" or "1d":
                return "每日";
            case "monthly" or "30d" or "1m":
                return "每月";
            case "hourly":
                return "1 小時";
        }
        if (w is { Length: > 1 } && w[^1] == 'h' && long.TryParse(w[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) && hours > 0)
            return hours == 24 ? "每日" : $"{hours} 小時";

        if (!string.IsNullOrWhiteSpace(bucketName))
        {
            if (bucketName.Contains("Five Hour", StringComparison.OrdinalIgnoreCase) || bucketName.Contains("5-Hour", StringComparison.OrdinalIgnoreCase))
                return "5 小時";
            if (bucketName.Contains("Weekly", StringComparison.OrdinalIgnoreCase))
                return "每週";
            if (bucketName.Contains("Daily", StringComparison.OrdinalIgnoreCase))
                return "每日";
            if (bucketName.Contains("Monthly", StringComparison.OrdinalIgnoreCase))
                return "每月";
        }
        return "額度";
    }

    /// <summary>
    /// Grok 的 _x.ai/billing 結果 → 一個時間窗與附註。使用率是 config.creditUsagePercent（0～100；proto3 省略 0，缺少時是 0%，同官方 TUI）；
    /// 沒有新欄位的舊回應以 used／monthlyLimit 推算。重置時間取 currentPeriod.end（缺時 billingPeriodEnd），名稱依 currentPeriod.type（每週／每月）。
    /// 用滿 100%、沒有隨用隨付額度（onDemandCap）或已用到上限、也沒有預購點數時鎖定；有隨用隨付或預購點數時寫進附註。看不懂（沒有 config）回 null
    /// </summary>
    public static (List<UsageWindow> Windows, string? Note)? ParseGrokBilling(JObject? billing)
    {
        if (billing?["config"] is not JObject config)
            return null;
        double percent;
        if (config.Value<double?>("creditUsagePercent") is { } pct)
            percent = pct;
        else
        {
            var limit = CentValue(config["monthlyLimit"]);
            var used = CentValue(config["used"]);
            percent = limit > 0 ? used * 100.0 / limit : 0;
        }
        var period = config["currentPeriod"] as JObject;
        var resets = ParseTime(period?["end"]) ?? ParseTime(config["billingPeriodEnd"]);
        var label = period?.Value<string>("type") switch
        {
            "USAGE_PERIOD_TYPE_WEEKLY" => "每週",
            "USAGE_PERIOD_TYPE_MONTHLY" => "每月",
            "USAGE_PERIOD_TYPE_DAILY" => "每日",
            _ => "額度",
        };
        var onDemandCap = CentValue(config["onDemandCap"]);
        var onDemandUsed = CentValue(config["onDemandUsed"]);
        var prepaid = CentValue(config["prepaidBalance"]);
        var window = new UsageWindow(GrokCreditsKey, label, Clamp(percent), resets, Locked: percent >= 100 && (onDemandCap <= 0 || onDemandUsed >= onDemandCap) && prepaid <= 0);

        var notes = new List<string>();
        if (onDemandCap > 0)
            notes.Add($"隨用隨付已用 {FormatCents(onDemandUsed)}／上限 {FormatCents(onDemandCap)}");
        if (prepaid > 0)
            notes.Add($"預購點數餘額 {FormatCents(prepaid)}");
        return (new List<UsageWindow> { window }, notes.Count == 0 ? null : string.Join("；", notes));
    }

    /// <summary>billing 的金額欄位是 {val: 美分}（proto 的 Cent）；沒有或不是數字回 0</summary>
    private static long CentValue(JToken? token)
        => token is JObject o && o.Value<long?>("val") is { } v ? v : token is JValue { Type: JTokenType.Integer } n ? n.Value<long>() : 0;

    private static string FormatCents(long cents) => (cents / 100.0).ToString("$#,0.00", CultureInfo.InvariantCulture);

    /// <summary>時間窗長度 → 中文（300 分＝5 小時、10080 分＝每週）</summary>
    public static string DescribeWindow(long? minutes) => minutes switch
    {
        null => "額度",
        10080 => "每週",
        1440 => "每日",
        >= 60 when minutes % 60 == 0 => $"{minutes / 60} 小時",
        _ => $"{minutes} 分鐘",
    };

    /// <summary>稀疏更新合併：同 key 取代數值、新 key 加入、其他保留</summary>
    public static List<UsageWindow> Merge(IReadOnlyList<UsageWindow>? existing, IReadOnlyList<UsageWindow> updates)
    {
        var result = (existing ?? Array.Empty<UsageWindow>()).ToList();
        foreach (var update in updates)
        {
            int index = result.FindIndex(w => w.Key == update.Key);
            if (index >= 0)
            {
                var old = result[index];
                // 同一個時間窗的名稱維持不變（稀疏更新可能少帶額度桶名稱）；更新沒帶重置時間時沿用舊值（rate_limit_event 有時只給使用率）
                result[index] = update with { Label = old.Label, ResetsAt = update.ResetsAt ?? old.ResetsAt };
            }
            else
            {
                result.Add(update);
            }
        }
        return Order(result);
    }

    /// <summary>
    /// 兩份快照在畫面上看起來一樣（時間窗、點數、說明、錯誤、過時、來源都相同；取得時間不算——只在過時時顯示）：
    /// 對話中的每個 API 請求都回報一次額度，數字通常沒變，一樣就不必通知（帳戶卡含走勢線整份重算、網頁重整側欄）
    /// </summary>
    public static bool SameForView(UsageSnapshot? a, UsageSnapshot b) =>
        a is not null && a.Source == b.Source && a.Stale == b.Stale && a.Credits == b.Credits && a.Note == b.Note && a.Error == b.Error
        && a.Windows.SequenceEqual(b.Windows);

    /// <summary>快取資料推算重置：重置時間已過的時間窗使用率歸零、解除鎖定（下一次的重置時間未知）</summary>
    public static List<UsageWindow> ApplyResets(IReadOnlyList<UsageWindow> windows, DateTimeOffset now)
        => windows.Select(w => w.ResetsAt.HasValue && w.ResetsAt.Value <= now ? w with { Percent = 0, ResetsAt = null, Locked = false } : w).ToList();

    /// <summary>
    /// 目前顯示的快照有時間窗已到重置時間（任何來源，含別台查到的共享用量）：照 <see cref="ApplyResets"/> 推算、標成過時的快取；沒有到期的回 null（不必換）。
    /// 輪詢每一拍都套（AccountManager.Tick）：這台沒被指派查用量（多電腦同步）、或查詢一直失敗時，畫面不會停在重置前的數字
    /// （2026-10-28 夜裡 example-user 沒有一台在查，每週額度重置後仍顯示 100%）。過時的不上傳（UsageSyncRules.Decide）
    /// </summary>
    public static UsageSnapshot? ApplyDueResets(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null || !snapshot.Windows.Any(w => w.ResetsAt is { } at && at <= now))
            return null;
        return snapshot with { Windows = ApplyResets(snapshot.Windows, now), Stale = true, Source = "cache" };
    }

    /// <summary>排序：5 小時／每週在前（Antigravity 為 Gemini 在前、同群組 5 小時在每週前），其他依 key</summary>
    public static List<UsageWindow> Order(IEnumerable<UsageWindow> windows)
        => windows
            .OrderBy(w => WindowRank(w.Key))
            .ThenBy(w => WindowMinutes(w.Key))
            .ThenBy(w => w.Key, StringComparer.Ordinal)
            .ToList();

    private static int WindowRank(string key)
    {
        if (key == SessionKey)
            return 0;
        if (key == WeeklyKey)
            return 1;
        if (key.StartsWith(WeeklyKey + ":", StringComparison.Ordinal))
            return 2;
        if (key.StartsWith(AntigravityKeyPrefix + "gemini-", StringComparison.Ordinal))
            return 3;
        if (key.StartsWith(AntigravityKeyPrefix, StringComparison.Ordinal))
            return 4;
        return 5;
    }

    private static long WindowMinutes(string key)
    {
        if (key.StartsWith(AntigravityKeyPrefix, StringComparison.Ordinal))
        {
            var dash = key.LastIndexOf('-');
            var suffix = dash >= 0 ? key[(dash + 1)..] : string.Empty;
            if (suffix == "weekly")
                return 10080;
            if (suffix == "daily")
                return 1440;
            if (suffix == "monthly")
                return 43200;
            if (suffix.Length > 1 && suffix[^1] == 'h' && long.TryParse(suffix[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var hours))
                return hours * 60;
            return long.MaxValue;
        }
        var colon = key.LastIndexOf(':');
        return colon >= 0 && long.TryParse(key[(colon + 1)..], out var minutes) ? minutes : 0;
    }

    /// <summary>重置倒數的中文（例如「3 小時 12 分後重置」）</summary>
    public static string DescribeReset(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (!resetsAt.HasValue)
            return string.Empty;
        var left = resetsAt.Value - now;
        if (left <= TimeSpan.Zero)
            return "已重置";
        if (left.TotalDays >= 1)
            return $"{(int)left.TotalDays} 天 {left.Hours} 小時後重置";
        if (left.TotalHours >= 1)
            return $"{(int)left.TotalHours} 小時 {left.Minutes} 分後重置";
        return $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} 分後重置";
    }

    private static double Clamp(double percent) => Math.Round(Math.Max(0, Math.Min(100, percent)), 1);

    private static DateTimeOffset? ParseTime(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null)
            return null;
        if (token.Type == JTokenType.Date)
            return token.Value<DateTime>() is var d ? new DateTimeOffset(d.ToUniversalTime(), TimeSpan.Zero) : null;
        if (token.Type is JTokenType.Integer or JTokenType.Float)
        {
            var n = token.Value<double>();
            return n > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)n) : DateTimeOffset.FromUnixTimeSeconds((long)n);
        }
        return DateTimeOffset.TryParse(token.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    }
}
