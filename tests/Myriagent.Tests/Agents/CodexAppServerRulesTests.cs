using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>Codex app-server（JSON-RPC over stdio）協定（codex-cli 0.160.1 探針實錄）</summary>
public class CodexAppServerRulesTests
{
    [Fact]
    public void 啟動參數_功能開關用設定覆寫而不是Enable旗標()
    {
        // 實測 --enable 遇到不認得的功能名稱直接 exit 1；-c features.X 只警告
        var args = CodexAppServerRules.BuildServerArguments(useLocalCustomizations: true, new[] { "node_repl" });

        args[0].Should().Be("app-server");
        args.Should().ContainInOrder("-c", "features.default_mode_request_user_input=true");
        args.Should().ContainInOrder("-c", "suppress_unstable_features_warning=true");
        args.Should().NotContain("--enable");
        args.Should().NotContain("features.plugins=false");
        args.Should().NotContain(a => a.StartsWith("mcp_servers", StringComparison.Ordinal), "載入使用者客製設定時不停用 MCP");
    }

    [Fact]
    public void 啟動參數_精簡模式停用外掛連接器與每個MCP伺服器()
    {
        var args = CodexAppServerRules.BuildServerArguments(useLocalCustomizations: false,
            new[] { "node_repl", "my-slack", "bad name", "docs.example", "org/tool", "team:helper@v2", "node_repl", "quote\"name" });

        args.Should().ContainInOrder("-c", "features.plugins=false");
        args.Should().ContainInOrder("-c", "features.apps=false");
        // -c 的鍵只用「.」切開，名稱含點就拆錯；改成一個行內表（值照 TOML 解析，鍵可以加引號），疊在使用者設定上只改 enabled
        args.Should().ContainInOrder("-c",
            "mcp_servers={\"node_repl\"={enabled=false},\"my-slack\"={enabled=false},\"docs.example\"={enabled=false},\"org/tool\"={enabled=false},\"team:helper@v2\"={enabled=false}}");
        args.Should().ContainSingle(a => a.StartsWith("mcp_servers", StringComparison.Ordinal),
            "多個 -c mcp_servers={…} 只有最後一個有效，所有名稱要放在同一個行內表");
        args.Should().NotContain(a => a.Contains("bad name") || a.Contains("quote"), "Codex 不接受的名稱（空白、引號）本來就啟動不了，也不能安全地放進 TOML 字串");
    }

    [Fact]
    public void 啟動參數_自動壓縮上限以設定覆寫帶入_精簡模式也帶()
    {
        CodexAppServerRules.BuildServerArguments(useLocalCustomizations: true, Array.Empty<string>(), autoCompactTokens: 400_000)
            .Should().ContainInOrder("-c", "model_auto_compact_token_limit=400000");
        CodexAppServerRules.BuildServerArguments(useLocalCustomizations: false, Array.Empty<string>(), autoCompactTokens: 150_000)
            .Should().ContainInOrder("-c", "model_auto_compact_token_limit=150000");
        CodexAppServerRules.BuildServerArguments(useLocalCustomizations: true, Array.Empty<string>())
            .Should().NotContain(a => a.StartsWith("model_auto_compact", StringComparison.Ordinal));
    }

    [Fact]
    public void 啟動參數_共用的MCP伺服器逐一以設定覆寫帶入_精簡模式不帶()
    {
        var shared = new[] { "mcp_servers.node_repl.command=\"node\"", "mcp_servers.node_repl.env.FOO=\"bar\"" };

        CodexAppServerRules.BuildServerArguments(useLocalCustomizations: true, Array.Empty<string>(), shared)
            .Should().ContainInOrder("-c", shared[0], "-c", shared[1]);
        CodexAppServerRules.BuildServerArguments(useLocalCustomizations: false, Array.Empty<string>(), shared)
            .Should().NotContain(shared[0]).And.NotContain(shared[1]);
    }

    [Fact]
    public void 啟動參數_精簡模式沒有MCP伺服器時不加MCP參數()
        => CodexAppServerRules.BuildServerArguments(useLocalCustomizations: false, Array.Empty<string>())
            .Should().NotContain(a => a.StartsWith("mcp_servers", StringComparison.Ordinal));

    [Theory]
    [InlineData("ready", nameof(CodexSandboxReadiness.Ready))]
    [InlineData("notConfigured", nameof(CodexSandboxReadiness.NotConfigured))]
    [InlineData("updateRequired", nameof(CodexSandboxReadiness.UpdateRequired))]
    [InlineData("somethingNew", nameof(CodexSandboxReadiness.Unknown))]
    [InlineData(null, nameof(CodexSandboxReadiness.Unknown))]
    public void 沙箱狀態_只有從未設定才自動設定(string? status, string expected)
    {
        // updateRequired 只出現在使用者設過 elevated 沙箱的電腦；自動跑 unelevated 設定會把 config.toml 的 windows.sandbox 改掉
        CodexAppServerRules.ClassifySandboxReadiness(status).Should().Be(Enum.Parse<CodexSandboxReadiness>(expected));
    }

    [Theory]
    [InlineData("thread 01a121f0-8243-77d2-aec9-5a96e7f855a3 already has an active writer", nameof(CodexResumeFailure.ActiveWriter))]
    [InlineData("no rollout found for thread id 01a121f0-8243-77d2-aec9-5a96e7f855a3", nameof(CodexResumeFailure.NotFound))]
    [InlineData("thread not found: 01a121f0", nameof(CodexResumeFailure.NotFound))]
    [InlineData("thread/resume 逾時", nameof(CodexResumeFailure.Other))]
    [InlineData(null, nameof(CodexResumeFailure.Other))]
    public void 續接失敗的分類(string? error, string expected)
        => CodexAppServerRules.ClassifyResumeError(error).Should().Be(Enum.Parse<CodexResumeFailure>(expected));

    [Fact]
    public void 子代理的Thread_帶別的ThreadId才算_不知道主對話時一律算主對話()
    {
        var child = JObject.Parse("""{"threadId":"child","turn":{"id":"c1"}}""");
        CodexAppServerRules.IsOtherThread(child, "main").Should().BeTrue();
        CodexAppServerRules.IsOtherThread(JObject.Parse("""{"threadId":"main"}"""), "main").Should().BeFalse();
        CodexAppServerRules.IsOtherThread(child, null).Should().BeFalse("還沒建立對話時沒有子代理");
        CodexAppServerRules.IsOtherThread(JObject.Parse("""{"rateLimits":{}}"""), "main").Should().BeFalse("帳戶層級的通知沒有 threadId");
        CodexAppServerRules.IsOtherThread(JObject.Parse("""{"threadId":null}"""), "main").Should().BeFalse();
        CodexAppServerRules.IsOtherThread(JObject.Parse("""{"threadId":{"x":1}}"""), "main").Should().BeFalse();
    }

