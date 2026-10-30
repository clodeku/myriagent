# Myriagent Hub 瀏覽器端到端（tools/hub-e2e）

第 2 階段 WP2.5（設計 `docs/plans/2026-10-10-multi-device-sync.md` 11.3；移植研究原型 `web/e2e/passkey-e2e.mjs`），第 3 階段擁有者登入從 passkey 改成**單一帳號＋密碼**（帳號 `admin`、首次啟動以預設密碼建立，設計 14.4）後改寫。Playwright 用**本機安裝的 Chrome**（`channel: 'chrome'`，不下載瀏覽器），開兩個瀏覽器內容（各自一份 cookie），對以暫存資料根、高埠啟動的 MyriagentHub 跑完整的擁有者流程：

登入頁 → 密碼錯誤、帳號錯誤 401 → 以預設密碼登入 → 登出（舊 cookie 也失效）→ 再登入、第二個瀏覽器也登入 → 撤銷第二個瀏覽器的工作階段（被撤銷的立即 401）→ 改密碼（目前的密碼錯 403、太短 400、成功後其他工作階段立即 401）→ 舊密碼不能登入、新密碼可以 → 再驗證 → CSP 0 違規＋插入 inline script 被擋（正向對照）→ 跨站 Origin 的 POST 被拒 → 改回預設密碼 → 版本頁的模組載入失敗時登入頁與配對核准頁照常 → 主控台 0 個非預期錯誤 → 未對應路徑一律 404（`/api` 底下匿名 401）、瀏覽器不要 `/favicon.ico`（登入頁的圖示是 `/app/favicon.ico`）→ 稽核紀錄（雜湊鏈、不含密碼）→ 結束時登出 → 正常停止、沒有孤兒金鑰檔 → 結果檔與伺服器記錄不含密碼。

頁面上的流程一律呼叫 `src/MyriagentHub/wwwroot/auth.js` 提供的掛鉤 `window.__hubProbe`（`login(userName, password)`、`logout()`、`stepUp(password)`、`changePassword(current, next)`、`revokeSession(id)`、`me()`、`releases()`（版本頁目前畫出的資料）、`tryInlineScript()`、`view`、`violations`、`ready`），走的是網頁真正用的程式碼；只有「重送舊 cookie」與「偽造的跨站請求」由 Node 直接送。

## 前提

- Windows、Node.js 20 以上、已安裝 Google Chrome（`C:\Program Files\Google\Chrome\Application\chrome.exe`）。
- `cd tools/hub-e2e && npm install`（只裝 `playwright-core` 1.64.0，與 `tools/ui-probe` 同版；`node_modules` 不進 repo）。
- 先建置 MyriagentHub：`dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release -nodeReuse:false -p:UseSharedCompilation=false`。
- 不需要管理員；MyriagentHub 以 `Development` 環境（`ServerDomain=localhost`）、`BindAddress=localhost`、自動挑的高埠、file 憑證模式（自簽）執行，資料根在工作資料夾。速率限制、近期驗證（5 分鐘）、退避都照正式設定。
- **瀏覽器內容一律經 `lib/quiet-browser.mjs` 的 `newContext` 建立**（2026-10-23；`test/quiet-browser.test.mjs` 掃這個資料夾有沒有直接呼叫 `browser.newContext`）：Playwright 的 `--mute-audio` 只靜音網頁自己播的聲音，網頁的語音合成在 Windows 交給系統的語音引擎念、照樣從喇叭出來。假裝置一完成對話，伺服器就送 `speak`，測試的網頁會以涵涵念出語音通知，和桌面 app 的雅婷疊在一起。`newContext` 在載入頁面之前把 `speechSynthesis.speak`／`cancel` 換成不出聲的版本（照樣送 start、end），網頁的流程不變。

## 執行

```bash
cd tools/hub-e2e
npm install
node hub-e2e.mjs                       # 自己啟動 MyriagentHub、跑完以 admin stop 正常結束
node --test authrules.test.mjs releaserules.test.mjs   # 登入頁與版本頁的純規則（不開瀏覽器、不啟動伺服器）
```

| 參數 | 預設 | 說明 |
|---|---|---|
| `--hub <MyriagentHub.exe>` | `<repo>\src\MyriagentHub\bin\Release\net10.0\win-x64\MyriagentHub.exe` | 要測的伺服器 |
| `--port <埠>` | 自動挑空的 | MyriagentHub 的 HTTPS 埠（網址一律 `https://localhost:<埠>`） |
| `--work <資料夾>` | `%TEMP%\myriagent-tools\hub-e2e\<時間>` | 工作資料夾：`result.json`、`hub-console.log`、`hub-data\`（資料根） |
| `--out <檔案>` | `<work>\result.json` | 結果檔 |
| `--headed` | 無頭 | 顯示瀏覽器視窗（除錯用） |
| `--base https://localhost:<埠>` | — | 改對已經在執行、**擁有者密碼還是預設**的**本機**伺服器（新的資料根，或剛 `MyriagentHub.exe admin reset-owner`）；不負責啟停，不檢查金鑰檔、不跑 `admin verify-audit`、不掃伺服器記錄，稽核次數只要求「至少」，工作階段數扣掉之前就在的。跑完把密碼改回預設並登出。**正式站守門**（計畫 D18，`lib/guard-rules.mjs` 的 `checkExternalBase`；WP4.9 合併前審查補上）：網址只收 https＋`127.0.0.1`／`localhost`／`[::1]`、不帶帳號密碼，任何 `example.com` 一律拒絕；取鎖、開瀏覽器、送任何登入之前先讀 `/readyz`，要 200、`environment` 是 `console`，連線看到的憑證與 `/readyz` 的 `tls` 不是 `hub.example.com` 或公開 CA（擋「`ssh -L` 轉送到正式服務」）。不過就以結束碼 2 結束 |

以前的 `--data`（讀一次性設定碼）已拿掉：設定碼沒有了，給了會直接報錯。

結束碼：0 全部通過、1 有檢查失敗。約 7 秒（未登入的登入一次流程 8 次，在每 IP 每分鐘 10 次以內，不必等；見「注意」）。

## 瀏覽器閘道（gateway.mjs）

第 3 階段 WP3.6（計畫 `docs/plans/2026-10-16-wp3.6-browser-gateway.md` 步驟 12；契約 `docs/web-protocol.md` 第六節）。同樣以本機 Chrome、暫存資料根、自動挑的高埠、`Development` 環境自己啟動 MyriagentHub，以**真的瀏覽器**驗證：嵌入的桌面網頁（`/app/`）在伺服器的 CSP 下 0 違規；瀏覽器的 SignalR JSON 協定走得完 ready → init → setting → settings；Origin 檢查真的擋住**帶有效 cookie** 的跨站升級；登出後開著的 WebSocket 被關。頁面裡的 WebSocket 不用 SignalR 程式庫，直接送 JSON 文字協定（握手 `{"protocol":"json","version":1}`、每則以 0x1E 結尾），WP3.5 的 `transport-hub.js` 合併前也測得到伺服器這一半。

WP3.5（計畫 `docs/plans/2026-10-16-wp3.5-web-transport.md` 步驟 7）起 wwwroot 有 `transport-hub.js`：匿名開 `/app/` 會被傳輸層導到登入頁 `/`、已登入的分頁由傳輸層自己連上 hub、登出後開著 `/app/` 的分頁被導回 `/`，所以預期的錯誤改成下面「預期的錯誤」那張表（次數要剛好）。CSP 違規收集器改用 `context.exposeBinding('__gwCsp', …)`：每筆違規連同 `location.href` 送回 Node 端累積，分頁導覽之後也不會遺失；頁面上的 `window.__csp` 只留給正向對照（同一個頁面前後比較）。用 `--hub` 指到沒有 `transport-hub.js` 的舊建置時，照舊走「要在 Clode Myriagent 裡開啟」的檢查。

WP3.5 步驟 8 加了「連線上限」：以真伺服器驗每個登入 8 條連線的上限與關閉訊息的字串（傳輸層以「包含 `HubException: too_many_connections`」比對，計畫 D7）。實測伺服器送的關閉訊息是 `Connection closed with an error. HubException: too_many_connections`（WebSocket 代碼 1000），SignalR 程式庫交給 `onclose` 的再加上 `Server returned an error on close: ` 前綴。

```bash
cd tools/hub-e2e
node gateway.mjs                       # 自己啟動 MyriagentHub、跑完以 admin stop 正常結束（約 8 秒）
```

參數 `--hub`、`--port`、`--out`、`--headed` 與 `hub-e2e.mjs` 相同；`--work` 預設 `%TEMP%\myriagent-tools\gateway\<時間>`。沒有 `--base`（流程會改漫遊偏好、登出，只對自己啟動的伺服器跑）。結束碼：0 全部通過、1 有檢查失敗。

| 步驟 | 通過條件 |
|---|---|
| 取得序列化鎖 | 見下面「序列化鎖」 |
| 啟動 MyriagentHub | `/healthz` 200；記下啟動期間新增的金鑰檔（算這個 MyriagentHub 的）與 wwwroot 有沒有 `transport-hub.js` |
| `/app` 302 到 `/app/` | `/app` 302 → `/app/`、`/app?mode=popout&conversation=c-1` 302 → `/app/?mode=popout&conversation=c-1`（查詢字串照帶）；`/app/` 200、`Cache-Control: no-store`、CSP 的 `script-src` 只有 `'self'`；匿名的 `<html data-theme="dark">`；wwwroot 有 `transport-hub.js` 時才插入傳輸腳本 |
| 頁面載入 | `context.addInitScript` 先掛 `securitypolicyviolation` 收集器（CDP 注入，不受 CSP 影響）。**有 `transport-hub.js`**：匿名的 `/app/`（第 2 步取得的 HTML 裡依序是 `/lib/signalr-…`、`/transport-hub.js`、`app.js`）5 秒內被導到 `/`、看得到 `meta[name="myriagenthub-page"][content="auth"]`，等 `window.__hubProbe.ready` 之後 ≥400 的回應正好是 `/api/v1/auth/me` 401×2（傳輸層一次、auth.js 一次）與 `/api/v1/sessions` 401×1、CSP 違規 0、非預期的主控台錯誤 0。**沒有**：CSP 違規 0、主控台錯誤 0、≥400 的回應 0；`document.scripts` 只有 `app.js`（module）；畫面顯示「這個頁面要在 Clode Myriagent 裡開啟」 |
| 頁面內登入 | 頁面內 `fetch('/api/v1/auth/login', {POST, X-Myriagent-Csrf: 1})`（預設密碼）200；cookie `__Host-myriagenthub` HttpOnly、SameSite=Strict |
| 瀏覽器 hub：ready → init | 頁面內 `new WebSocket('wss://localhost:<埠>/hubs/v1/browser')`（cookie 由瀏覽器自動帶）：握手回 `{}`；`Cmd {cmd:{cmd:"ready",mode:"main"}}` 的結果 `accepted:true`；`Msg` 的 `init`：`remote` 是 `{mode:"browser", deviceId:null, devices:[…], sync:null}`、`providers` 只有 Claude、Codex（`safeMode:false`）、`settings` 正好四個鍵、`cli:{}`、`conversations:[]`、`layout:null` |
| setting theme light | 頁面內照契約產生 ULID 的 `commandId`（`Date.now()` 48 位元＋`crypto.getRandomValues` 80 位元）；結果 `{accepted:true, commandId:<原樣>}`；收到 `settings.theme==="light"`；另開分頁（B）重新載入 `/app/` 是 `data-theme="light"`（有 `transport-hub.js` 時另等 `#app.ready`、`myriagentHubTransport.status()` 是 `online` 而且 `hubInit`、狀態列隱藏、沒有 `.no-host`）；從 Node 以同一份 cookie 取 `/app/` 是淺色、匿名仍是深色 |
| CSP 正向對照 | 重新載入的分頁插入 inline script：沒有執行、收集器正好 1 筆 `script-src-elem`／`inline`（證明收集器收得到） |
| 連線上限 | 這時同一個登入有 2 條連線（第 5 步分頁 A 的 `window.__gw`、分頁 B 的傳輸層；沒有 `transport-hub.js` 時只有 1 條）。分頁 A 另開原生 WebSocket（`window.__gwExtra`，不碰 `__gw`，登出那一步要用）湊滿 8 條：每條都要握手回 `{}`、每 10 秒 ping、0.5 秒後還開著。第 9 條（原生）握手之後收到 type 7 的關閉訊息，`error` 含 `HubException: too_many_connections`。**有 `transport-hub.js`**：再開分頁 C 的 `/app/`，10 秒內狀態列含「開了太多分頁」、`myriagentHubTransport.status().reason === 'tooManyConnections'`、`retryAt − lastCloseAt` 在 30000～36000 ms；關掉另開的、等 1 秒，`retryNow()`（最多 3 次、間隔 1 秒）之後 5 秒內 `online`、收到 init、`#app.ready`；最後關掉分頁 C |
| 方法呼叫被拒的錯誤字串 | 用第 5 步那條連線送不合格的 `ClientState {visible:"x"}`：completion 的 `error` 含 `HubException: invalid`、沒有 `result`，連線照常開著（傳輸層對方法呼叫的錯誤也以「包含 `HubException: <代碼>`」比對，計畫 D7、R4） |
| Origin 檢查 | 從 Node（`node:https`、不驗憑證）帶**有效 cookie**（Playwright 的 `context.cookies()`）：`Origin: https://evil.example` 的 `POST /hubs/v1/browser/negotiate?negotiateVersion=1` → 403 `{"error":"forbidden_origin"}`、WebSocket 升級（`Connection: Upgrade`、`Upgrade: websocket`、`Sec-WebSocket-Version: 13`）→ 403、沒有 `upgrade` 事件；正向對照：同一份 cookie、本站 Origin 的 negotiate 200（只有 WebSockets；查詢字串帶 `useStatefulReconnect=true` 要求了也不開——不帶的話伺服器不論設定都不會回 `useStatefulReconnect:true`，檢查不出設定被改開）、升級 101。SameSite=Strict 的 cookie 不會被跨站頁面帶上，所以要這樣才證明擋下的是 Origin |
| 登出後 WebSocket 被關 | 登出前那條 WebSocket 還開著（`readyState` 是 OPEN、還沒收到過 `close`，不然「關閉」可能是別的原因、而且早於登出）；頁面內 `POST /api/v1/auth/logout` 200 後 2 秒內 `close`（關閉時間不早於登出）；登出前的 cookie 再升級 401；有 `transport-hub.js` 時開著 `/app/` 的分頁 B 5 秒內被導到 `/`（伺服器關掉它的連線 → 傳輸層重連前問 auth/me 得到 401） |
| 稽核筆數 | 再登入後 `MyriagentHub.exe admin verify-audit` 結束碼 0、`GET /api/v1/audit?limit=200` 200，筆數剛好：`owner.created`×1、`auth.login` ok×2／denied×0、`prefs.set` ok×1（目標 `theme`、內容 `light`）／denied×0、`auth.logout`×1；`chain.ok` |
| 主控台只有預期的錯誤 | 所有分頁（A、B，有 `transport-hub.js` 時另有 C）的 `console.error`／`pageerror` 只有 CSP 正向對照那一筆與下表的 401（瀏覽器記成「Failed to load resource」）；≥400 的回應和下表逐項比對、次數剛好；收集器的 CSP 違規總共只有分頁 B 正向對照那一筆 |
| 停止 MyriagentHub | `admin stop` 結束碼 0、伺服器結束碼 0 |
| 主機副作用 | 見下面「金鑰檔的歸屬」 |

