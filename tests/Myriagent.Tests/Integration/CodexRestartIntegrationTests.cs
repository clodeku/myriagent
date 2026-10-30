using System.Collections.Concurrent;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>
/// IT-H2（設計 11.2）：以這台電腦真的 Codex CLI（系統預設帳戶）在工具執行中以新版重開——同一個執行檔模擬換版：
/// turn/interrupt → 關程序（確認舊指令都已結束）→ 新程序 thread/resume → turn/start 送接續訊息 → 這一輪完成，而且沒有和舊指令重疊。
/// 會用到訂閱額度（約 2 輪）：模型 gpt-6-luna、推理強度 low、提示詞盡量短。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~Integration"
/// </summary>
public sealed class CodexRestartIntegrationTests : IDisposable
{
    private const string Reason = "實際呼叫本機 Codex CLI（會用到訂閱額度）";
    private const string CodexModel = "gpt-6-luna";
    private const string Command = "Start-Sleep -Seconds 20; Add-Content runs.txt $PID";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(4);

    /// <summary>舊指令如果沒被結束，從開始算多久之後一定已經寫進 runs.txt（指令本身 20 秒）</summary>
    private static readonly TimeSpan OldCommandWouldHaveWritten = TimeSpan.FromSeconds(26);

    private readonly ITestOutputHelper output;
    private readonly TempDirectory temp = new("it-codex-restart");

    public CodexRestartIntegrationTests(ITestOutputHelper output) => this.output = output;

    public void Dispose() => temp.Dispose();

    private string Workspace => Path.Combine(temp.Root, "workspace");

    private AgentSessionSettings Settings(string? threadId)
    {
        Directory.CreateDirectory(Workspace);
        var launcher = CliLocator.Locate(AgentProvider.Codex)?.Launcher;
        launcher.Should().NotBeNull("這台電腦要安裝 Codex CLI");
        var account = new AgentAccount { Provider = AgentProvider.Codex, Label = "本機" };
        return new AgentSessionSettings(
            threadId, threadId != null, launcher, "it|Codex", env => AccountRules.ApplyEnvironment(account, env), null,
            CodexModel, "low", AgentPermissionMode.FullAccess, Workspace, temp.Root,
            AgentHubRules.BuildAgentInstructions(null, AgentProvider.Codex, Workspace, Path.Combine(temp.Root, "attachments")),
            UseLocalCustomizations: false);
    }

