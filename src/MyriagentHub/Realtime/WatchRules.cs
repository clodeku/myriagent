using Myriagent.Sync;
using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>一條瀏覽器連線的觀看狀態（<see cref="WatchRules"/> 的輸入）：最後一次 ClientState 與最後一次 <c>visible</c> 指令的清單</summary>
internal sealed record ViewerState(BrowserClientState? State, IReadOnlyList<string> Visible)
{
    public static ViewerState Of(BrowserConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ViewerState(connection.State, connection.VisibleConversationIds());
    }

    /// <summary>
    /// 同 <see cref="Of(BrowserConnection)"/>，協作人的連線（多人協作）只留分享給他的對話（<paramref name="canSee"/>）：
    /// 協作人回報的其他對話不能讓擁有者電腦送即時更新
    /// </summary>
    public static ViewerState Of(BrowserConnection connection, Func<string, bool> canSee)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(canSee);
        if (!connection.IsCollaborator)
            return Of(connection);
        var state = connection.State;
        if (state is not null)
            state = state with { OpenConversationIds = state.OpenConversationIds.Where(canSee).ToList() };
        return new ViewerState(state, connection.VisibleConversationIds().Where(canSee).ToList());
    }
}

/// <summary>
/// 觀看清單的純函式規則（WP4.2 計畫步 12；D25、P4）：<c>WatchChanged</c> 只列「看得到」的對話——ClientState 的 <c>visible</c> 為真，
/// 而且對話在這條連線最後一次 <c>visible</c> 指令的清單裡、或在以 <see cref="ConversationIds.IsWireId"/> 過濾過的 <c>openConversationIds</c> 裡（只是 open 不算）；
/// 再只留那台裝置擁有、政策 <c>full</c>、沒刪除的，依 id 排序、最多 <c>MaxWatchIds</c> 個，每次整份取代。
/// 擁有者據它決定送哪些對話的即時更新與 <c>IsSeenRemotely</c>（WP4.3），所以 <c>localOnly</c>、降級與刪除的對話一律不列
/// </summary>
internal static class WatchRules
{
    /// <summary>這條連線看得到的對話 id（可能重複、沒查目錄）；ClientState 不可見或還沒送過時是空的</summary>
    public static IEnumerable<string> SeenBy(ViewerState viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (viewer.State is not { Visible: true } state)
            return [];
        return viewer.Visible.Concat(state.OpenConversationIds).Where(ConversationIds.IsWireId);
    }

    /// <summary>這台裝置的觀看清單（依 id 排序、最多 <paramref name="max"/> 個）</summary>
    public static IReadOnlyList<string> WatchedBy(string ownerDeviceId, IEnumerable<ViewerState> viewers, Func<string, ConversationEntry?> find, int max)
    {
        ArgumentNullException.ThrowIfNull(ownerDeviceId);
        return ByOwner(viewers, find, max).TryGetValue(ownerDeviceId, out var list) ? list : [];
    }

    /// <summary>
    /// 每台擁有者的觀看清單（一次算全部；清單是空的裝置不列）：看得到的對話查目錄（<paramref name="find"/>），只留擁有者存在、政策 full、沒刪除的，
    /// 每台依 id 排序、最多 <paramref name="max"/> 個
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ByOwner(IEnumerable<ViewerState> viewers, Func<string, ConversationEntry?> find, int max)
    {
        ArgumentNullException.ThrowIfNull(viewers);
        ArgumentNullException.ThrowIfNull(find);
        var byOwner = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var viewer in viewers)
        {
            foreach (var conversationId in SeenBy(viewer))
            {
                if (!seen.Add(conversationId))
                    continue;
                if (find(conversationId) is not { Deleted: false, OwnerDeviceId: { } owner } entry
                    || !string.Equals(entry.SyncPolicy, ConversationSyncPolicies.Full, StringComparison.Ordinal))
                    continue;
                if (!byOwner.TryGetValue(owner, out var ids))
                    byOwner[owner] = ids = new SortedSet<string>(StringComparer.Ordinal);
                ids.Add(conversationId);
            }
        }
        var limit = Math.Max(0, max);
        return byOwner.Where(p => limit > 0)
            .ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.Take(limit).ToList(), StringComparer.Ordinal);
    }
}
