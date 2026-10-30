using Myriagent.Security;

namespace Myriagent.Sync;

/// <summary>收到名冊（GetTrust、配對回應、TrustChanged 之後拉的）的判斷</summary>
internal enum TrustCheck
{
    /// <summary>格式合格、有自己而且沒被撤銷：整份取代（不論版本大小）</summary>
    Accept,

    /// <summary>registry 是 null（伺服器說版本相同）：沿用</summary>
    Unchanged,

    /// <summary>格式不合格（含自己那一筆的公鑰不是自己的）：沿用上一份（sync-protocol 7.1）</summary>
    Invalid,

    /// <summary>自己那一筆被標成撤銷：停止同步</summary>
    SelfRevoked,

    /// <summary>名冊裡沒有自己：停止同步</summary>
    NotInRegistry,
}

/// <summary>
/// 裝置名冊的裝置端規則（設計 14.4：伺服器授權）：裝置只檢查格式與自己那一筆；版本只要不同就整份取代
/// （伺服器還原到舊備份時版本會變小，也照樣取代，sync-protocol 7.2）
/// </summary>
internal static class TrustRules
{
    public static TrustCheck CheckBundle(TrustBundle? bundle, string selfDeviceId, string selfPubKey)
    {
        if (bundle is null)
            return TrustCheck.Invalid;
        if (bundle.Registry is not { } registry)
            return TrustCheck.Unchanged;
        if (!DeviceRegistryRules.IsValid(registry))
            return TrustCheck.Invalid;
        var self = registry.FirstOrDefault(e => e.DeviceId == selfDeviceId);
        if (self is null)
            return TrustCheck.NotInRegistry;
        if (self.RevokedAt is not null)
            return TrustCheck.SelfRevoked;
        return DeviceRegistryRules.IsOwnRecord(self, selfDeviceId, selfPubKey) ? TrustCheck.Accept : TrustCheck.Invalid;
    }

    public static bool IsSelf(DeviceRevokedPayload? p, string selfDeviceId) => p is not null && p.DeviceId == selfDeviceId;
}
