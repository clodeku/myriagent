using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>逐行解析 claude stream-json 輸出（事件序列取自 Claude Code 2.1.293 探針實錄）</summary>
public class ClaudeStreamParserTests
{
    private static readonly string[] RecordedTurn =
    {
        """{"type":"system","subtype":"init","cwd":"C:\\work","session_id":"da509571-d5b2-48f2-aaf1-a92bc1f8982d","tools":["Bash"],"model":"claude-haiku-5-5","permissionMode":"default","apiKeySource":"none","claude_code_version":"2.1.293"}""",
        """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_A"}},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"使用者要我"}},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"這張圖是純"}},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"紅色。"}},"parent_tool_use_id":null}""",
        """{"type":"assistant","message":{"id":"msg_A","role":"assistant","content":[{"type":"text","text":"這張圖是純紅色。"}]},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_stop","index":1},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_Q","name":"AskUserQuestion","input":{}}},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"questions\": []}"}},"parent_tool_use_id":null}""",
        """{"type":"assistant","message":{"id":"msg_A","role":"assistant","content":[{"type":"tool_use","id":"toolu_Q","name":"AskUserQuestion","input":{"questions":[{"question":"你想喝哪種飲料？","header":"飲料選擇","multiSelect":false,"options":[{"label":"咖啡","description":"選擇咖啡"},{"label":"紅茶","description":"選擇紅茶"}]}]}}]},"parent_tool_use_id":null}""",
        """{"type":"control_request","request_id":"65fd92bb","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","display_name":"AskUserQuestion","input":{"questions":[{"question":"你想喝哪種飲料？","header":"飲料選擇","options":[{"label":"咖啡","description":"選擇咖啡"},{"label":"紅茶","description":"選擇紅茶"}],"multiSelect":false}]},"tool_use_id":"toolu_Q","requires_user_interaction":true}}""",
        """{"type":"stream_event","event":{"type":"content_block_stop","index":2},"parent_tool_use_id":null}""",
        """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"Your questions have been answered: \"你想喝哪種飲料？\"=\"紅茶\".","tool_use_id":"toolu_Q"}]},"parent_tool_use_id":null}""",
        """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","unifiedWindows":{"five_hour":{"utilization":0.15},"seven_day":{"utilization":0.05}}}}""",
        """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_B"}},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_W","name":"Write","input":{}}},"parent_tool_use_id":null}""",
        """{"type":"assistant","message":{"id":"msg_B","role":"assistant","content":[{"type":"tool_use","id":"toolu_W","name":"Write","input":{"file_path":"C:\\work\\chart.png","content":"..."}}]},"parent_tool_use_id":null}""",
        """{"type":"control_request","request_id":"perm-1","request":{"subtype":"can_use_tool","tool_name":"Write","display_name":"Write","input":{"file_path":"C:\\work\\chart.png","content":"..."},"description":"chart.png","permission_suggestions":[{"type":"setMode","mode":"acceptEdits","destination":"session"}],"tool_use_id":"toolu_W"}}""",
        """{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}""",
        """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_W","type":"tool_result","content":"File created","is_error":false}]},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_sub"}},"parent_tool_use_id":"toolu_task"}""",
        """{"type":"result","subtype":"success","is_error":false,"result":"你選的是紅茶。","session_id":"da509571-d5b2-48f2-aaf1-a92bc1f8982d","total_cost_usd":0.0022,"usage":{"input_tokens":6,"cache_creation_input_tokens":7048,"cache_read_input_tokens":64815,"output_tokens":419}}""",
    };

    private static List<AgentEvent> ParseAll(ClaudeStreamParser parser, IEnumerable<string> lines)
        => lines.SelectMany(l => parser.Parse(l).Events).ToList();

