namespace Myriagent.Sync;

/// <summary>發布計畫裡的一個操作（對到一種對話事件；一輪的開始與結束都是 <c>conv.turn</c>）</summary>
internal enum PublishOpKind
{
    Created,
    Meta,
    TurnStarted,
    Item,
    ItemRemoved,
    TurnCompleted,
    Queue,
    Attention,
    Deleted,
}

/// <param name="ItemId">項目類操作（<see cref="PublishOpKind.Item"/>、<see cref="PublishOpKind.ItemRemoved"/>）的項目 id；其他為 null</param>
internal sealed record PublishOp(PublishOpKind Kind, string? ItemId = null);

/// <summary>
/// 引擎對一個對話的發布狀態（sync.db 的 <c>publish_meta</c> 與旁檔）
/// </summary>
/// <param name="CreatedWritten">已知：這個對話的 <c>conv.created</c> 曾寫進 outbox（不是「伺服器已確認」）</param>
/// <param name="Blocked">封鎖原因（拒絕碼的處置，D14）；沒有封鎖為 null</param>
/// <param name="Deleted">墓碑：已寫過 <c>conv.deleted</c></param>
/// <param name="ServerDeleted">對話資料夾有旁檔 <c>sync-server-deleted.json</c>（已在伺服器上刪除、不能再改回同步，D25）</param>
/// <param name="EverCreated">
/// 曾經對任何一個伺服器實例寫過 <c>conv.created</c>（伺服器換實例時不重設）：換實例或從備份還原之後、重新發布之前，伺服器上可能還有它，
/// 刪除與改成 localOnly 照樣要送 <c>conv.deleted</c>（合併前審查 F3）
/// </param>
internal sealed record PublishKnowledge(bool CreatedWritten, string? Blocked, bool Deleted, bool ServerDeleted, bool EverCreated = false);

/// <summary>已發布的對話政策改變時要做的事（D25）</summary>
internal enum PolicyTransition
{
    /// <summary>不用特別處理（沒發布過、已有墓碑、政策沒變）</summary>
    None,

    /// <summary>改成只發 meta 類（metadata、paused）：送帶新政策的 <c>conv.meta</c>、清項目摘要（保留 Ord），伺服器清掉內容</summary>
    MetaOnly,

    /// <summary>改成 localOnly（含換到不進同步的代理種類、有伺服器刪除的旁檔）：先寫旁檔、再送 <c>conv.deleted</c>、記墓碑並封鎖</summary>
    Tombstone,

    /// <summary>從只發 meta 類改回 full：送 <c>conv.meta(full)</c>，接著整份重整</summary>
    Republish,
}

