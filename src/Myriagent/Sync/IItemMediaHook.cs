using Myriagent.Agents;

namespace Myriagent.Sync;

/// <summary>
/// 投影器的媒體接縫（WP4.7 計畫 D4、D31、6.3 的 WP4.3 契約點；引擎執行緒）：<see cref="ConversationProjector"/> 在算完來源摘要之後配參照、
/// 送 <c>conv.itemRemoved</c>／<c>conv.deleted</c> 時刪列；重整以 <see cref="RefsMissing"/> 讓摘要相同、但缺參照的項目也重新投影；
/// 截斷之前以 <see cref="UseBlobs"/>、<see cref="EnsureUploaded"/> 決定大欄位改送 blob 還是照 WP4.3 截斷
/// </summary>
internal interface IItemMediaHook
{
    /// <summary>
    /// 回填好 <see cref="AgentChatItem.MediaRefs"/> 的複本（沒有可顯示的媒體、或 <c>media_refs_local</c> 用不了時回原物件）；
    /// 新參照先寫 <c>media_refs_local</c>（之後才寫 outbox：寫 outbox 失敗時，重試沿用同一個參照）。<paramref name="snapshot"/> 只讀、不改
    /// </summary>
    AgentChatItem AssignRefs(string wireId, AgentChatItem snapshot);

    /// <summary>項目有可顯示的媒體、但 <c>media_refs_local</c> 缺列</summary>
    bool RefsMissing(string wireId, AgentChatItem item);

    /// <summary>
    /// 刪這個項目（<paramref name="itemId"/> 是 null 時整個對話）的列：<c>conv.itemRemoved</c> 依項目、<c>conv.deleted</c>（刪除、改成 localOnly）依對話（契約:700）。
    /// metadata、paused 不刪（請求時由 D26 擋）
    /// </summary>
    void Forget(string wireId, string? itemId);

    /// <summary>
    /// 這一筆要不要先試 blob 分支（步 10，D31）：協商出 media1（或離線但上一條連線有）、項目不是 Running、這個項目在本次執行沒被伺服器拒收過。
    /// false＝照 WP4.3 截斷
    /// </summary>
    bool UseBlobs(string wireId, AgentChatItem snapshot);

    /// <summary>
    /// 這些 blob（遮蔽之後的內容）都已確認在伺服器上回 true（沒有 blob 也是 true）；否則排背景上傳（完成後重新走這個對話、投影最新的快照）回 false，
    /// 投影器這次照 WP4.3 送截斷版
    /// </summary>
    bool EnsureUploaded(string wireId, string itemId, IReadOnlyList<ConversationBlob> blobs);
}

/// <summary>項目欄位 blob 的引擎那一側（WP4.7 計畫步 10；<see cref="MediaRefIssuer"/> 轉給它，實作在 SyncEngine.MediaBlobs.cs；引擎執行緒）</summary>
internal interface IItemBlobUploads
{
    /// <inheritdoc cref="IItemMediaHook.UseBlobs"/>
    bool UseBlobs(string wireId, AgentChatItem snapshot);

    /// <inheritdoc cref="IItemMediaHook.EnsureUploaded"/>
    bool EnsureUploaded(string wireId, string itemId, IReadOnlyList<ConversationBlob> blobs);
}
