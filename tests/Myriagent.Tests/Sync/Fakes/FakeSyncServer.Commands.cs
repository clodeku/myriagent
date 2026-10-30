using System.Collections.Concurrent;

namespace Myriagent.Tests.Sync.Fakes;

// 遠端指令的結果與輸出（WP4.4 計畫步 4）：送出時可以丟例外（ClientBug／暫時性）、可以扣住回應（驗「一次送一則」）
internal sealed partial class FakeSyncServer
{
    private readonly ConcurrentQueue<TaskCompletionSource<bool>> heldCommandSends = new();

    /// <summary>送 CommandResult／CommandOutput 時要丟的例外（引數是那則結果或輸出；null＝收下）。丟例外時那則不算收到</summary>
    public Func<object, Exception?>? CommandSendFailure { get; set; }

    /// <summary>扣住 CommandResult／CommandOutput 的回應：呼叫已記在 <see cref="Calls"/>、內容已收下，回應等 <see cref="ReleaseCommandSends"/> 才完成</summary>
    public bool HoldCommandSends { get; set; }

    /// <summary>扣住的回應數</summary>
    public int HeldCommandSends => heldCommandSends.Count;

    /// <summary>
    /// 完成目前扣住的所有回應（依序）。先全部取出再完成：邊取邊完成的話，引擎在另一條執行緒接著送的下一則
    /// 可能剛好排進佇列、被這個迴圈一起放行（「一次一則」的測試就會看到多送了一則）
    /// </summary>
    public void ReleaseCommandSends()
    {
        var held = new List<TaskCompletionSource<bool>>();
        while (heldCommandSends.TryDequeue(out var next))
            held.Add(next);
        foreach (var send in held)
            send.TrySetResult(true);
    }

    internal sealed partial class FakeSession
    {
        private Task CommandSend(FakeSyncServer owner, string method, object message, CancellationToken ct, Action accept)
        {
            var call = Call(method, ct, () =>
            {
                RequireProven();
                if (owner.CommandSendFailure?.Invoke(message) is { } failure)
                    throw failure;
                accept();
                return true;
            });
            if (!owner.HoldCommandSends || !call.IsCompletedSuccessfully)
                return call;
            var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.heldCommandSends.Enqueue(held);
            return held.Task;
        }
    }
}
