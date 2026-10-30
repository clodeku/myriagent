using System.Text;

namespace Myriagent.Sync;

/// <summary>
/// 時間窗鍵的線上格式（sync-protocol 6.4、12 節）：伺服器只收 <c>[A-Za-z0-9-_]</c>、≤32 字的鍵，本機的鍵卻有「:」
/// （Claude 的 <c>weekly:模型名</c>、Codex 的 <c>{limitId}:{分鐘數}</c>）。裝置上傳前以 <see cref="Encode"/> 跳脫、
/// 拉到時以 <see cref="Decode"/> 還原；伺服器存跳脫後的鍵（走勢也以它累積），瀏覽器閘道送給網頁時才還原。桌面與伺服器共用這一份
/// </summary>
internal static class UsageWindowKeyRules
{
    public const int MaxLength = 32;

    /// <summary>
    /// 以「_」跳脫：<c>_</c>→<c>__</c>、<c>:</c>→<c>_c</c>、<c>.</c>→<c>_d</c>、空白→<c>_s</c>；其他字元、空字串與跳脫後超過
    /// <see cref="MaxLength"/> 字的回 null（丟掉該窗）。沒有這些字元的鍵（session、weekly）不變
    /// </summary>
    public static string? Encode(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return null;
        var builder = new StringBuilder(key.Length + 4);
        foreach (var c in key)
        {
            switch (c)
            {
                case >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-':
                    builder.Append(c);
                    break;
                case '_':
                    builder.Append("__");
                    break;
                case ':':
                    builder.Append("_c");
                    break;
                case '.':
                    builder.Append("_d");
                    break;
                case ' ':
                    builder.Append("_s");
                    break;
                default:
                    return null;
            }

            if (builder.Length > MaxLength)
                return null;
        }

        return builder.ToString();
    }

    /// <summary>
    /// 同一份用量的全部鍵一起還原（拿鍵當時間窗識別的呼叫端用這個，不要逐一 <see cref="Decode"/>）：還原後的鍵等於這份用量裡另一個原本的鍵時
    /// （照原樣的 <c>a_x</c> 與合格式的 <c>a__x</c>，不論誰在前面），送原本的鍵。線上鍵兩兩不同（伺服器的 Validate 保證）、
    /// 而且只用 <c>[A-Za-z0-9-_]</c> 時，結果也兩兩不同（還原只改合格式的鍵，在合格式的鍵上是一對一）；保底：結果仍有重複時整份回原本的鍵。
    /// 只依鍵的集合決定，與順序無關
    /// </summary>
    public static IReadOnlyList<string> DecodeAll(IReadOnlyList<string> keys)
    {
        var originals = new HashSet<string>(keys, StringComparer.Ordinal);
        var decoded = new string[keys.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Count; i++)
        {
            var key = Decode(keys[i]);
            decoded[i] = key != keys[i] && originals.Contains(key) ? keys[i] : key;
            if (!seen.Add(decoded[i]))
                return keys;
        }

        return decoded;
    }

    /// <summary>
    /// <see cref="Encode"/> 的反向。不是這種跳脫格式的鍵（別的實作送的：尾端單獨的「_」、不認得的跳脫）整個照原樣回傳。
    /// 因此解碼不是一對一：照原樣的 <c>a_x</c> 與合格式的 <c>a__x</c> 都得到 <c>a_x</c>（要當識別的呼叫端用 <see cref="DecodeAll"/>）
    /// </summary>
    public static string Decode(string key)
    {
        if (!key.Contains('_'))
            return key;
        var builder = new StringBuilder(key.Length);
        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] != '_')
            {
                builder.Append(key[i]);
                continue;
            }

            if (i + 1 >= key.Length)
                return key;
            var decoded = key[i + 1] switch
            {
                '_' => '_',
                'c' => ':',
                'd' => '.',
                's' => ' ',
                _ => (char?)null,
            };
            if (decoded is null)
                return key;
            builder.Append(decoded.Value);
            i++;
        }

        return builder.ToString();
    }
}
