using Myriagent.Sync;
using MyriagentHub.Realtime;

namespace MyriagentHub.Sync;

/// <summary>
/// 各電腦的排程（sched1；docs/sync-protocol.md 14.12）的登記：存取（<see cref="DeviceScheduleStore"/>）、給桌面的轉送迴圈（<see cref="ScheduleFanout"/>），宣告 sched1。
/// 由 <c>HubApplication</c> 在 <c>AddHubTokens</c> 之後呼叫；瀏覽器那一側（init 與 <c>schedules</c> 訊息）在瀏覽器閘道
/// </summary>
internal static class ScheduleRegistration
{
    public static IServiceCollection AddHubSchedules(this IServiceCollection services)
    {
        services.AddSingleton<DeviceScheduleStore>();
        services.AddSingleton<ScheduleFanout>();
        services.AddHostedService(sp => sp.GetRequiredService<ScheduleFanout>());
        services.AddDeviceHubFeature(DeviceFeatureRules.Sched1);
        return services;
    }
}
