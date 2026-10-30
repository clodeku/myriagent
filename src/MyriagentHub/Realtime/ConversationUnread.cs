using MyriagentHub.Data;
using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 伺服器記的網頁未讀（WP4.2 計畫步 12A；答 6、D45、D46）：對話 id → 標未讀的那筆事件的 seq 與時間。
/// <list type="bullet">
/// <item>啟動時以一次唯讀交易從 sync.db v3 的 <c>conversation_unread</c> 載入（<c>IStartupTask</c>，排在對話目錄之後），只載入還在、沒刪除的對話。</item>
/// <item>之後只由閘道迴圈讀寫（<see cref="BrowserGateway"/>；規則在 <see cref="ConversationUnreadRules"/>），記憶體的集合為準：
/// 寫入經 <see cref="SyncWriter.WriteAsync{T}"/>、不等它完成（寫入者是先進先出，標了再清的順序不會亂），失敗只記錄（不含內容）。</item>
/// <item>標記以 <c>INSERT … SELECT … WHERE</c> 確認對話存在而且沒刪除：和刪除的交易交錯時（刪除先提交、標記後到）不會留下孤兒列。
/// 刪除的交易本身刪掉未讀列（<see cref="ConversationMaterializer"/>），閘道取到 Deleted 時以 <see cref="Forget"/> 從集合拿掉；
/// Deleted 因為變更通知溢出被丟掉時，閘道在整份重讀時照目錄補做（<see cref="ConversationViews.ReconcileUnread"/>）。</item>
/// <item>未讀的對話數給 ops/status 的 <c>unread</c>（<see cref="ConversationCounters.SetUnread"/>，只有計數）。</item>
/// </list>
/// 集合以鎖保護（閘道迴圈之外只有啟動時的載入、測試與營運的計數會碰），<see cref="Version"/> 每改一次加 1（清單的快取看它）。
/// </summary>
internal sealed class ConversationUnread(SyncWriter writer, ConversationCounters counters, ILogger<ConversationUnread> log)
{
    private readonly object gate = new();
    private readonly Dictionary<string, (long Seq, long At)> entries = new(StringComparer.Ordinal);
    private long version;

    /// <summary>未讀的對話數</summary>
    public int Count
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    /// <summary>集合每改一次加 1（清單的快取以它判斷要不要重組）</summary>
    public long Version
    {
        get
        {
            lock (gate)
                return version;
        }
    }

    /// <summary>這個對話是未讀嗎</summary>
    public bool Contains(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        lock (gate)
            return entries.ContainsKey(conversationId);
    }

    /// <summary>未讀的對話（依 id 排序的複本）</summary>
    public IReadOnlyList<string> Ids()
    {
        lock (gate)
            return entries.Keys.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>這個對話標未讀時的 seq 與時間；不是未讀回 null（測試與營運用）</summary>
    public (long Seq, long At)? Since(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        lock (gate)
            return entries.TryGetValue(conversationId, out var since) ? since : null;
    }

    /// <summary>啟動時載入（sync.db 已初始化、v3 的表已建；一次唯讀交易）：只載入還在、沒刪除的對話（孤兒列不載入）</summary>
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = new List<(string Id, long Seq, long At)>();
        using (var connection = writer.OpenRead())
        using (var tx = connection.BeginTransaction(deferred: true))
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT u.conversation_id, u.since_seq, u.since_at FROM conversation_unread u "
                + "JOIN conversations c ON c.conversation_id = u.conversation_id WHERE c.deleted_at IS NULL";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                loaded.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }
        int count;
        lock (gate)
        {
            entries.Clear();
            foreach (var (id, seq, at) in loaded)
                entries[id] = (seq, at);
            version++;
            count = entries.Count;
        }
        counters.SetUnread(count);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 標未讀（閘道迴圈）：已經是未讀就不動（留第一次的 seq 與時間，回 false）；否則加進集合、寫進 sync.db（不等），回 true
    /// </summary>
    public bool Mark(string conversationId, long seq, long at)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        int count;
        lock (gate)
        {
            if (!entries.TryAdd(conversationId, (seq, at)))
                return false;
            version++;
            count = entries.Count;
        }
        counters.SetUnread(count);
        Persist("mark", context => context.Execute(
            "INSERT OR REPLACE INTO conversation_unread(conversation_id, since_seq, since_at) SELECT $c, $seq, $at "
            + "WHERE EXISTS (SELECT 1 FROM conversations WHERE conversation_id = $c AND deleted_at IS NULL)",
            ("$c", conversationId), ("$seq", seq), ("$at", at)));
        return true;
    }

    /// <summary>清未讀（閘道迴圈：有連線開始看見它）：不是未讀回 false；否則從集合拿掉、刪掉 sync.db 的列（不等），回 true</summary>
    public bool Clear(string conversationId)
    {
        if (!Remove(conversationId))
            return false;
        Persist("clear", context => context.Execute("DELETE FROM conversation_unread WHERE conversation_id = $c", ("$c", conversationId)));
        return true;
    }

    /// <summary>對話在伺服器上刪除了（閘道取到 Deleted）：只從集合拿掉（sync.db 的列已在刪除的同一個交易刪掉，D34）。不是未讀回 false</summary>
    public bool Forget(string conversationId) => Remove(conversationId);

    private bool Remove(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        int count;
        lock (gate)
        {
            if (!entries.Remove(conversationId))
                return false;
            version++;
            count = entries.Count;
        }
        counters.SetUnread(count);
        return true;
    }

    /// <summary>寫進 sync.db（不等）：失敗只記錄操作與例外的型別（不記對話 id、內容與例外訊息），集合照樣為準</summary>
    private void Persist(string operation, Action<SyncWriteContext> work)
    {
        Task task;
        try
        {
            task = writer.WriteAsync(work);
        }
        catch (InvalidOperationException ex)
        {
            // sync.db 已關閉（停機中）
            log.LogWarning("未讀寫不進 sync.db（{Operation}）：{ExceptionType}", operation, ex.GetType().FullName);
            return;
        }
        task.ContinueWith(t => log.LogWarning("未讀寫不進 sync.db（{Operation}）：{ExceptionType}", operation, t.Exception?.GetBaseException().GetType().FullName),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
