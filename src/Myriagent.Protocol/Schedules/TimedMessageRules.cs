using System.Globalization;

namespace Myriagent.Schedules;

/// <summary>一次定時訊息的送出（最多留 30 筆）</summary>
internal sealed class TimedRun
{
    /// <summary>原定的時間</summary>
    public DateTime ScheduledAt { get; set; }

    /// <summary>真的送出的時間（錯過補送的會晚）；沒送出（錯過、出錯）是 null</summary>
    public DateTime? FiredAt { get; set; }

    public string? ConversationId { get; set; }

    /// <summary>sent（送出、這一輪還沒結束）、queued（排進佇列）、completed、failed、interrupted、missed（太久以前的一次性訊息不補）、error</summary>
    public string Outcome { get; set; } = "sent";

    public string? Error { get; set; }
}

/// <summary>
/// Myriagent 的定時訊息（S4；設計 docs/plans/2026-10-26-schedule-manager.md 第 8 節）：在指定的時間對某個對話送一則訊息，或每次開新對話。
/// 存在 &lt;資料根目錄&gt;\schedules\timed.json；只加欄位
/// </summary>
internal sealed class TimedMessage
{
    /// <summary>tm_ 加 8 碼</summary>
    public string Id { get; set; } = "";

    /// <summary>給代理的文字</summary>
    public string Text { get; set; } = "";

    /// <summary>名稱（排程頁與對話裡顯示；沒給時取文字的第一行）</summary>
    public string? Label { get; set; }

    /// <summary>重複：五欄的 cron（分 時 日 月 星期，本機時間）；一次性的是 null</summary>
    public string? Cron { get; set; }

