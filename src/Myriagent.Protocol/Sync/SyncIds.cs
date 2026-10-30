using System.Security.Cryptography;

namespace Myriagent.Sync;

/// <summary>
/// Crockford Base32（0–9、A–Z 去掉 I L O U）：位元流由高位到低位每 5 位元一個字，最後不足 5 位元補 0。
/// 識別碼與指紋都用它（大寫、沒有容易看錯的字母，念給人聽也不會混淆）。
/// </summary>
internal static class CrockfordBase32
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var chars = new char[(data.Length * 8 + 4) / 5];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                chars[index++] = Alphabet[(buffer >> bits) & 31];
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
            chars[index++] = Alphabet[(buffer << (5 - bits)) & 31];
        return new string(chars, 0, index);
    }

    public static bool IsValid(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (Alphabet.IndexOf(c) < 0)
                return false;
        }

        return true;
    }
}

/// <summary>
/// 同步的識別碼（設計 3.5）：deviceId＝<c>d_</c>、邏輯帳戶＝<c>la_</c>、授權＝<c>g_</c>，後面接 128 位元亂數的 Crockford Base32（26 字）；
/// 批次、指令、交接的 id 是 ULID（48 位元毫秒時間＋80 位元亂數，26 字，依時間排序）。
/// </summary>
internal static class SyncIds
{
    public const string DevicePrefix = "d_";

    public const string LogicalAccountPrefix = "la_";

    public const string GrantPrefix = "g_";

    public const int RandomChars = 26;

    public static string NewDeviceId() => DevicePrefix + NewRandom();

    public static string NewLogicalAccountId() => LogicalAccountPrefix + NewRandom();

    public static bool IsDeviceId(string? value) => HasPrefixAndBody(value, DevicePrefix);

    public static bool IsLogicalAccountId(string? value) => HasPrefixAndBody(value, LogicalAccountPrefix);

    /// <summary>ULID：時間在前（同一毫秒內的先後不保證），用來當批次與指令的 id</summary>
    public static string NewUlid(long unixMs)
    {
        Span<byte> bytes = stackalloc byte[16];
        var time = (ulong)Math.Max(0, unixMs);
        for (var i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)time;
            time >>= 8;
        }

        RandomNumberGenerator.Fill(bytes[6..]);
        // 128 位元編成 26 字時前面要補 2 個 0 位元（ULID 規格：第一個字 ≤ 7）
        Span<byte> padded = stackalloc byte[17];
        padded.Clear();
        ShiftRightTwoBits(bytes, padded);
        return CrockfordBase32.Encode(padded)[..RandomChars];
    }

    public static bool IsUlid(string? value) =>
        value is { Length: RandomChars } && value[0] <= '7' && CrockfordBase32.IsValid(value);

    private static string NewRandom()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return CrockfordBase32.Encode(bytes);
    }

    private static bool HasPrefixAndBody(string? value, string prefix) =>
        value is not null
        && value.Length == prefix.Length + RandomChars
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && CrockfordBase32.IsValid(value.AsSpan(prefix.Length));

    /// <summary>把 16 位元組右移 2 位元放進 17 位元組（最前面多出 2 個 0 位元，編碼後的前 26 字就是 ULID）</summary>
    private static void ShiftRightTwoBits(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var carry = 0;
        for (var i = 0; i < source.Length; i++)
        {
            target[i] = (byte)((carry << 6) | (source[i] >> 2));
            carry = source[i] & 0b11;
        }

        target[source.Length] = (byte)(carry << 6);
    }
}
