# Myriagent Hub（Hub 主機上的中央協調伺服器）

第 2 階段 WP2.3 的伺服器骨架：Kestrel 自架的 Windows 服務，直接聽 443，供應擁有者的帳號密碼登入頁與（第 3 階段起的）同步 API。
設計：`docs/plans/2026-10-10-multi-device-sync.md` 第 2.2、8、9 章；部署工具與 runbook 由 WP2.4（`tools/hub`、`tools/deploy`、`docs/hub-setup.md`）負責。

## 建置、測試、執行

```bash
# 單獨建置（WP2.1 合併後才加進 Myriagent.slnx）
dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release -nodeReuse:false -p:UseSharedCompilation=false
dotnet test tests/MyriagentHub.Tests/MyriagentHub.Tests.csproj -c Release

# 本機執行（Development：localhost:5443、檔案模式的自簽憑證、資料放在 --data）
dotnet run --project src/MyriagentHub -- --data <暫存資料夾> --environment Development

# 自含式發佈（Hub 主機沒有 AspNetCore 10 執行階段；約 120 MB、356 個檔案）
dotnet publish src/MyriagentHub/MyriagentHub.csproj -c Release -r win-x64 --self-contained -o <輸出資料夾>
```

- 服務：`MyriagentHub.exe --data C:\ProgramData\MyriagentHub`（`sc.exe create … obj= "NT SERVICE\MyriagentHub"`，見設計 9.5）。以服務執行時工作目錄是 `C:\Windows\System32`，內容根目錄固定是程式所在資料夾。
- 主控台執行時用 `MyriagentHub.exe admin stop --data <資料根>` 正常結束（Ctrl+C 也可以）；強制結束在 file 憑證模式會在使用者金鑰存放區留下一個孤兒金鑰檔。

## 資料根（`--data`，正式 `C:\ProgramData\MyriagentHub`）

