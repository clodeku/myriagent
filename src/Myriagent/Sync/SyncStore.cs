using System.Globalization;
using Myriagent.Core;
using Myriagent.Security;
using Microsoft.Data.Sqlite;

namespace Myriagent.Sync;

/// <summary>sync.db 打不開的原因（引擎停止並提示解除配對後重新配對，設計 3.6）</summary>
internal enum SyncStoreFailure
{
    /// <summary>檔案損毀（quick_check 不是 ok、不是 SQLite 檔、缺表）</summary>
    Corrupt,

    /// <summary>較新版的 app 建的、這一版讀不了（schema_min_reader 大於 <see cref="SyncStore.SchemaVersion"/>）</summary>
    TooNew,

    /// <summary>屬於另一台裝置（kv 的 device_id 與 device.json 不同）</summary>
    DeviceMismatch,

    /// <summary>檔案不見了（不會建出空檔）</summary>
    Missing,

    /// <summary>
    /// 暫時寫不進去或讀不到（被鎖、唯讀、磁碟滿、I/O 錯誤）：不是損毀，不提示解除配對。只有開啟之後的操作會丟
    /// （遠端指令與遠端操作紀錄，SyncStore.Commands.cs；WP4.4 計畫步 2），開啟（<see cref="SyncStore.Open"/>）不會
    /// </summary>
    Unavailable,
}

internal sealed class SyncStoreException(SyncStoreFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public SyncStoreFailure Failure { get; } = failure;
}

internal enum SyncStoreOpenMode
{
    /// <summary>配對完成時建新檔（Mode=ReadWriteCreate）</summary>
    Create,

    /// <summary>已配對時開既有的檔（Mode=ReadWrite：不存在就失敗，不會建出空檔）</summary>
    Open,
}

/// <summary>
/// 要寫進 outbox 的一筆（payload 是 JSON 原文，由 <see cref="SyncStore.AppendMany"/> 編碼並算雜湊）。
/// <paramref name="ConversationId"/>、<paramref name="Epoch"/>：第 4 階段的對話事件才有（線上的對話 id 與擁有權的世代，sync-protocol 14.2）；
/// 第 3 階段的事件兩個都是 null。外框與合併鍵照 <see cref="OutboxAppendRules"/>
/// </summary>
internal sealed record OutboxAppend(string Stream, string Kind, string Json, string? CoalesceKey, string? ConversationId = null, long? Epoch = null);

/// <summary>sync.db 的目錄快取（伺服器寫的事件化簡後的結果）</summary>
internal sealed record CatalogCache(
    IReadOnlyDictionary<string, AccountUpsertPayload> Accounts,
    IReadOnlyDictionary<string, DeviceUpsertPayload> Devices,
    IReadOnlyDictionary<string, DeviceRevokedPayload> Revoked,
    IReadOnlyDictionary<string, PrefsSetPayload> Prefs);

/// <summary>
/// 桌面的 sync.db（設計 3.6 的子集）：outbox、游標、目錄快取、名冊、published 摘要、detached 對應；
/// 結構 v2 另有第 4 階段的表（<see cref="SyncStoreSchema.CreateV2"/>，由 WP4.3／WP4.4／WP4.7 讀寫），v3 有對話發布的摘要與複本
/// （<see cref="SyncStoreSchema.CreateV3"/>；讀寫在 SyncStore.Conversations.cs），v4 有第 5 階段的逐字稿上傳、交接與資料夾對應（<see cref="SyncStoreSchema.CreateV4"/>）。
/// <list type="bullet">
/// <item>只在引擎執行緒使用（或配對提交時的背景執行緒），不加鎖；整個生命期開著一條長連線——不用連線池時最後一條連線關閉
/// 就會做檢查點並刪掉 -wal、-shm，引擎存活期間這條連線一直開著，<see cref="Dispose"/> 時關閉。</item>
/// <item>連線字串只由 <see cref="ConnectionString"/> 產生，一律 <c>Pooling=False</c>：Microsoft.Data.Sqlite 10.0.12 的連線池會讓兩條連線共用
/// 同一條原生連線（dotnet/efcore#39008），修正還沒發布到 NuGet。永不呼叫 ClearPool／ClearAllPools（SyncStoreTests 掃組件把關）。</item>
/// <item>每次寫入一個交易，synchronous=FULL：提交（fsync）之後才算數，「先持久化才送」的裝置端。</item>
/// <item><c>next_device_seq</c> 存在 kv、和新列在同一個交易寫：outbox 會被修剪，不能從現有的列推算。</item>
/// </list>
/// </summary>
internal sealed partial class SyncStore : IDisposable
{
    /// <summary>
    /// 這一版程式的結構版本（v2：第 4 階段的表，D55；v3：WP4.3 的對話發布摘要與複本，WP4.3 計畫 D11；v4：第 5 階段的 log_state、handoffs_local、path_cache）。
    /// 開啟時 <c>user_version</c> 補到這個值（比它大的不動）
    /// </summary>
    public const int SchemaVersion = 4;

    /// <summary>
    /// 讀得了這個檔的最舊程式版本：v2、v3、v4 都只加表，v1～v3 的程式照樣能開（回滾不必還原資料庫，計畫 0.1 第 5 點、第 5 階段計畫 4.3 第 5 點），所以維持 1。
    /// 要改成 2 以上，得先確定 v1 的程式讀到會壞
    /// </summary>
    public const int MinReaderVersion = 1;

