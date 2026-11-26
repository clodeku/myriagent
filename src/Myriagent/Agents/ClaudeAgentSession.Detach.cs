using Myriagent.Cli;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// 放開與接回程序（設計 docs/plans/2026-11-26-agent-host.md 3.3）：app 換版時，閒置、還在等背景工作的對話不關 CLI 程序——
/// 把程序留在代理主機、記下接回要用的狀態；新的 app（或放棄更新後的同一個 app）以同一個程序接著用，背景工作不受影響。
/// 只在靜止點做：沒有進行中的一輪、沒有等回應的請求、沒有控制請求在等、stdin 沒有寫到一半。這時要帶過去的狀態很少，
/// 一輪之內的狀態（工具、訊息區塊、插入的訊息）都是空的。
/// </summary>
internal sealed partial class ClaudeAgentSession
{
    /// <summary>
    /// 放開時記下的狀態（JSON；寫進 resume.json）。只加欄位：新版讀舊版寫的要讀得懂，讀不懂（Version 不認得）就當成接不回去
    /// </summary>
    internal sealed class DetachState
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

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

        // ── 背景工作（解析器的登記表：清單、各是哪個工具開始的、種類） ──
        public List<string> BackgroundTasks { get; set; } = new();

        public Dictionary<string, AgentBackgroundTool> TaskTools { get; set; } = new();

        public Dictionary<string, string> TaskTypes { get; set; } = new();

        // ── Remote Control ──
        public bool RemoteControlActive { get; set; }

        public bool RemoteControlEnableSent { get; set; }

        public AgentRemoteControlOptions? RemoteControl { get; set; }

        public string? RemoteBridgeSessionId { get; set; }

        public string? RemoteBridgeAccountKey { get; set; }
    }

    /// <summary>放開程序時等主機回覆、等已收到的輸出處理完的上限</summary>
    internal TimeSpan DetachTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>正在放開程序（DetachProcessAsync 途中）：這段期間不能用這個程序送東西</summary>
    private bool detaching;

    public bool CanDetachProcess
    {
        get
        {
            lock (gate)
                return CanDetachLocked();
        }
    }

    /// <summary>
    /// 現在放得開：程序經主機開、主機會保留它；閒置、沒有任何在途的事。背景工作完成的通知正在等 CLI 自己開一輪時不放
    /// （那一輪馬上要開始）；掛著委派 MCP 的不放（那條管道是這個 app 的，新的 app 接不上）
    /// </summary>
    private bool CanDetachLocked()
        => !disposed && !detaching && process is { CanDetach: true } && processSettings is { DelegationMcpPipeName: null }
           && !running && !restarting && !payloadWriting && autoTurn == null && reattachCompletion == null
           && pendingRequests.Count == 0 && controlWaiters.Count == 0 && pendingSteer == null
           && !cliOwnTurn && notificationTurnSince == 0 && remoteControlRequestId == null;

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

        DetachState state;
        bool reattach = false;
        lock (gate)
        {
            detaching = false;
            if (released == null || !ReferenceEquals(process, target))
                return null;   // 沒放開：程序照舊是這個會話的（或這段期間它結束了，OnExited 已經處理）
            state = CaptureLocked(released);
            // 主機回覆之前送來的輸出都處理完了。那幾行剛好讓 CLI 自己開了一輪（背景工作完成的通知）、或多了等回應的請求：
            // 這已經不是靜止點——馬上以同一份狀態接回，當成沒有放開過
            reattach = running || cliOwnTurn || notificationTurnSince != 0 || pendingRequests.Count > 0;
            if (!reattach)
            {
                process = null;
                pendingRequests.Clear();
                ResetProcessStateLocked();
                // 背景工作還在主機那邊跑：不發「背景工作沒了」，中樞照舊顯示它們（接回或換版後由新的會話接手）
                lastBackgroundCount = 0;
                lastBackgroundTools = Array.Empty<AgentBackgroundTool>();
            }
        }

        if (reattach)
        {
            Log.Info("放開 Claude 程序的途中 CLI 開始了新的工作，接回原本的程序");
            // 只把程序物件換成重新接上的那一個（同一個實例代號）：會話與解析器的狀態原封不動，這一輪照常進行
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
        }
        attached.BeginReading();
        return true;
    }

    private DetachState CaptureLocked(AgentProcessDetachState released)
    {
        var settings = processSettings!;
        var background = parser.ExportBackground();
        return new DetachState
        {
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
        if (parsed is not { Version: DetachState.CurrentVersion, Process: not null } || settings.ProcessHost == null)
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
        AgentProcess attached;
        try
        {
            attached = AgentProcess.Attach(host, state.Process);
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
        bool unwatched;
        lock (gate)
        {
            if (disposed || process != null)
            {
                // 這段期間會話被關掉或另外開了程序：這個接回的不要了（不結束它，留給主機的逾時收掉）
                attached.LineReceived -= OnLine;
                attached.Exited -= OnExited;
                _ = attached.DetachAsync(DetachTimeout);
                return false;
            }
            process = attached;
            parser = new ClaudeStreamParser { ApiKeyNoticeShown = parser.ApiKeyNoticeShown, FreshSession = false };
            parser.RestoreBackground(state.BackgroundTasks, state.TaskTools, state.TaskTypes);
            processSettings = launched;
            lastSettings ??= launched;
            processConfigDirectory = state.ConfigDirectory;
            processTranscript = state.Transcript;
            EnsureSubagentTailLocked();
            processStartedAt = DateTimeOffset.FromUnixTimeMilliseconds(state.StartedAtMs);
            pendingRequests.Clear();
            ResetProcessStateLocked();
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
            backgroundTaskIds = parser.BackgroundTaskIds.ToList();
            backgroundTools = parser.BackgroundTools;
            remoteControlOptions = hasRemoteControlOverride ? remoteControlOverride : state.RemoteControl;
            hasRemoteControlOverride = false;
            remoteControlOverride = null;
            remoteControlActive = state.RemoteControlActive;
            remoteControlEnableSent = state.RemoteControlEnableSent;
            remoteBridgeSessionId = state.RemoteBridgeSessionId;
            remoteBridgeAccountKey = state.RemoteBridgeAccountKey;
            capabilityReplies = null;
            unwatched = !watched;
        }

        Raise(new AgentProcessStartedEvent(attached.InstanceId, cli));
        if (state.RunningVersion != null)
            Raise(new AgentRunningVersionEvent(attached.InstanceId, state.RunningVersion));
        attached.BeginReading();
        // CLI 記著上一個 app 最後說的「有沒有人看」：照現在的再說一次（沒人看時它每秒才送一行）
        _ = WriteQuietlyAsync(attached, ClaudeStreamRules.BuildSetWatchedRequest(NextRequestId("watch"), !unwatched));
        UpdateSafePoint();
        return true;
    }

    /// <summary>放開途中接回失敗：對這個會話來說程序沒了（照程序自己結束的收尾，結束碼不明）</summary>
    private void OnDetachedProcessLost(AgentProcess lost)
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
            process = null;
            pendingRequests.Clear();
            ResetProcessStateLocked();
        }
        if (wasRunning)
            Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, "Claude 程序在放開時遺失", null));
        Raise(new AgentProcessExitedEvent(null, string.Empty, Expected: !wasRunning, lost.InstanceId));
        UpdateSafePoint();
    }
}
