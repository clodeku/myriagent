using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>Antigravity CLI 1.5.4+ <c>agy acp --stdio</c>（ACP JSON-RPC 2.0 雙向協定）純函式規則與跨 CLI 共用設定規則測試</summary>
public sealed class AntigravityAcpRulesTests
{
    [Fact]
    public void Initialize_宣告elicitation與terminal_auth_並解析版本_圖片能力_載入會話與登入方式()
    {
        AntigravityAcpRules.AgentArguments.Should().Equal("acp", "--stdio");

        var initParams = AntigravityAcpRules.BuildInitializeParams("請一律使用臺灣繁體中文");
        initParams.Value<int>("protocolVersion").Should().Be(1);
        initParams["clientCapabilities"]?["_meta"]?["elicitation"]?.Value<bool>("form").Should().BeTrue();
        initParams["clientCapabilities"]?["_meta"]?.Value<bool>("terminal-auth").Should().BeTrue();
        initParams["_meta"]?["antigravity"]?["systemPrompt"]?.Value<string>("append").Should().Be("請一律使用臺灣繁體中文");

        var initResponse = JObject.Parse("""
            {
              "protocolVersion": 1,
              "agentCapabilities": {
                "loadSession": true,
                "promptCapabilities": { "image": true }
              },
              "agentInfo": {
                "name": "antigravity-cli",
                "title": "Antigravity CLI",
                "version": "1.5.4-6774650932035584"
              },
              "authMethods": [
                {
                  "id": "antigravity-login",
                  "name": "Log in with Antigravity",
                  "description": "Run `agy login` in the terminal",
                  "type": "terminal"
                }
              ]
            }
            """);

        AntigravityAcpRules.ParseAgentVersion(initResponse).Should().Be("1.5.4-6774650932035584");
        AntigravityAcpRules.ParsePromptImages(initResponse).Should().BeTrue();
        AntigravityAcpRules.ParseSupportsLoadSession(initResponse).Should().BeTrue();
        AntigravityAcpRules.ParseAuthMethods(initResponse).Should().Equal(
            ("antigravity-login", "Log in with Antigravity", "Run `agy login` in the terminal"));
    }

    [Fact]
    public void SessionNew與Resume_組裝additionalDirectories与systemPrompt_解析模型_模式_configOptions與訂閱層級()
    {
        var newParams = AntigravityAcpRules.BuildNewSessionParams(
            @"C:\repo\app",
            additionalDirectories: new[] { @"C:\Users\u\conv\c1" },
            systemPromptAppend: "附加規則");
        newParams.Value<string>("cwd").Should().Be(@"C:\repo\app");
        newParams["_meta"]?["antigravity"]?["additionalDirectories"]?.ToObject<string[]>().Should().Equal(@"C:\Users\u\conv\c1");
        newParams["_meta"]?["antigravity"]?["systemPrompt"]?.Value<string>("append").Should().Be("附加規則");

        var resumeParams = AntigravityAcpRules.BuildResumeParams("sess-123", @"C:\repo\app", new[] { @"C:\Users\u\conv\c1" });
        resumeParams.Value<string>("sessionId").Should().Be("sess-123");
        resumeParams["_meta"]?["antigravity"]?["additionalDirectories"]?.ToObject<string[]>().Should().Equal(@"C:\Users\u\conv\c1");

        var sessionResponse = JObject.Parse("""
            {
              "sessionId": "11635df9-a2fb-4138-9c6b-c93b1c4acfb6",
              "models": {
                "currentModelId": "gemini-3-flash",
                "availableModels": [
                  { "modelId": "gemini-3-flash", "name": "Gemini 3 Flash" },
                  { "modelId": "claude-sonnet-4-6", "name": "Claude Sonnet 4.6" }
                ]
              },
              "modes": {
                "currentModeId": "default",
                "availableModes": [
                  { "id": "default", "name": "Default", "description": "Prompts for permission" },
                  { "id": "accept-edits", "name": "Accept Edits", "description": "Auto-accepts file edits" },
                  { "id": "plan", "name": "Plan", "description": "Read-only mode" },
                  { "id": "auto", "name": "Auto", "description": "Auto-approves all tool executions" }
                ]
              },
              "configOptions": [
                {
                  "id": "thought_level",
                  "name": "Thinking Level",
                  "category": "thought_level",
                  "currentValue": "high",
                  "options": [
                    { "value": "low", "name": "low" },
                    { "value": "medium", "name": "medium" },
                    { "value": "high", "name": "high" }
                  ]
                }
              ],
              "_meta": {
                "antigravity": {
                  "tier": {
                    "id": "g1-ultra-tier",
                    "name": "Google AI Ultra",
                    "description": "Google AI Ultra"
                  }
                }
              }
            }
            """);

        AntigravityAcpRules.ParseSessionId(sessionResponse).Should().Be("11635df9-a2fb-4138-9c6b-c93b1c4acfb6");
        AntigravityAcpRules.ParseSubscriptionTier(sessionResponse).Should().Be(
            new AntigravitySubscriptionTier("g1-ultra-tier", "Google AI Ultra", "Google AI Ultra"));

        var (currentModel, models) = AntigravityAcpRules.ParseSessionModels(sessionResponse);
        currentModel.Should().Be("gemini-3-flash");
        models.Select(m => (m.Value, m.Label)).Should().Equal(
            ("gemini-3-flash", "Gemini 3 Flash"),
            ("claude-sonnet-4-6", "Claude Sonnet 4.6"));

        var (currentMode, modes) = AntigravityAcpRules.ParseSessionModes(sessionResponse);
        currentMode.Should().Be("default");
        modes.Select(m => m.Id).Should().Equal("default", "accept-edits", "plan", "auto");

        var cfgOpts = AntigravityAcpRules.ParseSessionConfigOptions(sessionResponse);
        cfgOpts.Should().ContainSingle();
        cfgOpts[0].Id.Should().Be("thought_level");
        cfgOpts[0].CurrentValue.Should().Be("high");
        cfgOpts[0].Choices.Select(c => c.Value).Should().Equal("low", "medium", "high");
    }

