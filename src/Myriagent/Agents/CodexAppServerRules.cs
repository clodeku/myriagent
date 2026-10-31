using System.Text.RegularExpressions;
using Myriagent.Cli;
using Myriagent.Usage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>Codex 一種權限模式對應的核准政策與沙箱</summary>
/// <param name="ApprovalPolicy">untrusted／on-request／never</param>
/// <param name="Sandbox">thread/start 用的 SandboxMode：workspace-write／danger-full-access</param>
/// <param name="FallbackNotice">沙箱不可用而改用退路時給使用者看的說明；正常為 null</param>
internal sealed record CodexPolicy(string ApprovalPolicy, string Sandbox, string? FallbackNotice);

/// <summary>Codex app-server 發來、需要宿主回應的請求（核准、問卷、MCP 表單…）</summary>
internal sealed record CodexServerRequest(JToken Id, string Method, JObject Params)
{
    /// <summary>畫面與回應時用的鍵（JSON-RPC id 轉字串）</summary>
    public string Key => Id.Type == JTokenType.String ? Id.ToString() : Id.ToString(Formatting.None);
}

/// <summary>
/// windowsSandbox/readiness 的狀態。codex 0.160.1：沒設定＝notConfigured；unelevated 或設定完整的 elevated＝ready；
/// 使用者設過 elevated（系統管理員）沙箱但安裝要更新＝updateRequired
/// </summary>
internal enum CodexSandboxReadiness
{
    Ready,

    /// <summary>從未設定：可以用免系統管理員的 unelevated 模式自動設定</summary>
    NotConfigured,

    /// <summary>elevated 沙箱需要更新：不能自動改成 unelevated（設定成功後 codex 會改寫 config.toml，影響這台電腦所有 Codex 前端）</summary>
    UpdateRequired,

    /// <summary>不認得的狀態：不自動設定，先用退路</summary>
    Unknown,
}

/// <summary>thread/resume 失敗的原因</summary>
internal enum CodexResumeFailure
{
    /// <summary>codex 找不到這個 thread 的紀錄（no rollout found）</summary>
    NotFound,

    /// <summary>同一個 CODEX_HOME 的其他程式（VS Code 的 Codex 擴充、Codex 桌面版）正開著這個 thread（already has an active writer）</summary>
    ActiveWriter,

    /// <summary>其他錯誤（逾時、讀取失敗…）</summary>
    Other,
}

/// <summary>RestartAsync 一開始的判斷（熱更新契約 2.5 的流程表第 1、2 列）</summary>
internal enum CodexRestartEntry
{
    /// <summary>只在安全點重開，但現在不在安全點：什麼都不做（回 Deferred，中樞等下一個安全點事件再試）</summary>
    Defer,

    /// <summary>沒有活著的程序、也沒有停下待接續的一輪：什麼都不做（回 Restarted，下次送出自然用新版）</summary>
    NothingToRestart,

    /// <summary>往下做：停下舊程序（有的話），再依設定決定要不要啟動新程序並接續</summary>
    Proceed,
}

/// <summary>停下舊程序之後要做什麼（熱更新契約 2.5 的流程表第 4～6 列）</summary>
internal enum CodexRestartFollowUp
{
    /// <summary>Settings=null，沒有要接續的一輪（這一輪本來就沒在執行、在中斷生效前自己結束了，或使用者按了停止）：回 Stopped</summary>
    Stop,

    /// <summary>Settings=null，這一輪被打斷了：記住「停下待接續」，回 Stopped（中樞之後再帶 Settings 呼叫一次）</summary>
    StopAndSuspend,

    /// <summary>不啟動新程序，回 Restarted：沒有被打斷的一輪、不自動接續、重開途中按了停止或關閉</summary>
    NoContinuation,

    /// <summary>以新設定啟動新程序、thread/resume 後以 turn/start 送接續訊息，回 RestartedAndContinued</summary>
    Continue,
}

/// <summary>MCP 工具呼叫的核准（mcpServer/elicitation/request 裡 _meta.codex_approval_kind 是 mcp_tool_call 的那種）</summary>
/// <param name="CanAllowForSession">_meta.persist 有 session：可以回「本對話都允許」，由 Codex 自己記住</param>
internal sealed record CodexMcpToolApproval(string ServerName, string Title, string? Detail, bool CanAllowForSession);

/// <summary>安全點要追蹤的工具項目開始或結束（CodexAppServerRules.ParseToolLifecycle）</summary>
/// <param name="Key">thread＋項目 id（不同 thread 的項目 id 重複也分得開）</param>
/// <param name="Thread">項目所在的 thread（沒帶是空字串）</param>
/// <param name="ItemId">項目 id（主對話的就是工具卡的 ToolId）</param>
/// <param name="Started">item/started（否則是 item/completed）</param>
/// <param name="Command">指令（commandExecution）：可能讓出給模型、留在終端機裡繼續跑</param>
internal sealed record CodexToolLifecycle(string Key, string Thread, string ItemId, bool Started, bool Command);

/// <summary>一則 app-server 訊息的解析結果</summary>
internal sealed class CodexParseResult
{
    public List<AgentEvent> Events { get; } = new();

    /// <summary>對宿主請求的回應（有 id、沒有 method）</summary>
    public (JToken Id, JObject? Result, string? Error)? Response { get; set; }

    /// <summary>伺服器請求（有 id 也有 method）</summary>
    public CodexServerRequest? ServerRequest { get; set; }

    /// <summary>沒有 id 的通知（method＋params），給帳戶登入等流程用</summary>
    public (string Method, JObject Params)? Notification { get; set; }

    public string? StartedTurnId { get; set; }

    /// <summary>
    /// 重開會打斷的工具項目（指令、改檔、MCP 工具，含子代理的）開始或結束：Key＝thread＋項目 id、Started＝item/started、
    /// Thread＝項目所在的 thread、Command＝指令（commandExecution，可能讓出給模型、留在終端機裡繼續跑）。
    /// 會話據此判斷安全點（CodexAppServerRules.ParseToolLifecycle）
    /// </summary>
    public List<CodexToolLifecycle> ToolLifecycle { get; } = new();

    /// <summary>
    /// 這個 thread 的一次模型呼叫結束了（thread/tokenUsage/updated 的 threadId）：這時還沒結束的指令已讓出給模型
    /// （CodexAppServerRules.ParseCallEnded，docs/protocol-notes.md「Codex 的背景指令」）
    /// </summary>
    public string? CallEndedThread { get; set; }

    public (bool Success, string? Error)? SandboxSetupCompleted { get; set; }

    public AgentTurnCompletedEvent? TurnCompleted { get; set; }
}

/// <summary>
/// Codex app-server（JSON-RPC over stdio，每行一則、沒有 jsonrpc 欄位）的純函式規則：組請求與回應、權限模式對應、核准決策、問卷答案。
/// 協定實測見 docs/protocol-notes.md 與 SampleShop 的 AI代理紀錄：
/// 預設模式要開 default_mode_request_user_input 功能才有提問工具；指令核准的 availableDecisions 實測沒有 decline，只有 accept／acceptWithExecpolicyAmendment／cancel。
/// </summary>
internal static partial class CodexAppServerRules
{
    /// <summary>
    /// app-server 的啟動參數。功能開關一律用「-c features.名稱=…」：
    /// 實測 --enable 遇到這一版不認得的功能名稱會直接結束（exit 1），-c 只會警告並忽略——CLI 會自動更新，不能讓改名的旗標弄壞啟動。
    /// 精簡模式關掉外掛（plugins）、ChatGPT 連接器（apps）與設定檔裡的每個 MCP 伺服器。
    /// <paramref name="sharedMcpOverrides"/> 是共用設定帶過來的 MCP 伺服器（CodexConfigRules.BuildSharedMcpOverrides 的「-c」值），精簡模式不帶。
    /// <paramref name="autoCompactTokens"/> 是設定頁的自動壓縮上限（model_auto_compact_token_limit，AutoCompactRules），精簡模式也帶。
    /// </summary>
    public static List<string> BuildServerArguments(bool useLocalCustomizations, IReadOnlyList<string> mcpServerNames,
        IReadOnlyList<string>? sharedMcpOverrides = null, long? autoCompactTokens = null)
    {
        var args = new List<string>
        {
            "app-server",
            "-c", "features.default_mode_request_user_input=true",
            "-c", "suppress_unstable_features_warning=true",
        };
        if (AutoCompactRules.CodexOverride(autoCompactTokens) is { } autoCompact)
            args.AddRange(new[] { "-c", autoCompact });

        if (!useLocalCustomizations)
        {
            args.AddRange(new[] { "-c", "features.plugins=false", "-c", "features.apps=false" });
            // -c 的鍵只用「.」切開（名稱含點就拆錯），值則照 TOML 解析：所有名稱放進同一個行內表、鍵加引號。
            // 這一層會疊在使用者的設定上（只改 enabled，command 等照舊）；多個 -c mcp_servers=… 只有最後一個有效
            var disabled = (mcpServerNames ?? Array.Empty<string>())
                .Where(name => McpServerName().IsMatch(name))
                .Distinct(StringComparer.Ordinal)
                .Select(name => $"\"{name}\"={{enabled=false}}")
                .ToList();
            if (disabled.Count > 0)
            {
                args.Add("-c");
                args.Add($"mcp_servers={{{string.Join(",", disabled)}}}");
            }
        }
        else
        {
            foreach (var value in sharedMcpOverrides ?? Array.Empty<string>())
            {
                args.Add("-c");
                args.Add(value);
            }
        }

        return args;
    }

