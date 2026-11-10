using System.Diagnostics;
using System.Security.Authentication;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Handoff;
using MyriagentHub.Logs;
using MyriagentHub.Media;
using MyriagentHub.Ops;
using MyriagentHub.Pairing;
using MyriagentHub.Realtime;
using MyriagentHub.Releases;
using MyriagentHub.Sync;
using MyriagentHub.Tls;
using MyriagentHub.Trust;
using MyriagentHub.Web;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Hosting;

/// <summary>
/// 組合根（13.0 的登記點規則）：這裡只呼叫各模組的 AddXxx()／MapXxx()。Program 與測試共用同一個建立流程；
/// 測試以 <paramref name="configure"/> 換 TimeProvider、改用 TestServer。
/// </summary>
internal static class HubApplication
{
    /// <summary>
    /// 每條連線最多先從網路收多少還沒處理的位元組（socket 傳輸層的 <c>SocketTransportOptions.MaxReadBufferSize</c>，預設 1 MB；
    /// S9 閒置記憶體的修正，設計 14.20）。媒體轉送暫停時（瀏覽器讀得慢）伺服器不讀裝置的 PUT，HTTPS 的 HTTP/1.1 連線在 TLS 之下的傳輸層
    /// 原本先收到 1 MB，64 個轉送就是 64 MB 的 Kestrel 4 KB 區塊；轉送結束之後區塊回到 Kestrel 的池，.NET 10 的池自己淘汰得很慢
    /// （每 10 秒 1～5%），淘汰的區塊又在不壓縮的 pinned 物件堆積裡：S9 的 10 台在閒置修剪之後還留著 13,501 個、55.6 MB（heap dump）。
    /// 64 KB 等於 Kestrel 送出的預設緩衝（<c>MaxWriteBufferSize</c>）；Kestrel 的 <c>Limits.MaxRequestBufferSize</c> 管不到這一層（HTTPS 的 HTTP/1.1 直接讀傳輸層）
    /// </summary>
    internal const int TransportReadBufferBytes = 64 * 1024;

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        // 以服務執行時工作目錄是 C:\Windows\System32：內容根目錄與網頁根目錄一律是程式所在資料夾
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        // 資料根（--data）與資料根裡的設定檔（在發佈資料夾外，換版不會被覆蓋）；命令列仍然最優先
        var paths = new HubPaths(HostingRules.ResolveDataDir(
            builder.Configuration["data"],
            builder.Configuration["Hub:DataDir"],
            builder.Environment.IsProduction(),
            builder.Environment.ContentRootPath,
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)));
        builder.Configuration.AddJsonFile(Path.Combine(paths.ConfigDir, "appsettings.json"), optional: true, reloadOnChange: false);
        builder.Configuration.AddJsonFile(Path.Combine(paths.ConfigDir, $"appsettings.{builder.Environment.EnvironmentName}.json"), optional: true, reloadOnChange: false);
        builder.Configuration.AddCommandLine(args);
        var options = builder.Configuration.GetSection(HubOptions.Section).Get<HubOptions>() ?? new HubOptions();
        var publicOrigin = HostingRules.PublicOrigin(options);
        DrainRules.ValidateDeployDrainMaxSeconds(options.Deploy.DrainMaxSeconds);
        if (AppReleaseRules.ValidateSettings(options.Releases) is { } releaseSettingsError)
            throw new InvalidOperationException(releaseSettingsError);
        paths.EnsureCreated();

        builder.Services.AddWindowsService(o => o.ServiceName = "MyriagentHub");
        // SCM 停止時 30 秒內優雅結束（docs/hub-setup.md 第 11 節；安裝腳本 Stop-Service 等 30 秒）：WindowsServiceLifetime 的
        // OnStop 最多等主機這麼久才回報已停止，留 5 秒給程序本身結束
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = HostingRules.ShutdownTimeout);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.TryAddSingleton(TimeProvider.System);
        if (options.FileLog.Enabled)
            builder.Logging.AddProvider(new JsonFileLoggerProvider(paths.DiagDir, options.FileLog, TimeProvider.System));

        builder.Services.AddHostedService<StartupSequence>();
        builder.Services.AddSingleton(_ => ServiceLock.Acquire(paths, "server"));
        builder.Services.AddSingleton<IStartupTask>(sp => new StartupTask(StartupOrder.Paths, "資料根鎖定與程序優先權", _ =>
        {
            sp.GetRequiredService<ServiceLock>();
            if (options.BelowNormalPriority)
            {
                using var process = Process.GetCurrentProcess();
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            return Task.CompletedTask;
        }));

        builder.Services.AddHubData();
        builder.Services.AddHubSync();
        builder.Services.AddHubAudit();
        builder.Services.AddHubAuth(options, paths);
        builder.Services.AddHubTrust();
        builder.Services.AddHubRealtime(options);
        builder.Services.AddHubPairing();
        builder.Services.AddHubReleases(options);
        builder.Services.AddHubRateLimiting(options.RateLimits);
        builder.Services.AddHubTls(HostingRules.UseCertificateStore(options.Tls.CertStore, builder.Environment.IsProduction()));
        builder.Services.AddHubOps();
        builder.Services.AddHubBrowser(options);
        // 在 AddHubSync 之後：以 AddSingleton 換掉 WP4.1 的空 IBlobStore（後登記的為準）
        builder.Services.AddHubMedia(options);
        // 各電腦的即時狀態（sync-protocol 14.8）：stat1 排在 features 的最後
        builder.Services.AddHubStats();
        // 各帳戶的 token 計數（sync-protocol 14.9、14.10）：tok1、tok2 排在 stat1 之後
        builder.Services.AddHubTokens();
        // 各電腦的排程（sched1，docs/sync-protocol.md 14.12）：排在 tok1、tok2 之後
        builder.Services.AddHubSchedules();
        // 輸入框的草稿（draft1，sync-protocol 14.17）
        builder.Services.AddHubComposerDrafts();
        // 臨時對話（2026-11-11）：帶 ephemeral 的 conversationCreate 只送給協商出 quick1 的電腦（CommandRouter）
        builder.Services.AddDeviceHubFeature(Myriagent.Sync.DeviceFeatureRules.Quick1);
        // 多人協作（sync-protocol 14.14）：協作人發起的信封、CollabCmd、CollabUpdate；服務在 AddHubBrowser 登記
        builder.Services.AddDeviceHubFeature(Myriagent.Sync.DeviceFeatureRules.Collab1);
        // 電腦狀態面板的檔案傳輸（2026-11-09）：只把 deviceBrowse、deviceFetch、devicePut 送給協商出 xfer1 的連線
        builder.Services.AddDeviceHubFeature(Myriagent.Sync.DeviceFeatureRules.Xfer1);
        builder.Services.AddDeviceHubFeature(Myriagent.Sync.DeviceFeatureRules.Xfer2);
        // 遠端桌面（sync-protocol 14.16）：deskOpen、DeskUp／DeskDown（Sync/DeskRelays.cs）
        builder.Services.AddDeviceHubFeature(Myriagent.Sync.DeviceFeatureRules.Desk1);
        // 第 5 階段（sync-protocol 第 16 節）：log1、hand1 排在 tok1 之後，各自依開關登記（預設都關）
        builder.Services.AddHubLogs(options);
        builder.Services.AddHubHandoff(options);
        builder.WebHost.ConfigureHubKestrel(options);

        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseHubPipeline(publicOrigin);
        app.MapHubAuth();
        app.MapHubOps();
        app.MapHubTrust();
        app.MapHubRealtime();
        app.MapHubPairing();
        app.MapHubBrowser();
        app.MapHubMedia();
        app.MapHubReleases();
        app.MapHubLogs();
        app.MapHubHandoff();
        app.MapHubFallbacks();
        return app;
    }

    /// <summary>
    /// 中介程式順序：閒置修剪的活動（最外層：每個請求都算，健康檢查與 /hubs 除外）→ 安全標頭（連 500 也帶）→ 5xx 計數與例外 → 瀏覽器介面 /app/（嵌入的桌面網頁）→
    /// 靜態檔（不認證、不查資料庫）→ WebSocket 功能與瀏覽器 hub 的壓縮 → 路由 → Origin／CSRF（拒絕時不做任何認證）→ 登入端點拿掉舊工作階段 cookie（登入一定換新 key）→ 認證 →
    /// 速率限制（已認證的不受 IP 限制，所以在認證之後）→ 授權 → 端點。
    /// </summary>
    private static void UseHubPipeline(this WebApplication app, string publicOrigin)
    {
        app.UseMiddleware<HubActivityMiddleware>();
        app.UseMiddleware<SecurityHeadersMiddleware>(publicOrigin);
        app.UseMiddleware<ServerErrorCounterMiddleware>();
        app.UseHubBrowserAssets();
        app.UseDefaultFiles(new DefaultFilesOptions { DefaultFileNames = ["auth.html"] });
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
                SecurityHeaderRules.CacheControlFor(context.Context.Request.Path.Value ?? context.File.Name, isStaticFile: true),
        });
        // 瀏覽器 hub 的 WebSocket 壓縮（丙）：外層先掛 WebSocket 功能，再把瀏覽器 hub 的換成會壓縮的；SignalR 端點裡那一層看到已經有就不另建
        app.UseWebSockets();
        app.UseMiddleware<BrowserWebSocketCompressionMiddleware>();
        app.UseRouting();
        app.UseMiddleware<OriginGuardMiddleware>(publicOrigin);
        app.UseMiddleware<SessionRotation.Middleware>();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
    }

    /// <summary>
    /// Kestrel（8.5、9.1）：ListenAnyIP 雙堆疊、只允許 TLS 1.2／1.3、交握 10 秒、連線與標頭上限、傳輸層每條連線先收最多 64 KB（<see cref="TransportReadBufferBytes"/>）；
    /// TLS-ALPN-01 走同一個埠（OnAuthenticate 換驗證憑證），一般連線用含中繼憑證的 context（熱換不重啟）。不開 80、不用 HTTP.sys。
    /// </summary>
    private static void ConfigureHubKestrel(this IWebHostBuilder webHost, HubOptions options)
    {
        var (kind, address) = HostingRules.ParseBindAddress(options.BindAddress);
        webHost.UseSockets(sockets => sockets.MaxReadBufferSize = TransportReadBufferBytes);
        webHost.ConfigureKestrel((_, kestrel) =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = 512;
            kestrel.Limits.MaxConcurrentUpgradedConnections = 256;
            kestrel.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
            kestrel.Limits.MinRequestBodyDataRate = new MinDataRate(240, TimeSpan.FromSeconds(10));
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024;

            var services = kestrel.ApplicationServices;
            var alpn = services.GetRequiredService<TlsAlpnChallengeStore>();
            var holder = services.GetRequiredService<CertificateHolder>();
            var metrics = services.GetRequiredService<OpsMetrics>();
            void Configure(ListenOptions listen)
            {
                listen.Use(next => async connection =>
                {
                    metrics.ConnectionOpened();
                    try
                    {
                        await next(connection);
                    }
                    finally
                    {
                        metrics.ConnectionClosed();
                    }
                });
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    HandshakeTimeout = TimeSpan.FromSeconds(10),
                    ServerCertificateSelector = (_, _) => holder.Current?.Certificate,
                    TlsClientHelloBytesCallback = alpn.InspectClientHello,
                    OnAuthenticate = alpn.OnAuthenticate,
                });
            }

            switch (kind)
            {
                case BindKind.Any:
                    kestrel.ListenAnyIP(options.HttpsPort, Configure);
                    break;
                case BindKind.Localhost:
                    kestrel.ListenLocalhost(options.HttpsPort, Configure);
                    break;
                default:
                    kestrel.Listen(address!, options.HttpsPort, Configure);
                    break;
            }
        });
    }
}
