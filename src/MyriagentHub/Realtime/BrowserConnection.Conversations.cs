using Myriagent.Sync;

namespace MyriagentHub.Realtime;

/// <summary>瀏覽器要的對話內容（WP4.2 計畫 D38）：open 回整份 <c>conversation</c>、loadOlder 回 <c>olderItems</c></summary>
internal enum ConversationRequestKind
{
    Open,
    LoadOlder,
}

/// <summary>一個待處理的 open／loadOlder：hub 的指令處理只做 O(1) 的檢查就記下，由閘道迴圈依序讀快照、渲染、入列（D38）</summary>
/// <param name="BeforeId">loadOlder 的 <c>beforeId</c>（open 是 null）</param>
internal sealed record ConversationRequest(ConversationRequestKind Kind, string ConversationId, string? BeforeId = null);

/// <summary>最後一次送給這條連線的對話清單：內容的雜湊、算分組用的時區、下一個當地午夜（到了就重送，D22）</summary>
internal sealed record ConversationListMark(string Hash, int TzOffsetMinutes, long NextDayBoundaryMs);

/// <summary>
/// 一條瀏覽器連線的對話檢視狀態（WP4.2 計畫步 9；D23、D25、D38、D42）：開著的對話（與它的快照 seq）、待處理與處理中的 open／loadOlder、
/// 最後一次 <c>visible</c> 指令的清單、最後一次送的清單。全部在連線的 <c>gate</c> 鎖內讀寫（hub 的指令處理、閘道迴圈與通知各自的執行緒），
/// 入列一律在同一把鎖裡先檢查再入列（鎖的順序同主檔：gate → 送出佇列），所以：
/// <list type="bullet">
/// <item>通知類（<see cref="TryEnqueueAfterInit"/>）不會比這條連線的 init 先入列；</item>
/// <item>增量（<see cref="TryEnqueueConversationUpdate"/>）只給開著的對話、只給比快照新的 seq，close、init 之後一則都不會再排進去；</item>
/// <item>閘道取走、還沒完成的請求在 close 或 init 之後作廢（<see cref="CompleteConversationRequest"/> 回 null），晚到的快照不會把關掉的對話標回開著。</item>
/// </list>
/// 時區取自 <see cref="State"/>（ClientState），不在這裡另存。
/// </summary>
internal sealed partial class BrowserConnection
{
    /// <summary>待處理的 open／loadOlder 最多幾個（hub 的速率已經限制了每秒幾個；滿了回 false，指令處理回 rate_limited）</summary>
    public const int MaxPendingConversationRequests = 128;

    /// <summary><c>visible</c> 指令的清單最多留幾個（同 ClientState 的 <c>openConversationIds</c> 上限）</summary>
    public const int MaxVisibleConversations = MessageGuard.MaxOpenConversationIds;

    private readonly List<ConversationRequest> conversationRequests = [];
    private readonly List<ConversationRequest> inFlightRequests = [];
    private readonly Dictionary<string, long> openConversations = new(StringComparer.Ordinal);
    private string[] visibleConversations = [];
    private ConversationListMark? conversationList;

    /// <summary>
    /// 通知類（notify、clipboard，之後的 commandResult、fileRef）的入列：在連線鎖裡看「已送過 init」再入列（同 <see cref="TryEnqueueToast"/>），
    /// 所以不會比這條連線的 init 先送出；還沒送過 init 回 null（不入列）。入列的結果由呼叫端照溢出的規則處理
    /// </summary>
    public ClientOutboxResult? TryEnqueueAfterInit(ClientOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (gate)
            return initSent ? Outbox.Enqueue(message) : null;
    }

