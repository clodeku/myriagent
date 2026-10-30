using System.Collections.Concurrent;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Fakes;

// 各帳戶的 token 計數（sync-protocol 14.9）：收下 TokensPublish；TokensFailure 不是 null 時丟它（測拒收與斷線）
internal sealed partial class FakeSyncServer
{
    /// <summary>收到的 TokensPublish（依序；丟例外的也記）</summary>
    public ConcurrentQueue<TokensPublish> TokensPublishes { get; } = new();

    /// <summary>TokensPublish 要丟的例外（null＝照常收下）</summary>
    public Func<TokensPublish, Exception?>? TokensFailure { get; set; }

    internal sealed partial class FakeSession
    {
        public Task TokensPublishAsync(TokensPublish p, CancellationToken ct) => Call(nameof(DeviceHubMethods.TokensPublish), ct, () =>
        {
            RequireProven();
            server.TokensPublishes.Enqueue(p);
            if (server.TokensFailure?.Invoke(p) is { } failure)
                throw failure;
            return true;
        });
    }
}
