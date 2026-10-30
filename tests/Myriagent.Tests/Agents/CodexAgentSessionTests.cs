using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 以假的 codex app-server（Windows PowerShell 腳本，照 codex-cli 0.160.1 的訊息格式）測 CodexAgentSession 的流程：
/// 子代理（multi_agent）的事件、MCP 工具核准、網路存取核准、沙箱狀態、續接被拒、準備期間按停止、啟動就結束。不連網、不耗額度。
/// 腳本只用 ASCII（Windows PowerShell 讀沒有 BOM 的腳本用系統碼頁）。
/// </summary>
public sealed class CodexAgentSessionTests : IDisposable
{
    internal static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 假 codex：$scenario 決定這一輪做什麼。$calls 記收到的準備請求（建立對話、沙箱設定、turn/start 的沙箱），
    /// $log 記宿主對伺服器請求的回應；結束時以一段回覆文字回報，測試據此確認宿主送了什麼。
    /// </summary>
    private const string FakeCodex = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $calls = New-Object System.Collections.Generic.List[string]
        $log = New-Object System.Collections.Generic.List[string]
        function Emit([string]$json) { $out.WriteLine($json) }

        if ($scenario -eq 'exit_at_start') {
            [Console]::Error.WriteLine('Error: failed to load config.toml: invalid type at line 3')
            exit 1
        }

        $script:turnStarted = $false
        $script:interruptTurn = $null
        $script:steered = $null
        $script:bgAlive = $false

        function Respond($msg) {
            $id = $msg.id
            switch ($msg.method) {
                'initialize' { Emit ('{"id":' + $id + ',"result":{"userAgent":"fake","codexHome":"fake"}}') }
                'windowsSandbox/readiness' {
                    $status = 'ready'
                    if ($scenario -eq 'sandbox_update_required') { $status = 'updateRequired' }
                    if ($scenario -eq 'sandbox_slow' -or $scenario -eq 'sandbox_inflight') { $status = 'notConfigured' }
                    Emit ('{"id":' + $id + ',"result":{"status":"' + $status + '"}}')
                }
                'windowsSandbox/setupStart' {
                    $calls.Add('setupStart')
                    Emit ('{"id":' + $id + ',"result":{"started":true}}')
                    if ($scenario -eq 'sandbox_inflight') { Start-Sleep -Milliseconds 3000 }
                    if ($scenario -ne 'sandbox_slow') { Emit '{"method":"windowsSandbox/setupCompleted","params":{"mode":"unelevated","success":true,"error":null}}' }
                }
                'thread/start' {
                    $calls.Add('thread/start')
                    if ($scenario -eq 'thread_start_slow') {
                        Emit '{"method":"account/rateLimits/updated","params":{"rateLimits":{"marker":"thread/start"}}}'
                        return
                    }
                    Emit ('{"id":' + $id + ',"result":{"thread":{"id":"thread-fake"},"model":"fake"}}')
                }
                'thread/resume' {
                    $calls.Add('thread/resume')
                    if ($scenario -eq 'resume_active_writer') {
                        Emit ('{"error":{"code":-32600,"message":"thread ' + $msg.params.threadId + ' already has an active writer"},"id":' + $id + '}')
                        return
                    }
                    Emit ('{"id":' + $id + ',"result":{"thread":{"id":"' + $msg.params.threadId + '"},"model":"fake"}}')
                }
                'turn/start' {
                    $calls.Add('sandbox=' + $msg.params.sandboxPolicy.type)
                    Emit ('{"id":' + $id + ',"result":{"turn":{"id":"turn-1","items":[],"status":"inProgress"}}}')
                    $script:turnStarted = $true
                }
                'turn/interrupt' {
                    $script:interruptTurn = [string]$msg.params.turnId
                    Emit ('{"id":' + $id + ',"result":{}}')
                }
                # Measured on 0.164.0 (codex_background_probe.py --mode stop): a stopped terminal reports item/completed
                # (failed, exit -1) right away; here it is sent before the response (the host must not depend on the order)
                'thread/backgroundTerminals/list' {
                    $data = '[]'
                    if ($script:bgAlive) { $data = '[{"itemId":"bg1","processId":"12","command":"npm test","cwd":"C:/w1","osPid":null,"cpuPercent":null,"rssKb":null}]' }
                    Emit ('{"id":' + $id + ',"result":{"data":' + $data + ',"nextCursor":null}}')
                }
                'thread/backgroundTerminals/terminate' {
                    $ok = $script:bgAlive -and [string]$msg.params.processId -eq '12'
                    if ($ok) { $script:bgAlive = $false; Emit '{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"bg1","command":"npm test","cwd":"C:/w1","status":"failed","exitCode":-1,"durationMs":1200,"aggregatedOutput":null,"source":"unifiedExecStartup","processId":"12"},"threadId":"thread-fake","turnId":"turn-1"}}' }
                    Emit ('{"id":' + $id + ',"result":{"terminated":' + $ok.ToString().ToLower() + '}}')
                }
                'thread/backgroundTerminals/clean' {
                    if ($script:bgAlive) { $script:bgAlive = $false; Emit '{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"bg1","command":"npm test","cwd":"C:/w1","status":"failed","exitCode":-1,"durationMs":1200,"aggregatedOutput":null,"source":"unifiedExecStartup","processId":"12"},"threadId":"thread-fake","turnId":"turn-1"}}' }
                    Emit ('{"id":' + $id + ',"result":{}}')
                }
                'turn/steer' {
                    # Measured on 0.163.0: accepted into the running turn (returns its id); otherwise -32600 no active turn to steer
                    if ($scenario -eq 'steer_reject' -or -not $script:turnStarted -or $msg.params.expectedTurnId -ne 'turn-1') {
                        Emit ('{"error":{"code":-32600,"message":"no active turn to steer"},"id":' + $id + '}')
                        $script:steered = 'rejected'
                        return
                    }
                    $script:steered = [string]$msg.params.input[0].text + '|expected=' + [string]$msg.params.expectedTurnId
                    Emit ('{"id":' + $id + ',"result":{"turnId":"turn-1"}}')
                }
                default { Emit ('{"id":' + $id + ',"result":{}}') }
            }
        }

        function Pump {
            $line = $in.ReadLine()
            if ($line -eq $null) { exit 0 }
            if ($line.Trim().Length -eq 0) { return $null }
            $msg = $line | ConvertFrom-Json
            if ($msg.method) {
                if ($msg.id -ne $null) { Respond $msg }
                return $null
            }
            return $msg
        }

        function WaitFor([int]$id) {
            while ($true) {
                $r = Pump
                if ($r -ne $null -and $r.id -eq $id) { return $r }
            }
        }

        function WaitMany([int[]]$ids) {
            $got = @{}
            while ($got.Count -lt $ids.Count) {
                $r = Pump
                if ($r -ne $null -and $ids -contains [int]$r.id) { $got[[int]$r.id] = $r }
            }
            return $got
        }

        function Decision($r) { return [string]$r.result.decision }

        function AskCommand([int]$id, [string]$thread, [string]$command, [string]$extra) {
            Emit ('{"method":"item/commandExecution/requestApproval","id":' + $id + ',"params":{"kind":"command","threadId":"' + $thread + '","turnId":"turn-1","itemId":"e' + $id + '","command":"' + $command + '","cwd":"C:/w1","commandActions":[{"type":"unknown","command":"' + $command + '"}],"additionalPermissions":' + $extra + ',"availableDecisions":["accept","acceptForSession","decline","cancel"]}}')
        }

        function AskNetwork([int]$id, [string]$hostName) {
            Emit ('{"method":"item/commandExecution/requestApproval","id":' + $id + ',"params":{"kind":"command","threadId":"thread-fake","turnId":"turn-1","itemId":"net' + $id + '","reason":"' + $hostName + ' is not in the allowed_domains","networkApprovalContext":{"host":"' + $hostName + '","protocol":"https"},"command":null,"cwd":null,"commandActions":null,"additionalPermissions":null,"availableDecisions":["accept","acceptForSession","cancel"]}}')
        }

        function Finish([string]$text, [string]$status) {
            Emit ('{"method":"item/completed","params":{"item":{"type":"agentMessage","id":"msg-final","text":"' + $text + '"},"threadId":"thread-fake","turnId":"turn-1"}}')
            Emit '{"method":"thread/tokenUsage/updated","params":{"threadId":"thread-fake","turnId":"turn-1","tokenUsage":{"last":{"inputTokens":10,"outputTokens":7,"cachedInputTokens":0}}}}'
            Emit ('{"method":"turn/completed","params":{"threadId":"thread-fake","turn":{"id":"turn-1","items":[],"status":"' + $status + '","error":null}}}')
        }

        while (-not $script:turnStarted) { $null = Pump }
        Emit '{"method":"turn/started","params":{"threadId":"thread-fake","turn":{"id":"turn-1","status":"inProgress"}}}'

