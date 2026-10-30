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
/// 觀看追蹤與 <c>WatchChanged</c>（WP4.2 計畫步 12；R15，D25、P4、F34）：以 <see cref="ConversationHarness"/> 的觀看（記錄的送出端、測試以
/// <see cref="ConversationWatch.FlushAsync"/> 驅動）逐步驗證；降級與刪除時疊加層與觀看清單一起清掉（答 4）；經真的裝置 hub 與瀏覽器 hub 的一則驗證接線
/// </summary>
public sealed class ConversationWatchTests
{
    private static readonly string C1 = ConversationHarness.ConversationId(1);
    private static readonly string C2 = ConversationHarness.ConversationId(2);

    private static JObject Running(string text, long ver, string id = "item1", long ord = 1) => ConversationSamples.Item(text, "Running", id, ord, ver);

    [Fact]
    public async Task 沒有live1_收不到WatchChanged()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var conv = h.Online(device, [DeviceFeatureRules.Conv1]);
        var none = h.Online(device);
        var live = h.OnlineLive(device);
        // 已 Hello、還沒 Prove 的連線（協商出 live1）
        var unproven = new DeviceConnection("dc-unproven", device, () => { }, new UpstreamContext(device, null, null), [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1]);
        h.Registry.Add(unproven, h.Host.Clock.NowMs);
        var (a, _) = await h.ReadyAsync("b1");
        h.SetState(a, true, [C1]);

        await h.Watch.FlushAsync();

