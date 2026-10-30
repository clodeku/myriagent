namespace Myriagent.Sync;

/// <summary>
/// 桌面 sync.db 的結構（設計 3.6 的子集：第 3 階段的 v1、第 4 階段的 v2、WP4.3 的 v3、第 5 階段的 v4）。結構版本記在 <c>PRAGMA user_version</c>，另在 kv 記 <c>schema_min_reader</c>：
/// 遷移只做加法（加表、加欄位，部署回滾相容），新版的 min_reader 沒有超過這一版的 <see cref="SyncStore.SchemaVersion"/> 時這一版照樣能開。
/// 每一條 INSERT 都列出欄位名稱：新版加的欄位不會讓舊版的寫入失敗。
/// </summary>
internal static class SyncStoreSchema
{
    // kv 的鍵
    public const string DeviceId = "device_id";
    public const string PairedAt = "paired_at";
    public const string NextDeviceSeq = "next_device_seq";
    public const string SeenInstanceId = "seen_instance_id";
    public const string SeenRestoreGeneration = "seen_restore_generation";
    public const string PendingInstanceId = "pending_instance_id";
    public const string PendingRestoreGeneration = "pending_restore_generation";
    public const string Reconcile = "reconcile";
    public const string ReconcileThrough = "reconcile_through";
    public const string RegistryRev = "registry_rev";
    public const string LastHelloAt = "last_hello_at";
    public const string LastSyncAt = "last_sync_at";
    public const string SchemaMinReader = "schema_min_reader";

    // kv 的鍵（v3，WP4.3 的對話發布；計畫 D13、D36）

    /// <summary>上一次進 Live 的連線有沒有協商出 <c>conv1</c>（"1"／"0"；沒有這個鍵＝false）：離線時要不要照寫對話事件</summary>
    public const string ConvArmed = "conv_armed";

    /// <summary>
    /// 上一條交握完成的連線有沒有協商出 <c>prov1</c>（"1"／"0"；沒有這個鍵＝false）：離線時要不要照寫 Grok、Antigravity 的帳戶與對話事件
    /// （設計 2026-10-22-grok-antigravity-sync 第 3 節）
    /// </summary>
    public const string Prov1Armed = "prov1_armed";

    /// <summary>第一次協商出 <c>prov1</c> 時已把對話中繼資料的游標歸零、補拉過舊版略過的 Grok、Antigravity 對話（"1"）</summary>
    public const string Prov1Replicas = "prov1_replicas";

    /// <summary>上一次發布時的 <c>CredentialLeakRules.Version</c>：換版時重新投影（D36）</summary>
    public const string PublisherDlpVersion = "publisher_dlp_version";

    /// <summary>發布者最後一次寫 outbox 時的 next_device_seq（與 AppendMany 同一個交易）：開啟時比它大＝中間跑過舊版（D36）</summary>
    public const string PublisherNextDeviceSeq = "publisher_next_device_seq";

    /// <summary>發布者上一次看到的伺服器實例（與 seen_server 不同＝中間跑過舊版、舊版處理了換實例，D36）</summary>
    public const string PublishServer = "publish_server";

    /// <summary>
    /// 媒體參照的歷史回補（WP4.7 計畫 D4）排好時的伺服器實例（<see cref="SyncStore.PublishServerKey"/> 的值）：和目前的不同（第一次協商出 media1、
    /// 換實例或還原之後）就對已發布的 full 對話清 items.json 的摘要、記 needs_redo，重整時 <c>RefsMissing</c> 讓缺參照的項目重新投影
    /// </summary>
    public const string MediaBackfillDone = "media_backfill_done";

    /// <summary>
    /// 縮圖上傳時原檔已經不在（守門員回 not_found）的參照與記下的時間（JSON 物件，參照 → Unix 毫秒；<see cref="ThumbnailPublishRules.ParseGone"/>）：
    /// 一段時間內 E10 再列出來也不排（2026-10-27）。只加鍵、不改結構，前一版不讀它
    /// </summary>
    public const string ThumbGone = "thumb_gone";

    // kv 的鍵（第 5 階段；計畫 D19）

    /// <summary>
    /// 第一次協商出 <c>hand1</c> 時已把對話中繼資料與複本串流的游標歸零一次（"1"；同 <see cref="Prov1Replicas"/>）：沒有 hand1 的舊版略過了不認得的
    /// <c>conv.lease</c>、游標照樣前進，複本停在交接之前（WP5.7 寫）
    /// </summary>
    public const string Hand1Replicas = "hand1_replicas";

