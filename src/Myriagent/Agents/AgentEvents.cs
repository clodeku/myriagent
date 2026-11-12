using Myriagent.Cli;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// 代理會話（Claude／Codex 程序）回報給 AgentHub 的事件；由程序讀取執行緒發出，AgentHub 一律轉回 UI 執行緒處理。
/// 兩種 CLI 的事件經各自的解析器（ClaudeStreamParser、CodexEventParser）轉成這組共同事件，畫面不必知道是哪一種代理。
/// </summary>
internal abstract record AgentEvent;

/// <summary>Codex 持久目標的完整狀態；null＝已清除。</summary>
internal sealed record AgentCodexGoalEvent(JObject? Goal) : AgentEvent;

/// <summary>原生規劃內容；完成時以 CLI 給的全文覆蓋串流。</summary>
internal sealed record AgentCodexPlanEvent(string ItemId, string Text, bool Completed) : AgentEvent;

/// <summary>這一輪由 CLI 彙整的檔案差異。</summary>
internal sealed record AgentTurnDiffEvent(string TurnId, string Diff) : AgentEvent;

/// <summary>CLI 端的對話已建立或續接成功</summary>
internal sealed record AgentSessionStartedEvent(string SessionId, string? Model, string? Detail) : AgentEvent;

/// <summary>
/// CLI 端的工作階段名稱（Claude 的 system/session_title_changed：在 claude.ai／Claude App 改了名稱，或宿主自己改名的回送）；
/// 中樞照 SessionTitleRules 決定要不要改對話標題
/// </summary>
internal sealed record AgentSessionTitleEvent(string Title) : AgentEvent;

/// <summary>代理回覆文字的串流片段；BlockId 相同的片段屬於同一段回覆</summary>
internal sealed record AgentTextDeltaEvent(string BlockId, string Delta) : AgentEvent;

/// <summary>一段回覆完成（Text 是完整內容，取代串流累積的文字）</summary>
internal sealed record AgentTextCompletedEvent(string BlockId, string Text) : AgentEvent;

internal sealed record AgentThinkingDeltaEvent(string BlockId, string Delta) : AgentEvent;

/// <summary>代理開始呼叫工具；Diff／FilePath 給編輯類工具顯示差異與檔案連結；ArtifactMarkdown 供寫入／更新 Markdown 產物時直接預覽排版</summary>
internal sealed record AgentToolStartedEvent(
    string ToolId,
    string ToolName,
    string ToolKind,
    string Title,
    string? Detail,
    string? FilePath = null,
    IReadOnlyList<AgentDiffLine>? Diff = null,
    string? ArtifactMarkdown = null) : AgentEvent;

internal sealed record AgentToolOutputEvent(string ToolId, string Delta) : AgentEvent;

/// <summary>工具結束；MediaPaths＝工具產出、要顯示在畫面上的圖片／影片；ArtifactMarkdown＝完成後更新的 Markdown 產物全文</summary>
internal sealed record AgentToolCompletedEvent(
    string ToolId,
    AgentItemStatus Status,
    string? Output,
    IReadOnlyList<string> MediaPaths,
    string? ArtifactMarkdown = null) : AgentEvent;

/// <param name="Source">不是代理自己問的、畫在卡片標頭的來源（MCP 伺服器的 elicitation：「MCP：伺服器名稱」）；null＝代理問的</param>
internal sealed record AgentQuestionEvent(
    string RequestKey,
    IReadOnlyList<AgentQuestion> Questions,
    bool AnswerAsMessage,
    string? Detail = null,
    string? FilePath = null,
    string? Source = null) : AgentEvent;

/// <summary>代理要執行操作，等待使用者核准；IsPlan＝Claude ExitPlanMode（畫成計畫確認卡，Detail 是計畫 Markdown）</summary>
/// <param name="ToolId">要核准的工具（對到已畫好的工具卡：Claude 在完整的 assistant 區塊、Codex 在 item/started 就建卡）</param>
/// <param name="ToolStarted">
/// 那個工具已經開始執行、執行到一半才要求核准（Codex 受管網路的連線核准、擴大權限）。預設 false：工具卡代表還沒核准、還沒執行的操作，
/// CLI 重開時不算被中斷（中樞以「沒有核准、沒有執行」收尾，不跑高風險檢查）
/// </param>
internal sealed record AgentApprovalEvent(
    string RequestKey,
    string ToolName,
    string ToolKind,
    string Title,
    string? Detail,
    string? ToolId,
    bool CanAllowForSession,
    bool IsPlan = false,
    string? FilePath = null,
    IReadOnlyList<AgentDiffLine>? Diff = null,
    string? AllowSessionLabel = null,
    bool ToolStarted = false) : AgentEvent;

