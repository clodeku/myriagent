using Myriagent.Sync;
using MyriagentHub.Realtime;

namespace MyriagentHub.Sync;

/// <summary>
/// 各帳戶的 token 計數（tok1、tok2；docs/sync-protocol.md 14.9、14.10）的登記：寫入者（<see cref="TokenTallies"/>）、給桌面的轉送迴圈
/// （<see cref="TokenTallyFanout"/>），宣告 tok1、tok2。由 <c>HubApplication</c> 在 <c>AddHubStats</c> 之後呼叫（tok1、tok2 排在 HelloResponse.features 的最後）；
/// 瀏覽器那一側（init 與 <c>tokens</c> 訊息）在瀏覽器閘道
/// </summary>
internal static class TokenRegistration
{
    public static IServiceCollection AddHubTokens(this IServiceCollection services)
    {
        services.AddSingleton<TokenTallies>();
        services.AddSingleton<TokenTallyFanout>();
        services.AddHostedService(sp => sp.GetRequiredService<TokenTallyFanout>());
        services.AddDeviceHubFeature(DeviceFeatureRules.Tok1);
        services.AddDeviceHubFeature(DeviceFeatureRules.Tok2);
        return services;
    }
}
