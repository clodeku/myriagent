using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Myriagent.Sync;

namespace Myriagent.Security;

/// <summary>裝置對 HelloResponse 裡伺服器身分的判斷</summary>
internal enum HubIdentityOutcome
{
    /// <summary>是釘選的身分金鑰、簽章驗得過</summary>
    Ok,

    /// <summary>釘選的金鑰簽過輪換聲明、新金鑰的簽章驗得過：改釘新的</summary>
    Rotated,

    /// <summary>
    /// 身分金鑰換了、沒有可信的輪換聲明（伺服器重建、或離線期間輪換了兩次以上），但對方確實持有新金鑰：
    /// 停止同步，請使用者把新指紋（<see cref="P256KeyRules.AnchorFingerprint"/>，16 字）與**伺服器主控台**
    /// （在 Hub 主機上執行 <c>MyriagentHub.exe admin status</c>）印出的比對後才改釘。網頁不能當依據：DNS 被劫持、
    /// 中間人又拿到合法憑證時，瀏覽器打開的 hub.example.com 也是對方的，網頁上的指紋一定「相符」
    /// </summary>
    NeedsLocalConfirmation,

    /// <summary>簽章驗不過（中間人、重放、格式錯誤）：中斷連線</summary>
    Invalid,
}

/// <summary>
/// 裝置 hub 的 Hello／Prove 雙向簽章（設計 4.2、SEC-09、SEC-10）：
/// <list type="bullet">
/// <item>伺服器以身分金鑰簽 <c>myriagent-hub-hello-v1\n{deviceId}\n{nonceD}\n{nonceS}\n{observedSpki}\n{serverTime}</c>：
/// 裝置以配對時釘選的公鑰驗證，而且簽進去的 observedSpki 必須是裝置自己在 TLS 交握看到的——拿到合法憑證的中間人冒充不了伺服器。</item>
/// <item>裝置以裝置金鑰簽 <c>myriagent-device-hello-v1\n{deviceId}\n{nonceS}\n{nonceD}\n{observedSpki}</c>：偷到 chd_ 權杖但沒有私鑰過不了 Prove；
/// 簽進 observedSpki，中間人也不能把裝置的簽章轉送給真伺服器（真伺服器看到的 SPKI 是自己的，不是中間人的）。</item>
/// </list>
/// 兩把都是 ECDSA P-256、P1363 簽章（<see cref="P256KeyRules"/>）。nonce 是 32 位元組亂數的 base64url。
/// </summary>
internal static class HandshakeRules
{
    public const string HubHelloPurpose = "myriagent-hub-hello-v1";

    public const string DeviceHelloPurpose = "myriagent-device-hello-v1";

    public const string HubRotationPurpose = "myriagent-hub-rotate-v1";

    public const int NonceBytes = 32;

    /// <summary>伺服器時間與本機差超過它就在狀態列警告「請同步 Windows 時間」並記錄時差（不中斷，設計 10.4）；排序從不看時鐘</summary>
    public const long ClockWarningMs = 2 * 60 * 1000;

    public static string NewNonce() => Base64UrlRules.Encode(RandomNumberGenerator.GetBytes(NonceBytes));

    public static bool IsNonce(string? value) => Base64UrlRules.TryDecode(value, out var bytes, NonceBytes) && bytes.Length == NonceBytes;