    [Fact]
    public void 實錄的一輪_產生文字問卷工具額度與完成事件()
    {
        var parser = new ClaudeStreamParser();
        var events = new List<AgentEvent>();
        var requests = new List<ClaudeControlRequest>();
        var sequence = new List<string>();
        AgentTurnCompletedEvent? completed = null;
        foreach (var line in RecordedTurn)
        {
            var result = parser.Parse(line);
            events.AddRange(result.Events);
            sequence.AddRange(result.Events.OfType<AgentToolStartedEvent>().Select(t => "tool:" + t.ToolId));
            if (result.ControlRequest != null)
            {
                requests.Add(result.ControlRequest);
                sequence.Add("request:" + result.ControlRequest.ToolName);
            }
            if (result.TurnCompleted != null)
                completed = result.TurnCompleted;
        }

        events.OfType<AgentSessionStartedEvent>().Single().SessionId.Should().Be("da509571-d5b2-48f2-aaf1-a92bc1f8982d");
        events.OfType<AgentSessionStartedEvent>().Single().Detail.Should().Be("Claude Code 2.1.293");
        events.OfType<AgentNoticeEvent>().Should().BeEmpty("apiKeySource 為 none（走訂閱）不提示");
        events.OfType<AgentPermissionModeChangedEvent>().Single().Mode.Should().Be(AgentPermissionMode.Ask);
        events.OfType<AgentThinkingDeltaEvent>().Single().Delta.Should().Be("使用者要我");
        string.Concat(events.OfType<AgentTextDeltaEvent>().Select(e => e.Delta)).Should().Be("這張圖是純紅色。");
        events.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("這張圖是純紅色。", "已串流的訊息不再以完整 assistant 區塊重複一次");
        events.OfType<AgentTextDeltaEvent>().Select(e => e.BlockId).Distinct().Should().ContainSingle()
            .Which.Should().Be(events.OfType<AgentTextCompletedEvent>().Single().BlockId);

        // 問卷工具不畫工具卡；Write 工具卡在核准請求之前建立、只建立一次，帶檔案路徑與差異
        var tools = events.OfType<AgentToolStartedEvent>().ToList();
        tools.Should().ContainSingle();
        tools[0].ToolId.Should().Be("toolu_W");
        tools[0].ToolName.Should().Be("寫入檔案");
        tools[0].ToolKind.Should().Be("write");
        tools[0].Title.Should().Be(@"C:\work\chart.png");
        tools[0].FilePath.Should().Be(@"C:\work\chart.png");
        tools[0].Diff!.Single().Should().Be(new AgentDiffLine("add", "..."));
        sequence.IndexOf("tool:toolu_W").Should().BeLessThan(sequence.IndexOf("request:Write"), "實測核准請求比 content_block_stop 先到，工具卡要以完整 assistant 區塊先建立");

        requests.Select(r => r.ToolName).Should().Equal("AskUserQuestion", "Write");
        requests[0].Subtype.Should().Be("can_use_tool");
        requests[1].Suggestions.Should().NotBeNull();
        requests[1].ToolUseId.Should().Be("toolu_W");
        requests[1].Description.Should().Be("chart.png");
        parser.GetToolName("toolu_Q").Should().Be("AskUserQuestion");

        // AskUserQuestion 的 tool_result 不回報；Write 完成且是圖片檔 → 帶媒體路徑
        var done = events.OfType<AgentToolCompletedEvent>().Single();
        done.ToolId.Should().Be("toolu_W");
        done.Status.Should().Be(AgentItemStatus.Done);
        done.Output.Should().Be("File created");
        done.MediaPaths.Should().Equal(@"C:\work\chart.png");

        // 額度：原始 rate_limit_info 交給 Usage 模組解析
        var rate = events.OfType<AgentRateLimitEvent>().Single();
        rate.Provider.Should().Be(AgentProvider.Claude);
        rate.Raw["unifiedWindows"]!["five_hour"]!["utilization"]!.Value<double>().Should().Be(0.15);

        completed.Should().NotBeNull();
        completed!.Outcome.Should().Be(AgentTurnOutcome.Completed);
        completed.Usage!.OutputTokens.Should().Be(419);
        completed.Usage.InputTokens.Should().Be(6 + 7048 + 64815);
        completed.Usage.CachedInputTokens.Should().Be(64815);
        completed.Usage.CostUsd.Should().Be(0.0022);
    }

    [Fact]
    public void TodoWrite_發待辦清單事件_不畫工具卡也不回報完成()
    {
        var parser = new ClaudeStreamParser();
        var events = ParseAll(parser, new[]
        {
            """{"type":"assistant","message":{"id":"msg_T","content":[{"type":"tool_use","id":"toolu_T","name":"TodoWrite","input":{"todos":[{"content":"讀取報表","status":"completed","activeForm":"讀取報表中"},{"content":"彙總","status":"in_progress","activeForm":"彙總中"},{"content":"寄出","status":"pending","activeForm":"寄出中"}]}}]},"parent_tool_use_id":null}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"Todos have been modified successfully","tool_use_id":"toolu_T"}]},"parent_tool_use_id":null}""",
        });

        events.OfType<AgentToolStartedEvent>().Should().BeEmpty();
        events.OfType<AgentToolCompletedEvent>().Should().BeEmpty();
        var plan = events.OfType<AgentPlanEvent>().Single();
        plan.Explanation.Should().BeNull();
        plan.Steps.Select(s => $"{s.Status}:{s.Text}").Should().Equal("completed:讀取報表", "inProgress:彙總中", "pending:寄出");
    }

    [Fact]
    public void Edit_工具卡帶差異_完成後不是媒體檔就不帶媒體()
    {
        var parser = new ClaudeStreamParser();
        var events = ParseAll(parser, new[]
        {
            """{"type":"assistant","message":{"id":"msg_E","content":[{"type":"tool_use","id":"toolu_E","name":"Edit","input":{"file_path":"C:\\w\\Program.cs","old_string":"Console.WriteLine(1);","new_string":"Console.WriteLine(2);\nConsole.WriteLine(3);"}}]},"parent_tool_use_id":null}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"The file has been updated.","tool_use_id":"toolu_E"}]},"parent_tool_use_id":null}""",
        });

        var tool = events.OfType<AgentToolStartedEvent>().Single();
        tool.ToolName.Should().Be("修改檔案");
        tool.ToolKind.Should().Be("edit");
        tool.FilePath.Should().Be(@"C:\w\Program.cs");
        tool.Diff!.Select(l => $"{l.T}:{l.S}").Should().Equal("del:Console.WriteLine(1);", "add:Console.WriteLine(2);", "add:Console.WriteLine(3);");
        events.OfType<AgentToolCompletedEvent>().Single().MediaPaths.Should().BeEmpty();
    }

    [Fact]
    public void 工具失敗_狀態為Failed且不帶媒體()
    {
        var parser = new ClaudeStreamParser();
        var events = ParseAll(parser, new[]
        {
            """{"type":"assistant","message":{"id":"msg_R","content":[{"type":"tool_use","id":"toolu_R","name":"Read","input":{"file_path":"C:\\w\\missing.png"}}]},"parent_tool_use_id":null}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"File does not exist.","is_error":true,"tool_use_id":"toolu_R"}]},"parent_tool_use_id":null}""",
        });

        events.OfType<AgentToolStartedEvent>().Single().ToolKind.Should().Be("image");
        var done = events.OfType<AgentToolCompletedEvent>().Single();
        done.Status.Should().Be(AgentItemStatus.Failed);
        done.Output.Should().Be("File does not exist.");
        done.MediaPaths.Should().BeEmpty();
    }

