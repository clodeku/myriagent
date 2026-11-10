# tools/ci

測試與建置的輔助腳本（不在 Hub 主機上執行；照 repo 的 PowerShell 慣例只寫 ASCII，Windows PowerShell 5.1 與 PowerShell 7 都能跑）。

## Invoke-LocalTests.ps1

在開發機跑完整單元測試的快速做法：和 `dotnet test Myriagent.slnx -c Release` 同一批測試、每個只跑一次，兩個測試專案各分成幾個程序同時跑，預設先建置 slnx。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/Invoke-LocalTests.ps1                        # 建置＋兩個專案
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ci/Invoke-LocalTests.ps1 -NoBuild -Only Desktop  # 只跑桌面（Hub：只跑 MyriagentHub.Tests）
```

- **為什麼快**（開發機實測：`dotnet test Myriagent.slnx` 約 25 分鐘，這支約 5 分鐘）：xUnit v2 把 `DisableParallelization` 的集合排在所有平行的測試之後、一個接一個跑（AppPaths 的一百多個 class、MyriagentHub 子程序、DLP 樣式）；它們護的是程序內的全域狀態，分到不同程序就能同時跑。MyriagentHub.Tests 一開始就同時跑一百多個 class，在同一個程序裡互相拖慢，分成三個程序約快三倍。
- **怎麼分**：桌面從原始碼找出循序集合的 class（`[Collection(X.Name)]`，X 有 `DisableParallelization = true`），依上一次各 class 的耗時由長到短、每次放進目前最輕的程序（`-SerialShards`，預設 6 個，另加「其他全部」那一個：它先算平行的 class 裡最慢的那個的時間，有空檔就也收循序的 class）；`MyriagentHubCollection` 的 class 一律放在同一個程序（同一時間只有一個 MyriagentHub 子程序）。MyriagentHub.Tests 的每個 class 照同樣的方法分到 `-HubShards`（預設 3）個程序。實際啟動與重跑都交給 `Invoke-ShardedTests.ps1`，它一定另有「其他全部」那一份，新加的 class 照樣有人跑。
- **耗時紀錄**：`%TEMP%\myriagent-tests\timings\{desktop,hub}\`，存的是上一次每個程序都寫出 TRX 的那一次；沒有紀錄的 class 算 5 秒（第一次分得比較不平均，但照樣每個測試跑一次）。結果與記錄檔在 `%TEMP%\myriagent-tests\<時間>\`（預設的資料夾只留最近 5 次）。
- **重跑**：每個專案失敗 ≤ `-MaxRetry`（預設 15）個時，在一個程序裡重跑一次，全過就算通過並以 `::warning::` 列出。這麼多程序同時跑、機器又忙時，時序敏感的測試每次都會輸幾個；重跑還失敗、超過上限或程序當掉才算失敗。
- **單一 class 太慢時拆開**：xUnit 同一個 class 的測試依序跑，開很多子程序的 class（假 CLI、git）在整套裡一個就要好幾分鐘，會變成「其他全部」那個程序的下限。拆法：共用的欄位、建構式與輔助方法放在抽象的 `…Base`（`private` 改成 `private protected`），測試照原本的順序分到幾個 sealed class（例：`ClaudeSessionFakeCliTests`／`2`／`3`、`GitStatusReaderTestsBase`）。含中文的 `.ps1` 要帶 UTF-8 BOM（Windows PowerShell 5.1 讀沒有 BOM 的檔用系統碼頁，`TempDirectory.WriteFile` 已處理）。

## Invoke-ShardedTests.ps1

把一個測試專案分成幾個同時跑的 `dotnet test` 程序，失敗的測試再重跑一次。原本是 `desktop-release.yml` 的桌面單元測試用它；2026-10-22 起雲端不跑測試，現在由 `Invoke-LocalTests.ps1` 在開發機呼叫。要先建置 slnx（`--no-build`）。

```powershell
pwsh tools/ci/Invoke-ShardedTests.ps1 -Project tests/Myriagent.Tests/Myriagent.Tests.csproj `
    -Shard 'Myriagent.Tests.Hub', 'Myriagent.Tests.Accounts,Myriagent.Tests.Sync.Wiring', 'Myriagent.Tests.Sync.WithHub,Myriagent.Tests.FakeDevice'
# pwsh -File 呼叫時把各份以 ; 隔開放在同一個字串；-BaseFilter 只跑一部分（試腳本用）
```

- **為什麼分程序**：xUnit v2 等可以平行的測試都跑完，才一個一個跑 `DisableParallelization` 的集合。Myriagent.Tests 裡這些集合（AppPaths、MyriagentHub 子程序、DLP 樣式、記憶體量測）合計約 12 分鐘，平行的部分只要 1～2 分鐘。它們護的是程序內的全域狀態，分到不同程序就能同時跑。
- **怎麼分**：每一份是一串命名空間前綴（FullyQualifiedName 以「前綴.」開頭）；另外一定有「其他全部」那一份，所以新加的測試一定有人跑。前綴不能互相包含；某一份一個測試都沒跑到就失敗（前綴打錯或命名空間改名）。分法依 2026-10-22 的耗時：本機 TRX 的循序部分 Hub 約 230 秒、Accounts＋Sync.Wiring 約 200 秒、Sync.WithHub＋FakeDevice（MyriagentHub 子程序）約 150 秒，其他（DLP 的發佈測試約 110 秒、Sync.Engine 約 45 秒＋大部分平行測試）；雲端第一次實測各份 487／387／524／337 秒（那時 Sync.Engine 在第 3 份），之後把 Sync.Engine 移到其他。某一份明顯變慢時，照 TRX 的耗時重新分（`--logger trx` 的 `duration`）。
- **重跑**：失敗 ≤ `-MaxRetry`（預設 5）個、而且每個失敗的程序都在 TRX 列出失敗的測試時，把那幾個測試在一個程序裡重跑一次；全過就通過，並以 `::warning::` 與 job 摘要列出（雲端 4 核心的機器上有幾個時序敏感的測試會偶發失敗）。更多失敗、程序當掉、重跑仍失敗都失敗。
- **輸出**：各程序的主控台輸出（GitHub 上收成可展開的群組）、每一份的測試數、失敗數與秒數，`GITHUB_STEP_SUMMARY` 有設定時也寫進 job 摘要；TRX 與記錄檔在 `-ResultsDir`。
