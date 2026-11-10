using System.Diagnostics;
using System.Globalization;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 依擁有者 2026-10-19 指示（自用、伺服器可信任）一律放行（WP4.4 計畫 D13）：閘門 5a～5h 與信封是唯一的檢查。
/// 授權（GrantStore）、step-up、時間窗依指示不做（計畫 1.3）；處理者照樣要先看 <see cref="RemoteViewContext.Authorized"/>
/// </summary>
internal sealed class OwnerTrustedRemoteAuthorizer : IRemoteCommandAuthorizer
{
    public static OwnerTrustedRemoteAuthorizer Instance { get; } = new();

    private OwnerTrustedRemoteAuthorizer()
    {
    }

    public string? Authorize(RemoteViewContext context, string cmd, JObject command, RemoteTierFacts facts, RemoteTierResult tier) => null;
}

// 遠端指令的入口與處理者（WP4.4；擁有者：WP4.4）。中樞不碰同步引擎（S6）：同步協調者的 EnginePort 在 UI 執行緒呼叫
// ExecuteRemoteCommandAsync，閘門在 AgentHub.Remote.cs。處理者的共同規則：先看 context.Authorized（false 就 invalid）；
// 提示只給這個指令的回應檢視（不廣播）；每次 await 之後重新確認對話還在；遠端不寫這台的持久設定（D23）。
// 另有遠端動作的本機通知（D30）與副本的本機寫入攔截（D29）
internal sealed partial class AgentHub
{
    /// <summary>WP4.1 的登記點：換掉預設的 DenyAll（建構式呼叫，那時還看不到帳戶；授權者本身不看帳戶）</summary>
    partial void CreateRemoteAuthorizer(ref IRemoteCommandAuthorizer authorizer) => authorizer = OwnerTrustedRemoteAuthorizer.Instance;

    /// <summary>WP4.1 的登記點：對話指令的處理者（建構式呼叫）</summary>
    partial void RegisterConversationRemoteHandlers(List<IRemoteCommandHandler> into) => into.Add(new ConversationRemoteHandler(this));

    /// <summary>
    /// 擁有者 2026-10-19 回答 WP4.4 Q1 選 A（計畫 D27）：不偵測、不讀任何 CLI 設定檔（Claude 的 permissions.allow、Codex 的 rules 檔、
    /// 「本對話都允許」一律不算），只看對話的權限模式——完全自動才要「允許遠端完全自動」。推翻 2026-10-18 Q3-A 的後半
    /// </summary>
    partial void DetectPreApproved(AgentConversation conversation, ref bool? preApproved) => preApproved = false;

    /// <summary>新對話同 <see cref="DetectPreApproved"/>（WP4.4 Q1 選 A）：不看帳戶與資料夾的設定檔，一律 false</summary>
    partial void DetectAccountPreApproved(AgentAccount account, string? workingDirectory, ref bool? preApproved) => preApproved = false;

    /// <summary>遠端選資料夾的整體逾時（D18；測試可縮短）</summary>
    internal TimeSpan RemoteFolderTimeout { get; set; } = RemoteFolderProbe.DefaultTimeout;

    /// <summary>遠端選資料夾碰的檔案系統（測試接縫：模擬網路磁碟、卡住的磁碟）</summary>
    internal IRemoteFolderFileSystem RemoteFolderFileSystem { get; set; } = WindowsRemoteFolderFileSystem.Instance;

