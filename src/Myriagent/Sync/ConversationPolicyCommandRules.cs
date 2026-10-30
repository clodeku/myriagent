namespace Myriagent.Sync;

/// <summary>
/// 指令 <c>syncSetConversationPolicy</c>（docs/web-protocol.md 第二節；WP4.3 計畫 D35、D25）收不收：只收 <c>full</c>、<c>metadata</c>、
/// <c>localOnly</c>（<c>paused</c> 只由權杖 DLP 設定）；不同步的代理種類的對話只收 <c>localOnly</c>（2026-10-22 起四種都同步，目前沒有）；已從伺服器刪除的對話（旁檔
/// <c>sync-server-deleted.json</c>）只收 <c>localOnly</c>，不能再改回同步（擁有者 2026-10-19 的答 4）；草稿與複本一律不收。純函式
/// </summary>
internal static class ConversationPolicyCommandRules
{
    /// <summary>已從伺服器刪除的對話選了 full／metadata：中樞以 toast 告訴使用者（其他原因只記錄）</summary>
    public const string ServerDeletedReason = "這個對話已從伺服器刪除，不能再同步；要同步請開新對話";

    public const string DraftOrReplicaReason = "草稿與其他電腦的對話不設同步政策";

    public const string PausedReason = "paused 只由權杖 DLP 設定，不能以指令設定";

    public const string UnknownReason = "不認得的同步政策";

    public const string LocalOnlyProviderReason = "這種代理的對話只能是 localOnly";

    /// <summary>
    /// 合格回要存的值（就是 <paramref name="requested"/>），<paramref name="reason"/> 是 null；不合格回 null 與原因。
    /// 判斷順序：草稿或複本 → 值（paused、不認得的；大小寫不同也算不認得）→ 代理種類 → 伺服器刪除
    /// </summary>
    /// <param name="providerSyncable">代理種類會同步（<c>SyncProviderRules.TryWireName</c>；2026-10-22 起四種都是 true）</param>
    /// <param name="serverDeleted">這個對話有伺服器刪除的旁檔</param>
    public static string? Check(string? requested, bool providerSyncable, bool isDraft, bool isReplica, bool serverDeleted, out string? reason)
    {
        reason = null;
        if (isDraft || isReplica)
            reason = DraftOrReplicaReason;
        else if (requested == ConversationSyncPolicies.Paused)
            reason = PausedReason;
        else if (requested is not (ConversationSyncPolicies.Full or ConversationSyncPolicies.Metadata or ConversationSyncPolicies.LocalOnly))
            reason = UnknownReason;
        else if (!providerSyncable && requested != ConversationSyncPolicies.LocalOnly)
            reason = LocalOnlyProviderReason;
        else if (serverDeleted && requested != ConversationSyncPolicies.LocalOnly)
            reason = ServerDeletedReason;
        return reason == null ? requested : null;
    }

    /// <summary>
    /// 伺服器以 dlp 擋下 item 類事件時要不要改成 paused（D14，保留碼的防禦）：實際的政策（已含代理種類與伺服器刪除的旁檔）比 paused 寬
    /// （full、metadata）才改；已經是 paused 或 localOnly 的不動
    /// </summary>
    public static bool PausesForDlp(string? effectivePolicy)
        => ConversationSyncPolicies.Strictness(effectivePolicy) < ConversationSyncPolicies.Strictness(ConversationSyncPolicies.Paused);
}