### 預期的錯誤（有 `transport-hub.js` 時）

| 分頁 | 回應 | 次數 | 為什麼 |
|---|---|---|---|
| A | `GET /api/v1/auth/me` 401 | 2 | 匿名開 `/app/`：傳輸層連線前問一次，導到 `/` 之後 auth.js 的 `refresh()` 再問一次 |
| A | `GET /api/v1/sessions` 401 | 1 | 導到 `/` 之後 auth.js 的 `renderAccount` |
| B | `GET /api/v1/auth/me` 401 | 2 | 登出後伺服器關掉 B 的連線：傳輸層重連前問一次，導到 `/` 之後 auth.js 再問一次 |
| B | `GET /api/v1/sessions` 401 | 1 | 同上，auth.js 的 `renderAccount` |

稽核筆數不變：傳輸層的 `ready`、`ClientState` 不寫稽核；連線上限只記伺服器記錄、不寫稽核。分頁 C 沒有預期的錯誤（`/api/v1/auth/me` 都是 200、被拒的連線不會在主控台留下錯誤）。

### 序列化鎖

`%TEMP%\myriagent-tools\hub-e2e.lock`（`lock.mjs`，`hub-e2e.mjs`、`gateway.mjs`、`pairing.mjs`、`remote-control.mjs`、`full-flow.mjs`（WP4.9 步 14 起）、`real-exe.mjs`（步 20）、`releases.mjs`（OWNER-1）與 `tools/ui-probe/conv-probe.mjs` 共用；下文的「這四支」指前四支，其他幾支同樣的規則）：以 `fs.openSync(…, 'wx')` 建立（內容是 PID、工具、時間與工作資料夾），已存在而且不到 10 分鐘就每 0.5 秒再試、最多等 2 分鐘（等不到就記失敗、不啟動伺服器），超過 10 分鐘的當成上一次被強制結束留下的、刪掉再搶；結束時只刪自己的（`remote-control.mjs` 持有期間每分鐘更新一次鎖檔的修改時間，跑得久也不會被當成舊鎖）。各 worktree 的這四支因此不會同時跑（取得鎖之後才列金鑰檔、啟動 MyriagentHub，停止並檢查完金鑰檔才放；`pairing.mjs`、`remote-control.mjs` 另在放鎖之前結束假裝置、`unpair`）。鎖只管這四支：**都不要和其他 worktree 的 `dotnet test`／MyriagentHub／`tools/acme-e2e`／`tools/fake-device`（含 `measure_sync.py`）同時跑**（共用的金鑰存放區見下）。

### 金鑰檔的歸屬

`%APPDATA%\Microsoft\Crypto\Keys` 是整個使用者共用的。啟動 MyriagentHub 前列一次（A）、`/healthz` 200 之後再列一次（B），B 減 A 算這個 MyriagentHub 匯入的：停止後其中還在的才算失敗（孤兒）。之後才出現、現在還在的只記成「其他程序的金鑰檔」觀察（`ok:null`），不讓別的程序（例如其他 worktree 的 Kestrel 測試）造成誤判。`gateway.mjs`、`pairing.mjs`、`remote-control.mjs` 一開始就是這樣；`hub-e2e.mjs` 原本以整個存放區的前後差判斷，WP4.9 步 14 改成同樣的作法（計畫 R52）。`full-flow.mjs` 另以複製後的 exe 路徑（`hub-<pid>-<n>\MyriagentHub.exe` 的 UTF-16LE）認 MyriagentHub 的金鑰檔（`lib/hub-process.mjs`），假裝置以 `device.json` 記的金鑰名稱認。

## 配對與儀表板（pairing.mjs）

第 3 階段 WP3.5（計畫 `docs/plans/2026-10-16-wp3.5-web-transport.md` 步驟 13；設計 13.3 的演示）。本機 MyriagentHub（暫存資料根、自動挑的高埠、`Development`）＋兩台 `tools/fake-device` 假裝置，以真的瀏覽器走完「核准兩台 → 網頁版看矩陣與共享用量 → 改名、排序 → 撤銷 → 從目錄刪除 → 登出」，再以 `admin drain`／`undrain` 驗證換版不斷線（A 收到 shutdown、重新啟動後 Hello 回 draining、解除後回到 Live 並留在線上）。核准頁、矩陣、電腦頁都用網頁真正的程式碼與 DOM（按鈕、選單、對話框都是真的點擊），只有稽核由頁面內 `fetch` 讀。

```bash
dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release -nodeReuse:false -p:UseSharedCompilation=false
dotnet build tools/fake-device -c Release -nodeReuse:false -p:UseSharedCompilation=false
cd tools/hub-e2e
npm install
node pairing.mjs                       # 約 1 分鐘（WP3.5 時約 46 秒；U2 的排空那一步多約 10 秒）
node pairing.mjs --stepup              # 近期驗證改成 1 分鐘、撤銷前等到登入 65 秒後：撤銷時跳密碼框（WP3.5 時約 81 秒，同樣多約 10 秒）
```

| 參數 | 預設 | 說明 |
|---|---|---|
| `--hub <MyriagentHub.exe>` | `<repo>\src\MyriagentHub\bin\Release\net10.0\win-x64\MyriagentHub.exe` | 要測的伺服器（wwwroot 要有 `transport-hub.js`，沒有就直接失敗） |
| `--fake-device <FakeDevice.exe>` | `<repo>\tools\fake-device\bin\Release\net10.0-windows\win-x64\FakeDevice.exe` | 假裝置 |
| `--port <埠>` | 自動挑空的 | MyriagentHub 的 HTTPS 埠（網址一律 `https://localhost:<埠>`，假裝置以 `--dev-trust` 接受自簽憑證） |
| `--work <資料夾>` | `%TEMP%\myriagent-tools\pairing\<時間>` | 工作資料夾：`result.json`、`hub-console.log`、`hub-data\`、兩台假裝置的資料根 `fdA\`、`fdB\`、劇本、假裝置每次執行的輸出（`<名稱>-<序號>.stdout.log`／`.stderr.log`）、截圖 |
| `--out <檔案>` | `<work>\result.json` | 結果檔 |
| `--stepup` | 不開 | 伺服器加 `--Hub:Session:RecentAuthMinutes=1`，撤銷前等到「最後一次輸入密碼」的 65 秒後（先斷言兩台都還在線），撤銷時網頁跳密碼框、輸入預設密碼後重送 |
| `--headed` | 無頭 | 顯示瀏覽器視窗（除錯用） |

沒有 `--base`（流程會配對、撤銷、刪除帳戶，只對自己啟動的伺服器跑）。結束碼：0 全部通過、1 有檢查失敗。所有子程序（MyriagentHub、FakeDevice、`admin`）的環境變數剝掉 `CLAUDE*`、`ANTHROPIC_*`、`OPENAI_*`、`ANTIGRAVITY_*`、`GROK_*`、`XAI_*`、`MYRIAGENT_*`、`CODEX_*`、`ASPNETCORE_*`、`DOTNET_ENVIRONMENT`（同 `tools/fake-device/measure_sync.py`）；假裝置不執行任何 CLI、不連外網、不碰這台電腦真的帳戶（`tools/fake-device/README.md`）。

分頁都在同一個瀏覽器內容（同一份 cookie）：P（1440×900）、P2（390×844）、Q（1440×900）、R（1440×900，版本頁）。操作哪個分頁就先 `bringToFront()`：背景分頁的 `requestAnimationFrame` 停住，Playwright 等元素穩定，每次點擊會多等約 4 秒（實測），頁面本身的行為不變。

| 步驟 | 通過條件 |
|---|---|
| 取得序列化鎖 | 與 `hub-e2e.mjs`、`gateway.mjs` 共用，見上面「序列化鎖」 |
| 啟動 MyriagentHub | `/healthz` 200；記下啟動期間新增的金鑰檔（算這個 MyriagentHub 的） |
| A 開始配對 | 背景執行 `FakeDevice --data <work>\fdA pair --server <base> --name E2E-A --dev-trust --no-approve --timeout 120`，30 秒內印出第一行 JSON：`verificationUri` 是 `<base>/#pair`、`verificationUriComplete` 是 `<base>/#pair=<userCode>`（Development 的 PublicOrigin 是 `https://localhost:<埠>`）、配對碼 `XXXX-XXXX` |
| 核准 A | 分頁 P 匿名開 `verificationUriComplete`：等 `__hubProbe.ready`，畫面 login、有「登入後核准這台電腦的配對」提示；以**表單**（`#login-user`、`#login-password`、`#btn-login`）用預設密碼登入 → 仍在 `/#pair=…`、畫面 pair（沒有被導到 `/app/`）、名稱 `E2E-A`、指紋等於裝置印的 `deviceFingerprint`；等 1.1 秒、滑鼠移到「核准」上（核准鈕在細節出現 1 秒後、有真的操作才解除停用，`aria-disabled` 變 `false`）→ 按「核准」→ **一律先跳密碼框**（剛登入過也一樣）→ 輸入預設密碼 →「已核准。那台電腦會自動完成配對」（`data-tone="ok"`）、有「開啟網頁版」；A 60 秒內結束碼 0、最後一行 `state:"Live"`、`drained:true` |
| B 開始配對 | 同上，`--name "<img src=x onerror=alert(1)>"`（XSS 夾具） |
| 核准 B | 分頁 P2（390×844）開 `<base>/#pair`：已登入直接是核准頁、沒有細節；**手動輸入**小寫加空白的配對碼（例如 `hbmz rslk`）→「查詢」→ 欄位顯示 `HBMZ-RSLK`、名稱照字面、核准頁沒有 `img`、`window.__xss` 沒定義、沒有任何對話框；截圖 `pair-b-390.png`、文件寬不超過 390、沒有元素超出右緣；指紋相符 → 核准（同 A：先跳密碼框、輸入預設密碼）→ B 結束碼 0、`state:"Live"` |
| 裝置金鑰找得到（正向對照） | 兩台 `sync\device.json` 的 `keyName`（`Myriagent.Device.…`）以 UTF-16LE 比對金鑰存放區的檔案內容，各正好 1 個檔（收尾時用同一個方法確認 unpair 之後不在） |
| 兩台跑劇本 | 背景執行 `run --script script-A.json --dev-trust`、`script-B.json`，90 秒內兩台都印出 `waitDrained` 那一步（outbox 清空）而且還在跑：A 加 Claude（`orgId:"org-e2e"`、`a@example.test`）與 Codex（`c@example.test`）、CLI Claude 2.1.0、Codex 0.50.0、各 `poll` 一次；B 加**同一個身分**的 Claude、CLI Claude 2.0.9、`poll`；兩台都在 20 秒後再 `poll` 一次（指派要等伺服器重算，負責的那台這時才上傳共享用量），最後 `wait 600000` 撐住在線 |
| 網頁版：矩陣、查用量、各電腦 CLI、電腦頁 | 分頁 P 點核准頁的「開啟網頁版」到 `/app/`：`#app.ready`、傳輸層 online 而且收到 init、狀態列隱藏、沒有 no-host；30 秒內矩陣兩欄都在線、Claude 列 A、B 都是 ready、Codex 列 A ready、B absent，**每一列在線的格子正好一格有「查用量」**（S12 在畫面上的證據）；Claude 卡片「2 台已登入（2 台在線）」；各電腦 CLI 兩台（A：Claude 2.1.0、Codex 0.50.0；B：Claude 2.0.9、Codex 未安裝）；電腦頁兩台都「在線」、B 的名稱在電腦頁與矩陣欄頭照字面；`img[src=x]` 0 個、`window.__xss` 沒定義、沒有對話框 |
| 共享用量（觀察） | 不判定：30 秒內 Claude 卡片有沒有圓環與來源標籤 |
| CSP 正向對照 | 分頁 P 的 `/app/` 插入 inline script：沒有執行、收集器正好多 1 筆 `script-src-elem`／`inline` |
| 手機寬度的儀表板 | 分頁 P2 開 `/app/`：連上、矩陣畫好；`scrollWidth` 不超過視窗、`#dashboard` 不水平捲動、`.mx-wrap` 以外沒有元素超出畫面；截圖 `dashboard-390.png`、`matrix-390.png` |
| 從網頁改電腦名稱 | 電腦頁 A 的「改名…」→ `E2E-A2`：`devices` 推回新名稱、仍標「配對時名稱：E2E-A」、矩陣欄頭也換了 |
| 從網頁改帳戶名稱、上移 | Claude 卡片選單「重新命名…」：`accounts` 推回新名稱（卡片與矩陣列）；順序原本是 Claude、Codex，Codex 的「上移」之後變 Codex、Claude |
| 等近期驗證過期（只有 `--stepup`） | 等到最後一次輸入密碼的 65 秒後；兩台都還在線 |
| 撤銷 B | 電腦頁 B 的「撤銷…」→ 原因「退役」→「撤銷」（`--stepup` 時先跳密碼框、輸入預設密碼；不加時不能跳）→ 200；矩陣的 B 欄消失（記下回應到消失的毫秒數，SEC-13 只記錄）、電腦頁 B 排最後「已撤銷・退役・時間」、Claude「1 台已登入（1 台在線）」；結束 B 的劇本程序、等它真的結束，再跑 `status --dev-trust --wait 5`：結束碼 0、`state` 不是 `Live`（實測 `Stopped`、「權杖已撤銷，請解除配對後重新配對」） |
| 從目錄刪除 Codex | Codex 卡片選單「從目錄刪除…」：確認框含「電腦之後再回報同一個登入身分時，這個帳戶會再出現」→ 200；卡片與矩陣列消失，3 秒後仍沒有（剩 1 張卡） |
| 登出 | 另開分頁 Q 的 `/app/`（連上）；P2 回到 `/`（帳戶頁）按「登出」→ 回到登入頁；P 與 Q 5 秒內被導到 `/` |
| 排空：shutdown、draining、重連 | `MyriagentHub.exe admin drain --data <work>\hub-data --wait 15` 結束碼 0 → 15 秒內 A 的桌面記錄（`fdA\logs\*.log`：桌面的 `Log` 寫檔、不印在標準錯誤）多一行「伺服器通知即將停機」（收到 `ServerNotice{shutdown}`）→ 結束 A 的劇本程序、以同一個資料根重新啟動 `run --script script-A2.json --dev-trust --timeout 90`（劇本：`waitDrained` 再 `wait 600000`，之後的步驟要 A 在線；結束碼 3＝實例鎖還沒放開時每秒重試、最多 10 秒）→ 20 秒內記錄多一行「伺服器回應不認得的錯誤（draining）」（排空中的 Hello 回 `draining`，U3 之前的桌面當成連線錯誤照退避重試），這時劇本還沒開始（在等連線）→ `admin undrain` 結束碼 0 → 40 秒內標準錯誤印出劇本第 1 步 `waitDrained`（`drained:true`）而且沒有「還沒連上伺服器」（`run` 先等到 Live 才跑劇本），程序還在跑；記下 drain 到收到通知、重新啟動到 draining、undrain 到回到 Live 的毫秒數 |
| 稽核筆數 | P2 以 `__hubProbe.login` 再登入 → `MyriagentHub.exe admin verify-audit` 結束碼 0 → `GET /api/v1/audit?limit=200`，筆數剛好：`owner.created`×1、`auth.login` ok×2／denied×0、`pair.started`×2、`pair.approved`×2、`pair.completed`×2、`pair.denied`×0、`device.renamed`×1、`account.updated`×1、`account.reordered`×1、`device.revoked`×1、`account.deleted`×1、`auth.logout`×1、`auth.step_up` ok×（核准 A、B 各 1，`--stepup` 時撤銷再 1：3，否則 2）／denied×0、`prefs.set`×0、`admin.command`×3（排空那一步的 drain、undrain 與這一步的 verify-audit）、`server.drain`×0（管理指令的排空記成 `admin.command`；`server.drain` 只在部署的排空逾時自動解除時寫）；沒有任何不是 ok 的；`chain.ok`；回應全文沒有密碼。`device.hello`、`account.linked` 只記錄（WP3.5 實測各 4、3；U2 起 A 在排空那一步重新啟動，`device.hello` 多一筆，實測 5、3） |
| 版本頁 | 另開分頁 R 的 `<base>/#releases`（同一份 cookie，已登入）：`__hubProbe.view` 是 `releases`、網址仍是 `/#releases`；版本表顯示「還沒有收到任何版本」、沒有任何列，`__hubProbe.releases()` 的 `releases` 是空的、`latest` 是 null；電腦表正好 1 台 A（B 已撤銷，不列），A 的 app 版本（畫面與 `releases()` 的 `appVersion`）等於第 13 步稽核裡 A 最新一筆 `device.hello` 的 `appVersion`（不斷言在線）；分頁 R 沒有 CSP 違規；截圖 `releases.png`。≥400 的回應不多（下表不變） |
| 主控台只有預期的錯誤 | 所有分頁的 `console.error`／`pageerror` 只有 CSP 正向對照那一筆與下表的回應（瀏覽器記成「Failed to load resource」）；≥400 的回應和下表逐項比對、次數剛好；收集器的 CSP 違規只有正向對照那一筆；整個流程沒有任何對話框 |
| 收尾：結束假裝置、解除配對 | 不論成敗：結束還在跑的假裝置（`kill`，10 秒沒結束再 `taskkill /T /F`）並等它們真的結束；兩台有 `sync\device.json` 就 `unpair`（結束碼 3＝實例鎖還沒放開時每秒重試、最多 10 秒），只剩 `pending-key.json` 就跑一次 `status`（配對清理會刪掉金鑰）；之後兩個檔都不能在 |
| 停止 MyriagentHub | `admin stop` 結束碼 0、伺服器結束碼 0 |
| 主機副作用 | 這個 MyriagentHub 啟動期間新增的金鑰檔停止後都不在；兩台的裝置金鑰（以名稱比對內容）解除配對後都不在。之後才出現、含 `Myriagent.Device.` 但不是這兩把的只記成觀察 |
| 結果檔與記錄不含密碼 | 結果檔（寫出後再讀回確認一次）、`hub-console.log`、`hub-data\diag\`、假裝置每次執行的輸出與兩個資料根的 `logs\` 都搜不到預設密碼（搜尋器正向對照同 `hub-e2e.mjs`） |

### 預期的錯誤

| 分頁 | 回應 | 次數 | 為什麼 |
|---|---|---|---|
| P | `GET /api/v1/auth/me` 401 | 3 | 匿名開核准網址時 auth.js 一次；登出後伺服器關掉 P 的連線：傳輸層重連前一次、導到 `/` 之後 auth.js 一次 |
| P | `GET /api/v1/sessions` 401 | 2 | 匿名時與登出後被導到 `/` 時 auth.js 的 `renderAccount` |
| P2 | `GET /api/v1/auth/me` 401 | 1 | 按「登出」之後 auth.js 的 `refresh()`（開 `/#pair` 時已登入，沒有 401） |
| P2 | `GET /api/v1/sessions` 401 | 1 | 同上，`renderAccount` |
| Q | `GET /api/v1/auth/me` 401 | 2 | 登出後：傳輸層重連前一次、導到 `/` 之後 auth.js 一次 |
| Q | `GET /api/v1/sessions` 401 | 1 | 導到 `/` 之後 auth.js 的 `renderAccount` |
| P | `POST /api/v1/devices/<B>/revoke` 403 | 1 | 只有 `--stepup`：近期驗證過期，第一次撤銷回 `step_up_required` |

