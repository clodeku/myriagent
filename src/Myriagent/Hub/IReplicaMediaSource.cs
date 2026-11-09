namespace Myriagent.Hub;

/// <summary>
/// 其他電腦的對話（唯讀複本）在這台顯示時，圖片、影片與縮圖的來源（WP4.7 計畫步 12，D38）：經伺服器的 E9
/// （<c>GET /api/v1/device/media/{key}?purpose=media|thumb[&amp;s=]</c>）向擁有者電腦要位元組。
/// 介面只用 BCL 與中樞的型別（中樞與 AgentWebView 在未配對的路徑上也看得到它，S6）；實作是同步引擎的 <c>ReplicaMediaSource</c>，
/// 經 <see cref="IReplicaMediaSourceProvider"/>（對話發布者）交給中樞。任何執行緒都可以呼叫
/// </summary>
internal interface IReplicaMediaSource
{
    /// <summary>
    /// 取一個媒體或縮圖：內容整份緩衝在記憶體（≤8 MB；原檔超過時只回前 8 MB 的 206）、逾時 20 秒。
    /// 鍵、用途或縮圖大小不合格、沒有存取權杖、伺服器拒絕或連不上、逾時一律回 null（AgentWebView 回 404）
    /// </summary>
    /// <param name="key">媒體鍵 <c>{deviceId}-{ref}</c>（<c>MediaKeyRules</c>）</param>
    /// <param name="purpose"><c>media</c> 或 <c>thumb</c>（<c>MediaPurposes</c>）</param>
    /// <param name="size">縮圖的邊長（160 或 320）；原檔是 null</param>
    /// <param name="rangeHeader">網頁送來的 Range 標頭（只用在原檔；不合格的當成沒帶）</param>
    Task<ReplicaMediaReply?> FetchAsync(string key, string purpose, int? size, string? rangeHeader, CancellationToken ct);
}

/// <summary>交出目前的 <see cref="IReplicaMediaSource"/>（對話發布者實作；中樞以 <c>conversationSink as IReplicaMediaSourceProvider</c> 取，同 IRemoteFileTransferSource）</summary>
internal interface IReplicaMediaSourceProvider
{
    /// <summary>引擎還沒協商出 media1（沒有 E9 可用）或已經停了是 null</summary>
    IReplicaMediaSource? ReplicaMediaSource { get; }
}

/// <summary>
/// <see cref="IReplicaMediaSource"/> 取到的內容（已經檢查過的）：<paramref name="Status"/> 是 200、206 或 416；
/// <paramref name="ContentType"/> 是白名單內的媒體型別（<c>MediaHeaderRules.SafeMediaContentType</c>），不在白名單時是 <c>application/octet-stream</c>；
/// <paramref name="ContentRange"/> 只在 206（<c>bytes start-end/total</c>）與 416（<c>bytes */total</c>）有值
/// </summary>
internal sealed record ReplicaMediaReply(int Status, string ContentType, byte[] Body, string? ContentRange);

/// <summary>
/// 桌面遠端操作的上傳（sync-protocol 14.7：<c>PUT /api/v1/device/uploads/{uploadId}</c>，存取權杖；<see cref="ReplicaMediaSource"/> 實作）：
/// 把這台的一個檔上傳到伺服器，之後以 <c>addUploaded</c> 經 <c>PeerCmd</c> 交給那台電腦。任何執行緒都可以呼叫
/// </summary>
internal interface IPeerUploader
{
    /// <summary>
    /// 上傳 <paramref name="path"/>（呼叫端已確認是本機磁碟上的檔、大小在上限內）。回伺服器的狀態碼與本文（JSON，≤64 KB）；
    /// 沒有存取權杖、連不上是狀態 0、本文 null；取消時丟 <see cref="OperationCanceledException"/>
    /// </summary>
    Task<PeerUploadResult> UploadAsync(string uploadId, string path, CancellationToken ct);

    /// <summary>同 <see cref="UploadAsync(string, string, CancellationToken)"/>，另以 <paramref name="progress"/>（已送出、總長）回報進度（電腦狀態面板的檔案傳輸）</summary>
    Task<PeerUploadResult> UploadAsync(string uploadId, string path, Action<long, long>? progress, CancellationToken ct) => UploadAsync(uploadId, path, ct);
}

/// <summary>上傳的結果：伺服器的狀態碼（0＝沒有送出或連不上）與回應本文（同瀏覽器 E4 的 <c>{uploadId, sha256, size}</c> 或 <c>{error}</c>）</summary>
internal sealed record PeerUploadResult(int Status, string? Json);

/// <summary>
/// 桌面遠端操作的 <c>fileFetch</c>（sync-protocol 14.7：E11 <c>GET /api/v1/device/media/{key}?purpose=file</c>，存取權杖；<see cref="ReplicaMediaSource"/> 實作）。
/// 任何執行緒都可以呼叫
/// </summary>
internal interface IPeerFileSource
{
    /// <summary>整份檔案（≤<see cref="PeerFileRules.MaxBytes"/>）與伺服器給的檔名；鍵不合格、伺服器拒絕、連不上、太大回 null</summary>
    Task<PeerFile?> FetchFileAsync(string key, CancellationToken ct);

    /// <summary>
    /// 電腦狀態面板的檔案傳輸（2026-11-09）：以 E11 分段（每段 ≤8 MB 的 Range）把整個檔寫進 <paramref name="target"/>，沒有大小上限；
    /// 每寫完一段以 <paramref name="progress"/>（已收到、總長）回報。鍵不合格、伺服器拒絕、連不上、檔案在途中變了（ETag 或總長不同）回 null；
    /// 取消時丟 <see cref="OperationCanceledException"/>
    /// </summary>
    Task<PeerDownload?> DownloadAsync(string key, Stream target, Action<long, long>? progress, CancellationToken ct)
        => Task.FromResult<PeerDownload?>(null);
}

/// <summary>取到的檔案：內容與檔名（Content-Disposition 的檔名；沒有是 null）</summary>
internal sealed record PeerFile(byte[] Body, string? FileName);

/// <summary>分段下載完的檔：伺服器給的檔名（沒有是 null）與總長</summary>
internal sealed record PeerDownload(string? FileName, long Total);
