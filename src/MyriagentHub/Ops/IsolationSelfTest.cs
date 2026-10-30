using System.Security;
using System.Text.Json;
using MyriagentHub.Audit;
using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace MyriagentHub.Ops;

/// <summary>
/// 唯讀隔離自檢（9.3、SEC-14）：以服務身分實際嘗試寫入、列舉、開檔，結果寫 status\selftest.json 與 /readyz。
/// 服務每次啟動與每天一次；只做「嘗試」，絕不修改任何 ACL、不連 SQL Server、不碰 IIS 設定。
/// 寫入嘗試用 DeleteOnClose 的暫存檔，成功的話關檔即刪。
/// </summary>
internal sealed class IsolationSelfTest(
    HubPaths paths,
    AuditLog audit,
    TimeProvider time,
    IOptions<HubOptions> options,
    ILogger<IsolationSelfTest> log)
{
    private volatile IsolationReport? last;

    public IsolationReport? LastReport => last;

    public bool Enabled => HostingRules.SelfTestEnabled(options.Value.SelfTest.Enabled, HostInfo.IsWindowsService);

    public async Task<IsolationReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value.SelfTest;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windows) ?? @"C:\";
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var sqlDirs = settings.IncludeSqlServerFolders ? IsolationRules.SqlServerDirectories(ReadSqlServerInstances()) : [];
        var probes = IsolationRules.DefaultProbes(
            systemDrive,
            windows,
            programFiles,
            AppContext.BaseDirectory.TrimEnd('\\', '/'),
            [paths.DbDir, paths.BackupsDir, paths.StatusDir, paths.TmpDir, paths.DiagDir],
            sqlDirs,
            settings.MustNotWrite?.Select(Environment.ExpandEnvironmentVariables).ToList(),
            settings.MustNotList?.Select(Environment.ExpandEnvironmentVariables).ToList(),
            settings.MustNotReadFiles?.Select(Environment.ExpandEnvironmentVariables).ToList());

        var results = probes.Select(p =>
        {
            var (outcome, detail) = Probe(p);
            return (p, outcome, detail);
        }).ToList();
        var report = IsolationRules.Evaluate(time.GetUtcNow().ToUnixTimeMilliseconds(), results);
        last = report;
        await WriteReportAsync(report, cancellationToken);
        if (report.Ok)
            log.LogInformation("唯讀隔離自檢通過（{Count} 項）", report.Checks.Count);
        else
            log.LogCritical("唯讀隔離自檢不符 {Failures} 項：{Items}", report.Failures, string.Join("；", report.Checks.Where(c => !c.Pass).Select(c => $"{c.Name}={c.Outcome}")));
        await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, AuditActions.SelfTest, null, report.Ok ? AuditOutcomes.Ok : AuditOutcomes.Error,
            new { failures = report.Failures, failed = report.Checks.Where(c => !c.Pass).Select(c => c.Name).Take(20) }), cancellationToken);
        return report;
    }

    /// <summary>實際嘗試一個項目（不丟例外）</summary>
    public static (ProbeOutcome Outcome, string? Detail) Probe(IsolationProbe probe)
    {
        try
        {
            switch (probe.Expectation)
            {
                case IsolationExpectation.MustNotWrite:
                case IsolationExpectation.MustWrite:
                    if (!Directory.Exists(probe.Path))
                        return (ProbeOutcome.Absent, null);
                    var file = Path.Combine(probe.Path, ".myriagenthub-selftest-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                    {
                    }
                    return (ProbeOutcome.Allowed, null);
                case IsolationExpectation.MustNotList:
                    if (!Directory.Exists(probe.Path))
                        return (ProbeOutcome.Absent, null);
                    using (var entries = Directory.EnumerateFileSystemEntries(probe.Path).GetEnumerator())
                        entries.MoveNext();
                    return (ProbeOutcome.Allowed, null);
                default:
                    if (!File.Exists(probe.Path) && !Directory.Exists(Path.GetDirectoryName(probe.Path)))
                        return (ProbeOutcome.Absent, null);
                    using (new FileStream(probe.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
                    {
                    }
                    return (ProbeOutcome.Allowed, null);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return (ProbeOutcome.Denied, null);
        }
        catch (SecurityException)
        {
            return (ProbeOutcome.Denied, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (ProbeOutcome.Absent, null);
        }
        catch (IOException ex)
        {
            return (ProbeOutcome.Error, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>從登錄檔讀 SQL Server 執行個體的路徑（只讀；讀不到就當沒有）</summary>
    public static IReadOnlyList<SqlServerInstanceInfo> ReadSqlServerInstances()
    {
        var list = new List<SqlServerInstanceInfo>();
        try
        {
            using var names = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
            if (names is null)
                return list;
            foreach (var name in names.GetValueNames())
            {
                if (names.GetValue(name) is not string id || id.Length == 0)
                    continue;
                using var setup = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Microsoft SQL Server\{id}\Setup");
                using var server = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Microsoft SQL Server\{id}\MSSQLServer");
                list.Add(new SqlServerInstanceInfo(id,
                    setup?.GetValue("SQLDataRoot") as string,
                    server?.GetValue("DefaultData") as string,
                    server?.GetValue("DefaultLog") as string,
                    server?.GetValue("BackupDirectory") as string));
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // 讀不到登錄檔：沒有 SQL Server 路徑可測，其他項目照做
        }
        return list;
    }

    private async Task WriteReportAsync(IsolationReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.StatusDir);
        var temp = paths.SelfTestFile + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(report, StatusJson.Options), cancellationToken);
        File.Move(temp, paths.SelfTestFile, overwrite: true);
    }
}

/// <summary>status\*.json 的格式（縮排、camelCase；只含 ASCII 以外字元的跳脫，PowerShell 5.1 讀起來也不會亂碼）</summary>
internal static class StatusJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
