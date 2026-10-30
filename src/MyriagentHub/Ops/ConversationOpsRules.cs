using Myriagent.Sync;
using MyriagentHub.Sync;

namespace MyriagentHub.Ops;

/// <summary>
/// ops/status 的 <c>conversations</c> 區塊（WP4.2 計畫 D31、答 7）：<b>只有計數</b>，不放對話 id 與標題（<c>/readyz</c> 以裝置身分也讀得到同一份）。
/// 沒有功能開關，所以沒有開關的欄位（答 1）；<c>rejected</c> 沒有 <c>dlp</c>（伺服器遮蔽後接受、不回它，答 5）。
/// </summary>
internal static class ConversationOpsRules
{
    public const string LevelOk = "ok";

    /// <summary>「注意」：sync.db 主檔 ≥ <c>SyncDbWarnBytes</c>（2 GiB）</summary>
    public const string LevelWarn = "warn";

    /// <summary>「嚴重」：sync.db 主檔 ≥ <c>SyncDbCriticalBytes</c>（8 GiB）；只是告警，伺服器不停止收事件（K19）</summary>
    public const string LevelCritical = "critical";

    /// <summary>sync.db 的容量等級（依主檔大小；-wal 另列、會在檢查點後縮回）</summary>
    public static string SyncDbLevel(long bytes, long warnBytes, long criticalBytes) =>
        bytes >= criticalBytes ? LevelCritical : bytes >= warnBytes ? LevelWarn : LevelOk;

    /// <summary>區塊本身（屬性名稱就是 JSON 的鍵；鍵集合由 HealthTests 寫死）</summary>
    public static object Block(ConversationCountsSnapshot counts, int rebuilding, int rebuildPending, long syncDbBytes, long syncWalBytes, string syncDbLevel)
    {
        var dropped = counts.LiveDropped ?? new Dictionary<string, long>();
        long Dropped(string reason) => dropped.TryGetValue(reason, out var n) ? n : 0;
        long Rejected(string reason) => counts.Rejected.TryGetValue(reason, out var n) ? n : 0;
        return new
        {
            count = counts.Count,
            items = counts.Items,
            deleted = counts.Deleted,
            recovering = counts.Recovering,
            rebuilding,
            rebuildPending,
            unread = counts.Unread,
            overlayBytes = counts.OverlayBytes,
            liveDropped = new
            {
                rate = Dropped("rate"),
                dlp = Dropped("dlp"),
                notOwner = Dropped("notOwner"),
                policy = Dropped("policy"),
                final = Dropped("final"),
                stale = Dropped("stale"),
            },
            outboxOverflows = counts.OutboxOverflows,
            rejected = new
            {
                notOwner = Rejected(ConversationRejectCodes.NotOwner),
                fenced = Rejected(ConversationRejectCodes.Fenced),
                deleted = Rejected(ConversationRejectCodes.Deleted),
                invalid = Rejected(SyncErrorCodes.Invalid),
                tooLarge = Rejected(SyncErrorCodes.TooLarge),
            },
            remasked = new { events = counts.RemaskedEvents, live = counts.RemaskedLive },
            conversationEvents = counts.ConversationEvents,
            conversationEventBytes = counts.ConversationEventBytes,
            conversationEventsBaselined = counts.EventsBaselined,
            syncDbBytes,
            syncWalBytes,
            syncDbLevel,
            devices = counts.Devices.Select(d => new
            {
                deviceId = d.DeviceId,
                owned = d.Owned,
                recovering = d.Recovering,
                items = d.Items,
                maxSeq = d.MaxSeq,
                lastEventAt = d.LastEventAt,
            }).ToList(),
        };
    }
}
