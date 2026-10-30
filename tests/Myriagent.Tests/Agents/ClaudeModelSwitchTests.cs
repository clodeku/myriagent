using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 執行中換模型與推理強度：以假的 claude -p（Windows PowerShell 腳本，照實測 2.1.309 的 set_model／apply_flag_settings 格式）確認
/// 會話在一輪進行中就送出（不必等下一輪），成功後下一輪沿用同一個程序；CLI 拒絕時下一輪照舊重開程序套用。不連網、不耗額度。
/// </summary>
public sealed class ClaudeModelSwitchTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// 假的 claude -p（只用 ASCII）：每則使用者訊息一輪，送一個 can_use_tool 請求讓這一輪停在等核准；
    /// set_model 記下模型（"reject-me" 照實測回 not_found 錯誤），apply_flag_settings 記下 effortLevel（null 記成 default；
    /// env 的 CLAUDE_CODE_EFFORT_LEVEL 不是同值〔ultracode 是 xhigh〕時另記 badenv，帶 ultracode 時另記），
    /// initialize 回兩個模型、get_settings 回能用 ultracode，回覆文字是「turn=第幾輪;收到的切換與核准」
    /// </summary>
    private const string FakeClaude = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $argList = @($args)
        $log = New-Object System.Collections.Generic.List[string]
        function Emit([string]$json) { $out.WriteLine($json) }
        function ArgValue([string]$name) {
            $i = [array]::IndexOf($argList, $name)
            if ($i -ge 0 -and $i + 1 -lt $argList.Count) { return $argList[$i + 1] }
            return $null
        }
        $script:model = ArgValue '--model'
        if (-not $script:model) { $script:model = 'default' }

        function HandleControl($msg) {
            $subtype = $msg.request.subtype
            if ($subtype -eq 'set_model') {
                $m = $msg.request.model
                if ($m -eq 'reject-me') {
                    Emit ('{"type":"control_response","response":{"subtype":"error","request_id":"' + $msg.request_id + '","error":"Model ''reject-me'' not found","error_code":"not_found","model":"' + $script:model + '"}}')
                    return
                }
                if ($m -eq $null) { $m = 'default' }
                $script:model = $m
                $log.Add('model=' + $m)
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{"model":"resolved-' + $m + '"}}}')
                return
            }
            if ($subtype -eq 'apply_flag_settings') {
                $s = $msg.request.settings
                $e = $s.effortLevel
                $expected = $e
                if ($e -eq 'ultracode') { $expected = 'xhigh' }
                $level = $null
                if ($s.env -ne $null) { $level = $s.env.CLAUDE_CODE_EFFORT_LEVEL }
                if ($e -eq $null) { $e = 'default' }
                $log.Add('effort=' + $e)
                if ($level -ne $expected) { $log.Add('badenv=' + $level) }
                if ($s.PSObject.Properties['ultracode'] -ne $null) { $log.Add('ultracode=' + $s.ultracode) }
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '"}}')
                return
            }
            if ($subtype -eq 'initialize') {
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{"models":[{"value":"opus","displayName":"Opus","supportedEffortLevels":["low","high","xhigh","max"]},{"value":"claude-opus-4-6","displayName":"Opus 4.6","supportedEffortLevels":["low","high","max"]}]}}}')
                return
            }
            if ($subtype -eq 'get_settings') {
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{"applied":{"effort":"high","ultracode":false,"ultracodeAvailable":true}}}}')
                return
            }
            Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{}}}')
        }

        function ReadMessage {
            while ($true) {
                $line = $in.ReadLine()
                if ($line -eq $null) { exit 0 }
                if ($line.Trim().Length -eq 0) { continue }
                $msg = $line | ConvertFrom-Json
                if ($msg.type -eq 'control_request') { HandleControl $msg; continue }
                return $msg
            }
        }

        $turn = 0
        while ($true) {
            while ($true) { $m = ReadMessage; if ($m.type -eq 'user') { break } }
            $turn++
            $rid = 'r' + $turn
            Emit ('{"type":"system","subtype":"init","session_id":"fake-session","model":"' + $script:model + '","permissionMode":"default","apiKeySource":"none","claude_code_version":"fake"}')
            Emit ('{"type":"control_request","request_id":"' + $rid + '","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"echo a"},"permission_suggestions":[],"tool_use_id":"tu_' + $rid + '"}}')
            while ($true) {
                $msg = ReadMessage
                if ($msg.type -eq 'control_response' -and $msg.response.request_id -eq $rid) { $log.Add($rid + '=' + $msg.response.response.behavior); break }
            }
            Emit ('{"type":"assistant","message":{"id":"m' + $turn + '","model":"' + $script:model + '","content":[{"type":"text","text":"turn=' + $turn + ';' + [string]::Join(',', $log) + '"}]},"parent_tool_use_id":null}')
            $log.Clear()
            Emit '{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"fake-session"}'
        }
        """";

    private readonly TempDirectory temp = new("fake-cli");

    public void Dispose() => temp.Dispose();

    private AgentSessionSettings Settings(string? model, string? effort)
    {
        var script = temp.WriteFile("fake-claude-model.ps1", FakeClaude);
        return new AgentSessionSettings(
            Guid.NewGuid().ToString(),
            false,
            new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            "fake",
            _ => { },
            null,
            model,
            effort,
            AgentPermissionMode.Ask,
            temp.CreateDirectory("work"),
            temp.Root,
            "測試說明",
            UseLocalCustomizations: true);
    }

    private static AgentTurnInput Input(string text) => new(text, Array.Empty<AgentAttachment>());

    private static async Task<T> WaitForAsync<T>(BlockingCollection<AgentEvent> events, List<AgentEvent> seen) where T : AgentEvent
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!events.TryTake(out var ev, TimeSpan.FromMilliseconds(200)))
                continue;
            seen.Add(ev);
            if (ev is T match)
                return match;
        }
        throw new TimeoutException($"假的 CLI 沒有在時限內送出 {typeof(T).Name}；收到：" + string.Join("、", seen.Select(e => e.GetType().Name)));
    }

    [Fact]
    public async Task 一輪進行中換模型與推理強度_立即送給CLI_下一輪沿用同一個程序()
    {
        using var session = new ClaudeAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        var seen = new List<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);

        (await session.SetModelAsync("sonnet", "high")).Should().BeFalse("程序還沒啟動，只能等下一則訊息");

        var settings = Settings("haiku", null);
        await session.SendAsync(Input("第一則"), settings);
        var approval = await WaitForAsync<AgentApprovalEvent>(events, seen);
        var instance = session.ProcessInstance;

        (await session.SetModelAsync("sonnet", "high")).Should().BeTrue("這一輪還在進行（停在等核准），CLI 收到切換並回應成功");
        (await session.SetModelAsync("sonnet", "high")).Should().BeTrue("和目前程序的設定相同就不必再送");
        await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        (await WaitForAsync<AgentTextCompletedEvent>(events, seen)).Text
            .Should().Be("turn=1;model=sonnet,effort=high,r1=allow", "模型與強度在這一輪中途就送到 CLI，而且各只送一次");
        (await WaitForAsync<AgentTurnCompletedEvent>(events, seen)).Outcome.Should().Be(AgentTurnOutcome.Completed);

        // 下一輪照中樞的設定（已經是新的模型與強度）：不必重開程序
        await session.SendAsync(Input("第二則"), settings with { Model = "sonnet", Effort = "high" });
        approval = await WaitForAsync<AgentApprovalEvent>(events, seen);
        session.ProcessInstance.Should().Be(instance, "已即時套用的模型與強度不算設定改變");

        // 換回預設：模型與強度都送 null（CLI 回到帳戶預設模型與模型的預設強度，同不帶 --model、--effort）
        (await session.SetModelAsync(null, null)).Should().BeTrue();
        await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        (await WaitForAsync<AgentTextCompletedEvent>(events, seen)).Text.Should().Be("turn=2;model=default,effort=default,r2=allow");
        await WaitForAsync<AgentTurnCompletedEvent>(events, seen);

        await session.SendAsync(Input("第三則"), settings with { Model = null, Effort = null });
        await WaitForAsync<AgentApprovalEvent>(events, seen);
        session.ProcessInstance.Should().Be(instance);
    }

    [Fact]
    public async Task CLI拒絕換模型_回false_下一輪重開程序以新模型啟動()
    {
        using var session = new ClaudeAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        var seen = new List<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);

        var settings = Settings("haiku", "high");
        await session.SendAsync(Input("第一則"), settings);
        var approval = await WaitForAsync<AgentApprovalEvent>(events, seen);
        var instance = session.ProcessInstance;

        (await session.SetModelAsync("reject-me", "high")).Should().BeFalse("CLI 回 not_found：這一輪仍用原本的模型");
        await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        (await WaitForAsync<AgentTextCompletedEvent>(events, seen)).Text.Should().Be("turn=1;r1=allow", "被拒的模型沒有記成目前程序的設定，強度沒變也不送");
        await WaitForAsync<AgentTurnCompletedEvent>(events, seen);

        await session.SendAsync(Input("第二則"), settings with { Model = "reject-me" });
        await WaitForAsync<AgentApprovalEvent>(events, seen);
        session.ProcessInstance.Should().NotBe(instance, "沒有即時套用成功：下一輪照舊重開程序、以 --model 帶上新模型");
    }

    [Fact]
    public async Task Ultracode_模型清單等GetSettings才回報_換走時明確關掉()
    {
        using var session = new ClaudeAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        var seen = new List<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);

        var settings = Settings("opus", "high");
        await session.SendAsync(Input("第一則"), settings);
        var models = await WaitForAsync<AgentModelsEvent>(events, seen);
        models.Models.Single(m => m.Value == "opus").Efforts.Should().Equal("low", "high", "xhigh", "max", "ultracode");
        models.Models.Single(m => m.Value == "claude-opus-4-6").Efforts.Should().Equal(new[] { "low", "high", "max" }, "不支援 xhigh 的模型不列");
        seen.OfType<AgentModelsEvent>().Should().ContainSingle("initialize 與 get_settings 都到了才回報一次");

        var approval = await WaitForAsync<AgentApprovalEvent>(events, seen);
        (await session.SetModelAsync("opus", "ultracode")).Should().BeTrue();
        await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        (await WaitForAsync<AgentTextCompletedEvent>(events, seen)).Text
            .Should().Be("turn=1;effort=ultracode,r1=allow", "環境變數一起改成 xhigh，開啟不必另帶 ultracode");
        await WaitForAsync<AgentTurnCompletedEvent>(events, seen);

        await session.SendAsync(Input("第二則"), settings with { Effort = "ultracode" });
        approval = await WaitForAsync<AgentApprovalEvent>(events, seen);
        (await session.SetModelAsync("opus", "xhigh")).Should().BeTrue();
        await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);
        (await WaitForAsync<AgentTextCompletedEvent>(events, seen)).Text
            .Should().Be("turn=2;effort=xhigh,ultracode=False,r2=allow", "實測換到同樣是 xhigh 的強度時 CLI 不會自己關掉 ultracode");
    }
}
