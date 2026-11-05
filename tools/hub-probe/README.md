# Myriagent Hub 探針與 SQLite 量測（tools/hub-probe）

第 2 階段 WP2.5（設計 `docs/plans/2026-10-10-multi-device-sync.md` 11.3、11.4、11.6）：研究原型 `MyriagentHub.ProbeClient` 與 `MyriagentHub.SqliteBench` 收進 repo，成為可建置的專案。兩個都是主控台程式，每個結果印一行 JSON（非 ASCII 字元跳脫成 `\uXXXX`），不進 `Myriagent.slnx`（第 2 階段整合時確認：`tools/` 底下的 .NET 工具各自建置，設計 13.0）。

```bash
dotnet build tools/hub-probe/hub-probe.slnx -c Release -nodeReuse:false -p:UseSharedCompilation=false   # 三個專案（含 ProbeClient.Tests）
dotnet test tools/hub-probe/ProbeClient.Tests/ProbeClient.Tests.csproj -c Release -nodeReuse:false -p:UseSharedCompilation=false   # load 的純函式規則、守門、admin 包裝
tools/hub-probe/ProbeClient/bin/Release/net10.0/MyriagentHub.ProbeClient.exe <模式> --base https://localhost:<埠> [參數]
tools/hub-probe/SqliteBench/bin/Release/net10.0/MyriagentHub.SqliteBench.exe <資料夾> [秒數=30] [--sync FULL|NORMAL|FULL,NORMAL] [--only B1,B2,B3,B4,B5,B6] [--b6-gb 1,4] [--b6-runs 3] [--b6-keep]
```

## MyriagentHub.ProbeClient

結束碼：0 完成、1 例外（印 `{"event":"fatal"}`）、2 用法錯誤、3 伺服器不支援這個模式（印 `{"event":"unsupported"}`）、4 `load` 因伺服器私有記憶體超過 400 MB 自己中止（印 `{"event":"abort","reason":"memory"}`；在 `--relay-burst` 的轉送階段中止時照樣寫摘要，只有 `aborted` 與 `relay` 區塊）、5 正式站守門拒絕（印 `{"event":"guard","refused":true}`）。共用參數：`--base`（預設 `https://localhost:5443`）、`--sni`（預設 `--base` 的主機名；例如連 127.0.0.1 但要拿 `hub.myriagent.test` 的憑證）、`--hub-path`（裝置 hub 路徑，預設正式的 `/hubs/v1/device`；原型是 `/hubs/agent`）、`--minthreads N`（先把用戶端執行緒集區調大，排除用戶端暖身慢）。用戶端一律接受自簽憑證——只對本機或測試伺服器用。

**正式站守門**（WP4.9 計畫 D18）：所有模式連線之前先過 `tools/fake-device/Runtime/LocalHubTargetRules.cs`（以連結編譯同一份；測試表 `tools/hub-e2e/test/guard-cases.json` 三邊共用）——`--base` 只收 https 加 `127.0.0.1`／`localhost`／`[::1]`、拒絕任何 `example.com`；連上之後再匿名讀一次 `/readyz` 與 TLS 憑證，`environment` 是 `service`（Windows 服務）、憑證或 `/readyz` 的 `tls` 名稱含 `hub.example.com`、或簽發者是公開 CA 任一成立就拒絕（結束碼 5）；讀不到 `/readyz` 或沒有 `environment` 也拒絕。只看主機名稱擋不住 `ssh -L 本機埠:127.0.0.1:443` 的轉送。`load --hub-exe` 在自己啟動的伺服器就緒之後才檢查。

### 第 2 階段就能跑

