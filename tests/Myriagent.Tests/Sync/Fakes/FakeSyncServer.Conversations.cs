using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Fakes;

// WP4.3 計畫步 10：假伺服器照 WP4.2 的定案（6.3～6.5）處理對話事件。第一次取用 Conversations 時接上；沒接上時對話事件照第 3 階段的
// Materialize（WP4.1 的測試不受影響）
internal sealed partial class FakeSyncServer
{
    private FakeConversations? conversations;

    /// <summary>對話事件的模擬（第一次取用時接上，之後每筆對話事件都由它決定接受或擋下）</summary>
    public FakeConversations Conversations => conversations ??= new FakeConversations(DeviceId);

    /// <summary>在伺服器的鎖內、這筆分到 seq 之後呼叫：不是對話事件或沒接上模擬時回 null（照 Materialize）</summary>
    private BatchEventResult? MaterializeConversation(SyncEvent e)
        => conversations is { } model && ConversationEventKinds.IsConversationKind(e.Kind) ? model.Apply(e) : null;
}

/// <summary>
/// 照 WP4.2 的定案模擬伺服器的對話（6.3 的 C7／P2、C8、C10，6.4 的拒絕碼）：
/// <list type="bullet">
/// <item>未知對話的 <c>conv.created</c>、<c>conv.meta</c> 當成建立（作者成為擁有者、epoch 取事件上的值）；未知對話的其他種類回 <c>invalid</c>、不建列。</item>
/// <item><c>{}</c> 的對話事件除了 <c>conv.deleted</c> 都回 <c>invalid</c>；<c>{}</c> 的 <c>conv.deleted</c> 是真的刪除。</item>
/// <item>作者不是擁有者回 <c>not_owner</c>（帶 Owner）；epoch 不符回 <c>fenced</c>（帶 Epoch）；已刪除的對話回 <c>deleted</c>（WP4.2 的判斷順序）。</item>
/// <item>政策不是 full 時 item 類回 <c>invalid</c>；政策是 localOnly 的 <c>conv.meta</c> 回 <c>invalid</c>；full 降成 metadata／paused 的 meta 清掉內容（項目與佇列，同 WP4.2 的 PurgeContent）。</item>
/// <item>伺服器的第二道 DLP（答 5）：命中時遮蔽後接受（結果和沒命中一樣，只計數 <see cref="Remasked"/>）。</item>
/// <item><see cref="OldServer"/>：1724718 的伺服器，所有對話種類一律 <c>invalid</c>。</item>
/// </list>
/// 被擋下的事件記下 payload（<see cref="Rejected"/>：舊伺服器的 rejected_events 存的內容）。執行緒安全
/// </summary>
internal sealed class FakeConversations(string deviceId)
{
    private readonly object gate = new();
    private readonly Dictionary<string, State> states = new(StringComparer.Ordinal);
    private readonly List<RejectedEvent> rejected = [];
    private readonly List<SyncEvent> accepted = [];

    // 清掉內容的 meta 在 accepted 裡的位置（Items 依處理順序重算時，到這裡清空那個對話）
    private readonly HashSet<int> purges = [];
    private int remasked;

    /// <summary>伺服器上的一個對話</summary>
    public sealed record State(string Owner, long Epoch, string Policy, bool Deleted);

    /// <summary>被擋下的一筆（種類、解碼後的 payload、原因）</summary>
    public sealed record RejectedEvent(string Kind, string ConversationId, string Payload, string Code);

    /// <summary>1724718 的伺服器：所有對話種類一律 invalid（rejected_events 照存 payload）</summary>
    public bool OldServer { get; set; }

    /// <summary>第二道 DLP 比桌面多認的樣式（模擬）：每一筆合格的事件都當成命中，遮蔽後接受</summary>
    public bool RemaskAll { get; set; }

    /// <summary>測試強制的拒絕（回原因；null＝照規則）</summary>
    public Func<SyncEvent, string?>? Reject { get; set; }

    public IReadOnlyList<RejectedEvent> Rejected
    {
        get
        {
            lock (gate)
                return rejected.ToList();
        }
    }

    /// <summary>接受的對話事件（依處理順序）</summary>
    public IReadOnlyList<SyncEvent> Accepted
    {
        get
        {
            lock (gate)
                return accepted.ToList();
        }
    }

    /// <summary>命中第二道 DLP、遮蔽後接受的筆數</summary>
    public int Remasked
    {
        get
        {
            lock (gate)
                return remasked;
        }
    }

    public State? this[string conversationId]
    {
        get
        {
            lock (gate)
                return states.GetValueOrDefault(conversationId);
        }
    }

