using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// Claude 會話放開與接回程序（設計 docs/plans/2026-11-26-agent-host.md 3.3）：假的 claude 經真的代理主機開；
/// 這一輪結束、背景工作還在跑時放開，另一個會話物件（等於新版的 app）以記下的狀態接回同一個程序——
/// 背景工作沒有被動到，完成的通知與 CLI 自己開的那一輪照常收到，之後的送出也用同一個程序
/// </summary>
public sealed class ClaudeSessionDetachTests : ClaudeSessionFakeCliTestsBase
{
    private readonly TempDirectory hostRoot = new("claude-detach-host");

    private AgentHostClient NewHost() => new(Path.Combine(AppContext.BaseDirectory, AgentHostClient.ExecutableName), hostRoot.Root, hostRoot.Combine("host.log"));

    private AgentSessionSettings Background(AgentHostClient host, int delayMs, bool started = false)
        => Settings("bgtask", started, env: new Dictionary<string, string> { ["FAKE_BG_DELAY_MS"] = delayMs.ToString() }) with { ProcessHost = host };

    [Fact]
    public async Task 等背景工作時放開_另一個會話接回同一個程序_背景工作完成照常回報_之後的送出不重開()
    {
        var first = NewHost();
        var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await Turn(session, log, Background(first, delayMs: 4000));
        session.IsRunning.Should().BeFalse();
        session.HasLiveBackgroundWork.Should().BeTrue();
        session.CanDetachProcess.Should().BeTrue();
        var instance = session.ProcessInstance;
        log.Drain(200);

        var state = await session.DetachProcessAsync();

        state.Should().NotBeNull();
        session.HasLiveProcess.Should().BeFalse("放開之後這個會話沒有程序");
        session.CanDetachProcess.Should().BeFalse();
        var afterDetach = log.Drain(300);
        afterDetach.OfType<AgentProcessExitedEvent>().Should().BeEmpty("放開不是程序結束");
        afterDetach.OfType<AgentBackgroundWorkEvent>().Should().BeEmpty("背景工作還在主機那邊跑，不回報沒了");
        first.Handoff(TimeSpan.FromSeconds(60)).Should().BeTrue();
        session.Dispose();

        // 新的 app：另一個用戶端、另一個會話物件，狀態經過 JSON 文字（同 resume.json）
        using var second = NewHost();
        using var resumed = new ClaudeAgentSession();
        using var log2 = new EventLog(resumed);
        var restored = JObject.Parse(state!.ToString());

        (await resumed.AttachProcessAsync(restored, Background(second, delayMs: 4000, started: true))).Should().BeTrue();

        resumed.ProcessInstance.Should().Be(instance, "同一個程序實例（核准卡的有效範圍認它）");
        resumed.HasLiveProcess.Should().BeTrue();
        resumed.HasLiveBackgroundWork.Should().BeTrue();
        resumed.IsRunning.Should().BeFalse();
        var attachEvents = log2.Drain(300);
        attachEvents.OfType<AgentProcessStartedEvent>().Should().ContainSingle().Which.ProcessInstance.Should().Be(instance);
        var background = attachEvents.OfType<AgentBackgroundWorkEvent>().Should().ContainSingle().Subject;
        background.Count.Should().Be(1);
        background.Tools.Should().ContainSingle().Which.Title.Should().Be("npm test", "背景工作是哪個工具開始的也帶過來了");
        resumed.LiveBackgroundTools.Should().ContainSingle().Which.StopId.Should().Be("b9", "接回之後照樣可以單獨停止它");

        // 背景工作在主機那邊照常跑完：通知、CLI 自己開的那一輪都收得到
        var own = await log2.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 20);
        own.OfType<AgentBackgroundWorkEvent>().Should().ContainSingle().Which.Count.Should().Be(0);
        own.OfType<AgentIdleTurnStartedEvent>().Should().ContainSingle();
        Text(own).Should().Be("tests passed");
        resumed.HasLiveBackgroundWork.Should().BeFalse();

        // 之後的送出用同一個程序（設定沒變：不重開）
        var next = await Turn(resumed, log2, Background(second, delayMs: 200, started: true));
        next.OfType<AgentProcessStartedEvent>().Should().BeEmpty("沒有換程序");
        resumed.ProcessInstance.Should().Be(instance);
        await resumed.CloseAsync();
    }

    [Fact]
    public async Task 放開之後放棄_同一個會話接回_照常收到背景工作完成()
    {
        using var host = NewHost();
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        var settings = Background(host, delayMs: 3000);
        await Turn(session, log, settings);
        var instance = session.ProcessInstance;
        var state = await session.DetachProcessAsync();
        state.Should().NotBeNull();
        log.Drain(200);

        // 放棄這次更新：同一個 app、同一條主機連線，把程序接回來
        (await session.AttachProcessAsync(state!, settings with { SessionStarted = true })).Should().BeTrue();

        session.ProcessInstance.Should().Be(instance);
        var own = await log.UntilAsync(e => e is AgentTurnCompletedEvent, seconds: 20);
        Text(own).Should().Be("tests passed");
        await session.CloseAsync();
    }

    [Fact]
    public async Task 直接開的程序放不開()
    {
        using var host = NewHost();
        // MYRIAGENT_TEST_VIA_HOST=1 時沒指定主機的程序也經主機開（HostedProcessMode）：這一段只在平常的模式有意義
        if (!HostedProcessMode.Enabled)
        {
            using var direct = new ClaudeAgentSession();
            using var directLog = new EventLog(direct);
            await Turn(direct, directLog, Settings("bgtask", env: new Dictionary<string, string> { ["FAKE_BG_DELAY_MS"] = "3000" }));
            direct.CanDetachProcess.Should().BeFalse("不是經主機開的");
            (await direct.DetachProcessAsync()).Should().BeNull();
            direct.HasLiveProcess.Should().BeTrue("放不開：程序照舊是這個會話的");
            await direct.CloseAsync();
        }
    }

    [Fact]
    public async Task 主機不在了_狀態讀不懂_都接不回去_會話照舊沒有程序()
    {
        var first = NewHost();
        var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await Turn(session, log, Background(first, delayMs: 30000));
        var state = await session.DetachProcessAsync();
        state.Should().NotBeNull();
        session.Dispose();
        first.Dispose();   // 沒有交棒：主機結束、程序跟著結束

        using var second = NewHost();
        using var resumed = new ClaudeAgentSession();
        (await resumed.AttachProcessAsync(state!, Background(second, delayMs: 100, started: true))).Should().BeFalse("主機不在了");
        resumed.HasLiveProcess.Should().BeFalse();

        var unknown = (JObject)state!.DeepClone();
        unknown["Version"] = 99;
        (await resumed.AttachProcessAsync(unknown, Background(second, delayMs: 100, started: true))).Should().BeFalse("更新的格式讀不懂就當成接不回去");
        (await resumed.AttachProcessAsync(new JObject(), Background(second, delayMs: 100, started: true))).Should().BeFalse();
    }

    public new void Dispose()
    {
        base.Dispose();
        hostRoot.Dispose();
    }
}
