using System.Net;
using MyriagentHub.Auth;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// <c>control</c>（WP4.5 計畫 D24、步 5；web-protocol 第一節〔遠端〕、契約 D57）：每則 init 之後接著一則（第二次 ready、溢出後的 resync 也是），
/// 擁有者密碼的狀態改變時對每條已送過 init 的連線重送、還沒送過 init 的不送，合併鍵讓佇列裡只留最新的一則；沒有掛上來源的閘道不送
/// </summary>
public sealed class BrowserControlTests
{
    private const string NewPassword = "new-password-123";

    private static string ControlJson(bool defaultPassword) =>
        $$"""{"type":"control","tier1Until":null,"tier2Until":null,"defaultPassword":{{(defaultPassword ? "true" : "false")}}}""";

    // ───── 主機（正式的登記：啟動工作掛上來源、訂閱改密碼） ─────

    [Fact]
    public async Task ready之後是init接著control()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        (await owner.Hub.CmdAsync(new { cmd = "ready", mode = "main" }))["accepted"]!.Value<bool>().Should().BeTrue();

        var first = await NextAsync(owner.Hub);
        first["type"]!.Value<string>().Should().Be("init", "init 之前什麼都不送");
        first.Property("tier1Until").Should().BeNull("init 不帶 control 的欄位（契約 D57）");
        first.Property("defaultPassword").Should().BeNull();
        var second = await NextAsync(owner.Hub);
        second.ToString(Newtonsoft.Json.Formatting.None).Should().Be(ControlJson(defaultPassword: true), "init 之後緊接著 control；首次啟動是預設密碼");
        await owner.Hub.AssertNoMessageAsync("control", 300);
    }

    [Fact]
    public async Task 第二次ready又是init接著control()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        (await NextAsync(owner.Hub))["type"]!.Value<string>().Should().Be("control");

        (await owner.Hub.CmdAsync(new { cmd = "ready", mode = "main" }))["accepted"]!.Value<bool>().Should().BeTrue();
        var types = new List<string>();
        for (var i = 0; i < 2; i++)
            types.Add((await NextAsync(owner.Hub))["type"]!.Value<string>()!);
        types.Should().Equal(["init", "control"]);
    }

    [Fact]
    public async Task 改密碼後所有已送過init的連線收到defaultPassword_false()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        // 同一個工作階段的第二條連線（改密碼會撤銷其他工作階段，這條不受影響）
        await using var second = await BrowserHubClient.ConnectAsync(host, owner.Http);
        foreach (var hub in new[] { owner.Hub, second })
        {
            await hub.ReadyAsync();
            (await NextAsync(hub)).ToString(Newtonsoft.Json.Formatting.None).Should().Be(ControlJson(defaultPassword: true));
        }

        using (var response = await OwnerFlows.ChangePasswordAsync(owner.Http, OwnerAccount.DefaultPassword, NewPassword))
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var hub in new[] { owner.Hub, second })
            (await hub.WaitForAsync("control")).ToString(Newtonsoft.Json.Formatting.None).Should().Be(ControlJson(defaultPassword: false));

        // 新的 init 也帶目前的狀態
        (await owner.Hub.CmdAsync(new { cmd = "ready", mode = "main" }))["accepted"]!.Value<bool>().Should().BeTrue();
        await owner.Hub.WaitForAsync("init");
        (await NextAsync(owner.Hub)).ToString(Newtonsoft.Json.Formatting.None).Should().Be(ControlJson(defaultPassword: false));

        // 改回預設：又是 true（只警告，Q5）
        using (var response = await OwnerFlows.ChangePasswordAsync(owner.Http, NewPassword, OwnerAccount.DefaultPassword))
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.WaitForAsync("control")).ToString(Newtonsoft.Json.Formatting.None).Should().Be(ControlJson(defaultPassword: true));
    }

    [Fact]
    public async Task 未送init的連線收不到PushControl()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        await using var silent = await BrowserHubClient.ConnectAsync(host, owner.Http);
        await owner.Hub.ReadyAsync();
        (await NextAsync(owner.Hub))["type"]!.Value<string>().Should().Be("control");

        host.Services.GetRequiredService<BrowserGateway>().PushControl();
        (await owner.Hub.WaitForAsync("control"))["defaultPassword"]!.Value<bool>().Should().BeTrue("正向對照：送過 init 的收得到");
        await silent.AssertNoMessageAsync("control", 300);
        silent.ServerConnection!.InitSent.Should().BeFalse();
        silent.Drain().Should().BeEmpty("還沒送 ready 的連線什麼都收不到");

        // 之後送了 ready：照樣是 init 接著 control
        (await silent.CmdAsync(new { cmd = "ready", mode = "main" }))["accepted"]!.Value<bool>().Should().BeTrue();
        (await NextAsync(silent))["type"]!.Value<string>().Should().Be("init");
        (await NextAsync(silent))["type"]!.Value<string>().Should().Be("control");
    }

    [Fact]
    public async Task 正式登記有control的啟動工作_而且只登記一份()
    {
        await using var host = await HubTestHost.StartAsync();
        var tasks = host.Services.GetServices<MyriagentHub.Hosting.IStartupTask>().ToList();
        tasks.OfType<CommandStartup>().Should().ContainSingle();
        var startup = tasks.OfType<CommandStartup>().Single();
        startup.Name.Should().Be("commands");
        startup.Order.Should().BeGreaterThan(MyriagentHub.Hosting.StartupOrder.SyncDatabase);
        tasks.Where(t => t is not CommandStartup).Select(t => t.Order).Should().NotContain(startup.Order, "順序不和別的啟動步驟並列");
        host.Services.GetRequiredService<BrowserGateway>().ControlMessage().Should().NotBeNull("啟動工作已掛上來源");
    }

    // ───── 閘道單元測試（GatewayHarness：自己掛來源） ─────

    [Fact]
    public async Task 沒有掛上來源的閘道不送control()
    {
        var h = new GatewayHarness();
        h.Gateway.ControlMessage().Should().BeNull();
        var a = await h.ReadyAsync("c1");
        h.Gateway.PushControl();
        await a.AssertNothingAsync("沒有掛上 control 的來源：與第 3 階段相同，init 之後什麼都不送");
    }

    [Fact]
    public async Task 溢出重送init後control也重送()
    {
        var h = new GatewayHarness(o => o.Browser.OutboxMaxMessages = 2);
        h.Gateway.AttachControl(() => true);
        var a = h.Add("c1");
        a.Block();
        h.Gateway.RequestInit(a.Connection, "main");
        await h.RunAsync();
        await WaitUntilAsync(() => a.Connection.Outbox.Count == 1, because: "第一則 init 已出列、送出卡住，control 排在佇列裡");

        ChangeEverything(h);
        (await h.RunAsync()).Should().Be(TimeSpan.Zero, "control＋accounts＋devices 超過 2 則：溢出、清空、下一輪重送 init");
        a.Connection.Outbox.Count.Should().Be(0);
        a.Connection.InitSent.Should().BeFalse();

        await h.RunAsync();
        a.Connection.Outbox.Count.Should().Be(2, "重送的 init 接著 control");
        a.Release();
        var sent = await a.TakeAsync(3);
        sent.Select(m => m["type"]!.Value<string>()).Should().Equal(["init", "init", "control"], "溢出清掉的 control 在重送的 init 之後再送一次");
        await a.AssertNothingAsync();
        a.Connection.AbortReason.Should().BeNull();
    }

    [Fact]
    public async Task control入列就溢出_這一輪要求立刻重送init()
    {
        // 佇列上限 2：跨 init 保留的 toast＋init 剛好滿，接著入列的 control 溢出。Deliver 要回報 resync，迴圈立刻再跑一輪（不等推送間隔）
        var h = new GatewayHarness(o => o.Browser.OutboxMaxMessages = 2);
        h.Gateway.AttachControl(() => true);
        var a = await h.ReadyAsync("c1");
        (await a.NextAsync())["type"]!.Value<string>().Should().Be("control");

        a.Block();
        h.Gateway.SendToast(a.Connection, "send", Toast("t0"));
        await WaitUntilAsync(() => a.Connection.Outbox.Count == 0, because: "t0 已出列、送出卡住，之後入列的都留在佇列裡");
        h.Gateway.SendToast(a.Connection, "stop", Toast("t1"));
        a.Connection.Outbox.Count.Should().Be(1);
        h.Gateway.RequestInit(a.Connection, "main");
        (await h.RunAsync()).Should().Be(TimeSpan.Zero, "t1＋init＋control 超過 2 則：control 溢出、清空，下一輪立刻重送 init");
        a.Connection.InitSent.Should().BeFalse();
        a.Connection.Outbox.Count.Should().Be(0);

        await h.RunAsync();
        a.Connection.Outbox.Count.Should().Be(2, "重送的 init 接著 control");
        a.Release();
        var sent = await a.TakeAsync(3);
        sent.Select(m => m["type"]!.Value<string>()).Should().Equal(["toast", "init", "control"], "t1 隨溢出清掉；重送的 init 之後是 control");
        await a.AssertNothingAsync();
        a.Connection.AbortReason.Should().BeNull();
    }

    [Fact]
    public async Task 連續兩次PushControl_佇列裡只留一則()
    {
        var h = new GatewayHarness();
        var defaultPassword = true;
        h.Gateway.AttachControl(() => Volatile.Read(ref defaultPassword));
        var a = await h.ReadyAsync("c1");
        (await a.NextAsync()).ToString(Newtonsoft.Json.Formatting.None).Should().Be(ControlJson(defaultPassword: true));

        a.Block();
        h.Gateway.SendToast(a.Connection, "send", Toast("t0"));
        await WaitUntilAsync(() => a.Connection.Outbox.Count == 0, because: "t0 已出列、送出卡住，之後入列的都留在佇列裡");
        Volatile.Write(ref defaultPassword, false);
        h.Gateway.PushControl();
        Volatile.Write(ref defaultPassword, true);
        h.Gateway.PushControl();
        a.Connection.Outbox.Count.Should().Be(1, "合併鍵 control：同鍵的取代佇列裡的舊的");

        a.Release();
        var sent = await a.TakeAsync(0);
        sent.Select(m => m.ToString(Newtonsoft.Json.Formatting.None)).Should().Equal(
            BrowserCommandRules.ToastJson("t0", BrowserCommandRules.ToastLevel), ControlJson(defaultPassword: true));
    }

    [Fact]
    public async Task 排著的control被init清掉_init之後再送一則()
    {
        var h = new GatewayHarness();
        var defaultPassword = true;
        h.Gateway.AttachControl(() => Volatile.Read(ref defaultPassword));
        var a = await h.ReadyAsync("c1");
        (await a.NextAsync())["type"]!.Value<string>().Should().Be("control");

        a.Block();
        h.Gateway.SendToast(a.Connection, "send", Toast("t0"));
        await WaitUntilAsync(() => a.Connection.Outbox.Count == 0);
        Volatile.Write(ref defaultPassword, false);
        h.Gateway.PushControl();
        h.Gateway.RequestInit(a.Connection, "main");
        await h.RunAsync();
        a.Release();
        var sent = await a.TakeAsync(0);
        sent.Select(m => m["type"]!.Value<string>()).Should().Equal(["toast", "init", "control"], "排著的 control 被 init 清掉，init 之後只有一則");
        sent[2]["defaultPassword"]!.Value<bool>().Should().BeFalse();
    }

    private static async Task<JObject> NextAsync(BrowserHubClient hub, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return await hub.Messages.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"{timeoutMs} ms 內沒有收到訊息");
        }
    }

    private static ClientOutboxMessage Toast(string text) => new("toast", BrowserCommandRules.ToastJson(text, BrowserCommandRules.ToastLevel));

    private static void ChangeEverything(GatewayHarness h)
    {
        h.Source.Current = new BrowserSnapshotData("""[{"id":"la_1"}]""", """[{"id":"d_1"}]""", """{"d_1":{}}""",
            """{"theme":"dark","fontSize":13,"enterToSend":true,"notifications":true}""");
        h.Gateway.MarkDirty(BrowserParts.All);
        h.Time.Advance(TimeSpan.FromMilliseconds(500));
    }
}
