using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 代理主機 H2 的 Grok（GrokAgentSession.Detach.cs；設計 docs/plans/2026-11-26-agent-host.md 第 10 節）：假的 grok agent 經真的代理主機開，
/// 一輪進行中（指令在跑、回覆串流到一半）與閒置等背景工作時放開程序，另一個會話物件（等於新版的 app）接回——
/// 解析器的狀態直接帶過去：事件不重複、同一個文字區塊接著寫、這一輪照常由 session/prompt 的回應收尾，之後的訊息沿用同一個程序
/// </summary>
public sealed class GrokSessionDetachTests : GrokSessionFakeCliTestsBase
{
    private readonly TempDirectory hostRoot = new("grok-h2-host");

    private AgentHostClient NewHost() => new(Path.Combine(AppContext.BaseDirectory, AgentHostClient.ExecutableName), hostRoot.Root, hostRoot.Combine("host.log"));

    private static void Open(Fake fake, string gate) => File.WriteAllText(Path.Combine(fake.Work, gate), "go");

    private static async Task WaitDetachableAsync(GrokAgentSession session)
    {
        for (int i = 0; i < 200 && !session.CanDetachProcess; i++)
            await Task.Delay(25);
        session.CanDetachProcess.Should().BeTrue();
    }

    private static List<AgentEvent> Drain(System.Collections.Concurrent.BlockingCollection<AgentEvent> events, int milliseconds)
    {
        var drained = new List<AgentEvent>();
        while (events.TryTake(out var ev, milliseconds))
            drained.Add(ev);
        return drained;
    }

