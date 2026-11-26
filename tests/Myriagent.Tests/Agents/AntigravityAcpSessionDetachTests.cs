using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 代理主機 H2 的 Antigravity（AntigravityAcpSession.Detach.cs；設計 docs/plans/2026-11-26-agent-host.md 第 10 節）：假的 agy acp 經真的代理主機開，
/// 一輪進行中（指令在跑、回覆串流到一半）與閒置而背景工作還在時放開程序，另一個會話物件（等於新版的 app）接回——
/// 解析器的狀態直接帶過去：事件不重複、同一個文字區塊接著寫、這一輪照常由 session/prompt 的回應收尾，之後的訊息沿用同一個程序
/// </summary>
public sealed class AntigravityAcpSessionDetachTests : AntigravityAcpSessionFakeCliTestsBase
{
    private readonly TempDirectory hostRoot = new("agy-h2-host");

    private AgentHostClient NewHost() => new(Path.Combine(AppContext.BaseDirectory, AgentHostClient.ExecutableName), hostRoot.Root, hostRoot.Combine("host.log"));

    private string Gates => hostRoot.CreateDirectory("gates");

    private AgentSessionSettings Hosted(string scenario, AgentHostClient host, string? sessionId = null)
        => Settings(scenario, sessionId: sessionId, started: sessionId != null) with
        {
            ConfigureEnvironment = env =>
            {
                env["FAKE_ACP_SCENARIO"] = scenario;
                env["FAKE_GATE"] = Gates;
            },
            ProcessHost = host,
        };

    private void Open(string gate) => File.WriteAllText(Path.Combine(Gates, gate), "go");

    private static AgentTurnInput Input(string text) => new(text, Array.Empty<AgentAttachment>());

    private static async Task WaitDetachableAsync(AntigravityAcpSession session)
    {
        for (int i = 0; i < 200 && !session.CanDetachProcess; i++)
            await Task.Delay(25);
        session.CanDetachProcess.Should().BeTrue();
    }

    /// <summary>收集一個會話的事件（測試結束前都留著，要看「之後沒有再發什麼」）</summary>
    private sealed class Collector
    {
        private readonly System.Collections.Concurrent.BlockingCollection<AgentEvent> queue = new();

        public Collector(IAgentSession session) => session.EventReceived += queue.Add;