    /// <summary>每條連線開啟時設定（WAL、每次提交 fsync、等鎖 3 秒）</summary>
    public const string ConnectionPragmas = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=3000;";

    /// <summary>第 1 版的表（建庫時在同一個交易裡執行；凍結，v2 起只加不改）</summary>
    public const string CreateV1 = """
        CREATE TABLE kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE outbox (
          device_seq INTEGER PRIMARY KEY,
          stream TEXT NOT NULL,
          kind TEXT NOT NULL,
          enc TEXT NOT NULL,
          payload TEXT NOT NULL,
          payload_sha256 TEXT NOT NULL,
          coalesce_key TEXT,
          state TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          server_seq INTEGER,
          reject_reason TEXT,
          acked_at INTEGER,
          conversation_id TEXT,
          epoch INTEGER);
        CREATE UNIQUE INDEX ux_outbox_coalesce_pending ON outbox(coalesce_key) WHERE state = 'pending' AND coalesce_key IS NOT NULL;
        CREATE TABLE cursors (cls TEXT PRIMARY KEY, seq INTEGER NOT NULL);
        CREATE TABLE trust_registry (device_id TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE catalog_accounts (logical_account_id TEXT PRIMARY KEY, version INTEGER NOT NULL, json TEXT NOT NULL);
        CREATE TABLE catalog_devices (device_id TEXT PRIMARY KEY, json TEXT NOT NULL, revoked_json TEXT);
        CREATE TABLE catalog_prefs (key TEXT PRIMARY KEY, version INTEGER NOT NULL, json TEXT NOT NULL);
        CREATE TABLE published (local_account_id TEXT PRIMARY KEY, digest TEXT NOT NULL, identity_key TEXT);
        CREATE TABLE detached (local_account_id TEXT PRIMARY KEY, logical_account_id TEXT NOT NULL, at INTEGER NOT NULL);
        """;

    /// <summary>
    /// 第 2 版加的表（D55；第 4 階段的對話、遠端指令、媒體用，WP4.3／WP4.4／WP4.7 讀寫）：全部 IF NOT EXISTS，建庫時接在 <see cref="CreateV1"/> 之後、
    /// 開啟既有的檔時每次都在同一個交易裡跑（v1 的檔補上，已經有的不動）。只加表與索引、不改 v1 的表，所以 min_reader 維持 1：
    /// 回滾到 v1 的程式照樣開、不碰這些表（回滾期間沒有記到的由 publish_state.needs_redo 讓 WP4.3 重新發布，D46）。v2 凍結，之後的改動進 v3（同樣只加）。
    /// <list type="bullet">
    /// <item><c>conversation_cursors</c>：對話的拉取游標，鍵是類別 <c>meta</c> 或串流 <c>c:{conversationId}</c>（sync-protocol 14.2；第 3 階段的 <c>cursors</c> 只放 catalog、usage）。</item>
    /// <item><c>leases</c>：對話的擁有者與世代（state 是 owned、provisional、replica、handoffOut、handoffIn、fenced、importFailed，設計 3.6、6.1）。</item>
    /// <item><c>replica_state</c>：副本拉到哪裡；<c>publish_state</c>：擁有的對話發布到哪裡（<c>acked_seq</c> 伺服器確認到的 seq、<c>needs_redo</c> 要整份重新發布）。</item>
    /// <item><c>backfill_state</c>：回補的進度（設計 14.2 規則 1，加 <c>bytes_left</c>，D42）。</item>
    /// <item><c>commands_seen</c>：遠端指令的去重（D17：<c>content_sha256</c> 相同回上次的 <c>result_json</c>、不同回 conflict；state 是 executing、done、rejected、unknown），保留 8 天。</item>
    /// <item><c>remote_audit</c>：遠端操作紀錄（D22、設計 8.6），與 commands_seen 同一個交易寫，保留 180 天；<c>body_masked</c> 是遮蔽後的指令，代理輸入與回答不記原文。</item>
    /// <item><c>media_refs_local</c>：這台發的媒體參照的正本（WP4.7 計畫 D3、D4）。項目的列對到附件（<c>attachment_id</c>）或代理媒體（<c>media_path</c>），恰有一個；
    /// 投影時才把參照填進 AgentMediaRef（items.json 不存）。抽屜與 fileFetch 的列 <c>item_id</c> 是 <c>@list</c>／<c>@file</c>、<c>media_path</c> 是完整路徑。只在本機。</item>
    /// </list>
    /// 保留期限由使用的工作包修剪（依 <c>at</c> 的索引）
    /// </summary>
    public static readonly IReadOnlyList<string> V2Tables =
        ["conversation_cursors", "leases", "replica_state", "publish_state", "backfill_state", "commands_seen", "remote_audit", "media_refs_local"];

