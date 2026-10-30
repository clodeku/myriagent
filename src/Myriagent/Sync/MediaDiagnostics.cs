using Myriagent.Core;
using Myriagent.Diagnostics;

namespace Myriagent.Sync;

/// <summary>
/// 媒體轉送（<see cref="MediaServer"/>）與縮圖上傳（<see cref="ThumbnailPublisher"/>）不給的時候的診斷記錄（只寫本機記錄檔）：
/// 寫出對話、項目、用途、原因與要的檔，同一個參照＋用途＋原因 10 分鐘內只記一次、之後補記略過的次數。
/// 2026-10-23：網頁版的破圖在記錄裡只有「媒體轉送 {relayId}：守門員拒絕（forbidden）」，兩天上千行卻看不出是哪個對話的哪個檔
/// </summary>
internal static class MediaDiagnostics
{
    /// <summary>同一件事多久只記一次</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly LogThrottle Throttle = new(Window, TimeProvider.System);

    /// <summary>
    /// 記一筆不給（<paramref name="warn"/>＝看起來是程式或設定的問題，例如登記過的媒體被守門員擋下；檔案刪了、政策不給這類預期中的用 Info）
    /// </summary>
    /// <param name="action">哪一件事（「媒體轉送」「縮圖上傳」）</param>
    /// <param name="path">要的檔（知道時）；沒給時用參照列登記的路徑或附件 id</param>
    public static void Refused(string action, string? mediaRef, LocalMediaRefRow? row, string purpose, string reason, string? path = null, bool warn = false)
    {
        if (!Throttle.ShouldLog($"{action}|{mediaRef}|{purpose}|{reason}", out var suppressed))
            return;
        var line = MediaDiagnosticsRules.Describe(action, mediaRef, row, purpose, reason, path) + LogThrottle.SuppressedNote(suppressed);
        if (warn)
            Log.Warn(line);
        else
            Log.Info(line);
    }
}

/// <summary><see cref="MediaDiagnostics"/> 記錄行的內容（純函式）</summary>
internal static class MediaDiagnosticsRules
{
    /// <summary>
    /// 「{action}不給：{reason}（對話 …、項目 …、{purpose}、參照 …、檔 …）」：沒有參照列時對話與項目寫「?」；檔依序用
    /// <paramref name="path"/>、列登記的路徑、附件 id
    /// </summary>
    public static string Describe(string action, string? mediaRef, LocalMediaRefRow? row, string purpose, string reason, string? path)
    {
        var file = path ?? row?.MediaPath ?? (row?.AttachmentId is { } attachment ? "附件 " + attachment : null);
        return $"{action}不給：{reason}（對話 {row?.ConversationId ?? "?"}、項目 {row?.ItemId ?? "?"}、{purpose}、參照 {mediaRef ?? "?"}"
               + (file is null ? "）" : $"、檔 {file}）");
    }

    /// <summary>守門員的拒絕原因要不要用 Warn：檔不存在（刪了、搬走）是預期中的，其他（禁止清單、範圍外、重新解析點…）多半是規則或程式的問題</summary>
    public static bool IsUnexpectedGuardReason(string? reason) =>
        reason is not (RemoteFileGuardReasons.NotFound or RemoteFileGuardReasons.NotFile);
}
