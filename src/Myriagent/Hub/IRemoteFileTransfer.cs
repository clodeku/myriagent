namespace Myriagent.Hub;

/// <summary>
/// 中樞的遠端檔案指令要同步那一側做的事（WP4.7 計畫步 11，D22、D33、D36）：抽屜與 fileFetch 的媒體參照、瀏覽器上傳的檔的下載。
/// 介面只用中樞與 BCL 的型別（中樞在未配對的路徑上也看得到它，S6）；實作是同步引擎的 <c>RemoteFileTransfer</c>，
/// 經 <see cref="IRemoteFileTransferSource"/>（對話發布者）交給中樞。任何執行緒都可以呼叫
/// </summary>
internal interface IRemoteFileTransfer
{
    /// <summary>
    /// 排進引擎執行緒、依序寫 <c>media_refs_local</c>（先於輸出送出）；回這次發或重用的參照（和 <paramref name="fullPaths"/> 同順序）。
    /// <paramref name="conversationId"/> 是線上的對話 id；<paramref name="kind"/> 是 <c>@list</c>（抽屜，29 天內的同一個檔重用）或 <c>@file</c>（fileFetch，每次新發）。
    /// 同步資料庫還沒準備好、引擎停了丟 <see cref="InvalidOperationException"/>
    /// </summary>
    Task<IReadOnlyList<string>> RegisterRefsAsync(string conversationId, string kind /* "@list"｜"@file" */, IReadOnlyList<string> fullPaths, CancellationToken ct);

    /// <summary>
    /// 以裝置的 blob 端點（E6）下載瀏覽器上傳的檔、驗大小與 SHA-256；寫到 <paramref name="destinationFile"/>（呼叫端保證在 <c>cache\uploads\{uploadId}\</c> 底下、檔案還不存在）。
    /// 內容不符丟 <see cref="InvalidDataException"/>（寫了一半的檔刪掉），連不上或伺服器拒絕丟 <see cref="IOException"/>／<see cref="HttpRequestException"/>
    /// </summary>
    Task DownloadUploadAsync(RemoteUpload upload, string destinationFile, CancellationToken ct);

    /// <summary>已配對、這條連線協商出 media1、手上有存取權杖（cha_）</summary>
    bool CanTransfer { get; }
}

/// <summary>交出目前的 <see cref="IRemoteFileTransfer"/>（對話發布者實作；中樞以 <c>conversationSink is IRemoteFileTransferSource</c> 取，同 IReplicaRequester 的模式）</summary>
internal interface IRemoteFileTransferSource
{
    /// <summary>引擎還沒建（還沒證明過連線）或已經停了是 null</summary>
    IRemoteFileTransfer? RemoteFileTransfer { get; }
}