    /// <summary>
    /// 同步引擎送來的遠端指令（UI 執行緒；引擎已驗過信封、去重、先記 executing）：以信封的內容建 <see cref="RemoteViewContext"/>，
    /// 經閘門 5a～5j 執行。<paramref name="output"/> 收中樞回給這個指令的網頁訊息（照型錄的白名單、對話 id 是線上 id），在 UI 執行緒呼叫
    /// </summary>
    internal Task<RemoteOutcome> ExecuteRemoteCommandAsync(CommandEnvelopeBody body, Action<JObject> output)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(output);
        // 複本：閘門與處理者不改引擎手上的那份
        return ExecuteAndNotifyAsync(RemoteViewContext.FromEnvelope(body), (JObject)body.Cmd.DeepClone(), output);
    }

    /// <summary>經閘門執行，拿到結果之後在這台通知（D30；通知的錯誤只記錄，不影響結果）</summary>
    private async Task<RemoteOutcome> ExecuteAndNotifyAsync(RemoteViewContext context, JObject command, Action<JObject> output)
    {
        // 對象的名稱在執行之前取：刪除對話、移除帳戶之後就找不到了
        var subject = RemoteNotifySubject(context, command);
        var outcome = await ExecuteRemoteAsync(context, command, output);
        NotifyRemoteAction(context, outcome, subject);
        return outcome;
    }

    // ───────────────────────────── 遠端動作的本機通知（步 10、D30） ─────────────────────────────

    /// <summary>通知用的單調時鐘（毫秒；測試接縫，系統匣通知每分鐘最多一則）</summary>
    internal Func<long> RemoteNotifyClock { get; set; } = () => Environment.TickCount64;

    /// <summary>上一則遠端動作的系統匣通知（<see cref="RemoteNotifyClock"/> 的毫秒）。UI 執行緒</summary>
    private long? lastRemoteTrayAtMs;

    /// <summary>
    /// 通知裡的對象（UI 執行緒，閘門之前：指令還沒驗形狀，只取字串值）：對話的指令是對話標題，帶 accountId 的是帳戶名稱，
    /// 帶 logicalAccountId 的（accountLoginDevice）是目錄裡的帳戶名稱（經同步橋，WP4.8 D22），CLI 指令是代理種類；
    /// conversationCreate 在建立之後才知道（<see cref="NotifyRemoteAction"/> 另外取）
    /// </summary>
    private string? RemoteNotifySubject(RemoteViewContext context, JObject command)
    {
        if (context.ConversationId is { } wireId)
            return FindByWireId(wireId)?.Meta.Title;
        if (command["accountId"] is JValue { Type: JTokenType.String, Value: string accountId })
            return accounts.Find(accountId)?.Label;
        if (command["logicalAccountId"] is JValue { Type: JTokenType.String, Value: string logicalAccountId })
            return sync?.FindRemoteLoginTarget(null, logicalAccountId).Target?.Label;
        if (command["provider"] is JValue { Type: JTokenType.String, Value: string provider } && ParseProvider(provider) is { } parsed)
            return AccountRules.ProviderName(parsed);
        // 遠端執行指令（sync-protocol 14.11）：通知寫出指令的開頭
        if (context.CommandName == RemoteCommandCatalog.RemoteExec && command["command"] is JValue { Type: JTokenType.String, Value: string text })
            return RemoteExec.RemoteExecRunRules.Preview(text, RemoteNotifyRules.MaxSubjectLength);
        return null;
    }

    /// <summary>
    /// 遠端動作完成（done）之後在這台通知（D30，<see cref="RemoteNotifyRules"/>）：層級 1、2 而且「通知遠端操作」開著才通知；
    /// 對話正開在前景的視窗只對那些視窗提示一行，否則系統匣（每分鐘最多一則，點了開那個對話）。每個 done 的遠端動作都記一行紀錄
    /// </summary>
    private void NotifyRemoteAction(RemoteViewContext context, RemoteOutcome outcome, string? subject)
    {
        try
        {
            if (disposed || context.CommandName is not { } cmd || !string.Equals(outcome.State, RemoteResultStates.Done, StringComparison.Ordinal))
                return;
            ConversationState? c;
            if (cmd == RemoteCommandCatalog.ConversationCreate)
            {
                c = RemoteConversationIdRules.FromCommandId(context.CommandId) is { } createdId && conversations.TryGetValue(createdId, out var created) ? created : null;
                subject = c?.Meta.Title;
            }
            else
                c = context.ConversationId is { } wireId ? FindByWireId(wireId) : null;

            var foreground = c == null
                ? []
                : c.VisibleViewIds.Where(host.IsViewForeground).Select(id => views.TryGetValue(id, out var v) ? v : null).OfType<IAgentView>().ToList();
            var now = RemoteNotifyClock();
            var kind = RemoteNotifyRules.Decide(cmd, outcome, settings.Sync.NotifyRemoteActions, foreground.Count > 0, lastRemoteTrayAtMs, now);
            var text = RemoteNotifyRules.Describe(cmd, context.IssuerLabel, subject);
            Log.Info($"遠端操作 {context.CommandId}（層級 {outcome.Tier}，通知 {kind}）：{text}");
            switch (kind)
            {
                case RemoteNotifyKind.None:
                    break;
                case RemoteNotifyKind.ViewToast:
                    foreach (var view in foreground)
                        Toast(text, view: view);
                    break;
                case RemoteNotifyKind.Tray:
                    lastRemoteTrayAtMs = now;
                    host.Notify(RemoteNotifyRules.ActionTitle, text, c?.Meta.Id);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, "不認得的通知方式");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"遠端指令 {context.CommandId} 的本機通知失敗", ex);
        }
    }

    // ───────────────────────────── 副本的本機寫入攔截（步 10、D29） ─────────────────────────────

    /// <summary>副本被本機寫入時給那個檢視的提示</summary>
    internal const string ReplicaReadOnlyMessage = "這是另一台電腦的對話（唯讀）";

    /// <summary>
    /// 副本的指令被 WP4.3 的白名單擋下之後（HandleCommand 開頭的 <c>BlocksReplicaCommand</c>：只收看的指令，其他一律不處理，fail-closed）：
    /// 本機的檢視送來的是使用者的寫入（<see cref="ReplicaCommandRules"/>）時只給那個檢視一則「唯讀」的提示（D29）；自動送的（visible 之類）不提示。
    /// 遠端指令的回應檢視不提示：副本已由閘門 5c 拒絕
    /// </summary>
    private void NotifyReplicaReadOnly(IAgentView view, string? cmd)
    {
        if (view is RemoteResponseView || !ReplicaCommandRules.Blocks(cmd))
            return;
        Toast(ReplicaReadOnlyMessage, isError: true, view: view);
    }

    /// <summary>
    /// WP4.1 的登記點（HandleCommand 開頭、副本的白名單之後；WP4.4 持有唯一的實作）：本機網頁的指令交給 <see cref="InterceptCommandExtension"/>
    /// （第 4 階段沒有實作）。副本的寫入在這之前已被擋下（<see cref="NotifyReplicaReadOnly"/>）；
    /// 遠端指令的回應檢視（閘門的退路）不經這裡，遠端的擴充經 <see cref="IRemoteCommandHandler"/> 登記
    /// </summary>
    partial void InterceptCommand(IAgentView view, JObject command, ref bool handled)
    {
        if (view is RemoteResponseView)
            return;
        var cmd = ScalarText(command["cmd"]);
        InterceptCommandExtension(view, cmd, command, ref handled);
        if (!handled && InterceptCommandExtensionForTest is { } extension)
            handled = extension(view, cmd, command);
    }

    /// <summary>
    /// 第 5 階段以後的擴充點：本機網頁的指令在副本的白名單之後先分派到這裡；handled＝已處理，HandleCommand 的 switch 不再看。
    /// 第 4 階段沒有實作（WP4.6 的本機指令走 <c>HandleLocalViewCommand</c>，WP4.8 走 <see cref="IRemoteCommandHandler"/>）；
    /// partial 只能有一個實作，要用的工作包先在計畫裡說明。副本被擋下的指令不會到這裡；遠端指令的回應檢視不經這裡
    /// </summary>
    partial void InterceptCommandExtension(IAgentView view, string? cmd, JObject command, ref bool handled);

    /// <summary>測試接縫：在 <see cref="InterceptCommandExtension"/> 之後（還沒處理時）呼叫，回 true＝已處理</summary>
    internal Func<IAgentView, string?, JObject, bool>? InterceptCommandExtensionForTest { get; set; }

    /// <summary>指令欄位的字串值（同 HandleCommand 的 Value&lt;string&gt;：數字也當成字串；物件、陣列、null 回 null，不丟例外）</summary>
    private static string? ScalarText(JToken? token)
        => token is JValue { Value: { } value } ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    // ───────────────────────────── 對話指令的處理者 ─────────────────────────────

    /// <summary>
    /// 對話指令（D14）：setOption、setAccount、setFolder（步 6）、delete（步 7）、send、approve、approvePlan（步 8）、conversationCreate（步 9）、answer（步 11）；
    /// 不是這一包的回 false（交給下一個處理者或退路）。<paramref name="command"/> 的 conversationId 已經是本機 id（閘門 5j 換的）
    /// </summary>
    private sealed class ConversationRemoteHandler(AgentHub hub) : IRemoteCommandHandler
    {
        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            switch (cmd)
            {
                case "setOption":
                case "setAccount":
                case "setFolder":
                case "delete":
                case "send":
                case "voiceMessage":
                case "answer":
                case "approve":
                case "approvePlan":
                case RemoteCommandCatalog.ConversationCreate:
                    break;
                default:
                    return false;
            }

            if (!context.Authorized)
            {
                context.Reject(RemoteResultCodes.Invalid, "這個指令沒有經過這台電腦的遠端操作檢查", RemoteGateSteps.Handler);
                return true;
            }

            // 路由 Device：還沒有對話
            if (cmd == RemoteCommandCatalog.ConversationCreate)
            {
                _ = hub.CreateRemoteConversationAsync(context, view, command);
                return true;
            }

            if (command.Value<string>("conversationId") is not { } id || !hub.conversations.TryGetValue(id, out var c))
            {
                context.Reject(RemoteResultCodes.Conflict, "這個對話已不存在", RemoteGateSteps.Handler);
                return true;
            }

            switch (cmd)
            {
                case "setOption":
                    hub.RemoteSetOption(context, view, c, command);
                    break;
                case "setAccount":
                    hub.RemoteSetAccount(context, view, c, command);
                    break;
                case "setFolder":
                    _ = hub.RemoteSetFolderAsync(context, c, command.Value<string>("path"));
                    break;
                case "delete":
                    hub.RemoteDelete(context, c);
                    break;
                case "send":
                    _ = hub.RemoteSendAsync(context, view, c, command.Value<string>("text") ?? string.Empty);
                    break;
                case "voiceMessage":
                    hub.RemoteVoiceMessage(context, view, c, command);
                    break;
                case "answer":
                case "approve":
                case "approvePlan":
                    hub.RemoteRespondCard(context, c, cmd, command);
                    break;
            }

            return true;
        }
    }

    // ───────────────────────────── 送進代理的輸入（步 8、D19～D22） ─────────────────────────────

    /// <summary>
    /// 正在執行的遠端 send（cid → 它的 context，與這個指令有沒有自己帶來的內容：文字或網頁上傳的附件）：SendCoreAsync 建立使用者項目、
    /// HoldRemoteSend 留下這則時，以它回報這個指令的結果
    /// </summary>
    private readonly Dictionary<string, (RemoteViewContext Context, bool OwnText)> pendingRemoteInputs = new(StringComparer.Ordinal);

    /// <summary>
    /// send（D19）：經本機的 SendAsync（檢查不重寫，本機與遠端的送出不會分家），帶這個指令的來源（層級與 FullAccess 是 5d 的最終結果、模式是現在的）。
    /// 完成信號先到的為準：使用者項目建立（SendCoreAsync）→ done；RejectSend → conflict（sendRejected 已先轉出）；被 HoldRemoteSend 留下 → policy
    /// （帶文字的那則不留在這台，文字由伺服器還給瀏覽器；空送取出的那則放回佇列）；
    /// SendAsync 返回（排進佇列、CLI 更新中延後）→ 對話還在就 done、已被刪 conflict。空送而佇列是空的或正在執行 → conflict——
    /// 除非輸入區有網頁上傳的附件（WP4.7：只附檔、不打字的訊息，同本機的空送帶附件；合併前審查 INT-1）或還有上傳在處理（SendAsync 回「附件還在處理中」）
    /// </summary>
    /// <param name="displayText">語音訊息的顯示文字（AgentHub.Voice.cs）；null＝一般的 send</param>
    private async Task RemoteSendAsync(RemoteViewContext context, IAgentView view, ConversationState c, string text, string? displayText = null)
    {
        var registered = false;
        try
        {
            var uploaded = RemoteUploadedPending(c).Count > 0;
            if (text.Trim().Length == 0 && !uploaded && c.ImportingCount == 0 && (c.Running || c.Queue.Count == 0))
            {
                context.Reject(RemoteResultCodes.Conflict,
                    c.Running ? "代理執行中，沒有可以接著送的排隊訊息" : "佇列是空的，沒有可以接著送的訊息", RemoteGateSteps.Handler);
                return;
            }

            var origin = RemoteHandlerRules.Origin(context, Enum.IsDefined(c.Meta.PermissionMode) ? c.Meta.PermissionMode : null);
            // 這個指令自己帶來的內容（文字或網頁上傳的附件）：被 HoldRemoteSend 擋下時不留在這台（文字還給瀏覽器、附件放回輸入區）
            // 語音訊息不是使用者打的字（text 是給代理的說明）：被留下時留在這台的佇列，不把文字還給瀏覽器的輸入框
            pendingRemoteInputs[context.CommandId] = (context, OwnText: displayText == null && (text.Trim().Length > 0 || uploaded));
            registered = true;
            await SendAsync(view, c, text, origin, displayText);
            if (conversations.TryGetValue(c.Meta.Id, out var live) && ReferenceEquals(live, c))
                context.Complete();
            else
                context.Reject(RemoteResultCodes.Conflict, "這個對話在送出途中被刪除了", RemoteGateSteps.Handler);
        }
        catch (Exception ex)
        {
            Log.Error($"遠端指令 {context.CommandId} 送出訊息時出錯", ex);
            context.Fail("電腦處理這個指令時出錯，訊息可能已經送出；請看對話的內容確認", RemoteGateSteps.Handler);
        }
        finally
        {
            if (registered && pendingRemoteInputs.TryGetValue(context.CommandId, out var current) && ReferenceEquals(current.Context, context))
                pendingRemoteInputs.Remove(context.CommandId);
        }
    }

    /// <summary>SendCoreAsync 已建立使用者項目（D19）：正在執行的遠端 send 回 done（沒有來源、不是正在執行的那個指令就不做事）</summary>
    private void RemoteInputAccepted(RemoteOrigin? origin)
    {
        if (origin?.CommandId is { } cid && pendingRemoteInputs.TryGetValue(cid, out var pending))
            pending.Context.Complete();
    }

    /// <summary>
    /// 這是正在執行的遠端 send 自己帶來的文字或網頁上傳的附件（不是空送從佇列取出的那則）：HoldRemoteSend 不把它留在這台的佇列，文字由伺服器還給瀏覽器
    /// （合併前審查 AMO-02、F1），附件放回輸入區（WP4.7 合併前審查 INT-4）。
    /// 已經回過 done 之後才被留下的（排進佇列、CLI 更新中延後）不在這張表裡，照舊留在佇列
    /// </summary>
    private bool IsOwnRemoteSendText(RemoteOrigin origin)
        => origin.CommandId is { } cid && pendingRemoteInputs.TryGetValue(cid, out var pending) && pending.OwnText;

    /// <summary>
    /// HoldRemoteSend 擋下了正在執行的遠端 send 的訊息（D19、D20）：那個指令回 policy。<paramref name="kept"/>＝那則放回這台的佇列（空送取出的）；
    /// false＝這個指令自己的文字，沒有留在這台（伺服器把文字還給瀏覽器的輸入框）
    /// </summary>
    private void RemoteInputHeld(RemoteOrigin origin, bool kept)
    {
        if (origin.CommandId is { } cid && pendingRemoteInputs.TryGetValue(cid, out var pending))
            pending.Context.Reject(RemoteResultCodes.Policy,
                kept
                    ? "這則訊息留在那台電腦的佇列：對話的權限模式或那台的遠端操作設定已經改變，要在那台電腦上確認後才會送出"
                    : "訊息沒有送出：對話的權限模式、那台的遠端操作設定或同步設定已經改變",
                RemoteGateSteps.Handler);
    }

    /// <summary>
    /// approve、approvePlan、answer（D21）：卡片在 5h 已確認在等待、摘要相符。呼叫本機的 ApproveAsync／ApprovePlanAsync／AnswerAsync（不等回應 CLI），
    /// 同步的前段結束後：同一張卡、不再等待、而且 Decision 是這次要設的值 → done；否則 stale（被作廢、或在這之前已在別處處理）。
    /// 回應 CLI 失敗的提示照本機（那時遠端已收到 done＝中樞已接受）。approve 拒絕的附言、answer 當成訊息送出的回答帶這個指令的來源（D22、D21）；
    /// answer 的題目 id 經過權杖遮蔽，先以 <see cref="RemoteCardRules.TryMapAnswers"/> 對回原題，沒有任何答案 → invalid
    /// </summary>
    private void RemoteRespondCard(RemoteViewContext context, ConversationState c, string cmd, JObject command)
    {
        var itemId = command.Value<string>("itemId");
        var choice = command.Value<string>("choice");
        var message = command.Value<string>("message");
        if (FindItem(c, itemId) is not { Status: AgentItemStatus.Waiting } item)
        {
            context.Reject(RemoteResultCodes.Stale, "這張卡已在其他地方處理、內容已經改變，或 CLI 已重新啟動", RemoteGateSteps.Handler);
            return;
        }

        JObject? answers = null;
        JObject? notes = null;
        if (cmd == "answer")
        {
            if (!RemoteCardRules.TryMapAnswers(item, command["answers"] as JObject, out var mapped)
                || !RemoteCardRules.TryMapNotes(item, command["notes"] as JObject, out notes))
            {
                context.Reject(RemoteResultCodes.Invalid, "沒有任何答案，或答案對不回問卷的題目", RemoteGateSteps.Handler);
                return;
            }

            answers = RemoteHandlerRules.AnswersJson(mapped);
        }

        var origin = RemoteHandlerRules.Origin(context, Enum.IsDefined(c.Meta.PermissionMode) ? c.Meta.PermissionMode : null);
        // 誰回應的（多人協作）：協作人的名稱、擁有者是 null；回應沒成功（卡片還在等）時放回原本的
        var resolvedBefore = item.ResolvedBy;
        item.ResolvedBy = context.Collaborator;
        var task = cmd switch
        {
            "approve" => ApproveAsync(c, itemId, choice, message, origin),
            "approvePlan" => ApprovePlanAsync(c, itemId, choice, message),
            "answer" => AnswerAsync(c, itemId, answers, origin, notes),
            _ => throw new ArgumentOutOfRangeException(nameof(cmd), cmd, "不是卡片指令"),
        };
        if (task.IsFaulted)
        {
            Log.Error($"遠端指令 {context.CommandId} 回應卡片時出錯", task.Exception?.GetBaseException());
            context.Fail("電腦處理這個指令時出錯，卡片可能已經回應；請看對話的內容確認", RemoteGateSteps.Handler);
            return;
        }

        if (item.Status == AgentItemStatus.Waiting)
            item.ResolvedBy = resolvedBefore;
        if (item.Status != AgentItemStatus.Waiting && string.Equals(item.Decision, RemoteHandlerRules.CardDecision(cmd, choice), StringComparison.Ordinal)
            && ReferenceEquals(FindItem(c, itemId), item))
            context.Complete();
        else
            context.Reject(RemoteResultCodes.Stale, "這張卡已在其他地方處理、內容已經改變，或 CLI 已重新啟動", RemoteGateSteps.Handler);
        _ = task.ContinueWith(t => Log.Error($"遠端指令 {context.CommandId} 回應卡片之後出錯", t.Exception?.GetBaseException()),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>setOption（D23；型錄的 ConversationOnly）：只改這個對話，不把模型記成帳戶預設、不存設定；permission 的目標模式已在 5d／5e 判過</summary>
    private void RemoteSetOption(RemoteViewContext context, IAgentView view, ConversationState c, JObject command)
    {
        SetOption(view, c, command.Value<string>("key"), command.Value<string>("value"), persistAccountDefaults: false);
        context.Complete();
    }

    /// <summary>
    /// setAccount（D17、D23）：帳戶已在 5c 確認是這台的本機帳戶。同一個帳戶直接 done（執行中選了別的帳戶還沒換過去時同本機取消）；
    /// 執行中也能換（先停下這一輪、結束時換過去並從中斷處接著做，或 when 是 turnEnd 時等這一輪結束才換，同本機；AccountSwitchRules.WhileRunningBlock 擋的 conflict，目前四種都能換）；換到不進同步的代理種類
    /// policy——對話會變成只在這台、伺服器端刪除而且不可恢復（2026-10-22 起四種都同步，目前沒有）；不改「上次使用的帳戶」。
    /// 只能換成這台已登入的帳戶（2026-10-25 使用者要求）：沒登入、沒安裝 CLI 的 conflict（網頁只列那台登入的，畫面可能是舊的，這裡再擋一次）
    /// </summary>
    private void RemoteSetAccount(RemoteViewContext context, IAgentView view, ConversationState c, JObject command)
    {
        if (accounts.Find(command.Value<string>("accountId")) is not { } account)
        {
            context.Reject(RemoteResultCodes.Invalid, "這台電腦沒有這個帳戶", RemoteGateSteps.Handler);
            return;
        }

        if (c.Meta.AccountId == account.Id)
        {
            // 執行中選了別的帳戶、還沒換過去：同本機取消（沒有就什麼都不做）
            SetAccount(view, c, account.Id, rememberAccount: false);
            context.Complete();
            return;
        }

        var status = accounts.GetState(account.Id).Status;
        if (status is AccountStatus.NotLoggedIn or AccountStatus.NotInstalled)
        {
            context.Reject(RemoteResultCodes.Conflict, $"這台電腦的「{account.Label}」{AccountRules.DescribeStatus(account.Provider, status)}，不能換過去",
                RemoteGateSteps.Handler);
            return;
        }

        if (c.Running && AccountSwitchRules.WhileRunningBlock(c.Meta.Provider, account.Provider) is { } block)
        {
            context.Reject(RemoteResultCodes.Conflict, block, RemoteGateSteps.Handler);
            return;
        }

        if (!SyncProviderRules.TryWireName(account.Provider, out _))
        {
            context.Reject(RemoteResultCodes.Policy,
                $"{AccountRules.ProviderName(account.Provider)} 的對話只留在這台電腦，換過去這個對話會離開同步，不能從遠端換", RemoteGateSteps.Handler);
            return;
        }

        // 閒置、還有背景工作在跑時：遠端看不到這台的背景工作、不會帶 when now，等背景工作結束才換（對話裡有提示）
        var when = command.Value<string>("when");
        SetAccount(view, c, account.Id, rememberAccount: false, atTurnEnd: AccountSwitchRules.IsAtTurnEnd(when),
            explicitNow: AccountSwitchRules.IsExplicitNow(when));
        context.Complete();
    }

    /// <summary>
    /// setFolder（D18、D23；型錄的 NoRememberFolder）：執行中 conflict；資料夾在背景檢查（<see cref="RemoteFolderProbe"/>），
    /// 通過、對話還在而且仍沒在執行才套用；不進最近資料夾、不存設定
    /// </summary>
    private async Task RemoteSetFolderAsync(RemoteViewContext context, ConversationState c, string? path)
    {
        try
        {
            if (c.Running)
            {
                context.Reject(RemoteResultCodes.Conflict, "代理執行中不能更換工作資料夾，請先停止", RemoteGateSteps.Handler);
                return;
            }

            var check = await RemoteFolderProbe.CheckAsync(path, BuildFolderContext(c), RemoteFolderTimeout, RemoteFolderFileSystem);
            Log.Info($"遠端指令 {context.CommandId} 選的資料夾 {path}：{check.Verdict}{(check.Detail is { } detail ? "（" + detail + "）" : "")}");
            switch (check.Verdict)
            {
                case RemoteFolderVerdict.Ok:
                    if (disposed)
                        context.Reject(RemoteResultCodes.Busy, "電腦的程式正在關閉", RemoteGateSteps.Handler);
                    else if (!conversations.TryGetValue(c.Meta.Id, out var current) || !ReferenceEquals(current, c))
                        context.Reject(RemoteResultCodes.Conflict, "這個對話已不存在", RemoteGateSteps.Handler);
                    else if (c.Running)
                        context.Reject(RemoteResultCodes.Conflict, "代理執行中不能更換工作資料夾，請先停止", RemoteGateSteps.Handler);
                    else
                    {
                        ApplyFolder(c, check.Path);
                        context.Complete();
                    }

                    return;
                case RemoteFolderVerdict.Invalid:
                    context.Reject(RemoteResultCodes.Invalid, check.Detail ?? "這個資料夾不能從遠端使用", RemoteGateSteps.Handler);
                    return;
                case RemoteFolderVerdict.Policy:
                    context.Reject(RemoteResultCodes.Policy, check.Detail ?? "這個資料夾不能從遠端選", RemoteGateSteps.Handler);
                    return;
                case RemoteFolderVerdict.Missing:
                case RemoteFolderVerdict.Timeout:
                    context.Reject(RemoteResultCodes.Conflict, check.Detail ?? "資料夾無法存取", RemoteGateSteps.Handler);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(check), check.Verdict, "不認得的資料夾檢查結果");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"遠端指令 {context.CommandId} 換工作資料夾時出錯", ex);
            context.Fail("電腦處理這個指令時出錯，指令可能已經執行；請看對話的內容確認", RemoteGateSteps.Handler);
        }
    }

    // ───────────────────────────── 遠端開新對話（步 9、D26） ─────────────────────────────

    /// <summary>
    /// WP4.7：conversationCreate 的 uploads（建立之後、存成正式對話之前，在 UI 執行緒呼叫；<paramref name="conversation"/> 還是只在記憶體的草稿）。
    /// 接手了就把 <paramref name="prepared"/> 設成 true；沒有實作時留 false，處理者回 unsupported、什麼都不建
    /// </summary>
    partial void PrepareRemoteUploads(ConversationState conversation, JArray uploads, RemoteViewContext context, ref bool prepared);

    /// <summary>
    /// conversationCreate（D26）：對話 id 由 cid 推得（<see cref="RemoteConversationIdRules"/>；結果不帶 id，伺服器以同一條規則推算）。
    /// <b>rejected 一定表示什麼都沒建</b>：id 撞到清單、回收區、還沒搬完的遠端刪除或已有的資料夾 → conflict；資料夾有給就在背景檢查（D18），
    /// 沒給是對話自己的 workspace，都不記成最近資料夾；帳戶未登入、未安裝 → conflict（照 SendAsync 的訊息；CLI 正在更新時不看，送出會延後）。
    /// 都通過才經 restoring 的路徑建立（沿用 id、不取最近資料夾、不寫上次使用的帳戶），立刻把遠端檢視從 ViewIds 拿掉、存成正式對話、更新清單，回 done；
    /// 有文字再以這個指令的來源送出（模式是新對話的），不等它的完成信號——之後被拒或被 HoldRemoteSend 留下，照本機的規則留在那個對話
    /// </summary>
    private async Task CreateRemoteConversationAsync(RemoteViewContext context, IAgentView view, JObject command)
    {
        ConversationState? created = null;
        try
        {
            if (RemoteHandlerRules.ParseCreate(command) is not { } request)
            {
                context.Reject(RemoteResultCodes.Invalid, "開新對話的指令內容不對", RemoteGateSteps.Handler);
                return;
            }

            var id = RemoteConversationIdRules.FromCommandId(context.CommandId);
            if (CheckRemoteCreate(id, request.AccountId) is { } reject)
            {
                context.Reject(reject.Code, reject.Message, RemoteGateSteps.Handler);
                return;
            }

            string? folder = null;
            if (request.Folder != null)
            {
                var check = await RemoteFolderProbe.CheckAsync(request.Folder, BuildFolderContext(null), RemoteFolderTimeout, RemoteFolderFileSystem);
                Log.Info($"遠端指令 {context.CommandId} 開新對話選的資料夾 {request.Folder}：{check.Verdict}{(check.Detail is { } detail ? "（" + detail + "）" : "")}");
                if (RemoteFolderReject(check) is { } folderReject)
                {
                    context.Reject(folderReject.Code, folderReject.Message, RemoteGateSteps.Handler);
                    return;
                }

                // 檢查期間：程式關閉、同一個 id 已被建立、帳戶被移除
                if (disposed)
                {
                    context.Reject(RemoteResultCodes.Busy, "電腦的程式正在關閉", RemoteGateSteps.Handler);
                    return;
                }

                if (CheckRemoteCreate(id, request.AccountId) is { } lateReject)
                {
                    context.Reject(lateReject.Code, lateReject.Message, RemoteGateSteps.Handler);
                    return;
                }

                folder = check.Path;
            }

            var account = accounts.Find(request.AccountId)!;
            var status = accounts.GetState(account.Id).Status;
            if (!IsCliUpdateBlocking(account.Provider) && status is AccountStatus.NotLoggedIn or AccountStatus.NotInstalled)
            {
                context.Reject(RemoteResultCodes.Conflict, $"「{account.Label}」{AccountRules.DescribeStatus(account.Provider, status)}", RemoteGateSteps.Handler);
                return;
            }

            // CreateDraft 惰性補上的空帳戶預設不留：有文字時送出會存設定，遠端不寫這台的持久設定（D23）
            var hadAccountDefaults = settings.AccountDefaults.ContainsKey(account.Id);
            created = CreateDraft(view, account, new ResumeNewConversation(id!, account.Id, folder, string.Empty, []));
            created.ViewIds.Remove(view.Id);
            if (!hadAccountDefaults)
                settings.AccountDefaults.Remove(account.Id);
            if (request.Uploads is { } uploads)
            {
                var prepared = false;
                PrepareRemoteUploads(created, uploads, context, ref prepared);
                if (!prepared)
                {
                    RemoveUnsavedRemoteCreate(created);
                    created = null;
                    context.Reject(RemoteResultCodes.Unsupported, "這台電腦的程式還不支援從遠端附加檔案", RemoteGateSteps.Handler);
                    return;
                }
            }

            created.Meta.PermissionMode = AgentPermissionModes.Normalize(account.Provider, request.PermissionMode);
            if (request.Model != null)
                SetOption(view, created, "model", request.Model, persistAccountDefaults: false);
            if (request.Effort != null)
                SetOption(view, created, "effort", request.Effort, persistAccountDefaults: false);
            var now = DateTime.Now;
            created.IsDraft = false;
            created.Meta.Title = RemoteHandlerRules.CreateTitle(request.Title, request.Text);
            // 臨時對話（瀏覽器版右上角的對話鈕，AgentHub.QuickChat.cs）：不列在清單、瀏覽器關掉面板（delete）就刪掉；內容照常同步給那個瀏覽器
            if (command["ephemeral"] is JValue { Type: JTokenType.Boolean } ephemeral && (bool)ephemeral)
            {
                created.Meta.Ephemeral = true;
                created.Meta.WorkingDirectory = null;
            }
            created.Meta.CreatedAt = now;
            created.Meta.UpdatedAt = now;
            created.LastActivity = now;
            SaveConversation(created, saveItems: true);
            MarkListDirty();
            MarkAccountsDirty();
            var conversation = created;
            created = null;
            context.Complete();
            if (request.Text is { } text)
            {
                var origin = RemoteHandlerRules.Origin(context, conversation.Meta.PermissionMode);
                var commandId = context.CommandId;
                // 帶上傳時等下載與匯入完成再送，附件和第一則一起送出（WP4.7 計畫步 13，AgentHub.RemoteFiles.cs）
                if (!DeferUntilRemoteUploads(conversation, () => _ = SendRemoteCreateTextAsync(conversation, text, origin, commandId)))
                    _ = SendRemoteCreateTextAsync(conversation, text, origin, commandId);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"遠端指令 {context.CommandId} 開新對話時出錯", ex);
            if (created != null)
            {
                // 還沒存成正式對話：拿掉，rejected／unknown 時不留下半成品
                RemoveUnsavedRemoteCreate(created);
            }

            context.Fail("電腦處理這個指令時出錯，對話可能已經建立；請看對話清單確認", RemoteGateSteps.Handler);
        }
    }

    /// <summary>
    /// conversationCreate 建立之前的條件（D26；檢查資料夾前後各一次）：id 推不出來 invalid；已在清單、還沒搬完的遠端刪除、是副本（步 11）、在回收區、
    /// 對話資料夾已存在 → conflict；帳戶不存在 invalid（5c 已擋，防禦用）；代理種類不進同步 policy（同 5c）。通過回 null
    /// </summary>
    private (string Code, string Message)? CheckRemoteCreate(string? id, string accountId)
    {
        if (id == null)
            return (RemoteResultCodes.Invalid, "指令 id 不合格，推不出新對話的 id");
        if (conversations.ContainsKey(id) || FindByWireId(id) != null || IsTrashPending(id))
            return (RemoteResultCodes.Conflict, "這個對話已經在這台電腦上");
        // 其他電腦的對話在這台的副本（WP4.3；清單裡已拿掉的副本，儲存端仍記得它的 id）
        if (store.IsReplica(id))
            return (RemoteResultCodes.Conflict, "這個 id 是另一台電腦的對話");
        if (Trash.Contains(id))
            return (RemoteResultCodes.Conflict, "這個對話在這台電腦的回收區");
        if (Directory.Exists(store.GetConversationDirectory(id)))
            return (RemoteResultCodes.Conflict, "這台電腦已有同一個 id 的對話資料夾");
        if (accounts.Find(accountId) is not { } account)
            return (RemoteResultCodes.Invalid, "這台電腦沒有這個帳戶");
        if (!SyncProviderRules.TryWireName(account.Provider, out _))
            return (RemoteResultCodes.Policy, "這種代理的對話只留在這台電腦，不能從遠端建立");
        return null;
    }

    /// <summary>資料夾探針的結果 → 拒絕（同 setFolder）；Ok 回 null</summary>
    private static (string Code, string Message)? RemoteFolderReject(RemoteFolderCheck check) => check.Verdict switch
    {
        RemoteFolderVerdict.Ok => null,
        RemoteFolderVerdict.Invalid => (RemoteResultCodes.Invalid, check.Detail ?? "這個資料夾不能從遠端使用"),
        RemoteFolderVerdict.Policy => (RemoteResultCodes.Policy, check.Detail ?? "這個資料夾不能從遠端選"),
        RemoteFolderVerdict.Missing or RemoteFolderVerdict.Timeout => (RemoteResultCodes.Conflict, check.Detail ?? "資料夾無法存取"),
        _ => throw new ArgumentOutOfRangeException(nameof(check), check.Verdict, "不認得的資料夾檢查結果"),
    };

    /// <summary>還沒存成正式對話的遠端新對話：從清單拿掉（只在記憶體，沒有資料夾要刪；附件的資料夾由 WP4.7 的登記點自己收）</summary>
    private void RemoveUnsavedRemoteCreate(ConversationState c)
    {
        if (conversations.TryGetValue(c.Meta.Id, out var live) && ReferenceEquals(live, c))
            conversations.Remove(c.Meta.Id);
        c.Session?.Dispose();
        c.Session = null;
    }

    /// <summary>新對話的第一則（D26）：結果已是 done，這裡的錯誤只記錄；提示與被拒的文字轉給這台的視窗（<see cref="RemoteCreateEchoView"/>）</summary>
    private async Task SendRemoteCreateTextAsync(ConversationState c, string text, RemoteOrigin origin, string commandId)
    {
        try
        {
            await SendAsync(new RemoteCreateEchoView(this, commandId), c, text, origin);
        }
        catch (Exception ex)
        {
            Log.Error($"遠端指令 {commandId} 開新對話之後送出第一則時出錯", ex);
        }
    }

    /// <summary>
    /// 遠端新對話送出第一則用的檢視（D26）：結果已是 done，之後的輸出不再轉給瀏覽器（不輸出 sendRejected，伺服器被拒時才自己還原文字）；
    /// toast 與 sendRejected 轉給這台的所有視窗（照本機送出的規則：被拒的文字回到開著這個對話的輸入區，沒開的視窗不理會）。
    /// 不掛進視窗清單、不是任何對話的檢視、不是 <see cref="RemoteResponseView"/>（RejectSend 不改那個指令的結果）
    /// </summary>
    private sealed class RemoteCreateEchoView(AgentHub hub, string commandId) : IAgentView
    {
        public string Id { get; } = "remote:" + commandId + ":create";

        public bool IsPopout => false;

        public void PostToWeb(JObject message)
        {
            if (message.Value<string>("type") is "toast" or "sendRejected")
                hub.PostToAll(message);
        }
    }

    /// <summary>測試接縫：這個對話登記的檢視 id（遠端新對話建立之後不留遠端檢視，D26）</summary>
    internal IReadOnlyList<string> ViewIdsForTest(string conversationId)
        => conversations.TryGetValue(conversationId, out var c) ? c.ViewIds.ToList() : [];

    // ───────────────────────────── 遠端刪除 → 回收區（步 7、D24） ─────────────────────────────

    private ConversationTrash? trash;

    /// <summary>建構時還沒搬完（仍在 pending）的對話：載入時略過，不出現在清單（建構式寫，之後只讀）</summary>
    private IReadOnlySet<string> pendingTrash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>回收區（資料根目錄的 <c>conversations-trash</c>，由 <see cref="ConversationStore.RootDirectory"/> 的上層推得，D24）</summary>
    internal ConversationTrash Trash
        => trash ??= new ConversationTrash(ConversationTrash.RootFor(store.RootDirectory), store.RootDirectory, TimeProvider.System);

    /// <summary>搬移失敗時多久重試一次（D24；測試可縮短）</summary>
    internal TimeSpan TrashMoveRetryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>搬移失敗時最多重試多久，之後留在 pending、下次建構中樞時再搬（D24；測試可縮短）</summary>
    internal TimeSpan TrashMoveRetryLimit { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// delete（D24、SEC-27）：對話在 5c 已確認；記下標題，從清單移除（照本機刪除的流程：先停程序、刪除的掛鉤在移除之前），
    /// 資料夾在背景搬進回收區（不等），內容不變、30 天後才清掉。不是 app 建立的資料夾名稱的對話不刪（回收區只收那種名稱）
    /// </summary>
    private void RemoteDelete(RemoteViewContext context, ConversationState c)
    {
        // 臨時對話：瀏覽器關掉面板＝刪掉，不進回收區、連 CLI 的逐字稿一起刪（AgentHub.QuickChat.cs）
        if (QuickChatRules.IsEphemeral(c.Meta))
        {
            EndQuickChat(c);
            context.Complete();
            return;
        }
        if (!ConversationTrashRules.IsTrashableId(c.Meta.Id))
        {
            context.Reject(RemoteResultCodes.Conflict, "這個對話不能從遠端刪除，請在電腦上刪除", RemoteGateSteps.Handler);
            return;
        }

        Delete(c, toTrash: true, new TrashInfo(Trash.NowMs(), context.CommandId, context.IssuerLabel, c.Meta.Title));
        context.Complete();
    }

    /// <summary>
    /// 建構中樞時、載入對話之前（D24）：完成上次沒搬完的（仍搬不動的記在 <see cref="pendingTrash"/>，載入時略過），
    /// 並在背景清掉超過 30 天的。都不丟例外：回收區出問題不能讓 app 起不來
    /// </summary>
    private void CompletePendingTrashMoves()
    {
        try
        {
            pendingTrash = Trash.CompletePendingMoves();
            if (pendingTrash.Count > 0)
                Log.Warn($"回收區：{pendingTrash.Count} 個遠端刪除的對話仍搬不動，這次不出現在清單：{string.Join("、", pendingTrash)}");
        }
        catch (Exception ex)
        {
            Log.Error("完成回收區的搬移時出錯", ex);
        }

        var purging = Trash;
        _ = Task.Run(() =>
        {
            try
            {
                purging.Purge();
            }
            catch (Exception ex)
            {
                Log.Error("清理回收區時出錯", ex);
            }
        });
    }

    /// <summary>建構時仍沒搬完的遠端刪除（載入迴圈略過）</summary>
    private bool IsTrashPending(string conversationId) => pendingTrash.Contains(conversationId);

    /// <summary>
    /// 從回收區還原（WP4.6 的回收區清單與還原鈕；計畫步 11、D25；UI 執行緒）。清單裡已有同一個 id（大小寫不分）、是其他電腦的副本、
    /// 還沒搬完的遠端刪除、對話資料夾已存在、回收區沒有它 → false，什麼檔案都不動。否則搬回 <c>conversations\</c>（回收區先確認有伺服器刪除的旁檔：
    /// 還原的對話只在這台電腦、發布者不發布），讀出那一筆加回清單，回 true。讀不到（meta.json 損毀又重建不了）時搬回回收區、回 false
    /// </summary>
    internal bool RestoreFromTrash(string conversationId)
    {
        if (disposed || !ConversationTrashRules.IsTrashableId(conversationId))
            return false;
        if (conversations.Keys.Any(id => string.Equals(id, conversationId, StringComparison.OrdinalIgnoreCase)) || store.IsReplica(conversationId)
            || IsTrashPending(conversationId) || Directory.Exists(store.GetConversationDirectory(conversationId)) || !Trash.Contains(conversationId))
        {
            Log.Info($"對話 {conversationId} 不從回收區還原：清單裡已有、是副本、還沒搬完、資料夾已存在或回收區沒有它");
            return false;
        }

        var info = Trash.InfoOf(conversationId);
        if (!Trash.TryRestore(conversationId))
            return false;

        // 同建構中樞時的載入（舊版剝掉的同步政策從旁檔補回、損毀的 meta.json 從紀錄重建）
        var meta = store.LoadConversations().FirstOrDefault(m => string.Equals(m.Id, conversationId, StringComparison.Ordinal));
        if (meta == null)
        {
            Log.Warn($"對話 {conversationId} 搬回之後讀不到：放回回收區");
            if (!Trash.TryMove(conversationId, info ?? new TrashInfo(Trash.NowMs(), null, null, null)))
                Log.Warn($"對話 {conversationId} 放不回回收區，留在對話資料夾（有伺服器刪除的旁檔，不會發布）");
            return false;
        }

        var state = new ConversationState(meta);
        conversations[meta.Id] = state;
        RestoreQueue(state);
        // 執行中被刪、當時沒拿掉的 running.json（刪不掉、或 app 結束途中刪的）：還原的對話不是上次中斷的，下次啟動不能自動接續
        state.RunMarkerOnDisk = true;
        DeleteRunMarker(state);
        MarkListDirty();
        MarkAccountsDirty();
        Log.Info($"對話 {conversationId} 從回收區還原（只在這台電腦，不重新發布）");
        return true;
    }

    /// <summary>
    /// 在背景把對話資料夾搬進回收區（D24）：先記 pending（這裡同步寫：結果回 done 之前就落盤，搬到一半結束時下次啟動照樣完成），
    /// 每 <see cref="TrashMoveRetryInterval"/> 重試、最多 <see cref="TrashMoveRetryLimit"/>；搬好就拿掉 pending，搬不動就提示、留到下次啟動
    /// </summary>
    private void MoveToTrashInBackground(string conversationId, TrashInfo info)
    {
        // 背景還沒寫的紀錄作廢、正在寫的等它寫完（ItemsFileWriter 不會重建資料夾，這裡只是不跟搬動搶檔案）
        itemsWriter?.Forget(conversationId);
        mediaKeeper?.Forget(conversationId);
        var target = Trash;
        var interval = TrashMoveRetryInterval;
        var limit = TrashMoveRetryLimit;
        target.RecordPending(conversationId, info);
        _ = Task.Run(async () =>
        {
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                if (target.TryMove(conversationId, info))
                {
                    target.ClearPending(conversationId);
                    return true;
                }

                if (Stopwatch.GetElapsedTime(started) >= limit)
                    return false;
                await Task.Delay(interval);
            }
        }).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion && t.Result)
                return;
            Log.Warn($"對話 {conversationId} 移到回收區失敗（{t.Exception?.GetBaseException().Message ?? "檔案仍被占用"}），留到下次啟動");
            host.BeginInvoke(() =>
            {
                if (!disposed)
                    Toast("遠端刪除的對話有檔案仍被其他程式開啟，暫時移不到回收區；下次啟動時移到回收區（在那之前不會出現在清單）");
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 禁止清單用的資料夾（D18；UI 執行緒，只組原始路徑，展開在探針的背景工作裡）：使用者資料夾、%LOCALAPPDATA%、%APPDATA%、
    /// 資料根目錄（<see cref="ConversationStore.RootDirectory"/> 的上層，<c>--data</c> 時是那個資料夾）、各帳戶的設定資料夾；
    /// 有對話時加上它的資料夾與可以給遠端的 workspace、attachments
    /// </summary>
    private RemoteFolderContext BuildFolderContext(ConversationState? c)
    {
        var conversationsRoot = Path.TrimEndingDirectorySeparator(store.RootDirectory);
        var configDirectories = new List<string>();
        foreach (var account in accounts.Accounts)
        {
            try
            {
                configDirectories.Add(AccountRules.ResolveConfigDirectory(account));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // 解析不了的帳戶：放一個正規化不了的值，禁止清單不完整，探針一律不收（fail-closed）
                Log.Warn($"遠端選資料夾：帳戶 {account.Id} 的設定資料夾解析不了（{ex.Message}）");
                configDirectories.Add("?");
            }
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new RemoteFolderContext(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            string.IsNullOrEmpty(roaming) ? null : roaming,
            Path.GetDirectoryName(conversationsRoot) ?? conversationsRoot,
            configDirectories,
            c == null ? null : [store.GetDefaultWorkingDirectory(c.Meta.Id), store.GetAttachmentDirectory(c.Meta.Id)],
            c == null ? null : store.GetConversationDirectory(c.Meta.Id));
    }
}