    /// <summary>葉憑證公鑰的 SPKI SHA-256（base64url）：裝置在 TLS 交握看到的、伺服器自己目前或前一張憑證的</summary>
    public static string SpkiSha256(X509Certificate2 certificate) =>
        Base64UrlRules.Encode(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    public static bool IsSpkiSha256(string? value) => Base64UrlRules.TryDecode(value, out var bytes, 32) && bytes.Length == 32;

    public static byte[] HubSignedData(string deviceId, string nonceD, string nonceS, string observedSpki, long serverTime) =>
        Encoding.UTF8.GetBytes(string.Join('\n', HubHelloPurpose, deviceId, nonceD, nonceS, observedSpki, serverTime.ToString(CultureInfo.InvariantCulture)));

    public static byte[] DeviceSignedData(string deviceId, string nonceS, string nonceD, string observedSpki) =>
        Encoding.UTF8.GetBytes(string.Join('\n', DeviceHelloPurpose, deviceId, nonceS, nonceD, observedSpki));

    /// <summary>裝置端：驗伺服器的 hubSig（釘選的身分公鑰）</summary>
    public static bool VerifyHub(string pinnedHubSpki, string deviceId, string nonceD, string nonceS, string observedSpki, long serverTime, string? hubSig) =>
        P256KeyRules.Verify(pinnedHubSpki, HubSignedData(deviceId, nonceD, nonceS, observedSpki, serverTime), hubSig);

    /// <summary>伺服器端：驗裝置的 deviceSig（devices.device_pubkey）</summary>
    public static bool VerifyDevice(string devicePubKey, string deviceId, string nonceS, string nonceD, string observedSpki, string? deviceSig) =>
        P256KeyRules.Verify(devicePubKey, DeviceSignedData(deviceId, nonceS, nonceD, observedSpki), deviceSig);

    public static bool IsClockSkewed(long serverTime, long localTime)
    {
        // Int128：兩個極端值相減不會溢位（Math.Abs(long.MinValue) 會丟例外）
        var difference = (Int128)serverTime - localTime;
        return difference > ClockWarningMs || difference < -ClockWarningMs;
    }

    public static byte[] RotationSignedData(string oldSpki, string newSpki) =>
        Encoding.UTF8.GetBytes(string.Join('\n', HubRotationPurpose, oldSpki, newSpki));

    /// <summary>
    /// 裝置端：判斷 HelloResponse 的伺服器身分（簽章一律以裝置自己在 TLS 看到的 SPKI 雜湊、自己送出的 nonceD 驗）。
    /// 回傳的 pin 是之後要釘的公鑰（Ok 是原本的、Rotated 是新的、NeedsLocalConfirmation 是待確認的；Invalid 為 null）。
    /// </summary>
    public static (HubIdentityOutcome Outcome, string? Pin) CheckHubIdentity(string pinnedHubSpki, string deviceId, string nonceD, string observedSpki, HelloResponse response)
    {
        var claimed = response.HubIdentitySpki;
        if (!IsNonce(response.NonceS) || claimed is null)
            return (HubIdentityOutcome.Invalid, null);
        bool SignedBy(string spki) => VerifyHub(spki, deviceId, nonceD, response.NonceS, observedSpki, response.ServerTime, response.HubSig);

        if (claimed == pinnedHubSpki)
            return SignedBy(pinnedHubSpki) ? (HubIdentityOutcome.Ok, pinnedHubSpki) : (HubIdentityOutcome.Invalid, null);
        if (response.Rotation is { } rotation && rotation.NewSpki == claimed
            && AcceptRotation(pinnedHubSpki, rotation.OldSpki, rotation.NewSpki, rotation.Sig) is { } rotated)
        {
            return SignedBy(rotated) ? (HubIdentityOutcome.Rotated, rotated) : (HubIdentityOutcome.Invalid, null);
        }

        return P256KeyRules.IsValidSpki(claimed) && SignedBy(claimed) ? (HubIdentityOutcome.NeedsLocalConfirmation, claimed) : (HubIdentityOutcome.Invalid, null);
    }

    /// <summary>
    /// 裝置端：伺服器身分金鑰輪換。釘的是聲明裡的舊金鑰、新金鑰是 P-256、舊金鑰的簽章驗得過，才回傳要改釘的新公鑰；否則 null（不改釘）
    /// </summary>
    public static string? AcceptRotation(string pinnedHubSpki, string? oldSpki, string? newSpki, string? sig)
    {
        if (oldSpki != pinnedHubSpki || newSpki is null || newSpki == pinnedHubSpki || !P256KeyRules.IsValidSpki(newSpki))
            return null;
        return P256KeyRules.Verify(pinnedHubSpki, RotationSignedData(oldSpki, newSpki), sig) ? newSpki : null;
    }
}
