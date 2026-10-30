using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 一筆項目事件要記進 <c>published_items</c> 的（與 outbox 的列同一個交易寫，WP4.3 計畫 D11）
/// </summary>
/// <param name="ConversationId">線上的對話 id</param>
/// <param name="ItemId">項目 id</param>
/// <param name="Ord">送出的 Ord（<c>conv.itemRemoved</c> 沒有：列與 Ord 保留）</param>
/// <param name="SrcSha256">投影前的摘要（<see cref="PublishDigestRules.Source"/>，對中樞的快照算）</param>
/// <param name="WireSha256">線上的摘要（<see cref="PublishDigestRules.Wire"/>）</param>
/// <param name="Truncated">送出的是截斷過（或換成佔位）的版本（D16）</param>
/// <param name="Removed">這一筆是 <c>conv.itemRemoved</c>：摘要清掉、記「已移除」，Ord 保留（不重用）</param>
internal sealed record PublishRecord(string ConversationId, string? ItemId, long? Ord, string? SrcSha256, string? WireSha256, bool Truncated, bool Removed = false);

/// <summary>投影好的一筆項目：outbox 的一列與它的發布紀錄</summary>
internal sealed record ProjectedItem(OutboxAppend Append, PublishRecord Record);

/// <summary>投影好的中繼資料（<c>conv.created</c>／<c>conv.meta</c>）與它的摘要（<c>publish_meta.meta_sha256</c>，兩種同一個算法）</summary>
internal sealed record ProjectedMeta(OutboxAppend Append, string MetaSha256);

/// <summary>
/// 對話事件的投影（WP4.3 計畫 D1、D16、D18、D34；引擎執行緒，可單獨測）。項目依序：
/// 秘密回答清除（<see cref="SecretAnswerScrubRules"/> 的結果）→ 截斷（<see cref="PublishFitRules.Truncate"/>；<c>Validate</c> 嫌大時預算減半、最多
/// <see cref="PublishFitRules.MaxHalvings"/> 次，再不行換成 <see cref="PublishFitRules.Oversized"/> 的佔位）→ 權杖 DLP（<see cref="CredentialLeakRules.MaskItem"/>）→
/// 等待中的卡片以中樞的快照（含只在記憶體的 RequestKey）重算 <c>requestDigest</c>（MaskItem 照抄存的值，存的可能已經過時）→
/// <see cref="ConversationWire.TryItem"/> → 序列化 → <see cref="ConversationEventRules.Validate(string?, string?)"/> → <see cref="OutboxAppendRules.Problem"/>。
/// 不合格的回 null、記一行原因（不含內容）。中繼資料與佇列的遮蔽在 <see cref="ConversationWire"/> 裡。
/// 即時更新（<see cref="Live"/>）不截斷：遮蔽、投影，太大就不送
/// </summary>
/// <param name="mediaHook">媒體參照的配發與刪除（WP4.7 計畫 D4）；null＝不配參照</param>
internal sealed partial class ConversationProjector(IItemMediaHook? mediaHook = null)
{
    private static readonly IReadOnlyDictionary<string, string> NoScrub = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>媒體參照的接縫（WP4.7）：<see cref="Item"/> 在來源摘要之後配參照、截斷之前先試 blob 分支（ConversationProjector.Blobs.cs），<see cref="ItemRemoved"/>、<see cref="Deleted"/> 刪列</summary>
    public IItemMediaHook? MediaHook { get; } = mediaHook;

    /// <summary>因為不合格而丟掉的筆數（記錄與測試用）</summary>
    public int Dropped { get; private set; }

    /// <summary><see cref="Item"/> 的呼叫次數（每次都跑權杖 DLP；測試看「檔案沒變就不投影」）</summary>
    public int ItemCalls { get; private set; }

    /// <summary>沒有要換掉文字的項目時傳的空表</summary>
    public static IReadOnlyDictionary<string, string> NothingScrubbed => NoScrub;

