using System.Globalization;
using System.Security.Cryptography;
using Myriagent.Sync;
using Microsoft.Data.Sqlite;

namespace MyriagentHub.Data;

/// <summary>
/// sync.db 的結構（設計 3.2；第 3 階段的部分：持久事件、帳戶目錄、用量、CLI 狀態、漫遊偏好）。不經 EF：熱路徑只有
/// <see cref="SyncWriter"/> 一條寫入連線。版本存 <c>server_meta.schema_version</c>，遷移只往前、先加欄位、下一版才移除。
/// 時間一律 INTEGER Unix 毫秒；排序只看 <c>events.seq</c>，從不看時鐘。
/// </summary>
/// <remarks>
/// 與設計 3.2 不同之處（設計 14.3）：<c>rejected_events</c> 加 <c>stream</c>（冪等判斷的事件身分要它）；<c>device_streams</c> 加顯示名稱
/// <c>name</c>（device.update 與事件寫在同一個交易，不跨到 hub.db）與 Hello 回報的 <c>app_version</c>，CLI 狀態只放 <c>device_cli</c>（不另存 cli_json）；
/// payload 存原樣的字串（TEXT，<c>enc=gzip</c> 時是標準 base64），payload_sha256 存 32 位元組：裝置送的原文的 SHA-256（冪等身分）；
/// 伺服器再遮蔽過的對話事件（第二道 DLP 命中，WP4.2 D44），存的 payload 是遮蔽版，雜湊仍是裝置送的。
/// 結構 v2（第 4 階段的表，WP4.1 D54；與設計 3.2 不同之處見 <see cref="V2"/> 的說明與設計 14.11）只加表與索引，v1 的表一個字都不改：
/// 回滾到 v1 的程式時資料庫不還原（<see cref="SyncWriter"/> 只記警告，舊程式不碰這些表）。v2 凍結，之後的改動一律進 v3。
/// 結構 v3（WP4.2 步 12A、答 6）只加一張表 <c>conversation_unread</c>（伺服器記的網頁未讀，見 <see cref="V3"/>）：v2 的程式（<c>1724718</c>）開 v3 的檔時
/// from 比它的版本新，什麼都不動、不改 <c>schema_version</c>，只記一筆「比程式新」的警告，所以可以回滾。
/// 和桌面的 sync.db v3（<c>SyncStoreSchema</c>）是不同的資料庫，只是編號都叫 v3。
/// <c>commands</c> 另有兩個不升版本的維護索引（<see cref="CommandIndexes"/>，WP4.5 合併前審查 R1）。
/// 結構 v4（app 自我更新 U2）只加 <c>device_app</c>（<see cref="V4"/>），v1～v3 的物件一個字都不改；回滾到 v3 的程式時 from 比它新，什麼都不動、只記警告。v4 凍結，之後的改動一律進 v5。
/// 結構 v5（各帳戶的 token 計數，tok1）只加 <c>token_tallies</c>（<see cref="V5"/>），v1～v4 的物件一個字都不改；回滾到 v4 的程式時同樣什麼都不動、只記警告。
/// v5 凍結，之後的改動一律進 v6。
/// 結構 v6（第 5 階段的逐字稿日誌與交接，WP5.1）只加 <c>logs</c>、<c>log_chunks</c>、<c>log_generations</c>、<c>handoffs</c> 與它們的索引（<see cref="V6"/>），
/// v1～v5 的物件一個字都不改；回滾到 v5 的程式時同樣什麼都不動、只記警告（計畫 4.3 第 1 點）。v6 凍結，之後的改動一律進 v7。
/// 結構 v7（各電腦的排程，sched1；sync-protocol 14.12）只加 <c>device_schedules</c>（<see cref="V7"/>），v1～v6 的物件一個字都不改；
/// 回滾到 v6 的程式時同樣什麼都不動、只記警告。v7 凍結，之後的改動一律進 v8。
/// </remarks>
internal static class SyncSchema
{
    public const int CurrentVersion = 7;

    public const string SchemaVersionKey = "schema_version";

    /// <summary>建庫時的 128 位元亂數（小寫十六進位）：資料庫是新的（伺服器重建）時裝置看得出來</summary>
    public const string InstanceIdKey = "instance_id";

