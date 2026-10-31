using System.Diagnostics;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// Claude 代理：一個對話一個 claude -p 程序（雙向 stream-json），可連續多輪；
/// 模型、推理強度、工作資料夾、帳戶、執行檔（含 CLI 換版後的身分）、客製設定改變時於下一輪重開程序（--resume 續接），權限模式以 set_permission_mode 即時切換
/// （啟動時一律帶 --allow-dangerously-skip-permissions，否則實測切到完全自動會被拒；切換失敗就重開程序）。
/// 模型與推理強度也能以 set_model、apply_flag_settings 即時切換（SetModelAsync，一輪中途也從下一次模型呼叫起生效），成功的不算設定改變。
/// 目前的權限模式以 CLI 回報的為準（init、status），「本對話都允許」帶來的模式切換也明確送 set_permission_mode，畫面跟著改。
/// 換過工作資料夾時以逐字稿路徑續接（CLI 依資料夾分開存放逐字稿）；續接時若 CLI 回報找不到 session（逐字稿被清掉、換了帳戶），
/// 自動改開新 session、附上前文節錄重送這則訊息。
/// 執行中旗標從 SendAsync 一開始就成立：啟動程序、轉圖片的期間按停止，送出前會放棄；訊息送出後才補送中斷。
/// <para>
/// CLI 熱更新（契約見 docs/plans/2026-10-10-hot-restart-contract.md，實測見 docs/protocol-notes.md「CLI 執行中更新與重開續接」）：
/// 追蹤目前程序回報的版本、程序實例與安全點（工具、未回應的請求、背景工作），RestartAsync 以新版重開——不送 interrupt（中斷會被寫成使用者拒絕，
/// CLI 也不會接續）、結束程序樹並確認子孫都已結束、截掉逐字稿寫到一半的尾行，再以 --resume 加 RESUME 環境變數啟動，由 CLI 自己接續被打斷的一輪
/// （那一輪不是 SendAsync 開始的，一樣算執行中；新程序一啟動就接收事件，接續的一輪比 initialize 回應還早開始）。
/// </para>
/// </summary>
internal sealed class ClaudeAgentSession : IAgentSession
{
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(10);

    /// <summary>旁支提問等回答的上限（實測一般約 1.5 秒；長的答案要模型多寫一點）</summary>
    private static readonly TimeSpan SideQuestionTimeout = TimeSpan.FromMinutes(3);

    /// <summary>MCP 操作等回應的上限（重新連線遠端的伺服器要等連線結果）</summary>
    private static readonly TimeSpan McpActionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>MCP 伺服器還在連線中（pending）時隔多久再問一次、最多等多久（實測遠端的伺服器約 2 秒內連上或失敗）</summary>
    private static readonly TimeSpan McpPendingPollDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan McpPendingLimit = TimeSpan.FromSeconds(15);

    /// <summary>寫入失敗（程序已結束）後等 stdout 讀完的上限：讀到的 result 會由續接重試或結束事件接手</summary>
    private static readonly TimeSpan ExitDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>重開流程輪詢狀態的間隔（等 CLI 接手、等逐字稿寫入、等自動接續）</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>工具結果在 stdout 出現超過這麼久，就當成逐字稿已經寫入（只看尾段，太久以前的結果可能已不在尾段裡）</summary>
    private static readonly TimeSpan ToolResultPersistedAfter = TimeSpan.FromSeconds(10);

    /// <summary>檢查工具結果是否已寫入時讀逐字稿的尾段大小</summary>
    private const int TranscriptTailBytes = 256 * 1024;

    /// <summary>一則訊息最多附幾張圖、base64 總量上限（Claude 單次請求有大小限制）</summary>
    private const int MaxImagesPerMessage = 20;
    private const long MaxImageBytesPerMessage = 20L * 1024 * 1024;

    private readonly object gate = new();
    private readonly Dictionary<string, ClaudeControlRequest> pendingRequests = new();
    private readonly Dictionary<string, (AgentProcess Process, TaskCompletionSource<ClaudeControlResponse?> Completion)> controlWaiters = new();

    /// <summary>SetModelAsync 一次一個（連續切換依序送給 CLI）</summary>
    private readonly SemaphoreSlim modelSwitchGate = new(1, 1);

    /// <summary>
    /// 程序啟動時的 initialize（模型清單）與 get_settings（ultracodeAvailable）回應：兩個都到了才回報模型清單，
    /// 免得先回報沒有 ultracode 的清單、再回報有的（選單閃一下、設定多存一次）。只記最近一個程序的
    /// </summary>
    private (AgentProcess Process, List<AgentModelOption>? Models, bool? UltracodeAvailable)? capabilityReplies;

    /// <summary>
    /// 本對話放行的「完全相同的請求」（工具名稱＋輸入）；更廣的範圍以 CLI 收到的 session 規則為準
    /// （編輯類是 CLI 建議的「改為自動接受編輯」，工作資料夾外與敏感檔仍由 CLI 詢問）
    /// </summary>
    private readonly HashSet<string> sessionAllowedRequests = new(StringComparer.Ordinal);

    /// <summary>「本對話都允許」同意過的 CLI 規則與資料夾：CLI 的 session 規則只存在程序記憶體，重開程序時以 --allowedTools／--add-dir 帶回</summary>
    private readonly List<string> sessionRules = new();
    private readonly List<string> sessionDirectories = new();
    private AgentProcess? process;
    private ClaudeStreamParser parser = new();
    private AgentSessionSettings? processSettings;
    private AgentPermissionMode currentMode;
    private bool running;
    private bool interruptRequested;
    private bool interruptSent;
    private bool payloadSent;
    // 這一輪的訊息正在寫給 CLI：寫完、payloadSent 設好之前 CLI 就可能開始回應（system/init、requesting），那段活動也算接手
    private bool payloadWriting;
    private bool initSeen;
    private bool resumedProcess;
    private AgentTurnInput? lastInput;
    private AgentSessionSettings? lastSettings;
    private int requestCounter;

    /// <summary>這一輪的訊息寫出（或送出失敗、放棄）時完成：插入的訊息要排在它後面寫（SteerAsync 等它）</summary>
    private TaskCompletionSource? payloadDelivered;

    /// <summary>每次送出一則訊息加一；中斷看門狗只處理排下它的那一輪</summary>
    private int turnGeneration;
    private bool disposed;

    // ── CLI 熱更新：目前程序的狀態（都在 gate 內讀寫） ──

    /// <summary>目前程序回報的版本（system/init 的 claude_code_version）</summary>
    private string? runningVersion;

    /// <summary>目前程序寫的逐字稿（以路徑續接時是那個路徑，否則依設定資料夾、工作資料夾、session id 推算；不保證已存在）</summary>
    private string? processTranscript;
    private string? processConfigDirectory;

    /// <summary>
    /// 子代理逐字稿的用量（ClaudeSubagentUsageTail）：子代理沒有串流事件、轉送的訊息輸出是開頭的值，用量從逐字稿讀。
    /// 同一個 session 的程序共用一份（重開時接著讀，舊程序最後寫的不會漏）；換了 session（回溯分支）換一份
    /// </summary>
    private ClaudeSubagentUsageTail? subagentTail;

    /// <summary>上次因為子代理的訊息讀逐字稿的時間（Environment.TickCount64）：一秒最多一次，子代理結束、一輪結束時一定讀</summary>
    private long subagentPollTick;

    /// <summary>目前程序啟動的時間：/goal 只看這之後逐字稿寫的 goal_status（目標只活在程序裡，ClaudeGoalRules）</summary>
    private DateTimeOffset processStartedAt;

    /// <summary>上次回報的目標（null＝沒有）；每個程序重新開始</summary>
    private string? reportedGoal;

    /// <summary>這一輪已由 CLI 接手（送出訊息後看到這一輪的活動）：之前重開，剛送出的訊息可能還沒寫進逐字稿</summary>
    private bool turnAccepted;

    /// <summary>送出這一輪的訊息時逐字稿的長度（-1＝不明）：重開前確認 CLI 已把這一輪寫進逐字稿</summary>
    private long transcriptBaseline = -1;

    private bool toolRunning;
    private List<string> backgroundTaskIds = new();

    /// <summary>閒置時 CLI 自己開的一輪（背景工作完成的通知、續接時回報孤兒背景工作）：到它的 result 為止都算背景工作還沒結束</summary>
    private bool cliOwnTurn;

    /// <summary>
    /// 使用者的訊息排在 CLI 自己的一輪後面（那一輪進行中送出的；它的 result 已到）：等使用者那一輪自己開始（system/init）
    /// 才重新量逐字稿、之後的活動才算 CLI 接手了這則訊息
    /// </summary>
    private bool awaitingQueuedTurn;

    // ── 這一輪是哪一則訊息的（遠端送來的訊息與 app 的訊息排在同一個佇列） ──

    /// <summary>
    /// 這一輪開頭那則訊息的 uuid：app 送出時帶的、或閒置時遠端送來開始這一輪的；null＝不明（自動接續的一輪、接續訊息）。
    /// 對 result 的 user_message_uuid 判斷它是不是這一輪的（ClaudeStreamRules.IsResultForOtherTurn）
    /// </summary>
    private string? turnUuid;

    /// <summary>CLI 已開始處理 turnUuid 那則訊息（看到它的 command_lifecycle started 或回送）</summary>
    private bool turnUuidStarted;

    /// <summary>
    /// turnUuid 那則訊息排在別的指令後面（command_lifecycle queued、還沒 started）：這段期間的活動是前面那一輪的，
    /// 不算 CLI 接手了這則（這時重開，訊息只在 CLI 記憶體的佇列裡，會不見）
    /// </summary>
    private bool turnUuidQueued;

    /// <summary>
    /// 這一輪是閒置時遠端送來的訊息開始的：app 送出時可以接手（遠端那一輪的 result 以 uuid 認出不是 app 這則的）；
    /// 到它的 result 為止不是安全點（換版等遠端這一輪做完，不在中途重開）
    /// </summary>
    private bool remoteTurn;

    /// <summary>
    /// 這一輪是閒置時 CLI 自己開的（system/init 時會話不在執行中：背景工作完成的通知那一輪，cliOwnTurn 照舊算背景工作）：會話標成執行中，
    /// 畫面才看得出代理在做事、停止送得出去。這一輪沒有 app 的訊息（turnUuid 是 null），它的 result 不論 origin 都是這一輪的結尾；
    /// app 送出或插入時接手成為這一輪（同 remoteTurn），回送顯示是遠端送來的訊息時改記成遠端的一輪
    /// </summary>
    private bool cliTurn;

    /// <summary>
    /// 這個程序看過的指令 uuid（command_lifecycle，含 app 自己送的；回送的只記遠端送來的）：
    /// result 的 user_message_uuid 是其中另一則時，那一輪不是這一輪
    /// </summary>
    private readonly HashSet<string> commandUuids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>commandUuids 記下的先後（超過上限時淘汰最舊的）</summary>
    private readonly Queue<string> commandOrder = new();

    /// <summary>commandUuids 的上限（程序存活很久時不無限增長）</summary>
    private const int MaxCommandUuids = 256;

    /// <summary>存活的背景工作是由哪些工具開始的（解析器的快照；中樞在立即套用時判斷高風險操作）</summary>
    private IReadOnlyList<AgentBackgroundTool> backgroundTools = Array.Empty<AgentBackgroundTool>();

    /// <summary>背景工作完成（不是被停止）的通知還在等 CLI 自己開一輪告訴模型（Stopwatch 時間戳；0＝沒有）</summary>
    private long notificationTurnSince;

    private string? lastToolResultId;
    private long lastToolResultAt;

    /// <summary>上一次發出的安全點旗標（去重）</summary>
    private (bool Tool, bool User, bool Background) lastSafePoint;

    /// <summary>上一次發出的背景工作（AgentBackgroundWorkEvent；去重）</summary>
    private int lastBackgroundCount;
    private IReadOnlyList<AgentBackgroundTool> lastBackgroundTools = Array.Empty<AgentBackgroundTool>();

    /// <summary>RestartAsync 回過 Deferred：下次狀態變成安全點時即使三個旗標沒變也再發一次事件，讓中樞重試</summary>
    private bool safePointNudge;

    // ── CLI 熱更新：重開流程 ──

    private bool restarting;
    private TaskCompletionSource? restartCompletion;

    /// <summary>重開途中收到 InterruptAsync／CloseAsync：不接續</summary>
    private bool restartInterruptRequested;

    /// <summary>重開時卸下、正在結束的舊程序，與它在卸下之前送出的這一輪 result（這一輪其實已經結束）</summary>
    private AgentProcess? restartingOld;
    private AgentTurnCompletedEvent? restartOldResult;

    /// <summary>停下待接續的一輪（RestartAsync Settings=null 時這一輪正在執行）</summary>
    private InterruptedTurn? stoppedTurn;

    /// <summary>新程序帶 RESUME 環境變數啟動、等 CLI 自動接續的期間</summary>
    private AutoTurnWatch? autoTurn;

    private string? lastResumeReason;

    // ── Remote Control（都在 gate 內讀寫；協定實測見 docs/protocol-notes.md「Remote Control」） ──

    /// <summary>目前程序要不要開 Remote Control：啟動時的設定，SetRemoteControlAsync 改過就是改過的；null＝不開</summary>
    private AgentRemoteControlOptions? remoteControlOptions;

    /// <summary>還在等回應的 remote_control 請求（只處理最新的一個，之前的回應忽略）、它是開啟還是關閉、有沒有帶接回的 id</summary>
    private string? remoteControlRequestId;
    private bool remoteControlRequestEnables;
    private bool remoteControlRequestReattached;

    /// <summary>目前程序的 Remote Control 已開啟、遠端連線沒有斷</summary>
    private bool remoteControlActive;

    /// <summary>
    /// 目前程序送過開啟、之後沒送過關閉：關閉時照送（遠端暫時斷線時 remoteControlActive 是 false，CLI 之後可能自己恢復連線）
    /// </summary>
    private bool remoteControlEnableSent;

    /// <summary>
    /// 沒有程序時（正在啟動、重開）設定頁切換的值：接下來啟動的程序照它，不照送出當時的設定；之後的 SendAsync 帶來的設定比它新，就清掉
    /// </summary>
    private bool hasRemoteControlOverride;
    private AgentRemoteControlOptions? remoteControlOverride;

    /// <summary>
    /// 工作階段名稱（對話標題）：程序啟動時與 Remote Control 連上後以它送 rename_session（claude.ai／Claude App 上的名稱跟著對話標題）。
    /// 啟動時取 settings.Title；沒有程序時 RenameAsync 改過就照改過的（titleOverride），之後的 SendAsync 帶來的設定比它新就清掉
    /// </summary>
    private string? sessionTitle;
    private string? titleOverride;

    /// <summary>最近開啟成功的遠端工作階段與當時的帳戶（AccountKey）：程序重開時接回同一個工作階段；換了帳戶就不接</summary>
    private string? remoteBridgeSessionId;
    private string? remoteBridgeAccountKey;

    /// <summary>已提示過 Remote Control 開不起來或斷線：一個對話只提示一次（程序每次重開都會再試）</summary>
    private bool remoteControlNoticeShown;

    /// <summary>
    /// ReattachRemoteControlAsync 正在啟動程序：SendAsync 等它結束再送（沿用它啟動的程序，不會同時開兩個），RestartAsync 回 Deferred
    /// </summary>
    private TaskCompletionSource? reattachCompletion;

    /// <summary>重開程序接回 Remote Control 的途中收到 CloseAsync（app 結束、關掉對話）：程序起來後立刻關掉</summary>
    private bool reattachCancelled;

    public event Action<AgentEvent>? EventReceived;

