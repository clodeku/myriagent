using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 遠端指令的授權者（中樞閘門 5i；設計 8.11、14.11 D11）：前面的檢查都過了之後最後一道，null＝允許，否則是 <see cref="RemoteResultCodes"/> 的拒絕代碼。
/// 只在 UI 執行緒呼叫。WP4.1 的預設是 <see cref="DenyAllRemoteAuthorizer"/>（一律 unsupported）；WP4.4 以 partial <c>CreateRemoteAuthorizer</c> 換掉
/// </summary>
internal interface IRemoteCommandAuthorizer
{
    /// <param name="facts">閘門 5d 收集的事實</param>
    /// <param name="tier">最終的層級（信封宣告的與這台重算的取較高者）</param>
    string? Authorize(RemoteViewContext context, string cmd, JObject command, RemoteTierFacts facts, RemoteTierResult tier);
}

/// <summary>WP4.1 的預設授權者：一律回 <see cref="RemoteResultCodes.Unsupported"/>，WP4.4 之前沒有任何遠端指令會執行</summary>
internal sealed class DenyAllRemoteAuthorizer : IRemoteCommandAuthorizer
{
    public static DenyAllRemoteAuthorizer Instance { get; } = new();

    private DenyAllRemoteAuthorizer()
    {
    }

    public string? Authorize(RemoteViewContext context, string cmd, JObject command, RemoteTierFacts facts, RemoteTierResult tier) => RemoteResultCodes.Unsupported;
}

/// <summary>
/// 遠端指令的處理者（閘門 5j；WP4.4 的對話、WP4.7 的檔案、WP4.8 的帳戶以登記 partial 加進來）。只在 UI 執行緒呼叫
/// </summary>
internal interface IRemoteCommandHandler
{
    /// <summary>
    /// 回 true＝接下了：之後以 <see cref="RemoteViewContext.Complete"/>（中樞已接受，例如 send 已建立項目或排進佇列）或
    /// <see cref="RemoteViewContext.Reject"/> 回報，30 秒沒有就是 unknown。<see cref="RemoteViewContext.Authorized"/> 是 false 時必須拒絕
    /// （防止繞過閘門直接呼叫）。<paramref name="view"/> 是這個指令的回應檢視：中樞回給它的網頁訊息經 CommandOutput 轉給發起的瀏覽器連線
    /// </summary>
    /// <param name="command">套過型錄限制（RemoteCommandCatalog.ApplyLimits）、conversationId 換成本機 id 的複本</param>
    bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command);
}

/// <summary>閘門的結果（設計 14.11 D21）；<c>Step</c> 只進遠端操作紀錄</summary>
/// <param name="State"><see cref="RemoteResultStates"/></param>
/// <param name="Code">rejected 的代碼（<see cref="RemoteResultCodes"/>）；done、unknown 是 null</param>
/// <param name="Tier">最終的層級（閘門 5d 之前停下的是信封宣告的）</param>
/// <param name="Step">停在哪一步（<see cref="RemoteGateSteps"/>）；done 是 null</param>
internal sealed record RemoteOutcome(string State, string? Code, string? Message, int Tier, bool FullAccess, string? Step)
{
    /// <summary>回給伺服器的 CommandResult（docs/sync-protocol.md 14.6）</summary>
    public CommandResult ToResult(string commandId) => new(commandId, State, Code, Message, Tier, FullAccess);
}

/// <summary>
/// 一個遠端指令在中樞裡的狀態（閘門 <c>ExecuteRemoteAsync</c> 建立回應檢視、處理者回報結果；設計 14.11 D11、D21）。
/// 信封裡的值在信封驗過之後才放進來（<see cref="FromEnvelope"/>），所以閘門不再查簽章與時間。只在 UI 執行緒使用、只能用一次。
/// 「完成」是中樞已接受，不是代理這一輪結束；第一個信號為準（之後的 Complete／Reject 不改結果）
/// </summary>
internal sealed class RemoteViewContext
{
    private readonly TaskCompletionSource<RemoteOutcome> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<JObject>? output;
    private long outputUntil;
    private bool started;

    /// <param name="commandId">信封的 cid（伺服器產生的 ULID）</param>
    /// <param name="deviceId">信封的 dev（信封驗證已確認是這台）</param>
    /// <param name="conversationId">信封的 conv（線上的對話 id；路由 Device 的指令是 null）</param>
    /// <param name="epoch">信封的 epoch（與 conv 一起帶）</param>
    /// <param name="declaredTier">伺服器算的層級（0～2）</param>
    /// <param name="uvAt">伺服器工作階段最後一次輸入密碼的伺服器時間（閘門 5f）</param>
    /// <param name="created">指令建立的伺服器時間（閘門 5f）</param>
    /// <param name="defaultPassword">擁有者密碼還是預設值（閘門 5g：只記錄與警告）</param>
    /// <param name="issuerLabel">發起的瀏覽器與作業系統（本機通知與遠端操作紀錄顯示）</param>
    public RemoteViewContext(string commandId, string deviceId, string? conversationId, long? epoch, int declaredTier, bool declaredFullAccess,
        long? uvAt, long created, bool defaultPassword, string issuerLabel)
    {
        ArgumentException.ThrowIfNullOrEmpty(commandId);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        if (declaredTier is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(declaredTier), declaredTier, "層級是 0～2");
        CommandId = commandId;
        DeviceId = deviceId;
        ConversationId = conversationId;
        Epoch = epoch;
        DeclaredTier = declaredTier;
        DeclaredFullAccess = declaredFullAccess;
        UvAt = uvAt;
        Created = created;
        DefaultPassword = defaultPassword;
        IssuerLabel = issuerLabel ?? string.Empty;
        Tier = new RemoteTierResult(declaredTier, declaredFullAccess);
    }

