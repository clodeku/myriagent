using System.Globalization;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 各帳戶 token 計數的同步（tok1、tok2；docs/sync-protocol.md 14.9、14.10）的純規則：檢查 <see cref="TokensPublish"/>（伺服器）與 <see cref="TokensUpdate"/>（桌面）、
/// 本機計數換成上傳的格與決定這次送什麼（桌面）、各台的格加總成瀏覽器的 Tokens（伺服器，web-protocol 的 Tokens）、給每台的其他電腦合計（伺服器）與
/// 從其中取一個帳戶的天數合計（桌面）。日期一律是字串 yyyy-MM-dd，不比較各電腦的時鐘
/// </summary>
internal static class TokenTallySyncRules
{
    /// <summary>保留的天數（今天算一天；同桌面計數檔的保留天數）</summary>
    public const int RetentionDays = 30;

    /// <summary>一則最多幾格（30 天 × 100 個帳戶）</summary>
    public const int MaxRows = 3000;

    /// <summary>單一數字的上限（10^15）</summary>
    public const long MaxCount = 1_000_000_000_000_000;

    /// <summary>同一條連線兩次送出至少隔多久（對話執行中計數一直在變）</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);

    /// <summary>沒有變動通知時多久重算一次（目錄的連結變了、跨日時舊的一天滑出保留範圍；伺服器給桌面的轉送也是：撤銷裝置、刪除帳戶）</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);

    /// <summary>伺服器給桌面的 <see cref="TokensUpdate"/>（tok2）兩次重讀至少隔多久（任何一台送了就重讀，期間的併進同一次）</summary>
    public static readonly TimeSpan FanoutInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 伺服器在連線證明完成之後等多久才送第一份 <see cref="TokensUpdate"/>：ConnectionProven 時 ProveResponse 還沒到裝置，
    /// 0.2.621～0.2.624 的桌面在交握完成前收到會丟例外、斷線重連（之後的版本會暫存，sync-protocol 14.10）
    /// </summary>
    public static readonly TimeSpan FirstSendDelay = TimeSpan.FromSeconds(5);

    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>瀏覽器版的 Tokens 的 <c>scope</c>：各電腦的合計（桌面的 Tokens 沒有這個鍵）</summary>
    public const string AllDevicesScope = "all";

    public static string DayKey(DateOnly day) => day.ToString(DayFormat, CultureInfo.InvariantCulture);