    /// <summary><see cref="CreateV2"/> 建的索引</summary>
    public static readonly IReadOnlyList<string> V2Indexes = ["ix_commands_seen_at", "ix_remote_audit_at", "ix_media_refs_local_conversation"];

    /// <summary>第 4 階段的表（<see cref="V2Tables"/>、<see cref="V2Indexes"/> 是它建的表與索引）</summary>
    public const string CreateV2 = """
        CREATE TABLE IF NOT EXISTS conversation_cursors (stream TEXT PRIMARY KEY, seq INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS leases (
          conversation_id TEXT PRIMARY KEY,
          owner_device_id TEXT,
          epoch INTEGER NOT NULL,
          state TEXT NOT NULL,
          updated_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS replica_state (conversation_id TEXT PRIMARY KEY, snapshot_seq INTEGER NOT NULL, last_seq INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS publish_state (
          conversation_id TEXT PRIMARY KEY,
          acked_seq INTEGER NOT NULL DEFAULT 0,
          needs_redo INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS backfill_state (
          conversation_id TEXT PRIMARY KEY,
          items_done_at INTEGER,
          logs_done_at INTEGER,
          bytes_left INTEGER);
        CREATE TABLE IF NOT EXISTS commands_seen (
          command_id TEXT PRIMARY KEY,
          content_sha256 TEXT NOT NULL,
          state TEXT NOT NULL,
          result_json TEXT,
          at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_commands_seen_at ON commands_seen(at);
        CREATE TABLE IF NOT EXISTS remote_audit (
          seq INTEGER PRIMARY KEY AUTOINCREMENT,
          at INTEGER NOT NULL,
          command_id TEXT,
          cmd TEXT,
          conversation_id TEXT,
          declared_tier INTEGER,
          tier INTEGER,
          full_access INTEGER,
          policy TEXT,
          paused INTEGER,
          default_password INTEGER,
          iat INTEGER,
          created INTEGER,
          step TEXT,
          result TEXT NOT NULL,
          code TEXT,
          detail TEXT,
          count INTEGER NOT NULL DEFAULT 1,
          body_masked TEXT,
          signed_sha256 TEXT,
          sig TEXT);
        CREATE INDEX IF NOT EXISTS ix_remote_audit_at ON remote_audit(at);
        CREATE TABLE IF NOT EXISTS media_refs_local (
          ref TEXT PRIMARY KEY,
          conversation_id TEXT NOT NULL,
          item_id TEXT NOT NULL,
          attachment_id TEXT,
          media_path TEXT,
          created_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_media_refs_local_conversation ON media_refs_local(conversation_id);
        """;

    /// <summary>
    /// 第 3 版加的表（WP4.3 計畫 D11、D26：桌面的對話發布摘要與複本）：同 v2 只加表與索引、全部 IF NOT EXISTS，建庫時接在 <see cref="CreateV2"/> 之後、
    /// 開啟既有的檔時在同一個交易裡補上；不改 v1、v2 的物件，所以 min_reader 維持 1（回滾到 v1、v2 的程式照樣開、不碰這些表）。
    /// 這是桌面的 sync.db；伺服器的 sync.db 也有 v3（MyriagentHub 的 SyncSchema），是另一個資料庫，只是編號相同。
    /// <list type="bullet">
    /// <item><c>publish_meta</c>：擁有的對話發布到哪裡——<c>conv.created</c> 寫進 outbox 過沒有（<c>created_written</c>）、伺服器確認過沒有
    /// （<c>created_acked</c>）、發布時的政策、meta／佇列／items.json 的 SHA-256、封鎖的原因與時間、刪除的時間（墓碑：伺服器換實例也留著，好補送 conv.deleted）、
    /// <c>conv.created</c> 補送的次數與下次時間（拒絕碼 invalid 的處置，D14）、曾經對任何伺服器實例寫過 <c>conv.created</c>（<c>ever_created</c>：
    /// 換實例不重設，還原的伺服器上可能還有它，刪除與改成 localOnly 照樣送 conv.deleted）。</item>
    /// <item><c>published_items</c>：每個項目配到的 Ord（不重用：伺服器換實例、被拒都保留）、投影前與線上的摘要（可 null＝要重送）、截斷過、已送 itemRemoved。</item>
    /// <item><c>replica_meta</c>、<c>replica_items</c>：其他電腦的對話（複本，D26）：meta 的 JSON 與項目的線上 payload，依伺服器 seq 套用。</item>
    /// </list>
    /// v3 凍結，之後的改動進 v4（同樣只加）
    /// </summary>
    public static readonly IReadOnlyList<string> V3Tables = ["publish_meta", "published_items", "replica_meta", "replica_items"];

