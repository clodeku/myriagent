using System.Globalization;
using Newtonsoft.Json;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>一則 ACP（JSON-RPC 2.0）訊息的種類</summary>
internal enum GrokAcpMessageKind
{
    /// <summary>回應客戶端的請求（有 id、result 或 error）</summary>
    Response,

    /// <summary>agent 對客戶端的請求（有 id 與 method：核准、提問、計畫確認）</summary>
    Request,

    /// <summary>通知（只有 method）</summary>
    Notification,
}

/// <summary>背景工作通知：Kind 是 task_backgrounded 或 task_completed。WillWake 沒有欄位時是 null（不另外叫醒，避免和 grok 自己的續接重疊）</summary>
internal readonly record struct GrokBackgroundNote(string Kind, string TaskId, string? ToolCallId, string? Description, bool? WillWake);

/// <summary>JSON-RPC 的錯誤：Text 是給人看的完整說明（message 加上 data）</summary>
/// <param name="DataCode">data.code（grok 的錯誤分類，例如 FS_NOT_FOUND）；沒有為 null</param>
internal sealed record GrokAcpError(int Code, string Message, string? DataCode, string Text);

/// <summary>解析後的一則 ACP 訊息；Method 已去掉擴充方法在線上的底線前綴（_x.ai/… → x.ai/…）</summary>
internal sealed record GrokAcpMessage(GrokAcpMessageKind Kind, JToken? Id, string? Method, JObject Params, JToken? Result, GrokAcpError? Error);

/// <summary>核准卡要用的請求內容（session/request_permission）</summary>
/// <param name="AllowSessionOptionId">「本對話都允許」對應的選項（kind allow_always）；沒有為 null</param>
/// <param name="AllowOnceOptionId">允許一次（kind allow_once）</param>
/// <param name="RejectOnceOptionId">拒絕一次（kind reject_once）</param>
/// <param name="AcpKind">ACP 的工具種類（edit、execute…）</param>
/// <param name="Paths">這個操作涉及的檔案（自動接受編輯時檢查都在工作資料夾內）</param>
internal sealed record GrokPermissionRequest(
    string ToolCallId,
    string? AcpKind,
    string? AllowSessionOptionId,
    string? AllowOnceOptionId,
    string? RejectOnceOptionId,
    IReadOnlyList<string> Paths);

/// <summary>帳戶狀態的查詢結果（_x.ai/auth/info）</summary>
internal sealed record GrokAuthEvaluation(Accounts.AccountStatus Status, string? Email);

/// <summary>
/// Grok Build（grok agent stdio，ACP）的純函式規則：程序參數、JSON-RPC 訊息、session 請求、權限模式、工具與核准卡的對應、
/// 模型清單、用量、提問與計畫確認的回覆。協定實測見 docs/protocol-notes.md 的「Grok Build」一節
/// </summary>
internal static class GrokAcpRules
{
    /// <summary>每個對話一個 grok agent：不連 leader（多視窗共用的背景程序），以 stdio 講 ACP</summary>
    public static IReadOnlyList<string> AgentArguments { get; } = new[] { "agent", "--no-leader", "stdio" };

    /// <summary>app 啟動的 grok 一律不在背景自動更新（換版由 app 協調）</summary>
    public const string DisableAutoUpdaterVariable = "GROK_DISABLE_AUTOUPDATER";

    /// <summary>隔離帳戶的設定資料夾</summary>
    public const string HomeVariable = "GROK_HOME";

    /// <summary>
    /// 隔離帳戶預先寫好的 config.toml：標記「Claude 設定已匯入過」，grok 才不會讀使用者的 ~/.claude（權限規則、env、hooks、skill）。
    /// grok 之後會自己改寫這個檔（加外掛市集），這一段會保留（實測）
    /// </summary>
    public const string IsolatedConfigToml = "[claude_compat]\nimported = true\n";

    /// <summary>重開時被中斷的工具卡說明（狀態是「已停止」）</summary>
    public const string InterruptedToolOutput = "Grok 程序已結束，這個工具被中斷，結果未知";

    /// <summary>停止時還在執行的工具（grok 取消後不會再送它的結束更新）</summary>
    public const string CancelledToolOutput = "已停止";

    public const string NotSignedInMessage = "Grok 尚未登入，請到「帳戶」頁按登入，完成登入後再送出";

    /// <summary>系統預設帳戶的設定資料夾：%USERPROFILE%\.grok</summary>
    public static string ResolveHome(string userProfile) => Path.Combine(userProfile, ".grok");

    /// <summary>
    /// 隔離帳戶的資料夾：建立資料夾，config.toml 不存在就寫入 IsolatedConfigToml；已存在但沒有 [claude_compat] 段（還原的資料夾、被手動改過）就補上。
    /// 失敗只記錄（grok 照樣能跑，只是會匯入使用者的 Claude 設定）
    /// </summary>
    public static void EnsureIsolatedHome(string home)
    {
        try
        {
            Directory.CreateDirectory(home);
            var config = Path.Combine(home, "config.toml");
            if (!File.Exists(config))
            {
                File.WriteAllText(config, IsolatedConfigToml);
                return;
            }
            var text = File.ReadAllText(config);
            if (!HasClaudeCompatSection(text))
                File.AppendAllText(config, (text.Length == 0 || text.EndsWith('\n') ? "\n" : "\n\n") + IsolatedConfigToml);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Core.Log.Warn($"無法準備 Grok 隔離資料夾 {home}：{ex.Message}");
        }
    }

    /// <summary>config.toml 有沒有 [claude_compat] 段（行首、可有空白）</summary>
    public static bool HasClaudeCompatSection(string? toml)
        => !string.IsNullOrEmpty(toml)
           && toml.Replace("\r\n", "\n").Split('\n').Any(line => line.Trim() == "[claude_compat]");

    // ───────────────────────────── JSON-RPC ─────────────────────────────

    /// <summary>解析一行 stdout；不是 JSON-RPC 物件（啟動訊息、空行）回 null</summary>
    public static GrokAcpMessage? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
            return null;
        JObject json;
        try
        {
            // 不把像日期的字串轉成 DateTime：代理的文字片段、工具參數要原樣保留
            using var reader = new JsonTextReader(new StringReader(line)) { DateParseHandling = DateParseHandling.None };
            if (JToken.ReadFrom(reader) is not JObject parsed)
                return null;
            json = parsed;
        }
        catch (JsonException)
        {
            return null;
        }