| 模式 | 作法 | 參數 | 預期 |
|---|---|---|---|
| `tls` | 兩次 TLS 交握：提 `h2`＋`http/1.1`、只提 `acme-tls/1`；記協商出的 ALPN、葉憑證、SAN、是否帶 acmeIdentifier 擴充 | — | 一般連線 `h2`＋目前的憑證；沒有進行中的 TLS-ALPN-01 驗證時，只提 `acme-tls/1` 依 RFC 7301 被拒（`no_application_protocol`） |
| `conc` | 同時開 N 條 TLS 連線（每條送一個靜態檔請求後 keep-alive），全部開著時從其中 M 條同時送 `GET /healthz`；前後與期間讀 `/readyz`（本機迴路免認證）的 `connections.active`、`process.privateMb`、`threads` | `--n 200`、`--m 30`、`--path /auth.css`、`--hold 2`（開好後等幾秒再讀伺服器指標） | N 條都開成、靜態檔全 200；M 個請求全 200 且很快完成（沒有排隊）；伺服器連線數 1 → N＋1 → 1 |

- 靜態檔在中介程式的最前面、不經過速率限制；`/healthz`、`/readyz` 屬匿名請求（每 IP 每分鐘 120 次），所以 M 預設 30。
- `/readyz` 的 `privateMb`、`workingSetMb`、`threads` 是**查詢當下取樣**的值（`process.sampledAt`）；`cpuPercent` 是距上一次取樣（保險絲每 `Hub:MemoryFuse:SampleSeconds` 秒或上一次查詢）的平均，時間窗是 `process.cpuWindowSeconds`。WP2.5 時這些是保險絲 30 秒前的舊值、量 `conc` 要以 `--Hub:MemoryFuse:SampleSeconds=1` 啟動伺服器；整合時改成即時取樣，不必再加。
- 設計 11.3 的「conc（200 條連線）」原本是 200 條 SignalR 連線＋慢請求（原型版本保留成 `conc-hub`）；第 2 階段沒有 hub，這裡量的是 Kestrel 本身的連線上限與每連線記憶體。

### 原型的模式（要原型的測試端點）

程式原樣保留研究原型的作法，用的是**原型的**測試端點（`/test/mint-device`、`/test/push`、`/test/broadcast`、`/test/invoke`、`/test/slow`、`/test/stats`、`/test/swap-selfsigned`）、hub 方法（`WhoAmI`、`Echo`、`Publish`、`PublishBatch`、`Resume`、`Command`、`AskCallerBack`）與配對端點（`/api/pair/*`）。正式的 MyriagentHub 沒有這些，執行前先問 `/test/stats`，不是 200 就印 `unsupported`、結束碼 3（實測：404——`/test/*` 不是受保護前綴，未對應路徑不論是否登入一律 404；WP2.5 時是匿名 401）。`--hub-path` 的預設已改成正式的 `/hubs/v1/device`（WP4.9 步 9），方法名稱與測試掛鉤仍是原型的，所以照舊回 3；S9 的負載改由下面的 `load` 量。

| 模式 | 量什麼（設計 11.3） | 主要參數 |
|---|---|---|
| `conc-hub` | N 條 hub 連線＋M 個 2 秒慢請求同時進行，沒有排隊 | `--n 50`、`--slow 30` |
| `size` | 用戶端→伺服器 31／33 KB（預設 32 KB 上限）、伺服器→用戶端 512 KB／2 MB（`ApplicationMaxBufferSize`） | — |
| `invoke` | 伺服器呼叫指定裝置並等回覆（client results），含呼叫者自己的死結逾時 | — |
| `replay` | B 離線期間 A 發 100 則，B 重連後 `Resume(lastSeq)` 剛好補回 100 則；重送同 id 不重複 | — |
| `soak` | 長時間收發：A 每 100 ms 發一則（outbox），期間殺伺服器，最後 `missingAtB == 0` | `--seconds 40`、`--stopfile`、`--stateful true` |
| `blip` | TCP 轉送一次切斷所有連線，stateful reconnect 連線 ID 不變、斷線期間訊息補上（**正式的裝置 hub 不開 stateful reconnect**，設計 14.3；換成正式名稱時改成驗「重連後重新交握、補送不掉」） | `--relay-port 5444` |
| `backpressure` | 慢用戶端讓伺服器的送出等待；群組廣播時慢的會不會拖到快的 | `--count 3000`、`--size 1024`、`--delay 10` |
| `stall` | 完全卡住的用戶端：伺服器記憶體有上限、廣播不被拖住 | `--count 200000`、`--size 1024` |
| `pubrate` | 模擬 WAN（轉送每方向延遲）下逐則 Invoke／Send／批次的吞吐 | `--delay 15`、`--relay-port 5445` |
| `certswap` | WebSocket 開著時換憑證，新連線拿到新憑證、舊連線照常（第 2 階段的 HTTP keep-alive 版本在 `tools/acme-e2e`） | — |
| `pair` | 裝置配對（device flow）→ 連 hub → 發事件 → 回應指令 → 被撤銷後 negotiate 401 | `--name`、`--publish 5` |