    [Fact]
    public void ExitPlanMode_不畫工具卡_由can_use_tool畫成計畫確認卡()
    {
        var parser = new ClaudeStreamParser();
        var assistant = parser.Parse("""{"type":"assistant","message":{"id":"msg_P","content":[{"type":"tool_use","id":"toolu_P","name":"ExitPlanMode","input":{"plan":"## 計畫\n1. 改 A"}}]},"parent_tool_use_id":null}""");
        var request = parser.Parse("""{"type":"control_request","request_id":"plan-1","request":{"subtype":"can_use_tool","tool_name":"ExitPlanMode","input":{"plan":"## 計畫\n1. 改 A"},"tool_use_id":"toolu_P"}}""");
        var result = parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"User has approved your plan.","tool_use_id":"toolu_P"}]},"parent_tool_use_id":null}""");

        assistant.Events.Should().BeEmpty();
        request.ControlRequest!.ToolName.Should().Be("ExitPlanMode");
        request.ControlRequest.Input.Value<string>("plan").Should().Be("## 計畫\n1. 改 A");
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public void RateLimitEvent_發額度事件_被拒時另發錯誤提示()
    {
        var parser = new ClaudeStreamParser();
        var allowed = parser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1791436800,"unifiedWindows":{"five_hour":{"utilization":0.15,"resetsAt":1791436800}}}}""");
        allowed.Events.Should().ContainSingle().Which.Should().BeOfType<AgentRateLimitEvent>();

        var rejected = parser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1791436800,"rateLimitType":"five_hour","unifiedWindows":{"five_hour":{"utilization":1.0}}}}""");
        rejected.Events.OfType<AgentRateLimitEvent>().Single().Raw.Value<string>("status").Should().Be("rejected");
        var notice = rejected.Events.OfType<AgentNoticeEvent>().Single();
        notice.IsError.Should().BeTrue();
        notice.Message.Should().StartWith("Claude 訂閱額度已用完");

        parser.Parse("""{"type":"rate_limit_event"}""").Events.Should().BeEmpty("沒有 rate_limit_info 就不發");
    }

    [Fact]
    public void 錯誤結果_回報失敗與訊息()
    {
        var parser = new ClaudeStreamParser();
        var result = parser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":0,"errors":["[ede_diagnostic] result_type=user"]}""");
        result.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Failed);
        result.TurnCompleted.ErrorMessage.Should().Contain("ede_diagnostic");

        var withText = parser.Parse("""{"type":"result","subtype":"success","is_error":true,"result":"API Error: 529 overloaded"}""");
        withText.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Failed);
        withText.TurnCompleted.ErrorMessage.Should().Be("API Error: 529 overloaded");

        parser.Parse("""{"type":"result","subtype":"error_max_turns","is_error":true}""").TurnCompleted!.ErrorMessage.Should().Be("error_max_turns");
    }

    [Fact]
    public void 非JSON的行與空行_直接略過()
    {
        var parser = new ClaudeStreamParser();
        var result = parser.Parse("No conversation found with session ID: 3f1d2c4e");
        result.Events.Should().BeEmpty();
        result.TurnCompleted.Should().BeNull();
        result.ControlRequest.Should().BeNull();
        parser.Parse("   ").Events.Should().BeEmpty();
    }

    [Fact]
    public void Init帶APIKey來源_提示走API計費_規劃模式回報權限模式()
    {
        var parser = new ClaudeStreamParser();
        var result = parser.Parse("""{"type":"system","subtype":"init","session_id":"s","apiKeySource":"ANTHROPIC_API_KEY","permissionMode":"plan"}""");
        result.Events.OfType<AgentNoticeEvent>().Single().Message.Should().Contain("API Key");
        result.Events.OfType<AgentPermissionModeChangedEvent>().Single().Mode.Should().Be(AgentPermissionMode.Plan);
    }

    [Fact]
    public void 系統事件_壓縮與重試_轉成提示()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"system","subtype":"compact_boundary"}""").Events.OfType<AgentNoticeEvent>().Single().Message.Should().Contain("壓縮");
        parser.Parse("""{"type":"system","subtype":"api_retry"}""").Events.OfType<AgentNoticeEvent>().Single().Message.Should().Contain("重試");
        parser.Parse("""{"type":"system","subtype":"hook_response"}""").Events.Should().BeEmpty();
    }

    [Fact]
    public void 工作階段名稱改變_轉成名稱事件_空的或不是字串就略過()
    {
        var parser = new ClaudeStreamParser();
        // 2.1.308 實錄（id 換成假的；探針 claude_rename_probe.py）
        parser.Parse("""{"type":"system","subtype":"session_title_changed","title":"探針 改名 C","uuid":"u1","session_id":"s"}""")
            .Events.OfType<AgentSessionTitleEvent>().Single().Title.Should().Be("探針 改名 C");
        parser.Parse("""{"type":"system","subtype":"session_title_changed","title":"  前後空白  ","session_id":"s"}""")
            .Events.OfType<AgentSessionTitleEvent>().Single().Title.Should().Be("前後空白");
        foreach (var bad in new[] { "\"\"", "\"  \"", "null", "42", "[\"x\"]" })
            parser.Parse("{\"type\":\"system\",\"subtype\":\"session_title_changed\",\"title\":" + bad + "}").Events.Should().BeEmpty(bad);
        parser.Parse("""{"type":"system","subtype":"session_title_changed"}""").Events.Should().BeEmpty();
    }

    [Fact]
    public void 連續重試_同一輪只提示一次_下一輪再重試才再提示()
    {
        // CLI 每次重試都送一則 api_retry（帶 attempt／max_retries），每則都加提示會在紀錄裡堆一串「連線不穩」
        var parser = new ClaudeStreamParser();
        var notices = new List<AgentNoticeEvent>();
        foreach (var attempt in Enumerable.Range(1, 6))
            notices.AddRange(parser.Parse($$"""{"type":"system","subtype":"api_retry","attempt":{{attempt}},"max_retries":10,"retry_delay_ms":500}""").Events.OfType<AgentNoticeEvent>());
        notices.Should().ContainSingle();

        parser.Parse("""{"type":"result","subtype":"success","is_error":false,"result":"ok"}""");
        parser.Parse("""{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10}""").Events.OfType<AgentNoticeEvent>().Should().ContainSingle("新的一輪又遇到重試");
    }

    /// <summary>
    /// 2026-10-31 實機：送出請求 1.2 秒後連線中斷（ECONNRESET），CLI 送 api_retry 並重送請求；被丟下的那則回應已經送了工具的 content_block_start，
    /// 重試的回應是新的訊息與新的工具 id。舊的工具一直算在執行，這一輪剩下的時間都等不到安全點（換版排空 35 分鐘後放棄）
    /// </summary>
    [Fact]
    public void 串流中斷重送_被丟下的那則回應裡的工具_下一則訊息開始時不再算執行中()
    {
        var parser = new ClaudeStreamParser();
        var events = new List<AgentEvent>();
        void Feed(string line) => events.AddRange(parser.Parse(line).Events);

        Feed("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_lost"}},"parent_tool_use_id":null}""");
        Feed("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_lost","name":"Read","input":{}}},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeTrue("串流到的工具從 content_block_start 起算執行中");

        Feed("""{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"retry_delay_ms":618,"error_status":null,"error":"unknown"}""");
        parser.ToolRunning.Should().BeTrue("背景子代理的重試也送 api_retry，重試本身不拿掉主對話的工具");

        Feed("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_retry"}},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeFalse("重試的回應開始了：被丟下的那則回應的工具不會執行，也不會有結果");

        Feed("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_new","name":"Read","input":{}}},"parent_tool_use_id":null}""");
        Feed("""{"type":"assistant","message":{"id":"msg_retry","role":"assistant","content":[{"type":"tool_use","id":"toolu_new","name":"Read","input":{"file_path":"note.txt"}}]},"parent_tool_use_id":null}""");
        Feed("""{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeTrue();

        Feed("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"ok","tool_use_id":"toolu_new"}]},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeFalse("重試後的工具照常以結果結束：這時就是安全點");

        events.OfType<AgentToolStartedEvent>().Select(e => e.ToolId).Should().Equal("toolu_new");
        events.OfType<AgentToolCompletedEvent>().Select(e => e.ToolId).Should().Equal(new[] { "toolu_new" }, "沒畫卡的工具不發結束事件");

        // 被丟下的工具之後不會再被加回（例如晚到的重複行）
        Feed("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_next"}},"parent_tool_use_id":null}""");
        Feed("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_lost","name":"Read","input":{}}},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeFalse();
    }

    [Fact]
    public void 串流中斷重送_被丟下的工具已畫卡_改為已取消()
    {
        // 沒收到 assistant 區塊、只到 content_block_stop 的退路會畫卡；那則回應被丟下時卡片不能一直顯示執行中
        var parser = new ClaudeStreamParser();
        var events = new List<AgentEvent>();
        void Feed(string line) => events.AddRange(parser.Parse(line).Events);

        Feed("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_lost"}},"parent_tool_use_id":null}""");
        Feed("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_lost","name":"Bash","input":{}}},"parent_tool_use_id":null}""");
        Feed("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\": \"echo hi\"}"}},"parent_tool_use_id":null}""");
        Feed("""{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}""");
        events.OfType<AgentToolStartedEvent>().Should().ContainSingle(e => e.ToolId == "toolu_lost");

        Feed("""{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10}""");
        Feed("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_retry"}},"parent_tool_use_id":null}""");

        parser.ToolRunning.Should().BeFalse();
        var done = events.OfType<AgentToolCompletedEvent>().Single();
        done.ToolId.Should().Be("toolu_lost");
        done.Status.Should().Be(AgentItemStatus.Cancelled);
        done.Output.Should().Be(ClaudeStreamRules.AbandonedToolOutput);
    }

    [Fact]
    public void assistant區塊確認過的工具_下一則訊息開始也照樣算執行中()
    {
        // 正常的工具一定有 assistant 區塊；只有被丟下的回應才沒有。確認過的要等自己的結果（或這一輪結束）
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_A"}},"parent_tool_use_id":null}""");
        parser.Parse("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_a","name":"Bash","input":{}}},"parent_tool_use_id":null}""");
        parser.Parse("""{"type":"assistant","message":{"id":"msg_A","role":"assistant","content":[{"type":"tool_use","id":"toolu_a","name":"Bash","input":{"command":"sleep 60"}}]},"parent_tool_use_id":null}""");
        parser.Parse("""{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}""");

        // 子代理的串流（parent_tool_use_id 有值）與另一則主對話訊息的開始都不影響它
        parser.Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_sub"}},"parent_tool_use_id":"toolu_a"}""");
        parser.ToolRunning.Should().BeTrue();
        parser.Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_B"}},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeTrue();

        parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"done","tool_use_id":"toolu_a"}]},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeFalse();
    }

    [Fact]
    public void Init每輪都送_API計費提醒只提示一次_重開程序時可以把已提示帶給新的解析器()
    {
        const string init = """{"type":"system","subtype":"init","session_id":"s","apiKeySource":"/login managed key","permissionMode":"default"}""";
        var parser = new ClaudeStreamParser();
        parser.Parse(init).Events.OfType<AgentNoticeEvent>().Should().ContainSingle();
        parser.Parse(init).Events.OfType<AgentNoticeEvent>().Should().BeEmpty("第二輪的 init 不再提示");
        parser.Parse(init).Events.OfType<AgentSessionStartedEvent>().Should().ContainSingle("init 的其他事件照常發出");

        var next = new ClaudeStreamParser { ApiKeyNoticeShown = parser.ApiKeyNoticeShown };
        next.Parse(init).Events.OfType<AgentNoticeEvent>().Should().BeEmpty("同一個對話重開程序也不再提示");
    }

    [Fact]
    public void Status帶權限模式_回報模式改變_沒帶就不發()
    {
        // 實測 set_permission_mode 之後 CLI 立即送 system/status permissionMode；代理自己進出規劃模式也會改模式
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"system","subtype":"status","permissionMode":"acceptEdits","session_id":"s"}""")
            .Events.OfType<AgentPermissionModeChangedEvent>().Single().Mode.Should().Be(AgentPermissionMode.AcceptEdits);
        parser.Parse("""{"type":"system","subtype":"status","status":"requesting","session_id":"s"}""").Events.Should().BeEmpty();
    }

    [Fact]
    public void ControlCancelRequest_回報被撤回的請求()
    {
        var parser = new ClaudeStreamParser();
        var result = parser.Parse("""{"type":"control_cancel_request","request_id":"perm-7"}""");
        result.CancelledRequestId.Should().Be("perm-7");
        result.ControlRequest.Should().BeNull();
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public void Result的Origin不是使用者_標記為CLI自己處理的輪次()
    {
        // 實錄（2.1.295，--resume 時佇列裡有背景工作通知）：先送這個 result，之後才處理使用者剛送的訊息
        var parser = new ClaudeStreamParser();
        var notification = parser.Parse("""{"is_error":false,"duration_api_ms":0,"num_turns":0,"stop_reason":null,"session_id":"e0b92ba9","total_cost_usd":0.0073527,"permission_denials":[],"origin":{"kind":"task-notification"},"subtype":"success","result":"","type":"result","duration_ms":28}""");
        notification.TurnCompleted.Should().NotBeNull();
        notification.TurnFromOtherOrigin.Should().BeTrue();

        var user = parser.Parse("""{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"藍鯨42","terminal_reason":"completed"}""");
        user.TurnFromOtherOrigin.Should().BeFalse();
    }

    [Fact]
    public void TaskCreate與TaskUpdate_累積成待辦清單_不畫工具卡也不回報完成()
    {
        // 2.1.295 起舊模型（Haiku 4.5、Sonnet 4.x）預設停用 TodoWrite，改用 TaskCreate／TaskUpdate；編號由 TaskCreate 的結果給出
        var parser = new ClaudeStreamParser();
        var plans = new List<AgentPlanEvent>();
        var others = new List<AgentEvent>();
        void Feed(string line)
        {
            foreach (var ev in parser.Parse(line).Events)
            {
                if (ev is AgentPlanEvent plan)
                    plans.Add(plan);
                else
                    others.Add(ev);
            }
        }

        Feed("""{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"tc1","name":"TaskCreate","input":{"subject":"讀取報表","description":"讀進來","activeForm":"讀取報表中"}},{"type":"tool_use","id":"tc2","name":"TaskCreate","input":{"title":"寄出","description":"寄給主管"}}]},"parent_tool_use_id":null}""");
        plans.Last().Steps.Select(s => $"{s.Status}:{s.Text}").Should().Equal("pending:讀取報表", "pending:寄出");

        Feed("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tc1","content":"Task #1 created successfully: 讀取報表"},{"type":"tool_result","tool_use_id":"tc2","content":[{"type":"text","text":"Task #2 created successfully: 寄出"}]}]},"parent_tool_use_id":null}""");
        Feed("""{"type":"assistant","message":{"id":"m2","content":[{"type":"tool_use","id":"tu1","name":"TaskUpdate","input":{"taskId":"1","status":"in_progress"}}]},"parent_tool_use_id":null}""");
        plans.Last().Steps.Select(s => $"{s.Status}:{s.Text}").Should().Equal("inProgress:讀取報表中", "pending:寄出");

        Feed("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tu1","content":"Updated task #1 status"}]},"parent_tool_use_id":null}""");
        Feed("""{"type":"assistant","message":{"id":"m3","content":[{"type":"tool_use","id":"tu2","name":"TaskUpdate","input":{"taskId":"1","status":"completed"}},{"type":"tool_use","id":"tu3","name":"TaskUpdate","input":{"id":"2","status":"deleted"}},{"type":"tool_use","id":"tu4","name":"TaskUpdate","input":{"taskId":"9","status":"in_progress","subject":"重開程序前建立的"}}]},"parent_tool_use_id":null}""");
        plans.Last().Steps.Select(s => $"{s.Status}:{s.Text}").Should().Equal("completed:讀取報表", "inProgress:重開程序前建立的");

        others.Should().BeEmpty("TaskCreate／TaskUpdate 不畫工具卡，它們的 tool_result 也不回報");
    }

    [Fact]
    public void TaskCreate失敗就從清單拿掉_輸入不完整改畫一般工具卡()
    {
        var parser = new ClaudeStreamParser();
        var created = parser.Parse("""{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"tc1","name":"TaskCreate","input":{"subject":"會失敗的"}}]},"parent_tool_use_id":null}""");
        created.Events.OfType<AgentPlanEvent>().Single().Steps.Should().ContainSingle();
        var failed = parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tc1","content":"error","is_error":true}]},"parent_tool_use_id":null}""");
        failed.Events.OfType<AgentPlanEvent>().Single().Steps.Should().BeEmpty();
        failed.Events.OfType<AgentToolCompletedEvent>().Should().BeEmpty();

        var invalid = parser.Parse("""{"type":"assistant","message":{"id":"m2","content":[{"type":"tool_use","id":"tc2","name":"TaskCreate","input":{"description":"沒有標題"}},{"type":"tool_use","id":"tu1","name":"TaskUpdate","input":{"status":"completed"}}]},"parent_tool_use_id":null}""");
        invalid.Events.OfType<AgentPlanEvent>().Should().BeEmpty();
        invalid.Events.OfType<AgentToolStartedEvent>().Select(t => t.ToolName).Should().Equal("新增待辦", "更新待辦");
        parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"tc2","content":"InputValidationError","is_error":true}]},"parent_tool_use_id":null}""")
            .Events.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Failed, "畫成工具卡的照常回報結果");
    }

    [Fact]
    public void 沒有串流的完整回覆_仍產生完成文字()
    {
        var parser = new ClaudeStreamParser();
        var result = parser.Parse("""{"type":"assistant","message":{"id":"msg_X","content":[{"type":"text","text":"沒有串流的回覆"},{"type":"text","text":""}]},"parent_tool_use_id":null}""");
        result.Events.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("沒有串流的回覆");
    }

    [Fact]
    public void 子代理內部訊息_對不到主對話的Agent卡時不顯示()
    {
        var parser = new ClaudeStreamParser();
        var events = ParseAll(parser, new[]
        {
            """{"type":"assistant","message":{"id":"msg_S","content":[{"type":"text","text":"子代理說的話"},{"type":"tool_use","id":"toolu_S","name":"Bash","input":{"command":"ls"}}]},"parent_tool_use_id":"toolu_task"}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"x"}},"parent_tool_use_id":"toolu_task"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"a.txt","tool_use_id":"toolu_S"}]},"parent_tool_use_id":"toolu_task"}""",
        });
        events.Should().BeEmpty();
    }

    /// <summary>實錄（2.1.310，--forward-subagent-text，tools/probes/claude_cli3_probe.py --mode subagent）的形狀：子代理的指示、工具呼叫與結果、最後的回覆</summary>
    [Fact]
    public void 子代理的過程_接在Agent卡的輸出_結果到時放在結論前面()
    {
        var parser = new ClaudeStreamParser();
        var events = ParseAll(parser, new[]
        {
            """{"type":"assistant","message":{"id":"msg_A","content":[{"type":"tool_use","id":"toolu_agent","name":"Agent","input":{"description":"讀檔","subagent_type":"general-purpose","prompt":"讀 note.txt"}}]},"parent_tool_use_id":null}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"讀 note.txt"}]},"parent_tool_use_id":"toolu_agent","agent_id":"a1","subagent_type":"general-purpose"}""",
            """{"type":"assistant","message":{"id":"msg_S1","content":[{"type":"tool_use","id":"toolu_r","name":"Read","input":{"file_path":"C:\\w\\note.txt"}}]},"parent_tool_use_id":"toolu_agent"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"1\t第一行","tool_use_id":"toolu_r"}]},"parent_tool_use_id":"toolu_agent"}""",
            """{"type":"assistant","message":{"id":"msg_S2","content":[{"type":"tool_use","id":"toolu_b","name":"Bash","input":{"command":"echo sub-ok\nexit 1"}}]},"parent_tool_use_id":"toolu_agent"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"Exit code 1\nsub-ok","is_error":true,"tool_use_id":"toolu_b"}]},"parent_tool_use_id":"toolu_agent"}""",
            """{"type":"assistant","message":{"id":"msg_S3","content":[{"type":"text","text":"第一行是\n「第一行」"}]},"parent_tool_use_id":"toolu_agent"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":[{"type":"text","text":"note.txt 的第一行是「第一行」"}],"tool_use_id":"toolu_agent"}]},"parent_tool_use_id":null}""",
        });

        events.OfType<AgentToolStartedEvent>().Should().ContainSingle(e => e.ToolId == "toolu_agent", "子代理的工具不另畫卡片");
        events.OfType<AgentToolOutputEvent>().Select(e => (e.ToolId, e.Delta)).Should().Equal(
            ("toolu_agent", "▸ 讀取檔案：C:\\w\\note.txt\n"),
            ("toolu_agent", "▸ 執行指令：echo sub-ok\n"),
            ("toolu_agent", "  ✗ Exit code 1 sub-ok\n"),
            ("toolu_agent", "💬 第一行是 「第一行」\n"));
        var done = events.OfType<AgentToolCompletedEvent>().Single();
        done.ToolId.Should().Be("toolu_agent");
        done.Output.Should().Be(
            "── 子代理執行紀錄（共 4 項步驟）──\n▸ 讀取檔案：C:\\w\\note.txt\n▸ 執行指令：echo sub-ok\n  ✗ Exit code 1 sub-ok\n💬 第一行是 「第一行」\n\n"
            + "── 子代理結論 ──\nnote.txt 的第一行是「第一行」");
        parser.ToolRunning.Should().BeFalse("子代理的工具不算主對話的工具在執行");
    }

    [Fact]
    public void 子代理的過程_巢狀子代理接在最外層的卡_卡片有結果之後不再接()
    {
        var parser = new ClaudeStreamParser();
        var events = ParseAll(parser, new[]
        {
            """{"type":"assistant","message":{"id":"msg_A","content":[{"type":"tool_use","id":"toolu_outer","name":"Task","input":{"description":"外層","prompt":"做事"}}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_S1","content":[{"type":"tool_use","id":"toolu_inner","name":"Agent","input":{"description":"內層","prompt":"再派"}}]},"parent_tool_use_id":"toolu_outer"}""",
            """{"type":"assistant","message":{"id":"msg_S2","content":[{"type":"tool_use","id":"toolu_g","name":"Grep","input":{"pattern":"TODO"}}]},"parent_tool_use_id":"toolu_inner"}""",
            // 背景子代理：結果一開始就回來，之後的過程不再接到已經結束的卡
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"Async agent launched","tool_use_id":"toolu_outer"}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_S3","content":[{"type":"tool_use","id":"toolu_g2","name":"Grep","input":{"pattern":"FIXME"}}]},"parent_tool_use_id":"toolu_inner"}""",
        });

        events.OfType<AgentToolOutputEvent>().Select(e => e.ToolId).Should().Equal("toolu_outer", "toolu_outer");
        events.OfType<AgentToolOutputEvent>().Last().Delta.Should().Be("▸ 搜尋內容：TODO\n");
        events.OfType<AgentToolCompletedEvent>().Single().Output.Should().Be(
            "── 子代理執行紀錄（共 2 項步驟）──\n▸ 子代理：內層\n▸ 搜尋內容：TODO\n\n── 子代理結論 ──\nAsync agent launched");
    }

    [Fact]
    public void 子代理的過程_只留最後幾行_標頭寫出總數()
    {
        var steps = new[] { "▸ a", "▸ b" };
        ClaudeStreamRules.WithSubagentTranscript(steps, 5, "結論").Should().Be(
            "── 子代理執行紀錄（共 5 項步驟，只列最後 2 項）──\n▸ a\n▸ b\n\n── 子代理結論 ──\n結論");
        ClaudeStreamRules.OneLine("  第一行\r\n\t第二行  ", 300).Should().Be("第一行 第二行");
        ClaudeStreamRules.OneLine(new string('字', 10), 4).Should().Be("字字字字…");
        ClaudeStreamRules.OneLine("ab😀cd", 3).Should().Be("ab…", "不切開代理字組");
        ClaudeStreamRules.DescribeSubagentStep(JObject.Parse("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"想一想\n再說"},{"type":"text","text":"  "}]}}"""))
            .Should().Equal("💭 思考：想一想 再說");
        ClaudeStreamRules.DescribeSubagentStep(JObject.Parse("""{"type":"user","message":{"content":"字串內容"}}""")).Should().BeEmpty();
    }

    [Fact]
    public void 沒收到完整Assistant區塊時_以累積的InputJson建立工具卡()
    {
        var parser = new ClaudeStreamParser();
        var events = ParseAll(parser, new[]
        {
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_F"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_F","name":"Bash","input":{}}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\": \"dir"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":" /b\"}"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_F","content":[{"type":"tool_use","id":"toolu_F","name":"Bash","input":{"command":"dir /b"}}]},"parent_tool_use_id":null}""",
        });

        var tool = events.OfType<AgentToolStartedEvent>().Should().ContainSingle("同一個工具只建立一張卡").Subject;
        tool.Title.Should().Be("dir /b");
        tool.ToolKind.Should().Be("bash");
    }

    [Fact]
    public void 累積的InputJson壞掉時_仍建立空白輸入的工具卡()
    {
        var parser = new ClaudeStreamParser();
        var events = ParseAll(parser, new[]
        {
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_G"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_G","name":"Bash","input":{}}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\": "}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}""",
        });

        var tool = events.OfType<AgentToolStartedEvent>().Single();
        tool.ToolName.Should().Be("執行指令");
        tool.Title.Should().BeEmpty();
    }

    [Fact]
    public void ControlResponse_解析Initialize回應()
    {
        var parser = new ClaudeStreamParser();
        var ok = parser.Parse("""{"type":"control_response","response":{"subtype":"success","request_id":"init-1","response":{"models":[{"value":"opus","displayName":"Opus 5.5"}]}}}""");
        ok.ControlResponse!.RequestId.Should().Be("init-1");
        ok.ControlResponse.Success.Should().BeTrue();
        ClaudeStreamRules.ParseModels(ok.ControlResponse.Response).Single().Value.Should().Be("opus");

        var failed = parser.Parse("""{"type":"control_response","response":{"subtype":"error","request_id":"mode-2","error":"Cannot set permission mode to bypassPermissions"}}""");
        failed.ControlResponse!.Success.Should().BeFalse();
        failed.ControlResponse.Error.Should().Contain("bypassPermissions");

        parser.Parse("""{"type":"control_response","response":{"subtype":"success"}}""").ControlResponse.Should().BeNull("沒有 request_id 無法對應");
    }

    [Fact]
    public void BridgeState_取出狀態失敗種類與說明_不算這一輪的活動()
    {
        var parser = new ClaudeStreamParser();

        // 2.1.300 實錄：開啟 Remote Control 後的 ready、connected
        var ready = parser.Parse("""{"type":"system","subtype":"bridge_state","state":"ready","uuid":"u1","session_id":"s1"}""");
        ready.BridgeState.Should().Be("ready");
        ready.BridgeFailureKind.Should().BeNull();
        ready.TurnActivity.Should().BeFalse("閒置時的連線狀態不能被當成 CLI 自己開了一輪");
        ready.TurnInit.Should().BeFalse();
        ready.Events.Should().BeEmpty();
        parser.Parse("""{"type":"system","subtype":"bridge_state","state":"connected","bridge_epoch":1,"uuid":"u2","session_id":"s1"}""").BridgeState.Should().Be("connected");

        var failed = parser.Parse("""{"type":"system","subtype":"bridge_state","state":"failed","detail":"network gone","failure_kind":"terminal","bridge_epoch":1}""");
        failed.BridgeState.Should().Be("failed");
        failed.BridgeFailureKind.Should().Be("terminal");
        failed.BridgeDetail.Should().Be("network gone");

        var odd = parser.Parse("""{"type":"system","subtype":"bridge_state","state":null,"detail":{"x":1}}""");
        odd.BridgeState.Should().Be(string.Empty, "欄位形狀不對也不丟例外");
        odd.BridgeDetail.Should().BeNull();
        parser.Parse("""{"type":"system","subtype":"status","status":"requesting"}""").BridgeState.Should().BeNull();
    }

    [Fact]
    public void 回送的使用者訊息_取uuid與遠端訊息_不當成工具結果也不算這一輪的活動()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]},"parent_tool_use_id":null}""");

        // 2.1.301 實錄（uuid 換成假的）：遠端送來的訊息在工具結果之後併進這一輪
        var remote = parser.Parse("""{"type":"user","message":{"role":"user","content":"只回覆 M 一個字母（遠端）"},"session_id":"s1","parent_tool_use_id":null,"uuid":"r-1","timestamp":"2026-10-16T05:00:00.000Z","isReplay":true,"origin":{"kind":"human"}}""");
        remote.ReplayUuid.Should().Be("r-1");
        remote.RemoteUserMessage.Should().Be(new ClaudeRemoteUserMessage("r-1", "只回覆 M 一個字母（遠端）", 0, 0));
        remote.TurnActivity.Should().BeFalse("CLI 換模型時的說明等也會在兩輪之間回送，不能當成 CLI 自己開了一輪");
        remote.Events.Should().BeEmpty();

        // app 自己的訊息回送：只有 uuid；內容就算長得像工具結果也不處理
        var own = parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"x"}]},"uuid":"u-1","isReplay":true,"parent_tool_use_id":null}""");
        own.ReplayUuid.Should().Be("u-1");
        own.RemoteUserMessage.Should().BeNull();
        own.Events.Should().BeEmpty();
        parser.ToolRunning.Should().BeTrue("回送的訊息不是工具結果");
    }

    [Fact]
    public void Command_lifecycle與result_取指令的uuid()
    {
        var parser = new ClaudeStreamParser();

        var started = parser.Parse("""{"type":"command_lifecycle","command_uuid":"u-1","state":"started","uuid":"e-1","session_id":"s1"}""");
        started.CommandLifecycleState.Should().Be("started");
        started.CommandUuid.Should().Be("u-1", "uuid 欄位是這個事件自己的，指令的是 command_uuid");

        var result = parser.Parse("""{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"OK","user_message_uuid":"u-1","session_id":"s1"}""");
        result.UserMessageUuid.Should().Be("u-1");
        parser.Parse("""{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"OK","user_message_uuid":null}""").UserMessageUuid.Should().BeNull();
    }

    [Fact]
    public void 停止的標記之後失敗的result_算已停止_只影響這一輪()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"assistant","message":{"id":"m1","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"sleep 8"}}]},"parent_tool_use_id":null}""");
        // 2.1.301 實錄（在手機上按停止）：工具結果、停止的標記、error_during_execution
        parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"Interrupted","is_error":true}]},"parent_tool_use_id":null,"uuid":"x1"}""");
        var marker = parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]},"parent_tool_use_id":null,"uuid":"x2"}""");
        marker.Events.Should().BeEmpty("標記不顯示");
        var stopped = parser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":3,"origin":{"kind":"human"},"user_message_uuid":"r-1"}""");
        stopped.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        stopped.TurnCompleted.ErrorMessage.Should().BeNull();

        // 下一輪沒有標記：照常是失敗
        parser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":1}""").TurnCompleted!.Outcome
            .Should().Be(AgentTurnOutcome.Failed, "標記只屬於它那一輪");

        // 有標記但這一輪照常完成（例如停止生效前就結束了）：不改
        parser.Parse("""{"type":"user","message":{"role":"user","content":"[Request interrupted by user]"},"uuid":"x3"}""");
        parser.Parse("""{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"OK"}""").TurnCompleted!.Outcome
            .Should().Be(AgentTurnOutcome.Completed);

        // 回送的訊息內容剛好是標記：不是 CLI 插進的
        parser.Parse("""{"type":"user","message":{"role":"user","content":"[Request interrupted by user]"},"uuid":"r-9","isReplay":true,"origin":{"kind":"human"}}""");
        parser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":1}""").TurnCompleted!.Outcome
            .Should().Be(AgentTurnOutcome.Failed);
    }
}
