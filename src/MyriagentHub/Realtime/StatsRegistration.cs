using Myriagent.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 各電腦即時狀態（stat1；docs/sync-protocol.md 14.8）的登記：看板（只在記憶體）、MyriagentHub 主機的取樣、轉送迴圈，宣告 stat1。由 <c>HubApplication</c> 在
/// <c>AddHubMedia</c> 之後呼叫（stat1 排在 HelloResponse.features 的最後）；瀏覽器那一側（<c>monitor</c>、<c>deviceStats</c>）在瀏覽器閘道
/// </summary>
internal static class StatsRegistration
{
    public static IServiceCollection AddHubStats(this IServiceCollection services)
    {
        services.AddSingleton(sp => new DeviceStatsBoard(sp.GetRequiredService<TimeProvider>())
        {
            // 「有人在看」也算開著電腦狀態的瀏覽器（沒有瀏覽器閘道的主機一律 false）
            BrowserWatching = sp.GetService<BrowserConnections>() is { } browsers ? browsers.AnyMonitoring : static () => false,
        });
        services.AddSingleton<HubStatsSampler>();
        services.AddSingleton<DeviceStatsFanout>();
        services.AddHostedService(sp => sp.GetRequiredService<DeviceStatsFanout>());
        services.AddDeviceHubFeature(DeviceFeatureRules.Stat1);
        return services;
    }
}
