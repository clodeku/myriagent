using System.Globalization;
using System.Text;

namespace Myriagent.Schedules;

/// <summary>
/// 觸發條件的中文摘要（排程頁的「何時」）與一段時間內的預定執行時間（「接下來 24 小時」的時間軸、判斷兩次觀察之間有沒有漏看的執行）。
/// 時間一律是本機時間。登入、開機、事件這類不是時間觸發的不展開
/// </summary>
internal static class ScheduleTriggerRules
{
    /// <summary>一個工作在一段時間內最多展開幾次（每分鐘一次的工作 24 小時有 1440 次；時間軸只畫得出這麼多）</summary>
    public const int MaxOccurrences = 200;

    private static readonly string[] DayText = { "日", "一", "二", "三", "四", "五", "六" };

    /// <summary>整個工作的摘要：各觸發條件以「；」接起來；沒有觸發條件是「只能手動執行」</summary>
    public static string Describe(IReadOnlyList<ScheduleTrigger> triggers)
    {
        if (triggers.Count == 0)
            return "只能手動執行";
        return string.Join("；", triggers.Select(Describe).Distinct());
    }

    public static string Describe(ScheduleTrigger t)
    {
        var text = t.Kind switch
        {
            ScheduleTriggerKind.Once => DescribeOnce(t),
            ScheduleTriggerKind.Daily => DescribeRepeating(t, t.DaysInterval == 1 ? "每天" : $"每 {t.DaysInterval} 天"),
            ScheduleTriggerKind.Weekly => DescribeRepeating(t, WeeklyText(t)),
            ScheduleTriggerKind.Monthly => DescribeRepeating(t, MonthlyText(t)),
            ScheduleTriggerKind.MonthlyDayOfWeek => DescribeRepeating(t, MonthlyDowText(t)),
            ScheduleTriggerKind.Logon => (t.UserId != null ? $"{ShortUser(t.UserId)} 登入時" : "任何人登入時") + DelayText(t),
            ScheduleTriggerKind.Boot => "開機時" + DelayText(t),
            ScheduleTriggerKind.Idle => "電腦閒置時",
            ScheduleTriggerKind.Event => "發生特定事件時" + DelayText(t),
            ScheduleTriggerKind.Registration => "建立或修改時" + DelayText(t),
            ScheduleTriggerKind.SessionStateChange => SessionText(t.StateChange) + DelayText(t),
            _ => "其他條件",
        };
        if (!t.Enabled)
            text += "（已停用）";
        return text;
    }

    private static string DescribeOnce(ScheduleTrigger t)
    {
        if (t.Start is not { } start)
            return "一次";
        if (t.RepeatInterval is { } interval)
        {
            var head = $"{start:M/d HH:mm} 起{IntervalText(interval)}";
            return t.RepeatDuration is { } duration ? $"{head}，持續{SpanText(duration)}" : head;
        }
        return $"一次：{start:yyyy/M/d HH:mm}";
    }

    /// <summary>每天、每週、每月：沒有重複間隔是「每天 02:30」；重複間隔蓋滿整個週期是「每 1 小時（00:20 起）」，否則「每天 08:00 起每 15 分鐘，持續 10 小時」</summary>
    private static string DescribeRepeating(ScheduleTrigger t, string period)
    {
        var time = t.Start is { } s ? s.ToString("HH:mm", CultureInfo.InvariantCulture) : "";
        if (t.RepeatInterval is not { } interval)
            return $"{period} {time}".TrimEnd();
        bool coversDay = t.Kind == ScheduleTriggerKind.Daily && t.DaysInterval == 1 && (t.RepeatDuration is null || t.RepeatDuration >= TimeSpan.FromDays(1));
        if (coversDay)
            return $"{IntervalText(interval).TrimStart()}（{time} 起）";
        var head = $"{period} {time} 起{IntervalText(interval)}";
        return t.RepeatDuration is { } duration ? $"{head}，持續{SpanText(duration)}" : head;
    }

