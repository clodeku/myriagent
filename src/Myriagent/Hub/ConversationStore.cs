using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 對話存在本機：%LOCALAPPDATA%\Myriagent\conversations\{對話 Id}\（meta.json、items.json、attachments\、workspace\）。
/// 寫檔一律先寫暫存檔再取代，避免寫到一半當掉留下壞檔。
/// 同步政策不是 full 時另外寫一份旁檔 sync-policy.json（D37）：WP4.1 之前的版本不認得 meta.json 的 SyncPolicy，把對話存回去就會刪掉它
/// （null 會被解讀成 full），旁檔它們不讀也不寫；載入時取兩者中較嚴格的
/// </summary>
internal sealed class ConversationStore
{
    private const string MetaFileName = "meta.json";
    private const string ItemsFileName = "items.json";

    /// <summary>同步政策的旁檔（內容 <c>{"policy":"localOnly"}</c>；政策是 full 或沒設過時沒有這個檔）</summary>
    public const string SyncPolicyFileName = "sync-policy.json";

    /// <summary>
    /// Ord 保留的旁檔（內容 <c>{"reserved":N}</c>，WP4.3 計畫 D9）：只在配對後、中樞要發布這個對話時才寫（未配對的電腦不會有，S6）；
    /// 舊版程式不認得它、不碰它，所以舊版存檔剝掉 meta.json 的 <c>NextOrd</c> 之後仍然記得保留到哪裡
    /// </summary>
    internal const string OrdFileName = "sync-ord.json";

