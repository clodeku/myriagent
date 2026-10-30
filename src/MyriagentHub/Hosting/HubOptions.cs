namespace MyriagentHub.Hosting;

/// <summary>
/// 伺服器設定：appsettings 的 <c>Hub</c> 區段。正式環境由資料根的 <c>config\appsettings.Production.json</c> 提供
/// （在發佈資料夾外，換版不會被覆蓋；9.1）。所有時間長度一律用明確單位的整數，避免 TimeSpan 字串格式的歧義。
/// </summary>
internal sealed class HubOptions
{
    public const string Section = "Hub";

    /// <summary>對外網域（憑證、ACME、對外網址；明確設定、不從 Host 標頭推算）</summary>
    public string ServerDomain { get; set; } = "hub.example.com";

    /// <summary>瀏覽器看到的來源（Origin／CSRF 檢查、CSP、配對網址）；空白時由 ServerDomain 與 HttpsPort 組成</summary>
    public string? PublicOrigin { get; set; }

    /// <summary>HTTPS 連接埠：正式 443；開發與測試用高埠（0＝由系統挑選，只能搭配明確 IP）</summary>
    public int HttpsPort { get; set; } = 443;

    /// <summary>any＝[::] 雙堆疊（只聽 127.0.0.1 會讓 localhost 每條連線慢 2 秒）；localhost＝127.0.0.1＋::1；其他＝指定 IP</summary>
    public string BindAddress { get; set; } = "any";

    /// <summary>啟動後把程序優先權降到 BelowNormal（不和同機的正式 SQL Server 搶 CPU）</summary>
    public bool BelowNormalPriority { get; set; } = true;

    public SessionSettings Session { get; set; } = new();

    public RateLimitSettings RateLimits { get; set; } = new();

    public TlsSettings Tls { get; set; } = new();

    public AcmeSettings Acme { get; set; } = new();

    public BackupSettings Backup { get; set; } = new();

    public SelfTestSettings SelfTest { get; set; } = new();

    public MemoryFuseSettings MemoryFuse { get; set; } = new();

    public IdleTrimSettings IdleTrim { get; set; } = new();

    public FileLogSettings FileLog { get; set; } = new();

    public AdminInboxSettings AdminInbox { get; set; } = new();

    public DeploySettings Deploy { get; set; } = new();

    public SyncSettings Sync { get; set; } = new();

    public ReleaseSettings Releases { get; set; } = new();

    public BrowserSettings Browser { get; set; } = new();

    public ConversationSettings Conversations { get; set; } = new();

    /// <summary>媒體、blob 與上傳的上限（WP4.7；只有上限、沒有開關）</summary>
    public Media.MediaOptions Media { get; set; } = new();

    /// <summary>第 5 階段：逐字稿與檔案日誌（<c>log1</c>；預設休眠）</summary>
    public Logs.LogOptions Logs { get; set; } = new();

    /// <summary>第 5 階段：租約、交接與強制接手（<c>hand1</c>；預設休眠）</summary>
    public Handoff.HandoffOptions Handoff { get; set; } = new();
}

/// <summary>瀏覽器工作階段（8.2.2）</summary>
internal sealed class SessionSettings
{
    /// <summary>閒置逾時（滑動）</summary>
    public int IdleHours { get; set; } = 12;

    /// <summary>絕對期限（原型的 30 天對遠端執行入口太長）</summary>
    public int AbsoluteDays { get; set; } = 7;

    /// <summary>敏感操作要求最近幾分鐘內輸入過密碼（登入或再驗證，8.2.3）</summary>
    public int RecentAuthMinutes { get; set; } = 5;

    /// <summary>協作人的閒置逾時（滑動，天；擁有者 2026-10-30 選「30 天沒用才要重登」）</summary>
    public int CollaboratorIdleDays { get; set; } = 30;

    /// <summary>協作人的絕對期限（天）：再常用也要重登一次；cookie 本身也是這麼久</summary>
    public int CollaboratorAbsoluteDays { get; set; } = 180;
}

