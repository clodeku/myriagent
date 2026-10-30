using MyriagentHub.Data;
using MyriagentHub.Realtime;
using MyriagentHub.Hosting;

namespace MyriagentHub.Sync;

/// <summary>同步核心的登記（WP3.2）：sync.db 的單一寫入者與它的啟動、結束；事件紀錄、帳戶目錄、裝置目錄、通知與連線登記</summary>
internal static class SyncCoreRegistration
{
    public static IServiceCollection AddHubSync(this IServiceCollection services)
    {
        services.AddSingleton<SyncWriter>();
        services.AddSingleton<SyncNotifier>();
        services.AddSingleton<ISyncNotifier>(sp => sp.GetRequiredService<SyncNotifier>());
        services.AddSingleton<ConnectionRegistry>();
        services.AddSingleton<IDevicePresence>(sp => sp.GetRequiredService<ConnectionRegistry>());
        services.AddSingleton<DeviceDirectory>();
        // 第 4 階段的接縫（WP4.1 D52）：預設是空的；WP4.2 的對話儲存、WP4.7 的 blob 儲存在自己的 Add*（在這之後呼叫）以 AddSingleton 登記實作，後登記的為準
        services.AddSingleton<IConversationDirectory, EmptyConversationDirectory>();
        services.AddSingleton<IBlobStore, EmptyBlobStore>();
        services.AddSingleton<Catalog>();
        services.AddSingleton<EventLog>();
        services.AddSingleton<UsagePollStats>();
        services.AddSingleton<UsageCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<UsageCoordinator>());
        services.AddSingleton<IStartupTask>(sp => new StartupTask(StartupOrder.SyncDatabase, "sync.db", ct => InitializeAsync(sp, ct)));
        services.AddHostedService<SyncShutdown>();
        return services;
    }

    /// <summary>
    /// 開 sync.db（遷移、寫入執行緒）、載入裝置目錄（hub.db 已遷移）、通知的最高水位；最後補做撤銷在 hub.db 提交之後沒做完的
    /// sync.db 那一半（寫入者已停止、sync.db 寫入失敗、程序結束；<see cref="RevocationCompletion"/>）
    /// </summary>
    private static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var writer = services.GetRequiredService<SyncWriter>();
        await writer.InitializeAsync(cancellationToken);
        var directory = services.GetRequiredService<DeviceDirectory>();
        await directory.LoadAsync(cancellationToken);
        using (var connection = writer.OpenRead())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT IFNULL(MAX(seq), 0) FROM events";
            services.GetRequiredService<SyncNotifier>().Initialize((long)cmd.ExecuteScalar()!);
        }

        var revoked = directory.All.Where(d => d.Revoked).ToList();
        if (revoked.Count == 0)
            return;
        var catalog = services.GetRequiredService<Catalog>();
        var completed = await writer.WriteAsync(context => RevocationCompletion.Complete(context, catalog, revoked), cancellationToken);
        if (completed.Count > 0)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SyncCoreRegistration).FullName!)
                .LogWarning("補做 {Count} 台已撤銷裝置的 sync.db 那一半（解除帳戶連結、發布 device.revoked）：{Devices}", completed.Count, string.Join(", ", completed));
        }
    }
}

/// <summary>所有服務都停止之後：做完佇列裡的寫入、WAL 併回主檔、關閉寫入連線</summary>
internal sealed class SyncShutdown(SyncWriter writer) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        writer.Stop();
        return Task.CompletedTask;
    }
}
