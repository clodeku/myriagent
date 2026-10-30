# 架設 MyriagentHub

MyriagentHub 是多電腦同步與網頁版的伺服器。架設之後可以：

- 讓多台電腦同步帳戶目錄、用量與對話，在任何一台都看得到其他電腦的對話；
- 用手機或任何瀏覽器打開網頁版（`https://<你的網域>/app/`），遠端看、遠端操作；
- 讓對話裡的代理用 `myriagent-remote` 在其他電腦執行指令；
- 建立協作人帳號、分享對話；
- 發佈桌面新版，讓所有電腦自動更新（[release.md](release.md)）。

只用一台電腦、不需要遠端的話，可以不架。

## 1. 你需要準備

| 項目 | 說明 |
|---|---|
| 一台 Windows 電腦（Hub 主機） | Windows 10／11 或 Windows Server 2019 以上，x64，長時間開機。可以和桌面 app 是同一台，但要一直開著 |
| 網域名稱 | 例如 `hub.example.com`，DNS 的 A（或 AAAA）記錄指到 Hub 主機的對外 IP |
| TCP 443 | 從網際網路連得到 Hub 主機的 443（家用網路要在路由器設定連接埠轉送）。憑證以 Let's Encrypt 的 TLS-ALPN-01 在 443 上驗證，**不能換成其他連接埠** |
| 443 沒有被占用 | IIS、其他網站伺服器不能聽 443；`Setup-MyriagentHub.ps1` 會檢查並警告 |
| 磁碟空間 | 預設要保留 **40 GB** 可用空間：低於它時不收新的附件、不做備份（可調整，見第 8 節） |
| 軟體 | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)、[Git](https://git-scm.com/download/win)（在 Hub 主機上建置） |
| 權限 | Hub 主機的系統管理員（只在安裝與更新時需要；服務本身以低權限的虛擬帳戶執行） |

> 為什麼一定要公開的網域與正式憑證？桌面 app 只接受瀏覽器信任的 TLS 憑證；配對時還會另外釘選伺服器的身分金鑰，之後每次連線都會驗證。手機的瀏覽器也需要正式憑證才能使用網頁版。

## 2. 取得原始碼

在 Hub 主機上，以系統管理員開啟 PowerShell：

```powershell
git clone https://github.com/clodeku/myriagent.git C:\src\myriagent
cd C:\src\myriagent
```

## 3. 一次性設定

先乾跑看看會做哪些事（不會改任何東西）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\hub\Setup-MyriagentHub.ps1 -Domain hub.example.com -AcmeEmail you@example.com -WhatIf
```

沒問題就正式執行（拿掉 `-WhatIf`）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\hub\Setup-MyriagentHub.ps1 -Domain hub.example.com -AcmeEmail you@example.com
```

腳本做的事（每一步都先檢查現況，重跑是安全的）：

1. 檢查 TCP 443 有沒有被別的程式占用。
2. 建立資料夾：程式在 `C:\Services\MyriagentHub\`，資料在 `C:\ProgramData\MyriagentHub\`。
3. 建立 Windows 服務 `MyriagentHub`：虛擬帳戶 `NT SERVICE\MyriagentHub`、延遲自動啟動、失敗時自動重啟。
4. 只在這兩個資料夾設權限：移除繼承，只有 Administrators、SYSTEM 與服務帳戶能存取。
5. 確認服務帳戶能以服務身分登入；不行的話，自動改用替代方案。
6. 防火牆規則 `MyriagentHub-HTTPS-In`：允許 TCP 443 輸入，只限這個服務。
7. 事件記錄來源 `MyriagentHub`。
8. 寫入設定檔 `C:\ProgramData\MyriagentHub\config\appsettings.Production.json`（網域、連接埠 443、開啟 Let's Encrypt）。已經存在就不改。

`-AcmeEmail` 可以省略。填了的話，Let's Encrypt 會在憑證有問題時寫信通知你。

## 4. 安裝第一版

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\hub\Update-MyriagentHub.ps1
```

腳本會依序：

1. 以目前的提交建置 MyriagentHub。自含式發佈，Hub 主機不必另外安裝 ASP.NET Core 執行階段。
2. 打包成發佈套件（zip，加上逐檔 SHA-256 的 manifest）。
3. 安裝：預檢新版能不能讀現有的資料、停止服務、停機複製資料庫、把 `current` 改指到新版、啟動，並在 60 秒內通過健康閘門（`running.json`、隔離自檢、`/healthz` 都要正常）。沒通過的話，自動退回上一版。

結束碼 0 表示已經上線。第一次啟動時，服務會先向 Let's Encrypt 的測試環境驗證一次，通過後再申請正式憑證，大約一分鐘。

### 確認

從另一台電腦（或手機的行動網路）打開：

```
https://hub.example.com/healthz      → {"status":"ok","build":"<提交前 7 碼>"}
https://hub.example.com/             → 登入頁
```

瀏覽器顯示憑證錯誤的話，表示正式憑證還沒申請成功，見第 10 節的「憑證」。

## 5. 第一次登入：立刻改密碼

擁有者帳號是 `admin`，**預設密碼是 `changeme2026`**。

1. 打開 `https://hub.example.com/`，以 `admin`／`changeme2026` 登入。
2. 在「帳戶」頁的「改密碼」設定新密碼（至少 8 個字元）。

所有使用這份原始碼的人都知道預設密碼。擁有者還在用預設密碼時，桌面會拒絕層級較高的遠端操作。忘記密碼時，可以在 Hub 主機上執行 `admin reset-owner` 改回預設密碼（第 8 節）。

## 6. 配對電腦

每台要同步的電腦都配對一次：

1. 在那台電腦的 Clode Myriagent 開啟「設定 → 多電腦同步」。
2. 「Hub 網址」填 `https://hub.example.com`，「這台的名稱」可以留空（用電腦名稱），按「連接到 Hub」。
3. 畫面會顯示配對碼、一個網址與這台的指紋。在手機或任何已登入 Hub 的瀏覽器打開那個網址（或在 Hub 的「核准電腦的配對」輸入配對碼）。
4. 確認核准頁顯示的指紋和電腦上的相同，再按核准（要再輸入一次密碼）。

配對完成後，這台電腦的帳戶清單、用量與對話會開始同步。之後：

- **網頁版**：`https://hub.example.com/app/`。手機可以把它加到主畫面。
- **遠端操作的權限**：在每台電腦的「設定 → 多電腦同步」選擇其他電腦能不能操作這台（不接受、只能查看、可以操作），另外還有「允許遠端使用完全自動」「允許其他電腦的代理在這台執行指令」兩個開關，預設都是關的。這些都只能在那台電腦本機修改，伺服器無法替它打開。
- **撤銷電腦**：在 Hub 網頁的「電腦」頁撤銷或刪除。在電腦上「解除配對」只會刪掉那台保存的同步資料，伺服器上還是要另外撤銷。

## 7. 更新 Hub

```powershell
cd C:\src\myriagent
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File tools\hub\Update-MyriagentHub.ps1
```

更新時，服務會先通知已連線的電腦稍後重連，再停機換版；停機通常不到 10 秒，桌面會自動重連。新版沒通過健康閘門就自動退回，資料庫在換版前另外複製一份到 `backups\predeploy-<版本>\`。

**手動切回某一版**（舊版還留在 `C:\Services\MyriagentHub\releases\` 裡，預設保留最近 3 版）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Services\MyriagentHub\tools\Install-MyriagentHubRelease.ps1 -SwitchTo 0.2.0-1a2b3c4
```

注意：退回只會換回程式，不會還原資料庫。新版的資料庫遷移只會新增欄位與資料表，所以前一版照樣讀得懂。

## 8. 管理指令

所有管理指令都在 Hub 主機上，以系統管理員執行（沒有任何網路管理端點）：

```powershell
C:\Services\MyriagentHub\current\MyriagentHub.exe admin <指令>
```

| 指令 | 作用 |
|---|---|
| `status` | 服務狀態、版本、憑證、伺服器身分指紋（桌面要你確認伺服器身分時，和它比對） |
| `reset-owner` | 擁有者 `admin` 的密碼改回預設、登出所有瀏覽器 |
| `backup-now` | 立刻備份一次 |
| `restore --from <備份資料夾>` | 從備份還原（先 `Stop-Service MyriagentHub`） |
| `cert-renew [--force]` | 清掉失敗的退避、立刻重新檢查憑證；`--force` 直接續期 |
| `rotate-hub-identity` | 輪換伺服器身分金鑰（各電腦下次連線時自動改釘） |
| `drain`／`undrain` | 暫停／恢復接受新的同步工作（維護前用） |
| `selftest` | 重跑隔離自檢 |
| `verify-audit` | 驗證稽核記錄的雜湊鏈 |
| `app-releases`、`app-devices`、`app-release-withdraw <版本>` | 桌面發佈：列出版本、各電腦的版本、撤回一版（[release.md](release.md)） |
| `stop` | 正常停止服務 |

### 資料夾與記錄

| 位置 | 內容 |
|---|---|
| `C:\ProgramData\MyriagentHub\config\` | 設定檔（換版不會被覆蓋） |
| `…\db\` | `hub.db`（帳號、電腦、稽核）、`sync.db`（同步資料），SQLite |
| `…\secrets\` | 伺服器身分金鑰、ACME 帳戶金鑰、TLS 憑證、Data Protection 金鑰（DPAPI 加密，只有這台電腦解得開） |
| `…\backups\daily\` | 每日備份（本機時間 03:30；保留 7 份每日加 4 份每週，合計 100 GB 以內） |
| `…\blobs\` | 附件、縮圖等內容 |
| `…\diag\myriagenthub-yyyyMMdd.log` | 服務記錄（JSON 行，保留 14 天） |
| Windows 事件檢視器 → 應用程式 | 來源 `MyriagentHub` 的啟動、停止與錯誤 |

### 常用設定

設定寫在 `C:\ProgramData\MyriagentHub\config\appsettings.Production.json` 的 `Hub` 區段，改完重新啟動服務（`Restart-Service MyriagentHub`）。完整清單見 [src/MyriagentHub/README.md](../src/MyriagentHub/README.md)。

```json
{
  "Hub": {
    "ServerDomain": "hub.example.com",
    "DataDir": "C:\\ProgramData\\MyriagentHub",
    "HttpsPort": 443,
    "Acme": { "Enabled": true, "Directory": "https://acme-v02.api.letsencrypt.org/directory", "Email": "" },
    "Backup": { "DailyAt": "03:30", "MinFreeBytes": 42949672960 },
    "Media": { "QuotaBytes": 214748364800, "MinFreeBytes": 42949672960 }
  }
}
```

磁碟比較小的話，可以調低 `Backup:MinFreeBytes` 與 `Media:MinFreeBytes`（單位是位元組；預設 40 GB），以及附件的總配額 `Media:QuotaBytes`（預設 200 GB）。

## 9. 安全模型

- **單一擁有者**：伺服器只有一個擁有者帳號 `admin`，另外可以建立只能使用被分享對話的協作人帳號。工作階段在伺服器端，cookie 使用 `__Host-` 前綴與 `SameSite=Strict`；登入有限流。
- **配對要你核准**：電腦配對時以裝置金鑰（盡量放在 TPM）證明身分，核准頁顯示指紋給你比對；伺服器的身分金鑰在配對時被電腦釘選，之後被冒名會被擋下。
- **AI 帳戶的憑證不上伺服器**：Claude、Codex、Grok、Antigravity 的登入只存在各自的電腦。伺服器沒有、也不轉送任何 AI 帳戶的權杖。
- **遠端操作由電腦本機把關**：伺服器簽發的每一個遠端指令都帶有效期限與目標，電腦會重新驗證並套用自己的政策，至多執行一次，並留下本機的遠端操作紀錄。`myriagent-remote` 一律以那台電腦的一般權限執行，app 以系統管理員執行時整個不接受；判斷為高風險的指令，要那台的人在現場同意。
- **服務以低權限執行**：虛擬帳戶只能讀寫自己的兩個資料夾。每次啟動都會跑唯讀的隔離自檢，確認它碰不到不該碰的位置。
- **對話內容**：伺服器會保存同步過來的對話內容（以便網頁版與其他電腦閱讀）。上傳之前，桌面會遮蔽常見格式的權杖與金鑰。Hub 主機請比照存放原始碼與工作資料的電腦，做好防護。

## 10. 疑難排解

**`Setup-MyriagentHub.ps1` 警告 443 被占用**：用 `Get-NetTCPConnection -LocalPort 443 -State Listen` 找出是哪個程式（常見的是 IIS 的 Default Web Site 或其他網站伺服器），停掉或改連接埠後重跑。

**憑證**：瀏覽器顯示憑證錯誤，或 `admin status` 顯示仍是自簽憑證時：

1. 從外部確認網域解析到 Hub 主機的對外 IP，而且 443 連得到（防火牆、路由器的連接埠轉送）。
2. DNS 有 CAA 記錄的話，要允許 `letsencrypt.org`。
3. 看 `diag\myriagenthub-*.log` 裡 ACME 的錯誤。修好之後執行 `admin cert-renew`。
4. 連續失敗時服務會退避，不會一直打 Let's Encrypt（以免碰到它的速率限制）。

**`Update-MyriagentHub.ps1` 結束碼 2（已退回）**：新版沒通過健康閘門。看 `C:\Services\MyriagentHub\logs\deploy-*.log` 與事件檢視器裡的錯誤。第一次安裝失敗時，服務會留在停止狀態。

**結束碼 4（需要系統管理員處理）**：服務停不下來，或退回失敗。依訊息用 `sc query MyriagentHub` 查看狀態；服務停止之後，用 `-SwitchTo` 切到已知可用的版本。

**電腦顯示「伺服器身分換了」**：在 Hub 主機執行 `admin status`，比對畫面上的指紋。相同才在電腦上確認；不同表示連到的不是你的伺服器，不要確認。

**忘記擁有者密碼**：`admin reset-owner` 會改回預設密碼 `changeme2026`；登入後立刻改掉。

**磁碟快滿**：低於 40 GB 時不收新的附件（網頁上傳回 507），備份也會跳過。清出空間，或照第 8 節調低下限。