/// <summary>CLI 端自行撤銷了尚未回應的請求，畫面上的卡片改為已結束</summary>
internal sealed record AgentRequestResolvedEvent(string RequestKey) : AgentEvent;

/// <summary>
/// 已經切到完全自動，畫面上還在等待的這張核准卡請中樞當成使用者按了「允許」（照常經 RespondApprovalAsync 回應，卡片註明是自動允許）。
/// Codex 執行中切到完全自動時發：這一輪的核准政策在 turn/start 就定了，切換前已經跳出的卡片要由宿主放行。
/// 中樞找不到這張卡、或卡片已不在等待就略過（同一張卡可能收到兩次）
/// </summary>
internal sealed record AgentAutoApproveEvent(string RequestKey) : AgentEvent;

/// <summary>待辦清單更新（Claude TodoWrite 或 TaskCreate／TaskUpdate、Codex turn/plan/updated）</summary>
internal sealed record AgentPlanEvent(string? Explanation, IReadOnlyList<AgentPlanStep> Steps) : AgentEvent;

internal sealed record AgentMediaEvent(string Path) : AgentEvent;

internal sealed record AgentTurnCompletedEvent(AgentTurnOutcome Outcome, string? ErrorMessage, AgentUsage? Usage) : AgentEvent
{
    /// <summary>
    /// 正常結束、但 CLI 接著會自己開下一輪（Codex 的持續目標〔thread goal〕還是 active）：還沒真的完成，中樞不念完成、不通知。
    /// 下一輪以 AgentIdleTurnStartedEvent 開始
    /// </summary>
    public bool ContinuesOnItsOwn { get; init; }

    /// <summary>
    /// 這一輪是因為 CLI 程序自己結束（不是會話關的）而失敗：中樞把 running.json 多留一陣子（AgentHub.InterruptedTurns.cs 的 RunMarkerHold）——
    /// Windows 登出、關機時先結束主控台程序，app 接著才被結束，這一輪其實是被關機中斷，重新開機後要照常接續
    /// </summary>
    public bool ProcessExited { get; init; }

    /// <summary>
    /// 失敗的原因是 API 伺服器暫時無法服務或網路連線瞬斷（Claude 的 529 Overloaded、Antigravity 的 503 UNAVAILABLE 與 API 網路連線中斷）：
    /// 中樞等一下自動重試（OverloadRetryRules）。只有 Outcome 是 Failed 時才不是 None
    /// </summary>
    public AgentServerBusy ServerBusy { get; init; }
}

/// <summary>一輪失敗的原因是不是可自動重試的 API 暫時無法服務或網路瞬斷、是哪一種（中樞依種類決定等待時間、提示說法與連續自動重試上限，OverloadRetryRules）</summary>
internal enum AgentServerBusy
{
    /// <summary>不是（其他錯誤、或沒有失敗）</summary>
    None,

    /// <summary>Claude 的 529 Overloaded：CLI 自己的重試用完了（ClaudeStreamRules.IsOverloadedApiError）</summary>
    Overloaded,

    /// <summary>Antigravity 的 503 UNAVAILABLE「The service is currently unavailable」（AntigravityStreamRules.IsServiceUnavailable）</summary>
    Unavailable,

    /// <summary>Antigravity 呼叫 API 時底層網路連線中斷（wsasend／wsarecv／EOF／connection reset 等，AntigravityStreamRules.IsTransientNetworkError）</summary>
    NetworkError,
}

/// <summary>
/// 剛用掉的 token（增量：側欄帳戶卡的 token 計數 TokenTallyRules、每週額度每 5% 的分段紀錄 UsageSegmentRules）：CLI 一回報 API 呼叫的用量就發——
/// Claude 主對話的 message_start／message_delta、子代理逐字稿（ClaudeSubagentUsageTail）、一輪結束時 modelUsage 比已記的多的部分（壓縮等看不到的呼叫）；
/// Codex 的 thread/tokenUsage/updated（含子代理的 thread）；Antigravity 步驟的 usage；Grok 一輪結束時 session/prompt 的 usage（只有那時才有）。
/// <b>每一筆用量只由會話發一次</b>：中樞只累加這些增量，不再以 AgentTurnCompletedEvent.Usage 補差額（2026-10-25：中樞先清掉這一輪的計數、之後 CLI 才回報整輪用量時，
/// 整輪主對話的用量被再記一次，Codex 一天多記 15%）。CostUsd 一律 null。
/// <paramref name="Model"/> 是這筆用量的模型（CLI 回報的 id，例 claude-opus-5-5、gpt-6-astra；不知道是 null，中樞改用對話的模型）；
/// <paramref name="CacheWriteTokens"/> 是其中的快取寫入（含在 Delta.InputTokens 裡；只有 Claude 回報，其他是 0）
/// </summary>
internal sealed record AgentTokenUsageEvent(AgentUsage Delta, string? Model = null, long CacheWriteTokens = 0) : AgentEvent;

