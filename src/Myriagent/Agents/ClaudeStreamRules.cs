using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Usage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>啟動 claude 的選項</summary>
/// <param name="SessionId">預先產生的 session UUID（新對話以 --session-id 建立、之後以 --resume 續接）</param>
/// <param name="Resume">CLI 端已有這個 session（續接）</param>
/// <param name="AddDirectories">額外開放讀寫的資料夾（對話的附件資料夾、「本對話都允許」同意過的資料夾），讀附件不必核准</param>
/// <param name="UseLocalCustomizations">載入使用者的外掛、hooks、CLAUDE.md、MCP 與權限設定；false＝精簡模式（--safe-mode＋只讀專案層設定）</param>
/// <param name="ResumeTranscript">續接時改帶逐字稿完整路徑（換過工作資料夾、逐字稿在別的專案資料夾）；null＝以 SessionId 續接</param>
/// <param name="AllowedTools">「本對話都允許」同意過的規則（例如 Bash(npm test:*)），程序重開時以 --allowedTools 帶回</param>
/// <param name="AutoMemoryDirectory">共用設定：自動記憶改寫在這個資料夾（--settings 的 autoMemoryDirectory，CLI 設定優先於使用者設定）；null＝照 CLI 預設</param>
/// <param name="McpConfigFile">共用設定：另外載入這個 MCP 設定檔（--mcp-config，和帳戶自己的 MCP 並存）；null＝不帶</param>
/// <param name="ForkSession">
/// 延伸出來的對話第一次啟動：從 ResumeTranscript（原對話逐字稿的副本）分支成 SessionId 這個新的 session
/// （<c>--resume &lt;副本&gt; --fork-session --session-id &lt;SessionId&gt;</c>；實測 E-4：分支檔寫在目前工作資料夾的專案資料夾、自成一份，來源不動）
/// </param>
/// <param name="DelegationMcpPipeName">帳戶委派：以 --mcp-config 的 JSON 字串掛上 myriagent_accounts MCP（橋接連這個管道），兩個工具以 --allowedTools 放行；null＝不掛</param>
internal sealed record ClaudeLaunchOptions(
    string SessionId,
    bool Resume,
    AgentPermissionMode PermissionMode,
    string? Model,
    string? Effort,
    string? AppendSystemPrompt,
    IReadOnlyList<string> AddDirectories,
    bool UseLocalCustomizations,
    string? ResumeTranscript = null,
    IReadOnlyList<string>? AllowedTools = null,
    string? AutoMemoryDirectory = null,
    string? McpConfigFile = null,
    string? FallbackModel = null,
    string? Agent = null,
    string? OutputStyle = null,
    string? ResumeAt = null,
    bool ForkSession = false,
    string? DelegationMcpPipeName = null,
    long? AutoCompactTokens = null);

/// <summary>「本對話都允許」同意過、程序重開時要帶回的範圍</summary>
internal sealed record ClaudeSessionGrants(IReadOnlyList<string> Rules, IReadOnlyList<string> Directories);

/// <summary>can_use_tool 等 CLI 發來的控制請求</summary>
/// <param name="Raw">整個 request 物件（elicitation 的欄位在這裡：mcp_server_name、message、mode、url、requested_schema…）</param>
internal sealed record ClaudeControlRequest(
    string RequestId,
    string Subtype,
    string? ToolName,
    JObject Input,
    JArray? Suggestions,
    string? ToolUseId,
    string? Description,
    JObject? Raw = null);

/// <summary>CLI 對宿主控制請求的回應（initialize、interrupt、set_permission_mode、remote_control）</summary>
internal sealed record ClaudeControlResponse(string RequestId, bool Success, JObject? Response, string? Error);

/// <summary>使用者從 claude.ai 或 Claude App 送來的訊息（--replay-user-messages 回送、origin 是 human）</summary>
/// <param name="Uuid">訊息的 uuid（遠端送出時給的，command_lifecycle 與 result 的 user_message_uuid 也是它）</param>
/// <param name="Text">文字區塊接起來的內容</param>
/// <param name="ImageCount">圖片區塊數</param>
/// <param name="FileCount">文字與圖片以外的區塊數（文件等；畫面上只能標示數量）</param>
internal sealed record ClaudeRemoteUserMessage(string Uuid, string Text, int ImageCount, int FileCount);

/// <summary>system/bridge_state 對 Remote Control 的影響（ClaudeStreamRules.ClassifyBridgeState）</summary>
internal enum ClaudeBridgeChange
{
    /// <summary>其他狀態：不改變</summary>
    None,

    /// <summary>ready、connected：遠端可以接手</summary>
    Connected,

    /// <summary>暫時斷線（failure_kind 不是 terminal）：之後可能再回到 connected</summary>
    Lost,

    /// <summary>斷線且不會自己恢復（failed terminal、policy_disabled）：提示使用者</summary>
    LostForGood,
}

/// <summary>一行 stdout 的解析結果</summary>
internal sealed class ClaudeParseResult
{
    public List<AgentEvent> Events { get; } = new();

    public ClaudeControlRequest? ControlRequest { get; set; }

    public ClaudeControlResponse? ControlResponse { get; set; }

    public AgentTurnCompletedEvent? TurnCompleted { get; set; }

    /// <summary>
    /// 這個 result 屬於 CLI 自己處理的非使用者輪次（origin 不是使用者，例如續接時先處理背景工作的 task-notification），
    /// 不是使用者剛送出的那則訊息的結果
    /// </summary>
    public bool TurnFromOtherOrigin { get; set; }

    /// <summary>CLI 撤回了尚未回應的控制請求（control_cancel_request）</summary>
    public string? CancelledRequestId { get; set; }

    // ───── CLI 熱更新（安全點與自動接續的判斷） ─────

    /// <summary>system/init 回報的 CLI 版本（claude_code_version，原樣）；這一行不是 init 為 null</summary>
    public string? CliVersion { get; set; }

    /// <summary>這一行是 system/init（CLI 每一輪開始時都送，含 CLI 自己開的輪次）</summary>
    public bool TurnInit { get; set; }

    /// <summary>
    /// 這一行代表 CLI 正在處理一輪：API 請求已送出（system/status requesting）、主對話的串流與 assistant 訊息、工具結果、can_use_tool。
    /// 送出訊息後看到它，才算 CLI 已接手這一輪（之前重開，剛送出的訊息可能還沒寫進逐字稿）
    /// </summary>
    public bool TurnActivity { get; set; }

    /// <summary>
    /// command_lifecycle 的 state（queued／started／completed…）：帶 uuid 排進佇列的指令才有——app 送的訊息（帶 uuid）、
    /// 遠端送來的訊息、CLI 自動接續被打斷的一輪（started 比 initialize 回應還早）
    /// </summary>
    public string? CommandLifecycleState { get; set; }

    /// <summary>command_lifecycle 的 command_uuid（那則指令的 uuid；app 送的訊息就是送出時帶的 uuid）</summary>
    public string? CommandUuid { get; set; }

    /// <summary>
    /// --replay-user-messages 回送的使用者訊息（isReplay）的 uuid：CLI 開始把這則訊息交給模型（一輪的第一個輸出之前，
    /// 或併進進行中的一輪時在工具結果之後）
    /// </summary>
    public string? ReplayUuid { get; set; }

    /// <summary>回送的訊息是使用者從 claude.ai 或 Claude App 送來的（ClaudeStreamRules.ParseRemoteUserMessage）</summary>
    public ClaudeRemoteUserMessage? RemoteUserMessage { get; set; }

    /// <summary>result 的 user_message_uuid：這一輪開頭那則使用者訊息的 uuid（多則併成一輪時是第一則）</summary>
    public string? UserMessageUuid { get; set; }

    /// <summary>result 的 resume_reason：CLI 自動接續被打斷那一輪時才有（設了 CLAUDE_CODE_RESUME_REASON 時是那個值，否則 interrupted_turn）</summary>
    public string? ResumeReason { get; set; }

    /// <summary>system/task_notification 的 status（completed／stopped／failed…）：背景工作結束；不是這種事件為 null</summary>
    public string? TaskNotificationStatus { get; set; }

    // ───── Remote Control ─────

    /// <summary>system/bridge_state 的 state（ready／connected／failed／policy_disabled…）；不是這種事件為 null</summary>
    public string? BridgeState { get; set; }

    /// <summary>bridge_state 的 failure_kind（state 是 failed 時才有；CLI 沒標明時是 terminal）</summary>
    public string? BridgeFailureKind { get; set; }

    /// <summary>bridge_state 的 detail（給人看的說明）</summary>
    public string? BridgeDetail { get; set; }
}

/// <summary>工具的顯示資訊（工具卡與核准卡共用）</summary>
internal sealed record ToolDescription(
    string DisplayName,
    string Kind,
    string Title,
    string? Detail,
    string? FilePath = null,
    IReadOnlyList<AgentDiffLine>? Diff = null);

/// <summary>
/// Claude Code CLI 雙向 stream-json 協定的純函式規則：組啟動參數、組 stdin 訊息與控制回應、工具的顯示文字。
/// 協定實測見 docs/protocol-notes.md 與 SampleShop 的 AI代理紀錄：
/// can_use_tool 控制請求在所有權限模式都會送 AskUserQuestion；回 allow 並在 updatedInput 加 answers（題目文字 → 選項標籤）即完成作答。
/// </summary>
internal static partial class ClaudeStreamRules
{
    /// <summary>對話視窗內沒有意義、或會在背景留下排程與雲端工作的工具（程序結束就失效或對外建立工作）</summary>
    public static readonly IReadOnlyList<string> DisallowedTools = new[]
    {
        "CronCreate", "CronDelete", "CronList", "ScheduleWakeup", "RemoteTrigger", "EnterWorktree", "ExitWorktree",
    };

    public const string AskUserQuestionTool = "AskUserQuestion";

    public const string ExitPlanModeTool = "ExitPlanMode";

    public const string TodoWriteTool = "TodoWrite";

    /// <summary>Claude Code 2.1.295 起舊模型（Sonnet 4.x、Haiku 4.5）以這組工具管理待辦，TodoWrite 預設停用</summary>
    public const string TaskCreateTool = "TaskCreate";

    public const string TaskUpdateTool = "TaskUpdate";

    public const string TaskGetTool = "TaskGet";

    public const string TaskListTool = "TaskList";

    // ───── CLI 熱更新：重開續接的環境變數（實測見 docs/protocol-notes.md「CLI 執行中更新與重開續接」） ─────

    /// <summary>新程序以 --resume 啟動時自動接續中途結束的一輪（Claude Code 自己的背景 session 重生、VS Code「重新載入後繼續」也用這組）</summary>
    public const string ResumeInterruptedTurnVariable = "CLAUDE_CODE_RESUME_INTERRUPTED_TURN";

    /// <summary>被打斷的一輪（逐字稿最後一列）距今超過這麼多毫秒就不接續</summary>
    public const string ResumeMaxAgeVariable = "CLAUDE_CODE_RESUME_INTERRUPTED_TURN_MAX_AGE_MS";

    /// <summary>接續時補進逐字稿的 isMeta 訊息（沒設時是「Continue from where you left off.」）</summary>
    public const string ResumePromptVariable = "CLAUDE_CODE_RESUME_PROMPT";

    /// <summary>接續那一輪的 resume_reason（CLI 只收 ^[a-z_]{1,64}$）：用來分辨是 app 自己的重開</summary>
    public const string ResumeReasonVariable = "CLAUDE_CODE_RESUME_REASON";

    /// <summary>關閉 CLI 的背景自動更新（只影響背景更新，不影響手動 claude update）</summary>
    public const string DisableAutoUpdaterVariable = "DISABLE_AUTOUPDATER";

    /// <summary>開檔案檢查點（Agent SDK 的 enableFileCheckpointing；代理以編輯工具改的檔案每則使用者訊息記一個還原點，Bash 改的不記）</summary>
    public const string FileCheckpointingVariable = "CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING";

    /// <summary>推理強度的環境變數：CLI 以它蓋過 --effort 與 apply_flag_settings 的 effortLevel（實測 2.1.310：回 success 但強度不變）</summary>
    public const string EffortLevelVariable = "CLAUDE_CODE_EFFORT_LEVEL";

    /// <summary>
    /// 推理強度的 ultracode：CLI 的強度別名，等於 xhigh 加上整個工作階段開啟動態工作流程（每一輪模型都可以用 Workflow 同時派多個代理；
    /// 互動介面是強度滑桿按 Tab）。initialize 的 supportedEffortLevels 不列它，get_settings 的 applied.ultracodeAvailable
    /// 為 true 時支援 xhigh 的模型才能用（<see cref="WithUltracode"/>；實測見 protocol-notes「ultracode」）
    /// </summary>
    public const string UltracodeEffort = "ultracode";

    /// <summary>ultracode 背後的強度（CLI 內部的 {ultracode:"xhigh"}）</summary>
    private const string UltracodeBaseEffort = "xhigh";

    /// <summary>只在系統重開時才帶的變數（其他啟動一律拿掉）</summary>
    public static readonly IReadOnlyList<string> ResumeVariables = new[]
    {
        ResumeInterruptedTurnVariable, ResumeMaxAgeVariable, ResumePromptVariable, ResumeReasonVariable,
    };

    /// <summary>被打斷超過這麼久就不接續（同官方預設與 VS Code）</summary>
    public static readonly TimeSpan ResumeMaxAge = TimeSpan.FromHours(1);

    /// <summary>app 熱更新重開時的 CLAUDE_CODE_RESUME_REASON：CLI 原樣放進接續那一輪的 resume_reason（取代 interrupted_turn）</summary>
    public const string AppResumeReason = "myriagent_cli_update";

    /// <summary>沒有設 CLAUDE_CODE_RESUME_REASON 時接續那一輪的 resume_reason</summary>
    public const string InterruptedTurnResumeReason = "interrupted_turn";

    /// <summary>CLAUDE_CODE_RESUME_PROMPT 的長度上限（Windows 環境變數單一值上限 32,767 字元，留餘裕）</summary>
    public const int MaxResumePromptLength = 8000;

    /// <summary>重開時串流到一半的回覆：結束這段文字時接在後面的說明（接續那一輪 CLI 會從頭重寫，不會接著這段寫）</summary>
    public const string InterruptedTextNote = "（Claude Code 重新啟動，這段回覆在這裡中斷）";

    /// <summary>立即重開時被中斷的工具卡的輸出</summary>
    public const string InterruptedToolOutput = "Claude Code 重新啟動，這個工具被中斷，結果未知";

    /// <summary>串流在途中斷、CLI 重送請求：被丟下的那則回應裡的工具不會執行（重試的回應是新的訊息與新的工具 id）</summary>
    public const string AbandonedToolOutput = "連線中斷，Claude Code 重新送出請求，這個工具沒有執行";