    [Fact]
    public async Task 指令執行中放開_回覆串流到一半再放開一次_每次換會話接回_事件不重複_同一個區塊接著寫_這一輪照常結束()
    {
        var fake = new Fake(temp, "h2turn");

        // ── 第一個 app：送出，指令在跑 ──
        var host1 = NewHost();
        var first = new GrokAgentSession();
        var events1 = Collect(first);
        await first.SendAsync(Input("開始"), fake.Settings() with { ProcessHost = host1 });
        var part1 = await TakeUntilAsync(events1, ev => ev is AgentToolOutputEvent);
        await fake.WaitForLogAsync("p1:tool-running");
        part1.OfType<AgentToolStartedEvent>().Single().ToolId.Should().Be("call-h2");
        var firstText = part1.OfType<AgentTextCompletedEvent>().Single();
        firstText.Text.Should().Be("working");
        var instance = first.ProcessInstance;
        await WaitDetachableAsync(first);
        first.DetachBlocker.Should().BeNull();
        first.IsRunning.Should().BeTrue();
        first.IsAtSafePoint.Should().BeFalse("指令在執行");
        part1.AddRange(Drain(events1, 200));

        var state1 = await first.DetachProcessAsync();

        state1.Should().NotBeNull("指令執行中也放得開：結果到的時候由接回的會話收");
        state1!["Turn"]!.Value<long>("PromptRequestId").Should().BeGreaterThan(0);
        first.HasLiveProcess.Should().BeFalse();
        first.IsRunning.Should().BeFalse("這個會話物件不再有進行中的一輪：它在主機那邊照跑");
        Drain(events1, 300).Should().NotContain(e => e is AgentTurnCompletedEvent || e is AgentProcessExitedEvent || e is AgentToolCompletedEvent,
            "放開不是這一輪結束、不是程序結束，指令也沒有被中斷");
        host1.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        first.Dispose();

        // ── 第二個 app：接回，指令的結果與半句回覆 ──
        var host2 = NewHost();
        var second = new GrokAgentSession();
        var events2 = Collect(second);
        (await second.AttachProcessAsync(JObject.Parse(state1.ToString()), fake.Settings(started: true) with { ProcessHost = host2 })).Should().BeTrue();
        second.ProcessInstance.Should().Be(instance);
        second.IsRunning.Should().BeTrue("這一輪還在進行");
        second.IsAtSafePoint.Should().BeFalse("接回之後照樣知道指令還在執行");
        Open(fake, "gate-a");
        var part2 = await TakeUntilAsync(events2, ev => ev is AgentTextDeltaEvent);
        await fake.WaitForLogAsync("p1:half");
        part2.OfType<AgentProcessStartedEvent>().Single().ProcessInstance.Should().Be(instance);
        part2.Should().NotContain(e => e is AgentToolStartedEvent, "工具卡在放開之前就發過了：不重發");
        part2.OfType<AgentToolOutputEvent>().Select(e => e.Delta).Should().Equal(new[] { " more" }, "只送新增的輸出：之前的「partial」記在帶過來的狀態裡");
        var completedTool = part2.OfType<AgentToolCompletedEvent>().Single();
        completedTool.ToolId.Should().Be("call-h2");
        completedTool.Status.Should().Be(AgentItemStatus.Done);
        var half = part2.OfType<AgentTextDeltaEvent>().Single();
        half.Delta.Should().Be("half");
        half.BlockId.Should().NotBe(firstText.BlockId, "新的文字區塊接著編號，不和放開之前的撞");
        half.BlockId.Should().StartWith(firstText.BlockId[..firstText.BlockId.LastIndexOf('-')], "區塊 id 的前綴跟著程序走");
        await WaitDetachableAsync(second);
        second.IsAtSafePoint.Should().BeTrue("沒有工具在執行");

        var state2 = await second.DetachProcessAsync();

        state2.Should().NotBeNull("回覆串流到一半也放得開");
        state2!["Parser"]!.Value<string>("Text").Should().Be("half");
        Drain(events2, 300).Should().NotContain(e => e is AgentTurnCompletedEvent || e is AgentProcessExitedEvent || e is AgentTextCompletedEvent);
        host2.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        second.Dispose();

        // ── 第三個 app：接回，剩下的回覆與這一輪的結束 ──
        using var host3 = NewHost();
        using var third = new GrokAgentSession();
        var events3 = Collect(third);
        (await third.AttachProcessAsync(JObject.Parse(state2.ToString()), fake.Settings(started: true) with { ProcessHost = host3 })).Should().BeTrue();
        third.IsRunning.Should().BeTrue();
        Open(fake, "gate-b");
        var part3 = await TakeUntilAsync(events3, ev => ev is AgentTurnCompletedEvent);
        part3.OfType<AgentTextDeltaEvent>().Single().Should().Be(new AgentTextDeltaEvent(half.BlockId, " rest"), "同一個文字區塊接著寫");
        part3.OfType<AgentTextCompletedEvent>().Single().Should().Be(new AgentTextCompletedEvent(half.BlockId, "half rest"), "完整的文字：前半段在帶過來的狀態裡");
        part3.OfType<AgentTokenUsageEvent>().Should().ContainSingle("這一輪的用量只在回應裡、只記一次");
        part3.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        part3.Should().NotContain(e => e is AgentToolStartedEvent || e is AgentToolCompletedEvent);
        third.IsRunning.Should().BeFalse();

        // 之後的訊息：同一個程序、同一個對話（不重開、不重新續接）
        var next = await TurnAsync(third, events3, fake.Settings(started: true) with { ProcessHost = host3 }, "再來");
        next.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        next.Should().NotContain(e => e is AgentProcessStartedEvent);
        third.ProcessInstance.Should().Be(instance);
        var log = fake.Log();
        log.Should().OnlyContain(l => l.StartsWith("p1:", StringComparison.Ordinal), "從頭到尾同一個 grok 程序");
        log.Count(l => l.StartsWith("p1:session/", StringComparison.Ordinal)).Should().Be(1, "對話只建立一次，接回不重新續接");
        log.Count(l => l.StartsWith("p1:prompt ", StringComparison.Ordinal)).Should().Be(2);
        await third.CloseAsync();
    }

