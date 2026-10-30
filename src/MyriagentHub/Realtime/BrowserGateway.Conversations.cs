using System.Collections.Concurrent;
using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 瀏覽器閘道的對話訊息（WP4.2 計畫步 10；R4、R21～R29，D13、D22～D25、D28、D38、D39、D42、K4）：全部在閘道迴圈（<c>runGate</c> 之下）執行，
/// 渲染也只在這裡（D24）。每一輪（最多每 <c>ConversationPushIntervalMs</c> 一次）：
/// <list type="number">
/// <item>取走變更通知，摺疊成各對話要做的事（<see cref="ConversationGatewayRules.Fold"/>）。通知溢出（或上一輪處理失敗）時整份重讀：清單重讀、每條連線開著的對話重新排一次 open。</item>
/// <item>只對開著那個對話的連線推增量，而且只推 seq 比那條連線的快照（D38 的 S）新的：<c>header</c>、<c>item</c>、<c>removeItem</c>、<c>composer</c>
/// （帶合併鍵，D23）；刪除送 <c>conversationClosed</c>、政策降級與重建做完送 <c>resync</c>（之後標成不開著，網頁重新 open）。
/// 一輪要收的增量超過一頁、或送出佇列排進去會超過上限的一半的連線，改成伺服器自己重開（排一個 open、增量先擋掉；合併前審查 F1）。</item>
/// <item><c>notify</c>：給已送過 init、看不到那個對話的連線（D25、D28；在連線鎖裡檢查再入列）。</item>
/// <item>擁有者上線或離線（D39）：重送它的對話的標頭、輸入區，清單重組。</item>
/// <item>處理 hub 記下的 open／loadOlder（D38）：每一輪最多 <c>MaxOpensPerRound</c> 個、各連線輪流；送出佇列已超過上限一半的連線留到下一輪（K4），
/// 讀好的回應排進去會溢出的也放回去、留到下一輪（<see cref="ClientOutbox.WouldOverflow"/>，合併前審查 F2）。</item>
/// <item>未讀（步 12A，BrowserGateway.Unread.cs）：取走變更時標未讀；看見的連線換了之後清掉開始被看見的（不受 50 ms 節奏限制）。</item>
/// <item>依連線的時區送 <c>conversations</c>：內容（雜湊）變了才送；過了當地午夜重算一次（D22）。這一步每一輪都做（只比雜湊）。</item>
/// </list>
/// 沒有對話儲存（<c>views</c> 是 null：單元測試的 GatewayHarness）時這些都不做，與第 3 階段相同。
/// </summary>
internal sealed partial class BrowserGateway
{
    private readonly ConcurrentQueue<string> conversationDevices = new();
    private readonly List<ConversationChange> drained = [];
    private int allConversationDevices;
    private int conversationRefresh;
    private long? lastConversationRunAt;
    private long? conversationRetryAt;

    /// <summary>對話儲存（只在確認過 <c>views</c> 不是 null 的路徑上用）</summary>
    private ConversationViews Views => views ?? throw new InvalidOperationException("沒有對話儲存");

    /// <summary>D39：這台裝置上線或離線（SignalR 的執行緒，只記下並喚醒）：它擁有的、開著的對話重送標頭與輸入區，清單的上線狀態重組</summary>
    public void MarkConversationDevice(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        if (views is null)
            return;
        conversationDevices.Enqueue(deviceId);
        Wake();
    }

    /// <summary>有對話的變更、open／loadOlder 或 ClientState 換了：喚醒迴圈（任何執行緒；不可阻塞）</summary>
    public void WakeConversations()
    {
        if (views is not null)
            Wake();
    }

    /// <summary>
    /// 迴圈之外（hub 的指令處理）送一則給一條連線：<c>clipboard</c>、open 不存在的對話時的 <c>conversationClosed</c>。在連線鎖裡看「已送過 init」再入列
    /// （<see cref="BrowserConnection.TryEnqueueAfterInit"/>），溢出照第 3 階段的規則重送 init（同 <see cref="SendToast"/>）
    /// </summary>
    internal void SendAfterInit(BrowserConnection connection, ClientOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(message);
        var nowMs = time.GetUtcNow().ToUnixTimeMilliseconds();
        if (connection.TryEnqueueAfterInit(message) is { } result && HandleResult(connection, result, nowMs).Resync)
            Wake();
    }

