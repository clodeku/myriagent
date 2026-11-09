using System.Globalization;
using System.Text;
using Myriagent.Agents;
using Myriagent.Media;
using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Media;

/// <summary>
/// 遠端檔案指令的輸出在伺服器改寫（WP4.7 計畫 D37；WP4.5 把裝置的 CommandOutput 交給瀏覽器之前經 <see cref="ICommandOutputRewriter"/> 呼叫）：
/// 裝置只送媒體參照（<c>ref</c>），伺服器換成本站網址並登記 <c>media_refs</c>；裝置送來的 <c>url</c>、<c>thumbUrl</c> 一律丟掉（本機網址不離開電腦）。
/// <list type="bullet">
/// <item><c>files</c>（<c>listFiles</c>）：每個 entry 拿掉 <c>url</c>、<c>thumbUrl</c>、<c>ref</c>、<c>ver</c>；參照合格、<c>kind</c> 是 image／video、
/// 檔名是可顯示的媒體時登記 <c>list</c> 參照，<c>url</c>＝<c>/m/{key}</c>、<c>thumbUrl</c>＝<c>/t/{key}?s=160&amp;v={ver}</c>（<c>ver</c> 只收數字，不合格就不帶 <c>v</c>），
/// 其他設 null。<c>truncated</c> 等其他欄位原樣。</item>
/// <item><c>fileRef</c>（<c>fileFetch</c>）：登記 <c>file</c> 參照，<c>url</c>＝<c>/f/{key}</c>，<c>expiresAt</c> 取裝置給的值與現在加 15 分鐘的較小者；
/// 有 <c>error</c>（或參照不合格）時不登記、<c>url</c> 是 null。</item>
/// <item><c>fileSearch</c>：原樣（同一個物件）。</item>
/// <item>以上三種：對話的擁有者不是這台（對話不在也算）回 null（丟掉）；改寫後超過 <see cref="MaxMessageBytes"/> 回 null。
/// 指令沒有對話（例如新對話的資料夾挑選）時不檢查擁有者、也不登記參照（<c>media_refs</c> 一定屬於某個對話），媒體網址一律 null。</item>
/// <item>其他種類（<c>toast</c>、不認得的）：原樣回同一個物件。</item>
/// </list>
/// 不改呼叫端的物件（改寫的是複本）。純函式
/// </summary>
internal static class CommandOutputRewriteRules
{
    public const string FilesType = "files";

    public const string FileRefType = "fileRef";

    public const string FileSearchType = "fileSearch";

    /// <summary>改寫後（UTF-8、不縮排）的上限：瀏覽器那一頭的訊息上限</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>抽屜縮圖的邊長（<c>s</c>）</summary>
    public const int ListThumbSize = 160;

    /// <summary>回改寫後的物件與要登記的參照列；物件是 null＝丟掉（列一定是空的）</summary>
    /// <param name="commandName">原指令名稱（<c>listFiles</c>、<c>fileSearch</c>、<c>fileFetch</c>；只供記錄）</param>
    /// <param name="owner"><paramref name="conversationId"/> 在對話目錄裡的擁有者（不在是 null）</param>
    public static (JObject? Message, IReadOnlyList<MediaRefRow> Rows) Rewrite(string deviceId, string commandName, string? conversationId,
        ConversationOwnership? owner, JObject message, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(message);
        var type = message["type"] is JValue { Type: JTokenType.String, Value: string t } ? t : null;
        if (type is not (FilesType or FileRefType or FileSearchType))
            return (message, []);
        if (!SyncIds.IsDeviceId(deviceId) || (conversationId is not null && !IsOwner(conversationId, owner, deviceId)))
            return (null, []);
        if (type == FileSearchType)
            return (message, []);

        var copy = (JObject)message.DeepClone();
        var rows = type == FilesType ? RewriteFiles(copy, deviceId, conversationId, nowMs)
            : commandName == RemoteCommandCatalog.DeviceFetch ? RewriteTransferRef(copy, deviceId, nowMs)
            : RewriteFileRef(copy, deviceId, conversationId, nowMs);
        return Utf8Length(copy) > MaxMessageBytes ? (null, []) : (copy, rows);
    }

    /// <summary>改寫後的大小（UTF-8 位元組，不縮排）</summary>
    public static long Utf8Length(JToken message) => Encoding.UTF8.GetByteCount(message.ToString(Formatting.None));

    private static bool IsOwner(string conversationId, ConversationOwnership? owner, string deviceId) =>
        ConversationIds.IsWireId(conversationId) && owner is not null
        && string.Equals(owner.ConversationId, conversationId, StringComparison.Ordinal)
        && string.Equals(owner.OwnerDeviceId, deviceId, StringComparison.Ordinal);

