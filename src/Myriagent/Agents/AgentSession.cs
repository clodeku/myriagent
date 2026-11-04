using Myriagent.Cli;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>這台電腦沒有可用的代理 CLI（未安裝）；畫面收到後引導到設定的 CLI 區</summary>
internal sealed class AgentCliUnavailableException : Exception
{
    public AgentCliUnavailableException(AgentProvider provider, string message) : base(message)
    {
        Provider = provider;
    }

    public AgentProvider Provider { get; }
}

/// <summary>
/// 這個對話正在以新版 CLI 重新啟動（RestartAsync 進行中），SendAsync 不能開始新的一輪（Codex 會話丟這個；Claude 會話改成等重開結束再送）。
/// 中樞在重開期間本來就不呼叫 SendAsync（送出改排進佇列，重開做完後照常送出）；萬一收到，把訊息放回佇列最前面、不當成這一輪失敗
/// </summary>
internal sealed class AgentRestartInProgressException : InvalidOperationException
{
    public const string DefaultMessage = "正在以新版 CLI 重新啟動這個對話，請稍候再送出";

    public AgentRestartInProgressException() : base(DefaultMessage)
    {
    }
}

/// <summary>送出一則訊息時的對話設定（每次送出都帶最新值，session 自行判斷要不要重開程序）</summary>
/// <param name="SessionId">Claude 的 session UUID／Codex 的 thread id（Codex 新對話為 null）</param>
/// <param name="SessionStarted">CLI 端已建立過這個 session（續接）</param>
/// <param name="Launcher">CLI 執行檔；null＝這台電腦沒有安裝</param>
/// <param name="AccountKey">帳戶識別（帳戶或其設定資料夾改變時要重開程序）</param>
/// <param name="ConfigureEnvironment">子程序的環境變數（帳戶設定資料夾、剝除 API Key…）</param>
/// <param name="CodexHome">Codex 帳戶的 CODEX_HOME（讀 config.toml 找 MCP 伺服器名稱）；null＝系統預設</param>
/// <param name="ConversationDirectory">對話資料夾（附件放這裡；Claude 以 --add-dir 開放、Codex 列為可寫入根目錄）</param>
/// <param name="Instructions">給代理的背景說明（只在建立對話時生效）</param>
/// <param name="UseLocalCustomizations">載入使用者的外掛、連接器、MCP 與權限設定；false＝精簡模式</param>
/// <param name="HistoryRecap">續接失敗、改開新 session 時附在訊息前的前文節錄；沒有前文為 null</param>
/// <param name="Cli">
/// 這次要用的 CLI 執行檔身分（CliManager.GetIdentity）：與目前程序啟動時的身分不是同一份檔案
/// （CliBinaryIdentity.SameOrUnknown 為 false）就要重開程序；null＝身分不明（不因此重開）
/// </param>
/// <param name="RemoteControl">
/// 程序啟動時開啟 Remote Control（只有 Claude 支援，其他代理忽略）；null＝不開。改變它不重開程序，
/// 要立即套用到活著的程序用 IAgentSession.SetRemoteControlAsync
/// </param>
/// <param name="Title">
/// 對話標題：Claude 每次啟動程序與 Remote Control 連上時以它改工作階段名稱（rename_session），claude.ai／Claude App 上的名稱跟著對話標題；
/// null＝不改（其他代理忽略）。之後的改名用 IAgentSession.RenameAsync
/// </param>
/// <param name="PrepareShared">
/// 隔離帳戶共用系統預設帳戶的設定（Accounts.SharedCustomizations）：每次啟動程序前在背景執行緒以工作資料夾呼叫，回傳要多帶的啟動參數；
/// null＝不共用。開關改變時 AccountKey 也跟著變（程序會重開）
/// </param>
/// <param name="ForkSource">
/// 延伸出來的 Claude 對話（Hub/ConversationForkRules）第一次啟動要分支的來源逐字稿（原對話當下的副本）：SessionStarted 時以
/// <c>--resume &lt;它&gt; --fork-session --session-id &lt;SessionId&gt;</c> 啟動；新的 session 開始後中樞清掉。null＝照常（其他代理忽略）
/// </param>
/// <param name="DelegationMcpPipeName">
/// 帳戶委派（docs/account-delegation.md）：使用者 @ 過帳戶的對話掛上 myriagent_accounts MCP，橋接程序連這個命名管道；
/// 改變時重開程序（Claude 的 NeedsRestart、Codex 的程序簽章）。null＝不掛（只有 Claude、Codex 會用）
/// </param>
/// <param name="AutoCompactTokens">
/// 自動壓縮上限（token，AutoCompactRules）：Claude 以 --settings 的 env 帶 CLAUDE_CODE_AUTO_COMPACT_WINDOW（上限＋緩衝）、
/// Codex 以「-c model_auto_compact_token_limit」帶；改變時重開程序。null＝CLI 預設（其他代理忽略）
/// </param>
/// <param name="AutoCompactPercent">
/// 自動壓縮門檻（上下文視窗的百分比，AutoCompactRules）：Grok 以環境變數 GROK_AUTO_COMPACT_THRESHOLD_PERCENT 帶；改變時重開程序。
/// null＝CLI 預設（其他代理忽略）
/// </param>
internal sealed record AgentSessionSettings(
    string? SessionId,
    bool SessionStarted,
    AgentLauncher? Launcher,
    string AccountKey,
    Action<IDictionary<string, string?>> ConfigureEnvironment,
    string? CodexHome,
    string? Model,
    string? Effort,
    AgentPermissionMode PermissionMode,
    string WorkingDirectory,
    string ConversationDirectory,
    string Instructions,
    bool UseLocalCustomizations,
    string? HistoryRecap = null,
    CliBinaryIdentity? Cli = null,
    AgentRemoteControlOptions? RemoteControl = null,
    string? Title = null,
    Func<string, AgentSharedLaunch>? PrepareShared = null,
    string? Agent = null,
    string? FallbackModel = null,
    string? OutputStyle = null,
    string? ResumeAt = null,
    string? ForkSource = null,
    string? CodexServiceTier = null,
    IReadOnlyList<AgentSkillSelection>? CodexSkills = null,
    string? DelegationMcpPipeName = null,
    long? AutoCompactTokens = null,
    int? AutoCompactPercent = null)
{
    /// <summary>啟動程序前取得共用設定的啟動參數（背景執行緒；不共用回 None）</summary>
    public async Task<AgentSharedLaunch> PrepareSharedAsync()
        => PrepareShared == null ? AgentSharedLaunch.None : await Task.Run(() => PrepareShared(WorkingDirectory));
}

