using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Ops;
using MyriagentHub.Releases;
using MyriagentHub.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MyriagentHub.Admin;

internal sealed record PreflightCheck(string Name, bool Ok, string? Detail);

internal sealed record PreflightResult(bool Ok, string Version, string Build, IReadOnlyList<PreflightCheck> Checks, IReadOnlyList<string> PendingMigrations, IReadOnlyList<string> UnknownMigrations);

/// <summary>
/// 部署前檢查（9.8 步驟 b；docs/hub-setup.md 第 11 節）：以新版程式讀設定並驗證、對線上資料庫做唯讀的一致快照後在副本上試跑遷移。
/// 部署時以 runner 帳戶執行：對 db、config 只有讀取權限、只能寫自己的 %TEMP%，所以這裡絕不寫資料根（連暫存檔都不放），
/// 副本與探測檔一律放在 <c>workRoot</c>（%TEMP%）底下、用完即刪；180 秒內結束（安裝腳本的逾時）。
/// </summary>
internal static class Preflight
{
    public static async Task<PreflightResult> RunAsync(HubPaths paths, string workRoot, CancellationToken cancellationToken)
    {
        var checks = new List<PreflightCheck>();
        var pending = new List<string>();
        var unknown = new List<string>();

        // 1. 設定（與服務相同的來源：程式資料夾的 appsettings*.json＋資料根 config\）
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Production.json"), optional: true)
                .AddJsonFile(Path.Combine(paths.ConfigDir, "appsettings.json"), optional: true)
                .AddJsonFile(Path.Combine(paths.ConfigDir, "appsettings.Production.json"), optional: true)
                .Build();
            var o = configuration.GetSection(HubOptions.Section).Get<HubOptions>() ?? new HubOptions();
            checks.Add(new PreflightCheck("config.server_domain", IsDomain(o.ServerDomain), o.ServerDomain));
            checks.Add(new PreflightCheck("config.https_port", o.HttpsPort is > 0 and <= 65535, o.HttpsPort.ToString(CultureInfo.InvariantCulture)));
            checks.Add(FromTry("config.public_origin", () => HostingRules.PublicOrigin(o)));
            checks.Add(FromTry("config.bind_address", () => HostingRules.ParseBindAddress(o.BindAddress).Kind.ToString()));
            checks.Add(FromTry("config.backup_daily_at", () => BackupRules.ParseDailyAt(o.Backup.DailyAt).ToString("HH:mm", CultureInfo.InvariantCulture)));
            checks.Add(BackupRules.Validate(o.Backup) is { } backupProblem
                ? new PreflightCheck("config.backup", false, backupProblem)
                : new PreflightCheck("config.backup", true, string.Create(CultureInfo.InvariantCulture,
                    $"keepDaily={o.Backup.KeepDaily} keepWeekly={o.Backup.KeepWeekly} maxTotalBytes={o.Backup.MaxTotalBytes} minFreeBytes={o.Backup.MinFreeBytes}")));
            checks.Add(FromTry("config.cert_store", () => HostingRules.UseCertificateStore(o.Tls.CertStore, isProduction: true) ? "store" : "file"));
            checks.Add(FromTry("config.deploy_drain_max_seconds", () => DrainRules.ValidateDeployDrainMaxSeconds(o.Deploy.DrainMaxSeconds).ToString(CultureInfo.InvariantCulture)));
            checks.Add(ConversationSettingsRules.Validate(o.Conversations) is { } conversationProblem
                ? new PreflightCheck("config.conversations", false, conversationProblem)
                : new PreflightCheck("config.conversations", true, null));
            checks.Add(MediaOptionsRules.Validate(o.Media) is { } mediaProblem
                ? new PreflightCheck("config.media", false, mediaProblem)
                : new PreflightCheck("config.media", true, null));
            checks.Add(FromTry("config.releases", () => AppReleaseRules.ValidateSettings(o.Releases) is { } releaseError
                ? throw new InvalidOperationException(releaseError)
                : string.Create(CultureInfo.InvariantCulture,
                    $"keepAvailable={o.Releases.KeepAvailable} maxZipMb={o.Releases.MaxZipMb} minFreeDiskMb={o.Releases.MinFreeDiskMb} inboxPollSeconds={o.Releases.InboxPollSeconds}")));
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            checks.Add(new PreflightCheck("config.load", false, ex.Message));
        }

        // 2. 資料根讀得到（runner 帳戶對 config、db 只有讀取權限；資料根本身沒有任何權限，不列舉它）
        checks.Add(CheckReadable(paths));

