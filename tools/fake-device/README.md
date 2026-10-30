# 假裝置（tools/fake-device）

第 3 階段 WP3.3 第 14 步（計畫 `docs/plans/2026-10-16-wp3.3-desktop-sync.md`）：沒有視窗的「一台桌面」，直接用桌面 app 的中樞（`AgentHub`）、帳戶管理員、同步協調者與同步引擎，對**本機自己啟動的** MyriagentHub 配對、跑目錄與用量的劇本、做出積壓。第 15 步的長情境測試（S12 用量指派）以連結編譯用同一套元件（`Runtime/`）。不進 `Myriagent.slnx`。

WP4.3 步 13（計畫 `docs/plans/2026-10-19-wp4.3-desktop-publish.md`）加上對話：劇本化的會話（文字串流、工具、問卷卡、核准卡、媒體）、`chat` 操作，與可重現的「N 個舊對話」（`seedConversations`），給 `Sync.WithHub` 的對話情境（`ConversationScenarioTests`，同樣以連結編譯）與 `measure_sync.py --conversations` 用。

```bash
dotnet build tools/fake-device -c Release -nodeReuse:false -p:UseSharedCompilation=false
tools/fake-device/bin/Release/net10.0-windows/win-x64/FakeDevice.exe --data <資料根> <指令> [選項]
```

## 不碰這台電腦真的帳戶、不耗額度

- **不呼叫 `AccountManager.Initialize()`**：它在全新的資料根會建立指向使用者真正 `~/.claude`、`~/.codex` 的系統預設帳戶，並對每個帳戶查狀態與用量。帳戶一律以 `Seed` 放入，而且**只放隔離帳戶**（設定資料夾在 `<資料根>\accounts\{id}\claude|codex|grok`）；唯一的例外是 Antigravity（見下面的「Grok 與 Antigravity」）。`accounts.json` 裡任何其他帳戶（系統預設帳戶、資料夾不在這個資料根的、不是假裝置建的 Antigravity）啟動時略過並記一行警告。
- **不執行任何 CLI**：CLI 管理員的「找安裝」是假的（只認劇本 `cli` 設定的假路徑 `<資料根>\fake-cli\*.exe`，檔案不存在也沒關係）、不跑背景檢查、不查最新版、執行指令一律丟例外；帳戶管理員拿不到任何 CLI（`ResolveLauncher` 一律 null；唯一的例外是 `--fake-codex`，見下面的「假 Codex app-server」）、開瀏覽器只計數（`openedBrowser`）；對話是 `ScriptedSession`（不啟動程序）。
- **不連外網**：整個 `HttpClient` 是 `FakeUsageEndpoint`，只回應 Claude 的官方用量網址，而且只認假權杖（`fake-device-<帳戶 id>`，寫在帳戶資料夾的假 `.credentials.json`，一年後到期）；其他網址一律 404（計數，結束時大於 0 會印出來）。配對與同步連線用自己的連線。
- Claude 的 `poll` 走真的路徑（用量查詢閘門 → `RefreshAsync(includeStatus:false)` → api 快照）。Codex 的 `poll` 不經 app-server（`--fake-codex` 時也一樣）：閘門（`ShouldPoll`）放行時直接放入 `account/rateLimits/read` 格式解析出的 api 快照（`SeedUsage`），之後的鏡像 → 引擎 → `UsagePublish` 路徑和真的相同。
- 擁有者密碼只從環境變數讀（`--owner-password-env 變數名稱`），不放命令列，不寫進記錄與輸出。
- 拒絕使用正常使用的資料根（`%LOCALAPPDATA%\Myriagent`）。**只對本機自己啟動的 MyriagentHub 用**（開發信任 `--dev-trust` 也只在伺服器是迴路位址時生效）。

## 指令

結果以**單行 JSON** 印到標準輸出，進度與錯誤印到標準錯誤；記錄檔在 `<資料根>\logs\`。結束碼：0 成功、1 失敗、2 用法錯誤、3 這個資料根已有 Clode Myriagent 或 fake-device 在執行（同 app 的單一實例鎖，每個資料根一個）。

| 指令 | 作用 |
|---|---|
| `pair --server https://127.0.0.1:<埠> [--name 名稱] [--dev-trust] [--owner-user admin --owner-password-env 變數] [--owner-origin https://localhost] [--no-approve] [--no-drain] [--timeout 秒]` | 走 app 的配對流程（`syncPair`）。有擁有者帳密時自己以擁有者身分登入並核准（`/api/v1/auth/login`、`/api/v1/pair/approve`）；`--owner-origin` 是伺服器的 PublicOrigin（非 GET 要帶的 `Origin`；預設是 `--server`，Testing 的 MyriagentHub 是 `https://localhost`）。`--no-approve`：先印一行 `{"userCode","deviceFingerprint","verificationUri","verificationUriComplete","expiresAt"}`（給 hub-e2e 用瀏覽器核准），再等到配對碼到期。完成後等連上、outbox 清空，最後印一行結果 `{"command":"pair","deviceId",…,"state":"Live","noDrain":false,"drained":true,"backfill":…}`；`backfill` 是發布者的回補進度 `{"conversationsLeft","bytesLeft"}`（null＝沒有要回補的、已經做完，或伺服器沒有 conv1）。`--no-drain`（WP4.9 步 2，measure_sync 的 `head-paired-backfilling`）：`device.json` 寫好、連上之後不等 outbox 清空，只等到發布者回報回補的進度（資料根有舊對話時）或同步安定連續 1 秒（沒有要回補的）就結束，`drained`、`outboxRows`、`rejected` 是 null，`backfill` 是那一刻的進度 |
| `run [--script <劇本.json>] [--control-stdin] [--fake-codex <情境>] [--dev-trust] [--timeout 秒]` | 依序執行劇本；已配對時跑完等 outbox 清空（逾時算失敗）。輸出每一步的結果與最後的狀態（帳戶、logicalId、用量與來源、outbox 各狀態列數）。`--control-stdin`（WP4.9 步 11）：劇本（可省略）跑完之後接著從標準輸入讀控制指令，見下面的「控制通道」；`--script` 與 `--control-stdin` 至少要一個 |
| `status [--dev-trust] [--wait 秒] [--wait-backfill 秒]` | 已配對時先等連上（預設 15 秒），印狀態（含本機擁有的對話數 `conversations` 與回補的 `backfill`：`progress` 是剩下的對話數與位元組、`pause` 是磁碟保護上限造成的暫停；沒有發布者——未配對——時是 null）。`--wait-backfill`：再等回補做完——引擎在 Live、沒有在途的批次、outbox 每列都已確認或被拒、重整佇列是空的、沒有進行中的工作、發布者的進度是 null，連續 1 秒（`SyncSettleRules.IsSettled`，和實機探針等同步安定是同一組條件）——輸出加 `backfillDone`、`backfillSeconds`，逾時算失敗。伺服器沒有 conv1 時回補不會開始，連上、清空就算做完 |
| `unpair` | 解除配對（`syncUnpair`：停引擎、刪裝置金鑰、pending 檔、`sync.db`、最後刪 `device.json`）。只刪本機的配對資料，伺服器上的裝置要由擁有者撤銷 |
| `backlog --count 1000 [--dev-trust] [--no-wait] [--timeout 秒]` | 直接在 outbox 排 N 筆不合併的 `cli.state`（內容是假的 `0.0.0-backlog`），全部寫進 outbox 後再要求重送一次帳戶與真的 CLI 狀態（伺服器最後看到的是這台真的狀態）。預設等全部送完並回報秒數；`--no-wait` 寫進 `sync.db` 就結束（下次啟動時補送，S5 暖啟動量測用） |