/// <summary>分區速率限制（8.5）：匿名端點以 IP（IPv6 以 /64）分區；已認證流量不受 IP 限制</summary>
internal sealed class RateLimitSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>未認證請求總量（每 IP 每分鐘）</summary>
    public int AnonymousPerMinute { get; set; } = 120;

    /// <summary>/api/v1/auth/login（每 IP 每分鐘）</summary>
    public int LoginPerMinute { get; set; } = 10;

    /// <summary>/api/v1/auth/login（每 IP 每小時）；已登入後的密碼再驗證與改密碼以工作階段分區，用同一個上限</summary>
    public int LoginPerHour { get; set; } = 50;

    /// <summary>連續失敗幾次後該分區（IP 或工作階段）的密碼驗證退避</summary>
    public int FailuresBeforeBackoff { get; set; } = 20;

    public int BackoffMinutes { get; set; } = 15;

    /// <summary>/api/v1/pair/start（每 IP 每小時；第 3 階段的端點掛上這個政策）</summary>
    public int PairStartPerHour { get; set; } = 5;

    /// <summary>全域登入失敗告警門檻（1 小時內）</summary>
    public int GlobalFailureAlertPerHour { get; set; } = 20;

    /// <summary>瀏覽器 hub 的 negotiate 與 WebSocket 升級：未登入每 IP 每分鐘（設計 8.5）</summary>
    public int BrowserHubAnonymousPerMinute { get; set; } = 30;

    /// <summary>瀏覽器 hub 的 negotiate 與 WebSocket 升級：已登入每個工作階段每分鐘（設計 14.6）</summary>
    public int BrowserHubPerSessionPerMinute { get; set; } = 60;

    /// <summary>瀏覽器 hub 的 Cmd 與 ClientState 合計：每個工作階段每秒補幾次（設計 8.5）</summary>
    public int BrowserCmdPerSecond { get; set; } = 20;

    /// <summary>瀏覽器 hub 的 Cmd 與 ClientState 合計：每個工作階段的突發上限</summary>
    public int BrowserCmdBurst { get; set; } = 50;
}

/// <summary>TLS 憑證存放</summary>
internal sealed class TlsSettings
{
    /// <summary>
    /// store＝匯入服務身分的 CurrentUser\My 一次、之後以指紋載入（正式環境預設，不會每次啟動留下孤兒金鑰檔）；
    /// file＝PFX 以 DPAPI 加密存在 secrets\tls\、每次啟動匯入記憶體（開發與測試預設，不碰使用者的憑證存放區）。空白＝依環境決定。
    /// </summary>
    public string? CertStore { get; set; }

    /// <summary>自簽憑證效期（還沒有正式憑證時先用）</summary>
    public int SelfSignedDays { get; set; } = 90;
}

/// <summary>程式內 ACME（Certes，TLS-ALPN-01 走同一個 443；9.6）</summary>
internal sealed class AcmeSettings
{
    public bool Enabled { get; set; }

    public string Directory { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>先打 staging 確認外部真的連得到 443，成功才切正式目錄</summary>
    public string StagingDirectory { get; set; } = "https://acme-staging-v02.api.letsencrypt.org/directory";

    /// <summary>略過 staging（例如本機 Pebble 端到端時兩者是同一個目錄）</summary>
    public bool SkipStaging { get; set; }

    /// <summary>ACME 帳戶聯絡信箱（可空白）</summary>
    public string? Email { get; set; }

    /// <summary>憑證設定檔（目錄有公告才使用，並帶 ARI 的 replaces）；空白＝不指定</summary>
    public string? Profile { get; set; } = "classic";

    /// <summary>
    /// 只信任這個 PEM 裡的根憑證來連 ACME 目錄（本機 Pebble 端到端用）；空白＝系統信任清單。
    /// 正式組建沒有「接受任何憑證」的選項。
    /// </summary>
    public string? DirectoryTrustPem { get; set; }

    public int CheckIntervalHours { get; set; } = 12;
}

/// <summary>每日備份（9.9）</summary>
internal sealed class BackupSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>本機時間 HH:mm（避開白天的使用時段）</summary>
    public string DailyAt { get; set; } = "03:30";

    /// <summary>
    /// 留幾份每日備份（2026-10-25 從 14 改成 7）：每一份都是 hub.db＋sync.db 的完整複本，sync.db 一直在長（對話事件），
    /// 份數乘上去才是備份真正占的空間
    /// </summary>
    public int KeepDaily { get; set; } = 7;

    /// <summary>更舊的每個 ISO 週留一份，最多幾週（2026-10-25 從 8 改成 4）</summary>
    public int KeepWeekly { get; set; } = 4;

