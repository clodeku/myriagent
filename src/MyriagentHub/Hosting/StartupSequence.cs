using System.Diagnostics;

namespace MyriagentHub.Hosting;

/// <summary>
/// 開始聽連接埠之前要完成的初始化（資料庫遷移、擁有者帳號、TLS 憑證…）。各模組以 <see cref="IStartupTask"/> 登記，
/// 由 <see cref="StartupSequence"/> 依 <see cref="Order"/> 執行；任何一項失敗服務就不啟動（寧可起不來，也不要半套）。
/// </summary>
internal interface IStartupTask
{
    int Order { get; }

    string Name { get; }

    Task RunAsync(CancellationToken cancellationToken);
}

internal static class StartupOrder
{
    public const int Paths = 0;
    public const int Database = 100;

    /// <summary>sync.db（結構、單一寫入者）：hub.db 遷移之後</summary>
    public const int SyncDatabase = 150;

    public const int Audit = 200;
    /// <summary>擁有者帳號（首次啟動建立 admin、轉換第 2 階段的 passkey 帳號）</summary>
    public const int OwnerAccount = 300;

    /// <summary>協作人的名冊（多人協作）：載入記憶體；擁有者帳號之後、開始聽連接埠之前（票證取回要用）</summary>
    public const int Collaborators = 320;

    /// <summary>伺服器身分金鑰、信任資料的發布</summary>
    public const int Trust = 350;

    /// <summary>桌面版發佈（U2）：版本資料夾與 app_releases 的對帳、保存、載入最新版快取；hub.db 遷移之後、開始聽連接埠之前</summary>
    public const int Releases = 380;
    public const int Tls = 400;
    public const int Status = 900;
}

internal sealed class StartupTask(int order, string name, Func<CancellationToken, Task> run) : IStartupTask
{
    public int Order { get; } = order;

    public string Name { get; } = name;

    public Task RunAsync(CancellationToken cancellationToken) => run(cancellationToken);
}

/// <summary>
/// 以 IHostedLifecycleService.StartingAsync 執行：一定在所有 IHostedService.StartAsync（含啟動 Kestrel 的 web 主機服務）之前。
/// </summary>
internal sealed class StartupSequence(IEnumerable<IStartupTask> tasks, ILogger<StartupSequence> log) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        foreach (var task in tasks.OrderBy(t => t.Order))
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await task.RunAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                log.LogCritical(ex, "啟動步驟失敗：{Step}", task.Name);
                throw;
            }
            log.LogInformation("啟動步驟完成：{Step}（{Ms} ms）", task.Name, sw.ElapsedMilliseconds);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
