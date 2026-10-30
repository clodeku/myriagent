using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Pairing;

/// <summary>配對測試共用的資料</summary>
internal static class PairingTestData
{
    /// <summary>測試建的 CNG 金鑰一律用這個前綴（finally 刪除；萬一留下來也認得出是測試的）</summary>
    public const string TestKeyPrefix = "Myriagent.Test.";

    public const string Server = "https://hub.test";

    public static string NewSpki()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return P256KeyRules.ExportSpki(key);
    }

    public static string NewToken() => SyncProtocol.DeviceTokenPrefix + Base64UrlRules.Encode(RandomNumberGenerator.GetBytes(32));

    public static DeviceFileData Device(
        string? deviceId = null,
        string? keyName = null,
        string keyStorage = PairingRules.KeyStorageSoftware,
        string? devicePubKey = null,
        string? hubSpki = null,
        string? token = null) =>
        new(
            DeviceFileData.CurrentVersion,
            deviceId ?? SyncIds.NewDeviceId(),
            "OFFICE-PC",
            Server,
            1791500000000,
            DeviceSecrets.Protect(token ?? NewToken()),
            keyName ?? DeviceKey.NewKeyName(TestKeyPrefix),
            keyStorage,
            devicePubKey ?? NewSpki(),
            hubSpki ?? NewSpki());

    public static PairTokenResponse Approval(string deviceId, string devicePubKey, string? token = null, string? hubSpki = null, IReadOnlyList<DeviceRegistryEntry>? registry = null) =>
        new(
            deviceId,
            token ?? NewToken(),
            hubSpki ?? NewSpki(),
            new ServerInstance("instance-1", 0),
            new TrustBundle(1, registry ?? [new DeviceRegistryEntry(deviceId, "OFFICE-PC", devicePubKey, 1791500000000)]));

    public static PairStartResponse Start(int interval = 2, int expiresIn = 600) =>
        new(PairingRules.NewDeviceCode(), "BCDF-GHJK", Server + "/#pair", Server + "/#pair=BCDF-GHJK", expiresIn, interval);
}