    /// <summary>
    /// 權限模式 → 核准政策＋沙箱。Windows 沙箱未就緒（且自動設定失敗）時，需要沙箱的模式改為完全存取但每個指令都先問。
    /// </summary>
    public static CodexPolicy GetPolicy(AgentPermissionMode mode, bool sandboxReady) => AgentPermissionModes.Normalize(AgentProvider.Codex, mode) switch
    {
        AgentPermissionMode.FullAccess => new CodexPolicy("never", "danger-full-access", null),
        AgentPermissionMode.Auto when sandboxReady => new CodexPolicy("on-request", "workspace-write", null),
        _ when sandboxReady => new CodexPolicy("untrusted", "workspace-write", null),
        _ => new CodexPolicy("untrusted", "danger-full-access",
            "這台電腦的 Codex 沙箱尚未設定完成，改為「每個指令都先詢問」以策安全"),
    };

    /// <summary>windowsSandbox/readiness 的 status → 處理方式：只有從未設定（notConfigured）才自動設定</summary>
    public static CodexSandboxReadiness ClassifySandboxReadiness(string? status) => status switch
    {
        "ready" => CodexSandboxReadiness.Ready,
        "notConfigured" => CodexSandboxReadiness.NotConfigured,
        "updateRequired" => CodexSandboxReadiness.UpdateRequired,
        _ => CodexSandboxReadiness.Unknown,
    };

    /// <summary>
    /// error 通知或 turn 的 error（TurnError）是訂閱額度用完：codexErrorInfo 是 usageLimitExceeded（沒有內容的變體序列化成字串；
    /// 也接受 {"usageLimitExceeded": …} 的物件形式）。rateLimitExceeded（API 的速率限制，codex 自己重試）不算。
    /// 名稱依 codex 執行檔裡 app-server v2 的 CodexErrorInfo（docs/protocol-notes.md）
    /// </summary>
    public static bool IsUsageLimitError(JToken? error)
    {
        var info = (error as JObject)?["codexErrorInfo"];
        return info switch
        {
            JValue { Type: JTokenType.String } name => string.Equals((string?)name, "usageLimitExceeded", StringComparison.Ordinal),
            JObject variant => variant.Property("usageLimitExceeded", StringComparison.Ordinal) != null,
            _ => false,
        };
    }

    /// <summary>
    /// thread/goal/updated 的 goal（ThreadGoal）還在進行：status 是 active（paused、blocked、usageLimited、budgetLimited、complete 都不會再自己開下一輪）。
    /// 名稱依 0.164.0 的 app-server schema（ThreadGoalStatus）
    /// </summary>
    public static bool IsGoalActive(JObject? goal)
        => string.Equals(StringOf(goal?["status"]), "active", StringComparison.Ordinal);

    /// <summary>通知或請求屬於主對話以外的 thread（multi_agent 的子代理）；還不知道主對話、或訊息沒帶 threadId（帳戶層級的通知）時算主對話</summary>
    public static bool IsOtherThread(JObject? parameters, string? mainThreadId)
        => mainThreadId != null
            && StringOf(parameters?["threadId"]) is { } thread
            && !string.Equals(thread, mainThreadId, StringComparison.Ordinal);

    /// <summary>turn/start 的 sandboxPolicy（物件形式；workspace-write 開放網路，讓自動化工作能連線）</summary>
    public static JObject BuildSandboxPolicy(string sandbox, IReadOnlyList<string> writableRoots)
    {
        if (sandbox == "workspace-write")
        {
            return new JObject
            {
                ["type"] = "workspaceWrite",
                ["writableRoots"] = new JArray((writableRoots ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r))),
                ["networkAccess"] = true,
                ["excludeTmpdirEnvVar"] = false,
                ["excludeSlashTmp"] = false,
            };
        }

