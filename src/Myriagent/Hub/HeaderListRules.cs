using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 標頭瘦身（2026-10-23）：標頭的「/」指令清單（<c>commands</c>）與模型清單（<c>models</c>）一個 Claude 帳戶就有 14～33 KB，
/// 對話執行中標頭每 50 ms 就可能整份送一次（狀態文字、工具開始與結束）。同一個視窗上次收到的兩份清單沒變時，<c>header</c> 訊息省略這兩個欄位，
/// 網頁沿用上一份（<c>liverules.mergeHeaderLists</c>）；對話快照（<c>conversation</c>）一律整份。docs/web-protocol.md 的 header
/// </summary>
internal static class HeaderListRules
{
    /// <summary>沒變就省略的欄位</summary>
    public static IReadOnlyList<string> Fields { get; } = ["commands", "models", "agents", "outputStyles"];

    /// <summary>兩份清單的指紋（依序序列化後的 SHA-256）；沒有的欄位當成 null，所以「沒有」和「空陣列」不同</summary>
    public static string Signature(JObject header)
    {
        var sb = new StringBuilder();
        foreach (var field in Fields)
        {
            sb.Append(header.TryGetValue(field, out var value) ? value.ToString(Formatting.None) : "\0");
            sb.Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>上次送給這個視窗的指紋（沒送過是 null）等於這一次的：可以省略兩份清單</summary>
    public static bool CanOmit(string signature, string? lastSent) => lastSent is not null && string.Equals(signature, lastSent, StringComparison.Ordinal);

    /// <summary>去掉 <see cref="Fields"/> 的淺拷貝（其餘欄位照原順序；原本的標頭不變）</summary>
    public static JObject WithoutLists(JObject header) =>
        new(header.Properties().Where(p => !Fields.Contains(p.Name, StringComparer.Ordinal)).Select(p => new JProperty(p.Name, p.Value)));
}
