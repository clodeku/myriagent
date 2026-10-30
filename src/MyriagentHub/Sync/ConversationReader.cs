using System.Collections.Concurrent;
using System.Globalization;
using Myriagent.Agents;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Realtime;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>清單的一列（<c>conversations</c>，沒刪除的）；<see cref="AccountId"/> 是讀取時以目前的 <c>account_links</c> 對到的邏輯帳戶（D11）</summary>
/// <param name="WorkingDirectory">meta 的自訂工作資料夾（預設 workspace 時 null；已遮蔽的版本）</param>
/// <param name="UpdatedAt">顯示用的更新時間（D10：不晚於伺服器收到的時間）</param>
internal sealed record ConversationListRow(string ConversationId, string? OwnerDeviceId, string? AccountId, AgentProvider Provider, string Title,
    long UpdatedAt, bool CustomFolder, string? WorkingDirectory, bool Running, bool Waiting, long LastSeq);

/// <summary>標頭要的一列（<c>conversations</c> 與它的 meta）</summary>
/// <param name="PermissionMode">meta 的權限模式名稱（null＝不明）</param>
/// <param name="StatusText"><c>status_text</c>（第 4 階段不寫，一律 null）</param>
/// <param name="TurnStartedAt">這一輪開始的時間（伺服器收到 <c>conv.turn{started}</c> 的時間）</param>
/// <param name="LastTurn">上一輪結束的 <c>conv.turn{completed}</c>（解析失敗時 null）</param>
/// <param name="LeaseState"><c>lease_state</c> 的原值（不認得的值由 <see cref="ConversationViewRules.IsRecovering"/> 當成 owned）</param>
/// <param name="Models">擁有者電腦發布的模型清單（meta 的 <c>models</c>，<see cref="ConversationViewRules.PublishedModels"/>）；沒發布時 null</param>
internal sealed record ConversationHeaderRow(string ConversationId, string? OwnerDeviceId, string? AccountId, AgentProvider Provider, string Title,
    string? Model, string? Effort, string? PermissionMode, bool CustomFolder, string? WorkingDirectory, bool Running, string? StatusText,
    long? TurnStartedAt, ConversationTurnPayload? LastTurn, string? LeaseState, string SyncPolicy, long Epoch, long LastSeq,
    IReadOnlyList<ConversationModelOption>? Models = null);

/// <summary><c>conversation_runtime</c> 的一列：佇列（<c>conv.queue</c> 的快照）與附件處理中的狀態</summary>
/// <param name="More">佇列裡沒帶到的則數（<c>ConversationQueuePayload.More</c>）</param>
internal sealed record ConversationRuntimeRow(IReadOnlyList<ConversationQueueEntry> Queue, int More, bool Busy, string? BusyText);

/// <summary>一筆物化的項目（<c>items</c>，沒移除的）：渲染用的原料（<see cref="MyriagentHub.Rendering.HubItemSource"/>）</summary>
/// <param name="Body">存的 payload（命中過 DLP 的是遮蔽版）</param>
/// <param name="ReceivedAt">伺服器收到 <see cref="LastSeq"/> 那一筆事件的時間（項目沒有 createdAt 時當成建立時間）；那一筆事件不在時 0</param>
internal sealed record ConversationItemRow(string ItemId, long Ord, long LastSeq, string Kind, string Status, bool Final, string Body, string Enc, long ReceivedAt);

/// <summary>一頁項目（依 ord 由舊到新）；<see cref="HasMore"/>＝這一頁之前還有更早的項目</summary>
internal sealed record ConversationPage(IReadOnlyList<ConversationItemRow> Items, bool HasMore)
{
    public static ConversationPage Empty { get; } = new([], false);
}

/// <summary>清單的一次讀取：資料列、擁有者裝置的顯示名稱（<c>device_streams.name</c>）與交易看到的最大 seq</summary>
internal sealed record ConversationListRead(IReadOnlyList<ConversationListRow> Rows, IReadOnlyDictionary<string, string> DeviceNames, long Seq);

/// <summary>標頭的一次讀取：標頭、裝置的顯示名稱（<c>device_streams.name</c>）與交易看到的最大 seq（同一個唯讀交易；閘道的標頭、notify 與項目渲染用，步 10）</summary>
internal sealed record ConversationHeaderRead(ConversationHeaderRow Header, IReadOnlyDictionary<string, string> DeviceNames, long Seq);