/// <summary>隔離帳戶共用系統預設帳戶的設定時，啟動 CLI 要多帶的參數（Accounts.SharedCustomizations.Prepare 產生）</summary>
/// <param name="ClaudeMemoryDirectory">Claude 的自動記憶資料夾（--settings 的 autoMemoryDirectory）；null＝照 CLI 預設</param>
/// <param name="ClaudeMcpConfigFile">Claude 的 --mcp-config 設定檔；null＝沒有要共用的 MCP</param>
/// <param name="CodexConfigOverrides">Codex app-server 的「-c」值（共用的 MCP 伺服器）</param>
internal sealed record AgentSharedLaunch(string? ClaudeMemoryDirectory, string? ClaudeMcpConfigFile, IReadOnlyList<string> CodexConfigOverrides)
{
    public static readonly AgentSharedLaunch None = new(null, null, Array.Empty<string>());
}

/// <summary>開啟 Claude Remote Control 的選項（control_request remote_control）</summary>
/// <param name="Name">遠端工作階段的名稱（claude.ai／Claude App 的清單上顯示；對話標題）；null＝CLI 自己命名</param>
/// <param name="ReattachSessionId">
/// 上次的遠端工作階段 id（AgentRemoteControlEvent.BridgeSessionId）：接回同一個工作階段；null＝開新的。
/// 實測 2.1.300 接回不存在的 id 不報錯、直接開新的（回應帶新的 id）
/// </param>
internal sealed record AgentRemoteControlOptions(string? Name, string? ReattachSessionId);

/// <summary>為什麼要以新版 CLI 重開程序</summary>
internal enum AgentRestartReason
{
    /// <summary>CLI 換版：偵測到已穩定的新身分（CliManager.VersionChanged），或 npm 更新屏障（ICliUpdateParticipant）</summary>
    CliUpdated,

