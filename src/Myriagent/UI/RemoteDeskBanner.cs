using System.Runtime.InteropServices;
using Myriagent.RemoteDesk;

namespace Myriagent.UI;

/// <summary>
/// 被遠端操控時，主螢幕上方置中、一直在最上層的提示列（docs/sync-protocol.md 14.16）：「〈發起者〉正在遠端操控這台電腦」與「中斷」。
/// 不搶焦點（WS_EX_NOACTIVATE）、不出現在工作列，並以 <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c> 不出現在傳出去的畫面裡
/// （看的一方才不會被它擋住；Windows 10 2004 以前的版本不支援時照樣顯示、只是會被擷取到）。UI 執行緒建立與關閉
/// </summary>
internal sealed class RemoteDeskBanner : Form, IDeskIndicator
{
    private readonly Action disconnect;

    public RemoteDeskBanner(string text, Action disconnect)
    {
        this.disconnect = disconnect;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0xC2, 0x41, 0x0C);
        ForeColor = Color.White;
        Padding = new Padding(14, 6, 6, 6);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Microsoft JhengHei UI", 10f, FontStyle.Bold);

        var layout = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        var label = new Label { Text = "● " + text, AutoSize = true, Margin = new Padding(0, 6, 12, 0), ForeColor = Color.White };
        var button = new Button
        {
            Text = "中斷",
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = Color.FromArgb(0xC2, 0x41, 0x0C),
            Margin = Padding.Empty,
            Cursor = Cursors.Hand,
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => this.disconnect();
        layout.Controls.Add(label);
        layout.Controls.Add(button);
        Controls.Add(layout);
    }

    /// <summary>不搶焦點地顯示（建立後呼叫）</summary>
    public static RemoteDeskBanner? ShowFor(string text, Action disconnect)
    {
        var banner = new RemoteDeskBanner(text, disconnect);
        banner.Show();
        banner.PlaceTopCenter();
        return banner;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x00000008 /* WS_EX_TOPMOST */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            SetWindowDisplayAffinity(Handle, 0x00000011 /* WDA_EXCLUDEFROMCAPTURE */);
        }
        catch (EntryPointNotFoundException)
        {
            // 太舊的 Windows：照樣顯示
        }
    }

    private void PlaceTopCenter()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + 8);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
