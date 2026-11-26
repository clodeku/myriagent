using System.Diagnostics;
using Myriagent.Cli;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// Antigravity 代理（ACP 雙向協定，<c>agy acp --stdio</c>，agy 1.5.8+／1.5.17 實測）：
/// <list type="bullet">
/// <item>單一常駐程序以 JSON-RPC 2.0 NDJSON 通訊：首輪 <c>session/new</c>、重開程序後以 <c>session/resume</c> 續接（不重播歷史訊息；找不到對話時改開新對話並附上前文節錄）</item>
/// <item>對話中即時切換模型（<c>session/set_model</c>）、推理強度（<c>session/set_config_option</c> <c>thought_level</c>）與權限模式（<c>session/set_mode</c>），不必重開 190 MB 的 Go language server</item>
/// <item>支援「每步詢問」與子代理／工作流互動核准（<c>session/request_permission</c>）、<c>ask_question</c> 結構化問卷卡（<c>elicitation/create</c>）、內嵌 Base64 圖片、上下文視窗用量（<c>usage_update</c>）與 <c>update_steps</c> 即時步驟清單</item>
/// <item>停止以 <c>session/cancel</c> 平滑中斷進行中回合，不強殺常駐程序；CLI 熱更新與自我更新排空遵循與 Codex／Grok 相同的兩階段安全點契約</item>
/// </list>
/// </summary>
internal sealed partial class AntigravityAcpSession : IAgentSession
{
    private const string ConversationLostNotice = "agy 找不到這個對話先前的紀錄，已改開新對話並附上前文節錄";

    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionRequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ConfigRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ExitHandledTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailedProcessExitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CliSlashCommandTimeout = TimeSpan.FromSeconds(30);

    internal TimeSpan CancelTimeout { get; init; } = TimeSpan.FromSeconds(10);
    internal TimeSpan RestartInterruptTimeout { get; init; } = TimeSpan.FromSeconds(15);
    internal TimeSpan RestartCloseGrace { get; init; } = TimeSpan.FromSeconds(3);
    internal TimeSpan RestartTreeExitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    internal Func<string, string?>? TranscriptPathResolver { get; set; } = AntigravityTranscriptReader.DefaultResolveTranscriptPath;
    internal string? UserProfileOverride { get; set; }

    private enum PendingKind
    {
        Permission,
        Question,
    }

    private sealed record PendingRequest(
        JToken Id,
        PendingKind Kind,
        AgentProcess Process,
        AntigravityPermissionRequest? Permission = null,
        IReadOnlyList<AgentQuestion>? Questions = null);

    private sealed class RestartOperation
    {
        public bool TookOver;
        public string? PreviousInstance;
        public AgentProcess? Stopping;
        public readonly TaskCompletionSource TurnStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ExitHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TurnFinishedOnItsOwn;
        public bool Cancelled;
        public bool ContinuationStarted;
        public bool ContinuationReported;
    }

    private readonly object gate = new();
    private readonly object safePointOrder = new();
    private AgentProcess? process;
    private GrokAcpConnection? connection;
    private AntigravityAcpUpdateParser parser = new();
    private string? processSignature;
    private AgentSharedLaunch sharedLaunch = AgentSharedLaunch.None;
    private CliBinaryIdentity? processCli;
    private string? runningVersion;

    private string? processSessionId;
    private string? sessionId;
    private string? pendingTitle;
    private string? lastWorkingDirectory;

    private IReadOnlyList<AntigravityAcpConfigOption> configOptions = Array.Empty<AntigravityAcpConfigOption>();
    private string? defaultModelId;
    private string? currentTokenModel;
    private bool promptImages = true;

    private string? appliedModel;
    private bool modelKnown;
    private string? appliedEffort;
    private string? appliedModeId;
    private AgentPermissionMode currentMode = AgentPermissionMode.Ask;

    private bool running;
    private bool interruptRequested;
    private bool cancelSent;
    private bool promptWritten;
    private long? promptRequestId;
    private AgentProcess? promptProcess;
    private CancellationTokenSource? preparing;
    private TaskCompletionSource? preparationDone;
    private readonly Dictionary<string, PendingRequest> pendingRequests = new(StringComparer.Ordinal);
    private readonly HashSet<string> sessionAllowedKinds = new(StringComparer.OrdinalIgnoreCase);

    private string? suspendedSession;
    private RestartOperation? restart;
    private AgentSafePointChangedEvent? reportedSafePoint;
    private bool disposed;

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

    public AgentActivitySource? ActivitySource
    {
        get
        {
            lock (gate)
                return process?.Activity;
        }
    }

