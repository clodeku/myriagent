using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>解析 codex app-server 的輸出（事件取自 codex-cli 0.160.1 探針實錄）</summary>
public class CodexEventParserTests
{
    [Fact]
    public void 實錄的一輪_文字指令核准額度與完成()
    {
        var lines = new[]
        {
            """{"id":104,"result":{"turn":{"id":"turn-1","items":[],"status":"inProgress"}}}""",
            """{"method":"turn/started","params":{"threadId":"t","turn":{"id":"turn-1","status":"inProgress"}}}""",
            """{"method":"item/started","params":{"item":{"type":"agentMessage","id":"msg_1","text":"","phase":"commentary"},"threadId":"t","turnId":"turn-1"}}""",
            """{"method":"item/agentMessage/delta","params":{"threadId":"t","turnId":"turn-1","itemId":"msg_1","delta":"我會執行"}}""",
            """{"method":"item/completed","params":{"item":{"type":"agentMessage","id":"msg_1","text":"我會執行這個 ping。","phase":"commentary","questions":null},"threadId":"t","turnId":"turn-1"}}""",
            """{"method":"item/started","params":{"item":{"type":"commandExecution","id":"exec-1","command":"pwsh.exe -Command 'ping -n 3 127.0.0.1'","cwd":"C:\\w","status":"inProgress","commandActions":[{"type":"unknown","command":"ping -n 3 127.0.0.1"}]},"threadId":"t","turnId":"turn-1"}}""",
            """{"method":"item/commandExecution/requestApproval","id":0,"params":{"kind":"command","threadId":"t","turnId":"turn-1","itemId":"exec-1","command":"pwsh.exe -Command 'ping -n 3 127.0.0.1'","cwd":"C:\\w","commandActions":[{"type":"unknown","command":"ping -n 3 127.0.0.1"}],"availableDecisions":["accept","cancel"]}}""",
            """{"method":"serverRequest/resolved","params":{"threadId":"t","requestId":0}}""",
            """{"method":"item/commandExecution/outputDelta","params":{"threadId":"t","turnId":"turn-1","itemId":"exec-1","delta":"Reply from 127.0.0.1"}}""",
            """{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"exec-1","command":"x","status":"completed","aggregatedOutput":"Reply from 127.0.0.1","exitCode":0},"threadId":"t","turnId":"turn-1"}}""",
            """{"method":"thread/tokenUsage/updated","params":{"threadId":"t","turnId":"turn-1","tokenUsage":{"total":{"totalTokens":34449},"last":{"totalTokens":18361,"inputTokens":18259,"cachedInputTokens":15104,"outputTokens":102}}}}""",
            """{"method":"account/rateLimits/updated","params":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":1,"windowDurationMins":10080,"resetsAt":1791959804},"secondary":null}}}""",
            """{"method":"turn/completed","params":{"threadId":"t","turn":{"id":"turn-1","items":[],"status":"completed","error":null}}}""",
        };

        var parser = new CodexEventParser();
        var events = new List<AgentEvent>();
        var requests = new List<CodexServerRequest>();
        var responses = new List<JToken>();
        var notifications = new List<string>();
        string? turnId = null;
        AgentTurnCompletedEvent? completed = null;
        foreach (var line in lines)
        {
            var r = parser.Parse(line);
            events.AddRange(r.Events);
            if (r.ServerRequest != null)
                requests.Add(r.ServerRequest);
            if (r.Response is { } response)
                responses.Add(response.Id);
            if (r.Notification is { } n)
                notifications.Add(n.Method);
            turnId ??= r.StartedTurnId;
            completed ??= r.TurnCompleted;
        }

        responses.Should().ContainSingle().Which.Value<long>().Should().Be(104);
        turnId.Should().Be("turn-1");
        notifications.Should().Contain(new[] { "turn/started", "item/started", "turn/completed" });
        notifications.Should().NotContain("item/commandExecution/requestApproval", "有 id 的是伺服器請求，不是通知");
        events.OfType<AgentTextDeltaEvent>().Single().BlockId.Should().Be("msg_1");
        events.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("我會執行這個 ping。");
        var tool = events.OfType<AgentToolStartedEvent>().Single();
        tool.ToolId.Should().Be("exec-1");
        tool.ToolName.Should().Be("執行指令");
        tool.ToolKind.Should().Be("bash");
        tool.Title.Should().Be("ping -n 3 127.0.0.1");
        events.OfType<AgentToolOutputEvent>().Single().Delta.Should().Be("Reply from 127.0.0.1");
        var done = events.OfType<AgentToolCompletedEvent>().Single();
        done.Status.Should().Be(AgentItemStatus.Done);
        done.Output.Should().Be("Reply from 127.0.0.1");
        events.OfType<AgentRequestResolvedEvent>().Single().RequestKey.Should().Be("0");

        var rate = events.OfType<AgentRateLimitEvent>().Single();
        rate.Provider.Should().Be(AgentProvider.Codex);
        rate.Raw["primary"]!["usedPercent"]!.Value<int>().Should().Be(1, "傳的是 params.rateLimits 本身");

        requests.Should().ContainSingle();
        requests[0].Method.Should().Be("item/commandExecution/requestApproval");
        requests[0].Key.Should().Be("0");

        completed!.Outcome.Should().Be(AgentTurnOutcome.Completed);
        completed.Usage!.OutputTokens.Should().Be(102);
        completed.Usage.InputTokens.Should().Be(18259);
        completed.Usage.CachedInputTokens.Should().Be(15104);
    }

