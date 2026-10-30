# 安裝 Clode Myriagent（桌面 app）

這份文件說明怎麼在一台 Windows 電腦上建置、安裝與更新桌面 app。只用一台電腦的話，看完這份就能開始使用；要讓多台電腦同步、用手機操作，另外照 [hub-setup.md](hub-setup.md) 架設 MyriagentHub。

## 1. 準備

| 項目 | 說明 |
|---|---|
| 作業系統 | Windows 10 22H2 或 Windows 11，x64 |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | 建置用；裝了 SDK 就有執行需要的 Desktop Runtime |
| [Git](https://git-scm.com/download/win) | 取得原始碼；版本號也取自 git 的提交數 |
| WebView2 Runtime | Windows 11 內建；Windows 10 沒有的話從 [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/) 安裝 |
| 代理 CLI（至少一種） | 見下一節；也可以先不裝，開啟 app 後在「設定 → CLI」安裝 |
| 選用：ffmpeg | 放在 PATH 上時，附加的影片會擷取畫面給代理看；語音訊息的解碼也用它 |
| 選用：Python 3＋faster-whisper | 語音訊息（錄音轉逐字稿）用；沒有的話麥克風鈕不會出現 |

### 代理 CLI

每種代理都要用它自己的官方 CLI 與訂閱帳戶。只要裝你有訂閱的那幾種：

| 代理 | CLI | 安裝方式 |
|---|---|---|
| Claude Code | `claude` | [官方文件](https://docs.claude.com/claude-code) |
| Codex | `codex` | [openai/codex](https://github.com/openai/codex) |
| Grok Build | `grok` | [x.ai/cli](https://x.ai/cli)：`irm https://x.ai/cli/install.ps1 \| iex` |
| Antigravity | `agy` | [官方文件](https://antigravity.google/docs/cli) |

CLI 的登入不必先做，在 app 裡登入就好（見 [usage.md](usage.md) 的「帳戶」）。

## 2. 取得原始碼與建置

```powershell
git clone https://github.com/clodeku/myriagent.git
cd myriagent
dotnet build Myriagent.slnx -c Release
```

建置會產生桌面 app、伺服器與兩個測試專案，最後應該顯示「0 個錯誤」。想確認一切正常，可以跑單元測試（約 15 分鐘）：

```powershell
dotnet test Myriagent.slnx -c Release
```

## 3. 試用（不安裝）

```powershell
dotnet run --project src/Myriagent -c Release
```

這樣執行的是「開發版」：功能完整，但不會自動更新，也不能設定開機自動啟動。資料一樣放在 `%LOCALAPPDATA%\Myriagent\`（第 6 節），之後安裝時會沿用同一份帳戶與對話。

想用另一個資料夾試用、完全不碰平常的帳戶與對話，可以這樣執行：

```powershell
dotnet run --project src/Myriagent -c Release -- --data D:\myriagent-test
```

## 4. 安裝到這台電腦（建議）

受管理的安裝會放在 `%LOCALAPPDATA%\Programs\Myriagent\`，並建立開始選單捷徑「Clode Myriagent」、在「設定 → 應用程式」登記移除項目，之後才能設定開機自動啟動與自動更新。不需要系統管理員權限。

1. 以這次提交的版本號發佈一份：

   ```powershell
   $commit = git rev-parse HEAD
   $count = git rev-list --count HEAD
   dotnet publish src/Myriagent/Myriagent.csproj -c Release -r win-x64 --self-contained false -o out\desktop `
       -p:Version=0.2.$count -p:SourceRevisionId=$commit
   ```

   版本號是 `Myriagent.csproj` 的「主.次」加上提交數，所以每個新提交的版本都比較大。`SourceRevisionId` 讓 app 知道自己是哪一個提交；沒有它的建置會被當成開發版，不能安裝。

2. 執行 `out\desktop\Myriagent.exe`。
3. 到「設定 → 版本與更新」按「安裝到這台電腦」。app 會把自己複製到安裝位置，然後從那裡重新啟動。之後一律從開始選單的「Clode Myriagent」開啟。
4. 想要開機自動啟動，在「設定 → 一般」打開「開機時自動啟動」。

`out\desktop` 資料夾安裝完就可以刪掉。

## 5. 更新

### 有 MyriagentHub 的話：自動更新

配對到你的 Hub、而且你用自己的金鑰發佈新版之後，每台電腦會在背景下載並驗證簽章，等你沒在使用時，讓所有對話停在安全點再換版。新版通過健康檢查才算數；失敗或之後連續閃退，會自動退回前一版。換版後，停下的對話會自動接續。設定方法見 [release.md](release.md)。

### 沒有 Hub：手動更新

```powershell
git pull
# 照第 4 節第 1 步重新發佈（提交數會變大）
out\desktop\Myriagent.exe
```

新版會自動請正在執行的舊版交棒。舊版排空對話後結束，新版接手並提示你安裝；在「設定 → 版本與更新」按「安裝到這台電腦」即可。帳戶、對話與設定都會沿用。

## 6. 資料放在哪裡

| 位置 | 內容 |
|---|---|
| `%LOCALAPPDATA%\Myriagent\` | 資料根目錄：`settings.json`、`accounts.json`、`conversations\{對話}\`（紀錄、附件、排隊中的訊息、預設工作資料夾）、`usage\`、`schedules\`、`logs\` |
| `%LOCALAPPDATA%\Myriagent\accounts\{帳戶}\` | 在 app 裡新增的隔離帳戶的設定資料夾（Claude 的 `CLAUDE_CONFIG_DIR`、Codex 的 `CODEX_HOME`、Grok 的 `GROK_HOME`），內含該帳戶的登入憑證 |
| `~\.claude`、`~\.codex`、`~\.grok` | 這台電腦原本的 CLI 帳戶（app 稱為「預設帳戶」，直接沿用，不搬移） |
| `%LOCALAPPDATA%\Programs\Myriagent\` | 受管理的安裝：`versions\<版本>\`、指向目前版的 `current`、`updater-state.json` |

執行時加 `--data <資料夾>`，可以把資料根目錄換到別處。不同資料根目錄的 app 可以同時執行，彼此不影響。

## 7. 移除

到「設定 → 應用程式」找到「Clode Myriagent」，按「解除安裝」。移除只會刪掉程式、捷徑與開機自動啟動，**資料根目錄會保留**；確定不要了，再手動刪除 `%LOCALAPPDATA%\Myriagent\`。各 CLI 原本的 `~\.claude`、`~\.codex` 不受影響。

## 8. 安全須知

- 子程序一律剝除 `ANTHROPIC_API_KEY`、`OPENAI_API_KEY`、`XAI_API_KEY` 等環境變數，確保走訂閱額度，不會意外產生 API 費用。
- app 不會自己更新（refresh）Claude 的 OAuth 權杖，以免把 CLI 登出。閒置帳戶的權杖過期時，會借用同一個身分的其他權杖唯讀查用量；借不到時，會開一個不送訊息的 Claude 程序代查。後者每個帳戶每小時最多一次，可以在設定關掉。
- 介面有嚴格的 CSP，代理回覆的 Markdown 停用原始 HTML，外站圖片不自動載入；執行檔與腳本只在檔案總管中選取，不直接執行；UNC 路徑一律不處理。
- 「完全自動」權限模式下，代理會不經詢問就執行指令、修改檔案。只在你信任、而且有備份的工作資料夾使用。
