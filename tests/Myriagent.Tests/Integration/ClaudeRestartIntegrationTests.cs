using System.Collections.Concurrent;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>
/// CLI 熱更新的 Claude 會話重開，以這台電腦真的 Claude Code CLI（系統預設帳戶）驗證（設計 11.2 的 IT-H1、IT-H4）：
/// <list type="bullet">
/// <item>IT-H1 工具結果邊界：工具結果回來後的安全點殺程序樹、以 RESUME 環境變數 --resume 重開 → CLI 自動接續，result 帶 resume_reason
/// （設了 CLAUDE_CODE_RESUME_REASON，所以是 app 的標記 myriagent_cli_update，取代 interrupted_turn）；指令「if (Test-Path done.txt) … 先檢查再寫」只寫一次</item>
/// <item>IT-H1 產生文字中：第 3 個文字片段時重開，CLI 自動接續、從頭重寫回覆；串流到一半的那段結束為已中斷</item>
/// <item>IT-H1 備案：不帶 RESUME 環境變數（模擬它在某版失效）→ 新程序不會自動接續，改經 stdin 送接續訊息，一樣完成這一輪、不重複寫入</item>
/// <item>IT-H4 上一輪已完成：重開只關程序、不接續（沒有多耗一輪），下一則訊息照常續接、記得前文</item>
/// </list>
/// 會用到訂閱額度：haiku、推理強度 low、提示詞極短（每個情境 1～2 輪）。模型偶爾不照指示時重跑一次。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~Integration"
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class ClaudeRestartIntegrationTests : IDisposable
{
    private const string Reason = "實際呼叫本機 Claude CLI 並殺程序重開（會用到訂閱額度）";
    private const string Model = "haiku";
    private const string GuardedWrite = "if (Test-Path done.txt) { 'exists' } else { Set-Content done.txt done; 'written' }";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(4);

    private readonly ITestOutputHelper output;
    private readonly TempDirectory temp = new("it-restart");

    public ClaudeRestartIntegrationTests(ITestOutputHelper output) => this.output = output;

    public void Dispose() => temp.Dispose();

    private string Workspace => Path.Combine(temp.Root, "workspace");

    private AgentSessionSettings Settings(string sessionId, bool started)
    {
        Directory.CreateDirectory(Workspace);
        var launcher = CliLocator.Locate(AgentProvider.Claude)?.Launcher;
        launcher.Should().NotBeNull("這台電腦要安裝 Claude Code CLI");
        var account = new AgentAccount { Provider = AgentProvider.Claude, Label = "本機" };
        return new AgentSessionSettings(
            sessionId, started, launcher, "it|Claude", env => AccountRules.ApplyEnvironment(account, env), null,
            Model, "low", AgentPermissionMode.Ask, Workspace, temp.Root,
            AgentHubRules.BuildAgentInstructions(null, AgentProvider.Claude, Workspace, Path.Combine(temp.Root, "attachments")),
            UseLocalCustomizations: false);
    }

    private static AgentRestartRequest Request(AgentRestartMode mode, AgentSessionSettings settings)
        => new(AgentRestartReason.CliUpdated, mode, "2.1.296", "2.1.296", AutoContinue: true, ContinuationText: null,
            InterruptedTools: Array.Empty<AgentInterruptedTool>(), Settings: settings);

    /// <summary>
    /// 送出一則訊息並處理事件到這一輪結束：核准一律允許；trigger 對某個事件回傳要做的重開（只做一次），重開與事件處理同時進行
    /// </summary>
    private async Task<(List<AgentEvent> Events, AgentRestartResult? Restart)> RunAsync(ClaudeAgentSession session, string prompt,
        AgentSessionSettings settings, Func<AgentEvent, List<AgentEvent>, AgentRestartRequest?>? trigger = null)
    {
        var queue = new BlockingCollection<AgentEvent>();
        void Handler(AgentEvent ev) => queue.Add(ev);
        session.EventReceived += Handler;
        var events = new List<AgentEvent>();
        Task<AgentRestartResult>? restart = null;
        try
        {
            await session.SendAsync(new AgentTurnInput(prompt, Array.Empty<AgentAttachment>()), settings);
            var deadline = DateTime.UtcNow + TurnTimeout;
            while (DateTime.UtcNow < deadline)
            {
                // 重開後沒有接續（Restarted／Failed）：被打斷的那一輪由中樞收尾，不會再有 TurnCompleted
                if (restart is { IsCompleted: true } && (await restart).Outcome != AgentRestartOutcome.RestartedAndContinued)
                    return (events, await restart);
                if (!queue.TryTake(out var ev, TimeSpan.FromMilliseconds(200)))
                    continue;
                events.Add(ev);
                output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {ev.GetType().Name}: {Describe(ev)}");
                if (ev is AgentApprovalEvent { IsPlan: false } approval)
                    _ = session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
                if (restart == null && trigger?.Invoke(ev, events) is { } request)
                {
                    output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} ── 以新版重開（{request.Mode}）");
                    restart = session.RestartAsync(request);
                }
                if (ev is AgentTurnCompletedEvent)
                    return (events, restart == null ? null : await restart);
            }
            throw new TimeoutException("這一輪沒有在時限內結束");
        }
        finally
        {
            session.EventReceived -= Handler;
        }
    }

    private static string Describe(AgentEvent ev) => ev switch
    {
        AgentTextCompletedEvent t => t.Text,
        AgentToolStartedEvent t => $"{t.ToolName}（{t.ToolKind}）{t.Title}",
        AgentToolCompletedEvent t => $"{t.Status} {t.Output}",
        AgentApprovalEvent a => $"{a.ToolName}（{a.ToolKind}）{a.Title}",
        AgentTurnCompletedEvent c => $"{c.Outcome} {c.ErrorMessage} 輸入 {c.Usage?.InputTokens} 輸出 {c.Usage?.OutputTokens}",
        AgentNoticeEvent n => n.Message,
        AgentProcessStartedEvent p => p.ProcessInstance,
        AgentRunningVersionEvent v => $"{v.ProcessInstance} {v.Version}",
        AgentSafePointChangedEvent s => $"tool={s.ToolRunning} user={s.WaitingForUser} background={s.HasLiveBackgroundWork}",
        AgentTurnStartedByCliEvent t => t.Kind.ToString(),
        _ => string.Empty,
    };

    private static string AllText(IEnumerable<AgentEvent> events) => string.Join("\n", events.OfType<AgentTextCompletedEvent>().Select(t => t.Text));

    /// <summary>工具結果回來後的第一個安全點（中樞在這時候重試 RestartAsync）</summary>
    private static Func<AgentEvent, List<AgentEvent>, AgentRestartRequest?> AtToolResultBoundary(AgentRestartRequest request)
        => (ev, events) => ev is AgentSafePointChangedEvent { IsAtSafePoint: true } && events.OfType<AgentToolCompletedEvent>().Any() ? request : null;

    private string ReadTranscript(string sessionId)
    {
        var configDirectory = ClaudeStreamRules.ResolveConfigDirectory(env => AccountRules.ApplyEnvironment(new AgentAccount { Provider = AgentProvider.Claude }, env));
        var path = ClaudeStreamRules.FindTranscript(configDirectory, sessionId, Workspace);
        path.Should().NotBeNull("CLI 寫了這個 session 的逐字稿");
        using var stream = new FileStream(path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void AssertWrittenOnce(List<AgentEvent> events)
    {
        File.ReadAllText(Path.Combine(Workspace, "done.txt")).Trim().Should().Be("done");
        events.OfType<AgentToolCompletedEvent>().Count(t => t.Output?.Contains("written") == true)
            .Should().Be(1, "先檢查再寫：重開後即使重跑也只會回報 exists，不會再寫一次");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task IT_H1_工具結果邊界殺樹重開_CLI自動接續_ResumeReason是App的標記_不重複寫入()
    {
        var sessionId = Guid.NewGuid().ToString();
        using var session = new ClaudeAgentSession();
        var settings = Settings(sessionId, started: false);

        var (events, restart) = await RunAsync(session, $"請用 PowerShell 工具執行一次 `{GuardedWrite}`，執行完用一句話回報輸出。", settings,
            AtToolResultBoundary(Request(AgentRestartMode.SafePoint, settings with { SessionStarted = true })));

        restart.Should().NotBeNull("要在工具結果之後重開");
        output.WriteLine($"重開結果：{restart}");
        restart!.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        restart.PreviousProcessInstance.Should().NotBeNull();
        restart.ProcessInstance.Should().NotBe(restart.PreviousProcessInstance);
        events.OfType<AgentTurnStartedByCliEvent>().Select(e => e.Kind).Should().Equal(AgentAutoTurnKind.ResumeInterruptedTurn);
        events.OfType<AgentTurnCompletedEvent>().Should().ContainSingle("被打斷的那一輪不發 TurnCompleted，接續的一輪照常結束")
            .Which.Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.LastResumeReason.Should().Be(ClaudeStreamRules.AppResumeReason, "CLI 自動接續了被打斷的一輪（resume_reason＝CLAUDE_CODE_RESUME_REASON）");
        AssertWrittenOnce(events);
        var transcript = ReadTranscript(sessionId);
        transcript.Should().EndWith("\n", "逐字稿沒有半行");
        transcript.Should().Contain(AgentRestartRequest.DefaultContinuationText[..20], "接續訊息以 CLAUDE_CODE_RESUME_PROMPT 補進逐字稿（isMeta）");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task IT_H1_產生文字中重開_CLI自動接續並從頭重寫_串流到一半的回覆結束為已中斷()
    {
        var sessionId = Guid.NewGuid().ToString();
        using var session = new ClaudeAgentSession();
        var settings = Settings(sessionId, started: false);

        var (events, restart) = await RunAsync(session, "不要使用任何工具。請用繁體中文寫約 150 字，說明作業系統的「程序樹」是什麼。", settings,
            (ev, all) => ev is AgentTextDeltaEvent && all.OfType<AgentTextDeltaEvent>().Count() == 3
                ? Request(AgentRestartMode.SafePoint, settings with { SessionStarted = true })
                : null);

        restart.Should().NotBeNull();
        output.WriteLine($"重開結果：{restart}");
        restart!.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        events.OfType<AgentTextCompletedEvent>().Should().Contain(t => t.Text.EndsWith(ClaudeStreamRules.InterruptedTextNote),
            "重開前串流到一半的回覆結束為已中斷");
        events.OfType<AgentTurnCompletedEvent>().Should().ContainSingle().Which.Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.LastResumeReason.Should().Be(ClaudeStreamRules.AppResumeReason);
        AllText(events.SkipWhile(e => e is not AgentTurnStartedByCliEvent)).Should().Contain("程序", "接續的一輪重寫了回覆");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task IT_H1_備案_不帶RESUME環境變數時改經stdin送接續訊息_一樣完成而且不重複寫入()
    {
        var sessionId = Guid.NewGuid().ToString();
        using var session = new ClaudeAgentSession { UseResumeEnvironment = false };
        var settings = Settings(sessionId, started: false);

        var (events, restart) = await RunAsync(session, $"請用 PowerShell 工具執行一次 `{GuardedWrite}`，執行完用一句話回報輸出。", settings,
            AtToolResultBoundary(Request(AgentRestartMode.SafePoint, settings with { SessionStarted = true })));

        restart.Should().NotBeNull();
        output.WriteLine($"重開結果：{restart}");
        restart!.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        events.OfType<AgentTurnStartedByCliEvent>().Select(e => e.Kind).Should().Equal(AgentAutoTurnKind.ResumeInterruptedTurn, AgentAutoTurnKind.Continuation);
        events.OfType<AgentTurnCompletedEvent>().Should().ContainSingle().Which.Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.LastResumeReason.Should().BeNull("沒有帶 RESUME 變數：CLI 沒有自動接續，這一輪是宿主送的接續訊息");
        AssertWrittenOnce(events);
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task IT_H4_上一輪已完成_重開只關程序不接續_下一則訊息照常續接()
    {
        var sessionId = Guid.NewGuid().ToString();
        using var session = new ClaudeAgentSession();
        var settings = Settings(sessionId, started: false);
        var (first, _) = await RunAsync(session, "記住代號：藍鯨42。只回答「好」。", settings);
        first.OfType<AgentTurnCompletedEvent>().Last().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.HasLiveProcess.Should().BeTrue();

        var events = new BlockingCollection<AgentEvent>();
        void Handler(AgentEvent ev) => events.Add(ev);
        session.EventReceived += Handler;
        var restart = await session.RestartAsync(Request(AgentRestartMode.SafePoint, settings with { SessionStarted = true }));
        await Task.Delay(3000);
        session.EventReceived -= Handler;

        output.WriteLine($"重開結果：{restart}");
        restart.Outcome.Should().Be(AgentRestartOutcome.Restarted, "上一輪已完成：只關程序、不送接續（不多耗一輪）");
        restart.ProcessInstance.Should().BeNull("沒有啟動新程序");
        session.HasLiveProcess.Should().BeFalse();
        events.Should().NotContain(e => e is AgentTurnStartedByCliEvent || e is AgentTurnCompletedEvent || e is AgentProcessStartedEvent);

        var (next, _) = await RunAsync(session, "剛才請你記住的代號是什麼？只回答代號。", settings with { SessionStarted = true });
        next.OfType<AgentNoticeEvent>().Should().NotContain(n => n.Message.Contains("找不到原本的 Claude 對話紀錄"));
        next.OfType<AgentTurnCompletedEvent>().Should().ContainSingle().Which.Outcome.Should().Be(AgentTurnOutcome.Completed);
        next.OfType<AgentTurnStartedByCliEvent>().Should().BeEmpty();
        AllText(next).Should().Contain("藍鯨42");
        ReadTranscript(sessionId).Should().EndWith("\n");
    }
}
