# ACME 端到端（tools/acme-e2e）

第 2 階段 WP2.5（設計 `docs/plans/2026-10-10-multi-device-sync.md` 11.3、9.6）：用 Let's Encrypt 的測試 CA **Pebble v2.10.1**＋**pebble-challtestsrv** 對本機以高埠執行的 MyriagentHub 驗證：

- **TLS-ALPN-01 取得憑證**：驗證連線打在正式服務的同一個 HTTPS 埠（`TlsAlpnChallengeStore`），不必開 80、不必停服務。
- **熱換、開著的連線不斷**：熱換前建立的 HTTP/1.1 keep-alive 與 HTTP/2 連線在熱換後照常回應（同一個 TLS 工作階段、沒有重新交握）；新連線拿到新憑證與中繼憑證。
- **ARI 續期**：Pebble 支援 ARI（`renewalInfo`）與 `replaces`；以 Pebble 管理介面把憑證的建議續期視窗改成過去（模擬 CA 要求提前續期），MyriagentHub 帶 `replaces` 續期。
- **以 `admin cert-renew` 觸發**：清掉失敗退避立即重試；**一律重查 ARI**（忽略上次查詢時間，CA 要求提前續期時不必等 12 小時）並等這一輪做完回報；`--force` 不看續期時點直接續期。
- 重啟後以指紋載入已存的憑證、不重新申請；結束後檢查主機副作用（孤兒金鑰檔、CA 存放區）。

改到 `src/MyriagentHub/Tls/` 時必跑（設計 11.3）。

## 前提

- Windows x64、.NET 10 SDK；第一次執行要能連 GitHub 下載 Pebble（之後用快取）。
- 先建置 MyriagentHub：`dotnet build src/MyriagentHub/MyriagentHub.csproj -c Release -nodeReuse:false -p:UseSharedCompilation=false`（工具預設用 `src\MyriagentHub\bin\Release\net10.0\win-x64\MyriagentHub.exe`）。
- **不需要管理員、不碰 80／443**：Pebble、challtestsrv、MyriagentHub 全部只聽 127.0.0.1／localhost 的高埠（自動挑空的），不會跳防火牆詢問；MyriagentHub 以 `--data` 暫存資料根、`Development` 環境、file 憑證模式執行，不碰正式資料與使用者的憑證存放區（`CurrentUser\My`）。

## 外部執行檔（固定版本、驗 SHA-256、不進 repo）

