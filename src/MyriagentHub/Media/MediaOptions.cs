namespace MyriagentHub.Media;

/// <summary>
/// 媒體、blob 與上傳的上限（<c>Hub:Media</c>；WP4.7 計畫 D1、D14、D17）：**只有上限參數，沒有任何功能開關**（合併部署就生效）。
/// 啟動（<c>AddHubMedia</c>）與 admin preflight（<c>config.media</c>）都以 <see cref="MediaOptionsRules.Validate"/> 驗證；改了要重啟服務。
/// 容量依設計 3.4（200 GB、80% 告警、95% 拒收）與 Hub 主機的 C: 可用空間下限 40 GB（20 GB 是 ops/status 的 <c>disk_low</c> 告警）
/// </summary>
internal sealed class MediaOptions
{
    /// <summary>blob 的總配額（位元組）</summary>
    public long QuotaBytes { get; set; } = 200L << 30;

    /// <summary>用量到配額的幾 % 告警（ops/status 的 <c>blob_quota</c>）</summary>
    public int AlertPercent { get; set; } = 80;

    /// <summary>用量到配額的幾 % 起新的 blob 一律拒收（507；事件與對話照收）</summary>
    public int RefusePercent { get; set; } = 95;

    /// <summary>資料根所在磁碟區的可用空間低於這個值時新的 blob 一律拒收（507）：系統磁碟滿了會拖垮整台主機</summary>
    public long MinFreeBytes { get; set; } = 40L << 30;

    /// <summary>項目欄位 blob 的上限（D14：渲染器讀一個 blob 約要 6 倍記憶體，GC 硬上限 512 MB）；不能超過型別上限 64 MB</summary>
    public long MaxItemBlobBytes { get; set; } = 4L << 20;

    /// <summary>縮圖 blob 的上限</summary>
    public long MaxThumbBytes { get; set; } = 512L << 10;

    /// <summary>瀏覽器上傳單一檔的上限</summary>
    public long MaxUploadBytes { get; set; } = 512L << 20;

    /// <summary>待處理（還沒成為附件）的上傳合計上限</summary>
    public long MaxPendingUploadBytes { get; set; } = 4L << 30;

    /// <summary>伺服器縮圖快取（<c>cache\thumbs\</c>，LRU）的上限</summary>
    public long ThumbCacheBytes { get; set; } = 2L << 30;

    /// <summary>媒體轉送等來源電腦第一個位元組的秒數，逾時回 504（設計 4.10:1057）</summary>
    public int FirstByteTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// 所有來源電腦合計同時的媒體轉送上限（每台另有 <c>MediaRelayRules.MaxConcurrentPerDevice</c>＝20），滿了和每台的上限一樣排隊（<c>MediaRelay.QueueWait</c>，名額空出來依到達的順序給），等滿了才回 503 <c>busy</c>。
    /// 容量上限，不是限流：瀏覽器讀得慢時一個轉送佔約 2 MB（轉送的管線 1 MB＋Kestrel 給裝置 PUT 連線的輸入緩衝最多 1 MB），
    /// 10 台 × 20 個就超過記憶體保險絲的告警線 400 MB、逼近 GC 的 512 MB 硬上限（WP4.9 步 12 之後的實測）
    /// </summary>
    public int MaxConcurrentRelays { get; set; } = 64;
}