    [Fact]
    public void 本對話都允許的比對鍵_網路存取用主機_指令含種類工作資料夾與額外權限()
    {
        // 實際格式：有 networkApprovalContext 時 command／cwd／commandActions 全是 null
        static JObject Network(string host, string protocol = "https") => JObject.Parse(
            $$"""{"kind":"command","threadId":"t","turnId":"u","itemId":"net-1","reason":"{{host}} is not in the allowed_domains","networkApprovalContext":{"host":"{{host}}","protocol":"{{protocol}}"},"command":null,"cwd":null,"commandActions":null,"additionalPermissions":null,"availableDecisions":["accept","acceptForSession","cancel"]}""");
        static JObject Command(string command, string cwd = "C:\\\\w", string kind = "command", string extra = "null") => JObject.Parse(
            $$"""{"kind":"{{kind}}","threadId":"t","turnId":"u","itemId":"e1","command":"{{command}}","cwd":"{{cwd}}","commandActions":[{"type":"unknown","command":"echo a"}],"additionalPermissions":{{extra}}}""");
        const string Method = "item/commandExecution/requestApproval";

        var api = CodexAppServerRules.BuildSessionApprovalKey(Method, Network("api.example.com"));
        api.Should().NotBeNull().And.Contain("api.example.com");
        CodexAppServerRules.BuildSessionApprovalKey(Method, Network("evil.example.net")).Should().NotBe(api, "不同主機不能共用一次「本對話都允許」");
        CodexAppServerRules.BuildSessionApprovalKey(Method, Network("api.example.com", "http")).Should().NotBe(api);
        CodexAppServerRules.BuildSessionApprovalKey(Method, Network("api.example.com")).Should().Be(api);

        var plain = CodexAppServerRules.BuildSessionApprovalKey(Method, Command("pwsh -Command 'echo a'"));
        plain.Should().NotBeNull();
        CodexAppServerRules.BuildSessionApprovalKey(Method, Command("pwsh -Command 'echo a; rm x'")).Should().NotBe(plain,
            "用原始指令比對：commandActions 只是給畫面看的摘要，不同指令可能摘要相同");
        CodexAppServerRules.BuildSessionApprovalKey(Method, Command("pwsh -Command 'echo a'", cwd: "C:\\\\other")).Should().NotBe(plain);
        CodexAppServerRules.BuildSessionApprovalKey(Method, Command("pwsh -Command 'echo a'", kind: "writeStdin")).Should().NotBe(plain);
        CodexAppServerRules.BuildSessionApprovalKey(Method, Command("pwsh -Command 'echo a'", extra: """{"network":{"enabled":true}}""")).Should().NotBe(plain,
            "同一個指令要求額外權限時 Codex 會刻意再問，宿主不能拿之前的同意自動放行");
        CodexAppServerRules.BuildSessionApprovalKey(Method, Command("pwsh -Command 'echo a'")).Should().Be(plain);

        CodexAppServerRules.BuildSessionApprovalKey(Method, JObject.Parse("""{"command":null,"cwd":null}""")).Should().BeNull("沒有指令也沒有主機就不記，只交給 Codex 自己記");

        var legacy = CodexAppServerRules.BuildSessionApprovalKey("execCommandApproval", JObject.Parse("""{"command":["a b"],"cwd":"C:\\w"}"""));
        legacy.Should().NotBeNull();
        CodexAppServerRules.BuildSessionApprovalKey("execCommandApproval", JObject.Parse("""{"command":["a","b"],"cwd":"C:\\w"}""")).Should().NotBe(legacy);

        var permissions = CodexAppServerRules.BuildSessionApprovalKey("item/permissions/requestApproval", JObject.Parse("""{"permissions":{"network":{"enabled":true}}}"""));
        permissions.Should().NotBeNull();
        CodexAppServerRules.BuildSessionApprovalKey("item/permissions/requestApproval", JObject.Parse("""{"permissions":{"fileSystem":{"write":["C:\\x"]}}}""")).Should().NotBe(permissions);
        CodexAppServerRules.BuildSessionApprovalKey("item/fileChange/requestApproval", new JObject()).Should().BeNull("檔案修改整類放行，不用比對鍵");
    }

    [Fact]
    public void MCP工具核准_由Elicitation的Meta辨認_顯示工具與參數()
    {
        // codex 0.160.1 預設開 tool_call_mcp_elicitation：MCP 工具核准改走 mcpServer/elicitation/request（表單是空的、_meta 標明種類）
        var p = JObject.Parse("""
            {"threadId":"t","turnId":"u","serverName":"github","mode":"form",
             "_meta":{"codex_approval_kind":"mcp_tool_call","persist":"session","tool_title":"建立 issue","tool_description":"在 repo 建立 issue","tool_params":{"title":"bug"}},
             "message":"Allow the github MCP server to run tool \"create_issue\"?","requestedSchema":{"type":"object","properties":{}}}
            """);

        var approval = CodexAppServerRules.ParseMcpToolApproval(p);

        approval.Should().NotBeNull();
        approval!.ServerName.Should().Be("github");
        approval.Title.Should().Be("建立 issue");
        approval.Detail.Should().Contain("create_issue").And.Contain("在 repo 建立 issue").And.Contain("\"title\": \"bug\"");
        approval.CanAllowForSession.Should().BeTrue();

        var both = (JObject)p.DeepClone();
        both["_meta"]!["persist"] = new JArray("session", "always");
        CodexAppServerRules.ParseMcpToolApproval(both)!.CanAllowForSession.Should().BeTrue("persist 也可能是陣列");

        var once = (JObject)p.DeepClone();
        ((JObject)once["_meta"]!).Remove("persist");
        ((JObject)once["_meta"]!).Remove("tool_title");
        var onceApproval = CodexAppServerRules.ParseMcpToolApproval(once)!;
        onceApproval.CanAllowForSession.Should().BeFalse("沒有 persist 就只能允許這一次");
        onceApproval.Title.Should().Be("Allow the github MCP server to run tool \"create_issue\"?", "沒有工具標題時用請求的說明");

        CodexAppServerRules.ParseMcpToolApproval(JObject.Parse("""{"serverName":"s","mode":"form","_meta":null,"message":"請填寫","requestedSchema":{"type":"object","properties":{"name":{"type":"string"}}}}"""))
            .Should().BeNull("真正的表單不是核准");
        CodexAppServerRules.ParseMcpToolApproval(JObject.Parse("""{"serverName":"s","_meta":{"codex_approval_kind":"browser_auth"}}""")).Should().BeNull();
    }