    /// <summary>每次還原 +1（admin restore 寫，與 hub.db kv 的 restore_generation 相同的值）</summary>
    public const string RestoreGenerationKey = "restore_generation";

    /// <summary>還原後新事件的 seq 起點拉高的量（設計 10.2：不會與還原前已發給裝置的 seq 重複）</summary>
    public const long RestoreSeqJump = 1_000_000_000;

    /// <summary>結構 v1（第 3 階段）；測試以它重建 v1 的檔驗升級</summary>
    internal const string V1 = """
        CREATE TABLE server_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);

        -- 每台裝置的上行事件流狀態
        CREATE TABLE device_streams(
          device_id TEXT PRIMARY KEY,
          next_device_seq INTEGER NOT NULL DEFAULT 1,
          boot_id TEXT,
          name TEXT,
          app_version TEXT,
          last_hello_at INTEGER,
          last_heartbeat_at INTEGER,
          last_ui_alive_at INTEGER,
          needs_resync INTEGER NOT NULL DEFAULT 0,
          policy_json TEXT);

        -- 持久事件：只增不改
        CREATE TABLE events(
          seq INTEGER PRIMARY KEY AUTOINCREMENT,
          stream TEXT NOT NULL,
          cls TEXT NOT NULL,
          conversation_id TEXT,
          author TEXT NOT NULL,
          device_seq INTEGER,
          payload_sha256 BLOB,
          epoch INTEGER,
          kind TEXT NOT NULL,
          payload TEXT NOT NULL,
          enc TEXT NOT NULL DEFAULT 'json',
          created_at INTEGER NOT NULL);
        CREATE UNIQUE INDEX ux_events_author_seq ON events(author, device_seq) WHERE device_seq IS NOT NULL;
        CREATE INDEX ix_events_stream ON events(stream, seq);
        CREATE INDEX ix_events_cls ON events(cls, seq);

        -- 被擋下的上行事件：仍佔 deviceSeq（維持無間隙），供冪等判斷與鑑識
        CREATE TABLE rejected_events(
          author TEXT NOT NULL,
          device_seq INTEGER NOT NULL,
          payload_sha256 BLOB NOT NULL,
          stream TEXT NOT NULL,
          conversation_id TEXT,
          epoch INTEGER,
          kind TEXT NOT NULL,
          payload TEXT,
          enc TEXT,
          reason TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          PRIMARY KEY(author, device_seq));

        -- 帳戶目錄（沒有任何權杖欄位）
        CREATE TABLE logical_accounts(
          logical_account_id TEXT PRIMARY KEY,
          provider TEXT NOT NULL,
          identity_key TEXT NOT NULL UNIQUE,
          label TEXT NOT NULL,
          color TEXT NOT NULL,
          sort_order INTEGER NOT NULL,
          expected_email TEXT,
          expected_org_id TEXT,
          plan_hint TEXT,
          capabilities_json TEXT,
          version INTEGER NOT NULL,
          created_at INTEGER NOT NULL,
          deleted_at INTEGER);
        CREATE TABLE account_links(
          logical_account_id TEXT NOT NULL,
          device_id TEXT NOT NULL,
          local_account_id TEXT NOT NULL,
          config_kind TEXT NOT NULL,
          status TEXT NOT NULL,
          status_text TEXT,
          email TEXT,
          org_id TEXT,
          plan TEXT,
          access_expires_at INTEGER,
          login_expires_at INTEGER,
          login_busy INTEGER NOT NULL DEFAULT 0,
          identity_ok INTEGER NOT NULL,
          sessions_json TEXT,
          checked_at INTEGER,
          version INTEGER NOT NULL,
          PRIMARY KEY(logical_account_id, device_id));
        CREATE INDEX ix_account_links_device ON account_links(device_id, local_account_id);

        CREATE TABLE usage_latest(
          logical_account_id TEXT PRIMARY KEY,
          snapshot_json TEXT NOT NULL,
          source_device_id TEXT NOT NULL,
          source TEXT NOT NULL,
          fetched_at INTEGER NOT NULL);
        CREATE TABLE usage_points(
          logical_account_id TEXT NOT NULL,
          window_key TEXT NOT NULL,
          at INTEGER NOT NULL,
          percent REAL NOT NULL,
          PRIMARY KEY(logical_account_id, window_key, at));
        -- 用量查詢的指派（小租約）：device_id 是空字串＝目前沒有人負責（term 照樣保留、只增不減）
        CREATE TABLE usage_pollers(
          logical_account_id TEXT PRIMARY KEY,
          device_id TEXT NOT NULL,
          term INTEGER NOT NULL,
          assigned_at INTEGER NOT NULL);

        CREATE TABLE device_cli(
          device_id TEXT NOT NULL,
          provider TEXT NOT NULL,
          state_json TEXT NOT NULL,
          updated_at INTEGER NOT NULL,
          PRIMARY KEY(device_id, provider));

        -- 漫遊的外觀偏好（只接受 PrefsRules 的四個鍵）
        CREATE TABLE prefs(
          key TEXT PRIMARY KEY,
          value_json TEXT NOT NULL,
          version INTEGER NOT NULL,
          updated_at INTEGER NOT NULL,
          updated_by TEXT NOT NULL);
        """;

