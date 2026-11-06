namespace Myriagent.Core;

/// <summary>
/// 從其他電腦、瀏覽器版（含協作人）來的檔案標成「從網際網路下載」（NTFS 替代資料流 Zone.Identifier，ZoneId=3，和瀏覽器下載的一樣）：
/// 用預設程式開時 Office 以保護的檢視開啟、封鎖網路來的巨集，SmartScreen 照樣提醒（2026-11-07 資安檢查：以前這些檔沒有標記，
/// 在 app 裡點了就以完整信任開啟）。只標記、不擋；寫不了（不是 NTFS、權限）就算了
/// </summary>
internal static class MarkOfTheWeb
{
    public const string StreamName = "Zone.Identifier";

    /// <summary>Zone.Identifier 的內容（InternetZone）</summary>
    public const string Content = "[ZoneTransfer]\r\nZoneId=3\r\n";

    /// <summary>標記一個本機檔；成功回 true</summary>
    public static bool Mark(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;
        try
        {
            File.WriteAllText(path + ":" + StreamName, Content);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