        switch ($scenario) {
            'subagent' {
                Emit '{"method":"turn/started","params":{"threadId":"thread-child","turn":{"id":"child-turn","status":"inProgress"}}}'
                Emit '{"method":"item/agentMessage/delta","params":{"threadId":"thread-child","turnId":"child-turn","itemId":"child-msg","delta":"child says hi"}}'
                Emit '{"method":"item/completed","params":{"item":{"type":"agentMessage","id":"child-msg","text":"child says hi"},"threadId":"thread-child","turnId":"child-turn"}}'
                Emit '{"method":"thread/tokenUsage/updated","params":{"threadId":"thread-child","turnId":"child-turn","tokenUsage":{"last":{"inputTokens":999,"outputTokens":999,"cachedInputTokens":0}}}}'
                AskCommand 200 'thread-child' 'child cmd' 'null'
                $log.Add('200=' + (Decision (WaitFor 200)))
                Emit '{"method":"turn/completed","params":{"threadId":"thread-child","turn":{"id":"child-turn","items":[],"status":"completed","error":null}}}'
                AskCommand 201 'thread-child' 'child cmd 2' 'null'
                Finish ([string]::Join(',', $log)) 'completed'
                $log.Add('201=' + (Decision (WaitFor 201)))
                Emit ('{"method":"account/rateLimits/updated","params":{"rateLimits":{"marker":"' + [string]::Join(',', $log) + '"}}}')
            }
            'subagent_stop' {
                Emit '{"method":"turn/started","params":{"threadId":"thread-child","turn":{"id":"child-turn","status":"inProgress"}}}'
                Emit '{"method":"item/started","params":{"item":{"type":"collabAgentToolCall","id":"collab1","tool":"spawnAgent","prompt":"child task"},"threadId":"thread-fake","turnId":"turn-1"}}'
                while ($script:interruptTurn -eq $null) { $null = Pump }
                Finish ('interrupt=' + $script:interruptTurn) 'interrupted'
            }
            'mcp_elicitation' {
                Emit '{"method":"mcpServer/elicitation/request","id":300,"params":{"threadId":"thread-fake","turnId":"turn-1","serverName":"github","mode":"form","_meta":{"codex_approval_kind":"mcp_tool_call","persist":"session","tool_title":"Create issue","tool_params":{"title":"bug"}},"message":"Allow the github MCP server to run tool \"create_issue\"?","requestedSchema":{"type":"object","properties":{}}}}'
                $r = WaitFor 300
                $log.Add('300=' + [string]$r.result.action + '/' + [string]$r.result._meta.persist)
                Emit '{"method":"mcpServer/elicitation/request","id":301,"params":{"threadId":"thread-fake","turnId":"turn-1","serverName":"docs","mode":"form","_meta":null,"message":"Enter your account","requestedSchema":{"type":"object","properties":{"account":{"type":"string"}}}}}'
                $r = WaitFor 301
                $log.Add('301=' + [string]$r.result.action)
                Finish ([string]::Join(',', $log)) 'completed'
            }
            'network' {
                AskNetwork 400 'api.example.com'
                $log.Add('400=' + (Decision (WaitFor 400)))
                AskNetwork 401 'evil.example.net'
                $log.Add('401=' + (Decision (WaitFor 401)))
                AskNetwork 402 'api.example.com'
                $log.Add('402=' + (Decision (WaitFor 402)))
                AskCommand 403 'thread-fake' 'echo a' 'null'
                $log.Add('403=' + (Decision (WaitFor 403)))
                AskCommand 404 'thread-fake' 'echo a' '{"network":{"enabled":true}}'
                $log.Add('404=' + (Decision (WaitFor 404)))
                AskCommand 405 'thread-fake' 'echo a' 'null'
                $log.Add('405=' + (Decision (WaitFor 405)))
                Finish ([string]::Join(',', $log)) 'completed'
            }
            'full_access' {
                AskCommand 500 'thread-child' 'child cmd' 'null'
                $log.Add('500=' + (Decision (WaitFor 500)))
                Emit '{"method":"item/fileChange/requestApproval","id":501,"params":{"threadId":"thread-child","turnId":"child-turn","itemId":"p501","reason":null,"grantRoot":null}}'
                $log.Add('501=' + (Decision (WaitFor 501)))
                Emit '{"method":"mcpServer/elicitation/request","id":502,"params":{"threadId":"thread-child","turnId":"child-turn","serverName":"github","mode":"form","_meta":{"codex_approval_kind":"mcp_tool_call","persist":"session","tool_title":"Create issue","tool_params":{"title":"bug"}},"message":"Allow the github MCP server to run tool \"create_issue\"?","requestedSchema":{"type":"object","properties":{}}}}'
                $log.Add('502=' + [string](WaitFor 502).result.action)
                AskNetwork 503 'api.example.com'
                $log.Add('503=' + (Decision (WaitFor 503)))
                Emit '{"method":"item/tool/requestUserInput","id":504,"params":{"threadId":"thread-child","turnId":"child-turn","itemId":"q504","questions":[{"id":"q1","header":"Format","question":"Which format?","options":[{"label":"Excel"},{"label":"CSV"}]}]}}'
                $log.Add('504=' + [string](WaitFor 504).result.answers.q1.answers[0])
                Finish ([string]::Join(',', $log)) 'completed'
            }
            'switch_to_full_access' {
                AskCommand 600 'thread-child' 'child cmd' 'null'
                AskCommand 601 'thread-fake' 'main cmd' 'null'
                $got = WaitMany @(600, 601)
                $log.Add('600=' + (Decision $got[600]))
                $log.Add('601=' + (Decision $got[601]))
                AskCommand 602 'thread-child' 'child cmd 2' 'null'
                $log.Add('602=' + (Decision (WaitFor 602)))
                Finish ([string]::Join(',', $log)) 'completed'
            }
            { $_ -eq 'steer' -or $_ -eq 'steer_reject' } {
                # wait for the inserted message, then (accepted) record it as a userMessage item of the same turn
                while ($script:steered -eq $null) { $null = Pump }
                if ($script:steered -ne 'rejected') {
                    Emit '{"method":"item/started","params":{"item":{"type":"userMessage","id":"um-2","content":[]},"threadId":"thread-fake","turnId":"turn-1"}}'
                    Emit '{"method":"item/completed","params":{"item":{"type":"userMessage","id":"um-2","content":[]},"threadId":"thread-fake","turnId":"turn-1"}}'
                }
                Finish ('steered=' + $script:steered) 'completed'
            }
            { $_ -eq 'background' -or $_ -eq 'background_exit' -or $_ -eq 'background_interrupted' } {
                # Measured on 0.164.0 (docs/protocol-notes.md "Codex background commands"): a unified_exec command still running
                # when the turn completes; it reports item/completed later, outside any turn, and codex starts no turn of its own
                Emit '{"method":"item/started","params":{"item":{"type":"commandExecution","id":"c1","command":"echo quick","cwd":"C:/w1","status":"inProgress","source":"unifiedExecStartup","processId":"11"},"threadId":"thread-fake","turnId":"turn-1"}}'
                Emit '{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"c1","command":"echo quick","cwd":"C:/w1","status":"completed","exitCode":0,"durationMs":40,"source":"unifiedExecStartup","processId":"11"},"threadId":"thread-fake","turnId":"turn-1"}}'
                Emit '{"method":"item/started","params":{"item":{"type":"commandExecution","id":"bg1","command":"npm test","cwd":"C:/w1","status":"inProgress","source":"unifiedExecStartup","processId":"12"},"threadId":"thread-fake","turnId":"turn-1"}}'
                if ($scenario -eq 'background_interrupted') { Finish 'stopped' 'interrupted' } else { Finish 'started' 'completed' }
                Start-Sleep -Milliseconds 500
                if ($scenario -eq 'background_exit') { exit 0 }
                Emit '{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"bg1","command":"npm test","cwd":"C:/w1","status":"completed","exitCode":1,"durationMs":26891,"aggregatedOutput":"1 failed","source":"unifiedExecStartup","processId":"12"},"threadId":"thread-fake","turnId":"turn-1"}}'
            }
            'background_stop' {
                # a unified_exec command still running when the turn completes; it only ends when the host stops it
                Emit '{"method":"item/started","params":{"item":{"type":"commandExecution","id":"bg1","command":"npm test","cwd":"C:/w1","status":"inProgress","source":"unifiedExecStartup","processId":"12"},"threadId":"thread-fake","turnId":"turn-1"}}'
                $script:bgAlive = $true
                Finish 'started' 'completed'
                while ($true) { $null = Pump }
            }
            'yield' {
                # Measured on 0.164.0 (codex_background_probe.py --mode yield): a unified_exec command still running when its thread's
                # model call ends (thread/tokenUsage/updated) was handed back to the model, which goes on with other commands
                Emit '{"method":"item/started","params":{"item":{"type":"commandExecution","id":"dev1","command":"npm run dev","cwd":"C:/w1","status":"inProgress","source":"unifiedExecStartup","processId":"21"},"threadId":"thread-fake","turnId":"turn-1"}}'
                Emit '{"method":"item/started","params":{"item":{"type":"commandExecution","id":"cc1","command":"child serve","cwd":"C:/w1","status":"inProgress","source":"unifiedExecStartup","processId":"22"},"threadId":"thread-child","turnId":"child-turn"}}'
                Emit '{"method":"thread/tokenUsage/updated","params":{"threadId":"thread-child","turnId":"child-turn","tokenUsage":{"last":{"inputTokens":5,"outputTokens":1,"cachedInputTokens":0}}}}'
                Emit '{"method":"thread/tokenUsage/updated","params":{"threadId":"thread-fake","turnId":"turn-1","tokenUsage":{"last":{"inputTokens":10,"outputTokens":7,"cachedInputTokens":0}}}}'
                Emit '{"method":"item/started","params":{"item":{"type":"commandExecution","id":"c2","command":"npm run build","cwd":"C:/w1","status":"inProgress","source":"unifiedExecStartup","processId":"23"},"threadId":"thread-fake","turnId":"turn-1"}}'
                Emit '{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"c2","command":"npm run build","cwd":"C:/w1","status":"completed","exitCode":0,"durationMs":900,"source":"unifiedExecStartup","processId":"23"},"threadId":"thread-fake","turnId":"turn-1"}}'
                Emit '{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"dev1","command":"npm run dev","cwd":"C:/w1","status":"completed","exitCode":0,"durationMs":5000,"source":"unifiedExecStartup","processId":"21"},"threadId":"thread-fake","turnId":"turn-1"}}'
                Emit '{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"cc1","command":"child serve","cwd":"C:/w1","status":"completed","exitCode":0,"durationMs":5000,"source":"unifiedExecStartup","processId":"22"},"threadId":"thread-child","turnId":"child-turn"}}'
                Finish 'done' 'completed'
            }
            default { Finish ([string]::Join(',', $calls)) 'completed' }
        }
        while ($in.ReadLine() -ne $null) { }
        """";

    private readonly TempDirectory temp = new("fake-codex");

    public void Dispose() => temp.Dispose();

    private AgentSessionSettings Settings(string scenario, AgentPermissionMode mode = AgentPermissionMode.FullAccess, string? sessionId = null)
    {
        var script = temp.WriteFile($"fake-codex-{scenario}.ps1", $"$scenario = '{scenario}'\n" + FakeCodex);
        var work = temp.CreateDirectory("work");
        return new AgentSessionSettings(
            sessionId,
            sessionId != null,
            new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            "fake",
            _ => { },
            null,
            null,
            null,
            mode,
            work,
            temp.Root,
            "測試說明",
            UseLocalCustomizations: true);
    }

    internal static BlockingCollection<AgentEvent> Collect(IAgentSession session)
    {
        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);
        return events;
    }

    /// <summary>收事件直到 until 成立（含那一個事件）；onEvent 可以回應核准卡</summary>
    internal static async Task<List<AgentEvent>> TakeUntilAsync(BlockingCollection<AgentEvent> events, Func<AgentEvent, bool> until, Func<AgentEvent, Task>? onEvent = null, TimeSpan? timeout = null)
    {
        var collected = new List<AgentEvent>();
        var deadline = DateTime.UtcNow + (timeout ?? Deadline);
        while (DateTime.UtcNow < deadline)
        {
            if (!events.TryTake(out var ev, TimeSpan.FromMilliseconds(100)))
                continue;
            collected.Add(ev);
            if (onEvent != null)
                await onEvent(ev);
            if (until(ev))
                return collected;
        }
        throw new TimeoutException("假的 codex 沒有在時限內送出預期的事件；收到：" + string.Join("、", collected.Select(e => e.GetType().Name)));
    }

    [Fact]
    public async Task 子代理先做完_主對話這一輪不算結束_子代理的內容只進專用卡片_還沒回應的子代理核准在這一輪結束時婉拒()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("subagent", AgentPermissionMode.Ask));

        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent, async ev =>
        {
            if (ev is AgentApprovalEvent { RequestKey: "200" } approval)
                await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        });

        var completed = turn.OfType<AgentTurnCompletedEvent>().Single();
        completed.Outcome.Should().Be(AgentTurnOutcome.Completed);
        completed.Usage!.OutputTokens.Should().Be(7, "子代理的用量不能蓋掉主對話這一輪的");
        turn.OfType<AgentTextCompletedEvent>().Select(t => t.Text).Should().Equal(new[] { "200=accept" },
            "子代理的 turn/completed 不能讓這一輪提早結束（主對話的回覆在那之後）；子代理的回覆也不混進主對話");
        turn.OfType<AgentTextDeltaEvent>().Should().NotContain(d => d.BlockId == "child-msg");
        var approvals = turn.OfType<AgentApprovalEvent>().ToList();
        approvals.Select(a => a.RequestKey).Should().Equal(new[] { "200", "201" }, "子代理的核准一樣要顯示、可以回應");
        approvals.Should().OnlyContain(a => a.ToolName == "執行指令（子代理）");

        // 主對話這一輪結束時，畫面上的子代理核准卡跟著失效：宿主要回婉拒，子代理才不會一直等
        var marker = (await TakeUntilAsync(events, ev => ev is AgentRateLimitEvent)).OfType<AgentRateLimitEvent>().Single();
        marker.Raw["marker"]!.ToString().Should().Be("200=accept,201=decline");
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 子代理執行中按停止_送出的是主對話的TurnId()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("subagent_stop"));

        await TakeUntilAsync(events, ev => ev is AgentToolStartedEvent);
        await session.InterruptAsync();
        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);

        turn.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("interrupt=turn-1",
            "turn/interrupt 帶子代理的 turn id 會被 codex 拒絕，15 秒後看門狗只能殺掉整個程序");
        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);
    }

    // ───── 執行中插入訊息（docs/protocol-notes.md「執行中插入訊息」） ─────

    [Fact]
    public async Task 插入訊息_準備期間先等turnStart有回應_以turnSteer帶這一輪的id併進同一輪_使用者訊息項目不轉成事件()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        (await session.SteerAsync(new AgentTurnInput("idle", Array.Empty<AgentAttachment>()))).Should().BeFalse("沒在執行：中樞照常送出");

        // 送出還在準備（啟動程序、建立對話、turn/start 還沒回應）就插入：等 turn/start 有了這一輪的 id 才送
        var send = session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("steer"));
        var steered = await session.SteerAsync(new AgentTurnInput("more info", Array.Empty<AgentAttachment>()));
        await send;
        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);

        steered.Should().BeTrue();
        turn.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("steered=more info|expected=turn-1");
        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed, "插入的訊息併進同一輪，只結束一次");
        session.IsRunning.Should().BeFalse();
        (await session.SteerAsync(new AgentTurnInput("after", Array.Empty<AgentAttachment>()))).Should().BeFalse("這一輪已結束");
    }

    [Fact]
    public async Task 插入訊息_codex不接受時回false_這一輪照常進行()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("steer_reject"));

        (await session.SteerAsync(new AgentTurnInput("more", Array.Empty<AgentAttachment>()))).Should().BeFalse(
            "codex 回錯誤（這一輪剛結束、/review 這類不接受插入的一輪）：中樞把它留在佇列，這一輪結束後照常送");
        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
    }

    [Fact]
    public async Task 插入訊息_按了停止就不插入()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("subagent_stop"));
        await TakeUntilAsync(events, ev => ev is AgentToolStartedEvent);
        await session.InterruptAsync();

        (await session.SteerAsync(new AgentTurnInput("more", Array.Empty<AgentAttachment>()))).Should().BeFalse("按了停止：留在佇列跟著暫停");
        await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
    }

    // ───── 背景指令（docs/protocol-notes.md「Codex 的背景指令」） ─────

    [Fact]
    public async Task 背景指令_這一輪結束時還在跑的指令_先回報背景工作再結束這一輪_之後跑完回報結束碼與耗時()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("background"));

        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        var started = turn.OfType<AgentBackgroundWorkEvent>().Should().ContainSingle("這一輪跑完的 echo quick 不算，只有還在跑的 npm test").Subject;
        started.Count.Should().Be(1);
        started.ToolIds.Should().Equal("bg1");
        started.Tools.Single().Title.Should().Be("npm test");
        turn.IndexOf(started).Should().BeLessThan(turn.FindIndex(e => e is AgentTurnCompletedEvent), "中樞結束這一輪時要知道哪些卡片留在執行中");
        turn.OfType<AgentToolCompletedEvent>().Select(t => t.ToolId).Should().Equal(new[] { "c1" }, "背景指令這時還沒結束");
        session.IsRunning.Should().BeFalse();
        session.HasLiveBackgroundWork.Should().BeTrue();
        session.IsAtSafePoint.Should().BeFalse("重開會連同背景指令結束：等它跑完");
        session.LiveBackgroundTools.Single().Title.Should().Be("npm test");

        var later = await TakeUntilAsync(events, ev => ev is AgentBackgroundCommandFinishedEvent);
        later.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(t => t.ToolId == "bg1" && t.Status == AgentItemStatus.Failed && t.Output == "1 failed");
        later.OfType<AgentBackgroundWorkEvent>().Single().Count.Should().Be(0);
        later.OfType<AgentBackgroundCommandFinishedEvent>().Single().Should().Be(
            new AgentBackgroundCommandFinishedEvent("bg1", "npm test", AgentItemStatus.Failed, 1, TimeSpan.FromMilliseconds(26891)));
        later.OfType<AgentTurnStartedByCliEvent>().Should().BeEmpty("Codex 不會自己開一輪：接續由中樞決定");
        session.HasLiveBackgroundWork.Should().BeFalse();
        session.IsAtSafePoint.Should().BeTrue();
    }

    [Fact]
    public async Task 讓出的指令_模型呼叫結束時還在跑的指令改算背景工作_不算工具在執行_這一輪中途跑完不另外接續()
    {
        // 2026-10-24：npm run dev 讓出給模型後一直跑，模型接著做別的事，app 更新卻一直「等目前的指令完成」到排空逾時
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("yield"));

        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        turn.OfType<AgentSafePointChangedEvent>().Select(e => (e.ToolRunning, e.HasLiveBackgroundWork)).Should().Equal(
            (true, false),    // npm run dev 開始（子代理的指令也開始）
            (true, true),     // 子代理的模型呼叫結束：子代理的指令讓出，主對話的 npm run dev 照舊是工具在執行
            (false, true),    // 主對話的模型呼叫結束：npm run dev 讓出，不再是在等的工具
            (true, true),     // 模型接著跑 npm run build
            (false, true),    // npm run build 結束：前景沒有工具，只剩背景工作
            (false, false));  // 讓出的兩個指令都跑完
        turn.OfType<AgentBackgroundWorkEvent>().Select(e => (e.Count, string.Join(",", e.ToolIds ?? Array.Empty<string>()))).Should().Equal(
            (1, ""),          // 子代理的指令沒有工具卡，只算數
            (2, "dev1"),
            (1, ""),
            (0, ""));
        turn.OfType<AgentBackgroundWorkEvent>().ElementAt(1).Tools.Single().Title.Should().Be("npm run dev");
        turn.OfType<AgentToolCompletedEvent>().Select(t => t.ToolId).Should().Equal("c2", "dev1");
        turn.OfType<AgentBackgroundCommandFinishedEvent>().Should().BeEmpty("這一輪中途跑完的，模型自己會看到結果：不另外隱藏送出接續訊息");
        session.HasLiveBackgroundWork.Should().BeFalse();
        session.IsAtSafePoint.Should().BeTrue();
    }

    [Fact]
    public async Task 停止背景指令_單獨一個以list找processId再terminate_卡片改成已停止_結束事件帶StoppedByUser()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("background_stop"));
        await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        session.LiveBackgroundTools.Single().StopId.Should().Be("bg1", "以工具卡 id 單獨停止");

        (await session.StopBackgroundWorkAsync("zzz")).Should().Be(new AgentBackgroundStopResult(0, "這個背景指令已經結束了"), "只認目前的背景指令");
        (await session.StopBackgroundWorkAsync("bg1")).Should().Be(new AgentBackgroundStopResult(1, null));

        var later = await TakeUntilAsync(events, ev => ev is AgentBackgroundCommandFinishedEvent);
        later.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(t =>
            t.ToolId == "bg1" && t.Status == AgentItemStatus.Cancelled && t.Output == CodexAppServerRules.StoppedByUserOutput,
            "terminate 結束的是 failed、結束碼 -1：卡片寫已停止");
        later.OfType<AgentBackgroundCommandFinishedEvent>().Single().Should().Be(
            new AgentBackgroundCommandFinishedEvent("bg1", "npm test", AgentItemStatus.Cancelled, -1, TimeSpan.FromMilliseconds(1200), StoppedByUser: true));
        later.OfType<AgentBackgroundWorkEvent>().Single().Count.Should().Be(0);
        session.HasLiveBackgroundWork.Should().BeFalse();
        (await session.StopBackgroundWorkAsync(null)).Stopped.Should().Be(0, "沒有背景指令了");
    }

    [Fact]
    public async Task 停止背景指令_全部以clean停止()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("background_stop"));
        await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);

        (await session.StopBackgroundWorkAsync(null)).Should().Be(new AgentBackgroundStopResult(1, null));

        var later = await TakeUntilAsync(events, ev => ev is AgentBackgroundCommandFinishedEvent);
        later.OfType<AgentBackgroundCommandFinishedEvent>().Single().StoppedByUser.Should().BeTrue();
        later.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Cancelled);
        session.HasLiveBackgroundWork.Should().BeFalse();
    }

    [Fact]
    public async Task 背景指令_程序結束時跟著結束_卡片改成被中斷結果未知_背景工作回報0()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("background_exit"));
        await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        session.HasLiveBackgroundWork.Should().BeTrue();

        var exited = await TakeUntilAsync(events, ev => ev is AgentBackgroundWorkEvent { Count: 0 });
        exited.OfType<AgentToolCompletedEvent>().Single().Should().Be(
            new AgentToolCompletedEvent("bg1", AgentItemStatus.Cancelled, CodexAppServerRules.BackgroundInterruptedOutput, Array.Empty<string>()),
            "app-server 結束，背景指令跟著結束：不能一直顯示在執行中");
        exited.OfType<AgentBackgroundCommandFinishedEvent>().Should().BeEmpty("不是跑完：不接續");
        session.HasLiveBackgroundWork.Should().BeFalse();
    }

    [Fact]
    public async Task 背景指令_這一輪被停止時還在跑的指令不算背景指令()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("background_interrupted"));

        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        // 模型呼叫結束時 npm test 還在跑：這一輪中途算背景工作（讓出給模型）；停止、失敗的這一輪結束時不留——使用者不要它接著做
        turn.OfType<AgentBackgroundWorkEvent>().Select(e => e.Count).Should().Equal(1, 0);
        turn.FindLastIndex(e => e is AgentBackgroundWorkEvent).Should().BeLessThan(turn.FindIndex(e => e is AgentTurnCompletedEvent));
        session.HasLiveBackgroundWork.Should().BeFalse();
        var later = await TakeUntilAsync(events, ev => ev is AgentToolCompletedEvent { ToolId: "bg1" });
        later.OfType<AgentBackgroundCommandFinishedEvent>().Should().BeEmpty();
    }

    [Fact]
    public async Task MCP工具核准_顯示成核准卡_本對話都允許交給Codex記住_真正的表單才婉拒()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("mcp_elicitation", AgentPermissionMode.Ask));

        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent, async ev =>
        {
            if (ev is AgentApprovalEvent approval)
                await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.AllowForSession, null);
        });

        var card = turn.OfType<AgentApprovalEvent>().Single();
        card.RequestKey.Should().Be("300");
        card.ToolName.Should().Be("MCP：github");
        card.Title.Should().Be("Create issue");
        card.Detail.Should().Contain("create_issue").And.Contain("bug");
        card.CanAllowForSession.Should().BeTrue();
        card.AllowSessionLabel.Should().Be("本對話都允許這個 MCP 工具");
        turn.OfType<AgentNoticeEvent>().Should().ContainSingle(n => n.Message.Contains("「docs」") && n.Message.Contains("要求填寫表單") && n.Message.Contains("已婉拒"));
        turn.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("300=accept/session,301=decline");
    }

    [Fact]
    public async Task 網路存取核准_本對話都允許只放行同一個主機_指令要求額外權限時要再問()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("network", AgentPermissionMode.Ask));

        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent, async ev =>
        {
            if (ev is AgentApprovalEvent approval)
                await session.RespondApprovalAsync(approval.RequestKey,
                    approval.RequestKey is "400" or "403" ? AgentApprovalChoice.AllowForSession : AgentApprovalChoice.Allow, null);
        });

        var approvals = turn.OfType<AgentApprovalEvent>().ToList();
        approvals.Select(a => a.RequestKey).Should().Equal(new[] { "400", "401", "403", "404" },
            "401 是別的主機、404 多要了網路權限，都要再問；402 與 400 同主機、405 與 403 完全相同，自動放行");
        approvals[0].ToolName.Should().Be("網路存取");
        approvals[0].Title.Should().Contain("api.example.com");
        approvals[0].AllowSessionLabel.Should().Be("本對話都允許連線到 api.example.com");
        approvals[3].Detail.Should().Contain("額外權限");
        turn.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be(
            "400=acceptForSession,401=accept,402=acceptForSession,403=acceptForSession,404=accept,405=acceptForSession");
    }

    [Fact]
    public async Task 完全自動_子代理送來的核准請求直接放行不顯示卡片_問卷照常等使用者回答()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("full_access"));

        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent, async ev =>
        {
            if (ev is AgentQuestionEvent question)
                await session.AnswerQuestionAsync(question.RequestKey, new Dictionary<string, IReadOnlyList<string>> { ["q1"] = new[] { "CSV" } });
        });

        turn.OfType<AgentApprovalEvent>().Should().BeEmpty(
            "子代理建立時繼承的是那一輪的核准政策，codex 照樣會問；完全自動是宿主的選擇，一律放行");
        turn.OfType<AgentAutoApproveEvent>().Should().BeEmpty("直接回應 codex，沒有卡片要中樞放行");
        turn.OfType<AgentQuestionEvent>().Should().ContainSingle("問卷不是權限，完全自動也要使用者回答");
        turn.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("500=accept,501=accept,502=accept,503=accept,504=CSV",
            "完全自動只放行這一次（accept），不讓 codex 記成本對話都允許：之後切回其他模式就照常詢問");
    }

    [Fact]
    public async Task 執行中切到完全自動_還在等待的核准卡請中樞放行_之後的請求直接放行_切到其他模式這一輪不變()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("switch_to_full_access", AgentPermissionMode.Ask));

        var asked = await TakeUntilAsync(events, ev => ev is AgentApprovalEvent { RequestKey: "601" });
        asked.OfType<AgentApprovalEvent>().Select(a => a.RequestKey).Should().Equal("600", "601");
        (await session.SetPermissionModeAsync(AgentPermissionMode.Auto)).Should().BeFalse("核准政策隨 turn/start 帶上，自動模式從下一則訊息才生效");

        (await session.SetPermissionModeAsync(AgentPermissionMode.FullAccess)).Should().BeTrue("宿主自己放行，這一輪立即生效（中樞不必提示下一則訊息才生效）");
        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent, async ev =>
        {
            // 照中樞的做法：把等待中的卡片當成使用者按了允許
            if (ev is AgentAutoApproveEvent auto)
                await session.RespondApprovalAsync(auto.RequestKey, AgentApprovalChoice.Allow, null);
        });

        turn.OfType<AgentAutoApproveEvent>().Select(a => a.RequestKey).Distinct().Should().BeEquivalentTo(new[] { "600", "601" },
            "切換前已經跳出的卡片（子代理與主對話的）都要放行，不然這一輪會一直等");
        turn.OfType<AgentApprovalEvent>().Should().BeEmpty("切換之後的請求直接放行，不再跳卡片");
        turn.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("600=accept,601=accept,602=accept");
    }

    [Fact]
    public async Task 沙箱需要更新時_不自動改成Unelevated_這一輪先每個指令都問()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("sandbox_update_required", AgentPermissionMode.Ask));

        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);

        turn.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("thread/start,sandbox=dangerFullAccess",
            "updateRequired 代表使用者設過 elevated 沙箱：自動跑 unelevated 設定會把使用者的 config.toml 改掉，所以不送 setupStart、改用退路");
        turn.OfType<AgentNoticeEvent>().Should().Contain(n => n.Message.Contains("需要更新"));
        turn.OfType<AgentNoticeEvent>().Should().Contain(n => n.Message.Contains("每個指令都先詢問"));
    }

    [Fact]
    public async Task 續接時對話正在其他程式開啟_回報失敗_不另開新對話()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);

        var thrown = await FluentActions.Awaiting(() => session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()),
                Settings("resume_active_writer", sessionId: "t-old")))
            .Should().ThrowAsync<InvalidOperationException>();

        thrown.Which.Message.Should().Contain("其他程式");
        await Task.Delay(500);
        var raised = events.ToArray();
        raised.OfType<AgentSessionStartedEvent>().Should().BeEmpty("另開新對話會讓使用者以為接續了原對話，其實只帶了節錄");
        raised.OfType<AgentNoticeEvent>().Should().NotContain(n => n.Message.Contains("改開新的對話"));
        session.IsRunning.Should().BeFalse("可以關掉其他程式後再送");
    }

    [Fact]
    public async Task 建立對話期間按停止_馬上停_不會建立對話()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        var send = session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("thread_start_slow"));

        await TakeUntilAsync(events, ev => ev is AgentRateLimitEvent);   // 假 codex 收到 thread/start 了
        var stoppedAt = DateTime.UtcNow;
        await session.InterruptAsync();
        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent, timeout: TimeSpan.FromSeconds(10));

        (DateTime.UtcNow - stoppedAt).Should().BeLessThan(TimeSpan.FromSeconds(5), "不用等 thread/start 的 60 秒逾時");
        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        await send.WaitAsync(TimeSpan.FromSeconds(5));   // 回報已停止，不丟「送出失敗」
        turn.OfType<AgentSessionStartedEvent>().Should().BeEmpty();
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 沙箱設定期間按停止_馬上停_不會接著建立對話()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        var send = session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("sandbox_slow", AgentPermissionMode.Ask));

        await TakeUntilAsync(events, ev => ev is AgentNoticeEvent n && n.Message.Contains("正在設定"));
        var stoppedAt = DateTime.UtcNow;
        await session.InterruptAsync();
        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent, timeout: TimeSpan.FromSeconds(10));

        (DateTime.UtcNow - stoppedAt).Should().BeLessThan(TimeSpan.FromSeconds(5), "不用等沙箱設定的 120 秒");
        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        await send.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(500);
        turn.Concat(events.ToArray()).OfType<AgentSessionStartedEvent>().Should().BeEmpty("停止後不再送 thread/start");
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 沙箱設定期間按停止後再送出_等原本的設定做完_不重送設定()
    {
        using var session = new CodexAgentSession();
        var events = Collect(session);
        var settings = Settings("sandbox_inflight", AgentPermissionMode.Ask);
        var first = session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), settings);
        await TakeUntilAsync(events, ev => ev is AgentNoticeEvent n && n.Message.Contains("正在設定"));
        await session.InterruptAsync();
        await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent { Outcome: AgentTurnOutcome.Interrupted }, timeout: TimeSpan.FromSeconds(10));
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        // 假 codex 3 秒後才回報設定完成：第二輪要等那一次設定，不能再送一次 setupStart
        await session.SendAsync(new AgentTurnInput("再一次", Array.Empty<AgentAttachment>()), settings);
        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);

        turn.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("setupStart,thread/start,sandbox=workspaceWrite",
            "停止只取消等待，codex 在背景照樣把沙箱設定做完；做完就用沙箱");
        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
    }

    [Fact]
    public async Task 程序啟動就結束_同一個失敗只回報一次()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var session = new CodexAgentSession();
            var events = Collect(session);
            Exception? thrown = null;
            try
            {
                await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings("exit_at_start"));
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            await Task.Delay(1500);   // 等 OnExited 的回報
            var completed = events.ToArray().OfType<AgentTurnCompletedEvent>().ToList();
            ((thrown != null ? 1 : 0) + completed.Count).Should().Be(1,
                $"第 {attempt + 1} 次：例外（{thrown?.Message}）與 TurnCompleted（{string.Join("、", completed.Select(c => c.ErrorMessage))}）只能有一個，否則中樞會加兩列錯誤");
            completed.Should().OnlyContain(c => c.Outcome == AgentTurnOutcome.Failed);
            session.IsRunning.Should().BeFalse();
        }
    }
}

/// <summary>
/// CLI 熱更新（WP1.3）：以多程序的假 codex app-server 測 CodexAgentSession.RestartAsync 與安全點追蹤——
/// interrupt→關程序→確認舊指令都結束→截尾→thread/resume→turn/start 的訊息序列、interrupt 沒有回應時逾時改殺、
/// 直接殺時待回應的核准隨舊程序作廢、Settings=null 的停下待接續、不接續的情況、執行檔身分改變時換程序。不連網、不耗額度。
/// 獨立成一個類別：每個情境要等程序與子程序結束（數秒），和其他會話測試平行執行
/// </summary>
public sealed class CodexAgentSessionRestartTests : IDisposable
{
    private static readonly string PowerShell = CodexAgentSessionTests.PowerShell;
    private static readonly TimeSpan Deadline = CodexAgentSessionTests.Deadline;

    private readonly TempDirectory temp = new("fake-codex-restart");

    public void Dispose() => temp.Dispose();

    private static BlockingCollection<AgentEvent> Collect(IAgentSession session) => CodexAgentSessionTests.Collect(session);

    private static Task<List<AgentEvent>> TakeUntilAsync(BlockingCollection<AgentEvent> events, Func<AgentEvent, bool> until, Func<AgentEvent, Task>? onEvent = null)
        => CodexAgentSessionTests.TakeUntilAsync(events, until, onEvent);

    /// <summary>
    /// 多程序的假 codex app-server：以計數檔分辨是第幾個程序（p1＝舊版 0.160.1、之後＝新版 0.162.0），把收到的請求、
    /// 開出的子程序（模擬執行中的指令：ping 60 秒，繼承 stdout，不結束就讀不到 EOF）與 rollout 的狀態記進記錄檔，
    /// 測試據此比對 interrupt→關程序→resume→turn/start 的訊息序列，並由新程序在啟動時檢查舊程序與舊指令是不是都已結束（沒有重疊）。
    /// $scenario：restart＝interrupt 照常回 turn/completed(interrupted)、收到 EOF 就結束（指令還在跑）；
    /// restart_failed_item＝同 restart，但收到 EOF 時才送被結束的指令的 item/completed(failed)（0.160.1 實測：出現在 turn/completed(interrupted) 之後）；
    /// restart_kill＝送出核准請求後不理 interrupt、收到 EOF 也不結束（只能殺程序樹），新程序以同一個 request id 0 再要求核准；
    /// safepoint＝核准後指令結束（安全點），再收 interrupt；finish_before_interrupt＝收到 interrupt 時這一輪剛好自己完成；
    /// multi_turn＝每一輪直接回覆完成（換程序的測試）
    /// </summary>
    private const string FakeRestartCodex = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        function Emit([string]$json) { $out.WriteLine($json) }
        function J([string]$s) { return $s.Replace('\', '\\').Replace('"', '\"') }

        $n = 1
        if (Test-Path $counterPath) { $n = [int]([System.IO.File]::ReadAllText($counterPath)) + 1 }
        [System.IO.File]::WriteAllText($counterPath, [string]$n)
        $p = 'p' + $n
        function Log([string]$text) { [System.IO.File]::AppendAllText($logPath, $p + ':' + $text + [string][char]10, $utf8) }
        function Stamp($proc) { return [string]$proc.Id + '|' + [string]$proc.StartTime.ToUniversalTime().Ticks }
        function IsAlive([string]$path) {
            if (-not (Test-Path $path)) { return 'none' }
            $parts = ([System.IO.File]::ReadAllText($path)).Split('|')
            $proc = Get-Process -Id ([int]$parts[0]) -ErrorAction SilentlyContinue
            if ($proc -eq $null) { return 'False' }
            try { if ([string]$proc.StartTime.ToUniversalTime().Ticks -ne $parts[1]) { return 'False' } } catch { return 'False' }
            return [string](-not $proc.HasExited)
        }

        if ($n -eq 1) {
            [System.IO.File]::WriteAllText($parentPath, (Stamp (Get-Process -Id $PID)))
        } else {
            Log ('old-parent-alive=' + (IsAlive $parentPath) + ' old-child-alive=' + (IsAlive $childPath))
        }
        Log 'start'

        $script:turnCount = 0
        $script:turnId = ''
        $script:turnStarted = $false
        $script:interrupted = $false
        $script:ignoreEof = $false
        $script:failOnEof = $false

        function Respond($msg) {
            $id = $msg.id
            switch ($msg.method) {
                'initialize' {
                    $ver = '0.160.1'
                    if ($n -gt 1) { $ver = '0.162.0' }
                    Log 'initialize'
                    Emit ('{"id":' + $id + ',"result":{"userAgent":"myriagent/' + $ver + ' (Windows 10.0.26300; x86_64) fake","codexHome":"' + (J $codexHome) + '","platformFamily":"windows","platformOs":"windows"}}')
                }
                'thread/start' {
                    Log 'thread/start'
                    [System.IO.File]::WriteAllText($rolloutPath, '{"type":"session_meta"}' + [string][char]10)
                    Emit ('{"id":' + $id + ',"result":{"thread":{"id":"' + $tid + '","path":"' + (J $rolloutPath) + '"},"model":"fake"}}')
                }
                'thread/resume' {
                    $bytes = [System.IO.File]::ReadAllBytes($rolloutPath)
                    $lf = ($bytes.Length -gt 0) -and ($bytes[$bytes.Length - 1] -eq 10)
                    Log ('thread/resume thread=' + $msg.params.threadId + ' excludeTurns=' + $msg.params.excludeTurns + ' cwdOk=' + ($msg.params.cwd -eq $work) + ' rolloutEndsWithLf=' + $lf + ' rolloutBytes=' + $bytes.Length)
                    Emit ('{"id":' + $id + ',"result":{"thread":{"id":"' + $msg.params.threadId + '","path":"' + (J $rolloutPath) + '"},"model":"fake"}}')
                }
                'turn/start' {
                    $script:turnCount++
                    $script:turnId = $p + '-turn-' + $script:turnCount
                    Log ('turn/start text=' + $msg.params.input[0].text)
                    Emit ('{"id":' + $id + ',"result":{"turn":{"id":"' + $script:turnId + '","items":[],"status":"inProgress"}}}')
                    $script:turnStarted = $true
                }
                'turn/interrupt' {
                    Log ('turn/interrupt thread=' + $msg.params.threadId + ' turn=' + $msg.params.turnId)
                    Emit ('{"id":' + $id + ',"result":{}}')
                    $script:interrupted = $true
                }
                'thread/backgroundTerminals/clean' {
                    Log ('backgroundTerminals/clean thread=' + $msg.params.threadId)
                    Emit ('{"error":{"code":-32601,"message":"unknown method"},"id":' + $id + '}')
                }
                default { Emit ('{"id":' + $id + ',"result":{}}') }
            }
        }

        function Pump {
            $line = $in.ReadLine()
            if ($line -eq $null) {
                Log 'eof'
                if ($script:failOnEof) { Emit ('{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"exec-1","command":"ping","status":"failed","aggregatedOutput":"partial output","exitCode":1},"threadId":"' + $tid + '","turnId":"' + $script:turnId + '"}}') }
                if ($script:ignoreEof) { while ($true) { Start-Sleep -Seconds 1 } }
                exit 0
            }
            if ($line.Trim().Length -eq 0) { return $null }
            $msg = $line | ConvertFrom-Json
            if ($msg.method) {
                if ($msg.id -ne $null) { Respond $msg } else { Log $msg.method }
                return $null
            }
            Log ('response id=' + $msg.id + ' decision=' + [string]$msg.result.decision)
            return $msg
        }

        function WaitFor([int]$rid) { while ($true) { $r = Pump; if ($r -ne $null -and $r.id -eq $rid) { return $r } } }
        function WaitTurn { $script:turnStarted = $false; while (-not $script:turnStarted) { $null = Pump } }
        function TurnStarted { Emit ('{"method":"turn/started","params":{"threadId":"' + $tid + '","turn":{"id":"' + $script:turnId + '","status":"inProgress"}}}') }
        function ToolStarted([string]$item) { Emit ('{"method":"item/started","params":{"item":{"type":"commandExecution","id":"' + $item + '","command":"ping -n 60 127.0.0.1","status":"inProgress","commandActions":[{"type":"unknown","command":"ping -n 60 127.0.0.1"}]},"threadId":"' + $tid + '","turnId":"' + $script:turnId + '"}}') }
        function ToolCompleted([string]$item) { Emit ('{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"' + $item + '","command":"ping","status":"completed","aggregatedOutput":"ok","exitCode":0},"threadId":"' + $tid + '","turnId":"' + $script:turnId + '"}}') }
        function AskApproval([int]$rid, [string]$item) { Emit ('{"method":"item/commandExecution/requestApproval","id":' + $rid + ',"params":{"kind":"command","threadId":"' + $tid + '","turnId":"' + $script:turnId + '","itemId":"' + $item + '","command":"ping -n 60 127.0.0.1","cwd":"' + (J $work) + '","commandActions":[{"type":"unknown","command":"ping -n 60 127.0.0.1"}],"availableDecisions":["accept","cancel"]}}') }
        function TurnCompleted([string]$status) { Emit ('{"method":"turn/completed","params":{"threadId":"' + $tid + '","turn":{"id":"' + $script:turnId + '","items":[],"status":"' + $status + '","error":null}}}') }
        function Finish([string]$text) {
            Emit ('{"method":"item/completed","params":{"item":{"type":"agentMessage","id":"' + $p + '-msg-' + $script:turnCount + '","text":"' + $text + '"},"threadId":"' + $tid + '","turnId":"' + $script:turnId + '"}}')
            TurnCompleted 'completed'
        }
        function StartChild {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName = Join-Path $env:SystemRoot 'System32\PING.EXE'
            $psi.Arguments = '-n 60 127.0.0.1'
            $psi.UseShellExecute = $false
            $psi.CreateNoWindow = $true
            $child = [System.Diagnostics.Process]::Start($psi)
            [System.IO.File]::WriteAllText($childPath, (Stamp $child))
            Log ('child pid=' + $child.Id)
        }

        while ($true) {
            WaitTurn
            TurnStarted
            if ($n -eq 1 -and $script:turnCount -eq 1 -and $scenario -ne 'multi_turn') {
                ToolStarted 'exec-1'
                StartChild
                switch ($scenario) {
                    'restart' {
                        while (-not $script:interrupted) { $null = Pump }
                        [System.IO.File]::AppendAllText($rolloutPath, '{"type":"response_item","payload":{"type":"function_call_output"')
                        Emit ('{"method":"thread/status/changed","params":{"threadId":"' + $tid + '","status":{"type":"idle"}}}')
                        TurnCompleted 'interrupted'
                    }
                    'restart_failed_item' {
                        while (-not $script:interrupted) { $null = Pump }
                        Emit ('{"method":"thread/status/changed","params":{"threadId":"' + $tid + '","status":{"type":"idle"}}}')
                        TurnCompleted 'interrupted'
                        $script:failOnEof = $true
                    }
                    'restart_kill' {
                        AskApproval 0 'exec-1'
                        [System.IO.File]::AppendAllText($rolloutPath, '{"type":"response_item","payload":{"type":"function_call"')
                        $script:ignoreEof = $true
                        while ($true) { $null = Pump }
                    }
                    'safepoint' {
                        AskApproval 0 'exec-1'
                        $r = WaitFor 0
                        Log ('approval 0=' + [string]$r.result.decision)
                        ToolCompleted 'exec-1'
                        while (-not $script:interrupted) { $null = Pump }
                        TurnCompleted 'interrupted'
                    }
                    'finish_before_interrupt' {
                        while (-not $script:interrupted) { $null = Pump }
                        ToolCompleted 'exec-1'
                        Finish 'done-on-its-own'
                    }
                }
                continue
            }
            if ($scenario -eq 'restart_kill') {
                Start-Sleep -Milliseconds 2000
                AskApproval 0 ($p + '-exec')
                $r = WaitFor 0
                Log ('approval 0=' + [string]$r.result.decision)
            }
            Finish ($p + ' done')
        }
        """";

