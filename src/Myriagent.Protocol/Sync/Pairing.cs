using System.Security.Cryptography;
using System.Text;
using Myriagent.Security;

namespace Myriagent.Sync;

// 裝置配對（仿 RFC 8628 裝置授權，設計 6.3、8.2.4；docs/sync-protocol.md 第 2 節）。
// 裝置：POST /api/v1/pair/start → 顯示配對碼與裝置指紋 → 每 interval 秒 POST /api/v1/pair/token，直到擁有者在任何已登入的瀏覽器核准
// （密碼登入、近期驗證過；伺服器是授權者，設計 14.4）。這兩個端點是匿名的（速率限制）；配對當下靠 WebPKI，之後靠釘選的伺服器身分金鑰。

/// <summary>開始配對。<c>devicePubKey</c> 是裝置金鑰（CNG、不可匯出的 ECDSA P-256）的 SPKI base64url；<c>keyStorage</c> 是 <c>tpm</c> 或 <c>software</c></summary>
internal sealed record PairStartRequest(
    string DeviceName,
    string? MachineName,
    string? WindowsUser,
    string DevicePubKey,
    string KeyStorage,
    string AppVersion,
    int ProtocolVersion);

/// <summary>
/// <c>deviceCode</c> 只有裝置知道（256 位元，伺服器只存 SHA-256）；<c>userCode</c> 給人輸入（8 字、XXXX-XXXX）；
/// <c>verificationUriComplete</c> 已帶配對碼（QR 碼用）。<c>expiresIn</c>、<c>interval</c> 是秒。
/// </summary>
internal sealed record PairStartResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresIn,
    int Interval);

internal sealed record PairTokenRequest(string DeviceCode);

/// <summary>
/// 核准後換到的身分（deviceCode 只能換一次）：deviceId、裝置權杖（chd_，只在這一次出現）、伺服器身分公鑰（首次信任後釘選）與整份裝置名冊。
/// 裝置檢查通過才寫進 device.json 與 sync.db：名冊格式正確、裡面自己的那一筆是自己的公鑰（<see cref="DeviceRegistryRules.IsOwnRecord"/>）。
/// </summary>
internal sealed record PairTokenResponse(
    string DeviceId,
    string DeviceToken,
    string HubIdentitySpki,
    ServerInstance Server,
    TrustBundle Trust);

/// <summary>配對端點的錯誤（HTTP 400 的 JSON 本文；<c>slow_down</c> 時帶新的間隔）</summary>
internal sealed record PairError(string Error, int? Interval = null);

/// <summary>RFC 8628 的錯誤代碼＋本系統的上限</summary>
internal static class PairingErrors
{
    public const string AuthorizationPending = "authorization_pending";
    public const string SlowDown = "slow_down";
    public const string ExpiredToken = "expired_token";
    public const string AccessDenied = "access_denied";

    /// <summary>deviceCode 不存在或已經換過權杖</summary>
    public const string InvalidGrant = "invalid_grant";

    /// <summary>全域待核准已有 <see cref="PairingRules.MaxPending"/> 筆</summary>
    public const string TooManyPending = "too_many_pending";

    public const string InvalidRequest = "invalid_request";

    /// <summary>核准時：配對過的裝置（含已撤銷、不含已移除的）已達名冊上限 <see cref="Myriagent.Security.DeviceRegistryRules.MaxDevices"/>（409，只給擁有者的網頁）</summary>
    public const string TooManyDevices = "too_many_devices";
}

/// <summary>配對的純函式規則（伺服器與桌面共用）</summary>
internal static class PairingRules
{
    /// <summary>RFC 8628 建議的無母音字母（20 個），8 字約 34.6 位元；錯了要重新開始，10 分鐘到期</summary>
    public const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";

    public const int UserCodeLength = 8;

    public const int DeviceCodeBytes = 32;

    public const int ExpiresInSeconds = 600;

    public const int IntervalSeconds = 2;

    public const int SlowDownIncrementSeconds = 5;

    public const int MaxPending = 5;

    public const int MaxDeviceNameLength = DeviceRegistryRules.MaxNameLength;

    public const string KeyStorageTpm = "tpm";

    public const string KeyStorageSoftware = "software";

    public static string NewUserCode()
    {
        Span<char> chars = stackalloc char[UserCodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        return Format(chars);
    }

    /// <summary>人輸入的配對碼正規化：去掉空白與連字號、轉大寫；不是 8 個合法字母就回 null</summary>
    public static string? NormalizeUserCode(string? input)
    {
        if (input is null || input.Length > 32)
            return null;
        var builder = new StringBuilder(UserCodeLength);
        foreach (var c in input)
        {
            if (c is '-' or ' ')
                continue;
            var upper = char.ToUpperInvariant(c);
            if (UserCodeAlphabet.IndexOf(upper) < 0 || builder.Length == UserCodeLength)
                return null;
            builder.Append(upper);
        }

        return builder.Length == UserCodeLength ? Format(builder.ToString()) : null;
    }

    public static string NewDeviceCode() => Base64UrlRules.Encode(RandomNumberGenerator.GetBytes(DeviceCodeBytes));

    public static bool IsDeviceCode(string? value) => Base64UrlRules.TryDecode(value, out var bytes, DeviceCodeBytes) && bytes.Length == DeviceCodeBytes;

    /// <summary>收到 slow_down 就把間隔加 5 秒（RFC 8628 3.5）；伺服器有給新的間隔就用較大的</summary>
    public static int NextInterval(int current, PairError error) =>
        error.Error == PairingErrors.SlowDown
            ? Math.Max(current + SlowDownIncrementSeconds, error.Interval ?? 0)
            : current;

    /// <summary>輪詢太快（距上次不到目前的間隔）：伺服器回 slow_down</summary>
    public static bool IsTooFast(long? lastPollAt, long now, int intervalSeconds) =>
        lastPollAt is { } last && now - last < intervalSeconds * 1000L;

    /// <summary>裝置名稱：去掉控制與格式字元、去頭尾空白後 1～60 字（<see cref="LabelRules"/>）；不合格回 null（顯示時一律 textContent）</summary>
    public static string? CleanDeviceName(string? name) => LabelRules.Clean(name, MaxDeviceNameLength);

    public static bool IsKeyStorage(string? value) => value is KeyStorageTpm or KeyStorageSoftware;

    private static string Format(ReadOnlySpan<char> code) => string.Concat(code[..4], "-", code[4..]);
}