    [Fact]
    public void 即時切換模式_模型_推理強度_與Prompt內嵌圖片及Cancel()
    {
        AntigravityAcpRules.SessionModeId(AgentPermissionMode.Ask).Should().Be("default");
        AntigravityAcpRules.SessionModeId(AgentPermissionMode.AcceptEdits).Should().Be("accept-edits");
        AntigravityAcpRules.SessionModeId(AgentPermissionMode.Auto).Should().Be("auto");
        AntigravityAcpRules.SessionModeId(AgentPermissionMode.FullAccess).Should().Be("auto");
        AntigravityAcpRules.SessionModeId(AgentPermissionMode.Plan).Should().Be("plan");

        AntigravityAcpRules.BuildSetModeParams("s1", AgentPermissionMode.Plan)
            .Should().BeEquivalentTo(JObject.Parse("""{"sessionId":"s1","modeId":"plan"}"""));
        AntigravityAcpRules.BuildSetModelParams("s1", "claude-opus-4-6-thinking")
            .Should().BeEquivalentTo(JObject.Parse("""{"sessionId":"s1","modelId":"claude-opus-4-6-thinking"}"""));
        AntigravityAcpRules.BuildSetConfigOptionParams("s1", "thought_level", "max")
            .Should().BeEquivalentTo(JObject.Parse("""{"sessionId":"s1","configId":"thought_level","value":"max"}"""));
        AntigravityAcpRules.BuildCancelParams("s1")
            .Should().BeEquivalentTo(JObject.Parse("""{"sessionId":"s1"}"""));

        var attachments = new List<AgentAttachment>
        {
            new() { Kind = AgentAttachmentKind.Image, FileName = "ui.png", StoredPath = @"C:\att\ui.png", ModelImagePaths = { @"C:\att\ui.png" } },
        };
        var promptParams = AntigravityAcpRules.BuildPromptParams(
            "s1", null, null, "請檢查這張截圖", attachments, supportsInlineImages: true, readFile: _ => new byte[] { 1, 2, 3 });
        var blocks = (JArray)promptParams["prompt"]!;
        blocks.Count.Should().Be(2);
        blocks[0]!["type"]!.Value<string>().Should().Be("image");
        blocks[0]!["mimeType"]!.Value<string>().Should().Be("image/png");
    }

