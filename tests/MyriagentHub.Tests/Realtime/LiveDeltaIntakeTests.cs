using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 即時更新的增量（live2；docs/plans/2026-10-23-live-delta-and-replica-live.md 3.4）：整則建立基底、增量以基底重組成整則照常進疊加層，
/// 沒有基底或對不上就要整則（LiveResync，每秒最多一則）、DLP 命中不留基底、基底不受疊加層作廢影響、連線移除時清掉；沒有 live2 的連線照舊
/// </summary>
public sealed class LiveDeltaIntakeTests
{
    private static readonly string C1 = ConversationHarness.ConversationId(1);

    private static JObject Wire(string text, long ver) => ConversationSamples.Item(text, "Running", "item1", 1, ver);

    private static string Html(JToken message) => (message["item"] ?? message)["html"]!.Value<string>()!;

    private static List<LiveResync> Resyncs(ConversationHarness h, string connectionId) => h.DeviceSender.All
        .Where(m => m.ConnectionId == connectionId && m.Method == DeviceHubMethods.LiveResync).Select(m => (LiveResync)m.Message).ToList();

    [Fact]
    public async Task 整則建立基底_增量重組成整則進疊加層_瀏覽器看到新的內容()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive2(device);
        var (a, _) = await h.ReadyAsync("b1");
        await h.OpenAsync(a, C1);

        var first = Wire("第一段", 1);
        h.Relay(live, C1, 1, 1, first).Should().Be(LiveIntakeOutcome.Offered);
        h.Bases.Count.Should().Be(1, "整則成為這一筆的基底");
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("第一段");

