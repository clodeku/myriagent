using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Microsoft.Web.WebView2.Core;

namespace Myriagent.UI;

/// <summary>WebView2 某個程序失敗後要怎麼處理</summary>
internal enum WebViewRecovery
{
    /// <summary>只記錄：WebView2 會自動重建該程序，網頁本身不受影響</summary>
    None,

    /// <summary>重新載入網頁：主框架的渲染程序結束，網頁已被換成錯誤頁</summary>
    Reload,

    /// <summary>渲染程序沒有回應：可能只是暫時忙碌，持續一段時間才問使用者要不要重新載入</summary>
    AskReload,

    /// <summary>瀏覽器程序結束：環境不能再用，要換一個新的 WebView2</summary>
    RecreateWebView,
}

/// <summary>主視窗收到關閉（FormClosing）時怎麼處理</summary>
internal enum MainClosingAction
{
    /// <summary>照常關閉（已經在結束流程裡）</summary>
    Close,

    /// <summary>藏到系統匣，對話在背景繼續</summary>
    HideToTray,

    /// <summary>只藏起主視窗：還有另外開的視窗看得到（多視窗），app 照常在那些視窗裡用，不提示系統匣</summary>
    Hide,

    /// <summary>正常結束（有執行中的對話先確認，代理程序收尾）</summary>
    Exit,

    /// <summary>Windows 詢問能不能登出／關機（WM_QUERYENDSESSION）：只存檔，不停代理也不結束——使用者或其他程式之後仍可能取消</summary>
    SaveForSessionEnd,

    /// <summary>來不及等程序收尾：立即存檔並結束（工作管理員結束等）</summary>
    ExitImmediately,
}

/// <summary>另外開的視窗（多視窗）關閉時怎麼辦</summary>
internal enum ExtraClosingAction
{
    /// <summary>使用者關掉這個視窗：關閉並從 settings.json 移除（下次不再開回來）</summary>
    Discard,

    /// <summary>app 結束、Windows 登出關機、工作管理員：關閉但留著設定（下次啟動開回來）</summary>
    Keep,

    /// <summary>不縮到系統匣、而且這是最後一個看得到的視窗：當成結束 app（有執行中的對話先確認）</summary>
    Exit,
}

/// <summary>桌面外殼（程式進入點、視窗、WebView2）的純函式規則</summary>
internal static class ShellRules
{
    public const string AppTitle = "Clode Myriagent";

    /// <summary>標題列至少要有這麼寬落在螢幕工作區內，才算抓得到（實體像素）</summary>
    public const int MinVisibleCaptionWidth = 120;

    /// <summary>判斷標題列是否可見時，看視窗上緣多高的一條（實體像素）</summary>
    public const int CaptionStripHeight = 32;

    // ───────────────────────────── 單一實例 ─────────────────────────────