    /// <summary>嚴格的 yyyy-MM-dd（真的存在的日期）</summary>
    public static bool IsDayKey(string? day)
        => day is { Length: 10 } && DateOnly.TryParseExact(day, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>伺服器的檢查：null＝合格，否則是不合格的原因（伺服器回 <c>invalid</c>）</summary>
    public static string? Check(TokensPublish? publish) => CheckRows(publish?.Rows);

    /// <summary>桌面收到的 <see cref="TokensUpdate"/>（tok2）：同 <see cref="Check"/> 的格式與上限；null＝合格，否則整則略過</summary>
    public static string? CheckUpdate(TokensUpdate? update) => CheckRows(update?.Rows);

    private static string? CheckRows(IReadOnlyList<TokenTallyRow>? rows)
    {
        if (rows is null)
            return "沒有內容";
        if (rows.Count > MaxRows)
            return "格數太多";
        var keys = new HashSet<(string, string)>();
        foreach (var row in rows)
        {
            if (row is null || !SyncIds.IsLogicalAccountId(row.LogicalAccountId) || !IsDayKey(row.Day))
                return "帳戶或日期不正確";
            if (!InRange(row.Input) || !InRange(row.Cached) || !InRange(row.Output))
                return "數字不正確";
            if (!keys.Add((row.LogicalAccountId, row.Day)))
                return "同一格出現兩次";
        }
        return null;
    }

    private static bool InRange(long value) => value is >= 0 and <= MaxCount;

    /// <summary>
    /// 桌面：本機的計數換成上傳的格——依目錄裡這台的連結（<paramref name="logicalOf"/>：本機帳戶 → 邏輯帳戶）換成邏輯帳戶，沒有連結的略過，
    /// 同一格的加總；全是 0 的格不送；只留 <paramref name="today"/> 往回 <see cref="RetentionDays"/> 天內的（比今天晚的照留，同計數檔）。
    /// 依（日期新到舊、帳戶）排序，超過 <see cref="MaxRows"/> 時留日期新的
    /// </summary>
    public static List<TokenTallyRow> ToRows(IEnumerable<LocalTokenTally> local, IReadOnlyDictionary<string, string> logicalOf, DateOnly today)
    {
        var from = DayKey(today.AddDays(-(RetentionDays - 1)));
        var sums = new Dictionary<(string Logical, string Day), (long Input, long Cached, long Output)>();
        foreach (var tally in local)
        {
            if (!logicalOf.TryGetValue(tally.LocalAccountId, out var logical) || !IsDayKey(tally.Day) || string.CompareOrdinal(tally.Day, from) < 0)
                continue;
            var key = (logical, tally.Day);
            var sum = sums.GetValueOrDefault(key);
            sums[key] = (Clamp(sum.Input + Math.Max(0, tally.Input)), Clamp(sum.Cached + Math.Max(0, tally.Cached)), Clamp(sum.Output + Math.Max(0, tally.Output)));
        }
        return sums
            .Where(s => s.Value is not (0, 0, 0))
            .OrderByDescending(s => s.Key.Day, StringComparer.Ordinal)
            .ThenBy(s => s.Key.Logical, StringComparer.Ordinal)
            .Take(MaxRows)
            .Select(s => new TokenTallyRow(s.Key.Logical, s.Key.Day, s.Value.Input, s.Value.Cached, s.Value.Output))
            .ToList();
    }

    private static long Clamp(long value) => Math.Min(value, MaxCount);

    /// <summary>
    /// 伺服器：給 <paramref name="selfDeviceId"/> 的 <see cref="TokensUpdate"/>（tok2）——它以外的電腦的格，同一個邏輯帳戶、同一天的相加（不超過 <see cref="MaxCount"/>）。
    /// 全是 0 的格不送；依（日期新到舊、帳戶）排序，超過 <see cref="MaxRows"/> 時留日期新的（同樣的資料逐位元組相同，轉送迴圈以序列化的字串比對有沒有變）。
    /// 呼叫端先篩掉撤銷的裝置、刪除的帳戶與保留天數以外的列
    /// </summary>
    public static TokensUpdate Others(IEnumerable<(string DeviceId, TokenTallyRow Row)> rows, string selfDeviceId)
    {
        var sums = new Dictionary<(string Logical, string Day), (long Input, long Cached, long Output)>();
        foreach (var (deviceId, row) in rows)
        {
            if (deviceId == selfDeviceId)
                continue;
            var key = (row.LogicalAccountId, row.Day);
            var sum = sums.GetValueOrDefault(key);
            sums[key] = (Clamp(sum.Input + Math.Max(0, row.Input)), Clamp(sum.Cached + Math.Max(0, row.Cached)), Clamp(sum.Output + Math.Max(0, row.Output)));
        }
        return new TokensUpdate(sums
            .Where(s => s.Value is not (0, 0, 0))
            .OrderByDescending(s => s.Key.Day, StringComparer.Ordinal)
            .ThenBy(s => s.Key.Logical, StringComparer.Ordinal)
            .Take(MaxRows)
            .Select(s => new TokenTallyRow(s.Key.Logical, s.Key.Day, s.Value.Input, s.Value.Cached, s.Value.Output))
            .ToList());
    }

    /// <summary>
    /// 桌面：<paramref name="rows"/>（其他電腦的格，<see cref="TokensUpdate"/>）裡 <paramref name="logicalAccountId"/> 在 <paramref name="today"/>（這台的本機日期）往回
    /// <paramref name="days"/> 天（含今天）的合計；比今天晚的格不算（同 <see cref="View"/>）
    /// </summary>
    public static (long Input, long Cached, long Output) Sum(IEnumerable<TokenTallyRow> rows, string logicalAccountId, DateOnly today, int days)
    {
        var to = DayKey(today);
        var from = DayKey(today.AddDays(-(days - 1)));
        long input = 0, cached = 0, output = 0;
        foreach (var row in rows)
        {
            if (row.LogicalAccountId != logicalAccountId || string.CompareOrdinal(row.Day, from) < 0 || string.CompareOrdinal(row.Day, to) > 0)
                continue;
            input += row.Input;
            cached += row.Cached;
            output += row.Output;
        }
        return (input, cached, output);
    }

    /// <summary>
    /// 桌面：這次要送什麼。<paramref name="sent"/> 是這條連線上次送成功之後伺服器有的格（null＝這條連線還沒送過）。
    /// 還沒送過、上次有的格這次沒有（連結拿掉了、滑出保留範圍）、或任何數字變小（計數檔被重設）→ 整份；否則只送新的與變了的格；都沒變回 null
    /// </summary>
    public static TokensPublish? Plan(IReadOnlyDictionary<(string LogicalAccountId, string Day), TokenTallyRow>? sent, IReadOnlyList<TokenTallyRow> current)
    {
        if (sent is null)
            return new TokensPublish(true, current);
        var now = current.ToDictionary(r => (r.LogicalAccountId, r.Day));
        foreach (var (key, before) in sent)
        {
            if (!now.TryGetValue(key, out var after) || after.Input < before.Input || after.Cached < before.Cached || after.Output < before.Output)
                return new TokensPublish(true, current);
        }
        var changed = current.Where(r => !sent.TryGetValue((r.LogicalAccountId, r.Day), out var before) || before != r).ToList();
        return changed.Count == 0 ? null : new TokensPublish(false, changed);
    }

    /// <summary>
    /// 伺服器：各台的格加總成瀏覽器的 Tokens（web-protocol 的 Tokens，鍵是邏輯帳戶）。<c>today</c> 是 <paramref name="today"/>（伺服器的本機日期）那一天，
    /// <c>week</c>、<c>month</c> 是往回 7、30 天（含今天）。<c>scope</c> 一律是 <c>all</c>（各電腦的合計）：只列這 30 天內有任何一格的帳戶，
    /// 沒列的帳戶網頁照舊顯示方案名稱；比今天晚的格不算。帳戶依 id 排序（同樣的資料逐位元組相同，閘道以字串比對有沒有變）
    /// </summary>
    public static JObject View(IEnumerable<TokenTallyRow> rows, DateOnly today)
    {
        var todayKey = DayKey(today);
        var weekFrom = DayKey(today.AddDays(-6));
        var monthFrom = DayKey(today.AddDays(-(RetentionDays - 1)));
        var sums = new SortedDictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (string.CompareOrdinal(row.Day, monthFrom) < 0 || string.CompareOrdinal(row.Day, todayKey) > 0)
                continue;
            if (!sums.TryGetValue(row.LogicalAccountId, out var sum))
                sums[row.LogicalAccountId] = sum = new long[9];
            Accumulate(sum, 6, row);
            if (string.CompareOrdinal(row.Day, weekFrom) >= 0)
                Accumulate(sum, 3, row);
            if (row.Day == todayKey)
                Accumulate(sum, 0, row);
        }
        var accounts = new JObject();
        foreach (var (id, sum) in sums)
        {
            accounts[id] = new JObject
            {
                ["today"] = Count(sum, 0),
                ["week"] = Count(sum, 3),
                ["month"] = Count(sum, 6),
            };
        }
        return new JObject { ["day"] = todayKey, ["scope"] = AllDevicesScope, ["accounts"] = accounts };
    }

    private static void Accumulate(long[] sum, int at, TokenTallyRow row)
    {
        sum[at] += row.Input;
        sum[at + 1] += row.Cached;
        sum[at + 2] += row.Output;
    }

    private static JObject Count(long[] sum, int at) => new() { ["input"] = sum[at], ["cached"] = sum[at + 1], ["output"] = sum[at + 2] };
}
