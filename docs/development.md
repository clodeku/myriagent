# 開發說明

## 專案結構

| 路徑 | 內容 |
|---|---|
| `src/Myriagent` | 桌面 app：WinForms 殼＋WebView2。`Hub/`（中樞：對話、佇列、換帳戶、接續…）、`Agents/`（各 CLI 的會話層）、`Accounts/`、`Cli/`、`Sync/`（同步引擎）、`Update/`（自我更新）、`Web/`（網頁介面，純顯示層） |
| `src/Myriagent.Protocol` | 桌面與伺服器共用的模型、純函式規則、同步協定 DTO、信任與簽章、項目渲染（只能參考 BCL、Markdig、Newtonsoft.Json，不能有 P/Invoke 或啟動程序的程式碼） |
| `src/MyriagentHub` | 同步伺服器：ASP.NET Core＋SignalR＋SQLite，Windows 服務；`wwwroot/` 是登入與管理頁，網頁版 `/app/` 是嵌入的 `src/Myriagent/Web/**` |
| `src/MyriagentRemote`、`src/MyriagentSchedule` | 給代理用的命令列工具（經具名管道和這台的 app 說話） |
| `tests/Myriagent.Tests`、`tests/MyriagentHub.Tests` | xUnit 單元與整合測試 |
| `tools/` | 端到端、探針、部署與發佈工具（各自建置，用法見各自的 README） |
| `docs/` | 使用與營運文件、協定文件 |

## 建置與測試

```powershell
dotnet build Myriagent.slnx -c Release          # 最低驗證標準：0 錯誤
dotnet test Myriagent.slnx -c Release           # 桌面與 MyriagentHub 的單元測試
```

