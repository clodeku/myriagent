using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// 在一條專用的背景執行緒替回覆留媒體副本（<see cref="MediaKeepRules"/>）：中樞在 UI 執行緒交來項目的媒體路徑與回覆文字，
/// 這裡找出回覆 Markdown 引用的本機圖片、把要留的原檔複製到對話資料夾的 <c>attachments\.kept\</c>，做完在背景執行緒回呼（呼叫端自己回 UI 執行緒記進項目）。
/// 順序同 <see cref="ItemsFileWriter"/>：刪除或搬動對話資料夾之前先 <see cref="Forget"/>——排著的作廢、正在複製的等它做完；
/// 對話資料夾已經不在就不複製（不重建資料夾）。除了背景執行緒，其他方法都在 UI 執行緒呼叫；狀態以一把鎖保護
/// </summary>
internal sealed class MediaKeeper : IDisposable
{
    /// <param name="ConversationId">對話 id</param>
    /// <param name="ItemId">項目 id（副本的雜湊與回呼用）</param>
    /// <param name="ConversationDirectory">對話資料夾（不存在就不做）</param>
    /// <param name="MediaPaths">項目的 MediaPaths</param>
    /// <param name="MarkdownText">回覆的 Markdown（找引用的本機圖片；沒有圖片語法時是 null）</param>
    /// <param name="WorkingDirectory">Markdown 相對路徑的基準（同渲染）</param>
    /// <param name="AlreadyKept">項目已經留過的原檔（正規化後）</param>
    /// <param name="OnlyUnder">只留這個資料夾底下的（延伸對話補留其他對話資料夾裡的媒體；null＝不限）</param>
    /// <param name="ExceptUnder">這個資料夾底下的不留（null＝不排除）</param>
    public sealed record Job(string ConversationId, string ItemId, string ConversationDirectory, IReadOnlyList<string> MediaPaths,
        string? MarkdownText, string? WorkingDirectory, IReadOnlyCollection<string> AlreadyKept, string? OnlyUnder = null, string? ExceptUnder = null);

    private readonly object gate = new();
    private readonly Action<string, string, IReadOnlyDictionary<string, string>> kept;
    private readonly Action<string> warn;
    private readonly Queue<Job> pending = new();
    private Thread? thread;
    private string? working;
    private bool disposed;

    /// <param name="kept">一個項目做完、至少留了一份時在背景執行緒呼叫（對話 id、項目 id、原檔 → 副本相對路徑）</param>
    /// <param name="warn">複製失敗的記錄（背景執行緒）</param>
    public MediaKeeper(Action<string, string, IReadOnlyDictionary<string, string>> kept, Action<string> warn)
    {
        this.kept = kept ?? throw new ArgumentNullException(nameof(kept));
        this.warn = warn ?? throw new ArgumentNullException(nameof(warn));
    }

    /// <summary>測試用：做完幾個項目（含沒有留任何檔的）</summary>
    internal int Done { get; private set; }

    /// <summary>排一個項目；第一次呼叫時開背景執行緒</summary>
    public void Enqueue(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (gate)
        {
            if (disposed)
                return;
            pending.Enqueue(job);
            if (thread is null)
            {
                thread = new Thread(Loop) { IsBackground = true, Name = "留媒體副本" };
                thread.Start();
            }
            Monitor.PulseAll(gate);
        }
    }

    /// <summary>
    /// 刪除或搬動這個對話的資料夾之前呼叫：排著的作廢，正在複製的等它做完（最多 <paramref name="timeout"/>；單檔上限 64 MB，通常不到一秒）。
    /// 等不到回 false（呼叫端照常做：資料夾不在時複製會失敗，不會重建）
    /// </summary>
    public bool Forget(string conversationId, TimeSpan? timeout = null)
    {
        var deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(10)).TotalMilliseconds;
        lock (gate)
        {
            if (pending.Any(j => string.Equals(j.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase)))
            {
                var keep = pending.Where(j => !string.Equals(j.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase)).ToList();
                pending.Clear();
                foreach (var j in keep)
                    pending.Enqueue(j);
            }
            while (string.Equals(working, conversationId, StringComparison.OrdinalIgnoreCase))
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                    return false;
                Monitor.Wait(gate, (int)Math.Min(left, int.MaxValue));
            }
            return true;
        }
    }

    /// <summary>測試用：等排著的都做完（最多 <paramref name="timeout"/>）</summary>
    internal bool Drain(TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        lock (gate)
        {
            while (pending.Count > 0 || working is not null)
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
        while (true)
        {
            Job job;
            lock (gate)
            {
                while (!disposed && pending.Count == 0)
                    Monitor.Wait(gate);
                if (disposed)
                    return;
                job = pending.Dequeue();
                working = job.ConversationId;
            }

            Dictionary<string, string>? result = null;
            try
            {
                result = Keep(job, warn);
            }
            catch (Exception ex)
            {
                warn($"留回覆的媒體副本失敗（對話 {job.ConversationId}）：{ex.Message}");
            }

            // 先回呼再標成做完：Forget、Drain 回來時回呼一定已經排進 UI 執行緒了
            if (result is { Count: > 0 })
            {
                try
                {
                    kept(job.ConversationId, job.ItemId, result);
                }
                catch
                {
                    // 宿主正在結束：副本已經在資料夾裡，只是這次沒記進項目
                }
            }
            lock (gate)
            {
                Done++;
                working = null;
                Monitor.PulseAll(gate);
            }
        }
    }

    /// <summary>
    /// 一個項目：找出要留的檔、逐一複製；回傳這次留的（原檔 → 副本相對路徑）。背景執行緒用，延伸對話也在它自己的背景工作裡直接呼叫
    /// （新對話還沒登記，不經排程與回呼）
    /// </summary>
    internal static Dictionary<string, string> Keep(Job job, Action<string> warn)
    {
        var paths = new List<string?>(job.MediaPaths);
        if (!string.IsNullOrEmpty(job.MarkdownText))
        {
            // 和渲染同一個算法找引用的本機圖片（只要路徑；網址用桌面預設的，不碰白名單）
            var images = new List<string>();
            try
            {
                AgentMarkdownRenderer.ToHtml(job.MarkdownText, images, job.WorkingDirectory);
            }
            catch (ArgumentException)
            {
                // Markdig 遇到巢狀過深的內容：畫面也改用純文字顯示，沒有圖片要留
            }
            paths.AddRange(images);
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in MediaKeepRules.Candidates(paths, job.ConversationDirectory, job.AlreadyKept, job.OnlyUnder, job.ExceptUnder))
        {
            // 對話已經刪掉或搬進回收區：不做（不重建資料夾）
            if (!Directory.Exists(job.ConversationDirectory))
                break;
            var relative = MediaKeepRules.RelativePath(job.ItemId, original);
            try
            {
                if (CopyOne(original, Path.Combine(job.ConversationDirectory, relative)))
                    result[original] = relative;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warn($"留回覆的媒體副本失敗（{Path.GetFileName(original)}）：{ex.Message}");
            }
        }

        return result;
    }

    /// <summary>複製一個檔：先寫暫存檔再換名（不會留下寫一半的副本）；已經有了就不再複製。原檔不在或大小不合格回 false</summary>
    private static bool CopyOne(string original, string target)
    {
        if (File.Exists(target))
            return true;
        using var source = OpenSource(original);
        if (source is null || !MediaKeepRules.ShouldCopy(source.Length))
            return false;
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var temp = target + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                source.CopyTo(output);
            File.Move(temp, target, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>開原檔（其他程式可能還開著它：讀寫刪都共用）；不在回 null</summary>
    private static FileStream? OpenSource(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