    /// <summary>
    /// 記下一個 open／loadOlder（hub 的指令處理呼叫；閘道迴圈之後依到達順序處理）。同樣的請求已經在等就不重複記（回 true）；
    /// 已經有 <see cref="MaxPendingConversationRequests"/> 個在等回 false
    /// </summary>
    public bool TryRequestConversation(ConversationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            if (conversationRequests.Contains(request))
                return true;
            if (conversationRequests.Count >= MaxPendingConversationRequests)
                return false;
            conversationRequests.Add(request);
            return true;
        }
    }

    /// <summary>還在等的請求數（不含閘道已取走的）</summary>
    public int PendingConversationRequests
    {
        get
        {
            lock (gate)
                return conversationRequests.Count;
        }
    }

    /// <summary>
    /// 閘道迴圈取走最多 <paramref name="max"/> 個請求（依到達順序）；取走的變成處理中，完成時以 <see cref="CompleteConversationRequest"/> 入列。
    /// 處理中的請求在 close 那個對話或 init 之後作廢
    /// </summary>
    public IReadOnlyList<ConversationRequest> TakeConversationRequests(int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        lock (gate)
        {
            var count = Math.Min(max, conversationRequests.Count);
            var taken = conversationRequests.GetRange(0, count);
            conversationRequests.RemoveRange(0, count);
            inFlightRequests.AddRange(taken);
            return taken;
        }
    }

    /// <summary>
    /// 完成一個取走的請求：在連線鎖裡確認它沒有作廢、這條連線已送過 init，才把回應入列（open 的 <c>conversation</c>、loadOlder 的 <c>olderItems</c>，
    /// 或對話不在了時的 <c>conversationClosed</c>）。open 的回應排進去之後，以 <paramref name="openedAtSeq"/>（快照交易看到的最大 seq，D38 的 S）
    /// 標成開著，之後只推比它新的變更；<paramref name="openedAtSeq"/> 是 null（對話不在了）就標成不開著。作廢或還沒送過 init 回 null（不入列）
    /// </summary>
    public ClientOutboxResult? CompleteConversationRequest(ConversationRequest request, ClientOutboxMessage message, long? openedAtSeq)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(message);
        lock (gate)
        {
            var index = inFlightRequests.FindIndex(r => ReferenceEquals(r, request));
            if (index < 0)
                return null;
            inFlightRequests.RemoveAt(index);
            if (!initSent)
                return null;
            var result = Outbox.Enqueue(message);
            if (request.Kind == ConversationRequestKind.Open && result != ClientOutboxResult.Overflow)
            {
                if (openedAtSeq is { } seq)
                    openConversations[request.ConversationId] = seq;
                else
                    openConversations.Remove(request.ConversationId);
            }
            return result;
        }
    }

    /// <summary>
    /// 取走的請求沒處理完（閘道讀快照失敗，WP4.2 步 10）：放回待處理的最前面，下一輪再試；已經作廢（這段期間 close、init）的不放回。放回了回 true
    /// </summary>
    public bool RetryConversationRequest(ConversationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            var index = inFlightRequests.FindIndex(r => ReferenceEquals(r, request));
            if (index < 0)
                return false;
            inFlightRequests.RemoveAt(index);
            if (!conversationRequests.Contains(request))
                conversationRequests.Insert(0, request);
            return true;
        }
    }

    /// <summary>
    /// close（或對話被刪除）：標成不開著、丟掉這個對話還在等與處理中的請求（晚到的回覆不送，web-protocol 的 conversation）。有任何改變回 true
    /// </summary>
    public bool CloseConversation(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        lock (gate)
        {
            var changed = openConversations.Remove(conversationId);
            changed |= conversationRequests.RemoveAll(r => r.ConversationId == conversationId) > 0;
            changed |= inFlightRequests.RemoveAll(r => r.ConversationId == conversationId) > 0;
            return changed;
        }
    }

    /// <summary>
    /// 重開中的快照 seq（<see cref="RequestReopen"/>）：比任何 seq 都大，帶 seq 的增量一律擋掉，直到排著的 open 完成、寫回真正的 S
    /// </summary>
    public const long ReopeningSeq = long.MaxValue;

    /// <summary>
    /// 合併前審查 F1：這個對話的增量太多（或送出佇列已經排了很多）時改成重開——在連線鎖裡確認它開著，快照 seq 設成 <see cref="ReopeningSeq"/>，
    /// 排一個 open（同樣的請求已經在等就不重複；之後照常受 K4 管，完成時的 <c>conversation</c> 是屏障、清掉排著的增量，網頁收到未請求的
    /// <c>conversation</c> 照樣整份取代）。沒開著、還沒送過 init，或待處理的請求已滿（排不進去）回 false，呼叫端照常推增量
    /// </summary>
    public bool RequestReopen(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        lock (gate)
        {
            if (!initSent || !openConversations.ContainsKey(conversationId))
                return false;
            var request = new ConversationRequest(ConversationRequestKind.Open, conversationId);
            if (!conversationRequests.Contains(request))
            {
                if (conversationRequests.Count >= MaxPendingConversationRequests)
                    return false;
                conversationRequests.Add(request);
            }
            openConversations[conversationId] = ReopeningSeq;
            return true;
        }
    }

    /// <summary>這個對話開著嗎；開著時 <paramref name="snapshotSeq"/> 是它的快照 seq（D38 的 S；重開中是 <see cref="ReopeningSeq"/>）</summary>
    public bool TryGetOpenConversation(string conversationId, out long snapshotSeq)
    {
        lock (gate)
            return openConversations.TryGetValue(conversationId, out snapshotSeq);
    }

    /// <summary>開著的對話（依 id 排序的複本）</summary>
    public IReadOnlyList<string> OpenConversationIds()
    {
        lock (gate)
            return openConversations.Keys.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 對話的增量（item、removeItem、header、composer，或 conversationClosed、resync）：在連線鎖裡確認這條連線已送過 init、
    /// <see cref="ClientOutboxMessage.ConversationId"/> 開著、<paramref name="seq"/> 比它的快照新（null＝不比，即時更新用），才入列；否則回 null（不入列）
    /// </summary>
    public ClientOutboxResult? TryEnqueueConversationUpdate(ClientOutboxMessage message, long? seq)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.ConversationId is not { } conversationId)
            throw new ArgumentException("對話的增量要帶 ConversationId", nameof(message));
        lock (gate)
        {
            if (!initSent || !openConversations.TryGetValue(conversationId, out var snapshot))
                return null;
            if (seq is { } at && at <= snapshot)
                return null;
            return Outbox.Enqueue(message);
        }
    }

    /// <summary>
    /// 記下最後一次 <c>visible</c> 指令的清單（D25 的「看得到」的一半）：只留對話 id 形狀的（草稿不是）、去重、最多 <see cref="MaxVisibleConversations"/> 個。
    /// 內容變了回 true，並在鎖外通知 <see cref="BrowserConnections.StateChanged"/>（同 ClientState：觀看追蹤、閘道）
    /// </summary>
    public bool SetVisibleConversations(IEnumerable<string?> conversationIds)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        var next = conversationIds.Where(ConversationIds.IsWireId).Distinct(StringComparer.Ordinal).Take(MaxVisibleConversations)
            .Order(StringComparer.Ordinal).Select(id => id!).ToArray();
        lock (gate)
        {
            if (visibleConversations.AsSpan().SequenceEqual(next))
                return false;
            visibleConversations = next;
        }
        stateChanged?.Invoke(this);
        return true;
    }

    /// <summary>最後一次 <c>visible</c> 指令的清單（依 id 排序）</summary>
    public IReadOnlyList<string> VisibleConversationIds()
    {
        lock (gate)
            return visibleConversations;
    }

    /// <summary>最後一次送給這條連線的對話清單（init 之後、送第一份之前是 null）</summary>
    public ConversationListMark? ConversationList
    {
        get
        {
            lock (gate)
                return conversationList;
        }
    }

    /// <summary>
    /// 對話清單（<c>conversations</c>）的入列：在連線鎖裡確認已送過 init 才入列，排進去（沒溢出）就記下 <paramref name="mark"/>；還沒送過 init 回 null
    /// </summary>
    public ClientOutboxResult? TryEnqueueConversationList(ClientOutboxMessage message, ConversationListMark mark)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(mark);
        lock (gate)
        {
            if (!initSent)
                return null;
            var result = Outbox.Enqueue(message);
            if (result != ClientOutboxResult.Overflow)
                conversationList = mark;
            return result;
        }
    }

    /// <summary>init 帶的清單也算送過（閘道在 <see cref="EnqueueInit"/> 之後記；init 會先清掉上一份標記）</summary>
    public void MarkConversationListSent(ConversationListMark mark)
    {
        ArgumentNullException.ThrowIfNull(mark);
        lock (gate)
            conversationList = mark;
    }

    /// <summary>
    /// D42：入列 init 時（在 <see cref="EnqueueInit"/> 的同一把鎖裡）清掉開著的對話、還在等與處理中的請求、最後一次送的清單——網頁收到 init 會重新 open
    /// 畫面上的分頁。<c>visible</c> 的清單保留：它描述的是網頁畫面上顯示什麼，init 不改變畫面，網頁內容沒變時也不重送 <c>visible</c>
    /// </summary>
    private void ResetConversationViewLocked()
    {
        conversationRequests.Clear();
        inFlightRequests.Clear();
        openConversations.Clear();
        conversationList = null;
    }
}
