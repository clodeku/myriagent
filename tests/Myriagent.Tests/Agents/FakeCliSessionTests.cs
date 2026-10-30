using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 以假的 CLI 程序（Windows PowerShell 腳本，照實測的 stream-json／JSON-RPC 格式送出請求）測會話層的核准邏輯，不連網、不耗額度：
/// 「本對話都允許」只對檔案編輯整類放行；指令類只放行完全相同的請求（Codex 還要同一個工作資料夾），不同的指令仍要跳核准卡。
/// 假 CLI 把收到的每個回應記下來，最後以一段回覆文字回報，測試據此確認會話自動回應了哪些請求。
/// </summary>
public sealed class FakeCliSessionTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private const string CommonIo = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $log = New-Object System.Collections.Generic.List[string]
        function Emit([string]$json) { $out.WriteLine($json) }

        """";

    /// <summary>假的 claude -p（雙向 stream-json）：等使用者訊息，依序送 5 個 can_use_tool 請求，最後回報收到的回應</summary>
    private const string FakeClaude = CommonIo + """"
        function ReadMessage {
            while ($true) {
                $line = $in.ReadLine()
                if ($line -eq $null) { exit 0 }
                if ($line.Trim().Length -eq 0) { continue }
                $msg = $line | ConvertFrom-Json
                if ($msg.type -eq 'control_request') {
                    Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{}}}')
                    continue
                }
                return $msg
            }
        }

        function AskPermission([string]$id, [string]$tool, [string]$inputJson, [string]$suggestions) {
            Emit ('{"type":"control_request","request_id":"' + $id + '","request":{"subtype":"can_use_tool","tool_name":"' + $tool + '","input":' + $inputJson + ',"permission_suggestions":' + $suggestions + ',"tool_use_id":"tu_' + $id + '"}}')
            while ($true) {
                $msg = ReadMessage
                if ($msg.type -eq 'control_response' -and $msg.response.request_id -eq $id) {
                    $r = $msg.response.response
                    $entry = $id + '=' + $r.behavior
                    if ($r.updatedPermissions) { $entry += '/' + $r.updatedPermissions[0].destination }
                    $log.Add($entry)
                    return
                }
            }
        }

        while ($true) { $m = ReadMessage; if ($m.type -eq 'user') { break } }
        Emit '{"type":"system","subtype":"init","session_id":"fake-session","model":"fake","permissionMode":"default","apiKeySource":"none","claude_code_version":"fake"}'
        $bashRule = '[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"echo a"}],"behavior":"allow","destination":"localSettings"}]'
        AskPermission 'r1' 'Bash' '{"command":"echo a"}' $bashRule
        AskPermission 'r2' 'Bash' '{"command":"echo b"}' '[]'
        AskPermission 'r3' 'Bash' '{"command":"echo a"}' $bashRule
        AskPermission 'r4' 'Edit' '{"file_path":"C:/w/a.txt","old_string":"x","new_string":"y"}' '[]'
        AskPermission 'r5' 'Write' '{"file_path":"C:/w/b.txt","content":"z"}' '[]'
        Emit ('{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"' + [string]::Join(',', $log) + '"}]},"parent_tool_use_id":null}')
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"fake-session"}'
        while ($in.ReadLine() -ne $null) { }
        """";

    /// <summary>假的 codex app-server（JSON-RPC）：回應初始化與建立對話，turn/start 後送 6 個核准請求，最後回報收到的決定</summary>
    private const string FakeCodex = CommonIo + """"
        $script:turnStarted = $false

        function Respond($msg) {
            $id = $msg.id
            switch ($msg.method) {
                'initialize' { Emit ('{"id":' + $id + ',"result":{"userAgent":"fake","codexHome":"fake"}}') }
                'windowsSandbox/readiness' { Emit ('{"id":' + $id + ',"result":{"status":"ready"}}') }
                'thread/start' { Emit ('{"id":' + $id + ',"result":{"thread":{"id":"thread-fake"},"model":"fake"}}') }
                'turn/start' {
                    Emit ('{"id":' + $id + ',"result":{"turn":{"id":"turn-1","items":[],"status":"inProgress"}}}')
                    $script:turnStarted = $true
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
                if ($r -ne $null -and $r.id -eq $id) { $log.Add([string]$id + '=' + $r.result.decision); return }
            }
        }

        function AskCommand([int]$id, [string]$command, [string]$cwd) {
            Emit ('{"method":"item/commandExecution/requestApproval","id":' + $id + ',"params":{"threadId":"thread-fake","turnId":"turn-1","itemId":"e' + $id + '","command":"' + $command + '","cwd":"' + $cwd + '","commandActions":[{"type":"unknown","command":"' + $command + '"}],"availableDecisions":["accept","acceptForSession","decline","cancel"]}}')
            WaitFor $id
        }

        function AskFileChange([int]$id, [string]$itemId) {
            Emit ('{"method":"item/fileChange/requestApproval","id":' + $id + ',"params":{"threadId":"thread-fake","turnId":"turn-1","itemId":"' + $itemId + '"}}')
            WaitFor $id
        }

        while (-not $script:turnStarted) { $null = Pump }
        Emit '{"method":"turn/started","params":{"threadId":"thread-fake","turn":{"id":"turn-1","status":"inProgress"}}}'
        AskCommand 100 'echo a' 'C:/w1'
        AskCommand 101 'echo b' 'C:/w1'
        AskCommand 102 'echo a' 'C:/w1'
        AskCommand 103 'echo a' 'C:/w2'
        AskFileChange 104 'fc1'
        AskFileChange 105 'fc2'
        Emit ('{"method":"item/completed","params":{"item":{"type":"agentMessage","id":"msg1","text":"' + [string]::Join(',', $log) + '"},"threadId":"thread-fake","turnId":"turn-1"}}')
        Emit '{"method":"turn/completed","params":{"threadId":"thread-fake","turn":{"id":"turn-1","items":[],"status":"completed","error":null}}}'
        while ($in.ReadLine() -ne $null) { }
        """";

    private readonly TempDirectory temp = new("fake-cli");

    public void Dispose() => temp.Dispose();

    private AgentSessionSettings Settings(AgentProvider provider)
    {
        var script = temp.WriteFile(provider == AgentProvider.Claude ? "fake-claude.ps1" : "fake-codex.ps1", provider == AgentProvider.Claude ? FakeClaude : FakeCodex);
        var work = temp.CreateDirectory("work");
        return new AgentSessionSettings(
            provider == AgentProvider.Claude ? Guid.NewGuid().ToString() : null,
            false,
            new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            "fake",
            _ => { },
            null,
            null,
            null,
            AgentPermissionMode.Ask,
            work,
            temp.Root,
            "測試說明",
            UseLocalCustomizations: true);
    }

    /// <summary>跑一輪：核准卡依 decide 回應；回傳這一輪的事件</summary>
    private static async Task<List<AgentEvent>> RunTurnAsync(IAgentSession session, AgentSessionSettings settings, Func<AgentApprovalEvent, AgentApprovalChoice> decide)
    {
        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), settings);

        var collected = new List<AgentEvent>();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!events.TryTake(out var ev, TimeSpan.FromMilliseconds(200)))
                continue;
            collected.Add(ev);
            if (ev is AgentApprovalEvent approval)
                await session.RespondApprovalAsync(approval.RequestKey, decide(approval), null);
            if (ev is AgentTurnCompletedEvent)
                return collected;
        }
        throw new TimeoutException("假的 CLI 沒有在時限內結束這一輪；收到：" + string.Join("、", collected.Select(e => e.GetType().Name)));
    }

    [Fact]
    public async Task Claude_本對話都允許後_不同的指令仍要核准_完全相同的才自動放行_CLI還會詢問的檔案編輯不整類放行()
    {
        using var session = new ClaudeAgentSession();

        var events = await RunTurnAsync(session, Settings(AgentProvider.Claude),
            a => a.RequestKey is "r1" or "r4" ? AgentApprovalChoice.AllowForSession : AgentApprovalChoice.Allow);

        var approvals = events.OfType<AgentApprovalEvent>().ToList();
        approvals.Select(a => a.RequestKey).Should().Equal(new[] { "r1", "r2", "r4", "r5" },
            "r2 是不同的 Bash 指令要再問；r3 與 r1 完全相同自動放行；r5 是 CLI 還會送來詢問的寫入（沒有附建議，例如工作資料夾外或敏感檔），不能因為 r4 按過就放行");
        approvals[0].AllowSessionLabel.Should().Be("本對話都允許 Bash(echo a)");
        approvals[1].AllowSessionLabel.Should().Be("本對話同樣的操作都允許");
        approvals[2].AllowSessionLabel.Should().Be("本對話同樣的操作都允許", "CLI 沒有建議切換模式時，編輯也只放行完全相同的請求");
        events.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("r1=allow/session,r2=allow,r3=allow,r4=allow,r5=allow",
            "假 CLI 收到的回應：r1 的建議規則改成只在本次對話有效（不寫進使用者設定檔），r3 由會話自動回應");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Claude_只按允許時_同樣的指令下次照樣要問()
    {
        using var session = new ClaudeAgentSession();

        var events = await RunTurnAsync(session, Settings(AgentProvider.Claude), _ => AgentApprovalChoice.Allow);

        events.OfType<AgentApprovalEvent>().Select(a => a.RequestKey).Should().Equal("r1", "r2", "r3", "r4", "r5");
        events.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("r1=allow,r2=allow,r3=allow,r4=allow,r5=allow", "只允許這一次時不帶 session 規則");
    }

    [Fact]
    public async Task Codex_本對話都允許後_指令要完全相同且同一個工作資料夾才自動放行_檔案修改整類放行()
    {
        using var session = new CodexAgentSession();

        var events = await RunTurnAsync(session, Settings(AgentProvider.Codex),
            a => a.RequestKey is "100" or "104" ? AgentApprovalChoice.AllowForSession : AgentApprovalChoice.Allow);

        var approvals = events.OfType<AgentApprovalEvent>().ToList();
        approvals.Select(a => a.RequestKey).Should().Equal(new[] { "100", "101", "103", "104" },
            "101 指令不同、103 工作資料夾不同都要再問；102 完全相同、105 是檔案修改，自動放行");
        approvals[0].Title.Should().Be("echo a");
        approvals[0].AllowSessionLabel.Should().Be("本對話同樣的指令都允許");
        approvals[3].AllowSessionLabel.Should().Be("本對話的檔案修改都不再詢問");
        events.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be(
            "100=acceptForSession,101=accept,102=acceptForSession,103=accept,104=acceptForSession,105=acceptForSession");
        events.OfType<AgentSessionStartedEvent>().Single().SessionId.Should().Be("thread-fake");
        events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
    }

    [Fact]
    public async Task Codex_工作資料夾不存在時不偷偷建立_丟DirectoryNotFoundException()
    {
        using var codex = new CodexAgentSession();
        var settings = Settings(AgentProvider.Codex) with { WorkingDirectory = temp.Combine("不存在的資料夾") };

        await FluentActions.Awaiting(() => codex.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), settings))
            .Should().ThrowAsync<DirectoryNotFoundException>();

        Directory.Exists(settings.WorkingDirectory).Should().BeFalse();
        codex.IsRunning.Should().BeFalse("送出失敗後可以再送");
    }

    [Fact]
    public async Task Claude_工作資料夾不存在時不偷偷建立()
    {
        using var session = new ClaudeAgentSession();
        var settings = Settings(AgentProvider.Claude) with { WorkingDirectory = temp.Combine("不存在的資料夾") };

        await FluentActions.Awaiting(() => session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), settings))
            .Should().ThrowAsync<Exception>();

        Directory.Exists(settings.WorkingDirectory).Should().BeFalse();
        session.IsRunning.Should().BeFalse("送出失敗後可以再送");
    }

    [Fact]
    public async Task Claude_工作資料夾不存在時_錯誤要說出是資料夾不存在()
    {
        using var session = new ClaudeAgentSession();
        var settings = Settings(AgentProvider.Claude) with { WorkingDirectory = temp.Combine("不存在的資料夾") };

        var thrown = await FluentActions.Awaiting(() => session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), settings))
            .Should().ThrowAsync<Exception>();

        thrown.Which.Should().Match<Exception>(e => e is DirectoryNotFoundException || e.Message.Contains("工作資料夾不存在"));
    }
}