    /// <summary>
    /// 結構 v2：第 4 階段的對話物化、遠端指令、媒體參照與 blob（設計 3.2 照 14.11 的 D54 改寫；WP4.2、WP4.5、WP4.7 才寫這些表，WP4.1 只建）。
    /// 全部 IF NOT EXISTS。與設計 3.2 不同之處：
    /// <list type="bullet">
    /// <item><c>conversations</c>：不做 <c>delete_proof</c>，改記 <c>deleted_by</c>（刪除者：擁有者裝置 id 或 <c>owner</c>）；加 <c>transcript</c>（meta 的逐字稿狀態，第 5 階段才有 <c>missing</c>）；
    /// <c>meta_json</c> 是 <c>conv.meta</c> 的 payload（<c>ConversationMetaPayload</c>，sync-protocol 14.2）；<c>lease_state</c> 預設 <c>owned</c>（第 4 階段沒有租約）。</item>
    /// <item><c>items</c>：版本是 (<c>epoch</c>, <c>ver</c>)（<c>LiveRules</c>，不是設計的 (bootId, n)）；<c>body</c> 是 <c>conv.item</c> 的 payload（<c>WireItem</c>，TEXT、<c>enc</c> 同 events）。</item>
    /// <item><c>commands</c>：<c>command_id</c> 是伺服器產生的 <c>cid</c>（D17），瀏覽器的 id 另存 <c>browser_command_id</c>；<c>cmd_json</c> 是遮蔽後的指令（稽核與顯示）、
    /// <c>content_sha256</c> 是信封的內容摘要（<c>CommandEnvelope.ContentDigest</c>，取代設計的 <c>body_sha256</c>：重簽後不變）；
    /// durable 指令另存未簽的 body（<c>unsigned_body</c>，重簽用、送達或過期後清掉，D16）；加 <c>full_access</c>。</item>
    /// <item><c>media_refs</c>：ref 由裝置發給，主鍵 <c>(device_id, ref)</c>，<b>不存路徑</b>（路徑只在裝置本機的 <c>media_refs_local</c>，D14）。</item>
    /// </list>
    /// </summary>
    internal const string V2 = """
        -- 對話物化（WP4.2；EventLog 在同一個交易裡寫 events 並更新，任何時刻讀到的都對應某個 seq 前綴）
        CREATE TABLE IF NOT EXISTS conversations(
          conversation_id TEXT PRIMARY KEY,
          provider TEXT NOT NULL,
          logical_account_id TEXT,
          origin_device_id TEXT NOT NULL,
          owner_device_id TEXT,
          epoch INTEGER NOT NULL,
          lease_state TEXT NOT NULL DEFAULT 'owned',
          meta_json TEXT NOT NULL,
          header_json TEXT,
          title TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          updated_at INTEGER NOT NULL,
          last_seq INTEGER NOT NULL,
          item_count INTEGER NOT NULL DEFAULT 0,
          max_ord INTEGER NOT NULL DEFAULT 0,
          running INTEGER NOT NULL DEFAULT 0,
          waiting INTEGER NOT NULL DEFAULT 0,
          status_text TEXT,
          turn_started_at INTEGER,
          last_turn TEXT,
          sync_policy TEXT NOT NULL DEFAULT 'full',
          transcript TEXT,
          forked_from TEXT,
          fork_point_seq INTEGER,
          deleted_at INTEGER,
          deleted_by TEXT);
        CREATE INDEX IF NOT EXISTS ix_conv_updated ON conversations(updated_at DESC);

        -- 每筆項目只留最新版本（歷史在 events）
        CREATE TABLE IF NOT EXISTS items(
          conversation_id TEXT NOT NULL,
          item_id TEXT NOT NULL,
          ord INTEGER NOT NULL,
          last_seq INTEGER NOT NULL,
          epoch INTEGER NOT NULL,
          ver INTEGER NOT NULL,
          kind TEXT NOT NULL,
          status TEXT NOT NULL,
          final INTEGER NOT NULL,
          origin_device_id TEXT NOT NULL,
          body TEXT NOT NULL,
          enc TEXT NOT NULL DEFAULT 'json',
          removed INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY(conversation_id, item_id));
        CREATE INDEX IF NOT EXISTS ix_items_order ON items(conversation_id, ord);

        -- 佇列快照（conv.queue）與輸入區附件（第 5 階段起隨交接帶走）
        CREATE TABLE IF NOT EXISTS conversation_runtime(
          conversation_id TEXT PRIMARY KEY,
          queue_json TEXT NOT NULL,
          pending_attachments_json TEXT NOT NULL,
          busy INTEGER NOT NULL DEFAULT 0,
          busy_text TEXT,
          version INTEGER NOT NULL);

        -- 遠端指令（WP4.5）：去重、至多一次、結果回送、稽核連結
        CREATE TABLE IF NOT EXISTS commands(
          command_id TEXT PRIMARY KEY,
          browser_command_id TEXT,
          issuer TEXT NOT NULL,
          conversation_id TEXT,
          target_device_id TEXT NOT NULL,
          target_epoch INTEGER,
          cmd TEXT NOT NULL,
          cmd_json TEXT NOT NULL,
          tier INTEGER NOT NULL,
          full_access INTEGER NOT NULL DEFAULT 0,
          content_sha256 TEXT NOT NULL,
          durable INTEGER NOT NULL DEFAULT 0,
          unsigned_body TEXT,
          state TEXT NOT NULL,
          result_json TEXT,
          created_at INTEGER NOT NULL,
          delivered_at INTEGER,
          done_at INTEGER,
          expires_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_cmd_target ON commands(target_device_id, state);

        -- 媒體參照（WP4.7）：ref 由裝置發給，伺服器不存路徑
        CREATE TABLE IF NOT EXISTS media_refs(
          device_id TEXT NOT NULL,
          ref TEXT NOT NULL,
          conversation_id TEXT NOT NULL,
          purpose TEXT NOT NULL,
          thumb_blob TEXT,
          created_at INTEGER NOT NULL,
          last_used_at INTEGER NOT NULL,
          PRIMARY KEY(device_id, ref));
        CREATE INDEX IF NOT EXISTS ix_media_refs_conv ON media_refs(conversation_id);

        -- 內容定址的 blob（WP4.7；檔案在 blobs\ 資料夾）
        CREATE TABLE IF NOT EXISTS blobs(
          sha256 TEXT PRIMARY KEY,
          size INTEGER NOT NULL,
          content_type TEXT,
          kind TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          last_ref_at INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS blob_refs(
          sha256 TEXT NOT NULL,
          ref_kind TEXT NOT NULL,
          ref_id TEXT NOT NULL,
          PRIMARY KEY(sha256, ref_kind, ref_id));
        """;

