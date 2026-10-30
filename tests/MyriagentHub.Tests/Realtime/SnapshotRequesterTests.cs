using Myriagent.Sync;
using MyriagentHub.Data;
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
/// 向擁有者要整份內容（WP4.2 計畫步 12；R16，D26、D16 的 restore 後備、P3）：open 的條件與冷卻、restore 的寬限、每秒上限、去重與重連。
/// 以 <see cref="ConversationHarness"/> 的快照請求（記錄的送出端、測試時鐘；restore 以 <see cref="SnapshotRequester.TickAsync"/> 驅動）驗證；
/// 經真的裝置 hub 與瀏覽器 hub 的一則驗證接線
/// </summary>
public sealed class SnapshotRequesterTests
{
    private static readonly string C1 = ConversationHarness.ConversationId(1);
    private static readonly string C2 = ConversationHarness.ConversationId(2);

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(300);

    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(500);

    // ───── open（D26） ─────

    [Fact]
    public async Task open空的full對話_送open()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var conv = h.Online(device, [DeviceFeatureRules.Conv1]);
        var c3 = ConversationHarness.ConversationId(3);
        await h.CreateAsync(device, C1);
        await h.SeedAsync(device, C2, 1);
        await h.CreateAsync(device, c3);
        var (a, _) = await h.ReadyAsync("b1");

        await h.OpenAsync(a, C1);
        await h.OpenAsync(a, C2);
        (await h.CmdAsync(a, "loadOlder", new { conversationId = c3, beforeId = "item1" })).Should().BeNull();

