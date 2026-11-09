# 裝置↔伺服器同步協定 v1

桌面（Clode Myriagent）與伺服器（MyriagentHub，`https://hub.example.com`）之間的協定規格。設計與理由見 `docs/plans/2026-10-10-multi-device-sync.md` 第 3、4、6、8.11、10 章，實作與設計不同之處記在該文件第 14 章；網頁↔宿主的訊息見 `docs/web-protocol.md`。

第 1～13 節是**第 3 階段**（裝置配對、帳戶目錄、共享用量、CLI 狀態）的範圍，加上 app 自我更新 U2 的只更新連線（1.4）、`appRelease` 通知（第 4 節）、`app.state`（6.6）與 app 下載端點（**第 15 節**）。第 4 階段（對話事件、即時更新、遠端指令與信封）的契約在**第 14 節**（WP4.1，設計 14.11）；第 5 階段（租約、交接、逐字稿日誌）的契約在**第 16 節**（WP5.1，計畫 `docs/plans/2026-10-23-phase5-handoff.md`、設計 14.22）。新增欄位一律讓舊版忽略得掉（見 1.4）。

型別與純函式規則都在 `src/Myriagent.Protocol`（桌面、伺服器、測試、`tools/fake-device` 共用同一份）：

| 主題 | 程式 |
|---|---|
| 常數、方法名稱、錯誤代碼、通知種類、支援的協定範圍、上限 | `Sync/SyncProtocol.cs` |
| 裝置 hub 訊息 | `Sync/DeviceHubMessages.cs` |
| 事件種類、類別、串流 | `Sync/SyncEventKinds.cs` |
| 第 3 階段事件的 payload、漫遊偏好白名單 | `Sync/CatalogPayloads.cs` |
| 配對 | `Sync/Pairing.cs` |
| outbox 與上行事件的判斷 | `Sync/OutboxRules.cs` |
| 線上 JSON 設定、payload 編碼 | `Sync/SyncJson.cs` |
| 識別碼 | `Sync/SyncIds.cs` |
| Hello／Prove 簽章、伺服器身分釘選與輪換 | `Security/HandshakeRules.cs`、`Security/P256KeyRules.cs` |
| 裝置名冊的格式 | `Security/DeviceRegistryRules.cs` |
| 顯示名稱的清理 | `Security/FingerprintRules.cs` 的 `LabelRules` |
| 指紋、base64url | `Security/FingerprintRules.cs`、`Security/Base64UrlRules.cs` |
| app 下載端點的訊息與路由（U2） | `Update/AppReleaseMessages.cs` |
| 第 5 階段：交接的訊息、代碼、租約規則、日誌的識別與鏈雜湊（第 16 節） | `Sync/HandoffMessages.cs`、`Sync/HandoffCodes.cs`、`Sync/LeaseRules.cs`、`Sync/LeaseApplyRules.cs`、`Sync/LogIdRules.cs`、`Sync/LogChainRules.cs` |

---

## 1. 通道與共通格式

### 1.1 通道

| 通道 | 方向 | 認證 | 用途 |
|---|---|---|---|
| `wss://hub.example.com/hubs/v1/device`（SignalR，只允許 WebSockets，Newtonsoft JSON 協定） | 裝置↔伺服器 | `Authorization: Bearer chd_…`（只收標頭，不收查詢參數），連上後 Hello／Prove 雙向簽章 | 持久事件、拉取、心跳、用量、信任資料 |
| `https://hub.example.com/api/v1/pair/start`、`/pair/token` | 裝置→伺服器 | 匿名（速率限制） | 配對（第 2 節） |
| `https://hub.example.com/api/v1/pair/*`（其餘）、`/api/v1/trust` | 瀏覽器→伺服器 | 擁有者 cookie＋`Origin`＋`X-Myriagent-Csrf: 1` | 配對核准（2.2）、信任資訊（7） |
| `https://hub.example.com/api/v1/device/*` | 裝置→伺服器 | `Authorization: Bearer cha_…`（Prove 後發的 15 分鐘存取權杖；伺服器的 `HubPolicies.DeviceAccess` 只收它） | app 下載端點（第 15 節，U2）；第 4 階段的 blob、媒體轉送與縮圖（14.3 的 E5～E10，WP4.7）；第 5 階段的逐字稿 |
| `wss://hub.example.com/hubs/v1/browser` | 瀏覽器↔伺服器 | 擁有者 cookie | web-protocol（不在本文件） |

兩個 hub 完全分開：裝置權杖在瀏覽器 hub 無效、cookie 在裝置 hub 無效。裝置 hub 沒有任何「叫別台做事」的方法。

裝置 hub 的伺服器設定：`MaximumReceiveMessageSize` 1 MB（用戶端送出前自檢，永遠不送超過；量的是整個呼叫框——`{"type":1,"invocationId":…,"target":…,"arguments":[…]}` 加結尾的 0x1E，超過時伺服器關掉整條連線——所以單一引數的呼叫〔`CommandResult`、`CommandOutput`、`LiveRelay`〕引數序列化後 ≤1 MB − 4 KB，`SyncProtocol.FitsInvocation`／`InvocationFrameReserveBytes`；`PublishBatch` 另有 64 KB 的預留，`OutboxRules.BatchEnvelopeReserveBytes`）、`MaximumParallelInvocationsPerClient` 1（同一連線的呼叫依到達順序處理）、**不開 stateful reconnect**、KeepAlive 15 秒／ClientTimeout 30 秒。stateful reconnect 換上的新傳輸會沿用舊連線「已證明」的狀態、不重新綁 TLS 憑證，拿到合法憑證的中間人可以在裝置重連時接手；不開它，每條新的傳輸都是新連線、要重新 Hello／Prove。持久事件本來就不靠推送（BatchAck 沒收到就重送、Notify 漏了重連後會拉、用量指派與信任版本在交握時重給），用戶端也不要呼叫 `WithStatefulReconnect()`。hub 方法只做驗證與入列、立即返回，結果另以伺服器→裝置訊息送回。

### 1.2 JSON

- 外層訊息以 `SyncJson.Settings` 序列化：屬性名 camelCase、**字典的鍵照原樣**（例如 `cli.state` 的 `"Claude"`、`cursors` 的 `"catalog"`）、null 欄位省略、不把像日期的字串轉成日期、不認得的欄位忽略、最大深度 64。
- SignalR 的每個方法只有**一個參數**（一個 JSON 物件），伺服器→裝置的訊息也是。
- 時間一律 Unix 毫秒（`long`），只供顯示與到期判斷，**排序從不看時鐘**（只看伺服器 `seq`）。
- 二進位一律 base64url（RFC 4648 第 5 節、不加 `=`；解碼嚴格：只收 URL 字元、重新編碼必須等於原文）。payload 的 gzip 例外，用標準 base64（5.4）。
- 顯示文字（電腦名稱、Windows 使用者名稱、帳戶的 email、組織、方案、狀態說明、CLI 版本等）：伺服器只拒絕控制字元與單獨的代理字元（配對回 `invalid_request`、事件回 `invalid`）。格式字元（雙向覆寫、零寬字元、BOM）與行段分隔字元**不拒絕**，伺服器存進目錄時去掉再顯示；桌面不必自己清理。名稱（裝置名稱、帳戶名稱）另依 `LabelRules`（7.1）。

### 1.3 識別碼

| 識別碼 | 格式 | 產生者 |
|---|---|---|
| `deviceId` | `d_`＋26 個 Crockford Base32 字（16 位元組亂數，128 位元） | 伺服器（配對） |
| `logicalAccountId` | `la_`＋26 字 | 伺服器 |
| `batchId` | ULID（26 字） | 裝置 |
| `bootId` | 桌面程序這次啟動的亂數 | 裝置 |
| 伺服器 `instanceId` | 建庫時的亂數；`restoreGeneration` 每次還原＋1 | 伺服器 |

Crockford Base32 字母：`0123456789ABCDEFGHJKMNPQRSTVWXYZ`（大寫）。

### 1.4 版本相容

- Hello 帶 `protocolVersion`（目前 1）。伺服器支援 `SyncProtocol.MinSupportedVersion`～`SyncProtocol.Version`（目前都是 1，`SyncProtocol.IsSupported`）；升 `Version` 時至少留前一版：伺服器一律先部署，再發佈用新協定的桌面。
- `minAppVersion` 是伺服器設定 `Hub:Sync:MinAppVersion`（預設 `0.1.0`），HelloResponse 帶它。版本比較用 `AppVersionRules`：2～4 段數字、`-`／`+` 之後的字尾不看、缺的段補 0；格式不對當成太舊。

伺服器依序判斷 Hello，取第一個符合的列（app 自我更新 U2）：

| 條件 | 結果 |
|---|---|
| `protocolVersion` 比伺服器支援的最高版本新（伺服器回滾時才會發生），或小於 1 | `HubException("protocol")`，宣告了 `update1` 也一樣 |
| `protocolVersion` 比伺服器支援的最低版本舊，或 app 版本低於 `minAppVersion`，而且 Hello 的 `features` 含 `update1`（14.1） | **只更新的連線**：Hello／Prove 照常驗證，`ProveResponse.updateOnly` 是 `true` |
| 同上，但 `features` 沒有 `update1`（U3 之前的所有桌面） | `HubException("protocol")` |
| 其他 | 一般連線 |

- **只更新的連線**：拿到的存取權杖跟一般連線一樣（權杖不分範圍）；hub 只放行 `RefreshAccessToken` 與 `Heartbeat`，其他方法一律 `HubException("update_required")`。第 15 節的下載端點照常能用；照樣收到 `ServerNotice` 的 `shutdown` 與 `appRelease`，裝置被撤銷時照樣中斷。裝置（U3）不送批次、不拉取，outbox 照常保留，換版後以一般連線補送。
- 只有宣告了 `update1` 的桌面才拿得到只更新的連線：U3 之前的桌面不會下載更新，也不認得 `update_required`（不認得的代碼當成連線錯誤、退避重連，每次 Prove 還會多寫一筆 `device.upsert`），照舊回 `protocol` 才會停止重試、提示更新 app。
- 收到 `protocol` 時，U3 之前的桌面提示更新 app、停止重試；U3 起改成每小時重試一次（伺服器回滾後再升回來，桌面不必重開就連得上），協定比伺服器新時顯示「伺服器版本較舊」。
- 配對的 `/pair/start` 用同一個範圍檢查（`SyncProtocol.IsSupported`），協定不支援或 app 版本低於 `minAppVersion` 時回 400 `{"error":"protocol"}`。配對不開放只更新：太舊的 app 先更新再配對。
- **不變式**：`Hello`、`Prove`、`RefreshAccessToken` 的請求與回應形狀，以及第 15 節的端點，永遠只加選用欄位（加在最後、有預設值、沒有值時線上省略），任何舊版都更新得上來。`HandshakeShapeInvariantTests` 以反射凍結這些型別的主建構子。
- 新版加欄位不升版：舊版忽略不認得的欄位、新版讀不到的欄位是 null／預設值。拉到不認得的事件種類一律略過（游標照樣前進）。

**伺服器怎麼處理只更新的連線**（U2；`DeviceHubRules.Gate`、`DeviceHub`、`DeviceHubFilter`）：

- Hello 先過版本閘門，再檢查格式：`features` 不合格（14.1）回 `invalid`；`appVersion` 是空字串、超過 64 字或含控制字元也回 `invalid`。宣告了 `update1` 的舊版連版本號解析不了也會走到 Prove（當成太舊），這個字串會寫進 `device_streams`、`device.upsert` 與版本頁。
- 照常做的：Prove 驗簽與撤銷複查；登記連線（算在線，`device.upsert.online` 是 `true`，撤銷時一起中斷）；記錄 Hello（`device_streams.app_version`）；裝置權杖照常輪換（`tokenRotation`）；Prove 與 `RefreshAccessToken` 發一般的存取權杖（不分範圍）；`device.hello` 的稽核照舊。
- 不做的：
  - 不啟動批次佇列，也不進對帳：`resync` 一律 `false`，`nextDeviceSeqExpected` 是伺服器記的下一號；還原後的對帳旗標留給換版之後的完整連線。
  - 不當用量查詢的候選（`ConnectionRegistry.IsSyncing`）：`usageAssignments` 一律是空的，也不推 `UsageAssign`（換版交疊時同一台的完整連線照收）。
  - 不加入 `devices` 群組，改加入 `devices-update`：只收 `ServerNotice` 的 `shutdown`、`appRelease`，不收 `Notify`、`TrustChanged`。
  - 不啟用第 4 階段的功能：這條連線的功能只留 `update1`（`device.upsert.features` 也是），伺服器不送第 14 節的任何訊息。
- hub 的關卡是白名單：Prove 之後只放行 `RefreshAccessToken` 與 `Heartbeat`，交握方法與之後階段新增的方法也一律回 `update_required`。
- 「只更新」限制的是裝置 hub 的方法，不是存取權杖：權杖不分範圍，`/api/v1/device/*` 的端點（第 15 節的下載、WP4.7 的 blob 與媒體）跟其他已配對的裝置一樣通過認證。

---

## 2. 配對（仿 RFC 8628）

### 2.1 裝置端

1. 產生不可匯出的 CNG ECDSA P-256 裝置金鑰（能用 TPM 就用，`keyStorage` 記 `tpm`／`software`）。
2. `POST /api/v1/pair/start`：

```jsonc
// PairStartRequest
{ "deviceName": "OFFICE-PC", "machineName": "OFFICE-PC", "windowsUser": "User",
  "devicePubKey": "base64url(SPKI)", "keyStorage": "tpm", "appVersion": "0.3.0", "protocolVersion": 1 }
// → 200 PairStartResponse
{ "deviceCode": "base64url(32 位元組)", "userCode": "MTLZ-XFCH",
  "verificationUri": "https://hub.example.com/#pair", "verificationUriComplete": "https://hub.example.com/#pair=MTLZ-XFCH",
  "expiresIn": 600, "interval": 2 }
```

3. 顯示配對碼、網址、QR 碼（`verificationUriComplete`）與**裝置指紋**（`P256KeyRules.Fingerprint`：SPKI 的 SHA-256 取 Crockford Base32 前 8 字，`XXXX-XXXX`；只在配對的 10 分鐘內用來對照，8 字夠）。
4. 每 `interval` 秒 `POST /api/v1/pair/token {deviceCode}`：
   - 400 `{"error":"authorization_pending"}`：繼續。
   - 400 `{"error":"slow_down","interval":7}`：間隔改成 `PairingRules.NextInterval`（＋5 秒，或伺服器給的較大值）。
   - 400 `expired_token`／`access_denied`／`invalid_grant`：結束，回到未配對。
   - 200 `PairTokenResponse`：見 2.3。

伺服器端規則：配對碼 8 字取自 `BCDFGHJKLMNPQRSTVWXZ`（輸入時去掉空白與連字號、不分大小寫，`PairingRules.NormalizeUserCode`）；`deviceCode` 只存 SHA-256、只能換一次；10 分鐘到期；同時待核准最多 5 筆（第 6 筆回 `too_many_pending`）；輪詢比 `interval` 快回 `slow_down`；`deviceName` 經 `PairingRules.CleanDeviceName`（去掉控制與格式字元、去頭尾空白後 1～60 字，7.1），`devicePubKey` 必須是 P-256 SPKI，不合格回 `invalid_request`。

### 2.2 核准（擁有者的瀏覽器）

| 端點 | 用途 |
|---|---|
| `GET /api/v1/pair/pending?code=MTLZ-XFCH` | 核准頁的內容：`deviceName`、`machineName`、`windowsUser`、`keyStorage`、`appVersion`、來源 IP、建立與到期時間、**裝置指紋**（與桌面畫面對照） |
| `POST /api/v1/pair/approve {userCode}` | 要求近期驗證（5 分鐘內輸入過密碼；否則 403 `{"error":"step_up_required","stepUp":true}`，網頁先 `POST /api/v1/auth/stepup {password}` 再重送）。伺服器建 `devices` 列（名冊的一筆，7.1）、`registry` rev＋1、發 `TrustChanged` → 該裝置的下一次輪詢拿到權杖 |
| `POST /api/v1/pair/deny {userCode}` | 拒絕（裝置下一次輪詢拿到 `access_denied`） |

核准頁的字串（裝置名稱、電腦名稱、使用者名稱）一律 `textContent`。核准由伺服器授權（設計 14.4）：擁有者以帳號密碼登入、5 分鐘內輸入過密碼就是授權，沒有擁有者的簽章。

配對碼找不到或已結束回 404 `not_found`；同一把裝置金鑰的另一筆配對請求先被核准了（或那把金鑰配對過、撤銷過）回 400 `invalid_request`；配對過的裝置（含已撤銷的、不含已移除的）已達名冊上限 256 台回 409 `too_many_devices`（超過上限的名冊裝置不採用，之後的撤銷也會看不到）。核准與拒絕都寫稽核（`pair.approved`、`pair.denied`）。

核准頁在 `/#pair[=CODE]`（`src/MyriagentHub/wwwroot/auth.js`），沒有碼時可以手動輸入（去掉英數字以外的字元、不分大小寫，`authrules.js` 的 `cleanPairCode`）。未登入時先在同一頁登入，登入後留在核准頁、不導向 `/app/`；網頁的核准一律先跳密碼框（`/api/v1/auth/stepup`，近期驗證還有效也一樣），核准鈕在配對細節出現、視窗取得焦點或回到可見 1 秒後、有真的操作才解除停用（防 DoubleClickjacking；伺服器端的授權不變）（WP3.5，設計 14.8 D15）。

### 2.3 裝置收到權杖之後

```jsonc
// PairTokenResponse
{ "deviceId": "d_7K3M…", "deviceToken": "chd_…",
  "hubIdentitySpki": "base64url(伺服器身分公鑰 SPKI)",
  "server": { "instanceId": "9f1c…", "restoreGeneration": 0 },
  "trust": { "registryRev": 7, "registry": [ /* 整份 */ ] } }
```

裝置**驗證全部通過才**寫進 `device.json`／sync.db（權杖以 DPAPI CurrentUser 保存）：

1. 名冊格式合格（`DeviceRegistryRules.IsValid`，7.1），裡面有自己（`deviceId` 相同）那一筆且 `IsOwnRecord`（公鑰是自己的、未撤銷）。
2. `hubIdentitySpki` 是 P-256 SPKI：釘選它（首次信任；畫面顯示伺服器身分指紋 `P256KeyRules.AnchorFingerprint`，16 字，可與伺服器主控台 `MyriagentHub.exe admin status` 對照）。

任何一步失敗 → 不保存、顯示原因、回到未配對（權杖丟棄；擁有者在網頁撤銷那筆裝置）。

---

## 3. 連線交握：Hello／Prove

連上裝置 hub 後第一個呼叫一定是 `Hello`；`Prove` 成功前，hub 篩選器拒絕其他方法（`HubException("not_proven")`）。每次連上都完整走一次（裝置 hub 不開 stateful reconnect，見 1.1）。

```jsonc
// 1. Hello（Invoke）
{ "protocolVersion": 1, "deviceId": "d_7K3M…", "bootId": "4f2a9c1e0b7d", "appVersion": "0.3.0",
  "machineName": "OFFICE-PC", "windowsUser": "User",
  "nonceD": "base64url(32 位元組)",
  "observedSpkiSha256": "base64url(SHA-256(裝置在 TLS 交握看到的葉憑證 SPKI))",
  "seenServer": { "instanceId": "9f1c…", "restoreGeneration": 0 },
  "outbox": { "nextDeviceSeq": 10443, "oldestUnackedDeviceSeq": 10391, "oldestRetainedDeviceSeq": 9120 },
  "cursors": { "catalog": 1200, "usage": 5310 },
  "policy": { "remoteControlLevel": "Control", "allowRemoteFullAccess": false } }
// → HelloResponse
{ "serverTime": 1791580000000, "nonceS": "base64url(32)",
  "server": { "instanceId": "9f1c…", "restoreGeneration": 0 },
  "hubSig": "base64url(P1363 64 位元組)", "hubIdentitySpki": "base64url(SPKI)",
  "trustRevs": { "registry": 7 },
  "limits": { "maxBatchBytes": 262144, "maxBatchEvents": 500, "maxInlinePayload": 131072, "maxLiveHz": 4,
              "maxInFlightBatches": 4, "maxPullEvents": 500, "maxPullBytes": 1048576 },
  "minAppVersion": "0.3.0",
  "rotation": { "oldSpki": "…", "newSpki": "…", "sig": "…" },       // 輪換過身分金鑰才有
  "features": [ "conv1", "live1", "update1", "cmd1", "media1" ],    // 伺服器支援的功能（14.1；WP4.2 起一律帶，依登記順序），沒宣告的舊伺服器省略
  "latestApp": { "release": "0.2.1523-ab12cd3", "version": "0.2.1523" } }   // 有可用的桌面版才有（U2，第 15 節）
// 2. Prove（Invoke）
{ "deviceSig": "base64url(P1363 64 位元組)" }
// → ProveResponse
{ "accessToken": "cha_…", "accessTokenExpiresAt": 1791580900000,
  "nextDeviceSeqExpected": 10391, "resync": false,
  "usageAssignments": [ { "logicalAccountId": "la_…", "term": 4, "on": true } ],
  "tokenRotation": "chd_…",              // 權杖超過 30 天才有，否則省略
  "updateOnly": true }                   // 只更新的連線才有（U2，1.4），否則省略
```

簽的內容（UTF-8，欄位以 `\n` 串接，`HandshakeRules`）：

```
hubSig    = Sign(伺服器身分私鑰, "myriagent-hub-hello-v1\n{deviceId}\n{nonceD}\n{nonceS}\n{observedSpkiSha256}\n{serverTime}")
deviceSig = Sign(裝置私鑰,     "myriagent-device-hello-v1\n{deviceId}\n{nonceS}\n{nonceD}\n{observedSpkiSha256}")
```

兩把都是 ECDSA P-256、SHA-256、IEEE P1363（`r‖s` 64 位元組）。

**伺服器檢查 Hello**：伺服器排空中（部署換版）時新的 Hello 一律回 `HubException("draining")`（U2）；權杖對應的裝置＝`deviceId`、裝置未撤銷；`protocolVersion`、`appVersion` 支援，或可以給只更新的連線（1.4）；`nonceD` 是 32 位元組；`observedSpkiSha256` 等於**這條連線**的 TLS 交握實際用的葉憑證 SPKI 雜湊（不是比「目前或前一張憑證」：連上之後換了憑證，舊連線照樣對得上，新連線一定是新憑證；不符就中斷並稽核 `spki_mismatch`：裝置看到的是別張憑證＝中間人）。
**伺服器檢查 Prove**：`HandshakeRules.VerifyDevice(devices.device_pubkey, …)`；失敗 → 中斷、稽核。偷到 `chd_` 但沒有私鑰過不了；中間人轉送裝置的簽章給真伺服器也過不了（簽進去的 SPKI 是中間人的）。

**裝置檢查 HelloResponse**（`HandshakeRules.CheckHubIdentity`，一律以自己看到的 SPKI 雜湊與自己送的 nonceD 驗）：

| 結果 | 條件 | 裝置的動作 |
|---|---|---|
| `Ok` | `hubIdentitySpki`＝釘的、`hubSig` 驗得過 | 繼續 Prove |
| `Rotated` | 宣稱的是新公鑰、`rotation` 由釘的那把簽、`hubSig` 以新公鑰驗得過 | 改釘新公鑰、繼續 |
| `NeedsLocalConfirmation` | 換了金鑰但沒有可信的輪換聲明（伺服器重建；或離線期間輪換兩次以上），對方確實持有新金鑰 | 中斷、停止重試，顯示新的伺服器身分指紋（16 字）請使用者與**伺服器主控台**比對後才改釘 |
| `Invalid` | 其他（簽章不符、SPKI 不是裝置看到的、`nonceS` 不是 32 位元組、格式錯誤） | 中斷、記錄、退避重試 |

本機確認一律只認伺服器主控台（在 Hub 主機上執行 `MyriagentHub.exe admin status`，印出伺服器身分指紋）。**網頁不能當依據**：DNS 被劫持、中間人又拿到合法憑證時，瀏覽器打開的 hub.example.com 也是對方的，網頁上的指紋一定「相符」。指紋是 16 字（80 位元）：伺服器身分公鑰是公開的，8 字（40 位元）約 2^40 次嘗試就做得出同指紋的冒牌金鑰。

`|serverTime − 本機時間| > 2 分鐘` 只在狀態列警告「請同步 Windows 時間」（`HandshakeRules.IsClockSkewed`），不中斷。

身分金鑰輪換：`MyriagentHub.exe admin rotate-hub-identity` 產生新金鑰與輪換聲明 `Sign(舊私鑰, "myriagent-hub-rotate-v1\n{oldSpki}\n{newSpki}")`；伺服器之後的每個 HelloResponse 都附最近一次的聲明。

**ProveResponse**：

- `accessToken`：`cha_`，15 分鐘、綁 deviceId，只給 `/api/v1/device/*`；到期前 2 分鐘以 `RefreshAccessToken`（Invoke，無參數）換新 → `{accessToken, expiresAt}`。每台同時最多兩個有效：換新時保留上一個（換新前發出的請求還用它），更早的作廢。
- `nextDeviceSeqExpected`：伺服器期待的下一個 deviceSeq（之前的都已提交），裝置以 `OutboxRules.ApplyProve` 決定重送（5.2）。
- `resync`：伺服器還原後的對帳模式（第 9 節）。
- `usageAssignments`：這台目前負責查詢的邏輯帳戶（6.4）。
- `tokenRotation`：`chd_` 超過 30 天時帶新的，裝置換存。舊的再有效 24 小時（新權杖送到之前斷線、或換存失敗時不會被鎖在外面）；這段期間用舊的連上，Prove 會再發一個新的（上一個沒存到的新權杖作廢），舊的到期時間不延長。
- `updateOnly`（U2）：只更新的連線才是 `true`（1.4），否則省略（伺服器不送 `false`）。這時 `resync` 一律是 `false`、`usageAssignments` 是空的。

**Hello 帶的位置與政策**：`outbox` 由 `OutboxRules.Position` 算；`cursors` 以類別為鍵（第 3 階段 `catalog`、`usage`）；`policy` 只供網頁顯示，強制在裝置端；`trustRevs.registry` 與裝置採用的名冊版本不同就呼叫 `GetTrust`（7.2）。

**HelloResponse 的 `latestApp`**（選用，U2；U3 之前的桌面忽略）：`{release, version}`，伺服器目前最新的可用桌面版（同第 15 節的 latest），沒有就省略；位置在 `features` 之後。裝置每次連上就知道有沒有新版，再到第 15 節的端點取得、自己驗證。只更新模式的宣告用既有的 `features`（`update1`，1.4、14.1），`HelloRequest` 沒有新欄位。

---

## 4. 方法一覽（第 3 階段）

| 方向 | 方法 | 送法 | 參數 → 回應 |
|---|---|---|---|
| 裝置→伺服器 | `Hello` | Invoke | `HelloRequest` → `HelloResponse` |
| 裝置→伺服器 | `Prove` | Invoke | `ProveRequest` → `ProveResponse` |
| 裝置→伺服器 | `PublishBatch` | Invoke（只入列、立即返回） | `Batch` → 無；結果以 `BatchAck` 送回 |
| 裝置→伺服器 | `Pull` | Invoke | `PullRequest` → `PullResponse` |
| 裝置→伺服器 | `Heartbeat` | Send，每 15 秒 | `{uiAliveAt, running[], waiting[]}`（第 3 階段兩個陣列是空的） |
| 裝置→伺服器 | `UsagePublish` | Invoke | `{logicalAccountId, term?, usage}` → 無 |
| 裝置→伺服器 | `GetTrust` | Invoke | `{registryAfter}` → `TrustBundle` |
| 裝置→伺服器 | `RefreshAccessToken` | Invoke | 無 → `{accessToken, expiresAt}` |
| 伺服器→裝置 | `BatchAck` | Send | 5.1 |
| 伺服器→裝置 | `Notify` | Send（≤每 200 ms 合併一次） | `{high, cls[]}`：有新的持久事件，裝置自己來拉 |
| 伺服器→裝置 | `UsageAssign` | Send | `{logicalAccountId, term, on}` |
| 伺服器→裝置 | `TrustChanged` | Send | `{registry}`：與自己採用的版本不同就 `GetTrust` |
| 伺服器→裝置 | `ServerNotice` | Send | `{kind, retryAfterSec?, text?, release?}`：`shutdown`、`certExpiring`、`minAppVersion`、`alert`、`appRelease`（U2），見表下的種類表；〔第 4 階段〕`ownerLogin`（擁有者剛在網頁登入，桌面顯示通知，D27；依擁有者 2026-10-19 指示不做：伺服器不送、桌面不處理；改看 hub.db 的 `auth.login` 稽核） |

方法名稱是 `DeviceHubMethods` 的常數（兩邊以字串呼叫）。**第 3 階段沒有任何會讓裝置執行代理動作的伺服器→裝置訊息**；和程式有關的只有 app 自我更新 U2 的一個有界例外（`appRelease`，見下方）。第 4 階段的方法（`LiveRelay`、`WatchChanged`、`SnapshotRequest`、`MediaRequest`、`CommandDelivery`、`CommandResult`、`CommandOutput`）在 14.3、14.6（桌面遠端操作的 `PeerCmd`、`PeerReply` 在 14.7，各電腦即時狀態的 `StatsPublish`、`StatsUpdate` 在 14.8），只在 Hello 雙方宣告了對應的功能（14.1）之後才送；伺服器的裝置 hub 以方法允許清單收裝置→伺服器的呼叫，清單外的一律 `HubException("invalid")`，Prove 成功之前除了 `Hello`、`Prove` 一律 `not_proven`，只更新的連線（1.4）Prove 之後除了 `RefreshAccessToken`、`Heartbeat` 一律 `update_required`（`DeviceHubRules.CheckInvocation`，伺服器的 `DeviceHubFilter`；hub 的 partial 檔新增公開方法要一起改清單，測試以反射比對）。兩個方向的方法清單是 `DeviceHubMethods.DeviceToServer`／`ServerToDevice`（測試比對這張表與 14.3、14.6 的表）。

`ServerNotice` 的種類（`ServerNoticeKinds`；裝置遇到不認得的種類只記錄、略過）：

| kind | 欄位 | 意思與裝置的動作 |
|---|---|---|
| `shutdown` | `retryAfterSec`（沒帶時 30） | 伺服器即將停機：排空開始時（部署或 `admin drain`）送一次；直接停止服務（admin stop、服務管理員停止、重開機）不送，裝置把沒有通知的斷線當成連線中斷、照第 10 節的退避重連。等 `retryAfterSec` 再重連，不顯示錯誤 |
| `certExpiring`、`minAppVersion`、`alert` | `text` | 給人看的說明（交握完成才採用，12.2） |
| `appRelease` | `release` | 伺服器收進了新版（U2）：裝置到第 15 節的 latest 檢查（通知觸發的檢查限流 10 分鐘 1 次，U3），一律自己驗簽章 |

只更新的連線（1.4）也收得到 `shutdown` 與 `appRelease`。

**和程式有關的有界例外**（app 自我更新規格 §11）：`appRelease` 會讓裝置下載並執行新版，但只限 app 內建的發佈金鑰（`ReleaseKeys.Production`，編譯進程式）簽過、而且比目前新的版本，由裝置自己驗。Hub 主機不持有發佈私鑰，伺服器也加不了信任的發佈金鑰：伺服器不送任何金鑰，收件同樣只認內建的那一份。

裝置端的處理常式只做 O(1) 的事：放進同步引擎有上限的佇列後立刻返回（SignalR .NET 用戶端不會對伺服器背壓）；持久事件從不靠推送，佇列滿了也不會遺失。

**伺服器端的送出時機**（U2：`Realtime/DeviceBroadcaster.cs`、`DeviceHub.Hello`）：

- `ServerNotice` 只送給已 Prove 的連線：完整連線在 `devices` 群組、只更新的連線在 `devices-update` 群組（1.4）；送出不阻塞事件來源，失敗只記警告。
- `shutdown`：開始排空時送一次，帶排空要求的 `retryAfterSec`。直接停止服務（SCM 停止、`admin stop`、記憶體保險絲）不送：部署一律先送排空要求；沒有通知的斷線，裝置照第 10 節的退避重連。
- `appRelease`：收件收進一個新版本之後送一次（同一版重送回 `already_ingested`，不送）；排空不影響，裝置照下載端點的 `Retry-After` 稍後再下載。
- 排空中的 `Hello` 回 `HubException: draining`：在寫稽核、碰資料庫之前就拒絕，連線不中斷（同一條連線在排空解除後可以再 Hello，仍受連上後 30 秒內要完成 Prove 的限制）。
- **Hello 與 Prove 之間的空窗**（app 自我更新設計第 16 節第 64 項）：群組在 Prove 才加入，Hello 之後才開始的排空、或 Hello 之後才收進的新版，廣播送不到這條連線。Prove 加入群組之後再看一次：排空中就補送 `shutdown`（帶排空的 `retryAfterSec`），最新的可用版本比這條連線 Hello 回應的 `latestApp` 新就補送 `appRelease`（撤回讓最新版變舊時不送）；最多跟廣播重複一則，裝置照樣處理。補送比 `ProveResponse` 先到，裝置照交握完成前到的通知暫存、交握完成才採用（`DeviceHubRules.CatchUpNotices`）。
- `HelloResponse.latestApp`：可用版本中最新的一個（撤回的不算），沒有就省略這個欄位。

---

## 5. 持久事件

### 5.1 上行：outbox → PublishBatch → BatchAck

裝置先把事件寫進 sync.db 的 outbox 才送。`deviceSeq` 從 1 起**無間隙**遞增。

```jsonc
// Batch
{ "batchId": "01J9Q…", "events": [
  { "deviceSeq": 10391, "stream": "catalog", "kind": "link.upsert", "enc": "json",
    "payloadSha256": "64 個小寫十六進位", "payload": "{\"localAccountId\":\"…\",…}" },
  { "deviceSeq": 10392, "stream": "catalog", "kind": "cli.state", "enc": "gzip",
    "payloadSha256": "…", "payload": "H4sIAAAA…（標準 base64）" } ] }
// BatchAck（交易提交、fsync 之後才送）
{ "batchId": "01J9Q…", "acceptedThrough": 10392,
  "results": [ { "deviceSeq": 10391, "seq": 88123 }, { "deviceSeq": 10392, "rejected": "invalid" } ] }
// 整批錯誤（沒有任何一筆生效）
{ "batchId": "01J9Q…", "acceptedThrough": 10379, "results": [], "error": { "code": "gap", "expected": 10380 } }
```

**裝置規則**（`OutboxRules`）：

- 列的狀態：`pending`（沒送過）→ `sent`（在途）→ `acked`／`rejected`；要重送的改成 `retry`。
- **合併只發生在 `pending`**：同一個合併鍵（例如 `link:{localAccountId}`、`cli`）的新內容原地取代、保留 deviceSeq。送出過一次（`sent`／`retry`）的列內容就凍結——舊連線上的批次可能在重連後才被提交，改了內容再重送會被判成倒退。
- `NextBatch`：從第一個 `pending`／`retry` 起連續一段、≤`maxBatchEvents` 筆、≤`maxBatchBytes` 且 ≤ 單則訊息上限減 64 KB（每筆以 `PayloadRules.WireSize` 估：payload 位元組＋JSON 跳脫的膨脹＋256，是上限），最多 `maxInFlightBatches` 批在途。有待送時批次之間至少隔 100 ms（閒置後的第一批只等 10 ms 合併同一次刷新寫的幾筆，`SyncTimingRules.SendDelay`）。
- **選出的列先以 `MarkSent` 寫回 sync.db（同一個交易），之後才呼叫 `PublishBatch`**：送出後才寫的話，送出與寫入之間被合併進來的內容不是遺失、就是重連後被判成倒退。
- `ApplyAck`：逐筆標成 `acked`（帶伺服器 seq）或 `rejected`（帶原因，**不重送**）；results 裡重複的 deviceSeq 只取第一筆。整批錯誤：`gap` → 同 5.2 的一般模式（從 `expected` 重來）；`seq_regression` → 停止同步、提示重新配對；`invalid`／`too_large` → 停止同步並告警（是用戶端的錯，重送只會一直被拒）；其他代碼（新版伺服器多出的）或 `gap` 沒帶 `expected` → 在途的改回 `retry`、斷線後照退避重連（`Reconnect`），由 Prove 重新對齊。
- 已提交與被拒的列保留 24 小時再刪（`Prunable`，只刪最前面連續的一段，保留窗不能有洞），伺服器還原時用來補送。

