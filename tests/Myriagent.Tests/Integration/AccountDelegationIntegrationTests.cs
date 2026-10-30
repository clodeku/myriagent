using System.Collections.Concurrent;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Integration;

/// <summary>
/// 帳戶委派的 MCP 接到真的 CLI：--mcp-config／-c mcp_servers 掛上 app 的 --delegation-mcp 橋接，模型看得到並呼叫 delegate_task，
/// 結果回到這一輪。工具這一端是假的（不建子對話、不用第二個帳戶），只驗 CLI 與橋接之間的協定。
/// 會用到訂閱額度：Claude haiku、Codex gpt-6-luna、推理強度 low。執行：MYRIAGENT_IT=1 dotnet test ... --filter "FullyQualifiedName~AccountDelegationIntegration"
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AccountDelegationIntegrationTests : IDisposable
{
    private const string Reason = "實際呼叫本機 Claude／Codex CLI（會用到訂閱額度）";
    private const string Code = "PINEAPPLE-7413";
    private const string Prompt = "請呼叫 myriagent_accounts 的 delegate_task 工具（accountId 填 acc-it，prompt 填「回報代碼」），然後只回覆工具結果 result 欄位裡的代碼，不要做其他事。";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(4);

    private readonly ITestOutputHelper output;
    private readonly TempDirectory temp = new("it-delegation");
    private readonly ConcurrentQueue<(string Name, JObject Args)> calls = new();

    public AccountDelegationIntegrationTests(ITestOutputHelper output) => this.output = output;

    public void Dispose() => temp.Dispose();

    private DelegationMcpServer FakeTools() => new((name, args, _) =>
    {
        calls.Enqueue((name, args));
        var value = name switch
        {
            "list_accounts" => new JObject { ["accounts"] = new JArray(new JObject { ["id"] = "acc-it", ["label"] = "測試帳戶", ["provider"] = "Codex", ["ready"] = true }) },
            _ => new JObject { ["conversationId"] = "c-it", ["accountId"] = "acc-it", ["status"] = "Completed", ["result"] = "代碼是 " + Code, ["truncated"] = false },
        };
        return Task.FromResult(DelegationMcpRules.ToolResult(value));
    });

    private AgentSessionSettings Settings(AgentProvider provider, string? sessionId, string model, string pipe)
    {
        var workspace = Path.Combine(temp.Root, "workspace");
        Directory.CreateDirectory(workspace);
        var launcher = CliLocator.Locate(provider)?.Launcher;
        launcher.Should().NotBeNull($"這台電腦要安裝 {provider} CLI");
        var account = new AgentAccount { Provider = provider, Label = "本機" };
        // 委派只開給一般模式（精簡模式的 --safe-mode 會連 --mcp-config 一起停用）
        return new AgentSessionSettings(
            sessionId, false, launcher, $"it|{provider}", env => AccountRules.ApplyEnvironment(account, env), null,
            model, "low", AgentPermissionMode.Ask, workspace, temp.Root,
            AgentHubRules.BuildAgentInstructions(null, provider, workspace, Path.Combine(temp.Root, "attachments")),
            UseLocalCustomizations: true, DelegationMcpPipeName: pipe);
    }

    private async Task<List<AgentEvent>> RunTurnAsync(IAgentSession session, AgentSessionSettings settings)
    {
        var events = new BlockingCollection<AgentEvent>();
        void Handler(AgentEvent ev) => events.Add(ev);
        session.EventReceived += Handler;
        var collected = new List<AgentEvent>();
        try
        {
            await session.SendAsync(new AgentTurnInput(Prompt, []), settings);
            var deadline = DateTime.UtcNow + TurnTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!events.TryTake(out var ev, TimeSpan.FromSeconds(1)))
                    continue;
                collected.Add(ev);
                output.WriteLine($"{ev.GetType().Name}: {Describe(ev)}");
                switch (ev)
                {
                    case AgentApprovalEvent { IsPlan: false } a:
                        await session.RespondApprovalAsync(a.RequestKey, AgentApprovalChoice.Allow, null);
                        break;
                    case AgentTurnCompletedEvent:
                        return collected;
                }
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
        AgentApprovalEvent a => $"{a.ToolName}（{a.ToolKind}）{a.Title}",
        AgentTurnCompletedEvent c => $"{c.Outcome} {c.ErrorMessage}",
        AgentNoticeEvent n => n.Message,
        _ => string.Empty,
    };

    private void AssertDelegated(List<AgentEvent> events)
    {
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        calls.Should().Contain(c => c.Name == "delegate_task" && DelegationMcpRules.StringField(c.Args, "accountId") == "acc-it", "模型要經橋接呼叫到 delegate_task");
        string.Join("\n", events.OfType<AgentTextCompletedEvent>().Select(t => t.Text)).Should().Contain(Code, "工具結果要回到這一輪");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Claude_經MCP橋接呼叫delegate_task且不另外跳核准()
    {
        using var tools = FakeTools();
        using var session = new ClaudeAgentSession();
        var events = await RunTurnAsync(session, Settings(AgentProvider.Claude, Guid.NewGuid().ToString(), "haiku", tools.PipeName));
        AssertDelegated(events);
        events.OfType<AgentApprovalEvent>().Should().NotContain(a => a.ToolName.Contains(DelegationMcpRules.ServerName), "--allowedTools 放行了委派工具");
    }

    [EnvFact("MYRIAGENT_IT", Reason)]
    public async Task Codex_經MCP橋接呼叫delegate_task()
    {
        using var tools = FakeTools();
        using var session = new CodexAgentSession();
        var events = await RunTurnAsync(session, Settings(AgentProvider.Codex, null, "gpt-6-luna", tools.PipeName));
        AssertDelegated(events);
    }
}