| `session --server https://127.0.0.1:<埠> --owner-user admin --owner-password-env 變數 --script <劇本.json> [--ephemeral-key] [--fake-codex <情境>] [--dev-trust] [--name 名稱] [--owner-origin …] [--timeout 秒]`（WP4.9 步 8） | 同一個程序裡配對（一律自動核准）、跑劇本、等 outbox 清空、記下狀態，**不論成敗都解除配對**。`--ephemeral-key`（只能用在 session）：裝置金鑰只在記憶體（P-256、`keyStorage` 是 software），不碰 CNG 的使用者金鑰存放區，負載工具一次配對很多台時不在共用的存放區留下或刪除任何檔；程序結束金鑰就沒了，所以只有 session 能用。輸出 `{"command":"session","server","ephemeralKey","pair":{deviceId,…,keyName,keyStorage,seconds},"steps":[…],"drained","outboxRows","rejected","status":{…},"unpair":{deviceId},"seconds"}`；失敗時先印已有的部分（含 `unpair`）再以結束碼 1 結束 |

全域選項 `--app-version <版本>`（WP4.9 步 8，FF9 的換版用）：Hello 宣告的 app 版本（預設 `0.1.0`；1～64 個英數字、點、加號或減號）。

### 假 Codex app-server（WP4.9 步 13，計畫 D11，`run`／`session` 的 `--fake-codex <情境>`）

給全流程 FF7（遠端裝置碼登入、所有電腦登出）用：帳戶管理員找 Codex 的 CLI 時拿到假 app-server，**和 WP4.8 測試的 `FakeCodexAppServer` 同一份腳本**（`Runtime/FakeCodexAppServerScript.cs`，測試與 fake-device 共用；`FakeCodexAppServerScriptTests` 以搬移前記下的 SHA-256 釘住）。其他代理照舊找不到 CLI，CLI 管理員照舊拒絕執行任何指令（`refusedCli` 照常計數）。

