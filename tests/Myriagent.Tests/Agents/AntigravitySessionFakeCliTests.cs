using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 以假的 agy（Windows PowerShell 腳本，照 agy 1.3.5 實測的 stream-json 格式）測 AntigravityAgentSession 的會話層行為，不連網、不耗額度：
/// 啟動參數、背景說明只在新對話的第一則、同一個程序跑多輪、換設定重開以 --conversation 續接、停止、沒登入、續接失敗改開新對話。
/// 情境由環境變數 FAKE_SCENARIO 決定；假 agy 把收到的參數與訊息以回覆文字（JSON）回報給測試。
/// </summary>
public sealed class AntigravitySessionFakeCliTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// 假的 agy：腳本只用 ASCII（Windows PowerShell 讀沒有 BOM 的腳本用系統碼頁）。
    /// 每一輪：user_input、（續接的第一輪）system_message、規劃用的 agent_response（帶 usage）、run_command 工具、回覆文字（DONE 帶最後一段與 usage）、result（usage 是整個對話的累計）
    /// </summary>
    private const string FakeAgy = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $argList = @($args)
        $scenario = $env:FAKE_SCENARIO

        function Emit([string]$json) { $out.WriteLine($json) }
        function Warn([string]$text) { [Console]::Error.WriteLine($text); [Console]::Error.Flush() }
        function Js([string]$text) { return (ConvertTo-Json -InputObject $text -Compress) }
        function ArgValue([string]$name) {
            $i = [array]::IndexOf($argList, $name)
            if ($i -ge 0 -and $i + 1 -lt $argList.Count) { return $argList[$i + 1] }
            return $null
        }
        function Step([string]$conv, [int]$index, [string]$state, [string]$type, [string]$extra) {
            Emit ('{"event":"step_update","step_update":{"conversation_id":"' + $conv + '","step_index":' + $index + ',"state":"' + $state + '","step_type":"' + $type + '"' + $extra + '}}')
        }
        function Usage([int]$i, [int]$o, [int]$t, [int]$c) {
            return ',"usage":{"input_tokens":' + $i + ',"output_tokens":' + $o + ',"thinking_tokens":' + $t + ',"cache_read_tokens":' + $c + ',"total_tokens":' + ($i + $o) + '}'
        }
        function NewConv { return 'conv-' + [guid]::NewGuid().ToString('N').Substring(0, 8) }

        if ($scenario -eq 'signin') {
            Emit 'Authentication required. Please visit the URL to log in:'
            Emit 'https://accounts.google.com/o/oauth2/auth?client_id=fake'
            Start-Sleep -Seconds 60
            exit 1
        }

        $singlePrompt = ArgValue '-p'
        if ($singlePrompt) {
            Emit ('single-prompt:' + $singlePrompt)
            exit 0
        }

        $requested = ArgValue '--conversation'
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        if ($scenario -eq 'notfoundclosed' -and -not $requested) {
            # the retry with a new conversation: close stdin without exiting, so writing the message fails
            Add-Type -Namespace FakeAgy -Name Native -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern System.IntPtr GetStdHandle(int n); [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool CloseHandle(System.IntPtr h);'
            $in.Dispose()
            [void][FakeAgy.Native]::CloseHandle([FakeAgy.Native]::GetStdHandle(-10))
            Start-Sleep -Seconds 30
            exit 0
        }
        $conv = $requested
        $blank = $false
        if ($requested -and $scenario -like 'notfound*') {
            if ($scenario -eq 'notfounderror') {
                $null = $in.ReadLine()
                Emit ('{"event":"result","result":{"conversation_id":"","status":"ERROR","response":"","error":"conversation ' + $requested + ' not found","num_turns":0' + (Usage 0 0 0 0) + '}}')
                exit 1
            }
            # agy 1.3.5: only a warning on stderr, then a new blank conversation
            if ($scenario -ne 'notfoundlate') { Warn ('warning: conversation "' + $requested + '" not found') }
            $conv = NewConv
            $blank = $true
        }
        if (-not $conv) { $conv = NewConv }

        Emit ('{"event":"init","conversation_id":"' + $conv + '","init":{"model":"fake-model","cwd":' + (Js (Get-Location).Path) + ',"tools":["run_command","view_file"],"permission_mode":"request-review"}}')
        $step = 0
        if ($requested) { $step = 10 }
        $turn = 0
        $inTok = 0
        $outTok = 0
        while ($true) {
            $line = $in.ReadLine()
            if ($line -eq $null) { exit 0 }
            if ($line.Trim().Length -eq 0) { continue }
            $msg = $line | ConvertFrom-Json
            $turn++
            if ($blank) {
                if ($scenario -eq 'notfoundlate') { Warn ('warning: conversation "' + $requested + '" not found') }
                # the session should kill this process before the blank conversation answers
                Start-Sleep -Milliseconds 1500
                [IO.File]::WriteAllText((Join-Path $env:FAKE_WORKDIR 'blank-answered'), 'x')
            }
            Step $conv $step 'DONE' 'user_input' ''
            $step++
            if ($requested -and -not $blank -and $turn -eq 1) {
                Step $conv $step 'DONE' 'system_message' ',"duration_seconds":0'
                $step++
            }
            if ($scenario -eq 'hang') {
                Step $conv $step 'ACTIVE' 'agent_response' (',"text_delta":' + (Js 'working'))
                Step $conv ($step + 1) 'ACTIVE' 'tool' ',"tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"npm test"}}'
                Start-Sleep -Seconds 60
                exit 0
            }
            if ($scenario -eq 'laterror' -and $turn -eq 2) {
                Emit ('{"event":"result","result":{"conversation_id":"' + $conv + '","status":"ERROR","response":"","error":"invalid conversation state","num_turns":2' + (Usage $inTok $outTok 0 0) + '}}')
                continue
            }
            if ($scenario -eq 'neterr' -and -not $requested -and $turn -eq 2) {
                $wsErr = 'API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": write tcp 192.168.0.10:55012->172.217.112.4:443: wsasend: An existing connection was forcibly closed by the remote host.'
                Emit ('{"event":"result","result":{"conversation_id":"' + $conv + '","status":"ERROR","response":"","error":' + (Js $wsErr) + ',"num_turns":2' + (Usage $inTok $outTok 0 0) + '}}')
                continue
            }
            if ($scenario -eq 'bgneterr') {
                if ($turn -eq 1) {
                    Step $conv $step 'ACTIVE' 'tool' ',"tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"npm run watch","IsDaemon":true}}'
                    Step $conv $step 'DONE' 'tool' ',"tool_name":"run_command","duration_seconds":0.1,"tool_info":{"name":"run_command","parameters":{"CommandLine":"npm run watch","IsDaemon":true},"output":"Background task-watch-1 running in the background"}'
                    $step++
                    $reply = ConvertTo-Json -Compress -InputObject ([ordered]@{ turn = $turn; conv = $conv })
                    Step $conv $step 'DONE' 'agent_response' (',"text_delta":' + (Js $reply) + (Usage 20 5 0 0))
                    $step++
                    $inTok += 20; $outTok += 5
                    Emit ('{"event":"result","result":{"conversation_id":"' + $conv + '","status":"SUCCESS","response":"bg-started","duration_seconds":0.1,"num_turns":1' + (Usage $inTok $outTok 0 0) + '}}')
                    continue
                }
                if ($turn -eq 2) {
                    $wsErr = 'API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": write tcp 192.168.0.10:55012->172.217.112.4:443: wsasend: An existing connection was forcibly closed by the remote host.'
                    Emit ('{"event":"result","result":{"conversation_id":"' + $conv + '","status":"ERROR","response":"","error":' + (Js $wsErr) + ',"num_turns":2' + (Usage $inTok $outTok 0 0) + '}}')
                    continue
                }
            }
            if ($scenario -eq 'clirace' -and $turn -eq 1) {
                $reply = ConvertTo-Json -Compress -InputObject ([ordered]@{ turn = $turn; conv = $conv; args = $argList })
                Step $conv $step 'DONE' 'agent_response' (',"text_delta":' + (Js $reply) + (Usage 40 5 0 0))
                $step++
                $inTok += 40; $outTok += 5
                Emit ('{"event":"result","result":{"conversation_id":"' + $conv + '","status":"SUCCESS","response":"t1","duration_seconds":0.1,"num_turns":1' + (Usage $inTok $outTok 0 0) + '}}')
                # Immediately start an unsolicited CLI wakeup turn BEFORE reading the next user stdin line
                Step $conv $step 'ACTIVE' 'agent_response' ',"text_delta":"cli-wakeup-running"'
                $nextLine = $in.ReadLine()
                if ($nextLine -eq $null) { exit 0 }
                $nextMsg = $nextLine | ConvertFrom-Json
                # Finish the CLI wakeup turn first, then run the user's turn
                Step $conv $step 'DONE' 'agent_response' (',"text_delta":"!"' + (Usage 25 4 0 0))
                $step++
                $inTok += 25; $outTok += 4
                Emit ('{"event":"result","result":{"conversation_id":"' + $conv + '","status":"SUCCESS","response":"cli-wakeup-running!","duration_seconds":0.1,"num_turns":2' + (Usage $inTok $outTok 0 0) + '}}')
                $turn++
                Step $conv $step 'DONE' 'user_input' ''
                $step++
                $userReply = ConvertTo-Json -Compress -InputObject ([ordered]@{ turn = $turn; conv = $conv; text = [string]$nextMsg.message.content })
                Step $conv $step 'DONE' 'agent_response' (',"text_delta":' + (Js $userReply) + (Usage 60 12 0 0))
                $step++
                $inTok += 60; $outTok += 12
                Emit ('{"event":"result","result":{"conversation_id":"' + $conv + '","status":"SUCCESS","response":"user-done","duration_seconds":0.1,"num_turns":3' + (Usage $inTok $outTok 0 0) + '}}')
                continue
            }
            if ($scenario -eq 'bgwakeup') {
                Step $conv $step 'ACTIVE' 'tool' ',"tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"npm run build","IsDaemon":true}}'
                Step $conv $step 'DONE' 'tool' ',"tool_name":"run_command","duration_seconds":0.1,"tool_info":{"name":"run_command","parameters":{"CommandLine":"npm run build","IsDaemon":true},"output":"Background task-build-1 running in the background"}'
                $step++
                $reply = ConvertTo-Json -Compress -InputObject ([ordered]@{ turn = $turn; conv = $conv; args = $argList })
                Step $conv $step 'DONE' 'agent_response' (',"text_delta":' + (Js $reply) + (Usage 50 10 0 0))
                $step++
                $inTok += 50; $outTok += 10
                Emit ('{"event":"result","result":{"conversation_id":"' + $conv + '","status":"SUCCESS","response":"started","duration_seconds":0.2,"num_turns":1' + (Usage $inTok $outTok 0 0) + '}}')
                Start-Sleep -Milliseconds 120
                Step $conv $step 'DONE' 'user_input' ''
                $step++
                Step $conv $step 'DONE' 'agent_response' (',"text_delta":"wakeup-completed"' + (Usage 30 8 0 0))
                $step++
                $inTok += 30; $outTok += 8
                Emit ('{"event":"result","result":{"conversation_id":"' + $conv + '","status":"SUCCESS","response":"wakeup-completed","duration_seconds":0.2,"num_turns":2' + (Usage $inTok $outTok 0 0) + '}}')
                continue
            }
            $reply = ConvertTo-Json -Compress -InputObject ([ordered]@{ turn = $turn; conv = $conv; args = $argList; text = [string]$msg.message.content; cwd = (Get-Location).Path })
            Step $conv $step 'DONE' 'agent_response' (',"duration_seconds":1.5' + (Usage 100 10 5 0))
            $step++
            Step $conv $step 'ACTIVE' 'tool' ',"tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"}}'
            Step $conv $step 'DONE' 'tool' ',"tool_name":"run_command","duration_seconds":0.2,"tool_info":{"name":"run_command","parameters":{"CommandLine":"echo hi"},"output":"hi\r\n"}'
            $step++
            Step $conv $step 'ACTIVE' 'agent_response' (',"text_delta":' + (Js $reply))
            Step $conv $step 'DONE' 'agent_response' (',"text_delta":"\n","duration_seconds":1.2' + (Usage 40 3 0 60))
            $step++
            $inTok += 200
            $outTok += 13
            Emit ('{"event":"result","result":{"conversation_id":"' + $conv + '","status":"SUCCESS","response":"done","duration_seconds":3,"num_turns":' + $turn + (Usage $inTok $outTok 5 60) + '}}')
            if ($scenario -eq 'oneshot') { exit 0 }
        }
        """";

    private readonly TempDirectory temp = new("fake-agy-session");
    private readonly string script;
    private readonly string work;

    public AntigravitySessionFakeCliTests()
    {
        script = temp.WriteFile("fake-agy.ps1", FakeAgy);
        work = temp.CreateDirectory("work");
    }

    public void Dispose() => temp.Dispose();

    private static AntigravityAgentSession NewSession(TimeSpan? closeGrace = null, TimeSpan? treeTimeout = null) => new()
    {
        TranscriptPathResolver = null,
        RestartCloseGrace = closeGrace ?? TimeSpan.FromSeconds(2),
        RestartTreeExitTimeout = treeTimeout ?? TimeSpan.FromSeconds(5),
    };

    private AgentSessionSettings Settings(string scenario, string? sessionId = null, bool started = false, string? model = null, string? effort = null,
        AgentPermissionMode mode = AgentPermissionMode.Ask, string? workingDirectory = null, CliBinaryIdentity? cli = null, bool useLocalCustomizations = true, string? agent = null)
        => new(
            sessionId,
            started,
            new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            "fake",
            e =>
            {
                e["FAKE_SCENARIO"] = scenario;
                e["FAKE_WORKDIR"] = workingDirectory ?? work;
            },
            null,
            model,
            effort,
            mode,
            workingDirectory ?? work,
            temp.Root,
            "測試說明",
            UseLocalCustomizations: useLocalCustomizations,
            HistoryRecap: "（前文節錄）",
            Cli: cli,
            Agent: agent);

    private static AgentTurnInput Input(string text) => new(text, Array.Empty<AgentAttachment>());

    /// <summary>會話事件的收集器（事件在程序讀取執行緒發出）</summary>
    private sealed class EventLog : IDisposable
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
                if (until(ev))
                    return collected;
            }
            throw new TimeoutException("假的 agy 沒有在時限內完成；收到：" + string.Join("、", collected.Select(e => e.GetType().Name)));
        }

        /// <summary>等一小段時間，取出之後到達的事件</summary>
        public List<AgentEvent> Drain(int waitMilliseconds)
        {
            var result = new List<AgentEvent>();
            var deadline = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                if (queue.TryTake(out var ev, 50))
                    result.Add(ev);
            }
            return result;
        }

        public void Dispose() => session.EventReceived -= handler;
    }

    private static async Task<List<AgentEvent>> TurnAsync(IAgentSession session, EventLog log, AgentSessionSettings settings, string text)
    {
        await session.SendAsync(Input(text), settings);
        return await log.UntilAsync(e => e is AgentTurnCompletedEvent);
    }

    /// <summary>假 agy 的回覆（JSON）：turn、conv、args、text、cwd</summary>
    private static JObject Reply(IEnumerable<AgentEvent> events) => JObject.Parse(events.OfType<AgentTextCompletedEvent>().Single().Text);

    private static string[] Args(JObject reply) => reply["args"]!.Select(t => (string)t!).ToArray();

    [Fact]
    public async Task 新對話_背景說明只放在第一則_同一個程序跑第二輪_用量是這一輪的()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        var first = await TurnAsync(session, log, Settings("default"), "你好");
        var started = first.OfType<AgentSessionStartedEvent>().Single();
        var reply1 = Reply(first);
        Args(reply1).Should().Equal("--print=", "--input-format", "stream-json", "--output-format", "stream-json", "--mode", "accept-edits", "--add-dir", temp.Root);
        ((string)reply1["text"]!).Should().Be("<myriagent_instructions>\n測試說明\n</myriagent_instructions>\n\n你好");
        ((string)reply1["cwd"]!).Should().Be(work);
        started.SessionId.Should().Be((string)reply1["conv"]!);
        var tool = first.OfType<AgentToolStartedEvent>().Single();
        tool.ToolKind.Should().Be("bash");
        tool.Title.Should().Be("echo hi");
        first.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(e => e.ToolId == tool.ToolId && e.Status == AgentItemStatus.Done && e.Output == "hi\n");
        // 各步驟相加：輸入 100＋40＋快取 60、輸出 10＋思考 5＋3
        first.OfType<AgentTurnCompletedEvent>().Single().Should().Be(new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, new AgentUsage(200, 18, 60, null)));

        var processId = session.CurrentProcessId;
        var second = await TurnAsync(session, log, Settings("default"), "再一次");
        var reply2 = Reply(second);
        ((int)reply2["turn"]!).Should().Be(2, "同一個程序");
        session.CurrentProcessId.Should().Be(processId);
        ((string)reply2["text"]!).Should().Be("再一次", "背景說明只放在新對話的第一則");
        second.OfType<AgentTurnCompletedEvent>().Single().Usage.Should().Be(new AgentUsage(200, 18, 60, null), "這一輪各步驟相加，不是 result 的整個對話累計");
        second.OfType<AgentSessionStartedEvent>().Should().BeEmpty("同一個對話 id 不重複回報");
        second.OfType<AgentTextCompletedEvent>().Single().BlockId.Should().NotBe(first.OfType<AgentTextCompletedEvent>().Single().BlockId);
    }

    [Fact]
    public async Task 換模型與權限模式_重開程序以conversation續接_換資料夾時開頭告知()
    {
        using var session = NewSession();
        using var log = new EventLog(session);
        var conv = (string)Reply(await TurnAsync(session, log, Settings("default"), "第一則"))["conv"]!;

        var other = temp.CreateDirectory("other");
        var reply = Reply(await TurnAsync(session, log,
            Settings("default", model: "gemini-3.8-pro", effort: "high", mode: AgentPermissionMode.FullAccess, workingDirectory: other), "第二則"));

        ((int)reply["turn"]!).Should().Be(1, "新的程序");
        Args(reply).Should().Equal("--print=", "--input-format", "stream-json", "--output-format", "stream-json",
            "--conversation", conv, "--model", "gemini-3.8-pro", "--effort", "high", "--dangerously-skip-permissions", "--add-dir", temp.Root);
        ((string)reply["cwd"]!).Should().Be(other);
        ((string)reply["text"]!).Should().Be(AntigravityStreamRules.BuildWorkingDirectoryNotice(other) + "\n\n第二則");
    }

    [Fact]
    public async Task 程式重開後續接_用中樞記的對話id_不再附背景說明()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        var reply = Reply(await TurnAsync(session, log, Settings("default", sessionId: "conv-saved", started: true), "接著做"));

        Args(reply).Should().ContainInConsecutiveOrder("--conversation", "conv-saved");
        ((string)reply["text"]!).Should().Be("接著做");
        ((string)reply["conv"]!).Should().Be("conv-saved");
    }

    [Fact]
    public async Task 停止_結束程序_開著的工具改成已停止_回報已停止()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        await session.SendAsync(Input("跑很久"), Settings("hang"));
        await log.UntilAsync(e => e is AgentToolStartedEvent);
        session.IsAtSafePoint.Should().BeFalse("工具執行中");
        await session.InterruptAsync();
        var rest = await log.UntilAsync(e => e is AgentTurnCompletedEvent);

        rest.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(e => e.Status == AgentItemStatus.Cancelled && e.Output == AntigravityStreamRules.InterruptedToolOutput);
        rest.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("working");
        rest.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 沒登入_看到登入提示就結束程序_回報尚未登入()
    {
        using var session = NewSession();
        using var log = new EventLog(session);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var events = await TurnAsync(session, log, Settings("signin"), "你好");

        var completed = events.OfType<AgentTurnCompletedEvent>().Single();
        completed.Outcome.Should().Be(AgentTurnOutcome.Failed);
        completed.ErrorMessage.Should().Be(AntigravityStreamRules.NotSignedInMessage);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "不能等 agy 的 60 秒授權碼逾時");
    }

    [Fact]
    public async Task 續接失敗後改開新對話也失敗_這一輪仍要回報結束_不能卡在執行中()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        // 訊息夠大，寫入一定碰到已關閉的 stdin（不會整則落在管線緩衝裡）
        var events = await TurnAsync(session, log, Settings("notfoundclosed", sessionId: "conv-gone", started: true), "還記得嗎" + new string('長', 300_000));

        events.OfType<AgentNoticeEvent>().Should().ContainSingle("先改開新對話");
        var completed = events.OfType<AgentTurnCompletedEvent>().Single();
        completed.Outcome.Should().Be(AgentTurnOutcome.Failed);
        completed.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 閒置的程序自己結束_不回報任何一輪_下一則以conversation續接()
    {
        using var session = NewSession();
        using var log = new EventLog(session);
        var conv = (string)Reply(await TurnAsync(session, log, Settings("oneshot"), "第一則"))["conv"]!;

        var afterExit = log.Drain(2000);
        afterExit.OfType<AgentTurnCompletedEvent>().Should().BeEmpty("這一輪早就結束了");
        afterExit.OfType<AgentProcessExitedEvent>().Single().Expected.Should().BeTrue("閒置時結束不是錯誤");

        var reply = Reply(await TurnAsync(session, log, Settings("oneshot"), "第二則"));
        Args(reply).Should().ContainInConsecutiveOrder("--conversation", conv);
        ((string)reply["text"]!).Should().Be("第二則", "續接的對話不再附背景說明");
    }

    [Fact]
    public async Task 同一個程序後面幾輪的錯誤_不當成續接失敗_照實回報()
    {
        using var session = NewSession();
        using var log = new EventLog(session);
        await TurnAsync(session, log, Settings("laterror"), "第一則");

        var second = await TurnAsync(session, log, Settings("laterror"), "第二則");

        second.OfType<AgentNoticeEvent>().Should().BeEmpty("這個程序不是這一輪才以 --conversation 啟動的，不能丟掉 agy 的對話改開新的");
        var completed = second.OfType<AgentTurnCompletedEvent>().Single();
        completed.Outcome.Should().Be(AgentTurnOutcome.Failed);
        completed.ErrorMessage.Should().Be("invalid conversation state");
    }

    /// <summary>
    /// notfound：agy 1.3.5 的實際行為（啟動就在 stderr 警告、默默開空白的新對話）；notfoundlate：警告在訊息送出之後才到；
    /// notfounderror：以錯誤結果回報（舊版或日後可能的行為）
    /// </summary>
    [Theory]
    [InlineData("notfound")]
    [InlineData("notfoundlate")]
    [InlineData("notfounderror")]
    public async Task 續接失敗_結束空白對話的程序_改開新對話並附上前文節錄重送(string scenario)
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        var events = await TurnAsync(session, log, Settings(scenario, sessionId: "conv-gone", started: true), "還記得嗎");

        events.OfType<AgentNoticeEvent>().Should().ContainSingle();
        var reply = Reply(events);
        Args(reply).Should().NotContain("--conversation");
        ((string)reply["text"]!).Should().Be("<myriagent_instructions>\n測試說明\n</myriagent_instructions>\n\n（前文節錄）\n\n還記得嗎");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        events.OfType<AgentSessionStartedEvent>().Last().SessionId.Should().Be((string)reply["conv"]!, "最後記下的是重送的那個對話");
        log.Drain(2500).OfType<AgentTextCompletedEvent>().Should().BeEmpty("空白對話的程序已被結束，不會再冒出它的回覆");
        File.Exists(Path.Combine(work, "blank-answered")).Should().BeFalse("空白對話的程序要真的被結束，不能讓它沒有前文就答下去（完全自動時還會執行工具）");
    }

    [Fact]
    public async Task CLI本地唯讀斜線指令_介面管理回提示_單發查詢走減p不中斷既有對話程序()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        var first = await TurnAsync(session, log, Settings("default"), "第一則");
        var pid = session.CurrentProcessId;
        pid.Should().NotBeNull();

        var uiHintEvents = await TurnAsync(session, log, Settings("default"), "/model gemini-3.8-pro");
        uiHintEvents.OfType<AgentNoticeEvent>().Single().Message.Should().Contain("模型選單");
        uiHintEvents.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.CurrentProcessId.Should().Be(pid, "不碰正在跑的對話程序");

        var creditsEvents = await TurnAsync(session, log, Settings("default"), "  /credits  ");
        creditsEvents.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("single-prompt:/credits");
        creditsEvents.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.CurrentProcessId.Should().Be(pid, "單發 -p 獨立執行，既有對話程序不被殺掉");

        var second = await TurnAsync(session, log, Settings("default"), "第二則");
        ((int)Reply(second)["turn"]!).Should().Be(2, "同一個對話程序繼續處理後續訊息");
    }

    [Fact]
    public async Task 重開RestartAsync_工具執行中安全點延後_兩階段Stopped後以新版conversation自動接續()
    {
        using var session = NewSession(closeGrace: TimeSpan.FromMilliseconds(400), treeTimeout: TimeSpan.FromSeconds(2));
        using var log = new EventLog(session);

        // 先跑完第一輪拿到對話 id，再送第二輪卡在工具執行中（hang）
        var oldCli = CliIdentities.Antigravity("1.4.5", fileId: 1);
        var first = await TurnAsync(session, log, Settings("default", cli: oldCli), "準備");
        var conv = (string)Reply(first)["conv"]!;
        var p1 = first.OfType<AgentProcessStartedEvent>().Single();

        // 換成 hang 情境（因為環境變數不在 Signature 內，我們改用 Auto 沙箱模式讓第二輪在新的 hang 程序裡卡在工具執行中）
        await session.SendAsync(Input("長任務"), Settings("hang", sessionId: conv, started: true, mode: AgentPermissionMode.Auto, cli: oldCli));
        var before = await log.UntilAsync(e => e is AgentToolStartedEvent);
        var pHang = before.OfType<AgentProcessStartedEvent>().Single();
        session.IsAtSafePoint.Should().BeFalse();

        // 1. SafePoint 模式：工具執行中回 Deferred，什麼都不動
        var deferred = await session.RestartAsync(new AgentRestartRequest(
            AgentRestartReason.CliUpdated, AgentRestartMode.SafePoint, "1.4.5", "1.4.6",
            AutoContinue: true, "接續", Array.Empty<AgentInterruptedTool>(),
            Settings("default", sessionId: conv, started: true, mode: AgentPermissionMode.Auto)));
        deferred.Outcome.Should().Be(AgentRestartOutcome.Deferred);

        // 2. 第一階段停下（Settings = null，模擬更新換檔屏障或 app 自我更新排空）：回 Stopped 並記住被打斷的對話
        var stopped = await session.RestartAsync(new AgentRestartRequest(
            AgentRestartReason.CliUpdated, AgentRestartMode.Immediate, "1.4.5", "1.4.6",
            AutoContinue: true, null, Array.Empty<AgentInterruptedTool>(), Settings: null));
        stopped.Outcome.Should().Be(AgentRestartOutcome.Stopped);
        stopped.PreviousProcessInstance.Should().Be(pHang.ProcessInstance);
        session.HasLiveProcess.Should().BeFalse();

        // 3. 第二階段帶新版 CLI 的 Settings 與接續訊息重開：回 RestartedAndContinued，以 --conversation 啟動新版並送出接續訊息
        const string Continuation = "（系統通知：CLI 已更新至 1.4.6，請接著完成剛才的工作）";
        var newCli = CliIdentities.Antigravity("1.4.6", fileId: 2);
        var continued = await session.RestartAsync(new AgentRestartRequest(
            AgentRestartReason.CliUpdated, AgentRestartMode.Immediate, "1.4.5", "1.4.6",
            AutoContinue: true, Continuation, Array.Empty<AgentInterruptedTool>(),
            Settings("default", sessionId: conv, started: true, mode: AgentPermissionMode.Auto, cli: newCli, useLocalCustomizations: false)));
        continued.Outcome.Should().Be(AgentRestartOutcome.RestartedAndContinued);

        var after = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        after.OfType<AgentProcessStartedEvent>().Single().Cli.Should().Be(newCli);
        after.OfType<AgentRunningVersionEvent>().Should().Contain(e => e.Version == "1.4.6");
        after.OfType<AgentTurnStartedByCliEvent>().Should().Equal(new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));

        var reply = JObject.Parse(after.OfType<AgentTextCompletedEvent>().Last().Text);
        Args(reply).Should().ContainInConsecutiveOrder("--conversation", conv);
        Args(reply).Should().ContainInConsecutiveOrder("--dangerously-skip-permissions", "--sandbox", "--disable-slash-commands");
        ((string)reply["text"]!).Should().Be(Continuation);
        session.RunningVersion.Should().Be("1.4.6");
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task 自訂代理參數與背景任務閒置自動喚醒_發送AgentIdleTurnStartedEvent並完成第二輪()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        var firstTurn = await TurnAsync(session, log, Settings("bgwakeup", agent: "security-auditor"), "啟動背景建置");
        var reply = JObject.Parse(firstTurn.OfType<AgentTextCompletedEvent>().Single().Text);
        Args(reply).Should().ContainInConsecutiveOrder("--agent", "security-auditor");
        firstTurn.OfType<AgentBackgroundWorkEvent>().Should().ContainSingle().Which.Count.Should().Be(1);
        session.HasLiveBackgroundWork.Should().BeTrue("第一輪結束後背景任務仍在執行，不在安全點");
        session.IsAtSafePoint.Should().BeFalse();

        // 閒置中的假 agy 程序在 120ms 後自動吐出第二輪 step_update 與 result：會話自動轉為執行中並發 AgentIdleTurnStartedEvent
        var wakeupEvents = await log.UntilAsync(e => e is AgentTurnCompletedEvent);
        wakeupEvents.OfType<AgentIdleTurnStartedEvent>().Should().ContainSingle()
            .Which.AfterBackgroundTask.Should().BeTrue();
        wakeupEvents.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("wakeup-completed");
        session.IsRunning.Should().BeFalse();

        await session.CloseAsync();
        session.HasLiveBackgroundWork.Should().BeFalse();
    }

    [Fact]
    public async Task 背景自動喚醒cliTurn剛啟動時使用者同時送出訊息_接手既有輪次不拋例外且不提前結束使用者回合()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        await TurnAsync(session, log, Settings("clirace"), "第一則");
        await log.UntilAsync(e => e is AgentIdleTurnStartedEvent);
        session.IsRunning.Should().BeTrue("CLI 背景喚醒輪次正在跑");

        // 使用者在 cliTurn 尚未回到 UI 執行緒或剛並發送出時呼叫 SendAsync：不可丟出「這個對話還在執行中」
        await session.SendAsync(Input("使用者插話"), Settings("clirace"));
        var events = await log.UntilAsync(e => e is AgentTurnCompletedEvent);

        events.OfType<AgentTurnCompletedEvent>().Should().ContainSingle("背景輪次的 result 應被接手合併，只有使用者回合結束才報 TurnCompleted");
        var texts = events.OfType<AgentTextCompletedEvent>().Select(t => t.Text).ToList();
        texts.Should().Contain("cli-wakeup-running!");
        JObject.Parse(texts.Last())["text"]!.Value<string>().Should().Be("使用者插話");
        events.OfType<AgentTokenUsageEvent>().Sum(u => u.Delta.InputTokens).Should().Be(25 + 60, "背景輪次與使用者輪次的 token 只各算一次，不雙重計費");
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task API網路連線中斷_無背景任務時自動回收舊程序_下一輪以conversation啟動新程序續接()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        var first = await TurnAsync(session, log, Settings("neterr"), "第一則");
        var conv = (string)Reply(first)["conv"]!;
        var oldPid = session.CurrentProcessId;
        oldPid.Should().NotBeNull();

        var second = await TurnAsync(session, log, Settings("neterr", sessionId: conv, started: true), "第二則遇到wsasend斷線");
        var failedTurn = second.OfType<AgentTurnCompletedEvent>().Single();
        failedTurn.Outcome.Should().Be(AgentTurnOutcome.Failed);
        failedTurn.ServerBusy.Should().Be(AgentServerBusy.NetworkError);

        var exited = await log.UntilAsync(e => e is AgentProcessExitedEvent);
        exited.OfType<AgentProcessExitedEvent>().Single().Expected.Should().BeTrue("為清掉陳舊連線池主動回收舊程序");
        session.HasLiveProcess.Should().BeFalse();

        // 重試（或下一輪）送出時：啟動乾淨的新程序並以 --conversation 續接同一個對話
        var retried = await TurnAsync(session, log, Settings("neterr", sessionId: conv, started: true), "重試接續");
        retried.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.CurrentProcessId.Should().NotBeNull().And.NotBe(oldPid, "已換成新的 agy 程序與連線池");
        var reply = Reply(retried);
        Args(reply).Should().ContainInConsecutiveOrder("--conversation", conv);
        ((string)reply["text"]!).Should().Be("重試接續");
    }

    [Fact]
    public async Task API網路連線中斷_有存活背景任務時保留原程序不中斷背景工作()
    {
        using var session = NewSession();
        using var log = new EventLog(session);

        var first = await TurnAsync(session, log, Settings("bgneterr"), "啟動常駐監看");
        var conv = (string)JObject.Parse(first.OfType<AgentTextCompletedEvent>().Single().Text)["conv"]!;
        var pid = session.CurrentProcessId;
        session.HasLiveBackgroundWork.Should().BeTrue();

        var second = await TurnAsync(session, log, Settings("bgneterr", sessionId: conv, started: true), "查狀態時遇到wsasend");
        var failedTurn = second.OfType<AgentTurnCompletedEvent>().Single();
        failedTurn.Outcome.Should().Be(AgentTurnOutcome.Failed);
        failedTurn.ServerBusy.Should().Be(AgentServerBusy.NetworkError);

        session.HasLiveProcess.Should().BeTrue("有存活背景任務時不殺舊程序");
        session.CurrentProcessId.Should().Be(pid);
        session.HasLiveBackgroundWork.Should().BeTrue();

        var third = await TurnAsync(session, log, Settings("bgneterr", sessionId: conv, started: true), "同程序重試成功");
        third.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.CurrentProcessId.Should().Be(pid);
    }
}
