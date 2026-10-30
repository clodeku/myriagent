# 在對話中 @ 其他 AI 帳戶

在桌面版 Claude Code 或 Codex 的對話輸入 `@`，從清單選取另一個已登入的本機 AI 帳戶，再輸入要交辦的工作，例如「請這個 Codex 帳戶檢查測試，你負責修正並整合結果」。檔案搜尋仍在同一個選單（帳戶列在前面）。

選取後插入 `@[Codex 帳戶](account:帳戶id)`，以 id 識別帳戶，改名與同名都不影響指向。直接輸入 `@Codex` 會先顯示符合的選項，需要選取帳戶才完成提及。選取的帳戶隨主對話保存在本機 `meta.json` 的 `DelegatedAccountIds`，後續回合仍可委派；子對話的 `ParentConversationId` 指向來源。兩個欄位都只加不改（舊版忽略），同步的 meta payload 是逐欄挑選的，不會送到伺服器。

主 AI 透過 `myriagent_accounts` MCP 的 `list_accounts` 與 `delegate_task(accountId, prompt)` 交辦工作，只有使用者選取過的帳戶能被呼叫。使用該帳戶既有的 CLI 與登入環境（Claude 的 `CLAUDE_CONFIG_DIR`、Codex 的 `CODEX_HOME` 等），不另建 API 連線。送給主 AI 的訊息後面附一段 `<myriagent_account_delegation>` 說明（畫面上的使用者訊息不含）：這則訊息有 @ 時寫明對象的名稱與 accountId，之後的回合只提醒可以委派、使用者要求時才委派（`DelegationMcpRules.DelegationHint`）。Claude 以 `--allowedTools` 放行這兩個工具（@ 就是授權），不另外跳核准卡。

委派會建立一般子對話，共用主對話的工作資料夾；使用者可以從對話清單查看過程、回應問卷及核准。子對話沿用目標帳戶的模型預設值，權限使用「每步詢問」（不能提供核准協定的代理照其既有模式正規化），不自動繼承完全自動。主對話在規劃模式時不執行委派。子 AI 只取得委派的工作說明，不會自動收到主對話的全部紀錄或附件，主 AI 應在說明中補齊所需內容。

工作完成後，工具回傳子對話 id、帳戶 id、完成狀態與子對話這一輪的助理文字（超過 64000 字保留結尾，`truncated` 為 true），主 AI 接著整合結果。失敗、停止、刪除與連線取消都不回報成功；主對話停止或刪除會停止尚未完成的子工作。每個主對話最多同時 3 個委派，子對話不再往下委派（工作說明裡的提及格式只當文字）。精簡模式、其他電腦的副本，以及瀏覽器遠端送入的回合不開放這項功能。

一個委派最多等 4 小時（含子對話等使用者核准；Claude 的 `MCP_TOOL_TIMEOUT`、Codex 的 `tool_timeout_sec` 都設成這個值，Codex 預設只有 60 秒），逾時由 CLI 取消工具呼叫，子工作跟著停止。

MCP 只在對話 @ 過帳戶之後才掛上，沒用到委派的對話不多開橋接程序。第一次 @ 時管道名稱從無到有，會話在回合之間重開程序（Claude 的 `NeedsRestart`、Codex 的程序簽章都包含管道名稱；Claude 以 `--resume` 接回），同一次 app 執行期間名稱不變。MCP 只透過目前 Windows 使用者可連線的隨機命名管道，橋接程序是 app 自己的 exe 以 `--delegation-mcp` 啟動，不取得桌面單一實例鎖、不初始化帳戶、不開視窗。設定只放在 CLI 啟動參數，不修改使用者的全域 MCP 設定，也不加 `--strict-mcp-config`（使用者自己的 MCP 照常載入）：Claude 是 `--mcp-config` 的一個 JSON 字串，隔離帳戶共用設定的 MCP 檔也要帶時兩個值放在同一個 `--mcp-config` 之後（它可帶多個、吃掉後面所有不是旗標的參數）；Codex 是 app-server 的 `-c mcp_servers.myriagent_accounts.*`。精簡模式不載入 MCP，也就不掛、不放行。

執行中送出的訊息平常會直接插進這一輪（`SteerAsync`），但 @ 了 AI 帳戶的訊息不插：它要在回合之間重開程序掛上 MCP，所以照舊排隊、等這一輪結束再送（`CanSteerQueued`）。

命名：委派這一組是 `DelegationMcpRules`、`DelegationMcpServer`、`DelegationMcpBridge`（`src/Myriagent/Agents/`），和儀表板查帳戶 MCP 狀態的 `Hub/AccountMcpRules`、`AgentHub.AccountMcp.cs` 無關。

實作參考：[Codex MCP 設定](https://developers.openai.com/codex/mcp/)、[Claude Code MCP](https://code.claude.com/docs/en/mcp)。

驗證：`DelegationMcpRulesTests`、`AgentHubDelegationTests`、`DelegationMcpBridgeTests`（要先建置 slnx，啟動真的 exe 走 stdio）與 `tools/ui-probe/accountmentions.test.mjs`。測試以假的 AI session 執行，不使用真的訂閱額度；真的 Claude／Codex CLI 是否載入工具、能否完成一次委派，要在 app 裡實際操作確認。
