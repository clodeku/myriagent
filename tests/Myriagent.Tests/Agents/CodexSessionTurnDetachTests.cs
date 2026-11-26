using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 代理主機 H2（設計 docs/plans/2026-11-26-agent-host.md 第 10 節）：Codex 的一輪進行中放開程序、另一個會話物件（等於新版的 app）接回。
/// 會話自己記的這一輪狀態（執行中的指令…）照放開當下還原，解析器從檢查點重播——不重發事件、token 不重複記，
/// 之後的通知（指令的結果、剩下的回覆、這一輪的結束）照常收到。假的 app-server 經真的代理主機開，以檔案當閘門控制它走到哪一步
/// </summary>
public sealed class CodexSessionTurnDetachTests : CodexAgentSessionTestsBase
{
    private readonly TempDirectory hostRoot = new("codex-h2-host");

    private AgentHostClient NewHost() => new(Path.Combine(AppContext.BaseDirectory, AgentHostClient.ExecutableName), hostRoot.Root, hostRoot.Combine("host.log"));

    private string Gates => hostRoot.CreateDirectory("gates");

    private AgentSessionSettings Turn2(AgentHostClient host, bool started = false)
        => Settings("h2turn", sessionId: started ? "thread-fake" : null) with { ConfigureEnvironment = env => env["FAKE_GATE"] = Gates, ProcessHost = host };

    private void Open(string gate) => File.WriteAllText(Path.Combine(Gates, gate), "go");

    private static AgentTurnInput Input(string text) => new(text, Array.Empty<AgentAttachment>());

