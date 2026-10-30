using System.Runtime;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Sync;
using MyriagentHub.Tls;
using Microsoft.Extensions.Options;
using Microsoft.Win32;

namespace MyriagentHub.Ops;

/// <summary>健康狀態的輸入（純資料）</summary>
internal sealed record HealthInputs(bool SelfTestFailed, bool AuditChainBroken, bool MemoryWarning);

/// <summary>告警的輸入（純資料）</summary>
internal sealed record AlertInputs(
    string CertificateExpiry,
    bool CertificateSelfSigned,
    bool AcmeEnabled,
    bool BackupStale,
    bool BackupEnabled,
    long DiskFreeBytes,
    long PrivateBytes,
    long WarnPrivateBytes,
    bool SelfTestFailed,
    bool AuditChainBroken,
    int LoginFailuresLastHour,
    int LoginFailureAlertThreshold,
    BlobQuotaLevel BlobLevel = BlobQuotaLevel.Ok);

/// <summary>/healthz 的狀態與營運頁的告警（9.10）</summary>
internal static class HealthRules
{
    /// <summary>ok 或 degraded：隔離自檢不符、稽核鏈不一致、記憶體超過告警門檻</summary>
    public static string Status(HealthInputs inputs) =>
        inputs.SelfTestFailed || inputs.AuditChainBroken || inputs.MemoryWarning ? "degraded" : "ok";

    /// <summary>
    /// 告警代碼（網頁橫幅；第 3 階段起也推給所有在線桌面的系統匣）：憑證 &lt;14 天、備份 &gt;36 小時沒成功、磁碟 &lt;20 GB、
    /// 私有記憶體 &gt;400 MB、隔離自檢失敗、稽核鏈不一致、1 小時內登入失敗 ≥20 次、blob 用量到告警或拒收門檻（WP4.7）。
    /// </summary>
    public static IReadOnlyList<string> Alerts(AlertInputs a)
    {
        var alerts = new List<string>();
        if (a.CertificateExpiry is "critical" or "expired")
            alerts.Add("certificate_expiring");
        else if (a.CertificateExpiry == "warning")
            alerts.Add("certificate_expiring_soon");
        if (a.CertificateSelfSigned && a.AcmeEnabled)
            alerts.Add("certificate_self_signed");
        if (a.BackupEnabled && a.BackupStale)
            alerts.Add("backup_stale");
        if (a.DiskFreeBytes >= 0 && a.DiskFreeBytes < 20L * 1024 * 1024 * 1024)
            alerts.Add("disk_low");
        if (a.PrivateBytes > a.WarnPrivateBytes)
            alerts.Add("memory_high");
        if (a.SelfTestFailed)
            alerts.Add("isolation_selftest_failed");
        if (a.AuditChainBroken)
            alerts.Add("audit_chain_broken");
        if (a.LoginFailuresLastHour >= a.LoginFailureAlertThreshold)
            alerts.Add("login_failures");
        // WP4.7 D42：blob 用量到告警門檻；到拒收門檻（或磁碟可用空間低於 blob 的下限）時新的 blob 一律 507，只報較嚴重的那一個
        if (a.BlobLevel == BlobQuotaLevel.Warn)
            alerts.Add("blob_quota");
        else if (a.BlobLevel == BlobQuotaLevel.Refuse)
            alerts.Add("blob_refuse");
        return alerts;
    }
}

