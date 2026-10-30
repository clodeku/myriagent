using System.Globalization;
using System.Text;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Media;

/// <summary><see cref="MediaRefIndexRules.ForEvent"/> 的結果：索引器對一筆對話事件要做的事</summary>
internal enum MediaIndexAction
{
    /// <summary>和媒體參照無關（turn、attention、queue；full 的 meta）</summary>
    Ignore,

    /// <summary><c>conv.item</c>：這一筆的參照與項目欄位 blob 的引用整份換成新的</summary>
    Upsert,

    /// <summary><c>conv.itemRemoved</c>：刪這一筆的參照、縮圖引用與項目欄位 blob 的引用</summary>
    RemoveItem,

    /// <summary><c>conv.deleted</c>、降成 metadata／paused 的 meta：刪整個對話的參照（含抽屜與 fileFetch 的）與引用（答 4、D8）</summary>
    PurgeConversation,
}

/// <summary><c>media_refs</c> 的一列（伺服器不存路徑）</summary>
/// <param name="DeviceId">發參照的裝置（對話的擁有者，也是事件的作者）</param>
/// <param name="Purpose"><see cref="MediaRefPurposes"/>：項目是 <c>item:{itemId}</c></param>
/// <param name="CreatedAt">伺服器收到那筆事件的時間（Unix 毫秒；E10 由新到舊排序用，項目的參照不會到期）</param>
internal sealed record MediaRefRow(string DeviceId, string Ref, string ConversationId, string Purpose, long CreatedAt);

/// <summary>E10 的分頁游標：上一頁最舊一列的 <c>(created_at, ref)</c></summary>
internal readonly record struct MissingThumbCursor(long CreatedAt, string Ref);

/// <summary>
/// 媒體參照索引的純函式規則（WP4.7 計畫 D6、D8）：伺服器從對話事件維護 <c>media_refs</c> 與項目欄位 blob 的 <c>blob_refs</c>。
/// 不呼叫 <c>ConversationWire.TryReadItem</c>（整筆換回 blob 要好幾倍的記憶體）：只看 payload 的 <c>attachments</c>、<c>media</c> 兩欄的 <c>ref</c>，
/// 這兩欄本身是 blob 參照時才由呼叫端讀那個 blob（≤4 MB，D14），讀不到或不合格的那欄略過。純函式
/// </summary>
internal static class MediaRefIndexRules
{
    /// <summary>項目欄位 blob 的 <c>blob_refs.ref_kind</c>（<see cref="BlobRefKinds.Item"/>）的 <c>ref_id</c> 分隔字元：<c>{conversationId}:{itemId}</c></summary>
    public const char ItemRefSeparator = ':';

    /// <summary>E10 一頁最多幾列</summary>
    public const int MaxMissingPage = 200;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>項目的用途 <c>item:{itemId}</c></summary>
    public static string ItemPurpose(string itemId) => MediaRefPurposes.ItemPrefix + itemId;

    /// <summary>項目欄位 blob 的引用 id <c>{conversationId}:{itemId}</c>（對話 id 是 32 個小寫十六進位、項目 id 沒有冒號）</summary>
    public static string ItemBlobRefId(string conversationId, string itemId) => conversationId + ItemRefSeparator + itemId;

    /// <summary>引用 id 裡的對話 id（<see cref="ItemBlobRefId"/> 的前 32 字）；格式不對回 null</summary>
    public static string? ConversationOfItemBlobRef(string? refId) =>
        refId is { Length: > ConversationIds.Length + 1 } && refId[ConversationIds.Length] == ItemRefSeparator
                                                          && ConversationIds.IsWireId(refId[..ConversationIds.Length])
            ? refId[..ConversationIds.Length]
            : null;

    /// <summary>
    /// 一筆對話事件對索引的影響：<c>conv.item</c> 換新、<c>conv.itemRemoved</c> 刪那一筆、<c>conv.deleted</c> 刪整個對話；
    /// <c>conv.created</c>／<c>conv.meta</c> 帶的政策（經 <see cref="ConversationSyncPolicies.Interpret"/>）不是 full 時刪整個對話，其他一律不做事
    /// </summary>
    /// <param name="syncPolicy">meta 類事件帶的 <c>syncPolicy</c> 原字串（其他種類傳 null）</param>
    public static MediaIndexAction ForEvent(string? kind, string? syncPolicy) => kind switch
    {
        ConversationEventKinds.Item => MediaIndexAction.Upsert,
        ConversationEventKinds.ItemRemoved => MediaIndexAction.RemoveItem,
        ConversationEventKinds.Deleted => MediaIndexAction.PurgeConversation,
        ConversationEventKinds.Created or ConversationEventKinds.Meta =>
            ConversationSyncPolicies.Interpret(syncPolicy) == ConversationSyncPolicies.Full ? MediaIndexAction.Ignore : MediaIndexAction.PurgeConversation,
        _ => MediaIndexAction.Ignore,
    };

