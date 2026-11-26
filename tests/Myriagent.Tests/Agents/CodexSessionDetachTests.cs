using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// Codex 會話放開與接回程序（設計 docs/plans/2026-11-26-agent-host.md 3.3）：假的 app-server 經真的代理主機開；
/// 這一輪結束、背景指令還在跑時放開，另一個會話物件（等於新版的 app）以記下的狀態接回同一個程序——
/// 背景指令沒有被動到，跑完的 item/completed 照常收到，之後的送出用同一個程序、同一個 thread，token 接著算
/// </summary>
public sealed class CodexSessionDetachTests : CodexAgentSessionTestsBase
{
    private readonly TempDirectory hostRoot = new("codex-detach-host");

    private AgentHostClient NewHost() => new(Path.Combine(AppContext.BaseDirectory, AgentHostClient.ExecutableName), hostRoot.Root, hostRoot.Combine("host.log"));

    private AgentSessionSettings Background(AgentHostClient? host, int delayMs)
        => Settings("background_keep") with { ConfigureEnvironment = env => env["FAKE_BG_DELAY_MS"] = delayMs.ToString(), ProcessHost = host };

    private static AgentTurnInput Input(string text) => new(text, Array.Empty<AgentAttachment>());

    /// <summary>等到放得開（程序啟動時查模型清單的要求還在途時不放）</summary>
    private static async Task WaitDetachableAsync(CodexAgentSession session)
    {
        for (int i = 0; i < 100 && !session.CanDetachProcess; i++)
            await Task.Delay(50);
        session.CanDetachProcess.Should().BeTrue();
    }

    [Fact]
    public async Task 背景指令還在跑時放開_另一個會話接回同一個程序_跑完照常回報_之後的送出沿用程序與thread_token接著算()
    {
        var first = NewHost();
        var session = new CodexAgentSession();
        var events = Collect(session);
        var settings = Background(first, delayMs: 4000);
        await session.SendAsync(Input("開始"), settings);
        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        turn.OfType<AgentTokenUsageEvent>().Should().ContainSingle().Which.Delta.Should().Be(new AgentUsage(100, 20, 0, null));
        session.HasLiveBackgroundWork.Should().BeTrue();
        var instance = session.ProcessInstance;
        await WaitDetachableAsync(session);
        while (events.TryTake(out _, TimeSpan.FromMilliseconds(200))) { }

        var state = await session.DetachProcessAsync();

        state.Should().NotBeNull();
        session.HasLiveProcess.Should().BeFalse("放開之後這個會話沒有程序");
        session.CanDetachProcess.Should().BeFalse();
        var afterDetach = new List<AgentEvent>();
        while (events.TryTake(out var ev, TimeSpan.FromMilliseconds(300)))
            afterDetach.Add(ev);
        afterDetach.OfType<AgentProcessExitedEvent>().Should().BeEmpty("放開不是程序結束");
        afterDetach.OfType<AgentToolCompletedEvent>().Should().BeEmpty("背景指令沒有被中斷：卡片留在執行中");
        afterDetach.OfType<AgentBackgroundWorkEvent>().Should().BeEmpty("背景指令還在主機那邊跑，不回報沒了");
        first.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        session.Dispose();

        // 新的 app：另一個用戶端、另一個會話物件，狀態經過 JSON 文字（同 resume.json）
        using var second = NewHost();
        using var resumed = new CodexAgentSession();
        var events2 = Collect(resumed);
        var attachSettings = Background(second, delayMs: 4000) with { SessionId = "thread-fake", SessionStarted = true };

        (await resumed.AttachProcessAsync(JObject.Parse(state!.ToString()), attachSettings)).Should().BeTrue();

        resumed.ProcessInstance.Should().Be(instance, "同一個程序實例（核准卡的有效範圍認它）");
        resumed.HasLiveProcess.Should().BeTrue();
        resumed.HasLiveBackgroundWork.Should().BeTrue();
        resumed.IsRunning.Should().BeFalse();
        resumed.LiveBackgroundTools.Should().ContainSingle().Which.Title.Should().Be("npm test");

        var later = await TakeUntilAsync(events2, ev => ev is AgentBackgroundCommandFinishedEvent);
        later.OfType<AgentProcessStartedEvent>().Should().ContainSingle().Which.ProcessInstance.Should().Be(instance);
        var reported = later.OfType<AgentBackgroundWorkEvent>().First();
        reported.Count.Should().Be(1);
        reported.ToolIds.Should().Equal("bg1");
        later.IndexOf(reported).Should().BeLessThan(later.FindIndex(e => e is AgentToolCompletedEvent), "中樞先知道哪張卡片是背景指令，才收到它的結束");
        later.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(t => t.ToolId == "bg1" && t.Status == AgentItemStatus.Done && t.Output == "all passed");
        later.OfType<AgentBackgroundCommandFinishedEvent>().Single().Title.Should().Be("npm test");
        resumed.HasLiveBackgroundWork.Should().BeFalse();

        // 之後的送出：同一個程序、同一個 thread（不重開、不 thread/resume），token 從放開前的累計接著算
        await resumed.SendAsync(Input("接著做"), attachSettings);
        var next = await TakeUntilAsync(events2, ev => ev is AgentTurnCompletedEvent);
        next.OfType<AgentProcessStartedEvent>().Should().BeEmpty("沒有換程序");
        next.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("second:thread/start,sandbox=dangerFullAccess,sandbox=dangerFullAccess", "同一個 app-server：只建立過一次 thread、沒有續接");
        next.OfType<AgentTokenUsageEvent>().Should().ContainSingle().Which.Delta.Should().Be(new AgentUsage(50, 10, 0, null), "只記增量，不是把整個對話的累計再記一次");
        resumed.ProcessInstance.Should().Be(instance);
        await resumed.CloseAsync();
    }

