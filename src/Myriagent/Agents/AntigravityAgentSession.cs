using System.Diagnostics;
using Myriagent.Cli;
using Myriagent.Core;

namespace Myriagent.Agents;

/// <summary>
/// Antigravity 代理：一個對話一個 agy --print= --input-format stream-json --output-format stream-json 程序，每則訊息寫一行到 stdin、跑一輪。
/// 模型、推理強度、權限模式、精簡模式、工作資料夾、帳戶、執行檔改變時於下一輪重開程序（--conversation 續接 agy 自己的對話）。
/// <list type="bullet">
/// <item>agy 的 stream-json 沒有對話中途核准協定：拿不到核准的工具被 agy 略過（軟拒絕）；「自動（沙箱）」以 --dangerously-skip-permissions --sandbox 在 Windows 沙箱內自動執行；
/// 規劃模式寫出 plan.md 或 ArtifactMetadata.RequestFeedback=true 時，於本輪結束前發出 AnswerAsMessage 計畫確認卡</item>
/// <item>即時伴讀 brain\&lt;conversationId&gt;\.system_generated\logs\transcript_full.jsonl 補齊思考過程、編輯紅綠 Diff 與產出媒體預覽</item>
/// <item>停止＝結束程序樹（stream-json 沒有中斷指令）；下一則訊息以 --conversation 續接</item>
/// <item>CLI 本地唯讀斜線指令（/usage、/credits、/skills、/help…）送進 stream-json 會導致 exit 2：改攔截以單發 -p 執行並回報結果，不中斷對話程序</item>
/// <item>CLI 換版與 app 自我更新排空（RestartAsync）：與 Codex／Grok 共用安全點與接續狀態機，停下舊程序樹後以 --conversation 啟動新版並送出接續訊息</item>
/// </list>
/// </summary>
internal sealed class AntigravityAgentSession : IAgentSession
{
    private const string ConversationLostNotice = "agy 找不到這個對話先前的紀錄，已改開新對話並附上前文節錄";

    /// <summary>正常關閉（關 stdin）等 agy 收尾的上限</summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    /// <summary>重開時舊程序的結束事件最多等多久（stdout 被別的程序握著時不會觸發）</summary>
    private static readonly TimeSpan ExitHandledTimeout = TimeSpan.FromSeconds(5);

    /// <summary>重開失敗時剛啟動的新程序最多等它結束多久</summary>
    private static readonly TimeSpan FailedProcessExitTimeout = TimeSpan.FromSeconds(2);

    /// <summary>單發唯讀斜線指令（-p /credits 等）的執行時限</summary>
    private static readonly TimeSpan CliSlashCommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>重開時關 stdin 後等程序結束的上限，逾時結束程序樹（測試可調短）</summary>
    internal TimeSpan RestartCloseGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>重開時等程序樹（含 agy language server 與子指令）都結束的上限，逾時逐一結束</summary>
    internal TimeSpan RestartTreeExitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>解析逐字稿路徑的注入點（測試可替換或設為 null；預設指向使用者目錄下的 brain 記錄）</summary>
    internal Func<string, string?>? TranscriptPathResolver { get; set; } = AntigravityTranscriptReader.DefaultResolveTranscriptPath;

    private readonly object gate = new();
    private readonly object safePointOrder = new();
    private AgentProcess? process;
    private AntigravityStreamParser parser = new();
    private string? processSignature;
    private CliBinaryIdentity? processCli;
    private string? runningVersion;
    private bool running;
    private bool cliTurn;
    private bool tookOverCliTurn;
    private bool interruptRequested;
    private CancellationTokenSource? preparing;
    private TaskCompletionSource? preparationDone;

    /// <summary>agy 的對話 id（第一次從 init／step_update／result 得知；續接時以 --conversation 帶上）</summary>
    private string? conversationId;

    /// <summary>目前程序啟動時帶的 --conversation（null＝新對話）</summary>
    private string? processConversation;

    /// <summary>目前程序在 stderr 回報找不到 --conversation 指定的對話（它已默默改開空白的新對話）</summary>
    private bool processConversationMissing;

    /// <summary>目前程序已經送過一輪（之後它回報的錯誤與續接無關）</summary>
    private bool processTurnSent;

    /// <summary>這一輪的資料（續接失敗改開新對話時重送）；回報這一輪結束的地方都會清掉它</summary>
    private PendingTurn? currentTurn;

    /// <summary>這一輪送到（或為這一輪啟動）的程序：只有它結束才算這一輪失敗</summary>
    private AgentProcess? turnProcess;

    /// <summary>改開新對話重送時被停止：下一則送到新對話時仍要附前文節錄</summary>
    private bool pendingRecap;

    /// <summary>這個程序因為沒登入被結束</summary>
    private bool signInRequired;

    /// <summary>上一則送出時的工作資料夾（換了就在訊息開頭告知代理）</summary>
    private string? lastWorkingDirectory;

    /// <summary>重開時停下待接續的對話（Settings=null 的第一段停下、之後再帶 Settings 呼叫一次）</summary>
    private string? suspendedConversation;

    private RestartOperation? restart;
    private AgentSafePointChangedEvent? reportedSafePoint;
    private bool hasLiveBackgroundWork;
    private IReadOnlyList<AgentBackgroundTool> liveBackgroundTools = Array.Empty<AgentBackgroundTool>();
    private bool disposed;

    private sealed record PendingTurn(AgentTurnInput Input, AgentSessionSettings Settings, bool UsedConversation, bool RetriedFresh);

    private sealed class RestartOperation
    {
        public bool TookOver;
        public string? PreviousInstance;
        public AgentProcess? Stopping;
        public readonly TaskCompletionSource ExitHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TurnFinishedOnItsOwn;
        public bool Cancelled;
        public bool ContinuationStarted;
        public bool ContinuationReported;
    }

