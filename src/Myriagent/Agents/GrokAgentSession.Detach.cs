using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// 放開與接回程序（代理主機 H1、H2，設計 docs/plans/2026-11-26-agent-host.md 第 10 節）：app 更新時不關 grok 的程序——閒置而背景工作在跑的、
/// 一輪還在執行的都放得開，程序留在代理主機，新的 app（放棄更新時同一個 app）以同一個程序接回。
/// 和 Claude／Codex 不同，這裡不必從檢查點重播：grok 的 ACP 沒有跨行累積的串流狀態，解析器一輪中的狀態（開著的文字區塊、這一輪的工具）
/// 直接匯出、還原（<see cref="GrokUpdateParser.ExportState"/>）。session/prompt 的回應還沒到：記下它的請求 id，接回後照常由回應收尾。
/// 有等使用者回應的請求、在途的請求（設定、額度查詢）、正在停止或重開時不放
/// </summary>
internal sealed partial class GrokAgentSession
{
    /// <summary>放開的程序要帶走的狀態（只加欄位；讀不懂的版本當成接不回去）</summary>
    private sealed class DetachState
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public AgentProcessDetachState? Process { get; set; }

        public string? Signature { get; set; }

        public string? RunningVersion { get; set; }

        /// <summary>這個對話在 grok 的 id、程序上已接上的對話</summary>
        public string? SessionId { get; set; }

        public string? ProcessSessionId { get; set; }

        public string? LastWorkingDirectory { get; set; }

        /// <summary>grok 回報的模型清單（AgentModelOption 的清單）</summary>
        public JToken? Models { get; set; }

        public string? DefaultModelId { get; set; }

        public Dictionary<string, long> ContextWindows { get; set; } = new();

        public Dictionary<string, long[]> ContextChoices { get; set; } = new();

        public string? ContextModel { get; set; }

        public bool PromptImages { get; set; }

        public string? AppliedModel { get; set; }

        public bool ModelKnown { get; set; }

        public string? AppliedEffort { get; set; }

        /// <summary>AgentPermissionMode 的名稱</summary>
        public string? AppliedPermission { get; set; }

        public bool? AppliedPlan { get; set; }

        /// <summary>已經用掉的最後一個 JSON-RPC 請求 id（接回後接著編）</summary>
        public long LastRequestId { get; set; }

        public List<DetachedBackground> Background { get; set; } = new();

        public List<string> SessionAllowedKinds { get; set; } = new();

        /// <summary>解析器的狀態（區塊編號；一輪中另有開著的區塊與工具）</summary>
        public GrokParserState? Parser { get; set; }

