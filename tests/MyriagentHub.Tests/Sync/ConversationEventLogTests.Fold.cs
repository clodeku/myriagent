using System.Security.Cryptography;
using System.Text;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using K = Myriagent.Sync.ConversationEventKinds;
using P = Myriagent.Sync.ConversationSyncPolicies;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 隨機事件序列的物化正確性（WP4.2 計畫步 3、第 7 節「物化正確性」）：種子固定、事件依 seq 摺疊進一份獨立寫的模型（不呼叫
/// <see cref="ConversationApplyRules"/>），逐欄比對三張物化表；最後把三張表依主鍵排序輸出成 JSON，印出筆數與 SHA-256 當指紋（記進計畫附錄 B）。
/// 命中 DLP 的事件，模型以遮蔽版摺疊（<see cref="ConversationRemaskRules.Remask"/>：遮蔽規則本身由 ConversationRemaskRulesTests 驗）
/// </summary>
public sealed partial class ConversationEventLogTests
{
    private const int FoldSeed = 4242;

    private const int FoldEventCount = 2000;

    private const string FoldConversationColumns =
        "conversation_id, provider, logical_account_id, origin_device_id, owner_device_id, epoch, lease_state, meta_json, header_json, title, created_at, updated_at, "
        + "last_seq, item_count, max_ord, running, waiting, status_text, turn_started_at, last_turn, sync_policy, transcript, forked_from, fork_point_seq, deleted_at, deleted_by";

    private const string FoldItemColumns = "conversation_id, item_id, ord, last_seq, epoch, ver, kind, status, final, origin_device_id, enc, body, removed";

    private const string FoldRuntimeColumns = "conversation_id, queue_json, pending_attachments_json, busy, busy_text, version";

    internal static readonly string[] FoldDevices = ["d_0000000000000000000000000A", "d_0000000000000000000000000B"];

    private static readonly string[] FoldStatuses = ["Running", "Waiting", "Done", "Failed", "Declined", "Cancelled"];

    private static readonly string[] FoldItemKinds = ["Assistant", "User", "Tool", "Notice"];

    private static readonly string[] FoldAttentionKinds = ["question", "approval", "plan", "completed", "failed"];

    [Fact]
    public async Task 隨機事件序列_物化等於依seq摺疊()
    {
        await using var host = await HubTestHost.StartAsync();
        var (model, stats) = await RunFoldAsync(host);

        AssertMatchesModel(host, model);

        // 每一條路都走過：接受、再遮蔽後接受、四種被拒
        foreach (var key in new[] { "accepted", "accepted(remasked)", ConversationRejectCodes.NotOwner, ConversationRejectCodes.Fenced, ConversationRejectCodes.Deleted, SyncErrorCodes.Invalid })
            stats.GetValueOrDefault(key).Should().BeGreaterThan(0, key);
        model.Items.Values.Should().Contain(i => i.Removed).And.Contain(i => !i.Removed);
        model.PolicyRejections.Should().BeGreaterThan(0, "政策不是 full 時的 item 類事件");
        model.Purges.Should().BeGreaterThan(0, "降級與刪除清掉內容（答 4）");

        // 拉取（D17）：item 類只回目前 full、沒刪除的對話的事件；meta 類全部照回
        model.Accepted.Should().Contain(x => x.Item && !model.Visible(x.Conversation), "有被過濾的 item 類事件");
        var log = host.Services.GetRequiredService<EventLog>();
        PullAll(log, SyncClasses.Item).Should().Equal(model.Accepted.Where(a => a.Item && model.Visible(a.Conversation)).Select(a => a.Seq), "item 類依目前的狀態過濾");
        PullAll(log, SyncClasses.Meta).Should().Equal(model.Accepted.Where(a => !a.Item).Select(a => a.Seq), "meta 類照回");

        var dump = FoldDump(host);
        output.WriteLine($"物化指紋（種子 {FoldSeed}、{FoldEventCount} 筆）：" + dump.Describe());
        output.WriteLine("結果分布：" + string.Join("、", stats.Select(kv => $"{kv.Key} {kv.Value}")));
    }

    /// <summary>一次隨機序列的結果：摺疊模型與每種結果的筆數</summary>
    internal sealed record FoldRun(FoldModel Model, SortedDictionary<string, int> Stats);

