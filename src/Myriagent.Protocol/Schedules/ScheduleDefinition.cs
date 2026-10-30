using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Myriagent.Schedules;

/// <summary>觸發條件的種類（工作排程器 XML 的 *Trigger 元素）</summary>
internal enum ScheduleTriggerKind
{
    /// <summary>TimeTrigger：某個時間一次（可帶重複間隔）</summary>
    Once,
    Daily,
    Weekly,
    Monthly,
    MonthlyDayOfWeek,
    Logon,
    Boot,
    Idle,
    Event,
    Registration,
    SessionStateChange,
    Unknown,
}

/// <summary>一個觸發條件（ScheduleXmlRules.Parse 從工作的 XML 定義讀出；只讀、不寫回）</summary>
internal sealed class ScheduleTrigger
{
    public ScheduleTriggerKind Kind { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>開始時間（本機時間；XML 帶時區的已換成本機）</summary>
    public DateTime? Start { get; init; }

    /// <summary>到期時間（本機時間）；沒有是 null</summary>
    public DateTime? End { get; init; }

    /// <summary>重複間隔（Repetition/Interval）；不重複是 null</summary>
    public TimeSpan? RepeatInterval { get; init; }

    /// <summary>重複持續多久（Repetition/Duration）；null＋有間隔＝無限期</summary>
    public TimeSpan? RepeatDuration { get; init; }

    /// <summary>每幾天（Daily）</summary>
    public int DaysInterval { get; init; } = 1;

    /// <summary>每幾週（Weekly）</summary>
    public int WeeksInterval { get; init; } = 1;

    /// <summary>星期幾（Weekly、MonthlyDayOfWeek）</summary>
    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = Array.Empty<DayOfWeek>();

    /// <summary>幾月（Monthly、MonthlyDayOfWeek；空＝每個月）</summary>
    public IReadOnlyList<int> Months { get; init; } = Array.Empty<int>();

    /// <summary>每月的幾號（Monthly）</summary>
    public IReadOnlyList<int> DaysOfMonth { get; init; } = Array.Empty<int>();

    /// <summary>每月最後一天（Monthly 的 Day＝Last）</summary>
    public bool LastDayOfMonth { get; init; }

    /// <summary>每月的第幾週（MonthlyDayOfWeek：1～4）</summary>
    public IReadOnlyList<int> WeeksOfMonth { get; init; } = Array.Empty<int>();

    /// <summary>每月的最後一週（MonthlyDayOfWeek 的 Week＝Last）</summary>
    public bool LastWeekOfMonth { get; init; }

    /// <summary>延遲（Logon、Boot 等的 Delay）</summary>
    public TimeSpan? Delay { get; init; }

    /// <summary>只對某個使用者（Logon、SessionStateChange 的 UserId）</summary>
    public string? UserId { get; init; }

    /// <summary>SessionStateChange 的 StateChange（ConsoleConnect、SessionLock…）</summary>
    public string? StateChange { get; init; }
}

/// <summary>一個動作。Exec 才有指令；其他種類（ComHandler、寄信、訊息）只記種類</summary>
internal sealed record ScheduleAction(string Kind, string? Command, string? Arguments, string? WorkingDirectory);

/// <summary>工作的定義（XML 讀出的；狀態與上次結果另外從工作排程器取得）</summary>
internal sealed class ScheduleDefinition
{
    public string? Author { get; init; }

    public string? Description { get; init; }

    /// <summary>RegistrationInfo/Date（建立時間，本機時間；很多工作沒有）</summary>
    public DateTime? Created { get; init; }

    public IReadOnlyList<ScheduleTrigger> Triggers { get; init; } = Array.Empty<ScheduleTrigger>();

    public IReadOnlyList<ScheduleAction> Actions { get; init; } = Array.Empty<ScheduleAction>();

    /// <summary>執行身分（Principal/UserId 或 GroupId；SID 或「網域\使用者」）</summary>
    public string? RunAs { get; init; }

    /// <summary>Principal/LogonType：InteractiveToken（只在登入時）、Password（不論是否登入）、S4U、ServiceAccount…</summary>
    public string? LogonType { get; init; }

    /// <summary>Principal/RunLevel 是 HighestAvailable（以最高權限執行）</summary>
    public bool Elevated { get; init; }

    /// <summary>Settings/Enabled（預設 true）</summary>
    public bool Enabled { get; init; } = true;

    public bool Hidden { get; init; }

    public bool DisallowStartIfOnBatteries { get; init; }

    public bool StopIfGoingOnBatteries { get; init; }

    public bool StartWhenAvailable { get; init; }

    public bool RunOnlyIfNetworkAvailable { get; init; }

