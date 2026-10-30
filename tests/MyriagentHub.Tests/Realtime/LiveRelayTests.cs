using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 裝置 hub 的 LiveRelay 與伺服器的即時疊加層（WP4.2 計畫步 11；R2、R13、R14，D27、D44、提案 C4）：收件的各種丟棄與限速、第二道 DLP、
/// 疊加層的生命週期（擁有者斷線、重開、持久版本作廢、final、一輪結束）、50 ms 的合併。以 <see cref="ConversationHarness"/>（真的主機＋測試驅動的閘道與收件時鐘）
/// 逐輪驗證；經真的裝置 hub 與瀏覽器 hub 的兩則驗證接線
/// </summary>
public sealed class LiveRelayTests
{
    private static readonly string C1 = ConversationHarness.ConversationId(1);

    private static JObject Running(string text, long ver, string id = "item1", long ord = 1) => ConversationSamples.Item(text, "Running", id, ord, ver);

    // ───── 丟掉的情況（不回錯誤給裝置、只計數） ─────

    [Fact]
    public async Task 沒協商live1_丟掉()
    {
        // 經真的裝置 hub：只宣告 conv1 的連線（WP4.1 的桌面不宣告任何功能）送 LiveRelay：不回錯誤、不中斷、只計數，疊加層不收
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), features: [DeviceFeatureRules.Conv1]);
        (await ConversationEventLogTests.Append(host, device.Id, ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta()))).Ack.Results
            .Should().OnlyContain(r => r.Rejected == null);
        var counters = host.Services.GetRequiredService<ConversationCounters>();
        var overlay = host.Services.GetRequiredService<LiveOverlay>();

        await client.Connection.InvokeAsync(DeviceHubMethods.LiveRelay, RelayOf(ConversationSamples.Conv, Running("即時", 1)));

        counters.Snapshot().LiveDropped![LiveDropReason.NotOwner].Should().Be(1);
        overlay.HasPending.Should().BeFalse();
        overlay.Count.Should().Be(0);
        client.Connection.State.Should().Be(HubConnectionState.Connected, "不中斷連線");

        // 同一台在協商出 live1 的連線上送：照收（證明擋下的是功能，不是別的）
        await using var live = await DeviceHubClient.ConnectAsync(host, device.Token);
        await live.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1]);
        await live.Connection.InvokeAsync(DeviceHubMethods.LiveRelay, RelayOf(ConversationSamples.Conv, Running("即時", 1)));
        counters.Snapshot().LiveDropped![LiveDropReason.NotOwner].Should().Be(1);
        await WaitUntilAsync(() => overlay.Count == 1 || overlay.HasPending, because: "收下的放進疊加層（沒有瀏覽器連線時閘道取走後留著）");
    }

    [Fact]
    public async Task 非擁有者_丟掉()
    {
        await using var h = await ConversationHarness.StartAsync();
        var owner = await h.AddDeviceAsync("擁有者");
        var other = await h.AddDeviceAsync("另一台");
        await h.CreateAsync(owner, C1);
        var otherLive = h.OnlineLive(other);
        var ownerLive = h.OnlineLive(owner);

        h.Relay(otherLive, C1, 1, 1, Running("別台送的", 1)).Should().Be(LiveIntakeOutcome.Dropped);
        h.Relay(ownerLive, C1, 1, 1, Running("世代不符", 1), epoch: 2).Should().Be(LiveIntakeOutcome.Dropped, "epoch 不是對話目前的世代");
        h.Relay(ownerLive, ConversationHarness.ConversationId(9), 1, 1, Running("不認得的對話", 1)).Should().Be(LiveIntakeOutcome.Dropped);
        h.LiveDropped(LiveDropReason.NotOwner).Should().Be(3);
        h.Relay(ownerLive, C1, 1, 1, Running("擁有者的", 1)).Should().Be(LiveIntakeOutcome.Offered);
        h.Intake.ConversationBucketCount.Should().Be(1, "只有通過目錄確認的才建每個對話的權杖桶");
    }

    [Fact]
    public async Task metadata政策_丟掉()
    {
        // 答 4：政策不是 full、已刪除的對話不收即時更新
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var c2 = ConversationHarness.ConversationId(2);
        var c3 = ConversationHarness.ConversationId(3);
        await h.CreateAsync(device, C1, policy: ConversationSyncPolicies.Metadata);
        await h.CreateAsync(device, c2, policy: ConversationSyncPolicies.Paused);
        await h.CreateAsync(device, c3);
        await h.PublishAsync(device, (K.Deleted, new JObject(), c3));
        var live = h.OnlineLive(device);

        foreach (var id in new[] { C1, c2 })
            h.Relay(live, id, 1, 1, Running("不同步內容的對話", 1)).Should().Be(LiveIntakeOutcome.Dropped, id);
        h.LiveDropped(LiveDropReason.Policy).Should().Be(2);
        h.Relay(live, c3, 1, 1, Running("已刪除", 1)).Should().Be(LiveIntakeOutcome.Dropped);
        h.LiveDropped(LiveDropReason.Policy).Should().Be(3, "刪除也是政策（答 4：localOnly＝刪除）");
        h.Overlay.HasPending.Should().BeFalse();
    }

    [Fact]
    public async Task 已撤銷裝置_丟掉()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive(device);
        h.Relay(live, C1, 1, 1, Running("撤銷之前", 1)).Should().Be(LiveIntakeOutcome.Offered);

        // 撤銷會中斷連線；中斷之前已經在 hub 呼叫裡的即時更新照樣要擋
        var devices = h.Host.Services.GetRequiredService<DeviceDirectory>();
        devices.Put(devices.Get(device)! with { RevokedAt = h.Host.Clock.NowMs });
        h.Relay(live, C1, 1, 2, Running("撤銷之後", 2)).Should().Be(LiveIntakeOutcome.Dropped);
        h.LiveDropped(LiveDropReason.NotOwner).Should().Be(1);
    }

    [Fact]
    public async Task 每台每秒50則上限()
    {
        // 每個對話每秒 4 則（突發 4）：15 個對話各送 4 則，同一刻 60 則，每台的權杖桶只放 50 則
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var ids = Enumerable.Range(1, 15).Select(ConversationHarness.ConversationId).ToList();
        foreach (var id in ids)
            await h.CreateAsync(device, id);
        var live = h.OnlineLive(device);

        var outcomes = new List<LiveIntakeOutcome>();
        foreach (var id in ids)
        {
            for (var ver = 1; ver <= 4; ver++)
                outcomes.Add(h.Relay(live, id, 1, ver, Running("v" + ver, ver)));
        }
        outcomes.Count(o => o == LiveIntakeOutcome.Offered).Should().Be(50);
        h.LiveDropped(LiveDropReason.Rate).Should().Be(10);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        ids.Select(id => h.Relay(live, id, 1, 5, Running("一秒後", 5))).Should().OnlyContain(o => o == LiveIntakeOutcome.Offered, "一秒後每台補 50 則、每個對話補 4 則");
    }

    [Fact]
    public async Task 亂送的對話id_不建立權杖桶()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);

        for (var i = 0; i < 1_000; i++)
        {
            h.Relay(live, ConversationHarness.ConversationId(10_000 + i), 1, 1, Running("亂送", 1)).Should().Be(LiveIntakeOutcome.Dropped);
            h.Time.Advance(TimeSpan.FromMilliseconds(20));
        }
        h.Intake.ConversationBucketCount.Should().Be(0, "鍵在目錄確認之後才建");
        h.Intake.CheckRelayCalls.Should().Be(0, "目錄確認不過的不做完整檢查");
        (h.LiveDropped(LiveDropReason.NotOwner) + h.LiveDropped(LiveDropReason.Rate)).Should().Be(1_000);
    }

    [Fact]
    public async Task 外框不合格_丟掉_不做完整檢查()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive(device);
        var connection = h.Registry.Get(live);
        var item = Running("內容", 1).ToString(Newtonsoft.Json.Formatting.None);

        foreach (var relay in new LiveRelay?[]
        {
            null,
            new("0123456789ABCDEF0123456789ABCDEF", 1, 1, 1, item),
            new(C1, 0, 1, 1, item),
            new(C1, 1, 0, 1, item),
            new(C1, 1, 1, 0, item),
            new(C1, 1, 1, 1, null!),
            new(C1, 1, 1, 1, new string('x', LiveRules.MaxLiveBytes + 1)),
        })
        {
            h.Intake.Receive(connection, relay).Should().Be(LiveIntakeOutcome.Dropped);
        }
        h.Intake.Receive(null, new LiveRelay(C1, 1, 1, 1, item)).Should().Be(LiveIntakeOutcome.Dropped, "不在登記裡的連線");
        h.Intake.CheckRelayCalls.Should().Be(0);
        h.Intake.ConversationBucketCount.Should().Be(0);

        // 外框合格、裡面不合格（ord／ver 與外層不同、禁止的本機欄位）：完整檢查擋下
        h.Relay(live, C1, 1, 2, Running("ver 不同", 1)).Should().Be(LiveIntakeOutcome.Dropped);
        var withPath = Running("帶路徑", 1);
        withPath["path"] = "C:/secret";
        h.Relay(live, C1, 1, 1, withPath).Should().Be(LiveIntakeOutcome.Dropped);
        h.Intake.CheckRelayCalls.Should().Be(2);
        h.LiveDropped(LiveDropReason.Stale).Should().Be(9);
        h.LiveDropped(LiveDropReason.NotOwner).Should().Be(1);
    }

    // ───── DLP（答 5、D44） ─────

    [Fact]
    public async Task 即時更新命中_遮蔽後轉送()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive(device);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        var token = ConversationSamples.GitHub();
        h.Relay(live, C1, 1, 1, Running("串流中的權杖 " + token + " 之後", 1)).Should().Be(LiveIntakeOutcome.OfferedRemasked);
        h.Counters.Snapshot().RemaskedLive.Should().Be(1);
        await h.RoundAsync();

        var raw = await a.NextRawAsync();
        raw.Should().NotContain(token);
        var pushed = JObject.Parse(raw);
        pushed["type"]!.Value<string>().Should().Be("item");
        pushed["item"]!["html"]!.Value<string>().Should().Contain("串流中的權杖").And.Contain(CredentialLeakRules.Replacement);
        pushed["item"]!["seq"]!.Value<long>().Should().Be(1, "ord 不變");
    }

    [Fact]
    public async Task 即時更新遮蔽失敗_丟掉並計數()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive(device);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        // 注入點讓遮蔽回原文：遮蔽後仍命中
        h.Intake.RemaskForTest = (_, item) => item.ToString(Newtonsoft.Json.Formatting.None);
        h.Relay(live, C1, 1, 1, Running("權杖 " + ConversationSamples.GitHub(), 1)).Should().Be(LiveIntakeOutcome.Dropped);
        h.LiveDropped(LiveDropReason.Dlp).Should().Be(1);
        h.Counters.Snapshot().RemaskedLive.Should().Be(0);

        // 遮蔽丟出毒事件的例外也只丟這一則
        h.Intake.RemaskForTest = (_, _) => throw new FormatException("壞的遮蔽");
        h.Relay(live, C1, 1, 2, Running("權杖 " + ConversationSamples.GitHub('b'), 2)).Should().Be(LiveIntakeOutcome.Dropped);
        h.LiveDropped(LiveDropReason.Dlp).Should().Be(2);
        h.Overlay.HasPending.Should().BeFalse();
        await h.RoundAsync();
        await a.AssertNothingAsync();
    }

    [Fact]
    public async Task 即時更新的建構式語法_丟掉_不進疊加層()
    {
        // 合併前審查 DLP-CTOR-1：權杖寫成 new 名稱(…) 的建構式名稱，走遍檢查看不到
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive(device);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        var connection = h.Registry.Get(live);

        var key = ConversationSamples.AwsAccessKey();
        foreach (var raw in new[]
        {
            ConversationSamples.RawItem("\"串流中\"", ",\"x\":new " + key + "(1)", status: "Running"),
            ConversationSamples.RawItem("\"串流中\"", ",\"x\":new Foo(1)", status: "Running"),
        })
        {
            var relay = new LiveRelay(C1, 1, 1, 1, raw);
            LiveRules.CheckRelay(relay, out _).Should().BeNull("前提：完整檢查收建構式");
            h.Intake.Receive(connection, relay).Should().Be(LiveIntakeOutcome.Dropped);
        }
        h.LiveDropped(LiveDropReason.Stale).Should().Be(2);
        h.Overlay.HasPending.Should().BeFalse();
        await h.RoundAsync();
        await a.AssertNothingAsync();

        h.Relay(live, C1, 1, 1, Running("對照：標準 JSON", 1)).Should().Be(LiveIntakeOutcome.Offered);
    }

    // ───── 檢查的順序（F37） ─────

    [Fact]
    public async Task 超速的relay不做CheckRelay()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive(device);

        var offered = 0;
        for (var ver = 1; ver <= 1_000; ver++)
        {
            if (h.Relay(live, C1, 1, ver, Running("超速", ver)) == LiveIntakeOutcome.Offered)
                offered++;
        }
        offered.Should().Be(4, "每個對話突發 4 則");
        h.Intake.CheckRelayCalls.Should().Be(4, "超速的 996 則在權杖桶就丟掉，不做 Validate 與嚴格解析");
        h.LiveDropped(LiveDropReason.Rate).Should().Be(996);
    }

    // ───── 疊加層的生命週期 ─────

    [Fact]
    public async Task 擁有者斷線_疊加層清掉_觀看者收到持久版本()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        await h.ItemAsync(device, C1, Running("持久的第一版", 1));
        var (a, _) = await h.ReadyAsync("b1");
        (await h.OpenAsync(a, C1))["items"]![0]!["html"]!.Value<string>().Should().Contain("持久的第一版");

        h.Relay(live, C1, 1, 2, Running("即時的第二版", 2)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("即時的第二版");
        h.Overlay.Count.Should().Be(1);
        h.Counters.Snapshot().OverlayBytes.Should().BeGreaterThan(0);

        h.Offline(live);
        await h.RoundAsync();
        var items = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "item").ToList();
        Html(items.Should().ContainSingle().Subject).Should().Contain("持久的第一版", "提案 C4 的 c：擁有者連線斷了，觀看者改看持久版本");
        h.Overlay.Count.Should().Be(0);
        h.Counters.Snapshot().OverlayBytes.Should().Be(0);
    }

    [Fact]
    public async Task 擁有者重開_新連線ver從1起_新的即時更新照樣顯示()
    {
        // R14、F30：持久版本 ver 40 是舊連線提交的；擁有者重開之後新連線的 ver 從 1 起算，不能拿 ver 比
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var first = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        await h.ItemAsync(device, C1, Running("舊開機的持久版本", 40));
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("舊開機的持久版本");
        h.Overlay.MarkOf(C1, 1).Should().Be(new DurableMark(40, false, h.Registry.Get(first)!.Order), "提交時這條連線是那台最新的");

        h.Offline(first);
        var second = h.OnlineLive(device);
        h.Relay(second, C1, 1, 1, Running("新開機的第一則", 1)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html((await a.TakeAsync(0)).Last(m => m["type"]!.Value<string>() == "item")).Should().Contain("新開機的第一則");
        h.Overlay.Count.Should().Be(1);
    }

    [Fact]
    public async Task 持久item提交_疊加層作廢()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        h.Relay(live, C1, 1, 3, Running("即時 3", 3)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("即時 3");

        // 同一條連線提交的持久版本 ver 3（仍是 Running 的檢查點）：疊加不比它新，作廢；觀看者收到持久版本、只一則
        await h.ItemAsync(device, C1, Running("持久 3", 3));
        await h.RoundAsync();
        var items = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "item").ToList();
        Html(items.Should().ContainSingle().Subject).Should().Contain("持久 3");
        h.Overlay.Count.Should().Be(0);

        // 比持久版本舊的即時更新丟掉；比較新的照收
        h.Relay(live, C1, 1, 2, Running("即時 2", 2)).Should().Be(LiveIntakeOutcome.Offered, "收件只做便宜的檢查，新舊由閘道判斷");
        await h.RoundAsync();
        await a.AssertNothingAsync("ver 2 已被持久版本作廢");
        h.LiveDropped(LiveDropReason.Stale).Should().Be(1);
        h.Relay(live, C1, 1, 4, Running("即時 4", 4)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("即時 4");

        // 持久版本仍是 Running、但比疊加舊：推過持久版本之後再推一次疊加（畫面停在比較新的）
        await h.ItemAsync(device, C1, Running("持久 3b", 3));
        await h.RoundAsync();
        // 兩則同一個合併鍵：還在佇列裡就原地取代，送出幫浦先取走了持久版本就收到兩則；畫面最後都是疊加
        var last = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "item").ToList();
        last.Should().NotBeEmpty();
        Html(last[^1]).Should().Contain("即時 4");
        h.Overlay.Count.Should().Be(1);
    }

    [Fact]
    public async Task final之後的relay丟棄()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        h.Relay(live, C1, 1, 4, Running("即時 4", 4)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("即時 4");
        await h.ItemAsync(device, C1, ConversationSamples.Item("完成了", "Done", ver: 2));
        await h.RoundAsync();
        Html((await a.TakeAsync(0)).Single(m => m["type"]!.Value<string>() == "item")).Should().Contain("完成了", "持久版本不是 Running：清掉疊加、不比 ver（提案 C4 的 a）");

        h.Relay(live, C1, 1, 5, Running("final 之後的", 5)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        await a.AssertNothingAsync("設計 4.5：final 之後的即時更新一律丟掉");
        h.LiveDropped(LiveDropReason.Final).Should().Be(1);
        h.Overlay.Count.Should().Be(0);

        // 有人開著時提交的 final（當時沒有疊加）也記下：之後的即時更新同樣丟掉
        await h.ItemAsync(device, C1, ConversationSamples.Item("第二則完成", "Done", id: "item2", ord: 2));
        await h.RoundAsync();
        await a.TakeAsync(0);
        h.Relay(live, C1, 2, 9, Running("第二則 final 之後的", 9, id: "item2", ord: 2)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        await a.AssertNothingAsync();
        h.LiveDropped(LiveDropReason.Final).Should().Be(2);
    }

    [Fact]
    public async Task 沒人看時的即時更新_open時持久版本已經final_不顯示()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        await h.ItemAsync(device, C1, ConversationSamples.Item("早就完成了", "Done"));
        await h.RunAsync();

        // 沒有瀏覽器連線：閘道照樣取走（不累積），疊加層不知道持久版本是 final（沒人看時不記標記）
        h.Relay(live, C1, 1, 3, Running("晚到的即時更新", 3)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        h.Overlay.HasPending.Should().BeFalse("沒有連線時也取走");
        h.Overlay.Count.Should().Be(1);

        // open 的快照說它已經 final：清掉，不推給開的那條
        var (a, _) = await h.ReadyAsync("b1");
        var snapshot = await h.OpenAsync(a, C1);
        Html(snapshot["items"]![0]!).Should().Contain("早就完成了");
        await h.RoundAsync();
        await a.AssertNothingAsync("快照之後不再推已經 final 的疊加");
        h.Overlay.Count.Should().Be(0);
        h.Overlay.MarkOf(C1, 1)!.Final.Should().BeTrue();
    }

    [Fact]
    public async Task 即時更新與持久版本同一輪_持久版本為準()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        // 即時更新先收件、同一個 ver 的持久版本接著提交，閘道同一輪才取到：持久版本作廢它，不再推疊加
        h.Relay(live, C1, 1, 3, Running("即時 3", 3)).Should().Be(LiveIntakeOutcome.Offered);
        await h.ItemAsync(device, C1, Running("持久 3", 3));
        await h.RoundAsync();
        var items = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "item").ToList();
        items.Should().NotBeEmpty();
        Html(items[^1]).Should().Contain("持久 3");
        h.Overlay.Count.Should().Be(0);
    }

    [Fact]
    public async Task 疊加超過上限_淘汰最舊的_改推持久版本()
    {
        await using var h = await ConversationHarness.StartAsync(settings: Settings(("Hub:Conversations:OverlayMaxOrdsPerConversation", "2")));
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        await h.ItemAsync(device, C1, Running("持久 1", 1));
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        h.Relay(live, C1, 1, 2, Running("即時 1", 2)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("即時 1");

        h.Relay(live, C1, 2, 1, Running("即時 2", 1, id: "item2", ord: 2)).Should().Be(LiveIntakeOutcome.Offered);
        h.Relay(live, C1, 3, 1, Running("即時 3", 1, id: "item3", ord: 3)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        var messages = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "item").ToList();
        messages.Select(m => m["item"]!["id"]!.Value<string>()).Should().BeEquivalentTo(["item1", "item2", "item3"]);
        Html(messages.Single(m => m["item"]!["id"]!.Value<string>() == "item1")).Should().Contain("持久 1", "每個對話最多 2 個 ord：最舊的淘汰、改推持久版本");
        h.Overlay.Count.Should().Be(2);
        h.Overlay.Covers(C1, 1, "item1").Should().BeFalse();
    }

    [Fact]
    public async Task 收件之後政策降級_閘道取走時丟掉()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        h.Relay(live, C1, 1, 1, Running("降級之前收件的", 1)).Should().Be(LiveIntakeOutcome.Offered);
        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta("標題", ConversationSyncPolicies.Paused), C1));
        await h.RoundAsync();
        var messages = await a.TakeAsync(0);
        messages.Should().NotContain(m => m["type"]!.Value<string>() == "item");
        messages.Should().Contain(m => m["type"]!.Value<string>() == "resync");
        h.LiveDropped(LiveDropReason.Policy).Should().Be(1, "閘道取走時再看一次目錄");
        h.Overlay.Count.Should().Be(0);
    }

    [Fact]
    public async Task 政策降級與刪除_疊加層清掉()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        var c2 = ConversationHarness.ConversationId(2);
        await h.CreateAsync(device, C1);
        await h.CreateAsync(device, c2);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        await h.OpenAsync(a, c2);
        h.Relay(live, C1, 1, 1, Running("要降級的", 1)).Should().Be(LiveIntakeOutcome.Offered);
        h.Relay(live, c2, 1, 1, Running("要刪除的", 1)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        await a.TakeAsync(2);
        h.Overlay.Count.Should().Be(2);

        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta("標題", ConversationSyncPolicies.Metadata), C1), (K.Deleted, new JObject(), c2));
        await h.RoundAsync();
        h.Overlay.Count.Should().Be(0, "答 4：降級與刪除時清掉疊加層");
        h.Overlay.MarkOf(C1, 1).Should().BeNull();
        var messages = await a.TakeAsync(0);
        messages.Should().NotContain(m => m["type"]!.Value<string>() == "item" || m["type"]!.Value<string>() == "removeItem", "resync／conversationClosed 之後不推");
    }

    [Fact]
    public async Task 項目移除_疊加層清掉()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        await h.ItemAsync(device, C1, Running("持久 2", 1, id: "item2", ord: 2));
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        h.Relay(live, C1, 2, 2, Running("即時 2", 2, id: "item2", ord: 2)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("即時 2");

        await h.PublishAsync(device, (K.ItemRemoved, ConversationEventLogTests.Removal("item2"), C1));
        await h.RoundAsync();
        var messages = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() is "item" or "removeItem").ToList();
        messages.Should().ContainSingle().Which["type"]!.Value<string>().Should().Be("removeItem");
        h.Overlay.Count.Should().Be(0);
    }

    [Fact]
    public async Task 變更通知溢出_疊加層清空_重開後只有持久版本()
    {
        await using var h = await ConversationHarness.StartAsync(settings: Settings(("Hub:Conversations:ChangeFeedMaxEntries", "2")));
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        await h.ItemAsync(device, C1, Running("持久 1", 1));
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        h.Relay(live, C1, 1, 2, Running("即時 1", 2)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("即時 1");

        // 漏掉的持久變更可能已經作廢疊加：整份重讀時清空，開著的對話重新 open
        await h.PublishAsync(device, Enumerable.Range(2, 4).Select(i => (K.Item, (JToken)Running("持久 " + i, 1, id: "item" + i, ord: i), C1)).ToArray());
        await h.RoundAsync();
        await h.RoundAsync();
        var messages = await a.TakeAsync(0);
        var at = messages.FindLastIndex(m => m["type"]!.Value<string>() == "conversation");
        at.Should().BeGreaterThanOrEqualTo(0);
        messages.Skip(at + 1).Should().NotContain(m => m["type"]!.Value<string>() == "item", "重開之後沒有疊加可推");
        h.Overlay.Count.Should().Be(0);
    }

    [Fact]
    public async Task 持久版本Waiting_清掉疊加_之後的即時更新照收()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        h.Relay(live, C1, 1, 9, Running("即時 9", 9)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        await a.NextAsync();
        await h.ItemAsync(device, C1, ConversationSamples.Item("等核准", "Waiting", ver: 2));
        await h.RoundAsync();
        Html((await a.TakeAsync(0)).Single(m => m["type"]!.Value<string>() == "item")).Should().Contain("等核准");
        h.Overlay.Count.Should().Be(0, "Waiting 不是 Running：清掉、不比 ver");

        h.Relay(live, C1, 1, 10, Running("核准之後", 10)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("核准之後", "Waiting 不是 final：之後的即時更新照收");
    }

    [Fact]
    public async Task 一輪結束_疊加層清掉_觀看者收到持久版本()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        await h.ItemAsync(device, C1, Running("持久的", 1));
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        h.Relay(live, C1, 1, 2, Running("即時的", 2)).Should().Be(LiveIntakeOutcome.Offered);
        h.Relay(live, C1, 2, 1, Running("只有即時的", 1, id: "item2", ord: 2)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        (await a.TakeAsync(2)).Select(m => m["type"]!.Value<string>()).Should().Equal("item", "item");

        await h.PublishAsync(device, (K.Turn, ConversationEventLogTests.Turn(ConversationTurnPhases.Completed), C1));
        await h.RoundAsync();
        var messages = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() is "item" or "removeItem").ToList();
        messages.Should().HaveCount(2);
        Html(messages.Single(m => m["type"]!.Value<string>() == "item")).Should().Contain("持久的");
        messages.Single(m => m["type"]!.Value<string>() == "removeItem")["id"]!.Value<string>().Should().Be("item2", "只在即時更新出現過、沒有持久版本的項目拿掉");
        h.Overlay.Count.Should().Be(0);
    }

    [Fact]
    public async Task 同一台較新的連線Prove_較舊連線的疊加清掉_之後的也丟()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var old = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        h.Relay(old, C1, 1, 7, Running("舊連線的", 7)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("舊連線的");

        // 重連時舊連線還沒斷：新連線 Prove 就清掉舊連線的疊加（只有即時更新的項目改送 removeItem）
        h.OnlineLive(device);
        h.Relay(old, C1, 1, 8, Running("舊連線晚到的", 8)).Should().Be(LiveIntakeOutcome.Offered, "舊連線還在登記裡：收件照收");
        await h.RoundAsync();
        var messages = await a.TakeAsync(0);
        messages.Should().Contain(m => m["type"]!.Value<string>() == "removeItem");
        messages.Should().NotContain(m => m["type"]!.Value<string>() == "item", "舊連線晚到的在閘道取走時丟掉");
        h.Overlay.Count.Should().Be(0);
        h.LiveDropped(LiveDropReason.Stale).Should().Be(1);
    }

    [Fact]
    public async Task 同一台較新的只更新連線Prove_完整連線的疊加不清_之後的照收()
    {
        // 反向重疊（U2 T7 留下的）：完整連線還在，同一台較新的只更新連線 Prove 了（換版退回、舊版重開）。
        // 只更新的連線不送即時更新也不同步，不能讓完整連線的疊加被當成過時的清掉、之後的也丟
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var full = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        h.Relay(full, C1, 1, 7, Running("完整連線的", 7)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("完整連線的");

        h.Online(device, [DeviceFeatureRules.Update1], updateOnly: true);
        h.Relay(full, C1, 1, 8, Running("之後的", 8)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();

        var messages = await a.TakeAsync(0);
        messages.Should().NotContain(m => m["type"]!.Value<string>() == "removeItem");
        Html(messages.Single(m => m["type"]!.Value<string>() == "item")).Should().Contain("之後的");
        h.Overlay.Count.Should().Be(1);
        h.LiveDropped(LiveDropReason.Stale).Should().Be(0);
        h.Overlay.LatestProvenOrder(device).Should().Be(h.Registry.Get(full)!.Order, "持久版本的標記照完整連線的 Order 記");
    }

    [Fact]
    public async Task 只給開著的連線_open之後補上疊加()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        await h.ItemAsync(device, C1, Running("持久的", 1));
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");
        await h.OpenAsync(a, C1);

        h.Relay(live, C1, 1, 2, Running("即時的", 2)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("即時的");
        await b.AssertNothingAsync("沒開這個對話");

        // b 後來才開：快照（持久版本）之後接著收到疊加
        (await h.CmdAsync(b, "open", new { conversationId = C1 })).Should().BeNull();
        await h.RoundAsync();
        var messages = await b.TakeAsync(2);
        messages[0]["type"]!.Value<string>().Should().Be("conversation");
        Html(messages[0]["items"]![0]!).Should().Contain("持久的");
        messages[1]["type"]!.Value<string>().Should().Be("item");
        Html(messages[1]).Should().Contain("即時的");
        await a.AssertNothingAsync("已經收過的不重送");
    }

    [Fact]
    public async Task open之後的疊加超過佇列剩餘空間一半_不推_之後的即時更新照推()
    {
        // 合併前審查 F1：對話有很多 Running 的項目時，open 之後一次推出全部的格（每對話最多 64 格）會讓送出佇列溢出
        await using var h = await ConversationHarness.StartAsync(o => o.Browser.OutboxMaxBytes = 60_000);
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        for (var ord = 1; ord <= 12; ord++)
        {
            h.Relay(live, C1, ord, 1, Running(new string('長', 2000) + ord, 1, "item" + ord, ord)).Should().Be(LiveIntakeOutcome.Offered);
            h.Time.Advance(TimeSpan.FromMilliseconds(260));
            await h.RunAsync();
        }
        await a.TakeAsync(0);
        h.Overlay.SlotsOf(C1).Should().HaveCount(12, "前提：沒人開著也留著疊加");

        a.Block();
        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().BeNull();
        await h.RoundAsync();
        a.Connection.InitSent.Should().BeTrue("沒有溢出、不必重送 init");
        h.Counters.Snapshot().OutboxOverflows.Should().Be(0);
        a.Release();
        var messages = await a.TakeAsync(0);
        messages.Select(m => m["type"]!.Value<string>()).Should().Contain("conversation").And.NotContain("item", "12 格約 72 KB，超過剩餘空間（60 KB）的一半");

        h.Time.Advance(TimeSpan.FromMilliseconds(300));
        h.Relay(live, C1, 3, 2, Running("新的內容", 2, "item3", 3)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        Html((await a.TakeAsync(0)).Single(m => m["type"]!.Value<string>() == "item")).Should().Contain("新的內容", "之後的即時更新照推那一格");
    }

    // ───── 延遲 ─────

    [Fact]
    public async Task 即時更新50ms內合併送出()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        for (var ver = 1; ver <= 3; ver++)
            h.Relay(live, C1, 1, ver, Running("版本" + ver, ver)).Should().Be(LiveIntakeOutcome.Offered);
        h.Time.Advance(TimeSpan.FromMilliseconds(10));
        await h.RunAsync();
        await a.AssertNothingAsync("距離上一輪不到 50 ms");
        h.Time.Advance(TimeSpan.FromMilliseconds(40));
        await h.RunAsync();
        var items = (await a.TakeAsync(0)).Where(m => m["type"]!.Value<string>() == "item").ToList();
        Html(items.Should().ContainSingle().Subject).Should().Contain("版本3");
    }

    // ───── 端到端（真的裝置 hub、瀏覽器 hub、正式的閘道） ─────

    [Fact]
    public async Task 經裝置hub的LiveRelay_開著的瀏覽器收到item()
    {
        var now = DateTimeOffset.UtcNow;
        await using var host = await HubTestHost.StartAsync(clock: new ManualTimeProvider(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero)));
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (hello, _) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1]);
        hello.Features.Should().Contain(DeviceFeatureRules.Live1);
        (await ConversationEventLogTests.Append(host, device.Id, ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta()),
            ConversationEventLogTests.Ev(2, K.Item, Running("持久的", 1)))).Ack.Results.Should().OnlyContain(r => r.Rejected == null);

        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        (await owner.Hub.CmdAsync(new { cmd = "open", conversationId = ConversationSamples.Conv }))["accepted"]!.Value<bool>().Should().BeTrue();
        await owner.Hub.WaitForAsync("conversation");

        var token = ConversationSamples.GitHub();
        await client.Connection.InvokeAsync(DeviceHubMethods.LiveRelay, RelayOf(ConversationSamples.Conv, Running("裝置串流中 " + token, 2)));
        var pushed = await owner.Hub.WaitForAsync("item");
        pushed["conversationId"]!.Value<string>().Should().Be(ConversationSamples.Conv);
        Html(pushed).Should().Contain("裝置串流中").And.NotContain(token);
        host.Services.GetRequiredService<ConversationCounters>().Snapshot().RemaskedLive.Should().Be(1);
    }

    private static LiveRelay RelayOf(string conversationId, JObject item) =>
        new(conversationId, 1, item["ord"]!.Value<long>(), item["ver"]!.Value<long>(), item.ToString(Newtonsoft.Json.Formatting.None));

    /// <summary><c>item</c> 訊息（或 Item 本身）的 html</summary>
    private static string Html(JToken message) => (message["item"] ?? message)["html"]!.Value<string>()!;
}