**伺服器規則**（依到達順序，在寫入交易內）：

1. `OutboxRules.CheckBatchShape`：至少一筆、deviceSeq 連續遞增、筆數 ≤ 上限、`payloadSha256` 格式正確；不合格整批回 `invalid`／`too_large`（不佔 deviceSeq）。
2. 每筆以 `OutboxRules.ClassifyIncoming(next_device_seq, deviceSeq, 既有身分, 這筆的身分, resyncFrom)` 判斷。身分是 `OutboxRules.EventIdentity`（kind、stream、conversationId、epoch、payloadSha256 串起來；只比 payload 的話，倒退的裝置用同一個 deviceSeq 發出種類不同、內容碰巧相同的事件會被當成已提交）；既有身分取自 `events` 或 `rejected_events` 裡同一個 `(author, deviceSeq)` 的欄位。`resyncFrom` 是伺服器對這台在對帳模式時、這次連線的 `OutboxRules.ResyncFrom`（Hello 的 `oldestRetainedDeviceSeq`；outbox 是空的時候是 `nextDeviceSeq`），否則 null：

| 判斷 | 條件 | 處理 |
|---|---|---|
| `Next` | `deviceSeq == next_device_seq` | 解碼、重算雜湊比對、驗證 → 配發 seq（或記成被拒）→ `next_device_seq + 1` |
| `Duplicate` | 已有同一筆、身分相同 | 回原本的結果（冪等） |
| `Regression` | 已有同一筆、身分不同 | 整批回 `seq_regression`（裝置的 sync.db 被還原或複製到別台） |
| `Gap` | 比 `next_device_seq` 大 | 整批回 `{code:"gap", expected:next_device_seq}`（第一筆就跳號時；中間不會跳，因為批次連續） |
| `GapAccepted` | 對帳模式、比 `next_device_seq` 大、`next_device_seq` ≤ `resyncFrom` ≤ 這筆（大於等於而不是等於：Hello 之後才刪掉的已提交列讓第一筆落在起點之後） | 備份比這台的保留窗舊，缺口補不回來：稽核、`next_device_seq` 拉到它，再當 `Next` 處理（缺的內容由第 9 節第 3 步的重送補齊）。接受一次之後期待值越過起點，之後的跳號照樣是 `Gap` |
| `Missing` | 對帳模式、比 `next_device_seq` 小、伺服器沒有紀錄 | 當成新事件套用並稽核 |
| `Stale` | 不在對帳模式、比 `next_device_seq` 小、伺服器沒有紀錄 | 資料不一致：不套用（避免重複套用舊事件）、回成已提交（不帶 seq）並稽核 |

3. 被擋的事件仍然佔掉 deviceSeq、寫進 `rejected_events`，結果帶 `rejected`。第 3 階段的原因：`invalid`（種類不是裝置能寫的、串流不符、payload 格式或內容不合格、雜湊不符）、`too_large`、`conflict`（樂觀並行版本不符，6.2）。
4. 交易提交（`synchronous=FULL`）後才送 `BatchAck`；`acceptedThrough` 以前的 deviceSeq 都有結果。

### 5.2 重連後從哪裡重送

每次 Prove 成功都做（不綁在 SignalR 的 Reconnected 事件上：重連一律是新連線、重新交握，以 Prove 為準），`OutboxRules.ApplyProve(ordered, nextDeviceSeqExpected, nextDeviceSeq, resync)`：

- `nextDeviceSeqExpected > 本機的 nextDeviceSeq`：伺服器有這台沒發過的序號＝這台的 sync.db 倒退了 → 停止同步，提示重新配對成新的裝置身分。
- 一般模式：期待那筆以前的在途與重送列都已提交（標成 `acked`、伺服器 seq 未知）。期待那筆以前還有 `pending`＝伺服器提交了這台從沒送出的序號（`MarkSent` 在送出前就寫回，`pending` 不可能送過），也就是 sync.db 倒退了、伺服器存的是倒退前的舊內容 → 停止同步、提示重新配對（`StopSeqRegression`；當成已提交的話新內容會悄悄遺失，也永遠不會觸發 `seq_regression`）。從它起：在途的、以及**已標成提交或被拒、伺服器卻說沒有的**都改成 `retry`（內容凍結，重送安全；不這樣做會一直收到 gap 而無限循環）；`pending` 照舊。期待的比這台保留的最小 deviceSeq（outbox 空時是 `nextDeviceSeq`）還小＝伺服器缺了這台已刪掉的事件 → 停止同步並告警。
- 對帳模式（`resync: true`）：保留的列（含已提交與被拒的）全部改成 `retry` 重送；已在伺服器的冪等，備份比保留窗舊時伺服器在第一筆接受缺口（`GapAccepted`）。

### 5.3 下行：Notify → Pull

```jsonc
// Pull（Invoke）
{ "cursors": [ { "cls": "catalog", "after": 1200 }, { "cls": "usage", "after": 5310 } ], "maxEvents": 500, "maxBytes": 1048576 }
// → PullResponse
{ "events": [ { "seq": 1201, "stream": "catalog", "cls": "catalog", "author": "server", "kind": "account.upsert",
                "enc": "json", "payload": "{…}", "createdAt": 1791580001000 } ],
  "more": false, "high": 1201 }
```

- 游標以類別（`cls`）或串流（`stream`，第 4 階段的單一對話）擇一；`after` 是上次套用到的伺服器 seq。
- 回應 ≤`maxPullEvents` 筆且 ≤`maxPullBytes`；`more: true` 就再拉。`high` 是伺服器目前最大的 seq。
- 裝置依序把事件交給 UI 執行緒套用，**套用完才前進游標並寫回 sync.db**。
- 只套用**伺服器寫的**種類（`SyncEventKinds.IsServerAuthored`）；裝置寫的（含自己的）與不認得的種類略過，游標照樣前進。`author` 是 `server` 或裝置的 deviceId，只供稽核與顯示。
- 收到 `Notify` 時，`cls` 裡有自己拉的類別才拉；連上後（Draining）不等通知先拉一次。

### 5.4 payload 編碼

- payload 在線上是**字串**：`enc: "json"` 時是 JSON 原文；原文 UTF-8 ≥1 KB、而且 base64 之後比原文短時 `enc: "gzip"`，是 gzip(UTF-8 原文) 的標準 base64（`PayloadRules.Encode`；2026-10-23 以前門檻是 8 KB、一律壓）。讀的一方一律接受任何大小的 gzip，不能依大小推論編碼。
- `payloadSha256` 一律是 **UTF-8 原文**的 SHA-256（64 個小寫十六進位），裝置寫進 outbox 時算好、重送時不變；伺服器解碼後重算比對，並以它判斷冪等。
- 伺服器存的、拉取時發出去的都是同一串字串，不重新序列化。**唯一的例外**是第 4 階段對話事件的再遮蔽（WP4.2，14.2 的「伺服器」）：伺服器的第二道權杖 DLP 命中時，存下的、Pull 送出的是遮蔽後以 `SyncJson` 重新序列化的字串（`enc` 照上一點重新決定），`payloadSha256` 仍是裝置送來的原文雜湊——它是冪等身分（同一筆重送照樣判成重複、回原本的 seq），伺服器之後不再拿它驗存下的內容（拉到的事件本來就不帶雜湊，`PulledEvent` 只有 `enc` 與 `payload`）。原文命中的事件只存遮蔽版，未遮蔽的權杖不進伺服器的資料庫與記錄。
- 解碼（`PayloadRules.TryDecode`）有上限：解壓後 ≤`maxInlinePayload`（128 KB），必須是合法 UTF-8；超過或格式錯誤一律失敗（伺服器回 `invalid`／`too_large`）。
- payload 內部欄位用 camelCase（`SyncJson`）。第 4 階段的對話事件也是：項目不是桌面存檔格式（`ProtocolJson` 的 `AgentChatItem`），而是同步專用的投影（`ConversationWire`，14.2）。

---

## 6. 事件種類（第 3 階段）

類別（`cls`）與串流：目錄類事件都在 `cls=catalog`、`stream=catalog`；用量在 `cls=usage`、`stream=usage`（`SyncEventKinds.Placement`；裝置送的 stream 必須相同）。

| kind | 寫入者 | payload（`CatalogPayloads.cs`） | 合併鍵（建議） |
|---|---|---|---|
| `link.upsert` | 裝置 | `LinkUpsertPayload`：本機帳戶的狀態摘要（供應商、`configKind`、`identityKey`、目前認定的 `logicalAccountId`、狀態、email、組織、方案、到期時間、對話數〔`sessions {open, running, waiting, background}`；`background` 是等背景工作回報的對話數，2026-10-24 起，舊版不送、讀成 0，伺服器只檢查不是負數〕、名稱、顏色、順序、`checkedAt`） | `link:{localAccountId}` |
| `link.removed` | 裝置 | `{localAccountId}` | `link:{localAccountId}` |
| `account.update` | 裝置 | `{logicalAccountId, baseVersion, label?, color?, sortOrder?}`（只填要改的） | 無（每次修改都送） |
| `prefs.update` | 裝置 | `{key, value, baseVersion}` | 無 |
| `device.update` | 裝置 | `{deviceId, name}`：改顯示名稱（網頁的 `deviceRename`） | `device:{deviceId}` |
| `cli.state` | 裝置 | `{providers: {"Claude": DeviceCliView, "Codex": DeviceCliView}}` | `cli` |
| `app.state` | 裝置 | `AppStatePayload`：`{version, release?, phase, staged?, lastError?, checkedAt}`，這台 app 的更新狀態（6.6；伺服器從 U2 起收，桌面從 U3 起送） | `app` |
| `account.upsert` | 伺服器 | `AccountUpsertPayload`：邏輯帳戶整筆＋`version`＋各電腦矩陣 `links[]` | — |
| `account.deleted` | 伺服器 | `{logicalAccountId, version}` | — |
| `device.upsert` | 伺服器 | `DeviceUpsertPayload`：名稱、電腦名稱、版本、`keyStorage`、指紋、上線狀態、政策、CLI 狀態、app 的更新狀態（`app`，6.6） | — |
| `device.revoked` | 伺服器 | `{deviceId, revokedAt, reason}`（`retired`／`compromised`） | — |
| `prefs.set` | 伺服器 | `{key, value, version}` | — |
| `usage.snapshot` | 伺服器 | `{logicalAccountId, usage, sourceDeviceId, term?}` | — |

任何 payload 都不放權杖、登入碼或憑證檔內容（伺服器資料庫沒有能放權杖的欄位）。

### 6.1 帳戶目錄

- 裝置對每個本機帳戶送 `link.upsert`（狀態改變時）。伺服器依 `identityKey`（Claude `claude:{orgId}:{email 小寫}`、Codex `codex:{email 小寫}`；2026-10-22 起另有 Grok `grok:{email 小寫}`、Antigravity `antigravity:{email 小寫}`，桌面只在協商出 `prov1` 的連線上送這兩種，14.1）自動連結到邏輯帳戶；對不上就以這台的名稱與顏色新建一個。`identityKey` 是 null（還不知道身分）不自動連結。連結後身分變了（這台改登別的帳號）標 `identityOk: false`。
- 結果一律以 `account.upsert` 發布；裝置從中只套用**名稱、顏色、順序與自己的對應**（`links[]` 裡 `deviceId` 是自己的那幾格），其餘只供顯示。
- `link.removed`：這台移除了本機帳戶（「從這台移除」）。`account.deleted`：從目錄刪除；各台解除連結、本機帳戶保留（變成「未同步」）。

### 6.2 樂觀並行（CAS）

- `account.update`、`prefs.update` 帶 `baseVersion`：不是目前版本 → 結果 `rejected: "conflict"`，裝置不重送；之後拉到的 `account.upsert`／`prefs.set` 會蓋回伺服器的值。
- 擁有者在瀏覽器改的（`accountUpdate`、`accountReorder`、`setting`、`deviceRename`）由伺服器直接套用，同樣發布伺服器寫的事件。

### 6.3 漫遊偏好

只有四個鍵（`PrefsRules`）：`theme`（`dark`／`light`／`system`）、`fontSize`（12～18 的整數）、`enterToSend`（布林）、`notifications`（布林）。伺服器收 `prefs.update` 時檢查一次，裝置套用 `prefs.set` 時再檢查一次；其他設定（指示、權限預設、同步、CLI 更新政策）只能在每台本機改。

### 6.4 共享用量

- 每個邏輯帳戶由伺服器指派**一台**查官方用量（候選：連結正常、身分相符、在線、60 秒內有心跳、Claude 的存取權杖還有 5 分鐘以上——每台都不到 5 分鐘時不看權杖，照樣指派一台，讓它查詢時由 CLI 代查並換發，否則沒有一台會查、也沒有一台會換發〔2026-10-29〕；優先：有該帳戶執行中對話的 → 上一任 → deviceId 最小（序數比較））。換人時 `term`＋1，對新舊兩台各送 `UsageAssign{on/off, term}`；負責的那台 60 秒沒心跳就改派。伺服器每 15 秒、上下線時、目錄變更提交後重新評估。
- `UsageAssign` 的到達順序**不保證**（重新評估可能連續發生、兩台各自的連線不同步）：裝置對每個帳戶記下看過的最大 `term`，比它小的 `UsageAssign` 丟掉；同一個 `term` 只會有一台 `on`。ProveResponse 的 `usageAssignments` 一樣照 term 合併。
- 負責的那台照原本的頻率查，以 `UsagePublish{logicalAccountId, term, usage}` 上傳；對話中的 rate limit 事件（任何一台）以 `UsagePublish{logicalAccountId, usage}`（不帶 term）每帳戶 ≤每 60 秒一次上傳。伺服器的檢查：帶 term 的必須來自目前負責的那台、term 是目前值；不帶 term 的必須來自這個帳戶連結正常、身分相符的裝置，同一台同一帳戶兩次至少隔 50 秒（留 10 秒的計時誤差）。不合格的不採用（不回錯誤）。
- `usage` 的 `resetCredits`（0～1000 的整數）、`resetCreditExpiresAt`（毫秒、>0）是 Codex 的免費重置次數與最早到期時間（2026-10-30 加的選用欄位）：舊版的電腦不送、送了舊版忽略，伺服器照存照轉、閘道照樣給網頁（web-protocol 的 Usage）。
- 伺服器寫 `usage_latest`／`usage_points`（14 天）後發布 `usage.snapshot`；各台合併進自己的用量歷史（來源 `shared`）。歷史點的時間是**伺服器收到的時間**（不信任裝置時鐘），同一毫秒的兩點後到的取代先到的。
- 連線期間沒被指派的裝置**不查**官方端點；與伺服器斷線超過 5 分鐘自動回到本機各自查詢。

### 6.5 CLI 狀態

`cli.state` 帶這台每種 CLI 的 `installed`、`version`、`installKind`、`latest`、`updateAvailable`、`runningOnOldVersion`、`restartPending`、`installBroken`、`checkedAt`；伺服器併進 `device.upsert` 的 `cli`，給儀表板的「各電腦 CLI 版本」。

### 6.6 app 的更新狀態（app 自我更新 U2）

`app.state`（app 自我更新規格 7.8）帶這台 app 的更新狀態；伺服器併進 `device.upsert` 的 `app`，給擁有者的「版本」頁看各電腦的更新進度。

| 欄位 | 內容 |
|---|---|
| `version` | 目前執行的版本：三段數字的標準寫法（`ReleaseVersion`，例如 `0.2.300`；不帶空白與前導零） |
| `release` | 目前執行的 release 名稱（`0.2.300-ab12cd3`）；開發版沒有，省略 |
| `phase` | `AppStatePhases`：`idle`（沒有要做的事）、`downloading`（下載中）、`staged`（已下載並驗證，等著套用）、`waiting`（等使用者閒置）、`draining`（排空對話中）、`applying`（換版中）、`rolledBack`（新版啟動失敗、已退回）、`blocked`（缺 runtime、空間不足或未啟用） |
| `staged` | 已備妥、等著套用的 release 名稱；沒有時省略 |
| `lastError` | 最近一次失敗的原因：一行、最多 500 字；沒有時省略 |
| `checkedAt` | 最後一次檢查更新的時間（Unix 毫秒，只供顯示） |

- 伺服器的檢查（`CatalogRules.ValidateApp`）：`version` 是標準寫法的三段版本；`release`、`staged` 省略或是合法的 release 名稱（`ReleaseIdentity.TryParseRelease`）；`phase` 只收上面八個值（大小寫要相同）；`lastError` 最多 500 字、不含控制字元（換行也算，桌面送之前要整理成一行）；`checkedAt` 大於 0。不合格的回 `invalid`（5.1：仍佔 deviceSeq，裝置不重送）。
- 合格的存進 sync.db 的 `device_app`（結構 v4；每台一列，新的取代舊的）。存的是伺服器整理過的內容：`lastError` 去掉格式字元（同目錄裡其他的顯示文字），不認得的欄位不存；`updated_at` 是伺服器收到的時間。之後這台的每一筆 `device.upsert`（Hello、上下線、改名、`cli.state` 造成的都算）都帶最新的 `app`，在 `features`（14.1）之後；沒回報過的裝置沒有 `app` 鍵。撤銷裝置時不刪這一列（同 `device_cli`）。
- `app.version` 是送出當時的版本：換版之後到新版送出下一筆 `app.state` 之前，可能比同一筆 `device.upsert` 的 `appVersion`（Hello 回報的）舊，顯示目前的版本以 `appVersion` 為準。
- 合併鍵是 `app`（同 `cli.state`）。桌面（U3）照 `cli.state` 的做法：內容沒變不送；伺服器換實例或還原之後重送一次（第 9 節第 3 步在 U3 一併加上 `app.state`）。
- 只更新模式的連線（1.4）不能送批次，伺服器收不到它的 `app.state`；版本頁另外顯示 Hello 回報的 `appVersion` 與「只更新」標記。
- U2 只做伺服器端（收、存、併進 `device.upsert`）；產生與送出 `app.state` 是 U3 的桌面。U3 第二步（2026-10-22）起桌面送出：更新狀態每次改變時以 `AppStateRules` 算出，內容相同不送（published 摘要 `#app`），伺服器換實例或還原之後重送一次；`rolledBack` 不送（退回之後是 `idle`），沒檢查過時 `checkedAt` 是這次啟動的時間。
- 相容：U2 之前的伺服器把 `app.state` 當成裝置不能寫的種類回 `invalid`，所以照規格先部署伺服器、再發佈會送它的桌面版。U2 之前的桌面忽略 `device.upsert` 的 `app`（不認得的欄位）；拉到別台送的 `app.state` 時當成不認得的種類略過（游標照樣前進），同步狀態多一行「略過了不認得或解不開的伺服器事件（app.state）」，同步照常。U2 起的桌面把它算成裝置寫的種類，安靜略過。

---

## 7. 信任：伺服器身分、裝置名冊

信任模型是**伺服器說了算**（設計 14.4）：擁有者以帳號密碼登入網頁，配對核准與裝置名冊由伺服器授權、發布，沒有擁有者的簽章。裝置對伺服器的信任只靠釘選的伺服器身分金鑰（第 3 節的 Hello／Prove；換了金鑰要與伺服器主控台本機確認）：連到的是真的伺服器，就採用它發布的名冊。

後果：拿到伺服器主機（或擁有者帳號密碼）的人可以核准自己的裝置、改名冊、讀寫同步資料（帳戶目錄、用量、偏好）。第 3 階段沒有任何執行能力依賴名冊；第 4 階段的遠端控制另外要**目標電腦在本機開啟允許**（預設關閉、只能在那台電腦上改），伺服器的授權不足以讓別台電腦執行任何東西。

### 7.1 裝置名冊

```jsonc
// 名冊的一筆（DeviceRegistryEntry；伺服器為每台配對過的裝置各一筆，含已撤銷的、不含已移除的）。值是 null 的欄位在線上省略（1.2）
{ "deviceId": "d_…", "name": "OFFICE-PC", "devicePubKey": "base64url(SPKI)", "createdAt": 1791580000000,
  "revokedAt": null, "revokedReason": null }
```

- 格式（`DeviceRegistryRules.IsValid`，伺服器發布前與裝置收到時共用）：`deviceId` 格式正確；名稱 1～60 字且已清理（`LabelRules.IsClean`：不含控制與格式字元〔雙向覆寫、零寬字元〕、行段分隔與孤立的代理字元、沒有頭尾空白）；公鑰是 P-256 SPKI；`createdAt` ≥ 0；`revokedAt` 與 `revokedReason` 同時有或同時沒有，理由是 `retired`（退役）或 `compromised`（疑似遭入侵）。整份最多 256 台、`deviceId` 不重複。伺服器組名冊時跳過資料庫裡不合格的列（舊資料）；裝置收到不合格的整份就不採用（告警、沿用上一份）。
- 配對請求的裝置名稱以 `PairingRules.CleanDeviceName` 清理（去掉上述字元與頭尾空白；組合 emoji 的零寬連接字元也會去掉），清理後是空的就拒絕配對請求（`invalid_request`）。
- 名冊的 `name` 是配對時的名稱（hub.db `devices.name`）。顯示名稱存在 sync.db 的 `device_streams.name`，經 `deviceRename`／`device.update` 改，發布在 `device.upsert`。
- 版本（kv `registry_rev`）：配對核准、撤銷與移除時＋1，之後寫 `status\trust.json`、發 `TrustChanged`。撤銷的裝置之後的 Hello 一律被拒（第 3 節）。
- **移除**（2026-10-23，設計 14.22）：擁有者在網頁版把**已撤銷**的裝置移除（hub.db `devices.removed_at`；列留著，公鑰照樣不能再配對）。名冊不再列它、不算進 256 台的上限；伺服器不發新的事件種類，目錄裡它的 `device.upsert`、`device.revoked` 照舊留著（移除時重發一次它的 `device.upsert`，讓瀏覽器與各台重讀目錄）。裝置把「目錄有 `device.revoked` 而且不在目前採用的名冊」的裝置當成已移除（`DeviceRemovalRules`；名冊裡沒有自己時不判斷）：電腦頁、矩陣不列，丟掉它擁有的複本（每一頁拉取寫完之後與名冊更新之後檢查，所以名冊在升級之前就已不列它時，升級後的第一次拉取也會補上）。比這個舊的桌面只會照舊把它列成已撤銷、副本留著。它擁有的對話在伺服器上刪除，見 14.2 的 Pull 過濾。

### 7.2 GetTrust

```jsonc
// GetTrust（Invoke）
{ "registryAfter": 7 }
// → TrustBundle
{ "registryRev": 8, "registry": [ /* registryAfter 與目前版本不同時給整份，相同時省略（讀成 null） */ ] }
```

`registryAfter` 是裝置目前採用的版本（小於 0 回 `invalid`）。只要版本不同就給整份（包括伺服器還原到舊備份、版本比裝置的舊），裝置以整份換掉自己的。

---

## 8. 上限（`SyncLimits`，伺服器在 HelloResponse 告訴裝置）

| 項目 | 預設 | 超過時 |
|---|---|---|
| 單則 hub 訊息 | 1 MB | 用戶端送出前自檢，永遠不送超過（超過的話伺服器會關掉整條連線） |
| 批次 | ≤256 KB 且 ≤500 筆；最多 4 批在途 | 切成多批；伺服器整批回 `too_large` |
| 單筆 payload（解壓後） | ≤128 KB | 第 3 階段的 payload 遠小於此；第 4 階段的對話事件把大字串改成 blob 參照（14.2） |
| 拉取回應 | ≤500 筆或 1 MB | `more: true` |
| 心跳 | 每 15 秒；60 秒沒收到當成離線 | 用量改派 |
| 存取權杖 | 15 分鐘，到期前 2 分鐘換新 | — |
| 配對 | 待核准最多 5 筆、10 分鐘到期、輪詢間隔 2 秒 | `too_many_pending`、`expired_token`、`slow_down` |
| 即時更新 | 每個對話 `maxLiveHz` 4 則／秒、單則 ≤64 KB；協商出 `live2` 時 `maxLiveDeltaHz` 8 則／秒（依大小降頻）、單則 ≤256 KB（14.3） | 擁有者自己不送；伺服器丟掉、只計數 |

---

## 9. 伺服器還原後的對帳

伺服器還原備份後：`restoreGeneration`＋1、每台裝置的串流標成需要對帳、伺服器 seq 起點拉高 10 億（新事件的 seq 不會與還原前發給裝置的重複）。

裝置在 HelloResponse 看到 `server.instanceId` 或 `restoreGeneration` 與 `seenServer` 不同：

1. 所有拉取游標重設為 0，重新拉目錄與用量；清空用量指派的 term 表（term 只在同一個伺服器實例、同一個還原世代內可比較，由這次的 ProveResponse 重建）。
2. ProveResponse 的 `resync: true`：`nextDeviceSeqExpected` 是 min(伺服器的值, 這台保留的最小 deviceSeq)；保留的列全部重送（`ApplyProve` 對帳模式）。已在備份裡的冪等，缺的補進來（伺服器以 `Missing` 套用並稽核）；備份比保留窗還舊時，第一筆以 `GapAccepted` 接受缺口。對帳期間伺服器以 `OutboxRules.ResyncFrom(這次 Hello 的 outbox)` 當 `resyncFrom`（outbox 是空的時候是 `nextDeviceSeq`：第 3 步重送的本機狀態從那裡接受缺口）；`next_device_seq` 追上這次 Hello 的 `nextDeviceSeq` 後才解除對帳模式（`OutboxRules.IsResyncDone`；不能用「保留的列都提交了」判斷，outbox 是空的時候一開始就成立，之後重送的新事件會被判成 `Gap`）。
3. 本機帳戶狀態（`link.upsert`）、CLI 狀態全部重送一次（新的 deviceSeq），補齊比保留窗還舊的缺口。
4. 記下新的 `server`，之後的 Hello 帶它。

`instanceId` 不同（伺服器重建、資料庫是新的）且身分金鑰也換了：先走第 3 節的本機確認。

---

## 10. 裝置的連線狀態機

```
Unpaired ──配對──► Disconnected ──► Connecting ──► Hello／Prove ──► Draining（重送 outbox＋拉取）──► Live
                        ▲  │ 退避 1, 2, 4, 8, 16 秒（±20% 抖動；Live 滿 30 秒後從頭算）
                        │  └ 網路恢復、從睡眠喚醒、使用者按「立即重試」時立刻重試
                        │
   停止重試、顯示原因：401／403（權杖被撤銷 → 請重新配對）、seq_regression（請重新配對成新的裝置身分）、
                      protocol（請更新 app）、伺服器身分要本機確認、伺服器缺資料（StopServerBehind）、批次被整批拒絕、
                      找不到裝置金鑰（不認得的金鑰錯誤持續到門檻也是，12.9）
   斷線後照退避重連：BatchAck 是不認得的整批錯誤（Reconnect）；裝置金鑰暫時打不開（TPM 忙碌、TBS 還沒起來，12.9，不連伺服器）
```

- 斷線期間所有功能照常；outbox 照常累積與合併；標題列小點顯示「同步：離線（N 筆待送）」，不跳錯誤視窗。
- `ServerNotice{shutdown, retryAfterSec}`：等那麼久再重連，不顯示錯誤。
- 伺服器連不上時桌面完整可用；未配對時不建立同步元件、不載入 SignalR 與 SQLite（離線優先）。
- **只更新**（1.4）：伺服器那一側（U2）只放行 `RefreshAccessToken` 與 `Heartbeat`，照樣送 `shutdown`、`appRelease`，撤銷時照樣中斷連線。桌面的「只更新」狀態在 U3：不送批次、不拉取，outbox 照常保留，換版重啟之後以完整連線補送。U3 之前的桌面不宣告 `update1`，照舊收到 `protocol`、停止重試（上圖）。
- **桌面的只更新狀態（U3 第二步，2026-10-22）**：Hello 宣告 `update1`；Prove 回 `updateOnly` 時引擎進 `UpdateOnly`（網頁 `state` 是 `offline`、說明「這台的 app 版本太舊：伺服器只提供更新…」），只開心跳與存取權杖的換新，不送批次、不拉取、不拉名冊、不上傳用量，功能只留 `update1`；存取權杖照樣保存，第 15 節的下載照常。Hello 回 `protocol` 時不停止、1 小時 ×（1 ± 20%）後重試；回 `draining` 時下一次連線不早於 30 秒 ×（1 ± 20%）；`shutdown` 的 `retryAfterSec` 也乘上 ±20% 的抖動。

---

## 11. 錯誤代碼

| 代碼 | 出現在 | 意思與裝置的動作 |
|---|---|---|
| `gap` | `BatchAck.error`（帶 `expected`） | 從 `expected` 重送 |
| `seq_regression` | `BatchAck.error` | 這台的 sync.db 倒退了：停止同步，提示重新配對（新的裝置身分） |
| `invalid` | `BatchAck.error`、`results[].rejected` | 整批：停止同步並告警；單筆：記下、不重送 |
| `too_large` | 同上 | 同上 |
| `conflict` | `results[].rejected` | 樂觀並行版本不符：不重送，等伺服器的事件蓋回 |
| `protocol` | Hello 的 `HubException`；`/pair/start` 的 400 | 照 1.4 的表：協定比伺服器支援的最高版本新（或小於 1），或協定太舊、app 版本太舊而且沒宣告 `update1`；配對不看 `update1`。提示更新 app。U3 之前的桌面停止重試，U3 起每小時重試一次 |
| `draining` | Hello 的 `HubException`；app 套件下載的 503 本文（第 15 節） | 伺服器排空中（部署換版；排空開始時已送 `ServerNotice{shutdown}`）：照 `retryAfterSec`、`Retry-After` 或退避稍後再試，不顯示錯誤。U3 之前的桌面不認得這個代碼，顯示「伺服器回應不認得的錯誤（draining）」並照退避重連，直到伺服器換版完成（過渡期的已知顯示） |
| `update_required` | 只更新的連線呼叫 `RefreshAccessToken`、`Heartbeat` 以外的方法時的 `HubException` | 這台的 app 太舊，只能下載更新（1.4）：不送批次、不拉取，下載並換版（U3） |
| `not_proven` | 任何方法的 `HubException` | 沒先 Hello／Prove 或 Prove 失敗：重新連線 |
| `invalid` | `Hello`、`Pull`、`PublishBatch` 的 `HubException` | Hello 的格式不合格（`nonceD` 不是 32 位元組、`outbox` 的位置互相矛盾等）、拉取的游標不合格（沒有游標、超過 16 個、`cls`／`stream` 不是剛好一個、不認得的類別、`after` 小於 0），或在途批次超過上限：用戶端的錯，記錄並重新連線 |
| `authorization_pending`、`slow_down`、`expired_token`、`access_denied`、`invalid_grant`、`too_many_pending`、`invalid_request` | 配對端點的 400 本文 `{error, interval?}` | 2.1 |
| `not_found`（404）、`invalid_request`（400）、`step_up_required`（403）、`too_many_devices`（409） | 核准端點的本文 `{error}` | 2.2（網頁顯示原因；`step_up_required` 先再輸入密碼再重送） |

`HubException` 在 .NET 用戶端收到時，訊息是 `An unexpected error occurred invoking '{方法}' on the server. HubException: {代碼}`（伺服器只放代碼、不放說明）：裝置取最後一個 `HubException: ` 之後的字串當代碼，不認得的代碼當成連線錯誤處理（退避重連）。

稽核：Hello／Prove 失敗、`observedSpki` 不符、`seq_regression`、對帳模式套用的 `Missing` 事件、配對的核准與拒絕、裝置的撤銷，一律寫伺服器的稽核紀錄（雜湊鏈）。

---

## 12. 裝置端細則（WP3.3）

桌面同步引擎（`src/Myriagent/Sync/`）照這份協定實作時另外決定、而且對方（伺服器、WP3.6 的瀏覽器）要知道的事。設計的對照表在設計文件 14.5。

### 12.1 連線、退避與重試

- 每次重連都建一個新的 `HubConnection`（不開 stateful reconnect、不用 `WithAutomaticReconnect`），重新 Hello／Prove。只用 WebSockets、略過 negotiate；自訂的 `WebSocketFactory` 自己設 `Authorization: Bearer chd_…`（不設 `AccessTokenProvider`、權杖不進查詢字串）、不送 `Origin`、HTTP/1.1，以 `ClientWebSocket.HttpStatusCode` 分辨升級失敗的 401／403。
- **建立連線最多 20 秒**（`SyncTimingRules.ConnectTimeout`：TCP、TLS、WebSocket 升級加 SignalR 交握，用戶端的交握逾時 15 秒在這之內）：超過就記「連線伺服器 20 秒沒有完成」（`connect_timeout`，暫時性）、照退避重連；升級已回 HTTP 狀態或 TLS 驗不過的照原本的分類。`StartAsync` 本身只看取消：2026-10-22 實測伺服器部署換版的那幾秒建立的連線 TCP 連得上、之後沒有任何回應，伺服器也不關它，沒有上限時要到第 330 秒才失敗（裝置在網頁上一直是離線）。
- 退避 1, 2, 4, 8, 16 秒、±20% 抖動（第 10 節）；Live 滿 30 秒之後斷線，次數從頭算。`ServerNotice{shutdown}` 的 `retryAfterSec` 夾在 1～600 秒（沒帶時 30 秒），和退避取較晚的。網路恢復（可用性或位址改變）、從睡眠喚醒、使用者按「立即重試」（`syncRetry`）時，退避中的引擎立刻重連（`ServerNotice{shutdown}` 指定的時間之前不提前，改排在那個時間）；停止（Stopped）、待本機確認（NeedsConfirmation）、暫停（Paused）不受影響。
- **連線世代**：每條連線一個編號，斷線（`Drop`）之後那條連線的所有回呼（BatchAck、Notify、UsageAssign、呼叫的結果）一律忽略，在途的批次作廢、由下一次 Prove 重新對齊。
- **送出世代**：每次 Rewind（`gap`）與重連時加一，每批記下送出時的世代。最多 4 批在途時，一次 `gap` 會讓後面幾批也各回一次 `gap`：舊世代的 `gap` 只減在途數、不再 Rewind（否則剛重送、正在途中的列會被改回 Retry 再送一次）。
- 送出沒有固定週期的計時器：有可送的列才排一次，批次間隔至少 100 ms。**BatchAck 逾時 60 秒**（只在有在途批次時計時）：斷線、照退避重連。閒置的 Live 只剩 15 秒一次的心跳與 SignalR 的 keepalive。
- BatchAck 的整批錯誤：`seq_regression`、`invalid`、`too_large` 停止同步；不認得的代碼斷線、照退避重連。
- 排空中的 `Hello` 回 `draining`（U2 的伺服器）：U3 之前的桌面不認得這個代碼，照第 11 節的規則當成連線錯誤、照退避重試，記錄與狀態是「伺服器回應不認得的錯誤（draining）」，排空解除或新版服務起來之後自己連上（過渡期的已知顯示；U3 起認得 `draining`）。排空開始時送的 `ServerNotice{shutdown}` 帶排空要求的 `retryAfterSec`（部署預設 30），桌面照本節第二點的規則（夾在 1～600 秒、和退避取較晚的）在那之前不重連；有這個通知的斷線不記「同步連線中斷」，狀態顯示「伺服器正在重新啟動」。直接停止服務時沒有通知，桌面當成連線中斷、照退避重連。收到 `appRelease` 時，U3 之前的桌面記「略過不認得的伺服器通知：appRelease」，同步照常。

### 12.2 交握：SPKI、Prove 前的訊息、存取權杖

