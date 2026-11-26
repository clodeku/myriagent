using System.Diagnostics;
using Myriagent.Cli;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// 放開與接回程序（設計 docs/plans/2026-11-26-agent-host.md 3.3、第 10 節）：app 換版時不關 CLI 程序——把程序留在代理主機、
/// 記下接回要用的狀態；新的 app（或放棄更新後的同一個 app）以同一個程序接著用，進行中的一輪、工具與背景工作都不受影響。
/// <list type="bullet">
/// <item>H1：閒置、沒有任何在途的事時放開，帶過去的只有程序的設定與背景工作的登記表（狀態的 Version＝1）。</item>
/// <item>H2：一輪進行中也放開。每則 result 之後記一個檢查點（stdout 位移＋解析器的跨輪狀態），主機從那裡起留著輸出；
/// 接回時解析器從檢查點還原、把檢查點到放開位移之間的輸出重播一遍（只給解析器、不發事件、不寫 stdin），一輪之內的解析器狀態就回來了。
/// 會話自己的一輪中旗標（這一輪是哪則訊息、CLI 接手了沒有…）在放開當下另外記（Version＝2）。</item>
/// </list>
/// 不放的時候：stdin 寫到一半、有控制請求在等回覆、有等使用者回應的請求（卡片的回覆要寫回這個程序）、正在停止或重開、
/// 掛著委派 MCP（那條管道是這個 app 的，新的 app 接不上）
/// </summary>
internal sealed partial class ClaudeAgentSession
{
    /// <summary>
    /// 放開時記下的狀態（JSON；寫進 resume.json）。只加欄位：新版讀舊版寫的要讀得懂，讀不懂（Version 不認得）就當成接不回去
    /// </summary>
    internal sealed class DetachState
    {
        /// <summary>閒置、靜止時放開的（H1 就讀得懂）</summary>
        public const int IdleVersion = 1;

        /// <summary>帶檢查點與一輪中旗標的（H2）：讀不懂它的版本接不回去，照被中斷的一輪處理</summary>
        public const int TurnVersion = 2;

        public int Version { get; set; } = IdleVersion;

        public AgentProcessDetachState? Process { get; set; }

        // ── 程序啟動時的設定裡，決定「下次送出要不要重開」的那些（新的 app 重新算出來的設定可能已經不同：照這一份比） ──
        public string? SessionId { get; set; }

        public string? Model { get; set; }

        public string? Effort { get; set; }

        public string WorkingDirectory { get; set; } = string.Empty;

        public bool UseLocalCustomizations { get; set; }

        public string AccountKey { get; set; } = string.Empty;

        public string? LauncherFile { get; set; }

        public string? CliVersion { get; set; }

        public string? CliExecutable { get; set; }

        public string? FallbackModel { get; set; }

        public string? Agent { get; set; }

        public string? OutputStyle { get; set; }

        public string? ResumeAt { get; set; }

        public long? AutoCompactTokens { get; set; }

        // ── 程序的狀態 ──
        public string? RunningVersion { get; set; }

        public AgentPermissionMode Mode { get; set; }

        public bool InitSeen { get; set; }

        public int RequestCounter { get; set; }

        public string? Transcript { get; set; }

        public string? ConfigDirectory { get; set; }

        public long StartedAtMs { get; set; }

        public string? Goal { get; set; }

        public bool ModelSwapped { get; set; }

        public List<string> SessionRules { get; set; } = new();

        public List<string> SessionDirectories { get; set; } = new();

        public string? Title { get; set; }

        // ── 背景工作（解析器的登記表：清單、各是哪個工具開始的、種類；放開當下的值，H1 的接回用它） ──
        public List<string> BackgroundTasks { get; set; } = new();

        public Dictionary<string, AgentBackgroundTool> TaskTools { get; set; } = new();

        public Dictionary<string, string> TaskTypes { get; set; } = new();

        // ── Remote Control ──
        public bool RemoteControlActive { get; set; }

        public bool RemoteControlEnableSent { get; set; }

        public AgentRemoteControlOptions? RemoteControl { get; set; }

        public string? RemoteBridgeSessionId { get; set; }

        public string? RemoteBridgeAccountKey { get; set; }

        // ── H2：檢查點與重播 ──

        /// <summary>檢查點的 stdout 位移（最後一則 result 結束的位置；程序還沒有 result 時是 0）：接回時從這裡重播到 Process.OutOffset</summary>
        public long? ReplayOut { get; set; }