    public ConversationStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("根目錄不可為空", nameof(rootDirectory));
        RootDirectory = rootDirectory;
    }

    public string RootDirectory { get; }

    public string GetConversationDirectory(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || conversationId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || conversationId.Contains(".."))
            throw new ArgumentException($"不合法的對話 Id：{conversationId}", nameof(conversationId));
        return Path.Combine(RootDirectory, conversationId);
    }

    /// <summary>對話紀錄（items.json）的路徑；側欄的內文搜尋只讀（ConversationSearchIndex）</summary>
    public string GetItemsPath(string conversationId) => Path.Combine(GetConversationDirectory(conversationId), ItemsFileName);

    public string GetAttachmentDirectory(string conversationId) => Path.Combine(GetConversationDirectory(conversationId), "attachments");

    /// <summary>給模型看的縮圖與影片畫面（附件資料夾底下的隱藏子資料夾）</summary>
    public string GetModelImageDirectory(string conversationId) => Path.Combine(GetAttachmentDirectory(conversationId), ".model");

    public string GetDefaultWorkingDirectory(string conversationId) => Path.Combine(GetConversationDirectory(conversationId), "workspace");

    // ───────────────────────────── 複本拒寫（WP4.3 計畫步 15，F11：第二道防線） ─────────────────────────────

    /// <summary>
    /// 其他電腦的對話在這台的複本 id（中樞建立複本時登記；不分大小寫：資料夾名不分）。登記過的 id 一律不寫 meta.json、items.json、
    /// Ord 保留旁檔、queue.json，也不刪資料夾：中樞自己先不存複本（第一道），這裡防的是漏掉的呼叫端。中樞在 UI 執行緒登記，
    /// 刪資料夾在執行緒集區，所以加鎖。登記之後不取消（同一個 id 不會變成本機的對話）
    /// </summary>
    private readonly HashSet<string> replicaIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>登記複本 id（之後這個 id 的寫入一律拒絕並記警告）</summary>
    internal void MarkReplica(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        lock (replicaIds)
            replicaIds.Add(conversationId);
    }

    /// <summary>這個 id 登記成複本了</summary>
    internal bool IsReplica(string conversationId)
    {
        lock (replicaIds)
            return replicaIds.Contains(conversationId);
    }

    /// <summary>複本的寫入：記警告、回 true（呼叫端不寫）</summary>
    private bool RefusesReplica(string conversationId, string action)
    {
        if (!IsReplica(conversationId))
            return false;
        Log.Warn($"對話 {conversationId} 是其他電腦的對話（複本）：不{action}");
        return true;
    }

    /// <summary>啟動時要告訴使用者的事（對話資料檔損毀已還原、暫時讀不到）；中樞在介面就緒後顯示</summary>
    public string? StartupWarning { get; private set; }

    /// <summary>
    /// 所有對話（新到舊）。meta.json 損毀（原檔已備份）但紀錄還在時，以資料夾名稱重建一份最小的 meta 並存回，
    /// 對話才不會從清單消失；暫時讀不到的這次略過（原檔不動）。兩種情況都記在 StartupWarning
    /// </summary>
    public List<AgentConversation> LoadConversations()
    {
        var result = new List<AgentConversation>();
        if (!Directory.Exists(RootDirectory))
            return result;

        int recovered = 0, unreadable = 0;
        foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
        {
            var id = Path.GetFileName(dir);
            var meta = Path.Combine(dir, MetaFileName);
            if (!File.Exists(meta))
                continue;
            var (status, conversation, _) = JsonFile.TryRead<AgentConversation>(meta);
            switch (status)
            {
                case JsonReadStatus.Ok when conversation!.Id == id:
                    // 舊版存回時刪掉的政策從旁檔補回（記憶體裡；下次存檔寫回 meta.json）
                    conversation.SyncPolicy = ConversationStoreRules.MergeSyncPolicy(conversation.SyncPolicy, ReadSyncPolicySidecar(dir));
                    result.Add(conversation);
                    break;
                case JsonReadStatus.Corrupt when TryRecover(dir, id) is { } rebuilt:
                    result.Add(rebuilt);
                    recovered++;
                    break;
                case JsonReadStatus.Unreadable:
                    unreadable++;
                    break;
            }
        }

        var warnings = new List<string>();
        if (recovered > 0)
            warnings.Add($"{recovered} 個對話的資料檔（meta.json）已損毀，已從對話紀錄還原（原檔備份為 meta.json.corrupt-…）；帳戶需要重新選擇，下一則訊息會附上先前的節錄。");
        if (unreadable > 0)
            warnings.Add($"{unreadable} 個對話的資料檔暫時讀不到（可能被其他程式占用），這次不會出現在清單；請重新開啟 app。");
        StartupWarning = warnings.Count > 0 ? string.Concat(warnings) : null;

        return result.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    /// <summary>meta.json 損毀：紀錄（items.json）還在才還原（沒有內容就沒有可找回的東西）；重建的 meta 立即存回</summary>
    private AgentConversation? TryRecover(string dir, string id)
    {
        var items = Path.Combine(dir, ItemsFileName);
        if (!ConversationStoreRules.IsConversationFolderName(id) || !File.Exists(items))
            return null;
        var rebuilt = ConversationStoreRules.RebuildMeta(id, ReadFirstUserMessage(items), File.GetLastWriteTime(items), ReadSyncPolicySidecar(dir));
        try
        {
            SaveConversation(rebuilt);
            Log.Warn($"對話 {id} 的 meta.json 損毀，已從紀錄重建");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"對話 {id} 重建的 meta.json 存檔失敗：{ex.Message}");
        }
        return rebuilt;
    }

    /// <summary>標題用的第一則使用者訊息；只讀不改（紀錄檔本身損毀時由開啟對話的流程備份與提示）</summary>
    private static string? ReadFirstUserMessage(string itemsPath)
    {
        try
        {
            var items = JsonConvert.DeserializeObject<List<AgentChatItem>>(File.ReadAllText(itemsPath), JsonFile.Settings);
            return items?.FirstOrDefault(i => i.Kind == AgentItemKind.User)?.Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public List<AgentChatItem> LoadItems(string conversationId) => TryLoadItems(conversationId).Items;

    /// <summary>讀對話內容；失敗時 Status 告訴呼叫端不可以把空清單存回去（Corrupt 已備份原檔、Unreadable 原檔還在）</summary>
    public (List<AgentChatItem> Items, JsonReadStatus Status, string? BackupPath) TryLoadItems(string conversationId)
    {
        var (status, items, backup) = JsonFile.TryRead<List<AgentChatItem>>(Path.Combine(GetConversationDirectory(conversationId), ItemsFileName));
        return (items ?? new List<AgentChatItem>(), status, backup);
    }

    /// <summary>
    /// 存 meta.json，同時維持同步政策的旁檔：政策不是 full（也不是 null）就寫旁檔、改回 full 就刪掉；null 不動旁檔
    /// （null 可能是舊版存回時刪掉的，旁檔才是對的）。順序讓中途當掉時合併的結果只會比較嚴格：要旁檔時先寫旁檔再寫 meta.json，
    /// 改回 full 時先寫 meta.json 再刪旁檔（刪不掉只記錄：下次載入仍是旁檔的值，比較嚴格）
    /// </summary>
    public void SaveConversation(AgentConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (RefusesReplica(conversation.Id, "寫 " + MetaFileName))
            return;
        var dir = GetConversationDirectory(conversation.Id);
        var sidecar = Path.Combine(dir, SyncPolicyFileName);
        var policy = conversation.SyncPolicy;
        if (ConversationStoreRules.NeedsSyncPolicySidecar(policy) && ReadSyncPolicySidecar(dir) != policy)
            JsonFile.Write(sidecar, new SyncPolicySidecar { Policy = policy });
        JsonFile.Write(Path.Combine(dir, MetaFileName), conversation);
        if (policy == ConversationSyncPolicies.Full && File.Exists(sidecar))
        {
            try
            {
                File.Delete(sidecar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"對話 {conversation.Id} 的同步政策旁檔刪不掉（下次載入仍照旁檔）：{ex.Message}");
            }
        }
    }

    /// <summary>旁檔的政策：沒有旁檔是 null；讀不到、損毀或空的一律當成 localOnly（fail-closed）</summary>
    private static string? ReadSyncPolicySidecar(string dir)
    {
        var (status, value, _) = JsonFile.TryRead<SyncPolicySidecar>(Path.Combine(dir, SyncPolicyFileName));
        return status switch
        {
            JsonReadStatus.Missing => null,
            JsonReadStatus.Ok when !string.IsNullOrEmpty(value!.Policy) => value.Policy,
            _ => ConversationSyncPolicies.LocalOnly,
        };
    }

    /// <summary>旁檔的內容</summary>
    private sealed class SyncPolicySidecar
    {
        [JsonProperty("policy")]
        public string? Policy { get; set; }
    }

    public void SaveItems(string conversationId, IReadOnlyList<AgentChatItem> items)
    {
        if (RefusesReplica(conversationId, "寫 " + ItemsFileName))
            return;
        JsonFile.Write(Path.Combine(GetConversationDirectory(conversationId), ItemsFileName), items ?? Array.Empty<AgentChatItem>());
    }

    /// <summary>增量存 items.json（<paramref name="snapshot"/> 是 ItemsSaveCache 取的快照；輸出和整份序列化逐位元組相同，ItemsJsonFile）</summary>
    public void SaveItems(string conversationId, IReadOnlyList<AgentChatItem> snapshot, ItemsJsonLayout layout)
    {
        if (RefusesReplica(conversationId, "寫 " + ItemsFileName))
            return;
        ItemsJsonFile.Write(Path.Combine(GetConversationDirectory(conversationId), ItemsFileName), snapshot ?? Array.Empty<AgentChatItem>(), layout);
    }

    /// <summary>存 queue.json（排隊訊息、待送附件與拒絕說明；QueueStore 的寫入一律經這裡，複本才擋得住）</summary>
    public void SaveQueue(string conversationId, QueueSnapshot snapshot)
    {
        if (RefusesReplica(conversationId, "寫 " + QueueStore.FileName))
            return;
        QueueStore.Save(GetConversationDirectory(conversationId), snapshot);
    }

    // ───────────────────────────── Ord 保留的旁檔（WP4.3 計畫 D9、D10） ─────────────────────────────

    /// <summary>測試：在指定點（"SaveOrdReservation:sidecar"＝旁檔寫好之後）丟例外，模擬寫到一半當掉；正式程式不設</summary>
    internal Action<string>? FaultInjection { get; set; }

    /// <summary>這個對話 meta.json 在不在（保留 Ord 只對存過的對話做，不建資料夾）</summary>
    public bool MetaExists(string conversationId) => File.Exists(Path.Combine(GetConversationDirectory(conversationId), MetaFileName));

    /// <summary>讀這個對話的 Ord 保留旁檔（中樞在 UI 執行緒用；只讀，見 <see cref="ReadOrdReservationFile"/>）</summary>
    public OrdReservation ReadOrdReservation(string conversationId) => ReadOrdReservationFile(GetConversationDirectory(conversationId));

    /// <summary>
    /// 讀對話資料夾裡的 Ord 保留旁檔（執行緒集區也用：D10 的未載入對話）。只讀：損毀的檔不改名（不經 <see cref="JsonFile.TryRead{T}"/>），
    /// 下一次保留時整份蓋掉。沒有旁檔是 (null, false)；讀不到、內容損毀、值不合格（不是 ≥1 的整數）一律當成損毀：
    /// 不知道保留到哪裡，下一個 Ord 多跳（寧可跳號，也不重用）
    /// </summary>
    public static OrdReservation ReadOrdReservationFile(string conversationDirectory)
    {
        var path = Path.Combine(conversationDirectory, OrdFileName);
        if (!File.Exists(path))
            return new OrdReservation(null, false);
        try
        {
            var token = JToken.Parse(File.ReadAllText(path, Utf8));
            if (token is JObject o && o["reserved"] is JValue { Type: JTokenType.Integer, Value: long reserved } && reserved >= 1)
                return new OrdReservation(reserved, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Ord 保留旁檔讀不到或損毀（{path}，下一個 Ord 多跳）：{ex.Message}");
            return new OrdReservation(null, true);
        }
        Log.Warn($"Ord 保留旁檔的內容不合格（{path}，下一個 Ord 多跳）");
        return new OrdReservation(null, true);
    }

    /// <summary>
    /// 寫 Ord 保留旁檔（先保留、後配發：中樞寫好這個檔之後才改 meta.json 的 <c>NextOrd</c>、才配發）。meta.json 不存在（草稿、已刪除、
    /// 從沒存過）就回 false、不建資料夾；寫入失敗也回 false（記錄），呼叫端不配發
    /// </summary>
    public bool TrySaveOrdReservation(string conversationId, long reserved)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reserved, 1L);
        if (RefusesReplica(conversationId, "寫 " + OrdFileName))
            return false;
        var dir = GetConversationDirectory(conversationId);
        if (!File.Exists(Path.Combine(dir, MetaFileName)))
            return false;
        try
        {
            JsonFile.Write(Path.Combine(dir, OrdFileName), new OrdSidecar { Reserved = reserved });
            FaultInjection?.Invoke("SaveOrdReservation:sidecar");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"對話 {conversationId} 的 Ord 保留旁檔寫不進去：{ex.Message}");
            return false;
        }
    }

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Ord 保留旁檔的內容</summary>
    private sealed class OrdSidecar
    {
        [JsonProperty("reserved")]
        public long Reserved { get; set; }
    }

    // ───────────────────────────── 伺服器刪除的旁檔（WP4.3 計畫 D25；擁有者 2026-10-19 的答 4） ─────────────────────────────

    /// <summary>
    /// 已發布過的對話改成「只在這台電腦」（含換到 Antigravity）等於在伺服器上刪除，之後不能再改回同步：發布者先寫這個旁檔（內容
    /// <c>{"at": 毫秒}</c>，只是資訊）才送 <c>conv.deleted</c>。只看檔案在不在；舊版程式不認得它、不碰它
    /// </summary>
    internal const string ServerDeletedFileName = "sync-server-deleted.json";

    /// <summary>這個對話已從伺服器刪除（見 <see cref="ReadServerDeletedFile"/>）</summary>
    public bool IsServerDeleted(string conversationId) => ReadServerDeletedFile(GetConversationDirectory(conversationId));

    /// <summary>
    /// 對話資料夾裡有沒有伺服器刪除的旁檔（執行緒集區也用：D10 的未載入對話；只讀）。只看檔案在不在，不讀內容、不經
    /// <see cref="JsonFile.TryRead{T}"/> 的損毀改名：內容損毀照樣算已刪除，萬一被改名成 <c>.corrupt-*</c> 也算；
    /// 資料夾讀不到也當成已刪除（fail-closed：刪除過的對話不能因為讀檔失敗而「復活」）。資料夾不存在是 false
    /// </summary>
    public static bool ReadServerDeletedFile(string conversationDirectory)
    {
        try
        {
            if (File.Exists(Path.Combine(conversationDirectory, ServerDeletedFileName)))
                return true;
            return Directory.Exists(conversationDirectory)
                   && Directory.EnumerateFiles(conversationDirectory, ServerDeletedFileName + ".corrupt-*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"伺服器刪除的旁檔查不到（{conversationDirectory}，當成已刪除）：{ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 項目檔損毀過（有 <c>items.json.corrupt-*</c>，JsonFile 改名的損毀備份）：目前的項目清單可能少了東西，「清單裡沒有＝刪掉了」不能拿來推斷
    /// （發布時不送推斷出來的 itemRemoved，合併前審查 F2）。執行緒集區也用（只讀）；資料夾讀不到也當成損毀過（fail-closed），不存在是 false
    /// </summary>
    public static bool ItemsCorruptBackupExists(string conversationDirectory)
    {
        try
        {
            return Directory.Exists(conversationDirectory)
                   && Directory.EnumerateFiles(conversationDirectory, ItemsFileName + ".corrupt-*").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"項目檔的損毀備份查不到（{conversationDirectory}，當成損毀過）：{ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 寫伺服器刪除的旁檔。meta.json 不存在（草稿、已刪除、從沒存過）就回 false、不建資料夾，所以草稿殘留的判斷不受影響；
    /// 寫入失敗也回 false（記錄），呼叫端不送 <c>conv.deleted</c>
    /// </summary>
    public bool TryWriteServerDeleted(string conversationId, long atMs)
    {
        var dir = GetConversationDirectory(conversationId);
        if (!File.Exists(Path.Combine(dir, MetaFileName)))
            return false;
        try
        {
            JsonFile.Write(Path.Combine(dir, ServerDeletedFileName), new ServerDeletedSidecar { At = atMs });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"對話 {conversationId} 的伺服器刪除旁檔寫不進去：{ex.Message}");
            return false;
        }
    }

    /// <summary>伺服器刪除旁檔的內容</summary>
    private sealed class ServerDeletedSidecar
    {
        [JsonProperty("at")]
        public long At { get; set; }
    }

    /// <summary>
    /// 沒送出的草稿留下的資料夾（草稿只在記憶體裡，正常結束會刪掉；登出、關機、當掉時就留下附件複本）：
    /// 名稱是對話 Id，裡面只有 attachments\ 與空的 workspace\（沒有 meta.json、items.json，連損毀備份與暫存檔都沒有）。
    /// 草稿不存檔、不會有同步政策的旁檔；有旁檔的資料夾（存過 meta.json 的對話刪到一半）不算，不清理。
    /// 只能在啟動時、還沒有任何草稿之前呼叫（之後建立的草稿也長這樣）。
    /// </summary>
    public List<string> FindDraftLeftovers()
    {
        var result = new List<string>();
        if (!Directory.Exists(RootDirectory))
            return result;

        foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
        {
            var id = Path.GetFileName(dir);
            if (!Guid.TryParseExact(id, "N", out _) || File.Exists(Path.Combine(dir, MetaFileName)))
                continue;
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).All(IsDraftEntry))
                    result.Add(id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 讀不到就不動它
            }
        }
        return result;

        static bool IsDraftEntry(string path)
        {
            var name = Path.GetFileName(path);
            if (name.Equals("attachments", StringComparison.OrdinalIgnoreCase))
                return Directory.Exists(path);
            if (name.Equals("workspace", StringComparison.OrdinalIgnoreCase))
                return Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();
            return false;
        }
    }

    /// <summary>
    /// 刪除整個對話資料夾（含附件與預設工作資料夾；代理在 workspace 裡 git init／clone 留下的唯讀物件檔也一起刪）；
    /// 檔案被占用時盡量刪、回傳是否刪乾淨（刪不乾淨時至少刪掉 meta.json，對話不再出現在清單）。
    /// 複本的 id 不刪（它在這台沒有資料夾；同名的資料夾不是複本的），回 true
    /// </summary>
    public bool DeleteConversation(string conversationId)
    {
        if (RefusesReplica(conversationId, "刪資料夾"))
            return true;
        var dir = GetConversationDirectory(conversationId);
        if (DirectoryRemover.TryDelete(dir))
            return true;

        try
        {
            File.Delete(Path.Combine(dir, MetaFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }
}
