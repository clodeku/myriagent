using System.Runtime.CompilerServices;
using Myriagent.Core;
using Myriagent.Remote;

namespace Myriagent.Sync;

// 第 4 階段的登記點（WP4.1 步 10；設計 13.0、14.11，計畫 5.10、D45、D49）：
// 後續的工作包在自己的 partial 檔（SyncEngine.Conversations.cs〔WP4.3〕、SyncEngine.Commands.cs〔WP4.4〕、SyncEngine.Media.cs〔WP4.7〕）
// 實作這裡宣告的 partial 方法，不改引擎本體。沒有實作時編譯器連呼叫與引數運算一起拿掉：Hello、拉取、心跳的 JSON 與第 3 階段逐位元組相同，
// 第 4 階段的伺服器訊息一律略過（沒協商功能）。所有登記點都在引擎執行緒上呼叫（狀態只在引擎執行緒改、不加鎖）；
// 要改中樞一律經 ISyncHostPort.RunOnUiAsync（IAgentHost.BeginInvoke）。實作丟出的例外照呼叫點的處理（連線上的工作是斷線退避）。
// 有回傳值的登記寫成「傳入清單」或 ref 參數（有回傳值的 partial 必須有實作，計畫 0.1 第 3 點）；同一個登記點只能有一個實作，
// 所以會有多個工作包要用的（功能宣告、連線結束）依擁有者分開。
// 第 5 階段（計畫 docs/plans/2026-10-23-phase5-handoff.md 第 12 節的共通規則）：WP5.1 在這裡一次建好 log1、hand1 的登記點與分派，
// 之後的工作包只在自己的 partial 檔實作（SyncEngine.Logs.cs〔WP5.3〕、SyncEngine.HandoffOut.cs〔WP5.6〕、SyncEngine.HandoffIn.cs〔WP5.7〕）
internal sealed partial class SyncEngine
{
    /// <summary>
    /// 測試接縫：每個登記點在 partial 之後以 (登記點名稱, 引數) 通知（正式環境是 null）。引數是傳給 partial 的同一個物件——
    /// 清單可以加東西；ref 參數包成 <see cref="StrongBox{T}"/>。啟動前設定（之後只在引擎執行緒讀）
    /// </summary>
    internal Action<string, object?>? ContractProbeForTest { get; set; }

    // ── 對外：UI 執行緒 → 引擎 ──

    /// <summary>
    /// 串流中的項目（<c>live1</c>，sync-protocol 14.3；WP4.3 的對話接收者在 UI 執行緒合併後呼叫）：排進引擎執行緒交給 <see cref="OnLiveRelay"/>。
    /// 即時更新可以丟：沒有實作、沒連線、沒人在看、沒協商 <c>live1</c> 時都由實作端決定丟掉
    /// </summary>
    public void PostLive(LiveRelay relay)
    {
        ArgumentNullException.ThrowIfNull(relay);
        thread.Post(() => DeliverLive(relay));
    }

    // ── 登記點（partial；擁有者在說明裡） ──

    /// <summary>WP4.3：Hello 宣告的功能（<c>conv1</c>、<c>live1</c>）</summary>
    partial void ContributeConversationFeatures(List<string> into);

    /// <summary>WP4.4：Hello 宣告的功能（<c>cmd1</c>）</summary>
    partial void ContributeCommandFeatures(List<string> into);

    /// <summary>WP4.7：Hello 宣告的功能（<c>media1</c>）</summary>
    partial void ContributeMediaFeatures(List<string> into);

    /// <summary>WP4.3：Hello 的 <c>cursors</c> 多帶的類別（<c>meta</c>；14.2）。只能加新的鍵，第 3 階段的 <c>catalog</c>／<c>usage</c> 不讓改</summary>
    partial void ContributeHelloCursors(Dictionary<string, long> into);

    /// <summary>WP4.3：拉取多帶的游標（<c>cls=meta</c>、開著或擁有的對話 <c>stream=c:{id}</c>；14.2），排在第 3 階段的兩個之後</summary>
    partial void ContributePullCursors(List<PullCursor> into);

