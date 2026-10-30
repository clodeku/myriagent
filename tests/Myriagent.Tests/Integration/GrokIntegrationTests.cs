using System.Collections.Concurrent;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>
/// 以這台電腦真的 Grok Build CLI（grok，ACP）跑：登入狀態、額度與模型清單、串流文字與工具卡、跨程序以 session/resume 續接仍記得前文、
/// 核准卡的允許與拒絕、續接的對話不存在時改開新對話、執行中停止、工具執行中以新版重開（同一個執行檔模擬換版）。
/// 帳戶用已登入的隔離 GROK_HOME：MYRIAGENT_IT_GROK_HOME，沒有設就用探針的 %LOCALAPPDATA%\myriagent-probes\grok\home-a（tools/probes/grok/login_probe.py 登入）。
/// 會用到訂閱額度（約 10 輪）：模型 grok-4.6＋思考強度 low、提示詞盡量短。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~GrokIntegration"
/// </summary>
public sealed class GrokIntegrationTests : IDisposable
{
    private const string Reason = "實際呼叫本機 Grok Build CLI（要先登入，會用到訂閱額度）";
    private const string Model = "grok-4.6";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(4);

    private readonly ITestOutputHelper output;
    private readonly TempDirectory temp = new("it-grok");
    private readonly AgentAccount account = new()
    {
        Id = "it-grok",
        Provider = AgentProvider.Grok,
        Label = "Grok（整合測試）",
        ConfigDirectory = Environment.GetEnvironmentVariable("MYRIAGENT_IT_GROK_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "myriagent-probes", "grok", "home-a"),
    };

    public GrokIntegrationTests(ITestOutputHelper output) => this.output = output;

    public void Dispose() => temp.Dispose();

    private string Workspace => Path.Combine(temp.Root, "workspace");

    private static AgentLauncher Launcher()
    {
        var launcher = CliLocator.Locate(AgentProvider.Grok)?.Launcher;
        launcher.Should().NotBeNull("這台電腦要安裝 Grok Build CLI（irm https://x.ai/cli/install.ps1 | iex）");
        return launcher!;
    }

    private AgentSessionSettings Settings(string? sessionId, bool started, AgentPermissionMode mode = AgentPermissionMode.AcceptEdits)
    {
        Directory.CreateDirectory(Workspace);
        GrokAcpRules.EnsureIsolatedHome(account.ConfigDirectory!);
        return new AgentSessionSettings(
            sessionId ?? Guid.NewGuid().ToString(), started, Launcher(), "it|grok", env => AccountRules.ApplyEnvironment(account, env), null,
            Model, "low", mode, Workspace, temp.Root,
            AgentHubRules.BuildAgentInstructions(null, AgentProvider.Grok, Workspace, Path.Combine(temp.Root, "attachments")),
            UseLocalCustomizations: true,
            HistoryRecap: "（前文節錄：使用者先前請你記住暗號「藍鯨 42」）");
    }

    private async Task<List<AgentEvent>> TakeUntilAsync(BlockingCollection<AgentEvent> events, Func<AgentEvent, bool> until, Func<AgentEvent, Task>? onEvent = null)
    {
        var collected = new List<AgentEvent>();
        var deadline = DateTime.UtcNow + TurnTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!events.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                continue;
            collected.Add(ev);
            output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {ev.GetType().Name}: {Describe(ev)}");
            if (onEvent != null)
                await onEvent(ev);
            if (until(ev))
                return collected;
        }
        throw new TimeoutException("grok 沒有在時限內送出預期的事件");
    }