    /// <summary>驗過的信封內容（CommandEnvelope.Verify 的 Body）</summary>
    public static RemoteViewContext FromEnvelope(CommandEnvelopeBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new RemoteViewContext(body.Cid, body.Dev, body.Conv, body.Epoch, body.Tier, body.FullAccess, body.Auth.UvAt, body.Created,
            body.Auth.DefaultPassword, RemoteNotifyRules.IssuerDisplay(body.Issuer.Kind, body.Issuer.Label))
        {
            Collaborator = string.Equals(body.Issuer.Kind, Security.CommandIssuerKinds.Collaborator, StringComparison.Ordinal)
                ? Sync.CollaboratorRules.CleanAuthor(body.Issuer.Label) ?? "協作人"
                : null,
        };
    }

    /// <summary>
    /// 協作人發起的（信封的 <c>issuer.kind</c> 是 collaborator，多人協作）：協作人的顯示名稱；擁有者的瀏覽器與其他電腦是 null。
    /// 閘門只收 <see cref="Sync.CollaboratorRules.RemoteCommands"/>，送出的訊息與回應的卡片記下它
    /// </summary>
    public string? Collaborator { get; init; }

    public string CommandId { get; }

    public string DeviceId { get; }

    public string? ConversationId { get; }

    public long? Epoch { get; }

    public int DeclaredTier { get; }

    public bool DeclaredFullAccess { get; }

    public long? UvAt { get; }

    public long Created { get; }

    public bool DefaultPassword { get; }

    public string IssuerLabel { get; }

    /// <summary>指令名稱（閘門 5a 之後才有）：CommandOutput 的白名單照它</summary>
    public string? CommandName { get; private set; }

    /// <summary>最終的層級（閘門 5d 之後；之前是信封宣告的）</summary>
    public RemoteTierResult Tier { get; private set; }

    /// <summary>通過了閘門 5i（授權者）：只有這之後處理者才能執行（看到 false 必須拒絕）</summary>
    public bool Authorized { get; private set; }

    /// <summary>已經有結果</summary>
    public bool IsCompleted => completion.Task.IsCompleted;

    /// <summary>閘門的結果</summary>
    public Task<RemoteOutcome> Completion => completion.Task;

    /// <summary>
    /// 中樞已接受。<paramref name="output"/> 不是 null 時先當成一則回給這個檢視的網頁訊息轉出（同回應檢視，照型錄的白名單）
    /// </summary>
    public void Complete(JObject? output = null)
    {
        if (output != null)
            Output(output);
        completion.TrySetResult(new RemoteOutcome(RemoteResultStates.Done, null, null, Tier.Tier, Tier.FullAccess, null));
    }