    private static List<MediaRefRow> RewriteFiles(JObject message, string deviceId, string? conversationId, long nowMs)
    {
        var rows = new List<MediaRefRow>();
        if (message["entries"] is not JArray entries)
            return rows;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in entries)
        {
            if (token is not JObject entry)
                continue;
            var mediaRef = entry["ref"] is JValue { Type: JTokenType.String, Value: string r } ? r : null;
            var version = Version(entry["ver"]);
            foreach (var name in new[] { "ref", "ver", "url", "thumbUrl" })
                entry.Remove(name);

            string? url = null;
            string? thumbUrl = null;
            if (conversationId is not null && ConversationEventRules.IsMediaRef(mediaRef)
                && entry["kind"] is JValue { Type: JTokenType.String, Value: "image" or "video" }
                && entry["name"] is JValue { Type: JTokenType.String, Value: string fileName } && AgentAttachmentRules.IsDisplayableMedia(fileName))
            {
                var key = MediaKeyRules.Format(deviceId, mediaRef!);
                url = "/m/" + key;
                thumbUrl = "/t/" + key + "?s=" + ListThumbSize.ToString(CultureInfo.InvariantCulture) + (version is null ? string.Empty : "&v=" + version);
                if (seen.Add(mediaRef!))
                    rows.Add(new MediaRefRow(deviceId, mediaRef!, conversationId, MediaRefPurposes.List, nowMs));
            }

            entry["url"] = UrlValue(url);
            entry["thumbUrl"] = UrlValue(thumbUrl);
        }

        return rows;
    }

    /// <summary>網址或 JSON null（<c>(string?)null</c> 隱含轉成的 JValue 型別是 String，序列化雖然是 null，讀的一方看型別會誤判）</summary>
    private static JToken UrlValue(string? url) => url is null ? JValue.CreateNull() : new JValue(url);

    /// <summary>沒有值：欄位不在、JSON null，或值是 null 的 JValue</summary>
    private static bool IsNullish(JToken? token) => token is null || token.Type == JTokenType.Null || token is JValue { Value: null };

    private static List<MediaRefRow> RewriteFileRef(JObject message, string deviceId, string? conversationId, long nowMs)
    {
        var mediaRef = message["ref"] is JValue { Type: JTokenType.String, Value: string r } ? r : null;
        message.Remove("ref");
        message.Remove("url");
        if (!IsNullish(message["error"]) || conversationId is null || !ConversationEventRules.IsMediaRef(mediaRef))
        {
            message["url"] = JValue.CreateNull();
            return [];
        }

        message["url"] = "/f/" + MediaKeyRules.Format(deviceId, mediaRef!);
        var cap = nowMs + (long)MediaLookupRules.FileLifetime.TotalMilliseconds;
        message["expiresAt"] = message["expiresAt"] is JValue { Type: JTokenType.Integer, Value: long given } && given < cap ? given : cap;
        return [new MediaRefRow(deviceId, mediaRef!, conversationId, MediaRefPurposes.File, nowMs)];
    }

    /// <summary>
    /// 電腦狀態面板的檔案傳輸（deviceFetch 的 fileRef；2026-11-09）：沒有對話，登記 <c>xfer</c> 參照（對話 id 是空字串），<c>url</c>＝<c>/f/{key}</c>，
    /// <c>expiresAt</c> 取裝置給的值與現在加 2 小時的較小者；有 <c>error</c>（或參照不合格）時不登記、<c>url</c> 是 null
    /// </summary>
    private static List<MediaRefRow> RewriteTransferRef(JObject message, string deviceId, long nowMs)
    {
        var mediaRef = message["ref"] is JValue { Type: JTokenType.String, Value: string r } ? r : null;
        message.Remove("ref");
        message.Remove("url");
        if (!IsNullish(message["error"]) || !ConversationEventRules.IsMediaRef(mediaRef))
        {
            message["url"] = JValue.CreateNull();
            return [];
        }

        message["url"] = "/f/" + MediaKeyRules.Format(deviceId, mediaRef!);
        var cap = nowMs + (long)MediaLookupRules.XferLifetime.TotalMilliseconds;
        message["expiresAt"] = message["expiresAt"] is JValue { Type: JTokenType.Integer, Value: long given } && given < cap ? given : cap;
        return [new MediaRefRow(deviceId, mediaRef!, string.Empty, MediaRefPurposes.Transfer, nowMs)];
    }

    /// <summary>檔案版本（縮圖網址的 <c>v</c>）：1～19 位數字，數字或字串都收；其他回 null</summary>
    private static string? Version(JToken? value)
    {
        var text = value switch
        {
            JValue { Type: JTokenType.Integer } number => Convert.ToString(number.Value, CultureInfo.InvariantCulture),
            JValue { Type: JTokenType.String, Value: string s } => s,
            _ => null,
        };
        return text is not null && MediaServeRules.IsThumbVersion(text) ? text : null;
    }
}
