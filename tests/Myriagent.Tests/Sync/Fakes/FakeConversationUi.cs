using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>
/// 引擎測試用的 UI 端（<see cref="IConversationUiPort"/>，代替發布者＋中樞）：記憶體裡的對話，引擎要的快照直接放回盒子（同發布者的回應），
/// 盒子從空變成非空就喚醒引擎。引擎執行緒呼叫，測試執行緒也會呼叫 <see cref="Put"/>，以鎖保護
/// </summary>
internal sealed class FakeConversationUi(ConversationChangeBox box, Func<SyncEngine> engine) : IConversationUiPort
{
    private readonly object gate = new();
    private readonly Dictionary<string, FakeConversation> conversations = new(StringComparer.Ordinal);
    private readonly List<string> heldServerDeleted = [];

    public ConversationChangeBox Box { get; } = box;

    /// <summary>引擎呼叫過的方法（「方法:對話」，依呼叫順序）</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    public ConcurrentQueue<(PublishNoticeKind Kind, int Count)> Notices { get; } = new();

    public ConcurrentQueue<string> Paused { get; } = new();

    public ConcurrentQueue<IReadOnlySet<string>> Watched { get; } = new();

    /// <summary>寫伺服器刪除的旁檔成功與否（預設成功）</summary>
    public Func<string, bool> ServerDeletedResult { get; set; } = _ => true;

    /// <summary>扣住寫旁檔的結果，等測試 <see cref="ReleaseServerDeleted"/> 才放回盒子</summary>
    public bool HoldServerDeleted { get; set; }

    public FakeConversation Add(FakeConversation conversation)
    {
        lock (gate)
            conversations[conversation.Id] = conversation;
        return conversation;
    }

    public void Remove(string id)
    {
        lock (gate)
            conversations.Remove(id);
    }

    /// <summary>放進盒子；原本是空的就喚醒引擎（同發布者）</summary>
    public void Put(ConversationChange change)
    {
        if (Box.Put(change))
            engine().Post(ConversationWake.Instance);
    }

    public void RequestMeta(IReadOnlyList<string>? ids)
    {
        Calls.Enqueue($"RequestMeta:{(ids is null ? "*" : string.Join(",", ids))}");
        List<PublishMetaSnapshot> metas;
        IReadOnlyList<string>? all = null;
        List<string> requested;
        lock (gate)
        {
            var wanted = ids ?? conversations.Keys.Order(StringComparer.Ordinal).ToList();
            requested = wanted.ToList();
            metas = wanted.Where(conversations.ContainsKey).Select(id => conversations[id].Snapshot()).ToList();
            if (ids is null)
                all = wanted.ToList();
        }

        // 同發布者：每一批帶著要的 id（沒回來的＝中樞沒有它）
        Put(new MetaBatch(metas, all, Refresh: ids is null, Complete: ids is null, Requested: requested));
    }

    public void RequestItems(string conversationId, long afterOrd, int take, OrdHint? hint)
    {
        Calls.Enqueue($"RequestItems:{conversationId}:{afterOrd}");
        PublishItemsPage? page = null;
        lock (gate)
        {
            if (conversations.TryGetValue(conversationId, out var c) && c.Loaded && !c.IsDraft && !c.IsReplica)
            {
                // 同中樞的 PrepareOrdsForPublish：還沒有 Ord 的依清單順序補配
                foreach (var item in c.Items.Where(i => i.Ord is null))
                    item.Ord = c.NextOrd++;
                var selection = PublishPageRules.Select(c.Items, i => i.Ord, afterOrd, Math.Clamp(take, 1, PublishTimingRules.ItemsPage));
                var copies = selection.Indices.Select(i => ChatItemCopyRules.Copy(c.Items[i])).ToList();
                page = new PublishItemsPage(conversationId, copies, afterOrd <= 0 ? c.Items.Select(i => i.Id).ToList() : null, selection.LastOrd, selection.More, true,
                    afterOrd > 0 || c.RemovalsTrusted);
            }
        }

        Put(new ItemsPageChange(conversationId, afterOrd, page));
    }

    public void SetWatched(IReadOnlySet<string> ids) => Watched.Enqueue(ids);