    /// <summary><see cref="CreateV3"/> 建的索引（複本的項目依 Ord 讀）</summary>
    public static readonly IReadOnlyList<string> V3Indexes = ["ix_replica_items_ord"];

    /// <summary>WP4.3 的對話發布與複本（<see cref="V3Tables"/>、<see cref="V3Indexes"/> 是它建的表與索引）</summary>
    public const string CreateV3 = """
        CREATE TABLE IF NOT EXISTS publish_meta (
          conversation_id TEXT PRIMARY KEY,
          created_written INTEGER NOT NULL DEFAULT 0,
          created_acked INTEGER NOT NULL DEFAULT 0,
          policy TEXT,
          meta_sha256 TEXT,
          queue_sha256 TEXT,
          items_file_sha256 TEXT,
          blocked TEXT,
          blocked_at INTEGER,
          deleted_at INTEGER,
          created_retries INTEGER NOT NULL DEFAULT 0,
          retry_at INTEGER,
          ever_created INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS published_items (
          conversation_id TEXT NOT NULL,
          item_id TEXT NOT NULL,
          ord INTEGER NOT NULL,
          src_sha256 TEXT,
          wire_sha256 TEXT,
          truncated INTEGER NOT NULL DEFAULT 0,
          removed INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (conversation_id, item_id));
        CREATE TABLE IF NOT EXISTS replica_meta (
          conversation_id TEXT PRIMARY KEY,
          owner_device_id TEXT NOT NULL,
          epoch INTEGER NOT NULL,
          meta_json TEXT NOT NULL,
          deleted INTEGER NOT NULL DEFAULT 0,
          seq INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS replica_items (
          conversation_id TEXT NOT NULL,
          item_id TEXT NOT NULL,
          ord INTEGER NOT NULL,
          payload TEXT NOT NULL,
          seq INTEGER NOT NULL,
          PRIMARY KEY (conversation_id, item_id));
        CREATE INDEX IF NOT EXISTS ix_replica_items_ord ON replica_items(conversation_id, ord);
        """;

    /// <summary>
    /// 第 4 版加的表（第 5 階段計畫 5.7、WP5.1）：同 v2、v3 只加表與索引、全部 IF NOT EXISTS，建庫時接在 <see cref="CreateV3"/> 之後、開啟既有的檔時
    /// 在同一個交易裡補上；不改 v1～v3 的物件，所以 min_reader 維持 1（回滾到 v3 以前的程式照樣開、不碰這些表；計畫 4.3 第 5 點）。WP5.1 只建表、沒有程式讀寫。
    /// <list type="bullet">
    /// <item><c>log_state</c>：這台上傳的每個日誌（WP5.3 的 LogShipper）：本機路徑（只在本機）、主檔、世代、伺服器確認到的長度與鏈雜湊、
    /// 改寫偵測用的檔頭與 acked 前段雜湊、上次看到的大小與修改時間、<c>held</c>（含這台的 Claude／Codex 登入憑證而停住的原因，D11）。</item>
    /// <item><c>handoffs_local</c>：這台參與的交接（<c>role</c> 是 source、target、fork；<c>detail_json</c> 是各角色自己的進度）。</item>
    /// <item><c>path_cache</c>：repoKey 對應到這台的資料夾（WP5.4；<c>source</c> 是 user、scan、same，<c>confirmed</c>＝使用者確認過）。只在本機、不上伺服器。</item>
    /// </list>
    /// 另外開始使用 v2 已有的 <c>leases</c>（<see cref="LocalLeaseStates"/>；沒有列＝照第 4 階段判斷）與 <c>backfill_state.logs_done_at</c>。v4 凍結，之後的改動進 v5（同樣只加）
    /// </summary>
    public static readonly IReadOnlyList<string> V4Tables = ["log_state", "handoffs_local", "path_cache"];