    /// <summary>使用者按了「立即套用 CLI 更新」或更新屏障的「現在中斷」</summary>
    UserApplyNow,

    /// <summary>Clode Myriagent 本身要換版重啟（設計 9.2）：所有代理種類一起排空，停下後由新版 app 接續</summary>
    AppUpdate,
}

/// <summary>什麼時候可以重開</summary>
internal enum AgentRestartMode
{
    /// <summary>只在安全點重開（會話在鎖內重新確認 IsAtSafePoint）；不在安全點就什麼都不做、回 Deferred，中樞等下一個 AgentSafePointChangedEvent 再試</summary>
    SafePoint,

    /// <summary>不等安全點，立刻重開：執行中的工具被中斷（結果未知），未回應的請求作廢（CLI 接續後會以新的 request id 重新要求）</summary>
    Immediate,
}

/// <summary>重開時被打斷的工具（中樞依自己的工具卡填，給接續訊息與紀錄用）</summary>
/// <param name="ToolName">顯示名稱（執行指令、修改檔案…）</param>
/// <param name="Title">一行摘要（指令、檔案路徑）</param>
/// <param name="Elapsed">已執行多久</param>
internal sealed record AgentInterruptedTool(string ToolName, string? Title, TimeSpan Elapsed);

/// <summary>開始一個存活背景工作的工具（說明同那張工具卡）：中樞在「立即套用」打斷背景工作時以它判斷高風險操作（設計 7.7）</summary>
/// <param name="ToolName">顯示名稱（執行指令、子代理…）</param>
/// <param name="ToolKind">工具種類（bash、agent…）</param>
/// <param name="Title">一行摘要（指令的第一行）</param>
/// <param name="Detail">展開細節（說明與完整指令）</param>
/// <param name="TaskId">CLI 的背景工作 id（Claude 的背景指令與 Monitor：可以 get_task_output 看輸出；其他是 null）</param>
/// <param name="StopId">
/// 單獨停止它的 id（IAgentSession.StopBackgroundWorkAsync；Claude 是 task_id、Codex 是工具卡 id）；null＝只能和其他背景工作一起停止（Antigravity、Grok）
/// </param>
internal sealed record AgentBackgroundTool(string ToolName, string? ToolKind, string? Title, string? Detail, string? TaskId = null, string? StopId = null);

/// <summary>停止背景工作的結果（IAgentSession.StopBackgroundWorkAsync）：停了幾個；Error 不是 null＝有沒停掉的（原因給使用者看）</summary>
internal sealed record AgentBackgroundStopResult(int Stopped, string? Error);

