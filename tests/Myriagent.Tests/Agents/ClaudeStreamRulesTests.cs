using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// Claude Code CLI 雙向 stream-json 協定的規則（事件行取自 Claude Code 2.1.293／2.1.295 探針實錄，只刪掉與測試無關的欄位）
/// </summary>
public class ClaudeStreamRulesTests
{
    private static ClaudeLaunchOptions Options(bool resume = false, AgentPermissionMode mode = AgentPermissionMode.Ask,
        string? model = null, string? effort = null, bool local = false)
        => new("11111111-2222-3333-4444-555555555555", resume, mode, model, effort, "背景說明", new[] { @"C:\Agent\conv1" }, local);

    // ───── 啟動參數 ─────

    [Fact]
    public void BuildArguments_新對話_以SessionId建立並用stdio詢問權限()
    {
        var args = ClaudeStreamRules.BuildArguments(Options());

        args.Should().ContainInOrder("-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages");
        args.Should().ContainInOrder("--permission-prompt-tool", "stdio");
        args.Should().ContainInOrder("--permission-mode", "default");
        args.Should().Contain("--allow-dangerously-skip-permissions", "實測沒帶時執行中切到完全自動（set_permission_mode bypassPermissions）會被 CLI 拒絕");
        args.Should().NotContain("--dangerously-skip-permissions", "只允許之後切換，不能一開始就跳過所有權限");
        args.Should().ContainInOrder("--session-id", "11111111-2222-3333-4444-555555555555");
        args.Should().NotContain("--resume");
        args.Should().ContainInOrder("--add-dir", @"C:\Agent\conv1");
        args.Should().ContainInOrder("--append-system-prompt", "背景說明");
        args.Should().NotContain("--model");
        args.Should().NotContain("--effort");
        args.Should().NotContain("--settings");
    }

    [Fact]
    public void BuildArguments_續接_改用Resume()
    {
        var args = ClaudeStreamRules.BuildArguments(Options(resume: true));

        args.Should().ContainInOrder("--resume", "11111111-2222-3333-4444-555555555555");
        args.Should().NotContain("--session-id");
    }