    public bool RunOnlyIfIdle { get; init; }

    public bool WakeToRun { get; init; }

    /// <summary>ExecutionTimeLimit；PT0S（不限）與沒寫都是 null</summary>
    public TimeSpan? TimeLimit { get; init; }

    /// <summary>MultipleInstancesPolicy（IgnoreNew、Parallel、Queue、StopExisting）</summary>
    public string? MultipleInstances { get; init; }
}

/// <summary>工作排程器 XML（Task Scheduler Schema 1.x）的解析：只讀需要顯示與判斷的欄位，讀不懂的忽略</summary>
internal static class ScheduleXmlRules
{
    /// <summary>解析工作的 XML 定義；不是合法的 XML 丟 XmlException</summary>
    public static ScheduleDefinition Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new XmlException("沒有根元素");
        XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
        string? Text(XElement? e, string name) => Child(e, name)?.Value.Trim() is { Length: > 0 } v ? v : null;
        bool Flag(XElement? e, string name, bool fallback) => Text(e, name) is { } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;

        var registration = Child(root, "RegistrationInfo");
        var principal = Child(Child(root, "Principals"), "Principal");
        var settings = Child(root, "Settings");

        var actions = new List<ScheduleAction>();
        foreach (var a in Child(root, "Actions")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            actions.Add(a.Name.LocalName == "Exec"
                ? new ScheduleAction("Exec", Text(a, "Command"), Text(a, "Arguments"), Text(a, "WorkingDirectory"))
                : new ScheduleAction(a.Name.LocalName, null, null, null));
        }

        var triggers = new List<ScheduleTrigger>();
        foreach (var t in Child(root, "Triggers")?.Elements() ?? Enumerable.Empty<XElement>())
            triggers.Add(ParseTrigger(t, Child, Text, Flag));

        return new ScheduleDefinition
        {
            Author = Text(registration, "Author"),
            Description = Text(registration, "Description"),
            Created = ParseTime(Text(registration, "Date")),
            Triggers = triggers,
            Actions = actions,
            RunAs = Text(principal, "UserId") ?? Text(principal, "GroupId"),
            LogonType = Text(principal, "LogonType"),
            Elevated = string.Equals(Text(principal, "RunLevel"), "HighestAvailable", StringComparison.OrdinalIgnoreCase),
            Enabled = Flag(settings, "Enabled", true),
            Hidden = Flag(settings, "Hidden", false),
            DisallowStartIfOnBatteries = Flag(settings, "DisallowStartIfOnBatteries", false),
            StopIfGoingOnBatteries = Flag(settings, "StopIfGoingOnBatteries", false),
            StartWhenAvailable = Flag(settings, "StartWhenAvailable", false),
            RunOnlyIfNetworkAvailable = Flag(settings, "RunOnlyIfNetworkAvailable", false),
            RunOnlyIfIdle = Flag(settings, "RunOnlyIfIdle", false),
            WakeToRun = Flag(settings, "WakeToRun", false),
            TimeLimit = ParseDuration(Text(settings, "ExecutionTimeLimit")) is { } limit && limit > TimeSpan.Zero ? limit : null,
            MultipleInstances = Text(settings, "MultipleInstancesPolicy"),
        };
    }

