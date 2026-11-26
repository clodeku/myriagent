using Myriagent.Cli;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// 放開與接回程序（設計 docs/plans/2026-11-26-agent-host.md 3.3、第 10 節；Claude 的在 ClaudeAgentSession.Detach.cs）：app 換版時
/// 不關 app-server——程序留在代理主機、記下接回要用的狀態，新的 app（或放棄更新後的同一個 app）以同一個程序接著用，
/// 進行中的一輪、執行中的指令與背景指令都照跑，之後的通知照常收到。
/// <list type="bullet">
/// <item>H1：閒置時放開（狀態的 Version＝1）：thread、背景指令、token 累計。</item>
/// <item>H2：一輪進行中也放開（Version＝2）。每次主對話的 turn/completed 之後記一個檢查點（stdout 位移＋解析器的跨輪狀態），主機從那裡起
/// 留著輸出；接回時解析器從檢查點還原、把檢查點到放開位移之間的輸出重播一遍（只給解析器）。會話自己記的這一輪狀態
/// （執行中與讓出的指令、這一輪的指令卡）在放開當下另外記。</item>
/// </list>
/// 不放的時候：還在準備這一輪（turn/start 還沒回應）、有在途的 JSON-RPC 要求、有等使用者回應的請求、正在停止或重開、沙箱設定中、
/// 掛著委派 MCP
/// </summary>
internal sealed partial class CodexAgentSession
{
    /// <summary>放開時記下的狀態（JSON；寫進 resume.json）。只加欄位；Version 不認得就當成接不回去</summary>
    internal sealed class DetachState
    {
        /// <summary>閒置時放開的（H1 就讀得懂）</summary>
        public const int IdleVersion = 1;

        /// <summary>帶檢查點與一輪中狀態的（H2）</summary>
        public const int TurnVersion = 2;

        public int Version { get; set; } = IdleVersion;

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

        // ── 解析器（放開當下的值，H1 的接回用它）：token 計數取各 thread total 的差，不帶過去的話接回後第一筆會把整個對話的累計再記一次 ──
        public Dictionary<string, AgentUsage> TokenTotals { get; set; } = new();

        public Dictionary<string, string> ThreadModels { get; set; } = new();

        public string? MainModel { get; set; }

        // ── H2：檢查點與重播 ──

        /// <summary>檢查點的 stdout 位移（最後一次主對話 turn/completed 結束的位置；程序還沒有結束過一輪時是 0）</summary>
        public long? ReplayOut { get; set; }

        /// <summary>檢查點當時解析器的跨輪狀態</summary>
        public CodexParserCheckpoint? Parser { get; set; }

        /// <summary>會話在放開當下這一輪的狀態；null＝閒置</summary>
        public TurnState? Turn { get; set; }
    }

    internal sealed class DetachedCommand
    {
        public string ToolId { get; set; } = string.Empty;

        public AgentBackgroundTool? Tool { get; set; }
    }

    /// <summary>會話自己記的這一輪狀態（從通知一路累積的，不在解析器裡）</summary>
    internal sealed class TurnState
    {
        public bool Running { get; set; }

        /// <summary>這一輪是閒置時 codex 自己開始的（持續目標的下一輪）</summary>
        public bool CliTurn { get; set; }

        public string? TurnId { get; set; }

        /// <summary>執行中的工具項目（item/started 到 item/completed）</summary>
        public List<CodexToolLifecycle> RunningTools { get; set; } = new();

        /// <summary>這一輪已讓出給模型、還在跑的指令</summary>
        public List<CodexToolLifecycle> Yielded { get; set; } = new();

        /// <summary>這一輪主對話還沒結束的指令（工具卡 id → 卡片的說明）</summary>
        public List<DetachedCommand> TurnCommands { get; set; } = new();
    }

    /// <summary>檢查點：最後一次主對話的一輪結束時的位置與解析器的跨輪狀態</summary>
    private sealed record Checkpoint(long Offset, CodexParserCheckpoint Parser);

