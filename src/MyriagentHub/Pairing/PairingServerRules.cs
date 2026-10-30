using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Sync;

namespace MyriagentHub.Pairing;

/// <summary>配對碼輪詢的判斷（sync-protocol 2.1 第 4 步）</summary>
internal enum PollVerdict
{
    InvalidGrant,
    AccessDenied,
    Expired,
    SlowDown,
    Pending,
    Approved,
}

/// <summary>配對的伺服器端純函式規則（裝置與伺服器共用的在 Protocol 的 <see cref="PairingRules"/>、<see cref="DeviceRegistryRules"/>）</summary>
internal static class PairingServerRules
{
    /// <summary>電腦名稱、Windows 使用者名稱的上限（只供核准頁顯示，一律 textContent）</summary>
    public const int MaxTextLength = 128;

    /// <summary>核准之後裝置還沒換權杖時，配對碼到期後再寬限多久（核准落在到期前幾秒的情況）</summary>
    public const long ConsumeGraceMs = 5 * 60_000;

    /// <summary>結束（到期、拒絕、已換權杖）的配對請求保留多久再刪</summary>
    public const long RetentionMs = 24L * 60 * 60 * 1000;

    /// <summary>
    /// 開始配對的格式；null＝合格，否則是錯誤代碼（版本不支援回 protocol，桌面提示更新 app）。
    /// 協定範圍以 <see cref="SyncProtocol.IsSupported"/> 檢查（Hello 的 <see cref="MyriagentHub.Realtime.DeviceHubRules.Gate"/> 依同一組常數另外判斷）；
    /// 配對不開放只更新（D10）：app 太舊照舊回 protocol，先更新再配對
    /// </summary>
    public static string? CheckStart(PairStartRequest? request, string minAppVersion)
    {
        if (request is null)
            return PairingErrors.InvalidRequest;
        if (!SyncProtocol.IsSupported(request.ProtocolVersion) || !AppVersionRules.IsAtLeast(request.AppVersion, minAppVersion))
            return SyncErrorCodes.Protocol;
        if (PairingRules.CleanDeviceName(request.DeviceName) is null
            || !P256KeyRules.IsValidSpki(request.DevicePubKey)
            || !PairingRules.IsKeyStorage(request.KeyStorage)
            || !CatalogRules.IsOptionalText(request.MachineName, MaxTextLength)
            || !CatalogRules.IsOptionalText(request.WindowsUser, MaxTextLength))
            return PairingErrors.InvalidRequest;
        return null;
    }

    public static bool IsPending(PairingRequestRecord record, long now) =>
        record.ExpiresAt > now && record.ApprovedAt is null && record.DeniedAt is null && record.ConsumedAt is null;

    public static PollVerdict Poll(PairingRequestRecord? record, long now)
    {
        if (record is null || record.ConsumedAt is not null)
            return PollVerdict.InvalidGrant;
        if (record.DeniedAt is not null)
            return PollVerdict.AccessDenied;
        if (record.ApprovedAt is not null)
            return now <= record.ExpiresAt + ConsumeGraceMs ? PollVerdict.Approved : PollVerdict.Expired;
        if (record.ExpiresAt <= now)
            return PollVerdict.Expired;
        return PairingRules.IsTooFast(record.LastPollAt, now, record.IntervalSeconds) ? PollVerdict.SlowDown : PollVerdict.Pending;
    }
}
