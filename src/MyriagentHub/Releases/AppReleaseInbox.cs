using MyriagentHub.Hosting;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Releases;

/// <summary>
/// 收件匣的背景服務（U2 設計 T4）：監看 admin-inbox\app-release\request.json（Created、Renamed、Changed），加上每 Hub:Releases:InboxPollSeconds 秒的
/// 保險輪詢，啟動時也先跑一次；三個來源排進同一個迴圈，一次只處理一個要求。每輪最後跑一次保存（刪不掉的版本資料夾留到下一輪）。
/// 例外只記錯誤，不讓主機停下來
/// </summary>
internal sealed class AppReleaseInbox(HubPaths paths, AppReleaseIngestor ingestor, AppReleaseStore store,
    IOptions<HubOptions> options, ILogger<AppReleaseInbox> log) : BackgroundService
{
    private readonly SemaphoreSlim signal = new(0, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 第一個 await 之前的程式同步跑在主機的 StartAsync 裡：先讓出去，再開始收件
        await Task.Yield();
        var poll = TimeSpan.FromSeconds(options.Value.Releases.InboxPollSeconds);
        using var watcher = TryWatch(paths.AdminInboxAppReleaseDir);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunRoundAsync(stoppingToken);
            try
            {
                await signal.WaitAsync(poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunRoundAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ingestor.ProcessAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            log.LogError(ex, "收件匣處理失敗");
        }
        try
        {
            await store.PruneAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            log.LogError(ex, "發佈版本的保存失敗");
        }
    }

    /// <summary>監看 request.json；建不起來（資料夾沒有權限、路徑不對）就只靠輪詢</summary>
    private FileSystemWatcher? TryWatch(string dir)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            Directory.CreateDirectory(dir);
            watcher = new FileSystemWatcher(dir, AppReleaseIngestRules.RequestFileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, _) => Signal();
            watcher.Renamed += (_, _) => Signal();
            watcher.Changed += (_, _) => Signal();
            watcher.Error += (_, e) =>
            {
                log.LogWarning(e.GetException(), "收件匣的檔案監看出錯，改靠輪詢");
                Signal();
            };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            watcher?.Dispose();
            log.LogError(ex, "收件匣 {Dir} 監看不了，只靠每 {Seconds} 秒的輪詢", dir, options.Value.Releases.InboxPollSeconds);
            return null;
        }
    }

    private void Signal()
    {
        if (signal.CurrentCount == 0)
        {
            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }
}