    private const int SqliteCorrupt = 11;

    private const int SqliteNotADatabase = 26;

    /// <summary>
    /// 暫時性的 SQLite 錯誤（BUSY 5、LOCKED 6、READONLY 8、IOERR 10、FULL 13、CANTOPEN 14）：檔案被占用、唯讀、磁碟滿，不是損毀，
    /// 不能提示「解除配對」（解除配對會刪掉 sync.db 與還沒被確認的 outbox）。是的話回錯誤碼，否則 null
    /// </summary>
    public static int? TransientSqliteErrorCode(Exception ex)
        => ex is SqliteException { SqliteErrorCode: 5 or 6 or 8 or 10 or 13 or 14 } sqlite ? sqlite.SqliteErrorCode : null;

    private readonly SqliteConnection connection;
    private ServerInstance? seenServer;
    private bool reconciling;
    private long? reconcileThrough;
    private bool disposed;

    private SyncStore(SqliteConnection connection, string path)
    {
        this.connection = connection;
        Path = path;
    }

    public string Path { get; }

    public long NextDeviceSeq { get; private set; }

    public ServerInstance? SeenServer => seenServer;

    public bool IsReconciling => reconciling;

    public long? ReconcileThroughSeq => reconcileThrough;

    /// <summary>
    /// v3 的對話表都在（WP4.3 計畫 D3、步 8）：開啟時依結構是不是這一版設定。升級失敗（被鎖、唯讀、磁碟滿）時為 false、照舊版結構開啟，
    /// 所有對話的發布與複本都不做、Hello 不宣告 conv1；引擎進 Live 時以 <see cref="RetryUpgrade"/> 再試。
    /// v4 的表在同一個升級交易裡補上，所以它是 true 時 v4 的表也都在（第 5 階段的 log1 以它為條件）
    /// </summary>
    public bool ConversationTablesReady { get; private set; }

    /// <summary>測試：在交易的指定點（名稱見各方法）丟例外，確認整個交易回滾</summary>
    internal Action<string>? FaultInjection { get; set; }

    /// <summary>唯一的連線字串產生點：Pooling=False；Create→Mode=ReadWriteCreate，Open→Mode=ReadWrite</summary>
    public static string ConnectionString(string path, SyncStoreOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode switch
        {
            SyncStoreOpenMode.Create => SqliteOpenMode.ReadWriteCreate,
            SyncStoreOpenMode.Open => SqliteOpenMode.ReadWrite,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        },
        Pooling = false,
    }.ToString();

    /// <summary>配對完成時建新的 sync.db（檔案已存在就丟 IOException）：結構、kv、名冊在同一個交易；任何一步失敗都刪掉建到一半的檔</summary>
    public static SyncStore Create(string path, string deviceId, long pairedAt, ServerInstance server, TrustBundle trust)
    {
        if (File.Exists(path))
            throw new IOException($"同步資料庫已存在：{path}");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        // 上一個 sync.db 留下的 -wal／-shm（檔案本身已刪）不能套到新檔上
        DeleteSidecars(path);
        var connection = new SqliteConnection(ConnectionString(path, SyncStoreOpenMode.Create));
        try
        {
            connection.Open();
            Execute(connection, null, SyncStoreSchema.ConnectionPragmas);
            var store = new SyncStore(connection, path);
            using (var tx = connection.BeginTransaction())
            {
                Execute(connection, tx, SyncStoreSchema.CreateV1);
                Execute(connection, tx, SyncStoreSchema.CreateV2);
                Execute(connection, tx, SyncStoreSchema.CreateV3);
                Execute(connection, tx, SyncStoreSchema.CreateV4);
                Execute(connection, tx, $"PRAGMA user_version = {SchemaVersion};");
                store.PutKv(tx, SyncStoreSchema.SchemaMinReader, MinReaderVersion.ToString(CultureInfo.InvariantCulture));
                store.PutKv(tx, SyncStoreSchema.DeviceId, deviceId);
                store.PutKv(tx, SyncStoreSchema.PairedAt, pairedAt.ToString(CultureInfo.InvariantCulture));
                store.PutKv(tx, SyncStoreSchema.NextDeviceSeq, "1");
                store.PutKv(tx, SyncStoreSchema.SeenInstanceId, server.InstanceId);
                store.PutKv(tx, SyncStoreSchema.SeenRestoreGeneration, server.RestoreGeneration.ToString(CultureInfo.InvariantCulture));
                store.PutKv(tx, SyncStoreSchema.Reconcile, "0");
                store.WriteRegistry(tx, trust.RegistryRev, trust.Registry ?? []);
                tx.Commit();
            }

            store.LoadState();
            store.ConversationTablesReady = true;
            return store;
        }
        catch
        {
            connection.Dispose();
            TryDelete(path);
            DeleteSidecars(path);
            throw;
        }
    }

    /// <summary>
    /// 開既有的 sync.db：不存在→Missing（不建新檔）；quick_check 不是 ok、不是 SQLite 檔、缺表→Corrupt；
    /// schema_min_reader 大於這一版→TooNew；kv 的 device_id 不符→DeviceMismatch。
    /// 都過了才升級結構（<see cref="UpgradeSchema"/>：補上 v2～v4 的表、<c>user_version</c> 補到 <see cref="SchemaVersion"/>）
    /// </summary>
    public static SyncStore Open(string path, string expectedDeviceId) => Open(path, expectedDeviceId, null);

