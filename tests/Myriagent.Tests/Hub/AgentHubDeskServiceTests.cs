using System.Collections.Concurrent;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端桌面的鎖定畫面支援在中樞（web-protocol 的 deskService）：沒接不送；check 先送進行中再送結果；install／uninstall 以（假的）UAC 啟動安裝器、
/// 做完重查；UAC 被拒與安裝器失敗各有給人看的說明；進行中再送的不重做；只有這台的視窗能送（不在遠端指令的型錄裡）。
/// 管理是假的：不碰真的服務、Program Files 與 UAC
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubDeskServiceTests
{
    private static readonly string Payload = new('a', 64);

    private static readonly DeskServiceInstall Installed = new("MyriagentDesk", "0.2.950", "abc1234", Payload, "p-aaaaaaaaaaaaaaaa", "0123456789abcdef", "2026-11-13T03:00:00Z");

    private static readonly DeskServiceBundle Bundle = new("0.2.950", "abc1234", Payload);

    private static readonly DeskServiceHello Hello = new(1, "0.2.950", "abc1234", true, 1, ["probe"]);

    private sealed class FakeManager : IDeskServiceManager
    {
        public DeskServiceStatus Status { get; set; } = new(null, Bundle, null, null);

        /// <summary>安裝器的結束碼；成功時把狀態換成 <see cref="AfterInstall"/></summary>
        public int Exit { get; set; }

        public DeskServiceStatus? AfterInstall { get; set; }

        public ConcurrentQueue<string> Calls { get; } = new();

        public TaskCompletionSource? Gate { get; set; }

        public DeskServiceStatus Query(bool probe)
        {
            Calls.Enqueue("query:" + probe);
            return Status;
        }

        public int RunElevated(string verb)
        {
            Calls.Enqueue(verb);
            Gate?.Task.Wait();
            if (Exit == 0)
                Status = verb == "install" ? AfterInstall ?? Status : new DeskServiceStatus(null, Bundle, null, null);
            return Exit;
        }
    }

    private static (HubHarness H, FakeManager Manager) Create(bool preview = true)
    {
        var h = new HubHarness();
        var manager = new FakeManager();
        h.Ui.Invoke(() => h.Hub.AttachDeskService(manager, preview));
        return (h, manager);
    }

    private static Task<JObject> WaitIdleAsync(FakeView view, int since)
        => view.WaitForAsync("deskService", m => m.Value<string>("busy") is null && m.Value<bool>("known"), since, timeoutMs: 4000);

    [Fact]
    public void 沒接_指令不做事_不送()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "deskService", new { action = "check" });
        h.Command(h.Main, "deskService", new { action = "install" });
        h.Main.Messages("deskService").Should().BeEmpty();
    }

    [Fact]
    public async Task check_先送進行中再送結果_沒裝()
    {
        var (h, manager) = Create();
        using var dispose = h;
        var mark = h.Main.Mark();
        h.Command(h.Main, "deskService", new { action = "check" });

        var done = await WaitIdleAsync(h.Main, mark);
        h.Main.Messages("deskService", mark)[0].Value<string>("busy").Should().Be("check");
        done.Value<bool>("offered").Should().BeTrue("開了預覽");
        done.Value<bool>("installed").Should().BeFalse();
        done.Value<bool>("running").Should().BeFalse();
        done.Value<bool>("canInstall").Should().BeTrue();
        done.Value<bool>("update").Should().BeFalse();
        done["bundled"]!.Value<string>("version").Should().Be("0.2.950");
        done.Value<string>("error").Should().BeNull();
        manager.Calls.Should().Equal("query:True");
    }

    [Fact]
    public async Task 功能還在開發_沒開預覽也沒裝的電腦_不顯示()
    {
        var (h, manager) = Create(preview: false);
        using var dispose = h;
        var mark = h.Main.Mark();
        h.Command(h.Main, "deskService", new { action = "check" });
        (await WaitIdleAsync(h.Main, mark)).Value<bool>("offered").Should().BeFalse();

        // 已經裝了服務的電腦照樣看得到（才移除得掉）
        manager.Status = new DeskServiceStatus(Installed, Bundle, Hello, null);
        mark = h.Main.Mark();
        h.Command(h.Main, "deskService", new { action = "check" });
        (await WaitIdleAsync(h.Main, mark)).Value<bool>("offered").Should().BeTrue();
    }

    [Fact]
    public async Task install_啟動安裝器_做完重查_帶服務看到的輸入桌面()
    {
        var (h, manager) = Create();
        using var dispose = h;
        manager.AfterInstall = new DeskServiceStatus(Installed, Bundle, Hello, new DeskServiceProbe(1, "Default", false, 0, ["Default", "Winlogon"]));
        var mark = h.Main.Mark();
        h.Command(h.Main, "deskService", new { action = "install" });

        var done = await WaitIdleAsync(h.Main, mark);
        h.Main.Messages("deskService", mark)[0].Value<string>("busy").Should().Be("install");
        manager.Calls.Should().Equal("install", "query:True");
        done.Value<bool>("installed").Should().BeTrue();
        done.Value<bool>("running").Should().BeTrue();
        done.Value<bool>("system").Should().BeTrue();
        done.Value<string>("version").Should().Be("0.2.950");
        done.Value<string>("build").Should().Be("abc1234");
        JToken.DeepEquals(done["probe"], JObject.Parse("""{"desktop":"Default","secure":false,"error":0,"desktops":["Default","Winlogon"]}""")).Should().BeTrue(done["probe"]?.ToString());
        done.Value<string>("error").Should().BeNull();
    }

    [Fact]
    public async Task install_UAC沒有按是_說明原因_狀態照舊()
    {
        var (h, manager) = Create();
        using var dispose = h;
        manager.Exit = DeskServiceManager.Declined;
        var mark = h.Main.Mark();
        h.Command(h.Main, "deskService", new { action = "install" });

        var done = await WaitIdleAsync(h.Main, mark);
        done.Value<string>("error").Should().Contain("沒有安裝").And.Contain("UAC");
        done.Value<bool>("installed").Should().BeFalse();

        // 下一個動作開始就清掉上一次的錯誤
        mark = h.Main.Mark();
        h.Command(h.Main, "deskService", new { action = "check" });
        (await WaitIdleAsync(h.Main, mark)).Value<string>("error").Should().BeNull();
    }

    [Fact]
    public async Task install_安裝器失敗_帶結束碼的說明()
    {
        var (h, manager) = Create();
        using var dispose = h;
        manager.Exit = (int)DeskServiceExit.SignatureInvalid;
        var mark = h.Main.Mark();
        h.Command(h.Main, "deskService", new { action = "install" });
        var error = (await WaitIdleAsync(h.Main, mark)).Value<string>("error");
        error.Should().Contain("安裝失敗").And.Contain("發佈簽章").And.Contain("13");
    }

    [Fact]
    public async Task uninstall_做完是沒裝()
    {
        var (h, manager) = Create(preview: false);
        using var dispose = h;
        manager.Status = new DeskServiceStatus(Installed, Bundle, Hello, null);
        var mark = h.Main.Mark();
        h.Command(h.Main, "deskService", new { action = "uninstall" });

        var done = await WaitIdleAsync(h.Main, mark);
        manager.Calls.Should().Equal("uninstall", "query:True");
        done.Value<bool>("installed").Should().BeFalse();
        done.Value<bool>("offered").Should().BeFalse("移除之後、沒開預覽：這一區收起來");
    }

    [Fact]
    public async Task app帶的比裝好的新_標出有更新()
    {
        var (h, manager) = Create();
        using var dispose = h;
        manager.Status = new DeskServiceStatus(Installed with { Version = "0.2.940", Build = "0123456", Payload = new string('b', 64) }, Bundle, Hello, null);
        var mark = h.Main.Mark();
        h.Command(h.Main, "deskService", new { action = "check" });
        var done = await WaitIdleAsync(h.Main, mark);
        done.Value<bool>("update").Should().BeTrue();
        done.Value<string>("version").Should().Be("0.2.940");
    }

    [Fact]
    public async Task 進行中再送_不重做_只重送狀態()
    {
        var (h, manager) = Create();
        using var dispose = h;
        manager.Gate = new TaskCompletionSource();
        var mark = h.Main.Mark();
        h.Command(h.Main, "deskService", new { action = "install" });
        await h.Main.WaitForAsync("deskService", m => m.Value<string>("busy") == "install", mark);

        h.Command(h.Main, "deskService", new { action = "install" });
        h.Command(h.Main, "deskService", new { action = "uninstall" });
        manager.Gate.SetResult();
        await WaitIdleAsync(h.Main, mark);
        manager.Calls.Should().Equal("install", "query:True");
    }

    [Fact]
    public void 不認得的動作_不做事()
    {
        var (h, manager) = Create();
        using var dispose = h;
        h.Command(h.Main, "deskService", new { action = "format" });
        h.Command(h.Main, "deskService");
        manager.Calls.Should().BeEmpty();
        h.Main.Messages("deskService").Should().BeEmpty();
    }

    [Fact]
    public void 只在這台_遠端指令的型錄不收()
    {
        RemoteCommandCatalog.IsLocalOnly("deskService").Should().BeTrue();
        RemoteCommandCatalog.Find("deskService").Should().BeNull("瀏覽器版與其他電腦不能在這台裝系統服務");
    }

    [Fact]
    public void 結束碼的說明_移除與安裝各說各的()
    {
        DeskServiceHubRules.ErrorText("install", 0).Should().BeNull();
        DeskServiceHubRules.ErrorText("uninstall", DeskServiceManager.Declined).Should().Contain("沒有移除");
        DeskServiceHubRules.ErrorText("install", DeskServiceManager.TimedOut).Should().Contain("安裝程式");
        DeskServiceHubRules.ErrorText("uninstall", (int)DeskServiceExit.ServiceFailed).Should().Contain("移除失敗").And.Contain("15");
        DeskServiceHubRules.ErrorText("install", 12345).Should().Contain("未預期");
    }

    [Fact]
    public void 還沒查過_欄位是還不知道的樣子()
    {
        var message = DeskServiceHubRules.Message(null, preview: true, busy: "check", error: null);
        message.Value<bool>("known").Should().BeFalse();
        message.Value<bool>("installed").Should().BeFalse();
        message.Value<string>("busy").Should().Be("check");
        message["probe"].Should().BeNull();
        (message["bundled"] is JObject).Should().BeFalse();
    }
}
