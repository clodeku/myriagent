using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Myriagent.Launch;

/// <summary>一個程序：PID 加上建立時間（FILETIME，UTC）。PID 會被重複使用，接回時兩個都對得上才算同一個程序</summary>
internal sealed record LaunchProcessRef(int Pid, long Created);

/// <summary>
/// 執行列在這台開的、和 app 脫鉤的程式（<c>launch\runs.json</c> 的一筆）：app 重新啟動後據此接回，之後停止、重新建置並執行時先結束它。
/// 新檔，前一版不認得就不讀（設計 8.8）；種類存名稱，之後新增的種類舊版略過那一筆
/// </summary>
internal sealed class LaunchRunRecord
{
    public string? TargetId { get; set; }

    /// <summary><see cref="LaunchRules.KindName"/></summary>
    public string? Kind { get; set; }

    public string? Name { get; set; }
    public string? ProjectPath { get; set; }
    public string? RelativePath { get; set; }
    public string? Detail { get; set; }
    public string? Framework { get; set; }
    public string? Script { get; set; }

    /// <summary>這次開始執行的時間（Unix 毫秒）</summary>
    public long SinceMs { get; set; }

    /// <summary>暫停中：程序在 app 結束後仍然是暫停的，接回後照樣顯示暫停、按繼續才恢復</summary>
    public bool Paused { get; set; }

    /// <summary>輸出檔（狀態資料夾裡的檔名，<see cref="LaunchRules.LogFileName"/>）</summary>
    public string? LogFile { get; set; }

    /// <summary>屬於這個程式的程序（主程序在第一個）</summary>
    public List<LaunchProcessRef> Processes { get; set; } = [];
}

internal sealed class LaunchRunsFile
{
    public List<LaunchRunRecord> Runs { get; set; } = [];
}

// 和 app 脫鉤的執行（2026-10-23 使用者要求：app 重啟不影響執行列啟動的程式，但要記得它，重新建置並執行時先結束它；2026-10-25 起只有 Release 模式）
internal static partial class LaunchRules
{
    /// <summary>狀態資料夾（<c>&lt;資料根目錄&gt;\launch</c>）裡記錄執行中程式的檔案</summary>
    public const string StateFileName = "runs.json";

    /// <summary>執行的程式的輸出檔超過這個大小時，讀完就清空（程式以只能附加的方式寫，清空後接著寫在開頭）</summary>
    public const long MaxLogFileBytes = 16L * 1024 * 1024;

    /// <summary>讀輸出檔、檢查主程序結束的間隔</summary>
    public static readonly TimeSpan LogPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>接回時輸出開頭的說明</summary>
    public const string ReattachedNote = "（Clode Myriagent 重新啟動前開的程式，已接回）";

