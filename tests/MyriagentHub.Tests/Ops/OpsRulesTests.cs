using System.Diagnostics;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Ops;

/// <summary>營運的純函式規則：備份排程與保留、記憶體保險絲、健康狀態與告警、管理收件匣</summary>
public sealed class OpsRulesTests
{
    [Fact]
    public void 備份時點_今天還沒到就今天_過了就明天()
    {
        var at = BackupRules.ParseDailyAt("03:30");
        var tz = TimeSpan.FromHours(8);
        BackupRules.NextRun(new DateTimeOffset(2026, 10, 11, 1, 0, 0, tz), at).Should().Be(new DateTimeOffset(2026, 10, 11, 3, 30, 0, tz));
        BackupRules.NextRun(new DateTimeOffset(2026, 10, 11, 3, 30, 0, tz), at).Should().Be(new DateTimeOffset(2026, 10, 12, 3, 30, 0, tz));
        BackupRules.NextRun(new DateTimeOffset(2026, 10, 11, 23, 59, 0, tz), at).Should().Be(new DateTimeOffset(2026, 10, 12, 3, 30, 0, tz));
        var bad = () => BackupRules.ParseDailyAt("3點半");
        bad.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void 備份資料夾命名()
    {
        var now = new DateTimeOffset(2026, 10, 11, 3, 30, 5, TimeSpan.FromHours(8));
        BackupRules.FolderName(now, scheduled: true).Should().Be("20261011");
        BackupRules.FolderName(now, scheduled: false).Should().Be("20261011-033005");
        BackupRules.ParseFolder("20261011").Should().Be(new DateTime(2026, 10, 11));
        BackupRules.ParseFolder("20261011-033005").Should().Be(new DateTime(2026, 10, 11, 3, 30, 5));
        BackupRules.ParseFolder("pre-restore-x").Should().BeNull();
    }

    [Fact]
    public void 保留14份每日加8份每週_看不懂的資料夾不動()
    {
        var start = new DateTime(2026, 1, 1);
        var names = Enumerable.Range(0, 120).Select(i => start.AddDays(i).ToString("yyyyMMdd")).Append("人工放的資料夾").ToList();
        var expired = BackupRules.SelectExpired(names, keepDaily: 14, keepWeekly: 8);
        var kept = names.Except(expired).ToList();
        kept.Should().Contain("人工放的資料夾");
        var keptDates = kept.Select(BackupRules.ParseFolder).OfType<DateTime>().OrderByDescending(d => d).ToList();
        keptDates.Take(14).Should().Equal(Enumerable.Range(0, 14).Select(i => start.AddDays(119 - i)));
        keptDates.Skip(14).Should().HaveCount(8, "更舊的每週留一份，最多 8 週");
        keptDates.Skip(14).Select(d => (System.Globalization.ISOWeek.GetYear(d), System.Globalization.ISOWeek.GetWeekOfYear(d))).Should().OnlyHaveUniqueItems();
        BackupRules.SelectExpired(names.Take(10), 14, 8).Should().BeEmpty("不到 14 份不刪");
    }

    [Fact]
    public void 備份預設留7份每日加4份每週_合計100GB_至少剩40GB()
    {
        var b = new BackupSettings();
        (b.KeepDaily, b.KeepWeekly, b.MaxTotalBytes, b.MinFreeBytes).Should().Be((7, 4, 100L << 30, 40L << 30));
        BackupRules.Validate(b).Should().BeNull();
        BackupRules.Validate(new BackupSettings { KeepDaily = 0 }).Should().Contain("Hub:Backup:KeepDaily");
        BackupRules.Validate(new BackupSettings { KeepWeekly = -1 }).Should().Contain("Hub:Backup:KeepWeekly");
        BackupRules.Validate(new BackupSettings { KeepWeekly = 0 }).Should().BeNull("不留每週的也可以");
        BackupRules.Validate(new BackupSettings { MaxTotalBytes = 0 }).Should().Contain("Hub:Backup:MaxTotalBytes");
        BackupRules.Validate(new BackupSettings { MinFreeBytes = -1 }).Should().Contain("Hub:Backup:MinFreeBytes");
        BackupRules.Validate(new BackupSettings { MinFreeBytes = 0 }).Should().BeNull();
    }

    [Fact]
    public void 備份前的空間_夠就不刪_不夠從最舊的刪_最新的一定留()
    {
        var folders = new[]
        {
            new BackupFolderSize("20261003", 30),
            new BackupFolderSize("20261001", 10),
            new BackupFolderSize("人工放的資料夾", 1000),
            new BackupFolderSize("20261002-120000", 20),
        };

        BackupRules.PlanSpace(folders, estimate: 50, freeBytes: 150, minFreeBytes: 100).Should().BeEquivalentTo(new BackupSpacePlan(true, []), "剛好夠");
        var one = BackupRules.PlanSpace(folders, estimate: 50, freeBytes: 145, minFreeBytes: 100);
        one.Fits.Should().BeTrue();
        one.Delete.Should().Equal("20261001");
        var two = BackupRules.PlanSpace(folders, estimate: 50, freeBytes: 125, minFreeBytes: 100);
        two.Fits.Should().BeTrue();
        two.Delete.Should().Equal(["20261001", "20261002-120000"], "從最舊的刪起");
        BackupRules.PlanSpace(folders, estimate: 50, freeBytes: 115, minFreeBytes: 100).Should().BeEquivalentTo(new BackupSpacePlan(false, []),
            "最新的 20261003 一定留、看不懂的資料夾不算：刪光了也不夠就一份都不刪");
        BackupRules.PlanSpace(folders, estimate: 0, freeBytes: -1, minFreeBytes: 0).Should().BeEquivalentTo(new BackupSpacePlan(false, []), "量不到可用空間當成不夠");
        BackupRules.PlanSpace([], estimate: 50, freeBytes: 100, minFreeBytes: 100).Should().BeEquivalentTo(new BackupSpacePlan(false, []));
        BackupRules.PlanSpace([], estimate: 0, freeBytes: long.MaxValue, minFreeBytes: long.MaxValue).Fits.Should().BeTrue("不溢位");
    }

    [Fact]
    public void 備份合計超過上限_從最舊的刪_最新的一定留()
    {
        var folders = new[]
        {
            new BackupFolderSize("20261001", 10),
            new BackupFolderSize("20261003", 30),
            new BackupFolderSize("20261002", 20),
            new BackupFolderSize("人工放的資料夾", 1000),
        };

        BackupRules.SelectOverBudget(folders, maxTotalBytes: 60).Should().BeEmpty("看不懂的資料夾不算");
        BackupRules.SelectOverBudget(folders, maxTotalBytes: 59).Should().Equal("20261001");
        BackupRules.SelectOverBudget(folders, maxTotalBytes: 40).Should().Equal("20261001", "20261002");
        BackupRules.SelectOverBudget(folders, maxTotalBytes: 1).Should().Equal(["20261001", "20261002"], "最新的一份比上限大也留著");
        BackupRules.SelectOverBudget([], maxTotalBytes: 1).Should().BeEmpty();
    }

    [Fact]
    public void 備份超過36小時沒成功算過期()
    {
        const long hour = 3_600_000;
        BackupRules.IsStale(lastSuccessAt: 0, now: 37 * hour, startedAt: 0).Should().BeTrue();
        BackupRules.IsStale(lastSuccessAt: 2 * hour, now: 37 * hour, startedAt: 0).Should().BeFalse();
        BackupRules.IsStale(lastSuccessAt: null, now: 10 * hour, startedAt: 0).Should().BeFalse("剛部署還沒到第一次排程不算");
        BackupRules.IsStale(lastSuccessAt: null, now: 40 * hour, startedAt: 0).Should().BeTrue();
    }

    [Fact]
    public void 記憶體保險絲_超過告警門檻告警_超過上限持續5分鐘才退出()
    {
        var settings = new MemoryFuseSettings();
        var (s1, a1) = MemoryFuseRules.Evaluate(FuseState.Initial, 300, 0, settings);
        a1.Should().Be(FuseAction.None);
        var (s2, a2) = MemoryFuseRules.Evaluate(s1, 450, 1_000, settings);
        a2.Should().Be(FuseAction.Warn);
        var (s3, a3) = MemoryFuseRules.Evaluate(s2, 800, 2_000, settings);
        a3.Should().Be(FuseAction.None);
        s3.OverKillSince.Should().Be(2_000);
        var (s4, a4) = MemoryFuseRules.Evaluate(s3, 800, 2_000 + 4 * 60_000, settings);
        a4.Should().Be(FuseAction.None, "還不到 5 分鐘");
        var (_, a5) = MemoryFuseRules.Evaluate(s4, 800, 2_000 + 5 * 60_000, settings);
        a5.Should().Be(FuseAction.Kill);
        var (s6, _) = MemoryFuseRules.Evaluate(s4, 500, 2_000 + 5 * 60_000, settings);
        s6.OverKillSince.Should().BeNull("降回上限以下就重新計時");
        var (_, a7) = MemoryFuseRules.Evaluate(s6, 200, 3_000_000, settings);
        a7.Should().Be(FuseAction.Recovered);
    }

    [Fact]
    public void 程序取樣_記憶體每次都是當下_CPU時間窗太短時沿用上一次()
    {
        var clock = new ManualTimeProvider();
        var metrics = new OpsMetrics(clock);
        var a = metrics.Sample();
        a.At.Should().Be(clock.NowMs);
        a.PrivateBytes.Should().BeGreaterThan(0);
        a.Threads.Should().BeGreaterThan(0);

        var busy = Stopwatch.StartNew();
        while (busy.Elapsed < TimeSpan.FromMilliseconds(250))
        {
        }
        var b = metrics.Sample();
        b.CpuWindowSeconds.Should().BeGreaterThanOrEqualTo(OpsMetrics.MinCpuWindow.TotalSeconds);
        b.CpuPercent.Should().BeGreaterThan(0);

        clock.Advance(TimeSpan.FromSeconds(1));
        var c = metrics.Sample();
        c.At.Should().Be(clock.NowMs, "時間與記憶體是當下的");
        c.CpuPercent.Should().Be(b.CpuPercent, "距上一次不到 200 ms：沿用上一次的平均，不回報雜訊");
        c.CpuWindowSeconds.Should().Be(b.CpuWindowSeconds);
        metrics.Last.Should().BeSameAs(c);
    }

    [Fact]
    public void 健康狀態與告警()
    {
        HealthRules.Status(new HealthInputs(false, false, false)).Should().Be("ok");
        HealthRules.Status(new HealthInputs(true, false, false)).Should().Be("degraded");
        HealthRules.Status(new HealthInputs(false, true, false)).Should().Be("degraded");
        HealthRules.Status(new HealthInputs(false, false, true)).Should().Be("degraded");

        var quiet = new AlertInputs("none", false, true, false, true, 100L * 1024 * 1024 * 1024, 100, 400L * 1024 * 1024, false, false, 0, 20);
        HealthRules.Alerts(quiet).Should().BeEmpty();
        HealthRules.Alerts(quiet with { CertificateExpiry = "critical" }).Should().Contain("certificate_expiring");
        HealthRules.Alerts(quiet with { CertificateExpiry = "warning" }).Should().Contain("certificate_expiring_soon");
        HealthRules.Alerts(quiet with { CertificateSelfSigned = true }).Should().Contain("certificate_self_signed");
        HealthRules.Alerts(quiet with { CertificateSelfSigned = true, AcmeEnabled = false }).Should().BeEmpty("沒開 ACME 的開發環境用自簽是正常的");
        HealthRules.Alerts(quiet with { BackupStale = true }).Should().Contain("backup_stale");
        HealthRules.Alerts(quiet with { DiskFreeBytes = 5L * 1024 * 1024 * 1024 }).Should().Contain("disk_low");
        HealthRules.Alerts(quiet with { PrivateBytes = 401L * 1024 * 1024 }).Should().Contain("memory_high");
        HealthRules.Alerts(quiet with { SelfTestFailed = true }).Should().Contain("isolation_selftest_failed");
        HealthRules.Alerts(quiet with { AuditChainBroken = true }).Should().Contain("audit_chain_broken");
        HealthRules.Alerts(quiet with { LoginFailuresLastHour = 20 }).Should().Contain("login_failures");
    }

    [Fact]
    public void 管理收件匣規則()
    {
        AdminInboxRules.Canonical("owner-reset").Should().Be("reset-owner");
        AdminInboxRules.Canonical(" Reset-Owner ").Should().Be("reset-owner");
        AdminInboxRules.Canonical("drain").Should().Be("drain");
        AdminInboxRules.Canonical("format-c").Should().BeNull();
        AdminInboxRules.IsCommandFile("20261011093000000-abc.json").Should().BeTrue();
        AdminInboxRules.IsCommandFile("x.json.tmp").Should().BeFalse();
        AdminInboxRules.IsCommandFile(".hidden.json").Should().BeFalse();
        AdminInboxRules.IsCommandFile("x.txt").Should().BeFalse();
        AdminInboxRules.IsValidId("0123abcd-ef").Should().BeTrue();
        AdminInboxRules.IsValidId("..\\..\\x").Should().BeFalse("id 會當結果檔名，不能有路徑字元");
        AdminInboxRules.Parse("{\"id\":\"abc\",\"command\":\"owner-reset\",\"requestedAt\":1}", out var error)!.Command.Should().Be("reset-owner");
        error.Should().BeNull();
        AdminInboxRules.Parse("{\"id\":\"abc\",\"command\":\"rm -rf\"}", out error).Should().NotBeNull();
        error.Should().Contain("不認得");
        AdminInboxRules.Parse("{\"id\":\"../x\",\"command\":\"drain\"}", out error).Should().BeNull();
        AdminInboxRules.Parse("not json", out error).Should().BeNull();

        var now = DateTimeOffset.FromUnixTimeMilliseconds(100_000_000);
        AdminInboxRules.IsExpired(new AdminCommand("a", "drain", now.AddMinutes(-11).ToUnixTimeMilliseconds()), now).Should().BeTrue();
        AdminInboxRules.IsExpired(new AdminCommand("a", "drain", now.AddMinutes(-9).ToUnixTimeMilliseconds()), now).Should().BeFalse();
        AdminInboxRules.IsExpired(new AdminCommand("a", "stop", now.AddMinutes(30).ToUnixTimeMilliseconds()), now).Should().BeTrue("時間在未來太多也不收");
        AdminInboxRules.IsExpired(new AdminCommand("a", "reset-owner", 0), now).Should().BeFalse("reset-owner 不會過期");
    }

    [Fact]
    public void 主機設定規則()
    {
        HostingRules.PublicOrigin(new HubOptions { ServerDomain = "Hub.Example.COM", HttpsPort = 443 }).Should().Be("https://hub.example.com");
        HostingRules.PublicOrigin(new HubOptions { ServerDomain = "localhost", HttpsPort = 5443 }).Should().Be("https://localhost:5443");
        HostingRules.PublicOrigin(new HubOptions { PublicOrigin = "https://hub.example.com/" }).Should().Be("https://hub.example.com");
        HostingRules.NormalizeOrigin("https://[::1]:5443").Should().Be("https://[::1]:5443");
        HostingRules.NormalizeOrigin("https://a.b/c").Should().BeNull();
        HostingRules.NormalizeOrigin("ftp://a.b").Should().BeNull();
        HostingRules.NormalizeOrigin("null").Should().BeNull();
        HostingRules.ResolveDataDir("D:\\x", "E:\\y", true, "C:\\app", "C:\\ProgramData").Should().Be("D:\\x");
        HostingRules.ResolveDataDir(null, "E:\\y", true, "C:\\app", "C:\\ProgramData").Should().Be("E:\\y");
        HostingRules.ResolveDataDir(null, null, true, "C:\\app", "C:\\ProgramData").Should().Be("C:\\ProgramData\\MyriagentHub");
        HostingRules.ResolveDataDir(null, null, false, "C:\\app", "C:\\ProgramData").Should().Be("C:\\app\\data");
        HostingRules.ParseBindAddress("any").Kind.Should().Be(BindKind.Any);
        HostingRules.ParseBindAddress("LOCALHOST").Kind.Should().Be(BindKind.Localhost);
        HostingRules.ParseBindAddress("127.0.0.1").Address!.ToString().Should().Be("127.0.0.1");
        var bad = () => HostingRules.ParseBindAddress("everywhere");
        bad.Should().Throw<InvalidOperationException>();
        HostingRules.UseCertificateStore(null, isProduction: true).Should().BeTrue();
        HostingRules.UseCertificateStore(null, isProduction: false).Should().BeFalse();
        HostingRules.UseCertificateStore("file", isProduction: true).Should().BeFalse();
        HostingRules.SelfTestEnabled(null, isWindowsService: true).Should().BeTrue();
        HostingRules.SelfTestEnabled(null, isWindowsService: false).Should().BeFalse();
        HostingRules.SelfTestEnabled(true, isWindowsService: false).Should().BeTrue();
    }

    [Fact]
    public void 建置指紋是提交雜湊前7碼_MyriagentHubBuild優先_沒有時用SourceRevisionId()
    {
        // 沒有 MyriagentHubBuild（本機建置）：InformationalVersion「+」後面的 SourceRevisionId 前 7 碼
        var info = BuildInfoRules.Parse(null, "0.2.0+cee61868489bd7f39ee2d17178a3401da67c4f8e", null);
        info.Version.Should().Be("0.2.0");
        info.Build.Should().Be("cee6186");
        info.InformationalVersion.Should().Be("0.2.0+cee61868489bd7f39ee2d17178a3401da67c4f8e");

        // workflow 發佈：-p:MyriagentHubBuild=<7 碼> 與 -p:SourceRevisionId=<完整雜湊> 都帶，MyriagentHubBuild 優先
        BuildInfoRules.Parse("1a2b3c4", "0.2.0+cee61868489bd7f39ee2d17178a3401da67c4f8e", null).Build.Should().Be("1a2b3c4");
        BuildInfoRules.Parse("1A2B3C4D5E6F", "0.2.0", null).Build.Should().Be("1a2b3c4", "給了較長的雜湊也只取前 7 碼、轉小寫");
        BuildInfoRules.Parse("not-a-sha", "0.2.0+cee61868489bd7f39ee2d17178a3401da67c4f8e", null).Build.Should().Be("cee6186", "不是提交雜湊就退回 SourceRevisionId");
        BuildInfoRules.Parse(" ", "0.2.0+cee6186", null).Build.Should().Be("cee6186");

        BuildInfoRules.Parse(null, "0.2.0", new Version(0, 2, 0, 0)).Build.Should().Be("dev");
        BuildInfoRules.Parse(null, null, new Version(1, 2, 3, 0)).Version.Should().Be("1.2.3");
        BuildInfoRules.Parse(null, "1.0.0+abc", null).Build.Should().Be("dev", "太短的不是提交雜湊");
        BuildInfoRules.Parse("abc", "1.0.0", null).Build.Should().Be("dev");
        BuildInfo.Current.Build.Should().MatchRegex("^([0-9a-f]{7}|dev)$", "running.json、/healthz、manifest、安裝閘門、verify job 都是 7 碼");
    }

    [Fact]
    public void 快取與CSP規則()
    {
        SecurityHeaderRules.CacheControlFor("/auth.html", isStaticFile: true).Should().Be("no-store");
        SecurityHeaderRules.CacheControlFor("/auth.js", isStaticFile: true).Should().Be("no-cache");
        SecurityHeaderRules.CacheControlFor("/api/v1/auth/me", isStaticFile: false).Should().Be("no-store");
        SecurityHeaderRules.ContentSecurityPolicy("https://hub.example.com").Should().Contain("connect-src 'self' wss://hub.example.com;");
        SecurityHeaderRules.ContentSecurityPolicy("https://localhost:5443").Should().Contain("wss://localhost:5443");
    }
}