        return sandbox == "read-only"
            ? new JObject { ["type"] = "readOnly", ["networkAccess"] = true }
            : new JObject { ["type"] = "dangerFullAccess" };
    }

    public static string BuildRequest(long id, string method, JObject? parameters)
    {
        var message = new JObject { ["id"] = id, ["method"] = method };
        if (parameters != null)
            message["params"] = parameters;
        return message.ToString(Formatting.None);
    }

    public static string BuildNotification(string method, JObject? parameters = null)
    {
        var message = new JObject { ["method"] = method };
        if (parameters != null)
            message["params"] = parameters;
        return message.ToString(Formatting.None);
    }

    public static string BuildResult(JToken id, JObject result)
        => new JObject { ["id"] = id.DeepClone(), ["result"] = result }.ToString(Formatting.None);

    public static string BuildError(JToken id, int code, string message)
        => new JObject
        {
            ["id"] = id.DeepClone(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
        }.ToString(Formatting.None);

    public static JObject BuildInitializeParams(string version)
        => new()
        {
            ["clientInfo"] = new JObject
            {
                ["name"] = "myriagent",
                ["title"] = "Clode Myriagent",
                ["version"] = version,
            },
            ["capabilities"] = new JObject
            {
                ["experimentalApi"] = true,
                ["requestAttestation"] = false,
            },
        };

    public static JObject BuildThreadStartParams(string cwd, string? model, CodexPolicy policy, string? developerInstructions)
    {
        var p = new JObject
        {
            ["cwd"] = cwd,
            ["approvalPolicy"] = policy.ApprovalPolicy,
            ["sandbox"] = policy.Sandbox,
            ["config"] = new JObject { ["sandbox_workspace_write.network_access"] = true },
        };
        if (!string.IsNullOrWhiteSpace(model))
            p["model"] = model;
        if (!string.IsNullOrWhiteSpace(developerInstructions))
            p["developerInstructions"] = developerInstructions;
        return p;
    }

    /// <summary>thread/resume：新程序續接既有對話（只帶 metadata，不載入整段歷史）</summary>
    public static JObject BuildThreadResumeParams(string threadId, string cwd, string? model, CodexPolicy policy)
    {
        var p = new JObject
        {
            ["threadId"] = threadId,
            ["cwd"] = cwd,
            ["approvalPolicy"] = policy.ApprovalPolicy,
            ["sandbox"] = policy.Sandbox,
            ["excludeTurns"] = true,
        };
        if (!string.IsNullOrWhiteSpace(model))
            p["model"] = model;
        return p;
    }

    /// <summary>
    /// thread/resume 的錯誤 → 原因。實測（0.160.1）同一個 CODEX_HOME 的第二個 app-server 續接已載入的 thread 會回
    /// 「thread {id} already has an active writer」；真的找不到紀錄是「no rollout found for thread id {id}」
    /// </summary>
    public static CodexResumeFailure ClassifyResumeError(string? error)
    {
        if (string.IsNullOrEmpty(error))
            return CodexResumeFailure.Other;
        if (error.Contains("already has an active writer", StringComparison.OrdinalIgnoreCase))
            return CodexResumeFailure.ActiveWriter;
        if (error.Contains("no rollout found", StringComparison.OrdinalIgnoreCase) || error.Contains("thread not found", StringComparison.OrdinalIgnoreCase))
            return CodexResumeFailure.NotFound;
        return CodexResumeFailure.Other;
    }

    // ───── CLI 熱更新（docs/plans/2026-10-10-hot-restart-contract.md 的 WP1.3） ─────

    /// <summary>不是自己剛確認結束的程序時（app 或電腦當機後第一次續接），rollout 要多久沒有寫入才敢截尾：還有其他程式在寫就不動它</summary>
    public static readonly TimeSpan RolloutQuietPeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 目前的程序要不要換掉：帳戶、執行檔、客製設定（CodexAgentSession.Signature）不同，或執行檔身分不是同一份檔案。
    /// 身分以 CliBinaryIdentity.SameOrUnknown 比較：任一方不明（還沒偵測、讀取失敗）不構成重開的理由
    /// </summary>
    public static bool NeedsNewProcess(string? runningSignature, CliBinaryIdentity? runningCli, string nextSignature, CliBinaryIdentity? nextCli)
        => !string.Equals(runningSignature, nextSignature, StringComparison.Ordinal) || !CliBinaryIdentity.SameOrUnknown(runningCli, nextCli);

    /// <summary>
    /// initialize 回應 → 程序回報的 Codex 版本（userAgent「名稱/版本 (…)」斜線後的版本，CliVersionRules.ParseCodexUserAgent）
    /// 與 CODEX_HOME（找 rollout 用；不是本機磁碟的完整路徑就當成不明）
    /// </summary>
    public static (string? Version, string? CodexHome) ParseInitializeResult(JObject? result)
    {
        var home = StringOf(result?["codexHome"]);
        return (CliVersionRules.ParseCodexUserAgent(StringOf(result?["userAgent"])), IsUsableLocalPath(home) ? home : null);
    }

    /// <summary>thread/start、thread/resume 回應的 thread.path（[UNSTABLE] rollout 在磁碟上的位置）；沒有、或不是本機磁碟的完整路徑回 null</summary>
    public static string? ParseThreadPath(JObject? result)
    {
        var path = StringOf((result?["thread"] as JObject)?["path"]);
        return IsUsableLocalPath(path) ? path : null;
    }

    /// <summary>本機磁碟的完整路徑（UNC 一律不認：檢查存在就會對外送 SMB 雜湊）</summary>
    private static bool IsUsableLocalPath(string? path) => AgentAttachmentRules.IsLocalDrivePath(path) && Path.IsPathFullyQualified(path!);

    /// <summary>
    /// item/completed commandExecution 的結束碼與耗時（背景指令跑完時給接續訊息用）：不是這種通知、欄位不是數字時各自是 null
    /// </summary>
    public static (int? ExitCode, TimeSpan? Duration) ParseCommandOutcome(JObject? parameters)
    {
        if (parameters?["item"] is not JObject item || StringOf(item["type"]) != "commandExecution")
            return (null, null);
        int? exitCode = item["exitCode"] is JValue { Type: JTokenType.Integer } code ? (int)(long)code.Value! : null;
        TimeSpan? duration = item["durationMs"] is JValue { Type: JTokenType.Integer } ms && (long)ms.Value! >= 0
            ? TimeSpan.FromMilliseconds((long)ms.Value!)
            : null;
        return (exitCode, duration);
    }

    /// <summary>重開程序會打斷的工具項目種類（設計 7.4.2）：指令、改檔、MCP 工具呼叫</summary>
    public static bool IsSafePointTool(string? itemType) => itemType is "commandExecution" or "fileChange" or "mcpToolCall";

    /// <summary>
    /// item/started／item/completed 是不是安全點要追蹤的工具（IsSafePointTool）：是的話回傳追蹤鍵與是否開始，其他通知、其他種類、沒有 id 回 null。
    /// 子代理（multi_agent）的項目也算——它的指令同樣在這個程序底下執行，重開一樣會打斷；追蹤鍵含 threadId，不同 thread 的項目 id 重複也分得開
    /// </summary>
    public static CodexToolLifecycle? ParseToolLifecycle(string? method, JObject? parameters)
    {
        if (method is not ("item/started" or "item/completed") || parameters?["item"] is not JObject item)
            return null;
        var id = StringOf(item["id"]);
        var type = StringOf(item["type"]);
        if (string.IsNullOrEmpty(id) || !IsSafePointTool(type))
            return null;
        var thread = StringOf(parameters["threadId"]) ?? string.Empty;
        return new CodexToolLifecycle($"{thread}/{id}", thread, id, method == "item/started", type == "commandExecution");
    }

    /// <summary>
    /// thread/tokenUsage/updated＝那個 thread 的一次模型呼叫結束了（含它叫的工具都回給模型之後），回傳 threadId（沒帶是空字串）；其他通知回 null。
    /// 0.164.0 實測（tools/probes/codex_background_probe.py --mode yield）：unified_exec 的指令超過等待時間就把「還在跑」交回模型，
    /// 這一則在那時送出、指令仍是 inProgress，模型接著做別的事（開下一個指令、回覆），指令要等真的結束才 item/completed；
    /// 等到結束的指令則是先 item/completed 才有這一則。所以收到這一則時那個 thread 還沒結束的指令，都已讓出給模型、改在背景跑
    /// </summary>
    public static string? ParseCallEnded(string? method, JObject? parameters)
        => method == "thread/tokenUsage/updated" ? StringOf(parameters?["threadId"]) ?? string.Empty : null;

    /// <summary>
    /// 重開時被中斷的工具卡的說明（與 Claude 的 ClaudeStreamRules.InterruptedToolOutput 一致；工具卡顯示「已停止」，不是「失敗」）
    /// </summary>
    public const string InterruptedToolOutput = "Codex 重新啟動，這個工具被中斷，結果未知";

    /// <summary>背景指令（一輪結束後還在跑的指令）跟著 Codex 程序結束時，工具卡的說明</summary>
    public const string BackgroundInterruptedOutput = "Codex 程序結束，這個背景指令跟著被中斷，結果未知";

    /// <summary>使用者停止的背景指令，工具卡的說明（StopBackgroundWorkAsync）</summary>
    public const string StoppedByUserOutput = "已由使用者強制停止";

    /// <summary>
    /// 使用者停止的背景指令結束了（thread/backgroundTerminals 結束的是 failed、結束碼 -1）：失敗的改成已停止、輸出後面加上說明；
    /// 停止之前剛好自己結束的（完成）照舊
    /// </summary>
    public static AgentToolCompletedEvent MarkStoppedByUser(AgentToolCompletedEvent completed)
    {
        if (completed.Status != AgentItemStatus.Failed)
            return completed;
        var output = string.IsNullOrWhiteSpace(completed.Output) ? StoppedByUserOutput : completed.Output.TrimEnd() + "\n\n" + StoppedByUserOutput;
        return completed with { Status = AgentItemStatus.Cancelled, Output = output };
    }

    /// <summary>thread/backgroundTerminals/list 的回應 {data:[{itemId, processId, …}]} 裡那張工具卡（itemId）的 processId；找不到是 null</summary>
    public static string? FindBackgroundTerminal(JObject? listed, string itemId)
    {
        if (listed?["data"] is not JArray data)
            return null;
        foreach (var terminal in data.OfType<JObject>())
        {
            if (StringOf(terminal["itemId"]) == itemId && StringOf(terminal["processId"]) is { Length: > 0 } processId)
                return processId;
        }
        return null;
    }

    /// <summary>thread/backgroundTerminals/terminate 的回應 {terminated} 明確是 false（那個終端已經不在了）</summary>
    public static bool TerminatedNothing(JObject? response)
        => response?["terminated"] is JValue { Type: JTokenType.Boolean } flag && !flag.Value<bool>();

    /// <summary>
    /// 為了重開停下舊程序的期間收到的工具結束（0.160.1 實測：被結束的指令在 turn/completed(interrupted) 之後才送 item/completed(failed)）：
    /// 失敗的改成已停止，輸出後面加上「重新啟動、被中斷、結果未知」的說明——使用者不會以為指令自己失敗了；其他（完成、拒絕）照舊
    /// </summary>
    public static AgentToolCompletedEvent MarkInterruptedByRestart(AgentToolCompletedEvent completed)
    {
        if (completed.Status != AgentItemStatus.Failed)
            return completed;
        var output = string.IsNullOrWhiteSpace(completed.Output) ? InterruptedToolOutput : completed.Output.TrimEnd() + "\n\n" + InterruptedToolOutput;
        return completed with { Status = AgentItemStatus.Cancelled, Output = output };
    }

    /// <summary>RestartAsync 一開始的判斷：只在安全點重開卻不在安全點＝等；沒有程序也沒有停下待接續的一輪＝不用做；其他往下做</summary>
    public static CodexRestartEntry DecideRestartEntry(AgentRestartMode mode, bool atSafePoint, bool hasLiveProcess, bool hasSuspendedTurn)
        => mode == AgentRestartMode.SafePoint && !atSafePoint ? CodexRestartEntry.Defer
            : !hasLiveProcess && !hasSuspendedTurn ? CodexRestartEntry.NothingToRestart
            : CodexRestartEntry.Proceed;

    /// <summary>停下舊程序之後要做什麼</summary>
    /// <param name="hasSettings">要求帶了新程序的設定（null＝npm 更新屏障的第一段，只停下）</param>
    /// <param name="turnInterrupted">有被打斷、還沒結束的一輪：重開前這一輪在執行、而且沒有在中斷生效前自己結束；或之前停下待接續的</param>
    /// <param name="autoContinue">中樞要不要自動接續（AgentRestartRequest.AutoContinue）</param>
    /// <param name="cancelled">重開途中按了停止、關閉了程序或對話</param>
    public static CodexRestartFollowUp DecideRestartFollowUp(bool hasSettings, bool turnInterrupted, bool autoContinue, bool cancelled)
    {
        if (!hasSettings)
            return turnInterrupted && !cancelled ? CodexRestartFollowUp.StopAndSuspend : CodexRestartFollowUp.Stop;
        return turnInterrupted && autoContinue && !cancelled ? CodexRestartFollowUp.Continue : CodexRestartFollowUp.NoContinuation;
    }

    /// <summary>thread id 可以放進檔名搜尋樣式（Codex 的 thread id 是 UUIDv7：英數字與連字號）；含萬用字元、路徑字元的不搜尋</summary>
    public static bool IsSearchableThreadId(string? threadId)
        => !string.IsNullOrEmpty(threadId) && threadId.Length <= 64 && threadId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>CODEX_HOME\sessions 底下（含子資料夾）找 rollout 的檔名樣式。實測：sessions\yyyy\MM\dd\rollout-yyyy-MM-ddTHH-mm-ss-{thread id}.jsonl</summary>
    public static string RolloutSearchPattern(string threadId) => $"rollout-*-{threadId}.jsonl";

    /// <summary>檔名是不是這個 thread 的 rollout（不分大小寫；搜尋樣式的萬用字元配到的再確認一次）</summary>
    public static bool IsRolloutFileName(string? fileName, string threadId)
        => fileName != null
           && fileName.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase)
           && fileName.EndsWith($"-{threadId}.jsonl", StringComparison.OrdinalIgnoreCase);

    /// <summary>搜尋到的候選裡選這個 thread 的 rollout：檔名相符、最後寫入時間最新的那個；都不符回 null</summary>
    public static string? PickRolloutPath(string threadId, IEnumerable<(string Path, DateTime LastWriteUtc)> candidates)
        => candidates
            .Where(c => IsRolloutFileName(System.IO.Path.GetFileName(c.Path), threadId))
            .OrderByDescending(c => c.LastWriteUtc)
            .Select(c => c.Path)
            .FirstOrDefault();

    /// <summary>不是自己剛確認結束的程序時，rollout 最後一次寫入至少要過了 RolloutQuietPeriod 才截尾（時鐘倒退、寫入時間在未來也不截）</summary>
    public static bool IsQuietForTrim(DateTime lastWriteUtc, DateTime nowUtc) => nowUtc - lastWriteUtc >= RolloutQuietPeriod;

    /// <summary>
    /// rollout 的檔尾寫到一半、最近還有寫入（還不能截尾）時，要等多久才滿 RolloutQuietPeriod（等完再看一次：這段期間沒有新的寫入才截）；
    /// 已經安靜了回 null。寫入時間在未來（時鐘倒退）就等一整段
    /// </summary>
    public static TimeSpan? TrimQuietWait(DateTime lastWriteUtc, DateTime nowUtc)
    {
        if (IsQuietForTrim(lastWriteUtc, nowUtc))
            return null;
        var elapsed = nowUtc - lastWriteUtc;
        return elapsed < TimeSpan.Zero ? RolloutQuietPeriod : RolloutQuietPeriod - elapsed;
    }

    /// <summary>turn/start：一則使用者訊息（文字＋本機圖片），每輪都帶權限、沙箱、模型、強度，切換設定下一輪就生效</summary>
    public static JObject BuildTurnStartParams(
        string threadId,
        string text,
        IReadOnlyList<string> imagePaths,
        CodexPolicy policy,
        IReadOnlyList<string> writableRoots,
        string? model,
        string? effort)
    {
        var p = new JObject
        {
            ["threadId"] = threadId,
            ["input"] = BuildUserInput(text, imagePaths),
            ["approvalPolicy"] = policy.ApprovalPolicy,
            ["sandboxPolicy"] = BuildSandboxPolicy(policy.Sandbox, writableRoots),
        };
        if (!string.IsNullOrWhiteSpace(model))
            p["model"] = model;
        if (!string.IsNullOrWhiteSpace(effort))
            p["effort"] = effort;
        return p;
    }

    /// <summary>
    /// turn/steer：把一則使用者訊息插進進行中的這一輪。expectedTurnId 是這一輪的 id（對不上或這一輪已結束時 codex 回錯誤，不會另開一輪）；
    /// 權限、沙箱、模型沿用這一輪的，不能帶
    /// </summary>
    public static JObject BuildTurnSteerParams(string threadId, string turnId, string text, IReadOnlyList<string> imagePaths)
        => new()
        {
            ["threadId"] = threadId,
            ["expectedTurnId"] = turnId,
            ["input"] = BuildUserInput(text, imagePaths),
        };

    /// <summary>一則使用者訊息的 input：文字＋本機圖片</summary>
    private static JArray BuildUserInput(string text, IReadOnlyList<string>? imagePaths)
    {
        var input = new JArray
        {
            new JObject { ["type"] = "text", ["text"] = text ?? string.Empty, ["text_elements"] = new JArray() },
        };
        foreach (var path in imagePaths ?? Array.Empty<string>())
            input.Add(new JObject { ["type"] = "localImage", ["path"] = path });
        return input;
    }

    /// <summary>
    /// 核准卡選擇 → 回應的 decision。允許＝accept；本對話都允許＝有 acceptForSession 用它，否則 accept（宿主自己記住、之後自動放行）；
    /// 拒絕＝有 decline 用 decline（只拒這一步），否則 cancel（實測指令核准只提供 cancel，會結束本輪）。
    /// acceptWithExecpolicyAmendment 會把規則寫進使用者的 Codex 設定，一律不用。
    /// </summary>
    public static JToken ChooseDecision(JArray? availableDecisions, AgentApprovalChoice choice)
    {
        var available = (availableDecisions ?? new JArray())
            .Where(d => d.Type == JTokenType.String)
            .Select(d => d.ToString())
            .ToHashSet(StringComparer.Ordinal);
        bool unknown = availableDecisions == null || availableDecisions.Count == 0;

        return choice switch
        {
            AgentApprovalChoice.Allow => "accept",
            AgentApprovalChoice.AllowForSession => unknown || available.Contains("acceptForSession") ? "acceptForSession" : "accept",
            _ => unknown || available.Contains("decline") ? "decline" : "cancel",
        };
    }

    /// <summary>舊版（v1）核准請求 execCommandApproval／applyPatchApproval 的 decision</summary>
    public static JToken ChooseLegacyDecision(AgentApprovalChoice choice) => choice switch
    {
        AgentApprovalChoice.Allow => "approved",
        AgentApprovalChoice.AllowForSession => "approved_for_session",
        _ => new JObject { ["denied"] = new JObject { ["rejection"] = "使用者拒絕" } },
    };

    /// <summary>item/permissions/requestApproval 的回應：允許＝照請求的權限給（本輪或本對話）、拒絕＝不給任何權限</summary>
    public static JObject BuildPermissionsResponse(JObject requestParams, AgentApprovalChoice choice)
    {
        if (choice == AgentApprovalChoice.Deny)
            return new JObject { ["permissions"] = new JObject(), ["scope"] = "turn" };

        var granted = new JObject();
        if (requestParams?["permissions"] is JObject requested)
        {
            if (requested["network"] is JObject network)
                granted["network"] = network.DeepClone();
            if (requested["fileSystem"] is JObject fileSystem)
                granted["fileSystem"] = fileSystem.DeepClone();
        }

        return new JObject
        {
            ["permissions"] = granted,
            ["scope"] = choice == AgentApprovalChoice.AllowForSession ? "session" : "turn",
        };
    }

    /// <summary>
    /// 「本對話都允許」記住的完全相同比對鍵；null＝不記（只把 acceptForSession 交給 Codex 自己記）。
    /// 指令：種類（command／writeStdin）＋原始指令（commandActions 只是給畫面看的摘要）＋工作資料夾＋額外權限——
    /// Codex 自己的快取鍵也含額外權限，同一個指令多要權限時會刻意再問，宿主不能拿之前的同意放行。
    /// 網路存取（帶 networkApprovalContext 時指令、工作資料夾都是 null）：主機＋通訊協定。權限請求：請求的權限內容。
    /// </summary>
    public static string? BuildSessionApprovalKey(string method, JObject p)
    {
        const char Separator = (char)1;
        switch (method)
        {
            case "item/commandExecution/requestApproval":
            {
                if (p["networkApprovalContext"] is JObject network)
                {
                    var host = StringOf(network["host"]);
                    return string.IsNullOrWhiteSpace(host) ? null : string.Join(Separator, "network", host, StringOf(network["protocol"]) ?? string.Empty);
                }
                var command = StringOf(p["command"]);
                return string.IsNullOrWhiteSpace(command)
                    ? null
                    : string.Join(Separator, "command", StringOf(p["kind"]) ?? "command", command, StringOf(p["cwd"]) ?? string.Empty, Canonical(p["additionalPermissions"]));
            }
            case "execCommandApproval":
                return p["command"] is JArray { Count: > 0 } argv
                    ? string.Join(Separator, "command", "command", argv.ToString(Formatting.None), StringOf(p["cwd"]) ?? string.Empty, string.Empty)
                    : null;
            case "item/permissions/requestApproval":
                return p["permissions"] is JObject permissions ? string.Join(Separator, "permissions", permissions.ToString(Formatting.None)) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// mcpServer/elicitation/request 是不是 MCP 工具呼叫的核准。codex 0.160.1 預設開啟 tool_call_mcp_elicitation：
    /// 需要核准的 MCP 工具（沒有 readOnlyHint 的都算）不再走 requestUserInput，改送這個請求——表單是空的，_meta.codex_approval_kind＝mcp_tool_call。
    /// 不是核准（真正要填欄位的表單、開網址、驗證身分）回 null。
    /// </summary>
    public static CodexMcpToolApproval? ParseMcpToolApproval(JObject p)
    {
        if (p?["_meta"] is not JObject meta || StringOf(meta["codex_approval_kind"]) != "mcp_tool_call")
            return null;

        var message = StringOf(p["message"]);
        var toolTitle = StringOf(meta["tool_title"]);
        var title = !string.IsNullOrWhiteSpace(toolTitle) ? toolTitle
            : !string.IsNullOrWhiteSpace(message) ? ClaudeStreamRules.FirstLine(message)
            : "MCP 工具";
        var parameters = meta["tool_params"] is { Type: not JTokenType.Null } toolParams
            ? ClaudeStreamRules.Truncate(toolParams.ToString(Formatting.Indented), 4000)
            : null;
        var detail = string.Join("\n\n", new[] { title == message ? null : message, StringOf(meta["tool_description"]), parameters }
            .Where(part => !string.IsNullOrWhiteSpace(part)));
        // persist 可能是 "session"、"always" 或兩者的陣列；只用 session（always 會寫進使用者的 Codex 設定）
        var persist = meta["persist"];
        bool forSession = StringOf(persist) == "session" || (persist is JArray options && options.Any(o => StringOf(o) == "session"));
        return new CodexMcpToolApproval(StringOf(p["serverName"]) ?? "MCP", title, detail.Length > 0 ? detail : null, forSession);
    }

    /// <summary>
    /// MCP 工具核准的回應：允許＝accept；本對話都允許＝accept 加 _meta.persist＝session（Codex 自己記住）；
    /// 拒絕＝decline（Codex 告訴模型使用者拒絕了這次呼叫，這一輪繼續；cancel 會中止整個請求）
    /// </summary>
    public static JObject BuildMcpElicitationResponse(AgentApprovalChoice choice) => new()
    {
        ["action"] = choice == AgentApprovalChoice.Deny ? "decline" : "accept",
        ["content"] = JValue.CreateNull(),
        ["_meta"] = choice == AgentApprovalChoice.AllowForSession ? new JObject { ["persist"] = "session" } : JValue.CreateNull(),
    };

    /// <summary>宿主不支援的 MCP 請求（真正要填欄位的表單、開網址、驗證身分）：婉拒時給使用者看的說明</summary>
    public static string DescribeUnsupportedElicitation(JObject p)
    {
        var kind = StringOf(p?["mode"]) switch
        {
            "form" or "openai/form" or "openaiForm" => "要求填寫表單",
            "url" => "要求開啟網址",
            "openai/userVerification" => "要求驗證身分",
            _ => "送來不支援的請求",
        };
        var message = StringOf(p?["message"]) ?? StringOf(p?["title"]);
        var context = string.IsNullOrWhiteSpace(message) ? string.Empty : $"（{ClaudeStreamRules.FirstLine(message)}）";
        return $"MCP 伺服器「{StringOf(p?["serverName"]) ?? "MCP"}」{kind}{context}，目前不支援，已婉拒";
    }

    /// <summary>JSON 字串值；不是字串（null、物件、數字）回 null</summary>
    internal static string? StringOf(JToken? token) => token is JValue { Type: JTokenType.String } value ? (string?)value : null;

    /// <summary>
    /// thread/tokenUsage/updated 的一份用量（total 或 last）：inputTokens 含 cachedInputTokens、outputTokens 含 reasoningOutputTokens（totalTokens＝兩者相加）。
    /// 輸入與輸出都沒帶回 null
    /// </summary>
    public static AgentUsage? ParseTokenBreakdown(JObject? breakdown)
    {
        if (breakdown == null || (breakdown["inputTokens"] == null && breakdown["outputTokens"] == null))
            return null;
        return new AgentUsage(TokenCount(breakdown["inputTokens"]), TokenCount(breakdown["outputTokens"]), TokenCount(breakdown["cachedInputTokens"]), null);
    }

    private static long TokenCount(JToken? token) => token is JValue { Type: JTokenType.Integer } v && Convert.ToInt64(v.Value, System.Globalization.CultureInfo.InvariantCulture) is var n && n > 0 ? n : 0;

    /// <summary>比對用的 JSON 原文；沒有或 JSON null 回空字串</summary>
    private static string Canonical(JToken? token) => token == null || token.Type == JTokenType.Null ? string.Empty : token.ToString(Formatting.None);

    /// <summary>item/tool/requestUserInput 的回應：題目 id → { answers: [...] }</summary>
    public static JObject BuildUserInputResponse(IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var result = new JObject();
        foreach (var (questionId, values) in answers ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            var cleaned = (values ?? Array.Empty<string>())
                .Select(v => v?.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => (JToken)v!)
                .ToList();
            result[questionId] = new JObject { ["answers"] = new JArray(cleaned) };
        }

        return new JObject { ["answers"] = result };
    }

    public static List<AgentQuestion> ParseUserInputQuestions(JObject parameters)
    {
        var result = new List<AgentQuestion>();
        foreach (var token in parameters?["questions"] as JArray ?? new JArray())
        {
            if (token is not JObject q)
                continue;
            var id = q.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id))
                continue;
            var question = new AgentQuestion
            {
                Id = id,
                Header = q.Value<string>("header"),
                Text = q.Value<string>("question") ?? string.Empty,
                AllowOther = q.Value<bool?>("isOther") ?? false,
                IsSecret = q.Value<bool?>("isSecret") ?? false,
            };
            foreach (var o in q["options"] as JArray ?? new JArray())
            {
                if (o is not JObject option)
                    continue;
                var label = option["label"]?.ToString();
                if (string.IsNullOrWhiteSpace(label))
                    continue;
                question.Options.Add(new AgentQuestionOption { Label = label, Description = option["description"]?.ToString() });
            }

            if (question.Options.Count == 0)
                question.AllowOther = true;
            result.Add(question);
        }

        return result;
    }

    /// <summary>agentMessage.questions（非同步提問，不擋住本輪）→ 問卷；作答改以下一則訊息送出</summary>
    public static List<AgentQuestion> ParseAsyncQuestions(JArray? questions, string itemId)
    {
        var result = new List<AgentQuestion>();
        int index = 0;
        foreach (var token in questions ?? new JArray())
        {
            var title = (token as JObject)?["title"]?.ToString();
            if (string.IsNullOrWhiteSpace(title))
                continue;
            var question = new AgentQuestion { Id = $"{itemId}:{index++}", Text = title, AllowOther = true };
            foreach (var o in token!["options"] as JArray ?? new JArray())
            {
                var label = o?.ToString();
                if (!string.IsNullOrWhiteSpace(label))
                    question.Options.Add(new AgentQuestionOption { Label = label });
            }
            result.Add(question);
        }

        return result;
    }

    /// <summary>Codex 在 Windows 把指令包成 pwsh -Command '…'；commandActions 有原始指令就用它，否則剝掉外層包裝</summary>
    public static string DescribeCommand(JObject item)
    {
        var actions = item?["commandActions"] as JArray;
        var commands = (actions ?? new JArray())
            .OfType<JObject>()
            .Select(a => a["command"]?.ToString())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();
        if (commands.Count > 0)
            return string.Join(" && ", commands!);

        var command = item?["command"]?.ToString() ?? string.Empty;
        const string marker = "-Command '";
        int at = command.IndexOf(marker, StringComparison.Ordinal);
        if (at >= 0 && command.EndsWith('\''))
            return command.Substring(at + marker.Length, command.Length - at - marker.Length - 1).Replace("''", "'");
        return command;
    }

    /// <summary>model/list 的結果 → 模型選單（略過隱藏的模型；預設模型排最前）</summary>
    public static List<AgentModelOption> ParseModels(JObject? modelListResult)
    {
        var models = new List<(AgentModelOption Option, bool IsDefault)>();
        foreach (var token in modelListResult?["data"] as JArray ?? new JArray())
        {
            if (token is not JObject model || (model.Value<bool?>("hidden") ?? false))
                continue;
            var id = model.Value<string>("model") ?? model.Value<string>("id");
            if (string.IsNullOrWhiteSpace(id))
                continue;
            var efforts = (model["supportedReasoningEfforts"] as JArray ?? new JArray())
                .Select(e => e?["reasoningEffort"]?.ToString())
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Select(e => e!)
                .ToList();
            models.Add((new AgentModelOption(id, model.Value<string>("displayName") ?? id, model.Value<string>("description"), efforts),
                model.Value<bool?>("isDefault") ?? false));
        }

        return models.OrderByDescending(m => m.IsDefault).Select(m => m.Option).ToList();
    }

    /// <summary>Codex 接受的 MCP 伺服器名稱（codex-mcp 的 validate_mcp_server_name）；這些字元放進 TOML 基本字串也不必跳脫</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_:@/.-]+\z")]
    private static partial Regex McpServerName();
}

