using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using MyriagentHub.Ops;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Security;

/// <summary>SEC-14：唯讀隔離自檢的規則與實際嘗試（部署閘門）；SQL Server 與 IIS 的設定前後比對由部署腳本負責</summary>
public sealed class IsolationTests
{
    [Theory]
    [InlineData("MustNotWrite", "Denied", true)]
    [InlineData("MustNotWrite", "Absent", true)]
    [InlineData("MustNotWrite", "Allowed", false)]
    [InlineData("MustNotWrite", "Error", false)]
    [InlineData("MustNotList", "Allowed", false)]
    [InlineData("MustNotList", "Denied", true)]
    [InlineData("MustNotRead", "Allowed", false)]
    [InlineData("MustNotRead", "Absent", true)]
    [InlineData("MustWrite", "Allowed", true)]
    [InlineData("MustWrite", "Absent", false)]
    [InlineData("MustWrite", "Denied", false)]
    public void 判定規則_無法判斷一律算失敗(string expectation, string outcome, bool pass) =>
        IsolationRules.Passes(Enum.Parse<IsolationExpectation>(expectation), Enum.Parse<ProbeOutcome>(outcome)).Should().Be(pass);

    [Fact]
    public void 任何一項不符整體就不通過()
    {
        var ok = new IsolationProbe("a", "C:\\", IsolationExpectation.MustNotWrite);
        var own = new IsolationProbe("b", "D:\\data", IsolationExpectation.MustWrite);
        IsolationRules.Evaluate(1, [(ok, ProbeOutcome.Denied, null), (own, ProbeOutcome.Allowed, null)]).Ok.Should().BeTrue();
        var report = IsolationRules.Evaluate(1, [(ok, ProbeOutcome.Allowed, null), (own, ProbeOutcome.Allowed, null)]);
        report.Ok.Should().BeFalse();
        report.Failures.Should().Be(1);
        report.Checks[0].Expect.Should().Be("deny-write");
        IsolationRules.Evaluate(1, []).Ok.Should().BeFalse("一項都沒測不能算通過");
    }

