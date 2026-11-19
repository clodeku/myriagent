using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 各家代理共用記憶（Accounts.SharedMemoryRules）在 Codex 會話的接法：以假的 codex app-server（PowerShell）記下 thread/start 的
/// developerInstructions 與 turn/start 的 sandboxPolicy.writableRoots，最後以一段回覆文字回報。不連網、不耗額度
/// </summary>
public sealed class CodexSharedMemoryFakeCliTests : IDisposable
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private const string FakeCodex = """"
        $ErrorActionPreference = 'Stop'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $in = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), $utf8)
        $out = New-Object System.IO.StreamWriter([Console]::OpenStandardOutput(), $utf8)
        $out.NewLine = [string][char]10
        $out.AutoFlush = $true
        function Emit([string]$json) { $out.WriteLine($json) }
        $script:turnStarted = $false
        $script:instructions = ''
        $script:roots = ''

        while (-not $script:turnStarted) {
            $line = $in.ReadLine()
            if ($line -eq $null) { exit 0 }
            if ($line.Trim().Length -eq 0) { continue }
            $msg = $line | ConvertFrom-Json
            if (-not $msg.method -or $msg.id -eq $null) { continue }
            $id = $msg.id
            switch ($msg.method) {
                'initialize' { Emit ('{"id":' + $id + ',"result":{"userAgent":"fake","codexHome":"fake"}}') }
                'windowsSandbox/readiness' { Emit ('{"id":' + $id + ',"result":{"status":"ready"}}') }
                'thread/start' {
                    $script:instructions = [string]$msg.params.developerInstructions
                    Emit ('{"id":' + $id + ',"result":{"thread":{"id":"thread-fake"},"model":"fake"}}')
                }
                'turn/start' {
                    $script:roots = [string]::Join('|', @($msg.params.sandboxPolicy.writableRoots))
                    Emit ('{"id":' + $id + ',"result":{"turn":{"id":"turn-1","items":[],"status":"inProgress"}}}')
                    $script:turnStarted = $true
                }
                default { Emit ('{"id":' + $id + ',"result":{}}') }
            }
        }
        Emit '{"method":"turn/started","params":{"threadId":"thread-fake","turn":{"id":"turn-1","status":"inProgress"}}}'
        $report = [ordered]@{ instructions = $script:instructions; roots = $script:roots } | ConvertTo-Json -Compress
        $item = [ordered]@{ method = 'item/completed'; params = [ordered]@{ item = [ordered]@{ type = 'agentMessage'; id = 'msg1'; text = $report }; threadId = 'thread-fake'; turnId = 'turn-1' } }
        Emit ($item | ConvertTo-Json -Compress -Depth 6)
        Emit '{"method":"turn/completed","params":{"threadId":"thread-fake","turn":{"id":"turn-1","items":[],"status":"completed","error":null}}}'
        while ($in.ReadLine() -ne $null) { }
        """";

    private readonly TempDirectory temp = new("codex-shared-memory");

    public void Dispose() => temp.Dispose();

    private AgentSessionSettings Settings(Func<string, AgentSharedLaunch>? prepareShared)
    {
        var script = temp.WriteFile("fake-codex.ps1", FakeCodex);
        return new AgentSessionSettings(
            null,
            false,
            new AgentLauncher(PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }),
            "fake",
            _ => { },
            null,
            null,
            null,
            AgentPermissionMode.Ask,
            temp.CreateDirectory("work"),
            temp.CreateDirectory("conversation"),
            "測試說明",
            UseLocalCustomizations: true,
            PrepareShared: prepareShared);
    }

    /// <summary>跑一輪，回傳假 CLI 回報的（developerInstructions、以 | 連起來的 writableRoots）</summary>
    private static async Task<(string Instructions, string[] Roots)> RunAsync(AgentSessionSettings settings)
    {
        using var session = new CodexAgentSession();
        var events = new BlockingCollection<AgentEvent>();
        session.EventReceived += ev => events.Add(ev);
        await session.SendAsync(new AgentTurnInput("開始", Array.Empty<AgentAttachment>()), settings);

        string? report = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (!events.TryTake(out var ev, TimeSpan.FromMilliseconds(200)))
                continue;
            if (ev is AgentTextCompletedEvent text)
                report = text.Text;
            if (ev is AgentTurnCompletedEvent)
                break;
        }
        report.Should().NotBeNull("假的 CLI 要在時限內回報");
        var json = Newtonsoft.Json.Linq.JObject.Parse(report!);
        return ((string)json["instructions"]!, ((string)json["roots"]!).Split('|', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task 有共用記憶_說明接在developerInstructions後面_記憶資料夾是沙箱的可寫資料夾()
    {
        var memory = temp.CreateDirectory("memory");
        var settings = Settings(_ => AgentSharedLaunch.None with { SharedMemoryDirectory = memory, SharedMemoryInstructions = "\n\n# 記憶\n\n（測試的索引）" });

        var (instructions, roots) = await RunAsync(settings);

        instructions.Should().Be("測試說明\n\n# 記憶\n\n（測試的索引）");
        roots.Should().Equal(settings.ConversationDirectory, memory);
    }

    [Fact]
    public async Task 沒有共用記憶_說明與可寫資料夾照舊()
    {
        var settings = Settings(null);

        var (instructions, roots) = await RunAsync(settings);

        instructions.Should().Be("測試說明");
        roots.Should().Equal(settings.ConversationDirectory);
    }
}
