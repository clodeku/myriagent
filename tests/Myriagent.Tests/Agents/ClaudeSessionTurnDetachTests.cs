using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 代理主機 H2（設計 docs/plans/2026-11-26-agent-host.md 第 10 節）：Claude 的一輪進行中放開程序、另一個會話物件（等於新版的 app）接回。
/// 接回的會話從檢查點重播這一輪已經處理過的輸出來重建解析器的狀態——不重發事件、不重複記用量，之後的輸出（剩下的回覆、
/// 工具的結果、這一輪的結束）照常收到。假的 claude 經真的代理主機開，以檔案當閘門控制它走到哪一步
/// </summary>
public sealed class ClaudeSessionTurnDetachTests : ClaudeSessionFakeCliTestsBase
{
    private readonly TempDirectory hostRoot = new("claude-h2-host");

    private AgentHostClient NewHost() => new(Path.Combine(AppContext.BaseDirectory, AgentHostClient.ExecutableName), hostRoot.Root, hostRoot.Combine("host.log"));

    private string Gates => hostRoot.CreateDirectory("gates");

    private AgentSessionSettings Turn2(AgentHostClient host, bool started = false)
        => Settings("h2turn", started, env: new Dictionary<string, string> { ["FAKE_GATE"] = Gates }) with { ProcessHost = host };

    private void Open(string gate) => File.WriteAllText(Path.Combine(Gates, gate), "go");

    private static async Task WaitDetachableAsync(ClaudeAgentSession session)
    {
        for (int i = 0; i < 200 && !session.CanDetachProcess; i++)
            await Task.Delay(25);
        session.CanDetachProcess.Should().BeTrue();
    }

    [Fact]
    public async Task 回覆串流到一半放開_工具執行中再放開一次_每次換會話接回_事件不重複_用量不重複_這一輪照常結束()
    {
        // ── 第一個 app：送出，回覆串流到一半 ──
        var host1 = NewHost();
        var first = new ClaudeAgentSession();
        using var log1 = new EventLog(first);
        await first.SendAsync(Input("開始"), Turn2(host1));
        var part1 = await log1.UntilAsync(e => e is AgentTextDeltaEvent);
        var blockId = part1.OfType<AgentTextDeltaEvent>().Single().BlockId;
        part1.OfType<AgentTextDeltaEvent>().Single().Delta.Should().Be("half one, ");
        first.IsRunning.Should().BeTrue();
        var instance = first.ProcessInstance;
        await WaitDetachableAsync(first);
        part1.AddRange(log1.Drain(200));

        var state1 = await first.DetachProcessAsync();

        state1.Should().NotBeNull();
        state1!.Value<int>("Version").Should().Be(ClaudeAgentSession.DetachState.TurnVersion, "一輪進行中放開的");
        state1["Turn"]!.Value<bool>("Running").Should().BeTrue();
        state1.Value<long>("ReplayOut").Should().BeLessThan(state1["Process"]!.Value<long>("OutOffset"), "這一輪已經處理過的輸出要重播");
        first.HasLiveProcess.Should().BeFalse();
        first.IsRunning.Should().BeFalse("這個會話物件不再有進行中的一輪：它在主機那邊照跑");
        var afterDetach = log1.Drain(300);
        afterDetach.Should().NotContain(e => e is AgentTurnCompletedEvent || e is AgentProcessExitedEvent, "放開不是這一輪結束，也不是程序結束");
        host1.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        first.Dispose();

        // ── 第二個 app：接回，剩下的回覆接在同一個區塊；工具開始執行 ──
        var host2 = NewHost();
        var second = new ClaudeAgentSession();
        using var log2 = new EventLog(second);
        (await second.AttachProcessAsync(JObject.Parse(state1.ToString()), Turn2(host2, started: true))).Should().BeTrue();
        second.ProcessInstance.Should().Be(instance);
        second.IsRunning.Should().BeTrue("這一輪還在進行");
        second.CanDetachProcess.Should().BeTrue("接回之後檢查點還在：可以再放開");
        var attachEvents = log2.Drain(300);
        attachEvents.Should().NotContain(e => e is AgentTextDeltaEvent || e is AgentTextCompletedEvent || e is AgentTokenUsageEvent || e is AgentSessionStartedEvent,
            "重播的那一段上一個 app 處理過了：不重發");
        attachEvents.OfType<AgentProcessStartedEvent>().Should().ContainSingle().Which.ProcessInstance.Should().Be(instance);

        Open("text");
        var part2 = await log2.UntilAsync(e => e is AgentToolStartedEvent);
        part2.OfType<AgentTextDeltaEvent>().Should().ContainSingle().Which.Should().Be(new AgentTextDeltaEvent(blockId, "half two"), "同一個區塊接著來");
        part2.OfType<AgentTextCompletedEvent>().Should().ContainSingle().Which.Should().Be(new AgentTextCompletedEvent(blockId, "half one, half two"),
            "完成時是整段文字：前半段靠重播回來");
        var tool = part2.OfType<AgentToolStartedEvent>().Single();
        tool.Title.Should().Be("npm run build");
        await WaitDetachableAsync(second);
        part2.AddRange(log2.Drain(200));
        second.IsAtSafePoint.Should().BeFalse("工具在執行");

        var state2 = await second.DetachProcessAsync();

        state2.Should().NotBeNull("工具執行中也放得開：結果到的時候由接回的會話收");
        state2!["Turn"]!.Value<bool>("ToolRunning").Should().BeTrue();
        host2.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        second.Dispose();

        // ── 第三個 app：接回，工具的結果、最後的回覆與這一輪的結束 ──
        using var host3 = NewHost();
        using var third = new ClaudeAgentSession();
        using var log3 = new EventLog(third);
        (await third.AttachProcessAsync(JObject.Parse(state2.ToString()), Turn2(host3, started: true))).Should().BeTrue();
        third.IsRunning.Should().BeTrue();
        third.IsAtSafePoint.Should().BeFalse("接回之後照樣知道工具還在執行");
        log3.Drain(300).Should().NotContain(e => e is AgentToolStartedEvent || e is AgentTextCompletedEvent);

        Open("tool");
        var part3 = await log3.UntilAsync(e => e is AgentTurnCompletedEvent);
        var done = part3.OfType<AgentToolCompletedEvent>().Should().ContainSingle().Subject;
        done.ToolId.Should().Be(tool.ToolId, "結果對到重播時認得的那個工具");
        done.Status.Should().Be(AgentItemStatus.Done);
        done.Output.Should().Be("built ok");
        part3.OfType<AgentTextCompletedEvent>().Should().ContainSingle().Which.Text.Should().Be("all done");
        part3.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        third.IsRunning.Should().BeFalse();

        // 用量：三個會話合起來剛好是這一輪的（輸入 100、輸出 20），沒有哪一筆被記兩次
        var usage = part1.Concat(afterDetach).Concat(attachEvents).Concat(part2).Concat(part3).OfType<AgentTokenUsageEvent>().ToList();
        usage.Sum(u => u.Delta.InputTokens).Should().Be(100);
        usage.Sum(u => u.Delta.OutputTokens).Should().Be(20);

        // 之後的送出沿用同一個程序
        var next = third.SendAsync(Input("再一次"), Turn2(host3, started: true));
        var again = await log3.UntilAsync(e => e is AgentTurnCompletedEvent);
        await next;
        again.OfType<AgentProcessStartedEvent>().Should().BeEmpty("沒有換程序");
        again.OfType<AgentToolCompletedEvent>().Should().ContainSingle();
        third.ProcessInstance.Should().Be(instance);
        await third.CloseAsync();
    }

