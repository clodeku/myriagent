using System.Text;
using Myriagent.Hub;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Core;

/// <summary>背景活動日誌的寫入端（AgentHub.AttachBackgroundJournal 接上；中樞預設沒接＝不記）</summary>
internal interface IBackgroundJournal
{
    /// <summary>附加一筆（呼叫端多半在 UI 執行緒，不能擋：實作排進佇列、在背景寫）</summary>
    void Write(JObject entry);
}

/// <summary>
/// 背景活動日誌（docs/background-journal.md）：&lt;資料根目錄&gt;\logs\background\yyyy-MM-dd.jsonl，一行一筆 JSON（UTF-8、不帶 BOM）。
/// 背景執行緒寫檔、不擋呼叫端；程序結束時同步寫出還在佇列裡的（換版前最後幾筆最重要，執行緒集區的工作在 Main 返回後會直接被終止）。
/// 單檔超過 <see cref="MaxFileBytes"/> 就改名成 yyyy-MM-dd.1.jsonl（取代上一份）再寫新檔；保留 <see cref="RetentionDays"/> 天，每天清一次舊檔
/// </summary>
internal sealed class BackgroundJournalFile : IBackgroundJournal, IDisposable
{
    /// <summary>單檔的大小上限</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>保留幾天（比一般記錄的 7 天長：背景工作的問題常常要對照好幾次換版才看得出來）</summary>
    public const int RetentionDays = 30;

    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly string directory;
    private readonly Func<DateTime> clock;
    private readonly object gate = new();
    private readonly object writeGate = new();
    private readonly Queue<(DateTime Day, string Line)> pending = new();
    private bool flushing;
    private bool disposed;
    private DateTime lastCleanupDay;

    public BackgroundJournalFile(string directory, Func<DateTime>? clock = null)
    {
        this.directory = directory;
        this.clock = clock ?? (() => DateTime.Now);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public string Directory => directory;

    public void Write(JObject entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = entry.ToString(Formatting.None);
        lock (gate)
        {
            if (disposed)
                return;
            pending.Enqueue((clock().Date, line));
            if (flushing)
                return;
            flushing = true;
        }
        ThreadPool.QueueUserWorkItem(_ => FlushInBackground());
    }

    /// <summary>立即把佇列裡的寫進檔案（同步）：回來時，呼叫前寫入的每一筆都已落盤</summary>
    public void Flush() => WritePending();

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        WritePending();
        lock (gate)
            disposed = true;
    }

    private void OnProcessExit(object? sender, EventArgs e) => WritePending();

    private void FlushInBackground()
    {
        try
        {
            WritePending();
        }
        finally
        {
            bool again;
            lock (gate)
            {
                again = pending.Count > 0;
                flushing = again;
            }
            if (again)
                ThreadPool.QueueUserWorkItem(_ => FlushInBackground());
        }
    }

    /// <summary>取出佇列與寫檔都在 writeGate 內：背景寫檔與同步 Flush 不會同時開同一個檔，順序也不會亂</summary>
    private void WritePending()
    {
        lock (writeGate)
        {
            List<(DateTime Day, string Line)> lines;
            lock (gate)
            {
                if (pending.Count == 0)
                    return;
                lines = pending.ToList();
                pending.Clear();
            }
            try
            {
                System.IO.Directory.CreateDirectory(directory);
                foreach (var group in lines.GroupBy(l => l.Day))
                {
                    var path = Path.Combine(directory, BackgroundJournalRules.FileName(group.Key));
                    Log.RollIfTooLarge(path, MaxFileBytes, BackgroundJournalRules.RolledFileName(group.Key));
                    var sb = new StringBuilder();
                    foreach (var (_, line) in group)
                        sb.Append(line).Append('\n');
                    File.AppendAllText(path, sb.ToString(), Utf8);
                }
                var today = clock().Date;
                if (lastCleanupDay != today)
                    Cleanup(today);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"背景活動日誌寫不進去：{ex.Message}");
            }
        }
    }

    /// <summary>刪掉超過保留天數的日誌（檔名的日期；改名過的 .1.jsonl 也算）</summary>
    private void Cleanup(DateTime today)
    {
        lastCleanupDay = today;
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(directory, "*.jsonl"))
            {
                if (BackgroundJournalRules.IsExpired(Path.GetFileName(file), today))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
