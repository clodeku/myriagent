using System.Buffers.Text;

namespace Myriagent.Security;

/// <summary>
/// 簽章相關的位元組一律以不補等號的 base64url 傳遞（裝置與伺服器身分金鑰、簽章、nonce）。解碼採嚴格規則：
/// 只收 A–Z a–z 0–9 - _，不收等號、空白與標準 base64 的 + /，而且重新編碼必須得到同一個字串（尾端多餘的位元不為 0 的寫法不收）。
/// 同一串位元組只有一種寫法，字串才能直接當識別碼比較（credentialId 查表、challenge 比對）。
/// </summary>
internal static class Base64UrlRules
{
    /// <summary>預設上限：公鑰與簽章都遠小於它</summary>
    public const int DefaultMaxBytes = 64 * 1024;

    public static string Encode(ReadOnlySpan<byte> data) => Base64Url.EncodeToString(data);

    public static bool TryDecode(string? text, out byte[] bytes, int maxBytes = DefaultMaxBytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text) || text.Length % 4 == 1 || text.Length > ((long)maxBytes * 4 + 2) / 3)
            return false;
        foreach (var c in text)
        {
            if (!IsUrlChar(c))
                return false;
        }

        byte[] decoded;
        try
        {
            decoded = Base64Url.DecodeFromChars(text);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!string.Equals(Base64Url.EncodeToString(decoded), text, StringComparison.Ordinal))
            return false;
        bytes = decoded;
        return true;
    }

    /// <summary>是否是某串位元組唯一的 base64url 寫法（長度在 1～<paramref name="maxBytes"/> 位元組之間）</summary>
    public static bool IsCanonical(string? text, int maxBytes = DefaultMaxBytes) => TryDecode(text, out _, maxBytes);

    private static bool IsUrlChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
}