        // 3. 自己的暫存資料夾寫得進去（副本放這裡）
        var work = Path.Combine(workRoot, "myriagenthub-preflight-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        var workCheck = CheckWritable(work);
        checks.Add(workCheck);
        try
        {
            // 4. 資料庫：唯讀快照 → 副本上試跑遷移 → integrity_check
            if (!workCheck.Ok)
            {
                checks.Add(new PreflightCheck("db.migrate_copy", false, "暫存資料夾寫不進去，沒有試跑遷移"));
            }
            else if (File.Exists(paths.HubDb))
            {
                var copyPaths = new HubPaths(work);
                Directory.CreateDirectory(copyPaths.DbDir);
                try
                {
                    var method = SnapshotReadOnly(paths.HubDb, copyPaths.HubDb);
                    // 連線不用池（SqliteSetup）：服務容器與 scope 釋放時連線就關檔，之後的 Inspect 與刪除暫存資料夾不會被鎖住
                    await using (var services = HubDbContextFactory.CreateServices(copyPaths.HubDb))
                    {
                        using var scope = services.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
                        pending.AddRange(await db.Database.GetPendingMigrationsAsync(cancellationToken));
                        unknown.AddRange((await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Except(db.Database.GetMigrations()));
                        await db.Database.MigrateAsync(cancellationToken);
                    }
                    var (integrity, _) = BackupService.Inspect(copyPaths.HubDb);
                    checks.Add(new PreflightCheck("db.migrate_copy", integrity == "ok", $"snapshot={method} pending={pending.Count} integrity={integrity}"));
                    // 資料庫比程式新（回滾到舊版）：遷移採先擴充、下一版才收縮，舊版照樣能跑，只提示
                    checks.Add(new PreflightCheck("db.unknown_migrations", true, unknown.Count == 0 ? null : string.Join(",", unknown)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    checks.Add(new PreflightCheck("db.migrate_copy", false, ex.GetType().Name + ": " + ex.Message));
                }
            }
            else
            {
                checks.Add(new PreflightCheck("db.migrate_copy", true, "尚無 hub.db：服務第一次啟動時建立"));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 暫存副本刪不掉不影響結果
            }
        }

        var build = BuildInfo.Current;
        return new PreflightResult(checks.All(c => c.Ok), build.Version, build.Build, checks, pending, unknown);
    }

    /// <summary>immutable 讀取途中主檔被改（服務的最後一條連線關閉時做檢查點）時，整個快照最多做幾次</summary>
    public const int SnapshotAttempts = 5;

    /// <summary>
    /// 以唯讀身分對線上資料庫做一致快照（VACUUM INTO 到 <paramref name="target"/>），回傳用的方法：
    /// <list type="bullet">
    /// <item>readonly：唯讀連線（有 -wal、-shm 時：服務執行中一直開著保活連線，見 <see cref="HubDatabase"/>）。讀得到 WAL 裡
    /// 已提交的交易；-shm 只能唯讀開啟時 SQLite（3.22 起）改用私有記憶體建索引，不寫任何檔。</item>
    /// <item>immutable：唯讀連線開不了、而且 WAL 不存在或是空的。WAL 模式的資料庫沒有 -wal 時，唯讀連線要先建立 -wal／-shm
    /// 才開得了，沒有寫入權就失敗；這時主檔就是全部已提交的資料（連線不用池，最後一條連線關閉時 SQLite 已做完檢查點並刪掉
    /// -wal、-shm；見 <see cref="SqliteSetup"/>），改以 immutable=1 直接讀（不上鎖、不找 WAL）。通常是服務已停止，但服務也可能
    /// 剛好在啟動或結束（保活連線還沒開或已關），在讀取途中開連線、寫入、關閉時做檢查點改到主檔，所以讀之前與讀之後比對主檔的
    /// SHA-256、並確認讀完仍然沒有 WAL（服務在讀取途中開了保活連線、把交易寫進 WAL 時主檔不變，只看雜湊會漏掉那些交易），
    /// 不一致就整個重來（服務若已開著連線，-wal、-shm 都在，唯讀連線就開得了），最多 <see cref="SnapshotAttempts"/> 次；
    /// 重來過的回傳 <c>immutable,attempts=N</c>。讀取途中主檔被改時 SQLite 可能讀到前後不一致的頁面而丟出例外
    /// （database disk image is malformed），所以 immutable 讀取失敗也先比對：主檔變了或出現了 WAL 就照樣重來，
    /// 主檔沒變、也沒有 WAL 才是資料庫真的有問題，把原本的例外往外丟（不重來）。
    /// WAL 裡還有資料時不用 immutable（會漏掉已提交的交易），照實回報失敗。</item>
    /// </list>
    /// </summary>
    public static string SnapshotReadOnly(string source, string target) => SnapshotReadOnly(source, target, immutableRead: null);

    /// <param name="immutableRead">
    /// 測試用：包住每一次 immutable 讀取（參數是第幾次與真正的讀取動作），可以在讀取前後模擬服務改主檔、寫 WAL，
    /// 或丟出 <see cref="SqliteException"/> 模擬讀取失敗；沒給就直接讀
    /// </param>
    internal static string SnapshotReadOnly(string source, string target, Action<int, Action>? immutableRead)
    {
        void ReadImmutable() => VacuumInto(source, immutable: true, target);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                VacuumInto(source, immutable: false, target);
                return "readonly";
            }
            catch (SqliteException) when (WalIsEmpty(source))
            {
                // 沒有 -wal 或 WAL 是空的：主檔就是全部已提交的資料，改用 immutable（見上）
            }
            var before = BackupService.Sha256File(source);
            SqliteException? readFailure = null;
            try
            {
                if (immutableRead is null)
                    ReadImmutable();
                else
                    immutableRead(attempt, ReadImmutable);
            }
            catch (SqliteException ex)
            {
                // 先不判斷：讀取途中主檔被改也會讓 SQLite 讀到前後不一致的頁面而失敗，要比對過主檔才知道是哪一種
                readFailure = ex;
            }
            if (WalIsEmpty(source) && BackupService.Sha256File(source) == before)
            {
                // 主檔沒變、也沒有 WAL：讀到的是一致的資料；讀取本身失敗就是資料庫真的有問題，照實回報（不重來）
                if (readFailure is not null)
                    ExceptionDispatchInfo.Throw(readFailure);
                return attempt == 1 ? "immutable" : "immutable,attempts=" + attempt.ToString(CultureInfo.InvariantCulture);
            }
            if (attempt >= SnapshotAttempts)
                throw new IOException($"唯讀快照讀取途中主檔一直被改（服務正在寫入）：試了 {SnapshotAttempts} 次都不一致", readFailure);
            Thread.Sleep(TimeSpan.FromMilliseconds(200 * attempt));
        }
    }

    private static bool WalIsEmpty(string source)
    {
        var wal = new FileInfo(source + "-wal");
        return !wal.Exists || wal.Length == 0;
    }

    private static void VacuumInto(string source, bool immutable, string target)
    {
        // immutable 要用 URI 檔名（file:///C:/…?immutable=1）；Microsoft.Data.Sqlite 看到 file: 開頭就以 SQLITE_OPEN_URI 開啟
        var dataSource = immutable ? new Uri(Path.GetFullPath(source)).AbsoluteUri + "?immutable=1" : source;
        BackupService.DeleteDatabaseFiles(target);
        using var connection = new SqliteConnection(SqliteSetup.ConnectionString(dataSource, readOnly: true, defaultTimeoutSeconds: 60));
        connection.Open();
        SqliteSetup.Exec(connection, "PRAGMA busy_timeout=5000");
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "VACUUM INTO $target";
        cmd.Parameters.AddWithValue("$target", target);
        cmd.ExecuteNonQuery();
    }

    /// <summary>config、db 讀得到（存在才檢查；只列舉、不讀內容以外的東西）</summary>
    private static PreflightCheck CheckReadable(HubPaths paths)
    {
        try
        {
            var seen = new List<string>();
            foreach (var dir in new[] { paths.ConfigDir, paths.DbDir })
            {
                if (!Directory.Exists(dir))
                    continue;
                using (var entries = Directory.EnumerateFileSystemEntries(dir).GetEnumerator())
                    entries.MoveNext();
                seen.Add(Path.GetFileName(dir));
            }
            return new PreflightCheck("data.readable", true, $"{paths.Root}（{(seen.Count == 0 ? "還沒有 config、db" : string.Join("、", seen))}）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PreflightCheck("data.readable", false, ex.Message);
        }
    }

    /// <summary>暫存資料夾（%TEMP% 底下自己的子資料夾）寫得進去</summary>
    private static PreflightCheck CheckWritable(string work)
    {
        try
        {
            Directory.CreateDirectory(work);
            var probe = Path.Combine(work, ".preflight-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return new PreflightCheck("temp.writable", true, work);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PreflightCheck("temp.writable", false, ex.Message);
        }
    }

    private static bool IsDomain(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !IPAddress.TryParse(value, out _) && Uri.CheckHostName(value) == UriHostNameType.Dns;

    private static PreflightCheck FromTry(string name, Func<string> work)
    {
        try
        {
            return new PreflightCheck(name, true, work());
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return new PreflightCheck(name, false, ex.Message);
        }
    }
}
