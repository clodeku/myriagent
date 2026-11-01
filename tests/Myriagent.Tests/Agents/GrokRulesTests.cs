using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// Grok Build（grok 1.0.52，ACP）的純函式規則與 session/update 解讀：JSON-RPC 訊息、session 請求、權限模式、核准與代為核准、提問與計畫確認、
/// 模型、用量、工具卡（樣本取自 2026-10-16 實測，路徑改短）、帳戶、找檔與自我更新
/// </summary>
public class GrokRulesTests
{
    private static AgentAccount Grok(string? configDirectory = null) => new() { Id = configDirectory == null ? "local-grok" : "g1", Provider = AgentProvider.Grok, Label = "Grok", ConfigDirectory = configDirectory };

    // ───── JSON-RPC ─────

    [Fact]
    public void Parse_分辨回應請求與通知_擴充方法去掉底線_日期字串原樣保留()
    {
        var response = GrokAcpRules.Parse("""{"jsonrpc":"2.0","id":3,"result":{"stopReason":"end_turn","at":"2026-10-14T19:00:50+00:00"}}""")!;
        response.Kind.Should().Be(GrokAcpMessageKind.Response);
        response.Id!.Value<long>().Should().Be(3);
        response.Result!["at"]!.Type.Should().Be(JTokenType.String, "不把像日期的字串轉成 DateTime");
        ((string?)response.Result!["at"]).Should().Be("2026-10-14T19:00:50+00:00");

        var request = GrokAcpRules.Parse("""{"jsonrpc":"2.0","id":0,"method":"_x.ai/ask_user_question","params":{"sessionId":"s"}}""")!;
        request.Kind.Should().Be(GrokAcpMessageKind.Request);
        request.Method.Should().Be("x.ai/ask_user_question");
        GrokAcpRules.RequestKey(request.Id!).Should().Be("0");

        var note = GrokAcpRules.Parse("""{"jsonrpc":"2.0","method":"_x.ai/models/update","params":{"currentModelId":"grok-4.6"}}""")!;
        note.Kind.Should().Be(GrokAcpMessageKind.Notification);
        note.Method.Should().Be("x.ai/models/update");

        GrokAcpRules.Parse("Grok Build starting…").Should().BeNull();
        GrokAcpRules.Parse("{not json").Should().BeNull();
        GrokAcpRules.Parse("""{"jsonrpc":"2.0"}""").Should().BeNull("沒有 id 也沒有 method");
    }

    [Fact]
    public void ParseError_帶上data的代碼與說明_找不到對話與沒登入的判斷()
    {
        var notFound = GrokAcpRules.Parse("""{"jsonrpc":"2.0","id":5,"error":{"code":-32603,"message":"Path not found","data":{"code":"FS_NOT_FOUND","detail":"sessions\\x"}}}""")!.Error!;
        notFound.Should().Be(new GrokAcpError(-32603, "Path not found", "FS_NOT_FOUND", "Path not found（sessions\\x）"));
        GrokAcpRules.IsSessionNotFound(notFound).Should().BeTrue();
        GrokAcpRules.IsSessionNotFound(new GrokAcpError(-32602, "x", null, "unknown session id abc")).Should().BeTrue();
        GrokAcpRules.IsSessionNotFound(new GrokAcpError(-32603, "Internal error", null, "Internal error")).Should().BeFalse();

        var auth = GrokAcpRules.ParseError(JObject.Parse("""{"code":-32000,"message":"Authentication required"}"""));
        GrokAcpRules.IsAuthRequired(auth).Should().BeTrue();
        GrokAcpRules.IsAuthRequired(notFound).Should().BeFalse();
        GrokAcpRules.ParseError(JObject.Parse("""{"code":1,"message":"m","data":"m"}""")).Text.Should().Be("m", "data 和 message 一樣時不重複");
    }

    [Fact]
    public void Build_請求通知回應與錯誤_一行JSON()
    {
        GrokAcpRules.BuildRequest(7, "session/prompt", new JObject { ["sessionId"] = "s" })
            .Should().Be("""{"jsonrpc":"2.0","id":7,"method":"session/prompt","params":{"sessionId":"s"}}""");
        GrokAcpRules.BuildNotification("session/cancel", null).Should().Be("""{"jsonrpc":"2.0","method":"session/cancel","params":{}}""");
        GrokAcpRules.BuildResult(new JValue(0), new JObject { ["outcome"] = "cancelled" }).Should().Be("""{"jsonrpc":"2.0","id":0,"result":{"outcome":"cancelled"}}""");
        GrokAcpRules.BuildError(new JValue("a"), -32601, "不支援").Should().Be("""{"jsonrpc":"2.0","id":"a","error":{"code":-32601,"message":"不支援"}}""");
    }

    // ───── session 請求與權限模式 ─────