    internal int? CurrentProcessId
    {
        get
        {
            lock (gate)
                return process?.ProcessId;
        }
    }

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
                return parser.HasLiveBackgroundWork;
        }
    }

    public IReadOnlyList<AgentBackgroundTool> LiveBackgroundTools
    {
        get
        {
            lock (gate)
                return parser.LiveBackgroundTools;
        }
    }

    internal static string Signature(AgentSessionSettings settings)
        => AntigravityAcpRules.Signature(
            settings.AccountKey,
            settings.Launcher?.FileName,
            settings.WorkingDirectory,
            settings.ConversationDirectory,
            settings.UseLocalCustomizations,
            settings.Agent,
            settings.AutoCompactTokens);

    // ───────────────────────────── 送出訊息與本地斜線指令 ─────────────────────────────

    public async Task SendAsync(AgentTurnInput input, AgentSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);

        var cliSlash = input.Attachments.Count == 0
            ? AntigravityStreamRules.ClassifyCliOnlySlashCommand(input.Text)
            : new AntigravityStreamRules.CliOnlySlashClassification(AntigravityStreamRules.CliOnlySlashKind.None);
        bool isCliOnlySlash = cliSlash.Kind != AntigravityStreamRules.CliOnlySlashKind.None;

        var preparation = new CancellationTokenSource();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (restart != null)
            {
                preparation.Dispose();
                throw new AgentRestartInProgressException();
            }
            if (running)
            {
                preparation.Dispose();
                throw new InvalidOperationException("代理還在處理上一則訊息");
            }
            running = true;
            interruptRequested = false;
            cancelSent = false;
            suspendedSession = null;
            preparing = preparation;
            preparationDone = done;
        }

        try
        {
            if (isCliOnlySlash)
            {
                await HandleCliOnlySlashCommandAsync(cliSlash, settings, preparation.Token);
                return;
            }
            await SendCoreAsync(input, settings, preparation.Token);
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

    private async Task HandleCliOnlySlashCommandAsync(
        AntigravityStreamRules.CliOnlySlashClassification cliSlash,
        AgentSessionSettings settings,
        CancellationToken ct)
    {
        if (cliSlash.Kind == AntigravityStreamRules.CliOnlySlashKind.UiManaged)
        {
            var hint = cliSlash.PromptCommand == null
                ? "請直接由對話輸入框下方的模型、推理強度或權限選單切換（ACP 模式支援對話中即時生效）。"
                : cliSlash.UiHint!;
            lock (gate)
            {
                running = false;
                interruptRequested = false;
            }
            Raise(new AgentNoticeEvent(cliSlash.UiHint?.Replace("（下一則訊息生效）", "（即時生效）") ?? hint, IsError: false));
            Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, null));
            return;
        }

        try
        {
            if (TryAbortInterrupted())
                return;
            var launcher = settings.Launcher
                ?? throw new AgentCliUnavailableException(AgentProvider.Antigravity, "這台電腦尚未安裝 Antigravity CLI，請到「設定 → CLI」安裝");
            var cliArgs = cliSlash.SubcommandArgs ?? new[] { "-p", cliSlash.PromptCommand! };
            var cmdDisplay = cliSlash.SubcommandArgs != null
                ? string.Join(' ', cliSlash.SubcommandArgs)
                : $"-p {cliSlash.PromptCommand}";
            var run = await CliRunner.RunAsync(
                launcher,
                cliArgs,
                env => AntigravityStreamRules.ApplyLaunchEnvironment(env, settings),
                CliSlashCommandTimeout,
                ct,
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
                Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, $"agy {cmdDisplay} 逾時沒有回應", null));
                return;
            }
            var text = (run.Stdout ?? string.Empty).Trim();
            var err = (run.Stderr ?? string.Empty).Trim();
            if (run.ExitCode != 0 && text.Length == 0)
            {
                Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, err.Length > 0 ? err : $"agy {cmdDisplay} 結束碼 {run.ExitCode}", null));
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
    }

    private async Task SendCoreAsync(AgentTurnInput input, AgentSessionSettings settings, CancellationToken ct)
    {
        AgentProcess? current;
        AgentProcess? stale = null;
        int droppedBackground = 0;
        lock (gate)
        {
            var signature = Signature(settings);
            if (process != null && (process.HasExited || !string.Equals(processSignature, signature, StringComparison.Ordinal)
                || !CliBinaryIdentity.SameOrUnknown(processCli, settings.Cli)))
            {
                stale = process;
                droppedBackground = DetachProcessLocked();
            }
            current = process;
        }
        RaiseBackgroundCleared(droppedBackground);

        try
        {
            if (stale != null)
            {
                await stale.CloseAsync(CloseGrace);
                stale.Dispose();
            }
            if (TryAbortInterrupted())
                return;

            current ??= await StartProcessAsync(settings, ct);
            ThrowIfDisposed();
            if (TryAbortInterrupted())
                return;

            GrokAcpConnection conn;
            string? sid;
            lock (gate)
            {
                conn = connection ?? throw new InvalidOperationException("Antigravity 程序已結束");
                sid = processSessionId;
            }

            bool createdNew = false, lostHistory = false;
            if (sid == null)
                (sid, createdNew, lostHistory) = await OpenSessionAsync(conn, settings, ct);

            if (TryAbortInterrupted())
                return;
            await ApplyConfigAsync(conn, sid, settings, ct);
            if (TryAbortInterrupted())
                return;

            bool imagesInline;
            JObject promptParams;
            long id;
            lock (gate)
            {
                imagesInline = promptImages;
            }

            string? instructions = null;
            string? historyRecap = null;
            string? wdNotice = null;
            lock (gate)
            {
                if (createdNew)
                    instructions = settings.InstructionsWith(sharedLaunch);
                if (lostHistory)
                    historyRecap = settings.HistoryRecap;
                if (!createdNew && lastWorkingDirectory != null && !GrokAcpRules.SameDirectory(lastWorkingDirectory, settings.WorkingDirectory))
                    wdNotice = AntigravityStreamRules.BuildWorkingDirectoryNotice(settings.WorkingDirectory);
            }

            promptParams = AntigravityAcpRules.BuildPromptParams(
                sid,
                instructions,
                historyRecap,
                input.Text,
                input.Attachments,
                supportsInlineImages: imagesInline,
                workingDirectoryNotice: wdNotice);

            lock (gate)
            {
                if (interruptRequested)
                {
                    id = 0;
                }
                else
                {
                    lastWorkingDirectory = settings.WorkingDirectory;
                    currentMode = settings.PermissionMode;
                    parser.BeginTurn(planMode: settings.PermissionMode == AgentPermissionMode.Plan);
                    id = conn.ReserveId();
                    promptRequestId = id;
                    promptProcess = current;
                    promptWritten = false;
                    preparing = null;
                }
            }

            if (id == 0)
            {
                TryAbortInterrupted();
                return;
            }

            await conn.SendRequestAsync(id, "session/prompt", promptParams);
            await AfterPromptWrittenAsync(current, id, sid);
        }
        catch (OperationCanceledException) when (IsInterruptRequested())
        {
            TryAbortInterrupted();
        }
        catch (IOException) when (IsTurnHandledElsewhere())
        {
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
                {
                    running = false;
                    promptRequestId = null;
                    promptProcess = null;
                }
            }
            if (stopped)
            {
                TryAbortInterrupted();
                return;
            }
            throw;
        }
    }

    private async Task<AgentProcess> StartProcessAsync(AgentSessionSettings settings, CancellationToken ct)
    {
        var launcher = settings.Launcher
            ?? throw new AgentCliUnavailableException(AgentProvider.Antigravity, "這台電腦尚未安裝 Antigravity CLI，請到「設定 → CLI」安裝");
        if (!Directory.Exists(settings.WorkingDirectory))
            throw new DirectoryNotFoundException($"工作資料夾不存在：{settings.WorkingDirectory}");

        sharedLaunch = await settings.PrepareSharedAsync();
        bool hasKnownSession;
        lock (gate)
            hasKnownSession = (sessionId ?? (settings.SessionStarted ? settings.SessionId : null)) != null;

        var args = AntigravityAcpRules.BuildAgentArguments(
            settings.ConversationDirectory,
            settings.UseLocalCustomizations,
            settings.Agent,
            sharedLaunch.SharedMemoryDirectory,
            isResume: hasKnownSession);

        var started = await AgentProcess.StartWithRetryAsync(
            launcher,
            args,
            settings.WorkingDirectory,
            env => AntigravityStreamRules.ApplyLaunchEnvironment(env, settings),
            ct,
            host: settings.ProcessHost);

        var conn = Wire(started);

        lock (gate)
        {
            if (disposed)
            {
                started.Dispose();
                throw new ObjectDisposedException(nameof(AntigravityAcpSession));
            }
            process = started;
            connection = conn;
            // 經代理主機開的：app 更新時放得開（AntigravityAcpSession.Detach.cs）
            processHost = started.CanDetach ? settings.ProcessHost : null;
            processSignature = Signature(settings);
            processCli = settings.Cli;
            parser = new AntigravityAcpUpdateParser("agy-" + started.InstanceId[..8], TranscriptPathResolver);
            processSessionId = null;
            ResetAppliedLocked();
            runningVersion = settings.Cli?.Version;
        }

        Raise(new AgentProcessStartedEvent(started.InstanceId, settings.Cli));
        PublishSafePoint();
        started.BeginReading();

        (JToken? Result, GrokAcpError? Error) init;
        try
        {
            init = await conn.RequestAsync("initialize", AntigravityAcpRules.BuildInitializeParams(), SessionRequestTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            started.Kill();
            throw;
        }
        if (init.Error != null)
        {
            started.Kill();
            throw new InvalidOperationException(AntigravityAcpRules.IsAuthRequired(init.Error)
                ? AntigravityStreamRules.NotSignedInMessage
                : $"Antigravity ACP 初始化失敗：{init.Error.Text}");
        }

        var initObj = init.Result as JObject;
        var version = AntigravityAcpRules.ParseAgentVersion(initObj) ?? settings.Cli?.Version;
        var supportsImages = initObj == null || AntigravityAcpRules.ParsePromptImages(initObj);
        lock (gate)
        {
            if (ReferenceEquals(process, started))
            {
                runningVersion = version;
                promptImages = supportsImages;
            }
        }
        if (version != null)
            Raise(new AgentRunningVersionEvent(started.InstanceId, version));
        return started;
    }

    private async Task<(string SessionId, bool CreatedNew, bool LostHistory)> OpenSessionAsync(
        GrokAcpConnection conn,
        AgentSessionSettings settings,
        CancellationToken ct)
    {
        string? known;
        lock (gate)
            known = sessionId ?? (settings.SessionStarted ? settings.SessionId : null);

        var extraDirs = BuildAdditionalDirectories(settings, sharedLaunch);
        bool lost = false;

        if (!string.IsNullOrWhiteSpace(known))
        {
            var (resumed, resumeError) = await conn.RequestAsync(
                "session/resume",
                AntigravityAcpRules.BuildResumeParams(known, settings.WorkingDirectory, extraDirs),
                SessionRequestTimeout,
                ct);
            if (resumeError == null)
            {
                var resumedObj = resumed as JObject;
                ObserveSessionState(conn, known, resumedObj, settings.Model, isNew: false);
                return (known, false, false);
            }
            if (AntigravityAcpRules.IsAuthRequired(resumeError))
                throw new InvalidOperationException(AntigravityStreamRules.NotSignedInMessage);
            if (!AntigravityAcpRules.IsSessionNotFound(resumeError))
                throw new InvalidOperationException($"無法續接 Antigravity 對話：{resumeError.Text}");
            lost = true;
            Raise(new AgentNoticeEvent(ConversationLostNotice, IsError: false));
        }

        var (created, createError) = await conn.RequestAsync(
            "session/new",
            AntigravityAcpRules.BuildNewSessionParams(settings.WorkingDirectory, extraDirs),
            SessionRequestTimeout,
            ct);
        if (createError != null)
        {
            throw new InvalidOperationException(AntigravityAcpRules.IsAuthRequired(createError)
                ? AntigravityStreamRules.NotSignedInMessage
                : $"無法建立 Antigravity 對話：{createError.Text}");
        }

        var createdObj = created as JObject;
        var actual = AntigravityAcpRules.ParseSessionId(createdObj) ?? Guid.NewGuid().ToString();
        ObserveSessionState(conn, actual, createdObj, settings.Model, isNew: true);

        string? titleToSync;
        lock (gate)
        {
            titleToSync = pendingTitle ?? settings.Title;
            pendingTitle = null;
        }
        if (!string.IsNullOrWhiteSpace(titleToSync))
            AntigravityStreamRules.WriteConversationTitle(actual, titleToSync, UserProfileOverride);

        return (actual, true, lost);
    }

    private void ObserveSessionState(GrokAcpConnection conn, string sid, JObject? sessionObj, string? requestedModel, bool isNew)
    {
        var (currentModel, availableModels) = AntigravityAcpRules.ParseSessionModels(sessionObj);
        var (currentModeId, _) = AntigravityAcpRules.ParseSessionModes(sessionObj);
        var parsedConfig = AntigravityAcpRules.ParseSessionConfigOptions(sessionObj);

        lock (gate)
        {
            if (ReferenceEquals(connection, conn))
            {
                processSessionId = sid;
                sessionId = sid;
                parser.BindSession(sid);
            }
            if (parsedConfig.Count > 0)
                configOptions = parsedConfig;
            if (AntigravityStreamRules.NormalizeModel(requestedModel) == null && !string.IsNullOrEmpty(currentModel))
                defaultModelId = currentModel;
            currentTokenModel = currentModel ?? currentTokenModel ?? defaultModelId;
            appliedModeId = currentModeId;
            if (!isNew)
            {
                modelKnown = false;
                appliedEffort = null;
            }
        }

        if (availableModels.Count > 0)
            Raise(new AgentModelsEvent(availableModels));
        Raise(new AgentSessionStartedEvent(sid, currentModel, isNew ? "new" : "resumed"));
    }

    private static List<string> BuildAdditionalDirectories(AgentSessionSettings settings, AgentSharedLaunch shared)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.ConversationDirectory))
            list.Add(settings.ConversationDirectory);
        if (!string.IsNullOrWhiteSpace(shared.SharedMemoryDirectory))
            list.Add(shared.SharedMemoryDirectory);
        return list;
    }

    // ───────────────────────────── 即時切換模型、強度與權限模式 ─────────────────────────────

    private async Task ApplyConfigAsync(GrokAcpConnection conn, string sid, AgentSessionSettings settings, CancellationToken ct)
    {
        await ApplyModelConfigAsync(conn, sid, settings.Model, settings.Effort, ct);
        await ApplyModeAsync(conn, sid, settings.PermissionMode, ct);
    }

    private async Task<bool> ApplyModelConfigAsync(GrokAcpConnection conn, string sid, string? requestedModel, string? requestedEffort, CancellationToken ct)
    {
        string? sendModel = null;
        string? sendEffort = null;
        string? normalizedModel = AntigravityStreamRules.NormalizeModel(requestedModel);

        lock (gate)
        {
            var (resolvedModel, resolvedEffort) = AntigravityAcpRules.ResolveModelAndEffort(requestedModel, requestedEffort, configOptions);
            if (!modelKnown || !string.Equals(normalizedModel, appliedModel, StringComparison.Ordinal))
                sendModel = resolvedModel ?? defaultModelId;
            if (resolvedEffort != null && (sendModel != null || !string.Equals(resolvedEffort, appliedEffort, StringComparison.OrdinalIgnoreCase)))
                sendEffort = resolvedEffort;
        }

        bool ok = true;
        if (!string.IsNullOrWhiteSpace(sendModel))
        {
            var (_, error) = await conn.RequestAsync("session/set_model", AntigravityAcpRules.BuildSetModelParams(sid, sendModel), ConfigRequestTimeout, ct);
            if (error != null)
            {
                ok = false;
                Raise(new AgentNoticeEvent($"Antigravity 無法切換到模型 {sendModel}：{error.Text}", IsError: false));
            }
            else
            {
                lock (gate)
                {
                    currentTokenModel = sendModel;
                    if (!AntigravityStreamRules.SupportsEffortArgument(sendModel))
                        sendEffort = null;
                }
            }
        }

        lock (gate)
        {
            appliedModel = normalizedModel;
            modelKnown = true;
            if (sendEffort != null && configOptions.Count > 0 && !configOptions.Any(c => string.Equals(c.Id, "thought_level", StringComparison.OrdinalIgnoreCase)))
                sendEffort = null;
        }

        if (!string.IsNullOrWhiteSpace(sendEffort))
        {
            var (res, error) = await conn.RequestAsync(
                "session/set_config_option",
                AntigravityAcpRules.BuildSetConfigOptionParams(sid, "thought_level", sendEffort),
                ConfigRequestTimeout,
                ct);
            if (error != null)
            {
                ok = false;
                Raise(new AgentNoticeEvent($"Antigravity 無法切換推理強度（{sendEffort}）：{error.Text}", IsError: false));
            }
            else
            {
                var updatedOpts = AntigravityAcpRules.ParseSessionConfigOptions(res as JObject);
                lock (gate)
                {
                    appliedEffort = sendEffort;
                    if (updatedOpts.Count > 0)
                        configOptions = updatedOpts;
                }
            }
        }

        return ok;
    }

    private async Task<bool> ApplyModeAsync(GrokAcpConnection conn, string sid, AgentPermissionMode mode, CancellationToken ct)
    {
        var desiredModeId = AntigravityAcpRules.SessionModeId(mode);
        bool needSend;
        lock (gate)
        {
            currentMode = mode;
            needSend = !string.Equals(appliedModeId, desiredModeId, StringComparison.Ordinal);
        }
        if (!needSend)
            return true;

        var (_, error) = await conn.RequestAsync("session/set_mode", AntigravityAcpRules.BuildSetModeParams(sid, mode), ConfigRequestTimeout, ct);
        if (error != null)
        {
            Raise(new AgentNoticeEvent($"Antigravity 無法切換權限模式（{desiredModeId}）：{error.Text}", IsError: false));
            return false;
        }

        lock (gate)
            appliedModeId = desiredModeId;
        return true;
    }

    public async Task<bool> SetPermissionModeAsync(AgentPermissionMode mode)
    {
        GrokAcpConnection? conn;
        string? sid;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited || connection == null || processSessionId == null || promptRequestId == null || restart != null)
                return false;
            conn = connection;
            sid = processSessionId;
            currentMode = mode;
        }
        try
        {
            return await ApplyModeAsync(conn, sid, mode, CancellationToken.None);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async Task<bool> SetModelAsync(string? model, string? effort)
    {
        GrokAcpConnection? conn;
        string? sid;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited || connection == null || processSessionId == null || promptRequestId == null || restart != null)
                return false;
            conn = connection;
            sid = processSessionId;
        }
        try
        {
            return await ApplyModelConfigAsync(conn, sid, model, effort, CancellationToken.None);
        }
        catch (IOException)
        {
            return false;
        }
    }

    public Task<bool> SteerAsync(AgentTurnInput input, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public bool HasRemoteControl => false;

    public Task SetRemoteControlAsync(AgentRemoteControlOptions? options) => Task.CompletedTask;

    public Task<bool> ReattachRemoteControlAsync(AgentSessionSettings settings) => Task.FromResult(false);

    public Task RenameAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Task.CompletedTask;
        string? sid;
        lock (gate)
        {
            sid = sessionId;
            if (string.IsNullOrWhiteSpace(sid))
                pendingTitle = title.Trim();
        }
        if (!string.IsNullOrWhiteSpace(sid))
            AntigravityStreamRules.WriteConversationTitle(sid, title, UserProfileOverride);
        return Task.CompletedTask;
    }

    // ───────────────────────────── ACP 通知、請求與回合結束 ─────────────────────────────

    private bool IsCurrent(AgentProcess source)
    {
        lock (gate)
            return ReferenceEquals(source, process) && !(restart is { } op && ReferenceEquals(op.Stopping, source));
    }

    private void OnStderr(AgentProcess source, string line)
    {
        if (AntigravityStreamRules.TryParsePluginSignInNotice(line, out var pluginNotice) && pluginNotice != null && IsCurrent(source))
            Raise(new AgentNoticeEvent(pluginNotice, IsError: false));
    }

    private void OnNotification(AgentProcess source, GrokAcpMessage message)
    {
        if (!IsCurrent(source))
            return;
        if (message.Method != "session/update" || message.Params["update"] is not JObject update)
            return;

        var updateKind = update.Value<string>("sessionUpdate");
        if (updateKind == "current_mode_update")
        {
            OnModeUpdate(update.Value<string>("currentModeId"));
            return;
        }
        if (updateKind == "config_option_update")
        {
            var opts = AntigravityAcpRules.ParseSessionConfigOptions(update);
            if (opts.Count > 0)
            {
                lock (gate)
                    configOptions = opts;
            }
            return;
        }

        List<AgentEvent> events;
        lock (gate)
            events = parser.HandleUpdate(update);
        foreach (var ev in events)
            Raise(ev);
        if (events.Count > 0)
            PublishSafePoint();
        ScheduleTimerExpiry();
    }

    /// <summary>已排定的單次計時器到期檢查的時間（gate 保護；同一個時間不重複排）</summary>
    private DateTime? timerCheckAtUtc;

    /// <summary>
    /// 單次計時器（schedule 的 DurationSeconds）到期時把它從背景工作移除、通知中樞（ACP 沒有「計時器到了」的更新，
    /// AntigravityAcpUpdateParser.ExpireTimers 照時間算）：不然中樞記著的背景工作清單要等下一則更新才變，閒置的對話就一直掛著
    /// </summary>
    private void ScheduleTimerExpiry()
    {
        TimeSpan delay;
        lock (gate)
        {
            if (disposed || parser.NextTimerExpiryUtc is not { } next || (timerCheckAtUtc is { } scheduled && scheduled <= next))
                return;
            timerCheckAtUtc = next;
            delay = next - parser.UtcNow() + TimeSpan.FromMilliseconds(200);
        }
        _ = Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay).ContinueWith(_ =>
        {
            List<AgentEvent> events;
            lock (gate)
            {
                timerCheckAtUtc = null;
                if (disposed)
                    return;
                events = parser.ExpireTimers();
            }
            foreach (var ev in events)
                Raise(ev);
            if (events.Count > 0)
                PublishSafePoint();
            ScheduleTimerExpiry();
        }, TaskScheduler.Default);
    }

    private void OnModeUpdate(string? modeId)
    {
        if (string.IsNullOrWhiteSpace(modeId))
            return;
        AgentPermissionMode? changed = null;
        lock (gate)
        {
            appliedModeId = modeId;
            if (modeId == "plan" && currentMode != AgentPermissionMode.Plan)
                changed = currentMode = AgentPermissionMode.Plan;
            else if (modeId == "default" && currentMode == AgentPermissionMode.Plan)
                changed = currentMode = AgentPermissionMode.Ask;
            else if (modeId == "accept-edits" && currentMode == AgentPermissionMode.Plan)
                changed = currentMode = AgentPermissionMode.AcceptEdits;
            else if (modeId == "auto" && currentMode == AgentPermissionMode.Plan)
                changed = currentMode = AgentPermissionMode.Auto;
        }
        if (changed is { } next)
            Raise(new AgentPermissionModeChangedEvent(next));
    }

    private void OnRequest(AgentProcess source, GrokAcpMessage message)
    {
        var id = message.Id!;
        if (!IsCurrent(source))
        {
            _ = RespondQuietlyAsync(source, id, CancelledResult(message.Method));
            return;
        }

        switch (message.Method)
        {
            case "session/request_permission":
                HandlePermission(source, id, message.Params);
                break;
            case "elicitation/create":
                HandleElicitation(source, id, message.Params);
                break;
            default:
                _ = WriteQuietlyAsync(source, GrokAcpRules.BuildError(id, -32601, $"Clode Myriagent 不支援 {message.Method}"));
                break;
        }
    }

    private void HandlePermission(AgentProcess source, JToken id, JObject parameters)
    {
        var request = AntigravityAcpRules.ParsePermissionRequest(parameters);
        if (request == null)
        {
            _ = RespondQuietlyAsync(source, id, AntigravityAcpRules.BuildPermissionCancelledOutcome());
            return;
        }

        var toolCall = parameters["toolCall"] as JObject;
        bool autoAllow = false;
        List<AgentEvent> events;
        AntigravityStepRules.ToolDescription desc;
        IReadOnlyList<AgentDiffLine>? diff;
        var key = RequestKeyFor(source, id);

        lock (gate)
        {
            (events, desc, diff) = parser.AnnounceForPermission(toolCall);
            if (currentMode == AgentPermissionMode.FullAccess || sessionAllowedKinds.Contains(request.Kind))
                autoAllow = true;
            else
                pendingRequests[key] = new PendingRequest(id, PendingKind.Permission, source, Permission: request);
        }

        foreach (var ev in events)
            Raise(ev);

        if (autoAllow)
        {
            _ = RespondQuietlyAsync(source, id, AntigravityAcpRules.BuildPermissionOutcome(AgentApprovalChoice.Allow, request));
            return;
        }

        Raise(new AgentApprovalEvent(
            key,
            desc.Name,
            desc.Kind,
            desc.Title,
            desc.Detail ?? request.Detail,
            string.IsNullOrEmpty(request.ToolCallId) ? null : request.ToolCallId,
            CanAllowForSession: true,
            IsPlan: false,
            desc.FilePath,
            diff));
        PublishSafePoint();
    }

    private void HandleElicitation(AgentProcess source, JToken id, JObject parameters)
    {
        var questions = AntigravityAcpRules.ParseElicitationQuestions(parameters);
        if (questions.Count == 0)
        {
            _ = RespondQuietlyAsync(source, id, AntigravityAcpRules.BuildElicitationDecline());
            return;
        }
        var key = RequestKeyFor(source, id);
        lock (gate)
            pendingRequests[key] = new PendingRequest(id, PendingKind.Question, source, Questions: questions);
        Raise(new AgentQuestionEvent(key, questions, AnswerAsMessage: false));
        PublishSafePoint();
    }

    public async Task RespondApprovalAsync(string requestKey, AgentApprovalChoice choice, string? feedback)
    {
        var request = TakePending(requestKey, PendingKind.Permission);
        if (request?.Permission == null)
            return;
        if (choice == AgentApprovalChoice.AllowForSession)
        {
            lock (gate)
                sessionAllowedKinds.Add(request.Permission.Kind);
        }
        await RespondQuietlyAsync(request.Process, request.Id, AntigravityAcpRules.BuildPermissionOutcome(choice, request.Permission));
        PublishSafePoint();
    }

    public async Task AnswerQuestionAsync(string requestKey, IReadOnlyDictionary<string, IReadOnlyList<string>> answers, IReadOnlyDictionary<string, string>? notes = null)
    {
        var request = TakePending(requestKey, PendingKind.Question);
        if (request?.Questions == null)
            return;
        await RespondQuietlyAsync(request.Process, request.Id, AntigravityAcpRules.BuildElicitationAccept(request.Questions, answers, notes));
        PublishSafePoint();
    }

    public async Task DeclineQuestionAsync(string requestKey)
    {
        var request = TakePending(requestKey, PendingKind.Question);
        if (request == null)
            return;
        await RespondQuietlyAsync(request.Process, request.Id, AntigravityAcpRules.BuildElicitationDecline());
        PublishSafePoint();
    }

    public Task RespondPlanAsync(string requestKey, AgentPlanChoice choice, string? feedback) => Task.CompletedTask;

    private void OnResponse(AgentProcess source, GrokAcpMessage message)
    {
        if (message.Id is not { Type: JTokenType.Integer } id)
            return;
        var key = id.Value<long>();
        bool isPrompt;
        lock (gate)
            isPrompt = promptRequestId == key && ReferenceEquals(promptProcess, source);
        if (isPrompt)
            CompleteTurn(source, message);
    }

    private void CompleteTurn(AgentProcess source, GrokAcpMessage message)
    {
        var stopReason = (message.Result as JObject)?.Value<string>("stopReason");
        List<AgentEvent> events;
        AgentTurnCompletedEvent? completed = null;
        AgentQuotaExhaustedEvent? quotaEvent = null;
        AgentProcess? recycleOnNetworkError = null;
        int droppedBackground = 0;
        List<PendingRequest> leftovers;

        lock (gate)
        {
            if (!running || !ReferenceEquals(promptProcess, source))
                return;
            promptRequestId = null;
            promptProcess = null;
            leftovers = TakePendingForLocked(source);

            bool stoppedByRestart = false;
            if (restart is { } op && ReferenceEquals(op.Stopping, source))
            {
                op.TurnStopped.TrySetResult();
                stoppedByRestart = message.Error != null || string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase);
                if (!stoppedByRestart)
                    op.TurnFinishedOnItsOwn = true;
            }

            if (stoppedByRestart)
            {
                events = parser.FinishTextOnly();
            }
            else
            {
                var wasInterrupted = interruptRequested;
                running = false;
                interruptRequested = false;
                cancelSent = false;
                if (restart is { ContinuationStarted: true } continuing)
                    continuing.ContinuationReported = true;

                if (message.Error != null)
                {
                    events = parser.FinishTurn(AgentTurnOutcome.Failed);
                    var rawErr = message.Error.Text;
                    if (wasInterrupted)
                    {
                        completed = new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null);
                    }
                    else if (AntigravityAcpRules.IsAuthRequired(message.Error))
                    {
                        completed = new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, AntigravityStreamRules.NotSignedInMessage, null);
                    }
                    else
                    {
                        if (AntigravityStreamRules.IsQuotaExhausted(rawErr))
                            quotaEvent = new AgentQuotaExhaustedEvent(AgentProvider.Antigravity, null);
                        var busy = AntigravityStreamRules.ClassifyServerBusy(rawErr);
                        completed = new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, rawErr, null) { ServerBusy = busy };
                        if (busy == AgentServerBusy.NetworkError && !parser.HasLiveBackgroundWork && restart == null)
                        {
                            recycleOnNetworkError = source;
                            droppedBackground = DetachProcessLocked();
                        }
                    }
                }
                else
                {
                    var (outcome, err) = AntigravityAcpRules.EvaluateStopReason(stopReason);
                    if (wasInterrupted && outcome == AgentTurnOutcome.Completed && string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase))
                        outcome = AgentTurnOutcome.Interrupted;
                    events = parser.FinishTurn(outcome);
                    var turnUsage = AntigravityAcpRules.ParsePromptUsage(message.Result, out var cacheWrite);
                    if (turnUsage != null)
                        events.Add(new AgentTokenUsageEvent(turnUsage, currentTokenModel ?? defaultModelId, CacheWriteTokens: cacheWrite));
                    completed = new AgentTurnCompletedEvent(outcome, err, turnUsage);
                }
            }
        }

        RaiseBackgroundCleared(droppedBackground);
        foreach (var req in leftovers)
            _ = CancelRequestQuietlyAsync(req);
        foreach (var ev in events)
            Raise(ev);
        if (quotaEvent != null)
            Raise(quotaEvent);
        if (completed != null)
            Raise(completed);
        PublishSafePoint();

        if (recycleOnNetworkError != null)
            _ = Task.Run(() => RecycleBrokenProcessAsync(recycleOnNetworkError));
    }

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
            Log.Warn($"Antigravity ACP 回收網路連線中斷的舊程序失敗：{ex.Message}");
        }
        finally
        {
            Raise(new AgentProcessExitedEvent(null, stale.GetStderrTail(), Expected: true, stale.InstanceId));
            stale.Dispose();
        }
    }

    // ───────────────────────────── 停止、程序結束、關閉與熱更新 RestartAsync ─────────────────────────────

    public async Task InterruptAsync()
    {
        List<PendingRequest> pending;
        AgentProcess? target;
        string? sid;
        bool sendNow;
        long? prompt;
        CancellationTokenSource? preparation;
        lock (gate)
        {
            suspendedSession = null;
            if (restart is { ContinuationStarted: false } op)
            {
                op.Cancelled = true;
                if (op.TookOver)
                    return;
            }
            if (!running)
                return;
            interruptRequested = true;
            pending = TakePendingForLocked(null);
            target = promptProcess;
            prompt = promptRequestId;
            sid = processSessionId;
            sendNow = target != null && prompt != null && sid != null && promptWritten && !cancelSent;
            if (sendNow)
                cancelSent = true;
            preparation = prompt == null ? preparing : null;
        }

        try
        {
            preparation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        foreach (var req in pending)
            await CancelRequestQuietlyAsync(req);
        PublishSafePoint();
        if (!sendNow || target == null)
            return;

        await WriteQuietlyAsync(target, GrokAcpRules.BuildNotification("session/cancel", AntigravityAcpRules.BuildCancelParams(sid!)));
        _ = KillIfStuckAsync(target, prompt!.Value);
    }

    private async Task AfterPromptWrittenAsync(AgentProcess target, long id, string sid)
    {
        bool sendCancel;
        lock (gate)
        {
            if (promptRequestId != id || !ReferenceEquals(promptProcess, target))
                return;
            promptWritten = true;
            sendCancel = running && interruptRequested && !cancelSent;
            if (sendCancel)
                cancelSent = true;
        }
        if (!sendCancel)
            return;
        await WriteQuietlyAsync(target, GrokAcpRules.BuildNotification("session/cancel", AntigravityAcpRules.BuildCancelParams(sid))).ConfigureAwait(false);
        _ = KillIfStuckAsync(target, id);
    }

    private async Task KillIfStuckAsync(AgentProcess target, long prompt)
    {
        await Task.WhenAny(target.ExitTask, Task.Delay(CancelTimeout)).ConfigureAwait(false);
        bool stuck;
        lock (gate)
            stuck = running && promptRequestId == prompt && ReferenceEquals(promptProcess, target);
        if (stuck)
        {
            Log.Warn($"Antigravity ACP 在 {CancelTimeout.TotalSeconds:0.#} 秒內沒有回應停止，結束程序樹");
            target.Kill();
        }
    }

    private void OnExited(AgentProcess source, int? exitCode)
    {
        RestartOperation? stoppedForRestart = null;
        bool wasRunning = false;
        bool wasInterrupted = false;
        bool ownedByRestart;
        List<AgentEvent> events = new();
        GrokAcpConnection? conn = null;
        int droppedBackground = 0;

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
                conn = connection;
                droppedBackground = DetachProcessLocked();
            }
            TakePendingForLocked(source);

            ownedByRestart = stoppedForRestart != null || restart is { TookOver: true, ContinuationStarted: false };
            if (!ownedByRestart && running && ReferenceEquals(promptProcess, source))
            {
                wasRunning = true;
                wasInterrupted = interruptRequested;
                running = false;
                interruptRequested = false;
                cancelSent = false;
                promptRequestId = null;
                promptProcess = null;
                events = parser.FinishTurn(
                    wasInterrupted ? AgentTurnOutcome.Interrupted : AgentTurnOutcome.Failed,
                    AgentItemStatus.Cancelled,
                    wasInterrupted ? AntigravityAcpRules.CancelledToolOutput : AntigravityStreamRules.InterruptedToolOutput);
                if (restart is { ContinuationStarted: true } continuing)
                    continuing.ContinuationReported = true;
            }
        }

        conn?.FailAll("Antigravity 程序已結束");
        RaiseBackgroundCleared(droppedBackground);

        var tail = source.GetStderrTail();
        foreach (var ev in events)
            Raise(ev);
        if (wasRunning)
        {
            Raise(wasInterrupted
                ? new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null)
                : new AgentTurnCompletedEvent(
                    AgentTurnOutcome.Failed,
                    $"agy 程序意外結束（結束碼 {exitCode?.ToString() ?? "不明"}）{(string.IsNullOrWhiteSpace(tail) ? string.Empty : "：" + tail)}",
                    null) { ProcessExited = true });
        }

        Raise(new AgentProcessExitedEvent(exitCode, tail, Expected: ownedByRestart || !wasRunning || wasInterrupted, source.InstanceId));
        PublishSafePoint();
        stoppedForRestart?.ExitHandled.TrySetResult();
        source.Dispose();
    }

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
            Log.Error("Antigravity ACP 以新版重開失敗", ex);
            AgentProcess? leftover;
            lock (gate)
            {
                running = false;
                promptRequestId = null;
                promptProcess = null;
                suspendedSession = null;
                op.Stopping = null;
                leftover = process;
            }
            leftover?.Kill();
            return new AgentRestartResult(AgentRestartOutcome.Failed, op.PreviousInstance, Error: ex.Message);
        }
        finally
        {
            bool clearTurn;
            lock (gate)
            {
                clearTurn = op.TookOver && (!op.ContinuationStarted || op.Cancelled) && !op.ContinuationReported && running;
                if (clearTurn)
                {
                    running = false;
                    interruptRequested = false;
                }
                restart = null;
            }
            if (clearTurn && op.Cancelled)
                Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null));
            PublishSafePoint();
        }
    }

    private async Task<AgentRestartResult> RestartCoreAsync(AgentRestartRequest request, RestartOperation op)
    {
        AgentProcess? old;
        string? oldSid;
        long? oldPrompt;
        bool wasRunning;
        string? resumeId;
        Task? prepWait;
        CancellationTokenSource? prepCancel = null;
        List<PendingRequest> cancelledRequests;
        int droppedBackground = 0;

        lock (gate)
        {
            if (request.Mode == AgentRestartMode.SafePoint && !CurrentSafePointLocked().IsAtSafePoint)
                return new AgentRestartResult(AgentRestartOutcome.Deferred);

            old = process;
            oldSid = processSessionId;
            oldPrompt = promptRequestId;
            wasRunning = running;
            resumeId = sessionId ?? suspendedSession ?? (request.Settings is { SessionStarted: true } s ? s.SessionId : null);
            if (old == null && suspendedSession == null && !wasRunning)
                return new AgentRestartResult(AgentRestartOutcome.Restarted);

            op.PreviousInstance = old?.InstanceId;
            op.Stopping = old;
            op.TookOver = wasRunning || suspendedSession != null;
            prepWait = preparationDone?.Task;
            if (wasRunning && oldPrompt == null)
                prepCancel = preparing;
            cancelledRequests = TakePendingForLocked(old);
            if (old != null)
                droppedBackground = DetachProcessLocked();
            running = false;
            promptRequestId = null;
            promptProcess = null;
        }

        try
        {
            prepCancel?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        if (prepWait != null)
            await Task.WhenAny(prepWait, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        RaiseBackgroundCleared(droppedBackground);
        foreach (var req in cancelledRequests)
            await CancelRequestQuietlyAsync(req).ConfigureAwait(false);

        if (old != null)
        {
            var tree = old.CaptureTree();
            if (wasRunning && oldPrompt != null && !string.IsNullOrEmpty(oldSid) && !old.HasExited)
            {
                await WriteQuietlyAsync(old, GrokAcpRules.BuildNotification("session/cancel", AntigravityAcpRules.BuildCancelParams(oldSid))).ConfigureAwait(false);
                await Task.WhenAny(op.TurnStopped.Task, old.ExitTask, Task.Delay(RestartInterruptTimeout)).ConfigureAwait(false);
            }
            await old.CloseAsync(RestartCloseGrace).ConfigureAwait(false);
            if (!await tree.WaitForExitAsync(RestartTreeExitTimeout).ConfigureAwait(false))
                tree.KillAlive();
            await Task.WhenAny(op.ExitHandled.Task, Task.Delay(ExitHandledTimeout)).ConfigureAwait(false);
            old.Dispose();
        }

        if (request.Settings == null)
        {
            lock (gate)
            {
                if (op.TookOver && !op.TurnFinishedOnItsOwn && !op.Cancelled)
                    suspendedSession = resumeId ?? string.Empty;
            }
            return new AgentRestartResult(AgentRestartOutcome.Stopped, op.PreviousInstance);
        }

        bool shouldContinue = op.TookOver && !op.TurnFinishedOnItsOwn && !op.Cancelled && request.AutoContinue
                              && !string.IsNullOrWhiteSpace(request.EffectiveContinuationText);
        lock (gate)
            suspendedSession = null;

        if (!shouldContinue)
            return new AgentRestartResult(AgentRestartOutcome.Restarted, op.PreviousInstance);

        var settings = !string.IsNullOrWhiteSpace(resumeId)
            ? request.Settings with { SessionId = resumeId, SessionStarted = true }
            : request.Settings;

        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(resumeId))
                sessionId = resumeId;
            running = true;
            interruptRequested = false;
            cancelSent = false;
            op.ContinuationStarted = true;
        }

        var fresh = await StartProcessAsync(settings, CancellationToken.None).ConfigureAwait(false);
        GrokAcpConnection conn;
        lock (gate)
            conn = connection ?? throw new InvalidOperationException("Antigravity 新程序啟動後立即結束");

        var (sid, createdNew, lostHistory) = await OpenSessionAsync(conn, settings, CancellationToken.None).ConfigureAwait(false);
        await ApplyConfigAsync(conn, sid, settings, CancellationToken.None).ConfigureAwait(false);

        Raise(new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));

        var promptParams = AntigravityAcpRules.BuildPromptParams(
            sid,
            createdNew ? settings.InstructionsWith(sharedLaunch) : null,
            lostHistory ? settings.HistoryRecap : null,
            request.EffectiveContinuationText,
            Array.Empty<AgentAttachment>(),
            supportsInlineImages: false);

        long id;
        lock (gate)
        {
            lastWorkingDirectory = settings.WorkingDirectory;
            currentMode = settings.PermissionMode;
            parser.BeginTurn(planMode: settings.PermissionMode == AgentPermissionMode.Plan);
            id = conn.ReserveId();
            promptRequestId = id;
            promptProcess = fresh;
            promptWritten = false;
        }

        await conn.SendRequestAsync(id, "session/prompt", promptParams).ConfigureAwait(false);
        await AfterPromptWrittenAsync(fresh, id, sid).ConfigureAwait(false);
        return new AgentRestartResult(AgentRestartOutcome.RestartedAndContinued, op.PreviousInstance);
    }

    public async Task<AgentBackgroundStopResult> StopBackgroundWorkAsync(string? stopId)
    {
        if (stopId != null)
            return new AgentBackgroundStopResult(0, "Antigravity 的背景工作只能全部停止");
        AgentProcess target;
        string? sid;
        int count;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited)
                return new AgentBackgroundStopResult(0, "Antigravity 程序已經結束，背景工作也跟著結束了");
            if (running || restart != null)
                return new AgentBackgroundStopResult(0, "代理還在處理這一輪：先按停止，再停止背景工作");
            if (!parser.HasLiveBackgroundWork)
                return new AgentBackgroundStopResult(0, "背景工作已經結束了");
            target = process;
            sid = processSessionId;
            count = Math.Max(1, parser.LiveBackgroundTools.Count);
            DetachProcessLocked();
        }
        RaiseBackgroundCleared(count);
        var tree = await Task.Run(target.CaptureTree).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(sid) && !target.HasExited)
            await WriteQuietlyAsync(target, GrokAcpRules.BuildNotification("session/cancel", AntigravityAcpRules.BuildCancelParams(sid))).ConfigureAwait(false);
        await target.CloseAsync(CloseGrace).ConfigureAwait(false);
        if (!await tree.WaitForExitAsync(RestartTreeExitTimeout).ConfigureAwait(false))
            tree.KillAlive();
        Raise(new AgentProcessExitedEvent(null, target.GetStderrTail(), Expected: true, target.InstanceId));
        target.Dispose();
        PublishSafePoint();
        return new AgentBackgroundStopResult(count, null);
    }

    public async Task CloseAsync()
    {
        AgentProcess? target;
        List<PendingRequest> pending;
        int droppedBackground;
        lock (gate)
        {
            target = process;
            pending = TakePendingForLocked(target);
            droppedBackground = DetachProcessLocked();
            running = false;
            promptRequestId = null;
            promptProcess = null;
        }
        RaiseBackgroundCleared(droppedBackground);
        foreach (var req in pending)
            await CancelRequestQuietlyAsync(req);
        if (target != null)
        {
            await target.CloseAsync(CloseGrace);
            target.Dispose();
        }
        PublishSafePoint();
    }

    public void Dispose()
    {
        AgentProcess? target;
        CancellationTokenSource? prep;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            prep = preparing;
            target = process;
            DetachProcessLocked();
            pendingRequests.Clear();
            running = false;
            promptRequestId = null;
            promptProcess = null;
        }
        try
        {
            prep?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        target?.Dispose();
    }

    // ───────────────────────────── 內部輔助 ─────────────────────────────

    private void ResetAppliedLocked()
    {
        appliedModel = null;
        modelKnown = false;
        appliedEffort = null;
        appliedModeId = null;
        configOptions = Array.Empty<AntigravityAcpConfigOption>();
    }

    private int DetachProcessLocked()
    {
        int had = parser.ClearBackgroundTasks();
        promptImages = true;
        process = null;
        processHost = null;
        connection = null;
        processSessionId = null;
        processCli = null;
        processSignature = null;
        runningVersion = null;
        ResetAppliedLocked();
        return had;
    }

    private void RaiseBackgroundCleared(int had)
    {
        if (had > 0)
            Raise(new AgentBackgroundWorkEvent(0, Array.Empty<AgentBackgroundTool>()));
    }

    private bool IsInterruptRequested()
    {
        lock (gate)
            return interruptRequested;
    }

    private bool IsTurnHandledElsewhere()
    {
        lock (gate)
            return !running;
    }

    private void ThrowIfDisposed()
    {
        lock (gate)
            ObjectDisposedException.ThrowIf(disposed, this);
    }

    private bool TryAbortInterrupted()
    {
        lock (gate)
        {
            if (!interruptRequested || promptRequestId != null)
                return false;
            running = false;
            interruptRequested = false;
            cancelSent = false;
        }
        Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null));
        return true;
    }

    private PendingRequest? TakePending(string requestKey, PendingKind kind)
    {
        lock (gate)
        {
            if (!pendingRequests.TryGetValue(requestKey, out var req) || req.Kind != kind)
                return null;
            pendingRequests.Remove(requestKey);
            return ReferenceEquals(req.Process, process) ? req : null;
        }
    }

    private List<PendingRequest> TakePendingForLocked(AgentProcess? source)
    {
        var taken = pendingRequests.Where(p => source == null || ReferenceEquals(p.Value.Process, source)).ToList();
        foreach (var (k, _) in taken)
            pendingRequests.Remove(k);
        return taken.Select(p => p.Value).ToList();
    }

    private static JObject CancelledResult(string? method) => method switch
    {
        "elicitation/create" => AntigravityAcpRules.BuildElicitationDecline(cancelled: true),
        _ => AntigravityAcpRules.BuildPermissionCancelledOutcome(),
    };

    private static Task CancelRequestQuietlyAsync(PendingRequest req)
        => RespondQuietlyAsync(req.Process, req.Id, req.Kind switch
        {
            PendingKind.Question => AntigravityAcpRules.BuildElicitationDecline(cancelled: true),
            _ => AntigravityAcpRules.BuildPermissionCancelledOutcome(),
        });

    private static Task RespondQuietlyAsync(AgentProcess target, JToken id, JToken result)
        => WriteQuietlyAsync(target, GrokAcpRules.BuildResult(id, result));

    private static async Task WriteQuietlyAsync(AgentProcess target, string line)
    {
        try
        {
            await target.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static string RequestKeyFor(AgentProcess source, JToken id)
        => $"{source.InstanceId[..8]}:{GrokAcpRules.RequestKey(id)}";

    private AgentSafePointChangedEvent CurrentSafePointLocked()
        => new(
            ToolRunning: running && parser.HasRunningTools,
            WaitingForUser: pendingRequests.Count > 0,
            HasLiveBackgroundWork: parser.HasLiveBackgroundWork);

    private void PublishSafePoint()
    {
        lock (safePointOrder)
        {
            AgentSafePointChangedEvent? changed = null;
            lock (gate)
            {
                if (disposed)
                    return;
                var next = CurrentSafePointLocked();
                if (next != reportedSafePoint)
                    changed = reportedSafePoint = next;
            }
            if (changed != null)
                Raise(changed);
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
            Debug.WriteLine($"[AntigravityAcpSession] 事件處理失敗：{ex}");
        }
    }
}