    private static async Task WaitDetachableAsync(CodexAgentSession session)
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
    public async Task 指令執行中放開_回覆串流到一半再放開一次_每次換會話接回_事件不重複_token不重複_這一輪照常結束()
    {
        // ── 第一個 app：送出，指令在跑 ──
        var host1 = NewHost();
        var first = new CodexAgentSession();
        var events1 = Collect(first);
        await first.SendAsync(Input("開始"), Turn2(host1));
        var part1 = await TakeUntilAsync(events1, ev => ev is AgentToolStartedEvent);
        var tool = part1.OfType<AgentToolStartedEvent>().Single();
        tool.ToolId.Should().Be("c9");
        var instance = first.ProcessInstance;
        await WaitDetachableAsync(first);
        first.DetachBlocker.Should().BeNull();
        first.IsRunning.Should().BeTrue();
        first.IsAtSafePoint.Should().BeFalse("指令在執行");
        part1.AddRange(Drain(events1, 200));

        var state1 = await first.DetachProcessAsync();

        state1.Should().NotBeNull("指令執行中也放得開：結果到的時候由接回的會話收");
        state1!.Value<int>("Version").Should().Be(CodexAgentSession.DetachState.TurnVersion);
        state1["Turn"]!.Value<bool>("Running").Should().BeTrue();
        ((JArray)state1["Turn"]!["RunningTools"]!).Should().ContainSingle();
        first.HasLiveProcess.Should().BeFalse();
        first.IsRunning.Should().BeFalse("這個會話物件不再有進行中的一輪：它在主機那邊照跑");
        var afterDetach = Drain(events1, 300);
        afterDetach.Should().NotContain(e => e is AgentTurnCompletedEvent || e is AgentProcessExitedEvent || e is AgentToolCompletedEvent,
            "放開不是這一輪結束、不是程序結束，指令也沒有被中斷");
        host1.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        first.Dispose();

        // ── 第二個 app：接回，指令的結果與前半段回覆 ──
        var host2 = NewHost();
        var second = new CodexAgentSession();
        var events2 = Collect(second);
        (await second.AttachProcessAsync(JObject.Parse(state1.ToString()), Turn2(host2, started: true))).Should().BeTrue();
        second.ProcessInstance.Should().Be(instance);
        second.IsRunning.Should().BeTrue("這一輪還在進行");
        second.IsAtSafePoint.Should().BeFalse("接回之後照樣知道指令還在執行");
        var attachEvents = Drain(events2, 300);
        attachEvents.Should().NotContain(e => e is AgentToolStartedEvent || e is AgentSessionStartedEvent || e is AgentTokenUsageEvent,
            "重播的那一段上一個 app 處理過了：不重發");
        attachEvents.OfType<AgentProcessStartedEvent>().Should().ContainSingle().Which.ProcessInstance.Should().Be(instance);

        Open("tool");
        var part2 = await TakeUntilAsync(events2, ev => ev is AgentTextDeltaEvent);
        var done = part2.OfType<AgentToolCompletedEvent>().Should().ContainSingle().Subject;
        done.ToolId.Should().Be("c9");
        done.Output.Should().Be("built ok");
        var delta = part2.OfType<AgentTextDeltaEvent>().Single();
        delta.Delta.Should().Be("half one, ");
        await WaitDetachableAsync(second);
        part2.AddRange(Drain(events2, 200));
        second.IsAtSafePoint.Should().BeTrue("指令結束了：只是回覆還在串流");

        var state2 = await second.DetachProcessAsync();

        state2.Should().NotBeNull();
        host2.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        second.Dispose();

        // ── 第三個 app：接回，後半段回覆與這一輪的結束 ──
        using var host3 = NewHost();
        using var third = new CodexAgentSession();
        var events3 = Collect(third);
        (await third.AttachProcessAsync(JObject.Parse(state2.ToString()), Turn2(host3, started: true))).Should().BeTrue();
        third.IsRunning.Should().BeTrue();
        Drain(events3, 300).Should().NotContain(e => e is AgentTextDeltaEvent || e is AgentToolCompletedEvent || e is AgentTokenUsageEvent);

        Open("text");
        var part3 = await TakeUntilAsync(events3, ev => ev is AgentTurnCompletedEvent);
        part3.OfType<AgentTextDeltaEvent>().Should().ContainSingle().Which.Should().Be(new AgentTextDeltaEvent(delta.BlockId, "half two"), "同一個區塊接著來");
        part3.OfType<AgentTextCompletedEvent>().Should().ContainSingle().Which.Text.Should().Be("half one, half two");
        part3.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        third.IsRunning.Should().BeFalse();

        // token：三個會話合起來剛好是這一輪的（輸入 100、輸出 20）——第二個會話記了 60／10，第三個只記之後的 40／10
        var usage = part1.Concat(afterDetach).Concat(attachEvents).Concat(part2).Concat(part3).OfType<AgentTokenUsageEvent>().ToList();
        usage.Sum(u => u.Delta.InputTokens).Should().Be(100);
        usage.Sum(u => u.Delta.OutputTokens).Should().Be(20);
        part3.OfType<AgentTokenUsageEvent>().Should().ContainSingle().Which.Delta.Should().Be(new AgentUsage(40, 10, 0, null), "上一筆的累計靠重播回來：只記差");

        // 之後的送出：同一個程序、同一個 thread
        await third.SendAsync(Input("接著做"), Turn2(host3, started: true));
        var next = await TakeUntilAsync(events3, ev => ev is AgentTurnCompletedEvent);
        next.OfType<AgentProcessStartedEvent>().Should().BeEmpty("沒有換程序");
        next.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("second:thread/start,sandbox=dangerFullAccess,sandbox=dangerFullAccess", "同一個 app-server：只建立過一次 thread、沒有續接");
        next.OfType<AgentTokenUsageEvent>().Should().ContainSingle().Which.Delta.Should().Be(new AgentUsage(50, 10, 0, null));
        await third.CloseAsync();
    }

    [Fact]
    public async Task 放開之後放棄_同一個會話接回_這一輪接著進行()
    {
        using var host = NewHost();
        using var session = new CodexAgentSession();
        var events = Collect(session);
        var settings = Turn2(host);
        await session.SendAsync(Input("開始"), settings);
        await TakeUntilAsync(events, ev => ev is AgentToolStartedEvent);
        await WaitDetachableAsync(session);
        var state = await session.DetachProcessAsync();
        state.Should().NotBeNull();
        Drain(events, 200);

        (await session.AttachProcessAsync(state!, Turn2(host, started: true))).Should().BeTrue();

        session.IsRunning.Should().BeTrue();
        Open("tool");
        Open("text");
        var rest = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        rest.OfType<AgentToolCompletedEvent>().Should().ContainSingle().Which.Output.Should().Be("built ok");
        rest.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("half one, half two");
        rest.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        await session.CloseAsync();
    }

    public new void Dispose()
    {
        base.Dispose();
        hostRoot.Dispose();
    }
}
