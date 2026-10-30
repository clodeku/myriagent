using System.Runtime.InteropServices;

namespace MyriagentTools.UpdateE2e;

/// <summary>
/// app 程序的頂層視窗（設計 9.3：交棒前主視窗藏著，換版幫手啟動的新版照樣藏著）：列出看得見、沒有擁有者的頂層視窗
/// （同 Process.MainWindowHandle 的判斷，但不快取、列出全部；最小化的也算看得見），從外部把視窗最小化
/// </summary>
internal static class AppWindows
{
    private const int SW_MINIMIZE = 6;
    private const uint GW_OWNER = 4;

    /// <summary>這個程序看得見、沒有擁有者的頂層視窗（主視窗、彈出視窗；系統匣圖示與 WebView2 的視窗不算）</summary>
    public static IReadOnlyList<IntPtr> Visible(int processId)
    {
        var list = new List<IntPtr>();
        EnumWindows((hwnd, lParam) =>
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)processId && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero)
                list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>最小化（ShowWindow 對別的執行緒的視窗會等它處理完，回傳時 app 的 WindowState 已經是 Minimized）</summary>
    public static bool Minimize(IntPtr hwnd)
    {
        _ = ShowWindow(hwnd, SW_MINIMIZE);
        return IsIconic(hwnd);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