    [Fact]
    public void RequestPermission與ElicitationCreate_互動核准與結構化問題卡片轉換()
    {
        var permJson = JObject.Parse("""
            {
              "sessionId": "s1",
              "toolCall": {
                "toolCallId": "call-42",
                "title": "Run command: git status",
                "kind": "execute",
                "rawInput": { "CommandLine": "git status" }
              },
              "options": [
                { "optionId": "allow_once", "name": "Allow once", "kind": "allow_once" },
                { "optionId": "allow_always", "name": "Always allow", "kind": "allow_always" },
                { "optionId": "reject_once", "name": "Reject", "kind": "reject_once" }
              ]
            }
            """);

        var permReq = AntigravityAcpRules.ParsePermissionRequest(permJson);
        permReq.Should().NotBeNull();
        permReq!.ToolCallId.Should().Be("call-42");
        permReq.Kind.Should().Be("bash");
        permReq.CommandOrPath.Should().Be("git status");

        AntigravityAcpRules.BuildPermissionOutcome(AgentApprovalChoice.Allow, permReq)
            .Should().BeEquivalentTo(JObject.Parse("""{"outcome":{"outcome":"selected","optionId":"allow_once"}}"""));
        AntigravityAcpRules.BuildPermissionOutcome(AgentApprovalChoice.AllowForSession, permReq)
            .Should().BeEquivalentTo(JObject.Parse("""{"outcome":{"outcome":"selected","optionId":"allow_always"}}"""));
        AntigravityAcpRules.BuildPermissionOutcome(AgentApprovalChoice.Deny, permReq)
            .Should().BeEquivalentTo(JObject.Parse("""{"outcome":{"outcome":"selected","optionId":"reject_once"}}"""));

        var elicitationJson = JObject.Parse("""
            {
              "mode": "form",
              "message": "請選擇部署環境與附加元件",
              "requestedSchema": {
                "type": "object",
                "properties": {
                  "env": {
                    "type": "string",
                    "title": "部署環境",
                    "description": "要部署到哪個環境？",
                    "enum": ["(Recommended) staging", "production"]
                  },
                  "addons": {
                    "type": "array",
                    "title": "附加元件",
                    "description": "要啟用哪些元件？",
                    "items": {
                      "type": "string",
                      "enum": ["redis", "cdn"]
                    }
                  }
                }
              }
            }
            """);

        var questions = AntigravityAcpRules.ParseElicitationQuestions(elicitationJson);
        questions.Should().HaveCount(2);
        questions[0].Id.Should().Be("env");
        questions[0].MultiSelect.Should().BeFalse();
        questions[0].Options.Select(o => o.Label).Should().Equal("(Recommended) staging", "production");
        questions[1].Id.Should().Be("addons");
        questions[1].MultiSelect.Should().BeTrue();
        questions[1].Options.Select(o => o.Label).Should().Equal("redis", "cdn");

        var accept = AntigravityAcpRules.BuildElicitationAccept(
            questions,
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["env"] = new[] { "(Recommended) staging" },
                ["addons"] = new[] { "redis", "cdn" },
            });
        accept.Should().BeEquivalentTo(JObject.Parse("""
            {
              "action": "accept",
              "content": {
                "env": "(Recommended) staging",
                "addons": ["redis", "cdn"]
              }
            }
            """));

        // session_info_update 對話標題更新
        var infoUpdate = JObject.Parse("""{"sessionUpdate":"session_info_update","title":"整合 Antigravity 1.5.4 功能","updatedAt":"2026-11-11T01:00:00Z"}""");
        AntigravityAcpRules.TryParseSessionInfoUpdate(infoUpdate, out var title, out var updatedAt).Should().BeTrue();
        title.Should().Be("整合 Antigravity 1.5.4 功能");
        updatedAt.Should().Be("2026-11-11T01:00:00Z");
    }

    [Fact]
    public void SharedCustomizationRules_保留內建技能名稱_並安全合併Antigravity_MCP設定()
    {
        SharedCustomizationRules.IsSharedSkill(AgentProvider.Antigravity, "swarm").Should().BeFalse();
        SharedCustomizationRules.IsSharedSkill(AgentProvider.Antigravity, "agy-customizations").Should().BeFalse();
        SharedCustomizationRules.IsSharedSkill(AgentProvider.Antigravity, "antigravity-guide").Should().BeFalse();
        SharedCustomizationRules.IsSharedSkill(AgentProvider.Antigravity, "my-custom-skill").Should().BeTrue();

        var existingMcp = JObject.Parse("""
            {
              "mcpServers": {
                "playwright": { "command": "npx", "disabled": true }
              }
            }
            """);
        var sharedServers = JObject.Parse("""
            {
              "playwright": { "command": "npx", "args": ["@playwright/mcp"] },
              "github": { "command": "gh-mcp" }
            }
            """);

        var merged = SharedCustomizationRules.BuildAntigravityMcpConfig(existingMcp, sharedServers);
        merged.Should().NotBeNull();
        merged!["mcpServers"]!["playwright"]!.Value<bool>("disabled").Should().BeTrue("既有已設定（含 disabled: true）的同名伺服器不可被覆蓋");
        merged["mcpServers"]!["github"]!.Value<string>("command").Should().Be("gh-mcp");
    }
}