/// <summary>營運狀態的彙整（/readyz、/api/v1/ops/status）</summary>
internal sealed class OpsStatusService(
    HubPaths paths,
    OpsMetrics metrics,
    DrainState drain,
    AuditLog audit,
    IsolationSelfTest selfTest,
    BackupService backup,
    TlsManager tls,
    AnonymousAuthGuard authGuard,
    MemoryFuse fuse,
    RunningStatus running,
    UsagePollStats usagePolls,
    ConversationCounters conversationCounters,
    ConversationDirectoryCache conversationDirectory,
    CommandCounters commandCounters,
    MediaOpsStatus media,
    IdleTrimService idleTrim,
    TimeProvider time,
    IOptions<HubOptions> options)
{
    public string HealthStatus() => HealthRules.Status(new HealthInputs(
        selfTest.LastReport is { Ok: false },
        audit.LastVerification is { Ok: false },
        fuse.Warning));

    /// <summary>
    /// 營運狀態；記憶體、執行緒數在查詢當下取樣，CPU 是距上一次取樣的平均（時間窗見 process.cpuWindowSeconds）。
    /// 排空資訊用 <paramref name="drainSnapshot"/>（/readyz 用同一份決定狀態碼），沒給就讀一次目前的。
    /// </summary>
    public object Snapshot(DrainSnapshot? drainSnapshot = null)
    {
        var drainNow = drainSnapshot ?? drain.Current;
        var sample = metrics.Sample();
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var build = BuildInfo.Current;
        var gc = GC.GetGCMemoryInfo();
        var tlsSnapshot = tls.Snapshot();
        var backupState = backup.LastState;
        var settings = options.Value;
        var diskFree = DiskFree(paths.Root);
        var backupStale = BackupRules.IsStale(backupState?.LastSuccessAt, now, running.StartedAt == 0 ? now : running.StartedAt);
        var polls = usagePolls.Snapshot(now);
        var blobLevel = media.Level();
        var alerts = HealthRules.Alerts(new AlertInputs(
            tlsSnapshot.Expiry,
            tlsSnapshot.SelfSigned,
            tlsSnapshot.AcmeEnabled,
            backupStale,
            settings.Backup.Enabled,
            diskFree,
            sample.PrivateBytes,
            settings.MemoryFuse.WarnMb * 1024L * 1024,
            selfTest.LastReport is { Ok: false },
            audit.LastVerification is { Ok: false },
            authGuard.GlobalFailuresLastHour,
            settings.RateLimits.GlobalFailureAlertPerHour,
            blobLevel));
        return new
        {
            status = HealthStatus(),
            draining = drainNow.Draining,
            // 排空的來源（admin＝管理收件匣根目錄、不過期；deploy＝部署收件匣、expiresAt 到了自動解除）
            drain = drainNow.Draining
                ? new
                {
                    source = DrainRules.SourceName(drainNow.Source),
                    since = drainNow.Since,
                    expiresAt = drainNow.ExpiresAt,
                    retryAfterSec = drainNow.RetryAfterSec,
                    requestId = drainNow.RequestId,
                }
                : null,
            alerts,
            version = build.Version,
            build = build.Build,
            startedAt = running.StartedAt,
            uptimeSeconds = running.StartedAt == 0 ? 0 : (now - running.StartedAt) / 1000,
            environment = HostInfo.IsWindowsService ? "service" : "console",
            process = new
            {
                sampledAt = sample.At,
                privateMb = Math.Round(sample.PrivateBytes / 1048576.0, 1),
                workingSetMb = Math.Round(sample.WorkingSetBytes / 1048576.0, 1),
                gcHeapMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 1),
                // 上一次 GC 結束時 GC 已提交的空間（閒置修剪看它長回來沒有，設計 14.20）
                gcCommittedMb = Math.Round(gc.TotalCommittedBytes / 1048576.0, 1),
                gcHeapLimitMb = Math.Round(gc.TotalAvailableMemoryBytes / 1048576.0, 1),
                serverGc = GCSettings.IsServerGC,
                cpuPercent = sample.CpuPercent,
                cpuWindowSeconds = sample.CpuWindowSeconds,
                threads = sample.Threads,
                // 程序啟動以來閒置修剪（釋放 DLP 樣式、積極回收）的次數（Ops/IdleTrimRules.cs）
                idleTrims = idleTrim.Trims,
            },
            connections = new { active = metrics.ActiveConnections, total = metrics.TotalConnections },
            http5xxLastMinute = metrics.ServerErrorsLastMinute(now),
            database = new
            {
                hubDbBytes = FileSize(paths.HubDb),
                hubWalBytes = FileSize(paths.HubDb + "-wal"),
            },
            // 伺服器對話（WP4.2 D31）：只有計數、取自記憶體（status 本身不查資料庫），sync.db 的大小照 hubDbBytes 的作法
            conversations = Conversations(settings),
            // 遠端指令（WP4.5 D30）：程序啟動以來的計數（pendingDurable 是最近一次重掃的數字），沒有告警
            commands = commandCounters.Snapshot(),
            // 媒體與 blob（WP4.7 D42）：計數取自記憶體
            blobs = media.Status(),
            diskFreeBytes = diskFree,
            tls = tlsSnapshot,
            backup = new
            {
                enabled = settings.Backup.Enabled,
                dailyAt = settings.Backup.DailyAt,
                lastSuccessAt = backupState?.LastSuccessAt,
                lastFolder = backupState?.LastFolder,
                lastIntegrity = backupState?.LastIntegrity,
                lastAttemptAt = backupState?.LastAttemptAt,
                lastError = backupState?.LastError,
                stale = backupStale,
            },
            selfTest = selfTest.LastReport is { } report
                ? new { enabled = selfTest.Enabled, at = (long?)report.At, ok = (bool?)report.Ok, failures = report.Failures, failed = report.Checks.Where(c => !c.Pass).Select(c => c.Name).ToList() }
                : new { enabled = selfTest.Enabled, at = (long?)null, ok = (bool?)null, failures = 0, failed = new List<string>() },
            audit = audit.LastVerification,
            loginFailuresLastHour = authGuard.GlobalFailuresLastHour,
            // S12：每個邏輯帳戶最近一小時被採用的帶 term 用量上傳（查詢次數的代理值；記憶體，重新啟動歸零；UsagePollRateRules）
            usagePollsPerHour = new
            {
                max = polls.Max,
                accounts = polls.Accounts.Select(a => new { logicalAccountId = a.LogicalAccountId, count = a.Count }).ToList(),
            },
            windowsUpdateRebootPending = RebootPending(),
        };
    }

    private object Conversations(HubOptions settings)
    {
        var syncDbBytes = FileSize(paths.SyncDb);
        var (rebuilding, rebuildPending) = conversationDirectory.RebuildCounts();
        return ConversationOpsRules.Block(conversationCounters.Snapshot(), rebuilding, rebuildPending, syncDbBytes, FileSize(paths.SyncDb + "-wal"),
            ConversationOpsRules.SyncDbLevel(syncDbBytes, settings.Conversations.SyncDbWarnBytes, settings.Conversations.SyncDbCriticalBytes));
    }

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long DiskFree(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>Windows Update 是否有待重新開機（只讀登錄檔；家用版無法長期延後更新，營運頁提醒使用者）</summary>
    private static bool? RebootPending()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