/// <summary>
/// 上下文用量（只供顯示：輸入框旁的上下文提示，ContextUsageRules；實測見 docs/protocol-notes.md「上下文用量」）。只在主對話發，欄位是 null＝這次沒有這一項
/// （中樞沿用之前的值）。UsedTokens＝最近一次模型呼叫送進去的 token（含快取）加上它的輸出，也就是下一次呼叫的起點；WindowTokens＝目前模型的上下文視窗；
/// AutoCompactTokens＝CLI 會自動壓縮的門檻（只有 Claude 回報）。Compacted＝剛壓縮過：之前的 UsedTokens 不再準，清掉等下一次呼叫
/// （Codex 壓縮後直接給新的 UsedTokens，不用這個）
/// </summary>
internal sealed record AgentContextUsageEvent(long? UsedTokens, long? WindowTokens, long? AutoCompactTokens = null, bool Compacted = false) : AgentEvent;

/// <summary>
/// Claude 回報的快取過期後整份重送的額度成本（get_context_usage 的 coldResumeQuota，每一輪之後問一次；2026-11-11）。
/// Quota 是 null＝這次的回應沒有（不知道價格，或閒置壓縮已經寫好摘要：快取過期之後送的是摘要）
/// </summary>
internal sealed record AgentColdResumeQuotaEvent(ColdResumeQuota? Quota) : AgentEvent;

/// <summary>
/// 對話程序回報的額度資訊（原始 JSON）：Claude 是 rate_limit_event.rate_limit_info、Codex 是 account/rateLimits/updated 的 rateLimits。
/// 解析與合併由 Usage 模組負責（UsageRules），會話層不必懂時間窗的意義。
/// </summary>
internal sealed record AgentRateLimitEvent(AgentProvider Provider, JObject Raw) : AgentEvent;

/// <summary>
/// 這一輪因為帳戶的訂閱額度用完而失敗（不是暫時性的 429）：Claude 是合成的 assistant 訊息帶 apiError usage_limit_reached
/// （或同一輪 rate_limit_event 被拒、接著以錯誤結束），Codex 是 error／turn 的 codexErrorInfo usageLimitExceeded。
/// 只在主對話發，一輪最多一次、在 TurnCompleted 之前；中樞在這一輪結束時決定要不要自動換帳戶繼續（AccountSwitchRules）。
/// ResetsAt＝額度何時重置（不明為 null）
/// </summary>
internal sealed record AgentQuotaExhaustedEvent(AgentProvider Provider, DateTimeOffset? ResetsAt) : AgentEvent;

internal sealed record AgentNoticeEvent(string Message, bool IsError) : AgentEvent;

/// <summary>Claude 子代理（Agent 工具）的即時用量（task_progress／task_notification）：中樞放在標頭的 subagents，網頁畫在那張工具卡上</summary>
internal sealed record AgentSubagentUsageEvent(SubagentUsage Usage) : AgentEvent;

/// <summary>CLI 回報可選的模型清單（Claude initialize、Codex model/list）</summary>
internal sealed record AgentModelsEvent(IReadOnlyList<AgentModelOption> Models) : AgentEvent;

/// <summary>CLI 回報可用的「/」指令（Claude initialize 的 commands）</summary>
internal sealed record AgentCommandsEvent(IReadOnlyList<AgentSlashCommand> Commands) : AgentEvent;

/// <summary>Claude 的自訂代理與輸出風格（initialize 回應的 agents、available_output_styles）：中樞存成這個帳戶的清單</summary>
internal sealed record AgentCustomizationsEvent(IReadOnlyList<AgentNamedOption> Agents, IReadOnlyList<string> OutputStyles) : AgentEvent;