    /// <summary>
    /// 項目 payload 裡的媒體參照（附件與代理產出媒體的 <c>ref</c>，同一個參照只列一次）。作者不是裝置 id、項目 id 不合格回空的；
    /// 某一欄是 blob 參照時以 <paramref name="readBlob"/> 取內容（回 null＝讀不到，那欄略過），內容要是嚴格 UTF-8 的 JSON 陣列
    /// </summary>
    /// <param name="deviceId">事件的作者（擁有者裝置）</param>
    /// <param name="createdAt">伺服器收到那筆事件的時間</param>
    public static IReadOnlyList<MediaRefRow> FromItemPayload(string deviceId, string conversationId, JObject payload, long createdAt,
        Func<ConversationBlobRef, byte[]?> readBlob)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(readBlob);
        if (!SyncIds.IsDeviceId(deviceId) || !ConversationIds.IsWireId(conversationId) || ItemId(payload) is not { } itemId)
            return [];

        var purpose = ItemPurpose(itemId);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<MediaRefRow>();
        foreach (var field in new[] { "attachments", "media" })
        {
            if (Entries(payload[field], readBlob) is not { } entries)
                continue;
            foreach (var entry in entries)
            {
                if (entry is JObject o && o["ref"] is JValue { Type: JTokenType.String, Value: string mediaRef }
                    && ConversationEventRules.IsMediaRef(mediaRef) && seen.Add(mediaRef))
                    rows.Add(new MediaRefRow(deviceId, mediaRef, conversationId, purpose, createdAt));
            }
        }

        return rows;
    }

    /// <summary>項目 payload 裡所有換成 blob 參照的欄位（<see cref="ConversationEventRules.BlobFields"/>）的 SHA-256（同一個只列一次、依欄位順序）</summary>
    public static IReadOnlyList<string> BlobShas(JObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var shas = new List<string>();
        foreach (var name in ConversationEventRules.BlobFields)
        {
            if (ConversationEventRules.TryReadBlobRef(payload[name], out var blobRef) && !shas.Contains(blobRef.Blob, StringComparer.Ordinal))
                shas.Add(blobRef.Blob);
        }

        return shas;
    }

    /// <summary>payload 的項目 id（<see cref="ConversationIds.IsItemId"/>）；沒有或不合格回 null</summary>
    public static string? ItemId(JObject payload) =>
        payload["id"] is JValue { Type: JTokenType.String, Value: string id } && ConversationIds.IsItemId(id) ? id : null;

    /// <summary>E10 的 <c>limit</c>：沒帶或不是數字是 <see cref="MaxMissingPage"/>，1～<see cref="MaxMissingPage"/>（超出的夾住）</summary>
    public static int MissingLimit(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) ? Math.Clamp(limit, 1, MaxMissingPage) : MaxMissingPage;

    /// <summary>E10 的游標字串 <c>{createdAt}_{ref}</c></summary>
    public static string FormatCursor(MissingThumbCursor cursor) =>
        cursor.CreatedAt.ToString(CultureInfo.InvariantCulture) + "_" + cursor.Ref;

    /// <summary>解析 E10 的 <c>before</c>：沒帶（null 或空字串）回 true 且 <paramref name="cursor"/> 是 null；格式不對回 false</summary>
    public static bool TryParseCursor(string? value, out MissingThumbCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(value))
            return true;
        var separator = value.IndexOf('_');
        if (separator <= 0 || separator > 19
            || !long.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var at)
            || !ConversationEventRules.IsMediaRef(value[(separator + 1)..]))
            return false;
        cursor = new MissingThumbCursor(at, value[(separator + 1)..]);
        return true;
    }

    /// <summary>某一欄的陣列：內嵌的陣列原樣、blob 參照讀內容解析（嚴格 UTF-8、只收一種解讀的 JSON 陣列、長度要和參照相符），其他回 null</summary>
    private static JArray? Entries(JToken? value, Func<ConversationBlobRef, byte[]?> readBlob)
    {
        if (value is JArray inline)
            return inline;
        if (!ConversationEventRules.TryReadBlobRef(value, out var blobRef) || readBlob(blobRef) is not { } bytes || bytes.LongLength != blobRef.Length)
            return null;
        try
        {
            return ConversationEventRules.TryParseStrict(Strict.GetString(bytes), out var token) ? token as JArray : null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
