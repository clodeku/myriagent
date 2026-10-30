using Myriagent.Sync;
using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 伺服器記的網頁未讀（WP4.2 計畫步 12A；答 6、D45）的純函式規則。
/// <list type="bullet">
/// <item><b>看見</b>：一條連線的 ClientState 的 <c>visible</c> 與 <c>focused</c> 都為真，而且對話在 D25 的清單裡（最後一次 <c>visible</c> 指令的清單，
/// 或以 <see cref="ConversationIds.IsWireId"/> 過濾過的 <c>openConversationIds</c>）。等於 web-protocol 的「顯示在前景視窗的畫面上」；
/// 只是 open 不算（同 D25 的「看得到」，<see cref="WatchRules"/>）。</item>
/// <item><b>標未讀</b>：新的上行事件（<see cref="ConversationChange.Fresh"/>；對帳重送的舊事件與重建都不算），而且是五種注意事件之一
/// （question、approval、plan、completed、failed）或一輪結束的 <c>conv.turn</c>，而且沒有任何連線看見它。</item>
/// <item><b>清未讀</b>：任何一條連線開始看見它（ClientState 換了、<c>visible</c> 指令）。</item>
/// </list>
/// </summary>
internal static class ConversationUnreadRules
{
    /// <summary>這條連線看見這個對話嗎：<paramref name="inVisibleSet"/>＝對話在 D25 的清單裡；另要 ClientState 的 visible 與 focused 都為真</summary>
    public static bool Seen(BrowserClientState? state, bool inVisibleSet) => inVisibleSet && state is { Visible: true, Focused: true };

    /// <summary>這條連線看見這個對話嗎（<see cref="Seen(BrowserClientState?, bool)"/>，清單取自 <paramref name="viewer"/>）</summary>
    public static bool Seen(ViewerState viewer, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(conversationId);
        return Seen(viewer.State, ConversationGatewayRules.CanSee(viewer.State, viewer.Visible, conversationId));
    }

    /// <summary>所有連線看見的對話（聯集）：只看 visible 與 focused 都為真的連線，清單照 <see cref="WatchRules.SeenBy"/></summary>
    public static IReadOnlySet<string> SeenSet(IEnumerable<ViewerState> viewers)
    {
        ArgumentNullException.ThrowIfNull(viewers);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var viewer in viewers)
        {
            if (viewer.State is not { Visible: true, Focused: true })
                continue;
            seen.UnionWith(WatchRules.SeenBy(viewer));
        }
        return seen;
    }

    /// <summary>這筆變更要標未讀嗎（還要沒有任何連線看見）：新的上行事件，而且是五種注意事件之一或一輪結束</summary>
    public static bool MarksUnread(ConversationChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!change.Fresh)
            return false;
        return change.Kind switch
        {
            ConversationChangeKind.Attention => ConversationAttentionKinds.IsKnown(change.AttentionKind),
            ConversationChangeKind.Turn => string.Equals(change.TurnPhase, ConversationTurnPhases.Completed, StringComparison.Ordinal),
            ConversationChangeKind.Meta or ConversationChangeKind.Deleted or ConversationChangeKind.Item or ConversationChangeKind.ItemRemoved
                or ConversationChangeKind.Queue or ConversationChangeKind.Purged or ConversationChangeKind.Rebuilt => false,
            _ => throw new ArgumentOutOfRangeException(nameof(change), change.Kind, "未知的對話變更種類"),
        };
    }
}