    /// <summary>
    /// 「本對話都允許」按鈕的文字：照 CLI 的建議說清楚放行的範圍。
    /// 編輯類在每步詢問模式下 CLI 建議 setMode acceptEdits（同 VS Code 的「之後的編輯都接受」）：改為自動接受編輯，
    /// 工作資料夾外的路徑與 .claude、.git 等敏感檔仍由 CLI 自己再詢問；指令類只放行 CLI 建議的規則與完全相同的請求。
    /// </summary>
    public static string DescribeAllowForSession(string? toolName, JArray? suggestions)
    {
        var rules = new List<string>();
        foreach (var suggestion in (suggestions ?? new JArray()).OfType<JObject>())
        {
            switch (suggestion.Value<string>("type"))
            {
                case "addRules":
                    foreach (var rule in (suggestion["rules"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var tool = rule.Value<string>("toolName") ?? toolName ?? "工具";
                        var content = rule.Value<string>("ruleContent");
                        rules.Add(string.IsNullOrWhiteSpace(content) ? tool : $"{tool}({content})");
                    }
                    break;
                case "addDirectories":
                    rules.AddRange((suggestion["directories"] as JArray ?? new JArray()).Select(d => $"存取 {d}"));
                    break;
            }
        }

        var mode = FindSuggestedMode(suggestions);
        var modeText = mode.HasValue ? DescribeModeSwitch(mode.Value) : null;
        if (rules.Count == 0)
            return modeText != null ? $"本對話{modeText}" : "本對話同樣的操作都允許";
        var text = $"本對話都允許 {string.Join("、", rules.Take(3))}";
        return modeText != null ? $"{text}，並{modeText}" : text;
    }

    /// <summary>「本對話都允許」會把權限模式切到哪裡的說明（自動接受編輯連 mkdir、rm、mv 等檔案指令也不再詢問，要寫清楚）</summary>
    private static string DescribeModeSwitch(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.AcceptEdits => "改為自動接受編輯（檔案修改與 mkdir、rm、mv 等檔案指令不再詢問）",
        _ => $"改為「{AgentPermissionModes.For(AgentProvider.Claude).First(o => o.Mode == mode).Label}」",
    };

    /// <summary>CLI 建議的權限模式切換（setMode）；沒有或不認得回 null</summary>
    public static AgentPermissionMode? FindSuggestedMode(JArray? suggestions)
    {
        foreach (var suggestion in (suggestions ?? new JArray()).OfType<JObject>())
        {
            if (suggestion.Value<string>("type") != "setMode")
                continue;
            var mode = FromClaudePermissionMode(suggestion.Value<string>("mode"));
            if (mode.HasValue)
                return mode;
        }
        return null;
    }

    /// <summary>
    /// 「本對話都允許」同意的規則與資料夾（程序重開時以 --allowedTools／--add-dir 帶回，CLI 的 session 規則只存在程序記憶體）。
    /// 規則字串照 CLI 的格式：Tool(內容)，內容的反斜線與括號要跳脫；--allowedTools 會被 CLI 以逗號、空白切開（括號內除外，
    /// 但跳脫的右括號也會結束括號），切開後不再是同一條規則的就不帶，避免帶錯範圍。
    /// </summary>
    public static ClaudeSessionGrants ExtractSessionGrants(JArray? suggestions)
    {
        var rules = new List<string>();
        var directories = new List<string>();
        foreach (var suggestion in (suggestions ?? new JArray()).OfType<JObject>())
        {
            switch (suggestion.Value<string>("type"))
            {
                case "addRules" when suggestion.Value<string>("behavior") == "allow":
                    foreach (var rule in (suggestion["rules"] as JArray ?? new JArray()).OfType<JObject>())
                    {
                        var tool = rule.Value<string>("toolName");
                        if (string.IsNullOrWhiteSpace(tool))
                            continue;
                        var text = FormatPermissionRule(tool, rule.Value<string>("ruleContent"));
                        if (IsSingleCliRule(text) && !rules.Contains(text, StringComparer.Ordinal))
                            rules.Add(text);
                    }
                    break;
                case "addDirectories":
                    foreach (var dir in (suggestion["directories"] as JArray ?? new JArray()).Select(d => d.Type == JTokenType.String ? d.ToString() : null))
                    {
                        if (!string.IsNullOrWhiteSpace(dir) && !directories.Contains(dir, StringComparer.OrdinalIgnoreCase))
                            directories.Add(dir);
                    }
                    break;
            }
        }
        return new ClaudeSessionGrants(rules, directories);
    }

    /// <summary>權限規則字串（同 CLI：沒有內容或內容是 * 就只寫工具名稱）</summary>
    public static string FormatPermissionRule(string toolName, string? ruleContent)
    {
        if (string.IsNullOrEmpty(ruleContent) || ruleContent == "*")
            return toolName;
        var escaped = ruleContent.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        return $"{toolName}({escaped})";
    }

    /// <summary>這個字串經 CLI 的 --allowedTools 切割後仍是完整的一條規則（照 CLI 的切割方式：逗號、空白在括號內不切）</summary>
    internal static bool IsSingleCliRule(string rule)
    {
        if (string.IsNullOrWhiteSpace(rule) || rule.StartsWith('-'))
            return false;
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inParens = false;
        foreach (var ch in rule)
        {
            switch (ch)
            {
                case '(':
                    inParens = true;
                    current.Append(ch);
                    break;
                case ')':
                    inParens = false;
                    current.Append(ch);
                    break;
                case ',' or ' ' when !inParens:
                    if (current.ToString().Trim().Length > 0)
                        parts.Add(current.ToString().Trim());
                    current.Clear();
                    break;
                default:
                    if (current.Length > 0 || !char.IsWhiteSpace(ch))
                        current.Append(ch);
                    break;
            }
        }
        if (current.ToString().Trim().Length > 0)
            parts.Add(current.ToString().Trim());
        return parts.Count == 1 && parts[0] == rule;
    }

    /// <summary>CLI 放逐字稿的專案資料夾名稱長度上限（超過就截斷再加雜湊，雜湊算法不公開，只能比前綴）</summary>
    private const int MaxProjectFolderNameLength = 200;

    /// <summary>
    /// CLI 存放這個工作資料夾逐字稿的專案資料夾名稱（&lt;設定資料夾&gt;\projects\&lt;名稱&gt;）：英數字以外的字元都換成「-」。
    /// 實測 2.1.295 的轉換：C:\repo\proj → C--repo-proj。
    /// </summary>
    public static string ProjectFolderName(string workingDirectory)
    {
        var path = workingDirectory ?? string.Empty;
        if (path.Length > 3)
            path = Path.TrimEndingDirectorySeparator(path);
        return NonAlphanumeric().Replace(path, "-");
    }

    /// <summary>專案資料夾名稱是不是這個工作資料夾的（不分大小寫；名稱過長時 CLI 會截斷加雜湊，只比截斷的前綴）</summary>
    public static bool IsProjectFolderFor(string folderName, string workingDirectory)
    {
        var expected = ProjectFolderName(workingDirectory);
        return expected.Length <= MaxProjectFolderNameLength
            ? string.Equals(folderName, expected, StringComparison.OrdinalIgnoreCase)
            : folderName.StartsWith(expected[..MaxProjectFolderNameLength] + "-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 換過工作資料夾時要續接的逐字稿：CLI 依建立 session 時的工作資料夾分開存放逐字稿，
    /// 換了資料夾再以 session id 續接會「No conversation found」；實測 2.1.295 改帶逐字稿 .jsonl 完整路徑可以在新資料夾續接，
    /// session id 不變、之後的內容也寫回同一個檔案。逐字稿就在目前資料夾對應的專案資料夾（或到處都找不到）時回 null，照常以 id 續接。
    /// </summary>
    public static string? FindTranscriptElsewhere(string configDirectory, string sessionId, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory) || !Guid.TryParse(sessionId, out _))
            return null;
        try
        {
            var projects = Path.Combine(configDirectory, "projects");
            var fileName = sessionId + ".jsonl";
            if (File.Exists(Path.Combine(projects, ProjectFolderName(workingDirectory), fileName)) || !Directory.Exists(projects))
                return null;

            string? newest = null;
            var newestTime = DateTime.MinValue;
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var candidate = Path.Combine(dir, fileName);
                if (!File.Exists(candidate))
                    continue;
                if (IsProjectFolderFor(Path.GetFileName(dir), workingDirectory))
                    return null;
                var time = File.GetLastWriteTimeUtc(candidate);
                if (newest == null || time > newestTime)
                {
                    newest = candidate;
                    newestTime = time;
                }
            }
            return newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>子程序看到的 Claude 設定資料夾：帳戶環境設定後的 CLAUDE_CONFIG_DIR，沒有就是 %USERPROFILE%\.claude</summary>
    public static string ResolveConfigDirectory(Action<IDictionary<string, string?>>? configureEnvironment)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CLAUDE_CONFIG_DIR"] = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
        };
        configureEnvironment?.Invoke(env);
        return env.TryGetValue("CLAUDE_CONFIG_DIR", out var dir) && !string.IsNullOrWhiteSpace(dir)
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    /// <summary>
    /// 換了工作資料夾續接時附在訊息前的說明：CLI 會沿用對話第一次請求時記下的系統提示（含背景說明裡的舊工作資料夾），
    /// 要明確告訴代理新的資料夾
    /// </summary>
    public static string BuildFolderChangedNote(string workingDirectory)
        => $"（系統說明：使用者已把工作資料夾改為 {workingDirectory}，之後讀寫檔案、執行指令都以這個資料夾為準；先前說明中的工作資料夾已不適用。）\n\n";

    /// <summary>
    /// result 是不是使用者送出的訊息那一輪的結果：origin 沒有、或是 human／auto-continuation 才是；
    /// task-notification、peer、channel 等是 CLI 自己處理的輪次（實測續接時背景工作通知會先送一個 num_turns 0 的 result）
    /// </summary>
    public static bool IsUserTurnOrigin(JToken? origin)
    {
        if (origin is not JObject o)
            return true;
        var kind = o.Value<string>("kind");
        return string.IsNullOrEmpty(kind) || kind is "human" or "auto-continuation";
    }

    private static readonly string[] MessageUsageKeys = { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens" };

    /// <summary>
    /// 一則訊息的用量欄位（input、cache_creation、cache_read、output）：stream_event 的 message_start.message.usage 或 message_delta.usage 疊到上次記的值上，
    /// 沒帶的欄位沿用上次（實測 message_delta 帶齊四個、output 是這則的最終值；舊版 API 只帶 output_tokens）；不是數字或負的當成沒帶
    /// </summary>
    public static long[] MergeMessageUsage(long[]? before, JObject usage)
    {
        var after = new long[MessageUsageKeys.Length];
        for (int i = 0; i < after.Length; i++)
            after[i] = NonNegativeLong(usage[MessageUsageKeys[i]]) ?? before?[i] ?? 0;
        return after;
    }

    /// <summary>stdout 的這一行是 result（一輪結束；只看字串，猜錯只是多讀一次子代理逐字稿）</summary>
    public static bool IsResultLine(string line) => line.Contains("\"type\":\"result\"", StringComparison.Ordinal);

    /// <summary>stdout 的這一行是子代理的活動：轉送的子代理訊息（parent_tool_use_id 有值）或 system/task_*（只看字串）</summary>
    public static bool IsSubagentActivityLine(string line)
        => line.Contains("\"parent_tool_use_id\":\"", StringComparison.Ordinal) || line.Contains("\"subtype\":\"task_", StringComparison.Ordinal);

    /// <summary>子代理（背景工作）結束的通知：task_notification、task_updated（之後不會再有它的訊息觸發讀逐字稿）</summary>
    public static bool IsSubagentEndLine(string line)
        => line.Contains("\"subtype\":\"task_notification\"", StringComparison.Ordinal) || line.Contains("\"subtype\":\"task_updated\"", StringComparison.Ordinal);

    /// <summary>MergeMessageUsage 的欄位 → AgentUsage（輸入＝input＋cache_creation＋cache_read，同 result 的 usage）</summary>
    public static AgentUsage? MessageUsageOf(long[]? fields)
        => fields == null ? null : new AgentUsage(fields[0] + fields[1] + fields[2], fields[3], fields[2], null);

    /// <summary>
    /// result 的 modelUsage（各模型的 inputTokens、cacheCreationInputTokens、cacheReadInputTokens、outputTokens）相加：
    /// 實測是這個程序的累計（不是這一輪的），含子代理；--resume 開的程序從恢復的舊用量起算。沒有或不是物件回 null
    /// </summary>
    public static AgentUsage? SumModelUsage(JObject? modelUsage)
    {
        if (modelUsage == null)
            return null;
        long input = 0, cached = 0, output = 0;
        bool any = false;
        foreach (var model in modelUsage.Properties().Select(p => p.Value).OfType<JObject>())
        {
            any = true;
            long read = NonNegativeLong(model["cacheReadInputTokens"]) ?? 0;
            input += (NonNegativeLong(model["inputTokens"]) ?? 0) + (NonNegativeLong(model["cacheCreationInputTokens"]) ?? 0) + read;
            cached += read;
            output += NonNegativeLong(model["outputTokens"]) ?? 0;
        }
        return any ? new AgentUsage(input, output, cached, null) : null;
    }

    /// <summary>
    /// result 的 modelUsage 依模型的四個欄位（input、cache_creation、cache_read、output；順序同 MergeMessageUsage）。沒有或不是物件回 null
    /// </summary>
    public static Dictionary<string, long[]>? ModelUsageFields(JObject? modelUsage)
    {
        if (modelUsage == null)
            return null;
        var result = new Dictionary<string, long[]>(StringComparer.Ordinal);
        foreach (var property in modelUsage.Properties())
        {
            if (property.Value is not JObject model)
                continue;
            result[property.Name] = new[]
            {
                NonNegativeLong(model["inputTokens"]) ?? 0,
                NonNegativeLong(model["cacheCreationInputTokens"]) ?? 0,
                NonNegativeLong(model["cacheReadInputTokens"]) ?? 0,
                NonNegativeLong(model["outputTokens"]) ?? 0,
            };
        }
        return result;
    }

    /// <summary>四個欄位從 before 到 after 的增量（逐欄、負的當 0；before 是 null 當 0）</summary>
    public static long[] FieldIncrement(long[]? before, long[] after)
        => after.Select((v, i) => Math.Max(0, v - (before?[i] ?? 0))).ToArray();

    /// <summary>
    /// 一輪結束時要補的，依模型：補的總量＝全部模型 modelUsage 的增量合計減掉這段期間已記的合計（逐欄、負的當 0；和不分模型時一樣，絕不重複記），
    /// 再依各模型「增量減掉記在它名下的」分給各模型。記的時候的模型名稱對不到 modelUsage 的鍵（訊息沒帶 model、名稱寫法不同）也不會重複：
    /// 那部分照樣從總量扣掉，只是分配時不算在任何一個模型名下。回傳有東西要補的模型
    /// </summary>
    public static List<(string Model, long[] Fields)> DistributeTopUp(IReadOnlyDictionary<string, long[]> before, IReadOnlyDictionary<string, long[]> totals,
        IReadOnlyDictionary<string, long[]> counted)
    {
        var result = new List<(string Model, long[] Fields)>();
        var models = totals.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (models.Count == 0)
            return result;
        var increments = models.ToDictionary(m => m, m => FieldIncrement(before.TryGetValue(m, out var b) ? b : null, totals[m]), StringComparer.Ordinal);
        var matched = models.ToDictionary(m => m, _ => new long[4], StringComparer.Ordinal);
        var countedTotal = new long[4];
        foreach (var (key, fields) in counted)
        {
            for (int i = 0; i < 4; i++)
                countedTotal[i] += Math.Max(0, fields[i]);
            if (MatchModelKey(key, models) is { } model)
            {
                for (int i = 0; i < 4; i++)
                    matched[model][i] += Math.Max(0, fields[i]);
            }
        }
        var extras = models.ToDictionary(m => m, _ => new long[4], StringComparer.Ordinal);
        for (int i = 0; i < 4; i++)
        {
            long totalExtra = Math.Max(0, models.Sum(m => increments[m][i]) - countedTotal[i]);
            if (totalExtra == 0)
                continue;
            var residual = models.ToDictionary(m => m, m => Math.Max(0, increments[m][i] - matched[m][i]), StringComparer.Ordinal);
            long weight = residual.Values.Sum();
            if (weight <= 0)
            {
                // 每個模型都已記滿、但合計還少（不該發生）：給增量最多的模型
                extras[models.OrderByDescending(m => increments[m][i]).First()][i] += totalExtra;
                continue;
            }
            long given = 0;
            var ordered = models.Where(m => residual[m] > 0).OrderByDescending(m => residual[m]).ThenBy(m => m, StringComparer.Ordinal).ToList();
            for (int k = 0; k < ordered.Count; k++)
            {
                var share = k == ordered.Count - 1 ? totalExtra - given : (long)Math.Floor((double)totalExtra * residual[ordered[k]] / weight);
                extras[ordered[k]][i] += share;
                given += share;
            }
        }
        foreach (var m in models)
        {
            if (extras[m].Any(v => v > 0))
                result.Add((m, extras[m]));
        }
        return result;
    }

    /// <summary>
    /// 記帳時的模型名稱對到 modelUsage 的哪個鍵：完全相同（不分大小寫）、去掉「[1m]」這種結尾後相同、或一方是另一方的開頭（日期版號）；
    /// 沒帶名稱或對不到回 null
    /// </summary>
    public static string? MatchModelKey(string? model, IReadOnlyList<string> keys)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        static string Bare(string name)
        {
            var bracket = name.IndexOf('[');
            return (bracket > 0 ? name[..bracket] : name).Trim().ToLowerInvariant();
        }
        var exact = keys.FirstOrDefault(k => string.Equals(k, model, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;
        var bare = Bare(model);
        var same = keys.FirstOrDefault(k => Bare(k) == bare);
        if (same != null)
            return same;
        return keys.FirstOrDefault(k => Bare(k).StartsWith(bare, StringComparison.Ordinal) || bare.StartsWith(Bare(k), StringComparison.Ordinal));
    }

    /// <summary>
    /// 四個欄位（input、cache_creation、cache_read、output）→ token 增量事件：輸入＝input＋cache_creation＋cache_read、快取＝cache_read、
    /// 快取寫入＝cache_creation（同 MessageUsageOf）；全是 0 回 null
    /// </summary>
    public static AgentTokenUsageEvent? TokenEvent(string? model, long[] fields)
    {
        if (fields.All(v => v <= 0))
            return null;
        long input = Math.Max(0, fields[0]), write = Math.Max(0, fields[1]), read = Math.Max(0, fields[2]), output = Math.Max(0, fields[3]);
        return new AgentTokenUsageEvent(new AgentUsage(input + write + read, output, read, null), string.IsNullOrEmpty(model) ? null : model, write);
    }

    private static long? NonNegativeLong(JToken? token)
        => token is JValue { Type: JTokenType.Integer or JTokenType.Float } v && Convert.ToDouble(v.Value, CultureInfo.InvariantCulture) is var d && d >= 0 && d < long.MaxValue
            ? (long)d
            : null;

    /// <summary>
    /// result（或同一輪的 assistant 訊息）的 resume_reason：CLI 自動接續被重開打斷的一輪時才有——設了 CLAUDE_CODE_RESUME_REASON 時是那個值
    /// （app 的重開是 AppResumeReason），否則是 interrupted_turn；其他輪次沒有這個欄位。空白或不是字串回 null
    /// </summary>
    public static string? ParseResumeReason(JObject? message)
        => message?["resume_reason"] is JValue { Type: JTokenType.String } value && value.ToString().Trim() is { Length: > 0 } reason ? reason : null;

    /// <summary>
    /// 這個 result 是使用者那一輪的結果：origin 是使用者（或沒有，IsUserTurnOrigin），或帶 resume_reason——CLI 自動接續被打斷的那一輪
    /// （實測 E-15 沒有 origin 欄位；即使之後的版本加了 origin，它接續的仍是使用者那一輪）
    /// </summary>
    public static bool IsUserTurnResult(JObject result)
        => IsUserTurnOrigin(result["origin"]) || ParseResumeReason(result) != null;

    /// <summary>resume_reason 是 app 自己的重開（CLAUDE_CODE_RESUME_REASON＝AppResumeReason）</summary>
    public static bool IsAppResume(string? resumeReason) => string.Equals(resumeReason, AppResumeReason, StringComparison.Ordinal);

    /// <summary>「完全相同的請求」的比對鍵：工具名稱＋輸入 JSON</summary>
    public static string RequestKey(string? toolName, JObject? input)
        => $"{toolName}\n{(input ?? new JObject()).ToString(Formatting.None)}";

    public static string ToClaudePermissionMode(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.AcceptEdits => "acceptEdits",
        AgentPermissionMode.Plan => "plan",
        AgentPermissionMode.Auto => "auto",
        AgentPermissionMode.FullAccess => "bypassPermissions",
        _ => "default",
    };

    public static AgentPermissionMode? FromClaudePermissionMode(string? mode) => mode switch
    {
        "default" => AgentPermissionMode.Ask,
        "acceptEdits" => AgentPermissionMode.AcceptEdits,
        "plan" => AgentPermissionMode.Plan,
        "auto" => AgentPermissionMode.Auto,
        "bypassPermissions" => AgentPermissionMode.FullAccess,
        _ => null,
    };

    /// <summary>組 claude 的參數（不含執行檔本身）</summary>
    public static List<string> BuildArguments(ClaudeLaunchOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        if (string.IsNullOrWhiteSpace(o.SessionId))
            throw new ArgumentException("SessionId 不可為空", nameof(o));

        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            // 子代理的回覆文字也送出來（parent_tool_use_id 是它的 Agent 工具；不帶時只有子代理的工具呼叫與結果）：接在 Agent 工具卡的子代理過程
            "--forward-subagent-text",
            // 回送使用者訊息（isReplay）：Remote Control 時從 claude.ai 或 Claude App 送來的訊息只有這樣才看得到內容。
            // app 自己送的訊息也會回送（uuid 是送出時帶的、沒有 origin），宿主回覆核准的 control_response 也會原樣回送（request_id 是 CLI 的，對不到等待中的請求）
            "--replay-user-messages",
            "--permission-prompt-tool", "stdio",
            "--permission-mode", ToClaudePermissionMode(o.PermissionMode),
            // 只是「允許之後切換到完全自動」，不會改變目前模式；實測沒帶時 set_permission_mode bypassPermissions 會被拒
            "--allow-dangerously-skip-permissions",
            o.Resume ? "--resume" : "--session-id",
            o.Resume && !string.IsNullOrWhiteSpace(o.ResumeTranscript) ? o.ResumeTranscript : o.SessionId,
        };

        // 回溯：從這個 assistant 訊息之後分支出新的 session（實測 2.1.310：要給前一輪最後一則 assistant 的 uuid——給使用者訊息的 uuid 會連那則一起留著、代理接著回答它）
        bool resumeAt = o.Resume && !string.IsNullOrWhiteSpace(o.ResumeAt);
        if (resumeAt)
        {
            args.Add("--resume-session-at");
            args.Add(o.ResumeAt!.Trim());
            args.Add("--fork-session");
        }

        // 延伸出來的對話：從原對話逐字稿的副本分支，分支的 id 事先決定（之後照常以它續接）
        if (o.Resume && o.ForkSession && !string.IsNullOrWhiteSpace(o.ResumeTranscript))
        {
            if (!resumeAt)
                args.Add("--fork-session");
            args.Add("--session-id");
            args.Add(o.SessionId);
        }

        if (!string.IsNullOrWhiteSpace(o.Model))
        {
            args.Add("--model");
            args.Add(o.Model);
        }

        if (FallbackArgument(o.Model, o.FallbackModel) is { } fallback)
        {
            args.Add("--fallback-model");
            args.Add(fallback);
        }

        // 自訂主代理（精簡模式不載入自訂代理，不帶）
        if (o.UseLocalCustomizations && !string.IsNullOrWhiteSpace(o.Agent))
        {
            args.Add("--agent");
            args.Add(o.Agent.Trim());
        }

        // --settings 只帶一個（強度的環境變數與共用設定的記憶資料夾併在同一份 JSON）
        var flagSettings = new JObject();
        if (!string.IsNullOrWhiteSpace(o.Effort))
        {
            // 使用者 settings.json 的 env.CLAUDE_CODE_EFFORT_LEVEL 會蓋過 --effort（連 --safe-mode 也擋不住），
            // 以 --settings 帶同名變數（CLI 設定優先於使用者設定）；子程序環境另由 BuildEnvironmentOverrides 設定
            args.Add("--effort");
            args.Add(o.Effort);
            flagSettings["env"] = new JObject { [EffortLevelVariable] = EffortEnvironmentValue(o.Effort) };
        }
        // 自動壓縮上限：--settings 的 env 優先於程序環境與使用者 settings.json 的 env（實測），門檻＝帶的視窗減緩衝
        if (AutoCompactRules.ClaudeWindowValue(o.AutoCompactTokens) is { } autoCompactWindow)
        {
            var flagEnv = flagSettings["env"] as JObject ?? new JObject();
            flagEnv[AutoCompactRules.ClaudeWindowVariable] = autoCompactWindow;
            flagSettings["env"] = flagEnv;
        }
        if (o.UseLocalCustomizations && !string.IsNullOrWhiteSpace(o.AutoMemoryDirectory))
            flagSettings["autoMemoryDirectory"] = o.AutoMemoryDirectory;
        if (!string.IsNullOrWhiteSpace(o.OutputStyle))
            flagSettings["outputStyle"] = o.OutputStyle.Trim();
        if (flagSettings.Count > 0)
        {
            args.Add("--settings");
            args.Add(flagSettings.ToString(Formatting.None));
        }

        if (!o.UseLocalCustomizations)
        {
            // 精簡模式：--safe-mode 停用外掛、hooks、CLAUDE.md、MCP；使用者 settings.json 的權限放行規則仍會生效，
            // 另以 --setting-sources 排除使用者層設定（實測訂閱登入不受影響）
            args.Add("--safe-mode");
            args.Add("--setting-sources");
            args.Add("project,local");
        }
        else
        {
            // 共用設定的 MCP 檔與帳戶委派的 MCP（JSON 字串）放在同一個 --mcp-config 之後（可帶多個，吃掉後面所有不是旗標的參數）；
            // 委派不加 --strict-mcp-config，使用者自己的 MCP 照常載入
            var mcpConfigs = new List<string>();
            if (!string.IsNullOrWhiteSpace(o.McpConfigFile))
                mcpConfigs.Add(o.McpConfigFile);
            if (o.DelegationMcpPipeName != null)
                mcpConfigs.Add(DelegationMcpRules.ClaudeConfig(o.DelegationMcpPipeName).ToString(Formatting.None));
            if (mcpConfigs.Count > 0)
            {
                args.Add("--mcp-config");
                args.AddRange(mcpConfigs);
            }
        }

        foreach (var dir in o.AddDirectories ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            args.Add("--add-dir");
            args.Add(dir);
        }

        // 每條規則一個參數（實測 2.1.295 逐條解析，規則內容裡的空白與逗號不會被切開）
        var allowed = (o.AllowedTools ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (o.UseLocalCustomizations && o.DelegationMcpPipeName != null)
            allowed.AddRange(DelegationMcpRules.ClaudeAllowedTools.Except(allowed, StringComparer.Ordinal));
        if (allowed.Count > 0)
        {
            args.Add("--allowedTools");
            args.AddRange(allowed);
        }

        args.Add("--disallowedTools");
        args.Add(string.Join(",", DisallowedTools));

        if (!string.IsNullOrWhiteSpace(o.AppendSystemPrompt))
        {
            args.Add("--append-system-prompt");
            args.Add(o.AppendSystemPrompt);
        }

        return args;
    }

    /// <summary>
    /// 子程序要在帳戶環境設定（AccountRules.ApplyEnvironment 會剝掉所有 CLAUDE*）之後另外設定的環境變數：
    /// <list type="bullet">
    /// <item>DISABLE_AUTOUPDATER=1：app 啟動的 claude 一律不在背景自動更新（-p 本來就不會，當保險；換版由 app 偵測後統一重開）</item>
    /// <item>指定推理強度時一併設 CLAUDE_CODE_EFFORT_LEVEL，避免被使用者設定或繼承的變數蓋掉（ultracode 設 xhigh，<see cref="EffortEnvironmentValue"/>）</item>
    /// <item>resumePrompt 不為空白＝系統重開（熱更新）：CLAUDE_CODE_RESUME_INTERRUPTED_TURN=1、…_MAX_AGE_MS=3600000、CLAUDE_CODE_RESUME_PROMPT＝接續訊息、
    /// CLAUDE_CODE_RESUME_REASON＝AppResumeReason。新程序以 --resume 啟動時 CLI 會自己接續中途結束的一輪（實測 E-10），只有系統重開才帶</item>
    /// </list>
    /// </summary>
    public static Dictionary<string, string> BuildEnvironmentOverrides(string? effort, string? resumePrompt = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [DisableAutoUpdaterVariable] = "1",
            // 檔案檢查點（回溯的 rewind_files 要它；實測沒開時回 canRewind false「File rewinding is not enabled.」）
            [FileCheckpointingVariable] = "1",
        };
        if (EffortEnvironmentValue(effort) is { } level)
            env[EffortLevelVariable] = level;
        if (!string.IsNullOrWhiteSpace(resumePrompt))
        {
            env[ResumeInterruptedTurnVariable] = "1";
            env[ResumeMaxAgeVariable] = ((long)ResumeMaxAge.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            env[ResumePromptVariable] = TruncateResumePrompt(resumePrompt);
            env[ResumeReasonVariable] = AppResumeReason;
        }
        return env;
    }

    /// <summary>
    /// 子程序環境的最後一步（在帳戶的 ConfigureEnvironment 之後）：先拿掉所有 RESUME 變數（不是系統重開時，即使繼承或帳戶設定帶到也不能留），
    /// 再套用 BuildEnvironmentOverrides
    /// </summary>
    public static void ApplyLaunchEnvironment(IDictionary<string, string?> env, string? effort, string? resumePrompt)
    {
        ArgumentNullException.ThrowIfNull(env);
        foreach (var key in ResumeVariables)
            env.Remove(key);
        foreach (var (key, value) in BuildEnvironmentOverrides(effort, resumePrompt))
            env[key] = value;
    }

    /// <summary>接續訊息超過 MaxResumePromptLength 就截斷（環境變數單一值上限約 32K 字元；不切開代理字組）</summary>
    internal static string TruncateResumePrompt(string prompt)
    {
        if (prompt.Length <= MaxResumePromptLength)
            return prompt;
        var cut = MaxResumePromptLength;
        if (char.IsHighSurrogate(prompt[cut - 1]))
            cut--;
        return prompt[..cut];
    }

    /// <summary>使用者訊息（stdin 一行）：圖片區塊在前、文字在後</summary>
    /// <param name="uuid">
    /// 這則訊息的 uuid（每次寫入都要新的：CLI 會略過 uuid 重複的訊息）：CLI 以它回報 command_lifecycle、回送訊息與 result 的 user_message_uuid，
    /// 會話靠它分辨一個 result 是不是這則訊息那一輪的（遠端送來的訊息可能先處理）。null＝不帶，CLI 自己產生
    /// </param>
    public static string BuildUserMessage(string text, IReadOnlyList<(string MediaType, string Base64)> images, string? uuid = null)
    {
        var content = new JArray();
        foreach (var (mediaType, base64) in images ?? Array.Empty<(string, string)>())
        {
            content.Add(new JObject
            {
                ["type"] = "image",
                ["source"] = new JObject
                {
                    ["type"] = "base64",
                    ["media_type"] = mediaType,
                    ["data"] = base64,
                },
            });
        }

        content.Add(new JObject { ["type"] = "text", ["text"] = text ?? string.Empty });

        var message = new JObject
        {
            ["type"] = "user",
            ["message"] = new JObject { ["role"] = "user", ["content"] = content },
            ["parent_tool_use_id"] = JValue.CreateNull(),
            ["session_id"] = string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(uuid))
            message["uuid"] = uuid;
        return message.ToString(Formatting.None);
    }

    /// <summary>一行 {"type":"control_response","response":{…}} 的回應（沒有 request_id 回 null）</summary>
    public static ClaudeControlResponse? ParseControlResponseMessage(JObject message)
    {
        if (message["response"] is not JObject response || response.Value<string>("request_id") is not { } requestId)
            return null;
        return new ClaudeControlResponse(requestId, response.Value<string>("subtype") == "success", response["response"] as JObject,
            response["error"] is JValue { Type: JTokenType.String } e ? e.ToString() : null);
    }

    public static string BuildControlRequest(string requestId, JObject request)
        => new JObject
        {
            ["type"] = "control_request",
            ["request_id"] = requestId,
            ["request"] = request,
        }.ToString(Formatting.None);

    /// <summary>
    /// initialize：宣告 perTaskStopAffordance（2026-11-11）——app 有逐一停止背景工作的介面（taskStop 的 stop_task），所以 interrupt（按停止）
    /// 只結束這一輪、不砍背景子代理（沒宣告時 CLI 一併砍掉背景子代理；背景指令本來就不砍）。實測見 protocol-notes
    /// 「Claude：停止不砍背景工作、移到背景、重送成本與閒置壓縮」（tools/probes/claude_cli8_probe.py --mode stop）
    /// </summary>
    public static string BuildInitializeRequest(string requestId)
        => BuildControlRequest(requestId, new JObject { ["subtype"] = "initialize", ["hooks"] = JValue.CreateNull(), ["perTaskStopAffordance"] = true });

    /// <summary>
    /// 把執行中的前景工作移到背景（background_tasks，等於終端機的 Ctrl+B；2026-11-11）：toolUseId 只移那一個工具開的，null＝前景的指令與子代理全部。
    /// 每個被移走的工具立刻回「在背景執行」的工具結果、這一輪接著做，工作在背景跑完時送 task_notification
    /// </summary>
    public static string BuildBackgroundTasksRequest(string requestId, string? toolUseId)
    {
        var request = new JObject { ["subtype"] = "background_tasks" };
        if (!string.IsNullOrEmpty(toolUseId))
            request["tool_use_id"] = toolUseId;
        return BuildControlRequest(requestId, request);
    }

    public static string BuildInterruptRequest(string requestId)
        => BuildControlRequest(requestId, new JObject { ["subtype"] = "interrupt" });

    public static string BuildSetPermissionModeRequest(string requestId, AgentPermissionMode mode)
        => BuildControlRequest(requestId, new JObject { ["subtype"] = "set_permission_mode", ["mode"] = ToClaudePermissionMode(mode) });

    /// <summary>
    /// 換模型（同一個程序，一輪中途送也從下一次模型呼叫起生效；實測 2.1.309）：model 為 null＝帳戶預設模型（同不帶 --model），
    /// 回應 {model: 解析後的 id}；不認得的名稱回 error（error_code not_found）
    /// </summary>
    public static string BuildSetModelRequest(string requestId, string? model)
        => BuildControlRequest(requestId, new JObject { ["subtype"] = "set_model", ["model"] = model == null ? JValue.CreateNull() : model });

    /// <summary>
    /// 換推理強度：apply_flag_settings 的 effortLevel（蓋過啟動時的 --effort，收 max 與 ultracode）；null＝回到模型的預設（同不帶 --effort，
    /// 不是回到啟動時的 --effort）。不認得的值也回 success、強度不變（實測 2.1.309，get_settings 的 applied.effort）。
    /// 同時改 env 的 CLAUDE_CODE_EFFORT_LEVEL：啟動時帶了這個變數（app 指定強度時一定帶），只改 effortLevel 會回 success、強度卻不變
    /// （實測 2.1.310）；回到預設時變數也給 null。從 ultracode 換走要明確帶 ultracode:false——換到同樣是 xhigh 的強度時 CLI 不會自己關掉
    /// （換到其他強度、null 會關，帶了也無妨）
    /// </summary>
    public static string BuildSetEffortRequest(string requestId, string? effort, string? previousEffort = null)
    {
        var settings = new JObject
        {
            ["effortLevel"] = string.IsNullOrWhiteSpace(effort) ? JValue.CreateNull() : effort,
            ["env"] = new JObject { [EffortLevelVariable] = EffortEnvironmentValue(effort) is { } level ? level : JValue.CreateNull() },
        };
        if (IsUltracode(previousEffort) && !IsUltracode(effort))
            settings["ultracode"] = false;
        return BuildControlRequest(requestId, new JObject { ["subtype"] = "apply_flag_settings", ["settings"] = settings });
    }

    /// <summary>查目前套用的設定（不改任何東西；回應的 applied 有 effort、ultracode、ultracodeAvailable，實測 2.1.310）</summary>
    public static string BuildGetSettingsRequest(string requestId)
        => BuildControlRequest(requestId, new JObject { ["subtype"] = "get_settings" });

    /// <summary>get_settings 回應的 applied.ultracodeAvailable（舊版沒有這個欄位、回應失敗都算不能用）</summary>
    public static bool ParseUltracodeAvailable(JObject? getSettingsResponse)
        => getSettingsResponse?["applied"] is JObject applied && applied["ultracodeAvailable"] is JValue { Type: JTokenType.Boolean } available && (bool)available;

    public static bool IsUltracode(string? effort)
        => string.Equals(effort?.Trim(), UltracodeEffort, StringComparison.OrdinalIgnoreCase);

    /// <summary>環境變數 CLAUDE_CODE_EFFORT_LEVEL 要帶的值：CLI 的環境變數只認一般的強度，ultracode 帶它背後的 xhigh；空白＝不帶</summary>
    public static string? EffortEnvironmentValue(string? effort)
        => string.IsNullOrWhiteSpace(effort) ? null : IsUltracode(effort) ? UltracodeBaseEffort : effort;

    /// <summary>
    /// 帳戶能用 ultracode（get_settings 的 applied.ultracodeAvailable）時，支援 xhigh 的模型在強度清單最後多一個 ultracode
    /// （CLI 只對支援 xhigh 的模型開；不支援的模型帶了會退回模型能用的強度、ultracode 關閉）
    /// </summary>
    public static List<AgentModelOption> WithUltracode(IReadOnlyList<AgentModelOption> models, bool ultracodeAvailable)
        => models.Select(m => ultracodeAvailable && m.Efforts.Contains(UltracodeBaseEffort, StringComparer.Ordinal) && !m.Efforts.Contains(UltracodeEffort, StringComparer.Ordinal)
            ? m with { Efforts = m.Efforts.Append(UltracodeEffort).ToList() }
            : m).ToList();

    /// <summary>停掉一個背景工作（不經模型；CLI 回 task_notification stopped 與 background_tasks_changed，實測 E-11）</summary>
    public static string BuildStopTaskRequest(string requestId, string taskId)
        => BuildControlRequest(requestId, new JObject { ["subtype"] = "stop_task", ["task_id"] = taskId });

    // ───── Remote Control（control_request remote_control；實測見 docs/protocol-notes.md「Remote Control」） ─────

    /// <summary>遠端工作階段名稱的長度上限（CLI 對工作階段名稱的上限是 200 個字元）</summary>
    public const int MaxRemoteControlNameLength = 200;

    /// <summary>
    /// 開啟或關閉 Remote Control：options 不為 null＝開啟（name＝遠端工作階段名稱，reattach_session_id＝接回上一個工作階段），null＝關閉。
    /// 實測 2.1.300：精簡模式（--safe-mode）也能開；initialize 回應前、一輪進行中送都可以；已開著再送一次回同一個工作階段
    /// </summary>
    public static string BuildRemoteControlRequest(string requestId, AgentRemoteControlOptions? options)
    {
        var request = new JObject { ["subtype"] = "remote_control", ["enabled"] = options != null };
        if (options != null)
        {
            if (RemoteControlName(options.Name) is { } name)
                request["name"] = name;
            if (!string.IsNullOrWhiteSpace(options.ReattachSessionId))
                request["reattach_session_id"] = options.ReattachSessionId.Trim();
        }
        return BuildControlRequest(requestId, request);
    }

    /// <summary>遠端工作階段名稱：去頭尾空白、超過上限截斷（不切開代理字對）；空白回 null（由 CLI 命名）</summary>
    public static string? RemoteControlName(string? title)
    {
        var name = title?.Trim();
        if (string.IsNullOrEmpty(name))
            return null;
        if (name.Length <= MaxRemoteControlNameLength)
            return name;
        int length = char.IsHighSurrogate(name[MaxRemoteControlNameLength - 1]) ? MaxRemoteControlNameLength - 1 : MaxRemoteControlNameLength;
        return name[..length].TrimEnd();
    }

    /// <summary>
    /// 改工作階段名稱（control_request rename_session，source host）：名稱照 RemoteControlName 整理，空白回 null（CLI 會回錯誤，不送）。
    /// 實測 2.1.308（tools/probes/claude_rename_probe.py）：還沒開始任何一輪也能改；CLI 回 success 並回送 system/session_title_changed
    /// （控制字元換成空白、去頭尾），同名再送也照回；開著 Remote Control 時 CLI 把名稱推給 claude.ai／Claude App 上的工作階段
    /// </summary>
    public static string? BuildRenameSessionRequest(string requestId, string? title)
        => RemoteControlName(title) is { } name
            ? BuildControlRequest(requestId, new JObject { ["subtype"] = "rename_session", ["title"] = name, ["source"] = "host" })
            : null;

    /// <summary>
    /// 上下文用量（/context 的資料；實測 2.1.309 在本機算、約 5～10 毫秒回應、不連網）：每輪的 result 之後送，回應取自動壓縮門檻與 CLI 實際採用的視窗
    /// （ContextUsageRules.ParseClaudeContextResponse）。summary 不含逐格的明細；舊版不認得這個 subtype 時回 error，會話不理會
    /// </summary>
    public static string BuildContextUsageRequest(string requestId)
        => BuildControlRequest(requestId, new JObject { ["subtype"] = "get_context_usage", ["detail"] = "summary" });

    /// <summary>remote_control 開啟成功的回應：遠端工作階段 id（bridge_session_id）與網址（session_url）；欄位不是字串就當成沒有</summary>
    public static (string? BridgeSessionId, string? SessionUrl) ParseRemoteControlResponse(JObject? response)
        => (StringField(response, "bridge_session_id"), StringField(response, "session_url"));

    /// <summary>字串欄位（去掉前後空白；不是字串或空白是 null）</summary>
    internal static string? StringField(JObject? obj, string name)
        => obj?[name] is JValue { Type: JTokenType.String } value && value.ToString().Trim() is { Length: > 0 } text ? text : null;

    /// <summary>Remote Control 開不起來時給使用者的提示（一個對話只提示一次）</summary>
    public static string DescribeRemoteControlFailure(string? error)
        => $"Remote Control 沒有開啟（{(string.IsNullOrWhiteSpace(error) ? "CLI 沒有說明原因" : error.Trim())}），這個對話暫時無法從 claude.ai 或 Claude App 接手；"
           + "下次啟動程序時會再試。不需要的話可在「設定 → 對話」關掉。";

    /// <summary>system/bridge_state 對 Remote Control 的影響</summary>
    public static ClaudeBridgeChange ClassifyBridgeState(string? state, string? failureKind)
        => state switch
        {
            "ready" or "connected" => ClaudeBridgeChange.Connected,
            // CLI 對沒有標明的失敗一律當成 terminal（不會自己恢復）；其他種類之後可能再回到 connected
            "failed" when string.IsNullOrEmpty(failureKind) || failureKind == "terminal" => ClaudeBridgeChange.LostForGood,
            "failed" => ClaudeBridgeChange.Lost,
            "policy_disabled" => ClaudeBridgeChange.LostForGood,
            _ => ClaudeBridgeChange.None,
        };

    /// <summary>Remote Control 斷線且不會自己恢復時給使用者的提示</summary>
    public static string DescribeBridgeLost(string? state, string? detail)
    {
        var reason = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
        var head = state == "policy_disabled" ? "組織政策停用了 Remote Control" : "Remote Control 連線中斷";
        return $"{head}{(reason == null ? string.Empty : $"（{reason}）")}，這個對話暫時無法從 claude.ai 或 Claude App 接手；下次啟動程序時會再試。";
    }

    /// <summary>
    /// --replay-user-messages 回送的訊息是不是使用者從 claude.ai 或 Claude App 送來的：isReplay、不是 isSynthetic、origin.kind 是 human、有 uuid。
    /// 實測 2.1.301：遠端訊息（client_platform 是 ios／android／web_claude_ai／desktop_app）帶 {kind:"human"}；
    /// app 經 stdin 送的沒有 origin；其他工作階段送來的是 isSynthetic＋peer、背景工作通知是 task-notification——都不是
    /// </summary>
    public static ClaudeRemoteUserMessage? ParseRemoteUserMessage(JObject message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message["isReplay"] is not JValue { Type: JTokenType.Boolean } replay || !(bool)replay.Value!)
            return null;
        if (message["isSynthetic"] is JValue { Type: JTokenType.Boolean } synthetic && (bool)synthetic.Value!)
            return null;
        if (message["origin"] is not JObject origin || origin["kind"] is not JValue { Type: JTokenType.String } kind || kind.ToString() != "human")
            return null;
        if (message["uuid"] is not JValue { Type: JTokenType.String } uuid || string.IsNullOrWhiteSpace(uuid.ToString()))
            return null;

        var content = (message["message"] as JObject)?["content"];
        var texts = new List<string>();
        int images = 0;
        int files = 0;
        if (content is JValue { Type: JTokenType.String } plain)
        {
            texts.Add(plain.ToString());
        }
        else if (content is JArray blocks)
        {
            foreach (var block in blocks.OfType<JObject>())
            {
                switch (block.Value<string>("type"))
                {
                    case "text" when block["text"] is JValue { Type: JTokenType.String } text:
                        texts.Add(text.ToString());
                        break;
                    case "image":
                        images++;
                        break;
                    case "text":
                        break;
                    default:
                        // 文件之類的其他區塊：不顯示內容，只標示數量（不然畫面上是空白的氣泡）
                        files++;
                        break;
                }
            }
        }

        return new ClaudeRemoteUserMessage(uuid.ToString(), string.Join("\n", texts), images, files);
    }

    /// <summary>
    /// CLI 停止一輪時插進對話的使用者訊息（「[Request interrupted by user]」或「…for tool use]」，內容是字串或文字區塊）。
    /// 實測 2.1.301：從 claude.ai 或 Claude App 按停止時，CLI 先送這則（不是回送），接著 result 是 error_during_execution
    /// </summary>
    public static bool IsInterruptMarker(JObject? userMessage)
    {
        var content = userMessage?["content"];
        if (content is JValue { Type: JTokenType.String } plain)
            return IsInterruptMarkerText(plain.ToString());
        return content is JArray blocks && blocks.OfType<JObject>().Any(b =>
            b.Value<string>("type") == "text" && b["text"] is JValue { Type: JTokenType.String } text && IsInterruptMarkerText(text.ToString()));
    }

    private static bool IsInterruptMarkerText(string text)
        => text.TrimStart().StartsWith(InterruptMarkerPrefix, StringComparison.Ordinal);

    private const string InterruptMarkerPrefix = "[Request interrupted by user";

    /// <summary>
    /// 執行中的一輪收到的 result 是不是另一則訊息那一輪的（這一輪的訊息還排在 CLI 的佇列裡）：實測遠端送來的訊息先開始時，
    /// CLI 依序處理，app 的訊息排在它後面，先到的 result 的 user_message_uuid 是遠端那則。
    /// 只在能確定時才算：這一輪的訊息有 uuid、CLI 還沒開始處理它（沒看到它的 command_lifecycle started 或回送）、
    /// result 的 user_message_uuid 是看過的另一則指令。舊版 CLI 沒有這些欄位、對不上的 uuid 一律當成這一輪的結果
    /// （寧可提早結束，也不要一直卡在執行中）。併成一輪時（這一輪的訊息在工具結果之後被交給模型）已看過它開始，算這一輪的
    /// </summary>
    /// <param name="turnUuid">這一輪送出的訊息的 uuid（遠端開始的一輪是遠端訊息的）；null＝不明（自動接續的一輪）</param>
    /// <param name="turnStarted">CLI 已開始處理 turnUuid 那則訊息</param>
    /// <param name="resultUuid">result 的 user_message_uuid</param>
    /// <param name="otherCommands">這個程序看過的其他指令的 uuid（command_lifecycle、回送的遠端訊息）</param>
    public static bool IsResultForOtherTurn(string? turnUuid, bool turnStarted, string? resultUuid, IReadOnlySet<string> otherCommands)
    {
        ArgumentNullException.ThrowIfNull(otherCommands);
        return turnUuid != null && !turnStarted && resultUuid != null
               && !string.Equals(resultUuid, turnUuid, StringComparison.OrdinalIgnoreCase)
               && otherCommands.Contains(resultUuid);
    }

    // ───── CLI 熱更新：重開前後的判斷 ─────

    /// <summary>
    /// assistant 訊息的 stop_reason 是這一輪的結尾（end_turn、stop_sequence、refusal）：被打斷時這一輪其實已經回覆完，
    /// CLI 重開後不會接續（逐字稿最後一列是 assistant），也不必由宿主送接續訊息（只會白耗一輪）。
    /// tool_use、max_tokens、pause_turn 與還沒收到（null）都算這一輪還在途中
    /// </summary>
    public static bool IsFinalStopReason(string? stopReason) => stopReason is "end_turn" or "stop_sequence" or "refusal";

    /// <summary>
    /// 新版程序在時限內沒有自動接續時，要不要改由宿主經 stdin 送接續訊息（備案：RESUME 環境變數在某版失效、或 CLI 判定不接續但這一輪其實還在途中，
    /// 例如最後一個工具結果是使用者拒絕）：被打斷時這一輪還在途中（最後的 stop_reason 不是結尾）、而且逐字稿已記下這一輪才送。
    /// 逐字稿沒有這一輪（CLI 還沒寫入使用者訊息）時送接續，代理看不到使用者的訊息，不如不送
    /// </summary>
    public static bool ShouldSendContinuationFallback(bool autoResumed, string? lastStopReason, bool turnRecorded)
        => !autoResumed && turnRecorded && !IsFinalStopReason(lastStopReason);

    /// <summary>重開時串流到一半的回覆文字：接上 InterruptedTextNote；沒有文字回空字串（畫面會移除空的回覆項目）</summary>
    public static string MarkInterruptedText(string? text)
        => string.IsNullOrWhiteSpace(text) ? string.Empty : text.TrimEnd() + "\n\n" + InterruptedTextNote;

    /// <summary>CLI 放這個 session 逐字稿的位置（依建立 session 時的工作資料夾；不檢查是否存在）</summary>
    public static string TranscriptPath(string configDirectory, string sessionId, string workingDirectory)
        => Path.Combine(configDirectory, "projects", ProjectFolderName(workingDirectory), sessionId + ".jsonl");

    /// <summary>
    /// 這個工作資料夾底下這個 session 現有的逐字稿：先看推算的位置，名稱過長（CLI 截斷後加雜湊）時找前綴相同的專案資料夾；
    /// 都沒有（還沒寫過、在別的資料夾）回 null
    /// </summary>
    public static string? FindTranscript(string configDirectory, string sessionId, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory) || !Guid.TryParse(sessionId, out _))
            return null;
        try
        {
            var expected = TranscriptPath(configDirectory, sessionId, workingDirectory);
            if (File.Exists(expected))
                return expected;
            var projects = Path.Combine(configDirectory, "projects");
            if (ProjectFolderName(workingDirectory).Length <= MaxProjectFolderNameLength || !Directory.Exists(projects))
                return null;
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var candidate = Path.Combine(dir, sessionId + ".jsonl");
                if (IsProjectFolderFor(Path.GetFileName(dir), workingDirectory) && File.Exists(candidate))
                    return candidate;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 逐字稿的尾段（UTF-8 位元組）裡有沒有這個工具的結果列（同一行有 "tool_use_id" 與這個 id）：
    /// stdout 送出 tool_result 後約 0.17 秒逐字稿才寫入那一列（E-10），重開前要等它寫入，否則接續時這個工具會變成「結果未知」
    /// </summary>
    public static bool TranscriptTailHasToolResult(ReadOnlySpan<byte> tail, string toolUseId)
    {
        if (string.IsNullOrEmpty(toolUseId))
            return false;
        var id = Encoding.UTF8.GetBytes(toolUseId);
        var marker = "\"tool_use_id\""u8;
        while (tail.Length > 0)
        {
            int newline = tail.IndexOf((byte)'\n');
            var line = newline < 0 ? tail : tail[..newline];
            if (line.IndexOf(id) >= 0 && line.IndexOf(marker) >= 0)
                return true;
            if (newline < 0)
                break;
            tail = tail[(newline + 1)..];
        }
        return false;
    }

    public static string BuildSuccessResponse(string requestId, JObject response)
        => new JObject
        {
            ["type"] = "control_response",
            ["response"] = new JObject
            {
                ["subtype"] = "success",
                ["request_id"] = requestId,
                ["response"] = response,
            },
        }.ToString(Formatting.None);

    public static string BuildErrorResponse(string requestId, string error)
        => new JObject
        {
            ["type"] = "control_response",
            ["response"] = new JObject
            {
                ["subtype"] = "error",
                ["request_id"] = requestId,
                ["error"] = error,
            },
        }.ToString(Formatting.None);

    /// <summary>can_use_tool 放行：updatedInput 必帶（原樣或改過的輸入）；updatedPermissions 給「本對話都允許」</summary>
    public static JObject Allow(JObject input, JArray? updatedPermissions = null)
    {
        var result = new JObject
        {
            ["behavior"] = "allow",
            ["updatedInput"] = input ?? new JObject(),
        };
        if (updatedPermissions != null && updatedPermissions.Count > 0)
            result["updatedPermissions"] = updatedPermissions;
        return result;
    }

    /// <summary>can_use_tool 拒絕；interrupt＝同時中止本輪</summary>
    public static JObject Deny(string message, bool interrupt = false)
        => new JObject
        {
            ["behavior"] = "deny",
            ["message"] = message,
            ["interrupt"] = interrupt,
        };

    /// <summary>使用者拒絕操作時給代理的說明；有附說明就一併轉告</summary>
    public static string BuildDenyMessage(string? feedback)
        => string.IsNullOrWhiteSpace(feedback)
            ? "使用者拒絕了這個操作。不要改用其他方式繞過，請說明你原本要做什麼，並詢問使用者希望怎麼處理。"
            : $"使用者拒絕了這個操作，並說明：「{feedback.Trim()}」。請依照使用者的說明調整做法。";

    /// <summary>計畫被退回時給代理的說明</summary>
    public static string BuildKeepPlanningMessage(string? feedback)
        => string.IsNullOrWhiteSpace(feedback)
            ? "使用者還不同意這個計畫，請繼續規劃，必要時用 AskUserQuestion 詢問使用者想調整的地方。"
            : $"使用者還不同意這個計畫，並說明：「{feedback.Trim()}」。請依照說明修改計畫後再提出。";

    /// <summary>
    /// AskUserQuestion 作答：原輸入加上 answers（題目文字 → 答案；多選以「, 」串接，與 Claude Code 自己的介面相同）。
    /// 有補充說明時另加 annotations（題目文字 → {notes}；實測 2.1.316，tools/probes/claude_question_notes_probe.py：CLI 交給模型
    /// 「"題目"="選項" notes: …」）
    /// </summary>
    public static JObject BuildQuestionAnswerInput(JObject originalInput, IReadOnlyDictionary<string, IReadOnlyList<string>> answers, IReadOnlyDictionary<string, string>? notes = null)
    {
        var updated = (JObject)(originalInput ?? new JObject()).DeepClone();
        var answerObject = new JObject();
        foreach (var (question, values) in answers ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            var cleaned = (values ?? Array.Empty<string>())
                .Select(v => v?.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
            if (cleaned.Count > 0)
                answerObject[question] = string.Join(", ", cleaned);
        }

        updated["answers"] = answerObject;
        var annotations = new JObject();
        foreach (var (question, note) in notes ?? new Dictionary<string, string>())
        {
            if (answerObject.ContainsKey(question) && !string.IsNullOrWhiteSpace(note))
                annotations[question] = new JObject { ["notes"] = note.Trim() };
        }
        if (annotations.Count > 0)
            updated["annotations"] = annotations;
        return updated;
    }

    /// <summary>
    /// 「本對話都允許」：把 CLI 建議的權限更新全部改成只在本 session 有效（建議常寫進 localSettings 設定檔，不要）。
    /// 權限模式切換（setMode）不放進來：會話另以 set_permission_mode 明確切換並記下目前模式，畫面才跟得上
    /// </summary>
    public static JArray ToSessionPermissions(JArray? suggestions)
    {
        var result = new JArray();
        foreach (var token in suggestions ?? new JArray())
        {
            if (token is not JObject suggestion || suggestion.Value<string>("type") == "setMode")
                continue;
            var copy = (JObject)suggestion.DeepClone();
            copy["destination"] = "session";
            result.Add(copy);
        }

        return result;
    }

    public static List<AgentQuestion> ParseAskUserQuestion(JObject input)
    {
        var result = new List<AgentQuestion>();
        foreach (var token in input?["questions"] as JArray ?? new JArray())
        {
            if (token is not JObject q)
                continue;
            var text = q.Value<string>("question") ?? string.Empty;
            if (text.Length == 0)
                continue;

            var question = new AgentQuestion
            {
                Id = text,
                Header = q.Value<string>("header"),
                Text = text,
                MultiSelect = q.Value<bool?>("multiSelect") ?? false,
                AllowOther = true,
            };
            foreach (var o in q["options"] as JArray ?? new JArray())
            {
                if (o is not JObject option)
                    continue;
                var label = option.Value<string>("label");
                if (string.IsNullOrWhiteSpace(label))
                    continue;
                question.Options.Add(new AgentQuestionOption
                {
                    Label = label,
                    Description = option.Value<string>("description"),
                    Preview = option.Value<string>("preview"),
                });
            }

            result.Add(question);
        }

        return result;
    }

    /// <summary>
    /// 背景工作看不看得到輸出（get_task_output 只認指令與 Monitor：實測 2.1.310 其他回「no shell or Monitor task with that task_id」）：
    /// 種類是 local_bash 或含 monitor；CLI 沒給種類時看工具卡是不是指令
    /// </summary>
    public static bool HasTaskOutput(string? taskType, string? toolKind)
        => taskType != null
            ? taskType.StartsWith("local_bash", StringComparison.OrdinalIgnoreCase) || taskType.Contains("monitor", StringComparison.OrdinalIgnoreCase)
            : string.Equals(toolKind, "bash", StringComparison.Ordinal);

    /// <summary>備援模型的參數值：沒設、和主模型相同（含都是預設）時不帶</summary>
    public static string? FallbackArgument(string? model, string? fallback)
    {
        var f = fallback?.Trim();
        if (string.IsNullOrEmpty(f) || string.Equals(f, "default", StringComparison.OrdinalIgnoreCase))
            return null;
        return string.Equals(f, model?.Trim(), StringComparison.OrdinalIgnoreCase) ? null : f;
    }

    /// <summary>
    /// initialize 回應的 agents（[{name, description, model}]）→ 自訂主代理的清單：claude 是內建的預設主代理（不列），沒有名字或名字不合格的略過，
    /// 照 CLI 的順序、同名只留第一個
    /// </summary>
    public static List<AgentNamedOption> ParseAgents(JObject? initializeResponse)
    {
        var list = new List<AgentNamedOption>();
        foreach (var a in (initializeResponse?["agents"] as JArray ?? new JArray()).OfType<JObject>())
        {
            var name = a["name"] is JValue { Type: JTokenType.String } n ? n.ToString().Trim() : null;
            if (name == null || !IsValidAgentName(name) || string.Equals(name, DefaultMainAgent, StringComparison.Ordinal) || list.Any(x => x.Value == name))
                continue;
            list.Add(new AgentNamedOption(name, a["description"] is JValue { Type: JTokenType.String } d ? OneLine(d.ToString(), 300) : null));
        }
        return list;
    }

    /// <summary>Claude 內建的預設主代理（不帶 --agent 就是它）</summary>
    public const string DefaultMainAgent = "claude";

    /// <summary>自訂代理名稱：外掛的帶命名空間（autopilot:debugger），只收英數字與 . _ - :，最多 128 字</summary>
    public static bool IsValidAgentName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or ':');

    /// <summary>initialize 回應的 available_output_styles（字串陣列）；沒有或不是陣列回空清單</summary>
    public static List<string> ParseOutputStyles(JObject? initializeResponse)
        => (initializeResponse?["available_output_styles"] as JArray ?? new JArray())
            .OfType<JValue>().Where(v => v.Type == JTokenType.String).Select(v => v.ToString().Trim())
            .Where(v => v.Length is > 0 and <= 100).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>執行中換輸出風格（apply_flag_settings 的 outputStyle；實測 2.1.310 下一輪 system/init 就是新的，CLI 另記一份新的系統提示快照）</summary>
    public static string BuildSetOutputStyleRequest(string requestId, string style)
        => BuildControlRequest(requestId, new JObject { ["subtype"] = "apply_flag_settings", ["settings"] = new JObject { ["outputStyle"] = style } });

    /// <summary>initialize 回應的 models → 模型選單（value 是 --model 要帶的值，default＝帳號預設）</summary>
    public static List<AgentModelOption> ParseModels(JObject? initializeResponse)
    {
        var result = new List<AgentModelOption>();
        foreach (var token in (initializeResponse?["models"] as JArray ?? new JArray()).OfType<JObject>())
        {
            var value = token["value"]?.ToString();
            if (string.IsNullOrWhiteSpace(value))
                continue;
            var efforts = (token["supportedEffortLevels"] as JArray ?? new JArray())
                .Select(e => e.ToString())
                .Where(e => e.Length > 0)
                .ToList();
            result.Add(new AgentModelOption(value, token["displayName"]?.ToString() ?? value, token["description"]?.ToString(), efforts));
        }

        return result;
    }

    /// <summary>initialize 回應的 commands → 「/」指令選單</summary>
    public static List<AgentSlashCommand> ParseCommands(JObject? initializeResponse)
    {
        var result = new List<AgentSlashCommand>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in (initializeResponse?["commands"] as JArray ?? new JArray()).OfType<JObject>())
        {
            var name = token["name"]?.ToString()?.TrimStart('/');
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__", StringComparison.Ordinal) || !seen.Add(name))
                continue;
            result.Add(new AgentSlashCommand(name, token["description"]?.ToString(), token["argumentHint"]?.ToString()));
        }

        return result;
    }

    /// <summary>initialize 回應的 account → （email、方案）</summary>
    public static (string? Email, string? Plan) ParseAccount(JObject? initializeResponse)
    {
        if (initializeResponse?["account"] is not JObject account)
            return (null, null);
        return (account["email"]?.ToString(), account["subscriptionType"]?.ToString());
    }

    /// <summary>工具的中文名稱、種類、一行摘要、展開細節、檔案與差異</summary>
    public static ToolDescription DescribeTool(string? toolName, JObject? input)
    {
        var name = toolName ?? "工具";
        input ??= new JObject();

        string? Str(string key) => input[key]?.Type == JTokenType.String ? input.Value<string>(key) : null;

        switch (name)
        {
            case "Bash":
            case "PowerShell":
            {
                var command = Str("command") ?? string.Empty;
                var description = Str("description");
                var detail = string.IsNullOrWhiteSpace(description) ? command : $"{description}\n\n{command}";
                return new ToolDescription(name == "Bash" ? "執行指令" : "執行 PowerShell", "bash", FirstLine(command), detail);
            }
            case "Read":
            {
                var path = Str("file_path") ?? string.Empty;
                return new ToolDescription("讀取檔案", AgentAttachmentRules.IsDisplayableMedia(path) ? "image" : "read", path, null, NullIfEmpty(path));
            }
            case "Write":
            {
                var path = Str("file_path") ?? string.Empty;
                return new ToolDescription("寫入檔案", "write", path, null, NullIfEmpty(path), DiffRules.FromNewFile(Str("content")));
            }
            case "Edit":
            {
                var path = Str("file_path") ?? string.Empty;
                return new ToolDescription("修改檔案", "edit", path, null, NullIfEmpty(path), DiffRules.FromEdit(Str("old_string"), Str("new_string")));
            }
            case "MultiEdit":
            {
                var path = Str("file_path") ?? string.Empty;
                var edits = (input["edits"] as JArray ?? new JArray()).OfType<JObject>()
                    .Select(e => (e.Value<string>("old_string"), e.Value<string>("new_string")))
                    .ToList();
                return new ToolDescription("修改檔案", "edit", path, null, NullIfEmpty(path), DiffRules.FromEdits(edits));
            }
            case "NotebookEdit":
            {
                var path = Str("notebook_path") ?? string.Empty;
                return new ToolDescription("修改筆記本", "edit", path, null, NullIfEmpty(path), DiffRules.FromNewFile(Str("new_source")));
            }
            case "Glob":
                return new ToolDescription("搜尋檔案", "search", JoinNonEmpty(Str("pattern"), Str("path")), null);
            case "Grep":
                return new ToolDescription("搜尋內容", "search", JoinNonEmpty(Str("pattern"), Str("path") ?? Str("glob")), null);
            case "WebFetch":
                return new ToolDescription("讀取網頁", "web", Str("url") ?? string.Empty, Str("prompt"));
            case "WebSearch":
                return new ToolDescription("網路搜尋", "web", Str("query") ?? string.Empty, null);
            case "Task":
            case "Agent":
                return new ToolDescription("子代理", "agent", Str("description") ?? "子代理工作", Truncate(Str("prompt"), 4000));
            case TodoWriteTool:
                return new ToolDescription("更新待辦", "todo", DescribeTodos(input["todos"] as JArray), null);
            case TaskCreateTool:
                return new ToolDescription("新增待辦", "todo", FirstNonBlank(Str("subject"), Str("title"), Str("name")) ?? string.Empty, Str("description"));
            case TaskUpdateTool:
                return new ToolDescription("更新待辦", "todo", JoinNonEmpty(TaskLabel(FirstNonBlank(Str("taskId"), Str("id"), Str("task_id"))), DescribeTaskStatus(Str("status")), Str("subject")), null);
            case TaskGetTool:
                return new ToolDescription("查看待辦", "todo", TaskLabel(FirstNonBlank(Str("taskId"), Str("id"), Str("task_id"))) ?? string.Empty, null);
            case TaskListTool:
                return new ToolDescription("列出待辦", "todo", string.Empty, null);
            case "Skill":
                return new ToolDescription("使用技能", "other", Str("skill") ?? Str("command") ?? string.Empty, Str("args"));
            case "ToolSearch":
                return new ToolDescription("載入工具", "other", Str("query") ?? string.Empty, null);
            case ExitPlanModeTool:
                return new ToolDescription("確認執行計畫", "plan", "代理已規劃好做法，請確認是否照計畫執行", Str("plan"));
        }

        if (name.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = name.Split("__", 3);
            var server = parts.Length > 1 ? parts[1] : name;
            var tool = parts.Length > 2 ? parts[2] : name;
            return new ToolDescription($"MCP：{server}", "mcp", tool, Truncate(input.ToString(Formatting.Indented), MaxToolDetailLength));
        }

        var firstString = input.Properties()
            .Where(p => p.Value.Type == JTokenType.String)
            .Select(p => p.Value.ToString())
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return new ToolDescription(name, "other", FirstLine(firstString ?? string.Empty), input.HasValues ? Truncate(input.ToString(Formatting.Indented), MaxToolDetailLength) : null);
    }

    /// <summary>TodoWrite 的 todos → 待辦清單步驟（in_progress 以 activeForm 顯示正在做什麼）</summary>
    public static List<AgentPlanStep> ParseTodos(JArray? todos)
    {
        var result = new List<AgentPlanStep>();
        foreach (var todo in (todos ?? new JArray()).OfType<JObject>())
        {
            var step = ToPlanStep(todo.Value<string>("content"), todo.Value<string>("activeForm"), todo.Value<string>("status"));
            if (step != null)
                result.Add(step);
        }
        return result;
    }

    /// <summary>一個待辦 → 清單步驟（CLI 的 pending／in_progress／completed → pending／inProgress／completed）；沒有文字回 null</summary>
    public static AgentPlanStep? ToPlanStep(string? content, string? activeForm, string? cliStatus)
    {
        var status = cliStatus switch
        {
            "completed" => "completed",
            "in_progress" => "inProgress",
            _ => "pending",
        };
        var text = status == "inProgress" && !string.IsNullOrWhiteSpace(activeForm) ? activeForm : content ?? string.Empty;
        return text.Length > 0 ? new AgentPlanStep { Text = text, Status = status } : null;
    }

    /// <summary>tool_result 的內容（字串或區塊陣列）轉純文字；圖片區塊以「[圖片]」表示</summary>
    public static string ExtractToolResultText(JToken? content)
    {
        if (content == null || content.Type == JTokenType.Null)
            return string.Empty;
        if (content.Type == JTokenType.String)
            return content.ToString();
        if (content is not JArray blocks)
            return content.ToString(Formatting.None);

        var parts = new List<string>();
        foreach (var block in blocks)
        {
            var type = block["type"]?.ToString();
            if (type == "text")
                parts.Add(block["text"]?.ToString() ?? string.Empty);
            else if (type == "image")
                parts.Add("[圖片]");
        }

        return string.Join("\n", parts);
    }

    // ───── 子代理的過程（parent_tool_use_id 不是 null 的訊息；實測 2.1.310，protocol-notes「子代理的過程」） ─────

    /// <summary>子代理過程一行的長度上限（工具標題、回覆、思考都壓成一行再截斷）</summary>
    internal const int MaxSubagentLineLength = 300;

    /// <summary>失敗的工具結果在過程裡留多少字</summary>
    internal const int MaxSubagentErrorLength = 160;

    /// <summary>
    /// 子代理一則訊息在主對話 Agent 工具卡上的過程行（格式同 Antigravity 的子代理執行紀錄，AntigravityTranscriptRules.FormatSubagentTranscript）：
    /// 子代理呼叫的工具「▸ 工具：標題」、子代理的回覆「💬 …」（帶 --forward-subagent-text 才有）、思考「💭 思考：…」、失敗的工具結果「  ✗ …」。
    /// 子代理收到的指示（user 的文字）已經是卡片的輸入，成功的工具結果不列（只看它做了什麼）；其他種類的訊息回空清單
    /// </summary>
    public static List<string> DescribeSubagentStep(JObject message)
    {
        var lines = new List<string>();
        if (message["message"]?["content"] is not JArray content)
            return lines;
        var type = message.Value<string>("type");
        foreach (var block in content.OfType<JObject>())
        {
            switch ((type, block.Value<string>("type")))
            {
                case ("assistant", "tool_use"):
                {
                    var tool = DescribeTool(block.Value<string>("name"), block["input"] as JObject ?? new JObject());
                    var title = OneLine(tool.Title, MaxSubagentLineLength);
                    lines.Add(title.Length > 0 ? $"▸ {tool.DisplayName}：{title}" : $"▸ {tool.DisplayName}");
                    break;
                }
                case ("assistant", "text") when OneLine(StringValue(block["text"]), MaxSubagentLineLength) is { Length: > 0 } text:
                    lines.Add($"💬 {text}");
                    break;
                case ("assistant", "thinking") when OneLine(StringValue(block["thinking"]), MaxSubagentLineLength) is { Length: > 0 } thinking:
                    lines.Add($"💭 思考：{thinking}");
                    break;
                case ("user", "tool_result") when block.Value<bool?>("is_error") == true:
                {
                    var error = OneLine(ExtractToolResultText(block["content"]), MaxSubagentErrorLength);
                    lines.Add(error.Length > 0 ? $"  ✗ {error}" : "  ✗ 失敗");
                    break;
                }
            }
        }
        return lines;
    }

    /// <summary>
    /// 子代理結束時工具卡的輸出：過程在前、結論（Agent 工具的結果）在後——中樞只留輸出的結尾（AgentHubRules.TrimToolOutput），
    /// 過程很長時被截掉的是前面的過程，結論一定留著。total 是過程的總行數（steps 可能只剩最後幾行）
    /// </summary>
    public static string WithSubagentTranscript(IReadOnlyCollection<string> steps, int total, string conclusion)
    {
        var omitted = total - steps.Count;
        var head = omitted > 0 ? $"── 子代理執行紀錄（共 {total} 項步驟，只列最後 {steps.Count} 項）──" : $"── 子代理執行紀錄（共 {total} 項步驟）──";
        return $"{head}\n{string.Join("\n", steps)}\n\n── 子代理結論 ──\n{conclusion}";
    }

    /// <summary>壓成一行（連續的空白與換行變成一個空白、去頭尾），超過 max 截斷並加「…」（不切開代理字組）</summary>
    internal static string OneLine(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        var collapsed = WhitespaceRun().Replace(text, " ").Trim();
        if (collapsed.Length <= max)
            return collapsed;
        int cut = char.IsHighSurrogate(collapsed[max - 1]) ? max - 1 : max;
        return collapsed[..cut].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    private static string? StringValue(JToken? token) => token?.Type == JTokenType.String ? token.ToString() : null;

    /// <summary>
    /// rate_limit_event 是訂閱額度被拒（UsageRules.IsClaudeQuotaRejection：帶類型或重置時間）時的提示文字；其他回 null——
    /// 被拒但兩個都沒帶的是暫時性的 429（「not your usage limit」），不能說額度用完
    /// </summary>
    public static string? DescribeRateLimitRejection(JObject? info)
    {
        if (!UsageRules.IsClaudeQuotaRejection(info))
            return null;
        var resets = info!.Value<long?>("resetsAt");
        return resets.HasValue
            ? $"Claude 訂閱額度已用完，{DateTimeOffset.FromUnixTimeSeconds(resets.Value).LocalDateTime:M/d HH:mm} 重置；可以把這個對話換到其他帳戶繼續"
            : "Claude 訂閱額度已用完；可以把這個對話換到其他帳戶繼續";
    }

    /// <summary>rate_limit_event 的 rate_limit_info 的 status 是 rejected（不論是不是額度用完；額度用完看 UsageRules.IsClaudeQuotaRejection）</summary>
    public static bool IsRateLimitRejected(JObject? info)
        => info?["status"] is JValue { Type: JTokenType.String } status && string.Equals((string?)status, "rejected", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// CLI 合成的 API 錯誤訊息（頂層的 assistant 訊息，model 是 &lt;synthetic&gt;）是不是訂閱額度用完：頂層 error 是 rate_limit，而且
    /// api_error 是 usage_limit_reached、api_error_params.rate_limit_info（或逐字稿的 quotaLimits）是額度被拒，或這一輪已收到額度被拒的
    /// rate_limit_event。stream-json 的欄位是蛇形（api_error、api_error_params；2.1.310 實測，tools/probes/claude_api_error_probe.py usage-limit），
    /// 逐字稿才是駝峰（apiError、apiErrorParams、quotaLimits、apiErrorIsTransient），兩種都認。
    /// 不算的：api_error 是別的原因（model_requires_usage_credits、long_context_credits_required 等，CLI 已經說了原因）；
    /// 暫時性的 429（stream-json 是「Server is temporarily limiting requests (not your usage limit)」，前面的 rejected 事件不帶類型與重置時間；
    /// 逐字稿另有 apiErrorIsTransient）
    /// </summary>
    public static bool IsUsageLimitMessage(JObject? message, bool rejectedThisTurn)
    {
        if (message?["error"] is not JValue { Type: JTokenType.String } error || !string.Equals((string?)error, "rate_limit", StringComparison.Ordinal))
            return false;
        if (message["apiErrorIsTransient"] is JValue { Type: JTokenType.Boolean } transient && (bool)transient.Value!)
            return false;
        if (ApiErrorOf(message) is { } apiError)
            return string.Equals(apiError, UsageLimitReached, StringComparison.Ordinal);
        var parameters = message["api_error_params"] as JObject ?? message["apiErrorParams"] as JObject;
        return UsageRules.IsClaudeQuotaRejection(message["quotaLimits"] as JObject)
               || UsageRules.IsClaudeQuotaRejection(parameters?["rate_limit_info"] as JObject)
               || rejectedThisTurn;
    }

    /// <summary>失敗的 result 帶 api_error usage_limit_reached（2.1.310 起 result 也帶，同 assistant 合成訊息的 api_error）</summary>
    public static bool IsUsageLimitResult(JObject? result)
        => result?["is_error"] is JValue { Type: JTokenType.Boolean } isError && (bool)isError.Value!
           && string.Equals(ApiErrorOf(result), UsageLimitReached, StringComparison.Ordinal);

    private const string UsageLimitReached = "usage_limit_reached";

    /// <summary>API 錯誤的類型（stream-json 的 api_error、逐字稿的 apiError）；沒有或不是字串回 null</summary>
    private static string? ApiErrorOf(JObject? message)
        => (message?["api_error"] ?? message?["apiError"]) is JValue { Type: JTokenType.String } value && !string.IsNullOrEmpty((string?)value)
            ? (string?)value
            : null;

    /// <summary>
    /// API 過載（529 Overloaded）而結束的一輪：CLI 自己重試幾次仍過載後合成的 assistant 訊息，或這一輪的 result。
    /// stream-json 的欄位是蛇形（api_error_status；逐字稿才是 apiErrorStatus，兩種都認）。實測（2.1.310，tools/probes/claude_api_error_probe.py）：
    /// assistant 帶 error server_error、is_api_error_message、api_error_status 529，文字「API Error: 529 Overloaded. …」；
    /// result 是 is_error、api_error_status 529、terminal_reason api_error、result 同一段文字。執行檔的錯誤分類另有 overloaded（529 或 overloaded_error），
    /// 連續 529 的「Repeated 529 Overloaded errors」不一定帶狀態碼，以文字認——文字只在 API 錯誤訊息（is_api_error_message）與失敗的 result 上比對，
    /// 代理在一般回覆裡提到 529 不算
    /// </summary>
    public static bool IsOverloadedApiError(JObject? message)
    {
        if (message == null)
            return false;
        if (IsStatus529(message["api_error_status"]) || IsStatus529(message["apiErrorStatus"]))
            return true;
        if (message["error"] is JValue { Type: JTokenType.String } error && string.Equals((string?)error, "overloaded", StringComparison.Ordinal))
            return true;

        string? text;
        if (string.Equals(message.Value<string>("type"), "result", StringComparison.Ordinal))
        {
            if (message["is_error"] is not JValue { Type: JTokenType.Boolean } isError || !(bool)isError.Value!)
                return false;
            text = TextOf(message["result"]);
        }
        else
        {
            bool apiError = message["is_api_error_message"] is JValue { Type: JTokenType.Boolean } snake && (bool)snake.Value!
                            || message["isApiErrorMessage"] is JValue { Type: JTokenType.Boolean } camel && (bool)camel.Value!;
            if (!apiError || (message["message"] as JObject)?["content"] is not JArray content)
                return false;
            text = content.OfType<JObject>().Select(b => TextOf(b["text"])).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        }
        return text != null
               && (text.StartsWith("API Error: 529", StringComparison.Ordinal) || text.Contains("Repeated 529 Overloaded errors", StringComparison.Ordinal));

        static bool IsStatus529(JToken? status)
            => status is JValue { Type: JTokenType.Integer } value && Convert.ToInt64(value.Value, System.Globalization.CultureInfo.InvariantCulture) == 529;

        static string? TextOf(JToken? token) => token?.Type == JTokenType.String ? token.ToString() : null;
    }

    /// <summary>
    /// 額度用完的合成訊息帶的重置時間（stream-json 的 api_error_params.rate_limit_info，或逐字稿的 quotaLimits、apiErrorParams.rate_limit_info 的
    /// resetsAt，秒）；沒有回 null
    /// </summary>
    public static DateTimeOffset? UsageLimitResetsAt(JObject? message)
        => ResetsAtOf((message?["api_error_params"] as JObject)?["rate_limit_info"] as JObject)
           ?? ResetsAtOf(message?["quotaLimits"] as JObject)
           ?? ResetsAtOf((message?["apiErrorParams"] as JObject)?["rate_limit_info"] as JObject);

    /// <summary>rate_limit_info 的 resetsAt（秒）；沒有或不是數字回 null</summary>
    public static DateTimeOffset? ResetsAtOf(JObject? info)
    {
        if (info?["resetsAt"] is not JValue { Type: JTokenType.Integer or JTokenType.Float } value)
            return null;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value.Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private static string DescribeTodos(JArray? todos)
    {
        if (todos == null || todos.Count == 0)
            return string.Empty;
        int done = todos.Count(t => t?["status"]?.ToString() == "completed");
        var current = todos.FirstOrDefault(t => t?["status"]?.ToString() == "in_progress");
        var currentText = current?["activeForm"]?.ToString() ?? current?["content"]?.ToString();
        return string.IsNullOrWhiteSpace(currentText)
            ? $"{done}/{todos.Count} 完成"
            : $"{done}/{todos.Count} 完成，進行中：{currentText}";
    }

    /// <summary>一行摘要：規則在 Protocol 的 TextRules（伺服器也要用，例如 AgentHubRules 的拒絕說明），這裡只轉呼叫</summary>
    internal static string FirstLine(string? text) => TextRules.FirstLine(text);

    /// <summary>工具卡細節裡參數 JSON 的上限（MCP 與不認得的工具；Codex、Grok、Antigravity 照同一個上限）</summary>
    internal const int MaxToolDetailLength = 4000;

    internal static string? Truncate(string? text, int max)
    {
        if (text == null)
            return null;
        return text.Length <= max ? text : text[..max] + $"\n…（共 {text.Length:N0} 字，以下省略）";
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>非空白的值以全形空白連起來（搜尋卡的「樣式　資料夾」；Grok 的搜尋卡照同一個寫法，GrokAcpRules.DescribeTool）</summary>
    internal static string JoinNonEmpty(params string?[] values)
        => string.Join("　", values.Where(v => !string.IsNullOrWhiteSpace(v)));

    internal static string? FirstNonBlank(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? TaskLabel(string? taskId) => string.IsNullOrWhiteSpace(taskId) ? null : $"#{taskId}";

    private static string? DescribeTaskStatus(string? status) => status switch
    {
        "pending" => "待處理",
        "in_progress" => "進行中",
        "completed" => "已完成",
        "deleted" => "已刪除",
        _ => null,
    };

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NonAlphanumeric();
}

/// <summary>
/// 逐行解析 claude stream-json 輸出（一個程序一個實例，只由讀取 stdout 的那條執行緒呼叫）。
/// 文字與思考用 stream_event 的 delta 串流、在 content_block_stop 收尾；工具卡在完整的 assistant 區塊到達時建立
/// （實測 can_use_tool 會比該區塊的 content_block_stop 先到，工具卡要比核准卡早出現）；
/// TodoWrite 不畫工具卡，改成待辦清單事件；TaskCreate／TaskUpdate（2.1.295 起舊模型的待辦工具）照 CLI 自己的重建方式累積成待辦清單；
/// parent_tool_use_id 非 null 的是子代理內部訊息：不畫成主對話的項目，只把它做了什麼（呼叫的工具、回覆、失敗）接在主對話那張 Agent 工具卡的輸出（子代理的過程）。
/// 熱更新的安全點判斷也在這裡追蹤（ToolRunning、BackgroundTaskIds）：主對話的 tool_use 從 content_block_start 起到對應的 tool_result 為止都算工具在執行
/// （伺服器端工具 server_tool_use 不算：它在 API 那一端執行，重開只會讓 CLI 重送請求）；背景工作以 system/background_tasks_changed 的清單為準（取代式），
/// task_started（is_backgrounded）與 task_notification 只當輔助。
/// </summary>
internal sealed partial class ClaudeStreamParser
{
    private sealed class BlockState
    {
        public string Kind = string.Empty;
        public string BlockId = string.Empty;
        public string? ToolUseId;
        public string? ToolName;
        public bool Open = true;
        public readonly System.Text.StringBuilder Buffer = new();
    }

    /// <summary>執行中的工具（主對話的 tool_use 起到 tool_result）、已結束的工具（不再加回）、畫了工具卡的工具</summary>
    private readonly HashSet<string> runningTools = new(StringComparer.Ordinal);
    private readonly HashSet<string> finishedTools = new(StringComparer.Ordinal);
    private readonly HashSet<string> cardToolIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 只在串流裡開始（content_block_start）、還沒收到完整 assistant 區塊的主對話工具 → 所在的訊息。串流在途中斷、CLI 重送請求時（api_retry），
    /// 被丟下的那則回應不會有 assistant 區塊、工具也不會有結果，重試的回應是新的訊息與新的工具 id：主對話的下一則訊息開始時還留著的就拿掉，
    /// 不然這一輪剩下的時間都算工具在執行、等不到安全點（2026-10-31 實機：ECONNRESET 重試之後換版排空 35 分鐘找不到安全點而放棄）
    /// </summary>
    private readonly Dictionary<string, string> streamOnlyTools = new(StringComparer.Ordinal);

    /// <summary>子代理過程最多留幾行（工具卡的輸出本來就只留結尾，這裡只是不讓很久的子代理無限累積）</summary>
    internal const int MaxSubagentSteps = 200;

    /// <summary>主對話畫了卡的 Agent／Task 工具：子代理的過程接在它們的輸出</summary>
    private readonly HashSet<string> agentCardIds = new(StringComparer.Ordinal);

    /// <summary>子代理自己呼叫的 Agent 工具 → 主對話那張卡（巢狀的子代理也接在最外層的卡上）</summary>
    private readonly Dictionary<string, string> subagentRoots = new(StringComparer.Ordinal);

    /// <summary>各張 Agent 卡到目前的子代理過程（最後 MaxSubagentSteps 行）與總行數：工具結果到的時候放在結論前面</summary>
    private readonly Dictionary<string, SubagentLog> subagentLogs = new(StringComparer.Ordinal);

    private sealed class SubagentLog
    {
        public readonly Queue<string> Lines = new();
        public int Total;
    }

    /// <summary>存活的背景工作（background_tasks_changed 的清單；保持加入順序）</summary>
    private readonly List<string> backgroundTasks = new();

    /// <summary>
    /// 前景指令的 task_id（task_started 的 is_backgrounded 是 false）：2.1.296 實測跑得久的前景指令結束時也送 task_notification completed
    /// （緊接著 tool_result，之後 CLI 不會自己開一輪），不能當成背景工作完成
    /// </summary>
    private readonly HashSet<string> foregroundTasks = new(StringComparer.Ordinal);

    /// <summary>有工具在執行（主對話的 tool_use 已開始、還沒收到 tool_result）</summary>
    public bool ToolRunning => runningTools.Count > 0;

    /// <summary>存活的背景工作 id（run_in_background 的指令、背景子代理）</summary>
    public IReadOnlyList<string> BackgroundTaskIds => backgroundTasks;

    /// <summary>畫了工具卡、還沒有結果的工具（tool_use_id → 卡片的說明）：task_started 以 tool_use_id 對到它，記下背景工作是哪個工具開始的</summary>
    private readonly Dictionary<string, AgentBackgroundTool> openCardTools = new(StringComparer.Ordinal);

    /// <summary>工作（task_id）是由哪個工具開始的（task_started 的 tool_use_id 對到的工具卡；工作結束就移除）</summary>
    private readonly Dictionary<string, AgentBackgroundTool> taskTools = new(StringComparer.Ordinal);

    /// <summary>工作的種類（task_type：local_bash、local_agent…；工作結束就移除）：只有指令與 Monitor 看得到輸出</summary>
    private readonly Dictionary<string, string> taskTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// 存活的背景工作是由哪些工具開始的（依 background_tasks_changed 的順序；對不到工具的不列）：中樞在「立即套用」打斷背景工作時
    /// 以它們判斷高風險操作（背景跑的部署、發布被打斷，同前景的工具一樣不自動接續，設計 7.7）。每一個都能以 stop_task 停止（StopId 是 task_id）
    /// </summary>
    public IReadOnlyList<AgentBackgroundTool> BackgroundTools
        => backgroundTasks.Count == 0
            ? Array.Empty<AgentBackgroundTool>()
            : backgroundTasks.Select(id => taskTools.TryGetValue(id, out var tool)
                    ? tool with { TaskId = ClaudeStreamRules.HasTaskOutput(taskTypes.GetValueOrDefault(id), tool.ToolKind) ? id : null, StopId = id }
                    : null).OfType<AgentBackgroundTool>().ToList();

    /// <summary>最近一個主對話工具結果的 tool_use_id（重開前要等逐字稿也寫入它）</summary>
    public string? LastToolResultId { get; private set; }

    /// <summary>主對話最近一則 assistant 訊息的 stop_reason（message_delta）；新訊息開始時清成 null</summary>
    public string? LastStopReason { get; private set; }

    /// <summary>這一輪各則主對話訊息（message id）已記的用量欄位（MergeMessageUsage）：message_start 先記輸入，message_delta 補上輸出，token 計數只發增量</summary>
    private readonly Dictionary<string, long[]> messageUsage = new(StringComparer.Ordinal);

    /// <summary>主對話各則訊息的模型（message_start 的 message.model）：用量記在這個模型</summary>
    private readonly Dictionary<string, string?> messageModels = new(StringComparer.Ordinal);

    /// <summary>
    /// 上一則 result 之後已記過的 token，依模型（四個欄位：input、cache_creation、cache_read、output）：主對話的 message_start／message_delta 與子代理逐字稿
    /// （AddSubagentUsage）。result 時和 modelUsage 的差比對，只補串流與逐字稿都看不到的呼叫（壓縮、CLI 自己的小模型呼叫…）
    /// </summary>
    private readonly Dictionary<string, long[]> countedSinceResult = new(StringComparer.Ordinal);

    /// <summary>
    /// 上一則 result 的 modelUsage，依模型（這個程序的累計；--resume 開的程序含恢復的舊用量）。這個程序還沒有 result 時是 null（沒有基準，第一則不補）；
    /// 新的 session（<see cref="FreshSession"/>）從 0 起算
    /// </summary>
    private Dictionary<string, long[]>? lastModelUsage;

    /// <summary>這個程序開的是新的 session（不是 --resume）：modelUsage 從 0 起算，第一則 result 也能補（建立解析器時設定）</summary>
    public bool FreshSession
    {
        get => lastModelUsage is { Count: 0 };
        init => lastModelUsage = value ? new Dictionary<string, long[]>(StringComparer.Ordinal) : null;
    }

    /// <summary>主對話最近一則訊息的模型（message_start 的 message.model）：result 時取 modelUsage 裡這個模型的 contextWindow</summary>
    private string? contextModel;

    /// <summary>上次發出的上下文大小（同一則訊息的 message_start 與 message_delta 一樣時不重發）；壓縮後清掉</summary>
    private long? lastContextTokens;

    /// <summary>
    /// 重開時把舊程序串流到一半的東西結束為已中斷（讀取執行緒已停下後才呼叫）：還沒收尾的回覆文字接上 InterruptedTextNote，
    /// 畫了工具卡、還沒有結果的工具改為已取消（只有立即重開會有）
    /// </summary>
    public List<AgentEvent> InterruptOpenWork()
    {
        var events = new List<AgentEvent>();
        foreach (var state in blocks.Values.Where(b => b.Open && b.Kind == "text"))
        {
            state.Open = false;
            events.Add(new AgentTextCompletedEvent(state.BlockId, ClaudeStreamRules.MarkInterruptedText(state.Buffer.ToString())));
        }
        foreach (var id in runningTools.Where(cardToolIds.Contains).ToList())
            events.Add(new AgentToolCompletedEvent(id, AgentItemStatus.Cancelled, ClaudeStreamRules.InterruptedToolOutput, Array.Empty<string>()));
        foreach (var id in runningTools)
            finishedTools.Add(id);
        runningTools.Clear();
        streamOnlyTools.Clear();
        return events;
    }

    /// <summary>TaskCreate／TaskUpdate 累積的一個待辦（status 是 CLI 的 pending／in_progress／completed）</summary>
    private sealed record TaskEntry(string Content, string ActiveForm, string Status);

    private readonly HashSet<string> streamedMessageIds = new();
    private readonly Dictionary<int, BlockState> blocks = new();
    private readonly HashSet<string> startedToolIds = new();
    private readonly Dictionary<string, string> toolNames = new();
    private readonly Dictionary<string, string> toolFilePaths = new();

    /// <summary>已建立的待辦（依編號，保持建立順序）與還在等 TaskCreate 結果給編號的待辦（依 tool_use_id）</summary>
    private readonly OrderedDictionary<string, TaskEntry> tasks = new(StringComparer.Ordinal);
    private readonly OrderedDictionary<string, TaskEntry> pendingTaskCreates = new(StringComparer.Ordinal);

    /// <summary>畫成待辦清單、不畫工具卡的 TaskCreate／TaskUpdate（它們的 tool_result 不回報）</summary>
    private readonly HashSet<string> planToolIds = new(StringComparer.Ordinal);
    private string currentMessageId = string.Empty;
    private int syntheticBlockCounter;

    /// <summary>這一輪已提示過「連線不穩，正在重試」（CLI 每次重試都送 api_retry，只提示一次）</summary>
    private bool retryNoticeShown;

    /// <summary>這一輪看到 CLI 插進的「[Request interrupted by user…]」（ClaudeStreamRules.IsInterruptMarker）：接著失敗的 result 是被停止，不是出錯</summary>
    private bool interruptMarkerSeen;

    /// <summary>
    /// 這一輪收到額度被拒的 rate_limit_event（UsageRules.IsClaudeQuotaRejection）與它的重置時間：接著以錯誤結束就算額度用完（AgentQuotaExhaustedEvent）。
    /// 被拒但沒帶類型與重置時間的（暫時性的 429）不記
    /// </summary>
    private bool rateLimitRejected;
    private DateTimeOffset? rateLimitRejectedResetsAt;

    /// <summary>這一輪已發過 AgentQuotaExhaustedEvent（一輪最多一次）</summary>
    private bool quotaExhaustedReported;

    /// <summary>
    /// 這一輪的 rate_limit 錯誤訊息已經判定不是額度用完（CLI 說了別的原因、標成暫時性）：接著失敗的 result 不再以被拒的 rate_limit_event 當成額度用完
    /// </summary>
    private bool rateLimitErrorNotQuota;

    /// <summary>這一輪收到 API 過載（529）的合成訊息：接著失敗的 result 標成 ServerBusy＝Overloaded（中樞等一下自動重試）</summary>
    private bool overloadedThisTurn;

    /// <summary>
    /// 已提示過以 API Key 計費（init 每輪都送；會話重開程序時把這個值帶給新的解析器，一個對話只提示一次）
    /// </summary>
    public bool ApiKeyNoticeShown { get; set; }

    public string? GetToolName(string toolUseId)
        => toolNames.TryGetValue(toolUseId, out var name) ? name : null;

    public ClaudeParseResult Parse(string line)
    {
        var result = new ClaudeParseResult();
        if (string.IsNullOrWhiteSpace(line))
            return result;

        JObject message;
        try
        {
            message = JObject.Parse(line);
        }
        catch (JsonException)
        {
            return result;
        }

        switch (message.Value<string>("type"))
        {
            case "system":
                ParseSystem(message, result);
                break;
            case "stream_event":
                if (IsSubagent(message))
                    break;
                result.TurnActivity = true;
                ParseStreamEvent(message["event"] as JObject, result);
                break;
            case "assistant":
                if (IsSubagent(message))
                {
                    ParseSubagent(message, result);
                    break;
                }
                result.TurnActivity = true;
                ParseAssistant(message["message"] as JObject, result);
                // CLI 合成的「You've hit your session limit」：額度用完，這一輪接著以錯誤結束。其他 rate_limit 錯誤（暫時性的 429、
                // 要額外用量的模型）記下來：接著的失敗不以被拒的 rate_limit_event 當成額度用完
                if (!quotaExhaustedReported && ClaudeStreamRules.IsUsageLimitMessage(message, rateLimitRejected))
                {
                    quotaExhaustedReported = true;
                    result.Events.Add(new AgentQuotaExhaustedEvent(AgentProvider.Claude, ClaudeStreamRules.UsageLimitResetsAt(message) ?? rateLimitRejectedResetsAt));
                }
                else if (message["error"] is JValue { Type: JTokenType.String } rateLimitError && (string?)rateLimitError == "rate_limit")
                {
                    rateLimitErrorNotQuota = true;
                }
                // CLI 合成的「API Error: 529 Overloaded」：CLI 自己的重試用完了，這一輪接著以錯誤結束
                if (ClaudeStreamRules.IsOverloadedApiError(message))
                    overloadedThisTurn = true;
                break;
            case "user":
                if (IsSubagent(message))
                {
                    ParseSubagent(message, result);
                    break;
                }
                if (message["isReplay"] is JValue { Type: JTokenType.Boolean } replay && (bool)replay.Value!)
                {
                    // --replay-user-messages 回送的使用者訊息（app 自己的、遠端送來的、CLI 插入的說明）：不是工具結果，
                    // 也不算這一輪的活動（CLI 換模型時的說明等可能在兩輪之間回送）
                    result.ReplayUuid = StringOf(message["uuid"]);
                    result.RemoteUserMessage = ClaudeStreamRules.ParseRemoteUserMessage(message);
                    break;
                }
                // 停止的標記（app 或 claude.ai／Claude App 按停止都有）：只影響這一輪 result 的判讀，不顯示
                if (ClaudeStreamRules.IsInterruptMarker(message["message"] as JObject))
                    interruptMarkerSeen = true;
                ParseUser(message["message"] as JObject, result);
                break;
            case "control_request":
                result.ControlRequest = ParseControlRequest(message);
                // 核准與 MCP 伺服器的 elicitation（工具執行到一半要使用者輸入）都是這一輪在等使用者
                if (result.ControlRequest?.Subtype is "can_use_tool" or "elicitation")
                    result.TurnActivity = true;
                break;
            case "control_response":
                result.ControlResponse = ParseControlResponse(message["response"] as JObject);
                break;
            case "control_cancel_request":
                // CLI 撤回尚未回應的請求（工具被中止、有逾時的詢問被提問方收回）
                result.CancelledRequestId = message.Value<string>("request_id");
                break;
            case "command_lifecycle":
                // 帶 uuid 排進佇列的指令的狀態：app 送的訊息、遠端送來的訊息、CLI 自動接續被打斷的一輪（started 比 initialize 回應還早）
                result.CommandLifecycleState = message.Value<string>("state");
                result.CommandUuid = StringOf(message["command_uuid"]);
                break;
            case "result":
                retryNoticeShown = false;
                result.TurnCompleted = ParseResult(message);
                // 被停止的一輪（例如在手機上按停止）是 error_during_execution：顯示成已停止，不是失敗
                if (interruptMarkerSeen && result.TurnCompleted.Outcome == AgentTurnOutcome.Failed)
                    result.TurnCompleted = result.TurnCompleted with { Outcome = AgentTurnOutcome.Interrupted, ErrorMessage = null };
                interruptMarkerSeen = false;
                // 沒看到帶標記的合成訊息、以錯誤結束：result 帶 api_error usage_limit_reached，或額度被拒的 rate_limit_event 之後失敗
                // （合成訊息已判定是別的 rate_limit 錯誤時不算），同樣算額度用完；正常結束（例如改用額外用量）、被停止的不算
                if (!quotaExhaustedReported && result.TurnCompleted.Outcome == AgentTurnOutcome.Failed
                    && (ClaudeStreamRules.IsUsageLimitResult(message) || rateLimitRejected && !rateLimitErrorNotQuota))
                    result.Events.Add(new AgentQuotaExhaustedEvent(AgentProvider.Claude, rateLimitRejectedResetsAt));
                rateLimitRejected = false;
                rateLimitRejectedResetsAt = null;
                quotaExhaustedReported = false;
                rateLimitErrorNotQuota = false;
                // API 過載而失敗（合成訊息或 result 本身帶 529）：中樞等一下自動重試（AgentHub.OverloadRetry.cs）；被停止的不算
                if (result.TurnCompleted.Outcome == AgentTurnOutcome.Failed && (overloadedThisTurn || ClaudeStreamRules.IsOverloadedApiError(message)))
                    result.TurnCompleted = result.TurnCompleted with { ServerBusy = AgentServerBusy.Overloaded };
                overloadedThisTurn = false;
                result.ResumeReason = ClaudeStreamRules.ParseResumeReason(message);
                result.TurnFromOtherOrigin = !ClaudeStreamRules.IsUserTurnResult(message);
                result.UserMessageUuid = StringOf(message["user_message_uuid"]);
                // 一輪結束：這一輪的工具都已有結果（被中止的不會再回報）
                foreach (var id in runningTools)
                    finishedTools.Add(id);
                runningTools.Clear();
                streamOnlyTools.Clear();
                // stop_reason 只屬於這一輪：下一輪送出後、第一則 assistant 訊息之前被重開，不能拿上一輪的 end_turn 當成「已回覆完」
                LastStopReason = null;
                CloseTurnTokens(message, result);
                break;
            case "rate_limit_event":
            {
                if (message["rate_limit_info"] is JObject info)
                {
                    result.Events.Add(new AgentRateLimitEvent(AgentProvider.Claude, info));
                    var rejected = ClaudeStreamRules.DescribeRateLimitRejection(info);
                    if (rejected != null)
                    {
                        result.Events.Add(new AgentNoticeEvent(rejected, IsError: true));
                        rateLimitRejected = true;
                        rateLimitRejectedResetsAt = ClaudeStreamRules.ResetsAtOf(info) ?? rateLimitRejectedResetsAt;
                    }
                    else if (!ClaudeStreamRules.IsRateLimitRejected(info))
                    {
                        // 之後又回報可以用（重置了、改用額外用量）：先前的被拒不再算
                        rateLimitRejected = false;
                        rateLimitRejectedResetsAt = null;
                    }
                    // 被拒但沒帶類型與重置時間（暫時性的 429，「not your usage limit」）：不提示額度用完、不記成被拒，也不取消先前的被拒
                }
                break;
            }
        }

        return result;
    }

    private static bool IsSubagent(JObject message)
        => message["parent_tool_use_id"] is JToken t && t.Type == JTokenType.String && t.ToString().Length > 0;

    /// <summary>
    /// 子代理的一則訊息（assistant 或 user，parent_tool_use_id 是它所在的 Agent 工具）：過程行（ClaudeStreamRules.DescribeSubagentStep）以 AgentToolOutputEvent
    /// 接在主對話那張 Agent 卡的輸出，並記下來，等卡片的結果到了放在結論前面。不算這一輪的活動、不算工具在執行（安全點只看主對話的工具，
    /// 子代理整段由主對話的 Agent 工具涵蓋），也不記用量（result 時由 modelUsage 補上）。卡片已經有結果（背景子代理的結果一開始就回來）就不接
    /// </summary>
    private void ParseSubagent(JObject message, ClaudeParseResult result)
    {
        var parent = message.Value<string>("parent_tool_use_id")!;
        var root = subagentRoots.TryGetValue(parent, out var outer) ? outer : parent;
        if (!agentCardIds.Contains(root) || finishedTools.Contains(root))
            return;

        // 子代理自己又派了子代理：之後 parent 是這個工具的訊息也接在同一張卡上
        if (message.Value<string>("type") == "assistant" && message["message"]?["content"] is JArray content)
        {
            foreach (var block in content.OfType<JObject>())
            {
                if (block.Value<string>("type") == "tool_use" && block.Value<string>("id") is { Length: > 0 } id
                    && block.Value<string>("name") is "Agent" or "Task")
                    subagentRoots[id] = root;
            }
        }

        var lines = ClaudeStreamRules.DescribeSubagentStep(message);
        if (lines.Count == 0)
            return;
        if (!subagentLogs.TryGetValue(root, out var log))
            subagentLogs[root] = log = new SubagentLog();
        foreach (var line in lines)
        {
            log.Lines.Enqueue(line);
            log.Total++;
            if (log.Lines.Count > MaxSubagentSteps)
                log.Lines.Dequeue();
        }
        result.Events.Add(new AgentToolOutputEvent(root, string.Concat(lines.Select(l => l + "\n"))));
    }

    /// <summary>字串欄位的值；沒有、JSON null 或不是字串回 null</summary>
    private static string? StringOf(JToken? token) => token?.Type == JTokenType.String ? token.ToString() : null;

    private void ParseSystem(JObject message, ClaudeParseResult result)
    {
        switch (message.Value<string>("subtype"))
        {
            case "init":
            {
                var sessionId = message.Value<string>("session_id") ?? string.Empty;
                var apiKeySource = message.Value<string>("apiKeySource");
                var version = message["claude_code_version"]?.Type == JTokenType.String ? message.Value<string>("claude_code_version") : null;
                result.Events.Add(new AgentSessionStartedEvent(sessionId, message.Value<string>("model"),
                    $"Claude Code {version}"));
                result.TurnInit = true;
                result.CliVersion = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
                if (!ApiKeyNoticeShown && !string.IsNullOrEmpty(apiKeySource) && !string.Equals(apiKeySource, "none", StringComparison.OrdinalIgnoreCase))
                {
                    ApiKeyNoticeShown = true;
                    result.Events.Add(new AgentNoticeEvent($"注意：Claude 目前以 API Key（{apiKeySource}）計費，不是訂閱額度", IsError: false));
                }
                AddPermissionMode(message, result);
                break;
            }
            case "status":
                // 權限模式改變時 CLI 會送（set_permission_mode、本對話都允許的 setMode、代理自己進出規劃模式）；
                // status requesting＝這一輪的 API 請求已送出（CLI 已接手剛送出的訊息）
                if (message.Value<string>("status") == "requesting")
                    result.TurnActivity = true;
                AddPermissionMode(message, result);
                break;
            case "background_tasks_changed":
            {
                // 所有存活背景工作的完整清單（取代式；E-11）：程序重開就清空，不必配對 started／notification
                var tasks = (message["tasks"] as JArray ?? new JArray()).OfType<JObject>().ToList();
                var ids = tasks
                    .Select(t => t["task_id"]?.Type == JTokenType.String ? t.Value<string>("task_id") : null)
                    .OfType<string>()
                    .Where(id => id.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                backgroundTasks.Clear();
                backgroundTasks.AddRange(ids);
                foreach (var task in tasks)
                {
                    MapTaskTool(task.Value<string>("task_id"), task["tool_use_id"]?.Type == JTokenType.String ? task.Value<string>("tool_use_id") : null);
                    RememberTaskType(task);
                }
                break;
            }
            case "task_started":
            {
                // 輔助：前景指令也送 task_started，但 is_backgrounded 是 false
                var id = message["task_id"]?.Type == JTokenType.String ? message.Value<string>("task_id") : null;
                var backgrounded = message.Value<bool?>("is_backgrounded");
                if (!string.IsNullOrEmpty(id) && backgrounded == true && !backgroundTasks.Contains(id))
                    backgroundTasks.Add(id);
                else if (!string.IsNullOrEmpty(id) && backgrounded == false)
                    foregroundTasks.Add(id);
                // 前景的也記下：跑久了被移到背景時 background_tasks_changed 會列著它
                var toolUseId = message["tool_use_id"]?.Type == JTokenType.String ? message.Value<string>("tool_use_id") : null;
                MapTaskTool(id, toolUseId);
                RememberTaskType(message);
                // 背景活動日誌（指令與子代理的開始；中樞不據此改畫面）
                if (!string.IsNullOrEmpty(id))
                    result.Events.Add(new AgentTaskLifecycleEvent(id, Ended: false, ClaudeStreamRules.StringField(message, "task_type"), ClaudeStreamRules.StringField(message, "description"),
                        toolUseId, backgrounded, Status: null, Summary: null));
                break;
            }
            case "task_notification":
            {
                // 背景工作結束（completed／stopped／failed）：之後的 background_tasks_changed 也會拿掉它
                var id = message["task_id"]?.Type == JTokenType.String ? message.Value<string>("task_id") : null;
                if (!string.IsNullOrEmpty(id))
                {
                    // 前景指令（沒有被移到背景）結束：不是背景工作完成，CLI 不會自己開一輪——不然工具結束後 30 秒都不算安全點
                    bool foreground = foregroundTasks.Remove(id) && !backgroundTasks.Contains(id);
                    backgroundTasks.Remove(id);
                    taskTools.Remove(id);
                    taskTypes.Remove(id, out var taskType);
                    // 背景活動日誌（結束的狀態與摘要；重開時被丟下的是 stopped＋「Orphaned by…」）
                    result.Events.Add(new AgentTaskLifecycleEvent(id, Ended: true, taskType ?? ClaudeStreamRules.StringField(message, "task_type"), null,
                        ClaudeStreamRules.StringField(message, "tool_use_id"), Backgrounded: !foreground, ClaudeStreamRules.StringField(message, "status"), ClaudeStreamRules.StringField(message, "summary")));
                    // 子代理結束：最後的用量（帶 tool_use_id 與 usage 的才有）
                    if (ClaudeEventRules.Subagent(message, done: true) is { } finished)
                        result.Events.Add(new AgentSubagentUsageEvent(finished));
                    if (foreground)
                        break;
                }
                result.TaskNotificationStatus = message.Value<string>("status") ?? string.Empty;
                break;
            }
            case "task_progress":
                // 子代理每做一步送一次：token 合計、工具次數、最近的工具（實測 2.1.310，claude_cli7_probe.py --mode events）
                if (ClaudeEventRules.Subagent(message, done: false) is { } progress)
                    result.Events.Add(new AgentSubagentUsageEvent(progress));
                break;
            case "model_fallback":
                // 主模型不能用、改用 --fallback-model 接手（實測主模型不存在時 trigger 是 model_not_found）
                result.Events.Add(new AgentNoticeEvent(ClaudeEventRules.ModelFallback(message), IsError: false));
                break;
            case "permission_denied":
                // 權限規則、自動模式的判斷或 hook 擋下了一個工具（工具卡另外會以錯誤結束）
                result.Events.Add(new AgentNoticeEvent(ClaudeEventRules.PermissionDenied(message), IsError: false));
                break;
            case "notification":
                if (ClaudeEventRules.Notification(message) is { } note)
                    result.Events.Add(new AgentNoticeEvent(note.Text, note.IsError));
                break;
            case "memory_saved":
                if (ClaudeEventRules.MemorySaved(message) is { } saved)
                    result.Events.Add(new AgentNoticeEvent(saved, IsError: false));
                break;
            case "compact_boundary":
                // compact_metadata.trigger 是 manual（/compact）或 auto（實測 2.1.309）；post_tokens 只算摘要、不含系統提示與工具，
                // 不能當成壓縮後的上下文大小：上下文提示先清掉用量，等下一次呼叫
                result.Events.Add(new AgentNoticeEvent(
                    (message["compact_metadata"] as JObject)?.Value<string>("trigger") == "manual"
                        ? "已壓縮較早的對話內容"
                        : "對話已很長，Claude 自動壓縮了較早的內容",
                    IsError: false));
                lastContextTokens = null;
                result.Events.Add(new AgentContextUsageEvent(null, null, Compacted: true));
                break;
            case "session_title_changed":
            {
                // 工作階段名稱改了（在 claude.ai／Claude App 改名、宿主自己的 rename_session 也回送；AI 產生的標題不送）：
                // 啟動時已有名稱也送一次、可能早於 init、同名可能重複（執行檔說明，2.1.308）
                var title = StringOf(message["title"])?.Trim();
                if (!string.IsNullOrEmpty(title))
                    result.Events.Add(new AgentSessionTitleEvent(title));
                break;
            }
            case "bridge_state":
                // Remote Control 的連線狀態（開啟後 ready → connected；失敗時 failed＋failure_kind、組織政策停用時 policy_disabled）
                result.BridgeState = StringOf(message["state"]) ?? string.Empty;
                result.BridgeFailureKind = StringOf(message["failure_kind"]);
                result.BridgeDetail = StringOf(message["detail"]);
                break;
            case "api_retry":
                if (!retryNoticeShown)
                {
                    retryNoticeShown = true;
                    result.Events.Add(new AgentNoticeEvent("Claude 連線不穩，正在重試…", IsError: false));
                }
                break;
        }
    }

    /// <summary>記下工作（task_id）是由哪個工具開始的（tool_use_id 對到還沒有結果的工具卡；已經記過的不覆寫）</summary>
    private void RememberTaskType(JObject task)
    {
        if (task["task_id"] is JValue { Type: JTokenType.String } id && id.ToString().Length > 0
            && task["task_type"] is JValue { Type: JTokenType.String } type && type.ToString().Length > 0)
            taskTypes[id.ToString()] = type.ToString();
    }

    private void MapTaskTool(string? taskId, string? toolUseId)
    {
        if (string.IsNullOrEmpty(taskId) || string.IsNullOrEmpty(toolUseId) || taskTools.ContainsKey(taskId))
            return;
        if (openCardTools.TryGetValue(toolUseId, out var tool))
            taskTools[taskId] = tool;
    }

    private static void AddPermissionMode(JObject message, ClaudeParseResult result)
    {
        var mode = ClaudeStreamRules.FromClaudePermissionMode(message.Value<string>("permissionMode"));
        if (mode.HasValue)
            result.Events.Add(new AgentPermissionModeChangedEvent(mode.Value));
    }

    private void ParseStreamEvent(JObject? ev, ClaudeParseResult result)
    {
        if (ev == null)
            return;

        switch (ev.Value<string>("type"))
        {
            case "message_start":
                currentMessageId = (ev["message"] as JObject)?["id"]?.ToString() ?? Guid.NewGuid().ToString("N");
                streamedMessageIds.Add(currentMessageId);
                DropAbandonedStreamTools(currentMessageId, result);
                blocks.Clear();
                LastStopReason = null;
                if ((ev["message"] as JObject)?["model"] is JValue { Type: JTokenType.String } startModel)
                {
                    contextModel = (string?)startModel.Value;
                    messageModels[currentMessageId] = contextModel;
                }
                RecordMessageUsage(currentMessageId, (ev["message"] as JObject)?["usage"] as JObject, result);
                break;

            case "message_delta":
                if (ev["delta"] is JObject messageDelta && messageDelta["stop_reason"]?.Type == JTokenType.String)
                    LastStopReason = messageDelta.Value<string>("stop_reason");
                RecordMessageUsage(currentMessageId, ev["usage"] as JObject, result);
                break;

            case "content_block_start":
            {
                int index = ev.Value<int?>("index") ?? -1;
                var block = ev["content_block"] as JObject;
                var kind = block?.Value<string>("type") ?? string.Empty;
                var state = new BlockState { Kind = kind, BlockId = $"{currentMessageId}:{index}" };
                if (kind is "tool_use" or "server_tool_use")
                {
                    state.ToolUseId = block?.Value<string>("id");
                    state.ToolName = block?.Value<string>("name");
                    if (state.ToolUseId != null && state.ToolName != null)
                        toolNames[state.ToolUseId] = state.ToolName;
                    if (kind == "tool_use")
                    {
                        MarkToolRunning(state.ToolUseId);
                        if (state.ToolUseId != null && runningTools.Contains(state.ToolUseId))
                            streamOnlyTools.TryAdd(state.ToolUseId, currentMessageId);
                    }
                }
                else if (kind == "text")
                {
                    var initial = block?.Value<string>("text");
                    if (!string.IsNullOrEmpty(initial))
                    {
                        state.Buffer.Append(initial);
                        result.Events.Add(new AgentTextDeltaEvent(state.BlockId, initial));
                    }
                }

                blocks[index] = state;
                break;
            }

            case "content_block_delta":
            {
                int index = ev.Value<int?>("index") ?? -1;
                if (!blocks.TryGetValue(index, out var state))
                    break;
                var delta = ev["delta"] as JObject;
                switch (delta?.Value<string>("type"))
                {
                    case "text_delta":
                    {
                        var text = delta.Value<string>("text") ?? string.Empty;
                        state.Buffer.Append(text);
                        result.Events.Add(new AgentTextDeltaEvent(state.BlockId, text));
                        break;
                    }
                    case "thinking_delta":
                    {
                        var text = delta.Value<string>("thinking") ?? string.Empty;
                        if (text.Length > 0)
                        {
                            state.Buffer.Append(text);
                            result.Events.Add(new AgentThinkingDeltaEvent(state.BlockId, text));
                        }
                        break;
                    }
                    case "input_json_delta":
                        state.Buffer.Append(delta.Value<string>("partial_json") ?? string.Empty);
                        break;
                }
                break;
            }

            case "content_block_stop":
            {
                int index = ev.Value<int?>("index") ?? -1;
                if (!blocks.TryGetValue(index, out var state))
                    break;
                state.Open = false;
                if (state.Kind == "text")
                {
                    result.Events.Add(new AgentTextCompletedEvent(state.BlockId, state.Buffer.ToString()));
                }
                else if (state.Kind is "tool_use" or "server_tool_use" && state.ToolUseId != null && !startedToolIds.Contains(state.ToolUseId))
                {
                    // 沒收到完整 assistant 區塊時的退路：以累積的 input_json 建立工具卡
                    JObject input;
                    try
                    {
                        input = state.Buffer.Length > 0 ? JObject.Parse(state.Buffer.ToString()) : new JObject();
                    }
                    catch (JsonException)
                    {
                        input = new JObject();
                    }
                    AddToolStarted(state.ToolUseId, state.ToolName, input, result);
                }
                break;
            }
        }
    }

    private void ParseAssistant(JObject? message, ClaudeParseResult result)
    {
        if (message == null)
            return;

        var messageId = message.Value<string>("id") ?? string.Empty;
        bool streamed = messageId.Length > 0 && streamedMessageIds.Contains(messageId);
        if (!streamed && message["stop_reason"]?.Type == JTokenType.String)
            LastStopReason = message.Value<string>("stop_reason");

        foreach (var token in message["content"] as JArray ?? new JArray())
        {
            if (token is not JObject block)
                continue;
            switch (block.Value<string>("type"))
            {
                case "tool_use":
                case "server_tool_use":
                {
                    var id = block.Value<string>("id");
                    var name = block.Value<string>("name");
                    if (id == null)
                        break;
                    if (name != null)
                        toolNames[id] = name;
                    streamOnlyTools.Remove(id);
                    if (block.Value<string>("type") == "tool_use")
                        MarkToolRunning(id);
                    if (!startedToolIds.Contains(id))
                        AddToolStarted(id, name, block["input"] as JObject ?? new JObject(), result);
                    break;
                }
                case "text":
                    if (!streamed)
                    {
                        var text = block.Value<string>("text") ?? string.Empty;
                        if (text.Length > 0)
                            result.Events.Add(new AgentTextCompletedEvent($"{messageId}:s{syntheticBlockCounter++}", text));
                    }
                    break;
            }
        }
    }

    private void AddToolStarted(string toolUseId, string? toolName, JObject input, ClaudeParseResult result)
    {
        startedToolIds.Add(toolUseId);
        if (toolName == ClaudeStreamRules.AskUserQuestionTool || toolName == ClaudeStreamRules.ExitPlanModeTool)
            return;   // 問卷與計畫確認由 can_use_tool 畫成卡片，不另畫工具卡

        if (toolName == ClaudeStreamRules.TodoWriteTool)
        {
            result.Events.Add(new AgentPlanEvent(null, ClaudeStreamRules.ParseTodos(input["todos"] as JArray)));
            return;
        }

        if (toolName is ClaudeStreamRules.TaskCreateTool or ClaudeStreamRules.TaskUpdateTool && TrackTask(toolUseId, toolName, input))
        {
            planToolIds.Add(toolUseId);
            result.Events.Add(new AgentPlanEvent(null, CurrentTaskSteps()));
            return;
        }

        var filePath = input.Value<string>("file_path") ?? input.Value<string>("notebook_path");
        if (!string.IsNullOrWhiteSpace(filePath))
            toolFilePaths[toolUseId] = filePath;

        var d = ClaudeStreamRules.DescribeTool(toolName, input);
        cardToolIds.Add(toolUseId);
        // 指令與子代理可能被放到背景執行：留著卡片的說明（不含差異），task_started 以 tool_use_id 對到它
        if (d.Kind is "bash" or "agent")
            openCardTools[toolUseId] = new AgentBackgroundTool(d.DisplayName, d.Kind, d.Title, d.Detail);
        if (d.Kind == "agent")
            agentCardIds.Add(toolUseId);
        result.Events.Add(new AgentToolStartedEvent(toolUseId, d.DisplayName, d.Kind, d.Title, d.Detail, d.FilePath, d.Diff));
    }

    /// <summary>
    /// 主對話的下一則訊息開始了：之前的訊息裡只在串流開始、一直沒有 assistant 區塊也沒有結果的工具是被丟下的（串流中斷、CLI 重送請求），
    /// 不再算工具在執行；畫了卡的（content_block_stop 的退路）改為已取消。主對話的工具都有結果才會送下一個請求，正常的工具這時早已拿掉
    /// </summary>
    private void DropAbandonedStreamTools(string messageId, ClaudeParseResult result)
    {
        if (streamOnlyTools.Count == 0)
            return;
        foreach (var (id, owner) in streamOnlyTools.ToList())
        {
            if (string.Equals(owner, messageId, StringComparison.Ordinal))
                continue;
            streamOnlyTools.Remove(id);
            if (!runningTools.Remove(id))
                continue;
            finishedTools.Add(id);
            openCardTools.Remove(id);
            if (cardToolIds.Contains(id))
                result.Events.Add(new AgentToolCompletedEvent(id, AgentItemStatus.Cancelled, ClaudeStreamRules.AbandonedToolOutput, Array.Empty<string>()));
        }
    }

    /// <summary>主對話的工具開始執行（content_block_start 或 assistant 的 tool_use）；已經有結果的不再加回</summary>
    private void MarkToolRunning(string? toolUseId)
    {
        if (!string.IsNullOrEmpty(toolUseId) && !finishedTools.Contains(toolUseId))
            runningTools.Add(toolUseId);
    }

    /// <summary>
    /// 照 CLI 2.1.295 自己從串流重建待辦清單的方式累積 TaskCreate／TaskUpdate：建立的先依 tool_use_id 暫存、等結果給編號；
    /// 更新依編號合併，status deleted 就移除。輸入不完整（沒有標題、沒有編號、不認得的狀態）回 false，改畫一般工具卡。
    /// </summary>
    private bool TrackTask(string toolUseId, string toolName, JObject input)
    {
        string? Str(params string[] keys)
            => ClaudeStreamRules.FirstNonBlank(keys.Select(k => input[k]?.Type == JTokenType.String ? input.Value<string>(k) : null).ToArray());

        if (toolName == ClaudeStreamRules.TaskCreateTool)
        {
            var subject = Str("subject", "title", "name");
            if (subject == null)
                return false;
            pendingTaskCreates[toolUseId] = new TaskEntry(subject, Str("activeForm", "active_form") ?? subject, "pending");
            return true;
        }

        var taskId = Str("taskId", "id", "task_id");
        var status = Str("status");
        if (taskId == null || status is not (null or "pending" or "in_progress" or "completed" or "deleted"))
            return false;
        if (status == "deleted")
        {
            tasks.Remove(taskId);
            return true;
        }
        tasks.TryGetValue(taskId, out var existing);
        var newSubject = Str("subject");
        tasks[taskId] = new TaskEntry(
            newSubject ?? existing?.Content ?? $"#{taskId}",
            Str("activeForm", "active_form") ?? existing?.ActiveForm ?? newSubject ?? $"#{taskId}",
            status ?? existing?.Status ?? "pending");
        return true;
    }

    /// <summary>目前的待辦清單：已建立的依建立順序，後面接還在等編號的</summary>
    private List<AgentPlanStep> CurrentTaskSteps()
        => tasks.Values.Concat(pendingTaskCreates.Values)
            .Select(t => ClaudeStreamRules.ToPlanStep(t.Content, t.ActiveForm, t.Status))
            .OfType<AgentPlanStep>()
            .ToList();

    [GeneratedRegex(@"^Task #(\S+) created successfully")]
    private static partial Regex TaskCreatedPattern();

    private void ParseUser(JObject? message, ClaudeParseResult result)
    {
        if (message?["content"] is not JArray content)
            return;

        foreach (var token in content)
        {
            if (token is not JObject block || block.Value<string>("type") != "tool_result")
                continue;
            var toolUseId = block.Value<string>("tool_use_id");
            if (toolUseId == null)
                continue;
            // 安全點：這個工具執行完了（問卷、計畫確認、待辦工具也一樣，先於下面略過它們的卡片）
            result.TurnActivity = true;
            runningTools.Remove(toolUseId);
            streamOnlyTools.Remove(toolUseId);
            finishedTools.Add(toolUseId);
            // 放到背景的工具在結果之前就送了 task_started（已記進 taskTools）；之後不會再有工作對到它
            openCardTools.Remove(toolUseId);
            LastToolResultId = toolUseId;
            toolNames.TryGetValue(toolUseId, out var name);
            if (name is ClaudeStreamRules.AskUserQuestionTool or ClaudeStreamRules.ExitPlanModeTool or ClaudeStreamRules.TodoWriteTool)
                continue;

            bool isError = block.Value<bool?>("is_error") ?? false;
            var output = ClaudeStreamRules.ExtractToolResultText(block["content"]);
            // 子代理做完了：過程放在結論前面（執行中一行一行接上去的輸出會被結果整個換掉）
            agentCardIds.Remove(toolUseId);
            if (subagentLogs.Remove(toolUseId, out var subagentLog))
                output = ClaudeStreamRules.WithSubagentTranscript(subagentLog.Lines, subagentLog.Total, output);
            if (planToolIds.Contains(toolUseId))
            {
                // 待辦清單的工具不回報完成；TaskCreate 的結果給出編號（「Task #3 created successfully」），失敗就從清單拿掉
                if (pendingTaskCreates.TryGetValue(toolUseId, out var created))
                {
                    var match = isError ? null : TaskCreatedPattern().Match(output);
                    if (isError || match is { Success: true })
                    {
                        pendingTaskCreates.Remove(toolUseId);
                        if (match is { Success: true })
                            tasks.TryAdd(match.Groups[1].Value, created);
                        result.Events.Add(new AgentPlanEvent(null, CurrentTaskSteps()));
                    }
                }
                continue;
            }

            var media = new List<string>();
            if (!isError && toolFilePaths.TryGetValue(toolUseId, out var path) && name is "Write" or "Edit" or "MultiEdit" or "Read"
                && AgentAttachmentRules.IsDisplayableMedia(path))
            {
                media.Add(path);
            }

            result.Events.Add(new AgentToolCompletedEvent(toolUseId, isError ? AgentItemStatus.Failed : AgentItemStatus.Done, output, media));
        }
    }

    private static ClaudeControlRequest? ParseControlRequest(JObject message)
    {
        var requestId = message.Value<string>("request_id");
        var request = message["request"] as JObject;
        if (requestId == null || request == null)
            return null;

        return new ClaudeControlRequest(
            requestId,
            request.Value<string>("subtype") ?? string.Empty,
            request.Value<string>("tool_name"),
            request["input"] as JObject ?? new JObject(),
            request["permission_suggestions"] as JArray,
            request.Value<string>("tool_use_id"),
            request.Value<string>("description") ?? request.Value<string>("decision_reason"),
            request);
    }

    private static ClaudeControlResponse? ParseControlResponse(JObject? response)
    {
        var requestId = response?.Value<string>("request_id");
        if (requestId == null)
            return null;
        bool success = response!.Value<string>("subtype") == "success";
        return new ClaudeControlResponse(requestId, success, response["response"] as JObject, response.Value<string>("error"));
    }

    /// <summary>主對話一則訊息的用量更新了（message_start 或 message_delta）：比上次多的部分發成 token 增量</summary>
    private void RecordMessageUsage(string messageId, JObject? usage, ClaudeParseResult result)
    {
        if (usage == null)
            return;
        messageUsage.TryGetValue(messageId, out var before);
        var after = ClaudeStreamRules.MergeMessageUsage(before, usage);
        messageUsage[messageId] = after;
        // 上下文提示：這次呼叫的輸入（含快取）＋輸出（/compact 的 pre_tokens 就是這個數，docs/protocol-notes.md「上下文用量」）
        if (ContextUsageRules.ClaudeCallTokens(after) is { } context && context != lastContextTokens)
        {
            lastContextTokens = context;
            result.Events.Add(new AgentContextUsageEvent(context, null));
        }
        var fields = ClaudeStreamRules.FieldIncrement(before, after);
        messageModels.TryGetValue(messageId, out var model);
        if (CountTokens(model, fields) is { } tokens)
            result.Events.Add(tokens);
    }

    /// <summary>
    /// 子代理逐字稿讀到的一筆增量（ClaudeSubagentUsageTail；四個欄位 input、cache_creation、cache_read、output）：記成這個模型的 token，
    /// 一輪結束時 modelUsage 的差扣掉它（不重複補）。全是 0 回 null
    /// </summary>
    public AgentTokenUsageEvent? AddSubagentUsage(string? model, long[] fields) => CountTokens(model, fields);

    private AgentTokenUsageEvent? CountTokens(string? model, long[] fields)
    {
        if (fields.All(v => v <= 0))
            return null;
        var key = model ?? string.Empty;
        countedSinceResult[key] = countedSinceResult.TryGetValue(key, out var counted)
            ? counted.Zip(fields, (a, b) => a + Math.Max(0, b)).ToArray()
            : fields.Select(v => Math.Max(0, v)).ToArray();
        return ClaudeStreamRules.TokenEvent(model, fields);
    }

    /// <summary>
    /// 一輪的 result：modelUsage（這個程序的累計，依模型）比上一則 result 多、但串流與子代理逐字稿都沒記到的部分，逐模型補發成增量（壓縮那次呼叫、CLI 自己的小模型呼叫…）。
    /// 這個程序的第一則 result 沒有基準（--resume 時 modelUsage 含恢復的舊用量），只記下基準、不補；新的 session 從 0 起算
    /// </summary>
    private void CloseTurnTokens(JObject message, ClaudeParseResult result)
    {
        // 上下文提示的視窗：目前模型那一列的 contextWindow（自動壓縮門檻由會話之後的 get_context_usage 補上）
        if (ContextUsageRules.ClaudeWindow(message["modelUsage"] as JObject, contextModel) is { } window)
            result.Events.Add(new AgentContextUsageEvent(null, window));
        if (ClaudeStreamRules.ModelUsageFields(message["modelUsage"] as JObject) is { } totals)
        {
            if (lastModelUsage != null)
            {
                foreach (var (model, extra) in ClaudeStreamRules.DistributeTopUp(lastModelUsage, totals, countedSinceResult))
                {
                    if (ClaudeStreamRules.TokenEvent(model, extra) is { } tokens)
                        result.Events.Add(tokens);
                }
            }
            lastModelUsage = totals;
        }
        countedSinceResult.Clear();
        messageUsage.Clear();
        messageModels.Clear();
    }

    private static AgentTurnCompletedEvent ParseResult(JObject message)
    {
        bool isError = message.Value<bool?>("is_error") ?? false;
        var subtype = message.Value<string>("subtype") ?? string.Empty;

        AgentUsage? usage = null;
        if (message["usage"] is JObject u)
        {
            long input = (u.Value<long?>("input_tokens") ?? 0) + (u.Value<long?>("cache_creation_input_tokens") ?? 0) + (u.Value<long?>("cache_read_input_tokens") ?? 0);
            usage = new AgentUsage(input, u.Value<long?>("output_tokens") ?? 0, u.Value<long?>("cache_read_input_tokens") ?? 0, message.Value<double?>("total_cost_usd"));
        }

        if (!isError && subtype == "success")
            return new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, usage);

        string? error = null;
        if (message["result"]?.Type == JTokenType.String)
            error = message.Value<string>("result");
        if (string.IsNullOrWhiteSpace(error) && message["errors"] is JArray errors && errors.Count > 0)
            error = string.Join("；", errors.Select(e => e.ToString()));
        if (string.IsNullOrWhiteSpace(error))
            error = subtype;

        return new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, error, usage);
    }
}
