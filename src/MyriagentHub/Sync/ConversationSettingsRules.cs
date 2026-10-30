using System.Globalization;
using MyriagentHub.Hosting;

namespace MyriagentHub.Sync;

/// <summary>
/// <c>Hub:Conversations</c>（<see cref="ConversationSettings"/>）的驗證：啟動時（<c>AddHubRealtime</c>，不合格丟例外、服務起不來）與
/// admin preflight（<c>config.conversations</c> 一項，部署前就擋下）呼叫同一份。設定裡只有上限，沒有任何開關（WP4.2 計畫 D1）。
/// </summary>
internal static class ConversationSettingsRules
{
    public const string SectionKey = HubOptions.Section + ":Conversations";

    /// <summary>
    /// 第一個問題的說明（含設定鍵名與值），沒問題回 null。規則：<see cref="ConversationSettings.RejectedRetentionDays"/> ≥0（0＝不刪）；
    /// <see cref="ConversationSettings.SupersededRetentionDays"/> 是 0（不刪）或 ≥2；其他上限一律 &gt;0；<see cref="ConversationSettings.SyncDbWarnBytes"/> 要小於 <see cref="ConversationSettings.SyncDbCriticalBytes"/>
    /// </summary>
    public static string? Validate(ConversationSettings? settings)
    {
        if (settings is null)
            return SectionKey + " 不能是 null";

        foreach (var (name, value) in Limits(settings))
        {
            if (value <= 0)
                return $"{SectionKey}:{name} 必須大於 0：{value.ToString(CultureInfo.InvariantCulture)}";
        }

        if (settings.RejectedRetentionDays < 0)
        {
            return $"{SectionKey}:{nameof(ConversationSettings.RejectedRetentionDays)} 不能是負數（0＝不刪）："
                + settings.RejectedRetentionDays.ToString(CultureInfo.InvariantCulture);
        }

        if (settings.SupersededRetentionDays is < 0 or 1)
        {
            return $"{SectionKey}:{nameof(ConversationSettings.SupersededRetentionDays)} 必須是 0（不刪）或至少 2："
                + settings.SupersededRetentionDays.ToString(CultureInfo.InvariantCulture);
        }

        if (settings.SyncDbWarnBytes >= settings.SyncDbCriticalBytes)
        {
            return $"{SectionKey}:{nameof(ConversationSettings.SyncDbWarnBytes)}（{settings.SyncDbWarnBytes.ToString(CultureInfo.InvariantCulture)}）"
                + $"必須小於 {SectionKey}:{nameof(ConversationSettings.SyncDbCriticalBytes)}（{settings.SyncDbCriticalBytes.ToString(CultureInfo.InvariantCulture)}）";
        }

        return null;
    }

    /// <summary>必須大於 0 的上限（設定鍵名、值）；<see cref="ConversationSettings.RejectedRetentionDays"/>、<see cref="ConversationSettings.SupersededRetentionDays"/> 另外檢查（可以是 0）</summary>
    internal static IEnumerable<(string Name, long Value)> Limits(ConversationSettings s) =>
    [
        (nameof(s.MaxLivePerDevicePerSecond), s.MaxLivePerDevicePerSecond),
        (nameof(s.LiveBurstPerConversation), s.LiveBurstPerConversation),
        (nameof(s.OverlayMaxBytes), s.OverlayMaxBytes),
        (nameof(s.OverlayMaxOrdsPerConversation), s.OverlayMaxOrdsPerConversation),
        (nameof(s.RenderedItemMaxBytes), s.RenderedItemMaxBytes),
        (nameof(s.PageMaxBytes), s.PageMaxBytes),
        (nameof(s.RenderCacheMaxBytes), s.RenderCacheMaxBytes),
        (nameof(s.ChangeFeedMaxEntries), s.ChangeFeedMaxEntries),
        (nameof(s.ChangeFeedMaxBytes), s.ChangeFeedMaxBytes),
        (nameof(s.ConversationPushIntervalMs), s.ConversationPushIntervalMs),
        (nameof(s.MaxOpensPerRound), s.MaxOpensPerRound),
        (nameof(s.SnapshotOpenCooldownMinutes), s.SnapshotOpenCooldownMinutes),
        (nameof(s.SnapshotRestoreGraceSeconds), s.SnapshotRestoreGraceSeconds),
        (nameof(s.SnapshotRestorePerSecond), s.SnapshotRestorePerSecond),
        (nameof(s.DraftGraceSeconds), s.DraftGraceSeconds),
        (nameof(s.MaxDraftsPerSession), s.MaxDraftsPerSession),
        (nameof(s.MaxWatchIds), s.MaxWatchIds),
        (nameof(s.RebuildChunkEvents), s.RebuildChunkEvents),
        (nameof(s.RejectAuditWindowSeconds), s.RejectAuditWindowSeconds),
        (nameof(s.SyncDbWarnBytes), s.SyncDbWarnBytes),
        (nameof(s.SyncDbCriticalBytes), s.SyncDbCriticalBytes),
    ];
}