    /// <summary>送出中斷後多久還沒結束就強制結束程序樹（測試可縮短）</summary>
    internal TimeSpan InterruptWatchdogDelay { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>新程序 initialize 回應後多久還沒開始接續，就判定這次沒有接續（E-10：接續的一輪比 initialize 回應還早開始）</summary>
    internal TimeSpan AutoTurnGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>新程序多久都沒有回 initialize 就當成啟動失敗</summary>
    internal TimeSpan AutoTurnStartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>執行中重開前，等逐字稿寫入這一輪的訊息與最後一個工具結果的上限</summary>
    internal TimeSpan TranscriptCatchUpTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>立即重開時，訊息剛送出、CLI 還沒接手的話最多等多久</summary>
    internal TimeSpan HandoffTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>結束程序樹並確認子孫都已結束的上限</summary>
    internal TimeSpan KillTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>插入訊息時這一輪還在準備（程序啟動、訊息還沒寫出），最多等多久；逾時就不插入（中樞改排進佇列）</summary>
    internal TimeSpan SteerReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>重開前停掉背景工作（stop_task）等回應的上限</summary>
    internal TimeSpan StopTaskTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>背景工作完成的通知多久都沒等到 CLI 自己開的那一輪，就不再當成背景工作（通知可能已併進其他輪次）</summary>
    internal TimeSpan NotificationTurnTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 系統重開時帶 RESUME 環境變數由 CLI 自己接續（預設）。測試關掉它來模擬「環境變數在某版失效」：
    /// 這時新程序不會自動接續，改走備案——經 stdin 送接續訊息
    /// </summary>
    internal bool UseResumeEnvironment { get; init; } = true;

    /// <summary>測試用：SendAsync 把訊息寫給 CLI 之後、記下「已送出」之前呼叫（拉長這段時間重現 CLI 搶先回應）</summary>
    internal Func<Task>? AfterPayloadWrittenForTest { get; init; }

    /// <summary>測試用：啟動程序之前呼叫（重現程序啟動期間設定頁切換 Remote Control）</summary>
    internal Func<Task>? BeforeProcessStartForTest { get; init; }

    /// <summary>最近一個 result 的 resume_reason（CLI 自動接續被打斷那一輪時才有；記錄與測試用）</summary>
    internal string? LastResumeReason
    {
        get
        {
            lock (gate)
                return lastResumeReason;
        }
    }

    public AgentProvider Provider => AgentProvider.Claude;

    public bool IsRunning
    {
        get
        {
            lock (gate)
                return running;
        }
    }

    public bool HasLiveProcess
    {
        get
        {
            lock (gate)
                return process != null && !process.HasExited;
        }
    }

    public bool HasRemoteControl
    {
        get
        {
            lock (gate)
                return process != null && !process.HasExited && remoteControlActive;
        }
    }

    // ── CLI 熱更新（契約見 docs/plans/2026-10-10-hot-restart-contract.md） ──

    /// <summary>目前程序回報的版本（system/init 的 claude_code_version）；沒有程序或還沒回報為 null</summary>
    public string? RunningVersion
    {
        get
        {
            lock (gate)
                return runningVersion;
        }
    }

    /// <summary>目前程序的 AgentProcess.InstanceId；沒有程序為 null</summary>
    public string? ProcessInstance
    {
        get
        {
            lock (gate)
                return process?.InstanceId;
        }
    }

    /// <summary>目前程序的燈號來源（IAgentSession.ActivitySource：對話框的資料流與硬碟燈）；沒有程序或已結束為 null</summary>
    public AgentActivitySource? ActivitySource
    {
        get
        {
            lock (gate)
                return process?.Activity;
        }
    }

    /// <summary>
    /// 現在重開不會打斷任何工作：閒置時＝沒有背景工作（含背景工作完成後 CLI 自己開的那一輪）；
    /// 執行中還要沒有工具在執行、沒有未回應的請求，而且 CLI 已接手這一輪（剛送出的訊息還沒寫進逐字稿前重開，訊息會消失）
    /// </summary>
    public bool IsAtSafePoint
    {
        get
        {
            lock (gate)
                return IsAtSafePointLocked(ignoreRestart: false);
        }
    }

    /// <summary>run_in_background 的背景工作還在跑（background_tasks_changed 的清單），或剛完成、等 CLI 自己開一輪告訴模型</summary>
    public bool HasLiveBackgroundWork
    {
        get
        {
            lock (gate)
                return HasBackgroundWorkLocked();
        }
    }

    private bool HasBackgroundWorkLocked() => backgroundTaskIds.Count > 0 || cliOwnTurn || notificationTurnSince != 0;

    /// <summary>存活的背景工作是由哪些工具開始的（background_tasks_changed 的清單對到 task_started 的 tool_use_id；對不到的不列）</summary>
    public IReadOnlyList<AgentBackgroundTool> LiveBackgroundTools
    {
        get
        {
            lock (gate)
                return backgroundTools;
        }
    }

    private bool IsAtSafePointLocked(bool ignoreRestart)
    {
        if ((restarting && !ignoreRestart) || HasBackgroundWorkLocked())
            return false;
        if (!running)
            return true;
        return turnAccepted && !remoteTurn && !cliTurn && autoTurn == null && !toolRunning && pendingRequests.Count == 0;
    }

    public async Task SendAsync(AgentTurnInput input, AgentSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.SessionId))
            throw new ArgumentException("Claude 對話缺少 SessionId", nameof(settings));

