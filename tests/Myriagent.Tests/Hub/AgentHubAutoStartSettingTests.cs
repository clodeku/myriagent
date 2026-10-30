using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 設定頁「開機時自動啟動」（autoStart）：狀態在登錄（測試給假的 IAutoStart，不碰真的登錄）、不存 settings.json；
/// 這次執行不能設定時送 null，切換後一律重送設定，寫入失敗時提示並回到實際狀態
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubAutoStartSettingTests
{
    private sealed class FakeAutoStart(bool available) : IAutoStart
    {
        public bool Available { get; } = available;

        public bool Enabled { get; set; }

        public Exception? Failure { get; set; }

        public List<bool> Calls { get; } = new();

        public bool IsEnabled() => Enabled;

        public void SetEnabled(bool enabled)
        {
            Calls.Add(enabled);
            if (Failure != null)
                throw Failure;
            Enabled = enabled;
        }
    }

    private static FakeAutoStart Attach(HubHarness h, bool available)
    {
        var fake = new FakeAutoStart(available);
        h.Ui.Invoke(() => h.Hub.AutoStart = fake);
        return fake;
    }

    private static JToken? AutoStartIn(JObject message) => message["settings"]!["autoStart"];

    [Fact]
    public void 沒有登錄或這次執行不能設定_送null_切換不寫登錄()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "ready");
        AutoStartIn(h.Main.Last("init")!)!.Type.Should().Be(JTokenType.Null, "測試與開發版沒有接上登錄");

        var fake = Attach(h, available: false);
        fake.Enabled = true;
        var mark = h.Main.Mark();
        h.Command(h.Main, "setting", new { key = "autoStart", value = false });

        fake.Calls.Should().BeEmpty("開發版、--data 的探針不寫使用者的登錄");
        var settings = h.Main.Messages("settings", mark).Should().ContainSingle("照樣重送，網頁的開關才會回到停用").Subject;
        AutoStartIn(settings)!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void 可以設定_送登錄的實際狀態_打開與關閉都寫登錄並重送()
    {
        using var h = new HubHarness();
        var fake = Attach(h, available: true);
        h.Command(h.Main, "ready");
        AutoStartIn(h.Main.Last("init")!)!.Value<bool>().Should().BeFalse();
        var start = h.Main.Mark();

        var mark = h.Main.Mark();
        h.Command(h.Main, "setting", new { key = "autoStart", value = true });
        fake.Calls.Should().Equal(true);
        AutoStartIn(h.Main.Messages("settings", mark).Single())!.Value<bool>().Should().BeTrue();

        mark = h.Main.Mark();
        h.Command(h.Main, "setting", new { key = "autoStart", value = false });
        fake.Calls.Should().Equal(true, false);
        AutoStartIn(h.Main.Messages("settings", mark).Single())!.Value<bool>().Should().BeFalse();
        h.Main.Messages("toast", start).Should().BeEmpty();
    }

    [Fact]
    public void 工作管理員停用時_設定顯示關閉()
    {
        using var h = new HubHarness();
        var fake = Attach(h, available: true);
        h.Command(h.Main, "ready");
        fake.Enabled = true;
        var mark = h.Main.Mark();

        h.Command(h.Main, "setting", new { key = "theme", value = "light" });
        AutoStartIn(h.Main.Messages("settings", mark).Single())!.Value<bool>().Should().BeTrue("每次渲染設定都重讀登錄，不用 settings.json 的舊值");

        fake.Enabled = false;
        mark = h.Main.Mark();
        h.Command(h.Main, "setting", new { key = "theme", value = "dark" });
        AutoStartIn(h.Main.Messages("settings", mark).Single())!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public void 寫入失敗_提示原因並重送實際狀態()
    {
        using var h = new HubHarness();
        var fake = Attach(h, available: true);
        fake.Failure = new UnauthorizedAccessException("拒絕存取");
        var mark = h.Main.Mark();

        h.Command(h.Main, "setting", new { key = "autoStart", value = true });

        var toast = h.Main.Messages("toast", mark).Should().ContainSingle().Subject;
        toast.Value<string>("text").Should().Be("開啟開機時自動啟動失敗：拒絕存取");
        toast.Value<string>("level").Should().Be("error");
        AutoStartIn(h.Main.Messages("settings", mark).Single())!.Value<bool>().Should().BeFalse("網頁先把開關打開了，要回到登錄的實際狀態");
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void 不是布林_不寫登錄(string json)
    {
        using var h = new HubHarness();
        var fake = Attach(h, available: true);

        h.Ui.Invoke(() => h.Hub.HandleCommand(h.Main, JObject.Parse($$"""{"cmd":"setting","key":"autoStart","value":{{json}}}"""), null));

        fake.Calls.Should().BeEmpty();
    }
}
