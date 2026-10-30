using System.Globalization;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>重建時讀到的一筆 meta 類事件（<c>events</c> 存下的版本：命中過 DLP 的是遮蔽版；<paramref name="ReceivedAt"/> 是 <c>events.created_at</c>）</summary>
internal sealed record RebuildMetaEvent(long Seq, string Author, string Kind, long Epoch, long ReceivedAt, string Json, JObject Payload);

/// <summary>
/// 重建一個對話時，政策與清除點的進度（D14）：<paramref name="Policy"/> 是到目前為止最後的政策（解讀過）、<paramref name="Deleted"/>＝已刪除、
/// <paramref name="PurgeSeq"/>＝最後一次清除內容（full 降成 metadata／paused，或刪除）的 seq；沒有清除過是 0
/// </summary>
internal readonly record struct RebuildCursor(string Policy, bool Deleted, long PurgeSeq)
{
    /// <summary>還沒有任何 meta 時（第一筆 created／meta 之前）的起點</summary>
    public static RebuildCursor Initial => new(ConversationSyncPolicies.Full, false, 0);
}

/// <summary>
/// 從 meta 類事件摺疊出的 <c>conversations</c> 列（D14 的第一塊）：欄位的語意照增量物化（<see cref="ConversationMaterializer"/>，D5、D10、D16）。
/// <c>logical_account_id</c> 由呼叫端以 (<see cref="OwnerDeviceId"/>, <see cref="LocalAccountId"/>) 查目前的 <c>account_links</c>（D11）；
/// <c>lease_state</c> 不從事件來（recovering 由還原標記、由當時的上行狀態解除），重建保留列上原本的值
/// </summary>
internal sealed record RebuildMeta(
    string OwnerDeviceId,
    long Epoch,
    string Provider,
    string? LocalAccountId,
    string MetaJson,
    string Title,
    long CreatedAt,
    long UpdatedAt,
    long LastSeq,
    bool Running,
    bool Waiting,
    long? TurnStartedAt,
    string? LastTurn,
    long? DeletedAt,
    string? DeletedBy,
    RebuildCursor Cursor);

/// <summary>重播一筆 item 類事件的方式</summary>
internal enum RebuildReplay
{
    /// <summary>這筆在最後一次清除之前（或對話已刪除）：內容不放回，只讓 <c>conv.item</c> 的 ord 更新 <c>max_ord</c>（Ord 只增不減）</summary>
    OrdOnly,

    /// <summary>照增量物化套用（upsert、移除、佇列）</summary>
    Full,
}

