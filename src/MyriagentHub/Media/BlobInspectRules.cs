using System.Text;
using MyriagentHub.Sync;

namespace MyriagentHub.Media;

/// <summary>
/// 裝置上傳的項目欄位 blob 的第二道 DLP（WP4.7 計畫 D15；契約:764、SP:602）：內容是 UTF-8（字串欄位是字串本身、陣列欄位是那個陣列的 JSON），
/// 嚴格解碼後跑 <see cref="ConversationDlpRules.HasLeakText"/>（和渲染器讀 blob 時同一個函式）。禁止的鍵不在這裡查：伺服器分不出 blob 是字串欄還是陣列欄，
/// 內容剛好是含 <c>path</c> 鍵的 JSON 陣列的文字會被誤擋，交給讀取端（<c>ConversationWire.TryReadItem</c> 換回 blob 後重跑形狀檢查）。純函式
/// </summary>
internal static class BlobInspectRules
{
    /// <summary>不是嚴格 UTF-8（不合格的位元組序列、結尾被切斷的多位元組字元）</summary>
    public const string Invalid = "invalid";

    /// <summary>疑似權杖</summary>
    public const string Dlp = "dlp";

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>null＝通過；<see cref="Invalid"/>（不是嚴格 UTF-8）、<see cref="Dlp"/>（疑似權杖）</summary>
    public static string? CheckItemBody(ReadOnlySpan<byte> utf8)
    {
        string text;
        try
        {
            text = Strict.GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            return Invalid;
        }

        return ConversationDlpRules.HasLeakText(text) ? Dlp : null;
    }

    /// <summary>
    /// 縮圖的內容型別（D15：縮圖只驗魔數）：PNG（<c>89 50 4E 47 0D 0A 1A 0A</c>）回 <c>image/png</c>、JPEG（<c>FF D8 FF</c>）回 <c>image/jpeg</c>，
    /// 其他（含空的）回 null
    /// </summary>
    public static string? ThumbnailType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return "image/jpeg";
        return null;
    }
}
