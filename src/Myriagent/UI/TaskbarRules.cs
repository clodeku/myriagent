using System.Globalization;

namespace Myriagent.UI;

/// <summary>工作列按鈕的狀態：沒有、有對話執行中、有對話等你回應</summary>
internal enum TaskbarState
{
    None,
    Running,
    Waiting,
}

/// <summary>工作列按鈕要顯示的樣子：狀態、進度條要不要動（關掉動態效果時改成靜止的滿格）、疊在圖示上的數字（0＝不疊）</summary>
internal readonly record struct TaskbarView(TaskbarState State, bool Animated, int Overlay);

/// <summary>
/// 主視窗的工作列按鈕（2026-10-24 使用者要求）：有對話執行中時進度條是綠色的流動（不確定進度）、有對話等你回應時是琥珀色（暫停狀態、滿格）
/// 並在圖示上疊等你回應的對話數；兩種都有時等你回應優先（要你動手的先講）。縮小視窗、在別的程式裡也看得到。
/// 關掉「動態效果」時執行中改成靜止的滿格綠色，不讓工作列一直動
/// </summary>
internal static class TaskbarRules
{
    /// <summary>疊在圖示上的數字最多寫到 9，再多寫「9+」</summary>
    public const int OverlayMax = 9;

    public static TaskbarView Decide(int running, int waiting, bool visualEffects)
    {
        if (waiting > 0)
            return new TaskbarView(TaskbarState.Waiting, false, waiting);
        if (running > 0)
            return new TaskbarView(TaskbarState.Running, visualEffects, 0);
        return new TaskbarView(TaskbarState.None, false, 0);
    }

    /// <summary>疊在圖示上的字：「3」、「9+」；不疊是空字串</summary>
    public static string OverlayText(int count)
        => count <= 0 ? string.Empty : count > OverlayMax ? OverlayMax.ToString(CultureInfo.InvariantCulture) + "+" : count.ToString(CultureInfo.InvariantCulture);

    /// <summary>疊加圖示的說明（螢幕閱讀器念這一句）</summary>
    public static string OverlayDescription(int count) => count <= 0 ? string.Empty : $"{count} 個對話等你回應";
}