    /// <summary>拒絕（什麼都沒有執行）。代碼與步驟不認得是程式錯誤，丟例外</summary>
    public void Reject(string code, string message, string step)
    {
        if (!RemoteResultCodes.All.Contains(code, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(code), code, "不認得的拒絕代碼");
        if (!RemoteGateSteps.Order.Contains(step, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(step), step, "不認得的閘門步驟");
        completion.TrySetResult(new RemoteOutcome(RemoteResultStates.Rejected, code, Clip(message), Tier.Tier, Tier.FullAccess, step));
    }

    /// <summary>結果不明（處理途中出錯、30 秒沒有完成信號）：指令可能已執行，瀏覽器不得自動重送（D21）</summary>
    internal void Fail(string message, string step)
        => completion.TrySetResult(new RemoteOutcome(RemoteResultStates.Unknown, null, Clip(message), Tier.Tier, Tier.FullAccess, step));

    /// <summary>閘門開始（一個 context 只能用一次，已經有結果的也不行：gate/F6）：記下輸出的去處與 30 秒的回應期限</summary>
    internal void Begin(Action<JObject> sink, TimeSpan outputWindow)
    {
        if (started || IsCompleted)
            throw new InvalidOperationException("一個 RemoteViewContext 只能經閘門一次");
        started = true;
        output = sink;
        outputUntil = Environment.TickCount64 + (long)outputWindow.TotalMilliseconds;
    }

    internal void SetCommand(string name) => CommandName = name;

    internal void SetTier(RemoteTierResult tier) => Tier = tier;

    internal void MarkAuthorized() => Authorized = true;

    /// <summary>
    /// 中樞回給這個檢視的網頁訊息 → CommandOutput（D18、D57）：只在授權之後、閘門開始後 30 秒內，而且型錄允許這個指令回這種訊息
    /// （其他丟掉、記錄種類）；轉出的是複本，<c>conversationId</c> 換成線上 id（RejectSend 帶的是本機的資料夾名，可能是大寫；
    /// 伺服器以指令的線上 id 核對 sendRejected，WP4.4 計畫的對齊結果 A9）
    /// </summary>
    internal void Output(JObject message)
    {
        var type = message.Value<string>("type");
        if (!Authorized || output == null || Environment.TickCount64 > outputUntil || !RemoteCommandCatalog.AllowsOutput(CommandName, type))
        {
            Log.Info($"遠端指令 {CommandId}（{CommandName}）的輸出 {type} 不轉給瀏覽器");
            return;
        }

        try
        {
            var copy = (JObject)message.DeepClone();
            if (copy["conversationId"] is JValue { Type: JTokenType.String, Value: string localId } && ConversationIds.ToWire(localId) is { } wireId)
                copy["conversationId"] = wireId;
            output(copy);
        }
        catch (Exception ex)
        {
            Log.Error($"遠端指令 {CommandId} 的輸出轉送失敗", ex);
        }
    }

    private static string Clip(string? message)
    {
        var text = message ?? string.Empty;
        return text.Length <= RemoteResultRules.MaxMessageLength ? text : text[..(RemoteResultRules.MaxMessageLength - 1)] + "…";
    }
}

// 遠端指令進中樞的唯一入口（閘門 ExecuteRemoteAsync，設計 8.11、14.11 D11；各步的代碼見 docs/sync-protocol.md 14.6）與第 4 階段的登記點（D48）。
// 擁有者：WP4.4（本檔）。閘門不是 partial：檢查順序固定，WP4.4 以下的工作包只能經登記點加授權者、處理者與事實。
// 中樞只碰 Protocol 的規則與同步的純函式規則（SyncCoordinatorRules、SyncProviderRules），不碰同步引擎（S6）
internal sealed partial class AgentHub
{
    /// <summary>
    /// 沒有登記處理者時可以走 <see cref="HandleCommand"/> 的指令（閘門 5j 的退路）：效果都在 HandleCommand 裡、不送文字進代理、
    /// 不寫這台的持久設定、不碰工作資料夾以外的檔案。其他（代理輸入、卡片、要套 RemoteLimits 的、刪除〔要移到回收區，D10〕、
    /// 列檔與搜尋〔範圍與禁止清單，D14〕、HandleCommand 沒有的）沒有處理者就回 unsupported。
    /// 帳戶的登入、登出與 CLI 的安裝、更新由 WP4.8 的處理者回報真的結果（AgentHub.RemoteAccounts.cs），不在這裡
    /// </summary>
    internal static IReadOnlySet<string> RemoteFallbackCommands { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "cancelQueued", "stop", "skipQuestion", "rename", "resetFolder", "removeAttachment",
        "accountLoginCancel", "accountRemove", "accountRefresh", "cliCheck",
    };

    /// <summary>回應檢視轉送輸出的期限（指令開始後 30 秒，web-protocol 第一節〔遠端〕）</summary>
    internal static readonly TimeSpan RemoteOutputWindow = TimeSpan.FromSeconds(30);

    private IRemoteCommandAuthorizer remoteAuthorizer = DenyAllRemoteAuthorizer.Instance;

    private readonly List<IRemoteCommandHandler> remoteHandlers = new();

    /// <summary>交給處理者之後等完成信號多久（D21：逾時回 unknown）；測試可縮短</summary>
    internal TimeSpan RemoteCompletionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>副本旗標（D41）：其他電腦的對話在這台的副本，不能送出、不啟動 CLI，閘門 5c 拒絕。副本的載入在 WP4.3</summary>
    private sealed partial class ConversationState
    {
        public bool IsReplica;

        /// <summary>被 HoldRemoteSend 留下過、已經提示過本機的遠端指令（cid）：同一則重試多次只提示一次（gate/F3，WP4.4 計畫 D20）</summary>
        public readonly HashSet<string> HeldNotified = new(StringComparer.Ordinal);
    }

    // ───────────────────────────── 登記點（D48；沒有實作時呼叫連同引數一起被編譯器移除）─────────────────────────────

    /// <summary>WP4.4：換掉預設的 <see cref="DenyAllRemoteAuthorizer"/>（建構式呼叫，那時還看不到帳戶）</summary>
    partial void CreateRemoteAuthorizer(ref IRemoteCommandAuthorizer authorizer);

    /// <summary>WP4.4：對話指令的處理者</summary>
    partial void RegisterConversationRemoteHandlers(List<IRemoteCommandHandler> into);

    /// <summary>WP4.8：帳戶指令的處理者</summary>
    partial void RegisterAccountRemoteHandlers(List<IRemoteCommandHandler> into);

    /// <summary>WP4.7：檔案指令的處理者</summary>
    partial void RegisterFileRemoteHandlers(List<IRemoteCommandHandler> into);

    /// <summary>遠端執行指令（sync-protocol 14.11）的處理者</summary>
    partial void RegisterExecRemoteHandlers(List<IRemoteCommandHandler> into);

    /// <summary>
    /// WP4.4：對話有沒有預先放行規則（留 null＝不明，電腦當成有）。擁有者 2026-10-19 回答 WP4.4 Q1 選 A：不偵測 CLI 設定檔，
    /// 一律 false（只看對話的權限模式，推翻 Q3-A 的後半），實作在 AgentHub.RemoteHandlers.cs
    /// </summary>
    partial void DetectPreApproved(AgentConversation conversation, ref bool? preApproved);

    /// <summary>
    /// WP4.4：conversationCreate 要開的新對話有沒有預先放行規則（還沒有對話，只看帳戶）：<paramref name="workingDirectory"/> 是 null 時
    /// 代表對話自己的新 workspace。留 null＝不明（電腦當成有）；WP4.4 Q1 選 A，一律 false（同 <see cref="DetectPreApproved"/>）
    /// </summary>
    partial void DetectAccountPreApproved(AgentAccount account, string? workingDirectory, ref bool? preApproved);

    /// <summary>WP4.4：核准卡的遠端風險（RemoteRiskRules，設計 8.10；留 null＝沒分類，電腦當成非一般，D9）</summary>
    partial void ClassifyRemoteRisk(AgentConversation conversation, AgentChatItem card, ref AgentRemoteRisk? risk);

    /// <summary>WP4.3：這個對話有沒有人在其他地方（瀏覽器）看著（NotifyAttention 判斷要不要提醒）</summary>
    partial void IsSeenRemotely(string conversationId, ref bool seen);

    /// <summary>
    /// WP4.4：HandleCommand 開頭的擴充（副本的白名單之後）；handled＝已處理，switch 不再看。實作在 AgentHub.RemoteHandlers.cs，
    /// 第 5 階段以後的工作包經那裡的 <c>InterceptCommandExtension</c> 擴充（第 4 階段沒有實作）
    /// </summary>
    partial void InterceptCommand(IAgentView view, JObject command, ref bool handled);

    /// <summary>
    /// 第 5 階段（WP5.9 的 AgentHub.Handoff.cs 實作；計畫 5.8、第 10 節）：網頁的交接指令——<c>takeover</c>、<c>handoffCancel</c>、<c>handoffPreview</c>
    /// （本機的交給同步的協調者轉呼叫 HandoffStart／Cancel／Preview），以及桌面本機專用的 <c>handoffRetry</c>、<c>syncPathMaps</c>、
    /// <c>syncPathMapRemove</c>、<c>syncProjectRoots</c>（遠端的檢視一律拒絕）。在 HandleCommand 的最開頭呼叫（WP5.1 移到桌面遠端操作的轉送與副本的
    /// 白名單之前：「在這台接手」是對副本下的指令），所以遠端指令的回應檢視（<c>RemoteResponseView</c>）也會到這裡，實作要自己判斷；handled＝已處理
    /// </summary>
    partial void HandleHandoffCommand(IAgentView view, string? cmd, JObject command, ref bool handled);

    // 第 5 階段的中樞登記點（WP5.1 只宣告；呼叫點由實作的工作包加在計畫寫明的位置。沒有實作時編譯器連呼叫一起拿掉）

    /// <summary>
    /// WP5.6（AgentHub.HandoffOut.cs）：交出中的對話到了可能的安全點（RecheckRestart、OnTurnEndedForRestart 的同一批觸發點，計畫 A3）：
    /// afterTurn 等這一輪結束且到安全點才關程序
    /// </summary>
    partial void OnHandoffRecheck(ConversationState c);

    /// <summary>WP5.6：交出中的對話凍結佇列（CompleteTurn 不自動送 Queue[0]、新的送出一律排隊；計畫 A3）。<paramref name="hold"/> 設成 true＝凍結</summary>
    partial void HoldsQueueForHandoff(ConversationState c, ref bool hold);

    /// <summary>
    /// WP5.7（AgentHub.HandoffIn.cs）：啟動 CLI 之前的租約守門（StartProcessAsync 之前；計畫第 6 節）：<c>leases.state</c> 是 handoffOut（關閉之後）、
    /// handoffIn、importFailed、needsFolder、fenced 時不啟動。<paramref name="blocked"/> 設成給使用者看的原因＝不啟動
    /// </summary>
    partial void CheckLeaseBeforeStart(ConversationState c, ref string? blocked);

    /// <summary>建構式呼叫：授權者與處理者的登記（不碰帳戶：AppController 建好中樞之後才載入帳戶清單）</summary>
    private void RegisterRemoteExtensions()
    {
        IRemoteCommandAuthorizer authorizer = DenyAllRemoteAuthorizer.Instance;
        CreateRemoteAuthorizer(ref authorizer);
        remoteAuthorizer = authorizer;
        RegisterConversationRemoteHandlers(remoteHandlers);
        RegisterAccountRemoteHandlers(remoteHandlers);
        RegisterFileRemoteHandlers(remoteHandlers);
        RegisterExecRemoteHandlers(remoteHandlers);
        // 排程頁的遠端操作（AgentHub.ScheduleRemote.cs）
        RegisterScheduleRemoteHandlers(remoteHandlers);
    }

    // ───────────────────────────── 閘門 ─────────────────────────────

    /// <summary>
    /// 遠端指令進中樞的唯一入口（UI 執行緒；同步引擎驗過信封、去重、先記 executing 之後經 IAgentHost.BeginInvoke 轉入）。
    /// 依 5a～5j 的固定順序檢查，任一步失敗以 Reject 結束；交給處理者之後等完成信號，逾時回 unknown。
    /// <paramref name="output"/> 收中樞回給這個檢視的網頁訊息（已照型錄的白名單過濾），在 UI 執行緒呼叫
    /// </summary>
    internal async Task<RemoteOutcome> ExecuteRemoteAsync(RemoteViewContext context, JObject command, Action<JObject> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(output);
        context.Begin(output, RemoteOutputWindow);
        RunRemoteGate(context, command);
        if (!context.IsCompleted)
        {
            var timeout = Task.Delay(RemoteCompletionTimeout);
            if (await Task.WhenAny(context.Completion, timeout) != context.Completion)
                context.Fail("電腦沒有在時限內回報結果，指令可能已經執行；請看對話的內容確認", RemoteGateSteps.Handler);
        }

        return await context.Completion;
    }

    /// <summary>閘門 5a～5j 的同步部分；交給處理者之前出錯（程式錯誤）一律拒絕（什麼都還沒執行），處理途中出錯是 unknown</summary>
    private void RunRemoteGate(RemoteViewContext context, JObject command)
    {
        var step = RemoteGateSteps.Route;
        try
        {
            if (disposed)
            {
                context.Reject(RemoteResultCodes.Busy, "電腦的程式正在關閉", step);
                return;
            }

            // 5a 型錄的路由：只有 Owner／Device 由電腦執行；伺服器處理的、桌面專屬的、沒列名的（含大小寫不同）一律拒絕
            var name = command["cmd"] is JValue { Type: JTokenType.String, Value: string text } ? text : null;
            if (RemoteCommandCatalog.Find(name) is not { Route: RemoteRoute.Owner or RemoteRoute.Device } spec)
            {
                context.Reject(RemoteResultCodes.Invalid, "這不是可以從遠端執行的指令", step);
                return;
            }

            context.SetCommand(spec.Name);

            // 協作人（多人協作）：只收對話裡的「對話與核准」與檔案的瀏覽、下載（伺服器只是第一道，這裡再擋一次）
            if (context.Collaborator is not null && (spec.Route != RemoteRoute.Owner || !Sync.CollaboratorRules.RemoteCommands.Contains(spec.Name)))
            {
                context.Reject(RemoteResultCodes.Policy, "協作人不能做這件事", step);
                return;
            }

            // 5b 形狀：鍵白名單、型別、長度，數值列舉一律拒絕（D6）
            step = RemoteGateSteps.Shape;
            if (RemoteCommandShapeRules.Check(command) is { } shapeError)
            {
                context.Reject(RemoteResultCodes.Invalid, $"指令的格式不對：{shapeError}", step);
                return;
            }

            // 5c 對話條件（D12）
            step = RemoteGateSteps.Conversation;
            if (CheckRemoteConversation(context, spec, command, out var c) is { } conversationReject)
            {
                context.Reject(conversationReject.Code, conversationReject.Message, step);
                return;
            }

            // 5d 依這台的事實重算層級，取與信封宣告的較高者（D5）；不會拒絕
            step = RemoteGateSteps.Tier;
            AgentChatItem? card = null;
            if (c != null && RemoteCardRules.IsCardCommand(spec.Name))
            {
                EnsureItems(c, notifyUnreadable: false);
                card = FindItem(c, command.Value<string>("itemId"));
            }

            var facts = CollectRemoteFacts(spec.Name, command, c, card);
            var device = RemoteTierRules.Evaluate(spec.Name, command, facts, RemoteSide.Device);
            // setAccount 換到另一家時權限模式改成那一家設定頁的預設（SetAccount）：模式真的改變時照改成的模式算（完全自動要「允許遠端完全自動」，合併前審查 GATE-1）
            if (spec.Name == "setAccount" && c != null && facts.TargetMode is { } switched && switched != c.Meta.PermissionMode)
                device = device.Max(RemoteTierRules.ModeTier(switched));
            var tier = RemoteTierRules.Final(context.DeclaredTier, context.DeclaredFullAccess, device);
            context.SetTier(tier);

            // 5e 本機政策（暫停等於 Off：與 Hello 回報給伺服器的同一個，D7）
            step = RemoteGateSteps.Policy;
            if (RemotePolicyRules.Check(SyncCoordinatorRules.HelloPolicy(settings.Sync), tier.Tier, tier.FullAccess) is { } policyCode)
            {
                context.Reject(policyCode, "這台電腦的遠端操作設定不允許這個動作", step);
                return;
            }

            // 5f 驗證時間（只比伺服器時間；擁有者選「都不用輸入」，目前一律新鮮，Q2）
            step = RemoteGateSteps.Fresh;
            if (!RemoteAuthRules.IsFresh(tier.Tier, context.UvAt, context.Created))
            {
                context.Reject(RemoteResultCodes.StepUpRequired, "要先重新輸入密碼", step);
                return;
            }

            // 5g 預設密碼：目前不拒絕（Q5-B），旗標只進遠端操作紀錄與網頁的常駐警告
            step = RemoteGateSteps.DefaultPassword;
            if (!RemoteAuthRules.DefaultPasswordAllows(tier.Tier, context.DefaultPassword))
            {
                context.Reject(RemoteResultCodes.Policy, "擁有者密碼還是預設值，先改密碼才能做這個遠端操作", step);
                return;
            }

            // 5h 卡片：rd1 摘要與 RequestKey（遠端不放行 null，D36）。產生卡片的程序換了的，新程序啟動時已經作廢（OnProcessStarted），不在等待
            step = RemoteGateSteps.Card;
            if (RemoteCardRules.Check(spec.Name, command, card) is { } cardCode)
            {
                context.Reject(cardCode, cardCode == RemoteResultCodes.Stale ? "這張卡已在其他地方處理、內容已經改變，或 CLI 已重新啟動" : "回應的內容和這張卡對不上", step);
                return;
            }

            // 5i 授權者（WP4.1 的預設一律 unsupported）
            step = RemoteGateSteps.Authorize;
            if (remoteAuthorizer.Authorize(context, spec.Name, command, facts, tier) is { } denied)
            {
                var code = RemoteResultCodes.All.Contains(denied, StringComparer.Ordinal) ? denied : RemoteResultCodes.Unsupported;
                context.Reject(code, code == RemoteResultCodes.Unsupported ? "這台電腦的程式還不支援這個遠端指令" : "這台電腦不允許這個遠端指令", step);
                return;
            }

            context.MarkAuthorized();

            // 5j 交給處理者；沒有就以回應檢視走 HandleCommand（只限 RemoteFallbackCommands）
            step = RemoteGateSteps.Handler;
            var effective = RemoteCommandCatalog.ApplyLimits(command);
            if (c != null)
                effective["conversationId"] = c.Meta.Id;
            var view = new RemoteResponseView(context);
            foreach (var handler in remoteHandlers.ToList())
            {
                if (handler.TryHandle(context, view, spec.Name, effective))
                    return;
            }

            if (!RemoteFallbackCommands.Contains(spec.Name))
            {
                context.Reject(RemoteResultCodes.Unsupported, "這台電腦的程式還不支援這個遠端指令", step);
                return;
            }

            HandleCommand(view, effective);
            context.Complete();
        }
        catch (Exception ex)
        {
            Log.Error($"遠端指令 {context.CommandId} 在閘門的 {step} 出錯", ex);
            if (context.Authorized)
                context.Fail("電腦處理這個指令時出錯，指令可能已經執行；請看對話的內容確認", RemoteGateSteps.Handler);
            else
                context.Reject(RemoteResultCodes.Invalid, "電腦處理這個指令時出錯", step);
        }
    }

    /// <summary>
    /// 閘門 5c（D12）：路由 Owner 的指令，<c>conv</c> 要等於 <c>cmd.conversationId</c>、對話存在、不是草稿或副本、擁有者是 null 或這台、
    /// epoch 等於 <c>LeaseEpoch ?? 1</c>，而且對話的同步政策是 full（metadata、paused 只收 stop）；路由 Device 的指令（含 conversationCreate）不看對話，
    /// 不能帶 conv、<c>deviceId</c> 有帶時要是這台；conversationCreate 不能建不進同步的代理種類（D39）。
    /// 兩種路由的帳戶欄位都要是這台的本機帳戶（<see cref="CheckRemoteAccount"/>）。通過回 null
    /// </summary>
    private (string Code, string Message)? CheckRemoteConversation(RemoteViewContext context, RemoteCommandSpec spec, JObject command, out ConversationState? conversation)
    {
        conversation = null;
        if (spec.Route == RemoteRoute.Device)
        {
            if (context.ConversationId != null || context.Epoch != null
                || command["deviceId"] is JValue { Type: JTokenType.String, Value: string target } && !string.Equals(target, context.DeviceId, StringComparison.Ordinal))
                return (RemoteResultCodes.NotOwner, "這個指令不是給這台電腦的");
            if (CheckRemoteAccount(spec, command) is { } deviceAccountReject)
                return deviceAccountReject;
            if (spec.Name == RemoteCommandCatalog.ConversationCreate && accounts.Find(command.Value<string>("accountId")) is { } account
                && !SyncProviderRules.TryWireName(account.Provider, out _))
                return (RemoteResultCodes.Policy, "這種代理的對話只留在這台電腦，不能從遠端建立");
            return null;
        }

        var wireId = command.Value<string>("conversationId");
        if (context.ConversationId == null || !string.Equals(context.ConversationId, wireId, StringComparison.Ordinal)
            || FindByWireId(wireId) is not { } c || c.IsDraft || c.IsReplica
            || c.Meta.OwnerDeviceId is { } owner && !string.Equals(owner, context.DeviceId, StringComparison.Ordinal)
            || context.Epoch != (c.Meta.LeaseEpoch ?? ConversationIds.DefaultEpoch))
            return (RemoteResultCodes.NotOwner, "這台電腦不是這個對話的擁有者，或對話已不存在");
        // 有效的同步政策（伺服器刪除的旁檔一律 localOnly：從回收區還原的、換過不進同步的代理種類的；同發布端與 HoldRemoteSend，合併前審查 AMO-03）
        var policy = EffectiveSyncPolicy(c);
        // metadata 與 paused 都照樣發 meta 類（遠端看得到對話與執行中），只收 stop：DLP 在一輪執行中命中而自動 paused 時正是最需要從遠端停下代理的時候
        if (!(policy == ConversationSyncPolicies.Full
              || (policy == ConversationSyncPolicies.Metadata || policy == ConversationSyncPolicies.Paused) && spec.Name == RemoteCommandCatalog.Stop))
            return (RemoteResultCodes.Policy, "這個對話的同步設定不允許遠端操作");
        if (CheckRemoteAccount(spec, command) is { } ownerAccountReject)
            return ownerAccountReject;
        conversation = c;
        return null;
    }

    /// <summary>
    /// 閘門 5c 的帳戶（WP4.4 計畫 D16）：型錄的帳戶欄位（<see cref="RemoteFieldKind.AccountId"/>：setAccount、conversationCreate、帳戶指令）帶了字串值時，
    /// 一定要是這台的本機帳戶（伺服器簽信封前已把網頁的邏輯 id 換成這台的）；找不到回 invalid，退路的帳戶指令不會回假的 done、
    /// conversationCreate 不會跳過代理種類的檢查。帳戶 null（accountRefresh 的「全部帳戶」）不查
    /// </summary>
    private (string Code, string Message)? CheckRemoteAccount(RemoteCommandSpec spec, JObject command)
    {
        foreach (var field in spec.Fields)
        {
            if (field.Kind == RemoteFieldKind.AccountId && command[field.Name] is JValue { Type: JTokenType.String, Value: string accountId }
                && accounts.Find(accountId) is null)
                return (RemoteResultCodes.Invalid, "這台電腦沒有這個帳戶");
        }

        return null;
    }

    /// <summary>線上的對話 id（小寫）→ 這台的對話（資料夾名大小寫都收，D38）</summary>
    private ConversationState? FindByWireId(string? wireId)
        => wireId == null ? null : conversations.Values.FirstOrDefault(s => string.Equals(ConversationIds.ToWire(s.Meta.Id), wireId, StringComparison.Ordinal));

    /// <summary>
    /// 閘門 5d 的事實（電腦端：不明一律留 null，RemoteTierRules 當成最高）。磁碟種類要在背景執行緒查（設計 8.7），WP4.1 不查，
    /// 所以 <c>FolderForbidden</c> 是 null（遠端選資料夾一律層級 2）；預先放行規則與核准卡的風險由 WP4.4 的登記點提供
    /// （conversationCreate 還沒有對話，看帳戶層級的 <c>DetectAccountPreApproved</c>）。listFiles 的範圍是工作資料夾與
    /// 對話的 attachments\ 子資料夾，不是整個對話資料夾：queue.json、items.json、meta.json 與它們的備份是 CLI 原文，不給遠端
    /// </summary>
    private RemoteTierFacts CollectRemoteFacts(string name, JObject command, ConversationState? c, AgentChatItem? card)
    {
        bool? preApproved = null;
        if (c != null)
            DetectConversationPreApproved(c.Meta, ref preApproved);
        else if (name == RemoteCommandCatalog.ConversationCreate && accounts.Find(command.Value<string>("accountId")) is { } account)
            DetectNewConversationPreApproved(account, command.Value<string>("folder"), ref preApproved);
        var folder = name switch
        {
            "setFolder" => command.Value<string>("path"),
            RemoteCommandCatalog.ConversationCreate => command.Value<string>("folder"),
            _ => null,
        };
        var listPath = name == "listFiles" ? command.Value<string>("path") : null;
        return new RemoteTierFacts(
            Mode: c != null && Enum.IsDefined(c.Meta.PermissionMode) ? c.Meta.PermissionMode : null,
            PreApproved: preApproved,
            Risk: card is { Kind: AgentItemKind.Approval } ? card.Risk : null,
            FolderKnown: folder != null ? RemoteFolderRules.IsKnown(folder, settings.RecentFolders) : null,
            FolderForbidden: null,
            PathInScope: listPath != null && c != null
                ? RemoteFolderRules.IsInScope(listPath, [GetWorkingDirectory(c), store.GetAttachmentDirectory(c.Meta.Id)])
                : null,
            TargetMode: TargetModeFor(name, command, c));
    }

    /// <summary>
    /// 既有對話的預先放行偵測（閘門 5d 與送出前的再查都經這裡，WP4.4 計畫 D27）：測試接縫有設就用它，否則交給登記點
    /// （WP4.4 Q1 選 A：一律 false；沒有實作時留 null）
    /// </summary>
    private void DetectConversationPreApproved(AgentConversation conversation, ref bool? preApproved)
    {
        if (ConversationPreApprovedForTest is { } detect)
        {
            preApproved = detect(conversation);
            return;
        }

        DetectPreApproved(conversation, ref preApproved);
    }

    /// <summary>新對話的預先放行偵測：測試接縫有設就用它，否則交給登記點（WP4.4 Q1 選 A：一律 false；沒有實作時留 null）</summary>
    private void DetectNewConversationPreApproved(AgentAccount account, string? workingDirectory, ref bool? preApproved)
    {
        if (AccountPreApprovedForTest is { } detect)
        {
            preApproved = detect(account, workingDirectory);
            return;
        }

        DetectAccountPreApproved(account, workingDirectory, ref preApproved);
    }

    /// <summary>
    /// setOption permission／conversationCreate 實際會設成的模式（電腦一定要填：不明時 RemoteTierRules 取 2＋FullAccess）：
    /// <c>default</c>／空字串照設定頁的預設（同本機的 SetOption），名稱照代理種類正規化；帳戶不明是 null。
    /// setAccount 是換帳戶之後的模式（規則同 SetAccount：同一家沿用、換到另一家是那一家設定頁的預設；同一個帳戶或帳戶不明是 null，閘門 5d 只在模式改變時升級）
    /// </summary>
    private AgentPermissionMode? TargetModeFor(string name, JObject command, ConversationState? c)
    {
        switch (name)
        {
            case "setAccount" when c != null:
                return accounts.Find(command.Value<string>("accountId")) is { } target && target.Id != c.Meta.AccountId
                    ? AgentPermissionModes.Normalize(target.Provider, target.Provider == c.Meta.Provider
                        ? c.Meta.PermissionMode
                        : ConversationDefaultsRules.PermissionFor(target.Provider, ProviderDefaults(target.Provider)))
                    : null;

            case "setOption" when c != null && command.Value<string>("key") == "permission":
            {
                var value = command.Value<string>("value");
                if (string.IsNullOrWhiteSpace(value) || value == "default")
                    return ConversationDefaultsRules.PermissionFor(c.Meta.Provider, ProviderDefaults(c.Meta.Provider));
                return ParseModeName(value) is { } mode ? AgentPermissionModes.Normalize(c.Meta.Provider, mode) : null;
            }

            case RemoteCommandCatalog.ConversationCreate:
                return accounts.Find(command.Value<string>("accountId")) is { } account && ParseModeName(command.Value<string>("permissionMode")) is { } created
                    ? AgentPermissionModes.Normalize(account.Provider, created)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>權限模式的名稱（大小寫有別、不收數字）</summary>
    private static AgentPermissionMode? ParseModeName(string? value)
        => value != null && Enum.GetNames<AgentPermissionMode>().Contains(value, StringComparer.Ordinal) ? Enum.Parse<AgentPermissionMode>(value) : null;

    // ───────────────────────────── 遠端來源的代理輸入（D13）─────────────────────────────

    /// <summary>
    /// 遠端來源的輸入現在可以送進代理：以現在的模式、預先放行規則與本機政策查（RemoteQueueRules.CanDequeue），而且有效的同步政策仍是 full、
    /// 不是副本（閘門 5c 的對話條件，gate/F7）。送出前（HoldRemoteSend）與執行中插進這一輪之前（SteerQueuedAsync）共用，兩條路的條件一致
    /// </summary>
    private bool RemoteInputAllowedNow(ConversationState c, RemoteOrigin origin)
    {
        bool? preApproved = null;
        DetectConversationPreApproved(c.Meta, ref preApproved);
        var mode = Enum.IsDefined(c.Meta.PermissionMode) ? c.Meta.PermissionMode : (AgentPermissionMode?)null;
        return RemoteQueueRules.CanDequeue(origin, mode, preApproved, SyncCoordinatorRules.HelloPolicy(settings.Sync))
               && EffectiveSyncPolicy(c) == ConversationSyncPolicies.Full && !c.IsReplica;
    }

    /// <summary>
    /// 送出前的最後一關（SendCoreAsync 開頭）：遠端來源的輸入以現在的模式、預先放行規則與本機政策再查一次（RemoteInputAllowedNow），
    /// 並重查閘門 5c 的對話條件（有效的同步政策仍是 full——伺服器刪除的旁檔算 localOnly——、不是副本：gate/F7）。不允許就放回佇列最前面（佇列暫停：只有本機按送出或下一輪正常結束才再試），
    /// 標成 Held（Id 是 "r"＋cid，放回幾次都不變；本機看過提示再按送出就當本機訊息送出），提示本機使用者——同一個指令只提示一次（gate/F3）。
    /// 留下的是正在執行的遠端 send 自己的訊息時，那個指令回 policy（WP4.4 計畫 D19、D20）；那則是這個指令自己帶來的文字（不是空送從佇列取出的）時
    /// 不放回佇列、不提示，文字由伺服器還給瀏覽器（rejected＝什麼都沒執行，合併前審查 AMO-02、F1），帶的網頁上傳的附件放回輸入區（WP4.7 合併前審查 INT-4）。
    /// 本機的輸入（origin 是 null）不查。回 true＝已留下或已丟棄
    /// </summary>
    private bool HoldRemoteSend(ConversationState c, string text, List<AgentAttachment> attachments, AgentChatItem? answerFor, string? displayText, RemoteOrigin? origin)
    {
        if (origin == null || RemoteInputAllowedNow(c, origin))
            return false;

        // 正在執行的遠端 send 自己帶來的文字：不留在這台。這個指令回 policy、伺服器把文字還給瀏覽器的輸入框——同一段輸入不會在這台的佇列
        // 與瀏覽器各留一份、之後送進代理兩次。空送從佇列取出的那則本來就在佇列，照舊放回（伺服器沒有文字可還）
        var ownText = IsOwnRemoteSendText(origin);
        if (ownText)
        {
            Log.Info($"對話 {c.Meta.Id} 的遠端訊息（{origin.CommandId}）送出前條件已不允許：不留在佇列，文字還給發起的瀏覽器");
            if (answerFor == null && attachments.Count > 0)
            {
                // 這次 send 帶的網頁上傳的附件（送出前已從輸入區取走）：放回輸入區最前面，仍算網頁上傳的，之後的遠端 send 帶走
                // （瀏覽器那一側沒有附件可還；WP4.7 合併前審查 INT-4）
                c.PendingAttachments.InsertRange(0, attachments.Where(a => !c.PendingAttachments.Contains(a)).ToList());
                c.RemoteUploadedAttachmentIds.UnionWith(attachments.Select(a => a.Id));
                PostComposer(c);
            }
        }
        else
        {
            Log.Info($"對話 {c.Meta.Id} 的遠端訊息（{origin.CommandId}）留在佇列：模式、預先放行規則、遠端操作設定、同步政策或副本狀態已不允許");
            c.Queue.Insert(0, new QueuedMessage { Text = text, Attachments = attachments, AnswerFor = answerFor, RemoteOrigin = origin, DisplayText = displayText, Held = true });
            var firstTime = c.HeldNotified.Add(origin.CommandId ?? string.Empty);
            if (firstTime && !c.IsDraft)
            {
                AddItem(c, new AgentChatItem
                {
                    Kind = AgentItemKind.Notice,
                    Text = "從遠端排進佇列的訊息暫停了：這個對話的權限模式或這台電腦的遠端操作設定已經改變。請確認後按送出，或從佇列取消。",
                });
                SaveConversation(c, saveItems: true);
            }

            PostComposer(c);
            MarkHeaderDirty(c);
            if (firstTime)
                Toast("從遠端排進佇列的訊息暫停了，請在這台電腦上確認", isError: true);
        }

        RemoteInputHeld(origin, kept: !ownText);
        return true;
    }

    /// <summary>
    /// CLI 更新中、佇列暫停時的空白送出（QueueSendDuringCliUpdate）：佇列第一則改成這次送出的來源再延後（WP4.4 計畫 D19、D20：遠端空送用那個指令的來源，
    /// 本機按送出被留下過的那則當成本機的訊息）。來源相同就是原本那則；換過的那則 Id 不變、不再是 Held（之後照新的來源重查）
    /// </summary>
    private static QueuedMessage WithSendOrigin(ConversationState c, QueuedMessage message, RemoteOrigin? origin)
    {
        if (Equals(message.RemoteOrigin, origin) || c.Queue.IndexOf(message) is not (>= 0 and var index))
            return message;
        var replaced = new QueuedMessage
        {
            Id = message.Id, Text = message.Text, Attachments = message.Attachments, AnswerFor = message.AnswerFor, RemoteOrigin = origin, DisplayText = message.DisplayText,
        };
        c.Queue[index] = replaced;
        return replaced;
    }

    // ───────────────────────────── 回應檢視 ─────────────────────────────

    /// <summary>
    /// 一個遠端指令的回應檢視（設計 4.9.3 的 RemoteResponseView）：只給中樞回給「這個檢視」的網頁訊息用（sendRejected、toast、files…），
    /// 不掛進視窗清單（不收廣播）、不是任何對話的檢視；收到的訊息經 <see cref="RemoteViewContext.Output"/> 照型錄的白名單轉出。
    /// HandleCommand 看到它時預設分支不轉給同步、例外不廣播 toast
    /// </summary>
    private sealed class RemoteResponseView(RemoteViewContext context) : IAgentView
    {
        public RemoteViewContext Context { get; } = context;

        public string Id { get; } = "remote:" + context.CommandId;

        public bool IsPopout => false;

        public void PostToWeb(JObject message) => Context.Output(message);
    }

    // ───────────────────────────── 測試接縫（同 RenderCliViewForTest 的前例）─────────────────────────────

    /// <summary>測試接縫：conversationCreate 的帳戶層級預先放行偵測（登記點 DetectAccountPreApproved 沒有實作時無法注入）</summary>
    internal Func<AgentAccount, string?, bool?>? AccountPreApprovedForTest { get; set; }

    /// <summary>
    /// 測試接縫：既有對話的預先放行偵測（WP4.4 計畫 D27；有設就取代登記點 DetectPreApproved）。步 6 起閘門 5d 與送出前的再查都經它；
    /// WP4.1 的閘門測試設成一律 null，斷言不因真的偵測而改變
    /// </summary>
    internal Func<AgentConversation, bool?>? ConversationPreApprovedForTest { get; set; }

    internal void SetRemoteAuthorizerForTest(IRemoteCommandAuthorizer authorizer) => remoteAuthorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));

    internal void AddRemoteHandlerForTest(IRemoteCommandHandler handler) => remoteHandlers.Add(handler ?? throw new ArgumentNullException(nameof(handler)));

    /// <summary>測試接縫：拿掉登記的處理者（WP4.1 的閘門測試與「沒有處理者」的回歸組照 WP4.1 的語意跑，WP4.4 計畫 D35）</summary>
    internal void ClearRemoteHandlersForTest() => remoteHandlers.Clear();

    /// <summary>測試接縫：登記的處理者（直接呼叫 TryHandle，驗「沒授權就拒絕」）</summary>
    internal IReadOnlyList<IRemoteCommandHandler> RemoteHandlersForTest => remoteHandlers.ToList();

    internal void MarkReplicaForTest(string conversationId, bool replica = true)
    {
        if (conversations.TryGetValue(conversationId, out var c))
            c.IsReplica = replica;
    }

    /// <summary>測試接縫：佇列每一則的 Id、文字、來源與是否被留下（WP4.4 計畫步 8）</summary>
    internal IReadOnlyList<(string Id, string Text, RemoteOrigin? Origin, bool Held)> QueueEntriesForTest(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) ? c.Queue.Select(q => (q.Id, q.Text, q.RemoteOrigin, q.Held)).ToList() : [];

    /// <summary>測試接縫：待送的拒絕說明（Codex、Grok 的附言，WP4.4 計畫 D22）</summary>
    internal IReadOnlyList<DeniedOperation> PendingFeedbackForTest(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) ? c.PendingFeedback.ToList() : [];

    /// <summary>排一則遠端來源的訊息到佇列最後（WP4.4 之前沒有其他路徑會產生 RemoteOrigin）</summary>
    internal void EnqueueRemoteForTest(string conversationId, string text, RemoteOrigin origin, string? displayText = null)
    {
        if (!conversations.TryGetValue(conversationId, out var c))
            throw new ArgumentException("沒有這個對話", nameof(conversationId));
        c.Queue.Add(new QueuedMessage { Text = text, RemoteOrigin = origin, DisplayText = displayText });
        PostComposer(c);
    }

    /// <summary>不經閘門、直接以回應檢視呼叫 HandleCommand（只測 HandleCommand 對回應檢視的三處處理）</summary>
    internal void HandleCommandAsRemoteForTest(RemoteViewContext context, JObject command, Action<JObject> output)
    {
        context.Begin(output, RemoteOutputWindow);
        if (command.Value<string>("cmd") is { } name)
            context.SetCommand(name);
        context.MarkAuthorized();
        HandleCommand(new RemoteResponseView(context), command);
    }
}