/// <summary>
/// 物化表重建的純函式規則（WP4.2 計畫步 5；D14）：版本比對、meta 類事件的摺疊、item 類事件的重播方式與分塊。
/// 重播是可信任模式：<c>events</c> 裡的都是當時被接受的事件，只摺疊、不重跑授權、驗證與 DLP。
/// </summary>
internal static class ConversationRebuildRules
{
    /// <summary>
    /// 要不要重建：存的版本和程式的 <paramref name="current"/> 不相等就重建，不論哪邊比較大（從較新或較舊的 WP4.2 組建回滾或升級都一樣）；
    /// 沒有任何對話的資料時不用重建（呼叫端只寫版本）。存的值不是數字也算不相等
    /// </summary>
    public static bool NeedsRebuild(string? stored, int current, bool hasConversations) =>
        hasConversations && !string.Equals(stored, current.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>
    /// 一筆 meta 類事件之後的政策與清除點（和 <see cref="ConversationApplyRules.Decide"/> 的 <c>PurgeContent</c> 同一個判斷）：
    /// created／meta 從 full 改成 metadata／paused 時清除點移到這一筆；conv.deleted 標成已刪除、清除點移到這一筆；其他種類不變
    /// </summary>
    public static RebuildCursor Track(RebuildCursor cursor, long seq, string kind, string? incomingPolicy)
    {
        switch (kind)
        {
            case ConversationEventKinds.Created:
            case ConversationEventKinds.Meta:
            {
                var policy = ConversationSyncPolicies.Interpret(incomingPolicy);
                var purge = cursor.Policy == ConversationSyncPolicies.Full && policy is ConversationSyncPolicies.Metadata or ConversationSyncPolicies.Paused;
                return cursor with { Policy = policy, PurgeSeq = purge ? seq : cursor.PurgeSeq };
            }
            case ConversationEventKinds.Deleted:
                return cursor with { Deleted = true, PurgeSeq = seq };
            default:
                return cursor;
        }
    }

    /// <summary>
    /// 以一個對話的 meta 類事件（依 seq）摺疊出 <c>conversations</c> 列；第一筆 created／meta 之前的其他種類略過（可信任模式下不會有）。
    /// 沒有任何 created／meta 時回 null（這個對話不該有列）
    /// </summary>
    public static RebuildMeta? FoldMeta(IEnumerable<RebuildMetaEvent> metaEvents)
    {
        ArgumentNullException.ThrowIfNull(metaEvents);
        RebuildMeta? row = null;
        foreach (var e in metaEvents.OrderBy(e => e.Seq))
        {
            var p = e.Payload;
            switch (e.Kind)
            {
                case ConversationEventKinds.Created:
                case ConversationEventKinds.Meta:
                {
                    var cursor = Track(row?.Cursor ?? RebuildCursor.Initial, e.Seq, e.Kind, (string?)p["syncPolicy"]);
                    var updatedAt = ConversationApplyRules.DisplayTime((long?)p["updatedAt"], e.ReceivedAt);
                    row = row is null
                        ? new RebuildMeta(e.Author, e.Epoch, (string)p["provider"]!, (string?)p["localAccountId"], e.Json, (string)p["title"]!,
                            ConversationApplyRules.DisplayTime((long?)p["createdAt"], e.ReceivedAt), updatedAt, e.Seq, (bool)p["running"]!, (bool)p["waiting"]!,
                            null, null, null, null, cursor)
                        : row with
                        {
                            Provider = (string)p["provider"]!,
                            LocalAccountId = (string?)p["localAccountId"],
                            MetaJson = e.Json,
                            Title = (string)p["title"]!,
                            UpdatedAt = updatedAt,
                            LastSeq = e.Seq,
                            Running = (bool)p["running"]!,
                            Waiting = (bool)p["waiting"]!,
                            Cursor = cursor,
                        };
                    break;
                }
                case ConversationEventKinds.Turn when row is not null:
                    row = (string?)p["phase"] == ConversationTurnPhases.Started
                        ? row with { Running = true, TurnStartedAt = e.ReceivedAt, LastSeq = e.Seq }
                        : row with { Running = false, LastTurn = e.Json, LastSeq = e.Seq };
                    break;
                case ConversationEventKinds.Attention when row is not null:
                    row = row with { LastSeq = e.Seq };
                    break;
                case ConversationEventKinds.Deleted when row is not null:
                    row = row with
                    {
                        DeletedAt = e.ReceivedAt,
                        DeletedBy = e.Author,
                        LastSeq = e.Seq,
                        Cursor = Track(row.Cursor, e.Seq, e.Kind, null),
                    };
                    break;
            }
        }
        return row;
    }

    /// <summary>一筆 item 類事件重播時怎麼套用：對話已刪除、或這筆在最後一次清除之前（seq ≤ 清除點）只更新 max_ord，其他照增量物化</summary>
    public static RebuildReplay ReplayOf(long seq, RebuildCursor cursor) =>
        cursor.Deleted || seq <= cursor.PurgeSeq ? RebuildReplay.OrdOnly : RebuildReplay.Full;

    /// <summary>
    /// 這一塊是不是最後一塊：每塊在寫入執行緒上以「seq 大於已重播到的 seq、依 seq、最多 <paramref name="chunk"/> 筆」讀 item 類事件，
    /// 取到的比上限少表示已經追上（同一個寫入工作裡不會有新事件插進來），在同一個工作解除「重建中」
    /// </summary>
    public static bool IsLastChunk(int returned, int chunk)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunk);
        ArgumentOutOfRangeException.ThrowIfNegative(returned);
        return returned < chunk;
    }
}
