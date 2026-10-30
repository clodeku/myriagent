using Myriagent.Agents;
using Myriagent.Schedules;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Schedules;

/// <summary>
/// 排程的回溯（S2）：從對話紀錄的一行找出建立排程的指令（只看工具的指令參數、不看輸出與訊息文字）、時間與工作資料夾、
/// 一個排程的出處（刪掉重建取重建那次）、增量掃描（只讀新增的、截短的重讀、不見的檔拿掉）。紀錄檔都在暫存資料夾
/// </summary>
public sealed class TranscriptOriginTests
{
    private static string ClaudeToolUse(string command, string ts = "2026-07-30T02:27:49.000Z", string cwd = "C:\\\\repo\\\\sample-project")
        => new JObject
        {
            ["type"] = "assistant",
            ["timestamp"] = ts,
            ["cwd"] = cwd.Replace("\\\\", "\\"),
            ["message"] = new JObject
            {
                ["role"] = "assistant",
                ["content"] = new JArray(new JObject { ["type"] = "tool_use", ["name"] = "Bash", ["input"] = new JObject { ["command"] = command, ["description"] = "建立排程" } }),
            },
        }.ToString(Newtonsoft.Json.Formatting.None);

    [Fact]
    public void HitsInLine_OnlyToolCommands()
    {
        TranscriptOriginRules.HitsInLine(ClaudeToolUse("schtasks /create /tn TkReportSync /sc hourly /tr run.cmd"))
            .Should().Equal((ScheduleVerb.Create, "TkReportSync"));

        // Codex：arguments 是 JSON 字串、command 是陣列
        var codex = new JObject
        {
            ["timestamp"] = "2026-09-24T00:20:00Z",
            ["type"] = "response_item",
            ["payload"] = new JObject
            {
                ["type"] = "function_call",
                ["name"] = "shell",
                ["arguments"] = new JObject { ["command"] = new JArray("powershell", "-Command", "Register-ScheduledTask -TaskName FileMirrorSync -Action $a") }.ToString(Newtonsoft.Json.Formatting.None),
            },
        }.ToString(Newtonsoft.Json.Formatting.None);
        TranscriptOriginRules.HitsInLine(codex).Should().Equal((ScheduleVerb.Create, "FileMirrorSync"));

        // Claude 先寫註冊腳本（Write 工具的 content）：腳本裡以變數指定名稱
        var write = new JObject
        {
            ["type"] = "assistant",
            ["message"] = new JObject
            {
                ["content"] = new JArray(new JObject
                {
                    ["type"] = "tool_use", ["name"] = "Write",
                    ["input"] = new JObject { ["file_path"] = "C:\\repo\\x\\register_read_sync.ps1", ["content"] = "$TaskName = 'TkReportSync'\nRegister-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Force\n" },
                }),
            },
        }.ToString(Newtonsoft.Json.Formatting.None);
        TranscriptOriginRules.HitsInLine(write).Should().Equal((ScheduleVerb.Create, "TkReportSync"));

        // 工具的輸出、訊息文字、查詢、看不出名稱的不算
        var result = new JObject
        {
            ["type"] = "user",
            ["message"] = new JObject { ["content"] = new JArray(new JObject { ["type"] = "tool_result", ["content"] = "範例：schtasks /create /tn Demo" }) },
        }.ToString(Newtonsoft.Json.Formatting.None);
        TranscriptOriginRules.HitsInLine(result).Should().BeEmpty();
        var userText = new JObject { ["type"] = "user", ["message"] = new JObject { ["content"] = "幫我 schtasks /create /tn FromUser" } }.ToString(Newtonsoft.Json.Formatting.None);
        TranscriptOriginRules.HitsInLine(userText).Should().BeEmpty();
        TranscriptOriginRules.HitsInLine(ClaudeToolUse("schtasks /query /tn TkReportSync")).Should().BeEmpty();
        TranscriptOriginRules.HitsInLine(ClaudeToolUse("Register-ScheduledTask -TaskName $n -Action $a")).Should().BeEmpty();
        TranscriptOriginRules.HitsInLine("不是 JSON schtasks /create /tn X").Should().BeEmpty();

        var line = ClaudeToolUse("schtasks /delete /tn Old /f");
        TranscriptOriginRules.TimeOf(line).Should().Be(new DateTimeOffset(2026, 7, 30, 2, 27, 49, TimeSpan.Zero).LocalDateTime);
        TranscriptOriginRules.CwdOf(line).Should().Be(@"C:\repo\sample-project");
    }

