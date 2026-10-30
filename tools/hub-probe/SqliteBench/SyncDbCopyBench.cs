using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using MyriagentHub.Data;
using MyriagentHub.Ops;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.SqliteBench;

/// <summary>
/// B6（WP4.2 步 15、答 7、D41）：sync.db 長到幾 GB 時，部署的停機複製與每日備份要多久。
/// <list type="bullet">
/// <item>在 <c>&lt;資料夾&gt;\b6\</c> 產生指定大小（GiB）的合成 sync.db：伺服器真正的 v3 結構（連結 <c>SyncSchema.cs</c>），
/// 內容照 WP4.2 步 14 情境三（4 台回補）量到的組成：每個對話一筆 <c>conv.created</c>、100～199 則項目（45% 文字 300～3,000 字、
/// 35% 工具輸出最多 12 KB、15% 使用者訊息、5% 通知）、最後一筆佇列與一輪結束；每則項目一筆事件加一列 <c>items</c>（內容相同），
/// payload 照 <c>PayloadRules.Encode</c>（原文 ≥8 KB 用 gzip＋base64）。4 台裝置輪流、每 500 筆事件一個交易。</item>
/// <item>停機複製加兩次 SHA-256：以 Windows PowerShell 5.1 執行和 <c>tools/deploy/Install-MyriagentHubRelease.ps1</c> 停機複製那一段相同的
/// <c>Copy-Item</c>＋兩次 <c>Get-FileHash</c>（部署 job 就是用它；5.1 的 SHA-256 比 .NET 10 慢很多，不能用 .NET 代量）。</item>
/// <item>每日備份：照 <c>BackupService.SnapshotDatabase</c>，在背景模式執行緒（低 I/O 優先權）上 <c>VACUUM INTO</c> 暫存檔 →
/// <c>integrity_check</c> 與各表筆數 → 暫存檔的 SHA-256；連線的命令逾時照 <c>BackupTimeoutRules</c>。</item>
/// <item>命令逾時是否限制單一長敘述：以 1 秒的命令逾時各跑一次 <c>VACUUM INTO</c> 與 <c>integrity_check</c>（只在第一個大小做）。</item>
/// </list>
/// 每件各跑 <c>runs</c> 次取中位數，再除以檔案的 GiB 數。亂數種子固定（<see cref="Seed"/>），時間戳記與 instance_id 固定，
/// 同一個 .NET 與 SQLite 版本下合成檔逐位元組相同（<c>fileSha256</c>、<c>contentSha256</c>）。量完刪掉 <c>b6\</c>（<c>--b6-keep</c> 保留）。
/// 結果含作業系統檔案快取的效果（剛寫完的檔案多半還在快取裡），和服務剛停止時複製的情況接近。
/// </summary>
internal static class SyncDbCopyBench
{
    public const int Seed = 4206;

    private const long GiB = 1L << 30;
    private const int DeviceCount = 4;
    private const int EventsPerTransaction = 500;
    private const long BaseTime = 1_790_000_000_000;
    private const string FixedInstanceId = "b6b6b6b6b6b6b6b6b6b6b6b6b6b6b6b6";

    private static readonly JsonWriterOptions PayloadJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly string[] Words =
        ["檔案", "函式", "測試", "伺服器", "設定", "錯誤", "已完成", "修改", "the", "result", "build", "error", "value", "return", "async", "public", "目錄", "資料庫"];

