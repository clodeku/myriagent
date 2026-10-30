using System.Collections.Concurrent;
using System.Diagnostics;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// Codex 代理：一個對話一個 codex app-server 程序（JSON-RPC over stdio），第一則訊息 thread/start、程序重開後 thread/resume 續接；
/// 模型、推理強度、權限、沙箱、工作資料夾都隨每輪 turn/start 帶上，切換設定不必重開程序（帳戶、執行檔、執行檔身分、客製設定改了才重開）。
/// 需要沙箱的權限模式第一次使用時檢查 Windows 沙箱，從未設定就以免系統管理員的 unelevated 模式自動設定（使用者設過的 elevated 沙箱要更新時不動它），
/// 失敗則改「每個指令都問」。
/// CLI 熱更新（docs/plans/2026-10-10-hot-restart-contract.md，設計 7.6）：追蹤執行中的工具與待回應的請求（安全點），
/// RestartAsync 以 turn/interrupt 停下這一輪（逾時才結束程序樹）、關掉舊程序並確認它開的指令都已結束、截掉 rollout 寫到一半的尾行，
/// 再以新版 thread/resume 並用 turn/start 送接續訊息（Codex 沒有自動接續）；被重開打斷的那一輪不回報 TurnCompleted。
/// </summary>
internal sealed partial class CodexAgentSession : IAgentSession
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SandboxSetupTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan InterruptWatchdog = TimeSpan.FromSeconds(15);

    /// <summary>thread/backgroundTerminals/clean（實驗性）的等待上限：不支援會馬上回錯誤，逾時也照樣往下關程序</summary>
    private static readonly TimeSpan BackgroundTerminalsCleanTimeout = TimeSpan.FromSeconds(3);

    /// <summary>使用者停止背景指令（thread/backgroundTerminals/list、terminate、clean）的等待上限：0.164.0 實測 0.05 秒內就結束那個指令</summary>
    private static readonly TimeSpan StopBackgroundTimeout = TimeSpan.FromSeconds(10);

    /// <summary>重開時等舊程序的結束事件處理完的上限（新程序的事件因此排在舊程序的 AgentProcessExitedEvent 之後）</summary>
    private static readonly TimeSpan ExitHandledTimeout = TimeSpan.FromSeconds(5);

    /// <summary>接續失敗、關掉剛啟動的新程序後等它結束的上限</summary>
    private static readonly TimeSpan FailedProcessExitTimeout = TimeSpan.FromSeconds(2);
    private const int MaxImagesPerMessage = 20;

    private readonly object gate = new();
    private readonly ConcurrentDictionary<long, (AgentProcess Process, TaskCompletionSource<(JObject? Result, string? Error)> Completion)> responses = new();
    private readonly Dictionary<string, CodexServerRequest> pendingRequests = new();

    /// <summary>待回應請求的「完全相同」比對鍵（回應「本對話都允許」時記住它）</summary>
    private readonly Dictionary<string, string> requestKeys = new(StringComparer.Ordinal);
    /// <summary>本對話整類放行的種類（只有檔案修改，同 VS Code 的「接受編輯」）</summary>
    private readonly HashSet<string> sessionAllowed = new(StringComparer.Ordinal);

    /// <summary>本對話放行的「完全相同的請求」（比對鍵見 CodexAppServerRules.BuildSessionApprovalKey）</summary>
    private readonly HashSet<string> sessionAllowedRequests = new(StringComparer.Ordinal);

    /// <summary>
    /// 執行中的工具項目（CodexAppServerRules.ParseToolLifecycle 的鍵：item/started 到 item/completed）：有的話不在安全點。
    /// 已讓出給模型的指令移到 yieldedCommands
    /// </summary>
    private readonly Dictionary<string, CodexToolLifecycle> runningTools = new(StringComparer.Ordinal);

    /// <summary>
    /// 這一輪中途已讓出給模型的指令（鍵同 runningTools，子代理的也算）：unified_exec 的指令超過等待時間就把「還在跑」交回模型，模型接著做別的事，
    /// 指令留在終端機裡繼續跑（開發伺服器、長時間的測試）——那個 thread 的一次模型呼叫結束（thread/tokenUsage/updated）時還沒結束的指令。
    /// 不算「工具在執行」（模型沒有在等它，等它完成才停下會一直等到這一輪結束，2026-10-24 app 更新因此排空超過 30 分鐘、連續放棄三次），
    /// 算背景工作（重開會連同它結束）。這一輪結束時清掉：主對話這一輪正常結束時還在跑的照舊移到 backgroundCommands
    /// </summary>
    private readonly Dictionary<string, CodexToolLifecycle> yieldedCommands = new(StringComparer.Ordinal);

    /// <summary>
    /// 這一輪主對話還沒結束的指令（item/started commandExecution 到 item/completed；工具 id → 卡片的說明）。這一輪正常結束時還在跑的
    /// （unified_exec 的長時間指令，實測見 docs/protocol-notes.md「Codex 的背景指令」）移到 backgroundCommands
    /// </summary>
    private readonly Dictionary<string, AgentBackgroundTool> turnCommands = new(StringComparer.Ordinal);

    /// <summary>
    /// 背景指令：一輪正常結束時還在跑的主對話指令（加入的順序）。跑完時 app-server 在沒有一輪進行時送 item/completed，Codex 不會自己開一輪——
    /// 會話發 AgentBackgroundCommandFinishedEvent，由中樞隱藏送出接續訊息（比照 Claude 的 CLI 自己開一輪）。程序結束時跟著結束（結果未知）
    /// </summary>
    private readonly List<(string ToolId, AgentBackgroundTool Tool)> backgroundCommands = new();

    /// <summary>程序結束或關掉時一起結束的背景指令：PublishBackground 把它們的卡片結束成「被中斷、結果未知」</summary>
    private readonly List<string> droppedBackground = new();

    /// <summary>
    /// 使用者按了停止（StopBackgroundWorkAsync）、還沒收到 item/completed 的指令（工具卡 id）：結束時卡片改成「已停止」、
    /// 背景指令結束的事件帶 StoppedByUser（中樞不接續）。要在送出停止之前記下：item/completed 可能比回應先到
    /// </summary>
    private readonly HashSet<string> stoppingCommands = new(StringComparer.Ordinal);

    /// <summary>上次發出的背景工作（AgentBackgroundWorkEvent 的工作數與工具 id；只在 safePointOrder 內讀寫）</summary>
    private List<string> reportedBackground = new();
    private int reportedBackgroundCount;

    /// <summary>讓安全點事件依狀態改變的先後發出（程序讀取執行緒與回應卡片的 UI 執行緒都會改狀態）；不能在 gate 內取用</summary>
    private readonly object safePointOrder = new();
    private AgentProcess? process;
    private CodexEventParser parser = new();
    private string? threadId;
    private string? turnId;
    private bool running;

    /// <summary>
    /// 執行中的這一輪是閒置時 codex 自己開始的（持續目標的下一輪，AgentIdleTurnStartedEvent）：中樞還沒收到事件就送出的訊息可以接手它
    /// （turn/start 併進進行中的那一輪）。只在 running 時有意義
    /// </summary>
    private bool cliTurn;
    private bool interruptRequested;
    private bool interruptSent;
    private bool? sandboxReady;
    private string? processSignature;

    /// <summary>
    /// 使用者目前選的權限模式（SendAsync、接續的設定，或執行中 SetPermissionModeAsync 改的；已 Normalize）。完全自動時核准請求一律由宿主直接放行：
    /// 核准政策在 turn/start 就定了，執行中才切到完全自動的這一輪、以及子代理（建立時繼承當時那一輪的政策）都還會送核准請求
    /// </summary>
    private AgentPermissionMode permissionMode = AgentPermissionMode.Ask;

    /// <summary>目前程序啟動時的執行檔身分（AgentSessionSettings.Cli）；不明為 null</summary>
    private CliBinaryIdentity? processCli;

    /// <summary>目前程序 initialize 回報的 Codex 版本與 CODEX_HOME</summary>
    private string? runningVersion;
    private string? processCodexHome;

    /// <summary>目前 thread 的 rollout（thread/start／thread/resume 回應的 thread.path）；lastRollout 跨程序保留，下次續接同一個 thread 時截尾用</summary>
    private string? rolloutPath;
    private (string ThreadId, string Path)? lastRollout;
    private bool fallbackNotified;
    private TaskCompletionSource<(bool Success, string? Error)>? sandboxSetup;

    /// <summary>
    /// 這一輪準備期間（啟動程序、檢查沙箱、建立或續接對話）的取消來源：還沒送出 turn/start 時按停止就取消，不必等準備做完。
    /// 不 Dispose：InterruptAsync 可能在鎖外取消它（沒有計時器，不用釋放）
    /// </summary>
    private CancellationTokenSource? preparing;

    /// <summary>SendAsync 準備這一輪的期間（到 turn/start 有回應為止）：RestartAsync 等它做完才判斷，使用者剛送的訊息不會丟掉</summary>
    private TaskCompletionSource? preparationDone;

    /// <summary>進行中的 RestartAsync（同時只有一個）</summary>
    private RestartOperation? restart;

    /// <summary>「停下待接續」的一輪所在的 thread（RestartAsync(Settings: null) 停下執行中的一輪後記住，之後帶 Settings 的 RestartAsync 接續）</summary>
    private string? suspendedThread;

    /// <summary>上次發出的安全點狀態（只在 safePointOrder 內讀寫）；一開始是閒置的狀態，沒有改變就不發</summary>
    private AgentSafePointChangedEvent reportedSafePoint = new(false, false, false);
    private long nextRequestId;
    private bool disposed;

    public event Action<AgentEvent>? EventReceived;

    public AgentProvider Provider => AgentProvider.Codex;

    /// <summary>為了重開送出 turn/interrupt 後，等這一輪結束（turn/completed）的上限；逾時就直接關程序、必要時結束程序樹（測試可縮短）</summary>
    internal TimeSpan RestartInterruptTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>重開時關 stdin 後等 app-server 自己結束的上限（實測 0.05 秒、偶爾 1.8 秒），逾時結束程序樹（測試可縮短）</summary>
    internal TimeSpan RestartCloseGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>確認舊程序樹（含 turn/interrupt 不會停的執行中指令）都已結束的上限；還有殘留就逐一結束再等 2 秒</summary>
    internal TimeSpan RestartTreeExitTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>插入訊息時這一輪還在準備（啟動程序、沙箱、建立對話、turn/start 還沒回應），最多等多久；逾時就不插入（中樞改排進佇列）</summary>
    internal TimeSpan SteerReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>rollout 截尾時切下的半行存放處；null＝AppPaths.PartialTailDirectory（測試指到自己的暫存資料夾）</summary>
    internal string? TailBackupDirectory { get; init; }

    /// <summary>
    /// 一輪正在進行：SendAsync 起（含準備期間），或重開後接續的一輪開始起（AgentTurnStartedByCliEvent），
    /// 或閒置時 codex 自己開始的一輪（持續目標的下一輪，AgentIdleTurnStartedEvent）開始起，到 TurnCompleted 為止。
    /// 重開停下舊程序的期間，被打斷的那一輪仍算執行中；RestartAsync 回 Restarted／Stopped／Failed 後為 false（那一輪交給中樞收尾，
    /// 停下待接續的一輪也不算執行中：使用者送了別的訊息就照常送出）
    /// </summary>
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

    /// <summary>目前程序 initialize 回應 userAgent 裡的 Codex 版本（CliVersionRules.ParseCodexUserAgent）；沒有程序或還沒回報為 null</summary>
    public string? RunningVersion
    {
        get
        {
            lock (gate)
                return process != null ? runningVersion : null;
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

    /// <summary>目前程序的 PID（整合測試擷取程序樹，確認重開後舊指令都已結束）；沒有程序為 null</summary>
    internal int? CurrentProcessId
    {
        get
        {
            lock (gate)
                return process?.ProcessId;
        }
    }

    /// <summary>
    /// 現在重開不會打斷工作：沒有背景指令（一輪結束後還在跑的、這一輪讓出給模型的）；執行中時另外要沒有工具在執行（item/started 的
    /// commandExecution／fileChange／mcpToolCall 到 item/completed，子代理的也算，已讓出的指令除外）、也沒有未回應的請求（核准、問卷、MCP 工具核准）
    /// </summary>
    public bool IsAtSafePoint
    {
        get
        {
            lock (gate)
                return CurrentSafePointLocked().IsAtSafePoint;
        }
    }

    /// <summary>
    /// 有背景指令（一輪正常結束時還在跑的指令，見 backgroundCommands；這一輪中途已讓出給模型的指令，見 yieldedCommands）：
    /// 程序重開或回收會連同它結束
    /// </summary>
    public bool HasLiveBackgroundWork
    {
        get
        {
            lock (gate)
                return backgroundCommands.Count > 0 || yieldedCommands.Count > 0;
        }
    }

    /// <summary>背景指令的說明（同那張工具卡；一輪結束後還在跑的在前、這一輪讓出的在後，各依開始的順序；子代理的沒有卡片、不列）</summary>
    public IReadOnlyList<AgentBackgroundTool> LiveBackgroundTools
    {
        get
        {
            lock (gate)
                return LiveBackgroundLocked().Select(b => b.Tool).ToList();
        }
    }

    /// <summary>有工具卡的背景指令：一輪結束後還在跑的，加上這一輪讓出給模型的主對話指令（turnCommands 有它的說明）</summary>
    private List<(string ToolId, AgentBackgroundTool Tool)> LiveBackgroundLocked()
    {
        var live = backgroundCommands.ToList();
        foreach (var yielded in yieldedCommands.Values)
        {
            if (turnCommands.TryGetValue(yielded.ItemId, out var tool) && !live.Any(b => b.ToolId == yielded.ItemId))
                live.Add((yielded.ItemId, tool));
        }
        return live;
    }

    /// <summary>
    /// 強制停止背景指令（stopId 是工具卡 id，null＝主對話的全部）。0.164.0 實測（codex_background_probe.py --mode stop）：
    /// 單獨一個以 thread/backgroundTerminals/list 的 itemId 找到 processId 再 terminate，全部以 clean；那個指令 0.05 秒內 item/completed
    /// （failed、結束碼 -1）、檔案沒有寫出、codex 不會自己開一輪。這兩個是實驗性的方法（initialize 帶 experimentalApi）
    /// </summary>
    public async Task<AgentBackgroundStopResult> StopBackgroundWorkAsync(string? stopId)
    {
        AgentProcess target;
        string thread;
        List<string> toolIds;
        int count;
        lock (gate)
        {
            if (disposed || process == null || process.HasExited)
                return new AgentBackgroundStopResult(0, "Codex 程序已經結束，背景指令也跟著結束了");
            if (restart != null)
                return new AgentBackgroundStopResult(0, "對話正在以新版 CLI 重新啟動，稍後再試");
            toolIds = LiveBackgroundLocked().Select(b => b.ToolId).Where(id => stopId == null || id == stopId).ToList();
            count = stopId == null ? backgroundCommands.Count + yieldedCommands.Count : toolIds.Count;
            if (threadId == null || count == 0)
                return new AgentBackgroundStopResult(0, "這個背景指令已經結束了");
            target = process;
            thread = threadId;
            foreach (var id in toolIds)
                stoppingCommands.Add(id);
        }

        string? error;
        if (stopId == null)
        {
            (_, error) = await RequestAsync(target, "thread/backgroundTerminals/clean", new JObject { ["threadId"] = thread }, StopBackgroundTimeout).ConfigureAwait(false);
        }
        else
        {
            var (listed, listError) = await RequestAsync(target, "thread/backgroundTerminals/list", new JObject { ["threadId"] = thread }, StopBackgroundTimeout).ConfigureAwait(false);
            var processId = CodexAppServerRules.FindBackgroundTerminal(listed, stopId);
            if (processId == null)
            {
                error = listError ?? "這個背景指令已經結束了";
            }
            else
            {
                var (terminated, terminateError) = await RequestAsync(target, "thread/backgroundTerminals/terminate",
                    new JObject { ["threadId"] = thread, ["processId"] = processId }, StopBackgroundTimeout).ConfigureAwait(false);
                error = terminateError ?? (CodexAppServerRules.TerminatedNothing(terminated) ? "這個背景指令已經結束了" : null);
            }
        }
        if (error != null)
        {
            lock (gate)
            {
                foreach (var id in toolIds)
                    stoppingCommands.Remove(id);
            }
            Log.Info($"Codex 沒有停止背景指令：{error}");
            return new AgentBackgroundStopResult(0, error);
        }
        Log.Info($"Codex 停止背景指令：{count} 個");
        return new AgentBackgroundStopResult(count, null);
    }

    /// <summary>需要重開程序的設定組合：帳戶、執行檔、客製設定、帳戶委派的 MCP（執行檔身分另外比，見 CodexAppServerRules.NeedsNewProcess）</summary>
    internal static string Signature(AgentSessionSettings settings)
        => $"{settings.AccountKey}|{settings.Launcher?.FileName}|{settings.UseLocalCustomizations}|{settings.DelegationMcpPipeName}";

    private async Task SendCoreAsync(AgentTurnInput input, AgentSessionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(settings);

        AgentProcess? current;
        AgentProcess? stale = null;
        var preparation = new CancellationTokenSource();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // 以新版重開進行中（停下舊程序、續接、送接續訊息）：中樞把訊息放回佇列，重開做完後再送（被打斷的那一輪這時可能還算執行中）
            if (restart != null)
                throw new AgentRestartInProgressException();
            // 閒置時 codex 自己開始的一輪還在進行（持續目標的下一輪緊接在上一輪結束後開始，中樞還沒收到事件就接著送了佇列裡的訊息）：
            // 這則接手成為這一輪，turn/start 併進進行中的那一輪（0.164.0 實測），那一輪的 turn/completed 就是這則的結束。
            // 要換程序或還沒有 thread 就不能接手（會打斷那一輪）
            bool takeOver = running && cliTurn && process != null && !process.HasExited && threadId != null
                && !CodexAppServerRules.NeedsNewProcess(processSignature, processCli, Signature(settings), settings.Cli);
            if (running && !takeOver)
                throw new InvalidOperationException(cliTurn ? "代理還在處理持續目標的下一輪" : "代理還在處理上一則訊息");
            running = true;
            cliTurn = false;
            permissionMode = AgentPermissionModes.Normalize(AgentProvider.Codex, settings.PermissionMode);
            suspendedThread = null;   // 使用者送了別的：停下待接續的那一輪不再接續
            interruptRequested = false;
            interruptSent = false;
            if (!takeOver)
                turnId = null;   // 接手時留著進行中那一輪的 id：準備期間的插入與停止照樣對得到它
            preparing = preparation;
            preparationDone = prepared;
            if (process != null && (process.HasExited || CodexAppServerRules.NeedsNewProcess(processSignature, processCli, Signature(settings), settings.Cli)))
            {
                stale = process;
                process = null;
                threadId = null;
            }
            current = process;
        }

        var ct = preparation.Token;
        try
        {
            if (stale != null)
            {
                await stale.CloseAsync(TimeSpan.FromSeconds(5));
                stale.Dispose();
            }
            if (TryAbortInterrupted())
                return;

            current ??= await StartProcessAsync(settings, ct);
            ThrowIfDisposed();
            var specialInput = CodexControlRules.SpecialTurn(input.Text, string.Empty);
            if (specialInput != null && input.Attachments.Count > 0)
                throw new InvalidOperationException("審查與壓縮不接受附件，請先移除附件再執行");
            if (specialInput != null && settings.SessionStarted && settings.SessionId != null)
            {
                var existingGoal = await ControlRequestAsync(current, "thread/goal/get", new JObject { ["threadId"] = settings.SessionId });
                if (CodexAppServerRules.IsGoalActive(existingGoal["goal"] as JObject))
                    throw new InvalidOperationException("請先暫停持續目標，再執行審查或手動壓縮");
            }

            if (TryAbortInterrupted())
                return;

            if (AgentPermissionModes.Normalize(AgentProvider.Codex, settings.PermissionMode) != AgentPermissionMode.FullAccess && sandboxReady == null)
                sandboxReady = await EnsureSandboxAsync(settings.WorkingDirectory, ct);
            ThrowIfDisposed();
            if (TryAbortInterrupted())
                return;

            var policy = CodexAppServerRules.GetPolicy(settings.PermissionMode, sandboxReady ?? true);
            NotifyFallbackOnce(policy);

            string? thread;
            bool lostHistory = false;
            lock (gate)
                thread = threadId;
            if (thread == null)
                (thread, lostHistory) = await OpenThreadAsync(settings, policy, ct);
            ThrowIfDisposed();

            var (text, images) = BuildMessage(input);
            if (lostHistory && !string.IsNullOrWhiteSpace(settings.HistoryRecap))
                text = settings.HistoryRecap + text;
            var turnParams = CodexAppServerRules.BuildTurnStartParams(
                thread, text, images, policy, new[] { settings.ConversationDirectory }, NormalizeModel(settings.Model), settings.Effort);
            turnParams["cwd"] = settings.WorkingDirectory;
            if (NormalizeModel(settings.Model) is { } turnModel)
            {
                lock (gate)
                {
                    if (ReferenceEquals(process, current))
                        parser.MainModel = turnModel;
                }
            }

            if (TryAbortInterrupted())
                return;

            // turn/start 送出後不再取消（codex 可能已經開始這一輪）：之後按停止改送 turn/interrupt
            var special = CodexControlRules.SpecialTurn(input.Text, thread);
            JObject? result;
            string? error;
            if (special is { } operation)
            {
                await ApplyControlSettingsAsync(current, thread, settings, policy);
                if (TryAbortInterrupted()) return;
                (result, error) = await RequestAsync(current, operation.Method, operation.Params);
            }
            else
            {
                await ApplySelectedSkillsAsync(current, turnParams, settings);
                if (TryAbortInterrupted()) return;
                (result, error) = await RequestAsync(current, "turn/start", turnParams);
            }
            ThrowIfDisposed();
            if (error != null)
                throw new InvalidOperationException($"Codex 無法開始這一輪：{error}");

            AcceptStartedTurn(current, result);
        }
        catch
        {
            bool stopped;
            lock (gate)
            {
                // OnExited 已經以 TurnCompleted 回報這一輪（程序在準備期間或送出途中結束）：不再丟出例外，同一個失敗只回報一次
                if (!running && !disposed)
                    return;
                // 按了停止之後，準備中的請求被取消（或剛好失敗）：回報已停止，不是「送出失敗」
                stopped = interruptRequested && !disposed;
                if (!stopped)
                    running = false;
            }
            if (stopped)
            {
                TryAbortInterrupted();   // OnExited 搶先回報時這裡什麼都不做
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
                if (ReferenceEquals(preparationDone, prepared))
                    preparationDone = null;
            }
            prepared.TrySetResult();
            PublishSafePoint();
        }
    }

    /// <summary>訊息的文字（附件說明接在後面）與要直接給模型看的本機圖片</summary>
    private static (string Text, List<string> Images) BuildMessage(AgentTurnInput input)
    {
        var images = input.Attachments
            .SelectMany(a => a.ModelImagePaths)
            .Where(File.Exists)
            .Take(MaxImagesPerMessage)
            .ToList();
        return ((input.Text ?? string.Empty) + AgentAttachmentRules.BuildAttachmentNote(input.Attachments), images);
    }

    /// <summary>
    /// 插入訊息（實測見 docs/protocol-notes.md「執行中插入訊息」）：turn/steer 帶這一輪的 turnId，codex 把它併進進行中的這一輪
    /// （指令執行中或產生文字中都一樣，這一輪仍只有一個 turn/completed；插入的訊息成為這一輪的 userMessage 項目，不轉成事件）。
    /// turn/start 還沒有回應（準備中）時先等它；重開中、按了停止、codex 回錯誤（這一輪剛結束：-32600 no active turn to steer；
    /// /review、/compact 這類不接受插入的一輪）回 false
    /// </summary>
    public async Task<bool> SteerAsync(AgentTurnInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Text.Trim() == "/compact" || input.Text.Trim() == "/review" || input.Text.TrimStart().StartsWith("/review ", StringComparison.Ordinal)) return false;
        Task? preparation;
        lock (gate)
        {
            if (!CanSteerLocked())
                return false;
            preparation = turnId == null ? preparationDone?.Task : null;
            if (turnId == null && preparation == null)
                return false;
        }
        if (preparation != null)
        {
            try
            {
                await preparation.WaitAsync(SteerReadyTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        var (text, images) = BuildMessage(input);
        AgentProcess target;
        string thread;
        string turn;
        lock (gate)
        {
            // 送出之後就取消不了：最後一次檢查取消
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanSteerLocked() || process == null || threadId == null || turnId == null)
                return false;
            target = process;
            thread = threadId;
            turn = turnId;
        }

        var (_, error) = await RequestAsync(target, "turn/steer", CodexAppServerRules.BuildTurnSteerParams(thread, turn, text, images));
        if (error != null)
            Log.Info($"Codex 沒有接受插入的訊息：{error}");
        return error == null;
    }

    /// <summary>可以插入訊息：這一輪在執行、沒按停止、沒有在重開</summary>
    private bool CanSteerLocked() => !disposed && running && restart == null && !interruptRequested;

    /// <summary>turn/start 有回應了：記下這一輪的 id；準備期間就按了停止的，現在補送 turn/interrupt</summary>
    private void AcceptStartedTurn(AgentProcess current, JObject? result)
    {
        var startedTurn = (result?["turn"] as JObject)?["id"]?.ToString();
        string? interruptThread = null;
        string? interruptTurn = null;
        lock (gate)
        {
            turnId ??= startedTurn;
            if (interruptRequested && !interruptSent && threadId != null && turnId != null && ReferenceEquals(process, current))
            {
                interruptSent = true;
                interruptThread = threadId;
                interruptTurn = turnId;
            }
        }

        if (interruptTurn != null)
            SendInterrupt(current, interruptThread!, interruptTurn);
    }

    private void NotifyFallbackOnce(CodexPolicy policy)
    {
        if (policy.FallbackNotice == null || fallbackNotified)
            return;
        fallbackNotified = true;
        Raise(new AgentNoticeEvent(policy.FallbackNotice, IsError: false));
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
            if (!interruptRequested)
                return false;
            running = false;
            interruptRequested = false;
        }

        Raise(new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null));
        return true;
    }

    private void SendInterrupt(AgentProcess target, string thread, string turn)
    {
        _ = RequestAsync(target, "turn/interrupt", new JObject { ["threadId"] = thread, ["turnId"] = turn });
        _ = Task.Delay(InterruptWatchdog).ContinueWith(_ =>
        {
            bool stuck;
            lock (gate)
                stuck = running && interruptRequested && ReferenceEquals(process, target);
            if (stuck)
                target.Kill();
        }, TaskScheduler.Default);
    }

    internal static string? NormalizeModel(string? model)
        => string.IsNullOrWhiteSpace(model) || model.Equals("default", StringComparison.OrdinalIgnoreCase) ? null : model;

    private async Task<AgentProcess> StartProcessAsync(AgentSessionSettings settings, CancellationToken ct)
    {
        var launcher = settings.Launcher
            ?? throw new AgentCliUnavailableException(AgentProvider.Codex, "這台電腦尚未安裝 Codex CLI，請到「設定 → CLI」安裝");

        // 工作資料夾由中樞負責（預設 workspace 會先建好）；使用者選的資料夾不存在就明確回報，不偷偷重建
        if (!Directory.Exists(settings.WorkingDirectory))
            throw new DirectoryNotFoundException($"工作資料夾不存在：{settings.WorkingDirectory}");
        var mcpServers = settings.UseLocalCustomizations
            ? new List<string>()
            : CodexConfigRules.ParseMcpServerNames(CodexConfigRules.ReadConfig(settings.CodexHome));
        // 隔離帳戶共用系統預設帳戶的設定：同步全域指示與連結（背景執行緒），帶上共用的 MCP 伺服器
        var shared = await settings.PrepareSharedAsync();
        var serverArguments = CodexAppServerRules.BuildServerArguments(settings.UseLocalCustomizations, mcpServers, shared.CodexConfigOverrides);
        // 帳戶委派（使用者 @ 過帳戶的對話）：以 -c 掛上 myriagent_accounts MCP，不寫進帳戶的 config.toml；精簡模式不載入 MCP
        if (settings.UseLocalCustomizations && settings.DelegationMcpPipeName != null)
            DelegationMcpRules.AddCodexArguments(serverArguments, settings.DelegationMcpPipeName);
        // CLI 換檔的空窗（npm 從退役到解壓完成之間）啟動會暫時找不到執行檔：以 0.5／1／2 秒退避重試
        var started = await AgentProcess.StartWithRetryAsync(
            launcher,
            serverArguments,
            settings.WorkingDirectory,
            settings.ConfigureEnvironment,
            ct);
        started.LineReceived += OnLine;
        started.Exited += OnExited;
        lock (gate)
        {
            if (disposed)
            {
                started.Dispose();
                throw new ObjectDisposedException(nameof(CodexAgentSession));
            }

            process = started;
            processSignature = Signature(settings);
            processCli = settings.Cli;
            parser = new CodexEventParser();
            threadId = null;
            sandboxReady = null;
            // 舊程序設定到一半的沙箱（按了停止後程序被關掉或換掉）跟著作廢，新程序重新檢查
            sandboxSetup?.TrySetResult((false, "Codex 程序已重開"));
            sandboxSetup = null;
            ResetProcessStateLocked();
        }

        // 在讀這個程序的輸出之前發：中樞據此作廢其他實例的待回應卡片，之後建立的卡片都屬於這個實例
        Raise(new AgentProcessStartedEvent(started.InstanceId, settings.Cli));
        PublishSafePoint();
        started.BeginReading();

        var version = AppVersionInfo.VersionText;
        try
        {
            var (init, error) = await RequestAsync(started, "initialize", CodexAppServerRules.BuildInitializeParams(version), ct: ct);
            if (error != null)
                throw new InvalidOperationException($"Codex 初始化失敗：{error}{Environment.NewLine}{started.GetStderrTail(400)}");
            ReportInitialize(started, init);
        }
        catch
        {
            // 初始化失敗，或準備期間按了停止：還沒初始化完的程序不能留給下一則訊息用
            lock (gate)
            {
                if (ReferenceEquals(process, started))
                    process = null;
            }
            started.Kill();
            throw;
        }

        await started.WriteLineAsync(CodexAppServerRules.BuildNotification("initialized"));
        _ = LoadModelsAsync(started);
        return started;
    }

    /// <summary>換程序或程序結束時：屬於那個程序的狀態一律作廢（請求 id 每個程序各自從 0 數，舊的待回應請求不能留給新程序）</summary>
    private void ResetProcessStateLocked()
    {
        pendingRequests.Clear();
        requestKeys.Clear();
        runningTools.Clear();
        yieldedCommands.Clear();
        // 背景指令跟著程序結束：卡片由 PublishBackground 結束成「被中斷、結果未知」
        turnCommands.Clear();
        droppedBackground.AddRange(backgroundCommands.Select(b => b.ToolId));
        backgroundCommands.Clear();
        stoppingCommands.Clear();
        runningVersion = null;
        processCodexHome = null;
        rolloutPath = null;
    }

    /// <summary>initialize 回應：記下程序回報的版本（第一次回報或改變時發 AgentRunningVersionEvent）與 CODEX_HOME</summary>
    private void ReportInitialize(AgentProcess source, JObject? result)
    {
        var (version, home) = CodexAppServerRules.ParseInitializeResult(result);
        bool report;
        lock (gate)
        {
            if (!ReferenceEquals(process, source))
                return;
            report = version != null && !string.Equals(version, runningVersion, StringComparison.Ordinal);
            runningVersion = version;
            processCodexHome = home;
        }
        if (report)
            Raise(new AgentRunningVersionEvent(source.InstanceId, version!));
    }

    private async Task LoadModelsAsync(AgentProcess target)
    {
        try
        {
            var (result, error) = await RequestAsync(target, "model/list", new JObject());
            if (error == null)
            {
                var models = CodexAppServerRules.ParseModels(result);
                if (models.Count > 0)
                    Raise(new AgentModelsEvent(models));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }

    private async Task<bool> EnsureSandboxAsync(string workingDirectory, CancellationToken ct)
    {
        AgentProcess? target;
        TaskCompletionSource<(bool Success, string? Error)>? setup;
        lock (gate)
        {
            target = process;
            // 上一輪設定到一半就按了停止：codex 在背景照樣設定，等它做完就好，不再送一次設定
            setup = sandboxSetup is { Task.IsCompleted: false } inFlight ? inFlight : null;
        }
        if (target == null)
            return false;

        if (setup == null)
        {
            var (result, error) = await RequestAsync(target, "windowsSandbox/readiness", null, TimeSpan.FromSeconds(20), ct);
            if (error != null)
                return true;   // 不支援這個查詢：交給 Codex 自己處理
            switch (CodexAppServerRules.ClassifySandboxReadiness(result?["status"]?.ToString()))
            {
                case CodexSandboxReadiness.Ready:
                    return true;
                case CodexSandboxReadiness.UpdateRequired:
                    // 使用者設過 elevated 沙箱：自動以 unelevated 設定成功後 codex 會改寫 config.toml，這台電腦所有 Codex 前端都會跟著變
                    Raise(new AgentNoticeEvent("這台電腦的 Codex 沙箱（系統管理員模式）需要更新，請在 Codex 桌面版或 CLI 重新設定；Myriagent 不會自動改成免系統管理員模式（那會改寫 Codex 的設定）", IsError: false));
                    return false;
                case CodexSandboxReadiness.Unknown:
                    return false;
            }

            ct.ThrowIfCancellationRequested();
            Raise(new AgentNoticeEvent("正在設定這台電腦的 Codex 沙箱（免系統管理員模式，會寫入 Codex 的 config.toml；第一次使用需要一點時間）…", IsError: false));
            setup = new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
                sandboxSetup = setup;

            // 不帶 ct：登記了等待就一定要送出（codex 收到馬上回應、設定在背景跑）；按停止只取消後面的等待
            var (started, setupError) = await RequestAsync(target, "windowsSandbox/setupStart",
                new JObject { ["mode"] = "unelevated", ["cwd"] = workingDirectory });
            if (setupError != null || !(started?.Value<bool?>("started") ?? false))
            {
                setup.TrySetResult((false, setupError));
                return false;
            }
        }

        (bool Success, string? Error) outcome;
        try
        {
            outcome = await setup.Task.WaitAsync(SandboxSetupTimeout, ct);
        }
        catch (TimeoutException)
        {
            return false;
        }
        if (!outcome.Success)
            Raise(new AgentNoticeEvent($"Codex 沙箱設定失敗：{outcome.Error}", IsError: false));
        return outcome.Success;
    }

    /// <summary>建立或續接 thread；LostHistory＝原本的對話續接不了、改開了新的（呼叫端要補前文節錄）</summary>
    private async Task<(string ThreadId, bool LostHistory)> OpenThreadAsync(AgentSessionSettings settings, CodexPolicy policy, CancellationToken ct)
    {
        AgentProcess target;
        string? home;
        lock (gate)
        {
            target = process ?? throw new IOException("Codex 程序已結束");
            home = processCodexHome;
        }

        var model = NormalizeModel(settings.Model);
        bool lostHistory = false;
        if (settings.SessionStarted && !string.IsNullOrWhiteSpace(settings.SessionId))
        {
            // app 或電腦當機後第一次續接、自己的程序寫到一半就結束（當掉、停止的看門狗殺樹）後馬上重送：上一個程序可能把 rollout 寫到一半（設計 7.4.4）。
            // 不是自己剛確認結束的程序，最近還有寫入就先不動；檔尾是半行就等到安靜期滿再看一次，期間沒有新的寫入才截（不然續接會接在半行後面寫成永久壞行）
            var thread = settings.SessionId!;
            await TrimRolloutBeforeResumeAsync(thread, KnownRollout(thread), home ?? settings.CodexHome, ct);
            var (resumed, resumeError) = await RequestAsync(target, "thread/resume",
                CodexAppServerRules.BuildThreadResumeParams(thread, settings.WorkingDirectory, model, policy), ct: ct);
            if (resumeError == null)
            {
                RememberThread(target, thread, resumed);
                Raise(new AgentSessionStartedEvent(thread, resumed?["model"]?.ToString(), "resumed"));
                return (thread, false);
            }

            var failure = CodexAppServerRules.ClassifyResumeError(resumeError);
            if (failure == CodexResumeFailure.ActiveWriter)
            {
                // 同一個 CODEX_HOME 的其他程式正開著這個對話：另開新對話只帶得走節錄，等於悄悄分岔，請使用者先關掉那邊
                throw new InvalidOperationException("這個 Codex 對話正在其他程式（例如 VS Code 的 Codex 擴充或 Codex 桌面版）開啟中，請先在那邊關閉這個對話再送出");
            }

            ct.ThrowIfCancellationRequested();
            lostHistory = true;
            bool hasRecap = !string.IsNullOrWhiteSpace(settings.HistoryRecap);
            var reason = failure == CodexResumeFailure.NotFound
                ? $"找不到原本的 Codex 對話紀錄（{resumeError}）"
                : $"無法續接原本的 Codex 對話（{resumeError}）";
            Raise(new AgentNoticeEvent(
                reason + "，已改開新的對話"
                + (hasRecap ? "，並附上先前對話的節錄讓 Codex 了解前情。" : "；先前的內容 Codex 不會記得。"),
                IsError: false));
        }

        var (started, error) = await RequestAsync(target, "thread/start",
            CodexAppServerRules.BuildThreadStartParams(settings.WorkingDirectory, model, policy, settings.Instructions), ct: ct);
        var id = (started?["thread"] as JObject)?["id"]?.ToString();
        if (error != null || string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"Codex 無法建立對話：{error ?? "沒有回傳對話 id"}");

        RememberThread(target, id, started);
        Raise(new AgentSessionStartedEvent(id, started?["model"]?.ToString(), null));
        return (id, lostHistory);
    }

    /// <summary>thread 開好了（建立或續接）：記下主對話的 thread 與它的 rollout 位置</summary>
    private void RememberThread(AgentProcess source, string thread, JObject? response)
    {
        var path = CodexAppServerRules.ParseThreadPath(response);
        lock (gate)
        {
            if (!ReferenceEquals(process, source))
                return;
            threadId = thread;
            rolloutPath = path;
            if (path != null)
                lastRollout = (thread, path);
            // token 增量記在這個模型（turn/start 指定了模型時再換）
            if (CodexAppServerRules.StringOf(response?["model"]) is { Length: > 0 } model)
                parser.MainModel = model;
        }
    }

    private string? KnownRollout(string thread)
    {
        lock (gate)
            return lastRollout is { } known && string.Equals(known.ThreadId, thread, StringComparison.Ordinal) ? known.Path : null;
    }

    /// <summary>
    /// thread/resume 之前截尾：最近還有寫入、檔尾又是半行（例如自己的程序寫到一半被結束、使用者馬上重送）時，等到滿 RolloutQuietPeriod
    /// 再看一次——這段期間沒有新的寫入（沒有程式在寫）才截掉，還在寫就不動。可被停止取消（ct）
    /// </summary>
    private async Task TrimRolloutBeforeResumeAsync(string thread, string? knownPath, string? codexHome, CancellationToken ct)
    {
        var wait = await Task.Run(() => TrimRolloutTail(thread, knownPath, codexHome, confirmedNoWriter: false));
        if (wait is not { } delay)
            return;
        Log.Info($"Codex rollout 的尾行寫到一半、最近還有寫入：等 {delay.TotalSeconds:0.#} 秒確認沒有程式在寫再截尾");
        // 多等一點點：計時器的誤差不能讓第二次檢查差幾毫秒還不到安靜期
        await Task.Delay(delay + TimeSpan.FromMilliseconds(200), ct);
        await Task.Run(() => TrimRolloutTail(thread, knownPath, codexHome, confirmedNoWriter: false));
    }

    /// <summary>
    /// 截掉 rollout 寫到一半的尾行（設計 7.4.4，TranscriptTailRules）：rollout 位置用 Codex 回報過的 thread.path，沒有就到 CODEX_HOME\sessions 找。
    /// confirmedNoWriter＝寫這個檔的程序（自己的舊程序）剛確認都已結束；否則最近 RolloutQuietPeriod 內還有寫入就不動（可能有其他程式正在寫）。
    /// 任何失敗都只記錄、不擋續接。回傳：因為最近還有寫入而沒截、檔尾又是半行時，還要等多久才滿安靜期（其他情況 null）
    /// </summary>
    private TimeSpan? TrimRolloutTail(string thread, string? knownPath, string? codexHome, bool confirmedNoWriter)
    {
        try
        {
            var path = FindRolloutPath(thread, knownPath, codexHome);
            if (path == null)
                return null;
            if (!confirmedNoWriter && CodexAppServerRules.TrimQuietWait(File.GetLastWriteTimeUtc(path), DateTime.UtcNow) is { } wait)
                return TranscriptTailRules.EndsWithLineFeed(path) == false ? wait : null;
            var result = TranscriptTailRules.TrimPartialLine(path, TailBackupDirectory ?? AppPaths.PartialTailDirectory, thread, DateTime.Now);
            switch (result.Outcome)
            {
                case TranscriptTailOutcome.Trimmed:
                    Log.Info($"Codex rollout 截掉寫到一半的尾行：{path}（{result.OriginalLength} → {result.KeptLength} 位元組，備份 {result.BackupPath}）");
                    break;
                case TranscriptTailOutcome.Failed:
                    Log.Warn($"Codex rollout 截尾失敗（照常續接）：{path}：{result.Error}");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            Log.Warn($"找 Codex rollout 失敗（照常續接）：{ex.Message}");
        }
        return null;
    }

    private static string? FindRolloutPath(string thread, string? knownPath, string? codexHome)
    {
        if (knownPath != null && File.Exists(knownPath))
            return knownPath;
        if (!AgentAttachmentRules.IsLocalDrivePath(codexHome) || !CodexAppServerRules.IsSearchableThreadId(thread))
            return null;
        var sessions = Path.Combine(codexHome!, "sessions");
        if (!Directory.Exists(sessions))
            return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        var candidates = Directory.EnumerateFiles(sessions, CodexAppServerRules.RolloutSearchPattern(thread), options)
            .Select(path => (path, File.GetLastWriteTimeUtc(path)))
            .ToList();
        return CodexAppServerRules.PickRolloutPath(thread, candidates);
    }

    /// <summary>送一個請求等回應；ct 取消（準備期間按停止）時不送出或不再等，丟 OperationCanceledException</summary>
    private async Task<(JObject? Result, string? Error)> RequestAsync(AgentProcess target, string method, JObject? parameters, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref nextRequestId);
        var completion = new TaskCompletionSource<(JObject?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        responses[id] = (target, completion);
        try
        {
            await target.WriteLineAsync(CodexAppServerRules.BuildRequest(id, method, parameters));
        }
        catch (IOException)
        {
            responses.TryRemove(id, out _);
            return (null, $"Codex 程序已結束：{target.GetStderrTail(400)}");
        }

        try
        {
            return await completion.Task.WaitAsync(timeout ?? RequestTimeout, ct);
        }
        catch (TimeoutException)
        {
            responses.TryRemove(id, out _);
            return (null, $"{method} 逾時");
        }
        catch (OperationCanceledException)
        {
            // codex 那邊照常做完，晚到的回應沒有人收
            responses.TryRemove(id, out _);
            throw;
        }
    }

    private void OnLine(AgentProcess source, string line)
    {
        CodexEventParser currentParser;
        string? mainThread;
        bool stoppingForRestart;
        lock (gate)
        {
            if (!ReferenceEquals(source, process))
                return;
            currentParser = parser;
            mainThread = threadId;
            stoppingForRestart = restart is { } op && ReferenceEquals(op.Stopping, source);
        }

        if (!line.TrimStart().StartsWith('{'))
        {
            source.AppendStderr(line);
            return;
        }

        var result = currentParser.Parse(line, mainThread);
        if (result.Response is { } response)
        {
            if (response.Id.Type == JTokenType.Integer && responses.TryRemove(response.Id.Value<long>(), out var pending))
                pending.Completion.TrySetResult((response.Result, response.Error));
            return;
        }

        if (result.ServerRequest != null)
        {
            if (stoppingForRestart)
            {
                // 為了重開正在停下的程序又送來請求：程序馬上就要結束，不再顯示成卡片（婉拒；接續後 CLI 會以新的 request id 重新要求）
                _ = WriteQuietlyAsync(source, CodexAppServerRules.BuildResult(result.ServerRequest.Id, BuildDeclineResult(result.ServerRequest)));
                return;
            }
            HandleServerRequest(source, result.ServerRequest, CodexAppServerRules.IsOtherThread(result.ServerRequest.Params, mainThread));
            PublishSafePoint();
            return;
        }

        if (result.StartedTurnId != null)
        {
            string? interruptThread = null;
            AgentIdleTurnStartedEvent? idleTurn = null;
            lock (gate)
            {
                turnId = result.StartedTurnId;
                if (!running && restart == null && !disposed && ReferenceEquals(source, process))
                {
                    // 閒置時 codex 自己開始了一輪（持續目標的下一輪：上一輪結束後約 0.02 秒、或續接後立刻開始）：標成執行中，
                    // 不然畫面上回覆一直出來、對話卻不像在執行，也停不下來，換版還會把它當成閒置直接關掉程序
                    running = true;
                    cliTurn = true;
                    interruptRequested = false;
                    interruptSent = false;
                    idleTurn = new AgentIdleTurnStartedEvent(AfterBackgroundTask: false, ForThreadGoal: currentParser.GoalActive);
                }
                if (interruptRequested && !interruptSent && threadId != null)
                {
                    interruptSent = true;
                    interruptThread = threadId;
                }
            }
            if (idleTurn != null)
                Raise(idleTurn);
            if (interruptThread != null)
                SendInterrupt(source, interruptThread, result.StartedTurnId);
        }

        if (result.SandboxSetupCompleted is { } sandbox)
        {
            TaskCompletionSource<(bool, string?)>? setup;
            lock (gate)
                setup = sandboxSetup;
            setup?.TrySetResult((sandbox.Success, sandbox.Error));
        }

        bool yielded = false;
        if (result.ToolLifecycle.Count > 0 || result.CallEndedThread != null)
        {
            lock (gate)
            {
                if (ReferenceEquals(source, process))
                {
                    foreach (var tool in result.ToolLifecycle)
                    {
                        if (tool.Started)
                        {
                            runningTools[tool.Key] = tool;
                        }
                        else
                        {
                            runningTools.Remove(tool.Key);
                            yieldedCommands.Remove(tool.Key);
                        }
                    }
                    // 那個 thread 的一次模型呼叫結束了：還沒結束的指令已讓出給模型，改算背景工作（不再是「工具在執行」）
                    if (result.CallEndedThread is { } thread && running)
                    {
                        foreach (var tool in runningTools.Values.Where(t => t.Command && t.Thread == thread).ToList())
                        {
                            runningTools.Remove(tool.Key);
                            yieldedCommands[tool.Key] = tool;
                            yielded = true;
                        }
                    }
                }
            }
        }

        bool safePointMayChange = result.ToolLifecycle.Count > 0 || result.TurnCompleted != null || yielded;
        if (stoppingForRestart)
        {
            // 為了重開停下的程序：被結束的指令會在 turn/completed(interrupted) 之後才送 item/completed(failed)——卡片改成被重開中斷、結果未知
            for (int i = 0; i < result.Events.Count; i++)
            {
                if (result.Events[i] is AgentToolCompletedEvent completed)
                    result.Events[i] = CodexAppServerRules.MarkInterruptedByRestart(completed);
            }
        }
        lock (gate)
        {
            // 使用者停止的指令：thread/backgroundTerminals 結束的是 failed、結束碼 -1（0.164.0 實測）——卡片改成已停止，不像指令自己失敗了
            for (int i = 0; i < result.Events.Count && stoppingCommands.Count > 0; i++)
            {
                if (result.Events[i] is AgentToolCompletedEvent completed && stoppingCommands.Contains(completed.ToolId))
                    result.Events[i] = CodexAppServerRules.MarkStoppedByUser(completed);
            }
        }
        var finishedBackground = new List<AgentBackgroundCommandFinishedEvent>();
        foreach (var ev in result.Events)
        {
            if (ev is AgentRequestResolvedEvent resolved)
            {
                bool wasPending;
                lock (gate)
                    wasPending = pendingRequests.Remove(resolved.RequestKey);
                if (!wasPending)
                    continue;
                safePointMayChange = true;
            }
            else if (ev is AgentToolStartedEvent { ToolKind: "bash" } command)
            {
                // 主對話的指令（解析器只對主對話發工具事件）：這一輪結束時還在跑就是背景指令
                lock (gate)
                {
                    if (ReferenceEquals(source, process))
                        turnCommands[command.ToolId] = new AgentBackgroundTool(command.ToolName, command.ToolKind, command.Title, command.Detail, StopId: command.ToolId);
                }
            }
            else if (ev is AgentToolCompletedEvent done)
            {
                lock (gate)
                {
                    turnCommands.Remove(done.ToolId);
                    bool stoppedByUser = stoppingCommands.Remove(done.ToolId);
                    int index = backgroundCommands.FindIndex(b => b.ToolId == done.ToolId);
                    if (index >= 0 && ReferenceEquals(source, process))
                    {
                        var tool = backgroundCommands[index].Tool;
                        backgroundCommands.RemoveAt(index);
                        var (exitCode, duration) = CodexAppServerRules.ParseCommandOutcome(result.Notification?.Params);
                        finishedBackground.Add(new AgentBackgroundCommandFinishedEvent(done.ToolId, tool.Title, done.Status, exitCode, duration, stoppedByUser));
                    }
                }
            }
            Raise(ev);
        }

        if (finishedBackground.Count > 0)
        {
            // 背景指令跑完（這時通常沒有一輪在進行）：先更新背景工作，再交給中樞決定要不要接續
            PublishBackground();
            foreach (var finished in finishedBackground)
                Raise(finished);
            safePointMayChange = true;
        }

        if (result.TurnCompleted != null)
            CompleteTurn(source, result.TurnCompleted);

        // 這一輪的事件（工具結束、請求被撤銷、TurnCompleted）之後才發：中樞先處理完這些，再依新的安全點決定要不要重開
        if (safePointMayChange)
            PublishSafePoint();
    }

    /// <summary>
    /// 主對話這一輪結束（turn/completed）。為了重開送 turn/interrupt 而結束的（interrupted）不算這一輪結束：不發 TurnCompleted、
    /// 執行中旗標留給 RestartAsync 決定（接續或交給中樞收尾）；中斷生效前就自己結束的（completed、failed）照常回報、也不再接續
    /// </summary>
    private void CompleteTurn(AgentProcess source, AgentTurnCompletedEvent completed)
    {
        AgentTurnCompletedEvent final = completed;
        List<CodexServerRequest> leftover;
        bool report;
        lock (gate)
        {
            var op = restart;
            bool stoppingForRestart = op != null && ReferenceEquals(op.Stopping, source);
            report = !(stoppingForRestart && completed.Outcome == AgentTurnOutcome.Interrupted);
            if (report)
            {
                running = false;
                cliTurn = false;
                if (interruptRequested && final.Outcome != AgentTurnOutcome.Completed)
                    final = final with { Outcome = AgentTurnOutcome.Interrupted, ErrorMessage = null };
                if (stoppingForRestart)
                    op!.TurnFinishedOnItsOwn = true;
                else if (op is { ContinuationStarted: true })
                    op.ContinuationReported = true;
            }
            turnId = null;
            interruptRequested = false;
            interruptSent = false;
            runningTools.Clear();
            yieldedCommands.Clear();
            // 這一輪正常結束時還在跑的指令＝背景指令（unified_exec 的長時間指令會在之後才送 item/completed；這一輪中途已讓出的也在這裡）；
            // 停止、失敗、重開打斷的不算
            if (report && final.Outcome == AgentTurnOutcome.Completed && ReferenceEquals(source, process))
            {
                foreach (var (toolId, tool) in turnCommands)
                    backgroundCommands.Add((toolId, tool));
            }
            turnCommands.Clear();
            leftover = pendingRequests.Values.ToList();
            pendingRequests.Clear();
            foreach (var request in leftover)
                requestKeys.Remove(request.Key);
            if (stoppingForRestart)
                op!.TurnStopped.TrySetResult();
        }

        // codex 結束主對話這一輪時已撤銷主對話的請求；還留著的是子代理的核准或問卷——
        // 畫面上的卡片隨這一輪結束失效，回婉拒讓子代理不會一直等
        foreach (var request in leftover)
            _ = WriteQuietlyAsync(source, CodexAppServerRules.BuildResult(request.Id, BuildDeclineResult(request)));
        // 背景指令在 TurnCompleted 之前發：中樞結束這一輪時把它們的卡片留在執行中
        PublishBackground();
        if (report)
            Raise(final);
    }

    /// <param name="fromSubAgent">請求來自子代理（multi_agent）的 thread：一樣要顯示、可以回應（會擋住子代理，也擋住在等它的主代理），卡片註明是子代理</param>
    private void HandleServerRequest(AgentProcess source, CodexServerRequest request, bool fromSubAgent)
    {
        var p = request.Params;
        string Label(string toolName) => fromSubAgent ? $"{toolName}（子代理）" : toolName;
        switch (request.Method)
        {
            case "item/commandExecution/requestApproval":
            case "execCommandApproval":
            {
                if (TryAutoApprove(source, request, null, CodexAppServerRules.BuildSessionApprovalKey(request.Method, p)))
                    return;
                if (p["networkApprovalContext"] is JObject network)
                {
                    // 受管網路的連線核准：沒有指令也沒有工作資料夾，只有主機；「本對話都允許」只放行這個主機
                    var host = network.Value<string>("host") ?? "未知的主機";
                    var protocol = network.Value<string>("protocol");
                    // 指令已經在跑、連線時才問：CLI 重開時那個指令算被中斷（結果未知），不是沒執行
                    RaiseApproval(new AgentApprovalEvent(request.Key, Label("網路存取"), "web", $"連線到 {host}",
                        JoinLines(p.Value<string>("reason"), protocol != null ? $"通訊協定：{protocol}" : null), p.Value<string>("itemId"),
                        CanAllowForSession: true, AllowSessionLabel: $"本對話都允許連線到 {host}", ToolStarted: true));
                    return;
                }
                var command = request.Method == "execCommandApproval"
                    ? string.Join(" ", (p["command"] as JArray ?? new JArray()).Select(t => t.ToString()))
                    : CodexAppServerRules.DescribeCommand(p);
                var detail = JoinLines(p.Value<string>("reason"), command, p.Value<string>("cwd") is { } cwd ? $"工作資料夾：{cwd}" : null,
                    p["additionalPermissions"] is JObject extra ? $"額外權限：{extra.ToString(Newtonsoft.Json.Formatting.Indented)}" : null);
                RaiseApproval(new AgentApprovalEvent(request.Key, Label("執行指令"), "bash", ClaudeStreamRules.FirstLine(command), detail, p.Value<string>("itemId"),
                    CanAllowForSession: true, AllowSessionLabel: "本對話同樣的指令都允許"));
                return;
            }
            case "item/fileChange/requestApproval":
            case "applyPatchApproval":
            {
                if (TryAutoApprove(source, request, "fileChange", null))
                    return;
                var itemId = p.Value<string>("itemId") ?? p.Value<string>("callId");
                var (path, diff) = itemId != null ? parser.GetFileChange(itemId) : (null, null);
                var detail = JoinLines(p.Value<string>("reason"), p.Value<string>("grantRoot") is { } root ? $"要求寫入：{root}" : null);
                RaiseApproval(new AgentApprovalEvent(request.Key, Label("修改檔案"), "edit", path ?? "代理要修改檔案", string.IsNullOrWhiteSpace(detail) ? null : detail,
                    itemId, CanAllowForSession: true, IsPlan: false, path, diff, "本對話的檔案修改都不再詢問"));
                return;
            }
            case "item/permissions/requestApproval":
            {
                if (TryAutoApprove(source, request, null, CodexAppServerRules.BuildSessionApprovalKey(request.Method, p)))
                    return;
                var detail = JoinLines(p.Value<string>("reason"), p["permissions"]?.ToString(Newtonsoft.Json.Formatting.Indented));
                // 協定沒有說明要求權限時項目是否已開始執行：保守當成已經在跑（CLI 重開時算被中斷、照常檢查高風險），不宣稱它沒執行
                RaiseApproval(new AgentApprovalEvent(request.Key, Label("擴大權限"), "other", "代理要求更多權限（網路或檔案存取）", detail, p.Value<string>("itemId"),
                    CanAllowForSession: true, AllowSessionLabel: "本對話同樣的權限要求都允許", ToolStarted: true));
                return;
            }
            case "item/tool/requestUserInput":
            {
                var questions = CodexAppServerRules.ParseUserInputQuestions(p);
                if (questions.Count == 0)
                {
                    _ = WriteQuietlyAsync(source, CodexAppServerRules.BuildResult(request.Id, CodexAppServerRules.BuildUserInputResponse(new Dictionary<string, IReadOnlyList<string>>())));
                    return;
                }
                lock (gate)
                    pendingRequests[request.Key] = request;
                Raise(new AgentQuestionEvent(request.Key, questions, AnswerAsMessage: false));
                return;
            }
            case "mcpServer/elicitation/request":
            {
                if (CodexAppServerRules.ParseMcpToolApproval(p) is { } tool)
                {
                    // MCP 工具呼叫的核准（不是表單）：畫成核准卡；「本對話都允許」交給 Codex 自己記（_meta.persist），宿主只在完全自動時直接放行
                    if (TryAutoApprove(source, request, null, null))
                        return;
                    RaiseApproval(new AgentApprovalEvent(request.Key, Label($"MCP：{tool.ServerName}"), "mcp", tool.Title, tool.Detail, null,
                        CanAllowForSession: tool.CanAllowForSession, AllowSessionLabel: tool.CanAllowForSession ? "本對話都允許這個 MCP 工具" : null));
                    return;
                }
                _ = WriteQuietlyAsync(source, CodexAppServerRules.BuildResult(request.Id, CodexAppServerRules.BuildMcpElicitationResponse(AgentApprovalChoice.Deny)));
                Raise(new AgentNoticeEvent(CodexAppServerRules.DescribeUnsupportedElicitation(p), IsError: false));
                return;
            }
            default:
                _ = WriteQuietlyAsync(source, CodexAppServerRules.BuildError(request.Id, -32601, $"Clode Myriagent 不支援此請求：{request.Method}"));
                return;
        }
    }

    /// <summary>
    /// 不必問就直接放行：完全自動一律允許（只放行這一次，之後切回其他模式就照常詢問）；本對話已選過「都允許」的，
    /// kind 有值＝整類（只用於檔案修改）、exactKey 有值＝完全相同的請求。否則記為待回應
    /// </summary>
    private bool TryAutoApprove(AgentProcess source, CodexServerRequest request, string? kind, string? exactKey)
    {
        AgentApprovalChoice? choice;
        lock (gate)
        {
            choice = permissionMode == AgentPermissionMode.FullAccess ? AgentApprovalChoice.Allow
                : (kind != null && sessionAllowed.Contains(kind)) || (exactKey != null && sessionAllowedRequests.Contains(exactKey)) ? AgentApprovalChoice.AllowForSession
                : null;
            if (choice == null)
            {
                pendingRequests[request.Key] = request;
                if (exactKey != null)
                    requestKeys[request.Key] = exactKey;
            }
        }

        if (choice is { } allowed)
            _ = WriteQuietlyAsync(source, CodexAppServerRules.BuildResult(request.Id, BuildApprovalResult(request, allowed)));
        return choice != null;
    }

    /// <summary>
    /// 發出核准卡。記為待回應之後、卡片送到中樞之前切到完全自動的（SetPermissionModeAsync 那時請中樞放行，卡片還沒建立）：
    /// 卡片之後再請中樞放行一次（中樞依事件順序處理，這時卡片已經建立；已經放行過的會略過）
    /// </summary>
    private void RaiseApproval(AgentApprovalEvent approval)
    {
        Raise(approval);
        bool switchedMeanwhile;
        lock (gate)
            switchedMeanwhile = permissionMode == AgentPermissionMode.FullAccess && pendingRequests.ContainsKey(approval.RequestKey);
        if (switchedMeanwhile)
            Raise(new AgentAutoApproveEvent(approval.RequestKey));
    }

    private static JObject BuildApprovalResult(CodexServerRequest request, AgentApprovalChoice choice) => request.Method switch
    {
        "item/permissions/requestApproval" => CodexAppServerRules.BuildPermissionsResponse(request.Params, choice),
        "execCommandApproval" or "applyPatchApproval" => new JObject { ["decision"] = CodexAppServerRules.ChooseLegacyDecision(choice) },
        "mcpServer/elicitation/request" => CodexAppServerRules.BuildMcpElicitationResponse(choice),
        _ => new JObject { ["decision"] = CodexAppServerRules.ChooseDecision(request.Params["availableDecisions"] as JArray, choice) },
    };

    /// <summary>沒有人回應就收掉的請求（按停止、這一輪結束）：問卷回空答案，核准一律拒絕</summary>
    private static JObject BuildDeclineResult(CodexServerRequest request) => request.Method == "item/tool/requestUserInput"
        ? CodexAppServerRules.BuildUserInputResponse(new Dictionary<string, IReadOnlyList<string>>())
        : BuildApprovalResult(request, AgentApprovalChoice.Deny);

    private static string ApprovalKind(string method) => method switch
    {
        "item/fileChange/requestApproval" or "applyPatchApproval" => "fileChange",
        "item/permissions/requestApproval" => "permissions",
        _ => "command",
    };

    public async Task RespondApprovalAsync(string requestKey, AgentApprovalChoice choice, string? feedback)
    {
        var (request, target) = TakePending(requestKey);
        if (request == null || target == null)
            return;
        if (choice == AgentApprovalChoice.AllowForSession)
        {
            lock (gate)
            {
                // 檔案修改整類放行；指令與權限只放行完全相同的請求（一次同意不會變成任意指令都不再詢問）
                if (ApprovalKind(request.Method) == "fileChange")
                    sessionAllowed.Add("fileChange");
                else if (requestKeys.TryGetValue(requestKey, out var exact))
                    sessionAllowedRequests.Add(exact);
            }
        }
        lock (gate)
            requestKeys.Remove(requestKey);
        try
        {
            // 拒絕的說明 Codex 沒有欄位可放，由中樞在這一輪結束後當下一則訊息送出
            await target.WriteLineAsync(CodexAppServerRules.BuildResult(request.Id, BuildApprovalResult(request, choice)));
        }
        finally
        {
            // 回應送出之後才更新安全點：中樞不會在回應送到之前就重開
            PublishSafePoint();
        }
    }

    public Task RespondPlanAsync(string requestKey, AgentPlanChoice choice, string? feedback)
        => Task.CompletedTask;   // Codex 沒有計畫確認卡

    /// <summary>
    /// Codex 的核准政策與沙箱隨每一輪的 turn/start 帶上，這一輪中途改不了。只有切到完全自動做得到：之後的核准請求由宿主直接放行
    /// （TryAutoApprove），畫面上還在等待的核准卡請中樞放行（問卷照常等使用者回答）；其他模式從下一則訊息生效
    /// </summary>
    public Task<bool> SetPermissionModeAsync(AgentPermissionMode mode)
    {
        var normalized = AgentPermissionModes.Normalize(AgentProvider.Codex, mode);
        List<string> waiting;
        lock (gate)
        {
            if (disposed)
                return Task.FromResult(false);
            permissionMode = normalized;
            if (normalized != AgentPermissionMode.FullAccess)
                return Task.FromResult(false);
            waiting = pendingRequests.Values.Where(r => r.Method != "item/tool/requestUserInput").Select(r => r.Key).ToList();
        }

        foreach (var key in waiting)
            Raise(new AgentAutoApproveEvent(key));
        return Task.FromResult(true);
    }

    /// <summary>模型與推理強度隨每一輪的 turn/start 帶上（app-server 0.163.0 沒有一輪中途換的方法，turn/steer 只收輸入）：下一則訊息生效、不必重開程序</summary>
    public Task<bool> SetModelAsync(string? model, string? effort) => Task.FromResult(false);

    /// <summary>Codex 沒有 Remote Control</summary>
    public bool HasRemoteControl => false;

    public Task SetRemoteControlAsync(AgentRemoteControlOptions? options) => Task.CompletedTask;

    /// <summary>沒有外部看得到的工作階段名稱（只有 Claude 的 Remote Control 有）：不做事</summary>
    public Task RenameAsync(string title) => Task.CompletedTask;

    public Task<bool> ReattachRemoteControlAsync(AgentSessionSettings settings) => Task.FromResult(false);

    public async Task AnswerQuestionAsync(string requestKey, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        var (request, target) = TakePending(requestKey);
        if (request == null || target == null)
            return;
        try
        {
            await target.WriteLineAsync(CodexAppServerRules.BuildResult(request.Id, CodexAppServerRules.BuildUserInputResponse(answers)));
        }
        finally
        {
            PublishSafePoint();
        }
    }

    public async Task DeclineQuestionAsync(string requestKey)
    {
        var (request, target) = TakePending(requestKey);
        if (request == null || target == null)
            return;
        try
        {
            await target.WriteLineAsync(CodexAppServerRules.BuildResult(request.Id, CodexAppServerRules.BuildUserInputResponse(new Dictionary<string, IReadOnlyList<string>>())));
        }
        finally
        {
            PublishSafePoint();
        }
    }

    private (CodexServerRequest? Request, AgentProcess? Process) TakePending(string requestKey)
    {
        lock (gate)
        {
            if (process == null || !pendingRequests.Remove(requestKey, out var request))
                return (null, null);
            return (request, process);
        }
    }

    public async Task InterruptAsync()
    {
        List<CodexServerRequest> pending;
        AgentProcess? target;
        string? thread;
        string? turn;
        bool sendNow;
        CancellationTokenSource? preparation;
        lock (gate)
        {
            suspendedThread = null;   // 停下待接續的那一輪不再接續（由中樞收尾）
            if (restart is { ContinuationStarted: false } op)
            {
                // 重開途中按停止：不接續。舊程序本來就在停下，被打斷的那一輪由中樞收尾；
                // RestartAsync 還在等 SendAsync 準備這一輪時，照一般流程停下那一輪
                op.Cancelled = true;
                if (op.TookOver)
                    return;
            }
            if (!running)
                return;
            interruptRequested = true;
            pending = pendingRequests.Values.ToList();
            pendingRequests.Clear();
            target = process;
            thread = threadId;
            turn = turnId;
            sendNow = target != null && thread != null && turn != null && !interruptSent;
            if (sendNow)
                interruptSent = true;
            // 還沒送出 turn/start（準備中）：取消準備中的請求，不必等程序啟動、沙箱設定或建立對話做完
            preparation = turn == null ? preparing : null;
        }

        // 在鎖外取消：取消會同步執行等待中的接續
        preparation?.Cancel();
        PublishSafePoint();
        if (target == null)
            return;

        foreach (var request in pending)
            await WriteQuietlyAsync(target, CodexAppServerRules.BuildResult(request.Id, BuildDeclineResult(request)));

        if (sendNow)
            SendInterrupt(target, thread!, turn!);
    }

    private void OnExited(AgentProcess source, int? exitCode)
    {
        FailPendingResponses(source, "Codex 程序已結束");

        RestartOperation? stoppedForRestart = null;
        bool wasRunning = false;
        bool wasInterrupted = false;
        bool ownedByRestart;
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
                process = null;
                threadId = null;
                turnId = null;
                processCli = null;
                ResetProcessStateLocked();
                sandboxSetup?.TrySetResult((false, "Codex 程序已結束"));
            }

            // 重開接手這一輪、還沒開始接續的期間（停下舊程序、啟動新程序、續接）：程序的結束不回報成 TurnCompleted，
            // 被打斷的那一輪由 RestartAsync 接續或交給中樞收尾
            ownedByRestart = stoppedForRestart != null || restart is { TookOver: true, ContinuationStarted: false };
            if (!ownedByRestart)
            {
                wasRunning = running;
                wasInterrupted = interruptRequested;
                running = false;
                interruptRequested = false;
                interruptSent = false;
                if (wasRunning && restart is { ContinuationStarted: true } continuing)
                    continuing.ContinuationReported = true;
            }
        }

        var tail = source.GetStderrTail();
        if (wasRunning)
        {
            Raise(wasInterrupted
                ? new AgentTurnCompletedEvent(AgentTurnOutcome.Interrupted, null, null)
                : new AgentTurnCompletedEvent(AgentTurnOutcome.Failed, $"Codex 程序意外結束（結束碼 {exitCode?.ToString() ?? "不明"}）{(string.IsNullOrWhiteSpace(tail) ? string.Empty : "：" + tail)}", null));
        }

        // 中樞把這個實例的待回應卡片作廢：直接結束程序時 Codex 不會撤回待核准的請求（E-16）
        Raise(new AgentProcessExitedEvent(exitCode, tail, Expected: ownedByRestart || !wasRunning, source.InstanceId));
        PublishSafePoint();
        stoppedForRestart?.ExitHandled.TrySetResult();
        source.Dispose();
    }

    public async Task CloseAsync()
    {
        AgentProcess? target;
        lock (gate)
        {
            suspendedThread = null;
            if (restart != null)
                restart.Cancelled = true;
            target = process;
            process = null;
            threadId = null;
            running = false;
            processCli = null;
            ResetProcessStateLocked();
        }
        PublishSafePoint();

        if (target == null)
            return;
        try
        {
            await target.CloseAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            target.Dispose();
        }
    }

    // ───────────────────────────── CLI 熱更新：以新版重開 ─────────────────────────────

    /// <summary>
    /// 以新版 CLI 重開程序（熱更新契約 2.5 的流程表；設計 7.6）：
    /// 不在安全點（SafePoint 模式）回 Deferred；沒有程序也沒有停下待接續的一輪回 Restarted；
    /// 否則停下舊程序（這一輪在執行就 turn/interrupt、逾時才結束程序樹；thread/backgroundTerminals/clean；關 stdin；確認程序樹都已結束）、截尾，
    /// Settings=null 到此回 Stopped（被打斷的一輪記住停下待接續），不接續回 Restarted，
    /// 其他以 Settings 啟動新程序、thread/resume、發 AgentTurnStartedByCliEvent、turn/start 送接續訊息，回 RestartedAndContinued。
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
            if (restart != null || controlsBusy)
                return new AgentRestartResult(AgentRestartOutcome.Deferred);   // 另一個重開還在進行
            restart = op;
        }

        try
        {
            return await RestartCoreAsync(request, op).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 預期外的錯誤：回到閒置、沒有程序（被打斷的那一輪由中樞收尾）
            Log.Error("Codex 以新版重開失敗", ex);
            AgentProcess? leftover;
            lock (gate)
            {
                running = false;
                suspendedThread = null;
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
        // SendAsync 還在準備這一輪（啟動程序、沙箱、建立或續接對話、turn/start 還沒回應）：等它做完再判斷，使用者剛送的訊息才不會丟掉
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
        string? thread;
        string? turn;
        string? rollout;
        string? codexHome;
        bool turnRunning;
        lock (gate)
        {
            if (disposed)
                return new AgentRestartResult(AgentRestartOutcome.Failed, Error: "對話已關閉");
            bool live = process != null && !process.HasExited;
            switch (CodexAppServerRules.DecideRestartEntry(request.Mode, CurrentSafePointLocked().IsAtSafePoint, live, suspendedThread != null))
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
            thread = old != null ? threadId : null;
            turn = turnId;
            turnRunning = old != null && running;
            rollout = rolloutPath;
            codexHome = processCodexHome;
        }

        if (old != null)
        {
            Log.Info($"Codex 以新版重開（{request.Reason}，{request.FromVersion ?? "?"} → {request.ToVersion ?? "?"}）：停下程序 {old.InstanceId}"
                + (turnRunning ? "（這一輪執行中）" : string.Empty));
            var stopError = await StopForRestartAsync(old, thread, turnRunning ? turn : null, op).ConfigureAwait(false);
            if (stopError != null)
            {
                lock (gate)
                {
                    op.Stopping = null;
                    running = false;
                    suspendedThread = null;
                }
                Log.Warn($"Codex 重開失敗：{stopError}");
                return new AgentRestartResult(AgentRestartOutcome.Failed, op.PreviousInstance, Error: stopError);
            }

            // 舊程序（含它的指令）都確認結束了才截尾（設計 7.4.4）
            if (thread != null)
            {
                var home = codexHome ?? request.Settings?.CodexHome;
                await Task.Run(() => TrimRolloutTail(thread, rollout ?? KnownRollout(thread), home, confirmedNoWriter: true)).ConfigureAwait(false);
            }
        }

        string? resumeThread;
        AgentProcess? detached;
        AgentRestartResult? settled = null;
        lock (gate)
        {
            op.Stopping = null;
            detached = DetachStoppedProcessLocked(old);
            bool interrupted = (turnRunning && !op.TurnFinishedOnItsOwn) || suspendedThread != null;
            resumeThread = suspendedThread ?? thread;
            var followUp = disposed
                ? CodexRestartFollowUp.NoContinuation
                : CodexAppServerRules.DecideRestartFollowUp(request.Settings != null, interrupted && resumeThread != null, request.AutoContinue, op.Cancelled);
            switch (followUp)
            {
                case CodexRestartFollowUp.Stop:
                    settled = new AgentRestartResult(AgentRestartOutcome.Stopped, op.PreviousInstance);
                    break;
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
                suspendedThread = followUp == CodexRestartFollowUp.StopAndSuspend ? resumeThread : null;
            }
            else
            {
                // 接續：被打斷的那一輪繼續算執行中，接續的一輪照常以 TurnCompleted 結束
                suspendedThread = null;
                running = true;
                cliTurn = false;
                turnId = null;
                interruptRequested = false;
                interruptSent = false;
            }
        }

        ReportDetached(detached);
        if (settled != null)
            return settled;
        return await ContinueAfterRestartAsync(request, request.Settings!, resumeThread!, op).ConfigureAwait(false);
    }

    /// <summary>
    /// 停下要換掉的程序（設計 7.6、E-16）：這一輪在執行就送 turn/interrupt、等 turn/completed（上限 RestartInterruptTimeout），
    /// 試著清掉背景終端（thread/backgroundTerminals/clean，實驗性、不支援就略過），擷取程序樹後關 stdin（RestartCloseGrace 後結束程序樹），
    /// 再確認樹上的程序（含 turn/interrupt 不會停的執行中指令）都已結束——不確認就開新一輪，舊指令會和重跑的指令重疊〔RR〕。
    /// 回傳 null＝成功，否則是失敗原因
    /// </summary>
    private async Task<string?> StopForRestartAsync(AgentProcess old, string? thread, string? runningTurn, RestartOperation op)
    {
        // 先擷取一次程序樹：舊程序萬一在中斷途中自己結束，它開的指令就接不上之後擷取的樹
        var before = old.CaptureTree();
        if (thread != null && runningTurn != null)
        {
            _ = RequestAsync(old, "turn/interrupt", new JObject { ["threadId"] = thread, ["turnId"] = runningTurn });
            await Task.WhenAny(op.TurnStopped.Task, old.ExitTask, Task.Delay(RestartInterruptTimeout)).ConfigureAwait(false);
            if (!op.TurnStopped.Task.IsCompleted && !old.HasExited)
                Log.Warn($"Codex 在 {RestartInterruptTimeout.TotalSeconds:0.#} 秒內沒有結束被中斷的一輪，直接關閉程序（必要時結束程序樹）");
        }

        if (thread != null && !old.HasExited)
        {
            // 不支援（舊版沒有這個實驗性方法）或逾時都略過：關 stdin 後 app-server 結束時本來就會終止背景指令
            await RequestAsync(old, "thread/backgroundTerminals/clean", new JObject { ["threadId"] = thread }, BackgroundTerminalsCleanTimeout).ConfigureAwait(false);
        }

        var tree = old.CaptureTree();
        await old.CloseAsync(RestartCloseGrace).ConfigureAwait(false);
        bool gone = await WaitTreeExitAsync(tree).ConfigureAwait(false) & await WaitTreeExitAsync(before).ConfigureAwait(false);
        await Task.WhenAny(op.ExitHandled.Task, Task.Delay(ExitHandledTimeout)).ConfigureAwait(false);
        return gone ? null : "舊的 Codex 程序或它正在執行的指令沒有結束，為了避免和接續後重跑的指令重疊，沒有以新版重開";
    }

    /// <summary>等程序樹都結束；逾時就逐一結束還活著的再等 2 秒</summary>
    private async Task<bool> WaitTreeExitAsync(ProcessTreeSnapshot tree)
    {
        if (await tree.WaitForExitAsync(RestartTreeExitTimeout).ConfigureAwait(false))
            return true;
        int killed = tree.KillAlive();
        Log.Warn($"Codex 舊程序樹還有 {killed} 個程序沒有結束，已逐一結束");
        return await tree.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    /// <summary>
    /// 舊程序都結束了、它的結束事件卻還沒處理（stdout 被別的程序握著沒有讀完）：從會話拔掉，免得接續時被當成目前的程序；
    /// 結束事件由 ReportDetached 補發
    /// </summary>
    private AgentProcess? DetachStoppedProcessLocked(AgentProcess? old)
    {
        if (old == null || !ReferenceEquals(process, old))
            return null;
        process = null;
        threadId = null;
        turnId = null;
        processCli = null;
        ResetProcessStateLocked();
        return old;
    }

    private void ReportDetached(AgentProcess? detached)
    {
        if (detached == null)
            return;
        Raise(new AgentProcessExitedEvent(null, detached.GetStderrTail(), Expected: true, detached.InstanceId));
        detached.Dispose();
    }

    /// <summary>以 Settings 啟動新程序、續接被打斷的 thread，送接續訊息開始新的一輪（Codex 沒有自動接續，一定要宿主送）</summary>
    private async Task<AgentRestartResult> ContinueAfterRestartAsync(AgentRestartRequest request, AgentSessionSettings settings, string thread, RestartOperation op)
    {
        AgentProcess? fresh = null;
        lock (gate)
            permissionMode = AgentPermissionModes.Normalize(AgentProvider.Codex, settings.PermissionMode);
        try
        {
            fresh = await StartProcessAsync(settings, CancellationToken.None).ConfigureAwait(false);
            if (AgentPermissionModes.Normalize(AgentProvider.Codex, settings.PermissionMode) != AgentPermissionMode.FullAccess && sandboxReady == null)
                sandboxReady = await EnsureSandboxAsync(settings.WorkingDirectory, CancellationToken.None).ConfigureAwait(false);
            var policy = CodexAppServerRules.GetPolicy(settings.PermissionMode, sandboxReady ?? true);
            NotifyFallbackOnce(policy);
            var model = NormalizeModel(settings.Model);

            var (resumed, resumeError) = await RequestAsync(fresh, "thread/resume",
                CodexAppServerRules.BuildThreadResumeParams(thread, settings.WorkingDirectory, model, policy)).ConfigureAwait(false);
            if (resumeError != null)
            {
                // 改開新的 thread 沒有前文，接續訊息沒有意義：交給中樞收尾，使用者再送出時照一般流程（續接失敗就附前文節錄開新的）
                throw new InvalidOperationException(CodexAppServerRules.ClassifyResumeError(resumeError) == CodexResumeFailure.ActiveWriter
                    ? "這個 Codex 對話正在其他程式（例如 VS Code 的 Codex 擴充或 Codex 桌面版）開啟中，沒有自動接續"
                    : $"無法續接原本的 Codex 對話：{resumeError}");
            }
            RememberThread(fresh, thread, resumed);
            Raise(new AgentSessionStartedEvent(thread, resumed?["model"]?.ToString(), "resumed"));

            lock (gate)
            {
                // 接續的一輪指定了模型：token 增量記在這個模型
                if (model != null && ReferenceEquals(process, fresh))
                    parser.MainModel = model;
                if (op.Cancelled || disposed || !ReferenceEquals(process, fresh))
                {
                    // 重開途中按了停止或關閉：新程序已續接好（閒置），不送接續訊息
                    running = false;
                    return new AgentRestartResult(AgentRestartOutcome.Restarted, op.PreviousInstance, ReferenceEquals(process, fresh) ? fresh.InstanceId : null);
                }
                op.ContinuationStarted = true;
                running = true;
                cliTurn = false;
                turnId = null;
                interruptRequested = false;
                interruptSent = false;
            }

            // 在送出 turn/start 之前發：保證排在接續那一輪的任何事件之前（中樞據此把對話標成執行中）。
            // 接續訊息不產生使用者項目（Codex 的 userMessage 項目不轉成事件），中樞也不把它顯示成使用者訊息
            Raise(new AgentTurnStartedByCliEvent(AgentAutoTurnKind.Continuation));
            var turnParams = CodexAppServerRules.BuildTurnStartParams(
                thread, request.EffectiveContinuationText, Array.Empty<string>(), policy, new[] { settings.ConversationDirectory }, model, settings.Effort);
            turnParams["cwd"] = settings.WorkingDirectory;
            await ApplySelectedSkillsAsync(fresh, turnParams, settings).ConfigureAwait(false);
            var (result, turnError) = await RequestAsync(fresh, "turn/start", turnParams).ConfigureAwait(false);
            if (turnError != null)
                throw new InvalidOperationException($"Codex 無法開始接續的一輪：{turnError}");

            AcceptStartedTurn(fresh, result);
            Log.Info($"Codex 已以新版重開並接續（程序 {op.PreviousInstance ?? "無"} → {fresh.InstanceId}，版本 {RunningVersion ?? "不明"}）");
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
                    turnId = null;
                    interruptRequested = false;
                    interruptSent = false;
                }
            }
            if (reported)
                return new AgentRestartResult(AgentRestartOutcome.RestartedAndContinued, op.PreviousInstance, fresh?.InstanceId);

            // 任何一步失敗：回到閒置、沒有程序（剛啟動的新程序也關掉；它的結束不回報成 TurnCompleted——執行中旗標已清掉）
            if (fresh != null)
            {
                fresh.Kill();
                await Task.WhenAny(fresh.ExitTask, Task.Delay(FailedProcessExitTimeout)).ConfigureAwait(false);
            }
            if (cancelled)
                return new AgentRestartResult(AgentRestartOutcome.Restarted, op.PreviousInstance);
            Log.Warn($"Codex 以新版重開後接續失敗：{ex.Message}");
            return new AgentRestartResult(AgentRestartOutcome.Failed, op.PreviousInstance, Error: ex.Message);
        }
    }

    // ───────────────────────────── 安全點 ─────────────────────────────

    /// <summary>
    /// 目前的安全點旗標：工具與請求只在執行中算；背景指令（一輪結束後還在跑的指令）閒置時也算，這一輪讓出給模型的指令算背景工作、不算工具在執行
    /// </summary>
    private AgentSafePointChangedEvent CurrentSafePointLocked()
        => new(running && runningTools.Count > 0, running && pendingRequests.Count > 0,
            HasLiveBackgroundWork: backgroundCommands.Count > 0 || yieldedCommands.Count > 0);

    /// <summary>
    /// 背景指令有變就發 AgentBackgroundWorkEvent（帶要留在執行中的工具卡 id；去重），程序結束時一起結束的先把卡片結束成「被中斷、結果未知」。
    /// 這一輪讓出給模型的指令也算（子代理的沒有卡片，只算進工作數）：中樞據此不把它的卡片當成在等的工具。
    /// 不能在 gate 內呼叫：以 safePointOrder 讓事件依狀態改變的先後發出
    /// </summary>
    private void PublishBackground()
    {
        Debug.Assert(!Monitor.IsEntered(gate), "PublishBackground 不能在 gate 內呼叫");
        lock (safePointOrder)
        {
            List<string> dropped;
            List<(string ToolId, AgentBackgroundTool Tool)> now;
            int count;
            lock (gate)
            {
                dropped = droppedBackground.ToList();
                droppedBackground.Clear();
                now = LiveBackgroundLocked();
                count = backgroundCommands.Count + yieldedCommands.Count;
            }
            foreach (var toolId in dropped)
                Raise(new AgentToolCompletedEvent(toolId, AgentItemStatus.Cancelled, CodexAppServerRules.BackgroundInterruptedOutput, Array.Empty<string>()));
            var ids = now.Select(b => b.ToolId).ToList();
            if (count == reportedBackgroundCount && ids.SequenceEqual(reportedBackground, StringComparer.Ordinal))
                return;
            reportedBackground = ids;
            reportedBackgroundCount = count;
            Raise(new AgentBackgroundWorkEvent(count, now.Select(b => b.Tool).ToList(), ids));
        }
    }

    /// <summary>安全點的三個旗標有變就發 AgentSafePointChangedEvent（去重）。不能在 gate 內呼叫：以 safePointOrder 讓事件依狀態改變的先後發出</summary>
    private void PublishSafePoint()
    {
        Debug.Assert(!Monitor.IsEntered(gate), "PublishSafePoint 不能在 gate 內呼叫");
        // 程序結束、關掉、重開時背景指令跟著結束：這些路徑都會發安全點，背景工作在這裡一併更新
        PublishBackground();
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

    private void FailPendingResponses(AgentProcess? source, string error)
    {
        foreach (var (id, pending) in responses.ToArray())
        {
            if (source != null && !ReferenceEquals(pending.Process, source))
                continue;
            if (responses.TryRemove(id, out var removed))
                removed.Completion.TrySetResult((null, error));
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

    private static string JoinLines(params string?[] parts)
        => string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private void Raise(AgentEvent ev)
    {
        try
        {
            EventReceived?.Invoke(ev);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodexAgentSession] 事件處理失敗：{ex}");
        }
    }

    public void Dispose()
    {
        AgentProcess? target;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            suspendedThread = null;
            if (restart != null)
                restart.Cancelled = true;
            target = process;
            process = null;
            running = false;
            sandboxSetup?.TrySetResult((false, "對話已關閉"));
        }

        FailPendingResponses(null, "對話已關閉");
        target?.Dispose();
    }

    /// <summary>進行中的 RestartAsync（同一個會話同時只有一個）；欄位只在 gate 內讀寫</summary>
    private sealed class RestartOperation
    {
        /// <summary>舊程序的這一輪已結束（收到 turn/completed）</summary>
        public readonly TaskCompletionSource TurnStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>舊程序的結束已處理完（OnExited 發出了 AgentProcessExitedEvent）</summary>
        public readonly TaskCompletionSource ExitHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>正在停下的舊程序：它這一輪的 interrupted 與它的結束都不回報成 TurnCompleted，它新送來的請求不顯示</summary>
        public AgentProcess? Stopping;

        /// <summary>被停下的舊程序實例（結果的 PreviousProcessInstance）</summary>
        public string? PreviousInstance;

        /// <summary>重開已接手這一輪（等完 SendAsync 的準備、通過安全點檢查）：之後按停止＝不接續，交給中樞收尾</summary>
        public bool TookOver;

        /// <summary>這一輪在中斷生效前自己結束了（completed、failed，已照常回報 TurnCompleted）：不接續</summary>
        public bool TurnFinishedOnItsOwn;

        /// <summary>重開途中按了停止、關閉了程序或對話：不接續</summary>
        public bool Cancelled;

        /// <summary>接續的一輪已開始（已發 AgentTurnStartedByCliEvent）：之後按停止照一般流程中斷這一輪</summary>
        public bool ContinuationStarted;

        /// <summary>接續的一輪已以 TurnCompleted 回報結束（例如新程序在接續途中結束）</summary>
        public bool ContinuationReported;
    }
}
