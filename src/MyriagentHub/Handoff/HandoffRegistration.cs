using Myriagent.Sync;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;

namespace MyriagentHub.Handoff;

/// <summary>
/// 租約、交接與強制接手（<c>hand1</c>；docs/sync-protocol.md 第 16 節）的登記點：由 <c>HubApplication</c> 在 <c>AddHubLogs</c> 之後呼叫，
/// hand1 排在 HelloResponse.features 的最後。只在 <c>Hub:Handoff:Declare</c> 與 <c>Hub:Logs:Declare</c> 都打開時宣告（D1：hand1 只在也有 log1 時）。
/// WP5.1 只有開關與功能宣告；交接服務、在線判斷與裝置 hub 的方法由 WP5.5 在這裡接上
/// </summary>
internal static class HandoffRegistration
{
    public static IServiceCollection AddHubHandoff(this IServiceCollection services, HubOptions options)
    {
        if (options.Handoff.Declare && options.Logs.Declare)
            services.AddDeviceHubFeature(DeviceFeatureRules.Hand1);
        return services;
    }

    /// <summary>交接沒有 HTTP 端點（全部是裝置 hub 與瀏覽器 hub 的方法）；留給 WP5.5 需要時用</summary>
    public static WebApplication MapHubHandoff(this WebApplication app) => app;
}
