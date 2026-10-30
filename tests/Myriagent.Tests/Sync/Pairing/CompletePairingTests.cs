using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.Pairing;

/// <summary>
/// 配對提交（<see cref="SyncEngineLoader.CompletePairing"/>）遇到上一次留下的 sync.db：device.json 不存在或損毀時刪掉重建
/// （協調者把損毀的 device.json 當成未配對、允許重新配對）；device.json 正常時不覆寫
/// </summary>
public sealed class CompletePairingTests : IDisposable
{
    private readonly TempDirectory temp = new("complete-pairing");

    private SyncPaths Paths => new(temp.Combine("sync"));

    public void Dispose() => temp.Dispose();

    private (PairTokenResponse Response, DeviceFileData Device) NewPairing()
    {
        using var signer = new InMemoryDeviceSigner();
        var deviceId = SyncIds.NewDeviceId();
        var response = PairingTestData.Approval(deviceId, signer.PublicSpki);
        return (response, PairingTestData.Device(deviceId, devicePubKey: signer.PublicSpki, hubSpki: response.HubIdentitySpki, token: response.DeviceToken));
    }

    private void OldDatabase(string deviceId)
    {
        Directory.CreateDirectory(Paths.Directory);
        using (SyncStore.Create(Paths.DatabaseFile, deviceId, 1, new ServerInstance("old-instance", 3), new TrustBundle(1, [])))
        {
        }
    }

    [Theory]
    [InlineData("{ 這不是 JSON")]
    [InlineData("[]")]
    [InlineData("""{"version":1,"deviceId":"不合格"}""")]
    public void device_json損毀_刪掉殘留的sync_db_重新配對成功(string corrupt)
    {
        var oldDeviceId = SyncIds.NewDeviceId();
        OldDatabase(oldDeviceId);
        File.WriteAllText(Paths.DeviceFile, corrupt);
        DeviceFile.Load(Paths.DeviceFile).Status.Should().Be(DeviceFileStatus.Corrupt, "前提");
        var (response, device) = NewPairing();

        SyncEngineLoader.CompletePairing(Paths, response, device);

        var (status, saved) = DeviceFile.Load(Paths.DeviceFile);
        status.Should().Be(DeviceFileStatus.Ok, "損毀的內容被這次的 Save 蓋掉");
        saved!.DeviceId.Should().Be(device.DeviceId);
        using var store = SyncStore.Open(Paths.DatabaseFile, device.DeviceId);
        store.GetKv(SyncStoreSchema.DeviceId).Should().Be(device.DeviceId, "sync.db 是新建的，不是上一個裝置身分的");
        store.SeenServer.Should().Be(response.Server);
    }

    [Fact]
    public void device_json不存在_刪掉殘留的sync_db()
    {
        OldDatabase(SyncIds.NewDeviceId());
        var (response, device) = NewPairing();

        SyncEngineLoader.CompletePairing(Paths, response, device);

        using var store = SyncStore.Open(Paths.DatabaseFile, device.DeviceId);
        store.GetKv(SyncStoreSchema.DeviceId).Should().Be(device.DeviceId);
    }

    [Fact]
    public void device_json正常_不覆寫既有的配對()
    {
        var (oldResponse, oldDevice) = NewPairing();
        SyncEngineLoader.CompletePairing(Paths, oldResponse, oldDevice);
        var (response, device) = NewPairing();

        var act = () => SyncEngineLoader.CompletePairing(Paths, response, device);

        act.Should().Throw<IOException>("已配對的 sync.db 不能被另一次配對蓋掉");
        DeviceFile.Load(Paths.DeviceFile).Data!.DeviceId.Should().Be(oldDevice.DeviceId);
        using var store = SyncStore.Open(Paths.DatabaseFile, oldDevice.DeviceId);
        store.GetKv(SyncStoreSchema.DeviceId).Should().Be(oldDevice.DeviceId);
    }
}