- **SPKI 擷取**：TLS 驗證回呼記下這條連線葉憑證的 SPKI SHA-256（`HandshakeRules.SpkiSha256`），就是 Hello 的 `observedSpkiSha256`。正式環境走 WebPKI；只有設了 `MYRIAGENT_SYNC_DEV_TRUST=1`、而且連到的與配對時的伺服器都是迴路位址時，才接受驗不過的憑證（本機測試的自簽憑證）。TLS 終止代理換了憑證時，伺服器在 Hello 比對不符、稽核 `spki_mismatch` 並中斷，裝置不會走到 Prove、照退避重連（長情境 `MitmTests` 驗過）。
- **Prove 前收到的訊息不呼叫任何 hub 方法**：`Notify` 直接忽略（交握完成一定先拉一次）；`TrustChanged` 記下版本；`UsageAssign` 存在連線上，`ProveResponse.usageAssignments` 照 term 合併之後再併進去（先併進表的話，Prove 的清單會把沒列的 On 改成 Off）。Hello 的 `trustRevs.registry` 或之前記下的 `TrustChanged` 版本與本機不同時，交握完成前先 `GetTrust`：名冊驗過、自己沒被撤銷才進 Draining 送東西。
- **Prove 前的 `ServerNotice` 先不採用**：Hello 的 `hubSig` 驗過之前，通知的文字（`alert`、`certExpiring`、`minAppVersion`）與 `shutdown` 的 `retryAfterSec` 都可能是中間人注入的。Prove 成功之前收到的通知存在連線上（最多 8 則），交握完成才依序套用；交握失敗就隨連線丟掉（不影響這次的失敗原因與退避）。`shutdown` 只有在驗證過的連線上才把斷線原因記成伺服器停機。通知文字只屬於帶來它的那條連線：每次交握完成先清掉上一條的文字。
- **存取權杖**（`cha_`）：第 3 階段沒有使用它的端點，ProveResponse 帶的不保存、不排定時刷新；第 4 階段要用時才以 `RefreshAccessToken` 換。閒置時因此少一個喚醒。

### 12.3 名冊一律整份取代

版本只要不同（不論大小，包括伺服器還原到舊備份、版本變小）就 `GetTrust`，收到的名冊以 `TrustRules.CheckBundle` 檢查：格式不合格就**只記錄、沿用上一份**；自己那一筆被撤銷就停止（SelfRevoked），名冊裡沒有自己就停止（NotInRegistry）；其他一律整份換掉、存進 sync.db。裝置不檢查版本大小，也不驗任何簽章（7 節：伺服器說了算）。`GetTrust` 呼叫本身失敗（包括回 `invalid`）時記錄、斷線、照退避重連：沒拿到名冊就不能確認自己沒被撤銷，交握中的連線不進 Draining（和 `UsagePublish` 的 `invalid` 只記錄不同，12.5）。長情境 `RestoreTests` 驗過還原到另一台配對之前的備份：舊裝置的名冊版本 2 → 1、整份取代（沒有後來配對的那台），後來配對的那台 Hello 得到 401、停止。

### 12.4 伺服器還原後的對帳順序（9 節的裝置端，D15）

1. Hello 回的 `server` 和 `seen_server` 不同：同一個交易裡清空游標、目錄快取與 published 摘要，記下 `reconcile=1`、`reconcile_through`（目前發到的最後一個 deviceSeq）與新的 server（pending）；**`seen_server` 先不改**。記憶體裡的用量指派（term 表）也清空：還原後伺服器的 term 會倒退，沿用舊表的話 term 較小的新指派一律被丟掉（零台查或兩台同時查）；這次 Prove 的 `usageAssignments` 重建，之後的 `UsageAssign` 照常合併。
2. 以最後的帳戶與 CLI 快照重算發布：`link.upsert`、`cli.state` 以新的 deviceSeq 排進 outbox。
3. Prove 回 `resync:true`：`ApplyProve` 把保留的列全部改成重送，交握完成後照常送出。
4. `reconcile_through` 以前的列都完成、而且這條連線拉到 `more:false` 之後，才把 pending 寫成 `seen_server`、`reconcile=0`。對帳期間不修剪 outbox（伺服器只在第一個跳號接受缺口）；中途斷線時 `seen_server` 還是舊的，下一次 Hello 從第 1 步重來。

長情境 `RestoreTests` 驗過：備份之後再寫 500 筆、停機還原、啟動，2 秒內對帳完成，伺服器上這台 deviceSeq 1..N 各一筆、內容等於本機 outbox，`link.upsert` 與 `cli.state` 以新的 deviceSeq 重送；對帳期間的每一次取樣 `seen_server` 都還是舊的。

### 12.5 用量的閘門與上傳（6.4 的裝置端，D14）

- **閘門**（`UsageSyncRules.ShouldPoll`，AccountManager 的定期與手動查詢都經過它）：未配對、暫停、停止、待確認時照本機原本的頻率查；這個帳戶沒有連結、連結不是 ready 或身分不符時查；Live、Draining、Handshaking 時只有被指派（On）才查——沒收到這個帳戶任何指派（Unknown）也不查；斷線（Idle、Connecting、Backoff）未滿 5 分鐘不查、滿了查（不知道何時斷線就當成已經很久）。不查時不標過期（畫面顯示共享的用量）；但顯示的用量（含共享的）有時間窗到了重置時間，輪詢每一拍都照 `resets_at` 推算歸零、標成過時（`UsageRules.ApplyDueResets`，2026-10-29：沒有一台在查時一直停在重置前的數字）。
  - 後果：剛配對或剛啟動、這台還沒拉到**自己的連結**之前，閘門照設計放行（「沒有連結→查」）。長情境 S12 量到同一身分三台同時連上時，第三台在拉到自己的連結前多查了一次；拉到之後恰好一台放行。
- **上傳**（`UsageSyncRules.Decide`，只在 Live、連結 ready 而且身分相符、快照不是過期的時候）：來源 `api`、這台是 On、而且是新的一份（`fetchedAt` 和上次帶 term 上傳的不同）→ 帶目前的 term 上傳；來源 `session`（對話中的 rate limit）→ 不帶 term，每帳戶至少隔 60 秒；其他不傳。中樞的帳戶卡有短節流：同一個帳戶在節流時間內先查到 api、又收到 session 時，只有最後一份會被看到與上傳。
- `UsagePublish` 回 `invalid`（11 節的表沒列）：**只記錄、不重連**。
- 共享用量（拉到的 `usage.snapshot`）：自己發的略過；套用到這台連結到該邏輯帳戶的本機帳戶，來源 `shared`，歷史點的時間用事件的 `createdAt`（伺服器收到的時間）；目前值依 seq 取最後一筆，不和本機的 `fetchedAt` 比較。追趕積壓期間（還不是 Live），本機目前值是自己查到的（api／session）時，共享的只記進歷史。

### 12.6 帳戶目錄的發布（6.1、6.2 的裝置端）

- **`account.update` 每個邏輯帳戶最多一筆待送**（合併鍵 `account:{logicalAccountId}`）：還沒送出時又改名、改顏色或重排，新的一筆併入待送那筆的 `label`、`color`、`sortOrder`（新的值優先），不會有兩筆帶同一個 `baseVersion`、後送的那筆被判 `conflict`。
- **`baseVersion`**＝目錄快取的版本＋「已送出、伺服器因它發的 `account.upsert` 還沒拉回」的 `account.update` 筆數（狀態是已送出／重試，或已確認而且 seq 不小於目錄的游標）。`prefs.update` 同樣（每個鍵）。這依賴伺服器的語意：**每接受一筆，版本正好加一**（`Catalog.UpdateAccount`、`Prefs` 的 `version+1`）；伺服器改了這一點，裝置的連續修改會變成 `conflict`。
- **`sortOrder` 同分**：伺服器只在建立邏輯帳戶時寫 `sortOrder`（建立那台的本機索引），兩台各自以索引 0 建的帳戶就同分。重排時把現有的值整理成嚴格遞增（同值往後推一格、夾在 ±1,000,000）再分配，同分的兩個帳戶對調也一定送出不同的值。套用目錄的順序時，同分照這台目前的清單位置排（不照 logicalAccountId），不會打亂使用者的清單。伺服器與瀏覽器的同分排序（`sort_order`→`created_at`→id）和桌面不同：`account.upsert` 沒有 `createdAt`，要對齊得改 Protocol 與伺服器（已知差異）。
- **同一台兩個本機帳戶身分相同**（`identityKey` 相同）：只發布一個——已連結的那個，沒有就是清單順序的第一個；其他的不送 `link.upsert`、不套用目錄的名稱與顏色，用量閘門照「沒有連結」處理（查本機、不上傳）。否則兩個帳戶對同一個 (邏輯帳戶, 裝置) 互相覆蓋，連結來回翻轉。

### 12.7 時間窗的鍵

伺服器只收 `[A-Za-z0-9-_]`、≤32 字的時間窗鍵（6.4、`UsageRules.Validate`），本機的鍵卻有 `:`（Claude 的 `weekly:模型名`、Codex 的 `{limitId}:{分鐘數}`）。上傳前以 `UsageWindowKeyRules.Encode`（Protocol，桌面與伺服器共用）跳脫：`_`→`__`、`:`→`_c`、`.`→`_d`、空白→`_s`；其他字元或跳脫後超過 32 字的窗丟掉；沒有這些字元的鍵（`session`、`weekly`）不變。例：`weekly:Opus`→`weekly_cOpus`、`codex:300`→`codex_c300`。拉到的 `usage.snapshot` 以 `UsageWindowKeyRules.Decode` 還原（不是這種跳脫格式的鍵，例如尾端單獨的 `_`、不認得的跳脫，整個照原樣）。伺服器存的、累積走勢用的都是跳脫後的鍵（不改資料庫內容）；**瀏覽器閘道（`AccountViewRules.Usage`）送給網頁時以同一套規則還原**，網頁看到的鍵與桌面相同（`weekly:Opus`，web-protocol 第三節 UsageWindow）。解碼不是一對一：照原樣的 `a_x` 與合格式的 `a__x` 都得到 `a_x`（只有不是這個實作送的鍵才會撞），所以給網頁的鍵是同一份用量一起還原（`UsageWindowKeyRules.DecodeAll`，閘道與桌面共用）：還原後的鍵等於這份用量裡另一個原本的鍵時送原本的鍵，不論順序，送出的鍵兩兩不同（網頁拿鍵當時間窗的識別）；沒有鍵的窗略過（WP3.7 步驟 2、合併前審查）。

### 12.8 配對

- `/pair/start` 回 429：以 `rate_limited` 結束這次配對（帶 Retry-After，網頁顯示稍後再試）。`/pair/token` 的輪詢：`authorization_pending` 照目前間隔；`slow_down` 間隔＋5 秒（或伺服器給的較大值）；429 照 Retry-After（不短於目前間隔）；網路錯誤照間隔重試；其他終止的錯誤直接結束。
- **本機到期的寬限**：本機算的到期（`expiresIn`）之後不停（伺服器說了才算），再輪詢最多 5 分鐘——伺服器對「已核准、還沒換權杖」的請求在到期後寬限 5 分鐘，核准落在到期前幾秒時本機不能先放棄；超過就以 `expired_token` 結束。
- 配對輪詢期間只用 HttpClient；SignalR 與 SQLite 只在「配對成功、寫 sync.db 的那一刻」與「已配對時的延後啟動」才載入（未配對時一個都不載入，S6）。

### 12.9 裝置金鑰打不開（WP3.7）

開裝置金鑰（啟動、之後每次 `Connect`）或 Prove 簽章失敗時，依 CNG 回的錯誤碼分類（`DeviceKeyErrorRules`）。這只是裝置本機的行為，線上沒有新的訊息；伺服器看到的只是這台暫時沒有連線（或連上之後在 Prove 之前斷線）。

- **不存在**（`NTE_BAD_KEYSET`、`NTE_NO_KEY`、`NTE_NOT_FOUND`；`CngKey.Exists` 回 false；開到的公鑰和 device.json 的 `devicePubKey` 不同）：停止（`KeyMissing`），提示解除配對後重新配對。
- **暫時性**（TPM 忙碌、自我測試、字典攻擊鎖定、資源不足；Platform Crypto Provider 還沒準備好、為了防字典攻擊略過授權〔`NTE_AUTHENTICATION_IGNORED`、`TPM_E_PCP_AUTHENTICATION_IGNORED`：字典攻擊鎖定經 NCrypt 回來的實際形式〕；TBS 服務沒在跑或啟動中；逾時與忙碌，完整的表在規則的註解）：照第 10 節的退避步階重試、不限次數，金鑰打不開時不連伺服器；`sync.detail` 是「裝置金鑰暫時無法使用（0x…），N 秒後重試」。簽章時遇到的關掉這把金鑰、斷線，下次 `Connect` 重開。
- **不認得的**：先當暫時性重試；連續 10 次、而且從第一次起滿 10 分鐘才停在 `KeyMissing`（說明是「裝置金鑰一直無法使用（0x…），請解除配對後重新配對」）。簽章成功才把次數歸零；紀錄只在記憶體，重開 app 從頭算。
- 金鑰暫時打不開時引擎照常處理本機的事件（寫進 outbox）；「立即重試」、恢復暫停、網路恢復都會先重開金鑰。

---

## 13. 帳戶宣告與登入到期（WP3.4）

桌面在帳戶目錄上另外做的事（設計 14.7）。伺服器、Protocol 與 sync.db 的結構都沒有改：用的都是 `link.upsert` 既有的欄位。

### 13.1 `link.upsert` 由裝置宣告 `logicalAccountId`

- **「在這台登入」**（網頁的 `accountAdd {logicalAccountId}`）建立的本機帳戶存下宣告的邏輯帳戶（`AgentAccount.LogicalAccountId`）。宣告的帳戶**還沒登入也送** `link.upsert`，`logicalAccountId` 是宣告的值、`identityKey` 通常是 null；伺服器照 6.1 的順序（這個本機帳戶已有的連結 → 裝置認定的 `logicalAccountId`，要在目錄裡、沒刪除、種類相同 → `identityKey`）連到它，不另建邏輯帳戶。
- **目錄的對應優先**：目錄裡已經有這個本機帳戶的那一格時，一律帶目錄的邏輯帳戶，不看宣告（和伺服器的順序相同）。
- **同一台同一個邏輯帳戶只認一個**：伺服器以 (邏輯帳戶, 裝置) 為主鍵，後到的會把前一個本機帳戶蓋掉。裝置只在下列條件都成立時才帶宣告（`CatalogSyncRules.ResolveLinkLogical`）：格式合格（`la_` 加 26 字 Crockford）、目錄裡有而且種類相同、這台還沒有本機帳戶連到它、清單裡前面的帳戶還沒宣告它。不成立的帳戶照身分鍵連結（沒有身分就不送）。網頁的 `accountAdd` 在宿主就先擋下重複的宣告（WPR `accountAdd`）。
- **摘要**：未連結的帳戶摘要含 `logicalAccountId`，宣告改變會重送；連結之後摘要不含它（和名稱、顏色、順序一樣由目錄決定）。沒有宣告的帳戶這個欄位是 null、序列化時省略，既有帳戶的摘要不變。
- **身分不符只標記**：宣告的帳戶之後登進了別的帳號，伺服器照 6.1 把那一格標成 `identityOk: false`，邏輯帳戶不換、不另建（`Sync/WithHub/AccountClaimScenarioTests`）。本機發起的登入不因此自動登出。

### 13.2 Claude 的 org 不明時的身分鍵

Claude 帳戶已對應或宣告了邏輯帳戶、這台**沒有發布過**這個帳戶的身分鍵、org 不明（保守模式、權杖待更新）、email 已知時，拿目錄裡那個邏輯帳戶的 `identityKey` 最後一個 `:` 之後的 email 比對（不分大小寫）：

- 相同：不送身分鍵（null）。伺服器在 `identityKey` 是 null 時一律當成相符。例外：org 不明時以 CLI 設定的 `oauthAccount.organizationUuid`（未確認，只用來判斷不符；保守模式下、CLI 設定的 email 和帳戶的相同才有）比對目錄鍵的 org 段（`claude:` 之後第一個 `:` 與最後一個 `:` 之間，區分大小寫）；目錄鍵有 org 段而且不同時，也送 `claude:{email 小寫}`。這個值絕不寫進身分鍵或任何欄位。
- 不同：送 `claude:{email 小寫}`，讓伺服器標成身分不符。

其他情況的規則不變（org 已知送 `claude:{org}:{email}`；發布過的照發布過的鍵比對；沒對應也沒宣告、又不知道 org 時不發布）。

### 13.3 `loginExpiresAt`

- Claude：取憑證檔 `.credentials.json` 的 `claudeAiOauth.refreshTokenExpiresAt`（整數毫秒），> 0 而且不超過 9999-12-31（`AccountIdentityRules.MaxUnixMs`）才送，否則 null。不讀、不送任何權杖字串；不用「約 28 天」推估。換發權杖時這個值不會延後（2026-10-23 實測，protocol-notes 的 E-13 第三次）：是從登入起算的固定期限，重新登入才重新起算（設計 14.7）。
- Codex：一律 null。
- 伺服器照原樣存進 `account_links.login_expires_at`、放在 `account.upsert` 的 `links[].loginExpiresAt`（不檢查範圍）。

### 13.4 不放進 payload 的東西

任何 payload（`link.upsert` 在內）都不放權杖、裝置碼登入的驗證碼與網址、配對碼（6 節）。Codex 的裝置碼：本機發起的只顯示在那台桌面（設計 14.7 的 D1）；第 4 階段起瀏覽器發起的（WP4.8，設計 14.18）另經 `CommandOutput`（14.6）只給發起的連線——伺服器照型錄的輸出白名單投影後轉送、不保存，仍不進任何 payload、記錄檔與遠端操作紀錄，那台桌面的帳戶卡同時顯示同一組驗證碼。Claude 的遠端登入（2026-10-26 起的 app 內登入）同樣只經 `CommandOutput` 給發起的連線：第一則 `accountLogin` 帶登入網址 `loginUrl`（只收 https 的 `claude.com`／`claude.ai`，伺服器投影時再驗一次、不合格整則丟掉），貼回來的授權碼以型錄的 Device 指令 `accountLoginCode`（層級 2，`code` ≤2048 字）交給那台；授權碼和其他指令的參數一樣在信封裡（伺服器的指令列、那台的 `remote_audit` 留著信封本文），不進 payload 與記錄檔：它只能用一次，而且 OAuth 的 PKCE 要那台 CLI 手上的 verifier 才換得到權杖，留著的授權碼沒有用處。驗證碼與 loginId 也不寫進記錄檔：Codex 的錯誤文字先遮掉兩者才記（`CodexDeviceCodeRules.Redact`）。

---

## 14. 第 4 階段：對話串流、遠端指令與信封（WP4.1 契約）

第 4 階段（對話同步、網頁即時檢視、遠端操作）的裝置↔伺服器契約。設計與理由在設計 14.11（決策代號 D1～D58 與擁有者對 Q1～Q5 的回答），網頁那一側在 `docs/web-protocol.md` 的〔遠端〕。**WP4.1 只立型別、規則與接縫**：伺服器不送本節的新訊息、裝置不發布對話事件，遠端指令照舊不會執行（伺服器回 `not_available`，電腦的預設授權者一律回 `unsupported`）；各小節寫明由哪個工作包開始用。第 1～13 節不變。

| 主題 | 程式（Protocol；WP4.1 的步驟） |
|---|---|
| 對話事件種類、placement、payload、線上投影、共用驗證 | `Sync/ConversationEventKinds.cs`、`Sync/ConversationPayloads.cs`、`Sync/ConversationWire.cs`、`Sync/ConversationEventRules.cs`（步 3） |
| 即時更新、項目順序、卡片摘要 | `Sync/LiveRules.cs`、`Sync/OrdRules.cs`、`Sync/RequestDigestRules.cs`（步 4） |
| 權杖 DLP | `Sync/CredentialLeakRules.cs`（步 5） |
| 遠端指令的型錄、形狀、層級、驗證時間、政策、速率、卡片、資料夾、佇列、秘密回答 | `Remote/*.cs`（步 6） |
| 信封、伺服器時鐘 | `Security/CommandEnvelope.cs`、`Security/ServerClockRules.cs`、`Security/P256KeyRules.cs` 的 `VerifyHash`（步 7） |
| 新的 hub 訊息、功能宣告、結果與即時更新的檢查 | `Sync/DeviceHubMessages.cs`、`Sync/SyncProtocol.cs` 的 `DeviceHubMethods`、`Sync/DeviceFeatureRules.cs`、`Remote/RemoteResultRules.cs`、`Sync/LiveRules.cs` 的 `CheckRelay`（步 8） |
| 伺服器的接縫（不是 Protocol）：裝置 hub 的方法允許清單、伺服器的功能宣告與協商、連線事件、對話與指令的處理者介面、`DeviceAccess` 政策、sync.db 結構 v2 | `src/MyriagentHub` 的 `Realtime/DeviceHubRules.cs`（`CheckInvocation`）、`Realtime/DeviceHubFeatures.cs`、`Realtime/ConnectionRegistry.cs`、`Realtime/BrowserConnections.cs`、`Realtime/BrowserCommandSeams.cs`、`Sync/ConversationDirectory.cs`、`Data/SyncSchema.cs`（步 12；`src/MyriagentHub/README.md`「給其他工作包的接手點」） |

### 14.1 功能宣告

- `HelloRequest` 最後加 `features`（字串陣列，選用），`HelloResponse` 在 `rotation` 之後加同名欄位；雙方實際啟用的是兩邊的交集（`DeviceFeatureRules.Negotiate`：依裝置宣告的順序、去重）。名稱：`conv1`（對話事件與依串流的拉取，14.2）、`live1`（即時更新與觀看，14.3）、`cmd1`（遠端指令，14.4～14.6）、`media1`（媒體與檔案轉送，14.3）、`update1`（只更新模式，app 自我更新 U2：裝置宣告它，app 太舊時才拿得到只更新的連線，1.4；不對應任何方法）、`peer1`（桌面遠端操作，14.7）、`stat1`（各電腦的即時狀態，14.8）、`tok1`（各帳戶的 token 計數，14.9）、`tok2`（其他電腦的 token 計數，14.10）、`live2`（即時更新改送增量，14.3 的「增量」），常數在 `DeviceFeatureRules`（`Conv1`、`Live1`、`Cmd1`、`Media1`、`Update1`、`Peer1`、`Stat1`、`Tok1`、`Tok2`、`Live2`）。協商時不合格的名稱略過、最多 32 項，任一方是 null（沒宣告）就是空的；雙方都宣告、這版不認得的名稱也在交集裡，用不用由各自的程式決定（`Has`）。
- 伺服器檢查（`DeviceHubRules.CheckHello` 呼叫 `DeviceFeatureRules.IsValid`）：≤32 項、每項 1～32 字、只能是 `[a-z0-9]`（重複的照收），不合格整個 Hello 回 `invalid`；不認得的名稱照收、不啟用。
- 沒宣告（第 3 階段的裝置、WP4.1 的桌面）＝第 3 階段的行為：伺服器不送本節的任何訊息、不對它路由遠端指令（瀏覽器那邊回 `unsupported`）；裝置不發布對話事件。`features` 是 null 時 Hello 與 HelloResponse 的 JSON 和第 3 階段逐位元組相同。
- `features` 不在 `hubSig`／`deviceSig` 的簽章範圍內（第 3 節）。可以接受：通道已由 TLS 與 SPKI 綁定保護，功能只決定「送不送」，任何安全檢查都在裝置端重做、不依賴它。
- `prov1`（Grok、Antigravity 也同步；2026-10-22，設計 `docs/plans/2026-10-22-grok-antigravity-sync.md`）：伺服器宣告它，表示帳戶目錄（`link.upsert`，身分鍵見第 6.1 節）、對話事件（`conv.created`／`conv.meta` 的 `provider`）、`cli.state`（最多 4 種）都收 Claude、Codex、Grok、Antigravity。桌面只在目前這條連線協商出它時才發布後兩種，沒有協商出（舊伺服器、部署回滾）時和以前一樣只同步 Claude、Codex——舊伺服器的這三條檢查只收兩種，送了會被判 invalid、佔掉 deviceSeq 或讓對話被封鎖。不對應任何方法。
  - **桌面**（`SyncEngine.Prov1.cs`、`SyncProviderRules.Publishable`）：有 `conv1` 的引擎（對話盒子在）在 `conv1`、`live1` 之後宣告 `prov1`。交握完成、第一次拉取之前記下這條連線有沒有 `prov1`（kv `prov1_armed`，離線時照它規劃），和上一條不同就重算目錄。沒有 `prov1` 時：Grok、Antigravity 的帳戶不送 `link.upsert`、忘掉發布過的摘要（不送 `link.removed`），`cli.state` 只帶 Claude、Codex（`CliStateRules.ForServer`），對話照「沒有 `conv1`」的做法延後（不寫事件、已建立的記 `needs_redo`；**不是** localOnly，不送伺服器刪除），有 `conv1` 的連線送出前把這兩種對話還沒送的列換成 `{}`（被 invalid 擋下後重整）。`prov1` 回來時目錄的摘要不同、整筆重送，延後過對話時進 Live 重整一次。第一次協商出 `prov1` 時對話中繼資料（類別 `meta`）的游標歸零一次（kv `prov1_replicas`），補拉舊版當時略過的這兩種的對話。Antigravity 的身分鍵用 agy 記錄裡登入成功的信箱（protocol-notes 的 Antigravity 一節），讀不到就沒有身分、不建邏輯帳戶。
- 每個方法要哪個功能（`DeviceFeatureRules.RequiredFor`）：`SnapshotRequest` 要 `conv1`；`LiveRelay`、`WatchChanged` 要 `live1`；`CommandDelivery`、`CommandResult`、`CommandOutput` 要 `cmd1`；`MediaRequest` 要 `media1`；`PeerCmd`、`PeerReply` 要 `peer1`；`StatsPublish`、`StatsUpdate` 要 `stat1`；`TokensPublish` 要 `tok1`；`TokensUpdate` 要 `tok2`；`SchedulesPublish`、`SchedulesUpdate` 要 `sched1`；`LiveDelta`、`LiveResync` 要 `live2`；第 3 階段的方法不用。兩邊都照它決定送不送。
- **裝置端**（桌面的同步引擎，`Sync/SyncEngine.Contracts.cs`，WP4.1 步 10）：Hello 的 `features` 由各工作包的登記點依序加（WP4.3 的 `conv1`／`live1`、WP4.4 的 `cmd1`、WP4.7 的 `media1`；全部合併後是 `conv1, live1, cmd1, media1`，`media1` 只在也宣告了 `conv1`、v3 的對話表在的時候宣告），沒有就是 null；加出來的不合格（`IsValid` 不過）時整份不送、記錯誤（伺服器會整個拒絕 Hello）。伺服器身分驗過之後才以 `Negotiate` 記下這條連線的功能；之前收到的、沒協商出對應功能的伺服器訊息一律略過（每條連線每種只記一次 log），不交給處理、不回任何東西。
- 伺服器的 `device.upsert` payload（`DeviceUpsertPayload`）最後加選用的 `features`：那台目前在線的連線協商出的功能，沒有連線或是空的時候省略（舊版拉取端照舊、黃金檔不變）。app 自我更新 U2 在它之後再加選用的 `app`（6.6）。
- **伺服器端**（MyriagentHub，WP4.1 步 12）：伺服器宣告的功能由各工作包在自己的 `Add*` 以 `AddDeviceHubFeature(名稱)` 登記（`DeviceHubFeatures`；依登記的順序、去重，不合格的名稱是程式錯誤）。**沒有任何登記時** `HelloResponse.features` 是 null（JSON 與第 3 階段逐位元組相同），任何裝置協商出的都是空的：WP4.1 一項都沒有登記，WP4.2 起正式程式一律有，只有測試（`HubTestHost` 的 `withoutDeviceFeatures`）會拿掉。**U2 起 `AddHubReleases` 再登記 `update1`**（只更新模式，1.4；不對應任何方法）：只有也宣告 `update1` 的裝置協商出它，第 3 階段的裝置與 U3 之前的桌面協商不到；只更新的連線只留 `update1`，第 4 階段的功能一律不啟用（`DeviceHubRules.ConnectionFeatures`）。Hello 時以 `Negotiate(裝置的, 伺服器的)` 算出這條連線的功能，記在 Hello 的狀態、Prove 時交給連線登記（`DeviceConnection.Features`）；伺服器送第 4 階段的訊息之前一律問 `DeviceConnection.Allows(方法)`（`RequiredFor` 加協商結果）。`device.upsert` 的 `features` 取那台最新登記的連線（重連時舊的還沒斷就有兩條）；最新的那條斷了、剩下那條的功能不同時重發一次 `device.upsert`。
- **2026-10-22 起伺服器另宣告 `prov1`**：`RealtimeRegistration` 在 `conv1` 之後無條件登記，`HelloResponse.features` 是 `["conv1","prov1","live1","update1","cmd1","peer1","media1"]`（依登記的順序）。
- **2026-10-22 起伺服器另宣告 `stat1`**（14.8）：`HubApplication` 在 `AddHubMedia` 之後呼叫 `AddHubStats`，無條件登記在最後，`HelloResponse.features` 是 `["conv1","prov1","live1","update1","cmd1","peer1","media1","stat1"]`。桌面在 `update1` 之後宣告它（`SyncEngine.Stats.cs`）。
- **2026-10-23 起伺服器另宣告 `live2`**（14.3 的「增量」）：`RealtimeRegistration` 在 `live1` 之後無條件登記，`HelloResponse.features` 是 `["conv1","prov1","live1","live2","update1","cmd1","peer1","media1","stat1","tok1"]`。桌面在 `live1` 之後宣告它（`SyncEngine.Conversations.cs`）；`HelloResponse.limits` 最後加選用的 `maxLiveDeltaHz`（8），只在這條連線協商出 `live2` 時帶（`DeviceHubRules.HelloLimits`；沒協商出的連線線上省略，回應和第 3 階段逐位元組相同）；舊伺服器沒有＝null，桌面照 `maxLiveHz` 送整則。
- **2026-10-23 起伺服器另宣告 `tok1`**（14.9）：`HubApplication` 在 `AddHubStats` 之後呼叫 `AddHubTokens`，`HelloResponse.features` 是 `["conv1","prov1","live1","update1","cmd1","peer1","media1","stat1","tok1"]`。桌面在 `stat1` 之後宣告它（`SyncEngine.Tokens.cs`）。
- **2026-10-23 起伺服器另宣告 `tok2`**（14.10）：`AddHubTokens` 在 `tok1` 之後登記，`HelloResponse.features` 是 `["conv1","prov1","live1","live2","update1","cmd1","peer1","media1","stat1","tok1","tok2"]`。桌面在 `tok1` 之後宣告它（`SyncEngine.Tokens.cs`）。
- **2026-10-25 起伺服器另宣告 `exec1`**（14.11）：`AddHubCommands` 在 `peer1` 之後登記，`HelloResponse.features` 在 `peer1` 之後多一個 `exec1`。桌面在 `peer1` 之後、宿主接得住遠端執行指令時宣告它（`SyncEngine.Exec.cs`）。
- **2026-10-26 起伺服器另宣告 `sched1`**（14.12）：`HubApplication` 在 `AddHubTokens` 之後呼叫 `AddHubSchedules`，`HelloResponse.features` 在 `tok2` 之後多一個 `sched1`（之後才是依設定登記的第 5 階段 `log1`、`hand1`）；`SchedulesPublish`、`SchedulesUpdate` 要它，遠端的 `scheduleAction`、`scheduleAgent` 要目標協商出它（`CommandRouter` 先回 `unsupported`「版本太舊」）。
- **2026-10-30 起伺服器另宣告 `collab1`**（14.14）：`HubApplication` 在 `AddHubSchedules` 之後以 `AddDeviceHubFeature` 登記，`HelloResponse.features` 在 `sched1` 之後多一個 `collab1`。桌面在 `cmd1`、`peer1`（與 `exec1`）之後宣告。
- **第 5 階段的 `log1`、`hand1`**（16.1）：伺服器依 `Hub:Logs:Declare`、`Hub:Handoff:Declare` 登記在 `tok1`、`tok2` 之後，WP5.1 起兩個開關預設都關（`features` 不變）。
- **WP4.2 起伺服器一律宣告 `conv1`、`live1`，沒有開關**（答 1；`RealtimeRegistration` 以 `AddDeviceHubFeature` 無條件登記），所以 `HelloResponse.features` 不再是 null：WP4.2 起是 `["conv1","live1"]`，WP4.5 起在 `AddHubCommands` 加上 `cmd1`（`["conv1","live1","cmd1"]`，同樣沒有開關），WP4.7 起在 `AddHubMedia` 加上 `media1`（`["conv1","live1","cmd1","media1"]`，同樣沒有開關；14.3 的「媒體與檔案」），U2 起在 `AddHubReleases` 加上 `update1`（`["conv1","live1","update1","cmd1","media1"]`：`HubApplication` 呼叫 `AddHubReleases` 在 `AddHubRealtime` 之後、`AddHubBrowser`（含 `AddHubCommands`）與 `AddHubMedia` 之前，所以依登記順序排在 `live1` 之後、`cmd1` 之前；同樣沒有開關），桌面遠端操作起在 `AddHubCommands` 的 `cmd1` 之後加上 `peer1`（`["conv1","live1","update1","cmd1","peer1","media1"]`，同樣沒有開關，14.7）。伺服器收對話事件時不看連線協商出的功能（14.2 的「伺服器」）；「沒有 conv1」只會出現在伺服器回滾到 第 3 階段的舊版 或裝置本身沒宣告時。
- **桌面什麼時候宣告 `conv1`、`live1`**（WP4.3 D3、D38，合併閘門）：只在 `PublishPlanRules.DeclaresConv1` 成立時宣告：引擎有對話盒子（正式的引擎工廠一律帶）、桌面 sync.db v3 的對話表在（`SyncStore.ConversationTablesReady`；升級失敗時照舊版結構開啟、之後重試，成功後下一條連線才宣告）、`CredentialLeakRules.Version` ≥ `ConversationPublisher.RequiredDlpVersion`（2，權杖 DLP 補洞 F10 的那一版）。伺服器沒有任何功能開關，所以宣告 `conv1` 的桌面組建一定同時帶著 F10 與秘密回答的清除（契約:464 的 R3）；這兩者與這個閘門的測試在同一個測試專案。桌面只在協商出 `conv1` 的連線上發布對話事件、拉 `meta` 與複本串流、處理 `SnapshotRequest`；即時更新與觀看要 `conv1` 與 `live1` 都協商出。
- **沒有 `conv1` 的連線**（WP4.3 D13；兩包對齊的提案 C10，取代契約:780「卡在第一筆對話列」）：伺服器回滾到沒有 `conv1` 的版本（第 3 階段的舊版），或桌面自己沒宣告時，桌面不寫新的對話事件，對已知的對話記重整（`publish_state.needs_redo`）；在這條連線上送出之前，outbox 裡 **Pending** 的對話列原地換成 `{}`（保留 deviceSeq、kind、stream、conversationId、epoch），Sent 與 Retry 的列內容已凍結、不動（換掉會讓伺服器已提交的那一筆變成 `seq_regression`）。伺服器對 `conv.deleted` 以外的每一種對話事件，收到 `{}` 都回 `invalid`（`Validate` 本來就擋；第 3 階段的舊版 對所有對話事件都回 `invalid`，`rejected_events` 只存 `{}`）；`{}` 的 `conv.deleted` 就是真的刪除（它的 payload 本來就是 `{}`）。被擋下的對話列（含 `conv.deleted`）記重整，下一次 conv1 連線補送，所以回滾期間第 3 階段的目錄同步照常、對話事件不遺失。離線時以 kv `conv_armed`（上一次進 Live 的連線有沒有 `conv1`，預設 false）決定寫不寫對話事件。

### 14.2 對話事件與串流（`conv1`；WP4.3 發布、WP4.2 物化）