| 資產 | 大小 | SHA-256（GitHub release 公布的 digest，研究時的副本也相同） |
|---|---|---|
| [`pebble-windows-amd64.zip`](https://github.com/letsencrypt/pebble/releases/download/v2.10.1/pebble-windows-amd64.zip) | 3,331,375 | `2539f3903302d75f897522b23b1b52b3300f5f992245f13d3bdf3967cf23af6d` |
| [`pebble-challtestsrv-windows-amd64.zip`](https://github.com/letsencrypt/pebble/releases/download/v2.10.1/pebble-challtestsrv-windows-amd64.zip) | 2,924,445 | `f4621c6ce1ac025c4f49a7316ec3d16779df8c5b09276a2544f5a5198a95e32a` |

- 下載到 repo 以外的快取（預設 `%LOCALAPPDATA%\myriagent-tools\downloads\<sha256>\`；在 repo 裡會被拒絕）。每次下載都放進自己新建的空資料夾，大小與 SHA-256 都對了才改名成 `<sha256>` 資料夾，不對就整個刪掉。
- 每次使用前重新驗快取裡壓縮檔的雜湊（被改過就刪掉重下），並解到這次工作資料夾底下新的空資料夾（`bin\pebble\`、`bin\challtestsrv\`）執行；解出的執行檔雜湊記在結果檔（pebble.exe `467f99ed…a233f4`、pebble-challtestsrv.exe `7d0c2844…8db827`）。
- 改版本：改 `PinnedDownloads.cs` 的網址、大小、SHA-256（從 GitHub release 頁面的 digest 或下載後 `sha256sum` 取得）與 README 這張表。

## 執行

```bash
dotnet build tools/acme-e2e -c Release -nodeReuse:false -p:UseSharedCompilation=false
tools/acme-e2e/bin/Release/net10.0/acme-e2e.exe [選項]
```

| 選項 | 預設 | 說明 |
|---|---|---|
| `--hub <MyriagentHub.exe>` | `<repo>\src\MyriagentHub\bin\Release\net10.0\win-x64\MyriagentHub.exe` | 要測的伺服器 |
| `--work <資料夾>` | `%TEMP%\myriagent-tools\acme-e2e\<時間>` | 這次執行的工作資料夾：`result.json`、`pebble.log`、`challtestsrv.log`、`hub-console-*.log`、`hub-data\`（MyriagentHub 資料根）、`pebble\`（設定與監聽憑證） |
| `--cache <資料夾>` | `%LOCALAPPDATA%\myriagent-tools\downloads` | 下載快取（必須在 repo 以外） |
| `--domain <網域>` | `hub.myriagent.test` | MyriagentHub 的 `Hub:ServerDomain`＝ACME 識別；challtestsrv 把它解析到 127.0.0.1（A）與 ::1（AAAA） |
| `--hub-port`、`--acme-port`、`--acme-mgmt-port`、`--dns-port`、`--dns-mgmt-port` | 0（自動） | MyriagentHub 的 HTTPS 埠（＝Pebble 的 `tlsPort`）、Pebble 目錄、Pebble 管理介面、challtestsrv DNS、challtestsrv 管理介面 |
| `--timeout <秒>` | 60 | 每個等待的上限 |
| `--staging` | 不帶＝`SkipStaging=true` | 不略過 staging：第一次取得先打 staging 目錄、成功才打正式目錄（兩者都是同一個 Pebble，所以會簽兩次、驗證兩次） |

結束碼：0 全部通過、1 有檢查失敗、2 參數錯誤。整個流程約 20 秒（第一次多下載約 6 MB）。

### 測試環境的設定

- **Pebble**：設定檔由工具產生（`pebble\pebble-config.json`）：只聽 127.0.0.1、`tlsPort`＝MyriagentHub 的埠、`keyAlgorithm` ecdsa、設定檔 `classic`（90 天）與 `shortlived`（6 天）。環境變數 `PEBBLE_VA_NOSLEEP=1`（不隨機延遲）、`PEBBLE_WFE_NONCEREJECT=0`（不故意拒 nonce）、`PEBBLE_AUTHZREUSE=0`（不重用授權，每次續期都真的走 TLS-ALPN-01）。
  - 一定要有 `classic`：MyriagentHub 預設 `Hub:Acme:Profile=classic`，目錄有公告才以設定檔下單並帶 ARI 的 `replaces`；Pebble 內建設定只有 `default`／`shortlived`，訂單沒指定設定檔時會**隨機**挑一個。
  - Pebble 的發行壓縮檔只有執行檔、沒有 `test/certs`（minica 根），所以 Pebble 自己的 HTTPS 監聽憑證由工具產生：一個小型 CA（`pebble\listener-ca.pem`）簽 `localhost`／127.0.0.1／::1 的伺服器憑證；**這個 CA 交給 MyriagentHub 的 `Hub:Acme:DirectoryTrustPem`**（正式組建沒有「接受任何憑證」）。Pebble 每次啟動產生的發證根（管理介面 `/roots/0`）只用來驗證取得的憑證鏈。
- **challtestsrv**：只開 DNS 與管理介面（`-http01= -https01= -tlsalpn01= -doh=` 全部關掉，否則預設聽所有介面的 5001～5003、8443 會跳防火牆詢問）；Pebble 以 `-dnsserver` 用它解析（先查 AAAA，所以驗證連線來自 `[::1]`，MyriagentHub 用 `BindAddress=localhost` 同時聽兩個迴路位址）。
- **MyriagentHub**：`--environment Development --Hub:ServerDomain=hub.myriagent.test --Hub:HttpsPort=<埠> --Hub:BindAddress=localhost --Hub:Tls:CertStore=file --Hub:Acme:Enabled=true --Hub:Acme:Directory=<Pebble 目錄> --Hub:Acme:StagingDirectory=<同> --Hub:Acme:SkipStaging=true（--staging 時 false） --Hub:Acme:Profile=classic --Hub:Acme:DirectoryTrustPem=<監聽 CA> --Hub:AdminInbox:PollSeconds=1 --Hub:Backup:Enabled=false --Hub:SelfTest:Enabled=false`。狀態從 `/readyz`（本機迴路免認證）讀，證據另外看資料根 `diag\` 的 JSON 行記錄與 Pebble 的記錄。

## 流程與通過條件

| # | 步驟 | 作法 | 通過條件 |
|---|---|---|---|
| 1 | 準備 | 下載或用快取、驗雜湊、解壓；挑連接埠；產生 Pebble 監聽憑證；拍主機快照 | 兩個壓縮檔大小與 SHA-256 相符 |
| 2 | 啟動 challtestsrv | 管理介面 `/set-servfail` 讓網域回 SERVFAIL | 200 |
| 3 | 啟動 Pebble | 讀目錄、`/roots/0`、`/intermediates/0` | 目錄有 `renewalInfo`（ARI），設定檔含 `classic` |
| 4 | 啟動 MyriagentHub | 等 `/healthz` 200 | — |
| 5 | 第一次申請失敗 | 啟動時自動申請；DNS SERVFAIL → TLS-ALPN-01 驗證失敗 | `/readyz` 的 `tls`：`lastOk=false`、訊息含 TLS-ALPN-01（帳戶、下單、取驗證都走通了＝DirectoryTrustPem 有效）、`consecutiveFailures=1`、退避 55～65 分鐘、告警 `certificate_self_signed`；新連線仍拿到自簽憑證 |
| 6 | 熱換前開兩條連線 | 手寫 HTTP/1.1 keep-alive（`GET /healthz`）、HttpClient HTTP/2 | 都 200、都拿到自簽憑證 |
| 7 | cert-renew 立即重試 | 清掉 SERVFAIL → `MyriagentHub.exe admin cert-renew --data <資料根>`（等這一輪做完才回）→ 每 250 ms 開新連線看憑證 | 結束碼 0（回報已取得新憑證）、時限內換上新憑證 |
| 8 | 取得的憑證 | 檢查新連線的憑證與鏈 | Pebble 中繼簽發、SAN＝網域、伺服器有送中繼、以 Pebble 根驗得過、ALPN h2；MyriagentHub 記錄 TLS-ALPN-01 驗證交握 ≥1 次、Pebble 記錄驗證成功；`/readyz` 的 `lastOk=true`、`lastMessage=issued`、指紋相同、`tls.name`＝網域（憑證沒有 CN，從 SAN 取）、`renewAt`＝效期三分之二（ARI 還沒查）；沒有進行中的驗證時只提 `acme-tls/1` 的交握被拒（RFC 7301） |
| 9 | 熱換：連線不斷 | 兩條舊連線再各送一次 | HTTP/1.1 同一個 TLS 工作階段 200、憑證仍是熱換前的；HTTP/2 200、TCP 連線 1 條、TLS 交握 1 次 |
| 10 | ARI 續期 | Pebble 管理介面 `POST /set-renewal-info/` 把這張憑證的視窗改成一小時前 → `admin cert-renew` | MyriagentHub 查了 ARI、換上新憑證；Pebble 記錄 `ARI: order … is a replacement of …` 與 `… has been marked as replaced`（帶了 `replaces` 且被接受）；兩條舊連線仍然不斷 |
| 11 | cert-renew 一律重查 ARI | 剛續期的憑證（Pebble 給正常視窗）→ `admin cert-renew` | 結束碼 0、Pebble 收到 ARI 查詢 ≥1 次、動作 wait、憑證不變 |
| 12 | CA 改了視窗後立刻 cert-renew | 把視窗改成過去（距上次查詢不到 12 小時）→ `admin cert-renew` | 重查 ARI ≥1 次、動作 renew、換上新憑證、Pebble 記錄 replacement |
| 13 | cert-renew --force | 新憑證的視窗還沒到 → `admin cert-renew --force` | 結束碼 0、動作 renew（`forced`）、換上 Pebble 簽的新憑證 |
| 14 | 重啟沿用 | `admin stop` → 同一資料根再啟動 | 正常結束；新連線拿到同一張憑證；Pebble 簽發數不變 |
| 15 | 收尾 | `admin stop`；結束 Pebble、challtestsrv；比對主機快照 | 沒有新增且還在的金鑰檔；LocalMachine\CA 沒有新增；這次寫進 CurrentUser\CA 的 Pebble 中繼憑證已由工具移除 |

## 預期輸出

主控台每步一行 `[通過]／[失敗] 步驟：摘要`（`[觀察]` 是只記錄、不判失敗的步驟，目前沒有），最後印結果檔路徑。`result.json`：`steps[]`（`name`、`ok`＝true／false／null〔觀察〕、`at`、`summary`、`data`）、`ports`、`executables`、`ok`、`finishedAt`；每一步完成就整份重寫（中途被殺也留得下已完成的步驟）。

- MyriagentHub 記錄的簽發耗時（`ACME 簽發成功 … ms`）1,600、1,556、1,564、1,565 ms（研究原型 1.8 秒）；從下 `cert-renew` 到新連線拿到新憑證約 1.9 秒（`cert-renew` 等這一輪做完才回報，所以結束碼與訊息就是這一輪的結果）。
- `--staging` 在 WP2.5 時（build `ffa994bca6ce`）跑過一次、全部通過：第一次失敗的訊息改成 `staging: …`；cert-renew 後先在 staging 簽一次再打正式目錄，3,532 ms 後換上正式憑證（TLS-ALPN-01 交握 6 次、Pebble 驗證成功 2 次、`stagingVerifiedAt` 有值）。整合後沒有改到 staging 的流程。
- Pebble 每次驗證都從 `[::1]` 對同一個埠做 3 次 TLS-ALPN-01 交握（模擬多點驗證）。
- Pebble 簽的憑證主體是空的（`promote CN=false`，名稱只在 SAN）：`/readyz` 的 `tls.subject` 是空字串，`tls.name`（顯示名稱）從 SAN 取得，步驟 8 檢查它等於網域。

## 觀察與限制

- WP2.5 時發現 `admin cert-renew` 不會重查 ARI、也不能強制續期（步驟 11 當時只能觀察）；第 2 階段整合時修正（手動觸發一律重查 ARI、加 `--force`），步驟 11～13 改成檢查。
- 只測 **file 憑證模式**（開發與測試預設）。正式環境的 **store 模式**會匯入服務身分的 `CurrentUser\My`，在開發機跑會動到使用者自己的憑證存放區，所以不在這裡測；以服務身分的驗證是 WP2.4 的 F-2 探針。
- staging 與正式目錄是同一個 Pebble：`--staging` 只驗證「staging 成功才打正式目錄」的流程，不代表外部連得到 443（那是 Hub 主機上對 Let's Encrypt staging 的 F-3）。
- 熱換時「開著的連線」是 HTTP keep-alive（HTTP/1.1、HTTP/2）；第 3 階段有 SignalR 之後，WebSocket 版見 `tools/hub-probe` 的 `certswap`。

## 主機副作用與清理

- **CurrentUser\CA**：`SslStreamCertificateContext.Create(葉, 中繼)` 會把中繼憑證寫進 CA 存放區（LocalMachine 沒權限時退到 CurrentUser；設計 9.6 的預期副作用），Pebble 每次啟動產生新的中繼 CA，所以每次執行都會多一張。工具在結束時只移除「這次執行才出現、主體或簽發者含 Pebble」的憑證，並回報移除的指紋。
- **金鑰檔**（`%APPDATA%\Microsoft\Crypto\Keys`）：file 模式每次匯入 PFX 會寫一個 CNG 金鑰檔，MyriagentHub 正常結束（`admin stop`）會刪掉；被強制結束就留下孤兒。工具一律以 `admin stop` 停止，逾時才強制結束並判失敗；收尾時回報「執行前沒有、現在還在」的金鑰檔，**只回報不刪**（同機其他程序也會建金鑰檔）。同時有其他會匯入 PFX 的程式在跑時，這一項可能誤判，請依建立時間對照 `hub-data\db\hub.db` 的時間確認。
- **子程序**：Pebble、challtestsrv、MyriagentHub 都加進 Job Object（`KILL_ON_JOB_CLOSE`），本工具不論怎麼結束（Ctrl+C、被殺）子程序都會一起結束，不會佔著連接埠。
- **工作資料夾**不自動刪（要看記錄與結果檔）；裡面有測試用的 DPAPI 加密祕密（ACME 帳戶金鑰、TLS PFX）與本機路徑，看完直接刪整個資料夾，不要提交。
