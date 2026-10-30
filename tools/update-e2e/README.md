# update-e2e：app 自我更新的端到端

以這次產生的 P-256 測試金鑰建出三個版本（0.2.9001-e2e0001、0.2.9002-e2e0002、0.2.9003-e2e0003），安裝到工作資料夾裡的受管理位置，從本機資料夾來源依序跑（竄改與錯誤金鑰用不建置的假發佈：它們會被列壞版或這次執行不再重試，不能用之後要安裝的版本）：

| 步驟 | 來源放的版本 | 預期 |
|---|---|---|
| 竄改的 zip | 假發佈 0.2.9008，簽章後改掉 zip 的一個位元組 | 不換版、沒有留下 `versions\` 資料夾；重新下載一次仍錯就列壞版 |
| 不認得的金鑰 | 假發佈 0.2.9009，用另一把金鑰簽 | 不下載、不換版；這次執行不再重試（日誌只有一次「簽章不符」） |
| 換版到 v2 | v2，正確簽章 | 換版幫手切換 current、新版健康、只剩一個 app 程序、`backups\pre-<v2>\settings.json`、`update\resume.json` 用完即刪 |
| v2 藏著主視窗啟動 | （同上；來源放 v2 之前先從外部把 v1 的主視窗最小化） | v2 啟動後 3 秒內都沒有看得見的頂層視窗、日誌有「換版前主視窗藏著：這次啟動不顯示主視窗」，照樣通過健康閘門（藏著的 WebView2 照送 `ready`）；再啟動一次 app（第二個實例叫醒它）時顯示主視窗 |
| 降版 | v1，正確簽章 | 不換版、不備妥 |
| v3 閃退回滾 | v3（`MYRIAGENT_UPDATE_E2E_CRASH_RELEASE` 讓它一啟動就結束） | 兩次健康閘門失敗、v3 列壞版、留在 v2；交棒時主視窗開著，退回的 v2 照常顯示主視窗 |
| 沒有殘留 | — | `versions\.partial-*`、`staging\*.partial` 都不在 |
| 清理、真正的安裝與資料沒有變動 | — | 沒有留下 app 程序；這台電腦真正的安裝位置、app 資料夾、開始選單捷徑與移除項目和開始前一樣（見下面） |

不涵蓋（由別的測試負責）：通過健康閘門之後才連續閃退 3 次切回前一版（`LaunchGuardTests`）、跨程序接續對話（`MYRIAGENT_IT=1` 的 `AppUpdateResumeIntegrationTests`；這裡不開對話）、`--uninstall`（見下面）。

```bash
dotnet build tools/update-e2e -c Release -nodeReuse:false -p:UseSharedCompilation=false
tools/update-e2e/bin/Release/net10.0/update-e2e.exe            # 實測約 1 分鐘（obj 已建置過時每次 publish 約 5 秒；第一次建置會更久）
tools/update-e2e/bin/Release/net10.0/update-e2e.exe --keep     # 通過也保留工作資料夾（看 app 日誌、updater-state.json）
```

- 執行期間桌面上會出現 Clode Myriagent 的視窗（真的 WebView2 與健康閘門），換版時會關掉再開（v1 會被最小化，v2 先藏著、再被叫出來）；不要操作它。三次 publish 與 `src/` 的一般建置共用 `obj\`：執行期間不要在同一個 worktree 建置。
- 只動工作資料夾（預設 `%TEMP%\myriagent-tools\update-e2e\<時間>`；用 `--work` 指定時必須不存在或是空的，因為通過後會整個刪掉）：`MYRIAGENT_UPDATE_E2E_ROOT` 指到 `<work>\Programs\Myriagent`、`MYRIAGENT_AGENT_DATA` 指到 `<work>\data`；工作資料夾和真正的安裝位置或 app 資料夾重疊（同一個、在裡面或包含它們）就拒絕執行。端到端時 `--install-here` 不建開始選單捷徑、不寫「應用程式」的移除項目。
- 絕不執行 `--uninstall`：`SelfInstaller.Uninstall` 不分端到端，會刪掉真正的開始選單捷徑與移除項目。
- 結束前比對這台電腦真正的位置（開始前先記下）：`%LOCALAPPDATA%\Programs\Myriagent`、開始選單的 `Clode Myriagent.lnk`、`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\Myriagent` 整份比對；`%LOCALAPPDATA%\Myriagent` 可能正被自己的 app 使用，只比對 `backups\`、`update\` 與測試版本的 build（`e2e0001`…）在日誌裡出現的次數。執行期間自己啟動已安裝的那份 app 會改到 `updater-state.json`，這一步就會失敗。
- app 資料夾預先寫 `settings.json`：不自動更新 CLI、不跳通知、關視窗就結束。帳戶沿用新資料夾的預設（系統預設帳戶讀 `~/.claude`、`~/.codex` 查用量，只讀、不 refresh）。
- 所有 app 程序（含換版幫手與它啟動的新版、WebView2）都在 Job Object 裡，本程式怎麼結束都會一起結束；Ctrl+C 會結束進行中的 publish、記一步「中止」並保留工作資料夾。
- 讀 `updater-state.json` 時拿 app 同一個具名 mutex（`InstallLayout.StateMutexName`）：app 以取代的方式寫檔，這邊開著檔案時取代會失敗。
- 依賴的 app 日誌字串（改字樣要一起改 `UpdateScenario`）：「啟動 Clode Myriagent {版本}（{build}）」、「端到端測試：這一版模擬啟動就當掉」、「新版 {release} 驗證失敗（…），重新下載一次」、「新版 {release} 沒有備妥：{原因}」、「換版前主視窗藏著：這次啟動不顯示主視窗」。
- publish 的輸出在工作資料夾，`obj\` 與一般建置共用：跑完後下一次一般建置會重新產生組件資訊。測試金鑰只在這三個建置裡（`ReleaseKeysBuildTests` 守住一般建置）。
- 結果檔 `<work>\result.json` 每一步之後重寫；失敗時看 `<work>\data\logs\*.log` 與 `<work>\Programs\Myriagent\updater-state.json`，建置失敗看 `<work>\publish-<release>.log`。主控台最後一行有總時間。