    /// <summary>v2 新增的表（測試與文件比對用）</summary>
    internal static IReadOnlyList<string> V2Tables { get; } = ["conversations", "items", "conversation_runtime", "commands", "media_refs", "blobs", "blob_refs"];

    /// <summary>
    /// 結構 v3（WP4.2 步 12A；答 6、D46）：伺服器記的網頁未讀，有列就是未讀（<c>since_seq</c>／<c>since_at</c>＝標未讀的那筆事件的 seq 與時間）。
    /// 只在閘道寫（標記以 INSERT … SELECT 確認對話存在而且沒刪除，不留孤兒列）；對話在伺服器上刪除時同一個交易刪掉它的列，重建不碰這張表
    /// </summary>
    internal const string V3 = """
        CREATE TABLE IF NOT EXISTS conversation_unread(
          conversation_id TEXT PRIMARY KEY,
          since_seq INTEGER NOT NULL,
          since_at INTEGER NOT NULL);
        """;

    /// <summary>v3 新增的表（測試與文件比對用）</summary>
    internal static IReadOnlyList<string> V3Tables { get; } = ["conversation_unread"];

    /// <summary>
    /// 結構 v4（app 自我更新 U2，規格 7.8；sync-protocol 6.6）：各電腦 app 的更新狀態（<c>app.state</c> 的最新一份），每台一列、新的取代舊的。
    /// <c>state_json</c> 是伺服器檢查並整理過、重新序列化的 <c>AppStatePayload</c>（<c>SyncJson</c>），<c>updated_at</c> 是伺服器收到的時間；
    /// <c>device.upsert</c> 的 <c>app</c> 與「版本」頁都讀它。撤銷裝置時不刪（同 <c>device_cli</c>），讀的一方自己略過已撤銷的裝置。
    /// 只加這張表、IF NOT EXISTS，v1～v3 的物件一個字都不改
    /// </summary>
    internal const string V4 = """
        CREATE TABLE IF NOT EXISTS device_app(
          device_id TEXT PRIMARY KEY,
          state_json TEXT NOT NULL,
          updated_at INTEGER NOT NULL);
        """;

