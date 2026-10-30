namespace Myriagent.Hub;

/// <summary>視窗外框的狀態：照主題的灰、有對話執行中（帳戶色）、有對話等你回應（琥珀色）</summary>
internal enum WindowBorderState
{
    None,
    Running,
    Waiting,
}

/// <summary>一個視窗的外框：狀態與執行中那個對話的帳戶色（#RRGGBB；等你回應與沒有狀態時是 null）</summary>
internal readonly record struct WindowBorder(WindowBorderState State, string? AccountColor)
{
    public static WindowBorder None => new(WindowBorderState.None, null);
}

/// <summary>視窗開著的一個對話：執行中、等你回應、是不是畫面上看得到的分頁、帳戶色</summary>
internal readonly record struct WindowBorderConversation(bool Running, bool Waiting, bool Visible, string? AccountColor);

/// <summary>
/// 視窗外框跟著這個視窗裡的對話（2026-10-25 使用者要求，第七批視覺效果）：有對話等你回應時琥珀色、有對話執行中時帳戶色、都沒有就是主題原本的灰。
/// 多開的視窗各自看自己開著的對話；等你回應優先（和工作列一樣，要你動手的先講）。狀態改變時才設一次，沒有動畫；
/// Windows 10 沒有外框顏色的屬性，設了也沒有作用
/// </summary>
internal static class WindowBorderRules
{
    /// <summary>
    /// 決定一個視窗的外框：有等你回應的就是琥珀色；有執行中的取它的帳戶色（畫面上看得到的分頁優先，否則照開的順序取第一個）
    /// </summary>
    public static WindowBorder Decide(IEnumerable<WindowBorderConversation> conversations)
    {
        var list = conversations as IReadOnlyList<WindowBorderConversation> ?? conversations.ToList();
        if (list.Any(c => c.Waiting))
            return new WindowBorder(WindowBorderState.Waiting, null);
        var running = list.Where(c => c.Running).ToList();
        if (running.Count == 0)
            return WindowBorder.None;
        var pick = running.FirstOrDefault(c => c.Visible);
        if (!pick.Visible)
            pick = running[0];
        return new WindowBorder(WindowBorderState.Running, NormalizeColor(pick.AccountColor));
    }

    /// <summary>帳戶色只認 #RRGGBB（大小寫不拘），其他寫法當成沒有（用預設的帳戶色）</summary>
    public static string? NormalizeColor(string? color)
    {
        if (color is not { Length: 7 } || color[0] != '#')
            return null;
        for (int i = 1; i < 7; i++)
        {
            if (!Uri.IsHexDigit(color[i]))
                return null;
        }
        return color.ToUpperInvariant();
    }

    /// <summary>沒有帳戶色時用的顏色（AgentAccount.Color 的預設值）</summary>
    public const string DefaultAccountColor = "#D97757";

    /// <summary>主題原本的外框（NativeMethods.ApplyTitleBarTheme 設的那一組）</summary>
    public static (byte R, byte G, byte B) ThemeBorder(bool dark) => dark ? ((byte)0x2A, (byte)0x2A, (byte)0x30) : ((byte)0xDD, (byte)0xDD, (byte)0xE2);

    /// <summary>
    /// 外框的顏色（RGB）：沒有狀態是主題的灰；等你回應是琥珀色（深色主題同網頁的 --amber，淺色主題用亮一點的琥珀，太深的會看起來像黑框）；
    /// 執行中是帳戶色
    /// </summary>
    public static (byte R, byte G, byte B) Rgb(WindowBorder border, bool dark)
    {
        switch (border.State)
        {
            case WindowBorderState.None:
                return ThemeBorder(dark);
            case WindowBorderState.Waiting:
                return dark ? ((byte)0xE2, (byte)0xB3, (byte)0x40) : ((byte)0xC0, (byte)0x8A, (byte)0x12);
            case WindowBorderState.Running:
                var hex = NormalizeColor(border.AccountColor) ?? DefaultAccountColor;
                return (Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16), Convert.ToByte(hex.Substring(5, 2), 16));
            default:
                throw new ArgumentOutOfRangeException(nameof(border), border.State, "未知的外框狀態");
        }
    }
}
