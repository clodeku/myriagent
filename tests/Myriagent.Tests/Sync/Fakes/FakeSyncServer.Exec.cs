using System.Collections.Concurrent;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Fakes;

// 遠端執行指令（sync-protocol 14.11）：收下 ExecOutput、ExecCancel，照回應器回（預設 keep／found）；ExecReply、ExecAbort 由測試以 Session.Events 推
internal sealed partial class FakeSyncServer
{
    /// <summary>收到的 ExecOutput（依序）</summary>
    public ConcurrentQueue<ExecChunk> ExecOutputs { get; } = new();

    /// <summary>收到的 ExecCancel（依序）</summary>
    public ConcurrentQueue<ExecCancel> ExecCancels { get; } = new();

    /// <summary>ExecOutput 的回應（null＝keep true）；丟例外時那則不算收到</summary>
    public Func<ExecChunk, ExecOutputResult>? ExecOutputResponder { get; set; }

    internal sealed partial class FakeSession
    {
        public Task<ExecOutputResult> ExecOutputAsync(ExecChunk c, CancellationToken ct) => Call(nameof(DeviceHubMethods.ExecOutput), ct, () =>
        {
            RequireProven();
            var result = server.ExecOutputResponder?.Invoke(c) ?? new ExecOutputResult(true);
            server.ExecOutputs.Enqueue(c);
            return result;
        });

        public Task<ExecCancelResult> ExecCancelAsync(ExecCancel c, CancellationToken ct) => Call(nameof(DeviceHubMethods.ExecCancel), ct, () =>
        {
            RequireProven();
            server.ExecCancels.Enqueue(c);
            return new ExecCancelResult(true);
        });
    }
}
