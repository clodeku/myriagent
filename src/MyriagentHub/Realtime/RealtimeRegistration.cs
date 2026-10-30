using Myriagent.Sync;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using MyriagentHub.Sync;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>裝置 hub 的登記（WP3.2；sync-protocol 1.1 的伺服器設定）</summary>
internal static class RealtimeRegistration
{
    public static IServiceCollection AddHubRealtime(this IServiceCollection services, HubOptions options)
    {
        if (!AppVersionRules.TryParse(options.Sync.MinAppVersion, out _))
            throw new InvalidOperationException("Hub:Sync:MinAppVersion 不是版本號：" + options.Sync.MinAppVersion);
        // 伺服器對話的上限（WP4.2 D1：只有上限、沒有開關）：不合格就起不來，admin preflight 以同一份規則先擋
        if (ConversationSettingsRules.Validate(options.Conversations) is { } conversationProblem)
            throw new InvalidOperationException(conversationProblem);

        services.AddSignalR(o =>
            {
                o.EnableDetailedErrors = false;
                // 全域過濾器（裝置與瀏覽器 hub 都套用）：每一次呼叫記成閒置修剪的活動，心跳等背景往來不記
                o.AddFilter<HubActivityFilter>();
            })
            .AddNewtonsoftJsonProtocol(o => o.PayloadSerializerSettings = SyncJson.Settings)
            .AddHubOptions<DeviceHub>(o =>
            {
                o.MaximumReceiveMessageSize = SyncProtocol.DeviceHubMaxMessageBytes;
                o.MaximumParallelInvocationsPerClient = 1;
                o.KeepAliveInterval = TimeSpan.FromSeconds(SyncProtocol.HeartbeatSeconds);
                o.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                o.EnableDetailedErrors = false;
                o.AddFilter<DeviceHubFilter>();
            });
        services.AddSingleton<DeviceHubFilter>();
        services.AddSingleton<HubActivityFilter>();
        // 伺服器宣告的功能（sync-protocol 14.1）：各工作包以 AddDeviceHubFeature 登記；U2 起 AddHubReleases 宣告 update1
        services.AddSingleton<DeviceHubFeatures>();
        services.AddSingleton<DeviceHubRuntime>();
        services.AddSingleton<DeviceBroadcaster>();
        services.AddHostedService(sp => sp.GetRequiredService<DeviceBroadcaster>());
        AddConversations(services);
        return services;
    }