對話事件**只由擁有者裝置寫**（伺服器只寫自己的稽核，不寫對話事件，D19；第 5 階段唯一的例外是伺服器寫的 `conv.lease`，16.2）。每筆事件帶 `conversationId`（32 個小寫十六進位，`^[0-9a-f]{32}$`；本機資料夾名大小寫都收，送出前轉小寫，收到大寫一律拒絕，D38）與 `epoch`（≥1；第 4 階段沒有租約，一律是對話的 `LeaseEpoch`，沒有時是 1）。串流是 `c:{conversationId}`；類別依種類（`ConversationEventKinds.Placement(kind, conversationId)`，第 3 階段的 `SyncEventKinds.Placement` 不變，對話種類照舊回 null）：

| kind | cls | payload（`Sync/ConversationPayloads.cs`） | 說明 |
|---|---|---|---|
| `conv.created` | `meta` | `ConversationMetaPayload`：`{provider, localAccountId?, title, permissionMode?, model?, effort?, customFolder, workingDirectory?, syncPolicy, createdAt?, updatedAt?, running, waiting, models?}` | 第一筆；回補時也從它開始 |
| `conv.meta` | `meta` | 同 `conv.created`，**每次都帶整份**（不是只帶改了的） | 改名、換模式與模型、換資料夾、換帳戶、執行中與等待回應改變（未讀不送） |
| `conv.turn` | `meta` | `{phase: "started"｜"completed", outcome?: "completed"｜"interrupted"｜"failed", usage?: {inputTokens, outputTokens, cachedInputTokens, costUsd?}}` | 一輪開始與結束；`started` 不帶其他欄位，`completed` 一定帶 `outcome`（`usage` 是那一輪的 token 數） |
| `conv.attention` | `meta` | `{kind: "question"｜"approval"｜"plan"｜"completed"｜"failed", itemId?, speech?}` | 要使用者注意的事（瀏覽器的 `notify` 與 `speak`）。`speech`（2026-10-23 起，選填；≤ 4000 字，2026-10-24 以前 ≤ 120 字——舊版伺服器與桌面把超過 120 字的這一筆當成不合格，伺服器先部署）＝語音通知念的句子（沒有字數上限，桌面超過 4000 字時在子句邊界截短）：對話的政策是 `full` 才帶簡要說明（完成了什麼、問什麼），其他政策只有「對話標題＋發生什麼事」；空字串＝不念（背景工作還在跑時的完成）；不帶（舊版桌面、失敗由 `conv.turn` 帶出的那一筆）時伺服器自己組只有標題的句子。舊版伺服器忽略這個欄位 |
| `conv.deleted` | `meta` | `{}` | 擁有者刪除（遠端刪除是移到回收區，D10） |
| `conv.item` | `item` | `WireItem`：一筆項目的線上投影（新增或整筆取代，以 `id` 判斷；帶 `ord`、`ver`） | 見下 |
| `conv.itemRemoved` | `item` | `{id}` | |
| `conv.queue` | `item` | `{entries: [{id, displayText}], more}`：佇列快照，秘密回答已遮蔽（D20） | 最多 50 則、每則 ≤600 字（截斷加「…」）；放不下的從後面少帶，`more` 是沒帶的則數 |

- **中繼資料**（`ConversationWire.Meta`）：`conv.created` 與 `conv.meta` 是同一個形狀、都帶整份——outbox 合併同一個對話待送的 `conv.meta` 時是原地整筆取代（5.1），只帶改了的欄位會在合併時掉資料；所以 null 一律表示「預設」：`model`／`effort` 省略＝帳戶預設、`localAccountId` 省略＝帳戶不明（meta.json 重建）、`permissionMode` 省略＝存檔裡是沒定義的數值。`provider` 是線上的名稱（`Claude`／`Codex`）。`workingDirectory` 只在 `customFolder` 是 true 時帶、是完整路徑（只供顯示，與網頁 Header 的 `workingDirectory` 相同；路徑超過 1,024 字時省略）；對話自己的 workspace 不送路徑（`customFolder: false`）。`title` ≤200 字、`model`／`effort` ≤128 字，太長的由產生端截斷（任何對話都產生得出合格的 payload）。`models`（2026-10-23 起，選用）是這個對話的帳戶在擁有者電腦上可選的模型（CLI 回報的清單，沒回報過是內建的預設選單；不一定有 `default`，讀的一方補在最前面）：`[{value, label, description?, efforts?}]`，最多 32 項（`MaxModelOptions`），`value`、`label` 是 1～128 字、`description` ≤256 字（`MaxModelDescriptionLength`）、`efforts` 最多 16 個 1～128 字（`MaxModelEfforts`），每項裡不認得的欄位忽略、整份不合格回 `invalid`。產生端（`ConversationWire.ModelOptions`）略過空的、超過 128 字、遮蔽後會變的 `value`（截斷或遮蔽過就不是能送回 `setOption` 的值）與重複的 `value`，`label`、`description` 遮蔽後截斷（`label` 空的用 `value`），整份超過 128 KB 時從後面少帶，所以照樣任何對話都產生得出合格的 payload；清單在這台改變時（`AgentHub.UpdateModels`）這個帳戶的對話重發 meta（內容沒變的不送），每次啟動的重整也會比對摘要補發。其他電腦（桌面的副本）與瀏覽器版改這個對話的模型時照它列選單；舊版擁有者不送（舊版伺服器與桌面照「未知的欄位忽略」收）。`title`、`model`、`effort`、`workingDirectory` 與 `conv.queue` 的 `displayText` 也在擁有者端經權杖 DLP 遮蔽（`metadata`、`paused` 的對話照樣發 `meta` 類事件，標題常是第一則訊息的前 40 字）；截斷過、結尾是「…」的標題與佇列文字先去掉「…」前面連續的權杖字元再遮蔽（`CredentialLeakRules.MaskCut`、`ConversationWire.ClampMasked`：半截權杖可能不到樣式的長度下限）。時間是 Unix 毫秒、只供顯示。
- **同步政策**（`syncPolicy`，小寫，`ConversationSyncPolicies`）：`full`（預設，沒設過也是）全部發布；`metadata` 只發 `meta` 類；`localOnly` 什麼都不發（連 `conv.created` 都沒有）；`paused`（權杖 DLP 命中，設計 8.8）同 `metadata`，等擁有者在本機選擇。存檔的值經 `Interpret` 解讀：沒設過（null）是 `full`、不認得的值（含大小寫不同）一律當成最嚴格的 `localOnly`；meta.json 損毀重建的對話是 `localOnly`（原本的政策不明）。**儲存**：政策存在 meta.json 的 `SyncPolicy`；不是 null 也不是 `full` 時另寫對話資料夾裡的旁檔 `sync-policy.json`（`{"policy":"localOnly"}`），改回 `full` 時刪掉——WP4.1 以前的版本把 meta.json 讀進來再存回時會刪掉它不認得的 `SyncPolicy`（null 又解讀成 `full`），旁檔它不讀也不寫。載入時取 meta.json 與旁檔兩者中較嚴格的（`ConversationSyncPolicies.Strictness`：`localOnly` > `paused` > `metadata` > `full`；旁檔存在但讀不到或損毀當成 `localOnly`），meta.json 重建時用旁檔的值；旁檔由 `ConversationStore.SaveConversation` 維護（先寫旁檔再寫 meta.json），所以 WP4.3、WP4.6 改政策一律經它。Antigravity、Grok 的對話一律 `localOnly`（`SyncProviderRules.TryWireName` 回 false 的種類，`SyncProviderRules.ConversationPolicy`，D39），也不能從遠端建立。payload 的 `syncPolicy` 只會是 `full`／`metadata`／`paused`（`localOnly` 不發布）。已發布過的對話改政策時（答 4，下面「伺服器」的政策轉換表）：降成 `metadata`／`paused` 等於在伺服器上**清除內容**（物化的項目與輸入區刪掉）加上 Pull 過濾，網頁與其他電腦立刻只剩標題與狀態，改回 `full` 時擁有者整份重送；改成 `localOnly`（含換到 Antigravity）等於**在伺服器上刪除**（擁有者送 `conv.deleted`），之後不能改回同步。
- **項目的線上投影**（`ConversationWire.TryItem`，同步專用，與網頁的 `ItemRenderer` 分開，D32）：欄位是 `id, ord, ver, kind, status, createdAt?, updatedAt?, text?, toolName?, toolKind?, title?, detail?, output?, diff?, filePath?, attachments?, questions?, answers?, decision?, canAllowForSession, allowSessionLabel?, answerAsMessage, media?, plan?, noticeKind?, requestDigest?, risk?`；空字串與空清單省略。`kind`、`status`、`noticeKind`、`risk` 是列舉名稱（同網頁 Item 的 `kind`／`status`；`risk` 是 `Normal`／`High`，瀏覽器 Item 的小寫 `risk` 由伺服器的渲染轉）。**不送** `ProcessInstance`、`RequestKey`、`Seq`、`Source`（使用者訊息從 claude.ai 或 Claude App 送來的標示，只在本機：送上線要改契約，WP4.3 計畫 D34）、附件的 `StoredPath`、`ModelImagePaths`（只送張數 `frames`）與 `FrameSeconds`、`MediaPaths`（只送檔名與種類）、`MediaRefs` 的本機欄位。附件是 `{id, name, kind, size, frames?, duration?, ref?}`、代理產出的媒體是 `{ref?, kind: "image"｜"video", name}`，`ref` 是裝置發的媒體參照（14.3；還沒發的沒有）。問卷的答案在線上是陣列 `[{id, values: []}]`（不是以題目 id 為鍵的物件：Claude 的題目 id 是題目文字，每個字串都要在值的位置才能整欄換成 blob），秘密題目（`isSecret`）的答案一律是「（已隱藏）」。`ord` 是擁有者在這個對話內配發、嚴格遞增的順序（`OrdRules`）：配過就不再用（刪掉的項目的 `ord` 也不重用，即時更新以 (對話, ord) 合併），下一個記在 meta.json 的 `NextOrd`，實際取它與現有項目最大 `ord`＋1 的較大者（meta.json 與 items.json 不是同一次寫入），所以重開程式後也遞增；第一次同步載入時舊項目依檔案順序補配 1..N（`OrdRules.Backfill`）。同步只認 `ord`，網頁的 `seq` 不變（同步不變式）；`ver` 是擁有者對這筆的更新次數（≥1，14.3）；還沒配 `ord`、id 不合格、種類或狀態是沒定義數值的項目不上線。`requestDigest`、`risk` 照項目的欄位送（14.5）。所有字串在投影之前經 DLP 遮蔽（`CredentialLeakRules.MaskItem`，D43；見下面的「權杖 DLP」）。
- **權杖 DLP**（`CredentialLeakRules`，D43；設計 8.8）：擁有者電腦發布之前把項目每個字串欄位（含題目的 id 與標題、答案的鍵、工具名稱、檔案路徑、附件檔名、媒體路徑）裡疑似權杖的片段換成「［疑似權杖已遮蔽］」（`MaskItem`；只換上傳的副本，本機不動；工具的輸入輸出是 JSON 時先遮蔽解碼後的字串，`MaskJsonStrings`）。題目 id 與答案的鍵經同一個函式遮蔽，遠端回答時電腦以同一個 `Mask` 找回原題。遮蔽的文字不含 ASCII，任何樣式都不會再比對到，所以伺服器的第二道（`HasLeak`，命中時再遮蔽後接受〔5.4 的例外〕）看到遮蔽過的副本不會誤判。切開的權杖比對不到，所以另外三條：`text`、`detail`、`output` 開頭被截掉的（中樞只留尾段，「…（前 N 字省略）」）前綴之後到第一個換行整段換掉（`MaskTruncated`；切在私鑰裡的換到結尾標記或最後）；執行中的項目（串流到一半、持久的檢查點）先去掉尾端還沒完的權杖字元（`SafeLength`，同即時更新的 `MaskLive`：持久事件永久留在伺服器）；差異（`diff`）裡私鑰開頭標記到結尾標記之間的每一行（不論 add、del、ctx、hunk）整行換掉。換成 blob 的欄位（下面的「大小」）：WP4.7 的 blob 上傳端點與 WP4.2 讀 blob 時以 UTF-8 解碼後跑 `HasLeak`（命中就顯示佔位：當成讀不到），陣列欄位的 blob 也要檢查禁止的欄位（`FindForbiddenKey`）。樣式與邊界的規則在 `CredentialLeakRules` 的說明與設計 8.8。
- **大小**（`ConversationEventRules.TryFit`，D34）：payload 解壓後 ≤`maxInlinePayload`（128 KB，5.4 的解碼上限），任何內嵌字串 ≤32 KB（UTF-8）。blob 的單位是**項目的頂層欄位**（`text`、`output`、`detail`、`diff`、`questions`、`answers`、`attachments`、`media`、`plan`、`title`、`toolName`、`filePath`、`allowSessionLabel`）：含有超過 32 KB 字串的欄位（字串本身，或含它的陣列）整欄換成 blob 參照 `{blob: sha256, length}`（內容是 UTF-8：字串欄位是字串本身，陣列欄位是那個陣列的 JSON；blob 經 `/api/v1/device/blobs`，WP4.7）；整筆仍超過 128 KB 時由大到小一欄一欄換，直到放得下（一份很長、每行都很短的差異整欄換成一個 blob，不逐行換）。**WP4.7 的實際做法**：只在協商出 `media1`、項目不是 Running 時換 blob（執行中的照 WP4.3 截斷，定稿才走 blob）；每個 blob ≤4 MB、整筆的 blob 合計也 ≤4 MB（`Hub:Media:MaxItemBlobBytes`、桌面 `ItemBlobRules.MaxItemBytes`，不是型別上限 64 MB：渲染器換回一個 blob 約要 6 倍記憶體），超過的欄位先照 WP4.3 的規則截到 4 MB 再換；blob 內容一律取自遮蔽後的 payload；遮蔽後有超過 65,536 個字元的 base64 片段（權杖 DLP 只解碼開頭 64 KB）時不走 blob、照 WP4.3 截斷。blob 都確認在伺服器上（E5 的 `HEAD` 200 或 `PUT` 200／201）才送 blob 版，否則先送截斷版、背景上傳、完成後重新投影最新的快照；E5 回 422／413／411／400 時那個項目本次執行照截斷版。第一次協商出 `media1` 進 Live 時，`published_items.truncated` 的項目由新到舊重新投影（換回 blob 版）。讀的一方以 `ConversationWire.TryReadItem` 換回內容並核對雜湊與長度，換回之後再做一次形狀檢查（`ConversationEventRules.CheckItemShape`：禁止的欄位、識別碼與列舉的格式，同下面的共用驗證但不含大小上限——換回的內容本來就超過），不合格就當成讀不到。其他種類的 payload 由產生端截斷（標題、佇列），不用 blob。
- **共用驗證** `ConversationEventRules.Validate(kind, payload)`：裝置附加進 outbox 之前與伺服器收到時呼叫同一份（D33）；事件外框（種類、`conversationId`、`epoch`、串流 `c:{conversationId}`）另由 `CheckEvent` 檢查。只收一種解讀：重複的鍵、註解、結尾多餘的內容、深度超過 32 一律 `invalid`；整筆或內嵌字串超過上限是 `too_large`。識別碼與列舉照格式（項目、佇列訊息、附件的 id 是 1～64 個英數字、連字號、底線；`kind`、`status`、附件的 `kind` 只收認得的名稱；`noticeKind`、`risk` 收任何 1～32 個英文字母的名稱——新版的值照收，讀的一方當成沒有值）。未知的欄位照舊忽略（新版多送的欄位），只有 `conv.item` 裡任何一層出現 `processInstance`、`requestKey`、`seq`、`storedPath`、`modelImagePaths`、`mediaPaths`、`mediaPath`、`mediaRefs`、`path`（不分大小寫）一律 `invalid`——伺服器原樣保存並轉發 payload（5.4），未知欄位會原樣送到其他電腦，這幾個名稱一出現就是本機路徑或只在記憶體的欄位外洩。不合格時裝置不送（記錄原因，不含 payload 的內容）、伺服器回 `invalid`／`too_large`。
- **伺服器**（WP4.2）：`EventLog` 接收對話事件並綁定作者：事件的作者必須是對話的擁有者裝置（`conversations.owner_device_id`；建立對話的那一筆決定擁有者）。伺服器**不看**連線協商出的功能，一律接收（14.1：舊桌面不會送對話事件，WP4.3 的桌面只在 conv1 連線上寫）。被擋的事件照 5.1 寫進 `rejected_events`、不重送；原因只有 `not_owner`、`fenced`、`deleted` 三個碼加上 `SyncErrorCodes` 的 `invalid`、`too_large`。四個碼的常數在 `Sync/ConversationRejectCodes.cs`（`ConversationRejectCodes`；WP4.2 與 WP4.3 共用、內容逐位元組相同的檔，線上名稱固定、只加不改），其中 **`dlp` 是保留碼，伺服器不回它**：第二道 DLP 命中時再遮蔽後接受（答 5）。對話事件不會收到 `conflict`（那是目錄同步與遠端指令的）。
  - **判斷順序**：每一筆對話種類的事件，先在寫入者之外（呼叫端的執行緒上，不拖住全站的寫入）跑純函式的預先檢查：① 外框（`Placement` 算出的串流等於事件的、`CheckEvent`）② 解碼、大小與 `payloadSha256`（5.4）③ `Validate`；解析結果有標準 JSON 沒有的節點（Newtonsoft 接受的 `new 名稱(…)` 建構式、`undefined`；`SyncJson` 不會寫出）一律 `invalid`、`rejected_events.payload` 不存——走遍檢查不看建構式的名稱，只有英數字的權杖（例如 AWS 的存取金鑰）寫成名稱就漏掉 ④ 第二道 DLP：走遍解析後 payload 的每個字串值與屬性名稱跑 `HasLeak`；命中就**再遮蔽**（`ConversationRemaskRules`：每個字串先把私鑰區塊從開頭標記到結尾標記〔沒有結尾就到最後〕整段換掉；`conv.item` 的 `text`、`detail`、`output` 照擁有者端的 `MaskTruncated`、執行中只留 `SafeLength` 之前、`MaskJsonStrings`，`diff` 跨行處理私鑰區塊再逐行 `Mask`，其他字串與屬性名稱 `Mask`〔遮蔽後撞名的屬性依序加 `#2`、`#3`〕；meta 類的 `title`、`model`、`effort`、`workingDirectory` 與 `conv.queue` 的 `displayText` 經 `Mask`，結尾是「…」的用 `MaskCut`），以 `SyncJson` 重新序列化，再跑一次 `Validate` 與走遍檢查，仍命中或不合格回 `invalid`。然後在寫入者的交易裡：⑤ 照 5.1 判斷重複與缺號（重複的回原本的 seq，不再套用；再遮蔽過的事件存的雜湊仍是裝置送的，所以重送照樣判成重複）⑥ 預先檢查有拒絕就照它 ⑦ 查表授權（同一個交易可能先建立、後寫項目，所以不讀快取）：`not_owner`（帶 `Owner`）→ `fenced`（帶伺服器目前的 `Epoch`）→ `deleted` → item 類而且目前的政策不是 `full` 回 `invalid` ⑧ 寫進 `events`（再遮蔽過的存遮蔽版，5.4 的例外）並在同一個 SAVEPOINT 物化。物化時內容造成的例外（解析、型別、constraint 等）只回滾那一筆、回 `invalid`，同批其他事件照收；busy、IO、磁碟滿等照舊中斷連線。
  - **建立與冪等**：未知的對話收到 `conv.created` 或 `conv.meta` 都當成建立（作者成為擁有者、`epoch` 取事件上的值）；未知對話的 `conv.turn`、`conv.attention`、`conv.deleted` 與 item 類（`conv.item`、`conv.itemRemoved`、`conv.queue`）一律回 `invalid`、不建立列，`conv.deleted` 也不留墓碑。擁有者對已存在的對話（同 epoch）再送 `conv.created` 是**整份 meta 取代**、不拒絕（回補重跑、還原、後備快照都會這樣送），不改建立時間與擁有者。meta 類事件的 `syncPolicy` 經 `Interpret` 是 `localOnly`（含不認得的值）時回 `invalid`：不同步一律走 `conv.deleted`。`conv.itemRemoved` 指向不存在的項目照收（給 seq）、不做任何事。持久的 `conv.item` 依 (對話, 項目 id) 整筆取代、**不比 `ver`**；`conv.itemRemoved` 之後同一個 id 再送 `conv.item` 就回來。
  - **還原與 recovering**：`admin restore` 把還沒刪除的對話標成 `recovering`（網頁的標頭顯示「等待電腦重新上傳」）。只有擁有者的 `conv.created` 被接受、而且不是還原前就在 outbox 的舊事件時才解除：這條連線不在對帳模式（5.1 的 `resyncFrom` 是 null），或這筆的 `deviceSeq` ≥ 這次 Hello 的 `outbox.nextDeviceSeq`（Hello 之後才寫進 outbox）。對帳模式重新送達的舊 `conv.created` 照常套用 meta，但不解除。主路是擁有者整份重新發布（`conv.created` 先送）；14.3 的 `SnapshotRequest{restore}` 只當後備。
  - **拒絕碼**（「伺服器」欄由 WP4.2 寫；「桌面」欄由 WP4.3 在重新為基底時補上，`ConversationRejectRules.Classify`）。`BatchEventResult(DeviceSeq, Seq, Rejected, Epoch, Owner)` 不變：`fenced` 帶伺服器目前的 `Epoch`、`not_owner` 帶 `Owner`，只供記錄（桌面也只記錄，不拿它們判斷）。

    | 代碼 | 伺服器什麼時候回 | 桌面怎麼處理 |
    |---|---|---|
    | （接受） | 通過全部檢查（含再遮蔽後接受） | 確認；`conv.deleted` 以外的任何一筆被接受時記 `created_acked=1`（伺服器只接受「對話在、作者是擁有者」的事件，不在時只有 `conv.created`、`conv.meta` 會建立它；送 created 的那一批在途時斷線、Rewind 標成已提交而沒有結果時也能恢復，WP4.3 第二輪合併前審查 F2）；`conv.deleted` 被接受時清掉 `needs_redo` |
    | `not_owner` | 對話已存在，作者卻不是 `owner_device_id`；重新配對成新身分之後，對舊身分的對話也是這樣 | 封鎖；所有這種對話（例如重新配對前的舊身分）併成**一則**彙總提示 |
    | `fenced` | `epoch` 不等於 `conversations.epoch` | 封鎖 |
    | `deleted` | 對話已有 `deleted_at`（墓碑） | `conv.deleted`：當成功。其他事件：永久封鎖 |
    | `dlp` | **不回**（保留碼） | 防禦性處理（伺服器遮蔽後接受、不回它）：item 類事件把政策改成 `paused` 並加一則 Notice；meta 類事件封鎖 |
    | `invalid` | 以下任一：形狀不合（外框、`Validate`）、解碼或雜湊不符；遮蔽後仍命中；未知對話的非建立事件；目前政策不是 `full` 時的 item 類事件；`syncPolicy` 是 `localOnly` 的 meta 類事件；物化時內容造成的例外；舊伺服器（第 3 階段的舊版，沒有 conv1）上的所有對話事件；`{}` 的非 `conv.deleted` 事件（14.1 的「沒有 `conv1` 的連線」、下面的「桌面的政策降級」） | 依序判斷：1. 沒有 conv1 的連線：記成要重整；`conv.deleted` 也是（墓碑記 `needs_redo`，下一次 conv1 連線補送）。2. conv1 連線、`conv.deleted`：當成功（伺服器不認得的對話）。3. conv1 連線、`created_acked=0`（從沒確認過，或因換實例、還原而重設）：清被拒那筆的摘要、記 `needs_redo`，隔 1、10、60 分鐘補送 `conv.created`，第 4 次封鎖並併進彙總提示。4. conv1 連線、`created_acked=1`：程式錯誤，封鎖到 app 版本或伺服器實例改變為止（被拒的是沒有 conv1 時或政策降級時換成的 `{}` 時例外：記重整）。伺服器換實例或還原之後、重新發布之前，曾經建立過的對話（`publish_meta.ever_created`）被刪除或改成 `localOnly` 照樣送 `conv.deleted`，新的伺服器不認得它時落在第 2 點 |
    | `too_large` | 解壓後超過 5.4 的上限 | conv1 連線：程式錯誤，處理同上（同樣排除換成的 `{}`）。沒有 conv1：記成要重整 |
    | 其他（含 `conflict`、`unknown_conversation`） | 不應該出現 | 程式錯誤 |

  - **政策轉換**（答 4；伺服器只看事件，不需要知道桌面為什麼改）：

    | 轉換 | 擁有者送 | 伺服器 | 網頁與其他電腦 |
    |---|---|---|---|
    | `full` → `metadata`／`paused`（含桌面的 DLP 自動暫停） | 帶新政策的 `conv.meta` | 同一個交易**清除**這個對話物化的項目與輸入區（`items`、`conversation_runtime`），清掉即時疊加層、從 `WatchChanged` 拿掉；之後 Pull 不再回它的 item 類事件 | 立刻只剩標題與狀態（網頁開著的收到 `resync`） |
    | `metadata`／`paused` → `full` | `conv.meta(full)`，接著整份重送 | 照收，之後的 `conv.item` 都整筆取代 | Pull 又回 item 類事件，副本依 seq 收斂 |
    | 任何政策 → `localOnly`（含換到 Antigravity）、在本機刪除 | `conv.deleted {}` | 當成刪除：記 `deleted_at`，同一個交易清除項目、輸入區與未讀，保留墓碑列；目錄與清單不再有它，之後的事件一律 `deleted` | 從清單、網頁與副本移除，**不能改回同步** |

    原始事件（`events`）留著，只有被同一筆項目的新版本取代的 `conv.item`（`seq` 不是那筆項目物化的最新版本 `items.last_seq`；清掉內容的對話沒有 `items` 列，它的 `conv.item` 都算）收到超過 7 天後刪掉（2026-10-25，`Hub:Conversations:SupersededRetentionDays`）：每筆項目最新的那一筆一定留著，Pull 照 seq 拉、副本與重建照 seq 重播都收斂到同一份，只是拉不到中間的舊版本；meta 類、`conv.itemRemoved`、`conv.queue` 不刪。裝置不能依賴拉到某筆項目的每一個版本。被拒的對話事件（`rejected_events` 裡 `conversation_id` 不是 null 的）30 天後刪掉。
  - **Pull 過濾**：`Pull` 只看對話**目前**的狀態：已刪除、或目前政策不是 `full` 的對話，不回 item 類事件（`conv.item`、`conv.itemRemoved`、`conv.queue`）；meta 類照回（副本才知道它被刪或降級）。過濾在 SQL 裡，不影響第 3 階段的類別；被過濾的事件不回，`high` 照舊是伺服器目前最大的 seq。改回 `full` 之後新舊 item 類事件都拉得到：副本依 seq 套用，擁有者重送的新事件（含 `conv.itemRemoved`）取代舊的，所以已經在拉的副本與從頭拉的副本最後收斂到同一份內容。
  - **移除電腦時伺服器刪的對話**（2026-10-23，設計 14.22）：擁有者移除已撤銷的電腦時，伺服器刪掉它擁有、還沒刪除的對話——伺服器不寫對話事件，刪除只記在物化表（`deleted_at`、`deleted_by` 是 `server`；擁有者的 `conv.deleted` 記的是裝置 id），內容、輸入區與未讀照「在伺服器上刪除」清掉。這種對話 Pull **連 meta 類也不回**（沒有 `conv.deleted` 可以讓副本知道它被刪，回了只會讓從頭拉的電腦建出不存在的副本；各台的副本由 7.1 的移除規則丟掉）；重建不從事件把它推回來（保留墓碑）；媒體參照與項目的 blob 引用由媒體索引的對帳刪掉。
  - **Pull 不檢查 conv1**（風險，接受）：伺服器不以連線協商出的功能限制拉取，沒宣告 conv1 的裝置也拉得到對話類別。所有裝置都屬於同一個擁有者，沒有越權的問題；合法的用戶端（第 3 階段、WP4.1 的桌面）本來就不拉 `meta` 與 `c:{id}`。伺服器回滾到 第 3 階段的舊版 時舊版的 Pull 沒有上面的過濾，但沒有 conv1 的 WP4.3 桌面也不拉對話類別。
- **拉取**：所有裝置拉 `cls=meta`（全域的對話清單）；開著的副本另拉 `stream=c:{id}`（5.3 的游標已支援依串流）。擁有者不拉自己的對話（只有它寫這個串流，拉回來是重複下載；WP4.3 D30）。Hello 的 `cursors` 加 `meta` 鍵（`conv1` 啟用時；桌面在上一次進 Live 的連線協商出 `conv1`〔kv `conv_armed`〕而且這次宣告 `conv1` 時才帶）。桌面每次拉取帶 `meta` 一個與開著的副本串流最多 12 個（輪流），加上第 3 階段的游標不超過伺服器的 16 個；對話的游標存在桌面 sync.db 的 `conversation_cursors`，第 3 階段的 `cursors` 只放 catalog、usage（WP4.3 步 14）。拉到的對話事件交給對話的處理（WP4.3），不再進第 3 階段「不認得的種類」的警告（WP4.1 起裝置的化簡把 `conv.*` 另放一份、原樣交出，`PullDelta.Conversations`；要在 UI 執行緒做的與目錄同一次套用，失敗時那一頁的游標不前進）。
- **裝置的 outbox**（WP4.1 步 11）：對話事件照第 5 節先寫進 outbox，`conversationId`、`epoch` 存在列上、送出時原樣帶著；外框（對話種類、`conversationId`、`epoch`、串流）照 `CheckEvent`。合併鍵寫成 `{用途}:{conversationId}` 或 `{用途}:{conversationId}:{…}`（例如 `meta:{id}`、`item:{id}:{itemId}`）：同一個對話待送的同一個鍵原地取代（`epoch` 跟著新內容），不同對話的事件不會互相取代；不符是寫入端的程式錯誤，整批不寫（`OutboxAppendRules`）。
- **桌面 outbox 的鍵與順序**（WP4.3 D5、D6）：`conv.meta` 的鍵是 `meta:{id}:{gen}`（`gen`＝這次啟動的 `BootId`＋標記序號，每個一輪開始或結束之後換新世代：`running:false` 不會合併到 `turn.started` 之前）；`conv.item` 與 `conv.itemRemoved` 共用 `item:{id}:{itemId}`（最新的狀態勝）；`conv.queue` 是 `queue:{id}`；`conv.created`、`conv.turn`、`conv.attention`、`conv.deleted` 沒有鍵（`created` 帶鍵會被之後的 meta 換掉）。每個對話 `conv.created` 一律最先（伺服器還不認得的對話先補一筆）；收到標記（一輪開始、結束、提醒、刪除）時，先放這個對話待送的項目再放標記；一輪開始與結束之後接一筆 `conv.meta`；`conv.deleted` 最後，之後的一律丟掉（墓碑）。
- **桌面的發布時機**（WP4.3 D21，設計 4.4）：項目離開 Running、Waiting 卡片建立、一輪結束時立刻持久；仍在 Running 的每 10 秒一筆檢查點。被觀看的對話、要立刻持久的變化、移除、存檔的中繼資料、佇列在 50 ms 內寫進 outbox，沒被觀看而且只有執行中項目的串流 1 秒。每筆項目依序經秘密回答的清除、截斷（WP4.7 的 blob 之前，超過上限的欄位截斷而不是換成 blob：每欄 ≤30 KB、整筆 ≤120 KB，還放不下就換成「這個項目太大，未同步」的佔位，記 `published_items.truncated`）、`MaskItem`、`TryItem`、`Validate`，才寫進 outbox；摘要與 outbox 的列在同一個交易寫，內容沒變的不重送。
- **桌面的政策降級**（WP4.3 D25；答 4）：已發布的對話改成 `metadata`、或被權杖 DLP 自動改成 `paused`：送帶新政策的 `conv.meta`；改回 `full`：送 `conv.meta(full)`，接著整份重整，已經沒有的項目送 `conv.itemRemoved`，佇列不是空的另送一筆 `conv.queue`（伺服器降級時清掉了佇列，桌面把佇列摘要記成空佇列的）。降級成 `metadata`／`paused` 時，outbox 裡這個對話還沒送出（Pending）的 item 類列（`conv.item`、`conv.itemRemoved`、`conv.queue`）在同一個交易裡原地換成 `{}`（同沒有 `conv1` 的連線），待送的 `conv.attention` 拿掉 `speech`（`full` 時組的句子可能帶對話內容；伺服器改組只有標題的句子）；改成 `localOnly` 時**不論種類**（連帶著標題的 `conv.created`、`conv.meta`，以及 `conv.turn`、`conv.attention`）都換成 `{}`，而且在政策轉換的那一輪就換，不等旁檔寫好（寫旁檔的期間、寫不成功時都可能先連上、先送出；WP4.3 第二輪合併前審查 PRIV-1、PRIV-2）。待送列的合併鍵清掉，降級的 `conv.meta`（新的世代）或 `conv.deleted` 一定排在它們之後；伺服器以 `invalid` 擋下這些 `{}`，桌面記重整（WP4.3 合併前審查 P1、F1）。寫降級事件的交易失敗時，清除單獨補寫一次；還是寫不成就記著，conv1 連線每次送出之前先補寫，補不成就照本機失敗斷線、這一批不送（PRIV-4）。伺服器換實例或還原之後、重新發布之前（`created_written=0`、`ever_created=1`）改成 `metadata`／`paused` 也照降級清除（outbox 可能還有換實例之前寫的待送內容；PRIV-3）。**送出的閘門**（F1）：conv1 連線送出之前，這次啟動還沒看過中繼資料的對話（上一次執行留下的待送列：改政策的變化可能還在發布者的計時器裡就結束了），在它第一筆待送、不是 `{}` 的列之前截斷這一批，先向中樞要它現在的中繼資料、照上面的政策轉換處理完才送；中樞沒有這個對話時政策不明，它的待送列不論種類換成 `{}`、摘要清掉（待送的 `conv.itemRemoved` 記成沒送過）、記重整之後放行，對話回到中樞時重整補送（WP4.3 第三輪合併前審查 PRIV-R3-1、R3-2）。封鎖中的對話（`localOnly` 的墓碑以外）降級時，封鎖之前待送的列照上面的規則換成 `{}`，閘門也先要它的中繼資料（PRIV-R3-3）。補送 `conv.created`（上線時補確認、`invalid` 之後、`restore`）之前，還沒處理的政策轉換先照轉換走，不以現在的政策直接補（LIVE-1）。已知的對話改成 `localOnly`（含換到 Antigravity）：**先**在對話資料夾寫旁檔 `sync-server-deleted.json`（`{"at": Unix 毫秒}`），**再**送 `conv.deleted {}`，然後封鎖；之後不能再改回同步。有這個旁檔的對話有效政策一律是 `localOnly`（旁檔損毀或讀不到也是，換回 Claude 帳戶也一樣），`syncSetConversationPolicy` 選 `full`、`metadata` 被拒並跳 toast；舊版程式不讀它。寫旁檔失敗就不送 `conv.deleted`、記重整（待送列已經換成 `{}`）。**佇列的重送**（F3）：整份重新發布（重整佇列的每一種原因）都向中樞要一次佇列快照，摘要相同的不送；伺服器上確定沒有佇列時（第一次建立、改回 `full`）摘要記成空佇列的，空的不送，摘要不明時（換實例或還原、被擋下、開啟時清掉）空的也送一次，蓋掉伺服器上可能留著的舊佇列。
- **被拒時桌面怎麼處理**（WP4.3 D14，`ConversationRejectRules.Classify`）：見上面「伺服器」的拒絕碼表的「桌面」欄（重新為基底時兩張合成一張）。桌面只記錄 `BatchEventResult` 的 `Epoch`、`Owner`，不拿它們判斷。
- **回補**（設計 14.2 規則 1，WP4.3 D24；擁有者 2026-10-19 的答 2、答 3）：第一次連上支援 `conv1` 的伺服器時自動回補全部舊對話：先為所有本機擁有、會發布的對話送 `conv.created`（標題與狀態；清單先完整），再依最後更新由新到舊送內容（`full` 才有 `conv.item`），**不設天數門檻、不限速**，本機有對話執行中也照常；只在 outbox 沒有一般事件待送時才排下一輪（一般事件優先，一輪 ≤500 列，讀檔的一段 ≤2 MB）。跳過封鎖的對話、墓碑與有 `sync-server-deleted.json` 的對話。保留中的對話列 payload 合計超過 4 GiB、sync.db 所在磁碟剩下不到 2 GiB，或保留列數超過 `RetainedMaxRows`（1,000,000）時暫停等修剪（磁碟保護，不是節流）。每個對話全部確認後記在桌面 sync.db 的 `backfill_state`；進度給網頁的 `sync.backfill`（WP4.6 起顯示）。伺服器換實例或還原之後同樣整份重新發布（`conv.created` 先送，墓碑補送 `conv.deleted`；設計 10.2 第 4 步）。
- **桌面的心跳**（WP4.3 D20）：只在協商出 `conv1` 的連線上，`Heartbeat` 的 `running`、`waiting` 帶本機已知、沒有封鎖、會發 meta 的對話，各最多 1,000 筆（多的留最近更新的）；伺服器不讀它們（無害）。
- **副本**（WP4.3 D26）：拉到的對話事件照 `PayloadRules` 解碼、`CheckEvent`／`Validate` 驗證，不合格的略過、游標照樣前進；自己擁有的對話、自己寫的事件一律略過；本機已有的對話 id（重新配對前的舊身分）不建副本。副本存在桌面 sync.db 的 `replica_meta`、`replica_items`（不是契約 D40 的檔案），item 類事件依 seq 以（對話, 項目 id）套用；meta 的政策不是 `full` 時丟掉 `replica_items`、保留 `replica_meta`，`conv.deleted` 兩者都丟。解除配對刪掉 sync.db 時一起刪。

