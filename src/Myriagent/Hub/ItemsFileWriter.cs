using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Hub;

/// <summary>
/// 在一條專用的背景執行緒寫對話紀錄（items.json）：定期維護每 30 秒整份重寫執行中的對話，序列化加落盤在 UI 執行緒上一次要卡一兩百毫秒
/// （2026-10-24 實測：序列化 61%、落盤與換檔約 35%）。呼叫端在 UI 執行緒取快照（<see cref="AgentChatItem.CloneForSave"/>）交過來，
/// 這裡序列化、落盤、換檔。同一個對話只留最新的一份，一次只寫一個檔。
/// 順序：UI 執行緒要自己寫同一個檔（同步存檔）、刪除或搬動資料夾之前先 <see cref="Forget"/>——排著的作廢、正在寫的等它寫完，
/// 較舊的快照不會晚到蓋掉比較新的檔，也不會在刪掉之後重建資料夾（這裡寫入一律不建立資料夾）。
/// 除了背景執行緒，其他方法都在 UI 執行緒呼叫；狀態以一把鎖保護
/// </summary>
internal sealed class ItemsFileWriter : IDisposable
{
    private sealed record Job(string ConversationId, string Path, IReadOnlyList<AgentChatItem> Items, ItemsJsonLayout? Layout);

    private readonly object gate = new();
    private readonly Action<string, Exception> failed;
    private readonly Action<string, IReadOnlyList<AgentChatItem>>? writeForTests;
    private readonly Dictionary<string, Job> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> order = new();
    private Thread? thread;
    private string? writing;
    private bool disposed;

    /// <param name="failed">寫入失敗（不含對話資料夾已經不在）時，在背景執行緒上呼叫：呼叫端自己回 UI 執行緒（標成還沒存、下次再寫）</param>
    /// <param name="writeForTests">測試換掉實際的寫檔（null＝<see cref="JsonFile.Write{T}"/>，不建立資料夾）</param>
    public ItemsFileWriter(Action<string, Exception> failed, Action<string, IReadOnlyList<AgentChatItem>>? writeForTests = null)
    {
        this.failed = failed ?? throw new ArgumentNullException(nameof(failed));
        this.writeForTests = writeForTests;
    }

    /// <summary>測試用：寫了幾份（含失敗的）</summary>
    internal int Written { get; private set; }

    /// <summary>測試用：背景執行緒的 ManagedThreadId（還沒寫過是 null）</summary>
    internal int? WorkerThreadId { get; private set; }

    /// <summary>
    /// 排一份要寫的快照：同一個對話還沒開始寫的那份被換成這份；第一次呼叫時開背景執行緒。
    /// <paramref name="layout"/>＝這個對話的增量存檔（ItemsSaveCache.Layout：沒變的項目從上一份檔案複製，ItemsJsonFile）；null＝整份序列化
    /// </summary>
    public void Enqueue(string conversationId, string path, IReadOnlyList<AgentChatItem> snapshot, ItemsJsonLayout? layout = null)
    {
        lock (gate)
        {
            if (disposed)
                return;
            if (!pending.ContainsKey(conversationId))
                order.Enqueue(conversationId);
            pending[conversationId] = new Job(conversationId, path, snapshot, layout);
            if (thread is null)
            {
                thread = new Thread(Loop) { IsBackground = true, Name = "背景存檔" };
                thread.Start();
            }
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>這個對話還有排著或正在寫的</summary>
    public bool IsPending(string conversationId)
    {
        lock (gate)
            return pending.ContainsKey(conversationId) || string.Equals(writing, conversationId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// UI 執行緒要自己寫這個對話的紀錄、或刪除與搬動它的資料夾之前呼叫：排著的作廢，正在寫的等它寫完（寫一份通常幾十毫秒，最多等 <paramref name="timeout"/>）。
    /// 等不到回 false（呼叫端照常做：同步寫的會在背景那份之後換檔，順序只在極端的慢磁碟上不保證）
    /// </summary>
    public bool Forget(string conversationId, TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds;
        lock (gate)
        {
            pending.Remove(conversationId);
            while (string.Equals(writing, conversationId, StringComparison.OrdinalIgnoreCase))
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                    return false;
                Monitor.Wait(gate, (int)Math.Min(left, int.MaxValue));
            }
            return true;
        }
    }

    /// <summary>app 結束前：等排著的都寫完（最多 <paramref name="timeout"/>）。之後同步存檔的照常 <see cref="Forget"/></summary>
    public bool Drain(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (gate)
        {
            while (pending.Count > 0 || writing is not null)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                    return false;
                Monitor.Wait(gate, (int)Math.Min(left, int.MaxValue));
            }
            return true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending.Clear();
            Monitor.PulseAll(gate);
        }
    }

    private void Loop()
    {
        WorkerThreadId = Environment.CurrentManagedThreadId;
        while (true)
        {
            Job? job;
            lock (gate)
            {
                while (!disposed && order.Count == 0)
                    Monitor.Wait(gate);
                if (disposed)
                    return;
                // 被 Forget 拿掉的還留在 order 裡：略過
                if (!pending.Remove(order.Dequeue(), out job))
                    continue;
                writing = job.ConversationId;
            }

            Exception? error = null;
            try
            {
                if (writeForTests is not null)
                    writeForTests(job.Path, job.Items);
                else if (job.Layout is not null)
                    ItemsJsonFile.Write(job.Path, job.Items, job.Layout, createDirectory: false);
                else
                    JsonFile.Write(job.Path, job.Items, createDirectory: false);
            }
            catch (DirectoryNotFoundException)
            {
                // 對話已經刪掉或搬進回收區：不寫（不重建資料夾）
            }
            catch (Exception ex)
            {
                error = ex;
            }

            // 先回報失敗再標成寫完：Forget、Drain 回來時失敗一定已經回報了
            if (error is not null)
            {
                try
                {
                    failed(job.ConversationId, error);
                }
                catch
                {
                    // 通知失敗（宿主正在結束）：下次維護照樣會因為還沒存而重寫
                }
            }
            lock (gate)
            {
                Written++;
                writing = null;
                Monitor.PulseAll(gate);
            }
        }
    }
}
