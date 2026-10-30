using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Rules;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.Pairing;

public sealed class TrustStoreTests : IDisposable
{
    private readonly TempDirectory temp = new("trust-store");
    private readonly TestDevice self = TestDevice.Create("OFFICE-PC");
    private readonly TestDevice other = TestDevice.Create("HOME-PC");

    public TrustStoreTests()
    {
        Paths = new SyncPaths(temp.Combine("sync"));
        Data = PairingTestData.Device(deviceId: self.DeviceId, devicePubKey: self.PubKey);
        DeviceFile.Save(Paths.DeviceFile, Data);
        Store = SyncStore.Create(Paths.DatabaseFile, self.DeviceId, 1791500000000, new ServerInstance("instance-1", 0), new TrustBundle(5, [self.Entry(), other.Entry()]));
    }

    private SyncPaths Paths { get; }

    private DeviceFileData Data { get; }

    private SyncStore Store { get; }

    public void Dispose()
    {
        Store.Dispose();
        foreach (var file in Directory.EnumerateFiles(Paths.Directory))
            File.SetAttributes(file, FileAttributes.Normal);
        temp.Dispose();
    }

    [Fact]
    public void TryRepin_device_json先寫成功才換記憶體()
    {
        var trust = new TrustStore(new DeviceState(Paths, Data), Store);
        var next = PairingTestData.NewSpki();

        trust.TryRepin(next).Should().BeTrue();

        trust.PinnedHubSpki.Should().Be(next);
        trust.HubFingerprint.Should().Be(P256KeyRules.AnchorFingerprint(next));
        DeviceFile.Load(Paths.DeviceFile).Data!.HubIdentitySpki.Should().Be(next, "改釘已經寫進 device.json");
        trust.TryRepin("不是公鑰").Should().BeFalse();
        trust.PinnedHubSpki.Should().Be(next);
    }

    [Fact]
    public void 寫入失敗時保持舊pin()
    {
        var trust = new TrustStore(new DeviceState(Paths, Data), Store);
        File.SetAttributes(Paths.DeviceFile, FileAttributes.ReadOnly);

        trust.TryRepin(PairingTestData.NewSpki()).Should().BeFalse();

        trust.PinnedHubSpki.Should().Be(Data.HubIdentitySpki);
        File.SetAttributes(Paths.DeviceFile, FileAttributes.Normal);
        DeviceFile.Load(Paths.DeviceFile).Data!.HubIdentitySpki.Should().Be(Data.HubIdentitySpki);
        File.Exists(Paths.DeviceFile + ".tmp").Should().BeFalse("寫失敗時刪掉暫存檔");
    }

    [Fact]
    public void ApplyBundle_rev較舊_仍整份取代並存檔()
    {
        var trust = new TrustStore(new DeviceState(Paths, Data), Store);
        trust.RegistryRev.Should().Be(5);

        trust.ApplyBundle(new TrustBundle(3, [self.Entry()])).Should().Be(TrustCheck.Accept);

        trust.RegistryRev.Should().Be(3);
        trust.Registry.Should().ContainSingle().Which.DeviceId.Should().Be(self.DeviceId);
        var (rev, entries) = Store.LoadRegistry();
        rev.Should().Be(3);
        entries.Select(e => e.DeviceId).Should().Equal(self.DeviceId);
        new TrustStore(new DeviceState(Paths, Data), Store).RegistryRev.Should().Be(3, "重開後從 sync.db 讀回");
    }

    [Fact]
    public void ApplyBundle_不合格_沿用上一份()
    {
        var trust = new TrustStore(new DeviceState(Paths, Data), Store);
        var broken = self.Entry() with { Name = " 有空白 " };

        trust.ApplyBundle(new TrustBundle(9, [broken, other.Entry()])).Should().Be(TrustCheck.Invalid);
        trust.ApplyBundle(new TrustBundle(9, null)).Should().Be(TrustCheck.Unchanged);
        trust.ApplyBundle(new TrustBundle(9, [other.Entry()])).Should().Be(TrustCheck.NotInRegistry);
        trust.ApplyBundle(new TrustBundle(9, [self.Entry(1791580000000, DeviceRegistryRules.RevokedRetired)])).Should().Be(TrustCheck.SelfRevoked);

        trust.RegistryRev.Should().Be(5);
        trust.Registry.Should().HaveCount(2);
        Store.LoadRegistry().Rev.Should().Be(5, "不合格、停止的情況都不存");
    }
}

public sealed class DeviceStateTests : IDisposable
{
    private readonly TempDirectory temp = new("device-state");

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(temp.Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        temp.Dispose();
    }

    [Fact]
    public void TryUpdate_存檔失敗_記憶體不變()
    {
        var paths = new SyncPaths(temp.Combine("sync"));
        var data = PairingTestData.Device();
        DeviceFile.Save(paths.DeviceFile, data);
        var state = new DeviceState(paths, data);
        File.SetAttributes(paths.DeviceFile, FileAttributes.ReadOnly);

        state.TryUpdate(d => d with { ProtectedDeviceToken = DeviceSecrets.Protect(PairingTestData.NewToken()) }).Should().BeFalse();

        state.Current.Should().Be(data);
        File.SetAttributes(paths.DeviceFile, FileAttributes.Normal);
        state.TryUpdate(d => d with { DeviceName = "改過" }).Should().BeTrue();
        state.Current.DeviceName.Should().Be("改過");
        DeviceFile.Load(paths.DeviceFile).Data!.DeviceName.Should().Be("改過");
    }
}
