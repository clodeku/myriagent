using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Myriagent.UI;

/// <summary>
/// 主視窗工作列按鈕的進度與疊加圖示（ITaskbarList3）：狀態由 <see cref="TaskbarRules"/> 決定，這裡只呼叫 Windows。
/// 只在 UI 執行緒用（COM 物件是 STA）；按鈕還沒建立（視窗藏在系統匣）時呼叫會失敗而且不報錯，
/// 所以記住最後的樣子，視窗顯示出來或 Explorer 重開（TaskbarButtonCreated）時 <see cref="Reapply"/>。
/// 取不到 COM 物件（沒有工作列的環境）就整個停用，不影響其他功能
/// </summary>
internal sealed class TaskbarProgress : IDisposable
{
    private ITaskbarList3? list;
    private bool unavailable;
    private TaskbarView? applied;
    private TaskbarView last;
    private bool hasLast;
    private readonly Dictionary<string, Icon> overlays = new(StringComparer.Ordinal);

    /// <summary>最後一次呼叫 Windows 有沒有成功（測試用：有工作列按鈕時應該是 true）</summary>
    internal bool LastPushOk { get; private set; }

    /// <summary>套用（和上次一樣就不呼叫 Windows）</summary>
    public void Apply(Form form, TaskbarView view)
    {
        last = view;
        hasLast = true;
        if (applied == view)
            return;
        LastPushOk = Push(form, view);
        if (LastPushOk)
            applied = view;
    }

    /// <summary>按鈕重建了（視窗顯示、Explorer 重開）：照最後的樣子重設一次</summary>
    public void Reapply(Form form)
    {
        applied = null;
        if (hasLast && Push(form, last))
            applied = last;
    }

    private bool Push(Form form, TaskbarView view)
    {
        if (unavailable || form.IsDisposed || !form.IsHandleCreated)
            return false;
        var tb = Taskbar();
        if (tb is null)
            return false;
        var hwnd = form.Handle;
        try
        {
            switch (view.State)
            {
                case TaskbarState.Running when view.Animated:
                    tb.SetProgressState(hwnd, TBPF.Indeterminate);
                    break;
                case TaskbarState.Running:
                    tb.SetProgressState(hwnd, TBPF.Normal);
                    tb.SetProgressValue(hwnd, 100, 100);
                    break;
                case TaskbarState.Waiting:
                    tb.SetProgressState(hwnd, TBPF.Paused);
                    tb.SetProgressValue(hwnd, 100, 100);
                    break;
                case TaskbarState.None:
                    tb.SetProgressState(hwnd, TBPF.NoProgress);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(view), view.State, "未知的工作列狀態");
            }
            var text = TaskbarRules.OverlayText(view.Overlay);
            tb.SetOverlayIcon(hwnd, text.Length == 0 ? IntPtr.Zero : OverlayIcon(text, form.DeviceDpi).Handle, TaskbarRules.OverlayDescription(view.Overlay));
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private ITaskbarList3? Taskbar()
    {
        if (list is not null || unavailable)
            return list;
        try
        {
            var tb = (ITaskbarList3)new TaskbarListClass();
            tb.HrInit();
            list = tb;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or NotSupportedException)
        {
            unavailable = true;
        }
        return list;
    }

    /// <summary>疊加圖示：琥珀色圓底、黑字的數字（依 DPI 放大，同一個字只畫一次）</summary>
    private Icon OverlayIcon(string text, int dpi)
    {
        var size = Math.Max(16, (int)Math.Round(16 * dpi / 96.0));
        var key = text + "@" + size;
        if (overlays.TryGetValue(key, out var cached))
            return cached;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(0xF5, 0xA6, 0x23));
            g.FillEllipse(fill, 0, 0, size - 1, size - 1);
            using var font = new Font("Segoe UI", size * (text.Length > 1 ? 0.5f : 0.68f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Color.FromArgb(0x1A, 0x14, 0x05));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(text, font, ink, new RectangleF(0, 0, size, size + 1), format);
        }
        var handle = bmp.GetHicon();
        try
        {
            var icon = (Icon)Icon.FromHandle(handle).Clone();
            overlays[key] = icon;
            return icon;
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        foreach (var icon in overlays.Values)
            icon.Dispose();
        overlays.Clear();
        if (list is not null)
        {
            Marshal.FinalReleaseComObject(list);
            list = null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private enum TBPF
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    /// <summary>ITaskbarList3（方法順序照 vtable：ITaskbarList、ITaskbarList2、ITaskbarList3，用不到的也要列）</summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TBPF tbpFlags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint dwReserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButton);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListClass
    {
    }
}
