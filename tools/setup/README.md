# tools/setup：安裝程式

把桌面 app 包成**一個 exe 的安裝程式**：使用者下載、執行、按「安裝」就好，不必先裝 .NET、不需要系統管理員。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/setup/New-Setup.ps1                 # 單機版（預設）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/setup/New-Setup.ps1 -Edition full   # 一般的 app（可以配對伺服器）
```

輸出在 `out\setup\`：`Myriagent-Setup-<版本>.exe` 與 `setup.json`（檔名、版本、大小、SHA-256）。版本是 csproj 的「主.次」加 HEAD 的提交數（同桌面發佈）；不在 git 工作樹裡建置時要給 `-Commit`、`-CommitCount`（與選填的 `-CommitTime`）。

## 兩種版本

| | 單機版（`standalone`） | 一般（`full`） |
|---|---|---|
| 多電腦同步、配對、協作人 | 沒有（不讀配對資料、不接受配對；設定頁沒有這兩頁） | 有 |
| 自動更新 | 沒有（沒有更新來源）；要更新就再跑一次新版的安裝程式 | 配對伺服器之後由伺服器發佈 |
| 其他（多帳戶、對話、用量、排程、電腦狀態…） | 相同 | 相同 |

單機版是建置時決定的（`-p:AppEdition=standalone` → 組件中繼資料，`src/Myriagent/Core/AppEdition.cs`），執行時改不了。

## 怎麼運作

1. `dotnet publish`（Release、win-x64、**self-contained**：.NET 執行階段一起帶著）→ 壓成 `payload.zip`。
2. `tools/setup/Setup.csproj`（.NET Framework 4.8 的 WinForms 小程式，Windows 10 1903 起與 11 內建）把 `payload.zip` 當成嵌入資源編進去。
3. 使用者執行安裝程式：解到 `%TEMP%` 的新資料夾 → 執行裡面的 `Myriagent.exe --install-package` → app 自己把整個資料夾安裝到 `%LOCALAPPDATA%\Programs\Myriagent\versions\<版本>\`、`current` 指過去、建開始選單捷徑與「應用程式」裡的移除項目，然後啟動裝好的那一份 → 安裝程式刪掉暫存資料夾。
   - 已經裝過：換成這一版，帳戶與對話（資料夾在 `%LOCALAPPDATA%\Myriagent`）不動；舊版還在執行時，新版啟動後舊版會自己交棒。
   - 重裝同一版而且它正在執行：沿用原本的檔案（不會刪到正在用的檔）。
   - 失敗：app 把原因寫在暫存資料夾的 `install-error.txt`，安裝程式顯示出來。
4. 移除：Windows 設定 → 應用程式 → 解除安裝（`Myriagent.exe --uninstall`）；資料保留。

安裝程式的參數：`/quiet`（不顯示視窗，結束碼 0 成功、1 失敗）、`/nolaunch`（裝好不啟動）。從 Git Bash 執行時 `/` 開頭的參數會被改成路徑，改寫成 `-quiet`、`-nolaunch`（一樣認得）或用 PowerShell。

## 注意

- **沒有程式碼簽章**：第一次執行時 Windows SmartScreen 會警告（「其他資訊」→「仍要執行」）。要簽章得自己準備憑證，在 `New-Setup.ps1` 產出之後以 `signtool` 簽。
- 需要 Microsoft Edge WebView2 Runtime（Windows 11 內建，Windows 10 通常已隨 Edge 安裝）；沒有時 app 啟動會說明。
- 測試安裝流程不要碰真的安裝位置：設 `MYRIAGENT_UPDATE_E2E=1`、`MYRIAGENT_UPDATE_E2E_ROOT=<暫存>\root`、`MYRIAGENT_AGENT_DATA=<暫存>\data` 再以 `/quiet /nolaunch` 執行（端到端模式不建捷徑與移除項目）。
- `tools/setup` 不在 slnx：改了 `Program.cs`、`Setup.csproj` 要自己建一次（`dotnet build tools/setup/Setup.csproj -c Release`；沒給 `-p:PayloadZip` 也建得起來）。