    /// <summary>
    /// app 自我更新要等的階段：建置（重啟會中斷它，之後也不會執行）與停止中（可能接著重新建置）。執行中、暫停中的程式不算：
    /// Release 模式的不受 app 重啟影響，Debug 模式的另由 <see cref="DiesWithApp"/> 說明
    /// </summary>
    public static bool BlocksAppRestart(LaunchPhase phase) => phase switch
    {
        LaunchPhase.Building or LaunchPhase.Stopping => true,
        LaunchPhase.Idle or LaunchPhase.Running or LaunchPhase.Paused => false,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null),
    };

    // ───── 啟動程序 ─────

    /// <summary>
    /// 執行檔的完整路徑：有路徑的照用（存在才回），只有名稱的在 PATH 上找（沒有副檔名的加 .exe）；找不到回 null。
    /// 不找 app 自己的目前資料夾（CreateProcess 自己找會先找那裡）
    /// </summary>
    public static string? ResolveExecutable(string fileName, string? pathVariable, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        if (Path.IsPathRooted(fileName))
            return fileExists(fileName) ? fileName : null;
        if (fileName.IndexOfAny(['\\', '/']) >= 0)
            return null;
        var name = Path.HasExtension(fileName) ? fileName : fileName + ".exe";
        foreach (var raw in (pathVariable ?? string.Empty).Split(';'))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0 || !Path.IsPathRooted(dir))
                continue;
            string candidate;
            try
            {
                candidate = Path.Combine(dir, name);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (fileExists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// CreateProcess 的命令列：執行檔（含空白時加引號）加上逐一照 Windows 的規則（CommandLineToArgvW）加引號、跳脫的參數——
    /// 和 .NET 的 ProcessStartInfo.ArgumentList 組出來的一樣
    /// </summary>
    public static string CommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        if (executable.Contains(' ') || executable.Contains('\t'))
            sb.Append('"').Append(executable).Append('"');
        else
            sb.Append(executable);
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            AppendArgument(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendArgument(StringBuilder sb, string arg)
    {
        if (arg.Length != 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        for (int i = 0; i < arg.Length;)
        {
            var ch = arg[i++];
            if (ch == '\\')
            {
                int count = 1;
                while (i < arg.Length && arg[i] == '\\')
                {
                    i++;
                    count++;
                }
                if (i == arg.Length)
                    sb.Append('\\', count * 2);   // 接著是結尾的引號：反斜線都要成對
                else if (arg[i] == '"')
                {
                    sb.Append('\\', count * 2 + 1).Append('"');
                    i++;
                }
                else
                    sb.Append('\\', count);
            }
            else if (ch == '"')
                sb.Append('\\').Append('"');
            else
                sb.Append(ch);
        }
        sb.Append('"');
    }

    /// <summary>CreateProcess 的環境區塊（「名稱=值\0…」，最後再一個 \0 由呼叫端的字串結尾補上）：依名稱排序（不分大小寫），後面的同名變數蓋掉前面的</summary>
    public static string EnvironmentBlock(IEnumerable<KeyValuePair<string, string>> variables)
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in variables)
        {
            if (!string.IsNullOrEmpty(key) && !key.Contains('\0') && !value.Contains('\0'))
                map[key] = value;
        }
        var sb = new StringBuilder();
        foreach (var (key, value) in map)
            sb.Append(key).Append('=').Append(value).Append('\0');
        if (map.Count == 0)
            sb.Append('\0');
        return sb.ToString();
    }

    // ───── 輸出檔與狀態檔 ─────

    /// <summary>這次執行的輸出檔名（目標的鍵＋開始時間，每次不同：上一次的程式萬一沒結束、還握著舊檔，也不會寫進新的一次）</summary>
    public static string LogFileName(string targetId, DateTime utcNow)
        => $"{targetId}-{utcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}.log";

    [GeneratedRegex(@"^t[0-9a-f]{12}-\d{17}\.log$")]
    private static partial Regex LogFilePattern();

    /// <summary>是 <see cref="LogFileName"/> 產生的檔名（清理只刪這種、狀態檔裡的檔名不是這種就不用：不會指到狀態資料夾以外）</summary>
    public static bool IsLogFileName(string? name) => name != null && LogFilePattern().IsMatch(name);

    public static LaunchRunRecord ToRecord(LaunchTarget target, DateTime? since, bool paused, string? logFile, IReadOnlyList<LaunchProcessRef> processes) => new()
    {
        TargetId = target.Id,
        Kind = KindName(target.Kind),
        Name = target.Name,
        ProjectPath = target.ProjectPath,
        RelativePath = target.RelativePath,
        Detail = target.Detail,
        Framework = target.Framework,
        Script = target.Script,
        SinceMs = since is { } s ? ProtocolUnixMs(s) : 0,
        Paused = paused,
        LogFile = logFile,
        Processes = processes.ToList(),
    };

    /// <summary>狀態檔的一筆還原成目標；不完整、種類不認得、鍵和專案檔對不上回 null（那一筆略過）</summary>
    public static LaunchTarget? TargetFromRecord(LaunchRunRecord record)
    {
        if (string.IsNullOrEmpty(record.ProjectPath) || !Path.IsPathRooted(record.ProjectPath))
            return null;
        var id = TargetId(record.ProjectPath);
        if (record.TargetId != id)
            return null;
        LaunchKind? kind = record.Kind switch
        {
            "dotnet" => LaunchKind.DotNet,
            "electron" => LaunchKind.Electron,
            _ => null,
        };
        if (kind is not { } k)
            return null;
        var name = string.IsNullOrEmpty(record.Name) ? Path.GetFileNameWithoutExtension(record.ProjectPath) : record.Name;
        return new LaunchTarget(id, k, name, record.ProjectPath, record.RelativePath ?? Path.GetFileName(record.ProjectPath),
            record.Detail ?? string.Empty, record.Framework, record.Script);
    }

    /// <summary>狀態檔記的開始時間（UTC）；沒有回 null</summary>
    public static DateTime? SinceFromRecord(LaunchRunRecord record)
        => record.SinceMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(record.SinceMs).UtcDateTime : null;
}
