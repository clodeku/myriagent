using Myriagent.Agents;
using Myriagent.Sync;

namespace MyriagentHub.Sync;

/// <summary>
/// 授權時讀到的對話列（寫入者的交易裡經 <c>SyncWriteContext</c> 查 <c>conversations</c> 表，不讀目錄快取：同一個交易可能先有
/// <c>conv.created</c>、後有 <c>conv.item</c>；WP4.2 計畫 D4）
/// </summary>
/// <param name="OwnerDeviceId">擁有者裝置（第一筆建立事件的作者）；表上可為 null，null 時誰都不是擁有者</param>
/// <param name="SyncPolicy">列上的 <c>sync_policy</c>（物化時已經 <see cref="ConversationSyncPolicies.Interpret"/> 過）</param>
/// <param name="Deleted"><c>deleted_at</c> 不是 null（墓碑）</param>
/// <param name="Recovering"><c>lease_state</c> 是 <c>recovering</c>（伺服器還原之後）</param>
internal sealed record ConversationRow(string ConversationId, string? OwnerDeviceId, long Epoch, string SyncPolicy, bool Deleted, bool Recovering);

/// <summary>接受之後要做的物化</summary>
internal enum ConversationAction
{
    /// <summary>對話不存在：建立列（擁有者＝作者，epoch 取事件上的值）並寫 meta</summary>
    Create,

    /// <summary>對話已存在的 <c>conv.created</c>／<c>conv.meta</c>：整份 meta 取代（不改擁有者、來源裝置與建立時間）</summary>
    ReplaceMeta,

    Turn,

    Attention,

    /// <summary>在伺服器上刪除（墓碑；不能再改回同步，答 4）</summary>
    Delete,

    UpsertItem,

    RemoveItem,

    Queue,
}

/// <summary>
/// 一筆對話事件的授權結果：<see cref="Rejected"/> 不是 null 就是被擋（<see cref="Action"/> 是 null）。
/// 被擋時 <see cref="Epoch"/>／<see cref="Owner"/> 是伺服器目前的值，只有 <c>fenced</c> 帶 <see cref="Epoch"/>、<c>not_owner</c> 帶 <see cref="Owner"/>
/// （照抄進 <c>BatchEventResult</c>）；<see cref="ConversationAction.Create"/> 時兩者是新列的值（作者、事件上的 epoch），不進 <c>BatchEventResult</c>。
/// </summary>
/// <param name="PurgeContent">同一個交易要清掉物化的內容（items、runtime；刪除時另有未讀列）：full 降成 metadata／paused，或刪除（答 4）</param>
internal readonly record struct ConversationDecision(ConversationAction? Action, string? Rejected, long? Epoch = null, string? Owner = null,
    bool PurgeContent = false);

