using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// CLI 熱更新的 Claude 會話重開（WP1.2），以假的 claude -p（Windows PowerShell 腳本，照 E-10／E-11 實測的事件順序）測，不連網、不耗額度：
/// 安全點的追蹤與事件、殺樹重開時收到的參數與環境變數（RESUME 變數只在系統重開時帶、一律關閉自動更新）、自動接續那一輪當成執行中、
/// 被打斷的一輪不發 TurnCompleted、只停下（Settings=null）、沒有接續時的備案與關閉、重開途中按停止、立即套用先停背景工作。
/// 假 CLI 把逐字稿寫到 FAKE_TRANSCRIPT（比 stdout 晚寫入工具結果、最後留半行，驗證重開前會等它寫入並截掉半行），
/// 以回覆文字（JSON）回報自己收到的參數與 RESUME 環境變數。
/// </summary>
public abstract class ClaudeSessionRestartTestsBase : IDisposable
{
    private protected static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private protected const string Continuation = "（系統通知：測試用的接續訊息，不是使用者拒絕。）";

    /// <summary>假的 claude -p：腳本只用 ASCII（Windows PowerShell 讀沒有 BOM 的腳本用系統碼頁）</summary>
    private protected const string FakeClaude = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $argList = @($args)
        $scenario = $env:FAKE_SCENARIO
        $transcript = $env:FAKE_TRANSCRIPT
        $version = $env:FAKE_VERSION
        if (-not $version) { $version = '2.1.296' }
        $lf = [string][char]10

