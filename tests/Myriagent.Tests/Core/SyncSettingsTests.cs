using Myriagent.Core;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Core;

/// <summary>settings.json 的 Sync 區（暫停、遠端操作政策）</summary>
[Collection(AppPathsCollection.Name)]
public sealed class SyncSettingsTests : IDisposable
{
    private readonly TempDirectory temp = new("sync-settings");
    private readonly AppPathsScope scope;

    public SyncSettingsTests() => scope = new AppPathsScope(temp.Root);

    public void Dispose()
    {
        scope.Dispose();
        temp.Dispose();
    }

    [Fact]
    public void 預設Off與false()
    {
        var settings = new AppSettings();

        settings.Sync.Paused.Should().BeFalse();
        settings.Sync.RemotePaused.Should().BeFalse();
        settings.Sync.RemoteControlLevel.Should().Be("Off");
        settings.Sync.AllowRemoteFullAccess.Should().BeFalse();
        AppSettings.Load().Sync.RemoteControlLevel.Should().Be("Off", "沒有設定檔");
    }

    [Fact]
    public void 來回存讀()
    {
        var settings = new AppSettings();
        settings.Sync.Paused = true;
        settings.Sync.RemotePaused = true;
        settings.Sync.RemoteControlLevel = "Control";
        settings.Sync.AllowRemoteFullAccess = true;

        settings.Save();
        var loaded = AppSettings.Load();

        loaded.Sync.Paused.Should().BeTrue();
        loaded.Sync.RemotePaused.Should().BeTrue();
        loaded.Sync.RemoteControlLevel.Should().Be("Control");
        loaded.Sync.AllowRemoteFullAccess.Should().BeTrue();
    }

    [Fact]
    public void 不認得的層級_Off()
    {
        File.WriteAllText(AppPaths.SettingsFile, """{ "Theme": "light", "Sync": { "Paused": true, "RemoteControlLevel": "Admin", "AllowRemoteFullAccess": true } }""");

        var loaded = AppSettings.Load();

        loaded.Sync.RemoteControlLevel.Should().Be("Off");
        loaded.Sync.Paused.Should().BeTrue("其他欄位照讀");
        loaded.Sync.AllowRemoteFullAccess.Should().BeTrue();
        loaded.Theme.Should().Be("light");
        loaded.StartupWarning.Should().BeNull("不是損毀");
    }

    [Fact]
    public void NotifyRemoteActions預設開_舊設定檔沒有也是開_關掉後存讀得回來()
    {
        // 設計 14.11 D8：遠端執行的層級 1、2 動作在本機通知，預設開
        new AppSettings().Sync.NotifyRemoteActions.Should().BeTrue();
        File.WriteAllText(AppPaths.SettingsFile, """{ "Theme": "light", "Sync": { "Paused": false, "RemoteControlLevel": "Control", "AllowRemoteFullAccess": false } }""");
        var old = AppSettings.Load();
        old.Sync.NotifyRemoteActions.Should().BeTrue("WP4.1 之前的設定檔沒有這個欄位");
        old.Sync.RemoteControlLevel.Should().Be("Control", "陽性對照：其他欄位照讀");

        old.Sync.NotifyRemoteActions = false;
        old.Save();

        AppSettings.Load().Sync.NotifyRemoteActions.Should().BeFalse();
    }

    [Fact]
    public void 舊設定檔沒有Sync區_讀得回來()
    {
        File.WriteAllText(AppPaths.SettingsFile, """{ "Theme": "light", "FontSize": 16 }""");
        var old = AppSettings.Load();
        File.WriteAllText(AppPaths.SettingsFile, """{ "Theme": "light", "Sync": null }""");
        var nulled = AppSettings.Load();

        old.Sync.Should().NotBeNull();
        old.Sync.RemoteControlLevel.Should().Be("Off");
        old.FontSize.Should().Be(16);
        nulled.Sync.Should().NotBeNull("寫成 null 也補上");
        nulled.Sync.Paused.Should().BeFalse();
    }
}
