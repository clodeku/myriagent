using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.SqliteBench;

/// <summary>
/// SQLite 負載量測（移植研究原型；設計 11.4 的「SQLite（FULL）」列）：數台裝置、數百個對話、串流時每秒數十～上百筆更新。
/// 用法：MyriagentHub.SqliteBench &lt;資料夾&gt; [秒數=30] [--sync FULL|NORMAL|FULL,NORMAL] [--only B1,B2,B3,B4,B5,B6] [--b6-gb 1,4] [--b6-runs 3] [--b6-keep]
/// <list type="bullet">
/// <item>B1：每筆一個交易的吞吐（WAL+NORMAL、WAL+FULL、DELETE+FULL）。</item>
/// <item>B2／B3：5 個對話各 20 次／秒整筆取代＋事件 100 筆／秒＋2 個讀者；B2 每筆一個交易、B3 每 100 ms 合併一個交易。
/// MyriagentHub 用 WAL＋synchronous=FULL，所以預設跑 FULL（原型跑的是 NORMAL）；門檻：B3 寫入 p99 ≤10 ms。</item>
/// <item>B4：20 MB × 5 存 SQLite BLOB 與存成內容雜湊命名的檔案（WriteThrough）。</item>
/// <item>B5：每個操作開一條連線（MyriagentHub 的 EF 用法：開、套 PRAGMA、一個查詢或一筆寫入、關），比較連線池開／關、
/// 有沒有一條保活連線讓資料庫一直開著（沒有時最後一條連線關閉就做檢查點並刪掉 -wal、-shm）。</item>
/// <item>B6（WP4.2 步 15、答 7）：1 GB、4 GB（<c>--b6-gb</c>）的合成 sync.db（v3 結構、對話事件與物化表照回補的組成）的停機複製加兩次 SHA-256
/// （Windows PowerShell 5.1，同部署腳本）、每日備份（VACUUM INTO＋integrity_check＋SHA-256，同 BackupService）各跑 <c>--b6-runs</c> 次取中位數、
/// 換算每 GB 幾秒，並確認命令逾時會不會中斷單一長敘述。不在預設裡（要 <c>--only B6</c>）；要約 2.3 倍目標大小的暫存空間，
/// 不能在 MyriagentHub 的資料根裡跑。見 <see cref="SyncDbCopyBench"/>。</item>
/// </list>
/// 資料夾或它的上層是 MyriagentHub 的資料根（有 <c>status\service.lock</c> 或 <c>db\hub.db</c>）時，B1～B6 一律不跑、結束碼 2（不建任何資料夾或檔案）。
/// 亂數種子固定（歷史資料 42、讀者 100＋i、B4 的 BLOB 42＋i），產出指紋（歷史資料與 BLOB 的 SHA-256）每次都一樣，可用來確認重現。
/// 結果每項印一行 JSON（ASCII，非 ASCII 字元跳脫）。
/// </summary>
internal static class Program
{
    private const int Seed = 42;
    private const double WriteP99ThresholdMs = 10;

    /// <summary>與 src/MyriagentHub/Data/SqliteSetup.cs 的 PerConnectionPragmas 相同（B5、B6 每條連線都套）</summary>
    internal const string MyriagentHubPragmas = "PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA cache_size=-16384; PRAGMA mmap_size=0;";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>--only 收的名稱</summary>
    private static readonly string[] AllBenches = ["B1", "B2", "B3", "B4", "B5", "B6"];

    /// <summary>沒給 --only 時跑的（B6 要產生好幾 GB 的檔案、跑好幾分鐘，只在明確指定時跑）</summary>
    private static readonly string[] DefaultBenches = ["B1", "B2", "B3", "B4", "B5"];

    private const string Usage = "用法：MyriagentHub.SqliteBench <資料夾> [秒數=30] [--sync FULL|NORMAL|FULL,NORMAL] [--only B1,B2,B3,B4,B5,B6] [--b6-gb 1,4] [--b6-runs 3] [--b6-keep]";

