using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// 放開與接回程序（代理主機 H1、H2，設計 docs/plans/2026-11-26-agent-host.md 第 10 節）：app 更新時不關 agy 的程序——閒置而背景工作
/// （背景指令、工作流、排程）還在的、一輪還在執行的都放得開，程序留在代理主機，新的 app（放棄更新時同一個 app）以同一個程序接回。
/// 同 Grok：不必從檢查點重播，解析器的狀態直接匯出、還原（<see cref="AntigravityAcpUpdateParser.ExportState"/>）；
/// session/prompt 的回應還沒到就記下它的請求 id，接回後照常由回應收尾。有等使用者回應的請求、在途的請求、正在停止或重開時不放
/// </summary>
internal sealed partial class AntigravityAcpSession
{
    /// <summary>放開的程序要帶走的狀態（只加欄位；讀不懂的版本當成接不回去）</summary>
    private sealed class DetachState
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public AgentProcessDetachState? Process { get; set; }

        public string? Signature { get; set; }

        public string? RunningVersion { get; set; }

        public string? SessionId { get; set; }

        public string? ProcessSessionId { get; set; }

        public string? PendingTitle { get; set; }

        public string? LastWorkingDirectory { get; set; }

        /// <summary>agy 回報的設定欄位（AntigravityAcpConfigOption 的清單）</summary>
        public JToken? ConfigOptions { get; set; }

        public string? DefaultModelId { get; set; }

        public string? CurrentTokenModel { get; set; }

        public bool PromptImages { get; set; } = true;

        public string? AppliedModel { get; set; }

        public bool ModelKnown { get; set; }

        public string? AppliedEffort { get; set; }

        public string? AppliedModeId { get; set; }

        /// <summary>AgentPermissionMode 的名稱</summary>
        public string? CurrentMode { get; set; }

        /// <summary>已經用掉的最後一個 JSON-RPC 請求 id（接回後接著編）</summary>
        public long LastRequestId { get; set; }

        public List<string> SessionAllowedKinds { get; set; } = new();

        public AntigravityParserState? Parser { get; set; }

