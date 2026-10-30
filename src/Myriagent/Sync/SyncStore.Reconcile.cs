using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Myriagent.Sync;

// 重整與回補用的 sync.db 操作（WP4.3 計畫步 12；D22、D36、D38）：只在 ConversationTablesReady 時呼叫，引擎執行緒、每個方法一個交易
internal sealed partial class SyncStore
{
    /// <summary>
    /// 這個對話在 outbox 裡最後一筆（deviceSeq 最大）的 epoch；outbox 沒有它的列（已修剪）回 null。資料夾已不在、沒有中繼資料可看的刪除用它
    /// 決定 conv.deleted 的 epoch（第 5 階段接手後 epoch 不是 1，計畫 6）
    /// </summary>
    public long? LastConversationEpoch(string conversationId)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "SELECT epoch FROM outbox WHERE conversation_id = $id AND epoch IS NOT NULL ORDER BY device_seq DESC LIMIT 1");
        Bind(cmd, ("$id", conversationId));
        return cmd.ExecuteScalar() is long epoch ? epoch : null;
    }

    /// <summary>kv <c>publish_server</c> 的值：伺服器實例 id 與還原世代（沒有是空字串）</summary>
    public static string PublishServerKeyOf(ServerInstance? server)
        => server is null ? string.Empty : server.InstanceId + "/" + server.RestoreGeneration.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 發布狀態對應的伺服器（D23 的「世代」、D36 的比對）：換實例的對帳進行中是 pending 的那一個（BeginServerChange 已經為它重設了發布狀態），
    /// 否則是 <see cref="SeenServer"/>
    /// </summary>
    public string PublishServerKey()
    {
        ThrowIfDisposed();
        if (!IsReconciling)
            return PublishServerKeyOf(SeenServer);
        var pending = GetKv(SyncStoreSchema.PendingInstanceId);
        return pending is null
            ? PublishServerKeyOf(SeenServer)
            : PublishServerKeyOf(new ServerInstance(pending, ParseLong(GetKv(SyncStoreSchema.PendingRestoreGeneration)) ?? 0));
    }

    /// <summary>
    /// 開啟時才發現中間跑過的舊版處理了伺服器換實例（D36：kv <c>publish_server</c> 與目前的不同）：照 <see cref="BeginServerChange"/> 的同一套重設
    /// 發布狀態（保留墓碑與 Ord、墓碑記 needs_redo），一個交易，並記下新的 <c>publish_server</c>
    /// </summary>
    public void ResetConversationsForServerChange(string serverKey)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(serverKey);
        using var tx = connection.BeginTransaction();
        ClearConversationTables(tx, serverKey);
        Fault("ResetConversationsForServerChange:commit");
        tx.Commit();
    }

    /// <summary>
    /// 清掉發布的摘要（列與 Ord、墓碑保留），已知的對話（寫過 created、沒有墓碑）記 needs_redo，一個交易。回傳記了幾個對話。
    /// <paramref name="keepWire"/>＝只清來源摘要與 items.json 的 SHA-256（DLP 換版：重新投影後只送線上摘要改變的，D38）；
    /// false＝連線上摘要、meta 與佇列的摘要一起清（中間跑過舊版，D36）
    /// </summary>
    public int ForgetPublishDigests(bool keepWire)
    {
        ThrowIfDisposed();
        using var tx = connection.BeginTransaction();
        Execute(connection, tx, keepWire
            ? "UPDATE published_items SET src_sha256 = NULL;"
            : "UPDATE published_items SET src_sha256 = NULL, wire_sha256 = NULL;");
        Execute(connection, tx, keepWire
            ? "UPDATE publish_meta SET items_file_sha256 = NULL;"
            : "UPDATE publish_meta SET items_file_sha256 = NULL, meta_sha256 = NULL, queue_sha256 = NULL;");
        int marked;
        using (var redo = Command(tx, """
                   INSERT INTO publish_state (conversation_id, needs_redo)
                   SELECT conversation_id, 1 FROM publish_meta WHERE created_written != 0 AND deleted_at IS NULL
                   ON CONFLICT (conversation_id) DO UPDATE SET needs_redo = 1
                   """))
        {
            marked = redo.ExecuteNonQuery();
        }

        Fault("ForgetPublishDigests:commit");
        tx.Commit();
        return marked;
    }

    /// <summary>
    /// 保留中、被伺服器擋下的對話列（D36：舊版可能已經處理掉、沒清摘要）：清掉對應的摘要（項目的來源與線上摘要、items.json 的 SHA-256，
    /// 或 meta、佇列的摘要），記 needs_redo，一個交易（重複處理無害）。回傳記了幾個對話
    /// </summary>
    public int ForgetRejectedDigests(IReadOnlyList<OutboxEntry> rejected)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(rejected);
        var wires = new HashSet<string>(StringComparer.Ordinal);
        using var tx = connection.BeginTransaction();
        using var item = Command(tx, "UPDATE published_items SET src_sha256 = NULL, wire_sha256 = NULL WHERE conversation_id = $id AND item_id = $item");
        // 被擋下的 conv.itemRemoved：「已送 itemRemoved」也清掉，重整才會重送這筆刪除（合併前審查 F4）
        using var removal = Command(tx, "UPDATE published_items SET src_sha256 = NULL, wire_sha256 = NULL, removed = 0 WHERE conversation_id = $id AND item_id = $item");
        using var file = Command(tx, "UPDATE publish_meta SET items_file_sha256 = NULL WHERE conversation_id = $id");
        using var meta = Command(tx, "UPDATE publish_meta SET meta_sha256 = NULL WHERE conversation_id = $id");
        using var queue = Command(tx, "UPDATE publish_meta SET queue_sha256 = NULL WHERE conversation_id = $id");
        using var redo = Command(tx, """
            INSERT INTO publish_state (conversation_id, needs_redo) VALUES ($id, 1)
            ON CONFLICT (conversation_id) DO UPDATE SET needs_redo = 1
            """);
        foreach (var entry in rejected)
        {
            if (entry?.ConversationId is not { } wire || !ConversationEventKinds.IsConversationKind(entry.Kind))
                continue;
            if (PublishKeyRules.TryParseItem(entry.CoalesceKey, out _, out var itemId))
            {
                Run(entry.Kind == ConversationEventKinds.ItemRemoved ? removal : item, ("$id", wire), ("$item", itemId));
                Run(file, ("$id", wire));
            }
            else if (entry.Kind is ConversationEventKinds.Created or ConversationEventKinds.Meta)
            {
                Run(meta, ("$id", wire));
            }
            else if (entry.Kind == ConversationEventKinds.Queue)
            {
                Run(queue, ("$id", wire));
            }

            if (wires.Add(wire))
                Run(redo, ("$id", wire));
        }

        Fault("ForgetRejectedDigests:commit");
        tx.Commit();
        return wires.Count;
    }

    /// <summary>發布者的三個 kv（D36、D38）一個交易寫：目前的伺服器、next_device_seq、權杖 DLP 的版本</summary>
    public void WritePublisherState(string serverKey, int dlpVersion)
    {
        ThrowIfDisposed();
        using var tx = connection.BeginTransaction();
        PutKv(tx, SyncStoreSchema.PublishServer, serverKey);
        PutKv(tx, SyncStoreSchema.PublisherNextDeviceSeq, NextDeviceSeq.ToString(CultureInfo.InvariantCulture));
        PutKv(tx, SyncStoreSchema.PublisherDlpVersion, dlpVersion.ToString(CultureInfo.InvariantCulture));
        Fault("WritePublisherState:commit");
        tx.Commit();
    }

    private static void Run(SqliteCommand cmd, params (string Name, object? Value)[] values)
    {
        cmd.Parameters.Clear();
        Bind(cmd, values);
        cmd.ExecuteNonQuery();
    }
}
