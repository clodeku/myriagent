using System.Globalization;
using Myriagent.Sync;
using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 向擁有者要整份內容（<c>SnapshotRequest</c>）的純函式規則（WP4.2 計畫步 12；R16，D26、P3；類別名稱避開 Protocol 的 <see cref="SnapshotRequest"/>，F28）：
/// <list type="bullet">
/// <item><b>open</b>：瀏覽器 open 一個政策 full、有 meta（目錄裡有、沒刪除）、但伺服器上還沒有任何項目的對話（回補還沒輪到），擁有者在線而且那條連線協商出
/// <c>conv1</c>，而且這個對話在冷卻時間（<c>SnapshotOpenCooldownMinutes</c>）內沒送過。</item>
/// <item><b>restore（後備）</b>：擁有者 Prove 之後過了寬限（<c>SnapshotRestoreGraceSeconds</c>）仍是 recovering 的對話才送；每台每秒最多
/// <c>SnapshotRestorePerSecond</c> 則（兩則之間至少 1000/N 毫秒，<see cref="LiveRules.Allows"/>），同一個（裝置、還原世代、對話）在這個服務程序裡只送一次。
/// 主路是桌面自己整份重新發布（6.2 的「快照與還原」）。</item>
/// </list>
/// 時間一律是單調時鐘的毫秒
/// </summary>
internal static class SnapshotRequestRules
{
    /// <summary>open 要不要送（<paramref name="lastRequestedMs"/> 是這個對話上一次送 open 的時間，沒送過是 null）</summary>
    public static bool ShouldRequestOpen(ConversationEntry? entry, bool ownerOnline, bool allows, long? lastRequestedMs, long nowMs, long cooldownMs)
    {
        if (entry is not { Deleted: false, ItemCount: 0, OwnerDeviceId: not null }
            || !string.Equals(entry.SyncPolicy, ConversationSyncPolicies.Full, StringComparison.Ordinal))
            return false;
        if (!ownerOnline || !allows)
            return false;
        return lastRequestedMs is not { } last || nowMs - last >= cooldownMs;
    }

    /// <summary>Prove 之後過了寬限（含剛好等於）</summary>
    public static bool RestoreDue(long provenAtMs, long nowMs, long graceMs) => nowMs - provenAtMs >= graceMs;

    /// <summary>這台這一則 restore 可以送了嗎（上一則之後至少 1000/<paramref name="perSecond"/> 毫秒；≤0 一律不送）</summary>
    public static bool RestorePaced(long? lastSentMs, long nowMs, int perSecond) => LiveRules.Allows(lastSentMs, nowMs, perSecond);

    /// <summary>restore 的去重鍵（裝置、還原世代、對話）</summary>
    public static string RestoreKey(string deviceId, long generation, string conversationId) =>
        deviceId + "|" + generation.ToString(CultureInfo.InvariantCulture) + "|" + conversationId;

    /// <summary>restore 的下一個候選：這台擁有、沒刪除、仍是 recovering、這個世代還沒送過的（依 id 排序的第一個）；沒有就是 null</summary>
    public static ConversationEntry? NextRestore(IEnumerable<ConversationEntry> owned, string deviceId, long generation, IReadOnlySet<string> sent)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(sent);
        return owned.Where(e => !e.Deleted && e.Recovering && string.Equals(e.OwnerDeviceId, deviceId, StringComparison.Ordinal)
                && !sent.Contains(RestoreKey(deviceId, generation, e.ConversationId)))
            .OrderBy(e => e.ConversationId, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