    [Fact]
    public void BuildArguments_沒有SessionId_丟出例外()
    {
        var options = Options() with { SessionId = " " };
        FluentActions.Invoking(() => ClaudeStreamRules.BuildArguments(options)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void BuildArguments_精簡模式_排除使用者層設定讓放行規則不會跳過詢問()
    {
        // 實測：只加 --safe-mode 時，使用者 settings.json 的放行規則仍生效，「每步詢問」不會問；
        // 加 --setting-sources project,local 後才會跳 can_use_tool（訂閱登入不受影響）
        var args = ClaudeStreamRules.BuildArguments(Options(local: false));

        args.Should().Contain("--safe-mode");
        args.Should().ContainInOrder("--setting-sources", "project,local");
    }

    [Fact]
    public void BuildArguments_載入使用者客製設定_不帶精簡模式旗標()
    {
        var args = ClaudeStreamRules.BuildArguments(Options(local: true));

        args.Should().NotContain("--safe-mode");
        args.Should().NotContain("--setting-sources");
    }

    [Fact]
    public void BuildArguments_推理強度_以旗標與Settings雙重指定避免被使用者環境變數蓋掉()
    {
        var args = ClaudeStreamRules.BuildArguments(Options(effort: "high", model: "opus"));

        args.Should().ContainInOrder("--model", "opus");
        args.Should().ContainInOrder("--effort", "high");
        int i = args.IndexOf("--settings");
        i.Should().BeGreaterThan(0);
        JObject.Parse(args[i + 1])["env"]!["CLAUDE_CODE_EFFORT_LEVEL"]!.ToString().Should().Be("high");
        ClaudeStreamRules.BuildEnvironmentOverrides("high").Should().ContainKey("CLAUDE_CODE_EFFORT_LEVEL").WhoseValue.Should().Be("high");
        ClaudeStreamRules.BuildEnvironmentOverrides(null).Keys.Should().Equal(new[] { "DISABLE_AUTOUPDATER", "CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING" },
            "沒指定強度就不設強度（只有一律都帶的關閉自動更新與檔案檢查點）");
        ClaudeStreamRules.BuildEnvironmentOverrides(null)["CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING"].Should().Be("1", "回溯的 rewind_files 要它");
        ClaudeStreamRules.BuildEnvironmentOverrides(" ").Should().NotContainKey("CLAUDE_CODE_EFFORT_LEVEL");
    }

    [Fact]
    public void BuildArguments_Ultracode_旗標照帶_環境變數帶背後的xhigh()
    {
        var args = ClaudeStreamRules.BuildArguments(Options(effort: "ultracode", model: "opus"));

        args.Should().ContainInOrder("--effort", "ultracode");
        JObject.Parse(args[args.IndexOf("--settings") + 1])["env"]!["CLAUDE_CODE_EFFORT_LEVEL"]!.ToString()
            .Should().Be("xhigh", "環境變數只認一般的強度；實測 --effort ultracode 加上 xhigh 的變數照樣開啟 ultracode");
        ClaudeStreamRules.BuildEnvironmentOverrides("ultracode")["CLAUDE_CODE_EFFORT_LEVEL"].Should().Be("xhigh");
        ClaudeStreamRules.EffortEnvironmentValue("UltraCode").Should().Be("xhigh");
        ClaudeStreamRules.EffortEnvironmentValue("max").Should().Be("max");
        ClaudeStreamRules.EffortEnvironmentValue(null).Should().BeNull();
    }

    [Fact]
    public void BuildArguments_共用設定_記憶資料夾併進同一個Settings_MCP設定檔以McpConfig帶()
    {
        var args = ClaudeStreamRules.BuildArguments(Options(effort: "high", local: true) with
        {
            AutoMemoryDirectory = @"C:\Users\me\.claude\projects\C--repo-app\memory",
            McpConfigFile = @"C:\data\accounts\a1\claude\.myriagent-shared\mcp-1.json",
        });

        args.Count(a => a == "--settings").Should().Be(1, "只帶一個 --settings");
        var settings = JObject.Parse(args[args.IndexOf("--settings") + 1]);
        settings["env"]!["CLAUDE_CODE_EFFORT_LEVEL"]!.ToString().Should().Be("high");
        settings["autoMemoryDirectory"]!.ToString().Should().Be(@"C:\Users\me\.claude\projects\C--repo-app\memory");
        args.Should().ContainInOrder("--mcp-config", @"C:\data\accounts\a1\claude\.myriagent-shared\mcp-1.json");
        args[args.IndexOf("--mcp-config") + 2].Should().StartWith("--", "--mcp-config 會吃後面所有不是旗標的參數");

        var noEffort = ClaudeStreamRules.BuildArguments(Options(local: true) with { AutoMemoryDirectory = @"C:\m" });
        JObject.Parse(noEffort[noEffort.IndexOf("--settings") + 1]).Properties().Select(p => p.Name).Should().Equal("autoMemoryDirectory");
        noEffort.Should().NotContain("--mcp-config");
    }

    [Fact]
    public void BuildArguments_備援模型_和主模型相同或沒設時不帶()
    {
        var args = ClaudeStreamRules.BuildArguments(Options(model: "opus") with { FallbackModel = "sonnet" });
        args.Should().ContainInOrder("--fallback-model", "sonnet");

        ClaudeStreamRules.BuildArguments(Options(model: "sonnet") with { FallbackModel = "Sonnet" }).Should().NotContain("--fallback-model", "和主模型相同");
        ClaudeStreamRules.BuildArguments(Options() with { FallbackModel = "default" }).Should().NotContain("--fallback-model");
        ClaudeStreamRules.BuildArguments(Options() with { FallbackModel = " " }).Should().NotContain("--fallback-model");
        ClaudeStreamRules.BuildArguments(Options() with { FallbackModel = "haiku" }).Should().ContainInOrder("--fallback-model", "haiku");
    }

    [Fact]
    public void BuildArguments_自動壓縮上限以Settings的Env帶視窗_上限加緩衝_和強度併在一起()
    {
        var args = ClaudeStreamRules.BuildArguments(Options(effort: "high") with { AutoCompactTokens = 400_000 });
        var env = JObject.Parse(args[args.IndexOf("--settings") + 1])["env"]!;
        env["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]!.ToString().Should().Be("433000", "實測門檻＝視窗減 33,000");
        env["CLAUDE_CODE_EFFORT_LEVEL"]!.ToString().Should().Be("high");
        args.Count(a => a == "--settings").Should().Be(1);

        var alone = ClaudeStreamRules.BuildArguments(Options() with { AutoCompactTokens = 67_000 });
        JObject.Parse(alone[alone.IndexOf("--settings") + 1])["env"]!["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]!.ToString().Should().Be("100000");
        ClaudeStreamRules.BuildArguments(Options()).Should().NotContain(a => a.Contains("AUTO_COMPACT"));
    }

    [Fact]
    public void BuildArguments_主代理只在不是精簡模式時帶_輸出風格併進同一個Settings()
    {
        var args = ClaudeStreamRules.BuildArguments(Options(local: true, effort: "high") with { Agent = "autopilot:debugger", OutputStyle = "Explanatory" });
        args.Should().ContainInOrder("--agent", "autopilot:debugger");
        args.Count(a => a == "--settings").Should().Be(1);
        var settings = JObject.Parse(args[args.IndexOf("--settings") + 1]);
        settings["outputStyle"]!.ToString().Should().Be("Explanatory");
        settings["env"]!["CLAUDE_CODE_EFFORT_LEVEL"]!.ToString().Should().Be("high");

        var safe = ClaudeStreamRules.BuildArguments(Options(local: false) with { Agent = "probe-agent", OutputStyle = "Concise" });
        safe.Should().NotContain("--agent", "精簡模式不載入自訂代理");
        JObject.Parse(safe[safe.IndexOf("--settings") + 1])["outputStyle"]!.ToString().Should().Be("Concise", "內建的風格在精簡模式也能用（中樞在精簡模式不帶）");
    }

    [Fact]
    public void BuildArguments_回溯_續接時從那則assistant分支出新的session()
    {
        var args = ClaudeStreamRules.BuildArguments(Options(resume: true) with { ResumeAt = "aec5b384-b518-4e2c-bf14-5fb66196d341" });
        args.Should().ContainInOrder("--resume", "11111111-2222-3333-4444-555555555555", "--resume-session-at", "aec5b384-b518-4e2c-bf14-5fb66196d341", "--fork-session");

        ClaudeStreamRules.BuildArguments(Options(resume: false) with { ResumeAt = "aec5" }).Should().NotContain("--resume-session-at", "新的 session 沒有東西可以分支");
    }

    [Fact]
    public void ParseAgents與OutputStyles_取自initialize回應()
    {
        var response = JObject.Parse("""
            { "agents": [ { "name": "claude", "description": "內建" }, { "name": "Explore", "description": "Fast agent\nfor exploring" },
                          { "name": "probe-agent", "description": "Probe agent", "model": "inherit" }, { "name": "bad name" }, { "name": "Explore" }, { "description": "沒有名字" } ],
              "available_output_styles": [ "default", "Proactive", "Explanatory", 3, "", "default" ], "output_style": "default" }
            """);

        ClaudeStreamRules.ParseAgents(response).Should().Equal(
            new AgentNamedOption("Explore", "Fast agent for exploring"), new AgentNamedOption("probe-agent", "Probe agent"));
        ClaudeStreamRules.ParseOutputStyles(response).Should().Equal("default", "Proactive", "Explanatory");
        ClaudeStreamRules.ParseAgents(new JObject()).Should().BeEmpty();
        ClaudeStreamRules.ParseOutputStyles(JObject.Parse("""{ "available_output_styles": null }""")).Should().BeEmpty();
        ClaudeStreamRules.IsValidAgentName("autopilot:code-reviewer").Should().BeTrue();
        ClaudeStreamRules.IsValidAgentName("a b").Should().BeFalse();
    }

    [Fact]
    public void BuildSetOutputStyleRequest_以apply_flag_settings換風格()
    {
        var request = JObject.Parse(ClaudeStreamRules.BuildSetOutputStyleRequest("style1", "Concise"));
        request["request_id"]!.ToString().Should().Be("style1");
        request["request"]!["subtype"]!.ToString().Should().Be("apply_flag_settings");
        request["request"]!["settings"]!["outputStyle"]!.ToString().Should().Be("Concise");
    }

    [Fact]
    public void BuildArguments_精簡模式不帶共用的記憶與MCP()
    {
        var args = ClaudeStreamRules.BuildArguments(Options(local: false) with { AutoMemoryDirectory = @"C:\m", McpConfigFile = @"C:\mcp.json" });

        args.Should().NotContain("--settings").And.NotContain("--mcp-config");
        args.Should().Contain("--safe-mode");
    }

    [Fact]
    public void BuildArguments_停用背景排程與工作樹工具_略過空白的額外資料夾()
    {
        var args = ClaudeStreamRules.BuildArguments(Options() with { AddDirectories = new[] { @"C:\Agent\conv1", " ", "" } });
        int i = args.IndexOf("--disallowedTools");
        args[i + 1].Split(',').Should().Contain(new[] { "CronCreate", "ScheduleWakeup", "RemoteTrigger", "EnterWorktree", "ExitWorktree" });
        args.Count(a => a == "--add-dir").Should().Be(1);
    }

    [Fact]
    public void BuildArguments_沒有背景說明_不帶AppendSystemPrompt()
        => ClaudeStreamRules.BuildArguments(Options() with { AppendSystemPrompt = null }).Should().NotContain("--append-system-prompt");

    [Theory]
    [InlineData(nameof(AgentPermissionMode.Ask), "default")]
    [InlineData(nameof(AgentPermissionMode.AcceptEdits), "acceptEdits")]
    [InlineData(nameof(AgentPermissionMode.Plan), "plan")]
    [InlineData(nameof(AgentPermissionMode.Auto), "auto")]
    [InlineData(nameof(AgentPermissionMode.FullAccess), "bypassPermissions")]
    public void 權限模式_對應Claude的值且可以反向換回(string mode, string expected)
    {
        var parsed = Enum.Parse<AgentPermissionMode>(mode);
        ClaudeStreamRules.ToClaudePermissionMode(parsed).Should().Be(expected);
        ClaudeStreamRules.FromClaudePermissionMode(expected).Should().Be(parsed);
        ClaudeStreamRules.BuildArguments(Options(mode: parsed)).Should().ContainInOrder("--permission-mode", expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("dontAsk")]
    [InlineData("")]
    public void FromClaudePermissionMode_不認得的值回Null(string? value)
        => ClaudeStreamRules.FromClaudePermissionMode(value).Should().BeNull();

    // ───── stdin 訊息與控制回應 ─────

    [Fact]
    public void BuildUserMessage_圖片在前文字在後且是單行()
    {
        var line = ClaudeStreamRules.BuildUserMessage("這張圖是什麼顏色？\n第二行", new[] { ("image/png", "iVBOR") });
        var json = JObject.Parse(line);

        json["type"]!.ToString().Should().Be("user");
        json["parent_tool_use_id"]!.Type.Should().Be(JTokenType.Null);
        var content = (JArray)json["message"]!["content"]!;
        content.Should().HaveCount(2);
        content[0]!["type"]!.ToString().Should().Be("image");
        content[0]!["source"]!["media_type"]!.ToString().Should().Be("image/png");
        content[0]!["source"]!["data"]!.ToString().Should().Be("iVBOR");
        content[1]!["text"]!.ToString().Should().Be("這張圖是什麼顏色？\n第二行");
        line.Should().NotContain("\n", "stream-json 一行一則訊息");
    }

    [Fact]
    public void 控制請求_Initialize_Interrupt與切換權限模式()
    {
        var init = JObject.Parse(ClaudeStreamRules.BuildInitializeRequest("init-1"));
        init["type"]!.ToString().Should().Be("control_request");
        init["request_id"]!.ToString().Should().Be("init-1");
        init["request"]!["subtype"]!.ToString().Should().Be("initialize");

        JObject.Parse(ClaudeStreamRules.BuildInterruptRequest("x-2"))["request"]!["subtype"]!.ToString().Should().Be("interrupt");

        var mode = JObject.Parse(ClaudeStreamRules.BuildSetPermissionModeRequest("mode-3", AgentPermissionMode.AcceptEdits));
        mode["request"]!["subtype"]!.ToString().Should().Be("set_permission_mode");
        mode["request"]!["mode"]!.ToString().Should().Be("acceptEdits");
    }

    [Fact]
    public void 控制請求_換模型與推理強度_null是預設()
    {
        var model = JObject.Parse(ClaudeStreamRules.BuildSetModelRequest("model-1", "sonnet"));
        model["type"]!.ToString().Should().Be("control_request");
        model["request_id"]!.ToString().Should().Be("model-1");
        model["request"]!["subtype"]!.ToString().Should().Be("set_model");
        model["request"]!["model"]!.ToString().Should().Be("sonnet");
        JObject.Parse(ClaudeStreamRules.BuildSetModelRequest("model-2", null))["request"]!["model"]!.Type
            .Should().Be(JTokenType.Null, "實測 null 回到帳戶預設模型（同不帶 --model），不是啟動時的模型");

        var effort = JObject.Parse(ClaudeStreamRules.BuildSetEffortRequest("effort-1", "max"));
        effort["request"]!["subtype"]!.ToString().Should().Be("apply_flag_settings");
        effort["request"]!["settings"]!["effortLevel"]!.ToString().Should().Be("max", "apply_flag_settings 收 max（settings.json 的 effortLevel 不收）");
        JObject.Parse(ClaudeStreamRules.BuildSetEffortRequest("effort-2", null))["request"]!["settings"]!["effortLevel"]!.Type
            .Should().Be(JTokenType.Null, "實測 null 回到模型的預設強度（同不帶 --effort）");
        JObject.Parse(ClaudeStreamRules.BuildSetEffortRequest("effort-3", " "))["request"]!["settings"]!["effortLevel"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void BuildSetEffortRequest_連環境變數一起改_回到預設時變數也給null()
    {
        // 啟動時帶了 CLAUDE_CODE_EFFORT_LEVEL（app 指定強度時一定帶），只改 effortLevel 會回 success、強度卻不變（實測 2.1.310）
        var high = (JObject)JObject.Parse(ClaudeStreamRules.BuildSetEffortRequest("e1", "high"))["request"]!["settings"]!;
        high["effortLevel"]!.ToString().Should().Be("high");
        high["env"]!["CLAUDE_CODE_EFFORT_LEVEL"]!.ToString().Should().Be("high");
        high.Property("ultracode").Should().BeNull("沒有從 ultracode 換走就不帶（舊版 CLI 不認得這個鍵）");

        var reset = (JObject)JObject.Parse(ClaudeStreamRules.BuildSetEffortRequest("e2", null, "high"))["request"]!["settings"]!;
        reset["effortLevel"]!.Type.Should().Be(JTokenType.Null);
        reset["env"]!["CLAUDE_CODE_EFFORT_LEVEL"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void BuildSetEffortRequest_Ultracode_開啟送別名_換走時明確關掉()
    {
        var on = (JObject)JObject.Parse(ClaudeStreamRules.BuildSetEffortRequest("e1", "ultracode", "high"))["request"]!["settings"]!;
        on["effortLevel"]!.ToString().Should().Be("ultracode");
        on["env"]!["CLAUDE_CODE_EFFORT_LEVEL"]!.ToString().Should().Be("xhigh");
        on.Property("ultracode").Should().BeNull("effortLevel ultracode 本身就會開啟");

        // 實測：ultracode → xhigh 只送 effortLevel 時 ultracode 仍開著（強度數值沒變），要帶 ultracode:false
        foreach (var next in new[] { "xhigh", "high", null })
        {
            var off = (JObject)JObject.Parse(ClaudeStreamRules.BuildSetEffortRequest("e2", next, "ultracode"))["request"]!["settings"]!;
            off["ultracode"]!.Value<bool>().Should().BeFalse($"從 ultracode 換到 {next ?? "預設"}");
        }
    }

    [Fact]
    public void Ultracode_GetSettings的可用旗標_支援xhigh的模型才多列()
    {
        ClaudeStreamRules.ParseUltracodeAvailable(JObject.Parse("""{"applied":{"effort":"medium","ultracode":false,"ultracodeAvailable":true}}""")).Should().BeTrue();
        ClaudeStreamRules.ParseUltracodeAvailable(JObject.Parse("""{"applied":{"ultracodeAvailable":false}}""")).Should().BeFalse();
        ClaudeStreamRules.ParseUltracodeAvailable(JObject.Parse("""{"applied":{"effort":"high"}}""")).Should().BeFalse("舊版沒有這個欄位");
        ClaudeStreamRules.ParseUltracodeAvailable(JObject.Parse("""{"applied":{"ultracodeAvailable":"true"}}""")).Should().BeFalse("只認布林");
        ClaudeStreamRules.ParseUltracodeAvailable(JObject.Parse("{}")).Should().BeFalse();
        ClaudeStreamRules.ParseUltracodeAvailable(null).Should().BeFalse();

        var models = new[]
        {
            new AgentModelOption("opus", "Opus", null, new[] { "low", "medium", "high", "xhigh", "max" }),
            new AgentModelOption("claude-opus-4-6", "Opus 4.6", null, new[] { "low", "medium", "high", "max" }),
            new AgentModelOption("claude-haiku-4-5", "Haiku 4.5", null, Array.Empty<string>()),
        };
        var withUltra = ClaudeStreamRules.WithUltracode(models, ultracodeAvailable: true);
        withUltra[0].Efforts.Should().Equal("low", "medium", "high", "xhigh", "max", "ultracode");
        withUltra[1].Efforts.Should().Equal("low", "medium", "high", "max");
        withUltra[2].Efforts.Should().BeEmpty();
        ClaudeStreamRules.WithUltracode(withUltra, ultracodeAvailable: true)[0].Efforts.Count(e => e == "ultracode").Should().Be(1, "不重複加");
        ClaudeStreamRules.WithUltracode(models, ultracodeAvailable: false)[0].Efforts.Should().NotContain("ultracode", "動態工作流程沒開（例如 Pro 方案預設）");
        models[0].Efforts.Should().NotContain("ultracode", "不改原本的清單");
    }

    [Fact]
    public void QuestionAnswerInput_以題目文字為鍵_多選以逗號串接_不改原輸入()
    {
        var input = JObject.Parse("""{"questions":[{"question":"你想喝哪種飲料？","header":"飲料","options":[{"label":"咖啡"},{"label":"紅茶"}],"multiSelect":false},{"question":"加什麼？","options":[{"label":"糖"},{"label":"奶"}],"multiSelect":true}]}""");
        var answers = new Dictionary<string, IReadOnlyList<string>>
        {
            ["你想喝哪種飲料？"] = new[] { "紅茶" },
            ["加什麼？"] = new[] { "糖", " 奶 ", " " },
            ["空白題"] = new[] { " " },
        };

        var updated = ClaudeStreamRules.BuildQuestionAnswerInput(input, answers);

        updated["questions"]!.Should().BeEquivalentTo(input["questions"]);
        updated["answers"]!["你想喝哪種飲料？"]!.ToString().Should().Be("紅茶");
        updated["answers"]!["加什麼？"]!.ToString().Should().Be("糖, 奶");
        updated["answers"]!["空白題"].Should().BeNull("沒有有效答案的題目不送");
        input["answers"].Should().BeNull("不能改到原本的輸入");
    }

    [Fact]
    public void ParseAskUserQuestion_讀取標題選項與多選_略過沒有題目文字的題目()
    {
        var input = JObject.Parse("""{"questions":[{"question":"你想喝哪種飲料？","header":"飲料選擇","options":[{"label":"咖啡","description":"選擇咖啡"},{"label":"紅茶","description":"選擇紅茶","preview":"🍵"},{"label":" "}],"multiSelect":false},{"question":""},{"question":"要加冰嗎？","multiSelect":true}]}""");

        var questions = ClaudeStreamRules.ParseAskUserQuestion(input);

        questions.Should().HaveCount(2);
        var q = questions[0];
        q.Id.Should().Be("你想喝哪種飲料？", "Claude 的答案以題目文字為鍵");
        q.Header.Should().Be("飲料選擇");
        q.MultiSelect.Should().BeFalse();
        q.AllowOther.Should().BeTrue();
        q.Options.Select(o => o.Label).Should().Equal("咖啡", "紅茶");
        q.Options[1].Preview.Should().Be("🍵");
        questions[1].MultiSelect.Should().BeTrue();
        questions[1].Options.Should().BeEmpty();
    }

    [Fact]
    public void ToSessionPermissions_每一筆建議都改成只在本次對話有效_權限模式切換另外處理()
    {
        var suggestions = JArray.Parse("""[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"mkdir newdir *"}],"behavior":"allow","destination":"localSettings"},{"type":"setMode","mode":"acceptEdits","destination":"session"},{"type":"addDirectories","directories":["C:\\data"],"destination":"localSettings"},"不是物件"]""");

        var result = ClaudeStreamRules.ToSessionPermissions(suggestions);

        result.Select(t => t["type"]!.ToString()).Should().Equal(new[] { "addRules", "addDirectories" },
            "setMode 不靜默轉送：會話以 set_permission_mode 明確切換並記下目前模式，畫面才跟得上");
        result.Select(t => t["destination"]!.ToString()).Should().AllBe("session");
        suggestions[0]!["destination"]!.ToString().Should().Be("localSettings", "不能改到原本的建議");
        ClaudeStreamRules.ToSessionPermissions(null).Should().BeEmpty();
    }

    [Fact]
    public void FindSuggestedMode_取CLI建議的權限模式_不認得的略過()
    {
        ClaudeStreamRules.FindSuggestedMode(JArray.Parse("""[{"type":"addDirectories","directories":["C:\\x"]},{"type":"setMode","mode":"acceptEdits","destination":"session"}]"""))
            .Should().Be(AgentPermissionMode.AcceptEdits);
        ClaudeStreamRules.FindSuggestedMode(JArray.Parse("""[{"type":"setMode","mode":"dontAsk"}]""")).Should().BeNull();
        ClaudeStreamRules.FindSuggestedMode(JArray.Parse("""[{"type":"addRules","rules":[]}]""")).Should().BeNull();
        ClaudeStreamRules.FindSuggestedMode(null).Should().BeNull();
    }

    [Fact]
    public void ExtractSessionGrants_取同意的規則與資料夾_規則字串照CLI格式跳脫()
    {
        var suggestions = JArray.Parse("""
            [
              {"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"npm test:*"},{"toolName":"Read","ruleContent":"C:\\data\\**"},{"toolName":"mcp__srv__tool"},{"toolName":"Bash","ruleContent":"*"},{"ruleContent":"沒有工具名稱"}],"behavior":"allow","destination":"localSettings"},
              {"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"rm -rf *"}],"behavior":"deny","destination":"session"},
              {"type":"addDirectories","directories":["C:\\data","c:\\DATA"," ",3],"destination":"localSettings"},
              {"type":"setMode","mode":"acceptEdits","destination":"session"}
            ]
            """);

        var grants = ClaudeStreamRules.ExtractSessionGrants(suggestions);

        grants.Rules.Should().Equal(new[] { "Bash(npm test:*)", @"Read(C:\\data\\**)", "mcp__srv__tool", "Bash" },
            "只取 allow 規則；反斜線要跳脫（實測 CLI 以同樣格式顯示 --allowedTools 帶入的規則）；內容是 * 就只寫工具名稱");
        grants.Directories.Should().Equal(new[] { @"C:\data" }, "資料夾不分大小寫去重、略過空白與非字串");
        ClaudeStreamRules.ExtractSessionGrants(null).Rules.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Bash", "npm test:*", "Bash(npm test:*)")]
    [InlineData("Bash", "echo (a)", @"Bash(echo \(a\))")]
    [InlineData("Read", @"C:\x", @"Read(C:\\x)")]
    [InlineData("WebFetch", null, "WebFetch")]
    [InlineData("WebFetch", "", "WebFetch")]
    public void FormatPermissionRule_同CLI的規則字串格式(string tool, string? content, string expected)
        => ClaudeStreamRules.FormatPermissionRule(tool, content).Should().Be(expected);

    [Theory]
    [InlineData("Bash(npm test:*)", true)]
    [InlineData("Bash(echo a,b)", true)]
    [InlineData("mcp__srv__tool", true)]
    [InlineData(@"Bash(echo \(a\) b)", false)]
    [InlineData("Bash npm", false)]
    [InlineData("-Bash", false)]
    [InlineData(" ", false)]
    public void IsSingleCliRule_經CLI切割後仍是同一條規則才帶(string rule, bool expected)
        => ClaudeStreamRules.IsSingleCliRule(rule).Should().Be(expected,
            "CLI 以逗號、空白切開 --allowedTools（括號內不切，但跳脫的右括號也會結束括號）");

    [Fact]
    public void BuildArguments_同意過的規則以AllowedTools帶回_每條一個參數()
    {
        var args = ClaudeStreamRules.BuildArguments(Options() with { AllowedTools = new[] { "Bash(npm test:*)", " ", "mcp__srv__tool" } });

        int i = args.IndexOf("--allowedTools");
        i.Should().BeGreaterThan(0);
        args.Skip(i + 1).Take(2).Should().Equal("Bash(npm test:*)", "mcp__srv__tool");
        args[i + 3].Should().Be("--disallowedTools");
        ClaudeStreamRules.BuildArguments(Options()).Should().NotContain("--allowedTools", "沒有同意過的規則就不帶");
    }

    [Fact]
    public void BuildArguments_延伸的對話_從副本分支成事先決定的session()
    {
        const string copy = @"C:\data\conversations\bbb\fork-source\aaaa.jsonl";
        var args = ClaudeStreamRules.BuildArguments(Options(resume: true) with { ResumeTranscript = copy, ForkSession = true });
        args.Should().ContainInOrder("--resume", copy, "--fork-session", "--session-id", "11111111-2222-3333-4444-555555555555");

        var at = ClaudeStreamRules.BuildArguments(Options(resume: true) with { ResumeTranscript = copy, ForkSession = true, ResumeAt = "a1" });
        at.Should().ContainInOrder("--resume", copy, "--resume-session-at", "a1", "--fork-session", "--session-id", "11111111-2222-3333-4444-555555555555");
        at.Count(a => a == "--fork-session").Should().Be(1);

        ClaudeStreamRules.BuildArguments(Options(resume: true) with { ForkSession = true })
            .Should().NotContain("--fork-session", "沒有副本就照常續接");
        ClaudeStreamRules.BuildArguments(Options(resume: false) with { ResumeTranscript = copy, ForkSession = true })
            .Should().NotContain("--fork-session").And.NotContain(copy);
    }

    [Fact]
    public void BuildArguments_換過工作資料夾時以逐字稿路徑續接()
    {
        var args = ClaudeStreamRules.BuildArguments(Options(resume: true) with { ResumeTranscript = @"C:\cfg\projects\C--a\11111111-2222-3333-4444-555555555555.jsonl" });
        args.Should().ContainInOrder("--resume", @"C:\cfg\projects\C--a\11111111-2222-3333-4444-555555555555.jsonl");

        ClaudeStreamRules.BuildArguments(Options(resume: false) with { ResumeTranscript = @"C:\x.jsonl" })
            .Should().ContainInOrder("--session-id", "11111111-2222-3333-4444-555555555555").And.NotContain(@"C:\x.jsonl", "新對話不續接");
    }

    [Theory]
    [InlineData(@"C:\repo\proj", "C--repo-proj")]
    [InlineData(@"C:\repo\proj\", "C--repo-proj")]
    [InlineData(@"C:\", "C--")]
    [InlineData(@"D:\工作\a b", "D-----a-b")]
    public void ProjectFolderName_英數字以外換成減號(string cwd, string expected)
        => ClaudeStreamRules.ProjectFolderName(cwd).Should().Be(expected);

    [Fact]
    public void IsProjectFolderFor_不分大小寫_過長的名稱只比截斷的前綴()
    {
        ClaudeStreamRules.IsProjectFolderFor("c--REPO-proj", @"C:\repo\proj").Should().BeTrue();
        ClaudeStreamRules.IsProjectFolderFor("C--repo-proj2", @"C:\repo\proj").Should().BeFalse();

        var longPath = @"C:\" + string.Join(@"\", Enumerable.Repeat("folder", 40));
        var truncated = ClaudeStreamRules.ProjectFolderName(longPath)[..200];
        ClaudeStreamRules.IsProjectFolderFor(truncated + "-k3j9x", longPath).Should().BeTrue("CLI 對超過 200 字的名稱截斷後加雜湊");
        ClaudeStreamRules.IsProjectFolderFor(truncated, longPath).Should().BeFalse();
    }

    [Fact]
    public void FindTranscriptElsewhere_逐字稿在別的專案資料夾才回傳路徑()
    {
        using var temp = new TempDirectory("transcripts");
        var sessionId = Guid.NewGuid().ToString();
        var a = temp.CreateDirectory("workA");
        var b = temp.CreateDirectory("workB");
        var config = temp.CreateDirectory("cfg");
        var transcript = temp.WriteFile(Path.Combine("cfg", "projects", ClaudeStreamRules.ProjectFolderName(a), sessionId + ".jsonl"), "{}\n");

        ClaudeStreamRules.FindTranscriptElsewhere(config, sessionId, b).Should().Be(transcript, "換到 B 資料夾：要以逐字稿路徑續接");
        ClaudeStreamRules.FindTranscriptElsewhere(config, sessionId, a).Should().BeNull("就在目前資料夾對應的專案資料夾：照常以 id 續接");
        ClaudeStreamRules.FindTranscriptElsewhere(config, Guid.NewGuid().ToString(), b).Should().BeNull("到處都找不到：交給 CLI 回報找不到");
        ClaudeStreamRules.FindTranscriptElsewhere(config, @"..\x", b).Should().BeNull("session id 不是 UUID 就不找");
        ClaudeStreamRules.FindTranscriptElsewhere(temp.Combine("沒有這個資料夾"), sessionId, b).Should().BeNull();

        // 兩個資料夾都有（例如在 B 也建過）：目前資料夾對應的優先
        temp.WriteFile(Path.Combine("cfg", "projects", ClaudeStreamRules.ProjectFolderName(b), sessionId + ".jsonl"), "{}\n");
        ClaudeStreamRules.FindTranscriptElsewhere(config, sessionId, b).Should().BeNull();
    }

    [Fact]
    public void ResolveConfigDirectory_用帳戶環境設定後的CLAUDE_CONFIG_DIR()
    {
        ClaudeStreamRules.ResolveConfigDirectory(env => env["CLAUDE_CONFIG_DIR"] = @"C:\accounts\a1").Should().Be(@"C:\accounts\a1");

        var fallback = ClaudeStreamRules.ResolveConfigDirectory(env => env.Remove("CLAUDE_CONFIG_DIR"));
        fallback.Should().Be(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
    }

    [Fact]
    public void BuildFolderChangedNote_說明新的工作資料夾()
        => ClaudeStreamRules.BuildFolderChangedNote(@"D:\work").Should().Contain(@"D:\work").And.Contain("工作資料夾").And.EndWith("\n\n");

    [Theory]
    [InlineData(null, true)]
    [InlineData("""{"kind":"human"}""", true)]
    [InlineData("""{"kind":"auto-continuation"}""", true)]
    [InlineData("""{}""", true)]
    [InlineData("""{"kind":"task-notification"}""", false)]
    [InlineData("""{"kind":"peer"}""", false)]
    public void IsUserTurnOrigin_只有使用者送出的訊息那一輪才算(string? originJson, bool expected)
        => ClaudeStreamRules.IsUserTurnOrigin(originJson == null ? null : JObject.Parse(originJson)).Should().Be(expected);

    [Fact]
    public void AllowAndDeny_符合SDK的回應形狀()
    {
        var input = new JObject { ["command"] = "echo hi" };
        var allow = ClaudeStreamRules.Allow(input);
        allow["behavior"]!.ToString().Should().Be("allow");
        allow["updatedInput"]!["command"]!.ToString().Should().Be("echo hi");
        allow["updatedPermissions"].Should().BeNull();

        var withPermissions = ClaudeStreamRules.Allow(input, JArray.Parse("""[{"type":"setMode","mode":"acceptEdits","destination":"session"}]"""));
        ((JArray)withPermissions["updatedPermissions"]!).Should().ContainSingle();
        ClaudeStreamRules.Allow(input, new JArray())["updatedPermissions"].Should().BeNull("空清單不帶");

        var deny = ClaudeStreamRules.Deny("不行", interrupt: true);
        deny["behavior"]!.ToString().Should().Be("deny");
        deny["message"]!.ToString().Should().Be("不行");
        deny["interrupt"]!.Value<bool>().Should().BeTrue();

        var response = JObject.Parse(ClaudeStreamRules.BuildSuccessResponse("req-1", allow));
        response["type"]!.ToString().Should().Be("control_response");
        response["response"]!["subtype"]!.ToString().Should().Be("success");
        response["response"]!["request_id"]!.ToString().Should().Be("req-1");
        response["response"]!["response"]!["behavior"]!.ToString().Should().Be("allow");

        var error = JObject.Parse(ClaudeStreamRules.BuildErrorResponse("req-2", "不支援"));
        error["response"]!["subtype"]!.ToString().Should().Be("error");
        error["response"]!["error"]!.ToString().Should().Be("不支援");
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("MultiEdit")]
    [InlineData("NotebookEdit")]
    public void DescribeAllowForSession_編輯類照CLI建議改為自動接受編輯_沒有建議就只放行同樣的操作(string tool)
    {
        // 每步詢問下 CLI 對編輯建議 setMode acceptEdits；工作資料夾外、.claude 等敏感檔 CLI 不給這個建議（仍要逐次詢問）
        var setMode = JArray.Parse("""[{"type":"setMode","mode":"acceptEdits","destination":"session"}]""");
        ClaudeStreamRules.DescribeAllowForSession(tool, setMode).Should().Be("本對話改為自動接受編輯（檔案修改與 mkdir、rm、mv 等檔案指令不再詢問）");
        ClaudeStreamRules.DescribeAllowForSession(tool, null).Should().Be("本對話同樣的操作都允許");
    }

    [Fact]
    public void DescribeAllowForSession_其他工具說明CLI建議的規則範圍()
    {
        var rules = JArray.Parse("""[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"mkdir newdir *"},{"ruleContent":""}],"behavior":"allow","destination":"localSettings"},{"type":"addDirectories","directories":["C:\\data"],"destination":"session"}]""");
        ClaudeStreamRules.DescribeAllowForSession("Bash", rules).Should().Be(@"本對話都允許 Bash(mkdir newdir *)、Bash、存取 C:\data");

        var withMode = JArray.Parse("""[{"type":"addDirectories","directories":["C:\\data"],"destination":"session"},{"type":"setMode","mode":"acceptEdits","destination":"session"}]""");
        ClaudeStreamRules.DescribeAllowForSession("Bash", withMode)
            .Should().Be(@"本對話都允許 存取 C:\data，並改為自動接受編輯（檔案修改與 mkdir、rm、mv 等檔案指令不再詢問）", "連權限模式一起切換時要寫出來");

        var many = JArray.Parse("""[{"type":"addRules","rules":[{"toolName":"A"},{"toolName":"B"},{"toolName":"C"},{"toolName":"D"}]}]""");
        ClaudeStreamRules.DescribeAllowForSession("Bash", many).Should().Be("本對話都允許 A、B、C", "最多列三條");

        ClaudeStreamRules.DescribeAllowForSession("Bash", null).Should().Be("本對話同樣的操作都允許");
        ClaudeStreamRules.DescribeAllowForSession("mcp__x__y", new JArray()).Should().Be("本對話同樣的操作都允許");
        ClaudeStreamRules.DescribeAllowForSession("Bash", JArray.Parse("""[{"type":"setMode","mode":"plan"}]""")).Should().Be("本對話改為「規劃模式」");
    }

    [Fact]
    public void RequestKey_工具名稱加輸入JSON_相同請求才相同()
    {
        var a = ClaudeStreamRules.RequestKey("Bash", JObject.Parse("""{"command":"npm test"}"""));
        a.Should().Be("Bash\n{\"command\":\"npm test\"}");
        ClaudeStreamRules.RequestKey("Bash", JObject.Parse("""{"command": "npm test"}""")).Should().Be(a, "空白不影響");
        ClaudeStreamRules.RequestKey("Bash", JObject.Parse("""{"command":"npm run build"}""")).Should().NotBe(a);
        ClaudeStreamRules.RequestKey("PowerShell", JObject.Parse("""{"command":"npm test"}""")).Should().NotBe(a);
        ClaudeStreamRules.RequestKey(null, null).Should().Be("\n{}");
    }

    [Fact]
    public void 拒絕與退回計畫的說明_有附說明就轉告代理()
    {
        ClaudeStreamRules.BuildDenyMessage(null).Should().Contain("使用者拒絕了這個操作").And.Contain("不要改用其他方式繞過");
        ClaudeStreamRules.BuildDenyMessage("  改用 git rm  ").Should().Contain("「改用 git rm」").And.Contain("依照使用者的說明");
        ClaudeStreamRules.BuildKeepPlanningMessage(" ").Should().Contain("繼續規劃").And.Contain("AskUserQuestion");
        ClaudeStreamRules.BuildKeepPlanningMessage("先做測試").Should().Contain("「先做測試」").And.Contain("修改計畫");
    }

    // ───── 工具的顯示資訊 ─────

    [Theory]
    [InlineData("Bash", """{"command":"mkdir newdir","description":"建立資料夾"}""", "執行指令", "bash", "mkdir newdir")]
    [InlineData("PowerShell", """{"command":"Get-ChildItem\nWrite-Host 2"}""", "執行 PowerShell", "bash", "Get-ChildItem")]
    [InlineData("Read", """{"file_path":"C:\\a\\b.txt"}""", "讀取檔案", "read", @"C:\a\b.txt")]
    [InlineData("Read", """{"file_path":"C:\\a\\chart.png"}""", "讀取檔案", "image", @"C:\a\chart.png")]
    [InlineData("Glob", """{"pattern":"**/*.cs","path":"C:\\src"}""", "搜尋檔案", "search", "**/*.cs　C:\\src")]
    [InlineData("Grep", """{"pattern":"TODO","glob":"*.cs"}""", "搜尋內容", "search", "TODO　*.cs")]
    [InlineData("WebFetch", """{"url":"https://example.com","prompt":"摘要"}""", "讀取網頁", "web", "https://example.com")]
    [InlineData("WebSearch", """{"query":"天氣"}""", "網路搜尋", "web", "天氣")]
    [InlineData("Task", """{"description":"找檔案","prompt":"幫我找"}""", "子代理", "agent", "找檔案")]
    [InlineData("Agent", """{"prompt":"幫我找"}""", "子代理", "agent", "子代理工作")]
    [InlineData("Skill", """{"skill":"dataviz","args":"畫圖"}""", "使用技能", "other", "dataviz")]
    [InlineData("ToolSearch", """{"query":"select:Read"}""", "載入工具", "other", "select:Read")]
    [InlineData("ExitPlanMode", """{"plan":"## 計畫"}""", "確認執行計畫", "plan", "代理已規劃好做法，請確認是否照計畫執行")]
    [InlineData("TaskCreate", """{"subject":"寫測試","description":"補上回歸測試"}""", "新增待辦", "todo", "寫測試")]
    [InlineData("TaskUpdate", """{"taskId":"3","status":"in_progress"}""", "更新待辦", "todo", "#3　進行中")]
    [InlineData("TaskGet", """{"taskId":"3"}""", "查看待辦", "todo", "#3")]
    [InlineData("TaskList", """{}""", "列出待辦", "todo", "")]
    [InlineData("mcp__sqlserver__query", """{"sql":"select 1"}""", "MCP：sqlserver", "mcp", "query")]
    [InlineData("SomethingNew", """{"count":3,"target":"x"}""", "SomethingNew", "other", "x")]
    public void DescribeTool_中文名稱種類與摘要(string tool, string inputJson, string displayName, string kind, string title)
    {
        var d = ClaudeStreamRules.DescribeTool(tool, JObject.Parse(inputJson));
        d.DisplayName.Should().Be(displayName);
        d.Kind.Should().Be(kind);
        d.Title.Should().Be(title);
    }

    [Fact]
    public void DescribeTool_Bash的細節含說明與指令()
    {
        var d = ClaudeStreamRules.DescribeTool("Bash", JObject.Parse("""{"command":"npm test","description":"跑測試"}"""));
        d.Detail.Should().Be("跑測試\n\nnpm test");
        d.FilePath.Should().BeNull();
        d.Diff.Should().BeNull();
        ClaudeStreamRules.DescribeTool("Bash", JObject.Parse("""{"command":"npm test"}""")).Detail.Should().Be("npm test");
    }

    [Fact]
    public void DescribeTool_Edit帶檔案路徑與行差異()
    {
        var d = ClaudeStreamRules.DescribeTool("Edit", JObject.Parse("""{"file_path":"C:\\w\\a.cs","old_string":"int a = 1;\nint b = 2;","new_string":"int a = 1;\nint b = 3;"}"""));

        d.DisplayName.Should().Be("修改檔案");
        d.Kind.Should().Be("edit");
        d.Title.Should().Be(@"C:\w\a.cs");
        d.FilePath.Should().Be(@"C:\w\a.cs");
        d.Diff!.Select(l => $"{l.T}:{l.S}").Should().Equal("ctx:int a = 1;", "del:int b = 2;", "add:int b = 3;");
    }

    [Fact]
    public void DescribeTool_Write是整個新檔_MultiEdit每段有標題_NotebookEdit用新內容()
    {
        var write = ClaudeStreamRules.DescribeTool("Write", JObject.Parse("""{"file_path":"C:\\w\\new.txt","content":"第一行\n第二行\n"}"""));
        write.Kind.Should().Be("write");
        write.FilePath.Should().Be(@"C:\w\new.txt");
        write.Diff!.Select(l => $"{l.T}:{l.S}").Should().Equal("add:第一行", "add:第二行");

        var multi = ClaudeStreamRules.DescribeTool("MultiEdit", JObject.Parse("""{"file_path":"C:\\w\\m.cs","edits":[{"old_string":"a","new_string":"b"},{"old_string":"c","new_string":"d"}]}"""));
        multi.Kind.Should().Be("edit");
        multi.Diff!.Select(l => $"{l.T}:{l.S}").Should().Equal("hunk:編輯 1／2", "del:a", "add:b", "hunk:編輯 2／2", "del:c", "add:d");

        var notebook = ClaudeStreamRules.DescribeTool("NotebookEdit", JObject.Parse("""{"notebook_path":"C:\\w\\n.ipynb","new_source":"print(1)"}"""));
        notebook.DisplayName.Should().Be("修改筆記本");
        notebook.FilePath.Should().Be(@"C:\w\n.ipynb");
        notebook.Diff!.Single().Should().Be(new AgentDiffLine("add", "print(1)"));
    }

    [Fact]
    public void DescribeTool_沒有路徑時FilePath為Null_其他工具細節是縮排JSON()
    {
        ClaudeStreamRules.DescribeTool("Read", new JObject()).FilePath.Should().BeNull();
        ClaudeStreamRules.DescribeTool(null, null).DisplayName.Should().Be("工具");
        ClaudeStreamRules.DescribeTool("SomethingNew", new JObject()).Detail.Should().BeNull("沒有輸入就沒有細節");

        var mcp = ClaudeStreamRules.DescribeTool("mcp__github__create_issue", JObject.Parse("""{"title":"bug"}"""));
        mcp.Title.Should().Be("create_issue");
        mcp.Detail.Should().Contain("\"title\": \"bug\"");
    }

    [Fact]
    public void DescribeTool_TodoWrite摘要完成數與進行中項目()
    {
        var todos = JObject.Parse("""{"todos":[{"content":"讀取報表","status":"completed","activeForm":"讀取報表中"},{"content":"彙總","status":"in_progress","activeForm":"彙總中"},{"content":"寄出","status":"pending","activeForm":"寄出中"}]}""");

        var d = ClaudeStreamRules.DescribeTool("TodoWrite", todos);

        d.Kind.Should().Be("todo");
        d.Title.Should().Be("1/3 完成，進行中：彙總中");
        ClaudeStreamRules.DescribeTool("TodoWrite", JObject.Parse("""{"todos":[{"content":"a","status":"completed"}]}""")).Title.Should().Be("1/1 完成");
    }

    [Fact]
    public void ParseTodos_狀態對應_進行中顯示ActiveForm_略過空白項目()
    {
        var todos = JArray.Parse("""[{"content":"讀取報表","status":"completed","activeForm":"讀取報表中"},{"content":"彙總","status":"in_progress","activeForm":"正在彙總"},{"content":"寄出","status":"pending"},{"content":"沒有狀態"},{"content":"進行中但沒有 activeForm","status":"in_progress"},{"content":"","status":"pending"}]""");

        var steps = ClaudeStreamRules.ParseTodos(todos);

        steps.Select(s => $"{s.Status}:{s.Text}").Should().Equal(
            "completed:讀取報表", "inProgress:正在彙總", "pending:寄出", "pending:沒有狀態", "inProgress:進行中但沒有 activeForm");
        ClaudeStreamRules.ParseTodos(null).Should().BeEmpty();
    }

    [Fact]
    public void ExtractToolResultText_字串與區塊陣列_圖片以標記表示()
    {
        ClaudeStreamRules.ExtractToolResultText(null).Should().BeEmpty();
        ClaudeStreamRules.ExtractToolResultText(JValue.CreateNull()).Should().BeEmpty();
        ClaudeStreamRules.ExtractToolResultText(new JValue("純文字")).Should().Be("純文字");
        ClaudeStreamRules.ExtractToolResultText(JArray.Parse("""[{"type":"text","text":"第一段"},{"type":"image","source":{}},{"type":"text","text":"第二段"}]"""))
            .Should().Be("第一段\n[圖片]\n第二段");
    }

    // ───── initialize 回應 ─────

    [Fact]
    public void ParseModels_自Initialize回應_沒有強度清單時為空()
    {
        var models = ClaudeStreamRules.ParseModels(JObject.Parse(RealSamples.ClaudeInitializeResponse));

        models.Select(m => m.Value).Should().Equal("default", "haiku", "claude-haiku-4-5-20251001");
        models[0].Label.Should().Be("Default (recommended)");
        models[0].Description.Should().Be("Opus 5.5");
        models[0].Efforts.Should().Equal("low", "medium", "high", "xhigh", "max");
        models[2].Label.Should().Be("Haiku 4.5");
        models[2].Efforts.Should().BeEmpty();
        ClaudeStreamRules.ParseModels(null).Should().BeEmpty();
    }

    [Fact]
    public void ParseCommands_去掉斜線_略過內部指令與重複與空名稱()
    {
        var commands = ClaudeStreamRules.ParseCommands(JObject.Parse(RealSamples.ClaudeInitializeResponse));

        commands.Select(c => c.Name).Should().Equal("deep-research", "compact", "model");
        commands[1].ArgumentHint.Should().Be("<optional custom summarization instructions>");
        commands[2].Description.Should().Be("Set the AI model for Claude Code");
        ClaudeStreamRules.ParseCommands(null).Should().BeEmpty();
        ClaudeStreamRules.ParseCommands(new JObject()).Should().BeEmpty();
    }

    [Fact]
    public void ParseAccount_取Email與方案_沒有Account時為Null()
    {
        var (email, plan) = ClaudeStreamRules.ParseAccount(JObject.Parse(RealSamples.ClaudeInitializeResponse));
        email.Should().Be("user@example.com");
        plan.Should().Be("Claude Max");

        ClaudeStreamRules.ParseAccount(new JObject()).Should().Be(((string?)null, (string?)null));
        ClaudeStreamRules.ParseAccount(null).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public void DescribeRateLimitRejection_被拒才有提示()
    {
        ClaudeStreamRules.DescribeRateLimitRejection(JObject.Parse("""{"status":"allowed","resetsAt":1791436800}""")).Should().BeNull();
        ClaudeStreamRules.DescribeRateLimitRejection(null).Should().BeNull();
        ClaudeStreamRules.DescribeRateLimitRejection(JObject.Parse("""{"status":"rejected","resetsAt":1791436800}"""))
            .Should().StartWith("Claude 訂閱額度已用完，").And.Contain("重置").And.Contain("換到其他帳戶");
        ClaudeStreamRules.DescribeRateLimitRejection(JObject.Parse("""{"status":"REJECTED","rateLimitType":"seven_day"}"""))
            .Should().Be("Claude 訂閱額度已用完；可以把這個對話換到其他帳戶繼續");
        // 被拒但沒帶類型與重置時間：訂閱帳戶的暫時性 429（「not your usage limit」，2.1.310 實測），不是額度用完
        ClaudeStreamRules.DescribeRateLimitRejection(JObject.Parse("""{"status":"rejected","isUsingOverage":false}""")).Should().BeNull();
    }

    [Fact]
    public void FirstLine與Truncate_截斷規則()
    {
        ClaudeStreamRules.FirstLine("\r\n  \n  第一行  \n第二行").Should().Be("第一行");
        ClaudeStreamRules.FirstLine(null).Should().BeEmpty();
        ClaudeStreamRules.FirstLine(new string('字', 250)).Should().Be(new string('字', 200) + "…");
        ClaudeStreamRules.Truncate(null, 5).Should().BeNull();
        ClaudeStreamRules.Truncate("12345", 5).Should().Be("12345");
        ClaudeStreamRules.Truncate("1234567", 5).Should().StartWith("12345\n…（共 7 字");
    }

    // ───── CLI 熱更新：RESUME 環境變數（只在系統重開時加） ─────

    [Fact]
    public void 環境變數_一律關閉自動更新_RESUME變數只在系統重開時加()
    {
        var normal = ClaudeStreamRules.BuildEnvironmentOverrides("low");
        normal.Should().ContainKey("DISABLE_AUTOUPDATER").WhoseValue.Should().Be("1", "app 啟動的 claude 一律不在背景自動更新（7.2.4 保險）");
        normal.Keys.Should().NotContain(ClaudeStreamRules.ResumeVariables, "不是系統重開：CLI 不能自己接續上一輪");
        ClaudeStreamRules.BuildEnvironmentOverrides("low", " ").Keys.Should().NotContain(ClaudeStreamRules.ResumeVariables, "空白的接續訊息＝不是系統重開");

        var restart = ClaudeStreamRules.BuildEnvironmentOverrides(null, "（系統通知：接續）");
        restart["CLAUDE_CODE_RESUME_INTERRUPTED_TURN"].Should().Be("1");
        restart["CLAUDE_CODE_RESUME_INTERRUPTED_TURN_MAX_AGE_MS"].Should().Be("3600000", "超過一小時不接續（同官方預設與 VS Code）");
        restart["CLAUDE_CODE_RESUME_PROMPT"].Should().Be("（系統通知：接續）");
        restart["CLAUDE_CODE_RESUME_REASON"].Should().Be("myriagent_cli_update").And.MatchRegex("^[a-z_]{1,64}$", "CLI 只收小寫英文與底線");
        restart["DISABLE_AUTOUPDATER"].Should().Be("1");
        restart.Should().NotContainKey("CLAUDE_CODE_EFFORT_LEVEL");
    }

    [Fact]
    public void 環境變數_在帳戶環境剝掉CLAUDE之後才加回_平常啟動連繼承來的RESUME變數也拿掉()
    {
        // 從 Claude Code 的 session 裡啟動 app：父程序的 CLAUDE* 會被 AccountRules 剝掉，RESUME 變數要由 ApplyLaunchEnvironment 明確加回
        Dictionary<string, string?> Inherited() => new(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\Windows",
            ["CLAUDE_CODE_ENTRYPOINT"] = "cli",
            ["CLAUDE_CODE_RESUME_PROMPT"] = "父 session 的值",
            ["CLAUDE_CODE_RESUME_INTERRUPTED_TURN"] = "1",
        };
        var account = new Myriagent.Accounts.AgentAccount { Provider = AgentProvider.Claude, Label = "本機" };

        var restart = Inherited();
        Myriagent.Accounts.AccountRules.ApplyEnvironment(account, restart);
        restart.Keys.Should().NotContain(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase), "帳戶環境先剝掉所有 CLAUDE*");
        ClaudeStreamRules.ApplyLaunchEnvironment(restart, "low", "接續訊息");
        restart["CLAUDE_CODE_RESUME_PROMPT"].Should().Be("接續訊息");
        restart["CLAUDE_CODE_RESUME_INTERRUPTED_TURN"].Should().Be("1");
        restart["CLAUDE_CODE_RESUME_REASON"].Should().Be("myriagent_cli_update");
        restart["CLAUDE_CODE_EFFORT_LEVEL"].Should().Be("low");
        restart["PATH"].Should().Be(@"C:\Windows");

        // 帳戶的 ConfigureEnvironment 沒剝（例如測試替身）也一樣：不是系統重開就不能帶 RESUME 變數
        var normal = Inherited();
        ClaudeStreamRules.ApplyLaunchEnvironment(normal, null, null);
        normal.Keys.Should().NotContain(ClaudeStreamRules.ResumeVariables);
        normal["DISABLE_AUTOUPDATER"].Should().Be("1");
        normal["CLAUDE_CODE_ENTRYPOINT"].Should().Be("cli", "只動 RESUME 變數，其餘交給帳戶環境");
    }

    [Fact]
    public void 接續訊息太長就截斷_不切開代理字組()
    {
        var max = ClaudeStreamRules.MaxResumePromptLength;
        ClaudeStreamRules.TruncateResumePrompt("短訊息").Should().Be("短訊息");
        ClaudeStreamRules.TruncateResumePrompt(new string('字', max + 10)).Should().HaveLength(max);

        var emoji = char.ConvertFromUtf32(0x1F600);
        var straddling = new string('a', max - 1) + emoji + "尾";
        var cut = ClaudeStreamRules.TruncateResumePrompt(straddling);
        cut.Should().Be(new string('a', max - 1), "表情符號的兩個 UTF-16 字元跨過上限：整個拿掉，不留半個");
        ClaudeStreamRules.BuildEnvironmentOverrides(null, new string('x', max * 2))["CLAUDE_CODE_RESUME_PROMPT"].Should().HaveLength(max);
    }

    [Fact]
    public void 停止背景工作的控制請求()
    {
        var json = JObject.Parse(ClaudeStreamRules.BuildStopTaskRequest("stop-3", "bmbx3b5yr"));
        json["type"]!.ToString().Should().Be("control_request");
        json["request_id"]!.ToString().Should().Be("stop-3");
        json["request"]!["subtype"]!.ToString().Should().Be("stop_task");
        json["request"]!["task_id"]!.ToString().Should().Be("bmbx3b5yr");
    }

    // ───── CLI 熱更新：resume_reason 與使用者輪次 ─────

    [Theory]
    [InlineData("""{"type":"result","resume_reason":"interrupted_turn"}""", "interrupted_turn")]
    [InlineData("""{"type":"result","resume_reason":"myriagent_cli_update"}""", "myriagent_cli_update")]
    [InlineData("""{"type":"result","resume_reason":"  "}""", null)]
    [InlineData("""{"type":"result","resume_reason":null}""", null)]
    [InlineData("""{"type":"result","resume_reason":3}""", null)]
    [InlineData("""{"type":"result"}""", null)]
    public void ParseResumeReason_只取非空白的字串(string json, string? expected)
        => ClaudeStreamRules.ParseResumeReason(JObject.Parse(json)).Should().Be(expected);

    [Fact]
    public void 自動接續那一輪的Result算使用者那一輪_孤兒背景工作與背景通知那一輪不算()
    {
        // E-15 實錄（2.1.296）：接續那一輪的 result 沒有 origin，帶 resume_reason；設了 CLAUDE_CODE_RESUME_REASON 時是那個值
        ClaudeStreamRules.IsUserTurnResult(JObject.Parse("""{"type":"result","subtype":"success","num_turns":1,"resume_reason":"interrupted_turn","user_message_uuid":"beecd2a4"}""")).Should().BeTrue();
        ClaudeStreamRules.IsUserTurnResult(JObject.Parse("""{"type":"result","subtype":"success","resume_reason":"myriagent_cli_update"}""")).Should().BeTrue();
        ClaudeStreamRules.IsUserTurnResult(JObject.Parse("""{"type":"result","subtype":"success","origin":{"kind":"task-notification"},"resume_reason":"myriagent_cli_update"}"""))
            .Should().BeTrue("帶 resume_reason＝接續的是使用者那一輪，即使之後的版本加了 origin");
        // E-11 實錄：重開時回報孤兒背景工作（num_turns 0）、背景工作完成時 CLI 自己開的一輪
        ClaudeStreamRules.IsUserTurnResult(JObject.Parse("""{"type":"result","subtype":"success","num_turns":0,"result":"","origin":{"kind":"task-notification"}}""")).Should().BeFalse();
        ClaudeStreamRules.IsUserTurnResult(JObject.Parse("""{"type":"result","subtype":"success","num_turns":1,"origin":{"kind":"task-notification","producer":"session-task","runId":"0mv1jny0c-ac916b89"}}""")).Should().BeFalse();
        ClaudeStreamRules.IsUserTurnResult(JObject.Parse("""{"type":"result","subtype":"success"}""")).Should().BeTrue();

        ClaudeStreamRules.IsAppResume("myriagent_cli_update").Should().BeTrue();
        ClaudeStreamRules.IsAppResume("interrupted_turn").Should().BeFalse("CLI 自己（或別的宿主）接續的");
        ClaudeStreamRules.IsAppResume(null).Should().BeFalse();
    }

    [Fact]
    public void 解析器_接續那一輪的Result帶ResumeReason_CommandLifecycle與Init版本()
    {
        var parser = new ClaudeStreamParser();
        // E-10 實錄（2.1.296）：重開後 command_lifecycle started 比 system/init、initialize 回應都早
        parser.Parse("""{"type":"command_lifecycle","command_uuid":"01f43d9f-9a47-49bd-9c47-c70005cc31e4","state":"started","uuid":"c0ff977d","session_id":"74e97bf9"}""")
            .CommandLifecycleState.Should().Be("started");
        var init = parser.Parse("""{"type":"system","subtype":"init","session_id":"74e97bf9","model":"claude-opus-5-5","permissionMode":"bypassPermissions","apiKeySource":"none","claude_code_version":"2.1.296"}""");
        init.CliVersion.Should().Be("2.1.296");
        init.TurnInit.Should().BeTrue();
        init.Events.OfType<AgentSessionStartedEvent>().Single().Detail.Should().Be("Claude Code 2.1.296");
        parser.Parse("""{"type":"system","subtype":"status","status":"requesting","session_id":"74e97bf9","user_message_uuids":["beecd2a4"]}""")
            .TurnActivity.Should().BeTrue("API 請求已送出：CLI 正在處理一輪");
        parser.Parse("""{"type":"system","subtype":"status","permissionMode":"plan","session_id":"74e97bf9"}""")
            .TurnActivity.Should().BeFalse("只改權限模式不算這一輪的活動");

        var result = parser.Parse("""{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"已執行","resume_reason":"interrupted_turn","user_message_uuid":"beecd2a4","terminal_reason":"completed","stop_reason":"end_turn","session_id":"74e97bf9"}""");
        result.TurnCompleted!.Outcome.Should().Be(AgentTurnOutcome.Completed);
        result.ResumeReason.Should().Be("interrupted_turn");
        result.TurnFromOtherOrigin.Should().BeFalse("接續那一輪就是被打斷的使用者那一輪");

        // 孤兒背景工作的回報（num_turns 0）：不是使用者那一輪，也沒有 resume_reason
        var orphan = parser.Parse("""{"type":"result","subtype":"success","is_error":false,"num_turns":0,"result":"","origin":{"kind":"task-notification"},"session_id":"a384cf06"}""");
        orphan.TurnFromOtherOrigin.Should().BeTrue();
        orphan.ResumeReason.Should().BeNull();
        parser.Parse("""{"type":"system","subtype":"init","session_id":"s","claude_code_version":3}""").CliVersion.Should().BeNull("版本不是字串就不回報");
    }

    // ───── CLI 熱更新：tool_use 生命週期（安全點） ─────

    /// <summary>E-10 實錄（2.1.296，PowerShell 工具執行一次 Add-Content）：content_block_start 到 tool_result</summary>
    private static readonly string[] ToolTurn =
    {
        """{"type":"system","subtype":"init","session_id":"74e97bf9","model":"claude-opus-5-5","permissionMode":"bypassPermissions","apiKeySource":"none","claude_code_version":"2.1.296"}""",
        """{"type":"system","subtype":"status","status":"requesting","session_id":"74e97bf9"}""",
        """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_T1","type":"message","role":"assistant","content":[]}},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_01KRxjMg3oi6pztmGM2s4Cen","name":"PowerShell","input":{},"caller":{"type":"direct"}}},"parent_tool_use_id":null}""",
        """{"type":"assistant","message":{"id":"msg_T1","role":"assistant","content":[{"type":"tool_use","id":"toolu_01KRxjMg3oi6pztmGM2s4Cen","name":"PowerShell","input":{"command":"Add-Content e10.txt x","description":"Append x to e10.txt"}}]},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":40}},"parent_tool_use_id":null}""",
        """{"type":"stream_event","event":{"type":"message_stop"},"parent_tool_use_id":null}""",
        """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01KRxjMg3oi6pztmGM2s4Cen","type":"tool_result","content":"(PowerShell completed with no output)","is_error":false}]},"parent_tool_use_id":null,"session_id":"74e97bf9","tool_use_result":{"stdout":"","stderr":"","interrupted":false,"isImage":false}}""",
        """{"type":"system","subtype":"status","status":"requesting","session_id":"74e97bf9"}""",
    };

    [Fact]
    public void 工具生命週期_從ContentBlockStart到ToolResult都算工具在執行()
    {
        var parser = new ClaudeStreamParser();
        var running = new List<bool>();
        var activity = new List<bool>();
        foreach (var line in ToolTurn)
        {
            var result = parser.Parse(line);
            running.Add(parser.ToolRunning);
            activity.Add(result.TurnActivity);
        }

        running.Should().Equal(false, false, false, true, true, true, true, true, false, false);
        activity.Should().Equal(new[] { false, true, true, true, true, true, true, true, true, true }, "init 本身不算 CLI 接手了這一輪（訊息可能還沒寫進逐字稿）");
        parser.LastToolResultId.Should().Be("toolu_01KRxjMg3oi6pztmGM2s4Cen", "重開前要等逐字稿也寫入這個結果");
        parser.LastStopReason.Should().Be("tool_use", "這一輪還在途中：工具結果之後還有下一則回覆");

        parser.Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_T2"}},"parent_tool_use_id":null}""");
        parser.LastStopReason.Should().BeNull("新訊息開始時清掉");
        parser.Parse("""{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn"}},"parent_tool_use_id":null}""");
        parser.LastStopReason.Should().Be("end_turn");
    }

    [Fact]
    public void StopReason只屬於這一輪_Result之後清掉_下一輪送出後第一則回覆前不會留著上一輪的EndTurn()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_A"}},"parent_tool_use_id":null}""");
        parser.Parse("""{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn"}},"parent_tool_use_id":null}""");
        parser.LastStopReason.Should().Be("end_turn");
        parser.Parse("""{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"ok","session_id":"s"}""");
        parser.LastStopReason.Should().BeNull("一輪結束：上一輪的 end_turn 不能被重開當成「這一輪已回覆完」（備案就不送接續訊息了）");

        // 下一輪：init、requesting（CLI 已接手、還沒有第一個 token）
        parser.Parse("""{"type":"system","subtype":"init","session_id":"s","model":"m","permissionMode":"default","apiKeySource":"none","claude_code_version":"2.1.296"}""");
        parser.Parse("""{"type":"system","subtype":"status","status":"requesting","session_id":"s"}""");
        parser.LastStopReason.Should().BeNull();
        parser.Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_B"}},"parent_tool_use_id":null}""");
        parser.Parse("""{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"tool_use"}},"parent_tool_use_id":null}""");
        parser.LastStopReason.Should().Be("tool_use");

        // CLI 自己開的一輪（背景工作完成的通知）的 result 也清掉
        parser.Parse("""{"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"","origin":{"kind":"task-notification"}}""");
        parser.LastStopReason.Should().BeNull();
    }

    [Fact]
    public void 背景工作對到開始它的工具_立即套用時判斷高風險操作用_工具結果後也留著_工作結束就移除()
    {
        var parser = new ClaudeStreamParser();
        parser.BackgroundTools.Should().BeEmpty();
        // E-11：run_in_background 的指令：assistant 的 tool_use → background_tasks_changed → task_started(tool_use_id) → 工具結果「Command running in background」
        parser.Parse("""{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"tu_pub","name":"Bash","input":{"command":"npm publish --access public","description":"Publish","run_in_background":true}}]},"parent_tool_use_id":null}""");
        parser.Parse("""{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"b1","task_type":"local_bash","description":"Publish"}]}""");
        parser.BackgroundTools.Should().BeEmpty("task_started 之前還不知道是哪個工具");
        parser.Parse("""{"type":"system","subtype":"task_started","task_id":"b1","tool_use_id":"tu_pub","description":"Publish","is_backgrounded":true,"task_type":"local_bash"}""");
        parser.Parse("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"tu_pub","type":"tool_result","content":"Command running in background with ID: b1."}]},"parent_tool_use_id":null}""");
        var tool = parser.BackgroundTools.Should().ContainSingle().Subject;
        tool.Should().Be(new AgentBackgroundTool("執行指令", "bash", "npm publish --access public", "Publish\n\nnpm publish --access public", TaskId: "b1", StopId: "b1"),
            "local_bash 的工作看得到輸出（get_task_output）：帶 task_id；每個背景工作都能以 stop_task 停止");

        // 前景指令跑久了被移到背景：task_started 是前景（is_backgrounded false），之後 background_tasks_changed 列著它
        parser.Parse("""{"type":"assistant","message":{"id":"msg_2","content":[{"type":"tool_use","id":"tu_dev","name":"PowerShell","input":{"command":"npm run dev"}}]},"parent_tool_use_id":null}""");
        parser.Parse("""{"type":"system","subtype":"task_started","task_id":"b2","tool_use_id":"tu_dev","is_backgrounded":false,"task_type":"local_bash"}""");
        parser.BackgroundTools.Should().ContainSingle("還是前景：不算背景工作");
        parser.Parse("""{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"b1"},{"task_id":"b2"},{"task_id":"b3","tool_use_id":"tu_unknown"}]}""");
        parser.BackgroundTools.Select(t => t.Title).Should().Equal(new[] { "npm publish --access public", "npm run dev" }, "依清單順序；對不到工具的不列");

        // 工作結束：不再列
        parser.Parse("""{"type":"system","subtype":"task_notification","task_id":"b1","status":"completed","summary":"done"}""");
        parser.Parse("""{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"b2"}]}""");
        parser.BackgroundTools.Select(t => t.Title).Should().Equal("npm run dev");

        // 讀檔、改檔不是指令：不記
        parser.Parse("""{"type":"assistant","message":{"id":"msg_3","content":[{"type":"tool_use","id":"tu_read","name":"Read","input":{"file_path":"C:\\a.txt"}}]},"parent_tool_use_id":null}""");
        parser.Parse("""{"type":"system","subtype":"task_started","task_id":"b4","tool_use_id":"tu_read","is_backgrounded":true}""");
        parser.BackgroundTools.Select(t => t.Title).Should().Equal("npm run dev");
    }

    [Fact]
    public void 工具生命週期_問卷與計畫確認也算_伺服器端工具與子代理內部的工具不算_Result清掉()
    {
        var parser = new ClaudeStreamParser();
        // 問卷：沒有工具卡，但 tool_use 到 tool_result 之間一樣算（另外由 can_use_tool 算等使用者）
        parser.Parse("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_Q","name":"AskUserQuestion","input":{}}},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeTrue();
        parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"answered","tool_use_id":"toolu_Q"}]},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeFalse("問卷的結果不畫卡片，但工具已經結束");

        // 伺服器端工具（web_search）在 API 那一端執行：重開只會讓 CLI 重送請求
        parser.Parse("""{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{}}},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeFalse();

        // 子代理內部的工具由主對話的 Task 工具涵蓋
        parser.Parse("""{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_sub","name":"Bash","input":{}}},"parent_tool_use_id":"toolu_task"}""");
        parser.Parse("""{"type":"assistant","message":{"id":"msg_sub","content":[{"type":"tool_use","id":"toolu_sub2","name":"Bash","input":{}}]},"parent_tool_use_id":"toolu_task"}""");
        parser.ToolRunning.Should().BeFalse();

        // 主對話的 Task 工具執行中（子代理在做事）；被中止而沒有結果時，result 清掉
        parser.Parse("""{"type":"assistant","message":{"id":"msg_C","content":[{"type":"tool_use","id":"toolu_task","name":"Task","input":{"description":"找檔案","prompt":"找"}}]},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeTrue("沒有串流事件時以完整的 assistant 區塊開始計算");
        parser.Parse("""{"type":"result","subtype":"error_during_execution","is_error":true,"num_turns":1,"errors":[]}""");
        parser.ToolRunning.Should().BeFalse("一輪結束：被中止的工具不會再回報結果");

        // 已經有結果的工具不會因為晚到的 assistant 區塊又算回執行中
        parser.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"ok","tool_use_id":"toolu_late"}]},"parent_tool_use_id":null}""");
        parser.Parse("""{"type":"assistant","message":{"id":"msg_D","content":[{"type":"tool_use","id":"toolu_late","name":"Bash","input":{}}]},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeFalse();
    }

    [Fact]
    public void CanUseTool算這一輪的活動_其他控制請求不算()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"control_request","request_id":"r1","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"ls"},"tool_use_id":"tu_1"}}""")
            .TurnActivity.Should().BeTrue();
        parser.Parse("""{"type":"control_request","request_id":"r2","request":{"subtype":"hook_callback","callback_id":"x"}}""")
            .TurnActivity.Should().BeFalse();
    }

    // ───── CLI 熱更新：背景工作（background_tasks_changed 為準） ─────

    /// <summary>E-11 實錄（2.1.296）：三個 run_in_background 指令；A 自己完成、B 以 stop_task 停掉（task_id 照原樣，路徑已刪）</summary>
    private static readonly string[] BackgroundTurn =
    {
        """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_01Pb29X2BkDzEcPn1Uyp88We","name":"PowerShell","input":{},"caller":{"type":"direct"}}},"parent_tool_use_id":null}""",
        """{"type":"assistant","message":{"id":"msg_011CfsTkVDfgrmrRs6nGe8C3","role":"assistant","content":[{"type":"tool_use","id":"toolu_01Pb29X2BkDzEcPn1Uyp88We","name":"PowerShell","input":{"command":"Start-Sleep 15; Set-Content e11-a.txt a","run_in_background":true}}]},"parent_tool_use_id":null}""",
        """{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"b776pbpka","run_id":"0mv1jny0c-ac916b89","task_type":"local_bash","description":"A"}],"session_id":"a384cf06"}""",
        """{"type":"system","subtype":"task_started","task_id":"b776pbpka","run_id":"0mv1jny0c-ac916b89","tool_use_id":"toolu_01Pb29X2BkDzEcPn1Uyp88We","description":"A","is_backgrounded":true,"task_type":"local_bash","session_id":"a384cf06"}""",
        """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01Pb29X2BkDzEcPn1Uyp88We","type":"tool_result","content":"Command running in background with ID: b776pbpka. You will be notified when it completes.","is_error":false}]},"parent_tool_use_id":null,"tool_use_result":{"stdout":"","stderr":"","interrupted":false,"backgroundTaskId":"b776pbpka"}}""",
        """{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"b776pbpka","run_id":"0mv1jny0c-ac916b89","task_type":"local_bash","description":"A"},{"task_id":"bmbx3b5yr","run_id":"0mv1jnyj2-f6180074","task_type":"local_bash","description":"B"}],"session_id":"a384cf06"}""",
        """{"type":"system","subtype":"task_started","task_id":"bmbx3b5yr","run_id":"0mv1jnyj2-f6180074","tool_use_id":"toolu_013FTFnF7cK3AnayJDYU4zgN","description":"B","is_backgrounded":true,"task_type":"local_bash","session_id":"a384cf06"}""",
        """{"type":"result","subtype":"success","is_error":false,"num_turns":4,"result":"已啟動","stop_reason":"end_turn","session_id":"a384cf06"}""",
        """{"type":"system","subtype":"task_updated","task_id":"b776pbpka","run_id":"0mv1jny0c-ac916b89","patch":{"status":"completed","end_time":1791585374227},"session_id":"a384cf06"}""",
        """{"type":"system","subtype":"task_notification","task_id":"b776pbpka","run_id":"0mv1jny0c-ac916b89","tool_use_id":"toolu_01Pb29X2BkDzEcPn1Uyp88We","status":"completed","summary":"Background command \"A\" completed (exit code 0)","session_id":"a384cf06"}""",
        """{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"bmbx3b5yr","run_id":"0mv1jnyj2-f6180074","task_type":"local_bash","description":"B"}],"session_id":"a384cf06"}""",
        """{"type":"system","subtype":"task_updated","task_id":"bmbx3b5yr","run_id":"0mv1jnyj2-f6180074","patch":{"status":"killed","end_time":1791585376239},"session_id":"a384cf06"}""",
        """{"type":"system","subtype":"task_notification","task_id":"bmbx3b5yr","run_id":"0mv1jnyj2-f6180074","tool_use_id":"toolu_013FTFnF7cK3AnayJDYU4zgN","status":"stopped","summary":"B","session_id":"a384cf06"}""",
        """{"type":"system","subtype":"background_tasks_changed","tasks":[],"session_id":"a384cf06"}""",
    };

    [Fact]
    public void 背景工作_以BackgroundTasksChanged的清單為準_工具結果回來後仍算存活_完成與停止都會拿掉()
    {
        var parser = new ClaudeStreamParser();
        var tasks = new List<string>();
        var running = new List<bool>();
        var notifications = new List<string?>();
        foreach (var line in BackgroundTurn)
        {
            var result = parser.Parse(line);
            tasks.Add(string.Join(",", parser.BackgroundTaskIds));
            running.Add(parser.ToolRunning);
            notifications.Add(result.TaskNotificationStatus);
        }

        tasks.Should().Equal(
            "", "", "b776pbpka", "b776pbpka", "b776pbpka", "b776pbpka,bmbx3b5yr", "b776pbpka,bmbx3b5yr", "b776pbpka,bmbx3b5yr",
            "b776pbpka,bmbx3b5yr", "bmbx3b5yr", "bmbx3b5yr", "bmbx3b5yr", "", "");
        running.Should().Equal(new[] { true, true, true, true, false, false, false, false, false, false, false, false, false, false },
            "背景指令的工具結果「Command running in background」一回來工具就結束了，之後由背景工作接手");
        notifications.Where(n => n != null).Should().Equal("completed", "stopped");
    }

    [Fact]
    public void 背景工作_TaskStarted只在IsBackgrounded時輔助加入_重開後回報的孤兒通知不影響()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"system","subtype":"task_started","task_id":"fg1","tool_use_id":"tu_fg","description":"前景","is_backgrounded":false,"task_type":"local_bash"}""");
        parser.BackgroundTaskIds.Should().BeEmpty("前景指令也送 task_started，但不是背景工作");
        parser.Parse("""{"type":"system","subtype":"task_started","task_id":"bg1","tool_use_id":"tu_bg","description":"背景","is_backgrounded":true,"task_type":"local_bash"}""");
        parser.BackgroundTaskIds.Should().Equal(new[] { "bg1" }, "沒有 background_tasks_changed 的版本也追得到");

        // E-11 實錄：--resume 的新程序對逐字稿裡沒結束的背景工作各送一筆 stopped（這個解析器本來就沒有它們）
        var orphan = parser.Parse("""{"type":"system","subtype":"task_notification","task_id":"bhvmyoom6","tool_use_id":"toolu_01HP8LEkQNZiFCUKzDsnLnos","status":"stopped","output_file":"","summary":"Orphaned by a previous Claude Code process exit and reported in an aggregate summary."}""");
        orphan.TaskNotificationStatus.Should().Be("stopped");
        parser.BackgroundTaskIds.Should().Equal("bg1");
        parser.Parse("""{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"bg2"},{"task_id":""},{"run_id":"x"},{"task_id":"bg2"}]}""");
        parser.BackgroundTaskIds.Should().Equal(new[] { "bg2" }, "取代式；略過沒有 id 的、去重");
    }

    [Fact]
    public void 前景指令結束時的TaskNotification不是背景工作完成_工具結果之後就是安全點()
    {
        // 2.1.296 實測（WP1.5 的 IT-H3 與探針）：跑得久的前景 PowerShell 指令送 task_started(is_backgrounded:false)，
        // 結束時緊接在 tool_result 之前送 task_notification completed，之後 CLI 不會自己開一輪
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_fg","name":"PowerShell","input":{}}},"parent_tool_use_id":null}""");
        parser.Parse("""{"type":"system","subtype":"task_started","task_id":"bfg1","tool_use_id":"toolu_fg","description":"sleep","is_backgrounded":false,"task_type":"local_bash"}""");
        parser.ToolRunning.Should().BeTrue();

        var done = parser.Parse("""{"type":"system","subtype":"task_notification","task_id":"bfg1","tool_use_id":"toolu_fg","status":"completed","summary":"sleep"}""");
        done.TaskNotificationStatus.Should().BeNull("前景指令結束：不等 CLI 自己開的那一輪（否則工具結束後 30 秒都不算安全點，重開被延到這一輪結束）");
        parser.BackgroundTaskIds.Should().BeEmpty();
        parser.Parse("""{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_fg","type":"tool_result","content":"ok","is_error":false}]},"parent_tool_use_id":null}""");
        parser.ToolRunning.Should().BeFalse();

        // 移到背景的指令（background_tasks_changed 列著它）結束時照常是背景工作完成
        parser.Parse("""{"type":"system","subtype":"task_started","task_id":"bmoved","is_backgrounded":false,"task_type":"local_bash"}""");
        parser.Parse("""{"type":"system","subtype":"background_tasks_changed","tasks":[{"task_id":"bmoved"}]}""");
        parser.Parse("""{"type":"system","subtype":"task_notification","task_id":"bmoved","status":"completed"}""").TaskNotificationStatus.Should().Be("completed");
        // 不知道是不是前景的（例如重開前的程序開的）照舊回報
        parser.Parse("""{"type":"system","subtype":"task_notification","task_id":"bunknown","status":"completed"}""").TaskNotificationStatus.Should().Be("completed");
    }

    // ───── CLI 熱更新：重開時串流到一半的東西 ─────

    [Fact]
    public void 重開時_串流到一半的回覆結束為已中斷_收尾過的不動_被中斷的工具卡改為已取消()
    {
        var parser = new ClaudeStreamParser();
        foreach (var line in new[]
        {
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_X"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"先說明"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_stop","index":0},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_run","name":"Bash","input":{}}},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"id":"msg_X","content":[{"type":"tool_use","id":"toolu_run","name":"Bash","input":{"command":"npm test"}}]},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_stop","index":1},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"程序樹是"}},"parent_tool_use_id":null}""",
            """{"type":"stream_event","event":{"type":"content_block_start","index":3,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}""",
        })
            parser.Parse(line);

        var events = parser.InterruptOpenWork();

        events.OfType<AgentTextCompletedEvent>().Select(e => (e.BlockId, e.Text)).Should().Equal(
            ("msg_X:2", "程序樹是\n\n" + ClaudeStreamRules.InterruptedTextNote),
            ("msg_X:3", string.Empty));
        var tool = events.OfType<AgentToolCompletedEvent>().Single();
        tool.ToolId.Should().Be("toolu_run");
        tool.Status.Should().Be(AgentItemStatus.Cancelled);
        tool.Output.Should().Be(ClaudeStreamRules.InterruptedToolOutput);
        parser.ToolRunning.Should().BeFalse();
        parser.InterruptOpenWork().Should().BeEmpty("已經結束過了");
    }

    [Fact]
    public void MarkInterruptedText_接上中斷說明_沒有文字回空字串()
    {
        ClaudeStreamRules.MarkInterruptedText("這是一段 \n").Should().Be("這是一段\n\n" + ClaudeStreamRules.InterruptedTextNote);
        ClaudeStreamRules.MarkInterruptedText(" ").Should().BeEmpty("空的回覆項目由畫面移除");
        ClaudeStreamRules.MarkInterruptedText(null).Should().BeEmpty();
    }

    // ───── CLI 熱更新：沒有自動接續時的備案 ─────

    [Theory]
    [InlineData("end_turn", true)]
    [InlineData("stop_sequence", true)]
    [InlineData("refusal", true)]
    [InlineData("tool_use", false)]
    [InlineData("max_tokens", false)]
    [InlineData("pause_turn", false)]
    [InlineData(null, false)]
    public void IsFinalStopReason_回覆完才算這一輪的結尾(string? stopReason, bool expected)
        => ClaudeStreamRules.IsFinalStopReason(stopReason).Should().Be(expected);

    [Theory]
    [InlineData(false, "tool_use", true, true)]
    [InlineData(false, null, true, true)]
    [InlineData(false, "end_turn", true, false)]
    [InlineData(false, "tool_use", false, false)]
    [InlineData(true, "tool_use", true, false)]
    public void 沒有自動接續時_這一輪還在途中而且逐字稿記下了才改送接續訊息(bool autoResumed, string? lastStopReason, bool recorded, bool expected)
        => ClaudeStreamRules.ShouldSendContinuationFallback(autoResumed, lastStopReason, recorded).Should().Be(expected,
            "回覆完的一輪送接續只會白耗一輪；逐字稿沒有這一輪時代理看不到使用者的訊息");

    // ───── CLI 熱更新：逐字稿 ─────

    [Fact]
    public void TranscriptPath與FindTranscript_推算的位置_名稱過長時找前綴相同的資料夾()
    {
        using var temp = new TempDirectory("transcript-find");
        var config = temp.CreateDirectory("cfg");
        var sessionId = Guid.NewGuid().ToString();
        var work = temp.CreateDirectory("work");

        var expected = ClaudeStreamRules.TranscriptPath(config, sessionId, work);
        expected.Should().Be(Path.Combine(config, "projects", ClaudeStreamRules.ProjectFolderName(work), sessionId + ".jsonl"));
        ClaudeStreamRules.FindTranscript(config, sessionId, work).Should().BeNull("還沒寫過逐字稿");
        temp.WriteFile(Path.GetRelativePath(temp.Root, expected), "{}\n");
        ClaudeStreamRules.FindTranscript(config, sessionId, work).Should().Be(expected);
        ClaudeStreamRules.FindTranscript(config, "not-a-guid", work).Should().BeNull();

        var longWork = @"C:\" + string.Join(@"\", Enumerable.Repeat("folder", 40));
        var hashed = ClaudeStreamRules.ProjectFolderName(longWork)[..200] + "-k3j9x";
        var longTranscript = temp.WriteFile(Path.Combine("cfg", "projects", hashed, sessionId + ".jsonl"), "{}\n");
        ClaudeStreamRules.FindTranscript(config, sessionId, longWork).Should().Be(longTranscript, "CLI 對超過 200 字的名稱截斷後加雜湊，只能比前綴");
    }

    [Fact]
    public void 逐字稿尾段有沒有工具結果_同一行要有ToolUseId與這個Id()
    {
        // E-10 實錄（2.1.296）的逐字稿列：assistant 列也有這個 id，但不是結果
        var assistantRow = """{"parentUuid":"7ba60f24","type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01KRxjMg3oi6pztmGM2s4Cen","name":"PowerShell","input":{"command":"Add-Content e10.txt x"}}]},"uuid":"a1"}""";
        var resultRow = """{"parentUuid":"7ba60f24","isSidechain":false,"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01KRxjMg3oi6pztmGM2s4Cen","type":"tool_result","content":"(PowerShell completed with no output)","is_error":false}]},"uuid":"93132543"}""";
        var id = "toolu_01KRxjMg3oi6pztmGM2s4Cen";

        ClaudeStreamRules.TranscriptTailHasToolResult(System.Text.Encoding.UTF8.GetBytes(assistantRow + "\n"), id).Should().BeFalse("工具還沒有結果");
        ClaudeStreamRules.TranscriptTailHasToolResult(System.Text.Encoding.UTF8.GetBytes(assistantRow + "\n" + resultRow + "\n"), id).Should().BeTrue();
        ClaudeStreamRules.TranscriptTailHasToolResult(System.Text.Encoding.UTF8.GetBytes(assistantRow + "\n" + resultRow), id).Should().BeTrue("最後一行還沒換行也算（尾段從行中間開始也一樣）");
        ClaudeStreamRules.TranscriptTailHasToolResult(System.Text.Encoding.UTF8.GetBytes(resultRow + "\n"), "toolu_other").Should().BeFalse();
        ClaudeStreamRules.TranscriptTailHasToolResult(ReadOnlySpan<byte>.Empty, id).Should().BeFalse();
    }

    [Fact]
    public void NeedsRestart_CLI換版後的執行檔身分不同就重開_身分不明不重開()
    {
        AgentSessionSettings Settings(Myriagent.Cli.CliBinaryIdentity? cli)
            => new("s-1", true, new AgentLauncher(CliIdentities.ClaudeNativeExe, Array.Empty<string>()), "a1", _ => { }, null, null, null,
                AgentPermissionMode.Ask, @"C:\w", @"C:\conv", "說明", true, Cli: cli);

        var old = CliIdentities.Claude("2.1.296", fileId: 1);
        ClaudeAgentSession.NeedsRestart(Settings(old), Settings(CliIdentities.Claude("2.1.297", fileId: 2)))
            .Should().BeTrue("原地換檔：路徑相同，但已經不是同一份檔案");
        ClaudeAgentSession.NeedsRestart(Settings(old), Settings(old with { Version = "2.1.296.0" })).Should().BeFalse("版本來源不同不算換檔");
        ClaudeAgentSession.NeedsRestart(Settings(old), Settings(null)).Should().BeFalse("身分不明不構成重開的理由");
        ClaudeAgentSession.NeedsRestart(Settings(null), Settings(old)).Should().BeFalse();
        ClaudeAgentSession.NeedsRestart(Settings(old), Settings(old) with { RemoteControl = new AgentRemoteControlOptions("x", "cse_1") })
            .Should().BeFalse("Remote Control 的開關以 SetRemoteControlAsync 立即套用，不重開程序");
    }

    // ───── Remote Control ─────

    [Fact]
    public void BuildRemoteControlRequest_開啟帶名稱與接回的Id_關閉只帶enabled()
    {
        var on = JObject.Parse(ClaudeStreamRules.BuildRemoteControlRequest("rc-1", new AgentRemoteControlOptions("  整理報表 ", " cse_01abc ")));
        on["type"]!.ToString().Should().Be("control_request");
        on["request_id"]!.ToString().Should().Be("rc-1");
        on["request"]!.ToString(Newtonsoft.Json.Formatting.None)
            .Should().Be("""{"subtype":"remote_control","enabled":true,"name":"整理報表","reattach_session_id":"cse_01abc"}""");

        var fresh = JObject.Parse(ClaudeStreamRules.BuildRemoteControlRequest("rc-2", new AgentRemoteControlOptions(" ", null)));
        fresh["request"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"subtype":"remote_control","enabled":true}""", "沒有名稱由 CLI 命名、沒有 id 就開新的");

        var off = JObject.Parse(ClaudeStreamRules.BuildRemoteControlRequest("rc-3", null));
        off["request"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"subtype":"remote_control","enabled":false}""");
    }

    [Fact]
    public void BuildRenameSessionRequest_以宿主身分改名_名稱去頭尾截到上限_空白不送()
    {
        var rename = JObject.Parse(ClaudeStreamRules.BuildRenameSessionRequest("rn-1", "  整理報表 ")!);
        rename["type"]!.ToString().Should().Be("control_request");
        rename["request_id"]!.ToString().Should().Be("rn-1");
        rename["request"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"subtype":"rename_session","title":"整理報表","source":"host"}""");

        var longName = JObject.Parse(ClaudeStreamRules.BuildRenameSessionRequest("rn-2", new string('字', 300))!);
        longName["request"]!["title"]!.ToString().Should().HaveLength(ClaudeStreamRules.MaxRemoteControlNameLength);

        ClaudeStreamRules.BuildRenameSessionRequest("rn-3", null).Should().BeNull();
        ClaudeStreamRules.BuildRenameSessionRequest("rn-4", " \t ").Should().BeNull("CLI 回 title must be non-empty");
    }

    [Fact]
    public void RemoteControlName_超過上限截斷且不切開代理字對()
    {
        ClaudeStreamRules.RemoteControlName(null).Should().BeNull();
        ClaudeStreamRules.RemoteControlName(" \t").Should().BeNull();
        ClaudeStreamRules.RemoteControlName(new string('字', 300)).Should().HaveLength(ClaudeStreamRules.MaxRemoteControlNameLength);

        var emoji = new string('a', ClaudeStreamRules.MaxRemoteControlNameLength - 1) + "😀";
        var cut = ClaudeStreamRules.RemoteControlName(emoji)!;
        cut.Should().Be(new string('a', ClaudeStreamRules.MaxRemoteControlNameLength - 1), "表情符號的兩個代理字元剛好跨過上限：整個去掉");
        char.IsHighSurrogate(cut[^1]).Should().BeFalse();
    }

    [Fact]
    public void ParseRemoteControlResponse_取遠端工作階段與網址_欄位不是字串就當成沒有()
    {
        // 2.1.300 實錄（id 換成假的）
        var response = JObject.Parse("""{"session_url":"https://claude.ai/code/session_01abc","connect_url":"https://claude.ai/code?environment=","environment_id":"","bridge_epoch":1,"bridge_session_id":"cse_01abc"}""");
        ClaudeStreamRules.ParseRemoteControlResponse(response).Should().Be(("cse_01abc", "https://claude.ai/code/session_01abc"));

        ClaudeStreamRules.ParseRemoteControlResponse(JObject.Parse("""{"bridge_session_id":null,"session_url":{"x":1}}""")).Should().Be(((string?)null, (string?)null));
        ClaudeStreamRules.ParseRemoteControlResponse(null).Should().Be(((string?)null, (string?)null));
    }

    [Theory]
    [InlineData("ready", null, nameof(ClaudeBridgeChange.Connected))]
    [InlineData("connected", null, nameof(ClaudeBridgeChange.Connected))]
    [InlineData("failed", null, nameof(ClaudeBridgeChange.LostForGood))]
    [InlineData("failed", "terminal", nameof(ClaudeBridgeChange.LostForGood))]
    [InlineData("failed", "transient", nameof(ClaudeBridgeChange.Lost))]
    [InlineData("policy_disabled", null, nameof(ClaudeBridgeChange.LostForGood))]
    [InlineData("reconnecting", null, nameof(ClaudeBridgeChange.None))]
    [InlineData("", null, nameof(ClaudeBridgeChange.None))]
    public void ClassifyBridgeState_連上算開著_不會恢復的斷線要提示(string state, string? failureKind, string expected)
        => ClaudeStreamRules.ClassifyBridgeState(state, failureKind).ToString().Should().Be(expected);

    [Fact]
    public void Remote_Control的提示_帶原因_沒有原因也看得懂()
    {
        ClaudeStreamRules.DescribeRemoteControlFailure("Remote Control is not enabled").Should().StartWith("Remote Control 沒有開啟（Remote Control is not enabled）");
        ClaudeStreamRules.DescribeRemoteControlFailure(null).Should().Contain("CLI 沒有說明原因").And.Contain("設定 → 對話");
        ClaudeStreamRules.DescribeBridgeLost("failed", "network gone").Should().StartWith("Remote Control 連線中斷（network gone）");
        ClaudeStreamRules.DescribeBridgeLost("policy_disabled", null).Should().StartWith("組織政策停用了 Remote Control，");
    }

    // ───── 遠端送來的訊息（--replay-user-messages） ─────

    [Fact]
    public void BuildArguments_回送使用者訊息_遠端送來的訊息才看得到內容()
        => ClaudeStreamRules.BuildArguments(Options()).Should().Contain("--replay-user-messages");

    [Fact]
    public void BuildUserMessage_帶uuid_沒給就不帶()
    {
        JObject.Parse(ClaudeStreamRules.BuildUserMessage("嗨", Array.Empty<(string, string)>(), "0f1e2d3c-0000-4000-8000-000000000001"))["uuid"]!.ToString()
            .Should().Be("0f1e2d3c-0000-4000-8000-000000000001", "CLI 以它回報 command_lifecycle 與 result 的 user_message_uuid");
        JObject.Parse(ClaudeStreamRules.BuildUserMessage("嗨", Array.Empty<(string, string)>()))["uuid"].Should().BeNull();
        JObject.Parse(ClaudeStreamRules.BuildUserMessage("嗨", Array.Empty<(string, string)>(), " "))["uuid"].Should().BeNull();
    }

    [Fact]
    public void ParseRemoteUserMessage_遠端送來的才算_文字接起來且算圖片數()
    {
        // 2.1.301 實錄（uuid 換成假的）：網頁送的訊息回送時帶 origin human
        var remote = JObject.Parse("""{"type":"user","message":{"role":"user","content":"只回覆 OK 兩個字母（遠端）"},"session_id":"s1","parent_tool_use_id":null,"uuid":"r-1","timestamp":"2026-10-16T05:00:00.000Z","isReplay":true,"origin":{"kind":"human"}}""");
        ClaudeStreamRules.ParseRemoteUserMessage(remote).Should().Be(new ClaudeRemoteUserMessage("r-1", "只回覆 OK 兩個字母（遠端）", 0, 0));

        var blocks = JObject.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":"image/png","data":"x"}},{"type":"text","text":"這是什麼"},{"type":"text","text":"第二段"},{"type":"image","source":{}}]},"uuid":"r-2","isReplay":true,"origin":{"kind":"human"}}""");
        ClaudeStreamRules.ParseRemoteUserMessage(blocks).Should().Be(new ClaudeRemoteUserMessage("r-2", "這是什麼\n第二段", 2, 0));

        // 文件之類的其他區塊只算數量（不然畫面上是空白的氣泡）；text 欄位不是字串的文字區塊不算檔案
        var files = JObject.Parse("""{"type":"user","message":{"role":"user","content":[{"type":"document","source":{"type":"base64","media_type":"application/pdf","data":"x"}},{"type":"text","text":null},{"type":"x-unknown"}]},"uuid":"r-3","isReplay":true,"origin":{"kind":"human"}}""");
        ClaudeStreamRules.ParseRemoteUserMessage(files).Should().Be(new ClaudeRemoteUserMessage("r-3", string.Empty, 0, 2));
    }

    [Theory]
    [InlineData("""{"role":"user","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}""", true, "2.1.301 實錄：工具執行中在手機上按停止")]
    [InlineData("""{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}""", true, "沒有工具在跑時停止")]
    [InlineData("""{"role":"user","content":"[Request interrupted by user]"}""", true, "內容是字串")]
    [InlineData("""{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"[Request interrupted by user]"}]}""", false, "工具結果的內容不算")]
    [InlineData("""{"role":"user","content":"please stop: [Request interrupted by user]"}""", false, "要在開頭")]
    [InlineData("""{"role":"user","content":[{"type":"text","text":null}]}""", false, "text 不是字串")]
    [InlineData("""{"role":"user"}""", false, "沒有內容")]
    public void IsInterruptMarker_只認CLI停止時插進的那則(string json, bool expected, string because)
        => ClaudeStreamRules.IsInterruptMarker(JObject.Parse(json)).Should().Be(expected, because);

    [Theory]
    [InlineData("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"app 自己送的"}]},"uuid":"u-1","isReplay":true}""", "app 經 stdin 送的沒有 origin")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"Another Claude session sent a message"},"uuid":"u-2","isReplay":true,"isSynthetic":true,"origin":{"kind":"peer","from":"unknown"}}""", "其他工作階段送來的是 CLI 包裝過的 peer 訊息")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"x"},"uuid":"u-3","isReplay":true,"isSynthetic":true,"origin":{"kind":"human"}}""", "CLI 插入的訊息")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"x"},"uuid":"u-4","isReplay":true,"origin":{"kind":"task-notification"}}""", "背景工作通知")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"x"},"uuid":"u-5","origin":{"kind":"human"}}""", "不是回送")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"x"},"uuid":"u-6","isReplay":"true","origin":{"kind":"human"}}""", "isReplay 不是布林")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"x"},"isReplay":true,"origin":{"kind":"human"}}""", "沒有 uuid")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"x"},"uuid":"u-8","isReplay":true,"origin":"human"}""", "origin 不是物件")]
    public void ParseRemoteUserMessage_不是遠端使用者送的都回null(string json, string because)
        => ClaudeStreamRules.ParseRemoteUserMessage(JObject.Parse(json)).Should().BeNull(because);

    [Theory]
    [InlineData("u", false, "r", true, "遠端那則先開始、app 這則還沒開始：先到的 result 是遠端那一輪的")]
    [InlineData("u", false, "R", true, "uuid 大小寫不同也是同一則")]
    [InlineData("u", true, "r", false, "app 這則已經開始（在工具結果之後併進遠端那一輪）：這個 result 也是這一輪的")]
    [InlineData("u", false, "u", false, "就是這一則")]
    [InlineData("u", false, "x", false, "沒看過的 uuid：寧可結束這一輪，不要一直卡在執行中")]
    [InlineData("u", false, null, false, "舊版 CLI 沒有 user_message_uuid")]
    [InlineData(null, false, "r", false, "這一輪的訊息沒有 uuid（自動接續）")]
    public void IsResultForOtherTurn_只在能確定時才算另一則訊息的(string? turnUuid, bool started, string? resultUuid, bool expected, string because)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "r", "u" };
        ClaudeStreamRules.IsResultForOtherTurn(turnUuid, started, resultUuid, seen).Should().Be(expected, because);
    }
}
