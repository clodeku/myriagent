using System.Collections.Concurrent;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Speech;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 測試用的 UI 執行緒：模擬 WinForms 的單一 UI 執行緒——排進來的工作依序執行，async 的接續也回到這條執行緒。
/// 中樞與帳戶管理員的狀態只在 UI 執行緒讀寫（不加鎖），測試對它們的操作一律經 Invoke 送進來，才不會和合併送出的計時器搶著改。
/// </summary>
internal sealed class UiThread : IDisposable
{
    private readonly BlockingCollection<Action> queue = new();
    private readonly Thread thread;

    public UiThread()
    {
        Context = new UiSynchronizationContext(this);
        thread = new Thread(Run) { IsBackground = true, Name = "測試 UI 執行緒" };
        thread.Start();
    }

    public SynchronizationContext Context { get; }

    /// <summary>UI 執行緒上沒被接住的例外（測試結束時檢查）</summary>
    public ConcurrentQueue<Exception> Errors { get; } = new();

    public bool IsCurrent => Thread.CurrentThread == thread;

    public void Post(Action action)
    {
        try
        {
            queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // 已關閉（測試結束後才到的計時器）
        }
    }

    public void Invoke(Action action) => Invoke(() =>
    {
        action();
        return true;
    });

    /// <summary>在 UI 執行緒執行並等它完成（例外原樣丟回呼叫端）</summary>
    public T Invoke<T>(Func<T> func)
    {
        if (IsCurrent)
            return func();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                done.SetResult(func());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        if (!((IAsyncResult)done.Task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("UI 執行緒 15 秒內沒有處理完");
        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>等 UI 執行緒把目前排隊的工作（含其後排進來的 async 接續）做完</summary>
    public async Task DrainAsync(int rounds = 3)
    {
        for (int i = 0; i < rounds; i++)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() => done.TrySetResult());
            await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(Context);
        foreach (var action in queue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Errors.Enqueue(ex);
            }
        }
    }

    public void Dispose()
    {
        queue.CompleteAdding();
        thread.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class UiSynchronizationContext : SynchronizationContext
    {
        private readonly UiThread ui;

        public UiSynchronizationContext(UiThread ui) => this.ui = ui;

        public override void Post(SendOrPostCallback d, object? state) => ui.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state) => ui.Invoke(() => d(state));

        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>執行緒安全的呼叫紀錄</summary>
internal sealed class Recorder<T>
{
    private readonly List<T> items = new();

    public void Add(T item)
    {
        lock (items)
            items.Add(item);
    }

    public IReadOnlyList<T> All
    {
        get
        {
            lock (items)
                return items.ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (items)
                return items.Count;
        }
    }
}

/// <summary>
/// 假的宿主：BeginInvoke 在 UI 執行緒上直接同步執行；從其他執行緒（合併送出的 Task.Delay、維護計時器）呼叫時排回 UI 執行緒，
/// 保持「狀態只在 UI 執行緒讀寫」的前提。其他能力只記錄呼叫。
/// alwaysPost＝照正式環境一律排進 UI 執行緒（同步相關的測試用：引擎的 RunOnUiAsync 與協調者的轉送都不能在呼叫端同步執行）
/// </summary>
internal sealed class FakeHost : IAgentHost
{
    private readonly UiThread ui;
    private readonly bool alwaysPost;
    private int attention;
    private int speechStops;

    public FakeHost(UiThread ui, bool alwaysPost = false)
    {
        this.ui = ui;
        this.alwaysPost = alwaysPost;
    }

    public void BeginInvoke(Action action)
    {
        if (ui.IsCurrent && !alwaysPost)
            action();
        else
            ui.Post(action);
    }

    public List<string> FilesToPick { get; set; } = new();

    public string? FolderToPick { get; set; }

    public string? SaveFileToPick { get; set; }

    public Recorder<(string ViewId, string FileName)> SaveFileRequests { get; } = new();

    public IReadOnlyList<string> PickFiles(IAgentView view) => FilesToPick;

    public string? PickFolder(IAgentView view, string? initialDirectory) => FolderToPick;

    public string? PickSaveFile(IAgentView view, string fileName)
    {
        SaveFileRequests.Add((view.Id, fileName));
        return SaveFileToPick;
    }

    public Recorder<string> Opened { get; } = new();

    public Recorder<string> Revealed { get; } = new();

    public Recorder<string> Copied { get; } = new();

    public Recorder<string> Popouts { get; } = new();

    public Recorder<string> Themes { get; } = new();

    public Recorder<(string Title, string Message, string? ConversationId)> Notifications { get; } = new();

    public Recorder<(string Tooltip, int Running, int Waiting)> Tray { get; } = new();

    public int AttentionRequests => Volatile.Read(ref attention);

    /// <summary>每次閃爍提醒指定的 view（顯示該對話的視窗）</summary>
    public Recorder<IReadOnlyList<string>> AttentionViews { get; } = new();

    /// <summary>app 有視窗在前景（false＝全部視窗都在背景、最小化或藏到系統匣）</summary>
    public bool IsForeground { get; set; } = true;

    /// <summary>只有這個 view 所在的視窗是前景視窗（null＝每個 view 都算在前景）：模擬使用者正在另一個視窗操作</summary>
    public string? ForegroundViewId { get; set; }

    public bool IsViewForeground(string viewId) => IsForeground && (ForegroundViewId == null || ForegroundViewId == viewId);

    public void OpenWithShell(string pathOrUrl) => Opened.Add(pathOrUrl);

    public void RevealInExplorer(string path) => Revealed.Add(path);

    /// <summary>設定後 CopyText 丟出這個例外（測試中樞的例外處理）</summary>
    public Exception? CopyFailure { get; set; }

    public void CopyText(string text)
    {
        if (CopyFailure is { } failure)
            throw failure;
        Copied.Add(text);
    }

    public void OpenPopout(string conversationId) => Popouts.Add(conversationId);

    public Recorder<string> Deleted { get; } = new();

    public void ConversationDeleted(string conversationId) => Deleted.Add(conversationId);

    public void Notify(string title, string message, string? conversationId) => Notifications.Add((title, message, conversationId));

    public void RequestAttention(IReadOnlyCollection<string> viewIds)
    {
        AttentionViews.Add(viewIds.ToList());
        Interlocked.Increment(ref attention);
    }

    /// <summary>語音通知念的每一句</summary>
    public Recorder<string> Spoken { get; } = new();

    /// <summary>每一句的「開始念了」回呼（和 <see cref="Spoken"/> 同順序；測試在 UI 執行緒呼叫它，模擬聲音開始出來）</summary>
    public Recorder<Action?> SpeechStarted { get; } = new();

    /// <summary>每一句的語速與音調（和 <see cref="Spoken"/> 同順序）</summary>
    public Recorder<VoiceTuning> SpokenTuning { get; } = new();

    public int SpeechStops => Volatile.Read(ref speechStops);

    public void Speak(string text, VoiceTuning tuning, Action? started)
    {
        SpokenTuning.Add(tuning);
        Spoken.Add(text);
        SpeechStarted.Add(started);
    }

    public void StopSpeaking() => Interlocked.Increment(ref speechStops);

    public void ApplyTheme(string theme) => Themes.Add(theme);

    public void UpdateTray(string tooltip, int running, int waiting) => Tray.Add((tooltip, running, waiting));
}

/// <summary>假的視窗：記錄收到的每則訊息（深拷貝），可等待特定訊息</summary>
internal class FakeView : IAgentView
{
    private readonly List<JObject> messages = new();

    public FakeView(string id, bool isPopout = false)
    {
        Id = id;
        IsPopout = isPopout;
    }

    public string Id { get; }

    public bool IsPopout { get; }

    /// <summary>每則訊息送到時（在送出的執行緒上、記錄之前）呼叫：測試在那一刻檢查其他狀態，例如紀錄檔存了沒</summary>
    public Action<JObject>? Posting { get; set; }

    public void PostToWeb(JObject message)
    {
        Posting?.Invoke(message);
        lock (messages)
            messages.Add((JObject)message.DeepClone());
    }

    public IReadOnlyList<JObject> All()
    {
        lock (messages)
            return messages.ToList();
    }

    /// <summary>目前已收到幾則（之後用 since 只看這之後的訊息）</summary>
    public int Mark()
    {
        lock (messages)
            return messages.Count;
    }

    public IReadOnlyList<JObject> Messages(string type, int since = 0, string? conversationId = null)
        => All().Skip(since)
            .Where(m => m.Value<string>("type") == type && (conversationId == null || m.Value<string>("conversationId") == conversationId))
            .ToList();

    public JObject? Last(string type, string? conversationId = null)
        => Messages(type, 0, conversationId).LastOrDefault();

    /// <summary>輪詢等待符合條件的訊息（合併送出約 50 ms、帳戶卡最多每 500 ms 一次）</summary>
    public async Task<JObject> WaitForAsync(string type, Func<JObject, bool>? predicate = null, int since = 0, int timeoutMs = 2000, string? conversationId = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var match = Messages(type, since, conversationId).LastOrDefault(m => predicate == null || predicate(m));
            if (match != null)
                return match;
            if (DateTime.UtcNow > deadline)
            {
                var got = string.Join("、", All().Skip(since).Select(m => m.Value<string>("type")));
                throw new XunitException($"視窗 {Id} 在 {timeoutMs} ms 內沒有收到符合條件的 {type} 訊息；這段期間收到：{got}");
            }
            await Task.Delay(10);
        }
    }
}

/// <summary>假的彈出視窗（只顯示一個對話）</summary>
internal sealed class FakePopoutView : FakeView, IPopoutView
{
    public FakePopoutView(string id, string conversationId) : base(id, isPopout: true) => ConversationId = conversationId;

    public string ConversationId { get; }
}

/// <summary>假的代理會話：記錄中樞呼叫了什麼，事件由測試手動觸發</summary>
internal sealed class FakeSession : IAgentSession
{
    private int interrupts;
    private int closes;

    public FakeSession(AgentProvider provider)
    {
        Provider = provider;
        CanSwitchModeLive = provider == AgentProvider.Claude;
        CanSwitchModelLive = provider is AgentProvider.Claude or AgentProvider.Grok;
    }

    public AgentProvider Provider { get; }

    /// <summary>SendAsync 時成立、TurnCompleted 時清除；測試可直接設定（模擬 RestartAsync 自動接續的一輪）</summary>
    public bool IsRunning { get; set; }

    /// <summary>測試指定程序是否活著；null＝照預設（還沒 Dispose 就算活著）</summary>
    public bool? LiveProcess { get; set; }

    public bool HasLiveProcess => LiveProcess ?? !Disposed;

    public bool Disposed { get; private set; }

    // ── CLI 熱更新（契約見 docs/plans/2026-10-10-hot-restart-contract.md）──

    public string? RunningVersion { get; set; }

    public string? ProcessInstance { get; set; }

    /// <summary>對話框燈號的來源（AgentHub.Activity.cs）：測試直接設定 PID、程序實例與管線位元組累計；null＝沒有程序</summary>
    public AgentActivitySource? ActivitySource { get; set; }

    /// <summary>測試指定的安全點狀態；null＝照預設（閒置且沒有背景工作才算，同兩個會話類別在熱更新實作前的行為）</summary>
    public bool? SafePoint { get; set; }

    /// <summary>設定後讀安全點狀態（IsAtSafePoint）丟出這個例外：模擬中樞在建立更新屏障途中出錯</summary>
    public Exception? SafePointFailure { get; set; }

    public bool IsAtSafePoint => SafePointFailure is { } failure ? throw failure : SafePoint ?? (!IsRunning && !HasLiveBackgroundWork);

    public bool HasLiveBackgroundWork { get; set; }

    /// <summary>存活背景工作的工具（測試指定）</summary>
    public IReadOnlyList<AgentBackgroundTool> LiveBackgroundTools { get; set; } = Array.Empty<AgentBackgroundTool>();

    /// <summary>中樞要求的每次重開</summary>
    public Recorder<AgentRestartRequest> Restarts { get; } = new();

    /// <summary>設定後 RestartAsync 會等它完成（模擬殺程序樹、啟動新程序的期間）</summary>
    public TaskCompletionSource? RestartGate { get; set; }

    /// <summary>測試決定 RestartAsync 的結果；null＝NotSupported（同兩個會話類別在熱更新實作前的行為）</summary>
    public Func<AgentRestartRequest, AgentRestartResult>? OnRestart { get; set; }

    public async Task<AgentRestartResult> RestartAsync(AgentRestartRequest request)
    {
        Restarts.Add(request);
        if (RestartGate != null)
            await RestartGate.Task;
        return OnRestart?.Invoke(request) ?? AgentRestartResult.Unsupported;
    }

    /// <summary>
    /// 模擬會話照契約重開：Settings=null（更新屏障）＝停下（Stopped）；AutoContinue=false＝停下舊程序、不啟動新程序（Restarted）；
    /// 否則先發新程序的 AgentProcessStartedEvent（帶要求的身分）、再發 AgentTurnStartedByCliEvent，回 RestartedAndContinued
    /// </summary>
    public void SucceedRestarts(string instancePrefix = "next")
    {
        int started = 0;
        OnRestart = request =>
        {
            var previous = ProcessInstance;
            if (request.Settings == null || !request.AutoContinue)
            {
                ProcessInstance = null;
                IsRunning = false;
                return new AgentRestartResult(request.Settings == null ? AgentRestartOutcome.Stopped : AgentRestartOutcome.Restarted, previous);
            }
            var instance = $"{instancePrefix}-{++started}";
            ProcessInstance = instance;
            Raise(new AgentProcessStartedEvent(instance, request.Settings.Cli));
            IsRunning = true;
            Raise(new AgentTurnStartedByCliEvent(Provider == AgentProvider.Claude ? AgentAutoTurnKind.ResumeInterruptedTurn : AgentAutoTurnKind.Continuation));
            return new AgentRestartResult(AgentRestartOutcome.RestartedAndContinued, previous, instance);
        };
    }

    public int Interrupts => Volatile.Read(ref interrupts);

    public int Closes => Volatile.Read(ref closes);

    public Recorder<(AgentTurnInput Input, AgentSessionSettings Settings)> Sent { get; } = new();

    public Recorder<(string Key, IReadOnlyDictionary<string, IReadOnlyList<string>> Answers)> Answers { get; } = new();

    public Recorder<string> Declined { get; } = new();

    public Recorder<(string Key, AgentApprovalChoice Choice, string? Feedback)> Approvals { get; } = new();

    public Recorder<(string Key, AgentPlanChoice Choice, string? Feedback)> PlanResponses { get; } = new();

    /// <summary>旁支提問（AskSideQuestionAsync）問了什麼</summary>
    public Recorder<string> SideQuestions { get; } = new();

    /// <summary>設定後旁支提問等它的結果（模擬模型在想）；null＝立刻回「答案」</summary>
    public TaskCompletionSource<AgentSideAnswer>? SideAnswer { get; set; }

    public Task<AgentSideAnswer> AskSideQuestionAsync(string question, AgentSessionSettings settings)
    {
        SideQuestions.Add(question);
        return SideAnswer?.Task ?? Task.FromResult(new AgentSideAnswer("答案", null));
    }

    /// <summary>MCP 操作（McpActionAsync）做了什麼</summary>
    public Recorder<(string Server, AgentMcpAction Action)> McpActions { get; } = new();

    /// <summary>測試決定 MCP 操作的結果；null＝成功</summary>
    public Func<string, AgentMcpAction, AgentMcpActionResult>? OnMcpAction { get; set; }

    public Task<AgentMcpActionResult> McpActionAsync(string server, AgentMcpAction action, AgentSessionSettings settings)
    {
        McpActions.Add((server, action));
        return Task.FromResult(OnMcpAction?.Invoke(server, action) ?? new AgentMcpActionResult(true, null));
    }

    /// <summary>CLI 自己回報的清單（GetInventoryAsync，健康度用）；null＝不回報</summary>
    public Func<Myriagent.Health.ClaudeCliInventory?>? Inventory { get; set; }

    public Task<Myriagent.Health.ClaudeCliInventory?> GetInventoryAsync() => Task.FromResult(Inventory?.Invoke());

    /// <summary>MCP 狀態（GetMcpStatusAsync）：測試決定每次問到的結果；null＝回錯誤</summary>
    public Func<AgentMcpStatusResult>? McpStatus { get; set; }

    /// <summary>每次問 MCP 狀態時帶的設定</summary>
    public Recorder<AgentSessionSettings> McpStatusSettings { get; } = new();

    public Recorder<(string Action, JObject Args, AgentSessionSettings Settings)> CodexControls { get; } = new();

    public Func<string, JObject, Task<JObject>>? OnCodexControl { get; set; }

    public Task<JObject> CodexControlAsync(string action, JObject args, AgentSessionSettings settings)
    {
        CodexControls.Add((action, (JObject)args.DeepClone(), settings));
        return OnCodexControl?.Invoke(action, args) ?? Task.FromResult(new JObject());
    }

    public Task<AgentMcpStatusResult> GetMcpStatusAsync(AgentSessionSettings settings)
    {
        McpStatusSettings.Add(settings);
        return Task.FromResult(McpStatus?.Invoke() ?? new AgentMcpStatusResult(null, "測試沒有設定 MCP 狀態"));
    }

    /// <summary>設定後 SendAsync 會等它完成（模擬啟動程序、建立 thread 的準備期間）</summary>
    public TaskCompletionSource? SendGate { get; set; }

    /// <summary>接下來幾次 SendAsync 依序丟出的例外（模擬會話拒絕送出，例如正在以新版重開的 AgentRestartInProgressException）；被拒的不記進 Sent</summary>
    public ConcurrentQueue<Exception> SendFailures { get; } = new();

    public event Action<AgentEvent>? EventReceived;

    public void Raise(AgentEvent ev)
    {
        if (ev is AgentTurnCompletedEvent)
            IsRunning = false;
        // 同真的會話：閒置時遠端送來的訊息、CLI 自己開的一輪開始新的一輪
        if (ev is AgentRemoteUserMessageEvent { StartsTurn: true } or AgentIdleTurnStartedEvent)
            IsRunning = true;
        EventReceived?.Invoke(ev);
    }

    public async Task SendAsync(AgentTurnInput input, AgentSessionSettings settings)
    {
        if (SendFailures.TryDequeue(out var failure))
            throw failure;
        Sent.Add((input, settings));
        IsRunning = true;
        if (SendGate != null)
            await SendGate.Task;
    }

    /// <summary>
    /// SteerAsync 插不插得進去（預設不行：既有測試照舊走排隊；測試插入時設成 true）。
    /// 回 true 時把訊息記進 Steered；SteerGate 設定後先等它（模擬這一輪還在準備），等待期間取消就丟 OperationCanceledException
    /// </summary>
    public bool CanSteer { get; set; }

    public TaskCompletionSource<bool>? SteerGate { get; set; }

    public Recorder<AgentTurnInput> Steered { get; } = new();

    /// <summary>CancelSteerAsync 收到的 uuid 與要回的結果（CLI 還沒讀走＝true）</summary>
    public Recorder<string> SteerCancels { get; } = new();

    public bool CancelSteerResult { get; set; } = true;

    public bool CanCancelSteer => Provider == AgentProvider.Claude;

    public Task<bool> CancelSteerAsync(string messageUuid)
    {
        SteerCancels.Add(messageUuid);
        return Task.FromResult(CancelSteerResult);
    }

    public int SteerAttempts => Volatile.Read(ref steerAttempts);

    private int steerAttempts;

    public async Task<bool> SteerAsync(AgentTurnInput input, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref steerAttempts);
        bool ok = CanSteer && IsRunning;
        if (SteerGate != null)
            ok = await SteerGate.Task.WaitAsync(cancellationToken);
        if (ok)
            Steered.Add(input);
        return ok;
    }

    public Task InterruptAsync()
    {
        Interlocked.Increment(ref interrupts);
        return Task.CompletedTask;
    }

    public Task AnswerQuestionAsync(string requestKey, IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        Answers.Add((requestKey, answers));
        return Task.CompletedTask;
    }

    public Task DeclineQuestionAsync(string requestKey)
    {
        Declined.Add(requestKey);
        return Task.CompletedTask;
    }

    public Task RespondApprovalAsync(string requestKey, AgentApprovalChoice choice, string? feedback)
    {
        Approvals.Add((requestKey, choice, feedback));
        return Task.CompletedTask;
    }

    public Task RespondPlanAsync(string requestKey, AgentPlanChoice choice, string? feedback)
    {
        PlanResponses.Add((requestKey, choice, feedback));
        return Task.CompletedTask;
    }

    /// <summary>執行中切換權限模式的呼叫</summary>
    public Recorder<AgentPermissionMode> ModeChanges { get; } = new();

    /// <summary>能不能在一輪中途切換權限模式（預設照真的會話：Claude 可以、Codex 不行）</summary>
    public bool CanSwitchModeLive { get; set; }

    public Task<bool> SetPermissionModeAsync(AgentPermissionMode mode)
    {
        ModeChanges.Add(mode);
        return Task.FromResult(CanSwitchModeLive);
    }

    /// <summary>即時換模型與推理強度的呼叫（模型、強度；null＝預設）</summary>
    public Recorder<(string? Model, string? Effort)> ModelChanges { get; } = new();

    /// <summary>能不能即時換模型（預設照真的會話：Claude、Grok 可以，Codex、Antigravity 不行）</summary>
    public bool CanSwitchModelLive { get; set; }

    /// <summary>不為 null 時 SetModelAsync 等它完成才回應（測試等待期間這一輪結束、又換了模型）</summary>
    public TaskCompletionSource<bool>? ModelSwitchGate { get; set; }

    /// <summary>RewindFilesAsync 收到的（uuid、dry run）與要回的結果；TranscriptFor 回的逐字稿路徑</summary>
    public Recorder<(string Uuid, bool DryRun)> RewindCalls { get; } = new();

    public AgentRewindResult RewindResult { get; set; } = new(true, [@"C:\work\a.txt"], 1, 2, null);

    public string? Transcript { get; set; }

    public Task<AgentRewindResult> RewindFilesAsync(string userMessageUuid, bool dryRun, AgentSessionSettings settings)
    {
        RewindCalls.Add((userMessageUuid, dryRun));
        return Task.FromResult(RewindResult);
    }

    public string? TranscriptFor(AgentSessionSettings settings) => Transcript;

    /// <summary>GetTaskOutputAsync 收到的背景工作 id 與要回的結果</summary>
    public Recorder<string> TaskOutputRequests { get; } = new();

    public AgentTaskOutput TaskOutput { get; set; } = new("輸出", 6, false, null);

    public Task<AgentTaskOutput> GetTaskOutputAsync(string taskId)
    {
        TaskOutputRequests.Add(taskId);
        return Task.FromResult(TaskOutput);
    }

    /// <summary>StopBackgroundWorkAsync 收到的 stopId（全部記成 "*"）與要回的結果</summary>
    public Recorder<string> StopRequests { get; } = new();

    public AgentBackgroundStopResult StopResult { get; set; } = new(1, null);

    public Task<AgentBackgroundStopResult> StopBackgroundWorkAsync(string? stopId)
    {
        StopRequests.Add(stopId ?? "*");
        return Task.FromResult(StopResult);
    }

    /// <summary>SetOutputStyleAsync 收到的風格</summary>
    public Recorder<string> StyleChanges { get; } = new();

    public Task<bool> SetOutputStyleAsync(string style)
    {
        StyleChanges.Add(style);
        return Task.FromResult(true);
    }

    public async Task<bool> SetModelAsync(string? model, string? effort)
    {
        ModelChanges.Add((model, effort));
        if (ModelSwitchGate != null)
            return await ModelSwitchGate.Task;
        return CanSwitchModelLive;
    }

    /// <summary>測試指定程序是否開著 Remote Control</summary>
    public bool HasRemoteControl { get; set; }

    /// <summary>中樞要求的 Remote Control 切換（null＝關閉）</summary>
    public Recorder<AgentRemoteControlOptions?> RemoteControlChanges { get; } = new();

    public Task SetRemoteControlAsync(AgentRemoteControlOptions? options)
    {
        RemoteControlChanges.Add(options);
        return Task.CompletedTask;
    }

    /// <summary>程序內產生標題（generate_session_title）：設了就當成開著的 Claude 程序、回這個標題（空字串＝回 null）</summary>
    public string? LiveTitle { get; set; }

    /// <summary>中樞交給程序內產生標題的節錄</summary>
    public Recorder<string> TitleRequests { get; } = new();

    public bool CanGenerateTitle => LiveTitle != null && !Disposed;

    public Task<string?> GenerateTitleAsync(string description)
    {
        TitleRequests.Add(description);
        return Task.FromResult(string.IsNullOrEmpty(LiveTitle) ? null : LiveTitle);
    }

    /// <summary>中樞要求的 CLI 分叉（新 session id 的提議、當時的設定）；<see cref="ForkResult"/>＝回傳的新 session id（null＝失敗）</summary>
    public Recorder<(string NewSessionId, AgentSessionSettings Settings)> Forks { get; } = new();

    public string? ForkResult { get; set; }

    public Task<string?> ForkSessionAsync(string newSessionId, AgentSessionSettings settings)
    {
        Forks.Add((newSessionId, settings));
        return Task.FromResult(ForkResult);
    }

    /// <summary>中樞要求的工作階段改名</summary>
    public Recorder<string> Renames { get; } = new();

    public Task RenameAsync(string title)
    {
        Renames.Add(title);
        return Task.CompletedTask;
    }

    /// <summary>中樞要求的「重開程序、只開 Remote Control 接回」（app 更新後；帶的是當時的對話設定）</summary>
    public Recorder<AgentSessionSettings> Reattaches { get; } = new();

    /// <summary>ReattachRemoteControlAsync 的結果（預設 true：程序起來、送了 remote_control）</summary>
    public bool ReattachResult { get; set; } = true;

    public Task<bool> ReattachRemoteControlAsync(AgentSessionSettings settings)
    {
        Reattaches.Add(settings);
        return Task.FromResult(ReattachResult);
    }

    /// <summary>設定後 CloseAsync 會等它完成（模擬代理程序遲遲不結束：app 更新的結束流程等 8 秒就不等了、直接關視窗結束）</summary>
    public TaskCompletionSource? CloseGate { get; set; }

    public async Task CloseAsync()
    {
        Interlocked.Increment(ref closes);
        if (CloseGate != null)
            await CloseGate.Task;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// 一個測試用的中樞：暫存資料夾（AppPaths 指過去）、UI 執行緒、假宿主、真的 AccountManager（以 Seed 放入帳戶，不跑 CLI、不開登入視窗）、
/// 不做背景檢查的 CliManager、假會話工廠。帳戶：Claude 主帳戶（系統預設）、Claude 副帳戶（隔離）、Codex 帳戶（隔離）。
/// </summary>
internal sealed class HubHarness : IDisposable
{
    private readonly Recorder<FakeSession> sessions = new();
    private readonly AccountStatus seedStatus;
    private readonly Func<AgentProvider, IAgentSession>? customSessionFactory;

    /// <param name="loadSettings">讀設定的方式（預設＝全新的預設設定）；在 AppPaths 指到暫存資料夾之後呼叫</param>
    /// <param name="createCli">建立 CliManager（預設＝不監看、不跑背景檢查）；測試真的更新流程時換成以假安裝與假指令建立的</param>
    /// <param name="sessionFactory">建立會話（預設＝FakeSession）；真實 CLI 的整合測試換成真的會話</param>
    /// <param name="seedAccountsAfterHub">照 AppController 的順序：先建中樞、再載入帳戶清單（預設先載入帳戶）</param>
    /// <param name="alwaysPost">宿主的 BeginInvoke 一律排進 UI 執行緒（照正式環境；同步相關的測試用）</param>
    public HubHarness(Action<ConversationStore>? seedStore = null, AccountStatus status = AccountStatus.Ready, Func<AppSettings>? loadSettings = null,
        Func<HttpClient, CliManager>? createCli = null, Func<AgentProvider, IAgentSession>? sessionFactory = null, bool seedAccountsAfterHub = false,
        bool alwaysPost = false)
    {
        seedStatus = status;
        customSessionFactory = sessionFactory;
        Temp = new TempDirectory("hub");
        AppPaths.Override(Temp.Root);
        Ui = new UiThread();
        Host = new FakeHost(Ui, alwaysPost);
        Http = new HttpClient(new StubHttpHandler());
        Store = new ConversationStore(Path.Combine(Temp.Root, "conversations"));
        seedStore?.Invoke(Store);

        Claude = new AgentAccount { Id = "acc-claude", Provider = AgentProvider.Claude, Label = "Claude 主帳戶", Color = "#D97757" };
        Claude2 = new AgentAccount
        {
            Id = "acc-claude2", Provider = AgentProvider.Claude, Label = "Claude 副帳戶", Color = "#E5A04B",
            ConfigDirectory = Temp.Combine("accounts", "acc-claude2", "claude"),
        };
        Codex = new AgentAccount
        {
            Id = "acc-codex", Provider = AgentProvider.Codex, Label = "Codex 帳戶", Color = "#19C3A6",
            ConfigDirectory = Temp.Combine("accounts", "acc-codex", "codex"),
        };

        Ui.Invoke(() =>
        {
            Cli = createCli?.Invoke(Http) ?? new CliManager(Http, _ => false, () => false, startBackgroundChecks: false);
            Accounts = new AccountManager(Host.BeginInvoke, Cli, Http, id => Hub?.RunningForAccount(id) ?? 0);
            if (!seedAccountsAfterHub)
                Accounts.Seed(new[] { Claude, Claude2, Codex }, status);
            Settings = loadSettings?.Invoke() ?? new AppSettings();
            Hub = new AgentHub(Store, Host, Accounts, Cli, Settings, sessionFactory ?? CreateSession, File.Exists);
            if (seedAccountsAfterHub)
                Accounts.Seed(new[] { Claude, Claude2, Codex }, status);
        });

        Main = AddView("main");
    }

    public TempDirectory Temp { get; }

    public UiThread Ui { get; }

    public FakeHost Host { get; }

    public HttpClient Http { get; }

    public ConversationStore Store { get; }

    public CliManager Cli { get; private set; } = null!;

    public AccountManager Accounts { get; private set; } = null!;

    public AppSettings Settings { get; private set; } = null!;

    public AgentHub Hub { get; private set; } = null!;

    public AgentAccount Claude { get; }

    public AgentAccount Claude2 { get; }

    public AgentAccount Codex { get; }

    /// <summary>預設的主視窗（RestartAsync 之後換成掛在新中樞上的視窗）</summary>
    public FakeView Main { get; private set; }

    public IReadOnlyList<FakeSession> Sessions => sessions.All;

    /// <summary>每個新建的假會話先給測試設定（中樞當下才建、立刻就用的會話，例如儀表板 MCP 檢查用的）</summary>
    public Action<FakeSession>? OnSessionCreated { get; set; }

    private IAgentSession CreateSession(AgentProvider provider)
    {
        var session = new FakeSession(provider);
        OnSessionCreated?.Invoke(session);
        sessions.Add(session);
        return session;
    }

    public FakeView AddView(string id)
    {
        var view = new FakeView(id);
        Ui.Invoke(() => Hub.AttachView(view));
        return view;
    }

    public FakePopoutView AddPopout(string id, string conversationId)
    {
        var view = new FakePopoutView(id, conversationId);
        Ui.Invoke(() => Hub.AttachView(view));
        return view;
    }

    public static JObject MakeCommand(string cmd, object? args = null)
    {
        var o = args == null ? new JObject() : JObject.FromObject(args);
        o["cmd"] = cmd;
        return o;
    }

    /// <summary>網頁送指令（在 UI 執行緒處理；同步的部分執行完才回來）</summary>
    public void Command(FakeView view, string cmd, object? args = null, IReadOnlyList<string>? paths = null)
        => Ui.Invoke(() => Hub.HandleCommand(view, MakeCommand(cmd, args), paths));

    /// <summary>開新草稿，回傳對話 id</summary>
    public string NewConversation(FakeView view, AgentAccount account)
    {
        var mark = view.Mark();
        Command(view, "newConversation", new { accountId = account.Id, paneId = "p1" });
        return view.Messages("conversation", mark).Single()["conversationId"]!.ToString();
    }

    public void Send(FakeView view, string conversationId, string text)
        => Command(view, "send", new { conversationId, text });

    /// <summary>
    /// 開新對話並送出第一則，回傳（對話 id、建立的假會話）。
    /// started＝模擬 CLI 回報對話已建立（Claude 的 system/init、Codex 的 thread/start），之後的訊息才不會被當成新 session 而附上前文節錄
    /// </summary>
    public (string Id, FakeSession Session) StartConversation(FakeView view, AgentAccount account, string text = "第一則", bool started = true)
    {
        var id = NewConversation(view, account);
        var before = Sessions.Count;
        Send(view, id, text);
        Sessions.Count.Should().Be(before + 1, "送出第一則時建立會話");
        var session = Sessions[^1];
        if (started)
            Raise(session, new AgentSessionStartedEvent(Conversation(id)!.SessionId ?? "thread-" + Guid.NewGuid().ToString("N")[..8], null, null));
        return (id, session);
    }

    /// <summary>在 UI 執行緒觸發會話事件（中樞的 BeginInvoke 因此同步處理完才回來）</summary>
    public void Raise(FakeSession session, AgentEvent ev) => Ui.Invoke(() => session.Raise(ev));

    public void CompleteTurn(FakeSession session, AgentTurnOutcome outcome = AgentTurnOutcome.Completed, string? error = null, AgentUsage? usage = null)
        => Raise(session, new AgentTurnCompletedEvent(outcome, error, usage));

    // ── CLI 熱更新（契約見 docs/plans/2026-10-10-hot-restart-contract.md）──

    /// <summary>在背景執行緒宣告 CLI 安裝的變化（同偵測流程呼叫 CliManager.PublishVersionChange），等中樞處理完</summary>
    public async Task PublishCliAsync(CliVersionChange change)
    {
        await Task.Run(() => Cli.PublishVersionChange(change));
        await Ui.DrainAsync();
    }

    /// <summary>宣告已穩定的新身分（old 為 null＝啟動時第一次讀到，不算換版）</summary>
    public Task SwitchCliAsync(AgentProvider provider, CliBinaryIdentity? old, CliBinaryIdentity fresh, CliChangeOrigin origin = CliChangeOrigin.External)
        => PublishCliAsync(new CliVersionChange(provider, old, fresh, origin, Stable: true, InstallBroken: false, Detail: null));

    /// <summary>模擬會話啟動了新的 CLI 程序（AgentProcessStartedEvent：在讀那個程序的輸出之前發）</summary>
    public void ProcessStarted(FakeSession session, string instance, CliBinaryIdentity? cli)
    {
        session.ProcessInstance = instance;
        Raise(session, new AgentProcessStartedEvent(instance, cli));
    }

    public T OnUi<T>(Func<T> func) => Ui.Invoke(func);

    /// <summary>
    /// 輪詢等條件成立（中樞有些動作在背景執行緒做完才回 UI 執行緒：設定工作資料夾、送出前檢查資料夾、刪除對話資料夾）；
    /// condition 在 UI 執行緒上判斷
    /// </summary>
    public async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!Ui.Invoke(condition))
        {
            if (DateTime.UtcNow > deadline)
                throw new XunitException($"{timeoutMs} ms 內沒有等到：{because}");
            await Task.Delay(20);
        }
    }

    /// <summary>設定工作資料夾（立即生效）並等背景確認資料夾存在、記進最近使用的資料夾</summary>
    public async Task SetFolderAsync(FakeView view, string conversationId, string folder)
    {
        Command(view, "setFolder", new { conversationId, path = folder });
        var full = Path.GetFullPath(folder);
        await WaitUntilAsync(() => string.Equals(Hub.GetConversation(conversationId)?.WorkingDirectory, full, StringComparison.OrdinalIgnoreCase)
                                   && Settings.RecentFolders.Contains(full, StringComparer.OrdinalIgnoreCase),
            $"工作資料夾改成 {folder} 並確認存在");
    }

    public List<AgentChatItem> Items(string conversationId) => Ui.Invoke(() => Hub.GetItems(conversationId).ToList());

    public AgentConversation? Conversation(string conversationId) => Ui.Invoke(() => Hub.GetConversation(conversationId));

    public IReadOnlyList<string> Queue(string conversationId) => Ui.Invoke(() => Hub.GetQueue(conversationId));

    /// <summary>這個對話在中樞看來是否在執行一輪（c.Running）</summary>
    public bool IsRunning(string conversationId) => Ui.Invoke(() => Hub.IsRunning(conversationId));

    /// <summary>
    /// 模擬 app 結束後重新啟動：目前的中樞正常收尾（ShutdownAsync），以同一個暫存資料夾、ConversationStore 與設定物件重新建立 CliManager、帳戶與中樞，
    /// 並換一個新的主視窗（只掛上、不送 ready；舊視窗的訊息紀錄保留在舊物件上）。之後新開的會話照樣記在 Sessions（舊的保留在前面）。
    /// 一律用預設的 CliManager（不監看、不跑背景檢查；建構時的 createCli 不重用），帳戶一律在建立中樞之前 seed（不照 seedAccountsAfterHub）；
    /// 會話工廠與帳戶狀態沿用建構時的
    /// </summary>
    /// <param name="keepDrafts">結束時不刪資料夾的草稿（同 app 更新時 resume.json 列出的草稿對話）</param>
    /// <param name="crash">模擬當掉（被強制結束、斷電）：舊的中樞不收尾、不存檔，只停掉計時器（AgentHub.AbandonForTest）</param>
    public async Task RestartAsync(IReadOnlySet<string>? keepDrafts = null, bool crash = false)
    {
        if (crash)
            Ui.Invoke(() => Hub.AbandonForTest());
        else
            await Ui.Invoke(() => Hub.ShutdownAsync(keepDrafts));
        Ui.Invoke(() =>
        {
            Hub.Dispose();
            var accounts = Accounts.Accounts.ToList();   // 測試另外加的帳戶（例如整合測試的系統預設 Codex 帳戶）也帶過去
            Accounts.Dispose();
            Cli.Dispose();
            Cli = new CliManager(Http, _ => false, () => false, startBackgroundChecks: false);
            Accounts = new AccountManager(Host.BeginInvoke, Cli, Http, id => Hub?.RunningForAccount(id) ?? 0);
            Accounts.Seed(accounts, seedStatus);
            Hub = new AgentHub(Store, Host, Accounts, Cli, Settings, customSessionFactory ?? CreateSession, File.Exists);
        });
        Main = AddView("main");
        await Ui.DrainAsync();
    }

    public void Dispose()
    {
        try
        {
            Ui.Invoke(() =>
            {
                Hub.Dispose();
                Accounts.Dispose();
                Cli.Dispose();
            });
        }
        finally
        {
            Ui.Dispose();
            Http.Dispose();
            AppPaths.Override(null);
            Temp.Dispose();
        }

        if (!Ui.Errors.IsEmpty)
            throw new XunitException("UI 執行緒有未處理的例外：" + string.Join("\n", Ui.Errors.Select(e => e.ToString())));
    }
}