    /// <summary>一次性的時間（本機）</summary>
    public DateTime? At { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>一次性的送過了</summary>
    public bool Done { get; set; }

    /// <summary>送進哪個對話；NewConversation 時是建立時的對話（只記來源）</summary>
    public string? ConversationId { get; set; }

    /// <summary>每次開新對話（帳戶、模型、推理強度、權限模式、資料夾照下面記的）</summary>
    public bool NewConversation { get; set; }

    public string? AccountId { get; set; }

    public string? Model { get; set; }

    public string? Effort { get; set; }

    public string? PermissionMode { get; set; }

    public string? Folder { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>建立它的對話（代理以 myriagent-schedule 建立的）；使用者在排程頁新增的是 null</summary>
    public string? CreatedBy { get; set; }

    public string? CreatedByTitle { get; set; }

    public string? CreatedByProvider { get; set; }

    /// <summary>上一次原定的時間（下一次從它之後算）</summary>
    public DateTime? LastScheduledAt { get; set; }

    public List<TimedRun> Runs { get; set; } = new();
}

/// <summary>到點時要做什麼</summary>
internal enum TimedDue
{
    /// <summary>還沒到</summary>
    Wait,

    /// <summary>送出（準時或補送一次）</summary>
    Fire,

    /// <summary>一次性的、錯過太久：不補，記成 missed</summary>
    Missed,
}

/// <summary>
/// 五欄的 cron：分（0-59）時（0-23）日（1-31）月（1-12）星期（0-7，0 與 7 都是週日；也收 SUN…SAT）；每欄收 *、數字、a-b、a,b、*/n、a-b/n。
/// 日與星期都有限制時照一般 cron 是「其中一個符合」
/// </summary>
internal sealed class CronSpec
{
    private readonly bool[] minutes = new bool[60];
    private readonly bool[] hours = new bool[24];
    private readonly bool[] days = new bool[32];
    private readonly bool[] months = new bool[13];
    private readonly bool[] weekdays = new bool[7];
    private bool dayRestricted;
    private bool weekdayRestricted;

    private CronSpec(string text) => Text = text;

    public string Text { get; }

    private static readonly string[] DayNames = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

    public static CronSpec? Parse(string? text, out string? problem)
    {
        problem = null;
        var parts = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
        {
            problem = "cron 要有五欄：分 時 日 月 星期（例如 0 9 * * 1-5）";
            return null;
        }
        var spec = new CronSpec(string.Join(' ', parts));
        if (!Field(parts[0], 0, 59, spec.minutes, null, out _) || !Field(parts[1], 0, 23, spec.hours, null, out _)
            || !Field(parts[2], 1, 31, spec.days, null, out spec.dayRestricted) || !Field(parts[3], 1, 12, spec.months, null, out _))
        {
            problem = $"cron「{text}」有一欄看不懂";
            return null;
        }
        var dow = new bool[8];
        if (!Field(parts[4], 0, 7, dow, DayNames, out spec.weekdayRestricted))
        {
            problem = $"cron「{text}」的星期看不懂";
            return null;
        }
        for (int i = 0; i < 7; i++)
            spec.weekdays[i] = dow[i] || (i == 0 && dow[7]);
        return spec;
    }

    private static bool Field(string text, int min, int max, bool[] into, string[]? names, out bool restricted)
    {
        restricted = text != "*";
        foreach (var raw in text.Split(','))
        {
            var item = raw.Trim().ToUpperInvariant();
            int step = 1;
            int slash = item.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(item[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                    return false;
                item = item[..slash];
            }
            int lo, hi;
            if (item == "*")
            {
                lo = min;
                hi = max;
            }
            else if (item.Contains('-'))
            {
                var ab = item.Split('-');
                if (ab.Length != 2 || !Value(ab[0], names, out lo) || !Value(ab[1], names, out hi))
                    return false;
            }
            else
            {
                if (!Value(item, names, out lo))
                    return false;
                hi = slash >= 0 ? max : lo;
            }
            if (lo < min || hi > max || lo > hi)
                return false;
            for (int v = lo; v <= hi; v += step)
                into[v] = true;
        }
        return true;
    }

    private static bool Value(string s, string[]? names, out int v)
    {
        if (names != null && Array.IndexOf(names, s) is var i and >= 0)
        {
            v = i;
            return true;
        }
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out v);
    }

    private bool DayMatches(DateTime d)
    {
        bool dom = days[d.Day], dow = weekdays[(int)d.DayOfWeek];
        if (dayRestricted && weekdayRestricted)
            return dom || dow;
        return dom && dow;
    }

    /// <summary>在 <paramref name="after"/> 之後（不含）的下一次；兩年內沒有就是 null（例如 2 月 30 日）</summary>
    public DateTime? Next(DateTime after)
    {
        var t = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind).AddMinutes(1);
        var limit = after.AddYears(2);
        while (t < limit)
        {
            if (!months[t.Month])
            {
                t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, t.Kind).AddMonths(1);
                continue;
            }
            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }
            if (!hours[t.Hour])
            {
                t = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind).AddHours(1);
                continue;
            }
            if (!minutes[t.Minute])
            {
                t = t.AddMinutes(1);
                continue;
            }
            return t;
        }
        return null;
    }

    /// <summary>中文摘要：每 15 分鐘、每小時的 :05、每天 09:00、平日 09:00、每週一、三 08:00、每月 1 日 03:00；說不清楚的寫原本的 cron</summary>
    public string Describe()
    {
        var mins = Enumerable.Range(0, 60).Where(i => minutes[i]).ToList();
        var hrs = Enumerable.Range(0, 24).Where(i => hours[i]).ToList();
        bool everyDay = !dayRestricted && Enumerable.Range(1, 12).All(i => months[i]);
        bool allWeek = weekdays.All(x => x);
        if (everyDay && allWeek && hrs.Count == 24 && mins.Count > 1)
        {
            int step = mins.Count > 1 ? mins[1] - mins[0] : 60;
            bool even = mins[0] < step && mins.Zip(mins.Skip(1), (a, b) => b - a).All(d => d == step) && 60 % step == 0;
            if (even)
                return step == 1 ? "每分鐘" : $"每 {step} 分鐘";
        }
        if (everyDay && allWeek && hrs.Count == 24 && mins.Count == 1)
            return mins[0] == 0 ? "每小時" : $"每小時的 :{mins[0]:00}";
        if (mins.Count != 1 || hrs.Count != 1)
            return "cron：" + Text;
        var time = $"{hrs[0]:00}:{mins[0]:00}";
        if (!Enumerable.Range(1, 12).All(i => months[i]))
            return "cron：" + Text;
        if (!dayRestricted)
        {
            if (allWeek)
                return "每天 " + time;
            var set = Enumerable.Range(0, 7).Where(i => weekdays[i]).ToList();
            if (set.SequenceEqual(new[] { 1, 2, 3, 4, 5 }))
                return "平日 " + time;
            if (set.SequenceEqual(new[] { 0, 6 }))
                return "週末 " + time;
            return "每週" + string.Join("、", set.OrderBy(d => (d + 6) % 7).Select(d => "日一二三四五六"[d])) + " " + time;
        }
        if (allWeek)
            return "每月 " + string.Join("、", Enumerable.Range(1, 31).Where(i => days[i])) + " 日 " + time;
        return "cron：" + Text;
    }
}