    /// <summary>接回之後正在重播（檢查點到放開位移之間的輸出只給解析器）</summary>
    private sealed class ReplayState
    {
        public required long Until { get; init; }

        public string? MainModel { get; init; }

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>放開程序時等主機回覆、等已收到的輸出處理完的上限</summary>
    internal TimeSpan DetachTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>接回時等重播做完的上限</summary>
    internal TimeSpan ReplayTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>正在放開程序（DetachProcessAsync 途中）：這段期間不能用這個程序開始新的一輪</summary>
    private bool detaching;

    /// <summary>目前的程序掛著帳戶委派的 MCP（那條管道是這個 app 的，新的 app 接不上）：不放開</summary>
    private bool processHasDelegation;

    /// <summary>目前的程序是經哪個代理主機開的（放開途中反悔時向它接回）；直接開的是 null</summary>
    private IAgentProcessHost? processHost;

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
    /// 現在放得開：程序經主機開、主機會保留它，沒有等回覆或正在收尾的東西。一輪進行中、有指令在跑、持續目標進行中都要有檢查點
    /// （主機還留著這一輪的輸出）；沒有檢查點時只有閒置才放（H1 的做法）
    /// </summary>
    private bool CanDetachLocked()
    {
        if (disposed || detaching || replay != null || process is not { CanDetach: true } || processHasDelegation || threadId == null)
            return false;
        if (BlockedForDetachLocked())
            return false;
        return HasCheckpointLocked() || IsQuiescentLocked();
    }

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
        if (replay != null)
            return "replaying";
        if (process == null)
            return "noProcess";
        if (!process.CanDetach)
            return "notHosted";
        if (processHasDelegation)
            return "delegation";
        if (threadId == null)
            return "noThread";
        if (restart != null)
            return "restarting";
        if (preparing != null)
            return "preparing";
        if (suspendedThread != null)
            return "suspended";
        if (sandboxSetup != null)
            return "sandboxSetup";
        if (interruptRequested)
            return "interrupting";
        if (pendingRequests.Count > 0)
            return "waitingUser";
        if (!responses.IsEmpty)
            return "rpc:" + string.Join(",", responses.Keys.Take(3));
        if (stoppingCommands.Count > 0)
            return "stoppingCommands";
        if (HasCheckpointLocked() || IsQuiescentLocked())
            return null;
        return checkpoint == null ? "noCheckpoint" : "outputTooLarge";
    }

    /// <summary>有等回覆或正在收尾的東西：放開的話那些回覆沒有人收</summary>
    private bool BlockedForDetachLocked()
        => restart != null || preparing != null || suspendedThread != null || sandboxSetup != null || interruptRequested
           || pendingRequests.Count > 0 || !responses.IsEmpty || stoppingCommands.Count > 0;

    /// <summary>閒置：沒有進行中的一輪、沒有執行中或讓出的指令、沒有持續目標（H1 放得開的條件）</summary>
    private bool IsQuiescentLocked() => !running && runningTools.Count == 0 && yieldedCommands.Count == 0 && !parser.GoalActive;

    private bool HasCheckpointLocked() => checkpoint is { } held && process != null && process.OutputHeldFrom == held.Offset;