        public async Task<List<AgentEvent>> UntilAsync(Func<AgentEvent, bool> until, double seconds = 30)
        {
            var collected = new List<AgentEvent>();
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!queue.TryTake(out var ev, 100))
                {
                    await Task.Yield();
                    continue;
                }
                collected.Add(ev);
                if (until(ev))
                    return collected;
            }
            throw new TimeoutException("假 ACP agy 逾時未完成；已收到：" + string.Join("、", collected.Select(e => e.GetType().Name)));
        }

        public List<AgentEvent> Drain(int milliseconds)
        {
            var drained = new List<AgentEvent>();
            while (queue.TryTake(out var ev, milliseconds))
                drained.Add(ev);
            return drained;
        }
    }

    [Fact]
    public async Task 指令執行中放開_回覆串流到一半再放開一次_每次換會話接回_事件不重複_同一個區塊接著寫_這一輪照常結束()
    {
        // ── 第一個 app：送出，指令在跑 ──
        var host1 = NewHost();
        var first = NewSession();
        var events1 = new Collector(first);
        await first.SendAsync(Input("開始"), Hosted("h2turn", host1));
        var part1 = await events1.UntilAsync(ev => ev is AgentToolStartedEvent);
        part1.OfType<AgentToolStartedEvent>().Single().ToolId.Should().Be("call-h2");
        var firstText = part1.OfType<AgentTextCompletedEvent>().Single();
        firstText.Text.Should().Be("working");
        var sid = part1.OfType<AgentSessionStartedEvent>().Single().SessionId;
        var instance = first.ProcessInstance;
        await WaitDetachableAsync(first);
        first.DetachBlocker.Should().BeNull();
        first.IsRunning.Should().BeTrue();
        first.IsAtSafePoint.Should().BeFalse("指令在執行");
        events1.Drain(200);

        var state1 = await first.DetachProcessAsync();

        state1.Should().NotBeNull("指令執行中也放得開：結果到的時候由接回的會話收");
        state1!.Value<long>("PromptRequestId").Should().BeGreaterThan(0);
        first.HasLiveProcess.Should().BeFalse();
        first.IsRunning.Should().BeFalse("這個會話物件不再有進行中的一輪：它在主機那邊照跑");
        events1.Drain(300).Should().NotContain(e => e is AgentTurnCompletedEvent || e is AgentProcessExitedEvent || e is AgentToolCompletedEvent,
            "放開不是這一輪結束、不是程序結束，指令也沒有被中斷");
        host1.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        first.Dispose();

        // ── 第二個 app：接回，指令的結果與半句回覆 ──
        var host2 = NewHost();
        var second = NewSession();
        var events2 = new Collector(second);
        (await second.AttachProcessAsync(JObject.Parse(state1.ToString()), Hosted("h2turn", host2, sid))).Should().BeTrue();
        second.ProcessInstance.Should().Be(instance);
        second.IsRunning.Should().BeTrue("這一輪還在進行");
        second.IsAtSafePoint.Should().BeFalse("接回之後照樣知道指令還在執行");
        Open("gate-a");
        var part2 = await events2.UntilAsync(ev => ev is AgentTextDeltaEvent);
        part2.OfType<AgentProcessStartedEvent>().Single().ProcessInstance.Should().Be(instance);
        part2.Should().NotContain(e => e is AgentToolStartedEvent, "工具卡在放開之前就發過了：不重發");
        var completedTool = part2.OfType<AgentToolCompletedEvent>().Single();
        completedTool.ToolId.Should().Be("call-h2");
        completedTool.Status.Should().Be(AgentItemStatus.Done);
        var half = part2.OfType<AgentTextDeltaEvent>().Single();
        half.Delta.Should().Be("half");
        half.BlockId.Should().NotBe(firstText.BlockId, "新的文字區塊接著編號，不和放開之前的撞");
        half.BlockId.Should().StartWith(firstText.BlockId[..firstText.BlockId.LastIndexOf('-')], "區塊 id 的前綴與輪次跟著程序走");
        await WaitDetachableAsync(second);
        second.IsAtSafePoint.Should().BeTrue("沒有工具在執行");

        var state2 = await second.DetachProcessAsync();

        state2.Should().NotBeNull("回覆串流到一半也放得開");
        state2!["Parser"]!.Value<string>("Text").Should().Be("half");
        events2.Drain(300).Should().NotContain(e => e is AgentTurnCompletedEvent || e is AgentProcessExitedEvent || e is AgentTextCompletedEvent);
        host2.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        second.Dispose();

        // ── 第三個 app：接回，剩下的回覆與這一輪的結束 ──
        using var host3 = NewHost();
        using var third = NewSession();
        var events3 = new Collector(third);
        (await third.AttachProcessAsync(JObject.Parse(state2.ToString()), Hosted("h2turn", host3, sid))).Should().BeTrue();
        third.IsRunning.Should().BeTrue();
        Open("gate-b");
        var part3 = await events3.UntilAsync(ev => ev is AgentTurnCompletedEvent);
        part3.OfType<AgentTextDeltaEvent>().Single().Should().Be(new AgentTextDeltaEvent(half.BlockId, " rest"), "同一個文字區塊接著寫");
        part3.OfType<AgentTextCompletedEvent>().Single().Should().Be(new AgentTextCompletedEvent(half.BlockId, "half rest"), "完整的文字：前半段在帶過來的狀態裡");
        part3.OfType<AgentTokenUsageEvent>().Should().ContainSingle("這一輪的用量只在回應裡、只記一次");
        part3.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        part3.Should().NotContain(e => e is AgentToolStartedEvent || e is AgentToolCompletedEvent);
        third.IsRunning.Should().BeFalse();

        // 之後的訊息：同一個程序、同一個對話（假 agy 的輪次接著數，不是新程序的第 1 輪）
        await third.SendAsync(Input("再來"), Hosted("h2turn", host3, sid));
        var next = await events3.UntilAsync(ev => ev is AgentTurnCompletedEvent);
        next.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        next.Should().NotContain(e => e is AgentProcessStartedEvent);
        var reply = JObject.Parse(next.OfType<AgentTextCompletedEvent>().Last().Text);
        reply.Value<int>("turn").Should().Be(2, "同一個 agy 程序");
        reply.Value<string>("sid").Should().Be(sid);
        third.ProcessInstance.Should().Be(instance);
        await third.CloseAsync();
    }

    [Fact]
    public async Task 閒置而背景工作還在_放開_新的會話接回_背景工作照舊_之後的訊息沿用同一個程序()
    {
        var host1 = NewHost();
        var first = NewSession();
        var events1 = new Collector(first);
        await first.SendAsync(Input("開開發伺服器"), Hosted("h2bg", host1));
        var turn = await events1.UntilAsync(e => e is AgentTurnCompletedEvent);
        var sid = turn.OfType<AgentSessionStartedEvent>().Single().SessionId;
        first.HasLiveBackgroundWork.Should().BeTrue("常駐的指令這一輪結束後還在");
        var instance = first.ProcessInstance;
        await WaitDetachableAsync(first);

        var state = await first.DetachProcessAsync();

        state.Should().NotBeNull();
        state!["PromptRequestId"]!.Type.Should().Be(JTokenType.Null, "閒置：沒有進行中的一輪");
        first.HasLiveProcess.Should().BeFalse();
        events1.Drain(300).Should().NotContain(e => e is AgentBackgroundWorkEvent || e is AgentProcessExitedEvent, "不回報背景工作沒了：它還在跑");
        host1.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        first.Dispose();

        using var host2 = NewHost();
        using var second = NewSession();
        var events2 = new Collector(second);
        (await second.AttachProcessAsync(JObject.Parse(state.ToString()), Hosted("h2bg", host2, sid))).Should().BeTrue();
        var attached = events2.Drain(300);
        attached.OfType<AgentProcessStartedEvent>().Single().ProcessInstance.Should().Be(instance);
        attached.OfType<AgentBackgroundWorkEvent>().Single().Count.Should().Be(1, "接回時回報背景工作");
        second.HasLiveBackgroundWork.Should().BeTrue();
        second.LiveBackgroundTools.Should().ContainSingle().Which.Title.Should().Contain("npm run dev");
        second.IsRunning.Should().BeFalse();
        second.CanDetachProcess.Should().BeTrue("接回的程序照樣放得開（下一次換版）");

        await second.SendAsync(Input("再來"), Hosted("h2bg", host2, sid));
        var next = await events2.UntilAsync(ev => ev is AgentTurnCompletedEvent);
        next.Should().NotContain(e => e is AgentProcessStartedEvent);
        JObject.Parse(next.OfType<AgentTextCompletedEvent>().Last().Text).Value<int>("turn").Should().Be(2, "同一個 agy 程序");
        await second.CloseAsync();
    }

    [Fact]
    public async Task 等使用者回應時放不開_直接開的程序放不開_狀態讀不懂的接不回去()
    {
        using var host = NewHost();
        using var session = NewSession();
        var events = new Collector(session);
        await session.SendAsync(Input("跑指令"), Hosted("ask_perm", host));
        var approval = (AgentApprovalEvent)(await events.UntilAsync(e => e is AgentApprovalEvent)).Last();
        await Task.Delay(200);
        session.CanDetachProcess.Should().BeFalse("有等回應的請求：放開的話那個回覆沒有人收");
        session.DetachBlocker.Should().Be("waitingUser");
        (await session.DetachProcessAsync()).Should().BeNull();
        session.HasLiveProcess.Should().BeTrue();
        await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        (await events.UntilAsync(e => e is AgentTurnCompletedEvent)).OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        await session.CloseAsync();

        // MYRIAGENT_TEST_VIA_HOST=1 時沒指定主機的程序也經主機開（HostedProcessMode）：這一段只在平常的模式有意義
        if (!HostedProcessMode.Enabled)
        {
            using var direct = NewSession();
            var directEvents = new Collector(direct);
            await direct.SendAsync(Input("你好"), Settings("default"));
            await directEvents.UntilAsync(e => e is AgentTurnCompletedEvent);
            await Task.Delay(300);
            direct.CanDetachProcess.Should().BeFalse("不是經主機開的");
            direct.DetachBlocker.Should().Be("notHosted");
            (await direct.DetachProcessAsync()).Should().BeNull();
            direct.HasLiveProcess.Should().BeTrue();
            await direct.CloseAsync();
        }

        using var fresh = NewSession();
        (await fresh.AttachProcessAsync(new JObject { ["Version"] = 99 }, Hosted("default", host))).Should().BeFalse("版本太新");
        (await fresh.AttachProcessAsync(new JObject { ["Version"] = 1 }, Hosted("default", host))).Should().BeFalse("沒有程序");
        (await fresh.AttachProcessAsync(new JObject
        {
            ["Version"] = 1,
            ["ProcessSessionId"] = "s1",
            ["Process"] = JObject.FromObject(new AgentProcessDetachState(999, 1, 0, 0, "nope", 0)),
        }, Hosted("default", host))).Should().BeFalse("主機上沒有這個程序");
        fresh.HasLiveProcess.Should().BeFalse();
    }
}
