using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 遠端桌面的轉送（docs/sync-protocol.md 14.16，desk1）：只收工作階段兩邊送的、方向對的種類；瀏覽器看的轉成 desk 訊息、電腦看的以 DeskDown 轉；
/// 結束之後忘掉；看的瀏覽器斷線替它送 close；被看的電腦不在了告訴看的一方；名額與 id 檢查
/// </summary>
public sealed class DeskRelaysTests
{
    private const string Origin = "d_01J9QZ3K8M5R7T2V4W6X8Y0AAA";

    private const string Target = "d_01J9QZ3K8M5R7T2V4W6X8Y0BBB";

    private const string Other = "d_01J9QZ3K8M5R7T2V4W6X8Y0CCC";

    private const string Session = "01J9QZ3K8M5R7T2V4W6X8Y0SES";

    private static readonly string[] Desk = [DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Desk1];

    private sealed class RecordingBrowser : IDeskBrowserSender
    {
        public List<(BrowserConnection Connection, JObject Message)> Sent { get; } = [];

        public void Send(BrowserConnection connection, ClientOutboxMessage message) => Sent.Add((connection, JObject.Parse(message.Json)));
    }

    private readonly ConnectionRegistry registry = new();
    private readonly RecordingDeviceSender sender = new();
    private readonly RecordingBrowser browserSender = new();
    private readonly ManualTimeProvider clock = new();
    private readonly DeskRelays relays;

    public DeskRelaysTests() => relays = new DeskRelays(registry, sender, browserSender, clock, NullLogger<DeskRelays>.Instance);

    private DeviceConnection Connect(string connectionId, string deviceId, IReadOnlyList<string>? features = null)
    {
        var connection = new DeviceConnection(connectionId, deviceId, () => { }, new UpstreamContext(deviceId, null, null), features ?? Desk);
        registry.Add(connection, clock.NowMs);
        registry.MarkProven(connection);
        return connection;
    }

    private static BrowserConnection Browser(string id = "b1") => new(id, "s1", null, () => { }, new ClientOutbox(100, 1 << 20));

    private static DeskPacket Frame(long seq) => new(Session, DeskRules.Frame, seq, new JObject { ["w"] = 10, ["h"] = 10, ["tiles"] = new JArray() });

    private static DeskPacket Input() => new(Session, DeskRules.Input, 1, new JObject { ["events"] = new JArray(new JObject { ["t"] = "kd", ["k"] = 65 }) });

    [Fact]
    public async Task 電腦看電腦_影格轉給看的電腦_輸入轉給被看的_方向不對的丟掉()
    {
        var origin = Connect("origin", Origin);
        var target = Connect("target", Target);
        Connect("target-old", Target, [DeviceFeatureRules.Cmd1]);
        relays.Register(Session, Target, new DeviceCommandOrigin(Origin, "origin", null));

        (await relays.ReceiveFromDeviceAsync(target, Frame(1), CancellationToken.None)).Keep.Should().BeTrue();
        (await relays.ReceiveFromDeviceAsync(origin, Input(), CancellationToken.None)).Keep.Should().BeTrue();
        (await relays.ReceiveFromDeviceAsync(target, Input(), CancellationToken.None)).Keep.Should().BeTrue("方向不對只丟這一包");
        (await relays.ReceiveFromDeviceAsync(Connect("other", Other), Frame(2), CancellationToken.None)).Keep.Should().BeFalse("不是這個工作階段的");

        sender.All.Select(s => (s.ConnectionId, ((DeskPacket)s.Message).Kind)).Should().Equal(("origin", DeskRules.Frame), ("target", DeskRules.Input));
        sender.All.Should().OnlyContain(s => s.Method == DeviceHubMethods.DeskDown);
    }

