using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Newtonsoft.Json.Linq;
using static Myriagent.Tests.Sync.Engine.CommandEngineTests;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 指令結果與輸出的送出（WP4.4 計畫步 4；D10、D36）：同一條連線依序一次送一則、輸出先於結果、結果之後的輸出丟掉；斷線時結果留到下一條協商出 cmd1 的連線
/// （最多 64 則）、輸出丟掉；送出的錯誤照 ReplyBusy；引擎停止途中的結果不送、下次啟動是 unknown
/// </summary>
public sealed class CommandDeliveryOrderTests
{
    private static JObject Toast(string text) => new() { ["type"] = "toast", ["text"] = text };

    /// <summary>這條連線上與指令有關的 hub 呼叫（方法名稱，依序）</summary>
    private static List<string> CommandCalls(EngineHarness h, int generation) =>
        h.Server.Calls.Where(c => c.StartsWith(generation + ":Command", StringComparison.Ordinal)).Select(c => c[(c.IndexOf(':') + 1)..]).ToList();

    private static async Task WaitStateAsync(EngineHarness h, SyncEngineState state) =>
        await WaitUntilAsync(() => h.Engine.InvokeAsync(v => v.State).GetAwaiter().GetResult() == state, $"引擎進入 {state}");

    /// <summary>有指令卡在中樞時撥快（不能等引擎閒下來）：每觸發一個計時器就等引擎執行緒處理完排著的工作</summary>
    private static Task AdvanceBusyAsync(EngineHarness h, TimeSpan by) => h.Time.AdvanceAsync(by, () => h.Engine.InvokeAsync(_ => 0));

    [Fact]
    public async Task 輸出先於結果_依序送出_一次一則()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        h.Server.HoldCommandSends = true;
        port.Script = (b, output) =>
        {
            output(Toast(b.Cid + "-1"));
            output(Toast(b.Cid + "-2"));
            return Task.FromResult(FakeCommandPort.Done(b));
        };
        var first = f.Sign(f.Body());
        var second = f.Sign(f.Body(CommandEnvelopeFactory.Rename("第二個")));

        f.Send(first);
        f.Send(second);
        for (var i = 1; i <= 6; i++)
        {
            await WaitUntilAsync(() => h.Server.HeldCommandSends == 1, $"第 {i} 則送出、回應扣住");
            CommandCalls(h, 1).Should().HaveCount(i, "前一則的回應回來之前不送下一則");
            h.Server.ReleaseCommandSends();
        }