    /// <summary>
    /// 每日與手動備份（<c>backups\daily\</c>）合計的上限（位元組）：超過就從最舊的刪，最新的一份一定留（<see cref="Ops.BackupRules.SelectOverBudget"/>）。
    /// 部署前的複本（<c>predeploy-*</c>）由安裝腳本只留 2 份，不算在這裡
    /// </summary>
    public long MaxTotalBytes { get; set; } = 100L << 30;

    /// <summary>
    /// 備份之後資料根所在磁碟區至少要剩的空間（同 blob 的 <c>Hub:Media:MinFreeBytes</c>：系統磁碟滿了會拖垮整台主機）。
    /// 不夠時先刪最舊的備份騰出空間（最新的一份一定留），還是不夠就不備份（<c>backup_stale</c> 告警）
    /// </summary>
    public long MinFreeBytes { get; set; } = 40L << 30;
}

/// <summary>唯讀隔離自檢（9.3）</summary>
internal sealed class SelfTestSettings
{
    /// <summary>空白＝只有以 Windows 服務執行時啟用（開發機上以管理員跑時會真的寫得進 C:\，結果沒有意義）</summary>
    public bool? Enabled { get; set; }

    /// <summary>必須寫不進的資料夾；空白＝預設清單（C:\、Windows、Program Files、程式所在資料夾）</summary>
    public List<string>? MustNotWrite { get; set; }

    /// <summary>
    /// 必須列舉不到的資料夾（另外一律加上登錄檔裡的 SQL Server 資料、記錄與備份資料夾）；空白＝沒有其他的。
    /// C:\inetpub\wwwroot 不在預設清單：IIS 預設站台的公開內容，預設 Users 可讀。
    /// </summary>
    public List<string>? MustNotList { get; set; }

    /// <summary>必須開不了的檔案；空白＝預設清單（IIS 的 applicationHost.config）</summary>
    public List<string>? MustNotReadFiles { get; set; }

    /// <summary>是否從登錄檔加入 SQL Server 的資料與備份資料夾（只讀登錄檔、只嘗試列舉）</summary>
    public bool IncludeSqlServerFolders { get; set; } = true;

    public int IntervalHours { get; set; } = 24;
}

/// <summary>記憶體保險絲（9.10）</summary>
internal sealed class MemoryFuseSettings
{
    public bool Enabled { get; set; } = true;

    public int WarnMb { get; set; } = 400;

    public int KillMb { get; set; } = 700;

    public int KillAfterMinutes { get; set; } = 5;

    public int SampleSeconds { get; set; } = 30;
}

/// <summary>閒置修剪（S9 的閒置記憶體，設計 14.20；<c>Ops/IdleTrimRules.cs</c>）</summary>
internal sealed class IdleTrimSettings
{
    /// <summary>靜置之後釋放 DLP 樣式的快取並積極回收；測試主機關掉（同一個測試程序裡平行的主機共用 GC 與 DLP 樣式）</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>diag\myriagenthub-YYYYMMDD.log（JSON 行）</summary>
internal sealed class FileLogSettings
{
    public bool Enabled { get; set; } = true;

    public int MaxFileMb { get; set; } = 50;

    public int RetainDays { get; set; } = 14;
}

/// <summary>管理收件匣（只有本機管理員能寫入；沒有任何網路管理端點）</summary>
internal sealed class AdminInboxSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>檔案監看之外的保險輪詢</summary>
    public int PollSeconds { get; set; } = 10;
}

/// <summary>部署腳本與服務的介面（docs/hub-setup.md 第 11 節）</summary>
internal sealed class DeploySettings
{
    /// <summary>
    /// 部署收件匣（admin-inbox\deploy\）的 drain 最多維持幾秒：到期時服務仍在執行（不在停止中）就自動解除。
    /// 範圍 60～3600（啟動與 preflight 驗證）；要遠大於安裝腳本 drain 之後到停好服務的預算（DrainWaitSec 10＋StopTimeoutSec 30）。
    /// 管理收件匣根目錄（擁有者）的 drain 沒有期限。
    /// </summary>
    public int DrainMaxSeconds { get; set; } = Ops.DrainRules.DefaultDeployDrainMaxSeconds;
}

/// <summary>裝置同步（sync-protocol）</summary>
internal sealed class SyncSettings
{
    /// <summary>可以連線的最低桌面 app 版本（HelloResponse 的 minAppVersion；更舊的回 protocol、桌面提示更新）</summary>
    public string MinAppVersion { get; set; } = "0.1.0";
}