    public event Action<AgentEvent>? EventReceived;

    public AgentProvider Provider => AgentProvider.Antigravity;

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

    /// <summary>目前程序的 CLI 版本（agy 的輸出不帶版本，取啟動時的執行檔身分）；沒有程序為 null</summary>
    public string? RunningVersion
    {
        get
        {
            lock (gate)
                return process != null ? runningVersion : null;
        }
    }

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

    /// <summary>目前程序的 PID（整合測試用）；沒有程序為 null</summary>
    internal int? CurrentProcessId
    {
        get
        {
            lock (gate)
                return process?.ProcessId;
        }
    }

    /// <summary>沒有工具在執行（agy 沒有待回應的請求，也沒有撐過一輪的背景工作）</summary>
    public bool IsAtSafePoint
    {
        get
        {
            lock (gate)
                return CurrentSafePointLocked().IsAtSafePoint;
        }
    }

    public bool HasLiveBackgroundWork
    {
        get
        {
            lock (gate)
                return hasLiveBackgroundWork;
        }
    }

    public IReadOnlyList<AgentBackgroundTool> LiveBackgroundTools
    {
        get
        {
            lock (gate)
                return liveBackgroundTools;
        }
    }

    private static string Signature(AgentSessionSettings settings)
        => AntigravityStreamRules.Signature(
            settings.AccountKey,
            settings.Launcher?.FileName,
            settings.WorkingDirectory,
            settings.Model,
            settings.Effort,
            settings.PermissionMode,
            settings.ConversationDirectory,
            settings.UseLocalCustomizations,
            settings.Agent);

    public async Task SendAsync(AgentTurnInput input, AgentSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        var cliSlash = input.Attachments.Count == 0
            ? AntigravityStreamRules.ClassifyCliOnlySlashCommand(input.Text)
            : new AntigravityStreamRules.CliOnlySlashClassification(AntigravityStreamRules.CliOnlySlashKind.None, null, null);
        bool isCliOnlySlash = cliSlash.Kind != AntigravityStreamRules.CliOnlySlashKind.None;

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (restart != null)
                throw new AgentRestartInProgressException();
            if (running && !cliTurn)
                throw new InvalidOperationException("代理還在處理上一則訊息");
            if (running)
            {
                // 閒置時 CLI 自己開的一輪還在進行（中樞還沒收到 IdleTurnStarted 就送出了）：若沿用同一個程序，寫進 stdin 排在該輪後面並接手；要換程序或跑本地斜線指令則不能接手
                if (isCliOnlySlash
                    || process == null
                    || process.HasExited
                    || processConversationMissing
                    || AntigravityStreamRules.NeedsNewProcess(processSignature, processCli, Signature(settings), settings.Cli))
                {
                    throw new InvalidOperationException("代理還在處理背景工作的結果");
                }
                cliTurn = false;
                tookOverCliTurn = true;
            }
            else
            {
                running = true;
                cliTurn = false;
                tookOverCliTurn = false;
                turnProcess = null;
            }
            interruptRequested = false;
            suspendedConversation = null;
            currentTurn = null;
        }

        // CLI 本地唯讀斜線指令（送進 stream-json 會害 agy exit 2）：不碰對話程序，直接回應或單發 -p 查詢
        if (isCliOnlySlash)
        {
            await HandleCliOnlySlashCommandAsync(cliSlash, settings);
            return;
        }

