using System.Text;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// agy stream-json 輸出的解讀。實錄測試的每一行取自 agy 1.3.5 的實測紀錄（只縮短路徑、拿掉 init 的工具清單）：
/// init 的對話 id 在最外層；step_update 以 step_index 區分步驟、step_type 分 user_input／agent_response／tool／system_message；
/// 工具參數在 tool_info.parameters（PascalCase）、錯誤在 tool_info.error.message；result 的 usage 是整個對話的累計
/// </summary>
public class AntigravityStreamParserTests
{
    // p1：--mode accept-edits，第一輪要執行指令被自動拒絕、第二輪讀檔後回覆
    private const string P1Init = """{"event":"init","conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","init":{"model":"gemini-3.8-flash-low","cwd":"C:\\w","tools":["run_command","view_file"],"permission_mode":"request-review"}}""";
    private const string P1UserInput = """{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":0,"state":"DONE","step_type":"user_input"}}""";
    private const string P1Planning = """{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":1,"state":"DONE","step_type":"agent_response","duration_seconds":2.8380791,"usage":{"input_tokens":11922,"output_tokens":123,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12045}}}""";
    private const string P1CommandActive = """{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"Get-ChildItem"}}}}""";
    private const string P1CommandDenied = """{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":2,"state":"ERROR","step_type":"tool","tool_name":"run_command","duration_seconds":0.3597833,"tool_info":{"name":"run_command","parameters":{"CommandLine":"Get-ChildItem"},"error":{"type":"TOOL_ERROR","message":"permission check failed for command \"Get-ChildItem\": user denied permission to run command:\nGet-ChildItem\nDo not attempt to circumvent this denial."}}}}""";
    private const string P1Result1 = """{"event":"result","result":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","status":"SUCCESS","response":"","duration_seconds":2.8781644,"num_turns":1,"usage":{"input_tokens":11922,"output_tokens":123,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12045},"denied_actions":[{"action":"command","display_name":"RunCommand"}]}}""";
    private const string P1UserInput2 = """{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":3,"state":"DONE","step_type":"user_input"}}""";
    private const string P1Planning2 = """{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":4,"state":"DONE","step_type":"agent_response","duration_seconds":3.7689728,"usage":{"input_tokens":12272,"output_tokens":110,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12382}}}""";
    private const string P1ViewActive = """{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":5,"state":"ACTIVE","step_type":"tool","tool_name":"view_file","tool_info":{"name":"view_file","parameters":{"AbsolutePath":"C:\\w\\hello.txt"}}}}""";
    private const string P1ViewDone = """{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":5,"state":"DONE","step_type":"tool","tool_name":"view_file","duration_seconds":0.1394568,"tool_info":{"name":"view_file","parameters":{"AbsolutePath":"C:\\w\\hello.txt"},"output":"2 lines, 17 bytes"}}}""";
    private const string P1TextActive = """{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":6,"state":"ACTIVE","step_type":"agent_response","text_delta":"Hello from probe"}}""";
    private const string P1TextDone = """{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":6,"state":"DONE","step_type":"agent_response","text_delta":"\n","duration_seconds":1.7431679,"usage":{"input_tokens":12636,"output_tokens":3,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12639}}}""";
    private const string P1Result2 = """{"event":"result","result":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","status":"SUCCESS","response":"Hello from probe\n","duration_seconds":8.4463696,"num_turns":2,"usage":{"input_tokens":36830,"output_tokens":236,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":37066},"denied_actions":[{"action":"command","display_name":"RunCommand"}]}}""";

    [Fact]
    public void 實錄_指令被自動拒絕_讀檔後回覆_用量是這一輪各步驟相加()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();

        var init = parser.Parse(P1Init);
        init.NewConversationId.Should().Be("8d502931-d873-4b80-925e-d42e2c77000d", "init 的對話 id 在最外層");
        init.Model.Should().Be("gemini-3.8-flash-low");
        parser.Parse(P1UserInput).Events.Should().BeEmpty("使用者輸入的回顯不顯示");
        var planning = parser.Parse(P1Planning);
        planning.Events.Should().ContainSingle("規劃用的步驟沒有文字，只有這一步的用量（側欄的 token 計數）")
            .Which.Should().Be(new AgentTokenUsageEvent(new AgentUsage(11922, 123, 0, null), "gemini-3.8-flash-low"), "記在 init 的模型");
        planning.NewConversationId.Should().BeNull("同一個 id 不重複回報");

        var started = parser.Parse(P1CommandActive).Events.Should().ContainSingle().Which.Should().BeOfType<AgentToolStartedEvent>().Subject;
        started.Should().Match<AgentToolStartedEvent>(e => e.ToolId == "p-1-2" && e.ToolName == "執行指令" && e.ToolKind == "bash"
            && e.Title == "Get-ChildItem" && e.Detail == "Get-ChildItem" && e.FilePath == null);
        parser.ToolRunning.Should().BeTrue();
        parser.Parse(P1CommandDenied).Events.Should().Equal(new AgentToolCompletedEvent("p-1-2", AgentItemStatus.Failed,
            "permission check failed for command \"Get-ChildItem\": user denied permission to run command:\nGet-ChildItem\nDo not attempt to circumvent this denial.",
            Array.Empty<string>()));
        parser.ToolRunning.Should().BeFalse();