    /// <summary>
    /// 伺服器上這個對話目前的項目（項目 id → Ord）：依處理順序套用接受的 conv.item、conv.itemRemoved，conv.deleted 與降級的 meta 清空（WP4.3 步 12 的
    /// 「送完後 id 與 Ord 的集合等於本機」）
    /// </summary>
    public IReadOnlyDictionary<string, long> Items(string conversationId)
    {
        List<SyncEvent> events;
        HashSet<int> purged;
        lock (gate)
        {
            events = accepted.ToList();
            purged = [.. purges];
        }

        var items = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var index = 0; index < events.Count; index++)
        {
            var e = events[index];
            if (e.ConversationId != conversationId || !PayloadRules.TryDecode(e.Enc, e.Payload, 16 << 20, out var json))
                continue;
            if (purged.Contains(index))
            {
                items.Clear();
                continue;
            }

            switch (e.Kind)
            {
                case ConversationEventKinds.Item:
                {
                    var o = JObject.Parse(json);
                    items[o["id"]!.Value<string>()!] = o["ord"]!.Value<long>();
                    break;
                }

                case ConversationEventKinds.ItemRemoved:
                    items.Remove(JObject.Parse(json)["id"]!.Value<string>()!);
                    break;
                case ConversationEventKinds.Deleted:
                    items.Clear();
                    break;
            }
        }

        return items;
    }

    /// <summary>伺服器換了實例（重建，什麼都沒有）：對話、接受與擋下的紀錄全部清掉</summary>
    public void Clear()
    {
        lock (gate)
        {
            states.Clear();
            accepted.Clear();
            rejected.Clear();
            purges.Clear();
            remasked = 0;
        }
    }

    /// <summary>預先放一個對話（例如重新配對前的舊身分擁有的）</summary>
    public void Seed(string conversationId, string owner, long epoch = 1, string policy = ConversationSyncPolicies.Full, bool deleted = false)
    {
        lock (gate)
            states[conversationId] = new State(owner, epoch, policy, deleted);
    }

    public BatchEventResult Apply(SyncEvent e)
    {
        var json = PayloadRules.TryDecode(e.Enc, e.Payload, 16 << 20, out var text) ? text : null;
        lock (gate)
        {
            string? code;
            long? epoch = null;
            string? owner = null;
            var purge = false;
            if (OldServer)
                code = SyncErrorCodes.Invalid;
            else if (Reject?.Invoke(e) is { } forced)
                code = forced;
            else
                code = Evaluate(e, json, out epoch, out owner, out purge);
            if (code is not null)
            {
                rejected.Add(new RejectedEvent(e.Kind, e.ConversationId ?? string.Empty, json ?? string.Empty, code));
                return new BatchEventResult(e.DeviceSeq, Rejected: code, Epoch: epoch, Owner: owner);
            }

            if (purge)
                purges.Add(accepted.Count);
            accepted.Add(e);
            return new BatchEventResult(e.DeviceSeq);
        }
    }

    private string? Evaluate(SyncEvent e, string? json, out long? epoch, out string? owner, out bool purge)
    {
        epoch = null;
        owner = null;
        purge = false;
        if (json is null || ConversationEventRules.CheckEvent(e.Kind, e.Stream, e.ConversationId, e.Epoch) is not null)
            return SyncErrorCodes.Invalid;
        if (json.Trim() == "{}" && e.Kind != ConversationEventKinds.Deleted)
            return SyncErrorCodes.Invalid;
        if (ConversationEventRules.Validate(e.Kind, json) is { } invalid)
            return invalid;
        if (RemaskAll || CredentialLeakRules.HasLeak(json))
            remasked++;

        var id = e.ConversationId!;
        var isMeta = e.Kind is ConversationEventKinds.Created or ConversationEventKinds.Meta;
        if (!states.TryGetValue(id, out var state))
        {
            if (!isMeta)
                return SyncErrorCodes.Invalid;
            var created = PolicyOf(json);
            if (created == ConversationSyncPolicies.LocalOnly)
                return SyncErrorCodes.Invalid;
            states[id] = new State(deviceId, e.Epoch!.Value, created, false);
            return null;
        }

        if (state.Owner != deviceId)
        {
            owner = state.Owner;
            return ConversationRejectCodes.NotOwner;
        }

        if (state.Epoch != e.Epoch)
        {
            epoch = state.Epoch;
            return ConversationRejectCodes.Fenced;
        }

        if (state.Deleted)
            return ConversationRejectCodes.Deleted;
        if (isMeta)
        {
            var policy = PolicyOf(json);
            if (policy == ConversationSyncPolicies.LocalOnly)
                return SyncErrorCodes.Invalid;
            purge = state.Policy == ConversationSyncPolicies.Full && policy is ConversationSyncPolicies.Metadata or ConversationSyncPolicies.Paused;
            states[id] = state with { Policy = policy };
            return null;
        }

        if (e.Kind == ConversationEventKinds.Deleted)
        {
            states[id] = state with { Deleted = true };
            return null;
        }

        return ConversationEventKinds.ClassOf(e.Kind) == SyncClasses.Item && state.Policy != ConversationSyncPolicies.Full ? SyncErrorCodes.Invalid : null;
    }

    private static string PolicyOf(string json) => JObject.Parse(json)["syncPolicy"]?.Value<string>() ?? ConversationSyncPolicies.Full;
}
