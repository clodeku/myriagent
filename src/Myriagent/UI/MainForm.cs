using Myriagent.Core;
using Myriagent.Hub;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Myriagent.UI;

/// <summary>
/// 完整的視窗：整個介面是一個 WebView2（帳戶側欄、分頁與窗格、儀表板都在網頁裡）；標題列配合主題。
/// 主視窗之外可以另外開幾個（多視窗，像 Chrome；extra 不是 null）：共用同一個中樞，各有自己的版面，關掉就只關那一個
/// </summary>
internal sealed class MainForm : Form
{
    /// <summary>介面驗證過的最小大小與預設大小上限（邏輯像素，依螢幕縮放換算成實體像素）</summary>
    private static readonly Size LogicalMinimumSize = new(960, 620);

    private static readonly Size LogicalDefaultSize = new(1500, 940);

    private readonly AppController app;
    private WebView2 webView;
    private readonly Label loading;
    private bool initialized;

    /// <summary>換版幫手啟動、交棒前主視窗藏著（KeepHiddenOnStart）：擋下 Application.Run 顯示主視窗的那一次</summary>
    private bool keepHidden;

    /// <summary>另外開的視窗的設定（多視窗）；主視窗是 null</summary>
    private readonly AppWindowState? extra;

    /// <summary>啟動時開回來的另外開的視窗：第一次顯示不搶焦點（換版重開時使用者可能正在用別的程式）</summary>
    private bool showInactive;

    /// <param name="extra">另外開的視窗的設定（null＝主視窗）</param>
    /// <param name="showInactive">第一次顯示時不搶焦點</param>
    public MainForm(AppController app, AppWindowState? extra = null, bool showInactive = false)
    {
        this.app = app;
        this.extra = extra;
        this.showInactive = showInactive;
        Text = ShellRules.AppTitle;
        Icon = AppIcons.Window;
        // 程式建立的表單沒有設計時的縮放基準，大小直接以實體像素生效：依 DPI 換算（150% 縮放時最小 1440×930），但不超過螢幕工作區
        MinimumSize = ShellRules.FitMinimumSize(ShellRules.ScaleForDpi(LogicalMinimumSize, DeviceDpi), Screen.AllScreens.Select(s => s.WorkingArea).ToList());
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = app.IsDark ? Color.FromArgb(0x18, 0x18, 0x1B) : Color.FromArgb(0xF7, 0xF7, 0xF8);
        KeyPreview = false;

        webView = new WebView2 { Dock = DockStyle.Fill };
        ((System.ComponentModel.ISupportInitialize)webView).BeginInit();
        loading = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = extra == null ? "Clode Myriagent 啟動中…" : "載入中…",
            ForeColor = app.IsDark ? Color.FromArgb(0x9A, 0x9A, 0xA6) : Color.FromArgb(0x60, 0x60, 0x6B),
            Font = new Font("Segoe UI", 11f),
        };
        Controls.Add(webView);
        Controls.Add(loading);
        loading.BringToFront();
        ((System.ComponentModel.ISupportInitialize)webView).EndInit();

