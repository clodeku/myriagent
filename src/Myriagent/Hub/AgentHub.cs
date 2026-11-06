using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Background;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Git;
using Myriagent.Remote;
using Myriagent.Speech;
using Myriagent.Sync;
using Myriagent.Update;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 中樞：管理所有帳戶的所有對話與代理會話，把會話事件轉成畫面項目、處理網頁送來的指令、存檔，並把更新路由到「正在顯示該對話」的視窗。
/// 所有狀態只在 UI 執行緒讀寫（會話事件一律經 IAgentHost.BeginInvoke 轉回），所以不需要鎖。
/// 訊息協定見 docs/web-protocol.md。
/// </summary>
internal sealed partial class AgentHub : IDisposable, ICliUpdateParticipant
{
    private const string SendingStatus = "傳送中…";

    /// <summary>背景工作完成後 CLI 自己開的一輪（AgentIdleTurnStartedEvent）在第一個輸出之前的狀態文字</summary>
    internal const string BackgroundTaskTurnStatus = "背景工作完成，代理接著處理…";

    /// <summary>Codex 的持續目標還沒完成、codex 自己開的下一輪（AgentIdleTurnStartedEvent 的 ForThreadGoal）在第一個輸出之前的狀態文字</summary>
    internal const string ThreadGoalTurnStatus = "持續目標還沒完成，代理接著做下一輪…";
    internal const int PageSize = 80;

    private sealed class QueuedMessage
    {
        private string? id;

        /// <summary>
        /// 網頁以它指定這則（cancelQueued）。沒指定時：遠端來源的是 <c>"r"＋cid</c>（被留下再放回、重試都是同一個 Id，WP4.4 計畫 D20），
        /// 本機的是新的 Guid
        /// </summary>
        public string Id
        {
            get => id ??= RemoteHandlerRules.QueueIdOf(RemoteOrigin) ?? Guid.NewGuid().ToString("N");
            init => id = value;
        }

        public string Text { get; init; } = string.Empty;

        public List<AgentAttachment> Attachments { get; init; } = new();

        /// <summary>這則是非同步提問（Codex）的答案（執行中作答、或 CLI 更新與換版時被延後）：從佇列取消時卡片要回到可作答</summary>
        public AgentChatItem? AnswerFor { get; init; }

        /// <summary>從遠端送進來時的來源（設計 14.11 D13）；本機的是 null。WP4.1 只隨 queue.json 存讀，取出時的檢查在 WP4.4</summary>
        public RemoteOrigin? RemoteOrigin { get; init; }

        /// <summary>畫面、紀錄與同步看的文字（秘密回答遮蔽後的，D20）；null＝同 <see cref="Text"/>。WP4.1 只隨 queue.json 存讀</summary>
        public string? DisplayText { get; init; }

        /// <summary>
        /// 送出前被 HoldRemoteSend 留下的遠端訊息（只在記憶體，WP4.4 計畫 D20）：本機使用者看過提示之後親自按送出，就當成本機的訊息送出
        /// </summary>
        public bool Held { get; set; }
    }

    /// <summary>一個對話在中樞裡的狀態（partial：第 4 階段的旗標在 AgentHub.Remote.cs）</summary>
    private sealed partial class ConversationState
    {
        public ConversationState(AgentConversation meta) => Meta = meta;

        public AgentConversation Meta;
        public List<AgentChatItem> Items = new();
        public bool ItemsLoaded;
        public bool IsDraft;
        public IAgentSession? Session;
        public bool Running;
        public string StatusText = string.Empty;
        public DateTime LastActivity = DateTime.Now;
        public bool Unread;
        public int ImportingCount;
        public string? ImportingText;
        public readonly List<AgentAttachment> PendingAttachments = new();
        public readonly List<QueuedMessage> Queue = new();

        /// <summary>
        /// 正在插進這一輪的那則（佇列最前面，IAgentSession.SteerAsync 還沒回來）：這一輪剛好結束時不能由 CompleteTurn 另外送出（會送兩次），
        /// 取消它時改取消插入前的等待
        /// </summary>
        public QueuedMessage? SteeringMessage;
        public CancellationTokenSource? SteerCancel;

        /// <summary>插入途中這一輪正常結束了：插入有了結果之後，佇列照常接著送（CompleteTurn 跳過了插入中的那則）</summary>
        public bool SendQueueAfterSteer;

        /// <summary>開著這個對話分頁的 view（更新只送給它們）</summary>
        public readonly HashSet<string> ViewIds = new(StringComparer.Ordinal);

        /// <summary>這個對話正顯示在畫面上的 view（作用中分頁；網頁以 visible 回報）：判斷使用者看不看得到，決定未讀與通知</summary>
        public readonly HashSet<string> VisibleViewIds = new(StringComparer.Ordinal);

        /// <summary>沒顯示在任何視窗時上次送項目更新的時間（Environment.TickCount64；HiddenFlushRules）</summary>
        public long? HiddenFlushedAtMs;

        /// <summary>沒顯示在任何視窗時上次送標頭的時間（Environment.TickCount64；HiddenFlushRules，和項目分開算）</summary>
        public long? HiddenHeaderFlushedAtMs;

        /// <summary>很長的執行中項目上次送畫面的時間（項目 id → Environment.TickCount64；StreamFlushRules）</summary>
        public readonly Dictionary<string, long> StreamFlushedAtMs = new(StringComparer.Ordinal);

        /// <summary>每個 view 上次收到的標頭清單指紋（view id → HeaderListRules.Signature）：沒變就省略 commands、models</summary>
        public readonly Dictionary<string, string> HeaderListsSent = new(StringComparer.Ordinal);
        public readonly Dictionary<string, AgentChatItem> BlockItems = new();
        public readonly Dictionary<string, AgentChatItem> ToolItems = new();
        public readonly Dictionary<string, AgentChatItem> RequestItems = new();

        /// <summary>
        /// 還在等核准、還沒執行的工具卡（核准的 RequestKey → 它的 ToolId 對到的執行中工具卡）：CLI 重開時不算被中斷、不跑高風險檢查，
        /// 收尾時寫「沒有核准、沒有執行」。使用者回應、工具結束、這一輪結束或收尾時拿掉；核准卡因重開而失效時不拿掉（那個操作照樣沒有執行）
        /// </summary>
        public readonly Dictionary<string, AgentChatItem> UnapprovedTools = new();
        public AgentChatItem? PlanItem;
        public AgentUsage? LastUsage;

        /// <summary>上下文用量（輸入框旁的提示；會話的 AgentContextUsageEvent 以 ContextUsageRules.Apply 合併）：只在記憶體，/clear、換帳戶時清掉</summary>
        public ContextUsage? ContextUsage;
        public DateTime? TurnStartedAt;

        /// <summary>這一輪已記進 token 計數的增量（一輪結束時和這一輪的用量比對，補記差額；AgentHub.Tokens.cs）</summary>
        public AgentUsage? TurnTokens;

        /// <summary>項目改過、還沒存檔（維護計時器只存有變動的對話：長對話整份重寫要上百毫秒）</summary>
        public bool ItemsUnsaved;

        /// <summary>增量存檔的快取（上次的存檔快照與 items.json 的樣子，ItemsSaveCache）：第一次存檔時建立，停下一陣子後由維護丟掉</summary>
        public ItemsSaveCache? SaveCache;

        /// <summary>最後一個項目的建立順序（AgentChatItem.Seq）：載入時依序編號，之後每新增一個加一</summary>
        public long LastSeq;

        /// <summary>Codex 拒絕核准時使用者附的說明（同一輪可能拒絕多次，依序保留）：這一輪結束後當下一則訊息送出</summary>
        public readonly List<DeniedOperation> PendingFeedback = new();

        /// <summary>上次寫進 queue.json 的內容指紋（沒變就不重寫）</summary>
        public string? QueueFingerprint;

        /// <summary>queue.json 讀不到（被占用、沒有權限）：原檔還在，讀到之前不可以寫（維護時重讀）</summary>
        public bool QueueUnread;

        /// <summary>這一輪使用者按了停止</summary>
        public bool StopRequested;

        /// <summary>這一輪使用者拒絕過核准（Codex 的拒絕會結束這一輪，回報成 interrupted，不能當成使用者按停止）</summary>
        public bool DeniedThisTurn;

        /// <summary>
        /// 代理在這一輪送出非同步提問（Codex）、中樞已停下這一輪等回答（AgentHubRules.PausesForAsyncQuestion）：這一輪以 interrupted 結束時不算「已停止」，
        /// 停下途中作答或送出的訊息不插進這一輪（排進佇列，停下後接著送）
        /// </summary>
        public bool PausingForQuestion;

        /// <summary>
        /// 為了等回答而停下這一輪的那張提問卡（停下之後到下一輪開始為止）：還在等回答時背景指令跑完不自動接續（代理會不等答案就接著做），
        /// 略過它時送出接續訊息（不然代理一直停著）
        /// </summary>
        public AgentChatItem? PausedQuestion;

        /// <summary>這一輪是交給 Claude CLI 的 /clear：正常結束才加清除提示（CompleteTurn）</summary>
        public bool ClearTurn;

        /// <summary>改名時送給代理、還沒收到回送的工作階段名稱（SessionTitleRules；回送的不拿來改標題）</summary>
        public readonly List<string> SentSessionTitles = new();

        /// <summary>CLI 熱更新：事件流上的目前程序、排定的換版與結果（AgentHub.Restart.cs）</summary>
        public readonly RestartTracking Restart = new();

        public int WaitingCount => Items.Count(i => i.Status == AgentItemStatus.Waiting && !i.AnswerAsMessage);
    }

    private readonly ConversationStore store;
    private readonly IAgentHost host;
    private readonly AccountManager accounts;
    private readonly CliManager cli;
    private readonly AppSettings settings;
    private readonly Func<AgentProvider, IAgentSession> sessionFactory;
    private readonly Func<string, bool> fileExists;
    private readonly Dictionary<string, ConversationState> conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IAgentView> views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> dirtyItems = new(StringComparer.Ordinal);
    private readonly HashSet<string> dirtyHeaders = new(StringComparer.Ordinal);

    /// <summary>回報過自己顯示哪些對話（visible）的 view：只有它們的背景分頁才節流（HiddenFlushRules）</summary>
    private readonly HashSet<string> viewsReportingVisibility = new(StringComparer.Ordinal);
    private readonly System.Threading.Timer maintenanceTimer;
    private bool listDirty;
    private bool accountsDirty;
    private bool cliDirty;
    private bool flushScheduled;
    /// <summary>各種代理執行中的對話數（索引是 AgentProvider；CliManager 在背景執行緒讀，寫入在 UI 執行緒）</summary>
    private readonly int[] runningByProvider = new int[AgentProviders.All.Count];
    private bool disposed;

    /// <summary>「對話預設值」已遷移（第一次經 ProviderDefaults 取用時做）</summary>
    private bool conversationDefaultsReady;