    /// <summary>三張物化表依主鍵排序的內容與指紋（筆數＋SHA-256；計畫附錄 B）</summary>
    internal sealed record FoldSnapshot(List<object?[]> Conversations, List<object?[]> Items, List<object?[]> Runtime, string Sha)
    {
        public string Describe() =>
            $"conversations {Conversations.Count} 列、items {Items.Count} 列、conversation_runtime {Runtime.Count} 列；SHA-256 {Sha}";
    }

    /// <summary>
    /// 送出種子固定的隨機事件序列（兩台固定 id 的裝置、五個對話），每筆的結果與摺疊模型的判斷比對；重建的測試以同一份序列比對指紋（步 5）
    /// </summary>
    internal static async Task<FoldRun> RunFoldAsync(HubTestHost host)
    {
        foreach (var id in FoldDevices)
            await AddFixedDeviceAsync(host, id);
        var conversations = Enumerable.Range(0, 5).Select(i => new string("abcde"[i], 32)).ToArray();
        var rng = new Random(FoldSeed);
        var model = new FoldModel(host.Clock.NowMs);
        var nextDeviceSeq = new long[FoldDevices.Length];
        Array.Fill(nextDeviceSeq, 1);
        var stats = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var sent = 0;
        while (sent < FoldEventCount)
        {
            var d = rng.Next(FoldDevices.Length);
            var count = Math.Min(rng.Next(1, 16), FoldEventCount - sent);
            var generated = new List<FoldEvent>(count);
            for (var i = 0; i < count; i++)
                generated.Add(GenerateFoldEvent(rng, conversations, nextDeviceSeq[d]++, sent + i));

            var outcome = await EventLogTests.AppendAsync(host, FoldDevices[d], EventLogTests.Batch([.. generated.Select(g => g.Event)]));

            outcome.Ack.Error.Should().BeNull();
            for (var i = 0; i < count; i++)
            {
                var g = generated[i];
                var result = outcome.Ack.Results[i];
                var expected = model.Decide(FoldDevices[d], g);
                result.Rejected.Should().Be(expected, $"第 {sent + i} 筆（{g.Label}）");
                if (expected is null)
                {
                    result.Seq.Should().NotBeNull();
                    model.Fold(FoldDevices[d], g, result.Seq!.Value);
                }
                var key = expected ?? (g.Remasked ? "accepted(remasked)" : "accepted");
                stats[key] = stats.GetValueOrDefault(key) + 1;
            }
            sent += count;
        }
        return new FoldRun(model, stats);
    }

    /// <summary>三張物化表逐列逐欄等於摺疊模型（items 的 body 解碼後比；對話的 soak 也用，WP4.2 計畫步 14）</summary>
    internal static void AssertMatchesModel(HubTestHost host, FoldModel model)
    {
        var conversationRows = Rows(host, $"SELECT {FoldConversationColumns} FROM conversations ORDER BY conversation_id");
        conversationRows.Select(r => (string)r[0]!).Should().Equal(model.Conversations.Keys.Order(StringComparer.Ordinal));
        foreach (var row in conversationRows)
            row.Should().Equal(model.ConversationRow((string)row[0]!), $"conversations 的 {row[0]}");
        var itemRows = Rows(host, $"SELECT {FoldItemColumns} FROM items ORDER BY conversation_id, item_id");
        itemRows.Select(r => ((string)r[0]!, (string)r[1]!)).Should().Equal(
            model.Items.Keys.OrderBy(k => k.Conversation, StringComparer.Ordinal).ThenBy(k => k.Item, StringComparer.Ordinal).Select(k => (k.Conversation, k.Item)));
        foreach (var row in itemRows)
        {
            var actual = row.ToArray();
            actual[11] = PayloadRules.TryDecode((string)row[10]!, (string)row[11]!, 1 << 22, out var json) ? json : null;
            actual.Should().Equal(model.ItemRow((string)row[0]!, (string)row[1]!), $"items 的 {row[0]}／{row[1]}");
        }
        var runtimeRows = Rows(host, $"SELECT {FoldRuntimeColumns} FROM conversation_runtime ORDER BY conversation_id");
        runtimeRows.Select(r => (string)r[0]!).Should().Equal(model.Runtime.Keys.Order(StringComparer.Ordinal));
        foreach (var row in runtimeRows)
            row.Should().Equal(model.RuntimeRow((string)row[0]!), $"conversation_runtime 的 {row[0]}");
    }

