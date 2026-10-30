using Myriagent.Sync;

namespace Myriagent.Security;

/// <summary>
/// 伺服器發布的裝置名冊一筆（hub.db 的 devices，經 GetTrust 與配對回應發給裝置）：擁有者在密碼登入的瀏覽器核准配對、伺服器記下，
/// 裝置經釘選伺服器身分金鑰的通道收到就採用（伺服器是授權者，設計 14.4）。公鑰拿來驗那台裝置的簽章（Hello 之外，第 4 階段的逐字稿）。
/// </summary>
internal sealed record DeviceRegistryEntry(string DeviceId, string Name, string DevicePubKey, long CreatedAt, long? RevokedAt = null, string? RevokedReason = null);

/// <summary>裝置名冊的格式規則（伺服器發布前與裝置收到時共用）</summary>
internal static class DeviceRegistryRules
{
    public const int MaxNameLength = 60;

    /// <summary>名冊的裝置數上限（裝置收到超過就整份不採用）</summary>
    public const int MaxDevices = 256;

    /// <summary>退役（之前送出的內容仍可信）</summary>
    public const string RevokedRetired = "retired";

    /// <summary>疑似遭入侵</summary>
    public const string RevokedCompromised = "compromised";

    public static bool IsRevokedReason(string? reason) => reason is RevokedRetired or RevokedCompromised;

    /// <summary>一筆的格式：deviceId、清理過的名稱、P-256 SPKI；撤銷時間與原因同時有或同時沒有</summary>
    public static bool IsValid(DeviceRegistryEntry? entry) =>
        entry is not null
        && SyncIds.IsDeviceId(entry.DeviceId)
        && LabelRules.IsClean(entry.Name, MaxNameLength)
        && P256KeyRules.IsValidSpki(entry.DevicePubKey)
        && entry.CreatedAt >= 0
        && (entry.RevokedAt is null ? entry.RevokedReason is null : IsRevokedReason(entry.RevokedReason));

    /// <summary>整份名冊：不超過上限、每筆格式正確、deviceId 不重複</summary>
    public static bool IsValid(IReadOnlyList<DeviceRegistryEntry>? registry)
    {
        if (registry is null || registry.Count > MaxDevices)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in registry)
        {
            if (!IsValid(entry) || !seen.Add(entry.DeviceId))
                return false;
        }
        return true;
    }

    /// <summary>配對時檢查伺服器給的「自己的那一筆」：deviceId 與公鑰都必須是自己的、沒有撤銷</summary>
    public static bool IsOwnRecord(DeviceRegistryEntry entry, string deviceId, string devicePubKey) =>
        entry.DeviceId == deviceId && entry.DevicePubKey == devicePubKey && entry.RevokedAt is null;
}