/// <summary>
/// 解析 codex app-server 的輸出（一個程序一個實例，只由讀取 stdout 的執行緒呼叫）。
/// 工具類項目（指令、改檔、MCP、搜尋、生圖）在 item/started 建立工具卡、item/completed 收尾；
/// 回覆文字以 item/agentMessage/delta 串流、item/completed 給完整內容。
/// </summary>
internal sealed partial class CodexEventParser
{
    private readonly Dictionary<string, JObject> fileChangeItems = new();
    private AgentUsage? lastTurnUsage;

    /// <summary>各 thread（主對話與子代理）上一則 thread/tokenUsage/updated 的 total：token 計數取 total 的差</summary>
    private readonly Dictionary<string, AgentUsage> threadTokenTotals = new(StringComparer.Ordinal);

    /// <summary>子代理 thread 的模型（主對話或上層子代理的 subAgentActivity 項目帶 agentThreadId 與 model）：token 增量記在這個模型</summary>
    private readonly Dictionary<string, string> threadModels = new(StringComparer.Ordinal);

    /// <summary>
    /// 主對話目前的模型（會話設定：thread/start、thread/resume 回應的 model，turn/start 指定了就換成指定的）：token 增量記在這個模型；
    /// 子代理不知道模型時也用它。任何執行緒都可以設（只換參考）
    /// </summary>
    public string? MainModel { get; set; }