    /// <summary>WP4.3：心跳的執行中與等待回應的對話（線上的對話 id；sync-protocol 4 的 Heartbeat）</summary>
    partial void ContributeHeartbeat(List<string> running, List<string> waiting);

    /// <summary>
    /// WP4.3：收件匣裡引擎不認得的訊號（對話變化等，後續工作包自己定義的 <see cref="SyncSignal"/>）。處理了就把 <paramref name="handled"/> 設成 true，
    /// 要寫進 outbox 的加進 <paramref name="appends"/>（與這一輪的其他事件同一個交易）；沒人處理的照舊記警告
    /// </summary>
    partial void HandleConversationSignal(SyncSignal signal, List<OutboxAppend> appends, ref bool handled);

    /// <summary>
    /// WP4.3：這一頁拉到的對話事件（<see cref="PullDelta.Conversations"/>，依 seq，未解碼）。<paramref name="uiActions"/> 在 UI 執行緒與目錄同一次套用
    /// （丟例外時這一頁的游標不前進、下次重拉），<paramref name="afterCommit"/> 在游標與目錄提交進 sync.db 之後在引擎執行緒執行
    /// </summary>
    partial void PlanConversationApply(IReadOnlyList<PulledEvent> events, List<Action> uiActions, List<Action> afterCommit);

    /// <summary>WP4.3：串流中的項目（<see cref="PostLive"/> 排進來的；引擎執行緒）</summary>
    partial void OnLiveRelay(LiveRelay relay);

    /// <summary><c>live2</c>：伺服器要這一筆下一則送整則（14.3；引擎執行緒）</summary>
    partial void OnLiveResync(SyncConnection conn, LiveResync resync);

    /// <summary>WP4.3：Notify（交握完成後才呼叫）；對話的類別或串流要拉時把 <paramref name="pull"/> 設成 true（第 3 階段的類別已經算好）</summary>
    partial void OnNotifyForConversations(SyncConnection conn, Notify notify, ref bool pull);

    /// <summary>WP4.3：BatchAck 的結果寫回 outbox 之後（被擋的對話事件：<c>not_owner</c>、<c>fenced</c>、<c>deleted</c>、<c>dlp</c>）</summary>
    partial void OnBatchResultsForConversations(SyncConnection conn, BatchAck ack);

    /// <summary>WP4.3（<c>live1</c>）：有人在看的對話（整份取代）</summary>
    partial void OnWatchChanged(SyncConnection conn, WatchChanged changed);

    /// <summary>WP4.3（<c>conv1</c>）：伺服器要某個對話的整份內容</summary>
    partial void OnSnapshotRequest(SyncConnection conn, SnapshotRequest request);

    /// <summary>WP4.7（<c>media1</c>）：伺服器要某個媒體參照的位元組</summary>
    partial void OnMediaRequest(SyncConnection conn, MediaRequest request);

    /// <summary>
    /// WP4.4（<c>cmd1</c>）：遠端指令。實作先以 CommandEnvelope.Verify 驗信封，再去重、經 RunOnUiAsync 進中樞閘門 ExecuteRemoteAsync，
    /// 結果以 <c>conn.Session.CommandResultAsync</c> 回（連線斷了就等下一條連線的 <see cref="OnProvenForCommands"/>）
    /// </summary>
    partial void OnCommand(SyncConnection conn, CommandDelivery delivery);

    /// <summary>WP4.4：不認得的伺服器通知（例如擁有者登入的 <c>ownerLogin</c>，D27）；處理了就把 <paramref name="handled"/> 設成 true</summary>
    partial void HandleServerNoticeExtension(SyncConnection conn, ServerNotice notice, ref bool handled);

    /// <summary>WP4.4：交握完成（可以呼叫 hub 方法；送上一條連線沒送出的指令結果）。在交握完成前暫存的第 4 階段訊息處理之前</summary>
    partial void OnProvenForCommands(SyncConnection conn);

    /// <summary>WP4.3：進 Live（拉到底、outbox 送完；回補在這之後才排）</summary>
    partial void OnOnlineForConversations(SyncConnection conn);

    /// <summary>WP4.3：連線丟掉了（斷線、暫停、改政策重連、停止同步；之後這條連線的回呼一律忽略）。引擎結束時不呼叫</summary>
    partial void OnConnectionClosedForConversations(SyncConnection conn);