/// <summary>
/// 桌面版發佈（U2；規格 docs/plans/2026-10-16-app-self-update.md 第 7 章）。服務啟動與 admin preflight 以 AppReleaseRules.ValidateSettings 驗證。
/// 沒有任何信任或撤銷發佈金鑰的設定：伺服器收件只認組件內建的正式發佈金鑰（經 Releases/IBuiltInReleaseKeys 取得，U2 D1）
/// </summary>
internal sealed class ReleaseSettings
{
    /// <summary>收件匣（admin-inbox\app-release\）檔案監看之外的保險輪詢（1～3600 秒）</summary>
    public int InboxPollSeconds { get; set; } = 30;

    /// <summary>磁碟上保留幾個可用版本（1～20；規格 §7.1 是 3）：撤回與清除的版本不佔名額</summary>
    public int KeepAvailable { get; set; } = 3;

    /// <summary>zip 大小上限（1～4096 MiB）：讀檔與複製之前先比</summary>
    public int MaxZipMb { get; set; } = 256;

    /// <summary>複製完至少要留下的磁碟空間（≥ 0 MiB；營運告警的門檻是 20 GiB）</summary>
    public int MinFreeDiskMb { get; set; } = 2048;
}

/// <summary>瀏覽器閘道（WP3.6；設計 14.6、web-protocol 第六節）</summary>
internal sealed class BrowserSettings
{
    /// <summary>每個工作階段最多幾條瀏覽器 hub 連線（分頁、彈出視窗）</summary>
    public int MaxConnectionsPerSession { get; set; } = 8;

    /// <summary>全部最多幾條瀏覽器 hub 連線（算在 Kestrel 的升級連線上限 256 裡）</summary>
    public int MaxConnections { get; set; } = 32;

    /// <summary>工作階段巡檢（閒置與絕對期限、續期）的間隔（真實時間）</summary>
    public int SessionSweepSeconds { get; set; } = 30;

    /// <summary>推送的節流：最多每幾毫秒讀一次快照（設計 4.9.2）</summary>
    public int PushIntervalMs { get; set; } = 500;

    /// <summary>沒有任何變動通知時，最久多少秒保底重讀一次快照</summary>
    public int RefreshSeconds { get; set; } = 15;

    /// <summary>一則訊息送出逾時就中斷那條連線（卡住的用戶端不拖累其他連線）</summary>
    public int SendTimeoutSeconds { get; set; } = 15;

    /// <summary>每條連線送出佇列的則數上限（超過就清空並重送 init）</summary>
    public int OutboxMaxMessages { get; set; } = 500;

    /// <summary>每條連線送出佇列的位元組上限</summary>
    public int OutboxMaxBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// 網頁把剛斷線的電腦標成「重新連線中」的時間（毫秒，1000～60000）：只在 Development、Testing 環境生效（端到端縮短等待；init 的
    /// <c>remote.reconnectGraceMs</c>，<see cref="Realtime.BrowserTuningRules.ReconnectGraceMs"/>），其他環境忽略、網頁照內建的 60 秒
    /// </summary>
    public int? ReconnectGraceMs { get; set; }

    /// <summary>
    /// 瀏覽器 hub 的 WebSocket 壓縮（permessage-deflate；<c>off</c>｜<c>noContextTakeover</c>｜<c>contextTakeover</c>，不認得的值當成 off）：
    /// 預設每則訊息各自壓縮（擁有者 2026-10-23 選定，設計 2026-10-23-live-delta 第 5.3 節的旁通道分析）。出問題時改成 off、重啟服務即可，不必重新部署
    /// </summary>
    public string WebSocketCompression { get; set; } = Realtime.WebSocketCompressionRules.NoContextTakeover;
}

/// <summary>
/// 伺服器的對話（WP4.2；計畫 docs/plans/2026-10-19-wp4.2-server-conversations.md 的步 1 與各決定）：**只有上限參數，沒有任何功能開關**
/// （擁有者 2026-10-19 的答 1：合併部署就全部生效；減量與停用改用桌面的 syncPause、對話政策，最後一招是回滾）。
/// 啟動（<c>AddHubRealtime</c>）與 admin preflight 都以 <see cref="Sync.ConversationSettingsRules.Validate"/> 驗證；改了要重啟服務。
/// </summary>
internal sealed class ConversationSettings
{
    /// <summary>即時更新：每台裝置每秒幾則（權杖桶，突發同值；D27）</summary>
    public int MaxLivePerDevicePerSecond { get; set; } = 50;

