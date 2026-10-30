using System.Collections.Concurrent;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>測試用引擎的遠端指令功能（WP4.4 計畫 D35）：只記錄睡眠恢復、讀紀錄與匯出，讀紀錄與匯出的行為由測試決定（WP4.6 加讀紀錄的接縫）</summary>
internal sealed partial class RecordingSyncEngine : ISyncEngineCommands
{
    private int resumes;

    public int Resumes => Volatile.Read(ref resumes);

    /// <summary>每次 OnSystemResumed 是在哪個執行緒上呼叫的</summary>
    public ConcurrentQueue<int> ResumeThreads { get; } = new();

    /// <summary>每次匯出的路徑，與那時引擎是否已經停止（看解除配對先匯出還是先停）</summary>
    public ConcurrentQueue<(string Path, bool StoppedBefore)> Exports { get; } = new();

    /// <summary>匯出的行為（null＝立刻回 0 列，不寫檔）</summary>
    public Func<string, CancellationToken, Task<int>>? Export { get; set; }

    public void OnSystemResumed()
    {
        ResumeThreads.Enqueue(Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref resumes);
    }

    /// <summary>每次讀紀錄的參數（WP4.6 的 syncLoadRemoteAudit：看協調者多讀一筆、帶 beforeSeq）</summary>
    public ConcurrentQueue<(long? BeforeAt, int Limit, long? BeforeSeq)> AuditLoads { get; } = new();

    /// <summary>讀紀錄的行為（null＝空的一頁；WP4.6 的測試接真的 SyncStore 或回固定的列）</summary>
    public Func<long?, int, long?, Task<IReadOnlyList<RemoteAuditEntry>>>? Audit { get; set; }

    public Task<IReadOnlyList<RemoteAuditEntry>> LoadRemoteAuditAsync(long? beforeAt, int limit, CancellationToken ct, long? beforeSeq = null)
    {
        AuditLoads.Enqueue((beforeAt, limit, beforeSeq));
        return Audit?.Invoke(beforeAt, limit, beforeSeq) ?? Task.FromResult<IReadOnlyList<RemoteAuditEntry>>([]);
    }

    public Task<int> ExportRemoteAuditAsync(string path, CancellationToken ct)
    {
        Exports.Enqueue((path, Stopped));
        return Export?.Invoke(path, ct) ?? Task.FromResult(0);
    }
}