    /// <summary>WP4.4：連線丟掉了（同 <see cref="OnConnectionClosedForConversations"/>）</summary>
    partial void OnConnectionClosedForCommands(SyncConnection conn);

    /// <summary>WP4.7：Prove 完成（<see cref="ProveResponse.AccessToken"/> 是媒體 HTTP 端點的 <c>cha_</c>）；在 <see cref="OnProvenForCommands"/> 與暫存的第 4 階段訊息之前</summary>
    partial void OnProvenForMedia(SyncConnection conn, ProveResponse prove);

    /// <summary>WP4.7：進 Live（同 <see cref="OnOnlineForConversations"/>，排在它之後）</summary>
    partial void OnOnlineForMedia(SyncConnection conn);

    /// <summary>WP4.7：連線丟掉了（同 <see cref="OnConnectionClosedForConversations"/>）：清權杖、取消這條連線的轉送</summary>
    partial void OnConnectionClosedForMedia(SyncConnection conn);

    // ── 第 5 階段的登記點（WP5.1；沒有實作時同第 4 階段：編譯器連呼叫一起拿掉） ──

    /// <summary>
    /// WP5.3（SyncEngine.Logs.cs）：Hello 宣告的 <c>log1</c>，在 tok1 之後。條件：<see cref="Phase5"/> 的 Log1、也宣告了 conv1、
    /// v4 的表在（<see cref="SyncStore.ConversationTablesReady"/>）
    /// </summary>
    partial void ContributeLogFeatures(List<string> into);

    /// <summary>
    /// WP5.7（SyncEngine.HandoffIn.cs）：Hello 宣告的 <c>hand1</c>，在 log1 之後。條件：<see cref="Phase5"/> 的 <see cref="SyncPhase5Switches.DeclaresHand1"/>
    /// 與 log1（沒有 log1 的 hand1 由 <see cref="CollectHelloFeatures"/> 拿掉）
    /// </summary>
    partial void ContributeHandoffFeatures(List<string> into);

    /// <summary>WP5.6（SyncEngine.HandoffOut.cs；<c>hand1</c>）：伺服器請這台交出一個對話（7.2 的 A1～A6）</summary>
    partial void OnHandoffRequest(SyncConnection conn, HandoffRequest request);

    /// <summary>
    /// WP5.6／WP5.7（<c>hand1</c>）：交接的狀態（給發起者、交出端、接手端）。兩包都要用：partial 只能有一個實作，所以實作在 WP5.6 的
    /// SyncEngine.HandoffOut.cs，接手端與發起端的部分由它轉給 WP5.7 的處理（在計畫裡先說明）
    /// </summary>
    partial void OnHandoffUpdate(SyncConnection conn, HandoffUpdate update);

    /// <summary>WP5.3（SyncEngine.Logs.cs；<c>log1</c>）：伺服器存的日誌損毀，整檔以新世代重傳</summary>
    partial void OnLogRepair(SyncConnection conn, LogRepair repair);

    /// <summary>第 5 階段的開關（引擎建立時給的選項，沒給是正式的預設：兩個都關）</summary>
    private SyncPhase5Switches Phase5 => options.Phase5 ?? SyncPhase5Switches.Default;

    // ── 呼叫點（引擎本體只呼叫這些；partial 之後通知測試接縫） ──