        RestorePlacement(extra == null ? app.Settings.Window : extra.Placement);
        View = CreateView(webView);
    }

    public AgentWebView View { get; private set; }

    /// <summary>主視窗（第一個視窗）；false＝另外開的視窗</summary>
    public bool IsPrimary => extra == null;

    /// <summary>另外開的視窗在 settings.json 的設定（主視窗是 null）</summary>
    public AppWindowState? Extra => extra;

    public string WindowKey => extra?.Key ?? WindowRules.PrimaryKey;

    protected override bool ShowWithoutActivation => showInactive;

    /// <summary>網頁顯示完成時觸發（實機探針等用；重建 WebView2 後仍有效）</summary>
    public event Action? ViewLoaded;

    private AgentWebView CreateView(WebView2 control)
    {
        var view = new MainWebView(this, control, app.Hub, app.Thumbnails, WindowKey, IsPrimary);
        view.Loaded += () =>
        {
            loading.Visible = false;
            ViewLoaded?.Invoke();
        };
        view.BrowserCrashed += () => _ = RecreateWebViewAsync();
        return view;
    }

    /// <summary>
    /// WebView2 瀏覽器程序結束後重建：換一個新的 WebView2 控制項與環境，網頁重新載入後依存檔的版面還原分頁（對話狀態都在中樞，不會遺失）
    /// </summary>
    private async Task RecreateWebViewAsync()
    {
        if (IsDisposed || app.Exiting)
            return;
        Log.Warn("WebView2 瀏覽器程序結束，重建介面");
        View.Detach();
        Controls.Remove(webView);
        try
        {
            webView.Dispose();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
        loading.Text = "介面元件意外結束，正在重新載入…";
        loading.Visible = true;
        webView = new WebView2 { Dock = DockStyle.Fill };
        ((System.ComponentModel.ISupportInitialize)webView).BeginInit();
        Controls.Add(webView);
        loading.BringToFront();
        ((System.ComponentModel.ISupportInitialize)webView).EndInit();
        View = CreateView(webView);
        initialized = false;
        await Task.Delay(500);
        StartInitialization();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyTitleBarTheme(Handle, app.IsDark);
    }

    /// <summary>
    /// 啟動時不顯示主視窗（設計 9.3：無人值守的換版前主視窗藏著）。Application.Run 會在訊息迴圈開始前同步顯示 ApplicationContext.MainForm，
    /// 只是不呼叫 Show() 擋不住：這裡讓那一次只建立 handle（WebView2 照常初始化、送 ready 給健康閘門），
    /// 呼叫端在訊息迴圈開始後以 StopKeepingHidden 恢復，之後系統匣、通知與再次啟動的 ShowMain 照常顯示
    /// </summary>
    public void KeepHiddenOnStart() => keepHidden = true;

    public void StopKeepingHidden() => keepHidden = false;

    protected override void SetVisibleCore(bool value)
    {
        if (value && keepHidden)
        {
            if (!IsHandleCreated)
                CreateHandle();
            return;
        }
        base.SetVisibleCore(value);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        showInactive = false;
        StartInitialization();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        View.SetMemoryTarget(low: !Visible);
        // 從系統匣叫回來：工作列按鈕是新的，照最後的狀態重設進度與疊加數字
        if (Visible)
            app.ReapplyTaskbar();
    }

    /// <summary>Explorer 建立（或重開後重建）工作列按鈕時送的訊息</summary>
    private static readonly int WmTaskbarButtonCreated = RegisterWindowMessage("TaskbarButtonCreated");

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (WmTaskbarButtonCreated != 0 && m.Msg == WmTaskbarButtonCreated)
            app.ReapplyTaskbar();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 最小化時網頁停掉動畫與取樣（藏到系統匣走 OnVisibleChanged，控制項跟著視窗隱藏）
        View?.SetMinimized(WindowState == FormWindowState.Minimized);
    }

    /// <summary>建立 WebView2 並載入介面（只做一次；可在視窗顯示前呼叫）</summary>
    public async void StartInitialization()
    {
        if (initialized)
            return;
        initialized = true;
        _ = Handle;           // 確保視窗與 WebView2 控制項的 handle 已建立（視窗還沒顯示時也要）
        _ = webView.Handle;
        try
        {
            await View.InitializeAsync(string.Empty, app.IsDark);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (MessageBox.Show(this, "這台電腦缺少 Microsoft Edge WebView2 執行階段，介面無法顯示。\n\n要開啟微軟的下載頁面嗎？（安裝完成後重新開啟即可）",
                    "Clode Myriagent", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                app.OpenWithShell("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            }
            app.ExitImmediately();
        }
        catch (Exception ex)
        {
            Log.Error("介面初始化失敗", ex);
            MessageBox.Show(this, $"介面初始化失敗：{ex.Message}", "Clode Myriagent", MessageBoxButtons.OK, MessageBoxIcon.Error);
            app.ExitImmediately();
        }
    }

    public void ApplyTheme(bool dark)
    {
        BackColor = dark ? Color.FromArgb(0x18, 0x18, 0x1B) : Color.FromArgb(0xF7, 0xF7, 0xF8);
        NativeMethods.ApplyTitleBarTheme(Handle, dark);
        View.SetBackground(dark);
    }

    /// <summary>
    /// 還原上次的位置：標題列要抓得到（拔掉外接螢幕後只剩一角在畫面上時，移回螢幕內）；沒有存檔就在主螢幕置中。
    /// 存檔是實體像素：只排除比 100% 縮放的最小大小還小的明顯錯誤值，其餘不足的部分由 MinimumSize 撐開
    /// </summary>
    private void RestorePlacement(WindowPlacementInfo? placement)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
        if (placement != null && placement.Width >= LogicalMinimumSize.Width && placement.Height >= LogicalMinimumSize.Height)
        {
            var saved = new Rectangle(placement.X, placement.Y, Math.Max(placement.Width, MinimumSize.Width), Math.Max(placement.Height, MinimumSize.Height));
            if (ShellRules.FitToScreens(saved, Screen.AllScreens.Select(s => s.WorkingArea).ToList()) is { } rect)
            {
                Bounds = rect;
                if (placement.Maximized)
                    WindowState = FormWindowState.Maximized;
                return;
            }
        }
        var size = ShellRules.FitWithin(ShellRules.ScaleForDpi(LogicalDefaultSize, DeviceDpi), area, 0.85, 0.88);
        Bounds = new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2, size.Width, size.Height);
    }

    public WindowPlacementInfo CapturePlacement()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        return new WindowPlacementInfo
        {
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == FormWindowState.Maximized,
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (extra != null)
        {
            OnExtraWindowClosing(e);
            base.OnFormClosing(e);
            return;
        }
        app.Settings.Window = CapturePlacement();
        switch (ShellRules.ClosingAction(e.CloseReason, app.Exiting, app.Settings.CloseToTray, app.OtherWindowsVisible(this)))
        {
            case MainClosingAction.HideToTray:
                e.Cancel = true;
                Hide();
                app.ShowTrayHintOnce();
                return;
            case MainClosingAction.Hide:
                // 還有另外開的視窗看得到：只藏起主視窗（系統匣、再開一次 app 叫得回來），不提示「仍在背景執行」
                e.Cancel = true;
                Hide();
                return;
            case MainClosingAction.Exit:
                e.Cancel = true;
                _ = app.ExitAsync();
                return;
            case MainClosingAction.SaveForSessionEnd:
                // Windows 詢問能不能登出／關機（WM_QUERYENDSESSION）：使用者或其他程式之後仍可能取消，
                // 這時只存檔、不停代理也不結束；確定結束（WM_ENDSESSION）時才在 OnFormClosed 收尾
                app.Settings.Save();
                app.Hub.SaveRunningConversations();
                break;
            case MainClosingAction.ExitImmediately:
                // 工作管理員結束等：來不及等程序收尾，立即存檔並結束（視窗照常關閉）
                app.ExitImmediately(closeMainForm: false);
                break;
        }
        base.OnFormClosing(e);
    }

    /// <summary>另外開的視窗關閉：使用者關掉的移除設定、結束 app 的途中留著（下次開回來）；不縮到系統匣的最後一個視窗當成結束 app</summary>
    private void OnExtraWindowClosing(FormClosingEventArgs e)
    {
        switch (ShellRules.ExtraWindowClosingAction(e.CloseReason, app.Exiting, app.Settings.CloseToTray, app.OtherWindowsVisible(this)))
        {
            case ExtraClosingAction.Discard:
                app.ForgetWindow(this);
                break;
            case ExtraClosingAction.Keep:
                app.RememberWindow(this);
                break;
            case ExtraClosingAction.Exit:
                e.Cancel = true;
                _ = app.ExitAsync();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(e), e.CloseReason, "不認得的視窗關閉動作");
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Windows 確定登出／關機（WM_ENDSESSION）：程序隨時會被結束，來不及等代理收尾，立即存檔並結束（由主視窗做；另外開的視窗只關自己）
        if (extra == null && ShellRules.ExitWhenClosed(e.CloseReason, app.Exiting))
            app.ExitImmediately(closeMainForm: false);
        View.Detach();
        base.OnFormClosed(e);
    }
}