    /// <summary>v4 新增的表（測試與文件比對用）</summary>
    internal static IReadOnlyList<string> V4Tables { get; } = ["device_app"];

    /// <summary>
    /// 結構 v5（各帳戶的 token 計數，tok1；sync-protocol 14.9）：每台電腦、每個邏輯帳戶、每個日期（那台的本機日期 yyyy-MM-dd）一列，
    /// 是那台 app 經手的對話用掉的 token（<c>input</c> 含快取、<c>cached</c> 是其中的快取讀取）；<c>updated_at</c> 是伺服器收到的時間。
    /// 只供瀏覽器版的帳戶卡顯示：裝置的 <c>TokensPublish</c> 整份取代（full）或逐格取代，伺服器順便刪掉超過保留天數的列；
    /// 撤銷裝置、刪除帳戶時不刪，讀的一方自己略過。只加這張表、IF NOT EXISTS，v1～v4 的物件一個字都不改
    /// </summary>
    internal const string V5 = """
        CREATE TABLE IF NOT EXISTS token_tallies(
          device_id TEXT NOT NULL,
          logical_account_id TEXT NOT NULL,
          day TEXT NOT NULL,
          input INTEGER NOT NULL,
          cached INTEGER NOT NULL,
          output INTEGER NOT NULL,
          updated_at INTEGER NOT NULL,
          PRIMARY KEY(device_id, logical_account_id, day));
        """;

    /// <summary>v5 新增的表（測試與文件比對用）</summary>
    internal static IReadOnlyList<string> V5Tables { get; } = ["token_tallies"];

