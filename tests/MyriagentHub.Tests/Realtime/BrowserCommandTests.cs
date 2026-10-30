using System.Diagnostics;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Pairing;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.EventLogTests;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 擁有者在瀏覽器上的修改（設計 14.4 伺服器授權、14.6；web-protocol 第六節第 9 點；sync-protocol 6.2）：
/// setting、accountUpdate、accountReorder、deviceRename 由伺服器直接寫進 sync.db 並稽核，本機提交之後才回 accepted；
/// 桌面專屬的設定與指令回 desktop_only（SEC-24）；被拒時依 D17 送 toast
/// </summary>
public sealed class BrowserCommandTests
{
    // ───── setting ─────

    [Fact]
    public async Task setting漫遊鍵寫入prefs並推settings與發prefs_set事件()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        await using var other = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        await other.Hub.ReadyAsync();

        var result = await owner.Hub.CmdAsync(new { cmd = "setting", key = "theme", value = "light" });
        // 回 accepted 時已經提交：事件與 prefs 列都在
        var set = ServerPayloads<PrefsSetPayload>(host, SyncEventKinds.PrefsSet).Should().ContainSingle().Subject;
        set.Key.Should().Be("theme");
        set.Value.Value<string>().Should().Be("light");
        set.Version.Should().Be(1);
        Count(host, "SELECT COUNT(*) FROM prefs WHERE key = 'theme' AND value_json = '\"light\"' AND updated_by = 'owner'").Should().Be(1);
        result["accepted"]!.Value<bool>().Should().BeTrue();

        await WaitForAuditAsync(host, AuditActions.PrefsSet);
        var audit = (await AuditAsync(host, AuditActions.PrefsSet)).Single();
        audit.Outcome.Should().Be(AuditOutcomes.Ok);
        audit.ActorType.Should().Be(AuditActors.Owner);
        audit.ActorId.Should().NotBeNullOrEmpty().And.HaveLength(12, "工作階段 id 只記前 12 碼");
        audit.Ip.Should().Be(TestIpStartupFilter.DefaultIp);
        audit.Target.Should().Be("theme");
        audit.Detail.Should().Contain("light", "外觀偏好記鍵與值");

