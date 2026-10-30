using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// Codex 的持續目標（thread goal）：目標還沒完成時，codex 每輪結束後自己開下一輪（turn/started 不是 app 送 turn/start 開始的）。
/// 以前會話只記下 turn id、不標成執行中：回覆一直出來，對話卻沒有執行中的動畫、停止鈕，換版還把它當成閒置直接關掉程序。
/// 假 app-server 照 0.164.0 的實測（tools/probes/codex_goal_probe.py）：目標的一輪在跑時 turn/start 併進那一輪、回它的 id。
/// 腳本只用 ASCII（Windows PowerShell 讀沒有 BOM 的腳本用系統碼頁）
/// </summary>
public sealed class CodexGoalSessionTests : IDisposable
{
    private const string FakeCodex = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $log = New-Object System.Collections.Generic.List[string]
        function Emit([string]$json) { $out.WriteLine($json) }

        $script:active = $null
        $script:n = 0
        $script:steered = $null
        $script:interrupted = $null

        function Respond($msg) {
            $id = $msg.id
            switch ($msg.method) {
                'initialize' { Emit ('{"id":' + $id + ',"result":{"userAgent":"fake","codexHome":"fake"}}') }
                'thread/start' { Emit ('{"id":' + $id + ',"result":{"thread":{"id":"thread-goal"},"model":"fake"}}') }
                'turn/start' {
                    $text = [string]$msg.params.input[0].text
                    if ($script:active -ne $null) {
                        # 0.164.0: turn/start while a goal turn is running merges into it and returns its id
                        $log.Add('merged=' + $text + '|' + $script:active)
                        Emit ('{"id":' + $id + ',"result":{"turn":{"id":"' + $script:active + '","items":[],"status":"inProgress"}}}')
                        return
                    }
                    $script:n++
                    $script:active = 'turn-' + $script:n
                    Emit ('{"id":' + $id + ',"result":{"turn":{"id":"' + $script:active + '","items":[],"status":"inProgress"}}}')
                    Emit ('{"method":"turn/started","params":{"threadId":"thread-goal","turn":{"id":"' + $script:active + '","status":"inProgress"}}}')
                }
                'turn/steer' {
                    $script:steered = [string]$msg.params.input[0].text + '|' + [string]$msg.params.expectedTurnId
                    Emit ('{"id":' + $id + ',"result":{"turnId":"' + $script:active + '"}}')
                }
                'turn/interrupt' {
                    $script:interrupted = [string]$msg.params.turnId
                    Emit ('{"id":' + $id + ',"result":{}}')
                }
                default { Emit ('{"id":' + $id + ',"result":{}}') }
            }
        }

        function Pump {
            $line = $in.ReadLine()
            if ($line -eq $null) { exit 0 }
            if ($line.Trim().Length -eq 0) { return $null }
            $msg = $line | ConvertFrom-Json
            if ($msg.method -and $msg.id -ne $null) { Respond $msg }
            return $null
        }

        function Goal([string]$status) { Emit ('{"method":"thread/goal/updated","params":{"threadId":"thread-goal","turnId":null,"goal":{"threadId":"thread-goal","objective":"port everything","status":"' + $status + '","tokenBudget":null,"tokensUsed":0,"timeUsedSeconds":0,"createdAt":1,"updatedAt":1}}}') }
        function Message([string]$text) { Emit ('{"method":"item/completed","params":{"item":{"type":"agentMessage","id":"msg-' + $script:active + '","text":"' + $text + '"},"threadId":"thread-goal","turnId":"' + $script:active + '"}}') }
        function Tool([string]$method, [string]$status) { Emit ('{"method":"' + $method + '","params":{"item":{"type":"commandExecution","id":"exec-g","command":"ping","status":"' + $status + '","commandActions":[{"type":"unknown","command":"ping"}]},"threadId":"thread-goal","turnId":"' + $script:active + '"}}') }
        function Complete([string]$status) {
            Emit ('{"method":"turn/completed","params":{"threadId":"thread-goal","turn":{"id":"' + $script:active + '","items":[],"status":"' + $status + '","error":null}}}')
            $script:active = $null
        }

        # the app's turn: the model sets a goal and ends this turn; codex starts the goal's next turn on its own
        while ($script:active -eq $null) { $null = Pump }
        Goal 'active'
        Message 'first batch done'
        Complete 'completed'
        $script:n++
        $script:active = 'goal-' + $script:n
        Emit ('{"method":"turn/started","params":{"threadId":"thread-goal","turn":{"id":"' + $script:active + '","status":"inProgress"}}}')
        Tool 'item/started' 'inProgress'