        /// <summary>放開時還在執行的一輪；null＝閒置</summary>
        public DetachedTurn? Turn { get; set; }
    }

    private sealed class DetachedBackground
    {
        public string TaskId { get; set; } = string.Empty;

        public string? ToolCallId { get; set; }

        public AgentBackgroundTool? Tool { get; set; }

        public string? OutputFile { get; set; }
    }

    private sealed class DetachedTurn
    {
        /// <summary>這一輪的 session/prompt 的請求 id（回應還沒到）</summary>
        public long PromptRequestId { get; set; }

        public bool PromptIsCompact { get; set; }

        /// <summary>AgentPermissionMode 的名稱</summary>
        public string? Mode { get; set; }

        public string? WorkingDirectory { get; set; }

        public string? ConversationDirectory { get; set; }

        public bool AutoCompactSeenDuringManual { get; set; }

        public bool SuppressLateAutoCompact { get; set; }
    }

    /// <summary>放開程序時等主機回覆、等已收到的輸出處理完的上限</summary>
    internal TimeSpan DetachTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>正在放開程序（DetachProcessAsync 途中）</summary>
    private bool detaching;

    /// <summary>目前的程序是經哪個代理主機開的（放開途中反悔時向它接回）；直接開的是 null</summary>
    private IAgentProcessHost? processHost;

    /// <summary>
    /// 一輪的回應到了、還在讀取執行緒之外收尾（查額度之後才發 TurnCompleted，FinishTurnAfterBilling）：這段期間不放——
    /// 不然中樞以為這一輪還在執行、帶著它過去，而結束的事件永遠不會來
    /// </summary>
    private int turnFinishing;

    public bool CanDetachProcess
    {
        get
        {
            lock (gate)
                return CanDetachLocked();
        }
    }

    private bool CanDetachLocked()
        => !disposed && !detaching && process is { CanDetach: true } && !BlockedForDetachLocked();

    /// <summary>有等回覆或正在收尾的東西（放開的話那些回覆沒有人收），或這一輪的訊息還沒送出去</summary>
    private bool BlockedForDetachLocked()
        => restart != null || suspendedSession != null || attachDone != null || preparing != null || interruptRequested || turnFinishing > 0
           || pendingRequests.Count > 0 || connection is not { HasPending: false } || processSessionId == null
           || (running && !(promptWritten && promptRequestId != null && ReferenceEquals(promptProcess, process)));

    /// <summary>接上一個程序的 ACP 連線與事件（啟動、接回、放開途中反悔都經這裡）</summary>
    private GrokAcpConnection Wire(AgentProcess target, long lastRequestId = 0)
    {
        var conn = new GrokAcpConnection(target, lastRequestId);
        conn.Response += message => OnResponse(target, message);
        conn.Request += message => OnRequest(target, message);
        conn.Notification += message => OnNotification(target, message);
        target.LineReceived += (_, line) => conn.OnLine(line);
        target.Exited += OnExited;
        return conn;
    }

    public async Task<JObject?> DetachProcessAsync()
    {
        AgentProcess target;
        lock (gate)
        {
            if (!CanDetachLocked())
                return null;
            target = process!;
            detaching = true;
        }

        AgentProcessDetachState? released;
        try
        {
            released = await target.DetachAsync(DetachTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"放開 Grok 程序失敗：{ex.Message}");
            released = null;
        }

        DetachState? state = null;
        lock (gate)
        {
            detaching = false;
            if (released == null || !ReferenceEquals(process, target))
                return null;   // 沒放開：程序照舊是這個會話的（或這段期間它結束了，OnExited 已經處理）
            // 主機回覆之前送來的輸出都處理完了。那幾行可能讓狀態變了（來了核准請求、這一輪結束正在查額度…）：照現在的狀態再看一次
            if (!disposed && !BlockedForDetachLocked())
            {
                state = CaptureLocked(released);
                // 這個會話物件不再有程序、也不再有進行中的一輪（它在主機那邊照跑）；背景工作還在跑，不發「沒了」
                DetachProcessLocked();
                processHost = null;
                running = false;
                interruptRequested = false;
                cancelSent = false;
                promptRequestId = null;
                promptProcess = null;
                promptIsCompact = false;
                promptWritten = false;
                parser = new GrokUpdateParser();
            }
        }

        if (state == null)
        {
            Log.Info("放開 Grok 程序的途中狀態變了，接回原本的程序");
            if (!ReplaceDetachedProcess(target, released!))
                OnExited(target, null);   // 接不回去：對這個會話來說程序結束了
            return null;
        }

        // 上次回報的安全點歸零：接回時（這個或新的會話）才會再發一次
        lock (safePointOrder)
            reportedSafePoint = new AgentSafePointChangedEvent(false, false, false);
        return JObject.FromObject(state);
    }

    private DetachState CaptureLocked(AgentProcessDetachState released)
    {
        var state = new DetachState
        {
            Process = released,
            Signature = processSignature,
            RunningVersion = runningVersion,
            SessionId = sessionId,
            ProcessSessionId = processSessionId,
            LastWorkingDirectory = lastWorkingDirectory,
            Models = models == null ? null : JToken.FromObject(models),
            DefaultModelId = defaultModelId,
            ContextWindows = new Dictionary<string, long>(contextWindows, StringComparer.Ordinal),
            ContextChoices = new Dictionary<string, long[]>(contextChoices, StringComparer.Ordinal),
            ContextModel = contextModel,
            PromptImages = promptImages,
            AppliedModel = appliedModel,
            ModelKnown = modelKnown,
            AppliedEffort = appliedEffort,
            AppliedPermission = appliedPermission?.ToString(),
            AppliedPlan = appliedPlan,
            LastRequestId = connection?.LastId ?? 0,
            Background = background.Select(b => new DetachedBackground
            {
                TaskId = b.Key,
                ToolCallId = b.Value.ToolCallId,
                Tool = b.Value.Tool,
                OutputFile = b.Value.OutputFile,
            }).ToList(),
            SessionAllowedKinds = sessionAllowedKinds.ToList(),
            Parser = parser.ExportState(),
        };
        if (running && promptRequestId is { } prompt)
        {
            state.Turn = new DetachedTurn
            {
                PromptRequestId = prompt,
                PromptIsCompact = promptIsCompact,
                Mode = currentMode.ToString(),
                WorkingDirectory = currentWorkingDirectory,
                ConversationDirectory = currentConversationDirectory,
                AutoCompactSeenDuringManual = autoCompactSeenDuringManual,
                SuppressLateAutoCompact = suppressLateAutoCompact,
            };
        }
        return state;
    }

    /// <summary>放開之後馬上反悔：以同一個位移重新接上，換掉會話手上已經不能用的程序物件與連線（在途的請求跟著搬）；接不回去回 false</summary>
    private bool ReplaceDetachedProcess(AgentProcess old, AgentProcessDetachState released)
    {
        var host = processHost;
        if (host == null)
            return false;
        AgentProcess attached;
        try
        {
            attached = AgentProcess.Attach(host, released);
        }
        catch (AgentHostUnavailableException ex)
        {
            Log.Warn($"Grok 程序接不回去：{ex.Message}");
            return false;
        }
        GrokAcpConnection? previous;
        lock (gate)
            previous = connection;
        var conn = Wire(attached, previous?.LastId ?? 0);
        lock (gate)
        {
            if (disposed || !ReferenceEquals(process, old))
            {
                attached.Kill();
                return false;
            }
            if (previous != null)
                conn.AdoptPending(previous);
            process = attached;
            connection = conn;
            if (ReferenceEquals(promptProcess, old))
                promptProcess = attached;
            if (attachedFresh is { } fresh && ReferenceEquals(fresh.Process, old))
                attachedFresh = (attached, fresh.CreatedNew, fresh.LostHistory);
            foreach (var key in pendingRequests.Where(p => ReferenceEquals(p.Value.Process, old)).Select(p => p.Key).ToList())
                pendingRequests[key] = pendingRequests[key] with { Process = attached };
        }
        attached.BeginReading();
        return true;
    }

    public async Task<bool> AttachProcessAsync(JObject state, AgentSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        DetachState? detached;
        try
        {
            detached = state.ToObject<DetachState>();
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or InvalidCastException or FormatException or ArgumentException)
        {
            Log.Warn($"Grok 放開的狀態讀不懂：{ex.Message}");
            return false;
        }
        var host = settings.ProcessHost;
        if (detached is not { Version: >= 1 and <= DetachState.CurrentVersion, Process: { } released } || host == null
            || string.IsNullOrEmpty(detached.ProcessSessionId))
            return false;
        if (detached.Turn is { PromptRequestId: <= 0 })
            return false;
        lock (gate)
        {
            if (disposed || process != null || running || restart != null || attachDone != null)
                return false;
        }

        IReadOnlyList<AgentModelOption>? restoredModels = null;
        try
        {
            restoredModels = detached.Models?.ToObject<List<AgentModelOption>>();
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or InvalidCastException or FormatException or ArgumentException)
        {
            // 模型清單讀不懂不礙事：等 grok 下一次回報
        }
        // 共用設定（共用記憶的資料夾：自動接受編輯要用）照啟動程序時的做法準備
        var shared = await settings.PrepareSharedAsync().ConfigureAwait(false);

        AgentProcess attached;
        try
        {
            attached = AgentProcess.Attach(host, released);
        }
        catch (AgentHostUnavailableException ex)
        {
            Log.Warn($"Grok 程序接不回去：{ex.Message}");
            return false;
        }
        var conn = Wire(attached, detached.LastRequestId);
        AgentBackgroundWorkEvent? work = null;
        lock (gate)
        {
            if (disposed || process != null || running || restart != null || attachDone != null)
            {
                attached.Kill();
                return false;
            }
            process = attached;
            connection = conn;
            processHost = host;
            sharedLaunch = shared;
            processSignature = detached.Signature ?? Signature(settings);
            processCli = settings.Cli;
            runningVersion = detached.RunningVersion ?? settings.Cli?.Version;
            sessionId = detached.SessionId ?? detached.ProcessSessionId;
            processSessionId = detached.ProcessSessionId;
            lastWorkingDirectory = detached.LastWorkingDirectory;
            if (restoredModels is { Count: > 0 })
                models = restoredModels;
            defaultModelId = detached.DefaultModelId;
            contextWindows.Clear();
            foreach (var (model, window) in detached.ContextWindows ?? new Dictionary<string, long>())
                contextWindows[model] = window;
            contextChoices.Clear();
            foreach (var (model, choices) in detached.ContextChoices ?? new Dictionary<string, long[]>())
            {
                if (choices != null)
                    contextChoices[model] = choices;
            }
            contextModel = detached.ContextModel;
            promptImages = detached.PromptImages;
            appliedModel = detached.AppliedModel;
            modelKnown = detached.ModelKnown;
            appliedEffort = detached.AppliedEffort;
            appliedPermission = Enum.TryParse<AgentPermissionMode>(detached.AppliedPermission, out var permission) ? permission : null;
            appliedPlan = detached.AppliedPlan;
            sessionAllowedKinds.UnionWith((detached.SessionAllowedKinds ?? new List<string>()).Where(k => !string.IsNullOrEmpty(k)));
            parser = detached.Parser != null ? GrokUpdateParser.FromState(detached.Parser) : new GrokUpdateParser("grok-" + attached.InstanceId[..8]);
            background.Clear();
            foreach (var b in detached.Background ?? new List<DetachedBackground>())
            {
                if (b?.Tool != null && !string.IsNullOrEmpty(b.TaskId))
                    background[b.TaskId] = new BackgroundWork(b.ToolCallId, b.Tool, GrokAcpRules.AcceptOutputFile(b.OutputFile));
            }
            interruptRequested = false;
            cancelSent = false;
            suspendedSession = null;
            if (detached.Turn is { } turn)
            {
                // 這一輪照舊在執行：session/prompt 的回應還沒到，到了照常收尾
                running = true;
                promptRequestId = turn.PromptRequestId;
                promptProcess = attached;
                promptWritten = true;
                promptIsCompact = turn.PromptIsCompact;
                currentMode = Enum.TryParse<AgentPermissionMode>(turn.Mode, out var mode) ? mode : settings.PermissionMode;
                currentWorkingDirectory = turn.WorkingDirectory ?? settings.WorkingDirectory;
                currentConversationDirectory = turn.ConversationDirectory ?? settings.ConversationDirectory;
                autoCompactSeenDuringManual = turn.AutoCompactSeenDuringManual;
                suppressLateAutoCompact = turn.SuppressLateAutoCompact;
            }
            if (background.Count > 0)
                work = SnapshotBackgroundLocked();
        }

        // 同啟動：先回報程序（同一個實例代號）與版本，再回報背景工作（中樞要先知道哪些卡片是背景指令），然後才開始讀放開期間的輸出
        Raise(new AgentProcessStartedEvent(attached.InstanceId, settings.Cli));
        if (detached.RunningVersion != null)
            Raise(new AgentRunningVersionEvent(attached.InstanceId, detached.RunningVersion));
        if (work != null)
            Raise(work);
        PublishSafePoint();
        attached.BeginReading();
        return true;
    }
}
