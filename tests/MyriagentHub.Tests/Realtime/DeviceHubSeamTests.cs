using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Ops;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using MyriagentHub.Trust;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 裝置 hub 的第 4 階段接縫（WP4.1 步 12，D45、D50、D51、D58；sync-protocol 第 4 節、14.1）：方法允許清單、Hello 的功能宣告與協商、
/// device.upsert 的 features、連線事件。WP4.2 起正式程式宣告 conv1、live1、cmd1、media1，U2 再加 update1；
/// 以「伺服器只宣告測試自己登記的功能」為前提的測試，以 HubTestHost 的 withoutDeviceFeatures 拿掉正式的宣告
/// </summary>
public sealed class DeviceHubSeamTests
{
    /// <summary>
    /// 允許清單裡、hub 上還沒有方法的（呼叫會是 SignalR 的「方法不存在」）：第 5 階段的交接方法（WP5.1 只立名稱，WP5.5 在 Realtime/DeviceHub.Handoff.cs 加上，
    /// 那時這裡改回空的）。LiveRelay 在 WP4.2 步 11、CommandResult／CommandOutput 在 WP4.5 步 7 加上
    /// </summary>
    private static readonly string[] NotYetImplemented =
    [
        DeviceHubMethods.HandoffPreview, DeviceHubMethods.HandoffStart, DeviceHubMethods.HandoffCancel, DeviceHubMethods.HandoffProgress, DeviceHubMethods.HandoffReady,
    ];

    /// <summary>hub 上的公開方法、但刻意不在允許清單的（明列；目前沒有）</summary>
    private static readonly string[] Excluded = [];

    [Fact]
    public void DeviceHub的每個公開方法都在允許清單或明列排除_清單裡的方法都已實作()
    {
        // 同 SignalR 找 hub 方法的規則：公開的執行個體方法、不是屬性存取子，而且不是 Hub／object 本身的（OnConnectedAsync 這類覆寫不算）
        var hubMethods = typeof(DeviceHub).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => !m.IsSpecialName && m.GetBaseDefinition().DeclaringType == typeof(DeviceHub))
            .Select(m => m.Name)
            .ToList();
        hubMethods.Should().OnlyHaveUniqueItems("hub 方法不能多載");
        hubMethods.Should().Contain(DeviceHubMethods.Hello).And.Contain(DeviceHubMethods.PublishBatch, "確認讀到的是 hub 方法");

