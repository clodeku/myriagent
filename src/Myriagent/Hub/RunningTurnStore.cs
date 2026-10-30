using Myriagent.Agents;
using Myriagent.Core;

namespace Myriagent.Hub;

/// <summary>
/// conversations\{id}\running.json 的內容：這個對話有一輪正在執行、或上一輪回覆完了還在等背景工作回報（AgentHub.InterruptedTurns.cs）。
/// 一輪開始時寫、結束或使用者按停止時刪（正常結束時背景工作還在跑的改寫成等背景工作的快照，背景工作都沒了才刪）；
/// 執行期間每 30 秒（維護）、開始高風險操作時與 app 結束時更新快照。app 結束、當掉或電腦重新開機時它留在磁碟上，下次啟動據此接續。
/// 只加欄位（設計 8.8）：舊版不認得這個檔、不讀也不刪
/// </summary>
internal sealed class RunningTurnRecord
{
    public int Schema { get; set; } = 1;

    /// <summary>這一輪開始的時間（Unix 毫秒）</summary>
    public long StartedAt { get; set; }

    /// <summary>最後一次確認這一輪還在執行的時間（Unix 毫秒）：當成中斷的時間</summary>
    public long SeenAt { get; set; }

    /// <summary>app 正常結束時寫的（結束、Windows 登出或關機）；false＝最後一次是執行中的快照（之後當掉、被強制結束或斷電）</summary>
    public bool ClosedByApp { get; set; }

    /// <summary>快照當下正在執行的工具（還在等核准的不算）</summary>
    public List<AgentInterruptedTool> InterruptedTools { get; set; } = new();

    /// <summary>快照當下等使用者回應的請求（重新啟動後已失效，代理要重新提出）</summary>
    public List<string> PendingRequests { get; set; } = new();

    /// <summary>快照當下有存活的背景工作（app 結束時跟著停了）</summary>
    public bool BackgroundWork { get; set; }

    /// <summary>
    /// 這一輪已經回覆完、只在等背景工作回報（代理回一句「等測試跑完」就結束了這一輪）：一輪結束時背景工作還在跑就不刪這個檔，
    /// 改寫成這種快照，背景工作自己結束（或被換帳戶等使用者的操作停掉）才刪。比這個欄位舊的版本讀到時當成一般的執行中接續
    /// </summary>
    public bool WaitingBackground { get; set; }

    /// <summary>快照當下存活的背景工作（「執行指令：npm test」，最多 BackgroundWorkRules.MaxTasks 個）；給接續訊息列出要確認的工作</summary>
    public List<string> BackgroundTasks { get; set; } = new();

    /// <summary>快照當下在執行的高風險操作（CliRestartRules.FindRisk 的一行摘要）；有值就不自動接續</summary>
    public string? RiskOperation { get; set; }

    /// <summary>執行這一輪的 CLI 版本（顯示用）</summary>
    public string? CliVersion { get; set; }

    /// <summary>這一輪是連續第幾次自動接續（使用者自己送出的是 0）：達上限就不再自動接續，避免一直當掉一直接續</summary>
    public int AutoResumes { get; set; }
}

/// <summary>conversations\{id}\running.json 的讀寫（原子寫入）</summary>
internal static class RunningTurnStore
{
    public const string FileName = "running.json";

    public static string PathFor(string conversationDirectory) => Path.Combine(conversationDirectory, FileName);

    public static void Save(string conversationDirectory, RunningTurnRecord record) => JsonFile.Write(PathFor(conversationDirectory), record);

    /// <summary>刪掉 running.json（本來就沒有也算成功）；刪不掉的例外照樣丟出</summary>
    public static void Delete(string conversationDirectory)
    {
        try
        {
            File.Delete(PathFor(conversationDirectory));
        }
        catch (DirectoryNotFoundException)
        {
            // 資料夾已不在（對話被刪除）：檔案也不在
        }
    }

    /// <summary>
    /// 讀 running.json：Ok 時是正規化過的內容（手改或別的版本寫的合法 JSON，清單的 null 換成空的、null 項目略過）。
    /// 損毀的已由 JsonFile 改名成 .corrupt-*（回 Corrupt、null）；Unreadable 時檔案還在
    /// </summary>
    public static (JsonReadStatus Status, RunningTurnRecord? Record) TryLoad(string conversationDirectory)
    {
        var (status, value, _) = JsonFile.TryRead<RunningTurnRecord>(PathFor(conversationDirectory));
        if (status != JsonReadStatus.Ok || value == null)
            return (status == JsonReadStatus.Ok ? JsonReadStatus.Missing : status, null);
        value.InterruptedTools = (value.InterruptedTools ?? new()).Where(t => t != null).ToList();
        value.PendingRequests = (value.PendingRequests ?? new()).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        value.BackgroundTasks = (value.BackgroundTasks ?? new()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        return (status, value);
    }
}
