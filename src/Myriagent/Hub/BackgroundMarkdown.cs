using Myriagent.Agents;
using Myriagent.Rendering;

namespace Myriagent.Hub;

/// <summary>
/// 在一條專用的背景執行緒把很長的執行中回覆轉成 HTML（BackgroundMarkdownRules）。不用執行緒集區：CLI 程序多、電腦忙的時候集區會排隊好幾秒
/// （CLAUDE.md「子程序的輸出一律用專用執行緒讀」）。同一筆只留最新的一份請求，一次只轉一份；轉好時以 <c>ready</c>（在這條背景執行緒上）通知，
/// 呼叫端自己回到 UI 執行緒再 <see cref="Take"/>。網址用 <see cref="LocalMediaUrls.Deferred"/>：網址和 UI 執行緒轉的一樣，白名單由呼叫端回到 UI 執行緒補登記。
/// 除了背景執行緒，其他方法都在 UI 執行緒呼叫；狀態以一把鎖保護
/// </summary>
internal sealed class BackgroundMarkdown : IDisposable
{
    /// <summary>轉好的一份</summary>
    /// <param name="Text">轉的是哪一段文字</param>
    /// <param name="Markdown">轉好的 HTML 與本機圖片；null＝轉換失敗（呼叫端改在 UI 執行緒照常轉）</param>
    /// <param name="Links">出現過的本機檔案連結（呼叫端以 LocalMediaUrls.AllowMarkdownLink 補登記）</param>
    internal sealed record Result(string Text, PrerenderedMarkdown? Markdown, IReadOnlyList<string> Links);

    private sealed record Job(string Key, string ConversationId, string ItemId, string Text, string? WorkingDirectory);

    /// <summary>轉好、還沒被取走的最多留幾份（開著卻沒有視窗在看的對話不會來取）：超過就整批丟掉，要用時再轉</summary>
    internal const int MaxResults = 64;

    private readonly object gate = new();
    private readonly Action<string, string> ready;
    private readonly Dictionary<string, Job> pending = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private readonly Dictionary<string, Result> results = new(StringComparer.Ordinal);
    private Thread? thread;
    private string? renderingKey;
    private bool renderingForgotten;
    private bool disposed;

    /// <param name="ready">轉好一份時（對話 id、項目 id），在背景執行緒上呼叫：呼叫端要自己回到 UI 執行緒</param>
    public BackgroundMarkdown(Action<string, string> ready)
    {
        this.ready = ready ?? throw new ArgumentNullException(nameof(ready));
    }

    /// <summary>測試用：背景執行緒的 ManagedThreadId（還沒開始轉過是 null）</summary>
    internal int? WorkerThreadId { get; private set; }

    /// <summary>測試用：轉了幾份（含失敗與被丟掉的）</summary>
    internal int Rendered { get; private set; }

    private static string KeyOf(string conversationId, string itemId) => conversationId + "\n" + itemId;

    /// <summary>排一份要轉的文字：同一筆還沒開始轉的那份被換成這份；第一次呼叫時開背景執行緒</summary>
    /// <param name="workingDirectory">對話的工作資料夾（同 ItemRenderer.Render）</param>
    public void Request(string conversationId, string itemId, string text, string? workingDirectory)
    {
        var key = KeyOf(conversationId, itemId);
        lock (gate)
        {
            if (disposed)
                return;
            if (!pending.ContainsKey(key))
                order.Enqueue(key);
            pending[key] = new Job(key, conversationId, itemId, text, workingDirectory);
            if (thread is null)
            {
                thread = new Thread(Loop) { IsBackground = true, Name = "背景 Markdown", Priority = ThreadPriority.BelowNormal };
                thread.Start();
            }
            Monitor.Pulse(gate);
        }
    }

    /// <summary>這一筆還在排隊或正在轉</summary>
    public bool IsPending(string conversationId, string itemId)
    {
        var key = KeyOf(conversationId, itemId);
        lock (gate)
            return pending.ContainsKey(key) || renderingKey == key;
    }

    /// <summary>取走這一筆轉好的那一份（沒有回 null）</summary>
    public Result? Take(string conversationId, string itemId)
    {
        lock (gate)
            return results.Remove(KeyOf(conversationId, itemId), out var r) ? r : null;
    }

    /// <summary>
    /// 這一筆已經在 UI 執行緒照常轉過、送出了（結束、整份重送）：排著的、轉好的、正在轉的都作廢，免得稍早的內容晚到、蓋掉比較新的畫面
    /// </summary>
    public void Forget(string conversationId, string itemId)
    {
        var key = KeyOf(conversationId, itemId);
        lock (gate)
        {
            pending.Remove(key);
            results.Remove(key);
            if (renderingKey == key)
                renderingForgotten = true;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending.Clear();
            results.Clear();
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
                renderingKey = job.Key;
                renderingForgotten = false;
            }

            var result = Render(job);
            bool deliver;
            lock (gate)
            {
                Rendered++;
                deliver = !disposed && !renderingForgotten;
                if (deliver)
                {
                    if (results.Count >= MaxResults)
                        results.Clear();
                    results[job.Key] = result;
                }
                renderingKey = null;
            }
            if (deliver)
            {
                try
                {
                    ready(job.ConversationId, job.ItemId);
                }
                catch
                {
                    // 通知失敗（宿主正在結束）：轉好的留著，下次送畫面時照樣取得到
                }
            }
        }
    }

    /// <summary>和 ItemRenderer 的 MarkdownToHtml 一樣的轉換（同一個 AgentMarkdownRenderer.ToHtml、同樣的網址），只是不碰白名單</summary>
    internal static Result Render(string text, string? workingDirectory)
    {
        var urls = new LocalMediaUrls.Deferred();
        var images = new List<string>();
        try
        {
            var html = AgentMarkdownRenderer.ToHtml(text, images, workingDirectory, urls);
            return new Result(text, new PrerenderedMarkdown(html, images), urls.Links);
        }
        catch
        {
            // Markdig 遇到巢狀過深的內容會丟例外：回到 UI 執行緒照常轉（改用純文字顯示、記錄一次）
            return new Result(text, null, Array.Empty<string>());
        }
    }

    private static Result Render(Job job) => Render(job.Text, job.WorkingDirectory);
}