### 14.3 即時更新、觀看、快照與媒體（`live1`、`media1`；WP4.2、WP4.3、WP4.7）

即時更新只是提示，持久事件才是準（D46）：可以合併、可以丟，持久的 `conv.item` 到了就以它為準。

| 方向 | 方法 | 送法 | 內容 |
|---|---|---|---|
| 裝置→伺服器 | `LiveRelay` | Send | `{conversationId, epoch, ord, ver, item}`：串流中的項目（`item` 是 `conv.item` payload 的 JSON 字串——同持久事件的 payload 以字串傳，伺服器驗證只有一種解讀、原樣轉發；裡面的 `ord`／`ver` 要等於外層的，`LiveRules.CheckRelay`；同 `conv.item` 的投影、已遮蔽；串流中的文字欄位尾端連續的權杖字元先不送，`CredentialLeakRules.MaskLive`：有長度下限的權杖還沒傳完時比對不到）；≤64 KB（超過就不送這一則，等持久事件；`item` 裡的單一字串也以 64 KB 為限，不套持久 `conv.item` 的 32 KB——`CheckRelay` 以 `MaxLiveBytes` 呼叫 `Validate`，否則超過約 1.09 萬個中文字的回覆即時串流就停住）；每個對話 ≤`maxLiveHz` 次／秒 |
| 伺服器→裝置 | `WatchChanged` | Send | `{conversationIds: []}`：有人在看的對話（整份取代）；擁有者只對這些對話送 `LiveRelay`（設計 4.5） |
| 裝置→伺服器 | `LiveDelta` | Send | （`live2`）`{conversationId, epoch, ord, ver, baseVer, head, ops}`：串流中的項目相對於這條連線上一則的增量；格式與重組見下方「增量」 |
| 伺服器→裝置 | `LiveResync` | Send | （`live2`）`{conversationId, ord, reason: "base"｜"remask"｜"evicted"}`：這一筆改送整則；見下方「增量」 |
| 伺服器→裝置 | `SnapshotRequest` | Send | （`conv1`）`{conversationId, reason: "restore"｜"open"}`（`SnapshotReasons`）：伺服器還原後的補齊、回補還沒輪到就有人打開的對話（插隊到下一個） |
| 伺服器→裝置 | `MediaRequest` | Send | `{relayId, ref, purpose: "media"｜"thumb"｜"file", range?: {start, end?}}`（`MediaPurposes`；`range` 是位元組範圍、含頭含尾，同 HTTP 的 `Range: bytes=start-end`，`end` 省略＝到結尾，`range` 省略＝整份）：向發這個參照的裝置要位元組；裝置以 `PUT /api/v1/device/relay/{relayId}`（`cha_`）回（設計 4.10） |

- `ver`：擁有者對同一筆項目的每次更新遞增（從 1 起，持久的 `conv.item` 帶同一個計數）。規則在 `LiveRules`（擁有者的送出佇列、伺服器轉給觀看者、瀏覽器的疊加層共用）：同一筆以 (conversationId, ord) 認，新舊比 (epoch, ver)——世代大的勝（第 5 階段換擁有者後 ver 從頭算），同世代比 ver；`Merge` 同一筆只留最新的一則（版本相同時留後到的，位置是那一筆第一次出現的位置）；持久的 `conv.item` 版本不比某則即時更新舊時（`IsSuperseded`：世代較新，或同世代 `ver` ≥）那則就作廢。**`ver` 不持久保存**（只在擁有者的記憶體，程式重開後從 1 重來）：只拿來比即時更新之間、以及即時更新與同一次啟動之後的持久版本；持久的 `conv.item` 之間不比 `ver`，一律依伺服器 `seq` 整筆取代（伺服器的物化與瀏覽器都照做），所以重開之後的持久事件照樣取代舊的。**擁有者換了一次啟動**（WP4.3 D7）：`ver` 是擁有者這一次啟動內的計數（桌面的引擎每次啟動一個全域計數，從 1 起，持久與即時共用，不跨開機；WP4.2 的提案 P8「跨開機遞增」不採用）；觀看端（伺服器的疊加層、之後的副本）只在同一條連線內比 `ver`，跨連線以連線的 Order 決定（疊加層怎麼清由 WP4.2 寫）；持久的 `conv.item` 之間不比 `ver`；伺服器的再遮蔽（14.2）不改 `ver`，也不改 `ord`。大小以 UTF-8 算（`Fits`，≤64 KB），頻率以單調時鐘算（`Allows`：距離這個對話上一則至少 1000／`maxLiveHz` 毫秒、無條件進位，`maxLiveHz` ≤0 不送）。
- **伺服器的收件與疊加層**（WP4.2 D27、提案 C4、D44；`MyriagentHub.Realtime.LiveRelayIntake`、`LiveOverlay`、`LiveOverlayRules`）：
  - 收件在裝置 hub 的呼叫執行緒上、不等寫入者，任何一步不通過就丟掉、只計數（`ops/status` 的 `liveDropped`），**不回錯誤給裝置**。順序：連線協商出 `live1` → 外框（對話 id 的線上格式、`epoch`／`ord`／`ver` ≥1、`item` 不是 null 且 `Fits`）→ 每台的權杖桶（每秒 50 則、突發 50）→ 目錄（擁有者是這台、世代相符、沒刪除、政策 `full`）→ 每個對話的權杖桶（每秒 `maxLiveHz` 則、突發 4；鍵在目錄確認之後才建）→ 裝置沒被撤銷 → 完整的 `CheckRelay`（超速的不做；有建構式等標準 JSON 沒有的節點也丟掉，計入 `stale`）→ 第二道 DLP：命中就照 5.4 的例外再遮蔽，遮蔽版再檢查一次，仍命中或不合格就丟（遮蔽後轉送的計入 `remasked.live`；`ord`、`ver` 不變）。伺服器用權杖桶而不是最小間隔：網路抖動時最後一則不會被丟、畫面不會停在舊內容；桌面送出端照舊用 `Allows`，兩邊相容。
  - 疊加層以 (conversationId, ord) 為鍵，記下送出它的**裝置連線的 Order**（越大越新）；跨連線以 Order 決定新舊、不比 `ver`，同一條連線內才以 `IsNewer` 比。一則即時更新依序判斷：① 持久版本已經 final（status 不是 `Running` 也不是 `Waiting`）→ 丟；② 已有的疊加來自 Order 較大的連線 → 丟，同一條連線而且 `ver` 沒有比較新 → 丟；③ 持久版本是「提交時這條連線是那台裝置最新的已 Prove 連線」，而且這則不比它新（`IsSuperseded`）→ 丟；其他情況收下（取代 Order 較小的疊加）。從 open 的快照得到的持久版本不知道是哪條連線提交的，不比 `ver`。
  - 清除（提案 C4）：(a) 持久的 `conv.item` 提交時 status 不是 `Running`（包括 `Waiting`）就清掉那一格、不比 `ver`；之後的即時更新在持久版本是 `Waiting` 時照收、final 時照 ① 丟；(b) 持久版本仍是 `Running` 的檢查點照 ③ 比；(c) 擁有者的連線斷了（`ConnectionRemoved`）清掉那條連線產生的疊加，同一台有 Order 較大的連線 Prove 時清掉較小的。另外 `conv.turn` 的 `completed` 清掉那個對話的疊加（這一輪的項目都已經有持久版本），降級、刪除、項目移除與變更通知溢出（整份重讀）時也清。被清掉的格子，開著的連線改收持久版本。
  - 只轉給**開著**那個對話的瀏覽器連線（`item`，與持久的增量共用同一個鍵；open 的快照之後補上疊加）；上限每個對話 64 個 ord、全部 8 MiB，滿了先淘汰每個對話最舊的、再淘汰全部最舊的（`ops/status` 的 `overlayBytes`）。擁有者重開後新連線的 `ver` 從 1 起，因為 Order 較大照樣收；`ver` 怎麼送由 WP4.3 寫在上一條。
- **增量（`live2`，2026-10-23；設計 `docs/plans/2026-10-23-live-delta-and-replica-live.md` 第 3 節）**：串流中的項目每則都送整筆時流量隨長度平方成長；協商出 `live1` 與 `live2` 的連線改送增量。
  - 裝置→伺服器 `LiveDelta`（Send）：`{conversationId, epoch, ord, ver, baseVer, head, ops:[{f, at, s, n}]}`。`head` 是 `conv.item` 投影去掉 `text`、`detail`、`output` 的 JSON 字串（不得含這三個鍵，`id`／`ord`／`ver` 要等於外層的）；`ops` 每個有變的長欄位一項，`f` 只能是那三個、不重複，新值＝基底那一欄的前 `at` 個 UTF-16 碼元＋`s`（`at` 不切在代理對中間），`n` 是新值的長度；沒列的欄位同基底。規則在 Protocol 的 `LiveDeltaRules`：`Encode`（遮蔽之後的兩版之間算共同前綴）、`Apply`（以 `WireItem` 的欄位順序組回、`ConversationWire.Serialize`，與擁有者直接送整則逐位元組相同）、`ShouldSendFull`（`head`＋`s` 超過整則的一半）。
  - 伺服器→裝置 `LiveResync`（Send）：`{conversationId, ord, reason: "base"｜"remask"｜"evicted"}`（`LiveResyncReasons`）。`base`：沒有這一筆的基底或對不上（`baseVer` 不是記著的那一版、重組失敗、`CheckRelay` 不過）；`remask`：重組後的整則命中第二道 DLP（伺服器照樣遮蔽後轉送、不留基底）；`evicted`：基底超過上限被擠掉。每個（連線, 對話, ord）每秒最多一則。
  - 伺服器（`LiveRelayIntake.ReceiveDelta`、`LiveDeltaBases`）：外框 → 每台的權杖桶 → 以基底重組 → 之後照整則的步驟（目錄、每個對話的權杖桶〔`live2` 是 `maxLiveDeltaHz` 則／秒〕、撤銷、`CheckRelay`〔256 KB〕、第二道 DLP、疊加層）。基底以（連線, 對話, ord）為鍵，記這條連線最後送來、重組成功的那一版（整則也算；DLP 命中的不算），和疊加層分開：每對話權杖桶超速、持久版本作廢疊加時照樣前進。每條連線最多 32 筆、全部 16 MiB，60 秒沒用到或連線移除時清掉。協商出 `live2` 的連線，整則 `LiveRelay` 也照 256 KB 收。
  - 桌面（`SyncEngine.ConversationLive.cs`）：每一筆記最後送出的那一版（遮蔽之後的長欄位；屬於目前這條連線，斷線清空），有基底而且 `ShouldSendFull` 不成立時送 `LiveDelta`，否則送整則；不送出（沒人看、政策不允許）就忘掉基底。`LiveResync` 的 `base`／`evicted` 忘掉基底並照頻率補送一則整則，`remask` 讓這一筆到離開 Running 為止只送整則。頻率依整則大小降（`LiveRules.DeltaIntervalMs`：≤64 KB 是 1000／`maxLiveDeltaHz`、≤128 KB 是 250 ms、更大的 500 ms），單則上限 256 KB（`LiveRules.MaxDeltaItemBytes`）。
  - 沒有協商出 `live2`（舊伺服器、舊桌面、回滾）：完全照上面的 `live1`——整則、≤64 KB、`maxLiveHz`。基底只在記憶體，兩邊都不持久化。
- **伺服器的 `WatchChanged`**（WP4.2 D25、P4；`WatchRules`、`ConversationWatch`）：只列**看得到**的對話——瀏覽器連線最後一次 ClientState 的 `visible` 為真，而且對話在這條連線最後一次 `visible` 指令的清單裡、或在以 `IsWireId` 過濾過的 `openConversationIds` 裡（只是 `open` 不算，網頁協定 6 的第 8 點），再只留那台裝置擁有、政策 `full`、沒刪除的，依 id 排序、最多 500 個，每次整份取代。ClientState、`visible` 指令、瀏覽器斷線與任何對話變更之後 50 ms 合併重算，內容變了才送；只送給協商出 `live1` 的已 Prove 連線，Prove 時一律再送一次目前的清單（可能是空的，清掉上一條連線留下的）。送失敗只記錄、不重試。降級、刪除的對話因此一律不列；擁有者據它決定送哪些對話的 `LiveRelay`（伺服器這邊不看清單，收到的照樣經疊加層）與 `IsSeenRemotely`（WP4.3）。
- **伺服器的 `SnapshotRequest`**（WP4.2 D26；`SnapshotRequester`、`SnapshotRequestRules`）：**open**：瀏覽器 `open` 一個 `full`、有 meta、但伺服器上還沒有任何項目的對話，擁有者在線、最新的已 Prove 連線協商出 `conv1` 時送給那條連線；`loadOlder` 不算；每個對話 10 分鐘內只送一次（記憶體；沒送出去的不佔冷卻）。**restore 只當後備**：擁有者 Prove 之後過了 300 秒仍是 recovering 的對話才送（桌面自己的還原重送通常已經補齊），每台每秒最多 2 則（最小間隔），同一個 (裝置, 還原世代, 對話) 在這個服務程序裡只送一次。送之前一律問 `Allows`。
- **桌面送 `LiveRelay` 的條件**（WP4.3 D21）：只給 Running 的項目，而且同時成立：對話在伺服器最後一次 `WatchChanged` 的清單裡、連線協商出 `conv1` 與 `live1`、本機的政策是 `full`（有伺服器刪除旁檔的一律不是）、沒有封鎖、不是草稿或副本、已知（`conv.created` 寫進過 outbox），`Allows`、`Fits`（≤64 KB，太大的只計數、不算進頻率）與 `CheckRelay` 都過。被觀看的對話 50 ms 內送最新的一則；同一個變化同時要即時與持久時先投影即時（`ver` 小）再投影持久（`ver` 大），持久版本蓋過疊加。斷線時清空觀看清單、丟掉還沒送的即時更新（持久的檢查點照舊）。
- **桌面怎麼用 `WatchChanged`**（WP4.3 D19）：存伺服器最後一次的清單（只留 `IsWireId` 合格的、最多 500 個），**用的時候一律以本機政策再過濾**（被入侵的伺服器列出 `localOnly` 的 id 也拿不到內容）。中樞的 `IsSeenRemotely`（設計 4.11）只對「看得到」、政策 `full`、`created` 已確認、沒有封鎖、不是草稿或副本的對話成立：這時本機不另跳提醒；其他一律照常提醒（伺服器壓不下本機的提醒）。網頁的未讀由伺服器另外以 `visible`＋`focused` 判斷，桌面不送未讀。
- **桌面怎麼處理 `SnapshotRequest`**（WP4.3 D23）：先照上面即時更新的政策條件檢查。`open`：把那個對話移到重整佇列最前面、不清摘要（只補沒送過或改過的），同一個對話 10 分鐘最多一次。`restore`（後備；主路是換實例或還原之後的整份重新發布）：這個伺服器世代（`InstanceId`＋`RestoreGeneration`）還沒重新發布過的對話移到最前面，已經重新發布過的只補一筆新的 `conv.created`（新的 deviceSeq，會讓伺服器解除 recovering）；每個（對話, 世代）最多處理一次。
- **媒體參照**（D14）：由裝置發給，26 個 Crockford Base32 字的亂數（`ref`；項目的 `MediaRefs` 記著它對到哪個附件或媒體，只在本機）。線上放在項目投影的 `attachments[].ref` 與 `media[].ref`（14.2），種類（附件的 `Image`／`Video`／`Document`、媒體的 `image`／`video`）、檔名與大小跟著附件或媒體；路徑只在裝置本機的 `media_refs_local`。伺服器只存 `(device_id, ref)`，不存路徑；被要的時候裝置照設計 8.7 的 RemoteFileGuard 與 `RemoteFolderRules` 再檢查一次。
- 裝置對伺服器取 blob 與媒體的 HTTP 端點只收存取權杖（`cha_`，伺服器的 `DeviceAccess` 政策，D56）；`chd_` 只能用在裝置 hub。

#### 媒體與檔案（`media1`；WP4.7，2026-10-21）

**協商與權杖**
- 伺服器在 `AddHubMedia` 無條件宣告 `media1`（沒有開關）；桌面只在也宣告了 `conv1`、v3 的對話表在時宣告（`ContributeMediaFeatures`）。只有協商出 `media1` 的連線才有下面的一切：伺服器不對其他連線送 `MediaRequest`（媒體請求當成來源離線），桌面不保存那條連線 Prove 回的 `cha_`、不呼叫任何 `/api/v1/device/*`、項目照 WP4.3 截斷（14.2 的「大小」）；附件與媒體的參照照樣配發、寫進 `media_refs_local`（伺服器之後升級就能從 `events` 索引）。
- `cha_`：Prove 回的 `accessToken`（15 分鐘）。桌面記下收到時的單調時鐘時間（不比 `expiresAt` 與本機牆鐘），滿 13 分鐘以裝置 hub 的 `RefreshAccessToken` 換新（失敗 1 分鐘後再試）；HTTP 收到 401 時權杖還是送出時那一個就換一次、再送一次，第二次的結果照回。TLS 只照 `DevTrustRules.AcceptCertificate`（同 SignalR 的連線，不比 SPKI：憑證續期不影響）。不自動轉址、不帶 cookie；裝置的 PUT 不帶 `Origin`、豁免 CSRF 標頭（不用 cookie）。

**媒體鍵與參照的用途**
- 媒體鍵 `{deviceId}-{ref}`（`MediaKeyRules`：`d_`＋26 字、`-`、26 字，共 55 字，大小寫有別）；網址 `/m/{key}`、`/t/{key}?s=160|320[&v=]`、`/f/{key}` 都是本站相對網址。伺服器 `media_refs` 的主鍵是 `(device_id, ref)`，鍵帶裝置才能以主鍵查。
- 伺服器 `media_refs.purpose`（`MediaRefPurposes`，結構不改）：`item:{itemId}`（項目的附件與代理媒體；`MediaRefIndexer` 從 `events` 依 seq 增量維護，對話刪除、降級、`conv.itemRemoved` 時立即刪列與縮圖引用）、`list`（抽屜：`listFiles` 的輸出改寫時登記，從建立起 30 天）、`file`（`fileFetch`：15 分鐘）。不認得的值一律不收。`thumb_blob` 是 E8 綁上的縮圖 blob；`last_used_at` 不更新。
- 每個媒體請求（E1～E3、E9）：查 `media_refs`（查不到而索引器還沒追上最新 seq 時等它 ≤2 秒再查一次）→ 用途與端點相符（`/m/`、`/t/`、E9 只收 `item:*`、`list`；`/f/` 只收 `file`）→ 沒到期 → 對話還在、擁有者是鍵裡的裝置、政策 `full`；任何一項不符一律同一種 404。
- 桌面 `media_refs_local`（結構不改）：項目的列附件只存 `attachment_id`、代理媒體只存 `media_path`（＝項目 `MediaPaths` 的那一項）；抽屜與 `fileFetch` 的列 `item_id` 是 `@list`、`@file`（項目 id 不會有 `@`），`media_path` 存完整路徑。每日修剪 `@list` 超過 30 天、`@file` 超過 15 分鐘的列，對話刪除時一併刪。參照的正本是這張表（不寫 items.json，`AgentMediaRef` 的說明已改，設計 14.16 的 D4）。

**端點**（`/api/v1/device/*` 只收 `cha_`；錯誤一律 `{"error": 代碼}`，代碼在伺服器的 `MediaApiErrors`）

| 編號 | 方法與路徑 | 本文上限 | 成功 | 失敗 |
|---|---|---|---|---|
| E1 | `GET /m/{key}`（擁有者 cookie） | — | 200 整份（沒帶 Range，總長 ≤64 MB）、206 一段 | 404、413（沒帶 Range 而且 >64 MB）、416、502 `bad_gateway`、503 `offline`／`busy`（`Retry-After: 2`）、504 `timeout`；圖片的退回見下 |
| E2 | `GET /t/{key}?s=160\|320[&v=]` | — | 200 PNG／JPEG（以縮圖內容的 SHA-256 當 ETag、自己回 304：`thumb_blob`、快取與轉送來的都一樣，2026-10-23 以前只有 `thumb_blob`） | 400 `invalid`（`s`、`v` 不合格）、404、502、503、504 |
| E3 | `GET /f/{key}` | — | 200／206（文字 ≤2 MB inline，其他 attachment） | 同 E1 |
| E4 | `PUT /api/v1/uploads/{uploadId}`（擁有者 cookie＋`Origin`＋`X-Myriagent-Csrf: 1`） | 512 MB | 201 新的、200 同 id 同內容，`{uploadId, sha256, size}` | 400 `invalid`、409 `conflict`（同 id 正在上傳或內容不同）、413 `too_large`、507 `pending_full`／`insufficient_storage`、503 `unavailable` |
| E5 | `PUT`／`HEAD /api/v1/device/blobs/{sha256}` | 4 MB | `PUT` 201 新的、200 已存在（不讀本文），`{sha256, length, existed}`；`HEAD` 200（列與檔都在）／404（有列沒檔也是 404，裝置會重傳、把檔補回來） | 411 `length_required`、400 `mismatch`、413、422 `invalid`（不是嚴格 UTF-8）／`dlp`、507 |
| E6 | `GET /api/v1/device/blobs/{sha256}` | — | 200／206（單一區間，`application/octet-stream`、`no-store`） | 404、416 |
| E7 | `PUT /api/v1/device/relay/{relayId}` | 8 MB | 204 | 404（伺服器已不等這個轉送，或不是這台的）、410 `gone`（瀏覽器離開、逾時）、400 `invalid`（標頭不合格）、413 |
| E8 | `PUT /api/v1/device/thumbs/{ref}`（`X-Myriagent-Conversation`） | 512 KB | 200 `{sha256, length}`，綁到 `media_refs.thumb_blob` | 400、404（參照不在或不是這個對話的項目參照）、409 `policy`（對話不是這台的或不是 `full`）、411、413、415 `unsupported_type`（只看 PNG／JPEG 魔數）、507 |
| E9 | `GET /api/v1/device/media/{key}?purpose=media\|thumb[&s=]` | — | 同 E1／E2，一律 `no-store` | 同 E1／E2 |
| E10 | `GET /api/v1/device/thumbs/missing?before=&limit=` | — | 這台還缺縮圖的項目參照（只有 `item:*`），依 `created_at` 由新到舊、每頁 ≤200，`next` 是下一頁的 `before` | 400 |

- Kestrel 預設的 1 MB 本文上限在 E4、E5、E7、E8 各自於讀本文之前放寬到上表的值；`Content-Length` 宣告超過或實際讀超過都是 413。
- 上傳（E4）：一次 PUT、邊收邊算 SHA-256；不做文字 DLP（上傳的檔只成為附件）。待處理（還沒成為附件）的合計 ≤4 GB（`Hub:Media:MaxPendingUploadBytes`），引用 24 小時過期（`blob_refs(sha, 'upload', '{uploadId}@{unixMs}')`）。`uploadId` 照 `UploadIdRules`（1～64 個英數字、`_`、`-`），和遠端指令的 `uploads` 同一份。
- **`addUploaded` 的 `size`**：指令型錄的形狀上限是 2 GB（`AgentAttachmentRules.MaxAttachmentBytes`，桌面附件的上限），伺服器的上傳上限是 512 MB（`Hub:Media:MaxUploadBytes`）。形狀檢查只擋 2 GB 以上；伺服器簽信封之前以 `IUploadRegistry.TryVerify` 確認每一個 `{uploadId, size, sha256}` 都是這台伺服器上完成、沒過期的上傳（不符就以 `invalid`「上傳的檔案在伺服器上找不到、內容對不上或已過期，請重新上傳」立即拒絕，不簽、不寫指令列），所以實際上只會有 ≤512 MB 的；兩個上限分開，日後調高 `MaxUploadBytes` 不必改協定。`conversationCreate` 的 `uploads` 同樣檢查。
- 容量：blob 配額 200 GB（`Hub:Media:QuotaBytes`），80% 告警、95% 起新的 blob 一律 507；資料根所在磁碟的可用空間低於 40 GB（`Hub:Media:MinFreeBytes`）時也是 507。事件與對話照收。

**轉送（H1＋E7）**
- 伺服器向來源電腦要的每一段 ≤8 MB（`MediaRelayRules.MaxSegmentBytes`），每台來源同時 ≤20 個（依連線記，連線關閉只取消那條上的轉送）、所有來源合計同時 ≤64 個（`Hub:Media:MaxConcurrentRelays`；兩種滿了都先排隊、不送 `MediaRequest`，名額空出來依到達的順序交給那台與合計都還有空位的，排隊等滿 10 秒〔`MediaRelay.QueueWait`〕才回 503 `busy` 加 `Retry-After: 2`；2026-10-28 以前滿了直接回 503，瀏覽器的 `<img>` 不照 Retry-After 重試，圖多的對話一打開就有破圖），首位元組 15 秒沒到回 504；轉送不落地（管線直通、有背壓；瀏覽器讀得慢時一個轉送佔伺服器約 2 MB），裝置的本文提早結束或長度不符時中止給瀏覽器的連線（不讓截斷的內容被當成完整的）。
- Range（`HttpRangeRules`）：單一區間 `bytes=a-b`、`bytes=a-` 照轉，開放式或超過 8 MB 的縮成 8 MB、回 206 與實際的 `Content-Range`；**後綴範圍** `bytes=-N`：凍結的 `MediaRange` 沒有後綴的寫法，伺服器先以 `{start: 0, end: 0}` 問總長（讀完那 1 個位元組），再要 `max(0, 總長−N)` 到結尾；多區間、不是 `bytes` 或不合格的照 RFC 9110 忽略（整份）；起點在結尾之後 416（`Content-Range: bytes */總長`）。沒帶 Range：以多次 ≤8 MB 的轉送接成一個 200（`Accept-Ranges: bytes`），每段的 ETag 與總長要和第一段相同，否則中止連線。
- E7 的標頭（Protocol 的 `MediaRelayHeaders`，兩邊共用）：`X-Status`（裝置判斷的狀態：200、206、403、404、413、416、503）、`X-Total-Length`（總長）、`Content-Range`、`Content-Type`、`ETag`、`X-File-Name`、`X-Content-Kind`（`text`／`binary`，`/f/` 決定 inline 預覽或下載；`text` 只給嚴格 UTF-8 的檔，UTF-16 等其他編碼的文字檔照樣掃權杖、標成 `binary`）、`X-Myriagent-Error`（`MediaErrorCodes`：`not_found`、`policy`、`forbidden`、`too_large`、`dlp`）。拒絕的回覆不帶本文（`Content-Length: 0`）。
- **`X-File-Name` 的編碼**（`MediaHeaderRules.EncodeFileName`）：`UTF-8''` 加檔名 UTF-8 位元組的百分比編碼（RFC 8187；英數字與 `!#$&+-.^_`|~` 不編碼）；HTTP 標頭只能 ASCII，中文檔名照原樣送會讓整個回應失敗。編碼端不清理；伺服器解碼（`DecodeFileName`）後清理（`CleanFileName`：只留最後一段、去掉控制與格式字元、引號與 `<>:|?*` 換成 `_`、≤255 字保留副檔名），再組 `Content-Disposition`（`filename*` 加去掉非 ASCII 的 `filename` 後備）。
- 回應的標頭：`/m/`、`/t/`、`/f/` 與 E9 的 CSP 一律是 `MediaHeaderRules.MediaCsp`（`default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox`）、`X-Content-Type-Options: nosniff`；內容型別只收白名單（可顯示的圖片與可播放的影片，沒有 SVG 與文字型別），其他改成 `application/octet-stream` 加 attachment。`/m/`、`/t/` 是 `private, max-age=300`，`/f/`、E9 與退回的縮圖 `no-store`；`/m/` 轉傳裝置的 ETag；`MediaRequest` 沒有 If-None-Match，所以瀏覽器帶 If-None-Match、沒帶 Range 時伺服器先以 `(0,0)` 向電腦要 1 個位元組拿 ETag，相同回 304（不把整個檔從電腦再傳一次），不同或拿不到才照常整份轉送（2026-10-23 起）。
- **圖片的退回**：`/m/`（與 E9 的 `media`）的圖片在來源離線、沒有 `media1`（503）或電腦回 403 時，改回 `thumb_blob` 或縮圖快取（`Cache-Control: no-store`、`X-Myriagent-Fallback: thumb`）；原檔已經不在（電腦回 404，2026-10-27 起）時同樣先改回縮圖，沒有縮圖而且是瀏覽器 `<img>` 要的（`Sec-Fetch-Dest: image`）`/m/`、`/t/` 回一張「圖片已不存在」的 SVG 佔位圖（200、`Cache-Control: private, max-age=3600`、`X-Myriagent-Fallback: missing`；桌面複本的代理只收 PNG、JPEG，照回 404）；影片照回錯誤。
- **桌面的回覆**（`MediaServer`）：引擎執行緒查 `media_refs_local`，不認得的參照立刻回 404（伺服器不必等滿 15 秒）；同時超過 20 個回 503。背景：經 `RunOnUiAsync` 取中樞的快照（對話存在、本機擁有、不是複本、目前政策 `full`，否則 404）→ 解析路徑（附件由 `attachment_id` 找 `StoredPath`）→ 遠端政策（Q1-A：Off 或暫停時 `media`、`file` 回 403 `policy`，`thumb` 照樣給；View、Control 才給原檔）→ Codex 產生的圖片只給 `attachments\generated\` 的複本（Q2-A，定稿時複製）→ `RemoteFileGuard.OpenAsync`（範圍、禁止清單、重新解析點、UNC）→ 回原檔的一段、320 px 的 PNG 縮圖（自己的 `ThumbnailService`，>512 KB 回 413），或 `file`（整份讀進記憶體 ≤20 MB、文字整份跑 `CredentialLeakRules.HasLeak`，命中 403 `dlp`）。
- 縮圖的推與拉（E8、E10）：項目定稿時推（只在 outbox 沒有一般事件時排，佇列 ≤500、滿了丟最舊的）；每次協商出 `media1` 進 Live 時以 E10 拉缺漏清單補上。上傳時原檔已經不在（守門員回 `not_found`：回覆的媒體被刪掉、又沒有留存的副本）的參照，桌面記在自己 sync.db 的 kv（`thumb_gone`）7 天，期間 E10 再列出來也不再試（2026-10-27；伺服器照舊列著，協定不變）。抽屜的縮圖一律按需轉送、放進伺服器 `cache\thumbs\` 的 LRU（≤2 GB），不上傳 blob。
- 指令輸出（14.6）裡的檔案訊息（`files`、`fileSearch`、`fileRef`）：電腦只送參照（entry 的 `ref`、`ver`，`fileRef` 的 `ref`），不送任何網址；伺服器投影（`CommandOutputFileRules`：只留已知的鍵、`requestId` 與 `conversationId` 要等於指令的）之後由 `ICommandOutputRewriter` 換成本站網址並登記 `list`／`file` 參照（見 web-protocol 的 `files`、`fileRef`）。

### 14.4 遠端指令的信封（`cmd1`；WP4.5 簽發、WP4.4 驗證）

信任模型（設計 8.11，D1）：瀏覽器只有擁有者的工作階段（cookie），沒有任何金鑰；伺服器依型錄、形狀、層級與驗證時間檢查後，以**伺服器身分金鑰**（Hello 的 `hubSig` 用的同一把，`HubIdentity.Sign`）簽一個只對一台電腦、一條連線、一個指令有效的信封；裝置以配對時釘選的伺服器身分公鑰驗簽，再依自己的本機政策與事實決定。伺服器只是第一道：層級由裝置重算後取較高者（D5），所以伺服器的程式錯誤不會讓層級降低；擁有者密碼或伺服器被拿到時擋不住（Q1），最後防線是電腦本機的政策（預設 `Off`）。

```jsonc
// 伺服器 → 裝置：CommandDelivery（Send）
{ "commandId": "01J9Q…",                      // 等於信封的 cid
  "envelope": { "v": "myriagent-hub-cmd-v1",
                "body": "{\"aud\":\"https://hub.example.com\",\"cid\":\"01J9Q…\",\"dev\":\"d_…\",\"conn\":\"…\",\"conv\":\"5f3a…\",\"epoch\":1,\"cmd\":{\"cmd\":\"approve\",…},\"tier\":1,\"fullAccess\":false,\"durable\":false,\"created\":…,\"iat\":…,\"exp\":…,\"issuer\":{\"kind\":\"browser\",\"label\":\"Chrome・Windows\"},\"auth\":{\"uvAt\":…,\"defaultPassword\":false}}",
                "sig": "base64url(P1363 64 位元組)" } }