    public AgentHub(
        ConversationStore store,
        IAgentHost host,
        AccountManager accounts,
        CliManager cli,
        AppSettings settings,
        Func<AgentProvider, IAgentSession>? sessionFactory = null,
        Func<string, bool>? fileExists = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.accounts = accounts;
        this.cli = cli;
        this.settings = settings;
        this.sessionFactory = sessionFactory ?? CreateSession;
        this.fileExists = fileExists ?? File.Exists;

        // 遠端刪除還沒搬進回收區的（WP4.4 計畫 D24）：先補搬，仍搬不動的不載入（不出現在清單）
        CompletePendingTrashMoves();
        foreach (var meta in store.LoadConversations().Where(m => !IsTrashPending(m.Id)))
        {
            var state = new ConversationState(meta);
            conversations[meta.Id] = state;
            RestoreQueue(state);
            // 上次結束時還在執行的一輪（running.json）：第一次主視窗就緒時接續（AgentHub.InterruptedTurns.cs）
            LoadRunMarker(state);
        }

        // 第 4 階段的授權者與處理者（D48；ConversationStore 不載入其他電腦的副本，D40，所以這裡沒有副本要略過）
        RegisterRemoteExtensions();

        accounts.Changed += MarkAccountsDirty;
        accounts.LoginProgress += OnLoginProgress;
        accounts.UsageObserved += OnUsageObserved;
        accounts.NeedsCapabilities = accountId => !settings.Models.TryGetValue(accountId, out var known) || known.Count == 0
            || (accounts.Find(accountId)?.Provider == AgentProvider.Antigravity && !settings.Commands.ContainsKey(accountId));
        accounts.CapabilitiesDiscovered += OnCapabilitiesDiscovered;
        cli.Changed += () => host.BeginInvoke(OnCliStateChanged);
        cli.VersionChanged += change => host.BeginInvoke(() => OnCliVersionChanged(change));
        cli.UpdateParticipant = this;

        maintenanceTimer = new System.Threading.Timer(_ => host.BeginInvoke(RunMaintenance), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        validRecentFolders = settings.RecentFolders.ToList();
        _ = RefreshRecentFoldersAsync();
    }

    /// <summary>正在執行的對話數（關閉前確認、系統匣）</summary>
    public int RunningCount => conversations.Values.Count(c => c.Running);

    /// <summary>等使用者回應的對話數（系統匣；含 Codex 非同步提問，與清單、帳戶卡一致）</summary>
    public int WaitingCount => conversations.Values.Count(AwaitsUser);

    /// <summary>某種代理有沒有執行中的對話（CliManager 在背景執行緒呼叫，讀快照）</summary>
    public bool HasRunningSessions(AgentProvider provider) => Volatile.Read(ref runningByProvider[(int)provider]) > 0;

    private static IAgentSession CreateSession(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => new ClaudeAgentSession(),
        AgentProvider.Codex => new CodexAgentSession(),
        AgentProvider.Antigravity => new AntigravityAgentSession(),
        AgentProvider.Grok => new GrokAgentSession(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 新對話（或換帳戶後）的 SessionId：Claude 由 app 先產生 UUID（--session-id）、Grok 也是（session/new 的 _meta.sessionId）；
    /// Codex、Antigravity 由 CLI 產生，回報後才記下
    /// </summary>
    private static string? InitialSessionId(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude or AgentProvider.Grok => Guid.NewGuid().ToString(),
        AgentProvider.Codex or AgentProvider.Antigravity => null,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>某帳戶執行中的對話數（AccountManager 決定輪詢間隔）</summary>
    public int RunningForAccount(string accountId) => conversations.Values.Count(c => c.Running && c.Meta.AccountId == accountId);

    // CLI 更新屏障（ICliUpdateParticipant）與重開協調在 AgentHub.Restart.cs

    internal IReadOnlyList<AgentChatItem> GetItems(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) ? EnsureItems(c) : Array.Empty<AgentChatItem>();

    internal AgentConversation? GetConversation(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) ? c.Meta : null;

    internal IReadOnlyList<string> GetQueue(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) ? c.Queue.Select(q => q.Text).ToList() : Array.Empty<string>();

    internal bool IsRunning(string conversationId) => conversations.TryGetValue(conversationId, out var c) && c.Running;

    /// <summary>最近建立的草稿（實機探針用：送出 newConversation 後取得它的 id）</summary>
    internal string? LatestDraftId(string viewId)
        => conversations.Values.Where(c => c.IsDraft && c.ViewIds.Contains(viewId)).OrderByDescending(c => c.Meta.CreatedAt).Select(c => c.Meta.Id).FirstOrDefault();

    // ───────────────────────────── 視窗 ─────────────────────────────

    public void AttachView(IAgentView view) => views[view.Id] = view;

    /// <summary>視窗關閉：它顯示的對話都不再路由給它；沒有任何視窗顯示的草稿丟棄</summary>
    public void DetachView(string viewId)
    {
        views.Remove(viewId);
        viewsReportingVisibility.Remove(viewId);
        ForgetMonitorView(viewId);
        CancelSearch(viewId);
        scheduleWatchers.Remove(viewId);
        foreach (var c in conversations.Values.ToList())
        {
            c.VisibleViewIds.Remove(viewId);
            c.HeaderListsSent.Remove(viewId);
            if (!c.ViewIds.Remove(viewId))
                continue;
            if (c.IsDraft && c.ViewIds.Count == 0)
                DiscardDraft(c);
            else
                OnConversationViewRemoved(c);
        }
        MarkAccountsDirty();
    }

    private IEnumerable<IAgentView> ViewsOf(ConversationState c)
        => c.ViewIds.Select(id => views.TryGetValue(id, out var v) ? v : null).Where(v => v != null).Select(v => v!);

    private void PostToAll(JObject message)
    {
        foreach (var view in views.Values.ToList())
            view.PostToWeb(message);
    }

    private static void Post(IAgentView view, JObject message) => view.PostToWeb(message);

    // ───────────────────────────── 網頁指令 ─────────────────────────────

    /// <summary>處理網頁送來的指令（UI 執行緒）；additionalPaths＝拖放檔案時 WebView2 帶來的本機路徑</summary>
    public void HandleCommand(IAgentView view, JObject command, IReadOnlyList<string>? additionalPaths = null)
    {
        if (disposed || command == null)
            return;

        string? Str(string key) => command.Value<string>(key);
        ConversationState? Conv() => Str("conversationId") is { } id && conversations.TryGetValue(id, out var c) ? c : null;

        try
        {
            // 〔換機〕第 5 階段的交接指令（AgentHub.Remote.cs 的登記點，WP5.9 實作）最先分派：「在這台接手」是對副本下的，
            // 不能被下面的轉送與副本白名單攔掉。沒有實作時編譯器連呼叫一起拿掉，其他指令照舊
            var handled = false;
            HandleHandoffCommand(view, Str("cmd"), command, ref handled);
            if (handled)
                return;

            // 〔桌面遠端操作〕對其他電腦的對話（副本）或其他電腦的帳戶、CLI 下的指令（帶 commandId）交給同步（AgentHub.Peer.cs）
            if (TryHandlePeerUpload(view, command, additionalPaths) || TryHandlePeerFileSave(view, command) || TryHandlePeerDraft(view, command)
                || TryForwardPeerCommand(view, command))
                return;

            // 〔複本〕其他電腦的對話只收看的指令（AgentHub.Replica.cs）；本機的寫入另外給那個檢視唯讀的提示（AgentHub.RemoteHandlers.cs）
            if (BlocksReplicaCommand(Str("cmd"), Str("conversationId")))
            {
                NotifyReplicaReadOnly(view, Str("cmd"));
                return;
            }

            // 第 4 階段的擴充（AgentHub.Remote.cs 的登記點）：遠端與同步專用的指令先分派給各 partial 檔
            InterceptCommand(view, command, ref handled);
            if (handled)
                return;

            switch (Str("cmd"))
            {
                case "ready":
                    OnReady(view);
                    break;
                case "layout":
                    if (command["layout"] is JObject layout)
                        SaveLayout(view, layout);
                    break;
                case "newWindow":
                case "tabDrag":
                case "tabDragEnd":
                case "tabMoveIn":
                case "moveToNewWindow":
                case "closeWindow":
                    HandleWindowCommand(view, Str("cmd")!, command);
                    break;
                case "newConversation":
                    NewConversation(view, Str("accountId"), Str("paneId"));
                    break;
                case "listExternalConversations":
                    ListExternalConversations(view, Str("requestId"));
                    break;
                case "importExternalConversation":
                    ImportExternalConversation(view, Str("sourceKey"), Str("paneId"));
                    break;
                case "forkConversation":
                    ForkConversation(view, Conv(), Str("paneId"));
                    break;
                case "open":
                    // 找不到的對話以前什麼都不回、也不記，網頁就一直「載入對話中…」（2026-11-07 使用者回報）：至少留下記錄（網頁的看門狗會重送）
                    if (Conv() is { } opening)
                        Open(view, opening);
                    else
                        Log.Warn($"網頁要開的對話 {Str("conversationId")} 不在中樞，沒有回應（{ViewLabel(view)}）");
                    break;
                case "close":
                    Close(view, Conv());
                    break;
                case "visible":
                    SetVisible(view, command["conversationIds"] as JArray);
                    break;
                case "loadOlder":
                    LoadOlder(view, Conv(), Str("beforeId"));
                    break;
                case "send":
                    _ = SendAsync(view, Conv(), Str("text") ?? string.Empty);
                    break;
                case "cancelQueued":
                    CancelQueued(Conv(), Str("id"));
                    break;
                case "stop":
                    _ = StopAsync(Conv());
                    break;
                case "answer":
                    _ = AnswerAsync(Conv(), Str("itemId"), command["answers"] as JObject, notesJson: command["notes"] as JObject);
                    break;
                case "skipQuestion":
                    _ = SkipQuestionAsync(Conv(), Str("itemId"));
                    break;
                case "approve":
                    _ = ApproveAsync(Conv(), Str("itemId"), Str("choice"), Str("message"));
                    break;
                case "approvePlan":
                    _ = ApprovePlanAsync(Conv(), Str("itemId"), Str("choice"), Str("message"));
                    break;
                case "rename":
                    Rename(Conv(), Str("title"));
                    break;
                case "autoTitle":
                    AutoTitle(view, Conv());
                    break;
                case "delete":
                    if (command.Value<bool?>("stopPrograms") == true && Conv() is { } doomed)
                        StopConversationPrograms(doomed);
                    Delete(Conv());
                    break;
                case "setOption":
                    SetOption(view, Conv(), Str("key"), Str("value"));
                    break;
                case "setAgent":
                    SetAgent(view, Conv(), Str("agent"));
                    break;
                case "setOutputStyle":
                    SetOutputStyle(view, Conv(), Str("style"));
                    break;
                case "codexControl":
                    _ = HandleCodexControlAsync(view, Conv(), command);
                    break;
                case "quotaWaitCancel":
                    CancelQuotaWait(Conv());
                    break;
                case "goalClear":
                    ClearGoal(view, Conv());
                    break;
                case "rewindPreview":
                    _ = RewindPreviewAsync(view, Conv(), Str("itemId"), Str("requestId"));
                    break;
                case "rewind":
                    _ = RewindAsync(view, Conv(), Str("itemId"), command.Value<bool?>("files") == true, command.Value<bool?>("conversation") == true);
                    break;
                case "cancelSteer":
                    _ = CancelSteerAsync(view, Conv(), Str("id"));
                    break;
                case "taskDetail":
                    // 等背景工作回報那一行的「詳細」（AgentHub.TaskDetail.cs）：在做什麼、指令、目前的輸出
                    _ = TaskDetailAsync(view, Conv(), Str("key"), Str("requestId"));
                    break;
                case "promptHistory":
                    // 「最近的指令」那一行展開的歷史指令（AgentHub.PromptHistory.cs）：只回給問的視窗
                    PostPromptHistory(view, Conv(), Str("requestId"));
                    break;
                case "taskStop":
                    // 等背景工作回報那一行的「停止」（AgentHub.TaskStop.cs）：帶 stopId 停一個，不帶是全部
                    _ = StopBackgroundTaskAsync(view, Conv(), Str("stopId"));
                    break;
                case "setAccount":
                    SetAccount(view, Conv(), Str("accountId"), atTurnEnd: AccountSwitchRules.IsAtTurnEnd(Str("when")),
                        explicitNow: AccountSwitchRules.IsExplicitNow(Str("when")));
                    break;
                case "chooseFolder":
                    ChooseFolder(view, Conv());
                    break;
                case "setFolder":
                    _ = SetFolderAsync(Conv(), Str("path"));
                    break;
                case "resetFolder":
                    ResetFolder(Conv());
                    break;
                case "openFolder":
                    OpenFolder(Conv(), Str("which"));
                    break;
                case "attach":
                    if (Conv() is { } attachTarget)
                        AddAttachments(attachTarget, host.PickFiles(view));
                    break;
                case "addFiles":
                    if (Conv() is { } dropTarget)
                        AddAttachments(dropTarget, additionalPaths ?? Array.Empty<string>());
                    break;
                case "addPathAttachment":
                    if (Conv() is { } pathTarget && Str("path") is { } attachPath)
                        AddAttachments(pathTarget, new[] { attachPath });
                    break;
                case "pasteImage":
                    _ = PasteImageAsync(Conv(), Str("dataUrl"), Str("name"));
                    break;
                case "removeAttachment":
                    RemoveAttachment(Conv(), Str("id"));
                    break;
                case "listFiles":
                    _ = ListFilesAsync(view, Conv(), Str("requestId"), Str("path"));
                    break;
                case "fileSearch":
                    _ = SearchFilesAsync(view, Conv(), Str("requestId"), Str("query") ?? string.Empty);
                    break;
                case "voiceMessage":
                    // 輸入區的語音訊息（AgentHub.Voice.cs）：錄音存進附件、本機辨識成逐字稿，交給代理
                    _ = VoiceMessageAsync(view, Conv(), Str("dataUrl"), Str("wave"));
                    break;
                case "searchConversations":
                    SearchConversations(view, Str("requestId"), Str("query"));
                    break;
                case "openLink":
                    OpenLink(Str("href"));
                    break;
                case "openPath":
                    OpenLocalPath(Str("path"));
                    break;
                case "revealPath":
                    RevealPath(Str("path"));
                    break;
                case "saveFile":
                    _ = SaveFileAsync(view, Str("path"));
                    break;
                case "copyMedia":
                    _ = CopyMediaAsync(view, Str("path"));
                    break;
                case "copyText":
                    if (Str("text") is { } text)
                        host.CopyText(text);
                    break;
                case "copyItem":
                    // 代理回覆的「複製」：網頁只有轉好的 HTML，Markdown 原文在這裡
                    if (Conv() is { } copyFrom && FindItem(copyFrom, Str("itemId")) is { Kind: AgentItemKind.Assistant } reply && !string.IsNullOrEmpty(reply.Text))
                        host.CopyText(reply.Text);
                    break;
                case "popout":
                    if (Conv() is { IsDraft: false } popout)
                        host.OpenPopout(popout.Meta.Id);
                    break;
                case "accountAdd":
                    AccountAddCommand(view, command);
                    break;
                case "accountLogin":
                    AccountLoginCommand(view, command);
                    break;
                case "accountLoginCancel":
                    if (Str("accountId") is { } cancelLoginId)
                        accounts.CancelLogin(cancelLoginId);
                    break;
                case "accountLogout":
                    if (Str("accountId") is { } logoutId)
                        _ = accounts.LogoutAsync(logoutId);
                    break;
                case "accountUpdate":
                    if (Str("accountId") is { } updateId)
                    {
                        accounts.Update(updateId, Str("label"), Str("color"),
                            command["safeMode"]?.Type == JTokenType.Boolean ? command.Value<bool>("safeMode") : null,
                            command["shareCustomizations"]?.Type == JTokenType.Boolean ? command.Value<bool>("shareCustomizations") : null);
                        sync?.OnAccountUpdated(updateId);
                    }
                    break;
                case "accountRemove":
                    AccountRemove(Str("accountId"), command.Value<bool?>("deleteData") ?? false);
                    break;
                case "accountReorder":
                    if (command["ids"] is JArray ids)
                    {
                        accounts.Reorder(ids.Select(i => i.ToString()).ToList());
                        sync?.OnAccountsReordered();
                    }
                    break;
                case "accountRefresh":
                    if (Str("accountId") is { } refreshId)
                        _ = accounts.RefreshAsync(refreshId, includeStatus: true);
                    else
                        _ = accounts.RefreshAllAsync(includeStatus: true);
                    break;
                case "accountResetCredit":
                    if (Str("accountId") is { } resetId)
                        _ = UseResetCreditAsync(view, resetId);
                    break;
                case "accountOpenFolder":
                    if (accounts.Find(Str("accountId")) is { } folderAccount)
                    {
                        var dir = AccountRules.ResolveConfigDirectory(folderAccount);
                        Directory.CreateDirectory(dir);
                        host.RevealInExplorer(dir);
                    }
                    break;
                // CLI 動作在背景執行緒跑（下載、解壓縮、npm 都很久，不能卡在 UI 執行緒）
                case "cliCheck":
                    _ = RunCliActionAsync(null, () => Task.Run(cli.CheckAllAsync));
                    break;
                case "cliInstall":
                    if (ParseProvider(Str("provider")) is { } installProvider)
                        _ = RunCliActionAsync(installProvider, () => Task.Run(() => cli.InstallAsync(installProvider)));
                    break;
                case "cliUpdate":
                    if (ParseProvider(Str("provider")) is { } updateProvider)
                        _ = RunCliActionAsync(updateProvider, () => Task.Run(() => cli.UpdateAsync(updateProvider)));
                    break;
                // CLI 熱更新（AgentHub.Restart.cs）
                case "cliApplyNow":
                    ApplyCliUpdateNow(Conv(), ParseProvider(Str("provider")));
                    break;
                case "cliContinue":
                    ContinueAfterCliUpdate(view, Conv());
                    break;
                case "cliCancelUpdate":
                    if (ParseProvider(Str("provider")) is { } cancelProvider)
                        CancelCliUpdate(cancelProvider);
                    break;
                // app 自我更新（AgentHub.AppUpdate.cs）
                case "draftSnapshot":
                    OnDraftSnapshot(view, Str("requestId"), command["drafts"] as JArray);
                    break;
                case "appUpdateCheck":
                    AppUpdateCommands?.Check();
                    break;
                case "appUpdateRestartNow":
                    AppUpdateCommands?.RestartNow();
                    break;
                case "appUpdateForce":
                    AppUpdateCommands?.ForceRestart();
                    break;
                case "appUpdateCancel":
                    AppUpdateCommands?.CancelRestart();
                    break;
                case "appInstallHere":
                    AppUpdateCommands?.InstallHere();
                    break;
                case "setting":
                    ChangeSettingWithSync(Str("key"), command["value"]);
                    break;
                case "setConversationDefault":
                    SetConversationDefault(Str("provider"), Str("key"), Str("model"), Str("value"));
                    break;
                case "monitor":
                    // 電腦狀態面板（AgentHub.Monitor.cs）
                    SetMonitor(view, command.Value<bool?>("on") == true);
                    break;
                case "mcpCheck":
                    // 儀表板的 MCP（AgentHub.AccountMcp.cs）：切到儀表板時送，明細的「重新檢查」帶 force
                    RequestMcp(command.Value<bool?>("force") == true);
                    break;
                case "mcpAction":
                    // 儀表板 MCP 明細的授權、重新連線（以帳戶為單位，AgentHub.AccountMcp.cs）
                    McpAccountAction(view, Str("accountId"), Str("server"), Str("action"));
                    break;
                case "programStop":
                    // 對話標頭「另開的程式」選單的停止（AgentHub.BackgroundPrograms.cs）
                    StopProgram(view, Conv(), Str("programId"));
                    break;
                case "gitRefresh":
                    // 工作資料夾的 git 狀態「重新檢查」（AgentHub.Git.cs）：連遠端一起問
                    RequestGit(Conv(), GitStatusRules.Trigger.User);
                    break;
                case "launchAction":
                case "launchSelect":
                case "launchLog":
                case "launchRescan":
                    // 執行列：啟動、暫停、停止、重新建置並執行，選目標、拿輸出、重新偵測（AgentHub.Launch.cs）
                    HandleLaunchCommand(view, Str("cmd")!, Conv(), command);
                    break;
                case "healthCheck":
                    // 儀表板的健康度（AgentHub.Health.cs）：切到儀表板時送，「重新分析」帶 force
                    RequestHealth(view, command.Value<bool?>("force") == true);
                    break;
                case "usageSegments":
                    // 儀表板的「每週額度每 5% 的 token」（AgentHub.UsageSegments.cs）：切到儀表板與每分鐘送
                    RequestUsageSegments(view);
                    break;
                case "scheduleWatch":
                    // 排程頁（AgentHub.Schedules.cs）：顯示中每 30 秒重讀，離開時停
                    SetScheduleWatch(view, command.Value<bool?>("on") == true);
                    break;
                case "scheduleRefresh":
                    RequestScheduleScan();
                    TickScheduleTranscripts(force: true);
                    break;
                case "scheduleAction":
                    // 立即執行、停止、啟用、停用、不再列出、納入管理、個別的自動診斷
                    ScheduleAction(view, Str("path"), Str("action"), command.Value<bool?>("on"));
                    break;
                case "scheduleAgent":
                    // 交給代理：管理對話（沒有就開草稿）預先填好說明，不自動送出
                    ScheduleAgent(view, Str("path"), Str("task"), Str("accountId"), Str("folder"), Str("paneId"));
                    break;
                case "timedSave":
                {
                    // 排程頁的新增、修改定時訊息（AgentHub.TimedMessages.cs）
                    var when = command["when"] as JObject;
                    var (_, problem) = SaveTimed(Str("id"), Str("text"), Str("label"), when?.Value<string>("kind"), when?.Value<string>("value"),
                        Str("conversationId"), command.Value<bool?>("newConversation") == true, null);
                    if (problem != null)
                        Toast(problem, isError: true, view);
                    else
                        Toast("已儲存定時訊息", view: view);
                    break;
                }
                case "timedAction":
                    if (TimedAction(Str("id"), Str("action")) is { } timedProblem)
                        Toast(timedProblem, isError: true, view);
                    break;
                case "collabCreate":
                case "collabUpdate":
                case "collabDelete":
                case "collabShare":
                    // 多人協作（AgentHub.Collaborators.cs）：經同步橋交給伺服器；遠端指令的回應檢視不收（不在型錄）
                    if (view is not RemoteResponseView)
                        HandleCollaboratorCommand(view, Str("cmd")!, command);
                    break;
                case "healthFix":
                    // 「用代理合併」「用代理精簡」：開草稿、輸入框預先填好說明
                    HealthFix(view, Str("task"), Str("accountId"), Str("targetAccountId"), Str("paneId"));
                    break;
                case "clientError":
                    // 網頁的錯誤回報（AgentHub.ClientErrors.cs）
                    ReportClientError(view, command);
                    break;
                default:
                    // 〔同步〕sync*、deviceRename 交給同步協調者；沒有同步橋時照舊忽略不認得的指令。
                    // 遠端指令的回應檢視不轉給同步（閘門只放型錄裡由電腦執行的指令進來）
                    if (Str("cmd") is { } other && view is not RemoteResponseView)
                        sync?.HandleCommand(view, other, command);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("處理網頁指令失敗", ex);
            // 遠端指令：結果回給發起的瀏覽器（可能已經執行了一部分，所以是 unknown），不在這台的視窗廣播 toast
            if (view is RemoteResponseView remote)
                remote.Context.Fail($"操作失敗：{ex.Message}", RemoteGateSteps.Handler);
            else
                Toast($"操作失敗：{ex.Message}", isError: true);
        }
    }

    /// <summary>網頁送來的代理種類：只認名稱（"2" 這類數字字串 Enum.TryParse 也會接受，要另外以 IsDefined 擋掉）</summary>
    private static AgentProvider? ParseProvider(string? value)
        => Enum.TryParse<AgentProvider>(value, ignoreCase: true, out var provider) && Enum.IsDefined(provider)
            && !int.TryParse(value, out _) ? provider : null;

    private void OnReady(IAgentView view)
    {
        // 電腦狀態面板：網頁重新載入後收到 init 會再送一次 monitor
        ForgetMonitorView(view.Id);
        viewsReportingVisibility.Remove(view.Id);
        // 網頁（重新）載入：之前顯示在這個 view 的對話都不再算開著（網頁會重新 open 需要的分頁），沒人顯示的草稿丟棄
        foreach (var c in conversations.Values.ToList())
        {
            c.VisibleViewIds.Remove(view.Id);
            c.HeaderListsSent.Remove(view.Id);
            if (!c.ViewIds.Remove(view.Id))
                continue;
            if (c.IsDraft && c.ViewIds.Count == 0)
                DiscardDraft(c);
            else
                OnConversationViewRemoved(c);
        }

        // app 更新後還原的草稿對話在 init 之前建好、由 init 的 restoredDrafts 列出（網頁照存下的版面把它們放回原位、不另開空白的新對話）；
        // 它們的 conversation 在 init 之後由 RunPendingResume 送
        // 只有主視窗還原：另外開的視窗（多視窗）照自己的版面，app 更新前開在那裡的草稿也放回主視窗
        var primary = WindowRules.IsPrimary(view);
        var restoredDrafts = primary ? RestorePendingDrafts(view) : new List<ConversationState>();
        Post(view, new JObject
        {
            ["type"] = "init",
            ["mode"] = view.IsPopout ? "popout" : "main",
            // 多視窗：主視窗 primary、另外開的視窗 extra（搬走最後一個分頁時自己關掉）、彈出視窗 null
            ["windowRole"] = WindowRules.RoleOf(view),
            ["popoutConversationId"] = view is IPopoutView p ? p.ConversationId : null,
            ["app"] = new JObject { ["version"] = AppVersionInfo.VersionText, ["build"] = AppVersionInfo.Current.Build, ["release"] = AppVersionInfo.Current.Release, ["commitAt"] = AppVersionInfo.CommitAtMs },
            ["settings"] = RenderSettings(),
            ["accounts"] = RenderAccounts(),
            ["conversations"] = RenderConversationList(),
            // 其他電腦的對話清單還沒載入（AgentHub.Replica.cs 的 ReplicasPending）：網頁還原版面時不在清單的分頁先留著等
            ["replicasPending"] = replicasPending,
            ["cli"] = RenderCli(),
            ["providers"] = RenderProviders(),
            ["devicesCli"] = sync?.DevicesCli(),
            ["appUpdate"] = appUpdateState?.DeepClone(),
            ["layout"] = LayoutFor(view),
            ["restoredDrafts"] = new JArray(restoredDrafts.Select(c => c.Meta.Id)),
            // 為了搬進某個對話而開的新視窗：那個對話在 init 之後由 RunPendingMoveIn 送（同 restoredDrafts）
            ["movedIn"] = PendingMoveInIds(view),
            ["remote"] = sync?.InitRemote(),
            ["syncStarting"] = sync?.IsStarting ?? false,
            ["tokens"] = RenderTokens(),
            ["health"] = view.IsPopout ? null : RenderHealth(),
            // 排程頁（AgentHub.Schedules.cs）：沒掛讀取器時是 null（網頁不顯示排程頁）
            ["schedules"] = view.IsPopout ? null : RenderSchedules(),
            // 輸入區的麥克風鈕（語音訊息）：這台有辨識程序才顯示
            ["voiceInput"] = voiceTranscriber != null,
            // 多人協作（AgentHub.Collaborators.cs）：伺服器送來的協作人清單與分享；未配對、還沒收到是 null。桌面一律是擁有者
            ["collaborators"] = view.IsPopout ? null : RenderCollaborators(),
            ["viewer"] = new JObject { ["role"] = "owner" },
        });
        ReplayDeviceCodes(view);   // 〔帳戶目錄〕進行中的裝置碼登入重送驗證碼（AgentHub.AccountDirectory.cs）
        RunPendingMoveIn(view);    // 〔多視窗〕拖出分頁、「移到新視窗」開的視窗：把對話搬進來（AgentHub.Windows.cs）

        if (primary && !startupWarningShown)
        {
            startupWarningShown = true;
            foreach (var warning in StartupWarnings())
                Toast(warning, isError: true, view);
        }

        if (primary)
        {
            // app 更新後的接續（第一次主視窗就緒時執行一次：帳戶清單此時已載入），接著是上次結束時還在執行的一輪（resume.json 列出的除外），之後才算健康。
            // 接續自己隔離了例外；健康訊號放在 finally：無論如何都要發，否則好的新版會被健康閘門判失敗而回滾
            try
            {
                var handledByAppResume = AppResumeConversationIds();
                RunPendingResume(view, restoredDrafts);
                RunInterruptedTurnResume(handledByAppResume);
            }
            finally
            {
                MainViewReady?.Invoke();
            }
        }
    }

    // ───────────────────────────── 對話：開啟、關閉、新增 ─────────────────────────────

    /// <summary>最近使用、而且確認還存在的資料夾（背景檢查；網路磁碟離線時 Directory.Exists 會卡住，不能在 UI 執行緒做）</summary>
    private List<string> validRecentFolders = new();
    private bool startupWarningShown;

    private void RememberFolder(string folder)
    {
        settings.RecentFolders = AgentHubRules.PushRecentFolder(settings.RecentFolders, folder);
        validRecentFolders = AgentHubRules.PushRecentFolder(validRecentFolders, folder);
    }

    private async Task RefreshRecentFoldersAsync()
    {
        var snapshot = settings.RecentFolders.ToList();
        try
        {
            var valid = await Task.Run(() => snapshot.Where(f =>
            {
                try
                {
                    return AgentAttachmentRules.IsLocalDrivePath(f) && Directory.Exists(f);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }).ToList()).WaitAsync(TimeSpan.FromSeconds(15));
            if (!disposed)
                validRecentFolders = valid;
        }
        catch (TimeoutException)
        {
            Log.Warn("檢查最近使用的資料夾逾時（可能有離線的網路磁碟）");
        }
    }

    private void NewConversation(IAgentView view, string? accountId, string? paneId)
    {
        var account = accounts.Find(accountId) ?? accounts.Find(settings.LastAccountId) ?? accounts.Accounts.FirstOrDefault();
        if (account == null)
        {
            Toast("請先新增帳戶", isError: true);
            return;
        }
        var state = CreateDraft(view, account);
        PostConversation(view, state, paneId);
        MarkAccountsDirty();
    }

    /// <summary>
    /// 建立草稿對話（還沒送出第一則，只在記憶體）。restoring＝app 更新後還原的草稿對話：沿用原本的 id（資料夾與附件都在那裡）
    /// 與工作資料夾（null＝對話自己的工作區，不改成最近使用的資料夾），也不改「上次使用的帳戶」
    /// </summary>
    private ConversationState CreateDraft(IAgentView view, AgentAccount account, ResumeNewConversation? restoring = null)
    {
        // 模型、推理強度與權限模式一律用設定頁的「模型設定」（模型是「沿用上次選擇」時用這個帳戶上次選的）
        var providerDefaults = ProviderDefaults(account.Provider);
        var models = ModelOptions(account.Id, account.Provider);
        var model = ConversationDefaultsRules.ModelFor(providerDefaults, settings.DefaultsFor(account.Id).Model, models);
        var meta = new AgentConversation
        {
            AccountId = account.Id,
            Provider = account.Provider,
            Title = "新對話",
            Model = model,
            Effort = ConversationDefaultsRules.EffortFor(providerDefaults, model, models),
            PermissionMode = ConversationDefaultsRules.PermissionFor(account.Provider, providerDefaults),
            SessionId = InitialSessionId(account.Provider),
            WorkingDirectory = restoring != null ? restoring.Folder : validRecentFolders.FirstOrDefault(),
        };
        if (restoring != null)
            meta.Id = restoring.DraftId;
        else
            settings.LastAccountId = account.Id;
        var state = new ConversationState(meta) { IsDraft = true, ItemsLoaded = true };
        state.ViewIds.Add(view.Id);
        conversations[meta.Id] = state;
        RequestAntigravityWorkspaceScan(state);
        return state;
    }

    /// <param name="paneId">開在哪個窗格（排程頁的「交給代理」開管理對話；null＝網頁自己決定）</param>
    private void Open(IAgentView view, ConversationState? c, string? paneId = null)
    {
        if (c == null)
            return;
        // 開分頁不等於看到（還原版面時背景分頁也會 open）：未讀等網頁以 visible 回報顯示在畫面上才清掉
        c.ViewIds.Add(view.Id);
        c.LastActivity = DateTime.Now;
        PostConversation(view, c, paneId);
        OnConversationOpened(c);
        RequestAntigravityWorkspaceScan(c);
        MarkListDirty();
        MarkAccountsDirty();
    }

    private void Close(IAgentView view, ConversationState? c)
    {
        if (c == null)
            return;
        var removed = c.ViewIds.Remove(view.Id);
        c.VisibleViewIds.Remove(view.Id);
        c.HeaderListsSent.Remove(view.Id);
        if (c.IsDraft && c.ViewIds.Count == 0)
            DiscardDraft(c);
        else if (removed)
            OnConversationViewRemoved(c);
        MarkAccountsDirty();
    }

    /// <summary>
    /// 網頁回報這個 view 目前顯示在畫面上的對話（各窗格的作用中分頁；儀表板時為空）。
    /// 分頁切換、開關時送，視窗取得焦點時也重送：視窗在前景時，顯示中的對話就算看過了，清掉未讀。
    /// </summary>
    private void SetVisible(IAgentView view, JArray? conversationIds)
    {
        var shown = new HashSet<string>((conversationIds ?? new JArray()).Select(i => i.ToString()), StringComparer.Ordinal);
        // 這個 view 所在的視窗在前景才算看到（使用者在別的視窗操作、主視窗最小化或藏到系統匣都不算）
        bool foreground = host.IsViewForeground(view.Id);
        bool changed = false;
        viewsReportingVisibility.Add(view.Id);
        foreach (var c in conversations.Values)
        {
            if (!shown.Contains(c.Meta.Id) || !c.ViewIds.Contains(view.Id))
            {
                c.VisibleViewIds.Remove(view.Id);
                continue;
            }
            // 切回背景分頁：節流中還沒送的項目更新與標頭立刻送（HiddenFlushRules）
            if (c.VisibleViewIds.Add(view.Id) && (dirtyItems.ContainsKey(c.Meta.Id) || dirtyHeaders.Contains(c.Meta.Id)))
                ScheduleFlush();
            // 切到這個分頁、視窗回到前景：git 狀態太舊就重讀（使用者可能在別處提交、拉取了），Antigravity 工作資料夾自訂項也順便確認
            RequestGit(c, GitStatusRules.Trigger.Open);
            RequestAntigravityWorkspaceScan(c);
            if (foreground && c.Unread)
            {
                c.Unread = false;
                changed = true;
            }
        }
        if (changed)
            MarkListDirty();
        ScheduleActivity(); // 看得到的對話變了：對話框的燈號要開始（或停止）取樣（AgentHub.Activity.cs）
    }

    /// <summary>沒送出的草稿：刪掉附件資料夾、從清單移除</summary>
    private void DiscardDraft(ConversationState c)
    {
        c.Session?.Dispose();
        c.Session = null;
        conversations.Remove(c.Meta.Id);
        // 中樞關閉後（含 ShutdownAsync 還在等代理程序時）視窗才關閉：資料夾刪不刪只由 ShutdownAsync 的 keepDrafts 決定。
        // app 更新的結束流程在 ShutdownAsync 之後（或等它逾時）就關主視窗，這裡若照常刪，resume.json 列出的草稿附件會在結束前被刪掉；
        // 留下的資料夾由下次啟動的草稿清理（FindDraftLeftovers）收
        if (!disposed)
            DeleteFolderInBackground(c.Meta.Id, quiet: true);
    }

    /// <summary>刪對話資料夾（檔案被占用要重試，放到背景執行緒不卡畫面）</summary>
    private void DeleteFolderInBackground(string conversationId, bool quiet)
    {
        // 背景還沒寫的紀錄作廢、正在寫的等它寫完（ItemsFileWriter 不會重建資料夾，這裡只是不跟刪除搶檔案）
        itemsWriter?.Forget(conversationId);
        // 還沒留的媒體副本作廢、正在複製的等它做完（MediaKeeper 也不重建資料夾）
        mediaKeeper?.Forget(conversationId);
        _ = Task.Run(() => store.DeleteConversation(conversationId)).ContinueWith(t =>
        {
            if (!quiet && t.Status == TaskStatus.RanToCompletion && !t.Result)
                host.BeginInvoke(() => Toast($"對話已刪除，但部分檔案仍被其他程式開啟，稍後可手動刪除資料夾：{store.GetConversationDirectory(conversationId)}"));
        }, TaskScheduler.Default);
    }

    private void LoadOlder(IAgentView view, ConversationState? c, string? beforeId)
    {
        if (c == null)
            return;
        var items = EnsureItems(c);
        int end = beforeId == null ? items.Count : items.FindIndex(i => i.Id == beforeId);
        if (end < 0)
            end = items.Count;
        int start = Math.Max(0, end - PageSize);
        var workingDirectory = ShownWorkingDirectory(c);
        Post(view, new JObject
        {
            ["type"] = "olderItems",
            ["conversationId"] = c.Meta.Id,
            // 網頁帶來的 beforeId 原樣送回：網頁只採用自己還在等的那一頁（WP4.6 合併前審查 LIVE-2；web-protocol 的 olderItems）
            ["beforeId"] = beforeId is null ? JValue.CreateNull() : new JValue(beforeId),
            ["items"] = new JArray(items.Skip(start).Take(end - start).Select(i => RenderItemOf(c, i, workingDirectory))),
            ["hasMore"] = start > 0,
        });
    }

    private List<AgentChatItem> EnsureItems(ConversationState c, bool notifyUnreadable = true)
    {
        if (c.ItemsLoaded)
            return c.Items;

        var (items, status, backup) = store.TryLoadItems(c.Meta.Id);
        if (status == JsonReadStatus.Unreadable)
        {
            // 讀不到（被占用）：原檔還在，不標記已載入，存檔時就不會用空清單蓋掉它；下次再讀
            if (notifyUnreadable)
                Toast("這個對話的紀錄檔暫時讀不到（可能被其他程式占用），稍後重新開啟", isError: true);
            return c.Items;
        }

        // 讀不到的期間加進暫時清單的項目（例如換帳戶的提示）接在載入的內容後面，不能被檔案內容整個取代掉
        var pending = c.Items;
        c.Items = items;
        if (status == JsonReadStatus.Corrupt)
        {
            c.Items.Add(new AgentChatItem
            {
                Kind = AgentItemKind.Error,
                Text = $"這個對話的紀錄檔已損毀，原檔已備份為 `{backup}`；之後的內容會記在新的紀錄檔。",
            });
        }
        // 建立順序依清單順序編號（不存檔），之後新增的項目由 AddItem 接著編。讀不到的期間加的項目已經編過號，
        // 但接在載入的內容後面，要跟著往後重編：Seq 才會和清單順序一致（網頁靠它分辨新項目與還沒載入的較早項目），而且只增不減
        // 載入時收尾的項目要讓對話變化的接收者知道（WP4.3 據此發新的持久 conv.item，否則伺服器上那張卡一直停在等待或執行中）
        List<AgentChatItem>? finalized = null;
        foreach (var item in c.Items)
        {
            // 上次沒收尾的項目（app 當掉、關閉）：等待中的問卷／核准已無法回應，執行中的工具視為中斷（引擎唯讀讀檔也照這份收尾，D10）
            if (ItemLoadRules.FinalizeOnLoad(item) && conversationSink != null)
                (finalized ??= new List<AgentChatItem>()).Add(item);
            item.Seq = ++c.LastSeq;
        }
        foreach (var item in pending)
            item.Seq = ++c.LastSeq;
        c.Items.AddRange(pending);
        c.ItemsLoaded = true;
        if (finalized != null)
        {
            foreach (var item in finalized)
                HookItemChanged(c, item);
        }
        // 修正之前延伸出來的對話：其他對話資料夾裡的媒體補留一份（AgentHub.MediaKeep.cs）
        RepairForkMediaLater(c);

        return c.Items;
    }

    // ───────────────────────────── 送出 ─────────────────────────────

    /// <param name="remoteOrigin">
    /// 遠端的 send（WP4.4 計畫 D19）：這個指令的來源。遠端不取本機使用者自己附的待送附件，只帶網頁上傳的（WP4.7）；
    /// 空送而沒有網頁上傳的附件＝從佇列第一則接著送，取出時用這次的來源
    /// </param>
    /// <param name="displayText">
    /// 畫面、存檔、標題與同步看的文字（語音訊息的「語音訊息（0:12）」，AgentHub.Voice.cs）；null＝同 <paramref name="text"/>。
    /// 有它時被拒的文字不還給輸入框（<paramref name="text"/> 是給代理的長說明，不是使用者打的字）
    /// </param>
    private async Task SendAsync(IAgentView view, ConversationState? c, string text, RemoteOrigin? remoteOrigin = null, string? displayText = null)
    {
        if (c == null)
            return;
        // 被拒時要不要把文字還給輸入框：語音訊息（有 displayText）不還
        var restorable = displayText == null;
        if (c.CodexControlBusy)
        {
            RejectSend(view, c, restorable ? text : null, "Codex 設定正在套用，請稍候再送出");
            return;
        }
        // /clear：草稿、執行中與交給中樞自己清的在這裡就處理完（TryHandleClear）；交給 CLI 的照一般送出，但不帶輸入區的附件
        bool clear = AgentHubRules.IsClearCommand(text);
        if (clear && TryHandleClear(view, c, text.Trim(), remoteOrigin))
            return;
        // /btw：旁支提問，不送給代理、不排佇列、執行中也照問（AgentHub.SideQuestion.cs）
        if (TryHandleSideQuestion(view, c, text, remoteOrigin))
            return;
        if (c.ImportingCount > 0)
        {
            RejectSend(view, c, restorable ? text : null, "附件還在處理中，請稍候");
            return;
        }

        text = (text ?? string.Empty).Trim();
        List<AgentAttachment> attachments;
        QueuedMessage? fromQueue = null;
        // 有沒有要送的附件：本機看輸入區全部，遠端只看網頁上傳的（只附檔、不打字的遠端 send 和本機一樣送出附件；WP4.7 合併前審查 INT-1）
        if (text.Length == 0 && (remoteOrigin != null ? RemoteUploadedPending(c).Count == 0 : c.PendingAttachments.Count == 0))
        {
            // 佇列暫停中（上一輪被停止或失敗）：空白送出＝從佇列第一則接著送。第一則還在插進剛結束的那一輪（SteerAsync 還沒回來）就先不送，可能已經送到代理
            if (c.Running || c.Queue.Count == 0 || ReferenceEquals(c.Queue[0], c.SteeringMessage))
                return;
            fromQueue = c.Queue[0];
            text = fromQueue.Text;
            attachments = fromQueue.Attachments;
        }
        else
        {
            // 遠端只帶網頁上傳的附件（WP4.7 計畫步 13，AgentHub.RemoteFiles.cs），本機使用者自己附的留在輸入區
            attachments = clear ? new List<AgentAttachment>() : remoteOrigin == null ? c.PendingAttachments.ToList() : RemoteUploadedPending(c);
        }

        // 送進代理的來源（D19、D20）：遠端的一律是這次指令的；本機按送出時，被 HoldRemoteSend 留下過的那則（本機看過提示）當成本機的訊息
        var origin = remoteOrigin ?? (fromQueue is { Held: true } && view is not RemoteResponseView ? null : fromQueue?.RemoteOrigin);

        // 紀錄檔暫時讀不到：不能在暫時的空清單上續接（之後讀得到時對不上原本的紀錄，這段期間也存不了檔）
        EnsureItems(c, notifyUnreadable: false);
        if (!c.ItemsLoaded)
        {
            RejectSend(view, c, fromQueue == null && restorable ? text : null, "這個對話的紀錄檔暫時讀不到（可能被其他程式占用），訊息沒有送出，請稍後再試");
            return;
        }

        var account = accounts.Find(c.Meta.AccountId);
        if (account == null)
        {
            RejectSend(view, c, fromQueue == null && restorable ? text : null, "這個對話的帳戶已被移除，請先換一個帳戶");
            return;
        }
        // CLI 正在更新（更新屏障、安裝正在變動、npm 正在替換檔案）：這時啟動 CLI 一定失敗，排進佇列、更新完成後自動送出
        // （帳戶查詢也可能暫時把它看成未安裝，所以先判斷）
        if (QueueSendDuringCliUpdate(view, c, account.Provider, text, attachments, fromQueue, origin, displayText))
            return;
        var status = accounts.GetState(account.Id).Status;
        if (status is AccountStatus.NotLoggedIn or AccountStatus.NotInstalled)
        {
            RejectSend(view, c, fromQueue == null && restorable ? text : null, $"「{account.Label}」{AccountRules.DescribeStatus(account.Provider, status)}");
            return;
        }

        if (fromQueue == null && attachments.Count > 0)
        {
            // 附件在背景檢查資料夾之前就從輸入區取走：等待期間再按一次送出不會把同一批附件又排一次，按 x 移除也不會刪到要送出的檔案
            foreach (var attachment in attachments)
                c.PendingAttachments.Remove(attachment);
            PostComposer(c);
        }

        if (!string.IsNullOrWhiteSpace(c.Meta.WorkingDirectory))
        {
            // 在背景檢查（網路磁碟離線時 Directory.Exists 會卡到 SMB 逾時，不能卡住畫面）
            var workingDirectory = c.Meta.WorkingDirectory!;
            var exists = await DirectoryExistsAsync(workingDirectory);
            if (exists != true)
            {
                if (fromQueue == null && attachments.Count > 0)
                {
                    // 沒送出：附件還回輸入區（放回原本的位置，排在等待期間新附的檔案前面）；遠端帶的仍算網頁上傳的
                    // （等待期間另一則遠端 send 會把已不在輸入區的 id 拿掉；WP4.7 合併前審查 INT-4）
                    c.PendingAttachments.InsertRange(0, attachments);
                    if (remoteOrigin != null)
                        c.RemoteUploadedAttachmentIds.UnionWith(attachments.Select(a => a.Id));
                    PostComposer(c);
                }
                RejectSend(view, c, fromQueue == null && restorable ? text : null,
                    exists == null ? $"工作資料夾無法存取（逾時）：{workingDirectory}" : $"工作資料夾不存在：{workingDirectory}，請重新選擇");
                return;
            }
            if (!conversations.ContainsKey(c.Meta.Id))
                return;
        }

        if (fromQueue != null)
        {
            // 等待期間另一則已經開始執行（新訊息、回答提問）：這則留在佇列，那一輪正常完成後照常送出；已被取走或取消就不送
            if (c.Running || !c.Queue.Remove(fromQueue))
                return;
            PostComposer(c);
        }
        else if (c.Running)
        {
            // 執行中：訊息直接插進這一輪（代理下一步就讀到；遠端送來的插之前以現在的模式與政策重查）；插不進去或不允許的
            // 就留在佇列，這一輪正常完成後自動送出（遠端的帶著來源，Id 是 "r"＋cid，送出前經 HoldRemoteSend 重查；輸入區由 SteerQueuedAsync 更新）
            c.Queue.Add(new QueuedMessage { Text = text, Attachments = attachments, RemoteOrigin = origin, DisplayText = displayText });
            await SteerQueuedAsync(c);
            return;
        }

        await SendCoreAsync(c, text, attachments, answerFor: fromQueue?.AnswerFor, displayText: fromQueue?.DisplayText ?? displayText, remoteOrigin: origin);
    }

    /// <summary>
    /// /clear（AgentHubRules.IsClearCommand）：還沒有內容的草稿不必清；執行中不清（不插進這一輪、也不排隊）；Claude 已開始的 session 交給 CLI
    /// （回 false，照一般送出，這一輪正常結束才加清除提示，CompleteTurn），其他由中樞自己清（ClearLocally）。回 true＝已處理
    /// </summary>
    private bool TryHandleClear(IAgentView view, ConversationState c, string text, RemoteOrigin? remoteOrigin)
    {
        if (c.IsDraft)
        {
            const string empty = "這個對話還沒有內容，不需要清除";
            if (remoteOrigin != null)
                RejectSend(view, c, null, empty);
            else
                Toast(empty, view: view);
            return true;
        }
        if (c.Running)
        {
            RejectSend(view, c, text, "代理執行中不能清除對話，請先停止或等這一輪結束");
            return true;
        }
        // Claude 還沒開始的 session（換了帳戶、上一則沒建立成功）：CLI 那邊沒有要清的，送出去還會被接上前文節錄
        if (AgentHubRules.CliHandlesClear(c.Meta.Provider) && c.Meta.SessionStarted)
            return false;
        ClearLocally(view, c, text, remoteOrigin);
        return true;
    }

    /// <summary>
    /// 中樞自己做的 /clear：關掉程序、換新的 session（同換帳戶，但不附前文節錄），等著回答的卡片作廢，加清除提示。
    /// 項目不刪：畫面藏起提示之前的（網頁 liverules.clearBoundary），前文節錄也只收提示之後的
    /// </summary>
    private void ClearLocally(IAgentView view, ConversationState c, string text, RemoteOrigin? remoteOrigin)
    {
        // 紀錄檔暫時讀不到：清除提示存不進去（同送出）
        EnsureItems(c, notifyUnreadable: false);
        if (!c.ItemsLoaded)
        {
            RejectSend(view, c, text, "這個對話的紀錄檔暫時讀不到（可能被其他程式占用），沒有清除，請稍後再試");
            return;
        }
        // 正在以新版 CLI 重開這個對話：換掉會話會和重開的流程互相踩到
        if (IsRestartInFlight(c))
        {
            RejectSend(view, c, text, "這個對話正在換用新版的 CLI，請稍候再清除");
            return;
        }

        JournalProcessClose(c, "clear");
        c.Session?.Dispose();
        c.Session = null;
        c.Meta.SessionId = InitialSessionId(c.Meta.Provider);
        c.Meta.SessionStarted = false;
        c.CodexGoal = null;
        c.Meta.SessionAccountId = null;
        c.ContextUsage = null;
        // 等著回答的卡片（Codex 的非同步提問）屬於舊的 session，畫面上也藏起來了
        foreach (var item in c.Items.Where(i => i.Status == AgentItemStatus.Waiting).ToList())
        {
            item.Status = AgentItemStatus.Cancelled;
            MarkItemDirty(c, item);
        }
        c.RequestItems.Clear();
        AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = AgentHubRules.ClearNoticeText, NoticeKind = AgentNoticeKind.Clear });
        var now = DateTime.Now;
        c.LastActivity = now;
        c.Meta.UpdatedAt = now;
        SaveConversation(c, saveItems: true);
        RefreshRestarts(released: [c]);
        MarkHeaderDirty(c);
        MarkListDirty();
        RemoteInputAccepted(remoteOrigin);
    }

    /// <summary>
    /// 佇列最前面這則可以插進這一輪：本機的一律可以；遠端來源的要沒被留下過、而且現在仍然允許送進代理。
    /// @ 了 AI 帳戶的不插（AgentHub.Delegation.cs）：要在回合之間重開程序掛上委派的 MCP，等這一輪結束再照常送出
    /// </summary>
    private bool CanSteerQueued(ConversationState c, QueuedMessage message)
        => (message.RemoteOrigin is not { } origin || (!message.Held && RemoteInputAllowedNow(c, origin)))
           && !MentionsAccounts(c, message.Text);

    /// <summary>
    /// 執行中把佇列裡的訊息依序插進這一輪（IAgentSession.SteerAsync），成功的移出佇列、加成使用者訊息；
    /// 插不進去（停止、換版、代理不支援…）就停下，剩下的留在佇列等這一輪結束。同一個對話同時只有一個在插（後到的交給它接著處理），維持先後
    /// </summary>
    private async Task SteerQueuedAsync(ConversationState c)
    {
        if (c.SteeringMessage != null)
        {
            PostComposer(c);
            return;
        }
        // 遠端來源的（網頁版、其他電腦送來的）也插：插之前以現在的模式與政策重查（與 HoldRemoteSend 同一個條件，RemoteInputAllowedNow）。
        // 不允許的、和已被 HoldRemoteSend 留下等本機確認的（Held）不插，留在佇列等這一輪結束照舊處理；排在它後面的也跟著等，維持先後
        while (c.Queue.Count > 0 && CanSteerQueued(c, c.Queue[0]) && CanSteer(c))
        {
            var message = c.Queue[0];
            var session = c.Session!;
            var steerUuid = c.Meta.Provider == AgentProvider.Claude ? Guid.NewGuid().ToString() : null;
            using var cancel = new CancellationTokenSource();
            c.SteeringMessage = message;
            c.SteerCancel = cancel;
            bool steered;
            try
            {
                var steering = session.SteerAsync(new AgentTurnInput(message.Text, message.Attachments, steerUuid), cancel.Token);
                if (!steering.IsCompleted)
                    _ = ShowSteeringLaterAsync(c, message);
                steered = await steering;
            }
            catch (OperationCanceledException)
            {
                steered = false;
            }
            catch (Exception ex)
            {
                Log.Warn($"插入訊息失敗：{ex.Message}");
                steered = false;
            }
            finally
            {
                c.SteeringMessage = null;
                c.SteerCancel = null;
            }
            if (!conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
                return;
            if (!steered)
                break;

            // 已送到代理：等待期間按 × 取消的也一樣（附件已還回輸入區，使用者再自己移除）
            c.Queue.Remove(message);
            if (ReferenceEquals(c.Restart.DeferredSend, message))
            {
                c.Restart.DeferredSend = null;
                MarkHeaderDirty(c);
            }
            var now = DateTime.Now;
            // 畫面、存檔與同步看遮蔽後的文字（秘密回答，D20）；代理收到的是原文
            var steeredItem = new AgentChatItem
            {
                Kind = AgentItemKind.User, Text = message.DisplayText ?? message.Text, Attachments = message.Attachments, CliUuid = steerUuid,
                Author = message.RemoteOrigin?.Author,
            };
            AddItem(c, steeredItem);
            // 最近插入的這則可以取消（CLI 還沒讀走時，AgentHub.SteerCancel.cs）
            if (steerUuid != null)
                RememberSteer(c, steeredItem, steerUuid, message);
            c.LastActivity = now;
            c.Meta.UpdatedAt = now;
            MarkListDirty();
            // 插進去的訊息先上畫面再存檔（同 SendCoreAsync）
            FlushNow();
            SaveConversation(c, saveItems: true);
        }
        PostComposer(c);

        // 插入途中這一輪已正常結束（CompleteTurn 跳過了插入中的那則）：佇列照常接著送，沒有要送的就照常通知完成
        if (c.SendQueueAfterSteer)
        {
            c.SendQueueAfterSteer = false;
            if (c.Running)
                return;
            if (c.Queue.Count > 0)
            {
                var next = c.Queue[0];
                c.Queue.RemoveAt(0);
                PostComposer(c);
                await SendCoreAsync(c, next.Text, next.Attachments, answerFor: next.AnswerFor, displayText: next.DisplayText, remoteOrigin: next.RemoteOrigin);
                return;
            }
            NotifyAttention(c, "代理已完成", c.Meta.Title, ConversationAttentionKinds.Completed, speech: CompletionSpeech(c));
        }
    }

    /// <summary>
    /// 插入等了一陣子（這一輪還在準備）才在佇列顯示「正在插入」：通常幾毫秒就插進去，一開始就顯示會讓佇列閃一下。
    /// 還沒顯示前輸入區照樣能送出、停止（狀態都在中樞）
    /// </summary>
    private async Task ShowSteeringLaterAsync(ConversationState c, QueuedMessage message)
    {
        await Task.Delay(SteeringDisplayDelay);
        if (ReferenceEquals(c.SteeringMessage, message))
            PostComposer(c);
    }

    private static readonly TimeSpan SteeringDisplayDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>現在可以把訊息插進這一輪：在執行、沒按停止、會話是這個代理的、沒有在換版或等 CLI 更新、代理支援插入</summary>
    private bool CanSteer(ConversationState c)
        => c.Running && !c.StopRequested && !c.PausingForQuestion && c.Session != null && c.Session.Provider == c.Meta.Provider
           && AgentHubRules.SupportsSteering(c.Meta.Provider)
           && !IsRestartInFlight(c) && !IsCliUpdateBlocking(c.Meta.Provider);

    /// <summary>背景檢查資料夾是否存在；逾時（網路磁碟）回 null</summary>
    private static async Task<bool?> DirectoryExistsAsync(string path)
    {
        try
        {
            return await Task.Run(() => Directory.Exists(path)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <param name="text">退回給輸入框的原文；null＝不送 sendRejected（從佇列接著送的那則還在佇列裡）</param>
    private void RejectSend(IAgentView view, ConversationState c, string? text, string? message)
    {
        if (view is RemoteResponseView)
        {
            // 遠端：形狀照舊（伺服器照型錄的輸出白名單轉給發起的瀏覽器或電腦）；沒有文字（只附檔）不送
            if (!string.IsNullOrEmpty(text))
                Post(view, new JObject { ["type"] = "sendRejected", ["conversationId"] = c.Meta.Id, ["text"] = text });
        }
        else if (text != null)
        {
            // 本機：帶原因、只附檔沒打字的也送——網頁把對話欄裡「送出中」的那則標成沒有送出並寫出原因（Web/sendrules.js）
            var rejected = new JObject { ["type"] = "sendRejected", ["conversationId"] = c.Meta.Id, ["text"] = text };
            if (message != null)
                rejected["reason"] = message;
            Post(view, rejected);
        }
        if (message != null)
            Toast(message, isError: true, view);
        // 遠端的 send（WP4.4 計畫 D19）：sendRejected 與提示已轉給發起的瀏覽器，結果是 conflict（什麼都沒送出）
        if (view is RemoteResponseView remote)
            remote.Context.Reject(RemoteResultCodes.Conflict, message ?? "訊息沒有送出", RemoteGateSteps.Handler);
    }

    /// <param name="hiddenNotice">
    /// 不為 null＝隱藏送出（CLI 換版後按「繼續」、app 更新後的接續訊息）：不建立使用者項目，改加一則這段文字的 Notice（noticeKind 照 hiddenNoticeKind）
    /// </param>
    /// <param name="answerFor">這則是 Codex 非同步提問的答案（卡片已標成已回答）：被延後排進佇列時帶著，取消那則排隊訊息時卡片才回得到可作答</param>
    /// <param name="hiddenNoticeKind">隱藏送出那則 Notice 的種類：CLI 換版的接續是 CliUpdate、app 更新後的接續是 AppUpdate；null＝一般提示（API 過載後的自動重試）</param>
    /// <param name="displayText">
    /// 畫面、存檔、標題與同步看的文字（秘密回答遮蔽後的，設計 14.11 D20）；null＝同 <paramref name="text"/>。CLI 一律收 <paramref name="text"/>
    /// </param>
    /// <param name="remoteOrigin">從遠端送進來的輸入的來源（D13）：送出前以 RemoteQueueRules.CanDequeue 再查一次，不允許就留在佇列；null＝本機輸入，不查</param>
    /// <param name="returnHome">false＝不先換回額度用完之前的原帳戶（Codex 拒絕時附的說明：要給拒絕的那個程序）</param>
    private async Task SendCoreAsync(ConversationState c, string text, List<AgentAttachment> attachments, string? hiddenNotice = null, AgentChatItem? answerFor = null,
        AgentNoticeKind? hiddenNoticeKind = AgentNoticeKind.CliUpdate, string? displayText = null, RemoteOrigin? remoteOrigin = null, bool rememberLocal = true,
        bool returnHome = true)
    {
        // 遠端來源的輸入：入列後模式被調高、政策不再允許或遠端已暫停就留在佇列（AgentHub.Remote.cs）
        if (HoldRemoteSend(c, text, attachments, answerFor, displayText, remoteOrigin))
            return;
        // CLI 正在更新：延後到更新完成（佇列接著送、拒絕的說明、非同步提問的答案都經過這裡）
        if (DeferSendForCliUpdate(c, text, attachments, hidden: hiddenNotice != null, answerFor, displayText, remoteOrigin))
            return;
        // 這個對話正在換版（關掉舊程序或以新版重開進行中）：不呼叫 SendAsync，排進佇列、重開做完後照常送出
        if (DeferSendDuringRestart(c, text, attachments, hidden: hiddenNotice != null, answerFor, displayText, remoteOrigin))
            return;
        // 額度用完自動換走的對話、原帳戶的額度恢復了：這則先換回原帳戶再送（AgentHub.AccountSwitch.cs）。
        // 隱藏送出（換帳戶、換版、背景工作的接續）、問卷的答案、斜線指令不換
        if (returnHome && hiddenNotice == null && answerFor == null && AccountSwitchRules.ReturnsBeforeSend(text))
        {
            // 選了帳戶、在等背景工作結束才換的：背景工作都結束了（維護還沒輪到）就先換過去，這則由選的帳戶送
            if (!TryApplyDeferredAccountSwitch(c))
                TryReturnHome(c, fromSend: true, DateTime.Now);
        }
        EnsureItems(c);
        var previousRestartResult = c.Restart.Result;
        OnSendStartingForRestart(c);
        var now = DateTime.Now;
        var shownText = displayText ?? text;
        if (c.IsDraft)
        {
            c.IsDraft = false;
            c.Meta.Title = AgentHubRules.MakeTitle(shownText, attachments);
            c.Meta.CreatedAt = now;
        }

        // 新 session 但已有前文（換了帳戶、上一則沒建立成功、延伸出來的對話）：把前文節錄附在訊息前面
        var recap = c.Meta.ForkedFrom != null && !c.Meta.SessionStarted
            ? ConversationForkRules.BuildRecap(c.Items)
            : AgentHubRules.BuildHistoryRecap(c.Items);
        var sendText = !c.Meta.SessionStarted && recap != null ? recap + text : text;

        // Claude 的使用者訊息：寫給 CLI 的 uuid 由這裡給、記在項目（回溯以它指名那則，AgentHub.Rewind.cs）
        var cliUuid = hiddenNotice == null && c.Meta.Provider == AgentProvider.Claude ? Guid.NewGuid().ToString() : null;
        var sentItem = hiddenNotice == null
            ? new AgentChatItem { Kind = AgentItemKind.User, Text = shownText, Attachments = attachments, CliUuid = cliUuid, Author = remoteOrigin?.Author }
            : new AgentChatItem { Kind = AgentItemKind.Notice, Text = hiddenNotice, NoticeKind = hiddenNoticeKind };
        AddItem(c, sentItem);
        // 使用者送出（本機或遠端）：背景指令跑完的自動接續、API 過載的自動重試重新算次數（AgentHub.Background.cs、AgentHub.OverloadRetry.cs）
        if (hiddenNotice == null)
        {
            c.BackgroundContinues = 0;
            c.QuotaResumeAttempts = 0;
            c.OverloadRetries = 0;
        }
        PostComposer(c);

        c.Running = true;
        c.StopRequested = false;
        c.DeniedThisTurn = false;
        c.QuotaExhausted = null;
        // 交給 Claude CLI 的 /clear（佇列、CLI 更新後延後送出的也經過這裡；前面附了前文節錄就不是指令）：這一輪正常結束才清畫面
        c.ClearTurn = hiddenNotice == null && ReferenceEquals(sendText, text) && AgentHubRules.IsClearCommand(text) && AgentHubRules.CliHandlesClear(c.Meta.Provider);
        c.StatusText = SendingStatus;
        c.TurnStartedAt = now;
        c.LastActivity = now;
        c.Meta.UpdatedAt = now;
        HookTurnStarted(c, startedByCli: false);
        var settingsChanged = false;
        // rememberLocal＝false：中樞代遠端發起的動作送的（遠端在執行中換了帳戶之後的接續），同遠端不寫；
        // 委派的子對話（AgentHub.Delegation.cs）是主 AI 送的，也不改使用者上次用的帳戶與最近資料夾
        if (remoteOrigin == null && rememberLocal && c.Meta.ParentConversationId == null)
        {
            // 遠端不寫這台的持久設定（D15）：上次用的帳戶、最近資料夾只由本機的送出更新（遠端選的資料夾不因送出變成「已知」）
            var lastAccountBefore = settings.LastAccountId;
            var foldersBefore = settings.RecentFolders;
            settings.LastAccountId = c.Meta.AccountId;
            if (!string.IsNullOrWhiteSpace(c.Meta.WorkingDirectory))
                RememberFolder(c.Meta.WorkingDirectory!);
            settingsChanged = lastAccountBefore != settings.LastAccountId || !foldersBefore.SequenceEqual(settings.RecentFolders, StringComparer.Ordinal);
        }

        UpdateRunningCounts();
        accounts.ReportActivity(c.Meta.AccountId);
        MarkHeaderDirty(c);
        MarkListDirty();
        // 使用者的訊息與執行中的標頭先上畫面，再存檔（存檔在 UI 執行緒，存完之前畫面收不到任何東西）
        FlushNow();
        SaveConversation(c, saveItems: true);
        // 設定檔（含各帳戶的指令與模型清單，約 100 KB）在 UI 執行緒寫入並落盤：通常同一個帳戶、同一個資料夾連續送，沒變就不寫
        if (settingsChanged)
            settings.Save();
        // 遠端的 send：使用者項目已建立就是中樞已接受（WP4.4 計畫 D19；不等 CLI 啟動，第一次啟動慢也不會變成 unknown）
        RemoteInputAccepted(remoteOrigin);

        IAgentSession? session = null;
        try
        {
            // @ 其他 AI 帳戶（AgentHub.Delegation.cs）：記下選取的帳戶、附上給主 AI 的說明（畫面上的使用者訊息不含）；要先於會話設定（決定掛不掛 MCP）
            sendText += PrepareAccountDelegation(c, text, remoteOrigin);
            var sessionSettings = BuildSessionSettings(c, recap);
            session = EnsureSession(c);
            await session.SendAsync(new AgentTurnInput(sendText, attachments, cliUuid), sessionSettings);
            c.Restart.RejectedSends = 0;
            if (c.Running && c.StatusText == SendingStatus)
                SetStatus(c, "思考中…");
            EvaluatePendingAccountSwitch(c);
        }
        catch (AgentRestartInProgressException) when (!Abandoned(c, session))
        {
            // 會話正在以新版重開（中樞照理不會在重開期間送出；萬一錯過）：不是這一輪失敗——訊息放回佇列最前面，重開做完後再送
            RequeueSendRejectedByRestart(c, sentItem, text, attachments, hiddenNotice != null, previousRestartResult, answerFor, displayText, remoteOrigin);
        }
        catch (AgentCliUnavailableException ex)
        {
            if (Abandoned(c, session))
                return;
            FailTurn(c, ex.Message);
            cliDirty = true;
            ScheduleFlush();
        }
        catch (Exception ex)
        {
            if (Abandoned(c, session))
                return;
            FailTurn(c, $"送出失敗：{ex.Message}");
        }
    }

    /// <summary>送出途中對話被刪除、帳戶被移除或會話被換掉：結果已由那邊收尾，不再回報送出失敗</summary>
    private bool Abandoned(ConversationState c, IAgentSession? session)
        => !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c) || (session != null && !ReferenceEquals(c.Session, session));

    /// <summary>
    /// 本機收尾一輪（帳戶被移除這類情況，CLI 已經不會回報 TurnCompleted）：與 CompleteTurn(已停止) 相同的清理——
    /// 執行中的工具改為已停止、等待中的問卷與核准失效、清掉這一輪的暫存狀態
    /// </summary>
    private void AbortTurnLocally(ConversationState c, string notice)
    {
        EnsureItems(c);
        foreach (var item in c.Items.Where(i => i.Status == AgentItemStatus.Running).ToList())
        {
            if (item.Kind is AgentItemKind.Assistant or AgentItemKind.Thinking && string.IsNullOrWhiteSpace(item.Text) && item.MediaPaths.Count == 0)
            {
                RemoveItem(c, item);
                continue;
            }
            item.Status = item.Kind == AgentItemKind.Tool ? AgentItemStatus.Cancelled : AgentItemStatus.Done;
            MarkItemDirty(c, item);
        }
        foreach (var item in c.Items.Where(i => i.Status == AgentItemStatus.Waiting && !i.AnswerAsMessage).ToList())
        {
            item.Status = AgentItemStatus.Cancelled;
            MarkItemDirty(c, item);
        }
        AddItem(c, new AgentChatItem { Kind = AgentItemKind.Notice, Text = notice });
        var wasRunning = c.Running;
        c.Running = false;
        c.StatusText = string.Empty;
        if (wasRunning)
            HookTurnCompleted(c, AgentTurnOutcome.Interrupted, null);
        c.PendingFeedback.Clear();
        PersistQueueIfChanged(c);
        c.BlockItems.Clear();
        c.ToolItems.Clear();
        c.UnapprovedTools.Clear();
        foreach (var key in c.RequestItems.Where(kv => !kv.Value.AnswerAsMessage).Select(kv => kv.Key).ToList())
            c.RequestItems.Remove(key);
        c.PlanItem = null;
        SaveConversation(c, saveItems: true);
        UpdateRunningCounts();
        MarkHeaderDirty(c);
        MarkListDirty();
    }

    private void FailTurn(ConversationState c, string message)
    {
        var wasRunning = c.Running;
        c.Running = false;
        c.StatusText = string.Empty;
        if (wasRunning)
            HookTurnCompleted(c, AgentTurnOutcome.Failed, null);
        AddItem(c, new AgentChatItem { Kind = AgentItemKind.Error, Text = message });
        SaveConversation(c, saveItems: true);
        UpdateRunningCounts();
        MarkHeaderDirty(c);
        MarkListDirty();
    }

    private IAgentSession EnsureSession(ConversationState c)
    {
        if (c.Session != null && c.Session.Provider == c.Meta.Provider)
            return c.Session;

        c.Session?.Dispose();
        var session = sessionFactory(c.Meta.Provider);
        var conversationId = c.Meta.Id;
        session.EventReceived += ev => host.BeginInvoke(() =>
        {
            if (!disposed && conversations.TryGetValue(conversationId, out var state) && state.Session == session)
                OnSessionEvent(state, ev);
        });
        c.Session = session;
        return session;
    }

    private string GetWorkingDirectory(ConversationState c)
        => string.IsNullOrWhiteSpace(c.Meta.WorkingDirectory) ? store.GetDefaultWorkingDirectory(c.Meta.Id) : c.Meta.WorkingDirectory!;

    private AgentSessionSettings BuildSessionSettings(ConversationState c, string? historyRecap)
    {
        var account = accounts.Find(c.Meta.AccountId) ?? throw new InvalidOperationException("這個對話的帳戶已被移除");
        var workingDirectory = GetWorkingDirectory(c);
        var conversationDirectory = store.GetConversationDirectory(c.Meta.Id);
        Directory.CreateDirectory(conversationDirectory);
        // 只建立對話自己的預設 workspace；使用者選的資料夾被刪掉時不偷偷重建（CLI 啟動會失敗並回報）
        if (string.IsNullOrWhiteSpace(c.Meta.WorkingDirectory))
            Directory.CreateDirectory(workingDirectory);
        // 隔離帳戶共用系統預設帳戶的設定＋選用的跨四家 CLI 共用：開關改變時 AccountKey 跟著變，程序下次送出時重開
        var sharedCustomizations = accounts.Shared;
        var shared = sharedCustomizations != null ? SharedCustomizations.Snapshot(account) : null;
        bool crossCli = sharedCustomizations != null && settings.ShareCrossCliCustomizations && !account.SafeMode;
        return new AgentSessionSettings(
            c.Meta.SessionId,
            c.Meta.SessionStarted,
            cli.GetLauncher(account.Provider),
            $"{account.Id}|{AccountRules.ResolveConfigDirectory(account)}{(shared != null ? "|shared" : string.Empty)}{(crossCli ? "|cross" : string.Empty)}",
            env =>
            {
                AccountRules.ApplyEnvironment(account, env);
                // 這個 CLI 與它開出的所有程序屬於哪個對話：代理開的程式在這一輪結束後還在跑時，帳戶卡以它找得到（AgentHub.Background.cs）
                env[BackgroundProgramRules.MarkerVariable] = c.Meta.Id;
                // 遠端執行指令（AgentHub.RemoteExec.cs）：myriagent-remote 的管道、這個對話的權杖與它所在的資料夾
                ApplyRemoteExecEnvironment(c.Meta.Id, env);
            },
            account.Provider == AgentProvider.Codex && !account.IsSystemDefault ? account.ConfigDirectory : null,
            c.Meta.Model,
            c.Meta.Effort,
            AgentPermissionModes.Normalize(account.Provider, c.Meta.PermissionMode),
            workingDirectory,
            conversationDirectory,
            AgentHubRules.BuildAgentInstructions(settings.Instructions, account.Provider, workingDirectory, store.GetAttachmentDirectory(c.Meta.Id))
                + RemoteExecInstructions() + TimedInstructions(),
            UseLocalCustomizations: !account.SafeMode,
            HistoryRecap: historyRecap,
            Cli: SessionCliIdentity(c, account.Provider),
            RemoteControl: RemoteControlOptions(c),
            Title: c.IsDraft ? null : c.Meta.Title,
            PrepareShared: shared == null && !crossCli ? null : cwd => sharedCustomizations!.Prepare(shared, cwd, crossCli),
            Agent: account.Provider switch
            {
                AgentProvider.Antigravity => account.SafeMode ? null : c.Meta.Agent,
                AgentProvider.Claude => account.SafeMode ? null : ClaudeCustomizationRules.AgentForLaunch(c.Meta.Agent, settings.ClaudeAgents.GetValueOrDefault(account.Id)),
                _ => null,
            },
            FallbackModel: account.Provider == AgentProvider.Claude ? settings.ClaudeFallbackModel : null,
            OutputStyle: account.Provider == AgentProvider.Claude && !account.SafeMode ? ClaudeCustomizationRules.StyleForLaunch(c.Meta.OutputStyle, settings.OutputStyles.GetValueOrDefault(account.Id)) : null,
            ResumeAt: account.Provider == AgentProvider.Claude ? c.Meta.ResumeAt : null,
            ForkSource: account.Provider == AgentProvider.Claude ? c.Meta.ForkSourceTranscript : null,
            CodexServiceTier: c.Meta.CodexServiceTier,
            CodexSkills: c.Meta.CodexSkills,
            DelegationMcpPipeName: DelegationMcpFor(c)?.PipeName,
            // 自動壓縮上限是這一家 CLI 的設定：已開的對話也套用（改了下次送出時重開程序）
            AutoCompactTokens: AutoCompactRules.ForProvider(account.Provider, ProviderDefaults(account.Provider).AutoCompactTokens),
            AutoCompactPercent: AutoCompactRules.PercentForProvider(account.Provider, ProviderDefaults(account.Provider).AutoCompactPercent));
    }

    /// <summary>設定頁開著「Claude 對話開啟 Remote Control」時的選項：以對話標題命名、接回上次的遠端工作階段（只有 Claude 會用，其他代理忽略）</summary>
    private AgentRemoteControlOptions? RemoteControlOptions(ConversationState c)
        => settings.ClaudeRemoteControl ? new AgentRemoteControlOptions(c.Meta.Title, c.Meta.RemoteControlSessionId) : null;

    /// <summary>
    /// 設定頁切換 Remote Control：通知所有會話。活著的程序立即套用；正在啟動或重開（還沒有程序）的由會話記下，新程序照新設定，
    /// 不會沿用送出當時的舊值
    /// </summary>
    private void ApplyRemoteControlSetting()
    {
        foreach (var c in conversations.Values.Where(c => c.Session != null).ToList())
            _ = ApplyRemoteControlAsync(c.Session!, RemoteControlOptions(c));
    }

    private static async Task ApplyRemoteControlAsync(IAgentSession session, AgentRemoteControlOptions? options)
    {
        try
        {
            await session.SetRemoteControlAsync(options);
        }
        catch (Exception ex)
        {
            Log.Warn($"套用 Remote Control 設定失敗：{ex.Message}");
        }
    }

    private void CancelQueued(ConversationState? c, string? id)
    {
        if (c == null)
            return;
        var queued = c.Queue.FirstOrDefault(q => q.Id == id);
        if (queued == null)
            return;
        // 正在插進這一輪：取消插入前的等待（已經寫給代理的取消不了，SteerQueuedAsync 照樣把它加成使用者訊息）
        if (ReferenceEquals(queued, c.SteeringMessage))
            c.SteerCancel?.Cancel();
        c.Queue.Remove(queued);
        // 排隊訊息帶的附件還回輸入區
        c.PendingAttachments.AddRange(queued.Attachments);
        // 取消的是執行中排隊的提問回答：代理沒收到答案，卡片回到可作答
        if (queued.AnswerFor is { Status: AgentItemStatus.Done } question && c.Items.Contains(question))
        {
            question.Status = AgentItemStatus.Waiting;
            question.Decision = null;
            question.Answers = new Dictionary<string, List<string>>();
            MarkItemDirty(c, question);
            SaveConversation(c, saveItems: true);
        }
        PostComposer(c);
    }

    private async Task StopAsync(ConversationState? c)
    {
        if (c?.Session == null || !c.Running)
            return;
        c.StopRequested = true;
        // 按了停止：這一輪結束前 app 就結束（或當掉）也不在下次啟動接續
        DeleteRunMarker(c);
        // 還在等著插進這一輪的訊息不插了（留在佇列，跟著佇列暫停）
        c.SteerCancel?.Cancel();
        // 這一輪交辦出去、還沒完成的子工作一起停（AgentHub.Delegation.cs）
        CancelDelegatedChildren(c);
        SetStatus(c, "停止中…");
        foreach (var item in c.Items.Where(i => i.Status == AgentItemStatus.Waiting && !i.AnswerAsMessage && i.Kind is AgentItemKind.Question or AgentItemKind.Approval or AgentItemKind.PlanApproval))
        {
            item.Status = AgentItemStatus.Cancelled;
            MarkItemDirty(c, item);
        }
        try
        {
            await c.Session.InterruptAsync();
        }
        catch (Exception ex)
        {
            Toast($"停止失敗：{ex.Message}", isError: true);
        }
        // 更新屏障停下的一輪（等待 CLI 更新）沒有程序、不會回報 TurnCompleted：在本機收尾
        StopTurnHeldForCliUpdate(c);
    }

    // ───────────────────────────── 問卷、核准、計畫 ─────────────────────────────

    /// <param name="remoteOrigin">遠端的 answer（WP4.4 計畫 D21）：當成訊息送出的回答是代理輸入，入列與送出都帶著這個來源（取出時照樣重查）</param>
    private async Task AnswerAsync(ConversationState? c, string? itemId, JObject? answersJson, RemoteOrigin? remoteOrigin = null, JObject? notesJson = null)
    {
        var item = c == null ? null : FindItem(c, itemId);
        if (c == null || item == null || item.Kind != AgentItemKind.Question || item.Status != AgentItemStatus.Waiting || RejectStaleCard(c, item))
            return;

        var answers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var property in answersJson?.Properties() ?? Enumerable.Empty<JProperty>())
        {
            var values = property.Value is JArray array
                ? array.Select(v => v.ToString()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList()
                : new List<string> { property.Value.ToString() };
            if (values.Count > 0)
                answers[property.Name] = values;
        }
        if (answers.Count == 0)
        {
            Toast("請先選擇或輸入答案");
            return;
        }
        // 每題的補充說明（2026-11-05）：Claude、Grok 以 annotations.notes 交給 CLI；畫面、存檔、Codex 與答案當下一則訊息的都併成多一筆「補充說明：…」
        var notes = QuestionNoteRules.Parse(item.Questions, notesJson);
        var shown = QuestionNoteRules.Merge(answers, notes);

        // 密碼類題目（Codex isSecret）的答案只送給代理、不留在畫面與存檔
        item.Answers = shown.ToDictionary(
            a => a.Key,
            a => item.Questions.Any(q => q.Id == a.Key && q.IsSecret) ? new List<string> { "（已隱藏）" } : a.Value.ToList());
        item.Status = AgentItemStatus.Done;
        item.Decision = "answered";
        MarkItemDirty(c, item);

        if (item.AnswerAsMessage)
        {
            // CLI 收原文；畫面、存檔、佇列快照與同步只看秘密答案換成「（已隱藏）」的文字（設計 14.11 D20、契約 R3）
            var (message, display) = AnswerMessageRules.Compose(item.Questions, shown);
            if (c.Meta.Provider == AgentProvider.Antigravity
                && AntigravityStreamRules.TryResolvePlanFeedbackDecision(item.Questions, answers, out var targetMode, out var planCliMessage)
                && !string.IsNullOrWhiteSpace(planCliMessage))
            {
                message = planCliMessage;
                if (targetMode is { } nextMode && c.Meta.PermissionMode != nextMode)
                {
                    c.Meta.PermissionMode = AgentPermissionModes.Normalize(c.Meta.Provider, nextMode);
                    MarkHeaderDirty(c);
                }
            }
            if (c.Running)
            {
                // 卡片寫著「回答會當作下一則訊息送出」：執行中作答就插進這一輪，插不進去（或遠端作答的）就排進佇列、這一輪正常完成後送出；
                // 為了等這個回答正在停下的這一輪（Codex）不插（CanSteer），停下之後當下一則訊息送出
                c.Queue.Add(new QueuedMessage { Text = message, DisplayText = display, AnswerFor = item, RemoteOrigin = remoteOrigin });
                SaveConversation(c, saveItems: true);
                await SteerQueuedAsync(c);
                return;
            }
            await SendCoreAsync(c, message, new List<AgentAttachment>(), answerFor: item, displayText: display, remoteOrigin: remoteOrigin);
            return;
        }

        if (c.Session == null || item.RequestKey == null)
            return;
        SetStatus(c, "回覆中…");
        try
        {
            await c.Session.AnswerQuestionAsync(item.RequestKey, answers, notes.Count > 0 ? notes : null);
        }
        catch (Exception ex)
        {
            Toast($"送出答案失敗：{ex.Message}", isError: true);
        }
        SaveConversation(c, saveItems: true);
        RecheckRestart(c);
    }

    private async Task SkipQuestionAsync(ConversationState? c, string? itemId)
    {
        var item = c == null ? null : FindItem(c, itemId);
        // 只有問卷卡能略過：skipQuestion 不帶摘要（閘門 5h 不比對卡片），核准卡與計畫卡要走各自帶摘要的指令（D36）
        if (c == null || item == null || item.Kind != AgentItemKind.Question || item.Status != AgentItemStatus.Waiting || RejectStaleCard(c, item))
            return;
        item.Status = AgentItemStatus.Declined;
        item.Decision = "skip";
        MarkItemDirty(c, item);
        // 為了等這個回答而停下的這一輪（Codex 的非同步提問）：略過就請代理依自己的判斷接著做；停下途中略過的排進佇列，停下後接著送
        if (item.AnswerAsMessage && ReferenceEquals(c.PausedQuestion, item))
        {
            c.PausedQuestion = null;
            if (c.Running)
            {
                c.Queue.Add(new QueuedMessage { Text = AgentHubRules.SkippedAsyncQuestionMessage });
                SaveConversation(c, saveItems: true);
                PostComposer(c);
            }
            else
            {
                SaveConversation(c, saveItems: true);
                await SendCoreAsync(c, AgentHubRules.SkippedAsyncQuestionMessage, new List<AgentAttachment>());
            }
            return;
        }
        if (!item.AnswerAsMessage && c.Session != null && item.RequestKey != null)
        {
            try
            {
                await c.Session.DeclineQuestionAsync(item.RequestKey);
            }
            catch (Exception ex)
            {
                Toast($"略過失敗：{ex.Message}", isError: true);
            }
        }
        SaveConversation(c, saveItems: true);
        RecheckRestart(c);
    }

    /// <param name="remoteOrigin">遠端的 approve（WP4.4 計畫 D22）：拒絕的附言之後當成代理輸入送出，帶著這個來源（取出時照樣重查）</param>
    /// <param name="note">不是使用者按的（完全自動放行）：卡片上的說明</param>
    private async Task ApproveAsync(ConversationState? c, string? itemId, string? choiceText, string? message, RemoteOrigin? remoteOrigin = null, string? note = null)
    {
        var item = c == null ? null : FindItem(c, itemId);
        if (c == null || item == null || item.Kind != AgentItemKind.Approval || item.Status != AgentItemStatus.Waiting || c.Session == null || item.RequestKey == null
            || RejectStaleCard(c, item))
            return;

        var choice = choiceText switch
        {
            "allowSession" => AgentApprovalChoice.AllowForSession,
            "deny" => AgentApprovalChoice.Deny,
            _ => AgentApprovalChoice.Allow,
        };
        item.Decision = choice switch
        {
            AgentApprovalChoice.AllowForSession => "allowSession",
            AgentApprovalChoice.Deny => "deny",
            _ => "allow",
        };
        var feedback = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (choice == AgentApprovalChoice.Deny && feedback != null)
            item.Output = $"你的說明：{feedback}";
        else if (note != null)
            item.Output = note;
        item.Status = choice == AgentApprovalChoice.Deny ? AgentItemStatus.Declined : AgentItemStatus.Done;
        // 使用者回應了：允許的工具接著執行（之後重開就算被中斷），拒絕的由 CLI 回報結束
        c.UnapprovedTools.Remove(item.RequestKey);
        MarkItemDirty(c, item);
        SetStatus(c, choice == AgentApprovalChoice.Deny ? "已拒絕，等待代理回應…" : "執行中…");

        if (choice == AgentApprovalChoice.Deny)
            c.DeniedThisTurn = true;
        // Codex、Grok 的拒絕沒有欄位能帶說明：這一輪結束後當下一則訊息送出（檔案修改的拒絕不會結束這一輪，可能拒絕好幾次，說明依序保留）
        if (choice == AgentApprovalChoice.Deny && feedback != null && AgentHubRules.SendsDenyFeedbackAsMessage(c.Meta.Provider))
        {
            c.PendingFeedback.Add(new DeniedOperation(item.ToolName, item.Title, feedback, remoteOrigin));
            PersistQueueIfChanged(c);
        }
        try
        {
            await c.Session.RespondApprovalAsync(item.RequestKey, choice, feedback);
        }
        catch (Exception ex)
        {
            Toast($"回應失敗：{ex.Message}", isError: true);
        }
        SaveConversation(c, saveItems: true);
        RecheckRestart(c);
    }

    private async Task ApprovePlanAsync(ConversationState? c, string? itemId, string? choiceText, string? message)
    {
        var item = c == null ? null : FindItem(c, itemId);
        if (c == null || item == null || item.Kind != AgentItemKind.PlanApproval || item.Status != AgentItemStatus.Waiting || c.Session == null || item.RequestKey == null
            || RejectStaleCard(c, item))
            return;
        var choice = choiceText switch
        {
            "acceptEdits" => AgentPlanChoice.AcceptEdits,
            "keepPlanning" => AgentPlanChoice.KeepPlanning,
            _ => AgentPlanChoice.Manual,
        };
        item.Decision = choice switch
        {
            AgentPlanChoice.AcceptEdits => "acceptEdits",
            AgentPlanChoice.KeepPlanning => "keepPlanning",
            _ => "manual",
        };
        if (choice == AgentPlanChoice.KeepPlanning && !string.IsNullOrWhiteSpace(message))
            item.Output = $"你的說明：{message.Trim()}";
        item.Status = choice == AgentPlanChoice.KeepPlanning ? AgentItemStatus.Declined : AgentItemStatus.Done;
        c.UnapprovedTools.Remove(item.RequestKey);
        MarkItemDirty(c, item);
        SetStatus(c, choice == AgentPlanChoice.KeepPlanning ? "繼續規劃中…" : "照計畫執行中…");
        try
        {
            await c.Session.RespondPlanAsync(item.RequestKey, choice, message);
        }
        catch (Exception ex)
        {
            Toast($"回應失敗：{ex.Message}", isError: true);
        }
        SaveConversation(c, saveItems: true);
        RecheckRestart(c);
    }

    private static AgentChatItem? FindItem(ConversationState c, string? itemId)
        => itemId == null ? null : c.Items.FirstOrDefault(i => i.Id == itemId);

    // ───────────────────────────── 對話設定 ─────────────────────────────

    private void Rename(ConversationState? c, string? title)
    {
        if (c == null)
            return;
        var trimmed = (title ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return;
        c.Meta.Title = trimmed.Length <= 80 ? trimmed : trimmed[..80];
        if (!c.IsDraft)
            SaveConversation(c, saveItems: false);
        MarkHeaderDirty(c);
        MarkListDirty();
        RenameAgentSession(c);
    }

    /// <summary>
    /// 代理端的工作階段名稱跟著對話標題（Claude：claude.ai／Claude App 上的 Remote Control 工作階段）。沒有會話時不必送：
    /// 下一個程序啟動時以 AgentSessionSettings.Title 命名
    /// </summary>
    private void RenameAgentSession(ConversationState c)
    {
        if (c.IsDraft || c.Session is not { } session)
            return;
        var title = c.Meta.Title;
        SessionTitleRules.RememberSent(c.SentSessionTitles, title);
        _ = RenameAgentSessionAsync(session, title);
    }

    private static async Task RenameAgentSessionAsync(IAgentSession session, string title)
    {
        try
        {
            await session.RenameAsync(title);
        }
        catch (Exception ex)
        {
            Log.Warn($"改代理工作階段的名稱失敗：{ex.Message}");
        }
    }

    /// <summary>代理回報了工作階段名稱（在 claude.ai／Claude App 改名，或 app 自己改名的回送）：不同於對話標題就改標題（不再送回代理）</summary>
    private void OnAgentSessionTitle(ConversationState c, string reported)
    {
        if (c.IsDraft || SessionTitleRules.FromAgent(c.SentSessionTitles, c.Meta.Title, reported) is not { } title)
            return;
        c.Meta.Title = title;
        SaveConversation(c, saveItems: false);
        MarkHeaderDirty(c);
        MarkListDirty();
    }

    /// <param name="toTrash">true＝遠端刪除（WP4.4 計畫 D24）：資料夾移到回收區（<paramref name="info"/> 是它的資訊）而不是直接刪除</param>
    private void Delete(ConversationState? c, bool toTrash = false, TrashInfo? info = null)
    {
        if (c == null)
            return;
        if (toTrash)
            ArgumentNullException.ThrowIfNull(info);
        var id = c.Meta.Id;
        JournalProcessClose(c, "delete");
        c.Session?.Dispose();
        c.Session = null;
        var wasRunning = c.Running;
        // 執行中被刪的：回收區裡的資料夾不能帶著 running.json（還原後下次啟動會被當成上次中斷而自動接續）
        DeleteRunMarker(c);
        HookConversationDeleting(c);
        conversations.Remove(id);
        if (toTrash)
            MoveToTrashInBackground(id, info!);
        else
            DeleteFolderInBackground(id, quiet: false);
        PostToAll(new JObject { ["type"] = "conversationClosed", ["conversationId"] = id });
        host.ConversationDeleted(id);
        if (wasRunning)
            UpdateRunningCounts();
        MarkListDirty();
        MarkAccountsDirty();
        // 被刪的對話已不在清單：別的對話的換版狀態不動，只重新檢查屏障與這一波
        RefreshRestarts(released: []);
    }

    /// <param name="persistAccountDefaults">false＝遠端（WP4.4 計畫 D23）：只改這個對話，不把模型記成帳戶預設、不存這台的設定</param>
    private void SetOption(IAgentView view, ConversationState? c, string? key, string? value, bool persistAccountDefaults = true)
    {
        if (c == null || key == null)
            return;
        var normalized = string.IsNullOrWhiteSpace(value) || value == "default" ? null : value.Trim();
        var providerDefaults = ProviderDefaults(c.Meta.Provider);
        var previousMode = c.Meta.PermissionMode;
        var previousModel = c.Meta.Model;
        var previousEffort = c.Meta.Effort;
        // 只改這個對話（新對話一律用設定頁的預設）；模型另外記成這個帳戶上次選的（設定頁的預設模型是「沿用上次選擇」時新對話用它）
        switch (key)
        {
            case "model":
                c.Meta.Model = normalized;
                if (persistAccountDefaults)
                    settings.DefaultsFor(c.Meta.AccountId).Model = normalized;
                // 推理強度換成新模型的預設；沒有設定就沿用，新模型不支援就清掉——畫面顯示的才是實際傳給 CLI 的設定
                c.Meta.Effort = ConversationDefaultsRules.EffortAfterModelChange(providerDefaults, c.Meta.Effort, normalized, ModelOptions(c));
                break;
            case "effort":
                c.Meta.Effort = normalized;
                break;
            case "permission":
                if (normalized == null)
                {
                    // 「default」＝回到設定頁的預設
                    c.Meta.PermissionMode = ConversationDefaultsRules.PermissionFor(c.Meta.Provider, providerDefaults);
                    break;
                }
                if (!Enum.TryParse<AgentPermissionMode>(value, out var mode))
                    return;
                c.Meta.PermissionMode = AgentPermissionModes.Normalize(c.Meta.Provider, mode);
                break;
            default:
                return;
        }

        if (!c.IsDraft)
            SaveConversation(c, saveItems: false);
        if (persistAccountDefaults)
            settings.Save();
        MarkHeaderDirty(c);
        // 執行中換權限模式：標頭立即顯示新模式，所以要套用到這一輪（做不到就明說下一則訊息才生效）
        if (key == "permission" && c.Running && c.Session != null && c.Meta.PermissionMode != previousMode)
            _ = ApplyPermissionModeNowAsync(view, c, c.Session, c.Meta.PermissionMode);
        // 切換 CLI 模式不會回覆已送出的核准：完全自動由中樞統一放行，包含背景工作正在等待的卡片。
        if (key == "permission" && c.Meta.PermissionMode == AgentPermissionMode.FullAccess)
            foreach (var item in c.RequestItems.Values.ToList())
                TryAutoApprove(c, item);
        // 換模型、推理強度：活著的程序立即改用（Claude、Grok 一輪中途也從下一次模型呼叫起；Claude 閒置的程序也套用，下一輪不必重開），
        // 執行中做不到（Codex、Antigravity）就明說下一則訊息才生效
        if (key is "model" or "effort" && c.Session != null && (c.Meta.Model != previousModel || c.Meta.Effort != previousEffort))
            _ = ApplyModelNowAsync(view, c, c.Session, key, c.Meta.Model, c.Meta.Effort);
    }

    private async Task ApplyModelNowAsync(IAgentView view, ConversationState c, IAgentSession session, string key, string? model, string? effort)
    {
        bool applied;
        try
        {
            applied = await session.SetModelAsync(model, effort);
        }
        catch (Exception ex)
        {
            Log.Warn($"執行中切換模型失敗：{ex.Message}");
            applied = false;
        }
        // 等待期間這一輪已結束（下一則訊息本來就用新設定）、又改了模型或強度、換了會話：不必提示
        if (applied || disposed || !c.Running || !ReferenceEquals(c.Session, session) || c.Meta.Model != model || c.Meta.Effort != effort)
            return;
        Toast(key == "model"
            ? "這一輪仍用原本的模型執行，新的模型從下一則訊息開始生效"
            : "這一輪仍照原本的推理強度執行，新的強度從下一則訊息開始生效", view: view);
    }

    private async Task ApplyPermissionModeNowAsync(IAgentView view, ConversationState c, IAgentSession session, AgentPermissionMode mode)
    {
        bool applied;
        try
        {
            applied = await session.SetPermissionModeAsync(mode);
        }
        catch (Exception ex)
        {
            Log.Warn($"執行中切換權限模式失敗：{ex.Message}");
            applied = false;
        }
        // 等待期間這一輪已結束（下一則訊息本來就用新模式）、又換了模式或換了會話：不必提示
        if (applied || disposed || !c.Running || !ReferenceEquals(c.Session, session) || c.Meta.PermissionMode != mode)
            return;
        Toast("這一輪仍照原本的權限模式執行，新的模式從下一則訊息開始生效", view: view);
    }

    /// <summary>完全自動只回覆工具核准；問卷、計畫確認與已處理的卡片保留原狀。回應前仍經過程序實例檢查。</summary>
    private bool TryAutoApprove(ConversationState c, AgentChatItem item)
    {
        if (c.Meta.PermissionMode != AgentPermissionMode.FullAccess || c.Session == null
            || item.Kind != AgentItemKind.Approval || item.Status != AgentItemStatus.Waiting || item.RequestKey == null)
            return false;
        _ = ApproveAsync(c, item.Id, "allow", null, note: "已切換為完全自動，自動允許");
        return true;
    }

    /// <summary>
    /// 換帳戶：草稿直接換；已開始的對話關掉原本的程序、改由新帳戶開新 session，下一則訊息附上前文節錄（額度用完時換帳戶繼續）。
    /// 執行中也能換到任何一種代理的帳戶（2026-10-24 起四種互換，換到另一家以前文節錄開新對話；AccountSwitchRules.WhileRunningBlock）：
    /// 先停下這一輪，結束時換過去、從中斷處接著做（SwitchAccountWhileRunning）；或等目前步驟完成後停下交接（<paramref name="atTurnEnd"/>）
    /// </summary>
    /// <param name="rememberAccount">false＝遠端（WP4.4 計畫 D23）：不改「上次使用的帳戶」</param>
    /// <param name="atTurnEnd">setAccount 的 when 是 turnEnd：等目前步驟完成後停下交接（閒置時仍等背景工作結束）</param>
    /// <param name="explicitNow">
    /// setAccount 的 when 明確是 now：閒置、還有背景工作在跑時照換（背景工作跟著停，告訴新帳戶）；沒帶 when 的這時等背景工作結束才換
    /// （AgentHub.AccountSwitch.cs 的 DeferAccountSwitch）
    /// </param>
    private void SetAccount(IAgentView view, ConversationState? c, string? accountId, bool rememberAccount = true, bool atTurnEnd = false, bool explicitNow = false)
    {
        var account = accounts.Find(accountId);
        if (c == null || account == null)
            return;
        if (c.Meta.AccountId == account.Id)
        {
            // 執行中選了別的帳戶、又選回目前的：不換了（立即換的這一輪照樣停下；這一輪結束時才換的照常做完）
            if (c.Running && c.PendingAccountSwitch is { } pending)
            {
                c.PendingAccountSwitch = null;
                MarkHeaderDirty(c);
                if (pending.AtTurnEnd)
                    Toast("已取消：目前步驟完成後不換帳戶", view: view);
                else
                    SetStatus(c, "停止中…");
            }
            else if (c.Meta.DeferredAccount != null)
            {
                // 在等背景工作結束才換的：選回目前的帳戶＝不換了
                DropDeferredAccountSwitch(c, "cancelled");
                SaveConversation(c, saveItems: false);
                Toast("已取消：背景工作結束後不換帳戶", view: view);
            }
            return;
        }
        if (c.Running)
        {
            // 執行中：先停下這一輪，結束時換過去、從中斷處接著做（或等這一輪結束才換）；還不能在執行中換的種類（目前沒有）或未登入／未安裝的帳戶送 toast
            if (AccountSwitchRules.WhileRunningBlock(c.Meta.Provider, account.Provider) is { } block)
            {
                Toast(block, isError: true, view);
                return;
            }
            var targetStatus = accounts.GetState(account.Id).Status;
            if (targetStatus is AccountStatus.NotLoggedIn or AccountStatus.NotInstalled)
            {
                Toast($"「{account.Label}」{AccountRules.DescribeStatus(account.Provider, targetStatus)}", isError: true, view);
                return;
            }
            SwitchAccountWhileRunning(view, c, account, rememberAccount, atTurnEnd);
            return;
        }

        // 這一輪結束了、背景工作還在跑：換帳戶會關掉程序、背景工作跟著停。確認了現在就換的（網頁先問過）照換並告訴新帳戶，
        // 其他的（選了等背景工作結束、瀏覽器版與其他電腦看不到背景工作而沒帶 when）等背景工作結束才換
        if (!c.IsDraft && c.Session?.HasLiveBackgroundWork == true)
        {
            if (explicitNow)
                SwitchAccountStoppingBackground(c, account, rememberAccount);
            else
                DeferAccountSwitch(c, account, rememberAccount);
            return;
        }

        ApplyChosenAccount(c, account, rememberAccount);
    }

    /// <summary>沒在執行時換帳戶（手動、遠端，與執行中選了帳戶、這一輪剛好自己做完時）：換過去、加提示，下一則訊息附前文節錄</summary>
    private void ApplyChosenAccount(ConversationState c, AgentAccount account, bool rememberAccount)
    {
        var previous = accounts.Find(c.Meta.AccountId);
        // 使用者自己選了帳戶：不再自動換回額度用完之前的原帳戶
        c.Meta.QuotaHome = null;
        ApplyAccountChange(c, account);
        if (rememberAccount)
            settings.LastAccountId = account.Id;

        if (!c.IsDraft)
        {
            AddItem(c, new AgentChatItem
            {
                Kind = AgentItemKind.Notice,
                Text = $"已從「{previous?.Label ?? "原帳戶"}」改用「{account.Label}」繼續；下一則訊息會附上先前對話的節錄，讓代理了解前情。",
            });
            SaveConversation(c, saveItems: true);
            MarkListDirty();
        }

        PublishAccountChange(c);
    }

    /// <summary>
    /// 換帳戶的核心（手動換帳戶與額度用完時自動換帳戶共用；呼叫端確認不在執行中）：關掉原本的程序、改由新帳戶開新 session
    /// （SessionStarted＝false，下一則送出時附上前文節錄）；換到另一家時模型、推理強度與權限模式照新對話的預設。不存檔、不加提示
    /// </summary>
    private void ApplyAccountChange(ConversationState c, AgentAccount account)
    {
        bool sameProvider = c.Meta.Provider == account.Provider;
        JournalProcessClose(c, "accountSwitch", new JObject { ["to"] = account.Id });
        // 還在跑的背景工作跟著舊的程序停掉：當下記進背景活動日誌（新的會話不會回報舊程序的背景工作，以前要等新會話第一次回報才補記 sessionReplaced）
        if (BackgroundOf(c) is { } live)
        {
            Journal("bg.end", c, new JObject
            {
                ["how"] = "accountSwitch",
                ["to"] = account.Id,
                ["count"] = live.Count,
                ["tools"] = BackgroundJournalRules.Tools(live.Tools),
                ["sec"] = BackgroundJournalRules.Seconds(c.BackgroundSince is { } since ? DateTime.Now - since : null),
            });
        }
        c.BackgroundWork = null;
        c.BackgroundWorkSession = null;
        c.BackgroundStarts = new List<DateTime>();
        c.BackgroundSince = null;
        // 在等背景工作結束才換的帳戶：換了帳戶就不必再等（換到的就是它時呼叫端已經清掉）
        DropDeferredAccountSwitch(c, "switched");
        c.Session?.Dispose();
        c.Session = null;
        c.Meta.AccountId = account.Id;
        c.Meta.Provider = account.Provider;
        c.Meta.SessionId = InitialSessionId(account.Provider);
        c.Meta.SessionStarted = false;
        c.Meta.SessionAccountId = null;
        // 新帳戶開新的 CLI 對話：上下文從前文節錄重新算
        c.ContextUsage = null;
        // 遠端工作階段屬於原帳戶：新帳戶開新的
        c.Meta.RemoteControlSessionId = null;
        c.CodexGoal = null;
        if (!sameProvider)
        {
            // 換到另一家：同新對話（模型、推理強度與權限模式用設定頁的預設，Antigravity 專屬的自訂代理也清掉）
            var providerDefaults = ProviderDefaults(account.Provider);
            var models = ModelOptions(account.Id, account.Provider);
            c.Meta.Model = ConversationDefaultsRules.ModelFor(providerDefaults, settings.DefaultsFor(account.Id).Model, models);
            c.Meta.Effort = ConversationDefaultsRules.EffortFor(providerDefaults, c.Meta.Model, models);
            c.Meta.PermissionMode = ConversationDefaultsRules.PermissionFor(account.Provider, providerDefaults);
            c.Meta.Agent = null;
            c.Meta.OutputStyle = null;
            c.Meta.CodexServiceTier = null;
            c.Meta.CodexSkills = null;
        }
        else if (!string.IsNullOrEmpty(c.Meta.Model) && c.Meta.Model != "default")
        {
            // 同種代理換帳戶：若新帳戶已知模型清單不含原本選的訂閱專屬模型（例如 Pro 與 Ultra 差異），退回新帳戶可用的預設模型與推理強度
            var models = ModelOptions(account.Id, account.Provider);
            if (models.Any(m => m.Value != "default") && !models.Any(m => m.Value == c.Meta.Model))
            {
                var providerDefaults = ProviderDefaults(account.Provider);
                c.Meta.Model = ConversationDefaultsRules.ModelFor(providerDefaults, settings.DefaultsFor(account.Id).Model, models);
                c.Meta.Effort = ConversationDefaultsRules.EffortFor(providerDefaults, c.Meta.Model, models);
            }
        }
        c.Meta.PermissionMode = AgentPermissionModes.Normalize(account.Provider, c.Meta.PermissionMode);
        RequestAntigravityWorkspaceScan(c);
    }

    /// <summary>換帳戶之後通知各處：熱更新的等待清單放掉舊程序、開著這個對話的畫面整份重送（標頭的帳戶與模型清單）、帳戶卡的執行數</summary>
    private void PublishAccountChange(ConversationState c)
    {
        RefreshRestarts(released: [c]);
        foreach (var v in ViewsOf(c).ToList())
            PostConversation(v, c, null);
        MarkAccountsDirty();
    }

    private void ChooseFolder(IAgentView view, ConversationState? c)
    {
        if (c == null)
            return;
        if (c.Running)
        {
            Toast("代理執行中不能更換工作資料夾", view: view);
            return;
        }
        var folder = host.PickFolder(view, c.Meta.WorkingDirectory ?? validRecentFolders.FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!string.IsNullOrWhiteSpace(folder))
            _ = SetFolderAsync(c, folder);
    }

    /// <summary>
    /// 換工作資料夾：立即生效（緊接著送出的訊息就用新資料夾），存在與否在背景確認——不存在就還原並提示
    /// （送出時也會再確認一次，所以不存在的資料夾不會被代理用到）
    /// </summary>
    private async Task SetFolderAsync(ConversationState? c, string? path)
    {
        if (c == null || string.IsNullOrWhiteSpace(path))
            return;
        if (c.Running)
        {
            Toast("代理執行中不能更換工作資料夾");
            return;
        }
        string full;
        try
        {
            full = AgentAttachmentRules.IsLocalDrivePath(path) ? Path.GetFullPath(path) : throw new ArgumentException("不是本機磁碟路徑");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Toast($"不能用這個資料夾：{path}", isError: true);
            return;
        }

        var previous = c.Meta.WorkingDirectory;
        ApplyFolder(c, full);
        if (await DirectoryExistsAsync(full) == true)
        {
            RememberFolder(full);
            settings.Save();
            return;
        }
        Toast($"找不到資料夾：{full}", isError: true);
        if (conversations.ContainsKey(c.Meta.Id) && string.Equals(c.Meta.WorkingDirectory, full, StringComparison.OrdinalIgnoreCase) && !c.Running)
            ApplyFolder(c, previous);
    }

    private void ApplyFolder(ConversationState c, string? folder)
    {
        c.Meta.WorkingDirectory = folder;
        if (!c.IsDraft)
            SaveConversation(c, saveItems: false);
        MarkHeaderDirty(c);
        MarkListDirty();
        OnGitFolderChanged(c);
        OnLaunchFolderChanged(c);
        RequestAntigravityWorkspaceScan(c, force: true);
    }

    private void ResetFolder(ConversationState? c)
    {
        if (c == null || c.Running)
            return;
        // 與 setFolder 走同一條：存檔、更新標頭與對話清單（側欄提示的工作資料夾）
        ApplyFolder(c, null);
    }

    private void OpenFolder(ConversationState? c, string? which)
    {
        if (c == null)
            return;
        var folder = which == "attachments" ? store.GetAttachmentDirectory(c.Meta.Id) : GetWorkingDirectory(c);
        _ = OpenFolderAsync(folder, create: which == "attachments" || string.IsNullOrWhiteSpace(c.Meta.WorkingDirectory));
    }

    // ───────────────────────────── 帳戶、CLI、設定 ─────────────────────────────

    private void AccountAdd(string? providerText, string? label, string? color)
    {
        if (ParseProvider(providerText) is not { } provider)
            return;
        // 「已新增，請完成登入」由送出的網頁自己提示（宿主再發一次會重複，還會跳到其他視窗）；登入進度與結果經 accountLogin 送出
        try
        {
            accounts.Add(provider, label, color);
        }
        catch (InvalidOperationException ex)
        {
            // 只能有一個帳戶的 CLI（Antigravity）已經有了：網頁會停用這個選項，兩個視窗同時新增時才會走到這裡
            Toast(ex.Message, isError: true);
        }
    }

    private void AccountRemove(string? accountId, bool deleteData)
    {
        var account = accounts.Find(accountId);
        if (account == null)
            return;
        // 停止這個帳戶執行中的對話；對話保留在歷史（標示帳戶已移除，可換帳戶繼續）
        var released = conversations.Values.Where(c => c.Meta.AccountId == account.Id).ToList();
        foreach (var c in released)
        {
            JournalProcessClose(c, "accountRemoved");
            c.Session?.Dispose();
            c.Session = null;
            if (c.Running)
                AbortTurnLocally(c, "帳戶已移除，這一輪已停止");
            if (c.IsDraft)
            {
                foreach (var v in ViewsOf(c).ToList())
                    Post(v, new JObject { ["type"] = "conversationClosed", ["conversationId"] = c.Meta.Id });
                DiscardDraft(c);
                continue;
            }
            SaveConversation(c, saveItems: c.ItemsLoaded);
            MarkHeaderDirty(c);
        }
        var clean = accounts.Remove(account.Id, deleteData);
        usageSegments.RemoveAccount(account.Id);
        settings.AccountDefaults.Remove(account.Id);
        settings.Models.Remove(account.Id);
        settings.Commands.Remove(account.Id);
        settings.Save();
        UpdateRunningCounts();
        MarkListDirty();
        RefreshRestarts(released);
        Toast(clean ? $"已移除「{account.Label}」" : $"已移除「{account.Label}」，但部分檔案正在使用中，稍後可手動刪除", isError: !clean);
    }

    /// <summary>登入進度與結果：網頁收到 done 的 accountLogin 會自己顯示提示，宿主不另外發 toast（否則同一則會跳兩次）</summary>
    private void OnLoginProgress(string accountId, string message, bool done, bool? success)
    {
        var json = new JObject
        {
            ["type"] = "accountLogin",
            ["accountId"] = accountId,
            ["message"] = message,
            ["done"] = done,
            ["success"] = success,
        };
        AddDeviceCode(json, accountId, done);   // 〔帳戶目錄〕Codex 裝置碼登入進行中時帶驗證碼（AgentHub.AccountDirectory.cs）
        PostToAll(json);
    }

    private void OnCapabilitiesDiscovered(string accountId, AccountCapabilities capabilities)
    {
        UpdateModels(accountId, capabilities.Models);
        if (capabilities.Commands.Count > 0 && !ConversationDefaultsRules.SameCommands(settings.Commands.GetValueOrDefault(accountId), capabilities.Commands))
            settings.Commands[accountId] = capabilities.Commands.ToList();
        settings.Save();
        foreach (var c in conversations.Values.Where(c => c.Meta.AccountId == accountId && c.ViewIds.Count > 0))
            MarkHeaderDirty(c);
    }

    /// <summary>帳戶卡的「使用重置」（Codex 的免費重置，網頁已確認）：結果只告訴按的那個視窗</summary>
    private async Task UseResetCreditAsync(IAgentView view, string accountId)
    {
        CodexResetCreditResult result;
        try
        {
            result = await accounts.UseCodexResetCreditAsync(accountId);
        }
        catch (Exception ex)
        {
            result = new CodexResetCreditResult(false, $"使用免費重置失敗：{ex.Message}", "error");
        }
        if (!disposed)
            Toast(result.Text, view: view, level: result.Level);
    }

    private async Task RunCliActionAsync(AgentProvider? provider, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Toast($"CLI 操作失敗：{ex.Message}", isError: true);
        }
        if (disposed)
            return;
        // 安裝／更新失敗的原因（查版本不會清掉它）
        if (provider is { } p && cli.Get(p).ActionError is { } error)
            Toast(error, isError: true);
        else if (provider is { } done && cli.Get(done).Installed)
            Toast($"{AgentProviders.ProductName(done)} CLI 目前版本 {cli.Get(done).Version}", level: "success");
        cliDirty = true;
        ScheduleFlush();
        _ = accounts.RefreshAllAsync(includeStatus: true);
    }

    private void ChangeSetting(string? key, JToken? value)
    {
        if (key == null || value == null)
            return;
        switch (key)
        {
            case "theme":
                var theme = value.ToString();
                if (theme is "dark" or "light" or "system")
                {
                    settings.Theme = theme;
                    host.ApplyTheme(theme);
                }
                break;
            case "fontSize":
                if (int.TryParse(value.ToString(), out var size))
                    settings.FontSize = Math.Clamp(size, AppSettings.MinFontSize, AppSettings.MaxFontSize);
                break;
            case "closeToTray":
                settings.CloseToTray = value.Type == JTokenType.Boolean && value.Value<bool>();
                break;
            case "visualEffects":
                // 只收布林：其他值當成關掉（同其他開關）
                settings.VisualEffects = value.Type == JTokenType.Boolean && value.Value<bool>();
                break;
            case "autoStart":
                // 狀態在登錄（AgentHub.AutoStart.cs），不寫 settings.json；不論成敗都重送設定，開關才會回到實際狀態
                ChangeAutoStart(value);
                PostSettings();
                return;
            case "notifications":
                settings.Notifications = value.Type == JTokenType.Boolean && value.Value<bool>();
                break;
            case "voiceNotifications":
            {
                bool enabled = value.Type == JTokenType.Boolean && value.Value<bool>();
                bool changed = settings.VoiceNotifications != enabled;
                settings.VoiceNotifications = enabled;
                // 打開時念一句試聽（使用者馬上知道聲音與音量）；關掉時停下正在念的
                if (enabled && changed)
                    host.Speak(VoiceNotifyRules.EnabledSample, VoiceTuningSetting, null);
                else if (!enabled)
                    host.StopSpeaking();
                break;
            }
            case "voiceSpeed":
            case "voicePitch":
            {
                // 語音通知的語速、音調（設定頁的滑桿放開時送）：只收數字，限制在範圍內並取到間距；不是數字不改也不廣播
                if (value.Type is not (JTokenType.Integer or JTokenType.Float))
                    return;
                var before = VoiceTuningSetting;
                if (key == "voiceSpeed")
                    settings.VoiceSpeed = SpeechAudioRules.NormalizeSpeed(value.Value<double>());
                else
                    settings.VoicePitch = SpeechAudioRules.NormalizePitch(value.Value<double>());
                // 開著語音通知時以新的語速與音調念一句試聽；先停下正在念的（連續調好幾次只聽到最後一次）
                if (settings.VoiceNotifications && VoiceTuningSetting != before)
                {
                    host.StopSpeaking();
                    host.Speak(VoiceNotifyRules.TuningSample, VoiceTuningSetting, null);
                }
                break;
            }
            case "autoUpdateCli":
                settings.AutoUpdateCli = value.Type == JTokenType.Boolean && value.Value<bool>();
                break;
            case "scheduleAutoDiagnose":
                settings.ScheduleAutoDiagnose = value.Type == JTokenType.Boolean && value.Value<bool>();
                PostSchedules(force: true);
                break;
            case "autoUpdateApp":
                settings.AutoUpdateApp = value.Type == JTokenType.Boolean && value.Value<bool>();
                AppUpdateCommands?.Check();
                break;
            case "enterToSend":
                settings.EnterToSend = value.Type == JTokenType.Boolean && value.Value<bool>();
                break;
            case "claudeRemoteControl":
            {
                bool enabled = value.Type == JTokenType.Boolean && value.Value<bool>();
                bool changed = settings.ClaudeRemoteControl != enabled;
                settings.ClaudeRemoteControl = enabled;
                if (changed)
                    ApplyRemoteControlSetting();
                break;
            }
            case "autoSwitchAccount":
                settings.AutoSwitchAccountOnLimit = value.Type == JTokenType.Boolean && value.Value<bool>();
                break;
            case "autoSwitchCrossProvider":
                settings.AutoSwitchCrossProviderOnLimit = value.Type == JTokenType.Boolean && value.Value<bool>();
                break;
            case "shareCrossCliCustomizations":
            {
                bool enabled = value.Type == JTokenType.Boolean && value.Value<bool>();
                bool changed = settings.ShareCrossCliCustomizations != enabled;
                settings.ShareCrossCliCustomizations = enabled;
                if (changed && accounts.Shared is { } crossShared)
                {
                    _ = Task.Run(() =>
                    {
                        if (enabled)
                            crossShared.SyncCrossCli();
                        else
                            crossShared.UnshareCrossCli();
                        host.BeginInvoke(() =>
                        {
                            if (!disposed)
                                RequestHealth(null, force: true);
                        });
                    });
                }
                break;
            }
            case "claudeIdleResumeWarning":
                settings.ClaudeIdleResumeWarning = value.Type == JTokenType.Boolean && value.Value<bool>();
                break;
            case "autoResumeAfterQuota":
                settings.AutoResumeAfterQuotaReset = value.Type == JTokenType.Boolean && value.Value<bool>();
                break;
            case "claudeFallbackModel":
            {
                if (!ClaudeCustomizationRules.TryNormalizeFallback(value.Type == JTokenType.String ? value.ToString() : null,
                        ProviderModels(AgentProvider.Claude).Select(m => m.Value), out var fallback))
                    return;
                settings.ClaudeFallbackModel = fallback;
                break;
            }
            case "claudeIdleTokenRefresh":
            {
                bool enabled = value.Type == JTokenType.Boolean && value.Value<bool>();
                bool turnedOn = enabled && !settings.ClaudeIdleTokenRefresh;
                settings.ClaudeIdleTokenRefresh = enabled;
                // 打開時資料過時的 Claude 帳戶馬上再查一次（權杖過期的會開始換發）
                if (turnedOn)
                    accounts.PollStaleClaudeSoon();
                break;
            }
            case "instructions":
                var text = value.ToString().Trim();
                settings.Instructions = text.Length == 0 || text == AgentHubRules.DefaultInstructions ? null : text;
                break;
            case "continueText":
                // 輸入區「繼續」鈕送的那一句（設定頁離開輸入框時送）：只收字串，空的或等於預設存成 null
                if (value.Type != JTokenType.String)
                    return;
                settings.ContinueText = AgentHubRules.NormalizeContinueText(value.Value<string>());
                break;
            case "cliRestartPolicy":
                // CLI 更新後：SafePoint／AfterTurn／NextSend（只接受這三個值；AgentHub.Restart.cs）
                if (!ChangeRestartPolicy(value.Type == JTokenType.String ? value.Value<string>() : null))
                    return;
                break;
            default:
                return;
        }
        settings.Save();
        PostSettings();
    }

    private void PostSettings() => PostToAll(new JObject { ["type"] = "settings", ["settings"] = RenderSettings() });

    /// <summary>
    /// 設定頁「模型設定」（setConversationDefault）：某一家的預設模型（空白＝沿用上次選擇）、預設權限模式，
    /// 或某個模型的預設推理強度（"default"＝清除，回到 CLI 預設）、自動壓縮上限（autoCompact，空白＝CLI 預設）與百分比（autoCompactPercent，Grok）；不合法的值不改也不廣播。
    /// 已開的對話不受影響（自動壓縮上限除外：下次啟動程序時套用）
    /// </summary>
    private void SetConversationDefault(string? providerText, string? key, string? model, string? value)
    {
        if (ParseProvider(providerText) is not { } provider)
            return;
        var defaults = ProviderDefaults(provider);
        var changed = key switch
        {
            "model" => ConversationDefaultsRules.TrySetModel(defaults, value, ProviderModels(provider)),
            "permission" => ConversationDefaultsRules.TrySetPermission(defaults, provider, value),
            "effort" => ConversationDefaultsRules.TrySetEffort(defaults, model, value, ProviderModels(provider)),
            "autoCompact" => TrySetAutoCompact(defaults, provider, value),
            "autoCompactPercent" => TrySetAutoCompactPercent(defaults, provider, value),
            _ => false,
        };
        if (!changed)
            return;
        settings.Save();
        PostSettings();
    }

    /// <summary>自動壓縮上限（AutoCompactRules）：不支援的代理、看不懂的值不改；值沒變也不廣播</summary>
    private static bool TrySetAutoCompact(ProviderConversationDefaults defaults, AgentProvider provider, string? value)
    {
        if (!AutoCompactRules.TryNormalize(provider, value, out var tokens) || defaults.AutoCompactTokens == tokens)
            return false;
        defaults.AutoCompactTokens = tokens;
        return true;
    }

    /// <summary>自動壓縮門檻的百分比（AutoCompactRules，Grok）：不支援的代理、看不懂的值不改；值沒變也不廣播</summary>
    private static bool TrySetAutoCompactPercent(ProviderConversationDefaults defaults, AgentProvider provider, string? value)
    {
        if (!AutoCompactRules.TryNormalizePercent(provider, value, out var percent) || defaults.AutoCompactPercent == percent)
            return false;
        defaults.AutoCompactPercent = percent;
        return true;
    }

    /// <summary>
    /// 這一家的「對話預設值」（中樞一律經這裡取，不直接讀 settings.ConversationDefaults）。
    /// 第一次取用時做升級遷移：舊版的推理強度與權限模式是「新對話沿用上次選擇」，把帳戶記住的權限模式搬到設定頁的預設，
    /// 之後舊欄位不再寫回。不能在建構式做——AppController 建好中樞之後才載入帳戶清單（Accounts.Initialize），那時還看不到帳戶
    /// </summary>
    private ProviderConversationDefaults ProviderDefaults(AgentProvider provider)
    {
        if (!conversationDefaultsReady)
        {
            conversationDefaultsReady = true;
            settings.ConversationDefaults ??= ConversationDefaultsRules.Migrate(
                accounts.Accounts.Select(a => (a.Id, a.Provider)).ToList(), settings.AccountDefaults, settings.LastAccountId);
            foreach (var defaults in settings.AccountDefaults.Values)
                defaults.PermissionMode = null;
        }
        var key = provider.ToString();
        if (!settings.ConversationDefaults!.TryGetValue(key, out var result))
        {
            // 後來才支援的一家（升級前遷移過的設定沒有它）：預設權限模式換成這一家支援的（Antigravity 沒有每步詢問）
            result = new ProviderConversationDefaults { PermissionMode = AgentPermissionModes.Normalize(provider, AgentPermissionMode.Ask) };
            settings.ConversationDefaults[key] = result;
        }
        return result;
    }

    /// <summary>
    /// 記下帳戶的模型清單；這一家的清單變了就重送設定（設定頁「模型設定」列的是各帳戶的聯集）。
    /// 回傳這個帳戶記的清單有沒有改變（沒變時呼叫端不必存檔）
    /// </summary>
    private bool UpdateModels(string accountId, IReadOnlyList<AgentModelOption> models)
    {
        if (settings.Models.TryGetValue(accountId, out var stored) && ConversationDefaultsRules.SameModels(stored, models))
            return false;
        var provider = accounts.Find(accountId)?.Provider;
        var before = provider is { } p ? ProviderModels(p) : null;
        settings.Models[accountId] = models.ToList();
        if (provider is { } changed && !ConversationDefaultsRules.SameModels(before, ProviderModels(changed)))
            PostSettings();
        // 〔同步〕這個帳戶的對話發布的 meta 帶著模型清單（其他電腦與瀏覽器版照它列選單）：清單變了就重發（內容沒變的發布者不送）
        foreach (var c in conversations.Values)
        {
            if (c.Meta.AccountId == accountId && !c.IsDraft && !c.IsReplica)
                HookHeaderChanged(c);
        }
        return true;
    }

    // ───────────────────────────── 存檔與維護 ─────────────────────────────

    private void SaveConversation(ConversationState c, bool saveItems)
    {
        // 草稿與其他電腦的對話（複本）不存；已刪除的對話（背景事件、送出失敗晚到）也不能寫回
        if (c.IsDraft || c.IsReplica || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
            return;
        try
        {
            store.SaveConversation(c.Meta);
            var itemsSaved = saveItems && c.ItemsLoaded;
            if (itemsSaved)
            {
                // 背景還沒寫的較舊快照作廢、正在寫的等它寫完：不會晚到蓋掉這份（ItemsFileWriter）
                itemsWriter?.Forget(c.Meta.Id);
                // 增量存檔：沒變的項目沿用上次的快照、從上一份檔案複製位元組，只序列化變了的（ItemsSaveCache）
                var cache = c.SaveCache ??= new ItemsSaveCache();
                store.SaveItems(c.Meta.Id, cache.Take(c.Items), cache.Layout);
                c.ItemsUnsaved = false;
            }

            HookConversationSaved(c, itemsSaved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast($"對話存檔失敗：{ex.Message}", isError: true);
        }
    }

    private ItemsFileWriter? itemsWriter;

    /// <summary>背景寫對話紀錄（定期維護用）；第一次用到時建立，寫入失敗回 UI 執行緒標成還沒存、下次維護再寫</summary>
    private ItemsFileWriter ItemsWriter
        => itemsWriter ??= new ItemsFileWriter((conversationId, ex) => host.BeginInvoke(() => OnItemsWriteFailed(conversationId, ex)));

    /// <summary>測試用：背景寫對話紀錄的執行緒（還沒用到是 null）</summary>
    internal ItemsFileWriter? ItemsWriterForTests => itemsWriter;

    /// <summary>測試用：這個對話的增量存檔快取（還沒存過或已經丟掉是 null）</summary>
    internal ItemsSaveCache? SaveCacheForTests(string conversationId) => conversations.TryGetValue(conversationId, out var c) ? c.SaveCache : null;

    private void OnItemsWriteFailed(string conversationId, Exception ex)
    {
        Log.Warn($"背景寫對話 {conversationId} 的紀錄失敗（下次維護再寫）：{ex.Message}");
        if (disposed || !conversations.TryGetValue(conversationId, out var c))
            return;
        c.ItemsUnsaved = true;
        Toast($"對話存檔失敗：{ex.Message}", isError: true);
    }

    /// <summary>
    /// 定期維護存執行中的對話：中繼資料照舊在這裡寫（小，同步要立刻送），紀錄在 UI 執行緒只取快照（<see cref="AgentChatItem.CloneForSave"/>，
    /// 欄位複製、字串共用），序列化、落盤與換檔交給背景執行緒（ItemsFileWriter）。以前整份序列化加落盤都在 UI 執行緒，
    /// 幾個長對話同時在跑時每 30 秒卡一兩百毫秒（2026-10-24 實測一次 205 ms）
    /// </summary>
    private void SaveConversationInBackground(ConversationState c)
    {
        if (c.IsDraft || c.IsReplica || !c.ItemsLoaded || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
            return;
        try
        {
            store.SaveConversation(c.Meta);
            // 沒變的項目沿用上次的快照（不再複製），背景寫檔時也只序列化變了的（ItemsSaveCache、ItemsJsonFile）
            var cache = c.SaveCache ??= new ItemsSaveCache();
            ItemsWriter.Enqueue(c.Meta.Id, store.GetItemsPath(c.Meta.Id), cache.Take(c.Items), cache.Layout);
            c.ItemsUnsaved = false;
            HookConversationSaved(c, itemsSaved: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Toast($"對話存檔失敗：{ex.Message}", isError: true);
        }
    }

    /// <summary>空佇列的指紋：還沒寫過 queue.json 的對話（QueueFingerprint 為 null）以它比較</summary>
    private static readonly string EmptyQueueFingerprint = new QueueSnapshot(new(), new(), new()).Fingerprint();

    /// <summary>
    /// 排隊訊息、待送附件、待送的拒絕說明有變動就寫進 queue.json（設計 9.4；當掉、Windows 更新重開、app 自我更新都不遺失）。
    /// 草稿不存；已刪除的對話不寫（否則會重建只有 queue.json 的資料夾）；讀不到的 queue.json 讀到之前不寫（不能蓋掉上次的佇列）。
    /// 不看 disposed：關閉時正是最後一次寫入的機會
    /// </summary>
    private void PersistQueueIfChanged(ConversationState c)
    {
        if (c.IsDraft || c.IsReplica || c.QueueUnread || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
            return;
        var snapshot = new QueueSnapshot(
            c.Queue.Select(q => new QueuedMessageData(q.Id, q.Text, q.Attachments.ToList(), q.RemoteOrigin, q.DisplayText)).ToList(),
            c.PendingAttachments.ToList(),
            c.PendingFeedback.ToList());
        var fingerprint = snapshot.Fingerprint();
        if (fingerprint == (c.QueueFingerprint ?? EmptyQueueFingerprint))
            return;
        HookQueueChanged(c);
        try
        {
            store.SaveQueue(c.Meta.Id, snapshot);
            c.QueueFingerprint = fingerprint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"佇列存檔失敗（{c.Meta.Id}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 載入 queue.json（啟動時；啟動時讀不到的由維護重讀），接在現有內容前面：檔案裡的比較早，讀不到的期間可能已經排了新的。
    /// 附件檔已不存在的略過，附件都不見又沒有文字的訊息也略過。上次沒送出的拒絕說明，那一輪已經不在了：
    /// 照被打斷的一輪的慣例（WrapUpInterruptedTurn）排到佇列最前面，跟著佇列暫停、按送出才送。回傳是否載入了內容
    /// </summary>
    private bool RestoreQueue(ConversationState c)
    {
        var (status, snapshot) = QueueStore.TryLoad(store.GetConversationDirectory(c.Meta.Id));
        c.QueueUnread = status == JsonReadStatus.Unreadable;
        if (snapshot == null)
            return false;
        var restored = new List<QueuedMessage>();
        if (AgentHubRules.BuildDenyFeedbackMessage(snapshot.PendingFeedback) is { } feedback)
            restored.Add(new QueuedMessage { Text = feedback, RemoteOrigin = DenyFeedbackRules.OriginOf(snapshot.PendingFeedback) });
        foreach (var q in snapshot.Queue)
        {
            var attachments = q.Attachments.Where(a => fileExists(a.StoredPath)).ToList();
            if (!string.IsNullOrWhiteSpace(q.Text) || attachments.Count > 0)
                restored.Add(new QueuedMessage { Id = q.Id, Text = q.Text, Attachments = attachments, RemoteOrigin = q.RemoteOrigin, DisplayText = q.DisplayText });
        }
        c.Queue.InsertRange(0, restored);
        c.PendingAttachments.InsertRange(0, snapshot.PendingAttachments.Where(a => fileExists(a.StoredPath)));
        // 記下檔案內容的指紋：還原的結果與檔案不同（略過的附件、轉進佇列的說明、讀不到期間新排的）就在下次變動或維護時重寫
        c.QueueFingerprint = snapshot.Fingerprint();
        return true;
    }

    /// <summary>
    /// 每 30 秒：執行中、項目有變動的對話存一次檔（當掉時少丟一點；沒變動的不整份重寫，例如在等核准），執行中的對話在 running.json 的快照內容變了（或 5 分鐘沒寫）時更新
    /// （當掉後下次啟動照它接續），閒置太久的程序關掉。測試直接呼叫
    /// </summary>
    internal void RunMaintenance() => RunMaintenance(DateTime.Now);

    /// <param name="now">閒置回收用的現在時間（測試可指定）</param>
    internal void RunMaintenance(DateTime now)
    {
        if (disposed)
            return;
        foreach (var c in conversations.Values.ToList())
        {
            // 啟動時讀不到的 queue.json：每次維護重讀，讀到就接回佇列並更新輸入區（PostComposer 也會寫檔）
            if (c.QueueUnread && RestoreQueue(c))
                PostComposer(c);
            PersistQueueIfChanged(c);
            if (c.Running && c.ItemsUnsaved)
                SaveConversationInBackground(c);
            // 停下一陣子的對話丟掉增量存檔的快取（下次存檔整份序列化一次再建）
            if (c.SaveCache is { } cache && ItemsSaveCache.ShouldDrop(c.Running, cache.LastUsed, now))
                c.SaveCache = null;
            // 執行中與等背景工作的：更新 running.json 的快照（背景工作已經沒了的刪掉）
            RefreshRunMarker(c);
            // 有存活的背景工作（Claude run_in_background）不回收：背景工作撐不過程序重開；開著 Remote Control 的也不回收（隨時能從手機接手）
            if (c.Session != null && c.Session.HasLiveProcess && !c.Session.HasLiveBackgroundWork
                && AgentHubRules.ShouldRecycle(c.Running, c.WaitingCount > 0, c.Session.HasRemoteControl, c.LastActivity, now))
            {
                JournalProcessClose(c, "idle", now: now);
                _ = c.Session.CloseAsync();
            }
        }
        // 選了帳戶、在等背景工作結束才換的閒置對話：背景工作都結束了就換過去（AgentHub.AccountSwitch.cs）
        TickDeferredAccountSwitch();
        // 額度用完自動換走的閒置對話：原帳戶的額度恢復了就換回去（AgentHub.AccountSwitch.cs）
        TickReturnHome(now);
        // 額度全用完的對話：等到重置後自動接著做（AgentHub.QuotaWait.cs）
        TickQuotaWait();
        RefreshListOnNewDay(now);
        TickTokens(now);
        // 畫面上看得到的對話：工作資料夾的 git 狀態定時重讀（AgentHub.Git.cs）
        TickGit();
        // 畫面上看得到的對話：執行列重新偵測專案（AgentHub.Launch.cs）
        TickLaunch();
        // 排程頁：開著 30 秒、沒開 5 分鐘重讀這台的 Windows 排程（AgentHub.Schedules.cs）
        TickSchedules();
        // 定時訊息：計時器之外每 30 秒也補看一次（睡眠、改時間之後照樣接上；AgentHub.TimedMessages.cs）
        TickTimed();
        // CLI 熱更新：漏掉的事件補檢查、背景工作的提示、排空寬限
        TickRestarts();
        // 背景活動日誌：排空中還在等誰、平常還在跑的背景工作與背景程式（AgentHub.Journal.cs）
        TickJournal(now);
        // 倒數與相對時間由網頁自己算；這裡順便更新系統匣，每 5 分鐘重新確認最近使用的資料夾
        UpdateTray();
        if (++maintenanceTicks % 10 == 0)
            _ = RefreshRecentFoldersAsync();
    }

    private int maintenanceTicks;

    private void UpdateRunningCounts()
    {
        foreach (var provider in AgentProviders.All)
            Volatile.Write(ref runningByProvider[(int)provider], conversations.Values.Count(c => c.Running && c.Meta.Provider == provider));
        HookRunningCountsChanged();
        MarkAccountsDirty();
        UpdateTray();
    }

    private void UpdateTray()
    {
        int running = RunningCount;
        int waiting = WaitingCount;
        var parts = new List<string> { "Clode Myriagent" };
        if (running > 0)
            parts.Add($"執行中 {running}");
        // 代理這一輪結束了、背景工作還在跑（AgentHub.Background.cs）
        int background = BackgroundWaitingCount;
        if (background > 0)
            parts.Add($"等背景工作 {background}");
        if (waiting > 0)
            parts.Add($"等你回應 {waiting}");
        if (SyncTrayText is { } syncText)
            parts.Add(syncText);
        foreach (var account in accounts.Accounts.Take(3))
        {
            var first = accounts.GetUsage(account.Id)?.Windows.FirstOrDefault();
            if (first != null)
                parts.Add($"{account.Label} {first.Percent:0}%");
        }
        var tooltip = string.Join("｜", parts);
        host.UpdateTray(tooltip.Length <= 127 ? tooltip : tooltip[..126] + "…", running, waiting);
        // 各視窗的外框跟著自己開著的對話（AgentHub.Windows.cs）
        UpdateWindowBorders();
    }

    // ───────────────────────────── 關閉 ─────────────────────────────

    /// <summary>
    /// Windows 詢問能不能登出／關機時：先把執行中的對話存檔、更新 running.json 的快照，但不停止（使用者之後可能取消關機；
    /// 確定結束時 Dispose 再寫一次「app 正常結束」的快照）
    /// </summary>
    public void SaveRunningConversations()
    {
        if (disposed)
            return;
        foreach (var c in conversations.Values.Where(c => c.Running).ToList())
            SaveConversation(c, saveItems: true);
        WriteRunMarkersOnExit(closedByApp: false);
    }

    /// <summary>
    /// 關閉 app：執行中的對話存檔並寫 running.json 的最後快照（下次啟動自動接續，AgentHub.InterruptedTurns.cs），
    /// 所有程序正常關閉（最多等幾秒），未送出的草稿刪除
    /// </summary>
    /// <param name="keepDrafts">
    /// 不刪資料夾的草稿（app 更新時 resume.json 列出的草稿對話：新版還原它的附件）。
    /// 一開始就設 disposed，之後（含等代理程序的期間）視窗關閉也不會在 DiscardDraft 刪草稿資料夾
    /// </param>
    public async Task ShutdownAsync(IReadOnlySet<string>? keepDrafts = null)
    {
        if (disposed)
            return;
        disposed = true;
        maintenanceTimer.Dispose();
        CancelUpdateBarriers();
        CancelAutoTitles();
        StopGit();
        StopHealth();
        StopSchedules();
        DisposeAccountDelegation();
        StopLaunchAll();
        StopBackground();

        PersistQueuesOnExit();
        itemsWriter?.Drain(TimeSpan.FromSeconds(10));
        foreach (var c in conversations.Values.Where(c => c.Running).ToList())
            SaveConversation(c, saveItems: true);
        // 關程序之前：快照要看得到正在執行的工具與背景工作
        WriteRunMarkersOnExit(closedByApp: true);
        tokenTally.Flush();
        usageSegments.Flush();

        var closing = conversations.Values
            .Where(c => c.Session != null)
            .Select(async c =>
            {
                try
                {
                    if (c.Running)
                        c.Session!.Dispose();
                    else
                        await c.Session!.CloseAsync();
                }
                catch (Exception ex)
                {
                    Log.Warn($"關閉代理程序失敗：{ex.Message}");
                }
                finally
                {
                    c.Session?.Dispose();
                }
            })
            .ToList();
        await Task.WhenAll(closing);
        backgroundMarkdown?.Dispose();
        mediaKeeper?.Dispose();
        itemsWriter?.Dispose();

        // app 更新時列在 resume.json 的草稿留著（新版還原它的附件）
        foreach (var draft in conversations.Values.Where(c => c.IsDraft && keepDrafts?.Contains(c.Meta.Id) != true).ToList())
            store.DeleteConversation(draft.Meta.Id);
        settings.Save();
    }

    /// <summary>
    /// 結束時最後一次寫 queue.json（ShutdownAsync、Dispose；app 更新的結束流程也走 ShutdownAsync）。使用者按了停止、這一輪還沒結束的對話，
    /// 待送的拒絕說明先清掉：按了停止就不送拒絕說明（同 CompleteTurn、WrapUpInterruptedTurn），重啟後不能被 RestoreQueue 轉成佇列第一則
    /// </summary>
    private void PersistQueuesOnExit()
    {
        foreach (var c in conversations.Values.ToList())
        {
            if (c.StopRequested)
                c.PendingFeedback.Clear();
            PersistQueueIfChanged(c);
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            PersistQueuesOnExit();
            // 背景排著的紀錄先寫完（最多 10 秒），接著執行中的對話照常同步存最後一份（SaveConversation 會先等背景那份，順序不會顛倒）
            itemsWriter?.Drain(TimeSpan.FromSeconds(10));
            foreach (var c in conversations.Values.Where(c => c.Running).ToList())
                SaveConversation(c, saveItems: true);
            // 沒經過 ShutdownAsync 就結束（Windows 確定登出、關機或重新開機）：執行中的一輪下次啟動接續
            WriteRunMarkersOnExit(closedByApp: true);
            tokenTally.Flush();
            usageSegments.Flush();
            disposed = true;
            maintenanceTimer.Dispose();
            CancelUpdateBarriers();
            CancelAutoTitles();
            StopGit();
            StopHealth();
            StopSchedules();
            StopAccountMcp();
            DisposeAccountDelegation();
            StopLaunchAll();
            StopBackground();
            backgroundMarkdown?.Dispose();
            mediaKeeper?.Dispose();
            itemsWriter?.Dispose();
        }
        foreach (var c in conversations.Values)
            c.Session?.Dispose();
    }
}

/// <summary>只顯示一個對話的彈出視窗</summary>
internal interface IPopoutView : IAgentView
{
    string ConversationId { get; }
}
