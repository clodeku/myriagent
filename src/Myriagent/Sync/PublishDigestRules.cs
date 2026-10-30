using System.Globalization;
using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 發布狀態的摘要（WP4.3 計畫 D11；sync.db v3 的 <c>published_items</c>、<c>publish_meta</c>）：用來判斷「這一筆送過了沒有、之後改過沒有」，
/// 不必每次啟動整份重送。全部是 SHA-256 的小寫十六進位。摘要不含 <c>ver</c>（每次更新都遞增）與只在程序存活期間有效的欄位，
/// 所以同樣的內容不論哪一次啟動算出來都相同；排序只看 seq 與 Ord，摘要只用來比對內容
/// </summary>
internal static class PublishDigestRules
{
    private const string VerProperty = "ver";

    /// <summary>線上投影（<c>conv.item</c> 的 payload，<see cref="ConversationWire.TryItem"/>）拿掉 <c>ver</c> 之後的摘要；其他欄位照原本的順序</summary>
    public static string Wire(JObject wireItem)
    {
        ArgumentNullException.ThrowIfNull(wireItem);
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var writer = new JsonTextWriter(text) { Formatting = Formatting.None })
        {
            writer.WriteStartObject();
            foreach (var property in wireItem.Properties())
            {
                if (!string.Equals(property.Name, VerProperty, StringComparison.Ordinal))
                    property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return PayloadRules.Sha256Hex(text.ToString());
    }

    /// <summary>
    /// 來源項目（中樞的快照，或從 items.json 讀回的）的摘要：以存檔的設定（<see cref="JsonFile.Settings"/>：略過 null、時間轉本機時區）序列化、不縮排。
    /// 不存檔的欄位（<see cref="AgentChatItem.Seq"/>、<see cref="AgentChatItem.RequestKey"/>）不算在內，所以記憶體裡的項目與從檔案讀回的同一筆摘要相同
    /// </summary>
    public static string Source(AgentChatItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return PayloadRules.Sha256Hex(JsonConvert.SerializeObject(item, Formatting.None, JsonFile.Settings));
    }

    /// <summary>中繼資料、佇列這類整份的 payload 的摘要（種類不同的同一份內容摘要不同）；<paramref name="kind"/> 要是對話事件的種類</summary>
    public static string Payload(string kind, string json)
    {
        if (!ConversationEventKinds.IsConversationKind(kind))
            throw new ArgumentException("不是對話事件的種類", nameof(kind));
        ArgumentNullException.ThrowIfNull(json);
        // 種類裡沒有換行，第一個換行就是分隔，不會把 (種類, 內容) 的兩種切法算成同一個摘要
        return PayloadRules.Sha256Hex(kind + "\n" + json);
    }
}
