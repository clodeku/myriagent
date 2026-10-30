using Myriagent.Sync;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Realtime;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MyriagentHub.Sync;

/// <summary>
/// 遠端指令（WP4.5）的登記：由 <c>AddHubBrowser</c> 呼叫（一行；<c>HubApplication</c> 不動，契約 D52、WP4.5 計畫 D39）。
/// 每個服務都以 TryAdd 登記：測試可以先 <c>RemoveAll</c> 再換掉，或在之後以 <c>AddSingleton</c> 覆蓋；重複呼叫不會登記兩份
/// </summary>
internal static class CommandRegistration
{
    public static IServiceCollection AddHubCommands(this IServiceCollection services)
    {
        // 啟動工作（D28）：control 的來源與改密碼的推送（步 5）
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupTask, CommandStartup>());
        // 指令路由（步 6）：宣告 cmd1（AddHubRealtime 先宣告 conv1、live1；DeviceHubFeatures 依登記順序去重）
        services.AddDeviceHubFeature(DeviceFeatureRules.Cmd1);
        services.TryAddSingleton<CommandTracker>();
        services.TryAddSingleton<CommandStore>();
        services.TryAddSingleton<CommandIssuerResolver>();
        services.TryAddSingleton<CommandCounters>();
        // WP4.7 的接縫（上傳檢查、檔案指令輸出的改寫）：TryAdd 的預設，AddHubMedia 之後以 AddSingleton 換成實作（後登記的為準）
        services.AddMediaSeams();
        services.TryAddSingleton<CommandReplies>();
        services.TryAddSingleton<CommandRouter>();
        services.TryAddSingleton<IBrowserCommandRouter>(sp => sp.GetRequiredService<CommandRouter>());
        // 桌面遠端操作（sync-protocol 14.7）：宣告 peer1，裝置 hub 的 PeerCmd 以方法參數取用
        services.AddDeviceHubFeature(DeviceFeatureRules.Peer1);
        services.TryAddSingleton<PeerCommandHandler>();
        // 遠端執行指令（sync-protocol 14.11）：宣告 exec1；remoteExec 照上面的路由簽發，輸出與停止經 ExecRelays 轉送（裝置 hub 的 ExecOutput／ExecCancel）
        services.AddDeviceHubFeature(DeviceFeatureRules.Exec1);
        services.TryAddSingleton<ExecRelays>();
        // 結果與輸出（步 7）：裝置 hub 的 CommandResult／CommandOutput 以方法參數取用；100 秒期限的維護迴圈
        services.TryAddSingleton<CommandResults>();
        services.TryAddSingleton<CommandMaintenance>();
        services.AddHostedService(sp => sp.GetRequiredService<CommandMaintenance>());
        // durable（步 8）：所有電腦登出的重簽、補送與 busy 重送；啟動工作訂閱裝置連線的事件
        services.TryAddSingleton<DurableCommands>();
        services.AddHostedService(sp => sp.GetRequiredService<DurableCommands>());
        return services;
    }
}
