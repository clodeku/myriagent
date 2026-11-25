using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>Antigravity ACP（<c>agy acp --stdio</c>）回報的訂閱方案層級（<c>_meta.antigravity.tier</c>）</summary>
internal sealed record AntigravitySubscriptionTier(string Id, string Name, string? Description);

/// <summary>Antigravity ACP 的單一設定選項（<c>configOptions</c>：<c>model</c>、<c>thought_level</c>、<c>mode</c>）</summary>
internal sealed record AntigravityAcpConfigChoice(string Value, string Name, string? Description);

/// <summary>Antigravity ACP 的設定欄位定義（<c>session/new</c> 或 <c>config_option_update</c> 回傳）</summary>
internal sealed record AntigravityAcpConfigOption(
    string Id,
    string? Name,
    string? Category,
    string? CurrentValue,
    IReadOnlyList<AntigravityAcpConfigChoice> Choices);

/// <summary>Antigravity ACP 的 <c>session/request_permission</c> 核准請求解析結果</summary>
internal sealed record AntigravityPermissionRequest(
    string SessionId,
    string ToolCallId,
    string Title,
    string Kind,
    string? CommandOrPath,
    string? Detail,
    string AllowOnceOptionId,
    string AllowAlwaysOptionId,
    string RejectOnceOptionId);

/// <summary>
/// Antigravity CLI 1.5.4+ <c>agy acp --stdio</c>（Agent Client Protocol，JSON-RPC 2.0 NDJSON）的純函式規則：
/// <list type="bullet">
/// <item>與 <see cref="GrokAcpRules"/> 共用 JSON-RPC 2.0 封包格式（<c>BuildRequest</c>、<c>BuildResponse</c>、<c>ClassifyMessage</c>）</item>
/// <item>支援 <c>initialize</c>（含 <c>_meta.antigravity.systemPrompt.append</c> 與 <c>_meta.elicitation.form</c>）、<c>session/new</c>、<c>session/resume</c>、<c>session/load</c></item>
/// <item>支援對話中即時切換模型（<c>session/set_model</c>）、推理強度（<c>session/set_config_option</c> <c>thought_level</c>）與權限模式（<c>session/set_mode</c>：<c>default</c>／<c>accept-edits</c>／<c>auto</c>／<c>plan</c>）</item>
/// <item>支援 inline base64 圖片訊息（<c>session/prompt</c>）、單發取消（<c>session/cancel</c>，不需殺掉常駐程序）</item>
/// <item>支援互動式工具權限核准（<c>session/request_permission</c> → <c>allow_once</c>／<c>allow_always</c>／<c>reject_once</c>）与結構化表單提問（<c>elicitation/create</c> → <see cref="AgentQuestion"/>）</item>
/// <item>支援對話標題自動更新通知（<c>session/update</c> 的 <c>session_info_update</c>）</item>
/// </list>
/// </summary>
internal static class AntigravityAcpRules
{
    public const int ProtocolVersion = 1;

    /// <summary>啟動 Antigravity ACP 雙向 stdio 伺服器的預設參數</summary>
    public static IReadOnlyList<string> AgentArguments { get; } = new[] { "acp", "--stdio" };

    /// <summary>停止時還在執行的工具卡說明</summary>
    public const string CancelledToolOutput = "已停止";

    /// <summary>權限選項預設代碼（當 request_permission 未附 options 時使用）</summary>
    public const string DefaultAllowOnceOptionId = "allow_once";
    public const string DefaultAllowAlwaysOptionId = "allow_always";
    public const string DefaultRejectOnceOptionId = "reject_once";

    /// <summary>
    /// 組裝 <c>agy acp --stdio</c> 啟動參數：新對話且非精簡模式時可指定 <c>--agent</c>，精簡模式關閉預設自訂項，並以 <c>--add-dir</c> 加入對話與共用記憶資料夾
    /// </summary>
    public static List<string> BuildAgentArguments(
        string? conversationDirectory = null,
        bool useLocalCustomizations = true,
        string? agent = null,
        string? sharedMemoryDirectory = null,
        bool isResume = false)
    {
        var args = new List<string> { "acp", "--stdio" };
        if (!isResume && useLocalCustomizations && AntigravityStreamRules.NormalizeAgent(agent) is { } normalizedAgent)
        {
            args.Add("--agent");
            args.Add(normalizedAgent);
        }
        if (!useLocalCustomizations)
            args.Add("--include-default-customizations=false");
        if (!string.IsNullOrWhiteSpace(conversationDirectory))
        {
            args.Add("--add-dir");
            args.Add(conversationDirectory);
        }
        if (!string.IsNullOrWhiteSpace(sharedMemoryDirectory))
        {
            args.Add("--add-dir");
            args.Add(sharedMemoryDirectory);
        }
        return args;
    }

    /// <summary>
    /// ACP 程序的重開簽章：模型、推理強度與權限模式皆在程序內以 JSON-RPC 即時切換，不必放進簽章；
    /// 只有帳戶、執行檔、工作資料夾、對話資料夾、精簡模式、自訂代理或自動壓縮上限改變時才重開程序
    /// </summary>
    public static string Signature(
        string accountKey,
        string? launcher,
        string workingDirectory,
        string conversationDirectory,
        bool useLocalCustomizations = true,
        string? agent = null,
        long? autoCompactTokens = null)
        => string.Join("|",
               accountKey,
               launcher,
               workingDirectory,
               conversationDirectory,
               useLocalCustomizations ? "1" : "0",
               useLocalCustomizations ? AntigravityStreamRules.NormalizeAgent(agent) : null)
           + (autoCompactTokens is { } ac ? $"|ac:{ac}" : string.Empty);

