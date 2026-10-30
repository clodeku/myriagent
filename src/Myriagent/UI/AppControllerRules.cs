using Myriagent.Agents;
using Myriagent.Cli;

namespace Myriagent.UI;

/// <summary>
/// 系統匣通知（氣球提示）：標題、內容、點了要開的對話（null＝只開主視窗）、顯示的時間（UTC），
/// Merged＝由幾則 CLI 換版彙總合併而成（每一行前面已經帶著各自的標題）
/// </summary>
internal sealed record TrayNotice(string Title, string Message, string? ConversationId, DateTime ShownAtUtc, bool Merged = false);

/// <summary>AppController（WinForms 外殼）的純函式規則：結束前的確認、系統匣通知、app 更新後彈出視窗的位置</summary>
internal static class AppControllerRules
{
    /// <summary>氣球提示顯示多久（ShowBalloonTip 的 timeout）：這段時間內陸續到的 CLI 換版彙總合併成一則</summary>
    public static readonly TimeSpan BalloonDuration = TimeSpan.FromSeconds(6);

    /// <summary>Windows 氣球提示的標題最多 63 字（NOTIFYICONDATA.szInfoTitle），超過會被系統截掉</summary>
    public const int MaxBalloonTitle = 63;

    /// <summary>Windows 氣球提示的內容最多 255 字（NOTIFYICONDATA.szInfo），超過會被系統截掉</summary>
    public const int MaxBalloonText = 255;

    /// <summary>不同 CLI 的換版彙總合併後的標題</summary>
    public const string MergedCliTitle = "CLI 已更新";

    private static string CliName(AgentProvider provider) => AgentProviders.ProductName(provider);

    /// <summary>
    /// app 有視窗在前景（app 更新「使用者沒在用」的反面，設計 9.1 條件 1）：前景視窗屬於這個程序、看得見而且沒有最小化。
    /// 看程序而不是 Form.ActiveForm：選檔、選資料夾、MessageBox（結束確認）這些原生對話框不是 WinForms 的 Form，ActiveForm 是 null，
    /// 但它們一樣是 app 的視窗，開著時不能排空重啟（不然選檔落空，結束確認框會把「結束」變成「重啟並繼續跑」）
    /// </summary>
    /// <param name="foregroundProcessId">GetForegroundWindow 的視窗所屬的程序（沒有前景視窗為 0）</param>
    public static bool IsAppForeground(uint foregroundProcessId, bool foregroundVisible, bool foregroundMinimized, int selfProcessId)
        => foregroundProcessId != 0 && foregroundProcessId == (uint)selfProcessId && foregroundVisible && !foregroundMinimized;

    /// <summary>
    /// 結束 app 前要使用者確認的事（空清單＝直接結束）：有執行中的對話（結束會先停止它們，下次開啟時自動接續，AgentHub.InterruptedTurns.cs）；
    /// 〔熱更新〕npm 正在替換 CLI 的檔案（CliState.NpmInstalling：結束會讓 CLI 安裝不完整）；
    /// 更新還在等對話停下或等其他程序結束（還沒開始換檔：結束會取消這次更新）；其他安裝／更新進行中（建議等它完成）。
    /// <paramref name="backgroundConversations"/>：代理這一輪結束了、背景工作還在跑的對話（AgentHub.BackgroundWaitingCount）——
    /// 結束會連同 CLI 程序中斷它們，下次開啟時請代理確認它們做到哪裡、接著做（running.json 的等背景工作快照，AgentHub.InterruptedTurns.cs）
    /// </summary>
    public static IReadOnlyList<string> ExitWarnings(int runningConversations, IEnumerable<CliState> clis, int backgroundConversations = 0)
    {
        var warnings = new List<string>();
        if (runningConversations > 0)
            warnings.Add($"還有 {runningConversations} 個對話正在執行，結束會先停止它們，下次開啟 app 時會自動接續。");
        if (backgroundConversations > 0)
            warnings.Add($"還有 {backgroundConversations} 個對話的背景工作在執行（代理在等它們回報），結束會中斷這些背景工作，下次開啟 app 時會請代理確認它們做到哪裡、接著做。");
        foreach (var state in clis)
        {
            var name = CliName(state.Provider);
            if (state.NpmInstalling)
                warnings.Add($"{name} CLI 更新中（npm 正在替換檔案），現在結束會讓 CLI 安裝不完整。");
            else if (state.UpdatePhase is CliUpdatePhase.Draining or CliUpdatePhase.WaitingForProcesses)
                warnings.Add($"{name} CLI 正在等對話停下以便更新（還沒開始換檔），結束會取消這次更新。");
            else if (state.Busy)
                warnings.Add($"{name} CLI 正在安裝或更新，建議等它完成再結束，以免 CLI 停在更新到一半的狀態。");
        }
        return warnings;
    }

