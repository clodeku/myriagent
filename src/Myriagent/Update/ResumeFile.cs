using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Myriagent.Update;

internal enum ResumeMode
{
    /// <summary>新版啟動後自動送出接續訊息</summary>
    Continue,

    /// <summary>不自動接續：加提示、標頭給「繼續」（WhyNot 是原因）</summary>
    None,
}

/// <summary>
/// 一個停下等新版接續的對話（設計 9.3）：ContinuationRules.Build、WhyNotContinue 與「繼續」按鈕需要的輸入。
/// 列舉存成名稱：寫的與讀的是不同版本（舊版排空時寫、新版或退回的舊版讀），列舉順序改了也不會讀錯
/// </summary>
internal sealed class ResumeConversation
{
    public string Id { get; set; } = string.Empty;

    [JsonConverter(typeof(StringEnumConverter))]
    public AgentProvider Provider { get; set; }

    public string AccountId { get; set; } = string.Empty;

    public string? SessionId { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public ResumeMode Mode { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public CliNoContinueReason? WhyNot { get; set; }

    /// <summary>這一輪被停下的時間（Unix 毫秒）</summary>
    public long? StoppedAt { get; set; }

    public List<AgentInterruptedTool> InterruptedTools { get; set; } = new();

    public List<string> PendingRequests { get; set; } = new();

    public bool BackgroundWorkStopped { get; set; }

    /// <summary>WhyNot=RiskyTool 時被打斷的操作（一行摘要）</summary>
    public string? RiskOperation { get; set; }

    /// <summary>停下時的 CLI 版本（顯示用；接續一律用目前的 CLI）</summary>
    public string? CliVersion { get; set; }
}

internal sealed record ResumePopout(string ConversationId, int[]? Bounds);

internal sealed record ResumeDraft(string ConversationId, string Text);

/// <summary>還沒送出第一則的草稿對話：附件留在原本的對話資料夾（draftId），結束時與啟動清理都不刪</summary>
internal sealed record ResumeNewConversation(string DraftId, string AccountId, string? Folder, string Text, List<AgentAttachment> Attachments);

internal sealed class ResumeUi
{
    public List<ResumePopout> Popouts { get; set; } = new();

    public List<ResumeDraft> Drafts { get; set; } = new();

    public List<ResumeNewConversation> NewConversations { get; set; } = new();

    /// <summary>
    /// 交棒時主視窗藏著（關到系統匣、還沒顯示過或最小化）：換版幫手啟動的新版（或退回的舊版）照樣不顯示主視窗，
    /// 無人值守的換版不能把視窗叫到使用者面前（ResumeIntentRules.StartMainHidden）。舊版寫的檔案沒有這個欄位＝false，照常顯示
    /// </summary>
    public bool MainHidden { get; set; }
}

/// <summary>%LOCALAPPDATA%\Myriagent\update\resume.json（設計 9.3）：舊版排空時寫、新版（或退回的舊版）啟動後讀</summary>
internal sealed class AppResumeFile
{
    public int Schema { get; set; } = 1;

    public string Token { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public string? To { get; set; }

    /// <summary>Unix 毫秒（UTC）</summary>
    public long CreatedAt { get; set; }

    public List<ResumeConversation> Conversations { get; set; } = new();

    /// <summary>排空期間在閒置對話送出、被延後的（佇列第一則）：新版啟動後自動送出</summary>
    public List<string> DeferredSends { get; set; } = new();

    /// <summary>
    /// 排空關掉或停下時程序開著 Claude Remote Control 的對話（設計 9.2、9.5）：新版啟動後沒有自動接續、也沒有延後送出的，
    /// 重開程序、只開 Remote Control 接回原本的遠端工作階段（不送訊息）。合併 main 的 Remote Control 時加的欄位，不認得它的版本照樣讀得懂（設計 8.8）
    /// </summary>
    public List<string> RemoteControl { get; set; } = new();

    public ResumeUi Ui { get; set; } = new();
}

/// <summary>
/// 啟動時讀到的 resume.json（ResumeFileStore.LoadAtStartup）。Unreadable：檔案在但讀不到（被別的程式不共享讀取地開著、沒有權限），
/// File 是 null 但不能當成沒有——這次不照它接續、不清草稿殘留、也不刪它，留給下次啟動（設計 9.3、9.5）
/// </summary>
internal sealed record StartupResume(AppResumeFile? File, bool Unreadable)
{
    public static StartupResume None { get; } = new(null, false);
}

internal static class ResumeFileStore
{
    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "update", "resume.json");

    /// <summary>原子寫入（JsonFile.Write：暫存檔、落盤、改名）</summary>
    public static void Save(string dataRoot, AppResumeFile file) => JsonFile.Write(PathFor(dataRoot), file);

    /// <summary>
    /// 讀 resume.json 並回傳讀取狀態：Ok 時是正規化過的內容（內容有 null 的合法 JSON 照 ResumeIntentRules.Normalize 補齊、略過），其他狀態是 null。
    /// 損毀的已由 JsonFile 改名成 .corrupt-*；Unreadable 時檔案還在，呼叫端不可以覆寫或刪掉它
    /// </summary>
    public static (JsonReadStatus Status, AppResumeFile? File) TryLoad(string dataRoot)
    {
        var (status, value, _) = JsonFile.TryRead<AppResumeFile>(PathFor(dataRoot));
        return status == JsonReadStatus.Ok && value != null ? (status, ResumeIntentRules.Normalize(value)) : (status, null);
    }

    /// <summary>讀 resume.json；不存在或讀不到回 null</summary>
    public static AppResumeFile? Load(string dataRoot) => TryLoad(dataRoot).File;

    /// <summary>
    /// 啟動時讀 resume.json：讀不到（被占用）時隔 250 毫秒再讀，最多再等 retryFor；一直讀不到回 Unreadable（不是「沒有」）
    /// </summary>
    public static StartupResume LoadAtStartup(string dataRoot, TimeSpan retryFor)
    {
        var deadline = DateTime.UtcNow + retryFor;
        while (true)
        {
            var (status, file) = TryLoad(dataRoot);
            if (status != JsonReadStatus.Unreadable)
                return file == null ? StartupResume.None : new StartupResume(file, Unreadable: false);   // 沒有、損毀（已改名）都當成沒有
            if (DateTime.UtcNow >= deadline)
            {
                Log.Warn("resume.json 讀不到：這次啟動不照它接續、不清草稿殘留，檔案留給下次啟動");
                return new StartupResume(null, Unreadable: true);
            }
            Thread.Sleep(250);
        }
    }

    /// <summary>
    /// 刪掉 resume.json；刪掉了或本來就沒有回 true。其他程式短暫占用（防毒、備份軟體開著檔沒給 FILE_SHARE_DELETE）時
    /// 隔 80 毫秒再試、最多 3 次（同 JsonFile.TryRead）；還是刪不掉回 false：呼叫端要稍後再刪，
    /// 留下的檔案會讓下次一般啟動判成 TokenMismatch（不看建立時間），把已接續的對話當成上次中斷
    /// </summary>
    public static bool Delete(string dataRoot)
    {
        var path = PathFor(dataRoot);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(path);   // 檔案不存在不丟例外
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;   // 連 update 資料夾都沒有：檔案本來就不在
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(80);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"刪除 resume.json 失敗：{ex.Message}");
                return false;
            }
        }
    }
}
