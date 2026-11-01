using System.Diagnostics;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// Grok Build 代理：一個對話一個 grok agent --no-leader stdio 程序，講 ACP（JSON-RPC 2.0、一行一則）。協定實測見 docs/protocol-notes.md。
/// <list type="bullet">
/// <item>對話：第一則訊息 session/new（sessionId 由 app 預先產生）；程序重開一律 session/resume（不重播，畫面已有內容），
/// 找不到對話（FS_NOT_FOUND）時改開新對話並附上前文節錄</item>
/// <item>一輪：session/prompt 要等到這一輪結束才回應（stopReason＋_meta.usage），所以送出後不等回應，由回應收尾</item>
/// <item>模型、思考強度（session/set_config_option）與權限模式（_x.ai/yolo_mode_changed、session/set_mode）在同一個程序裡即時切換；
/// 帳戶、執行檔、工作資料夾改變時下一輪重開程序（換工作資料夾時在訊息開頭告知代理）</item>
/// <item>核准（session/request_permission）、提問（x.ai/ask_user_question）、計畫確認（x.ai/exit_plan_mode）畫成卡片；
/// 「自動接受編輯」grok 沒有對應模式，由會話代為核准工作資料夾內的編輯</item>
/// <item>停止：session/cancel（通知），prompt 回 cancelled；grok 不再送執行中工具的結束更新，由會話結束成「已停止」。等不到回應就結束程序樹</item>
/// <item>CLI 換版的熱重開：這一輪在執行就 session/cancel、等回應，關閉並確認程序樹都已結束，以新版 session/resume 後送接續訊息（與 Codex 同一套流程表）</item>
/// </list>
/// 執行中旗標從 SendAsync 一開始就成立：啟動程序、建立對話的期間按停止，送出前會放棄。
/// </summary>
internal sealed class GrokAgentSession : IAgentSession
{
    private const string SessionLostNotice = "grok 找不到這個對話先前的紀錄，已改開新對話並附上前文節錄";

    /// <summary>正常關閉（關 stdin）等 grok 收尾的上限</summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    /// <summary>initialize、session/new、session/resume 的時限（建立對話要掃工作資料夾、載入外掛）</summary>
    private static readonly TimeSpan SessionRequestTimeout = TimeSpan.FromSeconds(90);

    /// <summary>切換模型、思考強度、模式的時限</summary>
    private static readonly TimeSpan ConfigRequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>重開時舊程序的結束事件最多等多久（stdout 被別的程序握著時不會觸發）</summary>
    private static readonly TimeSpan ExitHandledTimeout = TimeSpan.FromSeconds(5);

    /// <summary>重開失敗時剛啟動的新程序最多等它結束多久</summary>
    private static readonly TimeSpan FailedProcessExitTimeout = TimeSpan.FromSeconds(2);

    /// <summary>停止後等 prompt 回應的上限，逾時就結束程序樹（測試可調短）</summary>
    internal TimeSpan CancelTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>這一輪結束後查 _x.ai/billing 的上限。查詢在讀取執行緒之外，逾時仍要回報這一輪結束</summary>
    internal TimeSpan BillingCheckTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>一則訊息直接附上的圖片上限（同 Claude）</summary>
    private const int MaxImagesPerMessage = 20;

    private const long MaxImageBytesPerMessage = 20L * 1024 * 1024;

    /// <summary>重開時送出 session/cancel 後等這一輪結束的上限</summary>
    internal TimeSpan RestartInterruptTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>重開時關 stdin 後等程序自己結束的上限，逾時結束程序樹</summary>
    internal TimeSpan RestartCloseGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>重開時等程序樹（含 grok 開的指令）都結束的上限，逾時逐一結束</summary>
    internal TimeSpan RestartTreeExitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    private readonly object gate = new();
    private readonly object safePointOrder = new();
    private AgentProcess? process;
    private GrokAcpConnection? connection;
    private GrokUpdateParser parser = new();
    private string? processSignature;
    private CliBinaryIdentity? processCli;
    private string? runningVersion;

    /// <summary>目前程序上已建立或續接好的 grok 對話（null＝這個程序還沒有）</summary>
    private string? processSessionId;

    /// <summary>這個對話在 grok 的 id（建立或續接成功後記下；中樞記的是 settings.SessionId）</summary>
    private string? sessionId;

    /// <summary>上一則送出時的工作資料夾（換了就在訊息開頭告知代理）</summary>
    private string? lastWorkingDirectory;

    /// <summary>grok 回報的模型清單（判斷思考強度能不能送）與預設模型</summary>
    private IReadOnlyList<AgentModelOption>? models;
    private string? defaultModelId;

    /// <summary>上下文提示：模型清單裡各模型的視窗（_meta.totalContextTokens）與 grok 目前用的模型（清單的 currentModelId、切換成功的模型）</summary>
    private readonly Dictionary<string, long> contextWindows = new(StringComparer.Ordinal);

    /// <summary>各模型回報的可選視窗（_meta.contextWindows）。ACP 不能切換，只給 /context-window 的說明</summary>
    private readonly Dictionary<string, long[]> contextChoices = new(StringComparer.Ordinal);
    private string? contextModel;

    /// <summary>沒有 modelUsage 的舊版 grok：這一輪的 token 記在目前的模型（切換成功的、或 grok 回報的目前模型）</summary>
    private string? CurrentTokenModel() => contextModel ?? appliedModel ?? defaultModelId;

    /// <summary>這次 initialize 的 promptCapabilities.image。程序換掉就清掉，等新的 initialize</summary>
    private bool promptImages;

    /// <summary>這一輪送的是 _x.ai/compact_conversation（成功才發壓縮提示，不把回應的 token 當成上下文）</summary>
    private bool promptIsCompact;

    /// <summary>手動壓縮進行中已經看過 auto_compact_completed（那則通知不另發提示，由這一輪的回應發）</summary>
    private bool autoCompactSeenDuringManual;

    /// <summary>手動壓縮的回應先到、通知還沒到：下一則 auto_compact_completed 不再發第二次提示</summary>
    private bool suppressLateAutoCompact;

    /// <summary>正在為分叉接上程序（不是一輪：不設 running，程序自己結束也不回報失敗）；這期間的送出等它做完再開始</summary>
    private TaskCompletionSource? attachDone;

    /// <summary>
    /// 分叉接上程序時開了新的 grok 對話（續接失敗或從來沒建立過）：那次 session/new 的結果留給這個程序上的下一次送出，
    /// 第一則才會帶 Myriagent 的指示與前文節錄（不然 sid 已經有了，送出時當成續接中的對話）
    /// </summary>
    private (AgentProcess Process, bool CreatedNew, bool LostHistory)? attachedFresh;

    /// <summary>還在跑的背景工作（task id → 工具卡與本機輸出檔）。一輪結束不清；程序換掉或結束才清，並發數量 0</summary>
    private readonly Dictionary<string, BackgroundWork> background = new(StringComparer.Ordinal);

    /// <summary>一筆背景工作。OutputFile 只留本機磁碟路徑</summary>
    private sealed class BackgroundWork
    {
        public BackgroundWork(string? toolCallId, AgentBackgroundTool tool, string? outputFile)
        {
            ToolCallId = toolCallId;
            Tool = tool;
            OutputFile = outputFile;
        }

        public string? ToolCallId { get; }

        public AgentBackgroundTool Tool { get; }

        public string? OutputFile { get; set; }
    }

    /// <summary>目前程序已套用的設定（null＝不知道，下次一定送）</summary>
    private string? appliedModel;
    private bool modelKnown;
    private string? appliedEffort;
    private AgentPermissionMode? appliedPermission;
    private bool? appliedPlan;

    /// <summary>這一輪的權限模式與資料夾（自動接受編輯要用）</summary>
    private AgentPermissionMode currentMode = AgentPermissionMode.Ask;
    private string? currentWorkingDirectory;
    private string? currentConversationDirectory;

    private bool running;
    private bool interruptRequested;
    private bool cancelSent;

    /// <summary>這一輪的 session/prompt 已寫出（之前按停止只記下，寫出後補送取消：先送的取消 grok 沒有進行中的一輪會忽略）</summary>
    private bool promptWritten;
    private long? promptRequestId;
    private AgentProcess? promptProcess;
    private CancellationTokenSource? preparing;
    private TaskCompletionSource? preparationDone;
    private readonly Dictionary<string, PendingRequest> pendingRequests = new(StringComparer.Ordinal);

    /// <summary>使用者選過「本對話都允許」的操作種類（edit、execute）：程序重開後 grok 不一定記得，由會話代為核准</summary>
    private readonly HashSet<string> sessionAllowedKinds = new(StringComparer.Ordinal);

    /// <summary>重開時停下待接續的對話（Settings=null 的第一段停下、之後再帶 Settings 呼叫一次）</summary>
    private string? suspendedSession;

    private RestartOperation? restart;
    private AgentSafePointChangedEvent? reportedSafePoint;
    private bool disposed;

    private enum PendingKind
    {
        Permission,
        Question,
        Plan,
    }

    private sealed record PendingRequest(JToken Id, PendingKind Kind, AgentProcess Process, GrokPermissionRequest? Permission = null, IReadOnlyList<AgentQuestion>? Questions = null);

    public event Action<AgentEvent>? EventReceived;

    public AgentProvider Provider => AgentProvider.Grok;

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

