namespace Myriagent.Sync;

/// <summary>
/// 對話事件被伺服器擋下的原因（<c>BatchEventResult.Rejected</c>；docs/sync-protocol.md 14.2）。
/// 第 3 階段的 <c>invalid</c>、<c>too_large</c> 在 <see cref="SyncErrorCodes"/>；<c>conflict</c> 屬於目錄同步，對話事件不會收到。
/// WP4.2（伺服器寫）與 WP4.3（桌面讀）共用同一份、逐位元組相同；線上名稱固定、只加不改，桌面不認得的碼一律當成程式錯誤。
/// </summary>
internal static class ConversationRejectCodes
{
    /// <summary>作者不是這個對話的擁有者裝置（第一筆建立事件決定擁有者；重新配對成新身分後的舊對話也是）</summary>
    public const string NotOwner = "not_owner";

    /// <summary>epoch 不是對話目前的世代</summary>
    public const string Fenced = "fenced";

    /// <summary>對話已在伺服器上刪除（conv.deleted 之後，含改成只在這台電腦）；不能再改回同步</summary>
    public const string Deleted = "deleted";

    /// <summary>
    /// 保留：伺服器端第二道權杖 DLP 命中。WP4.2 起伺服器遮蔽後接受、不回這個碼（擁有者 2026-10-19 選 C）；
    /// 桌面收到時照 ConversationRejectRules 防禦性處理。
    /// </summary>
    public const string Dlp = "dlp";

    /// <summary>全部的碼（測試與文件用）</summary>
    public static IReadOnlyList<string> All { get; } = [NotOwner, Fenced, Deleted, Dlp];

    /// <summary>是不是這裡定義的碼；null 與 SyncErrorCodes 的碼回 false</summary>
    public static bool IsKnown(string? code) => code is NotOwner or Fenced or Deleted or Dlp;
}
