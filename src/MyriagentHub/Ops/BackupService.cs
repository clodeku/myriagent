using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Ops;

/// <summary>備份裡的一個資料庫檔（manifest 的一列；符合「記下產出指紋（筆數＋SHA-256）」）</summary>
internal sealed record BackupFileInfo(string Name, long Bytes, string Sha256, string Integrity, IReadOnlyDictionary<string, long> Tables);

/// <summary>backups\daily\&lt;日期&gt;\manifest.json：寫在最後，有它才代表備份完整</summary>
/// <param name="BlobCount">快照的 sync.db 記的 blob 數（WP4.7 D18；blob 本身不備份）。之前的版本寫的 manifest 沒有這三個欄位（null），版本號照舊是 1</param>
/// <param name="BlobBytes">那些 blob 的合計位元組</param>
/// <param name="BlobInventorySha256"><see cref="BackupService.BlobInventoryFile"/> 的 SHA-256（還原時比對哪些 blob 還在）</param>
internal sealed record BackupManifest(int Format, long CreatedAt, string Version, string Build, string Reason, IReadOnlyList<BackupFileInfo> Files,
    long? BlobCount = null, long? BlobBytes = null, string? BlobInventorySha256 = null);

/// <summary>快照裡的 blob 清單的摘要（<see cref="BackupService.WriteBlobInventory"/>）</summary>
internal sealed record BlobInventory(long Count, long Bytes, string Sha256);

/// <summary>kv backup_state</summary>
internal sealed record BackupState(long? LastSuccessAt, string? LastFolder, string? LastIntegrity, long? LastAttemptAt, string? LastError);

internal sealed record BackupResult(bool Ok, string? Folder, string? Error, BackupManifest? Manifest, IReadOnlyList<string> Expired);

