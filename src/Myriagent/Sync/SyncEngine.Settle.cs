namespace Myriagent.Sync;

// WP4.9 計畫步 1（D1、D2）：同步安定的快照。讀的狀態和 EngineView 給測試看的那幾個欄位相同（State、InFlight、Refreshing、ActiveReconcile、
// ReconcileQueue、Outbox），只是不複製 outbox 與佇列（探針每 100 ms 讀一次，不在量記憶體的期間製造垃圾）
internal sealed partial class SyncEngine : ISyncSettleProbe
{
    /// <summary>引擎執行緒已經結束時回的快照（不在 Live，永遠不算安定）</summary>
    private static readonly SyncSettleSnapshot StoppedSettle = new(false, 0, false, false, 0, false, false);

    public async Task<SyncSettleSnapshot> SettleSnapshotAsync()
    {
        if (thread.Exited.IsCompleted)
            return StoppedSettle;
        var result = new TaskCompletionSource<SyncSettleSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        thread.Post(() =>
        {
            try
            {
                result.TrySetResult(ReadSettle());
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });
        // 執行緒在排進去之後才結束時，排進去的讀取不會執行（SyncEngineThread.Post 在 CompleteAdding 之後直接丟掉）
        var finished = await Task.WhenAny(result.Task, thread.Exited).ConfigureAwait(false);
        return finished == result.Task ? await result.Task.ConfigureAwait(false) : StoppedSettle;
    }

    /// <summary>引擎執行緒上：照目前的狀態組快照（BackfillDone 由呼叫端填）</summary>
    private SyncSettleSnapshot ReadSettle()
    {
        var outboxSettled = true;
        foreach (var entry in outbox)
        {
            if (entry.State is not (OutboxState.Acked or OutboxState.Rejected))
            {
                outboxSettled = false;
                break;
            }
        }

        return new SyncSettleSnapshot(
            state == SyncEngineState.Live,
            inFlight.Count,
            refreshing,
            activeJob is not null,
            reconcileQueue.Count,
            outboxSettled,
            BackfillDone: false);
    }
}