        await SendCoreAsync(input, settings, fresh: false);
    }

    private async Task HandleCliOnlySlashCommandAsync(AntigravityStreamRules.CliOnlySlashClassification cliSlash, AgentSessionSettings settings)
    {
        if (cliSlash.Kind == AntigravityStreamRules.CliOnlySlashKind.UiManaged)
        {
            lock (gate)
            {
                running = false;
                interruptRequested = false;
            }
            Raise(new AgentNoticeEvent(cliSlash.UiHint!, IsError: false));
            Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, null));
            PublishSafePoint();
            return;
        }

        var preparation = new CancellationTokenSource();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            preparing = preparation;
            preparationDone = done;
        }

        try
        {
            if (TryAbortInterrupted())
                return;
            var launcher = settings.Launcher
                ?? throw new AgentCliUnavailableException(AgentProvider.Antigravity, "這台電腦尚未安裝 Antigravity CLI，請到「設定 → CLI」安裝");
            var run = await CliRunner.RunAsync(
                launcher,
                new[] { "-p", cliSlash.PromptCommand! },
                settings.ConfigureEnvironment,
                CliSlashCommandTimeout,
                preparation.Token,
                Directory.Exists(settings.WorkingDirectory) ? settings.WorkingDirectory : null);

            if (TryAbortInterrupted())
                return;

            lock (gate)
            {
                running = false;
                interruptRequested = false;
            }

            if (run.TimedOut)
            {
                Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, $"agy -p {cliSlash.PromptCommand} 逾時沒有回應", null));
                return;
            }
            var text = (run.Stdout ?? string.Empty).Trim();
            var err = (run.Stderr ?? string.Empty).Trim();
            if (run.ExitCode != 0 && text.Length == 0)
            {
                Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, err.Length > 0 ? err : $"agy -p {cliSlash.PromptCommand} 結束碼 {run.ExitCode}", null));
                return;
            }
            if (text.Length > 0)
                Raise(new AgentTextCompletedEvent($"agy-cmd-{Guid.NewGuid():N}"[..16], text));
            else if (err.Length > 0)
                Raise(new AgentNoticeEvent(err, IsError: false));
            Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, null));
        }
        catch (OperationCanceledException) when (TryAbortInterrupted())
        {
        }
        catch
        {
            lock (gate)
            {
                running = false;
                interruptRequested = false;
            }
            throw;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(preparing, preparation))
                    preparing = null;
                if (ReferenceEquals(preparationDone, done))
                    preparationDone = null;
            }
            done.TrySetResult();
            preparation.Dispose();
            PublishSafePoint();
        }
    }

    /// <summary>送出一輪（running 已成立）。fresh＝續接失敗後改開新對話重送：不帶 --conversation、附上前文節錄</summary>
    private async Task SendCoreAsync(AgentTurnInput input, AgentSessionSettings settings, bool fresh)
    {
        AgentProcess? current;
        AgentProcess? stale = null;
        var preparation = new CancellationTokenSource();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingTurn? sentTurn = null;
        string? conversation;
        bool firstTurnOnProcess;
        bool missingOnIdleProcess;
        lock (gate)
        {
            preparing = preparation;
            preparationDone = done;
            // 閒置的程序回報過找不到對話（警告到的時候沒有可重送的一輪，例如準備期間按了停止）：
            // 它已在空白的新對話裡，換掉它、改開新對話並附上前文
            missingOnIdleProcess = process != null && processConversationMissing;
            if (missingOnIdleProcess)
                fresh = true;
            if (fresh)
                conversationId = null;
            var signature = Signature(settings);
            if (process != null && (missingOnIdleProcess || process.HasExited || AntigravityStreamRules.NeedsNewProcess(processSignature, processCli, signature, settings.Cli)))
            {
                stale = process;
                process = null;
            }
            current = process;
            conversation = current != null
                ? conversationId ?? processConversation
                : conversationId ?? (fresh || !settings.SessionStarted ? null : settings.SessionId);
            firstTurnOnProcess = current == null || !processTurnSent;
        }
        if (missingOnIdleProcess)
            Raise(new AgentNoticeEvent(ConversationLostNotice, IsError: false));

        var ct = preparation.Token;
        try
        {
            if (stale != null)
            {
                await stale.CloseAsync(CloseGrace);
                stale.Dispose();
            }
            if (TryAbortInterrupted())
                return;

            current ??= await StartProcessAsync(settings, conversation, ct);
            ThrowIfDisposed();
            if (TryAbortInterrupted())
                return;

            string text = string.Empty;
            bool abort;
            lock (gate)
            {
                // 最後一次檢查停止：這裡清掉 preparing 之後，停止改成結束程序（在這之前按的停止只取消準備）
                abort = interruptRequested;
                if (!abort)
                {
                    var instructions = conversation == null ? settings.Instructions : null;
                    var recap = fresh || (pendingRecap && conversation == null) ? settings.HistoryRecap : null;
                    var notice = conversation != null && lastWorkingDirectory != null
                        && !string.Equals(lastWorkingDirectory.TrimEnd('\\'), settings.WorkingDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                        ? AntigravityStreamRules.BuildWorkingDirectoryNotice(settings.WorkingDirectory)
                        : null;
                    text = AntigravityStreamRules.BuildTurnText(instructions, recap, notice, input.Text, input.Attachments);
                    lastWorkingDirectory = settings.WorkingDirectory;
                    pendingRecap = false;
                    sentTurn = currentTurn = new PendingTurn(input, settings, UsedConversation: firstTurnOnProcess && conversation != null, RetriedFresh: fresh);
                    turnProcess = current;
                    processTurnSent = true;
                    if (!tookOverCliTurn)
                        parser.BeginTurn();
                    preparing = null;
                }
            }
            if (abort)
            {
                TryAbortInterrupted();
                return;
            }

            // 寫出之後不再取消（agy 可能已經開始這一輪）：之後按停止改成結束程序
            await current.WriteLineAsync(AntigravityStreamRules.BuildUserMessage(text));
            // 「找不到對話」的警告在這一輪登記之前就到了（OnStderr 當時沒有可重送的一輪）：現在接手
            TryRetryFresh(null);
        }
        catch (IOException) when (IsTurnHandledElsewhere(sentTurn))
        {
            // 寫入時程序剛好結束：OnExited 已回報（或改開新對話重送）這一輪
        }
        catch
        {
            bool stopped;
            lock (gate)
            {
                if (!running && !disposed)
                    return;
                stopped = interruptRequested && !disposed;
                if (!stopped)
                    running = false;
            }
            if (stopped)
            {
                TryAbortInterrupted();
                return;
            }
            throw;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(preparing, preparation))
                    preparing = null;
                if (ReferenceEquals(preparationDone, done))
                    preparationDone = null;
            }
            done.TrySetResult();
            preparation.Dispose();
            PublishSafePoint();
        }
    }

    private bool IsTurnHandledElsewhere(PendingTurn? sent)
    {
        lock (gate)
            return !running || (sent != null && !ReferenceEquals(currentTurn, sent));
    }

    private void ThrowIfDisposed()
    {
        lock (gate)
            ObjectDisposedException.ThrowIf(disposed, this);
    }

    /// <summary>準備期間按了停止：放棄這一輪並回報已停止</summary>
    private bool TryAbortInterrupted()
    {
        lock (gate)
        {
            if (!interruptRequested)
                return false;
            running = false;
            cliTurn = false;
            tookOverCliTurn = false;
            interruptRequested = false;
            currentTurn = null;
            turnProcess = null;
        }
        Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null));
        return true;
    }

    private async Task<AgentProcess> StartProcessAsync(AgentSessionSettings settings, string? conversation, CancellationToken ct)
    {
        var launcher = settings.Launcher
            ?? throw new AgentCliUnavailableException(AgentProvider.Antigravity, "這台電腦尚未安裝 Antigravity CLI，請到「設定 → CLI」安裝");
        if (!Directory.Exists(settings.WorkingDirectory))
            throw new DirectoryNotFoundException($"工作資料夾不存在：{settings.WorkingDirectory}");
        // 跨四家 CLI 共用設定：啟動前在背景同步 ~/.gemini/GEMINI.md 與 ~/.gemini/config/skills
        _ = await settings.PrepareSharedAsync();

        var mode = AgentPermissionModes.Normalize(AgentProvider.Antigravity, settings.PermissionMode);
        var started = await AgentProcess.StartWithRetryAsync(
            launcher,
            AntigravityStreamRules.BuildArguments(
                conversation,
                settings.Model,
                settings.Effort,
                mode,
                settings.ConversationDirectory,
                settings.UseLocalCustomizations,
                settings.Agent),
            settings.WorkingDirectory,
            settings.ConfigureEnvironment,
            ct);
        started.LineReceived += OnLine;
        started.StderrReceived += OnStderr;
        started.Exited += OnExited;
        string? version;
        lock (gate)
        {
            if (disposed)
            {
                started.Dispose();
                throw new ObjectDisposedException(nameof(AntigravityAgentSession));
            }
            process = started;
            turnProcess = started;
            processConversation = conversation;
            processConversationMissing = false;
            processTurnSent = false;
            processSignature = Signature(settings);
            processCli = settings.Cli;
            parser = new AntigravityStreamParser(
                "agy-" + started.InstanceId[..8],
                TranscriptPathResolver,
                initialConversationId: conversation,
                permissionMode: mode);
            signInRequired = false;
            version = runningVersion = settings.Cli?.Version;
        }

        // 在讀這個程序的輸出之前發：中樞據此作廢其他實例的待回應卡片
        Raise(new AgentProcessStartedEvent(started.InstanceId, settings.Cli));
        if (version != null)
            Raise(new AgentRunningVersionEvent(started.InstanceId, version));
        PublishSafePoint();
        started.BeginReading();
        return started;
    }

    private void OnLine(AgentProcess source, string line)
    {
        AntigravityParseResult parsed;
        bool idleTurnStarted = false;
        bool idleAfterBackground = false;
        lock (gate)
        {
            if (!ReferenceEquals(source, process))
                return;
            if (!running && restart == null && !disposed && AntigravityStreamRules.StartsIdleTurn(line, parser.MinNextTurnStepIndex))
            {
                running = true;
                cliTurn = true;
                tookOverCliTurn = false;
                interruptRequested = false;
                currentTurn = null;
                turnProcess = source;
                parser.BeginTurn();
                idleTurnStarted = true;
                idleAfterBackground = hasLiveBackgroundWork;
            }
            if (!running)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('{') && !trimmed.Contains("\"init\"", StringComparison.Ordinal))
                    return;
            }
            parsed = parser.Parse(line);
            if (parsed.NewConversationId is { } newId)
                conversationId = newId;
            foreach (var bg in parsed.Events.OfType<AgentBackgroundWorkEvent>())
            {
                hasLiveBackgroundWork = bg.Count > 0;
                liveBackgroundTools = bg.Tools;
            }
        }

        if (parsed.PlainText != null)
        {
            source.AppendStderr(parsed.PlainText);
            if (AntigravityStreamRules.IsSignInPrompt(parsed.PlainText))
                StopForSignIn(source);
            return;
        }

        if (idleTurnStarted)
        {
            if (!IsCurrent(source))
                return;
            Raise(new AgentIdleTurnStartedEvent(AfterBackgroundTask: idleAfterBackground));
        }

        if (parsed.NewConversationId is { } id)
        {
            if (!IsCurrent(source))
                return;
            Raise(new AgentSessionStartedEvent(id, parsed.Model, null));
        }

        bool completed = false;
        AgentProcess? recycleOnNetworkError = null;
        foreach (var ev in parsed.Events)
        {
            if (ev is AgentTurnCompletedEvent turnCompleted)
            {
                bool swallowedByRestart = false;
                lock (gate)
                {
                    if (restart is { } op && ReferenceEquals(op.Stopping, source))
                    {
                        if (turnCompleted.Outcome == AgentTurnOutcome.Completed)
                            op.TurnFinishedOnItsOwn = true;
                        else
                            swallowedByRestart = true;
                    }
                }
                if (swallowedByRestart)
                    return;

                if (TryRetryFresh(turnCompleted.ErrorMessage))
                    return;
                bool swallowedTakeover = false;
                lock (gate)
                {
                    if (!ReferenceEquals(source, process))
                        return;
                    if (tookOverCliTurn && turnCompleted.Outcome == AgentTurnOutcome.Completed)
                    {
                        tookOverCliTurn = false;
                        parser.BeginTurn();
                        swallowedTakeover = true;
                    }
                    else
                    {
                        running = false;
                        cliTurn = false;
                        tookOverCliTurn = false;
                        interruptRequested = false;
                        currentTurn = null;
                        if (restart is { ContinuationStarted: true } continuing)
                            continuing.ContinuationReported = true;
                        // 網路連線瞬斷而失敗且無存活背景工作：卸下並關閉舊程序樹（含 Go language server），清掉陳舊的 HTTP/TLS 連線池，下一輪以 --conversation 開新程序續接
                        if (turnCompleted.Outcome == AgentTurnOutcome.Failed
                            && turnCompleted.ServerBusy == AgentServerBusy.NetworkError
                            && !hasLiveBackgroundWork
                            && restart == null)
                        {
                            recycleOnNetworkError = source;
                            DetachProcessLocked();
                        }
                    }
                }
                if (swallowedTakeover)
                    continue;
                completed = true;
            }
            else if (!IsCurrent(source) && !ReferenceEquals(source, recycleOnNetworkError))
            {
                return;
            }
            Raise(ev);
        }
        if (parsed.Events.Count > 0 || completed)
            PublishSafePoint();
        if (recycleOnNetworkError != null)
            _ = Task.Run(() => RecycleBrokenProcessAsync(recycleOnNetworkError));
    }

    /// <summary>網路連線中斷後關閉舊的 agy 程序樹（含 Go language server），清掉失效的連線池；下一輪以 --conversation 啟動新程序續接</summary>
    private async Task RecycleBrokenProcessAsync(AgentProcess stale)
    {
        try
        {
            var tree = stale.CaptureTree();
            await stale.CloseAsync(CloseGrace).ConfigureAwait(false);
            if (!await tree.WaitForExitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                tree.KillAlive();
        }
        catch (Exception ex)
        {
            Log.Warn($"Antigravity 回收網路連線中斷的舊程序失敗：{ex.Message}");
        }
        finally
        {
            Raise(new AgentProcessExitedEvent(null, stale.GetStderrTail(), Expected: true, stale.InstanceId));
            stale.Dispose();
        }
    }

    private bool IsCurrent(AgentProcess source)
    {
        lock (gate)
            return ReferenceEquals(source, process);
    }

    private void OnStderr(AgentProcess source, string line)
    {
        if (AntigravityStreamRules.IsSignInPrompt(line))
        {
            StopForSignIn(source);
            return;
        }
        if (AntigravityStreamRules.ParseConversationNotFoundWarning(line) is { } missing)
        {
            lock (gate)
            {
                if (!ReferenceEquals(source, process) || !string.Equals(missing, processConversation, StringComparison.Ordinal))
                    return;
                processConversationMissing = true;
            }
            TryRetryFresh(null);
        }
    }

    private void StopForSignIn(AgentProcess source)
    {
        lock (gate)
        {
            if (!ReferenceEquals(source, process) || signInRequired)
                return;
            signInRequired = true;
        }
        source.Kill();
    }

    private bool TryRetryFresh(string? error)
    {
        PendingTurn? turn;
        AgentProcess? old;
        List<AgentEvent> aborted;
        lock (gate)
        {
            turn = currentTurn;
            if (!running || turn == null || !turn.UsedConversation || turn.RetriedFresh || interruptRequested || disposed
                || restart is { TookOver: true, ContinuationStarted: false }
                || !(processConversationMissing || AntigravityStreamRules.IsConversationNotFound(error)))
                return false;
            old = process;
            process = null;
            conversationId = null;
            pendingRecap = true;
            currentTurn = turn with { RetriedFresh = true };
            aborted = parser.AbortOpenSteps(AntigravityStreamRules.InterruptedToolOutput);
        }
        foreach (var ev in aborted)
            Raise(ev);
        Raise(new AgentNoticeEvent(ConversationLostNotice, IsError: false));
        old?.Kill();
        _ = Task.Run(async () =>
        {
            try
            {
                await SendCoreAsync(turn.Input, turn.Settings, fresh: true);
            }
            catch (Exception ex)
            {
                bool report;
                lock (gate)
                {
                    report = currentTurn != null && !disposed;
                    running = false;
                    currentTurn = null;
                    turnProcess = null;
                }
                if (report)
                    Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, ex.Message, null));
                PublishSafePoint();
            }
        });
        return true;
    }

    private void OnExited(AgentProcess source, int? exitCode)
    {
        RestartOperation? stoppedForRestart = null;
        bool ownedByRestart;
        bool wasRunning = false;
        bool wasInterrupted = false;
        bool needsSignIn = false;
        bool hadBackground = false;
        List<AgentEvent> aborted;
        lock (gate)
        {
            if (restart is { } op && ReferenceEquals(op.Stopping, source))
                stoppedForRestart = op;
            bool wasCurrent = ReferenceEquals(source, process);
            if (!wasCurrent && stoppedForRestart == null)
            {
                source.Dispose();
                return;
            }
            if (wasCurrent)
            {
                hadBackground = hasLiveBackgroundWork;
                DetachProcessLocked();
            }

            aborted = parser.AbortOpenSteps(AntigravityStreamRules.InterruptedToolOutput);
            ownedByRestart = stoppedForRestart != null || restart is { TookOver: true, ContinuationStarted: false };
            if (!ownedByRestart && running && ReferenceEquals(turnProcess, source))
            {
                wasRunning = true;
                wasInterrupted = interruptRequested;
                needsSignIn = signInRequired;
            }
        }

        var tail = source.GetStderrTail();
        foreach (var ev in aborted)
            Raise(ev);
        if (hadBackground)
            Raise(new AgentBackgroundWorkEvent(0, Array.Empty<AgentBackgroundTool>()));
        if (wasRunning && !needsSignIn && !wasInterrupted && TryRetryFresh(tail))
        {
            Raise(new AgentProcessExitedEvent(exitCode, tail, Expected: true, source.InstanceId));
            stoppedForRestart?.ExitHandled.TrySetResult();
            source.Dispose();
            return;
        }

        if (wasRunning)
        {
            lock (gate)
            {
                running = false;
                interruptRequested = false;
                currentTurn = null;
                turnProcess = null;
                if (restart is { ContinuationStarted: true } continuing)
                    continuing.ContinuationReported = true;
            }
            Raise(needsSignIn ? new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, AntigravityStreamRules.NotSignedInMessage, null)
                : wasInterrupted ? new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null)
                : new AgentTurnCompletedEvent(AgentTurnOutcome.Failed,
                    $"agy 程序意外結束（結束碼 {exitCode?.ToString() ?? "不明"}）{(string.IsNullOrWhiteSpace(tail) ? string.Empty : "：" + tail)}", null));
        }
        Raise(new AgentProcessExitedEvent(exitCode, tail, Expected: ownedByRestart || !wasRunning || wasInterrupted, source.InstanceId));
        PublishSafePoint();
        stoppedForRestart?.ExitHandled.TrySetResult();
        source.Dispose();
    }

    private void DetachProcessLocked()
    {
        process = null;
        processCli = null;
        processSignature = null;
        runningVersion = null;
        cliTurn = false;
        tookOverCliTurn = false;
        hasLiveBackgroundWork = false;
        liveBackgroundTools = Array.Empty<AgentBackgroundTool>();
    }

    /// <summary>停止這一輪：準備期間取消準備；已送出就結束程序樹（OnExited 回報已停止，下一則訊息以 --conversation 續接）</summary>
    public Task InterruptAsync()
    {
        AgentProcess? target;
        CancellationTokenSource? preparation;
        lock (gate)
        {
            suspendedConversation = null;
            if (restart is { ContinuationStarted: false } op)
            {
                op.Cancelled = true;
                if (op.TookOver)
                    return Task.CompletedTask;
            }
            if (!running)
                return Task.CompletedTask;
            interruptRequested = true;
            preparation = preparing;
            target = preparation == null ? process : null;
        }
        try
        {
            preparation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        target?.Kill();
        return Task.CompletedTask;
    }

    public Task AnswerQuestionAsync(string requestKey, IReadOnlyDictionary<string, IReadOnlyList<string>> answers) => Task.CompletedTask;

    public Task DeclineQuestionAsync(string requestKey) => Task.CompletedTask;

    public Task RespondApprovalAsync(string requestKey, AgentApprovalChoice choice, string? feedback) => Task.CompletedTask;

    public Task RespondPlanAsync(string requestKey, AgentPlanChoice choice, string? feedback) => Task.CompletedTask;

    /// <summary>權限模式只能在啟動時指定：一律下一則訊息生效（重開程序、--conversation 續接）</summary>
    public Task<bool> SetPermissionModeAsync(AgentPermissionMode mode) => Task.FromResult(false);

    /// <summary>模型與推理強度也只能在啟動時指定（--model、--effort）：一律下一則訊息生效（重開程序、--conversation 續接）</summary>
    public Task<bool> SetModelAsync(string? model, string? effort) => Task.FromResult(false);

    /// <summary>agy 的 stream-json 沒有把訊息併進進行中一輪的協定：執行中送出的一律排進佇列，這一輪結束後送</summary>
    public Task<bool> SteerAsync(AgentTurnInput input, CancellationToken cancellationToken = default) => Task.FromResult(false);

    /// <summary>agy 的 Remote Control 是獨立的子指令（agy remote-control），對話程序開不了</summary>
    public bool HasRemoteControl => false;

    public Task SetRemoteControlAsync(AgentRemoteControlOptions? options) => Task.CompletedTask;

    public Task RenameAsync(string title) => Task.CompletedTask;

    public Task<bool> ReattachRemoteControlAsync(AgentSessionSettings settings) => Task.FromResult(false);

    // ───────────────────────────── CLI 熱更新與 app 排空：以新版重開 ─────────────────────────────

    /// <summary>
    /// 以新版 CLI 重開程序（與 Codex／Grok 共用熱更新契約 2.5 流程表）：
    /// SafePoint 模式下工具執行中回 Deferred；沒有程序也沒有停下待接續的一輪回 Restarted；
    /// 否則停下舊程序並確認整個程序樹（含 agy language server 與子指令）都已結束；
    /// Settings=null 回 Stopped（記住被打斷的對話待接續），不接續回 Restarted，
    /// 要接續時以 --conversation 啟動新版、發 AgentTurnStartedByCliEvent 並送出接續訊息，回 RestartedAndContinued
    /// </summary>
    public async Task<AgentRestartResult> RestartAsync(AgentRestartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var op = new RestartOperation();
        lock (gate)
        {
            if (disposed)
                return new AgentRestartResult(AgentRestartOutcome.Failed, Error: "對話已關閉");
            if (restart != null)
                return new AgentRestartResult(AgentRestartOutcome.Deferred);
            restart = op;
        }

        try
        {
            return await RestartCoreAsync(request, op).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Antigravity 以新版重開失敗", ex);
            AgentProcess? leftover;
            lock (gate)
            {
                running = false;
                currentTurn = null;
                turnProcess = null;
                suspendedConversation = null;
                op.Stopping = null;
                leftover = process;
            }
            leftover?.Kill();
            return new AgentRestartResult(AgentRestartOutcome.Failed, op.PreviousInstance, Error: ex.Message);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(restart, op))
                    restart = null;
            }
            PublishSafePoint();
        }
    }

    private async Task<AgentRestartResult> RestartCoreAsync(AgentRestartRequest request, RestartOperation op)
    {
        while (true)
        {
            Task? prepared;
            lock (gate)
                prepared = preparationDone?.Task;
            if (prepared == null)
                break;
            await prepared.ConfigureAwait(false);
        }

        AgentProcess? old;
        string? convId;
        bool turnRunning;
        lock (gate)
        {
            if (disposed)
                return new AgentRestartResult(AgentRestartOutcome.Failed, Error: "對話已關閉");
            bool live = process != null && !process.HasExited;
            switch (CodexAppServerRules.DecideRestartEntry(request.Mode, CurrentSafePointLocked().IsAtSafePoint, live, suspendedConversation != null))
            {
                case CodexRestartEntry.Defer:
                    return new AgentRestartResult(AgentRestartOutcome.Deferred);
                case CodexRestartEntry.NothingToRestart:
                    return new AgentRestartResult(AgentRestartOutcome.Restarted);
            }

            op.TookOver = true;
            old = live ? process : null;
            op.PreviousInstance = old?.InstanceId;
            op.Stopping = old;
            convId = conversationId ?? processConversation ?? request.Settings?.SessionId;
            turnRunning = old != null && running && ReferenceEquals(turnProcess, old);
        }

        if (old != null)
        {
            Log.Info($"Antigravity 以新版重開（{request.Reason}，{request.FromVersion ?? "?"} → {request.ToVersion ?? "?"}）：停下程序 {old.InstanceId}"
                + (turnRunning ? "（這一輪執行中）" : string.Empty));
            var stopError = await StopForRestartAsync(old, op).ConfigureAwait(false);
            if (stopError != null)
            {
                lock (gate)
                {
                    op.Stopping = null;
                    running = false;
                    currentTurn = null;
                    turnProcess = null;
                    suspendedConversation = null;
                }
                Log.Warn($"Antigravity 重開失敗：{stopError}");
                return new AgentRestartResult(AgentRestartOutcome.Failed, op.PreviousInstance, Error: stopError);
            }
        }

        string? resumeConv;
        AgentProcess? detached;
        AgentRestartResult? settled = null;
        lock (gate)
        {
            op.Stopping = null;
            detached = old != null && ReferenceEquals(process, old) ? old : null;
            if (detached != null)
                DetachProcessLocked();
            bool interrupted = (turnRunning && !op.TurnFinishedOnItsOwn) || suspendedConversation != null;
            resumeConv = suspendedConversation ?? conversationId ?? convId;
            var followUp = disposed
                ? CodexRestartFollowUp.NoContinuation
                : CodexAppServerRules.DecideRestartFollowUp(request.Settings != null, interrupted && !string.IsNullOrWhiteSpace(resumeConv), request.AutoContinue, op.Cancelled);
            switch (followUp)
            {
                case CodexRestartFollowUp.Stop:
                case CodexRestartFollowUp.StopAndSuspend:
                    settled = new AgentRestartResult(AgentRestartOutcome.Stopped, op.PreviousInstance);
                    break;
                case CodexRestartFollowUp.NoContinuation:
                    settled = disposed
                        ? new AgentRestartResult(AgentRestartOutcome.Failed, op.PreviousInstance, Error: "對話已關閉")
                        : new AgentRestartResult(AgentRestartOutcome.Restarted, op.PreviousInstance);
                    break;
            }

            if (settled != null)
            {
                running = false;
                currentTurn = null;
                turnProcess = null;
                suspendedConversation = followUp == CodexRestartFollowUp.StopAndSuspend ? resumeConv : null;
            }
            else
            {
                suspendedConversation = null;
                running = true;
                currentTurn = null;
                turnProcess = null;
                interruptRequested = false;
            }
        }

        if (detached != null)
        {
            Raise(new AgentProcessExitedEvent(null, detached.GetStderrTail(), Expected: true, detached.InstanceId));
            detached.Dispose();
        }
        if (settled != null)
            return settled;
        return await ContinueAfterRestartAsync(request, request.Settings!, resumeConv!, op).ConfigureAwait(false);
    }

    private async Task<string?> StopForRestartAsync(AgentProcess old, RestartOperation op)
    {
        var before = old.CaptureTree();
        var tree = old.CaptureTree();
        await old.CloseAsync(RestartCloseGrace).ConfigureAwait(false);
        bool gone = await WaitTreeExitAsync(tree).ConfigureAwait(false) & await WaitTreeExitAsync(before).ConfigureAwait(false);
        await Task.WhenAny(op.ExitHandled.Task, Task.Delay(ExitHandledTimeout)).ConfigureAwait(false);
        return gone ? null : "舊的 Antigravity 程序或它正在執行的指令沒有結束，為了避免和接續後重跑的指令重疊，沒有以新版重開";
    }

    private async Task<bool> WaitTreeExitAsync(ProcessTreeSnapshot tree)
    {
        if (await tree.WaitForExitAsync(RestartTreeExitTimeout).ConfigureAwait(false))
            return true;
        int killed = tree.KillAlive();
        Log.Warn($"Antigravity 舊程序樹還有 {killed} 個程序沒有結束，已逐一結束");
        return await tree.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private async Task<AgentRestartResult> ContinueAfterRestartAsync(AgentRestartRequest request, AgentSessionSettings settings, string resumeConv, RestartOperation op)
    {
        AgentProcess? fresh = null;
        try
        {
            fresh = await StartProcessAsync(settings, resumeConv, CancellationToken.None).ConfigureAwait(false);
            var continuationInput = new AgentTurnInput(request.EffectiveContinuationText, Array.Empty<AgentAttachment>());
            string messageLine;
            lock (gate)
            {
                if (op.Cancelled || disposed || !ReferenceEquals(process, fresh))
                {
                    running = false;
                    currentTurn = null;
                    turnProcess = null;
                    return new AgentRestartResult(AgentRestartOutcome.Restarted, op.PreviousInstance, ReferenceEquals(process, fresh) ? fresh.InstanceId : null);
                }
                op.ContinuationStarted = true;
                running = true;
                interruptRequested = false;
                lastWorkingDirectory = settings.WorkingDirectory;
                conversationId = resumeConv;
                currentTurn = new PendingTurn(continuationInput, settings, UsedConversation: true, RetriedFresh: false);
                turnProcess = fresh;
                processTurnSent = true;
                parser.BeginTurn();
                messageLine = AntigravityStreamRules.BuildUserMessage(request.EffectiveContinuationText);
            }

            Raise(new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));
            await fresh.WriteLineAsync(messageLine).ConfigureAwait(false);
            TryRetryFresh(null);
            Log.Info($"Antigravity 已以新版重開並接續（程序 {op.PreviousInstance ?? "無"} → {fresh.InstanceId}，版本 {RunningVersion ?? "不明"}）");
            return new AgentRestartResult(AgentRestartOutcome.RestartedAndContinued, op.PreviousInstance, fresh.InstanceId);
        }
        catch (Exception ex)
        {
            bool reported;
            bool cancelled;
            lock (gate)
            {
                reported = op.ContinuationReported;
                cancelled = op.Cancelled || disposed;
                if (!reported)
                {
                    running = false;
                    currentTurn = null;
                    turnProcess = null;
                    interruptRequested = false;
                }
            }
            if (reported)
                return new AgentRestartResult(AgentRestartOutcome.RestartedAndContinued, op.PreviousInstance, fresh?.InstanceId);

            if (fresh != null)
            {
                fresh.Kill();
                await Task.WhenAny(fresh.ExitTask, Task.Delay(FailedProcessExitTimeout)).ConfigureAwait(false);
            }
            if (cancelled)
                return new AgentRestartResult(AgentRestartOutcome.Restarted, op.PreviousInstance);
            Log.Warn($"Antigravity 以新版重開後接續失敗：{ex.Message}");
            return new AgentRestartResult(AgentRestartOutcome.Failed, op.PreviousInstance, Error: ex.Message);
        }
    }

    public Task CloseAsync() => CloseCoreAsync(null);

    /// <summary>
    /// 強制停止背景工作：agy 的 stream-json 沒有逐一停止的協定（control_request 會讓它 exit 2），只能全部停止——閒置時結束 CLI 的程序樹
    /// （背景工作是它的子孫，跟著結束；下次送出照常以 --conversation 續接），執行中不做
    /// </summary>
    public async Task<AgentBackgroundStopResult> StopBackgroundWorkAsync(string? stopId)
    {
        if (stopId != null)
            return new AgentBackgroundStopResult(0, "Antigravity 的背景工作只能全部停止");
        AgentProcess target;
        int count;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited)
                return new AgentBackgroundStopResult(0, "Antigravity 程序已經結束，背景工作也跟著結束了");
            if (running || restart != null)
                return new AgentBackgroundStopResult(0, "代理還在處理這一輪：先按停止，再停止背景工作");
            if (!hasLiveBackgroundWork)
                return new AgentBackgroundStopResult(0, "背景工作已經結束了");
            target = process;
            count = Math.Max(1, liveBackgroundTools.Count);
        }
        var tree = await Task.Run(target.CaptureTree).ConfigureAwait(false);
        if (!await CloseCoreAsync(tree, onlyIfIdle: target).ConfigureAwait(false))
            return new AgentBackgroundStopResult(0, "代理剛開始處理新的一輪：先按停止，再停止背景工作");
        Log.Info($"Antigravity 停止背景工作：結束程序樹（{tree.Processes.Count} 個程序）");
        return new AgentBackgroundStopResult(count, null);
    }

    /// <summary>
    /// 關閉程序。killTree 不是 null＝卸下程序後立刻結束那棵程序樹（強制停止背景工作，不等 CLI 收尾）；onlyIfIdle 不是 null＝只在它還是目前的程序、
    /// 閒置、沒在重開時才關（否則什麼都不做，回 false）
    /// </summary>
    private async Task<bool> CloseCoreAsync(ProcessTreeSnapshot? killTree, AgentProcess? onlyIfIdle = null)
    {
        AgentProcess? target;
        bool hadBackground;
        lock (gate)
        {
            if (onlyIfIdle != null && (!ReferenceEquals(process, onlyIfIdle) || running || restart != null))
                return false;
            suspendedConversation = null;
            if (restart != null)
                restart.Cancelled = true;
            target = process;
            hadBackground = hasLiveBackgroundWork;
            DetachProcessLocked();
            running = false;
            currentTurn = null;
            turnProcess = null;
        }
        if (hadBackground)
            Raise(new AgentBackgroundWorkEvent(0, Array.Empty<AgentBackgroundTool>()));
        PublishSafePoint();
        if (target == null)
            return true;
        killTree?.KillAlive();
        try
        {
            await target.CloseAsync(CloseGrace);
        }
        finally
        {
            target.Dispose();
        }
        return true;
    }

    private AgentSafePointChangedEvent CurrentSafePointLocked()
        => new(ToolRunning: running && parser.ToolRunning, WaitingForUser: false, HasLiveBackgroundWork: hasLiveBackgroundWork || cliTurn || tookOverCliTurn);

    private void PublishSafePoint()
    {
        Debug.Assert(!Monitor.IsEntered(gate), "PublishSafePoint 不能在 gate 內呼叫");
        lock (safePointOrder)
        {
            AgentSafePointChangedEvent now;
            lock (gate)
                now = CurrentSafePointLocked();
            if (now == reportedSafePoint)
                return;
            reportedSafePoint = now;
            Raise(now);
        }
    }

    private void Raise(AgentEvent ev)
    {
        try
        {
            EventReceived?.Invoke(ev);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AntigravityAgentSession] 事件處理失敗：{ex}");
        }
    }

    public void Dispose()
    {
        AgentProcess? target;
        CancellationTokenSource? preparation;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            suspendedConversation = null;
            if (restart != null)
                restart.Cancelled = true;
            target = process;
            DetachProcessLocked();
            running = false;
            preparation = preparing;
            preparing = null;
        }
        try
        {
            preparation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        target?.Dispose();
    }
}