- **情境**：`ok`、`evil_host`（網址主機不是 auth.openai.com）、`http`、`missing_fields`、`unsupported`（-32601）、`complete_fail`（一開始就送失敗的完成通知）、`init_fail`（initialize 回錯誤）；其他名稱是用法錯誤（結束碼 2）。驗證碼固定 `QWER-TY12Z`，完成之後 `account/read` 回 `device@example.test`。
- **資料根要在 TEMP 底下**（否則結束碼 2，連資料根都不建）：腳本的 `logout` 只在 TEMP 底下的 `CODEX_HOME` 留標記，隔離帳戶的 `CODEX_HOME` 在資料根底下。腳本、`calls.txt`、`complete.flag` 在 `<資料根>\fake-codex\`；同一個資料根重新啟動時沿用（登入狀態以 `account/read` 的回答為準：旗標在、這個 `CODEX_HOME` 沒有登出標記才是已登入）。
- **啟動器**（`Runtime/FakeLauncherRules.cs`，純函式，`FakeLauncherRulesTests`）：`ResolveLauncher` 只依代理種類分派、分辨不出子指令，所以 Codex 一律拿到同一個假啟動器，由腳本依 argv 分派 app-server 與 `logout`。只在三件事都成立時交出：代理是 Codex、腳本是 TEMP 底下的 `.ps1`（正規化之後比，`..` 走出去、使用者真的 `.codex` 都不算）、執行檔是 `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`；TEMP 是磁碟根目錄或包住使用者資料夾時也不交出。參數是 `-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <腳本>`。啟動器帶不了環境：fake-device 帶 `--fake-codex` 啟動時先從自己的程序環境剝掉 `PSModulePath`（從 PowerShell 7 繼承的會讓 5.1 載錯模組），假 app-server 繼承剝過的環境（測試程序裡的 DeviceRuntime 不剝）。
- **只模擬裝置碼登入**：遠端的 `accountLoginDevice` 對 Codex 一律用裝置碼。本機的一般登入（`accountLogin` 沒帶 `method`）拿不到登入網址時會退回主控台登入、開一個視窗跑腳本——劇本與控制通道都沒有這個 op，不要從別的地方送。
- 帳戶照舊以 `Seed` 放成已登入（Ready）；要讓假 app-server 決定狀態，得有一次狀態查詢（登入完成、登出之後帳戶管理員都會自己查）。
- 控制通道與劇本的 op：`fakeCodexComplete`、`fakeCodexCalls`（見下面）；狀態多 `fakeCodex`。

開發信任（`--dev-trust`）每次啟動都要帶（同 app 的 `MYRIAGENT_SYNC_DEV_TRUST=1`）：本機 MyriagentHub 是自簽憑證。

### 正式站守門（WP4.9 步 8，計畫 D18）

所有指令都經守門：`pair` 與 `session` 的 `--server`，以及資料根已有 `sync\device.json` 時它記的伺服器（`run`、`status`、`backlog`、`unpair` 與 `pair`、`session` 一啟動執行環境就照它連線；`Runtime/PairedServerGuard.cs`，WP4.9 合併前審查 F4：拿真實資料根的複本執行時，同一個使用者的金鑰與權杖都打得開，會以真實裝置的身分連到正式站），都在啟動之前先過 `Runtime/LocalHubTargetRules.cs`（`unpair` 只刪本機檔，只看網址、不連線）：網址只收 `https` 加 `127.0.0.1`、`localhost`、`[::1]`（不帶帳號密碼），主機名稱含 `example.com` 一律拒絕；再以匿名的本機迴路身分讀一次 `/readyz`、記下 TLS 握手看到的憑證，`environment` 是 `service`（Windows 服務）、憑證或 `/readyz` 的 `tls` 名稱含 `hub.example.com`、或簽發者是公開 CA（Let's Encrypt 等）任一成立就拒絕——只看主機名稱擋不住 `ssh -L 本機埠:127.0.0.1:443` 的轉送。讀不到 `/readyz` 或沒有 `environment` 也拒絕。規則和 `tools/hub-e2e/lib/guard-rules.mjs` 相同，測試表 `tools/hub-e2e/test/guard-cases.json` 兩邊共用（`LocalHubTargetRulesTests`）。結果記在標準錯誤的「守門：」一行。

### status 的新欄位（WP4.9 步 8）

`status`、`run` 與 `session` 的狀態另有：

| 欄位 | 內容 |
|---|---|
| `features` | 這條連線協商出的功能（例如 `conv1`、`live1`、`cmd1`；沒有連線時是空的、未配對是 null） |
| `liveRelay` | 即時更新：`sent`（交給連線送出的）、`masked`（做了遮蔽與序列化的）、`tooLarge`（太大不送的）、`dropped`（遮蔽過但沒有送出的：太大、不合格、送出前不再有人看或沒有 live1） |
| `replicas` | 複本（其他電腦的對話）：`known`（`replica_meta` 的列數）、`open`（顯示中的） |
| `refusedCli` | 拒絕執行 CLI 指令的次數（任何次數都表示有程式想跑 CLI） |
| `openedBrowser` | 帳戶管理員想開瀏覽器的次數（WP4.9 步 13；一律不開，只計數） |
| `fakeCodex` | 假 Codex app-server（WP4.9 步 13；沒帶 `--fake-codex` 是 null）：`scenario`、`folder`（`<資料根>\fake-codex`）、`launcher`（powershell 的完整路徑）、`started`（`calls.txt` 存在＝啟動過） |
| `ephemeralKey` | 裝置金鑰只在記憶體 |
| `session` | 這台所有假會話（`ScriptedSession`）收到的呼叫次數（WP4.9 步 15，`SessionCalls`）：`sends`（送出，含佇列裡的訊息接著送）、`interrupts`（停止）、`answers`（回答問卷）、`declines`（略過問卷）、`approvals`（回應核准卡）、`plans`（回應計畫）、`steers`（執行中插入的嘗試：中樞只對支援插入的代理呼叫 `SteerAsync`〔`AgentHubRules.SupportsSteering`：Claude、Codex〕，假會話一律不插、訊息照舊排隊；FF11 以它證明 Grok、Antigravity 的對話直接排隊）、`modelSwitches`（即時換模型與推理強度：中樞的 `SetOption` 呼叫 `SetModelAsync`，假會話一律回已套用）與其中這一輪還在進行時的 `modelSwitchesWhileRunning`（`Sync.WithHub` 的 `RunningModelSwitchScenarioTests` 另以 `SessionCalls.LastModelSwitch` 看收到的值）。卡片沒有在等（重送）也算一次——計數的是到了會話的呼叫；FF3 以增量證明第二個分頁與同一個 commandId 的重送停在中樞的 `requestDigest` 檢查、沒有到會話 |
| `batchAck` | BatchAck 的延遲（PublishBatch 送出到收到同一個 batchId 的確認）：`count`、`p50Ms`、`p95Ms`（nearest-rank）、`maxMs`，`engines` 是建過的引擎數。fake-device 的引擎一律經 `Runtime/TimingTransport.cs` 的 `TransportMeter` 包一層（`ConversationScenarioTests` 量回補時用同一份） |
| `outboxSeq` | outbox 的 deviceSeq（WP4.9 步 18）：`max`（最大的）、`doneMax`（已確認或被拒的最大值）、`firstUndone`（還沒完成的最小值；都完成是 null）；沒有引擎是 null。FF9 以清空之後的 `max` 對伺服器 events＋rejected_events 的最大 deviceSeq，證明 1..N 沒有遺失（`FakeDeviceSessionTests` 守） |

### 時間標記（WP4.9 步 8，計畫 D8）

劇本的 `text` 步驟可以加 `"stamp": true`：每一段開頭插入發出當下的時間標記 `〔`＋13 位數字的 UTC 毫秒＋`〕`（`Runtime/StampRules.cs`；`tools/hub-e2e/lib/stats-rules.mjs` 的 `parseStamps` 讀同一種），完成事件帶加了標記的全文。全形括號不是權杖字元、13 位數字也不是任何權杖的樣式，DLP 不改它、串流的「可以先送的長度」也不扣住結尾的標記。`"completeEachSegment": true`：每一段各成一個完成的項目（片段之後立刻完成），給「持久」那一條路量延遲；預設是一個項目串到底（「即時」那一條）。

### 控制通道（WP4.9 步 11，`run --control-stdin`）

給全流程「跑到一半下指令」（`tools/hub-e2e` 的 `full-flow.mjs`）：標準輸入每行一個 JSON 指令 `{"id":…,"op":"…",參數…}`（UTF-8；`id` 是整數或字串，可省略，回覆原樣帶回），標準輸出每個指令回一行 `{"id","op","ok":true,"result":{…},"ms"}` 或 `{"id","op","ok":false,"error":"…","ms"}`。開始時先寫一行 `{"event":"ready","pid","paired","state","ops":[…]}`。指令一個接一個執行（不並行）；不合格的一行（不是 JSON、未知的 op、不認得的參數鍵、型別或範圍不對）與執行失敗都只回 `ok:false`，照常讀下一行。`quit` 或輸入結束（EOF）就停：還在跑的背景串流先以網頁的 `stop` 停掉（最多等 10 秒），之後照 `run` 的流程（已配對時等 outbox 清空），最後一行是 `{"command":"run",…,"control":{"commands","errors","ended":"quit|eof","streams":[…]}}`。規則在 `Runtime/FakeControlRules.cs`（純函式，`FakeControlRulesTests`），執行在 `Runtime/FakeControl.cs`（`FakeControlTests` 在測試程序裡以 `StringReader` 跑同一個通道）。

| op | 參數 | 作用與結果 |
|---|---|---|
| `status` | — | 同 `status` 指令的狀態，另加 `streams`（每個背景串流的 `conversation`、`conversationId`、`done`、`outcome`、`items`、`seconds` 或 `error`）與 `remoteTurns`（`queued`、`taken`） |
| `stream` | `conversation`（別名或 id，必填）、`account`（開新對話用的帳戶別名）、`message`、`text`、`segments`（1～1000，預設 10）、`delayMs`（0～60000，預設 250）、`stamp`、`completeEachSegment`、`wait`、`seconds`（預設 30＋兩倍的串流時間） | 一輪只有一段文字串流的 chat。沒給 `text` 時每段是一樣長的「第 0001 段。」。預設在會話收下這一輪就回 `{conversationId, background:true, …}`，這一輪在背景跑完（同一個對話的上一個串流沒結束時失敗）；`wait:true` 等它結束，回 chat 的結果（`created`、`waiting`、`outcome`、`items`、`seconds`）。`stamp`、`completeEachSegment` 見「時間標記」 |
| `nextRemoteTurn` | `turn`（同 chat 的 `script`：步驟陣列或 `{"steps":[…],"outcome":…}`） | 排一份劇本給「下一則不是 chat 送的訊息」（網頁或手機的 `send`、排隊的訊息接著送）；排了幾份就用幾次，用完回到固定回覆。劇本裡的卡片由網頁回應（chat 才替使用者回應），沒有回應就停在那裡。回 `queued`、`steps` |
| `setTitle` | `conversation`、`title` | 網頁的 `rename`（中樞去頭尾空白、最多 80 字）；回套用後的 `title` 與 `truncated` |
| `setPolicy` | `level`（Off、View、Control）、`fullAccess`、`notify` | 同劇本的 `policy`（`syncSetPolicy`，回讀設定，沒套用就失敗） |
| `ords` | `conversation` | 本機項目的 `id`、`ord`（沒配 Ord 是 null）、`kind`、`status`，與 `count`、`withoutOrd`（FF1 和瀏覽器 `open`／`loadOlder` 拿到的比對） |
| `remoteAudit` | `beforeAt`、`beforeSeq`（要和 `beforeAt` 一起給）、`limit`（1～500，預設 100） | 本機的遠端操作紀錄（引擎的 `LoadRemoteAuditAsync`：新到舊；翻頁帶上一頁最後一列的 `at` 與 `seq`）；沒有配對時失敗 |
| `xssFill` | `payloadFile`（必填）、`fields`（必填）、`from`、`count`、`conversation`（卡片那個對話，預設別名 `xss-cards`）、`account`、`folder` | 把語料的 `[from, from+count)` 放進欄位，見下面 |
| `restoreFromTrash` | `conversation`（32 個十六進位字元的 id） | 先確認回收區（`<資料根>\conversations-trash`）在資料根底下（設計:3052），再經中樞的 `RestoreFromTrash`（同網頁的還原鈕）；回 `restored`、`wasInTrash`、`trashRoot`、`serverDeleted`（對話資料夾有沒有 `sync-server-deleted.json`：還原的對話只在這台）、`inList`。搬不回來（清單已有、資料夾已存在…）不算失敗，`restored:false` |
| `fakeCodexComplete` | — | （要 `--fake-codex`，WP4.9 步 13）「使用者在別的裝置核准了」：寫 `complete.flag`（同測試的 `Complete()`），進行中的裝置碼登入接著完成，之後 `account/read` 回已登入。回 `scenario`、`flag`、`alreadyComplete` |
| `fakeCodexCalls` | — | （要 `--fake-codex`）假 app-server 的 `calls.txt`：`calls`（每一行：`args=…`、`codexHome=…`、`pid=…`、`initialize`、`account/login/start type=…`、`account/read`、`logout`…）、`count`、`started`，與 `logouts`（`logout` 子指令的次數）、`loginStarts`、`codexHomes`（每次啟動時的 `CODEX_HOME`） |
| `localCommand` | `cmd`（必填：`open`、`close`、`rename`、`send`、`syncSetConversationPolicy`、`syncPause`）、`args`（物件，中樞指令的參數；不能有 `cmd`） | （WP4.9 步 16）這台的使用者在桌面上的一個操作：經中樞的 `HandleCommand` 交給假的主視窗（同 app 的主視窗送的指令；`sync*` 由同步協調者接手），等 UI 執行緒把接著排的工作做完。回 `cmd`、`toasts`（這段時間的錯誤 toast，例如對複本的寫入「這是另一台電腦的對話（唯讀）」、已從伺服器刪除的對話改回 full）；`args` 有 `conversationId` 時另附 `conversation`：`exists`、`replica`（其他電腦的複本）、`title`、`syncPolicy`（本機的是有效政策，有伺服器刪除的旁檔一律 localOnly；複本的是擁有者 meta 的）、`items`、`running`、`serverDeleted`（`sync-server-deleted.json`）；`syncPause` 另附 `paused` 與同步的 `state`（暫停＝斷線，伺服器看到這台離線）。清單只收全流程用得到的，刪除、解除配對、帳戶與 CLI 一律不收（`FakeControlRules.LocalCommands`） |
| `replicas` | `conversation`（選填，32 個十六進位字元的線上 id） | （WP4.9 步 16）這台的複本（sync.db 的 `replica_meta`、`replica_items`，在引擎執行緒讀）：`showReplicas`、`count`，每列 `conversationId`、`ownerDeviceId`、`epoch`、`seq`、`deleted`、`policy`、`title`、`items`（存著的項目數；只有開著的複本會拉項目）、`open`（引擎在拉它的串流）、`hubItems`（中樞裡顯示中的複本的項目數，中樞沒有它是 null）；沒有配對時失敗 |
| `unpair` | — | （WP4.9 步 17）在 `run` 的途中解除配對（同 `unpair` 子指令：網頁的 `syncUnpair`，協調者先把遠端操作紀錄匯出成 `<資料根>\remote-audit\{deviceId}.jsonl`、再停引擎、刪金鑰、pending 檔、`sync.db`，最後刪 `device.json`）：回 `deviceId` 與 `remoteAudit`（`path`、`exists`、`bytes`、`lines`；匯出失敗只記錄、解除配對照常，所以檔案不在不算失敗）。沒有配對時失敗；之後 `run` 結束時不再等 outbox 清空（結果的 `unpaired: true`）。FF8 看匯出的紀錄、FF10 收掉被撤銷的裝置（被撤銷的裝置連不上，等 outbox 清空只會逾時） |
| `quit` | — | 回 `{"commands"}` 之後結束通道（後面的行不讀） |

`xssFill` 的 `payloadFile` 只收 `.json`、完整的本機路徑（不收 UNC、裝置路徑、替代資料流），而且要在 repo 的 `tests/MyriagentHub.Tests/Rendering/Golden`、`tests/Myriagent.Tests/Rendering/Golden`（從執行檔往上找 `Myriagent.slnx`）或 TEMP 底下；格式是 `tests/MyriagentHub.Tests/Rendering/Golden/e2e-fixtures.json`（`{"mediaCsp","xss":[{"name","html"}…]}`，`E2eFixtureExportTests` 從 `ItemSanitizerXssCorpus` 匯出、逐位元組固定，取淨化之前的 `html`）或字串陣列。`fields`：

- `title`：每筆一個新對話（別名 `xss-title-{序號}`，固定回覆；之後再放同一個序號沿用），以 `rename` 改成這筆；中樞的標題最多 80 字，截掉的筆數在 `titleTruncated`、對話 id 在 `titleConversations`。要給 `account`。
- `accountLabel`（要給 `account`）、`deviceName`（要已配對，否則記未覆蓋）：只有一個欄位，放第一筆。
- `text`、`tool`、`question`、`approval`：同一輪（`cardsConversation`），每筆依序一段代理文字、一個工具（名稱、標題、細節、輸出）、一張問卷（題目與唯一的選項，答這個選項）、一張核准卡（工具名稱、標題、細節，拒絕），這一輪跑完。
- `fileNames`：在 `folder`（TEMP 底下、資料根以外：資料根在遠端讀檔的禁止清單裡）建每筆一個檔，檔名只留 Windows 合法的字元（`000-` 這種序號前綴、尖括號與引號等換成底線、最多 60 個字，`FakeControlRules.SafeFileName`；放不進的字元由 conv-probe 覆蓋），再把卡片那個對話的工作資料夾換成它（`listFiles` 只列對話工作資料夾裡的檔）；回 `folder`、`folderConversation`、`files`。
- `notifyTitle`、`toast`、`fileRefError`：假裝置放不進（由中樞或伺服器產生），結果記在 `uncovered`。

結果：`covered`（欄位 → 放了幾筆）、`uncovered`（`[{field, reason}]`）、`total`、`from`、`count`。

劇本也認得這些 op（`quit` 除外），參數與結果相同，例如 `{"op":"stream","conversation":"工作","segments":20,"delayMs":250,"stamp":true}`：劇本裡開始的背景串流在 `run --control-stdin` 時接著留在控制通道裡；沒有控制通道（`run`、`session`）時劇本跑完先等它們自己跑完（`run` 的結果在輸出的 `streams`，`session` 的印在標準錯誤）。

## 劇本格式

```json
{
  "steps": [
    { "op": "addAccount", "alias": "claude-a", "provider": "Claude", "label": "假 Claude A", "email": "a@example.test", "orgId": "org-a", "plan": "max" },
    { "op": "addAccount", "alias": "codex-b", "provider": "Codex", "email": "b@example.test", "plan": "pro" },
    { "op": "cli", "provider": "Claude", "version": "2.1.0" },
    { "op": "poll", "account": "claude-a", "windows": { "session": 12, "weekly": 34, "weekly:Opus": 7 } },
    { "op": "sessionUsage", "account": "claude-a", "windows": { "session": 16 } },
    { "op": "rename", "account": "claude-a", "label": "新名稱", "color": "#3b82f6" },
    { "op": "reorder", "accounts": [ "codex-b", "claude-a" ] },
    { "op": "pref", "key": "fontSize", "value": 15 },
    { "op": "deviceRename", "name": "新的裝置名稱" },
    { "op": "policy", "level": "Control", "fullAccess": false, "notify": true },
    { "op": "removeAccount", "account": "codex-b" },
    { "op": "wait", "ms": 1000 },
    { "op": "waitDrained", "seconds": 60 }
  ]
}
```

對話（WP4.3 步 13）：

```json
{
  "steps": [
    { "op": "seedConversations", "count": 300, "olderThanDays": 3, "itemsPerConversation": 40, "seed": 1, "baseTimeUtc": "2026-10-20T00:00:00Z", "account": "seed", "provider": "Claude" },
    { "op": "addAccount", "alias": "seed", "provider": "Claude", "email": "seed@example.test", "orgId": "org-seed", "plan": "max" },
    { "op": "chat", "conversation": "工作", "account": "seed", "message": "幫我整理專案", "attachments": [ "C:/temp/notes.txt" ], "seconds": 60,
      "script": { "steps": [
        { "text": "好的，我先看看。", "segments": 4, "delayMs": 20 },
        { "tool": "執行指令", "kind": "bash", "title": "dotnet build", "output": "建置成功", "segments": 2 },
        { "question": [ { "id": "q1", "text": "要用哪個資料夾？", "options": [ "src", "tests" ] } ], "answer": { "q1": [ "src" ] } },
        { "approval": "寫入檔案", "kind": "write", "title": "README.md", "decision": "allow" },
        { "media": "C:/temp/chart.png" }
      ], "outcome": "completed" } },
    { "op": "chat", "conversation": "工作", "message": "沒有劇本：一段固定回覆" }
  ]
}
```

- `seedConversations`（只能放在劇本**最前面**，可以連續好幾個）：在 fake-device 的中樞啟動之前把 N 個 WP4.3 之前格式的對話（項目沒有 `Ord`、meta 沒有 `NextOrd`、沒有同步政策＝full）寫進 `<資料根>\conversations`。`count`、`seed`、`baseTimeUtc`（一定要帶時區：`Z` 或 `±hh:mm`）必填；`olderThanDays`（預設 0）：最新的那個比基準時間早幾天，之後每個再早一小時；`itemsPerConversation`（預設 20）：使用者與代理交替、每五則一則工具（執行指令）；`account`（預設 `seed`）：對話的帳戶別名，要 `chat` 這些對話就以同一個別名 `addAccount`；`provider` 只能是 `Claude`、`Codex`、`Grok`（有隔離帳戶的）。id 與內容只由 `seed` 決定、時間只由 `baseTimeUtc` 決定（不用 `DateTime.Now`、`Guid.NewGuid`）；內容是一般的中英文字，沒有像憑證的字串。同一個 `seed` 不論 `count`、`itemsPerConversation`，第一個對話的 id 都相同（id 在項目之前產生）：連同一台伺服器的不同裝置要用不同的 `seed`，否則後上傳的裝置在伺服器上撞到前一台的擁有者（`not_owner`，那個對話封鎖、伺服器上沒有它的內容）。資料根已經有同 id 的對話就不寫（結束碼 1）。結果的 `sha256` 是**指紋**：每個對話一行 `{"meta":…,"items":[…]}`（時間以 UTC、不縮排、略過 null）的 UTF-8 SHA-256，與時區無關，同樣的參數在任何電腦都相同（例：`count 40、olderThanDays 120、itemsPerConversation 25、seed 20261020、baseTimeUtc 2026-10-20T00:00:00Z` 是 1000 則、`0ebf798c8cac3f8a22659c5cec552cdf60ae2f57fe2f21621c67e58db056aa07`，`FakeDeviceConversationTests` 印出）。
- `chat`：`conversation` 是 32 個十六進位字元時是既有對話的 id（先 `open`）；其他的是別名，第一次用時以 `account` 開新對話（`newConversation`），之後指到同一個（存檔模式記在 `<資料根>\fake-device-conversations.json`，送出成功才記）。`attachments` 是本機磁碟上已存在的檔案，經 `addPathAttachment` 附上、等處理完才送出。送出（`send`）被中樞擋下時以 toast 的內容失敗；這個對話還在執行上一輪時也失敗。結果：`conversationId`、`created`、`waiting`、`outcome`（Completed、Failed、Interrupted）、`items`（項目數）、`seconds`。
- `script`（這一輪的劇本；沒有就是一段固定回覆）是步驟陣列，或 `{"steps":[…],"outcome":"completed|failed","usage":{"input":…,"output":…,"cached":…}}`（`usage`：這一輪結束事件的用量，標頭的 `lastTurn` 與側欄的 token 計數；可省略）；每一步以它帶的鍵決定種類，`delayMs`（0～60000）是每一段之前等的毫秒數：
  - `text`：代理的回覆，切成 `segments` 段（1～1000，不切開代理字對）串流，最後一則完成事件帶完整內容；`stamp`、`completeEachSegment`（布林）見上面的「時間標記」。
  - `tool`：工具名稱；`kind`（預設 other）、`title`、`detail`、`filePath`、`output`（切成 `segments` 段）、`failed`。從執行中到完成（或失敗）。
  - `question`：題目陣列 `[{id?, header?, text, options[], multiSelect?, allowOther?, secret?}]`；`answer`（`{題目 id:[答案…]}`）或 `skip:true` 是 chat 替使用者的回應（經網頁的 `answer`、`skipQuestion` 指令）。
  - `approval`：工具名稱；`kind`、`title`、`detail`；`decision`（allow、allowSession、deny）經 `approve` 指令回應（拒絕也接著做）。
  - `media`：代理產出的媒體（本機磁碟上已存在的檔案，中樞只顯示存在的本機路徑）。
  - `thinking`：思考，切成 `segments` 段串流（Grok 的 `agent_thought_chunk`、Claude 的 thinking）。
  - `todo`：待辦清單 `[{text, status}]`（`pending`、`inProgress`、`completed`；Grok 的 ACP plan、Claude 的 TodoWrite），`explanation` 可省略。
  - `planApproval`：計畫確認卡（內容是 Markdown）；名稱與標題預設是 Grok 的 exit_plan_mode 卡（「計畫」「照這個計畫執行？」，`toolName`、`title` 可改）；`decision`（acceptEdits、manual、keepPlanning）經 `approvePlan` 指令回應，沒有就留著等。
  - 卡片沒有指定回應時這一輪停在那裡：chat 回 `waiting:true`（這一輪沒結束，同一個對話不能再送）；網頁的 `stop` 讓它以 Interrupted 結束。
- 劇本化的會話不啟動任何程序：事件依序經宿主的 UI 執行緒交給中樞（和真的會話一樣），所以中樞、發布者與引擎看到的是真的串流、工具與卡片。

- 帳戶一律以別名指定：32 個十六進位字元直接當帳戶 id（app 建的帳戶 id 就是這種格式），其他的取 `SHA-256("fake-device:"+別名)` 的前 32 字，同一份劇本重跑時指到同一個帳戶。`addAccount` 遇到已有的帳戶只更新 email、orgId、方案。`provider` 是 `Claude`、`Codex`、`Grok`、`Antigravity`（後兩種見下面的「Grok 與 Antigravity」）。Claude 帳戶要有 orgId 才會連結到邏輯帳戶（沒有 orgId 的 Claude 帳戶不連結）。
- `cli`：`version` 為 null 表示沒裝；狀態存在 `<資料根>\fake-device-cli.json`（真的 app 不讀它）。
- `poll`：模擬「到了本機的輪詢時間」，用量查詢閘門不放行時不查（結果 `polled:false`）。時間窗的鍵：Claude `session`、`weekly`、`weekly:模型名`；Codex `session`（primary，300 分鐘）、`weekly`（secondary，10080 分鐘）。值是百分比。
- `sessionUsage`：對話中回報的額度（Claude 的 `rate_limit_event`、Codex 的 `rateLimits/updated`），來源 `session`；Claude 只有 `session`、`weekly`。Grok、Antigravity 的 `poll` 與 `sessionUsage` 不模擬（失敗）。
- **Grok 與 Antigravity**（設計 `docs/plans/2026-10-22-grok-antigravity-sync.md` 的 P5）：Grok 是隔離帳戶（`GROK_HOME`＝`<資料根>\accounts\{id}\grok`），信箱就是身分（身分鍵 `grok:{信箱}`）。Antigravity 沒有隔離帳戶：`addAccount` 建的是和 app 一樣的本機預設帳戶形狀（`ConfigDirectory` 是 null），id 照別名算（不是 app 固定的 `local-antigravity`，載入時以此分辨，`DeviceRuntime.IsFakeAntigravity`），信箱就是 app 從 agy 記錄讀到的身分（`antigravity:{信箱}`）。假裝置對兩種都不執行任何 CLI（`ResolveLauncher` 一律 null、CLI 管理員拒絕執行），會話照樣是 `ScriptedSession`；中樞依代理種類的限制照常作用（Grok、Antigravity 不支援執行中插入，見 status 的 `session.steers`）。`cli` 的四種都存回 `fake-device-cli.json`，送不送伺服器由引擎依 `prov1` 決定。**劇本加了 Antigravity 帳戶的資料根不要給真的 exe 用**：真的 exe 會對它跑這台電腦的 `agy` 查登入狀態（`measure_sync.py`、`real-exe.mjs` 都不加）。
- `pref` 的漫遊鍵只有 `theme`、`fontSize`（整數）、`enterToSend`、`notifications`（其他設定照樣套用，但不同步）。
- `rename`、`reorder`、`pref`、`deviceRename`、`removeAccount` 都是網頁送給中樞的指令（`accountUpdate`、`accountReorder`、`setting`、`deviceRename`、`accountRemove`＋刪資料），之後經鏡像與橋接進引擎，和 app 相同。
- `policy`（WP4.6 步 13）：這台的遠端操作政策，`level`（`Off`、`View`、`Control`）必填，`fullAccess`（允許遠端用完全存取）、`notify`（遠端操作時在這台通知）可省略（沿用目前的）；經網頁的 `syncSetPolicy` 存進設定、馬上更新引擎的政策（連著的話引擎重新連線，以新的 Hello 帶上）。參數不合格（中樞整個忽略）時失敗；結果回讀套用後的 `level`、`fullAccess`、`notify`。設定存在資料根，重啟後沿用。
- 中樞的帳戶卡有短節流：`poll` 之後立刻 `sessionUsage`，鏡像只看到合併後的最後一份（上傳的是 session 那份）；要兩份都上傳就在中間加 `wait` 1000 毫秒以上。

## 沒有 conv1 的伺服器（`MYRIAGENT_SYNC_OLD_HUB`）

`ConversationScenarioTests` 的 `舊伺服器_沒有conv1_對話不外洩_目錄同步照常` 以舊版（WP4.1 結束時、WP4.2 之前）的 MyriagentHub 跑：上一次進 Live 時有 conv1、伺服器之後回滾，離線時寫的對話事件在送出前換成 `{}`、被 invalid 擋下，`rejected_events` 只有 `{}`、整個資料庫（含 -wal）沒有對話的文字，目錄同步照常。`MYRIAGENT_SYNC_OLD_HUB` 的值是以 `git archive <舊提交>` 建出的 `MyriagentHub.exe` 完整路徑（WP4.2 與 WP4.3 共用的約定），沒設就略過：

```bash
git archive --format=zip -o C:/<暫存>/hub-old.zip <舊提交>
env -u PSModulePath powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive C:\<暫存>\hub-old.zip C:\<暫存>\hub-old"
# 或在 Git Bash：mkdir -p C:/<暫存>/hub-old && git archive <舊提交> | tar -x --force-local -C C:/<暫存>/hub-old
dotnet build C:/<暫存>/hub-old/src/MyriagentHub -c Release -nodeReuse:false -p:UseSharedCompilation=false
export MYRIAGENT_SYNC_OLD_HUB=C:/<暫存>/hub-old/src/MyriagentHub/bin/Release/net10.0/win-x64/MyriagentHub.exe
MYRIAGENT_SYNC_OLD_HUB=$MYRIAGENT_SYNC_OLD_HUB dotnet test tests/Myriagent.Tests/Myriagent.Tests.csproj -c Release --filter "FullyQualifiedName~ConversationScenarioTests"
```

路徑一律寫 `C:/…`（Windows 原生程式打不開 `/c/…`）；暫存資料夾不要放在 repo 裡。測試把那個資料夾整個複製到 `%TEMP%\Myriagent.Tests\myriagenthub-old-<pid>` 再執行（金鑰檔以這個路徑認，結束時檢查沒有留下）。同一棵樹的 MyriagentHub 在重新為基底之前也沒有 conv1（`基底伺服器_沒有conv1_…`，預設就跑；WP4.2 進來之後由計畫步 16 刪掉）；conv1 的回補情境（`MYRIAGENT_SYNC_IT=1`）要等那之後才跑得過，之前會失敗並提示「需要 WP4.2 進同一棵樹」。

## 和真的 exe 共用資料根

`pair` 產生的 `<資料根>\sync\device.json`、裝置金鑰（CNG，使用者的金鑰存放區）與 `sync.db` 給 `Myriagent.exe --data <資料根>` 用時，**必須是同一個 Windows 使用者**：裝置權杖以 DPAPI（目前使用者）保護、金鑰在目前使用者的金鑰存放區。另外：

- 同一個資料根不能同時跑 app 與 fake-device（單一實例鎖，結束碼 3）。
- fake-device 寫的 `settings.json` 關掉 CLI 自動更新、把所有本機預設帳戶（Antigravity、Grok：`AppSettings` 裡名稱以 `AccountAdded` 結尾的旗標）都標成已加過，真的 app 不會因此建立指向這台電腦真正登入的系統預設帳戶、去跑 `agy`、`grok`（`FakeDeviceSettingsTests` 以反射把關，新增代理種類時會紅燈）。
- 真的 app 的 `AccountManager.Initialize` 在 `accounts.json` **沒有任何帳戶**時會建立指向使用者真正 `~/.claude`、`~/.codex` 的系統預設帳戶：給真的 exe 用之前，劇本至少加一個帳戶。真的 exe 對這些隔離帳戶仍會找這台電腦的 CLI、對假權杖查官方用量端點（必然 401），所以量測只在探針的「不找 CLI」模式下做（第 16 步）。

## 注意

- 不要和 `tools/acme-e2e`、`tools/hub-e2e` 同時跑：配對建立、解除配對刪除的裝置金鑰也在 `%APPDATA%\Microsoft\Crypto\Keys`，那兩個工具結束時的「沒有孤兒金鑰檔」檢查會誤判（設計 D5）。fake-device 結束前一定要 `unpair` 才不留下金鑰檔。
- 伺服器那邊用 `admin stop` 正常停止（強制結束會在同一個金鑰存放區留下 MyriagentHub 的孤兒金鑰檔）。

## 實機量測（`measure_sync.py`，WP3.3 第 16 步）

`measure_sync.py` 用真正的 `Myriagent.exe`（WebView2）量同步的 S5（未配對、已配對連得到、連不到、吞封包、回補中的暖啟動）與 S6（未配對 0 增加、已配對閒置的 CPU 與記憶體）。先建置 slnx 與這個工具的 Release，再：

```bash
python -X utf8 tools/fake-device/measure_sync.py --work <暫存資料夾> [--runs 5] [--idle-runs 3] [--seed 20261016] [--base-app <基準建置的 bin 或 exe>] [--base-label <名稱>] \
  [--conversations 300 [--items-per-conversation 40] [--conversation-days 3]] [--app-env DOTNET_GCgen0size=0x400000 ...] [--idle-extra-seconds 1800]
