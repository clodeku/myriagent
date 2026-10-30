# 完全自動與等待中的工具核准

2026-10-27：使用者要求四家代理在切到「完全自動」時，都直接放行已等待的工具核准。

## 問題與處理

**問題 →** Claude、Grok 切到完全自動後，已出現的核准卡仍要再點一次。

**原因 →** 切換 CLI 的權限模式與回覆既有的核准請求是兩件事。CLI 接受模式變更，不代表它會重新評估已送給宿主的請求。原本只有 Codex 另外發出自動核准事件。

**正確做法 →** 由 `AgentHub` 統一執行使用者選的完全自動模式：

- `SetOption` 選到 `FullAccess` 時，以現有的 `ApproveAsync` 回覆全部等待中的工具核准，包含主回合已結束後背景工作提出的核准。
- 收到新的 `AgentApprovalEvent` 時再次看目前模式，涵蓋切換時還在 UI 事件佇列中的請求；已自動放行的不發等待核准通知。
- CLI 發出的 `AgentAutoApproveEvent` 也必須檢查目前模式，使用者切回其他模式後，晚到的舊通知不能再放行。
- 只允許這一次，不建立「本對話都允許」規則。問卷、計畫確認、已拒絕或已處理的卡片不變；回覆仍檢查程序實例，過期的卡片不能送給新程序。

四家共用同一條中樞處理路徑。Antigravity 目前的 stream-json 沒有一般工具核准協定，所以正常不會出現這種卡片；它的程序權限仍在下一輪重開續接時套用，計畫確認也維持原本流程。

## 驗證

`AgentHubAutoApprovalTests` 使用四家假會話驗證等待中的多張卡片、切換與事件抵達的先後順序、切回自動後的詢問、背景工作，以及問卷與計畫確認的保留。既有的 `AgentHubTests` 驗證重複通知不重複回覆。

```powershell
dotnet build Myriagent.slnx -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet test Myriagent.slnx -c Release --no-build --no-restore -m:1 -nodeReuse:false
```