    /// <summary>
    /// <c>conv.item</c>：<paramref name="snapshot"/> 是中樞的快照（只讀、不改）；<paramref name="scrubbed"/> 是秘密回答清除的結果（項目 id → 要送的文字）。
    /// 還沒配 Ord 的回 null（呼叫端要重取快照，D8）
    /// </summary>
    public ProjectedItem? Item(string wireId, long epoch, AgentChatItem snapshot, IReadOnlyDictionary<string, string> scrubbed, long ver)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scrubbed);
        ItemCalls++;
        if (!ConversationIds.IsItemId(snapshot.Id))
            return Drop(wireId, null, "項目 id 不合格");
        if (snapshot.Ord is not >= 1)
            return Drop(wireId, snapshot.Id, "還沒配 Ord");

        var source = PublishDigestRules.Source(snapshot);
        snapshot = MediaHook?.AssignRefs(wireId, snapshot) ?? snapshot;
        var clean = Scrub(snapshot, scrubbed);
        if (TryBlobs(wireId, epoch, clean, snapshot, source, ver) is { } blobbed)
            return blobbed;
        var fieldBudget = PublishFitRules.FieldBudgetBytes;
        var itemBudget = PublishFitRules.ItemBudgetBytes;
        for (var attempt = 0; attempt <= PublishFitRules.MaxHalvings; attempt++)
        {
            var cut = PublishFitRules.Truncate(clean, fieldBudget, itemBudget, out var truncated);
            var (append, wire, problem) = Build(wireId, epoch, cut, snapshot, ver);
            if (append is not null)
                return new ProjectedItem(append, new PublishRecord(wireId, snapshot.Id, snapshot.Ord, source, PublishDigestRules.Wire(wire!), truncated.Count > 0));
            if (problem != SyncErrorCodes.TooLarge)
                return Drop(wireId, snapshot.Id, problem);
            fieldBudget /= 2;
            itemBudget /= 2;
        }

        // 預算減半到底仍放不下：只送佔位（遠端看得到這裡有一筆、內容在擁有者電腦）
        var (oversized, oversizedWire, oversizedProblem) = Build(wireId, epoch, PublishFitRules.Oversized(clean), snapshot, ver);
        return oversized is null
            ? Drop(wireId, snapshot.Id, oversizedProblem)
            : new ProjectedItem(oversized, new PublishRecord(wireId, snapshot.Id, snapshot.Ord, source, PublishDigestRules.Wire(oversizedWire!), true));
    }

    /// <summary>
    /// 即時更新（<c>LiveRelay</c>，計畫步 11，D21；docs/sync-protocol.md 14.3）：只給執行中的項目。<see cref="CredentialLeakRules.MaskItem"/>
    /// （執行中的項目去掉尾端還沒完的權杖）→ <see cref="ConversationWire.TryItem"/> → 序列化 → 超過 <see cref="LiveRules.MaxLiveBytes"/> 就不送（不截斷，
    /// 等持久事件）→ <see cref="LiveRules.CheckRelay"/>。不合格回 null；太大的只計數（每個片段都會來，不逐筆記錄）
    /// </summary>
    public LiveRelay? Live(string wireId, long epoch, AgentChatItem snapshot, long ver) => Live(wireId, epoch, snapshot, ver, LiveRules.MaxLiveBytes, out _);

    /// <summary>
    /// 同上，上限是 <paramref name="maxBytes"/>（<c>live2</c> 是 <see cref="LiveRules.MaxDeltaItemBytes"/>，<see cref="LiveRules.MaxItemBytes"/>）；
    /// <paramref name="wire"/> 是投影好（遮蔽之後）的整則，增量拿它和上一則比（<see cref="LiveDeltaRules.Encode"/>），不必再解析一次 JSON
    /// </summary>
    public LiveRelay? Live(string wireId, long epoch, AgentChatItem snapshot, long ver, int maxBytes, out JObject? wire)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        wire = null;
        if (snapshot.Status != AgentItemStatus.Running || !ConversationIds.IsItemId(snapshot.Id) || snapshot.Ord is not >= 1)
            return null;
        if (LiveLowerBound(snapshot) > maxBytes)
        {
            // 一定放不下：不做遮蔽與序列化（串流中的大項目每 50 ms 就來一次，整筆遮蔽 1 MB 要約 26 ms；合併前審查 T1）
            LiveTooLarge++;
            return null;
        }

        LiveMasked++;
        var masked = CredentialLeakRules.MaskItem(snapshot);
        if (!ConversationWire.TryItem(masked, ver, out var projected))
            return null;
        var json = ConversationWire.Serialize(projected);
        if (!LiveRules.Fits(json, maxBytes))
        {
            LiveTooLarge++;
            return null;
        }

        var relay = new LiveRelay(wireId, epoch, snapshot.Ord.Value, ver, json);
        if (LiveRules.CheckRelay(relay, out var detail, maxBytes) is { } code)
        {
            Dropped++;
            Log.Warn($"對話 {wireId} 的項目 {snapshot.Id} 的即時更新不合格（{code}：{detail}）：不送");
            return null;
        }

        wire = projected;
        return relay;
    }

    /// <summary>超過 64 KB 而不送的即時更新數（記錄與測試用）</summary>
    public int LiveTooLarge { get; private set; }

    /// <summary>即時更新做了遮蔽與序列化的次數（測試看「一定放不下的不做」）</summary>
    public int LiveMasked { get; private set; }

    /// <summary>
    /// 即時更新的 JSON 位元組數的下限：大欄位的 UTF-8 位元組數總和（只數、不配置；JSON 跳脫只會更長）。遮蔽偶爾讓內容變短，
    /// 誤判的代價只是少送一則可以丟的即時更新（持久事件照送）
    /// </summary>
    private static long LiveLowerBound(AgentChatItem item)
    {
        long bytes = Utf8Bytes(item.Text) + Utf8Bytes(item.Output) + Utf8Bytes(item.Detail);
        if (item.Diff is { } diff)
        {
            foreach (var line in diff)
                bytes += Utf8Bytes(line?.S);
        }

        return bytes;
    }

    private static long Utf8Bytes(string? text) => string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// <c>conv.created</c>（<paramref name="created"/>，沒有合併鍵）或 <c>conv.meta</c>（合併鍵 <c>meta:{id}:{gen}</c>）。
    /// 線上的 <c>syncPolicy</c> 是實際的政策（含代理種類與伺服器刪除的旁檔）；政策不發 meta 類（localOnly）、不進同步的代理種類回 null
    /// </summary>
    public ProjectedMeta? Meta(string wireId, PublishMetaSnapshot s, string generation, bool created)
    {
        ArgumentNullException.ThrowIfNull(s);
        var meta = s.Meta;
        if (!Enum.IsDefined(meta.Provider) || !SyncProviderRules.TryWireName(meta.Provider, out var provider))
            return DropMeta(wireId, "不進同步的代理種類");
        var policy = s.ServerDeleted ? ConversationSyncPolicies.LocalOnly : ConversationSyncPolicies.Interpret(s.EffectivePolicy);
        if (!ConversationSyncPolicies.Publishes(policy, ConversationEventKinds.Meta))
            return DropMeta(wireId, "政策不發布中繼資料");

        var payload = ConversationWire.Meta(meta, provider, s.Running, s.Waiting, s.Models) with { SyncPolicy = policy };
        var json = ConversationWire.Serialize(payload);
        var kind = created ? ConversationEventKinds.Created : ConversationEventKinds.Meta;
        var key = created ? null : PublishKeyRules.Meta(wireId, generation);
        if (Checked(kind, json, key, wireId, ConversationWire.EpochOf(meta), out var problem) is not { } append)
            return DropMeta(wireId, problem);
        return new ProjectedMeta(append, PublishDigestRules.Payload(ConversationEventKinds.Meta, json));
    }

    /// <summary><c>conv.queue</c>（合併鍵 <c>queue:{id}</c>；顯示文字在 <see cref="ConversationWire.Queue"/> 遮蔽與截斷）</summary>
    public OutboxAppend? Queue(string wireId, long epoch, PublishQueueSnapshot q)
    {
        ArgumentNullException.ThrowIfNull(q);
        var json = ConversationWire.Serialize(ConversationWire.Queue(q.Entries));
        var append = Checked(ConversationEventKinds.Queue, json, PublishKeyRules.Queue(wireId), wireId, epoch, out var problem);
        if (append is null)
            Drop(wireId, null, problem);
        return append;
    }

    /// <summary><c>conv.turn</c>（沒有合併鍵）；用量不合格（負數、非有限的金額）時不帶用量</summary>
    public OutboxAppend Turn(string wireId, long epoch, bool started, AgentTurnOutcome? outcome, AgentUsage? usage)
    {
        if (started)
            return Required(ConversationEventKinds.Turn, new ConversationTurnPayload(ConversationTurnPhases.Started), wireId, epoch);
        var result = ConversationTurnOutcomes.From(outcome ?? AgentTurnOutcome.Completed);
        if (usage is not null)
        {
            var withUsage = new ConversationTurnPayload(ConversationTurnPhases.Completed, result,
                new ConversationTurnUsage(usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens, usage.CostUsd));
            if (Checked(ConversationEventKinds.Turn, ConversationWire.Serialize(withUsage), null, wireId, epoch, out _) is { } append)
                return append;
        }

        return Required(ConversationEventKinds.Turn, new ConversationTurnPayload(ConversationTurnPhases.Completed, result), wireId, epoch);
    }

    /// <summary>
    /// <c>conv.attention</c>（沒有合併鍵）；<paramref name="kind"/> 要是 <see cref="ConversationAttentionKinds"/> 的名稱，項目 id 不合格的不帶；
    /// <paramref name="speech"/>＝語音通知念的句子（null＝不帶；超過 <see cref="ConversationEventRules.MaxSpeechLength"/> 的在子句邊界截短（<see cref="VoiceNotifyRules.ForWire"/>），
    /// 截完是空的不帶，伺服器改組只有標題的句子）
    /// </summary>
    public OutboxAppend Attention(string wireId, long epoch, string? kind, string? itemId, string? speech = null)
    {
        if (!ConversationAttentionKinds.IsKnown(kind))
            throw new ArgumentException("不認得的提醒種類", nameof(kind));
        var spoken = VoiceNotifyRules.ForWire(speech);
        return Required(ConversationEventKinds.Attention, new ConversationAttentionPayload(kind!, ConversationIds.IsItemId(itemId) ? itemId : null, spoken), wireId, epoch);
    }

    /// <summary><c>conv.deleted</c>：<c>{}</c>、沒有合併鍵（WP4.7：這個對話的媒體參照一起刪）</summary>
    public OutboxAppend Deleted(string wireId, long epoch)
    {
        MediaHook?.Forget(wireId, null);
        return Required(ConversationEventKinds.Deleted, new ConversationDeletedPayload(), wireId, epoch);
    }

    /// <summary><c>conv.itemRemoved</c>（合併鍵與 <c>conv.item</c> 共用 <c>item:{id}:{itemId}</c>，最新的狀態勝；WP4.7：這個項目的媒體參照一起刪）</summary>
    public OutboxAppend ItemRemoved(string wireId, long epoch, string itemId)
    {
        MediaHook?.Forget(wireId, itemId);
        return Required(ConversationEventKinds.ItemRemoved, new ConversationItemRemovedPayload(itemId), wireId, epoch, PublishKeyRules.Item(wireId, itemId));
    }

    private static AgentChatItem Scrub(AgentChatItem snapshot, IReadOnlyDictionary<string, string> scrubbed)
    {
        if (!scrubbed.TryGetValue(snapshot.Id, out var text) || string.Equals(text, snapshot.Text, StringComparison.Ordinal))
            return snapshot;
        var copy = ChatItemCopyRules.Copy(snapshot);
        copy.Text = text;
        return copy;
    }

    /// <summary>遮蔽、重算等待中卡片的摘要、投影、序列化、驗證；不合格回原因（<see cref="SyncErrorCodes.TooLarge"/> 時呼叫端減半重試）</summary>
    private (OutboxAppend? Append, JObject? Wire, string? Problem) Build(string wireId, long epoch, AgentChatItem item, AgentChatItem snapshot, long ver)
    {
        var masked = CredentialLeakRules.MaskItem(item);
        if (snapshot.Status == AgentItemStatus.Waiting)
            masked.RequestDigest = RequestDigestRules.Compute(snapshot);
        if (!ConversationWire.TryItem(masked, ver, out var wire))
            return (null, null, "投影不出線上格式（種類或狀態不認得）");
        var json = ConversationWire.Serialize(wire);
        var append = Checked(ConversationEventKinds.Item, json, PublishKeyRules.Item(wireId, snapshot.Id), wireId, epoch, out var problem);
        return (append, append is null ? null : wire, problem);
    }

    /// <summary>驗證 payload（Validate）與外框（OutboxAppendRules.Problem）；不合格回 null 與原因</summary>
    private static OutboxAppend? Checked(string kind, string json, string? key, string wireId, long epoch, out string? problem)
    {
        if (ConversationEventRules.Validate(kind, json, out var detail) is { } code)
        {
            problem = code == SyncErrorCodes.TooLarge ? code : $"{code}（{detail}）";
            return null;
        }

        var append = new OutboxAppend(SyncStreams.Conversation(wireId), kind, json, key, wireId, epoch);
        problem = OutboxAppendRules.Problem(append);
        return problem is null ? append : null;
    }

    /// <summary>一定合格的事件（turn、attention、deleted、itemRemoved）；不合格是程式錯誤</summary>
    private static OutboxAppend Required(string kind, object payload, string wireId, long epoch, string? key = null)
        => Checked(kind, ConversationWire.Serialize(payload), key, wireId, epoch, out var problem)
           ?? throw new InvalidOperationException($"{kind} 的投影不合格：{problem}");

    private ProjectedItem? Drop(string wireId, string? itemId, string? reason)
    {
        Dropped++;
        Log.Warn($"對話 {wireId} 的項目 {(ConversationIds.IsItemId(itemId) ? itemId : "?")} 不發布：{reason}");
        return null;
    }

    private ProjectedMeta? DropMeta(string wireId, string? reason)
    {
        Dropped++;
        Log.Info($"對話 {wireId} 的中繼資料不發布：{reason}");
        return null;
    }
}
