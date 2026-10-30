using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 執行中切換權限模式（審查 hub-core#6）：以假的 claude -p（Windows PowerShell 腳本，照實測的 stream-json 格式）確認
/// 會話在一輪進行中就送出 set_permission_mode（不必等下一輪），不連網、不耗額度。
/// </summary>
public sealed class ClaudePermissionSwitchTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// 假的 claude -p：等使用者訊息，送一個 can_use_tool 請求讓這一輪停在等核准；等待期間收到的控制請求一律回成功，
    /// set_permission_mode 記下模式，最後以一段回覆文字回報收到的模式
    /// </summary>
    private const string FakeClaude = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $log = New-Object System.Collections.Generic.List[string]
        function Emit([string]$json) { $out.WriteLine($json) }

        function ReadMessage {
            while ($true) {
                $line = $in.ReadLine()
                if ($line -eq $null) { exit 0 }
                if ($line.Trim().Length -eq 0) { continue }
                $msg = $line | ConvertFrom-Json
                if ($msg.type -eq 'control_request') {
                    if ($msg.request.subtype -eq 'set_permission_mode') { $log.Add('mode=' + $msg.request.mode) }
                    Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{}}}')
                    continue
                }
                return $msg
            }
        }

        while ($true) { $m = ReadMessage; if ($m.type -eq 'user') { break } }
        Emit '{"type":"system","subtype":"init","session_id":"fake-session","model":"fake","permissionMode":"default","apiKeySource":"none","claude_code_version":"fake"}'
        Emit '{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"echo a"},"permission_suggestions":[],"tool_use_id":"tu_r1"}}'
        while ($true) {
            $msg = ReadMessage
            if ($msg.type -eq 'control_response' -and $msg.response.request_id -eq 'r1') { $log.Add('r1=' + $msg.response.response.behavior); break }
        }
        Emit ('{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"' + [string]::Join(',', $log) + '"}]},"parent_tool_use_id":null}')
        Emit '{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"fake-session"}'
        while ($in.ReadLine() -ne $null) { }
        """";

    private readonly TempDirectory temp = new("fake-cli");

    public void Dispose() => temp.Dispose();

    private AgentSessionSettings Settings()
    {
        var script = temp.WriteFile("fake-claude-mode.ps1", FakeClaude);
        return new AgentSessionSettings(
            Guid.NewGuid().ToString(),
            false,
            new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            "fake",
            _ => { },
            null,
            null,
            null,
            AgentPermissionMode.Ask,
            temp.CreateDirectory("work"),
            temp.Root,
            "測試說明",
            UseLocalCustomizations: true);
    }

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
    public async Task 一輪進行中切換權限模式_立即送出set_permission_mode_程序還沒啟動時回false()
    {
        using var session = new ClaudeAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        var seen = new List<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);

        (await session.SetPermissionModeAsync(AgentPermissionMode.FullAccess)).Should().BeFalse("程序還沒啟動，只能等下一則訊息");

        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), Settings());
        var approval = await WaitForAsync<AgentApprovalEvent>(events, seen);

        (await session.SetPermissionModeAsync(AgentPermissionMode.FullAccess)).Should().BeTrue("這一輪還在進行（停在等核准），CLI 收到切換並回應成功");
        (await session.SetPermissionModeAsync(AgentPermissionMode.FullAccess)).Should().BeTrue("已經是這個模式就不必再送");
        await session.RespondApprovalAsync(approval.RequestKey, AgentApprovalChoice.Allow, null);

        var reply = await WaitForAsync<AgentTextCompletedEvent>(events, seen);
        reply.Text.Should().Be("mode=bypassPermissions,r1=allow", "切換在這一輪中途就送到 CLI，而且只送一次");
        (await WaitForAsync<AgentTurnCompletedEvent>(events, seen)).Outcome.Should().Be(AgentTurnOutcome.Completed);
    }
}