    [Fact]
    public void OriginFor_LatestCreateAfterLastDelete()
    {
        TranscriptHit H(int day, string verb, string name = "Job") => new() { At = new DateTime(2026, 10, day), Verb = verb, Name = name, SessionId = "s" + day };
        TranscriptOriginRules.OriginFor(@"\Job", new[] { H(1, "created"), H(3, "changed") })!.SessionId.Should().Be("s1");
        TranscriptOriginRules.OriginFor(@"\Job", new[] { H(1, "created"), H(2, "deleted"), H(4, "created") })!.SessionId.Should().Be("s4");
        TranscriptOriginRules.OriginFor(@"\Job", new[] { H(1, "created"), H(2, "deleted") }).Should().BeNull("刪掉之後沒有重建");
        TranscriptOriginRules.OriginFor(@"\Job", new[] { H(5, "changed") })!.SessionId.Should().Be("s5", "沒有建立就用修改");
        TranscriptOriginRules.OriginFor(@"\Folder\Job", new[] { H(1, "created", @"\Other\Job") }).Should().BeNull();
        TranscriptOriginRules.OriginFor(@"\Job", Array.Empty<TranscriptHit>()).Should().BeNull();
    }

    [Fact]
    public void Scanner_IncrementalTruncatedAndRemovedFiles()
    {
        using var temp = new TempDirectory("sched-transcripts");
        var claude = Path.Combine(temp.Root, "claude");
        var project = Path.Combine(claude, "projects", "C--repo-x");
        Directory.CreateDirectory(project);
        var file = Path.Combine(project, "11111111-2222-3333-4444-555555555555.jsonl");
        File.WriteAllText(file, "{\"type\":\"summary\"}\n" + ClaudeToolUse("schtasks /create /tn TkReportSync /sc hourly") + "\n");
        var codexDir = Path.Combine(temp.Root, "codex", "sessions", "2026", "10", "26");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Combine(codexDir, "rollout-2026-10-26T01-02-03-0199aaaa-bbbb-cccc-dddd-eeeeffff0000.jsonl"),
            "{\"timestamp\":\"2026-10-26T01:02:03Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"local_shell_call\",\"action\":{\"command\":[\"schtasks\",\"/create\",\"/tn\",\"CodexJob\"]}}}\n");
        var roots = new[]
        {
            new TranscriptRoot(AgentProvider.Claude, "acc-claude", claude),
            new TranscriptRoot(AgentProvider.Claude, "acc-claude-dup", claude + "\\"),
            new TranscriptRoot(AgentProvider.Codex, "acc-codex", Path.Combine(temp.Root, "codex")),
        };

        var index = TranscriptOriginScanner.Scan(roots, null, CancellationToken.None);
        index.Hits.Select(h => (h.Provider, h.Name, h.SessionId)).Should().BeEquivalentTo(new[]
        {
            ("Claude", "TkReportSync", "11111111-2222-3333-4444-555555555555"),
            ("Codex", "CodexJob", "0199aaaa-bbbb-cccc-dddd-eeeeffff0000"),
        }, "同一個資料夾只掃一次");
        var first = index.Files[file].Offset;
        first.Should().Be(new FileInfo(file).Length);

        // 加一行（還沒寫完換行的不算）：只讀新增的
        File.AppendAllText(file, ClaudeToolUse("Unregister-ScheduledTask -TaskName TkReportSync -Confirm:$false") + "\n" + ClaudeToolUse("schtasks /create /tn Partial"));
        index = TranscriptOriginScanner.Scan(roots, index, CancellationToken.None);
        index.Hits.Where(h => h.Name == "TkReportSync").Select(h => h.Verb).Should().Equal("created", "deleted");
        index.Hits.Should().NotContain(h => h.Name == "Partial", "最後一行還沒有換行");
        index.Files[file].Offset.Should().BeLessThan(new FileInfo(file).Length);
        File.AppendAllText(file, "\n");
        index = TranscriptOriginScanner.Scan(roots, index, CancellationToken.None);
        index.Hits.Should().Contain(h => h.Name == "Partial");

        // 截短（重寫）：從頭讀，舊的紀錄拿掉
        File.WriteAllText(file, ClaudeToolUse("schtasks /create /tn Rewritten") + "\n");
        index = TranscriptOriginScanner.Scan(roots, index, CancellationToken.None);
        index.Hits.Where(h => h.FilePath == file).Select(h => h.Name).Should().Equal("Rewritten");

        // 刪掉的檔：索引拿掉，找到的紀錄留著
        File.Delete(file);
        index = TranscriptOriginScanner.Scan(roots, index, CancellationToken.None);
        index.Files.Should().NotContainKey(file);
        index.Hits.Should().Contain(h => h.Name == "Rewritten");
    }
}