/// <summary>
/// 每日備份（9.9）：對每個資料庫做 VACUUM INTO（線上一致快照，不擋寫入）→ 對快照跑 PRAGMA integrity_check → 寫 manifest
/// （各表筆數、大小、SHA-256）；保留 <c>Hub:Backup:KeepDaily</c> 份每日＋<c>KeepWeekly</c> 份每週、合計不超過 <c>MaxTotalBytes</c>。
/// 備份之前先看磁碟（2026-10-25）：存了之後可用空間會低於 <c>MinFreeBytes</c> 就先刪最舊的備份騰出空間，還是不夠就不備份
/// （<see cref="BackupRules.PlanSpace"/>；系統磁碟滿了會拖垮整台主機）。在背景模式執行緒上跑（低 I/O 與記憶體優先權，不和正式 SQL Server 搶）。
/// </summary>
internal sealed class BackupService(HubPaths paths, KvStore kv, AuditLog audit, TimeProvider time, IOptions<HubOptions> options, IDiskSpaceProbe disk,
    ILogger<BackupService> log)
{
    public const string ManifestFile = "manifest.json";

    /// <summary>
    /// 備份資料夾裡的 blob 清單（WP4.7 D18）：快照的 sync.db 的 <c>blobs</c> 表，每列 <c>sha size kind</c>、依 sha 排序、LF 換行。
    /// 不在 manifest 的 <c>Files</c> 裡（還原只放回資料庫檔）
    /// </summary>
    public const string BlobInventoryFile = "blobs-inventory.txt";

    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile BackupState? cached;

    public BackupState? LastState => cached;

    public async Task LoadStateAsync(CancellationToken cancellationToken) =>
        cached = await kv.GetAsync<BackupState>(KvKeys.BackupState, cancellationToken);

    public async Task<BackupResult> RunAsync(string reason, bool scheduled, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var nowLocal = time.GetLocalNow();
            var name = BackupRules.FolderName(nowLocal, scheduled);
            var folder = Path.Combine(paths.DailyBackupsDir, name);
            if (scheduled && File.Exists(Path.Combine(folder, ManifestFile)))
                return new BackupResult(true, folder, null, ReadManifest(folder), []);

            var attemptAt = time.GetUtcNow().ToUnixTimeMilliseconds();
            try
            {
                var sources = DatabaseFiles(paths);
                var made = MakeRoom(sources.Sum(DatabaseBytes));
                Directory.CreateDirectory(folder);
                var files = new List<BackupFileInfo>();
                foreach (var source in sources)
                {
                    var target = Path.Combine(folder, Path.GetFileName(source));
                    files.Add(await RunInBackgroundModeAsync(() => SnapshotDatabase(source, target)));
                }
                // blob 不備份（和本體同一顆 C:），只記下快照當下有哪些（D18）
                var syncSnapshot = Path.Combine(folder, Path.GetFileName(paths.SyncDb));
                var inventory = File.Exists(syncSnapshot)
                    ? await RunInBackgroundModeAsync(() => WriteBlobInventory(syncSnapshot, Path.Combine(folder, BlobInventoryFile)))
                    : null;
                var build = BuildInfo.Current;
                var manifest = new BackupManifest(1, attemptAt, build.Version, build.Build, reason, files, inventory?.Count, inventory?.Bytes, inventory?.Sha256);
                WriteJsonAtomically(Path.Combine(folder, ManifestFile), manifest);
                var bad = files.FirstOrDefault(f => f.Integrity != "ok");
                if (bad is not null)
                    throw new InvalidDataException($"快照 {bad.Name} 的 integrity_check 不是 ok：{bad.Integrity}");

                cached = new BackupState(attemptAt, name, "ok", attemptAt, null);
                await kv.SetAsync(KvKeys.BackupState, cached, cancellationToken);
                var expired = made.Concat(ApplyRetention()).ToList();
                await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, AuditActions.Backup, name, AuditOutcomes.Ok,
                    new { reason, files = files.Select(f => new { f.Name, f.Bytes, f.Sha256 }), expired }), cancellationToken);
                log.LogInformation("備份完成：{Folder}（{Files}）", folder, string.Join(", ", files.Select(f => $"{f.Name} {f.Bytes} bytes")));
                return new BackupResult(true, folder, null, manifest, expired);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "備份失敗：{Folder}", folder);
                cached = new BackupState(cached?.LastSuccessAt, cached?.LastFolder, cached?.LastIntegrity, attemptAt, ex.GetType().Name + ": " + ex.Message);
                await kv.SetAsync(KvKeys.BackupState, cached, cancellationToken);
                await audit.WriteAsync(new AuditEntry(AuditActors.System, null, null, AuditActions.Backup, name, AuditOutcomes.Error,
                    new { reason, error = ex.GetType().Name }), cancellationToken);
                return new BackupResult(false, folder, ex.Message, null, []);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>要備份的資料庫（hub.db、sync.db）</summary>
    public static IReadOnlyList<string> DatabaseFiles(HubPaths paths) =>
        new[] { paths.HubDb, paths.SyncDb }.Where(File.Exists).ToList();

    /// <summary>VACUUM INTO 到暫存檔 → integrity_check → 改名；回傳 manifest 的一列</summary>
    public static BackupFileInfo SnapshotDatabase(string source, string target)
    {
        var temp = target + ".tmp";
        DeleteDatabaseFiles(temp);
        // 命令逾時依來源的大小放寬（D41：sync.db 會因回補長到幾 GB）
        using (var connection = OpenUnpooled(source, BackupTimeoutRules.CommandTimeoutSeconds(DatabaseBytes(source))))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "VACUUM INTO $target";
            cmd.Parameters.AddWithValue("$target", temp);
            cmd.ExecuteNonQuery();
        }
        var (integrity, tables) = Inspect(temp, BackupTimeoutRules.CommandTimeoutSeconds(DatabaseBytes(temp)));
        DeleteDatabaseFiles(target);
        File.Move(temp, target);
        return new BackupFileInfo(Path.GetFileName(target), new FileInfo(target).Length, Sha256File(target), integrity, tables);
    }

    /// <summary>integrity_check 與各表筆數（連線不用池，結束就關檔）；備份傳依大小放寬的逾時（D41），preflight 與還原照舊 60 秒</summary>
    public static (string Integrity, IReadOnlyDictionary<string, long> Tables) Inspect(string path, int timeoutSeconds = BackupTimeoutRules.MinSeconds)
    {
        using var connection = OpenUnpooled(path, timeoutSeconds);
        var integrity = SqliteSetup.Scalar(connection, "PRAGMA integrity_check")?.ToString() ?? "(none)";
        var names = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                names.Add(reader.GetString(0));
        }
        var tables = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var quoted = "\"" + name.Replace("\"", "\"\"") + "\"";
            tables[name] = Convert.ToInt64(SqliteSetup.Scalar(connection, "SELECT COUNT(*) FROM " + quoted));
        }
        return (integrity, tables);
    }

    /// <summary>
    /// 從快照的 sync.db 寫 blob 清單（<see cref="BlobInventoryFile"/>；先寫暫存檔再改名）；快照沒有 <c>blobs</c> 表（v2 之前的結構）回 null、不寫檔
    /// </summary>
    public static BlobInventory? WriteBlobInventory(string syncSnapshot, string target)
    {
        using var connection = OpenUnpooled(syncSnapshot, BackupTimeoutRules.CommandTimeoutSeconds(DatabaseBytes(syncSnapshot)));
        if (Convert.ToInt64(SqliteSetup.Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'blobs'")) == 0)
            return null;
        var temp = target + ".tmp";
        long count = 0;
        long bytes = 0;
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var text = new StreamWriter(file, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { NewLine = "\n" })
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT sha256, size, kind FROM blobs ORDER BY sha256";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var size = reader.GetInt64(1);
                text.Write(reader.GetString(0));
                text.Write(' ');
                text.Write(size.ToString(System.Globalization.CultureInfo.InvariantCulture));
                text.Write(' ');
                text.Write(reader.GetString(2));
                text.Write('\n');
                count++;
                bytes += size;
            }
        }

        File.Move(temp, target, overwrite: true);
        return new BlobInventory(count, bytes, Sha256File(target));
    }

    public static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static BackupManifest? ReadManifest(string folder)
    {
        var path = Path.Combine(folder, ManifestFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), StatusJson.Options) : null;
    }

    public static void WriteJsonAtomically<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, StatusJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    public static void DeleteDatabaseFiles(string path)
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            if (File.Exists(path + suffix))
                File.Delete(path + suffix);
        }
    }

    /// <summary>
    /// 備份、還原用的連線：與其他連線一樣不用池（<see cref="SqliteSetup"/>），命令逾時預設放寬到 60 秒（VACUUM INTO、integrity_check）；
    /// 備份依檔案大小傳入 <see cref="BackupTimeoutRules.CommandTimeoutSeconds"/>（D41），還原的呼叫用預設值
    /// </summary>
    public static SqliteConnection OpenUnpooled(string path, int timeoutSeconds = BackupTimeoutRules.MinSeconds) =>
        SqliteSetup.Open(path, defaultTimeoutSeconds: timeoutSeconds);

    /// <summary>資料庫檔與 -wal 的位元組數（不存在的當 0）</summary>
    public static long DatabaseBytes(string path) =>
        (File.Exists(path) ? new FileInfo(path).Length : 0) + (File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0);

    /// <summary>
    /// 備份之前騰出空間（<see cref="BackupRules.PlanSpace"/>）：回傳為此刪掉的資料夾；刪了最舊的備份還是放不下就丟 <see cref="BackupDiskLowException"/>（一份都不刪）
    /// </summary>
    private IReadOnlyList<string> MakeRoom(long estimate)
    {
        var settings = options.Value.Backup;
        var free = disk.FreeBytes(paths.DailyBackupsDir);
        var plan = BackupRules.PlanSpace(FolderSizes(), estimate, free, settings.MinFreeBytes);
        if (!plan.Fits)
        {
            throw new BackupDiskLowException(string.Create(CultureInfo.InvariantCulture,
                $"磁碟空間不夠：可用 {free} bytes，這次約 {estimate} bytes，備份之後至少要剩 {settings.MinFreeBytes} bytes（Hub:Backup:MinFreeBytes）"));
        }
        if (plan.Delete.Count > 0)
            log.LogWarning("磁碟空間不夠，先刪最舊的備份騰出空間：{Names}", string.Join(", ", plan.Delete));
        return DeleteFolders(plan.Delete);
    }

    /// <summary>保留：份數（<see cref="BackupRules.SelectExpired"/>）之後再看合計的上限（<see cref="BackupRules.SelectOverBudget"/>）</summary>
    private IReadOnlyList<string> ApplyRetention()
    {
        var settings = options.Value.Backup;
        var expired = DeleteFolders(BackupRules.SelectExpired(FolderSizes().Select(f => f.Name), settings.KeepDaily, settings.KeepWeekly)).ToList();
        var over = BackupRules.SelectOverBudget(FolderSizes(), settings.MaxTotalBytes);
        if (over.Count > 0)
            log.LogWarning("備份合計超過 Hub:Backup:MaxTotalBytes，刪掉最舊的：{Names}", string.Join(", ", over));
        expired.AddRange(DeleteFolders(over));
        return expired;
    }

    /// <summary><c>backups\daily\</c> 底下的資料夾與各自的大小（檔案合計）</summary>
    private List<BackupFolderSize> FolderSizes()
    {
        if (!Directory.Exists(paths.DailyBackupsDir))
            return [];
        return Directory.GetDirectories(paths.DailyBackupsDir)
            .Select(dir => new BackupFolderSize(Path.GetFileName(dir), FolderBytes(dir)))
            .ToList();
    }

    private static long FolderBytes(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>刪掉這些備份資料夾；回傳真的刪掉的（刪不掉的記警告）</summary>
    private List<string> DeleteFolders(IEnumerable<string> names)
    {
        var deleted = new List<string>();
        foreach (var name in names)
        {
            try
            {
                Directory.Delete(Path.Combine(paths.DailyBackupsDir, name), recursive: true);
                deleted.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "刪除過期備份失敗：{Name}", name);
            }
        }
        return deleted;
    }

    /// <summary>在背景模式執行緒上跑（THREAD_MODE_BACKGROUND_BEGIN：低 I/O 與記憶體優先權）</summary>
    private static Task<T> RunInBackgroundModeAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var background = NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.ThreadModeBackgroundBegin);
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
            finally
            {
                if (background)
                    NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.ThreadModeBackgroundEnd);
            }
        })
        {
            IsBackground = true,
            Name = "MyriagentHub 備份",
        };
        thread.Start();
        return tcs.Task;
    }

    private static class NativeMethods
    {
        public const int ThreadModeBackgroundBegin = 0x00010000;
        public const int ThreadModeBackgroundEnd = 0x00020000;

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetThreadPriority(IntPtr thread, int priority);
    }
}

/// <summary>備份之前的磁碟空間不夠（刪了最舊的備份也放不下）：這次不備份</summary>
internal sealed class BackupDiskLowException(string message) : IOException(message);