        switch ($scenario) {
            'stop' {
                while ($script:steered -eq $null) { $null = Pump }
                while ($script:interrupted -eq $null) { $null = Pump }
                Tool 'item/completed' 'failed'
                Complete 'interrupted'
                Emit ('{"method":"account/rateLimits/updated","params":{"rateLimits":{"marker":"steer=' + $script:steered + ',interrupt=' + $script:interrupted + '"}}}')
            }
            'takeover' {
                while ($log.Count -eq 0) { $null = Pump }
                Tool 'item/completed' 'completed'
                Goal 'complete'
                Message ([string]::Join(',', $log))
                Complete 'completed'
            }
        }
        while ($in.ReadLine() -ne $null) { }
        """";

    private readonly TempDirectory temp = new("fake-codex-goal");

    public void Dispose() => temp.Dispose();

    private AgentSessionSettings Settings(string scenario)
    {
        var script = temp.WriteFile($"fake-codex-goal-{scenario}.ps1", $"$scenario = '{scenario}'\n" + FakeCodex);
        return new AgentSessionSettings(
            null,
            false,
            new AgentLauncher(CodexAgentSessionTests.PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            "fake",
            _ => { },
            null,
            null,
            null,
            AgentPermissionMode.FullAccess,
            temp.CreateDirectory("work"),
            temp.Root,
            "測試說明",
            UseLocalCustomizations: true);
    }

    [Fact]
    public async Task 目標的下一輪_會話標成執行中並發事件_工具執行中不在安全點_可以插入與停止()
    {
        using var session = new CodexAgentSession();
        var events = CodexAgentSessionTests.Collect(session);
        await session.SendAsync(new AgentTurnInput("start", Array.Empty<AgentAttachment>()), Settings("stop"));

        var first = await CodexAgentSessionTests.TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        first.OfType<AgentTurnCompletedEvent>().Single().ContinuesOnItsOwn.Should().BeTrue("目標還在進行：codex 接著自己開下一輪，中樞不念完成");

        var goalTurn = await CodexAgentSessionTests.TakeUntilAsync(events, ev => ev is AgentSafePointChangedEvent { ToolRunning: true });
        goalTurn.OfType<AgentIdleTurnStartedEvent>().Should().ContainSingle()
            .Which.Should().Be(new AgentIdleTurnStartedEvent(AfterBackgroundTask: false, ForThreadGoal: true));
        goalTurn.FindIndex(e => e is AgentIdleTurnStartedEvent).Should().BeLessThan(goalTurn.FindIndex(e => e is AgentToolStartedEvent),
            "中樞要先把對話標成執行中，再收到這一輪的內容");
        session.IsRunning.Should().BeTrue("以前只記下 turn id：對話看起來閒置、停不下來");
        session.IsAtSafePoint.Should().BeFalse("指令在跑：換版不能把它當成閒置直接關掉程序");

        (await session.SteerAsync(new AgentTurnInput("more info", Array.Empty<AgentAttachment>()))).Should().BeTrue("送出的訊息插進目標的這一輪");
        await session.InterruptAsync();
        var stopped = await CodexAgentSessionTests.TakeUntilAsync(events, ev => ev is AgentRateLimitEvent);

        stopped.OfType<AgentTurnCompletedEvent>().Single().Should().Match<AgentTurnCompletedEvent>(t => t.Outcome == AgentTurnOutcome.Interrupted && !t.ContinuesOnItsOwn);
        stopped.OfType<AgentRateLimitEvent>().Single().Raw["marker"]!.ToString().Should().Be("steer=more info|goal-2,interrupt=goal-2",
            "插入與停止都帶目標這一輪的 id");
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 目標的下一輪剛開始時送出_接手成為這一輪_turnStart併進去_只結束一次()
    {
        using var session = new CodexAgentSession();
        var events = CodexAgentSessionTests.Collect(session);
        var settings = Settings("takeover");
        await session.SendAsync(new AgentTurnInput("start", Array.Empty<AgentAttachment>()), settings);
        await CodexAgentSessionTests.TakeUntilAsync(events, ev => ev is AgentIdleTurnStartedEvent);

        // 中樞收到上一輪的結束就接著送佇列裡的訊息：會話這時已因目標的下一輪在執行中，不能回「代理還在處理上一則訊息」
        await session.SendAsync(new AgentTurnInput("take over", Array.Empty<AgentAttachment>()), settings);
        var turn = await CodexAgentSessionTests.TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);

        turn.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("merged=take over|goal-2");
        turn.OfType<AgentTurnCompletedEvent>().Single().Should().Match<AgentTurnCompletedEvent>(t => t.Outcome == AgentTurnOutcome.Completed && !t.ContinuesOnItsOwn,
            "模型標記完成（goal complete 比 turn/completed 先到）：這一輪才是真的完成");
        turn.OfType<AgentIdleTurnStartedEvent>().Should().BeEmpty("接手的那一輪是 app 的：不再發一次開始");
        session.IsRunning.Should().BeFalse();
    }
}