        h.Watch.WatchedBy(device).Should().Equal(C1);
        h.DeviceSender.Watches(live).Should().NotBeEmpty().And.Subject.Last().Should().Equal(C1);
        h.DeviceSender.Watches(conv).Should().BeEmpty("只有 conv1、沒協商出 live1（DeviceFeatureRules.RequiredFor）");
        h.DeviceSender.Watches(none).Should().BeEmpty("第 3 階段的桌面");
        h.DeviceSender.Watches(unproven.ConnectionId).Should().BeEmpty("還沒 Prove");
    }

    [Fact]
    public async Task ConnectionProven後送一次()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        h.SetState(a, true, [C1]);
        await h.Watch.FlushAsync();
        h.DeviceSender.All.Should().BeEmpty("擁有者不在線");
        h.Watch.WatchedBy(device).Should().Equal(new[] { C1 }, "清單照算（擁有者上線時送）");

        var live = h.OnlineLive(device);
        h.DeviceSender.All.Should().BeEmpty("不在 ConnectionProven 的事件裡同步送（F34：那時 ProveResponse 還沒送到裝置）");
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Should().ContainSingle().Which.Should().Equal(C1);
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Should().ContainSingle("內容沒變、沒有新的 Prove：不重送");

        // 沒人看的裝置 Prove 也送一次空的清單（整份取代：清掉上一條連線留在桌面的）
        var other = await h.AddDeviceAsync("另一台");
        var otherLive = h.OnlineLive(other);
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(otherLive).Should().ContainSingle().Which.Should().BeEmpty();

        // 同一台再開一條：只送新的那條；Prove 之後、送出之前就斷了的不送
        var second = h.OnlineLive(device);
        var gone = h.OnlineLive(device);
        h.Offline(gone);
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(second).Should().ContainSingle().Which.Should().Equal(C1);
        h.DeviceSender.Watches(live).Should().ContainSingle();
        h.DeviceSender.Watches(gone).Should().BeEmpty();
    }

    [Fact]
    public async Task 內容沒變不送()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        await h.CreateAsync(device, C2);
        var live = h.OnlineLive(device);
        var (a, _) = await h.ReadyAsync("b1");
        h.SetState(a, true, []);
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Should().ContainSingle().Which.Should().BeEmpty("Prove 之後的那一則");
        (await h.CmdAsync(a, "visible", new { conversationIds = new[] { C1 } })).Should().BeNull();
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Last().Should().Equal(new[] { C1 }, "visible 指令的清單也觸發重算");
        h.DeviceSender.Clear();

        h.SetState(a, true, [C1], focused: false);
        (await h.CmdAsync(a, "visible", new { conversationIds = new[] { C1, C1 } })).Should().BeNull();
        await h.ItemAsync(device, C1, Running("新的一則", 1));
        await h.Watch.FlushAsync();
        h.DeviceSender.All.Should().BeEmpty("焦點、重複的 id、對話有新項目都不改變清單");

        (await h.CmdAsync(a, "visible", new { conversationIds = new[] { C2, C1 } })).Should().BeNull();
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Should().ContainSingle().Which.Should().Equal(C1, C2);
        h.SetState(a, false, [C1]);
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Last().Should().BeEmpty("分頁到背景：整份取代成空的");
        h.DeviceSender.Watches(live).Should().HaveCount(2);
    }

    [Fact]
    public async Task 瀏覽器斷線_清單變短()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        await h.CreateAsync(device, C2);
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");
        h.SetState(a, true, [C1]);
        h.SetState(b, true, [C1, C2]);
        var live = h.OnlineLive(device);
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Should().ContainSingle().Which.Should().Equal(C1, C2);

        h.Connections.Remove("b2");
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Last().Should().Equal(new[] { C1 }, "另一條還看得到 C1");
        h.Connections.Remove("b1");
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Last().Should().BeEmpty();
        h.DeviceSender.Watches(live).Should().HaveCount(3);
        h.Watch.WatchedBy(device).Should().BeEmpty();
    }

    [Fact]
    public async Task 改成metadata_疊加與WatchChanged也清掉()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        await h.CreateAsync(device, C2);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        h.SetState(a, true, [C1, C2]);
        h.Relay(live, C1, 1, 1, Running("即時", 1)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        h.Overlay.SlotsOf(C1).Should().ContainSingle();
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Last().Should().Equal(C1, C2);

        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta("標題", ConversationSyncPolicies.Metadata), C1));
        await h.RoundAsync();
        await h.Watch.FlushAsync();

        h.Overlay.SlotsOf(C1).Should().BeEmpty("答 4：降級時清掉疊加層");
        h.DeviceSender.Watches(live).Last().Should().Equal(new[] { C2 }, "擁有者不再送它的即時更新");
        h.Relay(live, C1, 1, 2, Running("即時 2", 2)).Should().Be(LiveIntakeOutcome.Dropped, "降級之後還在路上的即時更新丟掉");

        await h.PublishAsync(device, (K.Meta, ConversationSamples.Meta("標題"), C1));
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Last().Should().Equal(new[] { C1, C2 }, "改回 full 又看得到");
    }

    [Fact]
    public async Task deleted_疊加與WatchChanged也清掉()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var live = h.OnlineLive(device);
        await h.CreateAsync(device, C1);
        await h.CreateAsync(device, C2);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);
        h.SetState(a, true, [C1, C2]);
        h.Relay(live, C1, 1, 1, Running("即時", 1)).Should().Be(LiveIntakeOutcome.Offered);
        await h.RoundAsync();
        h.Overlay.SlotsOf(C1).Should().ContainSingle();
        await h.Watch.FlushAsync();

        await h.PublishAsync(device, (K.Deleted, new JObject(), C1));
        await h.RoundAsync();
        await h.Watch.FlushAsync();

        h.Overlay.SlotsOf(C1).Should().BeEmpty();
        h.Overlay.MarkOf(C1, 1).Should().BeNull();
        h.DeviceSender.Watches(live).Last().Should().Equal(new[] { C2 }, "網頁的 openConversationIds 還留著刪除的對話，照樣不列");
        h.Watch.WatchedBy(device).Should().Equal(C2);
    }

    [Fact]
    public async Task 送失敗只記錄_之後照常送()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive(device);
        var (a, _) = await h.ReadyAsync("b1");
        h.DeviceSender.FailWith = new InvalidOperationException("連線剛好斷了");
        h.SetState(a, true, [C1]);

        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Should().ContainSingle("Prove 與清單改變在同一次：只送一則最新的").Which.Should().Equal(C1);

        h.DeviceSender.FailWith = null;
        h.SetState(a, true, []);
        await h.Watch.FlushAsync();
        h.DeviceSender.Watches(live).Last().Should().BeEmpty();
    }

    // ───── 端到端（真的裝置 hub、瀏覽器 hub、正式的觀看追蹤） ─────

    [Fact]
    public async Task 經裝置hub_Prove之後收到WatchChanged_瀏覽器看得到與離開時更新_沒有live1的收不到()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host, "新版");
        using var legacy = await TestDevice.AddAsync(host, "舊版");
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        await using var old = await DeviceHubClient.ConnectAsync(host, legacy.Token);
        await old.HandshakeAsync(legacy, DeviceHubClient.ObservedSpki(host), features: [DeviceFeatureRules.Conv1]);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1]);
        (await client.WaitForAsync<WatchChanged>()).ConversationIds.Should().BeEmpty("Prove 之後送一次目前的清單");
        var mine = ConversationSamples.Conv;
        var theirs = ConversationHarness.ConversationId(7);
        (await ConversationEventLogTests.Append(host, device.Id, ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta()))).Ack.Results
            .Should().OnlyContain(r => r.Rejected == null);
        (await ConversationEventLogTests.Append(host, legacy.Id, ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta(), conversationId: theirs))).Ack.Results
            .Should().OnlyContain(r => r.Rejected == null);

        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        await owner.Hub.Connection.InvokeAsync(BrowserHubMethods.ClientState, new JObject
        {
            ["visible"] = true,
            ["focused"] = true,
            ["openConversationIds"] = new JArray(mine, theirs),
            ["tzOffsetMinutes"] = -480,
        });
        (await client.WaitForAsync<WatchChanged>()).ConversationIds.Should().Equal(mine);

        await owner.DisposeAsync();
        (await client.WaitForAsync<WatchChanged>()).ConversationIds.Should().BeEmpty("瀏覽器斷線");
        await Task.Delay(200);
        var received = new List<object>();
        while (old.Received.Reader.TryRead(out var message))
            received.Add(message);
        received.Should().NotContain(m => m is WatchChanged, "沒協商出 live1 的連線一則都不送");
    }
}
