using Myriagent.Security;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>已移除的電腦＝已撤銷（目錄有 device.revoked）而且不在名冊；名冊沒有這台自己時不判斷（設計 14.22）</summary>
public sealed class DeviceRemovalRulesTests
{
    private const long At = 1791570000000;

    private readonly TestDevice self = TestDevice.Create("OFFICE-PC");
    private readonly TestDevice home = TestDevice.Create("HOME-PC");
    private readonly TestDevice old = TestDevice.Create("OLD-PC");

    private Dictionary<string, DeviceRevokedPayload> Revoked(params TestDevice[] devices) =>
        devices.ToDictionary(d => d.DeviceId, d => new DeviceRevokedPayload(d.DeviceId, At, DeviceRegistryRules.RevokedRetired));

    [Fact]
    public void 已撤銷而且不在名冊的是已移除()
    {
        DeviceRemovalRules.Removed(self.DeviceId, [self.Entry(), home.Entry()], Revoked(old)).Should().BeEquivalentTo([old.DeviceId]);
    }

    [Fact]
    public void 已撤銷但名冊還列它_只是撤銷()
    {
        DeviceRemovalRules.Removed(self.DeviceId, [self.Entry(), old.Entry(At, DeviceRegistryRules.RevokedRetired)], Revoked(old)).Should().BeEmpty();
    }

    [Fact]
    public void 不在名冊但沒撤銷_不算()
    {
        // 還沒拉到 device.revoked、或伺服器還原到配對之前的備份：寧可照舊列著
        DeviceRemovalRules.Removed(self.DeviceId, [self.Entry()], Revoked()).Should().BeEmpty();
    }

    [Fact]
    public void 名冊沒有這台自己_一律不判斷()
    {
        DeviceRemovalRules.Removed(self.DeviceId, [], Revoked(old)).Should().BeEmpty("還沒拿到名冊");
        DeviceRemovalRules.Removed(self.DeviceId, [home.Entry()], Revoked(old)).Should().BeEmpty("不是完整的名冊");
    }

    [Fact]
    public void 這台自己永遠不算()
    {
        DeviceRemovalRules.Removed(self.DeviceId, [self.Entry()], Revoked(self, old)).Should().BeEquivalentTo([old.DeviceId]);
    }

    [Fact]
    public void 名冊裡的null略過()
    {
        DeviceRemovalRules.Removed(self.DeviceId, [null, self.Entry()], Revoked(old)).Should().BeEquivalentTo([old.DeviceId]);
    }
}