## 版本頁（releases.mjs）

app 自我更新 U2 的版本頁（`/#releases`：`src/MyriagentHub/wwwroot/releases.js`、`releaserules.js`，擁有者 API `Releases/AppReleaseOwnerEndpoints.cs`）在**有版本時**的瀏覽器端到端（U2 收尾的延後項目 OWNER-1；`pairing.mjs` 的版本頁那一步只看「還沒有收到任何版本」）。以 `lib/hub-process.mjs` 的 `startHub` 啟動本機 MyriagentHub（複製 bin 到 `%TEMP%\myriagent-tools\hub-<pid>-<n>`、`Testing` 環境、`127.0.0.1`、自動挑的高埠、file 憑證），版本頁的操作都是真的點擊與真的確認框。

```bash
dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release -nodeReuse:false -p:UseSharedCompilation=false
cd tools/hub-e2e
npm install
node releases.mjs                      # 約 15 秒
```

| 參數 | 預設 | 說明 |
|---|---|---|
| `--hub <MyriagentHub.exe>` | `<repo>\src\MyriagentHub\bin\Release\net10.0\win-x64\MyriagentHub.exe` | 要測的伺服器（wwwroot 要有 `releases.js`、`releaserules.js`，沒有就直接失敗） |
| `--work <資料夾>` | `%TEMP%\myriagent-tools\releases\<時間>` | 工作資料夾（要在 `%TEMP%` 底下）：`result.json`、`hub-data\`（資料根）、`downloads\`（點「下載」存下的檔）、`releases.png` |
| `--out <檔案>` | `<work>\result.json` | 結果檔 |
| `--headed` | 無頭 | 顯示瀏覽器視窗（除錯用） |

**版本怎麼來**：伺服器收件只認內建的 `ReleaseKeys.Production`，而且不能加任何信任別的金鑰的設定或指令（CLAUDE.md），所以不經收件匣：第一次啟動讓遷移建好 `hub.db`、`admin stop --data` 之後，以 `node:sqlite` 直接在 `db\hub.db` 的 `app_releases` 寫三列，並在 `app-releases\<release>\` 放三個檔。`AppReleaseStore.TryLoad` 只比 manifest 原文的 SHA-256、簽章檔的 kid 與 zip 的長度（不驗章、不解析 manifest），所以 zip 是決定性的位元組（SHA-256 計數器）、manifest 照 `ReleaseManifestRules.Build` 的欄位順序、簽章檔是格式正確的 `{kid,sig}`。三版：`0.2.300-a1b2c3d` 可用（40960 位元組）、`0.2.301-b2c3d4e` 已撤回（2000，另一把 kid）、`0.2.302-c3d4e5f` 可用（1536，最新）；收件時間各差 1 小時。之後以同一個資料根重新啟動。

| 步驟 | 通過條件 |
|---|---|
| 取得序列化鎖 | 見上面「序列化鎖」（持有期間每分鐘續期） |
| 第一次啟動、admin stop | `/healthz` 200；`admin stop --data` 結束碼 0、正常結束，伺服器自己刪了匯入的金鑰檔 |
| 種三個版本 | `app_releases` 原本是空的、寫入後正好三列 |
| 重新啟動 | `/healthz` 200；啟動對帳的保存刪掉撤回版的資料夾，可用的兩版留著 |
| 匿名開 `/#releases` → 表單登入 | 匿名時畫面 login、`__hubProbe.releases()` 是 null；以表單（`#login-user`、`#login-password`、`#btn-login`）用預設密碼登入後留在 `/#releases`、畫出版本表 |
| 版本表 | 依收件時間由新到舊（0.2.302、0.2.301、0.2.300）；每列的版本、build、提交（前 7 碼，`title` 是完整的 40 碼）、大小（`1.5 KB`、`2.0 KB`、`40.0 KB`）、收件時間（頁面同一個 `Intl.DateTimeFormat`）、狀態（`可用`／`已撤回`）、kid 都跟種的相符；「目前提供」只在 0.2.302；撤回的列有 `is-gone`、狀態的提示是「撤回時間 …」、動作欄是 `—`；可用的列有「下載」（`/api/v1/app/releases/<release>/package`、`download` 屬性）與「撤回」；電腦表顯示「還沒有配對任何電腦」 |
| API 與畫面一致 | `GET /api/v1/app/releases` 200：`latest` 0.2.302、順序與狀態、`zipBytes`、`kid`、`commit` 跟種的相同、`devices` 空的；`__hubProbe.releases()` 一致 |
| 點「下載」 | Playwright 的 download 事件：建議檔名 `myriagent-0.2.300-a1b2c3d.zip`、存下的檔與種的 zip 位元組相同 |
| 下載的回應標頭 | 頁面內 fetch：200、`Content-Type: application/zip`、`ETag: "<zip 的 SHA-256>"`、`Content-Disposition` 的檔名；本文 SHA-256 相同 |
| 撤回版的套件 | 410 `{"error":"gone"}` |
| 撤回時按取消 | 0.2.300 的「撤回」→ `confirm` 文字以「撤回 0.2.300-a1b2c3d？」開頭、說明「它不是目前提供的版本，目前提供的版本不變」→ 取消：沒有送出撤回、表與「目前提供」不變 |
| 撤回目前提供的版本 | 0.2.302 的「撤回」→ `confirm` 說明「它是目前提供的版本，改提供前一個可用的版本（沒有就不提供更新）」與「同名的版本也永遠不能再收」→ 確定：`POST …/withdraw` 200、帶 `X-Myriagent-Csrf: 1`；訊息「已撤回 0.2.302-c3d4e5f」（`ok`）；0.2.302 變成已撤回（有撤回時間的提示、沒有動作）、「目前提供」移到 0.2.300 |
| 撤回之後的 API | `latest` 0.2.300；0.2.302 `withdrawn`、`withdrawnAt` 是這次執行之後的時間；順序不變 |
| 剛撤回的套件 | 410 `gone`（資料夾留給下一次保存刪，只記錄） |
| 撤回 API 的其他結果 | 頁面內 fetch：再撤回一次 200 `alreadyWithdrawn:true`；不存在的 `0.2.999-abcdef0` 404 `not_found`；`not-a-release` 400 `invalid_release` |
| 稽核 | `GET /api/v1/audit?limit=200`（登入就是近期驗證）：`app.release.withdrawn` 正好 1 筆（`owner`、0.2.302、`ok`）——取消、再撤回與錯誤都不寫 |
| 重新整理按鈕 | 訊息「已重新整理」（`ok`）、「目前提供」仍是 0.2.300 |
| 截圖 | `releases.png`（只記錄） |
| 頁面乾淨 | CSP 違規 0 筆（收集器與 `__hubProbe.violations`）；主控台錯誤只有下面列的回應（瀏覽器記成「Failed to load resource」）；≥400 的回應逐項比對、次數剛好；對話框正好兩個（取消、確定），沒有非預期的 |
| 停止 | 先關瀏覽器內容、再 `browser.close()`（等 15 秒）；`admin stop --data` 結束碼 0、正常結束；金鑰存放區裡記著這份複製（`hub-<pid>-<n>\MyriagentHub.exe`）的檔 0 個；刪掉複製的 bin |

預期的錯誤（次數要剛好）：`GET /api/v1/auth/me` 401×1、`GET /api/v1/sessions` 401×1（匿名開 `/#releases` 時 auth.js 的 `refresh`）；`GET …/0.2.301-b2c3d4e/package` 410×1、`GET …/0.2.302-c3d4e5f/package` 410×1、`POST …/0.2.999-abcdef0/withdraw` 404×1、`POST …/not-a-release/withdraw` 400×1（都是刻意的）。這支在頁面內送的 fetch 帶 `X-E2E-Test: 1`，回應監看只把沒帶它的 `POST …/withdraw` 算成版本頁送的撤回。

結束碼：0 全部通過、1 有檢查失敗、2 用法錯誤。

## 瀏覽器遠端操作（remote-control.mjs）

第 4 階段 WP4.6（計畫 `docs/plans/2026-10-19-wp4.6-web-live.md` 步 13；設計 11.3）。本機 MyriagentHub（暫存資料根、自動挑的高埠、`Development`）＋一台 `tools/fake-device` 假裝置（不執行任何 CLI），以真的瀏覽器從網頁版遠端操作那台電腦的對話：清單與電腦徽章 → 核准卡片與串流延遲（S7）→ 送出（`commandResult` 比 `Cmd` 的回傳先到也對得上）→ `requestDigest` 與 stale → Grok 與 Antigravity 的對話（Grok／Antigravity 同步的 P5：標頭的 `provider`、`steer` 與權限選單照代理種類；Grok 的核准卡帶 `requestDigest`、執行中送出時送出鈕是「加入佇列」、只排隊不插入、這一輪完成後接著送；Antigravity 的對話沒有任何卡片、可以從網頁送出）→ 政策 View、電腦剛斷線（「重新連線中」，閘門照樣擋；MyriagentHub 以只在 Development、Testing 生效的 `--Hub:Browser:ReconnectGraceMs=5000` 把網頁內建的 60 秒寬限期縮成 5 秒，init 的 `remote.reconnectGraceMs`）與寬限期過後的離線、重啟後恢復 → 草稿換成正式對話（兩個分頁）→ 兩個分頁的通知與未讀 → 剪貼簿 → 上傳、檔案抽屜與 `/f/`、SEC-23、`dirsOnly` 選資料夾 → `admin stop` 後在同一個資料根重啟 → 載入全部 XSS 語料。操作都經網頁真正的程式碼與 DOM（點側欄、按卡片的「允許」、Enter 送出、Ctrl+N、迴紋針、檔案抽屜、資料夾選單、複製鈕）；只有 stale 的核准與通知的事件來源由頁面直接呼叫 `myriagentHubTransport.post`。

```bash
dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release -nodeReuse:false -p:UseSharedCompilation=false
dotnet build tools/fake-device -c Release -nodeReuse:false -p:UseSharedCompilation=false   # V-fd：改了 FakeScript.cs、Runtime\ 一定要重建
cd tools/hub-e2e
npm install
node remote-control.mjs                # 約 45 秒
```

