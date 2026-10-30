namespace Myriagent.Hub;

/// <summary>拖曳中的分頁（來源視窗在 dragstart 時送 tabDrag）：放開時搬移要用的來源與輸入框裡還沒送出的字</summary>
internal sealed record TabDragInfo(string ConversationId, string SourceViewId, string? Text, DateTime StartedAt);

/// <summary>新開的視窗就緒時要搬進去的對話（拖到視窗外、「移到新視窗」）</summary>
internal sealed record PendingMoveIn(string ConversationId, string SourceViewId, string? Text);

/// <summary>多視窗（像 Chrome：同一個程序開多個完整視窗，分頁可以在視窗之間拖曳搬移）的純函式規則</summary>
internal static class WindowRules
{
    /// <summary>主視窗的 WindowKey（版面存在 settings.json 的 Layout；其他視窗存在 Windows[].Layout）</summary>
    public const string PrimaryKey = "main";

    /// <summary>
    /// 拖曳的分頁資訊多久內還算數：來源的 dragend 與目標的 drop 經兩個 WebView2 送來，先後不一定，
    /// 所以來源的 tabDragEnd 不清掉它，目標晚到的 tabMoveIn 照樣用得到；放太久的不算（下一次拖曳一定會先送新的 tabDrag）
    /// </summary>
    public static readonly TimeSpan TabDragTtl = TimeSpan.FromSeconds(15);

    /// <summary>輸入框的字最多帶多少字元過去（和送出的上限同級；再長的截掉也不會讓搬移失敗）</summary>
    public const int MaxCarriedText = 200_000;

    /// <summary>新視窗的鍵（settings.json 的 Windows[].Key）</summary>
    public static string NewKey() => Guid.NewGuid().ToString("N");

    /// <summary>完整的視窗（主視窗或另外開的視窗）；彈出視窗、遠端與同步的檢視不是</summary>
    public static bool IsWindow(IAgentView view) => !view.IsPopout && view is IWindowView;

    /// <summary>
    /// 主視窗：不是彈出視窗，而且不是另外開的視窗。不是 IWindowView 的一般檢視（測試的假視窗）也算——多視窗之前只有一個主視窗
    /// </summary>
    public static bool IsPrimary(IAgentView view) => !view.IsPopout && (view is not IWindowView w || w.IsPrimaryWindow);

    /// <summary>init 的 windowRole：主視窗 "primary"、另外開的視窗 "extra"、其他（彈出視窗）null</summary>
    public static string? RoleOf(IAgentView view)
        => view.IsPopout ? null : IsPrimary(view) ? "primary" : "extra";

    /// <summary>
    /// 分頁搬到 target 時要請哪些視窗拿掉它：顯示這個對話的其他完整視窗，加上拖曳的來源（還沒切過去的背景分頁沒送過 open，不在 ViewIds 裡）。
    /// 彈出視窗照舊顯示，不拿掉；目標自己不算
    /// </summary>
    public static IReadOnlyList<string> SourcesToClear(IEnumerable<string> conversationViewIds, Func<string, bool> isWindowView, string targetViewId, string? dragSourceViewId)
    {
        var result = new List<string>();
        foreach (var id in conversationViewIds)
        {
            if (id != targetViewId && isWindowView(id) && !result.Contains(id))
                result.Add(id);
        }
        if (dragSourceViewId != null && dragSourceViewId != targetViewId && isWindowView(dragSourceViewId) && !result.Contains(dragSourceViewId))
            result.Add(dragSourceViewId);
        return result;
    }

    /// <summary>這次搬移用不用得到記下的拖曳資訊：同一個對話、還沒過期</summary>
    public static bool DragApplies(TabDragInfo? drag, string conversationId, DateTime now)
        => drag != null && drag.ConversationId == conversationId && now - drag.StartedAt <= TabDragTtl && now >= drag.StartedAt;

    /// <summary>網頁帶來的輸入框文字：空白的不帶、太長的截掉</summary>
    public static string? CarriedText(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Length > MaxCarriedText ? text[..MaxCarriedText] : text;
}