    private static string WeeklyText(ScheduleTrigger t)
    {
        var days = t.DaysOfWeek;
        string which;
        if (days.Count == 7)
            which = "每天";
        else if (days.Count == 5 && !days.Contains(DayOfWeek.Saturday) && !days.Contains(DayOfWeek.Sunday))
            which = "平日";
        else if (days.Count == 2 && days.Contains(DayOfWeek.Saturday) && days.Contains(DayOfWeek.Sunday))
            which = "週末";
        else
            which = "週" + string.Join("、", OrderMondayFirst(days).Select(d => DayText[(int)d]));
        if (t.WeeksInterval > 1)
            return $"每 {t.WeeksInterval} 週的{which}";
        return which is "每天" or "平日" or "週末" ? which : "每" + which;
    }

    private static string MonthlyText(ScheduleTrigger t)
    {
        var parts = t.DaysOfMonth.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToList();
        var days = parts.Count > 0 ? string.Join("、", parts) + " 日" : "";
        if (t.LastDayOfMonth)
            days = days.Length > 0 ? days + "與最後一天" : "最後一天";
        var prefix = MonthsPrefix(t.Months);
        return days.Length > 0 && char.IsDigit(days[0]) ? $"{prefix} {days}" : prefix + days;
    }

    private static string MonthlyDowText(ScheduleTrigger t)
    {
        var weeks = t.WeeksOfMonth.Select(w => w switch { 1 => "第一個", 2 => "第二個", 3 => "第三個", 4 => "第四個", _ => "" }).ToList();
        if (t.LastWeekOfMonth)
            weeks.Add("最後一個");
        var days = string.Join("、", OrderMondayFirst(t.DaysOfWeek).Select(d => "週" + DayText[(int)d]));
        return MonthsPrefix(t.Months) + string.Join("與", weeks) + days;
    }

    private static string MonthsPrefix(IReadOnlyList<int> months)
        => months.Count is 0 or 12 ? "每月" : $"每年 {string.Join("、", months)} 月的";

    private static IEnumerable<DayOfWeek> OrderMondayFirst(IEnumerable<DayOfWeek> days)
        => days.OrderBy(d => ((int)d + 6) % 7);

    private static string SessionText(string? state) => state switch
    {
        "SessionLock" => "鎖定時",
        "SessionUnlock" => "解除鎖定時",
        "ConsoleConnect" => "本機連線時",
        "ConsoleDisconnect" => "本機中斷連線時",
        "RemoteConnect" => "遠端連線時",
        "RemoteDisconnect" => "遠端中斷連線時",
        _ => "工作階段變更時",
    };

    private static string DelayText(ScheduleTrigger t) => t.Delay is { } d ? $"（延遲{SpanText(d)}）" : "";

    private static string ShortUser(string user)
    {
        int slash = user.LastIndexOf('\\');
        return slash >= 0 ? user[(slash + 1)..] : user;
    }

    /// <summary>重複間隔：「每 15 分鐘」「每小時」「每 2 小時」（開頭帶一個空白，接在時間後面）</summary>
    internal static string IntervalText(TimeSpan interval)
    {
        if (interval.TotalMinutes < 60)
            return $" 每 {(int)Math.Max(1, interval.TotalMinutes)} 分鐘";
        if (interval == TimeSpan.FromHours(1))
            return " 每小時";
        if (interval.TotalHours < 24 && interval.Minutes == 0)
            return $" 每 {(int)interval.TotalHours} 小時";
        return " 每" + SpanText(interval);
    }

    /// <summary>期間的中文：「 10 小時」「 1 天」「 1 小時 30 分」（開頭帶一個空白）</summary>
    internal static string SpanText(TimeSpan span)
    {
        var sb = new StringBuilder();
        if (span.Days > 0)
            sb.Append(' ').Append(span.Days).Append(" 天");
        if (span.Hours > 0)
            sb.Append(' ').Append(span.Hours).Append(" 小時");
        if (span.Minutes > 0)
            sb.Append(' ').Append(span.Minutes).Append(" 分");
        if (sb.Length == 0)
            sb.Append(' ').Append((int)span.TotalSeconds).Append(" 秒");
        return sb.ToString();
    }