| 參數 | 預設 | 說明 |
|---|---|---|
| `--hub <MyriagentHub.exe>` | `<repo>\src\MyriagentHub\bin\Release\net10.0\win-x64\MyriagentHub.exe` | 要測的伺服器（wwwroot 要有 `transport-hub.js`） |
| `--fake-device <FakeDevice.exe>` | `<repo>\tools\fake-device\bin\Release\net10.0-windows\win-x64\FakeDevice.exe` | 假裝置 |
| `--port <埠>` | 自動挑空的 | MyriagentHub 的 HTTPS 埠；重啟時用同一個埠 |
| `--work <資料夾>` | `%TEMP%\myriagent-tools\remote-control\<時間>` | 工作資料夾：`result.json`、`hub-console.log`（第二次啟動是 `hub-console-2.log`）、`hub-data\`、假裝置的資料根 `fd\`、劇本 `script-run1.json`～`script-run3.json`、假裝置每次執行的輸出 |
| `--out <檔案>` | `<work>\result.json` | 結果檔 |
| `--headed` | 無頭 | 顯示瀏覽器視窗（除錯用） |

沒有 `--base`（流程會配對、改政策、重啟伺服器，只對自己啟動的伺服器跑）。結束碼：0 全部通過、1 有檢查失敗。子程序的環境變數照 `pairing.mjs` 剝除。**開始時先檢查 `FakeDevice.exe` 存在而且修改時間不早於 `FakeScript.cs` 與 `Runtime\*.cs`**，否則印「先跑 V-fd」直接失敗（不取鎖、不啟動伺服器）：改了劇本操作卻忘了重建，會測到舊的 exe（計畫 F33）。

夾具：電腦名稱是 `E2E-PC <img src=x onerror=alert(1)>`、XSS 對話的標題是 `<img src=x onerror=alert(1)> XSS 語料`（清單、標頭、閘門說明、草稿晶片、通知都要照字面）。XSS 語料 210 筆是 Markdown（49 個危險網址各做連結與圖片、白名單以外的標籤、不合語法的寫法、Markdown 特有的寫法；改寫自 `tests/MyriagentHub.Tests/Rendering/ItemSanitizerXssCorpus.cs`，控制字元在執行時組、不放 NUL），以假裝置的 `chat` 劇本當成 210 則 Assistant 回覆：電腦以 Markdig 渲染、伺服器淨化、網頁顯示，走真的路徑（`seedConversations` 只種純文字）。剪貼簿：每個頁面的 `navigator.clipboard` 換成假的（`allow` 時記下並成功、`deny` 時丟 `NotAllowedError`），不碰這台電腦真的剪貼簿。

分頁都在同一個瀏覽器內容（同一份 cookie、localStorage）：L（登入）、P（清單到草稿）、C（`page.routeWebSocket` 轉送：扣住一次 `send` 的 `Cmd` 回傳到它的 `commandResult` 送進頁面之後）、Q（照 P 存下的版面開，草稿的第二個分頁）、A、B（通知）、M（剪貼簿到 XSS）、C2（`routeWebSocket` 丟掉一次 `send` 的 `commandResult`）、SEC-*（直接開媒體網址）、SEC-CTRL（正向對照）。各分頁的版面在頁面的腳本之前寫進 `myriagent.app.layout.v1`（每個分頁只寫第一次）。

| 步驟 | 通過條件 |
|---|---|
| FakeDevice.exe 不比原始碼舊 | 見上 |
| 取得序列化鎖 | 同上面「序列化鎖」 |
| 啟動 MyriagentHub | `/healthz` 200；記下啟動期間新增的金鑰檔 |
| 配對假裝置（自動核准） | `pair --name "E2E-PC <img…>" --dev-trust --owner-user admin --owner-password-env FD_OWNER_PW`（密碼只給這個子程序）：結束碼 0、`state:"Live"`、`drained:true`；裝置金鑰以名稱比對金鑰存放區正好 1 個檔（正向對照） |
| 假裝置劇本 | 背景 `run`：`seedConversations` 3 個舊對話（各 6 則、種子 20261021）→ `addAccount`、`cli` → `policy Control`（回讀 Control、fullAccess false、notify true）→ `chat` XSS 語料（211 則、Completed）→ `chat` 卡片對話（「寫入檔案」核准卡、不回應）→ `chat` 串流對話（「執行指令」核准卡、不回應，核准後 40 段、每段前 100 ms）→ `waitDrained`，之後 `wait 600000` 撐住在線 |
| 擁有者登入 | 登入頁的 `__hubProbe.login`（預設密碼） |
| 清單 | `/app/` 連上；清單 6 筆都是這台的；側欄 6 列都有電腦徽章、名稱照字面、在線；XSS 標題列照字面、列裡沒有 img；整頁 `img[src=x]`、`on*` 屬性、危險網址、可執行的標籤 0 個、`window.__xss` 沒定義 |
| 核准卡片帶 requestDigest、S7 | 點側欄開串流對話，按卡片的「允許」：`approve` 的 `requestDigest` 等於 `conversation` 給的、結果 done；之後 40 段 Assistant 每段第一次到達頁面的時間減項目的 `time`（同一台電腦的時鐘）**p95 ≤500 ms**，記 p50、p99、最大 |
| 送出 | P：Enter 送出 → done、輸入框清空、沒有說明；C：扣住 `Cmd` 的回傳，`commandResult` 先送進頁面 → 照樣 done、輸入框清空、沒有說明、User 項目出現 |
| requestDigest | 已核准的串流卡片再送原本的 digest → rejected／`stale`；卡片對話的卡片送改了最後一個字的 digest → rejected／`stale`、卡片仍在等待而且按鈕可按；按「允許」（正確的 digest）→ done、卡片不再等待 |
| 政策 View | 結束劇本程序、同一個資料根跑 `policy View`：輸入區說明逐字「在 E2E-PC <img…> 的設定中允許遠端操作」、送出鈕停用；Enter 之後字還在、沒有送出 `send` |
| 結束假裝置 | 說明逐字「電腦離線：E2E-PC <img…> 重新上線後才能送出」、送出鈕停用；側欄徽章 `data-online="false"` |
| 重啟假裝置 | 同一個資料根跑 `policy Control`：說明消失、送出鈕可按；送出留著的字 → done；徽章在線 |
| 草稿 | Ctrl+N：`newConversation` 帶這台的 `deviceId`（只有一台候選，不問）；標頭晶片「電腦：E2E-PC <img…>」、在線；等存下的版面有這份草稿再開 Q（也開著它）；P 送出第一則 → P、Q 都收到同一個 `replacedBy`，P 在同一個位置換成新對話（作用中）、Q 也換了；新對話的標題是第一則訊息 |
| 兩個分頁的 notify | A 看 X 後切到 Z、B 看 Y（兩頁都 `visible`）；B 對 X 送出 → A 收到 `notify completed`（標題、電腦名稱照字面）、X 在 A 的清單標未讀、有未讀點、頁內提示；A 對 Y 送出 → 看著 Y 的 B 收到 0 則、A 收到、Y 不標未讀。B 也看不到 X，照伺服器的規則也會收到 X 的 notify（只記錄） |
| 剪貼簿 | 最後一則語料按「複製」：伺服器回的 `clipboard` 等於劇本的原文、寫進（假的）剪貼簿 1 次、按鈕「已複製」；剪貼簿被拒時倒數第二則：退路框的文字等於原文、唯讀、沒有 img |
| 上傳 | 卡片對話按迴紋針選 2 個檔（文字、70,000 位元組的固定種子位元組）：PUT 201×2、晶片都已附加；`addUploaded` 的 size 與 SHA-256 等於本機算的、done；送出 done 後晶片清掉；電腦的 `attachments\` 裡兩個檔的 SHA-256 等於本機算的 |
| 檔案抽屜 | Node 在對話的工作資料夾放 `report.txt`、`evil.svg`、`evil.html`、`evil.png`（內容是 SVG）與 `picked\`；抽屜列出 5 項；`report.txt` 的「預覽或下載」框連結是 `fileRef` 的 `/f/…`（`rel="noopener noreferrer"`、`target="_blank"`）；GET 200、`text/plain; charset=utf-8`、內容相同、CSP 逐字等於 `MediaHeaderRules.MediaCsp` |
| SEC-23 | 正向對照：同一段 HTML 放進沒有 CSP 的空白頁偵測得到執行（主控台記號與對話框）；新分頁直接開 `evil.png` 的 `/m/`、`evil.svg` 與 `evil.html` 的 `/f/`：200、CSP 逐字等於 MediaCsp（含 `sandbox`）、`nosniff`、0 次執行 |
| dirsOnly 選資料夾 | 資料夾選單「瀏覽資料夾…」→ 挑選模式最上層有「目前的工作資料夾」→ `picked` →「選這個資料夾」：`listFiles` 都帶 `dirsOnly:true`、第一次 `path` 是 null（虛擬根）；`setFolder` 是 `picked` 的完整路徑、done；資料夾晶片換成 picked |
| admin stop 後重啟 | M 開一份草稿並打字、C2 送出一則（`commandResult` 被丟掉）；`admin stop` 結束碼 0、第一次啟動的金鑰檔都不在；同一個資料根、同一個埠重啟、build 相同 |
| 重啟後 | M、C2 60 秒內收到新的 `init`、重新 `open` 開著的對話；C2 的輸入區說明「結果不明，請看對話內容確認」；M 的草稿在同一個位置換成新的草稿、字還原；兩頁都沒有換版橫幅、沒有重新載入 |
| XSS 語料全部載入 | 按「載入較早的訊息」到沒有更多：對話裡 211 個項目；標頭標題照字面；`img[src=x]`、`on*` 屬性、危險網址（`javascript:`、`vbscript:`、`data:`、`livescript:`、`file:`）、可執行的標籤 0 個、`window.__xss` 沒定義；正向對照以外沒有任何對話框 |
| CSP 正向對照 | M 插入 inline script：沒有執行、收集器正好多 1 筆 `script-src-elem`／`inline`（之前 0 筆） |
| 主控台只有預期的錯誤 | 所有分頁的 `console.error`／`pageerror` 與 ≥400 的回應只有下表的；CSP 違規只有正向對照那一筆 |
| 收尾 | 不論成敗：結束假裝置、`unpair` 結束碼 0、配對資料不在 |
| 停止 MyriagentHub、主機副作用、密碼 | `admin stop` 結束碼 0；兩次啟動新增的金鑰檔與裝置金鑰都不在；結果檔、兩個 `hub-console*.log`、`diag\`、假裝置的輸出與 `logs\` 都搜不到預設密碼 |

### 預期的錯誤

| 分頁 | 錯誤 | 次數 | 為什麼 |
|---|---|---|---|
| L | `GET /api/v1/auth/me`、`GET /api/v1/sessions` 401 | 各 1 | 登入前開登入頁時 auth.js 查目前的登入 |
| M | `GET /t/<evil.png 的 key>` 404 | 1 | 內容是 SVG 的 `.png` 做不出縮圖（檔案抽屜的縮圖） |
| SEC-M-png、SEC-F-svg、SEC-F-html | `/favicon.ico` 的 `ERR_BLOCKED_BY_RESPONSE.NotSameOrigin` | 各 1 | 媒體頁有 CSP `sandbox`（不透明來源），瀏覽器自動要的圖示被 `Cross-Origin-Resource-Policy: same-origin` 擋下 |
| M、C2 | `ERR_CONNECTION_REFUSED`、WebSocket 連不上 | 1～3（不固定） | 伺服器停機期間（`admin stop` 到重新收到 `init` 的 2 秒內）傳輸層重連前的嘗試 |
| M | CSP `script-src 'self'` 擋下 inline script | 1 | CSP 正向對照 |

## 共用模組（lib/）與單元測試（WP4.9 步 7）

全流程與真的 exe 冒煙（WP4.9 計畫 `docs/plans/2026-10-20-wp4.9-e2e-perf-deploy.md`）共用的 Node 輔助；既有的腳本裡只有 `hub-e2e.mjs` 從步 14 起用 `lib/child-env.mjs`。純函式都有 `node:test` 單元測試：

```bash
cd tools/hub-e2e
npm test                               # = node --test test/*.test.mjs（約 6 秒；不啟動 MyriagentHub、不開瀏覽器、不碰使用者金鑰存放區）
```

Node 26 的 `--test` 把參數當成檔案或 glob，給資料夾（`node --test test/`）會找不到模組，所以寫成 glob。

| 檔案 | 內容 |
|---|---|
| `lib/stats-rules.mjs` | `percentile`（nearest-rank）、`parseStamps`（只認 `〔`＋13 位數字＋`〕`）、`summarize`、`sameOutcome`（不看時間、耗時、PID、埠）、`idFromCommandId`（同 C# 的 `RemoteConversationIdRules.FromCommandId`，測試用同一組向量） |
| `lib/guard-rules.mjs` | 正式站守門（D18）：`isAllowedServerUrl`（只收 https＋`127.0.0.1`／`localhost`／`[::1]`、拒絕任何 `example.com`）、`isProductionTarget`（`/readyz` 的 `environment` 是 `service`、憑證名稱含 `hub.example.com`、簽發者是公開 CA 任一成立）；`isExeStale`、`isUnderTemp`。測試表在 `test/guard-cases.json`，C# 的 `LocalHubTargetRules`（步 8）讀同一份 |
| `lib/delay-proxy.mjs` | 延遲代理（D9）：TLS 直通的 TCP 代理、只聽 127.0.0.1、每方向一條佇列；`setUpstream`、`hold`／`release`（`up`、`down`、`both`）、`stats()`。`stats().lag` 是**實際**加上的延遲：Windows 的計時器解析度預設約 15.6 ms，`delayMs: 20` 在這台實測每方向約 31 ms（往返約 62 ms），報告要記實際值 |
| `lib/hub-process.mjs` | `startHub`：把 MyriagentHub 所在資料夾複製到 `%TEMP%\myriagent-tools\hub-<pid>-<n>`、Testing 環境、127.0.0.1、file 憑證、`PublicOrigin` 預設是 base；`/healthz` 之後讀 `/readyz` 過守門。金鑰檔以 UTF-16LE 的 `hub-<pid>-<n>\MyriagentHub.exe`（任何對齊）與啟動前後的差集認；正常停止由伺服器自己刪、強制結束後由這裡刪。`adminStop` 沒有 `dataDir` 就丟例外（admin 不帶 `--data` 是正式資料根）；`killHard` 只用 PID。`restart({ extraArgs })`（步 16）：同一個資料根、同一個埠再啟動，`extraArgs` 有給就換掉多給的參數（FF6 以更低的 blob 配額重啟） |
| `lib/child-env.mjs` | 子程序剝掉的環境變數：代理與同步相關的、ASP.NET Core 環境，與 GC 設定的 `DOTNET_GC*` 和整個 `COMPlus_` 前綴（.NET 10 照樣採用 `COMPlus_gcServer` 等，會蓋掉 MyriagentHub 的 Workstation GC 與硬上限）；要給的 GC 設定以 `extra` 明確放回。清單和 `measure_sync.py` 的 `STRIP_PREFIXES`、hub-probe 的 `ChildEnvironment.cs` 完全相同（`test_measure_sync`、`FleetRulesTests` 比對；`pairing.mjs`、`remote-control.mjs` 各有一份同樣的清單） |
| `lib/tripwire.mjs` | D21：暫存資料夾的 `claude.cmd`、`codex.cmd`、`agy.cmd`、`grok.cmd` 墊片（寫標記檔、結束碼 9），`childEnv` 把它放在 PATH 最前面（保留原本 PATH 鍵的大小寫）；`verifyShims` 以 `where $PATH:<名稱>` 確認第一筆是墊片；`scanOwnTree` 以 `Win32_Process` 的父 PID 鏈（子程序要比父程序晚建立）找自己程序樹裡的 CLI。CreateProcess 不帶副檔名只補 `.exe`，墊片攔不到的由程序掃描抓 |
| `lib/lock-heartbeat.mjs` | 鎖續期（D10）：鎖檔的 `pid` 是自己才每 60 秒更新 mtime |
| `lib/harness.mjs`（步 14） | 全流程（`full-flow.mjs`、`real-exe.mjs`）的共用環境 `startEnv`（見下面「全流程」）；`openApp`、`front`、`postCmd`、`waitCommandResult`、`latestList`、`unexpected`、`createReport`、`buildFreshness`。步 18 加：`addDevice`（情境中途加一台，可 `sameSeedAs` 照抄另一台的種子）、`restartDevice`（同一個資料根重開一台：`hard` 以 PID 結束整棵樹、`beforeStart`、`appVersion`、`timeoutSeconds`）、`startAltHub`（以另一個組建開同一個資料根、新的高埠、同一個 PublicOrigin）、匯出 `login`、`killTree`。延遲代理在同一個 Node 程序裡，所以**不用任何 spawnSync**：子程序（假裝置、`admin stop`、程序掃描）一律非同步（`hub-process.mjs` 的 `adminStopAsync`、`tripwire.mjs` 的 `scanOwnTreeAsync`，步 14 加的；spawnSync 等幾秒時代理完全不轉送） |
| `lib/harness-rules.mjs`（步 14） | harness 的純函式：裝置設定的正規化（名稱、種子不重複、政策、假 Codex 情境；步 15 起種子可以是好幾段的陣列、`drain:false` 不等 outbox 清空）、假裝置的 `pair`／`run --control-stdin`／`unpair` 參數與第一份劇本、控制通道的一行、`missingFeatures`、`staleOutputs`、`parseFullFlowArgs`（測試在 `test/harness.test.mjs`）。步 18 加：`sameSeedAs`（種子照抄另一台、名稱與種子對已在跑的裝置也檢查）、`restartScript`、`ROLLBACK_BUILDS`／`rollbackBuilds`（三個舊組建的環境變數與 `--prev-ref`）、`--prev-ref <提交>` |
| `lib/flow-rules.mjs`（步 15） | FF1～FF3 的純函式：`latencySamples`（即時／持久、傳輸層／畫面、時間窗）、`worstSamples`、`judgeS7`（p95 ≤500、p99 ≤1000，樣本 ≥20、沒有漏的標記）、`backfillWindow`、`pickEvenly`、`compareIdOrd`、`countBy`、`counterDelta`、`unexpectedDrops`、`resultsPerCommand`、`ulid`、`containsSecret`（測試在 `test/flow-rules.test.mjs`） |
| `lib/flow-page.mjs`（步 15） | 頁面的記錄器 `flowRecorder`、讀記錄的函式、頁面裡的原始 SignalR 連線（`rawConnect`、`rawCmd`、`rawWait`、`rawReadAll`）、擁有者 API（`opsStatus`、`stepUp`、`auditSince`）、Node 端的框追蹤（`trackHubFrames`、`resultOrder`）；見下面「FF1～FF3」。步 16 加：記錄器的 `conversation` 另記草稿、開在哪台、標題（`ffDrafts`），框追蹤另記 `newConversation` 的 `accountId`、`deviceId` 與 `send` 的文字；`pageGet`（頁面裡以同源的 cookie GET，回狀態、標頭、大小、SHA-256、開頭的位元組）、`pageUpload`（頁面裡產生內容、`PUT /api/v1/uploads/{id}`） |
| `lib/media-rules.mjs`（步 16） | FF6 的純函式：確定的 PNG 與「看起來像 MP4」的夾具（`pngImage`、`mp4Bytes`、`seededBytes`）、`parseContentRange`、`statusOf`（≥400 的回應與主控台「status of N」的狀態碼）、`quotaPlan`（補到告警門檻、再傳會到拒收門檻、重啟時調低的配額）、`MEDIA_CSP`（測試在 `test/media-rules.test.mjs`） |
| `flows/common.mjs`、`ff1.mjs`～`ff11.mjs`（步 15～18、P5） | 全流程的情境模組：各自匯出 `env`（`startEnv` 的選項）與 `run(env)`（回不含時間與 id 的結果）。`common.mjs` 步 16 加 `sqliteRo`／`serverConversation`（非同步跑 `sqlite_ro.py`）、`localCommand`、`replicaOf`（假裝置的控制通道）。步 17：裝置的選項多 `codexEmail`（假 Codex 帳戶的信箱；兩台給 `FAKE_CODEX_EMAIL` 就連到同一個邏輯帳戶，遠端登入完成後身分也相符）與 `cli`（`{ Claude?: 版本, Codex?: 版本 }`：劇本的 `cli` op，宣告裝了假 CLI）；結果檔的路徑在 `env.resultFile`。步 18：`sqliteRoAt`（指定資料庫，例如假裝置自己的 `sync.db`）。P5：裝置的選項多 `providers`（`['Grok', 'Antigravity']`：另外加的帳戶，別名 `{小寫名稱}-grok`／`-agy`、信箱 `{小寫名稱}-grok@example.test`／`-agy@example.test`），`cli` 收四種；頁面記錄器（`flowRecorder`）的快照與 `header` 多記 `provider`、`steer`、`permission`、權限選單（`modes`）與 `lastTurn` |
| `lib/sqlite_ro.py` | 同步資料庫的只讀檢查（`file:…?mode=ro`）：`device <author>`（deviceSeq、間隙、被擋下的種類與原因）、`rejected [<author>]`、`conversation <id>`（步 16：政策、`item_count`、`max_ord`、刪除、items 與 `conversation_runtime` 的列數、`media_refs` 依用途與縮圖、項目欄位 blob 的個數與位元組）、步 18 加 `rejected-rows <author> [<after>]`（被拒列的 deviceSeq、種類、原因、對話、payload 是不是 `{}`）、`command <browserCommandId>`（遠端指令的狀態、durable）、`leases`（對話的 `lease_state` 計數、recovering 的 id）、`conversations`（還沒刪除的對話：擁有者、政策、標題、項目數）、`publish-blocks`（**桌面**的 `sync.db`：`publish_meta` 被封鎖的對話）、`--self-test`。一律 `python -X utf8` |

## 全流程（full-flow.mjs，WP4.9 步 14 起）

第 4 階段的全流程端到端：瀏覽器 → 本機 MyriagentHub（經延遲代理）→ 兩台假裝置（OFFICE、HOME，假 CLI）→ 回到瀏覽器。只補 `remote-control.mjs`（單台裝置、網頁面的流程）沒有的多裝置、回補、持久性、回滾、帳戶與本機政策（計畫 D22）。步 14 是骨架與 `smoke`，步 15 加 FF1～FF3（`flows/ff1.mjs`～`ff3.mjs`），步 16 加 FF4～FF6（`flows/ff4.mjs`～`ff6.mjs`），步 17 加 FF7、FF8、FF10（`flows/ff7.mjs`、`ff8.mjs`、`ff10.mjs`），步 18 加 FF9（`flows/ff9.mjs`），Grok／Antigravity 同步的 P5 加 FF11（`flows/ff11.mjs`）。

```bash
cd tools/hub-e2e
node full-flow.mjs --only smoke --repeat 2              # 先建置 MyriagentHub（slnx）與 tools/fake-device 的 Release
node full-flow.mjs --only FF1,FF2,FF3 --repeat 2        # 步 15 的驗證（約 8 分鐘）
node full-flow.mjs --only FF4,FF5,FF6 --repeat 2        # 步 16 的驗證（約 3 分鐘）
node full-flow.mjs --only FF7,FF8,FF10 --repeat 2       # 步 17 的驗證（約 5 分鐘）
MYRIAGENT_SYNC_OLD_HUB=… MYRIAGENT_SYNC_PREV_HUB=… MYRIAGENT_SYNC_DEPLOYED_PREV_HUB=… node full-flow.mjs --only FF9 --repeat 2 --prev-ref <提交>   # 步 18 的驗證（見下面「FF9」）
```

| 參數 | 預設 | 說明 |
|---|---|---|
| `--only <名稱,…>` | 全部（`smoke`、`FF1`～`FF11`） | 不認得的名稱是用法錯誤（結束碼 2） |
| `--repeat N` | 1 | 每一次都是全新的環境；最後以 `sameOutcome`（不看時間、耗時、PID、埠）比對每一次的情境與收尾結果 |
| `--work <資料夾>` | `%TEMP%\myriagent-tools\full-flow\<時間>` | 要在 TEMP 底下；每一次的每個情境在 `run-<n>\<情境>\`：`hub-data\`、`OFFICE\`、`HOME\`（假裝置的資料根）、劇本、每個子程序的 `.stdout.log`／`.stderr.log`、`tripwire\` |
| `--out <檔案>` | `tools/hub-e2e/out/full-flow-<時間>.json` | 結果檔（每一步先寫；`runs[].scenarios.<情境>.steps` 是每一步、`runs[].outcomes` 是 `sameOutcome` 比的結果、`runs[].measurements` 是 S7 的樣本摘要；`out/` 不進 repo） |
| `--hub`、`--fake-device` | Release 的輸出 | 要測的 MyriagentHub.exe、FakeDevice.exe |
| `--headed` | 無頭 | 顯示瀏覽器（除錯用） |
| `--prev-ref <提交>` | 沒有 | FF9 (b) 的 DEPLOYED_PREV 是哪個提交（7～40 位十六進位，只拿來記錄；組建本身由 `MYRIAGENT_SYNC_DEPLOYED_PREV_HUB` 指定）。沒給時那一輪記「未執行」、FF9 不通過 |

沒有 `--base`（給了是用法錯誤）：只對自己以暫存資料根啟動的伺服器跑。結束碼：0 全部通過、1 有檢查失敗、2 用法錯誤、130 被 Ctrl+C 中斷（照下面的 `env.close()` 收完才結束）。

**共用環境**（`lib/harness.mjs` 的 `startEnv`；`real-exe.mjs` 也用）依序：

1. MyriagentHub 與 FakeDevice 的建置不比原始碼舊：比的是編譯出來的組件（`MyriagentHub.dll` 對 `src/MyriagentHub/**/*.cs` 與嵌入的桌面 `Web/**`、兩邊的 `Myriagent.Protocol.dll`、`FakeDevice.dll` 對 `tools/fake-device/**/*.cs`、FakeDevice 旁的 `Myriagent.dll` 對 `src/Myriagent/**/*.cs`、`wwwroot` 逐檔）；舊了就失敗、不取鎖、不啟動任何程序。不比 apphost（exe）：參考的組件只改了內部時 `FakeDevice.dll` 不重新編譯，exe 的時間不代表新舊。
2. 取得序列化鎖，每 60 秒續期（`lib/lock-heartbeat.mjs`）。
3. tripwire：墊片放在子程序 PATH 最前面，`verifyShims` 通過才啟動任何子程序；之後每 2 秒非同步掃一次這個 Node 程序底下的整棵樹。
4. 延遲代理（每方向 20 ms；這台實測約 28 ms）＋MyriagentHub（`lib/hub-process.mjs`：複製到 `%TEMP%\myriagent-tools\hub-<pid>-<n>`、Testing 環境、127.0.0.1、限流關閉、`Hub:PublicOrigin=https://127.0.0.1:<代理埠>`）；經代理讀 `/readyz` 與憑證過正式站守門。埠被搶走時換埠重試，最多 3 次。
5. 每台假裝置：`pair --server https://127.0.0.1:<代理埠> --owner-origin <同一個>`（aud＝PublicOrigin；擁有者自動核准，密碼只經環境變數）→ `run --script <劇本> --control-stdin`（種子對話、帳戶、政策、等 outbox 清空），等控制通道的 `ready`。預設 OFFICE 種 3 個、HOME 種 2 個對話（種子不同：同種子的第一個對話 id 相同，後上傳的 `not_owner`），政策都是 Control、OFFICE 允許完全存取、HOME 不允許。
6. 瀏覽器（本機 Chrome、無頭）：`tools/ui-probe/hub-page.mjs` 的 `probeInit` 掛在每個頁面（CSP 違規經 `exposeBinding` 送回、XSS 計數、傳輸層訊息記錄）；擁有者以預設密碼在 `https://127.0.0.1:<代理埠>/` 登入（cookie `__Host-myriagenthub` 的網域是 `127.0.0.1`，Secure、HttpOnly、SameSite Strict）。操作分頁之前一律 `bringToFront()`。