    [Fact]
    public void MCP工具核准的回應_允許與本對話都允許都是Accept_拒絕是Decline()
    {
        var allow = CodexAppServerRules.BuildMcpElicitationResponse(AgentApprovalChoice.Allow);
        allow["action"]!.ToString().Should().Be("accept");
        allow["_meta"]!.Type.Should().Be(JTokenType.Null);

        var session = CodexAppServerRules.BuildMcpElicitationResponse(AgentApprovalChoice.AllowForSession);
        session["action"]!.ToString().Should().Be("accept");
        session["_meta"]!["persist"]!.ToString().Should().Be("session", "Codex 自己記住本對話都允許（不用 always，那會寫進使用者設定）");

        var deny = CodexAppServerRules.BuildMcpElicitationResponse(AgentApprovalChoice.Deny);
        deny["action"]!.ToString().Should().Be("decline", "decline＝使用者拒絕這次呼叫，模型會收到拒絕並繼續；cancel 會中止整個請求");
        deny["content"]!.Type.Should().Be(JTokenType.Null);
    }

    [Theory]
    [InlineData("form", "要求填寫表單")]
    [InlineData("openai/form", "要求填寫表單")]
    [InlineData("url", "要求開啟網址")]
    [InlineData("openai/userVerification", "要求驗證身分")]
    [InlineData("future", "送來不支援的請求")]
    public void 不支援的MCP請求_提示說明是哪一種請求(string mode, string expected)
    {
        var text = CodexAppServerRules.DescribeUnsupportedElicitation(JObject.Parse($$"""{"serverName":"docs","mode":"{{mode}}","message":"請輸入帳號"}"""));
        text.Should().Contain("「docs」").And.Contain(expected).And.Contain("已婉拒");
    }

    [Theory]
    [InlineData(nameof(AgentPermissionMode.Ask), true, "untrusted", "workspace-write", false)]
    [InlineData(nameof(AgentPermissionMode.AcceptEdits), true, "on-request", "workspace-write", false)]
    [InlineData(nameof(AgentPermissionMode.Plan), true, "untrusted", "workspace-write", false)]
    [InlineData(nameof(AgentPermissionMode.Auto), true, "on-request", "workspace-write", false)]
    [InlineData(nameof(AgentPermissionMode.FullAccess), true, "never", "danger-full-access", false)]
    [InlineData(nameof(AgentPermissionMode.FullAccess), false, "never", "danger-full-access", false)]
    [InlineData(nameof(AgentPermissionMode.Ask), false, "untrusted", "danger-full-access", true)]
    [InlineData(nameof(AgentPermissionMode.AcceptEdits), false, "untrusted", "danger-full-access", true)]
    [InlineData(nameof(AgentPermissionMode.Plan), false, "untrusted", "danger-full-access", true)]
    [InlineData(nameof(AgentPermissionMode.Auto), false, "untrusted", "danger-full-access", true)]
    public void GetPolicy_五種模式對應_沙箱未就緒時改成每步詢問(string mode, bool sandboxReady, string approval, string sandbox, bool hasNotice)
    {
        var policy = CodexAppServerRules.GetPolicy(Enum.Parse<AgentPermissionMode>(mode), sandboxReady);
        policy.ApprovalPolicy.Should().Be(approval);
        policy.Sandbox.Should().Be(sandbox);
        (policy.FallbackNotice != null).Should().Be(hasNotice);
        if (hasNotice)
            policy.FallbackNotice.Should().Contain("沙箱尚未設定完成");
    }

    [Fact]
    public void 沙箱政策_三種形狀()
    {
        var write = CodexAppServerRules.BuildSandboxPolicy("workspace-write", new[] { @"C:\conv", " " });
        write["type"]!.ToString().Should().Be("workspaceWrite");
        write["writableRoots"]!.Values<string>().Should().Equal(@"C:\conv");
        write["networkAccess"]!.Value<bool>().Should().BeTrue();

        var readOnly = CodexAppServerRules.BuildSandboxPolicy("read-only", Array.Empty<string>());
        readOnly["type"]!.ToString().Should().Be("readOnly");

        CodexAppServerRules.BuildSandboxPolicy("danger-full-access", Array.Empty<string>())["type"]!.ToString().Should().Be("dangerFullAccess");
    }

    [Fact]
    public void TurnStart_文字加本機圖片_帶政策模型與強度()
    {
        var policy = CodexAppServerRules.GetPolicy(AgentPermissionMode.Ask, true);
        var p = CodexAppServerRules.BuildTurnStartParams("thread-1", "看圖", new[] { @"C:\a\1.png" }, policy, new[] { @"C:\conv" }, "gpt-6.1-sol", "high");

        p["threadId"]!.ToString().Should().Be("thread-1");
        var input = (JArray)p["input"]!;
        input[0]!["type"]!.ToString().Should().Be("text");
        input[0]!["text"]!.ToString().Should().Be("看圖");
        input[0]!["text_elements"]!.Should().BeOfType<JArray>();
        input[1]!["type"]!.ToString().Should().Be("localImage");
        input[1]!["path"]!.ToString().Should().Be(@"C:\a\1.png");
        p["approvalPolicy"]!.ToString().Should().Be("untrusted");
        p["sandboxPolicy"]!["type"]!.ToString().Should().Be("workspaceWrite");
        p["sandboxPolicy"]!["networkAccess"]!.Value<bool>().Should().BeTrue();
        p["sandboxPolicy"]!["writableRoots"]!.Values<string>().Should().Equal(@"C:\conv");
        p["model"]!.ToString().Should().Be("gpt-6.1-sol");
        p["effort"]!.ToString().Should().Be("high");

        var full = CodexAppServerRules.BuildTurnStartParams("t", "x", Array.Empty<string>(), CodexAppServerRules.GetPolicy(AgentPermissionMode.FullAccess, true), Array.Empty<string>(), null, " ");
        full["sandboxPolicy"]!["type"]!.ToString().Should().Be("dangerFullAccess");
        full["model"].Should().BeNull();
        full["effort"].Should().BeNull();
    }