        var result1 = parser.Parse(P1Result1);
        result1.TurnCompleted.Should().BeTrue();
        result1.Events.Should().Equal(
            new AgentNoticeEvent(AntigravityStepRules.DeniedNotice(new[] { "RunCommand" }), false),
            new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, new AgentUsage(11922, 123, 0, null)));

        parser.BeginTurn();
        parser.Parse(P1UserInput2).Events.Should().BeEmpty();
        parser.Parse(P1Planning2).Events.Should().Equal(new AgentTokenUsageEvent(new AgentUsage(12272, 110, 0, null), "gemini-3.8-flash-low"));
        parser.Parse(P1ViewActive).Events.OfType<AgentToolStartedEvent>().Single()
            .Should().Match<AgentToolStartedEvent>(e => e.ToolKind == "read" && e.Title == @"C:\w\hello.txt" && e.FilePath == @"C:\w\hello.txt");
        parser.Parse(P1ViewDone).Events.Should().Equal(new AgentToolCompletedEvent("p-2-5", AgentItemStatus.Done, "2 lines, 17 bytes", Array.Empty<string>()));
        parser.Parse(P1TextActive).Events.Should().Equal(new AgentTextDeltaEvent("p-2-6", "Hello from probe"));
        parser.Parse(P1TextDone).Events.Should().Equal(new AgentTokenUsageEvent(new AgentUsage(12636, 3, 0, null), "gemini-3.8-flash-low"),
            new AgentTextDeltaEvent("p-2-6", "\n"), new AgentTextCompletedEvent("p-2-6", "Hello from probe\n"));

        // result 的 usage（36830）是整個對話的累計；denied_actions 也是累計，這一輪沒有新的拒絕就不再提示
        parser.Parse(P1Result2).Events.Should().Equal(
            new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, new AgentUsage(12272 + 12636, 110 + 3, 0, null)));
    }

    [Fact]
    public void 實錄_續接的程序_系統訊息不顯示_步驟編號接著算()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();

        parser.Parse("""{"event":"init","conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","init":{"model":"gemini-3.8-flash-low","cwd":"C:\\w","tools":[],"permission_mode":"request-review"}}""")
            .NewConversationId.Should().Be("8d502931-d873-4b80-925e-d42e2c77000d", "新的程序（解讀器）第一次看到");
        parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":7,"state":"DONE","step_type":"user_input"}}""").Events.Should().BeEmpty();
        parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":8,"state":"DONE","step_type":"system_message","duration_seconds":0}}""").Events.Should().BeEmpty();
        parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":9,"state":"ACTIVE","step_type":"agent_response","text_delta":"hello"}}""");
        parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":9,"state":"ACTIVE","step_type":"agent_response","text_delta":".txt"}}""");
        parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","step_index":9,"state":"DONE","step_type":"agent_response","text_delta":"\n","duration_seconds":1.5105307,"usage":{"input_tokens":12854,"output_tokens":3,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12857}}}""")
            .Events.Should().EndWith(new AgentTextCompletedEvent("p-1-9", "hello.txt\n"));

        var result = parser.Parse("""{"event":"result","result":{"conversation_id":"8d502931-d873-4b80-925e-d42e2c77000d","status":"SUCCESS","response":"hello.txt\n","duration_seconds":38.2019193,"num_turns":3,"usage":{"input_tokens":49684,"output_tokens":239,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":49923}}}""");
        result.Events.Should().Equal(new AgentEvent[] { new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, new AgentUsage(12854, 3, 0, null)) },
            "result 的 49684 是三輪的累計（含先前程序的兩輪），這一輪只有 12854");
    }

    [Fact]
    public void 實錄_提問被略過的步驟不顯示_快取讀取算進輸入()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();
        parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"c","step_index":1,"state":"DONE","step_type":"agent_response","duration_seconds":5.3664983,"usage":{"input_tokens":11910,"output_tokens":73,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":11983}}}""");
        parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"c","step_index":2,"state":"DONE","step_type":"unknown","duration_seconds":0.0189379}}""").Events.Should().BeEmpty();
        parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"c","step_index":3,"state":"DONE","step_type":"agent_response","text_delta":"您略過了選擇\n","duration_seconds":2.5626697,"usage":{"input_tokens":3937,"output_tokens":25,"thinking_tokens":0,"cache_read_tokens":8125,"total_tokens":3962}}}""");

        var result = parser.Parse("""{"event":"result","result":{"conversation_id":"c","status":"SUCCESS","response":"您略過了選擇\n","num_turns":1,"usage":{"input_tokens":15847,"output_tokens":98,"thinking_tokens":0,"cache_read_tokens":8125,"total_tokens":15945}}}""");

        // input_tokens 不含快取讀取（total＝input＋output）：輸入＝11910＋3937＋8125
        result.Events.OfType<AgentTurnCompletedEvent>().Single().Usage.Should().Be(new AgentUsage(23972, 98, 8125, null));
    }

    [Fact]
    public void 實錄_模型與強度衝突_錯誤結果_對話id是空字串不回報()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();

        var result = parser.Parse("""{"event":"result","result":{"conversation_id":"","status":"ERROR","response":"","error":"invalid model selection (--model \"gemini-3.8-flash-low\" --effort \"high\"): --model gemini-3.8-flash-low conflicts with --effort=high","duration_seconds":0,"num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":0}}}""");

        result.NewConversationId.Should().BeNull();
        result.Events.Should().Equal(new AgentTurnCompletedEvent(AgentTurnOutcome.Failed,
            "invalid model selection (--model \"gemini-3.8-flash-low\" --effort \"high\"): --model gemini-3.8-flash-low conflicts with --effort=high", null));
    }

    [Fact]
    public void 拒絕提示_同一個程序再被拒絕時再提示一次()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();
        parser.Parse(P1CommandActive);
        parser.Parse(P1CommandDenied);
        parser.Parse(P1Result1).Events.OfType<AgentNoticeEvent>().Should().ContainSingle();

        parser.BeginTurn();
        parser.Parse(P1CommandActive.Replace("\"step_index\":2", "\"step_index\":4"));
        parser.Parse(P1CommandDenied.Replace("\"step_index\":2", "\"step_index\":4"));
        parser.Parse(P1Result1).Events.OfType<AgentNoticeEvent>().Should().ContainSingle("這一輪又有工具因權限被拒");
    }

    // p15a：同樣被自動拒絕，agy 卻只報 DONE、沒有 output 欄位（三次實測兩次如此）
    private const string P15CommandActive = """{"event":"step_update","step_update":{"conversation_id":"a47dd83f-4260-41ca-98d0-245471bb5ab0","step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"name":"run_command","parameters":{"CommandLine":"echo agy-it"}}}}""";
    private const string P15CommandDone = """{"event":"step_update","step_update":{"conversation_id":"a47dd83f-4260-41ca-98d0-245471bb5ab0","step_index":2,"state":"DONE","step_type":"tool","tool_name":"run_command","duration_seconds":0.0180183,"tool_info":{"name":"run_command","parameters":{"CommandLine":"echo agy-it"}}}}""";
    private const string P15Result = """{"event":"result","result":{"conversation_id":"a47dd83f-4260-41ca-98d0-245471bb5ab0","status":"SUCCESS","response":"","duration_seconds":1.6,"num_turns":1,"usage":{"input_tokens":11902,"output_tokens":128,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12030},"denied_actions":[{"action":"command","display_name":"RunCommand"}]}}""";

    [Fact]
    public void 實錄_被拒的指令只報DONE沒有output_等到result依denied_actions改成失敗()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();
        parser.Parse(P15CommandActive);

        parser.Parse(P15CommandDone).Events.Should().BeEmpty("可能是被拒，等下一行再決定");
        parser.ToolRunning.Should().BeFalse("agy 已報結束");
        parser.Parse(P15Result).Events.Should().Equal(
            new AgentToolCompletedEvent("p-1-2", AgentItemStatus.Failed, AntigravityStepRules.DeniedCommandOutput, Array.Empty<string>()),
            new AgentNoticeEvent(AntigravityStepRules.DeniedNotice(new[] { "RunCommand" }), false),
            new AgentTokenUsageEvent(new AgentUsage(11902, 128, 0, null)),
            new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, new AgentUsage(11902, 128, 0, null)));
    }

    [Fact]
    public void 指令DONE沒有output_之後還有步驟也等到result_幾個一起被拒都改成失敗()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();
        parser.Parse(P15CommandActive);
        parser.Parse(P15CommandDone);
        parser.Parse(P15CommandDone).Events.Should().BeEmpty("同一個步驟重複報 DONE 仍在等");
        parser.Parse(P15CommandActive.Replace("\"step_index\":2", "\"step_index\":3")).Events.Should().ContainSingle().Which.Should().BeOfType<AgentToolStartedEvent>("平行的第二個指令不能讓第一個算成功");
        parser.Parse(P15CommandDone.Replace("\"step_index\":2", "\"step_index\":3")).Events.Should().BeEmpty();
        parser.Parse("""{"event":"step_update","step_update":{"step_index":4,"state":"ACTIVE","step_type":"agent_response","text_delta":"無法執行"}}""").Events
            .Should().Equal(new AgentEvent[] { new AgentTextDeltaEvent("p-1-4", "無法執行") }, "被拒後模型繼續回覆，也還不能判定");

        parser.Parse(P15Result).Events.OfType<AgentToolCompletedEvent>().Should().Equal(
            new AgentToolCompletedEvent("p-1-2", AgentItemStatus.Failed, AntigravityStepRules.DeniedCommandOutput, Array.Empty<string>()),
            new AgentToolCompletedEvent("p-1-3", AgentItemStatus.Failed, AntigravityStepRules.DeniedCommandOutput, Array.Empty<string>()));
    }

    [Fact]
    public void 指令DONE沒有output_result沒有拒絕或同一個步驟再報錯誤與輸出_照那一行()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();
        parser.Parse(P15CommandActive);
        parser.Parse(P15CommandDone);
        parser.Parse(Result(numTurns: 1, input: 1, output: 1)).Events.First()
            .Should().Be(new AgentToolCompletedEvent("p-1-2", AgentItemStatus.Done, null, Array.Empty<string>()), "沒有拒絕紀錄");

        parser.BeginTurn();
        parser.Parse(P15CommandActive);
        parser.Parse(P15CommandDone);
        parser.Parse(ToolStep(2, "DONE", "run_command", new { CommandLine = "echo agy-it" }, output: "agy-it\r\n")).Events
            .Should().Equal(new AgentToolCompletedEvent("p-2-2", AgentItemStatus.Done, "agy-it\n", Array.Empty<string>()));

        parser.BeginTurn();
        parser.Parse(P15CommandActive);
        parser.Parse(P15CommandDone);
        parser.Parse(P1CommandDenied).Events.Should().ContainSingle()
            .Which.Should().Match<AgentToolCompletedEvent>(e => e.ToolId == "p-3-2" && e.Status == AgentItemStatus.Failed && e.Output!.StartsWith("permission check failed"));

        parser.BeginTurn();
        parser.Parse(P15CommandActive);
        parser.Parse(P15CommandDone);
        parser.AbortOpenSteps("中斷").Should().Equal(new AgentEvent[] { new AgentToolCompletedEvent("p-4-2", AgentItemStatus.Done, null, Array.Empty<string>()) }, "agy 已報結束，不是被中斷");
    }

    [Fact]
    public void 沒有步驟用量時_只有對話第一輪用result的值()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();
        parser.Parse(Result(numTurns: 1, input: 500, output: 40, thinking: 10, cached: 100)).Events.OfType<AgentTurnCompletedEvent>().Single()
            .Usage.Should().Be(new AgentUsage(600, 50, 100, null));

        parser.BeginTurn();
        parser.Parse(Result(numTurns: 2, input: 900, output: 70)).Events.OfType<AgentTurnCompletedEvent>().Single()
            .Usage.Should().BeNull("累計值無法換算成這一輪");
    }

    [Fact]
    public void result失敗而且是503暫時無法服務_標成ServerBusy的Unavailable_其他錯誤與成功不標()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();
        var failed = parser.Parse(Result(numTurns: 3, input: 0, output: 0, status: "ERROR",
            error: "API error (attempt 1): UNAVAILABLE (code 503): The service is currently unavailable.")).Events;
        var turn = failed.OfType<AgentTurnCompletedEvent>().Single();
        turn.Outcome.Should().Be(AgentTurnOutcome.Failed);
        turn.ErrorMessage.Should().Be("API error (attempt 1): UNAVAILABLE (code 503): The service is currently unavailable.", "錯誤照原文顯示");
        turn.ServerBusy.Should().Be(AgentServerBusy.Unavailable, "中樞等一下自動重試");
        failed.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty("不是額度用完，不換帳戶");

        parser.BeginTurn();
        parser.Parse(Result(numTurns: 4, input: 0, output: 0, status: "ERROR", error: "Your Google One AI credits balance is too low to continue."))
            .Events.OfType<AgentTurnCompletedEvent>().Single().ServerBusy.Should().Be(AgentServerBusy.None, "點數用完要換帳戶，不是等一下就好");

        parser.BeginTurn();
        parser.Parse(Result(numTurns: 5, input: 0, output: 0, status: "ERROR", error: "permission check failed"))
            .Events.OfType<AgentTurnCompletedEvent>().Single().ServerBusy.Should().Be(AgentServerBusy.None);

        parser.BeginTurn();
        parser.Parse(Result(numTurns: 6, input: 1, output: 1, status: "CANCELED", error: "UNAVAILABLE (code 503)"))
            .Events.OfType<AgentTurnCompletedEvent>().Single().ServerBusy.Should().Be(AgentServerBusy.None, "被停止的一輪不重試");

        const string WsError = """API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": write tcp 192.168.0.10:55012->172.217.112.4:443: wsasend: An existing connection was forcibly closed by the remote host.""";
        parser.BeginTurn();
        var netFailed = parser.Parse(Result(numTurns: 7, input: 0, output: 0, status: "ERROR", error: WsError)).Events;
        var netTurn = netFailed.OfType<AgentTurnCompletedEvent>().Single();
        netTurn.Outcome.Should().Be(AgentTurnOutcome.Failed);
        netTurn.ErrorMessage.Should().Be(WsError, "錯誤照原文顯示");
        netTurn.ServerBusy.Should().Be(AgentServerBusy.NetworkError, "API 網路瞬斷交給中樞 10 秒後重試");
        netFailed.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();

        const string IdleTimeoutError = "API error (attempt 1): request failed: stream idle timeout (5m0s): context deadline exceeded";
        parser.BeginTurn();
        var idleFailed = parser.Parse(Result(numTurns: 8, input: 0, output: 0, status: "ERROR", error: IdleTimeoutError)).Events;
        var idleTurn = idleFailed.OfType<AgentTurnCompletedEvent>().Single();
        idleTurn.Outcome.Should().Be(AgentTurnOutcome.Failed);
        idleTurn.ErrorMessage.Should().Be(IdleTimeoutError);
        idleTurn.ServerBusy.Should().Be(AgentServerBusy.NetworkError, "SSE 串流靜默逾時同樣回收連線池並於 10 秒後自動重試");
        idleFailed.OfType<AgentQuotaExhaustedEvent>().Should().BeEmpty();
    }

    private static string Result(int numTurns, long input, long output, long thinking = 0, long cached = 0, string status = "SUCCESS", string? response = null, string? error = null)
        => new JObject
        {
            ["event"] = "result",
            ["result"] = JObject.FromObject(new
            {
                conversation_id = "conv-1",
                status,
                response,
                error,
                num_turns = numTurns,
                usage = new { input_tokens = input, output_tokens = output, thinking_tokens = thinking, cache_read_tokens = cached, total_tokens = input + output },
            }),
        }.ToString(Newtonsoft.Json.Formatting.None);

    private static string ToolStep(int index, string state, string tool, object? parameters = null, string? output = null)
    {
        var info = new JObject { ["name"] = tool };
        if (parameters != null)
            info["parameters"] = JObject.FromObject(parameters);
        if (output != null)
            info["output"] = output;
        return new JObject
        {
            ["event"] = "step_update",
            ["step_update"] = new JObject { ["conversation_id"] = "conv-1", ["step_index"] = index, ["state"] = state, ["step_type"] = "tool", ["tool_name"] = tool, ["tool_info"] = info },
        }.ToString(Newtonsoft.Json.Formatting.None);
    }

    [Theory]
    [InlineData("run_command", "bash")]
    [InlineData("command_status", "bash")]
    [InlineData("view_file", "read")]
    [InlineData("read_file", "read")]
    [InlineData("write_file", "write")]
    [InlineData("edit_file", "edit")]
    [InlineData("write_to_file", "write")]
    [InlineData("replace_file_content", "edit")]
    [InlineData("multi_replace_file_content", "edit")]
    [InlineData("list_dir", "search")]
    [InlineData("grep_search", "search")]
    [InlineData("find_by_name", "search")]
    [InlineData("search_web", "web")]
    [InlineData("read_url_content", "web")]
    [InlineData("browser_click_element", "web")]
    [InlineData("invoke_subagent", "agent")]
    [InlineData("call_mcp_tool", "mcp")]
    [InlineData("manage_task", "other")]
    public void 工具種類(string tool, string kind)
        => AntigravityStepRules.DescribeTool(tool, null).Kind.Should().Be(kind);

    [Fact]
    public void 工具參數_PascalCase與snake_case都認_只有檔案工具帶檔案路徑()
    {
        var write = AntigravityStepRules.DescribeTool("write_to_file", JObject.FromObject(new { parameters = new { TargetFile = @"C:\w\a.txt", CodeContent = "x" } }));
        write.Should().Match<AntigravityStepRules.ToolDescription>(t => t.Name == "寫入檔案" && t.Title == @"C:\w\a.txt" && t.FilePath == @"C:\w\a.txt");
        write.Detail.Should().Contain("CodeContent", "沒有紅綠差異時細節保留參數 JSON");
        AntigravityStepRules.DescribeTool("write_to_file", JObject.FromObject(new { parameters = new { TargetFile = @"C:\w\a.txt", CodeContent = "x" } }), hasDiff: true)
            .Detail.Should().BeNull("已有紅綠差異時不再重複列整包內容 JSON");

        var list = AntigravityStepRules.DescribeTool("list_dir", JObject.FromObject(new { parameters = new { DirectoryPath = @"C:\w" } }));
        list.Title.Should().Be(@"C:\w");
        list.FilePath.Should().BeNull("資料夾不是檔案連結");

        AntigravityStepRules.DescribeTool("run_command", JObject.FromObject(new { parameters = new { command_line = "npm test\n--watch=false" } }))
            .Should().Match<AntigravityStepRules.ToolDescription>(t => t.Title == "npm test" && t.Detail == "npm test\n--watch=false");
        AntigravityStepRules.DescribeTool("brand_new_tool", null).Should().Match<AntigravityStepRules.ToolDescription>(t => t.Name == "brand_new_tool" && t.Title == "brand_new_tool");
    }

    [Fact]
    public void 實錄_1_4_6的檔案工具_read_file_write_file_edit_file_有畫面名稱_參數只有Path所以沒有細節與差異()
    {
        // 2026-10-23 agy 1.4.6 的實測（tools/agy-probe；路徑改短）：init 的 tools 仍列 view_file、write_to_file，步驟的 tool_name 卻是這三個
        foreach (var (tool, name, kind, path) in new[]
                 {
                     ("read_file", "讀取檔案", "read", "C:/w/b.txt"),
                     ("write_file", "寫入檔案", "write", @"C:\w\a.txt"),
                     ("edit_file", "修改檔案", "edit", "C:/w/b.txt"),
                 })
        {
            var d = AntigravityStepRules.DescribeTool(tool, new JObject { ["name"] = tool, ["parameters"] = new JObject { ["Path"] = path } });
            d.Should().Match<AntigravityStepRules.ToolDescription>(t => t.Name == name && t.Kind == kind && t.Title == path && t.FilePath == path && t.Detail == null,
                $"{tool} 不再顯示原文的工具名稱；參數只有路徑（沒有內容與舊／新字串，組不出差異）");
        }
    }

    [Fact]
    public void 檔案工具的細節不重複列路徑_只有路徑時沒有細節()
    {
        // 實錄（P1ViewActive）：view_file 只有 AbsolutePath——標題與檔案連結已經是它，卡片的「輸入」不再是一段跳脫過反斜線的 JSON（2026-10-23）
        var view = AntigravityStepRules.DescribeTool("view_file", JObject.Parse("""{"name":"view_file","parameters":{"AbsolutePath":"C:\\w\\hello.txt"}}"""));
        view.Should().Match<AntigravityStepRules.ToolDescription>(t => t.Title == @"C:\w\hello.txt" && t.FilePath == @"C:\w\hello.txt" && t.Detail == null);

        var range = AntigravityStepRules.DescribeTool("view_file", JObject.FromObject(new { parameters = new { AbsolutePath = @"C:\w\a.cs", StartLine = 10, EndLine = 20 } }));
        range.Detail.Should().Contain("StartLine").And.NotContain("AbsolutePath", "其他參數照樣列出");

        var write = AntigravityStepRules.DescribeTool("write_to_file", JObject.FromObject(new { parameters = new { target_file = @"C:\w\a.txt", CodeContent = "x" } }));
        write.Should().Match<AntigravityStepRules.ToolDescription>(t => t.FilePath == @"C:\w\a.txt");
        write.Detail.Should().Contain("CodeContent").And.NotContain("target_file", "snake_case 的路徑參數也認得");

        // 參數 JSON 照 Claude、Codex 的上限截斷：write_to_file 的 CodeContent 是整個檔案
        var big = AntigravityStepRules.DescribeTool("write_to_file", JObject.FromObject(new { parameters = new { TargetFile = @"C:\w\big.txt", CodeContent = new string('x', 20_000) } }));
        big.Detail!.Length.Should().BeLessThan(4100);
        big.Detail.Should().StartWith("{").And.Contain("以下省略");

        // 不是檔案工具的路徑參數照舊（list_dir 的資料夾不是檔案連結，細節還要有它）
        AntigravityStepRules.DescribeTool("list_dir", JObject.FromObject(new { parameters = new { DirectoryPath = @"C:\w" } })).Detail.Should().Contain("DirectoryPath");
        AntigravityStepRules.DescribeTool("grep_search", JObject.FromObject(new { parameters = new { Query = "TODO", Path = @"C:\w" } })).Detail.Should().Contain("Path");
    }

    [Fact]
    public void 工具輸出_換行統一_沒有輸出用錯誤訊息()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();
        parser.Parse(ToolStep(2, "ACTIVE", "run_command", new { CommandLine = "echo agy-probe" }));
        parser.Parse(ToolStep(2, "DONE", "run_command", new { CommandLine = "echo agy-probe" }, output: "agy-probe\r\n"))
            .Events.Should().Equal(new AgentToolCompletedEvent("p-1-2", AgentItemStatus.Done, "agy-probe\n", Array.Empty<string>()));

        parser.Parse(ToolStep(4, "ACTIVE", "write_to_file", new { TargetFile = @"C:\w\a.txt" }));
        parser.Parse(ToolStep(4, "DONE", "write_to_file", new { TargetFile = @"C:\w\a.txt" }))
            .Events.Should().Equal(new AgentEvent[] { new AgentToolCompletedEvent("p-1-4", AgentItemStatus.Done, null, Array.Empty<string>()) }, "write_to_file 沒有輸出");

        AntigravityStepRules.DescribeToolOutput(JObject.Parse("""{"error":{"type":"TOOL_ERROR","message":"boom"}}""")).Should().Be("boom");
    }

    [Fact]
    public void 這一輪結束_未定稿的文字定稿_未結束的工具結束_只有response時補一段()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();
        parser.Parse("""{"event":"step_update","step_update":{"step_index":0,"state":"ACTIVE","step_type":"agent_response","text_delta":"部分"}}""");
        parser.Parse(ToolStep(1, "ACTIVE", "write_to_file"));

        parser.Parse(Result(numTurns: 1, input: 10, output: 5)).Events.Should().Equal(
            new AgentTextCompletedEvent("p-1-0", "部分"),
            new AgentToolCompletedEvent("p-1-1", AgentItemStatus.Done, null, Array.Empty<string>()),
            new AgentTokenUsageEvent(new AgentUsage(10, 5, 0, null)),
            new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, new AgentUsage(10, 5, 0, null)));

        parser.BeginTurn();
        parser.Parse(Result(numTurns: 2, input: 20, output: 6, response: "只有 response")).Events.First()
            .Should().Be(new AgentTextCompletedEvent("p-2-response", "只有 response"));

        parser.BeginTurn();
        parser.Parse(Result(numTurns: 3, input: 30, output: 7, status: "INVALID")).Events.OfType<AgentTurnCompletedEvent>().Single()
            .ErrorMessage.Should().Be("agy 回報這一輪失敗（INVALID）");
    }

    [Fact]
    public void 程序被結束_開著的工具改成已停止_文字照目前累積的定稿()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();
        parser.Parse("""{"event":"step_update","step_update":{"step_index":0,"state":"ACTIVE","step_type":"agent_response","text_delta":"寫到一半"}}""");
        parser.Parse(ToolStep(1, "ACTIVE", "run_command", new { CommandLine = "npm test" }));

        parser.AbortOpenSteps("中斷").Should().Equal(
            new AgentTextCompletedEvent("p-1-0", "寫到一半"),
            new AgentToolCompletedEvent("p-1-1", AgentItemStatus.Cancelled, "中斷", Array.Empty<string>()));
        parser.AbortOpenSteps("中斷").Should().BeEmpty("已經處理過");
    }

    [Fact]
    public void 不是JSON的行交給會話判斷_不認得的事件忽略()
    {
        var parser = new AntigravityStreamParser();
        parser.Parse("Authentication required. Please visit the URL to log in:").PlainText
            .Should().Be("Authentication required. Please visit the URL to log in:");
        var unknown = parser.Parse("""{"event":"telemetry","telemetry":{"x":1}}""");
        unknown.Events.Should().BeEmpty();
        unknown.TurnCompleted.Should().BeFalse();
        parser.Parse("").Events.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ACTIVE", "Running")]
    [InlineData("DONE", "Completed")]
    [InlineData("ERROR", "Failed")]
    [InlineData("STEP_STATE_DONE", "Completed")]
    [InlineData("CANCELED", "Cancelled")]
    [InlineData(null, "Running")]
    public void 步驟狀態(string? state, string expected) => AntigravityStepRules.ClassifyState(state).Should().Be(Enum.Parse<AntigravityStepState>(expected));

    [Theory]
    [InlineData("SUCCESS", "Completed")]
    [InlineData("CANCELED", "Interrupted")]
    [InlineData("INTERRUPTED", "Interrupted")]
    [InlineData("ERROR", "Failed")]
    [InlineData(null, "Failed")]
    public void 結果狀態(string? status, string expected) => AntigravityStepRules.ClassifyResultStatus(status).Should().Be(Enum.Parse<AgentTurnOutcome>(expected));

    // ───── 逐字稿伴讀（transcript_full.jsonl）、子代理軌跡、規劃確認卡與額度耗盡 ─────

    [Fact]
    public void TranscriptRules与Reader_半行寫入不遺失_雙重編碼解開_編輯紅綠Diff與本機媒體過濾()
    {
        var unwrapped = AntigravityTranscriptRules.UnwrapArgs(JObject.Parse("""{"Path":"\"C:\\\\w\\\\a.cs\"","Target":"\"old\"","Replacement":"\"new\"","Overwrite":"true"}"""));
        unwrapped.Value<string>("Path").Should().Be(@"C:\w\a.cs");
        unwrapped.Value<bool>("Overwrite").Should().BeTrue();

        var editDiff = AntigravityTranscriptRules.ComputeToolDiff("edit_file", unwrapped);
        editDiff.Should().NotBeNull();
        editDiff!.Select(l => (l.T, l.S)).Should().Equal(("del", "old"), ("add", "new"));

        var multiDiff = AntigravityTranscriptRules.ComputeToolDiff("multi_replace_file_content",
            JObject.Parse("""{"ReplacementChunks":[{"TargetContent":"a","ReplacementContent":"b"},{"TargetContent":"c","ReplacementContent":"d"}]}"""));
        multiDiff.Should().NotBeNull();
        multiDiff!.Count(l => l.T == "add").Should().Be(2);

        var media = AntigravityTranscriptRules.ExtractMediaPaths(JArray.Parse("""
            [
              {"mime_type":"image/png","uri":"file:///C:/w/out/shot.png"},
              {"mime_type":"image/png","uri":"file://evil-server/share/leak.png"},
              {"mime_type":"text/plain","uri":"file:///C:/w/out/readme.txt"}
            ]
            """));
        media.Should().Equal(@"C:\w\out\shot.png");

        using var temp = new TempDirectory("agy-transcript");
        var logPath = temp.Combine("transcript_full.jsonl");
        var reader = new AntigravityTranscriptReader(_ => logPath);
        reader.BindConversation("conv-t");

        var line1 = """{"step_index":1,"type":"PLANNER_RESPONSE","thinking":"先檢查檔案","tool_calls":[{"name":"edit_file","args":{"Path":"C:/w/a.cs","Target":"int x = 1;","Replacement":"int x = 2;"}}]}""";
        // 先寫前半行（沒有換行）：Reader 不能前進位移把半行吃掉
        File.WriteAllText(logPath, line1[..40], new UTF8Encoding(false));
        reader.Sync();
        reader.StepsFrom(0).Should().BeEmpty("尚未寫到換行字元");

        File.AppendAllText(logPath, line1[40..] + "\n", new UTF8Encoding(false));
        reader.Sync();
        reader.StepsFrom(0).Should().ContainSingle().Which.Thinking.Should().Be("先檢查檔案");
        var found = reader.FindToolCall(2, "edit_file", 0);
        found.Should().NotBeNull();
        found!.Args.Value<string>("Replacement").Should().Be("int x = 2;");
    }

    [Fact]
    public void 伴讀逐字稿_續接不重播舊思考_延後ACTIVE至DONE補齊紅綠Diff_非讀取工具附媒體路徑()
    {
        using var temp = new TempDirectory("agy-parser-transcript");
        var logPath = temp.Combine("transcript_full.jsonl");
        var utf8 = new UTF8Encoding(false);

        // 先寫入舊輪次（step 0~2）的思考紀錄：以 initialConversationId 續接時不可重播
        File.WriteAllLines(logPath, new[]
        {
            """{"step_index":0,"type":"USER_INPUT","content":"舊問題"}""",
            """{"step_index":1,"type":"PLANNER_RESPONSE","thinking":"這是上一輪的舊思考"}""",
        }, utf8);

        var parser = new AntigravityStreamParser("p", _ => logPath, initialConversationId: "conv-1");
        parser.BeginTurn();

        // 新一輪從 step 5 開始
        parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"conv-1","step_index":5,"state":"DONE","step_type":"user_input"}}""").Events.Should().BeEmpty();

        File.AppendAllLines(logPath, new[]
        {
            """{"step_index":6,"type":"PLANNER_RESPONSE","thinking":"新一輪思考：修改 b.cs 並截圖","tool_calls":[{"name":"edit_file","args":{"Path":"C:/w/b.cs","Target":"foo()","Replacement":"bar()"}},{"name":"browser_screenshot","args":{}}]}""",
        }, utf8);

        var planEvents = parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"conv-1","step_index":6,"state":"DONE","step_type":"agent_response","duration_seconds":1.0,"usage":{"input_tokens":100,"output_tokens":20,"thinking_tokens":10,"cache_read_tokens":0,"total_tokens":120}}}""").Events;
        planEvents.OfType<AgentThinkingDeltaEvent>().Should().ContainSingle()
            .Which.Delta.Should().Be("新一輪思考：修改 b.cs 並截圖");

        // step 7 edit_file：stream-json 只有 Path，伴讀逐字稿補上紅綠 Diff
        var editStarted = parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"conv-1","step_index":7,"state":"ACTIVE","step_type":"tool","tool_name":"edit_file","tool_info":{"name":"edit_file","parameters":{"Path":"C:/w/b.cs"}}}}""")
            .Events.OfType<AgentToolStartedEvent>().Single();
        editStarted.Diff.Should().NotBeNull();
        editStarted.Diff!.Select(d => (d.T, d.S)).Should().Equal(("del", "foo()"), ("add", "bar()"));
        editStarted.Detail.Should().BeNull();
        parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"conv-1","step_index":7,"state":"DONE","step_type":"tool","tool_name":"edit_file","tool_info":{"name":"edit_file","parameters":{"Path":"C:/w/b.cs"}}}}""");

        // step 8 browser_screenshot 產出圖片記在逐字稿 media；step 9 read_file 讀取圖片則不重複貼圖
        File.AppendAllLines(logPath, new[]
        {
            """{"step_index":8,"type":"TOOL_RESPONSE","media":[{"mime_type":"image/png","uri":"file:///C:/w/cap.png"}]}""",
            """{"step_index":9,"type":"TOOL_RESPONSE","media":[{"mime_type":"image/png","uri":"file:///C:/w/existing.png"}]}""",
        }, utf8);

        parser.Parse(ToolStep(8, "ACTIVE", "browser_screenshot"));
        parser.Parse(ToolStep(8, "DONE", "browser_screenshot", output: "saved")).Events.OfType<AgentToolCompletedEvent>().Single()
            .MediaPaths.Should().Equal(@"C:\w\cap.png");

        parser.Parse(ToolStep(9, "ACTIVE", "read_file", new { Path = @"C:\w\existing.png" }));
        parser.Parse(ToolStep(9, "DONE", "read_file", new { Path = @"C:\w\existing.png" }, output: "image")).Events.OfType<AgentToolCompletedEvent>().Single()
            .MediaPaths.Should().BeEmpty("讀取既有圖片的工具卡不當成產出貼圖");

        // 延遲寫入情境：step 11 write_file ACTIVE 時逐字稿還沒刷盤，延到 DONE 才寫入仍能補齊 Diff
        parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"conv-1","step_index":11,"state":"ACTIVE","step_type":"tool","tool_name":"write_file","tool_info":{"name":"write_file","parameters":{"Path":"C:/w/new.txt"}}}}""")
            .Events.Should().BeEmpty("先等逐字稿刷盤再發 Started，確保卡片帶紅綠 Diff");
        File.AppendAllLines(logPath, new[]
        {
            """{"step_index":10,"type":"PLANNER_RESPONSE","tool_calls":[{"name":"write_file","args":{"Path":"C:/w/new.txt","Content":"hello diff"}}]}""",
        }, utf8);
        var delayedDone = parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"conv-1","step_index":11,"state":"DONE","step_type":"tool","tool_name":"write_file","tool_info":{"name":"write_file","parameters":{"Path":"C:/w/new.txt"}}}}""").Events;
        delayedDone.OfType<AgentToolStartedEvent>().Single().Diff!.Select(d => (d.T, d.S)).Should().Equal(("add", "hello diff"));
        delayedDone.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Done);
    }

    [Fact]
    public void 子代理subagent_info_規劃模式plan確認卡_以及額度耗盡事件()
    {
        var parser = new AntigravityStreamParser("p", permissionMode: AgentPermissionMode.Plan);
        parser.BeginTurn();

        var subStep = """
            {"event":"step_update","step_update":{"conversation_id":"conv-1","step_index":2,"state":"DONE","step_type":"tool","tool_name":"invoke_subagent","tool_info":{"name":"invoke_subagent","parameters":{"TypeName":"research","Role":"Codebase Researcher","Prompt":"查清架構"},"output":"研究結論"},"subagent_info":{"conversation_id":"sub-99","type":"research","description":"Codebase Researcher","log_uri":"file:///C:/Users/u/.gemini/antigravity-cli/brain/sub-99/.system_generated/logs/transcript.jsonl","usage":{"input_tokens":500,"output_tokens":100,"thinking_tokens":20,"cache_read_tokens":50,"total_tokens":620}}}}
            """;
        var subEvents = parser.Parse(subStep).Events;
        subEvents.OfType<AgentTokenUsageEvent>().Single().Should().Be(new AgentTokenUsageEvent(new AgentUsage(550, 120, 50, null)));
        subEvents.OfType<AgentToolStartedEvent>().Single().Title.Should().Be("Codebase Researcher（research）");
        subEvents.OfType<AgentToolCompletedEvent>().Single().Output.Should()
            .Contain("研究結論").And.Contain("sub-99").And.Contain(@"C:\Users\u\.gemini\antigravity-cli\brain\sub-99\.system_generated\logs\transcript.jsonl");

        // 規劃模式下寫入 plan.md → 在 TurnCompleted 之前發出 AnswerAsMessage 計畫確認問題卡
        parser.Parse(ToolStep(3, "DONE", "write_file", new { Path = @"C:\Users\u\.gemini\antigravity-cli\brain\conv-1\plan.md", Content = "# 計畫" }));
        var resEvents = parser.Parse(Result(numTurns: 1, input: 500, output: 100)).Events;
        var qEvent = resEvents.OfType<AgentQuestionEvent>().Single();
        qEvent.AnswerAsMessage.Should().BeTrue();
        qEvent.Questions.Single().Id.Should().Be(AntigravityStreamRules.PlanFeedbackQuestionId);
        resEvents.Last().Should().BeOfType<AgentTurnCompletedEvent>();

        // 額度耗盡錯誤 → 發出 AgentQuotaExhaustedEvent
        parser.BeginTurn();
        var quotaEvents = parser.Parse(Result(numTurns: 2, input: 0, output: 0, status: "ERROR", error: "Your Google One AI credits balance is too low to continue.")).Events;
        quotaEvents.OfType<AgentQuotaExhaustedEvent>().Should().ContainSingle()
            .Which.Provider.Should().Be(AgentProvider.Antigravity);
    }

    [Fact]
    public void Artifact預覽與計畫全文_寫入brain或帶ArtifactMetadata時擷取Markdown_改檔完成時讀磁碟全文()
    {
        using var temp = new TempDirectory("agy-artifacts");
        var brainDir = temp.Combine("brain", "conv-a");
        Directory.CreateDirectory(brainDir);
        var planPath = Path.Combine(brainDir, "plan.md");
        File.WriteAllText(planPath, "# 實作計畫\n\n1. 第一階段\n2. 第二階段", new UTF8Encoding(false));

        var parser = new AntigravityStreamParser("p", permissionMode: AgentPermissionMode.Plan);
        parser.BeginTurn();

        // 1) write_file 寫入 Artifact（帶 ArtifactMetadata，RequestFeedback=false）：Started 即帶 ArtifactMarkdown
        var reportPath = Path.Combine(brainDir, "walkthrough.md");
        var writeEvents = parser.Parse(ToolStep(1, "DONE", "write_file", new
        {
            Path = reportPath,
            Content = "# 成果報告\n\n> [!NOTE]\n> 測試全數通過",
            ArtifactMetadata = new { Summary = "成果摘要", UserFacing = true, RequestFeedback = false },
        })).Events;
        writeEvents.OfType<AgentToolStartedEvent>().Single().ArtifactMarkdown.Should().Contain("# 成果報告");

        // 2) UserFacing = false 或 scratch 下的檔案不當成對話預覽 Artifact
        var scratchPath = Path.Combine(brainDir, "scratch", "notes.md");
        parser.Parse(ToolStep(2, "DONE", "write_file", new { Path = scratchPath, Content = "# 暫存筆記" }))
            .Events.OfType<AgentToolStartedEvent>().Single().ArtifactMarkdown.Should().BeNull();

        // 3) edit_file 修改磁碟上的 plan.md：完成時讀出更新後的磁碟全文，且結束時發出的 Question 帶完整 Detail 與 FilePath
        var editEvents = parser.Parse(ToolStep(3, "DONE", "edit_file", new
        {
            Path = planPath,
            Target = "1. 第一階段",
            Replacement = "1. 第一階段（已細化）",
            ArtifactMetadata = new { Summary = "更新計畫", UserFacing = true, RequestFeedback = true },
        })).Events;
        editEvents.OfType<AgentToolCompletedEvent>().Single().ArtifactMarkdown.Should().Contain("# 實作計畫");

        var turnEndEvents = parser.Parse(Result(numTurns: 1, input: 100, output: 50)).Events;
        var planQ = turnEndEvents.OfType<AgentQuestionEvent>().Single();
        planQ.Detail.Should().Contain("# 實作計畫").And.Contain("2. 第二階段");
        planQ.FilePath.Should().Be(planPath);
    }

    [Fact]
    public void 子代理完整逐字稿展開與背景任務排程追蹤()
    {
        using var temp = new TempDirectory("agy-sub-bg");
        var mainLog = temp.Combine("brain", "conv-main", ".system_generated", "logs", "transcript_full.jsonl");
        var subLog = temp.Combine("brain", "sub-42", ".system_generated", "logs", "transcript_full.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(mainLog)!);
        Directory.CreateDirectory(Path.GetDirectoryName(subLog)!);
        var utf8 = new UTF8Encoding(false);

        File.WriteAllLines(subLog, new[]
        {
            """{"step_index":0,"type":"USER_INPUT","content":"請檢查認證模組"}""",
            """{"step_index":1,"type":"PLANNER_RESPONSE","thinking":"先搜尋 Auth 相關檔案並讀取設定","content":"我先列出目錄","tool_calls":[{"name":"grep_search","args":{"Query":"OAuth"}},{"name":"read_file","args":{"Path":"C:/w/Auth.cs"}}]}""",
            """{"step_index":4,"type":"PLANNER_RESPONSE","thinking":"確認完畢，準備回報"}""",
        }, utf8);

        var parser = new AntigravityStreamParser(
            "p",
            id => temp.Combine("brain", id, ".system_generated", "logs", "transcript_full.jsonl"));
        parser.BeginTurn();
        parser.Parse("""{"event":"init","conversation_id":"conv-main","init":{"model":"gemini-3.8-pro"}}""");

        // 1) 子代理完成時：工具卡帶本機逐字稿路徑，且展開輸出上半部為步驟紀錄、下半部為最終結論（避免被 TrimToolOutput 裁掉結論）
        var subUri = new Uri(subLog).AbsoluteUri;
        var subStep = new JObject
        {
            ["event"] = "step_update",
            ["step_update"] = JObject.FromObject(new
            {
                conversation_id = "conv-main",
                step_index = 2,
                state = "DONE",
                step_type = "tool",
                tool_name = "invoke_subagent",
                tool_info = new
                {
                    name = "invoke_subagent",
                    parameters = new { TypeName = "research", Role = "Auth Auditor", Prompt = "檢查認證模組" },
                    output = "最終結論：OAuth 權杖刷新流程正常。",
                },
                subagent_info = new
                {
                    conversation_id = "sub-42",
                    type = "research",
                    log_uri = subUri,
                },
            }),
        }.ToString(Newtonsoft.Json.Formatting.None);

        var subEvents = parser.Parse(subStep).Events;
        subEvents.OfType<AgentToolStartedEvent>().Single().FilePath.Should().Be(subLog);
        var subOutput = subEvents.OfType<AgentToolCompletedEvent>().Single().Output!;
        subOutput.Should().Contain("── 子代理執行紀錄")
            .And.Contain("💭 思考：先搜尋 Auth 相關檔案並讀取設定")
            .And.Contain("▸ 搜尋內容：OAuth")
            .And.Contain("▸ 讀取檔案：C:/w/Auth.cs")
            .And.Contain("── 子代理結論 ──\n最終結論：OAuth 權杖刷新流程正常。");
        subOutput.IndexOf("── 子代理執行紀錄", StringComparison.Ordinal)
            .Should().BeLessThan(subOutput.IndexOf("── 子代理結論 ──", StringComparison.Ordinal));

        // 2) 背景指令與單次排程計時器：發出 AgentBackgroundWorkEvent
        var bgCmdEvents = parser.Parse(ToolStep(
            3, "DONE", "run_command",
            new { CommandLine = "npm run watch", IsDaemon = true },
            output: "Command started in background as task-watch-1")).Events;
        bgCmdEvents.OfType<AgentBackgroundWorkEvent>().Single().Count.Should().Be(1);
        parser.HasLiveBackgroundWork.Should().BeTrue();

        var schedEvents = parser.Parse(ToolStep(
            4, "DONE", "schedule",
            new { DurationSeconds = 30, Prompt = "檢查建置產物" },
            output: "Scheduled one-shot timer")).Events;
        schedEvents.OfType<AgentBackgroundWorkEvent>().Single().Count.Should().Be(2);

        // 3) manage_task kill 移除背景指令
        var killEvents = parser.Parse(ToolStep(
            5, "DONE", "manage_task",
            new { Action = "kill", TaskId = "task-watch-1" },
            output: "Killed task-watch-1")).Events;
        killEvents.OfType<AgentBackgroundWorkEvent>().Single().Count.Should().Be(1);

        parser.Parse(Result(numTurns: 1, input: 100, output: 20));

        // 4) 閒置時單次計時器到期喚醒新一輪：逐字稿寫入 SYSTEM_MESSAGE 後自動歸零背景工作
        parser.BeginTurn();
        File.WriteAllLines(mainLog, new[]
        {
            """{"step_index":6,"type":"USER_INPUT","content":"<SYSTEM_MESSAGE>Scheduled timer fired: 檢查建置產物</SYSTEM_MESSAGE>"}""",
        }, utf8);
        var wakeEvents = parser.Parse("""{"event":"step_update","step_update":{"conversation_id":"conv-main","step_index":6,"state":"DONE","step_type":"user_input"}}""").Events;
        wakeEvents.OfType<AgentBackgroundWorkEvent>().Single().Count.Should().Be(0);
        parser.HasLiveBackgroundWork.Should().BeFalse();

        // 5) 非常駐（IsDaemon=false）的背景 run_command 在本輪 result 結束前未收到顯式 kill 時，自動收斂不殘留永久背景燈號
        parser.BeginTurn();
        parser.Parse(ToolStep(7, "DONE", "run_command", new { CommandLine = "dotnet test", IsDaemon = false }, output: "Command started in background as task-test-99"));
        parser.HasLiveBackgroundWork.Should().BeTrue();
        var settleEvents = parser.Parse(Result(numTurns: 3, input: 50, output: 10)).Events;
        settleEvents.OfType<AgentBackgroundWorkEvent>().Single().Count.Should().Be(0);
        parser.HasLiveBackgroundWork.Should().BeFalse();
    }

    [Fact]
    public void 逐字稿單行超過4MB時不卡死_自動跨過超長行並讀取後續步驟_且超長Artifact自動截斷閉合圍欄()
    {
        using var temp = new TempDirectory("agy-huge-line");
        var logPath = temp.Combine("transcript_full.jsonl");
        var utf8 = new UTF8Encoding(false);

        using (var writer = new StreamWriter(logPath, append: false, utf8))
        {
            writer.Write("{\"step_index\":1,\"type\":\"TOOL_RESPONSE\",\"content\":\"");
            writer.Write(new string('A', AntigravityTranscriptReader.MaxSyncChunkBytes + 128 * 1024));
            writer.WriteLine("\"}");
            writer.WriteLine("""{"step_index":2,"type":"PLANNER_RESPONSE","thinking":"跨過超長行後的正常思考"}""");
        }

        var reader = new AntigravityTranscriptReader(_ => logPath);
        reader.BindConversation("conv-huge");
        var steps = reader.Sync();
        steps.Should().ContainSingle().Which.Thinking.Should().Be("跨過超長行後的正常思考");

        // 超長 Markdown Artifact 在未閉合程式碼區塊內截斷時自動補上 ``` 圍欄與提示
        var hugeMd = "# 報告\n\n```csharp\n" + new string('x', AntigravityTranscriptRules.MaxArtifactPreviewChars + 5000);
        var capped = AntigravityTranscriptRules.CapArtifactMarkdown(hugeMd);
        capped.Should().Contain("\n```\n\n> [!NOTE]\n> 預覽內容較長");
    }

    [Fact]
    public void 子代理模型歸屬_finished_reason判定_與外掛登入提示()
    {
        var parser = new AntigravityStreamParser("p");
        parser.BeginTurn();
        parser.Parse("""{"event":"init","conversation_id":"conv-1","init":{"model":"gemini-3-pro"}}""");

        // 1) 子代理 subagent_info.model 指定了 flash：token 歸屬到 flash，工具卡標題與摘要顯示模型
        var subStep = """
            {"event":"step_update","step_update":{"conversation_id":"conv-1","step_index":2,"state":"DONE","step_type":"tool","tool_name":"invoke_subagent","tool_info":{"name":"invoke_subagent","parameters":{"TypeName":"research","Role":"Quick Lookup","Model":"flash","Prompt":"查設定"},"output":"完成"},"subagent_info":{"conversation_id":"sub-77","type":"research","model":"gemini-3-flash","usage":{"input_tokens":200,"output_tokens":50,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":250}}}}
            """;
        var subEvents = parser.Parse(subStep).Events;
        subEvents.OfType<AgentTokenUsageEvent>().Single().Model.Should().Be("gemini-3-flash");
        subEvents.OfType<AgentToolStartedEvent>().Single().Title.Should().Be("Quick Lookup（research・gemini-3-flash）");
        subEvents.OfType<AgentToolCompletedEvent>().Single().Output.Should().Contain("模型：gemini-3-flash");

        // 2) 外掛 OAuth 未授權提示（純文字行）轉為 AgentNoticeEvent
        var plainRes = parser.Parse("⚠ The github plugin needs you to sign in. Open /plugin to sign in.");
        plainRes.PlainText.Should().NotBeNull();
        plainRes.Events.OfType<AgentNoticeEvent>().Single().Message.Should().Contain("github");

        // 3) finished_reason = interrupted（即使 status = ERROR）視為 Interrupted
        var interruptedRes = parser.Parse("""{"event":"result","result":{"conversation_id":"conv-1","status":"ERROR","finished_reason":"interrupted","error":"context canceled"}}""");
        interruptedRes.Events.OfType<AgentTurnCompletedEvent>().Single().Outcome.Should().Be(AgentTurnOutcome.Interrupted);

        // 4) finished_reason = print_timeout 且無 error 時給出明確逾時訊息
        parser.BeginTurn();
        var timeoutRes = parser.Parse("""{"event":"result","result":{"conversation_id":"conv-1","status":"ERROR","finished_reason":"print_timeout"}}""");
        var timeoutCompleted = timeoutRes.Events.OfType<AgentTurnCompletedEvent>().Single();
        timeoutCompleted.Outcome.Should().Be(AgentTurnOutcome.Failed);
        timeoutCompleted.ErrorMessage.Should().Contain("print_timeout");
    }
}