`env.close()`：先寫結果檔 → 有時限地關瀏覽器 → 每台假裝置 `quit`（等 outbox 清空）、`unpair`；只剩 `sync\pending-key.json`（配對到一半就失敗）的那台改跑一次 `status`（啟動時的配對清理刪掉金鑰，不連線），之後 `pending-key.json` 與那把金鑰都不能在 → `admin stop --data`（30 秒內沒結束才以 PID 強制結束、刪掉這個實例的金鑰檔）→ `startAltHub` 開過的其他組建（FF9）還在跑的也停掉（還在跑就記失敗）→ 關代理 → 裝置金鑰（`device.json` 或 `pending-key.json` 的 `keyName`）與每一份 MyriagentHub 複本（含 `startAltHub` 的）的 needle 都找不到檔 → tripwire（墊片沒被呼叫、掃過至少一次、樹裡沒有 CLI）→ 工作資料夾的記錄與結果都不含擁有者密碼（UTF-8 與 UTF-16LE）→ 放鎖。`env.close()` 只跑一次，重複呼叫拿到同一個結果。

**Ctrl+C**（WP4.9 合併前審查）：`lib/harness.mjs` 一載入就註冊 `SIGINT`／`SIGBREAK`，中斷時對每個還開著的環境照上面的 `env.close()` 收尾（每個最多 2 分鐘；`real-exe.mjs` 先以記下的 PID 結束真的 exe 的整棵樹，最多 30 秒），寫出部分結果之後以結束碼 130 結束。收尾期間再按一次 Ctrl+C 就直接結束（不收尾，子程序與金鑰可能留下，照「金鑰檔的歸屬」手動清）；正在啟動的環境看到中斷就不再啟動下一個程序。只以自己記下的 PID 結束程序，不用映像名稱。

**smoke**：兩台都協商出 `conv1`、`live1`、`cmd1`、`media1`（假裝置的 `status`）；`/app/` 在 127.0.0.1 來源連上；清單有兩台的種子對話；對兩台各送一個 `rename`（`cmd1` 的遠端指令，信封的 aud 是 PublicOrigin＝配對網址，計畫 R35）→ `commandResult done`、清單的標題跟著變；主控台、≥400 的回應、CSP、對話框、XSS 計數都乾淨（預期的只有登入頁的匿名 401 兩筆）。

### FF1～FF3（WP4.9 步 15）

每個情境各自一個全新的環境（`startEnv` 的選項由情境模組的 `env` 給：裝置與種子、`initOpts`、`initScripts`），收尾之後下一個情境再取鎖。頁面一律掛 `lib/flow-page.mjs` 的 `flowRecorder`（接在 `probeInit` 後面，`probeInit` 以 `lightMessages` 只記型別）：