        await h.IdleAsync();
        CommandCalls(h, 1).Should().Equal("CommandOutput", "CommandOutput", "CommandResult", "CommandOutput", "CommandOutput", "CommandResult");
        h.Server.CommandOutputs.Select(o => o.CommandId).Should().Equal(first.CommandId, first.CommandId, second.CommandId, second.CommandId);
        h.Server.CommandOutputs.Select(o => (string?)o.Message["text"]).Should().Equal(
            first.CommandId + "-1", first.CommandId + "-2", second.CommandId + "-1", second.CommandId + "-2");
        h.Server.CommandResults.Select(r => r.CommandId).Should().Equal(first.CommandId, second.CommandId);
    }

    [Fact]
    public async Task 結果送出後的輸出丟掉()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        Action<JObject>? late = null;
        port.Script = (b, output) =>
        {
            late = output;
            return Task.FromResult(FakeCommandPort.Done(b));
        };
        f.Send(f.Sign(f.Body()));
        await h.IdleAsync();
        h.Server.CommandResults.Should().ContainSingle();

        h.Ui.Invoke(() => late!(Toast("晚到")));
        await h.IdleAsync();

        h.Server.CommandOutputs.Should().BeEmpty("結果之後不再有那個指令的輸出（伺服器契約 C11）");
    }

    [Fact]
    public async Task 斷線時結果保留_下一條連線交握後補送_輸出丟掉()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var gate = new TaskCompletionSource<RemoteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<JObject>? output = null;
        port.Script = (_, o) =>
        {
            output = o;
            o(Toast("執行中"));
            return gate.Task;
        };
        var body = f.Body();
        f.Send(f.Sign(body));
        await WaitUntilAsync(() => h.Server.CommandOutputs.Count == 1, "執行中的輸出送出");

        h.Server.Current!.Drop();
        await WaitStateAsync(h, SyncEngineState.Backoff);
        h.Ui.Invoke(() => output!(Toast("斷線後")));
        gate.SetResult(FakeCommandPort.Done(body));
        await h.IdleAsync();
        h.Server.CommandResults.Should().BeEmpty("斷線了：結果留在記憶體");
        (await h.Engine.PendingCommandResultsForTestAsync()).Should().Be(1);

        await h.AdvanceAsync(1);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Server.CommandResults.Should().ContainSingle().Which.Should().Be(new CommandResult(body.Cid, RemoteResultStates.Done, Tier: 1, FullAccess: false));
        CommandCalls(h, 2).Should().Equal("CommandResult");
        h.Server.CommandOutputs.Should().ContainSingle("斷線後的輸出丟掉，也不在新連線補送");
        (await h.Engine.PendingCommandResultsForTestAsync()).Should().Be(0);
    }

    [Fact]
    public async Task 新連線沒有cmd1_結果留著不送_之後有cmd1的連線才送()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var gate = new TaskCompletionSource<RemoteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        port.Script = (_, _) => gate.Task;
        var body = f.Body();
        f.Send(f.Sign(body));
        await WaitUntilAsync(() => port.Runs.Count == 1, "進了閘門");

        // 伺服器回滾到沒有 cmd1 的版本：重連後的交集沒有 cmd1
        h.Server.Features = null;
        h.Server.Current!.Drop();
        await WaitStateAsync(h, SyncEngineState.Backoff);
        await AdvanceBusyAsync(h, TimeSpan.FromSeconds(1));
        await WaitStateAsync(h, SyncEngineState.Live);
        (await h.Inspect(v => v.Features)).Should().BeEmpty();
        gate.SetResult(FakeCommandPort.Done(body));
        await h.IdleAsync();
        h.Server.CommandResults.Should().BeEmpty("這條連線沒有 cmd1");
        h.Server.Calls.Should().NotContain("2:CommandResult");

        h.Server.Features = [DeviceFeatureRules.Cmd1];
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(2);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Server.CommandResults.Should().ContainSingle().Which.CommandId.Should().Be(body.Cid);
        CommandCalls(h, 3).Should().Equal("CommandResult");
    }

    [Fact]
    public async Task 保留超過64則_丟最舊的並記錄()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var gates = new Dictionary<string, TaskCompletionSource<RemoteOutcome>>(StringComparer.Ordinal);
        var deliveries = Enumerable.Range(0, SyncCommandRules.MaxPendingResults + 1)
            .Select(i => f.Sign(f.Body(CommandEnvelopeFactory.Rename("第 " + i + " 個"))))
            .ToList();
        foreach (var d in deliveries)
            gates[d.CommandId] = new TaskCompletionSource<RemoteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        port.Script = (b, _) => gates[b.Cid].Task;
        foreach (var d in deliveries)
            f.Send(d);
        await WaitUntilAsync(() => port.Runs.Count == deliveries.Count, "每一個都進了閘門");

        h.Server.Current!.Drop();
        await WaitStateAsync(h, SyncEngineState.Backoff);
        foreach (var d in deliveries)
        {
            gates[d.CommandId].SetResult(new RemoteOutcome(RemoteResultStates.Done, null, null, 1, false, null));
            await WaitUntilAsync(() => Audit(h).Single(a => a.CommandId == d.CommandId).Result == RemoteResultStates.Done, "依序完成");
        }

        await h.IdleAsync();
        (await h.Engine.PendingCommandResultsForTestAsync()).Should().Be(SyncCommandRules.MaxPendingResults);
        await h.AdvanceAsync(1);

        h.Server.CommandResults.Select(r => r.CommandId).Should().Equal(deliveries.Skip(1).Select(d => d.CommandId), "超過 64 則丟最舊的");
    }

    [Fact]
    public async Task 送出遇ClientBug_只記錄不重連_其他錯誤放回並斷線()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var rejected = f.Sign(f.Body());
        h.Server.CommandSendFailure = m => m is CommandResult r && r.CommandId == rejected.CommandId
            ? new SyncTransportException(SyncFailureRules.Classify(null, SyncErrorCodes.Invalid, false))
            : null;

        f.Send(rejected);
        await h.IdleAsync();
        var next = f.Sign(f.Body(CommandEnvelopeFactory.Rename("下一個")));
        f.Send(next);
        await h.IdleAsync();

        h.Server.CommandResults.Select(r => r.CommandId).Should().Equal(new[] { next.CommandId }, "ClientBug：丟掉那則、照常送下一則");
        h.Server.Sessions.Should().ContainSingle("ClientBug 不重連");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);

        var failOnce = 1;
        h.Server.CommandSendFailure = m => m is CommandResult && Interlocked.Exchange(ref failOnce, 0) == 1 ? EngineHarness.Transient() : null;
        var third = f.Sign(f.Body(CommandEnvelopeFactory.Rename("第三個")));
        f.Send(third);
        await h.IdleAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff, "暫時性的錯誤：斷線退避");
        (await h.Engine.PendingCommandResultsForTestAsync()).Should().Be(1, "結果放回、留到下一條連線");

        await h.AdvanceAsync(1);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Server.CommandResults.Select(r => r.CommandId).Should().Equal(next.CommandId, third.CommandId);
        CommandCalls(h, 2).Should().Equal("CommandResult");
        port.Executed.Should().HaveCount(3, "送出失敗不重做");
    }

    [Fact]
    public async Task 停止途中_結果不送_下次啟動是unknown()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var gate = new TaskCompletionSource<RemoteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        port.Script = (_, _) => gate.Task;
        var body = f.Body();
        f.Send(f.Sign(body));
        await WaitUntilAsync(() => port.Runs.Count == 1, "進了閘門");

        await h.Engine.StopAsync(TimeSpan.FromSeconds(5));
        gate.SetResult(FakeCommandPort.Done(body));
        await h.Ui.DrainAsync();

        h.Server.CommandResults.Should().BeEmpty("停止途中：結果送不出去");
        Seen(h).Should().ContainSingle().Which.State.Should().Be(SyncCommandRules.ExecutingState, "CompleteCommand 也不寫");

        await h.StartAsync();
        Seen(h).Should().ContainSingle().Which.State.Should().Be(RemoteResultStates.Unknown, "下次啟動改成 unknown");
        f.Send(f.Sign(body with { Conn = f.Conn }));
        await h.IdleAsync();

        port.Calls.Should().ContainSingle("不重做");
        h.Server.CommandResults.Should().ContainSingle().Which.Should().Be(SyncCommandRules.UnknownResult(body.Cid));
    }
}