    private async Task<List<AgentEvent>> TakeUntilAsync(BlockingCollection<AgentEvent> events, Func<AgentEvent, bool> until)
    {
        var collected = new List<AgentEvent>();
        var deadline = DateTime.UtcNow + TurnTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!events.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                continue;
            collected.Add(ev);
            output.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {ev.GetType().Name}: {Describe(ev)}");
            if (until(ev))
                return collected;
        }
        throw new TimeoutException("Codex 沒有在時限內送出預期的事件");
    }

    private static string Describe(AgentEvent ev) => ev switch
    {
        AgentTextCompletedEvent t => t.Text,
        AgentToolStartedEvent t => $"{t.ToolName}（{t.ToolKind}）{t.Title}",
        AgentToolCompletedEvent t => $"{t.Status} {ClaudeStreamRules.FirstLine(t.Output ?? string.Empty)}",
        AgentTurnCompletedEvent c => $"{c.Outcome} {c.ErrorMessage}",
        AgentNoticeEvent n => n.Message,
        AgentSessionStartedEvent s => $"{s.SessionId} {s.Model} {s.Detail}",
        AgentProcessStartedEvent p => p.ProcessInstance,
        AgentProcessExitedEvent p => $"{p.ProcessInstance} 結束碼 {p.ExitCode} Expected={p.Expected}",
        AgentRunningVersionEvent v => $"{v.ProcessInstance} {v.Version}",
        AgentSafePointChangedEvent s => $"工具 {s.ToolRunning}、等使用者 {s.WaitingForUser}",
        _ => string.Empty,
    };

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task IT_H2_Codex_工具執行中重開_interrupt關程序resume後以接續訊息完成_沒有和舊指令重疊()
    {
        using var session = new CodexAgentSession { TailBackupDirectory = Path.Combine(temp.Root, "partial") };
        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);
        await session.SendAsync(new AgentTurnInput($"請用 PowerShell 執行 `{Command}`（只執行一次），完成後只回答「完成」。", Array.Empty<AgentAttachment>()),
            Settings(null));

        var first = await TakeUntilAsync(events, ev => ev is AgentToolStartedEvent tool && $"{tool.Title}\n{tool.Detail}".Contains("Start-Sleep", StringComparison.OrdinalIgnoreCase));
        var commandStarted = DateTime.UtcNow;
        var oldInstance = first.OfType<AgentProcessStartedEvent>().Single().ProcessInstance;
        var thread = first.OfType<AgentSessionStartedEvent>().Single().SessionId;
        var version = first.OfType<AgentRunningVersionEvent>().Single().Version;
        output.WriteLine($"thread {thread}，Codex {version}");

        // 指令跑了一下才重開：先擷取舊程序樹（node → codex.exe → 執行指令的 shell），重開回來後裡面不能有活著的
        await Task.Delay(TimeSpan.FromSeconds(3));
        session.IsAtSafePoint.Should().BeFalse("指令在執行");
        var tree = ProcessTreeSnapshot.Capture(session.CurrentProcessId!.Value);
        output.WriteLine("舊程序樹：" + string.Join("、", tree.Processes.Select(p => $"{p.Id} {Path.GetFileName(ProcessImage.TryGetPath(p.Id))}")));
        tree.Processes.Count.Should().BeGreaterThan(2, "app-server 與它執行指令的子程序都在樹上");

        var continuation = "（系統通知：Clode Myriagent 為了把 Codex CLI 換成新版，自動中斷並重新啟動了這個對話，這不是使用者拒絕。"
            + $"重啟時你正在執行〈{Command}〉，那個指令已被終止、沒有完成（runs.txt 沒有新增內容）。"
            + "使用者在重啟後尚未傳送新訊息；請重新執行那個指令一次，完成後只回答「完成」。）";
        var request = new AgentRestartRequest(AgentRestartReason.UserApplyNow, AgentRestartMode.Immediate, version, version, AutoContinue: true, continuation,
            new[] { new AgentInterruptedTool("執行指令", Command, DateTime.UtcNow - commandStarted) }, Settings(thread));
        var result = await session.RestartAsync(request);

        output.WriteLine($"RestartAsync：{result}");
        result.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        result.PreviousProcessInstance.Should().Be(oldInstance);
        tree.GetAlive().Should().BeEmpty("turn/interrupt 不會停執行中的指令：接續前舊程序樹（含指令）一定都已結束，重跑的指令才不會和它重疊");

        var second = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        var exited = second.OfType<AgentProcessExitedEvent>().Single();
        exited.ProcessInstance.Should().Be(oldInstance);
        exited.Expected.Should().BeTrue();
        var fresh = second.OfType<AgentProcessStartedEvent>().Single();
        fresh.ProcessInstance.Should().Be(result.ProcessInstance);
        second.OfType<AgentRunningVersionEvent>().Single().Version.Should().Be(version, "同一個執行檔");
        second.OfType<AgentSessionStartedEvent>().Single().SessionId.Should().Be(thread, "續接同一個 thread");
        var continuedAt = second.FindIndex(ev => ev is AgentTurnStartedByCliEvent { Kind: AgentAutoTurnKind.Continuation });
        continuedAt.Should().BeGreaterThan(second.FindIndex(ev => ev is AgentProcessStartedEvent));
        second.Take(continuedAt).OfType<AgentTurnCompletedEvent>().Should().BeEmpty("被打斷的那一輪不回報結束");
        second.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);

        // 舊指令如果還活著，會在開始後 20 秒寫進 runs.txt：等過那個時間點再數
        var wait = commandStarted + OldCommandWouldHaveWritten - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            await Task.Delay(wait);
        var runs = Path.Combine(Workspace, "runs.txt");
        var lines = File.Exists(runs) ? File.ReadAllLines(runs).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray() : Array.Empty<string>();
        output.WriteLine($"runs.txt：{string.Join("、", lines)}");
        lines.Should().HaveCount(1, "舊指令在重開時被結束、沒有寫入；接續後只重跑一次（模型偶發沒照接續訊息重跑時重跑測試）");
        session.IsRunning.Should().BeFalse();
        session.ProcessInstance.Should().Be(fresh.ProcessInstance);
    }
}
