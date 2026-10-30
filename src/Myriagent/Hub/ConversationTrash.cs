using Myriagent.Core;
using Newtonsoft.Json;

namespace Myriagent.Hub;

/// <summary>一個進了回收區的對話的資訊（WP4.4 計畫 D24；存在資料夾旁邊的 <c>{id}.trash.json</c>，資料夾內容不變）</summary>
/// <param name="TrashedAt">
/// 資訊檔裡是真正搬進回收區的時間（Unix 毫秒）：保留期從這裡算（<see cref="ConversationTrash.TryMove"/> 一律寫成搬的當下，合併前審查 AMO-01）。
/// 呼叫端與 pending 傳進來的是遠端刪除的時間（伺服器刪除旁檔的時間）
/// </param>
/// <param name="CommandId">刪除它的遠端指令（cid）</param>
/// <param name="IssuerLabel">發起的瀏覽器與作業系統</param>
/// <param name="Title">刪除時的標題（回收區清單顯示用，WP4.6）</param>
internal sealed record TrashInfo(long TrashedAt, string? CommandId, string? IssuerLabel, string? Title);

/// <summary>回收區裡的一個對話</summary>
internal sealed record TrashEntry(string ConversationId, TrashInfo Info);

/// <summary>
/// 遠端刪除的對話的回收區（WP4.4 計畫步 7、11，D24、D25、SEC-27）：資料根目錄裡的 <c>conversations-trash\{id}</c>，與 <c>conversations\</c> 在同一個磁碟，
/// 搬移就是改名、原本的檔案內容不變；30 天後清掉。搬不動的（檔案被占用）記在 <c>pending-moves.json</c>，下次建構中樞時、載入對話之前完成，
/// 仍搬不動的不出現在清單。搬進來之前一定先在對話資料夾寫伺服器刪除的旁檔（WP4.3 的 <c>sync-server-deleted.json</c>，資料夾帶著它走）：
/// 之後不論還原或手動搬回，都是 localOnly、不重新發布。只處理 app 建立的對話資料夾名稱（<see cref="ConversationTrashRules.IsTrashableId"/>）。
/// 方法都不丟 I/O 例外（記錄後回失敗），可以在任何執行緒呼叫；搬移與 pending 檔的讀改寫以同一把鎖排隊
/// </summary>
internal sealed class ConversationTrash(string trashRoot, string conversationsRoot, TimeProvider time)
{
    /// <summary>對話根目錄與回收區的旁檔讀寫（只用 WP4.3 的伺服器刪除旁檔與 meta.json 是否存在：與中樞寫的是同一個格式）</summary>
    private readonly ConversationStore conversationsFiles = new(conversationsRoot);

    private readonly ConversationStore trashFiles = new(trashRoot);

    /// <summary>保留天數</summary>
    public const int RetentionDays = 30;

    /// <summary>還沒搬完的對話清單</summary>
    public const string PendingFileName = "pending-moves.json";

    /// <summary>資訊檔的副檔名（<c>{id}.trash.json</c>）</summary>
    public const string InfoSuffix = ".trash.json";

    /// <summary>對話資料夾裡的 meta.json（同 ConversationStore；只看修改時間）</summary>
    private const string MetaFileName = "meta.json";

    /// <summary>
    /// 搬移與 pending 檔的讀改寫排隊（背景的搬移與下一個中樞建構時的補搬都會碰；同一個程序只有一份資料根目錄）：
    /// 兩邊同時搬同一個 id 時，後到的會把先到的剛搬好的資料夾當成「舊資料夾」清掉
    /// </summary>
    private static readonly object Gate = new();

    public string RootDirectory { get; } = trashRoot;