/// <summary>open 的快照：同一個唯讀交易讀的標頭、最後一頁、輸入區與交易看到的最大 seq（D38 的 S）</summary>
internal sealed record ConversationSnapshotRead(ConversationHeaderRow Header, ConversationPage Page, ConversationRuntimeRow? Runtime,
    IReadOnlyDictionary<string, string> DeviceNames, long Seq);

/// <summary>
/// 對話物化表的唯讀讀取（WP4.2 計畫步 8；D10、D11、D20、D38）：每次讀取是一個短的 deferred 唯讀交易（<see cref="SyncWriter.OpenRead"/>，
/// 不用連線池、不拿寫鎖、不擋寫入者），所以每份結果都對應某個已提交的 seq 前綴；回傳交易看到的最大 seq，閘道之後只推比它新的變更（D38）。
/// 方法是同步的：唯一的呼叫端是閘道迴圈（在它的鎖裡）與 hub 的指令處理（一次短讀），Microsoft.Data.Sqlite 的非同步方法本來就是同步執行。
/// <list type="bullet">
/// <item>清單排除已刪除，依 <c>updated_at DESC, last_seq DESC</c>（D10），最多 <see cref="ConversationViewRules.MaxListItems"/> 筆。</item>
/// <item>帳戶：以 (owner_device_id, meta 的 <c>localAccountId</c>) 對照<b>目前</b>的 <c>account_links</c>（邏輯帳戶沒刪除的），重新連結之後跟著變（D11）；對不到是 null。</item>
/// <item>任何 JSON 欄位解析失敗時那一格給預設、整列照常；<c>provider</c> 不認得的列跳過。記錄只寫欄位與對話 id、不寫內容（D8），以對話去重、最多 1,000 個鍵。</item>
/// </list>
/// 只讀：這個類別沒有任何寫入。
/// </summary>
internal sealed class ConversationReader(SyncWriter writer, ILogger<ConversationReader> log)
{
    /// <summary>記錄去重最多記幾個鍵（超過就不再記新的）</summary>
    internal const int MaxLoggedKeys = 1000;

    private const string HeaderColumns =
        "conversation_id, owner_device_id, provider, title, meta_json, running, status_text, turn_started_at, last_turn, lease_state, sync_policy, epoch, last_seq";

    private const string ItemColumns =
        "i.item_id, i.ord, i.last_seq, i.kind, i.status, i.final, i.body, i.enc, IFNULL(e.created_at, 0)";

    private readonly ConcurrentDictionary<string, byte> logged = new(StringComparer.Ordinal);