    /// <summary>即時更新：每個對話的突發（速率是 SyncProtocol 的 MaxLiveHz；D27）</summary>
    public int LiveBurstPerConversation { get; set; } = 4;

    /// <summary>即時疊加層全部的位元組上限（滿了淘汰最舊的；D27）</summary>
    public int OverlayMaxBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>即時疊加層每個對話最多幾個 ord（D27）</summary>
    public int OverlayMaxOrdsPerConversation { get; set; } = 64;

    /// <summary>瀏覽器的一筆 Item 渲染後的位元組上限（超過先縮短、再不行送佔位；D20）</summary>
    public int RenderedItemMaxBytes { get; set; } = 256 * 1024;

    /// <summary>瀏覽器的 conversation／olderItems 一頁的位元組上限（D20）</summary>
    public int PageMaxBytes { get; set; } = 1024 * 1024;

    /// <summary>渲染結果快取（LRU，只在閘道迴圈裡存取）的位元組上限（D24）</summary>
    public int RenderCacheMaxBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>變更通知佇列的則數上限（滿了丟掉並設「整份重讀」；D13）</summary>
    public int ChangeFeedMaxEntries { get; set; } = 10_000;

    /// <summary>變更通知佇列的位元組上限（D13）</summary>
    public int ChangeFeedMaxBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>閘道最多每幾毫秒推一次對話的增量（D24）</summary>
    public int ConversationPushIntervalMs { get; set; } = 50;

    /// <summary>閘道每一輪最多處理幾個 open／loadOlder（其餘下一輪；D38）</summary>
    public int MaxOpensPerRound { get; set; } = 8;

    /// <summary>open 觸發的 SnapshotRequest：同一個對話幾分鐘內只送一次（D26）</summary>
    public int SnapshotOpenCooldownMinutes { get; set; } = 10;

    /// <summary>restore 的 SnapshotRequest（後備）：擁有者 Prove 之後過了幾秒仍是 recovering 才送（D26）</summary>
    public int SnapshotRestoreGraceSeconds { get; set; } = 300;

    /// <summary>restore 的 SnapshotRequest：每台裝置每秒最多幾則（D26）</summary>
    public int SnapshotRestorePerSecond { get; set; } = 2;

    /// <summary>伺服器草稿：工作階段斷線之後保留幾秒（D29）</summary>
    public int DraftGraceSeconds { get; set; } = 120;

    /// <summary>伺服器草稿：每個工作階段最多幾份（D29）</summary>
    public int MaxDraftsPerSession { get; set; } = 8;

    /// <summary>WatchChanged 一則最多列幾個對話（D25）</summary>
    public int MaxWatchIds { get; set; } = 500;

    /// <summary>重建物化表時每個寫入工作最多重播幾筆事件（D14）</summary>
    public int RebuildChunkEvents { get; set; } = 500;

    /// <summary>被拒與再遮蔽的稽核：每台裝置每幾秒最多寫一筆彙總（D8）</summary>
    public int RejectAuditWindowSeconds { get; set; } = 60;

    /// <summary>對話的被拒事件（rejected_events）保留幾天；0＝不刪（D40、答 7）</summary>
    public int RejectedRetentionDays { get; set; } = 30;

    /// <summary>
    /// 被同一筆項目的新版本取代的 <c>conv.item</c> 事件超過幾天就刪（2026-10-25；<see cref="Sync.ConversationMaintenance"/>）：
    /// 0＝不刪，否則至少 2（遠大於桌面 outbox 保留的 24 小時，之後再送那些 deviceSeq 只會被判成 Stale）
    /// </summary>
    public int SupersededRetentionDays { get; set; } = 7;

    /// <summary>sync.db 到這個大小，ops/status 的 syncDbLevel 標「注意」（warn；D31、答 7）</summary>
    public long SyncDbWarnBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>sync.db 到這個大小標「嚴重」（critical）：只是告警，伺服器不自動停收（D31、答 7）</summary>
    public long SyncDbCriticalBytes { get; set; } = 8L * 1024 * 1024 * 1024;
}
