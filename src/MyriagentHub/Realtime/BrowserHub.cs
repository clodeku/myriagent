using System.Security.Claims;
using MyriagentHub.Auth;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>瀏覽器 hub 的呼叫者：只認擁有者 cookie 的身分（有工作階段 id、沒有裝置權杖的 claim）</summary>
internal static class BrowserCaller
{
    /// <summary>擁有者工作階段的 id；裝置權杖或沒有工作階段的身分回 null</summary>
    public static string? SessionId(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return null;
        if (user.FindFirst(HubClaims.DeviceId) is not null || user.FindFirst(HubClaims.TokenKind) is not null)
            return null;
        return user.FindFirst(HubClaims.SessionId)?.Value is { Length: > 0 } sid ? sid : null;
    }
}

/// <summary>
/// 瀏覽器 hub（<c>/hubs/v1/browser</c>，設計 4.9、14.6；契約見 docs/web-protocol.md 第六節）：擁有者 cookie 認證、Origin 必須是本站
/// （<see cref="OriginGuardMiddleware"/>，認證之前就擋）、只開 WebSockets、不開 stateful reconnect。
/// 方法只有 <c>Cmd</c> 與 <c>ClientState</c>（<see cref="BrowserHubFilter"/> 的允許清單）；伺服器→瀏覽器一律 <c>Msg</c>，經每條連線的送出佇列。
/// 宣告成 partial：第 4 階段（WP4.5）在自己的檔案擴充（設計 13.0、13.4）。
/// </summary>
internal sealed partial class BrowserHub(
    BrowserConnections connections,
    BrowserCommandHandler handler,
    BrowserSessionMonitor sessions,
    IHubContext<BrowserHub> hubContext,
    CollaboratorDirectory collaborators,
    MyriagentHub.Sync.DeskRelays desk,
    ILogger<BrowserHub> log) : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Owner 政策只用 cookie 方案，這裡再擋一次：裝置權杖或沒有工作階段的身分不能連瀏覽器 hub（設計 8.3）
        if (BrowserCaller.SessionId(Context.User) is not { } sessionId)
        {
            log.LogWarning("瀏覽器 hub 的連線沒有擁有者工作階段，中斷：{Connection}", Context.ConnectionId);
            Context.Abort();
            return;
        }
        // 協作人（多人協作）：名冊裡啟用中才收（停用、刪除時另外撤銷工作階段並中斷）
        var collaboratorId = Context.User.CollaboratorId();
        if (collaboratorId is not null && !collaborators.IsActive(collaboratorId))
        {
            log.LogInformation("瀏覽器 hub 的協作人已停用或不在名冊，中斷：{Connection}", Context.ConnectionId);
            Context.Abort();
            return;
        }
        var connectionId = Context.ConnectionId;
        var clients = hubContext.Clients;
        // 每個部分只序列化一次（各連線共用同一份字串），以 JRaw 原樣寫進 SignalR 的訊息
        var (outcome, _) = await connections.AdmitAsync(connectionId, sessionId, Context.GetHttpContext()?.ClientIp(), Context.Abort,
            (json, cancellationToken) => clients.Client(connectionId).SendAsync(BrowserHubMethods.Msg, new JRaw(json), cancellationToken),
            cancellationToken => sessions.IsSessionValidAsync(sessionId, cancellationToken),
            Context.ConnectionAborted, collaboratorId);
        switch (outcome)
        {
            case AdmitOutcome.Admitted:
                return;
            case AdmitOutcome.TooManyConnections:
                // OnConnectedAsync 丟出的 HubException 會成為關閉訊息（EnableDetailedErrors=false 時也帶 HubException 的訊息）
                log.LogWarning("瀏覽器 hub 的連線超過上限：{Session}", SessionRules.ShortId(sessionId));
                throw new HubException(BrowserErrorCodes.TooManyConnections);
            case AdmitOutcome.SessionInvalid:
                // 認證之後、登記之前被撤銷：AdmitAsync 已移除並中斷
                log.LogInformation("瀏覽器 hub 的連線登記時工作階段已失效：{Session}", SessionRules.ShortId(sessionId));
                return;
            case AdmitOutcome.ShuttingDown:
                // 停機中（不帶原因關閉，網頁照退避重連；web-protocol 第六節第 7 點）
                log.LogInformation("停機中，不登記瀏覽器 hub 的連線：{Connection}", connectionId);
                Context.Abort();
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "未知的連線登記結果");
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var connection = connections.Get(Context.ConnectionId);
        connections.Remove(Context.ConnectionId);
        // 遠端桌面（14.16）：這條連線在看的工作階段替它結束（被看的電腦停止擷取、拿掉提示列）
        if (connection is not null)
            await desk.BrowserGoneAsync(connection);
    }

    /// <summary><c>Cmd {commandId?, envelope?, cmd}</c> → <c>{accepted, commandId?, error?}</c>（web-protocol 第六節第 4 點）</summary>
    public Task<CmdResult> Cmd(JToken? request)
    {
        var connection = connections.Get(Context.ConnectionId) ?? throw new HubException(BrowserErrorCodes.Invalid);
        return handler.HandleAsync(connection, MessageGuard.ParseCmd(request), Context.ConnectionAborted);
    }

    /// <summary><c>ClientState {visible, focused, openConversationIds, tzOffsetMinutes}</c>：不合格丟 HubException("invalid")；第 3 階段只驗證並保存</summary>
    public Task ClientState(JToken? state)
    {
        var connection = connections.Get(Context.ConnectionId) ?? throw new HubException(BrowserErrorCodes.Invalid);
        connection.SetState(MessageGuard.ParseClientState(state) ?? throw new HubException(BrowserErrorCodes.Invalid));
        return Task.CompletedTask;
    }
}