    /// <summary>回收區的根目錄：對話根目錄的上層加 <c>conversations-trash</c>（不讀 AppPaths；<c>--data</c> 時跟著資料根目錄）</summary>
    public static string RootFor(string conversationsRoot)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(conversationsRoot);
        return Path.GetDirectoryName(trimmed) is { Length: > 0 } parent ? Path.Combine(parent, "conversations-trash") : trimmed + "-trash";
    }

    /// <summary>現在（Unix 毫秒）</summary>
    public long NowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>
    /// 把 <c>conversations\{id}</c> 搬進回收區：先在對話資料夾寫伺服器刪除的旁檔（時間是 <paramref name="info"/> 的刪除時間；已有就不重寫；
    /// 沒有 meta.json 的資料夾載入不了，不用寫），再寫資訊檔（TrashedAt 是現在：保留期從真正搬進來的時候算，搬了幾天才搬得動的不會一進來就被清掉）、改名。
    /// 原資料夾不存在＝沒有東西要搬，回 true；回收區已有同一個 id 的舊資料夾時換成這一份。
    /// 失敗（旁檔寫不進去、檔案被占用、沒有權限）回 false，呼叫端重試、最後留在 pending
    /// </summary>
    public bool TryMove(string conversationId, TrashInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!ConversationTrashRules.IsTrashableId(conversationId))
        {
            Log.Warn($"回收區：對話 id {conversationId} 不是 app 建立的資料夾名稱，不搬");
            return false;
        }

        var source = Path.Combine(conversationsRoot, conversationId);
        var target = Path.Combine(RootDirectory, conversationId);
        lock (Gate)
        {
            return MoveLocked(conversationId, source, target, info);
        }
    }

    private bool MoveLocked(string conversationId, string source, string target, TrashInfo info)
    {
        try
        {
            if (!Directory.Exists(source))
            {
                // 已經搬好（上次搬完、還沒從 pending 拿掉就結束了）或被手動刪掉：後者把前一次留下的資訊檔一併拿掉
                if (!Directory.Exists(target) && File.Exists(InfoPath(conversationId)))
                    DeleteInfo(conversationId);
                return true;
            }

            if (conversationsFiles.MetaExists(conversationId) && !EnsureServerDeleted(conversationsFiles, conversationId, info.TrashedAt))
            {
                Log.Warn($"回收區：對話 {conversationId} 的伺服器刪除旁檔寫不進去，這次不搬");
                return false;
            }

            Directory.CreateDirectory(RootDirectory);
            WriteInfo(conversationId, info with { TrashedAt = NowMs() });
            if (Directory.Exists(target) && !DirectoryRemover.TryDelete(target))
            {
                Log.Warn($"回收區：對話 {conversationId} 的舊資料夾清不掉，這次不搬");
                return false;
            }

            Directory.Move(source, target);
            Log.Info($"對話 {conversationId} 移到回收區");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"對話 {conversationId} 移到回收區失敗：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 記進 pending（搬移之前先記：搬到一半程式結束，下次建構中樞時照樣完成）；寫不進去只記錄。一併記下 meta.json 這時的修改時間：
    /// 補搬之前用它判斷刪除之後資料夾有沒有再被用過（<see cref="CompletePendingMoves"/>）
    /// </summary>
    public void RecordPending(string conversationId, TrashInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!ConversationTrashRules.IsTrashableId(conversationId))
            return;
        lock (Gate)
        {
            var moves = ReadPending();
            moves.RemoveAll(m => string.Equals(m.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase));
            moves.Add(PendingMove.From(conversationId, info, MetaWrittenAt(conversationId)));
            WritePending(moves);
        }
    }

    /// <summary>搬好了：從 pending 拿掉</summary>
    public void ClearPending(string conversationId)
    {
        lock (Gate)
        {
            var moves = ReadPending();
            if (moves.RemoveAll(m => string.Equals(m.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase)) > 0)
                WritePending(moves);
        }
    }

    /// <summary>
    /// 完成 pending 的搬移（建構中樞時、載入對話之前；每個試一次）：原資料夾已不在的（已經搬好、或被手動刪掉）拿掉，
    /// id 不合格的拿掉。刪除之後資料夾又被用過的（meta.json 比記進 pending 時新超過 <see cref="UsedAfterDeleteSlackMs"/>：回滾到舊版、在舊版繼續用）
    /// 不搬：確認有伺服器刪除的旁檔（只在這台、不發布）就從 pending 拿掉、照常載入，旁檔寫不進去就這次仍略過（合併前審查 AMO-01）。
    /// 回仍未完成的 id（不分大小寫）：中樞載入時略過它們
    /// </summary>
    public IReadOnlySet<string> CompletePendingMoves()
    {
        lock (Gate)
        {
            var moves = ReadPending();
            if (moves.Count == 0)
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var remaining = new List<PendingMove>();
            foreach (var move in moves)
            {
                if (!ConversationTrashRules.IsTrashableId(move.ConversationId))
                    continue;
                var id = move.ConversationId!;
                if (UsedAfterDelete(move))
                {
                    if (!EnsureServerDeleted(conversationsFiles, id, move.TrashedAt))
                    {
                        Log.Warn($"回收區：對話 {id} 在遠端刪除之後又被用過，但伺服器刪除旁檔寫不進去：這次仍不出現在清單");
                        remaining.Add(move);
                        continue;
                    }

                    Log.Warn($"回收區：對話 {id} 在遠端刪除之後又被用過（可能在舊版繼續用）：不搬進回收區，留在清單（只在這台電腦）");
                    if (!Directory.Exists(Path.Combine(RootDirectory, id)) && File.Exists(InfoPath(id)))
                        DeleteInfo(id);
                    continue;
                }

                if (!TryMove(id, move.ToInfo()))
                    remaining.Add(move);
            }

            WritePending(remaining);
            return remaining.Select(m => m.ConversationId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>刪除當下可能還有最後一次存檔（程序停下時的寫入）：meta.json 比記下的新超過這麼久（毫秒）才算刪除之後又用過</summary>
    public const long UsedAfterDeleteSlackMs = 60_000;

    /// <summary>
    /// 刪除之後資料夾又被用過：meta.json 的修改時間比記進 pending 時（舊的 pending 沒有記，改比刪除的時間）新超過 <see cref="UsedAfterDeleteSlackMs"/>。
    /// meta.json 不在或讀不到修改時間時不算（照常搬）
    /// </summary>
    private bool UsedAfterDelete(PendingMove move)
        => MetaWrittenAt(move.ConversationId!) is { } written && written - (move.MetaWrittenAt ?? move.TrashedAt) > UsedAfterDeleteSlackMs;

    /// <summary>對話根目錄裡這個對話的 meta.json 的修改時間（Unix 毫秒）；不在或讀不到是 null</summary>
    private long? MetaWrittenAt(string conversationId)
    {
        try
        {
            var meta = new FileInfo(Path.Combine(conversationsRoot, conversationId, MetaFileName));
            return meta.Exists ? new DateTimeOffset(meta.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"回收區：對話 {conversationId} 的 meta.json 讀不到修改時間：{ex.Message}");
            return null;
        }
    }

    /// <summary>回收區裡有這個對話的資料夾</summary>
    public bool Contains(string conversationId)
        => ConversationTrashRules.IsTrashableId(conversationId) && Directory.Exists(Path.Combine(RootDirectory, conversationId));

    /// <summary>這個對話的資訊檔（沒有或讀不到是 null）</summary>
    public TrashInfo? InfoOf(string conversationId) => ConversationTrashRules.IsTrashableId(conversationId) ? ReadInfo(conversationId) : null;

    /// <summary>
    /// 從回收區搬回 <c>conversations\{id}</c>（WP4.6 的還原鈕經 <c>AgentHub.RestoreFromTrash</c>；計畫 D25）。回收區沒有、對話根目錄已有同一個 id 的資料夾、
    /// 資料夾裡沒有 meta.json（搬回去也載入不了）時回 false，什麼都不動。搬回之前確認回收區的資料夾有伺服器刪除的旁檔（沒有就寫，寫不進去回 false）：
    /// 還原的對話一律是 localOnly、發布者不發布。搬好之後拿掉資訊檔與 pending 裡的這個 id（不然下次建構中樞又會被搬回回收區）
    /// </summary>
    public bool TryRestore(string conversationId)
    {
        if (!ConversationTrashRules.IsTrashableId(conversationId))
            return false;
        var source = Path.Combine(RootDirectory, conversationId);
        var target = Path.Combine(conversationsRoot, conversationId);
        lock (Gate)
        {
            try
            {
                if (!Directory.Exists(source) || Directory.Exists(target) || File.Exists(target) || !trashFiles.MetaExists(conversationId))
                    return false;
                if (!EnsureServerDeleted(trashFiles, conversationId, NowMs()))
                {
                    Log.Warn($"回收區：對話 {conversationId} 的伺服器刪除旁檔寫不進去，不還原");
                    return false;
                }

                Directory.CreateDirectory(conversationsRoot);
                Directory.Move(source, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"對話 {conversationId} 從回收區還原失敗：{ex.Message}");
                return false;
            }

            DeleteInfo(conversationId);
            var moves = ReadPending();
            if (moves.RemoveAll(m => string.Equals(m.ConversationId, conversationId, StringComparison.OrdinalIgnoreCase)) > 0)
                WritePending(moves);
            Log.Info($"對話 {conversationId} 從回收區搬回");
            return true;
        }
    }

    /// <summary>對話資料夾有伺服器刪除的旁檔（已有就不重寫，保留第一次的時間；資料夾讀不到也算有，同 WP4.3 的 fail-closed）；寫不進去回 false</summary>
    private static bool EnsureServerDeleted(ConversationStore files, string conversationId, long atMs)
        => files.IsServerDeleted(conversationId) || files.TryWriteServerDeleted(conversationId, atMs);

    /// <summary>
    /// 清掉超過保留期的（資料夾與資訊檔）；回清掉的數目。沒有資訊檔的資料夾從現在起算（補寫資訊檔，不會因此提早清掉）；
    /// 資料夾已不在、超過保留期的資訊檔一併刪掉；id 不合格的一律不動
    /// </summary>
    public int Purge()
    {
        if (!Directory.Exists(RootDirectory))
            return 0;
        var now = NowMs();
        var purged = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
            {
                var id = Path.GetFileName(dir);
                if (!ConversationTrashRules.IsTrashableId(id))
                    continue;
                var info = ReadInfo(id);
                if (info == null)
                {
                    WriteInfo(id, new TrashInfo(now, null, null, null));
                    continue;
                }

                if (!ConversationTrashRules.IsExpired(info.TrashedAt, now))
                    continue;
                if (DirectoryRemover.TryDelete(dir))
                {
                    DeleteInfo(id);
                    purged++;
                    Log.Info($"回收區：對話 {id} 超過 {RetentionDays} 天，已清掉");
                }
            }

            foreach (var file in Directory.EnumerateFiles(RootDirectory, "*" + InfoSuffix))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(InfoSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var id = name[..^InfoSuffix.Length];
                if (ConversationTrashRules.IsTrashableId(id) && !Directory.Exists(Path.Combine(RootDirectory, id))
                    && ReadInfo(id) is { } stale && ConversationTrashRules.IsExpired(stale.TrashedAt, now))
                    DeleteInfo(id);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"清理回收區失敗：{ex.Message}");
        }

        return purged;
    }

    /// <summary>回收區的對話（新到舊；只列資料夾還在、資訊檔讀得到的；WP4.6 的清單）</summary>
    public IReadOnlyList<TrashEntry> List()
    {
        var result = new List<TrashEntry>();
        if (!Directory.Exists(RootDirectory))
            return result;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
            {
                var id = Path.GetFileName(dir);
                if (ConversationTrashRules.IsTrashableId(id) && ReadInfo(id) is { } info)
                    result.Add(new TrashEntry(id, info));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"讀回收區失敗：{ex.Message}");
        }

        return result.OrderByDescending(e => e.Info.TrashedAt).ToList();
    }

    private string InfoPath(string conversationId) => Path.Combine(RootDirectory, conversationId + InfoSuffix);

    private void WriteInfo(string conversationId, TrashInfo info) => JsonFile.Write(InfoPath(conversationId), InfoFile.From(info));

    private TrashInfo? ReadInfo(string conversationId)
        => JsonFile.TryRead<InfoFile>(InfoPath(conversationId)) is (JsonReadStatus.Ok, { TrashedAt: { } at } file, _)
            ? new TrashInfo(at, file.CommandId, file.Issuer, file.Title)
            : null;

    private void DeleteInfo(string conversationId)
    {
        try
        {
            File.Delete(InfoPath(conversationId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"回收區：對話 {conversationId} 的資訊檔刪不掉：{ex.Message}");
        }
    }

    private List<PendingMove> ReadPending()
        => JsonFile.TryRead<PendingFile>(Path.Combine(RootDirectory, PendingFileName)) is (JsonReadStatus.Ok, { Moves: { } moves }, _)
            ? moves.Where(m => m.ConversationId != null).ToList()
            : [];

    private bool WritePending(List<PendingMove> moves)
    {
        var path = Path.Combine(RootDirectory, PendingFileName);
        try
        {
            if (moves.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }

            JsonFile.Write(path, new PendingFile { Moves = moves });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"回收區的 {PendingFileName} 寫不進去：{ex.Message}");
            return false;
        }
    }

    /// <summary><c>{id}.trash.json</c> 的內容</summary>
    private sealed class InfoFile
    {
        [JsonProperty("trashedAt")]
        public long? TrashedAt { get; set; }

        [JsonProperty("commandId")]
        public string? CommandId { get; set; }

        [JsonProperty("issuer")]
        public string? Issuer { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        public static InfoFile From(TrashInfo info) => new() { TrashedAt = info.TrashedAt, CommandId = info.CommandId, Issuer = info.IssuerLabel, Title = info.Title };
    }

    /// <summary><c>pending-moves.json</c> 的內容</summary>
    private sealed class PendingFile
    {
        [JsonProperty("moves")]
        public List<PendingMove>? Moves { get; set; }
    }

    private sealed class PendingMove
    {
        [JsonProperty("conversationId")]
        public string? ConversationId { get; set; }

        [JsonProperty("trashedAt")]
        public long TrashedAt { get; set; }

        [JsonProperty("commandId")]
        public string? CommandId { get; set; }

        [JsonProperty("issuer")]
        public string? Issuer { get; set; }

        [JsonProperty("title")]
        public string? Title { get; set; }

        /// <summary>記進 pending 時 meta.json 的修改時間（Unix 毫秒；那時沒有 meta.json 是 null）</summary>
        [JsonProperty("metaWrittenAt")]
        public long? MetaWrittenAt { get; set; }

        public static PendingMove From(string conversationId, TrashInfo info, long? metaWrittenAt)
            => new()
            {
                ConversationId = conversationId, TrashedAt = info.TrashedAt, CommandId = info.CommandId, Issuer = info.IssuerLabel, Title = info.Title,
                MetaWrittenAt = metaWrittenAt,
            };

        public TrashInfo ToInfo() => new(TrashedAt, CommandId, Issuer, Title);
    }
}
