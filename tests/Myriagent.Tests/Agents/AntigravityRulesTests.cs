using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>Antigravity CLI（agy 1.4.6）的純函式規則：帳戶、沙箱與精簡模式、背景說明、agy models、Skills 探索、點數與額度耗盡、啟動參數、計畫確認與找檔</summary>
public class AntigravityRulesTests
{
    private static AgentAccount Antigravity() => new() { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity（本機）" };

    // ───── 帳戶 ─────

    [Fact]
    public void ApplyEnvironment_agy程序剝除GeminiAPIKey_保留ADC登入_所有子程序剝除父agy工作階段的變數()
    {
        Dictionary<string, string?> Inherited() => new(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\Windows",
            ["GEMINI_API_KEY"] = "gemini-key",
            ["google_api_key"] = "google-key",
            ["GOOGLE_GEMINI_BASE_URL"] = "https://proxy.example",
            ["AGY_ADC_AUTH"] = "1",
            ["GOOGLE_APPLICATION_CREDENTIALS"] = @"C:\adc.json",
            ["ANTIGRAVITY_CONVERSATION_ID"] = "parent",
            ["ANTIGRAVITY_LS_ADDRESS"] = "localhost:51995",
            ["ANTIGRAVITY_CSRF_TOKEN"] = "csrf",
            ["AGY_CLI_HIDE_LOGO"] = "1",
        };

        var agy = Inherited();
        AccountRules.ApplyEnvironment(Antigravity(), agy);
        agy.Keys.Should().BeEquivalentTo("PATH", "AGY_ADC_AUTH", "GOOGLE_APPLICATION_CREDENTIALS", "AGY_CLI_HIDE_LOGO", "AGY_CLI_DISABLE_AUTO_UPDATE");
        agy["AGY_CLI_DISABLE_AUTO_UPDATE"].Should().Be("true", "實測 agy 1.4.6 只認字串 true（設 1 仍會派生背景更新子程序）");

        var tool = Inherited();
        AccountRules.ApplyToolEnvironment(tool);
        tool.Keys.Should().NotContain("AGY_CLI_DISABLE_AUTO_UPDATE", "不分帳戶的工具環境（agy update）不關更新");

        var claude = Inherited();
        AccountRules.ApplyEnvironment(new AgentAccount { Provider = AgentProvider.Claude, Label = "c" }, claude);
        claude.Keys.Should().Contain(new[] { "GEMINI_API_KEY", "google_api_key", "GOOGLE_GEMINI_BASE_URL" }, "Claude、Codex 的工具可能正需要 Gemini 的變數");
        claude.Keys.Should().NotContain(k => k.StartsWith("ANTIGRAVITY_"), "父 agy 工作階段的對話 id、language server 位址不能傳下去");
    }

    [Fact]
    public void 只有本機預設帳戶_支援精簡模式_沒有憑證檔可監看_資料夾在使用者資料夾的gemini底下()
    {
        AccountRules.SystemDefaultId(AgentProvider.Antigravity).Should().Be("local-antigravity");
        AccountRules.SupportsIsolatedAccounts(AgentProvider.Antigravity).Should().BeFalse();
        AccountRules.SupportsIsolatedAccounts(AgentProvider.Claude).Should().BeTrue();
        AccountRules.SupportsSafeMode(AgentProvider.Antigravity).Should().BeTrue("agy 1.4.6 支援 --disable-slash-commands 精簡模式");
        AccountRules.SupportsSafeMode(AgentProvider.Codex).Should().BeTrue();
        AccountRules.HasUsageQuota(AgentProvider.Antigravity).Should().BeTrue("agy -p /usage --output-format json 提供無頭額度查詢");
        AccountRules.HasUsageQuota(AgentProvider.Claude).Should().BeTrue();
        AccountRules.GetCredentialFile(Antigravity()).Should().BeNull("登入在 Windows 認證管理員");
        AccountRules.ResolveConfigDirectory(Antigravity()).Should().Be(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli"));
        AntigravityStreamRules.ResolveHome(@"C:\Users\u").Should().Be(@"C:\Users\u\.gemini\antigravity-cli");
        AntigravityStreamRules.ResolveBrainDirectory(@"C:\Users\u", "conv-123").Should().Be(@"C:\Users\u\.gemini\antigravity-cli\brain\conv-123");
        AntigravityStreamRules.ResolveTranscriptPath(@"C:\Users\u", "conv-123")
            .Should().Be(@"C:\Users\u\.gemini\antigravity-cli\brain\conv-123\.system_generated\logs\transcript_full.jsonl");
        AntigravityStreamRules.ResolveBrainDirectory(@"C:\Users\u", "../escape").Should().BeNull("防目錄穿越");
        AntigravityStreamRules.ResolveBrainDirectory(@"C:\Users\u", @"a\b").Should().BeNull();
        AntigravityStreamRules.ResolveBrainDirectory(@"C:\Users\u", "..").Should().BeNull();
        AntigravityStreamRules.ResolveBrainDirectory(@"C:\Users\u", "").Should().BeNull();
        AccountRules.SystemDefaultAppearance(AgentProvider.Antigravity).Should().Be(("Antigravity（本機）", "#7C8CF8"));
        AccountRules.DescribeStatus(AgentProvider.Antigravity, AccountStatus.NotLoggedIn).Should().Be("尚未登入 Antigravity（Google 帳戶）");
        AccountRules.DescribeStatus(AgentProvider.Antigravity, AccountStatus.NotInstalled).Should().Be("這台電腦尚未安裝 Antigravity CLI");
        AccountRules.DefaultLabel(AgentProvider.Antigravity, Array.Empty<AgentAccount>()).Should().Be("Antigravity 帳戶");
        AccountRules.DefaultColor(AgentProvider.Antigravity, new[] { new AgentAccount { Color = "#7C8CF8" } }).Should().Be("#C678DD");
        AgentProviders.ProductName(AgentProvider.Antigravity).Should().Be("Antigravity");
        AgentProviders.All.Should().Equal(AgentProvider.Claude, AgentProvider.Codex, AgentProvider.Antigravity, AgentProvider.Grok);
    }

    // ───── 權限模式與背景說明 ─────

    [Fact]
    public void 權限模式_支援自動沙箱_每步詢問換成自動接受編輯()
    {
        var modes = AgentPermissionModes.For(AgentProvider.Antigravity);
        modes.Select(o => o.Mode).Should().Equal(
            AgentPermissionMode.AcceptEdits, AgentPermissionMode.Plan, AgentPermissionMode.Auto, AgentPermissionMode.FullAccess);
        modes.Single(o => o.Mode == AgentPermissionMode.Auto).Label.Should().Be("自動（沙箱）");
        AgentPermissionModes.Normalize(AgentProvider.Antigravity, AgentPermissionMode.Ask).Should().Be(AgentPermissionMode.AcceptEdits);
        AgentPermissionModes.Normalize(AgentProvider.Antigravity, AgentPermissionMode.Auto).Should().Be(AgentPermissionMode.Auto,
            "agy 1.4.6 以 --dangerously-skip-permissions --sandbox 提供 Windows 沙箱自動模式");
        AgentPermissionModes.Normalize(AgentProvider.Antigravity, AgentPermissionMode.Plan).Should().Be(AgentPermissionMode.Plan);
        AgentPermissionModes.Normalize(AgentProvider.Antigravity, AgentPermissionMode.FullAccess).Should().Be(AgentPermissionMode.FullAccess);
        ConversationDefaultsRules.PermissionFor(AgentProvider.Antigravity, null).Should().Be(AgentPermissionMode.AcceptEdits);
    }

    [Fact]
    public void 背景說明_沒有提問工具的那一行換成用文字詢問_工作資料夾註明換資料夾會另外告知()
    {
        var text = AgentHubRules.BuildAgentInstructions(null, AgentProvider.Antigravity, @"C:\work", @"C:\att");

        text.Should().NotContain("{questionTool}").And.NotContain("request_user_input").And.NotContain("AskUserQuestion");
        text.Should().Contain(AgentHubRules.AntigravityQuestionLine);
        text.Should().Contain(@"工作資料夾：C:\work" + AgentHubRules.AntigravityWorkingDirectoryNote);
        text.Should().Contain(@"使用者上傳的附件放在：C:\att");

        // 使用者自訂的範本：只換提到 {questionTool} 的行，連續兩行只換成一句
        AgentHubRules.BuildAgentInstructions("A\n用 {questionTool} 問\n再用 {questionTool}\nB {cwd}", AgentProvider.Antigravity, @"C:\w", @"C:\a")
            .Should().Be("A\n" + AgentHubRules.AntigravityQuestionLine + "\nB C:\\w" + AgentHubRules.AntigravityWorkingDirectoryNote);
        AgentHubRules.BuildAgentInstructions("沒有提到工具 {cwd}", AgentProvider.Antigravity, @"C:\w", @"C:\a")
            .Should().Be("沒有提到工具 C:\\w" + AgentHubRules.AntigravityWorkingDirectoryNote);
    }

    // ───── agy models ─────

    [Fact]
    public void 模型清單_沒登入的實測輸出是未登入()
    {
        var result = AntigravityStreamRules.EvaluateModelsCommand(1,
            "Fetching available models...\nError: Please sign in to view available models. Launch the CLI without arguments to sign in.\n", "", false);

        result.Status.Should().Be(AccountStatus.NotLoggedIn);
        result.Models.Should().BeEmpty();
        AntigravityStreamRules.EvaluateModelsCommand(0, "", "Authentication required. Please visit the URL to log in:", false).Status
            .Should().Be(AccountStatus.NotLoggedIn);
    }

    /// <summary>agy 1.3.5 實測的 agy models 輸出（stdout；「Fetching available models...」在 stderr）</summary>
    private const string RealModelsOutput =
        "gemini-4-argon-medium\tGemini 4 Argon (Medium)\ngemini-4-argon-high\tGemini 4 Argon (High)\ngemini-4-argon-low\tGemini 4 Argon (Low)\n"
        + "gemini-3.8-flash-high\tGemini 3.8 Flash (High)\ngemini-3.8-flash-medium\tGemini 3.8 Flash (Medium)\ngemini-3.8-flash-low\tGemini 3.8 Flash (Low)\n"
        + "claude-opus-5-5-low\tClaude Opus 5.5 (Low)\nclaude-opus-5-5-medium\tClaude Opus 5.5 (Medium)\nclaude-opus-5-5-high\tClaude Opus 5.5 (High)\n"
        + "claude-sonnet-5-5-low\tClaude Sonnet 5.5 (Low)\nclaude-sonnet-5-5-medium\tClaude Sonnet 5.5 (Medium)\nclaude-sonnet-5-5-high\tClaude Sonnet 5.5 (High)\n"
        + "gpt-oss-120b-medium\tGPT-OSS 120B (Medium)\n";

    [Fact]
    public void 模型清單_實測輸出_同一個模型的幾個強度合併成一項_強度由低到高()
    {
        var result = AntigravityStreamRules.EvaluateModelsCommand(0, RealModelsOutput, "Fetching available models...\n", false);

        result.Status.Should().Be(AccountStatus.Ready);
        result.Models.Select(m => (m.Value, m.Label, string.Join(",", m.Efforts))).Should().Equal(
            ("gemini-4-argon", "Gemini 4 Argon", "low,medium,high"),
            ("gemini-3.8-flash", "Gemini 3.8 Flash", "low,medium,high"),
            ("claude-opus-5-5", "Claude Opus 5.5", "low,medium,high"),
            ("claude-sonnet-5-5", "Claude Sonnet 5.5", "low,medium,high"),
            ("gpt-oss-120b", "GPT-OSS 120B", "medium"));
    }

    [Fact]
    public void 模型清單_說明行與重複不算_沒有強度的id照原樣_沒有強度選單()
    {
        var result = AntigravityStreamRules.EvaluateModelsCommand(0,
            "Fetching available models...\ngemini-3.8-pro\tGemini 3.8 Pro\nclaude-sonnet-4-6  Claude Sonnet 4.6\ngemini-3.8-pro\t重複\nflash-x-high\nflash-x-xhigh\tFlash X (Extra High)\n\nAvailable models:\n",
            "", false);

        result.Models.Select(m => (m.Value, m.Label, string.Join(",", m.Efforts))).Should().Equal(
            ("gemini-3.8-pro", "Gemini 3.8 Pro", ""),
            ("claude-sonnet-4-6", "Claude Sonnet 4.6", ""),
            ("flash-x", "flash-x", "high,xhigh"));
    }

    [Fact]
    public void 模型清單_逾時與失敗是檢查失敗_訊息取最後一行()
    {
        AntigravityStreamRules.EvaluateModelsCommand(-1, "", "", true).Should().BeEquivalentTo(
            new { Status = AccountStatus.Error, Error = "agy models 逾時沒有回應" });
        AntigravityStreamRules.EvaluateModelsCommand(3, "Fetching available models...\n", "Error: backend unavailable\n", false).Error
            .Should().Be("Error: backend unavailable");
    }

    [Theory]
    [InlineData("gemini-3.8-flash-high", true)]
    [InlineData("gemini-3.8-flash-xhigh", true)]
    [InlineData("gemini-3.8-flash", false)]
    [InlineData("claude-sonnet-4-6", false)]
    [InlineData(null, false)]
    public void 模型id帶推理強度(string? model, bool expected)
        => AntigravityStreamRules.ModelEncodesEffort(model).Should().Be(expected);

    // ───── 啟動參數與輸入 ─────

    [Fact]
    public void 啟動參數_提示要用等號接在print後面_訊息從stdin送()
    {
        // 實測：-p 會把下一個參數當成提示（「-p took "--input-format" as its prompt」），所以用 --print= 給空的提示
        AntigravityStreamRules.BuildArguments(null, "default", null, AgentPermissionMode.AcceptEdits, @"C:\conv").Should().Equal(
            "--print=", "--input-format", "stream-json", "--output-format", "stream-json", "--mode", "accept-edits", "--add-dir", @"C:\conv");
    }

    [Fact]
    public void 啟動參數_續接_模型_推理強度_沙箱自動_完全自動_精簡模式()
    {
        AntigravityStreamRules.BuildArguments("conv-1", "gemini-3.8-pro", "high", AgentPermissionMode.FullAccess, @"C:\conv").Should().Equal(
            "--print=", "--input-format", "stream-json", "--output-format", "stream-json",
            "--conversation", "conv-1", "--model", "gemini-3.8-pro", "--effort", "high", "--dangerously-skip-permissions", "--add-dir", @"C:\conv");
        AntigravityStreamRules.BuildArguments(null, "gemini-3.8-pro", "medium", AgentPermissionMode.Auto, @"C:\conv", useLocalCustomizations: false).Should().Equal(
            "--print=", "--input-format", "stream-json", "--output-format", "stream-json",
            "--model", "gemini-3.8-pro", "--effort", "medium", "--dangerously-skip-permissions", "--sandbox", "--disable-slash-commands", "--add-dir", @"C:\conv");
        // 選單給的是基本 id＋強度（實測可用）；舊設定留下的完整 id 不再加 --effort（實測會報 conflicts with --effort）
        AntigravityStreamRules.BuildArguments(null, "gemini-3.8-flash", "low", AgentPermissionMode.Plan, "").Should().Equal(
            "--print=", "--input-format", "stream-json", "--output-format", "stream-json", "--model", "gemini-3.8-flash", "--effort", "low", "--mode", "plan");
        AntigravityStreamRules.BuildArguments(null, "gemini-3.8-flash-high", "low", AgentPermissionMode.Plan, "").Should().Equal(
            "--print=", "--input-format", "stream-json", "--output-format", "stream-json", "--model", "gemini-3.8-flash-high", "--mode", "plan");
        AntigravityStreamRules.PermissionArguments(AgentPermissionMode.Auto).Should().Equal("--dangerously-skip-permissions", "--sandbox");
        AntigravityStreamRules.PermissionArguments(AgentPermissionMode.Ask).Should().Equal("--mode", "accept-edits");

        // 新對話且非精簡模式時帶 --agent；續接對話或精簡模式不帶 --agent
        AntigravityStreamRules.BuildArguments(null, "default", null, AgentPermissionMode.AcceptEdits, @"C:\conv", agent: "security-auditor").Should().Equal(
            "--print=", "--input-format", "stream-json", "--output-format", "stream-json",
            "--agent", "security-auditor", "--mode", "accept-edits", "--add-dir", @"C:\conv");
        AntigravityStreamRules.BuildArguments("conv-1", "default", null, AgentPermissionMode.AcceptEdits, @"C:\conv", agent: "security-auditor")
            .Should().NotContain("--agent", "續接既有對話時代理身分已綁在對話內");
        AntigravityStreamRules.BuildArguments(null, "default", null, AgentPermissionMode.AcceptEdits, @"C:\conv", useLocalCustomizations: false, agent: "security-auditor")
            .Should().NotContain("--agent", "精簡模式不載入自訂代理");
        AntigravityStreamRules.NormalizeAgent("default").Should().BeNull();
        AntigravityStreamRules.NormalizeAgent("  architect  ").Should().Be("architect");
    }

    [Fact]
    public void StartsIdleTurn_閒置時收到非system_message的step_update代表背景或排程自動喚醒新一輪()
    {
        AntigravityStreamRules.StartsIdleTurn("""{"event":"step_update","step_update":{"step_index":5,"state":"DONE","step_type":"user_input"}}""").Should().BeTrue();
        AntigravityStreamRules.StartsIdleTurn("""{"event":"step_update","step_update":{"step_index":6,"state":"ACTIVE","step_type":"agent_response","text_delta":"喚醒"}}""").Should().BeTrue();
        AntigravityStreamRules.StartsIdleTurn("""{"event":"step_update","step_update":{"step_index":4,"state":"DONE","step_type":"system_message"}}""").Should().BeFalse();
        AntigravityStreamRules.StartsIdleTurn("""{"event":"step_update","step_update":{"step_index":4,"state":"DONE","step_type":"user_input"}}""", minStepIndex: 5)
            .Should().BeFalse("早於本輪步驟下限的舊 step_update 不可誤開閒置輪次");
        AntigravityStreamRules.StartsIdleTurn("""{"event":"step_update","step_update":{"step_index":7,"state":"CANCELED","step_type":"agent_response"}}""")
            .Should().BeFalse("遲到的取消狀態不可誤開閒置輪次");
        AntigravityStreamRules.StartsIdleTurn("""{"event":"step_update","step_update":{"step_index":7,"state":"ERROR","step_type":"tool"}}""")
            .Should().BeFalse("遲到的錯誤狀態不可誤開閒置輪次");
        AntigravityStreamRules.StartsIdleTurn("""{"event":"step_update","step_update":{"step_index":7,"state":"DONE","step_type":"tool"}}""")
            .Should().BeFalse("遲到的工具完成不可誤開閒置輪次");
        AntigravityStreamRules.StartsIdleTurn("""{"event":"init","conversation_id":"c1"}""").Should().BeFalse();
        AntigravityStreamRules.StartsIdleTurn("""{"event":"result","result":{"status":"SUCCESS"}}""").Should().BeFalse();
        AntigravityStreamRules.StartsIdleTurn("not json").Should().BeFalse();
    }

    [Fact]
    public void 重開程序的條件_任何啟動參數或執行檔改變()
    {
        string Sig(string? model = "m", string? effort = "high", AgentPermissionMode mode = AgentPermissionMode.Auto, string cwd = @"C:\w", bool custom = true)
            => AntigravityStreamRules.Signature("acc|dir", @"C:\agy.exe", cwd, model, effort, mode, @"C:\conv", custom);

        AntigravityStreamRules.NeedsNewProcess(Sig(), null, Sig(), null).Should().BeFalse();
        Sig(model: "default").Should().Be(Sig(model: null), "「預設」就是不傳 --model");
        AntigravityStreamRules.NeedsNewProcess(Sig(), null, Sig(effort: "low"), null).Should().BeTrue();
        AntigravityStreamRules.NeedsNewProcess(Sig(), null, Sig(mode: AgentPermissionMode.FullAccess), null).Should().BeTrue();
        AntigravityStreamRules.NeedsNewProcess(Sig(mode: AgentPermissionMode.AcceptEdits), null, Sig(mode: AgentPermissionMode.Auto), null)
            .Should().BeTrue("沙箱自動帶 --sandbox，與 accept-edits 不同，必須重開");
        Sig(mode: AgentPermissionMode.Ask).Should().Be(Sig(mode: AgentPermissionMode.AcceptEdits), "Ask 會正規化成 AcceptEdits，啟動參數相同");
        AntigravityStreamRules.NeedsNewProcess(Sig(custom: true), null, Sig(custom: false), null)
            .Should().BeTrue("切換精簡模式（--disable-slash-commands）要重開程序");
        AntigravityStreamRules.NeedsNewProcess(Sig(), null, Sig(cwd: @"C:\other"), null).Should().BeTrue();
    }

    [Fact]
    public void 輸入訊息_一行JSON_content是文字()
    {
        var line = AntigravityStreamRules.BuildUserMessage("你好\n\"引號\"");

        line.Should().NotContain("\n");
        JObject.Parse(line).Should().BeEquivalentTo(JObject.Parse("""{"event":"user","message":{"content":"你好\n\"引號\""}}"""));
    }

    [Fact]
    public void 這一輪的文字_背景說明_前文節錄_換資料夾通知_訊息與附件路徑()
    {
        var attachments = new List<AgentAttachment>
        {
            new() { Kind = AgentAttachmentKind.Image, FileName = "a.png", StoredPath = @"C:\att\a.png", ModelImagePaths = { @"C:\att\a.png" } },
        };

        var text = AntigravityStreamRules.BuildTurnText("說明", "（前文）", AntigravityStreamRules.BuildWorkingDirectoryNotice(@"D:\new"), "看這張圖", attachments);

        text.Should().StartWith("<myriagent_instructions>\n說明\n</myriagent_instructions>\n\n（前文）\n\n（系統通知：工作資料夾已改為 D:\\new");
        text.Should().Contain("看這張圖").And.Contain(@"1. 圖片「a.png」：C:\att\a.png").And.NotContain("已附在這則訊息中");
        AntigravityStreamRules.BuildTurnText(null, null, null, "只有訊息", Array.Empty<AgentAttachment>()).Should().Be("只有訊息");
    }

    [Theory]
    [InlineData("Authentication required. Please visit the URL to log in:", true)]
    [InlineData("Or, paste the authorization code here and press Enter:", true)]
    [InlineData("Waiting for authentication (timeout 60s)...", false)]
    [InlineData("MCP server github: authentication required", false)]
    [InlineData("{\"event\":\"init\"}", false)]
    public void 沒登入的提示(string line, bool expected) => AntigravityStreamRules.IsSignInPrompt(line).Should().Be(expected);

    [Theory]
    [InlineData("warning: conversation \"8d502931-d873-4b80-925e-d42e2c77000d\" not found", "8d502931-d873-4b80-925e-d42e2c77000d")]
    [InlineData("warning: conversation \"00000000-0000-4000-8000-000000000000\" not found\r", "00000000-0000-4000-8000-000000000000")]
    // agy 1.3.5 執行檔裡其他含 conversation／not found 的字串：不能當成續接失敗而殺掉正在跑的程序
    [InlineData("GetConversationDetail: conversation 8d502931 not found locally, searching fallback import dirs", null)]
    [InlineData("parent conversation 8d502931 not found", null)]
    [InlineData("agent state for conversation 8d502931 not found", null)]
    [InlineData("Conversation contains invalid tool call id", null)]
    [InlineData("warning: --mode auto is not available for this account; continuing in the default mode.", null)]
    public void stderr的找不到對話警告_只認精確格式(string line, string? expected)
        => AntigravityStreamRules.ParseConversationNotFoundWarning(line).Should().Be(expected);

    // ───── 找檔與最新版 ─────

    [Fact]
    public void 找檔_官方安裝位置或PATH上的agy_exe_以agy_update更新()
    {
        var dirs = new[] { @"C:\Users\u\AppData\Local\agy\bin", @"C:\tools" };

        var found = CliLocator.Resolve(AgentProvider.Antigravity, dirs, p => p == @"C:\tools\agy.exe", @"C:\Users\u\.local\bin", @"C:\npm", @"C:\managed");

        found!.Kind.Should().Be(CliInstallKind.Other);
        found.Launcher.FileName.Should().Be(@"C:\tools\agy.exe");
        found.NativeExecutable.Should().Be(@"C:\tools\agy.exe");
        CliLocator.Resolve(AgentProvider.Antigravity, dirs, p => p.EndsWith("agy.cmd"), "", "", "").Should().BeNull("agy 沒有 npm 版");
    }

    [Fact]
    public void 最新版_官方manifest的version()
    {
        CliVersionRules.ParseAntigravityManifest(
            """{"version":"1.3.5","url":"https://storage.googleapis.com/antigravity-public/antigravity-cli/1.3.5-6164039479459840/windows-x64/cli_windows_x64.exe","sha512":"4483"}""")
            .Should().Be("1.3.5");
        CliVersionRules.ParseAntigravityManifest("<html>").Should().BeNull();
    }

    // ───── 附件 ─────

    [Fact]
    public void 只能送文字時的附件說明_圖片與影片畫面都給路徑()
    {
        var attachments = new List<AgentAttachment>
        {
            new() { Kind = AgentAttachmentKind.Image, FileName = "big.png", StoredPath = @"C:\att\big.png", ModelImagePaths = { @"C:\att\big.model.jpg" } },
            new() { Kind = AgentAttachmentKind.Video, FileName = "v.mp4", StoredPath = @"C:\att\v.mp4", DurationSeconds = 65, ModelImagePaths = { @"C:\att\v-0.jpg", @"C:\att\v-1.jpg" }, FrameSeconds = { 0, 30 } },
            new() { Kind = AgentAttachmentKind.Document, FileName = "a.txt", StoredPath = @"C:\att\a.txt" },
        };

        var note = AgentAttachmentRules.BuildAttachmentNote(attachments, imagesInline: false);

        note.Should().Contain(@"1. 圖片「big.png」：C:\att\big.png（縮小的複本：C:\att\big.model.jpg）");
        note.Should().Contain(@"2. 影片「v.mp4」：C:\att\v.mp4（長 1:05）");
        note.Should().Contain(@"   - 0:00 的畫面：C:\att\v-0.jpg").And.Contain(@"   - 0:30 的畫面：C:\att\v-1.jpg");
        note.Should().Contain(@"3. 檔案「a.txt」：C:\att\a.txt");
        note.Should().NotContain("已附");
    }

    [Fact]
    public void 記錄裡登入成功的信箱_取最後一行_沒有就是null()
    {
        // agy 1.4.5 的記錄（2026-10-22 實測，信箱換成假的）：載入登入之前每次都有幾十行 not logged into，之後才是成功的那兩行
        var log = "I1022 20:27:53.866616      49 server.go:1734] Starting language server process with pid 93952\n"
            + "E1022 20:27:54.050000      90 errorreport.go:224] error getting token source: You are not logged into Antigravity.\n"
            + "I1022 20:27:54.173342       1 server_oauth.go:224] applyAuthResult: email=First@Example.com, authMethod=consumer, quotaProject=\n"
            + "I1022 20:27:54.173342       1 server_oauth.go:229] OAuth: authenticated successfully as First@Example.com\n"
            + "I1022 20:30:00.000000       1 server_oauth.go:229] OAuth: authenticated successfully as second@example.com\r\n";
        AntigravityStreamRules.ParseSignedInEmail(log).Should().Be("second@example.com", "換過帳號時取最後一行；行尾的 \r 不算");
        AntigravityStreamRules.ParseSignedInEmail("E1022 errorreport.go:224] error getting token source: You are not logged into Antigravity.")
            .Should().BeNull("只有過渡訊息＝沒登入或還沒載入：身分未知");
        AntigravityStreamRules.ParseSignedInEmail(null).Should().BeNull();
        AntigravityStreamRules.ParseSignedInEmail("OAuth: authenticated successfully as not-an-email").Should().BeNull();
        AntigravityStreamRules.ParseSignedInEmail("OAuth: authenticated successfully as a@" + new string('x', 400) + ".com").Should().BeNull("超過 320 字");
    }

    [Fact]
    public void 帶記錄檔的agy_models與usage_旗標在子指令前面()
    {
        AntigravityStreamRules.ModelsArgumentsWithLog(@"C:\data\cache\agy-log\models-1.log")
            .Should().Equal("--log-file", @"C:\data\cache\agy-log\models-1.log", "models");
        AntigravityStreamRules.UsageArguments
            .Should().Equal("-p", "/usage", "--output-format", "json");
        AntigravityStreamRules.UsageArgumentsWithLog(@"C:\data\cache\agy-log\usage-1.log")
            .Should().Equal("--log-file", @"C:\data\cache\agy-log\usage-1.log", "-p", "/usage", "--output-format", "json");
    }

    // ───── agy -p /usage --output-format json ─────

    /// <summary>agy 1.4.6 實測的 -p /usage --output-format json 輸出</summary>
    private const string RealUsageJson = """
        {"conversation_id":"","status":"SUCCESS","response":"Gemini Models\tWeekly Limit Remaining\t95%\t2026-10-29T05:01:26Z\nGemini Models\tFive Hour Limit Remaining\t98%\t2026-10-23T18:50:48Z\nClaude and GPT models\tWeekly Limit Remaining\t100%\t2026-10-30T13:54:42Z\nClaude and GPT models\tFive Hour Limit Remaining\t100%\t2026-10-23T18:54:42Z\n","duration_seconds":0,"num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":0},"command":{"name":"usage","data":{"description":"Within each group, models share a weekly limit and a 5-hour limit.","groups":[{"name":"Gemini Models","description":"Models within this group: Gemini Flash, Gemini Pro","buckets":[{"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.9483054876327515,"reset_time":"2026-10-29T05:01:26Z"},{"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.9817987084388733,"reset_time":"2026-10-23T18:50:48Z"}]},{"name":"Claude and GPT models","description":"Models within this group: Claude Opus, Claude Sonnet, GPT-OSS","buckets":[{"id":"3p-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":1,"reset_time":"2026-10-30T13:54:42Z"},{"id":"3p-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":1,"reset_time":"2026-10-23T18:54:42Z"}]}]}}}
        """;

    [Fact]
    public void 額度指令判讀_實測已登入與未登入及錯誤()
    {
        var ok = AntigravityStreamRules.EvaluateUsageCommand(0, RealUsageJson, "", false);
        ok.Status.Should().Be(AccountStatus.Ready);
        ok.UsageData.Should().NotBeNull();
        ok.Error.Should().BeNull();

        // 實測未登入且 stdin 已關閉（CliRunner.RunAsync）：約 1.1 秒結束，stdout 是 ERROR JSON、stderr 印 authentication required
        var unauth = AntigravityStreamRules.EvaluateUsageCommand(1,
            """{"conversation_id":"","status":"ERROR","response":"","error":"authentication failed or timed out"}""",
            "Error: authentication required. Run 'agy.exe' to log in.\nerror: authentication failed or timed out\n",
            false);
        unauth.Status.Should().Be(AccountStatus.NotLoggedIn);
        unauth.UsageData.Should().BeNull();

        var unauthJsonOnly = AntigravityStreamRules.EvaluateUsageCommand(1,
            """{"conversation_id":"","status":"ERROR","error":"authentication failed or timed out"}""", "", false);
        unauthJsonOnly.Status.Should().Be(AccountStatus.NotLoggedIn);

        AntigravityStreamRules.EvaluateUsageCommand(-1, "", "", true)
            .Should().Be(new AntigravityStreamRules.UsageEvaluation(AccountStatus.Error, null, "agy -p /usage 逾時沒有回應"));

        AntigravityStreamRules.EvaluateUsageCommand(1,
            """{"conversation_id":"","status":"ERROR","error":"retrieveUserQuotaSummary: service unavailable"}""", "", false)
            .Should().Be(new AntigravityStreamRules.UsageEvaluation(AccountStatus.Error, null, "retrieveUserQuotaSummary: service unavailable"));

        // 舊版 CLI 結束碼 0 但沒有 command.data：已登入、用量交給 MarkStale
        var legacy = AntigravityStreamRules.EvaluateUsageCommand(0, """{"conversation_id":"","status":"SUCCESS","response":"..."}""", "", false);
        legacy.Status.Should().Be(AccountStatus.Ready);
        legacy.UsageData.Should().BeNull();
        legacy.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ParseAntigravityUsage_實測四個時間窗_Gemini在前且5小時在每週前_鍵符合同步上限()
    {
        var data = AntigravityStreamRules.EvaluateUsageCommand(0, RealUsageJson, "", false).UsageData;
        var parsed = UsageRules.ParseAntigravityUsage(data);

        parsed.Should().NotBeNull();
        var (windows, note) = parsed!.Value;
        note.Should().BeNull();
        windows.Select(w => (w.Key, w.Label, w.Percent, w.Locked)).Should().Equal(
            ("agy-gemini-5h", "5 小時・Gemini", 1.8, false),
            ("agy-gemini-weekly", "每週・Gemini", 5.2, false),
            ("agy-3p-5h", "5 小時・Claude/GPT", 0, false),
            ("agy-3p-weekly", "每週・Claude/GPT", 0, false));
        windows[0].ResetsAt.Should().Be(DateTimeOffset.Parse("2026-10-23T18:50:48Z"));
        windows.Should().OnlyContain(w => UsageWindowKeyRules.Encode(w.Key) == w.Key, "所有鍵直接符合伺服器 [A-Za-z0-9-_] 且 ≤32 字");
    }

    [Fact]
    public void ParseAntigravityUsage_用滿省略remaining_fraction時視為100已鎖定_停用桶略過_計次桶記附註()
    {
        var json = JObject.Parse("""
            {
              "groups": [
                {
                  "name": "Gemini Models",
                  "buckets": [
                    { "id": "gemini-5h", "window": "5h", "reset_time": "2026-10-23T18:50:48Z" },
                    { "id": "gemini-weekly", "window": "weekly", "remaining_fraction": 0, "reset_time": "2026-10-29T05:01:26Z" },
                    { "id": "gemini-disabled", "window": "daily", "remaining_fraction": 0.5, "disabled": true },
                    { "id": "gemini-calls", "window": "daily", "remaining_amount": 1200 }
                  ]
                }
              ]
            }
            """);

        var (windows, note) = UsageRules.ParseAntigravityUsage(json)!.Value;
        windows.Select(w => (w.Key, w.Label, w.Percent, w.Locked)).Should().Equal(
            ("agy-gemini-5h", "5 小時・Gemini", 100, true),
            ("agy-gemini-weekly", "每週・Gemini", 100, true));
        note.Should().Be("每日・Gemini 剩餘 1,200 次");

        UsageRules.ParseAntigravityUsage(JObject.Parse("""{"groups":[]}"""))!.Value.Windows.Should().BeEmpty("無配額桶的方案合法");
        UsageRules.ParseAntigravityUsage(JObject.Parse("""{"other":1}""")).Should().BeNull("沒有 groups");
        UsageRules.ParseAntigravityUsage(null).Should().BeNull();
    }

    // ───── Skills 探索、CLI 本地斜線指令防呆、點數餘額、額度耗盡與計畫確認 ─────

    [Fact]
    public void DiscoverLocalCommands与EvaluateSkillsCommand_合併內建工作流與Skills並處理外掛啟停去重()
    {
        using var temp = new TempDirectory("agy-skills");
        var agyDir = temp.CreateDirectory(@".gemini\antigravity-cli");
        temp.WriteFile(@".gemini\antigravity-cli\builtin\skills\agy-customizations\SKILL.md",
            "---\nname: agy-customizations\ndescription: >-\n  Guide for\n  customizations\n---\n內容");
        temp.WriteFile(@".gemini\config\skills\my-deploy\SKILL.md",
            "---\nname: my-deploy\ndescription: 部署腳本\n---\n");
        temp.WriteFile(@".gemini\config\skills\plan\SKILL.md",
            "---\nname: plan\ndescription: 與內建工作流撞名應去重保留第一個\n---\n");
        temp.WriteFile(@".gemini\config\plugins\p-enabled\plugin.json", """{"name":"p-enabled"}""");
        temp.WriteFile(@".gemini\config\plugins\p-enabled\skills\plugin-lint\SKILL.md",
            "---\ndescription: 沒寫 name 時用資料夾名稱\n---\n");
        temp.WriteFile(@".gemini\config\plugins\p-manifest-off\plugin.json", """{"name":"p-manifest-off","disabled":true}""");
        temp.WriteFile(@".gemini\config\plugins\p-manifest-off\skills\hidden-manifest\SKILL.md", "---\nname: hidden-manifest\n---\n");
        temp.WriteFile(@".gemini\config\plugins\p-config-off\plugin.json", """{"name":"p-config-off"}""");
        temp.WriteFile(@".gemini\config\plugins\p-config-off\skills\hidden-config\SKILL.md", "---\nname: hidden-config\n---\n");
        temp.WriteFile(@".gemini\antigravity-cli\config.json", """{"plugins":{"p-config-off":{"enabled":false}}}""");

        var local = AntigravityStreamRules.DiscoverLocalCommands(agyDir);
        local.Select(c => c.Name).Should().ContainInOrder("plan", "grill-me", "agy-customizations", "my-deploy", "plugin-lint");
        local.Select(c => c.Name).Should().NotContain(new[] { "hidden-manifest", "hidden-config" });
        local.Single(c => c.Name == "agy-customizations").Description.Should().Be("Guide for customizations");
        local.Count(c => c.Name == "plan").Should().Be(1);

        var fromJson = AntigravityStreamRules.EvaluateSkillsCommand(0, """
            {"status":"SUCCESS","command":{"name":"skills","data":{"skills":[{"name":"antigravity-guide","description":"AGY guide"},{"name":"/plan","description":"dup"}]}}}
            """);
        fromJson.Select(c => c.Name).Should().Contain("antigravity-guide");
        fromJson.Count(c => c.Name == "plan").Should().Be(1);
        AntigravityStreamRules.EvaluateSkillsCommand(1, "").Should().Equal(AntigravityStreamRules.BuiltinWorkflowCommands);
    }

    [Fact]
    public void ParseCreditsBalance与IsQuotaExhausted_只顯示大於零點數_只把非重試額度耗盡當成耗盡訊號()
    {
        AntigravityStreamRules.ParseCreditsBalance(0, """{"status":"SUCCESS","command":{"name":"credits","data":{"remaining_credits":125.5}}}""")
            .Should().Be("AI 點數餘額 125.5");
        AntigravityStreamRules.ParseCreditsBalance(0, """{"status":"SUCCESS","command":{"name":"credits","data":{"remaining_credits":0}}}""")
            .Should().BeNull("0 點不佔畫面");
        AntigravityStreamRules.ParseCreditsBalance(1, """{"status":"ERROR"}""").Should().BeNull();
        AntigravityStreamRules.ParseCreditsBalance(0, "not json").Should().BeNull();

        AntigravityStreamRules.IsQuotaExhausted("Your Google One AI credits balance is too low to continue.").Should().BeTrue();
        AntigravityStreamRules.IsQuotaExhausted("Error: individual quota reached for Gemini Pro; resets at 2026-10-29T05:00:00Z").Should().BeTrue();
        AntigravityStreamRules.IsQuotaExhausted("You have exhausted your weekly quota.").Should().BeTrue();
        AntigravityStreamRules.IsQuotaExhausted("429 Too Many Requests: rate limit per minute, retrying...").Should().BeFalse("暫時性 429 重試不算額度耗盡");
        AntigravityStreamRules.IsQuotaExhausted(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("API error (attempt 1): UNAVAILABLE (code 503): The service is currently unavailable.", true)]
    [InlineData("API error (attempt 3): UNAVAILABLE (code 503): The service is currently unavailable.}", true)]
    [InlineData("503 Service Unavailable", true)]
    [InlineData("The service is temporarily unavailable", true)]
    [InlineData("Your Google One AI credits balance is too low to continue.", false)]
    [InlineData("API error (attempt 1): RESOURCE_EXHAUSTED (code 429): individual quota reached", false)]
    [InlineData("API error (attempt 1): INTERNAL (code 500): Internal error encountered.", false)]
    [InlineData("model gemini-x is unavailable for this account", false)]
    [InlineData("authentication failed or timed out", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsServiceUnavailable_只認503暫時無法服務(string? error, bool expected)
        => AntigravityStreamRules.IsServiceUnavailable(error).Should().Be(expected);

    [Theory]
    [InlineData("""API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": write tcp 192.168.0.10:55012->172.217.112.4:443: wsasend: An existing connection was forcibly closed by the remote host.""", true)]
    [InlineData("""API error (attempt 1): request failed: Post "https://cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": read tcp 192.168.1.10:51234->142.250.199.10:443: wsarecv: An existing connection was forcibly closed by the remote host.""", true)]
    [InlineData("""API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": EOF""", true)]
    [InlineData("""API error (attempt 1): stream error: stream ID 7; INTERNAL_ERROR; received from peer""", true)]
    [InlineData("""API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": unexpected EOF""", true)]
    [InlineData("""API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": http2: client connection lost""", true)]
    [InlineData("""API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": dial tcp 172.217.112.4:443: i/o timeout""", true)]
    [InlineData("""API error (attempt 1): request failed: stream idle timeout (5m0s): context deadline exceeded""", true)]
    [InlineData("""API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": context deadline exceeded""", true)]
    [InlineData("""API error (attempt 1): DEADLINE_EXCEEDED (code 504): Deadline expired before operation could complete.""", true)]
    [InlineData("""API error (attempt 1): UNAVAILABLE (code 503): The service is currently unavailable.""", false)]
    [InlineData("""API error (attempt 1): INTERNAL (code 500): Internal error encountered.""", false)]
    [InlineData("""API error (attempt 1): INVALID_ARGUMENT (code 400): Request contains an invalid argument.""", false)]
    [InlineData("""API error (attempt 1): RESOURCE_EXHAUSTED (code 429): individual quota reached""", false)]
    [InlineData("""API error (attempt 1): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": tls: failed to verify certificate: x509: certificate signed by unknown authority""", false)]
    [InlineData("authentication failed or timed out", false)]
    [InlineData("command timed out: context deadline exceeded", false)]
    [InlineData("wsasend: An existing connection was forcibly closed by the remote host.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTransientNetworkError_只認API呼叫的底層網路連線中斷(string? error, bool expected)
        => AntigravityStreamRules.IsTransientNetworkError(error).Should().Be(expected);

    [Fact]
    public void ClassifyServerBusy_區分503暫時無法服務與API網路連線中斷()
    {
        AntigravityStreamRules.ClassifyServerBusy("API error (attempt 1): UNAVAILABLE (code 503): The service is currently unavailable.")
            .Should().Be(AgentServerBusy.Unavailable);
        AntigravityStreamRules.ClassifyServerBusy("""API error (attempt 2): request failed: Post "https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse": write tcp 192.168.0.10:55012->172.217.112.4:443: wsasend: An existing connection was forcibly closed by the remote host.""")
            .Should().Be(AgentServerBusy.NetworkError);
        AntigravityStreamRules.ClassifyServerBusy("API error (attempt 1): request failed: stream idle timeout (5m0s): context deadline exceeded")
            .Should().Be(AgentServerBusy.NetworkError);
        AntigravityStreamRules.ClassifyServerBusy("API error (attempt 1): INTERNAL (code 500): Internal error encountered.")
            .Should().Be(AgentServerBusy.None);
    }

    [Fact]
    public void ClassifyCliOnlySlashCommand_區分介面管理_單發唯讀_與一般工作流指令()
    {
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/model gemini-3.8-pro")
            .Should().Match<AntigravityStreamRules.CliOnlySlashClassification>(c => c.Kind == AntigravityStreamRules.CliOnlySlashKind.UiManaged && c.UiHint!.Contains("模型選單"));
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/effort high").Kind.Should().Be(AntigravityStreamRules.CliOnlySlashKind.UiManaged);
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/permissions").Kind.Should().Be(AntigravityStreamRules.CliOnlySlashKind.UiManaged);

        AntigravityStreamRules.ClassifyCliOnlySlashCommand("  /credits  ")
            .Should().Be(new AntigravityStreamRules.CliOnlySlashClassification(AntigravityStreamRules.CliOnlySlashKind.SinglePrompt, "/credits", null));
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/usage").Kind.Should().Be(AntigravityStreamRules.CliOnlySlashKind.SinglePrompt);
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/skills").Kind.Should().Be(AntigravityStreamRules.CliOnlySlashKind.SinglePrompt);
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/help").Kind.Should().Be(AntigravityStreamRules.CliOnlySlashKind.SinglePrompt);

        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/plan 幫我重構").Kind.Should().Be(AntigravityStreamRules.CliOnlySlashKind.None);
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/grill-me").Kind.Should().Be(AntigravityStreamRules.CliOnlySlashKind.None);
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/swarm 平行處理").Kind.Should().Be(AntigravityStreamRules.CliOnlySlashKind.None);
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/credits\n第二行").Kind.Should().Be(AntigravityStreamRules.CliOnlySlashKind.None, "多行文字是一般對話訊息");
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("普通訊息").Kind.Should().Be(AntigravityStreamRules.CliOnlySlashKind.None);

        // agy 1.5.2+ 單獨 /plugin 正規化為 /plugin list；/mcp 改用 agy mcp list 子指令
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/plugin")
            .Should().BeEquivalentTo(new { Kind = AntigravityStreamRules.CliOnlySlashKind.SinglePrompt, PromptCommand = "/plugin list" });
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/plugins")
            .Should().BeEquivalentTo(new { Kind = AntigravityStreamRules.CliOnlySlashKind.SinglePrompt, PromptCommand = "/plugin list" });
        AntigravityStreamRules.ClassifyCliOnlySlashCommand("/plugin install foo")
            .Should().BeEquivalentTo(new { Kind = AntigravityStreamRules.CliOnlySlashKind.SinglePrompt, PromptCommand = "/plugin install foo" });

        var mcpCmd = AntigravityStreamRules.ClassifyCliOnlySlashCommand("/mcp");
        mcpCmd.Kind.Should().Be(AntigravityStreamRules.CliOnlySlashKind.SinglePrompt);
        mcpCmd.SubcommandArgs.Should().Equal("mcp", "list");

        var mcpSub = AntigravityStreamRules.ClassifyCliOnlySlashCommand("/mcp list --json");
        mcpSub.SubcommandArgs.Should().Equal("mcp", "list", "--json");
    }

    [Fact]
    public void BuildArguments_續接對話且工作資料夾不在對話資料夾內時額外傳add_dir_並支援settings()
    {
        // 續接且工作資料夾在對話資料夾之外：同時傳兩個 --add-dir
        AntigravityStreamRules.BuildArguments(
            "conv-1", "gemini-3.8-pro", "high", AgentPermissionMode.AcceptEdits, @"C:\Users\u\conv\c1",
            workingDirectory: @"C:\repo\project", settingsJson: """{"theme":"dark"}""")
            .Should().Equal(
                "--print=", "--input-format", "stream-json", "--output-format", "stream-json",
                "--conversation", "conv-1", "--model", "gemini-3.8-pro", "--effort", "high", "--mode", "accept-edits",
                "--add-dir", @"C:\Users\u\conv\c1", "--add-dir", @"C:\repo\project", "--settings", """{"theme":"dark"}""");

        // 工作資料夾已在對話資料夾底下（或相同）：不重複加第二個 --add-dir
        AntigravityStreamRules.BuildArguments(
            "conv-1", "gemini-3.8-pro", "high", AgentPermissionMode.AcceptEdits, @"C:\Users\u\conv\c1",
            workingDirectory: @"C:\Users\u\conv\c1\sub")
            .Should().Equal(
                "--print=", "--input-format", "stream-json", "--output-format", "stream-json",
                "--conversation", "conv-1", "--model", "gemini-3.8-pro", "--effort", "high", "--mode", "accept-edits",
                "--add-dir", @"C:\Users\u\conv\c1");

        // 新對話（conversationId 為 null）：CWD 本身就是工作區根目錄，不額外加第二個 --add-dir
        AntigravityStreamRules.BuildArguments(
            null, "gemini-3.8-pro", "high", AgentPermissionMode.AcceptEdits, @"C:\Users\u\conv\c1",
            workingDirectory: @"C:\repo\project")
            .Should().Equal(
                "--print=", "--input-format", "stream-json", "--output-format", "stream-json",
                "--model", "gemini-3.8-pro", "--effort", "high", "--mode", "accept-edits",
                "--add-dir", @"C:\Users\u\conv\c1");
    }

    [Fact]
    public void EvaluateEffort_Model_Hooks_Permissions_解析1_5_4無頭JSON輸出與外掛登入提示()
    {
        AntigravityStreamRules.DefaultModelEfforts.Should().Equal("low", "medium", "high", "xhigh", "max");
        AntigravityStreamRules.BuiltinWorkflowCommands.Select(c => c.Name).Should().Contain("swarm");

        var effort = AntigravityStreamRules.EvaluateEffortCommand(0,
            """{"status":"SUCCESS","command":{"name":"effort","data":{"adjustable":true,"current":"high","available":["low","medium","high","xhigh","max"]}}}""");
        effort.Should().NotBeNull();
        effort!.Adjustable.Should().BeTrue();
        effort.Current.Should().Be("high");
        effort.Available.Should().Equal("low", "medium", "high", "xhigh", "max");

        var model = AntigravityStreamRules.EvaluateModelCommand(0,
            """{"status":"SUCCESS","command":{"name":"model","data":{"id":"gemini-3-flash","label":"Gemini 3 Flash","effort":"high","is_default":true}}}""");
        model.Should().NotBeNull();
        model!.Id.Should().Be("gemini-3-flash");
        model.Label.Should().Be("Gemini 3 Flash");
        model.Effort.Should().Be("high");
        model.IsDefault.Should().BeTrue();

        var hooks = AntigravityStreamRules.EvaluateHooksCommand(0,
            """{"status":"SUCCESS","command":{"name":"hooks","data":{"hooks":[{"event":"PreToolUse","matcher":"run_command","type":"command","command":"echo pre","source":"user"}]}}}""");
        hooks.Should().ContainSingle().Which.Should().Be(
            new AntigravityStreamRules.HookReportEntry("PreToolUse", "echo pre", "run_command", "user"));

        var perms = AntigravityStreamRules.EvaluatePermissionsCommand(0,
            """{"status":"SUCCESS","command":{"name":"permissions","data":{"permissions":[{"scope":"user","allow":["Read"],"ask":["Bash(git push:*)"],"deny":["Write(.env)"]}]}}}""");
        perms.Should().ContainSingle();
        perms[0].Scope.Should().Be("user");
        perms[0].Allow.Should().Equal("Read");
        perms[0].Ask.Should().Equal("Bash(git push:*)");
        perms[0].Deny.Should().Equal("Write(.env)");

        AntigravityStreamRules.TryParsePluginSignInNotice("⚠ The linear plugin needs you to sign in. Open /plugin to sign in.", out var notice)
            .Should().BeTrue();
        notice.Should().Contain("linear").And.Contain("/plugin");
        AntigravityStreamRules.TryParsePluginSignInNotice("normal output", out _).Should().BeFalse();
    }

    [Fact]
    public void 計畫確認卡_組問題與解析使用者選擇為切換權限送Proceed或直接送修改意見()
    {
        var q = AntigravityStreamRules.BuildPlanFeedbackQuestion(@"C:\Users\u\.gemini\antigravity-cli\brain\c1\plan.md", "實作沙箱與逐字稿伴讀\n第二行忽略");
        q.Id.Should().Be(AntigravityStreamRules.PlanFeedbackQuestionId);
        q.AllowOther.Should().BeTrue();
        q.Text.Should().Contain("plan.md").And.Contain("實作沙箱與逐字稿伴讀").And.NotContain("第二行忽略");
        q.Options.Select(o => o.Label).Should().Equal(
            AntigravityStreamRules.PlanProceedSandboxOption,
            AntigravityStreamRules.PlanProceedAcceptEditsOption);

        var questions = new[] { q };
        AntigravityStreamRules.TryResolvePlanFeedbackDecision(
            questions,
            new Dictionary<string, IReadOnlyList<string>> { [q.Id] = new[] { AntigravityStreamRules.PlanProceedSandboxOption } },
            out var mode1, out var msg1).Should().BeTrue();
        mode1.Should().Be(AgentPermissionMode.Auto);
        msg1.Should().Be("Proceed");

        AntigravityStreamRules.TryResolvePlanFeedbackDecision(
            questions,
            new Dictionary<string, IReadOnlyList<string>> { [q.Id] = new[] { AntigravityStreamRules.PlanProceedAcceptEditsOption } },
            out var mode2, out var msg2).Should().BeTrue();
        mode2.Should().Be(AgentPermissionMode.AcceptEdits);
        msg2.Should().Be("Proceed");

        AntigravityStreamRules.TryResolvePlanFeedbackDecision(
            questions,
            new Dictionary<string, IReadOnlyList<string>> { [q.Id] = new[] { "第 2 步請改成非同步處理" } },
            out var mode3, out var msg3).Should().BeTrue();
        mode3.Should().BeNull("自訂修改意見時保留原來的規劃／權限模式");
        msg3.Should().Be("第 2 步請改成非同步處理");
    }
}
