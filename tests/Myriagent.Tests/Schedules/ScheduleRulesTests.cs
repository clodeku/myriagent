using Myriagent.Schedules;
using FluentAssertions;

namespace Myriagent.Tests.Schedules;

/// <summary>排程的共用規則：XML 定義的解析、觸發條件的中文摘要與展開、結果碼、分類（以 2026-10-26 這台電腦的實際工作當語料）、遮蔽秘密、執行紀錄與失敗</summary>
public sealed class ScheduleRulesTests
{
    /// <summary>這台電腦的 \TkReportSync（代理以 Register-ScheduledTask 建立，2026-10-26 讀出）</summary>
    internal const string TkReportSyncXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <URI>\TkReportSync</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-21-1111111111-2222222222-3333333333-1000</UserId>
              <LogonType>InteractiveToken</LogonType>
            </Principal>
          </Principals>
          <Settings>
            <DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT3H</ExecutionTimeLimit>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <StartWhenAvailable>true</StartWhenAvailable>
            <IdleSettings>
              <Duration>PT10M</Duration>
              <WaitTimeout>PT1H</WaitTimeout>
              <StopOnIdleEnd>true</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
          </Settings>
          <Triggers>
            <CalendarTrigger>
              <StartBoundary>2026-09-24T00:20:00</StartBoundary>
              <Repetition>
                <Interval>PT1H</Interval>
                <Duration>P1D</Duration>
                <StopAtDurationEnd>true</StopAtDurationEnd>
              </Repetition>
              <ScheduleByDay>
                <DaysInterval>1</DaysInterval>
              </ScheduleByDay>
            </CalendarTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>C:\Python314\pythonw.exe</Command>
              <Arguments>"C:\repo\sample-project\tools\notes-sync\run_report_sync.py"</Arguments>
              <WorkingDirectory>C:\repo\sample-project</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    private const string MixedXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Date>2026-07-30T02:27:49</Date>
            <Author>DEMO-PC-3\User</Author>
            <Description>每週報表</Description>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>DEMO-PC-3\User</UserId>
              <LogonType>Password</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <Enabled>false</Enabled>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
          </Settings>
          <Triggers>
            <CalendarTrigger>
              <StartBoundary>2026-10-05T08:00:00+08:00</StartBoundary>
              <ScheduleByWeek>
                <DaysOfWeek><Monday /><Wednesday /><Friday /></DaysOfWeek>
                <WeeksInterval>1</WeeksInterval>
              </ScheduleByWeek>
            </CalendarTrigger>
            <CalendarTrigger>
              <StartBoundary>2026-01-01T03:00:00</StartBoundary>
              <ScheduleByMonth>
                <DaysOfMonth><Day>1</Day><Day>Last</Day></DaysOfMonth>
                <Months><January /><April /><July /><October /></Months>
              </ScheduleByMonth>
            </CalendarTrigger>
            <LogonTrigger>
              <Enabled>false</Enabled>
              <Delay>PT5M</Delay>
            </LogonTrigger>
            <BootTrigger />
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>powershell.exe</Command>
              <Arguments>-NoProfile -File "C:\repo\SampleShop\tools\report.ps1" -Token abcd1234efgh</Arguments>
            </Exec>
            <ComHandler><ClassId>{0}</ClassId></ComHandler>
          </Actions>
        </Task>
        """;

    private static readonly ScheduleEnvironment Env = new("User", "DEMO-PC-3", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ProgramFiles"] = @"C:\Program Files",
        ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
        ["ProgramW6432"] = @"C:\Program Files",
        ["SystemRoot"] = @"C:\Windows",
        ["windir"] = @"C:\Windows",
        ["ProgramData"] = @"C:\ProgramData",
        ["APPDATA"] = @"C:\Users\User\AppData\Roaming",
        ["LOCALAPPDATA"] = @"C:\Users\User\AppData\Local",
        ["USERPROFILE"] = @"C:\Users\User",
    });

    [Fact]
    public void Parse_ReadsTriggersActionsPrincipalAndSettings()
    {
        var d = ScheduleXmlRules.Parse(TkReportSyncXml);
        d.Author.Should().BeNull();
        d.Triggers.Should().ContainSingle();
        var t = d.Triggers[0];
        t.Kind.Should().Be(ScheduleTriggerKind.Daily);
        t.Start.Should().Be(new DateTime(2026, 9, 24, 0, 20, 0));
        t.RepeatInterval.Should().Be(TimeSpan.FromHours(1));
        t.RepeatDuration.Should().Be(TimeSpan.FromDays(1));
        d.Actions.Should().ContainSingle().Which.Should().Be(new ScheduleAction("Exec", @"C:\Python314\pythonw.exe",
            "\"C:\\repo\\sample-project\\tools\\notes-sync\\run_report_sync.py\"", @"C:\repo\sample-project"));
        d.LogonType.Should().Be("InteractiveToken");
        d.DisallowStartIfOnBatteries.Should().BeTrue();
        d.StartWhenAvailable.Should().BeTrue();
        d.TimeLimit.Should().Be(TimeSpan.FromHours(3));
        d.Enabled.Should().BeTrue();

        var m = ScheduleXmlRules.Parse(MixedXml);
        m.Author.Should().Be(@"DEMO-PC-3\User");
        m.Created.Should().Be(new DateTime(2026, 7, 30, 2, 27, 49));
        m.Enabled.Should().BeFalse();
        m.Elevated.Should().BeTrue();
        m.LogonType.Should().Be("Password");
        m.TimeLimit.Should().BeNull("PT0S＝不限");
        m.Triggers.Select(x => x.Kind).Should().Equal(ScheduleTriggerKind.Weekly, ScheduleTriggerKind.Monthly, ScheduleTriggerKind.Logon, ScheduleTriggerKind.Boot);
        m.Triggers[0].DaysOfWeek.Should().Equal(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday);
        m.Triggers[1].DaysOfMonth.Should().Equal(1);
        m.Triggers[1].LastDayOfMonth.Should().BeTrue();
        m.Triggers[1].Months.Should().Equal(1, 4, 7, 10);
        m.Triggers[2].Enabled.Should().BeFalse();
        m.Triggers[2].Delay.Should().Be(TimeSpan.FromMinutes(5));
        m.Actions.Select(a => a.Kind).Should().Equal("Exec", "ComHandler");
    }

    [Fact]
    public void Parse_TimeWithOffsetIsConvertedToLocal()
    {
        var utc = new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
        ScheduleXmlRules.ParseTime("2026-10-05T00:00:00Z").Should().Be(utc.LocalDateTime);
        ScheduleXmlRules.ParseTime("2026-10-05T08:00:00+08:00").Should().Be(utc.LocalDateTime);
        ScheduleXmlRules.ParseTime("2026-10-05T08:00:00").Should().Be(new DateTime(2026, 10, 5, 8, 0, 0));
        ScheduleXmlRules.ParseTime("亂寫").Should().BeNull();
        ScheduleXmlRules.ParseDuration("PT15M").Should().Be(TimeSpan.FromMinutes(15));
        ScheduleXmlRules.ParseDuration("P1DT2H").Should().Be(TimeSpan.FromHours(26));
        ScheduleXmlRules.ParseDuration("xx").Should().BeNull();
    }

    [Fact]
    public void Describe_ReadsLikeChinese()
    {
        ScheduleTriggerRules.Describe(ScheduleXmlRules.Parse(TkReportSyncXml).Triggers).Should().Be("每小時（00:20 起）");
        var m = ScheduleXmlRules.Parse(MixedXml).Triggers;
        ScheduleTriggerRules.Describe(m[0]).Should().Be("每週一、三、五 08:00");
        ScheduleTriggerRules.Describe(m[1]).Should().Be("每年 1、4、7、10 月的 1 日與最後一天 03:00");
        ScheduleTriggerRules.Describe(m[2]).Should().Be("任何人登入時（延遲 5 分）（已停用）");
        ScheduleTriggerRules.Describe(m[3]).Should().Be("開機時");
        ScheduleTriggerRules.Describe(Array.Empty<ScheduleTrigger>()).Should().Be("只能手動執行");

        var start = new DateTime(2026, 10, 1, 2, 30, 0);
        ScheduleTriggerRules.Describe(new ScheduleTrigger { Kind = ScheduleTriggerKind.Daily, Start = start }).Should().Be("每天 02:30");
        ScheduleTriggerRules.Describe(new ScheduleTrigger { Kind = ScheduleTriggerKind.Daily, Start = start, DaysInterval = 3 }).Should().Be("每 3 天 02:30");
        ScheduleTriggerRules.Describe(new ScheduleTrigger { Kind = ScheduleTriggerKind.Daily, Start = start, RepeatInterval = TimeSpan.FromMinutes(15) })
            .Should().Be("每 15 分鐘（02:30 起）", "無限期重複");
        ScheduleTriggerRules.Describe(new ScheduleTrigger
        {
            Kind = ScheduleTriggerKind.Daily, Start = start.Date.AddHours(8), RepeatInterval = TimeSpan.FromMinutes(15), RepeatDuration = TimeSpan.FromHours(10),
        }).Should().Be("每天 08:00 起 每 15 分鐘，持續 10 小時");
        ScheduleTriggerRules.Describe(new ScheduleTrigger
        {
            Kind = ScheduleTriggerKind.Weekly, Start = start,
            DaysOfWeek = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
        }).Should().Be("平日 02:30");
        ScheduleTriggerRules.Describe(new ScheduleTrigger
        {
            Kind = ScheduleTriggerKind.Weekly, Start = start, WeeksInterval = 2, DaysOfWeek = new[] { DayOfWeek.Sunday, DayOfWeek.Saturday },
        }).Should().Be("每 2 週的週末 02:30");
        ScheduleTriggerRules.Describe(new ScheduleTrigger
        {
            Kind = ScheduleTriggerKind.MonthlyDayOfWeek, Start = start, WeeksOfMonth = new[] { 1 }, LastWeekOfMonth = true, DaysOfWeek = new[] { DayOfWeek.Friday },
        }).Should().Be("每月第一個與最後一個週五 02:30");
        ScheduleTriggerRules.Describe(new ScheduleTrigger { Kind = ScheduleTriggerKind.Once, Start = start }).Should().Be("一次：2026/10/1 02:30");
        ScheduleTriggerRules.Describe(new ScheduleTrigger { Kind = ScheduleTriggerKind.SessionStateChange, StateChange = "SessionUnlock" }).Should().Be("解除鎖定時");
        ScheduleTriggerRules.Describe(new ScheduleTrigger { Kind = ScheduleTriggerKind.Logon, UserId = @"DEMO-PC-3\User" }).Should().Be("User 登入時");
    }

    [Fact]
    public void Occurrences_ExpandsCalendarTriggersWithRepetition()
    {
        var hourly = ScheduleXmlRules.Parse(TkReportSyncXml).Triggers;
        var from = new DateTime(2026, 10, 26, 5, 30, 0);
        var runs = ScheduleTriggerRules.Occurrences(hourly, from, from.AddHours(24));
        runs.Should().HaveCount(24);
        runs[0].Should().Be(new DateTime(2026, 10, 26, 6, 20, 0));
        runs[^1].Should().Be(new DateTime(2026, 10, 27, 5, 20, 0));

        var weekly = ScheduleXmlRules.Parse(MixedXml).Triggers;
        // 2026-10-26 是週一；週三、週五各一次，季初與季末的 3 點各一次（10/31 是 10 月最後一天）
        var week = ScheduleTriggerRules.Occurrences(weekly, new DateTime(2026, 10, 26), new DateTime(2026, 11, 2));
        week.Should().Equal(
            new DateTime(2026, 10, 26, 8, 0, 0).Add(TimeZoneOffsetFix(new DateTime(2026, 10, 26, 8, 0, 0))),
            new DateTime(2026, 10, 28, 8, 0, 0).Add(TimeZoneOffsetFix(new DateTime(2026, 10, 28, 8, 0, 0))),
            new DateTime(2026, 10, 30, 8, 0, 0).Add(TimeZoneOffsetFix(new DateTime(2026, 10, 30, 8, 0, 0))),
            new DateTime(2026, 10, 31, 3, 0, 0));
    }

    /// <summary>MixedXml 的週觸發帶 +08:00：測試機不在臺灣時區時換成本機的時間</summary>
    private static TimeSpan TimeZoneOffsetFix(DateTime taipeiWallClock)
    {
        var local = new DateTimeOffset(taipeiWallClock, TimeSpan.FromHours(8)).LocalDateTime;
        return local - taipeiWallClock;
    }

    [Fact]
    public void Occurrences_HonoursIntervalsEndsAndDisabledTriggers()
    {
        var start = new DateTime(2026, 10, 1, 2, 30, 0);
        var every3 = new ScheduleTrigger { Kind = ScheduleTriggerKind.Daily, Start = start, DaysInterval = 3 };
        ScheduleTriggerRules.Occurrences(new[] { every3 }, new DateTime(2026, 10, 1), new DateTime(2026, 10, 11))
            .Select(d => d.Day).Should().Equal(1, 4, 7, 10);
        var ended = new ScheduleTrigger { Kind = ScheduleTriggerKind.Daily, Start = start, End = new DateTime(2026, 10, 2, 12, 0, 0) };
        ScheduleTriggerRules.Occurrences(new[] { ended }, new DateTime(2026, 10, 1), new DateTime(2026, 10, 5)).Should().HaveCount(2);
        var disabled = new ScheduleTrigger { Kind = ScheduleTriggerKind.Daily, Start = start, Enabled = false };
        ScheduleTriggerRules.Occurrences(new[] { disabled }, new DateTime(2026, 10, 1), new DateTime(2026, 10, 5)).Should().BeEmpty();
        var logon = new ScheduleTrigger { Kind = ScheduleTriggerKind.Logon };
        ScheduleTriggerRules.Occurrences(new[] { logon }, new DateTime(2026, 10, 1), new DateTime(2026, 10, 5)).Should().BeEmpty();

        // 一次的觸發無限期每 15 分鐘：從很久以前開始，照樣算出對齊的時間
        var forever = new ScheduleTrigger { Kind = ScheduleTriggerKind.Once, Start = new DateTime(2026, 1, 1, 0, 5, 0), RepeatInterval = TimeSpan.FromMinutes(15) };
        ScheduleTriggerRules.Occurrences(new[] { forever }, new DateTime(2026, 10, 26, 10, 0, 0), new DateTime(2026, 10, 26, 11, 0, 0))
            .Select(d => d.Minute).Should().Equal(5, 20, 35, 50);

        // 每天無限期每 15 分鐘：前一天開始的那串延續過午夜
        var daily = new ScheduleTrigger { Kind = ScheduleTriggerKind.Daily, Start = new DateTime(2026, 10, 1, 23, 0, 0), RepeatInterval = TimeSpan.FromMinutes(15) };
        ScheduleTriggerRules.Occurrences(new[] { daily }, new DateTime(2026, 10, 26, 0, 0, 0), new DateTime(2026, 10, 26, 1, 0, 0)).Should().HaveCount(4);

        // 每分鐘一次：最多展開 MaxOccurrences 次
        var minutely = new ScheduleTrigger { Kind = ScheduleTriggerKind.Once, Start = new DateTime(2026, 1, 1), RepeatInterval = TimeSpan.FromMinutes(1) };
        ScheduleTriggerRules.Occurrences(new[] { minutely }, new DateTime(2026, 10, 26), new DateTime(2026, 10, 27)).Should().HaveCount(ScheduleTriggerRules.MaxOccurrences);

        // 每月第一個週一（2026-11-02）與最後一個週五（2026-11-27）
        var dow = new ScheduleTrigger
        {
            Kind = ScheduleTriggerKind.MonthlyDayOfWeek, Start = start, WeeksOfMonth = new[] { 1 }, DaysOfWeek = new[] { DayOfWeek.Monday },
        };
        var lastFri = new ScheduleTrigger
        {
            Kind = ScheduleTriggerKind.MonthlyDayOfWeek, Start = start, LastWeekOfMonth = true, DaysOfWeek = new[] { DayOfWeek.Friday },
        };
        ScheduleTriggerRules.Occurrences(new[] { dow, lastFri }, new DateTime(2026, 11, 1), new DateTime(2026, 12, 1)).Select(d => d.Day).Should().Equal(2, 27);
    }

    [Theory]
    [InlineData(0, "ok", "成功")]
    [InlineData(1, "fail", "結束碼 1")]
    [InlineData(0x41301, "info", "執行中")]
    [InlineData(0x41303, "info", "還沒執行過")]
    [InlineData(0x41306, "warn", "被停止")]
    [InlineData(unchecked((int)0x800710E0), "warn", "條件不符，這次沒有執行（例如使用電池、使用者沒登入）")]
    [InlineData(unchecked((int)0x80070002), "fail", "找不到要執行的檔案")]
    [InlineData(unchecked((int)0xC000013A), "warn", "被中斷（Ctrl+C 或關機）")]
    [InlineData(unchecked((int)0x8004FFFF), "fail", "結束碼 0x8004FFFF")]
    [InlineData(-1, "fail", "結束碼 0xFFFFFFFF")]
    public void Result_IsClassified(int code, string level, string text)
    {
        var r = ScheduleResultRules.Classify(code);
        r.LevelName.Should().Be(level);
        r.Text.Should().Be(text);
    }

    [Fact]
    public void Result_RunningAndNotYetRunAreNotFinal()
    {
        ScheduleResultRules.IsFinal(0x41301).Should().BeFalse();
        ScheduleResultRules.IsFinal(0x41303).Should().BeFalse();
        ScheduleResultRules.IsFinal(0).Should().BeTrue();
        ScheduleResultRules.IsFinal(2).Should().BeTrue();
        ScheduleResultRules.IsFinal(0x41306).Should().BeTrue();
    }

    /// <summary>一台典型電腦上 \Microsoft\ 以外的工作（作者、指令、參數、工作資料夾；廠商與程式名稱是虛構的）與該有的分類</summary>
    public static TheoryData<string, string?, string?, string?, string?, bool, string> Corpus => new()
    {
        { @"\VendorInstallLauncher", "Fabrikam, Inc.", @"C:\Program Files\Fabrikam\Bin64\InstallManager.exe", "/InstallAUEP", null, false, "Other" },
        { @"\VendorMetricsTask", "Fabrikam, Inc.", "\"C:\\Program Files\\Fabrikam\\Metrics\\metricsserver.exe\"", null, null, false, "Other" },
        { @"\CreateExplorerShellUnelevatedTask", "ExplorerShellUnelevated", @"C:\Windows\explorer.exe", "/NoUACCheck", null, false, "Other" },
        {
            @"\FileMirrorSync", @"DEMO-PC-3\User", @"C:\Python314\pythonw.exe",
            "\"C:\\repo\\sample-project\\tools\\filemirror\\run_file_mirror.py\" --root \"C:\\repo\\sample-project\"", @"C:\repo\sample-project", false,
            "Suspected"
        },
        { @"\TurboTool", null, @"%TurboTool%\TurboTool.exe", "/min /autostart", null, false, "Other" },
        { @"\MouseTool_DEMO-PC-3_User", null, @"C:\Program Files\MouseTool\MouseTool.Ui.exe", null, @"C:\Program Files\MouseTool\", false, "Other" },
        {
            @"\GpuDriverUpdateCheckDaily_{00000000-0000-0000-0000-000000000001}", "Northwind Graphics", "\"C:\\Program Files\\Northwind\\Container\\container.exe\"",
            "-d \"C:\\Program Files\\Northwind\\App\\DriverUpdateCheck\" -l 3", @"C:\Program Files\Northwind\Container", false, "Other"
        },
        { @"\OneDrive Reporting Task-S-1-5-21-1", "Microsoft Corporation", @"C:\Program Files\Microsoft OneDrive\OneDriveStandaloneUpdater.exe", "/reporting", null, false, "Other" },
        { @"\SsdTool", "SsdTool", "\"C:\\Program Files (x86)\\Contoso\\SsdTool\\SsdTool.exe\"", "/AUTOHIDE", null, false, "Other" },
        {
            @"\TkNotesSync", @"DEMO-PC-3\User", @"C:\Python314\pythonw.exe",
            "\"C:\\repo\\sample-project\\tools\\notes-sync\\run_notes_sync.py\" --root \"C:\\repo\\sample-project\"", @"C:\repo\sample-project", false,
            "Suspected"
        },
        {
            @"\TkReportSync", null, @"C:\Python314\pythonw.exe", "\"C:\\repo\\sample-project\\tools\\notes-sync\\run_report_sync.py\"", @"C:\repo\sample-project", false,
            "Suspected"
        },
        { @"\ChatAppUpdateTaskUser-S-1-5-21-1", "Contoso Communications, Inc.", "\"C:\\Users\\User\\AppData\\Roaming\\ChatApp\\bin\\ChatApp.exe\"", "--action=UpdateSchedule", null, false, "Other" },
        { @"\Litware\DriverHub", "Litware", @"C:\Program Files\Litware\DriverHub\DriverHub.exe", null, null, false, "Other" },
        {
            @"\GoogleUser\GoogleUpdater\GoogleUpdaterTaskUser157", @"DEMO-PC-3\User", "\"C:\\Users\\User\\AppData\\Local\\Google\\GoogleUpdater\\150.0.0.0\\updater.exe\"", "--wake", null, false,
            "Other"
        },
        {
            @"\GoogleUserPEH\RunPlatformExperienceHelper_Daily", @"DEMO-PC-3\User",
            "\"C:\\Program Files\\Google\\Chrome\\Application\\PlatformExperienceHelper\\platform_experience_helper.exe\"", null, null, false, "Other"
        },
        { @"\SoftLanding\S-1-5-21-1\SoftLandingCreativeManagementTask", null, null, null, null, false, "Other" },
        // 不是這台實際有的：在 git repo 裡的 exe、cmd 包著的一行指令、AppData 裡的腳本但在 repo 裡
        { @"\Tool", "SomeVendor", @"D:\work\tool\bin\tool.exe", null, null, true, "Suspected" },
        { @"\Inline", @"DEMO-PC-3\User", "cmd.exe", "/c echo hi > nul", null, false, "Suspected" },
        { @"\VendorCmd", "Contoso", "cmd.exe", "/c echo hi", null, false, "Other" },
        { @"\VendorScript", null, "powershell.exe", "-File \"C:\\Program Files\\Contoso\\update.ps1\"", null, false, "Other" },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Classify_MatchesThisMachine(string path, string? author, string? command, string? arguments, string? workdir, bool inRepo, string expected)
    {
        var actions = command == null
            ? new[] { new ScheduleAction("ComHandler", null, null, null) }
            : new[] { new ScheduleAction("Exec", command, arguments, workdir) };
        ScheduleClassifyRules.Classify(new ScheduleClassifyInput(path, author, actions, inRepo, Env)).ToString().Should().Be(expected);
    }

    [Fact]
    public void Classify_MyriagentWorkspaceUnderAppDataIsNotVendor()
    {
        var actions = new[] { new ScheduleAction("Exec", "powershell.exe", "-File \"C:\\Users\\User\\AppData\\Local\\Myriagent\\conversations\\abc\\workspace\\job.ps1\"", null) };
        ScheduleClassifyRules.Classify(new ScheduleClassifyInput(@"\Job", null, actions, false, Env)).Should().Be(ScheduleOrigin.Other, "沒有例外時算 AppData");
        var env = Env with { WorkRoots = new[] { @"C:\Users\User\AppData\Local\Myriagent\conversations" } };
        ScheduleClassifyRules.Classify(new ScheduleClassifyInput(@"\Job", null, actions, false, env)).Should().Be(ScheduleOrigin.Suspected);
    }

    [Fact]
    public void WorkFolder_AndCandidatePaths()
    {
        var filemirror = new[]
        {
            new ScheduleAction("Exec", @"C:\Python314\pythonw.exe",
                "\"C:\\repo\\sample-project\\tools\\filemirror\\run_file_mirror.py\" --root \"C:\\repo\\sample-project\"", null),
        };
        ScheduleClassifyRules.WorkFolder(filemirror, Env).Should().Be(@"C:\repo\sample-project\tools\filemirror");
        ScheduleClassifyRules.CandidatePaths(filemirror, Env).Should().Equal(
            @"C:\Python314\pythonw.exe", @"C:\repo\sample-project\tools\filemirror\run_file_mirror.py", @"C:\repo\sample-project");
        var withDir = new[] { new ScheduleAction("Exec", "cmd.exe", "/c run.bat", @"D:\jobs\nightly\") };
        ScheduleClassifyRules.WorkFolder(withDir, Env).Should().Be(@"D:\jobs\nightly");
        ScheduleClassifyRules.CandidatePaths(new[] { new ScheduleAction("Exec", @"\\server\share\x.exe", null, null) }, Env).Should().BeEmpty("UNC 一律不碰");
        ScheduleClassifyRules.IsSystemFolder(@"\Microsoft\Windows\Defrag").Should().BeTrue();
        ScheduleClassifyRules.IsSystemFolder(@"\MicrosoftEdgeUpdate").Should().BeFalse();
        ScheduleClassifyRules.ActionText(filemirror).Should().StartWith(@"C:\Python314\pythonw.exe ""C:\repo");
    }

    [Theory]
    [InlineData("schtasks /Create /TN \"TkReportSync\" /TR \"pythonw.exe run.py\" /SC HOURLY", "Create:TkReportSync")]
    [InlineData("MSYS_NO_PATHCONV=1 schtasks /create /tn FileMirrorSync /tr x /sc minute /mo 15 /f", "Create:FileMirrorSync")]
    [InlineData("schtasks //create //tn \"My Job\" //sc daily", "Create:My Job")]
    [InlineData("schtasks.exe -Delete -TN \\Myriagent\\Nightly -F", "Delete:\\Myriagent\\Nightly")]
    [InlineData("schtasks /change /tn TkNotesSync /disable", "Disable:TkNotesSync")]
    [InlineData("schtasks /Change /TN TkNotesSync /ENABLE", "Enable:TkNotesSync")]
    [InlineData("schtasks /change /tn TkNotesSync /st 03:00", "Change:TkNotesSync")]
    [InlineData("schtasks /run /tn TkNotesSync", "Run:TkNotesSync")]
    [InlineData("schtasks /query /tn TkNotesSync /v /fo LIST", "")]
    [InlineData("cmd /c \"schtasks /create /tn Backup /xml backup.xml\"", "Create:Backup")]
    [InlineData("subprocess.run([\"schtasks\", \"/create\", \"/tn\", \"PyJob\", \"/sc\", \"daily\"])", "Create:PyJob")]
    [InlineData("Register-ScheduledTask -TaskName 'TkReportSync' -Action $a -Trigger $t -Settings $s", "Create:TkReportSync")]
    [InlineData("Register-ScheduledTask -Action $a -Trigger $t -TaskName \"Later\" -Force", "Create:Later")]
    [InlineData("Register-ScheduledTask Nightly -InputObject $task", "Create:Nightly")]
    [InlineData("Register-ScheduledTask -TaskName Job -TaskPath '\\Myriagent\\' -Xml (Get-Content job.xml | Out-String)", "Create:\\Myriagent\\Job")]
    [InlineData("Register-ScheduledTask -TaskName $name -Action $a", "Create:")]
    [InlineData("$TaskName = 'TkReportSync'\nRegister-ScheduledTask -TaskName $TaskName -Action $a -Force", "Create:TkReportSync")]
    [InlineData("param([string]$Name = \"Nightly Job\")\nRegister-ScheduledTask -TaskName ${Name} -Action $a", "Create:Nightly Job")]
    [InlineData("set TN=CmdJob\r\nschtasks /create /tn %TN% /sc daily /tr x.cmd", "Create:CmdJob")]
    [InlineData("NAME=ShJob; schtasks //create //tn \"$NAME\" //sc daily", "Create:ShJob")]
    [InlineData("git commit -m \"schtasks /create /tn tools/filemirror\"", "Create:")]
    [InlineData("文件提到 Set-ScheduledTask ExecutionTimeLimit、首次啟動、port 的設定", "Change:")]
    [InlineData("Register-ScheduledTask -TaskName \"GZ-$Zone\" -Action $a", "Create:")]
    [InlineData("Register-ScheduledTask @params", "Create:")]
    [InlineData("Unregister-ScheduledTask -TaskName Old -Confirm:$false", "Delete:Old")]
    [InlineData("Get-ScheduledTask -TaskName TkReportSync | Disable-ScheduledTask", "Disable:TkReportSync")]
    [InlineData("Enable-ScheduledTask TkReportSync", "Enable:TkReportSync")]
    [InlineData("Set-ScheduledTask -TaskName TkReportSync -Trigger $t", "Change:TkReportSync")]
    [InlineData("Start-ScheduledTask -TaskName TkReportSync", "Run:TkReportSync")]
    [InlineData("Get-ScheduledTask | Where-Object TaskName -like 'Tk*'", "")]
    [InlineData("$svc = New-Object -ComObject Schedule.Service; $folder.RegisterTaskDefinition('X', $def, 6, $null, $null, 3)", "Unknown:")]
    [InlineData("Register-ScheduledTask `\n  -TaskName \"Continued\" `\n  -Action $a", "Create:Continued")]
    [InlineData("echo 'schtasks is a tool'", "")]
    [InlineData("git log --grep=ScheduledTask", "")]
    public void Origin_ParsesCommands(string text, string expected)
    {
        var hits = ScheduleOriginRules.Parse(text);
        var got = string.Join(";", hits.Select(h => $"{h.Verb}:{h.Name}"));
        got.Should().Be(expected);
    }

    [Fact]
    public void Origin_SeveralStatementsAndNameMatching()
    {
        var hits = ScheduleOriginRules.Parse("schtasks /delete /tn A /f && schtasks /create /tn A /xml a.xml; Start-ScheduledTask -TaskName A");
        hits.Select(h => h.Verb).Should().Equal(ScheduleVerb.Delete, ScheduleVerb.Create, ScheduleVerb.Run);
        ScheduleOriginRules.NameMatches("A", @"\A").Should().BeTrue();
        ScheduleOriginRules.NameMatches("a", @"\Folder\A").Should().BeTrue();
        ScheduleOriginRules.NameMatches(@"\Folder\A", @"\Folder\A").Should().BeTrue();
        ScheduleOriginRules.NameMatches(@"Folder\A", @"\Folder\A").Should().BeTrue();
        ScheduleOriginRules.NameMatches(@"\Other\A", @"\Folder\A").Should().BeFalse();
        ScheduleOriginRules.NameMatches(null, @"\A").Should().BeFalse();
        ScheduleOriginRules.Mutates(ScheduleVerb.Run).Should().BeFalse();
        ScheduleOriginRules.Mutates(ScheduleVerb.Unknown).Should().BeTrue();
        ScheduleOriginRules.MayTouch("ls -la").Should().BeFalse();
    }

    [Theory]
    [InlineData("--token abc123", "--token ••••")]
    [InlineData("--api-key=sk-live-xyz", "--api-key=••••")]
    [InlineData("-Password \"p@ss word\"", "-Password ••••")]
    [InlineData("/pwd:hunter2", "/pwd:••••")]
    [InlineData("API_KEY=abcdef run.py", "API_KEY=•••• run.py")]
    [InlineData("https://user:secret@example.com/x", "https://user:••••@example.com/x")]
    [InlineData("--header ghp_abcdefghijklmnopqrstuvwxyz0123", "--header ••••")]
    [InlineData("run.py 9f8e7d6c5b4a39281706f5e4d3c2b1a0ffeeddcc", "run.py ••••")]
    [InlineData("\"C:\\repo\\sample-project\\tools\\filemirror\\run_file_mirror.py\" --root \"C:\\repo\\sample-project\"",
        "\"C:\\repo\\sample-project\\tools\\filemirror\\run_file_mirror.py\" --root \"C:\\repo\\sample-project\"")]
    [InlineData("--days 30 --mode full", "--days 30 --mode full")]
    public void Redact_HidesSecrets(string text, string expected)
        => ScheduleRedactRules.Redact(text).Should().Be(expected);

    [Fact]
    public void Runs_ObserveOnlyNewFinalResultsAndMarkGaps()
    {
        var hourly = ScheduleXmlRules.Parse(TkReportSyncXml).Triggers;
        var now = new DateTime(2026, 10, 26, 10, 0, 0);
        ScheduleRunRules.Observe("\\T", null, null, 0, hourly, now).Should().BeNull("沒有執行過");
        ScheduleRunRules.Observe("\\T", null, new DateTime(1899, 12, 30), 0x41303, hourly, now).Should().BeNull();
        ScheduleRunRules.Observe("\\T", null, new DateTime(2026, 10, 26, 9, 20, 1), 0x41301, hourly, now).Should().BeNull("還在執行");
        var first = ScheduleRunRules.Observe("\\T", null, new DateTime(2026, 10, 26, 9, 20, 1, 400), 1, hourly, now)!;
        first.At.Should().Be(new DateTime(2026, 10, 26, 9, 20, 1), "只到秒");
        first.Gap.Should().BeFalse();
        ScheduleRunRules.Observe("\\T", first, new DateTime(2026, 10, 26, 9, 20, 1, 900), 1, hourly, now).Should().BeNull("同一次");
        var next = ScheduleRunRules.Observe("\\T", first, new DateTime(2026, 10, 26, 10, 20, 0), 0, hourly, now)!;
        next.Gap.Should().BeFalse("中間沒有別的預定執行");
        var skipped = ScheduleRunRules.Observe("\\T", first, new DateTime(2026, 10, 26, 13, 20, 0), 0, hourly, now)!;
        skipped.Gap.Should().BeTrue("10:20、11:20、12:20 沒觀察到");
    }

    [Fact]
    public void Runs_StreakAndAlerts()
    {
        var now = new DateTime(2026, 10, 26, 10, 0, 0);
        ScheduleRunRecord R(int hour, int code) => new("\\T", new DateTime(2026, 10, 26, hour, 0, 0), code, now, false);
        ScheduleRunRules.ConsecutiveFailures(new[] { R(1, 0), R(2, 1), R(3, 2) }).Should().Be(2);
        ScheduleRunRules.ConsecutiveFailures(new[] { R(1, 1), R(2, 0) }).Should().Be(0);
        ScheduleRunRules.ConsecutiveFailures(new[] { R(1, 1), R(2, 0x41306) }).Should().Be(0, "被停止不算失敗");
        ScheduleRunRules.ShouldAlert(R(9, 1), R(8, 0), now).Should().BeTrue();
        ScheduleRunRules.ShouldAlert(R(9, 1), null, now).Should().BeTrue();
        ScheduleRunRules.ShouldAlert(R(9, 1), R(8, 1), now).Should().BeFalse("同一串失敗只通知一次");
        ScheduleRunRules.ShouldAlert(R(9, 0), R(8, 1), now).Should().BeFalse();
        ScheduleRunRules.ShouldAlert(new ScheduleRunRecord("\\T", now.AddHours(-13), 1, now, false), null, now).Should().BeFalse("太久以前的");
    }

    [Fact]
    public void Diagnose_Limits()
    {
        var now = new DateTime(2026, 10, 26, 10, 0, 0);
        var none = Array.Empty<ScheduleDiagnoseRecord>();
        ScheduleRunRules.DiagnoseBlock("\\T", ScheduleOrigin.Agent, true, false, none, now, true, 50).Should().BeNull();
        ScheduleRunRules.DiagnoseBlock("\\T", ScheduleOrigin.Adopted, true, false, none, now, true, null).Should().BeNull();
        ScheduleRunRules.DiagnoseBlock("\\T", ScheduleOrigin.Agent, false, false, none, now, true, 50).Should().Contain("設定頁");
        ScheduleRunRules.DiagnoseBlock("\\T", ScheduleOrigin.Agent, true, true, none, now, true, 50).Should().Contain("這個排程");
        ScheduleRunRules.DiagnoseBlock("\\T", ScheduleOrigin.Suspected, true, false, none, now, true, 50).Should().NotBeNull();
        ScheduleRunRules.DiagnoseBlock("\\T", ScheduleOrigin.Agent, true, false, new[] { new ScheduleDiagnoseRecord("\\t", now.AddHours(-23), null) }, now, true, 50)
            .Should().Contain("24 小時");
        ScheduleRunRules.DiagnoseBlock("\\T", ScheduleOrigin.Agent, true, false, new[] { new ScheduleDiagnoseRecord("\\T", now.AddHours(-25), null) }, now, true, 50)
            .Should().BeNull();
        var many = Enumerable.Range(0, 10).Select(i => new ScheduleDiagnoseRecord($"\\X{i}", now.Date.AddMinutes(i), null)).ToList();
        ScheduleRunRules.DiagnoseBlock("\\T", ScheduleOrigin.Agent, true, false, many, now, true, 50).Should().Contain("10 次");
        ScheduleRunRules.DiagnoseBlock("\\T", ScheduleOrigin.Agent, true, false, none, now, false, 50).Should().Contain("帳戶");
        ScheduleRunRules.DiagnoseBlock("\\T", ScheduleOrigin.Agent, true, false, none, now, true, 9.5).Should().Contain("額度");
        ScheduleRunRules.Prune(new[] { new ScheduleDiagnoseRecord("\\T", now.AddDays(-8), null), new ScheduleDiagnoseRecord("\\T", now.AddDays(-1), null) }, now)
            .Should().ContainSingle();
    }
}