/// <summary>
/// 一個 MCP 伺服器的狀態（Claude 的 mcp_status）：Status 是 connected、failed、needs-auth、pending、disabled（原樣）；
/// Error 是連不上的原因、Transport 是 stdio／http／sse／claudeai-proxy…、Source 是設定從哪裡來（user、plugin、claudeai、project、local、dynamic…）、
/// Tools 是工具數（連上才有）
/// </summary>
internal sealed record AgentMcpServer(string Name, string Status, string? Error, string? Transport, string? Source, int? Tools);

/// <summary>MCP 狀態的查詢結果：Servers 是 null 時 Error 是查不到的原因</summary>
internal sealed record AgentMcpStatusResult(IReadOnlyList<AgentMcpServer>? Servers, string? Error);

/// <summary>
/// MCP 伺服器的操作（web-protocol 的 mcpAction，儀表板以帳戶為單位）。沒有啟用／停用：CLI 的 mcp_toggle 把停用記在
/// .claude.json 的 projects[工作資料夾].disabledMcpServers，是逐資料夾的設定，不是帳戶的（實測，protocol-notes）
/// </summary>
internal enum AgentMcpAction
{
    Reconnect,
    Authenticate,
}

/// <summary>MCP 操作的結果：失敗時 Error 是給使用者看的原因；授權時 AuthUrl 是要在瀏覽器開的網址（null＝不用使用者做什麼）</summary>
internal sealed record AgentMcpActionResult(bool Ok, string? Error, string? AuthUrl = null);

/// <summary>旁支提問的結果：Text 是答案（Markdown），失敗時 Text 是 null、Error 是原因</summary>
internal sealed record AgentSideAnswer(string? Text, string? Error);

/// <summary>CLI 回報登入的帳戶資訊（Claude initialize 的 account）</summary>
internal sealed record AgentAccountInfoEvent(string? Email, string? Plan) : AgentEvent;

/// <summary>權限模式在 CLI 端被改變（init／status 回報、計畫確認後或「本對話都允許」切到自動接受編輯）</summary>
internal sealed record AgentPermissionModeChangedEvent(AgentPermissionMode Mode) : AgentEvent;

/// <summary>
/// Claude 的 Remote Control 已開啟（control_request remote_control 成功）：可在 claude.ai/code 或 Claude App 接手這個對話。
/// 中樞記下 BridgeSessionId，程序重開時帶回（reattach_session_id），手機上看到的一直是同一個遠端工作階段
/// </summary>
/// <param name="BridgeSessionId">遠端工作階段的 id（cse_…）</param>
/// <param name="SessionUrl">在 claude.ai 開啟這個工作階段的網址</param>
internal sealed record AgentRemoteControlEvent(string BridgeSessionId, string? SessionUrl) : AgentEvent;

/// <summary>
/// Claude 的 Remote Control 開不起來、或斷線且不會自己恢復（會話對每個對話只發一次）：中樞在每個帳戶的第一個對話加提示，
/// 帳戶本身不支援時才不會每個對話都多一則
/// </summary>
internal sealed record AgentRemoteControlFailedEvent(string Message) : AgentEvent;

/// <summary>
/// 使用者從 claude.ai 或 Claude App（Remote Control）送來一則訊息，CLI 開始處理它：中樞把它加成使用者訊息（標示從遠端送出）
/// </summary>
/// <param name="Text">訊息文字</param>
/// <param name="ImageCount">附帶的圖片數（圖片不搬到本機，只提示張數）</param>
/// <param name="FileCount">附帶的其他檔案數（文件等；同樣只提示數量）</param>
/// <param name="StartsTurn">
/// 這則訊息開始了新的一輪（閒置時送來）：會話已把它當成執行中的一輪（可以停止、程序意外結束時回報失敗），中樞跟著標成執行中。
/// false＝併進正在進行的一輪（CLI 在工具結果之後把排隊的訊息一起交給模型），或會話正在重開
/// </param>
internal sealed record AgentRemoteUserMessageEvent(string Text, int ImageCount, int FileCount, bool StartsTurn) : AgentEvent;