    [Fact]
    public void 預設探測清單涵蓋設計要求的位置_inetpub公開內容不列入讀不到()
    {
        var probes = IsolationRules.DefaultProbes(@"C:\", @"C:\Windows", @"C:\Program Files", @"C:\Services\MyriagentHub\releases\0.2.0",
            [@"C:\ProgramData\MyriagentHub\db"], [@"C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL\DATA", @"D:\SQLData", @"D:\SQLLog", @"D:\SQLBackup"]);
        probes.Where(p => p.Expectation == IsolationExpectation.MustNotWrite).Select(p => p.Path)
            .Should().BeEquivalentTo(@"C:\", @"C:\Windows", @"C:\Program Files", @"C:\Services\MyriagentHub\releases\0.2.0");
        // C:\inetpub\wwwroot 是 IIS 預設站台的公開內容、預設 Users 可讀：不列入「必須讀不到」（applicationHost.config 照樣檢查）。
        // SQL Server 的資料、記錄與備份資料夾照樣列入：擁有者收緊 ACL 之前這幾項不會通過（部署閘門失敗是預期行為）
        probes.Where(p => p.Expectation == IsolationExpectation.MustNotList).Select(p => p.Path)
            .Should().BeEquivalentTo(@"C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL\DATA", @"D:\SQLData", @"D:\SQLLog", @"D:\SQLBackup");
        probes.Should().NotContain(p => p.Path.Contains("inetpub", StringComparison.OrdinalIgnoreCase));
        probes.Single(p => p.Expectation == IsolationExpectation.MustNotRead).Path
            .Should().Be(@"C:\Windows\System32\inetsrv\config\applicationHost.config");
        probes.Single(p => p.Expectation == IsolationExpectation.MustWrite).Path.Should().Be(@"C:\ProgramData\MyriagentHub\db");
    }

    [Fact]
    public void SQL_Server資料夾由登錄檔快照推出_去重()
    {
        var dirs = IsolationRules.SqlServerDirectories(
        [
            new SqlServerInstanceInfo("MSSQL17.MSSQLSERVER", @"C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL", @"D:\SQLData\", @"D:\SQLLog", @"C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL\Backup"),
            new SqlServerInstanceInfo("MSSQL16.EXPRESS", null, null, null, null),
        ]);
        dirs.Should().BeEquivalentTo(
            @"C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL\DATA",
            @"C:\Program Files\Microsoft SQL Server\MSSQL17.MSSQLSERVER\MSSQL\Backup",
            @"D:\SQLData",
            @"D:\SQLLog");
    }

    [Fact]
    public void 實際嘗試_可寫_被拒_不存在()
    {
        var dir = TempDir.Create("myriagenthub-isolation");
        try
        {
            IsolationSelfTest.Probe(new IsolationProbe("w", dir, IsolationExpectation.MustWrite)).Outcome.Should().Be(ProbeOutcome.Allowed);
            Directory.GetFiles(dir).Should().BeEmpty("寫入嘗試的暫存檔關檔即刪");
            IsolationSelfTest.Probe(new IsolationProbe("w", dir, IsolationExpectation.MustNotWrite)).Outcome.Should().Be(ProbeOutcome.Allowed);
            IsolationSelfTest.Probe(new IsolationProbe("l", dir, IsolationExpectation.MustNotList)).Outcome.Should().Be(ProbeOutcome.Allowed);
            IsolationSelfTest.Probe(new IsolationProbe("x", Path.Combine(dir, "nope"), IsolationExpectation.MustNotList)).Outcome.Should().Be(ProbeOutcome.Absent);
            IsolationSelfTest.Probe(new IsolationProbe("x", Path.Combine(dir, "nope", "f.txt"), IsolationExpectation.MustNotRead)).Outcome.Should().Be(ProbeOutcome.Absent);

            // 只授權給 SYSTEM 的檔案與資料夾：目前身分讀不到、列舉不到、寫不進去
            var secretFile = Path.Combine(dir, "secret.config");
            File.WriteAllText(secretFile, "x");
            var lockedDir = Path.Combine(dir, "locked");
            Directory.CreateDirectory(lockedDir);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(true, false);
            fileSecurity.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(secretFile).SetAccessControl(fileSecurity);
            var dirSecurity = new DirectorySecurity();
            dirSecurity.SetAccessRuleProtection(true, false);
            dirSecurity.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(lockedDir).SetAccessControl(dirSecurity);
            try
            {
                IsolationSelfTest.Probe(new IsolationProbe("r", secretFile, IsolationExpectation.MustNotRead)).Outcome.Should().Be(ProbeOutcome.Denied);
                IsolationSelfTest.Probe(new IsolationProbe("l", lockedDir, IsolationExpectation.MustNotList)).Outcome.Should().Be(ProbeOutcome.Denied);
                IsolationSelfTest.Probe(new IsolationProbe("w", lockedDir, IsolationExpectation.MustNotWrite)).Outcome.Should().Be(ProbeOutcome.Denied);
            }
            finally
            {
                RestoreOwnerAccess(secretFile, lockedDir);
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 自檢不符時_selftest_json記錄失敗項目且健康狀態降級()
    {
        var writable = TempDir.Create("myriagenthub-isolation");
        try
        {
            await using var host = await HubTestHost.StartAsync(settings: SelfTestSettings(mustNotWrite: writable));
            var report = await host.Services.GetRequiredService<IsolationSelfTest>().RunAsync();
            report.Ok.Should().BeFalse();
            report.Checks.Should().ContainSingle(c => !c.Pass).Which.Path.Should().Be(writable);

            var json = JsonDocument.Parse(await File.ReadAllTextAsync(host.Paths.SelfTestFile)).RootElement;
            json.GetProperty("ok").GetBoolean().Should().BeFalse();
            json.GetProperty("failures").GetInt32().Should().Be(1);

            using var client = host.CreateClient();
            var health = await client.GetJsonAsync("/healthz");
            health.GetProperty("status").GetString().Should().Be("degraded");
        }
        finally
        {
            TempDir.TryDelete(writable);
        }
    }

    [Fact]
    public async Task 自檢全部通過時健康狀態ok()
    {
        var absent = Path.Combine(Path.GetTempPath(), "myriagenthub-absent-" + Guid.NewGuid().ToString("N"));
        await using var host = await HubTestHost.StartAsync(settings: SelfTestSettings(mustNotWrite: absent));
        var report = await host.Services.GetRequiredService<IsolationSelfTest>().RunAsync();
        report.Ok.Should().BeTrue(string.Join("; ", report.Checks.Where(c => !c.Pass).Select(c => $"{c.Name}={c.Outcome}")));
        report.Checks.Should().Contain(c => c.Expect == "allow-write" && c.Pass, "自己的資料夾要寫得進去");
        using var client = host.CreateClient();
        var health = await client.GetJsonAsync("/healthz");
        health.GetProperty("status").GetString().Should().Be("ok");
    }

    private static Dictionary<string, string?> SelfTestSettings(string mustNotWrite)
    {
        var absent = Path.Combine(Path.GetTempPath(), "myriagenthub-absent-" + Guid.NewGuid().ToString("N"));
        return new Dictionary<string, string?>
        {
            ["Hub:SelfTest:MustNotWrite:0"] = mustNotWrite,
            ["Hub:SelfTest:MustNotList:0"] = absent,
            ["Hub:SelfTest:MustNotReadFiles:0"] = Path.Combine(absent, "applicationHost.config"),
            ["Hub:SelfTest:IncludeSqlServerFolders"] = "false",
        };
    }

    /// <summary>測試結束把權限還給自己（擁有者一定能改 DACL），暫存資料夾才刪得掉</summary>
    private static void RestoreOwnerAccess(string file, string dir)
    {
        using var me = WindowsIdentity.GetCurrent();
        var fileSecurity = new FileSecurity();
        fileSecurity.AddAccessRule(new FileSystemAccessRule(me.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(fileSecurity);
        var dirSecurity = new DirectorySecurity();
        dirSecurity.AddAccessRule(new FileSystemAccessRule(me.User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(dirSecurity);
    }
}
