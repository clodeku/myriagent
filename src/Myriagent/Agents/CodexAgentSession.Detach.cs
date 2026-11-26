using Myriagent.Cli;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// 放開與接回程序（設計 docs/plans/2026-11-26-agent-host.md 3.3；Claude 的在 ClaudeAgentSession.Detach.cs）：app 換版時，
/// 閒置、還有背景指令在跑（一輪結束後還沒結束的 unified_exec 指令）的對話不關 app-server——程序留在代理主機、記下接回要用的狀態，
/// 新的 app（或放棄更新後的同一個 app）以同一個程序接著用，背景指令照跑，跑完的 item/completed 照常收到。
/// 只在靜止點做：沒有進行中的一輪、沒有等回應的請求、沒有在途的 JSON-RPC 要求、沒有重開或沙箱設定在進行。
/// 持續目標（thread goal）還在進行的不放：codex 隨時會自己開下一輪
/// </summary>
internal sealed partial class CodexAgentSession
{
    /// <summary>放開時記下的狀態（JSON；寫進 resume.json）。只加欄位；Version 不認得就當成接不回去</summary>
    internal sealed class DetachState
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;

        public AgentProcessDetachState? Process { get; set; }

        public string? ThreadId { get; set; }

        public string? RolloutPath { get; set; }

        /// <summary>程序啟動時的設定組合（Signature）：之後的送出拿它和新的設定比，不同才重開</summary>
        public string? Signature { get; set; }

        public string? CliVersion { get; set; }

        public string? CliExecutable { get; set; }

        public string? RunningVersion { get; set; }

        public string? CodexHome { get; set; }

        public bool? SandboxReady { get; set; }

        public AgentPermissionMode Mode { get; set; }

        public bool FallbackNotified { get; set; }

        public long NextRequestId { get; set; }

        /// <summary>背景指令（工具卡 id 與卡片的說明，照開始的順序）</summary>
        public List<DetachedCommand> Background { get; set; } = new();

        public List<string> SessionAllowed { get; set; } = new();

        public List<string> SessionAllowedRequests { get; set; } = new();

        // ── 解析器：token 計數取各 thread total 的差，不帶過去的話接回後第一筆會把整個對話的累計再記一次 ──
        public Dictionary<string, AgentUsage> TokenTotals { get; set; } = new();

        public Dictionary<string, string> ThreadModels { get; set; } = new();