    [Fact]
    public async Task 閒置等背景工作_放開_新的會話接回_背景工作照舊_之後的訊息沿用同一個程序()
    {
        var fake = new Fake(temp, "background_idle");
        var host1 = NewHost();
        var first = new GrokAgentSession();
        var events1 = Collect(first);
        await first.SendAsync(Input("放背景"), fake.Settings() with { ProcessHost = host1 });
        await TakeUntilAsync(events1, e => e is AgentTurnCompletedEvent);
        first.HasLiveBackgroundWork.Should().BeTrue();
        using var child = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fake.Work, "bg-pid.txt"))));
        var instance = first.ProcessInstance;
        await WaitDetachableAsync(first);   // 這一輪結束後查額度的那一下放不開：等它做完

        var state = await first.DetachProcessAsync();

        state.Should().NotBeNull();
        state!["Turn"]!.Type.Should().Be(JTokenType.Null, "閒置：沒有進行中的一輪");
        first.HasLiveProcess.Should().BeFalse();
        Drain(events1, 300).Should().NotContain(e => e is AgentBackgroundWorkEvent || e is AgentProcessExitedEvent, "不回報背景工作沒了：它還在跑");
        child.HasExited.Should().BeFalse("背景工作沒有被動到");
        host1.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        first.Dispose();
        child.HasExited.Should().BeFalse();

        using var host2 = NewHost();
        using var second = new GrokAgentSession();
        var events2 = Collect(second);
        (await second.AttachProcessAsync(JObject.Parse(state.ToString()), fake.Settings(started: true) with { ProcessHost = host2 })).Should().BeTrue();
        var attached = Drain(events2, 300);
        attached.OfType<AgentProcessStartedEvent>().Single().ProcessInstance.Should().Be(instance);
        attached.OfType<AgentBackgroundWorkEvent>().Single().Count.Should().Be(1, "接回時回報背景工作");
        second.HasLiveBackgroundWork.Should().BeTrue();
        second.IsRunning.Should().BeFalse();
        (await second.GetTaskOutputAsync("bg1")).Output.Should().Contain("hello", "背景工作的輸出檔路徑也帶過來了");
        second.CanDetachProcess.Should().BeTrue("接回的程序照樣放得開（下一次換版）");

        var next = await TurnAsync(second, events2, fake.Settings(started: true) with { ProcessHost = host2 }, "再來");
        next.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        next.Should().NotContain(e => e is AgentProcessStartedEvent);
        fake.Log().Should().OnlyContain(l => l.StartsWith("p1:", StringComparison.Ordinal), "同一個 grok 程序");
        fake.Log().Count(l => l.StartsWith("p1:session/", StringComparison.Ordinal)).Should().Be(1);

        (await second.StopBackgroundWorkAsync(null)).Stopped.Should().Be(1);
        child.WaitForExit(10_000).Should().BeTrue();
    }

    [Fact]
    public async Task 等使用者回應時放不開_直接開的程序放不開_狀態讀不懂的接不回去()
    {
        using var host = NewHost();
        var fake = new Fake(temp, "permission");
        using var session = new GrokAgentSession();
        var events = Collect(session);
        await session.SendAsync(Input("exec"), fake.Settings() with { ProcessHost = host });
        var approval = (AgentApprovalEvent)(await TakeUntilAsync(events, e => e is AgentApprovalEvent)).Last();
        await Task.Delay(200);
        session.CanDetachProcess.Should().BeFalse("有等回應的請求：放開的話那個回覆沒有人收");
        session.DetachBlocker.Should().Be("waitingUser");
        (await session.DetachProcessAsync()).Should().BeNull();
        session.HasLiveProcess.Should().BeTrue();
        await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        (await TakeUntilAsync(events, e => e is AgentTurnCompletedEvent)).OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        await session.CloseAsync();

        // MYRIAGENT_TEST_VIA_HOST=1 時沒指定主機的程序也經主機開（HostedProcessMode）：這一段只在平常的模式有意義
        if (!HostedProcessMode.Enabled)
        {
            var directFake = new Fake(temp, "basic");
            using var direct = new GrokAgentSession();
            var directEvents = Collect(direct);
            await TurnAsync(direct, directEvents, directFake.Settings(), "你好");
            await Task.Delay(300);
            direct.CanDetachProcess.Should().BeFalse("不是經主機開的");
            direct.DetachBlocker.Should().Be("notHosted");
            (await direct.DetachProcessAsync()).Should().BeNull();
            direct.HasLiveProcess.Should().BeTrue();
            await direct.CloseAsync();
        }

        using var fresh = new GrokAgentSession();
        var basic = new Fake(temp, "basic");
        (await fresh.AttachProcessAsync(new JObject { ["Version"] = 99 }, basic.Settings() with { ProcessHost = host })).Should().BeFalse("版本太新");
        (await fresh.AttachProcessAsync(new JObject { ["Version"] = 1 }, basic.Settings() with { ProcessHost = host })).Should().BeFalse("沒有程序");
        (await fresh.AttachProcessAsync(new JObject
        {
            ["Version"] = 1,
            ["ProcessSessionId"] = "s-pre",
            ["Process"] = JObject.FromObject(new AgentProcessDetachState(999, 1, 0, 0, "nope", 0)),
        }, basic.Settings() with { ProcessHost = host })).Should().BeFalse("主機上沒有這個程序");
        fresh.HasLiveProcess.Should().BeFalse();
    }
}
