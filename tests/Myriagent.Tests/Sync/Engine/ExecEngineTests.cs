using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 遠端執行指令的引擎（sync-protocol 14.11）：宿主接得住才宣告 exec1（跟著 cmd1、peer1）；輸出依序以 ExecOutput 送、回 keep false 就請宿主停止；
/// 斷線時輸出丟掉、結束碼留到下一條連線；ExecReply、ExecAbort 驗過交給宿主；ExecCancel 在能送時送
/// </summary>
public sealed class ExecEngineTests
{
    private static readonly string Cid = SyncIds.NewUlid(1_760_000_000_000);

    private static FakeCommandPort UseExecPort(EngineHarness h, bool serverExec = true, bool hostExec = true)
    {
        var port = CommandEngineTests.UsePort(h);
        port.ExecSupported = hostExec;
        h.Server.Features = serverExec
            ? [DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Exec1]
            : [DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1];
        return port;
    }

    private static ExecChunk Out(long seq, string text) => new(Cid, seq, RemoteExecRules.KindOut, text);

    private static ExecChunk Exit(long seq, int code) => new(Cid, seq, RemoteExecRules.KindExit, null, code);

    [Fact]
    public async Task 宿主接得住才宣告exec1_協商出來時回報能搬()
    {
        await using (var without = new EngineHarness())
        {
            UseExecPort(without, hostExec: false);
            await without.StartAsync();
            without.Server.Hellos.Last().Features.Should().Equal(DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Collab1, DeviceFeatureRules.Xfer1, DeviceFeatureRules.Xfer2);
        }

        await using var h = new EngineHarness();
        var port = UseExecPort(h);
        await h.StartAsync();
        h.Server.Hellos.Last().Features.Should().Equal(DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Exec1, DeviceFeatureRules.Collab1, DeviceFeatureRules.Xfer1, DeviceFeatureRules.Xfer2);
        await CommandEngineTests.WaitUntilAsync(() => port.ExecLive.Count > 0, "回報能搬");
        port.ExecLive.Should().Equal(true);
    }

    [Fact]
    public async Task 輸出依序送_keep_false時請宿主停止()
    {
        await using var h = new EngineHarness();
        var port = UseExecPort(h);
        h.Server.ExecOutputResponder = c => new ExecOutputResult(c.Seq < 2);
        await h.StartAsync();

        for (var i = 0; i < 3; i++)
            h.Engine.SubmitExecOutput(Out(i, "第 " + i + " 段"));
        h.Engine.SubmitExecOutput(Exit(3, 0));
        await CommandEngineTests.WaitUntilAsync(() => h.Server.ExecOutputs.Count == 4, "四段都送了");
        await h.IdleAsync();

        h.Server.ExecOutputs.Select(c => c.Seq).Should().Equal(0, 1, 2, 3);
        port.ExecAborts.Should().Equal((Cid, true));
    }

    [Fact]
    public async Task 伺服器沒有exec1_輸出丟掉_結束碼留著()
    {
        await using var h = new EngineHarness();
        var port = UseExecPort(h, serverExec: false);
        await h.StartAsync();

        h.Engine.SubmitExecOutput(Out(0, "x"));
        h.Engine.SubmitExecOutput(Exit(1, 0));
        h.Engine.SubmitExecCancel(Cid);
        await h.IdleAsync();
        h.Server.ExecOutputs.Should().BeEmpty();
        h.Server.ExecCancels.Should().BeEmpty();
        port.ExecLive.Should().BeEmpty("一直是不能搬，值沒變就不回報");
    }

    [Fact]
    public async Task 能送時送ExecCancel()
    {
        await using var h = new EngineHarness();
        UseExecPort(h);
        await h.StartAsync();
        h.Engine.SubmitExecCancel(Cid);
        await CommandEngineTests.WaitUntilAsync(() => !h.Server.ExecCancels.IsEmpty, "送出停止要求");
        h.Server.ExecCancels.Single().CommandId.Should().Be(Cid);
    }

    [Fact]
    public async Task ExecReply與ExecAbort交給宿主_格式不合格的略過()
    {
        await using var h = new EngineHarness();
        var port = UseExecPort(h);
        await h.StartAsync();
        var session = h.Server.Current!;

        var good = Out(0, "hi");
        session.Events.OnExecReply(good);
        session.Events.OnExecReply(new ExecChunk("bad", 0, RemoteExecRules.KindOut, "hi"));
        session.Events.OnExecReply(new ExecChunk(Cid, 0, "weird", "hi"));
        session.Events.OnExecAbort(new ExecAbort(Cid));
        session.Events.OnExecAbort(new ExecAbort("bad"));
        await h.IdleAsync();

        port.ExecReplies.Should().ContainSingle().Which.Should().BeSameAs(good);
        port.ExecAborts.Should().Equal((Cid, false));
    }
}