    [Fact]
    public void TurnSteer_帶這一輪的id與同樣格式的輸入_不帶政策與模型()
    {
        var p = CodexAppServerRules.BuildTurnSteerParams("thread-1", "turn-9", "補充", new[] { @"C:\a\2.png" });

        p.Properties().Select(x => x.Name).Should().BeEquivalentTo(new[] { "threadId", "expectedTurnId", "input" },
            "權限、沙箱、模型沿用這一輪的（0.163.0 的 TurnSteerParams 只收 threadId、expectedTurnId、input、clientUserMessageId）");
        p["threadId"]!.ToString().Should().Be("thread-1");
        p["expectedTurnId"]!.ToString().Should().Be("turn-9");
        var input = (JArray)p["input"]!;
        input[0]!["type"]!.ToString().Should().Be("text");
        input[0]!["text"]!.ToString().Should().Be("補充");
        input[0]!["text_elements"]!.Should().BeOfType<JArray>();
        input[1]!["type"]!.ToString().Should().Be("localImage");
        input[1]!["path"]!.ToString().Should().Be(@"C:\a\2.png");
    }

    [Fact]
    public void ThreadStart與Resume的參數()
    {
        var policy = CodexAppServerRules.GetPolicy(AgentPermissionMode.Auto, true);
        var start = CodexAppServerRules.BuildThreadStartParams(@"C:\w", null, policy, "說明");
        start["cwd"]!.ToString().Should().Be(@"C:\w");
        start["approvalPolicy"]!.ToString().Should().Be("on-request");
        start["sandbox"]!.ToString().Should().Be("workspace-write");
        start["developerInstructions"]!.ToString().Should().Be("說明");
        start["config"]!["sandbox_workspace_write.network_access"]!.Value<bool>().Should().BeTrue();
        start["model"].Should().BeNull();
        CodexAppServerRules.BuildThreadStartParams(@"C:\w", "gpt-6-luna", policy, null)["developerInstructions"].Should().BeNull();

        var resume = CodexAppServerRules.BuildThreadResumeParams("t-1", @"C:\w", "gpt-6-astra", policy);
        resume["threadId"]!.ToString().Should().Be("t-1");
        resume["excludeTurns"]!.Value<bool>().Should().BeTrue();
        resume["model"]!.ToString().Should().Be("gpt-6-astra");
        resume["sandbox"]!.ToString().Should().Be("workspace-write");
    }

    [Fact]
    public void 請求與通知_沒有JsonRpc欄位()
    {
        var line = CodexAppServerRules.BuildRequest(7, "turn/interrupt", new JObject { ["threadId"] = "t", ["turnId"] = "u" });
        var json = JObject.Parse(line);
        json["id"]!.Value<long>().Should().Be(7);
        json["method"]!.ToString().Should().Be("turn/interrupt");
        json["jsonrpc"].Should().BeNull();
        JObject.Parse(CodexAppServerRules.BuildRequest(8, "account/rateLimits/read", null))["params"].Should().BeNull();
        JObject.Parse(CodexAppServerRules.BuildNotification("initialized"))["params"].Should().BeNull();

        var result = JObject.Parse(CodexAppServerRules.BuildResult(new JValue("abc"), new JObject { ["decision"] = "accept" }));
        result["id"]!.ToString().Should().Be("abc");
        result["result"]!["decision"]!.ToString().Should().Be("accept");
        var error = JObject.Parse(CodexAppServerRules.BuildError(new JValue(5), -32601, "不支援"));
        error["error"]!["code"]!.Value<int>().Should().Be(-32601);
        error["error"]!["message"]!.ToString().Should().Be("不支援");
    }