        AgentProcess? toClose = null;
        string? previousDirectory;
        bool startsProcess;
        int generation;
        // 每次寫入都用新的 uuid（CLI 略過 uuid 重複的訊息）；中樞給了就用它（記在使用者項目，回溯以它指名那則）
        var messageUuid = input.MessageUuid ?? Guid.NewGuid().ToString();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        while (true)
        {
            Task? pendingRestart = null;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                // 以新版重開進行中：等它結束（重開可能正在結束舊程序、截逐字稿的尾）
                if (restartCompletion != null)
                {
                    pendingRestart = restartCompletion.Task;
                }
                else if (reattachCompletion != null)
                {
                    // 正在重開程序接回 Remote Control（app 更新後）：等它起來，這則訊息沿用那個程序
                    pendingRestart = reattachCompletion.Task;
                }
                else
                {
                    if (running && !remoteTurn && !cliTurn)
                        throw new InvalidOperationException("代理還在處理上一則訊息");
                    // 遠端送來的訊息那一輪、或閒置時 CLI 自己開的一輪還在進行（中樞還沒收到事件就送出了）：這則接手成為這一輪，照常寫進 stdin，
                    // CLI 排在那一輪後面或併進同一輪，那一輪的 result 以 uuid 或 origin 認出不是這則的。要換程序就不能接手（會打斷那一輪）
                    if (running && (process == null || process.HasExited || NeedsRestart(processSettings!, settings)))
                        throw new InvalidOperationException(cliTurn ? "代理還在處理背景工作的結果" : "代理還在處理從 claude.ai 或 Claude App 送來的訊息");
                    running = true;
                    remoteTurn = false;
                    cliTurn = false;
                    turnUuid = messageUuid;
                    turnUuidStarted = false;
                    turnUuidQueued = false;
                    payloadDelivered?.TrySetResult();
                    payloadDelivered = delivered;
                    generation = ++turnGeneration;
                    interruptRequested = false;
                    interruptSent = false;
                    payloadSent = false;
                    payloadWriting = false;
                    turnAccepted = false;
                    awaitingQueuedTurn = false;
                    transcriptBaseline = -1;
                    // 使用者送了別的：停下待接續的那一輪不再接續
                    stoppedTurn = null;
                    // 這次的設定比之前沒有程序時記下的 Remote Control 切換與改名新
                    hasRemoteControlOverride = false;
                    remoteControlOverride = null;
                    titleOverride = null;
                    lastInput = input;
                    lastSettings = settings;
                    previousDirectory = processSettings?.WorkingDirectory;
                    if (process != null && (process.HasExited || NeedsRestart(processSettings!, settings)))
                    {
                        toClose = process;
                        process = null;
                        ResetProcessStateLocked();
                    }
                    startsProcess = process == null;
                    break;
                }
            }
            await pendingRestart;
        }

        AgentProcess? current = null;
        try
        {
            if (toClose != null)
                await CloseProcessAsync(toClose);

            // 要開新程序續接時先找逐字稿：換過工作資料夾就改以逐字稿路徑續接，並在訊息前告訴代理新的資料夾
            // （CLI 沿用對話第一次請求時記下的系統提示，背景說明裡還是舊的工作資料夾）
            var transcript = startsProcess ? await FindResumeTranscriptAsync(settings) : null;
            // 延伸出來的對話第一次啟動（從原對話逐字稿的副本分支）：工作資料夾和原對話相同，不必說換了資料夾
            var prefix = transcript != null && !IsForkStart(settings) && !SameDirectory(previousDirectory, settings.WorkingDirectory)
                ? ClaudeStreamRules.BuildFolderChangedNote(settings.WorkingDirectory)
                : null;
            var payload = await Task.Run(() => BuildPayload(input, prefix, messageUuid));

            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                current = process;
            }

            if (current == null)
            {
                current = await StartProcessAsync(settings, startsProcess ? transcript : await FindResumeTranscriptAsync(settings));
            }
            else if (!await EnsurePermissionModeAsync(current, settings.PermissionMode))
            {
                // 執行中切換權限模式被拒（或沒回應）：重開程序以新模式續接
                lock (gate)
                {
                    if (ReferenceEquals(process, current))
                    {
                        process = null;
                        ResetProcessStateLocked();
                    }
                }
                await CloseProcessAsync(current);
                // 舊程序已關閉：清掉 current，新程序建立失敗時 catch 才會回報錯誤（不會誤判成「程序已被接手」而吞掉例外）
                current = null;
                current = await StartProcessAsync(settings, await FindResumeTranscriptAsync(settings));
            }

            if (TryAbortInterrupted())
                return;

            // 記下送出前逐字稿的長度：重開前以「逐字稿變長了」確認 CLI 已把這則訊息寫進去
            string? transcriptPath;
            lock (gate)
                transcriptPath = processTranscript;
            var baseline = MeasureTranscript(transcriptPath);

            lock (gate)
                payloadWriting = true;
            await current.WriteLineAsync(payload);
            if (AfterPayloadWrittenForTest != null)
                await AfterPayloadWrittenForTest();

            bool interruptNow;
            lock (gate)
            {
                payloadWriting = false;
                payloadSent = true;
                transcriptBaseline = baseline;
                interruptNow = interruptRequested && !interruptSent && ReferenceEquals(process, current);
                if (interruptNow)
                    interruptSent = true;
            }

            if (interruptNow)
            {
                await WriteQuietlyAsync(current, ClaudeStreamRules.BuildInterruptRequest(NextRequestId("interrupt")));
                StartInterruptWatchdog(current, generation);
            }
        }
        catch (Exception ex)
        {
            if (ex is IOException && current != null)
            {
                // 寫入失敗＝程序已結束（例如續接失敗的 CLI 不讀 stdin 就結束）：等 stdout 讀完再判斷，
                // 讀到的 result 會由續接失敗的自動重試或結束事件接手這一輪，不能再對中樞回報一次送出失敗
                try
                {
                    await current.ExitTask.WaitAsync(ExitDrainTimeout);
                }
                catch (TimeoutException)
                {
                }
            }

            lock (gate)
            {
                // 程序已經換掉（續接失敗的自動重試、程序結束事件）或這一輪已由 result 回報：結果由它們負責
                if (current != null && (!ReferenceEquals(process, current) || !running))
                    return;
                running = false;
                turnAccepted = false;
            }
            UpdateSafePoint();

            // 只有程序已經啟動、寫入才失敗時才是「程序已結束」；啟動前的錯誤（例如工作資料夾不存在）原樣往上丟，訊息才看得出原因
            if (ex is IOException && current != null)
                throw new InvalidOperationException($"Claude 程序已結束：{current.GetStderrTail(400)}", ex);
            throw;
        }
        finally
        {
            // 等著插入的訊息不再等（寫出了就接著寫；沒寫出它會看到 payloadSent 為 false 而放棄）
            delivered.TrySetResult();
        }
    }

    /// <summary>
    /// 插入訊息（實測見 docs/protocol-notes.md「執行中插入訊息」）：帶新的 uuid 經 stdin 寫入。工具執行中寫入的，CLI 在工具結果之後併進同一輪
    /// （逐字稿記成 queued_command 附件，result 的 user_message_uuid 仍是這一輪開頭那則）；沒有工具邊界時，CLI 在這一輪的 result 之後把它當成
    /// 下一輪開始。兩種都算同一輪：這一輪的訊息改成插入的這則，它開始之前到的 result（user_message_uuid 是前一則）以 IsResultForOtherTurn
    /// 認出不是這一輪的結尾，等它開始後的 result 才結束。開始之前它只在 CLI 記憶體的佇列裡，不算 CLI 已接手（不是安全點，重開會讓它不見）；
    /// 這時按停止，停的是前面那一輪，它開始時會話再送一次中斷（ObserveCommands 的 reinterrupt）
    /// </summary>
    public async Task<bool> SteerAsync(AgentTurnInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Task? preparing;
        lock (gate)
        {
            if (!CanSteerLocked())
                return false;
            preparing = payloadSent ? null : payloadDelivered?.Task;
            if (!payloadSent && preparing == null)
                return false;
        }
        if (preparing != null)
        {
            try
            {
                await preparing.WaitAsync(SteerReadyTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        var messageUuid = input.MessageUuid ?? Guid.NewGuid().ToString();
        var payload = await Task.Run(() => BuildPayload(input, null, messageUuid), cancellationToken);
        string? transcriptPath;
        lock (gate)
            transcriptPath = processTranscript;
        var baseline = MeasureTranscript(transcriptPath);

        AgentProcess target;
        lock (gate)
        {
            // 寫出之後就取消不了：最後一次檢查取消
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanSteerLocked() || !payloadSent || process == null || process.HasExited)
                return false;
            target = process;
            // 取消插入（CancelSteerAsync）時回到這裡之前的狀態
            pendingSteer = new SteerUndo(messageUuid, turnUuid, turnUuidStarted, turnUuidQueued, turnAccepted, remoteTurn, cliTurn, transcriptBaseline,
                turnUuid != null && !commandUuids.Contains(turnUuid));
            // 前一則（這一輪開頭那則或上一則插入的）的 result 從此不算這一輪的結尾
            if (turnUuid != null)
                RememberCommandLocked(turnUuid);
            turnUuid = messageUuid;
            turnUuidStarted = false;
            turnUuidQueued = true;
            turnAccepted = false;
            // 遠端送來的訊息那一輪、CLI 自己開的一輪：這則接手成為這一輪（同 SendAsync）
            remoteTurn = false;
            cliTurn = false;
        }
        UpdateSafePoint();

        try
        {
            await target.WriteLineAsync(payload);
        }
        catch (IOException)
        {
            // 程序已結束：這一輪由程序結束事件收尾，這則沒送到
            return false;
        }
        lock (gate)
        {
            if (ReferenceEquals(process, target) && running && string.Equals(turnUuid, messageUuid, StringComparison.Ordinal))
                transcriptBaseline = baseline;
        }
        return true;
    }

    /// <summary>最近一次插入之前的這一輪狀態（取消插入時還原）：只有最近一則可以取消</summary>
    private sealed record SteerUndo(string Uuid, string? TurnUuid, bool Started, bool Queued, bool Accepted, bool RemoteTurn, bool CliTurn, long Baseline, bool Remembered);

    private SteerUndo? pendingSteer;

    public bool CanCancelSteer => true;

    /// <summary>
    /// 取消最近插入、CLI 還沒讀走的那則（cancel_async_message；實測見 protocol-notes）：只在有工具執行中時試——插入的訊息在工具結果之後才被讀走，
    /// 回應一定比這一輪的 result 先到；沒有工具在執行時那則排在 result 之後，result 可能在還原之前就到（會話以為那是別一輪的 result、這一輪停不下來），不試。
    /// CLI 說取消了才還原插入之前的狀態（這一輪的訊息回到前一則）
    /// </summary>
    public async Task<bool> CancelSteerAsync(string messageUuid)
    {
        AgentProcess target;
        lock (gate)
        {
            if (pendingSteer is not { } undo || undo.Uuid != messageUuid || !running || interruptRequested || process == null || process.HasExited
                || !string.Equals(turnUuid, messageUuid, StringComparison.Ordinal) || turnUuidStarted || !toolRunning)
                return false;
            target = process;
        }
        var response = await RequestControlAsync(target, "cancel", id => ClaudeControlRules.BuildCancelAsyncMessageRequest(id, messageUuid)).ConfigureAwait(false);
        bool cancelled = response is { Success: true } && ClaudeControlRules.ParseCancelled(response.Response);
        lock (gate)
        {
            if (pendingSteer is not { } undo || undo.Uuid != messageUuid)
                return cancelled;
            pendingSteer = null;
            if (!cancelled || !ReferenceEquals(process, target) || !string.Equals(turnUuid, messageUuid, StringComparison.Ordinal))
                return cancelled;
            turnUuid = undo.TurnUuid;
            turnUuidStarted = undo.Started;
            turnUuidQueued = undo.Queued;
            turnAccepted = undo.Accepted;
            remoteTurn = undo.RemoteTurn;
            cliTurn = undo.CliTurn;
            transcriptBaseline = undo.Baseline;
            if (undo.Remembered && undo.TurnUuid != null)
                commandUuids.Remove(undo.TurnUuid);
        }
        UpdateSafePoint();
        return true;
    }

    /// <summary>可以插入訊息：這一輪在執行、沒按停止、沒有在重開或等自動接續</summary>
    private bool CanSteerLocked() => !disposed && running && !interruptRequested && !restarting && restartCompletion == null && autoTurn == null;

    /// <summary>
    /// 續接時要帶給 --resume 的逐字稿路徑（只有換過工作資料夾才需要）；在背景找，設定資料夾可能在慢的磁碟上。
    /// 以 --resume 啟動前也在這裡截掉逐字稿寫到一半的尾行（app 或電腦當機、程序被結束時 CLI 可能寫到一半，新程序接在半行後面寫會黏成永久壞行）；
    /// 呼叫時這個對話的舊程序都已關閉
    /// </summary>
    private static Task<string?> FindResumeTranscriptAsync(AgentSessionSettings settings)
    {
        if (!settings.SessionStarted)
            return Task.FromResult<string?>(null);
        // 延伸出來的對話還沒分支：以原對話逐字稿的副本啟動（中樞複製時已截到最後一個換行，副本只有這裡讀，不截）
        if (IsForkStart(settings))
            return Task.FromResult(settings.ForkSource);
        var configDirectory = ClaudeStreamRules.ResolveConfigDirectory(settings.ConfigureEnvironment);
        return Task.Run(() =>
        {
            var elsewhere = ClaudeStreamRules.FindTranscriptElsewhere(configDirectory, settings.SessionId!, settings.WorkingDirectory);
            var path = elsewhere ?? ClaudeStreamRules.FindTranscript(configDirectory, settings.SessionId!, settings.WorkingDirectory);
            if (path != null)
                TrimTranscript(path, settings.SessionId!);
            return elsewhere;
        });
    }

    /// <summary>延伸出來的對話第一次啟動：以 --resume 原對話逐字稿的副本 --fork-session --session-id 這個對話的 sid 分支（ConversationForkRules）</summary>
    private static bool IsForkStart(AgentSessionSettings settings)
        => settings.SessionStarted && !string.IsNullOrWhiteSpace(settings.ForkSource) && !string.IsNullOrWhiteSpace(settings.SessionId);

    private static bool SameDirectory(string? a, string? b)
        => a != null && b != null
           && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private bool TryAbortInterrupted()
    {
        lock (gate)
        {
            if (!interruptRequested)
                return false;
            running = false;
            interruptRequested = false;
            turnAccepted = false;
        }

        Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null));
        UpdateSafePoint();
        return true;
    }

    /// <summary>權限模式不同就送 set_permission_mode 並等回應；成功（或本來就相同）回 true</summary>
    private async Task<bool> EnsurePermissionModeAsync(AgentProcess target, AgentPermissionMode mode)
    {
        lock (gate)
        {
            if (currentMode == mode)
                return true;
        }

        var requestId = NextRequestId("mode");
        var completion = new TaskCompletionSource<ClaudeControlResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
            controlWaiters[requestId] = (target, completion);
        try
        {
            await target.WriteLineAsync(ClaudeStreamRules.BuildSetPermissionModeRequest(requestId, mode));
            var finished = await Task.WhenAny(completion.Task, Task.Delay(ControlTimeout));
            var response = finished == completion.Task ? await completion.Task : null;
            if (response is not { Success: true })
                return false;
            lock (gate)
                currentMode = mode;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            lock (gate)
                controlWaiters.Remove(requestId);
        }
    }

    /// <summary>模型、強度、工作資料夾、帳戶、執行檔（路徑與換版後的檔案身分）、客製設定、帳戶委派的 MCP 改了就要重開程序（權限模式可即時切換）</summary>
    internal static bool NeedsRestart(AgentSessionSettings current, AgentSessionSettings next)
        => !string.Equals(NormalizeModel(current.Model), NormalizeModel(next.Model), StringComparison.OrdinalIgnoreCase)
           || !string.Equals(current.Effort ?? string.Empty, next.Effort ?? string.Empty, StringComparison.OrdinalIgnoreCase)
           || !string.Equals(Path.GetFullPath(current.WorkingDirectory), Path.GetFullPath(next.WorkingDirectory), StringComparison.OrdinalIgnoreCase)
           || current.UseLocalCustomizations != next.UseLocalCustomizations
           || !string.Equals(current.AccountKey, next.AccountKey, StringComparison.Ordinal)
           || !string.Equals(current.Launcher?.FileName, next.Launcher?.FileName, StringComparison.OrdinalIgnoreCase)
           || !CliBinaryIdentity.SameOrUnknown(current.Cli, next.Cli)
           || !string.Equals(current.SessionId, next.SessionId, StringComparison.Ordinal)
           || !string.Equals(current.FallbackModel ?? string.Empty, next.FallbackModel ?? string.Empty, StringComparison.OrdinalIgnoreCase)
           || !string.Equals(current.Agent ?? string.Empty, next.Agent ?? string.Empty, StringComparison.Ordinal)
           || !string.Equals(current.OutputStyle ?? string.Empty, next.OutputStyle ?? string.Empty, StringComparison.Ordinal)
           || !string.Equals(current.ResumeAt ?? string.Empty, next.ResumeAt ?? string.Empty, StringComparison.Ordinal)
           || !string.Equals(current.DelegationMcpPipeName, next.DelegationMcpPipeName, StringComparison.Ordinal)
           || current.AutoCompactTokens != next.AutoCompactTokens;

    /// <summary>「default」與空白都代表帳號預設（不帶 --model）</summary>
    internal static string? NormalizeModel(string? model)
        => string.IsNullOrWhiteSpace(model) || model.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : model;

    /// <summary>
    /// 啟動新程序（CLI 換檔空窗的暫時失敗會退避重試）。在開始讀輸出之前發 AgentProcessStartedEvent，保證排在這個程序的任何事件之前。
    /// autoTurnStart 不為 null＝系統重開：帶 RESUME 環境變數（ResumePrompt 為 null 時不帶），這個程序一啟動就把會話標成執行中（自動接續的一輪），
    /// 並在開始讀輸出之前發 AgentTurnStartedByCliEvent（接續的一輪比 initialize 回應還早開始）
    /// </summary>
    private async Task<AgentProcess> StartProcessAsync(AgentSessionSettings settings, string? resumeTranscript,
        AutoTurnStart? autoTurnStart = null)
    {
        var launcher = settings.Launcher
            ?? throw new AgentCliUnavailableException(AgentProvider.Claude, "這台電腦尚未安裝 Claude Code CLI，請到「設定 → CLI」安裝");

        // 「本對話都允許」同意過的規則與資料夾一併帶回（CLI 的 session 規則只存在上一個程序的記憶體）
        List<string> directories;
        List<string> rules;
        lock (gate)
        {
            directories = new[] { settings.ConversationDirectory }.Concat(sessionDirectories).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            rules = sessionRules.ToList();
        }

        // 延伸出來的對話第一次啟動：一律從副本分支（防呆：接回 Remote Control、控制用的程序遇到還沒分支的先停下，不會以 sid 續接一個還不存在的 session）
        bool fork = IsForkStart(settings);
        if (fork)
            resumeTranscript = settings.ForkSource;
        var options = new ClaudeLaunchOptions(
            settings.SessionId!,
            settings.SessionStarted,
            settings.PermissionMode,
            NormalizeModel(settings.Model),
            settings.Effort,
            settings.Instructions,
            directories,
            settings.UseLocalCustomizations,
            resumeTranscript,
            rules,
            FallbackModel: settings.FallbackModel,
            Agent: settings.Agent,
            OutputStyle: settings.OutputStyle,
            ResumeAt: settings.SessionStarted ? settings.ResumeAt : null,
            ForkSession: fork,
            DelegationMcpPipeName: settings.DelegationMcpPipeName,
            AutoCompactTokens: settings.AutoCompactTokens);

        // 工作資料夾由中樞負責（預設 workspace 會先建好）；使用者選的資料夾不存在就明確回報，不偷偷重建
        if (!Directory.Exists(settings.WorkingDirectory))
            throw new DirectoryNotFoundException($"工作資料夾不存在：{settings.WorkingDirectory}");
        // 隔離帳戶共用系統預設帳戶的設定：同步全域指示與連結（背景執行緒），帶上共用的記憶資料夾與 MCP
        var shared = await settings.PrepareSharedAsync();
        options = options with { AutoMemoryDirectory = shared.ClaudeMemoryDirectory, McpConfigFile = shared.ClaudeMcpConfigFile };
        var configDirectory = ClaudeStreamRules.ResolveConfigDirectory(settings.ConfigureEnvironment);
        var resumePrompt = autoTurnStart?.ResumePrompt;
        if (BeforeProcessStartForTest != null)
            await BeforeProcessStartForTest();
        var started = await AgentProcess.StartWithRetryAsync(
            launcher,
            ClaudeStreamRules.BuildArguments(options),
            settings.WorkingDirectory,
            env =>
            {
                // 帳戶環境（剝掉 API Key 與所有 CLAUDE*）之後才加：RESUME 變數只在系統重開時帶
                settings.ConfigureEnvironment(env);
                ClaudeStreamRules.ApplyLaunchEnvironment(env, settings.Effort, resumePrompt);
                // 委派要等子對話做完（含使用者核准）：MCP 工具的逾時設成同一個上限
                if (settings.DelegationMcpPipeName != null)
                    env["MCP_TOOL_TIMEOUT"] = ((long)DelegationMcpRules.ToolTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            });

        started.LineReceived += OnLine;
        started.Exited += OnExited;
        lock (gate)
        {
            if (disposed)
            {
                started.Dispose();
                throw new ObjectDisposedException(nameof(ClaudeAgentSession));
            }

            process = started;
            // 新的 session（不是 --resume，也不是從別的逐字稿延伸的分支：那會帶恢復的舊用量）：modelUsage 從 0 起算，第一則 result 也能補看不到的呼叫
            parser = new ClaudeStreamParser { ApiKeyNoticeShown = parser.ApiKeyNoticeShown, FreshSession = !settings.SessionStarted && !fork };
            processSettings = settings;
            processConfigDirectory = configDirectory;
            // 分支寫在目前工作資料夾的專案資料夾、以給的 sid 命名（實測 E-4），不是副本那份
            processTranscript = (fork ? null : resumeTranscript) ?? ClaudeStreamRules.TranscriptPath(configDirectory, settings.SessionId!, settings.WorkingDirectory);
            EnsureSubagentTailLocked();
            processStartedAt = DateTimeOffset.UtcNow;
            reportedGoal = null;
            currentMode = settings.PermissionMode;
            initSeen = false;
            resumedProcess = settings.SessionStarted;
            pendingRequests.Clear();
            ResetProcessStateLocked();
            // 啟動期間設定頁切換過就照切換後的值（settings 是送出或重開請求當時的）
            remoteControlOptions = hasRemoteControlOverride ? remoteControlOverride : settings.RemoteControl;
            hasRemoteControlOverride = false;
            remoteControlOverride = null;
            sessionTitle = titleOverride ?? settings.Title;
            titleOverride = null;
            if (autoTurnStart != null)
            {
                // 自動接續的一輪：這個程序一啟動就算執行中（CLI 自己開始，不是 SendAsync 開始的）；不能拿使用者上一則訊息重送
                running = true;
                turnGeneration++;
                interruptRequested = false;
                interruptSent = false;
                payloadSent = true;
                turnAccepted = false;
                transcriptBaseline = autoTurnStart.TranscriptBaseline;
                lastInput = null;
                lastSettings = settings;
                // 接續的是被打斷的那一輪：result 的 user_message_uuid 是那一輪的訊息，不拿來比對
                remoteTurn = false;
                turnUuid = null;
                turnUuidStarted = false;
                turnUuidQueued = false;
                autoTurn = new AutoTurnWatch(started);
            }
        }

        Raise(new AgentProcessStartedEvent(started.InstanceId, settings.Cli));
        if (autoTurnStart != null)
            Raise(new AgentTurnStartedByCliEvent(AgentAutoTurnKind.ResumeInterruptedTurn));
        started.BeginReading();
        lock (gate)
            capabilityReplies = (started, null, null);
        _ = WriteQuietlyAsync(started, ClaudeStreamRules.BuildInitializeRequest(NextRequestId("init")));
        // 帳戶能不能用 ultracode（模型清單要不要列；HandleCapabilityReply）。舊版不認得 get_settings 時回 error，當成不能用
        _ = WriteQuietlyAsync(started, ClaudeStreamRules.BuildGetSettingsRequest(NextRequestId("caps")));
        // 緊接著開 Remote Control（實測 initialize 回應前、一輪進行中送都可以，不影響接著寫入的訊息）
        RequestRemoteControl(started);
        // 工作階段名稱跟著對話標題（續接清單也看得到）。remote_control 帶的 name 不算工作階段的名稱（實測開啟時不送 session_title_changed，
        // 接回時也不一定改名），所以 Remote Control 連上後另外再送一次（HandleRemoteControlResponse）
        SendSessionTitle(started);
        return started;
    }

    /// <summary>以目前的工作階段名稱送 rename_session 給這個程序（沒有名稱不送）；回應不等（失敗只是名稱沒改，CLI 的錯誤回應沒有人等就略過）</summary>
    private void SendSessionTitle(AgentProcess target)
    {
        string? line;
        lock (gate)
        {
            if (disposed || !ReferenceEquals(process, target))
                return;
            line = ClaudeStreamRules.BuildRenameSessionRequest(NextRequestId("rn"), sessionTitle);
        }
        if (line != null)
            _ = WriteQuietlyAsync(target, line);
    }

    public Task RenameAsync(string title)
    {
        AgentProcess target;
        lock (gate)
        {
            if (disposed)
                return Task.CompletedTask;
            if (process == null || process.HasExited)
            {
                // 沒有程序（含正在啟動、重開）：記下來，接下來啟動的程序照它
                titleOverride = title;
                return Task.CompletedTask;
            }
            sessionTitle = title;
            target = process;
        }
        SendSessionTitle(target);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 照 remoteControlOptions 送 remote_control 給這個程序（開啟或關閉），回應由 HandleRemoteControlResponse 處理。
    /// 開啟時接回最近的遠端工作階段：這個會話開過、而且是同一個帳戶就用自己記的，否則用設定帶來的（中樞記在對話裡的）；
    /// reattach=false＝接回失敗後重送、開新的
    /// </summary>
    private void RequestRemoteControl(AgentProcess target, bool reattach = true)
    {
        string line;
        lock (gate)
        {
            if (disposed || !ReferenceEquals(process, target))
                return;
            var options = remoteControlOptions;
            // 關閉：這個程序沒送過開啟就不必送
            if (options == null && !remoteControlEnableSent)
                return;
            if (options != null)
            {
                var previous = remoteBridgeSessionId != null && string.Equals(remoteBridgeAccountKey, processSettings?.AccountKey, StringComparison.Ordinal)
                    ? remoteBridgeSessionId
                    : options.ReattachSessionId;
                options = options with { ReattachSessionId = reattach ? previous : null };
            }
            remoteControlRequestId = NextRequestId("rc");
            remoteControlRequestEnables = options != null;
            remoteControlRequestReattached = !string.IsNullOrWhiteSpace(options?.ReattachSessionId);
            remoteControlEnableSent = options != null;
            // 關閉：送出就不再算開著（閒置回收照常）
            if (options == null)
                remoteControlActive = false;
            line = ClaudeStreamRules.BuildRemoteControlRequest(remoteControlRequestId, options);
        }
        _ = WriteQuietlyAsync(target, line);
    }

    /// <summary>remote_control 的回應：開啟成功就記下遠端工作階段並通知中樞；失敗時帶了接回的 id 就不接回重送一次，否則提示一次</summary>
    private void HandleRemoteControlResponse(AgentProcess source, ClaudeControlResponse response)
    {
        AgentEvent? raise = null;
        bool retry = false;
        lock (gate)
        {
            // 只處理目前程序最新的請求：之後又切換過（或換了程序）的舊回應不算數
            if (!ReferenceEquals(process, source) || response.RequestId != remoteControlRequestId)
                return;
            remoteControlRequestId = null;
            // 關閉：送出時已不算開著；失敗也只能等程序結束
            if (!remoteControlRequestEnables)
                return;
            if (response.Success)
            {
                remoteControlActive = true;
                var (bridge, url) = ClaudeStreamRules.ParseRemoteControlResponse(response.Response);
                if (bridge != null)
                {
                    remoteBridgeSessionId = bridge;
                    remoteBridgeAccountKey = processSettings?.AccountKey;
                    raise = new AgentRemoteControlEvent(bridge, url);
                }
            }
            else if (remoteControlRequestReattached)
            {
                // 實測接回不存在的 id 會直接開新的、不會失敗；萬一失敗（例如工作階段屬於別的帳戶），不接回再送一次
                retry = true;
            }
            else
            {
                remoteControlActive = false;
                if (!remoteControlNoticeShown)
                {
                    remoteControlNoticeShown = true;
                    raise = new AgentRemoteControlFailedEvent(ClaudeStreamRules.DescribeRemoteControlFailure(response.Error));
                }
            }
        }

        if (!response.Success)
            Log.Warn($"Claude Remote Control 開啟失敗：{response.Error}");
        if (retry)
            RequestRemoteControl(source, reattach: false);
        // 開啟成功（含接回原本的遠端工作階段）：照對話標題再命名一次，claude.ai／Claude App 上的名稱一定跟上
        if (response.Success)
            SendSessionTitle(source);
        if (raise != null)
            Raise(raise);
    }

    /// <summary>system/bridge_state：遠端連線的狀態改變（開著的才追蹤）；斷線且不會自己恢復時提示一次</summary>
    private void HandleBridgeState(AgentProcess source, ClaudeParseResult result)
    {
        AgentRemoteControlFailedEvent? notice = null;
        lock (gate)
        {
            if (!ReferenceEquals(process, source) || remoteControlOptions == null)
                return;
            switch (ClaudeStreamRules.ClassifyBridgeState(result.BridgeState, result.BridgeFailureKind))
            {
                case ClaudeBridgeChange.Connected:
                    remoteControlActive = true;
                    break;
                case ClaudeBridgeChange.Lost:
                    remoteControlActive = false;
                    break;
                case ClaudeBridgeChange.LostForGood:
                    remoteControlActive = false;
                    if (!remoteControlNoticeShown)
                    {
                        remoteControlNoticeShown = true;
                        notice = new AgentRemoteControlFailedEvent(ClaudeStreamRules.DescribeBridgeLost(result.BridgeState, result.BridgeDetail));
                    }
                    break;
            }
        }

        if (result.BridgeState is "failed" or "policy_disabled")
            Log.Warn($"Claude Remote Control 狀態 {result.BridgeState}（{result.BridgeFailureKind}）：{result.BridgeDetail}");
        if (notice != null)
            Raise(notice);
    }

    public Task SetRemoteControlAsync(AgentRemoteControlOptions? options)
    {
        AgentProcess target;
        lock (gate)
        {
            if (disposed)
                return Task.CompletedTask;
            if (process == null || process.HasExited)
            {
                // 沒有程序（含正在啟動、重開）：記下來，接下來啟動的程序照它
                hasRemoteControlOverride = true;
                remoteControlOverride = options;
                return Task.CompletedTask;
            }
            bool wanted = remoteControlOptions != null;
            remoteControlOptions = options;
            // 已經是要的狀態就不重送：要開而且已開著或開啟的請求還在途中、要關而且本來就沒開。
            // 要開但上次開不起來（沒開著、也沒有請求在途中）就再試一次
            if (options != null && wanted && (remoteControlActive || (remoteControlRequestId != null && remoteControlRequestEnables)))
                return Task.CompletedTask;
            if (options == null && !wanted)
                return Task.CompletedTask;
            target = process;
        }
        RequestRemoteControl(target);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 沒有程序時以 --resume 啟動程序，只送 initialize 與 remote_control（帶接回的 id），不送訊息、不開始一輪、不耗額度
    /// （實測見 docs/protocol-notes.md「Remote Control」的接回）。app 更新排空時關掉的、開著 Remote Control 的程序由新版重開（設計 9.2、9.5）。
    /// 換過工作資料夾的對話不在這裡開：送出時才開，訊息前要附新資料夾的說明（SendAsync）
    /// </summary>
    public async Task<bool> ReattachRemoteControlAsync(AgentSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        TaskCompletionSource done;
        lock (gate)
        {
            if (disposed || running || restarting || restartCompletion != null || reattachCompletion != null || stoppedTurn != null
                || settings.RemoteControl == null || !settings.SessionStarted || string.IsNullOrWhiteSpace(settings.SessionId)
                || (process != null && !process.HasExited))
                return false;
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reattachCompletion = done;
            reattachCancelled = false;
            // 這次的設定比之前沒有程序時記下的 Remote Control 切換新（同 SendAsync）
            hasRemoteControlOverride = false;
            remoteControlOverride = null;
        }

        try
        {
            // 找逐字稿也截掉寫到一半的尾行（同送出時開新程序）
            if (await FindResumeTranscriptAsync(settings) != null)
                return false;
            var started = await StartProcessAsync(settings, resumeTranscript: null);
            AgentProcess? toClose = null;
            bool cancelled;
            lock (gate)
            {
                cancelled = reattachCancelled;
                if (cancelled && ReferenceEquals(process, started))
                {
                    toClose = started;
                    process = null;
                    ResetProcessStateLocked();
                }
            }
            if (toClose != null)
                await CloseProcessAsync(toClose);
            if (cancelled)
                return false;
            Log.Info("Claude 對話已重開程序、接回 Remote Control（沒有送訊息）");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Claude 對話重開程序接回 Remote Control 失敗：{ex.Message}");
            return false;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(reattachCompletion, done))
                    reattachCompletion = null;
                reattachCancelled = false;
            }
            done.TrySetResult();
        }
    }

    public async Task<AgentSideAnswer> AskSideQuestionAsync(string question, AgentSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var (target, error) = await EnsureProcessForControlAsync(settings);
        if (target == null)
            return new AgentSideAnswer(null, error);
        var response = await RequestControlAsync(target, "btw", id => ClaudeMcpRules.BuildSideQuestionRequest(id, question), SideQuestionTimeout);
        if (response == null)
            return new AgentSideAnswer(null, "Claude 沒有回應（逾時或程序已結束）");
        if (!response.Success)
            return new AgentSideAnswer(null, string.IsNullOrWhiteSpace(response.Error) ? "Claude 沒有回答" : response.Error.Trim());
        return ClaudeMcpRules.ParseSideAnswer(response.Response) is { } text
            ? new AgentSideAnswer(text, null)
            : new AgentSideAnswer(null, "Claude 沒有給答案");
    }

    public async Task<AgentMcpActionResult> McpActionAsync(string server, AgentMcpAction action, AgentSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var (target, error) = await EnsureProcessForControlAsync(settings);
        if (target == null)
            return new AgentMcpActionResult(false, error);
        var response = await RequestControlAsync(target, "mcpa", id => ClaudeMcpRules.BuildActionRequest(id, server, action), McpActionTimeout);
        if (response == null)
            return new AgentMcpActionResult(false, "Claude 沒有回應（逾時或程序已結束）");
        if (!response.Success)
            return new AgentMcpActionResult(false, string.IsNullOrWhiteSpace(response.Error) ? "Claude 沒有接受這個操作" : response.Error.Trim());
        if (action != AgentMcpAction.Authenticate)
            return new AgentMcpActionResult(true, null);
        return new AgentMcpActionResult(true, null, ClaudeMcpRules.ParseAuthUrl(response.Response));
    }

    public async Task<AgentMcpStatusResult> GetMcpStatusAsync(AgentSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var (target, error) = await EnsureProcessForControlAsync(settings);
        if (target == null)
            return new AgentMcpStatusResult(null, error);
        // 遠端的伺服器剛起來時是 pending：每秒再問，到都有結果或 McpPendingLimit 為止（還是 pending 的照實回）
        var deadline = DateTime.UtcNow + McpPendingLimit;
        while (true)
        {
            var response = await RequestControlAsync(target, "mcpq", ClaudeMcpRules.BuildStatusRequest).ConfigureAwait(false);
            if (response == null)
                return new AgentMcpStatusResult(null, "Claude 沒有回應（逾時或程序已結束）");
            // 舊版不認得 mcp_status 時回 error
            if (!response.Success || ClaudeMcpRules.ParseStatus(response.Response) is not { } servers)
                return new AgentMcpStatusResult(null, string.IsNullOrWhiteSpace(response.Error) ? "這個版本的 Claude 不回報 MCP 狀態" : response.Error.Trim());
            if (!ClaudeMcpRules.AnyPending(servers) || DateTime.UtcNow >= deadline)
                return new AgentMcpStatusResult(servers, null);
            await Task.Delay(McpPendingPollDelay).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 不開始一輪、但要用到程序的操作（旁支提問、MCP）：有活著的程序就用它；沒有就照 ReattachRemoteControlAsync 以 --resume 開一個
    /// （還沒建立 session 的以 --session-id 開；不送訊息、不開始一輪、不耗額度），之後照閒置規則回收。正在重開、接回、送出時剛好還沒有程序、
    /// 換過工作資料夾（下一則訊息前要附新資料夾的說明，送出時才開程序）回 null 與原因
    /// </summary>
    private async Task<(AgentProcess? Process, string? Error)> EnsureProcessForControlAsync(AgentSessionSettings settings)
    {
        TaskCompletionSource done;
        lock (gate)
        {
            if (disposed)
                return (null, "對話已經關閉");
            if (restarting || restartCompletion != null || reattachCompletion != null)
                return (null, "Claude 正在重新啟動，請稍後再試");
            if (process is { HasExited: false } live)
                return (live, null);
            if (running || stoppedTurn != null)
                return (null, "Claude 正在啟動，請稍後再試");
            if (string.IsNullOrWhiteSpace(settings.SessionId))
                return (null, "這個對話還沒有 Claude 的工作階段");
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reattachCompletion = done;
            reattachCancelled = false;
            hasRemoteControlOverride = false;
            remoteControlOverride = null;
        }

        try
        {
            if (await FindResumeTranscriptAsync(settings) != null)
                return (null, IsForkStart(settings) ? "這個延伸出來的對話還沒開始：先送一則訊息再試" : "工作資料夾換過了，先送一則訊息再試");
            var started = await StartProcessAsync(settings, resumeTranscript: null);
            AgentProcess? toClose = null;
            bool cancelled;
            lock (gate)
            {
                cancelled = reattachCancelled;
                if (cancelled && ReferenceEquals(process, started))
                {
                    toClose = started;
                    process = null;
                    ResetProcessStateLocked();
                }
            }
            if (toClose != null)
                await CloseProcessAsync(toClose);
            if (cancelled)
                return (null, "Claude 的程序被關掉了，請再試一次");
            return (started, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"Claude 對話為旁支提問或 MCP 操作開程序失敗：{ex.Message}");
            return (null, $"Claude 的程序開不起來：{ex.Message}");
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(reattachCompletion, done))
                    reattachCompletion = null;
                reattachCancelled = false;
            }
            done.TrySetResult();
        }
    }

    /// <summary>換了程序（或程序沒了）：清掉上一個程序的版本、工具與背景工作狀態</summary>
    private void ResetProcessStateLocked()
    {
        pendingSteer = null;
        remoteControlActive = false;
        remoteControlRequestId = null;
        remoteControlEnableSent = false;
        runningVersion = null;
        toolRunning = false;
        backgroundTaskIds = new List<string>();
        backgroundTools = Array.Empty<AgentBackgroundTool>();
        cliOwnTurn = false;
        awaitingQueuedTurn = false;
        notificationTurnSince = 0;
        lastToolResultId = null;
        lastToolResultAt = 0;
        commandUuids.Clear();
        commandOrder.Clear();
        // 新程序沒有舊程序的佇列
        turnUuidQueued = false;
        remoteTurn = false;
        cliTurn = false;
    }

    /// <summary>stdin 的使用者訊息；prefix＝附在訊息最前面給代理的說明（例如換了工作資料夾）、uuid＝這則訊息的 uuid</summary>
    private static string BuildPayload(AgentTurnInput input, string? prefix, string uuid)
    {
        var images = new List<(string MediaType, string Base64)>();
        long total = 0;
        int skipped = 0;
        foreach (var path in input.Attachments.SelectMany(a => a.ModelImagePaths))
        {
            var mediaType = AgentAttachmentRules.GetClaudeImageMediaType(path);
            if (mediaType == null || !File.Exists(path))
                continue;
            var length = new FileInfo(path).Length;
            if (images.Count >= MaxImagesPerMessage || total + length > MaxImageBytesPerMessage)
            {
                skipped++;
                continue;
            }
            images.Add((mediaType, Convert.ToBase64String(File.ReadAllBytes(path))));
            total += length;
        }

        var text = (prefix ?? string.Empty) + (input.Text ?? string.Empty) + AgentAttachmentRules.BuildAttachmentNote(input.Attachments);
        if (skipped > 0)
            text += $"\n（另有 {skipped} 張圖片超過單則訊息上限未直接附上，可用 Read 工具讀取上列路徑）";
        return ClaudeStreamRules.BuildUserMessage(text, images, uuid);
    }

    private void OnLine(AgentProcess source, string line)
    {
        ClaudeStreamParser currentParser;
        lock (gate)
        {
            if (!ReferenceEquals(source, process))
                return;
            currentParser = parser;
        }

        if (!line.TrimStart().StartsWith('{'))
            source.AppendStderr(line);

        // 一輪結束：先把子代理逐字稿讀完，modelUsage 的差才不會把已在逐字稿裡的子代理用量再補一次
        bool subagentLine = ClaudeStreamRules.IsSubagentActivityLine(line);
        if (subagentLine || ClaudeStreamRules.IsResultLine(line))
            PollSubagentUsage(currentParser, force: !subagentLine || ClaudeStreamRules.IsSubagentEndLine(line));

        var result = currentParser.Parse(line);
        foreach (var ev in result.Events)
        {
            if (ev is AgentSessionStartedEvent sessionStarted)
            {
                lock (gate)
                {
                    initSeen = true;
                    // 回溯（--resume-session-at＋--fork-session）分支出新的 session：之後的逐字稿寫在新的 id；
                    // 中樞收到這個事件會改記新的 id 並清掉 ResumeAt，程序的設定跟著改，下一則訊息才不會又重開
                    if (processSettings is { } ps && !string.IsNullOrWhiteSpace(sessionStarted.SessionId)
                        && !string.Equals(ps.SessionId, sessionStarted.SessionId, StringComparison.Ordinal) && ps.ResumeAt != null)
                    {
                        processSettings = ps with { SessionId = sessionStarted.SessionId, ResumeAt = null };
                        if (processConfigDirectory != null)
                        {
                            processTranscript = ClaudeStreamRules.TranscriptPath(processConfigDirectory, sessionStarted.SessionId, ps.WorkingDirectory);
                            EnsureSubagentTailLocked();
                        }
                    }
                    // 延伸出來的對話分支好了：之後這個程序（與重開）照常以自己的 session 續接，不再從副本分支（中樞同時清掉 meta 的來源）
                    if (processSettings is { ForkSource: not null } fs)
                        processSettings = fs with { ForkSource = null };
                }
            }
            else if (ev is AgentPermissionModeChangedEvent changed)
            {
                // 以 CLI 回報的模式為準（代理自己進出規劃模式等），之後要切換時才不會拿過期的值比對而略過
                lock (gate)
                    currentMode = changed.Mode;
            }
            Raise(ev);
        }

        var fromOther = ObserveCommands(source, result);

        ObserveLine(source, currentParser, result, fromOther);

        if (result.TurnCompleted != null)
            ScheduleGoalCheck(source);

        if (result.ControlRequest != null)
            HandleControlRequest(source, result.ControlRequest);
        if (result.ControlResponse != null)
            HandleControlResponse(source, result.ControlResponse);
        if (result.BridgeState != null)
            HandleBridgeState(source, result);
        if (result.CancelledRequestId != null)
            HandleCancelledRequest(result.CancelledRequestId);
        if (result.TurnCompleted != null)
        {
            HandleTurnCompleted(source, result.TurnCompleted, fromOther);
            RequestContextUsage(source);
        }
        UpdateSafePoint();
    }

    /// <summary>
    /// 每輪的 result 之後問 CLI 的上下文用量（get_context_usage，本機算、不連網）：補上自動壓縮門檻與 CLI 實際採用的視窗。
    /// 不等回應，回應在 HandleControlResponse（請求 id 以 ctx- 開頭）處理；程序已換掉或結束就不問
    /// </summary>
    private void RequestContextUsage(AgentProcess source)
    {
        lock (gate)
        {
            if (!ReferenceEquals(source, process))
                return;
        }
        if (!source.HasExited)
            _ = WriteQuietlyAsync(source, ClaudeStreamRules.BuildContextUsageRequest(NextRequestId("ctx")));
    }

    /// <summary>
    /// 佇列裡的指令（app 的訊息、遠端送來的訊息）：記下看過的 uuid、這一輪的訊息排隊或開始了沒有；遠端送來的訊息加成事件，
    /// 閒置時它開始新的一輪（當成執行中：可以停止、程序意外結束時回報失敗，換版等它結束）。
    /// 回傳這一行的 result 是不是別一輪的：CLI 自己的一輪（origin 不是使用者），或另一則訊息那一輪（這一輪的訊息還排在後面）
    /// </summary>
    private bool ObserveCommands(AgentProcess source, ClaudeParseResult result)
    {
        bool fromOther = result.TurnFromOtherOrigin;
        if (result.CommandUuid == null && result.ReplayUuid == null && result.TurnCompleted == null)
            return fromOther;

        bool reinterrupt = false;
        int generation = 0;
        AgentRemoteUserMessageEvent? remoteEvent = null;
        lock (gate)
        {
            if (!ReferenceEquals(source, process))
                return fromOther;

            bool ownCommand = result.CommandUuid != null && string.Equals(result.CommandUuid, turnUuid, StringComparison.OrdinalIgnoreCase);
            if (result.CommandUuid is { } command)
                RememberCommandLocked(command);
            if (ownCommand && result.CommandLifecycleState == "queued" && !turnUuidStarted)
            {
                // 排在別的指令後面（遠端那一輪、CLI 自己的一輪）：開始之前的活動都不是這則的，不算 CLI 接手了它
                turnUuidQueued = true;
                turnAccepted = false;
            }
            bool startedNow = !turnUuidStarted
                              && ((ownCommand && result.CommandLifecycleState == "started")
                                  || (result.ReplayUuid != null && string.Equals(result.ReplayUuid, turnUuid, StringComparison.OrdinalIgnoreCase)));
            if (startedNow)
            {
                turnUuidStarted = true;
                // 排隊時按了停止：中斷停的是前面那一輪（CLI 回 still_queued），接著開始的這則要再停一次
                reinterrupt = running && interruptRequested && interruptSent && payloadSent;
                generation = turnGeneration;
            }

            if (result.RemoteUserMessage is { } message)
            {
                // 只記遠端送來的：app 自己訊息的回送若帶了 CLI 另給的 uuid，記下來會把這一輪的 result 當成別一輪的
                RememberCommandLocked(message.Uuid);
                // 閒置時送來的才開始新的一輪；執行中（併進這一輪，或排在 app 這則前面）與重開期間只顯示
                bool startsTurn = !running && !restarting && autoTurn == null;
                // 回送之前的 system/init 已把這一輪當成 CLI 自己開的（cliTurn，見 ObserveLine）：其實是這則開始的（或併進了背景工作通知那一輪），
                // 改記成遠端的一輪。同一輪，停止、看門狗照舊
                bool claimsCliTurn = running && cliTurn && !restarting && autoTurn == null;
                if (claimsCliTurn)
                {
                    cliTurn = false;
                    remoteTurn = true;
                    turnUuid = message.Uuid;
                    turnUuidStarted = true;
                    turnUuidQueued = false;
                    cliOwnTurn = false;
                }
                else if (startsTurn)
                {
                    running = true;
                    remoteTurn = true;
                    cliTurn = false;
                    turnGeneration++;
                    interruptRequested = false;
                    interruptSent = false;
                    payloadSent = true;
                    payloadWriting = false;
                    turnAccepted = true;
                    awaitingQueuedTurn = false;
                    transcriptBaseline = -1;
                    lastInput = null;
                    lastSettings = processSettings;
                    turnUuid = message.Uuid;
                    turnUuidStarted = true;
                    turnUuidQueued = false;
                    // 不算背景工作（換版顯示與接續說明會說錯）：到它的 result 為止由 remoteTurn 擋住安全點。
                    // system/init 在回送之前，閒置時已被當成 CLI 自己的一輪；併進背景工作通知那一輪時也一樣
                    cliOwnTurn = false;
                }
                remoteEvent = new AgentRemoteUserMessageEvent(message.Text, message.ImageCount, message.FileCount, startsTurn || claimsCliTurn);
            }

            if (result.TurnCompleted != null)
            {
                // 這一輪的訊息已併進 CLI 自己的一輪（背景工作完成的通知那一輪）：那個 result 就是這一輪的
                bool ownStarted = turnUuid != null && turnUuidStarted;
                // 這一輪就是 CLI 自己開的（app 還沒接手）：它的 result 一定是這一輪的，不看 origin
                bool cliTurnResult = running && cliTurn;
                fromOther = !cliTurnResult
                            && ((result.TurnFromOtherOrigin && !ownStarted)
                                || (running && ClaudeStreamRules.IsResultForOtherTurn(turnUuid, turnUuidStarted, result.UserMessageUuid, commandUuids)));
            }
        }

        if (remoteEvent != null)
            Raise(remoteEvent);
        if (reinterrupt)
        {
            _ = WriteQuietlyAsync(source, ClaudeStreamRules.BuildInterruptRequest(NextRequestId("interrupt")));
            StartInterruptWatchdog(source, generation);
        }
        return fromOther;
    }

    /// <summary>記下看過的指令 uuid；超過上限時先進先出淘汰最舊的（正在等 result 的那則是最近的，不會被淘汰）</summary>
    private void RememberCommandLocked(string uuid)
    {
        if (!commandUuids.Add(uuid))
            return;
        commandOrder.Enqueue(uuid);
        while (commandOrder.Count > MaxCommandUuids)
            commandUuids.Remove(commandOrder.Dequeue());
    }

    /// <summary>
    /// 一行處理完後更新熱更新要用的狀態：程序回報的版本、工具與背景工作、CLI 是否已接手這一輪、閒置時 CLI 自己開的輪次、
    /// 自動接續是否已開始（command_lifecycle started 或這一輪的任何活動）與新程序的 initialize 回應時間
    /// </summary>
    /// <param name="fromOther">這一行的 result 是別一輪的：CLI 自己的一輪或另一則訊息那一輪（ObserveCommands）</param>
    private void ObserveLine(AgentProcess source, ClaudeStreamParser lineParser, ClaudeParseResult result, bool fromOther)
    {
        AgentRunningVersionEvent? versionEvent = null;
        AgentIdleTurnStartedEvent? idleTurnEvent = null;
        long notificationStamp = 0;
        // 背景工作對到的工具（解析器只在讀取執行緒上改，這裡先取快照再放進鎖裡）
        var tools = lineParser.BackgroundTools;
        string? remeasurePath = null;
        int remeasureGeneration = 0;
        bool remeasure = false;
        lock (gate)
        {
            if (!ReferenceEquals(source, process))
                return;

            if (result.CliVersion != null)
            {
                var version = CliVersionRules.ParseVersion(result.CliVersion) ?? result.CliVersion;
                if (!string.Equals(runningVersion, version, StringComparison.Ordinal))
                {
                    runningVersion = version;
                    versionEvent = new AgentRunningVersionEvent(source.InstanceId, version);
                }
            }

            toolRunning = lineParser.ToolRunning;
            if (!backgroundTaskIds.SequenceEqual(lineParser.BackgroundTaskIds, StringComparer.Ordinal))
                backgroundTaskIds = lineParser.BackgroundTaskIds.ToList();
            if (!backgroundTools.SequenceEqual(tools))
                backgroundTools = tools;
            if (lineParser.LastToolResultId != null && !string.Equals(lineParser.LastToolResultId, lastToolResultId, StringComparison.Ordinal))
            {
                lastToolResultId = lineParser.LastToolResultId;
                lastToolResultAt = Stopwatch.GetTimestamp();
            }

            if (running)
            {
                if (awaitingQueuedTurn)
                {
                    // CLI 自己的一輪結束後才輪到使用者的訊息：它的 system/init 才是這一輪的開始，從這裡重新量逐字稿（之後的活動才算接手）
                    if (result.TurnInit)
                    {
                        awaitingQueuedTurn = false;
                        remeasure = true;
                        remeasurePath = processTranscript;
                        remeasureGeneration = turnGeneration;
                    }
                }
                else if (result.TurnActivity && (payloadSent || payloadWriting) && autoTurn == null && !(turnUuidQueued && !turnUuidStarted))
                {
                    // 這則還排在別的指令後面時（command_lifecycle queued）的活動是前面那一輪的；沒有 command_lifecycle 的舊版照舊
                    turnAccepted = true;
                }
            }
            else if ((result.TurnInit || result.TurnActivity) && !restarting)
            {
                // 閒置時 CLI 自己開了一輪（背景工作完成的通知）：到它的 result 為止都還不能重開
                cliOwnTurn = true;
                // 那一輪的開始（system/init）：會話標成執行中，不然畫面上回覆一直出來、對話卻不像在執行，也停不下來。
                // 只有活動、沒有 init 的不算（例如背景的子代理要求核准，之後不一定有 result 收尾）
                if (result.TurnInit && !disposed)
                {
                    StartCliTurnLocked();
                    idleTurnEvent = new AgentIdleTurnStartedEvent(AfterBackgroundTask: notificationTurnSince != 0);
                }
            }

            if (result.TaskNotificationStatus is { } status && status is not ("stopped" or "killed"))
            {
                // 背景工作完成：CLI 接著會自己開一輪告訴模型（E-11 約 0.08 秒後），那一輪結束前都不算安全點
                notificationTurnSince = notificationStamp = Stopwatch.GetTimestamp();
            }

            if (result.TurnCompleted != null)
            {
                // 輪次依序處理：任何一個 result 都表示 CLI 自己開的那一輪已經結束（使用者在那一輪進行中送出時，result 到的時候會話已在執行中）
                cliOwnTurn = false;
                if (result.TurnFromOtherOrigin)
                    notificationTurnSince = 0;
                if (fromOther && running && (payloadSent || payloadWriting) && autoTurn == null)
                {
                    // 使用者的訊息排在 CLI 自己這一輪（或遠端送來的訊息那一輪）後面還沒處理：這一輪的串流不算 CLI 接手了它，
                    // 逐字稿這段期間的變長也不是它。等使用者那一輪開始再算（不然重開會在使用者的訊息寫進逐字稿之前殺樹，訊息就不見了）
                    turnAccepted = false;
                    awaitingQueuedTurn = true;
                }
                if (result.ResumeReason != null)
                    lastResumeReason = result.ResumeReason;
            }

            if (autoTurn is { } watch && ReferenceEquals(watch.Process, source) && !watch.Evidence)
            {
                if (result.ControlResponse is { } response && response.RequestId.StartsWith("init-", StringComparison.Ordinal))
                    watch.InitResponseAt = Stopwatch.GetTimestamp();
                // 自動接續已開始：command_lifecycle started（比 initialize 回應還早）、這一輪的任何活動、或帶 resume_reason 的 result
                // （續接時回報孤兒背景工作的那一輪只有 system/init 與 num_turns 0 的 result，不算）
                if (result.CommandLifecycleState == "started" || result.TurnActivity || (result.TurnCompleted != null && result.ResumeReason != null))
                {
                    watch.Evidence = true;
                    turnAccepted = true;
                }
            }
        }

        if (remeasure)
        {
            // 在讀取執行緒上量（不在鎖內做 I/O）：量完之前這個程序的下一行不會被處理，這一輪也還不算接手
            var length = MeasureTranscript(remeasurePath);
            lock (gate)
            {
                if (ReferenceEquals(source, process) && running && turnGeneration == remeasureGeneration)
                    transcriptBaseline = length;
            }
        }
        if (versionEvent != null)
            Raise(versionEvent);
        if (idleTurnEvent != null)
            Raise(idleTurnEvent);
        if (notificationStamp != 0)
            ScheduleNotificationTurnTimeout(source, notificationStamp);
    }

    /// <summary>
    /// 閒置時 CLI 自己開的一輪開始了（system/init）：同閒置時遠端送來的訊息，會話標成執行中（可停止、app 的送出接手、程序意外結束回報失敗、
    /// 換版等它結束）。這一輪沒有 app 的訊息：不比對 uuid、不拿上一則重送；cliOwnTurn 照舊（背景工作通知那一輪到 result 為止不是安全點）
    /// </summary>
    private void StartCliTurnLocked()
    {
        running = true;
        cliTurn = true;
        remoteTurn = false;
        turnGeneration++;
        interruptRequested = false;
        interruptSent = false;
        payloadSent = true;
        payloadWriting = false;
        turnAccepted = true;
        awaitingQueuedTurn = false;
        transcriptBaseline = -1;
        lastInput = null;
        lastSettings = processSettings;
        turnUuid = null;
        turnUuidStarted = false;
        turnUuidQueued = false;
    }

    /// <summary>背景工作完成的通知一直沒等到 CLI 自己開的那一輪（可能已併進使用者的輪次）：逾時就不再當成背景工作</summary>
    private void ScheduleNotificationTurnTimeout(AgentProcess source, long stamp)
    {
        _ = Task.Delay(NotificationTurnTimeout).ContinueWith(_ =>
        {
            bool cleared = false;
            lock (gate)
            {
                if (ReferenceEquals(process, source) && notificationTurnSince == stamp && !cliOwnTurn)
                {
                    notificationTurnSince = 0;
                    cleared = true;
                }
            }
            if (cleared)
                UpdateSafePoint();
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 安全點的三個旗標改變時發 AgentSafePointChangedEvent（去重；在鎖內發，順序與狀態一致）。
    /// RestartAsync 回過 Deferred 而現在已是安全點時，即使旗標沒變也再發一次，讓中樞重試
    /// </summary>
    private void UpdateSafePoint()
    {
        lock (gate)
        {
            if (disposed)
                return;
            // 背景工作的清單（畫面的「等背景工作回報」）：數量或對到的工具變了才發；程序結束、關閉時 ResetProcessStateLocked 清空，這裡發一次 0
            if (backgroundTaskIds.Count != lastBackgroundCount || !backgroundTools.SequenceEqual(lastBackgroundTools))
            {
                lastBackgroundCount = backgroundTaskIds.Count;
                lastBackgroundTools = backgroundTools;
                Raise(new AgentBackgroundWorkEvent(lastBackgroundCount, lastBackgroundTools));
            }
            var now = (toolRunning, pendingRequests.Count > 0, HasBackgroundWorkLocked());
            bool nudge = safePointNudge && IsAtSafePointLocked(ignoreRestart: false);
            if (now == lastSafePoint && !nudge)
                return;
            lastSafePoint = now;
            if (nudge)
                safePointNudge = false;
            Raise(new AgentSafePointChangedEvent(now.Item1, now.Item2, now.Item3));
        }
    }

    /// <summary>CLI 撤回了還沒回應的請求：卡片改為已失效，之後按下去也不再回應</summary>
    private void HandleCancelledRequest(string requestId)
    {
        bool removed;
        lock (gate)
            removed = pendingRequests.Remove(requestId);
        if (removed)
            Raise(new AgentRequestResolvedEvent(requestId));
    }

    private void HandleControlResponse(AgentProcess source, ClaudeControlResponse response)
    {
        if (response.RequestId.StartsWith("rc-", StringComparison.Ordinal))
        {
            HandleRemoteControlResponse(source, response);
            return;
        }
        if (response.RequestId.StartsWith("ctx-", StringComparison.Ordinal))
        {
            // get_context_usage（RequestContextUsage）：舊版不認得時回 error，不理會
            bool current;
            lock (gate)
                current = ReferenceEquals(source, process);
            if (current && response.Success)
            {
                var (window, autoCompact) = ContextUsageRules.ParseClaudeContextResponse(response.Response);
                if (window != null || autoCompact != null)
                    Raise(new AgentContextUsageEvent(null, window, autoCompact));
            }
            return;
        }
        if (response.RequestId.StartsWith("caps-", StringComparison.Ordinal))
        {
            // get_settings：舊版不認得時回 error，當成不能用 ultracode
            HandleCapabilityReply(source, null, response.Success && ClaudeStreamRules.ParseUltracodeAvailable(response.Response));
            return;
        }

        TaskCompletionSource<ClaudeControlResponse?>? waiter = null;
        lock (gate)
        {
            if (controlWaiters.TryGetValue(response.RequestId, out var entry))
                waiter = entry.Completion;
        }
        waiter?.TrySetResult(response);

        if (response.RequestId.StartsWith("init-", StringComparison.Ordinal))
            HandleCapabilityReply(source, response.Success ? ClaudeStreamRules.ParseModels(response.Response) : new List<AgentModelOption>(), null);
        if (response.RequestId.StartsWith("init-", StringComparison.Ordinal) && response.Success)
        {
            var commands = ClaudeStreamRules.ParseCommands(response.Response);
            if (commands.Count > 0)
                Raise(new AgentCommandsEvent(commands));
            var agents = ClaudeStreamRules.ParseAgents(response.Response);
            var styles = ClaudeStreamRules.ParseOutputStyles(response.Response);
            if (agents.Count > 0 || styles.Count > 0)
                Raise(new AgentCustomizationsEvent(agents, styles));
            var (email, plan) = ClaudeStreamRules.ParseAccount(response.Response);
            if (email != null || plan != null)
                Raise(new AgentAccountInfoEvent(email, plan));
        }
    }

    /// <summary>initialize 的模型清單與 get_settings 的 ultracodeAvailable 都到了才回報模型清單（能用 ultracode 時支援 xhigh 的模型多列它）</summary>
    private void HandleCapabilityReply(AgentProcess source, List<AgentModelOption>? models, bool? ultracodeAvailable)
    {
        List<AgentModelOption>? ready = null;
        lock (gate)
        {
            if (capabilityReplies is not { } replies || !ReferenceEquals(replies.Process, source))
                return;
            replies = (source, models ?? replies.Models, ultracodeAvailable ?? replies.UltracodeAvailable);
            if (replies.Models is { } known && replies.UltracodeAvailable is { } available)
            {
                ready = ClaudeStreamRules.WithUltracode(known, available);
                capabilityReplies = null;
            }
            else
            {
                capabilityReplies = replies;
            }
        }
        if (ready is { Count: > 0 })
            Raise(new AgentModelsEvent(ready));
    }

    private void HandleControlRequest(AgentProcess source, ClaudeControlRequest request)
    {
        if (request.Subtype == "elicitation")
        {
            HandleElicitation(source, request);
            return;
        }
        if (request.Subtype != "can_use_tool")
        {
            _ = WriteQuietlyAsync(source, ClaudeStreamRules.BuildErrorResponse(request.RequestId, $"Clode Myriagent 不支援此控制請求：{request.Subtype}"));
            return;
        }

        if (request.ToolName == ClaudeStreamRules.AskUserQuestionTool)
        {
            var questions = ClaudeStreamRules.ParseAskUserQuestion(request.Input);
            if (questions.Count == 0)
            {
                _ = WriteQuietlyAsync(source, ClaudeStreamRules.BuildSuccessResponse(request.RequestId, ClaudeStreamRules.Allow(request.Input)));
                return;
            }

            if (!TryAddPending(source, request))
                return;
            Raise(new AgentQuestionEvent(request.RequestId, questions, AnswerAsMessage: false));
            return;
        }

        if (request.ToolName == ClaudeStreamRules.ExitPlanModeTool)
        {
            if (!TryAddPending(source, request))
                return;
            var plan = request.Input.Value<string>("plan");
            Raise(new AgentApprovalEvent(request.RequestId, "確認執行計畫", "plan", "代理已規劃好做法，請確認是否照計畫執行", plan,
                request.ToolUseId, CanAllowForSession: false, IsPlan: true));
            return;
        }

        // 只自動放行完全相同的請求：CLI 還會送來詢問的（工作資料夾外的路徑、.claude、.git 等敏感檔、指令）都要使用者決定
        bool autoAllow;
        lock (gate)
        {
            // 重開時已卸下的舊程序：不再登記它的請求（新程序接續後會以新的 request id 重新要求）
            if (!ReferenceEquals(source, process))
                return;
            autoAllow = sessionAllowedRequests.Contains(ClaudeStreamRules.RequestKey(request.ToolName, request.Input));
            if (!autoAllow)
                pendingRequests[request.RequestId] = request;
        }

        if (autoAllow)
        {
            _ = WriteQuietlyAsync(source, ClaudeStreamRules.BuildSuccessResponse(request.RequestId, ClaudeStreamRules.Allow(request.Input)));
            return;
        }

        var d = ClaudeStreamRules.DescribeTool(request.ToolName, request.Input);
        var detail = d.Detail;
        if (!string.IsNullOrWhiteSpace(request.Description) && (detail == null || !detail.Contains(request.Description, StringComparison.Ordinal)))
            detail = string.IsNullOrWhiteSpace(detail) ? request.Description : $"{request.Description}\n\n{detail}";
        Raise(new AgentApprovalEvent(request.RequestId, d.DisplayName, d.Kind, d.Title, detail, request.ToolUseId,
            CanAllowForSession: true, IsPlan: false, d.FilePath, d.Diff,
            ClaudeStreamRules.DescribeAllowForSession(request.ToolName, request.Suggestions)));
    }

    /// <summary>
    /// MCP 伺服器要使用者輸入（elicitation：工具執行到一半，MCP 伺服器要填表單或開網址）：畫成問卷卡（McpElicitationRules），
    /// 回答時以 {action, content} 回覆。有畫不出來的欄位、網址不合格時直接婉拒並提示
    /// </summary>
    private void HandleElicitation(AgentProcess source, ClaudeControlRequest request)
    {
        var elicitation = McpElicitationRules.Parse(request.Raw);
        if (elicitation.Unsupported != null)
        {
            _ = WriteQuietlyAsync(source, ClaudeStreamRules.BuildSuccessResponse(request.RequestId, McpElicitationRules.Decline()));
            Raise(new AgentNoticeEvent(McpElicitationRules.DescribeUnsupported(elicitation), IsError: false));
            return;
        }
        if (!TryAddPending(source, request))
            return;
        Raise(new AgentQuestionEvent(request.RequestId, McpElicitationRules.ToQuestions(elicitation), AnswerAsMessage: false,
            Detail: McpElicitationRules.Detail(elicitation), Source: $"MCP：{elicitation.Server}"));
    }

    /// <summary>登記等使用者回應的請求；來源已不是目前程序（重開時卸下的舊程序）回 false</summary>
    private bool TryAddPending(AgentProcess source, ClaudeControlRequest request)
    {
        lock (gate)
        {
            if (!ReferenceEquals(source, process))
                return false;
            pendingRequests[request.RequestId] = request;
            return true;
        }
    }

    private void HandleTurnCompleted(AgentProcess source, AgentTurnCompletedEvent completed, bool fromOtherOrigin)
    {
        bool resumeFailed;
        AgentTurnInput? retryInput = null;
        AgentSessionSettings? retrySettings = null;
        AgentTurnCompletedEvent final = completed;
        lock (gate)
        {
            if (!ReferenceEquals(source, process))
            {
                // 重開時卸下的舊程序在卸下之前已送出這一輪的 result：這一輪其實已經結束，由 RestartAsync 照常回報、不接續
                if (restarting && ReferenceEquals(source, restartingOld) && running && !fromOtherOrigin)
                    restartOldResult = FinishTurnLocked(completed);
                return;
            }

            // CLI 先處理了不是使用者送出的輪次（續接時的背景工作通知），使用者這則訊息還沒處理：這一輪還沒結束
            if (fromOtherOrigin && running)
                return;

            if (autoTurn is { } watch && ReferenceEquals(watch.Process, source) && !watch.Evidence)
            {
                // 新版程序還沒開始接續就回報失敗（例如找不到 session）：交給 RestartAsync 回 Failed，被打斷的那一輪由中樞收尾
                if (completed.Outcome == AgentTurnOutcome.Failed)
                {
                    watch.Failure = string.IsNullOrWhiteSpace(completed.ErrorMessage) ? "新版 Claude 程序沒有接續這一輪" : completed.ErrorMessage;
                    return;
                }
                // 沒看到其他證據就完成了一輪：也算接續了（照常結束這一輪）
                watch.Evidence = true;
            }

            // 只在這一輪還沒回報給中樞時自動改開新對話（寫入先失敗、已回報送出失敗的就不要在背景重跑）
            resumeFailed = running && resumedProcess && !initSeen && completed.Outcome == AgentTurnOutcome.Failed && lastInput != null && lastSettings != null;
            if (resumeFailed)
            {
                retryInput = lastInput;
                retrySettings = lastSettings;
                process = null;   // 舊程序自己會結束；之後的輸出與結束事件一律忽略
                ResetProcessStateLocked();
            }
            else
            {
                final = FinishTurnLocked(completed);
            }
        }

        if (resumeFailed)
        {
            _ = RecoverFromMissingSessionAsync(source, retryInput!, retrySettings!, completed.ErrorMessage);
            return;
        }

        Raise(final);
    }

    /// <summary>這一輪結束：清掉執行中的旗標與等使用者回應的請求，回傳要回報的事件（使用者按過停止的失敗改成已停止）</summary>
    private AgentTurnCompletedEvent FinishTurnLocked(AgentTurnCompletedEvent completed)
    {
        running = false;
        remoteTurn = false;
        cliTurn = false;
        var final = interruptRequested && completed.Outcome == AgentTurnOutcome.Failed
            ? completed with { Outcome = AgentTurnOutcome.Interrupted, ErrorMessage = null }
            : completed;
        interruptRequested = false;
        interruptSent = false;
        payloadSent = false;
        payloadWriting = false;
        turnAccepted = false;
        awaitingQueuedTurn = false;
        pendingRequests.Clear();
        return final;
    }

    /// <summary>
    /// 續接失敗（CLI 端找不到 session，例如逐字稿被清掉或換了帳戶）：改開新 session，
    /// 把前文節錄附在這則訊息前面重送，讓代理至少知道先前談過什麼
    /// </summary>
    private async Task RecoverFromMissingSessionAsync(AgentProcess failed, AgentTurnInput input, AgentSessionSettings settings, string? error)
    {
        var tail = failed.GetStderrTail(300);
        _ = failed.CloseAsync(TimeSpan.FromSeconds(3)).ContinueWith(_ => failed.Dispose(), TaskScheduler.Default);
        bool hasRecap = !string.IsNullOrWhiteSpace(settings.HistoryRecap);
        Raise(new AgentNoticeEvent(
            $"找不到原本的 Claude 對話紀錄（{FirstNonEmpty(tail, error)}），已改開新的對話"
            + (hasRecap ? "，並附上先前對話的節錄讓 Claude 了解前情。" : "；先前的內容 Claude 不會記得。"),
            IsError: false));

        bool stopped;
        lock (gate)
        {
            stopped = interruptRequested || disposed;
            running = false;
            interruptRequested = false;
            turnAccepted = false;
        }
        if (stopped)
        {
            Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null));
            UpdateSafePoint();
            return;
        }

        // 新 session 的 id 由 CLI 的 system/init 事件回報（中樞收到才記為已建立）
        var fresh = settings with { SessionId = Guid.NewGuid().ToString(), SessionStarted = false };
        var retry = hasRecap ? input with { Text = settings.HistoryRecap + input.Text } : input;
        try
        {
            await SendAsync(retry, fresh);
        }
        catch (Exception ex)
        {
            Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, $"改開新對話也失敗：{ex.Message}", null));
        }
    }

    /// <summary>子代理逐字稿的資料夾跟著目前的逐字稿（呼叫端持有 gate）</summary>
    private void EnsureSubagentTailLocked()
    {
        if (processTranscript == null)
            return;
        var directory = ClaudeSubagentUsageTail.DirectoryFor(processTranscript);
        if (subagentTail == null || !string.Equals(subagentTail.Directory, directory, StringComparison.OrdinalIgnoreCase))
            subagentTail = new ClaudeSubagentUsageTail(directory);
    }

    /// <summary>
    /// 讀子代理逐字稿新增的用量，發成 token 增量（<paramref name="currentParser"/> 給了就經它記帳：一輪結束時扣掉）。
    /// 不是 <paramref name="force"/> 時一秒最多讀一次
    /// </summary>
    private void PollSubagentUsage(ClaudeStreamParser? currentParser, bool force)
    {
        ClaudeSubagentUsageTail? tail;
        lock (gate)
            tail = subagentTail;
        if (tail == null)
            return;
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref subagentPollTick) < 1000)
            return;
        Interlocked.Exchange(ref subagentPollTick, now);
        foreach (var (model, fields) in tail.Poll())
        {
            var tokens = currentParser != null ? currentParser.AddSubagentUsage(model, fields) : ClaudeStreamRules.TokenEvent(model, fields);
            if (tokens != null)
                Raise(tokens);
        }
    }

    private void OnExited(AgentProcess source, int? exitCode)
    {
        FailControlWaiters(source);
        // 程序結束前子代理最後寫進逐字稿的用量（這個程序不會再有 result，不必經解析器記帳）
        PollSubagentUsage(null, force: true);

        bool wasRunning;
        bool wasInterrupted;
        bool autoTurnExited = false;
        lock (gate)
        {
            if (!ReferenceEquals(source, process))
            {
                source.Dispose();
                return;
            }

            // 新版程序在開始接續之前就結束了：交給 RestartAsync 回 Failed，不當成這一輪失敗（被打斷的那一輪由中樞收尾）
            if (autoTurn is { } watch && ReferenceEquals(watch.Process, source) && !watch.Evidence)
            {
                watch.Exited = true;
                autoTurnExited = true;
            }

            wasRunning = running && !autoTurnExited;
            wasInterrupted = interruptRequested;
            if (!autoTurnExited)
                running = false;
            interruptRequested = false;
            interruptSent = false;
            payloadSent = false;
            payloadWriting = false;
            turnAccepted = false;
            process = null;
            pendingRequests.Clear();
            ResetProcessStateLocked();
        }

        var tail = source.GetStderrTail();
        if (wasRunning)
        {
            Raise(wasInterrupted
                ? new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null)
                : new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, $"Claude 程序意外結束（結束碼 {exitCode?.ToString() ?? "不明"}）{(string.IsNullOrWhiteSpace(tail) ? string.Empty : "：" + tail)}", null));
        }

        Raise(new AgentProcessExitedEvent(exitCode, tail, Expected: !wasRunning && !autoTurnExited, source.InstanceId));
        source.Dispose();
        UpdateSafePoint();
    }

    /// <summary>
    /// 一輪結束後讀逐字稿尾端最後一筆 goal_status（Stop hook 在 result 之前寫，稍等一下再讀），和上次回報的不同才發 AgentGoalEvent。
    /// 只看這個程序啟動之後寫的：--resume 開的新程序沒有之前的目標
    /// </summary>
    private void ScheduleGoalCheck(AgentProcess source)
    {
        string? path;
        DateTimeOffset since;
        lock (gate)
        {
            if (!ReferenceEquals(source, process) || disposed)
                return;
            path = processTranscript;
            since = processStartedAt - ClaudeGoalRules.StartSlack;
        }
        if (string.IsNullOrEmpty(path))
            return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            var goal = ClaudeGoalRules.LastGoal(ClaudeGoalRules.ReadTail(path), since);
            if (goal == null)
                return;
            string? condition = goal.Length == 0 ? null : ClaudeGoalRules.Shorten(goal);
            lock (gate)
            {
                if (!ReferenceEquals(source, process) || disposed || string.Equals(reportedGoal, condition, StringComparison.Ordinal))
                    return;
                reportedGoal = condition;
            }
            Raise(new AgentGoalEvent(condition));
        });
    }

    private void FailControlWaiters(AgentProcess? source)
    {
        List<TaskCompletionSource<ClaudeControlResponse?>> waiters;
        lock (gate)
        {
            waiters = controlWaiters.Values
                .Where(w => source == null || ReferenceEquals(w.Process, source))
                .Select(w => w.Completion)
                .ToList();
        }
        foreach (var waiter in waiters)
            waiter.TrySetResult(null);
    }

    public async Task AnswerQuestionAsync(string requestKey, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var (request, target) = TakePending(requestKey);
        if (request == null || target == null)
            return;
        if (request.Subtype == "elicitation")
        {
            var elicitation = McpElicitationRules.Parse(request.Raw);
            var (response, error) = McpElicitationRules.BuildResponse(elicitation, answers);
            await target.WriteLineAsync(ClaudeStreamRules.BuildSuccessResponse(requestKey, response));
            if (error != null)
                Raise(new AgentNoticeEvent(error, IsError: true));
            return;
        }
        var input = ClaudeStreamRules.BuildQuestionAnswerInput(request.Input, answers);
        await target.WriteLineAsync(ClaudeStreamRules.BuildSuccessResponse(requestKey, ClaudeStreamRules.Allow(input)));
    }

    public async Task DeclineQuestionAsync(string requestKey)
    {
        var (request, target) = TakePending(requestKey);
        if (request == null || target == null)
            return;
        await target.WriteLineAsync(ClaudeStreamRules.BuildSuccessResponse(requestKey, request.Subtype == "elicitation"
            ? McpElicitationRules.Decline()
            : ClaudeStreamRules.Deny("使用者略過了這個問題。請依你的判斷繼續，或在回覆中用文字詢問。")));
    }

    public async Task RespondApprovalAsync(string requestKey, AgentApprovalChoice choice, string? feedback)
    {
        var (request, target) = TakePending(requestKey);
        if (request == null || target == null)
            return;

        bool forSession = choice == AgentApprovalChoice.AllowForSession && request.ToolName != null;
        if (forSession)
        {
            // 本機只記完全相同的請求；更廣的範圍交給下面送給 CLI 的 session 規則（CLI 自己比對，例如 Bash(npm test:*)），
            // 同意過的規則與資料夾另外記下，程序重開時帶回
            var grants = ClaudeStreamRules.ExtractSessionGrants(request.Suggestions);
            lock (gate)
            {
                sessionAllowedRequests.Add(ClaudeStreamRules.RequestKey(request.ToolName, request.Input));
                sessionRules.AddRange(grants.Rules.Where(r => !sessionRules.Contains(r, StringComparer.Ordinal)));
                sessionDirectories.AddRange(grants.Directories.Where(d => !sessionDirectories.Contains(d, StringComparer.OrdinalIgnoreCase)));
            }
        }

        var response = choice == AgentApprovalChoice.Deny
            ? ClaudeStreamRules.Deny(ClaudeStreamRules.BuildDenyMessage(feedback))
            : ClaudeStreamRules.Allow(request.Input, forSession ? ClaudeStreamRules.ToSessionPermissions(request.Suggestions) : null);
        await target.WriteLineAsync(ClaudeStreamRules.BuildSuccessResponse(requestKey, response));

        // CLI 建議連權限模式一起切換（每步詢問下的編輯：改為自動接受編輯，同 VS Code）：明確送 set_permission_mode，
        // 記下目前模式並讓畫面跟著改；工作資料夾外的路徑與 .claude、.git 等敏感檔 CLI 仍會再詢問
        if (forSession && ClaudeStreamRules.FindSuggestedMode(request.Suggestions) is { } mode)
        {
            if (await EnsurePermissionModeAsync(target, mode))
                Raise(new AgentPermissionModeChangedEvent(mode));
            else
                Raise(new AgentNoticeEvent("Claude 沒有接受切換權限模式，之後的操作仍照原本的模式詢問", IsError: false));
        }
    }

    public async Task RespondPlanAsync(string requestKey, AgentPlanChoice choice, string? feedback)
    {
        var (request, target) = TakePending(requestKey);
        if (request == null || target == null)
            return;

        if (choice == AgentPlanChoice.KeepPlanning)
        {
            await target.WriteLineAsync(ClaudeStreamRules.BuildSuccessResponse(requestKey,
                ClaudeStreamRules.Deny(ClaudeStreamRules.BuildKeepPlanningMessage(feedback))));
            return;
        }

        // 同意計畫：先放行 ExitPlanMode，再把權限模式切出規劃模式（自動接受編輯或每步詢問）
        await target.WriteLineAsync(ClaudeStreamRules.BuildSuccessResponse(requestKey, ClaudeStreamRules.Allow(request.Input)));
        var mode = choice == AgentPlanChoice.AcceptEdits ? AgentPermissionMode.AcceptEdits : AgentPermissionMode.Ask;
        if (await EnsurePermissionModeAsync(target, mode))
            Raise(new AgentPermissionModeChangedEvent(mode));
    }

    /// <summary>執行中切換權限模式：程序還在就立即送 set_permission_mode（計畫確認也是在一輪中途這樣切）；程序還沒啟動或已結束回 false</summary>
    public async Task<bool> SetPermissionModeAsync(AgentPermissionMode mode)
    {
        AgentProcess? target;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited)
                return false;
            target = process;
        }
        return await EnsurePermissionModeAsync(target, mode);
    }

    /// <summary>
    /// 換模型與推理強度，立即套用到活著的程序（一輪中途也可以：CLI 從下一次模型呼叫起改用，實測見 protocol-notes「執行中換模型」）：
    /// 模型送 set_model、強度送 apply_flag_settings 的 effortLevel 與同值的環境變數（null＝回到 CLI 預設；從 ultracode 換走另帶 ultracode:false，
    /// <see cref="ClaudeStreamRules.BuildSetEffortRequest"/>），和目前程序的設定相同的不送；
    /// 成功的記成目前程序的設定，下一輪不必重開。沒有程序、正在重開、CLI 拒絕或沒回應回 false（下一則訊息照舊重開程序套用）
    /// </summary>
    public async Task<bool> SetModelAsync(string? model, string? effort)
    {
        // 連續切換依序送：CLI 最後套用的才會是最後選的
        await modelSwitchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            AgentProcess target;
            AgentSessionSettings current;
            lock (gate)
            {
                if (disposed || process == null || process.HasExited || restarting || restartCompletion != null || processSettings == null)
                    return false;
                target = process;
                current = processSettings;
            }

            if (!string.Equals(NormalizeModel(current.Model), NormalizeModel(model), StringComparison.OrdinalIgnoreCase))
            {
                var response = await RequestControlAsync(target, "model", id => ClaudeStreamRules.BuildSetModelRequest(id, NormalizeModel(model))).ConfigureAwait(false);
                if (response is not { Success: true })
                {
                    Log.Info($"Claude set_model {NormalizeModel(model) ?? "default"} 沒有套用：{response?.Error ?? "沒有回應"}");
                    return false;
                }
                if (!UpdateProcessSettings(target, s => s with { Model = model }))
                    return false;
            }

            if (!string.Equals(current.Effort ?? string.Empty, effort ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                var response = await RequestControlAsync(target, "effort", id => ClaudeStreamRules.BuildSetEffortRequest(id, effort, current.Effort)).ConfigureAwait(false);
                if (response is not { Success: true })
                {
                    Log.Info($"Claude 推理強度 {effort ?? "default"} 沒有套用：{response?.Error ?? "沒有回應"}");
                    return false;
                }
                if (!UpdateProcessSettings(target, s => s with { Effort = effort }))
                    return false;
            }
            return true;
        }
        finally
        {
            modelSwitchGate.Release();
        }
    }

    public async Task<AgentRewindResult> RewindFilesAsync(string userMessageUuid, bool dryRun, AgentSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var (target, error) = await EnsureProcessForControlAsync(settings);
        if (target == null)
            return new AgentRewindResult(false, [], 0, 0, error);
        var response = await RequestControlAsync(target, dryRun ? "rewind-dry" : "rewind",
            id => ClaudeControlRules.BuildRewindFilesRequest(id, userMessageUuid, dryRun)).ConfigureAwait(false);
        if (response == null)
            return new AgentRewindResult(false, [], 0, 0, "Claude 沒有回應（逾時或程序已結束）");
        if (!response.Success)
            return new AgentRewindResult(false, [], 0, 0, ClaudeRewindRules.DescribeFilesError(response.Error));
        var result = ClaudeControlRules.ParseRewind(response.Response);
        return result.CanRewind ? result : result with { Error = ClaudeRewindRules.DescribeFilesError(result.Error) };
    }

    /// <summary>這個對話目前的逐字稿（回溯對話時在背景讀）：開著程序時是它寫的那份，否則照設定推算</summary>
    public string? TranscriptFor(AgentSessionSettings settings)
    {
        lock (gate)
        {
            if (process is { HasExited: false } && processTranscript != null)
                return processTranscript;
        }
        if (string.IsNullOrWhiteSpace(settings.SessionId))
            return null;
        // 延伸出來的對話還沒分支：內容就是原對話逐字稿的副本（回溯在它裡面找分支點）
        if (IsForkStart(settings))
            return settings.ForkSource;
        var configDirectory = ClaudeStreamRules.ResolveConfigDirectory(settings.ConfigureEnvironment);
        return ClaudeStreamRules.FindTranscriptElsewhere(configDirectory, settings.SessionId, settings.WorkingDirectory)
               ?? ClaudeStreamRules.FindTranscript(configDirectory, settings.SessionId, settings.WorkingDirectory);
    }

    public async Task<Health.ClaudeCliInventory?> GetInventoryAsync()
    {
        AgentProcess target;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited)
                return null;
            target = process;
        }
        async Task<JObject?> Ask(string subtype)
        {
            var response = await RequestControlAsync(target, "inv", id => ClaudeStreamRules.BuildControlRequest(id, new JObject { ["subtype"] = subtype })).ConfigureAwait(false);
            return response is { Success: true } ? response.Response : null;
        }
        return Health.ClaudeCliInventory.Parse(await Ask("get_skills_dialog"), await Ask("get_hooks_listing"), await Ask("get_memory_dialog"));
    }

    /// <summary>這個程序給過的標題（generate_session_title 同一個程序第二次起一律回第一次的，對到就當成沒有新的標題）</summary>
    private readonly HashSet<string> generatedTitles = new(StringComparer.Ordinal);
    private AgentProcess? generatedTitlesProcess;

    public bool CanGenerateTitle
    {
        get
        {
            lock (gate)
                return !disposed && process is { HasExited: false } && !restarting && restartCompletion == null;
        }
    }

    public async Task<string?> GenerateTitleAsync(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;
        AgentProcess target;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited || restarting || restartCompletion != null)
                return null;
            target = process;
            if (!ReferenceEquals(generatedTitlesProcess, target))
            {
                generatedTitles.Clear();
                generatedTitlesProcess = target;
            }
        }
        var response = await RequestControlAsync(target, "title", id => ClaudeControlRules.BuildGenerateTitleRequest(id, description), TitleTimeout).ConfigureAwait(false);
        if (response is not { Success: true } || ClaudeControlRules.ParseGeneratedTitle(response.Response) is not { } title)
            return null;
        lock (gate)
        {
            if (!ReferenceEquals(generatedTitlesProcess, target) || !generatedTitles.Add(title))
                return null;
        }
        return title;
    }

    private static readonly TimeSpan TitleTimeout = TimeSpan.FromSeconds(30);

    public async Task<AgentTaskOutput> GetTaskOutputAsync(string taskId)
    {
        AgentProcess target;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited)
                return new AgentTaskOutput(null, 0, false, "Claude 程序已經結束，背景工作也跟著結束了");
            target = process;
        }
        var response = await RequestControlAsync(target, "taskout", id => ClaudeControlRules.BuildTaskOutputRequest(id, taskId)).ConfigureAwait(false);
        if (response == null)
            return new AgentTaskOutput(null, 0, false, "Claude 沒有回應（逾時或程序已結束）");
        if (!response.Success)
            return new AgentTaskOutput(null, 0, false, string.IsNullOrWhiteSpace(response.Error) ? "取不到這個背景工作的輸出" : response.Error.Trim());
        return ClaudeControlRules.ParseTaskOutput(response.Response);
    }

    /// <summary>
    /// 強制停止背景工作（stop_task；stopId null＝目前存活的全部，含對不到工具卡的）：CLI 回 task_notification stopped 與 background_tasks_changed、
    /// 不為此開一輪（E-11 實測）。只認 background_tasks_changed 目前列著的
    /// </summary>
    public async Task<AgentBackgroundStopResult> StopBackgroundWorkAsync(string? stopId)
    {
        AgentProcess target;
        List<string> ids;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited)
                return new AgentBackgroundStopResult(0, "Claude 程序已經結束，背景工作也跟著結束了");
            if (restarting)
                return new AgentBackgroundStopResult(0, "對話正在以新版 CLI 重新啟動，稍後再試");
            target = process;
            ids = stopId == null ? backgroundTaskIds.ToList() : backgroundTaskIds.Where(id => id == stopId).ToList();
        }
        if (ids.Count == 0)
            return new AgentBackgroundStopResult(0, "這個背景工作已經結束了");

        int stopped = 0;
        string? error = null;
        foreach (var taskId in ids)
        {
            var response = await RequestControlAsync(target, "stop", id => ClaudeStreamRules.BuildStopTaskRequest(id, taskId)).ConfigureAwait(false);
            if (response is { Success: true })
                stopped++;
            else
                error ??= response == null ? "Claude 沒有回應（逾時或程序已結束）" : string.IsNullOrWhiteSpace(response.Error) ? "Claude 沒有停止這個背景工作" : response.Error.Trim();
        }
        Log.Info($"Claude 停止背景工作：{stopped}/{ids.Count} 個" + (error != null ? $"（{error}）" : ""));
        return new AgentBackgroundStopResult(stopped, stopped == ids.Count ? null : error);
    }

    public async Task<bool> SetOutputStyleAsync(string style)
    {
        AgentProcess target;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited || restarting || restartCompletion != null || processSettings == null)
                return false;
            if (string.Equals(processSettings.OutputStyle, style, StringComparison.Ordinal))
                return true;
            target = process;
        }
        var response = await RequestControlAsync(target, "style", id => ClaudeStreamRules.BuildSetOutputStyleRequest(id, style)).ConfigureAwait(false);
        if (response is not { Success: true })
        {
            Log.Info($"Claude 輸出風格 {style} 沒有套用：{response?.Error ?? "沒有回應"}");
            return false;
        }
        return UpdateProcessSettings(target, s => s with { OutputStyle = style });
    }

    /// <summary>即時套用成功：程序還是同一個時改記目前程序（與這一輪）的設定，回 false＝程序已換掉（新程序照它啟動時的設定）</summary>
    private bool UpdateProcessSettings(AgentProcess target, Func<AgentSessionSettings, AgentSessionSettings> update)
    {
        lock (gate)
        {
            if (!ReferenceEquals(process, target) || processSettings == null)
                return false;
            processSettings = update(processSettings);
            if (lastSettings != null)
                lastSettings = update(lastSettings);
            return true;
        }
    }

    /// <summary>送一個控制請求並等回應最多 timeout（預設 ControlTimeout）；沒有回應或寫入失敗（程序已結束）回 null</summary>
    private async Task<ClaudeControlResponse?> RequestControlAsync(AgentProcess target, string prefix, Func<string, string> build, TimeSpan? timeout = null)
    {
        var requestId = NextRequestId(prefix);
        var completion = new TaskCompletionSource<ClaudeControlResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
            controlWaiters[requestId] = (target, completion);
        try
        {
            await target.WriteLineAsync(build(requestId)).ConfigureAwait(false);
            var finished = await Task.WhenAny(completion.Task, Task.Delay(timeout ?? ControlTimeout)).ConfigureAwait(false);
            return finished == completion.Task ? await completion.Task.ConfigureAwait(false) : null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            lock (gate)
                controlWaiters.Remove(requestId);
        }
    }

    private (ClaudeControlRequest? Request, AgentProcess? Process) TakePending(string requestKey)
    {
        ClaudeControlRequest? request;
        AgentProcess? target;
        lock (gate)
        {
            if (process == null || !pendingRequests.Remove(requestKey, out request))
                return (null, null);
            target = process;
        }
        // 使用者回應了：這一輪不再等使用者（工具接著執行，到 tool_result 才算結束）
        UpdateSafePoint();
        return (request, target);
    }

    public async Task InterruptAsync()
    {
        List<ClaudeControlRequest> pending;
        AgentProcess? target;
        bool sendNow;
        int generation;
        lock (gate)
        {
            // 以新版重開進行中按停止＝不接續：RestartAsync 回 Restarted，被打斷的那一輪由中樞收尾
            if (restarting)
            {
                restartInterruptRequested = true;
                return;
            }

            // 停下待接續的一輪（npm 更新屏障）：不再接續
            stoppedTurn = null;
            if (!running)
                return;
            interruptRequested = true;
            pending = pendingRequests.Values.ToList();
            pendingRequests.Clear();
            target = process;
            generation = turnGeneration;
            sendNow = target != null && payloadSent && !interruptSent;
            if (sendNow)
                interruptSent = true;
        }
        UpdateSafePoint();

        if (target == null)
            return;
        foreach (var request in pending)
        {
            // MCP 的 elicitation 回 cancel（使用者沒有回答），其他的照舊拒絕並中斷
            await WriteQuietlyAsync(target, ClaudeStreamRules.BuildSuccessResponse(request.RequestId, request.Subtype == "elicitation"
                ? McpElicitationRules.Cancel()
                : ClaudeStreamRules.Deny("使用者已停止這一輪", interrupt: true)));
        }
        if (!sendNow)
            return;

        await WriteQuietlyAsync(target, ClaudeStreamRules.BuildInterruptRequest(NextRequestId("interrupt")));
        StartInterruptWatchdog(target, generation);
    }

    /// <summary>中斷送出後太久沒結束就強制結束程序樹；只處理同一輪（上一輪排的看門狗不能砍掉下一輪剛送出的中斷）</summary>
    private void StartInterruptWatchdog(AgentProcess target, int generation)
    {
        _ = Task.Delay(InterruptWatchdogDelay).ContinueWith(_ =>
        {
            bool stuck;
            lock (gate)
                stuck = running && interruptRequested && turnGeneration == generation && ReferenceEquals(process, target);
            if (stuck)
                target.Kill();
        }, TaskScheduler.Default);
    }

    public async Task CloseAsync()
    {
        AgentProcess? target;
        lock (gate)
        {
            target = process;
            process = null;
            running = false;
            turnAccepted = false;
            pendingRequests.Clear();
            stoppedTurn = null;
            if (restarting)
                restartInterruptRequested = true;
            // 正在重開程序接回 Remote Control：程序可能還沒起來，起來後由 ReattachRemoteControlAsync 關掉
            if (reattachCompletion != null)
                reattachCancelled = true;
            ResetProcessStateLocked();
        }

        FailControlWaiters(null);
        UpdateSafePoint();
        if (target != null)
            await CloseProcessAsync(target);
    }

    /// <summary>正常關閉：關 stdin 讓 CLI 收尾，逾時才結束程序樹；回來時程序已結束（或已確定結束不了）</summary>
    private static async Task CloseProcessAsync(AgentProcess target)
    {
        try
        {
            await target.CloseAsync(TimeSpan.FromSeconds(5));
            await WaitQuietlyAsync(target.ExitTask, TimeSpan.FromSeconds(2));
        }
        finally
        {
            target.Dispose();
        }
    }

    // ───────────────────────────── CLI 熱更新：以新版重開 ─────────────────────────────

    /// <summary>
    /// 以新版 CLI 重開程序（流程見契約 2.5 與 AgentRestartRequest）：
    /// <list type="number">
    /// <item>SafePoint 且不在安全點（或剛送出的訊息 CLI 還沒接手）：Deferred，之後到了安全點會再發一次 AgentSafePointChangedEvent</item>
    /// <item>沒有程序也沒有停下待接續的一輪：Restarted</item>
    /// <item>執行中先等逐字稿寫入這一輪的訊息與最後一個工具結果；有背景工作就先 stop_task；卸下舊程序（之後它的輸出與結束都不算數，
    /// 被打斷的那一輪不發 TurnCompleted）、結束程序樹並確認子孫都已結束、把串流到一半的回覆與被中斷的工具卡結束為已中斷、截掉逐字稿寫到一半的尾行</item>
    /// <item>Settings=null：Stopped（這一輪正在執行就記住停下待接續）</item>
    /// <item>這一輪不在執行中、AutoContinue=false、重開途中按了停止：Restarted</item>
    /// <item>以 Settings 加 RESUME 環境變數 --resume 啟動新程序，一啟動就標成執行中並發 AgentTurnStartedByCliEvent；CLI 開始接續（command_lifecycle started）
    /// 就回 RestartedAndContinued。initialize 回應後 AutoTurnGrace 內沒有開始＝這次沒有接續：這一輪還在途中就改經 stdin 送接續訊息（備案），
    /// 否則關掉新程序回 Restarted</item>
    /// </list>
    /// </summary>
    public async Task<AgentRestartResult> RestartAsync(AgentRestartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 立即套用：訊息剛送出、CLI 還沒接手時先等它接手，否則重開會讓這則訊息消失
        if (request.Mode == AgentRestartMode.Immediate)
            await WaitForTurnHandoffAsync().ConfigureAwait(false);

        AgentProcess? old;
        TaskCompletionSource done;
        lock (gate)
        {
            if (disposed)
                return new AgentRestartResult(AgentRestartOutcome.Failed, Error: "這個對話已關閉");
            // 重開進行中、或正在重開程序接回 Remote Control（程序還沒起來）：之後再試
            if (restarting || reattachCompletion != null)
                return new AgentRestartResult(AgentRestartOutcome.Deferred);
            bool preparing = running && (!turnAccepted || autoTurn != null);
            if (preparing || (request.Mode == AgentRestartMode.SafePoint && !IsAtSafePointLocked(ignoreRestart: false)))
            {
                safePointNudge = true;
                return new AgentRestartResult(AgentRestartOutcome.Deferred);
            }

            old = process != null && !process.HasExited ? process : null;
            if (old == null && stoppedTurn == null)
            {
                // 沒有程序、也沒有停下待接續的一輪：下次送出自然用新版
                return new AgentRestartResult(AgentRestartOutcome.Restarted);
            }

            restarting = true;
            restartInterruptRequested = false;
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            restartCompletion = done;
        }

        var previousInstance = old?.InstanceId;
        Log.Info($"Claude 對話以新版重開：{request.Reason}／{request.Mode}，{request.FromVersion ?? "?"} → {request.ToVersion ?? "?"}，程序 {previousInstance ?? "（無）"}");
        try
        {
            InterruptedTurn? turn;
            if (old != null)
            {
                var stop = await StopProcessForRestartAsync(old, request).ConfigureAwait(false);
                if (stop.Early != null)
                    return stop.Early with { PreviousProcessInstance = stop.Early.Outcome == AgentRestartOutcome.Deferred ? null : previousInstance };
                turn = stop.Turn;
            }
            else
            {
                lock (gate)
                    turn = stoppedTurn;
            }

            // 4. 只停下（npm 更新屏障的第一段）：這一輪正在執行就記住停下待接續
            if (request.Settings == null)
            {
                lock (gate)
                {
                    stoppedTurn = turn != null && !restartInterruptRequested && !disposed ? turn : null;
                    running = false;
                    turnAccepted = false;
                }
                return Logged(new AgentRestartResult(AgentRestartOutcome.Stopped, previousInstance));
            }

            // 5. 不接續：這一輪不在執行中（上一輪已完成）、不要自動接續、重開途中按了停止、逐字稿沒有這個 session
            bool continueTurn;
            lock (gate)
            {
                continueTurn = turn is { CanResume: true } && request.AutoContinue && !restartInterruptRequested && !disposed;
                stoppedTurn = null;
                if (!continueTurn)
                {
                    running = false;
                    turnAccepted = false;
                }
            }
            if (!continueTurn)
                return Logged(new AgentRestartResult(AgentRestartOutcome.Restarted, previousInstance));

            // 6. 以新版啟動並自動接續
            return Logged(await StartAutoTurnAsync(request, turn!, previousInstance).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            Log.Error("Claude 對話以新版重開失敗", ex);
            lock (gate)
            {
                // 回到閒置（新程序啟動之後才失敗的，StartAutoTurnAsync 已經結束它）
                autoTurn = null;
                running = false;
                turnAccepted = false;
                stoppedTurn = null;
            }
            return new AgentRestartResult(AgentRestartOutcome.Failed, previousInstance, Error: ex.Message);
        }
        finally
        {
            lock (gate)
            {
                restarting = false;
                restartingOld = null;
                restartOldResult = null;
                if (ReferenceEquals(restartCompletion, done))
                    restartCompletion = null;
            }
            done.TrySetResult();
            UpdateSafePoint();
        }
    }

    private static AgentRestartResult Logged(AgentRestartResult result)
    {
        Log.Info($"Claude 對話重開結果：{result.Outcome}（{result.PreviousProcessInstance ?? "無"} → {result.ProcessInstance ?? "無"}）{result.Error}");
        return result;
    }

    /// <summary>立即重開前：這一輪的訊息剛送出、CLI 還沒接手（或正在等自動接續開始）就等一下，上限 HandoffTimeout</summary>
    private async Task WaitForTurnHandoffAsync()
    {
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < HandoffTimeout)
        {
            lock (gate)
            {
                if (disposed || restarting || !running || (turnAccepted && autoTurn == null))
                    return;
            }
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 停下舊程序（第 3 步）：回傳 Early（Deferred、Failed）或被打斷的這一輪（null＝這一輪不在執行中，或在卸下前已經結束）
    /// </summary>
    private async Task<(AgentRestartResult? Early, InterruptedTurn? Turn)> StopProcessForRestartAsync(AgentProcess old, AgentRestartRequest request)
    {
        bool wasRunning;
        lock (gate)
            wasRunning = running;

        // 執行中：逐字稿比 stdout 晚約 0.17 秒才寫入工具結果（E-10），在那之前殺掉，接續時這個工具會變成「結果未知」
        bool recorded = true;
        if (wasRunning)
        {
            recorded = await WaitForTranscriptCatchUpAsync().ConfigureAwait(false);
            lock (gate)
            {
                // 等的期間又開始了工具、要求核准或背景工作：不在安全點了
                if (request.Mode == AgentRestartMode.SafePoint && !IsAtSafePointLocked(ignoreRestart: true))
                {
                    safePointNudge = true;
                    return (new AgentRestartResult(AgentRestartOutcome.Deferred), null);
                }
            }
        }

        // 有背景工作（立即套用時才會到這裡）：先逐一 stop_task，CLI 記下已停止、不經模型，重開後狀態乾淨（E-11）
        await StopBackgroundTasksAsync(old).ConfigureAwait(false);

        // 卸下舊程序：之後它的輸出、結果與結束事件都不算數（被打斷的那一輪不發 TurnCompleted）
        ClaudeStreamParser oldParser;
        AgentSessionSettings? oldSettings;
        string? transcript;
        string? configDirectory;
        bool detachedHere;
        lock (gate)
        {
            oldParser = parser;
            oldSettings = processSettings;
            transcript = processTranscript;
            configDirectory = processConfigDirectory;
            restartingOld = old;
            detachedHere = ReferenceEquals(process, old);
            if (detachedHere)
            {
                process = null;
                pendingRequests.Clear();
                ResetProcessStateLocked();
            }
        }
        FailControlWaiters(old);

        // 不送 interrupt（會被寫成使用者拒絕，CLI 也不接續）：結束程序樹，確認子孫（指令、MCP 伺服器）都已結束。
        // 等待期間已被別的路徑卸下的（刪除對話的 Dispose、程序自己結束、CloseAsync）由那條路徑結束並釋放：釋放後 PID 可能被別的程序重複使用，
        // 不能再以 PID 擷取程序樹（會把不相干的程序當成根殺掉），只等它結束
        bool killed = detachedHere
            ? await old.KillTreeAsync(KillTimeout).ConfigureAwait(false)
            : await WaitQuietlyAsync(old.ExitTask, KillTimeout).ConfigureAwait(false);
        bool drained = await WaitQuietlyAsync(old.ExitTask, ExitDrainTimeout).ConfigureAwait(false);
        if (!killed)
        {
            Log.Warn($"Claude 重開：舊程序 {old.ProcessId} 的程序樹沒有在時限內結束");
            lock (gate)
            {
                running = false;
                turnAccepted = false;
            }
            return (new AgentRestartResult(AgentRestartOutcome.Failed, Error: "舊的 Claude 程序沒有在時限內結束，沒有以新版重開"), null);
        }

        // 舊程序在卸下之前已送出這一輪的 result：這一輪其實已經結束，照常回報、不接續
        AgentTurnCompletedEvent? finished;
        bool turnActive;
        lock (gate)
        {
            finished = restartOldResult;
            restartOldResult = null;
            turnActive = running && finished == null;
        }
        if (finished != null)
            Raise(finished);

        // 串流到一半的回覆與被中斷的工具卡結束為已中斷（讀取執行緒處理完最後一行才動解析器）
        string? lastStopReason = null;
        if (drained)
        {
            lastStopReason = oldParser.LastStopReason;
            if (turnActive)
            {
                foreach (var ev in oldParser.InterruptOpenWork())
                    Raise(ev);
            }
        }

        // 截掉逐字稿寫到一半的尾行（程序已確認結束）
        var sessionId = oldSettings?.SessionId;
        var path = await Task.Run(() => ResolveTranscript(transcript, configDirectory, sessionId, oldSettings?.WorkingDirectory)).ConfigureAwait(false);
        if (path != null && sessionId != null)
            await Task.Run(() => TrimTranscript(path, sessionId)).ConfigureAwait(false);

        if (!turnActive)
            return (null, null);
        // 逐字稿在：能以 --resume 續接；找不到逐字稿時只有本來就是續接的 session 才試（新對話還沒寫過逐字稿＝CLI 沒有這個 session）
        bool exists = path != null && File.Exists(path);
        bool canResume = exists || (path == null && oldSettings?.SessionStarted == true);
        return (null, new InterruptedTurn(sessionId, lastStopReason, recorded && (exists || path == null), canResume));
    }

    /// <summary>
    /// 等逐字稿寫入這一輪的使用者訊息（長度超過送出時記下的長度）與 stdout 上最後一個工具結果，上限 TranscriptCatchUpTimeout。
    /// 回傳逐字稿是否已記下這一輪（找不到逐字稿時當成有：無從確認，照常重開）
    /// </summary>
    private async Task<bool> WaitForTranscriptCatchUpAsync()
    {
        string? transcript;
        string? configDirectory;
        string? sessionId;
        string? workingDirectory;
        long baseline;
        string? toolId;
        lock (gate)
        {
            transcript = processTranscript;
            configDirectory = processConfigDirectory;
            sessionId = processSettings?.SessionId;
            workingDirectory = processSettings?.WorkingDirectory;
            baseline = transcriptBaseline;
            toolId = lastToolResultId != null && Stopwatch.GetElapsedTime(lastToolResultAt) < ToolResultPersistedAfter ? lastToolResultId : null;
        }

        // 新對話的逐字稿要等 CLI 第一次寫入才存在：找不到就等推算的位置出現
        var path = await Task.Run(() => ResolveTranscript(transcript, configDirectory, sessionId, workingDirectory)).ConfigureAwait(false) ?? transcript;
        if (path == null)
            return true;
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var (length, hasTool) = await Task.Run(() => ProbeTranscript(path, toolId)).ConfigureAwait(false);
            bool messageRecorded = baseline < 0 || length > baseline;
            if (messageRecorded && (toolId == null || hasTool))
                return true;
            if (Stopwatch.GetElapsedTime(start) >= TranscriptCatchUpTimeout)
            {
                Log.Warn($"Claude 重開：逐字稿在 {TranscriptCatchUpTimeout.TotalSeconds:0.#} 秒內沒有寫入（訊息 {messageRecorded}、工具結果 {toolId == null || hasTool}），照常重開");
                return messageRecorded;
            }
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>重開前停掉存活的背景工作（stop_task），等回應最多 StopTaskTimeout；之後程序樹反正會一起結束</summary>
    private async Task StopBackgroundTasksAsync(AgentProcess target)
    {
        List<string> ids;
        lock (gate)
            ids = ReferenceEquals(process, target) ? backgroundTaskIds.ToList() : new List<string>();
        if (ids.Count == 0)
            return;

        var requests = new List<(string RequestId, Task<ClaudeControlResponse?> Response)>();
        foreach (var taskId in ids)
        {
            var requestId = NextRequestId("stop");
            var completion = new TaskCompletionSource<ClaudeControlResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
                controlWaiters[requestId] = (target, completion);
            requests.Add((requestId, completion.Task));
            await WriteQuietlyAsync(target, ClaudeStreamRules.BuildStopTaskRequest(requestId, taskId)).ConfigureAwait(false);
        }

        await Task.WhenAny(Task.WhenAll(requests.Select(r => r.Response)), Task.Delay(StopTaskTimeout)).ConfigureAwait(false);
        lock (gate)
        {
            foreach (var (requestId, _) in requests)
                controlWaiters.Remove(requestId);
        }
        Log.Info($"Claude 重開：先停掉 {ids.Count} 個背景工作");
    }

    /// <summary>第 6 步：以新版加 RESUME 環境變數啟動，等 CLI 自動接續；沒有接續時依情況改送接續訊息（備案）或關掉新程序</summary>
    private async Task<AgentRestartResult> StartAutoTurnAsync(AgentRestartRequest request, InterruptedTurn turn, string? previousInstance)
    {
        // 接續的是被打斷那個程序的 session（中樞的設定可能還沒收到 init）；能走到這裡它一定已建立
        var requested = request.Settings!;
        var settings = requested with
        {
            SessionId = turn.SessionId ?? requested.SessionId,
            SessionStarted = true,
        };
        var configDirectory = ClaudeStreamRules.ResolveConfigDirectory(settings.ConfigureEnvironment);
        var (resumeTranscript, baseline) = await Task.Run(() =>
        {
            var elsewhere = ClaudeStreamRules.FindTranscriptElsewhere(configDirectory, settings.SessionId!, settings.WorkingDirectory);
            var path = elsewhere ?? ClaudeStreamRules.FindTranscript(configDirectory, settings.SessionId!, settings.WorkingDirectory);
            return (elsewhere, MeasureTranscript(path));
        }).ConfigureAwait(false);

        var resumePrompt = UseResumeEnvironment ? request.EffectiveContinuationText : null;
        var started = await StartProcessAsync(settings, resumeTranscript, new AutoTurnStart(resumePrompt, baseline)).ConfigureAwait(false);
        try
        {
            var decision = await WaitForAutoTurnAsync(started).ConfigureAwait(false);
            switch (decision.Kind)
            {
                case AutoTurnDecisionKind.Continued:
                    return new AgentRestartResult(AgentRestartOutcome.RestartedAndContinued, previousInstance, started.InstanceId);

                case AutoTurnDecisionKind.NotContinued when ClaudeStreamRules.ShouldSendContinuationFallback(false, turn.LastStopReason, turn.Recorded):
                    // 備案（RESUME 環境變數失效、或 CLI 判定不接續但這一輪其實還在途中，例如最後是使用者拒絕的工具結果）：
                    // 經 stdin 送接續訊息，不顯示成使用者訊息
                    Log.Info("Claude 重開：新版沒有自動接續，改送接續訊息");
                    await SendContinuationAsync(started, request.EffectiveContinuationText).ConfigureAwait(false);
                    return new AgentRestartResult(AgentRestartOutcome.RestartedAndContinued, previousInstance, started.InstanceId);

                case AutoTurnDecisionKind.NotContinued:
                    // 這一輪其實已經回覆完（或逐字稿沒有它）：關掉新程序，下次送出自然用新版
                    await ReleaseAutoTurnProcessAsync(started, kill: false).ConfigureAwait(false);
                    return new AgentRestartResult(AgentRestartOutcome.Restarted, previousInstance);

                case AutoTurnDecisionKind.Stopped:
                    // 重開途中按了停止（或關閉）：不接續
                    await ReleaseAutoTurnProcessAsync(started, kill: true).ConfigureAwait(false);
                    return new AgentRestartResult(AgentRestartOutcome.Restarted, previousInstance);

                default:
                    await ReleaseAutoTurnProcessAsync(started, kill: true).ConfigureAwait(false);
                    return new AgentRestartResult(AgentRestartOutcome.Failed, previousInstance, Error: decision.Error);
            }
        }
        catch
        {
            // 新程序啟動之後才失敗（例如寫入接續訊息時程序已結束）：結束它，回到閒置、沒有程序
            await ReleaseAutoTurnProcessAsync(started, kill: true).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>等新程序開始自動接續：有證據（command_lifecycle started、這一輪的活動）＝接續；initialize 回應後 AutoTurnGrace 內都沒有＝沒有接續</summary>
    private async Task<AutoTurnDecision> WaitForAutoTurnAsync(AgentProcess started)
    {
        var begin = Stopwatch.GetTimestamp();
        while (true)
        {
            lock (gate)
            {
                // 使用者按了停止（或關閉、釋放）：不接續，即使 CLI 已經開始
                if (disposed || restartInterruptRequested)
                    return new AutoTurnDecision(AutoTurnDecisionKind.Stopped);
                var watch = autoTurn;
                if (watch == null || !ReferenceEquals(watch.Process, started))
                    return new AutoTurnDecision(AutoTurnDecisionKind.Stopped);
                if (watch.Evidence)
                {
                    autoTurn = null;
                    turnAccepted = true;
                    return new AutoTurnDecision(AutoTurnDecisionKind.Continued);
                }
                if (watch.Failure != null)
                    return new AutoTurnDecision(AutoTurnDecisionKind.Failed, watch.Failure);
                if (watch.Exited)
                    return new AutoTurnDecision(AutoTurnDecisionKind.Failed, $"新版 Claude 程序啟動後就結束了：{FirstNonEmpty(started.GetStderrTail(300))}");
                if (!ReferenceEquals(process, started))
                    return new AutoTurnDecision(AutoTurnDecisionKind.Stopped);
                if (watch.InitResponseAt != 0 && Stopwatch.GetElapsedTime(watch.InitResponseAt) >= AutoTurnGrace)
                    return new AutoTurnDecision(AutoTurnDecisionKind.NotContinued);
                if (watch.InitResponseAt == 0 && Stopwatch.GetElapsedTime(begin) >= AutoTurnStartTimeout)
                    return new AutoTurnDecision(AutoTurnDecisionKind.Failed, "新版 Claude 程序沒有回應");
            }
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>備案：經 stdin 送接續訊息（不顯示成使用者訊息；CLI 會替結果沒寫進逐字稿的工具補「結果未知」，模型先確認再決定要不要重跑）</summary>
    private async Task SendContinuationAsync(AgentProcess target, string text)
    {
        string? transcriptPath;
        lock (gate)
        {
            autoTurn = null;
            transcriptPath = processTranscript;
        }
        var baseline = await Task.Run(() => MeasureTranscript(transcriptPath)).ConfigureAwait(false);
        lock (gate)
        {
            // 之前的輸出不算接手接續訊息；寫入期間 CLI 就開始的回應要算（寫完才清掉的話，那段活動會被蓋掉）
            turnAccepted = false;
            payloadWriting = true;
        }
        await target.WriteLineAsync(ClaudeStreamRules.BuildUserMessage(text, Array.Empty<(string, string)>())).ConfigureAwait(false);
        lock (gate)
        {
            payloadWriting = false;
            payloadSent = true;
            transcriptBaseline = baseline;
        }
        Raise(new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));
    }

    /// <summary>沒有接續（或失敗、按了停止）：卸下新程序並關掉，回到閒置、沒有程序</summary>
    private async Task ReleaseAutoTurnProcessAsync(AgentProcess started, bool kill)
    {
        bool current;
        lock (gate)
        {
            current = ReferenceEquals(process, started);
            if (current)
            {
                process = null;
                pendingRequests.Clear();
                ResetProcessStateLocked();
            }
            if (autoTurn != null && ReferenceEquals(autoTurn.Process, started))
                autoTurn = null;
            running = false;
            turnAccepted = false;
            payloadSent = false;
            payloadWriting = false;
        }
        FailControlWaiters(started);
        if (!current)
            return;
        if (kill)
            await KillAndForgetAsync(started).ConfigureAwait(false);
        else
            await CloseProcessAsync(started).ConfigureAwait(false);
    }

    /// <summary>結束一個已卸下的程序樹並截掉它可能寫到一半的逐字稿尾行</summary>
    private async Task KillAndForgetAsync(AgentProcess target)
    {
        string? transcript;
        string? configDirectory;
        AgentSessionSettings? settings;
        lock (gate)
        {
            transcript = processTranscript;
            configDirectory = processConfigDirectory;
            settings = processSettings;
        }
        try
        {
            if (await target.KillTreeAsync(KillTimeout).ConfigureAwait(false) && settings?.SessionId is { } sessionId)
            {
                var path = await Task.Run(() => ResolveTranscript(transcript, configDirectory, sessionId, settings.WorkingDirectory)).ConfigureAwait(false);
                if (path != null)
                    await Task.Run(() => TrimTranscript(path, sessionId)).ConfigureAwait(false);
            }
        }
        finally
        {
            target.Dispose();
        }
    }

    /// <summary>目前程序的逐字稿實際位置：推算的路徑存在就用它，否則找（名稱過長時 CLI 截斷加雜湊）；都找不到回 null</summary>
    private static string? ResolveTranscript(string? transcript, string? configDirectory, string? sessionId, string? workingDirectory)
    {
        if (transcript != null && File.Exists(transcript))
            return transcript;
        if (configDirectory == null || sessionId == null || workingDirectory == null)
            return null;
        return ClaudeStreamRules.FindTranscript(configDirectory, sessionId, workingDirectory);
    }

    /// <summary>逐字稿目前的長度（不存在＝0；沒有路徑或讀不到＝-1，表示不明）</summary>
    private static long MeasureTranscript(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return -1;
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return -1;
        }
    }

    /// <summary>逐字稿的長度（還不存在＝0，讀不到＝-1），以及尾段裡有沒有這個工具的結果列（toolId 為 null 時不找）</summary>
    private static (long Length, bool HasToolResult) ProbeTranscript(string path, string? toolId)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            if (toolId == null || length == 0)
                return (length, false);
            int count = (int)Math.Min(length, TranscriptTailBytes);
            var buffer = new byte[count];
            stream.Position = length - count;
            stream.ReadExactly(buffer, 0, count);
            return (length, ClaudeStreamRules.TranscriptTailHasToolResult(buffer, toolId));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (0, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (-1, false);
        }
    }

    /// <summary>截掉逐字稿寫到一半的尾行（切下的位元組存到 sync\partial，保留 7 天）；只能在寫這個檔的程序都已結束之後呼叫</summary>
    private static void TrimTranscript(string path, string sessionId)
    {
        var result = TranscriptTailRules.TrimPartialLine(path, AppPaths.PartialTailDirectory, sessionId, DateTime.Now);
        switch (result.Outcome)
        {
            case TranscriptTailOutcome.Trimmed:
                Log.Info($"Claude 逐字稿截掉寫到一半的尾行：{path}（{result.OriginalLength} → {result.KeptLength} 位元組，備份 {result.BackupPath}）");
                break;
            case TranscriptTailOutcome.Failed:
                Log.Warn($"Claude 逐字稿截尾失敗：{path}（{result.Error}）");
                break;
        }
    }

    private static async Task<bool> WaitQuietlyAsync(Task task, TimeSpan timeout)
    {
        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            // 等的工作本身失敗（例如程序已釋放）：也算結束了
            return true;
        }
    }

    private static async Task WriteQuietlyAsync(AgentProcess target, string line)
    {
        try
        {
            await target.WriteLineAsync(line);
        }
        catch (IOException)
        {
        }
    }

    private string NextRequestId(string prefix)
        => $"{prefix}-{Interlocked.Increment(ref requestCounter)}";

    private void Raise(AgentEvent ev)
    {
        try
        {
            EventReceived?.Invoke(ev);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClaudeAgentSession] 事件處理失敗：{ex}");
        }
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "原因不明";

    public void Dispose()
    {
        AgentProcess? target;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            target = process;
            process = null;
            running = false;
            stoppedTurn = null;
            autoTurn = null;
            restartInterruptRequested = true;
        }

        FailControlWaiters(null);
        target?.Dispose();
    }

    /// <summary>被重開打斷的一輪</summary>
    /// <param name="SessionId">被打斷那個程序的 session id</param>
    /// <param name="LastStopReason">被打斷時主對話最後一則 assistant 訊息的 stop_reason（判斷這一輪是否其實已經回覆完）</param>
    /// <param name="Recorded">逐字稿已記下這一輪（使用者訊息已寫入）</param>
    /// <param name="CanResume">能以 --resume 續接（逐字稿存在；找不到逐字稿時以 CLI 是否建立過 session 推斷）</param>
    private sealed record InterruptedTurn(string? SessionId, string? LastStopReason, bool Recorded, bool CanResume);

    /// <summary>以系統重開啟動新程序的參數：RESUME 接續訊息（null＝不帶環境變數，測試模擬它失效）、啟動前逐字稿的長度</summary>
    private sealed record AutoTurnStart(string? ResumePrompt, long TranscriptBaseline);

    /// <summary>新程序等 CLI 自動接續的期間（欄位在 gate 內讀寫）</summary>
    private sealed class AutoTurnWatch
    {
        public AutoTurnWatch(AgentProcess process) => Process = process;

        public AgentProcess Process { get; }

        /// <summary>initialize 回應的 Stopwatch 時間戳（0＝還沒回應）</summary>
        public long InitResponseAt;

        /// <summary>看到接續已開始的證據</summary>
        public bool Evidence;

        /// <summary>還沒開始接續就結束了</summary>
        public bool Exited;

        /// <summary>還沒開始接續就回報失敗的原因（例如找不到 session）</summary>
        public string? Failure;
    }

    private enum AutoTurnDecisionKind
    {
        Continued,
        NotContinued,
        Stopped,
        Failed,
    }

    private sealed record AutoTurnDecision(AutoTurnDecisionKind Kind, string? Error = null);
}