    [Fact]
    public async Task 瀏覽器看_影格成為desk訊息_只收這條連線開的_close之後忘掉()
    {
        var target = Connect("target", Target);
        var browser = Browser();
        relays.Register(Session, Target, new BrowserCommandOrigin(browser));

        (await relays.ReceiveFromDeviceAsync(target, Frame(3), CancellationToken.None)).Keep.Should().BeTrue();
        var message = browserSender.Sent.Single().Message;
        message.Value<string>("type").Should().Be("desk");
        message.Value<string>("kind").Should().Be(DeskRules.Frame);
        message.Value<long>("seq").Should().Be(3);

        (await relays.ReceiveFromBrowserAsync(Browser("b2"), Input(), CancellationToken.None)).Should().BeFalse("別的連線");
        (await relays.ReceiveFromBrowserAsync(browser, Input(), CancellationToken.None)).Should().BeTrue();
        (await relays.ReceiveFromBrowserAsync(browser, new DeskPacket(Session, DeskRules.Close, 2), CancellationToken.None)).Should().BeFalse();
        sender.All.Select(s => ((DeskPacket)s.Message).Kind).Should().Equal(DeskRules.Input, DeskRules.Close);
        relays.Count.Should().Be(0);
        (await relays.ReceiveFromDeviceAsync(target, Frame(4), CancellationToken.None)).Keep.Should().BeFalse("忘掉了，那台停止");
    }

    [Fact]
    public async Task 看的瀏覽器斷線替它送close_被看的電腦送end之後忘掉()
    {
        var target = Connect("target", Target);
        var browser = Browser();
        relays.Register(Session, Target, new BrowserCommandOrigin(browser));

        await relays.BrowserGoneAsync(browser);

        sender.All.Single().Method.Should().Be(DeviceHubMethods.DeskDown);
        ((DeskPacket)sender.All.Single().Message).Kind.Should().Be(DeskRules.Close);
        relays.Count.Should().Be(0);

        relays.Register(Session, Target, new BrowserCommandOrigin(browser));
        (await relays.ReceiveFromDeviceAsync(target, new DeskPacket(Session, DeskRules.End, 0, DeskRules.EndBody("targetClosed", "x")), CancellationToken.None))
            .Keep.Should().BeFalse();
        browserSender.Sent.Last().Message.Value<string>("kind").Should().Be(DeskRules.End);
        relays.Count.Should().Be(0);
    }

    [Fact]
    public async Task 被看的電腦不在了_告訴看的瀏覽器()
    {
        var browser = Browser();
        relays.Register(Session, Target, new BrowserCommandOrigin(browser));

        (await relays.ReceiveFromBrowserAsync(browser, Input(), CancellationToken.None)).Should().BeFalse();

        var end = browserSender.Sent.Single().Message;
        end.Value<string>("kind").Should().Be(DeskRules.End);
        end["body"]!.Value<string>("reason").Should().Be("targetGone");
        relays.Count.Should().Be(0);
    }

    [Fact]
    public async Task 看的電腦連不上太久_請被看的停止()
    {
        var target = Connect("target", Target);
        relays.Register(Session, Target, new DeviceCommandOrigin(Origin, "origin-gone", null));

        (await relays.ReceiveFromDeviceAsync(target, Frame(1), CancellationToken.None)).Keep.Should().BeTrue("還在寬限內");
        clock.Advance(DeskRules.OriginLostLimit + TimeSpan.FromSeconds(1));
        (await relays.ReceiveFromDeviceAsync(target, Frame(2), CancellationToken.None)).Keep.Should().BeFalse();
        relays.Count.Should().Be(0);
    }

    [Fact]
    public void 開始之前的檢查_id格式_重複_每台一個_忘掉之後可以再開()
    {
        relays.CheckOpen("短", Target).Should().NotBeNull();
        relays.CheckOpen(Session, Target).Should().BeNull();
        relays.Register(Session, Target, new BrowserCommandOrigin(Browser()));
        relays.CheckOpen(Session, Other).Should().Contain("重複");
        relays.CheckOpen("01J9QZ3K8M5R7T2V4W6X8Y0SE2", Target).Should().Contain("已經有人");
        relays.CheckOpen("01J9QZ3K8M5R7T2V4W6X8Y0SE2", Other).Should().BeNull();
        relays.Forget(Session);
        relays.CheckOpen("01J9QZ3K8M5R7T2V4W6X8Y0SE2", Target).Should().BeNull();
        relays.Register(Session, Target, new BrowserCommandOrigin(Browser()));
        clock.Advance(DeskRules.IdleForget + TimeSpan.FromSeconds(1));
        relays.CheckOpen("01J9QZ3K8M5R7T2V4W6X8Y0SE3", Target).Should().BeNull("太久沒有封包的忘掉");
    }
}