/// <summary>
/// 要求會話以新版 CLI 重開程序（IAgentSession.RestartAsync）。會話依 Settings 與自己的狀態決定做到哪一步：
/// <list type="bullet">
/// <item>Mode=SafePoint 且不在安全點（含閒置但有背景工作）：什麼都不做，回 Deferred</item>
/// <item>沒有活著的程序、也沒有停下待接續的一輪：什麼都不做，回 Restarted（下次送出自然用新版）</item>
/// <item>停下舊程序（Claude：不送 interrupt，直接結束程序樹；Codex：turn/interrupt、等 turn/completed、關 stdin，逾時才結束程序樹），
/// 以 ProcessTreeSnapshot 確認子孫都已結束，再以 TranscriptTailRules 截掉寫到一半的尾行</item>
/// <item>Settings=null：到此為止，回 Stopped；這一輪若正在執行，會話記住「停下待接續」，等中樞之後再以 Settings 呼叫一次</item>
/// <item>這一輪不在執行中（也沒有停下待接續的）、或 AutoContinue=false、或重開途中收到 InterruptAsync：不啟動新程序，回 Restarted（被打斷的那一輪由中樞收尾）</item>
/// <item>否則以 Settings 啟動新程序並自動接續（Claude：--resume＋CLAUDE_CODE_RESUME_INTERRUPTED_TURN／_MAX_AGE_MS／_PROMPT／_REASON，
/// 接續那一輪的 resume_reason 是 myriagent_cli_update；Codex：thread/resume 後 turn/start 送 ContinuationText），發 AgentTurnStartedByCliEvent，
/// 確定已接續才回 RestartedAndContinued</item>
/// </list>
/// 被打斷的那一輪不發 AgentTurnCompletedEvent（舊程序的結束、Codex 的 interrupted 都不能被當成這一輪結束）；
/// 接續的一輪照常以 AgentTurnCompletedEvent 結束。回 Restarted／Stopped／Failed 時，被打斷的那一輪由中樞收尾。
/// </summary>
/// <param name="FromVersion">換版前的版本（顯示與紀錄用）</param>
/// <param name="ToVersion">換版後的版本</param>
/// <param name="AutoContinue">被打斷的一輪要不要自動接續（中樞依政策與「不自動接續」的情況決定）</param>
/// <param name="ContinuationText">
/// 接續訊息（中樞以 ContinuationRules 組；Claude 放進 CLAUDE_CODE_RESUME_PROMPT、Codex 當 turn/start 的文字，都不顯示成使用者訊息）；
/// null＝用 DefaultContinuationText
/// </param>
/// <param name="InterruptedTools">被打斷的工具（安全點重開時為空）</param>
/// <param name="Settings">新程序的設定（中樞以目前的帳戶、資料夾、模型與新的 Launcher、Cli 身分重建）；null＝只停下舊程序、不啟動新程序（npm 更新屏障的第一段）</param>
internal sealed record AgentRestartRequest(
    AgentRestartReason Reason,
    AgentRestartMode Mode,
    string? FromVersion,
    string? ToVersion,
    bool AutoContinue,
    string? ContinuationText,
    IReadOnlyList<AgentInterruptedTool> InterruptedTools,
    AgentSessionSettings? Settings)
{
    /// <summary>中樞沒有給接續訊息時的預設（照官方預設文字的意思，明說是系統重啟而不是使用者拒絕）</summary>
    public const string DefaultContinuationText =
        "（系統通知：Clode Myriagent 已更新 CLI 並重新啟動，這不是使用者拒絕或中斷。請從中斷處繼續原本的工作；"
        + "被打斷的步驟結果未知，請先確認它是否已生效再決定要不要重跑，已完成的步驟不要重做。使用者在重啟後尚未傳送新訊息。）";

    /// <summary>實際要送的接續訊息</summary>
    public string EffectiveContinuationText => string.IsNullOrWhiteSpace(ContinuationText) ? DefaultContinuationText : ContinuationText;
}

/// <summary>RestartAsync 的結果</summary>
internal enum AgentRestartOutcome
{
    /// <summary>
    /// 已以新版重開，並確定開始接續被打斷的那一輪（已發 AgentTurnStartedByCliEvent）：Claude 是 CLI 開始自動接續（command_lifecycle started、
    /// 這一輪的活動或 resume_reason=myriagent_cli_update 的 result），或改經 stdin 送了接續訊息；Codex 是接續訊息的 turn/start 被接受。
    /// 中樞據此顯示「已用新版自動接續」；接續的一輪可能在這個結果之前就結束（TurnCompleted 先到）
    /// </summary>
    RestartedAndContinued,

    /// <summary>舊程序已停（或本來就沒有），沒有接續、也沒有啟動新程序：下次送出自然用新版</summary>
    Restarted,

    /// <summary>舊程序已停、沒有啟動新程序（Settings=null）；會話記住停下待接續的一輪，之後再以 Settings 呼叫一次完成重開與接續</summary>
    Stopped,

    /// <summary>SafePoint 模式但目前不在安全點（或另一個 RestartAsync 進行中）：什麼都沒做</summary>
    Deferred,

    /// <summary>失敗（Error 是原因）：會話回到閒置、沒有程序，被打斷的那一輪由中樞收尾</summary>
    Failed,

    /// <summary>這個會話還不支援（熱更新實作前的暫時行為）：什麼都沒做</summary>
    NotSupported,
}

/// <param name="PreviousProcessInstance">被停下的舊程序實例（中樞據此作廢它的 Waiting 卡片）；本來就沒有程序為 null</param>
/// <param name="ProcessInstance">新程序的實例；沒有啟動新程序為 null</param>
/// <param name="Error">Failed 的原因（給使用者看）</param>
internal sealed record AgentRestartResult(
    AgentRestartOutcome Outcome,
    string? PreviousProcessInstance = null,
    string? ProcessInstance = null,
    string? Error = null)
{
    /// <summary>熱更新實作前的暫時結果</summary>
    public static AgentRestartResult Unsupported { get; } = new(AgentRestartOutcome.NotSupported, Error: "這個對話的會話還不支援以新版 CLI 重開");
}