// sig = Sign(伺服器身分私鑰, UTF-8("myriagent-hub-cmd-v1\n" + body))
```

- **body**（`CommandEnvelopeBody`）：`aud`（伺服器的公開來源；電腦以配對時記下的伺服器網址比對，兩邊的正規化見下方「伺服器的簽發」）、`cid`（伺服器產生的 ULID，D17；瀏覽器的 commandId 另存、只用來回覆）、`dev`（目標裝置）、`conn`（目標裝置這條連線 Hello 回應的 `nonceS`：每條連線在 Hello 新產生一次、在 `hubSig` 的範圍內，裝置看得到；**不是** SignalR 的連線 id——裝置略過 negotiate，拿不到它。伺服器記在 `DeviceConnection.EnvelopeConn`、桌面記在 `SyncConnection.EnvelopeConn`，SignalR 的連線 id 只拿來路由）、`conv`／`epoch`（型錄路由 Owner 的對話指令必填；路由 Device 的帳戶、CLI 指令與 `conversationCreate` 不帶）、`cmd`（網頁的指令物件，形狀照型錄）、`tier`（伺服器算的層級，0～2）、`fullAccess`、`durable`、`created`（指令建立的伺服器時間；重簽時不變）、`iat`／`exp`（這次簽發的伺服器時間）、`issuer {kind: "browser", label}`（瀏覽器與作業系統，≤64 字、沒有 Cc／Cf 類字元；另一台桌面發起的是 `{kind: "device", label: 那台電腦的名稱}`，14.7）、`auth {uvAt, defaultPassword}`（工作階段最後一次輸入密碼的時間、擁有者密碼是不是預設值）。序列化（`CommandEnvelope.SerializeBody`）依上面的順序、不縮排，`conv`、`epoch`、`auth.uvAt` 沒有時省略。電腦解析時鍵只收這 15 個（伺服器多送一個就拒絕：信封要改格式就換版本字串）；`conv`、`epoch`、`auth.uvAt` 可以省略或是 null，其他必填；數字一律是整數（long；小數與超出 long 的拒絕），`tier` 另外要在 int 的範圍；`issuer` 恰好是 {kind, label}、`auth` 只有 {uvAt?, defaultPassword}。
- 簽的是 `body` 字串的原始 UTF-8 位元組，**驗完才解析**，不需要 JSON 正規化。`EnvelopeSigner` 委派（`ReadOnlySpan<byte>` → base64url）可以直接綁 `HubIdentity.Sign`；驗章用 `P256KeyRules.Verify`（大內容另有 `VerifyHash`：以 SHA-256 雜湊驗同一個簽章）。伺服器以 `CommandEnvelope.TrySign` 簽：body 的 UTF-8 超過 128 KB、或有不成對的代理字元（簽不出唯一的位元組）時不簽；要確認簽出來的電腦收得下，以自己的公鑰呼叫 `Verify` 即可。
- **裝置的驗證順序固定**（`CommandEnvelope.Verify`，先失敗的先回報；失敗一律寫遠端操作紀錄）：
  1. 版本 `v` 是 `myriagent-hub-cmd-v1`。
  2. `body` 的 UTF-8 ≤128 KB；有不成對的代理字元也在這一步拒絕（U+D800 與 U+FFFD 會編成同樣的位元組，簽章分不出兩者）。
  3. 簽章（釘選的伺服器身分公鑰）。
  4. 解析：重複的鍵（含巢狀）、註解、結尾多餘的內容、深度超過 32 一律拒絕（不同的解析器對重複鍵取前或取後不一致；`ConversationEventRules.TryParseStrict`）；鍵與型別照上面的 body。
  5. `aud` 是這台配對的伺服器、`dev` 是自己、`conn` 是收到它的這條連線（這條連線 Hello 回應的 `nonceS`；依這個順序，各有自己的步驟名稱）。
  6. `cid` 是 ULID 而且等於外層的 `commandId`。
  7. `tier` 在 0～2。
  8. 時間（全部用伺服器時間，D4）：`0 ≤ exp − iat` ≤60 秒、`iat` ≥ `created`；非 durable 的 `exp` ≤ `created`＋60 秒；`durable` 只限型錄標了 `DurableAllowed` 的指令（目前只有 `accountLogout` 的「所有電腦登出」）而且 `Upper − created` ≤7 天；`Upper > exp` 為過期；`iat > Lower＋15 秒` 為還沒生效。伺服器時間推算不可用時只收 `stop`（信封自己的時間照樣檢查，界線不看）。
  9. `issuer.kind` 是 `browser`，或收到它的這條連線協商出 `peer1` 時的 `device`（14.7；大小寫要相同；`CommandEnvelopeTarget.AcceptsDeviceIssuer`）、`label` 合格：合格的 UTF-16、≤64 個 Unicode 字元（不是 UTF-16 單位；空字串可以），沒有 Cc、Cf（例如 U+202E 由右至左覆寫、U+200B 零寬空格、U+FEFF）、Zl、Zp（U+2028、U+2029）類的字元（`CommandEnvelope.IsValidIssuerLabel`；本機通知與遠端操作紀錄會顯示它）。
  10. `cmd` 與 `conv` 一致：`conv` 與 `epoch` 一起帶或一起省略、`conv` 是線上的對話 id（小寫）、`epoch` ≥1；路由 Owner 的指令必帶 `conv`、路由 Device 的不帶；有 `conv` 時 `cmd.conversationId` 要等於它、沒有時 `cmd` 不能有 `conversationId`；`cmd.deviceId` 有帶時要等於 `dev`。型錄的路由與形狀不在信封判斷（不在型錄的指令信封照樣驗得過，由閘門 5a 拒絕），`epoch` 對不對也是 5c 的事。
- **失敗的代碼**：第 8 步看伺服器時間界線的（過期、還沒生效、durable 超過 7 天、推算不可用）是 `expired`，其他一律 `invalid`。步驟名稱（`CommandEnvelopeSteps`：`version`、`size`、`signature`、`parse`、`aud`、`dev`、`conn`、`cid`、`tierRange`、`time`、`issuer`、`target`）只進遠端操作紀錄；第 7 步叫 `tierRange`，因為閘門 5d 已經叫 `tier`。解析過了（第 4 步之後）的失敗也帶內容與內容摘要，紀錄寫得出是哪個指令。
- **型錄與形狀**（`Protocol/Remote/RemoteCommandCatalog`、`RemoteCommandShapeRules`，D6、D53；伺服器簽之前與電腦的閘門 5a、5b 共用）：`cmd` 只能是型錄裡由電腦執行的指令——路由 Owner（對話指令，轉給擁有者，第一個欄位是 `conversationId`）或 Device（帳戶、CLI 與 `conversationCreate`，轉給指定的電腦；可以帶 `deviceId`＝瀏覽器選的電腦）。伺服器自己處理的（`ready`、`setting`、`newConversation`、`copyItem` 等）、只在電腦本機的（`sync` 後面接大寫字母的、`openPath` 等，與 web-protocol 的 desktop_only 相同）與沒列名的一律不簽、電腦一律拒絕。形狀比網頁指令嚴：只收型錄列的鍵（多一個就拒絕，網頁指令是忽略）、型別與長度照型錄（一般字串 ≤4,096 字、送進代理的文字 ≤16,000 字、整份 ≤96 KB、巢狀 ≤8 層）、列舉只收名稱（數字、數字字串、大小寫不同都拒絕）；不合格回 `invalid`。每個指令的層級（`RemoteTierRules`）、`CommandOutput` 的白名單（14.6）與遠端的限制（`setOption` 只改對話、`setFolder` 不寫最近資料夾、`accountRemove` 一律保留資料夾，D15）也在型錄。
- **`conversationCreate`**（只由伺服器送給電腦，不是網頁指令：瀏覽器的伺服器草稿第一次 `send` 時，設計 4.9.2）：`{cmd: "conversationCreate", accountId, permissionMode, model?, effort?, folder?, title?, text?, uploads?}`。`permissionMode` 是權限模式的名稱（`Ask`／`AcceptEdits`／`Plan`／`Auto`／`FullAccess`）；`model`、`effort` 可以是 null（照那台電腦設定頁的「模型設定」）；`folder` 是 null 或省略＝對話自己的 workspace（不取最近資料夾，D15）；`text` ≤16,000 字；`uploads` 同 `addUploaded`。層級取模式（照 `setOption permission`）、資料夾（有給時照 `setFolder`）與第一則訊息（有文字時照代理輸入，模式是新對話的模式；不看任何 CLI 設定檔的預先放行規則，只看模式——完全自動才要「允許遠端完全自動」，WP4.4 Q1-A、設計 14.14）的最高者；輸出可以是 `sendRejected`、`toast`。新對話的 id 是 `RemoteConversationIdRules.FromCommandId(cid)`（ULID 的 128 位元依大端轉成 32 個小寫十六進位，D34）；`accountId` 由伺服器換成目標電腦的本機 id；`title` 沒有就省略。
- **`listFolders`**（2026-10-27；只由伺服器送給電腦，不是網頁指令：瀏覽器的伺服器草稿上「瀏覽資料夾…」送的 `listFiles {dirsOnly: true}` 由伺服器改成它）：`{cmd: "listFolders", deviceId, requestId, path}`，路由 Device，目標是草稿選的電腦（沒選照第一次 `send` 的挑法），層級 2（同 `dirsOnly`）。電腦還沒有這個對話：範圍的根只有這台的最近資料夾（沒有工作資料夾；禁止清單同 `listFiles`，但沒有對話的允許子樹，app 的資料夾整個不給），`path` 是 null 回最近資料夾的虛擬根，否則列那個資料夾底下的資料夾；範圍外回 `policy`。輸出只有 `files`、`toast`：`files` 的 `conversationId`、`root` 是 null（伺服器只收 `conversationId` 是 null 或省略的，換成草稿 id 給瀏覽器；不登記媒體參照），被拒時伺服器不另送 toast（同 `listFiles`，網頁的抽屜自己說明）。舊版的電腦不認得它，在閘門 5a 回 `invalid`。
- **伺服器的簽發**（WP4.5，`CommandRouter`；設計 14.11 D17、計畫 D6～D16）：
  - `aud`：伺服器簽 `HostingRules.NormalizeOrigin(HostingRules.PublicOrigin(options))`（scheme 與主機小寫、預設埠省略、IDN 轉成 punycode、不帶路徑）；電腦以同一套規則正規化配對時的伺服器網址再以序數比對。以別名（另一個主機名稱、IP）配對的電腦不支援（計畫 D12）。設定不合格時不簽，瀏覽器的 `Cmd` 回 `unavailable`。
  - 帳戶 id：瀏覽器送的邏輯帳戶 `la_…` 由伺服器換成目標電腦連結的本機帳戶 id（`account_links`，D6），那台沒有連結回 `rejected invalid`；`cmd.deviceId` 只在型錄有這個欄位時寫入（D8；`conversationCreate` 沒有）。改寫後的 `cmd` 再過一次 `RemoteCommandShapeRules`。
  - 自驗（D14）：簽完以自己的身分公鑰照電腦的 `CommandEnvelope.Verify` 驗一次（時間界線用伺服器現在的時間），驗不過不送、不寫列；失敗在簽章這一步時中斷那條連線。
  - 指令列（sync.db `commands`）：`cmd_json` 是遮蔽後的指令（送進代理的文字只留長度、不留雜湊），`content_sha256` 等於信封的內容摘要；`unsigned_body` 只有 durable 列保存（重簽用），到了最終結果或 expired 才清掉。列提交之後才送出、寫稽核（`command.issued`）。
- **竄改的自動暫停**（D24）：依擁有者 2026-10-19 指示（自用、伺服器可信任）不做；驗不過的信封（含簽章不符、`aud`／`dev` 不符）照樣拒絕並寫進遠端操作紀錄，暫停只靠本機的設定與系統匣的「暫停遠端操作」。`CommandEnvelopeVerdict.CountsAsTampering` 留在 Protocol、不呼叫（WP4.4）。
- **伺服器身分金鑰輪換**（D2、D24）：伺服器只對「Hello 時 `hubSig` 所用的身分公鑰等於目前的身分公鑰」的連線簽信封（`DeviceConnection.CanSignEnvelopes`）；不符時不簽——裝置還釘著舊公鑰，送出去必然驗章失敗、被算成竄改——改中斷那條連線、對瀏覽器回 `offline`（WP4.5）。輪換（`rotate-hub-identity`）之後伺服器立刻中斷所有裝置 hub 連線（`ConnectionRegistry.AbortAll`），裝置重連時依 Hello 的 `rotation` 自動改釘（第 3 節）。
- **伺服器時鐘**（`ServerClockRules.HelloClock`，D4）：裝置不用自己的 UTC 時鐘判斷信封。以 Hello 送出與收到回應的單調時鐘時間點（`Stopwatch`）推算：`Lower = serverTime ＋（現在 − 收到回應）`、`Upper = serverTime ＋（現在 − 送出 Hello）`（換算成毫秒時下界無條件捨去、上界無條件進位，`ServerClockRules.Bounds`）。往返超過 30 秒（`MaxRttMs`，剛好 30 秒可用）、單調時鐘倒退（現在早於收到回應）、算出來超出 long 都視為不可用，除了 `stop` 一律拒絕（`expired`）。睡眠或休眠恢復後一律重新 Hello（12.1），之前收到的信封都當成不可用。電腦（WP4.4，`SyncCommandRules.UsableBounds`）在 Hello 時鐘滿 12 小時後不再使用；協商出 `cmd1` 的連線時鐘滿 6 小時而且沒有執行中的指令時重新 Hello；時間判不過時每分鐘最多重新 Hello 一次（以單調時鐘判斷：牆上時鐘往回調不會讓它卡住）；時鐘不可用時非 `stop` 照「失敗的代碼」回 `expired`（不寫 `commands_seen`，伺服器以新時間重簽的 durable 照常執行）。
- **內容摘要**（`ContentDigest`，D2、D17）：不隨重簽改變的欄位（`aud`、`cid`、`dev`、`conv`、`epoch`、`cmd`、`tier`、`fullAccess`、`durable`、`created`、`issuer`、`auth`）依這個順序組成 JSON 陣列（不縮排；`conv`、`epoch` 沒有時是 null；`cmd` 依收到的鍵順序；`issuer` 寫成 `{kind, label}`、`auth` 寫成 `{uvAt, defaultPassword}`、`uvAt` 沒有時是 null）後取 SHA-256（64 個小寫十六進位），兩邊都存成 `content_sha256`。`conn`、`iat`、`exp` 不在裡面，所以重簽後摘要不變；固定向量在 `CommandEnvelopeTests`。
- **不排隊、durable 重簽**（D16）：非 durable 的指令不排隊，目標不在線伺服器立刻回 `offline`。durable 的伺服器保存**未簽的**內容與 `content_sha256`，裝置每次 Hello 之後 60 秒內以新連線的 `conn`（新的 `nonceS`）、新的 `iat`／`exp` 重簽送出；`created` 不變，所以 7 天上限以原始時間算。伺服器重簽的時機（WP4.5，D27）：那台的連線 Prove 完成（`ConnectionProven`）、pending 列提交之後（補上提交與 Prove 掃描之間的競態）、router 直接送出 delivered 列之後（只送送在別條連線上的：router 選了舊連線、新連線的 Prove 掃描在提交之前就跑完時，以新連線補送同一個 cid）、每 60 秒的重掃，一律選那台 `Order` 最大、已證明、協商出 `cmd1`、能簽的連線；電腦回 `busy`／`expired`（什麼都沒執行）時列回到 pending，5 秒後在同一條連線以新的 `iat`／`exp` 重簽重送，每條連線最多 3 次；之後不再排 5 秒的重送，改由下一次 Prove 或每 60 秒的重掃再送（重掃不看重送次數，WP4.8 起：電腦回 `busy` 的原因可能持續很久——這個帳戶正在那台登入、那台的 CLI 正在更新——一直連著的電腦不會再 Prove），直到完成或 `created`＋7 天到期。服務啟動時 durable 的 delivered 改回 pending、非 durable 未結束的標成 `unknown`（絕不重送，D28）。
- **去重與至多一次**（D17，同步不變式）：驗章 → 查 `commands_seen(cid)`：已有而且 `content_sha256` 相同就回上一次的結果（`count`＋1），不同就拒絕 `conflict` → 寫 `executing`（與遠端操作紀錄同一個交易）→ 執行 → 寫結果。執行中當機的，重啟後回 `unknown`、不重做。`busy` 不留在 `commands_seen`（什麼都沒執行，可以再送）；資料庫是 `executing` 而這次執行已不在（完成時寫不進去）時，重送回已完成結果的快取，快取沒有就回 `unknown`；驗信封失敗的不寫 `commands_seen`（同一個 `cid` 之後合格的信封照樣執行）。`commands_seen` 保留 8 天、遠端操作紀錄 180 天（WP4.4）。
- **早到緩衝**（D49，`SyncEarlyMessages`）：交握完成之前（伺服器身分還沒驗過、Prove 還沒完成或名冊還在更新）收到的第 4 階段訊息依到達順序暫存、不呼叫任何 hub 方法（12.2 的作法）：指令最多留 16 則，再來的只記 `commandId`（ULID 才記，最多 64 個；更多的不記，伺服器等不到結果照逾時處理），其他新訊息（`WatchChanged`、`SnapshotRequest`、`MediaRequest`、`PeerReply`）最多 32 則（溢出丟最舊的那則，指令不受影響）；整份取代的 `TokensUpdate`、`StatsUpdate` 每種只留最新的一則（2026-10-23 起；之前收到它們會丟例外、斷線重連，14.10）。交握完成時依到達順序處理（照樣先看功能），之後對溢出的指令回 `CommandResult {state: "rejected", code: "busy"}`（協商出 `cmd1` 才回）；連線在交握完成前中斷（例如伺服器身分驗證失敗）就整份丟掉。
- **速率**（D25）：依擁有者 2026-10-19 指示（自用、伺服器可信任）不做：伺服器不限每台電腦的指令數（瀏覽器 `Cmd` 照第 3 階段每個工作階段每秒 20 次），電腦也不限（WP4.4），`rate_limited` 不會出現在 `commandResult`。`RemoteRateRules` 留在 Protocol、不呼叫。

### 14.5 卡片摘要 rd1（`RequestDigestRules`，D36）

遠端回應核准卡、問卷與計畫確認卡（`approve`、`answer`、`approvePlan`）時必須帶卡片的摘要；電腦以目前的卡片重算，不同就回 `stale`（本機的 `RejectStaleCard` 在值是 null 時放行，遠端不放行）。摘要只由擁有者電腦算（`RequestKey` 只在那台的記憶體裡），存在項目的 `requestDigest`、隨 `conv.item` 發布。

- 輸入：前綴 `rd1\n`，接著依序編碼 `kind`、`toolName`、`toolKind`、`title`、`text`、`detail`、`filePath`、`canAllowForSession`、`allowSessionLabel`、`requestKey`、差異（行數，接每行的 `t`、`s`）、題目（題數，接每題的 `id`、`header`、`text`、`multiSelect`、`allowOther`、`isSecret`、選項數，接每個選項的 `label`、`description`、`preview`）。
- 每個值編成「UTF-8 位元組數」`:`「值」（例：`4:true`、`3:Ask`），null 編成 `-:`；布林是 `true`／`false`，列舉用名稱，數目用十進位；值之間不加分隔。不做 Unicode 正規化。
- 輸出 `rd1:` 加 SHA-256 的 64 個小寫十六進位。`requestKey` 是 null（卡片不在等待）時沒有摘要，遠端不能回應。
- 差異或題目清單本身是 null 時數目編成 `-:`（與空清單的 `1:0` 不同）；清單裡的 null 元素（壞檔）佔一個位置、每個欄位都編成 `-:`。電腦比對用 `RequestDigestRules.Matches`（固定時間比較；遠端帶來的是 null 一律不符）。固定向量在 `RequestDigestRulesTests`（另以 Python 的 hashlib 從同一串輸入核對過）。

### 14.6 結果與輸出（`cmd1`；WP4.4 送、WP4.5 收）

| 方向 | 方法 | 送法 | 內容 |
|---|---|---|---|
| 伺服器→裝置 | `CommandDelivery` | Send | 14.4 |
| 裝置→伺服器 | `CommandResult` | Invoke | `{commandId, state: "done"｜"rejected"｜"unknown", code?, message?, tier?, fullAccess?}` |
| 裝置→伺服器 | `CommandOutput` | Invoke | `{commandId, message}`：指令執行後 30 秒內中樞回給「這個檢視」的網頁訊息 |

- 大小：`CommandResult`、`CommandOutput` 序列化後 ≤1 MB − 4 KB（呼叫框的預留，1.1），超過的裝置不送（程式錯誤，`too_large`）。
- `state`：`done`＝電腦的中樞已接受（不是代理這一輪結束）；`rejected` 帶 `code`；`unknown`＝執行了但 30 秒內沒有完成信號，或執行中當機（指令可能已執行，瀏覽器不得自動重送）；伺服器 100 秒（`iat`＋100 秒）內沒收到結果也把列標成 `unknown` 並轉給瀏覽器（非 durable；durable 不受這個期限）。`code` 與瀏覽器看到的相同（`docs/web-protocol.md` 的 `commandResult`）。
- 遲到的結果（列已結束：期限到了、或重複回報）只記進列的 `result_json.late`（最多 4 筆，保留先到的）、不改狀態、不轉送；指令結束後 30 秒內的輸出照樣轉送，之後的丟掉（D19）。
- 伺服器只接受**目標裝置**任一條已證明連線送來的結果與輸出（重連之後送回的結果是合法的，D18），格式照 `RemoteResultRules`：`Check`（`commandId` 是 ULID、`rejected` 一定帶認得的 `code`、`done`／`unknown` 不帶、`message` ≤1,000 字、`tier` 0～2）、`CheckOutput`（`message` 是有字串 `type` 的物件，種類照伺服器記下的原本那個指令）；`CommandOutput` 的 `message.type` 只收型錄給這個指令列的白名單（例如 `listFiles` 只能回 `files`、`accountLogin` 只能回 `accountLogin`，另外都可以回 `toast`），其他丟棄；驗證網址的主機只收白名單（`RemoteLoginUrlRules`，D18）。伺服器只把它轉給發起的那條瀏覽器連線，不保存。
- **電腦端的處理順序**（中樞閘門 `ExecuteRemoteAsync`，設計 8.11）在信封驗過、去重之後，任一步失敗以下表的 `code` 回 `rejected`，`step` 只進遠端操作紀錄：

| 步 | `step` | 檢查 | `code` |
|---|---|---|---|
| 5a | `route` | 型錄的路由是 Owner／Device 執行（ServerOnly、桌面專屬與沒列名的一律拒絕） | `invalid` |
| 5b | `shape` | `RemoteCommandShapeRules`：鍵白名單、型別、數值列舉一律拒絕（只收型錄列出的名稱） | `invalid` |
| 5c | `conversation` | 對話存在、不是草稿或副本、擁有者是這台、`epoch` 相符（路由 Device 的指令〔含 `conversationCreate`〕不看對話，改驗 `dev`）；對話的有效同步政策是 `full`（有伺服器刪除旁檔的一律算 `localOnly`：從回收區還原的、換過不進同步的代理種類的；`metadata`、`paused` 只收 `stop`：兩者都照樣發 `meta` 類，遠端看得到對話與執行中；DLP 在一輪執行中命中而自動 `paused` 時正需要從遠端停下代理）；兩種路由都一樣：型錄裡種類是 `AccountId` 的欄位帶了字串值時，要是這台的本機帳戶（WP4.4；伺服器簽之前已把邏輯帳戶 `la_…` 換成本機 id） | `not_owner`；同步政策不允許時 `policy`；帶 `accountId` 的指令，帳戶不是這台的本機帳戶回 `invalid` |
| 5d | `tier` | 依這台的事實重算層級，取與信封較高者、`fullAccess` 取 OR；`setAccount` 換到另一家代理時權限模式會改成那一家設定頁的預設，模式改變時另以改成的模式的層級（同 `setOption permission`）取高，改成完全自動要 `allowRemoteFullAccess`（WP4.4 合併前審查 GATE-1） | — |
| 5e | `policy` | 本機政策（暫停等於 `Off`；`View` 不收任何指令；`Control` 收 0～2，`fullAccess` 要 `allowRemoteFullAccess`） | `policy` |
| 5f | `fresh` | `RemoteAuthRules.IsFresh(tier, uvAt, created)`：目前兩個時間窗都是 null（只要工作階段有效，Q2） | `step_up_required` |
| 5g | `defaultPassword` | 擁有者密碼是預設值：`DefaultPasswordMaxTier` 是 2，不拒絕，只記錄與警告（Q5） | — |
| 5h | `card` | 卡片摘要與 `RequestKey`（14.5）；卡片不讓「本對話都允許」卻選了 `allowSession`、答案對不回題目 | `stale`；後兩者 `invalid` |
| 5i | `authorize` | 登記的授權者：WP4.4 起依擁有者 2026-10-19 指示（自用、伺服器可信任）一律放行（`OwnerTrustedRemoteAuthorizer`），信封與 5a～5h 是唯一的檢查；WP4.1 的預設 `DenyAllRemoteAuthorizer` 一律拒絕 | WP4.4 之前 `unsupported` |
| 5j | `handler` | 先套用型錄的遠端限制（`ApplyLimits`）、`conversationId` 換成這台的對話 id，交給登記的處理者；沒有處理者時只有直接走本機流程也符合遠端限制的指令（不送進代理、不寫對話以外的預設與最近資料夾、不刪對話、不列檔；`AgentHub.RemoteFallbackCommands`：`cancelQueued`、`stop`、`skipQuestion`（只對問卷卡有作用：核准卡、計畫卡另有摘要檢查，不能拿它略過）、`rename`、`resetFolder`、`removeAttachment`、`accountLoginCancel`、`accountRemove`、`accountRefresh`、`cliCheck`）以回應檢視走中樞的 `HandleCommand`，其他回 `unsupported`；之後等完成信號 30 秒。WP4.4 登記 `send`、`answer`、`approve`、`approvePlan`、`delete`（移到回收區）、`setOption`、`setAccount`、`setFolder`、`conversationCreate` 的處理者（遠端不寫這台的持久設定）；WP4.7 登記 `addUploaded`、`listFiles`、`fileSearch`、`fileFetch`（2026-10-27 起另有 `listFolders`）的處理者（`AgentHub.RemoteFiles.cs`；`addUploaded`、`fileFetch` 要經伺服器搬位元組，這條連線沒協商出 `media1` 時回 `unsupported`）；WP4.8 登記 `accountLogin`、`accountLoginDevice`、`accountLoginCode`、`accountLogout`、`cliInstall`、`cliUpdate` 的處理者（`AgentHub.RemoteAccounts.cs`，回報真的結果，不再走退路的假 `done`；條件見 docs/web-protocol.md 的指令表與設計 14.18），`accountAdd` 維持 `unsupported`。`conversationCreate`：id 已存在（含副本、回收區）或帳戶未登入回 `conflict`，`rejected` 一定什麼都沒建，建立之後一律 `done`；沒有 `text`（含空字串）建立空對話；帶 `uploads` 時（WP4.7）先檢查傳輸與名額（沒有 `media1` 回 `unsupported`、同一個上傳正在下載回 `busy`，都什麼都沒建），`done` 之後在背景下載與匯入，第一則等它們處理完才和附件一起送出（上傳失敗時照樣送、對話裡加一則說明的 Notice；沒有 `text` 時附件留在輸入區）。遠端 `send` 只帶網頁上傳（`addUploaded`、`conversationCreate` 的 `uploads`）、還在輸入區的附件，本機使用者自己附的留著；還在下載或匯入時回 `conflict`「附件還在處理中，請稍候」 | `unsupported`；處理者的 `conflict`（這台目前的狀態不允許）、`invalid`（值不合格）、`policy`（這台的設定不允許這個值）、`stale`（卡片已處理或作廢）、`busy`（什麼都沒執行：電腦的程式正在關閉，以及 `accountLogout` 時那台的 CLI 正在更新、這個帳戶正在那台登入；「所有電腦登出」由伺服器重送）；逾時與處理途中出錯為 `unknown` |

- 5a～5i 的拒絕都發生在任何動作之前；授權者放行（5i）之後處理者或 `HandleCommand` 丟出例外一律是 `unknown`（可能已經執行了一部分），不改成拒絕。閘門本身的程式錯誤在 5i 之前是 `invalid`。回應檢視（`RemoteResponseView`）收到的網頁訊息只在授權之後 30 秒內、照型錄的白名單轉成 `CommandOutput`；給所有視窗的廣播（清單、帳戶、`toast` 沒指定檢視的）只到這台的視窗，不會轉出；`HandleCommand` 遇到回應檢視時不轉給同步協調者、出錯不在這台廣播 `toast`。
- 電腦的送出（WP4.4）：同一條連線上依序送，一個指令的 `CommandOutput` 都在 `CommandResult` 之前，結果之後那個指令的輸出丟掉；斷線時結果留著（最多 64 則），到下一條協商出 `cmd1` 的已證明連線再送，輸出不留。結果的 `message` 是給人看的臺灣繁體中文（≤1,000 字），伺服器直接當成瀏覽器的 toast；除錯細節只進遠端操作紀錄，驗信封失敗的 `message` 是 null。遠端操作紀錄的 `step` 除了 `CommandEnvelopeSteps` 與上表的名稱，還有 `seen`（同一個 `cid` 不同內容的去重衝突）。

---

### 14.7 桌面發起的指令（`peer1`；桌面遠端操作，2026-10-22）

擁有者 2026-10-22 決定「已配對就能操作」：已配對的桌面不必另外登入就能經伺服器對其他電腦下指令，被操作的電腦沿用現有的本機政策（`RemoteControlLevel = Control`、完全自動另要 `AllowRemoteFullAccess`），不另加開關。推翻設計 5.12、8.3 與 T6 的「沒有裝置對裝置控制」：任何一台已配對的電腦中毒，就能控制所有設成「控制」的電腦（擁有者已知並接受）。計畫見 `docs/plans/2026-10-22-desktop-peer-control.md`。

| 方向 | 方法 | 送法 | 內容 |
|---|---|---|---|
| 裝置→伺服器 | `PeerCmd` | Invoke | `{commandId, cmd}`：`commandId` 是桌面產生的 ULID（網頁的 commandId，結果以它回），`cmd` 是網頁指令物件（同瀏覽器 `Cmd` 的 `cmd`）；回應 `PeerCmdResult {accepted, commandId?, error?}`（同瀏覽器的 `CmdResult`） |
| 伺服器→裝置 | `PeerReply` | Send | `{commandId, message}`：`message` 是網頁訊息（`commandResult`、`sendRejected`、`toast`，或型錄給這個指令的輸出白名單），內容與瀏覽器收到的相同 |

- **伺服器**（`DeviceHub.PeerCmd` → `PeerCommandHandler` → `CommandRouter.RouteAsync(DeviceCommandOrigin …)`）：連線已證明、協商出 `peer1` 與 `cmd1`（否則 `not_available`）；每台電腦一個權杖桶（同瀏覽器每個工作階段：每秒 20 次、突發 50，超過回 `rate_limited`）；以瀏覽器 `Cmd` 的同一套解析（`MessageGuard.ParseCmd`）檢查形狀與上限，只收型錄路由 Owner／Device 的指令，其他回 `not_available`（`commandId` 不是 ULID 或形狀不合格回 `invalid`）。之後同瀏覽器的路由（14.4 的「伺服器的簽發」），差別只有：
  - 伺服器草稿（`d…`）一律 `invalid`（桌面沒有伺服器草稿）；所有電腦登出（fan-out）立即拒絕 `unsupported`（durable 重簽時才知道目標有沒有 `peer1`）。
  - 目標是發起的電腦自己（Owner 路由的擁有者、Device 路由的電腦）立即拒絕 `invalid`「這是這台電腦自己的對話或帳戶，請直接在這台操作」。
  - 選到的目標連線沒有協商出 `peer1`（舊版桌面的驗章第 9 步不認得 `device`）立即拒絕 `unsupported`「〈電腦〉的 Clode Myriagent 版本太舊…」，不簽、不送。
  - 信封的 `issuer` 是 `{kind: "device", label}`：`label` 是發起電腦在伺服器目錄的名稱，去掉 Cc／Cf／Zl／Zp 類字元與不成對的代理字元、去掉前後空白、截成 64 個 Unicode 字元，空的是「另一台電腦」（`PeerRules.DeviceLabel`）；`auth.uvAt` 是 null（裝置沒有輸入密碼這回事；兩個時間窗目前都是 null，`IsFresh` 照樣通過）、`auth.defaultPassword` 照舊。列的 `issuer` 照樣存 `{kind, label}`；稽核的 actor 是 `device`、id 是發起電腦的 id。
  - 回覆（立即拒絕的 `commandResult`、toast、`sendRejected`，與電腦回報的結果與輸出）經 `CommandReplies` 以 `PeerReply` 送給發起電腦目前 `Order` 最大、已證明、協商出 `peer1` 的連線（重連之後照樣收得到；沒有就丟掉，桌面等不到就當結果不明）；toast 不做 10 秒節流；序列化後超過 1 MB − 4 KB 的丟掉並記錄。`TrackedCommand.OriginDeviceId` 記發起的電腦。
- **被操作的電腦**：Hello 跟著 `cmd1` 一起宣告 `peer1`；驗章時 `AcceptsDeviceIssuer` 是這條連線有沒有協商出 `peer1`。其他驗證、去重、閘門 5a～5j、遠端限制完全不變；遠端操作紀錄、通知與回收區的發起者顯示成「電腦「X」」（`RemoteNotifyRules.IssuerDisplay`）。
- **在其他電腦開新對話**（`CommandRouter.PeerCreate.cs`）：桌面的草稿在桌面本機（不用伺服器草稿），第一次送出時送 `PeerCmd {cmd: "conversationCreate", deviceId, accountId: "la_…", permissionMode, model?, effort?, folder?, title?, text}`（`folder` 是草稿上選的那台的資料夾，沒選就省略＝對話自己的 workspace；那台照 14.4 的 `conversationCreate` 檢查，2026-10-27 起桌面才帶）。`deviceId` 必帶（不是 `d_` 的格式回 `invalid`；型錄沒有這個欄位，投影時去掉）、要存在而且沒撤銷、不是發起的電腦（立即拒絕 `invalid`）；`accountId` 要是目錄裡的邏輯帳戶（不在立即拒絕 `invalid`「這個帳戶已從目錄刪除…」），換成那台連結的本機 id（那台沒有連結回 `rejected invalid`）。之後同一般的指令；新對話的 id＝`RemoteConversationIdRules.FromCommandId(cid)`，寫列之後、送出之前先以 `PeerReply` 送 `peerCreated {commandId, conversationId}`；電腦回的 `sendRejected` 輸出以這個 id 核對。
- **附件與檔案**：上傳是裝置端點 `PUT /api/v1/device/uploads/{uploadId}`（`DeviceAccess`、免 CSRF；和瀏覽器 E4 同一個 `UploadRegistry` 與上限，之後的 `addUploaded` 照樣經 `PeerCmd`）。`fileFetch` 的 `fileRef` 是本站的 `/f/{鍵}`（擁有者 cookie），桌面改以 E11 `GET /api/v1/device/media/{key}?purpose=file`（`DeviceAccess`；同 `/f/`，只收 `file` 的參照、`no-store`）取檔；`listFiles` 的 `files` 裡的 `/m/`、`/t/` 照 E9 取（`list` 的參照）。
- **發起的電腦**（`SyncEngine.Peer.cs`）：只在目前這條連線交握完成、協商出 `peer1` 時呼叫 `PeerCmd`；沒有這種連線時不排隊，立刻交回一則合成的 `commandResult {state: "rejected", code: "not_sent", origin: "transport"}`（同瀏覽器 transport-hub 的合成）；伺服器回 `accepted: false` 時合成 rejected＋那個錯誤碼；呼叫因斷線失敗時不合成（伺服器可能已經處理）。`PeerReply` 只檢查 `commandId` 是 ULID、`message` 有非空字串的 `type`。交握完成、連線丟掉時回報「能不能送」（web-protocol 的 `sync.peer`）。

### 14.8 各電腦的即時狀態（`stat1`；2026-10-22）

對話視窗右邊的「電腦狀態」面板（web-protocol 的 `monitor`、`deviceStats`）：每台在線的電腦把這一刻的 CPU、記憶體、CPU 溫度與顯示卡、磁碟、網路流量與和伺服器的往返時間交給伺服器，伺服器連同 MyriagentHub 主機自己的負載轉給在看的電腦與瀏覽器。**給人看的即時資料**：不寫任何持久事件、不進 sync.db、不稽核，伺服器只放在記憶體（重啟就沒了，裝置 2～10 秒內會再送），可以合併、可以丟。型別在 `Sync/DeviceStatsMessages.cs`，規則與上限在 `Sync/DeviceStatsRules.cs`。

| 方向 | 方法 | 送法 | 內容 |
|---|---|---|---|
| 裝置→伺服器 | `StatsPublish` | Invoke | `{stats, watch}` → `StatsPublishResult {watched}`：`stats` 是 `DeviceStats`（下表），`watch`＝這台有視窗開著面板（要收 `StatsUpdate`）；`watched`＝現在有別人在看（其他電腦最後一次送的 `watch` 是 true，或有瀏覽器連線開著面板） |
| 伺服器→裝置 | `StatsUpdate` | Send | `{devices: [{deviceId, seq, ageMs, stats}], hub?}`：在線電腦的最新狀態，整份取代、依 `deviceId` 排序、最多 256 台（含收件的這台自己，裝置略過）；`hub` 是 MyriagentHub 主機的負載 `{name, seq, ageMs, stats, devices, browsers}`（還沒取樣時省略）；只送給最後一次 `watch` 是 true 的連線 |

`DeviceStats`（值是 null 的欄位在線上省略）：

| 欄位 | 內容 |
|---|---|
| `cpu` | 全部核心的平均使用率 0～100（一位小數；兩次取樣之間的平均） |
| `memUsed`、`memTotal` | 實體記憶體的已用、總量（位元組；`memTotal` > 0、`memUsed` ≤ `memTotal`） |
| `disks` | 固定磁碟 `[{name, used, total}]`，最多 8 個；`name` 是磁碟機代號（`C:`，1～16 字、`LabelRules.IsClean`）；網路磁碟、光碟、隨身碟不列 |
| `netRx`、`netTx` | 實體乙太網路與無線網路（含行動網路）合計的每秒接收、送出位元組；虛擬交換器、VPN、虛擬機的網卡不算（重複計算） |
| `rttMs` | 這台和伺服器上一次 `StatsPublish` 的往返時間（毫秒，0～600000）；沒量過省略 |
| `cores`、`cpuName`、`uptimeSec` | 邏輯處理器數（1～4096）、CPU 名稱（≤80 字、`LabelRules.IsClean`）、開機到現在的秒數；拿不到省略，只供顯示 |
| `gpus` | 顯示卡 `[{name, util, memUsed?, memTotal?}]`，最多 8 張（2026-10-23）：`name` ≤80 字、`LabelRules.IsClean`（可重複）；`util` 是使用率 0～100（一位小數；兩次取樣之間各引擎〔3D、複製、視訊解碼…〕中最忙的那個，同工作管理員）；`memUsed`／`memTotal` 是專用記憶體（位元組，兩個都有或都省略，`memTotal` > 0、`memUsed` ≤ `memTotal`）。拿不到（伺服器主機、沒有 GPU 計數器）省略 |
| `cpuTemp`、`cpuTempSource` | CPU 溫度（攝氏，-50～150；2026-10-23）與來源（≤16 字、`LabelRules.IsClean`；目前是 `hwinfo`、`lhm`、`ohm`、`acpi`，不認得的照收）；讀不到省略 |

- **裝置**（`SyncEngine.Stats.cs`）：Hello 有 `IDeviceStatsPort`（正式的協調者一律有）才宣告 `stat1`。協商出 `stat1` 的連線交握完成（只更新的連線除外）就送一次，之後每次收到回應才排下一次：伺服器回 `watched` 或這台自己開著面板時 2 秒、否則 10 秒（`DeviceStatsRules.PublishInterval`）；面板剛打開時立刻送一次（`watch` 變 true，伺服器下一份就送過來）。同一時間只有一個呼叫在途。往返時間是這次 Invoke 從送出到回應的經過時間（同一條連線的呼叫依序處理，排在批次或拉取後面時會偏高），下一次送出時帶上、也交給中樞顯示在這台的卡片上。伺服器回 `invalid` 只記錄、不重連（可丟的資料），照 10 秒再試；其他失敗照連線錯誤處理。收到的 `StatsUpdate` 以 `DeviceStatsRules.CheckUpdate` 檢查，不合格整則略過；連線丟掉時交給中樞一份空的、往返時間清掉。CPU 與網路是兩次取樣之間的平均：面板開著時每秒取樣，送出時用不超過 1.5 秒的那一份；面板沒開時送出當下取樣（距上一次送出的平均）。
- **顯示卡與 CPU 溫度**（2026-10-23；桌面的 `SystemStatus/GpuReader.cs`、`CpuTemperatureReader.cs`，解析規則在 Protocol 的 `DeviceSensorRules`）：一律只讀、不需要管理員、不裝驅動程式。顯示卡讀效能計數器「GPU Engine」「GPU Adapter Memory」（英文路徑，不受語系影響；同一個引擎各程序相加、一張卡取最忙的引擎），名稱與專用記憶體總量以 DXGI 列舉（依 LUID 對上，軟體轉譯器與 Microsoft 的基本顯示卡不列）；第一次取樣只收基準、不帶 `gpus`。真正的 CPU 溫度要核心驅動程式才讀得到，所以只讀已經在跑的監控工具，依序：HWiNFO 的共享記憶體（`Global\HWiNFO_SENS_SM2`，要開「Shared Memory Support」；免費版每 12 小時要重新開）、LibreHardwareMonitor／OpenHardwareMonitor 的 WMI（`root\LibreHardwareMonitor`、`root\OpenHardwareMonitor` 的 `Sensor`）、ACPI 熱區（效能計數器 Thermal Zone Information；筆電常有，桌機常沒有或是固定值）。讀不到的來源隔 10 秒～1 分鐘才再試，優先的來源恢復就換回去。新欄位都是選用的：舊伺服器、舊桌面照 `MissingMemberHandling.Ignore` 略過。
- **MyriagentHub 主機**（`HubStatsEntry`；`HubStatsSampler`）：伺服器以同一個取樣核心（Protocol 的 `DeviceStatsSampler`；CPU 與記憶體的 Win32 讀值在 `HubStatsSampler`，只讀、不改主機的任何設定）取樣自己，**只在有人在看時**（任何連線的 `watch` 是 true，或有瀏覽器開著面板）每 2 秒一次，沒人看時不取樣。`name` 是主機的電腦名稱（`LabelRules` 清理、≤64 字），`stats.rttMs` 一律省略，`devices` 是在線的電腦數（同一台的新舊連線算一台）、`browsers` 是連著的瀏覽器連線數（取樣當時的）。裝置以 `CheckUpdate` 一併檢查。
- **伺服器**（`DeviceHub.StatsPublish` → `DeviceStatsBoard`、`DeviceStatsFanout`）：連線已證明、協商出 `stat1`，`stats` 通過 `DeviceStatsRules.Check`，否則回 `HubException("invalid")`。看板以連線為鍵，同一條連線兩次至少隔 1 秒（更密的不收、照樣記 `watch` 並回 `watched`）；收下時配新的 `seq`（全域遞增），連線斷了（`ConnectionRegistry.ConnectionRemoved`）或 35 秒沒送就拿掉。轉送迴圈在看板改變或主機重新取樣後最多每秒發布一份（同一台有新舊兩條連線時取最後收下的那條），送 `StatsUpdate` 給在看、協商出 `stat1`、還沒收到這一份的已證明連線，送不出去只記錄；`ageMs` 是伺服器收下到發布的經過時間（單調時鐘），不比較任何電腦的時鐘。瀏覽器那一側由閘道送 `deviceStats`（web-protocol 第六節）。
- **相容**：沒宣告 `stat1` 的桌面不送也不收；沒有 `stat1` 的伺服器（回滾）協商不出，桌面的面板只顯示這台（往返時間是空的、沒有主機卡片）。`stat1` 不影響任何持久事件與其他功能。

### 14.9 各帳戶的 token 計數（`tok1`；2026-10-23）

瀏覽器版的帳戶卡顯示「今日 token」（web-protocol 的 `Tokens`，`scope:"all"`）：每台桌面把 app 經手的對話用掉的 token（中樞的 `TokenTallyStore`，依本機帳戶與本機日期累加；只記 CLI 回報的真實用量）依**目錄裡這台的連結**換成邏輯帳戶上傳，伺服器每台、每個帳戶、每天存一列，瀏覽器看到的是各電腦的合計。**只供顯示**：不寫持久事件、不稽核、不影響任何判斷；Claude／Codex 的憑證、對話內容都不經過它。

| 方向 | 方法 | 送法 | 內容 |
|---|---|---|---|
| 裝置→伺服器 | `TokensPublish` | Invoke | `{full, rows: [{logicalAccountId, day, input, cached, output}]}` → 無：`full`＝這是這台目前的全部（伺服器先刪掉這台的所有列再寫入），否則只有新的與變了的格（逐格取代）；`day` 是這台的本機日期 `yyyy-MM-dd`；`input` 含快取、`cached` 是其中的快取讀取（同 web-protocol 的 TokenCount） |

- **裝置**（`SyncEngine.Tokens.cs`，規則在 Protocol 的 `TokenTallySyncRules`）：Hello 有 `ITokenTallyPort`（正式的協調者一律有）才宣告 `tok1`。協商出 `tok1` 的連線交握完成（只更新的連線除外）就送一次整份；之後中樞的計數變了（`ISyncBridge.OnTokensChanged`，跟網頁的 `tokens` 一起每 50ms 合併）或拉到目錄的變動（連結換了）就重算，沒有通知時每 60 秒重算一次；同一條連線兩次送出至少隔 5 秒，同一時間只有一個呼叫在途。重算（`ToRows`）：沒有連結的本機帳戶略過、同一格加總、全是 0 的格不送、只留 30 天內的（比今天晚的照留），最多 3000 格（留日期新的）。送什麼（`Plan`）：這條連線還沒送過（或上一次被拒）、上次有的格這次沒有、或任何數字變小 → 整份；否則只送新的與變了的格；都沒變就不送。伺服器回 `invalid` 只記錄（每條連線一次）、不重連，下一次送整份；其他失敗照連線錯誤處理。連線丟掉時清掉，下一條連線從整份開始。計數檔在中樞的 UI 執行緒寫、引擎以 `TokenTallyStore.Snapshot` 取複本（同一把鎖）。
- **伺服器**（`DeviceHub.TokensPublish` → `TokenTallies`）：連線已證明、協商出 `tok1`、內容通過 `TokenTallySyncRules.Check`（`logicalAccountId` 格式、`day` 是真的日期、數字 0～10^15、同一格不重複、最多 3000 格），否則回 `HubException("invalid")`；帳戶不在目錄裡的格照收（只是不顯示）。寫進 sync.db 結構 v5 的 `token_tallies`（`full` 先刪這台的所有列；`updated_at` 是伺服器收到的時間），同一個交易刪掉所有裝置比伺服器的本機日期往回 30 天還早的列，提交之後瀏覽器閘道只重讀 `tokens`（`BrowserParts.Tokens`）。撤銷裝置、刪除帳戶時不刪列，讀的一方略過。
- **瀏覽器**（`AccountViewBuilder` → `TokenTallySyncRules.View`）：`today` 是**伺服器的本機日期**那一天、`week`／`month` 是往回 7／30 天（含今天），各台的格直接相加（各電腦的本機日期與伺服器不同時區時會錯開，自用的電腦與 Hub 主機都在同一個時區）；只列 30 天內有任何一格的帳戶，略過已撤銷的裝置與已刪除的帳戶，帳戶依 id 排序。init 帶 `tokens`，變了才送 `tokens` 訊息（整份取代）；跨日由閘道的保底重讀（每 `RefreshSeconds`）帶到。
- **相容**：沒宣告 `tok1` 的桌面（更新前的版本）不送，它用掉的 token 瀏覽器看不到——帳戶只有舊版的電腦在用時不列，網頁照舊顯示方案名稱。沒有 `tok1` 的伺服器（回滾）協商不出，桌面不送；`token_tallies` 留在 sync.db，舊版不碰它（結構 v5 只加這張表，回滾的舊組建開到時只記一則「比程式新」的警告）。`tok1` 不影響任何持久事件與其他功能。

### 14.10 其他電腦的 token 計數（`tok2`；2026-10-23）

桌面側欄的帳戶卡也顯示所有電腦的合計（web-protocol 的 Tokens〔同步〕的 `here`）：伺服器把 14.9 存的格扣掉收件那台自己的、加總之後推給桌面，桌面加上這台即時的計數（這台的部分不經伺服器繞一圈，數字照舊即時）。**只供顯示**，同 14.9。

| 方向 | 方法 | 送法 | 內容 |
|---|---|---|---|
| 伺服器→裝置 | `TokensUpdate` | Send | `{rows: [{logicalAccountId, day, input, cached, output}]}`：收件這台以外、沒撤銷的電腦在沒刪除的帳戶、保留天數內（伺服器的本機日期往回 30 天）每一格的合計（同一個邏輯帳戶、同一天的各台相加，每個數字不超過 10^15），全是 0 的格不送；依（日期新到舊、帳戶）排序、最多 3000 格；整份取代 |

- **伺服器**（`TokenTallyFanout`，規則是 Protocol 的 `TokenTallySyncRules.Others`）：連線證明完成（`ConnectionRegistry.ConnectionProven`）、協商出 `tok2`、不是只更新的連線，就列進送件名單，**證明完成 5 秒後**（`FirstSendDelay`）送第一份——ConnectionProven 時 ProveResponse 還沒到裝置，0.2.621～0.2.624 的桌面在交握完成前收到 `TokensUpdate` 會丟例外、斷線重連（每次 app 啟動都斷一次，2026-10-23 修正）；`token_tallies` 提交之後（`TokenTallies.Changed`）重讀，兩次重讀至少隔 2 秒（`FanoutInterval`；期間的變動併進同一次），沒有變動時每 60 秒重讀一次（撤銷裝置、刪除帳戶不經 Changed）。每次在一個 deferred 唯讀交易裡讀一次所有的格，每條連線各算它以外的合計，序列化後和這條連線上次收到的相同就不送；送不出去只記錄、下一次重讀再送。連線斷了就從名單拿掉。不寫任何東西、不稽核。
- **裝置**（`SyncEngine.Tokens.cs`）：Hello 有 `ITokenTallyViewPort`（正式的協調者一律有）才宣告 `tok2`。交握完成前到的 `TokensUpdate` 進早到緩衝（`SyncEarlyMessages`，同 `StatsUpdate` 只留最新的一則；`PeerReply` 也收，算其他類），交握完成後照常處理。協商出 `tok2` 的連線收到的 `TokensUpdate` 以 `TokenTallySyncRules.CheckUpdate`（同 14.9 伺服器的格式與上限）檢查，不合格整則略過、只記錄；合格的整份交給協調者，回 UI 執行緒存下並讓中樞重送 `tokens`（`AgentHub.RefreshTokens`：只重送給網頁，不排這台的上傳）。連線丟掉時**不清**：計數只會變大，最後收到的仍是對的下限；解除配對時清掉。
- **合計**（`TokenViewRules.Decorate`，中樞組 `tokens` 時經 `ISyncBridge.DecorateTokens`）：連結到帳戶目錄的本機帳戶，today／week／month 加上其他電腦這個邏輯帳戶的格（天數依**這台的本機日期**；比今天晚的格不算），並帶 `here`＝這台今天的；沒連結的照舊只有這台。remoteOnly 的邏輯帳戶（目錄有、這台沒有本機帳戶）在其他電腦有格時以邏輯帳戶 id 加一筆（`here` 是 0）。目錄的連結變了（`RefreshSyncDecorations`）也重送 `tokens`。
- **相容**：沒宣告 `tok2` 的桌面（更新前的版本）照舊只顯示這台；沒有 `tok2` 的伺服器（回滾）協商不出，桌面沒有 `here`、只顯示這台。舊伺服器不送 `TokensUpdate`，舊桌面沒有登記這個方法（SignalR 收到沒登記的方法只記警告）——伺服器只送給協商出 `tok2` 的連線。`tok2` 不影響任何持久事件與其他功能。
- **對話數**（同一時間改的，不經 `tok2`）：桌面帳戶卡的執行中、等你回應（2026-10-24 起另有等背景工作回報的 `background`）加上目錄裡其他在線電腦的 `link.upsert` 的 `sessions`（web-protocol 的 `otherSessions`，`SyncViewRules.OtherSessions`）；瀏覽器版的 `sessions` 合計同樣只算在線的電腦（`AccountViewRules.Account`），離線的那台最後回報的數字停在當時，不算。
- **背景程式數**（2026-10-24）：`link.upsert` 的 `sessions`（`LinkSessions`）在 `background` 之後再加選用的 `programs`——這一輪結束了、但代理開的程式脫離 CLI 還在跑的對話數（桌面的 AccountView `sessions.programs`，web-protocol；和 `background` 的「等背景工作回報」分開，CLI 底下的程序不算）。0 的時候是 null、線上省略：沒有背景程式的連結和以前逐位元組相同，摘要不變、不必重送；伺服器檢查同其他對話數（0～100000，`CatalogRules.ValidateLink`），存進 `account_links.sessions_json`、發布在 `account.upsert` 的 links 裡；瀏覽器的 `sessions.programs` 是在線電腦的合計，桌面的 `otherSessions.programs` 是其他在線電腦的合計。舊版（伺服器回滾、更新前的桌面）照 `MissingMemberHandling.Ignore` 略過這個欄位，舊伺服器存回去時丟掉它（只是看不到）。
- **子代理數**（2026-10-28）：`LinkSessions` 在 `programs` 之後再加選用的 `subagents`——執行中的子代理個數（桌面的 AccountView `sessions.subagents`，web-protocol）。規則同 `programs`：0 的時候是 null、線上省略，伺服器檢查同其他對話數、存進 `account_links.sessions_json`，瀏覽器的 `sessions.subagents` 是在線電腦的合計、桌面的 `otherSessions.subagents` 是其他在線電腦的合計；舊版忽略。

### 14.11 遠端執行指令（`exec1`；2026-10-25）

擁有者 2026-10-25 要求：已配對的電腦之間由**對話裡的代理**互相執行主控台指令，方便管理所有配對的電腦；高風險（例如要 UAC）的操作仍要到那台電腦同意。擁有者以問卷決定：只做「單次指令＋即時輸出」（不做互動終端機）、只給對話裡的代理用（`myriagent-remote` 指令列工具；不從瀏覽器或桌面畫面發起）、**不開放 Hub 主機**（伺服器只轉送）、高風險＝UAC＋本機確認。計畫見 `docs/plans/2026-10-25-remote-exec.md`。

`remoteExec` 照一般的遠端指令走（14.7 的 `PeerCmd {commandId, cmd: {cmd: "remoteExec", deviceId, shell, command, cwd?, timeoutSec?, label?}}` → 型錄、形狀、層級 → 伺服器身分金鑰簽信封 → 目標電腦驗章、`commands_seen` 至多一次、遠端操作紀錄、閘門 5a～5j）：`CommandResult` 的 `done` 只表示那台接下了，輸出、狀態與結束碼另走本節的串流（下表）。

| 方向 | 方法 | 送法 | 內容 |
|---|---|---|---|
| 裝置→伺服器 | `ExecOutput` | Invoke | `ExecChunk`（被操作的電腦送；`commandId` 是信封的 cid）；回 `{keep}` |
| 伺服器→裝置 | `ExecReply` | Send | `ExecChunk`（轉給發起的電腦；`commandId` 換成 `PeerCmd` 的 commandId） |
| 裝置→伺服器 | `ExecCancel` | Invoke | `{commandId}`（發起的電腦要停止它下的）；回 `{found}` |
| 伺服器→裝置 | `ExecAbort` | Send | `{commandId}`（給被操作的電腦：停止這個 cid） |

- **型錄**（`RemoteCommandCatalog.RemoteExec`，`NonWebCommands`）：路由 Device、層級 2、不 durable；欄位 `deviceId`（必帶）、`shell`（`powershell`｜`pwsh`｜`cmd`）、`command`（≤8000 字：cmd 的命令列上限）、`cwd`（選用、可 null）、`timeoutSec`（選用，1～86400 的 JSON 整數，`RemoteFieldKind.Seconds`；沒給是 600）、`label`（選用，發起的對話標題，≤120 字，給那台的人看）。不是網頁指令：瀏覽器的 `MessageGuard` 不認得（`invalid`），`CommandRouter.RouteAsync` 對瀏覽器發起的另外回 `not_available`。
- **功能 `exec1`**：`ExecOutput`、`ExecCancel`、`ExecReply`、`ExecAbort` 都要協商出它（`DeviceFeatureRules.RequiredFor`）。伺服器在 `AddHubCommands` 的 `peer1` 之後登記；桌面在 `peer1` 之後、宿主接得住（`IRemoteExecPort.ExecSupported`）時宣告。伺服器路由 `remoteExec` 時：選到的目標連線沒有 `exec1` 立即拒絕 `unsupported`「〈電腦〉的 Clode Myriagent 版本太舊，還不能執行遠端指令」（`RemoteExecServerRules.TargetTooOldMessage`），發起的這條連線沒有 `exec1` 回 `not_available`；目標是發起的電腦自己同 14.7 立即拒絕 `invalid`。
- **`ExecChunk {commandId, seq, kind, text?, exitCode?, code?}`**（`RemoteExecRules.IsValidChunk`）：`seq` 從 0 起每段加 1（收的一方以它看出缺漏、丟掉重複）；`kind` 是 `status`（必帶 `code`：`confirm` 等待那台的人同意、`started` 已開始、`truncated` 之後的輸出不再轉送）、`out`／`err`（`text` 非空）、`exit`（最後一段：`exitCode` 是程式的結束碼，沒跑起來是 null；`code` 是沒有正常結束的原因 `timeout`｜`cancelled`｜`denied`｜`failed`｜`orphaned`｜`app_exit`，正常結束省略；`text` 是給人看的說明）。`text` ≤32768 字、不能有不成對的代理字元；一個指令轉送的輸出最多 8 M 字（`MaxOutputChars`），之後送一段 `truncated`、程式照樣跑到結束。
- **伺服器**（`Sync/ExecRelays.cs`，只在記憶體、最多 1024 筆）：`CommandRouter` 簽好 `remoteExec`、送出之前登記 cid → 發起的電腦、它的 commandId、目標電腦，期限是指令的上限＋5 分鐘（`RelayGrace`：等待同意 3 分鐘＋路上）。`ExecOutput` 只收登記的目標電腦、已證明、協商出 `exec1` 的連線送的；換成發起時的 commandId，以 `ExecReply` 送給發起電腦 `Order` 最大、已證明、有 `exec1` 的連線；`exit` 轉送之後忘掉。回 `keep: false`（被操作的電腦要停止指令）的情況：不認得的 cid（伺服器重啟、已結束、過期）、發起的電腦連續 2 分鐘（`OriginLostLimit`）沒有能收的連線。格式不合格的段落丟掉、回 `keep: true`。`ExecCancel` 只認發起的電腦自己下、還在登記的，轉成 `ExecAbort` 給目標每條有 `exec1` 的連線。不保存任何輸出。
- **被操作的電腦**（`Hub/AgentHub.RemoteExec.cs`、`RemoteExec/RemoteExecRunner.cs`）：閘門 5e 照舊要 `Control`（暫停遠端操作等於 `Off`）；處理者另外要這台開了 `SyncSettings.AllowRemoteExec`（設定頁「允許其他電腦的代理在這台執行指令」，預設關、只能本機改；沒開回 `policy`），再過執行者的檢查——**app 以系統管理員執行（權杖裡的 Administrators 是啟用的，含 UAC 關掉的管理員帳戶）一律 `policy`**：指令會有完整的系統管理員權限、不經 UAC；工作資料夾要是這台的絕對路徑、存在、不是 UNC（`invalid`）；`pwsh` 沒裝 `invalid`；同時最多 8 個（`busy`）。通過就回 `done`，之後：
  1. `RemoteExecRiskRules.Classify` 判成高風險（關機與結束系統程序、格式化與磁碟分割、開機與復原設定、要求或繞過系統管理員權限〔含常見的免 UAC 提權手法〕、系統範圍的登錄檔、刪除系統資料夾／使用者資料夾的根／整顆磁碟、帳戶與檔案權限、防毒防火牆與安全設定、服務與驅動程式、移除已安裝的程式、看不到內容的指令）的先送 `status confirm`、在這台跳出置頂的確認視窗（預設焦點在「拒絕」），3 分鐘內按「允許執行」才執行；拒絕、逾時、關掉視窗是 `exit denied`。這是給代理誤操作的安全網，不是安全邊界（腳本檔、組字串都繞得過）；真正的邊界是一般權限＋UAC。
  2. 以 app 的身分（一般權限）在一個 KILL_ON_JOB_CLOSE 的工作物件裡執行：PowerShell 寫成暫存 .ps1（UTF-8 含 BOM）以 `-NoProfile -NonInteractive -ExecutionPolicy Bypass -File` 執行，開頭把輸出編碼改成 UTF-8，最後一個指令失敗時結束碼是原生程式的結束碼或 1；cmd 是 `/d /s /c "chcp 65001>nul & 指令"`。不給輸入；環境變數拿掉 `MYRIAGENT_REMOTE*` 與 `MYRIAGENT_AGENT_CONVERSATION`。需要系統管理員的操作由 Windows 在這台的桌面跳 UAC。
  3. 輸出以專用執行緒讀、UTF-8 解（不合格就改用主控台字碼頁），每 100 毫秒合併成一段；逾時、`ExecAbort`（`cancelled`）、`keep: false`（`orphaned`）、app 結束（`app_exit`）都結束整棵程序樹。
  - 引擎（`SyncEngine.Exec.cs`）依序一則一則送 `ExecOutput`（等回應才送下一則）；斷線時沒送出的輸出丟掉，結束碼留到下一條有 `exec1` 的連線補送（最多 64 則）。有遠端執行指令在跑時 app 自我更新不自動重啟（`AppRestartBlocker.RemoteExecRunning`）。完成時照 14.6 的遠端操作通知（「電腦「X」 執行了主控台指令『…』」）。
- **發起的電腦**（`RemoteExec/RemoteExecService.cs`、`src/MyriagentRemote`）：對話的 CLI 經環境變數拿到本機具名管道（`MYRIAGENT_REMOTE_PIPE`，名稱取資料根目錄的雜湊，`PipeOptions.CurrentUserOnly`）、這個對話的權杖（`MYRIAGENT_REMOTE_TOKEN`＝HMAC(app 這次執行的亂數, 對話 id)）與 `myriagent-remote.exe` 所在的資料夾（放到 `PATH` 最前面）；已配對時給代理的說明多一段用法。`myriagent-remote list` 列出電腦（`device.upsert` 的在線、`features` 有沒有 `exec1`、`policy.allowRemoteExec`）；`myriagent-remote run <電腦> [--shell] [--cwd] [--timeout] [--stdin|-- 指令]` 先在本機擋掉送不出去的（這台、離線、版本太舊、那台沒開放、這台沒連上），再送 `PeerCmd`，把結果與段落一行一行寫回；myriagent-remote 送 cancel（Ctrl+C）或中途結束（代理的工具逾時被砍）時送 `ExecCancel`。myriagent-remote 的結束碼是那台程式的結束碼；124 逾時、125 沒有送出或被拒、126 那台的人拒絕、130 已停止、255 結果不明。
- **政策回報**：`DevicePolicy` 最後加選用的 `allowRemoteExec`：層級是 `Control` 而且開著時是 true，其他省略（舊版與黃金檔不變；伺服器存、轉發，不判斷）。桌面的 `sync.local.allowRemoteExec` 與 `devices[].allowRemoteExec` 見 web-protocol。

---

### 14.12 各電腦的排程（`sched1`；2026-10-26）

排程頁的 S3（設計 `docs/plans/2026-10-26-schedule-manager.md` 第 4 節）：每台把自己排程頁的摘要上傳，伺服器存最後一份，給瀏覽器版與其他桌面的排程頁；做法同 `tok1`／`tok2`（14.9、14.10）。只供顯示：不寫持久事件、伺服器不解讀內容。

| 方向 | 方法 | 送法 | 內容 |
|---|---|---|---|
| 裝置→伺服器 | `SchedulesPublish` | Invoke | `{snapshot}` → 無：這台排程頁的摘要（整份取代） |
| 伺服器→裝置 | `SchedulesUpdate` | Send | `{devices: [{deviceId, updatedAt, snapshot}]}`：那台以外的電腦最後回報的 |

- **`SchedulesPublish`**（裝置 → 伺服器，Invoke）：`{ snapshot }`，這台排程頁的摘要整份取代（`ScheduleSyncRules.ForSync`）。摘要只留代理建立、納入管理與疑似代理建立的排程（系統、廠商、不再列出的只送數量 `otherCount`）與 Myriagent 的定時訊息；動作參數上傳前遮蔽秘密（`ScheduleRedactRules`：權杖、密碼、`key=…`、網址裡的帳密、像金鑰的長字串），不帶完整 XML、作者、描述、本機的出處路徑；定時訊息的文字截 300 字並遮蔽；每個排程最近 14 次的結果、接下來 24 小時最多 48 次。伺服器檢查（`ScheduleSyncRules.Check`）：序列化後 ≤256 KB、`entries` ≤300、`timed` ≤200、都要是物件陣列；不合格、連線沒協商出 `sched1` 回 `invalid`（裝置只記錄、不重連）。伺服器寫進 sync.db v7 的 `device_schedules`（一台一列，`updated_at` 是伺服器收到的時間；撤銷裝置時不刪，讀的一方略過）。
- **桌面何時送**（`SyncEngine.Schedules.cs`）：協商出 `sched1` 的連線交握完成就送一次；中樞讀完排程、內容不同時（`ISyncBridge.OnSchedulesChanged`）再送，兩次至少隔 2 秒；內容和這條連線上次送成功的一樣不送。中樞沒掛排程讀取器時不宣告 `sched1`。
- **`SchedulesUpdate`**（伺服器 → 裝置，Send）：`{ devices: [{ deviceId, updatedAt, snapshot }] }`，那台以外、沒撤銷的電腦最後回報的（照 deviceId 排序、最多 64 台）。`ScheduleFanout` 同 `TokenTallyFanout`：連線證明完成 5 秒後送第一份、`device_schedules` 有變時重讀（最多每 2 秒）、沒變時每 60 秒重讀，內容和這條連線上次收到的一樣不送。桌面檢查格式（`CheckUpdate`）後整份交給中樞，排程頁的 `devices`（web-protocol 的 ScheduleView）。
- **瀏覽器**：`init` 與 `schedules` 訊息的 ScheduleView 沒有這台（`entries`、`timed` 是空的），各台在 `devices`；`BrowserParts.Schedules`，`device_schedules` 有變時重讀。名稱與在不在線由網頁照 `devices` 對（離線的顯示最後回報的）。
- **遠端指令**（型錄的 Device 指令，照 14.4 的信封、至多執行一次、遠端操作紀錄）：`scheduleAction {deviceId, path, action: run|stop|enable|disable}`（層級 2）、`scheduleAgent {deviceId, path, task: diagnose|explain|retime|retire|custom, note?, accountId?}`（層級 1）。目標的政策要「可以操作」（擁有者 2026-10-26 決定不另開開關）；不再列出、納入管理、個別的自動診斷是那台自己的選擇，遠端不改（`action` 只收這四個）。`scheduleAgent` 在那台這個排程的管理對話送出說明（沒有就以 `accountId`〔沒給時那台的預設〕開一個；遠端看不到那台的草稿，所以直接送，執行中排進佇列），附上 `note`（「補充（發起的電腦或瀏覽器）」）；結果的 `done` 表示送出了，管理對話的連結隨那台下一次的摘要出現。找不到排程回 `conflict`、那台沒有排程頁回 `unsupported`。
- Hub 主機不是電腦（沒有 deviceId），沒有自己的排程列；伺服器只存與轉送。

### 14.14 多人協作（`collab1`；2026-10-30）

擁有者建立協作人帳號（帳號密碼，存在伺服器的 hub.db）、把對話分享給協作人；協作人只用網頁版（`/app/`）登入，只看得到、只能操作分享給他的對話（設計與決定見 `docs/plans/2026-10-30-collaborators.md`）。伺服器是協作人與分享的權威；擁有者電腦照舊是對話的單一寫入者，閘門再擋一次。

| 方向 | 方法 | 送法 | 內容 |
|---|---|---|---|
| 裝置→伺服器 | `CollabCmd` | Invoke | `CollabCmdRequest {cmd, userId?, userName?, displayName?, password?, disabled?, conversationId?, userIds?}` → `CollabCmdResult {ok, code?, userId?}`（`cmd` 是 `collabCreate`／`collabUpdate`／`collabDelete`／`collabShare`，欄位同 web-protocol 的 `collab*`；`conversationId` 是線上的對話 id） |
| 伺服器→裝置 | `CollabUpdate` | Send | `CollaboratorsState {list:[{id, userName, displayName, disabled, createdAt, lastLoginAt, online}], shares:{對話 id:[協作人 id…]}}`，整份取代 |

- **信封的發起者**：協作人的瀏覽器發起的遠端指令，伺服器簽 `issuer = {kind:"collaborator", label: 協作人的顯示名稱}`（≤32 字，照第 9 步的標籤規則；`auth` 同擁有者的瀏覽器）。電腦只在收到它的連線協商出 `collab1` 時接受這個種類（`CommandEnvelopeTarget.AcceptsCollaboratorIssuer`），舊版的電腦在第 9 步拒絕——伺服器在簽之前就看目標連線有沒有 `collab1`，沒有的立即拒絕 `unsupported`（「那台電腦的 Clode Myriagent 版本太舊，還不能讓協作人操作」）。內容摘要照舊包含 `issuer`。
- **協作人能送的指令**（`CollaboratorRules.RemoteCommands`，伺服器簽之前與電腦的閘門 5a 之後各查一次）：路由 Owner 的 `send`、`voiceMessage`、`cancelQueued`、`stop`、`answer`、`skipQuestion`、`approve`、`approvePlan`、`addUploaded`、`removeAttachment`、`listFiles`、`fileSearch`、`fileFetch`。其他（`setOption`、`setAccount`、`setFolder`、`rename`、`delete`、路由 Device 的一切）電腦回 `policy`（`step` 是 `route`）。伺服器另外只收分享給他的對話（瀏覽器閘道）。
- **項目的「誰」**：電腦把協作人的標籤記在使用者訊息的 `Author`（送出、插入這一輪、語音訊息；排進佇列的經 `RemoteOrigin.Author` 存在 queue.json）、回應的卡片的 `ResolvedBy`；`conv.item` 的 payload（`WireItem`）最後加選用的 `author`、`resolvedBy`（擁有者的省略；`CollaboratorRules.CleanAuthor` 清掉空的與不合格的）。舊版伺服器照收（不認得的鍵照留在原始 payload）；舊版的電腦讀副本時略過，`live2` 的 `head` 有這兩個鍵時舊版回 `LiveResync`（多一次整則重送）。
- **伺服器端**：`CollaboratorDirectory`（hub.db 的 `collaborators`、`collaborator_shares`、`browser_sessions.user_id`；Identity 的 AspNetUsers 放帳號密碼）、`CollaboratorCommandService`（瀏覽器版的 `collab*` 與 `CollabCmd` 共用）、`CollaboratorFanout`（連線證明完成時送一份，名冊或協作人的「在線」改變時重送，內容相同不送，最多每秒一次）。`CollabCmd` 只收已證明、協商出 `collab1`、不是只更新的連線（已配對就是擁有者：擁有者 2026-10-22「已配對就能操作」），稽核的 actor 是 `device`。
- **桌面端**：`SyncEngine.Collaborators.cs`（`CollabUpdate` 交給協調者、`CollabCmdAsync` 經目前的連線 Invoke；沒有連線或沒有 `collab1` 回 `unavailable`）、`AgentHub.Collaborators.cs`（web-protocol 的 `collaborators`、`collab*`、`collabResult`；`shares` 的鍵換成這台的對話 id）。解除配對時清掉。


### 14.15 電腦狀態面板的檔案傳輸（`xfer1`；2026-11-09）

擁有者 2026-11-09 要求：電腦狀態面板右鍵點一台電腦，瀏覽那台整台本機磁碟、多選上傳與下載。把關沿用遠端操作政策「可以操作」、範圍是整台本機磁碟、同名自動改名。

- **功能**：桌面跟著 `cmd1` 宣告 `xfer1`（`SyncEngine.Commands.cs`），伺服器也宣告（`HubApplication`）。伺服器只把 `deviceBrowse`、`deviceFetch`、`devicePut` 送給選到的連線協商出 `xfer1` 的電腦，否則立即拒絕 `unsupported`「〈電腦〉的 Clode Myriagent 版本太舊，還不能傳檔案；請先更新那台」（`CommandRouter.IssueAsync`）。不對應任何 hub 方法。
- **指令**（型錄的 Device、層級 2，形狀見 `RemoteCommandCatalog`）：`deviceBrowse {deviceId, requestId, path}` → 輸出 `files`；`deviceFetch {deviceId, requestId, path}` → 輸出 `fileRef`；`devicePut {deviceId, requestId, folder, uploads}`（`uploads` 照 `addUploaded`，伺服器簽之前以 `UploadRegistry.TryVerify` 驗）。輸出的 `conversationId` 一律是 null（`CommandOutputFileRules.Header` 只核對 `requestId`）。電腦的閘門照 14.6（5e 只收 `Control`），處理者（`AgentHub.DeviceFiles.cs`）另外在 app 以系統管理員執行時回 `policy`；範圍是整台的固定與卸除式磁碟（`DeviceTransferScope`），禁止清單是 `RemoteScopeBuilder.DeviceFoldersAsync` 的那一份加上 `DeviceTransferRules.ExtraForbiddenFolders`。這三個指令不跳本機通知（`RemoteNotifyRules.SilentCommands`），收檔做完由處理者在那台提示一次。
- **下載參照 `xfer`**：桌面 `media_refs_local` 的 `item_id = @xfer`、`conversation_id = ""`；伺服器改寫 `fileRef` 時登記 `media_refs(purpose = 'xfer', conversation_id = '')`，`url` 是 `/f/{key}`，`expiresAt` ≤ 現在＋2 小時（`MediaLookupRules.XferLifetime`）。`/f/`（E3）與 E11 收 `file` 與 `xfer`；`xfer` 不看對話目錄（沒有對話），只看到期，協作人的連線一律 404（`CanSee` 對空的對話 id 是 false）。向電腦要位元組時 `MediaRequest.purpose` 是 `xfer`（`MediaPurposes.Transfer`），電腦的 `MediaServer` 只在這條連線的政策是 `Control` 時給（`MediaServerRules.Decide`），以傳輸的範圍開檔、依範圍串流（不做文字 DLP、沒有 20 MB 的上限）。`/f/` 沒帶 Range 的整份上限：`xfer` 4 GB（`MediaRelayRules.MaxUnrangedTransferBytes`），其他照舊。過期的列兩邊照 `@file` 的作法修剪；索引器重建後的孤兒清理不刪 `xfer`。
- **網路芳鄰**（2026-11-09 擁有者要求「遠端也要能夠看到網路芳鄰的路徑」；`Hub/NetworkPathRules.cs`、`NetworkPathGuard.cs`、`NetworkShares.cs`）：虛擬根另列對應的網路磁碟機（「Z: (\\NAS\資料)」）與「網路」（路徑 `\\`）；`\\` 列區域網路上有檔案分享的電腦（`NetworkDiscovery`，約 2.5～6 秒：對這台所在的網段〔前綴小於 /22 的只查所在的 /24、最多 1024 個位址〕逐一送 NetBIOS 節點狀態查詢、只留有 <20> 檔案分享服務的；同時試連每個位址的 445 埠〔只建 TCP 連線、不送 SMB 封包，NetBIOS 關掉的 NAS 與 Windows 也找得到〕與 WS-Discovery 群播；取不到名稱的以反查 DNS 補、再不行用位址；另加那台已經連過的分享與舊式的 WNet 列舉——舊式的列舉在現在的 Windows 多半回 6118，2026-11-09 實機只靠它什麼都找不到）；找不到時 `files` 照樣帶 `path: "\\"`、空的 `entries` 與 `error`（網頁切到「網路」這一層並提示直接輸入 `\\電腦名稱`），`\\電腦` 列它的資料夾分享（NetShareEnum，不含 C$ 這類系統管理分享與 IPC$），分享以下照本機列資料夾與檔案。存取網路路徑時 Windows 會把那台登入的 NTLM 雜湊送給對方，所以**只收區域網路的主機**（名稱解析出的位址全在 10/8、172.16/12、192.168/16、169.254/16、100.64/10、fe80::/10、fc00::/7）、**不收那台自己**（`localhost`、電腦名稱、自己的任何位址：`\\localhost\C$` 會繞過本機的禁止清單）；網路磁碟機以它對應的 UNC 判斷。不在區域網路、是自己、找不到、逾時（12 秒）在瀏覽時以 `files.error` 說明，下載與存檔回 `policy`（格式、主機）或 `conflict`／fileRef 的 error（不存在、逾時）。憑證類檔名照樣不給；網路上不做重新解析點與硬連結的檢查（Windows 預設不在用戶端解析遠端到本機的符號連結）。`@xfer` 參照照樣記完整的網路路徑，轉送時（`MediaServer`）再驗一次。
- **接收的進度**：`DeviceStats.xfers`（14.8 的表外加的選用欄位：`[{id, name, size, done, state, saved?}]`，`id` 是 uploadId、`state` 是 `receiving`／`saved`／`failed`，最多 32 筆，`DeviceStatsRules.CheckXfer`）。給人看的即時資料，同 stat1 可丟；舊伺服器、舊桌面照 `MissingMemberHandling.Ignore` 略過。
- **發起的桌面**：上傳照 14.7 的 `PUT /api/v1/device/uploads/{uploadId}`；下載以 E11（`purpose=file`）每段 ≤8 MB 的 Range 依序取（`ReplicaMediaSource.DownloadAsync`：每段核對起點、總長、ETag，503 等 2 秒重試），寫進這台的資料夾。

## 15. app 下載端點（app 自我更新 U2）

桌面以存取權杖取得最新版與套件（U3 的 `HubReleaseSource`）。路由是 `AppReleaseRoutes`（`Update/AppReleaseMessages.cs`）的常數，回應以 `SyncJson` 序列化（1.2），形狀只加選用欄位（1.4 的不變式）。

| 端點 | 回應 |
|---|---|
| `GET /api/v1/device/app/releases/latest` | 200 `LatestAppRelease`；沒有可用的版本 204 |
| `GET /api/v1/device/app/releases/{release}/package` | 200 zip（`Accept-Ranges: bytes`、`ETag: "<zipSha256>"`）；`Range` 續傳 206、超出範圍 416；撤回或已清除 410；不存在或名稱格式不對 404；伺服器排空中 503＋`Retry-After`；zip 暫時開不了（伺服器上的防毒或備份正開著）也是 503＋`Retry-After` |

```jsonc
// GET /api/v1/device/app/releases/latest → 200
{ "release": "0.2.1523-ab12cd3", "version": "0.2.1523", "build": "ab12cd3",
  "manifest": "<manifest 原文位元組的標準 base64>",
  "sig": { "kid": "<16 個小寫十六進位字>", "sig": "<P1363 簽章的 base64url>" },
  "zipBytes": 73400320 }