        public string? MainModel { get; set; }
    }

    internal sealed class DetachedCommand
    {
        public string ToolId { get; set; } = string.Empty;

        public AgentBackgroundTool? Tool { get; set; }
    }

    /// <summary>放開程序時等主機回覆、等已收到的輸出處理完的上限</summary>
    internal TimeSpan DetachTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>正在放開程序（DetachProcessAsync 途中）：這段期間不能用這個程序開始新的一輪</summary>
    private bool detaching;

    /// <summary>
    /// 處理過幾行 app-server 的輸出：放開的途中（等主機回覆的那一下）又收到東西——背景指令剛好跑完、codex 開始了新的工作——
    /// 就不算靜止點，接回來當成沒放開過（那幾行的事件已經交給中樞，放開的話新的 app 不知道）
    /// </summary>
    private long linesSeen;

    /// <summary>目前的程序掛著帳戶委派的 MCP（那條管道是這個 app 的，新的 app 接不上）：不放開</summary>
    private bool processHasDelegation;

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
        => !disposed && !detaching && process is { CanDetach: true } && !processHasDelegation && threadId != null
           && !running && restart == null && preparing == null && suspendedThread == null && sandboxSetup == null
           && pendingRequests.Count == 0 && responses.IsEmpty && runningTools.Count == 0 && yieldedCommands.Count == 0
           && stoppingCommands.Count == 0 && !parser.GoalActive;

    public async Task<JObject?> DetachProcessAsync()
    {
        AgentProcess target;
        long seenBefore;
        lock (gate)
        {
            if (!CanDetachLocked())
                return null;
            target = process!;
            detaching = true;
            seenBefore = Interlocked.Read(ref linesSeen);
        }

        AgentProcessDetachState? released;
        try
        {
            released = await target.DetachAsync(DetachTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"放開 Codex 程序失敗：{ex.Message}");
            released = null;
        }

        DetachState state;
        bool reattach;
        lock (gate)
        {
            detaching = false;
            if (released == null || !ReferenceEquals(process, target))
                return null;   // 沒放開：程序照舊是這個會話的（或這段期間它結束了，OnExited 已經處理）
            state = CaptureLocked(released);
            // 主機回覆之前送來的輸出都處理完了。那段期間只要收到過東西（背景指令跑完、codex 自己開了一輪、多了等回應的請求）
            // 就已經不是靜止點，馬上接回
            reattach = running || pendingRequests.Count > 0 || runningTools.Count > 0 || !responses.IsEmpty || parser.GoalActive
                       || Interlocked.Read(ref linesSeen) != seenBefore;
            if (!reattach)
            {
                process = null;
                threadId = null;
                turnId = null;
                processCli = null;
                // 背景指令還在主機那邊跑：不算被中斷（ResetProcessStateLocked 會把還在清單裡的當成跟著程序結束）
                backgroundCommands.Clear();
                ResetProcessStateLocked();
            }
        }

        if (reattach)
        {
            Log.Info("放開 Codex 程序的途中 codex 開始了新的工作，接回原本的程序");
            if (!ReplaceDetachedProcess(target, released!))
                OnDetachedProcessLost(target);
            return null;
        }

        // 不發「背景工作沒了」：中樞照舊留著它們的卡片（接回時這個或新的會話重新回報）。上次回報的歸零，接回時才會再發一次
        lock (safePointOrder)
        {
            reportedBackground = new List<string>();
            reportedBackgroundCount = 0;
            reportedSafePoint = new AgentSafePointChangedEvent(false, false, false);
        }
        return JObject.FromObject(state);
    }

    private DetachState CaptureLocked(AgentProcessDetachState released)
    {
        var usage = parser.ExportUsage();
        return new DetachState
        {
            Process = released,
            ThreadId = threadId,
            RolloutPath = rolloutPath,
            Signature = processSignature,
            CliVersion = processCli?.Version,
            CliExecutable = processCli?.NativeExecutable,
            RunningVersion = runningVersion,
            CodexHome = processCodexHome,
            SandboxReady = sandboxReady,
            Mode = permissionMode,
            FallbackNotified = fallbackNotified,
            NextRequestId = Interlocked.Read(ref nextRequestId),
            Background = backgroundCommands.Select(b => new DetachedCommand { ToolId = b.ToolId, Tool = b.Tool }).ToList(),
            SessionAllowed = sessionAllowed.ToList(),
            SessionAllowedRequests = sessionAllowedRequests.ToList(),
            TokenTotals = usage.Totals,
            ThreadModels = usage.Models,
            MainModel = parser.MainModel,
        };
    }

    /// <summary>放開之後馬上反悔：以同一個位移重新接上，換掉會話手上已經不能用的程序物件；接不回去回 false</summary>
    private bool ReplaceDetachedProcess(AgentProcess old, AgentProcessDetachState released)
    {
        AgentProcess attached;
        try
        {
            attached = AgentProcess.Attach(processHost ?? throw new AgentHostUnavailableException("沒有代理主機"), released);
        }
        catch (AgentHostUnavailableException ex)
        {
            Log.Warn($"Codex 程序接不回去：{ex.Message}");
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
        }
        attached.BeginReading();
        return true;
    }

    /// <summary>放開途中接回失敗：對這個會話來說程序沒了（照程序自己結束的收尾，結束碼不明）</summary>
    private void OnDetachedProcessLost(AgentProcess lost)
    {
        FailPendingResponses(lost, "Codex 程序已結束");
        bool wasRunning;
        lock (gate)
        {
            wasRunning = running;
            running = false;
            cliTurn = false;
            interruptRequested = false;
            interruptSent = false;
            process = null;
            threadId = null;
            turnId = null;
            processCli = null;
            ResetProcessStateLocked();
        }
        if (wasRunning)
            Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, "Codex 程序在放開時遺失", null) { ProcessExited = true });
        Raise(new AgentProcessExitedEvent(null, string.Empty, Expected: !wasRunning, lost.InstanceId));
        PublishSafePoint();
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
        if (parsed is not { Version: DetachState.CurrentVersion, Process: not null, ThreadId: not null } || settings.ProcessHost == null)
            return Task.FromResult(false);
        lock (gate)
        {
            if (disposed || process != null || running || restart != null)
                return Task.FromResult(false);
        }
        // 接回要等主機回覆、補送暫存的輸出：不佔呼叫端的執行緒（中樞在 UI 執行緒呼叫）
        return Task.Run(() => AttachCoreAsync(parsed, settings, settings.ProcessHost));
    }

    /// <summary>以放開時的狀態接上同一個程序；接不回去回 false（會話照舊沒有程序）</summary>
    private async Task<bool> AttachCoreAsync(DetachState state, AgentSessionSettings settings, IAgentProcessHost host)
    {
        // 共用記憶的資料夾（每一輪的可寫資料夾要用）：照啟動程序時的做法準備；準備不了就當成沒有
        AgentSharedLaunch shared;
        try
        {
            shared = await settings.PrepareSharedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"接回 Codex 程序時準備共用設定失敗：{ex.Message}");
            shared = AgentSharedLaunch.None;
        }

        AgentProcess attached;
        try
        {
            attached = AgentProcess.Attach(host, state.Process!);
        }
        catch (AgentHostUnavailableException ex)
        {
            Log.Info($"Codex 程序接不回去：{ex.Message}");
            return false;
        }

        attached.LineReceived += OnLine;
        attached.Exited += OnExited;
        // CLI 的身分只在版本與執行檔都對得上目前安裝的那一份時沿用，否則當成不明（不明不構成重開的理由）
        var cli = settings.Cli is { } current && string.Equals(current.Version, state.CliVersion, StringComparison.Ordinal)
                  && string.Equals(current.NativeExecutable, state.CliExecutable, StringComparison.OrdinalIgnoreCase)
            ? current
            : null;
        lock (gate)
        {
            if (disposed || process != null || running || restart != null)
            {
                // 這段期間會話被關掉或另外開了程序：這個接回的不要了（不結束它，留給主機的逾時收掉）
                attached.LineReceived -= OnLine;
                attached.Exited -= OnExited;
                _ = attached.DetachAsync(DetachTimeout);
                return false;
            }
            process = attached;
            processSignature = state.Signature;
            processCli = cli;
            processHasDelegation = false;
            processHost = host;
            parser = new CodexEventParser();
            parser.RestoreUsage(state.TokenTotals, state.ThreadModels, state.MainModel);
            sandboxSetup?.TrySetResult((false, "Codex 程序已重開"));
            sandboxSetup = null;
            ResetProcessStateLocked();
            droppedBackground.Clear();   // 這個會話之前沒有程序：沒有跟著結束的背景指令
            threadId = state.ThreadId;
            turnId = null;
            sandboxReady = state.SandboxReady;
            sharedLaunch = shared;
            runningVersion = state.RunningVersion;
            processCodexHome = state.CodexHome;
            rolloutPath = state.RolloutPath;
            if (state.RolloutPath != null)
                lastRollout = (state.ThreadId!, state.RolloutPath);
            permissionMode = state.Mode;
            fallbackNotified = state.FallbackNotified;
            if (Interlocked.Read(ref nextRequestId) < state.NextRequestId)
                Interlocked.Exchange(ref nextRequestId, state.NextRequestId);
            foreach (var command in state.Background)
            {
                if (command is { ToolId.Length: > 0, Tool: not null })
                    backgroundCommands.Add((command.ToolId, command.Tool));
            }
            sessionAllowed.UnionWith(state.SessionAllowed);
            sessionAllowedRequests.UnionWith(state.SessionAllowedRequests);
        }

        Raise(new AgentProcessStartedEvent(attached.InstanceId, cli));
        if (state.RunningVersion != null)
            Raise(new AgentRunningVersionEvent(attached.InstanceId, state.RunningVersion));
        // 先回報背景指令（中樞據此把它們的卡片對回來），才開始讀——放開期間跑完的 item/completed 會馬上補送進來
        PublishSafePoint();
        attached.BeginReading();
        return true;
    }
}
