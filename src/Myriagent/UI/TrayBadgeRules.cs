namespace Myriagent.UI;

/// <summary>系統匣圖示右下角的狀態：沒有、執行中（綠點）、等你回應（琥珀色圓與數字）</summary>
internal readonly record struct TrayBadge(TaskbarState State, string Text);

/// <summary>右下角的圓（含外圍挖空的一圈）在圖示裡的位置：左上角與直徑，單位是像素</summary>
internal readonly record struct TrayBadgeBounds(int X, int Y, int Diameter, int Gap);

/// <summary>
/// 系統匣圖示的狀態點（2026-10-25 第八批）：app 縮到系統匣時工作列按鈕看不到，系統匣圖示也要看得出有對話在跑、在等你。
/// 和工作列同一組數字、同一個優先順序（等你回應優先）；圖示只在狀態變了才換，沒有動畫（閃爍要一直換圖示）
/// </summary>
internal static class TrayBadgeRules
{
    public static TrayBadge Decide(int running, int waiting)
    {
        var view = TaskbarRules.Decide(running, waiting, visualEffects: false);
        return view.State switch
        {
            TaskbarState.Waiting => new TrayBadge(TaskbarState.Waiting, TaskbarRules.OverlayText(view.Overlay)),
            TaskbarState.Running => new TrayBadge(TaskbarState.Running, string.Empty),
            TaskbarState.None => new TrayBadge(TaskbarState.None, string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(running), view.State, "未知的工作列狀態"),
        };
    }

    /// <summary>
    /// 狀態點的位置：貼著右下角；執行中是一半大小的點，等你回應要寫數字，佔約七成。
    /// 外圍挖空 1 px（大圖示 2 px），深色、淺色的工作列上都和圖示分得開
    /// </summary>
    public static TrayBadgeBounds Bounds(int size, TaskbarState state)
    {
        if (size < 8)
            throw new ArgumentOutOfRangeException(nameof(size), size, "圖示太小");
        int d = state switch
        {
            TaskbarState.Running => (int)Math.Round(size * 0.5),
            TaskbarState.Waiting => (int)Math.Round(size * 0.6875),
            TaskbarState.None => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未知的工作列狀態"),
        };
        int gap = size >= 32 ? 2 : 1;
        return new TrayBadgeBounds(size - d, size - d, d, d == 0 ? 0 : gap);
    }

    /// <summary>數字的字級（像素）：一個字約圓的 0.8，「9+」兩個字縮到 0.6</summary>
    public static float FontPixels(int diameter, string text) => diameter * (text.Length > 1 ? 0.6f : 0.8f);
}