    private async Task<List<AgentEvent>> RunTurnAsync(IAgentSession session, string text, AgentSessionSettings settings, Func<AgentEvent, Task>? onEvent = null)
    {
        var events = new BlockingCollection<AgentEvent>();
        void Handler(AgentEvent ev) => events.Add(ev);
        session.EventReceived += Handler;
        try
        {
            await session.SendAsync(new AgentTurnInput(text, Array.Empty<AgentAttachment>()), settings);
            return await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent, onEvent);
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
        AgentToolOutputEvent t => t.Delta,
        AgentToolCompletedEvent t => $"{t.Status} {ClaudeStreamRules.FirstLine(t.Output ?? string.Empty)}",
        AgentTurnCompletedEvent c => $"{c.Outcome} {c.ErrorMessage} 輸入 {c.Usage?.InputTokens} 輸出 {c.Usage?.OutputTokens} 美元 {c.Usage?.CostUsd}",
        AgentNoticeEvent n => n.Message,
        AgentSessionStartedEvent s => $"{s.SessionId} {s.Model} {s.Detail}",
        AgentApprovalEvent a => $"{a.ToolName}（{a.ToolKind}）{a.Title} 本對話：{a.AllowSessionLabel}",
        AgentProcessStartedEvent p => p.ProcessInstance,
        AgentProcessExitedEvent p => $"{p.ProcessInstance} 結束碼 {p.ExitCode} Expected={p.Expected}",
        AgentRunningVersionEvent v => $"{v.ProcessInstance} {v.Version}",
        _ => string.Empty,
    };

