using Myriagent.Remote;

namespace Myriagent.Hub;

/// <summary>
/// 副本（其他電腦的對話在這台的副本；WP4.3 載入）在本機是唯讀的（WP4.4 計畫 D29，P43 的交接）。攔不攔由 WP4.3 的白名單決定
/// （<c>AgentHub.ReplicaCommands</c>：只收看的指令，其他一律不處理，fail-closed）；這裡是被擋下時要給那個檢視一則「唯讀」提示的指令——
/// 使用者的寫入：型錄裡路由 Owner 的全部（遠端會改的就是本機會改的），加上只在本機的寫入、會啟動 CLI 的、改同步政策的，以及開路徑的
/// （副本的路徑是另一台的，可能是 UNC）。自動送的與讀取（open、close、visible、loadOlder、copyItem、layout、popout）不提示。
/// 這份清單與白名單沒有交集（測試核對）。遠端的回應檢視不提示：副本已由閘門 5c 拒絕
/// </summary>
internal static class ReplicaCommandRules
{
    /// <summary>副本上被擋下時要提示「唯讀」的指令（大小寫要相同，同 HandleCommand 的 switch）</summary>
    public static IReadOnlySet<string> Writes { get; } = BuildWrites();

    /// <summary>這個指令在副本上被擋下時要提示</summary>
    public static bool Blocks(string? cmd) => cmd != null && Writes.Contains(cmd);

    private static IReadOnlySet<string> BuildWrites()
    {
        // 區域變數，不用靜態欄位：靜態初始化的順序不會讓清單是 null
        string[] localWrites =
        [
            "chooseFolder", "attach", "addFiles", "addPathAttachment", "pasteImage",
            "cliContinue", "cliApplyNow", "syncSetConversationPolicy",
            "openFolder", "openPath", "revealPath", "saveFile", "copyMedia", "docPreview",
        ];
        var writes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in RemoteCommandCatalog.All)
        {
            if (spec.Route == RemoteRoute.Owner)
                writes.Add(spec.Name);
        }

        writes.UnionWith(localWrites);
        return writes;
    }
}