    [Fact]
    public void Initialize參數_帶用戶端資訊與實驗功能()
    {
        var p = CodexAppServerRules.BuildInitializeParams("0.1.0");
        p["clientInfo"]!["name"]!.ToString().Should().Be("myriagent");
        p["clientInfo"]!["version"]!.ToString().Should().Be("0.1.0");
        p["capabilities"]!["experimentalApi"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public void ChooseDecision_實測的可選決策沒有Decline()
    {
        // 實測 availableDecisions：["accept", {"acceptWithExecpolicyAmendment":…}, "cancel"]
        var measured = JArray.Parse("""["accept",{"acceptWithExecpolicyAmendment":{"execpolicy_amendment":["mkdir","newdir"]}},"cancel"]""");
        CodexAppServerRules.ChooseDecision(measured, AgentApprovalChoice.Allow).ToString().Should().Be("accept");
        CodexAppServerRules.ChooseDecision(measured, AgentApprovalChoice.AllowForSession).ToString().Should().Be("accept", "沒有 acceptForSession 時由宿主自己記住、之後自動放行；絕不用會寫入設定的 execpolicy");
        CodexAppServerRules.ChooseDecision(measured, AgentApprovalChoice.Deny).ToString().Should().Be("cancel");

        var full = JArray.Parse("""["accept","acceptForSession","decline","cancel"]""");
        CodexAppServerRules.ChooseDecision(full, AgentApprovalChoice.AllowForSession).ToString().Should().Be("acceptForSession");
        CodexAppServerRules.ChooseDecision(full, AgentApprovalChoice.Deny).ToString().Should().Be("decline");

        CodexAppServerRules.ChooseDecision(null, AgentApprovalChoice.Deny).ToString().Should().Be("decline", "改檔核准不帶 availableDecisions，用 v2 的標準值");
        CodexAppServerRules.ChooseDecision(new JArray(), AgentApprovalChoice.AllowForSession).ToString().Should().Be("acceptForSession");
    }

    [Fact]
    public void 舊版核准決策()
    {
        CodexAppServerRules.ChooseLegacyDecision(AgentApprovalChoice.Allow).ToString().Should().Be("approved");
        CodexAppServerRules.ChooseLegacyDecision(AgentApprovalChoice.AllowForSession).ToString().Should().Be("approved_for_session");
        CodexAppServerRules.ChooseLegacyDecision(AgentApprovalChoice.Deny)["denied"]!["rejection"]!.ToString().Should().Be("使用者拒絕");
    }

    [Fact]
    public void 權限請求的回應_允許照請求給_拒絕什麼都不給()
    {
        var request = JObject.Parse("""{"permissions":{"network":{"enabled":true},"fileSystem":null},"reason":"要下載"}""");
        var allow = CodexAppServerRules.BuildPermissionsResponse(request, AgentApprovalChoice.AllowForSession);
        allow["permissions"]!["network"]!["enabled"]!.Value<bool>().Should().BeTrue();
        allow["permissions"]!["fileSystem"].Should().BeNull();
        allow["scope"]!.ToString().Should().Be("session");
        CodexAppServerRules.BuildPermissionsResponse(request, AgentApprovalChoice.Allow)["scope"]!.ToString().Should().Be("turn");

        var deny = CodexAppServerRules.BuildPermissionsResponse(request, AgentApprovalChoice.Deny);
        ((JObject)deny["permissions"]!).Properties().Should().BeEmpty();
        deny["scope"]!.ToString().Should().Be("turn");
    }

    [Fact]
    public void 提問工具_題目與答案()
    {
        var p = JObject.Parse("""{"threadId":"t","turnId":"u","itemId":"call_1","questions":[{"id":"drink_choice","header":"飲料","question":"想喝咖啡還是紅茶？","isOther":true,"isSecret":false,"options":[{"label":"咖啡","description":"選擇咖啡。"},{"label":"紅茶","description":"選擇紅茶。"}]},{"id":"pwd","header":"密碼","question":"請輸入密碼","isSecret":true,"options":null},{"id":"","question":"沒有 id"},{"id":"no_other","question":"只能選","isOther":false,"options":[{"label":"是"}]}],"isBlocking":true}""");

        var questions = CodexAppServerRules.ParseUserInputQuestions(p);
        questions.Select(q => q.Id).Should().Equal("drink_choice", "pwd", "no_other");
        questions[0].AllowOther.Should().BeTrue();
        questions[0].Options.Select(o => o.Label).Should().Equal("咖啡", "紅茶");
        questions[0].Options[0].Description.Should().Be("選擇咖啡。");
        questions[1].IsSecret.Should().BeTrue();
        questions[1].AllowOther.Should().BeTrue("沒有選項的題目只能自行輸入");
        questions[2].AllowOther.Should().BeFalse();

        var response = CodexAppServerRules.BuildUserInputResponse(new Dictionary<string, IReadOnlyList<string>>
        {
            ["drink_choice"] = new[] { "紅茶" },
            ["pwd"] = new[] { " secret ", "" },
        });
        response["answers"]!["drink_choice"]!["answers"]!.Values<string>().Should().Equal("紅茶");
        response["answers"]!["pwd"]!["answers"]!.Values<string>().Should().Equal("secret");
        ((JObject)CodexAppServerRules.BuildUserInputResponse(new Dictionary<string, IReadOnlyList<string>>())["answers"]!).Properties().Should().BeEmpty();
    }

    [Fact]
    public void 非同步提問_題目編號與選項()
    {
        var questions = CodexAppServerRules.ParseAsyncQuestions(JArray.Parse("""[{"title":"輸出格式","options":["Excel","CSV",""]},{"title":""},{"title":"備註"}]"""), "m9");
        questions.Select(q => q.Id).Should().Equal("m9:0", "m9:1");
        questions[0].Options.Select(o => o.Label).Should().Equal("Excel", "CSV");
        questions[1].Text.Should().Be("備註");
        questions.Should().OnlyContain(q => q.AllowOther);
        CodexAppServerRules.ParseAsyncQuestions(null, "x").Should().BeEmpty();
    }

    [Fact]
    public void DescribeCommand_優先用CommandActions_否則剝掉Pwsh包裝()
    {
        var withActions = JObject.Parse("""{"command":"\"C:\\\\Program Files\\\\PowerShell\\\\7\\\\pwsh.exe\" -Command 'ping -n 30 127.0.0.1'","commandActions":[{"type":"unknown","command":"ping -n 30 127.0.0.1"}]}""");
        CodexAppServerRules.DescribeCommand(withActions).Should().Be("ping -n 30 127.0.0.1");

        var two = JObject.Parse("""{"command":"x","commandActions":[{"type":"read","command":"cat a.txt"},{"type":"unknown","command":"ls"}]}""");
        CodexAppServerRules.DescribeCommand(two).Should().Be("cat a.txt && ls");

        var wrapped = JObject.Parse("""{"command":"pwsh.exe -Command 'echo ''hi'''","commandActions":[]}""");
        CodexAppServerRules.DescribeCommand(wrapped).Should().Be("echo 'hi'");

        CodexAppServerRules.DescribeCommand(JObject.Parse("""{"command":"git status"}""")).Should().Be("git status");
    }

    [Fact]
    public void ParseModels_略過隱藏的模型且預設排最前()
    {
        var result = JObject.Parse("""{"data":[{"id":"gpt-6.1-sol","model":"gpt-6.1-sol","displayName":"GPT-6.1-Sol","hidden":false,"isDefault":false,"supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"high"}]},{"id":"gpt-6-astra","model":"gpt-6-astra","displayName":"GPT-6-Astra","hidden":false,"isDefault":true,"supportedReasoningEfforts":[]},{"id":"secret","model":"secret","hidden":true},{"id":"only-id"}]}""");

        var models = CodexAppServerRules.ParseModels(result);

        models.Select(m => m.Value).Should().Equal("gpt-6-astra", "gpt-6.1-sol", "only-id");
        models[1].Efforts.Should().Equal("low", "high");
        models[2].Label.Should().Be("only-id");
        CodexAppServerRules.ParseModels(null).Should().BeEmpty();
    }

    [Fact]
    public void ServerRequest的鍵_數字與字串Id()
    {
        new CodexServerRequest(new JValue(0), "m", new JObject()).Key.Should().Be("0");
        new CodexServerRequest(new JValue("req-a"), "m", new JObject()).Key.Should().Be("req-a");
    }

    // ───── CLI 熱更新（WP1.3） ─────

    [Fact]
    public void 要不要換程序_設定簽章不同或執行檔換過才換_身分不明不算()
    {
        var v160 = CliIdentities.Codex("0.160.1", fileId: 1);
        var v162 = CliIdentities.Codex("0.162.0", fileId: 2);

        CodexAppServerRules.NeedsNewProcess("a|codex|True", v160, "a|codex|True", v160).Should().BeFalse();
        CodexAppServerRules.NeedsNewProcess("a|codex|True", v160, "a|codex|True", v162).Should().BeTrue("原地換版：路徑一樣、檔案不一樣");
        CodexAppServerRules.NeedsNewProcess("a|codex|True", v160, "a|codex|True", v160 with { Version = "0.162.0" }).Should().BeFalse(
            "同一份檔案（版本只是顯示資訊，來源不同時不能當成換版）");
        CodexAppServerRules.NeedsNewProcess("a|codex|True", null, "a|codex|True", v162).Should().BeFalse("程序啟動時身分不明：不因此重開");
        CodexAppServerRules.NeedsNewProcess("a|codex|True", v160, "a|codex|True", null).Should().BeFalse("這次的身分不明（還沒偵測、讀取失敗）");
        CodexAppServerRules.NeedsNewProcess("a|codex|True", v160, "b|codex|True", v160).Should().BeTrue("換帳戶");
        CodexAppServerRules.NeedsNewProcess(null, null, "a|codex|True", null).Should().BeTrue("沒有程序的簽章");
    }

    [Fact]
    public void Initialize回應_取userAgent裡的Codex版本與CodexHome()
    {
        // 實測（0.160.1）：{"userAgent":"<clientInfo.name>/<Codex 版本> (Windows 10.0.26300; x86_64) …","codexHome":"C:\\Users\\U\\.codex",…}
        var (version, home) = CodexAppServerRules.ParseInitializeResult(JObject.Parse(
            """{"userAgent":"myriagent/0.162.0 (Windows 10.0.26300; x86_64) WindowsTerminal (myriagent; 1.0.0.0)","codexHome":"C:\\Users\\U\\.codex","platformFamily":"windows","platformOs":"windows"}"""));
        version.Should().Be("0.162.0", "斜線後是 Codex 的版本，不是 clientInfo 的版本");
        home.Should().Be(@"C:\Users\U\.codex");

        CodexAppServerRules.ParseInitializeResult(JObject.Parse("""{"userAgent":"fake","codexHome":"fake"}""")).Should().Be(((string?)null, (string?)null));
        CodexAppServerRules.ParseInitializeResult(JObject.Parse("""{"userAgent":null,"codexHome":"\\\\server\\share\\.codex"}""")).CodexHome.Should().BeNull("UNC 路徑一律不認");
        CodexAppServerRules.ParseInitializeResult(null).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public void Thread回應的Path_是rollout的位置_不是本機完整路徑就不用()
    {
        CodexAppServerRules.ParseThreadPath(JObject.Parse(
            """{"thread":{"id":"01a12283-b129-7b02-aabd-cf6005e44f6b","path":"C:\\Users\\U\\.codex\\sessions\\2026\\10\\10\\rollout-2026-10-10T05-13-38-01a12283-b129-7b02-aabd-cf6005e44f6b.jsonl"},"model":"gpt-6-luna"}"""))
            .Should().EndWith("rollout-2026-10-10T05-13-38-01a12283-b129-7b02-aabd-cf6005e44f6b.jsonl");
        CodexAppServerRules.ParseThreadPath(JObject.Parse("""{"thread":{"id":"t","path":null}}""")).Should().BeNull();
        CodexAppServerRules.ParseThreadPath(JObject.Parse("""{"thread":{"id":"t"}}""")).Should().BeNull();
        CodexAppServerRules.ParseThreadPath(JObject.Parse("""{"thread":{"id":"t","path":"sessions\\x.jsonl"}}""")).Should().BeNull("相對路徑");
        CodexAppServerRules.ParseThreadPath(JObject.Parse("""{"thread":{"id":"t","path":"\\\\server\\s\\x.jsonl"}}""")).Should().BeNull("UNC");
        CodexAppServerRules.ParseThreadPath(JObject.Parse("""{"thread":null}""")).Should().BeNull();
        CodexAppServerRules.ParseThreadPath(null).Should().BeNull();
    }

    [Theory]
    [InlineData("item/started", "commandExecution", true)]
    [InlineData("item/completed", "commandExecution", false)]
    [InlineData("item/started", "fileChange", true)]
    [InlineData("item/completed", "mcpToolCall", false)]
    public void 工具生命週期_指令改檔MCP才算(string method, string type, bool started)
    {
        var p = JObject.Parse($$"""{"item":{"type":"{{type}}","id":"call_1","status":"inProgress"},"threadId":"t-main","turnId":"u"}""");
        // 只有指令會讓出給模型、留在終端機裡繼續跑（改檔、MCP 工具一定等到結束）
        CodexAppServerRules.ParseToolLifecycle(method, p).Should().Be(new CodexToolLifecycle("t-main/call_1", "t-main", "call_1", started, type == "commandExecution"));
    }

    [Theory]
    [InlineData("item/started", """{"item":{"type":"agentMessage","id":"m1"},"threadId":"t"}""")]
    [InlineData("item/started", """{"item":{"type":"webSearch","id":"w1"},"threadId":"t"}""")]
    [InlineData("item/started", """{"item":{"type":"collabAgentToolCall","id":"c1"},"threadId":"t"}""")]
    [InlineData("item/started", """{"item":{"type":"reasoning","id":"r1"},"threadId":"t"}""")]
    [InlineData("item/started", """{"item":{"type":"commandExecution","id":null},"threadId":"t"}""")]
    [InlineData("item/started", """{"item":null,"threadId":"t"}""")]
    [InlineData("item/commandExecution/outputDelta", """{"item":{"type":"commandExecution","id":"e1"},"threadId":"t"}""")]
    [InlineData("turn/completed", """{"threadId":"t","turn":{"id":"u","status":"completed"}}""")]
    public void 工具生命週期_其他項目與通知不算(string method, string json)
        => CodexAppServerRules.ParseToolLifecycle(method, JObject.Parse(json)).Should().BeNull();

    [Fact]
    public void 工具生命週期_子代理的項目也算_不同Thread的同一個Id分開記()
    {
        var parser = new CodexEventParser();
        var lifecycle = new List<(string Key, bool Started)>();
        foreach (var line in new[]
                 {
                     """{"method":"item/started","params":{"item":{"type":"commandExecution","id":"exec-1","command":"x","status":"inProgress"},"threadId":"main","turnId":"u"}}""",
                     """{"method":"item/started","params":{"item":{"type":"commandExecution","id":"exec-1","command":"y","status":"inProgress"},"threadId":"child","turnId":"c"}}""",
                     """{"method":"item/completed","params":{"item":{"type":"commandExecution","id":"exec-1","status":"completed","exitCode":0},"threadId":"main","turnId":"u"}}""",
                     """{"method":"item/started","params":{"item":{"type":"agentMessage","id":"msg","text":""},"threadId":"main","turnId":"u"}}""",
                 })
        {
            lifecycle.AddRange(parser.Parse(line, "main").ToolLifecycle.Select(t => (t.Key, t.Started)));
        }

        lifecycle.Should().Equal(("main/exec-1", true), ("child/exec-1", true), ("main/exec-1", false));
    }

    [Theory]
    [InlineData(nameof(AgentRestartMode.SafePoint), false, true, false, nameof(CodexRestartEntry.Defer))]
    [InlineData(nameof(AgentRestartMode.SafePoint), false, false, false, nameof(CodexRestartEntry.Defer))]
    [InlineData(nameof(AgentRestartMode.SafePoint), true, false, false, nameof(CodexRestartEntry.NothingToRestart))]
    [InlineData(nameof(AgentRestartMode.SafePoint), true, true, false, nameof(CodexRestartEntry.Proceed))]
    [InlineData(nameof(AgentRestartMode.SafePoint), true, false, true, nameof(CodexRestartEntry.Proceed))]
    [InlineData(nameof(AgentRestartMode.Immediate), false, true, false, nameof(CodexRestartEntry.Proceed))]
    [InlineData(nameof(AgentRestartMode.Immediate), false, false, false, nameof(CodexRestartEntry.NothingToRestart))]
    [InlineData(nameof(AgentRestartMode.Immediate), true, false, true, nameof(CodexRestartEntry.Proceed))]
    public void 重開的第一步_不在安全點就等_沒有程序也沒有停下的一輪就不用做(string mode, bool atSafePoint, bool live, bool suspended, string expected)
        => CodexAppServerRules.DecideRestartEntry(Enum.Parse<AgentRestartMode>(mode), atSafePoint, live, suspended)
            .Should().Be(Enum.Parse<CodexRestartEntry>(expected));

    [Theory]
    [InlineData(false, true, true, false, nameof(CodexRestartFollowUp.StopAndSuspend))]
    [InlineData(false, true, false, false, nameof(CodexRestartFollowUp.StopAndSuspend))]
    [InlineData(false, false, true, false, nameof(CodexRestartFollowUp.Stop))]
    [InlineData(false, true, true, true, nameof(CodexRestartFollowUp.Stop))]
    [InlineData(true, true, true, false, nameof(CodexRestartFollowUp.Continue))]
    [InlineData(true, true, false, false, nameof(CodexRestartFollowUp.NoContinuation))]
    [InlineData(true, false, true, false, nameof(CodexRestartFollowUp.NoContinuation))]
    [InlineData(true, true, true, true, nameof(CodexRestartFollowUp.NoContinuation))]
    public void 停下舊程序之後_只停下_不接續_或接續(bool hasSettings, bool interrupted, bool autoContinue, bool cancelled, string expected)
        => CodexAppServerRules.DecideRestartFollowUp(hasSettings, interrupted, autoContinue, cancelled)
            .Should().Be(Enum.Parse<CodexRestartFollowUp>(expected));

    [Theory]
    [InlineData("01a12283-b129-7b02-aabd-cf6005e44f6b", true)]
    [InlineData("thread-fake", true)]
    [InlineData("*", false)]
    [InlineData("a?b", false)]
    [InlineData("..\\x", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ThreadId_能不能放進檔名搜尋樣式(string? threadId, bool expected)
        => CodexAppServerRules.IsSearchableThreadId(threadId).Should().Be(expected);

    [Fact]
    public void 找Rollout_檔名要是這個Thread_有多個取最新寫入的()
    {
        const string Id = "01a12283-b129-7b02-aabd-cf6005e44f6b";
        CodexAppServerRules.RolloutSearchPattern(Id).Should().Be($"rollout-*-{Id}.jsonl");
        CodexAppServerRules.IsRolloutFileName($"rollout-2026-10-10T05-13-38-{Id}.jsonl", Id).Should().BeTrue();
        CodexAppServerRules.IsRolloutFileName($"ROLLOUT-2026-10-10T05-13-38-{Id.ToUpperInvariant()}.JSONL", Id).Should().BeTrue();
        CodexAppServerRules.IsRolloutFileName($"rollout-2026-10-10T05-13-38-x{Id}.jsonl", Id).Should().BeFalse("別的 thread id 結尾剛好相同");
        CodexAppServerRules.IsRolloutFileName($"rollout-2026-10-10T05-13-38-{Id}.jsonl.bak", Id).Should().BeFalse();
        CodexAppServerRules.IsRolloutFileName(null, Id).Should().BeFalse();

        var older = new DateTime(2026, 10, 9, 1, 0, 0, DateTimeKind.Utc);
        var newer = older.AddHours(3);
        CodexAppServerRules.PickRolloutPath(Id, new[]
        {
            ($@"C:\h\sessions\2026\10\09\rollout-2026-10-09T09-00-00-{Id}.jsonl", older),
            ($@"C:\h\sessions\2026\10\10\rollout-2026-10-10T05-13-38-{Id}.jsonl", newer),
            ($@"C:\h\sessions\2026\10\10\rollout-2026-10-10T06-00-00-other-{Id}x.jsonl", newer.AddHours(1)),
        }).Should().Be($@"C:\h\sessions\2026\10\10\rollout-2026-10-10T05-13-38-{Id}.jsonl");
        CodexAppServerRules.PickRolloutPath(Id, Array.Empty<(string, DateTime)>()).Should().BeNull();
    }

    [Fact]
    public void 不是自己剛結束的程序_Rollout要安靜一段時間才截尾()
    {
        var now = new DateTime(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);
        CodexAppServerRules.IsQuietForTrim(now - CodexAppServerRules.RolloutQuietPeriod, now).Should().BeTrue();
        CodexAppServerRules.IsQuietForTrim(now.AddMinutes(-30), now).Should().BeTrue();
        CodexAppServerRules.IsQuietForTrim(now.AddSeconds(-1), now).Should().BeFalse("可能還有其他程式（VS Code 的 Codex）正在寫");
        CodexAppServerRules.IsQuietForTrim(now.AddSeconds(10), now).Should().BeFalse("寫入時間在未來（時鐘被調過）");
    }

    [Fact]
    public void 重開時被結束的工具_失敗改成被重開中斷的已停止_保留輸出_其他狀態不動()
    {
        var failed = new AgentToolCompletedEvent("exec-1", AgentItemStatus.Failed, "partial output\n", Array.Empty<string>());
        var marked = CodexAppServerRules.MarkInterruptedByRestart(failed);
        marked.Status.Should().Be(AgentItemStatus.Cancelled, "被重開結束的指令不是自己失敗");
        marked.Output.Should().Be("partial output\n\n" + CodexAppServerRules.InterruptedToolOutput);
        marked.ToolId.Should().Be("exec-1");

        CodexAppServerRules.MarkInterruptedByRestart(failed with { Output = null }).Output.Should().Be(CodexAppServerRules.InterruptedToolOutput, "沒有輸出就只有說明");
        var done = new AgentToolCompletedEvent("exec-2", AgentItemStatus.Done, "ok", Array.Empty<string>());
        CodexAppServerRules.MarkInterruptedByRestart(done).Should().BeSameAs(done, "中斷前已經完成的照舊");
        var declined = done with { Status = AgentItemStatus.Declined };
        CodexAppServerRules.MarkInterruptedByRestart(declined).Should().BeSameAs(declined);
        CodexAppServerRules.InterruptedToolOutput.Should().Be(ClaudeStreamRules.InterruptedToolOutput.Replace("Claude Code", "Codex"), "與 Claude 的說法一致");
    }

    [Fact]
    public void ParseCommandOutcome_結束碼與耗時_不是指令或欄位不是數字時是null()
    {
        JObject Item(string json) => JObject.Parse("{\"item\":" + json + ",\"threadId\":\"t\",\"turnId\":\"u\"}");

        CodexAppServerRules.ParseCommandOutcome(Item("{\"type\":\"commandExecution\",\"id\":\"bg1\",\"status\":\"completed\",\"exitCode\":1,\"durationMs\":26891}"))
            .Should().Be(((int?)1, (TimeSpan?)TimeSpan.FromMilliseconds(26891)));
        CodexAppServerRules.ParseCommandOutcome(Item("{\"type\":\"commandExecution\",\"id\":\"bg1\",\"exitCode\":null,\"durationMs\":\"x\"}"))
            .Should().Be(((int?)null, (TimeSpan?)null));
        CodexAppServerRules.ParseCommandOutcome(Item("{\"type\":\"fileChange\",\"id\":\"f\",\"exitCode\":0}")).Should().Be(((int?)null, (TimeSpan?)null));
        CodexAppServerRules.ParseCommandOutcome(null).Should().Be(((int?)null, (TimeSpan?)null));
    }

    [Fact]
    public void ParseCallEnded_tokenUsage是那個thread的一次模型呼叫結束_其他通知不算()
    {
        // 0.164.0 實測（codex_background_probe.py --mode yield）：讓出的指令在這一則時還是 inProgress，等到結束的指令先 item/completed 才有這一則
        CodexAppServerRules.ParseCallEnded("thread/tokenUsage/updated", JObject.Parse("{\"threadId\":\"child\",\"turnId\":\"u\",\"tokenUsage\":{}}")).Should().Be("child");
        CodexAppServerRules.ParseCallEnded("thread/tokenUsage/updated", new JObject()).Should().Be(string.Empty, "沒帶 threadId 的當成主對話（同 IsOtherThread）");
        CodexAppServerRules.ParseCallEnded("account/rateLimits/updated", JObject.Parse("{\"threadId\":\"t\"}")).Should().BeNull();
        CodexAppServerRules.ParseCallEnded("item/completed", JObject.Parse("{\"threadId\":\"t\"}")).Should().BeNull();
    }

    [Fact]
    public void 停止背景指令_list找工具卡的processId_terminate回false算已結束_停掉的失敗改成已停止()
    {
        // codex_background_probe.py --mode stop（0.164.0）的回應
        var listed = JObject.Parse("""{"data":[{"itemId":"exec-b","processId":"25100","command":"Start-Sleep 30","cwd":"C:\\w","osPid":null,"cpuPercent":null,"rssKb":null},{"itemId":"exec-a","processId":"49723","command":"x","cwd":"C:\\w"}],"nextCursor":null}""");
        CodexAppServerRules.FindBackgroundTerminal(listed, "exec-a").Should().Be("49723");
        CodexAppServerRules.FindBackgroundTerminal(listed, "exec-z").Should().BeNull("已經不在終端機裡（跑完或停了）");
        CodexAppServerRules.FindBackgroundTerminal(JObject.Parse("""{"data":[{"itemId":"exec-a","processId":null}]}"""), "exec-a").Should().BeNull();
        CodexAppServerRules.FindBackgroundTerminal(null, "exec-a").Should().BeNull("list 失敗（不支援這個實驗性方法）");

        CodexAppServerRules.TerminatedNothing(JObject.Parse("""{"terminated":false}""")).Should().BeTrue();
        CodexAppServerRules.TerminatedNothing(JObject.Parse("""{"terminated":true}""")).Should().BeFalse();
        CodexAppServerRules.TerminatedNothing(new JObject()).Should().BeFalse("沒寫就當成停了：之後的 item/completed 才是結果");

        var failed = new AgentToolCompletedEvent("exec-a", AgentItemStatus.Failed, null, Array.Empty<string>());
        CodexAppServerRules.MarkStoppedByUser(failed).Should().Be(failed with { Status = AgentItemStatus.Cancelled, Output = CodexAppServerRules.StoppedByUserOutput },
            "terminate 結束的是 failed、結束碼 -1：卡片寫已停止，不像指令自己失敗了");
        CodexAppServerRules.MarkStoppedByUser(failed with { Output = "partial\n" }).Output.Should().Be("partial\n\n" + CodexAppServerRules.StoppedByUserOutput);
        var done = failed with { Status = AgentItemStatus.Done };
        CodexAppServerRules.MarkStoppedByUser(done).Should().Be(done, "停止之前剛好自己跑完的照舊");
    }
}