    private static string AllText(IEnumerable<AgentEvent> events)
        => string.Join("\n", events.OfType<AgentTextCompletedEvent>().Select(t => t.Text));

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task 登入狀態_額度與模型清單_只用只讀的查詢()
    {
        GrokAcpRules.EnsureIsolatedHome(account.ConfigDirectory!);
        await using var client = await GrokAcpClient.StartAsync(Launcher(), env => AccountRules.ApplyEnvironment(account, env), CancellationToken.None);

        var (info, infoError) = await client.RequestAsync(GrokAcpRules.MethodAuthInfo, new Newtonsoft.Json.Linq.JObject(), TimeSpan.FromSeconds(30));
        infoError.Should().BeNull();
        GrokAcpRules.EvaluateAuthInfo(info).Status.Should().Be(AccountStatus.Ready, $"要先在 {account.ConfigDirectory} 登入（tools/probes/grok/login_probe.py）");

        var (billing, billingError) = await client.RequestAsync(GrokAcpRules.MethodBilling, new Newtonsoft.Json.Linq.JObject(), TimeSpan.FromSeconds(30));
        billingError.Should().BeNull();
        output.WriteLine($"方案 {GrokAcpRules.ParsePlan(billing)}");
        var usage = UsageRules.ParseGrokBilling(billing as Newtonsoft.Json.Linq.JObject);
        usage.Should().NotBeNull("訂閱帳戶有額度窗");
        usage!.Value.Windows.Should().ContainSingle().Which.Key.Should().Be(UsageRules.GrokCreditsKey);
        output.WriteLine(string.Join("、", usage.Value.Windows.Select(w => $"{w.Label} {w.Percent}% 重置 {w.ResetsAt}")));

        var models = GrokAcpRules.ParseInitializeModels(client.InitializeResult);
        output.WriteLine(string.Join("、", models.Select(m => $"{m.Value}[{string.Join("/", m.Efforts)}]")));
        models.Should().Contain(m => m.Value == Model && m.Efforts.Contains("low"), "整合測試用的模型");
        models[0].Value.Should().Be("default");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task 串流文字_工具卡_跨程序以resume續接仍記得前文()
    {
        string sessionId;
        using (var session = new GrokAgentSession())
        {
            var first = await RunTurnAsync(session, "請用工具在工作資料夾建立檔案 note.txt，內容是「ok」；另外記住暗號「藍鯨 42」（不要寫進檔案）。最後只回答「好」。",
                Settings(null, started: false));
            first.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
            first.OfType<AgentTextDeltaEvent>().Should().NotBeEmpty("文字要串流");
            first.OfType<AgentApprovalEvent>().Should().BeEmpty("自動接受編輯：工作資料夾內的編輯由會話代為核准");
            first.OfType<AgentToolStartedEvent>().Should().Contain(t => (t.ToolKind == "write" || t.ToolKind == "edit") && t.FilePath != null && t.FilePath.EndsWith("note.txt"));
            first.OfType<AgentToolStartedEvent>().Select(t => t.ToolId).Should().BeSubsetOf(first.OfType<AgentToolCompletedEvent>().Select(t => t.ToolId), "每個工具都要結束");
            File.Exists(Path.Combine(Workspace, "note.txt")).Should().BeTrue();
            var started = first.OfType<AgentSessionStartedEvent>().Single();
            sessionId = started.SessionId;
            started.Detail.Should().Be("new");
            first.OfType<AgentTurnCompletedEvent>().Single().Usage!.InputTokens.Should().BeGreaterThan(0);
            // 上下文用量提示：模型清單的視窗（totalContextTokens）與每次呼叫的大小（response_completed、prompt 回應的 _meta 頂層）
            var context = first.OfType<AgentContextUsageEvent>().Aggregate((ContextUsage?)null, ContextUsageRules.Apply);
            output.WriteLine($"上下文：{context}");
            context.Should().NotBeNull();
            context!.WindowTokens.Should().BeGreaterThan(100_000);
            context.UsedTokens.Should().BeInRange(1_000, context.WindowTokens!.Value);
            await session.CloseAsync();
        }

        using (var session = new GrokAgentSession())
        {
            var second = await RunTurnAsync(session, "剛才的暗號是什麼？不要使用工具，只回答暗號。", Settings(sessionId, started: true));
            second.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
            second.OfType<AgentNoticeEvent>().Should().BeEmpty("續接要成功，不能改開新對話");
            second.OfType<AgentSessionStartedEvent>().Single().Should().Match<AgentSessionStartedEvent>(s => s.SessionId == sessionId && s.Detail == "resumed");
            AllText(second).Should().Contain("42");
        }
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task 核准卡_允許後指令照常執行_拒絕後這一輪結束_工具卡標已拒絕()
    {
        using var session = new GrokAgentSession();

        var allowed = await RunTurnAsync(session, "請用終端機執行 PowerShell 指令 `Write-Output hi-myriagent`，然後只回覆它的輸出。", Settings(null, started: false, AgentPermissionMode.Ask),
            async ev =>
            {
                if (ev is AgentApprovalEvent approval)
                    await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
            });
        var card = allowed.OfType<AgentApprovalEvent>().Should().ContainSingle().Which;
        card.ToolKind.Should().Be("bash");
        card.AllowSessionLabel.Should().Be("本對話的指令都不再詢問");
        allowed.OfType<AgentToolCompletedEvent>().Should().Contain(t => t.Status == AgentItemStatus.Done && (t.Output ?? string.Empty).Contains("hi-myriagent"));
        allowed.OfType<AgentToolOutputEvent>().Select(o => o.Delta).Should().NotContain(d => d.Contains("Print") || d.Contains("Run"), "工具說明不當輸出");
        allowed.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);

        var denied = await RunTurnAsync(session, "請在工作資料夾建立 deny.txt，內容 nope。", Settings(null, started: false, AgentPermissionMode.Ask),
            async ev =>
            {
                if (ev is AgentApprovalEvent approval)
                    await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Deny, null);
            });
        denied.OfType<AgentApprovalEvent>().Should().ContainSingle().Which.AllowSessionLabel.Should().Be("本對話的編輯都允許");
        denied.OfType<AgentToolCompletedEvent>().Should().ContainSingle().Which.Status.Should().Be(AgentItemStatus.Declined);
        denied.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted, "grok 的拒絕會結束這一輪（中樞依這一輪拒絕過當成正常結束）");
        File.Exists(Path.Combine(Workspace, "deny.txt")).Should().BeFalse();
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task 續接的對話不存在_改開新對話並附上前文節錄()
    {
        using var session = new GrokAgentSession();
        var missing = Guid.NewGuid().ToString();

        var turn = await RunTurnAsync(session, "前文節錄裡的暗號是什麼？不要使用工具，只回答暗號。", Settings(missing, started: true));

        turn.OfType<AgentNoticeEvent>().Should().ContainSingle("續接不到要告訴使用者");
        turn.OfType<AgentSessionStartedEvent>().Single().Should().Match<AgentSessionStartedEvent>(s => s.SessionId != missing && s.Detail == "new");
        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        AllText(turn).Should().Contain("42", "附上了前文節錄");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task 執行指令中按停止_這一輪結束為已停止_程序留著下一輪照常()
    {
        using var session = new GrokAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += events.Add;

        await session.SendAsync(new AgentTurnInput("請用終端機執行 PowerShell 指令 `Start-Sleep -Seconds 30; Write-Output slept`，等它結束後回報輸出。", Array.Empty<AgentAttachment>()),
            Settings(null, started: false, AgentPermissionMode.FullAccess));
        await TakeUntilAsync(events, ev => ev is AgentToolStartedEvent tool && tool.Title.Contains("Start-Sleep", StringComparison.OrdinalIgnoreCase));
        await Task.Delay(TimeSpan.FromSeconds(2));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await session.InterruptAsync();
        var rest = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);

        rest.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "不能等指令跑完");
        session.HasLiveProcess.Should().BeTrue("停止只取消這一輪");