    /// <summary>資料夾正規化：完整路徑（相對路徑以目前的工作目錄展開）、反斜線、去掉結尾的斜線（磁碟根目錄保留）</summary>
    public static string NormalizeDataRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// 單一實例的 mutex 與事件名稱後綴：預設資料夾沒有後綴，其他資料夾（--data、MYRIAGENT_AGENT_DATA）各自一組，探針可以和正常使用的 app 同時跑。
    /// 比對與雜湊都用正規化後的路徑（不分大小寫）：同一個資料夾換個寫法（結尾斜線、正斜線、相對路徑）一定得到同一組名稱，不會同時跑兩個實例互相覆寫檔案
    /// </summary>
    public static string InstanceSuffix(string root, string defaultRoot)
    {
        var normalized = NormalizeDataRoot(root);
        if (string.Equals(normalized, NormalizeDataRoot(defaultRoot), StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        return "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized.ToLowerInvariant())))[..12];
    }

    // ───────────────────────────── WebView2 程序失敗 ─────────────────────────────

    /// <summary>
    /// 程序失敗的處理方式（依 WebView2 文件）：只有瀏覽器程序與主框架渲染程序結束需要復原。
    /// GPU、utility、sandbox helper、外掛、子框架與不明程序結束時 WebView2 會自動重建，重新載入只會丟掉草稿與輸入框裡還沒送出的文字
    /// </summary>
    public static WebViewRecovery RecoveryFor(CoreWebView2ProcessFailedKind kind) => kind switch
    {
        CoreWebView2ProcessFailedKind.BrowserProcessExited => WebViewRecovery.RecreateWebView,
        CoreWebView2ProcessFailedKind.RenderProcessExited => WebViewRecovery.Reload,
        CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => WebViewRecovery.AskReload,
        _ => WebViewRecovery.None,
    };

    // ───────────────────────────── 關閉、Windows 登出與關機 ─────────────────────────────

    /// <summary>
    /// 主視窗收到關閉要求：使用者按關閉＝藏到系統匣或正常結束；Windows 登出／關機的詢問（WM_QUERYENDSESSION，CloseReason＝WindowsShutDown）只存檔；
    /// 其他（工作管理員結束等）立即結束。已經在結束流程裡就照常關閉
    /// </summary>
    /// <param name="otherWindowsVisible">還有另外開的視窗看得到（多視窗）：使用者關主視窗時只藏起來，app 照常在那些視窗裡用（像 Chrome 關一個視窗）</param>
    public static MainClosingAction ClosingAction(CloseReason reason, bool exiting, bool closeToTray, bool otherWindowsVisible = false)
    {
        if (exiting)
            return MainClosingAction.Close;
        return reason switch
        {
            CloseReason.UserClosing => otherWindowsVisible ? MainClosingAction.Hide : closeToTray ? MainClosingAction.HideToTray : MainClosingAction.Exit,
            CloseReason.WindowsShutDown => MainClosingAction.SaveForSessionEnd,
            _ => MainClosingAction.ExitImmediately,
        };
    }

    /// <summary>
    /// 主視窗關閉後要不要立即結束 app：只有 Windows 確定登出／關機（WM_ENDSESSION 且 wParam＝TRUE，WinForms 這時才觸發 FormClosed）。
    /// 取消關機（wParam＝FALSE）不會觸發 FormClosed，app 照常執行
    /// </summary>
    public static bool ExitWhenClosed(CloseReason reason, bool exiting) => reason == CloseReason.WindowsShutDown && !exiting;

    /// <summary>
    /// 另外開的視窗（多視窗）關閉時：結束 app 的途中、Windows 登出關機、工作管理員結束一律留著設定（下次啟動開回來）；
    /// 使用者關掉（含網頁搬走最後一個分頁後的 closeWindow，CloseReason.None）就移除——除非不縮到系統匣、又沒有其他看得到的視窗，那是要結束 app
    /// </summary>
    public static ExtraClosingAction ExtraWindowClosingAction(CloseReason reason, bool exiting, bool closeToTray, bool otherWindowsVisible)
    {
        if (exiting)
            return ExtraClosingAction.Keep;
        return reason switch
        {
            CloseReason.UserClosing => !closeToTray && !otherWindowsVisible ? ExtraClosingAction.Exit : ExtraClosingAction.Discard,
            CloseReason.None => ExtraClosingAction.Discard,
            _ => ExtraClosingAction.Keep,
        };
    }

    /// <summary>
    /// 再開一次 app（另一個程序啟動、送來「開新視窗」）：主視窗藏在系統匣（或還沒顯示）時把它叫出來，否則像 Chrome 一樣開一個新視窗
    /// </summary>
    public static bool LaunchAgainOpensWindow(bool primaryVisible) => primaryVisible;

    // ───────────────────────────── 視窗位置與大小 ─────────────────────────────

    /// <summary>邏輯像素（96 DPI）換算成指定 DPI 的實體像素（150% 縮放＝144 DPI，820×900 → 1230×1350）</summary>
    public static Size ScaleForDpi(Size logical, int dpi)
        => dpi <= 0 || dpi == 96 ? logical : new Size(Scale(logical.Width, dpi), Scale(logical.Height, dpi));

    private static int Scale(int value, int dpi) => (int)Math.Round(value * (double)dpi / 96, MidpointRounding.AwayFromZero);

    /// <summary>大小不超過工作區的指定比例（螢幕太小時，預設大小縮到放得下）</summary>
    public static Size FitWithin(Size size, Rectangle area, double widthRatio, double heightRatio)
        => new(Math.Min(size.Width, (int)(area.Width * widthRatio)), Math.Min(size.Height, (int)(area.Height * heightRatio)));

    /// <summary>最小大小不超過最小的螢幕工作區（1080p 螢幕開 175% 縮放時，邏輯 960×620 換算後比工作區還高，視窗會超出螢幕）</summary>
    public static Size FitMinimumSize(Size minimum, IReadOnlyList<Rectangle> workingAreas)
        => workingAreas.Count == 0
            ? minimum
            : new(Math.Min(minimum.Width, workingAreas.Min(a => a.Width)), Math.Min(minimum.Height, workingAreas.Min(a => a.Height)));

    /// <summary>
    /// 還原存檔的視窗位置：標題列（視窗上緣一條）落在各螢幕工作區內的部分加起來至少 MinVisibleCaptionWidth 寬，才照存檔放；
    /// 否則（拔掉外接螢幕、解析度變小）移進重疊最多（都沒有重疊就最近）的螢幕工作區內，比工作區大就縮成工作區大小。
    /// 沒有任何螢幕回 null（呼叫端改用預設位置）
    /// </summary>
    public static Rectangle? FitToScreens(Rectangle saved, IReadOnlyList<Rectangle> workingAreas)
    {
        if (workingAreas.Count == 0 || saved.Width <= 0 || saved.Height <= 0)
            return null;

        var strip = new Rectangle(saved.X, saved.Y, saved.Width, Math.Min(CaptionStripHeight, saved.Height));
        int visible = 0;
        foreach (var area in workingAreas)
        {
            var part = Rectangle.Intersect(area, strip);
            if (part.Height * 2 >= strip.Height)
                visible += part.Width;
        }
        if (visible >= Math.Min(MinVisibleCaptionWidth, saved.Width))
            return saved;

        var target = workingAreas
            .OrderByDescending(a => AreaOf(Rectangle.Intersect(a, saved)))
            .ThenBy(a => DistanceSquared(a, saved))
            .First();
        int width = Math.Min(saved.Width, target.Width);
        int height = Math.Min(saved.Height, target.Height);
        return new Rectangle(
            Math.Clamp(saved.X, target.Left, target.Right - width),
            Math.Clamp(saved.Y, target.Top, target.Bottom - height),
            width,
            height);
    }

    /// <summary>新視窗相對於來源視窗錯開多少（邏輯像素；Ctrl+Shift+N、系統匣「開新視窗」）</summary>
    public static readonly Size CascadeOffset = new(32, 32);

    /// <summary>拖出分頁開的新視窗：游標落在新視窗左上角往內這麼多的地方（邏輯像素；大約是窗格分頁列上第一個分頁的位置）</summary>
    public static readonly Size TearOffGrip = new(140, 18);

    /// <summary>
    /// 新視窗的位置與大小：和來源視窗（沒有最大化時的大小）一樣大，從來源錯開一點（atCursor 時改成讓游標落在新視窗的分頁列附近），
    /// 再照 FitToScreens 移進螢幕；比工作區大就縮。沒有任何螢幕時照原樣
    /// </summary>
    public static Rectangle NewWindowBounds(Rectangle source, Point? cursor, int dpi, IReadOnlyList<Rectangle> workingAreas)
    {
        Rectangle rect;
        if (cursor is { } at)
        {
            var grip = ScaleForDpi(TearOffGrip, dpi);
            rect = new Rectangle(at.X - grip.Width, at.Y - grip.Height, source.Width, source.Height);
            // 大小以游標所在的螢幕為準：放不下就縮到工作區的九成（拖到比較小的螢幕上）
            var screen = workingAreas.FirstOrDefault(a => a.Contains(at));
            if (!screen.IsEmpty)
            {
                var size = FitWithin(rect.Size, screen, 0.9, 0.9);
                rect = new Rectangle(Math.Clamp(rect.X, screen.Left, Math.Max(screen.Left, screen.Right - size.Width)),
                    Math.Clamp(rect.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - size.Height)), size.Width, size.Height);
            }
        }
        else
        {
            var offset = ScaleForDpi(CascadeOffset, dpi);
            rect = new Rectangle(source.X + offset.Width, source.Y + offset.Height, source.Width, source.Height);
            // 錯開後掉出工作區右下：回到那個工作區的左上角（像 Chrome 的串聯排列繞回來）
            var area = workingAreas.FirstOrDefault(a => a.IntersectsWith(source));
            if (!area.IsEmpty && (rect.Right > area.Right || rect.Bottom > area.Bottom))
                rect = new Rectangle(area.Left + offset.Width, area.Top + offset.Height, Math.Min(rect.Width, area.Width - offset.Width), Math.Min(rect.Height, area.Height - offset.Height));
        }
        return FitToScreens(rect, workingAreas) ?? rect;
    }

    private static long AreaOf(Rectangle r) => r.IsEmpty ? 0 : (long)r.Width * r.Height;

    private static long DistanceSquared(Rectangle a, Rectangle b)
    {
        long dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        long dy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
        return dx * dx + dy * dy;
    }

    // ───────────────────────────── 標題 ─────────────────────────────

    /// <summary>
    /// 視窗標題跟著網頁的 document.title（「Clode Myriagent － 目前對話的標題」，Web/titlerules.js；多個彈出視窗在工作列與 Alt+Tab 才分得出來）；
    /// 空白或還沒載入完（WebView2 暫時用網址當標題）時用 app 名稱
    /// </summary>
    public static string WindowTitle(string? documentTitle)
        => string.IsNullOrWhiteSpace(documentTitle) || documentTitle.Contains(AgentMarkdownRenderer.Host, StringComparison.OrdinalIgnoreCase)
            ? AppTitle
            : documentTitle.Trim();
}

/// <summary>
/// 渲染程序沒有回應時要不要問使用者重新載入（WebView2 在沒有回應的期間每隔幾秒回報一次，恢復後就不再回報）：
/// 連續回報（間隔不超過 StreakGap）持續 AskAfter 以上才問；問過或使用者選「繼續等待」後，AskAgainAfter 內不再問
/// </summary>
internal sealed class UnresponsiveTracker
{
    public static readonly TimeSpan StreakGap = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan AskAfter = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromSeconds(60);

    private DateTime? streakStart;
    private DateTime lastReport;
    private DateTime? quietUntil;

    /// <summary>收到一次「沒有回應」；回傳這次要不要問使用者</summary>
    public bool Report(DateTime now)
    {
        if (streakStart == null || now - lastReport > StreakGap)
        {
            streakStart = now;
            quietUntil = null;
        }
        lastReport = now;
        if (now - streakStart.Value < AskAfter || (quietUntil is { } until && now < until))
            return false;
        quietUntil = now + AskAgainAfter;
        return true;
    }

    /// <summary>使用者選擇繼續等待</summary>
    public void Dismissed(DateTime now) => quietUntil = now + AskAgainAfter;

    /// <summary>已重新載入（或渲染程序結束）：重新計算</summary>
    public void Reset()
    {
        streakStart = null;
        quietUntil = null;
    }
}