        var second = Wire("第一段，接著第二段", 2);
        h.Delta(live, C1, 1, 2, 1, LiveDeltaRules.FieldsOf(first), second).Should().Be(LiveIntakeOutcome.Offered);
        h.Intake.DeltaApplied.Should().Be(1);
        await h.RoundAsync();
        Html(await a.NextAsync()).Should().Contain("接著第二段");
        Resyncs(h, live).Should().BeEmpty();
    }

    [Fact]
    public async Task 沒有基底或對不上_丟掉並要整則_同一筆每秒最多一則()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive2(device);

        h.Delta(live, C1, 1, 2, 1, LiveLongFields.Empty, Wire("沒有基底", 2)).Should().Be(LiveIntakeOutcome.Dropped);
        Resyncs(h, live).Should().ContainSingle().Which.Should().Be(new LiveResync(C1, 1, LiveResyncReasons.Base));

        h.Relay(live, C1, 1, 3, Wire("整則", 3)).Should().Be(LiveIntakeOutcome.Offered);
        h.Delta(live, C1, 1, 5, 4, LiveDeltaRules.FieldsOf(Wire("整則", 3)), Wire("整則之後", 5)).Should().Be(LiveIntakeOutcome.Dropped, "baseVer 4 不是伺服器記的 3");
        Resyncs(h, live).Should().HaveCount(1, "同一筆一秒內不再送");
        h.Bases.Count.Should().Be(0, "對不上就清掉基底");

        h.Time.Advance(TimeSpan.FromSeconds(1.1));
        h.Delta(live, C1, 1, 6, 5, LiveLongFields.Empty, Wire("再一次", 6)).Should().Be(LiveIntakeOutcome.Dropped);
        Resyncs(h, live).Should().HaveCount(2);
    }

    [Fact]
    public async Task 第二道DLP命中_遮蔽後轉送_不留基底_要這一筆只送整則()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive2(device);
        var first = Wire("乾淨的開頭 ", 1);
        h.Relay(live, C1, 1, 1, first).Should().Be(LiveIntakeOutcome.Offered);

        // 權杖切在兩則增量之間：重組後的整則才看得出來
        var token = ConversationSamples.GitHub();
        var leaked = Wire("乾淨的開頭 " + token, 2);
        h.Delta(live, C1, 1, 2, 1, LiveDeltaRules.FieldsOf(first), leaked).Should().Be(LiveIntakeOutcome.OfferedRemasked);
        h.Bases.Count.Should().Be(0, "伺服器不在記憶體保留判定外洩的原文");
        Resyncs(h, live).Should().ContainSingle().Which.Reason.Should().Be(LiveResyncReasons.Remask);
    }

    [Fact]
    public async Task 疊加層因持久版本作廢之後_基底照樣在_下一則增量照收()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive2(device);
        var first = Wire("第一段", 1);
        h.Relay(live, C1, 1, 1, first).Should().Be(LiveIntakeOutcome.Offered);
        await h.PublishAsync(device, (ConversationEventKinds.Item, ConversationSamples.Item("第一段", "Running", "item1", 1, 1), C1));
        await h.RoundAsync();

        h.Delta(live, C1, 1, 2, 1, LiveDeltaRules.FieldsOf(first), Wire("第一段，再多一點", 2)).Should().Be(LiveIntakeOutcome.Offered,
            "10 秒一次的檢查點作廢疊加時不能讓增量斷掉");
    }

    [Fact]
    public async Task 沒有live2的連線_LiveDelta丟掉_整則照舊64KB上限_有live2的到256KB()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var old = h.OnlineLive(device);
        var big = Wire(new string('長', 30_000), 1);   // UTF-8 約 90 KB

        h.Delta(old, C1, 1, 2, 1, LiveLongFields.Empty, Wire("x", 2)).Should().Be(LiveIntakeOutcome.Dropped);
        h.Relay(old, C1, 1, 1, big).Should().Be(LiveIntakeOutcome.Dropped, "沒有 live2：64 KB");
        Resyncs(h, old).Should().BeEmpty("沒有 live2 的連線收不到 LiveResync");
        h.Offline(old);

        var live = h.OnlineLive2(device);
        h.Relay(live, C1, 1, 1, big).Should().Be(LiveIntakeOutcome.Offered, "live2：256 KB");
    }

    [Fact]
    public async Task 連線移除_它的基底清掉()
    {
        await using var h = await ConversationHarness.StartAsync();
        var device = await h.AddDeviceAsync();
        await h.CreateAsync(device, C1);
        var live = h.OnlineLive2(device);
        h.Relay(live, C1, 1, 1, Wire("一", 1)).Should().Be(LiveIntakeOutcome.Offered);
        h.Bases.Count.Should().Be(1);

        h.Offline(live);
        h.Bases.Count.Should().Be(0);
    }

    [Fact]
    public void 基底的上限_每條連線32筆_丟最久沒用的_60秒沒用的過期()
    {
        var time = new FakeTimestampProvider();
        var bases = new LiveDeltaBases(time);
        var fields = new LiveLongFields("x", null, null);
        for (var ord = 1; ord <= LiveDeltaBases.MaxPerConnection; ord++)
        {
            bases.Set(new("c1", C1, ord), 1, fields).Should().BeEmpty();
            time.Advance(TimeSpan.FromMilliseconds(1));
        }
        bases.Set(new("c2", C1, 1), 1, fields).Should().BeEmpty("另一條連線另外算");
        bases.Set(new("c1", C1, 99), 1, fields).Should().Equal(new LiveDeltaBases.Key("c1", C1, 1));
        bases.TryGet(new("c1", C1, 1), out _, out _).Should().BeFalse();
        bases.TryGet(new("c1", C1, 99), out var ver, out var got).Should().BeTrue();
        (ver, got).Should().Be((1L, fields));

        time.Advance(LiveDeltaBases.IdleExpiry);
        bases.TryGet(new("c1", C1, 99), out _, out _).Should().BeFalse("60 秒沒用到就過期");
        bases.RemoveConnection("c1");
        bases.RemoveConnection("c2");
        bases.Count.Should().Be(0);
        bases.Bytes.Should().Be(0);
    }
}