    /// <summary>Hello 的 features：三個登記點依序加；沒有就是 null（第 3 階段逐位元組相同），不合格的整份不送（伺服器會整個拒絕 Hello）</summary>
    private IReadOnlyList<string>? CollectHelloFeatures()
    {
        var features = new List<string>();
        ContributeConversationFeatures(features);
        Probe(nameof(ContributeConversationFeatures), features);
        ContributeCommandFeatures(features);
        Probe(nameof(ContributeCommandFeatures), features);
        ContributeMediaFeatures(features);
        Probe(nameof(ContributeMediaFeatures), features);
        ContributeReleaseFeatures(features);
        ContributeStatsFeatures(features);
        ContributeTokensFeatures(features);
        ContributeSchedulesFeatures(features);
        ContributeLogFeatures(features);
        Probe(nameof(ContributeLogFeatures), features);
        ContributeHandoffFeatures(features);
        Probe(nameof(ContributeHandoffFeatures), features);
        if (features.Contains(DeviceFeatureRules.Hand1, StringComparer.Ordinal) && !features.Contains(DeviceFeatureRules.Log1, StringComparer.Ordinal))
        {
            // hand1 要靠日誌驗證逐字稿（D1）：沒有 log1 的 hand1 不宣告（伺服器的 Negotiate 也會拿掉；這裡是程式錯誤的保險）
            Log.Error("登記的功能宣告有 hand1 卻沒有 log1：這次 Hello 不宣告 hand1（程式錯誤）");
            features.RemoveAll(f => f == DeviceFeatureRules.Hand1);
        }

        if (features.Count == 0)
            return null;
        if (!DeviceFeatureRules.IsValid(features))
        {
            Log.Error($"登記的功能宣告不合格（{features.Count} 項）：這次 Hello 不宣告任何功能（程式錯誤）");
            return null;
        }

        return features;
    }

    private void CollectHelloCursors(Dictionary<string, long> cursors)
    {
        var extra = new Dictionary<string, long>(StringComparer.Ordinal);
        ContributeHelloCursors(extra);
        Probe(nameof(ContributeHelloCursors), extra);
        foreach (var (cls, after) in extra)
        {
            if (!cursors.TryAdd(cls, after))
                Log.Warn($"登記點要改 Hello 已有的游標 {CatalogSyncRules.CleanText(cls, 64)}：略過（程式錯誤）");
        }
    }

    private void CollectPullCursors(List<PullCursor> cursors)
    {
        var extra = new List<PullCursor>();
        ContributePullCursors(extra);
        Probe(nameof(ContributePullCursors), extra);
        cursors.AddRange(extra);
    }

    private (List<string> Running, List<string> Waiting) CollectHeartbeat()
    {
        var running = new List<string>();
        var waiting = new List<string>();
        ContributeHeartbeat(running, waiting);
        Probe(nameof(ContributeHeartbeat), (running, waiting));
        return (running, waiting);
    }

    private bool TryHandleConversationSignal(SyncSignal signal, List<OutboxAppend> appends)
    {
        var handled = false;
        HandleConversationSignal(signal, appends, ref handled);
        if (ContractProbeForTest is not null)
        {
            var box = new StrongBox<bool>(handled);
            Probe(nameof(HandleConversationSignal), (signal, appends, box));
            handled = box.Value;
        }

        return handled;
    }

    /// <summary>這一頁的對話事件要在 UI 執行緒與提交之後做的事（沒有對話事件就不呼叫，回 null）</summary>
    private (List<Action> Ui, List<Action> AfterCommit)? CollectConversationApply(IReadOnlyList<PulledEvent> events)
    {
        if (events.Count == 0)
            return null;
        var ui = new List<Action>();
        var after = new List<Action>();
        PlanConversationApply(events, ui, after);
        Probe(nameof(PlanConversationApply), (events, ui, after));
        return (ui, after);
    }

    private void DeliverLive(LiveRelay relay)
    {
        if (!opened || stopping)
            return;
        OnLiveRelay(relay);
        Probe(nameof(OnLiveRelay), relay);
    }

    private bool NotifyNeedsPull(SyncConnection conn, Notify notify, bool pull)
    {
        OnNotifyForConversations(conn, notify, ref pull);
        if (ContractProbeForTest is not null)
        {
            var box = new StrongBox<bool>(pull);
            Probe(nameof(OnNotifyForConversations), (notify, box));
            pull = box.Value;
        }

        return pull;
    }

    private void AfterBatchResults(SyncConnection conn, BatchAck ack)
    {
        OnBatchResultsForConversations(conn, ack);
        Probe(nameof(OnBatchResultsForConversations), ack);
    }

    private bool TryHandleServerNotice(SyncConnection conn, ServerNotice notice)
    {
        var handled = false;
        HandleServerNoticeExtension(conn, notice, ref handled);
        if (ContractProbeForTest is not null)
        {
            var box = new StrongBox<bool>(handled);
            Probe(nameof(HandleServerNoticeExtension), (notice, box));
            handled = box.Value;
        }

        return handled;
    }