    [Fact]
    public void 建立與續接對話_預先產生的id_權限帶在meta_預設模型不送()
    {
        var created = GrokAcpRules.BuildNewSessionParams("s1", @"C:\w", "grok-4.6", AgentPermissionMode.FullAccess);
        created.Should().BeEquivalentTo(JObject.Parse("""
            {"cwd":"C:\\w","mcpServers":[],"_meta":{"askUserQuestion":true,"yoloMode":true,"autoMode":false,"sessionId":"s1","modelId":"grok-4.6"}}
            """));
        GrokAcpRules.BuildNewSessionParams("s1", @"C:\w", "default", AgentPermissionMode.Auto)["_meta"]!["modelId"].Should().BeNull("預設＝不指定");
        GrokAcpRules.BuildNewSessionParams("s1", @"C:\w", null, AgentPermissionMode.Auto)["_meta"]!.Value<bool>("autoMode").Should().BeTrue();

        GrokAcpRules.BuildResumeParams("s1", @"C:\w", AgentPermissionMode.Ask).Should().BeEquivalentTo(JObject.Parse("""
            {"sessionId":"s1","cwd":"C:\\w","mcpServers":[],"_meta":{"askUserQuestion":true,"yoloMode":false,"autoMode":false}}
            """));
        GrokAcpRules.BuildSetConfigParams("s1", "reasoning_effort", "low").Should().BeEquivalentTo(JObject.Parse("""{"sessionId":"s1","configId":"reasoning_effort","value":"low"}"""),
            "值直接是字串（手冊的 {value:{value}} 會回 -32602）");
        GrokAcpRules.BuildPromptParams("s1", "你好")["prompt"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""[{"type":"text","text":"你好"}]""");
        GrokAcpRules.BuildPromptParams("s1", "你好", [("image/png", "QQ==")])["prompt"]!.ToString(Newtonsoft.Json.Formatting.None)
            .Should().Be("""[{"type":"text","text":"你好"},{"type":"image","mimeType":"image/png","data":"QQ=="}]""");
    }

    [Fact]
    public void 影片工具_成片路徑_零資料保留_壓縮與視窗指令_背景通知()
    {
        GrokAcpRules.DescribeTool(JObject.Parse("""{"title":"image_to_video","_meta":{"x.ai/tool":{"name":"image_to_video"}}}"""))
            .Should().Match<GrokAcpRules.ToolDescription>(d => d.Name == "圖片轉影片" && d.Kind == "image");
        GrokAcpRules.DescribeTool(JObject.Parse("""{"title":"reference_to_video","_meta":{"x.ai/tool":{"name":"reference_to_video"}}}""")).Name.Should().Be("參考生成影片");
        GrokAcpRules.DescribeTool(JObject.Parse("""{"title":"video_gen","_meta":{"x.ai/tool":{"name":"video_gen"}}}""")).Name.Should().Be("產生影片");
        GrokAcpRules.DescribeTool(JObject.Parse("""{"title":"get_command_or_subagent_output","_meta":{"x.ai/tool":{"name":"get_command_or_subagent_output"}}}""")).Kind.Should().Be("bash");

        var update = JObject.Parse("""{"rawOutput":{"path":"C:\\clips\\from-raw.png"},"rawInput":{"file_path":"C:\\secret\\in.png"},"content":[{"type":"content","content":{"type":"text","text":"也有 D:\\more\\b.webp"}}]}""");
        GrokAcpRules.ExtractDisplayMedia("""saved C:\clips\out.mp4 and \\server\share\a.mp4 and videos/1.mp4.""", update)
            .Should().Equal(@"C:\clips\out.mp4", @"D:\more\b.webp", @"C:\clips\from-raw.png");
        GrokAcpRules.ExtractDisplayMedia(null, null).Should().BeEmpty();

        GrokAcpRules.DescribeZeroRetention("Video generation tools are unavailable under zero data retention (ZDR). To enable, turn off /privacy.")
            .Should().NotBeNull().And.Contain("零資料保留").And.Contain("%USERPROFILE%\\.grok\\managed_config.toml");
        GrokAcpRules.DescribeZeroRetention("成片在 C:\\clips\\out.mp4").Should().BeNull();

        GrokAcpRules.IsCompactCommand("  /compact  ").Should().BeTrue();
        GrokAcpRules.IsCompactCommand("/Compact").Should().BeFalse();
        GrokAcpRules.IsCompactCommand("/compact now").Should().BeFalse();
        GrokAcpRules.BuildCompactParams("sid").Should().BeEquivalentTo(JObject.Parse("""{"sessionId":"sid"}"""));

        GrokAcpRules.TryParseContextWindowCommand("/context-window", out var none).Should().BeTrue();
        none.Should().BeNull();
        GrokAcpRules.TryParseContextWindowCommand("/context-window 500000", out var asked).Should().BeTrue();
        asked.Should().Be(500_000);
        GrokAcpRules.TryParseContextWindowCommand("/context-window 500000 extra", out _).Should().BeFalse();
        GrokAcpRules.TryParseContextWindowCommand("/context-window0", out _).Should().BeFalse();
        GrokAcpRules.DescribeContextWindow(500_000, "grok-4.7", 256_000, [256_000, 500_000])
            .Should().Contain("不能切換").And.Contain("256,000").And.Contain("500,000").And.Contain("grok-4.7");

        GrokAcpRules.ParsePromptImages(JObject.Parse("""{"agentCapabilities":{"promptCapabilities":{"image":true}}}""")).Should().BeTrue();
        GrokAcpRules.ParsePromptImages(JObject.Parse("""{"agentCapabilities":{"loadSession":true}}""")).Should().BeFalse();
        GrokAcpRules.ParsePromptImages(null).Should().BeFalse();

        GrokAcpRules.TryParseBackground(JObject.Parse("""{"sessionUpdate":"task_backgrounded","task_id":"bg1","tool_call_id":"call-bg","monitor_description":"sleep 30"}"""), null, out var snake).Should().BeTrue();
        snake.Kind.Should().Be("task_backgrounded");
        snake.TaskId.Should().Be("bg1");
        snake.ToolCallId.Should().Be("call-bg");
        snake.Description.Should().Be("sleep 30");
        snake.WillWake.Should().BeNull();
        snake.OutputFile.Should().BeNull();
        GrokAcpRules.TryParseBackground(JObject.Parse("""{"taskId":"bg2","toolCallId":"c2","monitorDescription":"看日誌","willWake":false}"""), "x.ai/task_completed", out var camel).Should().BeTrue();
        (camel.Kind, camel.TaskId, camel.ToolCallId, camel.Description, camel.WillWake).Should().Be(("task_completed", "bg2", "c2", "看日誌", false));
        GrokAcpRules.TryParseBackground(JObject.Parse("""{"sessionUpdate":"task_completed","will_wake":true,"task_snapshot":{"task_id":"bg3","description":"寫標記","exit_code":0}}"""), "x.ai/task_completed", out var snap).Should().BeTrue();
        (snap.Kind, snap.TaskId, snap.ToolCallId, snap.Description, snap.WillWake).Should().Be(("task_completed", "bg3", null, "寫標記", true));
        GrokAcpRules.TryParseBackground(JObject.Parse("""{"sessionUpdate":"task_backgrounded"}"""), null, out _).Should().BeFalse();
        GrokAcpRules.TryParseBackground(JObject.Parse("""{"sessionUpdate":"task_backgrounded","task_id":"bg4","output_file":"C:\\logs\\a.log","task_snapshot":{"output_file":"C:\\other.log"}}"""), null, out var fileNote).Should().BeTrue();
        fileNote.OutputFile.Should().Be(@"C:\logs\a.log");
        GrokAcpRules.TryParseBackground(JObject.Parse("""{"sessionUpdate":"task_backgrounded","task_id":"bg5","task_snapshot":{"output_file":"videos/1.log"}}"""), null, out var snapFile).Should().BeTrue();
        snapFile.OutputFile.Should().Be("videos/1.log");
        GrokAcpRules.AcceptOutputFile(@"C:\logs\a.log").Should().Be(@"C:\logs\a.log");
        GrokAcpRules.AcceptOutputFile(@"\\server\share\a.log").Should().BeNull();
        GrokAcpRules.AcceptOutputFile("videos/1.log").Should().BeNull();
    }

    [Theory]
    [InlineData("FullAccess", true, "always-approve", "default")]
    [InlineData("Auto", false, "auto", "default")]
    [InlineData("Ask", false, "ask", "default")]
    [InlineData("AcceptEdits", false, "ask", "default")]
    [InlineData("Plan", false, "ask", "plan")]
    public void 權限模式_yolo通知與session模式(string modeName, bool yolo, string permission, string modeId)
    {
        var mode = Enum.Parse<AgentPermissionMode>(modeName);
        var p = GrokAcpRules.BuildPermissionModeParams(mode);
        p.Value<bool>("yolo_mode").Should().Be(yolo);
        p.Value<string>("permission_mode").Should().Be(permission);
        GrokAcpRules.SessionModeId(mode).Should().Be(modeId);
        AgentPermissionModes.Normalize(AgentProvider.Grok, mode).Should().Be(mode, "五種模式 grok 都有");
    }

    [Fact]
    public void 一輪的結果_stopReason與用量()
    {
        GrokAcpRules.EvaluateStopReason("end_turn").Should().Be((AgentTurnOutcome.Completed, (string?)null));
        GrokAcpRules.EvaluateStopReason("cancelled").Should().Be((AgentTurnOutcome.Interrupted, (string?)null), "拒絕也是 cancelled（PermissionRejected），由中樞依這一輪拒絕過改成正常結束");
        GrokAcpRules.EvaluateStopReason("refusal").Outcome.Should().Be(AgentTurnOutcome.Failed);
        GrokAcpRules.DescribeStopLimit("max_tokens").Should().NotBeNull();
        GrokAcpRules.DescribeStopLimit("end_turn").Should().BeNull();

        // 2026-10-16 實測的 prompt 回應（_meta 頂層是最後一次呼叫，usage 是整輪合計）
        var result = JObject.Parse("""
            {"stopReason":"end_turn","_meta":{"inputTokens":28582,"outputTokens":22,"usage":{"inputTokens":57071,"outputTokens":90,"totalTokens":57161,
             "cachedReadTokens":28544,"cacheCreationTokens":0,"reasoningTokens":31,"modelCalls":2,"costUsdTicks":244344400}}}
            """);
        GrokAcpRules.ParseUsage(result).Should().Be(new AgentUsage(57071, 90, 28544, 0.024434));
        GrokAcpRules.ParseUsage(JObject.Parse("""{"stopReason":"end_turn"}""")).Should().BeNull();
    }

    // ───── 帳戶查詢 ─────

    [Fact]
    public void 登入狀態_方案與登入網址()
    {
        GrokAcpRules.EvaluateAuthInfo(JObject.Parse("""{"email":"a@b.c","methodId":"grok.com"}""")).Should().Be(new GrokAuthEvaluation(AccountStatus.Ready, "a@b.c"));
        GrokAcpRules.EvaluateAuthInfo(JObject.Parse("""{"methodId":"xai_api_key"}""")).Status.Should().Be(AccountStatus.ApiKey);
        GrokAcpRules.EvaluateAuthInfo(new JObject()).Status.Should().Be(AccountStatus.NotLoggedIn);
        GrokAcpRules.EvaluateAuthInfo(null).Status.Should().Be(AccountStatus.NotLoggedIn);

        GrokAcpRules.ParsePlan(JObject.Parse("""{"subscription_tier":" SuperGrok ","config":{}}""")).Should().Be("SuperGrok");
        GrokAcpRules.ParsePlan(new JObject()).Should().BeNull();

        GrokAcpRules.ParseAuthUrl(JObject.Parse("""{"auth_url":"https://accounts.x.ai/device?code=ABCD","mode":"device"}"""))!.Host.Should().Be("accounts.x.ai");
        GrokAcpRules.ParseAuthUrl(JObject.Parse("""{"auth_url":"http://evil.example/"}""")).Should().BeNull("只開 https");
        GrokAcpRules.ParseAuthUrl(JObject.Parse("""{"auth_url":"file:///C:/x"}""")).Should().BeNull();
    }

    [Fact]
    public void Billing_每週額度窗_0時省略的使用率當0_用完且沒有隨用隨付才鎖定()
    {
        // 2026-10-16 實測：creditUsagePercent 為 0 時省略
        var fresh = UsageRules.ParseGrokBilling(JObject.Parse("""
            {"subscription_tier":"SuperGrok","config":{"currentPeriod":{"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"2026-10-14T19:00:50.623949+00:00","end":"2026-10-21T19:00:50.623949+00:00"},
             "onDemandCap":{"val":0},"onDemandUsed":{"val":0},"prepaidBalance":{"val":0},"isUnifiedBillingUser":true,"billingPeriodEnd":"2026-10-21T19:00:50.623949+00:00"}}
            """))!.Value;
        fresh.Note.Should().BeNull();
        fresh.Windows.Should().Equal(new UsageWindow(UsageRules.GrokCreditsKey, "每週", 0, DateTimeOffset.Parse("2026-10-21T19:00:50.623949+00:00")));

        var full = UsageRules.ParseGrokBilling(JObject.Parse("""{"config":{"creditUsagePercent":100,"currentPeriod":{"type":"USAGE_PERIOD_TYPE_MONTHLY","end":"2026-11-01T00:00:00Z"}}}"""))!.Value;
        full.Windows.Single().Should().Match<UsageWindow>(w => w.Label == "每月" && w.Percent == 100 && w.Locked);

        var onDemand = UsageRules.ParseGrokBilling(JObject.Parse("""
            {"config":{"creditUsagePercent":100,"onDemandCap":{"val":2000},"onDemandUsed":{"val":350},"prepaidBalance":{"val":1234}}}
            """))!.Value;
        onDemand.Windows.Single().Locked.Should().BeFalse("還有隨用隨付或預購點數");
        onDemand.Note.Should().Be("隨用隨付已用 $3.50／上限 $20.00；預購點數餘額 $12.34");
        var capReached = UsageRules.ParseGrokBilling(JObject.Parse("""
            {"config":{"creditUsagePercent":100,"onDemandCap":{"val":1000},"onDemandUsed":{"val":1000},"prepaidBalance":{"val":0}}}
            """))!.Value;
        capReached.Windows.Single().Locked.Should().BeTrue("隨用隨付已用到上限、也沒有預購點數");
        var capLeft = UsageRules.ParseGrokBilling(JObject.Parse("""
            {"config":{"creditUsagePercent":100,"onDemandCap":{"val":1000},"onDemandUsed":{"val":999}}}
            """))!.Value;
        capLeft.Windows.Single().Locked.Should().BeFalse("隨用隨付還有額度");

        UsageRules.ParseGrokBilling(JObject.Parse("""{"subscription_tier":"SuperGrok"}""")).Should().BeNull("沒有 config");
        UsageRules.ParseGrokBilling(null).Should().BeNull();
    }

    // ───── 模型、指令與輸入 ─────

    [Fact]
    public void 模型清單_前面加預設_思考強度由低到高_判斷能不能送()
    {
        var state = JObject.Parse("""
            {"currentModelId":"grok-4.7","availableModels":[
              {"modelId":"grok-4.7","name":"Grok 4.7","description":"latest","_meta":{"reasoningEfforts":[{"id":"xhigh","value":"xhigh"},{"id":"high","value":"high"},{"id":"medium","value":"medium"},{"id":"low","value":"low"}]}},
              {"modelId":"grok-4.6","name":"Grok 4.6","_meta":{}},
              {"name":"沒有 id"}]}
            """);
        var models = GrokAcpRules.ParseModels(state);

        models.Select(m => m.Value).Should().Equal("default", "grok-4.7", "grok-4.6");
        models[0].Should().Match<AgentModelOption>(m => m.Label == "預設" && m.Description!.Contains("Grok 4.7"));
        models[0].Efforts.Should().Equal("low", "medium", "high", "xhigh");
        models[1].Efforts.Should().Equal("low", "medium", "high", "xhigh");
        models[2].Efforts.Should().BeEmpty();
        GrokAcpRules.ParseInitializeModels(new JObject { ["_meta"] = new JObject { ["modelState"] = state } }).Should().HaveCount(3);
        GrokAcpRules.ParseModels(JObject.Parse("""{"availableModels":[]}""")).Should().BeEmpty();

        GrokAcpRules.SupportsEffort(models, "default", "xhigh").Should().BeTrue();
        GrokAcpRules.SupportsEffort(models, null, "minimal").Should().BeFalse();
        GrokAcpRules.SupportsEffort(models, "grok-4.6", "high").Should().BeTrue("沒有清單就照送");
        GrokAcpRules.SupportsEffort(models, "grok-9", "high").Should().BeTrue("不認得的模型照送");
        GrokAcpRules.SupportsEffort(models, "grok-4.7", null).Should().BeFalse();
        GrokAcpRules.NormalizeModel(" grok-4.6 ").Should().Be("grok-4.6");
        GrokAcpRules.NormalizeModel("Default").Should().BeNull();
    }

    [Fact]
    public void 斜線指令_不列always_approve()
    {
        var commands = GrokAcpRules.ParseCommands(JArray.Parse("""
            [{"name":"compact","description":"Compact history"},{"name":"always-approve","description":"Toggle"},{"name":"review","description":"Review","input":{"hint":"<files>"}},{"description":"no name"}]
            """));
        commands.Should().Equal(new AgentSlashCommand("compact", "Compact history", null), new AgentSlashCommand("review", "Review", "<files>"));
    }

    [Fact]
    public void 斜線指令_沒有參數的指令是input_null_照常列出()
    {
        // 實測 grok 1.0.70：沒有參數的指令送 "input":null。以前對 JSON 的 null 取 hint 丟例外、整則 available_commands_update 被丟掉
        // （2026-10-23～24 的記錄有 474 次「Grok 訊息處理失敗（session/update）：Cannot access child value on Newtonsoft.Json.Linq.JValue.」）
        var commands = GrokAcpRules.ParseCommands(JArray.Parse("""
            [{"name":"compact","description":"Compress conversation history","input":null},{"name":"feedback","description":"Send feedback","input":{"hint":"feedback text"}},{"name":"context","description":"Show usage","input":{"hint":null}}]
            """));
        commands.Should().Equal(
            new AgentSlashCommand("compact", "Compress conversation history", null),
            new AgentSlashCommand("feedback", "Send feedback", "feedback text"),
            new AgentSlashCommand("context", "Show usage", null));
    }

    [Fact]
    public void 一輪的文字_背景說明前文節錄換資料夾通知與附件()
    {
        GrokAcpRules.BuildTurnText("說明", "（節錄）", "（通知）", "你好", Array.Empty<AgentAttachment>())
            .Should().Be("<myriagent_instructions>\n說明\n</myriagent_instructions>\n\n（節錄）\n\n（通知）\n\n你好");
        GrokAcpRules.BuildTurnText(null, null, null, "你好", Array.Empty<AgentAttachment>()).Should().Be("你好");
        GrokAcpRules.BuildWorkingDirectoryNotice(@"D:\proj").Should().Contain(@"D:\proj");
        GrokAcpRules.SameDirectory(@"C:\a\", @"c:\A").Should().BeTrue();
        GrokAcpRules.SameDirectory(@"C:\a", null).Should().BeFalse();
        GrokAcpRules.Signature("acc", "grok.exe", @"C:\w", @"C:\c").Should().Be(@"acc|grok.exe|C:\w|C:\c|True");
        GrokAcpRules.Signature("acc", "grok.exe", @"C:\w", @"C:\c", useLocalCustomizations: false).Should().Be(@"acc|grok.exe|C:\w|C:\c|False");
        AgentHubRules.SendsDenyFeedbackAsMessage(AgentProvider.Grok).Should().BeTrue("拒絕的回覆沒有欄位能帶說明");
        AgentHubRules.SendsDenyFeedbackAsMessage(AgentProvider.Claude).Should().BeFalse();
    }

    // ───── 核准 ─────

    /// <summary>2026-10-16 實測的指令核准請求（路徑改短）</summary>
    private static readonly JObject ExecRequest = JObject.Parse("""
        {"sessionId":"s","toolCall":{"toolCallId":"call-1","kind":"execute","title":"Execute `Write-Output hi`",
          "rawInput":{"variant":"Bash","command":"Write-Output hi","description":"Print hi","is_background":false},
          "_meta":{"x.ai/tool":{"version":1,"name":"run_terminal_command","kind":"execute","namespace":"grok_build","label":"Run Command","read_only":false}}},
         "options":[{"optionId":"always-allow","name":"Yes, and don't ask again for bash commands","kind":"allow_always"},{"optionId":"allow-once","name":"Yes, proceed","kind":"allow_once"},
                    {"optionId":"reject-once","name":"No, and tell Grok what to do differently","kind":"reject_once"},{"optionId":"reject-always","name":"No, and don't ask again for this command","kind":"reject_always"}]}
        """);

    private static JObject EditRequest(string path)
    {
        var request = JObject.Parse("""
            {"sessionId":"s","toolCall":{"toolCallId":"call-2","kind":"edit","title":"Write","rawInput":{"variant":"Write","content":"x\n"},
              "_meta":{"x.ai/tool":{"name":"write","kind":"write","input":{}}}},
             "options":[{"optionId":"allow-edits-session","kind":"allow_always"},{"optionId":"allow-once","kind":"allow_once"},{"optionId":"reject-once","kind":"reject_once"}]}
            """);
        request["toolCall"]!["rawInput"]!["file_path"] = path;
        request["toolCall"]!["_meta"]!["x.ai/tool"]!["input"]!["path"] = path;
        return request;
    }

    [Fact]
    public void 核准請求_選項依kind對應_回覆的選擇()
    {
        var exec = GrokAcpRules.ParsePermissionRequest(ExecRequest);
        exec.Should().BeEquivalentTo(new GrokPermissionRequest("call-1", "execute", "always-allow", "allow-once", "reject-once", Array.Empty<string>()));
        GrokAcpRules.BuildPermissionOutcome(exec, AgentApprovalChoice.AllowForSession).ToString(Newtonsoft.Json.Formatting.None)
            .Should().Be("""{"outcome":{"outcome":"selected","optionId":"always-allow"}}""");
        GrokAcpRules.BuildPermissionOutcome(exec, AgentApprovalChoice.Allow)["outcome"]!.Value<string>("optionId").Should().Be("allow-once");
        GrokAcpRules.BuildPermissionOutcome(exec, AgentApprovalChoice.Deny)["outcome"]!.Value<string>("optionId").Should().Be("reject-once", "不用 reject-always：只拒絕這一次");
        GrokAcpRules.BuildPermissionOutcome(exec with { RejectOnceOptionId = null }, AgentApprovalChoice.Deny).ToString(Newtonsoft.Json.Formatting.None)
            .Should().Be("""{"outcome":{"outcome":"cancelled"}}""");
        GrokAcpRules.BuildPermissionOutcome(exec with { AllowSessionOptionId = null }, AgentApprovalChoice.AllowForSession)["outcome"]!.Value<string>("optionId").Should().Be("allow-once");
        GrokAcpRules.AllowSessionLabel("execute").Should().Be("本對話的指令都不再詢問");
        GrokAcpRules.AllowSessionLabel("edit").Should().Be("本對話的編輯都允許");
        GrokAcpRules.AllowSessionLabel("fetch").Should().BeNull();
        GrokAcpRules.RemembersSessionAllow("edit").Should().BeTrue();
        GrokAcpRules.RemembersSessionAllow("execute").Should().BeTrue();
        GrokAcpRules.RemembersSessionAllow("other").Should().BeFalse("MCP 工具、網頁擷取都是 other：不由 app 擴大成整個 kind 都放行");
        GrokAcpRules.RemembersSessionAllow(null).Should().BeFalse();
    }

    [Fact]
    public void 自動接受編輯_只在工作資料夾或對話資料夾內_擋掉跳出與受保護的資料夾()
    {
        var work = Path.Combine(Path.GetTempPath(), "grok-rules-work");
        var conv = Path.Combine(Path.GetTempPath(), "grok-rules-conv");
        bool Can(string path) => GrokAcpRules.CanAutoApproveEdit(GrokAcpRules.ParsePermissionRequest(EditRequest(path)), work, conv, _ => null);

        GrokAcpRules.ParsePermissionRequest(EditRequest(Path.Combine(work, "a.txt"))).Paths.Should().Equal(new[] { Path.Combine(work, "a.txt") }, "rawInput 與 x.ai/tool 的路徑相同只算一次");
        Can(Path.Combine(work, "src", "a.txt")).Should().BeTrue();
        Can(Path.Combine(conv, "notes.md")).Should().BeTrue();
        Can(Path.Combine(work, "..", "other", "a.txt")).Should().BeFalse("..\\ 跳出工作資料夾");
        Can(work + "-evil" + Path.DirectorySeparatorChar + "a.txt").Should().BeFalse("前綴相同的別的資料夾");
        Can(Path.Combine(work, ".git", "config")).Should().BeFalse();
        Can(Path.Combine(work, ".grok", "config.toml")).Should().BeFalse();
        Can(Path.Combine(work, ".claude", "settings.json")).Should().BeFalse();
        Can(@"\\server\share\a.txt").Should().BeFalse("UNC 一律不認");
        Can("relative.txt").Should().BeFalse();
        // NTFS 串流語法：.git::$INDEX_ALLOCATION\hooks\pre-commit 實際寫進 .git\hooks（審查以 .NET 10 實測）
        Can(work + @"\.git::$INDEX_ALLOCATION\hooks\pre-commit").Should().BeFalse("串流語法繞過受保護資料夾");
        Can(work + @"\.git:$I30:$INDEX_ALLOCATION\hooks\pre-commit").Should().BeFalse();
        Can(work + @"\.claude::$INDEX_ALLOCATION\settings.json").Should().BeFalse();
        Can(work + @"\a.txt:hidden").Should().BeFalse("檔名:串流 寫進看不到的資料串流");
        Can(Path.Combine(work, "sub", ".git", "hooks", "pre-commit")).Should().BeFalse("巢狀的 .git 也不算");
        Can(Path.Combine(work, "sub", ".grok", "x")).Should().BeFalse();
        Can(work + @"\.git.\config").Should().BeFalse("結尾的點會被 Windows 去掉");
        Can(work).Should().BeFalse("資料夾本身不是檔案");

        // 交接點、符號連結：工作資料夾之下已存在的那一段是重新剖析點就不算（寫入會落到別處）；工作資料夾本身是交接點沒關係
        var linked = Path.Combine(work, "docs");
        bool CanWith(string path, Func<string, FileAttributes?> attributes)
            => GrokAcpRules.CanAutoApproveEdit(GrokAcpRules.ParsePermissionRequest(EditRequest(path)), work, conv, attributes);
        FileAttributes? Fake(string p)
            => string.Equals(p, work, StringComparison.OrdinalIgnoreCase) ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : string.Equals(p, linked, StringComparison.OrdinalIgnoreCase) ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : p.StartsWith(work, StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".txt", StringComparison.Ordinal) ? FileAttributes.Directory : null;
        CanWith(Path.Combine(linked, "authorized_keys"), Fake).Should().BeFalse("docs 是指向外面的連結");
        CanWith(Path.Combine(work, "src", "new.txt"), Fake).Should().BeTrue("工作資料夾本身是交接點、src 是一般資料夾、檔案還不存在");
        CanWith(Path.Combine(work, "src", "link.txt"), p => p.EndsWith("link.txt", StringComparison.Ordinal) ? FileAttributes.ReparsePoint : Fake(p))
            .Should().BeFalse("檔案本身是符號連結");

        var exec = GrokAcpRules.ParsePermissionRequest(ExecRequest);
        GrokAcpRules.CanAutoApproveEdit(exec, work, conv).Should().BeFalse("指令不是編輯");
        var noPaths = GrokAcpRules.ParsePermissionRequest(JObject.Parse("""{"toolCall":{"toolCallId":"c","kind":"edit"},"options":[{"optionId":"allow-once","kind":"allow_once"}]}"""));
        GrokAcpRules.CanAutoApproveEdit(noPaths, work, conv).Should().BeFalse("不知道改哪個檔案");
    }

    [Fact]
    public void CollectPaths_locations與差異與rawInput()
    {
        GrokAcpRules.CollectPaths(JObject.Parse("""
            {"locations":[{"path":"C:\\w\\a.txt"}],"content":[{"type":"diff","path":"C:\\w\\b.txt"},{"type":"content"}],"rawInput":{"file_path":"c:\\W\\A.TXT","target_file":"C:\\w\\c.txt"}}
            """)).Should().Equal(@"C:\w\a.txt", @"C:\w\b.txt", @"C:\w\c.txt");
        GrokAcpRules.CollectPaths(null).Should().BeEmpty();
    }

    // ───── 提問與計畫確認 ─────

    [Fact]
    public void 提問_實測格式_multiSelect為null_自填答案寫成Other加notes()
    {
        var questions = GrokAcpRules.ParseQuestions(JObject.Parse("""
            {"sessionId":"s","toolCallId":"c","questions":[{"question":"你偏好哪個顏色？","options":[{"label":"紅色","description":"選擇紅色"},{"label":"藍色","description":"選擇藍色"}],"multiSelect":null},
             {"question":"要哪些？","options":[{"label":"A"},{"label":"B","preview":"pv"}],"multiSelect":true},{"options":[]}],"mode":"default"}
            """));
        questions.Should().HaveCount(2);
        questions[0].Should().Match<AgentQuestion>(q => q.Id == "你偏好哪個顏色？" && !q.MultiSelect && q.AllowOther && q.Options.Count == 2 && q.Options[0].Description == "選擇紅色");
        questions[1].MultiSelect.Should().BeTrue();
        questions[1].Options[1].Preview.Should().Be("pv");

        var answer = GrokAcpRules.BuildQuestionAnswer(questions, new Dictionary<string, IReadOnlyList<string>>
        {
            ["你偏好哪個顏色？"] = new[] { "綠色" },
            ["要哪些？"] = new[] { "A", "C", "D" },
        });
        answer.Should().BeEquivalentTo(JObject.Parse("""
            {"outcome":"accepted","answers":{"你偏好哪個顏色？":["Other"],"要哪些？":["A","Other"]},"annotations":{"你偏好哪個顏色？":{"notes":"綠色"},"要哪些？":{"notes":"C\nD"}}}
            """));
        GrokAcpRules.BuildQuestionAnswer(questions, new Dictionary<string, IReadOnlyList<string>> { ["要哪些？"] = new[] { "B" } })
            .Should().BeEquivalentTo(JObject.Parse("""{"outcome":"accepted","answers":{"要哪些？":["B"]}}"""), "沒有自填就不帶 annotations");
        GrokAcpRules.BuildQuestionCancelled().ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"outcome":"cancelled"}""");
    }

    [Fact]
    public void 計畫確認_同意_繼續規劃附說明_作廢()
    {
        GrokAcpRules.BuildPlanResponse(AgentPlanChoice.AcceptEdits, "x").ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"outcome":"approved"}""");
        GrokAcpRules.BuildPlanResponse(AgentPlanChoice.Manual, null).Value<string>("outcome").Should().Be("approved");
        GrokAcpRules.BuildPlanResponse(AgentPlanChoice.KeepPlanning, " 先查資料 ").Should().BeEquivalentTo(JObject.Parse("""{"outcome":"cancelled","feedback":"先查資料"}"""));
        GrokAcpRules.BuildPlanResponse(AgentPlanChoice.KeepPlanning, null).Should().BeEquivalentTo(JObject.Parse("""{"outcome":"cancelled"}"""));
        GrokAcpRules.BuildPlanAbandoned().Value<string>("outcome").Should().Be("abandoned");
        GrokAcpRules.DescribePlan(JObject.Parse("""{"planContent":"# 計畫"}""")).Should().Be("# 計畫");
        GrokAcpRules.DescribePlan(new JObject()).Should().Contain("上面的回覆");
    }

    // ───── 工具卡 ─────

    [Fact]
    public void DescribeTool_指令編輯搜尋MCP與不認得的工具()
    {
        var exec = GrokAcpRules.DescribeTool((JObject)ExecRequest["toolCall"]!);
        exec.Should().Match<GrokAcpRules.ToolDescription>(d => d.Name == "執行指令" && d.Kind == "bash" && d.Title == "Write-Output hi" && d.Detail == "Print hi\n\nWrite-Output hi" && d.FilePath == null);

        var write = GrokAcpRules.DescribeTool(JObject.Parse("""
            {"kind":"edit","title":"Write `C:\\w\\hello.txt`","content":[{"type":"diff","path":"C:\\w\\hello.txt","oldText":"","newText":"hi-myriagent\n"}],
             "rawInput":{"variant":"Write","file_path":"C:\\w\\hello.txt","content":"hi-myriagent\n"},"_meta":{"x.ai/tool":{"name":"write","kind":"write"}}}
            """));
        write.Should().Match<GrokAcpRules.ToolDescription>(d => d.Kind == "write" && d.Title == @"C:\w\hello.txt" && d.Detail == null && d.FilePath == @"C:\w\hello.txt");
        write.Diff.Should().NotBeNullOrEmpty();

        var grep = GrokAcpRules.DescribeTool(JObject.Parse("""{"title":"grep","rawInput":{"variant":"Grep","pattern":"TODO","path":"src"},"_meta":{"x.ai/tool":{"name":"grep"}}}"""));
        grep.Should().Match<GrokAcpRules.ToolDescription>(d => d.Name == "搜尋內容" && d.Kind == "search" && d.FilePath == null);
        grep.Title.Should().Be($"TODO{(char)0x3000}src", "搜尋卡的標題是「樣式　資料夾」（同 Claude 的 Grep），不能只剩資料夾（2026-10-23）");
        grep.Detail.Should().NotContain("variant", "grok 內部的工具變體名稱不顯示");
        GrokAcpRules.DescribeTool(JObject.Parse("""{"title":"glob","rawInput":{"pattern":"**/*.cs"},"_meta":{"x.ai/tool":{"name":"glob"}}}""")).Title
            .Should().Be("**/*.cs", "沒有資料夾就只有樣式");
        GrokAcpRules.DescribeTool(JObject.Parse("""{"title":"list_dir","rawInput":{"path":"src"},"_meta":{"x.ai/tool":{"name":"list_dir"}}}""")).Title
            .Should().Be("src", "沒有樣式的搜尋類工具照舊是資料夾");
        var big = JObject.Parse("""{"title":"skill","rawInput":{},"_meta":{"x.ai/tool":{"name":"skill"}}}""");
        big["rawInput"]!["body"] = new string('x', 20_000);
        GrokAcpRules.DescribeTool(big).Detail!.Length.Should().BeLessThan(4100, "參數 JSON 照 Claude、Codex 的上限截斷");

        GrokAcpRules.DescribeTool(JObject.Parse("""{"title":"mcp__github__search","_meta":{"x.ai/tool":{"name":"mcp__github__search","namespace":"mcp"}}}"""))
            .Should().Match<GrokAcpRules.ToolDescription>(d => d.Kind == "mcp" && d.Name == "MCP：mcp__github__search");
        GrokAcpRules.DescribeTool(JObject.Parse("""{"title":"x_keyword_search","kind":"fetch","_meta":{"x.ai/tool":{"name":"x_keyword_search","label":"X Search"}}}"""))
            .Should().Match<GrokAcpRules.ToolDescription>(d => d.Name == "X Search" && d.Kind == "web");
        GrokAcpRules.IsHiddenTool("ask_user_question").Should().BeTrue();
        GrokAcpRules.IsHiddenTool("exit_plan_mode").Should().BeTrue();
        GrokAcpRules.IsHiddenTool("write").Should().BeFalse();
    }

    [Fact]
    public void 工具輸出_content的文字_指令沒輸出時不退回rawOutput_拒絕與結束碼()
    {
        GrokAcpRules.ParseToolOutput(JObject.Parse("""{"status":"completed","content":[{"type":"content","content":{"type":"text","text":"hello-myriagent\r\nline-two\r\n"}}],"rawOutput":{"type":"Bash","output_for_prompt":"exit: 0\nhello-myriagent\nline-two\n","exit_code":0}}"""))
            .Should().Be("hello-myriagent\nline-two\n");
        GrokAcpRules.ParseToolOutput(JObject.Parse("""{"status":"in_progress","content":[{"type":"content","content":{"type":"text","text":""}}],"rawOutput":{"type":"Bash","output_for_prompt":"","exit_code":0}}"""))
            .Should().BeNull();
        GrokAcpRules.ParseToolOutput(JObject.Parse("""{"status":"completed","content":[{"type":"content","content":{"type":"text","text":""}}],"rawOutput":{"type":"Bash","output_for_prompt":"exit: 0\n"}}"""))
            .Should().BeNull("給模型看的 exit: 0 不顯示");
        GrokAcpRules.ParseToolOutput(JObject.Parse("""{"status":"failed","rawOutput":{"type":"SearchReplace","EditsFailed":{"tool_output_for_prompt":"old_string not found"}}}"""))
            .Should().Be("old_string not found");
        GrokAcpRules.ParseToolOutput(JObject.Parse("""{"status":"failed","rawOutput":{"error":"boom"}}""")).Should().Be("boom");

        GrokAcpRules.IsRejectedUpdate(JObject.Parse("""{"status":"failed","_meta":{"toolNotRun":"rejected"}}""")).Should().BeTrue();
        GrokAcpRules.IsRejectedUpdate(JObject.Parse("""{"status":"failed"}""")).Should().BeFalse();
        GrokAcpRules.CommandExitCode(JObject.Parse("""{"rawOutput":{"type":"Bash","exit_code":2}}""")).Should().Be(2);
        GrokAcpRules.CommandExitCode(JObject.Parse("""{"rawOutput":{"type":"SearchReplace"}}""")).Should().BeNull();
        GrokAcpRules.ToolEndStatus("completed").Should().Be(AgentItemStatus.Done);
        GrokAcpRules.ToolEndStatus("failed").Should().Be(AgentItemStatus.Failed);
        GrokAcpRules.ToolEndStatus("in_progress").Should().BeNull();
        GrokAcpRules.ParsePlanEntries(JArray.Parse("""[{"content":"a","status":"in_progress"},{"content":"b","status":"completed"},{"content":"c"},{"status":"pending"}]"""))
            .Select(s => $"{s.Text}:{s.Status}").Should().Equal("a:inProgress", "b:completed", "c:pending");
    }

    // ───── session/update 解讀（實測樣本） ─────

    private static List<AgentEvent> Feed(GrokUpdateParser parser, params string[] updates)
        => updates.SelectMany(u => parser.HandleUpdate(JObject.Parse(u))).ToList();

    [Fact]
    public void 解讀_欄位是JSON的null不丟例外()
    {
        // ACP 的 _meta、指令的 input 都可以是 null：JSON 的 null 在 Newtonsoft 是 JValue，不是 C# 的 null，?[] 擋不住
        var parser = new GrokUpdateParser("g");
        parser.BeginTurn();
        var events = Feed(parser,
            """{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"compact","description":"c","input":null}]}""",
            """{"sessionUpdate":"tool_call","toolCallId":"r1","title":"read_file","rawInput":{"file_path":"a.txt"},"_meta":null}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"r1","kind":"read","title":"Read a.txt","_meta":null}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"r1","status":"completed","content":[{"type":"content","content":null}],"_meta":null}""");
        events.Select(e => e.GetType().Name).Should().Equal(nameof(AgentCommandsEvent), nameof(AgentToolStartedEvent), nameof(AgentToolCompletedEvent));
        events.OfType<AgentCommandsEvent>().Single().Commands.Should().Equal(new AgentSlashCommand("compact", "c", null));
        events.OfType<AgentToolStartedEvent>().Single().Title.Should().Be("a.txt");
        events.OfType<AgentToolCompletedEvent>().Single().Status.Should().Be(AgentItemStatus.Done);

        GrokAcpRules.DescribeTool(JObject.Parse("""{"title":"grep","rawInput":{"pattern":"x"},"_meta":null}""")).Name.Should().Be("搜尋內容");
        GrokAcpRules.IsRejectedUpdate(JObject.Parse("""{"status":"failed","_meta":null}""")).Should().BeFalse();
        GrokAcpRules.ParseToolOutput(JObject.Parse("""{"status":"completed","content":[{"type":"content","content":"純文字"}]}""")).Should().BeNull();
    }

    [Fact]
    public void 解讀_指令工具_等第一個更新才開卡_說明不當輸出_執行中只送新增_完成時整段()
    {
        var parser = new GrokUpdateParser("g");
        parser.BeginTurn();
        var events = Feed(parser,
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"我會執行"}}""",
            """{"sessionUpdate":"tool_call","toolCallId":"c1","title":"run_terminal_command","rawInput":{"command":"Write-Output hello-myriagent; Write-Output line-two","description":"Print two lines"},"_meta":{"x.ai/tool":{"version":1,"name":"run_terminal_command","kind":"execute","namespace":"grok_build","label":"Run Command","read_only":false}}}""");
        events.Select(e => e.GetType().Name).Should().Equal(nameof(AgentTextDeltaEvent), nameof(AgentTextCompletedEvent));
        events.OfType<AgentTextCompletedEvent>().Single().Should().Be(new AgentTextCompletedEvent("g-t1", "我會執行"));
        parser.RunningToolIds.Should().Equal("c1");

        var update = Feed(parser,
            """{"sessionUpdate":"tool_call_update","toolCallId":"c1","kind":"execute","title":"Execute `Write-Output hello-myriagent; Write-Output line-two`","content":[{"type":"content","content":{"type":"text","text":"Print two lines"}}],"locations":[],"rawInput":{"variant":"Bash","command":"Write-Output hello-myriagent; Write-Output line-two","description":"Print two lines","is_background":false},"_meta":{"x.ai/tool":{"version":1,"name":"run_terminal_command","kind":"execute","input":{"command":"Write-Output hello-myriagent; Write-Output line-two"}}}}""");
        var started = update.OfType<AgentToolStartedEvent>().Single();
        started.Should().Match<AgentToolStartedEvent>(e => e.ToolId == "c1" && e.ToolName == "執行指令" && e.ToolKind == "bash" && e.Title == "Write-Output hello-myriagent; Write-Output line-two");
        update.OfType<AgentToolOutputEvent>().Should().BeEmpty("沒有 status 的更新 content 是工具說明");

        var running = Feed(parser,
            """{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"in_progress","content":[{"type":"content","content":{"type":"text","text":""}}],"rawOutput":{"type":"Bash","output":[],"output_for_prompt":"","exit_code":0}}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"in_progress","content":[{"type":"content","content":{"type":"text","text":"hello-myriagent"}}],"rawOutput":{"type":"Bash","output_for_prompt":"hello-myriagent","exit_code":0}}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"hello-myriagent\r\nline-two\r\n"}}],"rawOutput":{"type":"Bash","output_for_prompt":"exit: 0\nhello-myriagent\nline-two\n","exit_code":0}}""");
        running.OfType<AgentToolOutputEvent>().Select(e => e.Delta).Should().Equal("hello-myriagent");
        running.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(e => e.Status == AgentItemStatus.Done && e.Output == "hello-myriagent\nline-two\n");
        parser.HasRunningTools.Should().BeFalse();

        var next = Feed(parser, """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"hello-myriagent"}}""");
        next.OfType<AgentTextDeltaEvent>().Single().BlockId.Should().Be("g-t2", "工具之後開新的文字區塊");
    }

    [Fact]
    public void 解讀_編輯被拒絕_工具卡標已拒絕_編輯成功有差異不附輸出_結束碼非0是失敗()
    {
        var parser = new GrokUpdateParser("g");
        parser.BeginTurn();
        var rejected = Feed(parser,
            """{"sessionUpdate":"tool_call","toolCallId":"w1","title":"write","rawInput":{"file_path":"C:\\w\\reject.txt","content":"nope\n"},"_meta":{"x.ai/tool":{"name":"write","kind":"write"}}}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"w1","kind":"edit","title":"Write `C:\\w\\reject.txt`","content":[{"type":"diff","path":"C:\\w\\reject.txt","oldText":"","newText":"nope\n"}],"locations":[{"path":"C:\\w\\reject.txt"}],"rawInput":{"variant":"Write","file_path":"C:\\w\\reject.txt","content":"nope\n"},"_meta":{"x.ai/tool":{"name":"write","kind":"write","input":{"path":"C:\\w\\reject.txt"}}}}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"w1","status":"failed","content":[{"type":"content","content":{"type":"text","text":"User rejected the execution for tool `write`"}}],"_meta":{"toolNotRun":"rejected"}}""");
        var card = rejected.OfType<AgentToolStartedEvent>().Single();
        card.Should().Match<AgentToolStartedEvent>(e => e.ToolKind == "write" && e.FilePath == @"C:\w\reject.txt" && e.Diff != null);
        rejected.OfType<AgentToolCompletedEvent>().Single().Should().Be(new AgentToolCompletedEvent("w1", AgentItemStatus.Declined, null, Array.Empty<string>()));

        var written = Feed(parser,
            """{"sessionUpdate":"tool_call","toolCallId":"w2","title":"write","rawInput":{"file_path":"C:\\w\\a.txt","content":"a"},"_meta":{"x.ai/tool":{"name":"write","kind":"write"}}}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"w2","kind":"edit","title":"Write","content":[{"type":"diff","path":"C:\\w\\a.txt","oldText":"","newText":"a"}],"rawInput":{"variant":"Write","file_path":"C:\\w\\a.txt","content":"a"}}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"w2","status":"completed","content":[{"type":"diff","path":"C:\\w\\a.txt","oldText":"","newText":"a"}],"rawOutput":{"type":"SearchReplace","EditsApplied":{"tool_output_for_prompt":"The file C:\\w\\a.txt has been created."}}}""");
        written.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(e => e.Status == AgentItemStatus.Done && e.Output == null);

        var failedCommand = Feed(parser,
            """{"sessionUpdate":"tool_call","toolCallId":"x1","title":"run_terminal_command","rawInput":{"command":"exit 2"},"_meta":{"x.ai/tool":{"name":"run_terminal_command","kind":"execute"}}}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"x1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"bad\r\n"}}],"rawOutput":{"type":"Bash","exit_code":2}}""");
        failedCommand.OfType<AgentToolStartedEvent>().Should().ContainSingle("tool_call 就帶了結束狀態：照更新處理");
        failedCommand.OfType<AgentToolCompletedEvent>().Single().Should().Match<AgentToolCompletedEvent>(e => e.Status == AgentItemStatus.Failed && e.Output == "bad\n");
    }

    [Fact]
    public void 解讀_提問與計畫確認不畫成工具卡_思考與待辦清單_核准請求先補發工具卡()
    {
        var parser = new GrokUpdateParser("g");
        parser.BeginTurn();
        var hidden = Feed(parser,
            """{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"想一下"}}""",
            """{"sessionUpdate":"tool_call","toolCallId":"q1","title":"ask_user_question","rawInput":{"questions":[]},"_meta":{"x.ai/tool":{"name":"ask_user_question","kind":"ask_user"}}}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"q1","kind":"other","title":"Ask: 你偏好哪個顏色？","_meta":{"x.ai/tool":{"name":"ask_user_question","kind":"ask_user"}}}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"q1","status":"completed","content":[{"type":"content","content":{"type":"text","text":"User has answered"}}]}""",
            """{"sessionUpdate":"plan","entries":[{"content":"寫測試","status":"in_progress","priority":"medium"}]}""",
            """{"sessionUpdate":"available_commands_update","availableCommands":[{"name":"compact","description":"c"}]}""",
            """{"sessionUpdate":"current_mode_update","currentModeId":"default"}""");
        hidden.Select(e => e.GetType().Name).Should().Equal(nameof(AgentThinkingDeltaEvent), nameof(AgentPlanEvent), nameof(AgentCommandsEvent));
        hidden.OfType<AgentThinkingDeltaEvent>().Single().BlockId.Should().Be("g-r1");
        parser.RunningToolIds.Should().BeEmpty();

        Feed(parser, """{"sessionUpdate":"tool_call","toolCallId":"e1","title":"run_terminal_command","rawInput":{"command":"npm test"},"_meta":{"x.ai/tool":{"name":"run_terminal_command","kind":"execute"}}}""")
            .Should().BeEmpty("等第一個更新或核准請求才開卡");
        var announced = parser.AnnounceForRequest(JObject.Parse("""{"toolCallId":"e1","kind":"execute","title":"Execute `npm test`","rawInput":{"variant":"Bash","command":"npm test"}}"""));
        announced.OfType<AgentToolStartedEvent>().Single().Title.Should().Be("npm test");
        parser.GetToolCall("e1")!.Value<string>("kind").Should().Be("execute", "合併了請求裡的內容");
        Feed(parser, """{"sessionUpdate":"tool_call_update","toolCallId":"e1","kind":"execute","title":"Execute `npm test`"}""")
            .OfType<AgentToolStartedEvent>().Should().BeEmpty("同一張卡只開一次");

        var finished = parser.FinishTurn();
        finished.OfType<AgentToolCompletedEvent>().Single().Should().Be(new AgentToolCompletedEvent("e1", AgentItemStatus.Cancelled, GrokAcpRules.CancelledToolOutput, Array.Empty<string>()));
    }

    [Fact]
    public void 解讀_這一輪結束_還沒開卡的工具補開再結束_保留已有的輸出()
    {
        var parser = new GrokUpdateParser("g");
        parser.BeginTurn();
        Feed(parser,
            """{"sessionUpdate":"tool_call","toolCallId":"a","title":"run_terminal_command","rawInput":{"command":"sleep 30"},"_meta":{"x.ai/tool":{"name":"run_terminal_command"}}}""",
            """{"sessionUpdate":"tool_call","toolCallId":"b","title":"run_terminal_command","rawInput":{"command":"echo"},"_meta":{"x.ai/tool":{"name":"run_terminal_command"}}}""",
            """{"sessionUpdate":"tool_call_update","toolCallId":"b","status":"in_progress","content":[{"type":"content","content":{"type":"text","text":"partial"}}],"rawOutput":{"type":"Bash"}}""",
            """{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"等一下"}}""");

        var events = parser.FinishTurn(AgentItemStatus.Cancelled, GrokAcpRules.InterruptedToolOutput);

        events.OfType<AgentTextCompletedEvent>().Single().Text.Should().Be("等一下");
        events.OfType<AgentToolStartedEvent>().Single().ToolId.Should().Be("a", "b 已經開過卡");
        events.OfType<AgentToolCompletedEvent>().Select(e => (e.ToolId, e.Output)).Should().Equal(
            ("a", GrokAcpRules.InterruptedToolOutput), ("b", "partial\n\n" + GrokAcpRules.InterruptedToolOutput));
        parser.BeginTurn();
        parser.RunningToolIds.Should().BeEmpty("新的一輪清掉上一輪的工具");
    }

    // ───── 帳戶 ─────

    [Fact]
    public void ApplyEnvironment_剝除xAI金鑰與指向別處的設定_一律關自動更新_隔離帳戶設GROK_HOME()
    {
        Dictionary<string, string?> Inherited() => new(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\Windows",
            ["XAI_API_KEY"] = "xai-key",
            ["GROK_AUTH_PATH"] = @"C:\auth.json",
            ["grok_config"] = @"C:\c.toml",
            ["GROK_CONFIG_PATH"] = @"C:\c2.toml",
            ["GROK_LEADER_SOCKET"] = @"\\.\pipe\grok",
            ["GROK_HOME"] = @"D:\mygrok",
            ["CLAUDECODE"] = "1",
        };

        var system = Inherited();
        AccountRules.ApplyEnvironment(Grok(), system);
        system.Should().BeEquivalentTo(new Dictionary<string, string?> { ["PATH"] = @"C:\Windows", ["GROK_HOME"] = @"D:\mygrok", ["GROK_DISABLE_AUTOUPDATER"] = "1" },
            "系統預設帳戶保留使用者自己設的 GROK_HOME");

        var isolated = Inherited();
        AccountRules.ApplyEnvironment(Grok(@"C:\acc\g1\grok"), isolated);
        isolated["GROK_HOME"].Should().Be(@"C:\acc\g1\grok");
        isolated.Keys.Should().NotContain(new[] { "XAI_API_KEY", "GROK_AUTH_PATH", "grok_config", "GROK_LEADER_SOCKET" });

        var claude = Inherited();
        AccountRules.ApplyEnvironment(new AgentAccount { Provider = AgentProvider.Claude, Label = "c" }, claude);
        claude.Keys.Should().Contain("XAI_API_KEY", "Claude 的工具可能正需要 xAI 的變數").And.NotContain("GROK_DISABLE_AUTOUPDATER");
    }

    [Fact]
    public void 帳戶規則_本機預設id_可隔離_沒有精簡模式_有額度_憑證檔與名稱()
    {
        AccountRules.SystemDefaultId(AgentProvider.Grok).Should().Be("local-grok");
        AccountRules.SupportsIsolatedAccounts(AgentProvider.Grok).Should().BeTrue();
        AccountRules.SupportsSafeMode(AgentProvider.Grok).Should().BeFalse();
        AccountRules.HasUsageQuota(AgentProvider.Grok).Should().BeTrue();
        AccountRules.GetCredentialFile(Grok(@"C:\acc\g1\grok")).Should().Be(@"C:\acc\g1\grok\auth.json");
        AccountRules.ResolveConfigDirectory(Grok(@"C:\acc\g1\grok")).Should().Be(@"C:\acc\g1\grok");
        GrokAcpRules.ResolveHome(@"C:\Users\u").Should().Be(@"C:\Users\u\.grok");
        AccountRules.SystemDefaultAppearance(AgentProvider.Grok).Should().Be(("Grok（本機）", "#A3B83A"));
        AccountRules.DescribeStatus(AgentProvider.Grok, AccountStatus.NotLoggedIn).Should().Be("尚未登入 Grok（grok.com 帳戶）");
        AccountRules.DescribeStatus(AgentProvider.Grok, AccountStatus.NotInstalled).Should().Be("這台電腦尚未安裝 Grok Build CLI");
        AccountRules.DefaultColor(AgentProvider.Grok, new[] { new AgentAccount { Color = "#A3B83A" } }).Should().Be("#3FB950");
        AgentProviders.ProductName(AgentProvider.Grok).Should().Be("Grok Build");
        AccountRules.GuessProviderFromUsageKeys(new[] { UsageRules.GrokCreditsKey }).Should().Be(AgentProvider.Grok);
        AccountRules.GuessProviderFromUsageKeys(new[] { UsageRules.GrokCreditsKey, "session" }).Should().BeNull("像不只一種");
        CliRestartRules.MaxConcurrentStarts(AgentProvider.Grok).Should().BeGreaterThan(0);
    }

    [Fact]
    public void 隔離資料夾_寫入不匯入Claude設定的config_已有設定時只補段落()
    {
        using var temp = new TempDirectory("grok-home");
        var fresh = Path.Combine(temp.Root, "fresh");
        GrokAcpRules.EnsureIsolatedHome(fresh);
        File.ReadAllText(Path.Combine(fresh, "config.toml")).Should().Be("[claude_compat]\nimported = true\n");

        var existing = temp.CreateDirectory("existing");
        File.WriteAllText(Path.Combine(existing, "config.toml"), "[marketplace]\nurl = \"x\"");
        GrokAcpRules.EnsureIsolatedHome(existing);
        File.ReadAllText(Path.Combine(existing, "config.toml")).Should().Be("[marketplace]\nurl = \"x\"\n\n[claude_compat]\nimported = true\n");

        GrokAcpRules.EnsureIsolatedHome(existing);
        File.ReadAllText(Path.Combine(existing, "config.toml")).Split("[claude_compat]").Should().HaveCount(2, "已經有就不重複補");
        GrokAcpRules.HasClaudeCompatSection("a = 1\r\n  [claude_compat]  \r\n").Should().BeTrue();
        GrokAcpRules.HasClaudeCompatSection("[claude_compat.extra]").Should().BeFalse();
    }

    // ───── 找檔與更新 ─────

    [Fact]
    public void 找檔與更新路線_官方安裝位置優先_以grok_update自我更新()
    {
        CliLocator.GetSearchDirectories(AgentProvider.Grok)[0].Should().Be(CliLocator.GrokInstallDirectory);
        var found = CliLocator.Resolve(
            AgentProvider.Grok,
            new[] { @"C:\Users\U\.grok\bin", @"C:\Path" },
            p => p == CliIdentities.GrokNativeExe,
            @"C:\Users\U\.local\bin",
            @"C:\npm",
            @"C:\managed");
        found.Should().NotBeNull();
        found!.Kind.Should().Be(CliInstallKind.Other);
        found.NativeExecutable.Should().Be(CliIdentities.GrokNativeExe);
        CliUpdateRules.RouteFor(found.Kind).Should().Be(CliUpdateRoute.SelfUpdate);
        CliVersionRules.ParseVersion("grok 1.0.67 (2fe064390d07) [stable]").Should().Be("1.0.67");
        CliManager.GrokInstallCommand.Should().Contain("https://x.ai/cli/install.ps1").And.NotContain("winget");
    }

    [Fact]
    public void 登入網址的裝置碼_只收accounts_x_ai與合格的user_code()
    {
        // tools/probes/grok/login_url_probe.py 實測的形狀（2026-10-22）：https://accounts.x.ai/<路徑>/device?user_code=XXXX-XXXX，get_url 沒回期限
        var now = DateTimeOffset.FromUnixTimeMilliseconds(1792000000000);
        var url = new Uri("https://accounts.x.ai/oauth2/device?user_code=ABCD-EF12");
        var code = GrokAcpRules.ParseDeviceCode(url, now)!;
        code.UserCode.Should().Be("ABCD-EF12");
        code.VerificationUrl.Should().Be(url, "驗證網址就是整個登入網址（代碼已帶在裡面）");
        code.ExpiresAt.Should().Be((now + GrokAcpRules.DeviceCodeLifetime).ToUnixTimeMilliseconds());
        code.LoginId.Should().Be("grok");
        CodexDeviceCodeRules.IsAllowedVerificationUrl(code.VerificationUrl.AbsoluteUri).Should().BeTrue("電腦與伺服器的白名單都收 accounts.x.ai");

        foreach (var bad in new[]
                 {
                     "https://evil.example/oauth2/device?user_code=ABCD-EF12",
                     "http://accounts.x.ai/oauth2/device?user_code=ABCD-EF12",
                     "https://accounts.x.ai:8443/oauth2/device?user_code=ABCD-EF12",
                     "https://user@accounts.x.ai/oauth2/device?user_code=ABCD-EF12",
                     "https://accounts.x.ai/oauth2/device",
                     "https://accounts.x.ai/oauth2/device?user_code=abcd-ef12",
                     "https://accounts.x.ai/oauth2/device?user_code=AB",
                     "https://accounts.x.ai/oauth2/device?user_code=-ABCD",
                     "https://accounts.x.ai/oauth2/device?user_code=ABCD%3Cx%3E",
                 })
            GrokAcpRules.ParseDeviceCode(new Uri(bad), now).Should().BeNull(bad);
        GrokAcpRules.ParseDeviceCode(null, now).Should().BeNull();
    }

    [Fact]
    public void 工具卡_子代理流程與排程用中文名稱()
    {
        GrokAcpRules.DescribeTool(Tool("spawn_subagent")).Should().BeEquivalentTo(new { Name = "子代理", Kind = "agent" }, opts => opts.ExcludingMissingMembers());
        GrokAcpRules.DescribeTool(Tool("workflow")).Kind.Should().Be("other");
        GrokAcpRules.DescribeTool(Tool("workflow")).Name.Should().Be("流程");
        GrokAcpRules.DescribeTool(Tool("scheduler_create")).Name.Should().Be("排程");
        GrokAcpRules.DescribeTool(Tool("scheduler_create")).Kind.Should().Be("other");
        GrokAcpRules.DescribeTool(Tool("scheduler_list")).Name.Should().Be("排程清單");
        GrokAcpRules.DescribeTool(Tool("scheduler_delete")).Name.Should().Be("取消排程");
    }

    [Fact]
    public void 背景輸出_只讀本機檔尾_缺檔是空的_其他IO給中文()
    {
        var dir = Path.Combine(Path.GetTempPath(), "myriagent-grok-bg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "out.log");
            File.WriteAllText(path, "a\r\nb");
            var read = GrokAcpRules.ReadBackgroundOutput(path);
            read.Output.Should().Be("a\nb");
            read.Error.Should().BeNull();
            read.Truncated.Should().BeFalse();
            read.TotalBytes.Should().Be(new FileInfo(path).Length);

            var big = Path.Combine(dir, "big.log");
            File.WriteAllText(big, new string('x', ClaudeControlRules.MaxTaskOutputChars + 20) + "TAIL");
            var tail = GrokAcpRules.ReadBackgroundOutput(big);
            tail.Truncated.Should().BeTrue();
            tail.Output.Should().EndWith("TAIL");
            tail.Output!.Length.Should().Be(ClaudeControlRules.MaxTaskOutputChars);
            tail.TotalBytes.Should().Be(new FileInfo(big).Length);

            var missing = GrokAcpRules.ReadBackgroundOutput(Path.Combine(dir, "missing.log"));
            missing.Output.Should().BeEmpty();
            missing.Error.Should().BeNull();

            GrokAcpRules.ReadBackgroundOutput(@"\\server\share\a.log").Error.Should().Be("這個背景工作沒有本機的輸出檔");
            GrokAcpRules.ReadBackgroundOutput(dir).Error.Should().StartWith("讀不到這個背景工作的輸出：");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 分叉_參數照實測的形狀_回應取newSessionId()
    {
        // 1.0.70 實測（tools/probes/grok/fork_probe.py）：兩個 id 都是來源、兩個 cwd 相同、不帶 targetPromptIndex（整份複製）
        var fork = GrokAcpRules.BuildForkParams("sid", @"C:\w", "new-id");
        fork.Properties().Select(p => p.Name).Should().Equal("sessionId", "sourceSessionId", "sourceCwd", "newCwd", "newSessionId");
        fork.Value<string>("sessionId").Should().Be("sid");
        fork.Value<string>("sourceSessionId").Should().Be("sid");
        fork.Value<string>("sourceCwd").Should().Be(fork.Value<string>("newCwd"));
        fork["targetPromptIndex"].Should().BeNull();

        GrokAcpRules.ParseForkSessionId(JObject.Parse("""{"newSessionId":"child","chatMessagesCopied":6,"parentSessionId":"sid"}""")).Should().Be("child");
        GrokAcpRules.ParseForkSessionId(JObject.Parse("""{}""")).Should().BeNull();
        GrokAcpRules.ParseForkSessionId(JObject.Parse("""{"newSessionId":""}""")).Should().BeNull();
        GrokAcpRules.ParseForkSessionId(null).Should().BeNull();
    }

    private static JObject Tool(string name)
        => JObject.Parse("""{"title":"TOOL","_meta":{"x.ai/tool":{"name":"TOOL"}}}""".Replace("TOOL", name));
}
