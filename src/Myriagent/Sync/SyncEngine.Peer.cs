using Myriagent.Core;
using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>引擎給協調者的桌面遠端操作功能（<c>SyncEngine</c> 實作；協調者以 <c>engine as ISyncEnginePeer</c> 取得）。只把工作排進引擎執行緒</summary>
internal interface ISyncEnginePeer
{
    /// <summary>送一個這台對其他電腦下的指令（任何執行緒）：結果與輸出經 <see cref="IPeerCommandPort.DeliverPeerReply"/> 回來</summary>
    void SubmitPeerCommand(PeerCmd command);
}

/// <summary>
/// 引擎 → 協調者的桌面遠端操作（協調者的 EnginePort 實作；引擎以 <c>args.Host as IPeerCommandPort</c> 取得，沒有就不送也不收）。任何執行緒呼叫
/// </summary>
internal interface IPeerCommandPort
{
    /// <summary>伺服器送回的結果與輸出（<c>PeerReply</c>），或引擎自己合成的 <c>commandResult rejected</c>（沒有送出、伺服器的 <c>PeerCmd</c> 錯誤）</summary>
    void DeliverPeerReply(PeerReply reply);

    /// <summary>目前能不能對其他電腦下指令（這條連線交握完成而且協商出 <c>peer1</c>）；值變了才呼叫</summary>
    void ReportPeerLive(bool live);
}

// 桌面遠端操作的發起端（sync-protocol 14.7；擁有者 2026-10-22 決定「已配對就能操作」）：中樞把副本上的指令經協調者交給 SubmitPeerCommand，
// 這裡在目前這條協商出 peer1 的連線呼叫 PeerCmd。沒有這種連線時不排隊（同瀏覽器沒連線時不送），立刻合成 commandResult rejected not_sent；
// 伺服器回 accepted=false 時合成 rejected＋那個錯誤碼（origin 是 transport，同瀏覽器的 transport-hub）；呼叫因斷線失敗時不合成
// （伺服器可能已經處理，網頁 150 秒等不到就當結果不明）。PeerReply 只檢查 commandId 與 message.type，內容交給中樞轉給發起的檢視。
// 狀態只在引擎執行緒讀寫
internal sealed partial class SyncEngine : ISyncEnginePeer
{
    private IPeerCommandPort? PeerPort => args.Host as IPeerCommandPort;

    /// <summary>上一次回報的「能不能送」（引擎執行緒）</summary>
    private bool peerLiveReported;

    public void SubmitPeerCommand(PeerCmd command)
    {
        ArgumentNullException.ThrowIfNull(command);
        thread.Post(() => OnSubmitPeerCommand(command));
    }

    private void OnSubmitPeerCommand(PeerCmd command)
    {
        if (PeerPort is not { } port)
            return;
        var conn = current;
        if (conn is not { Proven: true, Session: not null } || !DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Peer1))
        {
            port.DeliverPeerReply(PeerCommandRules.Synthesized(command.CommandId, PeerCommandRules.NotSent));
            return;
        }

        Spawn(async () =>
        {
            PeerCmdResult? result;
            try
            {
                result = await conn.Session!.PeerCmdAsync(command, conn.Token);
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                // 太大或伺服器判定格式錯誤：這一則沒有處理，不重連
                Log.Warn($"伺服器拒收桌面遠端操作的指令 {command.CommandId}（{ex.Failure.Code}）");
                port.DeliverPeerReply(PeerCommandRules.Synthesized(command.CommandId,
                    ex.Failure.Code == SyncErrorCodes.TooLarge ? PeerCommandRules.NotSent : RemoteResultCodes.Invalid));
                return;
            }
            catch (SyncTransportException ex)
            {
                Fail(conn, ex.Failure);
                return;
            }

            if (result is null || !result.Accepted)
                port.DeliverPeerReply(PeerCommandRules.Synthesized(command.CommandId, result?.Error));
        }, conn);
    }

    /// <summary><c>peer1</c> 的 <c>PeerReply</c>：commandId 是 ULID、message 是有字串 type 的物件才交給協調者</summary>
    private void OnPeerReply(SyncConnection conn, PeerReply reply)
    {
        if (PeerPort is not { } port)
            return;
        if (!PeerCommandRules.IsValidReply(reply))
        {
            Log.Warn("伺服器送來格式不合格的 PeerReply：略過");
            return;
        }
        port.DeliverPeerReply(reply);
    }

    /// <summary>交握完成或連線丟掉之後：這條連線能不能對其他電腦下指令，值變了才回報</summary>
    private void ReportPeerLive(SyncConnection? conn)
    {
        if (PeerPort is not { } port)
            return;
        var live = conn is { Proven: true } && ReferenceEquals(conn, current) && DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Peer1);
        if (live == peerLiveReported)
            return;
        peerLiveReported = live;
        try
        {
            port.ReportPeerLive(live);
        }
        catch (Exception ex)
        {
            Log.Error("回報桌面遠端操作能不能用失敗", ex);
        }
    }
}

/// <summary>桌面遠端操作的引擎規則（sync-protocol 14.7）</summary>
internal static class PeerCommandRules
{
    /// <summary>沒有送出（沒有連線、伺服器不支援、太大）：同瀏覽器 transport-hub 的 <c>not_sent</c></summary>
    public const string NotSent = "not_sent";

    /// <summary>
    /// 引擎合成的 <c>commandResult</c>（<c>origin: "transport"</c>，web-protocol 第六節第 13 點）：<paramref name="code"/> 是伺服器 <c>PeerCmd</c> 的錯誤碼
    /// 或 <see cref="NotSent"/>；null 或空字串當成 <c>invalid</c>
    /// </summary>
    public static PeerReply Synthesized(string commandId, string? code) => new(commandId, new JObject
    {
        ["type"] = "commandResult",
        ["commandId"] = commandId,
        ["state"] = RemoteResultStates.Rejected,
        ["code"] = string.IsNullOrEmpty(code) ? RemoteResultCodes.Invalid : code,
        ["message"] = null,
        ["tier"] = null,
        ["origin"] = "transport",
    });

    /// <summary>伺服器送來的 <c>PeerReply</c>：commandId 是 ULID、message 是有非空字串 <c>type</c> 的物件</summary>
    public static bool IsValidReply(PeerReply? reply)
        => reply is { Message: not null } && SyncIds.IsUlid(reply.CommandId)
           && reply.Message["type"] is JValue { Type: JTokenType.String, Value: string { Length: > 0 } };
}
