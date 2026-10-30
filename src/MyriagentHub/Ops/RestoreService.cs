using System.Globalization;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Ops;

/// <param name="SyncSeqFloor">sync.db 還原後新事件的 seq 從這個值之後開始（沒有 sync.db 為 null）</param>
internal sealed record RestoreResult(string From, long Generation, string PreRestoreFolder, int RevokedSessions, IReadOnlyList<string> Files, long? SyncSeqFloor = null);

/// <summary>還原失敗的原因（代碼給 admin 指令的 JSON 與結束碼用）</summary>
internal sealed class RestoreException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// <c>MyriagentHub.exe admin restore --from backups\daily\… --data C:\ProgramData\MyriagentHub</c>（9.9）：服務必須已停止。
/// 驗 manifest 的 SHA-256 → 在暫存資料夾複製並跑 integrity_check → 目前的資料庫檔移到 backups\pre-restore-時間\（不刪）→ 放回 →
/// restore_generation＋1、撤銷所有瀏覽器工作階段（還原會讓之後才撤銷的工作階段復活）、寫一筆稽核。
/// sync.db（備份裡有就是還原的，沒有就是線上那份）：restore_generation 設成同一個值、events 的 seq 起點拉高 10 億
/// （新事件的 seq 不會與還原前已發給裝置的重複，裝置的游標不會跳過新事件）、每台裝置標成要對帳（needs_resync，設計 10.2、sync-protocol 第 9 節）。
/// 第 4 階段起另把所有對話標成 recovering。
/// </summary>
internal static class RestoreService
{
    public static RestoreResult Restore(HubPaths paths, string fromFolder, TimeProvider time)
    {
        using var serviceLock = ServiceLock.TryAcquire(paths, "admin-restore")
            ?? throw new RestoreException("service_running", "MyriagentHub 服務仍在執行（status\\service.lock 被鎖住）：請先 Stop-Service MyriagentHub");

        var from = Path.GetFullPath(fromFolder);
        var manifest = BackupService.ReadManifest(from)
            ?? throw new RestoreException("manifest_missing", $"找不到 {Path.Combine(from, BackupService.ManifestFile)}：不是完整的備份");
        if (manifest.Files.Count == 0 || manifest.Files.All(f => !f.Name.Equals("hub.db", StringComparison.OrdinalIgnoreCase)))
            throw new RestoreException("manifest_invalid", "manifest 裡沒有 hub.db");

        var stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var staging = UniqueDirectory(Path.Combine(paths.TmpDir, "restore-" + stamp));
        try
        {
            // 1. 驗證每個檔：存在、大小與 SHA-256 符合，暫存複本的 integrity_check 是 ok
            foreach (var file in manifest.Files)
            {
                if (file.Name.Contains('/') || file.Name.Contains('\\') || file.Name.Contains(".."))
                    throw new RestoreException("manifest_invalid", "manifest 的檔名不合法：" + file.Name);
                var source = Path.Combine(from, file.Name);
                if (!File.Exists(source))
                    throw new RestoreException("file_missing", "備份裡少了 " + file.Name);
                var actual = BackupService.Sha256File(source);
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new RestoreException("hash_mismatch", $"{file.Name} 的 SHA-256 與 manifest 不符（備份被改過或損毀）");
                var copy = Path.Combine(staging, file.Name);
                File.Copy(source, copy, overwrite: true);
                string integrity;
                try
                {
                    integrity = BackupService.Inspect(copy).Integrity;
                }
                catch (SqliteException ex)
                {
                    integrity = "SQLite " + ex.SqliteErrorCode + ": " + ex.Message;
                }
                if (integrity != "ok")
                    throw new RestoreException("integrity_failed", $"{file.Name} 的 integrity_check：{integrity}");
            }

            // 世代要跨還原遞增：取「目前線上」與「備份裡」較大者再＋1（備份是舊的，裡面的世代可能比線上小）
            var currentGeneration = File.Exists(paths.HubDb) ? TryReadGeneration(paths.HubDb) : 0;
            // seq 也一樣：線上那份發過的 seq（可能是上一次還原之後的 10 億起）不能在新的起點之後被重用
            var currentSeqHigh = File.Exists(paths.SyncDb) ? TryReadSeqHigh(paths.SyncDb) : 0;

            // 2. 目前的資料庫檔（含 -wal、-shm）整組移到 pre-restore（不刪，萬一要反悔）。
            //    服務已停止（service.lock）；這個程序自己開過的連線（讀世代）都已 Dispose，不用連線池所以檔案已關
            var preRestore = UniqueDirectory(Path.Combine(paths.BackupsDir, "pre-restore-" + stamp));
            Directory.CreateDirectory(paths.DbDir);
            foreach (var file in manifest.Files)
            {
                foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
                {
                    var current = Path.Combine(paths.DbDir, file.Name + suffix);
                    if (File.Exists(current))
                        File.Move(current, Path.Combine(preRestore, file.Name + suffix));
                }
            }

            // 3. 放回
            foreach (var file in manifest.Files)
                File.Move(Path.Combine(staging, file.Name), Path.Combine(paths.DbDir, file.Name));

            // 4. hub.db：restore_generation＋1、撤銷所有瀏覽器工作階段、稽核（直接 SQL：還原的檔案可能是較舊的結構，服務啟動時才遷移）
            var hubDb = paths.HubDb;
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();
            long generation;
            int revoked;
            using (var connection = BackupService.OpenUnpooled(hubDb))
            using (var tx = connection.BeginTransaction())
            {
                generation = Math.Max(currentGeneration, ReadGeneration(connection, tx)) + 1;
                Execute(connection, tx, "INSERT INTO kv(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                    ("$k", KvKeys.RestoreGeneration), ("$v", generation.ToString(CultureInfo.InvariantCulture)));
                revoked = Execute(connection, tx, "UPDATE browser_sessions SET revoked_at = $now WHERE revoked_at IS NULL", ("$now", now));
                AppendAudit(connection, tx, new AuditContent(now, AuditActors.Admin, null, null, AuditActions.Restore, Path.GetFileName(from), AuditOutcomes.Ok,
                    System.Text.Json.JsonSerializer.Serialize(new { from = Path.GetFileName(from), generation, revokedSessions = revoked, createdAt = manifest.CreatedAt, build = manifest.Build }, KvStore.Json)));
                tx.Commit();
            }

            // 5. sync.db：同一個世代、seq 跳號、每台裝置要對帳
            var seqFloor = File.Exists(paths.SyncDb) ? MarkSyncRestored(paths.SyncDb, generation, currentSeqHigh) : null;
            return new RestoreResult(from, generation, preRestore, revoked, manifest.Files.Select(f => f.Name).ToList(), seqFloor);
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 暫存資料夾刪不掉不影響還原結果
            }
        }
    }

