using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 遠端執行指令的串流轉送（docs/sync-protocol.md 14.11，exec1）：只收登記的目標電腦送來的段落，commandId 換成發起時的轉給發起電腦最新的 exec1 連線；
/// 發起電腦連不上超過上限、不認得的 cid、過期都回 keep false；結束碼轉送之後忘掉；停止要求只認發起的電腦下的
/// </summary>
public sealed class ExecRelaysTests
{
    private const string Origin = "d_01J9QZ3K8M5R7T2V4W6X8Y0AAA";

    private const string Target = "d_01J9QZ3K8M5R7T2V4W6X8Y0BBB";

    private const string Other = "d_01J9QZ3K8M5R7T2V4W6X8Y0CCC";

    private const string Cid = "01J9QZ3K8M5R7T2V4W6X8Y0C1D";

    private const string OriginCommandId = "01J9QZ3K8M5R7T2V4W6X8Y0A1G";

    private static readonly string[] Exec = [DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Exec1];

    private readonly ConnectionRegistry registry = new();

    private readonly RecordingDeviceSender sender = new();

    private readonly ManualTimeProvider clock = new();

    private readonly ExecRelays relays;

    public ExecRelaysTests() => relays = new ExecRelays(registry, sender, clock, NullLogger<ExecRelays>.Instance);

    private DeviceConnection Connect(string connectionId, string deviceId, IReadOnlyList<string>? features = null, bool prove = true)
    {
        var connection = new DeviceConnection(connectionId, deviceId, () => { }, new UpstreamContext(deviceId, null, null), features ?? Exec);
        registry.Add(connection, clock.NowMs);
        if (prove)
            registry.MarkProven(connection);
        return connection;
    }

    private static ExecChunk Out(long seq) => new(Cid, seq, RemoteExecRules.KindOut, "第 " + seq + " 段");

    [Fact]
    public async Task 目標的段落換成發起時的commandId_轉給發起電腦最新的連線_結束碼之後忘掉()
    {
        Connect("origin-old", Origin);
        Connect("origin-new", Origin);
        Connect("origin-no-exec", Origin, [DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1]);
        var target = Connect("target", Target);
        relays.Register(Cid, Origin, OriginCommandId, Target, 60);

        (await relays.ReceiveOutputAsync(target, Out(0), CancellationToken.None)).Keep.Should().BeTrue();
        (await relays.ReceiveOutputAsync(target, new ExecChunk(Cid, 1, RemoteExecRules.KindExit, null, 0), CancellationToken.None)).Keep.Should().BeTrue();

        var replies = sender.All.Where(s => s.Method == DeviceHubMethods.ExecReply).ToList();
        replies.Select(r => r.ConnectionId).Should().Equal("origin-new", "origin-new");
        replies.Select(r => ((ExecChunk)r.Message).CommandId).Should().AllBe(OriginCommandId);
        ((ExecChunk)replies[0].Message).Text.Should().Be("第 0 段");
        relays.Count.Should().Be(0, "結束碼轉送之後忘掉");
        (await relays.ReceiveOutputAsync(target, Out(2), CancellationToken.None)).Keep.Should().BeFalse("之後到的不認得");
    }

    [Fact]
    public async Task 只收目標電腦的_沒證明或沒有exec1的連線一律不收_格式不合格的丟掉()
    {
        Connect("origin", Origin);
        var target = Connect("target", Target);
        var other = Connect("other", Other);
        var unproven = Connect("target-unproven", Target, prove: false);
        var oldTarget = Connect("target-old", Target, [DeviceFeatureRules.Cmd1]);
        relays.Register(Cid, Origin, OriginCommandId, Target, 60);

        (await relays.ReceiveOutputAsync(other, Out(0), CancellationToken.None)).Keep.Should().BeFalse("不是這個指令的目標");
        (await relays.ReceiveOutputAsync(unproven, Out(0), CancellationToken.None)).Keep.Should().BeFalse();
        (await relays.ReceiveOutputAsync(oldTarget, Out(0), CancellationToken.None)).Keep.Should().BeFalse();
        (await relays.ReceiveOutputAsync(null, Out(0), CancellationToken.None)).Keep.Should().BeFalse();
        (await relays.ReceiveOutputAsync(target, new ExecChunk(Cid, 0, "weird", "x"), CancellationToken.None)).Keep.Should().BeTrue("格式不合格只丟這一段");
        sender.All.Should().BeEmpty();
        relays.Count.Should().Be(1);
    }

