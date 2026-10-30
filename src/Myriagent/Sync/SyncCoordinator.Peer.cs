using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

// 協調者的桌面遠端操作（sync-protocol 14.7）：中樞的 IPeerCommandSink（建立引擎時掛上、解除配對時拿掉），把網頁對其他電腦下的指令交給
// 這一代的引擎；引擎經同一代的 EnginePort（IPeerCommandPort）送回結果與「能不能送」，回 UI 執行緒交給中樞（舊一代的丟掉）。
// 「能不能送」進 sync 狀態的 peer（web-protocol 的 remote.sync.peer），網頁照它決定副本能不能操作。只在 UI 執行緒改狀態
internal sealed partial class SyncCoordinator : IPeerCommandSink
{
    /// <summary>這一代引擎回報的「能不能送」（UI 執行緒；換代時不清，比對世代）</summary>
    private (int Gen, bool Live)? peerLive;

    string? IPeerCommandSink.DeviceId => device?.DeviceId;

    void IPeerCommandSink.SubmitPeerCommand(string commandId, JObject cmd)
    {
        if (engine is ISyncEnginePeer peer)
        {
            peer.SubmitPeerCommand(new PeerCmd(commandId, cmd));
            return;
        }

        // 引擎不支援（測試的替身）：照樣回一則結果，排到之後（不在 HandleCommand 裡重入中樞）
        var reply = PeerCommandRules.Synthesized(commandId, PeerCommandRules.NotSent);
        host.BeginInvoke(() => hub.OnPeerReply(reply.CommandId, reply.Message));
    }

    /// <summary>這一代能不能對其他電腦下指令（UI 執行緒；sync 狀態的 peer）</summary>
    private bool PeerLiveNow() => engine is not null && peerLive is { } p && p.Gen == Volatile.Read(ref generation) && p.Live;

    /// <summary>引擎的回報（任何執行緒）：回 UI 執行緒記下（舊一代的丟掉）；值變了就告訴中樞、重送 sync</summary>
    private void OnPeerLive(int gen, bool live) => host.BeginInvoke(() =>
    {
        if (disposed || engine is null || gen != Volatile.Read(ref generation))
            return;
        var before = PeerLiveNow();
        peerLive = (gen, live);
        var now = PeerLiveNow();
        hub.PeerLive = now;
        if (now != before && CurrentStatus() is not null)
            PostStatus();
    });

    /// <summary>引擎送回的結果與輸出（任何執行緒）：回 UI 執行緒交給中樞（舊一代、已釋放的丟掉）</summary>
    private void OnPeerReply(int gen, PeerReply reply) => host.BeginInvoke(() =>
    {
        if (disposed || gen != Volatile.Read(ref generation))
            return;
        hub.OnPeerReply(reply.CommandId, reply.Message);
    });

    private sealed partial class EnginePort : IPeerCommandPort
    {
        public void DeliverPeerReply(PeerReply reply)
        {
            // 遠端執行指令（SyncCoordinator.RemoteExec.cs）的結果交給 myriagent-remote 的連線，不經中樞
            if (owner.remoteExec?.TryDeliverPeerReply(reply) == true)
                return;
            owner.OnPeerReply(gen, reply);
        }

        public void ReportPeerLive(bool live) => owner.OnPeerLive(gen, live);
    }
}