    /// <summary>同 <see cref="Open(string, string)"/>；測試：<paramref name="faultInjection"/> 在升級結構之前就裝上（開啟時的升級也能注入失敗）</summary>
    internal static SyncStore Open(string path, string expectedDeviceId, Action<string>? faultInjection)
    {
        if (!File.Exists(path))
            throw new SyncStoreException(SyncStoreFailure.Missing, $"同步資料庫不見了：{path}");
        var connection = new SqliteConnection(ConnectionString(path, SyncStoreOpenMode.Open));
        try
        {
            try
            {
                connection.Open();
                Execute(connection, null, "PRAGMA busy_timeout=3000;");
                var check = Scalar(connection, null, "PRAGMA quick_check;");
                if (!string.Equals(check, "ok", StringComparison.Ordinal))
                    throw new SyncStoreException(SyncStoreFailure.Corrupt, $"同步資料庫損毀（quick_check：{check}）");
                Execute(connection, null, SyncStoreSchema.ConnectionPragmas);
                if (!TableExists(connection, "kv"))
                    throw new SyncStoreException(SyncStoreFailure.Corrupt, "同步資料庫缺少 kv 表");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
            {
                throw new SyncStoreException(SyncStoreFailure.Corrupt, $"同步資料庫損毀（SQLite {ex.SqliteErrorCode}）", ex);
            }

            var store = new SyncStore(connection, path) { FaultInjection = faultInjection };
            var minReader = store.GetKv(SyncStoreSchema.SchemaMinReader);
            if (!int.TryParse(minReader, NumberStyles.None, CultureInfo.InvariantCulture, out var min))
                throw new SyncStoreException(SyncStoreFailure.Corrupt, "同步資料庫沒有結構版本");
            if (min > SchemaVersion)
                throw new SyncStoreException(SyncStoreFailure.TooNew, $"同步資料庫需要結構版本 {min} 以上的 app（這一版是 {SchemaVersion}）");
            var deviceId = store.GetKv(SyncStoreSchema.DeviceId);
            if (deviceId != expectedDeviceId)
                throw new SyncStoreException(SyncStoreFailure.DeviceMismatch, "同步資料庫屬於另一台裝置");
            store.UpgradeSchema();
            store.LoadState();
            store.ConversationTablesReady = store.IsSchemaCurrent();
            return store;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 開啟時升級失敗（<see cref="ConversationTablesReady"/> 是 false）的「可重試的狀態」（WP4.3 計畫步 8）：再升級一次，成功就把
    /// <see cref="ConversationTablesReady"/> 設成 true 並回 true。已是這一版直接回 true（不開寫入交易）。暫時性的錯誤記錄後回 false；
    /// 升級時損毀丟 <see cref="SyncStoreException"/>（Corrupt）。多久試一次由引擎決定（進 Live 時，最多 10 分鐘一次）
    /// </summary>
    public bool RetryUpgrade()
    {
        ThrowIfDisposed();
        if (ConversationTablesReady)
            return true;
        UpgradeSchema();
        ConversationTablesReady = IsSchemaCurrent();
        return ConversationTablesReady;
    }

    /// <summary>
    /// 先以唯讀查詢看要不要升級（<c>user_version</c> ≥ <see cref="SchemaVersion"/> 而且 v2～v4 的表與索引都在就不開寫入交易：
    /// BeginTransaction 是 BEGIN IMMEDIATE，別的連線拿著寫入鎖時會失敗）。要升級才在一個交易裡跑 <see cref="SyncStoreSchema.CreateV2"/>、
    /// <see cref="SyncStoreSchema.CreateV3"/>、<see cref="SyncStoreSchema.CreateV4"/>（IF NOT EXISTS：舊版的檔補上，已經有的不動），<c>user_version</c> 取 max(現值, <see cref="SchemaVersion"/>)——
    /// 較新版的程式開過的檔不降回來。min_reader 不動（D55）。升級時損毀才算 Corrupt；其他 SQLite 錯誤（被鎖、唯讀、磁碟滿）記錄後照舊版結構繼續開啟，
    /// <see cref="ConversationTablesReady"/> 是 false，之後由 <see cref="RetryUpgrade"/> 或下次開啟再試
    /// </summary>
    private void UpgradeSchema()
    {
        try
        {
            if (IsSchemaCurrent())
                return;
            using var tx = connection.BeginTransaction();
            Execute(connection, tx, SyncStoreSchema.CreateV2);
            Fault("UpgradeSchema:v3");
            Execute(connection, tx, SyncStoreSchema.CreateV3);
            Fault("UpgradeSchema:v4");
            Execute(connection, tx, SyncStoreSchema.CreateV4);
            var version = ParseLong(Scalar(connection, tx, "PRAGMA user_version;")) ?? 0;
            if (version < SchemaVersion)
                Execute(connection, tx, $"PRAGMA user_version = {SchemaVersion};");
            tx.Commit();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            throw new SyncStoreException(SyncStoreFailure.Corrupt, $"同步資料庫損毀（升級結構時 SQLite {ex.SqliteErrorCode}）", ex);
        }
        catch (SqliteException ex)
        {
            Log.Warn($"同步資料庫升級結構失敗（SQLite {ex.SqliteErrorCode}），這次照舊版結構開啟、不發布對話（之後再試）：{ex.Message}");
        }
    }

    /// <summary>結構已是這一版（唯讀查詢）：user_version 夠新，而且 v2～v4 的表與索引都在</summary>
    private bool IsSchemaCurrent()
    {
        if ((ParseLong(Scalar(connection, null, "PRAGMA user_version;")) ?? 0) < SchemaVersion)
            return false;
        IReadOnlyList<string> tablesWanted = [.. SyncStoreSchema.V2Tables, .. SyncStoreSchema.V3Tables, .. SyncStoreSchema.V4Tables];
        IReadOnlyList<string> indexesWanted = [.. SyncStoreSchema.V2Indexes, .. SyncStoreSchema.V3Indexes, .. SyncStoreSchema.V4Indexes];
        using var cmd = connection.CreateCommand();
        var tables = string.Join(", ", tablesWanted.Select((_, i) => "$t" + i.ToString(CultureInfo.InvariantCulture)));
        var indexes = string.Join(", ", indexesWanted.Select((_, i) => "$i" + i.ToString(CultureInfo.InvariantCulture)));
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE (type = 'table' AND name IN ({tables})) OR (type = 'index' AND name IN ({indexes}))";
        for (var i = 0; i < tablesWanted.Count; i++)
            cmd.Parameters.AddWithValue("$t" + i.ToString(CultureInfo.InvariantCulture), tablesWanted[i]);
        for (var i = 0; i < indexesWanted.Count; i++)
            cmd.Parameters.AddWithValue("$i" + i.ToString(CultureInfo.InvariantCulture), indexesWanted[i]);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == tablesWanted.Count + indexesWanted.Count;
    }

    // ── outbox ──

    public IReadOnlyList<OutboxEntry> LoadOutbox()
    {
        using var cmd = Command(null, """
            SELECT device_seq, stream, kind, enc, payload, payload_sha256, coalesce_key, state, created_at,
                   server_seq, reject_reason, acked_at, conversation_id, epoch
            FROM outbox ORDER BY device_seq
            """);
        using var reader = cmd.ExecuteReader();
        var entries = new List<OutboxEntry>();
        while (reader.Read())
            entries.Add(ReadEntry(reader));
        return entries;
    }

    /// <summary>
    /// 一個交易寫進 outbox：合併鍵相同的 Pending 列原地取代 kind、stream、enc、payload、payload_sha256、epoch（deviceSeq 與建立時間不變）；
    /// 否則配新的 deviceSeq（next_device_seq 同一個交易寫）。回傳每一筆寫入後的列（順序同 <paramref name="items"/>）。
    /// 合併只發生在 Pending：送過一次的列內容凍結（OutboxRules.CanCoalesceInto）。
    /// 外框或合併鍵不合格（<see cref="OutboxAppendRules.Problem"/>），或合併鍵的待送列屬於另一個對話（或一邊沒有對話）時丟 <see cref="ArgumentException"/>，
    /// 整批都不寫（程式錯誤）。<paramref name="publish"/>：對話的發布狀態（項目摘要、<c>publish_meta</c>、needs_redo）與這些列同一個交易寫
    /// （WP4.3 計畫 D11：當機時不會以為已送）；kv <c>publisher_next_device_seq</c> 每次都寫（D36：之後開啟時比它大＝中間跑過舊版）。
    /// <paramref name="publish"/> 的 <see cref="ConversationPublishWrite.ScrubPending"/> 在寫這一批的列之前處理，改過的待送列加進 <paramref name="scrubbed"/>（提交之後才有效）
    /// </summary>
    public IReadOnlyList<OutboxEntry> AppendMany(IReadOnlyList<OutboxAppend> items, long now, ConversationPublishWrite? publish = null, List<OutboxEntry>? scrubbed = null)
    {
        ThrowIfDisposed();
        if (items.Count == 0)
            return [];
        for (var i = 0; i < items.Count; i++)
        {
            if (OutboxAppendRules.Problem(items[i]) is { } problem)
                throw new ArgumentException($"outbox 的第 {i + 1} 筆（{items[i].Kind}）不合格：{problem}", nameof(items));
        }

        var next = NextDeviceSeq;
        var written = new List<OutboxEntry>(items.Count);
        // 保留中的對話列數與位元組的增減（提交之後才記，RetainedConversationStats）
        var statsRows = 0;
        var statsBytes = 0L;
        var scrubbedRows = new List<OutboxEntry>();
        using (var tx = connection.BeginTransaction())
        {
            // 政策降級的對話：之前待送的列先處理（這一批的列接在後面、不會合併回去）
            statsBytes += ScrubPendingFor(tx, publish?.ScrubPending, publish?.ScrubAllPending, scrubbedRows);
            using var find = Command(tx, "SELECT device_seq, created_at, conversation_id, octet_length(payload) FROM outbox WHERE coalesce_key = $key AND state = 'pending'");
            var findKey = find.Parameters.Add("$key", SqliteType.Text);
            using var replace = Command(tx, """
                UPDATE outbox SET stream = $stream, kind = $kind, enc = $enc, payload = $payload, payload_sha256 = $sha, conversation_id = $conversation, epoch = $epoch
                WHERE device_seq = $seq AND state = 'pending'
                """);
            using var insert = Command(tx, """
                INSERT INTO outbox (device_seq, stream, kind, enc, payload, payload_sha256, coalesce_key, state, created_at, conversation_id, epoch)
                VALUES ($seq, $stream, $kind, $enc, $payload, $sha, $key, 'pending', $created, $conversation, $epoch)
                """);
            foreach (var item in items)
            {
                var (enc, payload) = PayloadRules.Encode(item.Json);
                var sha = PayloadRules.Sha256Hex(item.Json);
                long? existing = null;
                long createdAt = now;
                long replacedBytes = 0;
                if (item.CoalesceKey is not null)
                {
                    findKey.Value = item.CoalesceKey;
                    using var reader = find.ExecuteReader();
                    if (reader.Read())
                    {
                        existing = reader.GetInt64(0);
                        createdAt = reader.GetInt64(1);
                        var owner = reader.IsDBNull(2) ? null : reader.GetString(2);
                        replacedBytes = reader.GetInt64(3);
                        // 交易還沒提交：丟出去就整批回滾（同一批前面寫的也不留）
                        if (!string.Equals(owner, item.ConversationId, StringComparison.Ordinal))
                            throw new ArgumentException($"合併鍵 {item.CoalesceKey} 的待送列屬於另一個對話", nameof(items));
                    }
                }

                if (item.ConversationId is not null)
                {
                    // 合併只換同一個對話的列（上面檢查過）：列數不變、位元組換成新的
                    statsRows += existing is null ? 1 : 0;
                    statsBytes += System.Text.Encoding.UTF8.GetByteCount(payload) - replacedBytes;
                }

                long seq;
                if (existing is { } found)
                {
                    seq = found;
                    replace.Parameters.Clear();
                    Bind(replace, ("$stream", item.Stream), ("$kind", item.Kind), ("$enc", enc), ("$payload", payload), ("$sha", sha),
                        ("$conversation", item.ConversationId), ("$epoch", item.Epoch), ("$seq", seq));
                    if (replace.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException($"outbox 的合併列 {seq} 不見了");
                }
                else
                {
                    seq = next++;
                    insert.Parameters.Clear();
                    Bind(insert, ("$seq", seq), ("$stream", item.Stream), ("$kind", item.Kind), ("$enc", enc), ("$payload", payload), ("$sha", sha),
                        ("$key", item.CoalesceKey), ("$created", createdAt), ("$conversation", item.ConversationId), ("$epoch", item.Epoch));
                    insert.ExecuteNonQuery();
                }

                Fault("AppendMany:item");
                written.Add(new OutboxEntry(seq, item.Stream, item.Kind, enc, payload, sha, item.CoalesceKey, OutboxState.Pending, createdAt,
                    ConversationId: item.ConversationId, Epoch: item.Epoch));
            }

            if (publish is not null)
                WritePublish(tx, publish);
            PutKv(tx, SyncStoreSchema.NextDeviceSeq, next.ToString(CultureInfo.InvariantCulture));
            PutKv(tx, SyncStoreSchema.PublisherNextDeviceSeq, next.ToString(CultureInfo.InvariantCulture));
            Fault("AppendMany:commit");
            tx.Commit();
        }

        NextDeviceSeq = next;
        AdjustRetainedStats(statsRows, statsBytes);
        scrubbed?.AddRange(scrubbedRows);
        return written;
    }

    /// <summary>一個交易寫回列的狀態（state、server_seq、reject_reason、acked_at；內容送出後凍結，不在這裡改）</summary>
    public void SaveOutbox(IReadOnlyList<OutboxEntry> changed)
    {
        ThrowIfDisposed();
        if (changed.Count == 0)
            return;
        using var tx = connection.BeginTransaction();
        using var update = Command(tx, """
            UPDATE outbox SET state = $state, server_seq = $serverSeq, reject_reason = $reason, acked_at = $ackedAt
            WHERE device_seq = $seq
            """);
        foreach (var entry in changed)
        {
            update.Parameters.Clear();
            Bind(update, ("$state", SyncStoreSchema.StateText(entry.State)), ("$serverSeq", entry.ServerSeq), ("$reason", entry.RejectReason),
                ("$ackedAt", entry.AckedAt), ("$seq", entry.DeviceSeq));
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"outbox 沒有 deviceSeq {entry.DeviceSeq}");
            Fault("SaveOutbox:item");
        }

        tx.Commit();
    }

    public void DeleteOutbox(IReadOnlyList<long> deviceSeqs)
    {
        ThrowIfDisposed();
        if (deviceSeqs.Count == 0)
            return;
        var statsRows = 0;
        var statsBytes = 0L;
        using (var tx = connection.BeginTransaction())
        {
            // RETURNING：刪掉的是不是對話列、payload 幾個位元組（保留中的對話列數與位元組，提交之後才記）
            using var delete = Command(tx, "DELETE FROM outbox WHERE device_seq = $seq RETURNING conversation_id IS NOT NULL, octet_length(payload)");
            var seq = delete.Parameters.Add("$seq", SqliteType.Integer);
            foreach (var deviceSeq in deviceSeqs)
            {
                seq.Value = deviceSeq;
                using var reader = delete.ExecuteReader();
                if (reader.Read() && reader.GetInt64(0) != 0)
                {
                    statsRows--;
                    statsBytes -= reader.GetInt64(1);
                }
            }

            tx.Commit();
        }

        AdjustRetainedStats(statsRows, statsBytes);
    }

    /// <summary>還沒完成的列（不是 Acked／Rejected：Pending、Retry、Sent）</summary>
    public int CountPending()
    {
        ThrowIfDisposed();
        return Convert.ToInt32(ScalarObject(null, "SELECT COUNT(*) FROM outbox WHERE state NOT IN ('acked', 'rejected')"), CultureInfo.InvariantCulture);
    }

    // ── 拉取 ──

    public IReadOnlyDictionary<string, long> LoadCursors()
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "SELECT cls, seq FROM cursors");
        using var reader = cmd.ExecuteReader();
        var cursors = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
            cursors[reader.GetString(0)] = reader.GetInt64(1);
        return cursors;
    }

    public CatalogCache LoadCatalog()
    {
        ThrowIfDisposed();
        var accounts = new Dictionary<string, AccountUpsertPayload>(StringComparer.Ordinal);
        using (var cmd = Command(null, "SELECT logical_account_id, json FROM catalog_accounts"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (SyncJson.Deserialize<AccountUpsertPayload>(reader.GetString(1)) is { } account)
                    accounts[reader.GetString(0)] = account;
            }
        }

        var devices = new Dictionary<string, DeviceUpsertPayload>(StringComparer.Ordinal);
        var revoked = new Dictionary<string, DeviceRevokedPayload>(StringComparer.Ordinal);
        using (var cmd = Command(null, "SELECT device_id, json, revoked_json FROM catalog_devices"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (SyncJson.Deserialize<DeviceUpsertPayload>(reader.GetString(1)) is { } device)
                    devices[id] = device;
                if (!reader.IsDBNull(2) && SyncJson.Deserialize<DeviceRevokedPayload>(reader.GetString(2)) is { } revoke)
                    revoked[id] = revoke;
            }
        }

        var prefs = new Dictionary<string, PrefsSetPayload>(StringComparer.Ordinal);
        using (var cmd = Command(null, "SELECT key, json FROM catalog_prefs"))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (SyncJson.Deserialize<PrefsSetPayload>(reader.GetString(1)) is { } pref)
                    prefs[reader.GetString(0)] = pref;
            }
        }

