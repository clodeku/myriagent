using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>用假的 app-server 驗證原生 RPC、讀取不續跑、設定順序與結構化技能。</summary>
public sealed class CodexControlsSessionTests : IDisposable
{
    private readonly TempDirectory temp = new("codex-controls");
    private const string Server = """
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $inputReader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $outputWriter = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $outputWriter.AutoFlush = $true
        $goal = @{ objective = 'original'; status = 'paused'; tokensUsed = 5; tokenBudget = 1000 }
        function Emit($x) { $outputWriter.WriteLine(($x | ConvertTo-Json -Depth 30 -Compress)) }
        while (($line = $inputReader.ReadLine()) -ne $null) {
            $m = $line | ConvertFrom-Json
            if ($null -eq $m.id) { continue }
            [System.IO.File]::AppendAllText($logPath, $line + "`n", $utf8)
            $r = @{}
            $finish = $false
            switch ($m.method) {
                'initialize' { $r = @{ userAgent = 'fake/0.164.0'; codexHome = 'fake' } }
                'model/list' { $r = @{ data = @(@{ id = 'fake'; model = 'fake'; isDefault = $true; serviceTiers = @(@{ id = 'priority'; name = 'Fast' }) }); nextCursor = $null } }
                'skills/list' { $r = @{ data = @(@{ cwd = 'work'; skills = @(@{ name = 'test-skill'; path = 'C:/skills/test/SKILL.md'; description = 'test'; enabled = $true }); errors = @() }) } }
                'collaborationMode/list' { $r = @{ data = @(@{ mode = 'default'; name = 'Default' }, @{ mode = 'plan'; name = 'Plan' }) } }
                'thread/goal/get' { $r = @{ goal = $goal } }
                'thread/goal/set' {
                    $goal.status = $m.params.status
                    if ($m.params.objective) { $goal.objective = $m.params.objective }
                    $r = @{ goal = $goal }
                }
                'thread/goal/clear' { $goal = $null }
                'thread/start' { $r = @{ thread = @{ id = 'thread-fake' }; model = 'fake' } }
                'thread/resume' { $r = @{ thread = @{ id = $m.params.threadId }; model = 'fake' } }
                'mcpServerStatus/list' {
                    if ($m.params.cursor) { $r = @{ data = @(@{ name = 'second'; authStatus = 'notLoggedIn'; tools = @{} }); nextCursor = $null } }
                    else { $r = @{ data = @(@{ name = 'first'; authStatus = 'unsupported'; runtimeStatus = 'connected'; tools = @{ one = @{} } }); nextCursor = 'page2' } }
                }
                'mcpServer/oauth/login' { $r = @{ authorizationUrl = 'https://example.test/oauth' } }
                { $_ -in 'turn/start', 'review/start', 'thread/compact/start' } {
                    $r = @{ turn = @{ id = 'turn-1'; status = 'inProgress' } }
                    $finish = $true
                }
            }
            Emit @{ id = $m.id; result = $r }
            if ($finish) {
                Emit @{ method = 'turn/started'; params = @{ threadId = 'thread-fake'; turn = @{ id = 'turn-1'; status = 'inProgress' } } }
                Emit @{ method = 'turn/completed'; params = @{ threadId = 'thread-fake'; turn = @{ id = 'turn-1'; status = 'completed' } } }
            }
        }
        """;