/// <summary>
/// 存活的背景工作改變了（Claude 的 background_tasks_changed：run_in_background 的指令、背景子代理；數量或對到的工具變了才發，程序結束或關閉時發一次 0）。
/// 中樞據此在畫面上顯示「代理這一輪已結束、在等背景工作回報」（標頭與清單的 background）
/// </summary>
/// <param name="Count">存活的背景工作數</param>
/// <param name="Tools">開始它們的工具（對不到工具的不列，所以可能比 Count 少）</param>
/// <param name="ToolIds">
/// 還在跑、卡片要留在執行中的工具 id（Codex：一輪結束時還沒結束的指令，之後的 AgentToolCompletedEvent 才結束它；null＝沒有，
/// Claude 的背景工作開始時工具就已經有結果）。在 TurnCompleted 之前發，中樞結束這一輪時據此不把那些卡片標成完成
/// </param>
internal sealed record AgentBackgroundWorkEvent(int Count, IReadOnlyList<AgentBackgroundTool> Tools, IReadOnlyList<string>? ToolIds = null) : AgentEvent;

/// <summary>
/// 背景指令跑完了（Codex：一輪結束後才送來的 item/completed；Codex 不會自己開一輪告訴模型）：中樞在閒置時隱藏送出接續訊息，
/// 執行中就等這一輪結束再送（比照 Claude 的 CLI 自己開一輪，E-11）。工具卡的結束照常以前面的 AgentToolCompletedEvent 處理
/// </summary>
/// <param name="ToolId">那張工具卡</param>
/// <param name="Title">指令的一行摘要</param>
/// <param name="Status">結束的狀態（Done／Failed／Declined）</param>
/// <param name="ExitCode">結束碼（不明是 null）</param>
/// <param name="Duration">執行了多久（不明是 null）</param>
/// <param name="StoppedByUser">使用者按了停止背景工作（StopBackgroundWorkAsync）才結束的：中樞不接續（Claude 的 stop_task 也不會讓 CLI 開一輪）</param>
internal sealed record AgentBackgroundCommandFinishedEvent(string ToolId, string? Title, AgentItemStatus Status, int? ExitCode, TimeSpan? Duration, bool StoppedByUser = false) : AgentEvent;

/// <summary>
/// Claude 的工作開始或結束（system 的 task_started／task_notification；前景與背景的指令、子代理〔task_type local_agent〕都有）。
/// 只給背景活動日誌記錄（docs/background-journal.md），中樞不據此改畫面：背景工作的清單照 AgentBackgroundWorkEvent
/// </summary>
/// <param name="TaskId">CLI 的 task_id</param>
/// <param name="Ended">false＝task_started；true＝task_notification</param>
/// <param name="TaskType">local_bash、local_agent、monitor…；不明是 null</param>
/// <param name="Description">開始時的說明；結束時是 null</param>
/// <param name="ToolUseId">開始它的工具卡</param>
/// <param name="Backgrounded">開始時：一開始就在背景（is_backgrounded）；結束時：是不是背景工作（前景的指令與子代理結束時是 false）</param>
/// <param name="Status">結束的狀態（completed／stopped／failed／killed）；開始時是 null</param>
/// <param name="Summary">結束的摘要（重開時被丟下的是 stopped＋「Orphaned by…」）</param>
internal sealed record AgentTaskLifecycleEvent(string TaskId, bool Ended, string? TaskType, string? Description, string? ToolUseId, bool? Backgrounded,
    string? Status, string? Summary) : AgentEvent;

/// <summary>CLI 程序結束（正常關閉或崩潰）</summary>
/// <param name="ProcessInstance">結束的程序實例（AgentProcessStartedEvent 發過的）：中樞把這個實例的 Waiting 卡片作廢；null＝不明</param>
internal sealed record AgentProcessExitedEvent(int? ExitCode, string? StderrTail, bool Expected, string? ProcessInstance = null) : AgentEvent;

// ───────────────────────────── CLI 熱更新 ─────────────────────────────

/// <summary>
/// 會話啟動了一個新的 CLI 程序（任何原因：第一次送出、設定改變、續接失敗改開新的、RestartAsync）。
/// 在開始讀那個程序的輸出之前發出，保證排在那個程序的任何事件之前：中樞據此把這個對話所有 ProcessInstance 不是這個實例的
/// Waiting 卡片（Approval／Question／PlanApproval，AnswerAsMessage 除外）作廢，之後建立的卡片寫上這個實例
/// </summary>
/// <param name="ProcessInstance">新程序的實例（AgentProcess.InstanceId）</param>
/// <param name="Cli">啟動用的執行檔身分（AgentSessionSettings.Cli）；不明為 null</param>
internal sealed record AgentProcessStartedEvent(string ProcessInstance, CliBinaryIdentity? Cli) : AgentEvent;

