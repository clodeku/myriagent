using System.Diagnostics.CodeAnalysis;
using Myriagent.Sync;

namespace Myriagent.Media;

/// <summary>
/// 媒體網址的鍵（WP4.7 計畫 D2）：<c>{deviceId}-{ref}</c>，也就是 <c>d_</c>＋26 字、<c>-</c>、26 字（都是 Crockford Base32 大寫）。
/// 伺服器 <c>media_refs</c> 的主鍵是 <c>(device_id, ref)</c>，鍵裡帶裝置才能直接以主鍵查；
/// 鍵只有英數字、<c>_</c>、<c>-</c>，伺服器 <c>ItemSanitizer</c> 收網址的正規式不用改
/// </summary>
internal static class MediaKeyRules
{
    /// <summary>鍵的固定長度：裝置 id 28 字＋分隔 1 字＋參照 26 字</summary>
    public const int Length = 2 + SyncIds.RandomChars + 1 + SyncIds.RandomChars;

    private const int SeparatorIndex = 2 + SyncIds.RandomChars;

    /// <summary>組出 <c>{deviceId}-{ref}</c>；裝置 id 或參照不合格丟 <see cref="ArgumentException"/>（呼叫端的錯，不是外部輸入）</summary>
    public static string Format(string deviceId, string mediaRef)
    {
        if (!SyncIds.IsDeviceId(deviceId))
            throw new ArgumentException("不是裝置 id", nameof(deviceId));
        if (!ConversationEventRules.IsMediaRef(mediaRef))
            throw new ArgumentException("不是媒體參照（26 個 Crockford Base32 字）", nameof(mediaRef));
        return deviceId + "-" + mediaRef;
    }

    /// <summary>解析網址裡的鍵；長度、分隔字元、裝置 id 與參照都要完全合格（大小寫有別、不收查詢字串或多餘的字）</summary>
    public static bool TryParse(string? key, [NotNullWhen(true)] out string? deviceId, [NotNullWhen(true)] out string? mediaRef)
    {
        deviceId = null;
        mediaRef = null;
        if (key is not { Length: Length } || key[SeparatorIndex] != '-')
            return false;
        var device = key[..SeparatorIndex];
        var reference = key[(SeparatorIndex + 1)..];
        if (!SyncIds.IsDeviceId(device) || !ConversationEventRules.IsMediaRef(reference))
            return false;
        deviceId = device;
        mediaRef = reference;
        return true;
    }
}