    /// <summary>裝置目錄（devices 部分）變了：所有開著的對話重送標頭（名稱），清單重讀</summary>
    private void MarkAllConversationDevices()
    {
        if (views is not null)
            Interlocked.Exchange(ref allConversationDevices, 1);
    }

    /// <summary>
    /// 沒有任何連線：取走並丟掉變更（沒有人開著任何對話），清單與標頭下次重讀。即時疊加層照樣取走即時更新、看過持久變更（不推給任何人），
    /// 下一條連線 open 時疊加層與持久版本一致
    /// </summary>
    private void DiscardConversations()
    {
        if (views is null)
            return;
        var store = Views;
        drained.Clear();
        var any = store.Feed.TryDrain(drained, out var overflowed);
        // 沒有連線＝沒人看見：要標未讀的照標（步 12A），看見的連線沒有可清的
        ApplyUnread(drained, [], time.GetUtcNow().ToUnixTimeMilliseconds());
        Interlocked.Exchange(ref viewersChanged, 0);
        while (conversationDevices.TryDequeue(out _))
            any = true;
        any |= Interlocked.Exchange(ref allConversationDevices, 0) == 1;
        var refresh = Interlocked.Exchange(ref conversationRefresh, 0) == 1;
        any |= refresh;
        // 被丟掉的 Deleted 不會再取到：未讀集合照目錄補做（P4SRV-MAT-04）
        if (refresh || overflowed)
            store.ReconcileUnread();
        if (overlay is not null)
            DiscardLive(refresh || overflowed);
        drained.Clear();
        if (any)
            store.MarkDevicesChanged();
    }