    /// <summary>目前程序的 CLI 版本（initialize 回應的 agentVersion，沒有就用啟動時的執行檔身分）</summary>
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
                return background.Count > 0;
        }
    }

    public IReadOnlyList<AgentBackgroundTool> LiveBackgroundTools
    {
        get
        {
            lock (gate)
                return background.Values.Select(v => v.Tool).ToArray();
        }
    }

    /// <summary>帳戶環境之後加上設定頁的自動壓縮門檻（AutoCompactRules）：蓋過伺服器給模型的門檻與 config 的設定；沒設定時照 CLI 預設</summary>
    internal static void ApplyLaunchEnvironment(IDictionary<string, string?> env, AgentSessionSettings settings)
    {
        settings.ConfigureEnvironment(env);
        if (settings.AutoCompactPercent is { } percent)
            env[AutoCompactRules.GrokPercentVariable] = percent.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static string Signature(AgentSessionSettings settings)
        => GrokAcpRules.Signature(settings.AccountKey, settings.Launcher?.FileName, settings.WorkingDirectory, settings.ConversationDirectory, settings.UseLocalCustomizations)
           + "|" + settings.AutoCompactPercent;

    // ───────────────────────────── 送出 ─────────────────────────────

    public async Task SendAsync(AgentTurnInput input, AgentSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);
        // 分叉正在接上這個對話的程序（通常不到一秒）：等它做完再送，同一個程序與對話接著用
        while (true)
        {
            Task? attachWait;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                attachWait = attachDone?.Task;
            }
            if (attachWait == null)
                break;
            await attachWait.ConfigureAwait(false);
        }

        var preparation = new CancellationTokenSource();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (attachDone != null)
            {
                preparation.Dispose();
                throw new InvalidOperationException("正在準備 Grok 對話，請稍後再送出");
            }
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
            suppressLateAutoCompact = false;
            interruptRequested = false;
            cancelSent = false;
            suspendedSession = null;
            preparing = preparation;
            preparationDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        try
        {
            await SendCoreAsync(input, settings, preparation.Token);
        }
        finally
        {
            TaskCompletionSource? done;
            lock (gate)
            {
                if (ReferenceEquals(preparing, preparation))
                    preparing = null;
                done = preparationDone;
                preparationDone = null;
            }
            done?.TrySetResult();
            preparation.Dispose();
            PublishSafePoint();
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
                conn = connection ?? throw new InvalidOperationException("Grok 程序已結束");
                sid = processSessionId;
            }
            bool createdNew = false, lostHistory = false;
            if (sid == null)
            {
                (sid, createdNew, lostHistory) = await OpenSessionAsync(conn, settings, ct);
            }
            else
            {
                lock (gate)
                {
                    if (attachedFresh is { } fresh && ReferenceEquals(fresh.Process, current))
                        (createdNew, lostHistory) = (fresh.CreatedNew, fresh.LostHistory);
                    attachedFresh = null;
                }
            }
            if (TryAbortInterrupted())
                return;
            await ApplyConfigAsync(conn, sid, settings, ct);
            if (TryAbortInterrupted())
                return;

            // 斜線指令在套用設定之後看：視窗大小要先從 session/new 的模型清單進來。不當成一般提示送出
            if (GrokAcpRules.TryParseContextWindowCommand(input.Text, out var requestedWindow))
            {
                FinishLocalCommand(DescribeContextWindow(requestedWindow));
                return;
            }
            if (GrokAcpRules.IsCompactCommand(input.Text))
            {
                await SendCompactAsync(conn, current, sid, settings);
                return;
            }

            bool imagesInline;
            lock (gate)
                imagesInline = promptImages;
            var images = ReadPromptImages(input.Attachments, imagesInline, out var skippedImages);

            long id;
            string text;
            lock (gate)
            {
                // 最後一次檢查停止：這裡清掉 preparing 之後，停止改成送 session/cancel
                if (interruptRequested)
                {
                    id = 0;
                    text = string.Empty;
                }
                else
                {
                    var notice = !createdNew && lastWorkingDirectory != null && !GrokAcpRules.SameDirectory(lastWorkingDirectory, settings.WorkingDirectory)
                        ? GrokAcpRules.BuildWorkingDirectoryNotice(settings.WorkingDirectory)
                        : null;
                    text = GrokAcpRules.BuildTurnText(createdNew ? settings.Instructions : null, lostHistory ? settings.HistoryRecap : null, notice, input.Text, input.Attachments, imagesInline);
                    if (skippedImages > 0)
                        text += $"\n（另有 {skippedImages} 張圖片超過單則訊息上限未直接附上，可用 Read 工具讀取上列路徑）";
                    lastWorkingDirectory = settings.WorkingDirectory;
                    currentMode = settings.PermissionMode;
                    currentWorkingDirectory = settings.WorkingDirectory;
                    currentConversationDirectory = settings.ConversationDirectory;
                    parser.BeginTurn();
                    id = conn.ReserveId();
                    promptRequestId = id;
                    promptProcess = current;
                    promptWritten = false;
                    promptIsCompact = false;
                    preparing = null;
                }
            }
            if (id == 0)
            {
                TryAbortInterrupted();
                return;
            }

            await conn.SendRequestAsync(id, "session/prompt", GrokAcpRules.BuildPromptParams(sid, text, images));
            await AfterPromptWrittenAsync(current, id, sid);
        }
        catch (OperationCanceledException) when (IsInterruptRequested())
        {
            TryAbortInterrupted();
        }
        catch (IOException) when (IsTurnHandledElsewhere())
        {
            // 寫入時程序剛好結束：OnExited 已回報這一輪
        }
        catch
        {
            bool stopped;
            lock (gate)
            {
                // OnExited 已經以 TurnCompleted 回報這一輪：不再丟出例外，同一個失敗只回報一次
                if (!running && !disposed)
                    return;
                stopped = interruptRequested && !disposed;
                if (!stopped)
                {
                    running = false;
                    promptRequestId = null;
                    promptProcess = null;
                    promptIsCompact = false;
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

    private bool IsInterruptRequested()
    {
        lock (gate)
            return interruptRequested;
    }

    /// <summary>寫入失敗時：這一輪是不是已經由 OnExited 回報了（running 已清）</summary>
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

    /// <summary>準備期間（訊息還沒送出）按了停止：放棄這一輪並回報已停止</summary>
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

    /// <summary>啟動 grok agent、initialize；在讀它的輸出之前發 AgentProcessStartedEvent</summary>
    private async Task<AgentProcess> StartProcessAsync(AgentSessionSettings settings, CancellationToken ct)
    {
        var launcher = settings.Launcher
            ?? throw new AgentCliUnavailableException(AgentProvider.Grok, "這台電腦尚未安裝 Grok Build CLI，請到「設定 → CLI」安裝");
        // 工作資料夾由中樞負責（預設 workspace 會先建好）；使用者選的資料夾不存在就明確回報，不偷偷重建
        if (!Directory.Exists(settings.WorkingDirectory))
            throw new DirectoryNotFoundException($"工作資料夾不存在：{settings.WorkingDirectory}");
        // 共用設定（Grok 隔離帳戶或跨四家 CLI 共用）：啟動前在背景同步全域指示與連結 skills／rules
        _ = await settings.PrepareSharedAsync();

        var started = await AgentProcess.StartWithRetryAsync(launcher, GrokAcpRules.AgentArguments, settings.WorkingDirectory,
            env => ApplyLaunchEnvironment(env, settings), ct);
        var conn = new GrokAcpConnection(started);
        conn.Response += message => OnResponse(started, message);
        conn.Request += message => OnRequest(started, message);
        conn.Notification += message => OnNotification(started, message);
        started.LineReceived += (_, line) => conn.OnLine(line);
        started.Exited += OnExited;
        lock (gate)
        {
            if (disposed)
            {
                started.Dispose();
                throw new ObjectDisposedException(nameof(GrokAgentSession));
            }
            process = started;
            connection = conn;
            processSignature = Signature(settings);
            processCli = settings.Cli;
            parser = new GrokUpdateParser("grok-" + started.InstanceId[..8]);
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
            var clientVersion = typeof(GrokAgentSession).Assembly.GetName().Version?.ToString() ?? "1.0";
            init = await conn.RequestAsync("initialize", GrokAcpRules.BuildInitializeParams(clientVersion), SessionRequestTimeout, ct);
        }
        catch (OperationCanceledException)
        {
            // 初始化做到一半的程序不能留著重用
            started.Kill();
            throw;
        }
        if (init.Error != null)
        {
            started.Kill();
            throw new InvalidOperationException($"Grok 初始化失敗：{init.Error.Text}");
        }

        var version = GrokAcpRules.ParseAgentVersion(init.Result) ?? settings.Cli?.Version;
        var initialModels = GrokAcpRules.ParseInitializeModels(init.Result);
        var images = GrokAcpRules.ParsePromptImages(init.Result);
        ObserveContextWindows(((init.Result as JObject)?["_meta"] as JObject)?["modelState"]);
        lock (gate)
        {
            if (ReferenceEquals(process, started))
            {
                runningVersion = version;
                promptImages = images;
                if (initialModels.Count > 0)
                    models = initialModels;
            }
        }
        if (version != null)
            Raise(new AgentRunningVersionEvent(started.InstanceId, version));
        return started;
    }

    /// <summary>
    /// 在目前程序上建立或續接 grok 對話：知道 id（中樞記的已建立對話、或這個會話建立過的）就 session/resume，
    /// 找不到對話時改開新對話（附前文節錄）；新對話用中樞預先產生的 id，失敗再換一個新的 id 試一次
    /// </summary>
    private async Task<(string SessionId, bool CreatedNew, bool LostHistory)> OpenSessionAsync(GrokAcpConnection conn, AgentSessionSettings settings, CancellationToken ct)
    {
        string? known;
        lock (gate)
            known = sessionId ?? (settings.SessionStarted ? settings.SessionId : null);
        var mode = settings.PermissionMode;

        bool lost = false;
        if (known != null)
        {
            var (resumed, resumeError) = await conn.RequestAsync("session/resume", GrokAcpRules.BuildResumeParams(known, settings.WorkingDirectory, mode), SessionRequestTimeout, ct);
            if (resumeError == null)
            {
                lock (gate)
                {
                    if (ReferenceEquals(connection, conn))
                    {
                        processSessionId = known;
                        sessionId = known;
                    }
                    // 續接後模型與思考強度沿用對話裡的設定（實測），權限模式與規劃模式不確定：下次一律送
                    appliedPermission = null;
                    appliedPlan = null;
                }
                ObserveContextWindows((resumed as JObject)?["models"]);
                Raise(new AgentSessionStartedEvent(known, null, "resumed"));
                return (known, false, false);
            }
            if (GrokAcpRules.IsAuthRequired(resumeError))
                throw new InvalidOperationException(GrokAcpRules.NotSignedInMessage);
            if (!GrokAcpRules.IsSessionNotFound(resumeError))
                throw new InvalidOperationException($"無法續接 Grok 對話：{resumeError.Text}");
            lost = true;
            Raise(new AgentNoticeEvent(SessionLostNotice, IsError: false));
        }

        var preferred = !lost && !settings.SessionStarted && !string.IsNullOrWhiteSpace(settings.SessionId) ? settings.SessionId! : Guid.NewGuid().ToString();
        var (created, createError) = await conn.RequestAsync("session/new",
            GrokAcpRules.BuildNewSessionParams(preferred, settings.WorkingDirectory, settings.Model, mode), SessionRequestTimeout, ct);
        if (createError != null && !GrokAcpRules.IsAuthRequired(createError) && preferred == settings.SessionId)
        {
            // 預先產生的 id 可能已經被上一次沒做完的建立用掉了：換一個新的 id 再試一次
            Log.Warn($"Grok 以預先產生的 id 建立對話失敗（{createError.Text}），改用新的 id");
            preferred = Guid.NewGuid().ToString();
            (created, createError) = await conn.RequestAsync("session/new",
                GrokAcpRules.BuildNewSessionParams(preferred, settings.WorkingDirectory, settings.Model, mode), SessionRequestTimeout, ct);
        }
        if (createError != null)
        {
            throw new InvalidOperationException(GrokAcpRules.IsAuthRequired(createError)
                ? GrokAcpRules.NotSignedInMessage
                : $"無法建立 Grok 對話：{createError.Text}");
        }

        var result = created as JObject;
        var actual = result?.Value<string>("sessionId") ?? preferred;
        var sessionModels = GrokAcpRules.ParseModels(result?["models"]);
        var currentModel = result?["models"]?.Value<string>("currentModelId");
        lock (gate)
        {
            if (ReferenceEquals(connection, conn))
            {
                processSessionId = actual;
                sessionId = actual;
            }
            if (sessionModels.Count > 0)
                models = sessionModels;
            if (GrokAcpRules.NormalizeModel(settings.Model) == null)
                defaultModelId = currentModel ?? defaultModelId;
            // 建立時已帶上模型（_meta.modelId）與權限（yoloMode／autoMode），規劃模式要另外切
            appliedModel = GrokAcpRules.NormalizeModel(settings.Model);
            modelKnown = true;
            appliedEffort = null;
            appliedPermission = GrokLevel(mode);
            appliedPlan = false;
        }
        if (sessionModels.Count > 0)
            Raise(new AgentModelsEvent(sessionModels));
        ObserveContextWindows(result?["models"]);
        Raise(new AgentSessionStartedEvent(actual, currentModel, "new"));
        return (actual, true, lost);
    }

    /// <summary>模型、思考強度、權限模式和目前程序套用的不同就送（失敗只提示，不擋這一輪）</summary>
    private async Task ApplyConfigAsync(GrokAcpConnection conn, string sid, AgentSessionSettings settings, CancellationToken ct)
    {
        await ApplyModelConfigAsync(conn, sid, settings.Model, settings.Effort, ct);
        await ApplyModeAsync(conn, sid, settings.PermissionMode, ct);
    }

    /// <summary>模型、思考強度和目前程序套用的不同就送（失敗只提示）；都沒有被拒（或本來就相同）回 true</summary>
    private async Task<bool> ApplyModelConfigAsync(GrokAcpConnection conn, string sid, string? requestedModel, string? effort, CancellationToken ct)
    {
        var model = GrokAcpRules.NormalizeModel(requestedModel);
        string? sendModel = null;
        string? sendEffort = null;
        lock (gate)
        {
            if (!modelKnown || !string.Equals(model, appliedModel, StringComparison.Ordinal))
                sendModel = model ?? defaultModelId;   // 換回「預設」：送 grok 的預設模型（不知道就不送）
            // 換模型時一併重送思考強度：不確定 grok 換模型會不會把強度重設成新模型的預設
            if (!string.IsNullOrWhiteSpace(effort) && (sendModel != null || !string.Equals(effort, appliedEffort, StringComparison.OrdinalIgnoreCase))
                && GrokAcpRules.SupportsEffort(models, model, effort))
                sendEffort = effort;
        }

        bool ok = true;
        if (sendModel != null)
        {
            var (_, error) = await conn.RequestAsync("session/set_config_option", GrokAcpRules.BuildSetConfigParams(sid, "model", sendModel), ConfigRequestTimeout, ct);
            if (error != null)
            {
                ok = false;
                Raise(new AgentNoticeEvent($"Grok 無法切換到模型 {sendModel}：{error.Text}", IsError: false));
            }
            else
            {
                ObserveContextModel(sendModel);
            }
        }
        lock (gate)
        {
            appliedModel = model;
            modelKnown = true;
        }
        if (sendEffort != null)
        {
            var (_, error) = await conn.RequestAsync("session/set_config_option", GrokAcpRules.BuildSetConfigParams(sid, "reasoning_effort", sendEffort), ConfigRequestTimeout, ct);
            if (error != null)
            {
                ok = false;
                Raise(new AgentNoticeEvent($"Grok 無法切換思考強度（{sendEffort}）：{error.Text}", IsError: false));
            }
            lock (gate)
                appliedEffort = sendEffort;
        }
        return ok;
    }

    /// <summary>grok 層級的權限：完全自動＝always-approve、自動＝auto、其他都是 ask</summary>
    private static AgentPermissionMode GrokLevel(AgentPermissionMode mode)
        => mode is AgentPermissionMode.FullAccess or AgentPermissionMode.Auto ? mode : AgentPermissionMode.Ask;

    /// <summary>套用權限模式：規劃模式以 session/set_mode 切換，其餘以 _x.ai/yolo_mode_changed 通知（對話中途即時生效）</summary>
    private async Task<bool> ApplyModeAsync(GrokAcpConnection conn, string sid, AgentPermissionMode mode, CancellationToken ct)
    {
        bool plan = mode == AgentPermissionMode.Plan;
        var level = GrokLevel(mode);
        bool sendPlan, sendLevel;
        lock (gate)
        {
            sendPlan = appliedPlan != plan;
            sendLevel = appliedPermission != level;
        }
        bool ok = true;
        if (sendPlan)
        {
            var (_, error) = await conn.RequestAsync("session/set_mode", GrokAcpRules.BuildSetModeParams(sid, mode), ConfigRequestTimeout, ct);
            if (error != null)
            {
                ok = false;
                Raise(new AgentNoticeEvent($"Grok 無法{(plan ? "進入" : "離開")}規劃模式：{error.Text}", IsError: false));
            }
            else
            {
                lock (gate)
                    appliedPlan = plan;
            }
        }
        if (sendLevel)
        {
            await conn.NotifyAsync(GrokAcpRules.MethodPermissionMode, GrokAcpRules.BuildPermissionModeParams(mode));
            lock (gate)
                appliedPermission = level;
        }
        return ok;
    }

    /// <summary>Grok 沒有 Remote Control</summary>
    public bool HasRemoteControl => false;

    public Task SetRemoteControlAsync(AgentRemoteControlOptions? options) => Task.CompletedTask;

    /// <summary>沒有外部看得到的工作階段名稱（只有 Claude 的 Remote Control 有）：不做事</summary>
    public Task RenameAsync(string title) => Task.CompletedTask;

    public Task<bool> ReattachRemoteControlAsync(AgentSessionSettings settings) => Task.FromResult(false);

    /// <summary>ACP 的 session/prompt 在一輪進行中不能再送（沒有併進同一輪的協定）：執行中送出的一律排進佇列，這一輪結束後送</summary>
    public Task<bool> SteerAsync(AgentTurnInput input, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public async Task<bool> SetPermissionModeAsync(AgentPermissionMode mode)
    {
        GrokAcpConnection? conn;
        string? sid;
        lock (gate)
        {
            // 這一輪還沒送出 prompt（準備中，稍後會照這一輪開始時的設定套用）或正在熱重開（新程序照重開時的設定）：
            // 現在套用會被蓋回，回 false 由中樞明說新模式從下一則訊息開始生效
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

    /// <summary>
    /// 執行中換模型與思考強度：session/set_config_option 在 session/prompt 進行中送，grok 立即回應、從下一次模型呼叫起改用
    /// （實測 1.0.67，見 protocol-notes「執行中換模型」）。只在這一輪的 prompt 已送出時送：閒置時下一輪開始前本來就照設定送（ApplyConfigAsync），
    /// 準備中或熱重開時現在送會被蓋回，回 false 由中樞明說下一則訊息才生效；被拒也回 false（另有提示）
    /// </summary>
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

    // ───────────────────────────── 收到的訊息 ─────────────────────────────

    private bool IsCurrent(AgentProcess source)
    {
        lock (gate)
            return ReferenceEquals(source, process) && !(restart is { } op && ReferenceEquals(op.Stopping, source));
    }

    private void OnNotification(AgentProcess source, GrokAcpMessage message)
    {
        if (!IsCurrent(source))
            return;
        switch (message.Method)
        {
            case "session/update" when message.Params["update"] is JObject { } modeUpdate && modeUpdate.Value<string>("sessionUpdate") == "current_mode_update":
                OnModeUpdate(modeUpdate.Value<string>("currentModeId"));
                break;
            case "session/update" when message.Params["update"] is JObject update:
            {
                NoteBackground(update, null);
                List<AgentEvent> events;
                lock (gate)
                    events = parser.HandleUpdate(update);
                foreach (var ev in events)
                    Raise(ev);
                if (events.Count > 0)
                    PublishSafePoint();
                break;
            }
            case "x.ai/task_backgrounded":
            case "x.ai/task_completed":
                NoteBackground(message.Params["update"] as JObject ?? message.Params, message.Method);
                break;
            case "x.ai/models/update":
            {
                ObserveContextWindows(message.Params);
                var updated = GrokAcpRules.ParseModels(message.Params);
                if (updated.Count == 0)
                    break;
                lock (gate)
                    models = updated;
                Raise(new AgentModelsEvent(updated));
                break;
            }
            case "x.ai/session_notification" when message.Params["update"] is JObject { } note && note.Value<string>("sessionUpdate") == "response_completed":
                // 每次模型呼叫結束（實測 1.0.67）：上下文提示的用量
                if (ContextUsageRules.GrokCallTokens(note["usage"] as JObject) is { } used)
                    Raise(new AgentContextUsageEvent(used, null));
                break;
            case "x.ai/session_notification" when message.Params["update"] is JObject { } compact && compact.Value<string>("sessionUpdate") == "auto_compact_completed":
                // 1.0.70 實測：tokens_after 可以比 tokens_before 大，不是剩下的上下文。手動 /compact 也會送這則，那次由回應收尾，這裡不發第二次
                OnAutoCompact();
                break;
        }
    }

    /// <summary>grok 自己壓縮完。不採用通知裡的 token 數。呼叫端不在讀取執行緒上 await 別的請求</summary>
    private void OnAutoCompact()
    {
        bool raise;
        lock (gate)
        {
            if (promptIsCompact)
            {
                autoCompactSeenDuringManual = true;
                raise = false;
            }
            else if (suppressLateAutoCompact)
            {
                suppressLateAutoCompact = false;
                raise = false;
            }
            else
            {
                raise = true;
            }
        }
        if (!raise)
            return;
        Raise(new AgentNoticeEvent(GrokAcpRules.CompactedNotice, IsError: false));
        Raise(new AgentContextUsageEvent(null, null, Compacted: true));
    }

    /// <summary>模型清單（initialize、session/new、session/resume、_x.ai/models/update）帶的各模型視窗：記下來，目前的模型有視窗就發上下文提示</summary>
    private void ObserveContextWindows(JToken? modelState)
    {
        var (current, windows) = ContextUsageRules.ParseGrokWindows(modelState);
        if (current == null && windows.Count == 0)
            return;
        long? window;
        lock (gate)
        {
            foreach (var (id, size) in windows)
                contextWindows[id] = size;
            foreach (var (id, sizes) in ContextUsageRules.ParseGrokWindowChoices(modelState))
                contextChoices[id] = sizes;
            if (current != null)
                contextModel = current;
            window = contextModel != null && contextWindows.TryGetValue(contextModel, out var size2) ? size2 : null;
        }
        if (window != null)
            Raise(new AgentContextUsageEvent(null, window));
    }

    /// <summary>切換模型成功：上下文提示改用新模型的視窗（清單裡有的話）</summary>
    private void ObserveContextModel(string model)
    {
        long? window;
        lock (gate)
        {
            contextModel = model;
            window = contextWindows.TryGetValue(model, out var size) ? size : null;
        }
        if (window != null)
            Raise(new AgentContextUsageEvent(null, window));
    }

    /// <summary>ACP 不能切換視窗：用目前模型的 totalContextTokens 與 contextWindows 組中文說明</summary>
    private string DescribeContextWindow(long? requested)
    {
        string? model;
        long? window;
        long[]? choices;
        lock (gate)
        {
            model = contextModel;
            window = model != null && contextWindows.TryGetValue(model, out var size) ? size : null;
            choices = model != null && contextChoices.TryGetValue(model, out var listed) ? listed : null;
        }
        return GrokAcpRules.DescribeContextWindow(requested, model, window, choices);
    }

    /// <summary>
    /// /context-window 在本機完成：不當成提示送出、不佔 prompt id、不查額度。
    /// SendAsync 的 finally 仍會發安全點
    /// </summary>
    private void FinishLocalCommand(string notice)
    {
        lock (gate)
        {
            running = false;
            interruptRequested = false;
            cancelSent = false;
            promptRequestId = null;
            promptProcess = null;
            promptIsCompact = false;
            preparing = null;
        }
        Raise(new AgentNoticeEvent(notice, IsError: false));
        Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Completed, null, null));
    }

    /// <summary>攔截 /compact：用同一個 prompt id 送 _x.ai/compact_conversation。停止仍走 session/cancel</summary>
    private async Task SendCompactAsync(GrokAcpConnection conn, AgentProcess current, string sid, AgentSessionSettings settings)
    {
        if (TryAbortInterrupted())
            return;

        long id;
        lock (gate)
        {
            if (interruptRequested)
            {
                id = 0;
            }
            else
            {
                currentMode = settings.PermissionMode;
                currentWorkingDirectory = settings.WorkingDirectory;
                currentConversationDirectory = settings.ConversationDirectory;
                parser.BeginTurn();
                id = conn.ReserveId();
                promptRequestId = id;
                promptProcess = current;
                promptWritten = false;
                promptIsCompact = true;
                autoCompactSeenDuringManual = false;
                preparing = null;
            }
        }
        if (id == 0)
        {
            TryAbortInterrupted();
            return;
        }

        await conn.SendRequestAsync(id, GrokAcpRules.MethodCompact, GrokAcpRules.BuildCompactParams(sid));
        await AfterPromptWrittenAsync(current, id, sid);
    }

    /// <summary>
    /// 這一則要附上的圖片（鎖外讀檔）。沒開旗標、不是圖片、檔案不在或讀不到就跳過。
    /// 超過 20 張或 20MB 的計入 skipped，呼叫端在文字裡說明
    /// </summary>
    private static List<(string MimeType, string Data)> ReadPromptImages(IReadOnlyList<AgentAttachment>? attachments, bool enabled, out int skipped)
    {
        skipped = 0;
        var images = new List<(string MimeType, string Data)>();
        if (!enabled || attachments == null)
            return images;
        long total = 0;
        foreach (var path in attachments.SelectMany(a => a.ModelImagePaths))
        {
            var mediaType = AgentAttachmentRules.GetClaudeImageMediaType(path);
            if (mediaType == null || !File.Exists(path))
                continue;
            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (images.Count >= MaxImagesPerMessage || total + length > MaxImageBytesPerMessage)
            {
                skipped++;
                continue;
            }
            try
            {
                images.Add((mediaType, Convert.ToBase64String(File.ReadAllBytes(path))));
                total += length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
        }
        return images;
    }

    /// <summary>
    /// grok 自己換了模式：計畫確認同意後回到 default（實測，接著在同一輪執行）、代理用 enter_plan_mode 進入 plan。
    /// 記下 grok 端的狀態；app 的模式跟著改（同意計畫時 RespondPlanAsync 已先改好，這裡不會再改）
    /// </summary>
    private void OnModeUpdate(string? modeId)
    {
        AgentPermissionMode? changed = null;
        lock (gate)
        {
            if (modeId == "plan")
            {
                appliedPlan = true;
                if (currentMode != AgentPermissionMode.Plan)
                    changed = currentMode = AgentPermissionMode.Plan;
            }
            else if (modeId == "default")
            {
                appliedPlan = false;
                if (currentMode == AgentPermissionMode.Plan)
                    changed = currentMode = AgentPermissionMode.Ask;
            }
        }
        if (changed is { } mode)
            Raise(new AgentPermissionModeChangedEvent(mode));
    }

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

    /// <summary>session/prompt 回應了：這一輪結束（stopReason＋_meta.usage）</summary>
    private void CompleteTurn(AgentProcess source, GrokAcpMessage message)
    {
        var stopReason = (message.Result as JObject)?.Value<string>("stopReason");
        List<AgentEvent> events;
        AgentTurnCompletedEvent? completed = null;
        string? limitNotice = null;
        List<PendingRequest> leftovers;
        lock (gate)
        {
            if (!running || !ReferenceEquals(promptProcess, source))
                return;
            var compacting = promptIsCompact;
            promptRequestId = null;
            promptProcess = null;
            promptIsCompact = false;
            leftovers = TakePendingForLocked(source);

            bool stoppedByRestart = false;
            if (restart is { } op && ReferenceEquals(op.Stopping, source))
            {
                op.TurnStopped.TrySetResult();
                // 重開送的取消讓這一輪停下：不回報 TurnCompleted（被打斷的那一輪由重開接續或交給中樞收尾），工具卡由中樞收尾。
                // 在取消生效前自己結束了：照常回報，不接續
                stoppedByRestart = message.Error != null || stopReason == "cancelled";
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
                events = parser.FinishTurn();
                if (message.Error != null)
                {
                    completed = new AgentTurnCompletedEvent(wasInterrupted ? AgentTurnOutcome.Interrupted : AgentTurnOutcome.Failed,
                        wasInterrupted ? null : GrokAcpRules.IsAuthRequired(message.Error) ? GrokAcpRules.NotSignedInMessage : $"Grok 執行失敗：{message.Error.Text}", null);
                }
                else
                {
                    var (outcome, error) = GrokAcpRules.EvaluateStopReason(stopReason);
                    limitNotice = GrokAcpRules.DescribeStopLimit(stopReason);
                    completed = new AgentTurnCompletedEvent(outcome, error, GrokAcpRules.ParseUsage(message.Result));
                    // token 計數：這一輪的用量只在這裡（依模型），在 TurnCompleted 之前發
                    events.AddRange(GrokAcpRules.ParseTokenEvents(message.Result, CurrentTokenModel()));
                    if (compacting && outcome == AgentTurnOutcome.Completed)
                    {
                        // 壓縮那次回應的 token 不是對話剩下的上下文：清掉用量，等下一次呼叫。
                        // 通知若還沒到，下一則 auto_compact_completed 不再發第二次
                        events.Add(new AgentNoticeEvent(GrokAcpRules.CompactedNotice, IsError: false));
                        events.Add(new AgentContextUsageEvent(null, null, Compacted: true));
                        if (!autoCompactSeenDuringManual)
                            suppressLateAutoCompact = true;
                    }
                    else if (ContextUsageRules.GrokPromptTokens(message.Result) is { } used)
                    {
                        // 上下文提示：_meta 頂層是最後一次模型呼叫（舊版沒有 response_completed 時也有）
                        events.Add(new AgentContextUsageEvent(used, null));
                    }
                }
            }
        }

        foreach (var request in leftovers)
            _ = CancelRequestQuietlyAsync(request);
        foreach (var ev in events)
            Raise(ev);
        if (completed == null)
        {
            PublishSafePoint();
            return;
        }
        // 中斷與重開接手的那一輪不查額度。查詢不能在讀取執行緒上等（OnLine 先觸發這個方法，RequestAsync 的回應也要同一條執行緒讀）
        if (completed.Outcome == AgentTurnOutcome.Interrupted)
        {
            if (limitNotice != null)
                Raise(new AgentNoticeEvent(limitNotice, IsError: false));
            Raise(completed);
            PublishSafePoint();
            return;
        }
        FinishTurnAfterBilling(source, completed, limitNotice);
    }

    /// <summary>
    /// 這一輪的內容已經送出。到讀取執行緒之外查 billing，鎖定才發 AgentQuotaExhaustedEvent，然後才發 TurnCompleted
    /// （中樞依到達順序決定要不要換帳戶）。程序已經沒了、查詢失敗或逾時也要發 TurnCompleted，否則這一輪會停在執行中
    /// </summary>
    private void FinishTurnAfterBilling(AgentProcess source, AgentTurnCompletedEvent completed, string? limitNotice)
    {
        GrokAcpConnection? conn;
        lock (gate)
            conn = ReferenceEquals(process, source) ? connection : null;
        _ = Task.Run(async () =>
        {
            AgentQuotaExhaustedEvent? quota = null;
            try
            {
                if (conn != null && !source.HasExited)
                {
                    var (result, error) = await conn.RequestAsync(GrokAcpRules.MethodBilling, new JObject(), BillingCheckTimeout).ConfigureAwait(false);
                    if (error == null && UsageRules.ParseGrokBilling(result as JObject)?.Windows.FirstOrDefault(w => w.Locked) is { } locked)
                        quota = new AgentQuotaExhaustedEvent(AgentProvider.Grok, locked.ResetsAt);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Grok 這一輪結束後查額度失敗：{ex.Message}");
            }
            if (quota != null)
                Raise(quota);
            if (limitNotice != null)
                Raise(new AgentNoticeEvent(limitNotice, IsError: false));
            Raise(completed);
            PublishSafePoint();
        });
    }

    private void OnRequest(AgentProcess source, GrokAcpMessage message)
    {
        var id = message.Id!;
        if (!IsCurrent(source))
        {
            // 已換掉或正在停下的程序：它的請求不顯示，直接以取消回覆
            _ = RespondQuietlyAsync(source, id, CancelledResult(message.Method, message.Params));
            return;
        }
        switch (message.Method)
        {
            case "session/request_permission":
                HandlePermission(source, id, message.Params);
                break;
            case "x.ai/ask_user_question":
            case "x.ai/ask_user_questions":
                HandleQuestion(source, id, message.Params);
                break;
            case "x.ai/exit_plan_mode":
                HandlePlan(source, id, message.Params);
                break;
            case "x.ai/folder_trust/request":
                // 初始化時沒有宣告互動式資料夾信任，grok 不會送；萬一送了，工作資料夾是使用者自己選的，回信任
                Log.Info("Grok 要求確認資料夾信任，已回覆信任（工作資料夾由使用者選擇）");
                _ = RespondQuietlyAsync(source, id, new JObject { ["outcome"] = "trust" });
                break;
            default:
                _ = WriteQuietlyAsync(source, GrokAcpRules.BuildError(id, -32601, $"Clode Myriagent 不支援 {message.Method}"));
                break;
        }
    }

    private void HandlePermission(AgentProcess source, JToken id, JObject parameters)
    {
        var request = GrokAcpRules.ParsePermissionRequest(parameters);
        var toolCall = parameters["toolCall"] as JObject;
        string? autoOption = null;
        List<AgentEvent> events;
        JObject? merged;
        string key = RequestKeyFor(source, id);
        lock (gate)
        {
            events = parser.AnnounceForRequest(toolCall);
            merged = parser.GetToolCall(request.ToolCallId) ?? toolCall;
            if (request.AcpKind != null && sessionAllowedKinds.Contains(request.AcpKind))
                autoOption = request.AllowOnceOptionId;
            else if (currentMode == AgentPermissionMode.FullAccess)
                autoOption = request.AllowOnceOptionId;
            else if (currentMode == AgentPermissionMode.AcceptEdits && currentWorkingDirectory != null
                && GrokAcpRules.CanAutoApproveEdit(request, currentWorkingDirectory, currentConversationDirectory ?? currentWorkingDirectory))
                autoOption = request.AllowOnceOptionId;
            if (autoOption == null)
                pendingRequests[key] = new PendingRequest(id, PendingKind.Permission, source, request);
        }

        foreach (var ev in events)
            Raise(ev);
        if (autoOption != null)
        {
            _ = RespondQuietlyAsync(source, id, GrokAcpRules.SelectedPermissionOutcome(autoOption));
            return;
        }

        var d = GrokAcpRules.DescribeTool(merged ?? new JObject());
        Raise(new AgentApprovalEvent(key, d.Name, d.Kind, d.Title, d.Detail, string.IsNullOrEmpty(request.ToolCallId) ? null : request.ToolCallId,
            CanAllowForSession: request.AllowSessionOptionId != null, IsPlan: false, d.FilePath, d.Diff, GrokAcpRules.AllowSessionLabel(request.AcpKind)));
        PublishSafePoint();
    }

    private void HandleQuestion(AgentProcess source, JToken id, JObject parameters)
    {
        var questions = GrokAcpRules.ParseQuestions(parameters);
        if (questions.Count == 0)
        {
            _ = RespondQuietlyAsync(source, id, GrokAcpRules.BuildQuestionCancelled());
            return;
        }
        var key = RequestKeyFor(source, id);
        lock (gate)
            pendingRequests[key] = new PendingRequest(id, PendingKind.Question, source, Questions: questions);
        Raise(new AgentQuestionEvent(key, questions, AnswerAsMessage: false));
        PublishSafePoint();
    }

    private void HandlePlan(AgentProcess source, JToken id, JObject parameters)
    {
        var key = RequestKeyFor(source, id);
        lock (gate)
            pendingRequests[key] = new PendingRequest(id, PendingKind.Plan, source);
        Raise(new AgentApprovalEvent(key, "計畫", "plan", "照這個計畫執行？", GrokAcpRules.DescribePlan(parameters),
            parameters.Value<string>("toolCallId"), CanAllowForSession: false, IsPlan: true));
        PublishSafePoint();
    }

    /// <summary>請求鍵帶上程序實例：不同程序的 JSON-RPC id 會重複（都從 0 起算）</summary>
    private static string RequestKeyFor(AgentProcess source, JToken id) => $"{source.InstanceId[..8]}:{GrokAcpRules.RequestKey(id)}";

    // ───────────────────────────── 回應卡片 ─────────────────────────────

    public async Task RespondApprovalAsync(string requestKey, AgentApprovalChoice choice, string? feedback)
    {
        var request = TakePending(requestKey, PendingKind.Permission);
        if (request?.Permission == null)
            return;
        if (choice == AgentApprovalChoice.AllowForSession && GrokAcpRules.RemembersSessionAllow(request.Permission.AcpKind) && request.Permission.AllowSessionOptionId != null)
        {
            lock (gate)
                sessionAllowedKinds.Add(request.Permission.AcpKind!);
        }
        // 拒絕時附的說明由中樞在這一輪結束後當下一則訊息送（grok 的拒絕回覆沒有欄位能帶說明）
        await RespondQuietlyAsync(request.Process, request.Id, GrokAcpRules.BuildPermissionOutcome(request.Permission, choice));
        PublishSafePoint();
    }

    public async Task AnswerQuestionAsync(string requestKey, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var request = TakePending(requestKey, PendingKind.Question);
        if (request?.Questions == null)
            return;
        await RespondQuietlyAsync(request.Process, request.Id, GrokAcpRules.BuildQuestionAnswer(request.Questions, answers));
        PublishSafePoint();
    }

    public async Task DeclineQuestionAsync(string requestKey)
    {
        var request = TakePending(requestKey, PendingKind.Question);
        if (request == null)
            return;
        await RespondQuietlyAsync(request.Process, request.Id, GrokAcpRules.BuildQuestionCancelled());
        PublishSafePoint();
    }

    /// <summary>
    /// 計畫確認：同意＝approved，再把權限模式切出規劃模式（照計畫執行、之後改檔案不再詢問＝自動接受編輯；仍要詢問＝每步詢問）；
    /// 繼續規劃＝cancelled（附說明）
    /// </summary>
    public async Task RespondPlanAsync(string requestKey, AgentPlanChoice choice, string? feedback)
    {
        var request = TakePending(requestKey, PendingKind.Plan);
        if (request == null)
            return;
        if (choice == AgentPlanChoice.KeepPlanning)
        {
            await RespondQuietlyAsync(request.Process, request.Id, GrokAcpRules.BuildPlanResponse(choice, feedback));
            PublishSafePoint();
            return;
        }

        // 同意後 grok 立刻回到 default 模式並在同一輪接著執行（實測）：先改好 app 的模式再回覆，接下來的核准請求才照新模式判斷
        var mode = choice == AgentPlanChoice.AcceptEdits ? AgentPermissionMode.AcceptEdits : AgentPermissionMode.Ask;
        GrokAcpConnection? conn;
        string? sid;
        lock (gate)
        {
            currentMode = mode;
            conn = ReferenceEquals(process, request.Process) ? connection : null;
            sid = processSessionId;
        }
        await RespondQuietlyAsync(request.Process, request.Id, GrokAcpRules.BuildPlanResponse(choice, feedback));
        PublishSafePoint();
        if (conn != null && sid != null)
        {
            try
            {
                await ApplyModeAsync(conn, sid, mode, CancellationToken.None);
            }
            catch (IOException)
            {
            }
        }
        Raise(new AgentPermissionModeChangedEvent(mode));
    }

    private PendingRequest? TakePending(string requestKey, PendingKind kind)
    {
        lock (gate)
        {
            if (!pendingRequests.TryGetValue(requestKey, out var request) || request.Kind != kind)
                return null;
            pendingRequests.Remove(requestKey);
            return ReferenceEquals(request.Process, process) ? request : null;
        }
    }

    /// <summary>取出某個程序所有未回應的請求（這一輪結束、停止、程序結束時以取消回覆）</summary>
    private List<PendingRequest> TakePendingForLocked(AgentProcess? source)
    {
        var taken = pendingRequests.Where(p => source == null || ReferenceEquals(p.Value.Process, source)).ToList();
        foreach (var (key, _) in taken)
            pendingRequests.Remove(key);
        return taken.Select(p => p.Value).ToList();
    }

    private static JObject CancelledResult(string? method, JObject parameters) => method switch
    {
        "x.ai/ask_user_question" or "x.ai/ask_user_questions" => GrokAcpRules.BuildQuestionCancelled(),
        "x.ai/exit_plan_mode" => GrokAcpRules.BuildPlanAbandoned(),
        "x.ai/folder_trust/request" => new JObject { ["outcome"] = "reject" },
        _ => GrokAcpRules.CancelledPermissionOutcome(),
    };

    private Task CancelRequestQuietlyAsync(PendingRequest request)
        => RespondQuietlyAsync(request.Process, request.Id, request.Kind switch
        {
            PendingKind.Question => GrokAcpRules.BuildQuestionCancelled(),
            PendingKind.Plan => GrokAcpRules.BuildPlanAbandoned(),
            _ => GrokAcpRules.CancelledPermissionOutcome(),
        });

    private static Task RespondQuietlyAsync(AgentProcess target, JToken id, JToken result) => WriteQuietlyAsync(target, GrokAcpRules.BuildResult(id, result));

    // ───────────────────────────── 停止、程序結束、關閉 ─────────────────────────────

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
            suspendedSession = null;   // 停下待接續的那一輪不再接續（由中樞收尾）
            if (restart is { ContinuationStarted: false } op)
            {
                // 重開途中按停止：不接續。舊程序本來就在停下，被打斷的那一輪由中樞收尾
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
            // 還沒送出 session/prompt（準備中）：取消準備中的請求，不必等程序啟動或建立對話做完
            preparation = prompt == null ? preparing : null;
        }

        // 在鎖外取消：取消會同步執行等待中的接續
        preparation?.Cancel();
        foreach (var request in pending)
            await CancelRequestQuietlyAsync(request);
        PublishSafePoint();
        if (!sendNow || target == null)
            return;

        await WriteQuietlyAsync(target, GrokAcpRules.BuildNotification("session/cancel", GrokAcpRules.BuildCancelParams(sid!)));
        _ = KillIfStuckAsync(target, prompt!.Value);
    }

    /// <summary>
    /// session/prompt 寫出之後：標成已寫出；寫出之前已按停止（停止那時沒送取消）就現在補送，
    /// 免得取消比 prompt 先到被 grok 忽略、這一輪照跑到被當成卡住硬殺
    /// </summary>
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
        await WriteQuietlyAsync(target, GrokAcpRules.BuildNotification("session/cancel", GrokAcpRules.BuildCancelParams(sid))).ConfigureAwait(false);
        _ = KillIfStuckAsync(target, id);
    }

    /// <summary>送出取消後 CancelTimeout 內 prompt 還沒回應：結束程序樹（OnExited 以「已停止」收尾）</summary>
    private async Task KillIfStuckAsync(AgentProcess target, long prompt)
    {
        await Task.WhenAny(target.ExitTask, Task.Delay(CancelTimeout)).ConfigureAwait(false);
        bool stuck;
        lock (gate)
            stuck = running && promptRequestId == prompt && ReferenceEquals(promptProcess, target);
        if (stuck)
        {
            Log.Warn($"Grok 在 {CancelTimeout.TotalSeconds:0.#} 秒內沒有回應停止，結束程序樹");
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

            // 重開接手這一輪、還沒開始接續的期間：程序的結束不回報成 TurnCompleted
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
                promptIsCompact = false;
                events = parser.FinishTurn(AgentItemStatus.Cancelled, wasInterrupted ? GrokAcpRules.CancelledToolOutput : GrokAcpRules.InterruptedToolOutput);
                if (restart is { ContinuationStarted: true } continuing)
                    continuing.ContinuationReported = true;
            }
        }
        conn?.FailAll("Grok 程序已結束");
        RaiseBackgroundCleared(droppedBackground);

        var tail = source.GetStderrTail();
        foreach (var ev in events)
            Raise(ev);
        if (wasRunning)
        {
            Raise(wasInterrupted
                ? new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null)
                : new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, $"Grok 程序意外結束（結束碼 {exitCode?.ToString() ?? "不明"}）{(string.IsNullOrWhiteSpace(tail) ? string.Empty : "：" + tail)}", null));
        }

        // 中樞把這個實例的待回應卡片作廢
        Raise(new AgentProcessExitedEvent(exitCode, tail, Expected: ownedByRestart || !wasRunning, source.InstanceId));
        PublishSafePoint();
        stoppedForRestart?.ExitHandled.TrySetResult();
        source.Dispose();
    }

    /// <summary>從會話拔掉目前的程序（呼叫端負責關閉它）：程序上的對話與套用過的設定一併清掉。回傳清掉的背景工作數（呼叫端在鎖外發數量 0）</summary>
    private int DetachProcessLocked()
    {
        var had = background.Count;
        background.Clear();
        promptImages = false;
        process = null;
        connection = null;
        processSessionId = null;
        attachedFresh = null;
        processCli = null;
        processSignature = null;
        ResetAppliedLocked();
        return had;
    }

    private void RaiseBackgroundCleared(int had)
    {
        if (had > 0)
            Raise(new AgentBackgroundWorkEvent(0, Array.Empty<AgentBackgroundTool>()));
    }

    /// <summary>
    /// task_backgrounded 記入並發數量；task_completed 且 will_wake 明確是 false、對得到這一筆，才發背景指令結束。
    /// will_wake 是 true 或沒寫時只更新數量（grok 自己會接續，避免送兩次）。task id 可在 task_snapshot。不在持有 gate 時發事件
    /// </summary>
    private void NoteBackground(JObject? body, string? method)
    {
        if (!GrokAcpRules.TryParseBackground(body, method, out var parsed))
            return;

        AgentBackgroundWorkEvent? work = null;
        AgentBackgroundCommandFinishedEvent? finished = null;
        lock (gate)
        {
            if (parsed.Kind == "task_backgrounded")
            {
                var described = DescribeBackgroundToolLocked(parsed);
                if (described.OutputFile == null && background.TryGetValue(parsed.TaskId, out var previous))
                    described.OutputFile = previous.OutputFile;
                background[parsed.TaskId] = described;
                work = SnapshotBackgroundLocked();
            }
            else if (parsed.Kind == "task_completed" && background.Remove(parsed.TaskId, out var existing))
            {
                work = SnapshotBackgroundLocked();
                if (parsed.WillWake == false)
                {
                    var toolId = existing.ToolCallId ?? parsed.ToolCallId ?? parsed.TaskId;
                    finished = new AgentBackgroundCommandFinishedEvent(toolId, existing.Tool.Title, AgentItemStatus.Done, null, null);
                }
            }
        }
        if (work != null)
            Raise(work);
        if (finished != null)
            Raise(finished);
        if (work != null || finished != null)
            PublishSafePoint();
    }

    /// <summary>呼叫端已持有 gate。對得到工具卡就用它的名稱；否則標題用 monitor 說明。TaskId 讓「詳細」來讀輸出檔</summary>
    private BackgroundWork DescribeBackgroundToolLocked(GrokBackgroundNote parsed)
    {
        AgentBackgroundTool tool;
        if (parsed.ToolCallId is { Length: > 0 } toolCallId && parser.GetToolCall(toolCallId) is { } call)
        {
            var described = GrokAcpRules.DescribeTool(call);
            tool = new AgentBackgroundTool(described.Name, described.Kind, described.Title, described.Detail, parsed.TaskId);
        }
        else
        {
            var title = string.IsNullOrWhiteSpace(parsed.Description) ? "背景工作" : parsed.Description.Trim();
            tool = new AgentBackgroundTool("背景工作", "bash", title, null, parsed.TaskId);
        }
        return new BackgroundWork(parsed.ToolCallId, tool, GrokAcpRules.AcceptOutputFile(parsed.OutputFile));
    }

    /// <summary>呼叫端已持有 gate。ToolIds 空的時候給 null，中樞才留得住對不到卡的背景工作</summary>
    private AgentBackgroundWorkEvent SnapshotBackgroundLocked()
    {
        var tools = background.Values.Select(v => v.Tool).ToArray();
        var ids = background.Values.Select(v => v.ToolCallId).Where(id => !string.IsNullOrEmpty(id)).Cast<string>().ToArray();
        return new AgentBackgroundWorkEvent(background.Count, tools, ids.Length == 0 ? null : ids);
    }

    /// <summary>背景工作的輸出：讀通知帶來的本機檔尾端。沒有這筆、或路徑不是本機磁碟，回錯誤；檔案還沒寫出來回空字串</summary>
    public Task<AgentTaskOutput> GetTaskOutputAsync(string taskId)
    {
        string? path;
        lock (gate)
        {
            if (!background.TryGetValue(taskId, out var entry))
                return Task.FromResult(new AgentTaskOutput(null, 0, false, "這個背景工作已經結束了"));
            path = entry.OutputFile;
        }
        if (path == null)
            return Task.FromResult(new AgentTaskOutput(null, 0, false, "這個背景工作沒有本機的輸出檔"));
        var file = path;
        return Task.Run(() => GrokAcpRules.ReadBackgroundOutput(file));
    }

    /// <summary>
    /// 把目前的 grok 對話整份分叉（1.0.70 實測，tools/probes/grok/fork_probe.py：新對話續接後記得分叉前的內容、看不到原對話之後的，原對話不受影響）。
    /// 成功回新的 session id（程序仍留在來源那份）。失敗回 null，呼叫端改附前文節錄。還沒建立過對話、或接上時原本的對話已經續接不了
    /// （改開了新的，分叉出來的會是空的）也回 null
    /// </summary>
    public async Task<string?> ForkSessionAsync(string newSessionId, AgentSessionSettings settings)
    {
        try
        {
            if (!HasGrokSession(settings))
                return null;
            var (conn, sid, fresh) = await EnsureAttachedAsync(settings, CancellationToken.None).ConfigureAwait(false);
            if (fresh)
            {
                Log.Warn("Grok 分叉：原本的對話已經續接不了（改開了新的），改附前文節錄");
                return null;
            }
            var (result, error) = await conn.RequestAsync(GrokAcpRules.MethodFork, GrokAcpRules.BuildForkParams(sid, settings.WorkingDirectory, newSessionId), SessionRequestTimeout).ConfigureAwait(false);
            if (error != null)
            {
                Log.Warn($"Grok 分叉失敗：{error.Text}");
                return null;
            }
            var id = GrokAcpRules.ParseForkSessionId(result);
            if (id == null)
                Log.Warn("Grok 分叉回應沒有 newSessionId");
            return id;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or DirectoryNotFoundException or AgentCliUnavailableException)
        {
            Log.Warn($"Grok 分叉失敗：{ex.Message}");
            return null;
        }
    }

    private bool HasGrokSession(AgentSessionSettings settings)
    {
        lock (gate)
            return sessionId != null || settings.SessionStarted;
    }

    /// <summary>
    /// 分叉要用活著的連線，但不開始一輪（不設 running，程序自己結束不會被當成這一輪失敗）。
    /// 程序已在、這個對話已接上、設定沒變：直接用，執行中也可以。沒有可用的程序、這一輪又在跑：失敗。
    /// 閒置且沒有程序：啟動並建立或續接，不送提示。Fresh＝這次開了新的 grok 對話（續接失敗或從來沒建立過，留給下一次送出帶指示與前文節錄）
    /// </summary>
    private async Task<(GrokAcpConnection Connection, string SessionId, bool Fresh)> EnsureAttachedAsync(AgentSessionSettings settings, CancellationToken ct)
    {
        TaskCompletionSource done;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (attachDone != null)
                throw new InvalidOperationException("正在準備 Grok 對話");
            if (restart != null)
                throw new InvalidOperationException("正在重開 Grok");
            var signature = Signature(settings);
            bool same = process != null && !process.HasExited && connection != null && processSessionId != null
                && string.Equals(processSignature, signature, StringComparison.Ordinal)
                && CliBinaryIdentity.SameOrUnknown(processCli, settings.Cli);
            if (same)
                return (connection!, processSessionId!, false);
            if (running)
                throw new InvalidOperationException("這一輪還在執行，不能另外接上 Grok");
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            attachDone = done;
        }

        try
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
            if (stale != null)
            {
                await stale.CloseAsync(CloseGrace).ConfigureAwait(false);
                stale.Dispose();
            }
            ThrowIfDisposed();
            current ??= await StartProcessAsync(settings, ct).ConfigureAwait(false);
            ThrowIfDisposed();
            GrokAcpConnection conn;
            string? sid;
            lock (gate)
            {
                conn = connection ?? throw new InvalidOperationException("Grok 程序已結束");
                sid = processSessionId;
            }
            bool fresh = false;
            if (sid == null)
            {
                var (opened, createdNew, lostHistory) = await OpenSessionAsync(conn, settings, ct).ConfigureAwait(false);
                sid = opened;
                fresh = createdNew || lostHistory;
                if (fresh)
                {
                    lock (gate)
                        attachedFresh = ReferenceEquals(connection, conn) && process is { } owner ? (owner, createdNew, lostHistory) : null;
                }
            }
            return (conn, sid, fresh);
        }
        finally
        {
            lock (gate)
                attachDone = null;
            done.TrySetResult();
        }
    }

    private void ResetAppliedLocked()
    {
        appliedModel = null;
        modelKnown = false;
        appliedEffort = null;
        appliedPermission = null;
        appliedPlan = null;
    }

    public Task CloseAsync() => CloseCoreAsync(null);

    /// <summary>
    /// 強制停止背景工作：ACP 沒有停止背景工作的方法（kill_task 只是模型的工具），只能全部停止——閒置時結束 grok 的程序樹
    /// （--no-leader：背景工作是它的子孫，跟著結束；下次送出照常 session/load 續接），執行中不做
    /// </summary>
    public async Task<AgentBackgroundStopResult> StopBackgroundWorkAsync(string? stopId)
    {
        if (stopId != null)
            return new AgentBackgroundStopResult(0, "Grok 的背景工作只能全部停止");
        AgentProcess target;
        int count;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited)
                return new AgentBackgroundStopResult(0, "Grok 程序已經結束，背景工作也跟著結束了");
            if (running || restart != null)
                return new AgentBackgroundStopResult(0, "代理還在處理這一輪：先按停止，再停止背景工作");
            if (background.Count == 0)
                return new AgentBackgroundStopResult(0, "背景工作已經結束了");
            target = process;
            count = background.Count;
        }
        var tree = await Task.Run(target.CaptureTree).ConfigureAwait(false);
        if (!await CloseCoreAsync(tree, onlyIfIdle: target).ConfigureAwait(false))
            return new AgentBackgroundStopResult(0, "代理剛開始處理新的一輪：先按停止，再停止背景工作");
        Log.Info($"Grok 停止背景工作：結束程序樹（{tree.Processes.Count} 個程序）");
        return new AgentBackgroundStopResult(count, null);
    }

    /// <summary>
    /// 關閉程序。killTree 不是 null＝卸下程序後立刻結束那棵程序樹（強制停止背景工作，不等 CLI 收尾）；onlyIfIdle 不是 null＝只在它還是目前的程序、
    /// 閒置、沒在重開時才關（否則什麼都不做，回 false）
    /// </summary>
    private async Task<bool> CloseCoreAsync(ProcessTreeSnapshot? killTree, AgentProcess? onlyIfIdle = null)
    {
        AgentProcess? target;
        GrokAcpConnection? conn;
        int droppedBackground;
        lock (gate)
        {
            if (onlyIfIdle != null && (!ReferenceEquals(process, onlyIfIdle) || running || restart != null))
                return false;
            suspendedSession = null;
            if (restart != null)
                restart.Cancelled = true;
            target = process;
            conn = connection;
            droppedBackground = DetachProcessLocked();
            running = false;
            promptRequestId = null;
            promptProcess = null;
            promptIsCompact = false;
            pendingRequests.Clear();
        }
        RaiseBackgroundCleared(droppedBackground);
        PublishSafePoint();
        conn?.FailAll("對話已關閉");

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

    // ───────────────────────────── CLI 熱更新：以新版重開 ─────────────────────────────

    /// <summary>
    /// 以新版 CLI 重開程序（熱更新契約 2.5 的流程表，與 Codex 同一套判斷）：不在安全點（SafePoint 模式）回 Deferred；沒有程序也沒有停下待接續的一輪回 Restarted；
    /// 否則停下舊程序（這一輪在執行就 session/cancel、等 prompt 回應，逾時才結束程序樹；關 stdin；確認程序樹都已結束），
    /// Settings=null 到此回 Stopped（被打斷的一輪記住停下待接續），不接續回 Restarted，
    /// 其他以 Settings 啟動新程序、session/resume、發 AgentTurnStartedByCliEvent、session/prompt 送接續訊息，回 RestartedAndContinued。
    /// 失敗回 Failed（回到閒置、沒有程序）
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
            Log.Error("Grok 以新版重開失敗", ex);
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
        // SendAsync 還在準備這一輪（啟動程序、建立或續接對話）：等它做完再判斷，使用者剛送的訊息才不會丟掉
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
        string? sid;
        bool turnRunning;
        lock (gate)
        {
            if (disposed)
                return new AgentRestartResult(AgentRestartOutcome.Failed, Error: "對話已關閉");
            bool live = process != null && !process.HasExited;
            switch (CodexAppServerRules.DecideRestartEntry(request.Mode, CurrentSafePointLocked().IsAtSafePoint, live, suspendedSession != null))
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
            sid = processSessionId ?? sessionId;
            turnRunning = old != null && running && promptRequestId != null && ReferenceEquals(promptProcess, old);
        }

        if (old != null)
        {
            Log.Info($"Grok 以新版重開（{request.Reason}，{request.FromVersion ?? "?"} → {request.ToVersion ?? "?"}）：停下程序 {old.InstanceId}"
                + (turnRunning ? "（這一輪執行中）" : string.Empty));
            var stopError = await StopForRestartAsync(old, turnRunning ? sid : null, op).ConfigureAwait(false);
            if (stopError != null)
            {
                lock (gate)
                {
                    op.Stopping = null;
                    running = false;
                    promptRequestId = null;
                    promptProcess = null;
                    suspendedSession = null;
                }
                Log.Warn($"Grok 重開失敗：{stopError}");
                return new AgentRestartResult(AgentRestartOutcome.Failed, op.PreviousInstance, Error: stopError);
            }
        }

        string? resumeSession;
        AgentProcess? detached;
        AgentRestartResult? settled = null;
        int droppedBackground = 0;
        lock (gate)
        {
            op.Stopping = null;
            detached = old != null && ReferenceEquals(process, old) ? old : null;
            if (detached != null)
                droppedBackground = DetachProcessLocked();
            bool interrupted = (turnRunning && !op.TurnFinishedOnItsOwn) || suspendedSession != null;
            resumeSession = suspendedSession ?? sid;
            var followUp = disposed
                ? CodexRestartFollowUp.NoContinuation
                : CodexAppServerRules.DecideRestartFollowUp(request.Settings != null, interrupted && resumeSession != null, request.AutoContinue, op.Cancelled);
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
                // 不接續：被打斷的那一輪交給中樞收尾（Stopped 時記住停下待接續，之後帶 Settings 再呼叫一次）
                running = false;
                promptRequestId = null;
                promptProcess = null;
                suspendedSession = followUp == CodexRestartFollowUp.StopAndSuspend ? resumeSession : null;
            }
            else
            {
                // 接續：被打斷的那一輪繼續算執行中，接續的一輪照常以 TurnCompleted 結束
                suspendedSession = null;
                running = true;
                promptRequestId = null;
                promptProcess = null;
                interruptRequested = false;
                cancelSent = false;
            }
        }

        RaiseBackgroundCleared(droppedBackground);
        if (detached != null)
        {
            // 舊程序都結束了、它的結束事件卻還沒處理（stdout 被別的程序握著）：補發結束事件
            Raise(new AgentProcessExitedEvent(null, detached.GetStderrTail(), Expected: true, detached.InstanceId));
            detached.Dispose();
        }
        if (settled != null)
            return settled;
        return await ContinueAfterRestartAsync(request, request.Settings!, resumeSession!, op).ConfigureAwait(false);
    }

    /// <summary>
    /// 停下要換掉的程序：這一輪在執行就送 session/cancel、等 prompt 回應（上限 RestartInterruptTimeout），擷取程序樹後關 stdin
    /// （RestartCloseGrace 後結束程序樹），再確認樹上的程序（含 grok 開的指令）都已結束——不確認就開新一輪，舊指令會和重跑的指令重疊。
    /// 回傳 null＝成功，否則是失敗原因
    /// </summary>
    private async Task<string?> StopForRestartAsync(AgentProcess old, string? runningSession, RestartOperation op)
    {
        var before = old.CaptureTree();
        List<PendingRequest> pending;
        lock (gate)
            pending = TakePendingForLocked(old);
        foreach (var request in pending)
            await CancelRequestQuietlyAsync(request).ConfigureAwait(false);

        if (runningSession != null)
        {
            await WriteQuietlyAsync(old, GrokAcpRules.BuildNotification("session/cancel", GrokAcpRules.BuildCancelParams(runningSession))).ConfigureAwait(false);
            await Task.WhenAny(op.TurnStopped.Task, old.ExitTask, Task.Delay(RestartInterruptTimeout)).ConfigureAwait(false);
            if (!op.TurnStopped.Task.IsCompleted && !old.HasExited)
                Log.Warn($"Grok 在 {RestartInterruptTimeout.TotalSeconds:0.#} 秒內沒有結束被取消的一輪，直接關閉程序（必要時結束程序樹）");
        }

        var tree = old.CaptureTree();
        await old.CloseAsync(RestartCloseGrace).ConfigureAwait(false);
        bool gone = await WaitTreeExitAsync(tree).ConfigureAwait(false) & await WaitTreeExitAsync(before).ConfigureAwait(false);
        await Task.WhenAny(op.ExitHandled.Task, Task.Delay(ExitHandledTimeout)).ConfigureAwait(false);
        return gone ? null : "舊的 Grok 程序或它正在執行的指令沒有結束，為了避免和接續後重跑的指令重疊，沒有以新版重開";
    }

    private async Task<bool> WaitTreeExitAsync(ProcessTreeSnapshot tree)
    {
        if (await tree.WaitForExitAsync(RestartTreeExitTimeout).ConfigureAwait(false))
            return true;
        int killed = tree.KillAlive();
        Log.Warn($"Grok 舊程序樹還有 {killed} 個程序沒有結束，已逐一結束");
        return await tree.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    /// <summary>以 Settings 啟動新程序、session/resume 被打斷的對話，送接續訊息開始新的一輪</summary>
    private async Task<AgentRestartResult> ContinueAfterRestartAsync(AgentRestartRequest request, AgentSessionSettings settings, string sid, RestartOperation op)
    {
        AgentProcess? fresh = null;
        try
        {
            fresh = await StartProcessAsync(settings, CancellationToken.None).ConfigureAwait(false);
            GrokAcpConnection conn;
            lock (gate)
                conn = connection ?? throw new InvalidOperationException("Grok 程序已結束");
            var (_, resumeError) = await conn.RequestAsync("session/resume", GrokAcpRules.BuildResumeParams(sid, settings.WorkingDirectory, settings.PermissionMode),
                SessionRequestTimeout).ConfigureAwait(false);
            if (resumeError != null)
            {
                // 改開新對話沒有前文，接續訊息沒有意義：交給中樞收尾，使用者再送出時照一般流程（續接失敗就附前文節錄開新的）
                throw new InvalidOperationException($"無法續接原本的 Grok 對話：{resumeError.Text}");
            }
            lock (gate)
            {
                processSessionId = sid;
                sessionId = sid;
                appliedPermission = null;
                appliedPlan = null;
            }
            Raise(new AgentSessionStartedEvent(sid, null, "resumed"));
            await ApplyConfigAsync(conn, sid, settings, CancellationToken.None).ConfigureAwait(false);

            long id;
            lock (gate)
            {
                if (op.Cancelled || disposed || !ReferenceEquals(process, fresh))
                {
                    // 重開途中按了停止或關閉：新程序已續接好（閒置），不送接續訊息
                    running = false;
                    return new AgentRestartResult(AgentRestartOutcome.Restarted, op.PreviousInstance, ReferenceEquals(process, fresh) ? fresh.InstanceId : null);
                }
                op.ContinuationStarted = true;
                running = true;
                interruptRequested = false;
                cancelSent = false;
                lastWorkingDirectory = settings.WorkingDirectory;
                currentMode = settings.PermissionMode;
                currentWorkingDirectory = settings.WorkingDirectory;
                currentConversationDirectory = settings.ConversationDirectory;
                parser.BeginTurn();
                id = conn.ReserveId();
                promptRequestId = id;
                promptProcess = fresh;
                promptWritten = false;
            }

            // 在送出 session/prompt 之前發：保證排在接續那一輪的任何事件之前（中樞據此把對話標成執行中）。接續訊息不顯示成使用者訊息
            Raise(new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));
            await conn.SendRequestAsync(id, "session/prompt", GrokAcpRules.BuildPromptParams(sid, request.EffectiveContinuationText)).ConfigureAwait(false);
            await AfterPromptWrittenAsync(fresh, id, sid).ConfigureAwait(false);
            Log.Info($"Grok 已以新版重開並接續（程序 {op.PreviousInstance ?? "無"} → {fresh.InstanceId}，版本 {RunningVersion ?? "不明"}）");
            return new AgentRestartResult(AgentRestartOutcome.RestartedAndContinued, op.PreviousInstance, fresh.InstanceId);
        }
        catch (Exception ex)
        {
            bool reported;
            bool cancelled;
            lock (gate)
            {
                // 接續的一輪已經以 TurnCompleted 回報結束（新程序在途中結束）：照實回報已接續，不再重複收尾
                reported = op.ContinuationReported;
                cancelled = op.Cancelled || disposed;
                if (!reported)
                {
                    running = false;
                    promptRequestId = null;
                    promptProcess = null;
                    interruptRequested = false;
                    cancelSent = false;
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
            Log.Warn($"Grok 以新版重開後接續失敗：{ex.Message}");
            return new AgentRestartResult(AgentRestartOutcome.Failed, op.PreviousInstance, Error: ex.Message);
        }
    }

    // ───────────────────────────── 安全點 ─────────────────────────────

    /// <summary>目前的安全點旗標（呼叫端已持有 gate）。背景工作撐過這一輪時不能重開</summary>
    private AgentSafePointChangedEvent CurrentSafePointLocked()
        => new(running && parser.HasRunningTools, running && pendingRequests.Count > 0, background.Count > 0);

    /// <summary>安全點的三個旗標有變就發 AgentSafePointChangedEvent（去重）。不能在 gate 內呼叫</summary>
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

    private static async Task WriteQuietlyAsync(AgentProcess target, string line)
    {
        try
        {
            await target.WriteLineAsync(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
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
            Debug.WriteLine($"[GrokAgentSession] 事件處理失敗：{ex}");
        }
    }

    public void Dispose()
    {
        AgentProcess? target;
        GrokAcpConnection? conn;
        int droppedBackground = 0;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            suspendedSession = null;
            if (restart != null)
                restart.Cancelled = true;
            target = process;
            conn = connection;
            droppedBackground = DetachProcessLocked();
            running = false;
            promptRequestId = null;
            promptProcess = null;
            promptIsCompact = false;
            pendingRequests.Clear();
        }
        RaiseBackgroundCleared(droppedBackground);
        conn?.FailAll("對話已關閉");
        target?.Dispose();
    }

    /// <summary>進行中的 RestartAsync（同一個會話同時只有一個）；欄位只在 gate 內讀寫</summary>
    private sealed class RestartOperation
    {
        /// <summary>舊程序的這一輪已結束（收到 prompt 回應）</summary>
        public readonly TaskCompletionSource TurnStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>舊程序的結束已處理完（OnExited 發出了 AgentProcessExitedEvent）</summary>
        public readonly TaskCompletionSource ExitHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>正在停下的舊程序：它這一輪的取消與它的結束都不回報成 TurnCompleted，它新送來的請求不顯示</summary>
        public AgentProcess? Stopping;

        /// <summary>被停下的舊程序實例（結果的 PreviousProcessInstance）</summary>
        public string? PreviousInstance;

        /// <summary>重開已接手這一輪（等完 SendAsync 的準備、通過安全點檢查）：之後按停止＝不接續，交給中樞收尾</summary>
        public bool TookOver;

        /// <summary>這一輪在取消生效前自己結束了（已照常回報 TurnCompleted）：不接續</summary>
        public bool TurnFinishedOnItsOwn;

        /// <summary>重開途中按了停止、關閉了程序或對話：不接續</summary>
        public bool Cancelled;

        /// <summary>接續的一輪已開始（已發 AgentTurnStartedByCliEvent）：之後按停止照一般流程中斷這一輪</summary>
        public bool ContinuationStarted;

        /// <summary>接續的一輪已以 TurnCompleted 回報結束（例如新程序在接續途中結束）</summary>
        public bool ContinuationReported;
    }
}
