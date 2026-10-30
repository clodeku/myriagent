using System.Diagnostics;
using System.Text;

namespace Myriagent.Core;

/// <summary>
/// 簡單的檔案記錄：%LOCALAPPDATA%\Myriagent\logs\yyyy-MM-dd.log（保留 7 天）。
/// 背景執行緒寫檔、不擋呼叫端；Release 版也看得到（Debug.WriteLine 在 Release 是空的）。
/// 程序結束時同步寫出還在佇列裡的記錄（執行緒集區的工作會直接被終止）；單檔超過 10 MB 就換新檔，長時間執行時每天清一次舊檔。
/// </summary>
internal static class Log
{
    /// <summary>單一記錄檔的大小上限：超過就改名成 yyyy-MM-dd.1.log（取代上一份）再寫新檔，持續性的錯誤不會讓檔案無限長大</summary>
    public const long MaxFileBytes = 10L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly object WriteGate = new();
    private static readonly Queue<string> Pending = new();
    private static readonly UTF8Encoding Utf8 = new(false);
    private static bool flushing;
    private static DateTime lastCleanupDay;

    static Log()
    {
        // Main 返回後執行緒集區的工作直接被終止：結束前才記下的幾行（例如關閉代理程序、設定存檔失敗）在這裡同步寫出
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex == null ? message : $"{message}：{ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            Pending.Enqueue(line);
            if (flushing)
                return;
            flushing = true;
        }
        ThreadPool.QueueUserWorkItem(_ => FlushInBackground());
    }

    /// <summary>立即把佇列裡的記錄寫進檔案（同步；程序結束、未處理的例外時呼叫）。回來時，呼叫前記下的每一行都已寫出</summary>
    public static void Flush() => WritePending();

    private static void FlushInBackground()
    {
        try
        {
            WritePending();
        }
        finally
        {
            bool again;
            lock (Gate)
            {
                again = Pending.Count > 0;
                flushing = again;
            }
            if (again)
                ThreadPool.QueueUserWorkItem(_ => FlushInBackground());
        }
    }

    /// <summary>取出佇列與寫檔都在 WriteGate 內：背景寫檔與同步 Flush 不會同時開同一個檔，順序也不會亂</summary>
    private static void WritePending()
    {
        lock (WriteGate)
        {
            var sb = new StringBuilder();
            lock (Gate)
            {
                while (Pending.Count > 0)
                    sb.AppendLine(Pending.Dequeue());
            }
            if (sb.Length == 0)
                return;
            try
            {
                Directory.CreateDirectory(AppPaths.LogDirectory);
                var path = Path.Combine(AppPaths.LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
                RollIfTooLarge(path, MaxFileBytes);
                File.AppendAllText(path, sb.ToString(), Utf8);
                if (lastCleanupDay != DateTime.Today)
                    Cleanup();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>檔案超過上限就改名成 *.1.log（取代上一份；<paramref name="rolledFileName"/> 給了就改成同資料夾的那個檔名），之後的記錄寫進新檔</summary>
    internal static void RollIfTooLarge(string path, long maxBytes, string? rolledFileName = null)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length >= maxBytes)
            File.Move(path, rolledFileName == null ? Path.ChangeExtension(path, null) + ".1.log" : Path.Combine(info.DirectoryName!, rolledFileName), overwrite: true);
    }

    /// <summary>刪掉 7 天前的記錄檔（啟動時呼叫；長時間執行時寫檔的背景執行緒每天再清一次）</summary>
    public static void Cleanup()
    {
        lastCleanupDay = DateTime.Today;
        try
        {
            if (!Directory.Exists(AppPaths.LogDirectory))
                return;
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-7))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
