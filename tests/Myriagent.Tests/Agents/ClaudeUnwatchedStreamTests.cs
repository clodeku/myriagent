using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 有沒有人在看（SetWatched → set_has_subscribers）、安全機制換過模型之後換回去要再送一次 set_model、插入的訊息被讀到（AgentSteerReadEvent）：
/// 以假的 claude -p（Windows PowerShell 腳本）確認會話送出的控制請求與回報的事件。不連網、不耗額度
/// </summary>
public sealed class ClaudeUnwatchedStreamTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// 假的 claude -p（只用 ASCII）：set_has_subscribers 記 watch=True／False、set_model 記 model=…；每則使用者訊息一輪，回覆文字是「log=到目前記下的」並清空。
    /// 訊息含 refuse 時先送 model_refusal_fallback；含 wait 時等下一則使用者訊息（插入的），照 command_lifecycle queued、started 回報它再結束
    /// </summary>
    private const string FakeClaude = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        $log = New-Object System.Collections.Generic.List[string]
        $log.Add('hold=' + $env:CLAUDE_CODE_STREAM_EVENT_HOLD_MS + '/' + $env:CLAUDE_CODE_STREAM_EVENT_NO_SUBSCRIBER_HOLD_MS)
        function Emit([string]$json) { $out.WriteLine($json) }
        function ReadUser {
            while ($true) {
                $line = $in.ReadLine()
                if ($line -eq $null) { exit 0 }
                if ($line.Trim().Length -eq 0) { continue }
                $msg = $line | ConvertFrom-Json
                if ($msg.type -eq 'user') { return $msg }
                if ($msg.type -ne 'control_request') { continue }
                $subtype = $msg.request.subtype
                if ($subtype -eq 'set_has_subscribers') { $log.Add('watch=' + $msg.request.has_subscribers) }
                if ($subtype -eq 'set_model') { $log.Add('model=' + $msg.request.model) }
                Emit ('{"type":"control_response","response":{"subtype":"success","request_id":"' + $msg.request_id + '","response":{}}}')
            }
        }
        while ($true) {
            $m = ReadUser
            $text = ($m.message.content | ForEach-Object { $_.text }) -join ''
            Emit '{"type":"system","subtype":"init","session_id":"fake-session","model":"fable","permissionMode":"default","apiKeySource":"none","claude_code_version":"fake"}'
            if ($text -match 'refuse') {
                Emit '{"type":"system","subtype":"model_refusal_fallback","trigger":"refusal","direction":"retry","scope":"session","original_model":"claude-fable-5-2","fallback_model":"claude-opus-4-8","api_refusal_category":"cyber","content":"x"}'
            }
            if ($text -match 'wait') {
                $s = ReadUser
                Emit ('{"type":"command_lifecycle","command_uuid":"' + $s.uuid + '","state":"queued"}')
                Emit ('{"type":"command_lifecycle","command_uuid":"' + $s.uuid + '","state":"started"}')
            }
            Emit ('{"type":"assistant","message":{"id":"m","content":[{"type":"text","text":"log=' + [string]::Join(',', $log) + '"}]},"parent_tool_use_id":null}')
            $log.Clear()
            Emit '{"type":"result","subtype":"success","is_error":false,"result":"done","session_id":"fake-session"}'
        }
        """";

    private readonly TempDirectory temp = new("fake-cli");

    public void Dispose() => temp.Dispose();

    private AgentSessionSettings Settings()
    {
        var script = temp.WriteFile("fake-claude-watch.ps1", FakeClaude);
        return new AgentSessionSettings(
            Guid.NewGuid().ToString(),
            false,
            new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            "fake",
            _ => { },
            null,
            "fable",
            null,
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
    public async Task 沒人看時告訴CLI_有人看再說一次_沒變不重送_新程序也會再說()
    {
        using var session = new ClaudeAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        var seen = new List<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);

        // 程序還沒啟動就沒人看：啟動後補說一次
        session.SetWatched(false);
        session.SetWatched(false);
        var settings = Settings();
        await session.SendAsync(Input("one"), settings);
        (await WaitForAsync<AgentTextCompletedEvent>(events, seen)).Text.Should().Be("log=hold=50/1000,watch=False", "環境變數帶著合併的時間，啟動後說一次沒人看");
        await WaitForAsync<AgentTurnCompletedEvent>(events, seen);

        session.SetWatched(true);
        session.SetWatched(true);
        await Task.Delay(200);
        await session.SendAsync(Input("two"), settings);
        (await WaitForAsync<AgentTextCompletedEvent>(events, seen)).Text.Should().Be("log=watch=True", "只有改變時才送");
    }

    [Fact]
    public async Task 安全機制換過模型_同一個模型也要再送一次set_model_送過就不再送()
    {
        using var session = new ClaudeAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        var seen = new List<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);

        var settings = Settings();
        await session.SendAsync(Input("refuse"), settings);
        var refusal = await WaitForAsync<AgentModelRefusalEvent>(events, seen);
        refusal.SessionSwitched.Should().BeTrue();
        refusal.FallbackModel.Should().Be("claude-opus-4-8");
        await WaitForAsync<AgentTurnCompletedEvent>(events, seen);

        (await session.SetModelAsync("fable", null)).Should().BeTrue();
        (await session.SetModelAsync("fable", null)).Should().BeTrue("已經換回來：和目前的設定相同，不必再送");
        await session.SendAsync(Input("two"), settings);
        (await WaitForAsync<AgentTextCompletedEvent>(events, seen)).Text.Should().Be("log=model=fable");
    }

    [Fact]
    public async Task 插入的訊息被讀到_回報一次()
    {
        using var session = new ClaudeAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        var seen = new List<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);

        await session.SendAsync(Input("wait"), Settings());
        await WaitForAsync<AgentSessionStartedEvent>(events, seen);
        var uuid = Guid.NewGuid().ToString();
        (await session.SteerAsync(Input("插入") with { MessageUuid = uuid })).Should().BeTrue();
        (await WaitForAsync<AgentSteerReadEvent>(events, seen)).MessageUuid.Should().Be(uuid);
        seen.OfType<AgentSteerReadEvent>().Should().ContainSingle();
    }
}