    public static int Run(string dir, IReadOnlyList<int> sizesGiB, int runs, bool keep)
    {
        if (FindDataRoot(dir) is { } root)
        {
            Console.Error.WriteLine($"B6 不能在 MyriagentHub 的資料根裡跑（{root} 有 status\\service.lock 或 db\\hub.db）：請給資料根以外的暫存資料夾");
            return 2;
        }
        var work = Path.Combine(dir, "b6");
        Directory.CreateDirectory(work);
        try
        {
            var probed = false;
            foreach (var gib in sizesGiB)
            {
                var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(work))!).AvailableFreeSpace;
                var need = (long)(gib * GiB * 2.3) + GiB;
                if (free < need)
                {
                    Program.Emit(new { bench = "B6-syncdb-copy-backup", targetGiB = gib, skipped = true, reason = "disk", freeGiB = Math.Round(free / (double)GiB, 1), needGiB = Math.Round(need / (double)GiB, 1) });
                    continue;
                }
                var source = Path.Combine(work, $"sync-{gib}g.db");
                var generated = Generate(source, gib);
                var copy = MeasureStopCopy(source, work, runs);
                var backup = MeasureBackup(source, work, runs);
                var fileGiB = generated.FileBytes / (double)GiB;
                Program.Emit(new
                {
                    bench = "B6-syncdb-copy-backup",
                    targetGiB = gib,
                    fileBytes = generated.FileBytes,
                    fileGiB = Math.Round(fileGiB, 3),
                    rows = new { events = generated.Events, items = generated.Items, conversations = generated.Conversations },
                    composition = new
                    {
                        eventsPayloadBytes = generated.EventsPayloadBytes,
                        itemsBodyBytes = generated.ItemsBodyBytes,
                        eventsPayloadShare = Math.Round(generated.EventsPayloadBytes / (double)generated.FileBytes, 3),
                        itemsBodyShare = Math.Round(generated.ItemsBodyBytes / (double)generated.FileBytes, 3),
                        decodedHistoryBytes = generated.DecodedBytes,
                        fileBytesPerDecodedByte = Math.Round(generated.FileBytes / (double)generated.DecodedBytes, 3),
                    },
                    seed = Seed,
                    contentSha256 = generated.ContentSha256,
                    fileSha256 = copy.SourceSha256,
                    generateSeconds = Math.Round(generated.Seconds, 1),
                    runs,
                    stopCopy = new
                    {
                        method = copy.Method,
                        copySeconds = copy.Copy.Select(Round).ToArray(),
                        hashSourceSeconds = copy.HashSource.Select(Round).ToArray(),
                        hashCopySeconds = copy.HashCopy.Select(Round).ToArray(),
                        totalSeconds = copy.Total.Select(Round).ToArray(),
                        medianTotalSeconds = Round(Median(copy.Total)),
                        secondsPerGiB = Round(Median(copy.Total) / fileGiB),
                        copySecondsPerGiB = Round(Median(copy.Copy) / fileGiB),
                        hashSecondsPerGiB = Round((Median(copy.HashSource) + Median(copy.HashCopy)) / fileGiB),
                    },
                    backup = new
                    {
                        backgroundMode = backup.BackgroundMode,
                        commandTimeoutSeconds = backup.CommandTimeoutSeconds,
                        integrity = backup.Integrity,
                        vacuumSeconds = backup.Vacuum.Select(Round).ToArray(),
                        integritySeconds = backup.IntegrityCheck.Select(Round).ToArray(),
                        sha256Seconds = backup.Sha256.Select(Round).ToArray(),
                        totalSeconds = backup.Total.Select(Round).ToArray(),
                        medianVacuumSeconds = Round(Median(backup.Vacuum)),
                        medianTotalSeconds = Round(Median(backup.Total)),
                        vacuumSecondsPerGiB = Round(Median(backup.Vacuum) / fileGiB),
                        integritySecondsPerGiB = Round(Median(backup.IntegrityCheck) / fileGiB),
                        backupSecondsPerGiB = Round(Median(backup.Total) / fileGiB),
                        snapshotBytes = backup.SnapshotBytes,
                    },
                });
                if (!probed)
                {
                    ProbeCommandTimeout(source, work);
                    probed = true;
                }
                if (!keep)
                    DeleteDb(source);
            }
        }
        finally
        {
            if (!keep)
            {
                try
                {
                    Directory.Delete(work, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"刪不掉 {work}：{ex.Message}（請手動刪除）");
                }
            }
        }
        return 0;
    }

    // ── 合成資料 ──

    private sealed record Generated(long FileBytes, long Events, long Items, long Conversations, long EventsPayloadBytes, long ItemsBodyBytes,
        long DecodedBytes, string ContentSha256, double Seconds);

    private static Generated Generate(string path, int gib)
    {
        DeleteDb(path);
        var started = Stopwatch.StartNew();
        var target = gib * GiB;
        var rng = new Random(Seed);
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long events = 0, items = 0, conversations = 0, eventsPayload = 0, itemsBody = 0, decoded = 0;
        var nextDeviceSeq = new long[DeviceCount];
        var conversationsPerDevice = new int[DeviceCount];
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            c.Open();
            // 產生資料時不寫日誌、不 fsync（只影響產生速度，不影響檔案的頁面配置）；做完才改成服務用的 WAL
            Program.Exec(c, "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA cache_size=-262144;");
            SyncSchema.Migrate(c);
            using (var tx = c.BeginTransaction())
            {
                SyncSchema.SetMeta(c, tx, SyncSchema.InstanceIdKey, FixedInstanceId);
                tx.Commit();
            }

            var writer = new Writer(c);
            var inTransaction = 0;
            writer.Begin();
            var done = false;
            while (!done)
            {
                for (var d = 0; d < DeviceCount && !done; d++)
                {
                    var device = DeviceId(d);
                    var n = conversationsPerDevice[d]++;
                    var id = device[^8..] + n.ToString("x24", CultureInfo.InvariantCulture);
                    var time = BaseTime + conversations * 60_000;
                    conversations++;
                    // conv.created：建立對話列
                    var meta = Meta("回補 " + n.ToString(CultureInfo.InvariantCulture), time);
                    var created = writer.Event(id, "meta", device, ++nextDeviceSeq[d], "conv.created", meta, time);
                    Account(created);
                    writer.Conversation(id, device, meta, time, created.Seq);
                    var count = 100 + rng.Next(100);
                    for (var ord = 1; ord <= count; ord++)
                    {
                        var (kind, json) = Item(rng, ord, time + ord * 1000);
                        var e = writer.Event(id, "item", device, ++nextDeviceSeq[d], "conv.item", json, time + ord * 1000);
                        Account(e);
                        writer.Item(id, "i" + ord.ToString(CultureInfo.InvariantCulture), ord, e.Seq, kind, device, e.Payload, e.Enc);
                        itemsBody += Encoding.UTF8.GetByteCount(e.Payload);
                        items++;
                    }
                    var queue = Queue("下一步");
                    var q = writer.Event(id, "item", device, ++nextDeviceSeq[d], "conv.queue", queue, time + (count + 1) * 1000);
                    Account(q);
                    writer.Runtime(id, queue);
                    var turn = "{\"phase\":\"completed\",\"outcome\":\"completed\",\"usage\":{\"inputTokens\":12345,\"outputTokens\":2100,\"cachedInputTokens\":0}}";
                    var t = writer.Event(id, "meta", device, ++nextDeviceSeq[d], "conv.turn", Encoding.UTF8.GetBytes(turn), time + (count + 2) * 1000);
                    Account(t);
                    writer.FinishConversation(id, count, t.Seq, time + (count + 2) * 1000, turn);
                    inTransaction += count + 3;
                    if (inTransaction >= EventsPerTransaction)
                    {
                        writer.Commit();
                        inTransaction = 0;
                        if (Size(c) >= target)
                            done = true;
                        else
                            writer.Begin();
                    }
                }
            }
            if (inTransaction > 0)
                writer.Commit();
            using (var tx = c.BeginTransaction())
            {
                for (var d = 0; d < DeviceCount; d++)
                {
                    using var cmd = c.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO device_streams(device_id, next_device_seq, name, app_version, last_hello_at) VALUES($d, $n, $name, '0.3.0', $t)";
                    cmd.Parameters.AddWithValue("$d", DeviceId(d));
                    cmd.Parameters.AddWithValue("$n", nextDeviceSeq[d] + 1);
                    cmd.Parameters.AddWithValue("$name", "B6-PC-" + d.ToString(CultureInfo.InvariantCulture));
                    cmd.Parameters.AddWithValue("$t", BaseTime);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            writer.Dispose();
            var maxSeq = Convert.ToInt64(Program.Scalar(c, "SELECT MAX(seq) FROM events"), CultureInfo.InvariantCulture);
            if (maxSeq != events)
                throw new InvalidOperationException($"events 的 seq 不連續：最大 {maxSeq}、寫了 {events} 筆");
            // 服務的 sync.db 是 WAL（寫在檔頭）；最後一條連線關閉時做檢查點並刪掉 -wal、-shm，和服務正常停止之後一樣只剩主檔
            Program.Exec(c, "PRAGMA journal_mode=WAL;");
        }
        return new Generated(new FileInfo(path).Length, events, items, conversations, eventsPayload, itemsBody, decoded,
            Convert.ToHexStringLower(content.GetHashAndReset()), started.Elapsed.TotalSeconds);

        void Account(Writer.Written e)
        {
            events++;
            eventsPayload += Encoding.UTF8.GetByteCount(e.Payload);
            decoded += e.DecodedBytes;
            content.AppendData(Encoding.UTF8.GetBytes(e.Payload));
        }
    }

    private static long Size(SqliteConnection c) =>
        Convert.ToInt64(Program.Scalar(c, "PRAGMA page_count"), CultureInfo.InvariantCulture) *
        Convert.ToInt64(Program.Scalar(c, "PRAGMA page_size"), CultureInfo.InvariantCulture);

    private static string DeviceId(int d) => "d_b6device" + d.ToString(CultureInfo.InvariantCulture).PadLeft(16, '0');

    private static byte[] Meta(string title, long time) => Json(w =>
    {
        w.WriteString("provider", "Claude");
        w.WriteString("localAccountId", "acc-1");
        w.WriteString("title", title);
        w.WriteString("model", "opus");
        w.WriteBoolean("customFolder", false);
        w.WriteString("syncPolicy", "full");
        w.WriteNumber("createdAt", time);
        w.WriteNumber("updatedAt", time);
        w.WriteBoolean("running", false);
        w.WriteBoolean("waiting", false);
    });

    private static byte[] Queue(string text) => Json(w =>
    {
        w.WriteStartArray("entries");
        w.WriteStartObject();
        w.WriteString("id", "q0");
        w.WriteString("displayText", text);
        w.WriteEndObject();
        w.WriteEndArray();
        w.WriteNumber("more", 0);
    });

    /// <summary>一則項目（組成同 MyriagentHub.Tests 的 ConversationWriteBench.Backfill）</summary>
    private static (string Kind, byte[] Json) Item(Random rng, int ord, long time)
    {
        var roll = rng.Next(100);
        var kind = roll < 45 ? "Assistant" : roll < 80 ? "Tool" : roll < 95 ? "User" : "Notice";
        string? text = null, detail = null, output = null;
        switch (kind)
        {
            case "Assistant":
                text = MixedText(rng, 300 + rng.Next(2700));
                break;
            case "Tool":
                detail = MixedText(rng, 100 + rng.Next(400));
                output = MixedText(rng, 500 + rng.Next(11500));
                break;
            case "User":
                text = MixedText(rng, 50 + rng.Next(450));
                break;
            default:
                text = "通知 " + ord.ToString(CultureInfo.InvariantCulture);
                break;
        }
        return (kind, Json(w =>
        {
            w.WriteString("id", "i" + ord.ToString(CultureInfo.InvariantCulture));
            w.WriteNumber("ord", ord);
            w.WriteNumber("ver", 1);
            w.WriteString("kind", kind);
            w.WriteString("status", "Done");
            w.WriteNumber("createdAt", time);
            if (text is not null)
                w.WriteString("text", text);
            if (kind == "Tool")
            {
                w.WriteString("title", "Bash");
                w.WriteString("detail", detail);
                w.WriteString("output", output);
            }
            w.WriteBoolean("canAllowForSession", true);
            w.WriteBoolean("answerAsMessage", false);
        }));
    }

    private static string MixedText(Random rng, int length)
    {
        var text = new StringBuilder(length + 16);
        while (text.Length < length)
        {
            text.Append(Words[rng.Next(Words.Length)]);
            text.Append(rng.Next(12) == 0 ? '\n' : ' ');
            if (rng.Next(20) == 0)
                text.Append(rng.Next(100000).ToString(CultureInfo.InvariantCulture)).Append(' ');
        }
        return text.ToString(0, length);
    }

    private static byte[] Json(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer, PayloadJson))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>照 PayloadRules.Encode：原文 UTF-8 未滿 8 KB 照原樣，否則 gzip（Optimal）＋標準 base64</summary>
    private static (string Enc, string Payload) Encode(byte[] raw)
    {
        if (raw.Length < 8 * 1024)
            return ("json", Encoding.UTF8.GetString(raw));
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(raw);
        return ("gzip", Convert.ToBase64String(buffer.ToArray()));
    }

    /// <summary>四張表的插入（欄位照 SyncSchema 的 v1、v2；物化的值照 ConversationMaterializer 寫的形狀）</summary>
    private sealed class Writer(SqliteConnection c) : IDisposable
    {
        public sealed record Written(long Seq, string Payload, string Enc, long DecodedBytes);

        private readonly SqliteConnection connection = c;
        private SqliteTransaction? tx;
        private long seq;
        private readonly SqliteCommand ev = Prepare(c,
            "INSERT INTO events(stream, cls, conversation_id, author, device_seq, payload_sha256, epoch, kind, payload, enc, created_at) " +
            "VALUES($stream, $cls, $c, $author, $ds, $sha, 1, $kind, $p, $enc, $t)");
        private readonly SqliteCommand item = Prepare(c,
            "INSERT INTO items(conversation_id, item_id, ord, last_seq, epoch, ver, kind, status, final, origin_device_id, body, enc, removed) " +
            "VALUES($c, $i, $ord, $seq, 1, 1, $kind, 'Done', 1, $author, $b, $enc, 0)");
        private readonly SqliteCommand conv = Prepare(c,
            "INSERT INTO conversations(conversation_id, provider, logical_account_id, origin_device_id, owner_device_id, epoch, meta_json, title, created_at, updated_at, last_seq, sync_policy) " +
            "VALUES($c, 'Claude', NULL, $author, $author, 1, $meta, $title, $t, $t, $seq, 'full')");
        private readonly SqliteCommand runtime = Prepare(c,
            "INSERT INTO conversation_runtime(conversation_id, queue_json, pending_attachments_json, version) VALUES($c, $q, '[]', 1)");
        private readonly SqliteCommand finish = Prepare(c,
            "UPDATE conversations SET item_count=$n, max_ord=$n, last_seq=$seq, updated_at=$t, last_turn=$turn WHERE conversation_id=$c");

        public void Begin()
        {
            tx = connection.BeginTransaction();
            foreach (var cmd in new[] { ev, item, conv, runtime, finish })
                cmd.Transaction = tx;
        }

        public void Commit()
        {
            tx!.Commit();
            tx.Dispose();
            tx = null;
        }

        public Written Event(string conversationId, string cls, string author, long deviceSeq, string kind, byte[] raw, long time)
        {
            var (enc, payload) = Encode(raw);
            Set(ev, "$stream", "c:" + conversationId);
            Set(ev, "$cls", cls);
            Set(ev, "$c", conversationId);
            Set(ev, "$author", author);
            Set(ev, "$ds", deviceSeq);
            Set(ev, "$sha", SHA256.HashData(raw));
            Set(ev, "$kind", kind);
            Set(ev, "$p", payload);
            Set(ev, "$enc", enc);
            Set(ev, "$t", time);
            ev.ExecuteNonQuery();
            return new Written(++seq, payload, enc, raw.Length);
        }

        public void Conversation(string conversationId, string author, byte[] meta, long time, long lastSeq)
        {
            Set(conv, "$c", conversationId);
            Set(conv, "$author", author);
            Set(conv, "$meta", Encoding.UTF8.GetString(meta));
            Set(conv, "$title", "回補");
            Set(conv, "$t", time);
            Set(conv, "$seq", lastSeq);
            conv.ExecuteNonQuery();
        }

        public void Item(string conversationId, string itemId, long ord, long lastSeq, string kind, string author, string body, string enc)
        {
            Set(item, "$c", conversationId);
            Set(item, "$i", itemId);
            Set(item, "$ord", ord);
            Set(item, "$seq", lastSeq);
            Set(item, "$kind", kind);
            Set(item, "$author", author);
            Set(item, "$b", body);
            Set(item, "$enc", enc);
            item.ExecuteNonQuery();
        }

        public void Runtime(string conversationId, byte[] queue)
        {
            Set(runtime, "$c", conversationId);
            Set(runtime, "$q", Encoding.UTF8.GetString(queue));
            runtime.ExecuteNonQuery();
        }

        public void FinishConversation(string conversationId, int count, long lastSeq, long time, string turn)
        {
            Set(finish, "$c", conversationId);
            Set(finish, "$n", count);
            Set(finish, "$seq", lastSeq);
            Set(finish, "$t", time);
            Set(finish, "$turn", turn);
            finish.ExecuteNonQuery();
        }

        public void Dispose()
        {
            tx?.Dispose();
            foreach (var cmd in new[] { ev, item, conv, runtime, finish })
                cmd.Dispose();
        }

        private static SqliteCommand Prepare(SqliteConnection c, string sql)
        {
            var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return cmd;
        }

        private static void Set(SqliteCommand cmd, string name, object value)
        {
            if (cmd.Parameters.Contains(name))
                cmd.Parameters[name].Value = value;
            else
                cmd.Parameters.AddWithValue(name, value);
        }
    }

    // ── 停機複製加兩次 SHA-256（Windows PowerShell 5.1，同部署腳本）──

    private sealed record StopCopy(string Method, List<double> Copy, List<double> HashSource, List<double> HashCopy, List<double> Total, string SourceSha256);

    /// <summary>
    /// 和 Install-MyriagentHubRelease.ps1 停機複製的一段相同：Copy-Item -Force、來源與複本各一次 Get-FileHash SHA256、比對。
    /// 路徑以環境變數傳（不經命令列的引號）；子程序拿掉 PSModulePath（從 PowerShell 7 啟動時 5.1 會找不到 Get-FileHash）
    /// </summary>
    private const string CopyScript = """
        $ErrorActionPreference = 'Stop'
        $src = $env:MYRIAGENT_B6_SRC
        $dest = $env:MYRIAGENT_B6_DEST
        $inv = [Globalization.CultureInfo]::InvariantCulture
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Copy-Item -LiteralPath $src -Destination $dest -Force
        $t1 = $sw.Elapsed.TotalSeconds
        $hs = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash.ToLowerInvariant()
        $t2 = $sw.Elapsed.TotalSeconds
        $hd = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
        $t3 = $sw.Elapsed.TotalSeconds
        if ($hs -ne $hd) { throw 'copy verification failed' }
        'B6|' + $t1.ToString($inv) + '|' + ($t2 - $t1).ToString($inv) + '|' + ($t3 - $t2).ToString($inv) + '|' + $hs + '|' + $PSVersionTable.PSVersion.ToString()
        """;

    private static StopCopy MeasureStopCopy(string source, string work, int runs)
    {
        var copy = new List<double>();
        var hashSource = new List<double>();
        var hashCopy = new List<double>();
        var total = new List<double>();
        var sha = string.Empty;
        var method = string.Empty;
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        for (var i = 0; i < runs; i++)
        {
            var dest = Path.Combine(work, $"predeploy-{i}.db");
            DeleteDb(dest);
            if (File.Exists(powershell))
            {
                var psi = new ProcessStartInfo(powershell)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                             Convert.ToBase64String(Encoding.Unicode.GetBytes(CopyScript)) })
                    psi.ArgumentList.Add(a);
                psi.Environment.Remove("PSModulePath");
                psi.Environment["MYRIAGENT_B6_SRC"] = source;
                psi.Environment["MYRIAGENT_B6_DEST"] = dest;
                using var p = Process.Start(psi)!;
                var stderr = p.StandardError.ReadToEndAsync();
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                var line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("B6|", StringComparison.Ordinal));
                if (p.ExitCode != 0 || line is null)
                    throw new InvalidOperationException($"Windows PowerShell 的停機複製失敗（結束碼 {p.ExitCode}）：{stdout} {stderr.Result}");
                var parts = line.Split('|');
                var c = double.Parse(parts[1], CultureInfo.InvariantCulture);
                var h1 = double.Parse(parts[2], CultureInfo.InvariantCulture);
                var h2 = double.Parse(parts[3], CultureInfo.InvariantCulture);
                copy.Add(c);
                hashSource.Add(h1);
                hashCopy.Add(h2);
                total.Add(c + h1 + h2);
                sha = parts[4];
                method = "powershell-" + parts[5];
            }
            else
            {
                // 沒有 Windows PowerShell（不是 Windows）：以 .NET 代量，數字會比部署腳本快（method 註明）
                var sw = Stopwatch.StartNew();
                File.Copy(source, dest, overwrite: true);
                var c = sw.Elapsed.TotalSeconds;
                var hs = Sha256File(source);
                var h1 = sw.Elapsed.TotalSeconds - c;
                var hd = Sha256File(dest);
                var h2 = sw.Elapsed.TotalSeconds - c - h1;
                if (hs != hd)
                    throw new InvalidOperationException("複本的 SHA-256 不符");
                copy.Add(c);
                hashSource.Add(h1);
                hashCopy.Add(h2);
                total.Add(c + h1 + h2);
                sha = hs;
                method = "dotnet";
            }
            DeleteDb(dest);
        }
        return new StopCopy(method, copy, hashSource, hashCopy, total, sha);
    }

    // ── 每日備份（同 BackupService.SnapshotDatabase）──

    private sealed record Backup(bool BackgroundMode, int CommandTimeoutSeconds, string Integrity, List<double> Vacuum, List<double> IntegrityCheck,
        List<double> Sha256, List<double> Total, long SnapshotBytes);

    private static Backup MeasureBackup(string source, string work, int runs)
    {
        var vacuum = new List<double>();
        var integrity = new List<double>();
        var sha = new List<double>();
        var total = new List<double>();
        var result = string.Empty;
        long snapshotBytes = 0;
        var background = false;
        var timeout = BackupTimeoutRules.CommandTimeoutSeconds(DatabaseBytes(source));
        for (var i = 0; i < runs; i++)
        {
            var temp = Path.Combine(work, $"backup-{i}.db.tmp");
            DeleteDb(temp);
            var run = OnBackgroundThread(() =>
            {
                var sw = Stopwatch.StartNew();
                using (var c = OpenLikeServer(source, timeout))
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "VACUUM INTO $target";
                    cmd.Parameters.AddWithValue("$target", temp);
                    cmd.ExecuteNonQuery();
                }
                var vacuumed = sw.Elapsed.TotalSeconds;
                var outcome = Inspect(temp, BackupTimeoutRules.CommandTimeoutSeconds(DatabaseBytes(temp)));
                var inspected = sw.Elapsed.TotalSeconds;
                Sha256File(temp);
                var hashed = sw.Elapsed.TotalSeconds;
                return (vacuumed, inspected - vacuumed, hashed - inspected, outcome);
            });
            background = run.Background;
            vacuum.Add(run.Vacuum);
            integrity.Add(run.Integrity);
            sha.Add(run.Sha256);
            total.Add(run.Vacuum + run.Integrity + run.Sha256);
            result = run.Text;
            snapshotBytes = new FileInfo(temp).Length;
            DeleteDb(temp);
        }
        return new Backup(background, timeout, result, vacuum, integrity, sha, total, snapshotBytes);
    }

    /// <summary>
    /// 命令逾時（Microsoft.Data.Sqlite 的 CommandTimeout）是否會中斷單一個很久的敘述：以 1 秒的逾時各跑一次 VACUUM INTO 與 integrity_check。
    /// 跑超過 1 秒還完成＝逾時只限制忙碌時的重試（SQLITE_BUSY），不限制敘述本身
    /// </summary>
    private static void ProbeCommandTimeout(string source, string work)
    {
        var temp = Path.Combine(work, "timeout-probe.db.tmp");
        DeleteDb(temp);
        var (vacuumSeconds, vacuumError) = Timed(() =>
        {
            using var c = OpenLikeServer(source, 1);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "VACUUM INTO $target";
            cmd.Parameters.AddWithValue("$target", temp);
            cmd.ExecuteNonQuery();
        });
        var (integritySeconds, integrityError) = Timed(() =>
        {
            using var c = OpenLikeServer(source, 1);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            cmd.ExecuteScalar();
        });
        DeleteDb(temp);
        Program.Emit(new
        {
            bench = "B6-command-timeout",
            commandTimeoutSeconds = 1,
            vacuumSeconds = Round(vacuumSeconds),
            vacuumError,
            integritySeconds = Round(integritySeconds),
            integrityError,
            conclusive = vacuumSeconds > 1.5 && integritySeconds > 1.5,
            limitsLongStatement = vacuumError is not null || integrityError is not null,
        });

        static (double Seconds, string? Error) Timed(Action work)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                work();
                return (sw.Elapsed.TotalSeconds, null);
            }
            catch (SqliteException ex)
            {
                return (sw.Elapsed.TotalSeconds, $"SqliteException {ex.SqliteErrorCode}: {ex.Message}");
            }
        }
    }

    /// <summary>同 SqliteSetup.Open（不用連線池、套服務的 PRAGMA）；命令逾時照呼叫端</summary>
    private static SqliteConnection OpenLikeServer(string path, int timeoutSeconds)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = timeoutSeconds }.ToString());
        c.Open();
        Program.Exec(c, Program.MyriagentHubPragmas);
        return c;
    }

    /// <summary>同 BackupService.Inspect：integrity_check 與各表筆數</summary>
    private static string Inspect(string path, int timeoutSeconds)
    {
        using var c = OpenLikeServer(path, timeoutSeconds);
        var integrity = Program.Scalar(c, "PRAGMA integrity_check")?.ToString() ?? "(none)";
        var names = new List<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                names.Add(reader.GetString(0));
        }
        foreach (var name in names)
            Program.Scalar(c, "SELECT COUNT(*) FROM \"" + name.Replace("\"", "\"\"") + "\"");
        return integrity;
    }

    /// <summary>同 BackupService 的背景模式執行緒（THREAD_MODE_BACKGROUND_BEGIN：低 I/O 與記憶體優先權）；回傳是否真的進了背景模式</summary>
    private static (bool Background, double Vacuum, double Integrity, double Sha256, string Text) OnBackgroundThread(Func<(double, double, double, string)> work)
    {
        (double, double, double, string) result = default;
        Exception? error = null;
        var background = false;
        var thread = new Thread(() =>
        {
            background = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                if (background)
                    SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
            }
        })
        {
            IsBackground = true,
            Name = "B6 備份",
        };
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new InvalidOperationException("備份量測失敗", error);
        return (background, result.Item1, result.Item2, result.Item3, result.Item4 ?? string.Empty);
    }

    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    // ── 雜項 ──

    /// <summary>資料夾或它的上層是 MyriagentHub 的資料根（有 status\service.lock 或 db\hub.db）時回傳那個資料根（Program.Main 在跑任何量測之前也查）</summary>
    internal static string? FindDataRoot(string dir)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(dir)); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "status", "service.lock")) || File.Exists(Path.Combine(d.FullName, "db", "hub.db")))
                return d.FullName;
        }
        return null;
    }

    private static string Sha256File(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static long DatabaseBytes(string path) =>
        (File.Exists(path) ? new FileInfo(path).Length : 0) + (File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0);

    private static void DeleteDb(string path)
    {
        foreach (var f in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            if (File.Exists(f))
                File.Delete(f);
        }
    }

    private static double Median(List<double> values)
    {
        var s = values.OrderBy(x => x).ToList();
        return s.Count == 0 ? 0 : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }

    private static double Round(double value) => Math.Round(value, 2);
}