    /// <summary>
    /// 在 [from, to) 之間預定的執行時間（由小到大、去重、最多 <paramref name="max"/> 次）。只展開時間觸發的（一次、每天、每週、每月）；
    /// 停用的觸發條件、過了到期時間的不算。無限期重複的每天觸發，前一天開始的那串也延續到今天
    /// </summary>
    public static IReadOnlyList<DateTime> Occurrences(IReadOnlyList<ScheduleTrigger> triggers, DateTime from, DateTime to, int max = MaxOccurrences)
    {
        var result = new SortedSet<DateTime>();
        foreach (var t in triggers)
        {
            if (!t.Enabled || t.Start is not { } start)
                continue;
            if (t.Kind is not (ScheduleTriggerKind.Once or ScheduleTriggerKind.Daily or ScheduleTriggerKind.Weekly
                or ScheduleTriggerKind.Monthly or ScheduleTriggerKind.MonthlyDayOfWeek))
                continue;
            var bases = new List<DateTime>();
            if (t.Kind == ScheduleTriggerKind.Once)
            {
                bases.Add(start);
            }
            else
            {
                // 前一個基準開始的重複可能延續到 from 之後：往回看重複持續的長度（無限期的看一天）
                var lookBack = t.RepeatInterval == null ? TimeSpan.Zero : t.RepeatDuration ?? TimeSpan.FromDays(1);
                if (lookBack > TimeSpan.FromDays(31))
                    lookBack = TimeSpan.FromDays(31);
                for (var day = (from - lookBack).Date; day < to; day = day.AddDays(1))
                {
                    if (day < start.Date || !MatchesDay(t, day, start))
                        continue;
                    bases.Add(day + start.TimeOfDay);
                }
            }
            foreach (var b in bases)
            {
                if (b < start)
                    continue;
                if (t.RepeatInterval is not { } interval)
                {
                    Add(result, t, b, from, to);
                    continue;
                }
                var until = t.RepeatDuration is { } d ? b + d : (t.Kind == ScheduleTriggerKind.Once ? DateTime.MaxValue : b.AddDays(1));
                if (until > to)
                    until = to;
                var first = b;
                if (b < from)
                {
                    long steps = (long)Math.Ceiling((from - b).Ticks / (double)interval.Ticks);
                    first = b + TimeSpan.FromTicks(interval.Ticks * steps);
                }
                for (var x = first; x < until; x += interval)
                {
                    Add(result, t, x, from, to);
                    if (result.Count > max * 2)
                        break;
                }
            }
        }
        return result.Take(max).ToList();
    }

    private static void Add(SortedSet<DateTime> set, ScheduleTrigger t, DateTime x, DateTime from, DateTime to)
    {
        if (x < from || x >= to)
            return;
        if (t.End is { } end && x > end)
            return;
        set.Add(x);
    }

    internal static bool MatchesDay(ScheduleTrigger t, DateTime day, DateTime start)
    {
        switch (t.Kind)
        {
            case ScheduleTriggerKind.Daily:
                return (day - start.Date).Days % t.DaysInterval == 0;
            case ScheduleTriggerKind.Weekly:
            {
                if (!t.DaysOfWeek.Contains(day.DayOfWeek))
                    return false;
                if (t.WeeksInterval <= 1)
                    return true;
                var startWeek = start.Date.AddDays(-(int)start.DayOfWeek);
                var week = (day.Date.AddDays(-(int)day.DayOfWeek) - startWeek).Days / 7;
                return week % t.WeeksInterval == 0;
            }
            case ScheduleTriggerKind.Monthly:
            {
                if (t.Months.Count > 0 && !t.Months.Contains(day.Month))
                    return false;
                bool last = day.AddDays(1).Month != day.Month;
                return t.DaysOfMonth.Contains(day.Day) || (t.LastDayOfMonth && last);
            }
            case ScheduleTriggerKind.MonthlyDayOfWeek:
            {
                if (t.Months.Count > 0 && !t.Months.Contains(day.Month))
                    return false;
                if (!t.DaysOfWeek.Contains(day.DayOfWeek))
                    return false;
                int week = (day.Day - 1) / 7 + 1;
                bool lastWeek = day.AddDays(7).Month != day.Month;
                return t.WeeksOfMonth.Contains(week) || (t.LastWeekOfMonth && lastWeek);
            }
            default:
                return false;
        }
    }
}
