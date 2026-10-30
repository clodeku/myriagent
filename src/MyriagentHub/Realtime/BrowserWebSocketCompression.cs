using System.Net.WebSockets;
using Myriagent.Sync;
using MyriagentHub.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Realtime;

/// <summary>瀏覽器 hub 的 WebSocket 壓縮（permessage-deflate，丙；設計 docs/plans/2026-10-23-live-delta-and-replica-live.md 第 5 節）</summary>
internal enum WebSocketCompressionMode
{
    /// <summary>不壓縮（上線前的行為；設定寫錯也是這個）</summary>
    Off,

    /// <summary>每則訊息各自壓縮（伺服器不保留前後訊息的字典）：旁通道只剩同一則訊息內（擁有者 2026-10-23 選的，Q4 (b)）</summary>
    NoContextTakeover,

    /// <summary>前後訊息共用字典（壓縮率較高，旁通道的面較大）</summary>
    ContextTakeover,
}

/// <summary>
/// 壓縮的純規則：設定值 <c>Hub:Browser:WebSocketCompression</c> 的解讀、哪些請求適用、接受升級時怎麼設。
/// 只有瀏覽器 hub（<see cref="SyncProtocol.BrowserHubPath"/>）壓縮；裝置 hub 一律不壓（指令信封、媒體請求、帳戶操作都走那裡，增量之後流量也小）
/// </summary>
internal static class WebSocketCompressionRules
{
    public const string Off = "off";
    public const string NoContextTakeover = "noContextTakeover";
    public const string ContextTakeover = "contextTakeover";

    /// <summary>壓縮視窗：不降（9 bits 時同一串流的壓縮幾乎失效，設計 0.3 表）</summary>
    public const int ServerMaxWindowBits = 15;

    /// <summary>設定值（不分大小寫、前後空白忽略）→ 模式；不認得的一律 <see cref="WebSocketCompressionMode.Off"/>（寫錯時退回上線前的行為）</summary>
    public static WebSocketCompressionMode Parse(string? value) => value?.Trim() switch
    {
        { } v when v.Equals(NoContextTakeover, StringComparison.OrdinalIgnoreCase) => WebSocketCompressionMode.NoContextTakeover,
        { } v when v.Equals(ContextTakeover, StringComparison.OrdinalIgnoreCase) => WebSocketCompressionMode.ContextTakeover,
        _ => WebSocketCompressionMode.Off,
    };

    /// <summary>這個請求要不要換成會壓縮的 WebSocket 功能：模式不是 Off、路徑是瀏覽器 hub（含其下的子路徑）</summary>
    public static bool Applies(PathString path, WebSocketCompressionMode mode) =>
        mode != WebSocketCompressionMode.Off && path.StartsWithSegments(SyncProtocol.BrowserHubPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 接受升級時的設定：<b>新的</b> <see cref="WebSocketAcceptContext"/>，照抄 SignalR 給的每個欄位（子協定、keep-alive）再加壓縮
    /// （用戶端沒提出 permessage-deflate 時框架照舊不壓縮）。絕不改傳進來的那一個：SignalR 在同一個程序裡共用它，改了會漏到之後的每一次升級
    /// （裝置 hub、設成 off 的主機都被開了壓縮——P0 實測抓到的）。<c>Off</c> 也回新的複本
    /// </summary>
    public static WebSocketAcceptContext Configure(WebSocketAcceptContext context, WebSocketCompressionMode mode)
    {
        ArgumentNullException.ThrowIfNull(context);
        var copy = new WebSocketAcceptContext
        {
            SubProtocol = context.SubProtocol,
            KeepAliveInterval = context.KeepAliveInterval,
            KeepAliveTimeout = context.KeepAliveTimeout,
            DangerousEnableCompression = context.DangerousEnableCompression,
            DisableServerContextTakeover = context.DisableServerContextTakeover,
            ServerMaxWindowBits = context.ServerMaxWindowBits,
        };
        if (mode == WebSocketCompressionMode.Off)
            return copy;
        copy.DangerousEnableCompression = true;
        copy.DisableServerContextTakeover = mode == WebSocketCompressionMode.NoContextTakeover;
        copy.ServerMaxWindowBits = ServerMaxWindowBits;
        return copy;
    }
}

/// <summary>包住外層 <c>UseWebSockets</c> 的功能：SignalR 接受升級時照它給的子協定與 keep-alive，另加壓縮設定</summary>
internal sealed class CompressingWebSocketFeature(IHttpWebSocketFeature inner, WebSocketCompressionMode mode) : IHttpWebSocketFeature
{
    public bool IsWebSocketRequest => inner.IsWebSocketRequest;

    public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => inner.AcceptAsync(WebSocketCompressionRules.Configure(context, mode));
}

/// <summary>
/// 管線在外層 <c>UseWebSockets</c> 之後、路由之前：瀏覽器 hub 的升級請求換成 <see cref="CompressingWebSocketFeature"/>。
/// SignalR 端點的子管線自己也掛一次 WebSocket 中介軟體，它看到功能已經存在就不另建，所以用的是這裡換上的（gateway.mjs 與
/// <c>BrowserWebSocketCompressionTests</c> 以回應的 <c>Sec-WebSocket-Extensions</c> 確認）
/// </summary>
internal sealed class BrowserWebSocketCompressionMiddleware(RequestDelegate next, IOptions<HubOptions> options)
{
    private readonly WebSocketCompressionMode mode = WebSocketCompressionRules.Parse(options.Value.Browser.WebSocketCompression);

    public Task InvokeAsync(HttpContext context)
    {
        if (WebSocketCompressionRules.Applies(context.Request.Path, mode)
            && context.Features.Get<IHttpWebSocketFeature>() is { IsWebSocketRequest: true } inner)
            context.Features.Set<IHttpWebSocketFeature>(new CompressingWebSocketFeature(inner, mode));
        return next(context);
    }
}
