using System.Collections.Concurrent;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>
/// 測試用的引擎：只記錄 UI 執行緒交給它的訊號與呼叫（執行緒安全）。<see cref="Conversations"/> 預設是 null（不發布對話，協調者不掛發布者）；
/// 給了盒子就是「支援對話的引擎」（WP4.3 計畫步 7），盒子裡的東西由測試自己取
/// </summary>
internal sealed partial class RecordingSyncEngine : ISyncEngine, IConversationEngine
{
    public ConversationChangeBox? Conversations { get; init; }

    private int starts;
    private int retries;
    private int cancels;

    public ConcurrentQueue<SyncSignal> Posted { get; } = new();

    public ConcurrentQueue<bool> Paused { get; } = new();

    public ConcurrentQueue<string> Confirmed { get; } = new();

    public ConcurrentQueue<DevicePolicy> Policies { get; } = new();

    public int Starts => Volatile.Read(ref starts);

    public int Retries => Volatile.Read(ref retries);

    public int Cancels => Volatile.Read(ref cancels);

    public bool Stopped { get; private set; }

    public long HeartbeatsSent { get; set; }

    public IReadOnlyList<T> Of<T>() where T : SyncSignal => Posted.OfType<T>().ToList();

    public void Start() => Interlocked.Increment(ref starts);

    public void Post(SyncSignal signal) => Posted.Enqueue(signal);

    public ConcurrentQueue<LiveRelay> Live { get; } = new();

    public void PostLive(LiveRelay relay) => Live.Enqueue(relay);

    public void SetPaused(bool paused) => Paused.Enqueue(paused);

    /// <summary>每次 RetryNow 是在哪個執行緒上呼叫的</summary>
    public ConcurrentQueue<int> RetryThreads { get; } = new();

    public void RetryNow()
    {
        RetryThreads.Enqueue(Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref retries);
    }

    public void ConfirmHubIdentity(string fingerprint) => Confirmed.Enqueue(fingerprint);

    public void UpdatePolicy(DevicePolicy policy) => Policies.Enqueue(policy);

    public Task StopAsync(TimeSpan timeout)
    {
        Stopped = true;
        return Task.CompletedTask;
    }

    public void Cancel() => Interlocked.Increment(ref cancels);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