        (await other.Hub.WaitForAsync("settings", m => m["settings"]!["theme"]!.Value<string>() == "light"))["settings"]!["fontSize"]!
            .Value<int>().Should().Be(14, "整份取代：其他鍵照舊");
        (await owner.Hub.WaitForAsync("settings", m => m["settings"]!["theme"]!.Value<string>() == "light")).Should().NotBeNull("送出的那條也收到");
    }

    [Theory]
    [InlineData("instructions")]
    [InlineData("closeToTray")]
    [InlineData("autoUpdateCli")]
    [InlineData("autoUpdateApp")]
    [InlineData("cliRestartPolicy")]
    [InlineData("claudeRemoteControl")]
    public async Task setting的instructions回desktop_only且不寫入並稽核一次(string key)
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var high = host.Services.GetRequiredService<SyncNotifier>().High;
        for (var i = 0; i < 2; i++)
        {
            var result = await owner.Hub.CmdAsync(new { cmd = "setting", key, value = "x" });
            result["accepted"]!.Value<bool>().Should().BeFalse();
            result["error"]!.Value<string>().Should().Be(BrowserErrorCodes.DesktopOnly);
        }
        Count(host, $"SELECT COUNT(*) FROM prefs WHERE key = '{key}'").Should().Be(0);
        host.Services.GetRequiredService<SyncNotifier>().High.Should().Be(high, "沒有任何事件");
        ServerPayloads<PrefsSetPayload>(host, SyncEventKinds.PrefsSet).Should().BeEmpty();

        // 被拒的稽核在回應之前就寫了；連送兩次只有一筆
        var audits = await AuditAsync(host, AuditActions.PrefsSet);
        audits.Should().ContainSingle();
        audits[0].Outcome.Should().Be(AuditOutcomes.Denied);
        audits[0].Target.Should().Be(key);

        // 另一條連線（另一個分頁）各自記一次
        await using var tab = await BrowserHubClient.ConnectAsync(host, owner.Http);
        (await tab.CmdAsync(new { cmd = "setting", key, value = "x" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.DesktopOnly);
        (await AuditAsync(host, AuditActions.PrefsSet)).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("__proto__")]
    [InlineData("autoUpdate")]
    public async Task setting不在白名單的鍵回invalid(string key)
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var result = await owner.Hub.CmdAsync(new { cmd = "setting", key, value = true });
        result["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid);
        Count(host, "SELECT COUNT(*) FROM prefs").Should().Be(0);
        (await AuditAsync(host, AuditActions.PrefsSet)).Should().BeEmpty("不在白名單的鍵不進稽核");
    }

    [Fact]
    public async Task syncSetPolicy回desktop_only()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var high = host.Services.GetRequiredService<SyncNotifier>().High;
        var result = await owner.Hub.CmdAsync(new { cmd = "syncSetPolicy", remoteControlLevel = "Control", allowRemoteFullAccess = true });
        result["error"]!.Value<string>().Should().Be(BrowserErrorCodes.DesktopOnly, "遠端改不了本機政策（SEC-24）");
        host.Services.GetRequiredService<SyncNotifier>().High.Should().Be(high);
    }

    // ───── accountUpdate ─────

    [Fact]
    public async Task accountUpdate改名稱與顏色()
    {
        await using var host = await HubTestHost.StartAsync();
        var la = await AddAccountAsync(host);
        var before = LatestAccount(host);
        var upserts = ServerPayloads<AccountUpsertPayload>(host, SyncEventKinds.AccountUpsert).Count;
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();

        var result = await owner.Hub.CmdAsync(new { cmd = "accountUpdate", accountId = la, label = "  私人帳號  ", color = "#3366FF" });
        result["accepted"]!.Value<bool>().Should().BeTrue();
        ServerPayloads<AccountUpsertPayload>(host, SyncEventKinds.AccountUpsert).Should().HaveCount(upserts + 1);
        var after = LatestAccount(host);
        after.Label.Should().Be("私人帳號", "名稱照目錄規則清理");
        after.Color.Should().Be("#3366FF");
        after.Version.Should().Be(before.Version + 1);

        await WaitForAuditAsync(host, AuditActions.AccountUpdated);
        var audit = (await AuditAsync(host, AuditActions.AccountUpdated)).Single();
        audit.Target.Should().Be(la);
        audit.Detail.Should().Contain("label").And.Contain("color").And.NotContain("私人", "不記名稱原文");

        var pushed = await owner.Hub.WaitForAsync("accounts", m => m["accounts"]!.FirstOrDefault()?["label"]?.Value<string>() == "私人帳號");
        pushed["accounts"]![0]!["color"]!.Value<string>().Should().Be("#3366FF");
    }

    [Fact]
    public async Task accountUpdate帶safeMode回desktop_only且不寫入()
    {
        await using var host = await HubTestHost.StartAsync();
        var la = await AddAccountAsync(host);
        var before = LatestAccount(host);
        await using var owner = await LoginAndConnectAsync(host);
        var result = await owner.Hub.CmdAsync(new { cmd = "accountUpdate", accountId = la, label = "改了", safeMode = true });
        result["error"]!.Value<string>().Should().Be(BrowserErrorCodes.DesktopOnly);
        LatestAccount(host).Should().BeEquivalentTo(before, "什麼都不套用");
        (await AuditAsync(host, AuditActions.AccountUpdated)).Should().BeEmpty();
    }

    [Fact]
    public async Task accountUpdate已刪除的帳戶回conflict並送toast()
    {
        await using var host = await HubTestHost.StartAsync();
        var la = await AddAccountAsync(host);
        var catalog = host.Services.GetRequiredService<Catalog>();
        (await host.Services.GetRequiredService<SyncWriter>().WriteAsync(ctx => catalog.DeleteAccount(ctx, la))).Rejected.Should().BeNull();
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();

        var result = await owner.Hub.CmdAsync(new { cmd = "accountUpdate", accountId = la, label = "改了" });
        result["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Conflict);
        var toast = await owner.Hub.WaitForAsync("toast");
        toast["text"]!.Value<string>().Should().Be("這個帳戶已從目錄刪除");
        toast["level"]!.Value<string>().Should().Be("error");

        (await owner.Hub.CmdAsync(new { cmd = "accountUpdate", accountId = SyncIds.NewLogicalAccountId(), label = "x" }))["error"]!
            .Value<string>().Should().Be(BrowserErrorCodes.Invalid, "不存在的帳戶");
    }

    // ───── accountReorder ─────

    [Fact]
    public async Task accountReorder整批套用且只更新有變的()
    {
        await using var host = await HubTestHost.StartAsync();
        var deviceId = await AddDeviceAsync(host);
        (await AppendAsync(host, deviceId, Batch(
            Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com")),
            Ev(2, SyncEventKinds.LinkUpsert, Link("acct-2", "claude:org2:b@example.com")),
            Ev(3, SyncEventKinds.LinkUpsert, Link("acct-3", "claude:org3:c@example.com"))))).Ack.Results.Should().OnlyContain(r => r.Rejected == null);
        var current = Orders(host);
        current.Should().HaveCount(3);
        var requested = AccountOrderRules.Sort(current).Select(a => a.LogicalAccountId).Reverse().ToList();
        var expected = AccountOrderRules.Plan(current, requested);
        expected.Should().NotBeEmpty();
        var upserts = ServerPayloads<AccountUpsertPayload>(host, SyncEventKinds.AccountUpsert).Count;
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();

        (await owner.Hub.CmdAsync(new { cmd = "accountReorder", ids = requested }))["accepted"]!.Value<bool>().Should().BeTrue();
        ServerPayloads<AccountUpsertPayload>(host, SyncEventKinds.AccountUpsert).Should().HaveCount(upserts + expected.Count, "只有順序有變的帳戶發事件");
        AccountOrderRules.Sort(Orders(host)).Select(a => a.LogicalAccountId).Should().Equal(requested);
        var pushed = await owner.Hub.WaitForAsync("accounts", m => m["accounts"]!.Select(a => a["id"]!.Value<string>()).SequenceEqual(requested));
        pushed["accounts"]!.Should().HaveCount(3);
        await WaitForAuditAsync(host, AuditActions.AccountReordered);
        (await AuditAsync(host, AuditActions.AccountReordered)).Single().Detail.Should().Contain($"{expected.Count}");

        // 同樣的順序再送一次：沒有任何變更、不發事件、不稽核
        (await owner.Hub.CmdAsync(new { cmd = "accountReorder", ids = requested }))["accepted"]!.Value<bool>().Should().BeTrue();
        ServerPayloads<AccountUpsertPayload>(host, SyncEventKinds.AccountUpsert).Should().HaveCount(upserts + expected.Count);
        await Task.Delay(200);
        (await AuditAsync(host, AuditActions.AccountReordered)).Should().ContainSingle();
    }

    [Fact]
    public async Task accountReorder任一筆失敗整批回滾()
    {
        await using var host = await HubTestHost.StartAsync();
        var deviceId = await AddDeviceAsync(host);
        (await AppendAsync(host, deviceId, Batch(
            Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com")),
            Ev(2, SyncEventKinds.LinkUpsert, Link("acct-2", "claude:org2:b@example.com"))))).Ack.Results.Should().OnlyContain(r => r.Rejected == null);
        var before = Orders(host);
        var high = host.Services.GetRequiredService<SyncNotifier>().High;
        var catalog = host.Services.GetRequiredService<Catalog>();
        var writer = host.Services.GetRequiredService<SyncWriter>();

        // 第一筆可以套用、第二筆的帳戶不存在：整個工作回滾，第一筆的更新與它發的事件一起撤掉
        IReadOnlyList<(string, int)> plan = [(before[0].LogicalAccountId, 7), (SyncIds.NewLogicalAccountId(), 8)];
        var apply = () => writer.WriteAsync(ctx => AccountReorder.ApplyPlan(ctx, catalog, plan));
        (await apply.Should().ThrowAsync<CatalogRejectedException>()).Which.Code.Should().Be(BrowserErrorCodes.Invalid);
        Orders(host).Should().BeEquivalentTo(before);
        host.Services.GetRequiredService<SyncNotifier>().High.Should().Be(high, "事件數沒變");
        Count(host, "SELECT COUNT(*) FROM logical_accounts WHERE sort_order = 7").Should().Be(0);

        // 寫入者照常可用
        (await writer.WriteAsync(ctx => AccountReorder.ApplyPlan(ctx, catalog, [(before[0].LogicalAccountId, 7)]))).Should().Be(1);
    }

    // ───── deviceRename ─────

    [Fact]
    public async Task deviceRename改顯示名稱signedName不變()
    {
        await using var host = await HubTestHost.StartAsync();
        var deviceId = await AddDeviceAsync(host, "書房電腦");
        await using var owner = await LoginAndConnectAsync(host);
        (await owner.Hub.ReadyAsync())["remote"]!["devices"]!.Single()["name"]!.Value<string>().Should().Be("書房電腦");

        (await owner.Hub.CmdAsync(new { cmd = "deviceRename", deviceId, name = "客廳電腦" }))["accepted"]!.Value<bool>().Should().BeTrue();
        var device = (await owner.Hub.WaitForAsync("devices", m => m["devices"]!.Single()["name"]!.Value<string>() == "客廳電腦"))["devices"]!.Single();
        device["signedName"]!.Value<string>().Should().Be("書房電腦", "名冊裡配對時的名稱不變");
        device["id"]!.Value<string>().Should().Be(deviceId);
        ServerPayloads<DeviceUpsertPayload>(host, SyncEventKinds.DeviceUpsert)[^1].Name.Should().Be("客廳電腦");
        await WaitForAuditAsync(host, AuditActions.DeviceRenamed);
        var audit = (await AuditAsync(host, AuditActions.DeviceRenamed)).Single();
        audit.Target.Should().Be(deviceId);
        audit.Detail.Should().Contain("name").And.NotContain("客廳", "不記名稱原文");
    }

    [Fact]
    public async Task deviceRename已撤銷的裝置回invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host, "舊電腦");
        await host.Services.GetRequiredService<PairingService>().RevokeAsync(device.Id, "retired", null, null);
        var high = host.Services.GetRequiredService<SyncNotifier>().High;
        await using var owner = await LoginAndConnectAsync(host);
        (await owner.Hub.CmdAsync(new { cmd = "deviceRename", deviceId = device.Id, name = "新名字" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid);
        (await owner.Hub.CmdAsync(new { cmd = "deviceRename", deviceId = SyncIds.NewDeviceId(), name = "新名字" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid, "不存在的裝置");
        host.Services.GetRequiredService<SyncNotifier>().High.Should().Be(high);
        (await AuditAsync(host, AuditActions.DeviceRenamed)).Should().BeEmpty();
    }

    // ───── 被動指令、toast、commandId ─────

    [Fact]
    public async Task 被動指令accepted且不寫入()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        var high = host.Services.GetRequiredService<SyncNotifier>().High;
        foreach (var cmd in new object[]
        {
            new { cmd = "layout", layout = new { } },
            new { cmd = "open", conversationId = "c1" },
            new { cmd = "close", conversationId = "c1" },
            new { cmd = "visible", visible = true },
            new { cmd = "loadOlder", conversationId = "c1" },
        })
        {
            (await owner.Hub.CmdAsync(cmd))["accepted"]!.Value<bool>().Should().BeTrue();
        }
        host.Services.GetRequiredService<SyncNotifier>().High.Should().Be(high);
        await owner.Hub.AssertNoMessageAsync("toast", 200);
    }

    [Fact]
    public async Task not_available指令送toast且十秒內同一cmd只送一則()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        // WP4.5 起 send 由指令路由轉給電腦、WP4.7 起檔案指令也轉送：以仍是 not_available 的 takeover（第 5 階段）測
        for (var i = 0; i < 2; i++)
            (await owner.Hub.CmdAsync(new { cmd = "takeover", conversationId = "c1" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable);
        (await owner.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("網頁版還不能做這件事");
        await owner.Hub.AssertNoMessageAsync("toast", 300);

        // 別的 cmd 照送；10 秒後同一個 cmd 再送
        (await owner.Hub.CmdAsync(new { cmd = "openFolder", path = "C:\\" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.DesktopOnly);
        (await owner.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Contain("只能在電腦上");
        host.Clock.Advance(TimeSpan.FromSeconds(10));
        await owner.Hub.CmdAsync(new { cmd = "takeover", conversationId = "c1" });
        (await owner.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("網頁版還不能做這件事");
    }

    [Fact]
    public async Task 不同的sync前綴名稱共用一個toast節流()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        for (var i = 0; i < 200; i++)
            (await owner.Hub.CmdAsync(new { cmd = "syncX" + i }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.DesktopOnly);
        // 不靠等待：指令的結果回來時要不要送 toast 已經決定、要送的也已入列（ready 回來時伺服器一定已標成送過 init，WP3.7 步驟 1）
        owner.Hub.ServerConnection!.ToastKeys.Should().Be(1, "toast 紀錄不隨不同的名稱長大");

        // 以另一個 cmd 的 toast 當屏障：同一條連線的訊息依序送出，兩則之間沒有別的 toast＝200 個指令只送了一則（不必等一段時間看有沒有多的）
        (await owner.Hub.CmdAsync(new { cmd = "takeover", conversationId = "c1" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable);
        (await owner.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Contain("只能在電腦上");
        (await owner.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("網頁版還不能做這件事", "sync 前綴的指令只送了一則 toast");

        // 清單裡的桌面專屬指令照舊各自節流
        (await owner.Hub.CmdAsync(new { cmd = "syncPair" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.DesktopOnly);
        (await owner.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Contain("只能在電腦上");
        owner.Hub.ServerConnection.ToastKeys.Should().Be(3, "sync*、takeover、syncPair 各一筆");
    }

    [Fact]
    public async Task 自動送出的指令與限流不送toast()
    {
        await using var host = await HubTestHost.StartAsync(settings: Settings(
            ("Hub:RateLimits:Enabled", "true"), ("Hub:RateLimits:BrowserCmdBurst", "5"), ("Hub:RateLimits:BrowserCmdPerSecond", "1")));
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        (await owner.Hub.CmdAsync(new { cmd = "newConversation" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable);
        // WP4.7 起 listFiles 轉送：c1 不是線上 id、沒帶 commandId，路由回 invalid（有提示文字的代碼），自動送出的指令照樣不提示
        (await owner.Hub.CmdAsync(new { cmd = "listFiles", conversationId = "c1", path = "" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid);
        (await owner.Hub.CmdAsync(new { cmd = "visible", visible = false }))["accepted"]!.Value<bool>().Should().BeTrue();
        (await owner.Hub.CmdAsync(new { cmd = "visible", visible = true }))["accepted"]!.Value<bool>().Should().BeTrue();
        // 第 6 個（ready 也算一個）：限流
        (await owner.Hub.CmdAsync(new { cmd = "send", conversationId = "c1", text = "hi" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.RateLimited);
        await owner.Hub.AssertNoMessageAsync("toast", 300);
    }

    [Fact]
    public async Task init之前被拒不送toast()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        (await owner.Hub.CmdAsync(new { cmd = "takeover", conversationId = "c1" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable);
        await owner.Hub.ReadyAsync();
        await owner.Hub.AssertNoMessageAsync("toast", 300);
        owner.Hub.ServerConnection!.InitSent.Should().BeTrue();
    }

    [Fact]
    public async Task commandId照原樣回傳()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        var ok = SyncIds.NewUlid(host.Clock.NowMs);
        var accepted = await owner.Hub.CmdAsync(new { cmd = "setting", key = "fontSize", value = 16 }, ok);
        accepted["accepted"]!.Value<bool>().Should().BeTrue();
        accepted["commandId"]!.Value<string>().Should().Be(ok);

        var rejected = SyncIds.NewUlid(host.Clock.NowMs);
        var denied = await owner.Hub.CmdAsync(new { cmd = "setting", key = "fontSize", value = "大" }, rejected);
        denied["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid);
        denied["commandId"]!.Value<string>().Should().Be(rejected);

        var unknown = SyncIds.NewUlid(host.Clock.NowMs);
        (await owner.Hub.CmdAsync(new { cmd = "Foo" }, unknown))["commandId"]!.Value<string>().Should().Be(unknown);
    }

    // ───── 寫入的路徑 ─────

    [Fact]
    public async Task Cmd只等本機寫入不等裝置()
    {
        await using var host = await HubTestHost.StartAsync();
        await AddDeviceAsync(host, "離線的電腦");
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        var watch = Stopwatch.StartNew();
        (await owner.Hub.CmdAsync(new { cmd = "setting", key = "enterToSend", value = false }))["accepted"]!.Value<bool>().Should().BeTrue();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "沒有任何裝置在線也立刻回");
    }

    [Fact]
    public async Task 寫入者停止後回unavailable()
    {
        await using var host = await HubTestHost.StartAsync();
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        host.Services.GetRequiredService<SyncWriter>().Stop();

        (await owner.Hub.CmdAsync(new { cmd = "setting", key = "theme", value = "light" }))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Unavailable);
        (await owner.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Contain("稍後");
        owner.Hub.Closed.IsCompleted.Should().BeFalse("連線保持");
        (await owner.Hub.CmdAsync(new { cmd = "visible", visible = true }))["accepted"]!.Value<bool>().Should().BeTrue();
        (await AuditAsync(host, AuditActions.PrefsSet)).Should().BeEmpty();
    }

    [Fact]
    public async Task 裝置拉取得到瀏覽器的修改()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host, "書房電腦");
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();

        (await owner.Hub.CmdAsync(new { cmd = "setting", key = "notifications", value = false }))["accepted"]!.Value<bool>().Should().BeTrue();
        (await owner.Hub.CmdAsync(new { cmd = "deviceRename", deviceId = device.Id, name = "工作站" }))["accepted"]!.Value<bool>().Should().BeTrue();

        var pulled = await client.Connection.InvokeAsync<PullResponse>(DeviceHubMethods.Pull, new PullRequest([new PullCursor(0, Cls: SyncClasses.Catalog)], 500, 1_000_000));
        var prefs = pulled.Events.Where(e => e.Kind == SyncEventKinds.PrefsSet).Select(Decode<PrefsSetPayload>).Should().ContainSingle().Subject;
        prefs.Key.Should().Be("notifications");
        prefs.Value.Value<bool>().Should().BeFalse();
        pulled.Events.Where(e => e.Kind == SyncEventKinds.DeviceUpsert).Select(Decode<DeviceUpsertPayload>)
            .Should().Contain(p => p.DeviceId == device.Id && p.Name == "工作站");
        pulled.Events.Where(e => e.Kind is SyncEventKinds.PrefsSet or SyncEventKinds.DeviceUpsert).Should().OnlyContain(e => e.Author == SyncEventStore.ServerAuthor);
    }

    // ───── 工具 ─────

    /// <summary>一台裝置上傳一個帳戶連結，回傳伺服器建立的邏輯帳戶 id</summary>
    private static async Task<string> AddAccountAsync(HubTestHost host)
    {
        var deviceId = await AddDeviceAsync(host);
        (await AppendAsync(host, deviceId, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com")))))
            .Ack.Results.Single().Rejected.Should().BeNull();
        return LatestAccount(host).LogicalAccountId;
    }

    private static List<AccountOrder> Orders(HubTestHost host)
    {
        using var read = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        using var cmd = read.CreateCommand();
        cmd.CommandText = "SELECT logical_account_id, sort_order, created_at FROM logical_accounts WHERE deleted_at IS NULL ORDER BY logical_account_id";
        using var reader = cmd.ExecuteReader();
        var list = new List<AccountOrder>();
        while (reader.Read())
            list.Add(new AccountOrder(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        return list;
    }

    private static async Task<List<AuditRecord>> AuditAsync(HubTestHost host, string action)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await db.Audit.AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.Seq).ToListAsync();
    }

    private static T Decode<T>(PulledEvent e) =>
        PayloadRules.TryDecode(e.Enc, e.Payload, 1 << 22, out var json) ? SyncJson.Deserialize<T>(json)! : throw new InvalidOperationException("payload 解不開");
}