    /// <summary>主對話進行中的這一輪（turn/started 起到 turn/completed）</summary>
    private string? currentTurnId;

    /// <summary>主對話這一輪記過的 token 增量（這一輪的用量；last 只是最後一次 API 呼叫）</summary>
    private AgentUsage? turnTokens;

    /// <summary>這一輪已經提示過連線重試（每重試一次就有一則 error 通知，只提示第一次）</summary>
    private bool retryNoticed;

    /// <summary>這一輪 willRetry:false 的錯誤：隨這一輪的結束回報一次（turn/completed 通常帶同一段文字）</summary>
    private string? turnError;

    /// <summary>這一輪 willRetry:false 的錯誤是額度用完（codexErrorInfo usageLimitExceeded）：隨這一輪的結束發 AgentQuotaExhaustedEvent</summary>
    private bool turnUsageLimit;

    /// <summary>
    /// 主對話的持續目標（thread goal：模型呼叫 create_goal 或用戶端 thread/goal/set）還在進行（最近一則 thread/goal/updated 的 status 是 active；
    /// cleared 或其他狀態為 false）：codex 每輪結束後會自己接著開下一輪（docs/protocol-notes.md「Codex 對話：持續目標」）
    /// </summary>
    public bool GoalActive { get; private set; }

    /// <summary>改檔項目的差異（核准卡顯示用；核准請求本身不帶差異）</summary>
    public (string? Path, IReadOnlyList<AgentDiffLine>? Diff) GetFileChange(string itemId)
    {
        if (!fileChangeItems.TryGetValue(itemId, out var item))
            return (null, null);
        var (_, path, diff) = DescribeFileChanges(item);
        return (path, diff);
    }