        return new CatalogCache(accounts, devices, revoked, prefs);
    }

    /// <summary>拉取的結果：目錄快取與游標在同一個交易（中途失敗兩者都不變，下次從舊游標重拉）</summary>
    public void CommitPull(PullDelta delta, IReadOnlyDictionary<string, long> cursors)
    {
        ThrowIfDisposed();
        using var tx = connection.BeginTransaction();
        foreach (var (id, (_, upsert, deleted)) in delta.Accounts)
        {
            if (upsert is not null)
            {
                using var cmd = Command(tx, """
                    INSERT INTO catalog_accounts (logical_account_id, version, json) VALUES ($id, $version, $json)
                    ON CONFLICT (logical_account_id) DO UPDATE SET version = excluded.version, json = excluded.json
                    """);
                Bind(cmd, ("$id", id), ("$version", upsert.Version), ("$json", SyncJson.Serialize(upsert)));
                cmd.ExecuteNonQuery();
            }
            else if (deleted is not null)
            {
                using var cmd = Command(tx, "DELETE FROM catalog_accounts WHERE logical_account_id = $id");
                Bind(cmd, ("$id", id));
                cmd.ExecuteNonQuery();
            }
        }

        Fault("CommitPull:accounts");
        foreach (var (id, device) in delta.Devices)
        {
            using var cmd = Command(tx, """
                INSERT INTO catalog_devices (device_id, json) VALUES ($id, $json)
                ON CONFLICT (device_id) DO UPDATE SET json = excluded.json
                """);
            Bind(cmd, ("$id", id), ("$json", SyncJson.Serialize(device)));
            cmd.ExecuteNonQuery();
        }

        foreach (var (id, revoke) in delta.Revoked)
        {
            using var cmd = Command(tx, """
                INSERT INTO catalog_devices (device_id, json, revoked_json) VALUES ($id, $none, $json)
                ON CONFLICT (device_id) DO UPDATE SET revoked_json = excluded.revoked_json
                """);
            Bind(cmd, ("$id", id), ("$none", SyncStoreSchema.NoDeviceJson), ("$json", SyncJson.Serialize(revoke)));
            cmd.ExecuteNonQuery();
        }

        foreach (var (key, pref) in delta.Prefs)
        {
            using var cmd = Command(tx, """
                INSERT INTO catalog_prefs (key, version, json) VALUES ($key, $version, $json)
                ON CONFLICT (key) DO UPDATE SET version = excluded.version, json = excluded.json
                """);
            Bind(cmd, ("$key", key), ("$version", pref.Version), ("$json", SyncJson.Serialize(pref)));
            cmd.ExecuteNonQuery();
        }

        Fault("CommitPull:catalog");
        WriteCursors(tx, cursors);
        Fault("CommitPull:cursors");
        tx.Commit();
    }

    // ── 伺服器還原的對帳（D15） ──

    /// <summary>
    /// D15 第 1 步：同一個交易裡游標歸 0、清空目錄快取與 published、記 pending_server、reconcile=1、
    /// reconcile_through=NextDeviceSeq−1。seen_server 不變（中途當掉時下次 Hello 仍帶舊的，同樣重來一次）。
    /// 對話的發布狀態與複本也在同一個交易清掉（保留 Ord 與墓碑，WP4.3 計畫 D22；<see cref="ClearConversationTables"/>）
    /// </summary>
    public void BeginServerChange(ServerInstance server)
    {
        ThrowIfDisposed();
        var through = NextDeviceSeq - 1;
        using (var tx = connection.BeginTransaction())
        {
            Execute(connection, tx, "DELETE FROM cursors; DELETE FROM catalog_accounts; DELETE FROM catalog_devices; DELETE FROM catalog_prefs; DELETE FROM published;");
            ClearConversationTables(tx, PublishServerKeyOf(server));
            Fault("BeginServerChange:cleared");
            PutKv(tx, SyncStoreSchema.PendingInstanceId, server.InstanceId);
            PutKv(tx, SyncStoreSchema.PendingRestoreGeneration, server.RestoreGeneration.ToString(CultureInfo.InvariantCulture));
            PutKv(tx, SyncStoreSchema.Reconcile, "1");
            PutKv(tx, SyncStoreSchema.ReconcileThrough, through.ToString(CultureInfo.InvariantCulture));
            tx.Commit();
        }

        reconciling = true;
        reconcileThrough = through;
    }

    /// <summary>D15 第 4 步：seen_server=pending_server、reconcile=0（對帳開始前的列都完成、而且拉到 more:false 之後）</summary>
    public void CompleteServerChange()
    {
        ThrowIfDisposed();
        var pendingId = GetKv(SyncStoreSchema.PendingInstanceId) ?? throw new InvalidOperationException("沒有進行中的伺服器變更");
        var pendingGeneration = ParseLong(GetKv(SyncStoreSchema.PendingRestoreGeneration)) ?? 0;
        using (var tx = connection.BeginTransaction())
        {
            PutKv(tx, SyncStoreSchema.SeenInstanceId, pendingId);
            PutKv(tx, SyncStoreSchema.SeenRestoreGeneration, pendingGeneration.ToString(CultureInfo.InvariantCulture));
            PutKv(tx, SyncStoreSchema.Reconcile, "0");
            DeleteKv(tx, SyncStoreSchema.PendingInstanceId);
            DeleteKv(tx, SyncStoreSchema.PendingRestoreGeneration);
            DeleteKv(tx, SyncStoreSchema.ReconcileThrough);
            Fault("CompleteServerChange:commit");
            tx.Commit();
        }

        seenServer = new ServerInstance(pendingId, pendingGeneration);
        reconciling = false;
        reconcileThrough = null;
    }

    // ── published、名冊、detached、kv ──

    public IReadOnlyDictionary<string, PublishedLink> LoadPublished()
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "SELECT local_account_id, digest, identity_key FROM published");
        using var reader = cmd.ExecuteReader();
        var published = new Dictionary<string, PublishedLink>(StringComparer.Ordinal);
        while (reader.Read())
            published[reader.GetString(0)] = new PublishedLink(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        return published;
    }

    /// <summary>上次送出的摘要；null＝刪除</summary>
    public void SetPublished(string localAccountId, PublishedLink? link)
    {
        ThrowIfDisposed();
        WritePublished(null, localAccountId, link);
    }

    /// <summary>一次寫多筆 published（一個交易、一次 fsync）；null＝刪除</summary>
    public void SetPublishedMany(IReadOnlyList<(string Key, PublishedLink? Link)> changes)
    {
        ThrowIfDisposed();
        if (changes.Count == 0)
            return;
        using var tx = connection.BeginTransaction();
        foreach (var (key, link) in changes)
            WritePublished(tx, key, link);
        Fault("SetPublishedMany:commit");
        tx.Commit();
    }

    /// <summary>清掉所有 published 摘要（全部重送）</summary>
    public void ClearPublished()
    {
        ThrowIfDisposed();
        Execute(connection, null, "DELETE FROM published;");
    }

    private void WritePublished(SqliteTransaction? tx, string localAccountId, PublishedLink? link)
    {
        using var cmd = link is null
            ? Command(tx, "DELETE FROM published WHERE local_account_id = $id")
            : Command(tx, """
                INSERT INTO published (local_account_id, digest, identity_key) VALUES ($id, $digest, $identity)
                ON CONFLICT (local_account_id) DO UPDATE SET digest = excluded.digest, identity_key = excluded.identity_key
                """);
        Bind(cmd, ("$id", localAccountId));
        if (link is not null)
            Bind(cmd, ("$digest", link.Digest), ("$identity", link.IdentityKey));
        cmd.ExecuteNonQuery();
    }

    public (long Rev, IReadOnlyList<DeviceRegistryEntry> Entries) LoadRegistry()
    {
        ThrowIfDisposed();
        var rev = ParseLong(GetKv(SyncStoreSchema.RegistryRev)) ?? 0;
        using var cmd = Command(null, "SELECT json FROM trust_registry ORDER BY device_id");
        using var reader = cmd.ExecuteReader();
        var entries = new List<DeviceRegistryEntry>();
        while (reader.Read())
        {
            if (SyncJson.Deserialize<DeviceRegistryEntry>(reader.GetString(0)) is { } entry)
                entries.Add(entry);
        }

        return (rev, entries);
    }

    /// <summary>整份取代（版本只要不同就取代，不論大小；sync-protocol 7.2）</summary>
    public void SaveRegistry(long rev, IReadOnlyList<DeviceRegistryEntry> entries)
    {
        ThrowIfDisposed();
        using var tx = connection.BeginTransaction();
        WriteRegistry(tx, rev, entries);
        Fault("SaveRegistry:commit");
        tx.Commit();
    }

    /// <summary>本機帳戶 id → 被解除連結時的邏輯帳戶 id（account.deleted 之後不再自動連結）</summary>
    public IReadOnlyDictionary<string, string> LoadDetached()
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "SELECT local_account_id, logical_account_id FROM detached");
        using var reader = cmd.ExecuteReader();
        var detached = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            detached[reader.GetString(0)] = reader.GetString(1);
        return detached;
    }

    public void SetDetached(string localAccountId, string logicalAccountId, long at)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, """
            INSERT INTO detached (local_account_id, logical_account_id, at) VALUES ($local, $logical, $at)
            ON CONFLICT (local_account_id) DO UPDATE SET logical_account_id = excluded.logical_account_id, at = excluded.at
            """);
        Bind(cmd, ("$local", localAccountId), ("$logical", logicalAccountId), ("$at", at));
        cmd.ExecuteNonQuery();
    }

    public void ClearDetached(string logicalAccountId)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "DELETE FROM detached WHERE logical_account_id = $logical");
        Bind(cmd, ("$logical", logicalAccountId));
        cmd.ExecuteNonQuery();
    }

    public string? GetKv(string key)
    {
        ThrowIfDisposed();
        using var cmd = Command(null, "SELECT value FROM kv WHERE key = $key");
        Bind(cmd, ("$key", key));
        return cmd.ExecuteScalar() as string;
    }

    public void SetKv(string key, string value)
    {
        ThrowIfDisposed();
        PutKv(null, key, value);
    }

    /// <summary>測試：這條連線的 PRAGMA 值</summary>
    internal string? Pragma(string name)
    {
        if (name.AsSpan().IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz_") >= 0)
            throw new ArgumentException("PRAGMA 名稱只能是小寫英文與底線", nameof(name));
        return Scalar(connection, null, $"PRAGMA {name};");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        connection.Dispose();
    }

    // ── 內部 ──

    private void LoadState()
    {
        NextDeviceSeq = ParseLong(GetKv(SyncStoreSchema.NextDeviceSeq)) is { } next and >= 1
            ? next
            : throw new SyncStoreException(SyncStoreFailure.Corrupt, "同步資料庫的 next_device_seq 不正確");
        var seenId = GetKv(SyncStoreSchema.SeenInstanceId);
        seenServer = seenId is null ? null : new ServerInstance(seenId, ParseLong(GetKv(SyncStoreSchema.SeenRestoreGeneration)) ?? 0);
        reconciling = GetKv(SyncStoreSchema.Reconcile) == "1";
        reconcileThrough = reconciling ? ParseLong(GetKv(SyncStoreSchema.ReconcileThrough)) : null;
    }

    private void WriteRegistry(SqliteTransaction tx, long rev, IReadOnlyList<DeviceRegistryEntry> entries)
    {
        Execute(connection, tx, "DELETE FROM trust_registry;");
        using var insert = Command(tx, "INSERT INTO trust_registry (device_id, json) VALUES ($id, $json)");
        foreach (var entry in entries)
        {
            insert.Parameters.Clear();
            Bind(insert, ("$id", entry.DeviceId), ("$json", SyncJson.Serialize(entry)));
            insert.ExecuteNonQuery();
        }

        PutKv(tx, SyncStoreSchema.RegistryRev, rev.ToString(CultureInfo.InvariantCulture));
    }

    private void WriteCursors(SqliteTransaction tx, IReadOnlyDictionary<string, long> cursors)
    {
        using var cmd = Command(tx, """
            INSERT INTO cursors (cls, seq) VALUES ($cls, $seq)
            ON CONFLICT (cls) DO UPDATE SET seq = excluded.seq
            """);
        foreach (var (cls, seq) in cursors)
        {
            cmd.Parameters.Clear();
            Bind(cmd, ("$cls", cls), ("$seq", seq));
            cmd.ExecuteNonQuery();
        }
    }

    private void PutKv(SqliteTransaction? tx, string key, string value)
    {
        using var cmd = Command(tx, """
            INSERT INTO kv (key, value) VALUES ($key, $value)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value
            """);
        Bind(cmd, ("$key", key), ("$value", value));
        cmd.ExecuteNonQuery();
    }

    private void DeleteKv(SqliteTransaction tx, string key)
    {
        using var cmd = Command(tx, "DELETE FROM kv WHERE key = $key");
        Bind(cmd, ("$key", key));
        cmd.ExecuteNonQuery();
    }

    private static OutboxEntry ReadEntry(SqliteDataReader r)
    {
        var state = SyncStoreSchema.ParseState(r.GetString(7))
            ?? throw new SyncStoreException(SyncStoreFailure.Corrupt, $"outbox 的狀態不認得：{r.GetString(7)}");
        return new OutboxEntry(
            r.GetInt64(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.GetString(4),
            r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            state,
            r.GetInt64(8),
            r.IsDBNull(9) ? null : r.GetInt64(9),
            r.IsDBNull(10) ? null : r.GetString(10),
            r.IsDBNull(11) ? null : r.GetInt64(11),
            r.IsDBNull(12) ? null : r.GetString(12),
            r.IsDBNull(13) ? null : r.GetInt64(13));
    }

    private SqliteCommand Command(SqliteTransaction? tx, string sql)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }

    private object? ScalarObject(SqliteTransaction? tx, string sql)
    {
        using var cmd = Command(tx, sql);
        return cmd.ExecuteScalar();
    }

    private static void Bind(SqliteCommand cmd, params (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool TableExists(SqliteConnection connection, string table, SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static long? ParseLong(string? text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    private void Fault(string point) => FaultInjection?.Invoke(point);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private static void DeleteSidecars(string path)
    {
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 刪不掉就留著：下次配對前 PairingRecovery 會再清一次
        }
    }
}