        /// <summary>放開時還在執行的一輪的 session/prompt 請求 id（回應還沒到）；null＝閒置</summary>
        public long? PromptRequestId { get; set; }
    }

    /// <summary>放開程序時等主機回覆、等已收到的輸出處理完的上限</summary>
    internal TimeSpan DetachTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>正在放開程序（DetachProcessAsync 途中）</summary>
    private bool detaching;

    /// <summary>目前的程序是經哪個代理主機開的（放開途中反悔時向它接回）；直接開的是 null</summary>
    private IAgentProcessHost? processHost;

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

    public string? DetachBlocker
    {
        get
        {
            lock (gate)
                return DetachBlockerLocked();
        }
    }

    /// <summary>同 <see cref="CanDetachLocked"/> 的順序，回第一個擋住的原因（放得開是 null）</summary>
    private string? DetachBlockerLocked()
    {
        if (disposed)
            return "disposed";
        if (detaching)
            return "detaching";
        if (process == null)
            return "noProcess";
        if (!process.CanDetach)
            return "notHosted";
        if (restart != null)
            return "restarting";
        if (suspendedSession != null)
            return "suspended";
        if (preparing != null)
            return "preparing";
        if (interruptRequested)
            return "interrupting";
        if (pendingRequests.Count > 0)
            return "waitingUser";
        if (connection is not { HasPending: false })
            return "rpc";
        if (processSessionId == null)
            return "noSession";
        if (running && !(promptWritten && promptRequestId != null && ReferenceEquals(promptProcess, process)))
            return "notSent";
        return null;
    }

    /// <summary>有等回覆或正在收尾的東西（放開的話那些回覆沒有人收），或這一輪的訊息還沒送出去（含在本機另外執行的斜線指令）</summary>
    private bool BlockedForDetachLocked()
        => restart != null || suspendedSession != null || preparing != null || interruptRequested
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
        target.StderrReceived += OnStderr;
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
            Log.Warn($"放開 Antigravity 程序失敗：{ex.Message}");
            released = null;
        }

        DetachState? state = null;
        lock (gate)
        {
            detaching = false;
            if (released == null || !ReferenceEquals(process, target))
                return null;   // 沒放開：程序照舊是這個會話的（或這段期間它結束了，OnExited 已經處理）
            // 主機回覆之前送來的輸出都處理完了。那幾行可能讓狀態變了（來了核准請求、這一輪結束了…）：照現在的狀態再看一次
            if (!disposed && !BlockedForDetachLocked())
            {
                state = CaptureLocked(released);
                // 這個會話物件不再有程序、也不再有進行中的一輪（它在主機那邊照跑）；背景工作還在，不發「沒了」
                DetachProcessLocked();
                running = false;
                interruptRequested = false;
                cancelSent = false;
                promptRequestId = null;
                promptProcess = null;
                promptWritten = false;
                parser = new AntigravityAcpUpdateParser("agy", TranscriptPathResolver);
            }
        }

        if (state == null)
        {
            Log.Info("放開 Antigravity 程序的途中狀態變了，接回原本的程序");
            if (!ReplaceDetachedProcess(target, released!))
                OnExited(target, null);   // 接不回去：對這個會話來說程序結束了
            return null;
        }

        // 上次回報的安全點歸零：接回時（這個或新的會話）才會再發一次
        lock (safePointOrder)
        {
            lock (gate)
                reportedSafePoint = null;
        }
        return JObject.FromObject(state);
    }

    private DetachState CaptureLocked(AgentProcessDetachState released) => new()
    {
        Process = released,
        Signature = processSignature,
        RunningVersion = runningVersion,
        SessionId = sessionId,
        ProcessSessionId = processSessionId,
        PendingTitle = pendingTitle,
        LastWorkingDirectory = lastWorkingDirectory,
        ConfigOptions = configOptions.Count == 0 ? null : JToken.FromObject(configOptions),
        DefaultModelId = defaultModelId,
        CurrentTokenModel = currentTokenModel,
        PromptImages = promptImages,
        AppliedModel = appliedModel,
        ModelKnown = modelKnown,
        AppliedEffort = appliedEffort,
        AppliedModeId = appliedModeId,
        CurrentMode = currentMode.ToString(),
        LastRequestId = connection?.LastId ?? 0,
        SessionAllowedKinds = sessionAllowedKinds.ToList(),
        Parser = parser.ExportState(),
        PromptRequestId = running ? promptRequestId : null,
    };

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
            Log.Warn($"Antigravity 程序接不回去：{ex.Message}");
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
            Log.Warn($"Antigravity 放開的狀態讀不懂：{ex.Message}");
            return false;
        }
        var host = settings.ProcessHost;
        if (detached is not { Version: >= 1 and <= DetachState.CurrentVersion, Process: { } released } || host == null
            || string.IsNullOrEmpty(detached.ProcessSessionId) || detached.PromptRequestId is <= 0)
            return false;
        lock (gate)
        {
            if (disposed || process != null || running || restart != null)
                return false;
        }

        IReadOnlyList<AntigravityAcpConfigOption>? restoredOptions = null;
        try
        {
            restoredOptions = detached.ConfigOptions?.ToObject<List<AntigravityAcpConfigOption>>();
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or InvalidCastException or FormatException or ArgumentException)
        {
            // 設定欄位讀不懂不礙事：等 agy 下一次回報
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
            Log.Warn($"Antigravity 程序接不回去：{ex.Message}");
            return false;
        }
        var conn = Wire(attached, detached.LastRequestId);
        AgentBackgroundWorkEvent? work;
        lock (gate)
        {
            if (disposed || process != null || running || restart != null)
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
            pendingTitle = detached.PendingTitle;
            lastWorkingDirectory = detached.LastWorkingDirectory;
            configOptions = restoredOptions is { Count: > 0 } ? restoredOptions : Array.Empty<AntigravityAcpConfigOption>();
            defaultModelId = detached.DefaultModelId;
            currentTokenModel = detached.CurrentTokenModel;
            promptImages = detached.PromptImages;
            appliedModel = detached.AppliedModel;
            modelKnown = detached.ModelKnown;
            appliedEffort = detached.AppliedEffort;
            appliedModeId = detached.AppliedModeId;
            currentMode = Enum.TryParse<AgentPermissionMode>(detached.CurrentMode, out var mode) ? mode : settings.PermissionMode;
            sessionAllowedKinds.UnionWith((detached.SessionAllowedKinds ?? new List<string>()).Where(k => !string.IsNullOrEmpty(k)));
            parser = detached.Parser != null
                ? AntigravityAcpUpdateParser.FromState(detached.Parser, TranscriptPathResolver)
                : new AntigravityAcpUpdateParser("agy-" + attached.InstanceId[..8], TranscriptPathResolver);
            parser.BindSession(detached.ProcessSessionId);
            interruptRequested = false;
            cancelSent = false;
            suspendedSession = null;
            if (detached.PromptRequestId is { } prompt)
            {
                // 這一輪照舊在執行：session/prompt 的回應還沒到，到了照常收尾
                running = true;
                promptRequestId = prompt;
                promptProcess = attached;
                promptWritten = true;
            }
            work = parser.SnapshotBackgroundWork();
        }

        // 同啟動：先回報程序（同一個實例代號）與版本，再回報背景工作（中樞要先知道哪些還在跑），然後才開始讀放開期間的輸出
        Raise(new AgentProcessStartedEvent(attached.InstanceId, settings.Cli));
        if (detached.RunningVersion != null)
            Raise(new AgentRunningVersionEvent(attached.InstanceId, detached.RunningVersion));
        if (work != null)
            Raise(work);
        PublishSafePoint();
        attached.BeginReading();
        // 單次計時器的到期檢查跟著程序走：重新排
        ScheduleTimerExpiry();
        return true;
    }
}