    public static int Main(string[] args)
    {
        var positional = new List<string>();
        var syncModes = new[] { "FULL" };
        var only = DefaultBenches.ToHashSet();
        int[] b6Sizes = [1, 4];
        var b6Runs = 3;
        var b6Keep = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "-h" or "--help")
            {
                Console.WriteLine(Usage);
                return 0;
            }
            if (args[i] == "--b6-gb" && i + 1 < args.Length)
            {
                var parts = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var sizes = new List<int>();
                foreach (var part in parts)
                {
                    if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var gib) || gib is < 1 or > 64)
                    {
                        Console.Error.WriteLine("--b6-gb 是 1～64 的整數（GiB）的逗號清單，例如 --b6-gb 1,4");
                        return 2;
                    }
                    sizes.Add(gib);
                }
                if (sizes.Count == 0)
                {
                    Console.Error.WriteLine("--b6-gb 是 1～64 的整數（GiB）的逗號清單，例如 --b6-gb 1,4");
                    return 2;
                }
                b6Sizes = [.. sizes];
                continue;
            }
            if (args[i] == "--b6-runs" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out b6Runs) || b6Runs is < 1 or > 9)
                {
                    Console.Error.WriteLine("--b6-runs 是 1～9");
                    return 2;
                }
                continue;
            }
            if (args[i] == "--b6-keep")
            {
                b6Keep = true;
                continue;
            }
            if (args[i] == "--sync" && i + 1 < args.Length)
            {
                syncModes = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant()).ToArray();
                if (syncModes.Length == 0 || syncModes.Any(s => s is not ("FULL" or "NORMAL")))
                {
                    Console.Error.WriteLine("--sync 只能是 FULL、NORMAL 或 FULL,NORMAL");
                    return 2;
                }
                continue;
            }
            if (args[i] == "--only" && i + 1 < args.Length)
            {
                only = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant()).ToHashSet();
                if (only.Count == 0 || only.Any(b => !AllBenches.Contains(b)))
                {
                    Console.Error.WriteLine("--only 是 B1～B6 的逗號清單，例如 --only B3,B5");
                    return 2;
                }
                continue;
            }
            positional.Add(args[i]);
        }
        var dir = Path.GetFullPath(positional.Count > 0 ? positional[0] : "bench-data");
        var seconds = positional.Count > 1 ? int.Parse(positional[1], CultureInfo.InvariantCulture) : 30;
        // B1～B6 都不在 MyriagentHub 的資料根（或它底下）裡跑（WP4.9 合併前審查：營運 16.1 的 P2a 在 Hub 主機上跑 B3，資料夾打錯時不能寫進正式資料根）；
        // 建任何資料夾、寫任何檔之前檢查，B6 裡原本那一道留著當第二道
        if (SyncDbCopyBench.FindDataRoot(dir) is { } root)
        {
            Console.Error.WriteLine($"不能在 MyriagentHub 的資料根裡跑（{root} 有 status\\service.lock 或 db\\hub.db）：請給資料根以外的暫存資料夾");
            return 2;
        }
        Directory.CreateDirectory(dir);

        if (only.Contains("B1"))
            SingleRowTransactions(dir);
        foreach (var sync in syncModes)
        {
            foreach (var batched in new[] { false, true })
            {
                if (only.Contains(batched ? "B3" : "B2"))
                    Streaming(dir, seconds, sync, batched);
            }
        }
        if (only.Contains("B4"))
            LargeBlobs(dir);
        if (only.Contains("B5"))
            OpenPerOperation(dir);
        if (only.Contains("B6"))
        {
            var code = SyncDbCopyBench.Run(dir, b6Sizes, b6Runs, b6Keep);
            if (code != 0)
                return code;
        }
        Emit(new { bench = "env", sqliteVersion = SqliteVersion(), Environment.ProcessorCount, os = Environment.OSVersion.VersionString, dotnet = Environment.Version.ToString(), dir, seed = Seed });
        return 0;
    }

    // ── B1：每筆一個交易的最大吞吐 ──
    private static void SingleRowTransactions(string dir)
    {
        foreach (var (journal, sync) in new[] { ("WAL", "NORMAL"), ("WAL", "FULL"), ("DELETE", "FULL") })
        {
            var path = Path.Combine(dir, $"b1-{journal}-{sync}.db");
            DeleteDb(path);
            using var c = Open(path, journal, sync);
            Exec(c, "CREATE TABLE ev(seq INTEGER PRIMARY KEY AUTOINCREMENT, conv TEXT NOT NULL, payload TEXT NOT NULL, at INTEGER NOT NULL)");
            var payload = new string('x', 1024);
            const int n = 3000;
            var lat = new List<double>(n);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO ev(conv, payload, at) VALUES($c, $p, $t)";
            var pc = cmd.Parameters.Add("$c", SqliteType.Text);
            var pp = cmd.Parameters.Add("$p", SqliteType.Text);
            var pt = cmd.Parameters.Add("$t", SqliteType.Integer);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
            {
                var t = Stopwatch.GetTimestamp();
                pc.Value = "c" + (i % 300);
                pp.Value = payload;
                pt.Value = i;
                cmd.ExecuteNonQuery(); // 自動交易：每筆 commit 一次
                lat.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }
            Emit(new { bench = "B1-single-row-tx", journal, sync, rows = n, opsPerSec = Math.Round(n / sw.Elapsed.TotalSeconds), p50ms = P(lat, 50), p99ms = P(lat, 99), maxMs = Math.Round(lat.Max(), 2) });
        }
    }

    // ── B2／B3：串流負載（5 個對話各 20 次／秒整筆取代、事件記錄 100 筆／秒）＋ 2 個讀者 ──
    private static void Streaming(string dir, int seconds, string sync, bool batched)
    {
        var path = Path.Combine(dir, $"b2-{sync.ToLowerInvariant()}-{(batched ? "batched" : "per-row")}.db");
        DeleteDb(path);
        string historySha;
        using (var c = Open(path, "WAL", sync))
        {
            Exec(c, """
                CREATE TABLE items(conv TEXT NOT NULL, item_id TEXT NOT NULL, seq INTEGER NOT NULL, body TEXT NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(conv, item_id)) WITHOUT ROWID;
                CREATE INDEX ix_items_conv_seq ON items(conv, seq);
                CREATE TABLE events(seq INTEGER PRIMARY KEY AUTOINCREMENT, conv TEXT NOT NULL, kind TEXT NOT NULL, payload TEXT NOT NULL, at INTEGER NOT NULL);
                """);
            // 預先放 300 個對話 × 40 筆（模擬歷史）；內容由種子決定，SHA-256 當產出指紋
            var rnd0 = new Random(Seed);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var tx = c.BeginTransaction();
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO items VALUES($c,$i,$s,$b,0)";
            for (var conv = 0; conv < 300; conv++)
            {
                for (var i = 0; i < 40; i++)
                {
                    var body = RandomText(rnd0, 500 + rnd0.Next(3000));
                    hash.AppendData(Encoding.UTF8.GetBytes(body));
                    ins.Parameters.Clear();
                    ins.Parameters.AddWithValue("$c", "c" + conv);
                    ins.Parameters.AddWithValue("$i", "i" + i);
                    ins.Parameters.AddWithValue("$s", i);
                    ins.Parameters.AddWithValue("$b", body);
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
            historySha = Convert.ToHexStringLower(hash.GetHashAndReset());
        }

        var stop = false;
        var writeLat = new List<double>();
        var commitLat = new List<double>();
        var readLat = new List<double>();
        long maxWal = 0;
        var readerErrors = 0;
        var writes = 0;
        var readers = Enumerable.Range(0, 2).Select(r => Task.Run(() =>
        {
            var rnd = new Random(100 + r);
            using var rc = Open(path, "WAL", sync);
            using var q = rc.CreateCommand();
            q.CommandText = "SELECT item_id, body FROM items WHERE conv=$c ORDER BY seq DESC LIMIT 80";
            var p = q.Parameters.Add("$c", SqliteType.Text);
            while (!Volatile.Read(ref stop))
            {
                var t = Stopwatch.GetTimestamp();
                try
                {
                    p.Value = "c" + rnd.Next(300);
                    using var rd = q.ExecuteReader();
                    long bytes = 0;
                    while (rd.Read())
                        bytes += rd.GetString(1).Length;
                    lock (readLat)
                        readLat.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                }
                catch (Exception)
                {
                    // 讀者失敗（例如 SQLITE_BUSY）只計數，不中斷量測
                    Interlocked.Increment(ref readerErrors);
                }
                Thread.Sleep(50);
            }
        })).ToArray();

        using (var w = Open(path, "WAL", sync))
        {
            using var up = w.CreateCommand();
            up.CommandText = "INSERT INTO items VALUES($c,$i,$s,$b,$t) ON CONFLICT(conv,item_id) DO UPDATE SET body=excluded.body, updated_at=excluded.updated_at";
            using var ev = w.CreateCommand();
            ev.CommandText = "INSERT INTO events(conv, kind, payload, at) VALUES($c,'delta',$p,$t)";
            var rnd = new Random(Seed);
            var streamingConvs = Enumerable.Range(0, 5).Select(i => "c" + (i * 7)).ToArray();
            var itemLen = new int[5];
            var sw = Stopwatch.StartNew();
            var tick = 0;
            SqliteTransaction? batch = null;
            var lastBatch = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                // 每 10 ms 一個 tick：每個串流對話每 50 ms 取代一次（20 次／秒）→ 5 個對話合計 100 次／秒；事件 100 筆／秒
                var due = tick % 5;
                var t0 = Stopwatch.GetTimestamp();
                if (batched && batch is null)
                    batch = w.BeginTransaction();
                up.Transaction = batch;
                ev.Transaction = batch;
                var conv = streamingConvs[due];
                itemLen[due] = Math.Min(32_000, itemLen[due] + 150); // 回覆越寫越長，最長 32 KB
                up.Parameters.Clear();
                up.Parameters.AddWithValue("$c", conv);
                up.Parameters.AddWithValue("$i", "live-" + (tick / 2000));
                up.Parameters.AddWithValue("$s", 1000 + tick / 2000);
                up.Parameters.AddWithValue("$b", RandomText(rnd, itemLen[due]));
                up.Parameters.AddWithValue("$t", sw.ElapsedMilliseconds);
                up.ExecuteNonQuery();
                ev.Parameters.Clear();
                ev.Parameters.AddWithValue("$c", conv);
                ev.Parameters.AddWithValue("$p", RandomText(rnd, 120));
                ev.Parameters.AddWithValue("$t", sw.ElapsedMilliseconds);
                ev.ExecuteNonQuery();
                writes += 2;
                if (batched && lastBatch.ElapsedMilliseconds >= 100)
                {
                    var tc = Stopwatch.GetTimestamp();
                    batch!.Commit();
                    commitLat.Add(Stopwatch.GetElapsedTime(tc).TotalMilliseconds);
                    batch.Dispose();
                    batch = null;
                    lastBatch.Restart();
                }
                writeLat.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                if (tick % 100 == 0)
                {
                    var wal = path + "-wal";
                    if (File.Exists(wal))
                        maxWal = Math.Max(maxWal, new FileInfo(wal).Length);
                }
                tick++;
                var next = tick * 10.0;
                var wait = next - sw.Elapsed.TotalMilliseconds;
                if (wait > 0)
                    Thread.Sleep(TimeSpan.FromMilliseconds(wait));
            }
            batch?.Commit();
        }
        Volatile.Write(ref stop, true);
        Task.WaitAll(readers);
        var writeP99 = P(writeLat, 99);
        Emit(new
        {
            bench = batched ? "B3-stream-batched-100ms" : "B2-stream-per-row-tx",
            journal = "WAL",
            sync,
            seconds,
            writeStatementsPerSec = Math.Round(writes / (double)seconds),
            writeP50ms = P(writeLat, 50),
            writeP99ms = writeP99,
            writeMaxMs = Math.Round(writeLat.Max(), 2),
            commits = batched ? commitLat.Count : (int?)null,
            commitP50ms = batched ? P(commitLat, 50) : (double?)null,
            commitP99ms = batched ? P(commitLat, 99) : (double?)null,
            readQueries = readLat.Count,
            readP50ms = P(readLat, 50),
            readP99ms = P(readLat, 99),
            readerErrors,
            maxWalMB = Math.Round(maxWal / 1048576.0, 2),
            dbMB = Math.Round(new FileInfo(path).Length / 1048576.0, 2),
            historyRows = 300 * 40,
            historySha256 = historySha,
            // 設計 11.4 的門檻只看「100 ms 合併交易＋FULL」
            threshold = batched && sync == "FULL" ? new { writeP99ms = WriteP99ThresholdMs, pass = writeP99 <= WriteP99ThresholdMs } : null,
        });
    }

    // ── B4：大型紀錄檔／附件：20 MB × 5 存 SQLite BLOB vs 存檔案（內容雜湊命名）──
    private static void LargeBlobs(string dir)
    {
        var path = Path.Combine(dir, "b4-blob.db");
        DeleteDb(path);
        var blobs = Enumerable.Range(0, 5).Select(i =>
        {
            var b = new byte[20 * 1024 * 1024];
            new Random(Seed + i).NextBytes(b);
            return b;
        }).ToList();
        var shas = blobs.Select(b => Convert.ToHexStringLower(SHA256.HashData(b))).ToList();
        using var c = Open(path, "WAL", "NORMAL");
        Exec(c, "CREATE TABLE blobs(id INTEGER PRIMARY KEY, sha TEXT, data BLOB)");
        var sw = Stopwatch.StartNew();
        long maxWal = 0;
        for (var i = 0; i < blobs.Count; i++)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO blobs(sha, data) VALUES($s, $d)";
            cmd.Parameters.AddWithValue("$s", shas[i]);
            cmd.Parameters.AddWithValue("$d", blobs[i]);
            cmd.ExecuteNonQuery();
            maxWal = Math.Max(maxWal, new FileInfo(path + "-wal").Length);
        }
        var dbMs = sw.ElapsedMilliseconds;
        var walAfter = new FileInfo(path + "-wal").Length;

        var blobDir = Path.Combine(dir, "b4-files");
        if (Directory.Exists(blobDir))
            Directory.Delete(blobDir, true);
        sw.Restart();
        for (var i = 0; i < blobs.Count; i++)
        {
            var sha = shas[i];
            var sub = Path.Combine(blobDir, sha[..2]);
            Directory.CreateDirectory(sub);
            var tmp = Path.Combine(sub, sha + ".tmp");
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
                fs.Write(blobs[i]);
            File.Move(tmp, Path.Combine(sub, sha), overwrite: true);
        }
        var fileMs = sw.ElapsedMilliseconds;
        Emit(new
        {
            bench = "B4-20MBx5",
            sqliteBlobMs = dbMs,
            sqliteMaxWalMB = Math.Round(maxWal / 1048576.0, 1),
            sqliteWalAfterMB = Math.Round(walAfter / 1048576.0, 1),
            dbMB = Math.Round(new FileInfo(path).Length / 1048576.0, 1),
            filesWriteThroughMs = fileMs,
            blobSha256 = shas.Select(s => s[..16]),
        });
    }

    // ── B5：每個操作開一條連線（MyriagentHub 的 EF 用法）：連線池開／關 × 有沒有保活連線 ──
    private static void OpenPerOperation(string dir)
    {
        var path = Path.Combine(dir, "b5-open.db");
        DeleteDb(path);
        using (var c = Open(path, "WAL", "FULL"))
        {
            Exec(c, "CREATE TABLE kv(key TEXT PRIMARY KEY, value TEXT NOT NULL)");
            using var tx = c.BeginTransaction();
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO kv(key, value) VALUES($k, $v)";
            for (var i = 0; i < 100; i++)
            {
                ins.Parameters.Clear();
                ins.Parameters.AddWithValue("$k", "k" + i);
                ins.Parameters.AddWithValue("$v", new string('v', 200));
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
        foreach (var pooling in new[] { true, false })
        {
            foreach (var keepAlive in new[] { false, true })
            {
                // 每種組合從空的連線池開始（量測程式只有一個執行緒在開連線，清池不會碰到別人正在開的連線）
                SqliteConnection.ClearAllPools();
                using var keeper = keepAlive ? Open(path, "WAL", "FULL") : null;
                if (keeper is not null)
                    Exec(keeper, "SELECT COUNT(*) FROM kv"); // 讀過一次才持有主檔的共用鎖：之後別的連線關閉都不是「最後一條」
                foreach (var (op, n) in new[] { ("read", 2000), ("write", 300) })
                {
                    var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = pooling, DefaultTimeout = 30 }.ToString();
                    for (var i = 0; i < 50; i++)
                        OneOperation(cs, op, i); // 暖身
                    var lat = new List<double>(n);
                    var open = new List<double>(n);
                    var work = new List<double>(n);
                    var close = new List<double>(n);
                    var walGone = 0;
                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < n; i++)
                    {
                        var (openMs, opMs, closeMs) = OneOperation(cs, op, i);
                        lat.Add(openMs + opMs + closeMs);
                        open.Add(openMs);
                        work.Add(opMs);
                        close.Add(closeMs);
                        if (!File.Exists(path + "-wal"))
                            walGone++;
                    }
                    Emit(new
                    {
                        bench = "B5-open-per-op",
                        op,
                        pooling,
                        keepAlive,
                        ops = n,
                        opsPerSec = Math.Round(n / sw.Elapsed.TotalSeconds),
                        p50ms = P(lat, 50),
                        p99ms = P(lat, 99),
                        maxMs = Math.Round(lat.Max(), 2),
                        // 拆開的三段：開連線＋PRAGMA、讀或寫、關連線
                        openP50ms = P(open, 50),
                        opP50ms = P(work, 50),
                        closeP50ms = P(close, 50),
                        walDeletedAfterOps = walGone,
                    });
                }
            }
        }
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// 開一條連線、套 MyriagentHub 的 PRAGMA、讀一列或寫一列（自動交易＝synchronous=FULL 的一次提交）、關；
    /// 回傳三段的毫秒數（開＋PRAGMA、讀寫、關）
    /// </summary>
    private static (double OpenMs, double OpMs, double CloseMs) OneOperation(string connectionString, string op, int i)
    {
        var t0 = Stopwatch.GetTimestamp();
        var c = new SqliteConnection(connectionString);
        try
        {
            c.Open();
            Exec(c, MyriagentHubPragmas);
            var t1 = Stopwatch.GetTimestamp();
            using (var cmd = c.CreateCommand())
            {
                cmd.Parameters.AddWithValue("$k", "k" + (i % 100));
                if (op == "read")
                {
                    cmd.CommandText = "SELECT value FROM kv WHERE key = $k";
                    cmd.ExecuteScalar();
                }
                else
                {
                    cmd.CommandText = "INSERT INTO kv(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
                    cmd.Parameters.AddWithValue("$v", "w" + i.ToString(CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }
            }
            var t2 = Stopwatch.GetTimestamp();
            c.Dispose();
            var t3 = Stopwatch.GetTimestamp();
            return (Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds, Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds, Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds);
        }
        finally
        {
            c.Dispose();
        }
    }

    internal static void Emit(object o) => Console.WriteLine(JsonSerializer.Serialize(o, Json));

    private static SqliteConnection Open(string path, string journal, string sync)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        Exec(c, $"PRAGMA journal_mode={journal}; PRAGMA synchronous={sync}; PRAGMA busy_timeout=5000;");
        return c;
    }

    internal static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static string SqliteVersion()
    {
        using var c = new SqliteConnection("Data Source=:memory:");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "select sqlite_version()";
        return (string)cmd.ExecuteScalar()!;
    }

    private static void DeleteDb(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            if (File.Exists(f))
                File.Delete(f);
        }
    }

    private static double P(List<double> v, int pct)
    {
        if (v.Count == 0)
            return 0;
        var s = v.OrderBy(x => x).ToList();
        return Math.Round(s[Math.Min(s.Count - 1, (int)Math.Ceiling(pct / 100.0 * s.Count) - 1)], 2);
    }

    private static string RandomText(Random r, int len)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz 中文測試的回覆內容，代理正在執行指令。0123456789";
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++)
            sb.Append(chars[r.Next(chars.Length)]);
        return sb.ToString();
    }
}
