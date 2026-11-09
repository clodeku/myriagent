using Myriagent.Core;
using Myriagent.Remote;

namespace Myriagent.Sync;

/// <summary>引擎給協調者的遠端桌面功能（<c>SyncEngine</c> 實作；協調者以 <c>engine as ISyncEngineDesk</c> 取得）。只把工作排進引擎執行緒</summary>
internal interface ISyncEngineDesk
{
    /// <summary>送一個封包（任何執行緒；依呼叫的順序送）：被看的一方的影格與狀態、看的一方的輸入與確認</summary>
    void SubmitDeskUp(DeskPacket packet);
}

/// <summary>引擎 → 協調者的遠端桌面（協調者的 EnginePort 實作；沒有就不宣告 desk1）。任何執行緒呼叫</summary>
internal interface IDeskPort
{
    /// <summary>宿主接得住遠端桌面（false 時不宣告 desk1）</summary>
    bool DeskSupported { get; }

    /// <summary>伺服器轉來的封包（<c>DeskDown</c>）</summary>
    void DeliverDeskDown(DeskPacket packet);

    /// <summary>伺服器不再轉送這個工作階段（<c>DeskUp</c> 回 keep false），或連線斷了</summary>
    void DeskDropped(string sessionId);

    /// <summary>目前能不能搬遠端桌面的封包（這條連線交握完成而且協商出 desk1）；值變了才呼叫。false 時所有工作階段都已經斷了</summary>
    void ReportDeskLive(bool live);
}

// 遠端桌面的引擎部分（docs/sync-protocol.md 14.16，desk1）：deskOpen 本身照一般的遠端指令；這裡只搬封包。
// 一次一則以 DeskUp 送（等前一則的回應才送下一則，順序不亂）；回 keep false 就告訴協調者那個工作階段斷了、排著的同一個工作階段的封包丟掉。
// 排著的太多（看的一方很慢）先丟最舊的影格；連線斷了丟掉全部、告訴協調者每個工作階段都斷了（伺服器那邊的工作階段由另一邊結束）。狀態只在引擎執行緒讀寫
internal sealed partial class SyncEngine : ISyncEngineDesk
{
    /// <summary>排著的封包上限（超過先丟最舊的影格）</summary>
    internal const int MaxDeskQueue = 48;

    private IDeskPort? DeskPort => args.Host is IDeskPort { DeskSupported: true } port ? port : null;

    private bool deskLiveReported;

    private readonly LinkedList<DeskPacket> deskOutgoing = new();

    private readonly HashSet<string> deskSessions = new(StringComparer.Ordinal);

    private SyncConnection? deskSendingOn;

    public void SubmitDeskUp(DeskPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        thread.Post(() => OnSubmitDeskUp(packet));
    }

    private SyncConnection? DeskConnection()
        => current is { Proven: true, Session: not null } conn && DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Desk1) ? conn : null;

    private void OnSubmitDeskUp(DeskPacket packet)
    {
        if (DeskConnection() is null)
        {
            Dropped(packet.SessionId);
            return;
        }

        deskSessions.Add(packet.SessionId);
        deskOutgoing.AddLast(packet);
        while (deskOutgoing.Count > MaxDeskQueue && deskOutgoing.FirstOrDefault(p => p.Kind == DeskRules.Frame) is { } oldest)
            deskOutgoing.Remove(oldest);
        PumpDesk();
    }

    private void PumpDesk()
    {
        if (deskSendingOn is not null || deskOutgoing.First is null || DeskConnection() is not { } conn)
            return;
        var packet = deskOutgoing.First.Value;
        deskOutgoing.RemoveFirst();
        deskSendingOn = conn;
        Spawn(async () =>
        {
            DeskUpResult? result = null;
            try
            {
                result = await conn.Session!.DeskUpAsync(packet, conn.Token);
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                Log.Warn($"伺服器拒收遠端桌面 {packet.SessionId} 的封包（{ex.Failure.Code}）");
            }
            catch (SyncTransportException ex)
            {
                if (ReferenceEquals(deskSendingOn, conn))
                    deskSendingOn = null;
                Fail(conn, ex.Failure);
                return;
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(deskSendingOn, conn))
                    deskSendingOn = null;
                throw;
            }

            if (ReferenceEquals(deskSendingOn, conn))
                deskSendingOn = null;
            if (result is { Keep: false })
            {
                // 同一個工作階段排著的不必再送
                for (var node = deskOutgoing.First; node is not null;)
                {
                    var next = node.Next;
                    if (node.Value.SessionId == packet.SessionId)
                        deskOutgoing.Remove(node);
                    node = next;
                }

                if (packet.Kind is not (DeskRules.End or DeskRules.Close))
                    Dropped(packet.SessionId);
                deskSessions.Remove(packet.SessionId);
            }

            PumpDesk();
        }, conn);
    }

    private void Dropped(string sessionId)
    {
        if (DeskPort is not { } port)
            return;
        try
        {
            port.DeskDropped(sessionId);
        }
        catch (Exception ex)
        {
            Log.Error("告訴協調者遠端桌面斷了失敗", ex);
        }
    }

    /// <summary><c>desk1</c> 的 <c>DeskDown</c>：工作階段 id 合格才交給協調者（內容由收的一方檢查）</summary>
    private void OnDeskDown(SyncConnection conn, DeskPacket packet)
    {
        if (DeskPort is not { } port)
            return;
        if (packet is null || !DeskRules.IsSessionId(packet.SessionId))
        {
            Log.Warn("伺服器送來格式不合格的 DeskDown：略過");
            return;
        }

        deskSessions.Add(packet.SessionId);
        port.DeliverDeskDown(packet);
    }

    private void ReportDeskLive(SyncConnection? conn)
    {
        if (DeskPort is not { } port)
            return;
        var live = conn is not null && ReferenceEquals(DeskConnection(), conn);
        if (live == deskLiveReported)
            return;
        deskLiveReported = live;
        try
        {
            port.ReportDeskLive(live);
        }
        catch (Exception ex)
        {
            Log.Error("回報遠端桌面能不能用失敗", ex);
        }
    }

    private void OnDeskProven(SyncConnection conn) => ReportDeskLive(conn);

    /// <summary>連線丟掉了：排著的丟掉，每個工作階段都告訴協調者斷了</summary>
    private void OnDeskClosed(SyncConnection conn)
    {
        if (ReferenceEquals(deskSendingOn, conn))
            deskSendingOn = null;
        deskOutgoing.Clear();
        foreach (var session in deskSessions)
            Dropped(session);
        deskSessions.Clear();
        ReportDeskLive(null);
    }
}
