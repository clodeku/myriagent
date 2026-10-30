using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 伺服器草稿換成正式對話的純函式規則（WP4.6 計畫 D17、D41；web-protocol 的 <c>conversationClosed.replacedBy</c>）。
/// 第一次 send 時 WP4.5 在草稿記下新對話的線上 id（<see cref="BrowserDraft.PendingConversationId"/>）；那個對話出現在伺服器
/// （<c>conv.created</c>／<c>conv.meta</c> 提交）時，閘道對那個工作階段的每條連線送 <c>{type:"conversationClosed", conversationId: 草稿 id, replacedBy: 新 id}</c>，
/// 網頁在草稿的位置換成新對話、重新 open（舊網頁不認得 <c>replacedBy</c>，只會關掉草稿）
/// </summary>
internal static class DraftSwapRules
{
    /// <summary><c>conversationClosed</c> 的選用欄位：草稿換成的正式對話 id</summary>
    public const string ReplacedByKey = "replacedBy";

    /// <summary>這一輪變更裡新出現或改了中繼資料的對話 id（<see cref="ConversationChangeKind.Meta"/>、對話 id 形狀、去重、照出現順序）</summary>
    public static IReadOnlyList<string> CreatedIds(IReadOnlyList<ConversationChange> drained)
    {
        ArgumentNullException.ThrowIfNull(drained);
        List<string>? ids = null;
        HashSet<string>? seen = null;
        foreach (var change in drained)
        {
            if (change.Kind != ConversationChangeKind.Meta || !ConversationIds.IsWireId(change.ConversationId))
                continue;
            if ((seen ??= new HashSet<string>(StringComparer.Ordinal)).Add(change.ConversationId))
                (ids ??= []).Add(change.ConversationId);
        }
        return ids ?? [];
    }

    /// <summary>
    /// <c>{type:"conversationClosed", conversationId: draftId, replacedBy: conversationId}</c>：合併鍵是 null（同 <see cref="ConversationGatewayRules.ClosedMessage"/>），
    /// 訊息屬於草稿（<see cref="ClientOutboxMessage.ConversationId"/> 是草稿 id）
    /// </summary>
    public static ClientOutboxMessage ClosedReplaced(string draftId, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(draftId);
        ArgumentNullException.ThrowIfNull(conversationId);
        var json = new JObject
        {
            ["type"] = ConversationGatewayRules.ConversationClosedType,
            ["conversationId"] = draftId,
            [ReplacedByKey] = conversationId,
        };
        return new ClientOutboxMessage(ConversationGatewayRules.ConversationClosedType, json.ToString(Formatting.None), null, draftId);
    }
}
