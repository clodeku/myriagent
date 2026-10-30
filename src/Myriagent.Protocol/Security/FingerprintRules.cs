using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Sync;

namespace Myriagent.Security;

/// <summary>
/// 給人對照的指紋（設計 6.3、8.2.4）：SHA-256 的 Crockford Base32 前若干字，每 4 字一組以連字號分隔。
/// <list type="bullet">
/// <item><see cref="Of"/>：8 字（40 位元），裝置指紋——配對時桌面與核准頁同時顯示、10 分鐘內有效，攻擊者來不及做出同指紋的金鑰。</item>
/// <item><see cref="Anchor"/>：16 字（80 位元），信任根——伺服器身分金鑰（SPKI）。
/// 這是公開、長期不變的值，只用 40 位元的話做得出同指紋的冒牌金鑰（約 2^40 次嘗試），本機確認時會被騙過。</item>
/// </list>
/// 一律取公鑰的 SPKI（正規編碼）。
/// </summary>
internal static class FingerprintRules
{
    public const int DeviceChars = 8;

    public const int AnchorChars = 16;

    public static string Of(ReadOnlySpan<byte> data) => Format(data, DeviceChars);

    public static string Anchor(ReadOnlySpan<byte> data) => Format(data, AnchorChars);

    private static string Format(ReadOnlySpan<byte> data, int chars)
    {
        var text = CrockfordBase32.Encode(SHA256.HashData(data));
        return string.Join('-', Enumerable.Range(0, chars / 4).Select(i => text.Substring(i * 4, 4)));
    }
}

/// <summary>
/// 不可信的顯示名稱（裝置名稱、電腦名稱；顯示在核准頁與裝置清單）：去頭尾空白後 1～上限字，
/// 不含控制字元與格式字元（雙向覆寫 U+202E、零寬字元等，可以讓畫面上的名稱與實際內容不同）。顯示時仍一律 textContent。
/// </summary>
internal static class LabelRules
{
    /// <summary>
    /// 清理後的名稱：去掉控制、格式（雙向覆寫、零寬字元）、行段分隔字元與孤立的代理字元，再去頭尾空白；結果是空的或超過
    /// <paramref name="maxLength"/> 回 null。去掉而不是整個拒絕：電腦名稱常有含零寬連接字元的組合 emoji，
    /// 整個拒絕的話只能換成預設名稱
    /// </summary>
    public static string? Clean(string? name, int maxLength)
    {
        if (name is null)
            return null;
        var builder = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length;)
        {
            // 孤立的代理字元（不成對的 UTF-16）也去掉：轉 UTF-8 時會被換成 U+FFFD，簽的與顯示的就不同
            var status = Rune.DecodeFromUtf16(name.AsSpan(i), out var rune, out var used);
            if (status == OperationStatus.Done
                && Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
                builder.Append(name, i, used);
            i += Math.Max(used, 1);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 || cleaned.Length > maxLength ? null : cleaned;
    }

    /// <summary>名冊裡的名稱必須已經是清理過的樣子（伺服器存入前先 <see cref="Clean"/>）</summary>
    public static bool IsClean(string? name, int maxLength) => name is not null && Clean(name, maxLength) == name;
}