    /// <summary>
    /// 結構 v6（第 5 階段計畫 5.7；docs/sync-protocol.md 第 16 節）：逐字稿與檔案日誌（WP5.2 寫）與交接（WP5.5 寫）。WP5.1 只建表、沒有程式讀寫。
    /// <list type="bullet">
    /// <item><c>logs</c>：每個日誌（logKey＝logId 的 SHA-256）目前世代的狀態——已提交的長度與鏈雜湊、行數、整檔的總長度（檔案種類）、寫入者與 epoch、
    /// 算過的整檔 SHA-256 快取（<c>full_sha256</c> 對應 <c>full_sha256_length</c>）、掃描發現損毀的時間。位元組在資料根的 <c>transcripts\</c>。</item>
    /// <item><c>log_chunks</c>：每一次附加的邊界與接上之後的鏈雜湊（掃描依它重算；冪等重送以 (offset, length, chain_after) 判斷）。</item>
    /// <item><c>log_generations</c>：被取代的舊世代（<c>rewrite</c>：本機檔被改寫；<c>repair</c>：損毀後重傳），保留 30 天。</item>
    /// <item><c>handoffs</c>：交接與強制接手（<c>command_id</c> 唯一＝發起端重送去重；同一個對話最多一筆進行中的，<c>ux_handoffs_active</c>）。
    /// <c>ready_json</c> 是 A 的 HandoffReady（重啟後重新驗證用）、<c>fence_json</c> 是強制接手的圍欄、<c>committed_seq</c> 是 conv.lease 的 seq。</item>
    /// </list>
    /// 全部 IF NOT EXISTS；不放 <c>conversation_files</c>、<c>repos</c>、<c>path_maps</c>（附件與工作區也是日誌、資料夾對應只在電腦端，計畫第 2 節）
    /// </summary>
    internal const string V6 = """
        CREATE TABLE IF NOT EXISTS logs(
          log_key TEXT PRIMARY KEY,
          log_id TEXT NOT NULL,
          conversation_id TEXT NOT NULL,
          kind TEXT NOT NULL,
          is_main INTEGER NOT NULL DEFAULT 0,
          gen INTEGER NOT NULL,
          length INTEGER NOT NULL,
          chain BLOB NOT NULL,
          line_count INTEGER NOT NULL,
          total INTEGER,
          rel_path TEXT,
          ref_id TEXT,
          writer_device_id TEXT NOT NULL,
          writer_epoch INTEGER NOT NULL,
          writer_cli_version TEXT,
          full_sha256 BLOB,
          full_sha256_length INTEGER,
          damaged_at INTEGER,
          updated_at INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_logs_conversation ON logs(conversation_id);
        CREATE TABLE IF NOT EXISTS log_chunks(
          log_key TEXT NOT NULL,
          gen INTEGER NOT NULL,
          offset INTEGER NOT NULL,
          length INTEGER NOT NULL,
          chain_after BLOB NOT NULL,
          epoch INTEGER NOT NULL,
          device_id TEXT NOT NULL,
          created_at INTEGER NOT NULL,
          PRIMARY KEY(log_key, gen, offset));
        CREATE TABLE IF NOT EXISTS log_generations(
          log_key TEXT NOT NULL,
          gen INTEGER NOT NULL,
          length INTEGER NOT NULL,
          chain BLOB NOT NULL,
          reason TEXT NOT NULL,
          superseded_at INTEGER NOT NULL,
          PRIMARY KEY(log_key, gen));
        CREATE TABLE IF NOT EXISTS handoffs(
          handoff_id TEXT PRIMARY KEY,
          command_id TEXT NOT NULL UNIQUE,
          conversation_id TEXT NOT NULL,
          kind TEXT NOT NULL,
          from_device_id TEXT NOT NULL,
          to_device_id TEXT NOT NULL,
          from_epoch INTEGER NOT NULL,
          to_epoch INTEGER,
          mode TEXT,
          auto_continue INTEGER NOT NULL,
          initiator_kind TEXT NOT NULL,
          initiator_id TEXT,
          state TEXT NOT NULL,
          requested_at INTEGER NOT NULL,
          deadline_at INTEGER NOT NULL,
          state_at INTEGER NOT NULL,
          final_device_seq INTEGER,
          ready_json TEXT,
          fence_json TEXT,
          error TEXT,
          committed_seq INTEGER,
          import_state TEXT,
          import_error TEXT,
          updated_at INTEGER NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_handoffs_active ON handoffs(conversation_id) WHERE state IN ('requested','accepted','draining','ready');
        """;

    /// <summary>v6 新增的表（測試與文件比對用）</summary>
    internal static IReadOnlyList<string> V6Tables { get; } = ["logs", "log_chunks", "log_generations", "handoffs"];

    /// <summary>v6 新增的索引（不含主鍵與 UNIQUE 的自動索引）</summary>
    internal static IReadOnlyList<string> V6Indexes { get; } = ["ix_logs_conversation", "ux_handoffs_active"];

    /// <summary>
    /// 結構 v7（各電腦的排程，sched1；sync-protocol 14.12）：每台電腦一列，是那台排程頁最後上傳的摘要（JSON，<c>SchedulesPublish</c> 整份取代）、
    /// <c>updated_at</c> 是伺服器收到的時間。只供顯示（瀏覽器版與其他桌面的排程頁，那台離線時顯示最後回報的）；撤銷裝置時不刪，讀的一方自己略過。
    /// 只加這張表、IF NOT EXISTS，v1～v6 的物件一個字都不改
    /// </summary>
    internal const string V7 = """
        CREATE TABLE IF NOT EXISTS device_schedules(
          device_id TEXT PRIMARY KEY,
          snapshot TEXT NOT NULL,
          updated_at INTEGER NOT NULL);
        """;