    /// <summary>對話清單（沒刪除的，D10 的順序）</summary>
    public ConversationListRead List(int limit = ConversationViewRules.MaxListItems)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        var links = ReadLinks(connection, tx);
        var rows = new List<ConversationListRow>();
        using (var cmd = Command(connection, tx,
            "SELECT conversation_id, owner_device_id, provider, title, updated_at, meta_json, running, waiting, last_seq FROM conversations "
            + "WHERE deleted_at IS NULL ORDER BY updated_at DESC, last_seq DESC LIMIT $n", ("$n", limit)))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (Provider(id, reader.GetString(2)) is not { } provider)
                    continue;
                var owner = Text(reader, 1);
                var meta = ParseMeta(id, reader.GetString(5));
                rows.Add(new ConversationListRow(id, owner, Account(links, owner, meta.LocalAccountId), provider, reader.GetString(3), reader.GetInt64(4),
                    meta.CustomFolder, meta.WorkingDirectory, reader.GetInt64(6) != 0, reader.GetInt64(7) != 0, reader.GetInt64(8)));
            }
        }
        return new ConversationListRead(rows, ReadDeviceNames(connection, tx), MaxSeq(connection, tx));
    }

    /// <summary>標頭要的一列；不存在、已刪除或 provider 不認得回 null</summary>
    public (ConversationHeaderRow? Header, long Seq) ReadHeader(string conversationId)
    {
        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        return (ReadHeader(connection, tx, conversationId), MaxSeq(connection, tx));
    }

    /// <summary>標頭與裝置的顯示名稱（同一個唯讀交易）；不存在、已刪除或 provider 不認得回 null</summary>
    public ConversationHeaderRead? ReadHeaderView(string conversationId)
    {
        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        if (ReadHeader(connection, tx, conversationId) is not { } header)
            return null;
        return new ConversationHeaderRead(header, ReadDeviceNames(connection, tx), MaxSeq(connection, tx));
    }

    /// <summary>
    /// 一頁項目（沒移除的，依 ord）：<paramref name="beforeItemId"/> 是 null 時是最後一頁；否則是那一筆之前的最多 <paramref name="limit"/> 筆
    /// （那一筆已移除也照它的 ord 分頁）。找不到 <paramref name="beforeItemId"/>（清掉了、或不是這個對話的）回最後一頁
    /// </summary>
    public (ConversationPage Page, long Seq) ReadPage(string conversationId, string? beforeItemId, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        return (ReadPage(connection, tx, conversationId, beforeItemId, limit), MaxSeq(connection, tx));
    }

    /// <summary>一筆項目（沒移除、對話沒刪除）；沒有回 null（copyItem 與超過 16 KB、沒附在變更裡的項目用）</summary>
    public (ConversationItemRow? Item, long Seq) ReadItem(string conversationId, string itemId)
    {
        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        ConversationItemRow? item = null;
        using (var cmd = Command(connection, tx,
            "SELECT " + ItemColumns + " FROM items i JOIN conversations c ON c.conversation_id = i.conversation_id LEFT JOIN events e ON e.seq = i.last_seq "
            + "WHERE i.conversation_id = $c AND i.item_id = $i AND i.removed = 0 AND c.deleted_at IS NULL", ("$c", conversationId), ("$i", itemId)))
        using (var reader = cmd.ExecuteReader())
        {
            if (reader.Read())
                item = Item(reader);
        }
        return (item, MaxSeq(connection, tx));
    }

    /// <summary>側欄的對話搜尋讀的種類（<see cref="Myriagent.Hub.ConversationSearchRules.IsSearchable"/>；<c>items.kind</c> 存的是名稱）</summary>
    private static readonly string[] SearchKinds = Enum.GetValues<AgentItemKind>().Where(Myriagent.Hub.ConversationSearchRules.IsSearchable).Select(k => k.ToString()).ToArray();

    /// <summary>
    /// 側欄的對話搜尋（web-protocol 的 <c>searchConversations</c>）：依序對每個對話，在同一個唯讀交易讀它沒移除、搜得到的種類的項目（依 ord），
    /// 交給 <paramref name="each"/>（對話 id、各項目的 (enc, body)）；對話不存在或已刪除時給空清單。每個對話之間檢查 <paramref name="cancellationToken"/>
    /// </summary>
    public void ReadSearchItems(IReadOnlyList<string> conversationIds, Action<string, IReadOnlyList<(string Enc, string Body)>> each, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        ArgumentNullException.ThrowIfNull(each);
        if (conversationIds.Count == 0)
            return;
        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        var kinds = string.Join(", ", SearchKinds.Select((_, i) => "$k" + i.ToString(CultureInfo.InvariantCulture)));
        using var cmd = Command(connection, tx,
            "SELECT i.enc, i.body FROM items i JOIN conversations c ON c.conversation_id = i.conversation_id "
            + "WHERE i.conversation_id = $c AND i.removed = 0 AND c.deleted_at IS NULL AND i.kind IN (" + kinds + ") ORDER BY i.ord",
            [("$c", string.Empty), .. SearchKinds.Select((k, i) => ("$k" + i.ToString(CultureInfo.InvariantCulture), (object?)k))]);
        foreach (var conversationId in conversationIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cmd.Parameters["$c"].Value = conversationId;
            var items = new List<(string Enc, string Body)>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    items.Add((reader.GetString(0), reader.GetString(1)));
            }
            each(conversationId, items);
        }
    }

    /// <summary>輸入區的一列；沒有（還沒收過 conv.queue、被清掉）回 null</summary>
    public (ConversationRuntimeRow? Runtime, long Seq) ReadRuntime(string conversationId)
    {
        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        return (ReadRuntime(connection, tx, conversationId), MaxSeq(connection, tx));
    }

    /// <summary>open 的快照（D38）：標頭、最後一頁（最多 <paramref name="limit"/> 筆）、輸入區在同一個唯讀交易；對話不存在、已刪除回 null</summary>
    public ConversationSnapshotRead? ReadSnapshot(string conversationId, int limit = ConversationViewRules.PageItems)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var connection = writer.OpenRead();
        using var tx = connection.BeginTransaction(deferred: true);
        if (ReadHeader(connection, tx, conversationId) is not { } header)
            return null;
        return new ConversationSnapshotRead(header, ReadPage(connection, tx, conversationId, null, limit), ReadRuntime(connection, tx, conversationId),
            ReadDeviceNames(connection, tx), MaxSeq(connection, tx));
    }

    private ConversationHeaderRow? ReadHeader(SqliteConnection connection, SqliteTransaction tx, string conversationId)
    {
        using var cmd = Command(connection, tx, "SELECT " + HeaderColumns + " FROM conversations WHERE conversation_id = $c AND deleted_at IS NULL", ("$c", conversationId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        if (Provider(conversationId, reader.GetString(2)) is not { } provider)
            return null;
        var owner = Text(reader, 1);
        var meta = ParseMeta(conversationId, reader.GetString(4));
        var account = owner is not null && meta.LocalAccountId is not null ? LinkedAccount(connection, tx, owner, meta.LocalAccountId) : null;
        return new ConversationHeaderRow(conversationId, owner, account, provider, reader.GetString(3), meta.Model, meta.Effort, meta.PermissionMode,
            meta.CustomFolder, meta.WorkingDirectory, reader.GetInt64(5) != 0, Text(reader, 6), Long(reader, 7),
            Text(reader, 8) is { } turn ? Parse<ConversationTurnPayload>(conversationId, "last_turn", turn) : null,
            Text(reader, 9), reader.GetString(10), reader.GetInt64(11), reader.GetInt64(12), meta.Models);
    }

    private static ConversationPage ReadPage(SqliteConnection connection, SqliteTransaction tx, string conversationId, string? beforeItemId, int limit)
    {
        long? beforeOrd = null;
        if (beforeItemId is not null)
        {
            using var find = Command(connection, tx, "SELECT ord FROM items WHERE conversation_id = $c AND item_id = $i", ("$c", conversationId), ("$i", beforeItemId));
            beforeOrd = find.ExecuteScalar() is long ord ? ord : null;
        }

        // 多讀一筆判斷前面還有沒有；同一個 ord（不該有）以 item_id 定順序
        var sql = "SELECT " + ItemColumns + " FROM items i LEFT JOIN events e ON e.seq = i.last_seq WHERE i.conversation_id = $c AND i.removed = 0"
            + (beforeOrd is null ? string.Empty : " AND (i.ord < $o OR (i.ord = $o AND i.item_id < $b))")
            + " ORDER BY i.ord DESC, i.item_id DESC LIMIT $n";
        var parameters = new List<(string, object?)> { ("$c", conversationId), ("$n", (long)limit + 1) };
        if (beforeOrd is { } o)
        {
            parameters.Add(("$o", o));
            parameters.Add(("$b", beforeItemId));
        }
        var items = new List<ConversationItemRow>();
        using (var cmd = Command(connection, tx, sql, parameters.ToArray()))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                items.Add(Item(reader));
        }
        var hasMore = items.Count > limit;
        if (hasMore)
            items.RemoveAt(items.Count - 1);
        items.Reverse();
        return new ConversationPage(items, hasMore);
    }

    private ConversationRuntimeRow? ReadRuntime(SqliteConnection connection, SqliteTransaction tx, string conversationId)
    {
        using var cmd = Command(connection, tx, "SELECT queue_json, busy, busy_text FROM conversation_runtime WHERE conversation_id = $c", ("$c", conversationId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        var queue = Parse<ConversationQueuePayload>(conversationId, "queue_json", reader.GetString(0));
        var entries = (queue?.Entries ?? Array.Empty<ConversationQueueEntry>()).Where(e => e is { Id: not null, DisplayText: not null }).ToList();
        return new ConversationRuntimeRow(entries, Math.Max(0, queue?.More ?? 0), reader.GetInt64(1) != 0, Text(reader, 2));
    }

    /// <summary>(裝置, 本機帳戶) → 邏輯帳戶（目前的 account_links，邏輯帳戶沒刪除的）</summary>
    private static Dictionary<(string DeviceId, string LocalAccountId), string> ReadLinks(SqliteConnection connection, SqliteTransaction tx)
    {
        var links = new Dictionary<(string, string), string>();
        using var cmd = Command(connection, tx,
            "SELECT l.device_id, l.local_account_id, l.logical_account_id FROM account_links l "
            + "JOIN logical_accounts a ON a.logical_account_id = l.logical_account_id WHERE a.deleted_at IS NULL");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            links[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        return links;
    }

    private static string? LinkedAccount(SqliteConnection connection, SqliteTransaction tx, string deviceId, string localAccountId)
    {
        using var cmd = Command(connection, tx,
            "SELECT l.logical_account_id FROM account_links l JOIN logical_accounts a ON a.logical_account_id = l.logical_account_id "
            + "WHERE l.device_id = $d AND l.local_account_id = $l AND a.deleted_at IS NULL", ("$d", deviceId), ("$l", localAccountId));
        return cmd.ExecuteScalar() as string;
    }

    private static string? Account(Dictionary<(string DeviceId, string LocalAccountId), string> links, string? ownerDeviceId, string? localAccountId) =>
        ownerDeviceId is not null && localAccountId is not null && links.TryGetValue((ownerDeviceId, localAccountId), out var logical) ? logical : null;

    /// <summary>裝置的顯示名稱（<c>device_streams.name</c>，沒有名稱的不列）</summary>
    private static Dictionary<string, string> ReadDeviceNames(SqliteConnection connection, SqliteTransaction tx)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = Command(connection, tx, "SELECT device_id, name FROM device_streams WHERE name IS NOT NULL");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names[reader.GetString(0)] = reader.GetString(1);
        return names;
    }

    /// <summary>交易看到的最大 seq（events 的 INTEGER PRIMARY KEY：O(log n)）</summary>
    private static long MaxSeq(SqliteConnection connection, SqliteTransaction tx)
    {
        using var cmd = Command(connection, tx, "SELECT IFNULL(MAX(seq), 0) FROM events");
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static ConversationItemRow Item(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5) != 0,
            reader.GetString(6), reader.GetString(7), reader.GetInt64(8));

    private AgentProvider? Provider(string conversationId, string wire)
    {
        if (ConversationViewRules.ProviderOf(wire) is { } provider)
            return provider;
        Warn(conversationId, "provider");
        return null;
    }

    /// <summary>meta_json（存下的 <c>ConversationMetaPayload</c>）裡給畫面看的欄位；型別不對的欄位給預設、解析失敗整份給預設</summary>
    private MetaFields ParseMeta(string conversationId, string json)
    {
        if (ParseObject(json) is not { } meta)
        {
            Warn(conversationId, "meta_json");
            return MetaFields.Empty;
        }
        return new MetaFields(Str(meta, "localAccountId"), Str(meta, "permissionMode"), Str(meta, "model"), Str(meta, "effort"),
            meta["customFolder"] is JValue { Type: JTokenType.Boolean } custom && (bool)custom, Str(meta, "workingDirectory"),
            ConversationViewRules.PublishedModels(meta["models"]));
    }

    private T? Parse<T>(string conversationId, string column, string json) where T : class
    {
        try
        {
            return SyncJson.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            // 例外訊息可能帶內容的片段（D8）：只記欄位與對話
            Warn(conversationId, column);
            return null;
        }
    }

    /// <summary>一個欄位讀不懂（同一個對話的同一欄只記一次、最多 <see cref="MaxLoggedKeys"/> 個鍵）：只記欄位與對話 id</summary>
    private void Warn(string conversationId, string column)
    {
        if (logged.Count >= MaxLoggedKeys || !logged.TryAdd(column + ":" + conversationId, 0))
            return;
        log.LogWarning("sync.db 的 {Column} 讀不懂（provider 不認得時那一列略過，其他欄位給預設）：{ConversationId}", column, conversationId);
    }

    private static JObject? ParseObject(string json)
    {
        try
        {
            using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Double, MaxDepth = 64 };
            var token = JToken.ReadFrom(reader);
            return reader.Read() ? null : token as JObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JObject o, string name) => o[name] is JValue { Type: JTokenType.String } value ? (string?)value : null;

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    private static long? Long(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);

    /// <summary>meta 裡給畫面看的欄位</summary>
    private sealed record MetaFields(string? LocalAccountId, string? PermissionMode, string? Model, string? Effort, bool CustomFolder, string? WorkingDirectory,
        IReadOnlyList<ConversationModelOption>? Models)
    {
        public static MetaFields Empty { get; } = new(null, null, null, null, false, null, null);
    }
}