    private AgentSessionSettings Settings(bool started = false)
    {
        var log = Path.Combine(temp.Root, "calls.jsonl");
        var script = temp.WriteFile("server.ps1", "$logPath = '" + log.Replace("'", "''") + "'\n" + Server);
        return new AgentSessionSettings(started ? "thread-fake" : null, started,
            new AgentLauncher(CodexAgentSessionTests.PowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script]),
            "fake", _ => { }, null, "fake", "low", AgentPermissionMode.FullAccess,
            temp.CreateDirectory("work"), temp.Root, "測試", UseLocalCustomizations: true);
    }

    private List<JObject> Calls() => File.ReadAllLines(Path.Combine(temp.Root, "calls.jsonl")).Select(JObject.Parse).ToList();

    [Fact]
    public async Task 規劃與執行都傳入原生模式_不改寫內建指示()
    {
        using var session = new CodexAgentSession();
        var events = CodexAgentSessionTests.Collect(session);
        foreach (var mode in new[] { "plan", "default" })
        {
            await session.CodexControlAsync("mode", new JObject { ["mode"] = mode }, Settings());
            await session.SendAsync(new AgentTurnInput("測試", []), Settings() with { CodexMode = mode });
            await CodexAgentSessionTests.TakeUntilAsync(events, e => e is AgentTurnCompletedEvent);
            var sent = Calls().Last(c => c.Value<string>("method") == "turn/start")["params"]!["collaborationMode"]!;
            sent.Value<string>("mode").Should().Be(mode);
            sent["settings"]!.Value<string>("model").Should().Be("fake");
            sent["settings"]!["developer_instructions"]!.Type.Should().Be(JTokenType.Null);
        }
    }
    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task 開面板只讀取_不載入原對話也不開始一輪()
    {
        using var session = new CodexAgentSession();
        var result = await session.CodexControlAsync("inspect", new JObject(), Settings(started: true));
        result["goal"]!.Value<string>("status").Should().Be("paused");
        result["tiers"]![0]!.Value<string>("id").Should().Be("priority");
        result["skills"]!.Should().HaveCount(1);
        Calls().Select(c => c.Value<string>("method")).Should().NotContain(["thread/resume", "thread/start", "turn/start"]);
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 繼續目標前先暫停_載入並套用設定後才啟動()
    {
        using var session = new CodexAgentSession();
        var settings = Settings(started: true) with { CodexServiceTier = "priority" };
        await session.CodexControlAsync("goalResume", new JObject(), settings);
        var relevant = Calls().Where(c => c.Value<string>("method")!.StartsWith("thread/")).ToList();
        relevant.Select(c => c.Value<string>("method")).Should().ContainInOrder("thread/goal/get", "thread/goal/set", "thread/resume", "thread/settings/update", "thread/goal/set");
        relevant.First(c => c.Value<string>("method") == "thread/goal/set")["params"]!.Value<string>("status").Should().Be("paused");
        relevant.Last()["params"]!.Value<string>("status").Should().Be("active");
        var applied = relevant.Single(c => c.Value<string>("method") == "thread/settings/update")["params"]!;
        applied.Value<string>("serviceTier").Should().Be("priority");
        applied.Value<string>("approvalPolicy").Should().Be("never");
        applied.Value<string>("cwd").Should().Be(settings.WorkingDirectory);
    }

    [Theory]
    [InlineData("/compact", "thread/compact/start")]
    [InlineData("/review", "review/start")]
    public async Task 手動操作使用原生方法_不用一般訊息冒充(string text, string method)
    {
        using var session = new CodexAgentSession();
        var events = CodexAgentSessionTests.Collect(session);
        await session.SendAsync(new AgentTurnInput(text, []), Settings());
        await CodexAgentSessionTests.TakeUntilAsync(events, e => e is AgentTurnCompletedEvent);
        Calls().Select(c => c.Value<string>("method")).Should().Contain(method).And.NotContain("turn/start");
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 指定技能與速度送成結構化參數_不存在的技能不送出()
    {
        using var session = new CodexAgentSession();
        var settings = Settings() with { CodexServiceTier = "priority", CodexSkills = [new("test-skill", "C:/skills/test/SKILL.md")] };
        var events = CodexAgentSessionTests.Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", []), settings);
        await CodexAgentSessionTests.TakeUntilAsync(events, e => e is AgentTurnCompletedEvent);
        var sent = Calls().Single(c => c.Value<string>("method") == "turn/start")["params"]!;
        sent.Value<string>("serviceTier").Should().Be("priority");
        sent["input"]!.OfType<JObject>().Single(i => i.Value<string>("type") == "skill").Value<string>("path").Should().Be("C:/skills/test/SKILL.md");
        await session.Invoking(s => s.CodexControlAsync("skillSelect", JObject.Parse("""{"paths":["C:/not-present/SKILL.md"]}"""), settings))
            .Should().ThrowAsync<InvalidOperationException>();
        await session.Invoking(s => s.CodexControlAsync("speed", JObject.Parse("""{"serviceTier":"ultrafast"}"""), settings))
            .Should().ThrowAsync<InvalidOperationException>();
        Calls().Count(c => c.Value<string>("method") == "turn/start").Should().Be(1);
    }

    [Fact]
    public async Task MCP分頁_授權與重連都走原生方法_不開對話()
    {
        using var session = new CodexAgentSession();
        var settings = Settings();
        var status = await session.GetMcpStatusAsync(settings);
        status.Servers!.Select(s => s.Status).Should().Equal("connected", "needs-auth");
        var auth = await session.McpActionAsync("second", AgentMcpAction.Authenticate, settings);
        auth.AuthUrl.Should().Be("https://example.test/oauth");
        (await session.McpActionAsync("first", AgentMcpAction.Reconnect, settings)).Ok.Should().BeTrue();
        Calls().Select(c => c.Value<string>("method")).Should().Contain("config/mcpServer/reload").And.NotContain("thread/start");
    }
}