    [Fact]
    public void 一輪被停止或失敗()
    {
        var parser = new CodexEventParser();
        parser.Parse("""{"method":"turn/completed","params":{"turn":{"id":"u","status":"interrupted","error":null}}}""").TurnCompleted!.Outcome
            .Should().Be(AgentTurnOutcome.Interrupted);
        var failed = parser.Parse("""{"method":"turn/completed","params":{"turn":{"id":"u","status":"failed","error":{"message":"usage limit"}}}}""").TurnCompleted!;
        failed.Outcome.Should().Be(AgentTurnOutcome.Failed);
        failed.ErrorMessage.Should().Be("usage limit");
        failed.Usage.Should().BeNull("這一輪沒有收到 thread/tokenUsage/updated");
    }

    [Fact]
    public void 指令失敗或被拒_狀態對應()
    {
        var parser = new CodexEventParser();
        parser.Parse("""{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"e1","status":"completed","exitCode":1}}}""")
            .Events.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Failed, "結束碼非 0 算失敗");
        parser.Parse("""{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"e2","status":"declined"}}}""")
            .Events.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Declined);
        parser.Parse("""{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"e3","status":"failed","exitCode":0}}}""")
            .Events.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Failed);
    }

    [Fact]
    public void 生圖_回報存檔路徑當媒體_額度用完算失敗()
    {
        var parser = new CodexEventParser();
        parser.Parse("""{"method":"item/started","params":{"item":{"type":"imageGeneration","id":"ig_1","status":"inProgress","revisedPrompt":null,"result":""}}}""")
            .Events.OfType<AgentToolStartedEvent>().Single().ToolName.Should().Be("生成圖片");
        var done = parser.Parse("""{"method":"item/completed","params":{"item":{"type":"imageGeneration","id":"ig_1","status":"completed","revisedPrompt":"一隻貓","result":"","failure":null,"savedPath":"C:\\Users\\U\\.codex\\generated_images\\t\\a.png"}}}""")
            .Events.OfType<AgentToolCompletedEvent>().Single();
        done.MediaPaths.Should().Equal(@"C:\Users\U\.codex\generated_images\t\a.png");
        done.Output.Should().Be("一隻貓");

        var failed = parser.Parse("""{"method":"item/completed","params":{"item":{"type":"imageGeneration","id":"ig_2","failure":{"type":"quota"},"savedPath":"C:\\x.png"}}}""")
            .Events.OfType<AgentToolCompletedEvent>().Single();
        failed.Status.Should().Be(AgentItemStatus.Failed);
        failed.MediaPaths.Should().BeEmpty();
    }

    [Fact]
    public void 改檔_核准卡取得路徑與差異_完成時圖片檔當媒體_刪除的不算()
    {
        var parser = new CodexEventParser();
        var started = parser.Parse("""{"method":"item/started","params":{"item":{"type":"fileChange","id":"fc_1","status":"inProgress","changes":[{"path":"C:\\w\\a.txt","kind":{"type":"add"},"diff":"hello\nworld"},{"path":"C:\\w\\b.png","kind":{"type":"add"},"diff":""},{"path":"C:\\w\\old.png","kind":{"type":"delete"},"diff":""}]}}}""");

        var tool = started.Events.OfType<AgentToolStartedEvent>().Single();
        tool.ToolName.Should().Be("修改檔案");
        tool.ToolKind.Should().Be("edit");
        tool.Title.Should().Be(@"C:\w\a.txt 等 3 個檔案");
        tool.FilePath.Should().Be(@"C:\w\a.txt");
        tool.Diff!.Select(l => $"{l.T}:{l.S}").Should().Equal(@"hunk:C:\w\a.txt", "add:hello", "add:world", @"hunk:C:\w\b.png", @"hunk:C:\w\old.png");

        var (path, diff) = parser.GetFileChange("fc_1");
        path.Should().Be(@"C:\w\a.txt");
        diff.Should().NotBeNull();
        parser.GetFileChange("沒有這個").Should().Be(((string?)null, (IReadOnlyList<AgentDiffLine>?)null));

        var done = parser.Parse("""{"method":"item/completed","params":{"item":{"type":"fileChange","id":"fc_1","status":"completed","changes":[{"path":"C:\\w\\a.txt","kind":{"type":"add"},"diff":"hello"},{"path":"C:\\w\\b.png","kind":{"type":"add"},"diff":""},{"path":"C:\\w\\old.png","kind":{"type":"delete"},"diff":""}]}}}""")
            .Events.OfType<AgentToolCompletedEvent>().Single();
        done.MediaPaths.Should().Equal(@"C:\w\b.png");
    }

    [Fact]
    public void 改檔_單一檔案的UnifiedDiff_不加檔名標題()
    {
        var item = JObject.Parse("""{"type":"fileChange","id":"fc_2","changes":[{"path":"C:\\w\\a.cs","kind":{"type":"update","move_path":null},"diff":"@@ -1,2 +1,2 @@\n-int a = 1;\n+int a = 2;\n int b = 3;"}]}""");
        var (title, path, diff) = CodexEventParser.DescribeFileChanges(item);

        title.Should().Be(@"C:\w\a.cs");
        path.Should().Be(@"C:\w\a.cs");
        diff!.Select(l => $"{l.T}:{l.S}").Should().Equal("hunk:@@ -1,2 +1,2 @@", "del:int a = 1;", "add:int a = 2;", "ctx:int b = 3;");

        var (emptyTitle, emptyPath, emptyDiff) = CodexEventParser.DescribeFileChanges(JObject.Parse("""{"type":"fileChange","id":"x"}"""));
        emptyTitle.Should().Be("修改檔案");
        emptyPath.Should().BeNull();
        emptyDiff.Should().BeNull();
    }

    [Fact]
    public void 非同步提問_附在回覆上_以下一則訊息作答()
    {
        var parser = new CodexEventParser();
        var r = parser.Parse("""{"method":"item/completed","params":{"item":{"type":"agentMessage","id":"m9","text":"要用哪個格式？","questions":[{"title":"輸出格式","options":["Excel","CSV"]}]}}}""");
        r.Events.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("要用哪個格式？");
        var q = r.Events.OfType<AgentQuestionEvent>().Single();
        q.AnswerAsMessage.Should().BeTrue();
        q.RequestKey.Should().Be("async:m9");
        q.Questions.Single().Options.Select(o => o.Label).Should().Equal("Excel", "CSV");
    }

    [Fact]
    public void 待辦清單更新_轉成計畫事件()
    {
        var parser = new CodexEventParser();
        var plan = parser.Parse("""{"method":"turn/plan/updated","params":{"threadId":"t","turnId":"u","explanation":"先讀再改","plan":[{"step":"讀取設定","status":"completed"},{"step":"修改程式","status":"inProgress"},{"step":"","status":"pending"},{"step":"跑測試"}]}}""")
            .Events.OfType<AgentPlanEvent>().Single();
        plan.Explanation.Should().Be("先讀再改");
        plan.Steps.Select(s => $"{s.Status}:{s.Text}").Should().Equal("completed:讀取設定", "inProgress:修改程式", "pending:跑測試");
    }

    [Fact]
    public void 其他工具類項目_MCP搜尋檢視圖片與子代理()
    {
        var parser = new CodexEventParser();
        var mcp = parser.Parse("""{"method":"item/started","params":{"item":{"type":"mcpToolCall","id":"mc1","server":"github","tool":"create_issue","arguments":{"title":"bug"}}}}""")
            .Events.OfType<AgentToolStartedEvent>().Single();
        mcp.ToolName.Should().Be("MCP：github");
        mcp.ToolKind.Should().Be("mcp");
        mcp.Title.Should().Be("create_issue");
        var mcpDone = parser.Parse("""{"method":"item/completed","params":{"item":{"type":"mcpToolCall","id":"mc1","status":"completed","result":{"content":[{"type":"text","text":"已建立 #12"},{"type":"image"}]}}}}""")
            .Events.OfType<AgentToolCompletedEvent>().Single();
        mcpDone.Output.Should().Be("已建立 #12\n[非文字內容]");
        parser.Parse("""{"method":"item/completed","params":{"item":{"type":"mcpToolCall","id":"mc1","error":{"message":"權限不足"}}}}""")
            .Events.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Failed);

        parser.Parse("""{"method":"item/started","params":{"item":{"type":"webSearch","id":"ws1","query":"天氣"}}}""")
            .Events.OfType<AgentToolStartedEvent>().Single().ToolKind.Should().Be("web");

        var view = parser.Parse("""{"method":"item/started","params":{"item":{"type":"imageView","id":"iv1","path":"C:\\w\\shot.png"}}}""")
            .Events.OfType<AgentToolStartedEvent>().Single();
        view.ToolKind.Should().Be("image");
        view.FilePath.Should().Be(@"C:\w\shot.png");
        parser.Parse("""{"method":"item/completed","params":{"item":{"type":"imageView","id":"iv1","path":"C:\\w\\shot.png"}}}""")
            .Events.OfType<AgentToolCompletedEvent>().Single().MediaPaths.Should().Equal(@"C:\w\shot.png");

        parser.Parse("""{"method":"item/started","params":{"item":{"type":"collabAgentToolCall","id":"ca1","tool":"spawn","prompt":"幫我查"}}}""")
            .Events.OfType<AgentToolStartedEvent>().Single().ToolKind.Should().Be("agent");
    }

    [Fact]
    public void 錯誤通知與壓縮_轉成提示()
    {
        var parser = new CodexEventParser();
        var retry = parser.Parse("""{"method":"error","params":{"error":{"message":"stream disconnected"},"willRetry":true}}""").Events.OfType<AgentNoticeEvent>().Single();
        retry.IsError.Should().BeFalse();
        retry.Message.Should().Be("Codex 連線不穩，正在重試：stream disconnected");
        parser.Parse("""{"method":"thread/compacted","params":{}}""").Events.OfType<AgentNoticeEvent>().Single().Message.Should().Contain("壓縮");
    }

    [Fact]
    public void 錯誤通知_重試只提示一次_最後的錯誤只隨這一輪的失敗顯示一次()
    {
        // 實測：未登入時一輪收到 9 則 willRetry:true，接著 willRetry:false 的 error，再來 turn/completed（failed，error 是同一段文字）
        var parser = new CodexEventParser();
        var events = new List<AgentEvent>();
        parser.Parse("""{"method":"turn/started","params":{"threadId":"t","turn":{"id":"u1","status":"inProgress"}}}""");
        for (int n = 2; n <= 5; n++)
            events.AddRange(parser.Parse($$$"""{"method":"error","params":{"error":{"message":"Reconnecting... {{{n}}}/5"},"willRetry":true,"threadId":"t","turnId":"u1"}}""").Events);
        var fatal = parser.Parse("""{"method":"error","params":{"error":{"message":"You've hit your usage limit."},"willRetry":false,"threadId":"t","turnId":"u1"}}""");
        events.AddRange(fatal.Events);
        var completed = parser.Parse("""{"method":"turn/completed","params":{"threadId":"t","turn":{"id":"u1","status":"failed","error":{"message":"You've hit your usage limit."}}}}""");
        events.AddRange(completed.Events);

        events.OfType<AgentNoticeEvent>().Should().ContainSingle("同一輪的重試只留一列提示")
            .Which.Message.Should().Be("Codex 連線不穩，正在重試：Reconnecting... 2/5");
        fatal.Events.Should().BeEmpty("最後的錯誤由這一輪的失敗回報，不另加一列");
        completed.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Failed);
        completed.TurnCompleted.ErrorMessage.Should().Be("You've hit your usage limit.");

        // 下一輪重新計算
        parser.Parse("""{"method":"turn/started","params":{"threadId":"t","turn":{"id":"u2","status":"inProgress"}}}""");
        parser.Parse("""{"method":"error","params":{"error":{"message":"Reconnecting... 1/5"},"willRetry":true,"threadId":"t","turnId":"u2"}}""")
            .Events.OfType<AgentNoticeEvent>().Should().ContainSingle();
    }

    [Fact]
    public void 錯誤通知_這一輪沒帶錯誤或沒有失敗時_最後的錯誤照樣顯示()
    {
        var parser = new CodexEventParser();
        parser.Parse("""{"method":"error","params":{"error":{"message":"model overloaded"},"willRetry":false}}""").Events.Should().BeEmpty();
        parser.Parse("""{"method":"turn/completed","params":{"turn":{"id":"u","status":"failed","error":null}}}""").TurnCompleted!.ErrorMessage
            .Should().Be("model overloaded", "turn/completed 沒帶錯誤時用先前 error 通知的訊息");

        parser.Parse("""{"method":"error","params":{"error":{"message":"tool crashed"},"willRetry":false}}""");
        var interrupted = parser.Parse("""{"method":"turn/completed","params":{"turn":{"id":"u","status":"interrupted","error":null}}}""");
        interrupted.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Interrupted);
        interrupted.Events.OfType<AgentNoticeEvent>().Should().ContainSingle(n => n.IsError && n.Message == "tool crashed", "這一輪不是以失敗結束時，錯誤不能就此消失");

        parser.Parse("""{"method":"turn/completed","params":{"turn":{"id":"u","status":"completed"}}}""").Events.Should().BeEmpty("錯誤只顯示一次");
    }

    [Fact]
    public void 持續目標_還在進行時正常結束的一輪標成會自己接著做_標記完成或停止或清除後不算()
    {
        // 0.164.0 實測（tools/probes/codex_goal_probe.py）：目標進行中每輪結束約 0.03 秒後 codex 自己開下一輪；
        // 模型標記完成的那一輪，status complete 的 thread/goal/updated 比 turn/completed 先到
        const string Main = "main-thread";
        var parser = new CodexEventParser();
        string Goal(string status, string thread = Main)
            => $$$$"""{"method":"thread/goal/updated","params":{"threadId":"{{{{thread}}}}","turnId":null,"goal":{"threadId":"{{{{thread}}}}","objective":"移植全部功能","status":"{{{{status}}}}","tokenBudget":null,"tokensUsed":0,"timeUsedSeconds":0,"createdAt":1,"updatedAt":1}}}""";
        AgentTurnCompletedEvent Turn(string id, string status)
        {
            parser.Parse($$$$"""{"method":"turn/started","params":{"threadId":"main-thread","turn":{"id":"{{{{id}}}}","status":"inProgress"}}}""", Main);
            return parser.Parse($$$$"""{"method":"turn/completed","params":{"threadId":"main-thread","turn":{"id":"{{{{id}}}}","status":"{{{{status}}}}","error":null}}}""", Main).TurnCompleted!;
        }

        Turn("u0", "completed").ContinuesOnItsOwn.Should().BeFalse("沒有目標");
        parser.Parse(Goal("active"), Main).Events.Should().ContainSingle().Which.Should().BeOfType<AgentCodexGoalEvent>();
        parser.GoalActive.Should().BeTrue();
        Turn("u1", "completed").ContinuesOnItsOwn.Should().BeTrue("目標還沒完成：codex 接著自己開下一輪，中樞不念完成");
        Turn("u2", "interrupted").ContinuesOnItsOwn.Should().BeFalse("按停止的一輪：目標仍是 active，但 codex 不會自己再開");

        parser.Parse(Goal("complete"), Main);
        Turn("u3", "completed").ContinuesOnItsOwn.Should().BeFalse("模型標記完成的那一輪才是真的完成");

        parser.Parse(Goal("active"), Main);
        parser.Parse(Goal("budgetLimited"), Main);
        parser.GoalActive.Should().BeFalse("用完預算、暫停、卡住都不會再自己開");
        parser.Parse(Goal("active"), Main);
        parser.Parse("""{"method":"thread/goal/cleared","params":{"threadId":"main-thread"}}""", Main);
        parser.GoalActive.Should().BeFalse();

        parser.Parse(Goal("active", thread: "child-thread"), Main);
        parser.GoalActive.Should().BeFalse("子代理的目標不算主對話的");
    }

    [Fact]
    public void 子代理的事件_不影響主對話這一輪()
    {
        // multi_agent：app-server 會替連線自動訂閱子代理的 thread，子代理的 turn／item／用量通知都帶自己的 threadId
        var parser = new CodexEventParser();
        const string Main = "main-thread";
        var started = parser.Parse("""{"method":"turn/started","params":{"threadId":"main-thread","turn":{"id":"main-turn","status":"inProgress"}}}""", Main);
        started.StartedTurnId.Should().Be("main-turn");

        parser.Parse("""{"method":"turn/started","params":{"threadId":"child-thread","turn":{"id":"child-turn","status":"inProgress"}}}""", Main)
            .StartedTurnId.Should().BeNull("子代理的 turn 不能蓋掉主對話的 turn id（停止時要送主對話的）");
        parser.Parse("""{"method":"item/agentMessage/delta","params":{"threadId":"child-thread","turnId":"child-turn","itemId":"child-msg","delta":"child says hi"}}""", Main)
            .Events.Should().ContainSingle().Which.Should().BeOfType<AgentToolOutputEvent>("子代理內容只進專用卡片");
        parser.Parse("""{"method":"item/started","params":{"item":{"type":"fileChange","id":"child-fc","changes":[{"path":"C:\\w\\c.txt","kind":{"type":"add"},"diff":"x"}]},"threadId":"child-thread","turnId":"child-turn"}}""", Main)
            .Events.Should().ContainSingle().Which.Should().BeOfType<AgentToolOutputEvent>();
        parser.GetFileChange("child-fc").Path.Should().Be(@"C:\w\c.txt", "子代理的改檔核准卡仍要顯示差異");
        parser.Parse("""{"method":"error","params":{"error":{"message":"child failed"},"willRetry":false,"threadId":"child-thread","turnId":"child-turn"}}""", Main)
            .Events.Should().ContainSingle().Which.Should().BeOfType<AgentToolOutputEvent>();
        parser.Parse("""{"method":"thread/tokenUsage/updated","params":{"threadId":"child-thread","turnId":"child-turn","tokenUsage":{"last":{"inputTokens":999,"outputTokens":999,"cachedInputTokens":0}}}}""", Main);
        parser.Parse("""{"method":"turn/completed","params":{"threadId":"child-thread","turn":{"id":"child-turn","status":"completed","error":null}}}""", Main)
            .TurnCompleted.Should().BeNull("子代理先做完不代表主對話這一輪結束");
        parser.Parse("""{"method":"serverRequest/resolved","params":{"threadId":"child-thread","requestId":7}}""", Main)
            .Events.OfType<AgentRequestResolvedEvent>().Single().RequestKey.Should().Be("7", "子代理的核准卡被撤銷時一樣要收掉");
        parser.Parse("""{"method":"account/rateLimits/updated","params":{"rateLimits":{"primary":{"usedPercent":3}}}}""", Main)
            .Events.OfType<AgentRateLimitEvent>().Should().ContainSingle("帳戶額度不分 thread");

        parser.Parse("""{"method":"thread/tokenUsage/updated","params":{"threadId":"main-thread","turnId":"main-turn","tokenUsage":{"last":{"inputTokens":10,"outputTokens":7,"cachedInputTokens":0}}}}""", Main);
        var completed = parser.Parse("""{"method":"turn/completed","params":{"threadId":"main-thread","turn":{"id":"main-turn","status":"completed","error":null}}}""", Main).TurnCompleted;
        completed.Should().NotBeNull();
        completed!.Outcome.Should().Be(AgentTurnOutcome.Completed);
        completed.Usage!.OutputTokens.Should().Be(7, "用量只算主對話這一輪");
    }

    [Fact]
    public void 推理摘要的新段落_插入空行分隔()
    {
        var parser = new CodexEventParser();
        var events = new List<AgentEvent>();
        foreach (var line in new[]
        {
            """{"method":"item/reasoning/summaryPartAdded","params":{"threadId":"t","turnId":"u","itemId":"rs_1","summaryIndex":0}}""",
            """{"method":"item/reasoning/summaryTextDelta","params":{"threadId":"t","turnId":"u","itemId":"rs_1","delta":"**讀取設定**\n先看設定檔。","summaryIndex":0}}""",
            """{"method":"item/reasoning/summaryPartAdded","params":{"threadId":"t","turnId":"u","itemId":"rs_1","summaryIndex":1}}""",
            """{"method":"item/reasoning/summaryTextDelta","params":{"threadId":"t","turnId":"u","itemId":"rs_1","delta":"**規劃修改**","summaryIndex":1}}""",
        })
        {
            events.AddRange(parser.Parse(line).Events);
        }

        string.Concat(events.OfType<AgentThinkingDeltaEvent>().Where(e => e.BlockId == "rs_1").Select(e => e.Delta))
            .Should().Be("**讀取設定**\n先看設定檔。\n\n**規劃修改**", "第一段之前不加，之後每段前空一行，Markdown 才會分段");
    }

    [Fact]
    public void 錯誤回應_取出訊息_JSON的Null不算錯誤()
    {
        var parser = new CodexEventParser();
        var r = parser.Parse("""{"id":5,"error":{"code":-32600,"message":"thread not found"}}""");
        r.Response!.Value.Error.Should().Be("thread not found");
        r.Response.Value.Result.Should().BeNull();

        var ok = parser.Parse("""{"id":6,"result":{"thread":{"id":"t-9"}},"error":null}""");
        ok.Response!.Value.Error.Should().BeNull();
        ok.Response.Value.Result!["thread"]!["id"]!.ToString().Should().Be("t-9");

        parser.Parse("""{"id":7,"error":"純文字錯誤"}""").Response!.Value.Error.Should().Be("純文字錯誤");
    }

    [Fact]
    public void 沙箱設定完成通知()
    {
        var parser = new CodexEventParser();
        parser.Parse("""{"method":"windowsSandbox/setupCompleted","params":{"success":false,"error":"需要系統管理員"}}""").SandboxSetupCompleted
            .Should().Be((false, "需要系統管理員"));
    }

    [Fact]
    public void 非JSON與空行_直接略過()
    {
        var parser = new CodexEventParser();
        parser.Parse("WARNING: 某個警告").Events.Should().BeEmpty();
        parser.Parse("").Response.Should().BeNull();
    }
}