- **時間標記**（D8）：每個（對話, 標記）第一次經傳輸層收到（`ws`）、第一次出現在完成的項目裡（`wsDone`）、第一次出現在畫面上（`dom`，MutationObserver 看 `.conv[data-conv] .it`）、第一次出現在不是執行中的項目上（`domDone`）。即時那一條看 `ws`／`dom`，持久那一條看 `wsDone`／`domDone`；S7 以畫面（`dom`）判，`requestAnimationFrame` 停住時改用 `ws` 並在結果寫明（網頁以 rAF 合併畫面的更新，同一個瀏覽器內容裡只有前景的分頁跑 rAF，所以 FF2 的第二個頁面開在另一個內容：`addBrowserContext`，另一個工作階段）。
- 項目的最新狀態（`kind`、`status`、`seq`〔瀏覽器版是 Ord〕、`requestDigest`）、每個對話最近一次的 `conversation` 與 `olderItems`、精簡的對話清單、`commandResult`、`toast`、輸入區的佇列長度、標頭的 `running`。
- `rawConnect`：在頁面裡另開一條 `/hubs/v1/browser`（同一個工作階段的 cookie、本站 Origin；SignalR 的 JSON 握手、每 10 秒一個 ping、送 `ready` 等 `init`；**不送 ClientState**，所以它 open 的對話不算看得到）。FF1 拿來 `open`／`loadOlder` 抽樣的對話，FF2 拿來 open 沒人看得到的對話，FF3 拿來以同一個 commandId 重送。
- `trackHubFrames`：Node 端看 `/hubs/v1/browser` 的框（Cmd、Cmd 的回傳、`commandResult`；每條 WebSocket 一個編號），記 `commandResult` 與 Cmd 的回傳哪個先到。

**FF1 配對與回補（S19）**：HOME 種 30 個、OFFICE 種 300 個（100 個最近的、200 個超過 90 天的，兩段 `seedConversations`；每個對話 12 則，讓回補長過串流的開頭），HOME 先配對、OFFICE 後配對而且不等 outbox 清空（`drain:false`：回補在情境裡進行）。每 500 ms 讀一次 OFFICE 的 `status.backfill`；回補中在 OFFICE 開兩個新對話、瀏覽器以兩個窗格看著，串流即時那一條（一個執行中的項目，每秒 4 段、200 段）與持久那一條（每段一個完成的項目，每秒 2 段、100 段）。斷言：每個標記都收到；**標記的時間落在回補期間的**兩條 S7 都 p95 ≤500 ms、p99 ≤1 秒（`backfillWindow`）；回補結束、outbox 清空後 `ops/status` 的 `conversations.count`＝332（種子 330＋新對話 2）；抽 20 個對話（OFFICE 依更新時間平均挑 15 個、HOME 3 個、兩個新對話），原始連線 `open`＋`loadOlder` 到底拿到的項目 id 與 Ord 集合等於假裝置的 `ords`，至少一個用到 `loadOlder`（持久那個新對話有 103 則）。

**FF2 多裝置即時與 S7**：OFFICE 三個、HOME 兩個對話各每秒 4 段（各 240 段，合計 1,200 段，即時那一條）；HOME 另一個對話每秒一段完成的項目（60 段，持久那一條）。頁面 A（四個窗格：OFFICE 三個＋HOME 一個）、頁面 B（另一個瀏覽器內容：HOME 另一個＋持久那個）。先以一段固定回覆開好對話、開好頁面，再開始串流（所有標記都在有人看的時候產生）。斷言：每個標記都收到；即時與持久分開算，p95 ≤500 ms、p99 ≤1 秒；`liveDropped` 的增量只有 `final`、`stale`（持久版本先到、被持久版本作廢；`rate`、`dlp`、`notOwner`、`policy` 都不該有）；之後 HOME 串流一個只在原始連線上 open 的對話：HOME 的 `liveRelay.sent` 增量 0（不在 `WatchChanged` 裡）、持久的完成項目照樣到；同一個計數器在有人看的那一段（上面的串流）兩台的增量都要大於 0（陽性對照，WP4.9 合併前審查：否則計數器沒在數也會得到 0）。

