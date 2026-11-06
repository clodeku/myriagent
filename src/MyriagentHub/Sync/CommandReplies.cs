using Myriagent.Sync;
using MyriagentHub.Realtime;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 把遠端指令的結果與輸出送回發起者（sync-protocol 14.6、14.7）：
/// <list type="bullet">
/// <item>瀏覽器：照舊經 <see cref="BrowserGateway.SendAfterInit"/>／<see cref="BrowserGateway.SendToast"/>（送過 init 才送、toast 10 秒節流）；連線不在了就不送。</item>
/// <item>裝置：<c>PeerReply {commandId, message}</c> 送給發起電腦目前 <c>Order</c> 最大、已證明、協商出 <c>peer1</c> 的連線
/// （重連之後照樣收得到；沒有這種連線就丟掉，桌面等不到就當結果不明）。toast 不節流（桌面自己決定怎麼提示）。</item>
/// </list>
/// 任何執行緒都能呼叫；送出不等待（失敗只記錄）
/// </summary>
internal sealed class CommandReplies(
    BrowserConnections browsers,
    BrowserGateway gateway,
    ConnectionRegistry registry,
    IDeviceMessageSender sender,
    ILogger<CommandReplies> log)
{
    /// <summary>給發起者一則網頁訊息；<paramref name="originCommandId"/> 是發起時帶的 commandId（瀏覽器的、或桌面的）</summary>
    public void Send(CommandOrigin origin, string? originCommandId, ClientOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(message);
        switch (origin)
        {
            case BrowserCommandOrigin browser:
                gateway.SendAfterInit(browser.Connection, message);
                break;
            case DeviceCommandOrigin device:
                SendPeer(device.Device, originCommandId, message);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(origin), origin.GetType().Name, "未知的發起者");
        }
    }

    /// <summary>給發起者一則 toast（瀏覽器照 <paramref name="cmd"/> 的 10 秒節流）</summary>
    public void SendToast(CommandOrigin origin, string? originCommandId, string cmd, ClientOutboxMessage toast)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(toast);
        switch (origin)
        {
            case BrowserCommandOrigin browser:
                gateway.SendToast(browser.Connection, cmd, toast);
                break;
            case DeviceCommandOrigin device:
                SendPeer(device.Device, originCommandId, toast);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(origin), origin.GetType().Name, "未知的發起者");
        }
    }

    /// <summary>追蹤中的指令：發起的瀏覽器連線（還在的話）或發起的電腦</summary>
    public void Send(TrackedCommand tracked, ClientOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        if (tracked.OriginDeviceId is { } device)
            SendPeer(device, tracked.BrowserCommandId, message);
        else if (BrowserOf(tracked) is { } browser)
            gateway.SendAfterInit(browser, message);
    }

    /// <summary>追蹤中的指令的 toast（瀏覽器照指令名稱節流）</summary>
    public void SendToast(TrackedCommand tracked, ClientOutboxMessage toast)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        if (tracked.OriginDeviceId is { } device)
            SendPeer(device, tracked.BrowserCommandId, toast);
        else if (BrowserOf(tracked) is { } browser)
            gateway.SendToast(browser, tracked.CmdName, toast);
    }

    /// <summary>
    /// 發起的瀏覽器連線（還在的話）。協作人在指令執行途中被取消分享（或停用）：之後才到的結果與輸出（檔案清單、搜尋結果）不再給他（2026-11-07 資安檢查）
    /// </summary>
    private BrowserConnection? BrowserOf(TrackedCommand tracked)
    {
        if (browsers.Get(tracked.BrowserConnectionId) is not { } browser)
            return null;
        if (browser.IsCollaborator && (tracked.ExpectedConversationId ?? tracked.BrowserConversationId) is { } conversationId
            && !gateway.CanSeeConversation(browser, conversationId))
            return null;
        return browser;
    }

    /// <summary>發起者還在嗎（瀏覽器：連線還在；裝置：一律當成在，送的時候才選連線）</summary>
    public bool IsPresent(TrackedCommand tracked)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        return tracked.OriginDeviceId is not null || browsers.Get(tracked.BrowserConnectionId) is not null;
    }

    private void SendPeer(string deviceId, string? commandId, ClientOutboxMessage message)
    {
        if (string.IsNullOrEmpty(commandId))
            return;
        var connection = PeerRules.ReplyConnection(registry.ConnectionsOf(deviceId));
        if (connection is null)
        {
            log.LogDebug("電腦 {DeviceId} 沒有可以收 PeerReply 的連線，丟掉 {Type}（{CommandId}）", deviceId, message.Type, commandId);
            return;
        }
        JObject body;
        try
        {
            body = JObject.Parse(message.Json);
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            log.LogError(ex, "要送給電腦 {DeviceId} 的 {Type} 不是 JSON 物件（程式錯誤）", deviceId, message.Type);
            return;
        }
        var reply = new PeerReply(commandId, body);
        if (!PeerRules.Fits(reply))
        {
            log.LogWarning("要送給電腦 {DeviceId} 的 {Type}（{CommandId}）超過單則訊息上限，丟掉", deviceId, message.Type, commandId);
            return;
        }
        _ = SendAsync(connection, reply, message.Type);
    }

    private async Task SendAsync(string connectionId, PeerReply reply, string type)
    {
        try
        {
            await sender.SendAsync(connectionId, DeviceHubMethods.PeerReply, reply, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "PeerReply（{Type}、{CommandId}）送不到電腦的連線 {Connection}", type, reply.CommandId, connectionId);
        }
    }
}