```

第 4 階段（WP4.9 步 2）的兩種量法，S5 各跑一次、兩次的 `s5Pass` 都要有值：

```bash
# 配對前的基準（<配對前的提交>＋探針修補，見下面的「基準建置」）：不帶舊對話
python -X utf8 tools/fake-device/measure_sync.py --work <暫存>/v9b --runs 5 --idle-runs 3 --seed 20261016 --base-app <<配對前的提交>＋修補的桌面 bin> --base-label <配對前的提交>+probe
# 第 4 階段之前的基準（git archive <舊提交> 建的桌面）：300 個舊對話
python -X utf8 tools/fake-device/measure_sync.py --work <暫存>/v9 --runs 5 --idle-runs 3 --seed 1 --conversations 300 --base-app <<舊提交> 的桌面 bin> --base-label <舊提交>
```

- **工作資料夾**：不存在或是空的才直接用；非空時只有含這個工具寫的標記檔 `.measure_sync` 才整個清掉重來，否則結束碼 2。repo、repo 的上層、家目錄、磁碟根目錄一律拒絕。
- 腳本把 app 與 MyriagentHub 的 bin 複製到工作資料夾再執行，在自動挑的高埠啟動 Testing 的 MyriagentHub（暫存資料根），以這個工具準備資料根（各一個隔離的假 Claude 帳戶，`orgId` 是 `org-measure`）：一個沒配對、一個配對到這個 MyriagentHub（擁有者自動核准），有 `--base-app` 時再一個給基準建置用的沒配對資料根。
- 每次跑 app 都是實機探針，一律加 `MYRIAGENT_AGENT_PROBE_SEND=0`（不送訊息）與 `MYRIAGENT_AGENT_PROBE_NO_CLI=1`（不找、不執行任何 claude／codex／agy／grok，帳戶一律未安裝、不查用量），`CLAUDE_CONFIG_DIR`、`CODEX_HOME` 指到工作資料夾裡空的資料夾，並剝掉本 session 的 `CLAUDE*`、`CODEX_*`、`GROK_*`、`XAI_*`、`MYRIAGENT_*` 等環境變數。執行前先靜態檢查受測的 `Myriagent.dll` 含 `MYRIAGENT_AGENT_PROBE_NO_CLI` 字串（UTF-16LE）：不認得這個開關的建置會找到並執行這台電腦真正的 CLI，不量（HEAD 那份同時是這個檢查的陽性對照）。
- **S5**：每一輪以固定種子（`--seed`，每一輪的順序記進 summary 的 `s5Order`）打亂各組的順序，各跑 `--runs` 次，比 report.json 的 `uiReadyMs`（各組回報中位數與 p90）。HEAD 的組（WP4.9 步 2：設計 11.4 的三種狀態都量，另加回補中與吞封包）：
  - `head-unpaired`：沒配對的資料根。
  - `head-paired-online`：已配對、`MYRIAGENT_SYNC_DEV_TRUST=1` 連本機的 MyriagentHub。
  - `head-paired-offline`：已配對、`MYRIAGENT_SYNC_SERVER=https://127.0.0.1:9`（連線被拒）。
  - `head-paired-blackhole`：已配對、`MYRIAGENT_SYNC_SERVER` 指到腳本自己開的 TCP 監聽（只聽 127.0.0.1、自動挑的埠；接受連線、從不讀也不寫，TLS 交握永遠等不到回應；結束時關掉，summary 記 `blackholePort` 與接受過的連線數 `blackholeAccepted`）。
  - `head-paired-backfilling`（只在 `--conversations` 時）：每一輪一個新的資料根，先以 `run` 灌 N 個舊對話、再 `pair --no-drain`（配對、連上、回補開始就結束，不等 outbox 清空）、立刻跑 app，app 啟動時回補還沒做完。同一台伺服器上的不同裝置要用不同的種子（否則後上傳的撞 `not_owner`），所以這一組每一輪的種子是 `--seed`＋1000＋第幾輪（0 起）、帳戶別名 `measure-seed-b<輪>`：對話數、則數與時間分布相同，內容不同，指紋另記在每一回合的 `seeded`。
  - 有 `--base-app` 時再加 `base-unpaired`（基準建置、沒配對；`--base-label` 寫進 summary 的 `baseLabel`）。

  判定：有基準建置時**每一個** head 組的中位數都不超過 `base-unpaired` 的中位數＋50 ms（剛好等於算過；各組減基準的差列在 `s5HeadMinusReferenceMs`，p90 另外列在 `s5P90WithinBase`，不判定）；沒有時和根目錄 README 記的基準上限 930 ms（2026-10-10 重測 0.87～0.93 秒）＋50 ms 比。**已配對對未配對的差值在構造上必然接近 0**（同步在介面就緒之後 2.5 秒才延後啟動，各組在 `uiReady` 之前跑的程式相同），只證明延後啟動有效，`s5PairedMinusUnpairedMs`（離線減未配對）不拿來判定。S6 那幾回合的介面就緒另列在 `s5Stats` 的 `s6-paired-online`（只供參考）。各組的閒置記憶體另列 `s5IdleMemoryMb`（`idle`＝第一次取樣、`late`＝第二次、`max`＝每一回合兩次的較大值，都是中位數），減 `head-unpaired` 的差值列 `s5IdleMinusUnpairedMb`（`idle`、`max`）：連不上的兩組不在 S6 的判定裡，WP4.9 步 6b 以它看「已配對但連不上的桌面閒置時比未配對多多少」（只供參考，不判定）。