    public void PauseForDlp(string conversationId)
    {
        Calls.Enqueue($"PauseForDlp:{conversationId}");
        Paused.Enqueue(conversationId);
    }

    public void MarkServerDeleted(string conversationId)
    {
        Calls.Enqueue($"MarkServerDeleted:{conversationId}");
        lock (gate)
        {
            if (HoldServerDeleted)
            {
                heldServerDeleted.Add(conversationId);
                return;
            }
        }

        AnswerServerDeleted(conversationId);
    }

    /// <summary>放回扣住的寫旁檔結果</summary>
    public void ReleaseServerDeleted()
    {
        List<string> held;
        lock (gate)
        {
            HoldServerDeleted = false;
            held = heldServerDeleted.ToList();
            heldServerDeleted.Clear();
        }

        foreach (var id in held)
            AnswerServerDeleted(id);
    }

    public void RequestQueue(string conversationId)
    {
        Calls.Enqueue($"RequestQueue:{conversationId}");
        PublishQueueSnapshot? queue = null;
        lock (gate)
        {
            if (conversations.TryGetValue(conversationId, out var c) && !c.IsDraft && !c.IsReplica)
                queue = new PublishQueueSnapshot(conversationId, c.Queue.ToList());
        }

        if (queue is not null)
            Put(new QueueChanged(conversationId, queue, Resync: true));
    }

    public void Notify(PublishNoticeKind kind, int count) => Notices.Enqueue((kind, count));

    /// <summary>引擎交來的回補進度與暫停時間（依呼叫順序；WP4.3 步 12）</summary>
    public ConcurrentQueue<(SyncBackfillProgress? Progress, BackfillPause? Pause)> Progress { get; } = new();

    public void ReportProgress(SyncBackfillProgress? progress, BackfillPause? pause) => Progress.Enqueue((progress, pause));

    /// <summary>引擎交來的複本變化（依呼叫順序；WP4.3 步 14）</summary>
    public ConcurrentQueue<ReplicaDelta> ReplicaDeltas { get; } = new();

    /// <summary>套用複本變化時要丟的例外（null＝成功；測試 UI 套用失敗時游標不前進）</summary>
    public Func<ReplicaDelta, Exception?>? ReplicaFailure { get; set; }

    public void ApplyReplicaDelta(ReplicaDelta delta)
    {
        if (ReplicaFailure?.Invoke(delta) is { } failure)
            throw failure;
        ReplicaDeltas.Enqueue(delta);
    }

    private void AnswerServerDeleted(string conversationId)
    {
        var ok = ServerDeletedResult(conversationId);
        if (ok)
        {
            lock (gate)
            {
                if (conversations.TryGetValue(conversationId, out var c))
                    c.ServerDeleted = true;
            }
        }

        Put(new ServerDeletedWritten(conversationId, ok));
    }
}

/// <summary>測試的一個對話（中樞那邊的狀態）</summary>
internal sealed class FakeConversation(AgentConversation meta)
{
    public AgentConversation Meta { get; } = meta;

    public string Id => Meta.Id;

    public List<AgentChatItem> Items { get; } = [];

    /// <summary>佇列（id、顯示文字；引擎以 RequestQueue 要快照時用）</summary>
    public List<(string Id, string DisplayText)> Queue { get; } = [];

    public long NextOrd { get; set; } = 1;

    public bool Running { get; set; }

    public bool Waiting { get; set; }

    public bool Loaded { get; set; } = true;

    public bool IsDraft { get; set; }

    public bool IsReplica { get; set; }

    public bool ServerDeleted { get; set; }

    /// <summary>第一頁的完整 id 集合可以拿來推斷刪除（false＝同中樞看到 items.json.corrupt-*）</summary>
    public bool RemovalsTrusted { get; set; } = true;

    /// <summary>同中樞的快照：實際的政策含代理種類與伺服器刪除的旁檔</summary>
    public PublishMetaSnapshot Snapshot() => new(
        ChatItemCopyRules.CopyMeta(Meta),
        ServerDeleted ? ConversationSyncPolicies.LocalOnly : SyncProviderRules.ConversationPolicy(Meta),
        Running,
        Waiting,
        Loaded,
        IsDraft,
        IsReplica,
        ServerDeleted);
}
