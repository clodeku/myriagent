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

        /// <summary>請服務自己更新的結果（null＝成功，把狀態換成 <see cref="AfterInstall"/>）</summary>
        public string? SelfUpdateError { get; set; }

        public string? SelfUpdate()
        {
            Calls.Enqueue("selfUpdate");
            if (SelfUpdateError is null)
                Status = AfterInstall ?? Status;
            return SelfUpdateError;
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
    public async Task 狀態_安全桌面能不能輸入_服務記下的伺服器要是現在配對的這一台()
    {
        var (h, manager) = Create();
        using var dispose = h;
        using var hub = new Myriagent.Tests.RemoteDesk.DeskOpenSamples();
        using var otherHub = new Myriagent.Tests.RemoteDesk.DeskOpenSamples();
        async Task<string?> StateAsync(DeskServiceStatus status)
        {
            manager.Status = status;
            var mark = h.Main.Mark();
            h.Command(h.Main, "deskService", new { action = "check" });
            return (await WaitIdleAsync(h.Main, mark)).Value<string>("secureInput");
        }

        (await StateAsync(new DeskServiceStatus(null, Bundle, null, null, null, hub.Trust))).Should().BeNull("沒裝：不談");
        (await StateAsync(new DeskServiceStatus(Installed, Bundle, Hello, null, hub.Trust, hub.Trust))).Should().Be("ready");
        (await StateAsync(new DeskServiceStatus(Installed, Bundle, Hello, null, null, hub.Trust))).Should().Be("reinstall", "安裝時還沒配對");
        (await StateAsync(new DeskServiceStatus(Installed, Bundle, Hello, null, otherHub.Trust, hub.Trust))).Should().Be("reinstall", "之後換了伺服器");
        (await StateAsync(new DeskServiceStatus(Installed, Bundle, Hello, null, hub.Trust, null))).Should().Be("unpaired");
    }

    // ---- 更新不經 UAC：服務自己驗簽章後換版 ----

    private static readonly DeskServiceInstall OldInstalled = Installed with { Version = "0.2.940", Build = "0123456", Payload = new string('b', 64) };

    private static readonly DeskServiceHello UpdatingHello = new(1, "0.2.940", "0123456", true, 1, ["probe", "capture", "selfUpdate"]);

    private static readonly DeskServiceBundle SignedBundle = Bundle with { Signed = true };

    [Fact]
    public void 能不能請服務自己更新_服務認得_系統權限_app帶的是簽章過而且比較新的()
    {
        var can = new DeskServiceStatus(OldInstalled, SignedBundle, UpdatingHello, null);
        DeskServiceHubRules.CanSelfUpdate(can).Should().BeTrue();
        DeskServiceHubRules.Message(can, true, null, null).Value<bool>("selfUpdate").Should().BeTrue();

        DeskServiceHubRules.CanSelfUpdate(null).Should().BeFalse();
        DeskServiceHubRules.CanSelfUpdate(can with { Installed = null }).Should().BeFalse("沒裝：第一次一定要按 UAC");
        DeskServiceHubRules.CanSelfUpdate(can with { Hello = null }).Should().BeFalse("服務沒在執行");
        DeskServiceHubRules.CanSelfUpdate(can with { Hello = UpdatingHello with { System = false } }).Should().BeFalse();
        DeskServiceHubRules.CanSelfUpdate(can with { Hello = UpdatingHello with { Features = ["probe", "capture"] } }).Should().BeFalse("舊的服務不會自己更新");
        DeskServiceHubRules.CanSelfUpdate(can with { Bundled = Bundle }).Should().BeFalse("沒有發佈清單與簽章（開發版的建置輸出）");
        DeskServiceHubRules.CanSelfUpdate(can with { Bundled = SignedBundle with { Build = "dev" } }).Should().BeFalse();
        DeskServiceHubRules.CanSelfUpdate(can with { Installed = Installed }).Should().BeFalse("已經是同一份");
        DeskServiceHubRules.CanSelfUpdate(can with { Installed = OldInstalled with { Version = "0.2.960" } }).Should().BeFalse("app 回滾到舊版：不把服務降回去");
        DeskServiceHubRules.Message(can with { Installed = Installed }, true, null, null).Value<bool>("selfUpdate").Should().BeFalse();
    }

    [Fact]
    public async Task 自動更新_能自己更新就請服務換版_不跳UAC_做完重查()
    {
        var (h, manager) = Create();
        using var dispose = h;
        manager.Status = new DeskServiceStatus(OldInstalled, SignedBundle, UpdatingHello, null);
        manager.AfterInstall = new DeskServiceStatus(Installed, SignedBundle, Hello with { Features = ["probe", "capture", "selfUpdate"] }, null);
        var mark = h.Main.Mark();
        h.Ui.Invoke(h.Hub.StartDeskServiceAutoUpdate);

        var done = await WaitIdleAsync(h.Main, mark);
        h.Main.Messages("deskService", mark)[0].Value<string>("busy").Should().Be("update");
        manager.Calls.Should().Equal("query:False", "selfUpdate", "query:False");
        done.Value<string>("version").Should().Be("0.2.950");
        done.Value<bool>("update").Should().BeFalse();
        done.Value<string>("error").Should().BeNull();
    }

    [Fact]
    public async Task 自動更新_不能自己更新的不動_失敗只留說明不退回UAC()
    {
        var (h, manager) = Create();
        using var dispose = h;
        // 舊的服務（不認得 selfUpdate）：自動更新什麼都不做，等使用者在設定頁按
        manager.Status = new DeskServiceStatus(OldInstalled, SignedBundle, UpdatingHello with { Features = ["probe", "capture"] }, null);
        var mark = h.Main.Mark();
        h.Ui.Invoke(h.Hub.StartDeskServiceAutoUpdate);
        var idle = await WaitIdleAsync(h.Main, mark);
        manager.Calls.Should().Equal("query:False");
        idle.Value<bool>("update").Should().BeTrue();
        idle.Value<string>("error").Should().BeNull();

        // 服務不收（例如遠端桌面正在用）：不能自己跳 UAC
        manager.Status = new DeskServiceStatus(OldInstalled, SignedBundle, UpdatingHello, null);
        manager.SelfUpdateError = "服務沒有收下更新：busy：遠端桌面正在使用鎖定畫面支援，稍後再更新";
        manager.Calls.Clear();
        mark = h.Main.Mark();
        h.Ui.Invoke(h.Hub.StartDeskServiceAutoUpdate);
        var failed = await WaitIdleAsync(h.Main, mark);
        manager.Calls.Should().Equal("query:False", "selfUpdate", "query:False");
        manager.Calls.Should().NotContain("install");
        failed.Value<string>("error").Should().Contain("自動更新沒有完成").And.Contain("稍後再更新");
    }

    [Fact]
    public async Task 按更新_先請服務自己換版_不成才退回UAC的安裝器()
    {
        var (h, manager) = Create();
        using var dispose = h;
        async Task<JObject> RunAsync(string action)
        {
            var mark = h.Main.Mark();
            h.Command(h.Main, "deskService", new { action });
            return await WaitIdleAsync(h.Main, mark);
        }

        // 設定頁先查過狀態（知道服務能自己更新），再按「更新」
        manager.Status = new DeskServiceStatus(OldInstalled, SignedBundle, UpdatingHello, null);
        manager.AfterInstall = new DeskServiceStatus(Installed, SignedBundle, Hello, null);
        (await RunAsync("check")).Value<bool>("selfUpdate").Should().BeTrue();
        manager.Calls.Clear();
        (await RunAsync("install")).Value<string>("version").Should().Be("0.2.950");
        manager.Calls.Should().Equal("selfUpdate", "query:True");
        manager.Calls.Should().NotContain("install", "服務自己換好了，不必按 UAC");

        manager.Status = new DeskServiceStatus(OldInstalled, SignedBundle, UpdatingHello, null);
        manager.SelfUpdateError = "服務沒有在時間內換成新版";
        await RunAsync("check");
        manager.Calls.Clear();
        var done = await RunAsync("install");
        manager.Calls.Should().Equal("selfUpdate", "install", "query:True");
        done.Value<string>("error").Should().BeNull("安裝器裝好了");

        // 移除不走這條路
        manager.Calls.Clear();
        await RunAsync("uninstall");
        manager.Calls.Should().Equal("uninstall", "query:True");
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
