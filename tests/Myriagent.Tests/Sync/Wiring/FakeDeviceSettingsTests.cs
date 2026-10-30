using System.Reflection;
using Myriagent.Core;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>
/// tools/fake-device 的資料根之後也會給真的 Myriagent.exe 用（--data）：settings.json 要把每一種「已補過本機預設帳戶」都標成 true，
/// 否則真的 app 啟動時建立指向這台電腦真正登入的系統預設帳戶並執行真的 CLI（合併 Grok 時漏了 GrokAccountAdded）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class FakeDeviceSettingsTests
{
    private static IReadOnlyList<PropertyInfo> AccountAddedFlags() =>
        typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(bool) && p.Name.EndsWith("AccountAdded", StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void 標記每一種本機預設帳戶都已補過()
    {
        var flags = AccountAddedFlags();
        flags.Select(p => p.Name).Should().Contain(["AntigravityAccountAdded", "GrokAccountAdded"], "陽性對照：反射找得到既有的旗標");
        var settings = new AppSettings();

        DeviceRuntime.MarkLocalDefaultsAdded(settings);

        flags.Where(p => !(bool)p.GetValue(settings)!).Select(p => p.Name).Should().BeEmpty("新的代理種類加了 XxxAccountAdded 時，fake-device 也要標記");
    }

    [Fact]
    public async Task 存檔模式寫出的settings_json_全部是true()
    {
        using var temp = new TempDirectory("fake-device-settings");
        using (new AppPathsScope(temp.Root))
        {
            await using (await DeviceRuntime.StartAsync(new DeviceRuntimeOptions { DataRoot = temp.Root }))
            {
            }

            var json = JObject.Parse(File.ReadAllText(AppPaths.SettingsFile));
            foreach (var flag in AccountAddedFlags())
                json.Value<bool?>(flag.Name).Should().BeTrue($"{flag.Name}：真的 app 用這個資料根時不補這種本機預設帳戶");
            json.Value<bool?>("AutoUpdateCli").Should().BeFalse();
        }
    }
}