        h.DeviceSender.Snapshots(conv).Should().Equal(new[] { new SnapshotRequest(C1, SnapshotReasons.Open) }, "C2 已經有項目；loadOlder 不算 open");
        h.DeviceSender.Snapshots().Should().ContainSingle();
    }

    [Fact]
    public async Task _10分鐘內不重送()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var conv = h.Online(device, [DeviceFeatureRules.Conv1]);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");
        var (b, _) = await h.ReadyAsync("b2");

        // open 指令當下就決定（閘道回 conversation 之前）；這裡不跑閘道，時間只照測試撥的走
        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().BeNull();
        (await h.CmdAsync(b, "open", new { conversationId = C1 })).Should().BeNull();
        h.Time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromMilliseconds(1));
        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().BeNull();
        h.DeviceSender.Snapshots(conv).Should().ContainSingle("同一個對話 10 分鐘內只送一次（不分連線）");

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        (await h.CmdAsync(b, "open", new { conversationId = C1 })).Should().BeNull();
        h.DeviceSender.Snapshots(conv).Should().HaveCount(2, "冷卻剛好過了");
        h.Time.Advance(TimeSpan.FromMinutes(3));
        await h.Snapshots.TickAsync();
        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().BeNull();
        h.DeviceSender.Snapshots(conv).Should().HaveCount(2, "重新計時（清掉過期記錄的那一輪不影響還在冷卻的）");
    }

    [Fact]
    public async Task metadata對話不送()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var conv = h.Online(device, [DeviceFeatureRules.Conv1]);
        await h.CreateAsync(device, C1, policy: ConversationSyncPolicies.Metadata);
        await h.CreateAsync(device, C2, policy: ConversationSyncPolicies.Paused);
        var (a, _) = await h.ReadyAsync("b1");

        await h.OpenAsync(a, C1);
        await h.OpenAsync(a, C2);

        h.DeviceSender.Snapshots(conv).Should().BeEmpty("只同步標題的對話伺服器本來就沒有內容");
    }

    [Fact]
    public async Task 沒有conv1_收不到()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var none = h.Online(device);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");

        await h.OpenAsync(a, C1);
        h.DeviceSender.All.Should().BeEmpty("第 3 階段的桌面不認得 SnapshotRequest（DeviceFeatureRules.RequiredFor）");

        // 擁有者上線時閘道會重送開著的對話的標頭：之後直接送 open 指令（請求在指令當下決定）
        var liveOnly = h.Online(device, [DeviceFeatureRules.Live1]);
        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().BeNull();
        h.DeviceSender.All.Should().BeEmpty("看的是最新的那條：只有 live1");

        var conv = h.Online(device, [DeviceFeatureRules.Conv1]);
        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().BeNull();
        h.DeviceSender.Snapshots(conv).Should().ContainSingle("沒送過的不佔冷卻");
        h.DeviceSender.Snapshots(none).Should().BeEmpty();
        h.DeviceSender.Snapshots(liveOnly).Should().BeEmpty();
    }

    [Fact]
    public async Task 同一台較新的只更新連線_open照樣送給完整連線()
    {
        // 反向重疊（U2 T7 留下的）：完整連線還在，較新的只更新連線 Prove 了。原本挑「最新的已 Prove 連線」挑到只更新的那條、它沒有 conv1 就不送
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var conv = h.Online(device, [DeviceFeatureRules.Conv1]);
        var updateOnly = h.Online(device, [DeviceFeatureRules.Update1], updateOnly: true);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");

        await h.OpenAsync(a, C1);

        h.DeviceSender.Snapshots(conv).Should().Equal(new SnapshotRequest(C1, SnapshotReasons.Open));
        h.DeviceSender.Snapshots(updateOnly).Should().BeEmpty();
    }

    [Fact]
    public async Task 擁有者不在線不送()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        var other = await h.AddDeviceAsync("另一台");
        var otherConv = h.Online(other, [DeviceFeatureRules.Conv1]);
        await h.CreateAsync(device, C1);
        var (a, _) = await h.ReadyAsync("b1");

        await h.OpenAsync(a, C1);
        h.DeviceSender.All.Should().BeEmpty("擁有者不在線（別台在線不算）");

        var conv = h.Online(device, [DeviceFeatureRules.Conv1]);
        (await h.CmdAsync(a, "open", new { conversationId = C1 })).Should().BeNull();
        h.DeviceSender.Snapshots(conv).Should().ContainSingle("擁有者上線之後照常送（沒送出的不佔冷卻）");
        h.DeviceSender.Snapshots(otherConv).Should().BeEmpty();
    }

    // ───── restore（後備；D16、D26） ─────

    [Fact]
    public async Task Prove之後寬限內不送restore()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        await MarkRecoveringAsync(h, device);
        await h.CreateAsync(device, C2);
        h.Directory.TryGet(C2, out var fresh).Should().BeTrue();
        fresh!.Recovering.Should().BeFalse("還原之後才建立的");
        var conv = h.Online(device, [DeviceFeatureRules.Conv1]);
        var none = h.Online(device);

        h.Time.Advance(Grace - TimeSpan.FromMilliseconds(1));
        await h.Snapshots.TickAsync();
        h.DeviceSender.All.Should().BeEmpty("Prove 之後 300 秒內是桌面自己整份重新發布的時間（主路）");

        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await h.Snapshots.TickAsync();
        h.DeviceSender.Snapshots(conv).Should().Equal(new SnapshotRequest(C1, SnapshotReasons.Restore));
        h.DeviceSender.Snapshots(none).Should().BeEmpty("較新的那條沒有 conv1：送給協商出 conv1 的那條");

        await h.Snapshots.TickAsync();
        h.Time.Advance(TimeSpan.FromMinutes(5));
        await h.Snapshots.TickAsync();
        h.DeviceSender.Snapshots().Should().ContainSingle("同一個對話只送一次（還沒解除也一樣）；不是 recovering 的不送");
    }

    [Fact]
    public async Task 寬限之後每秒最多2則()
    {
        await using var h = await ConversationHarness.StartAsync();
        var a = await h.AddDeviceAsync("甲");
        var b = await h.AddDeviceAsync("乙");
        await CreateManyAsync(h, a, 1, 10);
        await CreateManyAsync(h, b, 101, 10);
        await MarkRecoveringAsync(h, a, b);
        var connA = h.Online(a, [DeviceFeatureRules.Conv1]);
        var connB = h.Online(b, [DeviceFeatureRules.Conv1]);
        var sentAt = new List<(string ConnectionId, long AtMs)>();
        h.DeviceSender.OnSent = (connectionId, _) => sentAt.Add((connectionId, h.Time.GetTimestamp() / 1000));

        h.Time.Advance(Grace);
        for (var step = 0; step < 30; step++)
        {
            await h.Snapshots.TickAsync();
            await h.Snapshots.TickAsync();
            h.Time.Advance(TimeSpan.FromMilliseconds(100));
        }

        foreach (var connection in new[] { connA, connB })
        {
            var times = sentAt.Where(s => s.ConnectionId == connection).Select(s => s.AtMs).ToList();
            times.Should().HaveCount(6, "3 秒：0、0.5、…、2.5 秒各一則");
            times.Zip(times.Skip(1), (x, y) => y - x).Should().OnlyContain(gap => gap >= 500, "每台兩則之間至少 500 ms（每秒最多 2 則）");
        }
        h.DeviceSender.Snapshots(connA).Select(r => r.ConversationId).Should().Equal(Enumerable.Range(1, 6).Select(ConversationHarness.ConversationId), "依 id 逐一送");
        h.DeviceSender.Snapshots(connB).Select(r => r.ConversationId).Should().Equal(Enumerable.Range(101, 6).Select(ConversationHarness.ConversationId), "各台分開計");
    }

    [Fact]
    public async Task _600個recovering_在有限時間內全部送出一次_解除後不再送()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await CreateManyAsync(h, device, 1, 600);
        await MarkRecoveringAsync(h, device);
        h.Counters.Snapshot().Recovering.Should().Be(600);
        var conv = h.Online(device, [DeviceFeatureRules.Conv1]);
        // 擁有者每收到一則就整份重新發布那個對話（conv.created 先送：解除 recovering，D16）
        var asked = new Queue<string>();
        h.DeviceSender.OnSent = (_, message) => asked.Enqueue(((SnapshotRequest)message).ConversationId);

        for (var elapsed = TimeSpan.Zero; elapsed < Grace + Grace; elapsed += Pace)
        {
            h.Time.Advance(Pace);
            await h.Snapshots.TickAsync();
            while (asked.TryDequeue(out var conversationId))
                await h.CreateAsync(device, conversationId);
        }

        h.Counters.Snapshot().Recovering.Should().Be(0, "300＋300 秒內全部解除");
        var requests = h.DeviceSender.Snapshots(conv);
        requests.Should().HaveCount(600).And.OnlyContain(r => r.Reason == SnapshotReasons.Restore);
        requests.Select(r => r.ConversationId).Should().OnlyHaveUniqueItems("每個對話只收到一次");
        h.Snapshots.RestoreSentCount.Should().Be(600);

        for (var i = 0; i < 20; i++)
        {
            h.Time.Advance(Pace);
            await h.Snapshots.TickAsync();
        }
        h.DeviceSender.Snapshots(conv).Should().HaveCount(600, "解除之後不再送");
    }

    [Fact]
    public async Task 重連之後不重複送已送過的()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await CreateManyAsync(h, device, 1, 4);
        await MarkRecoveringAsync(h, device);
        var first = h.Online(device, [DeviceFeatureRules.Conv1]);
        h.Time.Advance(Grace);
        await h.Snapshots.TickAsync();
        h.Time.Advance(Pace);
        await h.Snapshots.TickAsync();
        h.DeviceSender.Snapshots(first).Select(r => r.ConversationId).Should().Equal(ConversationHarness.ConversationId(1), ConversationHarness.ConversationId(2));

        h.Offline(first);
        h.Time.Advance(Pace);
        await h.Snapshots.TickAsync();
        h.DeviceSender.Snapshots().Should().HaveCount(2, "連線斷了就停");

        var second = h.Online(device, [DeviceFeatureRules.Conv1]);
        h.Time.Advance(Grace - TimeSpan.FromMilliseconds(1));
        await h.Snapshots.TickAsync();
        h.DeviceSender.Snapshots().Should().HaveCount(2, "下次 Prove 重新計時");
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await h.Snapshots.TickAsync();
        h.DeviceSender.Snapshots(second).Select(r => r.ConversationId).Should().Equal(new[] { ConversationHarness.ConversationId(3) }, "已送過的（還沒解除也一樣）不再送");

        // 重連的重疊：新的連線先上來、舊的還沒斷：只看最新的那條（還在寬限內）
        var third = h.Online(device, [DeviceFeatureRules.Conv1]);
        h.Time.Advance(Pace);
        await h.Snapshots.TickAsync();
        h.DeviceSender.Snapshots().Should().HaveCount(3, "最新的那條還在寬限內，較舊的那條不再送");
        h.Offline(second);
        for (var i = 0; i < 4; i++)
        {
            h.Time.Advance(Grace / 2);
            await h.Snapshots.TickAsync();
        }
        h.DeviceSender.Snapshots(third).Select(r => r.ConversationId).Should().Equal(new[] { ConversationHarness.ConversationId(4) });
        h.DeviceSender.Snapshots().Select(r => r.ConversationId).Should().OnlyHaveUniqueItems().And.HaveCount(4);
    }

    // ───── 端到端（真的裝置 hub、瀏覽器 hub、正式的快照請求） ─────

    [Fact]
    public async Task 經瀏覽器hub_open空的對話_擁有者收到SnapshotRequest()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), features: [DeviceFeatureRules.Conv1]);
        (await ConversationEventLogTests.Append(host, device.Id, ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta()))).Ack.Results
            .Should().OnlyContain(r => r.Rejected == null);

        await using var owner = await LoginAndConnectAsync(host);
        await owner.Hub.ReadyAsync();
        (await owner.Hub.CmdAsync(new { cmd = "open", conversationId = ConversationSamples.Conv }))["accepted"]!.Value<bool>().Should().BeTrue();

        var request = await client.WaitForAsync<SnapshotRequest>();
        request.Should().Be(new SnapshotRequest(ConversationSamples.Conv, SnapshotReasons.Open));
    }

    /// <summary>conversationId(first)…(first+count-1) 各一個（只有 meta、沒有項目），每批 100 個</summary>
    private static async Task CreateManyAsync(ConversationHarness h, string deviceId, int first, int count)
    {
        for (var start = first; start < first + count; start += 100)
        {
            var events = Enumerable.Range(start, Math.Min(100, first + count - start))
                .Select(n => (K.Created, (JToken)ConversationSamples.Meta("對話 " + n), ConversationHarness.ConversationId(n)))
                .ToArray();
            await h.PublishAsync(deviceId, events);
        }
    }

    /// <summary>還原的標記（同 RestoreService：沒刪除的都標成 recovering），目錄快取換上標記過的列（還原之後的服務啟動時載入的就是這樣）</summary>
    private static async Task MarkRecoveringAsync(ConversationHarness h, params string[] deviceIds)
    {
        await h.Host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute("UPDATE conversations SET lease_state = 'recovering' WHERE deleted_at IS NULL"));
        foreach (var entry in deviceIds.SelectMany(h.Directory.OwnedBy).ToList())
            h.Directory.Apply(entry with { Recovering = true });
    }
}