    private static ScheduleTrigger ParseTrigger(
        XElement t,
        Func<XElement?, string, XElement?> child,
        Func<XElement?, string, string?> text,
        Func<XElement?, string, bool, bool> flag)
    {
        var repetition = child(t, "Repetition");
        var interval = ParseDuration(text(repetition, "Interval"));
        var duration = ParseDuration(text(repetition, "Duration"));
        var common = new
        {
            Enabled = flag(t, "Enabled", true),
            Start = ParseTime(text(t, "StartBoundary")),
            End = ParseTime(text(t, "EndBoundary")),
            Interval = interval is { } i && i > TimeSpan.Zero ? interval : null,
            Duration = duration is { } d && d > TimeSpan.Zero ? duration : null,
            Delay = ParseDuration(text(t, "Delay")) is { } delay && delay > TimeSpan.Zero ? delay : (TimeSpan?)null,
            UserId = text(t, "UserId"),
        };
        ScheduleTrigger Make(ScheduleTriggerKind kind, int days = 1, int weeks = 1, IReadOnlyList<DayOfWeek>? dow = null, IReadOnlyList<int>? months = null,
            IReadOnlyList<int>? dom = null, bool lastDay = false, IReadOnlyList<int>? wom = null, bool lastWeek = false, string? state = null)
            => new()
            {
                Kind = kind,
                Enabled = common.Enabled,
                Start = common.Start,
                End = common.End,
                RepeatInterval = common.Interval,
                RepeatDuration = common.Interval != null ? common.Duration : null,
                DaysInterval = Math.Max(1, days),
                WeeksInterval = Math.Max(1, weeks),
                DaysOfWeek = dow ?? Array.Empty<DayOfWeek>(),
                Months = months ?? Array.Empty<int>(),
                DaysOfMonth = dom ?? Array.Empty<int>(),
                LastDayOfMonth = lastDay,
                WeeksOfMonth = wom ?? Array.Empty<int>(),
                LastWeekOfMonth = lastWeek,
                Delay = common.Delay,
                UserId = common.UserId,
                StateChange = state,
            };

        switch (t.Name.LocalName)
        {
            case "TimeTrigger":
                return Make(ScheduleTriggerKind.Once);
            case "BootTrigger":
                return Make(ScheduleTriggerKind.Boot);
            case "LogonTrigger":
                return Make(ScheduleTriggerKind.Logon);
            case "IdleTrigger":
                return Make(ScheduleTriggerKind.Idle);
            case "EventTrigger":
                return Make(ScheduleTriggerKind.Event);
            case "RegistrationTrigger":
                return Make(ScheduleTriggerKind.Registration);
            case "SessionStateChangeTrigger":
                return Make(ScheduleTriggerKind.SessionStateChange, state: text(t, "StateChange"));
            case "CalendarTrigger":
                if (child(t, "ScheduleByDay") is { } byDay)
                    return Make(ScheduleTriggerKind.Daily, days: ParseInt(text(byDay, "DaysInterval"), 1));
                if (child(t, "ScheduleByWeek") is { } byWeek)
                    return Make(ScheduleTriggerKind.Weekly, weeks: ParseInt(text(byWeek, "WeeksInterval"), 1), dow: ParseDays(child(byWeek, "DaysOfWeek")));
                if (child(t, "ScheduleByMonth") is { } byMonth)
                {
                    var days = child(byMonth, "DaysOfMonth")?.Elements().Select(e => e.Value.Trim()).ToList() ?? new List<string>();
                    return Make(ScheduleTriggerKind.Monthly, months: ParseMonths(child(byMonth, "Months")),
                        dom: days.Select(d => ParseInt(d, 0)).Where(d => d is >= 1 and <= 31).Distinct().Order().ToList(),
                        lastDay: days.Any(d => d.Equals("Last", StringComparison.OrdinalIgnoreCase)));
                }
                if (child(t, "ScheduleByMonthDayOfWeek") is { } byDow)
                {
                    var weeks = child(byDow, "Weeks")?.Elements().Select(e => e.Value.Trim()).ToList() ?? new List<string>();
                    return Make(ScheduleTriggerKind.MonthlyDayOfWeek, dow: ParseDays(child(byDow, "DaysOfWeek")), months: ParseMonths(child(byDow, "Months")),
                        wom: weeks.Select(w => ParseInt(w, 0)).Where(w => w is >= 1 and <= 4).Distinct().Order().ToList(),
                        lastWeek: weeks.Any(w => w.Equals("Last", StringComparison.OrdinalIgnoreCase)));
                }
                return Make(ScheduleTriggerKind.Unknown);
            default:
                return Make(ScheduleTriggerKind.Unknown);
        }
    }

    private static readonly string[] DayNames = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };

    private static readonly string[] MonthNames =
        { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    private static List<DayOfWeek> ParseDays(XElement? e)
        => e?.Elements().Select(x => Array.IndexOf(DayNames, x.Name.LocalName)).Where(i => i >= 0).Distinct().Order().Select(i => (DayOfWeek)i).ToList()
           ?? new List<DayOfWeek>();

    private static List<int> ParseMonths(XElement? e)
        => e?.Elements().Select(x => Array.IndexOf(MonthNames, x.Name.LocalName) + 1).Where(i => i >= 1).Distinct().Order().ToList() ?? new List<int>();

    private static int ParseInt(string? s, int fallback)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>XML 的時間：帶時區的（勾了「跨時區同步」）換成本機時間，不帶的就是本機的牆上時間</summary>
    internal static DateTime? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        s = s.Trim();
        bool hasOffset = s.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                         || (s.Length > 6 && (s[^6] == '+' || s[^6] == '-') && s[^3] == ':');
        if (hasOffset)
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) ? dto.LocalDateTime : null;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? DateTime.SpecifyKind(dt, DateTimeKind.Local) : null;
    }

    /// <summary>ISO 8601 的期間（PT15M、P1D、PT0S）；讀不懂是 null</summary>
    internal static TimeSpan? ParseDuration(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        try
        {
            return XmlConvert.ToTimeSpan(s.Trim());
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
