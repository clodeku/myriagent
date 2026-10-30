using Myriagent.Hub;

namespace Myriagent.Sync;

/// <summary>
/// 對話發布的彙總提示文字（WP4.3 計畫 D14、D32；中樞以 toast 顯示）：同一種原因的所有對話併成一則，只說幾個、不列對話。純函式
/// </summary>
internal static class PublishNoticeRules
{
    /// <summary><paramref name="count"/> 個對話因為 <paramref name="kind"/> 停止上傳的提示；<paramref name="count"/> ≤0 丟例外</summary>
    public static string Text(PublishNoticeKind kind, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return kind switch
        {
            PublishNoticeKind.NotOwner => $"有 {count} 個對話是這台電腦重新配對之前上傳的：伺服器上停在之前的內容，這台照常使用，之後的變更不再上傳",
            PublishNoticeKind.Blocked => $"有 {count} 個對話的同步被伺服器擋下，已停止上傳（原因寫在記錄檔）",
            PublishNoticeKind.DlpPaused => $"有 {count} 個對話含疑似權杖的內容被伺服器擋下，同步範圍已改成「暫停」",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "不認得的提示種類"),
        };
    }
}