        /// <summary>檢查點當時解析器的跨輪狀態</summary>
        public ClaudeParserCheckpoint? Parser { get; set; }

        /// <summary>放開當下「上一則 result 之後已記過的 token」（含子代理逐字稿讀到的：重播重建不出來）</summary>
        public Dictionary<string, long[]>? Counted { get; set; }

        /// <summary>會話在放開當下的一輪中旗標；null＝閒置（全是預設值）</summary>
        public TurnState? Turn { get; set; }
    }

    /// <summary>會話自己的一輪中狀態（不在 stdout 裡、重播重建不出來的：這一輪是誰開始的、CLI 接手了沒有、看過哪些指令）</summary>
    internal sealed class TurnState
    {
        public bool Running { get; set; }

        public bool PayloadSent { get; set; }

        public bool TurnAccepted { get; set; }

        public bool ToolRunning { get; set; }

        public bool CliOwnTurn { get; set; }

        public bool AwaitingQueuedTurn { get; set; }

        public string? TurnUuid { get; set; }

        public bool TurnUuidStarted { get; set; }

        public bool TurnUuidQueued { get; set; }

        public bool RemoteTurn { get; set; }

        public bool CliTurn { get; set; }

        /// <summary>看過的指令 uuid（依看到的順序）：分辨別一輪的 result 用</summary>
        public List<string> CommandUuids { get; set; } = new();

        /// <summary>背景工作完成的通知還在等 CLI 自己開一輪</summary>
        public bool NotificationTurn { get; set; }

        public string? LastToolResultId { get; set; }

        public long TranscriptBaseline { get; set; } = -1;

        /// <summary>本對話放行的「完全相同的請求」</summary>
        public List<string> SessionAllowedRequests { get; set; } = new();
    }

    /// <summary>檢查點：最後一則 result 處理完的位置與當時解析器的跨輪狀態（程序啟動時是位移 0 與剛建立的解析器）</summary>
    private sealed record Checkpoint(long Offset, ClaudeParserCheckpoint Parser);

    /// <summary>接回之後正在重播（檢查點到放開位移之間的輸出只給解析器）</summary>
    private sealed class ReplayState
    {
        public required long Until { get; init; }

        public Dictionary<string, long[]>? Counted { get; init; }

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>放開程序時等主機回覆、等已收到的輸出處理完的上限</summary>
    internal TimeSpan DetachTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>接回時等重播做完的上限（一輪的輸出最多留 32 MB）</summary>
    internal TimeSpan ReplayTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>正在放開程序（DetachProcessAsync 途中）：這段期間不能用這個程序送東西</summary>
    private bool detaching;

    /// <summary>目前程序的檢查點；null＝沒有（直接開的程序、這一輪的輸出太多被主機放掉）</summary>
    private Checkpoint? checkpoint;

    private ReplayState? replay;

    public bool CanDetachProcess
    {
        get
        {
            lock (gate)
                return CanDetachLocked();
        }
    }

    /// <summary>
    /// 現在放得開：程序經主機開、主機會保留它，沒有寫到一半或等回覆的東西。一輪進行中（工具在跑、回覆串流到一半、CLI 自己的一輪）
    /// 要有檢查點（主機還留著這一輪的輸出）；沒有檢查點時只有完全靜止才放（H1 的做法）
    /// </summary>
    private bool CanDetachLocked()
    {
        if (disposed || detaching || replay != null || process is not { CanDetach: true } || processSettings is not { DelegationMcpPipeName: null })
            return false;
        if (BlockedForDetachLocked())
            return false;
        return HasCheckpointLocked() || IsQuiescentLocked();
    }

    /// <summary>有寫到一半、等回覆或正在收尾的東西：放開的話那些回覆沒有人收</summary>
    private bool BlockedForDetachLocked()
        => restarting || payloadWriting || autoTurn != null || reattachCompletion != null || interruptRequested
           || pendingRequests.Count > 0 || controlWaiters.Count > 0 || pendingSteer != null || remoteControlRequestId != null
           // 這一輪的訊息還沒交給 CLI（啟動中、寫入前）
           || (running && !payloadSent);

    /// <summary>完全靜止：沒有進行中的一輪、CLI 也沒有自己在忙（H1 放得開的條件）</summary>
    private bool IsQuiescentLocked() => !running && !cliOwnTurn && notificationTurnSince == 0;

    /// <summary>檢查點還在：主機從那個位移起的輸出都留著</summary>
    private bool HasCheckpointLocked() => checkpoint is { } held && process != null && process.OutputHeldFrom == held.Offset;