        hubMethods.Except(Excluded).Should().BeSubsetOf(DeviceHubMethods.DeviceToServer, "partial 檔新增的公開方法要一起改允許清單（DeviceHubMethods.DeviceToServer）");
        DeviceHubMethods.DeviceToServer.Except(hubMethods).Should().BeEquivalentTo(NotYetImplemented);
        DeviceHubMethods.DeviceToServer.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(DeviceHubMethods.Hello, false, null)]
    [InlineData(DeviceHubMethods.Prove, false, null)]
    [InlineData(DeviceHubMethods.Hello, true, null)]
    [InlineData(DeviceHubMethods.PublishBatch, false, SyncErrorCodes.NotProven)]
    [InlineData(DeviceHubMethods.PublishBatch, true, null)]
    [InlineData(DeviceHubMethods.RefreshAccessToken, false, SyncErrorCodes.NotProven)]
    [InlineData(DeviceHubMethods.CommandResult, false, SyncErrorCodes.NotProven)]
    [InlineData(DeviceHubMethods.CommandResult, true, null)]
    [InlineData("OnConnectedAsync", true, SyncErrorCodes.Invalid)]
    [InlineData("hello", false, SyncErrorCodes.Invalid)]
    [InlineData("Notify", true, SyncErrorCodes.Invalid)]
    [InlineData("CommandDelivery", true, SyncErrorCodes.Invalid)]
    [InlineData("", true, SyncErrorCodes.Invalid)]
    [InlineData(null, true, SyncErrorCodes.Invalid)]
    public void 呼叫的關卡_清單外一律invalid_Prove之前只能交握(string? method, bool proven, string? expected) =>
        DeviceHubRules.CheckInvocation(method, proven).Should().Be(expected);

    [Fact]
    public async Task 關卡的過濾器_清單外的方法丟invalid_Prove前丟not_proven_放行的交給下一個()
    {
        var filter = new DeviceHubFilter();
        var context = new FakeCallerContext();
        var calls = 0;
        ValueTask<object?> Next(HubInvocationContext _)
        {
            calls++;
            return ValueTask.FromResult<object?>("ok");
        }
        HubInvocationContext Invocation(string method) =>
            new(context, new ServiceCollection().BuildServiceProvider(), new FakeHub(), typeof(FakeHub).GetMethod(method)!, []);

        await FluentActions.Awaiting(async () => await filter.InvokeMethodAsync(Invocation(nameof(FakeHub.Pull)), Next))
            .Should().ThrowAsync<HubException>().WithMessage(SyncErrorCodes.NotProven);
        (await filter.InvokeMethodAsync(Invocation(nameof(FakeHub.Hello)), Next)).Should().Be("ok");
        DeviceSession.SetProven(context);
        (await filter.InvokeMethodAsync(Invocation(nameof(FakeHub.Pull)), Next)).Should().Be("ok");
        await FluentActions.Awaiting(async () => await filter.InvokeMethodAsync(Invocation(nameof(FakeHub.Debug)), Next))
            .Should().ThrowAsync<HubException>().WithMessage(SyncErrorCodes.Invalid, "已證明也一樣：不在允許清單的方法一律不放行");
        calls.Should().Be(2);
    }

    [Fact]
    public void 允許清單的每個方法_Prove之後都放行_之前只放行Hello與Prove()
    {
        foreach (var method in DeviceHubMethods.DeviceToServer)
        {
            DeviceHubRules.CheckInvocation(method, proven: true).Should().BeNull(method);
            DeviceHubRules.CheckInvocation(method, proven: false).Should().Be(method is DeviceHubMethods.Hello or DeviceHubMethods.Prove ? null : SyncErrorCodes.NotProven, method);
        }
        foreach (var method in DeviceHubMethods.ServerToDevice)
            DeviceHubRules.CheckInvocation(method, proven: true).Should().Be(SyncErrorCodes.Invalid, "伺服器→裝置的方法不能由裝置呼叫：" + method);
    }

    [Fact]
    public void Hello的features_上限與字元_null與重複與不認得的名稱照收()
    {
        using var device = TestDevice.Paired(SyncIds.NewDeviceId(), "chd_x", System.Security.Cryptography.ECDsa.Create());
        var ok = DeviceHubClient.HelloRequest(device, Base64UrlRules.Encode(new byte[32]), HandshakeRules.NewNonce());
        DeviceHubRules.CheckHello(ok).Should().BeNull("沒宣告（第 3 階段）");
        DeviceHubRules.CheckHello(ok with { Features = [] }).Should().BeNull();
        DeviceHubRules.CheckHello(ok with { Features = ["conv1", "conv1", "future9"] }).Should().BeNull("重複的與這版不認得的照收（協商時去掉、不啟用）");
        DeviceHubRules.CheckHello(ok with { Features = Enumerable.Range(0, 32).Select(i => "f" + i).ToList() }).Should().BeNull("32 項");
        DeviceHubRules.CheckHello(ok with { Features = [new string('a', 32)] }).Should().BeNull("32 字");

        DeviceHubRules.CheckHello(ok with { Features = Enumerable.Range(0, 33).Select(i => "f" + i).ToList() }).Should().NotBeNull("33 項");
        DeviceHubRules.CheckHello(ok with { Features = [new string('a', 33)] }).Should().NotBeNull("33 字");
        DeviceHubRules.CheckHello(ok with { Features = ["Conv1"] }).Should().NotBeNull("大寫");
        DeviceHubRules.CheckHello(ok with { Features = [""] }).Should().NotBeNull("空字串");
        DeviceHubRules.CheckHello(ok with { Features = ["conv-1"] }).Should().NotBeNull("連字號");
        DeviceHubRules.CheckHello(ok with { Features = [null!] }).Should().NotBeNull("null 元素");
    }

    [Fact]
    public void 伺服器的功能宣告_去重_不合格或超過上限丟例外_沒有登記是null()
    {
        new DeviceHubFeatures([]).Declared.Should().BeNull("WP4.1：一項都沒有，HelloResponse 不帶 features");
        new DeviceHubFeatures([new(DeviceFeatureRules.Cmd1), new(DeviceFeatureRules.Conv1), new(DeviceFeatureRules.Cmd1)]).Declared.Should()
            .Equal(new[] { DeviceFeatureRules.Cmd1, DeviceFeatureRules.Conv1 }, "依登記的順序、去重");
        FluentActions.Invoking(() => new DeviceHubFeatures([new("Conv1")])).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => new DeviceHubFeatures(Enumerable.Range(0, 33).Select(i => new DeviceHubFeature("f" + i)))).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void 連線允許的方法_第3階段的一律可以_第4階段的要協商出對應的功能()
    {
        var none = new DeviceConnection("c1", "d1", () => { }, new UpstreamContext("d1", null, null));
        none.Features.Should().BeEmpty();
        foreach (var method in DeviceHubMethods.DeviceToServer.Concat(DeviceHubMethods.ServerToDevice))
            none.Allows(method).Should().Be(DeviceFeatureRules.RequiredFor(method) is null, method);

        var cmd = new DeviceConnection("c2", "d1", () => { }, new UpstreamContext("d1", null, null), [DeviceFeatureRules.Cmd1]);
        cmd.Allows(DeviceHubMethods.CommandDelivery).Should().BeTrue();
        cmd.Allows(DeviceHubMethods.CommandResult).Should().BeTrue();
        cmd.Allows(DeviceHubMethods.CommandOutput).Should().BeTrue();
        cmd.Allows(DeviceHubMethods.WatchChanged).Should().BeFalse();
        cmd.Allows(DeviceHubMethods.SnapshotRequest).Should().BeFalse();
        cmd.Allows(DeviceHubMethods.MediaRequest).Should().BeFalse();
        cmd.Allows(DeviceHubMethods.Notify).Should().BeTrue();
        FluentActions.Invoking(() => cmd.Allows("Unknown")).Should().Throw<ArgumentOutOfRangeException>("新方法一定要決定功能");
    }

    [Fact]
    public void 連線登記_功能取最新的那條_事件各觸發一次_處理者丟例外不影響其他與登記()
    {
        var registry = new ConnectionRegistry();
        var proven = new List<string>();
        var removed = new List<string>();
        var order = new List<string>();
        registry.ConnectionProven += _ => throw new InvalidOperationException("壞的處理者");
        registry.ConnectionProven += c => proven.Add(c.ConnectionId);
        registry.ConnectionRemoved += _ => throw new InvalidOperationException("壞的處理者");
        registry.ConnectionRemoved += c =>
        {
            removed.Add(c.ConnectionId);
            order.Add("removed");
        };
        registry.PresenceChanged += _ => order.Add("presence");

        var old = new DeviceConnection("old", "d1", () => { }, new UpstreamContext("d1", null, null), [DeviceFeatureRules.Conv1]);
        var fresh = new DeviceConnection("new", "d1", () => { }, new UpstreamContext("d1", null, null), [DeviceFeatureRules.Cmd1]);
        registry.FeaturesOf("d1").Should().BeEmpty("不在線");
        registry.Add(old, 1);
        registry.Add(fresh, 2);
        registry.FeaturesOf("d1").Should().Equal(new[] { DeviceFeatureRules.Cmd1 }, "同時兩條時取最新登記的");
        fresh.Order.Should().BeGreaterThan(old.Order);

        old.IsProven.Should().BeFalse();
        registry.MarkProven(old);
        registry.MarkProven(old);
        old.IsProven.Should().BeTrue();
        var stranger = new DeviceConnection("new", "d1", () => { }, new UpstreamContext("d1", null, null));
        registry.MarkProven(stranger);
        stranger.IsProven.Should().BeFalse("不是登記中的那個物件（同 id 的另一條）不算");
        registry.MarkProven(new DeviceConnection("nobody", "d1", () => { }, new UpstreamContext("d1", null, null)));
        proven.Should().Equal(new[] { "old" }, "只在第一次、只對登記中的連線");

        order.Clear();
        registry.Remove("new").Should().BeSameAs(fresh);
        order.Should().Equal(new[] { "removed", "presence" }, "ConnectionRemoved 在 PresenceChanged 之前");
        registry.FeaturesOf("d1").Should().Equal(new[] { DeviceFeatureRules.Conv1 }, "最新的斷了就看剩下的");
        registry.Remove("new").Should().BeNull();
        registry.Remove("old");
        removed.Should().Equal("new", "old");
        registry.FeaturesOf("d1").Should().BeEmpty();
        registry.IsOnline("d1").Should().BeFalse();
    }

    [Fact]
    public async Task 伺服器沒宣告功能時_HelloResponse與device_upsert都不帶features_協商出的是空的()
    {
        // WP4.2 起正式程式一律宣告 conv1（沒有開關，答 1）：以測試專用的 DI 拿掉宣告，證明「沒宣告」時與第 3 階段相同（F51）
        await using var host = await HubTestHost.StartAsync(withoutDeviceFeatures: true);
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);

        var (hello, _) = await HandshakeRawAsync(client, device, DeviceHubClient.ObservedSpki(host), [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1]);

        hello.Properties().Select(p => p.Name).Should().Equal(
            new[] { "serverTime", "nonceS", "server", "hubSig", "hubIdentitySpki", "trustRevs", "limits", "minAppVersion" }, "與第 3 階段相同（rotation、features 是 null 不寫）");
        var connection = host.Services.GetRequiredService<ConnectionRegistry>().ConnectionsOf(device.Id).Single();
        connection.Features.Should().BeEmpty();
        connection.IsProven.Should().BeTrue();
        connection.Allows(DeviceHubMethods.CommandDelivery).Should().BeFalse("沒協商出 cmd1：伺服器不送指令");
        host.Services.GetRequiredService<IDevicePresence>().FeaturesOf(device.Id).Should().BeEmpty();
        var upsert = RawDeviceUpserts(host, device.Id).Last();
        upsert["online"]!.Value<bool>().Should().BeTrue();
        upsert.ContainsKey("features").Should().BeFalse("空的時候省略，舊版拉取端與黃金檔不變");
    }

    [Fact]
    public async Task 伺服器宣告功能時_取交集依裝置的順序_HelloResponse帶伺服器的宣告_device_upsert帶協商出的()
    {
        // 先拿掉正式的宣告（WP4.2 的 conv1），再照原本的順序登記：預期值與 WP4.1 相同（F51）
        await using var host = await HubTestHost.StartAsync(withoutDeviceFeatures: true, services: s => s
            .AddDeviceHubFeature(DeviceFeatureRules.Conv1)
            .AddDeviceHubFeature(DeviceFeatureRules.Cmd1)
            .AddDeviceHubFeature(DeviceFeatureRules.Conv1));
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);

        var (hello, prove) = await HandshakeRawAsync(client, device, DeviceHubClient.ObservedSpki(host),
            [DeviceFeatureRules.Media1, DeviceFeatureRules.Cmd1, "future9", DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1]);

        prove.AccessToken.Should().NotBeNullOrEmpty();
        hello["features"]!.ToObject<string[]>().Should().Equal(new[] { DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1 }, "伺服器宣告的（去重），不是交集");
        hello.Properties().Last().Name.Should().Be("features", "加在最後");
        var connection = host.Services.GetRequiredService<ConnectionRegistry>().ConnectionsOf(device.Id).Single();
        connection.Features.Should().Equal(new[] { DeviceFeatureRules.Cmd1, DeviceFeatureRules.Conv1 }, "依裝置宣告的順序、去重、伺服器沒有的不算");
        connection.Allows(DeviceHubMethods.CommandDelivery).Should().BeTrue();
        connection.Allows(DeviceHubMethods.MediaRequest).Should().BeFalse();
        RawDeviceUpserts(host, device.Id).Last()["features"]!.ToObject<string[]>().Should().Equal(DeviceFeatureRules.Cmd1, DeviceFeatureRules.Conv1);
    }

    [Fact]
    public async Task 預設主機宣告update1_沒宣告的裝置協商出空的()
    {
        await using var host = await HubTestHost.StartAsync();
        var observed = DeviceHubClient.ObservedSpki(host);
        var registry = host.Services.GetRequiredService<ConnectionRegistry>();
        var declared = host.Services.GetRequiredService<DeviceHubFeatures>().Declared!;
        declared.Should().Equal(new[] { DeviceFeatureRules.Conv1, DeviceFeatureRules.Prov1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2, DeviceFeatureRules.Update1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Exec1, DeviceFeatureRules.Media1, DeviceFeatureRules.Stat1, DeviceFeatureRules.Tok1, DeviceFeatureRules.Tok2, DeviceFeatureRules.Sched1, DeviceFeatureRules.Draft1, DeviceFeatureRules.Collab1, DeviceFeatureRules.Xfer1, DeviceFeatureRules.Xfer2, DeviceFeatureRules.Desk1 },
            "依登記的順序：AddHubRealtime 的 conv1、prov1、live1，AddHubReleases 的 update1，AddHubCommands 的 cmd1、peer1，AddHubMedia 的 media1，AddHubStats 的 stat1，AddHubTokens 的 tok1、tok2，AddHubSchedules 的 sched1");

        var cases = new (IReadOnlyList<string>? Declared, string[] Negotiated, string Because)[]
        {
            (null, [], "U3 之前的桌面不宣告任何功能"),
            (["future9"], [], "只宣告伺服器沒有的"),
            ([DeviceFeatureRules.Update1], [DeviceFeatureRules.Update1], "裝置也宣告 update1"),
        };
        foreach (var c in cases)
        {
            using var device = await TestDevice.AddAsync(host);
            await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);

            var (hello, prove) = await HandshakeRawAsync(client, device, observed, c.Declared);

            prove.UpdateOnly.Should().BeNull("app 版本夠新：完整連線（{0}）", c.Because);
            hello["features"]!.ToObject<string[]>().Should().Equal(declared, "伺服器宣告的全部（{0}）", c.Because);
            hello.Properties().Last().Name.Should().Be("features", "沒有可用版本時 latestApp 省略，features 是最後一個鍵");
            registry.ConnectionsOf(device.Id).Single().Features.Should().Equal(c.Negotiated, c.Because);
            var upsert = RawDeviceUpserts(host, device.Id).Last();
            if (c.Negotiated.Length == 0)
                upsert.ContainsKey("features").Should().BeFalse("協商出空的：device.upsert 照舊不帶 features（{0}）", c.Because);
            else
                upsert["features"]!.ToObject<string[]>().Should().Equal(c.Negotiated);
        }
    }

    [Fact]
    public async Task Hello的features不合格_整個Hello回invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        await using var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var request = DeviceHubClient.HelloRequest(device, DeviceHubClient.ObservedSpki(host), HandshakeRules.NewNonce());

        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, request with { Features = ["Conv1"] }))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid");
        await FluentActions.Awaiting(() => client.Connection.InvokeAsync<HelloResponse>(DeviceHubMethods.Hello, request with { Features = Enumerable.Range(0, 33).Select(i => "f" + i).ToList() }))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid");
    }

    [Fact]
    public async Task 交握觸發ConnectionProven一次_斷線觸發ConnectionRemoved一次()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        var registry = host.Services.GetRequiredService<ConnectionRegistry>();
        var provenAt = new TaskCompletionSource<(DeviceConnection Connection, bool ProvenFlag)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removedAt = new TaskCompletionSource<DeviceConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provenCount = 0;
        var removedCount = 0;
        registry.ConnectionProven += c =>
        {
            Interlocked.Increment(ref provenCount);
            provenAt.TrySetResult((c, c.IsProven));
        };
        registry.ConnectionRemoved += c =>
        {
            Interlocked.Increment(ref removedCount);
            removedAt.TrySetResult(c);
        };

        var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
        var (connection, flag) = await provenAt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        connection.DeviceId.Should().Be(device.Id);
        flag.Should().BeTrue("事件觸發時已經標成已證明");
        await client.DisposeAsync();

        (await removedAt.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeSameAs(connection);
        provenCount.Should().Be(1);
        removedCount.Should().Be(1);
    }

    [Fact]
    public async Task 兩條連線時最新的那條斷了而且功能不同_重發device_upsert帶剩下那條的功能()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        var registry = host.Services.GetRequiredService<ConnectionRegistry>();
        var runtime = host.Services.GetRequiredService<DeviceHubRuntime>();
        foreach (var (id, feature) in new[] { ("a", DeviceFeatureRules.Conv1), ("b", DeviceFeatureRules.Cmd1), ("c", DeviceFeatureRules.Cmd1), ("d", DeviceFeatureRules.Cmd1) })
            registry.Add(new DeviceConnection(id, device.Id, () => { }, new UpstreamContext(device.Id, null, null), [feature]), host.Clock.NowMs);
        var before = RawDeviceUpserts(host, device.Id).Count;

        await runtime.OnDisconnectedAsync("b");
        RawDeviceUpserts(host, device.Id).Should().HaveCount(before, "斷的不是最新的那條：功能不變、不重發");
        await runtime.OnDisconnectedAsync("d");
        RawDeviceUpserts(host, device.Id).Should().HaveCount(before, "最新的斷了，但剩下最新的那條功能相同：不重發");

        await runtime.OnDisconnectedAsync("c");
        var upserts = RawDeviceUpserts(host, device.Id);
        upserts.Should().HaveCount(before + 1, "最新的斷了、剩下那條的功能不同：重發");
        upserts[^1]["online"]!.Value<bool>().Should().BeTrue();
        upserts[^1]["features"]!.ToObject<string[]>().Should().Equal(DeviceFeatureRules.Conv1);

        await runtime.OnDisconnectedAsync("a");
        upserts = RawDeviceUpserts(host, device.Id);
        upserts.Should().HaveCount(before + 2, "最後一條斷了：離線");
        upserts[^1]["online"]!.Value<bool>().Should().BeFalse();
        upserts[^1].ContainsKey("features").Should().BeFalse();
    }

    // ── 呼叫框的預留（合併前審查 seams/F4）──

    [Fact]
    public void 呼叫框的預留_引數到上限減預留時整個框仍在單則上限內_只量引數的話框會超過()
    {
        var protocol = new NewtonsoftJsonHubProtocol(Options.Create(new NewtonsoftJsonHubProtocolOptions { PayloadSerializerSettings = SyncJson.Settings }));
        var target = DeviceHubMethods.DeviceToServer.MaxBy(m => m.Length)!;
        var conversation = new string('c', 32);
        var empty = Encoding.UTF8.GetByteCount(SyncJson.Serialize(new LiveRelay(conversation, long.MaxValue, long.MaxValue, long.MaxValue, string.Empty)));
        int FrameBytes(int argumentBytes)
        {
            var relay = new LiveRelay(conversation, long.MaxValue, long.MaxValue, long.MaxValue, new string('a', argumentBytes - empty));
            Encoding.UTF8.GetByteCount(SyncJson.Serialize(relay)).Should().Be(argumentBytes);
            var buffer = new ArrayBufferWriter<byte>();
            protocol.WriteMessage(new InvocationMessage(int.MaxValue.ToString(CultureInfo.InvariantCulture), target, [relay]), buffer);
            return buffer.WrittenCount;
        }

        var limit = SyncProtocol.DeviceHubMaxMessageBytes - SyncProtocol.InvocationFrameReserveBytes;
        SyncProtocol.FitsInvocation(limit).Should().BeTrue();
        SyncProtocol.FitsInvocation(limit + 1).Should().BeFalse();
        FrameBytes(limit).Should().BeLessThanOrEqualTo(SyncProtocol.DeviceHubMaxMessageBytes, "伺服器的 MaximumReceiveMessageSize 量的是整個框");
        FrameBytes(SyncProtocol.DeviceHubMaxMessageBytes).Should().BeGreaterThan(SyncProtocol.DeviceHubMaxMessageBytes,
            "引數剛好到上限時加上框就超過：伺服器會關掉整條連線（修正前 EnsureFits 只量引數）");
    }

    // ── 信封的 conn 與身分金鑰輪換（合併前審查 seams/F1、envelope/ENV-2）──

    [Fact]
    public void 簽信封的條件_要有nonceS而且Hello時的身分公鑰是目前的_AbortAll中斷每一條()
    {
        var registry = new ConnectionRegistry();
        var aborted = new List<string>();
        var known = new DeviceConnection("a", "d1", () => aborted.Add("a"), new UpstreamContext("d1", null, null), envelopeConn: "nonce-a", helloHubSpki: "spki-1");
        var legacy = new DeviceConnection("b", "d2", () => aborted.Add("b"), new UpstreamContext("d2", null, null));
        registry.Add(known, 1);
        registry.Add(legacy, 2);

        known.CanSignEnvelopes("spki-1").Should().BeTrue();
        known.CanSignEnvelopes("spki-2").Should().BeFalse("身分金鑰輪換之後：裝置還釘著舊公鑰");
        legacy.CanSignEnvelopes("spki-1").Should().BeFalse("不知道 Hello 的 nonceS 與公鑰");
        new DeviceConnection("c", "d1", () => { }, new UpstreamContext("d1", null, null), envelopeConn: "nonce-c").CanSignEnvelopes("spki-1")
            .Should().BeFalse("不知道 Hello 時的公鑰");

        registry.AbortAll();
        aborted.Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task 交握後連線記下Hello的nonceS與公鑰_輪換身分金鑰中斷所有連線_重連改釘後簽給新連線的信封驗得過()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        var registry = host.Services.GetRequiredService<ConnectionRegistry>();
        var identity = host.Services.GetRequiredService<HubIdentity>();
        var pinned = identity.Spki;
        var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (hello, _) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));

        var before = registry.ConnectionsOf(device.Id).Single();
        before.EnvelopeConn.Should().Be(hello.NonceS, "conn 是裝置看得到的 nonceS，不是 SignalR 的連線 id");
        before.EnvelopeConn.Should().NotBe(before.ConnectionId);
        before.HelloHubSpki.Should().Be(pinned).And.Be(hello.HubIdentitySpki);
        before.CanSignEnvelopes(identity.Spki).Should().BeTrue();

        var result = await host.Services.GetRequiredService<AdminCommandExecutor>()
            .ExecuteAsync(new AdminCommand("rotate-1", "rotate-hub-identity", host.Clock.NowMs), CancellationToken.None);
        result.Ok.Should().BeTrue(result.Message);
        identity.Spki.Should().NotBe(pinned);
        before.CanSignEnvelopes(identity.Spki).Should().BeFalse("Hello 時的公鑰已不是目前的");
        await client.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();

        await using var again = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (rehello, _) = await again.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
        var rotation = rehello.Rotation!;
        var repinned = HandshakeRules.AcceptRotation(pinned, rotation.OldSpki, rotation.NewSpki, rotation.Sig);
        repinned.Should().Be(identity.Spki, "釘舊公鑰的裝置依輪換聲明改釘");
        // 舊連線在伺服器端的移除（OnDisconnectedAsync）可能還沒跑完：以 Order 取最新的那條
        var after = registry.ConnectionsOf(device.Id).MaxBy(c => c.Order)!;
        after.Should().NotBeSameAs(before);
        after.EnvelopeConn.Should().Be(rehello.NonceS).And.NotBe(hello.NonceS);
        after.CanSignEnvelopes(identity.Spki).Should().BeTrue();

        EnvelopeSigner signer = identity.Sign;
        var now = host.Clock.NowMs;
        var cid = SyncIds.NewUlid(now);
        var body = new CommandEnvelopeBody("https://hub.example.com", cid, device.Id, after.EnvelopeConn!, null, null,
            new JObject { ["cmd"] = "accountLogout", ["accountId"] = "acc-1" }, 2, false, false,
            now, now, now + 30_000, new CommandIssuer(CommandIssuerKinds.Browser, "Chrome・Windows"), new CommandAuth(now, false));
        CommandEnvelope.TrySign(body, signer, out var envelope).Should().BeTrue();
        var bounds = new ServerTimeBounds(now, now + 1_000);
        var verdict = CommandEnvelope.Verify(envelope, cid, new CommandEnvelopeTarget(repinned!, body.Aud, device.Id, rehello.NonceS), bounds);
        verdict.Ok.Should().BeTrue(verdict.Detail);
        CommandEnvelope.Verify(envelope, cid, new CommandEnvelopeTarget(pinned, body.Aud, device.Id, rehello.NonceS), bounds).Step
            .Should().Be(CommandEnvelopeSteps.Signature, "沒有改釘的話一定驗章失敗（所以輪換後要中斷連線、不對舊連線簽）");
    }

    /// <summary>過濾器測試用的 hub：方法名稱是 HubInvocationContext.HubMethodName 的來源（Debug 不在允許清單）</summary>
    private sealed class FakeHub : Hub
    {
        public void Hello()
        {
        }

        public void Pull()
        {
        }

        public void Debug()
        {
        }
    }

    private sealed class FakeCallerContext : HubCallerContext
    {
        public override string ConnectionId => "c1";

        public override string? UserIdentifier => null;

        public override System.Security.Claims.ClaimsPrincipal? User => null;

        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();

        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
        }
    }

    /// <summary>Hello 以 JObject 收（看得到原始的鍵與順序）再 Prove</summary>
    private static async Task<(JObject Hello, ProveResponse Prove)> HandshakeRawAsync(DeviceHubClient client, TestDevice device, string observed, IReadOnlyList<string>? features)
    {
        var nonceD = HandshakeRules.NewNonce();
        var hello = await client.Connection.InvokeAsync<JObject>(DeviceHubMethods.Hello, DeviceHubClient.HelloRequest(device, observed, nonceD) with { Features = features });
        var nonceS = hello["nonceS"]!.Value<string>()!;
        var sig = P256KeyRules.Sign(device.Key, HandshakeRules.DeviceSignedData(device.Id, nonceS, nonceD, observed));
        var prove = await client.Connection.InvokeAsync<ProveResponse>(DeviceHubMethods.Prove, new ProveRequest(sig));
        return (hello, prove);
    }

    /// <summary>這台的 device.upsert（伺服器寫）原樣的 payload JSON，依 seq</summary>
    private static List<JObject> RawDeviceUpserts(HubTestHost host, string deviceId)
    {
        var pulled = host.Services.GetRequiredService<EventLog>().Pull(new PullRequest([new PullCursor(0, Cls: SyncClasses.Catalog)], 500, 4_000_000));
        return pulled.Events.Where(e => e.Kind == SyncEventKinds.DeviceUpsert && e.Author == SyncEventStore.ServerAuthor)
            .Select(e => PayloadRules.TryDecode(e.Enc, e.Payload, 1 << 22, out var json) ? JObject.Parse(json) : throw new InvalidOperationException("payload 解不開"))
            .Where(p => p["deviceId"]!.Value<string>() == deviceId)
            .ToList();
    }
}