/// <summary>
/// 一個對話這一輪要寫哪些事件（WP4.3 計畫 D6、D21、D25、D38；docs/sync-protocol.md 14.2）。純函式；政策一律是已經套用代理種類限制的
/// 有效政策（<see cref="ConversationSyncPolicies.Effective"/>），有伺服器刪除的旁檔時這裡再當成 localOnly（fail-closed）
/// </summary>
internal static class PublishPlanRules
{
    /// <summary>操作對到的對話事件種類（<see cref="ConversationEventKinds"/>）</summary>
    public static string KindOf(PublishOpKind op) => op switch
    {
        PublishOpKind.Created => ConversationEventKinds.Created,
        PublishOpKind.Meta => ConversationEventKinds.Meta,
        PublishOpKind.TurnStarted => ConversationEventKinds.Turn,
        PublishOpKind.TurnCompleted => ConversationEventKinds.Turn,
        PublishOpKind.Item => ConversationEventKinds.Item,
        PublishOpKind.ItemRemoved => ConversationEventKinds.ItemRemoved,
        PublishOpKind.Queue => ConversationEventKinds.Queue,
        PublishOpKind.Attention => ConversationEventKinds.Attention,
        PublishOpKind.Deleted => ConversationEventKinds.Deleted,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "不認得的發布操作"),
    };

    /// <summary>
    /// 依政策與發布狀態整理這個對話的操作（輸入是掛鉤的順序）：
    /// <list type="bullet">
    /// <item>封鎖或墓碑：全部丟掉。</item>
    /// <item>未知（<c>conv.created</c> 沒寫過）的對話被刪除：全部丟掉（伺服器上本來就沒有它，不必先建立再刪除）；但曾經建立過
    /// （<see cref="PublishKnowledge.EverCreated"/>，換實例或還原之後還沒重新發布）或有伺服器刪除旁檔的，只送 <c>Deleted</c>（不先建立）。</item>
    /// <item>未知：最前面補一個 <c>Created</c>（只補一次；輸入裡的 <c>Created</c> 併進這一個）。已知的對話輸入裡的 <c>Created</c> 照原位保留（重送是冪等的）。</item>
    /// <item>其餘依政策以 <see cref="ConversationSyncPolicies.Publishes"/> 過濾；但已知（或有伺服器刪除旁檔）的對話的 <c>Deleted</c> 一律保留
    /// （政策已是 localOnly 時，這正是伺服器上的刪除）。</item>
    /// <item><c>Deleted</c> 之後的一律丟掉；其餘保持輸入的順序。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<PublishOp> Plan(string effectivePolicy, PublishKnowledge knowledge, IReadOnlyList<PublishOp> ops)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(ops);
        if (knowledge.Blocked is not null || knowledge.Deleted || ops.Count == 0)
            return [];

        var policy = PolicyOf(effectivePolicy, knowledge);
        var known = knowledge.CreatedWritten;
        // 回傳的是輸入裡的那個操作（呼叫端以參考對回變化）
        if (!known && ops.FirstOrDefault(op => op.Kind == PublishOpKind.Deleted) is { } deleted)
            return knowledge.ServerDeleted || knowledge.EverCreated ? [deleted] : [];

        var result = new List<PublishOp>(ops.Count + 1);
        if (!known && ConversationSyncPolicies.Publishes(policy, ConversationEventKinds.Created))
            result.Add(new PublishOp(PublishOpKind.Created));
        foreach (var op in ops)
        {
            if (op.Kind == PublishOpKind.Deleted)
            {
                result.Add(op);
                break;
            }

            if (op.Kind == PublishOpKind.Created && !known)
                continue;
            if (ConversationSyncPolicies.Publishes(policy, KindOf(op.Kind)))
                result.Add(op);
        }

        return result;
    }

    /// <summary>
    /// 已發布的對話政策改變時要做的事（D25）：
    /// <list type="bullet">
    /// <item>從沒建立過（未知、也沒有 <see cref="PublishKnowledge.EverCreated"/>）或已有墓碑：None。</item>
    /// <item>有伺服器刪除的旁檔但還沒有墓碑（寫旁檔與寫 outbox 之間當機）、或新的有效政策是 localOnly：Tombstone（換實例或還原之後還沒重新發布的也是，合併前審查 F3）。</item>
    /// <item>換實例或還原之後還沒重新發布（曾經建立過、這個實例沒寫過 created）：發布時的政策不明，outbox 可能還有換實例之前寫的待送內容——
    /// 新政策是 metadata／paused 一律 MetaOnly（清掉待送的內容），full 是 None（等重新發布；第二輪合併前審查 PRIV-3）。</item>
    /// <item>新政策是 metadata／paused、和發布時不同：MetaOnly。</item>
    /// <item>新政策是 full、發布時不是：Republish。</item>
    /// <item>相同：None。</item>
    /// </list>
    /// </summary>
    /// <param name="publishedPolicy">發布時的政策（<c>publish_meta</c>）；沒記錄時當成 full（同 <see cref="ConversationSyncPolicies.Interpret"/>）</param>
    /// <param name="newPolicy">現在的有效政策</param>
    public static PolicyTransition Transition(string? publishedPolicy, string newPolicy, PublishKnowledge knowledge)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        if (knowledge.Deleted)
            return PolicyTransition.None;

        var target = PolicyOf(newPolicy, knowledge);
        if (!knowledge.CreatedWritten && !knowledge.EverCreated)
            return PolicyTransition.None;
        if (target == ConversationSyncPolicies.LocalOnly)
            return PolicyTransition.Tombstone;
        if (!knowledge.CreatedWritten)
            return target == ConversationSyncPolicies.Full ? PolicyTransition.None : PolicyTransition.MetaOnly;

        var published = ConversationSyncPolicies.Interpret(publishedPolicy);
        if (published == target)
            return PolicyTransition.None;
        return target == ConversationSyncPolicies.Full ? PolicyTransition.Republish : PolicyTransition.MetaOnly;
    }

    /// <summary>
    /// 可以送即時更新、可以讓遠端的觀看壓下本機提醒、可以回應快照請求（D19、D21、D23）：政策 full、已知、沒有封鎖與墓碑、
    /// 沒有伺服器刪除的旁檔、不是草稿或複本。伺服器的觀看清單只是第一道，這裡一律以本機政策再判斷
    /// </summary>
    public static bool LiveAllowed(string effectivePolicy, PublishKnowledge knowledge, bool isDraft, bool isReplica)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        return !isDraft && !isReplica
            && knowledge.CreatedWritten && knowledge.Blocked is null && !knowledge.Deleted
            && PolicyOf(effectivePolicy, knowledge) == ConversationSyncPolicies.Full;
    }

    /// <summary>
    /// Hello 要不要宣告 <c>conv1</c>、<c>live1</c>（D3、D38）：有對話的變更盒子、sync.db v3 的對話表在，而且權杖 DLP 的版本不低於必要版本。
    /// 伺服器沒有功能開關（擁有者 2026-10-19 答 1），所以宣告 <c>conv1</c> 的組建一定要帶著那一版的 DLP
    /// </summary>
    public static bool DeclaresConv1(bool hasBox, bool tablesReady, int dlpVersion, int requiredDlpVersion)
        => hasBox && tablesReady && dlpVersion >= requiredDlpVersion;

    /// <summary>有伺服器刪除旁檔的對話一律是 localOnly；其餘照存的值解讀（不認得的值是 localOnly）</summary>
    private static string PolicyOf(string? effectivePolicy, PublishKnowledge knowledge)
        => knowledge.ServerDeleted ? ConversationSyncPolicies.LocalOnly : ConversationSyncPolicies.Interpret(effectivePolicy);
}