        function Emit([string]$json) { $out.WriteLine($json) }
        function Js([string]$text) { return (ConvertTo-Json -InputObject $text -Compress) }
        function ArgValue([string]$name) {
            $i = [array]::IndexOf($argList, $name)
            if ($i -ge 0 -and $i + 1 -lt $argList.Count) { return $argList[$i + 1] }
            return $null
        }
        function Record([string]$row) {
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($transcript))
            [IO.File]::AppendAllText($transcript, $row, $utf8)
        }

        $resume = ArgValue '--resume'
        if ($resume -and $resume.EndsWith('.jsonl')) { $sessionId = [IO.Path]::GetFileNameWithoutExtension($resume) }
        elseif ($resume) { $sessionId = $resume }
        else { $sessionId = ArgValue '--session-id' }
        $autoResume = ($resume -and $env:CLAUDE_CODE_RESUME_INTERRUPTED_TURN -eq '1')
        $fresh = -not $resume

        function Report([string]$text) {
            return (ConvertTo-Json -Compress -InputObject ([ordered]@{
                resume = $resume
                text = $text
                turn = $env:CLAUDE_CODE_RESUME_INTERRUPTED_TURN
                maxAge = $env:CLAUDE_CODE_RESUME_INTERRUPTED_TURN_MAX_AGE_MS
                prompt = $env:CLAUDE_CODE_RESUME_PROMPT
                reason = $env:CLAUDE_CODE_RESUME_REASON
                noUpdate = $env:DISABLE_AUTOUPDATER
                version = $version
            }))
        }

        function HandleControl($msg) {
            if ($msg.request.subtype -eq 'stop_task') {
                $task = [string]$msg.request.task_id
                if ($env:FAKE_MARKERS) { [IO.File]::WriteAllText((Join-Path $env:FAKE_MARKERS ('stop_task-' + $task)), 'x') }
                Emit ('{"type":"system","subtype":"task_updated","task_id":"' + $task + '","patch":{"status":"killed"}}')
                Emit ('{"type":"system","subtype":"task_notification","task_id":"' + $task + '","status":"stopped","summary":"stopped"}')
                Emit '{"type":"system","subtype":"background_tasks_changed","tasks":[]}'
            }
            Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{}}}')
        }

        function ReadMsg {
            while ($true) {
                $line = $in.ReadLine()
                if ($line -eq $null) { exit 0 }
                if ($line.Trim().Length -eq 0) { continue }
                $m = $line | ConvertFrom-Json
                if ($m.type -eq 'control_request') { HandleControl $m; continue }
                return $m
            }
        }

        function Init { Emit ('{"type":"system","subtype":"init","session_id":"' + $sessionId + '","model":"fake","permissionMode":"default","apiKeySource":"none","claude_code_version":"' + $version + '"}') }
        function Requesting { Emit ('{"type":"system","subtype":"status","status":"requesting","session_id":"' + $sessionId + '"}') }
        function BeginMessage([string]$id) { Emit ('{"type":"stream_event","event":{"type":"message_start","message":{"id":"' + $id + '"}},"parent_tool_use_id":null}') }
        function StopReason([string]$reason) {
            Emit ('{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"' + $reason + '"}},"parent_tool_use_id":null}')
            Emit '{"type":"stream_event","event":{"type":"message_stop"},"parent_tool_use_id":null}'
        }
        function Say([string]$id, [string]$text) {
            BeginMessage $id
            Emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}'
            Emit ('{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":' + (Js $text) + '}},"parent_tool_use_id":null}')
            Emit '{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}'
            StopReason 'end_turn'
        }
        function Done([string]$reason) {
            $extra = ''
            if ($reason) { $extra = ',"resume_reason":"' + $reason + '"' }
            Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"done","session_id":"' + $sessionId + '"' + $extra + '}')
        }
        function ToolUse([string]$msgId, [string]$toolId) {
            BeginMessage $msgId
            Emit ('{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"' + $toolId + '","name":"Bash","input":{}}},"parent_tool_use_id":null}')
            Emit ('{"type":"assistant","message":{"id":"' + $msgId + '","content":[{"type":"tool_use","id":"' + $toolId + '","name":"Bash","input":{"command":"echo hi"}}]},"parent_tool_use_id":null}')
            Emit '{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}'
            StopReason 'tool_use'
            Record ('{"type":"assistant","message":{"content":[{"type":"tool_use","id":"' + $toolId + '","name":"Bash"}]}}' + $lf)
        }
        function ToolResult([string]$toolId) {
            Emit ('{"type":"user","message":{"role":"user","content":[{"tool_use_id":"' + $toolId + '","type":"tool_result","content":"hi","is_error":false}]},"parent_tool_use_id":null}')
        }
        function ToolResultRow([string]$toolId) { return ('{"type":"user","message":{"role":"user","content":[{"tool_use_id":"' + $toolId + '","type":"tool_result","content":"hi"}]}}' + $lf) }

        # The interrupted turn of the first process
        function FirstTurn {
            if ($scenario -ne 'lagging') { Record ('{"type":"user","message":{"role":"user","content":"start"}}' + $lf) }
            Init
            Requesting
            switch ($scenario) {
                'lagging' {
                    # streaming text; the transcript never catches up (the restart waits for it)
                    BeginMessage 'msg_l1'
                    Emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}'
                    Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"lagging"}},"parent_tool_use_id":null}'
                }
                'bgturn' {
                    Emit '{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"bg1","task_type":"local_bash","description":"sleep"}]}'
                    Emit '{"type":"system","subtype":"task_started","task_id":"bg1","tool_use_id":"tu_bg","description":"sleep","is_backgrounded":true,"task_type":"local_bash"}'
                    Say 'msg_b1' 'started'
                    Record ('{"type":"assistant","message":{"content":[{"type":"text","text":"started"}]}}' + $lf)
                    Done ''
                    Start-Sleep -Milliseconds 400
                    # E-11: the background task finishes while idle and the CLI starts a turn of its own
                    Emit '{"type":"system","subtype":"task_notification","task_id":"bg1","status":"completed","summary":"done"}'
                    Emit '{"type":"system","subtype":"background_tasks_changed","tasks":[]}'
                    Init
                    Requesting
                    BeginMessage 'msg_own'
                    Emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}'
                    Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"notification-reply"}},"parent_tool_use_id":null}'
                    # the user sends a message while this turn is streaming: the CLI queues it and handles it after this turn
                    $queued = ReadMsg
                    Emit '{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}'
                    StopReason 'end_turn'
                    Record ('{"type":"assistant","message":{"content":[{"type":"text","text":"notification-reply"}]}}' + $lf)
                    Emit ('{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"","session_id":"' + $sessionId + '","origin":{"kind":"task-notification","producer":"session-task"}}')
                    if ($env:FAKE_QUEUE_GAP_MS) { Start-Sleep -Milliseconds ([int]$env:FAKE_QUEUE_GAP_MS) }
                    $qtext = ((@($queued.message.content) | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join '')
                    Record ('{"type":"user","message":{"role":"user","content":' + (Js $qtext) + '}}' + $lf)
                    Init
                    Requesting
                    Say 'msg_q1' (Report $qtext)
                    Record ('{"type":"assistant","message":{"content":[{"type":"text","text":"reply"}]}}' + $lf)
                    Done ''
                }
                'tool' {
                    ToolUse 'msg_t1' 'tu_1'
                    Start-Sleep -Milliseconds 200
                    ToolResult 'tu_1'
                    # the CLI writes the transcript row later than stdout (E-10: about 0.17 s); leave a half line at the end
                    Start-Sleep -Milliseconds 500
                    Record ((ToolResultRow 'tu_1') + '{"type":"assistant","partial')
                    Requesting
                }
                'text' {
                    BeginMessage 'msg_x1'
                    Emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}'
                    Emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"partial-answer"}},"parent_tool_use_id":null}'
                }
                'done' {
                    Say 'msg_d1' 'full-answer'
                    Record ('{"type":"assistant","message":{"content":[{"type":"text","text":"full-answer"}]}}' + $lf)
                }
                'safepoint' {
                    ToolUse 'msg_s1' 'tu_s'
                    Emit '{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"echo hi"},"tool_use_id":"tu_s"}}'
                    while ($true) { $m = ReadMsg; if ($m.type -eq 'control_response' -and $m.response.request_id -eq 'r1') { break } }
                    ToolResult 'tu_s'
                    Record (ToolResultRow 'tu_s')
                    Emit '{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"bg1","task_type":"local_bash","description":"sleep"}]}'
                    Emit '{"type":"system","subtype":"task_started","task_id":"bg1","tool_use_id":"tu_bg","description":"sleep","is_backgrounded":true,"task_type":"local_bash"}'
                    Say 'msg_s2' 'started'
                    Done ''
                }
                default {
                    Say 'msg_n1' (Report 'first')
                    Done ''
                }
            }
        }

        # Restarted by the app with the RESUME variables: the CLI resumes the interrupted turn by itself
        if ($autoResume) {
            if ($env:FAKE_RESUME_DELAY_MS) { Start-Sleep -Milliseconds ([int]$env:FAKE_RESUME_DELAY_MS) }
            if ($env:FAKE_RESUME_FAIL -eq '1') {
                [Console]::Error.WriteLine('No conversation found with session ID: ' + $sessionId)
                Emit ('{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":0,"errors":["No conversation found with session ID: ' + $sessionId + '"]}')
                exit 1
            }
            if ($scenario -ne 'done') {
                Emit ('{"type":"command_lifecycle","command_uuid":"c1","state":"started","session_id":"' + $sessionId + '"}')
                $first = $in.ReadLine()
                if ($first) { $m = $first | ConvertFrom-Json; if ($m.type -eq 'control_request') { HandleControl $m } }
                Record ('{"type":"user","isMeta":true,"message":{"role":"user","content":' + (Js $env:CLAUDE_CODE_RESUME_PROMPT) + '}}' + $lf)
                Init
                Requesting
                Start-Sleep -Milliseconds 1500
                Say 'msg_r1' (Report 'auto')
                Done $env:CLAUDE_CODE_RESUME_REASON
                Emit '{"type":"command_lifecycle","command_uuid":"c1","state":"completed"}'
            }
        }

        while ($true) {
            $m = ReadMsg
            if ($m.type -ne 'user') { continue }
            $text = ((@($m.message.content) | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join '')
            if (-not $resume) {
                $resume = '(new)'
                FirstTurn
                continue
            }
            Record ('{"type":"user","message":{"role":"user","content":"next"}}' + $lf)
            Init
            Requesting
            # 'slow': the API request is sent but the first token takes long (only in the first process)
            if ($scenario -eq 'slow' -and $fresh) { Start-Sleep -Milliseconds 20000 }
            Say ('msg_' + [guid]::NewGuid().ToString('N')) (Report $text)
            Done ''
        }
        """";

    private protected readonly TempDirectory temp = new("fake-claude-restart");
    private protected readonly string script;
    private protected readonly string work;
    private protected readonly string config;
    private protected readonly string markers;
    private protected readonly string sessionId = Guid.NewGuid().ToString();

    protected ClaudeSessionRestartTestsBase()
    {
        script = temp.WriteFile("fake-claude.ps1", FakeClaude);
        work = temp.CreateDirectory("work");
        config = temp.CreateDirectory("cfg");
        markers = temp.CreateDirectory("markers");
    }

    public void Dispose() => temp.Dispose();

    /// <summary>CLI 放這個 session 逐字稿的位置（假 CLI 也寫在這裡）</summary>
    private protected string Transcript => ClaudeStreamRules.TranscriptPath(config, sessionId, work);

    private protected AgentSessionSettings Settings(string scenario, bool started = false, string version = "2.1.296", CliBinaryIdentity? cli = null,
        IReadOnlyDictionary<string, string>? env = null)
        => new(
            sessionId,
            started,
            new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            "fake",
            e =>
            {
                e["FAKE_SCENARIO"] = scenario;
                e["FAKE_TRANSCRIPT"] = Transcript;
                e["FAKE_VERSION"] = version;
                e["FAKE_MARKERS"] = markers;
                e["CLAUDE_CONFIG_DIR"] = config;
                foreach (var (key, value) in env ?? new Dictionary<string, string>())
                    e[key] = value;
            },
            null,
            null,
            null,
            AgentPermissionMode.Ask,
            work,
            temp.Root,
            "測試說明",
            UseLocalCustomizations: true,
            Cli: cli);

    private protected static AgentRestartRequest Request(AgentRestartMode mode, AgentSessionSettings? settings, bool autoContinue = true,
        AgentRestartReason reason = AgentRestartReason.CliUpdated)
        => new(reason, mode, "2.1.296", "2.1.297", autoContinue, Continuation, Array.Empty<AgentInterruptedTool>(), settings);

    private protected static AgentTurnInput Input(string text) => new(text, Array.Empty<AgentAttachment>());

    /// <summary>假 CLI 回覆裡的回報（參數與環境變數）</summary>
    private protected static JObject Report(IEnumerable<AgentEvent> events) => JObject.Parse(events.OfType<AgentTextCompletedEvent>().Last().Text);

    /// <summary>會話事件的收集器（事件在程序讀取執行緒發出）</summary>
    private protected sealed class EventLog : IDisposable
    {
        private readonly IAgentSession session;
        private readonly BlockingCollection<AgentEvent> queue = new();
        private readonly Action<AgentEvent> handler;

        public EventLog(IAgentSession session)
        {
            this.session = session;
            handler = queue.Add;
            session.EventReceived += handler;
        }

        public List<AgentEvent> All { get; } = new();

        public async Task<List<AgentEvent>> UntilAsync(Func<AgentEvent, bool> until, double seconds = 30)
        {
            var collected = new List<AgentEvent>();
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!queue.TryTake(out var ev, 100))
                {
                    await Task.Yield();
                    continue;
                }
                collected.Add(ev);
                All.Add(ev);
                if (until(ev))
                    return collected;
            }
            throw new TimeoutException("假的 CLI 沒有在時限內完成；收到：" + string.Join("、", collected.Select(e => e.GetType().Name)));
        }

        public List<AgentEvent> Drain(int waitMilliseconds)
        {
            var result = new List<AgentEvent>();
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                if (queue.TryTake(out var ev, 50))
                {
                    result.Add(ev);
                    All.Add(ev);
                }
            }
            return result;
        }

        public void Dispose() => session.EventReceived -= handler;
    }
}

// 測試分成幾個 class：xUnit 同一個 class 的測試依序跑，分開才能同時跑
public sealed class ClaudeSessionRestartTests : ClaudeSessionRestartTestsBase
{
    [Fact]
    public async Task 沒有程序時_重開什麼都不做回Restarted_閒置算安全點()
    {
        using var session = new ClaudeAgentSession();

        session.RunningVersion.Should().BeNull();
        session.ProcessInstance.Should().BeNull();
        session.HasLiveBackgroundWork.Should().BeFalse();
        session.IsAtSafePoint.Should().BeTrue("閒置、沒有背景工作");
        (await session.RestartAsync(Request(AgentRestartMode.Immediate, null, reason: AgentRestartReason.UserApplyNow)))
            .Should().Be(new AgentRestartResult(AgentRestartOutcome.Restarted), "沒有程序也沒有停下待接續的一輪：下次送出自然用新版");
        (await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("normal")))).Outcome.Should().Be(AgentRestartOutcome.Restarted);
        session.HasLiveProcess.Should().BeFalse("不能因此啟動程序");
    }

    [Fact]
    public async Task 安全點_工具結果寫進逐字稿後殺樹重開_以RESUME環境變數自動接續_那一輪當成執行中_被打斷的一輪不發TurnCompleted()
    {
        var oldCli = CliIdentities.Claude("2.1.296", fileId: 1);
        var newCli = CliIdentities.Claude("2.1.297", fileId: 2);
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);

        await session.SendAsync(Input("開始"), Settings("tool", cli: oldCli));
        // 工具結果回來後的第一個安全點（中樞在這時候重試 RestartAsync）
        var first = await log.UntilAsync(e => e is AgentSafePointChangedEvent { IsAtSafePoint: true });
        var oldStart = first.OfType<AgentProcessStartedEvent>().Single();
        oldStart.Cli.Should().Be(oldCli, "啟動用的身分");
        first.IndexOf(oldStart).Should().Be(0, "在讀這個程序的輸出之前發");
        first.OfType<AgentRunningVersionEvent>().Single().Should().Be(new AgentRunningVersionEvent(oldStart.ProcessInstance, "2.1.296"));
        first.OfType<AgentSafePointChangedEvent>().Select(e => e.ToolRunning).Should().Equal(true, false);
        session.ProcessInstance.Should().Be(oldStart.ProcessInstance);
        session.RunningVersion.Should().Be("2.1.296");
        session.IsAtSafePoint.Should().BeTrue("工具已有結果、沒有等使用者回應");

        var result = await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("tool", started: true, version: "2.1.297", cli: newCli)));

        result.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        result.PreviousProcessInstance.Should().Be(oldStart.ProcessInstance);
        result.ProcessInstance.Should().NotBeNull().And.NotBe(oldStart.ProcessInstance);
        session.IsRunning.Should().BeTrue("自動接續的一輪不是 SendAsync 開始的，一樣算執行中");
        session.ProcessInstance.Should().Be(result.ProcessInstance);

        var second = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        var all = log.All;
        var newStart = all.OfType<AgentProcessStartedEvent>().Last();
        newStart.Should().Be(new AgentProcessStartedEvent(result.ProcessInstance!, newCli));
        var autoTurn = all.OfType<AgentTurnStartedByCliEvent>().Single();
        autoTurn.Kind.Should().Be(AgentAutoTurnKind.ResumeInterruptedTurn);
        all.IndexOf(autoTurn).Should().Be(all.IndexOf(newStart) + 1, "新程序一啟動就標成執行中（接續的一輪比 initialize 回應還早開始）");
        all.OfType<AgentRunningVersionEvent>().Last().Should().Be(new AgentRunningVersionEvent(result.ProcessInstance!, "2.1.297"));
        all.IndexOf(all.OfType<AgentRunningVersionEvent>().Last()).Should().BeGreaterThan(all.IndexOf(autoTurn));
        all.OfType<AgentTurnCompletedEvent>().Should().ContainSingle("被打斷的那一輪不發 TurnCompleted，接續的一輪照常結束")
            .Which.Outcome.Should().Be(AgentTurnOutcome.Completed);
        all.OfType<AgentProcessExitedEvent>().Should().NotContain(e => e.ProcessInstance == oldStart.ProcessInstance, "舊程序是重開時卸下的");

        var report = Report(second);
        report.Value<string>("resume").Should().Be(sessionId, "以 --resume 續接同一個 session");
        report.Value<string>("turn").Should().Be("1");
        report.Value<string>("maxAge").Should().Be("3600000");
        report.Value<string>("prompt").Should().Be(Continuation);
        report.Value<string>("reason").Should().Be(ClaudeStreamRules.AppResumeReason);
        report.Value<string>("noUpdate").Should().Be("1");
        session.LastResumeReason.Should().Be("myriagent_cli_update", "接續那一輪的 result 帶 resume_reason");
        session.IsRunning.Should().BeFalse();
        session.RunningVersion.Should().Be("2.1.297");

        var bytes = File.ReadAllBytes(Transcript);
        bytes[^1].Should().Be((byte)'\n', "重開前截掉了舊程序寫到一半的尾行");
        File.ReadAllText(Transcript).Should().NotContain("\"partial").And.Contain("tool_result", "工具結果在殺樹之前就寫進逐字稿了");
    }

    [Fact]
    public async Task 只停下_回Stopped_串流到一半的回覆結束為已中斷_之後帶設定再呼叫一次才接續()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("text"));
        await log.UntilAsync(e => e is AgentTextDeltaEvent);

        var stop = await session.RestartAsync(Request(AgentRestartMode.SafePoint, settings: null));

        stop.Outcome.Should().Be(AgentRestartOutcome.Stopped);
        stop.PreviousProcessInstance.Should().NotBeNull();
        stop.ProcessInstance.Should().BeNull();
        session.HasLiveProcess.Should().BeFalse();
        session.ProcessInstance.Should().BeNull();
        session.RunningVersion.Should().BeNull("沒有程序");
        session.IsRunning.Should().BeFalse("停下待接續：沒有程序在跑");
        log.Drain(300);
        log.All.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("partial-answer\n\n" + ClaudeStreamRules.InterruptedTextNote,
            "接續那一輪 CLI 會從頭重寫，舊的這段結束為已中斷，不和新的接在一起");
        log.All.OfType<AgentTurnCompletedEvent>().Should().BeEmpty("被打斷的那一輪不發 TurnCompleted（中樞照「等待 CLI 更新」顯示）");

        var resumed = await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("text", started: true, version: "2.1.297")));

        resumed.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued, "停下待接續的一輪由帶設定的那一次用掉");
        resumed.PreviousProcessInstance.Should().BeNull("停下時已經沒有程序");
        var done = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        Report(done).Value<string>("reason").Should().Be("myriagent_cli_update");
        (await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("text", started: true, version: "2.1.297"))))
            .Outcome.Should().Be(AgentRestartOutcome.Restarted, "已經用掉了：上一輪已完成，只關程序");
        session.HasLiveProcess.Should().BeFalse();
    }

    [Fact]
    public async Task 新版沒有自動接續而這一輪還在途中_改經stdin送接續訊息()
    {
        // RESUME 環境變數在某版失效的備案（測試以不帶變數模擬）：殺樹、--resume、由宿主送接續訊息
        using var session = new ClaudeAgentSession { UseResumeEnvironment = false, AutoTurnGrace = TimeSpan.FromSeconds(1) };
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("tool"));
        await log.UntilAsync(e => e is AgentSafePointChangedEvent { IsAtSafePoint: true });

        var result = await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("tool", started: true)));

        result.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);
        var done = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        log.All.OfType<AgentTurnStartedByCliEvent>().Select(e => e.Kind).Should().Equal(AgentAutoTurnKind.ResumeInterruptedTurn, AgentAutoTurnKind.Continuation);
        var report = Report(done);
        report.Value<string>("text").Should().Be(Continuation, "接續訊息經 stdin 送出（不顯示成使用者訊息）");
        report.Value<string>("turn").Should().BeNull();
        report.Value<string>("resume").Should().Be(sessionId);
        log.All.OfType<AgentTurnCompletedEvent>().Should().ContainSingle();
    }

    [Fact]
    public async Task 新版沒有自動接續而上一輪其實已經回覆完_關掉新程序回Restarted()
    {
        using var session = new ClaudeAgentSession { AutoTurnGrace = TimeSpan.FromSeconds(1) };
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("done"));
        await log.UntilAsync(e => e is AgentTextCompletedEvent);
        log.Drain(300);

        var result = await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("done", started: true)));

        result.Outcome.Should().Be(AgentRestartOutcome.Restarted, "回覆已經寫進逐字稿（end_turn）：CLI 不接續，送接續訊息只會白耗一輪");
        result.ProcessInstance.Should().BeNull();
        session.HasLiveProcess.Should().BeFalse();
        session.IsRunning.Should().BeFalse("被打斷的那一輪由中樞收尾");
        log.Drain(500).OfType<AgentTurnCompletedEvent>().Should().BeEmpty();
        log.All.OfType<AgentTurnStartedByCliEvent>().Select(e => e.Kind).Should().Equal(AgentAutoTurnKind.ResumeInterruptedTurn);
    }

    [Fact]
    public async Task 新版找不到Session_回Failed且不當成這一輪結束()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("text"));
        await log.UntilAsync(e => e is AgentTextDeltaEvent);

        var result = await session.RestartAsync(Request(AgentRestartMode.SafePoint,
            Settings("text", started: true, env: new Dictionary<string, string> { ["FAKE_RESUME_FAIL"] = "1" })));

        result.Outcome.Should().Be(AgentRestartOutcome.Failed);
        result.Error.Should().Contain("No conversation found");
        session.IsRunning.Should().BeFalse("回到閒置、沒有程序");
        session.HasLiveProcess.Should().BeFalse();
        log.Drain(500).OfType<AgentTurnCompletedEvent>().Should().BeEmpty("Failed 時被打斷的那一輪由中樞收尾，也不能拿使用者的訊息自動重送");
        log.All.OfType<AgentNoticeEvent>().Should().NotContain(n => n.Message.Contains("找不到原本的 Claude 對話紀錄"));
    }
}

public sealed class ClaudeSessionRestartTests2 : ClaudeSessionRestartTestsBase
{
    [Fact]
    public async Task 上一輪已完成_重開只關程序不接續_下次送出照常續接_不帶RESUME變數且關閉自動更新()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("normal"));
        var first = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        var report = Report(first);
        report.Value<string>("noUpdate").Should().Be("1", "app 啟動的 claude 一律關閉自動更新");
        report.Value<string>("turn").Should().BeNull("不是系統重開：不帶 RESUME 變數");
        report.Value<string>("prompt").Should().BeNull();
        report.Value<string>("reason").Should().BeNull();
        var instance = session.ProcessInstance;

        var result = await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("normal", started: true, version: "2.1.297")));

        result.Should().Be(new AgentRestartResult(AgentRestartOutcome.Restarted, instance), "上一輪已完成：只關程序、不送接續（沒有多耗一輪）");
        session.HasLiveProcess.Should().BeFalse();
        log.Drain(500).Should().NotContain(e => e is AgentTurnStartedByCliEvent || e is AgentTurnCompletedEvent || e is AgentProcessStartedEvent);

        await session.SendAsync(Input("下一則"), Settings("normal", started: true, version: "2.1.297"));
        var next = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        var nextReport = Report(next);
        nextReport.Value<string>("resume").Should().Be(sessionId);
        nextReport.Value<string>("text").Should().Be("下一則");
        nextReport.Value<string>("turn").Should().BeNull("使用者自己送的訊息：CLI 不能自己接續別的輪次");
        nextReport.Value<string>("noUpdate").Should().Be("1");
        next.OfType<AgentRunningVersionEvent>().Single().Version.Should().Be("2.1.297");
    }

    [Fact]
    public async Task 停止背景工作_stop_task_背景工作清單清空_程序留著_不開新的一輪_不是目前的背景工作不送()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("safepoint"));
        await log.UntilAsync(e => e is AgentApprovalEvent);
        await session.RespondApprovalAsync("r1", AgentApprovalChoice.Allow, null);
        await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        session.HasLiveBackgroundWork.Should().BeTrue();

        (await session.StopBackgroundWorkAsync("zzz")).Should().Be(new AgentBackgroundStopResult(0, "這個背景工作已經結束了"));
        File.Exists(Path.Combine(markers, "stop_task-zzz")).Should().BeFalse("只認 background_tasks_changed 目前列著的");

        (await session.StopBackgroundWorkAsync(null)).Should().Be(new AgentBackgroundStopResult(1, null), "全部：含對不到工具卡的");
        File.Exists(Path.Combine(markers, "stop_task-bg1")).Should().BeTrue();
        await log.UntilAsync(e => e is AgentBackgroundWorkEvent { Count: 0 });
        session.HasLiveBackgroundWork.Should().BeFalse();
        session.HasLiveProcess.Should().BeTrue("只停背景工作，程序留著（下一則訊息照常）");
        session.IsRunning.Should().BeFalse();
        log.Drain(300).OfType<AgentIdleTurnStartedEvent>().Should().BeEmpty("stop_task 的 task_notification stopped 不會讓 CLI 開一輪");
    }

    [Fact]
    public async Task 安全點追蹤_工具執行中與等使用者回Deferred_背景工作存活時也延後_立即套用先停背景工作再結束()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("safepoint"));
        await log.UntilAsync(e => e is AgentApprovalEvent);

        session.IsAtSafePoint.Should().BeFalse("工具在執行、等使用者核准");
        (await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("safepoint", started: true))))
            .Outcome.Should().Be(AgentRestartOutcome.Deferred);

        await session.RespondApprovalAsync("r1", AgentApprovalChoice.Allow, null);
        await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        session.IsRunning.Should().BeFalse();
        session.HasLiveBackgroundWork.Should().BeTrue("run_in_background 的工作還在跑");
        session.IsAtSafePoint.Should().BeFalse("閒置但背景工作還在：背景工作撐不過重開");
        (await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("safepoint", started: true))))
            .Outcome.Should().Be(AgentRestartOutcome.Deferred);

        var applied = await session.RestartAsync(Request(AgentRestartMode.Immediate, Settings("safepoint", started: true), reason: AgentRestartReason.UserApplyNow));

        applied.Outcome.Should().Be(AgentRestartOutcome.Restarted, "上一輪已完成：只結束程序，不接續");
        File.Exists(Path.Combine(markers, "stop_task-bg1")).Should().BeTrue("立即套用：先送 stop_task 停掉背景工作，再結束程序樹");
        session.HasLiveProcess.Should().BeFalse();
        session.HasLiveBackgroundWork.Should().BeFalse();
        session.IsAtSafePoint.Should().BeTrue();
        log.Drain(300);
        var flags = log.All.OfType<AgentSafePointChangedEvent>().Select(e => (e.ToolRunning, e.WaitingForUser, e.HasLiveBackgroundWork)).ToList();
        flags.Should().StartWith(new[] { (true, false, false), (true, true, false), (true, false, false), (false, false, false), (false, false, true) },
            "tool_use 開始→等核准→核准後工具執行中→工具結果→背景工作啟動");
        flags.Last().Should().Be((false, false, false));
    }

    [Fact]
    public async Task 重開途中按停止_不接續_回Restarted且不發TurnCompleted()
    {
        using var session = new ClaudeAgentSession();
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("text"));
        await log.UntilAsync(e => e is AgentTextDeltaEvent);

        // 新程序過 3 秒才開始接續：在那之前按停止
        var restart = session.RestartAsync(Request(AgentRestartMode.SafePoint,
            Settings("text", started: true, env: new Dictionary<string, string> { ["FAKE_RESUME_DELAY_MS"] = "3000" })));
        await log.UntilAsync(e => e is AgentTurnStartedByCliEvent);
        await session.InterruptAsync();
        var result = await restart;

        result.Outcome.Should().Be(AgentRestartOutcome.Restarted, "重開途中按停止＝不接續，被打斷的那一輪由中樞收尾");
        session.IsRunning.Should().BeFalse();
        session.HasLiveProcess.Should().BeFalse();
        log.Drain(1000).OfType<AgentTurnCompletedEvent>().Should().BeEmpty();
    }

    // ───── 審查後的修正（claude-restart-1～3） ─────

    [Fact]
    public async Task 閒置時CLI自己那一輪進行中使用者送出_兩輪都結束後不再算有背景工作_閒置回到安全點()
    {
        using var session = new ClaudeAgentSession { NotificationTurnTimeout = TimeSpan.FromSeconds(1) };
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("bgturn"));
        await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        await log.UntilAsync(e => e is AgentTextDeltaEvent d && d.Delta.Contains("notification"));
        session.IsRunning.Should().BeTrue("CLI 自己開的一輪也標成執行中（畫面轉圈、可以停止）");
        session.HasLiveBackgroundWork.Should().BeTrue("CLI 自己那一輪到它的 result 為止都算背景工作");

        // 中樞還沒收到那一輪開始的事件就送出：接手成為這一輪（CLI 排在那一輪後面處理）
        await session.SendAsync(Input("second"), Settings("bgturn", started: true));
        var second = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        Report(second).Value<string>("text").Should().Be("second");
        await Task.Delay(1500);   // 超過 NotificationTurnTimeout

        session.IsRunning.Should().BeFalse();
        session.HasLiveBackgroundWork.Should().BeFalse("CLI 自己那一輪的 result 到的時候會話正在執行（使用者剛送出），也要清掉：輪次是依序處理的");
        session.IsAtSafePoint.Should().BeTrue();
        log.Drain(200);
        log.All.OfType<AgentSafePointChangedEvent>().Last().HasLiveBackgroundWork.Should().BeFalse("中樞以這個事件知道背景工作已結束");
        (await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("bgturn", started: true)))).Outcome
            .Should().Be(AgentRestartOutcome.Restarted, "閒置、沒有背景工作：以新版重開（以前一直回 Deferred，對話一直停在舊版）");
    }

    [Fact]
    public async Task 使用者的訊息排在CLI自己那一輪後面_那一輪結束到使用者那一輪開始之間不算安全點()
    {
        var env = new Dictionary<string, string> { ["FAKE_QUEUE_GAP_MS"] = "2500" };
        using var session = new ClaudeAgentSession { NotificationTurnTimeout = TimeSpan.FromSeconds(1) };
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("bgturn", env: env));
        await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        await log.UntilAsync(e => e is AgentTextDeltaEvent d && d.Delta.Contains("notification"));

        await session.SendAsync(Input("second"), Settings("bgturn", started: true, env: env));
        // CLI 自己那一輪的 result 已到，使用者那一輪還沒開始（CLI 停 2.5 秒才處理排隊的訊息）
        await log.UntilAsync(e => e is AgentTextCompletedEvent t && t.Text.Contains("notification-reply"));
        await Task.Delay(400);

        session.IsAtSafePoint.Should().BeFalse("CLI 還沒開始處理使用者的訊息（那一輪的串流不算接手）：這時重開，訊息還沒寫進逐字稿就會不見");
        (await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("bgturn", started: true, env: env)))).Outcome
            .Should().Be(AgentRestartOutcome.Deferred);
        File.ReadAllText(Transcript).Should().NotContain("\"second\"", "使用者的訊息確實還沒寫進逐字稿");

        await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        File.ReadAllText(Transcript).Should().Contain("\"second\"");
        session.HasLiveBackgroundWork.Should().BeFalse();
    }

    [Fact]
    public async Task 新一輪送出後第一則回覆前被重開_不拿上一輪的EndTurn當成已回覆完_備案照樣送接續訊息()
    {
        // RESUME 環境變數失效時的備案（測試以不帶變數模擬）
        using var session = new ClaudeAgentSession { UseResumeEnvironment = false, AutoTurnGrace = TimeSpan.FromSeconds(1) };
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("slow"));
        await log.UntilAsync(e => e is AgentTurnCompletedEvent);

        await session.SendAsync(Input("second"), Settings("slow", started: true));
        var start = DateTime.UtcNow;
        while (!session.IsAtSafePoint && DateTime.UtcNow - start < TimeSpan.FromSeconds(10))
            await Task.Delay(50);
        session.IsAtSafePoint.Should().BeTrue("API 請求已送出（requesting）：CLI 已接手，還沒有第一個 token");

        var result = await session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("slow", started: true)));

        result.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued,
            "這一輪的使用者訊息已寫進逐字稿、還沒有任何回覆：上一輪的 end_turn 不屬於這一輪，備案要改經 stdin 送接續訊息");
        var done = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        Report(done).Value<string>("text").Should().Be(Continuation);
    }

    [Fact]
    public async Task CLI在訊息寫完_記下已送出之前就開始回應_那段活動也算接手_到得了安全點()
    {
        // 拉長「寫完 → 記下已送出」這段：假 CLI 在這段期間就回 system/init 與 requesting，之後 20 秒沒有輸出
        // （雲端 CI 的慢機器上真的發生過：安全點一直到不了，因為接手的證據在 payloadSent 設好之前就到了）
        using var session = new ClaudeAgentSession { AfterPayloadWrittenForTest = () => Task.Delay(1500) };
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("slow"));
        await log.UntilAsync(e => e is AgentTurnCompletedEvent);

        await session.SendAsync(Input("second"), Settings("slow", started: true));
        var start = DateTime.UtcNow;
        while (!session.IsAtSafePoint && DateTime.UtcNow - start < TimeSpan.FromSeconds(5))
            await Task.Delay(50);
        session.IsAtSafePoint.Should().BeTrue("寫入期間 CLI 已回 requesting：它接手了這一輪、還沒有第一個 token");
    }

    [Fact]
    public async Task 重開等逐字稿時對話被刪除_舊程序由刪除的路徑結束_不再以可能被重複使用的PID殺程序樹()
    {
        using var session = new ClaudeAgentSession { TranscriptCatchUpTimeout = TimeSpan.FromSeconds(3), KillTimeout = TimeSpan.FromSeconds(2) };
        using var log = new EventLog(session);
        await session.SendAsync(Input("開始"), Settings("lagging"));
        await log.UntilAsync(e => e is AgentTextDeltaEvent);
        session.IsAtSafePoint.Should().BeTrue();

        var restart = session.RestartAsync(Request(AgentRestartMode.SafePoint, Settings("lagging", started: true)));
        await Task.Delay(300);
        restart.IsCompleted.Should().BeFalse("在等逐字稿寫入這一輪");

        var old = (AgentProcess)typeof(ClaudeAgentSession).GetField("process", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(session)!;
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE")) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add("30");
        psi.ArgumentList.Add("127.0.0.1");
        using var unrelated = System.Diagnostics.Process.Start(psi)!;
        try
        {
            // 模擬：舊程序被釋放後，它的 PID 被一個不相干的程序拿去用了
            typeof(AgentProcess).GetProperty(nameof(AgentProcess.ProcessId))!.SetValue(old, unrelated.Id);
            session.Dispose();   // 刪除對話（AgentHub.Delete）：釋放會話，舊程序被結束並釋放

            var result = await restart.WaitAsync(TimeSpan.FromSeconds(20));

            result.Outcome.Should().NotBe(AgentRestartOutcome.RestartedAndContinued);
            old.HasExited.Should().BeTrue();
            unrelated.HasExited.Should().BeFalse("被別的路徑卸下並釋放的舊程序只等它結束：不能以 PID 擷取程序樹，那個 PID 已經是別的程序");
        }
        finally
        {
            if (!unrelated.HasExited)
                unrelated.Kill();
        }
    }
}