- 桌面測試裡的 `Sync.WithHub` 會從建置輸出（`src\MyriagentHub\bin\Release\net10.0\win-x64\`）複製 MyriagentHub.exe 到暫存資料夾執行，所以要先建置整個 slnx。
- app 執行中時建置會因為檔案被鎖住而失敗（MSB3027），先關掉 app。多個建置同時跑時加 `-nodeReuse:false`。
- 用獨立的資料夾執行、不碰平常的帳戶與對話：`dotnet run --project src/Myriagent -- --data <暫存資料夾>`。

### 選擇性的測試（以環境變數打開）

| 指令 | 內容 |
|---|---|
| `$env:MYRIAGENT_IT=1; dotnet test tests/Myriagent.Tests -c Release --filter "FullyQualifiedName~Integration"` | 真的 CLI（會用到訂閱額度） |
| `$env:MYRIAGENT_SYNC_IT=1; dotnet test tests/Myriagent.Tests -c Release --filter "FullyQualifiedName~Sync.LongRun."` | 同步的長情境（對 MyriagentHub 子程序，約 6 分鐘） |
| `$env:MYRIAGENT_LAUNCH_IT=1; … --filter "FullyQualifiedName~LaunchDotNetIntegration"` | 執行列的端到端（真的 dotnet build／run） |
| `$env:MYRIAGENT_SCHED_IT=1; … --filter "FullyQualifiedName~TaskSchedulerReaderTests"` | 排程頁的讀取器對真的工作排程器（建立暫時的停用排程，結束一定刪掉） |
| `$env:MYRIAGENT_VOICE_IT=1; … --filter "FullyQualifiedName~WhisperVoiceTranscriberIntegrationTests"` | 語音訊息的本機辨識 |

### 網頁介面

網頁在 `src/Myriagent/Web/`，是純顯示層：所有狀態都在 C#，訊息協定照 [web-protocol.md](web-protocol.md)，改協定時兩邊一起改，並更新文件。

```powershell
cd tools/ui-probe
npm install
node --test rules.test.mjs settingsrules.test.mjs live-rules.test.mjs   # 網頁的純規則
node probe.mjs              # 介面探針：模擬宿主截圖，輸出到 tools/ui-probe/out/
node remote-probe.mjs       # 伺服器模式（瀏覽器版）的探針
node live-probe.mjs         # 網頁即時檢視與遠端操作的探針
```

### MyriagentHub

```powershell
dotnet build tools/acme-e2e -c Release; tools/acme-e2e/bin/Release/net10.0/acme-e2e.exe   # ACME 端到端（Pebble）
cd tools/hub-e2e; npm install
node hub-e2e.mjs            # 帳號密碼登入
node gateway.mjs            # 瀏覽器閘道
node pairing.mjs            # 配對與儀表板（要先建置 tools/fake-device）
node remote-control.mjs     # 瀏覽器遠端操作
```

- 這些工具都在暫存資料夾、自動挑選的高埠、Development 環境執行，不需要系統管理員，也不碰 443。
- 部署腳本的乾跑：`powershell -NoProfile -ExecutionPolicy Bypass -File tools/deploy/Test-MyriagentHubDeploy.ps1`（假服務、假 healthz）。

### app 自我更新

```powershell
dotnet build tools/update-e2e -c Release -nodeReuse:false -p:UseSharedCompilation=false
tools/update-e2e/bin/Release/net10.0/update-e2e.exe    # 約 1 分鐘，桌面會出現 app 視窗，不要操作
dotnet run --project tools/release-tool -c Release -- selftest   # 打包、簽章、驗證與竄改偵測（暫時金鑰）
```

改到 `src/Myriagent/Update/`、`Program.cs` 的啟動參數或換版流程時一定要跑 update-e2e。它只用暫存資料夾；測試金鑰只經 `-p:MyriagentUpdateTestKeySpki` 注入，app 只在 `MYRIAGENT_UPDATE_E2E=1` 時認。

## 設計重點

- **狀態只在 UI 執行緒**：`AgentHub`、`AccountManager` 不加鎖；背景工作（程序輸出、查詢、計時器）一律回到 UI 執行緒再改狀態。
- **子程序的輸出用專用執行緒讀**（`Core/PipeReadThread.cs`）：重新導向的管線讀取會長時間佔住執行緒集區的執行緒。
- **代理種類一律 `switch`**：`AgentProvider` 的分派寫成 `switch`，未知值丟例外。新增種類時，同步的分類（桌面 `SyncProviderRules`、伺服器 `AccountViewRules`）也要補上。
- **多帳戶隔離**：子程序的環境只經 `AccountRules.ApplyEnvironment` 設定（`CLAUDE_CONFIG_DIR`、`CODEX_HOME`、`GROK_HOME`，剝掉 API Key）。
- **資料相容**：新版寫出的資料，前一版必須讀得懂（只加欄位、列舉存名稱）。退回前一版時，前一版會讀到新版寫的檔。
- **同步不變式**（[sync-protocol.md](sync-protocol.md)）：排序只看伺服器的 `seq` 與對話內的 `Ord`，不比較各電腦的時鐘；每個對話只有一個寫入者；伺服器 fsync 之後才確認；遠端指令至多執行一次；AI 帳戶的憑證永不離開電腦；沒有配對時不載入任何同步元件。
- **SQLite 不用連線池**：伺服器的連線字串只由 `SqliteSetup.ConnectionString` 產生、桌面只由 `SyncStore.ConnectionString` 產生（`Pooling=False`）。
- **網頁效能**：無限循環的動畫只能動 `transform`／`opacity`；不要把 `:has()` 掛在包住大片畫面的容器上；網頁不聽 `animation*` 事件；有控制列的影片放在 iframe 裡。`tools/ui-probe` 的 `node probe.mjs --perf` 把關。
- **網頁安全**：代理產生的文字一律用 `textContent`，只有 `*html` 欄位（Markdig 停用原始 HTML）可以用 `innerHTML`；CSP 禁止 inline script；`src/Myriagent/Web/` 保持平的（不能有子資料夾：它以 `app/<檔名>` 嵌入伺服器）。

## 程式風格

- 註解、UI 文字與 commit 訊息使用臺灣繁體中文。
- C#：Allman 大括號、4 空白縮排、file-scoped namespace；`async void` 只用在事件處理器。
- 純函式規則放在 `*Rules.cs`，並附測試；新行為先寫規則，再接進流程。
- 文字檔以 LF 存進 repo（`.gitattributes`）。
- 給 Windows PowerShell 5.1 執行的腳本（`tools/hub`、`tools/deploy`、`tools/release`）只用 ASCII。

## 關於程式註解裡的文件

程式註解與部分工具的 README 會提到 `docs/plans/*.md`（設計計畫）、`docs/protocol-notes.md`（CLI 協定的實測紀錄）、`docs/ops/*.md` 與 WP、U1 之類的工作包編號。這些是原專案開發過程的內部紀錄，公開版沒有收錄；需要的協定細節都在 [web-protocol.md](web-protocol.md) 與 [sync-protocol.md](sync-protocol.md)。

## 授權

貢獻的程式碼同樣以 [PolyForm Noncommercial License 1.0.0](../LICENSE) 授權。