        var method = json.Value<string>("method") is { } m ? StripExtensionPrefix(m) : null;
        var id = json["id"] is { Type: not JTokenType.Null } i ? i : null;
        var parameters = json["params"] as JObject ?? new JObject();
        if (method != null)
            return new GrokAcpMessage(id != null ? GrokAcpMessageKind.Request : GrokAcpMessageKind.Notification, id, method, parameters, null, null);
        if (id == null)
            return null;
        var error = json["error"] is JObject e ? ParseError(e) : null;
        return new GrokAcpMessage(GrokAcpMessageKind.Response, id, null, parameters, error == null ? json["result"] : null, error);
    }

    /// <summary>ACP 自訂方法在線上加了底線前綴（_x.ai/billing）：比對時一律去掉</summary>
    public static string StripExtensionPrefix(string method) => method.StartsWith('_') ? method[1..] : method;

    public static GrokAcpError ParseError(JObject error)
    {
        var code = error.Value<int?>("code") ?? 0;
        var message = error.Value<string>("message") ?? "未知的錯誤";
        string? dataCode = null;
        string? detail = null;
        switch (error["data"])
        {
            case JObject data:
                dataCode = data.Value<string>("code");
                detail = data.Value<string>("detail") ?? data.Value<string>("message");
                break;
            case JValue { Type: JTokenType.String } text:
                detail = (string?)text;
                break;
        }
        var full = string.IsNullOrWhiteSpace(detail) || detail == message ? message : $"{message}（{detail}）";
        return new GrokAcpError(code, message, dataCode, full);
    }

    public static string BuildRequest(long id, string method, JObject? parameters)
        => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new JObject(),
        }.ToString(Formatting.None);

    public static string BuildNotification(string method, JObject? parameters)
        => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters ?? new JObject(),
        }.ToString(Formatting.None);

    public static string BuildResult(JToken id, JToken result)
        => new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result }.ToString(Formatting.None);

    public static string BuildError(JToken id, int code, string message)
        => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
        }.ToString(Formatting.None);

    /// <summary>請求鍵（核准卡、提問卡）：JSON-RPC id 的字串形式</summary>
    public static string RequestKey(JToken id) => id.Type == JTokenType.String ? (string)id! : id.ToString(Formatting.None);

    // ───────────────────────────── 方法名稱（客戶端 → agent） ─────────────────────────────

    public const string MethodBilling = "_x.ai/billing";

    /// <summary>手動壓縮（斜線指令 /compact 由會話攔截後送這個方法，不當成一般提示）</summary>
    public const string MethodCompact = "_x.ai/compact_conversation";

    /// <summary>壓縮成功的提示（AgentNoticeEvent）</summary>
    public const string CompactedNotice = "已壓縮較早的對話內容";

    /// <summary>grok 拒絕產生影片時的英文原文（工具輸出含這句就改發中文說明）</summary>
    public const string ZeroRetentionMarker = "Video generation tools are unavailable under zero data retention (ZDR)";

    public const string MethodAuthInfo = "_x.ai/auth/info";
    public const string MethodAuthGetUrl = "_x.ai/auth/get_url";
    public const string MethodAuthCancel = "_x.ai/auth/cancel";
    public const string MethodPermissionMode = "_x.ai/yolo_mode_changed";

    /// <summary>初始化：宣告不提供檔案與終端（grok 用自己的工具），也不宣告互動式資料夾信任（不會收到 folder_trust 請求）</summary>
    public static JObject BuildInitializeParams(string clientVersion)
        => new()
        {
            ["protocolVersion"] = 1,
            ["clientCapabilities"] = new JObject
            {
                ["fs"] = new JObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                ["terminal"] = false,
            },
            ["clientInfo"] = new JObject { ["name"] = "myriagent", ["title"] = "Clode Myriagent", ["version"] = clientVersion },
        };

    /// <summary>initialize 回應的 agentVersion（_meta.agentVersion）</summary>
    public static string? ParseAgentVersion(JToken? initializeResult)
        => ((initializeResult as JObject)?["_meta"] as JObject)?["agentVersion"] is JValue { Type: JTokenType.String } v ? Cli.CliVersionRules.ParseVersion((string?)v) : null;

    /// <summary>
    /// initialize 的 promptCapabilities.image。沒有這個欄位（舊版、假 CLI）維持 false：圖片只寫路徑。
    /// grok 1.0.67 實測是 true
    /// </summary>
    public static bool ParsePromptImages(JToken? initializeResult)
        => ((initializeResult as JObject)?["agentCapabilities"] as JObject)?["promptCapabilities"] is JObject caps && caps.Value<bool?>("image") == true;

    /// <summary>模型 "default"／空白＝不指定（用 grok 自己的預設）</summary>
    public static string? NormalizeModel(string? model)
        => string.IsNullOrWhiteSpace(model) || model.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : model.Trim();

    /// <summary>
    /// session/new：sessionId 由 app 預先產生（續接鍵不必等回應）；askUserQuestion＝開提問工具（由 app 畫問題卡）；
    /// 權限模式（yoloMode／autoMode）在建立時就帶上，之後再以 _x.ai/yolo_mode_changed 確認
    /// </summary>
    public static JObject BuildNewSessionParams(string sessionId, string workingDirectory, string? model, AgentPermissionMode mode)
    {
        var meta = SessionMeta(mode);
        meta["sessionId"] = sessionId;
        if (NormalizeModel(model) is { } m)
            meta["modelId"] = m;
        return new JObject
        {
            ["cwd"] = workingDirectory,
            ["mcpServers"] = new JArray(),
            ["_meta"] = meta,
        };
    }

    /// <summary>session/resume（不重播歷史：畫面上已有內容）</summary>
    public static JObject BuildResumeParams(string sessionId, string workingDirectory, AgentPermissionMode mode)
        => new()
        {
            ["sessionId"] = sessionId,
            ["cwd"] = workingDirectory,
            ["mcpServers"] = new JArray(),
            ["_meta"] = SessionMeta(mode),
        };

    private static JObject SessionMeta(AgentPermissionMode mode)
        => new()
        {
            ["askUserQuestion"] = true,
            ["yoloMode"] = mode == AgentPermissionMode.FullAccess,
            ["autoMode"] = mode == AgentPermissionMode.Auto,
        };

    /// <summary>續接失敗的原因是找不到這個對話（grok 1.0.52：-32603 Path not found、data.code FS_NOT_FOUND；prompt 帶未知 id 是 unknown session id）</summary>
    public static bool IsSessionNotFound(GrokAcpError? error)
        => error != null
           && (string.Equals(error.DataCode, "FS_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
               || error.Text.Contains("unknown session", StringComparison.OrdinalIgnoreCase)
               || error.Text.Contains("session not found", StringComparison.OrdinalIgnoreCase));

    /// <summary>沒有登入（session/new、billing 回 -32000 Authentication required）</summary>
    public static bool IsAuthRequired(GrokAcpError? error)
        => error != null && error.Text.Contains("Authentication required", StringComparison.OrdinalIgnoreCase);

    /// <summary>session/prompt。文字區塊永遠在第一個（假 CLI 讀 prompt[0].text）；圖片接在後面（mimeType＋base64）</summary>
    public static JObject BuildPromptParams(string sessionId, string text, IReadOnlyList<(string MimeType, string Data)>? images = null)
    {
        var prompt = new JArray { new JObject { ["type"] = "text", ["text"] = text ?? string.Empty } };
        if (images != null)
        {
            foreach (var (mimeType, data) in images)
                prompt.Add(new JObject { ["type"] = "image", ["mimeType"] = mimeType, ["data"] = data });
        }
        return new JObject { ["sessionId"] = sessionId, ["prompt"] = prompt };
    }

    /// <summary>_x.ai/compact_conversation：只帶 sessionId（1.0.67 會開始 pre_compact）</summary>
    public static JObject BuildCompactParams(string sessionId) => new() { ["sessionId"] = sessionId };

    /// <summary>使用者輸入是 /compact（與 /clear 一樣：去掉前後空白、大小寫相符才算）</summary>
    public static bool IsCompactCommand(string? text) => string.Equals(text?.Trim(), "/compact", StringComparison.Ordinal);

    /// <summary>
    /// 使用者輸入是 /context-window，或後面只帶一個正整數。多出來的字不算（交給模型）。
    /// ACP 沒有切換視窗的方法，會話只發說明、不送提示
    /// </summary>
    public static bool TryParseContextWindowCommand(string? text, out long? requested)
    {
        requested = null;
        var trimmed = text?.Trim();
        const string name = "/context-window";
        if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith(name, StringComparison.Ordinal))
            return false;
        var rest = trimmed[name.Length..];
        if (rest.Length == 0)
            return true;
        if (rest[0] is not (' ' or '\t'))
            return false;
        rest = rest.Trim();
        if (rest.Length == 0)
            return true;
        if (rest.Any(char.IsWhiteSpace))
            return false;
        if (!long.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0)
            return false;
        requested = size;
        return true;
    }

    /// <summary>ACP 不能切換上下文視窗時的說明：目前的 totalContextTokens、清單上的可選大小、終端機指令與模型設定</summary>
    public static string DescribeContextWindow(long? requested, string? modelId, long? currentWindow, IReadOnlyList<long>? choices)
    {
        var current = currentWindow is { } window ? window.ToString("N0", CultureInfo.InvariantCulture) : "不明";
        var model = string.IsNullOrWhiteSpace(modelId) ? "目前的模型" : modelId.Trim();
        var asked = requested is { } size ? $"（你指定了 {size.ToString("N0", CultureInfo.InvariantCulture)}）" : string.Empty;
        var listed = choices is { Count: > 0 }
            ? "這個模型回報的可選大小是 " + string.Join("、", choices.Select(n => n.ToString("N0", CultureInfo.InvariantCulture))) + "。"
            : string.Empty;
        return $"ACP 連線不能切換上下文視窗{asked}。{model} 目前的視窗是 {current} 個 token。{listed}請在終端機用 /context-window，或在模型設定寫 context_windows。";
    }

    /// <summary>session/set_config_option：值直接是字串（手冊寫的 {value:{value}} 會回 -32602，實測）</summary>
    public static JObject BuildSetConfigParams(string sessionId, string configId, string value)
        => new() { ["sessionId"] = sessionId, ["configId"] = configId, ["value"] = value };

    public static JObject BuildCancelParams(string sessionId) => new() { ["sessionId"] = sessionId };

    /// <summary>
    /// 權限模式 → _x.ai/yolo_mode_changed 的內容（對話中途即時生效；grok 不寫回設定檔）。
    /// 每步詢問、自動接受編輯、規劃模式都是 grok 的 ask（自動接受編輯由會話代為核准、規劃模式另外以 session/set_mode 切換）
    /// </summary>
    public static JObject BuildPermissionModeParams(AgentPermissionMode mode) => mode switch
    {
        AgentPermissionMode.FullAccess => new JObject { ["yolo_mode"] = true, ["permission_mode"] = "always-approve" },
        AgentPermissionMode.Auto => new JObject { ["yolo_mode"] = false, ["permission_mode"] = "auto" },
        _ => new JObject { ["yolo_mode"] = false, ["permission_mode"] = "ask" },
    };

    /// <summary>session/set_mode 的 modeId：規劃模式是 plan，其他是 default</summary>
    public static string SessionModeId(AgentPermissionMode mode) => mode == AgentPermissionMode.Plan ? "plan" : "default";

    public static JObject BuildSetModeParams(string sessionId, AgentPermissionMode mode)
        => new() { ["sessionId"] = sessionId, ["modeId"] = SessionModeId(mode) };

    // ───────────────────────────── 一輪的結果 ─────────────────────────────

    /// <summary>session/prompt 的 stopReason → 這一輪的結果；refusal 當成失敗並說明</summary>
    public static (AgentTurnOutcome Outcome, string? Error) EvaluateStopReason(string? stopReason) => stopReason switch
    {
        "cancelled" => (AgentTurnOutcome.Interrupted, null),
        "refusal" => (AgentTurnOutcome.Failed, "Grok 拒絕回應這個要求"),
        _ => (AgentTurnOutcome.Completed, null),
    };

    /// <summary>stopReason 是因為上限而停下（給使用者的提示）；其他回 null</summary>
    public static string? DescribeStopLimit(string? stopReason) => stopReason switch
    {
        "max_tokens" => "Grok 的回覆達到輸出長度上限而中斷",
        "max_turn_requests" => "Grok 這一輪達到工具呼叫次數上限而停下",
        _ => null,
    };

    /// <summary>prompt 回應的 _meta.usage（整輪合計）→ 用量；costUsdTicks＝美元×10¹⁰（API 等值，只供顯示）</summary>
    /// <summary>
    /// 這一輪的 token 增量（只有 session/prompt 的結果有用量，grok 沒有逐次呼叫的通知）：依 _meta.usage.modelUsage 逐模型；
    /// 舊版沒有 modelUsage 時整份記在 <paramref name="fallbackModel"/>。沒有用量回空的
    /// </summary>
    public static List<AgentTokenUsageEvent> ParseTokenEvents(JToken? promptResult, string? fallbackModel)
    {
        var events = new List<AgentTokenUsageEvent>();
        if (((promptResult as JObject)?["_meta"] as JObject)?["usage"] is not JObject usage)
            return events;
        if (usage["modelUsage"] is JObject byModel && byModel.Count > 0)
        {
            foreach (var property in byModel.Properties())
            {
                if (property.Value is not JObject m)
                    continue;
                var delta = new AgentUsage(m.Value<long?>("inputTokens") ?? 0, m.Value<long?>("outputTokens") ?? 0, m.Value<long?>("cachedReadTokens") ?? 0, null);
                if (!TokenTallyRules.IsZero(delta))
                    events.Add(new AgentTokenUsageEvent(delta, property.Name));
            }
            return events;
        }
        var total = new AgentUsage(usage.Value<long?>("inputTokens") ?? 0, usage.Value<long?>("outputTokens") ?? 0, usage.Value<long?>("cachedReadTokens") ?? 0, null);
        if (!TokenTallyRules.IsZero(total))
            events.Add(new AgentTokenUsageEvent(total, string.IsNullOrWhiteSpace(fallbackModel) ? null : fallbackModel));
        return events;
    }

    public static AgentUsage? ParseUsage(JToken? promptResult)
    {
        if (((promptResult as JObject)?["_meta"] as JObject)?["usage"] is not JObject usage)
            return null;
        var ticks = usage.Value<double?>("costUsdTicks");
        return new AgentUsage(
            usage.Value<long?>("inputTokens") ?? 0,
            usage.Value<long?>("outputTokens") ?? 0,
            usage.Value<long?>("cachedReadTokens") ?? 0,
            ticks.HasValue ? Math.Round(ticks.Value / 1e10, 6) : null);
    }

    // ───────────────────────────── 帳戶 ─────────────────────────────

    /// <summary>
    /// _x.ai/auth/info → 登入狀態：有信箱＝已登入；沒有信箱但有登入方式（API Key 之類）＝API Key；都沒有＝未登入。
    /// 只讀（不像 check_subscription 會順便 refresh 權杖）
    /// </summary>
    public static GrokAuthEvaluation EvaluateAuthInfo(JToken? result)
    {
        var info = result as JObject;
        var email = info?.Value<string>("email");
        if (!string.IsNullOrWhiteSpace(email))
            return new GrokAuthEvaluation(Accounts.AccountStatus.Ready, email);
        var method = info?.Value<string>("methodId");
        return method != null && method.Contains("api", StringComparison.OrdinalIgnoreCase)
            ? new GrokAuthEvaluation(Accounts.AccountStatus.ApiKey, null)
            : new GrokAuthEvaluation(Accounts.AccountStatus.NotLoggedIn, null);
    }

    /// <summary>_x.ai/billing 的方案名稱（頂層 subscription_tier，例 "SuperGrok"）</summary>
    public static string? ParsePlan(JToken? billing)
        => (billing as JObject)?.Value<string>("subscription_tier") is { Length: > 0 } tier ? tier.Trim() : null;

    /// <summary>_x.ai/auth/get_url 的登入網址：只接受 https（裝置碼已在網址裡）</summary>
    public static Uri? ParseAuthUrl(JToken? result)
        => (result as JObject)?.Value<string>("auth_url") is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;

    /// <summary>登入網址的主機（2026-10-22 實測；伺服器轉給瀏覽器之前以 RemoteLoginUrlRules 的白名單再驗一次）</summary>
    public const string AuthHost = "accounts.x.ai";

    /// <summary>Grok 沒回裝置碼的期限：app 給 10 分鐘，到期就取消這次登入、請使用者重來</summary>
    public static readonly TimeSpan DeviceCodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 登入網址 → 給網頁顯示的裝置碼（設計 2026-10-22-grok-antigravity-sync 的 P3；tools/probes/grok/login_url_probe.py 實測：
    /// <c>https://accounts.x.ai/&lt;路徑&gt;/device?user_code=XXXX-XXXX</c>、get_url 沒回期限）。https、預設埠、沒有帳密、主機是
    /// <see cref="AuthHost"/>、查詢參數 <c>user_code</c> 是 4～16 個大寫英數字與連字號才收，否則 null。驗證網址就是整個登入網址
    /// （代碼已帶在裡面）；LoginId 固定 <c>grok</c>（沒有登入 id，取消用 <c>_x.ai/auth/cancel</c>）
    /// </summary>
    public static Myriagent.Accounts.CodexDeviceCode? ParseDeviceCode(Uri? url, DateTimeOffset now)
    {
        if (url is null || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || !string.IsNullOrEmpty(url.UserInfo)
            || !string.Equals(url.IdnHost, AuthHost, StringComparison.OrdinalIgnoreCase))
            return null;
        string? code = null;
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == "user_code")
                code = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        if (code is not { Length: >= 4 and <= 16 } || code.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-") >= 0
            || code[0] == '-' || code[^1] == '-')
            return null;
        return new Myriagent.Accounts.CodexDeviceCode("grok", code, url, (now + DeviceCodeLifetime).ToUnixTimeMilliseconds());
    }

    // ───────────────────────────── 模型與指令 ─────────────────────────────

    /// <summary>
    /// 模型清單：initialize 的 _meta.modelState、session/new 的 models、_x.ai/models/update 都是
    /// {currentModelId, availableModels:[{modelId, name, description, _meta:{reasoningEfforts:[{value}]}}]}；
    /// 前面加一個「預設」（grok 自己的預設模型，思考強度取它的清單）
    /// </summary>
    public static List<AgentModelOption> ParseModels(JToken? state)
    {
        var result = new List<AgentModelOption>();
        if ((state as JObject)?["availableModels"] is not JArray models)
            return result;
        var current = state.Value<string>("currentModelId");
        foreach (var model in models.OfType<JObject>())
        {
            var id = model.Value<string>("modelId");
            if (string.IsNullOrWhiteSpace(id))
                continue;
            var efforts = ((model["_meta"] as JObject)?["reasoningEfforts"] as JArray)?
                .OfType<JObject>()
                .Select(e => e.Value<string>("value") ?? e.Value<string>("id"))
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .Reverse()   // grok 由高到低列，選單由低到高
                .ToList() ?? new List<string>();
            result.Add(new AgentModelOption(id, model.Value<string>("name") ?? id, model.Value<string>("description"), efforts));
        }
        if (result.Count == 0)
            return result;
        var fallback = result.FirstOrDefault(m => m.Value == current) ?? result[0];
        result.Insert(0, new AgentModelOption("default", "預設", $"grok 的預設模型（目前是 {fallback.Label}）", fallback.Efforts));
        return result;
    }

    /// <summary>initialize 回應的模型（_meta.modelState）</summary>
    public static List<AgentModelOption> ParseInitializeModels(JToken? initializeResult)
        => ParseModels(((initializeResult as JObject)?["_meta"] as JObject)?["modelState"]);

    /// <summary>這個模型支援的思考強度（清單裡沒有就不送 reasoning_effort）</summary>
    public static bool SupportsEffort(IReadOnlyList<AgentModelOption>? models, string? model, string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return false;
        var id = NormalizeModel(model) ?? "default";
        var option = models?.FirstOrDefault(m => m.Value == id);
        return option == null || option.Efforts.Count == 0 || option.Efforts.Contains(effort, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// available_commands_update → 「/」指令。always-approve 不列（權限模式由 app 的選單切換，兩邊會不一致）
    /// </summary>
    public static List<AgentSlashCommand> ParseCommands(JToken? commands)
    {
        var result = new List<AgentSlashCommand>();
        foreach (var command in (commands as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            var name = command.Value<string>("name");
            if (string.IsNullOrWhiteSpace(name) || HiddenCommands.Contains(name))
                continue;
            result.Add(new AgentSlashCommand(name, command.Value<string>("description"), (command["input"] as JObject)?.Value<string>("hint")));
        }
        return result;
    }

    private static readonly HashSet<string> HiddenCommands = new(StringComparer.OrdinalIgnoreCase) { "always-approve" };

    // ───────────────────────────── 輸入 ─────────────────────────────

    /// <summary>
    /// 這一輪要送的文字：新對話先放背景說明；續接不到、改開新對話時放前文節錄；換過工作資料夾時開頭告知。
    /// imagesInline 只在這次 initialize 的 promptCapabilities.image 是 true 時打開（圖片另以區塊附上）；否則路徑說明請代理自己讀
    /// </summary>
    public static string BuildTurnText(string? instructions, string? historyRecap, string? workingDirectoryNotice, string? text, IReadOnlyList<AgentAttachment> attachments, bool imagesInline = false)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(instructions))
            parts.Add($"<myriagent_instructions>\n{instructions.Trim()}\n</myriagent_instructions>");
        if (!string.IsNullOrWhiteSpace(historyRecap))
            parts.Add(historyRecap.Trim());
        if (!string.IsNullOrWhiteSpace(workingDirectoryNotice))
            parts.Add(workingDirectoryNotice);
        parts.Add((text ?? string.Empty) + AgentAttachmentRules.BuildAttachmentNote(attachments, imagesInline));
        return string.Join("\n\n", parts);
    }

    /// <summary>工作資料夾換了（程序以新的資料夾重開、session/resume 帶新的 cwd）：這一則訊息開頭告訴代理</summary>
    public static string BuildWorkingDirectoryNotice(string workingDirectory)
        => $"（系統通知：工作資料夾已改為 {workingDirectory}，之後的檔案與指令請以這個資料夾為準）";

    /// <summary>要換新程序的設定（模型、思考強度、權限模式在程序裡即時切換，不在其中；精簡模式變了要重開才能卸載／載入全域指示與 skills）</summary>
    public static string Signature(string accountKey, string? launcher, string workingDirectory, string conversationDirectory, bool useLocalCustomizations = true)
        => string.Join("|", accountKey, launcher, workingDirectory, conversationDirectory, useLocalCustomizations);

    /// <summary>工作資料夾是否相同（不分大小寫、忽略結尾的反斜線）</summary>
    public static bool SameDirectory(string? a, string? b)
        => a != null && b != null && string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // ───────────────────────────── 核准 ─────────────────────────────

    /// <summary>session/request_permission 的內容：選項依 kind 對應（實測：編輯有 allow-edits-session、指令有 always-allow 與 reject-always）</summary>
    public static GrokPermissionRequest ParsePermissionRequest(JObject parameters)
    {
        var toolCall = parameters["toolCall"] as JObject ?? new JObject();
        var options = (parameters["options"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
        string? Option(string kind) => options.FirstOrDefault(o => o.Value<string>("kind") == kind)?.Value<string>("optionId");
        return new GrokPermissionRequest(
            toolCall.Value<string>("toolCallId") ?? string.Empty,
            toolCall.Value<string>("kind"),
            Option("allow_always"),
            Option("allow_once"),
            Option("reject_once"),
            CollectPaths(toolCall));
    }

    /// <summary>工具呼叫涉及的檔案：locations[].path、diff 內容的 path、rawInput 的 file_path／path</summary>
    public static List<string> CollectPaths(JObject? toolCall)
    {
        var paths = new List<string>();
        if (toolCall == null)
            return paths;
        foreach (var location in (toolCall["locations"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            Add(location.Value<string>("path"));
        foreach (var content in (toolCall["content"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            if (content.Value<string>("type") == "diff")
                Add(content.Value<string>("path"));
        }
        if (toolCall["rawInput"] is JObject raw)
        {
            Add(raw.Value<string>("file_path"));
            Add(raw.Value<string>("path"));
            Add(raw.Value<string>("target_file"));
        }
        return paths;

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                paths.Add(path);
        }
    }

    /// <summary>
    /// 自動接受編輯：kind 是 edit、至少有一個路徑、每個路徑都在工作資料夾或對話資料夾之內（以完整路徑比對，擋掉 ..\ 跳出；
    /// 任何一層的 .git、.grok、.claude 不算；經過交接點、符號連結的不算）。attributes 是查檔案屬性的方法（測試替換；預設查磁碟）
    /// </summary>
    public static bool CanAutoApproveEdit(GrokPermissionRequest request, string workingDirectory, string conversationDirectory,
        Func<string, FileAttributes?>? attributes = null)
    {
        if (request.AcpKind != "edit" || request.Paths.Count == 0 || request.AllowOnceOptionId == null)
            return false;
        attributes ??= ReadAttributes;
        return request.Paths.All(path => IsInside(path, workingDirectory, attributes) || IsInside(path, conversationDirectory, attributes));
    }

    private static readonly string[] ProtectedFolders = { ".git", ".grok", ".claude" };

    internal static bool IsInside(string path, string root, Func<string, FileAttributes?> attributes)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        string full, baseDir;
        try
        {
            full = Path.GetFullPath(path);
            baseDir = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        // 磁碟代號之後的冒號是 NTFS 串流語法：.git::$INDEX_ALLOCATION\hooks 實際寫進 .git\hooks，檔名:串流 寫進看不到的資料串流
        if (full.IndexOf(':', 2) >= 0 || !full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
            return false;
        var segments = full[baseDir.Length..].Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => ProtectedFolders.Contains(s.TrimEnd('.', ' '), StringComparer.OrdinalIgnoreCase)))
            return false;
        // 工作資料夾之下的交接點、符號連結（git 簽出的 symlink）會讓寫入落到別處：一層層查已存在的部分（不存在的部分之後也不會存在）
        var current = baseDir.TrimEnd('\\');
        foreach (var segment in segments)
        {
            current = current + Path.DirectorySeparatorChar + segment;
            if (attributes(current) is not { } attr)
                break;
            if (attr.HasFlag(FileAttributes.ReparsePoint))
                return false;
        }
        return true;
    }

    /// <summary>查檔案屬性，不跟隨重新剖析點（不存在或查不到回 null）</summary>
    private static FileAttributes? ReadAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 「本對話都允許」之後由會話代為核准的 ACP kind：只有 grok 說明過效力範圍的 edit（之後的編輯）與 execute（之後的指令）。
    /// 其他 kind（MCP 工具、網頁擷取等都是 other）只把 allow_always 轉給 grok，不由 app 擴大成整個 kind 都放行
    /// </summary>
    public static bool RemembersSessionAllow(string? acpKind) => acpKind is "edit" or "execute";

    /// <summary>核准卡的選擇 → 回覆（選項不存在時退回允許一次或拒絕一次；都沒有就以 cancelled 回覆）</summary>
    public static JObject BuildPermissionOutcome(GrokPermissionRequest request, AgentApprovalChoice choice)
    {
        var optionId = choice switch
        {
            AgentApprovalChoice.AllowForSession => request.AllowSessionOptionId ?? request.AllowOnceOptionId,
            AgentApprovalChoice.Allow => request.AllowOnceOptionId,
            _ => request.RejectOnceOptionId,
        };
        return optionId == null ? CancelledPermissionOutcome() : SelectedPermissionOutcome(optionId);
    }

    public static JObject SelectedPermissionOutcome(string optionId)
        => new() { ["outcome"] = new JObject { ["outcome"] = "selected", ["optionId"] = optionId } };

    /// <summary>停止、程序要關閉時把未回應的核准請求以 cancelled 回覆（ACP 的規定）</summary>
    public static JObject CancelledPermissionOutcome() => new() { ["outcome"] = new JObject { ["outcome"] = "cancelled" } };

    /// <summary>「本對話都允許」按鈕的說明（grok 的 allow_always：編輯是整個對話的編輯、指令是之後的 bash 指令都不問）</summary>
    public static string? AllowSessionLabel(string? acpKind) => acpKind switch
    {
        "edit" => "本對話的編輯都允許",
        "execute" => "本對話的指令都不再詢問",
        _ => null,
    };

    // ───────────────────────────── 提問與計畫確認 ─────────────────────────────

    /// <summary>x.ai/ask_user_question 的題目：{questions:[{question, options:[{label, description, preview}], multiSelect}]}；答案以題目文字為鍵</summary>
    public static List<AgentQuestion> ParseQuestions(JObject parameters)
    {
        var result = new List<AgentQuestion>();
        foreach (var q in (parameters["questions"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            var text = q.Value<string>("question");
            if (string.IsNullOrWhiteSpace(text))
                continue;
            result.Add(new AgentQuestion
            {
                Id = text,
                Text = text,
                MultiSelect = (q.Value<bool?>("multiSelect") ?? q.Value<bool?>("multi_select")) == true,
                AllowOther = true,
                Options = (q["options"] as JArray)?.OfType<JObject>()
                    .Where(o => !string.IsNullOrWhiteSpace(o.Value<string>("label")))
                    .Select(o => new AgentQuestionOption { Label = o.Value<string>("label")!, Description = o.Value<string>("description"), Preview = o.Value<string>("preview") })
                    .ToList() ?? new List<AgentQuestionOption>(),
            });
        }
        return result;
    }

    /// <summary>
    /// 問卷的答案 → {outcome:"accepted", answers:{題目:[選項…]}, annotations:{題目:{notes}}}。
    /// 不是選項的文字（使用者自填）照 grok 的格式寫成 "Other"，文字放進 annotations 的 notes
    /// </summary>
    public static JObject BuildQuestionAnswer(IReadOnlyList<AgentQuestion> questions, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var answerJson = new JObject();
        var annotations = new JObject();
        foreach (var question in questions)
        {
            if (!answers.TryGetValue(question.Id, out var values) || values.Count == 0)
                continue;
            var labels = new JArray();
            var notes = new List<string>();
            foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
            {
                if (question.Options.Any(o => o.Label == value))
                {
                    labels.Add(value);
                }
                else
                {
                    notes.Add(value.Trim());
                    if (!labels.Any(l => (string?)l == "Other"))
                        labels.Add("Other");
                }
            }
            if (labels.Count == 0)
                continue;
            answerJson[question.Id] = labels;
            if (notes.Count > 0)
                annotations[question.Id] = new JObject { ["notes"] = string.Join("\n", notes) };
        }
        var result = new JObject { ["outcome"] = "accepted", ["answers"] = answerJson };
        if (annotations.Count > 0)
            result["annotations"] = annotations;
        return result;
    }

    public static JObject BuildQuestionCancelled() => new() { ["outcome"] = "cancelled" };

    /// <summary>x.ai/exit_plan_mode 的回覆：同意＝approved；繼續規劃＝cancelled（附說明）；作廢＝abandoned</summary>
    public static JObject BuildPlanResponse(AgentPlanChoice choice, string? feedback)
    {
        if (choice != AgentPlanChoice.KeepPlanning)
            return new JObject { ["outcome"] = "approved" };
        var result = new JObject { ["outcome"] = "cancelled" };
        if (!string.IsNullOrWhiteSpace(feedback))
            result["feedback"] = feedback.Trim();
        return result;
    }

    public static JObject BuildPlanAbandoned() => new() { ["outcome"] = "abandoned" };

    /// <summary>計畫確認卡的內容（planContent，沒有就說明在上面的回覆裡）</summary>
    public static string DescribePlan(JObject parameters)
        => parameters.Value<string>("planContent") is { Length: > 0 } plan ? plan : "（計畫內容在上面的回覆裡）";

    // ───────────────────────────── 工具 ─────────────────────────────

    /// <summary>工具卡的畫面名稱與種類（決定圖示）：先看 x.ai/tool 的名稱，沒有就照 ACP 的 kind</summary>
    private static readonly Dictionary<string, (string Name, string Kind)> KnownTools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["run_terminal_command"] = ("執行指令", "bash"),
        ["run_terminal_cmd"] = ("執行指令", "bash"),
        ["bash"] = ("執行指令", "bash"),
        ["get_task_output"] = ("查看背景工作", "bash"),
        ["get_command_or_subagent_output"] = ("查看背景工作", "bash"),
        ["kill_task"] = ("停止背景工作", "bash"),
        ["kill_command_or_subagent"] = ("停止背景工作", "bash"),
        ["monitor"] = ("監看輸出", "bash"),
        ["read"] = ("讀取檔案", "read"),
        ["read_file"] = ("讀取檔案", "read"),
        ["write"] = ("寫入檔案", "write"),
        ["edit"] = ("修改檔案", "edit"),
        ["search_replace"] = ("修改檔案", "edit"),
        ["hashline_edit"] = ("修改檔案", "edit"),
        ["list_dir"] = ("列出資料夾", "search"),
        ["glob"] = ("尋找檔案", "search"),
        ["grep"] = ("搜尋內容", "search"),
        ["lsp"] = ("程式碼導覽", "search"),
        ["web_search"] = ("網路搜尋", "web"),
        ["web_fetch"] = ("讀取網頁", "web"),
        ["todo_write"] = ("待辦清單", "todo"),
        ["todowrite"] = ("待辦清單", "todo"),
        ["task"] = ("子代理", "agent"),
        ["skill"] = ("技能", "other"),
        ["image_gen"] = ("產生圖片", "image"),
        ["image_edit"] = ("修改圖片", "image"),
        ["image_to_video"] = ("圖片轉影片", "image"),
        ["reference_to_video"] = ("參考生成影片", "image"),
        ["video_gen"] = ("產生影片", "image"),
        ["enter_plan_mode"] = ("進入規劃模式", "plan"),
    };

    /// <summary>不畫成工具卡的工具：提問與計畫確認由各自的卡片呈現</summary>
    public static bool IsHiddenTool(string? toolName)
        => toolName is "ask_user_question" or "exit_plan_mode";

    public sealed record ToolDescription(string Name, string Kind, string Title, string? Detail, string? FilePath, IReadOnlyList<AgentDiffLine>? Diff);

    /// <summary>
    /// 工具呼叫（tool_call 與之後 tool_call_update 合併的內容）→ 卡片：名稱與種類、一行摘要（指令、路徑、查詢…）、細節、檔案、差異
    /// </summary>
    public static ToolDescription DescribeTool(JObject toolCall)
    {
        var meta = (toolCall["_meta"] as JObject)?["x.ai/tool"] as JObject;
        var toolName = meta?.Value<string>("name") ?? toolCall.Value<string>("title");
        var (name, kind) = toolName != null && KnownTools.TryGetValue(toolName, out var known) ? known : GuessTool(toolName, meta, toolCall.Value<string>("kind"));
        var raw = toolCall["rawInput"] as JObject;
        var command = First(raw, "command", "cmd");
        var path = First(raw, "file_path", "path", "target_file", "absolute_path") ?? (toolCall["locations"] as JArray)?.OfType<JObject>().FirstOrDefault()?.Value<string>("path");
        var query = First(raw, "pattern", "query", "url", "glob", "search_term", "directory");
        var description = First(raw, "description", "prompt", "task", "title");
        var acpTitle = toolCall.Value<string>("title") is { } t && t != toolName ? t.Replace("`", string.Empty) : null;
        // 搜尋卡（grep、glob）的 rawInput 同時有 pattern 與 path：標題寫「樣式　資料夾」（同 Claude 的 Grep），不能只剩資料夾（2026-10-23：網頁與副本上看不出在搜什麼）
        var search = kind == "search" && query != null ? ClaudeStreamRules.JoinNonEmpty(query, path) : null;
        var title = command ?? search ?? path ?? query ?? description ?? acpTitle ?? name;
        string? detail = command != null
            ? (string.IsNullOrWhiteSpace(description) ? command : $"{description}\n\n{command}")
            : kind is "read" or "write" or "edit" ? null
            // 參數 JSON 照 Claude、Codex 的上限截斷（2026-10-23：沒截時一個大參數整段進項目、同步與網頁）
            : raw is { Count: > 0 } ? ClaudeStreamRules.Truncate(StripVariant(raw).ToString(Formatting.Indented), ClaudeStreamRules.MaxToolDetailLength) : null;
        var filePath = kind is "read" or "write" or "edit" ? path : null;
        return new ToolDescription(name, kind, ClaudeStreamRules.FirstLine(title), detail, filePath, ParseDiff(toolCall["content"] as JArray));
    }

    /// <summary>沒列在表上的工具：MCP（mcp__伺服器__工具、namespace mcp）、x.ai/tool 的 kind、ACP 的 kind；名稱照 label 或原文</summary>
    private static (string Name, string Kind) GuessTool(string? toolName, JObject? meta, string? acpKind)
    {
        var label = meta?.Value<string>("label") ?? toolName ?? "工具";
        if ((toolName?.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase) ?? false) || meta?.Value<string>("namespace") == "mcp")
            return ($"MCP：{toolName}", "mcp");
        var kind = (meta?.Value<string>("kind") ?? acpKind ?? string.Empty).ToLowerInvariant() switch
        {
            "execute" or "background_task_action" or "kill_task_action" => "bash",
            "read" => "read",
            "write" => "write",
            "edit" or "delete" or "move" => "edit",
            "search" or "list_dir" or "list" or "lsp" => "search",
            "web_search" or "web_fetch" or "fetch" => "web",
            "task" => "agent",
            "plan" => "todo",
            "image_gen" or "image_to_video" or "reference_to_video" or "video_gen" => "image",
            _ => "other",
        };
        return (label, kind);
    }

    private static string? First(JObject? raw, params string[] names)
    {
        if (raw == null)
            return null;
        foreach (var name in names)
        {
            if (raw[name] is JValue { Type: JTokenType.String } v && !string.IsNullOrWhiteSpace((string?)v))
                return (string?)v;
        }
        return null;
    }

    /// <summary>rawInput 的 variant（grok 內部的工具變體名稱）不顯示</summary>
    private static JObject StripVariant(JObject raw)
    {
        var copy = (JObject)raw.DeepClone();
        copy.Remove("variant");
        return copy;
    }

    /// <summary>content 裡的 diff 區塊（{type:"diff", path, oldText, newText}）→ 行差異；oldText 空白＝新檔案</summary>
    public static IReadOnlyList<AgentDiffLine>? ParseDiff(JArray? content)
    {
        var diffs = content?.OfType<JObject>().Where(c => c.Value<string>("type") == "diff").ToList();
        if (diffs == null || diffs.Count == 0)
            return null;
        if (diffs.Count == 1)
        {
            var oldText = diffs[0].Value<string>("oldText");
            var newText = diffs[0].Value<string>("newText");
            return string.IsNullOrEmpty(oldText) ? DiffRules.FromNewFile(newText) : DiffRules.FromEdit(oldText, newText);
        }
        return DiffRules.FromEdits(diffs.Select(d => (d.Value<string>("oldText"), d.Value<string>("newText"))).ToList());
    }

    /// <summary>
    /// 工具的輸出文字：content 裡 {type:"content", content:{type:"text", text}} 的文字；沒有就看 rawOutput（指令的 output_for_prompt、
    /// 編輯的 tool_output_for_prompt、錯誤訊息）。CRLF 換成 LF；都沒有回 null
    /// </summary>
    public static string? ParseToolOutput(JObject? update)
    {
        if (update == null)
            return null;
        var texts = (update["content"] as JArray)?.OfType<JObject>()
            .Where(c => c.Value<string>("type") == "content")
            .Select(c => (c["content"] as JObject)?["text"]?.ToString())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        string? text = texts is { Count: > 0 } ? string.Join("\n", texts) : null;
        bool hasContentText = (update["content"] as JArray)?.OfType<JObject>().Any(c => c.Value<string>("type") == "content") == true;
        // 指令的 content 一定有（沒輸出時是空字串）：這時不退回 rawOutput（output_for_prompt 是給模型看的「exit: 0」）
        if (string.IsNullOrEmpty(text) && !hasContentText && update["rawOutput"] is JObject raw)
        {
            text = raw.Value<string>("output_for_prompt")
                ?? raw.Value<string>("error")
                ?? raw.Value<string>("message")
                ?? raw.Properties().Select(p => p.Value as JObject).FirstOrDefault(o => o?["tool_output_for_prompt"] != null)?.Value<string>("tool_output_for_prompt");
        }
        return string.IsNullOrEmpty(text) ? null : text.Replace("\r\n", "\n");
    }

    /// <summary>
    /// 工具輸出裡要放進燈箱的本機圖片／影片：輸出文字，以及 content、rawOutput 的字串（不看 rawInput，避免使用者自己的 file_path）。
    /// 只收磁碟代號開頭、副檔名可顯示的絕對路徑；videos/1.mp4 這種相對路徑不收。去掉結尾標點後去重
    /// </summary>
    public static IReadOnlyList<string> ExtractDisplayMedia(string? output, JObject? update)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? candidate)
        {
            var path = CleanMediaPath(candidate);
            if (path == null || !seen.Add(path))
                return;
            if (!AgentAttachmentRules.IsLocalDrivePath(path) || !AgentAttachmentRules.IsDisplayableMedia(path))
                return;
            found.Add(path);
        }

        if (!string.IsNullOrEmpty(output))
            ScanMediaPaths(output, Add);
        WalkMedia(update?["content"], Add);
        WalkMedia(update?["rawOutput"], Add);
        return found;
    }

    /// <summary>影片工具被零資料保留擋住時的中文說明；輸出沒有那句英文就回 null。不寫入使用者的金鑰或家目錄</summary>
    public static string? DescribeZeroRetention(string? output)
    {
        if (output == null || !output.Contains(ZeroRetentionMarker, StringComparison.Ordinal))
            return null;
        return "影片生成被零資料保留（ZDR）擋住了。可以關掉 /privacy（同意保留資料），或在 %USERPROFILE%\\.grok\\managed_config.toml 設定 [tools.zdr_video_output_s3]"
            + "（見 https://docs.x.ai/build/settings/zdr-video-storage）。Myriagent 不會代存儲存桶金鑰。";
    }

    /// <summary>
    /// session/update 或 x.ai/task_backgrounded、x.ai/task_completed 的背景工作。
    /// 1.0.67 的 task_completed 把 task_id 放在 task_snapshot，頂層只有 will_wake。兩層都沒有 task id 就不認
    /// </summary>
    public static bool TryParseBackground(JObject? body, string? method, out GrokBackgroundNote note)
    {
        note = default;
        var kind = body?.Value<string>("sessionUpdate") ?? body?.Value<string>("session_update");
        if (kind is not ("task_backgrounded" or "task_completed"))
        {
            kind = method switch
            {
                "x.ai/task_backgrounded" or "task_backgrounded" => "task_backgrounded",
                "x.ai/task_completed" or "task_completed" => "task_completed",
                _ => null,
            };
        }
        if (kind == null || body == null)
            return false;
        var snapshot = body["task_snapshot"] as JObject ?? body["taskSnapshot"] as JObject;
        var taskId = First(body, "task_id", "taskId")?.Trim();
        if (string.IsNullOrEmpty(taskId))
            taskId = snapshot == null ? null : First(snapshot, "task_id", "taskId")?.Trim();
        if (string.IsNullOrEmpty(taskId))
            return false;
        var toolCallId = First(body, "tool_call_id", "toolCallId")?.Trim();
        if (string.IsNullOrEmpty(toolCallId) && snapshot != null)
            toolCallId = First(snapshot, "tool_call_id", "toolCallId")?.Trim();
        var description = First(body, "monitor_description", "monitorDescription", "description")?.Trim();
        if (string.IsNullOrEmpty(description) && snapshot != null)
            description = First(snapshot, "monitor_description", "monitorDescription", "description")?.Trim();
        bool? willWake = (body["will_wake"] ?? body["willWake"]) is JValue { Type: JTokenType.Boolean } flag ? flag.Value<bool>() : null;
        note = new GrokBackgroundNote(kind, taskId, string.IsNullOrEmpty(toolCallId) ? null : toolCallId, string.IsNullOrEmpty(description) ? null : description, willWake);
        return true;
    }

    private static void WalkMedia(JToken? token, Action<string?> add)
    {
        switch (token)
        {
            case JValue { Type: JTokenType.String } value:
                ScanMediaPaths(value.Value<string>(), add);
                break;
            case JArray array:
                foreach (var child in array)
                    WalkMedia(child, add);
                break;
            case JObject obj:
                foreach (var property in obj.Properties())
                {
                    if (property.Name.Equals("rawInput", StringComparison.Ordinal))
                        continue;
                    WalkMedia(property.Value, add);
                }
                break;
        }
    }

    private static void ScanMediaPaths(string? text, Action<string?> add)
    {
        if (string.IsNullOrEmpty(text))
            return;
        for (int i = 0; i + 3 < text.Length; i++)
        {
            if (!char.IsAsciiLetter(text[i]) || text[i + 1] != ':' || text[i + 2] is not ('\\' or '/'))
                continue;
            int end = i + 3;
            while (end < text.Length && !IsMediaPathStop(text[end]))
                end++;
            add(text[i..end]);
            i = Math.Max(i, end - 1);
        }
    }

    private static bool IsMediaPathStop(char c)
        => char.IsWhiteSpace(c) || c is '"' or '\'' or '<' or '>' or '|' or '*' or '?' or '`';

    private static string? CleanMediaPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var path = raw.Trim().Trim('"', '\'', '`').TrimEnd('.', ',', ';', ':', ')', ']', '}', '>', '!');
        return path.Length == 0 ? null : path;
    }

    /// <summary>使用者拒絕而沒執行的工具：結束更新的 _meta.toolNotRun 是 rejected（實測：status failed、文字 User rejected the execution for tool …）</summary>
    public static bool IsRejectedUpdate(JObject update)
        => (update["_meta"] as JObject)?["toolNotRun"] is JValue { Type: JTokenType.String } v && (string?)v == "rejected";

    /// <summary>指令的結束碼（rawOutput.type 是 Bash 的 exit_code）；不是指令或沒有回 null</summary>
    public static int? CommandExitCode(JObject update)
        => update["rawOutput"] is JObject raw && raw.Value<string>("type") == "Bash" && raw["exit_code"] is JValue { Type: JTokenType.Integer } code ? code.Value<int>() : null;

    /// <summary>tool_call_update 的 status → 工具卡結束的狀態；還沒結束回 null</summary>
    public static AgentItemStatus? ToolEndStatus(string? status) => status switch
    {
        "completed" => AgentItemStatus.Done,
        "failed" => AgentItemStatus.Failed,
        _ => null,
    };

    /// <summary>ACP plan 更新的 entries → 待辦清單步驟（status 是 pending／in_progress／completed）</summary>
    public static List<AgentPlanStep> ParsePlanEntries(JToken? entries)
        => (entries as JArray)?.OfType<JObject>()
            .Where(e => !string.IsNullOrWhiteSpace(e.Value<string>("content")))
            .Select(e => new AgentPlanStep
            {
                Text = e.Value<string>("content")!,
                Status = e.Value<string>("status") switch
                {
                    "in_progress" => "inProgress",
                    "completed" => "completed",
                    _ => "pending",
                },
            })
            .ToList() ?? new List<AgentPlanStep>();

    internal static string FormatTokens(long tokens) => tokens.ToString("N0", CultureInfo.InvariantCulture);
}
