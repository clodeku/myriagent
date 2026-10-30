using Myriagent.Sync;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;

namespace MyriagentHub.Logs;

/// <summary>
/// 逐字稿與檔案日誌（<c>log1</c>；docs/sync-protocol.md 第 16 節）的登記點：由 <c>HubApplication</c> 在 <c>AddHubTokens</c> 之後呼叫，
/// log1 排在 HelloResponse.features 的 tok1 之後。WP5.1 只有開關與功能宣告；日誌的儲存、端點、掃描與維護由 WP5.2 在這裡接上
/// （端點掛在 <see cref="MapHubLogs"/>，開關關著時一律 404 <c>not_available</c>）
/// </summary>
internal static class LogRegistration
{
    public static IServiceCollection AddHubLogs(this IServiceCollection services, HubOptions options)
    {
        if (options.Logs.Declare)
            services.AddDeviceHubFeature(DeviceFeatureRules.Log1);
        return services;
    }

    /// <summary>日誌的 HTTP 端點（<c>/api/v1/device/logs</c>，WP5.2）；WP5.1 沒有任何端點</summary>
    public static WebApplication MapHubLogs(this WebApplication app) => app;
}