    /// <summary>程序啟動、主對話每一輪結束：記下檢查點，主機從這裡起留著輸出（讀取執行緒或啟動時呼叫）</summary>
    private void UpdateCheckpoint(AgentProcess source, CodexEventParser currentParser)
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
            Log.Warn($"放開 Codex 程序失敗：{ex.Message}");
            released = null;
        }

        DetachState? state = null;
        lock (gate)
        {
            detaching = false;
            if (released == null || !ReferenceEquals(process, target))
                return null;   // 沒放開：程序照舊是這個會話的（或這段期間它結束了，OnExited 已經處理）
            // 主機回覆之前送來的輸出都處理完了。那幾行可能讓狀態變了（多了等回應的請求、codex 自己開了一輪…）：照現在的狀態再看一次
            var held = checkpoint is { } cp && cp.Offset <= released.OutOffset && target.OutputHeldFrom == cp.Offset ? cp : null;
            if (threadId != null && !BlockedForDetachLocked() && (held != null || IsQuiescentLocked()))
            {
                state = CaptureLocked(released, held);
                process = null;
                threadId = null;
                turnId = null;
                processCli = null;
                // 這個會話物件不再有進行中的一輪（它在主機那邊照跑）
                running = false;
                cliTurn = false;
                // 背景指令還在主機那邊跑：不算被中斷（ResetProcessStateLocked 會把還在清單裡的當成跟著程序結束）
                backgroundCommands.Clear();
                ResetProcessStateLocked();
            }
        }

        if (state == null)
        {
            Log.Info("放開 Codex 程序的途中狀態變了，接回原本的程序");
            if (!ReplaceDetachedProcess(target, released!))
                OnDetachedProcessLost(target);
            return null;
        }

        // 不發「背景工作沒了」「這一輪結束了」：中樞照舊留著（接回時這個或新的會話重新回報）。上次回報的歸零，接回時才會再發一次
        lock (safePointOrder)
        {
            reportedBackground = new List<string>();
            reportedBackgroundCount = 0;
            reportedSafePoint = new AgentSafePointChangedEvent(false, false, false);
        }
        return JObject.FromObject(state);
    }

    private DetachState CaptureLocked(AgentProcessDetachState released, Checkpoint? held)
    {
        var usage = parser.ExportUsage();
        bool quiescent = IsQuiescentLocked();
        return new DetachState
        {
            // 閒置時放開的寫成舊格式的版本（另外帶檢查點）：回滾到只懂 H1 的版本也接得回去
            Version = quiescent ? DetachState.IdleVersion : DetachState.TurnVersion,
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
            ReplayOut = held?.Offset,
            Parser = held?.Parser,
            Turn = quiescent
                ? null
                : new TurnState
                {
                    Running = running,
                    CliTurn = cliTurn,
                    TurnId = turnId,
                    RunningTools = runningTools.Values.ToList(),
                    Yielded = yieldedCommands.Values.ToList(),
                    TurnCommands = turnCommands.Select(kv => new DetachedCommand { ToolId = kv.Key, Tool = kv.Value }).ToList(),
                },
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
        long? hold;
        lock (gate)
        {
            if (disposed || !ReferenceEquals(process, old))
            {
                attached.Kill();
                return false;
            }
            process = attached;
            // 主機在放開時不會丟掉留著的輸出：檢查點照舊有效，新的程序物件接著留
            hold = checkpoint?.Offset;
        }
        if (hold is { } offset)
            attached.HoldOutputFrom(offset);
        attached.BeginReading();
        return true;
    }

    /// <summary>放開途中或接回途中程序沒了：對這個會話來說程序結束了（照程序自己結束的收尾，結束碼不明）</summary>
    private void OnDetachedProcessLost(AgentProcess lost, bool report = true)
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
            if (ReferenceEquals(process, lost))
                process = null;
            threadId = null;
            turnId = null;
            processCli = null;
            ResetProcessStateLocked();
        }
        if (!report)
            return;
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
        if (parsed is not { Version: DetachState.IdleVersion or DetachState.TurnVersion, Process: not null, ThreadId: not null } || settings.ProcessHost == null)
            return Task.FromResult(false);
        // 一輪中放開的一定要有檢查點才接得回去（解析器一輪之內的狀態靠重播）
        if (parsed.Version == DetachState.TurnVersion && (parsed.Parser == null || parsed.ReplayOut == null))
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

        bool fromCheckpoint = state is { Parser: not null, ReplayOut: >= 0 } && state.ReplayOut <= state.Process!.OutOffset;
        AgentProcess attached;
        try
        {
            attached = AgentProcess.Attach(host, state.Process!, fromCheckpoint ? state.ReplayOut : null);
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
        ReplayState? replaying = null;
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
            if (fromCheckpoint)
            {
                parser = CodexEventParser.FromCheckpoint(state.Parser!);
                if (state.ReplayOut < state.Process!.OutOffset)
                    replaying = new ReplayState { Until = state.Process.OutOffset, MainModel = state.MainModel };
                else if (!string.IsNullOrEmpty(state.MainModel))
                    parser.MainModel = state.MainModel;
            }
            else
            {
                // H1 的狀態（沒有檢查點）：token 累計照放開當下的
                parser = new CodexEventParser();
                parser.RestoreUsage(state.TokenTotals, state.ThreadModels, state.MainModel);
            }
            sandboxSetup?.TrySetResult((false, "Codex 程序已重開"));
            sandboxSetup = null;
            ResetProcessStateLocked();
            droppedBackground.Clear();   // 這個會話之前沒有程序：沒有跟著結束的背景指令
            // ResetProcessStateLocked 清掉了檢查點與重播：現在才設
            checkpoint = fromCheckpoint ? new Checkpoint(state.ReplayOut!.Value, state.Parser!) : null;
            replay = replaying;
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
            if (state.Turn is { } turn)
            {
                running = turn.Running;
                cliTurn = turn.CliTurn;
                turnId = turn.TurnId;
                interruptRequested = false;
                interruptSent = false;
                foreach (var tool in turn.RunningTools ?? new List<CodexToolLifecycle>())
                {
                    if (tool is { Key.Length: > 0 })
                        runningTools[tool.Key] = tool;
                }
                foreach (var tool in turn.Yielded ?? new List<CodexToolLifecycle>())
                {
                    if (tool is { Key.Length: > 0 })
                        yieldedCommands[tool.Key] = tool;
                }
                foreach (var command in turn.TurnCommands ?? new List<DetachedCommand>())
                {
                    if (command is { ToolId.Length: > 0, Tool: not null })
                        turnCommands[command.ToolId] = command.Tool;
                }
            }
        }

        Raise(new AgentProcessStartedEvent(attached.InstanceId, cli));
        if (state.RunningVersion != null)
            Raise(new AgentRunningVersionEvent(attached.InstanceId, state.RunningVersion));
        // 先回報背景指令與安全點（中樞據此把卡片對回來），才開始讀——放開期間跑完的 item/completed 會馬上補送進來。
        // 這些都是會話自己記的（已經照放開當下還原），不必等解析器重播完
        PublishSafePoint();
        // 沒有檢查點的（H1 的狀態）從現在的位置起留：之後的 turn/completed 會再往前移
        if (!fromCheckpoint)
            UpdateCheckpoint(attached, parser);
        attached.BeginReading();
        if (replaying != null && !await WaitReplayAsync(replaying).ConfigureAwait(false))
        {
            Log.Warn("接回 Codex 程序時重播沒有在時限內做完，放棄這個程序");
            OnDetachedProcessLost(attached, report: false);
            attached.Kill();
            return false;
        }
        return true;
    }

    private async Task<bool> WaitReplayAsync(ReplayState replaying)
        => await Task.WhenAny(replaying.Done.Task, Task.Delay(ReplayTimeout)).ConfigureAwait(false) == replaying.Done.Task;

    /// <summary>重播的一行：只給解析器（結果丟掉，不發事件、不回覆）；到了放開的位移就結束重播</summary>
    private void ReplayLine(AgentProcess source, CodexEventParser currentParser, string? mainThread, string line, ReplayState replaying)
    {
        if (line.TrimStart().StartsWith('{'))
        {
            try
            {
                currentParser.Parse(line, mainThread);
            }
            catch (Exception ex)
            {
                Log.Warn($"重播 Codex 的輸出時有一行解析失敗：{ex.Message}");
            }
        }
        if (source.StdoutOffset < replaying.Until)
            return;
        lock (gate)
        {
            if (!ReferenceEquals(replay, replaying))
                return;
            replay = null;
            // 主對話的模型是會話設給解析器的（thread 的回應、turn/start 指定的）：照放開當下的
            if (ReferenceEquals(process, source) && !string.IsNullOrEmpty(replaying.MainModel))
                currentParser.MainModel = replaying.MainModel;
        }
        replaying.Done.TrySetResult();
    }
}