    /// <summary>v7 新增的表（測試與文件比對用）</summary>
    internal static IReadOnlyList<string> V7Tables { get; } = ["device_schedules"];

    /// <summary>
    /// <c>commands</c> 的維護查詢用的索引（WP4.5 合併前審查 R1）：每秒的結果期限掃描與每 60 秒的 durable 7 天期限（<c>state</c>, <c>expires_at</c>）、
    /// 30 天清理（<c>done_at</c>，只索引結束的列），沒有它們時三個查詢都整表掃描再排序，成本跟著 30 天保留的列數長。
    /// 只加索引、不升結構版本（不和其他工作包搶版本號）：全部 IF NOT EXISTS，<see cref="Migrate"/> 對程式的版本（含）以下的檔每次都執行
    /// （正式站已是 v3，放進 <see cref="V2"/>、<see cref="V3"/> 不會執行），比程式新的檔不動。舊程式（<c>5538705</c>、<c>1724718</c>）不比對索引，多出來的不影響它們
    /// </summary>
    internal const string CommandIndexes = """
        CREATE INDEX IF NOT EXISTS ix_cmd_state_expires ON commands(state, expires_at);
        CREATE INDEX IF NOT EXISTS ix_cmd_done ON commands(done_at) WHERE done_at IS NOT NULL;
        """;

    /// <summary><see cref="CommandIndexes"/> 建的索引（測試比對用）</summary>
    internal static IReadOnlyList<string> CommandIndexNames { get; } = ["ix_cmd_state_expires", "ix_cmd_done"];

    /// <summary>建立或遷移結構（在寫入連線上、交易內）；回傳遷移前的版本（新建庫為 0）</summary>
    public static int Migrate(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        var exists = Convert.ToInt64(Scalar(connection, tx, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='server_meta'"), CultureInfo.InvariantCulture) > 0;
        var from = exists && Scalar(connection, tx, "SELECT value FROM server_meta WHERE key = $k", ("$k", SchemaVersionKey)) is string text
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
                ? version
                : 0;
        if (from == 0)
        {
            Execute(connection, tx, V1);
            SetMeta(connection, tx, InstanceIdKey, NewInstanceId());
            SetMeta(connection, tx, RestoreGenerationKey, "0");
        }
        // 依序往前加；比程式新（回滾到舊版）的不動
        if (from < 2)
            Execute(connection, tx, V2);
        if (from < 3)
            Execute(connection, tx, V3);
        if (from < 4)
            Execute(connection, tx, V4);
        if (from < 5)
            Execute(connection, tx, V5);
        if (from < 6)
            Execute(connection, tx, V6);
        if (from < 7)
            Execute(connection, tx, V7);
        // 之後的版本在這裡依序加：if (from < 8) { … }
        // 不升版本的索引（R1）：程式的版本以下每次都補（IF NOT EXISTS）；比程式新的不動
        if (from <= CurrentVersion)
            Execute(connection, tx, CommandIndexes);
        if (from < CurrentVersion)
            SetMeta(connection, tx, SchemaVersionKey, CurrentVersion.ToString(CultureInfo.InvariantCulture));
        tx.Commit();
        return from;
    }

    /// <summary>伺服器資料庫的身分（Hello、配對回應）</summary>
    public static ServerInstance ReadInstance(SqliteConnection connection, SqliteTransaction? tx = null)
    {
        var id = Scalar(connection, tx, "SELECT value FROM server_meta WHERE key = $k", ("$k", InstanceIdKey)) as string
            ?? throw new InvalidOperationException("sync.db 沒有 instance_id");
        var generation = Scalar(connection, tx, "SELECT value FROM server_meta WHERE key = $k", ("$k", RestoreGenerationKey)) is string g
            && long.TryParse(g, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        return new ServerInstance(id, generation);
    }

    public static string NewInstanceId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    internal static void SetMeta(SqliteConnection connection, SqliteTransaction tx, string key, string value) =>
        Execute(connection, tx, "INSERT INTO server_meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value", ("$k", key), ("$v", value));

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return cmd.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
