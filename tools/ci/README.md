# tools/ci

測試與建置的輔助腳本（不在 Hub 主機上執行；照 repo 的 PowerShell 慣例只寫 ASCII，Windows PowerShell 5.1 與 PowerShell 7 都能跑）。

## Invoke-ShardedTests.ps1

把一個測試專案分成幾個同時跑的 `dotnet test` 程序，失敗的測試再重跑一次。原本是 `desktop-release.yml` 的桌面單元測試用它；2026-10-22 起雲端不跑測試，發佈前在開發機照 `docs/release.md` 第 2 節直接 `dotnet test`，這支留著備用。要先建置 slnx（`--no-build`）。

```powershell
pwsh tools/ci/Invoke-ShardedTests.ps1 -Project tests/Myriagent.Tests/Myriagent.Tests.csproj `
    -Shard 'Myriagent.Tests.Hub', 'Myriagent.Tests.Accounts,Myriagent.Tests.Sync.Wiring', 'Myriagent.Tests.Sync.WithHub,Myriagent.Tests.FakeDevice'
# pwsh -File 呼叫時把各份以 ; 隔開放在同一個字串；-BaseFilter 只跑一部分（試腳本用）
```

- **為什麼分程序**：xUnit v2 等可以平行的測試都跑完，才一個一個跑 `DisableParallelization` 的集合。Myriagent.Tests 裡這些集合（AppPaths、MyriagentHub 子程序、DLP 樣式、記憶體量測）合計約 12 分鐘，平行的部分只要 1～2 分鐘。它們護的是程序內的全域狀態，分到不同程序就能同時跑。
- **怎麼分**：每一份是一串命名空間前綴（FullyQualifiedName 以「前綴.」開頭）；另外一定有「其他全部」那一份，所以新加的測試一定有人跑。前綴不能互相包含；某一份一個測試都沒跑到就失敗（前綴打錯或命名空間改名）。分法依 2026-10-22 的耗時：本機 TRX 的循序部分 Hub 約 230 秒、Accounts＋Sync.Wiring 約 200 秒、Sync.WithHub＋FakeDevice（MyriagentHub 子程序）約 150 秒，其他（DLP 的發佈測試約 110 秒、Sync.Engine 約 45 秒＋大部分平行測試）；雲端第一次實測各份 487／387／524／337 秒（那時 Sync.Engine 在第 3 份），之後把 Sync.Engine 移到其他。某一份明顯變慢時，照 TRX 的耗時重新分（`--logger trx` 的 `duration`）。
- **重跑**：失敗 ≤ `-MaxRetry`（預設 5）個、而且每個失敗的程序都在 TRX 列出失敗的測試時，把那幾個測試在一個程序裡重跑一次；全過就通過，並以 `::warning::` 與 job 摘要列出（雲端 4 核心的機器上有幾個時序敏感的測試會偶發失敗）。更多失敗、程序當掉、重跑仍失敗都失敗。
- **輸出**：各程序的主控台輸出（GitHub 上收成可展開的群組）、每一份的測試數、失敗數與秒數，`GITHUB_STEP_SUMMARY` 有設定時也寫進 job 摘要；TRX 與記錄檔在 `-ResultsDir`。
