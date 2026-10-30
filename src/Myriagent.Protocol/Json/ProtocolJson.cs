using Newtonsoft.Json;

namespace Myriagent.Json;

/// <summary>
/// 桌面與伺服器共用的 Newtonsoft 設定（設計 2.5）：與桌面存檔（JsonFile）完全相同——JsonFile.Settings 就是這一份。
/// items.json、meta.json 與要送給伺服器的原始項目是同一種格式，伺服器反序列化回來的 AgentChatItem 才會與桌面記憶體裡的一樣、
/// 渲染出一樣的 Item JSON（ItemRendererGoldenTests 以它往返後再渲染，輸出與黃金檔逐位元組相同）。
/// 不存檔的欄位（AgentChatItem.Seq、RequestKey）不在 JSON 裡，由接收端自己配
/// </summary>
internal static class ProtocolJson
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        Formatting = Formatting.Indented,
        NullValueHandling = NullValueHandling.Ignore,
        DateTimeZoneHandling = DateTimeZoneHandling.Local,
    };
}
