using System.Collections.Concurrent;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Fakes;

// 桌面遠端操作（sync-protocol 14.7）：收下 PeerCmd、照 PeerCmdResponder 回應（預設 accepted），PeerReply 由測試以 Session.Events 推
internal sealed partial class FakeSyncServer
{
    /// <summary>收到的 PeerCmd（依序）</summary>
    public ConcurrentQueue<PeerCmd> PeerCmds { get; } = new();

    /// <summary>PeerCmd 的回應（null＝accepted）；丟例外時那則不算收到</summary>
    public Func<PeerCmd, PeerCmdResult>? PeerCmdResponder { get; set; }

    internal sealed partial class FakeSession
    {
        public Task<PeerCmdResult> PeerCmdAsync(PeerCmd c, CancellationToken ct) => Call(nameof(DeviceHubMethods.PeerCmd), ct, () =>
        {
            RequireProven();
            var result = server.PeerCmdResponder?.Invoke(c) ?? new PeerCmdResult(true, c.CommandId);
            server.PeerCmds.Enqueue(c);
            return result;
        });
    }
}
