namespace MyriagentHub.Ops;

/// <summary>自檢項目要確認的事</summary>
internal enum IsolationExpectation
{
    /// <summary>服務身分必須寫不進這個資料夾</summary>
    MustNotWrite,

    /// <summary>服務身分必須列舉不到這個資料夾（只嘗試開目錄列舉，不讀任何檔案）</summary>
    MustNotList,

    /// <summary>服務身分必須開不了這個檔案</summary>
    MustNotRead,

    /// <summary>服務身分必須寫得進自己的資料夾</summary>
    MustWrite,
}

/// <summary>實際嘗試的結果</summary>
internal enum ProbeOutcome
{
    /// <summary>做得到（寫入成功、列舉成功、開檔成功）</summary>
    Allowed,

    /// <summary>被拒絕（UnauthorizedAccessException）</summary>
    Denied,

    /// <summary>不存在（沒裝 IIS、SQL Server 等）</summary>
    Absent,

    /// <summary>其他錯誤（無法判斷）</summary>
    Error,
}

internal sealed record IsolationProbe(string Name, string Path, IsolationExpectation Expectation);

internal sealed record IsolationCheck(string Name, string Path, string Expect, string Outcome, bool Pass, string? Detail);

internal sealed record IsolationReport(long At, bool Ok, int Failures, IReadOnlyList<IsolationCheck> Checks);

/// <summary>
/// 唯讀隔離自檢的純函式規則（9.3、SEC-14）：服務身分實際嘗試，必須寫不進 C:\、Windows、Program Files、自己的 current 資料夾；
/// 必須讀不到 applicationHost.config、SQL Server 資料與備份資料夾的列舉；必須寫得進自己的資料夾。
/// C:\inetpub\wwwroot 不列入「必須讀不到」：它是 IIS 預設站台的公開內容，預設 Users 可讀（實作校正 2026-10-11）。
/// 任何一項不符＝degraded、告警、部署閘門失敗（由使用者決定如何處理，不自動改任何 ACL）。
/// </summary>
internal static class IsolationRules
{
    /// <summary>
    /// 「不存在」對禁止類算通過（沒裝 IIS 就沒有東西可讀），對必須可寫算失敗；
    /// 「無法判斷」一律算失敗（寧可誤報要人看，也不要漏報）。
    /// </summary>
    public static bool Passes(IsolationExpectation expectation, ProbeOutcome outcome) => expectation switch
    {
        IsolationExpectation.MustWrite => outcome == ProbeOutcome.Allowed,
        _ => outcome is ProbeOutcome.Denied or ProbeOutcome.Absent,
    };

    public static IsolationReport Evaluate(long at, IEnumerable<(IsolationProbe Probe, ProbeOutcome Outcome, string? Detail)> results)
    {
        var checks = results
            .Select(r => new IsolationCheck(
                r.Probe.Name,
                r.Probe.Path,
                ExpectText(r.Probe.Expectation),
                r.Outcome.ToString().ToLowerInvariant(),
                Passes(r.Probe.Expectation, r.Outcome),
                r.Detail))
            .ToList();
        var failures = checks.Count(c => !c.Pass);
        return new IsolationReport(at, failures == 0 && checks.Count > 0, failures, checks);
    }

    /// <summary>
    /// 預設的探測清單（可由 Hub:SelfTest 覆寫）。路徑以環境變數展開；SQL Server 的資料夾由登錄檔讀出（只讀登錄檔、只嘗試列舉）。
    /// 「必須列舉不到」預設只有 SQL Server 的資料、記錄與備份資料夾（D:\SQLData 這類資料夾的 ACL 由擁有者收緊，
    /// 收緊前這幾項不會通過、部署閘門失敗，是預期行為；docs/hub-setup.md）。
    /// </summary>
    public static IReadOnlyList<IsolationProbe> DefaultProbes(
        string systemDrive,
        string windowsDir,
        string programFiles,
        string currentReleaseDir,
        IEnumerable<string> ownWritableDirs,
        IEnumerable<string> sqlServerDirs,
        IReadOnlyList<string>? mustNotWrite = null,
        IReadOnlyList<string>? mustNotList = null,
        IReadOnlyList<string>? mustNotRead = null)
    {
        var probes = new List<IsolationProbe>();
        var noWrite = mustNotWrite is { Count: > 0 } ? mustNotWrite : [systemDrive, windowsDir, programFiles, currentReleaseDir];
        probes.AddRange(noWrite.Select(p => new IsolationProbe("寫入 " + p, p, IsolationExpectation.MustNotWrite)));
        IReadOnlyList<string> noList = mustNotList is { Count: > 0 } ? mustNotList : [];
        probes.AddRange(noList.Concat(sqlServerDirs).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => new IsolationProbe("列舉 " + p, p, IsolationExpectation.MustNotList)));
        var noRead = mustNotRead is { Count: > 0 } ? mustNotRead : [System.IO.Path.Combine(windowsDir, "System32", "inetsrv", "config", "applicationHost.config")];
        probes.AddRange(noRead.Select(p => new IsolationProbe("讀取 " + p, p, IsolationExpectation.MustNotRead)));
        probes.AddRange(ownWritableDirs.Select(p => new IsolationProbe("寫入自己的 " + p, p, IsolationExpectation.MustWrite)));
        return probes;
    }

    /// <summary>
    /// 由登錄檔快照找出 SQL Server 的資料、記錄與備份資料夾：每個執行個體的 Setup\SQLDataRoot（加 DATA、Backup）與
    /// MSSQLServer 的 DefaultData、DefaultLog、BackupDirectory。只回路徑，不連線、不修改任何東西。
    /// </summary>
    public static IReadOnlyList<string> SqlServerDirectories(IEnumerable<SqlServerInstanceInfo> instances)
    {
        var dirs = new List<string>();
        foreach (var instance in instances)
        {
            if (!string.IsNullOrWhiteSpace(instance.SqlDataRoot))
            {
                dirs.Add(System.IO.Path.Combine(instance.SqlDataRoot, "DATA"));
                dirs.Add(System.IO.Path.Combine(instance.SqlDataRoot, "Backup"));
            }
            foreach (var extra in new[] { instance.DefaultData, instance.DefaultLog, instance.BackupDirectory })
            {
                if (!string.IsNullOrWhiteSpace(extra))
                    dirs.Add(extra);
            }
        }
        return dirs.Select(d => d.TrimEnd('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ExpectText(IsolationExpectation expectation) => expectation switch
    {
        IsolationExpectation.MustNotWrite => "deny-write",
        IsolationExpectation.MustNotList => "deny-list",
        IsolationExpectation.MustNotRead => "deny-read",
        _ => "allow-write",
    };
}

/// <summary>登錄檔裡一個 SQL Server 執行個體的路徑資訊</summary>
internal sealed record SqlServerInstanceInfo(string InstanceId, string? SqlDataRoot, string? DefaultData, string? DefaultLog, string? BackupDirectory);