```

- **權杖**：只收 `Authorization: Bearer cha_…`（Prove 與 `RefreshAccessToken` 發的存取權杖），伺服器以既有的 `HubPolicies.DeviceAccess` 政策把關（WP4.1 D56；跟 WP4.7 的 blob 與媒體端點同一個政策）：裝置權杖 `chd_` 回 403，擁有者 cookie 與匿名回 401。一般連線與只更新的連線（1.4）拿到的是同一種權杖，都能用。
- **錯誤的本文**：`{"error": 代碼}`，代碼是 `not_found`（404）、`gone`（410）、`draining`（503，排空中）、`unavailable`（503，zip 暫時開不了，`Retry-After` 30）。401、403 沒有本文。裝置依狀態碼處理：503 一律照 `Retry-After` 稍後再試。
- **manifest 一律是原文位元組**：`manifest` 是打包時那份 manifest 原文的**標準** base64（不是 base64url），從打包、簽章、收件到這裡都不重新序列化。裝置以 `Convert.FromBase64String` 還原，簽章對象就是這串位元組；`sig` 以 `ReleaseManifestRules.SerializeSignature(new ReleaseSignature(kid, sig))` 組回 `.manifest.sig` 的內容（不超過 1024 字元，`TryParseSignature` 讀得回）。
- **提供哪些版本**：只有可用的（`available`）。latest 與 Hello 的 `latestApp` 是可用版本中最新的一個；撤回（`withdrawn`）或保存規則清掉檔案（`pruned`）的版本，套件回 410；它正好是 latest 時，latest 與 `latestApp` 立刻改回前一個可用版本（沒有就 204、省略 `latestApp`）。
- **裝置一律自己驗**（U3）：以 app 內建的發佈金鑰（`ReleaseKeys.Production`）驗 `sig`，版本也必須比目前的新。伺服器不送任何金鑰，也加不了信任的發佈金鑰。
- **續傳**：`Range: bytes=<已下載長度>-` 加 `If-Range: "<zipSha256>"`；`If-Range` 不符時回整個檔（200）。下載途中版本被撤回或清除，進行中的傳輸照樣傳完，之後的續傳與新下載回 410。
- **排空中**（伺服器換版）：套件回 503，`Retry-After` 是排空要求帶的秒數（沒有時 30）；latest 照常回應。收件與 `appRelease` 通知不看排空，裝置照 `Retry-After` 稍後再下載。
- **不做的**（使用者 2026-10-19 決定：自用、伺服器可信）：權杖不分範圍、不限每台的下載次數與同時下載數、不寫下載稽核。
- **通知**：收件完成時對所有已連線的裝置送 `ServerNotice{kind:"appRelease", release}`（第 4 節）；Hello 的 `latestApp` 帶最新的可用版本（第 3 節）。通知只是叫裝置去檢查。
- **桌面（U3 第一步，2026-10-22）**：`HubReleaseSource` 經同步引擎的 `DeviceHttpClient`（跟 WP4.7 的媒體共用存取權杖，401 時換一次）；沒有權杖或沒連上時不算失敗，等連上再試。latest 與套件的暫時失敗（連不上、503、410、回應不合格）照 `Retry-After`，沒有時 1、2、4…60 分鐘退避。Hello 的 `latestApp` 與 `appRelease` 通知觸發的檢查共用 10 分鐘的限流，只在版本比這台新、而且不是已備妥的那一版時檢查（app 自我更新設計第 16 節第 47～52 項）。

## 16. 第 5 階段：租約、交接與逐字稿日誌（WP5.1 契約）

第 5 階段（換機接手與離線續接）的裝置↔伺服器契約。計畫與理由在 `docs/plans/2026-10-23-phase5-handoff.md`（決定 D1～D21、擁有者的決定），實作與原設計不同之處記在設計 14.22；網頁那一側在 `docs/web-protocol.md` 的〔換機〕。**WP5.1 只立型別、規則、結構與登記點**：伺服器與桌面的兩個功能都休眠（16.1），沒有任何連線協商得出，所以本節的訊息、端點與 `conv.lease` 都不會出現；各小節寫明由哪個工作包開始用。第 1～15 節不變。

| 主題 | 程式（Protocol） |
|---|---|
| 功能 `log1`、`hand1` 與方法要的功能 | `Sync/DeviceFeatureRules.cs`、`Sync/SyncProtocol.cs` 的 `DeviceHubMethods` |
| 交接的訊息、狀態與列舉 | `Sync/HandoffMessages.cs` |
| 代碼 | `Sync/HandoffCodes.cs` |
| 失聯、HandoffStart 的檢查、狀態機、還原後的認領、期限 | `Sync/LeaseRules.cs` |
| 電腦端套用 `conv.lease` | `Sync/LeaseApplyRules.cs` |
| 日誌的識別、種類、相對路徑 | `Sync/LogIdRules.cs` |
| 鏈雜湊 | `Sync/LogChainRules.cs` |
| `conv.lease` 的種類、payload、驗證、blob 換回 | `Sync/ConversationEventKinds.cs`、`Sync/ConversationPayloads.cs`、`Sync/ConversationEventRules.cs`、`Sync/ConversationWire.cs` 的 `TryReadLease` |
| 可否換電腦（代理種類） | `Hub/AgentHubRules.cs` 的 `SupportsHandoff`、`LeaseRules.SupportsHandoff`（伺服器以線上名稱） |
| 伺服器的接縫（不是 Protocol）：開關、功能登記、sync.db v6、fenced 的順序 | `src/MyriagentHub` 的 `Logs/LogOptions.cs`、`Logs/LogRegistration.cs`、`Handoff/HandoffOptions.cs`、`Handoff/HandoffRegistration.cs`、`Data/SyncSchema.cs` 的 `V6`、`Sync/ConversationApplyRules.cs` |
| 桌面的接縫：開關、sync.db v4、引擎的登記點與分派 | `src/Myriagent` 的 `Sync/SyncPhase5Switches.cs`、`Sync/SyncStoreSchema.cs` 的 `CreateV4`、`Sync/SyncEngine.Contracts.cs`、`Sync/ISyncTransport.cs` |

### 16.1 功能宣告（`log1`、`hand1`）

- `DeviceFeatureRules.Log1 = "log1"`（逐字稿與檔案日誌：16.6 的端點、`LogRepair`）、`Hand1 = "hand1"`（租約、交接、強制接手：16.5 的方法、`conv.lease`）。`hand1` 只在也有 `log1` 時有效：`Negotiate` 的交集裡沒有 `log1` 時拿掉 `hand1`，兩邊的宣告本來就照這個條件。
- **伺服器**：`HubApplication` 在 `AddHubTokens` 之後呼叫 `AddHubLogs`、`AddHubHandoff`，依開關登記，`HelloResponse.features` 的最後兩項。開關是 `Hub:Logs:Declare`、`Hub:Handoff:Declare`（`HubOptions.Logs`、`HubOptions.Handoff`，**預設都是 false**）；`hand1` 要兩個都打開才宣告。開關關著時 `features` 與第 5 階段之前完全相同（`["conv1","prov1","live1","update1","cmd1","peer1","media1","stat1","tok1"]`）。WP5.3 結束時把 `Logs:Declare` 的預設改成 true、WP5.10 改 `Handoff:Declare`；測試以 `HubTestHost` 的設定與 `MyriagentHubProcess` 的環境變數（`Hub__Logs__Declare=true`）打開。
- **桌面**：`ContributeLogFeatures`（WP5.3 的 `SyncEngine.Logs.cs`）在 `tok1` 之後宣告 `log1`，條件是開關（`SyncPhase5Switches.Log1`）、也宣告了 `conv1`、v4 的表在（`SyncStore.ConversationTablesReady`：v4 與 v2、v3 在同一個升級交易）；`ContributeHandoffFeatures`（WP5.7 的 `SyncEngine.HandoffIn.cs`）在 `log1` 之後宣告 `hand1`，條件是 `SyncPhase5Switches.DeclaresHand1`（兩個開關都開）。開關是 `SyncEngineOptions.Phase5`（沒給是 `SyncPhase5Switches.Default`，兩個都關）；引擎的 `CollectHelloFeatures` 另外拿掉沒有 `log1` 的 `hand1`（程式錯誤的保險）。
- 每個方法要的功能（`RequiredFor`）：`HandoffPreview`、`HandoffStart`、`HandoffCancel`、`HandoffProgress`、`HandoffReady`、`HandoffRequest`、`HandoffUpdate` 要 `hand1`；`LogRepair` 要 `log1`。日誌的 HTTP 端點不是 hub 方法：伺服器開關打開才回應，桌面只在目前這條連線協商出 `log1` 時呼叫。
- 交握完成前到的 `HandoffRequest`、`HandoffUpdate`、`LogRepair` 照 14.4 的早到緩衝算「其他」類（`SyncEarlyMessages`，最多 32 則、滿了丟最舊的）：丟了不要緊，伺服器在 `ConnectionProven` 時重送進行中的 `HandoffRequest`（計畫 D17），`LogRepair` 在下一次維護再送。

### 16.2 伺服器事件 `conv.lease`（WP5.5 寫、WP5.6～5.8 套用）

擁有者換手唯一的來源（計畫 D2）：交接服務在提交交易裡寫（16.4、D3），類別 `meta`、串流 `c:{id}`、作者 `server`（`SyncAuthors.Server`），epoch 是新的 epoch。**不改 Hello／Prove**（`DeviceHubMessages.cs` 檔頭原本預留的 `leases`、`leaseVerdicts`、`pendingCommands` 不做）：離線的電腦重連拉 `meta` 就得知，順序天然跟 seq 走。

```jsonc
{ "owner": "d_B", "epoch": 3, "prevOwner": "d_A", "prevEpoch": 2,
  "reason": "transfer",                 // transfer | takeover | restore（LeaseReasons）
  "handoffId": "01J…",                  // ULID；restore 沒有
  "mode": "afterTurn",                  // afterTurn | now；只有 transfer 有
  "autoContinue": false,                // 只有 now 可以是 true
  "initiator": { "kind": "device", "deviceId": "d_B" },   // browser（帶 label）| device；restore 沒有
  "fromName": "OFFICE-PC", "toName": "HOME-PC",           // 提交當下的 devices.name
  "logicalAccountId": "la_…",
  "logs": [ { "logId": "cl:ed70…", "logKey": "<64 hex>", "kind": "claude-main", "main": true, "gen": 1,
              "length": 129629, "chain": "<base64>", "fullSha256": "<64 hex>", "relPath": null, "refId": null } ],
  "transcript": null,                   // null | missing | held | unportable | damaged
  "workspace": { "repoKey": "git:github.com/a/b", "sub": "src", "branch": "feature/x", "head": "1a2b3c4", "upstream": "origin/feature/x",
                 "ahead": 2, "behind": 0, "dirty": 3, "untracked": 1, "dirtyFiles": ["src/a.cs"] },
  "interruption": { "tools": ["Bash"], "pendingRequests": 1, "backgroundStopped": false },   // now 才有
  "queue": [ { "id": "q1", "text": "…", "attachments": ["att-1"], "secret": false } ],
  "skipped": { "workspaceFiles": ["big.iso"], "attachments": [] },
  "fence": { "atSeq": 81234, "lastDeviceSeq": 5521 },   // 只有 takeover 有，而且一定要有
  "cliVersion": "2.1.295" }