    /// <summary>
    /// 一輪的對話訊息；回傳「最晚多久後要再醒」（Timeout.InfiniteTimeSpan＝等到喚醒）。溢出要重送 init 時把 <paramref name="resync"/> 設成 true。
    /// 變更與請求的處理失敗（讀不到資料庫）時記錯誤、下一輪整份重讀（取走的變更已經不在通知裡），1 秒後再試
    /// </summary>
    private TimeSpan RunConversations(IReadOnlyList<BrowserConnection> all, long now, long nowMs, ref bool resync)
    {
        if (views is null)
            return Timeout.InfiniteTimeSpan;
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, options.Value.Conversations.ConversationPushIntervalMs));
        var wait = Timeout.InfiniteTimeSpan;
        if (conversationRetryAt is { } retry && now < retry)
        {
            wait = Positive(time.GetElapsedTime(now, retry));
        }
        else if (HasConversationWork(all))
        {
            if (Since(lastConversationRunAt, now) is { } elapsed && elapsed < interval)
            {
                wait = interval - elapsed;
            }
            else
            {
                lastConversationRunAt = now;
                try
                {
                    ProcessConversationChanges(all, nowMs, ref resync);
                    ProcessConversationRequests(all, nowMs, ref resync);
                    conversationRetryAt = null;
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref conversationRefresh, 1);
                    conversationRetryAt = now + (long)(Backoff[0].TotalSeconds * time.TimestampFrequency);
                    log.LogError(ex, "瀏覽器閘道處理對話失敗，{Delay} 秒後整份重讀", Backoff[0].TotalSeconds);
                    return Backoff[0];
                }
                if (HasConversationWork(all))
                    wait = interval;
            }
        }

        // 開始被看見的未讀（步 12A）：不受 50 ms 節奏限制，清單在同一輪重組
        ClearSeenUnread(all);
        if (DeliverConversationLists(all, nowMs, ref resync) is { } boundary)
            wait = Earlier(wait, TimeSpan.FromMilliseconds(Math.Max(0, boundary - nowMs)));
        return wait;
    }

    /// <summary>有沒有要處理的：變更通知、上線狀態、整份重讀，或還在等的 open／loadOlder</summary>
    private bool HasConversationWork(IReadOnlyList<BrowserConnection> all) =>
        Views.Feed.HasPending || !conversationDevices.IsEmpty || Volatile.Read(ref allConversationDevices) != 0 || Volatile.Read(ref conversationRefresh) != 0
        || overlay?.HasPending == true || all.Any(c => c.InitSent && c.PendingConversationRequests > 0);

    private void ProcessConversationChanges(IReadOnlyList<BrowserConnection> all, long nowMs, ref bool resync)
    {
        var store = Views;
        drained.Clear();
        store.Feed.TryDrain(drained, out var overflowed);
        // 未讀（步 12A）：依提交順序標記、刪除的拿掉；「看見」看所有的連線（還沒送過 init 的分頁也可能在畫面上）
        ApplyUnread(drained, all, nowMs);
        var refresh = Interlocked.Exchange(ref conversationRefresh, 0) == 1 || overflowed;
        var allDevices = Interlocked.Exchange(ref allConversationDevices, 0) == 1;
        var presence = new HashSet<string>(StringComparer.Ordinal);
        while (conversationDevices.TryDequeue(out var deviceId))
            presence.Add(deviceId);
        var live = all.Where(c => c.InitSent).ToList();
        // 即時疊加層（步 11）：先取走即時更新，再依提交順序看持久變更（收件早於提交的即時更新，一定在它的持久版本之前或同一輪被看到）
        var liveRound = BeginLiveRound(live, refresh, nowMs);
        if (liveRound is not null)
        {
            foreach (var change in drained)
                liveRound.Apply(overlay!.OnDurable(change, liveRound.Watched.Contains(change.ConversationId)));
        }
        // 第一次送出建立的新對話出現了：換掉草稿（WP4.6 步 7、D41；Fold 之前，drained 還在）
        if (drafts is not null)
            SwapCreatedDrafts(drained, refresh);
        var round = ConversationGatewayRules.Fold(drained);
        drained.Clear();

        if (refresh)
        {
            // 有變更因為通知滿了被丟掉（或上一輪沒處理完）：清單重讀，開著的對話各自重新排一次 open（快照是屏障，D23）；
            // 被丟掉的 Deleted 不會再取到，未讀集合照目錄補做（P4SRV-MAT-04）
            store.ReconcileUnread();
            store.MarkListStale();
            store.ClearHeaders();
            foreach (var connection in live)
            {
                foreach (var conversationId in connection.OpenConversationIds())
                    connection.TryRequestConversation(new ConversationRequest(ConversationRequestKind.Open, conversationId));
            }
        }
        if (allDevices)
            store.MarkDevicesChanged();
        else if (presence.Count > 0)
            store.MarkPresenceChanged();
        if (round.ListChanged)
            store.MarkListStale();

        foreach (var work in round.Conversations)
        {
            if (work.HeaderSeq is not null || work.Deleted || work.Resync)
                store.InvalidateHeader(work.ConversationId);
            PushConversation(live, work, nowMs, ref resync);
        }

        if (allDevices || presence.Count > 0)
        {
            foreach (var conversationId in live.SelectMany(c => c.OpenConversationIds()).Distinct(StringComparer.Ordinal).ToList())
            {
                if (allDevices || (store.OwnerOf(conversationId) is { } owner && presence.Contains(owner)))
                    PushDevice(live, conversationId, nowMs, ref resync);
            }
        }

        if (liveRound is not null)
            FinishLiveRound(live, liveRound, round, nowMs, ref resync);
    }

    /// <summary>一個對話這一輪的增量與通知（見類別說明的第 2、3 點）</summary>
    private void PushConversation(List<BrowserConnection> live, ConversationRoundWork work, long nowMs, ref bool resync)
    {
        var store = Views;
        var conversationId = work.ConversationId;
        var open = new List<(BrowserConnection Connection, long Seq)>();
        foreach (var connection in live)
        {
            if (connection.TryGetOpenConversation(conversationId, out var snapshotSeq))
                open.Add((connection, snapshotSeq));
        }

        if (work.Deleted)
        {
            var closed = ConversationGatewayRules.ClosedMessage(conversationId);
            foreach (var (connection, _) in open)
            {
                Update(connection, closed, null, nowMs, ref resync);
                connection.CloseConversation(conversationId);
            }
            return;
        }

        if (work.Resync && open.Count > 0)
        {
            var message = ConversationGatewayRules.ResyncMessage(conversationId);
            for (var i = open.Count - 1; i >= 0; i--)
            {
                var (connection, snapshotSeq) = open[i];
                if (work.ResyncSeq is { } at && at <= snapshotSeq)
                    continue;
                Update(connection, message, null, nowMs, ref resync);
                connection.CloseConversation(conversationId);
                open.RemoveAt(i);
            }
        }

        if (open.Count > 0)
            PushIncrements(open, work, nowMs, ref resync);

        foreach (var attention in work.Attentions)
        {
            if (ConversationGatewayRules.NotifyKind(attention.AttentionKind) is not { } kind || store.NotifyInfo(conversationId) is not { } info)
                continue;
            var notify = ConversationGatewayRules.NotifyMessage(conversationId, kind, info.Title, info.DeviceName);
            // 語音通知：每一條連線都送（看得到也念，同桌面；網頁依這個瀏覽器的開關決定），句子由擁有者依同步政策組好
            var speak = ConversationGatewayRules.SpeechText(attention.Speech, attention.AttentionKind, info.Title) is { } text
                ? ConversationGatewayRules.SpeakMessage(conversationId, attention.Seq, text)
                : null;
            foreach (var connection in live)
            {
                // 協作人只收分享給他的對話的通知（多人協作）
                if (!CanSeeConversation(connection, conversationId))
                    continue;
                if (!ConversationGatewayRules.CanSee(connection.State, connection.VisibleConversationIds(), conversationId)
                    && connection.TryEnqueueAfterInit(notify) is { } result)
                    resync |= HandleResult(connection, result, nowMs).Resync;
                if (speak is not null && connection.TryEnqueueAfterInit(speak) is { } spoken)
                    resync |= HandleResult(connection, spoken, nowMs).Resync;
            }
        }
    }

    /// <summary>
    /// 標頭、項目、輸入區：各組一次、各連線共用；每條連線只收 seq 比它的快照新的（入列時在連線鎖裡比）。
    /// 流量控制（合併前審查 F1）：渲染之前，要收的則數超過一頁或送出佇列已經排了很多的連線改成重開（<see cref="BrowserConnection.RequestReopen"/>），
    /// 全部都重開就完全不渲染；渲染之後再以位元組看一次（<see cref="ConversationGatewayRules.ShouldReopen"/>）
    /// </summary>
    private void PushIncrements(List<(BrowserConnection Connection, long Seq)> open, ConversationRoundWork work, long nowMs, ref bool resync)
    {
        var store = Views;
        var conversationId = work.ConversationId;
        var browser = options.Value.Browser;
        for (var i = open.Count - 1; i >= 0; i--)
        {
            var (connection, snapshotSeq) = open[i];
            var count = IncrementCount(work, snapshotSeq);
            if (count > 0
                && ConversationGatewayRules.ShouldReopen(count, 0, connection.Outbox.Count, connection.Outbox.Bytes, browser.OutboxMaxMessages, browser.OutboxMaxBytes)
                && connection.RequestReopen(conversationId))
                open.RemoveAt(i);
        }
        if (open.Count == 0)
            return;

        var oldest = open.Min(o => o.Seq);
        var needsHeader = work.HeaderSeq is { } h && h > oldest;
        var needsItems = work.Items.Any(c => c.Seq > oldest);
        var messages = new List<(ClientOutboxMessage Message, long Seq)>();
        if (needsHeader || needsItems)
        {
            // 對話不在了（刪除還沒取走）：之後的 Deleted 會關掉它
            if (store.Header(conversationId) is not { } view)
                return;
            if (needsHeader)
                messages.Add((ConversationGatewayRules.HeaderMessage(conversationId, store.HeaderJson(view)), work.HeaderSeq!.Value));
            foreach (var change in work.Items)
            {
                if (change.Seq <= oldest)
                    continue;
                ClientOutboxMessage? message = change.Kind == ConversationChangeKind.ItemRemoved
                    ? ConversationGatewayRules.RemoveItemMessage(conversationId, change.ItemId!)
                    : store.ChangeItemJson(change, view.Header) is { } json ? ConversationGatewayRules.ItemMessage(conversationId, change.ItemId!, json) : null;
                if (message is not null)
                    messages.Add((message, change.Seq));
            }
        }
        if (work.ComposerSeq is { } composerSeq && composerSeq > oldest)
            messages.Add((ConversationGatewayRules.ComposerMessage(conversationId, store.ComposerJson(conversationId)), composerSeq));

        foreach (var (connection, snapshotSeq) in open)
        {
            var count = 0;
            long incoming = 0;
            foreach (var (message, seq) in messages)
            {
                if (seq <= snapshotSeq)
                    continue;
                count++;
                incoming += message.Bytes;
            }
            if (count == 0)
                continue;
            if (ConversationGatewayRules.ShouldReopen(count, incoming, connection.Outbox.Count, connection.Outbox.Bytes, browser.OutboxMaxMessages, browser.OutboxMaxBytes)
                && connection.RequestReopen(conversationId))
                continue;
            foreach (var (message, seq) in messages)
                Update(connection, message, seq, nowMs, ref resync);
        }
    }

    /// <summary>快照 seq 是 <paramref name="snapshotSeq"/> 的連線這一輪最多要收幾則增量（標頭、項目、輸入區；渲染之前估）</summary>
    private static int IncrementCount(ConversationRoundWork work, long snapshotSeq)
    {
        var count = work.Items.Count(c => c.Seq > snapshotSeq);
        if (work.HeaderSeq > snapshotSeq)
            count++;
        if (work.ComposerSeq > snapshotSeq)
            count++;
        return count;
    }

    /// <summary>D39：擁有者上線或離線（或裝置目錄變了）：開著這個對話的連線重送標頭與輸入區（不比 seq：讀到的就是現在的）</summary>
    private void PushDevice(List<BrowserConnection> live, string conversationId, long nowMs, ref bool resync)
    {
        var store = Views;
        var open = live.Where(c => c.TryGetOpenConversation(conversationId, out _)).ToList();
        if (open.Count == 0 || store.Header(conversationId) is not { } view)
            return;
        var header = ConversationGatewayRules.HeaderMessage(conversationId, store.HeaderJson(view));
        var composer = ConversationGatewayRules.ComposerMessage(conversationId, store.ComposerJson(conversationId));
        foreach (var connection in open)
        {
            Update(connection, header, null, nowMs, ref resync);
            Update(connection, composer, null, nowMs, ref resync);
        }
    }

    /// <summary>
    /// 處理 hub 記下的 open／loadOlder（D38）：讀快照（記下交易看到的最大 seq）、渲染、在連線鎖裡完成（作廢的不入列；open 排進去才標成開著）。
    /// 每一輪最多 <c>MaxOpensPerRound</c> 個，各連線輪流一個；送出佇列超過上限一半的連線這一輪跳過（K4）。讀取失敗的請求放回去、整輪失敗
    /// </summary>
    private void ProcessConversationRequests(IReadOnlyList<BrowserConnection> all, long nowMs, ref bool resync)
    {
        var store = Views;
        var budget = Math.Max(1, options.Value.Conversations.MaxOpensPerRound);
        var outboxMaxBytes = options.Value.Browser.OutboxMaxBytes;
        HashSet<BrowserConnection>? deferred = null;
        var progress = true;
        while (budget > 0 && progress)
        {
            progress = false;
            foreach (var connection in all)
            {
                if (budget == 0)
                    break;
                if (!connection.InitSent || connection.PendingConversationRequests == 0 || deferred?.Contains(connection) == true
                    || ConversationGatewayRules.ShouldDeferOpen(connection.Outbox.Bytes, outboxMaxBytes))
                    continue;
                var taken = connection.TakeConversationRequests(1);
                if (taken.Count == 0)
                    continue;
                budget--;
                progress = true;
                var request = taken[0];
                // 協作人（多人協作）：記下請求到這一輪之間取消了分享的，照不存在處理
                if (!CanSeeConversation(connection, request.ConversationId))
                {
                    if (request.Kind == ConversationRequestKind.Open && connection.TryEnqueueAfterInit(ConversationGatewayRules.ClosedMessage(request.ConversationId)) is { } closed)
                        resync |= HandleResult(connection, closed, nowMs).Resync;
                    continue;
                }
                (ClientOutboxMessage Message, long? Seq, IReadOnlyList<(long Ord, bool Final)> Items) response;
                try
                {
                    response = request.Kind switch
                    {
                        ConversationRequestKind.Open => store.Snapshot(request.ConversationId),
                        ConversationRequestKind.LoadOlder => WithoutItems(store.Older(request.ConversationId, request.BeforeId ?? string.Empty)),
                        _ => throw new ArgumentOutOfRangeException(nameof(all), request.Kind, "未知的對話請求"),
                    };
                }
                catch
                {
                    connection.RetryConversationRequest(request);
                    throw;
                }
                // F2（合併前審查）：K4 只看佇列有沒有超過一半，一份快照（一頁 ≤PageMaxBytes 再加標頭、輸入區與外框）本身就可能超過一半：
                // 以實際的大小判斷，排進去會溢出就放回去、這一輪不再處理這條連線（佇列空了一定照收，所以一定有進展）
                if (connection.Outbox.WouldOverflow(response.Message))
                {
                    connection.RetryConversationRequest(request);
                    (deferred ??= new HashSet<BrowserConnection>(ReferenceEqualityComparer.Instance)).Add(connection);
                    continue;
                }
                if (connection.CompleteConversationRequest(request, response.Message, response.Seq) is { } result)
                {
                    resync |= HandleResult(connection, result, nowMs).Resync;
                    // open 之後疊上即時更新（D38：讀快照、合併疊加層、入列 conversation；屏障之後的 item 不會被清掉）
                    if (request.Kind == ConversationRequestKind.Open && response.Seq is not null)
                        AfterOpen(all, connection, request.ConversationId, response.Items, nowMs, ref resync);
                }
            }
        }
    }

    /// <summary>
    /// 各連線的對話清單（D22）：依連線的時區取清單（同一份資料、同一個時區與當地日期共用），雜湊變了才送，時區或下一個當地午夜變了只更新標記。
    /// 回傳最早的下一個當地午夜（迴圈到時候要醒來重算）；沒有已送過 init 的連線回 null
    /// </summary>
    private long? DeliverConversationLists(IReadOnlyList<BrowserConnection> all, long nowMs, ref bool resync)
    {
        var store = Views;
        long? earliest = null;
        foreach (var connection in all)
        {
            if (!connection.InitSent)
                continue;
            var tz = ConversationGatewayRules.TimezoneOf(connection.State, time.LocalTimeZone, nowMs);
            var list = ListOf(connection, tz, nowMs);
            var boundary = ConversationViewRules.NextDayBoundaryMs(nowMs, tz);
            var mark = new ConversationListMark(list.Hash, tz, boundary);
            switch (ConversationGatewayRules.ListAction(connection.ConversationList, list.Hash, tz, boundary))
            {
                case ConversationListAction.Send:
                    if (connection.TryEnqueueConversationList(ConversationGatewayRules.ConversationsMessage(list.ItemsJson), mark) is { } result)
                        resync |= HandleResult(connection, result, nowMs).Resync;
                    break;
                case ConversationListAction.Remark:
                    connection.MarkConversationListSent(mark);
                    break;
                case ConversationListAction.None:
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(all), "未知的清單動作");
            }
            earliest = earliest is { } e ? Math.Min(e, boundary) : boundary;
        }
        return earliest;
    }

    /// <summary>
    /// init 帶的清單（D22、D42）：這條連線的時區、這一刻的清單與它的標記（閘道在 init 入列之後記下）。讀不到資料庫時帶空的清單
    /// （之後的一輪讀得到時雜湊不同、照常重送），不讓 init 送不出去
    /// </summary>
    private (ConversationListView List, ConversationListMark Mark) InitConversationList(BrowserConnection connection, long nowMs)
    {
        var tz = ConversationGatewayRules.TimezoneOf(connection.State, time.LocalTimeZone, nowMs);
        ConversationListView list;
        try
        {
            list = ListOf(connection, tz, nowMs);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "瀏覽器閘道讀不到對話清單，init 先帶空的清單");
            list = new ConversationListView("unavailable", "[]", ConversationGatewayRules.ListHash("[]"));
        }
        return (list, new ConversationListMark(list.Hash, tz, ConversationViewRules.NextDayBoundaryMs(nowMs, tz)));
    }

    /// <summary>這條連線的清單：擁有者是全部，協作人只有分享給他的（多人協作）</summary>
    private ConversationListView ListOf(BrowserConnection connection, int tz, long nowMs)
    {
        if (connection.CollaboratorId is not { } collaboratorId || collaborators is null)
            return Views.ListFor(tz, nowMs);
        return Views.ListForCollaborator(collaboratorId, collaborators.SharedConversations(collaboratorId), collaborators.Version, tz, nowMs);
    }

    /// <summary>對話的增量入列（在連線鎖裡看開著、seq 比快照新、已送過 init）並處理溢出</summary>
    private void Update(BrowserConnection connection, ClientOutboxMessage message, long? seq, long nowMs, ref bool resync)
    {
        if (connection.TryEnqueueConversationUpdate(message, seq) is { } result)
            resync |= HandleResult(connection, result, nowMs).Resync;
    }

    private static TimeSpan Earlier(TimeSpan a, TimeSpan b)
    {
        if (a == Timeout.InfiniteTimeSpan)
            return b;
        if (b == Timeout.InfiniteTimeSpan)
            return a;
        return a < b ? a : b;
    }
}
