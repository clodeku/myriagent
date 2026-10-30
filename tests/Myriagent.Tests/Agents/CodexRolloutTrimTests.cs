using System.Text;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 續接前的 rollout 截尾（設計 7.4.4，審查 codex-restart-1）：自己的 app-server 寫到一半就結束（當掉、停止的看門狗殺樹），使用者馬上重送時，
/// 最近還有寫入、檔尾又是半行——等到安靜期滿再看一次，沒有新的寫入就截掉（不然 thread/resume 與之後的寫入會接在半行後面，成為永久壞行）；
/// 一直有程式在寫就照舊不動。假 app-server 照〔CX〕每次 resume 都附加一行，thread/resume 時記下 rollout 是否以 LF 結尾
/// </summary>
public sealed class CodexRolloutTrimTests : IDisposable
{
    private readonly TempDirectory temp = new("codex-rollout-trim");

    public void Dispose() => temp.Dispose();

    /// <summary>假的 codex app-server（只用 ASCII）：crash＝第一個程序在 turn/start 後寫了半行就結束</summary>
    private const string Fake = """"
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
        function Append([string]$text) { [System.IO.File]::AppendAllText($rolloutPath, $text, $utf8) }
        $script:turnCount = 0
        while ($true) {
            $line = $in.ReadLine()
            if ($line -eq $null) { exit 0 }
            if ($line.Trim().Length -eq 0) { continue }
            $msg = $line | ConvertFrom-Json
            if (-not $msg.method) { continue }
            if ($msg.id -eq $null) { continue }
            $id = $msg.id
            switch ($msg.method) {
                'initialize' { Emit ('{"id":' + $id + ',"result":{"userAgent":"myriagent/0.160.1 (Windows; x86_64) fake","codexHome":"' + (J $codexHome) + '"}}') }
                'thread/start' {
                    [System.IO.File]::WriteAllText($rolloutPath, '{"type":"session_meta"}' + [string][char]10)
                    Emit ('{"id":' + $id + ',"result":{"thread":{"id":"' + $tid + '","path":"' + (J $rolloutPath) + '"},"model":"fake"}}')
                }
                'thread/resume' {
                    $bytes = [System.IO.File]::ReadAllBytes($rolloutPath)
                    $lf = ($bytes.Length -gt 0) -and ($bytes[$bytes.Length - 1] -eq 10)
                    Log ('thread/resume rolloutEndsWithLf=' + $lf)
                    Append ('{"type":"thread_settings_applied","p":"' + $p + '"}' + [string][char]10)
                    Emit ('{"id":' + $id + ',"result":{"thread":{"id":"' + $msg.params.threadId + '","path":"' + (J $rolloutPath) + '"},"model":"fake"}}')
                }
                'turn/start' {
                    $script:turnCount++
                    $turnId = $p + '-turn-' + $script:turnCount
                    Emit ('{"id":' + $id + ',"result":{"turn":{"id":"' + $turnId + '","items":[],"status":"inProgress"}}}')
                    Emit ('{"method":"turn/started","params":{"threadId":"' + $tid + '","turn":{"id":"' + $turnId + '","status":"inProgress"}}}')
                    if ($n -eq 1 -and $scenario -eq 'crash') {
                        Start-Sleep -Milliseconds 300
                        Append '{"type":"response_item","payload":{"type":"message","content":"half'
                        exit 1
                    }
                    Append ('{"type":"turn_context","p":"' + $p + '"}' + [string][char]10)
                    Emit ('{"method":"item/completed","params":{"item":{"type":"agentMessage","id":"' + $p + '-msg","text":"' + $p + ' done"},"threadId":"' + $tid + '","turnId":"' + $turnId + '"}}')
                    Emit ('{"method":"turn/completed","params":{"threadId":"' + $tid + '","turn":{"id":"' + $turnId + '","items":[],"status":"completed","error":null}}}')
                }
                default { Emit ('{"id":' + $id + ',"result":{}}') }
            }
        }
        """";

    private sealed class FakeServer
    {
        /// <param name="underSessions">rollout 放在 CODEX_HOME\sessions 底下（新的會話物件不知道 thread.path，要靠搜尋找到它）</param>
        public FakeServer(TempDirectory temp, string scenario, bool underSessions = false)
        {
            var root = temp.CreateDirectory(scenario);
            Work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            Conversation = root;
            LogPath = Path.Combine(root, "log.txt");
            TailDirectory = Path.Combine(root, "partial");
            ThreadId = "thread-" + Guid.NewGuid().ToString("N")[..8];
            var sessions = Directory.CreateDirectory(Path.Combine(root, "codex-home", "sessions", "2026", "10", "10")).FullName;
            RolloutPath = underSessions ? Path.Combine(sessions, $"rollout-2026-10-10T08-00-00-{ThreadId}.jsonl") : Path.Combine(root, "rollout.jsonl");
            static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var header = new StringBuilder()
                .Append("$scenario = ").Append(Quote(scenario)).Append('\n')
                .Append("$logPath = ").Append(Quote(LogPath)).Append('\n')
                .Append("$counterPath = ").Append(Quote(Path.Combine(root, "count.txt"))).Append('\n')
                .Append("$rolloutPath = ").Append(Quote(RolloutPath)).Append('\n')
                .Append("$codexHome = ").Append(Quote(Path.Combine(root, "codex-home"))).Append('\n')
                .Append("$tid = ").Append(Quote(ThreadId)).Append('\n');
            Script = Path.Combine(root, "fake-codex.ps1");
            File.WriteAllText(Script, header + Fake);
        }

        public string Script { get; }

        public string Work { get; }

        public string Conversation { get; }

        public string LogPath { get; }