/// <summary>一個對話目前程序的燈號來源：根程序的 PID、程序實例（換程序時重新取基準）、app 與它之間管線的位元組累計</summary>
internal readonly record struct AgentActivitySource(int ProcessId, string Instance, long PipeBytes);

/// <summary>
/// 一個對話的代理程序（Claude 或 Codex）；事件在背景執行緒發出。
/// 屬性可在任何執行緒讀取（實作以鎖保護、回傳快照）；方法由中樞在 UI 執行緒呼叫，不能阻塞呼叫端
/// </summary>
internal interface IAgentSession : IDisposable
{
    AgentProvider Provider { get; }

    /// <summary>
    /// 一輪正在進行：呼叫 SendAsync 起（含啟動程序的準備期間），或 RestartAsync 自動接續的一輪開始起（AgentTurnStartedByCliEvent），
    /// 或閒置時 CLI 自己開的一輪開始起（Claude：背景工作完成的通知、遠端送來的訊息；Codex：持續目標的下一輪；
    /// AgentIdleTurnStartedEvent、AgentRemoteUserMessageEvent），
    /// 到 TurnCompleted 為止
    /// </summary>
    bool IsRunning { get; }

    /// <summary>程序還活著（閒置回收用）</summary>
    bool HasLiveProcess { get; }

    /// <summary>
    /// 目前程序回報的 CLI 版本（Claude：system/init 的 claude_code_version；Codex：initialize 回應 userAgent 裡的版本，CliVersionRules.ParseCodexUserAgent）；
    /// 沒有程序或還沒回報為 null。回報（或改變）時另發 AgentRunningVersionEvent
    /// </summary>
    string? RunningVersion { get; }

    /// <summary>
    /// 目前程序的實例 id（AgentProcess.InstanceId，Guid N 格式）；沒有程序為 null。每次啟動新程序都換新的，
    /// 並在讀那個程序的輸出之前發 AgentProcessStartedEvent。核准卡的有效範圍就是產生它的程序實例
    /// </summary>
    string? ProcessInstance { get; }

    /// <summary>
    /// 目前程序的燈號來源（對話框的資料流與硬碟燈，web-protocol 的 activity；AgentHub.Activity.cs）：PID、程序實例、
    /// app 與它之間管線的位元組累計（AgentProcess.PipeBytes）；沒有活著的程序回 null。預設 null（不取樣）
    /// </summary>
    AgentActivitySource? ActivitySource => null;

    /// <summary>
    /// 現在重開程序不會打斷任何工作：沒有工具在執行（Claude：content_block_start 的 tool_use 起到對應的 tool_result；
    /// Codex：item/started 的 commandExecution／fileChange／mcpToolCall 到 item/completed）、沒有未回應的請求（核准、問卷、計畫確認）、
    /// 沒有存活的背景工作。閒置（IsRunning=false）時等於 !HasLiveBackgroundWork。
    /// 設計所說的「安全點（執行中）」＝IsRunning &amp;&amp; IsAtSafePoint。由會話自己追蹤（中樞的工具卡在完整的 assistant 區塊才建立，不能拿卡片判斷）；
    /// 改變時發 AgentSafePointChangedEvent
    /// </summary>
    bool IsAtSafePoint { get; }

    /// <summary>
    /// 有存活的背景工作（Claude：run_in_background 的工具還沒收到 system/task_notification 的 completed／stopped；Codex 沒有，固定 false）。
    /// 背景工作撐不過程序重開
    /// </summary>
    bool HasLiveBackgroundWork { get; }

    /// <summary>
    /// 存活的背景工作是由哪些工具開始的（Claude：background_tasks_changed 的清單以 task_started 的 tool_use_id 對到工具卡；Codex 沒有，空清單）。
    /// 對不到工具的背景工作不列（有沒有背景工作看 HasLiveBackgroundWork）。中樞在立即套用、現在中斷時以它判斷被打斷的背景操作是不是高風險
    /// </summary>
    IReadOnlyList<AgentBackgroundTool> LiveBackgroundTools { get; }