- **基準建置**（`--base-app`）有兩種（不新增 worktree，一律 `git archive` 匯出到暫存資料夾再建置）：
  - **配對前**（設計 14.9 留下的「以配對前的基準建置比」）：main 配對功能之前的提交 沒有 `MYRIAGENT_AGENT_PROBE_NO_CLI`，不能直接用。`git archive --format=zip -o <暫存>/base-old.zip <配對前的提交>` 解開後，只套用 加入探針開關的提交 裡探針「不找 CLI」的那幾段：`git show --format= --output=<暫存>/probe.patch <加入探針開關的提交> -- src/Myriagent/UI/ProbeRules.cs src/Myriagent/UI/AppController.cs src/Myriagent/UI/ProbeRunner.cs`，在匯出的資料夾 `git apply --exclude=src/Myriagent/UI/ProbeRunner.cs <patch>`（`ProbeRules.cs` 新增、`AppController` 以選項建構 `CliManager` 的那一段位移 7 行套得上；`ProbeRunner.cs` 在 配對功能之前的提交 和 加入探針開關的提交 之間改過、套不上），`ProbeRunner.cs` 照修補手改兩處：CLI 偵測（`CheckAllAsync` 與 `report["cli"]`）包進 `if (!ProbeRules.NoCliRequested())`、送訊息的條件加 `!noCli`；`ProbeRules.NoCliOptions()` 另加 `LocateWinget = NoWinget`（和 HEAD 相同，配對功能之前的提交 的 `CliManagerOptions` 已有這個欄位）。`dotnet build <暫存>/base-old/src/Myriagent/Myriagent.csproj -c Release -nodeReuse:false -p:UseSharedCompilation=false` 之後把 `src/Myriagent/bin/Release/net10.0-windows/win-x64` 傳給 `--base-app`、`--base-label <配對前的提交>+probe`，不帶 `--conversations`（和第 3 階段配對前的量法同一個情境）。這一版沒有同步，report 沒有 `sync` 段，判定只看它的 `ok` 與 `uiReadyMs`。
  - **第 4 階段之前**：`git archive <舊提交>` 建的桌面（已含 加入探針開關的提交 的探針開關，直接建置），`--base-label <舊提交>`、帶 `--conversations 300`。
