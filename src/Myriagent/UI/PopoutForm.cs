using Myriagent.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Myriagent.UI;

/// <summary>彈出視窗：只顯示一個對話（多螢幕並排用），與主視窗共用同一個 WebView2 環境與中樞</summary>
internal sealed class PopoutForm : Form
{
    /// <summary>最小大小與預設大小（邏輯像素，依螢幕縮放換算成實體像素）</summary>
    private static readonly Size LogicalMinimumSize = new(520, 480);

    private static readonly Size LogicalDefaultSize = new(820, 900);

    private readonly AppController app;
    private readonly WebView2 webView;
    private bool initialized;

    public PopoutForm(AppController app, string conversationId)
    {
        this.app = app;
        ConversationId = conversationId;
        Text = ShellRules.AppTitle;
        Icon = AppIcons.Window;
        // 程式建立的表單沒有設計時的縮放基準，大小直接以實體像素生效：依 DPI 換算（150% 縮放時 820×900 → 1230×1350），
        // 放不下就縮到置中所在螢幕（滑鼠所在的螢幕）工作區的九成
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        MinimumSize = ShellRules.FitMinimumSize(ShellRules.ScaleForDpi(LogicalMinimumSize, DeviceDpi), new[] { area });
        Size = ShellRules.FitWithin(ShellRules.ScaleForDpi(LogicalDefaultSize, DeviceDpi), area, 0.9, 0.9);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = app.IsDark ? Color.FromArgb(0x18, 0x18, 0x1B) : Color.FromArgb(0xF7, 0xF7, 0xF8);

        webView = new WebView2 { Dock = DockStyle.Fill };
        ((System.ComponentModel.ISupportInitialize)webView).BeginInit();
        Controls.Add(webView);
        ((System.ComponentModel.ISupportInitialize)webView).EndInit();
        View = new PopoutWebView(this, webView, app.Hub, app.Thumbnails, conversationId);
        // 瀏覽器程序結束：主視窗會自己重建，彈出視窗直接關閉（使用者可再從主視窗彈出）
        View.BrowserCrashed += Close;
    }

    public string ConversationId { get; }

    public PopoutWebView View { get; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyTitleBarTheme(Handle, app.IsDark);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (initialized)
            return;
        initialized = true;
        try
        {
            await View.InitializeAsync($"?mode=popout&conversation={Uri.EscapeDataString(ConversationId)}", app.IsDark);
        }
        catch (Exception ex)
        {
            Log.Error("彈出視窗初始化失敗", ex);
            Close();
        }
    }

    public void ApplyTheme(bool dark)
    {
        BackColor = dark ? Color.FromArgb(0x18, 0x18, 0x1B) : Color.FromArgb(0xF7, 0xF7, 0xF8);
        NativeMethods.ApplyTitleBarTheme(Handle, dark);
        View.SetBackground(dark);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 最小化時網頁停掉動畫（同主視窗，AgentWebView.SetMinimized）；建構式設定大小時 View 還沒建立
        View?.SetMinimized(WindowState == FormWindowState.Minimized);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        View.Detach();
        app.OnPopoutClosed(this);
        base.OnFormClosed(e);
    }
}
