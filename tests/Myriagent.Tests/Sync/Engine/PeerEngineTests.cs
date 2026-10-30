using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 桌面遠端操作的引擎（sync-protocol 14.7）：宣告 peer1（跟著 cmd1）、交握完成而且協商出 peer1 才回報能送；能送時呼叫 PeerCmd，
/// 不能送或伺服器回 accepted=false 時合成 commandResult rejected（origin transport）；PeerReply 驗過交給 port
/// </summary>
public sealed class PeerEngineTests
{
    private static readonly string CommandId = SyncIds.NewUlid(1_760_000_000_000);

    private static PeerCmd Stop() => new(CommandId, new JObject { ["cmd"] = "stop", ["conversationId"] = "0123456789abcdef0123456789abcdef" });

    private static FakeCommandPort UsePeerPort(EngineHarness h, bool serverPeer)
    {
        var port = CommandEngineTests.UsePort(h);
        h.Server.Features = serverPeer ? [DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1] : [DeviceFeatureRules.Cmd1];
        return port;
    }

    [Fact]
    public async Task 宣告peer1_協商出來時回報能送()
    {
        await using var h = new EngineHarness();
        var port = UsePeerPort(h, serverPeer: true);
        await h.StartAsync();

        h.Server.Hellos.Last().Features.Should().Equal(DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Collab1);
        await CommandEngineTests.WaitUntilAsync(() => port.PeerLive.Count > 0, "回報能送");
        port.PeerLive.Should().Equal(true);
    }

    [Fact]
    public async Task 伺服器沒有peer1_不回報能送_送出時合成not_sent()
    {
        await using var h = new EngineHarness();
        var port = UsePeerPort(h, serverPeer: false);
        await h.StartAsync();

        h.Engine.SubmitPeerCommand(Stop());
        await CommandEngineTests.WaitUntilAsync(() => !port.PeerReplies.IsEmpty, "合成結果");

        port.PeerLive.Should().BeEmpty("一直是不能送，值沒變就不回報");
        h.Server.PeerCmds.Should().BeEmpty();
        var reply = port.PeerReplies.Single();
        reply.CommandId.Should().Be(CommandId);
        reply.Message["type"]!.Value<string>().Should().Be("commandResult");
        reply.Message["state"]!.Value<string>().Should().Be(RemoteResultStates.Rejected);
        reply.Message["code"]!.Value<string>().Should().Be(PeerCommandRules.NotSent);
        reply.Message["origin"]!.Value<string>().Should().Be("transport");
    }

    [Fact]
    public async Task 能送時呼叫PeerCmd_accepted不合成()
    {
        await using var h = new EngineHarness();
        var port = UsePeerPort(h, serverPeer: true);
        await h.StartAsync();

        h.Engine.SubmitPeerCommand(Stop());
        await CommandEngineTests.WaitUntilAsync(() => !h.Server.PeerCmds.IsEmpty, "送出");
        await h.IdleAsync();

        h.Server.PeerCmds.Single().Should().BeEquivalentTo(Stop());
        port.PeerReplies.Should().BeEmpty();
    }

    [Fact]
    public async Task 伺服器回accepted_false_合成那個錯誤碼()
    {
        await using var h = new EngineHarness();
        var port = UsePeerPort(h, serverPeer: true);
        h.Server.PeerCmdResponder = c => new PeerCmdResult(false, c.CommandId, "rate_limited");
        await h.StartAsync();

        h.Engine.SubmitPeerCommand(Stop());
        await CommandEngineTests.WaitUntilAsync(() => !port.PeerReplies.IsEmpty, "合成結果");

        var reply = port.PeerReplies.Single();
        (reply.Message["code"]!.Value<string>(), reply.Message["origin"]!.Value<string>()).Should().Be(("rate_limited", "transport"));
    }

    [Fact]
    public async Task PeerReply交給port_格式不合格的略過()
    {
        await using var h = new EngineHarness();
        var port = UsePeerPort(h, serverPeer: true);
        await h.StartAsync();
        var session = h.Server.Current!;

        var good = new PeerReply(CommandId, new JObject { ["type"] = "toast", ["text"] = "ok" });
        session.Events.OnPeerReply(good);
        session.Events.OnPeerReply(new PeerReply("not-a-ulid", new JObject { ["type"] = "toast" }));
        session.Events.OnPeerReply(new PeerReply(CommandId, new JObject { ["text"] = "沒有 type" }));
        session.Events.OnPeerReply(null!);
        await h.IdleAsync();

        port.PeerReplies.Should().ContainSingle().Which.Should().BeSameAs(good);
    }

    [Fact]
    public void 規則_合成的結果與PeerReply的檢查()
    {
        var synthesized = PeerCommandRules.Synthesized(CommandId, null);
        synthesized.Message["code"]!.Value<string>().Should().Be(RemoteResultCodes.Invalid, "沒有錯誤碼當成 invalid");
        synthesized.Message["commandId"]!.Value<string>().Should().Be(CommandId);
        PeerCommandRules.IsValidReply(synthesized).Should().BeTrue();
        PeerCommandRules.IsValidReply(new PeerReply(CommandId, new JObject { ["type"] = "" })).Should().BeFalse();
        PeerCommandRules.IsValidReply(new PeerReply(CommandId, new JObject { ["type"] = 1 })).Should().BeFalse();
        PeerCommandRules.IsValidReply(null).Should().BeFalse();
    }
}