    event Action<AgentEvent>? EventReceived;

    Task SendAsync(AgentTurnInput input, AgentSessionSettings settings);

    /// <summary>Codex 本機控制面板；動作由 CodexControlRules 限定。</summary>
    Task<JObject> CodexControlAsync(string action, JObject args, AgentSessionSettings settings)
        => Task.FromException<JObject>(new NotSupportedException("這種代理不支援 Codex 控制"));

    /// <summary>
    /// 把一則訊息插進進行中的這一輪（使用者在代理執行中送出的訊息不等這一輪結束；實測見 docs/protocol-notes.md「執行中插入訊息」）：
    /// Claude 經 stdin 寫入，CLI 在下一個工具結果之後併進同一輪，沒有工具邊界就在這一輪的 result 之後接著處理（都算同一輪，到它的 result 才結束）；
    /// Codex 以 turn/steer 併進同一輪。這一輪還在準備（程序啟動、訊息還沒送出）時先等準備完成，cancellationToken 只能取消這段等待。
    /// 回 false＝沒有插入（沒在執行、按了停止、正在重開或接續、等準備逾時、這一輪不接受插入、代理不支援），中樞改排進佇列等這一輪結束；
    /// 回 true 之後這則就是這一輪的一部分，不另外回報 TurnCompleted
    /// </summary>
    Task<bool> SteerAsync(AgentTurnInput input, CancellationToken cancellationToken = default);

    /// <summary>停止這一輪；準備期間（訊息還沒送出）按停止，SendAsync 送出前會放棄並回報已停止。RestartAsync 進行中按停止＝不接續</summary>
    Task InterruptAsync();

    /// <param name="notes">每題的補充說明（題目 id → 文字；QuestionNoteRules，2026-11-05）：Claude、Grok 放進作答的 annotations.notes，Codex 併進答案；沒有是 null</param>
    Task AnswerQuestionAsync(string requestKey, IReadOnlyDictionary<string, IReadOnlyList<string>> answers, IReadOnlyDictionary<string, string>? notes = null);

    Task DeclineQuestionAsync(string requestKey);

    /// <summary>回應核准卡；拒絕時 feedback 是使用者附給代理的說明（Claude 放進拒絕訊息；Codex 由中樞改當下一則訊息）</summary>
    Task RespondApprovalAsync(string requestKey, AgentApprovalChoice choice, string? feedback);

    /// <summary>回應計畫確認卡（Claude ExitPlanMode）</summary>
    Task RespondPlanAsync(string requestKey, AgentPlanChoice choice, string? feedback);

    /// <summary>
    /// 執行中切換權限模式，立即套用到這一輪；做不到（Codex 的政策隨 turn/start 帶上、程序還沒啟動）回 false，新的模式從下一則訊息生效。
    /// Codex 切到完全自動做得到：之後的核准請求由宿主直接放行，還在等待的核准卡以 AgentAutoApproveEvent 請中樞放行
    /// </summary>
    Task<bool> SetPermissionModeAsync(AgentPermissionMode mode);

    /// <summary>
    /// 換模型與推理強度（null＝預設），立即套用到活著的程序；一輪進行中也從下一次模型呼叫起改用（實測見 docs/protocol-notes.md「執行中換模型」）。
    /// Claude 送 set_model／apply_flag_settings（閒置的程序也套用，下一輪不必重開）；Grok 只在這一輪的 prompt 已送出時送 session/set_config_option
    /// （閒置時下一輪開始前本來就會送）。做不到回 false（Codex 的模型隨 turn/start 帶上、Antigravity 要重開程序、沒有程序、準備中或正在重開、被拒），
    /// 新的設定從下一則訊息生效
    /// </summary>
    Task<bool> SetModelAsync(string? model, string? effort);

    /// <summary>
    /// 目前的程序開著 Remote Control（開啟成功、遠端連線沒有失敗）：可以從 claude.ai 或 Claude App 接手，閒置 2 小時才回收程序（AgentHubRules.RemoteControlIdleTimeout）。
    /// 只有 Claude 支援，其他代理固定 false
    /// </summary>
    bool HasRemoteControl { get; }

