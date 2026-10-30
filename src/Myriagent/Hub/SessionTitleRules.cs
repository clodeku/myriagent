using System.Globalization;

namespace Myriagent.Hub;

/// <summary>
/// 對話標題與代理工作階段名稱的同步（Claude：rename_session／system/session_title_changed，實測見 docs/protocol-notes.md 的 Remote Control）：
/// app 改名時送給 CLI，CLI 回報的名稱（在 claude.ai／Claude App 改的，或 app 自己改名的回送）不同於對話標題才改標題
/// </summary>
internal static class SessionTitleRules
{
    /// <summary>對話標題的長度上限（同中樞的改名）</summary>
    public const int MaxTitleLength = 80;

    /// <summary>記住最近送出的幾個名稱（連續改名時，CLI 依序回送較早的名稱）</summary>
    public const int MaxPending = 8;

    /// <summary>
    /// 照 CLI 整理名稱的方式比較（控制字元與格式字元〔雙向、零寬〕換成空白、去頭尾）；整理後是空的回 null。
    /// CLI 的上限是 200 字，這裡不截，比較前由呼叫端截到對話標題的上限
    /// </summary>
    public static string? Normalize(string? title)
    {
        if (string.IsNullOrEmpty(title))
            return null;
        var chars = title.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsControl(chars[i]) || CharUnicodeInfo.GetUnicodeCategory(chars[i]) == UnicodeCategory.Format)
                chars[i] = ' ';
        }
        var result = new string(chars).Trim();
        return result.Length == 0 ? null : result;
    }

    /// <summary>截到對話標題的上限（不切開代理字對）</summary>
    public static string Clip(string title)
    {
        if (title.Length <= MaxTitleLength)
            return title;
        int length = char.IsHighSurrogate(title[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength;
        return title[..length].TrimEnd();
    }

    /// <summary>app 改名送給 CLI 時記下（整理後的名稱），最多 MaxPending 個</summary>
    public static void RememberSent(List<string> sent, string title)
    {
        if (Normalize(title) is not { } name)
            return;
        sent.Add(name);
        if (sent.Count > MaxPending)
            sent.RemoveRange(0, sent.Count - MaxPending);
    }

    /// <summary>
    /// CLI 回報了工作階段名稱：要改成的對話標題，不必改回 null。
    /// 是 app 自己送出的名稱（含連續改名時較早的那幾個）就是回送：連同更早的一起從 sent 拿掉、不改；
    /// 和目前的標題相同（整理、截斷後）也不改（啟動時與同名重送）；其他是在 claude.ai／Claude App 改的，截到上限後改成它
    /// </summary>
    public static string? FromAgent(List<string> sent, string current, string? reported)
    {
        if (Normalize(reported) is not { } name)
            return null;
        int index = sent.IndexOf(name);
        if (index >= 0)
        {
            sent.RemoveRange(0, index + 1);
            return null;
        }
        var title = Clip(name);
        var now = Normalize(current);
        return now != null && (string.Equals(now, title, StringComparison.Ordinal) || string.Equals(Clip(now), title, StringComparison.Ordinal))
            ? null
            : title;
    }
}