- **S6**：未配對（`head-unpaired`）每一次的 `sync.loadedForbidden` 都要是空的；已配對帶 `MYRIAGENT_SYNC_DEV_TRUST=1` 連本機的 MyriagentHub 跑 `--idle-runs` 次。探針的量法（WP4.9 步 1）：等到 Live 先記一次 `memoryAtLiveMb`（進 Live 當下，含啟動重整的暫態；舊量法就在這裡取樣），已配對而且連上時再等**同步安定**（`SyncSettleRules.IsSettled`：在 Live、沒有在途批次、不在重整、重整佇列空而且沒有進行中的對話、outbox 全部確認或被拒、發布者的回補進度是 null，連續 1 秒；和 `status --wait-backfill` 同一組條件，最多 10 分鐘，花的秒數記 `settleSeconds`、逾時記 `settleTimedOut`），之後**不論是否配對**都再等 25 秒（`ProbeRules.IdleSettleDelay`）才取閒置記憶體（`sync.appPrivateMb`＝`memoryIdleMb.appPrivateMb`，`memoryIdleMb.gc` 是同一次 `GC.Collect()` 之後的 `heapMb`、`committedMb`、`fragmentedMb` 與 `nativeEstimateMb`＝私有記憶體減 GC 已提交），最後連取三個 10 秒窗的 app 程序 CPU（`idleCpuWindows`，`idleCpuPercent` 是最大值；30 秒涵蓋至少兩次 15 秒的心跳與 SignalR keepalive，只取一窗的話第一次心跳在 Live＋15 秒、落不進取樣窗），記下取樣期間送出的心跳數（`sync.heartbeatsDuringSample`）。CPU 窗之後（再等 `--idle-extra-seconds` 秒，預設 0；探針的 `MYRIAGENT_AGENT_PROBE_IDLE_EXTRA_SECONDS`）**再取一次閒置記憶體**（`memoryIdleLateMb`，WP4.9 合併前審查 F1）：第一次落在閒置釋放 DLP 樣式的那一次積極回收之後約 5 秒，是已配對那一邊的低點，之後心跳與 keepalive 的配置會讓 gen0 的已提交空間長回來；兩次之間的配置量與速率、gen0 預算記在 `idleLate`（`allocatedMb`、`allocMbPerHour`、`gen0MaxBudgetMb`、`gen0BudgetFillMinutes`）。**判定**：每一回合取兩次的較大值，已配對的中位數比未配對多**不超過** 30 MB（`s6JudgedDeltaMb`；剛好 30.0 算過，設計 1.3 的 +≤30 MB）；兩次各自的差值另列 `s6PrivateDeltaMb`、`s6LateDeltaMb`，進 Live 當下的差值 `s6AtLiveDeltaMb`（和舊量法對照，不判定），另列 `s6Gc`、`s6GcLate` 的中位數、`s6IdleAllocMbPerHour`、`s6Gen0MaxBudgetMb` 與每回合的 `s6SettleSeconds`、`s6IdleLateSeconds`。`--idle-extra-seconds` 只給 `head-unpaired` 與 S6 已配對的回合（S6 記憶體判定用到的兩群；長閒置的穩態，例如 1800），時限跟著加。**副本**：S6 的回合排在全部 S5 之後，伺服器上已有 `head-paired-backfilling` 每一輪那台裝置的對話，root-paired 連上時（WP4.6 起）拉成副本，所以 S6 量的是「自己 N 個對話＋N×輪數個副本」；summary 記 `s6ReplicaConversations`（＝`--conversations`×輪數），S5 已配對的每一回合記 `serverOtherConversations`（那一回合開始時伺服器上別台裝置的對話數）。每回合因此比以前多約 25 秒（已配對再加安定的時間），一回合的時限是 865 秒。這幾回合的 `uiReadyMs` 另列在 `s5Stats` 的 `s6-paired-online`（只供參考）。
- **`--app-env KEY=VALUE`**（可重複，WP4.9 步 1）：給受測的 app（HEAD 與基準建置）加環境變數，GC 的實驗用（例如 `DOTNET_GCgen0size=0x400000`、`DOTNET_GCConserveMemory=5`）；鍵不分大小寫要以 `DOTNET_GC` 開頭，否則在碰工作資料夾之前就結束碼 2。所有子程序本來就不繼承這個 session 的 `DOTNET_GC*` 與整個 `COMPlus_` 前綴（舊的前綴，.NET 10 照樣採用：`COMPlus_GCgen0MaxBudget`、`COMPlus_gcServer` 都生效），只有 `--app-env` 給的才有；summary 的 `appEnv` 記下給了什麼。剝掉的清單和 `tools/hub-e2e/lib/child-env.mjs`、`tools/hub-probe/ProbeClient/ChildEnvironment.cs` 完全相同（`test_measure_sync` 比對）。
- **前提**（任一不成立，對應的判定是 `null`、原因列在 `result.invalid`）：每一回合 `ok`；`head-unpaired` 是 `paired:false`；`head-paired-offline` 與 `head-paired-blackhole` 是 `paired:true`、`state` 為 offline／connecting，而且 `loadedForbidden` 含 SignalR 與 Sqlite（陽性對照：真的走到同步路徑）；`head-paired-online` 是 `paired:true`、`state:"online"`；`head-paired-backfilling` 每一輪 `pair --no-drain` 輸出的 `backfill` 不是 null（記在回合的 `pairBackfill`；null＝app 啟動之前回補就做完了，量到的不是回補中的暖啟動）；S6 每一回合 `paired:true`、`state:"online"`、沒有 `notOnline`、`heartbeatsDuringSample` 是正數；S6 每一回合 `settleTimedOut` 是 `false`（逾時或沒有記＝閒置記憶體取在重整或回補的途中，只有記憶體判定變 `null`）；S6 已配對與 `head-unpaired` 每一回合都有 `memoryIdleLateMb`（沒有＝探針太舊，只有記憶體判定變 `null`）。
- **單元測試**：`python -X utf8 -m unittest discover -s tools/fake-device -p "test_*.py"`（`test_measure_sync.py`：`--app-env` 與 `--idle-extra-seconds` 的驗證、剝掉的環境變數（含 `COMPlus_`，和另外兩份清單相同）、判定的前提、五個 head 組各自超過基準＋50 ms 的判定、S6 記憶體以兩次取樣的較大值判定與 30.0／30.1 MB 的邊界、S5 各組的閒置記憶體與差值、吞封包的監聽；不啟動 app 與伺服器）。
- **建置指紋**：summary 的 `build` 記下 git HEAD、工作樹是否乾淨（`dirty`、`dirtyFiles`），與受測的 exe／dll（app、Protocol、MyriagentHub、FakeDevice、基準建置）的 SHA-256 與修改時間；dll 比 HEAD 的提交時間舊（忘了重建）就是 `stale`。引用數字時一併附上 HEAD 與 `Myriagent.dll` 的雜湊。
- **收尾**（不論成敗）：關掉吞封包的監聽；每一個配對過的資料根（`root-paired` 與 `head-paired-backfilling` 每一輪的 `root-backfilling-<輪>`）有 `sync\device.json` 就 `unpair`（刪掉裝置金鑰；配對在伺服器已收下、device.json 已寫入之後才失敗時也一樣），只剩 `pending-key.json` 就跑一次 `status`（啟動時的配對清理會刪）；`admin stop`；掃金鑰存放區，這次的 fake-device 建、還留著的 `Myriagent.Device.*` 金鑰檔寫進 `orphanDeviceKeys`（別的程式同時建的另列在 `otherNewDeviceKeys`，只記錄）。
- 結果寫進 `<工作資料夾>/summary.json`，最後一行印出判定（`s5Pass`、`s6UnpairedPass`、`s6CpuPass`、`s6MemoryPass`、`invalid`、`orphanDeviceKeys`）；任何判定不是 `true`、有孤兒金鑰或收尾失敗時結束碼 1。
- **負載**（WP4.9：S5 的時間照負載判讀，數字旁邊記當時的負載）：量測的同時在背景跑 `load-sampler.ps1`（每 30 秒一行整機 CPU 與相關程序數，停止檔出現就結束；只讀程序清單與 CPU 計數器），`python -X utf8 tools/fake-device/load_stats.py <load.txt>` 摘成取樣數、起訖、CPU 平均／範圍、>50% 的次數與各程序數的範圍（`test_load_stats.py`）。
  ```powershell
  $job = Start-Job -FilePath tools/fake-device/load-sampler.ps1 -ArgumentList <暫存>\v9.load.txt, <暫存>\v9.stop, 30
  python -X utf8 tools/fake-device/measure_sync.py --work <暫存>\v9 ...
  New-Item -ItemType File <暫存>\v9.stop; Wait-Job $job -Timeout 90; Remove-Job -Force $job
  ```