設計 11.3 另外列的 `soak` 的「10 分鐘內隨機殺伺服器 10 次、5,000 筆、`missing == 0`」編排，研究原型沒有（原型的 `soak` 只收發，伺服器由外部重啟）；`load` 見下一節。

### `load`（WP4.9 步 9；S9，另量伺服器端的 S7 與回補負載）

**依擁有者決定（WP4.9 計畫 Q2＝3，2026-10-21）不在 Hub 主機跑**：P2b 與 D19 的臨時實例取消，S9、S7 與回補負載只有開發機的數字（只當參考：`verdict` 的「平均 CPU ≤5%」是對 Hub 主機的 i9-12900KF 訂的）。所有模式都過下面的正式站守門，連到正式服務一律結束碼 5；之後若擁有者另外核准在 Hub 主機量，作法是計畫 D19 的臨時實例（Hub 主機的 `%TEMP%`、只聽 127.0.0.1 的高埠、暫存資料根、不是服務，從開發機經 ssh 隧道打進去），`admin` 指令一律帶 `--data`（`docs/hub-setup.md` 第 16 節）。開發機上的實測在本檔最後一節。

```bash
export MYRIAGENT_LOAD_PW=<擁有者密碼>     # 密碼只從環境變數讀（--owner-password-env 指它的名稱），不上命令列
MyriagentHub.ProbeClient load --hub-exe src/MyriagentHub/bin/Release/net10.0/win-x64/MyriagentHub.exe \
  --fake-device tools/fake-device/bin/Release/net10.0-windows/win-x64/FakeDevice.exe --work <空資料夾> --owner-password-env MYRIAGENT_LOAD_PW \
  [--devices 10] [--browsers 5] [--streams 5] [--persistent 1] [--minutes 10] [--sample-seconds 5] [--stalled-browser 1] [--idle-minutes 2] [--relay-burst] [--out <json>]
MyriagentHub.ProbeClient load --base https://127.0.0.1:<埠> --owner-origin <PublicOrigin> …   # 已經在跑的本機 MyriagentHub（例如經 Node 延遲代理）
```

