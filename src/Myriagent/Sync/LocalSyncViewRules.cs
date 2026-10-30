using System.Globalization;
using Myriagent.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 桌面同步區與副本的網頁檢視（WP4.6 計畫步 2；D22、D25、D27、D44）：遠端操作紀錄的請求解析、對話的同步政策選單、
/// 副本的清單項目與標頭欄位、手動匯出紀錄的路徑（步 2）；紀錄的一頁、回收區清單與匯出結果的網頁訊息（步 9，用 WP4.4 的
/// <see cref="RemoteAuditEntry"/>、<see cref="TrashEntry"/>）。純函式；協調者與中樞在步 9 接上
/// </summary>
internal static class LocalSyncViewRules
{
    /// <summary><c>syncLoadRemoteAudit</c> 沒帶 <c>limit</c> 時一頁幾筆（web-protocol 的 remoteAudit）</summary>
    public const int AuditDefaultLimit = 50;

    /// <summary><c>limit</c> 的上限（同 sync.db 一次最多讀的筆數）</summary>
    public const int AuditMaxLimit = 200;

    /// <summary>
    /// <c>syncLoadRemoteAudit {before?, beforeSeq?, limit?}</c>（D44）：<c>before</c>、<c>beforeSeq</c> 是 0 以上的整數或省略（null 當成省略），
    /// <c>beforeSeq</c> 只能和 <c>before</c> 一起；<c>limit</c> 是 1～<see cref="AuditMaxLimit"/> 的整數，省略是 <see cref="AuditDefaultLimit"/>。
    /// 任何一項不合格回 null（整個指令忽略，不猜）
    /// </summary>
    public static (long? Before, long? BeforeSeq, int Limit)? ParseAuditRequest(JObject msg)
    {
        ArgumentNullException.ThrowIfNull(msg);
        if (!TryOptionalCount(msg["before"], out var before) || !TryOptionalCount(msg["beforeSeq"], out var beforeSeq))
            return null;
        if (beforeSeq is not null && before is null)
            return null;
        if (!TryOptionalCount(msg["limit"], out var limit))
            return null;
        if (limit is null)
            return (before, beforeSeq, AuditDefaultLimit);
        if (limit < 1 || limit > AuditMaxLimit)
            return null;
        return (before, beforeSeq, (int)limit.Value);
    }