| 路徑 | 內容 |
|---|---|
| `config\appsettings.json`、`config\appsettings.Production.json` | 正式設定（在發佈資料夾外，換版不會被覆蓋；命令列參數仍然最優先） |
| `db\hub.db` | SQLite（WAL、`synchronous=FULL`、單一寫入者、**不用連線池**＋服務執行期間一條保活連線，見 `Data/SqliteSetup.cs`）：Identity（Version3；擁有者帳號 admin 與密碼雜湊）、`devices`、`pairing_requests`、`browser_sessions`、`audit`、`kv`、`app_releases`（U2：收進來的桌面版，檔案在 `app-releases\<release>\`）。第 3 階段改成帳號密碼、伺服器授權之後不再寫入的 `AspNetUserPasskeys`、`owner_key_log`、`devices.registry_statement`／`registry_assertion`（新的列是空字串）、`browser_sessions.passkey_credential_id`（新的列是 null）留到下一版的遷移才移除：部署回滾不還原資料庫，上一版還要讀得到，所以從第 2 階段升級的伺服器保留舊列（舊的 `owner` 帳號、它的 passkey、`owner_key_log` 的 genesis）。服務執行中一直有 `-wal`、`-shm`，正常停止後只剩 `hub.db` |
| `db\sync.db` | 同步資料（`Data/SyncSchema.cs`，版本在 `server_meta.schema_version`，目前 4）：`server_meta`（instanceId、restoreGeneration）、`device_streams`、`events`（持久事件，只增不改）、`rejected_events`、`logical_accounts`、`account_links`、`usage_latest`／`usage_points`／`usage_pollers`、`device_cli`、`prefs`；v2（WP4.1）加第 4 階段的 `conversations`、`items`、`conversation_runtime`、`commands`、`media_refs`、`blobs`、`blob_refs`（只建表，WP4.2／WP4.5／WP4.7 才寫；只加不改，回滾到 v1 的程式不必還原資料庫）；v3（WP4.2 步 12A）只加 `conversation_unread`（伺服器記的網頁未讀，有列就是未讀；第 3 階段的舊版 開 v3 的檔只記「比程式新」的警告、不動結構，可以回滾）；v4（app 自我更新 U2）只加 `device_app`（各電腦 app 的更新狀態，`app.state` 的最新一份；只加表，回滾到 v3 的程式同樣不必還原）。WP4.2 寫 `conversations`、`items`、`conversation_runtime`、`conversation_unread` 與 `server_meta` 的 `conv_materializer_version`（物化的版本，資料不是結構：和程式的不相等就在背景重建）。只有 `SyncWriter` 一條長寫入連線（WAL、`synchronous=FULL`、提交後才確認），讀取各自開唯讀連線；同樣不用連線池。每日備份與 `admin restore` 都含它 |
| `secrets\dp-keys\` | Data Protection 金鑰（DPAPI LocalMachine 加密） |
| `secrets\acme-account.dpapi` | ACME 帳戶金鑰（DPAPI LocalMachine＋用途 entropy、受保護 DACL） |
| `secrets\hub-identity.dpapi` | 伺服器身分金鑰（ECDSA P-256，DPAPI LocalMachine）：Hello 的 `hubSig`，裝置配對時釘選它的公鑰。遺失時重建的新金鑰要每台在本機確認指紋 |
| `secrets\tls\*.pfx.dpapi` | file 模式的 TLS 憑證（store 模式改放服務身分的 `CurrentUser\My`） |
| `status\running.json` | `state`（running／stopping／stopped）、`version`、`build`、`pid`、時間 |
| `status\selftest.json` | 唯讀隔離自檢結果（`ok`、`failures`、每一項的 `expect`／`outcome`／`pass`） |
| `status\trust.json` | 信任資訊（伺服器身分指紋與公鑰、名冊的版本）：啟動與名冊變更時寫，`admin status` 印出 |
| `status\service.lock` | 執行期間獨佔開著：同一資料根只能有一個實例，`admin restore` 靠它確認服務已停止 |
| `status\drain.json` | 部署收件匣的 drain 處理完的回覆：`id`（安裝腳本比對）、`ok`、`state`、`release`、`retryAfterSec`、時間 |
| `status\app-release.json` | app 發佈收件的回執（U2）：`id`（送件腳本比對）、`ok`、`release`、`message`（ASCII 原因代碼，例如 `ingested`、`already_ingested`、`signature`、`zip_entries`）、`processedAt`；不回顯收件匣的任何內容，中文原因只寫服務記錄 |
| `admin-inbox\`、`admin-inbox\results\` | 管理收件匣與結果檔（只有本機管理員寫得進來，所有管理指令都認） |
| `admin-inbox\deploy\` | 部署收件匣：runner 帳戶能寫的兩個子資料夾之一（另一個是下一列的 app 發佈收件匣），**只接受 drain**；其他內容一律拒絕、記稽核（actor `deploy`、`denied`）後刪除 |
| `admin-inbox\app-release\` | app 發佈收件匣（U2）：runner 帳戶（`Setup-Runner.ps1` 授權）先放 `myriagent-<release>.zip`、`.manifest.json`、`.manifest.sig`，最後原子寫 `request.json`（`{id, release, requestedAt}`；服務不看 `requestedAt`，沒有過期）。服務只認 `request.json`、處理期間一直開著它（不允許刪除或改名），在 `app-releases\.partial-<id>\` 的副本上驗證，簽章只認組件內建的 `ReleaseKeys.Production`；檔案監看加每 `Releases:InboxPollSeconds` 秒輪詢 |
| `app-releases\<release>\` | 收進來的發佈（zip、manifest 原文、簽章檔），對應 hub.db 的 `app_releases`（`state` 是 `available`、`withdrawn` 或 `pruned`）。保留 `Releases:KeepAvailable` 個可用版本；其餘的與撤回的先改標資料列再刪資料夾，下載中的 zip 開著刪不掉就留到下一輪。`.partial-<id>\` 是收件中的副本，啟動時刪掉；名稱是合法 release、但資料表沒有對應列的資料夾啟動時也刪掉（還原舊備份後，備份之後收的版本因此被刪，見 `docs/hub-setup.md` 第 8 節）。每日備份不含這個資料夾 |
| `backups\daily\yyyyMMdd[-HHmmss]\` | `hub.db` 與 `sync.db` 的快照＋`manifest.json`（筆數、大小、SHA-256、integrity_check）；`backups\pre-restore-*` 是還原前被換下的資料庫。WP4.7 起另有 `blobs-inventory.txt`（快照的 `blobs` 表：每列 `sha size kind`、依 sha 排序、LF）與 manifest 的選用欄位 `blobCount`、`blobBytes`、`blobInventorySha256`（版本號照舊是 1）；**blob 本身不備份**（和本體同一顆 C:，D18） |
| `blobs\{sha 前 2 碼}\{sha}` | 內容定址的 blob（WP4.7 D16）：項目欄位 blob（`item-body`）、縮圖（`thumb`）、瀏覽器上傳（`upload`）；檔名就是內容的 SHA-256，寫入一律 `tmp\blob-*` → 驗雜湊與長度 → 改名，之後不再改。正本是 sync.db 的 `blobs`／`blob_refs` 表（沒有列的檔是孤兒，每日清理對帳刪掉） |
| `cache\thumbs\` | 媒體轉送得到的縮圖快取（WP4.7 D30，檔名是媒體鍵＋尺寸；LRU，上限 `Hub:Media:ThumbCacheBytes` 2 GB）：可以整個刪掉，下次再從來源電腦取 |
| `diag\myriagenthub-yyyyMMdd.log` | JSON 行記錄（UTF-8、每日一檔、50 MB 上限、保留 14 天） |
| `tmp\` | 還原的暫存副本（用完即刪）；服務的 TMP／TEMP 指到這裡（`Setup-MyriagentHub.ps1`）。`preflight` 不用它，副本放執行者自己的 `%TEMP%`。WP4.7 起 blob 與上傳收到一半的 `blob-*`、縮圖快取的 `thumb-*` 也在這裡（和 `blobs\` 同一個磁碟區才能原子改名；超過 1 小時的由每日清理刪、啟動時也清一次） |

## 設定（`Hub` 區段，完整清單見 `Hosting/HubOptions.cs`）

| 鍵 | 預設 | 說明 |
|---|---|---|
| `ServerDomain` | `hub.example.com` | 對外網域（明確設定，不從 Host 推算） |
| `PublicOrigin` | `https://{ServerDomain}[:{HttpsPort}]` | Origin／CSRF、CSP 的 wss 都只認它 |
| `HttpsPort` | 443（Development 5443） | 0＝由系統挑選（只能搭配明確 IP，測試用） |
| `BindAddress` | `any`（Development `localhost`） | `any`＝`[::]` 雙堆疊；`localhost`；或明確 IP |
| `Tls:CertStore` | Production `store`、其他 `file` | `store`：每張憑證只匯入一次到 `CurrentUser\My`、以指紋載入 |
| `Acme:Enabled` | `false` | 正式環境由資料根設定打開；`Directory`／`StagingDirectory`／`SkipStaging`／`Email`／`Profile`（預設 `classic`）／`DirectoryTrustPem`（只給本機 Pebble） |
| `Backup:DailyAt` | `03:30` | 本機時間；`KeepDaily` 7、`KeepWeekly` 4、`MaxTotalBytes` 100 GB（`backups\daily\` 合計）、`MinFreeBytes` 40 GB（備份之後至少要剩的可用空間，不夠先刪最舊的、還是不夠就不備份） |
| `SelfTest:Enabled` | 只有以服務執行時 | `MustNotWrite`／`MustNotList`／`MustNotReadFiles` 可覆寫預設清單；`IncludeSqlServerFolders` |
| `Session:IdleHours`／`AbsoluteDays`／`RecentAuthMinutes` | 12／7／5 | 伺服器端工作階段與近期驗證 |
| `RateLimits:*` | 見 8.5 | 匿名以 IP（IPv6 /64）分區；已認證不受 IP 限制。`LoginPerMinute` 10（登入、再驗證、改密碼；未登入的每 IP、已登入的每工作階段）、`LoginPerHour` 50、`FailuresBeforeBackoff` 20、`BackoffMinutes` 15（密碼錯誤的退避：未登入以 IP、已登入以工作階段分區） |
| `RateLimits:BrowserHubAnonymousPerMinute`／`BrowserHubPerSessionPerMinute`／`BrowserCmdPerSecond`／`BrowserCmdBurst` | 30／60／20／50 | 瀏覽器 hub 的 negotiate 與 WebSocket 升級（匿名每 IP、已登入每個工作階段，每分鐘）；`Cmd` 與 `ClientState` 共用的權杖桶（每個工作階段每秒、突發） |
| `Browser:MaxConnectionsPerSession`／`MaxConnections` | 8／32 | 瀏覽器 hub 的連線上限（超過時以 `too_many_connections` 關閉） |
| （常數，不能設定）瀏覽器 hub 的單則訊息 | 128 KB | `BrowserRegistration.MaxMessageBytes`（收與傳輸緩衝；WP4.5 由 64 KB 放寬，`wwwroot/transport-hub.js` 的 `MAX_FRAME_BYTES` 同值），超過時關閉整條連線。`Cmd` 的字串值讀名稱之前 ≤16,000、屬性名 ≤4096；轉送給電腦的指令另限本文 ≤`MaxBodyBytes`（96 KB），其他指令與 `ClientState` 照 4096（`MessageGuard`，web-protocol 第六節第 11 點） |
| `Browser:SessionSweepSeconds`／`PushIntervalMs`／`RefreshSeconds` | 30／500／15 | 工作階段巡檢（閒置與絕對期限、活動續期）的間隔；閘道讀快照最多每 500 ms 一次、沒有變動通知時每 15 秒保底重讀 |
| `Browser:SendTimeoutSeconds`／`OutboxMaxMessages`／`OutboxMaxBytes` | 15／500／2 MB | 每條連線的送出佇列：單則送不出去就中斷；超過上限清空並重送 `init`，60 秒內第 3 次中斷 |
| `MemoryFuse:WarnMb`／`KillMb`／`KillAfterMinutes` | 400／700／5 | 超過上限持續 5 分鐘以結束碼 3 退出（服務要設 `sc.exe failureflag MyriagentHub 1` 才會被 SCM 重啟） |
| `IdleTrim:Enabled` | `true` | 閒置修剪（S9 的閒置記憶體，設計 14.20；`Ops/IdleTrimRules.cs`）：最後一次活動之後靜置 30 秒、沒有請求或 hub 呼叫在處理時，釋放已編譯的 DLP 樣式（約 17 MB，之後第一次遮蔽時重建）並做一次阻塞、壓縮的積極回收（含大型物件堆積），把負載留下的死物件與空隙還給系統。活動是 HTTP 請求（`/healthz`、`/readyz` 與 `/hubs/*` 的連線本身除外；媒體轉送與上傳都是請求，處理中的不回收）與 hub 呼叫（裝置的 `Heartbeat`、`UsagePublish`、`RefreshAccessToken` 不算）。每段閒置最多一次，而且要有東西可修剪：啟動之後第一次，或 GC 已提交的空間比上一次回收之後多 16 MB 以上，靜置滿就做；只有 DLP 樣式已編譯的，要距上一次回收滿 10 分鐘（`MinDlpOnlyTrimInterval`；平常使用時回合之間常停超過 30 秒，不讓每個回合之後都積極回收一次），不到就等到滿；同一段閒置裡之後只在已提交的空間又多 16 MB 以上、距上一次滿一小時時再回收。每次記一行 `閒置修剪：…`（回收前後的已提交空間與耗時），`process.idleTrims` 是次數。測試主機關掉（同一個測試程序裡的主機共用 GC 與 DLP 樣式） |
| `Sync:MinAppVersion` | `0.1.0` | 裝置 app 的最低版本：低於它的 Hello 回 `protocol`（桌面提示更新），在 Hello 宣告了 `update1` 的（U3 起的桌面）改給只更新的連線（sync-protocol 1.4：裝置 hub 只放行換存取權杖與心跳，裝置的 REST 端點照常可用、含下載新版）；`/pair/start` 低於它一律回 `protocol`（配對不開放只更新）；HelloResponse 帶它。格式不對時啟動失敗（`AppVersionRules`） |
| `Media:QuotaBytes`／`AlertPercent`／`RefusePercent`／`MinFreeBytes` | 200 GB／80／95／40 GB | blob 的配額與磁碟保護（WP4.7 D17；`Media/MediaOptions.cs`）：用量到 80% 是 `blob_quota` 告警、到 95% 或資料根磁碟的可用空間 <40 GB 時新的 blob 一律 507（`blob_refuse` 告警；事件與對話照收）。只有上限、沒有開關；啟動與 `admin preflight`（`config.media`）以 `MediaOptionsRules.Validate` 驗證 |
| `Media:MaxItemBlobBytes`／`MaxThumbBytes`／`MaxUploadBytes`／`MaxPendingUploadBytes`／`ThumbCacheBytes`／`FirstByteTimeoutSeconds` | 4 MB／512 KB／512 MB／4 GB／2 GB／15 | 項目欄位 blob（D14：渲染器讀一個 blob 約要 6 倍記憶體，型別上限 64 MB 會撐爆 GC 的 512 MB）、縮圖、單一上傳、待處理上傳合計、縮圖快取的上限；媒體轉送等來源電腦第一個位元組的秒數（逾時 504） |
| `Media:MaxConcurrentRelays` | 64 | 所有來源電腦合計同時的媒體轉送（每台另有常數 20 個，`MediaRelayRules.MaxConcurrentPerDevice`），滿了和每台的上限一樣回 503 `busy`。容量上限，不是限流：瀏覽器讀得慢時一個轉送佔約 1.1 MB（管線 1 MB＋電腦 PUT 連線的傳輸層最多先收 64 KB〔`HubApplication.TransportReadBufferBytes`，Kestrel 預設 1 MB，設計 14.20〕；傳輸層還是 1 MB 時約 2 MB，沒有合計上限的 10 台 × 20 個逼近 GC 的 512 MB），實測見 `docs/hub-setup.md` 的「記憶體」 |
| `Logs:Declare`／`Handoff:Declare` | `false`／`false` | 第 5 階段的休眠開關（`Logs/LogOptions.cs`、`Handoff/HandoffOptions.cs`；sync-protocol 16.1）：打開才在 HelloResponse.features 的 `tok1` 之後宣告 `log1`、`hand1`（`hand1` 要兩個都開）。WP5.3 結束時改 `Logs:Declare` 的預設、WP5.10 改 `Handoff:Declare`；正式環境在那之前不要手動打開（WP5.1 只有宣告，端點與方法還沒有） |
| `Deploy:DrainMaxSeconds` | 300 | 部署收件匣的 drain 最多維持幾秒，到期時服務仍在執行（不在停止中）就自動解除（安裝腳本的停止要求沒送到服務時不會一直排空；服務收到停止要求後就不解除，停止逾時要管理員啟動）；只收 60～3600（啟動與 `admin preflight` 驗證），要遠大於安裝腳本的 `DrainWaitSec` 10＋`StopTimeoutSec` 30。管理收件匣根目錄的 drain 沒有期限 |
| `Releases:InboxPollSeconds`／`KeepAvailable`／`MaxZipMb`／`MinFreeDiskMb` | 30／3／256／2048 | app 發佈收件匣（U2）：輪詢間隔（1～3600 秒；另有檔案監看）、保留幾個可用版本（1～20）、zip 上限（1～4096 MB）、收件複製之後至少要留的磁碟空間（MB，≥ 0）。啟動與 `admin preflight` 驗證。沒有任何設定或指令能加信任的發佈金鑰：收件只認組件內建的 `ReleaseKeys.Production`（U2 設計 D1） |

## 擁有者帳號

單一擁有者帳號 `admin`，首次啟動以預設密碼 `changeme2026` 建立（`Auth/OwnerAccountService.cs`；稽核 `owner.created`），不強制更換，登入後可在網頁「改密碼」。密碼以 Identity 的 PBKDF2 雜湊存在 hub.db；規則只看長度（8～128 字、不可含控制字元，`Auth/PasswordRules.cs`）。不做帳號全域鎖定（會讓任何人鎖住擁有者），改以 IP／工作階段分區的退避擋暴力猜測。從第 2 階段 passkey 版升級時另建 `admin`，舊的 `owner` 帳號、它的 passkey、一次性設定碼的 kv 狀態與 `setup\` 資料夾**一律不動**（部署失敗的自動回滾只換回程式、不還原資料庫，上一版要照樣能以 passkey 登入；下一版的遷移才刪）。這一版不認 `owner`：登入只找 `admin`，上一版以 `owner` 登入的工作階段在 `SqliteTicketStore` 當成沒有。忘記密碼：在主機上 `MyriagentHub.exe admin reset-owner`。是否仍是預設密碼記在 `OwnerAccountService.IsDefaultPassword`（WP4.5：啟動時以存下的雜湊比對一次，改密碼、`reset-owner` 時更新，值變了觸發 `PasswordStateChanged`），給遠端指令信封的 `auth.defaultPassword` 與瀏覽器的 `control` 用；預設密碼只警告、不擋。

## 端點

匿名允許清單（`tests/MyriagentHub.Tests/Security/EndpointAuthorizationTests` 斷言正好是這幾個；其他端點一律要認證，未對應的 `/api`、`/hubs`、`/m`、`/t`、`/f` 路徑匿名也是 401，探測不出哪些端點存在）。**其他未對應的路徑**（瀏覽器自動要的 `/favicon.ico`、`/robots.txt`、打錯的網址）不論是否登入一律 404（`Hosting/FallbackEndpoints.cs` 的 `MapFallback`；沒有它時授權的 FallbackPolicy 對「沒有端點」的請求也生效，匿名會得到 401）；登入頁與 `/app/` 的網頁都以 `<link rel="icon">` 明確指定圖示 `/app/favicon.ico`（與桌面 app 圖示同一張圖，`tools/make_icon.py` 產生；`/app/*` 匿名可取），瀏覽器不會去要 `/favicon.ico`：

| 方法 | 路徑 | 說明 |
|---|---|---|
| GET | `/`（＝`/auth.html`）、`/auth.js`、`/authrules.js`、`/auth.css`、`/releases.js`、`/releaserules.js` | 靜態登入頁（`<meta name="myriagenthub-page" content="auth">` 是部署驗證用的固定標記）；配對核准頁（`/#pair[=CODE]`，WP3.5）與版本頁（`/#releases`，app 自我更新 U2）也在這裡。`authrules.js`、`releaserules.js` 是純函式（module），`releases.js` 是版本頁（`auth.js` 以動態 import 載入這兩個版本頁的模組，載入失敗不影響登入與配對核准） |
| GET | `/transport-hub.js`、`/lib/signalr-10.0.11.min.js`、`/lib/signalr-10.0.11.LICENSE.txt` | 瀏覽器版網頁的傳輸層（WP3.5）與 SignalR JS 程式庫（逐位元組取自 npm，`tools/vendor-signalr`）；`/app/` 插入這兩支腳本（`docs/web-protocol.md` 第六節第 2 點）。`/lib/` 底下檔名以 `-主.次.修[.min].js` 結尾的是 `public, max-age=31536000, immutable`，其他靜態檔（含授權檔）`no-cache`、HTML `no-store`（`SecurityHeaderRules.CacheControlFor`）。換版程序見 `tools/vendor-signalr/README.md` |
| GET | `/healthz` | `{"status":"ok"\|"degraded","build":"提交雜湊前 7 碼"}`（發佈時 `-p:MyriagentHubBuild=<7 碼>` 優先，沒帶時取 SourceRevisionId 前 7 碼；與 `status\running.json`、manifest、安裝腳本的健康閘門、workflow 的 verify job 比對同一個值） |
| GET | `/app`、`/app/*` | 瀏覽器版網頁（嵌入的桌面 `src/Myriagent/Web/**`，`Web/AssetServer.cs`；中介程式，不是端點，不在允許清單的斷言裡）：`/app` 302 到 `/app/`（查詢字串照帶）；`index.html` 插入 wwwroot 實際存在的傳輸腳本、根元素的 `data-theme` 只有驗過擁有者 cookie 而且漫遊偏好是 light 才是淺色；強 ETag，`index.html` 是 `no-store`、其他 `no-cache`；不認得的名稱與子路徑 404、非 GET／HEAD 405；嵌入的網頁或 wwwroot 的腳本組合不合法時 `/app/*` 503（其他端點照常）。契約見 `docs/web-protocol.md` 第六節 |
| 任何 | 其他未對應、也不是靜態檔的路徑（`/{**path}`） | 404（匿名與登入後都是） |
| POST | `/api/v1/auth/login` `{userName, password}` | 擁有者登入（帳號不分大小寫、前後空白不算；錯誤一律 401 `login_failed`，回應不分是帳號還是密碼錯；每 IP 每分鐘 10 次、每小時 50 次；連續 20 次失敗退避 15 分鐘，退避期間正確的密碼也回 429） |
| POST | `/api/v1/pair/start`、`/api/v1/pair/token` | 裝置配對（`docs/sync-protocol.md` 2.1；不用 cookie、`.CsrfExempt()`；start 每 IP 每小時 5 次）。請求與回應是 `SyncJson`（與裝置 hub 同一種線上格式），錯誤是 400 `{"error":…}` |

裝置（`Authorization: Bearer chd_…`，只收標頭）：`/hubs/v1/device`（SignalR，只允許 WebSockets、Newtonsoft JSON 協定；Hello／Prove 之前只能交握，sync-protocol 3、4）。`cha_`（Prove 發的 15 分鐘存取權杖）只認 `/api/v1/device/*`，在 hub 與其他 API 一律無效；`/api/v1/device/*` 的端點一律掛 `HubPolicies.DeviceAccess`（只收 `cha_`：`chd_` 403、擁有者 cookie 與匿名 401；WP4.1 D56）。

裝置的 app 下載（U2，`Releases/AppReleaseEndpoints.cs`；sync-protocol 第 15 節）跟下面「媒體與檔案（WP4.7）」的裝置端點一樣只收 `cha_`：權杖不分範圍、不限每台的次數與同時下載數、不寫下載稽核（U2 設計 D21）。成功的 JSON 以 `SyncJson` 序列化（與配對端點相同），錯誤是 `{"error":…}`：

| 方法 | 路徑 | 說明 |
|---|---|---|
| GET | `/api/v1/device/app/releases/latest` | 200 `{release, version, build, manifest, sig: {kid, sig}, zipBytes}`：最新的可用版本（`available` 之中版本最大的）；`manifest` 是 manifest 原文的標準 base64，`sig` 是 `.manifest.sig` 的內容，裝置以組件內建的發佈金鑰自己驗。沒有可用版本 204。排空中照常回應 |
| GET | `/api/v1/device/app/releases/{release}/package` | zip 串流：`application/zip`、`Accept-Ranges: bytes`、強 ETag `"<zipSha256>"`、`Content-Disposition` 帶 `myriagent-<release>.zip`。`Range` 續傳回 206、起點超出檔尾 416（`Content-Range: bytes */<長度>`）、`If-Range` 不符回整檔 200。名稱不是標準的 release 或沒有這一版 404 `not_found`；撤回或清除（`withdrawn`、`pruned`）、檔案不在 410 `gone`；排空中 503 `draining`＋`Retry-After`（排空的建議秒數）。以 `FileShare.Read` 開檔：下載中保存規則刪不掉資料夾（留到下一輪），資料列先改標，所以之後的續傳與新下載立刻 410。開檔失敗時再讀一次資料列（`AppReleaseRules.ClassifyOpenFailure`）：剛被撤回或清除 410 `gone`；仍是可用版本而檔案或資料夾不在 410 `gone`（記錯誤）；其他開檔失敗（防毒或備份獨占開著）503 `unavailable`＋`Retry-After: 30`（記警告），不會變成 500 |

擁有者（cookie＋Origin＋CSRF）：`GET /api/v1/pair/pending?code=`、`POST /api/v1/pair/approve` `{userCode}`（要求近期驗證；配對過的裝置含撤銷的達 256 台時 409 `too_many_devices`）、`POST /api/v1/pair/deny` `{userCode}`、`GET /api/v1/trust`（伺服器身分指紋與名冊版本；sync-protocol 2.2、7）。核准由伺服器授權（設計 14.4）：伺服器建立裝置、名冊版本＋1、廣播 `TrustChanged`。

擁有者（`__Host-myriagenthub` cookie）：`GET /api/v1/auth/me`、`POST /api/v1/auth/logout`、`POST /api/v1/auth/stepup` `{password}`（再輸入密碼；錯誤 403 `password_failed`）、`POST /api/v1/auth/password` `{currentPassword, newPassword}`（改密碼：目前的密碼錯 403 `password_failed`、新密碼不合 400 `password_too_short`／`password_too_long`／`password_invalid`／`password_unchanged`；成功後撤銷其他所有工作階段、回 `{ok, revokedSessions}`）、`GET /api/v1/sessions`、`GET /api/v1/ops/status`。再驗證與改密碼的密碼錯誤以工作階段分區退避（偷到 cookie 也不能拿它猜密碼）。

協作人（多人協作，2026-10-30；`docs/plans/2026-10-30-collaborators.md`、sync-protocol 14.14）：同一個 `__Host-myriagenthub` cookie，票證帶 `myriagenthub:collab`（協作人的 Identity 使用者 id）。`HubPolicies.Owner` 只收帳號 admin（`OwnerAccount.IsOwnerPrincipal`），`HubPolicies.Browser`（擁有者或協作人）只給 `/hubs/v1/browser`、`/m`／`/t`／`/f`（另外逐對話檢查分享）、`PUT /api/v1/uploads/{id}`、`GET /api/v1/auth/me`（多帶 `role`、`displayName`）、`POST /api/v1/auth/logout`、`POST /api/v1/auth/password`（協作人改自己的密碼：只撤銷他自己的其他工作階段）；FallbackPolicy、`Authenticated`、`OpsRead` 都不收協作人。登入回 `{ok, role}`；協作人的工作階段閒置 30 天、絕對 180 天（`Hub:Session:CollaboratorIdleDays`、`CollaboratorAbsoluteDays`），cookie 是持久的。名冊在 hub.db 的 `collaborators`、`collaborator_shares`（`Auth/CollaboratorDirectory.cs`，啟動步驟「協作人名冊」載入記憶體）；擁有者以瀏覽器 hub 的 `collab*` 指令或裝置 hub 的 `CollabCmd` 管理（`Realtime/CollaboratorCommandService.cs`），推送在 `Realtime/CollaboratorFanout.cs` 與 `Realtime/BrowserGateway.Collaborators.cs`。新增擁有者的端點一律掛 `HubPolicies.Owner`（`Security/CollaboratorTests` 逐一列舉端點確認協作人被拒）。
擁有者（cookie＋`Origin` 本站，瀏覽器閘道 WP3.6）：`/hubs/v1/browser`（SignalR，只允許 WebSockets、JSON 協定、不開 stateful reconnect；方法只有 `Cmd`、`ClientState`，伺服器→瀏覽器一律 `Msg`；連線、速率與訊息大小的上限見 `docs/web-protocol.md` 第六節第 11 點）、`POST /api/v1/devices/{id}/revoke` `{reason: "retired"|"compromised"}`（400 `invalid_request`、404 `not_found`、409 `already_revoked`）、`POST /api/v1/devices/{id}/remove`（只收已撤銷的；200 `{ok, conversations}`、404 `not_found`、409 `not_revoked`／`already_removed`、503 `unavailable`；設計 14.22）、`POST /api/v1/accounts/{id}/delete`（404 `not_found`、503 `unavailable`）。
擁有者（cookie；版本頁，app 自我更新 U2，`Releases/AppReleaseOwnerEndpoints.cs`）：`GET /api/v1/app/releases` → `{releases:[{release, version, build, commit, zipBytes, kid, state, ingestedAt, withdrawnAt}], latest, devices:[{deviceId, name, appVersion, lastHelloAt, online, updateOnly, app}]}`（版本依收件時間由新到舊；`latest` 是目前提供給裝置的版本、沒有是 null；電腦不含已撤銷的，`name` 是顯示名稱，`appVersion` 是最近一次 Hello 回報的，`updateOnly` 是在線而且全部連線都是只更新，`app` 是 `app.state` 的最新一份、沒有是 null）；`POST /api/v1/app/releases/{release}/withdraw`（Origin＋CSRF；不讀本文）→ `{release, state:"withdrawn", alreadyWithdrawn}`（release 不是標準寫法 400 `invalid_release`、沒有這一版 404 `not_found`；撤回成功寫稽核 `app.release.withdrawn`〔actor owner、工作階段短 id、IP〕，先前就撤回的回 `alreadyWithdrawn:true`、不再寫；撤回的是目前提供的版本時 latest 立刻改成前一個可用版本（沒有就是 null），撤回較舊的版本時 latest 不變；套件端點對撤回的版本回 410；資料夾由下一輪保存刪除，下載中的照樣傳完）；`GET /api/v1/app/releases/{release}/package`（擁有者以 cookie 下載 zip：跟裝置的套件端點同一段程式，Range、ETag、410、503、404 都一樣）。撤回只要登入的工作階段、不要求近期驗證，擁有者下載不寫稽核（使用者 2026-10-19 決定，U2 設計 D21）。
要求近期驗證（5 分鐘內輸入過密碼：登入或再驗證，否則 403 `{"error":"step_up_required","stepUp":true}`）：`POST /api/v1/sessions/{id}/revoke`、`GET /api/v1/audit?before=&limit=`、`POST /api/v1/pair/approve`、`POST /api/v1/devices/{id}/revoke`、`POST /api/v1/devices/{id}/remove`、`POST /api/v1/accounts/{id}/delete`（`PasswordLoginTests` 斷言正好是這 6 個）。

`GET /readyz`：擁有者、裝置（`Authorization: Bearer chd_…`）或本機迴路；排空中回 503。內容同 `ops/status`（排空、版本、記憶體、CPU、連線、資料庫與 WAL 大小、磁碟、TLS 與 ACME、備份、自檢、稽核鏈、5xx、登入失敗數、用量查詢次數 `usagePollsPerHour`、伺服器對話的計數 `conversations`、Windows Update 待重開機、告警代碼）。`drain` 是排空的來源與期限：`{"source":"admin"|"deploy","since":…,"expiresAt":…,"retryAfterSec":…,"requestId":…}`（`admin`＝管理收件匣根目錄、`expiresAt` 是 null；`deploy`＝部署收件匣、到 `expiresAt` 自動解除），沒有排空時是 null；狀態碼與內容取自同一份排空狀態。`usagePollsPerHour`（S12）是 `{"max":…,"accounts":[{"logicalAccountId":"la_…","count":…}]}`：每個邏輯帳戶最近一小時（滑動窗，剛好滿一小時的不算）**被採用的帶 term 用量上傳**次數，當成官方用量查詢次數的代理值（負責查詢那台每查一次就上傳一次），`accounts` 依 id 排序、0 次的不列、只有邏輯帳戶 id（不含 email），`max` 是其中最大的（沒有時 0）。不算不帶 term 的上傳（對話中的 rate limit 事件）、沒被採用的（舊 term、不是負責的那台），也看不到裝置查了卻沒上傳的；計數只在記憶體，服務重新啟動就歸零（`uptimeSeconds` 不到 3600 時只涵蓋啟動之後），每個帳戶最多記 3600 筆。裝置本機的查詢次數仍以長情境 `UsageAssignmentTests` 為準。
`conversations`（WP4.2 步 5、D31）只有計數，不放對話 id 與標題（裝置也讀得到 `/readyz`）：`count`、`items`、`deleted`、`recovering`、`rebuilding`／`rebuildPending`（物化表重建中、等著重建的對話數）、`unread`、`overlayBytes`、`liveDropped{rate,dlp,notOwner,policy,final,stale}`、`outboxOverflows`、`rejected{notOwner,fenced,deleted,invalid,tooLarge}`（沒有 `dlp`：伺服器遮蔽後接受）、`remasked{events,live}`、`conversationEvents`／`conversationEventBytes`（sync.db 裡被接受的對話事件；啟動前的由背景補，`conversationEventsBaselined` 是 true 才含）、`syncDbBytes`／`syncWalBytes`、`syncDbLevel`（主檔 ≥`Hub:Conversations:SyncDbWarnBytes`〔2 GiB〕是 `warn`、≥`SyncDbCriticalBytes`〔8 GiB〕是 `critical`，只是告警）、`devices[{deviceId,owned,recovering,items,maxSeq,lastEventAt}]`（觀察回補）。`unread`、`overlayBytes`、`liveDropped`、`outboxOverflows`、`remasked.live` 由 WP4.2 的後續步驟接上，之前一律是 0。
`process` 的私有記憶體、工作集與執行緒數是**查詢當下取樣**（`sampledAt`）；`cpuPercent` 是距上一次取樣（記憶體保險絲每 `MemoryFuse:SampleSeconds` 秒一次，或上一次查詢）的平均，`cpuWindowSeconds` 是那段時間（至少 0.2 秒，查得太密時沿用上一次的值）。`gcHeapMb` 是受管理堆積（含上一次 GC 之後的配置與還沒回收的死物件），`gcCommittedMb` 是上一次 GC 結束時 GC 已提交的空間，`idleTrims` 是程序啟動以來閒置修剪的次數（上面的 `IdleTrim:Enabled`）。`tls.name` 是憑證的顯示名稱（主體的 CN；Let's Encrypt 短效憑證與 Pebble 的憑證主體是空的，改用 SAN 的 DNS 名稱），`tls.subject` 照原樣。

媒體與檔案（WP4.7；線上格式見 `docs/sync-protocol.md` 14.3 的「媒體與檔案」，網頁那一側見 `docs/web-protocol.md` 的 Item 媒體網址、`files`、`fileRef`）：
- 擁有者（cookie）：`GET /m/{key}`（原檔，支援 Range）、`GET /t/{key}?s=160|320[&v=]`（縮圖）、`GET /f/{key}`（遠端 `fileFetch` 的檔，文字 ≤2 MB 直接顯示、其他 `attachment`）；`key`＝`{deviceId}-{ref}`。每個請求都查 `media_refs`（用途、期限、對話與政策），不符一律 404；回應一律 `Content-Security-Policy: default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox`＋`nosniff`。`PUT /api/v1/uploads/{uploadId}`（E4，≤512 MB；`Origin`＋`X-Myriagent-Csrf: 1`，**不要求近期驗證**：擁有者 2026-10-19 指示）。
- 裝置（`Authorization: Bearer cha_…`，`/api/v1/device` 群組）：`PUT`／`HEAD /api/v1/device/blobs/{sha}`（E5，項目欄位 blob ≤4 MB，第二道 DLP 命中 422）、`GET /api/v1/device/blobs/{sha}`（E6，網頁上傳的檔，支援 Range）、`PUT /api/v1/device/relay/{relayId}`（E7，回應伺服器的 `MediaRequest`）、`PUT /api/v1/device/thumbs/{ref}`（E8）、`GET /api/v1/device/media/{key}?purpose=media|thumb[&s=]`（E9，複本的媒體經伺服器向來源電腦取）、`GET /api/v1/device/thumbs/missing`（E10）。不用 cookie，PUT 以 `.CsrfExempt()` 標記；`chd_` 在這些端點無效。

所有非 GET 的 `/api` 請求都要 `Origin` 等於 `PublicOrigin` 且帶 `X-Myriagent-Csrf: 1`（否則 403 `forbidden_origin`）；第 3 階段不用 cookie 的裝置端點以 `.CsrfExempt()` 標記。`/hubs/v1/browser` 的協商與升級要求 Origin 必須是本站。

## 管理指令（只能在主機上以管理員執行；沒有任何網路管理端點）

`MyriagentHub.exe admin <指令> [--data <資料根>] [--wait <秒>]`，輸出一個 JSON 物件（非 ASCII 一律 `\uXXXX`，經 ssh 的 cp950 主控台不會亂碼），結束碼 0 成功、1 失敗、2 用法錯誤、3 服務仍在執行（restore）、4 等不到服務處理。

| 指令 | 作用 |
|---|---|
| `reset-owner`（＝`owner-reset`） | 擁有者 admin 的密碼改回預設（`changeme2026`）、撤銷所有瀏覽器工作階段（忘記密碼時用；服務沒在跑時留在收件匣，啟動後處理） |
| `drain`／`undrain` | 部署前排空：`/readyz` 回 503（第 3 階段起廣播 ServerNotice、停收新指令）。這裡（管理收件匣根目錄＝擁有者）的 drain 沒有期限，要 `undrain` 才解除；`undrain` 也清掉部署的排空。排空中新的裝置連線 Hello 回 `draining`、照退避重試，`undrain` 後自己連上（忘了解除，重新連線的裝置就一直連不上、同步停住）。部署腳本不用這個指令，而是以 runner 帳戶寫 `admin-inbox\deploy\`，那種 drain 在 `Deploy:DrainMaxSeconds` 後自動解除（見下方「給部署的對接點」） |
| `backup-now`、`selftest`、`verify-audit` | 立即備份、隔離自檢、驗證稽核鏈 |
| `cert-renew [--force]` | 立即做一輪憑證檢查並等結果（最多 20 秒）：清掉失敗退避、**一律重查 ARI**（忽略上次查詢時間與 Retry-After）、到了續期時點就續期；`--force` 不看續期時點直接續期。ACME 沒啟用或申請失敗時結束碼 1 |
| `stop` | 正常結束（主控台執行用；服務照樣用 `Stop-Service`） |
| `app-releases` | 列出收進來的桌面版（收件時間由新到舊；`data` 的欄位同 `GET /api/v1/app/releases` 的 `releases`），訊息寫目前提供的版本 |
| `app-release-withdraw <release>` | 撤回一個桌面版（同版本頁的撤回；release 是第二個參數、要標準寫法，例如 `0.2.1523-ab12cd3`，不合法時結束碼 2、什麼都不寫）：`data` 是 `{release, state, alreadyWithdrawn, latest}`；找不到結束碼 1；稽核 `app.release.withdrawn`（actor admin）。撤回的是目前提供的版本時 latest 改成前一個可用版本，否則不變。**不能復原，同名的版本也永遠不能再收**（`release_exists`；CLI 沒有確認步驟，打錯版本號只能升版號重新發佈）。服務沒在跑時留在收件匣，啟動後處理 |
| `restore --from <備份資料夾>` | 服務必須已停止；驗 manifest 的 SHA-256 與 integrity_check → 舊檔移到 `backups\pre-restore-*` → 放回 → `restore_generation`＋1、撤銷所有工作階段、寫稽核 |
| `preflight` | 檢查設定、對線上資料庫做唯讀的一致快照並在副本上試跑遷移（不改線上檔案、不寫資料根）。只需要 `db`、`config` 的讀取權與自己的 `%TEMP%`：部署時以 runner 帳戶執行。服務執行中走唯讀連線（讀得到 WAL）；服務已停止、WAL 已併回主檔時以 `immutable=1` 讀主檔（不上鎖：讀前讀後比對主檔的 SHA-256、並確認讀完仍然沒有 WAL，不一致就重來，最多 5 次；讀取本身失敗時也先比對，主檔變了照樣重來，主檔沒變才是真的損毀） |
| `status` | 印出 `running.json` 與 `selftest.json` |

暫時性指令（`reset-owner`、`rotate-hub-identity`、`app-release-withdraw` 以外的）在 CLI 逾時時收回；收件匣裡放超過 10 分鐘的暫時性指令作廢不執行。10 分鐘內的照樣執行，所以停機時留下的指令靠收回：CLI 等不到結果時收回自己的指令檔，安裝腳本等不到 drain 的回覆時也收回；部署收件匣另外只執行本程序啟動之後寫的 drain（`AdminInboxRules.IsFromEarlierInstance`；給上一個程序的要求不會讓剛部署好的新版一啟動就排空）。

## 資料庫遷移

`HubDbContext` 的模型一改就要新增遷移（`Data/Migrations`，只往前、先加欄位、下一版才移除）；`tests/MyriagentHub.Tests/Data/DataTests` 會在模型與遷移不一致時失敗。設計階段工具以測試專案為啟動專案（設計套件不會進伺服器的發佈內容）：

```bash
dotnet build tests/MyriagentHub.Tests/MyriagentHub.Tests.csproj -c Debug
dotnet ef migrations add <名稱> --project src/MyriagentHub/MyriagentHub.csproj --startup-project tests/MyriagentHub.Tests/MyriagentHub.Tests.csproj \
  --output-dir Data/Migrations --configuration Debug