    /// <summary>
    /// 把 Remote Control 的開關立即套用到活著的程序（設定頁切換）：options 不為 null＝開啟（已開著就不動），null＝關閉。
    /// 沒有程序時（含正在啟動、重開）記下來，接下來啟動的程序照它開或不開（之後的 SendAsync 帶來的設定比它新，就改照設定）；
    /// 不支援的代理什麼都不做
    /// </summary>
    Task SetRemoteControlAsync(AgentRemoteControlOptions? options);

    /// <summary>
    /// 對話改名：把代理端的工作階段名稱改成 title（Claude 的 rename_session：claude.ai／Claude App 上的 Remote Control 工作階段、
    /// 續接清單看得到）。活著的程序立即送；沒有程序時記下，下一個程序啟動時送（比 settings.Title 新）。
    /// 其他代理沒有外部看得到的工作階段名稱，什麼都不做
    /// </summary>
    Task RenameAsync(string title);

    /// <summary>
    /// 沒有程序時啟動程序、只開 Remote Control 接回遠端工作階段（settings.RemoteControl 的 ReattachSessionId），不送訊息、不開始一輪：
    /// app 更新排空時關掉的、開著 Remote Control 的程序，由新版（放棄這次更新時就地）重開，手機上不必等桌面再送出一則訊息（設計 9.2、9.5）。
    /// 已有活著的程序、正在送出、重開或停下待接續、設定沒開 Remote Control、CLI 端還沒建立 session 時什麼都不做，回 false；
    /// 只有 Claude 支援，其他代理固定回 false
    /// </summary>
    Task<bool> ReattachRemoteControlAsync(AgentSessionSettings settings);

    /// <summary>
    /// 旁支提問（同 Claude 互動介面的 /btw）：以目前對話的上下文問一個問題，不打斷這一輪、問答不進對話的上下文與逐字稿。
    /// 沒有程序時照 settings 以續接開一個（不送訊息、不開始一輪），之後照閒置規則回收。只有 Claude 支援，其他代理回 Error
    /// </summary>
    Task<AgentSideAnswer> AskSideQuestionAsync(string question, AgentSessionSettings settings)
        => Task.FromResult(new AgentSideAnswer(null, "這種代理不支援旁支提問"));

    /// <summary>
    /// 對一個 MCP 伺服器做操作（重新連線、授權）。授權回傳要在瀏覽器開的網址：CLI 在這個程序裡開本機的回呼等瀏覽器回來，
    /// 呼叫端要讓程序開著、以 GetMcpStatusAsync 問到不再需要授權為止。沒有程序時同 AskSideQuestionAsync 開一個。只有 Claude 支援，其他代理回失敗
    /// </summary>
    Task<AgentMcpActionResult> McpActionAsync(string server, AgentMcpAction action, AgentSessionSettings settings)
        => Task.FromResult(new AgentMcpActionResult(false, "這種代理不支援 MCP 操作"));

    /// <summary>
    /// 這個程序載入的 MCP 伺服器與狀態（Claude 的 mcp_status；還在連線中的每秒再問、最多 15 秒）。沒有程序時同 AskSideQuestionAsync 開一個。
    /// 只有 Claude 支援，其他代理回錯誤
    /// </summary>
    Task<AgentMcpStatusResult> GetMcpStatusAsync(AgentSessionSettings settings)
        => Task.FromResult(new AgentMcpStatusResult(null, "這種代理不支援查 MCP 狀態"));

    /// <summary>
    /// 換輸出風格，立即套用到活著的程序（Claude 的 apply_flag_settings outputStyle；實測一輪之後就照新的風格）。
    /// 沒有程序、正在重開、被拒回 false：新的風格從下一次啟動生效。只有 Claude 支援，其他代理回 false
    /// </summary>
    Task<bool> SetOutputStyleAsync(string style) => Task.FromResult(false);

    /// <summary>
    /// 背景工作（Claude 的 run_in_background 指令、Monitor）目前的輸出（get_task_output）：背景工作屬於活著的程序，沒有程序時回錯誤，不開新的。
    /// 只有 Claude 支援
    /// </summary>
    Task<AgentTaskOutput> GetTaskOutputAsync(string taskId)
        => Task.FromResult(new AgentTaskOutput(null, 0, false, "這種代理不支援查看背景工作的輸出"));

