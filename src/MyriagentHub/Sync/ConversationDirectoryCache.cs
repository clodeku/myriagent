using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Myriagent.Sync;
using MyriagentHub.Data;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Sync;

/// <summary>
/// 目錄快取的一列（<c>conversations</c> 表提交之後的狀態；不可變）
/// </summary>
/// <param name="OwnerDeviceId">擁有者裝置（重新配對留下的舊身分照樣是擁有者，答 8）</param>
/// <param name="SyncPolicy">解讀後的同步政策（full／metadata／paused）</param>
/// <param name="Deleted">已在伺服器上刪除（墓碑）</param>
/// <param name="Recovering"><c>lease_state</c> 是 recovering（伺服器還原之後；不認得的值當成 owned，D16）</param>
/// <param name="UpdatedAt">顯示用的更新時間（D10）</param>
/// <param name="LastSeq">這個對話最後一筆被接受的事件的 seq</param>
internal sealed record ConversationEntry(string ConversationId, string? OwnerDeviceId, long Epoch, string SyncPolicy,
    bool Deleted, bool Recovering, int ItemCount, long UpdatedAt, long LastSeq);

/// <summary>
/// 伺服器的對話目錄（WP4.2 計畫 D12；實作 WP4.1 的 <see cref="IConversationDirectory"/> 接縫）：啟動時以一次唯讀交易從 <c>conversations</c> 表載入
/// （<c>IStartupTask</c>，排在 sync.db 之後），之後只在寫入者的 AfterCommit 以 <see cref="Apply"/> 更新——交易裡剛建立、還沒提交的對話看不到。
/// 以 <see cref="ConcurrentDictionary{TKey, TValue}"/> 存不可變的記錄：任何執行緒都能讀、O(1)；寫入只有寫入執行緒（與啟動時的載入）。
/// 營運計數（<see cref="ConversationCounters"/>）跟著每次換列調整。
/// </summary>
internal sealed class ConversationDirectoryCache(ConversationCounters counters) : IConversationDirectory
{
    /// <summary><see cref="Read"/> 讀的欄位（順序固定）</summary>
    internal const string Columns = "conversation_id, owner_device_id, epoch, sync_policy, deleted_at, lease_state, item_count, updated_at, last_seq";

    private const byte RebuildPending = 1;

    private const byte RebuildActive = 2;

    private readonly ConcurrentDictionary<string, ConversationEntry> entries = new(StringComparer.Ordinal);

    /// <summary>重建中或等著重建的對話（值是 <see cref="RebuildPending"/>／<see cref="RebuildActive"/>）</summary>
    private readonly ConcurrentDictionary<string, byte> rebuild = new(StringComparer.Ordinal);

    private long restoreGeneration;

    /// <summary>載入時 sync.db 的還原世代（restore 的 SnapshotRequest 去重用，D26）</summary>
    public long RestoreGeneration => Interlocked.Read(ref restoreGeneration);

    /// <summary>目錄裡有幾列（含已刪除的墓碑）</summary>
    public int Count => entries.Count;

    /// <summary>擁有者與世代：id 不合格、不存在、已刪除（或沒有擁有者）回 null（D12）；重新配對留下的舊身分的對話照樣找得到（答 8）</summary>
    public ConversationOwnership? Find(string conversationId)
    {
        if (!ConversationIds.IsWireId(conversationId) || !entries.TryGetValue(conversationId, out var entry) || entry.Deleted
            || entry.OwnerDeviceId is not { } owner)
            return null;
        return new ConversationOwnership(entry.ConversationId, owner, entry.Epoch, entry.SyncPolicy);
    }

    /// <summary>內部用（閘道、營運）：含已刪除的墓碑、recovering、項目數與更新時間</summary>
    public bool TryGet(string? conversationId, [MaybeNullWhen(false)] out ConversationEntry entry)
    {
        entry = null;
        return conversationId is not null && entries.TryGetValue(conversationId, out entry);
    }

    /// <summary>這台擁有、沒刪除的對話（依 id 排序）</summary>
    public IEnumerable<ConversationEntry> OwnedBy(string deviceId) =>
        entries.Values.Where(e => !e.Deleted && string.Equals(e.OwnerDeviceId, deviceId, StringComparison.Ordinal))
            .OrderBy(e => e.ConversationId, StringComparer.Ordinal);

