namespace Myriagent.Sync;

/// <summary>中樞那一頭的遠端桌面（<c>AgentHub</c> 的轉接；收到的東西自己排回 UI 執行緒）。任何執行緒呼叫</summary>
internal interface IDeskEndpoint
{
    void OnDeskDown(DeskPacket packet);

    void OnDeskDropped(string sessionId);

    void OnDeskLive(bool live);
}

/// <summary>中樞送遠端桌面封包的出口（<see cref="SyncCoordinator"/> 實作）</summary>
internal interface IDeskTransport
{
    /// <summary>送一個封包（任何執行緒）；沒有引擎、連線不能搬時由引擎回報斷了</summary>
    void SendDeskUp(DeskPacket packet);

    /// <summary>取走這個工作階段的 deskOpen 信封（鎖定畫面支援的服務要的憑據；ISyncEngineDesk）。沒有回 null（測試的假傳輸不必實作）</summary>
    Security.CommandEnvelope? TakeDeskEnvelope(string sessionId) => null;
}

// 協調者的遠端桌面（docs/sync-protocol.md 14.16）：中樞經 IDeskTransport 交來要送的封包，交給這一代的引擎（沒有引擎時直接回報斷了）；
// 引擎經 EnginePort（IDeskPort）送回的封包、斷線直接交給中樞的轉接（它自己排回 UI 執行緒）
internal sealed partial class SyncCoordinator : IDeskTransport
{
    private IDeskEndpoint? desk;

    /// <summary>接上遠端桌面（AppController 在 Start 之前呼叫）：之後啟動的引擎才宣告 desk1</summary>
    internal void AttachDesk(IDeskEndpoint endpoint) => desk = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    Security.CommandEnvelope? IDeskTransport.TakeDeskEnvelope(string sessionId)
        => Volatile.Read(ref engine) is ISyncEngineDesk engineDesk ? engineDesk.TakeDeskEnvelope(sessionId) : null;

    /// <summary>這台目前信任的伺服器（任何執行緒；沒配對、引擎還沒起來是 null）：安裝鎖定畫面支援時交給安裝器記下</summary>
    internal Remote.DeskServiceTrust? DeskTrust => Volatile.Read(ref engine) is ISyncEngineDesk engineDesk ? engineDesk.DeskTrust : null;

    void IDeskTransport.SendDeskUp(DeskPacket packet)
    {
        if (Volatile.Read(ref engine) is ISyncEngineDesk engineDesk)
        {
            engineDesk.SubmitDeskUp(packet);
            return;
        }

        var endpoint = desk;
        Task.Run(() => endpoint?.OnDeskDropped(packet.SessionId));
    }

    private sealed partial class EnginePort : IDeskPort
    {
        public bool DeskSupported => owner.desk is not null;

        public void DeliverDeskDown(DeskPacket packet) => owner.desk?.OnDeskDown(packet);

        public void DeskDropped(string sessionId) => owner.desk?.OnDeskDropped(sessionId);

        public void ReportDeskLive(bool live) => owner.desk?.OnDeskLive(live);
    }
}