    /// <summary>程序啟動、每則 result 處理完：記下檢查點，主機從這裡起留著輸出（讀取執行緒或啟動時呼叫）</summary>
    private void UpdateCheckpoint(AgentProcess source, ClaudeStreamParser currentParser)
    {
        long offset = source.StdoutOffset;
        if (offset < 0 || !source.CanDetach)
            return;
        var exported = currentParser.ExportCheckpoint();
        lock (gate)
        {
            if (!ReferenceEquals(process, source))
                return;
            checkpoint = new Checkpoint(offset, exported);
        }
        source.HoldOutputFrom(offset);
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
            Log.Warn($"放開 Claude 程序失敗：{ex.Message}");
            released = null;
        }

        DetachState? state = null;
        lock (gate)
        {
            detaching = false;
            if (released == null || !ReferenceEquals(process, target))
                return null;   // 沒放開：程序照舊是這個會話的（或這段期間它結束了，OnExited 已經處理）
            // 主機回覆之前送來的輸出都處理完了。那幾行可能讓狀態變了（多了等回應的請求、這一輪結束了…）：照現在的狀態再看一次
            var held = checkpoint is { } cp && cp.Offset <= released.OutOffset && target.OutputHeldFrom == cp.Offset ? cp : null;
            if (!BlockedForDetachLocked() && (held != null || IsQuiescentLocked()))
            {
                state = CaptureLocked(released, held);
                process = null;
                // 這個會話物件不再有進行中的一輪（那一輪在主機那邊照跑）；不發「這一輪結束了」「背景工作沒了」，
                // 中樞照舊顯示它們（接回或換版後由會話重新回報）
                FinishTurnLocked(new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null));
                ResetProcessStateLocked();
                lastBackgroundCount = 0;
                lastBackgroundTools = Array.Empty<AgentBackgroundTool>();
                lastSafePoint = default;
            }
        }