internal static class TimedMessageRules
{
    /// <summary>一次性的錯過超過這麼久就不補（改成通知）</summary>
    public static readonly TimeSpan OneShotGrace = TimeSpan.FromHours(12);

    /// <summary>一個 app 最多幾則定時訊息</summary>
    public const int MaxMessages = 200;

    /// <summary>文字上限</summary>
    public const int MaxTextLength = 20_000;

    /// <summary>每則留幾筆送出紀錄</summary>
    public const int KeepRuns = 30;

    /// <summary>重複的最短間隔（每分鐘也可以，但太密的對話會一直在跑）</summary>
    public const int MinIntervalMinutes = 5;

    /// <summary>
    /// 把使用者或代理給的時間寫法換成 cron 或一次性的時間：--at「2026-10-27 14:00」「14:00」（今天或明天）、--daily HH:mm、--weekdays HH:mm、
    /// --every 15m／2h、--cron 五欄。失敗回原因
    /// </summary>
    public static (string? Cron, DateTime? At, string? Problem) ParseWhen(string kind, string? value, DateTime now)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value))
            return (null, null, "少了時間");
        switch (kind)
        {
            case "at":
            {
                if (TryTime(value, out var hm))
                {
                    var today = now.Date + hm;
                    return (null, today > now ? today : today.AddDays(1), null);
                }
                string[] formats = { "yyyy-MM-dd HH:mm", "yyyy/M/d HH:mm", "yyyy-MM-ddTHH:mm", "yyyy/MM/dd HH:mm", "yyyy-M-d H:mm", "M/d HH:mm", "M-d HH:mm" };
                if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var at))
                {
                    // 沒寫年份（10/27 14:00）：今年的已經過了就是明年
                    bool hasYear = value.Count(char.IsDigit) >= 8 || value.Split('-', '/', ' ')[0].Length == 4;
                    if (!hasYear && at <= now)
                        at = at.AddYears(1);
                    at = DateTime.SpecifyKind(at, DateTimeKind.Local);
                    return at <= now ? (null, null, "時間已經過了") : (null, at, null);
                }
                return (null, null, "時間看不懂：用「2026-10-27 14:00」或「14:00」");
            }
            case "daily":
            case "weekdays":
                if (!TryTime(value, out var t))
                    return (null, null, "時間看不懂：用 HH:mm（例如 09:00）");
                return ($"{t.Minutes} {t.Hours} * * {(kind == "daily" ? "*" : "1-5")}", null, null);
            case "every":
            {
                var unit = value[^1];
                if (!int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
                    return (null, null, "間隔看不懂：用 15m、2h 這樣的寫法");
                if (unit is 'm' or 'M')
                {
                    if (n < MinIntervalMinutes || 60 % n != 0)
                        return (null, null, $"每幾分鐘要能整除 60，而且至少 {MinIntervalMinutes} 分鐘（例如 5、10、15、30）");
                    return ($"*/{n} * * * *", null, null);
                }
                if (unit is 'h' or 'H')
                {
                    if (n > 23 || 24 % n != 0)
                        return (null, null, "每幾小時要能整除 24（例如 1、2、3、4、6、8、12）");
                    return ($"0 */{n} * * *", null, null);
                }
                return (null, null, "間隔看不懂：用 15m、2h 這樣的寫法");
            }
            case "cron":
            {
                var spec = CronSpec.Parse(value, out var problem);
                if (spec == null)
                    return (null, null, problem);
                // 太密的拒絕：兩次之間少於 5 分鐘
                if (spec.Next(now) is { } a && spec.Next(a) is { } b && b - a < TimeSpan.FromMinutes(MinIntervalMinutes))
                    return (null, null, $"兩次之間至少要 {MinIntervalMinutes} 分鐘");
                return (spec.Text, null, null);
            }
            default:
                return (null, null, "不認得的時間寫法");
        }
    }

    private static bool TryTime(string s, out TimeSpan t)
    {
        t = default;
        var parts = s.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) || h > 23 || m > 59 || parts[1].Length != 2)
            return false;
        t = new TimeSpan(h, m, 0);
        return true;
    }

    /// <summary>時間的中文摘要：一次性的「一次：10/27 14:00」，重複的照 cron</summary>
    public static string Describe(TimedMessage m)
    {
        if (m.Cron != null)
            return CronSpec.Parse(m.Cron, out _)?.Describe() ?? "cron：" + m.Cron;
        return m.At is { } at ? $"一次：{at:yyyy/M/d HH:mm}" : "沒有時間";
    }

    /// <summary>名稱：給了就用，沒給取文字的第一行（最多 40 字）</summary>
    public static string LabelOf(TimedMessage m)
    {
        if (!string.IsNullOrWhiteSpace(m.Label))
            return m.Label!.Trim();
        var first = (m.Text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "定時訊息";
        return first.Length > 40 ? first[..40] + "…" : first;
    }

    /// <summary>下一次原定的時間（停用、做完、沒有時間的是 null）：從上一次原定的時間（沒有就是建立的時間）之後算</summary>
    public static DateTime? NextDue(TimedMessage m)
    {
        if (!m.Enabled || m.Done)
            return null;
        if (m.Cron == null)
            return m.At;
        var spec = CronSpec.Parse(m.Cron, out _);
        return spec?.Next(m.LastScheduledAt ?? m.CreatedAt);
    }

    /// <summary>
    /// 現在要不要送：還沒到 Wait；到了 Fire（錯過好幾次的重複只補一次，scheduled 是錯過的最後一次）；一次性的錯過超過 12 小時 Missed
    /// </summary>
    public static (TimedDue Due, DateTime Scheduled, bool Late) Decide(TimedMessage m, DateTime now)
    {
        if (NextDue(m) is not { } due || due > now)
            return (TimedDue.Wait, default, false);
        bool late = now - due > TimeSpan.FromMinutes(2);
        if (m.Cron == null)
            return now - due > OneShotGrace ? (TimedDue.Missed, due, true) : (TimedDue.Fire, due, late);
        // 重複的：錯過的只補一次，記成最後一次錯過的原定時間（下一次從它之後算）
        var spec = CronSpec.Parse(m.Cron, out _)!;
        var latest = due;
        for (int i = 0; i < 100_000 && spec.Next(latest) is { } n && n <= now; i++)
            latest = n;
        return (TimedDue.Fire, latest, late || latest != due);
    }

    /// <summary>給代理的文字：前面一行說明這是定時訊息（名稱、原定時間、補送的另外說）</summary>
    public static string AgentText(TimedMessage m, DateTime scheduled, bool late)
        => $"（這是 Myriagent 的定時訊息「{LabelOf(m)}」，{Describe(m)}，原定 {scheduled:yyyy/M/d HH:mm}{(late ? "，電腦或 app 當時沒開，現在補送" : "")}）\n\n{m.Text}";

    /// <summary>對話裡顯示的文字</summary>
    public static string DisplayText(TimedMessage m, bool late)
        => $"（定時訊息{(late ? "・補送" : "")}：{LabelOf(m)}）\n{m.Text}";

    /// <summary>[from, to) 之間預定送出的時間（排程頁的時間軸；最多 200 次）</summary>
    public static IReadOnlyList<DateTime> Upcoming(TimedMessage m, DateTime from, DateTime to)
    {
        var list = new List<DateTime>();
        if (!m.Enabled || m.Done)
            return list;
        if (m.Cron == null)
        {
            if (m.At is { } at && at >= from && at < to)
                list.Add(at);
            return list;
        }
        var spec = CronSpec.Parse(m.Cron, out _);
        var t = from.AddMinutes(-1);
        while (spec?.Next(t) is { } n && n < to && list.Count < ScheduleTriggerRules.MaxOccurrences)
        {
            list.Add(n);
            t = n;
        }
        return list;
    }

    public static void AddRun(TimedMessage m, TimedRun run)
    {
        m.Runs.Add(run);
        if (m.Runs.Count > KeepRuns)
            m.Runs.RemoveRange(0, m.Runs.Count - KeepRuns);
    }

    /// <summary>一輪的結果寫回送出紀錄的 outcome</summary>
    public static string OutcomeName(bool failed, bool interrupted) => failed ? "failed" : interrupted ? "interrupted" : "completed";

    /// <summary>結果的等級（排程頁的燈號與長條）</summary>
    public static string LevelOf(string outcome) => outcome switch
    {
        "completed" => "ok",
        "sent" or "queued" => "info",
        "interrupted" or "missed" => "warn",
        _ => "fail",
    };

    public static string OutcomeText(TimedRun r) => r.Outcome switch
    {
        "sent" => "已送出",
        "queued" => "已排進佇列",
        "completed" => "完成",
        "failed" => "失敗",
        "interrupted" => "被停止",
        "missed" => "錯過太久沒有補送",
        _ => "沒送出" + (r.Error != null ? "：" + r.Error : ""),
    };
}