**FF3 遠端操作一輪**：OFFICE 一個新對話、三份 `nextRemoteTurn` 劇本，同一個瀏覽器內容的分頁 P、Q 都開著它。P：真的鍵盤送出 → 核准卡（Q 看到同一張、同一個 digest）按「允許」（真的點擊；approve 帶的 `requestDigest` 等於項目的）→ 以 `answer` 回答含秘密題目的問卷 → 收到 3 段以上就 `stop` → 再送一則、執行中又送一則（輸入區的佇列一則，第二輪完成後接著送）。之後 Q 照它看到的 digest 送 approve、再以 P 那個 approve 的 commandId 在原始連線上重送：兩個都 `rejected／stale`（「這張卡已在其他地方處理、內容已經改變，或 CLI 已重新啟動」）。斷言：假裝置 `status.session` 的增量正好是送出 3、停止 1、回答 1、核准 1；每條連線每個 commandId 至多一則 `commandResult`（伺服器不對 bcid 去重，WP4.5 D21：重送是新的指令、結果只給發起的那條連線）；`stepup` 之後讀稽核，`commands.issued` 的增量＝`command.issued` 筆數＝8、`rejectedAtServer` 0；假裝置的 `remoteAudit` 8 列、指令 id 的集合等於稽核的 `target`；秘密回答（執行時組的字串）不在 `hub-data\` 底下任何檔（db、`-wal`、`-shm`）與伺服器的主控台記錄裡（UTF-8、UTF-16LE），正向對照（送出的訊息裡的標記）在 `sync.db-wal` 找得到；畫面上沒有秘密回答。

FF1 第一次試跑（機器負載不明的時段）回補拉長到約 138 秒，回補期間 p95 445／460 ms 照過，但 p99 1,204／1,541 ms 超過 1 秒；之後三次（回補約 61～63 秒）p99 都在 400 ms 以內。重現不了，記在計畫附錄 B 步 15 列。

### FF4～FF6（WP4.9 步 16）

假裝置這一邊的操作經控制通道的 `localCommand`（這台的使用者在桌面上的操作：開關對話、改名、送出、改對話的同步政策、暫停同步）與 `replicas`（這台的複本），見 `tools/fake-device/README.md` 的「控制通道」。伺服器這一邊以 `lib/sqlite_ro.py conversation <id>` 只讀查 `sync.db`（非同步執行，不擋延遲代理）。

**FF4 草稿到新對話（多裝置部分）**：版面先開著 OFFICE 的一個對話（網頁沒有分頁時會自己開草稿）。在側欄 HOME 的帳戶卡按「用這個帳戶開新對話」（滑過才顯示）：只有 HOME 登入這個帳戶，選電腦直接是 HOME、不跳選擇框，`newConversation` 帶 HOME 的 `deviceId` 與這個帳戶的 logicalId，伺服器的草稿標頭的電腦是 HOME → 打第一則、按 Enter（真的鍵盤）→ `send` done、`conversationClosed` 的 `replacedBy`。斷言：新對話 id＝`idFromCommandId(cid)`（設計:3264），cid 是伺服器發給 HOME 的 `conversationCreate` 的指令 id（HOME 的 `remoteAudit` 一列 done，也是稽核 `command.issued` 的 target；瀏覽器 send 的 commandId 換算出來不等於新對話 id）；HOME 的假會話收到一次 send、清單上它在 HOME 底下、標題是第一則；HOME 的中樞裡是本機的對話（不是複本）、Ord 都配好也都發布過；OFFICE 的 `replicas` 有它（擁有者 HOME、標題是第一則），OFFICE 的中樞把它當成唯讀的複本。同一窗格與第二個分頁的 `replacedBy` 由 `remote-control.mjs` 第 8 段守。

**FF5 副本與同步政策**：HOME 一個對話 X（兩輪、4 則），OFFICE 的複本開著（`localCommand open`：只有開著的複本會拉項目）並拉到 4 則。對複本的寫入（OFFICE 本機的 `rename`、`send`）都跳「這是另一台電腦的對話（唯讀）」，OFFICE、HOME、伺服器的標題與項目、兩台假會話的 send 次數都沒變。網頁開著 X 之後 HOME 依序改政策：**metadata** → 伺服器 `items` 與 `conversation_runtime` 沒有列、`item_count` 0（`max_ord` 留著）、網頁 `open` 0 則、清單的標題還在、畫面 0 則、OFFICE 的複本政策 metadata、0 則；**full** → 伺服器、網頁 `open`（id 等於 HOME 的）、畫面、OFFICE 的複本都回到 4 則；**localOnly** → 伺服器 `deleted_at`、網頁清單沒有它、OFFICE 的複本整列拿掉、HOME 寫了伺服器刪除的旁檔；再改回 **full** → 跳「這個對話已從伺服器刪除，不能再同步；要同步請開新對話」、政策維持 localOnly（設計:3193）。

**FF6 媒體與檔案**：伺服器以 `--Hub:Media:QuotaBytes=16777216 --Hub:Media:MinFreeBytes=1048576` 啟動（配額調低；磁碟下限調低，判斷不受開發機的可用空間影響）。夾具在工作資料夾的 `media\`（不在任何資料根裡）：800×600 的 PNG 與 1 MiB「看起來像 MP4」的檔（`lib/media-rules.mjs`）。網頁送出一則，那一輪回圖片、影片與約 220 KB 的回覆。斷言：
- `/m/` 圖片 200、內容的 SHA-256 等於原檔、CSP 逐字是 MediaCsp；影片 `Range: bytes=0-1023` 206、`Content-Range: bytes 0-1023/1048576`、內容等於原檔的前 1024 位元組；超出檔尾的 Range 416、`Content-Range: bytes */1048576`；電腦傳上縮圖 blob（`media_refs.thumb_blob`；影片產不出縮圖，伺服器的 E10 每次重連都會再要一次）、`/t/…?s=320` 回 PNG。
- 超過 128 KB 的回覆走項目欄位 blob（`blob_refs` 的 item，約 230 KB）；網頁的項目不是佔位、畫面有最後一行（伺服器只留結尾渲染）。
- OFFICE 暫停同步（斷線）：`/m/` 圖片 200、`X-Myriagent-Fallback: thumb`、`Cache-Control: no-store`、內容等於 `/t/` 的縮圖；影片 503（沒有縮圖可退）；`open` 照樣拿到全部項目（P47:1265）。暫停一定在 `finally` 恢復（暫停中的電腦收不了尾）。
- 範圍外的路徑（`listFiles` 系統資料夾、`fileFetch` 的 win.ini、這台的 `device.json`、工作資料夾上一層的 `items.json`）都 `rejected／policy`，OFFICE 的遠端紀錄各一列（`rejected`、`policy`）。UNC 不測（檢查存在就會對外送 SMB 雜湊）。
- 上傳補到配額的 85% → `blobs.level` warn、告警 `blob_quota`；再傳會到 97% → 507 `insufficient_storage`、用量沒變；`admin stop` 之後以 `restart({ extraArgs })` 把配額調到「現在用量是它的 98%」重啟同一個資料根（用量照 `blobs` 表載入）→ `refuse`、告警 `blob_refuse`、任何上傳 507；網頁與兩台假裝置自己重新連上，網頁再送一則照樣 done、伺服器的項目增加、回覆到了（事件與對話照收，P47:1266、:1268）。
- 預期的錯誤（`env.expected`）：故意的 416、到配額的 507、OFFICE 離線期間的 `/m/`、`/t/`、重啟伺服器的停機期間（連線失敗與重連）；其他都算頁面不乾淨。

上傳的 SHA-256 與 SEC-23 由 `remote-control.mjs` 第 11 段守。

### FF7、FF8、FF10（WP4.9 步 17）

**FF7 遠端帳戶與 CLI**：OFFICE 帶假 Codex app-server 的 `evil_host`、HOME 帶 `ok`，兩台的 Codex 帳戶信箱都是 `device@example.test`（假 app-server 登入完成後 `account/read` 回的信箱：連到同一個邏輯帳戶，遠端登入完成後身分相符、不會自動登出）；HOME 另以劇本的 `cli` op 宣告裝了 Claude 2.1.0 與 Codex 0.50.0（假的，不會執行任何程序）。頁面要整則的訊息（`initOpts` 不用 `lightMessages`：帳戶、`devicesCli`、`accountLogin`），同一個瀏覽器內容的分頁 P、Q 都是儀表板。依序：
- 網頁 `accountRefresh` 兩台 → 兩格都是未登入（假帳戶起始是已登入，遠端登入的前置條件不收已登入的）。
- P 在矩陣按 HOME 那一格的「用裝置碼登入」（真的點擊）→ `accountLoginDevice` 25 秒內 `done`（P48:903）；P 收到一則帶假 app-server 驗證碼的 `accountLogin`、帳戶卡畫出大字驗證碼；Q 帶到前景之後，它收到的訊息與整份 DOM 的文字都沒有驗證碼、沒有 `accountLogin`。
- HOME 的 `fakeCodexComplete` → 那一格 `ready`、身分相符、畫面 `data-kind="ready"`。
- 以 UTF-8 與 UTF-16LE 掃驗證碼：伺服器資料根（db、`-wal`、`-shm`、記錄）、兩台的 `logs\`、工作資料夾的 `.log`／`.json`（伺服器主控台、假裝置的輸出與控制通道、劇本）、結果檔（`env.resultFile`；驗證碼不寫進結果）都沒有；正向對照：假 app-server 的腳本找得到（P48:990）。HOME 資料根的其他地方只記觀察。
- 對 OFFICE（`evil_host`：驗證網址的主機是 `auth.openai.com.evil.x`）送 `accountLoginDevice` → `rejected`（實測 `conflict`「Codex 回傳的登入資訊不合格，已取消」）、P 沒有收到任何 `accountLogin`、OFFICE 的假 app-server 開始了登入之後取消。
- 所有電腦登出：HOME 先暫停同步（伺服器看到離線）→ `accountLogout` 不帶 `deviceId` → P 只收到一則 `done`（設計:3260）；離線期間 HOME 沒有登出；恢復同步後 HOME 恰好登出一次（假 app-server 的 `logout` 子指令，再等 8 秒仍是一次）、那一格回到未登入；OFFICE（本來就未登入）不執行 CLI。
- `cliUpdate`：HOME 已裝的 Claude → `done`（＝已開始），更新跑到 `claude update` 時被假裝置拒絕（`refusedCli` 加 1）；OFFICE 沒裝 → `rejected／conflict`「那台還沒有安裝 Claude Code CLI，請改用安裝」。
- `cliCheck` HOME → `done`、`devicesCli` 兩種 CLI 的 `checkedAt` 都更新。放在 `cliUpdate` 之後：假 CLI 的執行檔不在磁碟上，檢查重新辨識之後就算沒裝（`installed:false`，只記錄），之後的更新會落進「還沒安裝」。
- 每一段的 `refusedCli` 變化逐一比對預期（只有 HOME 的 Claude 更新那一段是 1，其他都是 0：帳戶的登入、登出、檢查走帳戶管理員的假 app-server，不經 CLI 管理員）。

**FF8 本機政策與桌面安全**：HOME（Control、不允許遠端完全自動）兩個對話 c1、c2，OFFICE 一個對照；P 開著 c1、另有一條原始連線。
- `setOption permission FullAccess`：HOME `rejected／policy`（HOME 的遠端紀錄：閘門 `policy`、層級 2、`fullAccess:true`）；OFFICE（允許）同一個指令 `done`。
- 桌面專屬：`syncSetPolicy`、`syncSetConversationPolicy`、`syncPause`、`setting autoUpdateCli`、`setting instructions` 的 Cmd 回傳都是 `desktop_only`；HOME 的遠端紀錄沒有多任何一列（伺服器沒有轉給電腦）、裝置清單的政策沒變。
- 控制通道的 `setPolicy` 改成 View、再改成 Off：P 的裝置清單看到之後，輸入區的停用說明逐字等於「在 HOME 的設定中允許遠端操作」（WP4.6 的 `policyText` 與 WP4.8 的 `remoteAllowed` 共用的那一句，P48:926）、按 Enter 不送出任何 `send`、以 `postCmd` 直接送的 `send` 回 `rejected／policy`；改回 Control 之後說明消失。
- 遠端刪除 c2（`delete`）→ `done`；資料夾搬進 `conversations-trash\`，刪除前每個檔的 SHA-256 等於回收區裡的（SEC-27）；多出來的只能是伺服器確認刪除之後寫進去的 `sync-server-deleted.json`；伺服器上標為刪除。
- 控制通道的 `restoreFromTrash` → 搬回來、有 `sync-server-deleted.json`（只留在這台）、原本的檔內容不變、伺服器上仍是刪除、網頁清單沒有它（設計:3234）。
- 控制通道的 `unpair`（步 17 新增）→ `{資料根}\remote-audit\{deviceId}.jsonl` 存在、每行都是 JSON、有這一段的 `setOption`、`send`、`delete`（P44:1135）。

**FF10 撤銷**：HOME 一個對話，P 開著它、另有一條原始連線。`stepup` 之後 `POST /api/v1/devices/{HOME}/revoke {reason:"retired"}` → 200；同時每 50 ms 讀一次 HOME 的同步狀態：回應之後 1 秒內離開 Live（斷線）；之後重連的 negotiate 401 → 同步停在 `Stopped／Unauthorized`「權杖已撤銷，請解除配對後重新配對」；對 HOME 的 `rename` 立即被拒，而且要是 `Cmd` 照收、`commandResult` 是 `rejected／not_owner`（撤銷的檢查；這時 HOME 已經斷線，只看「被拒」的話撤銷的檢查壞了也會以 `offline` 被拒而照樣通過，WP4.9 合併前審查改成要求代碼；實測約 0.2 秒）；原始連線 `open` 照樣拿到項目、標頭的狀態是「電腦離線」、畫面也有（設計:3111）；裝置清單標為撤銷（`retired`、離線）；再撤銷一次 409 `already_revoked`（預期的錯誤）。最後以控制通道的 `unpair` 刪掉 HOME 本機的配對資料（被撤銷的那台連不上，`quit` 等 outbox 清空只會逾時）。

### FF11（Grok／Antigravity 同步的 P5）

設計 `docs/plans/2026-10-22-grok-antigravity-sync.md` 的 P5：FF3 的同一套做法換成 Grok 與 Antigravity。OFFICE 另有 Grok（隔離帳戶）與 Antigravity（本機預設帳戶的形狀）帳戶、宣告四種假 CLI；HOME 照舊只有 Claude；假裝置不執行任何 CLI（tripwire 照常守）。OFFICE 開 Grok、Antigravity、Claude 各一個對話，P 三個窗格各開一個：

- 兩台都協商出 `prov1`；標頭的 `provider`、`steer`（Claude true；Grok、Antigravity false）、權限選單（Antigravity 只有自動接受編輯、規劃模式、完全自動，目前的權限是自動接受編輯）；HOME 有兩個對話的副本（`replicas`）；帳戶矩陣有兩種的邏輯帳戶（OFFICE 那一格）。
- Grok：送出 → 思考、待辦、工具 → 核准卡（真的點擊，`approve` 帶項目的 `requestDigest`）→ 計畫確認卡（`approvePlan` 帶 `requestDigest`）→ 串流中再送一則：送出鈕「加入佇列（這一輪完成後送出）」、輸入區的佇列一則、假會話的 `steers` 不變 → 這一輪完成後接著送、標頭的上一輪是劇本的用量（「輸入 2,345・輸出 67 tokens」）→ 再送一則、串流中 `stop`。
- Antigravity：送出 → 串流（項目裡沒有核准、問卷與計畫確認卡）→ 再送一則只排隊不插入 → 接著送完 → 再送一則、串流中 `stop`；遠端登出（`accountLogout` 帶 OFFICE）→ `rejected／policy`「Antigravity 沒有登出指令，請到那台的 agy 互動介面輸入 /logout」、`refusedCli` 不變。
- Claude（對照）：串流中再送一則 → 送出鈕「插入這一輪（…）」、假會話的 `steers` 加 1（中樞嘗試插入；假會話一律不插，照舊排隊、接著送）。
- 假會話的增量 `{sends: 8, interrupts: 2, approvals: 1, plans: 1, steers: 1}`；頁面乾淨。

Grok 的遠端登入、登出（會執行 `grok`）不在這裡：P3 以 `RemoteAccountRig` 的假 grok 在單元測試守。

### FF9（WP4.9 步 18）

持久性與回滾。OFFICE、HOME 兩台（HOME 種 4 個對話），網頁 P 開著 HOME 的 Y 與 OFFICE 的一個對話、Y 的輸入區打了字沒送出。五段依序在**同一個資料根**上跑（一段的例外記成失敗、伺服器還在就接著跑下一段）：

- **(a) 強制結束**：先等 HOME 清空（新對話的項目要等 `conv.created` 確認之後才發布）→ 代理暫停**下行**（伺服器照收、照提交，確認送不回去）→ HOME 串流 1,000 段（每段一個完成的項目）、outbox 積壓到 1,000 筆以上 → 瀏覽器對 OFFICE 送一個非 durable 的 `rename` → 再暫停上行 → 以 PID 強制結束伺服器 → 3 秒內以同一個埠重啟 → 放開代理。斷言：強制結束到重新啟動 ≤3 秒；兩台 Live、P 收到新的 init 都在 30 秒內；清空之後 `sqlite_ro.py device` 的每台 deviceSeq 正好 1..N（N＝假裝置 `status.outboxSeq.max`，0 遺失 0 重複；確認丟了的批次重送時由伺服器去重）、對話的項目數等於 HOME 的；那個 `rename` 在伺服器上是 `unknown`、OFFICE 的遠端紀錄沒有多、標題沒變（營運:576）；新開的分頁以原始連線 `open`＋`loadOlder` 讀到的 id 與 Ord 等於 HOME 的（S8 的瀏覽器那一半）。
- **(b) 回滾鏈**：對 OLD（`MYRIAGENT_SYNC_OLD_HUB`，<舊提交>）、PREV（`MYRIAGENT_SYNC_PREV_HUB`，<舊提交>）、DEPLOYED_PREV（`MYRIAGENT_SYNC_DEPLOYED_PREV_HUB`＋`--prev-ref`）各一輪：另開一個沒打字的分頁 Q → 暫停上行、HOME 新開對話 X（`conv.created` 在途，切換那一刻有在途批次）→ `admin stop` → 舊組建開同一個資料根（新的高埠、代理 `setUpstream`，網址與憑證不變）→ 兩台重連；P 收到舊版的 init、顯示換版橫幅「伺服器已換版本，重新載入以取得對應的網頁」、**不**重新載入（有沒送出的字），Q 自動重新載入（WP4.6 的換版規則）→ 回滾期間 HOME 把 X 改成 metadata、在 Y 串流帶標記的項目 → 舊伺服器新記下的被拒列只能是 `{}` 或 deviceSeq 不大於切換那一刻的最大值（K1 凍結的）→ 舊版 `admin stop`、它的金鑰檔都不在 → 新版開同一個資料根。斷言：收斂（X 在伺服器上是 metadata、0 個項目〔LIVE-1：先套轉換再補 created〕；Y 經瀏覽器讀到的 id 與 Ord 等於 HOME 的、標記在伺服器上；P 收到新版的 init）；X 改回 full 之後整份重送（伺服器的項目數等於 HOME 的）；新開的分頁與 P 的清單都等於伺服器的對話。沒設的組建那一輪記「未執行」、FF9 不通過。
- **(c) PRIV-R3-1/2**：HOME 暫停同步、在 C 串流帶標記的項目（待送列留在 outbox）→ 以 PID 結束 HOME → 以 Windows PowerShell 獨占 C 的 `meta.json`（`[IO.File]::Open(…, FileShare.None)`，等待用 `[Threading.Thread]::Sleep`）→ 重啟 HOME（`--timeout 10`：暫停中的那台不會 Live）：中樞沒有 C（`ords` 找不到）→ 解除暫停：C 新記下的被拒列都是 `{}`、伺服器 `db\` 的每個檔（含 `-wal`、`-shm`，UTF-8 與 UTF-16LE）都沒有標記 → 依 PID 結束 PowerShell → 重啟 HOME：C 回到中樞、照摘要重送，經瀏覽器讀到的 id 與 Ord 等於 HOME 的、標記在伺服器上。
- **(d) 還原**：`admin backup-now --wait 60` → `admin stop` → `admin restore --from <備份>`：伺服器還沒起來時每個還沒刪除的對話都是 `recovering`（`sqlite_ro.py leases`）→ 啟動 → 90 秒內都解除、對話的 id、政策與項目數等於還原前。還原撤銷所有瀏覽器工作階段（`RestoreService`）：P 自己回到登入頁（`/api/v1/auth/me` 401）→ 同一個瀏覽器內容重新登入 → P 回到 `/app/`。所以「P 的字還在」在 (d) 之前查。
- **(e) PRIV-R3-3**：第三台 OFFICE2 照抄 OFFICE 的種子配對（`sameSeedAs`：同 id 的對話伺服器上是 OFFICE 的）→ OFFICE2 的 `publish_meta` 有 OFFICE 種子數個 `not_owner` 封鎖 → OFFICE2 把其中一個 B 改成 localOnly、之後串流一段標記；OFFICE 另串流一段（正向對照）→ 伺服器 `db\*` 沒有標記、正向對照找得到、B 的擁有者還是 OFFICE → 以 `--app-version 9.9.9-ff9` 重開 OFFICE2：not_owner 封鎖照舊（程式錯誤的封鎖才在換版時解除，由引擎的單元測試守）、標記照樣不在。
- 最後頁面乾淨：停機（連不上、WebSocket 失敗）、舊版（404）、還原到重新登入（401）的時間窗以外，主控台錯誤、≥400 的回應、CSP、對話框、XSS 計數都是 0。

**舊組建**：照 `src/MyriagentHub/README.md` 的約定以 `git archive <提交>` 解到暫存資料夾、`dotnet build src/MyriagentHub -c Release`（沒有 `.git`，`/healthz` 的 build 是 `dev`）；環境變數是那份 `MyriagentHub.exe` 的完整路徑。DEPLOYED_PREV 是 Hub 主機上一版：第 4 階段收尾時主 session 以實際部署的上一版提交重跑（`--prev-ref <那個提交>`）。

```bash
export MYRIAGENT_SYNC_OLD_HUB=C:/<暫存>/hub-old/src/MyriagentHub/bin/Release/net10.0/win-x64/MyriagentHub.exe
export MYRIAGENT_SYNC_PREV_HUB=C:/<暫存>/hub-<舊提交>/src/MyriagentHub/bin/Release/net10.0/win-x64/MyriagentHub.exe
export MYRIAGENT_SYNC_DEPLOYED_PREV_HUB=C:/<暫存>/hub-<上一版>/src/MyriagentHub/bin/Release/net10.0/win-x64/MyriagentHub.exe
node full-flow.mjs --only FF9 --repeat 2 --prev-ref <上一版>
```

開發時可以用 `MYRIAGENT_FF9_PHASES=a,c` 只跑幾段（沒跑的段落記「未執行」、FF9 不通過；驗收一律全部跑）。

## 安全檢查（WP4.9 步 19）

計畫步 19 的安全腳本沒有做，原因與計畫的 10 項檢查由哪些既有的測試與腳本涵蓋，見 WP4.9 計畫（`docs/plans/2026-10-20-wp4.9-e2e-perf-deploy.md`）附錄 B 的步 19 列。

## 真的 exe（real-exe.mjs，WP4.9 步 20 的 R34）

假裝置涵蓋了 `AgentHub` 建構式裡登記的處理者，但 app 的 `IAgentHost`、系統匣提醒與同步的接線只有真的 `Myriagent.exe` 走得到（計畫 D23）。這支以真的 exe 連本機的 MyriagentHub 跑一次：

1. 受測的桌面建置（預設 `src\Myriagent\bin\Release\net10.0-windows\win-x64`）不比原始碼舊、`Myriagent.dll` 含 `MYRIAGENT_AGENT_PROBE_NO_CLI`（UTF-16LE；不認得這個開關的建置會找到並執行這台電腦真正的 CLI，不跑）；整個 bin 複製到工作資料夾的 `app\`。
2. `lib/harness.mjs` 的環境（延遲代理、本機 MyriagentHub、tripwire、正式站守門、擁有者登入），一台假裝置 `REAL`：配對、種一個對話與一個隔離的假 Claude 帳戶、政策 Control；網頁的清單看得到那個對話之後假裝置 `quit`（outbox 清空、配對資料留著）。
3. **同一個資料根、同一個 Windows 使用者**啟動真的 exe（`--data <資料根>`）：`MYRIAGENT_AGENT_PROBE=<工作資料夾>\probe`、`MYRIAGENT_AGENT_PROBE_SEND=0`、`MYRIAGENT_AGENT_PROBE_NO_CLI=1`、`MYRIAGENT_AGENT_PROBE_KEEP=1`、`MYRIAGENT_SYNC_DEV_TRUST=1`、空的 `CLAUDE_CONFIG_DIR`／`CODEX_HOME`（工作資料夾的 `empty-config\`），剝掉本 session 的環境變數、PATH 最前面是 tripwire 的墊片（`lib/real-exe-rules.mjs` 的 `appEnv`）。剛結束的假裝置還沒放開單一實例鎖時 app 會叫醒「既有的實例」後結束，等 1 秒重試、最多 15 秒。
4. 斷言：`<資料根>\logs\` 出現「啟動提醒：遠端操作是 Control（允許完全自動：False）」；清單上那個對話的電腦重新上線；網頁對它送 `rename` → `commandResult` done、伺服器（`sqlite_ro.py conversation`）與清單的標題都變了；頁面乾淨。
5. 只以這個 PID 結束真的 exe 的整棵樹（`taskkill /PID <pid> /T /F`，含 WebView2 的子程序），之後照 harness 收尾（假裝置 `unpair`、`admin stop --data`、裝置金鑰與 MyriagentHub 複本的金鑰檔、tripwire、密碼掃描）。Ctrl+C 時也是先這樣結束真的 exe、再照 harness 收尾（見全流程的「Ctrl+C」）。

系統匣的氣球不在 WebView 的截圖裡，由擁有者在 P4 目視。執行期間螢幕上會出現 app 的視窗與那個氣球（約 10 秒）。

```bash
node real-exe.mjs [--work <TEMP 底下>] [--out <結果檔>] [--app <Myriagent.exe>] [--hub <MyriagentHub.exe>] [--fake-device <FakeDevice.exe>] [--headed] [--wait-seconds N]
```

結果檔預設在 `out\real-exe-<時間>.json`；結束碼 0 全部通過、1 有檢查失敗、2 用法錯誤、130 被 Ctrl+C 中斷。只對自己以暫存資料根啟動的伺服器跑（不收 `--base`）。

## 步驟與通過條件

| 步驟 | 通過條件 |
|---|---|
| 取得序列化鎖 | 與 `gateway.mjs`、`pairing.mjs` 共用，見上面「序列化鎖」；取得之後才列金鑰檔（主機副作用的「執行前」） |
| 啟動 MyriagentHub | `/healthz` 200，記下 build |
| 載入登入頁 | `view`＝login、`<meta name="myriagenthub-page" content="auth">`、安全環境、密碼欄 `type=password`；未登入 `me()` 401；回應的 CSP 的 `script-src` 只有 `'self'`（沒有 `unsafe-inline`） |
| 密碼錯誤、帳號錯誤都是 401 | `login('admin', 錯誤密碼)` 與 `login('owner', 預設密碼)`（第 2 階段的帳號名＋正確密碼）都是 401 `login_failed`；仍在 login 畫面、`me()` 401 |
| 以預設密碼登入 | `login('admin', 'changeme2026')` 成功、畫面 account、`me().name`＝admin、`session.recentAuth`＝true、頁面「帳號」顯示 admin；cookie `__Host-myriagenthub` 是 HttpOnly、Secure、SameSite=Strict、Path=/、7 天後到期的持久 cookie；記下「之前就在的工作階段」（自己啟動的伺服器必須是 0 個；`--base` 時可能有別的瀏覽器或中途失敗的上一次留下的，之後的工作階段數都扣掉它們） |
| 登出（舊 cookie 也失效） | 登出前用同一份 cookie 從 Node 查 `/api/v1/auth/me` 200（正向對照）；`logout()` 後畫面 login、`me()` 401、**重送登出前的 cookie 401**（伺服器端工作階段已刪）、瀏覽器的 cookie 已刪 |
| 再登入、第二個瀏覽器也登入 | 擁有者再登入；新的瀏覽器內容（另一份 cookie）登入前是 login 畫面、以預設密碼登入後 account；擁有者看到這次的 2 個工作階段，非目前的那個就是第二個瀏覽器 `me().session.id` |
| 撤銷工作階段 | `revokeSession(第二個瀏覽器的)` → `{ok, self:false}`；第二個瀏覽器**下一個請求就是 401**、重新整理回到 login；擁有者自己的仍有效、這次的只剩 1 個（目前這個），清單裡沒有被撤銷的 |
| 等登入的額度（觀察，通常不出現） | 不是檢查：`--base` 連跑時這個時間窗的未登入登入次數不夠下一段用，等到最後一次使用的 61 秒後 |
| 改密碼（其他工作階段立即 401） | 第二個瀏覽器重新登入（這次的 2 個工作階段）；`changePassword(錯的目前密碼, 新)` 403 `password_failed`、`changePassword(預設, 7 字)` 400 `password_too_short`，兩次失敗後第二個瀏覽器**仍有效**（反向對照）；`changePassword(預設, 新)` → `{ok, revokedSessions}`，`revokedSessions`＝目前這個以外的全部（自己啟動的伺服器是 1）、第二個瀏覽器下一個請求 401；擁有者自己的仍有效、近期驗證 true、整台伺服器只剩 1 個 |
| 舊密碼不能登入、新密碼可以 | 第二個瀏覽器重新整理是 login；預設（舊）密碼 401 `login_failed`、仍在 login；新密碼登入成功、畫面 account |
| 再驗證 | `stepUp(錯的)` 403 `password_failed`、仍在登入狀態、`session.lastUvAt` 不變；`stepUp(新密碼)` → `{ok, recentAuthUntil}`、`lastUvAt` 變大、`me().session.recentAuthUntil` 與回應相同 |
| CSP | 正向對照前兩個瀏覽器的 `__hubProbe.violations` 都是 0 筆；`tryInlineScript()` 沒有執行、之後正好 1 筆 `script-src-elem`／`inline` |
| 跨站 Origin 的 POST 被拒 | 帶擁有者 cookie 的 `POST /api/v1/auth/logout`：錯誤 Origin（`https://evil.example`）、同站但沒有 `X-Myriagent-Csrf`、沒有 Origin 都是 403 `forbidden_origin`；另由 Playwright 攔截供應的 `https://evil.test/` 頁面自動送出表單 POST（瀏覽器實際帶 `Origin: https://evil.test`、沒帶 cookie）→ 403；正向對照：同一份 cookie、同站 Origin＋CSRF 標頭的 `POST /api/v1/auth/stepup`（目前的密碼）200 `{ok:true}`；擁有者仍在登入狀態 |
| 改回預設密碼 | `changePassword(新, 預設)` → `{ok, revokedSessions:1}`（以新密碼登入的第二個瀏覽器 401）；擁有者自己的仍有效 |
| 版本頁的模組載入失敗不影響登入頁與配對核准頁 | `auth.js` 以動態 import 載入版本頁的 `releaserules.js`（一開始）與 `releases.js`（第一次顯示版本頁）。另開四個瀏覽器內容，以 `route` 讓指定的模組請求失敗：兩個都失敗時，未登入的 `/#pair` 是 login 畫面、配對提示顯示、狀態列「請登入」，已登入（沿用擁有者的 cookie，不再登入、不多寫稽核）的 `/#pair=ZZZZ-ZZZZ` 是 pair 畫面、查詢回「找不到這個配對碼…」；只有 `releases.js` 失敗時，已登入的 `/#releases` 是 releases 畫面、狀態列「版本頁載入失敗，請重新整理頁面」、`releases()` 是 null；只有 `releaserules.js` 失敗時 `/#releases` 當成一般的片段（account 畫面）。每個情境 CSP 違規 0 筆，要了哪些模組、攔下哪些都剛好（正向對照；不是版本頁時一次都不要 `releases.js`）。這四個內容不監看（被攔下的模組會在主控台留下載入失敗），不算進下一步 |
| 主控台 0 個非預期錯誤 | 兩個瀏覽器的 `console.error`／`pageerror` 排除下表的預期項目後為 0；`response` 事件裡 ≥400 的回應也都在下表，而且刻意送的錯誤次數剛好（下表「次數」欄） |
| 未對應路徑 | 匿名 `/favicon.ico` 404、登入後 404（同一份 cookie 的 `/api/v1/auth/me` 200）、匿名 `/no-such-page` 404、匿名 `/api/v1/no-such-endpoint` 401；兩個瀏覽器的主控台與回應裡沒有任何 `/favicon.ico`（`auth.html` 以 `<link rel="icon" href="/app/favicon.ico">` 明確指定圖示）；登入頁的圖示匿名 `/app/favicon.ico` 200、`image/x-icon` |
| 媒體路徑 | WP4.7 D9：匿名 `/m/{鍵}`、`/t/{鍵}?s=320`、`/f/{鍵}`、`/api/v1/device/media/{鍵}?purpose=media` 都是 401；擁有者的 cookie 要格式合格但不存在的鍵，`/m/`、`/t/`、`/f/` 都是 404 `not_found`、`Cache-Control: no-store`；每個回應的 CSP 都是媒體的 `default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'; sandbox`、`X-Content-Type-Options: nosniff`（沒有電腦連著，只驗路由、授權與安全標頭） |
| 稽核紀錄（不含密碼） | 先 `MyriagentHub.exe admin verify-audit`（結束碼 0），近期驗證內 `GET /api/v1/audit?limit=200` 200；次數剛好（`--base` 時至少）：`owner.created`×1（執行者 system、目標 admin）、`auth.login` ok×5／denied×3、`auth.logout`×1、`session.revoked`×1、`owner.password_changed` ok×2／denied×1、`auth.step_up` ok×2／denied×1；`chain.ok` 且驗到這次最後一筆；**回應全文（含解開後的 detail）搜尋這次用過的每個密碼都沒有出現**，搜尋器另以「密碼包在 detail 形狀裡」的正向對照確認找得到 |
| 結束時登出 | `logout()` 後畫面 login、重送登出前的 cookie 401（第二個瀏覽器的已在改回預設密碼時撤銷，`--base` 的伺服器不留下這次的工作階段） |
| 停止 MyriagentHub | `admin stop` 結束碼 0、伺服器結束碼 0 |
| 主機副作用 | 這個 MyriagentHub 啟動期間新增的金鑰檔（啟動前與 `/healthz` 200 之後各列一次的差）停止後都不在（自簽憑證的暫時金鑰在正常結束時刪除）；之後才出現、現在還在的只記成「其他程序的金鑰檔」觀察（WP4.9 步 14 起；之前是整個存放區的前後差） |
| 結果檔與伺服器記錄不含密碼 | 結果檔（到這一步為止的全部內容，寫出後再讀回確認一次）、`hub-console.log`、資料根 `diag\` 的記錄檔都搜不到這次用過的密碼（正向對照同上） |

### 用到的密碼

- 預設密碼 `changeme2026`（伺服器的 `OwnerAccount.DefaultPassword`）；錯誤的登入密碼、錯誤的目前密碼、太短的新密碼（7 字）、新密碼、錯誤的再驗證密碼每次隨機產生，只用小寫字母、數字與連字號（伺服器的 JSON 不會跳脫這些字元，原文搜尋才不會漏），隨機部分不含十六進位字元（不會和雜湊、識別碼碰巧相同）。
- 密碼只在記憶體裡：主控台輸出與結果檔只寫密碼的名稱（「預設密碼」「新密碼」…）。
- 流程中把密碼改成新密碼、最後改回預設，所以 `--base` 的伺服器跑完維持原狀。中途失敗時密碼可能停在隨機的新密碼（不會寫在任何地方）：在伺服器上 `MyriagentHub.exe admin reset-owner --data <資料根>` 改回預設。`--base` 只收本機的開發伺服器（正式站守門，見上面的參數表），正式服務與經 ssh 轉送的一律以結束碼 2 拒絕。

### 主控台錯誤的排除規則

`auth.js` 的 `refresh()` 以 `/api/v1/auth/me` 與 `/api/v1/sessions` 的 401 判斷要顯示登入畫面（`renderAccount` 同時取兩個），瀏覽器會把這些網路回應記成主控台的「Failed to load resource」。只排除下列（路徑＋狀態碼都要符合），其他任何主控台錯誤都算失敗：

| 路徑 | 狀態 | 次數 | 原因 |
|---|---|---|---|
| `/api/v1/auth/me` | 401 | — | 未登入時判斷畫面、`me()` 確認已登出或被撤銷 |
| `/api/v1/sessions` | 401 | — | 未登入時 `renderAccount` 同時取 |
| `/api/v1/auth/login` | 401 | 3 | 刻意的錯誤密碼、錯誤帳號、改密碼後的舊密碼 |
| `/api/v1/auth/password` | 403 | 1 | 改密碼時目前的密碼錯誤（刻意） |
| `/api/v1/auth/password` | 400 | 1 | 新密碼太短（刻意） |
| `/api/v1/auth/stepup` | 403 | 1 | 再驗證的密碼錯誤（刻意） |
| （CSP） | — | — | 正向對照刻意插入的那一筆 inline script（只限擁有者頁面、內容含 Content Security Policy 與 inline） |

## 注意

- 速率限制照正式設定（沒有關）：登入類端點（`/api/v1/auth/login`、`stepup`、`password`）每分鐘 10 次（固定時間窗），未登入的以 IP、已登入的以工作階段分區：一次流程的未登入登入 8 次、每個工作階段的再驗證與改密碼最多 7 次，都不必等（腳本照樣記帳，不夠時等到最後一次使用的 61 秒後）；匿名登入嘗試每 IP 每小時 50 次，一次流程用 8 次；其他匿名請求（頁面、`/healthz`、未登入的 401）遠低於每分鐘 120 次。限流狀態在伺服器記憶體裡，每次自己啟動伺服器都是新的；用 `--base` 對同一個伺服器時，前後兩次至少隔一分鐘、一小時內最多 6 次，跑的期間別從同一台電腦做其他登入。
- 近期驗證 5 分鐘：撤銷工作階段、看稽核都在登入、改密碼或再驗證之後 5 分鐘內，所以不會跳再驗證對話框。**沒有涵蓋**：近期驗證過期後敏感操作回 403 `step_up_required`、網頁跳出密碼對話框再重試的流程（要等 5 分鐘或改設定，這支腳本不改正式設定）。
- 孤兒金鑰檔的檢查（WP4.9 步 14 起）只算這個 MyriagentHub 啟動期間新增的金鑰檔，見「金鑰檔的歸屬」。之前看的是整個使用者的金鑰存放區（`%APPDATA%\Microsoft\Crypto\Keys`）的前後差：同一台電腦上別的工作階段剛好在跑 MyriagentHub 的測試（Kestrel 測試也以 file 憑證模式匯入 PFX）時，會把它們的金鑰檔算進來而誤判失敗（2026-10-16 實際發生過一次；WP3.5 步驟 13 又碰到兩次，是另一個 worktree 的 `Sync.LongRun`／`Sync.WithHub` 的 testhost，多出的檔幾秒後就自己消失）。現在那種檔只記成觀察；MyriagentHub 啟動的那幾秒裡別人剛好匯入的仍會被算成這個 MyriagentHub 的（停止後它還在就誤判），失敗時先看執行期間有沒有別的 `testhost`／MyriagentHub，沒有了再重跑。
- MyriagentHub 與 `admin` 子程序的環境變數（WP4.9 步 14 起）剝掉代理與同步相關的、ASP.NET Core 環境與 GC 設定（`lib/child-env.mjs`，同 `pairing.mjs`）。
- 結果檔不含密碼，但含本機路徑；`hub-data\` 有資料庫（密碼雜湊、工作階段）與 Data Protection 金鑰。看完刪掉整個工作資料夾，不要提交。
- 第 3 階段以後（設計 11.3）：以 `tools/fake-device` 配對並核准、儀表板矩陣、撤銷裝置、近期驗證過期時的密碼框已由 `pairing.mjs` 驗證（錯誤 Origin 的 WebSocket 由 `gateway.mjs`）；第 4 階段 WP4.6 起，開啟對話與串流延遲、遠端操作（送出、核准與 `requestDigest`、政策與離線的閘門、草稿換成正式對話、通知與未讀、剪貼簿）、上傳、檔案抽屜與 `/f/`、`/m/` 的 SEC-23、伺服器重開後的重連與草稿重開、XSS 語料由 `remote-control.mjs` 驗證。WP4.9 起撤銷裝置後 negotiate 401 由 `full-flow.mjs` 的 FF10、`allowRemoteFullAccess`（完全存取模式）的遠端切換由 FF8 驗證；層級 2 操作的再驗證依擁有者 2026-10-19 指示（自用、伺服器可信任）不做。