        public string RolloutPath { get; }

        public string TailDirectory { get; }

        public string ThreadId { get; }

        public AgentSessionSettings Settings(bool started) => new(
            started ? ThreadId : null,
            started,
            new AgentLauncher(CodexAgentSessionTests.PowerShell, new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script }),
            "fake",
            _ => { },
            null,
            null,
            null,
            AgentPermissionMode.FullAccess,
            Work,
            Conversation,
            "測試說明",
            UseLocalCustomizations: true);

        public CodexAgentSession NewSession() => new() { TailBackupDirectory = TailDirectory };

        public string[] Log()
        {
            if (!File.Exists(LogPath))
                return Array.Empty<string>();
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>rollout 裡解析不了的行</summary>
        public List<string> BadLines()
            => File.ReadAllText(RolloutPath, Encoding.UTF8).Split('\n').Where(l => l.Length > 0 && !IsJson(l)).ToList();

        public string[] Backups() => Directory.Exists(TailDirectory) ? Directory.GetFiles(TailDirectory, "*.tail") : Array.Empty<string>();

        private static bool IsJson(string line)
        {
            try
            {
                JObject.Parse(line);
                return true;
            }
            catch (Newtonsoft.Json.JsonReaderException)
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task 自己的程序寫到一半就結束_使用者馬上重送_等安靜期滿確認沒有程式在寫就截掉半行再續接()
    {
        var fake = new FakeServer(temp, "crash");
        using var session = fake.NewSession();
        var events = CodexAgentSessionTests.Collect(session);
        await session.SendAsync(new AgentTurnInput("first", Array.Empty<AgentAttachment>()), fake.Settings(started: false));
        var first = await CodexAgentSessionTests.TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent);
        first.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Failed, "app-server 當掉");

        // 使用者看到失敗後馬上重送（距最後一次寫入不到 5 秒）
        await session.SendAsync(new AgentTurnInput("again", Array.Empty<AgentAttachment>()), fake.Settings(started: true));
        var second = await CodexAgentSessionTests.TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent, timeout: TimeSpan.FromSeconds(60));

        second.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Completed);
        fake.Log().Should().Contain("p2:thread/resume rolloutEndsWithLf=True", "thread/resume 之前截掉了自己的程序寫到一半的尾行");
        var backup = fake.Backups().Should().ContainSingle("切下的半行另存成 .tail 備份").Subject;
        File.ReadAllText(backup).Should().Contain("\"half");
        fake.BadLines().Should().BeEmpty("續接附加的行沒有黏在半行後面");
    }

    [Fact]
    public async Task 有其他程式還在寫rollout_等完安靜期仍有新的寫入_照舊不動()
    {
        var fake = new FakeServer(temp, "normal", underSessions: true);
        // 模擬 app 重開後第一次續接，而另一個程式（例如 VS Code 的 Codex 擴充）正開著同一個對話、一直在寫：
        // 檔尾是它正在寫的那一行，最後寫入時間一直在更新（只改時間不寫內容：不和假 app-server 的寫入搶檔案）
        File.WriteAllText(fake.RolloutPath, "{\"type\":\"session_meta\"}\n{\"type\":\"response_item\",\"payload\":\"");
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                File.SetLastWriteTimeUtc(fake.RolloutPath, DateTime.UtcNow);
                await Task.Delay(200);
            }
        });
        try
        {
            using var session = fake.NewSession();
            var events = CodexAgentSessionTests.Collect(session);
            await session.SendAsync(new AgentTurnInput("resume", Array.Empty<AgentAttachment>()), fake.Settings(started: true));
            await CodexAgentSessionTests.TakeUntilAsync(events, ev => ev is AgentTurnCompletedEvent, timeout: TimeSpan.FromSeconds(60));

            fake.Log().Should().Contain("p1:thread/resume rolloutEndsWithLf=False", "還有程式在寫：不能截掉它正在寫的那一行");
            fake.Backups().Should().BeEmpty();
        }
        finally
        {
            stop.Cancel();
            await writer;
        }
    }

    [Theory]
    [InlineData(0, 5000)]
    [InlineData(1200, 3800)]
    [InlineData(4999, 1)]
    public void TrimQuietWait_最近還有寫入時等到滿安靜期_已經安靜就不等(int elapsedMs, int expectedMs)
    {
        var now = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        CodexAppServerRules.TrimQuietWait(now.AddMilliseconds(-elapsedMs), now).Should().Be(TimeSpan.FromMilliseconds(expectedMs));
        CodexAppServerRules.TrimQuietWait(now.AddSeconds(-5), now).Should().BeNull("已經安靜 5 秒");
        CodexAppServerRules.TrimQuietWait(now.AddSeconds(-60), now).Should().BeNull();
        CodexAppServerRules.TrimQuietWait(now.AddSeconds(30), now).Should().Be(CodexAppServerRules.RolloutQuietPeriod, "寫入時間在未來（時鐘倒退）：等一整段");
    }

    [Fact]
    public void EndsWithLineFeed_檔尾是完整的一行_空檔也算_不存在回null()
    {
        var full = temp.WriteFile("full.jsonl", "{\"a\":1}\n");
        var half = temp.WriteFile("half.jsonl", "{\"a\":1}\n{\"b\":");
        var empty = temp.WriteFile("empty.jsonl", string.Empty);
        TranscriptTailRules.EndsWithLineFeed(full).Should().BeTrue();
        TranscriptTailRules.EndsWithLineFeed(half).Should().BeFalse();
        TranscriptTailRules.EndsWithLineFeed(empty).Should().BeTrue();
        TranscriptTailRules.EndsWithLineFeed(temp.Combine("missing.jsonl")).Should().BeNull();
    }
}
