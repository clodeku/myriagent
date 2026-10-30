using System.Collections.Concurrent;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Fakes;

// 各電腦的即時狀態（sync-protocol 14.8）：收下 StatsPublish、照 StatsWatched 回 watched（預設 false）；StatsUpdate 由測試以 Session.Events 推
internal sealed partial class FakeSyncServer
{
    /// <summary>收到的 StatsPublish（依序）</summary>
    public ConcurrentQueue<StatsPublish> StatsPublishes { get; } = new();

    /// <summary>StatsPublish 回的 watched</summary>
    public bool StatsWatched { get; set; }

    internal sealed partial class FakeSession
    {
        public Task<StatsPublishResult> StatsPublishAsync(StatsPublish p, CancellationToken ct) => Call(nameof(DeviceHubMethods.StatsPublish), ct, () =>
        {
            RequireProven();
            server.StatsPublishes.Enqueue(p);
            return new StatsPublishResult(server.StatsWatched);
        });
    }
}