    /// <summary>
    /// 強制停止背景工作（web-protocol 的 taskStop；2026-10-25 使用者要求）：stopId 是 LiveBackgroundTools 的 StopId，null＝全部。
    /// Claude 以 stop_task 逐一停止（CLI 不會為此開一輪）；Codex 以 thread/backgroundTerminals/terminate（全部是 clean），停掉的指令結束時
    /// 卡片是「已停止」、AgentBackgroundCommandFinishedEvent 帶 StoppedByUser（中樞不接續）；Antigravity、Grok 沒有逐一停止的協定，
    /// 只能全部停止：閒置時結束 CLI 的程序樹（背景工作跟著結束，下次送出照常續接），執行中不做。背景工作屬於活著的程序，沒有程序不開新的
    /// </summary>
    Task<AgentBackgroundStopResult> StopBackgroundWorkAsync(string? stopId)
        => Task.FromResult(new AgentBackgroundStopResult(0, "這種代理不支援停止背景工作"));

    /// <summary>
    /// 取消插進這一輪、CLI 還沒讀走的那則（cancel_async_message；messageUuid 是送出時的 AgentTurnInput.MessageUuid）。
    /// 回 true＝那則不會被代理看到（會話已回到插入之前的狀態）；false＝已被讀走或做不到。只有 Claude 支援
    /// </summary>
    Task<bool> CancelSteerAsync(string messageUuid) => Task.FromResult(false);

    /// <summary>支援取消插入的訊息（CancelSteerAsync）：Claude</summary>
    bool CanCancelSteer => false;

    /// <summary>
    /// 把代理以編輯工具改的檔案回溯到這則使用者訊息之前（rewind_files；dryRun 只回會改哪些檔案）。要用到程序：沒有時照
    /// ReattachRemoteControlAsync 開一個（不送訊息）。只有 Claude 支援
    /// </summary>
    Task<AgentRewindResult> RewindFilesAsync(string userMessageUuid, bool dryRun, AgentSessionSettings settings)
        => Task.FromResult(new AgentRewindResult(false, [], 0, 0, "這種代理不支援回溯檔案"));

    /// <summary>
    /// 以活著的程序產生對話標題（Claude 的 generate_session_title，persist false）：沒有程序、程序回的是這個程序之前給過的標題
    /// （實測 2.1.310 同一個程序第二次問一律回第一次的標題）、失敗都回 null，中樞改用短命的 CLI。只有 Claude 支援
    /// </summary>
    Task<string?> GenerateTitleAsync(string description) => Task.FromResult<string?>(null);

    /// <summary>
    /// 延伸成新對話時以 CLI 自己的分叉複製這段對話（Grok 的 _x.ai/session/fork）：成功回新對話的 session id（有完整的前文），
    /// 不支援、還沒建立過對話或失敗回 null（中樞改附前文節錄）。Claude 以逐字稿副本分支，不經這裡
    /// </summary>
    Task<string?> ForkSessionAsync(string newSessionId, AgentSessionSettings settings) => Task.FromResult<string?>(null);

    /// <summary>現在能以活著的程序產生標題（Claude、程序開著）：中樞只在這時先試 GenerateTitleAsync</summary>
    bool CanGenerateTitle => false;

    /// <summary>
    /// CLI 自己回報的 skill、hooks、記憶清單（Claude 的 get_skills_dialog、get_hooks_listing、get_memory_dialog；只問活著的程序，
    /// 儀表板的 MCP 檢查順便問）。沒有程序、舊版 CLI、其他代理回 null
    /// </summary>
    Task<Health.ClaudeCliInventory?> GetInventoryAsync() => Task.FromResult<Health.ClaudeCliInventory?>(null);

    /// <summary>回溯對話時要讀的逐字稿路徑（Claude；不保證存在）。其他代理是 null</summary>
    string? TranscriptFor(AgentSessionSettings settings) => null;

    /// <summary>
    /// 以新版 CLI 重開程序，必要時自動接續被打斷的一輪（流程見 AgentRestartRequest）。中樞保證同一個會話同時只有一個 RestartAsync；
    /// 重入時回 Deferred
    /// </summary>
    Task<AgentRestartResult> RestartAsync(AgentRestartRequest request);

    /// <summary>正常關閉程序（閒置回收、關視窗）；下次送訊息會自動續接</summary>
    Task CloseAsync();
}