    /// <summary>一個重開情境的假 codex：腳本、記錄檔、rollout 與 CODEX_HOME 都在測試的暫存資料夾</summary>
    /// <param name="mode">送出與重開用的權限模式；要顯示核准卡的情境用「每步詢問」（完全自動時宿主直接放行，不顯示卡片）</param>
    private sealed class RestartFake
    {
        private readonly AgentPermissionMode mode;

        public RestartFake(TempDirectory temp, string scenario, AgentPermissionMode mode = AgentPermissionMode.FullAccess)
        {
            this.mode = mode;
            var root = temp.CreateDirectory("restart-" + scenario);
            Work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            CodexHome = Directory.CreateDirectory(Path.Combine(root, "codex-home")).FullName;
            LogPath = Path.Combine(root, "log.txt");
            ChildPath = Path.Combine(root, "child.txt");
            ParentPath = Path.Combine(root, "parent.txt");
            RolloutPath = Path.Combine(root, "rollout.jsonl");
            TailDirectory = Path.Combine(root, "partial");
            ThreadId = "thread-" + Guid.NewGuid().ToString("N")[..8];
            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var header = new StringBuilder()
                .Append("$scenario = ").Append(Quote(scenario)).Append('\n')
                .Append("$logPath = ").Append(Quote(LogPath)).Append('\n')
                .Append("$counterPath = ").Append(Quote(Path.Combine(root, "count.txt"))).Append('\n')
                .Append("$childPath = ").Append(Quote(ChildPath)).Append('\n')
                .Append("$parentPath = ").Append(Quote(ParentPath)).Append('\n')
                .Append("$rolloutPath = ").Append(Quote(RolloutPath)).Append('\n')
                .Append("$codexHome = ").Append(Quote(CodexHome)).Append('\n')
                .Append("$work = ").Append(Quote(Work)).Append('\n')
                .Append("$tid = ").Append(Quote(ThreadId)).Append('\n');
            Script = Path.Combine(root, "fake-codex.ps1");
            File.WriteAllText(Script, header + FakeRestartCodex);
            Conversation = root;
        }

