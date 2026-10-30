using Myriagent.Sync;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MyriagentHub.Media;

/// <summary>
/// 媒體、blob 與上傳的登記（WP4.7）：在 <c>AddHubSync</c> 之後呼叫，以 <c>AddSingleton</c> 換掉 WP4.1 的空 <see cref="IBlobStore"/>
/// （後登記的為準）。沒有功能開關（D1）：<c>Hub:Media</c> 只有上限，不合格就起不來（admin preflight 以同一份規則先擋）
/// </summary>
internal static class MediaRegistration
{
    /// <summary>blob 儲存的啟動步驟：sync.db 開好之後（載入用量要讀 <c>blobs</c> 表）</summary>
    internal const int BlobStoreStartupOrder = StartupOrder.SyncDatabase + 10;

    /// <summary>上傳登記的啟動步驟：blob 儲存之後（從 <c>blob_refs</c> 載回待處理的上傳）</summary>
    internal const int UploadRegistryStartupOrder = StartupOrder.SyncDatabase + 11;

    /// <summary>縮圖快取的啟動步驟：清掉上次留在 tmp 的 <c>thumb-*</c>（不碰資料庫）</summary>
    internal const int ThumbCacheStartupOrder = StartupOrder.SyncDatabase + 12;

    /// <summary>裝置端點（只收存取權杖 <c>cha_</c>）的前綴：存取權杖只認這個前綴底下的路徑（<see cref="AccessTokenStore.PathPrefix"/>）</summary>
    internal const string DevicePrefix = "/api/v1/device";

    public static IServiceCollection AddHubMedia(this IServiceCollection services, HubOptions options)
    {
        if (MediaOptionsRules.Validate(options.Media) is { } problem)
            throw new InvalidOperationException(problem);

        // 接縫檔的預設（TryAdd；WP4.5 也可以呼叫）先登記，下面以 AddSingleton 換成實作（後登記的為準，步 6）
        services.AddMediaSeams();
        services.TryAddSingleton<IDiskSpaceProbe, DriveSpaceProbe>();
        services.AddSingleton<BlobStore>();
        services.AddSingleton<IBlobStore>(sp => sp.GetRequiredService<BlobStore>());
        services.AddSingleton<IStartupTask>(sp => new StartupTask(BlobStoreStartupOrder, "blob 儲存", _ =>
        {
            sp.GetRequiredService<BlobStore>().Initialize();
            return Task.CompletedTask;
        }));

        services.AddSingleton<ItemBlobInspector>();
        services.AddSingleton<UploadRegistry>();
        services.AddSingleton<IUploadRegistry>(sp => sp.GetRequiredService<UploadRegistry>());
        services.AddSingleton<IStartupTask>(sp => new StartupTask(UploadRegistryStartupOrder, "上傳登記", _ =>
        {
            sp.GetRequiredService<UploadRegistry>().Initialize();
            return Task.CompletedTask;
        }));

        // 媒體轉送（步 4）：沒有開關（D1），一律宣告 media1；還沒有任何桌面宣告 media1 時，伺服器一則 MediaRequest 都不送（送之前問 DeviceConnection.Allows）
        services.AddSingleton<MediaLookup>();
        services.AddSingleton<ThumbCache>();
        services.AddSingleton<IStartupTask>(sp => new StartupTask(ThumbCacheStartupOrder, "縮圖快取", _ =>
        {
            sp.GetRequiredService<ThumbCache>().Initialize();
            return Task.CompletedTask;
        }));
        // 轉送在建構時訂閱裝置連線的關閉（ConnectionRegistry.ConnectionRemoved）：那條連線上還在等回覆的轉送立即改成離線
        services.AddSingleton<MediaRelay>();
        services.AddSingleton<MediaResponder>();
        services.AddDeviceHubFeature(DeviceFeatureRules.Media1);

        // 參照索引、每日清理與 ops/status（步 5）：索引在 ApplicationStarted 之後才開始（重建不擋部署的健康閘門）
        services.AddSingleton<MediaRefIndexer>();
        services.AddHostedService(sp => sp.GetRequiredService<MediaRefIndexer>());
        services.AddSingleton<MediaMaintenance>();
        services.AddSingleton<MediaOpsStatus>();

        // 遠端檔案指令的輸出改寫（步 6，D37）：WP4.5 的 CommandResults 在投影之後呼叫（步 13 接上）
        services.AddSingleton<CommandOutputRewriter>();
        services.AddSingleton<ICommandOutputRewriter>(sp => sp.GetRequiredService<CommandOutputRewriter>());
        return services;
    }

    /// <summary>
    /// 媒體的路由（在 <c>MapHubFallbacks</c> 之前）：裝置的 blob 端點（E5、E6）、瀏覽器上傳（E4）、媒體轉送（E1～E3、E7、E9）、縮圖（E8、E10）。
    /// 都要認證（SEC-01），本文上限各自在讀本文之前放寬（<see cref="RequestBodyLimits"/>）
    /// </summary>
    public static void MapHubMedia(this WebApplication app)
    {
        var device = app.MapGroup(DevicePrefix).RequireAuthorization(HubPolicies.DeviceAccess);
        BlobEndpoints.Map(device);
        UploadEndpoints.Map(app, device);
        MediaEndpoints.Map(app, device);
        ThumbEndpoints.Map(device);
    }
}