    /// <summary>ACP 錯誤是否代表尚未登入 Google／Antigravity</summary>
    public static bool IsAuthRequired(GrokAcpError? error)
    {
        if (error == null)
            return false;
        if (error.Code == -32000)
            return true;
        var text = error.Text ?? string.Empty;
        return text.Contains("Authentication required", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Please sign in", StringComparison.OrdinalIgnoreCase)
               || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
               || text.Contains("visit the URL to log in", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ACP <c>session/resume</c> 或 <c>session/load</c> 找不到指定對話（實測 agy 1.5.17：<c>session/load</c> 回 <c>-32002 Session not found</c>，
    /// <c>session/resume</c> 回 <c>-32603 Failed to read session ...; retry, or load it with session/load</c>）
    /// </summary>
    public static bool IsSessionNotFound(GrokAcpError? error)
    {
        if (error == null)
            return false;
        if (error.Code == -32002)
            return true;
        var text = error.Text ?? string.Empty;
        return text.Contains("Session not found", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Failed to read session", StringComparison.OrdinalIgnoreCase)
               || AntigravityStreamRules.IsConversationNotFound(text);
    }

    // ───────────────────────────── initialize ─────────────────────────────

    /// <summary>
    /// <c>initialize</c> 參數：宣告支援 <c>elicitation.form</c> 與 <c>terminal-auth</c>；
    /// 檔案讀寫與終端機設為 false，讓 agy 使用內建工具執行並透過 <c>session/request_permission</c> 請求核准
    /// </summary>
    public static JObject BuildInitializeParams(string? systemPromptAppend = null)
    {
        var obj = new JObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["clientInfo"] = new JObject
            {
                ["name"] = "Clode AI Agent",
                ["version"] = "1.0",
            },
            ["clientCapabilities"] = new JObject
            {
                ["fs"] = new JObject
                {
                    ["readTextFile"] = false,
                    ["writeTextFile"] = false,
                },
                ["terminal"] = false,
                ["auth"] = new JObject
                {
                    ["terminal"] = true,
                },
                ["_meta"] = new JObject
                {
                    ["elicitation"] = new JObject
                    {
                        ["form"] = true,
                    },
                    ["terminal-auth"] = true,
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(systemPromptAppend))
        {
            obj["_meta"] = new JObject
            {
                ["antigravity"] = new JObject
                {
                    ["systemPrompt"] = new JObject
                    {
                        ["append"] = systemPromptAppend.Trim(),
                    },
                },
            };
        }

        return obj;
    }

    public static string? ParseAgentVersion(JObject? result)
        => result?["agentInfo"]?.Value<string>("version") is { Length: > 0 } v ? v : null;

    public static bool ParsePromptImages(JObject? result)
        => result?["agentCapabilities"]?["promptCapabilities"]?.Value<bool>("image") == true;

    public static bool ParseSupportsLoadSession(JObject? result)
        => result?["agentCapabilities"]?.Value<bool>("loadSession") == true;

    /// <summary>initialize 的 authMethods 清單（例如 antigravity-login）</summary>
    public static IReadOnlyList<(string Id, string Name, string? Description)> ParseAuthMethods(JObject? result)
    {
        if (result?["authMethods"] is not JArray arr)
            return Array.Empty<(string, string, string?)>();
        var list = new List<(string, string, string?)>();
        foreach (var item in arr.OfType<JObject>())
        {
            var id = item.Value<string>("id")?.Trim();
            if (string.IsNullOrEmpty(id))
                continue;
            var name = item.Value<string>("name")?.Trim();
            var desc = item.Value<string>("description")?.Trim();
            list.Add((id, string.IsNullOrEmpty(name) ? id : name, string.IsNullOrEmpty(desc) ? null : desc));
        }
        return list;
    }

    // ───────────────────────────── session/new、session/resume、session/load ─────────────────────────────

    public static JObject BuildNewSessionParams(
        string cwd,
        IReadOnlyList<string>? additionalDirectories = null,
        string? systemPromptAppend = null,
        JArray? mcpServers = null)
    {
        var obj = new JObject
        {
            ["cwd"] = cwd,
            ["mcpServers"] = mcpServers != null ? (JArray)mcpServers.DeepClone() : new JArray(),
        };

        var agyMeta = BuildAntigravitySessionMeta(additionalDirectories, systemPromptAppend);
        if (agyMeta != null)
            obj["_meta"] = new JObject { ["antigravity"] = agyMeta };

        return obj;
    }

    public static JObject BuildResumeParams(
        string sessionId,
        string cwd,
        IReadOnlyList<string>? additionalDirectories = null,
        JArray? mcpServers = null)
    {
        var obj = new JObject
        {
            ["sessionId"] = sessionId,
            ["cwd"] = cwd,
            ["mcpServers"] = mcpServers != null ? (JArray)mcpServers.DeepClone() : new JArray(),
        };

        var agyMeta = BuildAntigravitySessionMeta(additionalDirectories, systemPromptAppend: null);
        if (agyMeta != null)
            obj["_meta"] = new JObject { ["antigravity"] = agyMeta };

        return obj;
    }

    private static JObject? BuildAntigravitySessionMeta(IReadOnlyList<string>? additionalDirectories, string? systemPromptAppend)
    {
        JObject? agy = null;
        if (additionalDirectories is { Count: > 0 })
        {
            var dirs = new JArray();
            foreach (var dir in additionalDirectories)
            {
                if (!string.IsNullOrWhiteSpace(dir))
                    dirs.Add(dir.Trim());
            }
            if (dirs.Count > 0)
            {
                agy ??= new JObject();
                agy["additionalDirectories"] = dirs;
            }
        }
        if (!string.IsNullOrWhiteSpace(systemPromptAppend))
        {
            agy ??= new JObject();
            agy["systemPrompt"] = new JObject { ["append"] = systemPromptAppend.Trim() };
        }
        return agy;
    }

    public static string? ParseSessionId(JObject? result)
        => result?.Value<string>("sessionId") is { Length: > 0 } s ? s : null;

    /// <summary>解析 <c>session/new</c> 或 <c>session/resume</c> 回應中的 <c>_meta.antigravity.tier</c> 訂閱方案資訊</summary>
    public static AntigravitySubscriptionTier? ParseSubscriptionTier(JObject? result)
    {
        if (result?["_meta"]?["antigravity"]?["tier"] is not JObject tier)
            return null;
        var id = tier.Value<string>("id")?.Trim();
        var name = tier.Value<string>("name")?.Trim();
        if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name))
            return null;
        var desc = tier.Value<string>("description")?.Trim();
        return new AntigravitySubscriptionTier(
            id ?? name!,
            name ?? id!,
            string.IsNullOrEmpty(desc) ? null : desc);
    }

    /// <summary>
    /// 解析 <c>session/new</c> 或 <c>session/resume</c> 回應的 <c>models</c>（<c>currentModelId</c> 與 <c>availableModels</c>）：
    /// 同一個模型若帶 <c>-low/-medium/-high/-xhigh/-max</c> 後綴（實測 agy 1.5.17 <c>availableModels</c>），自動合併為基本 ID 與可選強度清單；
    /// Orchestrator 雙模型不附強度選單；本來就沒有後綴的一般模型維持預設強度清單
    /// </summary>
    public static (string? CurrentModelId, IReadOnlyList<AgentModelOption> AvailableModels) ParseSessionModels(JObject? result)
    {
        var modelsObj = result?["models"] as JObject;
        if (modelsObj == null)
            return (null, Array.Empty<AgentModelOption>());
        var rawCurrent = modelsObj.Value<string>("currentModelId")?.Trim();
        string? current = string.IsNullOrEmpty(rawCurrent) ? null : StripEffortSuffix(rawCurrent, out _);

        var groups = new List<(string Id, string Label, string? Description, List<string> Efforts, bool HadSuffix)>();
        if (modelsObj["availableModels"] is JArray arr)
        {
            foreach (var item in arr.OfType<JObject>())
            {
                var id = item.Value<string>("modelId")?.Trim();
                if (string.IsNullOrEmpty(id))
                    continue;
                var name = item.Value<string>("name")?.Trim();
                var desc = item.Value<string>("description")?.Trim();
                var baseId = StripEffortSuffix(id, out var effort);
                var cleanLabel = string.IsNullOrEmpty(name)
                    ? baseId
                    : effort != null ? StripEffortLabel(name) : name;
                var idx = groups.FindIndex(g => string.Equals(g.Id, baseId, StringComparison.Ordinal));
                if (idx < 0)
                {
                    groups.Add((
                        baseId,
                        cleanLabel.Length > 0 ? cleanLabel : baseId,
                        string.IsNullOrEmpty(desc) ? null : desc,
                        new List<string>(),
                        effort != null));
                    idx = groups.Count - 1;
                }
                if (effort != null && !groups[idx].Efforts.Contains(effort))
                    groups[idx].Efforts.Add(effort);
            }
        }

        var list = groups.Select(g =>
        {
            IReadOnlyList<string> efforts;
            if (AntigravityStreamRules.IsOrchestratorModel(g.Id))
                efforts = Array.Empty<string>();
            else if (g.Efforts.Count > 0)
                efforts = OrderEfforts(g.Efforts);
            else
                efforts = AntigravityStreamRules.DefaultModelEfforts;
            return new AgentModelOption(g.Id, g.Label, g.Description, efforts);
        }).ToList();

        return (current, list);
    }

    private static readonly string[] EffortRank = { "minimal", "low", "medium", "high", "xhigh", "max" };

    private static string[] OrderEfforts(List<string> efforts)
        => efforts.OrderBy(e => Array.IndexOf(EffortRank, e) is >= 0 and var i ? i : int.MaxValue).ToArray();

    private static string StripEffortSuffix(string modelId, out string? effort)
    {
        effort = null;
        var dash = modelId.LastIndexOf('-');
        if (dash <= 0 || dash >= modelId.Length - 1)
            return modelId;
        var tail = modelId[(dash + 1)..].ToLowerInvariant();
        if (Array.IndexOf(EffortRank, tail) < 0)
            return modelId;
        effort = tail;
        return modelId[..dash];
    }

    private static string StripEffortLabel(string label)
    {
        var open = label.LastIndexOf('(');
        if (open > 0 && label.EndsWith(')'))
        {
            var inner = label[(open + 1)..^1].Trim().ToLowerInvariant();
            if (inner is "minimal" or "low" or "medium" or "high" or "xhigh" or "max" or "extra high" or "extra")
                return label[..open].TrimEnd();
        }
        return label;
    }

    /// <summary>
    /// 依 ACP <c>configOptions</c> 解析實際要送給 <c>session/set_model</c> 的模型 ID 與 <c>thought_level</c> 強度（實測 gpt-oss-120b-medium 在 configOptions 為完整 ID，
    /// 而 gemini-4-neon 等為無後綴 ID、Orchestrator 則不含 thought_level）
    /// </summary>
    public static (string? ModelId, string? Effort) ResolveModelAndEffort(
        string? requestedModel,
        string? requestedEffort,
        IReadOnlyList<AntigravityAcpConfigOption>? configOptions)
    {
        var normalized = AntigravityStreamRules.NormalizeModel(requestedModel);
        string? extractedEffort = null;
        string? modelToSend = normalized;
        if (normalized != null)
        {
            var modelOpt = configOptions?.FirstOrDefault(c => string.Equals(c.Id, "model", StringComparison.OrdinalIgnoreCase));
            if (modelOpt != null && modelOpt.Choices.Any(c => string.Equals(c.Value, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                modelToSend = normalized;
            }
            else
            {
                var stripped = StripEffortSuffix(normalized, out extractedEffort);
                if (modelOpt == null || modelOpt.Choices.Count == 0 || modelOpt.Choices.Any(c => string.Equals(c.Value, stripped, StringComparison.OrdinalIgnoreCase)))
                {
                    modelToSend = stripped;
                }
                else
                {
                    var withEffort = modelOpt.Choices.FirstOrDefault(c => c.Value.StartsWith(stripped + "-", StringComparison.OrdinalIgnoreCase));
                    if (withEffort != null)
                        modelToSend = withEffort.Value;
                }
            }
        }

        var desiredEffort = !string.IsNullOrWhiteSpace(requestedEffort) ? requestedEffort.Trim().ToLowerInvariant() : extractedEffort;
        if (string.IsNullOrWhiteSpace(desiredEffort) || !AntigravityStreamRules.SupportsEffortArgument(modelToSend))
            return (modelToSend, null);

        if (configOptions is { Count: > 0 })
        {
            var thoughtOpt = configOptions.FirstOrDefault(c => string.Equals(c.Id, "thought_level", StringComparison.OrdinalIgnoreCase));
            if (thoughtOpt == null)
                return (modelToSend, null);
            if (thoughtOpt.Choices.Count > 0 && !thoughtOpt.Choices.Any(c => string.Equals(c.Value, desiredEffort, StringComparison.OrdinalIgnoreCase)))
                return (modelToSend, null);
        }

        return (modelToSend, desiredEffort);
    }

    /// <summary>解析 <c>session/new</c> 回應的 <c>modes</c>（<c>currentModeId</c> 與 <c>availableModes</c>）</summary>
    public static (string? CurrentModeId, IReadOnlyList<(string Id, string Name, string? Description)> AvailableModes) ParseSessionModes(JObject? result)
    {
        var modesObj = result?["modes"] as JObject;
        if (modesObj == null)
            return (null, Array.Empty<(string, string, string?)>());
        var current = modesObj.Value<string>("currentModeId")?.Trim();
        var list = new List<(string, string, string?)>();
        if (modesObj["availableModes"] is JArray arr)
        {
            foreach (var item in arr.OfType<JObject>())
            {
                var id = item.Value<string>("id")?.Trim();
                if (string.IsNullOrEmpty(id))
                    continue;
                var name = item.Value<string>("name")?.Trim();
                var desc = item.Value<string>("description")?.Trim();
                list.Add((id, string.IsNullOrEmpty(name) ? id : name, string.IsNullOrEmpty(desc) ? null : desc));
            }
        }
        return (string.IsNullOrEmpty(current) ? null : current, list);
    }

    /// <summary>解析 <c>session/new</c> 或 <c>config_option_update</c> 的 <c>configOptions</c>（<c>model</c>、<c>thought_level</c>、<c>mode</c>）</summary>
    public static IReadOnlyList<AntigravityAcpConfigOption> ParseSessionConfigOptions(JObject? obj)
    {
        var arr = obj?["configOptions"] as JArray;
        if (arr == null)
            return Array.Empty<AntigravityAcpConfigOption>();
        var result = new List<AntigravityAcpConfigOption>();
        foreach (var item in arr.OfType<JObject>())
        {
            var id = item.Value<string>("id")?.Trim();
            if (string.IsNullOrEmpty(id))
                continue;
            var name = item.Value<string>("name")?.Trim();
            var category = item.Value<string>("category")?.Trim();
            var currentVal = item.Value<string>("currentValue")?.Trim();
            var choices = new List<AntigravityAcpConfigChoice>();
            if (item["options"] is JArray opts)
            {
                foreach (var opt in opts.OfType<JObject>())
                {
                    var val = opt.Value<string>("value")?.Trim();
                    if (string.IsNullOrEmpty(val))
                        continue;
                    var optName = opt.Value<string>("name")?.Trim();
                    var optDesc = opt.Value<string>("description")?.Trim();
                    choices.Add(new AntigravityAcpConfigChoice(
                        val,
                        string.IsNullOrEmpty(optName) ? val : optName,
                        string.IsNullOrEmpty(optDesc) ? null : optDesc));
                }
            }
            result.Add(new AntigravityAcpConfigOption(id, name, category, currentVal, choices));
        }
        return result;
    }

    // ───────────────────────────── 即時切換模式、模型與推理強度 ─────────────────────────────

    /// <summary>
    /// app 的權限模式對應 Antigravity ACP 的 <c>modeId</c>：
    /// <list type="bullet">
    /// <item><see cref="AgentPermissionMode.Ask"/> → <c>default</c>（檔案編輯與執行指令皆透過 <c>session/request_permission</c> 詢問）</item>
    /// <item><see cref="AgentPermissionMode.AcceptEdits"/> → <c>accept-edits</c>（自動接受工作區內編輯，指令詢問）</item>
    /// <item><see cref="AgentPermissionMode.Auto"/>／<see cref="AgentPermissionMode.FullAccess"/> → <c>auto</c>（自動核准所有工具）</item>
    /// <item><see cref="AgentPermissionMode.Plan"/> → <c>plan</c>（唯讀規劃模式）</item>
    /// </list>
    /// </summary>
    public static string SessionModeId(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.Ask => "default",
        AgentPermissionMode.AcceptEdits => "accept-edits",
        AgentPermissionMode.Auto or AgentPermissionMode.FullAccess => "auto",
        AgentPermissionMode.Plan => "plan",
        _ => "accept-edits",
    };

    /// <summary><c>session/set_mode</c> 參數（不重開程序即時切換權限模式）</summary>
    public static JObject BuildSetModeParams(string sessionId, AgentPermissionMode mode)
        => new()
        {
            ["sessionId"] = sessionId,
            ["modeId"] = SessionModeId(mode),
        };

    /// <summary><c>session/set_model</c> 參數（不重開程序即時切換模型）</summary>
    public static JObject BuildSetModelParams(string sessionId, string modelId)
        => new()
        {
            ["sessionId"] = sessionId,
            ["modelId"] = modelId,
        };

    /// <summary><c>session/set_config_option</c> 參數（<c>configId</c> 為 <c>model</c>、<c>thought_level</c> 或 <c>mode</c>）</summary>
    public static JObject BuildSetConfigOptionParams(string sessionId, string configId, string value)
        => new()
        {
            ["sessionId"] = sessionId,
            ["configId"] = configId,
            ["value"] = value,
        };

    // ───────────────────────────── session/prompt 與 session/cancel ─────────────────────────────

    /// <summary>
    /// <c>session/prompt</c> 參數：支援 inline base64 圖片區塊（<c>type: "image"</c>）與文字區塊（<c>type: "text"</c>）
    /// </summary>
    public static JObject BuildPromptParams(
        string sessionId,
        string? instructions,
        string? historyRecap,
        string? text,
        IReadOnlyList<AgentAttachment> attachments,
        bool supportsInlineImages = true,
        Func<string, byte[]>? readFile = null,
        string? workingDirectoryNotice = null)
    {
        var prompt = new JArray();
        var reader = readFile ?? File.ReadAllBytes;
        bool inlinedAnyImage = false;
        int imageCount = 0;
        long totalBytes = 0;
        int skippedImages = 0;
        const int MaxImages = 20;
        const long MaxBytes = 20L * 1024 * 1024;

        if (supportsInlineImages && attachments is { Count: > 0 })
        {
            foreach (var att in attachments)
            {
                foreach (var imgPath in att.ModelImagePaths)
                {
                    if (string.IsNullOrWhiteSpace(imgPath))
                        continue;
                    try
                    {
                        var bytes = reader(imgPath);
                        if (imageCount >= MaxImages || totalBytes + bytes.LongLength > MaxBytes)
                        {
                            skippedImages++;
                            continue;
                        }
                        var ext = Path.GetExtension(imgPath).ToLowerInvariant();
                        var mime = ext switch
                        {
                            ".jpg" or ".jpeg" => "image/jpeg",
                            ".gif" => "image/gif",
                            ".webp" => "image/webp",
                            _ => "image/png",
                        };
                        prompt.Add(new JObject
                        {
                            ["type"] = "image",
                            ["mimeType"] = mime,
                            ["data"] = Convert.ToBase64String(bytes),
                        });
                        inlinedAnyImage = true;
                        imageCount++;
                        totalBytes += bytes.LongLength;
                    }
                    catch
                    {
                    }
                }
            }
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(instructions))
            parts.Add($"<myriagent_instructions>\n{instructions.Trim()}\n</myriagent_instructions>");
        if (!string.IsNullOrWhiteSpace(historyRecap))
            parts.Add(historyRecap.Trim());
        if (!string.IsNullOrWhiteSpace(workingDirectoryNotice))
            parts.Add(workingDirectoryNotice);
        var body = (text ?? string.Empty) + AgentAttachmentRules.BuildAttachmentNote(attachments, imagesInline: inlinedAnyImage);
        if (skippedImages > 0)
            body += $"\n（另有 {skippedImages} 張圖片超過單則訊息上限未直接附上，可用讀取工具查看上列路徑）";
        parts.Add(body);

        prompt.Add(new JObject
        {
            ["type"] = "text",
            ["text"] = string.Join("\n\n", parts),
        });

        return new JObject
        {
            ["sessionId"] = sessionId,
            ["prompt"] = prompt,
        };
    }

    /// <summary><c>session/cancel</c> 通知參數：中斷進行中的一輪而不殺掉常駐程序</summary>
    public static JObject BuildCancelParams(string sessionId)
        => new() { ["sessionId"] = sessionId };

    // ───────────────────────────── session/request_permission 互動核准 ─────────────────────────────

    /// <summary>
    /// 解析 agy 發來的 <c>session/request_permission</c> 請求：提取工具呼叫 ID、標題、種類、指令／路徑與核准選項 ID
    /// </summary>
    public static AntigravityPermissionRequest? ParsePermissionRequest(JObject? @params)
    {
        if (@params == null)
            return null;
        var sessionId = @params.Value<string>("sessionId") ?? string.Empty;
        var toolCall = @params["toolCall"] as JObject;
        var toolCallId = toolCall?.Value<string>("toolCallId") ?? "perm";
        var rawTitle = toolCall?.Value<string>("title")?.Trim();
        var rawKind = toolCall?.Value<string>("kind")?.Trim();
        var rawInput = toolCall?["rawInput"] as JObject;

        var command = rawInput?.Value<string>("CommandLine") ?? rawInput?.Value<string>("command");
        var path = rawInput?.Value<string>("Path")
                   ?? rawInput?.Value<string>("FilePath")
                   ?? (toolCall?["locations"] is JArray { Count: > 0 } locs ? (locs[0] as JObject)?.Value<string>("path") : null);

        var kind = NormalizePermissionKind(rawKind, command, path);
        var title = !string.IsNullOrWhiteSpace(rawTitle)
            ? rawTitle!
            : command ?? path ?? "需要核准的操作";
        var detail = command ?? (rawInput is { Count: > 0 } ? rawInput.ToString(Newtonsoft.Json.Formatting.Indented) : path);

        string allowOnce = DefaultAllowOnceOptionId;
        string? nativeAllowAlways = null;
        string rejectOnce = DefaultRejectOnceOptionId;
        bool sawOptions = false;

        if (@params["options"] is JArray options && options.Count > 0)
        {
            sawOptions = true;
            foreach (var opt in options.OfType<JObject>())
            {
                var optionId = opt.Value<string>("optionId")?.Trim();
                var optKind = opt.Value<string>("kind")?.Trim();
                if (string.IsNullOrEmpty(optionId) || string.IsNullOrEmpty(optKind))
                    continue;
                if (optKind.Equals("allow_once", StringComparison.OrdinalIgnoreCase))
                    allowOnce = optionId;
                else if (optKind.Equals("allow_always", StringComparison.OrdinalIgnoreCase))
                    nativeAllowAlways = optionId;
                else if (optKind.Equals("reject_once", StringComparison.OrdinalIgnoreCase)
                         || optKind.Equals("reject_always", StringComparison.OrdinalIgnoreCase))
                    rejectOnce = optionId;
            }
        }

        // 實測 agy 1.5.17：Windows PowerShell 指令只附 allow（allow_once）與 deny（reject_once），不附 allow_always；
        // 若無 allow_always 選項，AllowForSession 退回送 allowOnce（並由會話層記住本對話自動放行）
        var effectiveAllowAlways = nativeAllowAlways ?? (sawOptions ? allowOnce : DefaultAllowAlwaysOptionId);

        return new AntigravityPermissionRequest(
            sessionId,
            toolCallId,
            title,
            kind,
            command ?? path,
            detail,
            allowOnce,
            effectiveAllowAlways,
            rejectOnce);
    }

    private static string NormalizePermissionKind(string? rawKind, string? command, string? path)
    {
        var k = (rawKind ?? string.Empty).Trim().ToLowerInvariant();
        if (k is "execute" or "bash" or "command" || !string.IsNullOrWhiteSpace(command))
            return "bash";
        if (k is "edit" or "write" or "delete")
            return k == "delete" ? "edit" : k;
        if (k is "read" or "fetch" or "search")
            return k;
        if (!string.IsNullOrWhiteSpace(path))
            return "edit";
        return string.IsNullOrEmpty(k) ? "other" : k;
    }

    /// <summary>
    /// 將使用者的核准決定轉成 ACP <c>session/request_permission</c> 回應的 <c>result</c> 物件
    /// </summary>
    public static JObject BuildPermissionOutcome(AgentApprovalChoice choice, AntigravityPermissionRequest? request = null)
    {
        var optionId = choice switch
        {
            AgentApprovalChoice.Allow => request?.AllowOnceOptionId ?? DefaultAllowOnceOptionId,
            AgentApprovalChoice.AllowForSession => request?.AllowAlwaysOptionId ?? DefaultAllowAlwaysOptionId,
            _ => request?.RejectOnceOptionId ?? DefaultRejectOnceOptionId,
        };
        return new JObject
        {
            ["outcome"] = new JObject
            {
                ["outcome"] = "selected",
                ["optionId"] = optionId,
            },
        };
    }

    /// <summary>取消待決的 <c>session/request_permission</c> 請求（例如使用者按下停止）</summary>
    public static JObject BuildPermissionCancelledOutcome()
        => new()
        {
            ["outcome"] = new JObject
            {
                ["outcome"] = "cancelled",
            },
        };

    // ───────────────────────────── elicitation/create 結構化提問 ─────────────────────────────

    /// <summary>
    /// 解析 agy 的 <c>elicitation/create</c> 請求（對應 <c>ask_question</c> 工具）：
    /// 將 <c>requestedSchema.properties</c> 的每個屬性轉為一張 <see cref="AgentQuestion"/> 選擇題或問答題
    /// </summary>
    public static IReadOnlyList<AgentQuestion> ParseElicitationQuestions(JObject? @params)
    {
        if (@params == null)
            return Array.Empty<AgentQuestion>();
        var topMessage = @params.Value<string>("message")?.Trim();
        var properties = @params["requestedSchema"]?["properties"] as JObject;
        if (properties == null || properties.Count == 0)
        {
            if (string.IsNullOrWhiteSpace(topMessage))
                return Array.Empty<AgentQuestion>();
            return new[]
            {
                new AgentQuestion
                {
                    Id = "answer",
                    Header = "問題",
                    Text = topMessage!,
                    MultiSelect = false,
                    AllowOther = true,
                    Options = new List<AgentQuestionOption>(),
                },
            };
        }

        var questions = new List<AgentQuestion>();
        foreach (var prop in properties.Properties())
        {
            if (prop.Value is not JObject schema)
                continue;
            var propType = schema.Value<string>("type")?.Trim() ?? "string";
            bool multiSelect = propType.Equals("array", StringComparison.OrdinalIgnoreCase);
            var title = schema.Value<string>("title")?.Trim();
            var description = schema.Value<string>("description")?.Trim();
            var questionText = !string.IsNullOrWhiteSpace(description)
                ? description!
                : !string.IsNullOrWhiteSpace(title)
                    ? title!
                    : !string.IsNullOrWhiteSpace(topMessage)
                        ? topMessage!
                        : prop.Name;

            var options = ExtractSchemaOptions(multiSelect ? schema["items"] as JObject ?? schema : schema);
            questions.Add(new AgentQuestion
            {
                Id = prop.Name,
                Header = !string.IsNullOrWhiteSpace(title) ? title! : prop.Name,
                Text = questionText,
                MultiSelect = multiSelect,
                AllowOther = true,
                Options = options,
            });
        }
        return questions;
    }

    private static List<AgentQuestionOption> ExtractSchemaOptions(JObject? schema)
    {
        var result = new List<AgentQuestionOption>();
        if (schema == null)
            return result;

        if (schema["enum"] is JArray enumArr)
        {
            foreach (var v in enumArr.OfType<JValue>())
            {
                var label = v.Value?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(label))
                    result.Add(new AgentQuestionOption { Label = label, Description = string.Empty });
            }
            return result;
        }

        var choicesArray = schema["oneOf"] as JArray ?? schema["anyOf"] as JArray;
        if (choicesArray != null)
        {
            foreach (var item in choicesArray.OfType<JObject>())
            {
                var constVal = item.Value<string>("const")?.Trim()
                               ?? (item["enum"] is JArray { Count: > 0 } e ? e[0]?.ToString()?.Trim() : null);
                var title = item.Value<string>("title")?.Trim();
                var desc = item.Value<string>("description")?.Trim() ?? string.Empty;
                var label = !string.IsNullOrEmpty(constVal) ? constVal : title;
                if (!string.IsNullOrEmpty(label))
                {
                    result.Add(new AgentQuestionOption
                    {
                        Label = label!,
                        Description = !string.IsNullOrEmpty(desc) ? desc : (title != null && title != label ? title : string.Empty),
                    });
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 將使用者在問題卡片上的回答轉成 <c>elicitation/create</c> 的 <c>{ action: "accept", content: { ... } }</c> 回應
    /// </summary>
    public static JObject BuildElicitationAccept(
        IReadOnlyList<AgentQuestion> questions,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers,
        IReadOnlyDictionary<string, string>? notes = null)
    {
        var content = new JObject();
        foreach (var q in questions)
        {
            answers.TryGetValue(q.Id, out var selected);
            var list = selected?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList() ?? new List<string>();
            if (notes != null && notes.TryGetValue(q.Id, out var note) && !string.IsNullOrWhiteSpace(note))
                list.Add(note.Trim());

            if (q.MultiSelect)
            {
                var arr = new JArray();
                foreach (var item in list)
                    arr.Add(item);
                content[q.Id] = arr;
            }
            else
            {
                content[q.Id] = list.Count > 0 ? list[0] : string.Empty;
            }
        }
        return new JObject
        {
            ["action"] = "accept",
            ["content"] = content,
        };
    }

    /// <summary>使用者略過或取消 <c>elicitation/create</c> 表單時的回應</summary>
    public static JObject BuildElicitationDecline(bool cancelled = false)
        => new()
        {
            ["action"] = cancelled ? "cancel" : "decline",
        };

    // ───────────────────────────── session/update 擴充通知 ─────────────────────────────

    /// <summary>
    /// 解析 <c>session/update</c> 中的 <c>session_info_update</c>（agy 自動產生的對話標題與更新時間）
    /// </summary>
    public static bool TryParseSessionInfoUpdate(JObject? update, out string? title, out string? updatedAt)
    {
        title = null;
        updatedAt = null;
        if (update == null)
            return false;
        var kind = update.Value<string>("sessionUpdate");
        if (!string.Equals(kind, "session_info_update", StringComparison.Ordinal))
            return false;
        var rawTitle = update.Value<string>("title")?.Trim();
        var rawUpdated = update["updatedAt"] switch
        {
            JValue { Type: JTokenType.Date, Value: DateTime dt } => dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            JValue { Type: JTokenType.Date, Value: DateTimeOffset dto } => dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            _ => update.Value<string>("updatedAt")?.Trim(),
        };
        title = string.IsNullOrEmpty(rawTitle) ? null : rawTitle;
        updatedAt = string.IsNullOrEmpty(rawUpdated) ? null : rawUpdated;
        return title != null || updatedAt != null;
    }

    /// <summary>
    /// 解析 <c>session/update</c> 中的 <c>usage_update</c>（agy 1.5.8+ 實測：<c>{"sessionUpdate":"usage_update","used":13497,"size":1048576}</c>）
    /// </summary>
    public static bool TryParseUsageUpdate(JObject? update, out long? usedTokens, out long? windowTokens)
    {
        usedTokens = null;
        windowTokens = null;
        if (update == null || !string.Equals(update.Value<string>("sessionUpdate"), "usage_update", StringComparison.Ordinal))
            return false;
        long used = Long(update["used"]);
        long size = Long(update["size"]);
        if (used > 0)
            usedTokens = used;
        if (size > 0)
            windowTokens = size;
        return usedTokens != null || windowTokens != null;
    }

    /// <summary>
    /// 解析 <c>session/prompt</c> 回應中的 <c>usage</c>（實測 agy 1.5.17：<c>inputTokens</c>、<c>outputTokens</c>、<c>cachedReadTokens</c>、<c>cachedWriteTokens</c>、<c>thoughtTokens</c>）
    /// </summary>
    public static AgentUsage? ParsePromptUsage(JToken? result, out long cacheWriteTokens)
    {
        cacheWriteTokens = 0;
        if ((result as JObject)?["usage"] is not JObject usage)
            return null;
        long cached = Long(usage["cachedReadTokens"] ?? usage["cache_read_tokens"]);
        cacheWriteTokens = Long(usage["cachedWriteTokens"] ?? usage["cache_write_tokens"]);
        long input = Long(usage["inputTokens"] ?? usage["input_tokens"]) + cached + cacheWriteTokens;
        long output = Long(usage["outputTokens"] ?? usage["output_tokens"]) + Long(usage["thoughtTokens"] ?? usage["thinking_tokens"]);
        return input + output > 0 ? new AgentUsage(input, output, cached, null) : null;
    }

    /// <summary>解析 <c>session/prompt</c> 的 <c>stopReason</c></summary>
    public static (AgentTurnOutcome Outcome, string? Error) EvaluateStopReason(string? stopReason) => (stopReason ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "cancelled" or "canceled" or "interrupted" => (AgentTurnOutcome.Interrupted, null),
        "end_turn" or "stop_sequence" or "max_tokens" or "max_turn_requests" or "" => (AgentTurnOutcome.Completed, null),
        var other => (AgentTurnOutcome.Completed, null),
    };

    /// <summary>從 ACP <c>tool_call</c>／<c>tool_call_update</c> 取出工具名稱與完整參數物件</summary>
    public static (string? ToolName, JObject? Args) ExtractToolNameAndArgs(JObject? call)
    {
        if (call == null)
            return (null, null);
        var name = call.Value<string>("name")?.Trim();
        var args = call["args"] as JObject ?? call["rawInput"] as JObject;
        return (string.IsNullOrEmpty(name) ? null : name, args);
    }

    /// <summary>從 ACP <c>tool_call</c>／<c>tool_call_update</c> 計算編輯紅綠 Diff（優先採用 ACP <c>content[type="diff"]</c>，否則由工具參數推導）</summary>
    public static IReadOnlyList<AgentDiffLine>? ExtractToolDiff(JObject? call)
    {
        if (call == null)
            return null;
        if (call["content"] is JArray contentArr)
        {
            var pairs = new List<(string? OldString, string? NewString)>();
            foreach (var item in contentArr.OfType<JObject>())
            {
                if (!string.Equals(item.Value<string>("type"), "diff", StringComparison.OrdinalIgnoreCase))
                    continue;
                var oldText = item.Value<string>("oldText");
                var newText = item.Value<string>("newText");
                if (oldText != null || newText != null)
                    pairs.Add((oldText, newText));
            }
            if (pairs.Count == 1 && string.IsNullOrEmpty(pairs[0].OldString) && pairs[0].NewString != null)
                return DiffRules.FromNewFile(pairs[0].NewString!);
            if (pairs.Count > 0)
                return DiffRules.FromEdits(pairs);
        }

        var (name, args) = ExtractToolNameAndArgs(call);
        return AntigravityTranscriptRules.ComputeToolDiff(name, args);
    }

    /// <summary>從 ACP <c>tool_call_update</c> 取出工具文字輸出與結束碼</summary>
    public static (string? Output, int? ExitCode) ExtractToolOutput(JObject? update)
    {
        if (update == null)
            return (null, null);
        int? exitCode = null;
        string? rawOutText = null;
        if (update["rawOutput"] is JObject rawOut)
        {
            if (rawOut["exit_code"] is JValue { Value: not null } ec
                && int.TryParse(Convert.ToString(ec.Value, System.Globalization.CultureInfo.InvariantCulture), out var parsedEc))
            {
                exitCode = parsedEc;
            }
            else if (rawOut["exitCode"] is JValue { Value: not null } ec2
                     && int.TryParse(Convert.ToString(ec2.Value, System.Globalization.CultureInfo.InvariantCulture), out var parsedEc2))
            {
                exitCode = parsedEc2;
            }
            rawOutText = rawOut.Value<string>("output") ?? rawOut.Value<string>("error");
        }

        if (string.IsNullOrEmpty(rawOutText) && update["content"] is JArray contentArr)
        {
            var parts = new List<string>();
            foreach (var item in contentArr.OfType<JObject>())
            {
                if (item["content"] is JObject inner && string.Equals(inner.Value<string>("type"), "text", StringComparison.OrdinalIgnoreCase))
                {
                    var t = inner.Value<string>("text");
                    if (!string.IsNullOrEmpty(t))
                        parts.Add(t);
                }
                else if (string.Equals(item.Value<string>("type"), "text", StringComparison.OrdinalIgnoreCase))
                {
                    var t = item.Value<string>("text");
                    if (!string.IsNullOrEmpty(t))
                        parts.Add(t);
                }
            }
            if (parts.Count > 0)
                rawOutText = string.Join("\n", parts);
        }

        var normalized = string.IsNullOrEmpty(rawOutText) ? null : rawOutText.Replace("\r\n", "\n");
        return (normalized, exitCode);
    }

    /// <summary>ACP <c>tool_call</c>／<c>tool_call_update</c> 的結束狀態（未結束回 null）</summary>
    public static AgentItemStatus? ClassifyToolEndStatus(string? status) => (status ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "completed" or "done" or "succeeded" => AgentItemStatus.Done,
        "failed" or "error" => AgentItemStatus.Failed,
        "cancelled" or "canceled" => AgentItemStatus.Cancelled,
        "rejected" or "declined" => AgentItemStatus.Declined,
        _ => null,
    };

    /// <summary>工具更新是否代表使用者在核准卡按了拒絕</summary>
    public static bool IsRejectedToolUpdate(string? status, string? output)
    {
        if (string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "declined", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return output != null
               && (output.Contains("user denied permission", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("User rejected", StringComparison.OrdinalIgnoreCase)
                   || output.Contains("Permission denied by user", StringComparison.OrdinalIgnoreCase));
    }

    private static long Long(JToken? token)
        => token is JValue { Value: not null } v && long.TryParse(Convert.ToString(v.Value, System.Globalization.CultureInfo.InvariantCulture), out var n) ? n : 0;
}
