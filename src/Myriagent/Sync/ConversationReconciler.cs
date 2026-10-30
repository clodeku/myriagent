using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Newtonsoft.Json;

namespace Myriagent.Sync;

/// <summary>唯讀讀一個未載入的對話（<see cref="ConversationReconciler.Read"/>）的結果種類</summary>
internal enum ReconcileFileStatus
{
    /// <summary>讀到了：項目的 Ord 已依 <see cref="OrdAssignRules.PlanLoad"/> 補好、秘密回答清除的結果已算好</summary>
    Ok,

    /// <summary>items.json 的 SHA-256 與上次重整時（<c>publish_meta.items_file_sha256</c>）相同：不投影</summary>
    Unchanged,

    /// <summary>資料夾有伺服器刪除的旁檔（D25）：只補送 conv.deleted、不送內容</summary>
    ServerDeleted,

    /// <summary>讀不到（被占用、沒有權限）：原檔不動，之後再試</summary>
    Unreadable,

    /// <summary>items.json 解析不了：唯讀，不改名（中樞載入時才備份），這次不送</summary>
    Corrupt,
}

/// <param name="ItemsSha256">items.json 的 SHA-256（小寫十六進位；沒有這個檔是 null）</param>
/// <param name="Items">項目（清單順序；已照中樞載入時的規則收尾、Ord 補好）</param>
/// <param name="AllIds">完整的項目 id 集合（<c>published_items</c> 有、這裡沒有的送 itemRemoved）</param>
/// <param name="Scrubbed">秘密回答清除的結果（對整份清單算一次）</param>
/// <param name="NewOrds">這次規劃才配的 Ord（項目 id → Ord）：與 outbox 同一個交易記進 published_items，之後中樞載入時以 hint 得到相同的值</param>
/// <param name="RemovalsTrusted">
/// <paramref name="AllIds"/> 可以拿來推斷刪除：items.json 不在（被改名、遺失）或有 <c>items.json.corrupt-*</c> 時是 false（清單可能少了東西，合併前審查 F2）
/// </param>
internal sealed record ReconcileFileResult(
    ReconcileFileStatus Status,
    string? ItemsSha256,
    IReadOnlyList<AgentChatItem> Items,
    IReadOnlyCollection<string> AllIds,
    IReadOnlyDictionary<string, string> Scrubbed,
    IReadOnlyList<(string Id, long Ord)> NewOrds,
    bool RemovalsTrusted = true)
{
    public static ReconcileFileResult Of(ReconcileFileStatus status, string? sha = null)
        => new(status, sha, [], Array.Empty<string>(), ConversationProjector.NothingScrubbed, []);
}

/// <summary>
/// 未載入的對話在執行緒集區（低優先）唯讀讀檔（WP4.3 計畫 D10、D12；步 12）：不寫任何檔、不經 <see cref="JsonFile.TryRead{T}"/>（它會把損毀的檔改名），
/// 不碰引擎或中樞的狀態（只有引數與檔案）。投影在引擎執行緒做（ver 由引擎配，D1、D7），這裡只交回項目
/// </summary>
internal static class ConversationReconciler
{
    private const string MetaFileName = "meta.json";
    private const string ItemsFileName = "items.json";