    /// <summary>啟動時載入（sync.db 已初始化；一次唯讀交易）</summary>
    public Task LoadAsync(SyncWriter writer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = new List<ConversationEntry>();
        long startSeq;
        using (var connection = writer.OpenRead())
        using (var tx = connection.BeginTransaction(deferred: true))
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT " + Columns + " FROM conversations";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    loaded.Add(Read(reader, 0));
            }
            // 啟動時的最大 seq：之後被接受的對話事件都比它大，由 AfterCommit 累加；它之前的由背景查詢補（ConversationMaintenance，D31）
            using var high = connection.CreateCommand();
            high.Transaction = tx;
            high.CommandText = "SELECT IFNULL(MAX(seq), 0) FROM events";
            startSeq = (long)high.ExecuteScalar()!;
        }

        foreach (var entry in loaded)
            Apply(entry);
        counters.SetStartSeq(startSeq);
        Interlocked.Exchange(ref restoreGeneration, writer.Instance.RestoreGeneration);
        return Task.CompletedTask;
    }

    /// <summary>提交之後換上新的一列（只在寫入者的 AfterCommit 與啟動時的載入呼叫）；回傳換掉的那一列</summary>
    public ConversationEntry? Apply(ConversationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ConversationEntry? before = null;
        entries.AddOrUpdate(entry.ConversationId, entry, (_, old) =>
        {
            before = old;
            return entry;
        });
        counters.Track(before, entry);
        return before;
    }

    /// <summary>拿掉一列（重建發現這個對話沒有任何 created／meta 事件時；只在 AfterCommit 呼叫）</summary>
    public void Remove(string conversationId)
    {
        if (entries.TryRemove(conversationId, out var before))
            counters.Track(before, null);
        rebuild.TryRemove(conversationId, out _);
    }

    // ───── 重建的狀態（D14；只供顯示與營運計數：伺服器正在整理的對話，Header 顯示「伺服器正在整理這個對話」，步 8） ─────

    /// <summary>重建開始：這些對話等著重建（還沒開始的）</summary>
    public void MarkRebuildPending(IEnumerable<string> conversationIds)
    {
        foreach (var id in conversationIds)
            rebuild[id] = RebuildPending;
    }

    /// <summary>這個對話的重建開始了（第一塊提交之後）</summary>
    public void MarkRebuildActive(string conversationId) => rebuild[conversationId] = RebuildActive;

    /// <summary>這個對話的重建做完了（最後一塊提交之後）</summary>
    public void MarkRebuildDone(string conversationId) => rebuild.TryRemove(conversationId, out _);

    /// <summary>重建放棄或全部做完：清掉所有標記</summary>
    public void ClearRebuild() => rebuild.Clear();

    /// <summary>這個對話正在重建、或等著重建（物化表的內容可能還不對）</summary>
    public bool IsRebuilding(string? conversationId) => conversationId is not null && rebuild.ContainsKey(conversationId);

    /// <summary>正在重建（已開始、還沒做完）與等著重建的對話數（ops/status 的 rebuilding、rebuildPending）</summary>
    public (int Rebuilding, int Pending) RebuildCounts()
    {
        var active = 0;
        var pending = 0;
        foreach (var state in rebuild.Values)
        {
            if (state == RebuildActive)
                active++;
            else
                pending++;
        }
        return (active, pending);
    }

    /// <summary>從 <see cref="Columns"/> 讀一列（<paramref name="start"/> 是第一欄的索引）</summary>
    internal static ConversationEntry Read(SqliteDataReader reader, int start) =>
        new(reader.GetString(start),
            reader.IsDBNull(start + 1) ? null : reader.GetString(start + 1),
            reader.GetInt64(start + 2),
            ConversationSyncPolicies.Interpret(reader.GetString(start + 3)),
            !reader.IsDBNull(start + 4),
            !reader.IsDBNull(start + 5) && reader.GetString(start + 5) == ConversationLeaseStates.Recovering,
            checked((int)reader.GetInt64(start + 6)),
            reader.GetInt64(start + 7),
            reader.GetInt64(start + 8));
}

/// <summary><c>conversations.lease_state</c> 的值（第 4 階段沒有租約；不認得的值一律當成 owned 顯示，D16）</summary>
internal static class ConversationLeaseStates
{
    public const string Owned = "owned";

    public const string Recovering = "recovering";
}