    /// <summary>
    /// 伺服器的對話儲存（WP4.2 計畫步 3，D2）：物化、目錄快取（取代 AddHubSync 登記的空目錄，後登記的為準）、變更通知、營運計數、
    /// 被拒與再遮蔽的彙總稽核；目錄在 sync.db 之後載入；即時更新的收件與疊加層（步 11）、快照請求（步 12）。沒有開關（答 1）：一律宣告 conv1、live1，
    /// 零台發布裝置時與第 3 階段相同（只多 HelloResponse 的 features）
    /// </summary>
    private static void AddConversations(IServiceCollection services)
    {
        services.AddSingleton<ConversationCounters>();
        services.AddSingleton<ConversationDirectoryCache>();
        services.AddSingleton<IConversationDirectory>(sp => sp.GetRequiredService<ConversationDirectoryCache>());
        services.AddSingleton<ConversationChangeFeed>();
        services.AddSingleton<ConversationRejectAudit>();
        services.AddHostedService(sp => sp.GetRequiredService<ConversationRejectAudit>());
        services.AddSingleton<ConversationMaterializer>();
        // 物化表的重建（步 5、D14）：要不要重建在啟動時決定（目錄載入之後、開始收事件之前），重建本身在背景
        services.AddSingleton<ConversationRebuildHooks>();
        services.AddSingleton<ConversationRebuild>();
        services.AddHostedService(sp => sp.GetRequiredService<ConversationRebuild>());
        // 營運計數的起始值與被拒事件的保留期（步 5、D31、D40）
        services.AddSingleton<ConversationMaintenance>();
        services.AddHostedService(sp => sp.GetRequiredService<ConversationMaintenance>());
        services.AddSingleton<IStartupTask>(sp => new StartupTask(StartupOrder.SyncDatabase + 10, "conversations", async ct =>
        {
            await sp.GetRequiredService<ConversationDirectoryCache>().LoadAsync(sp.GetRequiredService<SyncWriter>(), ct);
            await CompleteRemovalsAsync(sp, ct);
            sp.GetRequiredService<ConversationRebuild>().Check();
        }));
        services.AddDeviceHubFeature(DeviceFeatureRules.Conv1);
        // Grok、Antigravity 也同步（設計 2026-10-22-grok-antigravity-sync 第 3 節）：目錄（CatalogRules）、對話事件（ConversationEventRules）、
        // cli.state 都收四種；桌面只在協商出 prov1 時送這兩種
        services.AddDeviceHubFeature(DeviceFeatureRules.Prov1);
        // 即時更新（步 11、D27）：裝置 hub 的 LiveRelay 收件與閘道的疊加層（疊加層訂閱裝置連線的事件）；一律宣告 live1（答 1）
        services.AddSingleton<LiveOverlay>();
        services.AddHostedService(sp => sp.GetRequiredService<LiveOverlay>());
        services.AddSingleton<LiveRelayIntake>();
        services.AddDeviceHubFeature(DeviceFeatureRules.Live1);
        // 即時更新改送增量（2026-10-23，docs/plans/2026-10-23-live-delta-and-replica-live.md 第 3 節）：基底以（連線, 對話, ord）記在記憶體
        services.AddSingleton<LiveDeltaBases>();
        services.AddDeviceHubFeature(DeviceFeatureRules.Live2);
        // 伺服器 → 裝置的 WatchChanged 與 SnapshotRequest（步 12、D25、D26）：觀看追蹤在 AddHubBrowser（要瀏覽器連線），快照請求在這裡（restore 只看裝置連線）
        services.AddSingleton<IDeviceMessageSender, HubDeviceMessageSender>();
        services.AddSingleton<SnapshotRequester>();
        services.AddHostedService(sp => sp.GetRequiredService<SnapshotRequester>());
    }

    /// <summary>
    /// 移除電腦（設計 14.22）在 hub.db 提交之後沒做完的對話刪除（寫入者已停止、程序結束）：目錄載入之後、重建之前補做（冪等，什麼都沒補就什麼都不寫）
    /// </summary>
    private static async Task CompleteRemovalsAsync(IServiceProvider sp, CancellationToken cancellationToken)
    {
        var removed = sp.GetRequiredService<DeviceDirectory>().All.Where(d => d.Removed).Select(d => d.Id).ToList();
        if (removed.Count == 0)
            return;
        var materializer = sp.GetRequiredService<ConversationMaterializer>();
        var deleted = await sp.GetRequiredService<SyncWriter>().WriteAsync(context => removed.Sum(id => materializer.DeleteOwnedBy(context, id).Count), cancellationToken);
        if (deleted == 0)
            return;
        sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RealtimeRegistration).FullName!)
            .LogWarning("補做已移除電腦的對話刪除：{Count} 個對話", deleted);
        sp.GetService<Media.MediaRefIndexer>()?.RequestReconcile();
    }

    /// <summary>只允許 WebSockets；裝置權杖（chd_）認證，Origin 由 <see cref="OriginGuardMiddleware"/> 檢查（有就必須是本站）</summary>
    public static void MapHubRealtime(this IEndpointRouteBuilder app) =>
        app.MapHub<DeviceHub>(SyncProtocol.DeviceHubPath, o =>
        {
            o.Transports = HttpTransportType.WebSockets;
            // 不開 stateful reconnect：重連換上的新 TLS 連線沿用舊連線「已證明」的狀態、不重新綁 SPKI，
            // 拿到合法憑證的中間人可以在裝置重連時接手。每條新的傳輸都要重新 Hello／Prove；
            // 持久事件不靠推送（BatchAck 沒收到就重送、Notify 漏了重連後會拉、UsageAssign 與信任版本在交握時重給）
            o.AllowStatefulReconnects = false;
        }).RequireAuthorization(HubPolicies.Device);
}