| 參數 | 預設 | 說明 |
|---|---|---|
| `--base` 或 `--hub-exe` | 擇一 | `--hub-exe`：把 MyriagentHub.exe 的資料夾複製到 `%TEMP%\myriagent-tools\probe-hub-<pid>-<n>`，以 `<work>\hub-data`、127.0.0.1 的高埠、Testing 環境、file 憑證庫、速率限制關閉啟動（參數照 tests 的 `MyriagentHubProcess.Arguments`，但 `Hub:PublicOrigin` 是 base、記錄檔 `Hub:FileLog` 打開：主控台的輸出是主控台碼頁〔繁中 cp950〕，伺服器記錄的中斷原因在停止之後從 `hub-data\diag\myriagenthub-*.log` 的 JSON 行數）；停止一律 `admin stop --data <資料根>`（`AdminWrapper`：沒有資料根就拒絕，D19），等不到才以 PID 結束，再刪掉這次啟動新增、記著這份複製的金鑰檔 |
| `--fake-device`、`--work`、`--owner-password-env` | 必要 | `--work` 要不存在或是空的：裝置的資料根與劇本在 `devices\`、伺服器的主控台輸出 `hub.log`（`--hub-exe` 時）、摘要 `load-summary.json` |
| `--owner-user`、`--owner-origin` | `admin`、base | 擁有者帳號與網頁的 Origin（伺服器的 PublicOrigin） |
| `--devices`、`--streams`、`--persistent` | 10、5、1 | 假裝置台數；其中幾台串流、幾台跑持久那一條，其他只連著（每台同時只跑一個對話，串流＋持久不能超過台數） |
| `--browsers`、`--stalled-browser` | 5、1 | 擁有者瀏覽器與卡住的瀏覽器（各自登入，一條一個工作階段） |
| `--minutes`、`--idle-minutes`、`--sample-seconds` | 10、2、5 | 負載期間（從全部配對好起算）、之後的閒置期間（裝置照樣連著）、讀 `/readyz` 的間隔 |
| `--seed-conversations N`、`--backfill-only` | 0、否 | 每台啟動前先有 N 個對話（配對後回補上去）；`--backfill-only` 只量回補：沒有瀏覽器與串流，負載期間從全部配對好起算 |
| `--delay-ms` | — | 只記錄在摘要裡：WAN 延遲由外部（`tools/hub-e2e/lib/delay-proxy.mjs`）設定，工具另外量 `/healthz` 的來回時間（`rttMs`） |
| `--slack-seconds` | 20 | 估計每一批配對要多久：較早配對的裝置對話多跑這麼久，才蓋得滿從全部配對好開始的負載期間 |
| `--server-processors` | 本機 | `--base` 時伺服器的處理器數（換算核心數；Hub 主機的 i9-12900KF 是 24） |
| `--relay-burst` | 否 | 負載期間之後接 2 分鐘的媒體轉送上限階段（WP4.9 步 12，見下面第 4b 點）；要 `--browsers ≥1`、`--streams ≥1`、`--devices ≤10`（每台 21 個 GET 加桌面回覆的 20 個 PUT，伺服器同時最多 512 條連線），不能配 `--backfill-only` |
| `--relay-cap` | 64 | （WP4.9 步 19，要配 `--relay-burst`）伺服器所有來源合計同時的轉送上限 `Hub:Media:MaxConcurrentRelays`（1～1024）：判定照它算；`--hub-exe` 時另以 `--Hub:Media:MaxConcurrentRelays=<值>` 傳給自己啟動的伺服器，`--base` 時要和那台伺服器的設定相同 |

流程：
1. 守門、量 `/healthz` 的來回時間；之後每 `--sample-seconds` 秒讀 `/readyz`（印 `{"event":"sample"}`；伺服器有的話也帶 `gcHeapMb`、`gcCommittedMb` 與 `idleTrims`），私有記憶體 >400 MB 就中止（結束碼 4）。
2. 瀏覽器（.NET SignalR 用戶端，連 `/hubs/v1/browser`、帶 cookie 與 `Origin`）送 `ready`、`ClientState`（visible、focused）；清單裡出現標題 `load-stream-NN`／`load-persist-NN` 的對話就 `open`，並以 `visible` 指令與 `openConversationIds` 讓它「看得到」（擁有者電腦只對看得到的對話送即時更新）。卡住的瀏覽器用原生 WebSocket（接收緩衝區 4 KB）完成 SignalR 的 JSON 握手、送 `ready`，看到全部 load 的對話後送 `ClientState`、`open`、`visible` 就停止讀取（照樣每 5 秒送 ping，不讓伺服器以逾時關閉）。
3. 假裝置 `FakeDevice session --ephemeral-key`（裝置金鑰只在記憶體）一批最多 4 台：這一批的劇本第一步（`addAccount`）都做完（已配對、連上）才送下一批（伺服器的待核准上限是 5）。配對順序：閒置的、持久的、串流的（串流最後配對，最接近負載的起點）。劇本：串流每 250 ms 一段（每個對話每秒 4 則，設計:3187）、每步最多 1,000 段、每段開頭帶時間標記 `〔13 位 UTC 毫秒〕`；持久那一條每秒一段、每段各成一個完成的項目；跑完都再連著閒置一段時間，session 接著等 outbox 清空、解除配對、自己結束。環境變數照 hub-e2e 的 `child-env.mjs` 剝掉（`ChildEnvironment`，測試比對兩邊的清單），擁有者密碼的變數再明確放回。
4. 全部配對好起算 `--minutes` 分鐘是負載期間：瀏覽器從 `item` 的文字解析時間標記——串流對話裡**執行中**的項目第一次帶著標記出現算一筆即時延遲；持久對話裡**完成**的項目第一次帶著標記出現算一筆持久延遲（D8；`open` 的快照裡已有的只記下、不算）。產生者與觀看者在同一台電腦，時鐘差 0。
4b. `--relay-burst`（WP4.9 步 12；WP4.7 R6：每台來源同時 ≤20 個轉送、每段 ≤8 MB）：啟動前每台在 `<work>\media\load-NN.mp4` 寫一個正好 8 MiB 的檔（開頭是 `ftyp` 方塊、其餘是 `new Random(2000＋第幾台)` 的位元組；放在裝置的資料根以外，資料根在 `RemoteFileGuard` 的禁止清單裡），劇本在 addAccount 之後先 `policy View`（原檔要 View 以上）、再跑一個標題 `load-relay-NN`、只有一個 `media` 步驟的短對話，之後照角色跑；串流與持久的對話多跑 120＋60 秒。瀏覽器也開這些短對話，從項目的 `media[].url` 記下 `/m/` 網址。負載期間結束後的 2 分鐘（串流照跑）：工具自己登入一次，對每台並行——同時發 20 個 GET（HTTP/1.1、每條連線的接收緩衝區固定 64 KB，本文沒讀完伺服器就放不掉名額），20 個都拿到標頭之後才發第 21 個（要立刻 503 `busy` 加 `Retry-After`），接著 20 個本文照 90 秒讀完的節奏每次讀 16 KB、算 SHA-256 比對檔案，最後一個 `Range: bytes=0-65535`（所有台都讀完之後才送，名額收回了才是 206、內容是檔案開頭）。判定 `LoadRules.RelayVerdict`：每台恰好第 21 個被拒、其餘 20 個完整讀完、名額收回；伺服器另有所有來源合計的上限 `Hub:Media:MaxConcurrentRelays`（預設 64，`--relay-cap`），台數 × 20 超過它時（4 台起）改成合計剛好 `relayCap` 個完整讀完、前 20 個裡其餘的都是 503 `busy` 加 `Retry-After`（摘要的 `relay.expectedAccepted`、`relay.accepted`）。**這時驗不到每台的上限**：伺服器對每台上限與合計上限回的是同一個 503 `busy`，每台的第 21 個一定先撞上已滿的合計上限，每台的上限被拿掉也照樣 pass（WP4.9 合併前審查）；摘要的 `relay.perDeviceCapVerified`（台數 × 20 ≤ `relayCap`）標出這一輪有沒有驗到，是 false 時每台上限的證據只有 MyriagentHub.Tests 的 `MediaRelayTests.第21個同時轉送回503_釋放後再試成功`，要實跑的證據另跑 3 台（3 × 20＝60 ≤64；實測見本檔最後一節）。另外要求期間串流的即時延遲 p95 ≤500 ms，伺服器私有記憶體最大值 <400 MB（記憶體保險絲的告警線）；台數不對、沒拿到網址、沒有延遲或記憶體的樣本是 `insufficient`；伺服器在期間內結束（`--hub-exe` 看結束碼）或期間的 `/readyz` 一次都讀不到是 `fail`（不是樣本不足）；私有記憶體超過 400 MB 時 load 照 D19 中止（結束碼 4），摘要的 `relay` 帶到中止為止的取樣與 `fail`（`LoadRules.RelayAbortedVerdict`）。**2026-10-21 本機實測：當時（轉送修正之前）的 MyriagentHub 過不了**——1 台 20 個轉送時受管理堆積從 61 MB 跳到 364 MB、私有記憶體 477 MB（load 中止），3 台時 MyriagentHub 以「Out of memory.」結束（GC 硬上限 512 MB）；原因的推算見 `tools/probes/wp4.9/relay-pipe/README.md`，數字在計畫附錄 B 第 12 列。修正之後的實測（10 台，pass）在本檔最後一節。
5. 負載結束：卡住的瀏覽器恢復讀取（ping 已經送不出去、讀到關閉訊框或例外＝伺服器關掉了它）；等串流與持久的對話都跑完，再閒置 `--idle-minutes` 分鐘量閒置記憶體。
6. 摘要（一行 `{"event":"summary"}`，也寫進 `--out`）：`load.cpu`（`LoadRules.Cpu`：依 `cpuWindowSeconds` 加權、只算落在期間裡的那一段、同一組 `(cpuPercent, cpuWindowSeconds)` 連續出現只算一次、涵蓋率與換算的核心數，D16）、`load.maxPrivateMb`／`p95PrivateMb`、`load.idlePrivateMb`（閒置期間後半的中位數）、`load.idleGcHeapMb`／`idleGcCommittedMb`（取法相同，沒有這個欄位的取樣不算，只記錄）、`verdict`（私有記憶體 ≤300 MB、平均 CPU ≤5%、閒置 <150 MB；樣本不足或涵蓋率 <90% 是 `insufficient`；≤5% 只拿 Hub 主機的數字判，本機的判定只當參考。WP4.9 合併前審查起另以 `LoadRules.LoadApplied` 確認負載真的施加了：不是每台都跑完對話、假裝置沒有全部自己結束或結束碼不是 0、有串流〔持久的對話〕與瀏覽器卻沒有任何即時〔持久〕延遲的樣本，都記進 `verdict.notApplied` 並判 `insufficient`；下面 `slowClient` 的 `onlyStalled` 不成立判 `fail`。結束碼照舊是 0〔D16〕，看 `verdict.result`）、`s7.live`／`s7.persistent` 的 p50／p95／p99、`slowClient`（`onlyStalled`：卡住的那條被伺服器關掉、正常的瀏覽器一條都沒被關、`--hub-exe` 時伺服器記錄的中斷次數〔`slow_client`、`send_timeout`、`send_failed`〕不多於卡住的條數。實測這個次數是 0：卡住的連線是被 SignalR 傳輸層的送出逾時〔`TransportSendTimeout`，MyriagentHub 沒設、預設 10 秒〕關掉的，比 MyriagentHub 自己的送出逾時 15 秒與「60 秒內第 3 次溢出」都早，只在 `Microsoft.AspNetCore.Http.Connections` 的 Trace 記錄留一行〔`HttpConnectionContext[9]`「…elapsed attempting to send a message to the transport. Closing connection …」〕）、每台裝置的結果（結束碼、drained、rejected、BatchAck、即時轉送）、`hubStop`（正常停止、孤兒金鑰）；`--relay-burst` 時另有 `relay`：期間（`startMs`、`burstEndMs`、`endMs`）、每台每個請求的狀態碼、錯誤代碼、`Retry-After`、位元組數、是否完整、拿到標頭與讀完的毫秒、名額收回的那一個，期間的 `s7Live`、`maxPrivateMb`、伺服器 `/readyz` 的 `blobs.relaysActive` 最大值與期間的 `relayTimeouts` 增量（只記錄），與 `verdict`。

已知限制：工具被強制結束時，它啟動的 MyriagentHub 與 FakeDevice 不會跟著結束（沒有工作物件；Ctrl+C 會照常清理）；`--base` 連的伺服器若開著速率限制，6 次瀏覽器登入加每台核准一次登入可能被擋。

## MyriagentHub.SqliteBench

- **B1**：每筆一個交易的吞吐（WAL＋NORMAL、WAL＋FULL、DELETE＋FULL；3,000 筆、每筆 1 KB）。
- **B2／B3**：5 個對話各 20 次／秒整筆取代（回覆越寫越長，最長 32 KB）＋事件 100 筆／秒＋2 個讀者（每 50 ms 讀一個對話的最後 80 筆）；預先放 300 個對話 × 40 筆歷史。B2 每筆一個交易、B3 每 100 ms 合併一個交易。MyriagentHub 用 WAL＋`synchronous=FULL`，所以預設跑 FULL（原型跑的是 NORMAL；`--sync FULL,NORMAL` 兩種都跑）。
- **門檻**（設計 11.4）：B3＋FULL 的寫入 p99 ≤10 ms（輸出的 `threshold.pass`）；要在 Hub 主機的磁碟上跑才算數（WP2.4 的 F-5）。
- **B4**：20 MB × 5 存 SQLite BLOB 與存成內容雜湊命名的檔案（WriteThrough）。
- **B5**：每個操作開一條連線（MyriagentHub 的 EF 用法：開、套 `SqliteSetup.PerConnectionPragmas` 同樣的 PRAGMA、讀一列或寫一列（自動交易＝`synchronous=FULL` 的一次提交）、關），`read` 2,000 次、`write` 300 次（各先暖身 50 次），比較 `pooling`（連線池開／關）× `keepAlive`（另外開一條保活連線讓資料庫一直有連線開著）。`walDeletedAfterOps`＝操作完 `-wal` 不在的次數：沒有任何連線開著時，最後一條連線關閉就做檢查點並刪掉 `-wal`、`-shm`。MyriagentHub 用的是 `pooling=false`＋`keepAlive=true`（`SqliteSetup` 不用池的原因、`HubDatabase` 的保活連線）；**改回連線池之前用它量**（`--only B5` 約 7 秒）。
- 亂數種子固定：歷史資料與串流 42、讀者 100＋i、B4 的 BLOB 42＋i（同一個 .NET 版本下 `new Random(seed)` 的序列固定；.NET 不保證跨主版本相同，換版本後指紋要重新記錄）。**產出指紋**（.NET 10.0.12）：`historySha256`（12,000 筆歷史內容的 SHA-256）＝`ed8efa5e278ac4b396e04693e65fe22d2a00f6e23b827759692e0046993c9db2`；`blobSha256`（前 16 碼）＝`bf4de57e07eb582f`、`f0591a4b358ac7df`、`04a497774e464ed8`、`a8eaf45db20316a6`、`4a7f062f0ddbe0e3`。重跑後這兩項應該完全相同（時間量測當然會不同）。
- 資料夾裡的 `b1-*.db`、`b2-*.db`、`b4-blob.db`、`b4-files\`、`b5-open.db`（只跑 FULL 約 330 MB、`--sync FULL,NORMAL` 約 460 MB）每次都會先刪再建，量完整個資料夾刪掉，不要放在 repo 裡。資料夾或它的上層是 MyriagentHub 的資料根（有 `status\service.lock` 或 `db\hub.db`）時 B1～B6 一律不跑、結束碼 2，不建任何資料夾或檔案（WP4.9 合併前審查；原本只有 B6 檢查）。
- **B6**（WP4.2 步 15、答 7；`SqliteBench/SyncDbCopyBench.cs`）：sync.db 的停機複製與備份每 GiB 要幾秒。**不在預設的 B1～B5 裡**，要 `--only B6`（每種大小要大小 × 2.3＋1 GiB 的空間）；`--b6-gb` 是要量的大小（GiB，1～64，預設 `1,4`）、`--b6-runs` 每件事跑幾次取中位數（1～9，預設 3）、`--b6-keep` 量完留著 `b6\`（預設刪掉）。
  - **合成資料**：在 `<資料夾>\b6\sync-{n}g.db` 以伺服器真正的結構建（csproj 連結 `src/MyriagentHub/Data/SyncSchema.cs`，v3；`ServerInstanceShim.cs` 補它用到的 Protocol 型別），4 台裝置輪流、每個對話 100～199 個項目（45% 文字 300～3,000 字、35% 工具〔detail 100～500 字、output 500～12,000 字〕、15% 使用者訊息 50～500 字、5% 通知），之後一則佇列與完成的一輪；payload 照 `PayloadRules`（≥8 KB 用 gzip＋base64），`items.body` 與事件同一份。比例照 `ConversationWriteBench` 情境三（步 14）：events 的 payload 與 items 的 body 各約 38%、檔案約是解碼後歷史的 1.49 倍（輸出的 `composition`）。產生時 `journal_mode=OFF`、`synchronous=OFF`，建完改回 WAL；種子固定 4206、時間與 instance_id 固定，`contentSha256`（每一筆事件存下的 payload 字串依寫入順序〔＝seq〕串起來的 SHA-256）與 `fileSha256`（合成檔本身）在同一個 .NET 與 SQLite 版本下完全相同。
  - **停機複製**：以真的 Windows PowerShell 5.1（`powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand`，路徑經環境變數傳、子程序拿掉 `PSModulePath`）照 `tools/deploy/Install-MyriagentHubRelease.ps1` 的停機複製跑 `Copy-Item -Force`、來源與複本各一次 `Get-FileHash -Algorithm SHA256`（部署 job 就是 5.1；它的 SHA-256 比 .NET 10 慢，不能用 .NET 代量）。找不到 powershell.exe 時改用 .NET（`method: "dotnet"`）。
  - **備份**：照 `BackupService.SnapshotDatabase`，在背景模式的執行緒上（`THREAD_MODE_BACKGROUND_BEGIN`，同服務）`VACUUM INTO`、對快照 `integrity_check`、快照的 SHA-256，連線套 MyriagentHub 的 PRAGMA、命令逾時照 `BackupTimeoutRules`（同樣是連結的檔）。
  - **命令逾時**（`B6-command-timeout`）：以 1 秒的命令逾時對第一個大小的檔跑一次 `VACUUM INTO` 與 `integrity_check`，看 Microsoft.Data.Sqlite 的逾時會不會中斷執行中的單一敘述（`limitsLongStatement`；`conclusive` 是兩個都超過 1.5 秒，太快的那個不能判斷）。
  - 資料夾或它的上層是 MyriagentHub 的資料根（有 `status\service.lock` 或 `db\hub.db`）時拒絕（結束碼 2），不會碰到正式的資料。在 Hub 主機跑的程序與時段見 `docs/hub-setup.md` 第 8 節。

## 開啟對話的 Hub 端成本（`snapshot_cost.py`、`SnapshotRenderBench`）

對象是 sync.db 的複本（例如 Hub 的每日備份 `backups\daily\<日期>\sync.db`，只讀；內容是真的對話，複本放暫存資料夾、量完刪掉）：

```bash
python -X utf8 tools/hub-probe/snapshot_cost.py <sync.db 的複本>      # ReadSnapshot 的查詢：每個對話最後一頁的耗時、原始大小、查詢計畫
MYRIAGENT_SNAPSHOT_BENCH=<sync.db 的複本> [MYRIAGENT_SNAPSHOT_DUMP=<暫存資料夾>] dotnet test tests/MyriagentHub.Tests/MyriagentHub.Tests.csproj -c Release --filter "FullyQualifiedName~SnapshotRenderBench" --logger "console;verbosity=detailed"
# 以真的 HubItemRenderer 渲染每個對話的第一頁（備份不含 blob，參照 blob 的變成佔位：量到的是下限）；DUMP 另把最大的 8 頁寫成瀏覽器的 items 陣列，給 tools/ui-probe/open-cost.mjs
```
