using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Myriagent.Json;

/// <summary>
/// 可為 null 的列舉屬性用（設計 8.8）：讀到不認得的名稱（之後的版本新增的值）回 null、不丟例外——
/// 否則回滾後的舊版讀到新版寫的 items.json 會把整份檔當成損毀改名備份，那個對話在舊版看起來是空的。
/// 數字（含沒有定義的值，例如黃金語料 undefined-enums 的 5）、null 與認得的名稱照 StringEnumConverter 原樣還原；
/// 寫入完全沿用 StringEnumConverter（存名稱），輸出逐位元組不變。
/// 只掛在「不認得時當成沒有值也不影響流程」的欄位（AgentChatItem.NoticeKind：未知的提示種類當成一般提示，舊版存回時這個欄位會消失；
/// 第 4 階段的 AgentChatItem.Risk 與 RemoteOrigin.EffectiveMode：null 一律當成最嚴格——風險當成非一般、佇列不取出）；
/// 項目種類、狀態、代理種類這類讀錯會讓流程判斷錯的列舉維持嚴格。不可為 null 的型別一律照 StringEnumConverter
/// </summary>
internal sealed class UnknownEnumNameAsNullConverter : StringEnumConverter
{
    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType != JsonToken.String || Nullable.GetUnderlyingType(objectType) == null)
            return base.ReadJson(reader, objectType, existingValue, serializer);
        try
        {
            return base.ReadJson(reader, objectType, existingValue, serializer);
        }
        catch (JsonSerializationException)
        {
            return null;    // 字串只占一個 token，不必略過後面的內容
        }
    }
}
