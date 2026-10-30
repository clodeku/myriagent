using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.Rendering;

/// <summary>
/// 黃金檔的 Grok 與 Antigravity 語料（ItemRendererCorpus 的 grok、antigravity；設計 2026-10-22-grok-antigravity-sync 第 10 節第 4 項）要是真的形狀：
/// 把實錄的 ACP session/update（GrokRulesTests 的樣本）與 agy stream-json（AntigravityStreamParserTests 的實錄）交給真的解讀器，
/// 事件經中樞（假會話）建成項目——語料裡每一張工具卡的欄位都要和中樞建的那一張相同。解讀器或中樞改了工具卡的欄位，這裡先紅，再照實際的形狀更新語料與黃金檔
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class ProviderCorpusShapeTests(ITestOutputHelper output)
{
    /// <summary>實錄的 Grok 工具呼叫（2026-10-16 的 grok 1.0.52；路徑改短），依序：指令、新檔案、被拒的寫入、結束碼非 0、搜尋、修改、讀檔、MCP、X 搜尋、子代理</summary>
    internal static readonly string[] GrokUpdates =
    [
        """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"run_terminal_command","rawInput":{"command":"Write-Output hello-myriagent; Write-Output line-two","description":"Print two lines"},"_meta":{"x.ai/tool":{"version":1,"name":"run_terminal_command","kind":"execute","namespace":"grok_build","label":"Run Command","read_only":false}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"c1","kind":"execute","title":"Execute `Write-Output hello-myriagent; Write-Output line-two`","content":[{"type":"content","content":{"type":"text","text":"Print two lines"}}],"locations":[],"rawInput":{"variant":"Bash","command":"Write-Output hello-myriagent; Write-Output line-two","description":"Print two lines","is_background":false},"_meta":{"x.ai/tool":{"version":1,"name":"run_terminal_command","kind":"execute","input":{"command":"Write-Output hello-myriagent; Write-Output line-two"}}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"in_progress","content":[{"type":"content","content":{"type":"text","text":"hello-myriagent"}}],"rawOutput":{"type":"Bash","output_for_prompt":"hello-myriagent","exit_code":0}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"hello-myriagent\r\nline-two\r\n"}}],"rawOutput":{"type":"Bash","output_for_prompt":"exit: 0\nhello-myriagent\nline-two\n","exit_code":0}}""",
        """{"sessionUpdate":"tool_call","toolCallId":"w2","title":"write","rawInput":{"file_path":"C:\\w\\a.txt","content":"a"},"_meta":{"x.ai/tool":{"name":"write","kind":"write"}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"w2","kind":"edit","title":"Write","content":[{"type":"diff","path":"C:\\w\\a.txt","oldText":"","newText":"a"}],"rawInput":{"variant":"Write","file_path":"C:\\w\\a.txt","content":"a"}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"w2","status":"completed","content":[{"type":"diff","path":"C:\\w\\a.txt","oldText":"","newText":"a"}],"rawOutput":{"type":"SearchReplace","EditsApplied":{"tool_output_for_prompt":"The file C:\\w\\a.txt has been created."}}}""",
        """{"sessionUpdate":"tool_call","toolCallId":"w1","title":"write","rawInput":{"file_path":"C:\\w\\reject.txt","content":"nope\n"},"_meta":{"x.ai/tool":{"name":"write","kind":"write"}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"w1","kind":"edit","title":"Write `C:\\w\\reject.txt`","content":[{"type":"diff","path":"C:\\w\\reject.txt","oldText":"","newText":"nope\n"}],"locations":[{"path":"C:\\w\\reject.txt"}],"rawInput":{"variant":"Write","file_path":"C:\\w\\reject.txt","content":"nope\n"},"_meta":{"x.ai/tool":{"name":"write","kind":"write","input":{"path":"C:\\w\\reject.txt"}}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"w1","status":"failed","content":[{"type":"content","content":{"type":"text","text":"User rejected the execution for tool `write`"}}],"_meta":{"toolNotRun":"rejected"}}""",
        """{"sessionUpdate":"tool_call","toolCallId":"x1","title":"run_terminal_command","rawInput":{"command":"exit 2"},"_meta":{"x.ai/tool":{"name":"run_terminal_command","kind":"execute"}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"x1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"bad\r\n"}}],"rawOutput":{"type":"Bash","exit_code":2}}""",
        """{"sessionUpdate":"tool_call","toolCallId":"g1","title":"grep","rawInput":{"variant":"Grep","pattern":"TODO","path":"src"},"_meta":{"x.ai/tool":{"name":"grep","kind":"search"}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"g1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"src\\a.cs:3:// TODO 補測試\r\n"}}]}""",
        """{"sessionUpdate":"tool_call","toolCallId":"e2","title":"search_replace","rawInput":{"file_path":"C:\\w\\a.cs","old_string":"int a = 1;","new_string":"int a = 2;"},"_meta":{"x.ai/tool":{"name":"search_replace","kind":"edit"}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"e2","kind":"edit","title":"Edit `C:\\w\\a.cs`","content":[{"type":"diff","path":"C:\\w\\a.cs","oldText":"int a = 1;","newText":"int a = 2;"}],"locations":[{"path":"C:\\w\\a.cs"}]}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"e2","status":"completed","content":[{"type":"diff","path":"C:\\w\\a.cs","oldText":"int a = 1;","newText":"int a = 2;"}],"rawOutput":{"type":"SearchReplace","EditsApplied":{"tool_output_for_prompt":"Edited C:\\w\\a.cs"}}}""",
        """{"sessionUpdate":"tool_call","toolCallId":"r1","title":"read_file","rawInput":{"target_file":"C:\\w\\hello.txt"},"_meta":{"x.ai/tool":{"name":"read_file","kind":"read"}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"r1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"hello-myriagent\r\nline-two\r\n"}}]}""",
        """{"sessionUpdate":"tool_call","toolCallId":"m1","title":"mcp__github__search","rawInput":{"query":"login hang"},"_meta":{"x.ai/tool":{"name":"mcp__github__search","namespace":"mcp"}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"m1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"找到 1 個議題：#12 登入卡住"}}]}""",
        """{"sessionUpdate":"tool_call","toolCallId":"s1","title":"x_keyword_search","kind":"fetch","rawInput":{"query":"Clode Myriagent"},"_meta":{"x.ai/tool":{"name":"x_keyword_search","label":"X Search"}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"s1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"3 則貼文"}}]}""",
        """{"sessionUpdate":"tool_call","toolCallId":"t1","title":"task","rawInput":{"description":"檢查呼叫端","prompt":"找出所有呼叫 LoginAsync 的地方"},"_meta":{"x.ai/tool":{"name":"task","kind":"task"}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"t1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"共 3 處，都已處理逾時"}}]}""",
        """{"sessionUpdate":"tool_call","toolCallId":"k1","title":"run_terminal_command","rawInput":{"command":"npm test"},"_meta":{"x.ai/tool":{"name":"run_terminal_command","kind":"execute"}}}""",
        """{"sessionUpdate":"tool_call_update","toolCallId":"k1","status":"in_progress","content":[{"type":"content","content":{"type":"text","text":"partial"}}],"rawOutput":{"type":"Bash"}}""",
    ];

    /// <summary>
    /// 實錄的 agy 步驟（AntigravityStreamParserTests 的 P1 與同一形狀的工具步驟；第一輪的指令被自動拒絕、第二輪讀檔、執行、寫檔、搜尋、指令被拒但只報 DONE、
    /// 1.4.6 的 edit_file 與 write_file〔2026-10-23 實測，參數只有 Path〕、程序結束時還在跑的指令）
    /// </summary>
    internal static readonly string[][] AgyTurns =
    [
        [
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"Get-ChildItem"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":2,"state":"ERROR","step_type":"tool","tool_name":"run_command","duration_seconds":0.3597833,"tool_info":{"name":"run_command","parameters":{"CommandLine":"Get-ChildItem"},"error":{"type":"TOOL_ERROR","message":"permission check failed for command \"Get-ChildItem\": user denied permission to run command:\nGet-ChildItem\nDo not attempt to circumvent this denial."}}}}""",
            """{"event":"result","result":{"conversation_id":"c","status":"SUCCESS","response":"","duration_seconds":2.8781644,"num_turns":1,"usage":{"input_tokens":11922,"output_tokens":123,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12045},"denied_actions":[{"action":"command","display_name":"RunCommand"}]}}""",
        ],
        [
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":5,"state":"ACTIVE","step_type":"tool","tool_name":"view_file","tool_info":{"name":"view_file","parameters":{"AbsolutePath":"C:\\w\\hello.txt"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":5,"state":"DONE","step_type":"tool","tool_name":"view_file","duration_seconds":0.1394568,"tool_info":{"name":"view_file","parameters":{"AbsolutePath":"C:\\w\\hello.txt"},"output":"2 lines, 17 bytes"}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":6,"state":"DONE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"Write-Output agy-probe"},"output":"agy-probe\r\n"}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":7,"state":"DONE","step_type":"tool","tool_name":"write_to_file","tool_info":{"name":"write_to_file","parameters":{"TargetFile":"C:\\w\\a.txt","CodeContent":"hello"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":8,"state":"DONE","step_type":"tool","tool_name":"grep_search","tool_info":{"name":"grep_search","parameters":{"Query":"TODO","SearchPath":"C:\\w"},"output":"Found 2 results"}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":9,"state":"DONE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"npm test"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":11,"state":"ACTIVE","step_type":"tool","tool_name":"edit_file","tool_info":{"name":"edit_file","parameters":{"Path":"C:/w/b.txt"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":11,"state":"DONE","step_type":"tool","tool_name":"edit_file","duration_seconds":0.089123,"tool_info":{"name":"edit_file","parameters":{"Path":"C:/w/b.txt"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":12,"state":"DONE","step_type":"tool","tool_name":"write_file","duration_seconds":0.2477565,"tool_info":{"name":"write_file","parameters":{"Path":"C:\\w\\c.txt"}}}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":10,"state":"ACTIVE","step_type":"agent_response","text_delta":"Hello from probe"}}""",
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":10,"state":"DONE","step_type":"agent_response","text_delta":"\n","duration_seconds":1.7431679,"usage":{"input_tokens":12636,"output_tokens":3,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12639}}}""",
            """{"event":"result","result":{"conversation_id":"c","status":"SUCCESS","response":"Hello from probe\n","duration_seconds":8.4463696,"num_turns":2,"usage":{"input_tokens":36830,"output_tokens":236,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":37066},"denied_actions":[{"action":"command","display_name":"RunCommand"}]}}""",
        ],
        [
            """{"event":"step_update","step_update":{"conversation_id":"c","step_index":13,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"npm run dev"}}}}""",
        ],
    ];

    [Fact]
    public void Grok語料的工具卡_與實錄經解讀器和中樞建的相同()
    {
        using var h = new HubHarness();
        var (id, session) = Start(h, AgentProvider.Grok);
        var parser = new GrokUpdateParser("g");
        parser.BeginTurn();
        foreach (var update in GrokUpdates)
            foreach (var ev in parser.HandleUpdate(JObject.Parse(update)))
                h.Raise(session, ev);
        // 這一輪被停下：還在跑的工具以「已停止」收尾（GrokAgentSession 照這樣呼叫 FinishTurn）
        foreach (var ev in parser.FinishTurn())
            h.Raise(session, ev);
        h.CompleteTurn(session, AgentTurnOutcome.Interrupted);

        Compare(h, id, "grok");
    }

    [Fact]
    public void Antigravity語料的工具卡_與實錄經解讀器和中樞建的相同()
    {
        using var h = new HubHarness();
        var (id, session) = Start(h, AgentProvider.Antigravity);
        var parser = new AntigravityStreamParser("p");
        for (var turn = 0; turn < AgyTurns.Length; turn++)
        {
            if (turn > 0)
                h.Send(h.Main, id, $"第 {turn + 1} 輪");
            parser.BeginTurn();
            foreach (var line in AgyTurns[turn])
                foreach (var ev in parser.Parse(line).Events)
                    h.Raise(session, ev);
        }

        // 第三輪執行中 agy 程序結束（AntigravityAgentSession 照這樣呼叫 AbortOpenSteps）
        foreach (var ev in parser.AbortOpenSteps(AntigravityStreamRules.InterruptedToolOutput))
            h.Raise(session, ev);
        h.CompleteTurn(session, AgentTurnOutcome.Failed, "agy 程序意外結束（結束碼 1）");

        Compare(h, id, "antigravity");
    }

    private static (string Id, FakeSession Session) Start(HubHarness h, AgentProvider provider)
    {
        var account = new AgentAccount
        {
            Id = "acc-" + provider.ToString().ToLowerInvariant(), Provider = provider, Label = provider.ToString(), Color = "#123456",
            ConfigDirectory = AccountRules.SupportsIsolatedAccounts(provider) ? h.Temp.Combine("accounts", provider.ToString().ToLowerInvariant()) : null,
        };
        h.Ui.Invoke(() => h.Accounts.Seed([account], AccountStatus.Ready));
        return h.StartConversation(h.Main, account);
    }

    /// <summary>中樞建的工具卡（依序）與語料的工具卡逐欄相同；不同時印出中樞建的，照它更新語料</summary>
    private void Compare(HubHarness h, string id, string corpus)
    {
        var actual = h.OnUi(() => h.Hub.GetItems(id).Where(i => i.Kind == AgentItemKind.Tool).Select(Shape).ToList());
        var expected = ItemRendererCorpus.Get(corpus).Items.Where(i => i.Kind == AgentItemKind.Tool).Select(Shape).ToList();
        foreach (var shape in actual)
            output.WriteLine(shape);
        actual.Should().Equal(expected, $"{corpus} 語料的工具卡要和實錄經解讀器、中樞建的一樣（上面印出中樞建的）");
    }

    private static string Shape(AgentChatItem i) => new JObject
    {
        ["status"] = i.Status.ToString(),
        ["toolName"] = i.ToolName,
        ["toolKind"] = i.ToolKind,
        ["title"] = i.Title,
        ["detail"] = i.Detail,
        ["filePath"] = i.FilePath,
        ["output"] = i.Output,
        ["diff"] = i.Diff is { } diff ? new JArray(diff.Select(d => d.T + ":" + d.S)) : null,
        ["media"] = new JArray(i.MediaPaths),
    }.ToString(Newtonsoft.Json.Formatting.None);
}
