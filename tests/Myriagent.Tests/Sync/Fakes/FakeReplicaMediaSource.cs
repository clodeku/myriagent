using System.Collections.Concurrent;
using Myriagent.Hub;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>收到的一次複本媒體請求</summary>
internal sealed record ReplicaMediaCall(string Key, string Purpose, int? Size, string? Range);

/// <summary>假的複本媒體來源（WP4.7 步 12）：記錄每次請求，以 <see cref="Reply"/> 回（沒設時回 null）</summary>
internal sealed class FakeReplicaMediaSource : IReplicaMediaSource
{
    private readonly ConcurrentQueue<ReplicaMediaCall> calls = new();

    public IReadOnlyList<ReplicaMediaCall> Calls => calls.ToList();

    public Func<ReplicaMediaCall, ReplicaMediaReply?>? Reply { get; set; }

    public Task<ReplicaMediaReply?> FetchAsync(string key, string purpose, int? size, string? rangeHeader, CancellationToken ct)
    {
        var call = new ReplicaMediaCall(key, purpose, size, rangeHeader);
        calls.Enqueue(call);
        return Task.FromResult(Reply?.Invoke(call));
    }
}
