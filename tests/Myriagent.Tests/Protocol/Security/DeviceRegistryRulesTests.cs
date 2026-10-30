using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Security;

/// <summary>
/// 裝置名冊（設計 14.4）：伺服器是授權者，名冊不另外簽署；裝置只檢查格式（deviceId、清理過的名稱、P-256 公鑰、撤銷欄位一致、不重複）
/// 與配對時「自己的那一筆」。
/// </summary>
public sealed class DeviceRegistryRulesTests
{
    private const long T0 = 1_791_580_000_000;

    [Fact]
    public void 格式正確的一筆_合格_自己的紀錄要deviceId與公鑰都相同而且沒有撤銷()
    {
        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var entry = Entry(device, "OFFICE-PC");

        DeviceRegistryRules.IsValid(entry).Should().BeTrue();
        DeviceRegistryRules.IsOwnRecord(entry, entry.DeviceId, entry.DevicePubKey).Should().BeTrue();
        DeviceRegistryRules.IsOwnRecord(entry, entry.DeviceId, Entry(ECDsa.Create(ECCurve.NamedCurves.nistP256), "x").DevicePubKey).Should().BeFalse("伺服器給的公鑰不是自己的");
        DeviceRegistryRules.IsOwnRecord(entry, SyncIds.NewDeviceId(), entry.DevicePubKey).Should().BeFalse("deviceId 不是自己的");
        DeviceRegistryRules.IsOwnRecord(entry with { RevokedAt = T0, RevokedReason = DeviceRegistryRules.RevokedRetired }, entry.DeviceId, entry.DevicePubKey)
            .Should().BeFalse("被撤銷的不能當成自己的有效紀錄");
    }

    [Fact]
    public void 撤銷時間與原因要同時有或同時沒有_原因只認retired或compromised()
    {
        var entry = Entry(ECDsa.Create(ECCurve.NamedCurves.nistP256), "OLD-PC");

        DeviceRegistryRules.IsValid(entry with { RevokedAt = T0, RevokedReason = DeviceRegistryRules.RevokedRetired }).Should().BeTrue();
        DeviceRegistryRules.IsValid(entry with { RevokedAt = T0, RevokedReason = DeviceRegistryRules.RevokedCompromised }).Should().BeTrue();
        DeviceRegistryRules.IsValid(entry with { RevokedAt = T0 }).Should().BeFalse("撤銷了卻沒有原因");
        DeviceRegistryRules.IsValid(entry with { RevokedReason = DeviceRegistryRules.RevokedRetired }).Should().BeFalse("沒有撤銷卻有原因");
        DeviceRegistryRules.IsValid(entry with { RevokedAt = T0, RevokedReason = "lost" }).Should().BeFalse();
        DeviceRegistryRules.IsRevokedReason("retired").Should().BeTrue();
        DeviceRegistryRules.IsRevokedReason("Retired").Should().BeFalse();
    }

    [Fact]
    public void 一筆的格式_公鑰不是P256_名稱沒清理過或太長_deviceId格式_建立時間負數_一律不合格()
    {
        var good = Entry(ECDsa.Create(ECCurve.NamedCurves.nistP256), "OFFICE-PC");
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);

        DeviceRegistryRules.IsValid(good with { DevicePubKey = Base64UrlRules.Encode(p384.ExportSubjectPublicKeyInfo()) }).Should().BeFalse("P-384");
        DeviceRegistryRules.IsValid(good with { DevicePubKey = Base64UrlRules.Encode(rsa.ExportSubjectPublicKeyInfo()) }).Should().BeFalse("RSA");
        DeviceRegistryRules.IsValid(good with { DevicePubKey = good.DevicePubKey + "AA" }).Should().BeFalse("SPKI 後面多了位元組");
        Base64UrlRules.TryDecode(good.DevicePubKey, out var spki).Should().BeTrue();
        spki[^1] ^= 0x01;
        DeviceRegistryRules.IsValid(good with { DevicePubKey = Base64UrlRules.Encode(spki) }).Should().BeFalse("曲線外的點（CNG 丟 PlatformNotSupportedException，不可讓檢查崩潰）");
        HandshakeRules.VerifyDevice(Base64UrlRules.Encode(spki), "d_x", "n", "n", "s", Base64UrlRules.Encode(new byte[64])).Should().BeFalse();
        DeviceRegistryRules.IsValid(good with { Name = new string('x', 61) }).Should().BeFalse("名稱超過 60 字");
        DeviceRegistryRules.IsValid(good with { Name = "" }).Should().BeFalse();
        DeviceRegistryRules.IsValid(good with { Name = " OFFICE-PC" }).Should().BeFalse("名稱要已經是伺服器清理過的樣子");
        DeviceRegistryRules.IsValid(good with { DeviceId = "d_short" }).Should().BeFalse();
        DeviceRegistryRules.IsValid(good with { CreatedAt = -1 }).Should().BeFalse();
        DeviceRegistryRules.IsValid((DeviceRegistryEntry?)null).Should().BeFalse();
    }

    [Fact]
    public void 整份名冊_空的合格_重複的deviceId_超過上限_null_有一筆不合格就整份不採用()
    {
        var a = Entry(ECDsa.Create(ECCurve.NamedCurves.nistP256), "PC-A");
        var b = Entry(ECDsa.Create(ECCurve.NamedCurves.nistP256), "PC-B");

        DeviceRegistryRules.IsValid(new List<DeviceRegistryEntry>()).Should().BeTrue("還沒有配對任何裝置");
        DeviceRegistryRules.IsValid([a, b]).Should().BeTrue();
        DeviceRegistryRules.IsValid([a, b with { DeviceId = a.DeviceId }]).Should().BeFalse("重複的 deviceId");
        DeviceRegistryRules.IsValid([a, b with { Name = "PC‮B" }]).Should().BeFalse();
        DeviceRegistryRules.IsValid((IReadOnlyList<DeviceRegistryEntry>?)null).Should().BeFalse();

        var many = Enumerable.Range(0, DeviceRegistryRules.MaxDevices + 1).Select(i => a with { DeviceId = SyncIds.NewDeviceId(), Name = $"PC-{i}" }).ToList();
        DeviceRegistryRules.IsValid(many).Should().BeFalse();
        DeviceRegistryRules.IsValid(many.Take(DeviceRegistryRules.MaxDevices).ToList()).Should().BeTrue();
    }

    private static DeviceRegistryEntry Entry(ECDsa key, string name) => new(SyncIds.NewDeviceId(), name, P256KeyRules.ExportSpki(key), T0);
}