        var next = await RunTurnAsync(session, "只回覆一個字：好", Settings(null, started: false, AgentPermissionMode.FullAccess));
        next.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task 工具執行中重開_cancel關程序resume後以接續訊息完成_沒有和舊指令重疊()
    {
        const string Command = "Start-Sleep -Seconds 20; Add-Content runs.txt $PID";
        using var session = new GrokAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += events.Add;
        await session.SendAsync(new AgentTurnInput($"請用 PowerShell 執行 `{Command}`（只執行一次），完成後只回答「完成」。", Array.Empty<AgentAttachment>()),
            Settings(null, started: false, AgentPermissionMode.FullAccess));

        var first = await TakeUntilAsync(events, ev => ev is AgentToolStartedEvent tool && $"{tool.Title}\n{tool.Detail}".Contains("Start-Sleep", StringComparison.OrdinalIgnoreCase));
        var commandStarted = DateTime.UtcNow;
        var oldInstance = first.OfType<AgentProcessStartedEvent>().Single().ProcessInstance;
        var sessionId = first.OfType<AgentSessionStartedEvent>().Single().SessionId;
        var version = first.OfType<AgentRunningVersionEvent>().Single().Version;
        await Task.Delay(TimeSpan.FromSeconds(3));
        session.IsAtSafePoint.Should().BeFalse("指令在執行");
        var tree = ProcessTreeSnapshot.Capture(session.CurrentProcessId!.Value);
        output.WriteLine("舊程序樹：" + string.Join("、", tree.Processes.Select(p => $"{p.Id} {Path.GetFileName(ProcessImage.TryGetPath(p.Id))}")));

        var continuation = "（系統通知：Clode Myriagent 為了把 Grok Build CLI 換成新版，自動中斷並重新啟動了這個對話，這不是使用者拒絕。"
            + $"重啟時你正在執行〈{Command}〉，那個指令已被終止、沒有完成（runs.txt 沒有新增內容）。"
            + "使用者在重啟後尚未傳送新訊息；請重新執行那個指令一次，完成後只回答「完成」。）";
        var request = new AgentRestartRequest(AgentRestartReason.UserApplyNow, AgentRestartMode.Immediate, version, version, AutoContinue: true, continuation,
            new[] { new AgentInterruptedTool("執行指令", Command, DateTime.UtcNow - commandStarted) }, Settings(sessionId, started: true, AgentPermissionMode.FullAccess));
        var result = await session.RestartAsync(request);

        output.WriteLine($"RestartAsync：{result}");
        result.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        result.PreviousProcessInstance.Should().Be(oldInstance);
        tree.GetAlive().Should().BeEmpty("接續前舊程序樹（含指令）一定都已結束，重跑的指令才不會和它重疊");

        var second = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        second.OfType<AgentProcessExitedEvent>().Single().Should().Match<AgentProcessExitedEvent>(e => e.ProcessInstance == oldInstance && e.Expected);
        second.OfType<AgentSessionStartedEvent>().Single().Should().Match<AgentSessionStartedEvent>(s => s.SessionId == sessionId && s.Detail == "resumed");
        var continuedAt = second.FindIndex(ev => ev is AgentTurnStartedByCliEvent { Kind: AgentAutoTurnKind.Continuation });
        continuedAt.Should().BeGreaterThan(second.FindIndex(ev => ev is AgentProcessStartedEvent));
        second.Take(continuedAt).OfType<AgentTurnCompletedEvent>().Should().BeEmpty("被打斷的那一輪不回報結束");
        second.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);

        var wait = commandStarted + TimeSpan.FromSeconds(26) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait);
        var runs = Path.Combine(Workspace, "runs.txt");
        var lines = File.Exists(runs) ? File.ReadAllLines(runs).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray() : Array.Empty<string>();
        output.WriteLine($"runs.txt：{string.Join("、", lines)}");
        lines.Should().HaveCount(1, "舊指令在重開時被結束、沒有寫入；接續後只重跑一次（模型偶發沒照接續訊息重跑時重跑測試）");
        session.IsRunning.Should().BeFalse();
    }
}
