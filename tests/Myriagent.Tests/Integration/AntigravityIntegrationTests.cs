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
/// 以這台電腦真的 Antigravity CLI（agy，本機的 Google 登入）跑：登入狀態與模型清單、串流文字、工具卡、跨程序以 --conversation 續接、
/// 續接的對話不存在時改開新對話、執行中停止。要先登入 agy（不帶參數執行 agy 一次）。
/// 會用到額度：模型用最便宜的 gemini-3.8-flash＋推理強度 low（也驗證「基本 id＋--effort」的啟動參數）、提示詞盡量短。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~AntigravityIntegration"
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AntigravityIntegrationTests : IDisposable
{
    private const string Reason = "實際呼叫本機 Antigravity CLI（要先登入 agy，會用到額度）";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(4);

    /// <summary>agy models 合併後的基本 id（實測有 low／medium／high）</summary>
    private const string Model = "gemini-3.8-flash";

    private readonly ITestOutputHelper output;
    private readonly TempDirectory temp = new("it-agy");
    private readonly AgentAccount account = new() { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity（本機）" };

    public AntigravityIntegrationTests(ITestOutputHelper output) => this.output = output;

    public void Dispose() => temp.Dispose();

    private string Workspace => Path.Combine(temp.Root, "workspace");

    private static AgentLauncher Launcher()
    {
        var launcher = CliLocator.Locate(AgentProvider.Antigravity)?.Launcher;
        launcher.Should().NotBeNull("這台電腦要安裝 Antigravity CLI");
        return launcher!;
    }

    private AgentSessionSettings Settings(string? conversationId, bool started, AgentPermissionMode mode = AgentPermissionMode.AcceptEdits)
    {
        Directory.CreateDirectory(Workspace);
        return new AgentSessionSettings(
            conversationId, started, Launcher(), "it|agy", env => AccountRules.ApplyEnvironment(account, env), null,
            Model, "low", mode, Workspace, temp.Root,
            AgentHubRules.BuildAgentInstructions(null, AgentProvider.Antigravity, Workspace, Path.Combine(temp.Root, "attachments")),
            UseLocalCustomizations: true);
    }

    private async Task<List<AgentEvent>> RunTurnAsync(IAgentSession session, string text, AgentSessionSettings settings)
    {
        var events = new BlockingCollection<AgentEvent>();
        void Handler(AgentEvent ev) => events.Add(ev);
        session.EventReceived += Handler;
        var collected = new List<AgentEvent>();
        try
        {
            await session.SendAsync(new AgentTurnInput(text, Array.Empty<AgentAttachment>()), settings);
            var deadline = DateTime.UtcNow + TurnTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!events.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                    continue;
                collected.Add(ev);
                output.WriteLine($"{ev.GetType().Name}: {Describe(ev)}");
                if (ev is AgentTurnCompletedEvent)
                    return collected;
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
        AgentTurnCompletedEvent c => $"{c.Outcome} {c.ErrorMessage} 輸入 {c.Usage?.InputTokens} 輸出 {c.Usage?.OutputTokens}",
        AgentNoticeEvent n => n.Message,
        AgentSessionStartedEvent s => $"{s.SessionId} {s.Model}",
        _ => string.Empty,
    };

    private static string AllText(IEnumerable<AgentEvent> events)
        => string.Join("\n", events.OfType<AgentTextCompletedEvent>().Select(t => t.Text));

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task 登入狀態與模型清單_agy_models_及無頭額度查詢_agy_print_usage()
    {
        var result = await CliRunner.RunAsync(Launcher(), AntigravityStreamRules.ModelsArguments,
            env => AccountRules.ApplyCommandEnvironment(account, env), TimeSpan.FromSeconds(60), CancellationToken.None);
        output.WriteLine($"結束碼 {result.ExitCode}\n{result.Stdout}\n{result.Stderr}");

        var evaluation = AntigravityStreamRules.EvaluateModelsCommand(result.ExitCode, result.Stdout, result.Stderr, result.TimedOut);
        evaluation.Status.Should().Be(AccountStatus.Ready, "要先登入 agy");
        evaluation.Models.Should().NotBeEmpty("登入後 agy models 會列出模型");
        evaluation.Models.Should().Contain(m => m.Value == Model && m.Efforts.Contains("low"), "整合測試用的模型");
        evaluation.Models.Should().OnlyContain(m => !AntigravityStreamRules.ModelEncodesEffort(m.Value), "同一個模型的幾個強度要合併成基本 id");

        var logFile = Path.Combine(temp.Root, "usage.log");
        var usageRun = await CliRunner.RunAsync(Launcher(), AntigravityStreamRules.UsageArgumentsWithLog(logFile),
            env => AccountRules.ApplyCommandEnvironment(account, env), TimeSpan.FromSeconds(60), CancellationToken.None);
        output.WriteLine($"額度結束碼 {usageRun.ExitCode}\n{usageRun.Stdout}\n{usageRun.Stderr}");

        var email = AntigravityStreamRules.ParseSignedInEmail(File.Exists(logFile) ? File.ReadAllText(logFile) : null);
        email.Should().NotBeNullOrWhiteSpace("--log-file 搭 -p /usage 應讀得到登入的信箱");
        var usageEval = AntigravityStreamRules.EvaluateUsageCommand(usageRun.ExitCode, usageRun.Stdout, usageRun.Stderr, usageRun.TimedOut);
        usageEval.Status.Should().Be(AccountStatus.Ready);
        var parsed = UsageRules.ParseAntigravityUsage(usageEval.UsageData);
        parsed.Should().NotBeNull();
        parsed!.Value.Windows.Should().NotBeEmpty("登入後 -p /usage 會回報配額桶");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task 串流文字_工具卡_跨程序以conversation續接仍記得前文()
    {
        string conversationId;
        using (var session = new AntigravityAgentSession())
        {
            // 自動接受編輯可以寫檔（執行指令會被自動拒絕，見下一個測試）
            var first = await RunTurnAsync(session, "請用工具在工作資料夾建立檔案 note.txt，內容是「ok」；另外記住暗號「藍鯨 42」（不要寫進檔案）。最後只回答「好」。", Settings(null, started: false));
            first.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
            first.OfType<AgentTextDeltaEvent>().Should().NotBeEmpty("文字要串流");
            first.OfType<AgentToolStartedEvent>().Should().NotBeEmpty("要有工具卡");
            first.OfType<AgentToolStartedEvent>().Select(t => t.ToolId).Should().BeSubsetOf(first.OfType<AgentToolCompletedEvent>().Select(t => t.ToolId), "每個工具都要結束");
            var writeTool = first.OfType<AgentToolStartedEvent>().Single(t => t.ToolKind == "write" && t.FilePath != null && t.FilePath.EndsWith("note.txt"));
            writeTool.Diff.Should().NotBeNull("伴讀 transcript_full.jsonl 應補齊 1.4.6 write_file 的紅綠 Diff");
            writeTool.Diff!.Should().Contain(d => d.T == "add" && d.S.Contains("ok"));
            File.Exists(Path.Combine(Workspace, "note.txt")).Should().BeTrue();
            var started = first.OfType<AgentSessionStartedEvent>().Single();
            conversationId = started.SessionId;
            started.Model.Should().Be(Model, "--model 基本 id＋--effort low");
            first.OfType<AgentTurnCompletedEvent>().Single().Usage!.InputTokens.Should().BeGreaterThan(0);
            await session.CloseAsync();
        }

        using (var session = new AntigravityAgentSession())
        {
            var second = await RunTurnAsync(session, "剛才的暗號是什麼？不要使用工具，只回答暗號。", Settings(conversationId, started: true));
            second.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
            second.OfType<AgentNoticeEvent>().Should().BeEmpty("續接要成功，不能改開新對話");
            AllText(second).Should().Contain("42");
        }
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task 自動接受編輯時執行指令被自動拒絕_工具卡失敗並提示改用完全自動()
    {
        using var session = new AntigravityAgentSession();

        var events = await RunTurnAsync(session, "請用 run_command 工具執行指令 echo agy-it，然後回報輸出。", Settings(null, started: false));

        // agy 有時報 ERROR（附 permission check failed 訊息）、有時只報 DONE 沒有輸出（由 denied_actions 判斷）：兩種都要是失敗並附說明
        events.OfType<AgentToolCompletedEvent>().Should().Contain(t => t.Status == AgentItemStatus.Failed && !string.IsNullOrWhiteSpace(t.Output));
        events.OfType<AgentNoticeEvent>().Should().ContainSingle().Which.Message.Should().Contain("自動（沙箱）").And.Contain("完全自動");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed, "agy 照常結束這一輪（常常沒有任何文字）");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task 續接的對話不存在_agy只在stderr警告_會話改開新對話並附上前文()
    {
        const string missing = "00000000-0000-4000-8000-000000000000";
        using var session = new AntigravityAgentSession();
        var settings = Settings(missing, started: true) with
        {
            HistoryRecap = AgentHubRules.BuildHistoryRecap(new[]
            {
                new AgentChatItem { Kind = AgentItemKind.User, Text = "請記住暗號「白鷺 17」" },
                new AgentChatItem { Kind = AgentItemKind.Assistant, Text = "好" },
            }),
        };

        var events = await RunTurnAsync(session, "剛才的暗號是什麼？只回答暗號。", settings);

        events.OfType<AgentNoticeEvent>().Should().ContainSingle(n => n.Message.Contains("改開新對話"));
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        AllText(events).Should().Contain("17", "重送時附上了前文節錄");
        events.OfType<AgentSessionStartedEvent>().Last().SessionId.Should().NotBe(missing);
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task 執行指令中按停止_這一輪結束為已停止()
    {
        using var session = new AntigravityAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);
        await session.SendAsync(new AgentTurnInput("執行 ping -n 60 127.0.0.1 並回報結果。", Array.Empty<AgentAttachment>()),
            Settings(null, started: false, AgentPermissionMode.FullAccess));

        var deadline = DateTime.UtcNow + TurnTimeout;
        AgentTurnCompletedEvent? completed = null;
        bool interrupted = false;
        while (DateTime.UtcNow < deadline && completed == null)
        {
            if (!events.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                continue;
            output.WriteLine($"{ev.GetType().Name}: {Describe(ev)}");
            if (ev is AgentToolStartedEvent && !interrupted)
            {
                await Task.Delay(3000);
                await session.InterruptAsync();
                interrupted = true;
            }
            completed = ev as AgentTurnCompletedEvent;
        }

        interrupted.Should().BeTrue("要在指令執行中按停止");
        completed.Should().NotBeNull();
        completed!.Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        session.IsRunning.Should().BeFalse();
    }
}