```

產生的檔案有 BOM 與 CRLF，提交前轉成無 BOM 的 UTF-8＋LF（`.editorconfig`）；模型快照 `HubDbContextModelSnapshot.cs` 要在 `Data/Migrations/` 底下（工具會就地更新既有的快照）。

服務在開始聽連接埠之前套用遷移（啟動步驟），並清掉遷移途中被殺留下的 `__EFMigrationsLock`（服務持有 `service.lock`，看到的鎖一定是殘留的）。

## 給部署（WP2.4）與端到端（WP2.5）的對接點

- 部署閘門：`status\running.json` 的 `state`＝`running` 且 `version`／`build` 等於部署版本；`status\selftest.json` 的 `ok`＝`true`（只有以服務執行時才自動跑，主控台執行可用 `admin selftest`）；`/healthz` 不論 ok 或 degraded 都是 HTTP 200，要看 `status` 欄位。
- 部署收件匣（介面總表見 `docs/hub-setup.md` 第 11 節）：安裝腳本以 runner 帳戶寫 `admin-inbox\deploy\<id>.json`（`{"command":"drain","id":…,"release":…,"retryAfterSec":30,"requestedAt":"ISO 8601"}`，先寫 `.tmp` 再改名；`requestedAt` 也接受 Unix 毫秒），服務只接受 drain，處理完寫 `status\drain.json` 回同一個 id（`requestedAt` 早於本程序開始時間的是寫給上一個程序的，記警告後刪除、不執行；安裝腳本等不到回覆就收回要求）；其他管理指令只認 `admin-inbox\` 根目錄。部署的 drain 在 `Deploy:DrainMaxSeconds`（預設 300 秒）後、服務若仍在執行（不在停止中）就由 `DrainExpiryService` 自動解除（稽核 `server.drain`，actor `system`；記警告），部署排空中再來一次就重設期限；擁有者的排空中收到部署的 drain 維持擁有者的、不加期限（規則在 `Ops/DrainRules.cs`）。`admin preflight` 以 runner 身分執行（只讀 `db`／`config`、只寫 `%TEMP%`）。SCM 停止時主機最多等 25 秒（`HostingRules.ShutdownTimeout`），程序在安裝腳本的 30 秒停止逾時內結束。
- 唯讀隔離自檢的預設清單照設計 9.3，但**不含 `C:\inetpub\wwwroot`**（IIS 預設站台的公開內容，預設 Users 可讀；設計的「實作校正（2026-10-11）」）；`applicationHost.config` 照樣「必須讀不到」。SQL Server 的資料、記錄與備份資料夾（登錄檔讀出，Hub 主機上是 `D:\SQLData`、`D:\SQLLog`、`D:\SQLBackup`）照樣「必須列舉不到」：擁有者依 `docs/hub-setup.md`「擁有者要先做的事：收緊 SQL 資料夾權限」收緊之前這幾項不會通過、部署閘門失敗，這是預期行為；程式不改任何 ACL。寫入嘗試只建立檔案（不建資料夾）。
- 記憶體保險絲以結束碼 3 結束（同時設 `ServiceBase.ExitCode`：以服務執行時 SCM 只看它；60 秒內停不下來才直接結束程序），靠 `sc.exe failureflag MyriagentHub 1` 才會被 SCM 重啟（`Setup-MyriagentHub.ps1` 會設，`Test-Hub 主機Scripts.ps1` 會檢查）；Windows 事件記錄要先由管理員建立 `MyriagentHub` 事件來源。
- 端到端：`auth.js` 在 `window.__hubProbe` 提供 `ready`、`view`（login／account／pair／releases）、`violations`（CSP 違規）與 `login(userName, password)`、`logout()`、`stepUp(password)`、`changePassword(current, next)`、`revokeSession(id)`、`me()`、`releases()`（版本頁目前畫出的 `{releases, latest, devices}`，還沒載入過是 null）、`tryInlineScript()`。
- ACME 端到端（Pebble）：`Hub:Acme:Enabled=true`、`Directory`＝`StagingDirectory`＝Pebble 目錄或 `SkipStaging=true`、`DirectoryTrustPem`＝Pebble 的根憑證 PEM、`HttpsPort`＝Pebble 的 TLS-ALPN 驗證埠；`admin cert-renew` 清掉失敗退避、強制重查 ARI（忽略上次查詢時間）並等這一輪做完回報（`--force` 不看續期時點直接續期），結果也看 `/readyz` 的 `tls` 與稽核 `tls.certificate`。

## 同步核心（WP3.2）的結構

| 模組 | 內容 |
|---|---|
| `Data/SyncSchema.cs`、`Data/SyncWriter.cs` | sync.db 結構與唯一寫入者：有工作就開交易、把佇列裡已有的一起提交（每個工作一個 SAVEPOINT）；`WriteAsync` 的 Task 在提交（fsync）之後才完成。`SyncWriteContext.AfterCommit` 登記提交後才做的事（通知、廣播、稽核），回滾到 SAVEPOINT 時以 `AfterCommitMark`／`DiscardAfterCommit` 丟掉 |
| `Sync/EventLog.cs` | 上行批次（`OutboxRules` 的判斷、被擋的寫 `rejected_events`、整批錯誤 gap／seq_regression）與拉取（依類別或串流的游標、前綴保證、位元組上限） |
| `Sync/Catalog.cs` | 帳戶目錄、漫遊偏好、裝置名稱、CLI 狀態與 app 的更新狀態（`app.state` → `device_app`）的物化表，以及伺服器寫的事件（`account.upsert`、`device.upsert`、`prefs.set` 等）；全部在呼叫端的寫入交易內 |
| `Sync/UsageCoordinator.cs`、`Sync/UsageRules.cs` | 共享用量的指派（term）與上傳的驗收（sync-protocol 6.4） |
| `Sync/SyncNotifier.cs` | 最大 seq 與 `EventCommitted(cls, seq)`（提交後觸發） |
| `Realtime/DeviceHub.cs`（`partial`）、`DeviceHubRuntime.cs`、`DeviceHubRules.cs`、`DeviceBroadcaster.cs`、`ConnectionRegistry.cs` | 裝置 hub：交握、每條連線一個批次幫浦（有上限的佇列、依序提交、提交後送 BatchAck）、心跳、權杖輪換；`Notify` 每 200 ms 合併、`TrustChanged`、`ServerNotice`（排空開始時的 `shutdown`、收件收進新版本的 `appRelease`）、`UsageAssign`；排空中的 Hello 回 `draining`、HelloResponse 帶最新可用版本 `latestApp`；誰在線（`PresenceChanged`）。方法允許清單與 Hello 的功能協商（WP4.1）見下面「給其他工作包的接手點」 |
| `Pairing/*` | 配對（device flow）、核准（伺服器授權）、撤銷的核心 |
| `Trust/*` | 伺服器身分金鑰、名冊（伺服器發布）與 `GetTrust`、`status\trust.json` |
| `Auth/AccessTokens.cs` | `cha_` 存取權杖（只在記憶體，服務重啟後全部失效，裝置重連時 Prove 會再發） |

本機的 S8 soak（`tests/MyriagentHub.Tests/Realtime/SoakTests`）以子程序執行 MyriagentHub.exe、串流中強制結束並重啟，驗證確認過的事件 0 遺失、0 重複；改到 `SyncWriter`、`EventLog`、`DeviceHubRuntime` 的批次幫浦時它會一起跑（`dotnet test` 就有，約 11 秒）。

## 伺服器對話（WP4.2）的接收、物化與營運

**沒有功能開關**（答 1）：合併部署就生效，`conv1`、`live1` 無條件宣告在 HelloResponse 的 `features`（`RealtimeRegistration`），`Hub:Conversations`（`ConversationSettings`）只有上限參數，啟動與 `admin preflight`（`config.conversations`）以 `ConversationSettingsRules.Validate` 驗證。沒有任何裝置發布對話時，伺服器的行為與 第 3 階段的舊版 相同（只多無害的新增：宣告的功能、ops/status 的計數、v3 的空表）。減量與停用的作法見 `docs/hub-setup.md` 第 6 節。計畫、決定（D1～D47）與實作紀錄在 `docs/plans/2026-10-19-wp4.2-server-conversations.md`，與設計的差異見設計 14.12，線上的規則見 `docs/sync-protocol.md` 14.2、14.3。

| 模組 | 內容 |
|---|---|
| `Sync/EventLog.Conversations.cs`、`ConversationPrecheckRules.cs`、`ConversationDlpRules.cs`、`ConversationRemaskRules.cs` | 上行（D3、D8、D44）：純函式的預先檢查在 `WriteAsync` 之前、呼叫端的執行緒上做完（外框、解碼與大小、SHA-256、`Validate`、第二道權杖 DLP；命中就再遮蔽、重新序列化後再驗一次，仍命中回 `invalid`），寫入者裡只查表授權（`not_owner`→`fenced`→`deleted`→政策）、寫入與物化。再遮蔽過的事件存遮蔽版，`payload_sha256` 仍是裝置送的（冪等身分） |
| `Sync/ConversationApplyRules.cs`、`ConversationMaterializer.cs`、`ConversationFaultRules.cs` | 物化（D4、D5、D9、D10、D16、D34、D37）：在寫入者的交易裡、寫進 `events` 的同一個 SAVEPOINT 更新 `conversations`、`items`、`conversation_runtime`；未知對話的 created／meta 當成建立、其他回 `invalid`；降級與刪除同一個交易清除內容（`PurgeContent`），刪除留墓碑列；毒事件只回滾那一筆 |
| `Sync/ConversationDirectoryCache.cs`、`ConversationChangeFeed.cs`、`ConversationCounters.cs` | 目錄（`IConversationDirectory.Find`，O(1)、提交後才看得到）、變更通知（有界佇列，滿了設整份重讀）與營運計數；都只在 AfterCommit 更新（D12、D13） |
| `Sync/ConversationMaterializer.Rebuild.cs`、`ConversationRebuild.cs`、`ConversationRebuildRules.cs` | 物化表重建（D14）：`server_meta.conv_materializer_version` 和程式的不相等就在背景依對話分塊重建（不擋健康閘門），全部做完才寫版本鍵 |
| `Sync/ConversationPullRules.cs` | Pull 過濾（D17）：已刪除、或目前政策不是 `full` 的對話不回 item 類事件，meta 類照回 |
| `Sync/ConversationRejectAudit.cs`、`ConversationMaintenance.cs` | 拒收與再遮蔽的稽核（每台每 60 秒一筆、只有計數）；每天刪掉 30 天前的對話被拒列（D8、D40） |
| `Sync/ConversationReader.cs` | 物化表的唯讀讀取（清單、標頭、一頁項目、輸入區），一律經 `SyncWriter.OpenRead`，回傳交易看到的最大 seq |
| `Rendering/HubItemRenderer.cs`、`HubItemRules.cs`、`HubMediaUrls.cs`、`ItemSanitizer.cs` | 伺服器的項目渲染（D15、D18～D20、D33）：存的 payload → `ConversationWire.TryReadItem` → 共用的 `ItemRenderer` → 拿掉 `path`、自己組附件與媒體（WP4.7 起有參照的照 `HubMediaUrls.ForRef` 給 `/m/`、`/t/`，沒有參照的改成檔名附件）、卡片加 `requestDigest`／`risk`、`html`／`detailHtml` 經白名單淨化；單筆 ≤256 KB，超過先縮再送佔位 |
| `Ops/ConversationOpsRules.cs`、`Ops/BackupTimeoutRules.cs` | ops/status 的 `conversations` 區塊（只有計數，D31）；備份連線依檔案大小放寬的命令逾時（D41） |
| `Data/SyncSchema.cs`（v3）、`Ops/RestoreService.cs` | `conversation_unread`（D46）；還原時同一個交易把還沒刪除的對話標成 recovering（D16） |

## 遠端指令（WP4.5）的路由、結果與營運

**沒有功能開關**：合併部署就生效，`cmd1` 無條件宣告（`AddHubCommands` 裡的 `AddDeviceHubFeature`）。只有協商出 cmd1 的已證明連線收得到 `CommandDelivery`、送得上結果與輸出；WP4.4 之前的桌面都不宣告 cmd1，所以沒有 WP4.4 的桌面時，現在的網頁畫面不變（它送的轉送指令只有側欄的 `accountRefresh {accountId:null}`，照舊 not_available）。sync.db 結構不變（v2 的 `commands` 表）。計畫、決定（D1～D41）與實作紀錄在 `docs/plans/2026-10-19-wp4.5-command-routing.md`，與設計的差異見設計 14.15，線上的規則見 `docs/sync-protocol.md` 14.4、14.6，網頁那一側見 `docs/web-protocol.md` 的 `commandResult`、`control` 與第六節第 9 點。

| 模組 | 內容 |
|---|---|
| `Sync/CommandRegistration.cs` | `AddHubCommands()`（由 `AddHubBrowser` 呼叫一行，`HubApplication` 不動）：宣告 cmd1、登記下面的服務（都是 `TryAddSingleton`，測試可以換掉）、啟動工作與兩個背景服務 |
| `Sync/CommandRouter.cs`（`partial`：`.FanOut.cs`、`.Drafts.cs`）、`CommandRouteRules.cs`、`CommandMaskRules.cs`、`IssuerLabelRules.cs`、`CommandIssuerResolver.cs` | `IBrowserCommandRouter`：型錄路由 Owner／Device 的指令（WP4.7 合併後 `Deferred` 是空的，檔案指令照常轉送）投影、形狀檢查、帶 `uploads` 的指令（`addUploaded`、`conversationCreate`）在簽章前以 WP4.7 的 `IUploadRegistry.TryVerify` 確認上傳都在這台（`IssueAsync`；不符立即 `invalid`、不寫列）、選目標（Owner：對話目錄的擁有者；Device：`deviceId` 或只連結一台的帳戶）、`la_` 換成那台的本機帳戶 id、選連線（已證明、cmd1、能簽、Order 最大）、簽 `myriagent-hub-cmd-v1` 信封並自驗、寫列提交之後才登記、送出、寫稽核。立即拒絕不寫列；所有電腦登出（`.FanOut.cs`）每台一列 durable；伺服器草稿的指令與第一次 send 的 `conversationCreate`（`.Drafts.cs`，草稿經 WP4.2 的 `IBrowserDrafts`） |
| `Sync/CommandStore.cs`、`CommandStateRules.cs`、`DurableBodyRules.cs` | `commands` 表的讀寫（每個寫入都在交易裡以規則再判斷一次）；狀態轉移、100 秒期限、啟動掃描、7 天與 30 天的純規則；durable 列未簽的內容 |
| `Sync/CommandTracker.cs`、`CommandResults.cs`、`CommandOutputRules.cs`、`Realtime/DeviceHub.Commands.cs`、`CommandMessageRules.cs` | 記憶體的追蹤（cid → 發起的瀏覽器連線、bcid、要核對的對話 id，伺服器重啟就沒有）；裝置 hub 的 `CommandResult`（只收目標電腦、提交之後才轉）與 `CommandOutput`（白名單、投影、WP4.7 的網址改寫、只給發起的連線、不保存）；`commandResult`、toast、`sendRejected` 的訊息 |
| `Sync/CommandMaintenance.cs`、`DurableCommands.cs`、`CommandStartup.cs` | 每秒的期限掃描（非 durable 的 delivered 到 `iat`＋100 秒變 unknown）、每 60 秒的 durable 重掃、每 24 小時刪 30 天前結束的列；durable 的重簽與補送（Prove、提交後、busy／expired 5 秒後同一條連線最多 3 次，之後由 60 秒的重掃再送：重掃不看重送次數，WP4.8 起）；啟動工作（非 durable 未結束的標 unknown、durable 的 delivered 改回 pending、掛上 control 的來源） |
| `Realtime/BrowserGateway.Commands.cs`、`Auth/OwnerAccountService.cs` | 每則 `init` 之後送 `control`（兩個時間窗一律 null、`defaultPassword`），擁有者密碼狀態改變時對所有已送過 init 的連線再送 |
| `Sync/CommandCounters.cs`、`Ops/OpsStatus.cs` | `ops/status` 的 `commands`：`issued`、`rejectedAtServer`、`delivered`、`done`、`rejected`、`unknown`、`expired`、`pendingDurable`（最近一次重掃）、`ignoredResults`、`droppedOutputs`、`resigned`、`aborted`。只有計數、沒有告警（看哪些見 `docs/hub-setup.md` 第 14 節） |

## 媒體、檔案與上傳（WP4.7）

**沒有功能開關**（D1）：合併部署就生效，`media1` 無條件宣告（`AddHubMedia` 裡的 `AddDeviceHubFeature`），`Hub:Media`（`MediaOptions`）只有上限。WP4.7 之前的桌面不宣告 `media1`、也不發媒體參照：它們的附件與代理產出的媒體在網頁上照舊只有檔名，沒有 `/m/`、`/t/` 網址（伺服器只從帶 `ref` 的項目建參照）。發過參照的 WP4.7 桌面不在線或沒有 `media1`（離線、伺服器回滾後再升級還沒重新協商）時，伺服器一則 `MediaRequest` 都不送（送之前問 `DeviceConnection.Allows`）：`/m/` 的影片回 503，圖片有縮圖時以縮圖退回、沒有時 503。sync.db 結構不變（v3；用 WP4.1 建好的 `media_refs`、`blobs`、`blob_refs`）。計畫、決定（D1～D45）、端點（E1～E10）與實作紀錄在 `docs/plans/2026-10-19-wp4.7-media.md`，與設計的差異見設計 14.16，線上的規則見 `docs/sync-protocol.md` 14.2、14.3 的「媒體與檔案」，網頁那一側見 `docs/web-protocol.md`，營運（資料夾、容量、清理、回滾）見 `docs/hub-setup.md` 第 15 節。伺服器的媒體程式全在 `Media/`，`HubApplication` 呼叫 `AddHubMedia`／`MapHubMedia`（`Media/MediaRegistration.cs`）。

| 模組 | 內容 |
|---|---|
| `Media/BlobStore.cs`、`BlobQuotaRules.cs`、`BlobInspectRules.cs`、`RequestBodyLimits.cs` | `IBlobStore` 的實作（以 `AddSingleton` 換掉 WP4.1 的空實作）：內容定址、`tmp\blob-*`（WriteThrough）→ 驗 SHA-256 與長度 → 檢查 → 改名 → 寫 `blobs` 列（D20）；配額 200 GB、80%／95%、磁碟可用空間 <40 GB 拒收（D17）；用量啟動時從 `blobs` 表載入、之後在記憶體 |
| `Media/BlobEndpoints.cs`、`ThumbEndpoints.cs`、`UploadEndpoints.cs`、`UploadRegistry.cs`、`UploadRules.cs`、`MediaApiRules.cs` | E5／E6（項目欄位 blob 的第二道 DLP 以 `SemaphoreSlim(1)` 序列化，D15）、E8／E10（縮圖只驗 PNG／JPEG 魔數、≤512 KB；要政策 full）、E4（瀏覽器上傳 ≤512 MB、待處理合計 ≤4 GB，同 id 同內容 200、不同內容 409）；`UploadRegistry` 是 `IUploadRegistry`（記憶體以 id 查，啟動時從 `blob_refs` 的 `upload` 引用載回，24 小時過期） |
| `Media/MediaRelay.cs`、`RelaySegmentPool.cs`、`MediaEndpoints.cs`（含 `MediaResponder`）、`MediaServeRules.cs`、`MediaReplyRules.cs`、`MediaResults.cs`、`MediaLookup.cs`、`MediaLookupRules.cs` | `/m/`、`/t/`、`/f/`（E1～E3）與 E9：每次請求查 `media_refs`（D7，索引器沒追上時等 ≤2 秒）、向來源電腦送 `MediaRequest`、配對 E7 的回覆、管線直通（不落地、首位元組 15 秒、每台同時 ≤20、合計 ≤64〔`Hub:Media:MaxConcurrentRelays`〕、單段 ≤8 MB、沒帶 Range 時多段接成 200〔≤64 MB〕，D11；管線的 64 KB 區段由 `RelaySegmentPool` 給：並行的轉送之間重用，最後一個借出的區段還回來就全部交給 GC，不進共用的 ArrayPool，設計 14.20）；圖片在來源離線或 403 時以縮圖退回（D29）；裝置連線關閉時那條連線上還在等的轉送立即改成離線 |
| `Media/ThumbCache.cs` | `cache\thumbs\` 的 LRU（2 GB；`/t/` 轉送一次就寫進來，來源離線時照給） |
| `Media/MediaRefIndexer.cs`、`MediaRefIndexRules.cs` | 參照索引（D6、D8；`BackgroundService`）：從 `events` 依游標（`server_meta` 的 `media_index_seq`、`media_index_instance`）增量維護 `media_refs` 與項目欄位 blob 的 `blob_refs`；沒有游標或實例／還原世代不同就從 `items` 分頁重建；刪除、`localOnly`、降級、`conv.itemRemoved` 立即刪參照與縮圖 |
| `Media/MediaMaintenance.cs`、`MediaMaintenanceRules.cs`、`Ops/BackupService.cs` | 每日清理（D19；`MaintenanceService` 在備份之後呼叫、冪等）：刪除門檻＝最舊的備份減 1 天（讀不到備份就不刪）、上傳 24 小時過期、`tmp\` 超過 1 小時的檔、檔與列的對帳；備份另寫 `blobs-inventory.txt`（D18，blob 不備份） |
| `Media/MediaOpsStatus.cs`、`Ops/OpsStatus.cs` | ops/status（與 `/readyz`）的 `blobs`：`count`、`bytes`、`quotaBytes`、`level`（ok／warn／refuse）、`thumbCacheBytes`、`uploadsPending`、`relaysActive`、`relayTimeouts`；告警 `blob_quota`、`blob_refuse`（D42，只加不改） |
| `Media/CommandOutputRewriter.cs`、`CommandOutputRewriteRules.cs`、`CommandOutputFileRules.cs`、`CommandUploadRules.cs` | 遠端檔案指令（D37）：WP4.5 的投影只留已知的鍵（`CommandOutputFileRules`），改寫把參照換成 `/m/`、`/t/`、`/f/` 並登記 `list`（30 天）／`file`（15 分鐘）的 `media_refs`；`fileRef` 的 `expiresAt` 不超過現在＋15 分鐘、有 `error` 時 `url: null`。`CommandUploadRules.Claims` 從 `addUploaded`、`conversationCreate` 的本文取出上傳宣告給指令路由驗證 |
| `Media/MediaSeams.cs` | `ICommandOutputRewriter`、`IUploadRegistry` 與它們的預設（不改寫、一律不通過），`AddMediaSeams()` 以 `TryAdd` 登記：`AddHubCommands`（WP4.5）與 `AddHubMedia` 都呼叫，`AddHubMedia` 再以 `AddSingleton` 換成實作 |
| `Media/MediaOptions.cs`、`MediaOptionsRules.cs`、`Hosting/HubPaths.cs`、`Hosting/SecurityHeaders.cs` | `Hub:Media` 的上限與驗證（啟動與 `admin preflight` 的 `config.media`）；`blobs\`、`cache\thumbs\`（`EnsureCreated`）；媒體回應的 CSP（`MediaHeaderRules.MediaCsp`，含 `sandbox`）與 `nosniff` |

**呼叫點**（WP4.5 的檔、WP4.7 計畫步 13 接上）：
- `Sync/CommandRouter.cs` 的 `IssueAsync`：簽信封之前 `CommandUploadRules.Claims(mapped)`，有宣告就 `IUploadRegistry.TryVerify(claims)`；宣告的形狀不對或驗不過立即拒絕 `invalid`（`CommandRouter.UploadsUnknownMessage`），不寫 `commands` 列、不送。伺服器草稿上的 `addUploaded` 也是 `invalid`（草稿沒有電腦）。
- `Sync/CommandResults.cs` 的 `ReceiveOutputAsync`：`RemoteResultRules.CheckOutput` → `CommandOutputRules.Project` → `await ICommandOutputRewriter.RewriteAsync(deviceId, cmd, conversationId, projected)`，回 null 就丟掉（計進 `commands.droppedOutputs`）。
- 路由只看 `cmd1`：沒有 `media1` 的電腦（WP4.5～4.6 的桌面）照樣收到檔案指令，回 `unsupported`。

## 瀏覽器閘道（WP3.6）的結構

| 模組 | 內容 |
|---|---|
| `Web/AssetServer.cs`、`Web/AssetServerRules.cs`、`Web/PrefsThemeSource.cs` | `/app/` 的網頁供應：嵌入資源啟動時載入一次（插入腳本、深淺兩份 `index.html`、ETag），腳本組合以 wwwroot 實際存在的檔案判斷 |
| `Realtime/BrowserHub.cs`（`partial`）、`BrowserHubFilter.cs`、`BrowserHubMethods.cs` | 瀏覽器 hub：擁有者工作階段才登記（`BrowserCaller`）、方法允許清單、權杖桶限流、hub 活動記號 |
| `Realtime/BrowserConnections.cs` | 連線登記與上限、每條連線的狀態（鎖內）、依工作階段中斷、停機時 `Shutdown()`（先停止登記、再中斷全部）；權杖桶不隨最後一條連線丟，巡檢時丟掉沒有連線而且補滿的（`PruneIdleBuckets`） |
| `Realtime/ClientOutbox.cs`、`BrowserLimitRules.cs` | 每條連線的送出佇列（合併、`init` 屏障、上限、溢出）與送出幫浦（單則逾時就中斷）；權杖桶、連線上限、節流的純函式。`init` 屏障清掉排著的快照類訊息，**保留 `toast`**、依原順序移到 `init` 後面（`ClientOutboxRules.KeptAcrossInit`；溢出照樣清空整個佇列）；閘道在連線鎖裡標成送過 `init` 並把它入列（`BrowserConnection.EnqueueInit`），被拒指令的 toast 也在同一把鎖裡檢查再入列（`TryEnqueueToast`），所以 toast 不會比 `init` 先到（WP3.7，設計 14.10） |
| `Realtime/BrowserSessionMonitor.cs` | `SessionEvents.Revoked` 即斷線、每 30 秒巡檢閒置與絕對期限、hub 活動經 `SqliteTicketStore.TouchAsync` 續期 |
| `Realtime/BrowserGateway.cs`（`partial`，含 `BrowserGatewayWiring`）、`Sync/AccountViewBuilder.cs`、`Sync/UsageHistoryCache.cs`、`Sync/AccountViewRules.cs` | 單一背景迴圈：提交與上下線只記髒，最多每 500 ms 以一個 deferred 唯讀交易讀快照、分四部分比對、沒變不送；`init` 由迴圈送；停止時每條連線的佇列停收 |
| `Realtime/MessageGuard.cs`、`BrowserCommandRules.cs`、`BrowserCommandHandler.cs`、`Sync/AccountReorder.cs`、`Sync/AccountOrderRules.cs` | 訊息結構驗證、指令分類與 toast；擁有者的修改在 `SyncWriter.WriteAsync` 內呼叫 `Catalog`（被拒就丟 `CatalogRejectedException` 讓 SAVEPOINT 回滾），本機提交後才回 accepted、寫稽核 |
| `Realtime/BrowserEndpoints.cs`、`Sync/RevocationCompletion.cs` | 撤銷裝置（以 `PairingService.RevokeAsync`：稽核與 hub.db 的撤銷同一個交易，提交之後不理會取消）與刪除帳戶，都要近期驗證；撤銷的 sync.db 那一半（解除連結、`device.revoked`）冪等，沒做完的由重送撤銷或下次啟動補做 |
| `Realtime/BrowserGateway.Conversations.cs`、`ConversationViews.cs`、`ConversationGatewayRules.cs`、`BrowserConversationCommands.cs`（WP4.2 步 10） | 閘道迴圈裡的對話訊息：每 50 ms 取走 `ConversationChangeFeed`、依對話摺疊，只推給開著那個對話、快照比變更舊的連線；一輪要收的增量超過一頁或排進去會讓送出佇列超過上限一半的連線改成伺服器自己重開（`BrowserConnection.RequestReopen`、`ConversationGatewayRules.ShouldReopen`）；清單依連線的時區、內容變了才送；open／loadOlder 由 hub 指令記下（O(1)），迴圈讀快照、渲染（LRU 渲染快取）再入列，排進去會溢出的放回去（`ClientOutbox.WouldOverflow`）。正式的閘道是變更通知唯一的消費者，沒有瀏覽器連線時也取走就丟：測試要自己取通知時，主機以 `HubTestHost.StartAsync(withoutConversationGateway: true)` 啟動 |
| `Realtime/DeviceHub.Conversations.cs`、`LiveRelayIntake.cs`、`LiveOverlay.cs`、`LiveOverlayRules.cs`、`BrowserGateway.Live.cs`（WP4.2 步 11） | 即時更新：裝置 hub 的 `LiveRelay` 在呼叫執行緒上做完便宜的檢查、權杖桶（每台、每個對話）與第二道 DLP，不等寫入者、不回錯誤（只計數 `liveDropped`）；通過的放進疊加層的有界佇列，閘道迴圈取走、以 (對話, ord) 與裝置連線的 Order 決定新舊（持久版本 final 就丟、跨開機不比 ver），推給開著的連線；疊加被清掉（持久版本、擁有者斷線、較新的連線 Prove、上限）時改推持久版本 |
| `Realtime/LiveDeltaBases.cs`、`ItemPatchRules.cs`（`live2`、`patch1`，2026-10-23；多電腦同步設計 14.23） | 即時更新的增量：`LiveDelta` 以（連線, 對話, ord）的基底重組成整則再照上一列的步驟（基底和疊加層分開，對不上回 `LiveResync`）；閘道對宣告 `patch1` 的瀏覽器連線、同一筆項目在渲染後的欄位上送 `itemPatch`（`rv` 核對；送出佇列裡有同一筆還沒送的就送整筆） |
| `Realtime/ConversationWatch.cs`、`WatchRules.cs`、`SnapshotRequester.cs`、`SnapshotRequestRules.cs`、`DeviceMessageSender.cs`（WP4.2 步 12） | 伺服器 → 裝置：瀏覽器「看得到」的對話（同閘道的 `CanSee`）換了、或對話有變更時，50 ms 合併後重算每台擁有者的觀看清單，變了才在背景對協商出 `live1` 的已 Prove 連線送 `WatchChanged`（Prove 時另送一次）；open 一個伺服器上還沒有項目的 full 對話時請擁有者送整份（`SnapshotRequest` open，每個對話冷卻 10 分鐘），還原之後 Prove 過了 300 秒仍是 recovering 的對話每台每秒最多 2 則送 restore（只當後備、每個對話只送一次）。都經 `IDeviceMessageSender`（正式是 `IHubContext<DeviceHub>`），送之前看 `Allows` |
| `Realtime/ConversationUnread.cs`、`ConversationUnreadRules.cs`、`BrowserGateway.Unread.cs`（WP4.2 步 12A） | 伺服器記的網頁未讀（sync.db v3 的 `conversation_unread`，有列就是未讀）：閘道取走變更時，新的上行事件（對帳重送與重建不算）是五種注意事件之一或一輪結束、而且沒有任何連線「看見」（ClientState 的 `visible` 與 `focused` 都為真、對話在 `visible` 指令的清單或 `openConversationIds` 裡）就標；ClientState 或 `visible` 指令的清單換了之後清掉開始被看見的。集合在記憶體為準、寫入不等；刪除的交易同時刪掉未讀列，降級與重建不碰。清單的 `unread` 取這個集合，改了就在同一輪重組、重送 `conversations` |
| `Realtime/BrowserDrafts.cs`、`BrowserDraftRules.cs`（WP4.2 步 13） | 伺服器草稿：`newConversation` 開一份只存在記憶體的草稿（id 是 `d`＋ULID，不進清單、目錄、WatchChanged 與未讀），回 `conversation`（`isDraft`、`paneId` 原樣送回）；以工作階段為鍵、每個最多 8 份（丟最久沒用的並送 `conversationClosed`），最後一條連線斷了 2 分鐘內重新連上（`BrowserConnections.Added`）就還在。WP4.5 的草稿指令經 `IBrowserDrafts`（`TryGet`／`TryUpdate`／`Remove`）；帳戶取閘道的帳戶快照（`BrowserGateway.DraftAccounts`） |

背景迴圈（閘道、巡檢）每一輪都有最外層例外攔截，送出幫浦不丟例外：閘道的錯誤不讓服務停機（`BrowserShutdownTests`）。真的瀏覽器的驗證是 `tools/hub-e2e/gateway.mjs`。

## 給其他工作包的接手點

- 組合根 `Hosting/HubApplication.cs` 只呼叫各模組的 `AddXxx()`／`MapXxx()`；開始聽連接埠前要做的初始化登記 `IStartupTask`（`StartupOrder`）。
- `SessionEvents.Revoked`：工作階段撤銷、登出、登入換 key 時觸發，瀏覽器 hub 訂閱它中斷該工作階段的連線（`HubClaims.SessionId` 是同一個 id）。
- **WP3.3（桌面同步引擎）**：線上行為以 `docs/sync-protocol.md` 為準；`tests/MyriagentHub.Tests/TestSupport/DeviceHubClient.cs` 是一個最小的裝置用戶端（Hello／Prove 簽章、批次、等訊息），`SoakTests` 的 `SoakDevice` 是 outbox 續傳的參考作法。`HubException` 只帶代碼（sync-protocol 11）。裝置 hub 不開 stateful reconnect，用戶端不要呼叫 `WithStatefulReconnect()`：重連一律是新連線、重新 Hello／Prove（sync-protocol 1.1）。
- **WP3.5（網頁傳輸層與儀表板，已完成）**：只改了 `wwwroot/**`（伺服器程式沒動）：`transport-hub.js`（連 `/hubs/v1/browser`、無限重連、`ready`／`ClientState`、上限自檢、限流重送，掛 `window.myriagentHubTransport`）、`lib/signalr-10.0.11.min.js`（`CspTests` 以固定 SHA-256 清單把關）、登入頁表單登入後導向 `/app/`、配對核准頁 `/#pair[=CODE]`（`auth.js`、`authrules.js`）。網頁那一半（`src/Myriagent/Web/` 的 `transport.js`、`remote.js`、`remoterules.js`、`hubapi.js`：帳戶 × 電腦矩陣、各電腦 CLI、電腦頁的改名與撤銷、從目錄刪除帳戶）以嵌入資源在 `/app/` 供應。傳輸層的契約在 `docs/web-protocol.md` 第六節第 13 點、與設計的差異見設計 14.8、實作計畫與實作紀錄 `docs/plans/2026-10-16-wp3.5-web-transport.md`（附錄 B）。真的瀏覽器的驗證是 `tools/hub-e2e/gateway.mjs` 與 `pairing.mjs`（本機 MyriagentHub＋兩台 fake-device）。
- **WP3.6（瀏覽器閘道，已完成）**：契約見 `docs/web-protocol.md` 第六節、與設計的差異見設計 14.6、實作計畫與實作紀錄 `docs/plans/2026-10-16-wp3.6-browser-gateway.md`（附錄 B）、結構見上一節「瀏覽器閘道（WP3.6）的結構」。裝置頁、撤銷與刪除的畫面在 WP3.5。給第 4 階段的接縫：`BrowserHub`（WP4.5）、`BrowserGateway`（WP4.2 的 `BrowserGateway.Conversations.cs`）都是 `partial`；hub 加方法要一起改 `BrowserHubFilter` 的方法允許清單；對話類的整份取代或合併要加 `ClientOutboxRules.MergeKey`（`init` 是屏障，第 4 階段的 `resync` 型別在這裡加）；`not_available` 的指令改由伺服器處理時改 `BrowserCommandRules` 的分類與 web-protocol 第六節第 9 點；快照的新部分加在 `BrowserParts`／`BrowserGatewayWiring.PartsFor`。
- **WP3.7（第 3 階段收尾，已完成）**：toast 不再因 `init` 的時序遺失、閘道送還原後的時間窗鍵（`UsageWindowKeyRules`，Protocol）、`ops/status` 的 `usagePollsPerHour`（上方 `/readyz` 一段）；桌面那一半是裝置金鑰暫時性錯誤的退避（sync-protocol 12.9）。差異與實測見設計 14.10，計畫與實作紀錄 `docs/plans/2026-10-17-wp3.7-phase3-tail.md`（附錄 B）。
- **第 4 階段**：裝置 hub 是 `partial`，新方法放各自的 partial 檔；`EventLog.Pull` 已支援依串流（`stream`）的游標，每個游標讀到累計超過位元組上限就停讀（`PullRules.CursorDone`，回應與讀滿時相同）。瀏覽器送出佇列的 `init` 屏障只保留 `ClientOutboxRules.KeptAcrossInit` 列出的種類（目前是 `toast` 與 WP4.2 的 `notify`、`clipboard`）：新增的**通知類**訊息要列進去，否則重送 `ready` 或溢出後的 resync 會把它清掉；快照的增量不要列（會疊在新的快照上）。迴圈之外送的通知要像 toast 一樣在連線鎖裡檢查「已送過 init」再入列（`BrowserConnection.TryEnqueueToast` 的作法），不要讀了 `InitSent` 才在鎖外入列（會比 `init` 先到）。
- **WP4.1（第 4 階段契約，已完成）**：只加接縫，正式環境的行為不變（伺服器沒有宣告任何功能、遠端指令照樣 `not_available`）。契約見 `docs/sync-protocol.md` 第 14 節、與設計的差異見設計 14.11、計畫與實作紀錄 `docs/plans/2026-10-17-wp4.1-contract.md`（附錄 B 步 12 是伺服器這一半）。後續工作包只在自己的檔案實作：
  - **裝置 hub**：`DeviceHubFilter` 只放行 `DeviceHubMethods.DeviceToServer`（`DeviceHubRules.CheckInvocation`：清單外 `invalid`、Prove 前只能 Hello／Prove；U2 的只更新連線 Prove 之後只能 `RefreshAccessToken`、`Heartbeat`，其他 `update_required`），加 hub 方法（partial 檔、服務以方法參數注入，不加長主建構式）要一起改清單，`DeviceHubSeamTests` 的反射測試把關（清單裡的方法都已實作：WP4.2 的 `LiveRelay`、WP4.5 的 `CommandResult`、`CommandOutput`，測試的 `NotYetImplemented` 是空的；之後在清單加了還沒實作的方法就先列在那裡）。
  - **功能宣告**：在自己的 `Add*` 裡 `services.AddDeviceHubFeature("conv1")`（WP4.2：`conv1`、`live1`；WP4.5：`cmd1`；WP4.7：`media1`；U2：`update1`，在 `AddHubReleases`；`DeviceHubFeatures` 依登記順序去重、不合格的名稱啟動時丟例外）；每條連線協商出的在 `DeviceConnection.Features`（只更新的連線只留 `update1`，`DeviceHubRules.ConnectionFeatures`），送第 4 階段的伺服器→裝置訊息之前一律問 `connection.Allows(method)`；`IDevicePresence.FeaturesOf(deviceId)` 是最後登記的那條連線的，device.upsert 的 `features` 照它填（空的不輸出）。
  - **連線事件**：`ConnectionRegistry.ConnectionProven`（Prove 成功之後、每條連線一次）／`ConnectionRemoved`（`PresenceChanged` 之前），`BrowserConnections.StateChanged`（`ClientState` 之後）／`Removed`；都在呼叫端的執行緒同步觸發、處理者的例外記錄後吞掉，處理者要很快回來。
  - **瀏覽器指令**：`CmdParse.Body` 是認得的指令的原始 `cmd` 物件；`BrowserCommandHandler` 的可選 `IBrowserConversationCommands`（WP4.2：接手它 `Handles` 的 `not_available` 與接受不處理的指令）與 `IBrowserCommandRouter`（WP4.5：接手型錄路由 `Owner`／`Device` 的 `not_available` 指令；信封簽章只在這裡），沒登記時與第 3 階段相同（`Realtime/BrowserCommandSeams.cs`）。
  - **儲存**：`IConversationDirectory`（WP4.2，`Find` 回擁有者、epoch、同步政策）、`IBlobStore`（WP4.7）的空實作在 `AddHubSync` 登記（`Sync/ConversationDirectory.cs`），實作在之後以 `AddSingleton` 登記、最後登記的勝出；sync.db v2 的欄位以 `SyncSchema.V2` 為準（v2 凍結；v3 只加 `conversation_unread`〔WP4.2 步 12A〕，見 `SyncSchema.V3`；v4 只加 `device_app`〔app 自我更新 U2〕，見 `SyncSchema.V4`；之後的改動進 v5）。
  - **驗證**：`HubPolicies.DeviceAccess`（裝置驗證方案、只收存取權杖 `cha_`）給 `/api/v1/device/*` 的裝置端點（WP4.7 的 blob、媒體轉送與縮圖，U2 的 app 下載；`Auth/HubAuthNames.cs` 裡它的說明還寫「WP4.1 只立政策，沒有端點用它」，WP4.7 之後已經過時，U2 不改 `Auth/*`，留給之後改 `Auth/` 的工作）；`InternalsVisibleTo` 已開給 `tools/fake-server`（WP4.4，組件名稱 `FakeServer`）。
- **WP5.1（第 5 階段契約，已完成）**：只加型別、規則、結構與登記點，正式環境的行為不變（兩個開關預設關、沒有連線協商得出 `log1`／`hand1`）。契約見 `docs/sync-protocol.md` 第 16 節、與設計的差異見設計 14.24、計畫與實作紀錄 `docs/plans/2026-10-23-phase5-handoff.md`（第 15 節）。
  - **登記點**：`Logs/LogRegistration.cs`（`AddHubLogs`／`MapHubLogs`，WP5.2 接上日誌的儲存、端點、掃描與維護）、`Handoff/HandoffRegistration.cs`（`AddHubHandoff`／`MapHubHandoff`，WP5.5 接上交接服務與在線判斷）；`HubApplication` 在 `AddHubTokens` 之後各呼叫一次。
  - **裝置 hub**：`DeviceHubMethods.DeviceToServer` 已有五個 `Handoff*`，hub 上的方法由 WP5.5 加在 `Realtime/DeviceHub.Handoff.cs`（那時把 `DeviceHubSeamTests.NotYetImplemented` 改回空的）。
  - **結構與規則**：sync.db v6（`logs`、`log_chunks`、`log_generations`、`handoffs`，WP5.1 沒有程式讀寫）；`ConversationApplyRules` 對 epoch 較舊的事件在擁有者檢查之前回 `fenced`（D4），裝置送的 `conv.lease` 一律 `invalid`（`ConversationPrecheckRules`、`ConversationApplyRules`）；`SyncEventStore.ServerAuthor` 改成引用 Protocol 的 `SyncAuthors.Server`（值不變）。
- 還原（`Ops/RestoreService.cs`）已處理 sync.db：`restore_generation` 同步、events 的 seq 起點拉高 10 億、每台 `needs_resync`；WP4.2 在同一個交易把還沒刪除的對話標成 `recovering`。
- **WP4.2（伺服器對話，已完成）**：結構見上面「伺服器對話（WP4.2）的接收、物化與營運」與「瀏覽器閘道（WP3.6）的結構」的 WP4.2 列；線上規則見 `docs/sync-protocol.md` 5.4、14.2、14.3，網頁那一側見 `docs/web-protocol.md` 的〔遠端〕與第六節第 8、9 點，與設計的差異見設計 14.12，計畫與實作紀錄 `docs/plans/2026-10-19-wp4.2-server-conversations.md`（6.1 是完整的介面表、附錄 B）。之後的工作包可以依賴的：
  - **目錄與變更**：`IConversationDirectory.Find`（`ConversationDirectoryCache`；任何執行緒、O(1)、提交之後才看得到，不存在、已刪除、id 不合格回 null）與內部的 `TryGet`／`OwnedBy`（另帶 recovering、項目數、更新時間）；`ConversationChangeFeed.Posted`／`TryDrain`（只在 AfterCommit 觸發、溢出設 `FullRefresh`，種類含 `Purged`、`Deleted`、`Rebuilt`，`Fresh` 標出新的上行事件，body 是存下的版本、可能是遮蔽版）。正式的閘道是變更通知唯一的消費者，要自己取的測試以 `HubTestHost.StartAsync(withoutConversationGateway: true)` 啟動。
  - **規則**：`ConversationDlpRules`、`ConversationRemaskRules`（WP4.7 的 blob 上傳與讀取；遮蔽後的走遍檢查為假才算成功）、`ItemSanitizer.Sanitize`（WP4.5、WP4.7），都是純函式、執行緒安全。
  - **讀取與渲染**：`ConversationReader`（唯讀、短連線、`Pooling=False` 經 `OpenRead`）；`HubItemRenderer` 與 `HubMediaUrls.ForRef`（WP4.7 只改 `ForRef` 與 `IBlobStore`，渲染器自動帶出 `/m/`、`/t/`；`media_refs` 由 WP4.7 從 `items` 補）。
  - **瀏覽器**：`ConversationUnread`（未讀集合，只在閘道迴圈裡讀寫，WP4.6）；`IBrowserDrafts`（WP4.5 的草稿指令與第一次 `send`）；`LiveOverlay.RemoveConversation`、`ConversationWatch.Recompute`（WP4.5、第 5 階段的交接與 epoch 改變）；通知類的入列一律經 `BrowserConnection.TryEnqueueAfterInit`、帶 `ClientOutboxMessage.Key`／`ConversationId`，新種類（WP4.5 的 `commandResult`、指令輸出，WP4.7 的 `fileRef`）要列進 `KeptAcrossInit`（例外：側欄搜尋的 `conversationSearch` 不列，網頁每則 `init` 之後自己重搜）。`newConversation`、`copyItem` 已由 WP4.2 的 `IBrowserConversationCommands` 接手（`BrowserCommandRules` 的分類沒改，WP4.5 改表時一併整理）。側欄的對話搜尋 `searchConversations`（2026-10-23）同樣由它接手，交給 `Realtime/BrowserConversationSearch.cs`：hub 指令只登記，背景依序讀清單與 `ConversationReader.ReadSearchItems`、原文經 `HubItemRenderer.TryReadSearchText`（同 copyItem 的 blob 與第二道 DLP），依各對話的 `last_seq` 快取在記憶體。
  - **營運與測試**：`Hub:Conversations:*` 只有上限、沒有開關；ops/status 的 `conversations` 區塊只有計數（含 `remasked`、`rejected`、`devices[]`、`syncDbLevel`）；回滾測試的 `MYRIAGENT_SYNC_OLD_HUB` 見上面「回滾測試的舊組建」；sync.db 的停機複製與備份量測見「sync.db 的停機複製與備份量測（SqliteBench B6）」。
- **WP4.5（遠端指令路由，已完成）**：結構見上面「遠端指令（WP4.5）的路由、結果與營運」；之後的工作包可以依賴的：
  - **路由**：`IBrowserCommandRouter`（`CommandRouter`）是轉送指令唯一的入口與唯一簽信封的地方；`CommandRouteRules.Deferred` 原本是延後到 WP4.7 的四個指令，WP4.7 合併時清空（集合留著，之後要延後別的指令時再用）。帳戶與 CLI 指令已轉送，電腦那一半在 WP4.4／WP4.8。
  - **結果與輸出**：`commandResult` 每個 bcid 至多一則、只給發起的連線；電腦的輸出照型錄的白名單投影（`CommandOutputRules.Project`；檔案指令的 `files`、`fileSearch`、`fileRef` 由 WP4.7 的 `CommandOutputFileRules` 投影）、再經 `ICommandOutputRewriter`（WP4.7）換成本站網址之後只給發起的連線。
  - **草稿**：第一次 send 之後草稿記著 `SentCommandId`、`PendingConversationId`（`BrowserDraft`）；新對話出現時閘道以 `conversationClosed.replacedBy` 換掉草稿（WP4.6 步 7：`BrowserGateway.Drafts.cs`、`BrowserDrafts.Swap.cs`、`DraftSwapRules`，即時、通知溢出、open 草稿三個時機）。草稿標頭的 `deviceId` 是 newConversation 選的電腦。
  - **測試**：`CommandRouterTests.Setup`（擁有者電腦 A、它的對話、Chrome／Windows 的瀏覽器）與 `NextCommandId`、`LinkAsync`、`AssertNoDeliveryAsync` 是 internal，其他測試類別共用；回滾測試的 `MYRIAGENT_SYNC_PREV_HUB` 見上面「回滾到合併前的 main」。
  - `ConversationRejectCodes`（Protocol）的四個線上名稱固定，與 WP4.3 逐位元組相同（`git hash-object` 是 `78f2646c4f10a19f63557ed149e9195dd7e77c80`）。
- **WP4.7（媒體、檔案與上傳，已完成）**：結構見上面「媒體、檔案與上傳（WP4.7）」；計畫 6.1 是完整的介面表。之後的工作包可以依賴的：
  - **網址與回應**（WP4.6）：`/m/`、`/t/`、`/f/` 一律是本站相對網址；`/m/` 的圖片在來源離線或 Off 時回縮圖並帶 `X-Myriagent-Fallback: thumb`；錯誤狀態碼 404／413／416／503／504；回應的 CSP 逐字等於 `MediaHeaderRules.MediaCsp`（含 `sandbox`）、`nosniff`，`SafeMediaContentType` 不收 SVG（SEC-23 的伺服器端保證）。E4 要 `X-Myriagent-Csrf: 1`、回 `{uploadId, sha256, size}`；伺服器草稿上的 `addUploaded` 回 `invalid`；第 4 階段的新對話在送出第一則之前不能附檔（草稿的 `send` 只帶 `text`，伺服器組的 `conversationCreate` 沒有 `uploads`；網頁在草稿停用附加），第一則送出、對話建立之後再 `addUploaded`。`conversationCreate` 的 `uploads`（`TryVerify` 與桌面的 `PrepareRemoteUploads`）留給之後的工作包。
  - **營運**（WP6.3、WP4.9）：ops/status 的 `blobs` 區塊與 `blob_quota`／`blob_refuse` 告警只加不改。
  - **儲存**（WP5.6 附件原檔同步）：`BlobStore.PutStreamingAsync`、`blob_refs` 的種類（`item`、`thumb`、`upload`）、`MediaRelay`；新種類只要加新的 `ref_kind`、清理依種類分開，原檔同步要另訂上限（項目 blob 的 4 MB 不適用）。
  - **共用規則**（WP4.9、第 5 階段）：Protocol 的 `MediaRelayHeaders`、`MediaKeyRules`、`HttpRangeRules`、`UploadIdRules`、`BlobIdRules`。
  - **測試**：`tests/MyriagentHub.Tests/Media/MediaTestSupport.cs` 的 `MediaHost`（Kestrel 測試主機＋擁有者用戶端）與 `MediaRelayTestSupport.cs` 的 `FakeMediaDevice`（同程序回應 `MediaRequest` 的假裝置）、`MediaSeed`（對話、項目、參照、blob）是 internal，其他測試類別共用；回滾測試的變數見上面「回滾到合併前的 main」的媒體一段。