    /// <summary>
    /// 結束前要不要確認的嚴重程度（每種 CLI）：npm 正在替換檔案（3：結束會讓 CLI 安裝不完整）＞還在等對話停下或等其他程序結束
    /// （2：還沒換檔，結束會取消這次更新）＞其他安裝／更新進行中（1）＞沒有（0）
    /// </summary>
    public static int ExitRisk(CliState state)
        => state.NpmInstalling ? 3
            : state.UpdatePhase is CliUpdatePhase.Draining or CliUpdatePhase.WaitingForProcesses ? 2
            : state.Busy ? 1
            : 0;

    /// <summary>
    /// 結束確認框開著的期間情況變嚴重了（MessageBox 是巢狀訊息迴圈，更新照常推進：排空完成、npm 開始換檔；或原本沒有執行中的對話現在有了）：
    /// 使用者按「是」時看到的警告已經過時，要以新的警告再問一次。變輕（例如換檔完成）不必再問
    /// </summary>
    public static bool NeedsExitReconfirm(int confirmedRunning, IEnumerable<CliState> confirmed, int running, IEnumerable<CliState> current)
    {
        if (confirmedRunning == 0 && running > 0)
            return true;
        var before = confirmed.ToDictionary(s => s.Provider, ExitRisk);
        return current.Any(s => ExitRisk(s) > (before.TryGetValue(s.Provider, out var risk) ? risk : 0));
    }

    /// <summary>
    /// 下一則要顯示的系統匣通知。中樞在一波 CLI 換版都處理完後送一則彙總（conversationId 為 null：點了開主視窗，不逐一通知各對話）；
    /// 前一則彙總還顯示著（BalloonDuration 內）又來一則（例如 Claude Code 與 Codex 同時更新）就合併成一則，後到的不會蓋掉先到的：
    /// 標題相同只把內容接在後面，不同就改用「CLI 已更新」、每一行帶著原本的標題。對話的通知照舊一則換一則（點了回到那個對話）。
    /// 標題與內容去掉頭尾空白並截到 Windows 的上限；標題空白時用 app 名稱
    /// </summary>
    public static TrayNotice NextTrayNotice(TrayNotice? shown, string? title, string? message, string? conversationId, DateTime nowUtc)
    {
        var t = string.IsNullOrWhiteSpace(title) ? ShellRules.AppTitle : title.Trim();
        var m = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
        bool mergeable = conversationId == null && shown is { ConversationId: null }
                         && nowUtc >= shown.ShownAtUtc && nowUtc - shown.ShownAtUtc < BalloonDuration;
        if (!mergeable)
            return new TrayNotice(Clip(t, MaxBalloonTitle), Clip(m, MaxBalloonText), conversationId, nowUtc);

        if (!shown!.Merged && string.Equals(shown.Title, Clip(t, MaxBalloonTitle), StringComparison.Ordinal))
            return new TrayNotice(shown.Title, Clip(Join(shown.Message, m), MaxBalloonText), null, nowUtc);
        var first = shown.Merged ? shown.Message : Line(shown.Title, shown.Message);
        return new TrayNotice(MergedCliTitle, Clip(Join(first, Line(t, m)), MaxBalloonText), null, nowUtc, Merged: true);
    }

    /// <summary>
    /// app 更新後重開彈出視窗的位置（resume.json 記的 x, y, w, h）：剛好 4 個數字、寬高都大於 100、和某個螢幕的工作區相交才照原樣還原；
    /// 否則回 null，用預設位置（螢幕拔掉或解析度變了，不能把視窗開在看不到的地方）
    /// </summary>
    public static Rectangle? RestorableBounds(int[]? bounds, IEnumerable<Rectangle> workingAreas)
    {
        if (bounds is not { Length: 4 })
            return null;
        var rect = new Rectangle(bounds[0], bounds[1], bounds[2], bounds[3]);
        if (rect.Width <= 100 || rect.Height <= 100)
            return null;
        return workingAreas.Any(area => area.IntersectsWith(rect)) ? rect : null;
    }

    /// <summary>一行「標題：內容」（內容空白時只有標題）</summary>
    private static string Line(string title, string message) => message.Length == 0 ? title : $"{title}：{message}";

    /// <summary>把 next 接在 text 後面（換行）；text 已經包含同樣的內容就不重複</summary>
    private static string Join(string text, string next)
    {
        if (next.Length == 0 || text.Contains(next, StringComparison.Ordinal))
            return text;
        return text.Length == 0 ? next : text + "\n" + next;
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