    /// <summary>交握完成（CompleteHandshake 的最後）：指令的接續，再依到達順序處理交握完成前暫存的第 4 階段訊息、回溢出的 busy</summary>
    private void AfterHandshake(SyncConnection conn)
    {
        OnProvenForCommands(conn);
        Probe(nameof(OnProvenForCommands), conn.Generation);
        if (!IsCurrent(conn))
            return;
        var dropped = conn.EarlyMessages.Dropped;
        var (messages, busy) = conn.EarlyMessages.TakeAll();
        if (dropped > 0 || busy.Count > 0)
            Log.Warn($"交握完成前收到太多第 4 階段的伺服器訊息：丟掉 {dropped} 則，{busy.Count} 個指令回 busy");
        foreach (var message in messages)
        {
            if (!IsCurrent(conn))
                return;
            DispatchPhase4(conn, message);
        }

        foreach (var commandId in busy)
        {
            if (!IsCurrent(conn))
                return;
            ReplyBusy(conn, commandId);
        }
    }

    /// <summary>Prove 完成（CompleteHandshake 裡、<see cref="AfterHandshake"/> 之前）：媒體保存 <c>cha_</c></summary>
    private void AfterProveForMedia(SyncConnection conn, ProveResponse prove)
    {
        OnProvenForMedia(conn, prove);
        Probe(nameof(OnProvenForMedia), conn.Generation);
    }

    private void AfterOnline(SyncConnection conn)
    {
        OnOnlineForConversations(conn);
        Probe(nameof(OnOnlineForConversations), conn.Generation);
        OnOnlineForMedia(conn);
        Probe(nameof(OnOnlineForMedia), conn.Generation);
    }

    private void AfterConnectionClosed(SyncConnection conn)
    {
        OnConnectionClosedForConversations(conn);
        Probe(nameof(OnConnectionClosedForConversations), conn.Generation);
        OnConnectionClosedForCommands(conn);
        Probe(nameof(OnConnectionClosedForCommands), conn.Generation);
        OnConnectionClosedForMedia(conn);
        Probe(nameof(OnConnectionClosedForMedia), conn.Generation);
    }

    // ── 第 4 階段的伺服器 → 裝置 ──

    /// <summary>傳輸送來的第 4 階段訊息（引擎執行緒、目前的連線）：交握完成前暫存（D49），之後照功能分派</summary>
    private void OnPhase4Message(SyncConnection conn, object? message)
    {
        if (message is null)
            return;
        if (!conn.Proven)
        {
            // 交握完成前不會有基底（還沒送過即時更新）：要整則的請求直接略過，不佔暫存
            if (message is LiveResync)
                return;
            // 伺服器身分還沒驗過、或 Prove 還沒完成：不呼叫任何 hub 方法、不交給實作（同 ServerNotice 的暫存）
            conn.EarlyMessages.Add(message);
            return;
        }

        DispatchPhase4(conn, message);
    }