    /// <param name="mainThreadId">主對話的 thread id；給了才分得出子代理（multi_agent）的通知，null＝全部當成主對話</param>
    public CodexParseResult Parse(string line, string? mainThreadId = null)
    {
        var result = new CodexParseResult();
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

        var method = message.Value<string>("method");
        var id = message["id"];
        if (id != null && method == null)
        {
            var error = message["error"];
            result.Response = (id, message["result"] as JObject, error switch
            {
                null => null,
                JObject e => e.Value<string>("message") ?? e.ToString(Formatting.None),
                _ when error.Type == JTokenType.Null => null,
                _ => error.ToString(),
            });
            return result;
        }

        var parameters = message["params"] as JObject ?? new JObject();
        if (id != null && method != null)
        {
            result.ServerRequest = new CodexServerRequest(id, method, parameters);
            return result;
        }

        if (method != null)
            result.Notification = (method, parameters);

        // 安全點：主對話與子代理的工具項目都算（重開一樣會打斷子代理正在跑的指令）
        if (CodexAppServerRules.ParseToolLifecycle(method, parameters) is { } tool)
            result.ToolLifecycle.Add(tool);
        result.CallEndedThread = CodexAppServerRules.ParseCallEnded(method, parameters);

        if (CodexAppServerRules.IsOtherThread(parameters, mainThreadId))
        {
            ParseOtherThread(method, parameters, result);
            return result;
        }

        switch (method)
        {
            case "turn/started":
                result.StartedTurnId = (parameters["turn"] as JObject)?["id"]?.ToString();
                currentTurnId = result.StartedTurnId;
                turnTokens = null;
                retryNoticed = false;
                turnError = null;
                turnUsageLimit = false;
                break;
            case "item/started":
                OnItemStarted(parameters["item"] as JObject, result);
                break;
            case "item/completed":
                OnItemCompleted(parameters["item"] as JObject, result);
                break;
            case "item/agentMessage/delta":
                AddDelta(parameters, result, (block, delta) => new AgentTextDeltaEvent(block, delta));
                break;
            case "item/reasoning/summaryTextDelta":
            case "item/reasoning/textDelta":
                AddDelta(parameters, result, (block, delta) => new AgentThinkingDeltaEvent(block, delta));
                break;
            case "item/reasoning/summaryPartAdded":
                // 推理摘要的新段落（文字片段本身不含分隔）：第一段之後每段前空一行，Markdown 才會分段
                if (parameters["summaryIndex"] is JValue { Type: JTokenType.Integer } index && (long)index > 0
                    && parameters.Value<string>("itemId") is { Length: > 0 } reasoningItem)
                {
                    result.Events.Add(new AgentThinkingDeltaEvent(reasoningItem, "\n\n"));
                }
                break;
            case "item/commandExecution/outputDelta":
            case "item/fileChange/outputDelta":
                AddDelta(parameters, result, (block, delta) => new AgentToolOutputEvent(block, delta));
                break;
            case "turn/plan/updated":
            {
                var steps = (parameters["plan"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Select(s => new AgentPlanStep { Text = s["step"]?.ToString() ?? string.Empty, Status = s["status"]?.ToString() ?? "pending" })
                    .Where(s => s.Text.Length > 0)
                    .ToList();
                result.Events.Add(new AgentPlanEvent(parameters.Value<string>("explanation"), steps));
                break;
            }
            case "turn/completed":
            {
                // 這一輪的用量：記過的增量（每次 API 呼叫的合計）；沒記到（例如沒看到 turn/started）或比最後一次呼叫還少時用 last
                var turnUsage = turnTokens != null && TokenTallyRules.Total(turnTokens) >= TokenTallyRules.Total(lastTurnUsage) ? turnTokens : lastTurnUsage;
                var completed = ParseTurnCompleted(parameters["turn"] as JObject, turnUsage);
                if (turnError != null)
                {
                    if (completed.Outcome != AgentTurnOutcome.Failed)
                        result.Events.Add(new AgentNoticeEvent(turnError, IsError: true));   // 不是以失敗結束：錯誤不能就此消失
                    else if (string.IsNullOrWhiteSpace(completed.ErrorMessage))
                        completed = completed with { ErrorMessage = turnError };
                }
                // 額度用完（error 通知或 turn 的 error 帶 usageLimitExceeded）：在 TurnCompleted 之前發一次，中樞據此決定要不要換帳戶繼續
                if (completed.Outcome != AgentTurnOutcome.Interrupted
                    && (turnUsageLimit || CodexAppServerRules.IsUsageLimitError((parameters["turn"] as JObject)?["error"])))
                    result.Events.Add(new AgentQuotaExhaustedEvent(AgentProvider.Codex, null));
                // 持續目標還在進行：codex 接著自己開下一輪（模型標記完成時 thread/goal/updated 比 turn/completed 先到）
                if (completed.Outcome == AgentTurnOutcome.Completed && GoalActive)
                    completed = completed with { ContinuesOnItsOwn = true };
                result.TurnCompleted = completed;
                lastTurnUsage = null;
                turnTokens = null;
                currentTurnId = null;
                retryNoticed = false;
                turnError = null;
                turnUsageLimit = false;
                break;
            }
            case "thread/tokenUsage/updated":
                if ((parameters["tokenUsage"] as JObject)?["last"] is JObject last)
                {
                    lastTurnUsage = new AgentUsage(
                        last.Value<long?>("inputTokens") ?? 0,
                        last.Value<long?>("outputTokens") ?? 0,
                        last.Value<long?>("cachedInputTokens") ?? 0,
                        null);
                }
                OnTokenUsage(parameters, mainThread: true, result);
                break;
            case "account/rateLimits/updated":
                if (parameters["rateLimits"] is JObject rateLimits)
                    result.Events.Add(new AgentRateLimitEvent(AgentProvider.Codex, rateLimits));
                break;
            case "error":
            {
                var error = (parameters["error"] as JObject)?["message"]?.ToString() ?? "Codex 發生錯誤";
                bool willRetry = parameters.Value<bool?>("willRetry") ?? false;
                if (!willRetry)
                {
                    // 最後的錯誤：codex 接著送 turn/completed（failed，error 是同一段文字），由這一輪的結束回報，不另加一列
                    turnError = error;
                    if (CodexAppServerRules.IsUsageLimitError(parameters["error"]))
                        turnUsageLimit = true;
                }
                else if (!retryNoticed)
                {
                    // 串流斷線重試：每重試一次就有一則（實測一輪 9 則），同一輪只提示第一次
                    retryNoticed = true;
                    result.Events.Add(new AgentNoticeEvent($"Codex 連線不穩，正在重試：{error}", IsError: false));
                }
                break;
            }
            case "serverRequest/resolved":
                AddRequestResolved(parameters, result);
                break;
            case "windowsSandbox/setupCompleted":
                result.SandboxSetupCompleted = (parameters.Value<bool?>("success") ?? false, parameters.Value<string>("error"));
                break;
            case "thread/compacted":
                result.Events.Add(new AgentNoticeEvent("對話已很長，Codex 自動壓縮了較早的內容", IsError: false));
                break;
            case "thread/goal/updated":
                GoalActive = CodexAppServerRules.IsGoalActive(parameters["goal"] as JObject);
                result.Events.Add(new AgentCodexGoalEvent(parameters["goal"] as JObject));
                break;
            case "thread/goal/cleared":
                GoalActive = false;
                result.Events.Add(new AgentCodexGoalEvent(null));
                break;
        }

        return result;
    }

    private static void AddDelta(JObject parameters, CodexParseResult result, Func<string, string, AgentEvent> create)
    {
        var itemId = parameters.Value<string>("itemId");
        var delta = parameters.Value<string>("delta");
        if (!string.IsNullOrEmpty(itemId) && !string.IsNullOrEmpty(delta))
            result.Events.Add(create(itemId, delta));
    }

    /// <summary>
    /// 子代理（multi_agent）的 thread：app-server 會替連線自動訂閱子代理的 thread，turn／item／用量／錯誤通知都帶子代理的 threadId。
    /// 這一輪的狀態（turn id、完成、用量、錯誤）只看主對話，子代理的內容也不顯示（主對話有「子代理」工具卡）；
    /// 只記下改檔項目（子代理的改檔核准卡要顯示差異），撤銷請求照常處理（子代理的核准卡也要能收掉）
    /// </summary>
    private void ParseOtherThread(string? method, JObject parameters, CodexParseResult result)
    {
        ChildEvent(method, parameters, result);
        switch (method)
        {
            case "item/started":
            case "item/completed":
                if (parameters["item"] is JObject item && item.Value<string>("type") == "fileChange" && item.Value<string>("id") is { } itemId)
                    fileChangeItems[itemId] = item;
                // 子代理再派的子代理：它的模型
                RememberSubAgentModel(parameters["item"] as JObject);
                break;
            case "serverRequest/resolved":
                AddRequestResolved(parameters, result);
                break;
            case "thread/tokenUsage/updated":
                // 子代理用的也是這個帳戶的額度：照樣記進 token 計數（不算進主對話這一輪的用量）
                OnTokenUsage(parameters, mainThread: false, result);
                break;
        }
    }

    /// <summary>
    /// thread/tokenUsage/updated → token 增量（TokenTallyRules.CodexIncrement）。主對話第一次看到時，只有屬於這一輪的通知才記 last：
    /// thread/resume 一接上就先送一則恢復的累計（實測帶的是上一輪的 turnId），只能當基準；子代理的 thread 都是這一輪開的
    /// </summary>
    private void OnTokenUsage(JObject parameters, bool mainThread, CodexParseResult result)
    {
        var tokenUsage = parameters["tokenUsage"] as JObject;
        var total = CodexAppServerRules.ParseTokenBreakdown(tokenUsage?["total"] as JObject);
        var last = CodexAppServerRules.ParseTokenBreakdown(tokenUsage?["last"] as JObject);
        var thread = CodexAppServerRules.StringOf(parameters["threadId"]) ?? string.Empty;
        var turnId = CodexAppServerRules.StringOf(parameters["turnId"]);
        bool countFirst = !mainThread || (currentTurnId != null && (turnId == null || turnId == currentTurnId));
        // 上下文提示：主對話最近一次呼叫的大小與視窗（續接時恢復的那一則也算：last 是上一輪最後一次呼叫；壓縮後的那一則只有 totalTokens）
        if (mainThread)
        {
            var (contextUsed, contextWindow) = ContextUsageRules.ParseCodexTokenUsage(tokenUsage);
            if (contextUsed != null || contextWindow != null)
                result.Events.Add(new AgentContextUsageEvent(contextUsed, contextWindow));
        }
        threadTokenTotals.TryGetValue(thread, out var previous);
        var delta = TokenTallyRules.CodexIncrement(previous, total, last, countFirst);
        if (total != null)
            threadTokenTotals[thread] = total;
        if (TokenTallyRules.IsZero(delta))
            return;
        if (mainThread)
            turnTokens = TokenTallyRules.Add(turnTokens, delta);
        var model = mainThread ? MainModel : threadModels.TryGetValue(thread, out var subModel) ? subModel : MainModel;
        result.Events.Add(new AgentTokenUsageEvent(delta, model));
    }

    /// <summary>subAgentActivity 項目（started 帶 model 與 agentThreadId）：記下那個子代理 thread 的模型</summary>
    private void RememberSubAgentModel(JObject? item)
    {
        if (item?.Value<string>("type") != "subAgentActivity")
            return;
        if (CodexAppServerRules.StringOf(item["agentThreadId"]) is { Length: > 0 } agentThread
            && CodexAppServerRules.StringOf(item["model"]) is { Length: > 0 } model)
        {
            threadModels[agentThread] = model;
        }
    }

    private static void AddRequestResolved(JObject parameters, CodexParseResult result)
    {
        var requestId = parameters["requestId"];
        if (requestId != null)
            result.Events.Add(new AgentRequestResolvedEvent(requestId.Type == JTokenType.String ? requestId.ToString() : requestId.ToString(Formatting.None)));
    }

    private void OnItemStarted(JObject? item, CodexParseResult result)
    {
        RememberSubAgentModel(item);
        SubagentActivity(item, result);
        var itemId = item?.Value<string>("id");
        if (item == null || itemId == null)
            return;

        switch (item.Value<string>("type"))
        {
            case "commandExecution":
            {
                var command = CodexAppServerRules.DescribeCommand(item);
                result.Events.Add(new AgentToolStartedEvent(itemId, "執行指令", "bash", ClaudeStreamRules.FirstLine(command), command));
                break;
            }
            case "fileChange":
            {
                fileChangeItems[itemId] = item;
                var (title, path, diff) = DescribeFileChanges(item);
                result.Events.Add(new AgentToolStartedEvent(itemId, "修改檔案", "edit", title, null, path, diff));
                break;
            }
            case "mcpToolCall":
                result.Events.Add(new AgentToolStartedEvent(itemId, $"MCP：{item.Value<string>("server")}", "mcp", item.Value<string>("tool") ?? string.Empty,
                    ClaudeStreamRules.Truncate(item["arguments"]?.ToString(Formatting.Indented), 4000)));
                break;
            case "dynamicToolCall":
                result.Events.Add(new AgentToolStartedEvent(itemId, "工具", "other", item.Value<string>("tool") ?? string.Empty,
                    ClaudeStreamRules.Truncate(item["arguments"]?.ToString(Formatting.Indented), 4000)));
                break;
            case "webSearch":
                result.Events.Add(new AgentToolStartedEvent(itemId, "網路搜尋", "web", item.Value<string>("query") ?? string.Empty, null));
                break;
            case "imageGeneration":
                result.Events.Add(new AgentToolStartedEvent(itemId, "生成圖片", "image", "生成中…", item.Value<string>("revisedPrompt")));
                break;
            case "imageView":
            {
                var path = item.Value<string>("path");
                result.Events.Add(new AgentToolStartedEvent(itemId, "檢視圖片", "image", path ?? string.Empty, null, path));
                break;
            }
            case "collabAgentToolCall":
                result.Events.Add(new AgentToolStartedEvent(itemId, "子代理", "agent", item.Value<string>("tool") ?? "子代理",
                    ClaudeStreamRules.Truncate(item.Value<string>("prompt"), 4000)));
                break;
        }
    }

    private void OnItemCompleted(JObject? item, CodexParseResult result)
    {
        var itemId = item?.Value<string>("id");
        if (item == null || itemId == null)
            return;

        switch (item.Value<string>("type"))
        {
            case "agentMessage":
            {
                result.Events.Add(new AgentTextCompletedEvent(itemId, item.Value<string>("text") ?? string.Empty));
                var questions = CodexAppServerRules.ParseAsyncQuestions(item["questions"] as JArray, itemId);
                if (questions.Count > 0)
                    result.Events.Add(new AgentQuestionEvent($"async:{itemId}", questions, AnswerAsMessage: true));
                break;
            }
            case "commandExecution":
            {
                var status = item.Value<string>("status");
                int? exitCode = item.Value<int?>("exitCode");
                var itemStatus = status switch
                {
                    "declined" => AgentItemStatus.Declined,
                    "failed" => AgentItemStatus.Failed,
                    _ when exitCode.HasValue && exitCode.Value != 0 => AgentItemStatus.Failed,
                    _ => AgentItemStatus.Done,
                };
                result.Events.Add(new AgentToolCompletedEvent(itemId, itemStatus, item.Value<string>("aggregatedOutput"), Array.Empty<string>()));
                break;
            }
            case "fileChange":
            {
                fileChangeItems[itemId] = item;
                var status = MapStatus(item.Value<string>("status"));
                var media = status == AgentItemStatus.Done
                    ? (item["changes"] as JArray ?? new JArray())
                        .Where(c => ((c as JObject)?["kind"] as JObject)?["type"]?.ToString() != "delete")
                        .Select(c => (c as JObject)?["path"]?.ToString())
                        .Where(p => !string.IsNullOrWhiteSpace(p) && AgentAttachmentRules.IsDisplayableMedia(p!))
                        .Select(p => p!)
                        .ToList()
                    : new List<string>();
                result.Events.Add(new AgentToolCompletedEvent(itemId, status, null, media));
                break;
            }
            case "mcpToolCall":
            {
                var error = (item["error"] as JObject)?["message"]?.ToString();
                var output = error ?? DescribeMcpResult(item["result"] as JObject);
                result.Events.Add(new AgentToolCompletedEvent(itemId, error != null ? AgentItemStatus.Failed : MapStatus(item.Value<string>("status")), output, Array.Empty<string>()));
                break;
            }
            case "dynamicToolCall":
                result.Events.Add(new AgentToolCompletedEvent(itemId, (item.Value<bool?>("success") ?? true) ? AgentItemStatus.Done : AgentItemStatus.Failed, null, Array.Empty<string>()));
                break;
            case "webSearch":
                result.Events.Add(new AgentToolCompletedEvent(itemId, AgentItemStatus.Done, null, Array.Empty<string>()));
                break;
            case "imageGeneration":
            {
                var savedPath = item.Value<string>("savedPath");
                bool failed = item["failure"] is JObject;
                var output = failed ? "生圖額度已用完" : item.Value<string>("revisedPrompt");
                var media = !failed && !string.IsNullOrWhiteSpace(savedPath) ? new[] { savedPath! } : Array.Empty<string>();
                result.Events.Add(new AgentToolCompletedEvent(itemId, failed ? AgentItemStatus.Failed : AgentItemStatus.Done, output, media));
                break;
            }
            case "imageView":
            {
                var path = item.Value<string>("path");
                var media = !string.IsNullOrWhiteSpace(path) && AgentAttachmentRules.IsDisplayableMedia(path) ? new[] { path! } : Array.Empty<string>();
                result.Events.Add(new AgentToolCompletedEvent(itemId, AgentItemStatus.Done, null, media));
                break;
            }
            case "collabAgentToolCall":
                result.Events.Add(new AgentToolCompletedEvent(itemId, AgentItemStatus.Done, null, Array.Empty<string>()));
                break;
            case "contextCompaction":
                result.Events.Add(new AgentNoticeEvent("對話已很長，Codex 自動壓縮了較早的內容", IsError: false));
                break;
        }
    }

    private static AgentItemStatus MapStatus(string? status) => status switch
    {
        "failed" => AgentItemStatus.Failed,
        "declined" => AgentItemStatus.Declined,
        _ => AgentItemStatus.Done,
    };

    /// <summary>fileChange 的 changes → 標題、第一個檔案路徑、合併後的行差異（每個檔案前加一行檔名）</summary>
    internal static (string Title, string? Path, IReadOnlyList<AgentDiffLine>? Diff) DescribeFileChanges(JObject item)
    {
        var changes = (item["changes"] as JArray ?? new JArray()).OfType<JObject>().ToList();
        var paths = changes.Select(c => c["path"]?.ToString()).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).ToList();
        var title = paths.Count switch
        {
            0 => "修改檔案",
            1 => paths[0],
            _ => $"{paths[0]} 等 {paths.Count} 個檔案",
        };

        var diff = new List<AgentDiffLine>();
        foreach (var change in changes)
        {
            var kind = (change["kind"] as JObject)?["type"]?.ToString() ?? change["kind"]?.ToString();
            if (changes.Count > 1)
                diff.Add(new AgentDiffLine("hunk", change["path"]?.ToString() ?? string.Empty));
            diff.AddRange(DiffRules.FromUnified(change["diff"]?.ToString(), kind));
            if (diff.Count > DiffRules.MaxLines)
                break;
        }

        return (title, paths.FirstOrDefault(), diff.Count > 0 ? diff : null);
    }

    private static string? DescribeMcpResult(JObject? result)
    {
        if (result?["content"] is not JArray content)
            return null;
        var texts = content.OfType<JObject>().Select(c => c["type"]?.ToString() == "text" ? c["text"]?.ToString() : "[非文字內容]")
            .Where(t => !string.IsNullOrEmpty(t));
        return string.Join("\n", texts);
    }

    private static AgentTurnCompletedEvent ParseTurnCompleted(JObject? turn, AgentUsage? usage)
    {
        var status = turn?.Value<string>("status");
        // error 常是 JSON null：一律先轉 JObject 再取欄位
        var error = (turn?["error"] as JObject)?["message"]?.ToString();
        var outcome = status switch
        {
            "interrupted" => AgentTurnOutcome.Interrupted,
            "failed" => AgentTurnOutcome.Failed,
            _ => AgentTurnOutcome.Completed,
        };
        return new AgentTurnCompletedEvent(outcome, error, usage);
    }
}
