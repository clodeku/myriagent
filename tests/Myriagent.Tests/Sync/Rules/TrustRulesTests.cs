using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>名冊測試用的裝置：亂數 deviceId 與 P-256 公鑰</summary>
internal sealed record TestDevice(string DeviceId, string PubKey, string Name)
{
    public static TestDevice Create(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new TestDevice(SyncIds.NewDeviceId(), P256KeyRules.ExportSpki(key), name);
    }

    public DeviceRegistryEntry Entry(long? revokedAt = null, string? reason = null) => new(DeviceId, Name, PubKey, 1791500000000, revokedAt, reason);
}

public sealed class TrustRulesTests
{
    private readonly TestDevice self = TestDevice.Create("OFFICE-PC");
    private readonly TestDevice other = TestDevice.Create("HOME-PC");

    [Fact]
    public void rev較舊_仍整份取代()
    {
        // 伺服器還原到舊備份時版本會變小：版本只要不同就整份取代（sync-protocol 7.2）
        TrustRules.CheckBundle(new TrustBundle(1, [self.Entry(), other.Entry()]), self.DeviceId, self.PubKey).Should().Be(TrustCheck.Accept);
        TrustRules.CheckBundle(new TrustBundle(0, [self.Entry()]), self.DeviceId, self.PubKey).Should().Be(TrustCheck.Accept);
    }

    [Fact]
    public void registry為null_Unchanged()
    {
        TrustRules.CheckBundle(new TrustBundle(5, null), self.DeviceId, self.PubKey).Should().Be(TrustCheck.Unchanged);
    }

    [Fact]
    public void 名冊不含自己_NotInRegistry()
    {
        TrustRules.CheckBundle(new TrustBundle(5, [other.Entry()]), self.DeviceId, self.PubKey).Should().Be(TrustCheck.NotInRegistry);
        TrustRules.CheckBundle(new TrustBundle(5, []), self.DeviceId, self.PubKey).Should().Be(TrustCheck.NotInRegistry);
    }

    [Fact]
    public void 自己被標撤銷_SelfRevoked()
    {
        TrustRules.CheckBundle(new TrustBundle(5, [self.Entry(1791580000000, DeviceRegistryRules.RevokedCompromised), other.Entry()]), self.DeviceId, self.PubKey)
            .Should().Be(TrustCheck.SelfRevoked);
        TrustRules.IsSelf(new DeviceRevokedPayload(self.DeviceId, 1, "retired"), self.DeviceId).Should().BeTrue();
        TrustRules.IsSelf(new DeviceRevokedPayload(other.DeviceId, 1, "retired"), self.DeviceId).Should().BeFalse();
        TrustRules.IsSelf(null, self.DeviceId).Should().BeFalse();
    }

    [Fact]
    public void 格式錯_Invalid()
    {
        TrustRules.CheckBundle(new TrustBundle(5, [self.Entry(), self.Entry()]), self.DeviceId, self.PubKey).Should().Be(TrustCheck.Invalid, "deviceId 重複");
        TrustRules.CheckBundle(new TrustBundle(5, [self.Entry() with { DevicePubKey = "不是公鑰" }]), self.DeviceId, self.PubKey).Should().Be(TrustCheck.Invalid);
        TrustRules.CheckBundle(new TrustBundle(5, [self.Entry() with { Name = " 頭尾空白 " }]), self.DeviceId, self.PubKey).Should().Be(TrustCheck.Invalid);
        TrustRules.CheckBundle(new TrustBundle(5, [self.Entry(1, null)]), self.DeviceId, self.PubKey).Should().Be(TrustCheck.Invalid, "撤銷時間與原因要同時有");
        TrustRules.CheckBundle(new TrustBundle(5, [self.Entry() with { DevicePubKey = other.PubKey }]), self.DeviceId, self.PubKey)
            .Should().Be(TrustCheck.Invalid, "自己那一筆的公鑰不是自己的");
        TrustRules.CheckBundle(null, self.DeviceId, self.PubKey).Should().Be(TrustCheck.Invalid);
        var tooMany = Enumerable.Range(0, DeviceRegistryRules.MaxDevices).Select(i => TestDevice.Create($"PC-{i}").Entry()).Append(self.Entry()).ToList();
        TrustRules.CheckBundle(new TrustBundle(5, tooMany), self.DeviceId, self.PubKey).Should().Be(TrustCheck.Invalid, "超過 256 台整份不採用");
    }
}