        if (state == null)
        {
            Log.Info("放開 Claude 程序的途中狀態變了，接回原本的程序");
            // 只把程序物件換成重新接上的那一個（同一個實例代號）：會話與解析器的狀態原封不動
            if (!ReplaceDetachedProcess(target, released!))
                OnDetachedProcessLost(target);
            return null;
        }
        return JObject.FromObject(state);
    }

    /// <summary>放開之後馬上反悔：以同一個位移重新接上，換掉會話手上已經不能用的程序物件；接不回去回 false</summary>
    private bool ReplaceDetachedProcess(AgentProcess old, AgentProcessDetachState released)
    {
        var host = processSettings?.ProcessHost;
        if (host == null)
            return false;
        AgentProcess attached;
        try
        {
            attached = AgentProcess.Attach(host, released);
        }
        catch (AgentHostUnavailableException ex)
        {
            Log.Warn($"Claude 程序接不回去：{ex.Message}");
            return false;
        }
        attached.LineReceived += OnLine;
        attached.Exited += OnExited;
        long? hold;
        lock (gate)
        {
            if (disposed || !ReferenceEquals(process, old))
            {
                attached.Kill();
                return false;
            }
            process = attached;
            if (autoTurn is { } watch && ReferenceEquals(watch.Process, old))
                autoTurn = null;
            // 主機在放開時不會丟掉留著的輸出：檢查點照舊有效，新的程序物件接著留
            hold = checkpoint?.Offset;
        }
        if (hold is { } offset)
            attached.HoldOutputFrom(offset);
        attached.BeginReading();
        return true;
    }

    private DetachState CaptureLocked(AgentProcessDetachState released, Checkpoint? held)
    {
        var settings = processSettings!;
        var background = parser.ExportBackground();
        bool quiescent = IsQuiescentLocked();
        return new DetachState
        {
            // 靜止時放開的寫成舊格式的版本（另外帶檢查點）：回滾到只懂 H1 的版本也接得回去
            Version = quiescent ? DetachState.IdleVersion : DetachState.TurnVersion,
            Process = released,
            SessionId = settings.SessionId,
            Model = settings.Model,
            Effort = settings.Effort,
            WorkingDirectory = settings.WorkingDirectory,
            UseLocalCustomizations = settings.UseLocalCustomizations,
            AccountKey = settings.AccountKey,
            LauncherFile = settings.Launcher?.FileName,
            CliVersion = settings.Cli?.Version,
            CliExecutable = settings.Cli?.NativeExecutable,
            FallbackModel = settings.FallbackModel,
            Agent = settings.Agent,
            OutputStyle = settings.OutputStyle,
            ResumeAt = settings.ResumeAt,
            AutoCompactTokens = settings.AutoCompactTokens,
            RunningVersion = runningVersion,
            Mode = currentMode,
            InitSeen = initSeen,
            RequestCounter = requestCounter,
            Transcript = processTranscript,
            ConfigDirectory = processConfigDirectory,
            StartedAtMs = processStartedAt.ToUnixTimeMilliseconds(),
            Goal = reportedGoal,
            ModelSwapped = modelSwapped,
            SessionRules = sessionRules.ToList(),
            SessionDirectories = sessionDirectories.ToList(),
            Title = sessionTitle,
            BackgroundTasks = background.Tasks,
            TaskTools = background.Tools,
            TaskTypes = background.Types,
            RemoteControlActive = remoteControlActive,
            RemoteControlEnableSent = remoteControlEnableSent,
            RemoteControl = remoteControlOptions,
            RemoteBridgeSessionId = remoteBridgeSessionId,
            RemoteBridgeAccountKey = remoteBridgeAccountKey,
            ReplayOut = held?.Offset,
            Parser = held?.Parser,
            Counted = held != null ? parser.ExportCounted() : null,
            Turn = quiescent
                ? null
                : new TurnState
                {
                    Running = running,
                    PayloadSent = payloadSent,
                    TurnAccepted = turnAccepted,
                    ToolRunning = toolRunning,
                    CliOwnTurn = cliOwnTurn,
                    AwaitingQueuedTurn = awaitingQueuedTurn,
                    TurnUuid = turnUuid,
                    TurnUuidStarted = turnUuidStarted,
                    TurnUuidQueued = turnUuidQueued,
                    RemoteTurn = remoteTurn,
                    CliTurn = cliTurn,
                    CommandUuids = commandOrder.ToList(),
                    NotificationTurn = notificationTurnSince != 0,
                    LastToolResultId = lastToolResultId,
                    TranscriptBaseline = transcriptBaseline,
                    SessionAllowedRequests = sessionAllowedRequests.ToList(),
                },
        };
    }

    public Task<bool> AttachProcessAsync(JObject state, AgentSessionSettings settings)
    {
        DetachState? parsed;
        try
        {
            parsed = state.ToObject<DetachState>();
        }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or InvalidCastException or FormatException)
        {
            parsed = null;
        }
        if (parsed is not { Version: DetachState.IdleVersion or DetachState.TurnVersion, Process: not null } || settings.ProcessHost == null)
            return Task.FromResult(false);
        // 一輪中放開的一定要有檢查點才接得回去（解析器一輪之內的狀態靠重播）
        if (parsed.Version == DetachState.TurnVersion && (parsed.Parser == null || parsed.ReplayOut == null))
            return Task.FromResult(false);
        lock (gate)
        {
            if (disposed || process != null || running || restarting)
                return Task.FromResult(false);
        }
        // 接回要等主機回覆、補送暫存的輸出：不佔呼叫端的執行緒（中樞在 UI 執行緒呼叫）
        return Task.Run(() => AttachCore(parsed, settings, settings.ProcessHost));
    }

    /// <summary>以放開時的狀態接上同一個程序；接不回去回 false（會話照舊沒有程序）</summary>
    private bool AttachCore(DetachState state, AgentSessionSettings settings, IAgentProcessHost? host)
    {
        if (host == null || state.Process == null)
            return false;
        bool fromCheckpoint = state is { Parser: not null, ReplayOut: >= 0 } && state.ReplayOut <= state.Process.OutOffset;
        AgentProcess attached;
        try
        {
            attached = AgentProcess.Attach(host, state.Process, fromCheckpoint ? state.ReplayOut : null);
        }
        catch (AgentHostUnavailableException ex)
        {
            Log.Info($"Claude 程序接不回去：{ex.Message}");
            return false;
        }

        attached.LineReceived += OnLine;
        attached.Exited += OnExited;
        // 程序是照放開時記下的設定啟動的：之後的送出拿它和新的設定比，不同才重開（背景工作結束之前中樞不會讓它重開）。
        // CLI 的身分只在版本與執行檔都對得上目前安裝的那一份時沿用，否則當成不明（不明不構成重開的理由）
        var cli = settings.Cli is { } current && string.Equals(current.Version, state.CliVersion, StringComparison.Ordinal)
                  && string.Equals(current.NativeExecutable, state.CliExecutable, StringComparison.OrdinalIgnoreCase)
            ? current
            : null;
        var launched = settings with
        {
            SessionId = state.SessionId,
            SessionStarted = true,
            Model = state.Model,
            Effort = state.Effort,
            WorkingDirectory = state.WorkingDirectory,
            UseLocalCustomizations = state.UseLocalCustomizations,
            AccountKey = state.AccountKey,
            Launcher = settings.Launcher is { } launcher && state.LauncherFile != null ? launcher with { FileName = state.LauncherFile } : settings.Launcher,
            Cli = cli,
            FallbackModel = state.FallbackModel,
            Agent = state.Agent,
            OutputStyle = state.OutputStyle,
            ResumeAt = state.ResumeAt,
            DelegationMcpPipeName = null,
            AutoCompactTokens = state.AutoCompactTokens,
            PermissionMode = state.Mode,
        };
        ReplayState? replaying = null;
        bool unwatched;
        lock (gate)
        {
            if (disposed || process != null || running)
            {
                // 這段期間會話被關掉或另外開了程序：這個接回的不要了（不結束它，留給主機的逾時收掉）
                attached.LineReceived -= OnLine;
                attached.Exited -= OnExited;
                _ = attached.DetachAsync(DetachTimeout);
                return false;
            }
            process = attached;
            ClaudeStreamParser restored;
            if (fromCheckpoint)
            {
                restored = ClaudeStreamParser.FromCheckpoint(state.Parser!);
                restored.ApiKeyNoticeShown |= parser.ApiKeyNoticeShown;
                if (state.ReplayOut < state.Process.OutOffset)
                    replaying = new ReplayState { Until = state.Process.OutOffset, Counted = state.Counted };
                else
                    restored.RestoreCounted(state.Counted);
            }
            else
            {
                // H1 的狀態（沒有檢查點）：背景工作的登記表照放開當下的
                restored = new ClaudeStreamParser { ApiKeyNoticeShown = parser.ApiKeyNoticeShown, FreshSession = false };
                restored.RestoreBackground(state.BackgroundTasks, state.TaskTools, state.TaskTypes);
            }
            parser = restored;
            processSettings = launched;
            lastSettings ??= launched;
            processConfigDirectory = state.ConfigDirectory;
            processTranscript = state.Transcript;
            EnsureSubagentTailLocked();
            processStartedAt = DateTimeOffset.FromUnixTimeMilliseconds(state.StartedAtMs);
            pendingRequests.Clear();
            ResetProcessStateLocked();
            // ResetProcessStateLocked 清掉了檢查點與重播：現在才設
            checkpoint = fromCheckpoint ? new Checkpoint(state.ReplayOut!.Value, state.Parser!) : null;
            replay = replaying;
            reportedGoal = state.Goal;
            currentMode = state.Mode;
            initSeen = state.InitSeen;
            resumedProcess = true;
            runningVersion = state.RunningVersion;
            modelSwapped = state.ModelSwapped;
            requestCounter = Math.Max(requestCounter, state.RequestCounter);
            if (sessionRules.Count == 0)
                sessionRules.AddRange(state.SessionRules);
            if (sessionDirectories.Count == 0)
                sessionDirectories.AddRange(state.SessionDirectories);
            sessionTitle = titleOverride ?? state.Title;
            titleOverride = null;
            pendingTitles.Clear();
            startupTitleRequestId = null;
            remoteControlOptions = hasRemoteControlOverride ? remoteControlOverride : state.RemoteControl;
            hasRemoteControlOverride = false;
            remoteControlOverride = null;
            remoteControlActive = state.RemoteControlActive;
            remoteControlEnableSent = state.RemoteControlEnableSent;
            remoteBridgeSessionId = state.RemoteBridgeSessionId;
            remoteBridgeAccountKey = state.RemoteBridgeAccountKey;
            capabilityReplies = null;
            lastBackgroundCount = 0;
            lastBackgroundTools = Array.Empty<AgentBackgroundTool>();
            lastSafePoint = default;
            if (state.Turn is { } turn)
                ApplyTurnLocked(turn);
            if (replaying == null)
                SyncFromParserLocked();
            unwatched = !watched;
        }

        Raise(new AgentProcessStartedEvent(attached.InstanceId, cli));
        if (state.RunningVersion != null)
            Raise(new AgentRunningVersionEvent(attached.InstanceId, state.RunningVersion));
        if (replaying == null)
            UpdateSafePoint();   // 沒有要重播的：背景工作與安全點現在就回報（有重播的由讀取執行緒在重播完的那一刻回報）
        // 沒有檢查點的（H1 的狀態）從現在的位置起留：之後的 result 會再往前移
        if (!fromCheckpoint)
            UpdateCheckpoint(attached, parser);
        attached.BeginReading();
        if (replaying != null && !replaying.Done.Task.Wait(ReplayTimeout))
        {
            Log.Warn("接回 Claude 程序時重播沒有在時限內做完，放棄這個程序");
            OnDetachedProcessLost(attached, report: false);
            attached.Kill();
            return false;
        }
        // CLI 記著上一個 app 最後說的「有沒有人看」：照現在的再說一次（沒人看時它每秒才送一行）
        _ = WriteQuietlyAsync(attached, ClaudeStreamRules.BuildSetWatchedRequest(NextRequestId("watch"), !unwatched));
        return true;
    }

    /// <summary>還原放開當下的一輪中旗標</summary>
    private void ApplyTurnLocked(TurnState turn)
    {
        running = turn.Running;
        payloadSent = turn.PayloadSent;
        payloadWriting = false;
        turnAccepted = turn.TurnAccepted;
        interruptRequested = false;
        interruptSent = false;
        toolRunning = turn.ToolRunning;
        cliOwnTurn = turn.CliOwnTurn;
        awaitingQueuedTurn = turn.AwaitingQueuedTurn;
        turnUuid = turn.TurnUuid;
        turnUuidStarted = turn.TurnUuidStarted;
        turnUuidQueued = turn.TurnUuidQueued;
        remoteTurn = turn.RemoteTurn;
        cliTurn = turn.CliTurn;
        foreach (var uuid in turn.CommandUuids ?? new List<string>())
        {
            if (!string.IsNullOrEmpty(uuid))
                RememberCommandLocked(uuid);
        }
        notificationTurnSince = turn.NotificationTurn ? Stopwatch.GetTimestamp() : 0;
        lastToolResultId = turn.LastToolResultId;
        lastToolResultAt = 0;
        transcriptBaseline = turn.TranscriptBaseline;
        sessionAllowedRequests.UnionWith(turn.SessionAllowedRequests ?? new List<string>());
        if (running)
            turnGeneration++;
    }

    /// <summary>解析器的背景工作登記表 → 會話的快照（安全點與 HasLiveBackgroundWork 看它）</summary>
    private void SyncFromParserLocked()
    {
        backgroundTaskIds = parser.BackgroundTaskIds.ToList();
        backgroundTools = parser.BackgroundTools;
    }

    /// <summary>重播的一行：只給解析器（結果丟掉，不發事件、不寫 stdin）；到了放開的位移就結束重播</summary>
    private void ReplayLine(AgentProcess source, ClaudeStreamParser currentParser, string line, ReplayState replaying)
    {
        if (line.TrimStart().StartsWith('{'))
        {
            try
            {
                currentParser.Parse(line);
            }
            catch (Exception ex)
            {
                Log.Warn($"重播 Claude 的輸出時有一行解析失敗：{ex.Message}");
            }
        }
        if (source.StdoutOffset < replaying.Until)
            return;
        lock (gate)
        {
            if (!ReferenceEquals(replay, replaying))
                return;
            replay = null;
            if (ReferenceEquals(process, source))
            {
                currentParser.RestoreCounted(replaying.Counted);
                SyncFromParserLocked();
            }
        }
        // 重播完的那一刻回報背景工作與安全點：之後的行照常處理，中樞先知道現況才收到後面的事件
        UpdateSafePoint();
        replaying.Done.TrySetResult();
    }

    /// <summary>放開途中或接回途中程序沒了：對這個會話來說程序結束了（照程序自己結束的收尾，結束碼不明）</summary>
    private void OnDetachedProcessLost(AgentProcess lost, bool report = true)
    {
        bool wasRunning;
        lock (gate)
        {
            wasRunning = running;
            running = false;
            interruptRequested = false;
            interruptSent = false;
            payloadSent = false;
            payloadWriting = false;
            turnAccepted = false;
            if (ReferenceEquals(process, lost))
                process = null;
            pendingRequests.Clear();
            ResetProcessStateLocked();
        }
        if (!report)
            return;
        if (wasRunning)
            Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, "Claude 程序在放開時遺失", null));
        Raise(new AgentProcessExitedEvent(null, string.Empty, Expected: !wasRunning, lost.InstanceId));
        UpdateSafePoint();
    }
}