- **舊對話**（`--conversations N`，WP4.3 步 13）：三個資料根（unpaired、base-unpaired、paired）在啟動之前都以 `seedConversations` 灌**同一份** N 個舊對話（種子＝`--seed`、`baseTimeUtc` 固定 `2026-10-20T00:00:00Z`、每個 `--items-per-conversation`（預設 40）則、最新的早 `--conversation-days`（預設 3）天、帳戶別名 `measure-seed`，三個資料根都加這個帳戶）；summary 的 `conversations.roots` 記每個資料根的筆數與指紋，三份不同時 S5 與 S6 記憶體的判定是 `null`。一定要有 `--base-app`（同一份資料跑基準建置比較，不拿 930 ms 比；沒有就結束碼 2），`--base-app` 可以給 bin 資料夾或其中的 `Myriagent.exe`；基準建置可以直接用上面 `git archive <舊提交>` 建出的 `src/Myriagent/bin/Release/net10.0-windows/win-x64`（<舊提交> 已含 加入探針開關的提交 的探針開關）。S6 的回合之前先以 `status --wait-backfill` 等回補做完，秒數記在 `conversations.backfillSeconds`（回補不限速，S6 照舊在回補做完之後取樣）。**重新為基底之前**（同一棵樹的 MyriagentHub 沒有 WP4.2、不宣告 conv1）回補不會開始，量到的是「有 N 個舊對話、沒有回補」的情境，量不到「已回補」的情境；那要等重新為基底之後（計畫步 16）重量。

