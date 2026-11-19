using System.Collections.Concurrent;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>
/// 各家代理共用記憶（Accounts.SharedMemoryRules）對這台電腦真的 CLI：Codex、Grok、Antigravity 的代理拿到說明之後，
/// 讀得到既有的記憶（索引指到的檔）、也寫得進記憶資料夾（沙箱的可寫資料夾、自動接受編輯、--add-dir 真的有用）。
/// 記憶資料夾在暫存的假 ~/.claude 底下，不碰真的使用者目錄。每一家兩輪，會用到訂閱額度：模型選最便宜的、推理強度 low。
/// 執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~SharedMemoryIntegration"（Grok 的登入資料夾見那個測試的說明）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class SharedMemoryIntegrationTests : IDisposable
{
    private const string Reason = "實際呼叫本機 Codex／Grok／Antigravity CLI（要先登入，會用到額度）";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(5);

    private readonly ITestOutputHelper output;
    private readonly TempDirectory temp = new("it-shared-memory");

    public SharedMemoryIntegrationTests(ITestOutputHelper output) => this.output = output;

    public void Dispose() => temp.Dispose();

    private string Workspace => Path.Combine(temp.Root, "workspace");

    /// <summary>假的 ~/.claude 與這個工作資料夾的記憶：一則既有的記憶（暗號）</summary>
    private (SharedCustomizations Shared, string Memory) Seed()
    {
        Directory.CreateDirectory(Workspace);
        var source = temp.CreateDirectory(".claude");
        var memory = SharedCustomizationRules.SharedMemoryDirectory(source, Workspace);
        Directory.CreateDirectory(memory);
        File.WriteAllText(Path.Combine(memory, "MEMORY.md"), "- [專案暗號](project-code.md) — 這個專案對外確認身分用的暗號\n");
        File.WriteAllText(Path.Combine(memory, "project-code.md"),
            "---\nname: project-code\ndescription: 這個專案對外確認身分用的暗號\nmetadata:\n  type: project\n---\n\n這個專案的暗號是「藍鯨 42」。\n\n**Why:** 測試共用記憶讀不讀得到。\n**How to apply:** 有人問暗號就回這一個。\n");
        return (new SharedCustomizations(_ => source, () => temp.Combine(".claude.json")), memory);
    }

    private AgentSessionSettings Settings(AgentProvider provider, AgentAccount account, string model, AgentPermissionMode mode, SharedCustomizations shared, string? sessionId)
    {
        var launcher = CliLocator.Locate(provider)?.Launcher;
        launcher.Should().NotBeNull($"這台電腦要安裝 {provider} CLI");
        return new AgentSessionSettings(
            sessionId, false, launcher, $"it|{provider}|memory", env => AccountRules.ApplyEnvironment(account, env), null,
            model, "low", mode, Workspace, temp.Root,
            AgentHubRules.BuildAgentInstructions(null, provider, Workspace, Path.Combine(temp.Root, "attachments")),
            UseLocalCustomizations: true,
            PrepareShared: cwd => shared.WithSharedMemory(AgentSharedLaunch.None, cwd));
    }

    /// <summary>跑一輪：核准一律允許（記下問了什麼）；回傳本輪事件</summary>
    private async Task<List<AgentEvent>> RunTurnAsync(IAgentSession session, string text, AgentSessionSettings settings, List<string> approvals)
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
                output.WriteLine($"{DateTime.Now:HH:mm:ss} {ev.GetType().Name}: {Describe(ev)}");
                if (ev is AgentApprovalEvent approval)
                {
                    approvals.Add($"{approval.ToolKind}:{approval.Title}");
                    await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
                }
                if (ev is AgentTurnCompletedEvent)
                    return collected;
            }
            throw new TimeoutException("CLI 沒有在時限內結束這一輪");
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
        AgentToolCompletedEvent t => $"{t.Status} {ClaudeStreamRules.FirstLine(t.Output ?? string.Empty)}",
        AgentTurnCompletedEvent c => $"{c.Outcome} {c.ErrorMessage}",
        AgentNoticeEvent n => n.Message,
        AgentApprovalEvent a => $"{a.ToolName}（{a.ToolKind}）{a.Title}",
        _ => string.Empty,
    };

    /// <summary>讀既有的記憶、再存一則新的；回傳存記憶那一輪問了哪些核准</summary>
    private async Task<List<string>> ReadThenSaveAsync(IAgentSession session, Func<string?, AgentSessionSettings> settingsFor, string memory)
    {
        var approvals = new List<string>();
        var first = await RunTurnAsync(session, "這個專案的暗號是什麼？查記憶，只回答暗號本身。", settingsFor(null), approvals);
        first.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        string.Join("\n", first.OfType<AgentTextCompletedEvent>().Select(t => t.Text)).Should().Contain("藍鯨").And.Contain("42", "代理要從共用記憶讀到既有的那一則");
        var sessionId = first.OfType<AgentSessionStartedEvent>().LastOrDefault()?.SessionId;

        approvals.Clear();
        var second = await RunTurnAsync(session, "請記住這件事，存成一則記憶：這個專案部署之前一定要先跑 smoke 測試（原因：上次沒跑就上線，出過事）。存好後只回「已存」。", settingsFor(sessionId), approvals);
        second.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);

        var files = Directory.GetFiles(memory, "*.md").Select(Path.GetFileName).ToList();
        output.WriteLine("記憶資料夾：" + string.Join("、", files));
        var index = File.ReadAllText(Path.Combine(memory, "MEMORY.md"));
        output.WriteLine("MEMORY.md：\n" + index);
        var added = files.Where(f => f is not ("MEMORY.md" or "project-code.md")).ToList();
        added.Should().NotBeEmpty("新的記憶要寫進共用的記憶資料夾（寫不進去就是沙箱或權限沒放行）");
        File.ReadAllText(Path.Combine(memory, added[0]!)).Should().ContainEquivalentOf("smoke").And.Contain("name:");
        index.Should().Contain("project-code.md", "原本的索引不能被蓋掉").And.Contain(added[0], "新的記憶要加進索引");
        output.WriteLine("存記憶時的核准：" + (approvals.Count == 0 ? "（沒有）" : string.Join("、", approvals)));
        return approvals;
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_讀得到既有的記憶_自動模式的沙箱寫得進記憶資料夾()
    {
        var (shared, memory) = Seed();
        var account = new AgentAccount { Provider = AgentProvider.Codex, Label = "本機" };
        using var session = new CodexAgentSession();

        var approvals = await ReadThenSaveAsync(session,
            id => Settings(AgentProvider.Codex, account, "gpt-6-luna", AgentPermissionMode.Auto, shared, id) with { SessionStarted = id != null }, memory);

        approvals.Should().BeEmpty("記憶資料夾是沙箱的可寫資料夾：不必跳出沙箱、不必問");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Grok_讀得到既有的記憶_接受編輯時寫記憶不必問()
    {
        var (shared, memory) = Seed();
        // 帳戶同 GrokIntegrationTests：已登入的隔離 GROK_HOME（MYRIAGENT_IT_GROK_HOME，沒設就用探針的 home-a）；
        // MYRIAGENT_IT_GROK_HOME=default 改用這台系統預設的 ~/.grok（探針的登入過期時用；系統預設帳戶不改使用者的設定）
        var home = Environment.GetEnvironmentVariable("MYRIAGENT_IT_GROK_HOME");
        bool systemDefault = string.Equals(home, "default", StringComparison.OrdinalIgnoreCase);
        var account = new AgentAccount
        {
            Id = "it-grok",
            Provider = AgentProvider.Grok,
            Label = "Grok（整合測試）",
            ConfigDirectory = systemDefault
                ? null
                : home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "myriagent-probes", "grok", "home-a"),
        };
        if (!systemDefault)
            GrokAcpRules.EnsureIsolatedHome(account.ConfigDirectory!);
        using var session = new GrokAgentSession();
        var sid = Guid.NewGuid().ToString();

        var approvals = await ReadThenSaveAsync(session,
            id => Settings(AgentProvider.Grok, account, "grok-4.6", AgentPermissionMode.AcceptEdits, shared, sid) with { SessionStarted = id != null }, memory);

        approvals.Where(a => a.StartsWith("edit", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty("記憶資料夾裡的編輯在「接受編輯」時代為核准");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Antigravity_讀得到既有的記憶_寫得進記憶資料夾()
    {
        var (shared, memory) = Seed();
        var account = new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity（本機）" };
        using var session = new AntigravityAgentSession();

        await ReadThenSaveAsync(session,
            id => Settings(AgentProvider.Antigravity, account, "gemini-3.8-flash", AgentPermissionMode.AcceptEdits, shared, id) with { SessionStarted = id != null }, memory);
    }
}