    [Fact]
    public async Task 放開之後放棄_同一個會話接回_這一輪接著進行()
    {
        using var host = NewHost();
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Turn2(host);
        await session.SendAsync(Input("開始"), settings);
        await log.UntilAsync(e => e is AgentTextDeltaEvent);
        await WaitDetachableAsync(session);
        var state = await session.DetachProcessAsync();
        state.Should().NotBeNull();
        log.Drain(200);

        (await session.AttachProcessAsync(state!, settings with { SessionStarted = true })).Should().BeTrue();

        session.IsRunning.Should().BeTrue();
        Open("text");
        Open("tool");
        var rest = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        rest.OfType<AgentTextCompletedEvent>().Select(t => t.Text).Should().Equal("half one, half two", "all done");
        rest.OfType<AgentToolCompletedEvent>().Should().ContainSingle().Which.Output.Should().Be("built ok");
        rest.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        await session.CloseAsync();
    }

    [Fact]
    public async Task 等使用者回應時不放開_一輪中的狀態讀不懂的版本接不回去()
    {
        using var host = NewHost();
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("elicitstop") with { ProcessHost = host });
        await log.UntilAsync(e => e is AgentQuestionEvent);
        await Task.Delay(200);
        session.CanDetachProcess.Should().BeFalse("問卷的回覆要寫回這個程序：等使用者回應時不放");
        (await session.DetachProcessAsync()).Should().BeNull();
        session.HasLiveProcess.Should().BeTrue();
        await session.CloseAsync();

        using var other = new ClaudeAgentSession();
        var broken = new JObject
        {
            ["Version"] = ClaudeAgentSession.DetachState.TurnVersion,
            ["Process"] = JObject.FromObject(new AgentProcessDetachState(1, 1, 10, 0, "x", 0)),
        };
        (await other.AttachProcessAsync(broken, Settings("h2turn", started: true) with { ProcessHost = host })).Should().BeFalse("一輪中的狀態沒有檢查點：接不回去");
    }

    public new void Dispose()
    {
        base.Dispose();
        hostRoot.Dispose();
    }
}
