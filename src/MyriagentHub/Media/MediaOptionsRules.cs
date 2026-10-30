using System.Globalization;
using Myriagent.Sync;
using MyriagentHub.Hosting;

namespace MyriagentHub.Media;

/// <summary>
/// <c>Hub:Media</c>（<see cref="MediaOptions"/>）的驗證：啟動時（<c>AddHubMedia</c>，不合格丟例外、服務起不來）與
/// admin preflight（<c>config.media</c> 一項，部署前就擋下）呼叫同一份。純函式
/// </summary>
internal static class MediaOptionsRules
{
    public const string SectionKey = HubOptions.Section + ":Media";

    /// <summary>
    /// 第一個問題的說明（含設定鍵名與值），沒問題回 null。規則：所有上限 &gt;0；
    /// <see cref="MediaOptions.MaxItemBlobBytes"/> ≤ 型別上限（<see cref="ConversationEventRules.MaxBlobLength"/>）；
    /// <see cref="MediaOptions.MaxUploadBytes"/> ≤ <see cref="MediaOptions.MaxPendingUploadBytes"/>；
    /// 0 &lt; <see cref="MediaOptions.AlertPercent"/> &lt; <see cref="MediaOptions.RefusePercent"/> ≤ 100
    /// </summary>
    public static string? Validate(MediaOptions? options)
    {
        if (options is null)
            return SectionKey + " 不能是 null";

        foreach (var (name, value) in Limits(options))
        {
            if (value <= 0)
                return $"{SectionKey}:{name} 必須大於 0：{value.ToString(CultureInfo.InvariantCulture)}";
        }

        if (options.MaxItemBlobBytes > ConversationEventRules.MaxBlobLength)
        {
            return $"{SectionKey}:{nameof(MediaOptions.MaxItemBlobBytes)}（{options.MaxItemBlobBytes.ToString(CultureInfo.InvariantCulture)}）"
                + $"不能超過 blob 的型別上限 {ConversationEventRules.MaxBlobLength.ToString(CultureInfo.InvariantCulture)}";
        }

        if (options.MaxUploadBytes > options.MaxPendingUploadBytes)
        {
            return $"{SectionKey}:{nameof(MediaOptions.MaxUploadBytes)}（{options.MaxUploadBytes.ToString(CultureInfo.InvariantCulture)}）"
                + $"不能超過 {SectionKey}:{nameof(MediaOptions.MaxPendingUploadBytes)}（{options.MaxPendingUploadBytes.ToString(CultureInfo.InvariantCulture)}）";
        }

        if (options.AlertPercent >= options.RefusePercent || options.RefusePercent > 100)
        {
            return $"{SectionKey}:{nameof(MediaOptions.AlertPercent)}（{options.AlertPercent.ToString(CultureInfo.InvariantCulture)}）"
                + $"要小於 {SectionKey}:{nameof(MediaOptions.RefusePercent)}（{options.RefusePercent.ToString(CultureInfo.InvariantCulture)}），後者 ≤100";
        }

        return null;
    }

    /// <summary>必須大於 0 的上限（設定鍵名、值）</summary>
    internal static IEnumerable<(string Name, long Value)> Limits(MediaOptions o) =>
    [
        (nameof(o.QuotaBytes), o.QuotaBytes),
        (nameof(o.AlertPercent), o.AlertPercent),
        (nameof(o.RefusePercent), o.RefusePercent),
        (nameof(o.MinFreeBytes), o.MinFreeBytes),
        (nameof(o.MaxItemBlobBytes), o.MaxItemBlobBytes),
        (nameof(o.MaxThumbBytes), o.MaxThumbBytes),
        (nameof(o.MaxUploadBytes), o.MaxUploadBytes),
        (nameof(o.MaxPendingUploadBytes), o.MaxPendingUploadBytes),
        (nameof(o.ThumbCacheBytes), o.ThumbCacheBytes),
        (nameof(o.FirstByteTimeoutSeconds), o.FirstByteTimeoutSeconds),
        (nameof(o.MaxConcurrentRelays), o.MaxConcurrentRelays),
    ];
}