```

- `ConversationEventKinds.Lease = "conv.lease"`、`Placement` 是 `meta`；`IsServerAuthored(kind)` 只有它（`ConversationEventKinds.DeviceAuthored` 是其他八種）。**裝置送它一律 `invalid`**：伺服器的 `ConversationPrecheckRules`（連 DLP 都不跑、payload 不存）與 `ConversationApplyRules`，桌面寫 outbox 前的 `OutboxAppendRules`。電腦拉到作者不是 `server` 的略過（`LeaseApplyRules`）。前一版的拉取端不認得它、略過，游標照樣前進。
- **形狀**（`ConversationEventRules.Validate`／`CheckLeaseShape`）：owner、prevOwner 是不同的 deviceId；transfer、takeover 的 epoch＝prevEpoch＋1，restore 只要變大；欄位的有無照上面的註解；`logs` ≤2,000 筆、logKey 不重複，每筆的 logKey 等於 `LogIdRules.LogKey(logId)`、kind 等於 logId 決定的種類（16.6）、gen ≥1、length ≥0、chain 是 32 位元組的標準 base64、fullSha256 是 64 個小寫十六進位、relPath 有的話等於 logId 裡的相對路徑、`main` 只有主檔與 rollout 可以；`queue` ≤50 則；`workspace.dirtyFiles` ≤20；`transcript` 是列舉名稱（不認得的照收，讀的一方當成不能搬）。未知的欄位照舊忽略。
- **太大時換成 blob 參照**（WP5.1 加的，計畫沒寫）：`logs` 2,000 筆遠超過對話事件 payload 的上限（解壓後 128 KB），佇列的原文也可能超過內嵌上限（32 KB），所以 `logs`、`queue` 可以整欄換成 `{blob, length}`（同 `conv.item` 的 D34；內容是那個陣列的 JSON，存在伺服器的 blob）。讀的一方以 `ConversationWire.TryReadLease` 換回內容（雜湊、長度、UTF-8、只收一種解讀的陣列）、以不收 blob 的形狀再驗一次。
- `logs` 是接手端要下載的完整集合（主檔、伴隨檔、附件、這次快照的工作區檔）：線上交接取 A 回報的值、強制接手取伺服器驗證過的值。
- 電腦端的套用（`LeaseApplyRules.Decide`）：作者不是伺服器、epoch 不比這台已知的新（本機擁有時是 `LeaseEpoch ?? 1`、複本是 `replica_meta.epoch`）→ 不理；`owner` 是自己 → 匯入（WP5.7）；本機擁有、`handoffs_local` 有同一個 `handoffId` 的 source → 交出完成、變複本（WP5.6）；本機擁有的其他情況（強制接手、這台不知道的交接）→ 圍欄（WP5.8）；複本 → 更新 `replica_meta` 的擁有者與 epoch；不認得的對話 → 照第 4 階段（只影響清單）。

### 16.3 `conv.created`／`conv.meta` 的新欄位（選用、在最後）

- `transcript`：省略＝正常；`missing`（找不到主檔，14.2 規則 2）、`held`（含這台的 Claude／Codex 登入憑證：只有這台各帳戶設定資料夾裡的權杖值會讓日誌停住，一般像權杖的字串照樣上傳，計畫 D11 的擁有者決定）、`unportable`（Codex 主 rollout 的 `session_meta` 帶 `history_base`，E-5）。伺服器寫進既有的 `conversations.transcript`（WP5.2）；這幾種換電腦時一律前文節錄。驗證只要求是列舉名稱（新版的值照收）。WP5.3 才傳值。
- `workingDirectoryRef`：`{ "kind": "git", "repoKey": "git:github.com/example-user/proj", "sub": "src", "display": "proj" }`、`{ "kind": "path" }`；只有 `customFolder` 是 true 時帶（否則 invalid），git 一定要有 repoKey（≤512 字）、sub ≤1024 字、display ≤200 字，kind 是列舉名稱（新版的值照收）。投影（`ConversationWire.Meta` 的 `workingDirectoryRef` 參數）只送認得的種類，sub、display 先遮蔽並截斷、repoKey 遮蔽，不合格就整個不送。存檔是 `AgentConversation.WorkingDirectoryRef`（`PathRef`）。WP5.4 才傳值。
- 兩個欄位都沒傳時，`conv.meta` 的線上格式與第 4 階段逐位元組相同；前一版伺服器忽略不認得的欄位。

### 16.4 伺服器接收對話事件的改動（`ConversationApplyRules`）

1. **列存在時，事件的 epoch 比列上的舊 → `fenced`（帶列上的 epoch），在擁有者檢查之前**（計畫 D4）：交接後原擁有者的遲到事件要被當成圍欄（桌面的 FenceReconciler，WP5.8），不是 `not_owner`（桌面會封鎖並併成「重新配對」的彙總提示）。第 4 階段 epoch 一律是 1，這一步從來不成立，結果和以前相同（WP5.1 起）。
2. `lease_state = recovering` 而且是 `conv.created`：作者是擁有者照舊（`ClearsRecovering`）；作者不是擁有者而且 epoch 比列上的大（交接後的新擁有者，還原把擁有者倒回去了）→ 認領（`LeaseRules.IsRecoveringClaim`）：同一個寫入裡把 owner、epoch 改成事件的值，再寫一筆 `conv.lease{reason: restore}`；epoch 相同而作者不同照舊 `not_owner`。**WP5.5 才做**（WP5.1 只有規則）。
3. 擁有者與 epoch 只在交接服務的提交交易裡改（D3）：同一交易寫 `conv.lease`、更新 `conversations`（owner、epoch、`lease_state='owned'`）、把這個對話待處理的指令改成 `rejected`（`not_owner`）。**不改 `ConversationMaterializer.Version`**：重建照摺疊 `conv.lease`（WP5.5），前一版的程式不重建（版本相同），回滾後照樣接受 B 以新 epoch 寫的事件。
4. 其他不變。

### 16.5 裝置 hub 的方法（`hand1`；WP5.5 伺服器、WP5.6／5.7 桌面）

| 方向 | 方法 | 型態 | 說明 |
|---|---|---|---|
| 裝置→伺服器 | `HandoffPreview` | Invoke | `HandoffPreviewRequest {conversationId}` → `HandoffPreviewResult`：換電腦的預覽（瀏覽器經閘道呼叫同一個服務，D15） |
| 裝置→伺服器 | `HandoffStart` | Invoke | `HandoffStartRequest {commandId, conversationId, toDeviceId, mode, force, autoContinue}` → `HandoffStartResult {handoffId?, error?}`：同一個 `commandId`（發起端的 ULID）回原結果 |
| 裝置→伺服器 | `HandoffCancel` | Invoke | `HandoffCancelRequest {handoffId}` → `HandoffCancelResult {error?}`（提交前；任何發起端都可以） |
| 裝置→伺服器 | `HandoffProgress` | Invoke | `HandoffProgress {handoffId, role, state, error?, workspace?, detail?}` → `HandoffAck {ok, error?}`：交出端 accepted／draining／aborted、接手端 importing／needsFolder／imported／importFailed（`HandoffProgressStates.IsValid`） |
| 裝置→伺服器 | `HandoffReady` | Invoke | `HandoffReady {handoffId, finalDeviceSeq, logs, transcript?, workspace?, interruption?, queue?, skipped?, cliVersion?}` → `HandoffAck`：交出端排空、上傳完；伺服器驗過就在同一個交易提交 |
| 伺服器→裝置 | `HandoffRequest` | Send | `{handoffId, conversationId, epoch, mode, toDeviceId, toName?, deadlineAt, initiator}`：請擁有者 A 交出；同一個 handoffId 重送時 A 照目前進度回 |
| 伺服器→裝置 | `HandoffUpdate` | Send | `{handoffId, conversationId, kind, state, fromDeviceId, toDeviceId, mode?, deadlineAt?, error?, detail?, importState?}`：給發起者、A、B |
| 伺服器→裝置 | `LogRepair` | Send | （`log1`）`{logKey, logId, reason: "damaged"}`：存放的日誌損毀，擁有者整檔以新世代重傳 |

```jsonc
// HandoffPreview 的結果
{ "provider": "Claude", "ownerDeviceId": "d_A", "ownerOnline": false, "ownerLastSeenAt": 1791587000000, "canForce": true,
  "transcript": "ok",                  // ok | missing | held | unportable | damaged | none（沒有日誌）
  "transcriptAt": 1791586000000, "cliVersion": "2.1.295",
  "targets": [ { "deviceId": "d_B", "name": "HOME-PC", "eligible": true, "why": null } ],   // why：offline | outdated | no_account | policy | owner
  "blocked": null,                     // 整個對話不能換：unsupported_provider | not_full | deleted | busy
  "error": null }                      // 預覽本身失敗：not_found | not_available（WP5.1 加的欄位）
```

- 不簽章（擁有者決定 3、D16）：連線已綁 TLS SPKI；A 照樣寫遠端操作紀錄。所有方法在 `DeviceHubMethods.DeviceToServer`／`ServerToDevice` 登記（伺服器的方法允許清單），伺服器的實作是 WP5.5 的 partial 檔 `Realtime/DeviceHub.Handoff.cs`（WP5.1 時允許清單已有、hub 上還沒有方法：呼叫會是 SignalR 的「方法不存在」，只有協商出 `hand1` 的連線會呼叫，所以不會發生）。
- 狀態機（`LeaseRules.Transition`；伺服器 `handoffs.state`）：requested →（A 15 秒內接受）accepted →（A 排空）draining →（A 的 HandoffReady）ready →（驗證通過）committed；requested 沒接受或期限到 → expired，accepted／draining 期限到（afterTurn 30 分、now 3 分，`LeaseRules.DeadlineFor`）→ expired；進行中（requested～ready）取消 → cancelled、A 中止 → aborted；ready 驗證失敗 → aborted；伺服器重啟時 ready 重新驗證再提交、其他進行中的 → aborted（`server_restart`，D17）。強制接手不經這張表，直接建 committed 的列。
- `HandoffStart` 的檢查（`LeaseRules.CheckStart`，依序、第一個不符就回 16.8 的代碼）：對話存在、沒刪除；`SupportsHandoff`；同步政策是 full；沒有進行中的交接（強制接手不擋，進行中的那筆由交接服務中止）；目標不是擁有者；目標在線而且有 `hand1`；目標有這個邏輯帳戶 Ready 的連結；不是目標自己發起時目標允許遠端操作（Hello 帶的政策是 Control、沒有暫停）；一般交接時擁有者在線、有 `hand1`、不是擁有者自己發起時允許遠端操作；強制接手時擁有者必須失聯、不問擁有者的政策。「改成立即」（同一筆進行中的 afterTurn 改 now）不經這張表，由交接服務在檢查之前處理。
- 失聯（`LeaseRules.IsOrphaned`，D7）：沒有已證明的連線滿 60 秒，或連線在但 `uiAliveAt` 超過 120 秒（還沒收到心跳不算）；被撤銷的裝置立即算。伺服器重啟寬限 180 秒，只給重啟前還在線的裝置（最後一次有連線不早於啟動時間減 120 秒；啟動之前的以持久的 `device_streams.last_heartbeat_at` 代替）。

### 16.6 逐字稿與檔案的 HTTP 端點（`/api/v1/device/logs`，`DeviceAccess`，`log1`；WP5.2 實作與定稿）

- **識別**（`LogIdRules`）：`cl:{sid}`（Claude 主檔，`claude-main`）、`cl:{sid}/subagents/{名稱}`（`.jsonl` 是 `claude-sub`，其他如 `.meta.json` 是 `claude-file`）、`cl:{sid}/tool-results/{名稱}`（`claude-file`）、`cx:{threadId}`（Codex rollout，`codex-rollout`）、`fa:{conversationId}/{相對路徑}`（附件，含 `.model\`，`attachment`）、`fw:{conversationId}/{相對路徑}`（預設工作區，`workspace`）。**種類由 logId 決定**（`LogIdRules.KindOf`）：上傳的 `X-Myriagent-Kind` 與 `conv.lease.logs[].kind` 必須等於它。session id 與 thread id 是 1～64 個英數字、連字號、底線（接手端拿它組檔名，不能有句點）；對話 id 是線上格式。Claude 的伴隨檔一律以 `<sid>\` 之下的相對路徑命名（E-1）。網址用 `logKey`＝logId 的 UTF-8 的 SHA-256 小寫十六進位。`claude-main`、`claude-sub`、`codex-rollout` 每段要到最後一個 LF（`RequiresLf`），其他是整檔。
- **相對路徑**（`LogIdRules.NormalizeRelativePath`）：反斜線換成正斜線；不能是空的、根路徑、含 `:`、控制字元、`<>"|?*`、落單的代理字元；每一段不能是空的、`.`、`..`，不能以句點或空白結尾，主檔名不能是保留的裝置名稱（CON、PRN、AUX、NUL、COM0～9、LPT0～9，不分大小寫、含副檔名）；非 ASCII 照收；≤1,024 字。logId 裡的相對路徑必須已是正規化的寫法。接手端寫入前另以 `HandoffImportRules.SafeRelativePath` 確認完整路徑在目標資料夾內（WP5.7）。
- **鏈雜湊**（`LogChainRules`，設計 4.12）：`H_0 = SHA256(UTF8("myriagent-log/1|" + logId + "|" + gen))`，`H_n = SHA256(H_{n-1} ‖ SHA256(chunk_n))`；線上是 44 字的標準 base64，只收唯一的寫法。同一段位元組切法不同結果就不同，所以伺服器依 `log_chunks` 的邊界重算。只偵測傳輸與存放的損毀、截斷（擁有者決定 2：不簽章，沒有 `X-Myriagent-Sig`、`bad_attestation`）。
- **附加** `POST /api/v1/device/logs/{logKey}/append`（本文是原始位元組，可 `Content-Encoding: gzip`）與它的標頭（`X-Myriagent-Log`〔logId 的 base64url〕、`X-Myriagent-Conversation`、`X-Myriagent-Epoch`、`X-Myriagent-Kind`、`X-Myriagent-Gen`、`X-Myriagent-Offset`、`X-Myriagent-Length`〔解壓後〕、`X-Myriagent-Prev-Chain`、`X-Myriagent-Chain-After`、`X-Myriagent-Main`、`X-Myriagent-Ref`、`X-Myriagent-Total`、`X-Myriagent-Cli-Version`）、判斷表、寫入順序、狀態 `GET /api/v1/device/logs?conversationId=`、下載 `GET /api/v1/device/logs/{logKey}?gen=`（Range、`ETag: "{gen}-{length}"`、`X-Myriagent-Full-Sha256`）、掃描與配額：照計畫 5.6，WP5.2 實作時在這裡定稿。

### 16.7 資料表

伺服器 sync.db **v6**（`SyncSchema.V6`，全部 `IF NOT EXISTS`，v1～v5 的物件一個字都不改；回滾到 v5 的程式時 from 比它新，什麼都不動、只記警告）：

| 表 | 用途（寫的工作包） |
|---|---|
| `logs` | 每個日誌目前世代的狀態：長度、鏈雜湊、行數、整檔總長度、寫入者與 epoch、整檔 SHA-256 的快取、損毀時間（WP5.2）；索引 `ix_logs_conversation` |
| `log_chunks` | 每一次附加的邊界與接上之後的鏈雜湊（主鍵 `(log_key, gen, offset)`；`offset` 是 SQLite 的關鍵字，當欄位名稱照樣能用） |
| `log_generations` | 被取代的舊世代（`rewrite`／`repair`），保留 30 天 |
| `handoffs` | 交接與強制接手（`command_id` 唯一；`ux_handoffs_active`：同一個對話最多一筆 requested／accepted／draining／ready）（WP5.5） |

欄位照計畫 5.7。伺服器不放 `conversation_files`、`repos`、`path_maps`（附件與工作區也是日誌、資料夾對應只在電腦端）。

桌面 sync.db **v4**（`SyncStoreSchema.CreateV4`；`SchemaVersion = 4`、`MinReaderVersion` 維持 1，v1～v3 的程式照樣能開）：`log_state`（這台上傳的每個日誌：本機路徑只在本機、確認到的長度與鏈雜湊、改寫偵測的雜湊、`held`；WP5.3）、`handoffs_local`（這台參與的交接，role 是 source／target／fork）、`path_cache`（repoKey 對應到這台的資料夾；WP5.4）；索引 `ix_log_state_conversation`。另外開始使用 v2 已有的 `leases`：`state` 是 `handoffOut`、`handoffIn`、`importFailed`、`needsFolder`、`fenced`（`LocalLeaseStates`），**沒有列＝照第 4 階段判斷**；與 `backfill_state.logs_done_at`。kv `hand1_replicas`：第一次協商出 `hand1` 時對話中繼資料與複本串流的游標歸零一次（D19，WP5.7）。

### 16.8 代碼（`HandoffCodes`）

`busy`（已有進行中的交接）、`owner_online`（要強制接手但 A 在線）、`owner_offline`（一般交接但 A 失聯，改用強制接手）、`target_offline`、`target_outdated`（目標或擁有者沒有 `hand1`）、`no_account`、`policy`、`unsupported_provider`、`not_full`（同步政策不是 full）、`not_found`、`deleted`、`same_device`、`log_mismatch`、`events_missing`、`log_damaged`、`expired`、`cancelled`、`server_restart`、`not_available`。網頁的文字對照在 `Web/handoffrules.js`（WP5.9）。

### 16.9 休眠與回滾相容

- 開關關著（正式環境的預設）時：伺服器不宣告 `log1`／`hand1`、日誌端點回 404 `not_available`（WP5.2 起）、瀏覽器的 `takeover`／`handoffCancel`／`handoffPreview` 回 `not_available`；桌面不宣告，沒有任何連線協商得出，本節的訊息一則都不送。
- 伺服器回滾只換程式、不還原資料庫：v6 只加表；`conv.lease` 存在 `events`，前一版照回拉取、裝置略過；不改物化版本，回滾不觸發重建（回滾到第 5 階段之前的版本後不要手動重建物化表，WP5.2 寫進 `docs/hub-setup.md`）；日誌檔在 `transcripts\`，前一版不碰。
- 桌面資料檔只加欄位：`AgentConversation` 的 `WorkingDirectoryRef`、`ForkedFrom`、`ForkedFromTitle`、`ForkPointSeq`、`ForkSourceTranscript`、`PendingHandoffNote`（null 時不寫）；`AgentNoticeKind` 的 `Handoff`、`Fork`（U1 起的舊版讀成 null；數值 5 刻意不用，見 `AgentModels.cs`）。不新增其他持久列舉值；交接與匯入的狀態是 sync.db 裡的字串。
- 刪除事件的 epoch 改用對話的 `LeaseEpoch`（`DeletingChange.Epoch`；資料夾已不在、沒有中繼資料可看時取 outbox 裡最後一筆的 epoch）：接手後的對話 epoch 不是 1，寫死 1 的 `conv.deleted` 會被 fenced（WP5.1 起；第 4 階段 epoch 一律是 1，結果相同）。
