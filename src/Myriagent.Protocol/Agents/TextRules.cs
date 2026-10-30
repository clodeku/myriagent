namespace Myriagent.Agents;

/// <summary>
/// 文字的純函式規則（桌面與伺服器共用）：工具卡與核准卡的一行摘要、拒絕說明裡的操作名稱都用它，兩邊顯示的字才會一樣。
/// </summary>
internal static class TextRules
{
    /// <summary>第一個不是空白的行（去掉頭尾空白；\r\n 與 \n 都算換行），超過 200 字截斷並加「…」；null 或全是空白回空字串</summary>
    public static string FirstLine(string? text)
    {
        var line = (text ?? string.Empty).Replace("\r", string.Empty).Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;
        line = line.Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }
}