/// <summary>
/// Claude 的持續目標（/goal）：一輪結束後讀逐字稿尾端最後一筆 goal_status 的結果（ClaudeGoalRules）。Condition 是還沒達成的目標，
/// null＝沒有目標（達成、清除）。目標只活在程序裡：程序開始、結束時中樞自己清掉
/// </summary>
internal sealed record AgentGoalEvent(string? Condition) : AgentEvent;

/// <summary>
/// CLI 程序回報了自己的版本（Claude：system/init 的 claude_code_version；Codex：initialize 回應的 userAgent）。
/// 同一個程序只在第一次回報或版本改變時發；中樞記進 AgentConversation.LastCliVersion，並據此判斷對話是不是還在跑舊版
/// </summary>
internal sealed record AgentRunningVersionEvent(string ProcessInstance, string Version) : AgentEvent;

/// <summary>安全點狀態改變（三個旗標的組合改變時才發；定義見 IAgentSession.IsAtSafePoint）</summary>
/// <param name="ToolRunning">有工具在執行（Claude：tool_use 開始到 tool_result；Codex：item/started 到 item/completed）</param>
/// <param name="WaitingForUser">有未回應的請求（核准、問卷、計畫確認）</param>
/// <param name="HasLiveBackgroundWork">有存活的背景工作（Claude run_in_background）</param>
internal sealed record AgentSafePointChangedEvent(bool ToolRunning, bool WaitingForUser, bool HasLiveBackgroundWork) : AgentEvent
{
    public bool IsAtSafePoint => !ToolRunning && !WaitingForUser && !HasLiveBackgroundWork;
}

/// <summary>不是由 SendAsync 開始的一輪是怎麼開始的</summary>
internal enum AgentAutoTurnKind
{
    /// <summary>
    /// Claude 新程序依 CLAUDE_CODE_RESUME_INTERRUPTED_TURN 自動接續被打斷的一輪。那一輪的 result（與同一輪的 assistant 訊息）帶
    /// resume_reason=myriagent_cli_update：app 以 CLAUDE_CODE_RESUME_REASON 指定（ClaudeStreamRules.AppResumeReason），取代 CLI 預設的 interrupted_turn
    /// </summary>
    ResumeInterruptedTurn,

    /// <summary>Codex 重開後由會話以 turn/start 送出接續訊息（不顯示成使用者訊息）；Claude 的備案（新程序沒有自動接續，改經 stdin 送接續訊息）也是這個</summary>
    Continuation,
}

/// <summary>
/// 不是由 SendAsync 開始的一輪開始了（RestartAsync 的自動接續）：會話已把自己標成執行中，這一輪照常以 AgentTurnCompletedEvent 結束。
/// 中樞收到時對話還不在執行中就改成執行中，狀態文字「正在以新版重新啟動…」。Claude 新程序一啟動就發（接續的一輪比 initialize 回應還早開始），
/// CLI 這時還不一定會接續：「已用新版自動接續」（標頭 continued、Notice）等 RestartAsync 回 RestartedAndContinued 才確認——Claude 會話以
/// command_lifecycle started、這一輪的活動或帶 resume_reason=myriagent_cli_update 的 result 判定 CLI 已接續；沒有接續時回 Restarted，中樞在本機收尾
/// </summary>
internal sealed record AgentTurnStartedByCliEvent(AgentAutoTurnKind Kind) : AgentEvent;

/// <summary>
/// 閒置時 CLI 自己開始了一輪（Claude：背景工作完成後 CLI 開一輪告訴模型〔E-11〕；從 claude.ai 或 Claude App 送來的訊息在回送之前也先看到這一輪的
/// system/init。Codex：持續目標〔thread goal〕的下一輪，閒置時收到主對話的 turn/started）：會話已把自己標成執行中，這一輪照常以
/// AgentTurnCompletedEvent 結束。中樞收到時對話還不在執行中就改成執行中——轉圈、可以停止、
/// app 送出的插入或排進佇列、換版等它結束；已在執行中（app 的送出比這個事件先到，會話已讓它接手）就不動
/// </summary>
/// <param name="AfterBackgroundTask">這一輪之前剛收到背景工作完成的通知（task_notification completed）：狀態文字照它說明</param>
/// <param name="ForThreadGoal">Codex 的持續目標還在進行時開始的（目標的下一輪）：狀態文字照它說明</param>
internal sealed record AgentIdleTurnStartedEvent(bool AfterBackgroundTask, bool ForThreadGoal = false) : AgentEvent;
