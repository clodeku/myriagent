using System.Collections.Concurrent;
using Myriagent.Sync;
using Myriagent.Tests.Hub;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>
/// 測試用的宿主（<see cref="ISyncHostPort"/>）：UI 工作一律 Post 到測試的 <see cref="UiThread"/>（正式環境的 BeginInvoke 一律 Post），
/// 記錄執行的執行緒、發布的狀態（含時間）與用量查詢快照。<see cref="BlockUi"/> 時 UI 工作永遠不執行（模擬 app 結束時排了不會跑）
/// </summary>
internal sealed partial class FakeHostPort(UiThread ui, TimeProvider time) : ISyncHostPort
{
    private long uiAliveAt;
    private int pings;

    public UiThread Ui { get; } = ui;

    /// <summary>RunOnUiAsync 排的工作不執行，只等取消</summary>
    public bool BlockUi { get; set; }

    /// <summary>PingUi 當場寫下存活時間（測試心跳帶的值；正式環境是排到 UI 執行緒、不等它）</summary>
    public bool PingSynchronously { get; set; }

    public ConcurrentQueue<(SyncStatusSnapshot Status, DateTimeOffset At)> Statuses { get; } = new();

    public ConcurrentQueue<UsagePollSnapshot> PollSnapshots { get; } = new();

    /// <summary>RunOnUiAsync 的工作實際執行時是不是在 UI 執行緒（每次一筆）</summary>
    public ConcurrentQueue<bool> RanOnUi { get; } = new();

    public int Pings => Volatile.Read(ref pings);

    public SyncStatusSnapshot? LastStatus => Statuses.LastOrDefault().Status;

    public UsagePollSnapshot? LastPoll => PollSnapshots.LastOrDefault();

    public long UiAliveAt => Volatile.Read(ref uiAliveAt);

    /// <summary>用量閘門：設定後發布的快照也交給它（照協調者的接法，UI 執行緒以 Volatile 讀）</summary>
    public UsagePollGate? Gate { get; set; }

    public Task RunOnUiAsync(Action action, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => done.TrySetCanceled(ct));
        if (BlockUi)
            return done.Task;
        Ui.Post(() =>
        {
            registration.Dispose();
            if (ct.IsCancellationRequested)
            {
                done.TrySetCanceled(ct);
                return;
            }

            RanOnUi.Enqueue(Ui.IsCurrent);
            try
            {
                action();
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }

    public void PublishStatus(SyncStatusSnapshot status) => Statuses.Enqueue((status, time.GetUtcNow()));

    public void PublishPollSnapshot(UsagePollSnapshot snapshot)
    {
        PollSnapshots.Enqueue(snapshot);
        Gate?.Publish(snapshot);
    }

    public void PingUi()
    {
        Interlocked.Increment(ref pings);
        if (PingSynchronously)
            Volatile.Write(ref uiAliveAt, time.GetUtcNow().ToUnixTimeMilliseconds());
        else
            Ui.Post(() => Volatile.Write(ref uiAliveAt, time.GetUtcNow().ToUnixTimeMilliseconds()));
    }
}

/// <summary>測試用的 UI 套用端：記錄每次套用與執行緒，可以讓它丟例外</summary>
internal sealed partial class RecordingApplier(UiThread ui) : ISyncUiApplier
{
    private int offUi;

    public ConcurrentQueue<CatalogApply> Catalogs { get; } = new();

    public ConcurrentQueue<IReadOnlyList<SharedUsageApply>> SharedUsage { get; } = new();

    /// <summary>remoteOnly 卡片的共享用量（每次套用的項目與 reset）</summary>
    public ConcurrentQueue<(IReadOnlyList<RemoteUsageApply> Items, bool Reset)> RemoteUsage { get; } = new();

    /// <summary>不在 UI 執行緒上被呼叫的次數（應該永遠是 0）</summary>
    public int OffUiCalls => Volatile.Read(ref offUi);

    /// <summary>套用目錄時丟這個例外（null＝正常）</summary>
    public Exception? ThrowOnCatalog { get; set; }

    /// <summary>套用目錄時（在 UI 執行緒上、記錄之前）先做的事</summary>
    public Action<CatalogApply>? OnCatalog { get; set; }

    public void ApplyCatalog(CatalogApply apply)
    {
        if (!ui.IsCurrent)
            Interlocked.Increment(ref offUi);
        OnCatalog?.Invoke(apply);
        if (ThrowOnCatalog is { } ex)
            throw ex;
        Catalogs.Enqueue(apply);
    }

    public void ApplySharedUsage(IReadOnlyList<SharedUsageApply> items)
    {
        if (!ui.IsCurrent)
            Interlocked.Increment(ref offUi);
        SharedUsage.Enqueue(items);
    }

    public void ApplyRemoteUsage(IReadOnlyList<RemoteUsageApply> items, bool reset)
    {
        if (!ui.IsCurrent)
            Interlocked.Increment(ref offUi);
        RemoteUsage.Enqueue((items, reset));
    }
}