    [Fact]
    public async Task 發起電腦連不上太久_請目標停止()
    {
        var origin = Connect("origin", Origin);
        var target = Connect("target", Target);
        relays.Register(Cid, Origin, OriginCommandId, Target, 3600);
        registry.Remove(origin.ConnectionId);

        (await relays.ReceiveOutputAsync(target, Out(0), CancellationToken.None)).Keep.Should().BeTrue("剛斷線：先丟掉這段、等它回來");
        clock.Advance(RemoteExecRules.OriginLostLimit - TimeSpan.FromSeconds(1));
        (await relays.ReceiveOutputAsync(target, Out(1), CancellationToken.None)).Keep.Should().BeTrue();

        Connect("origin-back", Origin);
        (await relays.ReceiveOutputAsync(target, Out(2), CancellationToken.None)).Keep.Should().BeTrue();
        sender.All.Should().ContainSingle().Which.ConnectionId.Should().Be("origin-back");

        registry.Remove("origin-back");
        (await relays.ReceiveOutputAsync(target, Out(3), CancellationToken.None)).Keep.Should().BeTrue("回來過一次：重新計時");
        clock.Advance(RemoteExecRules.OriginLostLimit);
        (await relays.ReceiveOutputAsync(target, Out(4), CancellationToken.None)).Keep.Should().BeFalse("連不上超過上限");
        relays.Count.Should().Be(0);
    }

    [Fact]
    public async Task 過期的不轉送()
    {
        Connect("origin", Origin);
        var target = Connect("target", Target);
        relays.Register(Cid, Origin, OriginCommandId, Target, 10);
        clock.Advance(TimeSpan.FromSeconds(10) + RemoteExecRules.RelayGrace);
        (await relays.ReceiveOutputAsync(target, Out(0), CancellationToken.None)).Keep.Should().BeFalse();
        sender.All.Should().BeEmpty();
    }

    [Fact]
    public async Task 停止要求只認發起的電腦_轉成ExecAbort給目標每條exec1連線()
    {
        var origin = Connect("origin", Origin);
        var other = Connect("other", Other);
        Connect("target-1", Target);
        Connect("target-2", Target);
        Connect("target-old", Target, [DeviceFeatureRules.Cmd1]);
        relays.Register(Cid, Origin, OriginCommandId, Target, 60);

        (await relays.CancelAsync(other, new ExecCancel(OriginCommandId), CancellationToken.None)).Found.Should().BeFalse("不是它下的");
        (await relays.CancelAsync(origin, new ExecCancel("01J9QZ3K8M5R7T2V4W6X8Y0ZZZ"), CancellationToken.None)).Found.Should().BeFalse();
        (await relays.CancelAsync(origin, new ExecCancel("bad"), CancellationToken.None)).Found.Should().BeFalse();
        sender.All.Should().BeEmpty();

        (await relays.CancelAsync(origin, new ExecCancel(OriginCommandId), CancellationToken.None)).Found.Should().BeTrue();
        var aborts = sender.All.Where(s => s.Method == DeviceHubMethods.ExecAbort).ToList();
        aborts.Select(a => a.ConnectionId).Should().BeEquivalentTo(["target-1", "target-2"]);
        aborts.Select(a => ((ExecAbort)a.Message).CommandId).Should().AllBe(Cid);
        relays.Count.Should().Be(1, "還要轉送最後的結束碼");
    }

    [Fact]
    public void 登記滿了先丟最舊的()
    {
        for (var i = 0; i < ExecRelays.MaxEntries + 5; i++)
            relays.Register(SyncIds.NewUlid(1_760_000_000_000 + i), Origin, OriginCommandId, Target, 60);
        relays.Count.Should().Be(ExecRelays.MaxEntries);
    }
}
