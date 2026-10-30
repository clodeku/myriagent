using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Ops;

internal static class OpsEndpoints
{
    /// <summary>
    /// /healthz：匿名（受未認證總量限流），只回 status 與 build（私有 repo 的提交雜湊，外部無法據此找弱點）。
    /// /readyz：擁有者、裝置或本機迴路；排空中回 503（內容的 drain 是來源、開始時間與期限）。/api/v1/ops/status：擁有者（第 3 階段起含裝置）。
    /// </summary>
    public static void MapHubOps(this WebApplication app)
    {
        app.MapGet("/healthz", (OpsStatusService ops) => Results.Ok(new { status = ops.HealthStatus(), build = BuildInfo.Current.Build }))
            .AllowAnonymous();
        app.MapGet("/readyz", (OpsStatusService ops, DrainState drain) =>
            {
                // 狀態碼與內容的排空資訊來自同一份快照（不會 503 卻寫著沒有排空）
                var snapshot = drain.Current;
                return snapshot.Draining
                    ? Results.Json(ops.Snapshot(snapshot), statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(ops.Snapshot(snapshot));
            })
            .RequireAuthorization(HubPolicies.OpsRead);
        app.MapGet("/api/v1/ops/status", (OpsStatusService ops) => Results.Ok(ops.Snapshot()))
            .RequireAuthorization(HubPolicies.Owner);
    }
}

/// <summary>
/// 每日維護（本機時間 Hub:Backup:DailyAt，預設 03:30，避開白天的使用時段）：備份、媒體清理（WP4.7）、稽核保留期清除與雜湊鏈驗證、
/// 工作階段清理、管理收件匣舊結果清理；啟動後先跑一次隔離自檢，之後每天一次。
/// </summary>
internal sealed class MaintenanceService(
    HubPaths paths,
    BackupService backup,
    AuditLog audit,
    SessionService sessions,
    IsolationSelfTest selfTest,
    MediaMaintenance media,
    TimeProvider time,
    IOptions<HubOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<MaintenanceService> log) : BackgroundService
{
    public const int AuditRetentionDays = 400;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await backup.LoadStateAsync(stoppingToken);
        if (!await WaitForStartAsync(stoppingToken))
            return;

        if (selfTest.Enabled)
            await SafeAsync("隔離自檢", () => selfTest.RunAsync(stoppingToken));

        var at = BackupRules.ParseDailyAt(options.Value.Backup.DailyAt);
        while (!stoppingToken.IsCancellationRequested)
        {
            var next = BackupRules.NextRun(time.GetLocalNow(), at);
            var delay = next - time.GetLocalNow();
            try
            {
                await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await RunDailyAsync(stoppingToken);
        }
    }

    public async Task RunDailyAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Backup.Enabled)
            await SafeAsync("每日備份", () => backup.RunAsync("daily", scheduled: true, cancellationToken));
        // WP4.7 D19：在備份之後（刪除門檻看最舊的備份）
        await SafeAsync("媒體清理", () => media.RunAsync(cancellationToken));
        await SafeAsync("稽核保留期清除", () => audit.PruneAsync(AuditRetentionDays, cancellationToken));
        await SafeAsync("稽核雜湊鏈驗證", () => audit.VerifyAsync(cancellationToken));
        await SafeAsync("工作階段清理", () => sessions.CleanupAsync(cancellationToken));
        await SafeAsync("管理收件匣結果清理", () => Task.FromResult(CleanupAdminResults()));
        if (selfTest.Enabled)
            await SafeAsync("隔離自檢", () => selfTest.RunAsync(cancellationToken));
    }

    private int CleanupAdminResults()
    {
        if (!Directory.Exists(paths.AdminResultsDir))
            return 0;
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-7);
        var removed = 0;
        foreach (var file in Directory.GetFiles(paths.AdminResultsDir, "*.json"))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                File.Delete(file);
                removed++;
            }
        }
        return removed;
    }

    private async Task<bool> WaitForStartAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var cancel = stoppingToken.Register(() => started.TrySetCanceled());
        try
        {
            await started.Task;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SafeAsync<T>(string name, Func<Task<T>> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "每日維護步驟失敗：{Step}", name);
        }
    }
}

internal static class OpsServiceCollectionExtensions
{
    public static IServiceCollection AddHubOps(this IServiceCollection services)
    {
        services.AddSingleton<OpsMetrics>();
        services.AddSingleton<DrainState>();
        services.AddSingleton<DrainExpiryService>();
        services.AddHostedService(sp => sp.GetRequiredService<DrainExpiryService>());
        services.AddSingleton<IsolationSelfTest>();
        // 備份之前查磁碟可用空間（同 blob 與縮圖快取的接縫；測試換成固定值）
        services.TryAddSingleton<IDiskSpaceProbe, DriveSpaceProbe>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<AdminCommandExecutor>();
        services.AddSingleton<OpsStatusService>();
        services.AddSingleton<RunningStatus>();
        services.AddHostedService(sp => sp.GetRequiredService<RunningStatus>());
        services.AddSingleton<MemoryFuse>();
        services.AddHostedService(sp => sp.GetRequiredService<MemoryFuse>());
        // 閒置修剪（S9 的閒置記憶體，設計 14.20）：活動由最外層的中介程式與 hub 的全域過濾器記（HubApplication、RealtimeRegistration）
        services.AddSingleton<HubActivity>();
        services.AddSingleton<IHubHeapPort, ProcessHubHeap>();
        services.AddSingleton<IdleTrimService>();
        services.AddHostedService(sp => sp.GetRequiredService<IdleTrimService>());
        services.AddSingleton<MaintenanceService>();
        services.AddHostedService(sp => sp.GetRequiredService<MaintenanceService>());
        // 在登記服務時（HubApplication.Build，早於 SCM 回報 Running）記下本程序的開始時間：部署收件匣裡比它早的 drain 是給上一個程序的
        services.AddSingleton(new HubInstance(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        services.AddSingleton<AdminInboxProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<AdminInboxProcessor>());
        return services;
    }
}
