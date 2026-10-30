using Myriagent.Sync;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using MyriagentHub.Rendering;
using MyriagentHub.Sync;
using MyriagentHub.Web;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器閘道的登記（WP3.6）：/app/ 的網頁供應、瀏覽器 hub（傳輸、認證、連線與速率上限）、工作階段監看、閘道（init 與推送）。
/// 傳輸設定照設計 4.1 的數值，與設計不同之處（不開 stateful reconnect、不用 CloseOnAuthenticationExpiration）見設計 14.6
/// </summary>
internal static class BrowserRegistration
{
    /// <summary>
    /// 單則訊息上限（收）與傳輸緩衝（設計 4.1 的 64 KB 由 WP4.5 D32 放寬）：16,000 個要跳脫成六個字元的字元就有 96,000 位元組，
    /// 轉送給電腦的指令本文上限 <c>MaxBodyBytes</c>（96 KB）加上呼叫框要 128 KB；<c>transport-hub.js</c> 的 <c>MAX_FRAME_BYTES</c> 要一起改
    /// </summary>
    public const int MaxMessageBytes = 128 * 1024;

    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan ClientTimeoutInterval = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddHubBrowser(this IServiceCollection services, HubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton<PrefsThemeSource>();
        services.AddSingleton<BrowserConnections>();
        services.AddSingleton<BrowserCommandHandler>();
        services.AddSingleton<BrowserHubFilter>();
        services.AddSingleton<BrowserSessionMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<BrowserSessionMonitor>());
        services.AddSingleton<UsageHistoryCache>();
        services.AddSingleton<AccountViewBuilder>();
        services.AddSingleton<IBrowserSnapshotSource>(sp => sp.GetRequiredService<AccountViewBuilder>());
        // 伺服器對話的讀取、渲染與瀏覽器指令（WP4.2 步 10；沒有開關，答 1）：閘道送清單、內容與增量，open／close／loadOlder／visible／copyItem 由伺服器處理
        services.AddSingleton<HubMediaUrls>();
        services.AddSingleton<HubItemRenderer>();
        services.AddSingleton<ConversationReader>();
        // 伺服器記的網頁未讀（步 12A、答 6）：sync.db v3 的 conversation_unread，在對話目錄之後載入；只由閘道讀寫
        services.AddSingleton<ConversationUnread>();
        services.AddSingleton<IStartupTask>(sp => new StartupTask(StartupOrder.SyncDatabase + 20, "對話未讀",
            ct => sp.GetRequiredService<ConversationUnread>().LoadAsync(ct)));
        services.AddSingleton<ConversationViews>();
        // 伺服器草稿（步 13、D29）：只存在記憶體；WP4.5 的草稿指令經 IBrowserDrafts
        services.AddSingleton<BrowserDrafts>();
        services.AddSingleton<IBrowserDrafts>(sp => sp.GetRequiredService<BrowserDrafts>());
        // 側欄的對話搜尋（標題與內容，2026-10-23）：快取各對話的原文，在背景比對
        services.AddSingleton<BrowserConversationSearch>();
        services.AddSingleton<IBrowserConversationCommands, BrowserConversationCommands>();
        services.AddSingleton<BrowserGateway>();
        services.AddHostedService(sp => sp.GetRequiredService<BrowserGateway>());
        services.AddHostedService<BrowserGatewayWiring>();
        // 多人協作（2026-10-30，sync-protocol 14.14）：擁有者的管理指令（瀏覽器與裝置 hub 共用）、名冊改變的推送；collab1 由 HubApplication 在 strat1 之後宣告
        services.AddSingleton<CollaboratorCommandService>();
        services.AddSingleton<CollaboratorFanout>();
        services.AddHostedService(sp => sp.GetRequiredService<CollaboratorFanout>());
        // 觀看追蹤（步 12、D25）：瀏覽器看得到的對話 → 擁有者的 WatchChanged
        services.AddSingleton<ConversationWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<ConversationWatch>());
        // 遠端指令（WP4.5）：control、指令路由、結果與輸出
        services.AddHubCommands();
        // AddSignalR 可以重複呼叫；Newtonsoft 協定已由 AddHubRealtime 全域登記
        services.AddSignalR().AddHubOptions<BrowserHub>(ConfigureHub);
        return services;
    }

    /// <summary>
    /// 瀏覽器 hub 只收擁有者 cookie（Owner 政策只用 cookie 方案：帶裝置權杖的請求進不來，設計 8.3）；negotiate 與升級套速率政策；
    /// 同源要求由 <see cref="OriginGuardMiddleware"/> 在認證之前就擋。另對應撤銷裝置與刪除帳戶的擁有者端點（<see cref="BrowserEndpoints"/>）
    /// </summary>
    public static void MapHubBrowser(this IEndpointRouteBuilder app)
    {
        // 擁有者與協作人（多人協作）：協作人的連線在閘道只看得到分享給他的對話
        app.MapHub<BrowserHub>(SyncProtocol.BrowserHubPath, ConfigureDispatcher)
            .RequireAuthorization(HubPolicies.Browser)
            .RequireRateLimiting(RateLimitPolicies.BrowserHub);
        BrowserEndpoints.Map(app);
    }

    /// <summary>hub 設定（設計 4.1）：單則 128 KB（WP4.5 D32）、每條連線同時一個呼叫、KeepAlive 15 秒、逾時 30 秒、只用 JSON 協定、不送例外細節</summary>
    internal static void ConfigureHub(Microsoft.AspNetCore.SignalR.HubOptions<BrowserHub> o)
    {
        o.MaximumReceiveMessageSize = MaxMessageBytes;
        o.MaximumParallelInvocationsPerClient = 1;
        o.KeepAliveInterval = KeepAliveInterval;
        o.ClientTimeoutInterval = ClientTimeoutInterval;
        o.EnableDetailedErrors = false;
        o.SupportedProtocols = ["json"];
        o.AddFilter<BrowserHubFilter>();
    }

    /// <summary>
    /// 傳輸設定：只開 WebSockets。不開 stateful reconnect（第 3 階段的訊息都是整份取代，重連後 ready 拿新的 init；每條傳輸都重新經過
    /// cookie、Origin、連線上限的檢查）。不開 CloseOnAuthenticationExpiration：它以系統時鐘比對 cookie 的期限，與注入的 TimeProvider 不一致
    /// （cookie 的期限來自 TimeProvider），到期改由 BrowserSessionMonitor 的巡檢處理
    /// </summary>
    internal static void ConfigureDispatcher(HttpConnectionDispatcherOptions o)
    {
        o.Transports = HttpTransportType.WebSockets;
        o.AllowStatefulReconnects = false;
        o.CloseOnAuthenticationExpiration = false;
        o.ApplicationMaxBufferSize = MaxMessageBytes;
        o.TransportMaxBufferSize = MaxMessageBytes;
    }
}