    /// <summary>
    /// 標頭的 <c>conversationSync {policy, choices, serverDeleted}</c>（D25）：可同步而且伺服器沒刪除時 choices 是 full、metadata、localOnly；
    /// 伺服器已刪除或這種代理不能同步時只有 localOnly。<paramref name="policy"/> 原樣（含權杖 DLP 設的 paused）
    /// </summary>
    public static JObject ConversationSyncView(string policy, bool syncable, bool serverDeleted)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var choices = syncable && !serverDeleted
            ? new JArray(ConversationSyncPolicies.Full, ConversationSyncPolicies.Metadata, ConversationSyncPolicies.LocalOnly)
            : new JArray(ConversationSyncPolicies.LocalOnly);
        return new JObject
        {
            ["policy"] = policy,
            ["choices"] = choices,
            ["serverDeleted"] = serverDeleted,
        };
    }

    /// <summary>
    /// 副本標頭的 <c>replica {running, waiting, itemsAvailable, accountId}</c>（擁有者那邊的狀態，只供顯示）：<c>accountId</c> 是擁有者那台執行這個對話的
    /// 帳戶連結到的邏輯帳戶（la_…；帳戶晶片與換帳戶選單用，2026-10-25 起），不明（沒有連結、目錄還沒到、擁有者沒送）時 null
    /// </summary>
    public static JObject ReplicaHeaderView(bool running, bool waiting, bool itemsAvailable, string? accountId = null) => new()
    {
        ["running"] = running,
        ["waiting"] = waiting,
        ["itemsAvailable"] = itemsAvailable,
        ["accountId"] = Text(accountId),
    };

    /// <summary>
    /// 副本的清單項目（D22）：加 <c>deviceId</c>（擁有者電腦），<c>running</c>、<c>waiting</c> 換成擁有者那邊的，
    /// <c>customFolder</c>、<c>workingDirectory</c> 換成擁有者那邊的（<see cref="ApplyReplicaFolder"/>）；其他鍵不動。
    /// 名稱與在線不填（中樞看不到目錄），網頁從 <c>remote.devices</c> 查
    /// </summary>
    public static void ApplyReplicaSummary(JObject summary, string? ownerDeviceId, bool running, bool waiting, bool customFolder, string? folder)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ApplyReplicaFolder(summary, customFolder, folder);
        summary["deviceId"] = Text(ownerDeviceId);
        summary["running"] = running;
        summary["waiting"] = waiting;
    }

    /// <summary>
    /// 副本的標頭（D22）：加 <c>deviceId</c> 與 <c>replica</c>；<c>accountMissing</c> 改成 false（副本的 AccountId 是空字串，不是帳戶被移除）、
    /// <c>recentFolders</c> 與 <c>commands</c> 改成空陣列（不是這台的帳戶與資料夾）、<c>customFolder</c>、<c>workingDirectory</c> 換成擁有者那邊的
    /// （<see cref="ApplyReplicaFolder"/>）；<c>running</c> 照舊是這台的（副本沒有程序）、<c>accountId</c> 照舊是空字串（擁有者那台的帳戶在
    /// <c>replica.accountId</c>，<paramref name="accountId"/> 是它的邏輯帳戶），其他鍵不動
    /// </summary>
    public static void ApplyReplicaHeader(JObject header, string? ownerDeviceId, bool running, bool waiting, bool itemsAvailable, bool customFolder, string? folder,
        string? accountId = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        ApplyReplicaFolder(header, customFolder, folder);
        header["deviceId"] = Text(ownerDeviceId);
        header["replica"] = ReplicaHeaderView(running, waiting, itemsAvailable, accountId);
        header["accountMissing"] = false;
        header["recentFolders"] = new JArray();
        header["commands"] = new JArray();
    }

    /// <summary>
    /// 副本的工作資料夾（桌面遠端操作：從這台換那台的資料夾之後要看得到結果，同瀏覽器版的標頭）：<c>customFolder</c> 照擁有者發布的，
    /// <c>workingDirectory</c> 只有自訂資料夾才帶擁有者電腦上的路徑（只供顯示），預設 workspace 是 null。複本的 Meta.WorkingDirectory 一律是 null，
    /// 所以中樞算出的兩個鍵是「不是自訂、沒有路徑」，這裡就地改值（鍵的位置不變）
    /// </summary>
    private static void ApplyReplicaFolder(JObject view, bool customFolder, string? folder)
    {
        var path = customFolder && !string.IsNullOrWhiteSpace(folder) ? folder : null;
        view["workingDirectory"] = path is null ? JValue.CreateNull() : path;
        view["customFolder"] = customFolder;
    }

    /// <summary>
    /// 設定頁「匯出」遠端操作紀錄的檔案（D28）：和解除配對時的匯出檔同一個資料夾，檔名加 <c>-yyyyMMdd-HHmmss</c>（<paramref name="now"/> 自己的時區），
    /// 所以不會蓋掉解除配對時寫的那一份
    /// </summary>
    public static string ManualAuditExportPath(string unpairExportPath, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(unpairExportPath);
        var directory = Path.GetDirectoryName(unpairExportPath);
        var name = Path.GetFileNameWithoutExtension(unpairExportPath) + "-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + Path.GetExtension(unpairExportPath);
        return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
    }

    /// <summary>
    /// <c>remoteAudit</c> 的一筆（D44；鍵照 web-protocol 的 entries 再加 <c>seq</c>，網頁以 <c>(at, seq)</c> 翻頁）：<c>tier</c> 是閘門算的最終層級，
    /// 還沒完成或在閘門之前就拒絕時用信封宣告的；<c>issuer</c> 可以是 null（標籤不合格，網頁顯示替代文字）；<c>state</c> 原樣（含 <c>executing</c>）
    /// </summary>
    public static JObject AuditEntryView(RemoteAuditEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new JObject
        {
            ["seq"] = e.Seq,
            ["at"] = e.At,
            ["commandId"] = Text(e.CommandId),
            ["cmd"] = Text(e.Cmd),
            ["conversationId"] = Text(e.ConversationId),
            ["issuer"] = Text(e.IssuerLabel),
            ["tier"] = e.Tier ?? e.DeclaredTier,
            ["fullAccess"] = e.FullAccess,
            ["policy"] = Text(e.Policy),
            ["state"] = Text(e.State),
            ["code"] = Text(e.Code),
            ["defaultPassword"] = e.DefaultPassword,
            ["count"] = e.Count,
        };
    }

    /// <summary>
    /// 回給網頁的一頁紀錄 <c>{type:"remoteAudit", entries, hasMore}</c>：<paramref name="rows"/> 是多讀一筆的結果（最多 <paramref name="limit"/>＋1 筆、
    /// 由新到舊），只送前 <paramref name="limit"/> 筆，多出來的那一筆只用來判斷 <c>hasMore</c>
    /// </summary>
    public static JObject AuditPage(IReadOnlyList<RemoteAuditEntry> rows, int limit)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return new JObject
        {
            ["type"] = "remoteAudit",
            ["entries"] = new JArray(rows.Take(limit).Select(AuditEntryView)),
            ["hasMore"] = rows.Count > limit,
        };
    }

    /// <summary>
    /// 回收區清單 <c>{type:"trash", entries:[{conversationId, title, trashedAt, issuer, commandId, expiresAt}]}</c>（D27；順序照
    /// <c>ConversationTrash.List</c>，新到舊）：<c>expiresAt</c>＝搬進回收區的時間加保留期（剛好到期還留著，同 <see cref="ConversationTrashRules.IsExpired"/>）
    /// </summary>
    public static JObject TrashView(IReadOnlyList<TrashEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return new JObject
        {
            ["type"] = "trash",
            ["entries"] = new JArray(entries.Select(e => new JObject
            {
                ["conversationId"] = Text(e.ConversationId),
                ["title"] = Text(e.Info.Title),
                ["trashedAt"] = e.Info.TrashedAt,
                ["issuer"] = Text(e.Info.IssuerLabel),
                ["commandId"] = Text(e.Info.CommandId),
                ["expiresAt"] = e.Info.TrashedAt + ConversationTrashRules.RetentionMs,
            })),
        };
    }

    /// <summary>手動匯出紀錄的結果 <c>{type:"remoteAuditExport", path, count, error}</c>（D28）：成功時 error 是 null；失敗時 path 是 null、count 是 0</summary>
    public static JObject AuditExportView(string? path, int count, string? error) => new()
    {
        ["type"] = "remoteAuditExport",
        ["path"] = Text(error is null ? path : null),
        ["count"] = error is null ? count : 0,
        ["error"] = Text(error),
    };

    /// <summary>可以是 null 的字串：null 輸出成 JSON null（Newtonsoft 從 null 字串隱含轉換出來的是型別 String、值 null 的 JValue）</summary>
    private static JToken Text(string? value) => value is null ? JValue.CreateNull() : new JValue(value);

    /// <summary>省略或 null 回 true（值是 null）；0 以上、放得進 long 的 JSON 整數回 true；其他（字串、小數、布林、負數、太大）回 false</summary>
    private static bool TryOptionalCount(JToken? token, out long? value)
    {
        value = null;
        if (token is null || token.Type == JTokenType.Null)
            return true;
        if (token is not JValue { Type: JTokenType.Integer } integer)
            return false;
        long number;
        switch (integer.Value)
        {
            case long l:
                number = l;
                break;
            case int i:
                number = i;
                break;
            default:
                return false;
        }
        if (number < 0)
            return false;
        value = number;
        return true;
    }
}
