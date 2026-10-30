using Myriagent.Sync;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Realtime;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MyriagentHub.Releases;

/// <summary>
/// 桌面版發佈（U2；規格 docs/plans/2026-10-16-app-self-update.md 第 7 章）：版本資料表與資料夾、啟動對帳與保存（T3）、
/// 收件匣（T4：內建發佈金鑰、收件流程、監看加輪詢的背景服務）、裝置的下載端點（T5）、裝置 hub 的功能 update1（T7：只更新模式）、
/// 擁有者的版本頁 API 與它讀的各電腦 app 狀態（T9：<see cref="AppReleaseOwnerEndpoints"/>、<see cref="AppReleaseDeviceStates"/>）
/// </summary>
internal static class ReleasesRegistration
{
    public static IServiceCollection AddHubReleases(this IServiceCollection services, HubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton<AppReleaseStore>();
        services.AddSingleton<AppReleaseDeviceStates>();
        services.AddSingleton<IStartupTask>(sp => new StartupTask(StartupOrder.Releases, "app 發佈版本（啟動對帳）",
            ct => sp.GetRequiredService<AppReleaseStore>().InitializeAsync(ct)));
        // 收件匣（U2 T4）：內建發佈金鑰（介面只給測試換）、收件流程、監看加輪詢的背景服務；
        // 磁碟可用空間沿用媒體的接縫（TryAdd：AddHubMedia 登記的是同一個預設）
        services.AddSingleton<IBuiltInReleaseKeys, ProductionBuiltInReleaseKeys>();
        services.TryAddSingleton<IDiskSpaceProbe, DriveSpaceProbe>();
        services.AddSingleton<AppReleaseIngestor>();
        services.AddSingleton<AppReleaseInbox>();
        services.AddHostedService(sp => sp.GetRequiredService<AppReleaseInbox>());
        // 只更新模式（sync-protocol 1.4、14.1；U2 D4）：HelloResponse 宣告 update1（跟其他工作包的功能並列），裝置也宣告時才進交集
        services.AddDeviceHubFeature(DeviceFeatureRules.Update1);
        return services;
    }

    /// <summary>發佈的端點：裝置的 latest 與套件（T5，<see cref="AppReleaseEndpoints"/>）、擁有者的版本頁 API（T9，<see cref="AppReleaseOwnerEndpoints"/>）</summary>
    public static void MapHubReleases(this IEndpointRouteBuilder app)
    {
        app.MapDeviceReleases();
        app.MapOwnerReleases();
    }
}