    /// <summary>三張物化表依主鍵排序輸出成 JSON 的指紋（items 的 body 照存下的編碼）</summary>
    internal static FoldSnapshot FoldDump(HubTestHost host)
    {
        var conversationRows = Rows(host, $"SELECT {FoldConversationColumns} FROM conversations ORDER BY conversation_id");
        var itemRows = Rows(host, $"SELECT {FoldItemColumns} FROM items ORDER BY conversation_id, item_id");
        var runtimeRows = Rows(host, $"SELECT {FoldRuntimeColumns} FROM conversation_runtime ORDER BY conversation_id");
        var dump = JsonConvert.SerializeObject(new { conversations = conversationRows, items = itemRows, conversation_runtime = runtimeRows }, Formatting.None);
        return new FoldSnapshot(conversationRows, itemRows, runtimeRows, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dump))));
    }

    /// <summary>依類別游標從頭拉到底，回傳每筆的 seq（每次最多 100 筆，照 cls 前進游標）</summary>
    internal static List<long> PullAll(EventLog log, string cls)
    {
        var seqs = new List<long>();
        var after = 0L;
        while (true)
        {
            var page = log.Pull(new PullRequest([new PullCursor(after, Cls: cls)], 100, 1_000_000));
            seqs.AddRange(page.Events.Select(e => e.Seq));
            if (page.Events.Count > 0)
                after = page.Events[^1].Seq;
            if (!page.More)
                return seqs;
        }
    }

    /// <summary>一筆產生的事件：<see cref="Bad"/>＝預先檢查就會被擋；<see cref="StoredJson"/>＝伺服器存下的版本（命中 DLP 時是遮蔽版）</summary>
    internal sealed record FoldEvent(SyncEvent Event, string Label, bool Bad, bool Remasked, JObject? Stored, string? StoredJson);

    private static FoldEvent GenerateFoldEvent(Random rng, string[] conversations, long deviceSeq, int index)
    {
        var conversationId = conversations[rng.Next(conversations.Length)];
        var r = rng.Next(100);
        if (r < 14)
        {
            var kind = r < 8 ? K.Created : K.Meta;
            var roll = rng.Next(100);
            var policy = roll < 85 ? P.Full : roll < 92 ? P.Metadata : roll < 97 ? P.Paused : P.LocalOnly;
            var meta = Meta("標題" + rng.Next(100), policy);
            meta["createdAt"] = rng.Next(20) == 0 ? 1_900_000_000_000L : 1_760_000_000_000L + rng.Next(1_000_000) * 1000L;
            if (rng.Next(10) == 0)
                meta.Remove("updatedAt");
            else
                meta["updatedAt"] = rng.Next(20) == 0 ? 1_900_000_000_000L : 1_770_000_000_000L + rng.Next(1_000_000) * 1000L;
            meta["running"] = rng.Next(2) == 0;
            meta["waiting"] = rng.Next(4) == 0;
            var token = rng.Next(100) < 3;
            if (token)
                meta["title"] = "標題 " + GitHub((char)('a' + rng.Next(26)));
            // localOnly 的 meta 在 Validate 就不合格（localOnly 的對話不發布）
            return Make(kind, meta, conversationId, deviceSeq, $"{kind} {policy}", bad: policy == P.LocalOnly, token: token);
        }
        if (r < 20)
        {
            var started = rng.Next(2) == 0;
            return Make(K.Turn, Turn(started ? ConversationTurnPhases.Started : ConversationTurnPhases.Completed), conversationId, deviceSeq, K.Turn);
        }
        if (r < 25)
        {
            // 刪除只在後段出現（刪了就不能改回，太早刪會讓之後的事件全部是 deleted）
            if (r >= 23 && index > 1400)
                return Make(K.Deleted, new JObject(), conversationId, deviceSeq, K.Deleted);
            return Make(K.Attention, new JObject { ["kind"] = FoldAttentionKinds[rng.Next(FoldAttentionKinds.Length)] }, conversationId, deviceSeq, K.Attention);
        }
        if (r < 70)
        {
            var (item, token) = FoldItem(rng);
            return Make(K.Item, item, conversationId, deviceSeq, K.Item, token: token);
        }
        if (r < 78)
            return Make(K.ItemRemoved, Removal("item" + rng.Next(13)), conversationId, deviceSeq, K.ItemRemoved);
        if (r < 85)
        {
            var texts = Enumerable.Range(0, rng.Next(1, 4)).Select(_ => "待辦" + rng.Next(100)).ToArray();
            return Make(K.Queue, Queue(texts), conversationId, deviceSeq, K.Queue);
        }
        if (r < 90)
        {
            var (item, _) = FoldItem(rng, allowToken: false);
            var json = Json(item);
            switch (rng.Next(5))
            {
                case 0:
                    return new FoldEvent(Event(K.Item, json, conversationId.ToUpperInvariant(), deviceSeq: deviceSeq), "大寫 id", true, false, null, null);
                case 1:
                    var other = conversations[(Array.IndexOf(conversations, conversationId) + 1) % conversations.Length];
                    return new FoldEvent(Event(K.Item, json, conversationId, stream: SyncStreams.Conversation(other), deviceSeq: deviceSeq), "串流不符", true, false, null, null);
                case 2:
                    return new FoldEvent(Event(K.Item, json, conversationId, deviceSeq: deviceSeq, sha: new string('0', 64)), "雜湊不符", true, false, null, null);
                case 3:
                    item["path"] = "C:/secret.txt";
                    return Make(K.Item, item, conversationId, deviceSeq, "path 鍵", bad: true);
                default:
                    item["status"] = "Bogus";
                    return Make(K.Item, item, conversationId, deviceSeq, "不認得的 status", bad: true);
            }
        }
        var (fenced, _) = FoldItem(rng, allowToken: false);
        return Make(K.Item, fenced, conversationId, deviceSeq, "epoch 2 的 item", epoch: 2);
    }

    private static (JObject Item, bool Token) FoldItem(Random rng, bool allowToken = true)
    {
        var text = "訊息" + rng.Next(1000);
        var token = allowToken && rng.Next(100) < 6;
        if (token)
            text += " " + GitHub((char)('a' + rng.Next(26)));
        var item = Item(text, FoldStatuses[rng.Next(FoldStatuses.Length)], "item" + rng.Next(10), rng.Next(1, 41), rng.Next(1, 10));
        item["kind"] = FoldItemKinds[rng.Next(FoldItemKinds.Length)];
        return (item, token);
    }

    /// <summary>一筆事件與伺服器會存下的版本（<paramref name="token"/>＝內容有權杖：存遮蔽版；<paramref name="bad"/>＝預先檢查就會被擋）</summary>
    internal static FoldEvent Make(string kind, JObject payload, string conversationId, long deviceSeq, string label, long epoch = 1, bool bad = false, bool token = false)
    {
        var json = Json(payload);
        var e = Event(kind, json, conversationId, epoch, deviceSeq: deviceSeq);
        if (bad)
            return new FoldEvent(e, label, true, false, null, null);
        ConversationEventRules.TryParseStrict(json, out var parsed).Should().BeTrue();
        var stored = token ? ConversationRemaskRules.Remask(kind, (JObject)parsed!) : json;
        return new FoldEvent(e, label + (token ? "（含權杖）" : string.Empty), false, token, JObject.Parse(stored), stored);
    }

    internal static async Task AddFixedDeviceAsync(HubTestHost host, string id)
    {
        var record = new DeviceRecord
        {
            Id = id,
            Name = "摺疊測試",
            TokenHash = Guid.NewGuid().ToByteArray(),
            DevicePubkey = [1, 2, 3, 4],
            KeyStorage = "software",
            CreatedAt = 1,
        };
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
            db.Devices.Add(record);
            await db.SaveChangesAsync();
        }
        host.Services.GetRequiredService<DeviceDirectory>().Put(DeviceInfo.From(record));
    }

    /// <summary>
    /// 獨立的摺疊模型（照設計 3.2 與計畫 D4、D5、D7、D9、D10 的文字寫，不呼叫伺服器的授權與物化）：依 seq 一筆一筆套用，最後輸出和三張表相同欄位順序的列
    /// </summary>
    internal sealed class FoldModel(long start)
    {
        /// <summary>沒給收到時間時用的固定時間</summary>
        private readonly long fixedNow = start;

        /// <summary>目前這一筆的收到時間（<see cref="Fold"/> 設定）</summary>
        private long now = start;

        public Dictionary<string, FoldConversation> Conversations { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string Conversation, string Item), FoldItemRow> Items { get; } = [];

        public Dictionary<string, (string QueueJson, long Version)> Runtime { get; } = new(StringComparer.Ordinal);

        /// <summary>因為政策不是 full 而被拒的 item 類事件數（確認這條路走過）</summary>
        public int PolicyRejections { get; private set; }

        /// <summary>伺服器應回的拒絕碼；null＝接受</summary>
        public string? Decide(string device, FoldEvent g)
        {
            if (g.Bad)
                return SyncErrorCodes.Invalid;
            var e = g.Event;
            var isMeta = e.Kind is K.Created or K.Meta;
            var isItemClass = e.Kind is K.Item or K.ItemRemoved or K.Queue;
            if (!Conversations.TryGetValue(e.ConversationId!, out var row))
                return isMeta ? null : SyncErrorCodes.Invalid;
            if (row.Owner != device)
                return ConversationRejectCodes.NotOwner;
            if (row.Epoch != e.Epoch)
                return ConversationRejectCodes.Fenced;
            if (row.DeletedAt is not null)
                return ConversationRejectCodes.Deleted;
            if (isItemClass && row.Policy != P.Full)
            {
                PolicyRejections++;
                return SyncErrorCodes.Invalid;
            }
            return null;
        }

        /// <summary>被接受的事件，依 seq（<c>Item</c>＝item 類）</summary>
        public List<(long Seq, string Conversation, bool Item)> Accepted { get; } = [];

        /// <summary>拉取時回不回這個對話的 item 類事件：目前政策是 full、沒刪除（D17）</summary>
        public bool Visible(string conversationId) =>
            Conversations[conversationId] is { Policy: P.Full, DeletedAt: null };

        /// <param name="at">
        /// 伺服器收到這筆的時間（<c>events.created_at</c>，同一個交易的物化也用它）；null＝建構時給的固定時間（測試主機的假時鐘不動）。
        /// 子程序用真的時鐘時（對話的 soak，WP4.2 計畫步 14）每筆不同
        /// </param>
        public void Fold(string device, FoldEvent g, long seq, long? at = null)
        {
            now = at ?? fixedNow;
            var e = g.Event;
            var c = e.ConversationId!;
            var p = g.Stored!;
            var json = g.StoredJson!;
            Accepted.Add((seq, c, e.Kind is K.Item or K.ItemRemoved or K.Queue));
            if (e.Kind is K.Created or K.Meta)
            {
                if (!Conversations.TryGetValue(c, out var created))
                {
                    created = new FoldConversation { Owner = device, Origin = device, Epoch = e.Epoch!.Value, CreatedAt = Display(p["createdAt"]) };
                    Conversations[c] = created;
                }
                created.Provider = (string)p["provider"]!;
                created.Title = (string)p["title"]!;
                created.MetaJson = json;
                created.UpdatedAt = Display(p["updatedAt"]);
                created.Running = (bool)p["running"]!;
                created.Waiting = (bool)p["waiting"]!;
                var policy = (string)p["syncPolicy"]!;
                // 答 4：從 full 降成只同步標題或暫停，伺服器上的內容立刻清掉（改回 full 由擁有者整份重送）
                if (created.Policy == P.Full && policy is P.Metadata or P.Paused)
                    Purge(c, created);
                created.Policy = policy;
                created.LastSeq = seq;
                return;
            }

            var row = Conversations[c];
            row.LastSeq = seq;
            switch (e.Kind)
            {
                case K.Turn when (string)p["phase"]! == ConversationTurnPhases.Started:
                    row.Running = true;
                    row.TurnStartedAt = now;
                    break;
                case K.Turn:
                    row.Running = false;
                    row.LastTurn = json;
                    break;
                case K.Deleted:
                    // 答 4：在伺服器上刪除＝墓碑留著、內容清掉
                    row.DeletedAt = now;
                    row.DeletedBy = device;
                    Purge(c, row);
                    break;
                case K.Item:
                {
                    var key = (c, (string)p["id"]!);
                    var wasLive = Items.TryGetValue(key, out var before) && !before.Removed;
                    var status = (string)p["status"]!;
                    var ord = (long)p["ord"]!;
                    Items[key] = new FoldItemRow
                    {
                        Ord = ord, LastSeq = seq, Epoch = e.Epoch!.Value, Ver = (long)p["ver"]!, Kind = (string)p["kind"]!, Status = status,
                        Final = status is not ("Running" or "Waiting"), Origin = device, Body = json,
                    };
                    row.MaxOrd = Math.Max(row.MaxOrd, ord);
                    if (!wasLive)
                        row.ItemCount++;
                    break;
                }
                case K.ItemRemoved:
                    if (Items.TryGetValue((c, (string)p["id"]!), out var removed))
                    {
                        if (!removed.Removed)
                        {
                            removed.Removed = true;
                            row.ItemCount--;
                        }
                        removed.LastSeq = seq;
                    }
                    break;
                case K.Queue:
                    Runtime[c] = (json, Runtime.TryGetValue(c, out var queue) ? queue.Version + 1 : 1);
                    break;
            }
        }

        public object?[] ConversationRow(string id)
        {
            var c = Conversations[id];
            return
            [
                id, c.Provider, null, c.Origin, c.Owner, c.Epoch, ConversationLeaseStates.Owned, c.MetaJson, null, c.Title, c.CreatedAt, c.UpdatedAt,
                c.LastSeq, c.ItemCount, c.MaxOrd, c.Running ? 1L : 0L, c.Waiting ? 1L : 0L, null, c.TurnStartedAt, c.LastTurn, c.Policy, null, null, null,
                c.DeletedAt, c.DeletedBy,
            ];
        }

        public object?[] ItemRow(string conversationId, string itemId)
        {
            var i = Items[(conversationId, itemId)];
            return
            [
                conversationId, itemId, i.Ord, i.LastSeq, i.Epoch, i.Ver, i.Kind, i.Status, i.Final ? 1L : 0L, i.Origin, PayloadRules.Encode(i.Body).Enc, i.Body,
                i.Removed ? 1L : 0L,
            ];
        }

        public object?[] RuntimeRow(string id)
        {
            var (queueJson, version) = Runtime[id];
            return [id, queueJson, "[]", 0L, null, version];
        }

        /// <summary>這個對話被清掉內容的次數（降級與刪除；確認這條路走過）</summary>
        public int Purges { get; private set; }

        private long Display(JToken? deviceTime) => Math.Min((long?)deviceTime ?? now, now);

        /// <summary>清掉一個對話的項目與佇列，項目數歸 0、最大 Ord 不變</summary>
        private void Purge(string conversationId, FoldConversation row)
        {
            foreach (var key in Items.Keys.Where(k => k.Conversation == conversationId).ToList())
                Items.Remove(key);
            Runtime.Remove(conversationId);
            row.ItemCount = 0;
            Purges++;
        }
    }

    internal sealed class FoldConversation
    {
        public required string Owner { get; init; }

        public required string Origin { get; init; }

        public required long Epoch { get; init; }

        public required long CreatedAt { get; init; }

        public string Provider { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string MetaJson { get; set; } = string.Empty;

        public long UpdatedAt { get; set; }

        public long LastSeq { get; set; }

        public long ItemCount { get; set; }

        public long MaxOrd { get; set; }

        public bool Running { get; set; }

        public bool Waiting { get; set; }

        public long? TurnStartedAt { get; set; }

        public string? LastTurn { get; set; }

        public string Policy { get; set; } = P.Full;

        public long? DeletedAt { get; set; }

        public string? DeletedBy { get; set; }
    }

    internal sealed class FoldItemRow
    {
        public long Ord { get; init; }

        public long LastSeq { get; set; }

        public long Epoch { get; init; }

        public long Ver { get; init; }

        public required string Kind { get; init; }

        public required string Status { get; init; }

        public bool Final { get; init; }

        public required string Origin { get; init; }

        public required string Body { get; init; }

        public bool Removed { get; set; }
    }
}
