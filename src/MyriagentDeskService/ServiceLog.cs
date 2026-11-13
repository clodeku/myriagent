using System.Globalization;
using System.Text;

namespace MyriagentDesk;

/// <summary>
/// 服務與安裝器的記錄。只寫在服務自己的安裝資料夾底下（<c>%ProgramFiles%\Myriagent\DeskService\logs\</c>，只有系統管理員能寫、使用者讀得到）：
/// 系統權限的程序不可以把檔案寫到使用者建得了資料夾的地方（ProgramData、Temp）——使用者先放一個連結，系統權限就替他寫到別處。
/// 沒有指定資料夾（前景模式、還沒裝好）時只寫到標準錯誤。寫不進去一律吞掉：記錄不能讓服務停下來
/// </summary>
internal static class ServiceLog
{
    private const int KeepDays = 14;

    private static readonly object Gate = new();

    private static string? directory;

    private static string? cleanedOn;

    public static void UseDirectory(string? dir)
    {
        lock (Gate)
            directory = dir;
    }

    public static void Info(string text) => Write("INFO", text);

    public static void Warn(string text) => Write("WARN", text);

    public static void Error(string text, Exception? ex = null)
        => Write("ERROR", ex is null ? text : $"{text}：{ex.GetType().Name}：{ex.Message}");

    private static void Write(string level, string text)
    {
        var now = DateTime.Now;
        var line = string.Create(CultureInfo.InvariantCulture, $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {text}");
        lock (Gate)
        {
            try
            {
                if (directory is null)
                {
                    Console.Error.WriteLine(line);
                    return;
                }

                Directory.CreateDirectory(directory);
                var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                File.AppendAllText(Path.Combine(directory, $"service-{day}.log"), line + "\n", new UTF8Encoding(false));
                if (cleanedOn != day)
                {
                    cleanedOn = day;
                    Clean(directory, now);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 寫不進去就算了
            }
        }
    }

    private static void Clean(string dir, DateTime now)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "service-*.log"))
        {
            if (File.GetLastWriteTime(file) < now.AddDays(-KeepDays))
                File.Delete(file);
        }
    }
}