    [Fact]
    public async Task 放開之後放棄_同一個會話接回_放開期間跑完的結束補送進來()
    {
        using var host = NewHost();
        using var session = new CodexAgentSession();
        var events = Collect(session);
        var settings = Background(host, delayMs: 1500);
        await session.SendAsync(Input("開始"), settings);
        await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        var instance = session.ProcessInstance;
        await WaitDetachableAsync(session);
        var state = await session.DetachProcessAsync();
        state.Should().NotBeNull();
        while (events.TryTake(out _, TimeSpan.FromMilliseconds(100))) { }
        await Task.Delay(2500);   // 背景指令在沒有人接著的期間跑完：輸出留在主機

        (await session.AttachProcessAsync(state!, settings with { SessionId = "thread-fake", SessionStarted = true })).Should().BeTrue();

        session.ProcessInstance.Should().Be(instance);
        var later = await TakeUntilAsync(events, ev => ev is AgentBackgroundCommandFinishedEvent);
        later.OfType<AgentBackgroundWorkEvent>().First().ToolIds.Should().Equal("bg1");
        later.OfType<AgentToolCompletedEvent>().Single().ToolId.Should().Be("bg1");
        session.HasLiveBackgroundWork.Should().BeFalse();
        await session.CloseAsync();
    }

    [Fact]
    public async Task 直接開的程序放不開_狀態讀不懂的接不回去()
    {
        using var host = NewHost();
        // MYRIAGENT_TEST_VIA_HOST=1 時沒指定主機的程序也經主機開（HostedProcessMode）：這一段只在平常的模式有意義
        if (!HostedProcessMode.Enabled)
        {
            using var direct = new CodexAgentSession();
            var directEvents = Collect(direct);
            await direct.SendAsync(Input("開始"), Background(null, delayMs: 3000));
            await TakeUntilAsync(directEvents, ev => ev is AgentTurnCompletedEvent);
            await Task.Delay(300);
            direct.CanDetachProcess.Should().BeFalse("不是經主機開的");
            (await direct.DetachProcessAsync()).Should().BeNull();
            direct.HasLiveProcess.Should().BeTrue("放不開：程序照舊是這個會話的");
            direct.HasLiveBackgroundWork.Should().BeTrue();
            await direct.CloseAsync();
        }

        using var hosted = new CodexAgentSession();
        var hostedEvents = Collect(hosted);
        await hosted.SendAsync(Input("開始"), Settings("background_stop") with { ProcessHost = host });
        await TakeUntilAsync(hostedEvents, ev => ev is AgentTurnCompletedEvent);
        await WaitDetachableAsync(hosted);
        var state = await hosted.DetachProcessAsync();
        state.Should().NotBeNull();

        using var other = new CodexAgentSession();
        var unknown = (JObject)state!.DeepClone();
        unknown["Version"] = 99;
        var attach = Settings("background_stop") with { ProcessHost = host, SessionId = "thread-fake", SessionStarted = true };
        (await other.AttachProcessAsync(unknown, attach)).Should().BeFalse("更新的格式讀不懂就當成接不回去");
        (await other.AttachProcessAsync(new JObject(), attach)).Should().BeFalse();
        (await other.AttachProcessAsync(state, attach with { ProcessHost = null })).Should().BeFalse("沒有主機");
        other.HasLiveProcess.Should().BeFalse();
        (await other.AttachProcessAsync(state, attach)).Should().BeTrue("原樣的狀態照樣接得回去");
        other.HasLiveBackgroundWork.Should().BeTrue();
        await other.CloseAsync();
    }

    public new void Dispose()
    {
        base.Dispose();
        hostRoot.Dispose();
    }
}