    /// <summary><see cref="CreateV4"/> 建的索引</summary>
    public static readonly IReadOnlyList<string> V4Indexes = ["ix_log_state_conversation"];

    /// <summary>第 5 階段的逐字稿上傳、交接與資料夾對應（<see cref="V4Tables"/>、<see cref="V4Indexes"/> 是它建的表與索引）</summary>
    public const string CreateV4 = """
        CREATE TABLE IF NOT EXISTS log_state (
          log_key TEXT PRIMARY KEY,
          log_id TEXT NOT NULL,
          conversation_id TEXT NOT NULL,
          kind TEXT NOT NULL,
          local_path TEXT NOT NULL,
          is_main INTEGER NOT NULL,
          gen INTEGER NOT NULL,
          acked_length INTEGER NOT NULL,
          acked_chain BLOB NOT NULL,
          head_hash BLOB,
          tail_hash BLOB,
          last_size INTEGER,
          last_write INTEGER,
          held TEXT,
          updated_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_log_state_conversation ON log_state(conversation_id);
        CREATE TABLE IF NOT EXISTS handoffs_local (
          handoff_id TEXT PRIMARY KEY,
          conversation_id TEXT NOT NULL,
          role TEXT NOT NULL,
          state TEXT NOT NULL,
          detail_json TEXT,
          deadline_at INTEGER,
          updated_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS path_cache (
          repo_key TEXT PRIMARY KEY,
          local_root TEXT NOT NULL,
          source TEXT NOT NULL,
          confirmed INTEGER NOT NULL,
          updated_at INTEGER NOT NULL);
        """;

    /// <summary>catalog_devices 只有撤銷、還沒有 device.upsert 時 json 欄位的值</summary>
    public const string NoDeviceJson = "null";

    public static string StateText(OutboxState state) => state switch
    {
        OutboxState.Pending => "pending",
        OutboxState.Retry => "retry",
        OutboxState.Sent => "sent",
        OutboxState.Acked => "acked",
        OutboxState.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "不認得的 outbox 狀態"),
    };

    /// <summary>以文字存（不存列舉的數值，列舉重排也不會讀錯）；不認得的回 null</summary>
    public static OutboxState? ParseState(string? text) => text switch
    {
        "pending" => OutboxState.Pending,
        "retry" => OutboxState.Retry,
        "sent" => OutboxState.Sent,
        "acked" => OutboxState.Acked,
        "rejected" => OutboxState.Rejected,
        _ => null,
    };
}

/// <summary>
/// 第 5 階段開始寫的 <c>leases.state</c>（v2 的表，計畫 5.7、6）：只有交接相關的狀態才有列，<b>沒有列＝照第 4 階段判斷</b>（資料夾在＝本機擁有、
/// 複本照 replica_meta）。中樞在這幾種狀態下不啟動 CLI（WP5.7 的守門；handoffOut 是關閉之後）
/// </summary>
internal static class LocalLeaseStates
{
    /// <summary>交出中（A：收到 HandoffRequest、接受之後到變成複本）</summary>
    public const string HandoffOut = "handoffOut";

    /// <summary>接手中（B：拉到 conv.lease、匯入完成之前）</summary>
    public const string HandoffIn = "handoffIn";

    /// <summary>匯入失敗（B 是擁有者，但還不能啟動 CLI：重試或交回）</summary>
    public const string ImportFailed = "importFailed";

    /// <summary>B 的工作資料夾還沒對應到（選資料夾之後才放回 CLI 紀錄）</summary>
    public const string NeedsFolder = "needsFolder";

    /// <summary>A 得知已被別台強制接手（FenceReconciler 處理完之前）</summary>
    public const string Fenced = "fenced";

    public static IReadOnlyList<string> All { get; } = [HandoffOut, HandoffIn, ImportFailed, NeedsFolder, Fenced];
}
