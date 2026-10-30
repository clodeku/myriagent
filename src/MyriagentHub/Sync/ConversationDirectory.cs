namespace MyriagentHub.Sync;

/// <summary>對話的擁有者與世代（WP4.2 物化的 <c>conversations</c> 表；sync-protocol 14.2）</summary>
/// <param name="ConversationId">線上的對話 id（32 個小寫十六進位）</param>
/// <param name="OwnerDeviceId">擁有者裝置：只有它能寫這個對話的事件、遠端指令轉給它（路由 Owner）</param>
/// <param name="Epoch">目前的世代（信封的 <c>epoch</c>；第 4 階段沒有租約，是擁有者的 <c>LeaseEpoch</c> 或 1）</param>
/// <param name="SyncPolicy">對話的同步政策（<c>full</c>／<c>metadata</c>／<c>paused</c>，<c>ConversationSyncPolicies</c>）</param>
internal sealed record ConversationOwnership(string ConversationId, string OwnerDeviceId, long Epoch, string SyncPolicy);

/// <summary>
/// 伺服器的對話目錄（WP4.1 D50、6.2）：WP4.2 以物化的 <c>conversations</c> 表實作；WP4.5 的指令路由以它找擁有者與世代，
/// 裝置 hub 的第 4 階段方法以方法注入取得（不加長 DeviceHub 的主建構式）。WP4.1 只有 <see cref="EmptyConversationDirectory"/>。
/// 在任何執行緒呼叫，要很快（讀快取或唯讀交易），不可阻塞寫入者
/// </summary>
internal interface IConversationDirectory
{
    /// <summary>這個對話的擁有者與世代；不存在、已刪除或 id 不合格回 null</summary>
    ConversationOwnership? Find(string conversationId);
}

/// <summary>WP4.1 的預設（<c>AddHubSync</c> 登記）：一個對話都沒有</summary>
internal sealed class EmptyConversationDirectory : IConversationDirectory
{
    public ConversationOwnership? Find(string conversationId) => null;
}

/// <summary>
/// 內容定址的 blob（設計 3.4；sync-protocol 14.2 的項目欄位 blob、14.3 的縮圖、上傳）：WP4.7 以 <c>blobs\</c> 資料夾與 sync.db 的
/// <c>blobs</c>／<c>blob_refs</c> 實作，裝置經 <c>/api/v1/device/blobs</c>（<c>DeviceAccess</c> 政策，只收 <c>cha_</c>，D56）存取。
/// WP4.1 只有 <see cref="EmptyBlobStore"/>。<c>sha256</c> 一律是 64 個小寫十六進位
/// </summary>
internal interface IBlobStore
{
    bool Exists(string sha256);

    /// <summary>開啟讀取（呼叫端負責關閉）；沒有回 null</summary>
    Stream? OpenRead(string sha256);

    /// <summary>
    /// 存入一份內容：內容的 SHA-256 與長度要等於參數（不符丟 <see cref="InvalidDataException"/>、什麼都不留）；已存在回 false（不重寫）。
    /// <paramref name="kind"/> 是 <c>blobs.kind</c>（upload、thumb、attachment、item-body…）
    /// </summary>
    Task<bool> PutAsync(string sha256, long length, string kind, string? contentType, Stream content, CancellationToken cancellationToken);
}

/// <summary>WP4.1 的預設（<c>AddHubSync</c> 登記）：什麼都沒有，存入一律丟 <see cref="NotSupportedException"/>（WP4.1 沒有任何呼叫端）</summary>
internal sealed class EmptyBlobStore : IBlobStore
{
    public bool Exists(string sha256) => false;

    public Stream? OpenRead(string sha256) => null;

    public Task<bool> PutAsync(string sha256, long length, string kind, string? contentType, Stream content, CancellationToken cancellationToken) =>
        throw new NotSupportedException("伺服器的 blob 儲存在 WP4.7 才有");
}
