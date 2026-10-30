using Myriagent.Core;
using Myriagent.RemoteExec;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

// 協調者的遠端執行指令（docs/sync-protocol.md 14.11）：RemoteExecService 經 IRemoteExecTransport 交來要送的指令、停止要求與輸出，
// 這裡交給這一代的引擎（沒有引擎時：指令合成 not_sent 的結果、停止與輸出丟掉）；引擎經 EnginePort（IRemoteExecPort）送回的結果、輸出、
// 停止要求直接交給 RemoteExecService（不回 UI 執行緒：它自己加鎖；commandId 是唯一的，不必比對世代）。這台能不能送（交握完成、協商出 exec1）
// 由引擎回報，list 與 run 的檢查用
internal sealed partial class SyncCoordinator : IRemoteExecTransport
{
    private IRemoteExecEndpoint? remoteExec;

    /// <summary>這一代引擎回報的「能不能送遠端執行指令」（任何執行緒讀）</summary>
    private int execLiveGen = -1;

    private volatile bool execLive;

    /// <summary>接上遠端執行指令的桌面端（AppController 在 Start 之前呼叫）：之後啟動的引擎才宣告 exec1</summary>
    internal void AttachRemoteExec(IRemoteExecEndpoint endpoint) => remoteExec = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    Task<RemoteExecDirectory> IRemoteExecTransport.GetDirectoryAsync()
    {
        var done = new TaskCompletionSource<RemoteExecDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeginInvoke(() =>
        {
            try
            {
                done.TrySetResult(ExecDirectory());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    void IRemoteExecTransport.SubmitPeerCommand(string commandId, JObject cmd)
    {
        if (Volatile.Read(ref engine) is ISyncEnginePeer peer)
        {
            peer.SubmitPeerCommand(new PeerCmd(commandId, cmd));
            return;
        }

        // 沒有引擎（未配對、解除配對中）：照樣回一則結果
        var reply = PeerCommandRules.Synthesized(commandId, PeerCommandRules.NotSent);
        Task.Run(() => remoteExec?.TryDeliverPeerReply(reply));
    }

    void IRemoteExecTransport.CancelExec(string commandId) => (Volatile.Read(ref engine) as ISyncEngineExec)?.SubmitExecCancel(commandId);

    void IRemoteExecTransport.SendExecOutput(ExecChunk chunk) => (Volatile.Read(ref engine) as ISyncEngineExec)?.SubmitExecOutput(chunk);

    /// <summary>list 與 run 用的快照（UI 執行緒）</summary>
    private RemoteExecDirectory ExecDirectory()
    {
        var live = engine is not null && execLive && Volatile.Read(ref execLiveGen) == Volatile.Read(ref generation);
        if (engine is null || device is null || catalogView is not { } view)
            return new RemoteExecDirectory(device is not null && engine is not null, live, device?.DeviceId, []);
        return new RemoteExecDirectory(true, live, device.DeviceId, RemoteExecDirectoryRules.Devices(view));
    }

    private void OnExecLive(int gen, bool live)
    {
        Volatile.Write(ref execLiveGen, gen);
        execLive = live;
    }

    private sealed partial class EnginePort : IRemoteExecPort
    {
        public bool ExecSupported => owner.remoteExec is not null;

        public void DeliverExecReply(ExecChunk chunk) => owner.remoteExec?.DeliverExecReply(chunk);

        public void AbortExec(string commandId, bool orphaned) => owner.remoteExec?.AbortExec(commandId, orphaned);

        public void ReportExecLive(bool live) => owner.OnExecLive(gen, live);
    }
}

/// <summary>同步目錄 → 遠端執行指令的電腦清單（純函式）</summary>
internal static class RemoteExecDirectoryRules
{
    /// <summary>
    /// 目錄裡的每一台（名冊與 device.upsert 合起來，依名稱排序）：在線、版本（那台目前的連線協商出 exec1）、遠端操作層級與「允許遠端執行指令」取 device.upsert，
    /// 撤銷看名冊與 device.revoked
    /// </summary>
    public static IReadOnlyList<RemoteExecDevice> Devices(SyncCatalogView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var registry = view.Registry.GroupBy(e => e.DeviceId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var ids = registry.Keys.Union(view.Devices.Keys, StringComparer.Ordinal);
        var result = new List<RemoteExecDevice>();
        foreach (var id in ids)
        {
            view.Devices.TryGetValue(id, out var up);
            registry.TryGetValue(id, out var reg);
            var revoked = reg?.RevokedAt is not null || view.Revoked.ContainsKey(id);
            result.Add(new RemoteExecDevice(id, up?.Name ?? reg?.Name ?? id, up?.MachineName, id == view.SelfDeviceId, up?.Online ?? false, revoked,
                DeviceFeatureRules.Has(up?.Features, DeviceFeatureRules.Exec1), up?.Policy?.RemoteControlLevel ?? SyncSettings.LevelOff,
                up?.Policy?.AllowRemoteExec == true));
        }

        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
    }
}