        public string Script { get; }

        public string Work { get; }

        public string Conversation { get; }

        public string CodexHome { get; }

        public string LogPath { get; }

        public string ChildPath { get; }

        public string ParentPath { get; }

        public string RolloutPath { get; }

        public string TailDirectory { get; }

        public string ThreadId { get; }

        /// <summary>送出或重開用的設定（started＝中樞已記下 thread id，像重開時重建的設定）</summary>
        public AgentSessionSettings Settings(bool started = false, CliBinaryIdentity? cli = null)
            => new(
                started ? ThreadId : null,
                started,
                new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script }),
                "fake",
                _ => { },
                null,
                null,
                null,
                mode,
                Work,
                Conversation,
                "測試說明",
                UseLocalCustomizations: true,
                Cli: cli);

        public CodexAgentSession NewSession(TimeSpan? interruptTimeout = null) => new()
        {
            RestartInterruptTimeout = interruptTimeout ?? TimeSpan.FromSeconds(10),
            RestartCloseGrace = TimeSpan.FromSeconds(1),
            RestartTreeExitTimeout = TimeSpan.FromSeconds(1),
            TailBackupDirectory = TailDirectory,
        };

        public string[] Log() => File.Exists(LogPath) ? File.ReadAllLines(LogPath, Encoding.UTF8) : Array.Empty<string>();

        public async Task WaitForLogAsync(string prefix)
        {
            var deadline = DateTime.UtcNow + Deadline;
            while (DateTime.UtcNow < deadline)
            {
                if (Log().Any(l => l.StartsWith(prefix, StringComparison.Ordinal)))
                    return;
                await Task.Delay(50);
            }
            throw new TimeoutException($"假 codex 的記錄沒有出現「{prefix}」；目前：{string.Join(" | ", Log())}");
        }

        /// <summary>記錄的「PID|建立時間」對應的程序還活著（同一個 PID、建立時間相同）</summary>
        public static bool IsAlive(string stampPath)
        {
            var parts = File.ReadAllText(stampPath).Split('|');
            try
            {
                using var process = Process.GetProcessById(int.Parse(parts[0]));
                return !process.HasExited && process.StartTime.ToUniversalTime().Ticks.ToString() == parts[1];
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }

    private static AgentRestartRequest Restart(AgentRestartMode mode, AgentSessionSettings? settings, string? continuation = null, bool autoContinue = true)
        => new(AgentRestartReason.CliUpdated, mode, "0.160.1", "0.162.0", autoContinue, continuation, Array.Empty<AgentInterruptedTool>(), settings);

    private static int IndexOf<T>(List<AgentEvent> events, Func<T, bool>? match = null) where T : AgentEvent
        => events.FindIndex(e => e is T t && (match == null || match(t)));

    [Fact]
    public async Task 重開_工具執行中立即套用_interrupt後關程序_確認舊指令都結束_截尾_resume並以turnStart送接續訊息()
    {
        var fake = new RestartFake(temp, "restart");
        using var session = fake.NewSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), fake.Settings(cli: CliIdentities.Codex("0.160.1", fileId: 1)));

        var before = await TakeUntilAsync(events, ev => ev is AgentSafePointChangedEvent);
        before[0].Should().BeOfType<AgentProcessStartedEvent>("程序的任何事件之前先發 AgentProcessStartedEvent");
        var p1 = (AgentProcessStartedEvent)before[0];
        p1.Cli.Should().Be(CliIdentities.Codex("0.160.1", fileId: 1));
        before.OfType<AgentRunningVersionEvent>().Should().Equal(new AgentRunningVersionEvent(p1.ProcessInstance, "0.160.1"));
        before.OfType<AgentToolStartedEvent>().Should().ContainSingle();
        before[^1].Should().Be(new AgentSafePointChangedEvent(ToolRunning: true, WaitingForUser: false, HasLiveBackgroundWork: false));
        session.ProcessInstance.Should().Be(p1.ProcessInstance);
        session.RunningVersion.Should().Be("0.160.1");
        session.IsAtSafePoint.Should().BeFalse("指令在執行");
        await fake.WaitForLogAsync("p1:child");

        (await session.RestartAsync(Restart(AgentRestartMode.SafePoint, fake.Settings(started: true)))).Outcome.Should().Be(AgentRestartOutcome.Deferred,
            "只在安全點重開：指令還在跑就什麼都不做");
        fake.Log().Should().NotContain(l => l.Contains("turn/interrupt"));

        const string Continuation = "（系統通知：測試的接續訊息，不是使用者拒絕）";
        var newCli = CliIdentities.Codex("0.162.0", fileId: 2);
        var result = await session.RestartAsync(Restart(AgentRestartMode.Immediate, fake.Settings(started: true, cli: newCli), Continuation));

        result.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        result.PreviousProcessInstance.Should().Be(p1.ProcessInstance);
        result.ProcessInstance.Should().NotBeNull().And.NotBe(p1.ProcessInstance);
        RestartFake.IsAlive(fake.ChildPath).Should().BeFalse("turn/interrupt 不會停執行中的指令：重開前一定要確認舊指令都已結束");
        RestartFake.IsAlive(fake.ParentPath).Should().BeFalse();

        var after = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        var exited = after.OfType<AgentProcessExitedEvent>().Single();
        exited.ProcessInstance.Should().Be(p1.ProcessInstance);
        exited.Expected.Should().BeTrue();
        var p2 = after.OfType<AgentProcessStartedEvent>().Single();
        p2.ProcessInstance.Should().Be(result.ProcessInstance);
        p2.Cli.Should().Be(newCli);
        after.OfType<AgentRunningVersionEvent>().Should().Equal(new AgentRunningVersionEvent(p2.ProcessInstance, "0.162.0"));
        after.OfType<AgentSessionStartedEvent>().Single().Detail.Should().Be("resumed");
        after.OfType<AgentTurnStartedByCliEvent>().Should().Equal(new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));
        after.OfType<AgentTurnCompletedEvent>().Should().ContainSingle("被重開打斷的那一輪不回報結束，只有接續的一輪")
            .Which.Outcome.Should().Be(AgentTurnOutcome.Completed);
        IndexOf<AgentProcessExitedEvent>(after).Should().BeLessThan(IndexOf<AgentProcessStartedEvent>(after));
        IndexOf<AgentProcessStartedEvent>(after).Should().BeLessThan(IndexOf<AgentRunningVersionEvent>(after));
        IndexOf<AgentRunningVersionEvent>(after).Should().BeLessThan(IndexOf<AgentTurnStartedByCliEvent>(after));
        IndexOf<AgentTurnStartedByCliEvent>(after).Should().BeLessThan(IndexOf<AgentTextCompletedEvent>(after), "接續一輪的事件都排在它後面");
        after.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("p2 done");

        var log = fake.Log();
        log.Should().ContainInOrder(
            "p1:start", "p1:initialize", "p1:initialized", "p1:thread/start", "p1:turn/start text=開始",
            $"p1:turn/interrupt thread={fake.ThreadId} turn=p1-turn-1",
            $"p1:backgroundTerminals/clean thread={fake.ThreadId}",
            "p1:eof",
            "p2:old-parent-alive=False old-child-alive=False",
            "p2:start", "p2:initialize", "p2:initialized",
            $"p2:thread/resume thread={fake.ThreadId} excludeTurns=True cwdOk=True rolloutEndsWithLf=True rolloutBytes=24",
            $"p2:turn/start text={Continuation}");
        log.Should().NotContain(l => l.StartsWith("p2:thread/start", StringComparison.Ordinal), "續接原本的 thread，不開新的");

        File.ReadAllText(fake.RolloutPath).Should().Be("{\"type\":\"session_meta\"}\n", "舊程序寫到一半的尾行在續接前截掉");
        var backup = Directory.GetFiles(fake.TailDirectory, $"{fake.ThreadId}-*.tail").Should().ContainSingle().Which;
        File.ReadAllText(backup).Should().Be("{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\"");

        session.IsRunning.Should().BeFalse();
        session.ProcessInstance.Should().Be(p2.ProcessInstance);
        session.RunningVersion.Should().Be("0.162.0");
        session.IsAtSafePoint.Should().BeTrue();
    }

    [Fact]
    public async Task 重開_被結束的指令在turnCompleted之後才送失敗_工具卡改成被重開中斷的已停止_保留輸出()
    {
        var fake = new RestartFake(temp, "restart_failed_item");
        using var session = fake.NewSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), fake.Settings());
        await TakeUntilAsync(events, ev => ev is AgentSafePointChangedEvent { ToolRunning: true });
        await fake.WaitForLogAsync("p1:child");

        var result = await session.RestartAsync(Restart(AgentRestartMode.Immediate, fake.Settings(started: true)));

        result.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        var after = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        var tool = after.OfType<AgentToolCompletedEvent>().Should().ContainSingle(e => e.ToolId == "exec-1").Subject;
        tool.Status.Should().Be(AgentItemStatus.Cancelled, "被重開結束的指令不是自己失敗：不要讓使用者以為指令失敗");
        tool.Output.Should().Be("partial output\n\n" + CodexAppServerRules.InterruptedToolOutput, "保留指令的輸出，後面註明被重開中斷、結果未知");
        IndexOf<AgentToolCompletedEvent>(after).Should().BeLessThan(IndexOf<AgentProcessStartedEvent>(after), "舊程序的事件都在新程序之前");
        after.OfType<AgentTurnCompletedEvent>().Should().ContainSingle().Which.Outcome.Should().Be(AgentTurnOutcome.Completed, "只有接續的一輪回報結束");
    }

    [Fact]
    public async Task 重開_interrupt沒有回應_逾時改殺程序樹_待回應的核准隨舊程序作廢_新程序同號的請求是新卡()
    {
        var fake = new RestartFake(temp, "restart_kill", AgentPermissionMode.Ask);
        using var session = fake.NewSession(interruptTimeout: TimeSpan.FromSeconds(1));
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), fake.Settings());

        var before = await TakeUntilAsync(events, ev => ev is AgentSafePointChangedEvent { WaitingForUser: true });
        var p1 = before.OfType<AgentProcessStartedEvent>().Single();
        before.OfType<AgentApprovalEvent>().Single().RequestKey.Should().Be("0");
        before[^1].Should().Be(new AgentSafePointChangedEvent(ToolRunning: true, WaitingForUser: true, HasLiveBackgroundWork: false));
        (await session.RestartAsync(Restart(AgentRestartMode.SafePoint, fake.Settings(started: true)))).Outcome.Should().Be(AgentRestartOutcome.Deferred,
            "等使用者回應時不打斷");

        var watch = Stopwatch.StartNew();
        var result = await session.RestartAsync(Restart(AgentRestartMode.Immediate, fake.Settings(started: true), "接續"));
        watch.Stop();

        result.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        result.PreviousProcessInstance.Should().Be(p1.ProcessInstance);
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.8), "先等 turn/interrupt（1 秒），關 stdin 也不結束再等 1 秒才殺程序樹");
        RestartFake.IsAlive(fake.ParentPath).Should().BeFalse("不理 EOF 的舊程序要被結束");
        RestartFake.IsAlive(fake.ChildPath).Should().BeFalse();

        // 舊卡與新程序的請求同號（request id 每個程序從 0 數）：舊卡的回應不能送到新程序
        await session.RespondApprovalAsync("0", AgentApprovalChoice.Deny, null);

        var after = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent, async ev =>
        {
            if (ev is AgentApprovalEvent approval)
                await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        });

        var exitAt = IndexOf<AgentProcessExitedEvent>(after, e => e.ProcessInstance == p1.ProcessInstance);
        exitAt.Should().BeGreaterThanOrEqualTo(0, "直接殺時 Codex 不會撤回待核准的請求：靠舊實例的結束事件讓中樞作廢它的卡片");
        ((AgentProcessExitedEvent)after[exitAt]).Expected.Should().BeTrue();
        after.Skip(exitAt).OfType<AgentSafePointChangedEvent>().First().WaitingForUser.Should().BeFalse("舊程序的待回應請求隨它結束而作廢");
        var p2At = IndexOf<AgentProcessStartedEvent>(after);
        p2At.Should().BeGreaterThan(exitAt);
        var newCard = after.OfType<AgentApprovalEvent>().Single();
        newCard.RequestKey.Should().Be("0");
        after.IndexOf(newCard).Should().BeGreaterThan(p2At, "新程序的請求在新實例的 AgentProcessStartedEvent 之後：中樞把它記成新實例的卡片");
        after.OfType<AgentTurnCompletedEvent>().Should().ContainSingle().Which.Outcome.Should().Be(AgentTurnOutcome.Completed);
        after.Take(IndexOf<AgentTurnStartedByCliEvent>(after)).OfType<AgentTurnCompletedEvent>().Should().BeEmpty("被打斷的那一輪不回報結束");

        var log = fake.Log();
        log.Should().ContainInOrder($"p1:turn/interrupt thread={fake.ThreadId} turn=p1-turn-1", "p1:eof", "p2:old-parent-alive=False old-child-alive=False");
        log.Should().Contain(l => l.StartsWith("p2:thread/resume", StringComparison.Ordinal) && l.Contains("rolloutEndsWithLf=True"), "被殺時寫到一半的 rollout 尾行已截掉");
        log.Should().Contain("p2:approval 0=accept");
        log.Should().NotContain(l => l.StartsWith("p2:response id=0 decision=cancel", StringComparison.Ordinal), "舊卡的拒絕沒有送到新程序");
        log.Should().NotContain(l => l.StartsWith("p1:response", StringComparison.Ordinal), "沒有人回應舊程序的核准");
    }

    [Fact]
    public async Task 安全點_工具執行中與等核准都不算_工具結束後在安全點重開並接續()
    {
        var fake = new RestartFake(temp, "safepoint", AgentPermissionMode.Ask);
        using var session = fake.NewSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), fake.Settings());

        var waiting = await TakeUntilAsync(events, ev => ev is AgentSafePointChangedEvent { WaitingForUser: true });
        waiting[^1].Should().Be(new AgentSafePointChangedEvent(ToolRunning: true, WaitingForUser: true, HasLiveBackgroundWork: false));
        session.IsAtSafePoint.Should().BeFalse();
        (await session.RestartAsync(Restart(AgentRestartMode.SafePoint, fake.Settings(started: true)))).Outcome.Should().Be(AgentRestartOutcome.Deferred);

        await session.RespondApprovalAsync("0", AgentApprovalChoice.Allow, null);
        var safe = await TakeUntilAsync(events, ev => ev is AgentSafePointChangedEvent { IsAtSafePoint: true });
        IndexOf<AgentToolCompletedEvent>(safe).Should().BeGreaterThanOrEqualTo(0).And.BeLessThan(safe.Count - 1, "工具結束的事件先到，中樞再依安全點重開");
        session.IsAtSafePoint.Should().BeTrue();
        session.IsRunning.Should().BeTrue("這一輪還在執行（安全點＝執行中且沒有工具、沒有待回應的請求）");

        var result = await session.RestartAsync(Restart(AgentRestartMode.SafePoint, fake.Settings(started: true), "接續"));
        result.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        var after = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        after.OfType<AgentTurnCompletedEvent>().Should().ContainSingle().Which.Outcome.Should().Be(AgentTurnOutcome.Completed);
        IndexOf<AgentTurnStartedByCliEvent>(after).Should().BeLessThan(IndexOf<AgentTurnCompletedEvent>(after));
        fake.Log().Should().ContainInOrder("p1:approval 0=accept", $"p1:turn/interrupt thread={fake.ThreadId} turn=p1-turn-1", "p2:turn/start text=接續");
    }

    [Fact]
    public async Task 重開_只停下時回Stopped_停下待接續的一輪之後帶設定再呼叫才續接()
    {
        var fake = new RestartFake(temp, "restart");
        using var session = fake.NewSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), fake.Settings());
        var before = await TakeUntilAsync(events, ev => ev is AgentToolStartedEvent);
        var p1 = before.OfType<AgentProcessStartedEvent>().Single();
        await fake.WaitForLogAsync("p1:child");

        var stopped = await session.RestartAsync(Restart(AgentRestartMode.Immediate, settings: null));

        stopped.Outcome.Should().Be(AgentRestartOutcome.Stopped);
        stopped.PreviousProcessInstance.Should().Be(p1.ProcessInstance);
        stopped.ProcessInstance.Should().BeNull();
        session.HasLiveProcess.Should().BeFalse();
        session.ProcessInstance.Should().BeNull();
        session.RunningVersion.Should().BeNull();
        session.IsRunning.Should().BeFalse("停下待接續的一輪交給中樞（等 CLI 更新），使用者改送別的訊息也能照常送出");
        RestartFake.IsAlive(fake.ChildPath).Should().BeFalse();
        await Task.Delay(300);
        events.ToArray().OfType<AgentTurnCompletedEvent>().Should().BeEmpty("被打斷的那一輪不回報結束");
        fake.Log().Should().NotContain(l => l.StartsWith("p2:", StringComparison.Ordinal), "只停下、不啟動新程序");

        var resumed = await session.RestartAsync(Restart(AgentRestartMode.SafePoint, fake.Settings(started: true), "接續"));

        resumed.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        resumed.PreviousProcessInstance.Should().BeNull("這次沒有停下任何程序");
        var after = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        after.OfType<AgentProcessStartedEvent>().Single().ProcessInstance.Should().Be(resumed.ProcessInstance);
        after.OfType<AgentTurnCompletedEvent>().Should().ContainSingle().Which.Outcome.Should().Be(AgentTurnOutcome.Completed);
        fake.Log().Should().ContainInOrder($"p2:thread/resume thread={fake.ThreadId} excludeTurns=True cwdOk=True rolloutEndsWithLf=True rolloutBytes=24", "p2:turn/start text=接續");

        (await session.RestartAsync(Restart(AgentRestartMode.SafePoint, fake.Settings(started: true)))).Outcome.Should().Be(AgentRestartOutcome.Restarted,
            "停下待接續的一輪已用掉：閒置、程序活著時只關程序");
        session.HasLiveProcess.Should().BeFalse();
    }

    [Fact]
    public async Task 重開_不自動接續時只關程序_回Restarted_不啟動新程序()
    {
        var fake = new RestartFake(temp, "restart");
        using var session = fake.NewSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), fake.Settings());
        await TakeUntilAsync(events, ev => ev is AgentToolStartedEvent);
        await fake.WaitForLogAsync("p1:child");

        var result = await session.RestartAsync(Restart(AgentRestartMode.Immediate, fake.Settings(started: true), autoContinue: false));

        result.Outcome.Should().Be(AgentRestartOutcome.Restarted);
        result.PreviousProcessInstance.Should().NotBeNull();
        result.ProcessInstance.Should().BeNull();
        session.IsRunning.Should().BeFalse("被打斷的那一輪交給中樞收尾");
        session.HasLiveProcess.Should().BeFalse();
        await Task.Delay(300);
        events.ToArray().OfType<AgentTurnCompletedEvent>().Should().BeEmpty();
        fake.Log().Should().NotContain(l => l.StartsWith("p2:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 重開途中按停止_不接續()
    {
        var fake = new RestartFake(temp, "restart");
        using var session = fake.NewSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), fake.Settings());
        await TakeUntilAsync(events, ev => ev is AgentToolStartedEvent);
        await fake.WaitForLogAsync("p1:child");

        var restarting = session.RestartAsync(Restart(AgentRestartMode.Immediate, fake.Settings(started: true)));
        await session.InterruptAsync();
        var result = await restarting;

        result.Outcome.Should().Be(AgentRestartOutcome.Restarted);
        result.ProcessInstance.Should().BeNull();
        session.IsRunning.Should().BeFalse();
        await Task.Delay(300);
        events.ToArray().OfType<AgentTurnCompletedEvent>().Should().BeEmpty("被打斷的那一輪由中樞收尾");
        fake.Log().Should().NotContain(l => l.StartsWith("p2:", StringComparison.Ordinal));
        (await session.RestartAsync(Restart(AgentRestartMode.Immediate, fake.Settings(started: true)))).Outcome.Should().Be(AgentRestartOutcome.Restarted,
            "沒有程序、也沒有停下待接續的一輪");
    }

    [Fact]
    public async Task 重開_這一輪在中斷生效前自己完成_照常回報結束且不接續()
    {
        var fake = new RestartFake(temp, "finish_before_interrupt");
        using var session = fake.NewSession();
        var events = Collect(session);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), fake.Settings());
        await TakeUntilAsync(events, ev => ev is AgentToolStartedEvent);
        await fake.WaitForLogAsync("p1:child");

        var result = await session.RestartAsync(Restart(AgentRestartMode.Immediate, fake.Settings(started: true)));

        result.Outcome.Should().Be(AgentRestartOutcome.Restarted, "上一輪已完成：送接續只會白耗一輪");
        var turn = await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        turn.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        turn.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("done-on-its-own");
        fake.Log().Should().NotContain(l => l.StartsWith("p2:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 重開_沒有程序也沒有停下待接續的一輪_什麼都不做()
    {
        using var session = new CodexAgentSession();
        session.IsAtSafePoint.Should().BeTrue("閒置、沒有背景工作");
        session.HasLiveBackgroundWork.Should().BeFalse();
        session.RunningVersion.Should().BeNull();
        session.ProcessInstance.Should().BeNull();

        var result = await session.RestartAsync(Restart(AgentRestartMode.Immediate, settings: null));

        result.Should().Be(new AgentRestartResult(AgentRestartOutcome.Restarted), "下次送出自然用新版");
        session.HasLiveProcess.Should().BeFalse();
    }

    [Fact]
    public async Task 執行檔身分換了才換程序_身分不明不換_新程序回報新版本()
    {
        var fake = new RestartFake(temp, "multi_turn");
        using var session = fake.NewSession();
        var events = Collect(session);
        var v160 = CliIdentities.Codex("0.160.1", fileId: 1);

        async Task<List<AgentEvent>> TurnAsync(AgentSessionSettings settings)
        {
            await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), settings);
            return await TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        }

        var first = await TurnAsync(fake.Settings(cli: v160));
        first[0].Should().BeOfType<AgentProcessStartedEvent>();
        var p1 = (AgentProcessStartedEvent)first[0];
        first.OfType<AgentRunningVersionEvent>().Should().Equal(new AgentRunningVersionEvent(p1.ProcessInstance, "0.160.1"));

        (await TurnAsync(fake.Settings(started: true, cli: v160))).OfType<AgentProcessStartedEvent>().Should().BeEmpty("同一份執行檔");
        (await TurnAsync(fake.Settings(started: true, cli: null))).OfType<AgentProcessStartedEvent>().Should().BeEmpty("身分不明不構成重開的理由");
        (await TurnAsync(fake.Settings(started: true, cli: v160 with { Version = "0.162.0" }))).OfType<AgentProcessStartedEvent>().Should().BeEmpty(
            "版本只是顯示資訊：同一份檔案不算換版");

        var v162 = CliIdentities.Codex("0.162.0", fileId: 2);
        var switched = await TurnAsync(fake.Settings(started: true, cli: v162));
        var p2 = switched.OfType<AgentProcessStartedEvent>().Single();
        p2.Cli.Should().Be(v162);
        p2.ProcessInstance.Should().NotBe(p1.ProcessInstance);
        switched.OfType<AgentRunningVersionEvent>().Should().Equal(new AgentRunningVersionEvent(p2.ProcessInstance, "0.162.0"));
        session.RunningVersion.Should().Be("0.162.0");
        fake.Log().Should().Contain(l => l.StartsWith($"p2:thread/resume thread={fake.ThreadId}", StringComparison.Ordinal), "換程序後續接同一個 thread");
    }
}