    /// <summary>
    /// sync.db 還原後的標記（直接 SQL，服務已停止）：server_meta.restore_generation＝<paramref name="generation"/>、
    /// events 的 AUTOINCREMENT 起點＝max(目前起點, 最大 seq, 還原前線上的最大 seq <paramref name="onlineSeqHigh"/>)＋10 億、所有 device_streams.needs_resync＝1、
    /// 有 conversations 表時還沒刪除的對話 lease_state＝recovering（WP4.2 步 5）。
    /// 還沒有同步結構（server_meta 不存在）就不動。回傳之後新事件的 seq 從哪個值之後開始。
    /// </summary>
    internal static long? MarkSyncRestored(string syncDb, long generation, long onlineSeqHigh = 0)
    {
        using var connection = BackupService.OpenUnpooled(syncDb);
        using var tx = connection.BeginTransaction();
        using (var check = connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('server_meta', 'events', 'device_streams')";
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) < 3)
                return null;
        }
        SyncSchema.SetMeta(connection, tx, SyncSchema.RestoreGenerationKey, generation.ToString(CultureInfo.InvariantCulture));
        Execute(connection, tx, "INSERT INTO sqlite_sequence(name, seq) SELECT 'events', 0 WHERE NOT EXISTS (SELECT 1 FROM sqlite_sequence WHERE name = 'events')");
        Execute(connection, tx, "UPDATE sqlite_sequence SET seq = MAX(seq, (SELECT IFNULL(MAX(seq), 0) FROM events), $online) + $jump WHERE name = 'events'",
            ("$online", onlineSeqHigh), ("$jump", SyncSchema.RestoreSeqJump));
        Execute(connection, tx, "UPDATE device_streams SET needs_resync = 1");
        // 第 4 階段（WP4.2 步 5、D16）：還沒刪除的對話都標成 recovering，擁有者在對帳模式之外送新的 conv.created 才解除；
        // 第 3 階段的備份（v1，沒有 conversations 表）不動
        using (var conversations = connection.CreateCommand())
        {
            conversations.Transaction = tx;
            conversations.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = 'conversations'";
            if (Convert.ToInt64(conversations.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
                Execute(connection, tx, "UPDATE conversations SET lease_state = 'recovering' WHERE deleted_at IS NULL");
        }
        long floor;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT seq FROM sqlite_sequence WHERE name = 'events'";
            floor = Convert.ToInt64(read.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        tx.Commit();
        return floor;
    }

    /// <summary>建立不重複的資料夾（同一秒內連續還原也不會撞名）</summary>
    private static string UniqueDirectory(string basePath)
    {
        var path = basePath;
        for (var i = 2; Directory.Exists(path); i++)
            path = basePath + "-" + i.ToString(CultureInfo.InvariantCulture);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>線上資料庫的世代（讀不到——檔案損毀、沒有 kv 表——當 0）</summary>
    /// <summary>線上 sync.db 發過的最大 seq（AUTOINCREMENT 起點與 events 的最大值取大者）；讀不到（還沒有同步結構、檔案壞了）是 0</summary>
    private static long TryReadSeqHigh(string path)
    {
        try
        {
            using var connection = BackupService.OpenUnpooled(path);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT MAX(IFNULL((SELECT seq FROM sqlite_sequence WHERE name = 'events'), 0), IFNULL((SELECT MAX(seq) FROM events), 0))";
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static long TryReadGeneration(string path)
    {
        try
        {
            using var connection = BackupService.OpenUnpooled(path);
            using var tx = connection.BeginTransaction(deferred: true);
            return ReadGeneration(connection, tx);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    private static long ReadGeneration(SqliteConnection connection, SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM kv WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", KvKeys.RestoreGeneration);
        return cmd.ExecuteScalar() is string s && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : 0;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>在服務外附加一列稽核（與 AuditLog 相同的雜湊鏈規則）</summary>
    private static void AppendAudit(SqliteConnection connection, SqliteTransaction tx, AuditContent content)
    {
        byte[] prev;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT hash FROM audit ORDER BY seq DESC LIMIT 1";
            prev = cmd.ExecuteScalar() as byte[] ?? AuditRules.GenesisHash();
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO audit(at, actor_type, actor_id, ip, action, target, outcome, detail, prev_hash, hash) VALUES($at, $type, $id, $ip, $action, $target, $outcome, $detail, $prev, $hash)";
        insert.Parameters.AddWithValue("$at", content.At);
        insert.Parameters.AddWithValue("$type", content.ActorType);
        insert.Parameters.AddWithValue("$id", (object?)content.ActorId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$ip", (object?)content.Ip ?? DBNull.Value);
        insert.Parameters.AddWithValue("$action", content.Action);
        insert.Parameters.AddWithValue("$target", (object?)content.Target ?? DBNull.Value);
        insert.Parameters.AddWithValue("$outcome", content.Outcome);
        insert.Parameters.AddWithValue("$detail", (object?)content.Detail ?? DBNull.Value);
        insert.Parameters.AddWithValue("$prev", prev);
        insert.Parameters.AddWithValue("$hash", AuditRules.ComputeHash(prev, content));
        insert.ExecuteNonQuery();
    }
}