/// <summary>
/// 對話事件上行的純函式規則（WP4.2 計畫步 2；D4、D5、D7、D9、D10、D13、D16、D28、D34）：與資料庫無關，物化（步 3）照它做。
/// </summary>
internal static class ConversationApplyRules
{
    /// <summary>
    /// 授權與動作。判斷順序：
    /// <list type="number">
    /// <item>沒有列：<c>conv.created</c>、<c>conv.meta</c> 都是 <see cref="ConversationAction.Create"/>（政策經 Interpret 是 localOnly 時 invalid）；
    /// 其他六種（turn、attention、deleted 與 item 類）一律 invalid、不建立列（P2）。</item>
    /// <item>有列：事件的 epoch 比列上的舊 → <c>fenced</c>（帶列上的 epoch；第 5 階段 D4：在擁有者檢查之前，交接後原擁有者的遲到事件才會被當成圍欄、
    /// 不是 not_owner）→ 作者不是擁有者 → <c>not_owner</c>（帶 <see cref="ConversationRow.OwnerDeviceId"/>）→ epoch 不同（比列上的新）→ <c>fenced</c>
    /// → 已刪除 → <c>deleted</c> → item 類而且列上的政策不發布它 → invalid → meta 類帶的政策是 localOnly（含不認得的值）→ invalid → 動作。
    /// 第 4 階段 epoch 一律是 1，所以第一步從來不成立、結果和以前相同。</item>
    /// <item>伺服器寫的種類（<see cref="ConversationEventKinds.IsServerAuthored"/>：<c>conv.lease</c>）一律 invalid：裝置不能送。</item>
    /// </list>
    /// 不看項目存不存在：<c>conv.itemRemoved</c> 指向不存在的項目照樣是 <see cref="ConversationAction.RemoveItem"/>（物化時什麼都不做，提案 C5）。
    /// </summary>
    /// <param name="incomingPolicy">meta 類事件帶的政策（Interpret 之前的原字串；只有 conv.created／conv.meta 有）；其他種類傳 null</param>
    public static ConversationDecision Decide(ConversationRow? row, string author, string kind, long epoch, string? incomingPolicy)
    {
        var cls = ConversationEventKinds.ClassOf(kind);
        if (cls is null || ConversationEventKinds.IsServerAuthored(kind))
            return Reject(SyncErrorCodes.Invalid);
        var carriesPolicy = kind is ConversationEventKinds.Created or ConversationEventKinds.Meta;
        var newPolicy = ConversationSyncPolicies.Interpret(incomingPolicy);
        // 只有 conv.created／conv.meta 帶政策（其他種類多傳的值不算數）；localOnly 的對話不應該有任何事件，改成不同步一律走 conv.deleted（D7）
        var localOnly = carriesPolicy && newPolicy == ConversationSyncPolicies.LocalOnly;

        if (row is null)
        {
            if (!carriesPolicy || localOnly)
                return Reject(SyncErrorCodes.Invalid);
            return new ConversationDecision(ConversationAction.Create, null, Epoch: epoch, Owner: author);
        }

        if (epoch < row.Epoch)
            return new ConversationDecision(null, ConversationRejectCodes.Fenced, Epoch: row.Epoch);
        if (!string.Equals(row.OwnerDeviceId, author, StringComparison.Ordinal))
            return new ConversationDecision(null, ConversationRejectCodes.NotOwner, Owner: row.OwnerDeviceId);
        if (row.Epoch != epoch)
            return new ConversationDecision(null, ConversationRejectCodes.Fenced, Epoch: row.Epoch);
        if (row.Deleted)
            return Reject(ConversationRejectCodes.Deleted);
        var currentPolicy = ConversationSyncPolicies.Interpret(row.SyncPolicy);
        if (cls == SyncClasses.Item && !ConversationSyncPolicies.Publishes(currentPolicy, kind))
            return Reject(SyncErrorCodes.Invalid);
        if (localOnly)
            return Reject(SyncErrorCodes.Invalid);

        return kind switch
        {
            ConversationEventKinds.Created or ConversationEventKinds.Meta => new ConversationDecision(ConversationAction.ReplaceMeta, null,
                PurgeContent: currentPolicy == ConversationSyncPolicies.Full
                              && newPolicy is ConversationSyncPolicies.Metadata or ConversationSyncPolicies.Paused),
            ConversationEventKinds.Turn => new ConversationDecision(ConversationAction.Turn, null),
            ConversationEventKinds.Attention => new ConversationDecision(ConversationAction.Attention, null),
            ConversationEventKinds.Deleted => new ConversationDecision(ConversationAction.Delete, null, PurgeContent: true),
            ConversationEventKinds.Item => new ConversationDecision(ConversationAction.UpsertItem, null),
            ConversationEventKinds.ItemRemoved => new ConversationDecision(ConversationAction.RemoveItem, null),
            ConversationEventKinds.Queue => new ConversationDecision(ConversationAction.Queue, null),
            _ => Reject(SyncErrorCodes.Invalid),
        };
    }

    /// <summary>
    /// 這筆是新的上行事件（D13：變更通知的 <c>Fresh</c>，給未讀用）：不在對帳模式（<paramref name="resyncFrom"/> 是 null），
    /// 或 deviceSeq ≥ Hello 時 outbox 的 NextDeviceSeq（Hello 之後才寫進 outbox）。對帳模式下 Hello 沒帶 outbox 位置時一律不是新的
    /// </summary>
    public static bool IsFresh(long? resyncFrom, long? helloNextDeviceSeq, long deviceSeq) =>
        resyncFrom is null || (helloNextDeviceSeq is { } next && deviceSeq >= next);

    /// <summary>
    /// 擁有者被接受的 <c>conv.created</c> 解除 recovering 嗎（D16）：判斷同 <see cref="IsFresh"/>——對帳模式下重新送達、還原前就在 outbox 的舊 created
    /// 照常套用 meta 但不解除。只對擁有者的 conv.created 呼叫
    /// </summary>
    public static bool ClearsRecovering(long? resyncFrom, long? helloNextDeviceSeq, long deviceSeq) => IsFresh(resyncFrom, helloNextDeviceSeq, deviceSeq);

    /// <summary>顯示用的時間（D10）：取裝置的時間，但不晚於伺服器收到的時間；沒有裝置時間用收到的時間。只影響畫面順序，不影響事件排序</summary>
    public static long DisplayTime(long? deviceMs, long receivedMs) => Math.Min(deviceMs ?? receivedMs, receivedMs);

    /// <summary>項目的狀態已經不會再變（D9 的 <c>final</c>）：不是 Running、也不是 Waiting</summary>
    public static bool IsFinal(string status) =>
        status is not (nameof(AgentItemStatus.Running) or nameof(AgentItemStatus.Waiting));

    /// <summary><c>conv.attention</c> 的 kind 對應到瀏覽器 <c>notify</c> 的 kind（D28）：plan → approval、其他認得的照名稱、不認得的 null（不送）</summary>
    public static string? NotifyKind(string attentionKind) => attentionKind switch
    {
        ConversationAttentionKinds.Plan => ConversationAttentionKinds.Approval,
        ConversationAttentionKinds.Question or ConversationAttentionKinds.Approval
            or ConversationAttentionKinds.Completed or ConversationAttentionKinds.Failed => attentionKind,
        _ => null,
    };

    private static ConversationDecision Reject(string reason) => new(null, reason);
}