    /// <summary>
    /// 讀 <paramref name="directory"/> 的 items.json：有伺服器刪除的旁檔回 <see cref="ReconcileFileStatus.ServerDeleted"/>；SHA-256 等於
    /// <paramref name="knownItemsSha256"/> 回 <see cref="ReconcileFileStatus.Unchanged"/>；否則解析、照中樞載入的規則收尾
    /// （<see cref="ItemLoadRules.FinalizeOnLoad"/>）、以 <see cref="OrdAssignRules.PlanLoad"/>（meta 的 <c>NextOrd</c>、旁檔 sync-ord.json、
    /// <paramref name="hint"/>）補 Ord、對整份清單清除秘密回答。沒有 items.json 當成空清單（同中樞），但不拿來推斷刪除（<see cref="ReconcileFileResult.RemovalsTrusted"/>）
    /// </summary>
    /// <param name="metaNextOrd">meta.json 的 <c>NextOrd</c>（引擎用中樞給的中繼資料快照：未載入的對話中樞不改它）</param>
    public static ReconcileFileResult Read(string directory, string? knownItemsSha256, long? metaNextOrd, OrdHint? hint)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        try
        {
            thread.Priority = ThreadPriority.BelowNormal;
            return ReadCore(directory, knownItemsSha256, metaNextOrd, hint);
        }
        finally
        {
            thread.Priority = priority;
        }
    }

    /// <summary>
    /// 「已知、但中樞清單裡沒有」的對話的資料夾（D12；只看存在與否，不讀內容）：根目錄不在或讀不到時一律算讀取錯誤
    /// （磁碟沒接上時不能把每個對話都當成刪掉了）；File.Exists 對沒有權限的路徑回 false，所以資料夾在的時候先列一次確認讀得到
    /// </summary>
    public static FolderProbe ProbeFolder(string root, string conversationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(conversationId);
        try
        {
            if (!Directory.Exists(root))
                return new FolderProbe(false, false, false, ReadError: true);
            var dir = Path.Combine(root, conversationId);
            if (!Directory.Exists(dir))
            {
                // 根目錄要讀得到，「資料夾不在」才算數
                _ = Directory.EnumerateFileSystemEntries(root).Take(1).Count();
                return new FolderProbe(false, false, false, false);
            }

            _ = Directory.EnumerateFileSystemEntries(dir).Take(1).Count();
            var meta = File.Exists(Path.Combine(dir, MetaFileName));
            var items = File.Exists(Path.Combine(dir, ItemsFileName));
            var corrupt = Directory.EnumerateFiles(dir, MetaFileName + ".corrupt-*").Any();
            return new FolderProbe(meta, items, corrupt, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"檢查對話 {conversationId} 的資料夾失敗（當成讀取錯誤、不送刪除）：{ex.Message}");
            return new FolderProbe(false, false, false, ReadError: true);
        }
    }

    /// <summary>items.json 的大小（回補進度的估計；讀不到或不存在回 0）</summary>
    public static long ItemsFileBytes(string root, string conversationId)
    {
        try
        {
            var info = new FileInfo(Path.Combine(root, conversationId, ItemsFileName));
            return info.Exists ? info.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    private static ReconcileFileResult ReadCore(string directory, string? knownItemsSha256, long? metaNextOrd, OrdHint? hint)
    {
        // 旁檔讀不到也算已刪除（fail-closed，ConversationStore.ReadServerDeletedFile）
        if (ConversationStore.ReadServerDeletedFile(directory))
            return ReconcileFileResult.Of(ReconcileFileStatus.ServerDeleted);

        var path = Path.Combine(directory, ItemsFileName);
        byte[]? bytes = null;
        try
        {
            if (File.Exists(path))
            {
                // 先串流算 SHA-256：和上次相同就不把整個檔讀進記憶體（每次啟動的重整多半是這種）；變了才讀
                if (knownItemsSha256 is not null && HashShared(path) is var streamed && string.Equals(streamed, knownItemsSha256, StringComparison.Ordinal))
                    return ReconcileFileResult.Of(ReconcileFileStatus.Unchanged, streamed);
                bytes = ReadShared(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            Log.Warn($"重整時讀不到 {path}（之後再試）：{ex.Message}");
            return ReconcileFileResult.Of(ReconcileFileStatus.Unreadable);
        }

        // 摘要以讀進來的內容為準（算完雜湊之後檔案可能又被換掉）
        var sha = bytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (sha is not null && string.Equals(sha, knownItemsSha256, StringComparison.Ordinal))
            return ReconcileFileResult.Of(ReconcileFileStatus.Unchanged, sha);

        List<AgentChatItem> items;
        if (bytes is null)
        {
            items = [];
        }
        else
        {
            try
            {
                using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                var parsed = JsonConvert.DeserializeObject<List<AgentChatItem>>(reader.ReadToEnd(), JsonFile.Settings);
                if (parsed is null)
                    return ReconcileFileResult.Of(ReconcileFileStatus.Corrupt, sha);
                items = parsed.Where(i => i is not null).ToList();
            }
            catch (JsonException ex)
            {
                Log.Warn($"重整時 {path} 解析不了（唯讀、不改名；這次不送）：{ex.Message}");
                return ReconcileFileResult.Of(ReconcileFileStatus.Corrupt, sha);
            }
        }

        foreach (var item in items)
            ItemLoadRules.FinalizeOnLoad(item);

        var plan = OrdAssignRules.PlanLoad(items.ConvertAll(i => (i.Id, i.Ord)), metaNextOrd is >= 1 ? metaNextOrd : null,
            ConversationStore.ReadOrdReservationFile(directory), hint);
        var newOrds = new List<(string Id, long Ord)>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Ord is >= 1)
                continue;
            items[i].Ord = plan.Ords[i];
            if (items[i].Id is { } id)
                newOrds.Add((id, plan.Ords[i]));
        }

        if (plan.Stripped)
            Log.Warn($"重整時 {directory} 的項目沒有 Ord、meta 也沒有 NextOrd，但旁檔或發布紀錄有值（可能中間跑過舊版）：依旁檔與發布紀錄重配");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.Id is { } id)
                ids.Add(id);
        }

        // 沒有 items.json（被改名或遺失）、或有損毀備份：當成空清單照常發布，但不拿它推斷刪除
        var trusted = bytes is not null && !ConversationStore.ItemsCorruptBackupExists(directory);
        return new ReconcileFileResult(ReconcileFileStatus.Ok, sha, items, ids, SecretAnswerScrubRules.ScrubHistory(items), newOrds, trusted);
    }

    /// <summary>
    /// 讀整個檔（只配一份剛好大小的陣列、開著的時間盡量短）。允許中樞同時讀取；中樞這時以暫存檔取代它會被 Windows 拒絕，由 JsonFile.Write 重試
    /// </summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = OpenShared(path);
        var buffer = new byte[checked((int)stream.Length)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>串流算整個檔的 SHA-256（小寫十六進位；不把檔案讀進記憶體）</summary>
    private static string HashShared(string path)
    {
        using var stream = OpenShared(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
}