    /// <summary>這條連線協商出訊息要的功能（<see cref="DeviceFeatureRules.RequiredFor"/>）才交給登記點，否則略過（每種只記一次）</summary>
    private void DispatchPhase4(SyncConnection conn, object message)
    {
        var method = Phase4Method(message);
        if (DeviceFeatureRules.RequiredFor(method) is { } feature && !DeviceFeatureRules.Has(conn.Features, feature))
        {
            if (conn.IgnoredMethods.Add(method))
                Log.Info($"略過沒有協商功能 {feature} 的伺服器訊息 {method}（這條連線之後同種的不再記錄）");
            return;
        }

        switch (message)
        {
            case WatchChanged changed:
                OnWatchChanged(conn, changed);
                Probe(nameof(OnWatchChanged), changed);
                break;
            case SnapshotRequest request:
                OnSnapshotRequest(conn, request);
                Probe(nameof(OnSnapshotRequest), request);
                break;
            case MediaRequest request:
                OnMediaRequest(conn, request);
                Probe(nameof(OnMediaRequest), request);
                break;
            case CommandDelivery delivery:
                OnCommand(conn, delivery);
                Probe(nameof(OnCommand), delivery);
                break;
            case PeerReply reply:
                OnPeerReply(conn, reply);
                Probe(nameof(OnPeerReply), reply);
                break;
            case StatsUpdate update:
                OnStatsUpdate(conn, update);
                Probe(nameof(OnStatsUpdate), update);
                break;
            case TokensUpdate update:
                OnTokensUpdate(conn, update);
                Probe(nameof(OnTokensUpdate), update);
                break;
            case SchedulesUpdate schedules:
                OnSchedulesUpdate(conn, schedules);
                Probe(nameof(OnSchedulesUpdate), schedules);
                break;
            case CollaboratorsState collaborators:
                OnCollabUpdate(conn, collaborators);
                Probe(nameof(OnCollabUpdate), collaborators);
                break;
            case LiveResync resync:
                OnLiveResync(conn, resync);
                Probe(nameof(OnLiveResync), resync);
                break;
            case HandoffRequest request:
                OnHandoffRequest(conn, request);
                Probe(nameof(OnHandoffRequest), request);
                break;
            case HandoffUpdate update:
                OnHandoffUpdate(conn, update);
                Probe(nameof(OnHandoffUpdate), update);
                break;
            case LogRepair repair:
                OnLogRepair(conn, repair);
                Probe(nameof(OnLogRepair), repair);
                break;
            case ExecChunk chunk:
                OnExecReply(conn, chunk);
                Probe(nameof(OnExecReply), chunk);
                break;
            case ExecAbort abort:
                OnExecAbort(conn, abort);
                Probe(nameof(OnExecAbort), abort);
                break;
            case DeskPacket desk:
                OnDeskDown(conn, desk);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(message), message.GetType().Name, "不認得的第 4 階段伺服器訊息");
        }
    }

    private static string Phase4Method(object message) => message switch
    {
        WatchChanged => DeviceHubMethods.WatchChanged,
        SnapshotRequest => DeviceHubMethods.SnapshotRequest,
        MediaRequest => DeviceHubMethods.MediaRequest,
        CommandDelivery => DeviceHubMethods.CommandDelivery,
        PeerReply => DeviceHubMethods.PeerReply,
        StatsUpdate => DeviceHubMethods.StatsUpdate,
        TokensUpdate => DeviceHubMethods.TokensUpdate,
        SchedulesUpdate => DeviceHubMethods.SchedulesUpdate,
        CollaboratorsState => DeviceHubMethods.CollabUpdate,
        LiveResync => DeviceHubMethods.LiveResync,
        HandoffRequest => DeviceHubMethods.HandoffRequest,
        HandoffUpdate => DeviceHubMethods.HandoffUpdate,
        LogRepair => DeviceHubMethods.LogRepair,
        ExecChunk => DeviceHubMethods.ExecReply,
        ExecAbort => DeviceHubMethods.ExecAbort,
        DeskPacket => DeviceHubMethods.DeskDown,
        _ => throw new ArgumentOutOfRangeException(nameof(message), message.GetType().Name, "不認得的第 4 階段伺服器訊息"),
    };

    /// <summary>早到緩衝溢出的指令（D49）：協商出 <c>cmd1</c> 才回 <c>rejected</c>／<c>busy</c>（沒協商的伺服器不該送，也不送它新訊息）</summary>
    private void ReplyBusy(SyncConnection conn, string commandId)
    {
        if (!DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Cmd1))
            return;
        var result = new CommandResult(commandId, RemoteResultStates.Rejected, RemoteResultCodes.Busy, "電腦剛連上，暫時收不下更多指令");
        Spawn(async () =>
        {
            try
            {
                await conn.Session!.CommandResultAsync(result, conn.Token);
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                Log.Warn($"伺服器拒收指令 {commandId} 的 busy 結果（{ex.Failure.Code}）：只記錄、不重連");
            }
            catch (SyncTransportException ex)
            {
                Fail(conn, ex.Failure);
            }
        }, conn);
    }

    private void Probe(string hook, object? argument) => ContractProbeForTest?.Invoke(hook, argument);
}
