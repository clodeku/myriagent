# 網頁 ↔ 宿主訊息協定（v1；第 3 階段起加 v2 同步擴充，第 4 階段定稿 v2）

介面是 WebView2 載入的嵌入網頁（`https://myriagent.local/index.html`），**狀態全在 C#（`AgentHub`）**，網頁是純顯示層。
雙向都是 JSON 物件：宿主 → 網頁用 `type` 欄位，網頁 → 宿主用 `cmd` 欄位。
同一個中樞可以同時服務多個視窗（主視窗＋彈出視窗），每個視窗是一個「view」。

- 網頁送出：`chrome.webview.postMessage(obj)`；拖放／貼上檔案用 `chrome.webview.postMessageWithAdditionalObjects(obj, files)`。
- 網頁接收：`chrome.webview.addEventListener('message', e => handle(e.data))`。
- **安全**：代理產生的文字一律用 `textContent`；只有宿主送來、欄位名稱以 `html` 結尾的字串（C# Markdig 停用原始 HTML 後轉出）可用 `innerHTML`。網頁 CSP：`script-src 'self'`，所以不能有 inline script、`onclick=` 屬性。
- 時間欄位一律是 Unix 毫秒（number），由網頁自行格式化（相對時間、倒數）；沒有值時是 null。
- 標示「選用」或資料形狀裡寫 `| null` 的欄位：沒有值時宿主可能**直接省略欄位**（例如 Item 的 `diff`、`attachments`、`media`），網頁要把「沒有欄位」與 null 視為相同。
- 所有 `id` 都是不透明字串。
- 標示 **〔熱更新〕** 的欄位與指令是第 1 階段「CLI 熱更新與自動續接」新增的（全部是新增、沒有改既有欄位的意義；契約見 `docs/plans/2026-10-10-hot-restart-contract.md`）。這些欄位都由宿主產生、網頁只顯示，網頁不自行推算換版狀態；欄位不存在（舊宿主）＝沒有換版要處理。呈現方式見第五節。
- 標示 **〔同步〕** 的欄位與指令是第 3 階段「多電腦同步」新增的 v2 擴充（設計 `docs/plans/2026-10-10-multi-device-sync.md` 4.9.3；裝置↔伺服器的協定見 `docs/sync-protocol.md`）。同一份網頁在兩種宿主下執行：**桌面**（WebView2，宿主是 `AgentHub`）與**瀏覽器**（`https://hub.example.com`，宿主是 MyriagentHub 的瀏覽器閘道）。欄位不存在（舊宿主、桌面未配對）＝單機模式，網頁照 v1 顯示；只有網頁用到的欄位桌面忽略。裝置名稱、電腦名稱、帳戶名稱一律是不可信字串，用 `textContent`。
- 標示 **〔電腦狀態〕** 的訊息與指令是對話檢視右側的「電腦狀態」面板（2026-10-22；`Web/monitor.js`）：每台連線電腦的 CPU、記憶體、CPU 溫度與顯示卡（2026-10-23）、磁碟、與伺服器的延遲、網路流量，以及 MyriagentHub 主機自己的負載。其他電腦的數字經伺服器轉來（裝置↔伺服器那一段見 `docs/sync-protocol.md`）；不送 `monitor` 的舊網頁收不到 `deviceStats`，不認得 `deviceStats` 的舊網頁直接忽略。
- 標示 **〔換機〕** 的欄位、訊息與指令是第 5 階段「換機接手與離線續接」的部分（第七節；`docs/sync-protocol.md` 第 16 節）：`hand1` 啟用之前宿主不送，欄位不存在＝沒有換電腦的功能。
- 標示 **〔遠端〕** 的欄位、訊息與指令是第 4 階段（對話同步、網頁即時檢視與遠端操作）定稿的 v2 部分，契約是 WP4.1（設計 14.11；遠端指令的信任模型、信封與裝置端的驗證順序見 `docs/sync-protocol.md` 第 14 節）。**WP4.1 只立契約**：各節寫明由哪個工作包開始送或處理，在那之前宿主不送這些訊息、遠端指令在瀏覽器模式照舊回 `not_available`。網頁收到不認得的 `type` 一律忽略（`app.js` 的 `default`）。

## 一、宿主 → 網頁（`type`）

### `init`
網頁送 `ready` 後的第一則訊息。
```jsonc
{
  "type": "init",
  "mode": "main",                       // "main"＝完整的視窗（主視窗或另外開的視窗）；"popout"＝只顯示一個對話的彈出視窗
  "windowRole": "primary" | "extra" | null,   // 〔多視窗〕只有桌面：主視窗 "primary"、另外開的視窗 "extra"（搬走最後一個分頁時送 closeWindow）、彈出視窗 null；瀏覽器版與舊宿主沒有這個鍵
  "popoutConversationId": null,         // mode=popout 時要顯示的對話
  "app": { "version": "0.2.0", "build": "ab12cd3", "release": "0.2.0-ab12cd3", "commitAt": 1792732512000 | null },   // 組件的 InformationalVersion；build＝提交前 7 碼，沒有提交雜湊（在 git 外建置）時是 "dev"（release＝"0.2.0-dev"）；
                                        // commitAt＝這一版的提交時間（Unix 毫秒；建置時以 git 讀 HEAD 的 committer date 寫進組件中繼資料 MyriagentCommitTime，CommitTimeRules），建置時沒有 git 是 null，舊宿主沒有這個鍵
  "settings": Settings,
  "accounts": [AccountView],
  "conversations": [ConversationSummary],
  "replicasPending": false,             // 〔同步〕只有桌面：其他電腦的對話（複本）清單還沒載入（見下方說明）；瀏覽器版、舊宿主沒有這個鍵
  "cli": { "Claude": CliView, "Codex": CliView, "Antigravity": CliView, "Grok": CliView },
  "providers": [ProviderView],          // 支援的 CLI，順序＝宿主的 AgentProviders.All（CLI 卡、新增帳戶的選項、設定頁「模型設定」都照這個順序）
  "devicesCli": { "d_…": { "Claude": DeviceCliView, "Codex": DeviceCliView } } | null,   // 〔同步〕各電腦的 CLI 狀態（儀表板「各電腦 CLI 版本」）；未配對為 null
  "appUpdate": AppUpdateView | null,    // app 自我更新的狀態（頂部列下方的橫幅、設定頁的「版本與更新」分頁）；協調器還沒送過為 null
  "layout": Layout | null,              // 這個視窗上次儲存的版面（完整的視窗才有；〔多視窗〕每個視窗各一份）；null＝網頁自行決定（開一個新對話分頁）
  "restoredDrafts": ["d…"],             // app 更新後還原的草稿對話 id（主視窗第一次就緒時才可能有，其他時候是空陣列）：見下方說明
  "movedIn": ["c1"],                    // 〔多視窗〕只有桌面：為了搬進對話而開的新視窗（拖到視窗外、moveToNewWindow），那個對話在 init 之後由宿主送，網頁照 restoredDrafts 的方式放好分頁；其他時候是空陣列，舊宿主沒有這個鍵
  "remote": Remote | null,              // 〔同步〕多電腦同步；桌面未配對（也沒在配對）為 null；同步元件還在啟動時也是 null（看 syncStarting）
  "syncStarting": false,                // 〔帳戶目錄〕只有桌面：同步元件還在讀配對資料（主視窗載入後約 3 秒內）。這時 remote 是 null，但不一定是未配對：
                                        // 設定頁顯示「同步元件啟動中…」、不顯示配對表單；收到任何 `sync` 就結束（讀完沒有配對時宿主送一則 sync:null）
  "tokens": Tokens | null,              // 側欄帳戶卡的今日 token 計數；桌面是這台的、〔遠端〕瀏覽器版是各電腦的合計（scope:"all"，sync-protocol 14.9）；舊宿主沒有這個鍵（網頁不顯示 token 列）
  "health": HealthView | null,          // 儀表板的健康度（只有桌面的主視窗；還沒分析過時 accounts 是空的、instructions 是 null）；彈出視窗、瀏覽器版與舊宿主是 null 或沒有這個鍵
  "schedules": ScheduleView | null,     // 排程頁（第一節 schedules；只有桌面的主視窗與額外視窗，還沒讀過時 loaded 是 false）；宿主沒掛讀取器、彈出視窗、瀏覽器版與舊宿主是 null 或沒有這個鍵（不顯示排程鈕）
  "voiceInput": true                    // 只有桌面：這台接上了語音訊息的辨識程序（輸入區顯示麥克風鈕，第二節 voiceMessage）；瀏覽器版、舊宿主沒有這個鍵（瀏覽器版由對話所在的電腦辨識，不看它）
}
```
〔遠端〕瀏覽器版的 `app` 是伺服器的：`{ "version": "0.2.0", "build": "ab12cd3", "commitAt": 1792732512000 | null }`（WP4.6 起帶 `build`，同 `/healthz` 的 build，沒有 `release`；`commitAt` 是這個 build 的提交時間，規則同桌面）。網頁記下第一則 `init` 的 `build`，之後的 `init`（重連）只要不同就算伺服器換了版本——缺少、不是字串或空字串一律當成沒有，所以回滾到沒有這個鍵的舊伺服器也算。換版之後網頁一定會自動重新載入（`liverules.reloadPlan`）：沒有重新載入會弄丟的東西（等你回應的卡片打到一半的字或選項、還在上傳或加進輸入區中的附件）、沒有開著的對話框、沒有指令還在等結果、使用者不在用（分頁看得到時：焦點不在有字的輸入框、10 秒內沒有按鍵、點擊、滾輪、觸控或輸入；分頁看不到時不管這兩項），而且這個分頁 60 秒內沒有自動重新載入過（sessionStorage `myriagent.app.reloadAt.v1`）就馬上重新載入；還不行時在頂部列下方顯示「伺服器已換版本，重新載入以取得對應的網頁」與「重新載入」鈕，每 2 秒重查、分頁一藏起來也重查，狀況解除就自動重新載入（build 又變回第一則的就不再等、拿掉橫幅）。重新載入之前（自動或按鈕）先把每個分頁輸入框沒送出的字存進 sessionStorage `myriagent.app.reloadDrafts.v1`（伺服器草稿另記帳戶與電腦），載入後讀出一次就刪、各對話建好時填回輸入框；新版的伺服器已經沒有那個草稿時（重開過），以記下的帳戶與電腦在同一個位置開新草稿、字還原。sessionStorage 不能用（讀寫丟例外）時不自動重新載入，只顯示橫幅（記不下時間會無限重新載入，字也存不下）。

**app 更新後還原的草稿對話**：新版啟動後，宿主把換版前還沒送出的新對話（沿用原本的 id）在 `init` 之後緊接著以 `conversation`（`paneId` 為 null、可能帶 `draft`）送來。草稿不在 `conversations` 清單裡，網頁還原版面時把 `restoredDrafts` 列出的 id 也當成有效的分頁：留在 `layout` 存下的位置（作用中的分頁照舊）、不送 `open`（宿主已經會送）；`layout` 裡沒有的（例如原本顯示在彈出視窗）放在聚焦窗格的最後、不搶作用中的分頁。版面裡有任何分頁（含還原的草稿）時，網頁不另開空白的新對話。

**〔同步〕還原版面時其他電腦的對話還沒進清單**（`replicasPending`，2026-10-29：重開機後其他電腦的對話分頁全部不見，少了分頁的版面又馬上存回去）：同步元件在主視窗載入後約 2.5 秒才啟動，複本要等引擎從 sync.db 讀出來才進 `conversations`，`init` 時清單裡一定還沒有它們。宿主在同步協調者建立後把 `replicasPending` 設成 true，收到引擎的第一份整份複本清單、或確定不會有複本（未配對、配對資料損毀或讀不到、建引擎失敗、沒有發布者、解除配對）時改成 false 並重送 `conversations`（之後每則 `conversations` 都帶這個鍵）。網頁（`liverules.awaitsList`、`workspace.settleAwaiting`）在 `replicasPending` 為 true 時，把版面裡不在清單、形狀是對話 id 的分頁照原位留著（標題「載入中…」、不送 `open`，存版面時照樣存進去）；之後的 `conversations` 裡出現了就接上（作用中的分頁送 `open`，其他的切過去才送），`replicasPending` 變成 false 時還不在的才關掉（不送 `close`），關完一個分頁都沒有時照還原的規則開一個新對話。

〔多人協作〕`init.viewer`＝`{role: "owner"}`（桌面、擁有者的瀏覽器）或 `{role: "collaborator", name: 顯示名稱, userName}`（協作人的瀏覽器；舊宿主沒有這個鍵，當成擁有者）；`init.collaborators` 見 `collaborators`。協作人的 `init`（伺服器組）：`accounts` 是空陣列、`tokens`／`schedules` 是 null、`devicesCli` 與 `cli` 是空物件、`remote.devices` 是空陣列、`settings` 是他自己的介面設定（四個漫遊鍵，存在伺服器、不同步給電腦）、`conversations` 只有分享給他的對話（未讀一律 false）；之後也不推 `accounts`、`devices`、`cli`、`tokens`、`schedules`、`control`、`deviceStats`。網頁在協作人模式只留對話（側欄的對話清單、窗格、檔案瀏覽器），藏起儀表板、排程、電腦狀態、帳戶區、新對話；對話的模型、強度、權限模式、帳戶、資料夾只顯示、不能改；設定頁只有外觀、通知、輸入與「我的帳號」。

### `accounts`
帳戶清單或任何帳戶的狀態、用量、對話數改變時送（宿主節流，最多每 500ms 一次）。整份取代。
```jsonc
{ "type": "accounts", "accounts": [AccountView] }
```

### `tokens`
各帳戶的 token 計數變了（對話的 CLI 每回報一次 API 呼叫的用量就變，與其他更新一起每 50ms 合併）、或跨了本機日期（維護計時器，30 秒內）時送。整份取代。〔遠端〕瀏覽器版由伺服器送：任一台電腦送來新的計數（每台最多每 5 秒一次）、裝置撤銷、帳戶刪除或跨了伺服器的本機日期（閘道的保底重讀）讓內容變了才送。
```jsonc
{ "type": "tokens", "tokens": Tokens }
```

### `conversations`
對話歷史清單（所有帳戶）改變時送，整份取代；依更新時間新到舊，最多 1000 筆。日期分組（`group`）是送出當下算的，跨日時宿主會重送一次（網頁照用 `group`，不自己重算）。
```jsonc
{ "type": "conversations", "items": [ConversationSummary], "replicasPending": false }   // replicasPending：只有桌面，同 init（第一節 init 之後的說明）
```
〔遠端〕瀏覽器模式（伺服器送，WP4.2 起）：
- 內容是伺服器物化的對話（所有電腦擁有的，不含已刪除的），`init.conversations` 是同一份。排序依顯示用的更新時間（取電腦送的時間，但不晚於伺服器收到的時間；沒有時用收到的時間），同時間再依伺服器的 seq；只影響畫面順序。
- `group` 依**每條連線**的時區算：`ClientState.tzOffsetMinutes` 照 JS 的 `getTimezoneOffset()`（UTC 減本地的分鐘數，臺灣 `-480`），當地時間＝UTC **減** `tzOffsetMinutes` 分鐘；收到 `ClientState` 之前用伺服器的時區。到了那條連線的下一個當地午夜重算，分組變了才重送。
- 內容（含 `unread`、`running`、`deviceOnline`）變了才送，最多每 50 ms 一次；同一條連線排著的 `conversations` 只留最新的一則（第六節第 8 點的合併）。
- 擁有者電腦離線時那些列的 `running` 是 false；`unread` 是伺服器記的未讀（第三節 ConversationSummary）。

### `conversation`
回應 `open`／`newConversation`／`setAccount`：整個對話的內容（最後一頁）。網頁收到就在對應分頁顯示（沒有分頁就開在 `paneId` 指定的窗格或目前聚焦的窗格）。
網頁送出 `open`／`setAccount` 後、回覆到達前就關掉的分頁，晚到的回覆（`paneId` 為 null）直接丟掉：宿主處理 `close` 後不再送這個對話的更新，照常開成分頁會停住不動。
```jsonc
{
  "type": "conversation",
  "conversationId": "c1",
  "paneId": "p1" | null,                // newConversation 帶來的窗格 id，原樣送回
  "header": Header,
  "items": [Item],                      // 依時間順序，最多最後 80 筆
  "hasMore": true,                      // 還有更早的項目（網頁顯示「載入較早的訊息」，送 loadOlder）
  "composer": Composer,
  "draft": "還沒送出的字" | null,        // 選用：app 更新前輸入框裡還沒送出的字（draftSnapshot 存下的），只在換版後第一次送這個對話時帶一次；網頁在輸入框是空的時填入
  "reload": true                        // 選用：宿主主動重送整份（不是 open 的回覆）。網頁沒有這個分頁時直接丟掉，也不算 open 的回覆（不抵掉等待回覆的計數）
}
```
宿主主動重送的時機（帶 `reload`，WP4.3）：開著的唯讀複本（其他電腦的對話）整份換掉、或一次變了超過一頁要重排時。不帶這個欄位時，網頁收到不認得的對話會開成新分頁，所以已經關掉的分頁在宿主處理 `close` 之前收到主動重送，會被重開而且之後停住不動。
〔遠端〕瀏覽器模式（伺服器送，WP4.2 起）：
- 回應 `open`（`paneId`、`draft` 是 null）與 `newConversation`（伺服器草稿，見第六節第 9 點；`paneId` 原樣送回、`header.isDraft=true`、沒有項目）。標頭、項目與輸入區在同一個唯讀交易讀，之後只推比這份快照新的增量。
- 一頁最多 80 筆、而且渲染後合計 ≤1 MB（放不下就少放、至少一筆），`hasMore` 照實；單筆渲染後 ≤256 KB，超過時長欄位只留尾段，仍超過或內容讀不到（WP4.7 之前的大欄位 blob）就送一則佔位項目。
- `open` 的對話不存在或已刪除：`Cmd` 回 `invalid`，另送 `conversationClosed`。
- 送出佇列裡 `conversation` 是這個對話的屏障：入列時清掉排在它前面、同一個對話的 `item`、`removeItem`、`header`、`composer`。
- 伺服器也會**主動**送開著的對話的 `conversation`（網頁沒有送 `open`；`paneId`、`draft` 一樣是 null，網頁照樣整份取代那個分頁）：變更通知溢出的整份重讀，以及一輪要收的增量超過一頁（80 筆）或排進去會讓送出佇列超過上限的一半時（電腦整份重新發布、改回完整同步、回補時一輪就有幾百筆），改送一份新的快照，不逐筆送 `item`。
- 這種主動重送不帶 `reload`：網頁沒有這個對話的分頁、也沒在等它的回覆（沒有送過還沒回的 `open`、換帳戶）時直接丟掉（WP4.6 合併前審查 LIVE-3）。使用者剛關掉分頁、伺服器還沒處理 `close` 時送到的，不會重開成停住不動的分頁。

### `olderItems`
回應 `loadOlder`。
```jsonc
{ "type": "olderItems", "conversationId": "c1", "beforeId": "i81" | null, "items": [Item], "hasMore": false }
```
`beforeId`（WP4.6 合併前審查 LIVE-2 起）：那一次 `loadOlder` 帶來的 `beforeId`，原樣送回（桌面與伺服器都帶）。網頁只採用自己還在等的那一頁：沒有在等（`loadOlder` 之後先到了整份 `conversation`，例如重連後重新 `open`、伺服器主動重送）或 `beforeId` 不是這一次請求的，就丟掉，不插到新的視窗前面。沒帶 `beforeId` 的（舊版的宿主）只看有沒有在等。

### `header` / `item` / `itemPatch` / `removeItem` / `composer`
只送給開著該對話分頁的 view（網頁以 `open`／`close` 告知宿主；分頁藏在後面也照送，切過去不必重新 open）。串流期間宿主每 50ms 合併送一次。
```jsonc
{ "type": "header", "conversationId": "c1", "header": Header }
{ "type": "item", "conversationId": "c1", "item": Item }          // 新增或整筆取代（以 item.id 判斷）；網頁沒有這筆、hasMore 為真且 seq 比已載入的第一筆小＝還沒載入的較早項目，先不顯示
{ "type": "removeItem", "conversationId": "c1", "id": "i9" }
{ "type": "composer", "conversationId": "c1", "composer": Composer }
```
`header.goal`（2026-10-25）：Claude 的 `/goal` 是程序的 Stop hook，每一輪結束時評估、在逐字稿記一筆 `goal_status`；按了停止之後目標還在，下一則訊息又會接著追（protocol-notes「Claude：/goal」）。會話在一輪結束後讀逐字稿尾端最後一筆（只看這個程序啟動之後寫的：`--resume` 開的新程序沒有之前的目標），還沒達成就是 `{ condition }`（最多 2000 字）；程序開始、結束時清掉。只在記憶體。網頁（`claudeoptrules.goalView`）在晶片列顯示「目標：…」，選單有完整的條件與「清除目標」（`goalClear`；執行中停用並說明）。

`header` 的 `commands` 與 `models`（2026-10-23 起；`agents`、`outputStyles` 同樣處理，`HeaderListRules.Fields`／`liverules.HEADER_LIST_FIELDS`）：桌面宿主對每個 view 記住上次送的這兩份清單，沒變時 `header` 訊息**不帶這兩個鍵**，網頁沿用上一份（`liverules.mergeHeaderLists`；同一批排著的那一份帶了就沿用它的）。帶了的（空陣列也算）照新的取代。`conversation` 快照一律帶整份；宿主對沒送過整份的 view 一律送整份。一個 Claude 帳戶的指令清單有 14～33 KB，執行中標頭每 50 ms 就可能送一次。伺服器（瀏覽器版）照舊一律帶。

〔遠端〕瀏覽器模式（伺服器送，WP4.2 起）：只送給「開著」這個對話的連線（`open` 過、還沒 `close`，第六節第 8 點的定義），而且只送比那條連線的 `conversation` 快照新的變更；每 50 ms 依對話摺疊一次（同一個項目只送最後一筆）。送出佇列以 `item:{對話}:{項目}`（`item` 與 `removeItem` 共用，後到的整筆取代）、`header:{對話}`、`composer:{對話}` 原地合併。串流中的項目（電腦的即時更新）也以 `item` 送，和持久的項目共用同一個合併鍵；持久的版本到了就以它為準（〔遠端〕的即時更新只是提示，`docs/sync-protocol.md` 14.3）。擁有者電腦上線或離線時重送開著它的對話的 `header` 與 `composer`。

〔遠端〕即時更新的增量（2026-10-23 起，`patch1`；設計 `docs/plans/2026-10-23-live-delta-and-replica-live.md` 3.2、3.4、3.5）：
```jsonc
{ "type": "item", "conversationId": "c1", "item": Item, "rv": 9123 }      // 疊加層（電腦的即時更新）的 item 多帶 rv：伺服器那一格的版本；持久的 item 不帶
{ "type": "itemPatch", "conversationId": "c1", "id": "i9", "rv": 9130, "baseRv": 9123,
  "set": { /* Item 去掉 html、detailHtml、detail、output 的其餘欄位（一律帶） */ },
  "ops": [ { "f": "html", "at": 18211, "s": "<p>新的一段</p>", "n": 18960 } ] }
```
- 只送給 `ClientState.features` 含 `patch1` 的連線（第六節第 13 點）；`Item` 形狀不變（`rv` 在訊息層），舊網頁忽略 `rv`。
- 新的 Item＝`set`＋四個長欄位：有 op 的是「基底那一欄的前 `at` 個 UTF-16 碼元＋`s`」（長度要等於 `n`），沒有的沿用基底。基底是網頁排著還沒套用的那一筆、否則畫面上那一筆；`baseRv` 要等於網頁記著的這一筆的 `rv`（帶 `rv` 的 `item` 記下、持久的 `item`、`removeItem`、`conversation`、`conversationClosed`、`init` 清掉）。`html` 仍是伺服器淨化後的整筆輸出的子字串，拼出來的就是伺服器那一刻要送的整筆。
- 伺服器只在「這條連線最後入列的就是 `baseRv` 那一版、送出佇列裡沒有同一筆還沒送的訊息」時送（增量從不和排著的訊息合併，也沒有合併鍵；`conversation` 屏障照樣清掉它）；長欄位「有／沒有」的組合變了、或增量不比整筆省一半時送整筆 `item`。
- 對不上（`baseRv` 不同、`at` 超出範圍、`n` 對不上、沒有基底）就丟掉那則，對那個對話重送一次既有的 `open`（伺服器照樣回 `conversation` 快照與疊加），每個對話 10 秒最多一次。不加新的網頁指令。
- 沒宣告 `patch1` 的連線（部署前載入的舊分頁）照舊：整筆，只送電腦送來 ≤64 KB 的即時更新（`live2` 讓上限到 256 KB，舊分頁不收更大的整筆）。

### `conversationClosed`
對話被刪除，或草稿被丟棄：網頁關掉對應分頁。
```jsonc
{ "type": "conversationClosed", "conversationId": "c1", "replacedBy": "5f3a…" | null }   // replacedBy：〔遠端〕選用，見下方
```
〔遠端〕瀏覽器模式：對話在伺服器上被刪除（擁有者刪除、改成只在這台電腦或換到 Antigravity）時送給開著它的連線；`open` 不存在或已刪除的對話、伺服器草稿被丟棄（每個工作階段最多 8 份，超過時丟最久沒用的；`close`）時送給那個工作階段的每一條連線。

〔遠端〕`replacedBy`（WP4.6 起，選用）：伺服器草稿第一則送出後換成正式對話——第一次 `send` 建立的新對話出現在伺服器時（`conv.created`，通常比 `commandResult done` 早），伺服器對那個工作階段的每一條連線送 `conversationClosed`，`conversationId` 是草稿 id、`replacedBy` 是新對話的 id，並丟掉草稿。網頁在草稿的位置換成這個 id 的分頁（作用中的照樣作用中）並重新 `open`；這個 id 已經開在別的分頁時只關掉草稿、聚焦那個分頁。送的時機有三個：這一輪的變更裡有它、變更通知溢出時查目錄、`open` 草稿時對話已經在目錄裡（變更在沒有連線時被丟掉）。伺服器拿走草稿時留一筆墓碑（同一個工作階段、至少 10 分鐘而且不短於草稿的斷線寬限、每個工作階段最多草稿上限的兩倍筆，WP4.6 合併前審查 LIVE-1）：換的那一則送進已經斷掉的連線、還沒送過 `init` 的連線，或那時這個工作階段沒有連線，網頁重連後重新 `open` 草稿（或對草稿送指令，指令照舊回 `invalid`）時，伺服器從墓碑再送一次帶 `replacedBy` 的 `conversationClosed`（`open` 不算錯誤）；墓碑過期、或換成的對話已經不在目錄裡，才只送沒有 `replacedBy` 的 `conversationClosed`（新對話照常在清單裡）。沒有 `replacedBy`（或舊網頁不認得它）時照舊只關掉分頁。

### `conversationMoved`〔多視窗〕
只有桌面：這個對話的分頁搬到 app 的另一個視窗了（`tabMoveIn`、拖到視窗外、`moveToNewWindow`）。網頁關掉這個分頁、**不送 `close`**（宿主已經不把這個視窗算成開著它）；另外開的視窗（`init.windowRole` 是 `extra`）搬走之後一個分頁都沒有時送 `closeWindow`（像 Chrome 拖走最後一個分頁）。彈出視窗收不到。
```jsonc
{ "type": "conversationMoved", "conversationId": "c1" }
```

### `tabDrag`〔多視窗〕
只有桌面：另一個完整視窗開始拖分頁（`conversationId`）或拖完了（`null`），宿主送給其他的完整視窗。WebView2 之間的拖曳不一定帶得過自訂的資料型別：網頁在收到這則之後把「不是檔案」的拖曳當成分頁，`dragover` 時接受（顯示放置位置）、`drop` 時送 `tabMoveIn`；拖曳資料裡有分頁的型別時以它為準。
```jsonc
{ "type": "tabDrag", "conversationId": "c1" | null }
```

### `focusConversation`
要求網頁把某個對話切到前景（例如使用者點了系統通知、〔多視窗〕分頁剛搬進這個視窗）。沒有開著就送 `open`。
```jsonc
{ "type": "focusConversation", "conversationId": "c1" }
```

### `flash`
要你回來看的事（對話完成、失敗，代理提問、要求核准、計畫等確認；和語音通知念的是同一組，背景工作還在跑的結束、使用者按停止的那一輪不送）一發生就送給每個視窗（只有桌面），不管語音通知開不開：網頁（`attnflash.js`）讓那個對話的分頁與側欄那一列一直閃、對話標頭以帳戶色一直閃，**閃到使用者點了那個對話為止**（2026-10-25 起；以前是語音開始念時閃三下）。「點了」＝在那個對話的分頁、側欄那一列或對話裡按下滑鼠、觸控或筆，在對話裡按鍵（在分頁、側欄的列上按 Enter），或點了系統通知切到它（`focusConversation`）；一個視窗點了，以 `BroadcastChannel('myriagent.attn-flash')` 告訴同一個來源的其他視窗（彈出視窗、瀏覽器的其他分頁）也停。狀態只在網頁的記憶體：重新載入、之後才開的視窗不閃；對話被刪除（`conversationClosed`）就不閃了。減少動態或關掉動態效果時是靜態標示（一樣到點了才拿掉）。沒開著的對話只有側欄那一列；不切換分頁、不搶焦點。
```jsonc
{ "type": "flash", "conversationId": "c1" }                     // 開始閃到使用者點它
{ "type": "flash", "conversationId": "c1", "speaking": true }   // 語音通知開始念這件事（聲音出來的那一刻，設定 voiceNotifications 開著才送）：還在閃就讓標頭另外閃三下（和側欄點對話時同一種），一聽到就找得到念的是哪一個；沒在閃的不動
```

### `showView`
要求主視窗切換檢視（系統匣選單「帳戶儀表板」）。
```jsonc
{ "type": "showView", "view": "dashboard" | "chat" }
```

### `files`
回應 `listFiles`（對話內的檔案瀏覽器）。
```jsonc
{
  "type": "files",
  "requestId": "r12",
  "conversationId": "c1",
  "path": "C:\\repo\\proj\\assets",     // 實際列出的資料夾
  "parent": "C:\\repo\\proj" | null,    // 上一層；磁碟根目錄為 null
  "root": "C:\\repo\\proj",             // 對話的工作資料夾（麵包屑從這裡開始顯示「工作資料夾」）
  "entries": [FileEntry],               // 資料夾在前，名稱自然排序；最多 3000 筆
  "truncated": false,
  "error": null                         // 讀不到時的錯誤訊息（entries 為空）
}
```
〔遠端〕瀏覽器版（WP4.7 起；擁有者電腦回、伺服器投影與改寫後只給發起的連線）：
- `path`、`parent`、`root`、FileEntry 的 `path` 是擁有者電腦上的完整路徑（下一次 `listFiles`、`fileFetch` 原樣帶回）；範圍只有對話的工作資料夾子樹與對話的 `attachments\`，在範圍的根時 `parent` 是 null，範圍外、禁止的資料夾回 `commandResult rejected policy`。`dirsOnly` 的範圍是這台的最近資料夾與工作資料夾，`path` 是 null 時回虛擬根（`parent` null）。
- FileEntry 的 `url`、`thumbUrl` 是本站相對網址：圖片與影片（可顯示的副檔名）是 `/m/{key}`、`/t/{key}?s=160&v={版本}`（`key` 是 `{deviceId}-{ref}`；參照 30 天內有效），其他一律 null；電腦送的網址一律丟掉。不列重新解析點（OneDrive 的佔位檔例外）；禁止的檔名照列名稱、沒有網址。
- `truncated: true`：清單在序列化後約 700 KB 處停下（遠端輸出的上限），或超過 3000 筆。`error` 是給人看的訊息（≤1,000 字）。
- `fileSearch` 的 `results` 同桌面（`fullPath` 是電腦上的完整路徑），最多 30 筆，只搜範圍內、不跟重新解析點。
- 網頁（WP4.6）：遠端對話的資料夾選單有「瀏覽資料夾…」（瀏覽器版的遠端正式對話與新對話〔伺服器草稿，2026-10-27〕；執行中、沒連線、電腦離線、政策不允許時停用並說明原因），在檔案抽屜以 `dirsOnly: true` 挑選：先列虛擬根（「最近的資料夾」，最上面另加「目前的工作資料夾」＝`root`），只顯示資料夾，按「選這個資料夾」送 `setFolder`。被拒或沒送出時抽屜顯示「讀不到這個資料夾」或「目前沒有連線，連上之後才能瀏覽」；結果不明（重連後的 `init`、150 秒還等不到，`files` 只送回發出的那條連線、等不到了）顯示「沒有收到那台電腦的回覆，請按重新整理再試一次」，不再一直轉圈（WP4.6 合併前審查 LIVE-6、P1；之後真的到了同一個 `requestId` 的 `files` 照樣採用）。挑選模式的路徑列只有虛擬根列出的資料夾（最近的資料夾與工作資料夾）底下的段可以點，範圍外的上層（例如 `C:\`）照樣顯示但停用（電腦一定拒絕，合併前審查 P4）。瀏覽器版的檔案項目沒有「在檔案總管顯示」與「加入附件」。
- 新對話（伺服器草稿）的「瀏覽資料夾…」（2026-10-27 使用者要求：選完電腦就能挑那台的資料夾，不必先送一則訊息）：網頁照樣送 `listFiles {conversationId: 草稿 id, dirsOnly: true}`，伺服器改成 `listFolders`（sync-protocol 14.4）轉給草稿選的電腦（沒選是第一次送出時會挑的那台），那台還沒有這個對話，只列它的最近資料夾（範圍同 `dirsOnly`，但沒有工作資料夾、`root` 是 null、app 自己的資料夾整個不給）；回來的 `files` 由伺服器換成草稿 id。選定後同樣送 `setFolder`（伺服器記在草稿，第一次送出時那台檢查）。那台的版本還不認得 `listFolders` 時回 `rejected invalid`，抽屜說明「那台電腦的 Clode Myriagent 版本太舊…請先更新那台，或改用「輸入資料夾路徑…」」（`liverules.listFailText`）；草稿上沒帶 `dirsOnly` 的 `listFiles` 照舊 `invalid`。

- 〔電腦狀態〕電腦狀態面板的檔案傳輸（`deviceBrowse` 的回覆，2026-11-09；`Web/devicefiles.js`）：`conversationId` 一律是 null，網頁以 `requestId` 認領（不交給對話的抽屜）。`path` 是 null 時是虛擬根：那台的磁碟（`name` 是「本機磁碟 (C:)」這種、`path` 是 `C:\\`）與常用資料夾（桌面、文件、下載、使用者資料夾）；否則是那個資料夾的資料夾與檔案，在磁碟根目錄時 `parent` 是 null（回虛擬根）。範圍是整台的固定磁碟與隨身碟，禁止的資料夾（app 的資料、帳戶設定、金鑰與憑證）、網路磁碟、捷徑連結回 `commandResult rejected policy`。FileEntry 的 `url`、`thumbUrl` 一律 null、`root` 是 null。網路芳鄰（2026-11-09）：虛擬根另有對應的網路磁碟機與「網路」（`path` 是 `\\`）；`\\` 列網路上的電腦（`\\電腦`）、`\\電腦` 列它的分享（`parent` 是 `\\`）、分享以下同一般資料夾；只收區域網路的電腦、不收那台自己，被拒、找不到與逾時以 `error` 說明（`entries` 是空的；`path`、`parent` 照樣帶，網頁照樣切到那一層、錯誤寫在清單下方）。網頁的路徑列從「網路」開始（`devicefilesrules.crumbs`），「網路」與電腦這兩層不能放檔案（`canUploadHere`）。

### `fileSearch`
回應 `fileSearch`（輸入框 `@` 檔案提示）。
```jsonc
{
  "type": "fileSearch",
  "requestId": "r13",
  "conversationId": "c1",
  "query": "app",
  "results": [ { "relativePath": "src\\Web\\app.js", "fullPath": "C:\\repo\\proj\\src\\Web\\app.js", "kind": "file" } ]  // 最多 30 筆，已依相關度排序
}
```
〔遠端〕網頁（WP4.6 合併前審查 P2）：瀏覽器版以 `commandResult` 追蹤 `fileSearch`。伺服器草稿不送（只收設定類的指令，一定被拒），提示框說明「送出第一則訊息後才能搜尋檔案」；沒送出、被拒（政策、電腦離線…）、結果不明時分別說明「目前沒有連線，連上之後才能搜尋檔案」「現在不能搜尋這台電腦上的檔案」「沒有收到那台電腦的回覆，請再輸入一次」，不再停在「搜尋檔案中…」。提示框顯示這些說明時 Enter 關掉提示並照常送出（桌面的「搜尋中按 Enter 等結果、找不到時保留原文」不變）。

### `conversationSearch`
回應 `searchConversations`（側欄的對話搜尋，2026-10-23；〔遠端〕瀏覽器版由伺服器搜，同日起）：只回給發出的視窗（瀏覽器版是發出的連線）。
```jsonc
{
  "type": "conversationSearch",
  "requestId": "cs3",                   // 原樣送回；網頁只採用最後一次請求的回覆
  "query": "部署 失敗",                  // 原樣送回
  "hits": [ { "conversationId": "c1", "snippet": "…上次 部署 到 Hub 主機時 失敗 的原因是…" | null } ]
}
```
- 範圍是對話清單列的那些（同 `conversations`：本機最多 1000 個、複本最多 1000 個，不含草稿），`hits` 依清單順序。
- 查詢以空白分成關鍵字（整串先截到 200 字、不分大小寫去掉重複、最多 8 個），**每個關鍵字都出現在標題或內容**才算符合（不分大小寫）；沒有關鍵字時 `hits` 是空陣列。規則在 Protocol 的 `ConversationSearchRules`，網頁的標題比對（`searchrules.js`）用同樣的切法。
- 內容只看使用者訊息、代理回覆與計畫確認的計畫內容（`Item.kind` 是 `User`、`Assistant`、`PlanApproval`）；工具輸出、思考與提示不算。複本照它在這台的項目（政策不是 full 時沒有項目，只比標題）。
- `snippet`：依對話順序第一則含任一關鍵字的內容，從最早的符合處截一行（空白與換行併成一個空格，前面最多 12 字、從符合處起最多 60 字，有截掉的那端加「…」）；只有標題符合時是 null。不是 Markdown、不是 HTML，網頁以文字節點顯示，符合的關鍵字以 `<mark>` 標示。
- 宿主只讀：載入過的對話與複本用記憶體裡的項目，其他的在背景讀 `items.json`（依修改時間與大小快取，沒變不重讀；讀不到或損毀的這次只比標題，不改名、不提示），不會因此載入對話。同一個視窗送新的 `searchConversations` 時取消上一個（被取消的不回覆）。
- 〔遠端〕瀏覽器版（伺服器，`BrowserConversationSearch`）：範圍是瀏覽器的對話清單（`conversations` 的那些：沒刪除的、最多 1000 個，所有電腦的），規則與片段同上；內容是伺服器物化的項目（沒移除、`kind` 是上面三種）的原文：blob 換回後跑第二道權杖 DLP（同 `copyItem`），payload 解不開、blob 讀不到或命中 DLP 的那一則不算；政策不是 full 的對話伺服器上沒有項目，只比標題。各對話的原文依 `conversations.last_seq` 快取在伺服器記憶體（項目新增、修改、移除都會讓它變大，沒變不重讀），不寫資料庫、不稽核，記錄不寫查詢與內容。`Cmd` 只登記就回（`requestId` 要是 1～64 字的字串、`query` 要是字串，否則 `invalid`），比對在背景、所有連線的搜尋依序執行；同一條連線的新搜尋取消上一個。`conversationSearch` 以已送過 `init` 的連線入列、合併鍵是 type（排著的只留最新的一則），斷線、溢出或重送 `init` 時可能遺失：網頁送出後 20 秒等不到就只比對標題並說明「沒有收到伺服器的回覆」，每則 `init` 之後搜尋框有字就重搜一次；沒有連線時傳輸層不送、不提示（`AUTO_COMMANDS`），網頁說明「目前沒有連線」。

### `gitStatus`
對話工作資料夾的 git 狀態（2026-10-23；只有桌面，瀏覽器版與其他電腦的副本不送）：網頁在標頭的資料夾晶片旁顯示 git 晶片（分支＋未提交、未推送、未拉取、衝突、進行中的操作、還沒合併進預設分支等徽章），點了看明細或「重新檢查」（`gitRefresh`）。只送給開著這個對話的視窗。
```jsonc
{ "type": "gitStatus", "conversationId": "c1", "git": GitStatusView | null }   // null：不是 git repo、還沒讀到、找不到 git，網頁藏起晶片
```
- 時機：`conversation` 之後立刻送快取的（還沒讀過是 null），讀完再送；換了工作資料夾先送新資料夾快取的（沒有就是 null）再重讀；這一輪結束、`gitRefresh` 之後重讀。開分頁、切到前景（`visible`）時超過 5 秒沒讀就重讀，畫面上看得到的對話每 30 秒重讀一次（不是 repo 的每 2 分鐘）。讀失敗（逾時、git 出錯）時留著上次的、2 分鐘內只有 `gitRefresh` 才再讀。同一個資料夾的對話共用一份。
- 只讀：宿主以 `GIT_OPTIONAL_LOCKS=0`、`-c core.fsmonitor=false` 執行 `rev-parse`、`status --porcelain=v2 --branch --show-stash`、`for-each-ref`、`rev-list`，不寫索引、不取鎖、**不 fetch**；遠端只以 `ls-remote` 問（不更新任何參照、不跳登入提示），讀完本機之後最多每 5 分鐘問一次、`gitRefresh` 一定問。UNC 路徑與網路磁碟不讀。

### `launchStatus`
執行列（2026-10-23；只有桌面，瀏覽器版與其他電腦的副本不送）：對話的工作資料夾裡有可以執行的視窗程式專案時，網頁在標題列第二行 git 晶片後面顯示啟動（建置並執行）、暫停／繼續、停止、重新建置並執行，與選目標的晶片、狀態、「輸出」鈕（窗格窄到放不下或標題列擠回一行時換到下一行），輸出面板在輸入區上方展開（`Web/launchbar.js`，純規則 `Web/launchrules.js`）。只送給開著這個對話的視窗。
```jsonc
{ "type": "launchStatus", "conversationId": "c1", "launch": LaunchView | null }   // null：還沒偵測完、沒有可以執行的專案、換了資料夾，網頁藏起執行列
```
- 偵測（宿主 `Launch/LaunchScanner.cs`、`Launch/LaunchRules.cs`，只讀）：從工作資料夾往下 3 層、最多 2000 個資料夾，讀 `.csproj`／`.fsproj`／`.vbproj` 與 `package.json`（各最多 1 MB）；不跟重新解析點，跳過以點開頭、隱藏、系統資料夾與 `bin`、`obj`、`node_modules`、`packages`、`dist`、`out`、`build`、`target` 等。認得的：SDK 式 .NET 專案的 `OutputType` 是 `WinExe`（或 `Exe` 加上 `UseWindowsForms`／`UseWPF`；只看沒有 `Condition` 的屬性，測試專案與 MAUI 不列）、`package.json` 的 dependencies 或 devDependencies 有 `electron` 而且有 `start`（其次 `dev`）指令碼。最多 20 個，淺的在前。UNC 路徑與網路磁碟不讀。
- 時機：`conversation` 之後立刻送快取的（還沒偵測過是 null），偵測完再送；開分頁時超過 30 秒、這一輪結束時超過 3 秒、畫面上看得到的每 60 秒重新偵測；換了工作資料夾與 `launchRescan` 一定偵測。偵測結果沒變時不重送。同一個資料夾的對話共用一份；執行狀態以專案檔為單位，不同資料夾偵測到同一個專案時一起顯示。狀態改變（開始、建置完、暫停、結束）時送；有新的輸出時最多每 300 毫秒送一次（`logSeq` 變了）。
- 執行（宿主 `Hub/AgentHub.Launch.cs`、`Launch/LaunchProcess.cs`）：.NET 先 `dotnet build <專案> -c <組態> -nologo -tl:off -nodeReuse:false`（多目標的專案加 `-f`，有 Windows 的優先），成功再 `dotnet run --project <專案> -c <組態> --no-build`（照專案的 launchSettings.json，工作資料夾是專案資料夾）；Electron 是 `cmd /d /s /c npm run start`（或 `dev`）。程序以自己的工作物件包住：停止＝結束整個程序樹；暫停＝暫停每個程序（程式的視窗會「沒有回應」，繼續後恢復）；執行的程式在主程序結束時結束剩下的子程序。
- **執行模式**（2026-10-25，`LaunchView.mode`，每個專案各自記得、沒選過是 Debug）：**Debug**＝組態 `Debug`，執行的程式是 app 的子程序（輸出經管線、工作物件設 KILL_ON_JOB_CLOSE）——app 關閉、當掉、自我更新重啟時一起結束，不記進 `runs.json`；有 Debug 模式執行中（或暫停中）的程式時 app 自我更新不自動重啟（`AppUpdateView.blocker` 說明原因；按「立即重啟」照樣開始，程式被結束）。**Release**＝組態 `Release`，**執行的程式和 app 脫鉤**（2026-10-23）：app 結束、當掉、自我更新重啟都不影響它，輸出寫進宿主資料根目錄的 `launch\` 輸出檔；開著的記在 `launch\runs.json`，app 下次啟動時接回——照樣是 `running`／`paused`、`since` 不變、`runMode` 是 `release`，輸出換一個 `epoch`、第一行是「（Clode Myriagent 重新啟動前開的程式，已接回）」，接著是輸出檔從頭的內容；之後停止、重新建置並執行時先結束它、等它真的結束才建置。模式在開始的那一刻定下來：執行中換模式只改 `mode`，下一次啟動或重新建置並執行才套用（`runMode` 是這一次的）。建置是 app 的子程序：app 結束時結束建置（之後也不會執行），所以建置中（與停止中）app 自我更新也不自動重啟。
- **記得的選擇**（`launch\prefs.json`，2026-10-25）：每個工作資料夾選的目標（`launchSelect`、`launchAction` 選過的才記；偵測時自動選的第一個不記）與每個目標的執行模式（`launchSelect` 帶 `mode`），最近用的在前、各最多 200 筆；app 重新啟動後照舊（記的目標這次沒偵測到時選第一個、記的不改）。

### `launchLog`
`launchLog` 指令（第二節）的回覆：這個目標的輸出（建置與執行的 stdout、stderr，每行去掉 ANSI 色碼與控制字元、最多 2000 字；先試 UTF-8，不是的照主控台字碼頁解）。宿主每個目標只留最後 2000 行，每次啟動清空、`epoch` 加一。
```jsonc
{ "type": "launchLog", "conversationId": "c1", "targetId": "t1a2b3c4d5e6", "epoch": 3, "seq": 120, "reset": false, "lines": ["…"] }
```
- `reset: true`：`lines` 是緩衝區的全部，網頁換掉整份；`false`：接在網頁已有的後面（`seq` 是最後一行的序號）。要的 `epoch` 不是目前的、或 `after` 之後的行已經丟掉時是 `reset`。
- 第一行是執行的指令（「> dotnet build …」，只給人看）；開不起來時接著錯誤訊息。

### `activity`
只有桌面：輸入框下方那一行最前面的燈號（運行、資料、網路、硬碟）與 token 數後面的 CPU 波形（網頁 `Web/activityrules.js`、`composer.js` 的 `applyActivity`），只連動真的計數器。宿主（`Hub/AgentHub.Activity.cs`，規則在 `ActivityRules`）在這台的本機對話有視窗看得到（`visible`）時取樣：有對話執行中或有背景工作時每 250ms、否則每 500ms（只量網路）；上一次做完才排下一次。只送給看得到那些對話的視窗，副本與遠端的檢視不送。
```jsonc
{ "type": "activity", "ms": 250, "conversations": [ { "id": "c1", "data": 5000, "disk": 8192, "cpu": 3.1, "mem": 734003200 } ], "net": { "rx": 4096, "tx": 1024 } | null }
```
- `ms`：這次取樣涵蓋的毫秒數（網頁換成每秒）。所有數字都是這段時間的位元組增量。
- `conversations`：這個視窗看得到的對話裡有動靜的（`data` 或 `disk` 大於 0）；沒列出的這次沒有動靜。`data`＝app 與這個對話的 CLI 之間管線的位元組（stdout、stderr 讀到的加上 stdin 寫入的，`AgentProcess.PipeBytes`）；`disk`＝CLI 程序樹（CLI 與它執行的工具；每次走 Toolhelp32 程序表找子孫）每個程序讀寫的位元組（GetProcessIoCounters）增量加總，扣掉同一段時間的 `data`（程序的 I/O 計數器含管線；CLI 與工具之間的管線會算進來，網路 socket 不算）。只量執行中或有背景工作的對話的程序樹；第一次量、換了程序時只取基準不送。
  - `mem`（2026-10-26 起；選填）：和 `cpu` 同一組程序（CLI 程序樹加上脫離 CLI 的那些，每個程序只算一次）的記憶體，位元組：各程序的私人工作集（`K32GetProcessMemoryInfo` 的 `PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize`，同工作管理員的「記憶體」欄；舊的 Windows 讀不到時改用私人位元組）加總。不是增量，第一次量（`cpu` 還沒有）就有；一個程序都沒讀到時沒有這個欄位。網頁寫在 CPU 百分比後面（「MEM 700 MB」，1 GB 以下整數 MB、10 GB 以下一位小數），跟著 CPU 一起出現與收起。
  - `cpu`（2026-10-26 起；選填）：同一個程序樹，加上這個對話開出、脫離 CLI 的程序（帶這個對話的標記 `MYRIAGENT_AGENT_CONVERSATION`、不在任何 CLI 底下的程序樹：以 nohup、Start-Process 開的背景程式，Git Bash 經 `timeout`、`bash -c` 開而父子關係斷掉的工具，主程序結束後還在跑的子代理；判斷同背景程式，`BackgroundProgramRules.Classify`，多個對話共用的建置常駐程序不算），這段時間用掉的處理器時間（GetProcessTimes 的核心＋使用者時間）佔整台電腦的百分比（除以經過時間乘邏輯處理器數，同工作管理員「處理程序」頁），0～100、一位小數；`disk` 照舊只看 CLI 程序樹。量了程序樹的對話（執行中、有 CLI 的背景工作，或有背景程式還在跑——這一輪結束了也照量；CLI 程序沒在跑時只量那些程式）每次都放，0 也放（網頁的波形要畫平的），所以那時對話即使沒有資料流與硬碟的動靜也會列出；第一次量、換了程序時沒有這個欄位。兩次取樣之間才被看到的程序（程序表 3 秒才重列）依它在這段時間活著的比例攤（`ActivityRules.TreeCpuDelta`），不把它啟動以來的 CPU 整個算進這一格。網頁在 token 數（與輸出速度）後面畫成波形（使用者選的「極光面積、依負載變色」：平滑曲線下的填色面積往底部漸隱、頂端一條亮線，顏色依每一格的負載由青到紅；最近 5.5 秒；高度依看得到的最高值自動縮放，下限 5%）並寫出最新的百分比（顏色同那一格），1.5 秒沒收到就收起。canvas 只在收到一格時重畫，兩格之間由合成器平移（canvas 的 transform 動畫，長度＝這則的 `ms`），下一格早到或晚到都從目前的位置接著移；視窗沒有焦點或要求減少動態時不平移、最多每秒重畫一次。
- `net`：整台電腦硬體網卡（不含輕量篩選器、虛擬與回送介面，GetIfTable2）收（`rx`）與送（`tx`）的增量，收送都是 0 時是 null。讀不到網卡時一律 null。
- 三者都沒有動靜、也沒有量程序樹的對話（`cpu`、`mem` 都沒有）時不送。網頁每次收到就讓有動靜的燈亮 160ms（減少動態或關掉動態效果時 900ms），滑過時寫出資料來源與最近一次的速率；運行燈不靠這個訊息，跟著標頭的執行中（副本照 `replica.running`）。瀏覽器版與副本只顯示運行燈。

### `health`
儀表板的健康度（2026-10-23；只有桌面的主視窗，瀏覽器版、彈出視窗與其他電腦不送）：每個本機帳戶每次對話都會載入的東西有沒有太肥（全域指示、Claude 的自動記憶索引、skill 清單、MCP 伺服器、外掛、設定檔），以及電腦層級的 CLAUDE.md／AGENTS.md／GEMINI.md 要不要合併與同步。網頁在帳戶卡顯示「健康度」一行（點了開明細）、在帳戶區之後顯示「電腦層級的指示檔」。
```jsonc
{ "type": "health", "health": HealthView }   // 整份取代
```
- 時機：網頁切到儀表板（含一開始就在儀表板）時送 `healthCheck`，宿主開始分析時先送一則 `running: true`（內容是上次的結果），分析完再送。不帶 `force` 時，30 秒內分析過而且帳戶的組成（id、資料夾、精簡模式、共用設定、名稱）沒變就不重做，只對送指令的視窗重送目前的結果。分析中又收到帶 `force` 的就分析完再做一次。
- 只讀：宿主在背景執行緒讀各帳戶的設定資料夾（`Health/HealthScanner.cs`），不寫任何檔；一個檔讀不到就當不存在。門檻與說明由宿主算好（`Health/HealthRules.cs`、`Health/InstructionCompareRules.cs`），網頁只顯示（`Web/health.js`、純規則 `Web/healthrules.js`）。token 數一律是粗估（CJK 一字一個、其他四個字元一個）。

CLI 安裝／版本／更新狀態改變時送；〔熱更新〕任何對話的換版狀態（`restartWaiting` 等彙總）改變時也送（與其他更新一起每 50ms 合併）。整份取代。
```jsonc
{ "type": "cli", "cli": { "Claude": CliView, "Codex": CliView, "Antigravity": CliView, "Grok": CliView }, "devicesCli": { … } | null }   // devicesCli〔同步〕同 init
```
〔同步〕瀏覽器模式的 `cli` 一律是 `{}`（第 3 階段沒有選定電腦）；各台的 CLI 看 `devicesCli`。

### `schedules`
排程頁（2026-10-26；只有桌面的主視窗與額外視窗，瀏覽器版、彈出視窗與同步鏡像不送；設計 `docs/plans/2026-10-26-schedule-manager.md`）：這台電腦的 Windows 排程（工作排程器 `\Microsoft\` 以外的工作）。頂端切換鈕的「排程」和對話、儀表板同位階（`Layout.view` 是 `"schedules"`）；init 的 `schedules` 是 null（宿主沒掛讀取器）時不顯示這個鈕。
```jsonc
{ "type": "schedules", "schedules": ScheduleView }   // 整份取代

ScheduleView = {
  "machine": "DEMO-PC-3",              // 這台的名稱
  "loaded": true,                       // 讀過至少一次（之前 entries 是空的，網頁顯示「正在讀取」）
  "scanning": false,                    // 讀取中（「重新掃描」停用）
  "scannedAt": 1761456000000 | null,    // 上次讀完的時間
  "error": null | "…",                  // 讀不到工作排程器的原因
  "failing": 1,                         // 列入管理（origin 是 agent、adopted）的排程裡最近一次失敗的數量（頂端「排程」鈕的數字）
  "autoDiagnose": true,                 // 設定 scheduleAutoDiagnose
  "accountId": "acc-…" | null,          // 排程頁上次用來開對話的帳戶（選帳戶的選單排第一）
  "entries": [ ScheduleEntry ],         // 照路徑排序；網頁自己篩選與排序
  "timed": [ TimedView ],               // Myriagent 的定時訊息（S4，2026-10-26）：下一次最早的在前
  "devices": [ { "deviceId": "d_…", "updatedAt": ms, "snapshot": ScheduleSnapshot } ]   // 其他電腦最後回報的（S3，sync-protocol 14.12）；未配對、沒有時是空陣列
}
ScheduleSnapshot = { "machine", "scannedAt", "error", "failing", "otherCount", "entries": [ScheduleEntry 的摘要：沒有 xml、author、description，action 遮蔽過], "timed": [TimedView 的摘要：text 截 300 字遮蔽過] }
TimedView = {
  "id": "tm_1a2b3c4d", "label": "整理昨天的訂單異常", "text": "…",   // 名稱（沒給時是文字的第一行）、給代理的文字
  "when": "平日 09:00", "cron": "0 9 * * 1-5" | null, "at": ms | null,   // 時間的中文摘要；重複的 cron（本機時間）或一次性的時間
  "enabled": true, "done": false,       // 暫停是 enabled:false；一次性的送過是 done
  "newConversation": false,             // 每次開新對話（帳戶、模型、權限模式、資料夾照建立時記的）
  "conversationId": "…", "conversationTitle": "…" | null, "conversationExists": true,   // 送進的對話（每次開新對話時是建立時的來源）
  "provider": "Claude" | null, "folder": "C:\\repo\\…" | null,
  "nextAt": ms | null, "upcoming": [ms, …],   // 下一次、接下來 24 小時的
  "createdAt": ms, "createdBy": { "conversationId": "…", "title": "…", "provider": "Claude", "exists": true } | null,   // 代理以 myriagent-schedule 建立的；排程頁新增的是 null
  "last": { "level": "ok" | "info" | "warn" | "fail", "text": "完成", "at": ms } | null,
  "runs": [ { "at": ms, "scheduledAt": ms, "late": false, "conversationId": … | null, "exists": true, "level": "ok", "text": "完成" } ]   // 新的在前，最多 30 筆；late＝錯過後補送
}
ScheduleEntry = {
  "path": "\\TkNotesSync", "name": "TkNotesSync", "folder": "\\",   // 工作的路徑、名稱、所在資料夾
  "state": "ready" | "running" | "disabled" | "queued" | "error",   // error＝定義讀不到（看 error）
  "enabled": true,
  "busy": false,                        // 快速操作進行中（按鈕停用）
  "origin": "agent" | "adopted" | "suspected" | "other" | "ignored",   // 代理建立（有證據）／使用者納入管理／疑似代理建立／系統或廠商／使用者不再列出
  "managed": true,                      // origin 是 agent 或 adopted：失敗時通知與自動診斷
  "trigger": "每天 02:30" | null,        // 觸發條件的中文摘要（ScheduleTriggerRules）
  "action": "C:\\Python314\\pythonw.exe \"…\"" | null,   // 動作（指令＋參數；多個以「；」接）
  "workFolder": "C:\\repo\\…" | null,  // 工作資料夾，沒有就是腳本所在的資料夾、再來是 git repo（交給代理時對話的資料夾）
  "repoRoot": "C:\\repo\\…" | null,    // 動作用到的路徑所在的 git repo
  "runAs": "…，只在使用者登入時執行" | null, "conditions": ["使用電池時不啟動", …], "author": … | null, "description": … | null, "created": ms | null,
  "lastRun": ms | null,                 // 工作排程器記的上次開始時間
  "last": { "code": 1, "level": "ok" | "info" | "warn" | "fail", "text": "結束碼 1" } | null,   // 最近一次的結果（Myriagent 的紀錄，沒有時用工作排程器的；ScheduleResultRules）
  "nextRun": ms | null, "missed": 0,    // 下次執行（停用時是 null）、錯過的次數
  "streak": 3,                          // 連續失敗幾次
  "runs": [ { "at": ms, "code": 1, "level": "fail", "text": "結束碼 1", "gap": false } ],   // Myriagent 觀察到的執行（新的在前，最多 30 筆）；gap＝和前一筆之間還有預定的執行沒觀察到
  "upcoming": [ms, …],                  // 接下來 24 小時預定的執行（只展開時間觸發的，每個排程最多 200 次）
  "provenance": { "source": "myriagent" | "transcript", "verb": "created" | "changed", "at": ms, "conversationId": … | null, "title": … | null, "provider": "Claude" | null, "exists": true,
                  "importable": false, "cwd": … | null } | null,   // transcript＝在終端機或 IDE 的工作階段裡建立（S2，從 CLI 的對話紀錄找到的）；importable＝可以匯入成 Myriagent 對話（scheduleAction 的 importOrigin）
  "management": { "conversationId": "…", "title": "…", "provider": "Claude", "running": false, "isDraft": false } | null,   // 這個排程的管理對話（交給代理時沿用）
  "changes": [ { "at": ms, "kind": "created" | "changed" | "deleted" | "enabled" | "disabled", "text": "修改定義", "conversationId": … | null, "title": … | null, "provider": … | null, "exists": true } ],   // 新的在前，最多 10 筆
  "autoDiagnose": true,                 // 這個排程沒有個別關掉自動診斷
  "diagnoseBlock": null | "24 小時內已經自動診斷過",   // 上次沒有自動診斷的原因
  "lastDiagnosis": { "at": ms, "conversationId": "…", "exists": true } | null,
  "error": null | "…", "xml": "<?xml …"  // 讀不到的原因、完整定義（只在這台，不上伺服器）
}
```
- 時機：網頁切到排程頁送 `scheduleWatch {on:true}`、離開送 `{on:false}`。有視窗開著排程頁時宿主每 30 秒讀一次，沒有時每 5 分鐘（還是要知道排程有沒有失敗）；任何對話的工具指令碰到排程（`schtasks /create`、`Register-ScheduledTask` 等，ScheduleOriginRules），那一輪結束就立刻讀；`scheduleRefresh`、快速操作之後也讀。內容沒變不重送（讀取中、快速操作中的狀態改變照送）。
- 讀取只讀（工作排程器的 COM，一般權限）；比對上一次記下建立、修改、刪除、啟用、停用，這段期間有對話的工具指令碰到排程就記成那個對話做的、排程算 `agent`。紀錄在 `<資料根目錄>\schedules\`（`state.json`、`runs.jsonl`）。
- 列入管理的排程新的一串失敗開始時（上一筆不是失敗、開始時間在 12 小時內）：語音通知開著就念一句、系統通知開著就送，設定 `scheduleAutoDiagnose` 開著時自動在管理對話請代理診斷（只診斷不修改；能規劃的代理用規劃模式；同一個排程 24 小時一次、每天 10 次、每週額度剩不到 10% 不做；ScheduleRunRules）。
- 網頁 `Web/schedules.js`、純規則 `Web/schedulerules.js`（`tools/ui-probe` 的 `schedulerules.test.mjs`、`probe.mjs --schedules`）。
- **其他電腦與瀏覽器版**（S3，sync-protocol 14.12）：桌面的 `devices` 是其他電腦最後回報的摘要；瀏覽器版的 `init.schedules` 與 `schedules` 訊息由伺服器送（沒有這台：`entries`、`timed` 空的、`failing` 是 0，全部在 `devices`），沒有來源時是 null（不顯示排程頁）。頂端「排程」鈕的數字是這台的 `failing` 加上各台摘要的 `failing`。網頁以 `deviceId::路徑` 區分選取；其他電腦的列只有立即執行、停止、啟用、停用與交給代理（`scheduleAction`／`scheduleAgent` 帶 `deviceId`，由那台執行；桌面照 `liverules.PEER_COMMANDS` 帶 commandId），沒有不再列出、納入管理、開啟資料夾、XML、匯入；定時訊息只看。瀏覽器版不送 `scheduleWatch`、`scheduleRefresh`，沒有新增排程與新增定時訊息。
- **回溯**（S2，`Hub/AgentHub.ScheduleTranscripts.cs`、`Schedules/TranscriptOriginScanner.cs`、Protocol 的 `TranscriptOriginRules`）：還沒有出處的排程，從各帳戶的 CLI 對話紀錄（Claude 的 projects、Codex 的 sessions、Antigravity 的 brain、Grok 的 sessions）找建立它的指令——只看工具的指令參數，不看輸出與訊息文字。工作階段 id 對得上 Myriagent 對話的連到對話，否則是 `source: "transcript"`（標題是「Claude 工作階段・資料夾名稱」），匯入之後自動改連到匯入的對話。增量掃描（記在 `schedules\scan-index.json`），啟動 1 分鐘後第一次、之後每 30 分鐘，`scheduleRefresh` 也補一次；背景低優先的執行緒、只讀。
- **Myriagent 的定時訊息**（`timed`；`Hub/AgentHub.TimedMessages.cs`、Protocol 的 `Schedules/TimedMessageRules.cs`）：在指定的時間把文字送進某個對話（或每次開新對話），取代 Myriagent 停用的 Claude `CronCreate`（程序關掉就失效）。定義在 `<資料根目錄>\schedules\timed.json`；只在 app 開著時送（計時器排到最近一則，維護計時器每 30 秒也補看；app 剛啟動先等 20 秒）。到點時照一般的送出（對話執行中就排進佇列，不插進這一輪）；畫面上那一則是「（定時訊息：名稱）＋文字」，代理收到的前面多一行說明（名稱、時間、原定時間、是不是補送）。錯過的重複訊息只補一次（記成錯過的最後一次），一次性的錯過超過 12 小時不補（記成 missed、送 `toast`）。送出的那一輪結束時記下結果（completed／failed／interrupted）。代理以 `myriagent-schedule`（和 `myriagent-remote` 同一條本機管道與對話權杖，`op:"timed"`，sync-protocol 不涉及）建立，送進呼叫的對話；只能暫停、繼續、刪除自己建立或送進自己的。

### `usageSegments`
儀表板的「每週額度每 5% 的 token」（2026-10-25；只有桌面的主視窗，瀏覽器版、彈出視窗與同步鏡像不送）：用來比較不同帳戶、不同模型「每 1% 的每週額度能用多少 token」是不是同一個基準。
```jsonc
{ "type": "usageSegments", "usageSegments": UsageSegmentsView }   // 整份取代
```
- 時機：回網頁的 `usageSegments` 指令（切到儀表板、在儀表板時每分鐘）；之後任何帳戶結了新的一筆，宿主主動送給要過的視窗。
- 規則（`Usage/UsageSegmentRules.cs`、`Hub/AgentHub.UsageSegments.cs`）：AccountManager 每存下一份用量快照（定期查詢、對話中的 rate_limit_event／rateLimits/updated、同步來而取代目前值的共享用量），宿主就看那個帳戶的每週額度窗（Claude 的每週與每週・模型、Codex 10080 分鐘的、Antigravity 與 Grok 標成每週的）有沒有跨過下一個 5% 的邊界；跨過就把這一段（從跨過上一個邊界起）各模型用掉的 token 結成一筆，附加到 `usage-segments\records.jsonl`（一行一筆、camelCase、只附加；外部分析直接讀它）。一次跳過好幾個邊界結成一筆（終點是最後跨過的）。token 是會話的每一筆 `AgentTokenUsageEvent`（依模型，含 Claude 的快取寫入），加到這個帳戶正在記的每一段。換週（重置時間往後超過一天、或使用率掉了超過 3 個百分點）時進行中的那段丟掉。開始追蹤時已在一段中間的那筆 `complete: false`，不算進彙總。
- 只算這台 app 經手的對話：同一個帳戶在其他電腦、終端機的 CLI、網頁版用掉的額度也會讓使用率上升，那幾段每 5% 的 token 會偏少（紀錄本身看不出來）。

### `steerCancelled` / `rewound`
只有桌面（2026-10-25）：`{ "type": "steerCancelled" | "rewound", "conversationId": "c1", "text": "那則訊息的原文" }`，只給送 `cancelSteer`／`rewind` 的視窗。網頁照「放回輸入框」把文字放回去（`composer.putBack`：輸入框是空的就放進去，已經有字就插在游標處）；項目的移除另外以 `removeItem` 送給所有視窗。

### `rewindPreview`
只有桌面（2026-10-25）：回應 `rewindPreview` 指令，只給送指令的視窗。
```jsonc
{ "type": "rewindPreview", "conversationId": "c1", "requestId": "rewind4", "itemId": "i42",
  "error": null | "這一輪還在執行：先按停止再回溯",          // 不是 null 時其他欄位都沒有
  "laterItems": 12, "laterMessages": 3,                        // 這則與之後的項目數、使用者訊息數（含這則）
  "conversation": true, "conversationError": null | "…",       // 對話回不回得去（逐字稿裡找得到分支點）
  "files": ["a.txt", "src\\b.cs"], "fileCount": 2, "insertions": 4, "deletions": 1,   // rewind_files 的 dry run（工作資料夾裡的顯示相對路徑，最多 100 個）
  "filesError": null | "這則訊息沒有檔案還原點（app 開始記還原點之前送的訊息）" }
```
- 網頁（`Web/rewind.js`、純規則 `claudeoptrules.rewindPlan`）在使用者訊息滑過時的「回溯」（只有這台的 Claude 對話、不在執行中：對話欄的 `data-rewind`）開對話框：「回溯對話」「還原檔案」兩個勾選（做得到的預設勾；沒有要還原的檔案時不勾），說明以指令改的檔案不會還原，按「回溯」送 `rewind`。

### `taskDetail`
只有桌面（2026-10-25；取代同一天的 `taskOutput`）：回應 `taskDetail` 指令，只給送指令的視窗。
```jsonc
{ "type": "taskDetail", "conversationId": "c1", "requestId": "taskdetail3", "key": "t3f9a0c12b7de",
  "tool": "執行指令", "title": "ping -n 30 127.0.0.1",
  "explain": "等待一段時間，或反覆檢查某個條件、成立了才結束（輪詢），結束時代理會接著處理。",
  "agentNote": "Wait for the server" | null, "content": "ping -n 30 127.0.0.1" | null, "contentLabel": "指令",
  "output": "…" | null, "totalBytes": 272 | null, "truncated": false | null, "outputNote": null | "Grok 看不到背景工作執行中的輸出。",
  "error": null | "這個背景工作已經結束了" }
```
- `error` 不是 null＝這個背景工作已經結束了（不是目前回報的、或程序已結束），其他欄位不帶；網頁停止再問、留著上一次的內容。
- `explain` 是宿主依工具卡寫的白話說明（`BackgroundTaskDetailRules`：子代理寫它在處理什麼；指令依樣子分成開發伺服器、監看、測試、建置、安裝、部署、等待／輪詢、網路、腳本，都不像時是「在背景執行指令」；Claude 的 Monitor、Antigravity 的排程另有說法），`agentNote` 是代理開始它時自己寫的說明（Claude 指令的 description，原文照登），`content` 是指令全文或交代給子代理的內容（`contentLabel` 是它的標題）。
- 輸出：Claude 的背景指令與 Monitor（`taskId`）以 `get_task_output` 問活著的程序（最多 20 萬字、超過只留尾端並標 `truncated`，問不到時原因放 `outputNote`）；Codex 用工具卡收到的輸出（一輪結束後還在跑的指令的 `outputDelta` 也接在卡片上，沒有 `totalBytes`）；Grok 讀 `task_backgrounded` 通知帶的本機 `output_file` 尾端（同樣最多 20 萬字；磁碟代號開頭才讀，UNC 與相對路徑不讀，2026-11-01）；其他沒有 `output`，`outputNote` 寫看不到的原因。網頁（`Web/taskdetail.js`、純規則 `liverules.taskDetailView`）開著對話框時每 3 秒再問一次。

### `promptHistory`
只有桌面（2026-10-25 使用者要求）：回應 `promptHistory` 指令，只給送指令的視窗。對話上方「最近的指令」那一行行尾的「歷史指令」鈕展開的清單。
```jsonc
{ "type": "promptHistory", "conversationId": "c1", "requestId": "ph3",
  "prompts": [ { "id": "i7", "seq": 7, "time": 1791580000000, "text": "把報表改成月結", "attachments": 1 } ] }   // 最新的在前，最多 10 則
```
- 來源是對話的 items（`AgentHubRules.RecentPrompts`），所以 app 重開後照樣在：由新到舊的使用者訊息（含從 claude.ai、其他電腦送來的；`/clear` 之前的也列——是送過的指令，不是代理還看得到的前文），文字空白（只有附件）、超過 10 萬字、語音訊息（「語音訊息（0:12）」）與內容和較新的一則相同的略過。`text` 是畫面上那則的完整純文字（秘密答案已遮蔽，不像 `lastPrompt` 截在 2000 字）。項目還沒載入時是空的。
- 網頁（`conversation.js`，純規則 `liverules.promptHistory`）按了鈕才問、回來才開清單；每一則一行（太長的「…」）、說明是時間與附件數，點了就把那則的文字換進輸入框（可以 Ctrl+Z 還原原本的文字，附件不帶）。〔遠端〕瀏覽器版不問（`desktop_only`），從已載入的項目找。

### `mcp`
只有桌面（2026-10-24 使用者要求「以帳戶為單位放在儀表板」）：各 Claude 帳戶的 MCP 伺服器。回應 `mcpCheck` 與 `mcpAction`，狀態改變時送給所有主視窗（不給彈出視窗與同步檢視、瀏覽器版不送）。
```jsonc
{ "type": "mcp", "mcp": { "accounts": {
  "a1": { "checking": false, "checkedAt": 1791580000000 | null, "error": null, "authorizing": null, "safeMode": false,
          "servers": [ { "name": "github", "status": "connected", "source": "user", "transport": "http", "tools": 12 },
                       { "name": "notion", "status": "needs-auth", "source": "claudeai" },
                       { "name": "broken", "status": "failed", "source": "plugin", "error": "Connection closed" } ] | null } } } }
```
- 只有這台的 Claude 與 Codex 帳戶（`accounts` 以帳戶 id 為鍵）。`servers` 是 null＝還沒查到過；`error` 是上次查不到的原因（之前查到的 `servers` 照留）；`checking`、`authorizing`（正在授權的伺服器名稱）時網頁轉圈、停用按鈕；`safeMode`＝精簡模式不載入 MCP、不查。
- `status` 原樣（`connected`、`failed`、`needs-auth`、`pending`、`disabled`）；`source` 是設定從哪裡來（`user`、`plugin`、`claudeai`、`project`、`local`、`dynamic`〔隔離帳戶共用來源的 MCP 經 `--mcp-config` 帶〕…）；`error` 壓成一行、最多 300 字；`tools` 是工具數（連上才有）；每個帳戶最多 60 個。
- 檢查用的程序在中立的資料夾開，所以只看得到帳戶層的（使用者層、外掛、claude.ai 連接器）；專案資料夾的 `.mcp.json` 只在那個資料夾開的對話載入，這裡不列。
- 網頁（`Web/mcp.js`、純規則 `Web/mcprules.js`）在 Claude 與 Codex 帳戶卡上健康度那一行下面多一行「MCP」（樣式同健康度那一行：已連線數／總數、要授權與連不上的數量；有連不上的是 bad、有要授權的是 warn），點了開明細：每個伺服器的狀態與來源、錯誤原因或工具數，要授權的有「授權…」、連不上與已連線的有「重新連線」（`mcpAction`），底部「重新檢查」（`mcpCheck` 帶 `force`）。

### `settings`
```jsonc
{ "type": "settings", "settings": Settings }
```

### `appUpdate`
app 自我更新的狀態改變時送給所有視窗（檢查、下載進度、已備妥、排空、重新啟動、安裝到這台電腦…），整份取代 `init.appUpdate`。網頁據此顯示頂部列下方的橫幅（主視窗才有）與設定頁的「版本與更新」分頁，不自行推算更新狀態。
```jsonc
{ "type": "appUpdate", "state": AppUpdateView }
```

### `draftSnapshotRequest`
app 更新排空完成、要換版之前，宿主向每個視窗要輸入框裡還沒送出的字（設計 9.3）。網頁立刻回 `draftSnapshot`（同一個 `requestId`）；2 秒內沒回的視窗就略過（不擋更新）。
```jsonc
{ "type": "draftSnapshotRequest", "requestId": "…" }
```

### `sendRejected`
宿主沒有送出這則訊息（工作資料夾不存在、帳戶未登入、npm 正在更新這個 CLI、對話紀錄檔暫時讀不到…）：網頁送出時已清空輸入框，把原文還回去（輸入框目前是空的才還）；附件留在輸入區（以宿主的 `composer` 為準）。對話欄裡同樣文字的「送出中」那則標成沒有送出（見下）。
```jsonc
{ "type": "sendRejected", "conversationId": "c1", "text": "原本的訊息", "reason": "工作資料夾不存在：D:\\gone，請重新選擇" }
```
`reason`（2026-10-23，選用）：沒有送出的原因，和同時送的錯誤 `toast` 同一段文字；只給本機的視窗——遠端（瀏覽器版、其他電腦經伺服器轉來的）的 `sendRejected` 形狀不變、沒有這個欄位，`text` 是空的也不送。本機只附檔、沒打字的被拒時 `text` 是空字串（網頁以它對上只附檔的那則，輸入框不還原）。

**送出中**（2026-10-23，只在網頁，不是新的訊息）：按下送出到宿主回傳這則使用者訊息的 `item` 之間，宿主要檢查工作資料夾、存檔、插進這一輪，遠端還要繞伺服器與那台電腦一圈；網頁先在所有項目之後畫一則虛線框、寫「送出中…」的（`Web/sendrules.js`、`ConvView.addSending`）。收掉（換成正式的或交給佇列）的時機：
- 新出現的 User 項目 `html` 還原成文字後（去掉空白）和它相同，而且 `seq` 比送出當時畫面上最大的大（沒有 `seq` 的不比）——換成正式的那則、不再播進場動畫；整份 `conversation` 重送時也照這個條件比對；
- `composer.queue` 裡有它（文字照宿主的寫法：有附件時加「（附 N 個檔案）」）而且不是 `composer.steering` 那則：排進佇列了，由佇列顯示。

真的沒有送出時**留在對話欄**、標出原因（`sendrules.failureOf`），下方有「重新送出」「放回輸入框」「移除」，標題列閃一下：
- **沒有送出**（紅）：`sendRejected`（原因是 `reason`，沒有時只寫「沒有送出」），或〔遠端〕這則 `send` 的 `commandResult` 是 `rejected`（原因照 `code`）；
- **不確定有沒有送出**（琥珀）：〔遠端〕`commandResult` 是 `unknown`，或本機 90 秒都沒對上（另跳錯誤提示）；之後使用者訊息照樣到了就換成正式的；
- 兩個通知先後都到時，確定的蓋過不確定的、有原因的蓋過沒有原因的（`failureUpdates`）；字照舊依上面的規則還回輸入框，之後再送同樣的文字（按「重新送出」或從輸入框送）就收掉舊的標示；
- 〔遠端〕`commandResult` 是 `done`：寫「已送到那台電腦，等待同步…」，使用者訊息經同步拉回來時換掉，5 分鐘都沒到就安靜收掉（已經確定送出）。

不畫的情況：沒有內容（空白送出＝從佇列接著送）、`/clear`、執行中而且這則會排進佇列（`header.steer` 是 false；其他電腦的對話與瀏覽器版在執行中一律不畫，那台的佇列不標正在插入、會先進佇列）。宿主那邊，送出與插入建立使用者項目之後先把 `item` 與標頭送出（`AgentHub.FlushNow`），再寫 `items.json`。

### `collaborators`〔多人協作〕
```jsonc
{ "type": "collaborators",
  "collaborators": {
    "list": [ { "id": "Identity 的使用者 id", "userName": "ming", "displayName": "小明", "disabled": false,
                "createdAt": 1761811200000, "lastLoginAt": 1761897600000 | null, "online": true } ],
    "shares": { "對話 id": ["使用者 id", …] } } | null }
```
多人協作（2026-10-30，docs/plans/2026-10-30-collaborators.md）：擁有者看的協作人清單（依建立的順序）與每個對話分享給誰，整份取代；`init.collaborators` 是同一份。桌面是伺服器經裝置 hub 送來的 `CollabUpdate`（sync-protocol 14.14），`shares` 的鍵換成這台的對話 id（這台的對話、其他電腦的副本都是資料夾名）；未配對、伺服器太舊時是 null（設定頁的「協作人」顯示要先配對、對話選單沒有「協作人…」）。瀏覽器版由伺服器送給擁有者的連線，協作人的連線沒有這個訊息（`init.collaborators` 是 null）。`online`＝目前有瀏覽器連線。網頁以 `textContent` 顯示名稱與帳號。

### `collabResult`〔多人協作〕
`{ type: "collabResult", requestId, cmd, ok, code?, userId? }`：`collab*` 指令的結果，只給發起的視窗。`code` 是 `invalid`、`user_name_taken`、`not_found`、`too_many`、`password_too_short`、`password_too_long`、`password_invalid`、`unavailable`（伺服器連不上、未配對；桌面另送 toast）；`collabCreate` 成功時帶新的 `userId`。

### `toast`
```jsonc
{ "type": "toast", "text": "已複製", "level": "info" | "error" | "success" }
```

### `peerUploadProgress`〔電腦狀態〕
只有桌面：帶 `deviceId` 的 `peerUpload`（電腦狀態面板的上傳，2026-11-09）上傳到伺服器的進度，只送給送出的視窗、最密每 300 ms 一則；結果照樣是 `peerUpload`。
```jsonc
{ "type": "peerUploadProgress", "uploadId": "01J9Q…", "sent": 10485760, "total": 52428800 }
```

### `xferProgress`〔電腦狀態〕
只有桌面：`peerXferSave`（第二節）的進度與結果，只送給送出的視窗。`state`：`running`（最密每 300 ms 一則）、`done`（`path`、`name` 是實際存成的完整路徑與檔名：同名時自動加「 (2)」）、`failed`（`error` 是給人看的說明）、`cancelled`。
```jsonc
{ "type": "xferProgress", "transferId": "01J9Q…", "state": "running", "done": 8388608, "total": 52428800, "path": null, "name": null, "error": null }
```

### `xferFolder`〔電腦狀態〕
只有桌面：`xferPickFolder` 的結果（這台的資料夾選擇視窗；按取消是 null）。網頁把它記在 localStorage，之後的 `peerXferSave` 帶它當 `folder`。
```jsonc
{ "type": "xferFolder", "requestId": "xf3", "path": "D:\\下載" | null }
```

### `peerUpload`〔桌面遠端操作〕
只有桌面：`peerUpload` 指令（第二節）的結果，只送給送出的視窗。形狀照瀏覽器 E4 的回應，網頁照 `liverules.uploadResult` 解讀，成功之後照瀏覽器版送 `addUploaded`。
```jsonc
{ "type": "peerUpload", "uploadId": "01J9Q…",
  "status": 201,                     // 伺服器的狀態碼；0＝取消、沒有存取權杖或連不上；宿主自己擋下的：503 unavailable（不是可以操作的副本、同一個 id 在上傳）、
                                     //   413 too_large（超過 512 MB、dataUrl 超過 30 MB）、404 not_found（不是本機磁碟上的檔）、400 no_path（沒有路徑也沒有 dataUrl）
  "json": "{\"uploadId\":\"01J9Q…\",\"sha256\":\"…\",\"size\":123}" | null }   // 伺服器回應本文的 JSON 字串（或宿主的 {error}）
```

### `accountLogin`
帳戶登入流程的提示（例如 Codex 已開啟瀏覽器、Claude 已開啟登入視窗；排在其他帳戶的登入後面時是「等待另一個帳戶完成登入…」）。網頁在帳戶卡上顯示進行中狀態；`done:true` 時結束（被 `accountLoginCancel` 取消時訊息是「已取消登入」），結果（成功、失敗、取消、移除帳戶時資料夾沒刪乾淨）由網頁顯示提示，宿主不另外送 `toast`。新增帳戶的「已新增，請完成登入」也由送出 `accountAdd` 的網頁自己提示。
```jsonc
{ "type": "accountLogin", "accountId": "a1", "message": "已開啟瀏覽器，請完成 ChatGPT 登入…", "done": false, "success": null }
```
〔帳戶目錄〕Codex 裝置碼登入（`accountLogin` 指令帶 `method:"deviceCode"`）進行中時多帶 `deviceCode`（本機視窗；〔遠端〕瀏覽器發起的另經 `CommandOutput` 只給發起的連線，見下；`done:true` 的訊息不帶），網頁可以做成大字加複製鈕；沒做也沒關係，`message` 文字裡已經有驗證碼與網址。驗證碼與 `loginId` 不進 AccountView、記錄檔與任何同步 payload，`loginId` 不給網頁：
```jsonc
{ "type": "accountLogin", "accountId": "a3", "message": "請在手機或其他電腦打開 https://auth.openai.com/codex/device，輸入驗證碼 ABCD-EFGH2（15 分鐘內有效）",
  "done": false, "success": null,
  "deviceCode": { "userCode": "ABCD-EFGH2", "verificationUrl": "https://auth.openai.com/codex/device", "expiresAt": 1792000900000 } }   // expiresAt 毫秒：Codex 給的到期與 15 分鐘取較早者
```
網頁（重新）載入後（`ready`），進行中、還沒到期的裝置碼登入會在 `init` 之後對這個視窗重送一則進度（`done:false`，含 `deviceCode`）；彈出視窗不送。

〔遠端〕瀏覽器發起的登入（`accountLogin`、`accountLoginDevice`，WP4.8 起）：電腦只把**第一則** `accountLogin`（`done:false`）當成指令的輸出（`docs/sync-protocol.md` 14.6 的 `CommandOutput`），而且在 `commandResult` 的 `done` 之前送出：Codex 是帶 `deviceCode` 的那一則（拿到合格的驗證碼），Claude 是帶 `loginUrl` 的那一則（2026-10-26 起的 app 內登入：`claude.com`／`claude.ai` 的 https 網址，網頁在帳戶卡做成「打開 Claude 登入頁」連結與貼授權碼的輸入框，送 `accountLoginCode`），CLI 太舊而退回主控台時是「已在那台電腦開啟 Claude 登入視窗…」（沒有 `deviceCode`、`loginUrl`）。伺服器只轉給發起的連線、不保存、不寫進任何事件或稽核，`accountLoginDevice` 的 `accountId` 換成邏輯帳戶 id（`la_…`）。之後的進度與結束（`done:true`）不會到瀏覽器：網頁依帳戶的 `loginBusy` 與那一格的狀態判斷結果（成功、沒有完成、驗證碼到期、取消），驗證碼同時顯示在那台桌面的帳戶卡上（本機視窗照常收到每一則）。被拒（`commandResult` 的 `rejected`）時沒有任何 `accountLogin`，網頁立刻結束「登入中」；其他連線與其他電腦只看到 `loginBusy`。驗證網址的主機清單定案只有 `auth.openai.com`（`RemoteLoginUrlRules.VerificationHosts`，電腦與伺服器用同一份；網頁只要求 https 才做成連結）：不合格時電腦取消這次登入、不送出網址，伺服器也整則丟掉。Claude 的 `loginUrl` 同樣只收 https、沒有帳密、預設埠的 `claude.com`／`claude.ai`（`RemoteLoginUrlRules.ClaudeLoginHosts`，≤4096 字），不合格時電腦取消、伺服器整則丟掉、網頁不做成連結。

### `devices`〔同步〕
裝置清單（名稱、上線、版本、撤銷）改變時送，整份取代 `init.remote.devices`（宿主節流，最多每 500ms 一次）。
```jsonc
{ "type": "devices", "devices": [DeviceView] }
```

### `sync`〔同步〕
只有桌面：這台的同步狀態改變時送（狀態、待送筆數、配對進度、要本機確認的事），整份取代 `init.remote.sync`。標題列的同步小點與設定頁「多電腦同步」都看它。
```jsonc
{ "type": "sync", "sync": SyncStatus | null }
```
- `sync` 是 null：解除配對、或取消還沒完成的配對（回到未配對）。網頁把 `init.remote` 當成 null（不顯示同步的東西）。〔帳戶目錄〕送過 `init.syncStarting:true` 的話，讀完配對資料、確定沒有配對時也送一則 null（結束「啟動中」）；沒送過就照舊不送。
- `init.remote` 是 null（未配對時啟動）之後收到非 null 的 `sync`（開始配對、配對完成）：網頁自己建 `remote = { mode: "desktop", deviceId: sync.deviceId, devices: [], sync }`，之後的 `sync`、`devices` 照常整份取代。
- 配對完成後 `init.remote.deviceId` 不會跟著更新：這台的 deviceId 以 `sync.deviceId` 為準。

### `connection`〔同步〕
只有瀏覽器：由網頁的傳輸層（`transport-hub.js`）自己產生，不是伺服器送的。換了新連線時先送 `online` 再自動送 `ready`（伺服器回新的 `init`，網頁重新 `open` 畫面上的分頁）；斷線期間輸入區停用並顯示「重新連線中」（瀏覽器沒有離線佇列）。
```jsonc
{ "type": "connection", "state": "online" | "reconnecting" | "offline", "reason": "tooManyConnections" | "offline" | "server" | null, "retryAt": 1791580000000 | null }
```
- `reason`、`retryAt`（下一次嘗試的時間，ms）是選用欄位，只有 `reconnecting`／`offline` 時才有值。
- `online` 之後、收到 `init` 之前網頁顯示「載入中…」。

### `deviceStats`〔電腦狀態〕
右側「電腦狀態」面板的資料：整份取代，只送給 `monitor` 開著的視窗（桌面，每個 view 各自）或連線（瀏覽器，每條連線各自），最多約每秒一則；收到 `monitor {on:true}` 時有資料就立刻送一則。
```jsonc
{ "type": "deviceStats", "devices": [DeviceStatsView], "hub": HubStatsView | null }
```
- 桌面：`devices` 第一筆是這台（`self:true`、`name` 是電腦名稱；未配對時 `id` 是 null，已配對時是這台的 deviceId），之後是伺服器轉來的其他電腦（`self:false`、`name:null`；名稱、在線與否網頁照 `id` 從 `remote.devices` 查）。瀏覽器：全部來自伺服器，沒有 `self:true` 的項目。
- `seq`：每台各自遞增。同一筆樣本會因為別台更新而重送，`seq` 沒變就不是新樣本（走勢圖不加點）；變小（那台重開）也算新樣本。
- `ageMs`：宿主送出當下這筆樣本已經多舊（毫秒）。網頁以「收到的時間 − `ageMs`」當樣本時間，只拿來顯示「幾秒前」與走勢圖的橫軸、不拿來排序；超過 20 秒標「資料過時」（數字照樣顯示、變淡）。
- `hub`：MyriagentHub 主機（伺服器）自己的負載，網頁畫成清單最上面一張「MyriagentHub 主機」卡片（第三格是連線數，沒有延遲）。拿不到（桌面未配對、伺服器較舊、還沒取樣）時是 null 或省略，網頁不顯示那張卡片。
- 網頁的清單（`monitorrules.js` 的 `buildRows`）：MyriagentHub 主機 → 這台 → `remote.devices` 裡在線的電腦（照 `remote.devices` 的順序；在線卻沒有統計的顯示「沒有狀態資料（版本較舊或剛連上）」）→ 有統計卻還不在 `remote.devices` 裡的 → 離線的電腦（顯示最後上線時間，不看統計）。已撤銷的不列。未配對的桌面只有這台，面板底部提示配對之後能看到其他電腦。
- 數值欄位缺漏、不是有限的數字時網頁當成沒有值；不合格的項目略過（第三節 DeviceStatsView）。

### `copyFallback`〔遠端〕
只有瀏覽器：由網頁的傳輸層（`transport-hub.js`）自己產生，不是伺服器送的（WP4.6）。網頁的 `copyText`（Clipboard API 寫不進去時的退路，第二節）在瀏覽器版不送伺服器，傳輸層改交給網頁這一則；網頁開一個可以選取的唯讀文字框（以 `value` 放文字、自動全選）讓使用者自己複製。
```jsonc
{ "type": "copyFallback", "text": "要複製的文字" }
```
- `text` 不是字串的 `copyText` 不處理（傳輸層只在主控台警告）。這則不經伺服器的送出佇列，和 `init` 屏障無關。

### `control`〔遠端〕
只有瀏覽器：這條連線做遠端操作時的驗證狀態（伺服器的工作階段，設計 14.11 D3、D4）。伺服器在每則 `init` 之後、以及狀態改變時（再輸入密碼、改密碼）送；不放進 `init`（`init.remote` 沒有 `control`）。WP4.5 起送。
```jsonc
{ "type": "control", "tier1Until": 1791587200000 | null, "tier2Until": 1791580300000 | null, "defaultPassword": false }
```
- `tier1Until`／`tier2Until`：在這個時間之前做層級 1／2 的遠端操作不必重新輸入密碼，之後的操作伺服器回 `step_up_required`（網頁先 `POST /api/v1/auth/stepup {password}` 再重送）。`null`＝不要求近期輸入過密碼、只要工作階段有效。擁有者選「都不用輸入」（設計 14.11 的 Q2），依擁有者 2026-10-19 指示（自用、伺服器可信任）兩個一律是 `null`，伺服器與電腦都不回 `step_up_required`；日後收緊只改伺服器與電腦共用的常數（`RemoteAuthRules`），網頁照這兩個欄位顯示即可。
- `defaultPassword`：擁有者的密碼還是預設值。網頁常駐警告「密碼還是預設值，知道它的人都能遠端操作你開了遠端控制的電腦」並提供改密碼（Q5：照常可用、不擋操作）。
- 網頁（WP4.6）：警告放在頂部列下方，「改密碼」連到登入頁 `/`；之後收到 `defaultPassword` 不是 `true` 的 `control` 就拿掉。`tier1Until`／`tier2Until` 不顯示。

### `commandResult`〔遠端〕
瀏覽器：遠端指令（瀏覽器模式下轉送給電腦的 `cmd`）的最終結果，只送給發起的連線。`Cmd` 的回傳只代表伺服器收下（`accepted:true`），結果一律看這則。WP4.5 起送。
〔桌面遠端操作〕桌面：對其他電腦下的指令（帶 `commandId` 的副本寫入、其他電腦的帳戶與 CLI，第二節的「桌面遠端操作」）的結果，宿主只送給發起的視窗；形狀與瀏覽器相同（伺服器以 `PeerReply` 轉來，`docs/sync-protocol.md` 14.7），宿主或引擎合成的帶 `origin: "transport"`（`code` 是 `not_sent`：沒有連上伺服器或伺服器不支援；或伺服器 `PeerCmd` 的錯誤碼）。
```jsonc
{ "type": "commandResult", "commandId": "01J9Q…",          // 網頁送 Cmd 時帶的 commandId（第六節第 5 點）
  "state": "done" | "rejected" | "unknown",
  "code": "stale" | null,                                  // rejected 的原因，見下表；done、unknown 為 null
  "message": "這張核准卡已在其他地方處理" | null,          // 給人看的說明（純文字，textContent）
  "tier": 1 | null }                                       // 電腦算出的層級（step_up_required 時網頁據此提示）
```
- `done`：電腦的中樞已接受（例如 `send` 已建立項目或排進佇列），不是代理這一輪結束；之後的變化照常以 `item`、`header` 等送來。
- `unknown`：電腦 30 秒內沒有回報結果，指令**可能已經執行**。網頁不得自動重送（同一個指令重送時電腦回上一次的結果，至多執行一次），請使用者看對話內容確認；伺服器在 `iat`＋100 秒內沒收到電腦的結果也回 `unknown`。
- `rejected` 的 `code`：

| code | 意思 |
|---|---|
| `offline` | 目標電腦不在線（遠端指令不排隊，第 4 階段只有「所有電腦登出」會等電腦上線） |
| `policy` | 那台的本機政策不允許（`remoteControlLevel` 不是 `Control`、遠端已暫停、或要完全自動但 `allowRemoteFullAccess` 關閉），或那台的設定不允許這個值（禁止的資料夾〔`setFolder` 與新對話的資料夾經過 junction 或符號連結時照實際所在判斷，指到禁止的資料夾或網路一樣是 `policy`，通過時套用實際的資料夾〕、換到不進同步的代理種類、送出時被那台留下〔帶文字的這一則不留在那台、文字回到輸入框，帶的網頁上傳附件回到那台的輸入區；空送接著送的那則留在那台的佇列〕、從網頁登入電腦預設或不是 app 受管資料夾的 Codex 帳戶、對不進同步的代理種類做帳戶或 CLI 操作），或 `listFiles`、`fileFetch` 的路徑不在對話的工作資料夾與 `attachments` 裡（禁止清單、UNC、網路磁碟、junction 也是）；伺服器也以它回同步政策不是 `full` 的對話 |
| `step_up_required` | 要先重新輸入密碼（伺服器的工作階段；`tier` 是需要的層級）。依擁有者 2026-10-19 指示（自用、伺服器可信任）第 4 階段不會出現：兩個時間窗都是 `null`，伺服器與電腦都不回它 |
| `stale` | 卡片已被處理、內容變了（`requestDigest` 不符），或對話的程序已重開 |
| `not_owner` | 那台已不是這個對話的擁有者（交接、租約換了），或對話是其他電腦的副本 |
| `rate_limited` | 遠端操作太頻繁。依擁有者 2026-10-19 指示第 4 階段不會出現在 `commandResult`：伺服器與電腦都不限每台電腦的指令數（`Cmd` 層第 3 階段每個工作階段每秒 20 次的 `rate_limited` 照舊） |
| `busy` | 什麼都沒執行，稍後再試：電腦剛連上還在交握、電腦暫時寫不進遠端操作紀錄、電腦的程式正在關閉、同一個上傳的檔案正在下載（`addUploaded`）、那台的 CLI 正在更新或這個帳戶正在那台登入（`accountLogout`），或伺服器上的新對話還在建立中；「所有電腦登出」由伺服器自動重送（同一條連線先每 5 秒 3 次，之後每 60 秒一次，直到完成或 7 天到期） |
| `expired` | 指令在途中過期，沒有執行（伺服器與電腦之間太慢、電腦的時間推算不可用；電腦會重新 Hello）；「所有電腦登出」由伺服器以新的時間重簽重送，`created`＋7 天才停 |
| `conflict` | 那台目前的狀態不允許（執行中換帳戶或資料夾、資料夾不存在或逾時、帳戶未登入、新對話的 id 已存在、附件還在處理中〔網頁上傳的檔還在下載或匯入〕、空送時佇列是空的——輸入區有網頁上傳的附件時，空送會送出這些附件〔只附檔、不打字的訊息〕，不是接著送佇列；帳戶與 CLI 操作：帳戶已登入或正在登入、那台正在登入別的帳戶、CLI 未安裝或正在更新、要安裝的 CLI 已經裝好、裝置碼沒有在 25 秒內開始），或同一個指令 id 已經執行過不同的內容（不應發生） |
| `invalid` | 形狀不對（不認得的鍵、型別、數值列舉、超過上限），或那台沒有這個帳戶、資料夾不是本機磁碟的完整路徑、問卷沒有任何答案；`addUploaded` 的附件超過每則上限（20 個）、同一個上傳出現兩次，或伺服器上查不到、對不上、已過期的上傳（伺服器在轉送之前就回） |
| `unsupported` | 那台電腦的程式還不支援這個遠端指令（WP4.1 起的版本在 WP4.4 之前一律如此）；或那台目前沒有和伺服器連線、伺服器不支援媒體（WP4.7 的 `fileFetch`、`addUploaded`、`conversationCreate` 的 `uploads`：例如伺服器回滾到 WP4.7 之前），提示要寫「不支援或沒有連線」，不是只叫使用者更新程式 |

- 瀏覽器的傳輸層也會自己合成 `commandResult`（多一個 `origin:"transport"`；`code` 是 `Cmd` 的錯誤代碼、`rate_limited` 或 `not_sent`），見第六節第 13 點（WP4.6）。

### `resync`〔遠端〕
只有瀏覽器：伺服器上這個對話的內容不連續了（伺服器還原、對話的 epoch 換了、即時更新漏掉），網頁要丟掉已載入的項目、重新 `open`（宿主回整份 `conversation`）。`conversationId` 為 null 時伺服器會另外送 `init`，照第六節第 8 點整份取代。`init` 屏障可以清掉排著的 `resync`（`init` 之後網頁本來就重新 `open` 畫面上的分頁）。WP4.2 起送。
```jsonc
{ "type": "resync", "conversationId": "5f3a…" | null }
```
- 網頁（WP4.6）：開著那個對話時重送一次 `open`（之後的 `conversation` 整份重建）；沒開著或 `conversationId` 是 null 時什麼都不做。
- WP4.2 的伺服器只送帶 `conversationId` 的，給開著那個對話的連線：政策降級（只同步標題、DLP 暫停）清掉伺服器上的內容之後（快照比清除舊的連線才送）、物化表重建做完之後；送了就把那條連線標成不開著，增量停送到網頁重新 `open`。送出佇列溢出時伺服器重送 `init`，不送 `conversationId` 為 null 的 `resync`。

### `notify`〔遠端〕
只有瀏覽器：對話完成、提問、要核准或失敗時，給沒有在看那個對話的連線的通知（桌面由系統匣通知，不經網頁）。網頁依 `Settings.notifications`、自己的可見度與 Notification API 的權限決定要不要顯示；文字一律純文字。屬於通知類（第六節第 8 點：`init` 屏障保留、不清掉）。WP4.2 起送。
```jsonc
{ "type": "notify", "conversationId": "5f3a…", "kind": "completed" | "question" | "approval" | "failed",
  "title": "修正登入流程", "deviceName": "OFFICE-PC" }
```
- **對象**（WP4.2）：擁有者電腦新送來的 `conv.attention`（`docs/sync-protocol.md` 14.2；伺服器還原後的對帳重送與物化表重建不算），送給每一條已送過 `init`、**看不到**這個對話的連線（「看得到」的定義在第六節第 8 點）。看得到但沒有 focus 的分頁不收 `notify`，只留未讀（ConversationSummary 的 `unread`，取得 focus 才清）。
- `kind`：`conv.attention` 的 `plan`（計畫確認）送成 `approval`，其他照名稱；不認得的不送。`title` 是對話標題，`deviceName` 是擁有者電腦的名稱（裝置目錄，可能是 null）；兩個都是不可信字串，用 `textContent`。
- 通知類：`init` 屏障保留，但送出佇列溢出時整個清空，排著的 `notify` 會跟著遺失（同第 3 階段的 `toast`）；網頁不能拿它當成唯一的狀態來源，以清單的 `unread`、`waiting` 為準。
- 網頁（WP4.6）：`Settings.notifications` 關閉時不顯示；分頁在背景而且已授權時用系統通知（標題「OFFICE-PC：對話標題」、本文依 `kind`，點了回到這個分頁並聚焦那個對話），其他時候是頁內提示。網頁不在載入時要求授權：第一次收到 `notify` 而且權限還沒決定時，頂部列下方出現「開啟瀏覽器通知」，使用者按下才要求。未讀的點照清單的 `unread` 畫，網頁不自己算。

### `speak`〔遠端〕
只有瀏覽器：語音通知（2026-10-23 起）。擁有者電腦新送來的 `conv.attention`（同 `notify` 的對象規則，但**每一條**已送過 `init` 的連線都送，看不看得到那個對話都一樣——桌面也是看得到照念），句子取自事件的 `speech`（`docs/sync-protocol.md` 的 `conv.attention`；空字串不送），沒帶就由伺服器組只有「對話標題＋發生什麼事」的句子。通知類（`init` 屏障保留；溢出時跟著遺失）。
```jsonc
{ "type": "speak", "conversationId": "5f3a…", "id": 1234,   // id＝那筆事件的伺服器 seq（同一個瀏覽器的分頁之間去重）
  "text": "「整理報表」已完成：月報已經匯出成 Excel" }      // 沒有字數上限（VoiceNotifyRules；同步協定的 speech ≤ 4000 字）；不可信字串，只交給語音合成、不進 DOM
```
- 網頁（`voice.js`）：這個瀏覽器的開關（設定頁「語音通知」，`localStorage` 的 `myriagent.voice`，預設開、不送 `setting`、不跟其他裝置同步）關著就不念；以 Web Speech API 念（`lang` zh-TW，挑臺灣華語的聲音、本機的優先；語速與音調是這個瀏覽器的設定〔設定頁「語音通知」底下的滑桿，`localStorage` 的 `myriagent.voice.speed`、`myriagent.voice.pitch`，範圍同桌面的 `voiceSpeed`、`voicePitch`，預設 1.5 倍、升 2 個半音〕：`rate` 是語速、`pitch` 是 2^(半音/12)，放開滑桿時念一句試聽），最多排 3 句。收到 `speak` 就讓那個對話一直閃、閃到使用者點它（同桌面的 `flash`：每個分頁都閃，開關關著、念不出來也閃），開始出聲時還在閃就讓標頭另外閃三下（同 `speaking`）。這一頁在用麥克風（手機與平板的聽寫、錄音，`voicemsg.js`）時先不念：正在念的停下、之後到的記著，用完麥克風再照順序補念（兩分鐘內的；念的聲音會被麥克風收進去，手機上還會讓語音辨識聽不到）。同一個瀏覽器開好幾個分頁時，每個 `id` 只有一個分頁念（Web Locks＋`localStorage` 的紀錄，十分鐘）。
- 瀏覽器在使用者還沒點過頁面之前不准發聲（自動播放政策，手機也是）：被擋下的那一句先記著，頂部列下方出現「瀏覽器要先點一下畫面才能播放語音通知」與「播放」；點畫面任何地方或按「播放」就補念（兩分鐘以內的）。手機要開著這個頁面（在前景）才念得出來。

### `clipboard`〔遠端〕
只有瀏覽器：回應瀏覽器模式的 `copyItem`（網頁只有轉好的 HTML，原文在伺服器）：網頁以 Clipboard API 寫入 `text`；寫不進去（往返之後已不在使用者手勢內）時顯示可以選取的文字框。只送給發起的連線，通知類（`init` 屏障保留）。WP4.2 起送。
```jsonc
{ "type": "clipboard", "conversationId": "5f3a…", "itemId": "i9", "text": "Markdown 原文" }
```
- WP4.2：`copyItem` 只收 Assistant 的項目，`text` 是伺服器存下的原文（命中權杖 DLP 時是遮蔽版）。對話或項目不存在、已刪除、不是 Assistant：`Cmd` 回 `invalid`；原文是還讀不到的 blob（WP4.7 之前）：`unavailable`。和 `notify` 一樣在溢出時會遺失：網頁要能處理等不到回覆的情況（重按會再送一次 `copyItem`）。
- 網頁（WP4.6）：只處理 30 秒內自己送過 `copyItem` 的同一個對話與項目，其他的（沒有要、過期）一律略過：不寫剪貼簿、不開框。按下「複製」之後按鈕是「複製中…」，寫入成功才標「已複製」；寫不進去時開 `copyFallback` 同一個文字框。

### `desk`〔電腦狀態〕
遠端桌面（docs/sync-protocol.md 14.16）的封包，只送給開這個工作階段的視窗或連線：`{type:"desk", sessionId, kind, seq, body}`。`kind`：`frame`（影格：`w`、`h`、`key`〔整個畫面重送〕、`part`／`parts`〔太大拆成幾包，同一個 `seq`〕、`tiles:[{x, y, w, h, d}]`〔`d` 是 JPEG 的 base64〕、`screen`、第一包的 `screens:[{i, name, w, h, primary}]`、最後一包的 `cur {x, y, v, img?, hx?, hy?, sw?}`〔游標：位置是比例、形狀變了才帶 PNG〕）、`status`（`text` 是給人看的說明，null＝恢復）、`end`（`reason`、`text`）。
- 網頁（`remotedesk.js`，規則 `remotedeskrules.js`）：同一個 `seq` 的包都到了才畫（區塊以 `createImageBitmap` 在背景解碼、依序畫），畫完以下一包 `desk input` 的 `ack` 確認；6 秒沒有新的影格送 `config {key:true}`。

### `fileTree`〔電腦狀態〕
`deviceTree`（第二節）的結果，只送回發起的視窗或連線（`conversationId` 是 null、`requestId` 是指令的）。`root` 是那台的完整路徑、`name` 是資料夾名稱（網頁存進這台時的資料夾名稱）、`files` 是 `{path（相對、反斜線）, size}`、`dirs` 是相對路徑（含空的資料夾）；列不出時 `root` 是 null、清單是空的、`error` 是說明。伺服器投影時只留這些欄位，格式不對整則丟掉。
```json
{ "type": "fileTree", "requestId": "dt-3", "conversationId": null, "root": "D:\\相簿", "name": "相簿",
  "files": [{ "path": "a.jpg", "size": 1024 }, { "path": "2024\\b.jpg", "size": 2048 }], "dirs": ["2024"], "truncated": false, "error": null }
```
- 網頁（`devicefiles.js`）：一個資料夾在傳輸清單是一列（第幾個檔／共幾個、位元組的進度），底下的檔每 200 個送一次 `deviceFetchMany`；資料夾是空的照樣算完成。

### `fileRefs`〔電腦狀態〕
`deviceFetchMany`（第二節）的結果。`refs` 每一項是 `{path, name, size, url}`（`url` 是 `/f/{key}`，2 小時內有效）或 `{path, error, url: null}`；電腦送出時是 `ref`，伺服器改寫成 `url`（同 `fileRef`）。桌面的同步橋（`AllowPeerReplyUrls`）把每個 `url` 登記成可以轉送的鍵。
```json
{ "type": "fileRefs", "requestId": "dm-4", "conversationId": null, "root": "D:\\相簿", "expiresAt": 1791587200000,
  "refs": [{ "path": "a.jpg", "name": "a.jpg", "size": 1024, "url": "/f/…" }, { "path": "id_rsa", "error": "這個檔案不能從遠端讀取", "url": null }] }
```
- 網頁：桌面對每個檔送 `peerXferSave`（帶 `subfolder`）；瀏覽器版寫進使用者以 `showDirectoryPicker` 選的資料夾（Chrome、Edge；沒有這個能力的瀏覽器〔手機、Safari〕不給下載整個資料夾，提示改進資料夾裡選檔），照相對路徑建子資料夾、同名加「 (2)」。

### `fileRef`〔遠端〕
只有瀏覽器：回應 `fileFetch`。`url` 是伺服器的相對網址（`/f/{key}`，`key` 是 `{deviceId}-{ref}`，設計 4.10），網頁開新分頁預覽或下載（嚴格 UTF-8 的文字 ≤2 MB 以 `text/plain; charset=utf-8` inline 預覽，其他下載；UTF-16 等其他編碼的文字檔照樣掃權杖、以下載給）；`expiresAt` 是伺服器限制過的到期時間（最多從現在起 15 分鐘，之後 `/f/` 回 404）。`error` 不為 null 時只帶 `type`、`requestId`、`conversationId`、`error`，另外 `url` 是 null（伺服器的改寫一律寫 `url`）；`error` 的文字固定是「這個檔案不能從網頁讀取」（太大、禁止清單、含疑似憑證、讀不到都是這句，不分原因）。只送給發起的連線，通知類（`init` 屏障保留，跨 `init` 保留）。WP4.7 起送。
```jsonc
{ "type": "fileRef", "requestId": "r14", "conversationId": "5f3a…",
  "url": "/f/k3…", "name": "report.txt", "size": 18233, "expiresAt": 1791580900000,
  "error": null | "這個檔案不能從網頁讀取" }
```
- 網頁（WP4.6）：檔案瀏覽器的檔案（按兩下或「預覽或下載」）與 Item 裡的檔案連結送 `fileFetch`（`requestId` 是新的 ULID），開「預覽或下載檔案」框顯示「向電腦讀取檔案中…」；只處理自己送過的 `requestId` 而且是同一個對話的 `fileRef`，其他的略過。`url` 一律經 `liverules.fileRefView` 檢查（只收本站 `/f/` 開頭的相對網址），不合格或 `error` 不是 null 時框裡只顯示固定的「這個檔案不能從網頁讀取」（不顯示電腦或伺服器的原文）；合格時是「開啟」連結（`target="_blank" rel="noopener noreferrer"`，按了就關框）與「連結在 HH:MM 前有效」。`fileFetch` 被拒或沒送出時關框，原因看伺服器或傳輸層的提示；結果不明（重連後的 `init`、150 秒還等不到：`fileRef` 只送回發出的那條連線，重連後等不到了）時框裡改成「連線中斷，結果不明，請關閉後再試一次」（WP4.6 合併前審查 LIVE-6；晚到的 `fileRef` 照樣換成「開啟」）。瀏覽器版的文件附件沒有 `path`（本機路徑不離開電腦），不能 `fileFetch`：按了只提示「這個附件在對話所在的電腦上，網頁版不能開啟」。

### `remoteAudit`〔遠端〕
只有桌面：這台的遠端操作紀錄（sync.db 的 `remote_audit`，保留 180 天；設計 14.11 D22），回應本機的 `syncLoadRemoteAudit {before?, beforeSeq?, limit?}`（`before`、`beforeSeq` 是上一頁最舊一筆的 `at` 與 `seq`，`beforeSeq` 只能和 `before` 一起；`limit` 1～200，預設 50；任一項不合格整個指令忽略、不回覆；這個指令在第二節的表，以 `sync` 開頭，瀏覽器模式一律 `desktop_only`）。只送給發問的視窗。WP4.6 起送。
```jsonc
{ "type": "remoteAudit",
  "entries": [ { "at": 1791580000000, "commandId": "01J9Q…", "cmd": "send", "conversationId": "5f3a…" | null,
                 "issuer": "Chrome・Windows", "tier": 1, "fullAccess": false, "policy": "Control",
                 "state": "done" | "rejected" | "unknown", "code": null, "defaultPassword": false, "count": 1 } ],
  "hasMore": false }
```
- 指令內容只記遮蔽後的摘要（代理輸入的文字、回答的內容不記原文）；`count` 是同一個指令被重送、回上次結果的次數。
- `issuer` 取自紀錄的 `body_masked`（sync.db v2 的 `remote_audit` 沒有 `issuer` 欄；WP4.4 的 `RemoteAuditEntry.IssuerLabel` 已取出，不合格的標籤是 null）。`state` 也可能是 `executing`（還在執行，或電腦在執行途中結束、重新啟動之後還沒連上伺服器：第一次 Hello 時才改成 `unknown`）：WP4.6 顯示成「執行中或不明」。
- `defaultPassword`：發出這個指令時擁有者的密碼還是預設值（Q5：只記錄與警告，不擋）。
- 〔遠端〕WP4.6：每筆另帶 `seq`（sync.db 的流水號，例如 `"seq": 7`）。entries 由新到舊依 `(at, seq)` 排，下一頁以這一頁最舊一筆的 `(at, seq)` 當 `before`、`beforeSeq` 取，同一毫秒的列不會被跳過或重複。`hasMore` 是宿主多讀一筆算的：讀 `limit`＋1 筆，多出來的那筆不送、只表示還有更早的（`limit` 是 200 時 sync.db 一次只給 200 筆，宿主另外以最舊一筆的 `(at, seq)` 探一筆）。未配對、讀取失敗時回空的一頁（`hasMore` false）。

### `trash`〔遠端〕
只有桌面：這台的對話回收區（遠端 `delete` 搬進去的對話資料夾，保留 30 天），回應本機的 `syncListTrash`、`syncRestoreTrash`，只送給發問的視窗。WP4.6 起送。
```jsonc
{ "type": "trash",
  "entries": [ { "conversationId": "5f3a…", "title": "整理報表" | null, "trashedAt": 1791580000000,
                 "issuer": "Chrome・Windows" | null, "commandId": "01J9Q…" | null, "expiresAt": 1794172000000 } ] }
```
- 新到舊。`trashedAt` 是搬進回收區的時間；`expiresAt`＝`trashedAt`＋30 天，過了這個時間才清掉（剛好到期還在）。`title`、`issuer`、`commandId` 取自回收區的資訊檔，可以是 null（網頁顯示替代文字）。
- 還原的對話一律只在這台（回收區先寫伺服器刪除的旁檔，等於 `localOnly`、不再發布）；宿主在列舉回收區時不在 UI 執行緒讀磁碟。

### `remoteAuditExport`〔遠端〕
只有桌面：回應本機的 `syncExportRemoteAudit`，只送給發問的視窗。WP4.6 起送。
```jsonc
{ "type": "remoteAuditExport", "path": "C:\\Users\\…\\remote-audit\\d_…-20261021-153000.jsonl" | null, "count": 12, "error": null | "匯出逾時，請稍後再試" }
```
- 匯出到解除配對時那個檔的同一個資料夾，檔名加 `-yyyyMMdd-HHmmss`（不蓋掉解除配對時寫的那一份）；格式同解除配對的匯出（每列一個 JSON 物件、UTF-8、LF）。成功時 `error` 是 null，`path` 給網頁的「在檔案總管顯示」（`revealPath`）；失敗（未配對、同步資料庫還沒開啟、逾時 10 秒、寫入錯誤）時 `path` 是 null、`count` 是 0、`error` 是給人看的說明。

### `externalConversations`
只有桌面：回應本機的 `listExternalConversations`，只送給發問的視窗（2026-10-24 起；`Hub/AgentHub.ExternalImport.cs`、網頁 `Web/importconv.js`）。
```jsonc
{ "type": "externalConversations", "requestId": "imp-1",
  "items": [ { "sourceKey": "8f2a…", "provider": "Antigravity", "accountId": "local-antigravity", "accountLabel": "Antigravity",
               "sessionId": "e436d714-…", "title": "重構資料庫連線", "workingDirectory": "C:\\repo\\proj" | null,
               "updatedAt": 1791580000000, "messageCount": 14 } ] }
```
- 新到舊排序（依檔案最後寫入時間），最多 100 筆。已存在於 Myriagent 對話清單的 `(provider, sessionId)`、子代理對話（Claude 的 `isSidechain`、Codex 的 `source.subagent`、Antigravity 由 `invoke_subagent` 產生的子對話）與空對話不列。`sourceKey` 是帳戶與對話檔案路徑的 SHA-256 雜湊，本機路徑不交給網頁指定。

## 二、網頁 → 宿主（`cmd`）

| cmd | 參數 | 說明 |
|---|---|---|
| `ready` | `mode`（"main"／"popout"）、`conversationId`（popout） | 網頁載入完成；宿主回 `init` |
| `layout` | `layout: Layout` | 完整視窗的分頁／窗格版面改變（宿主只存起來，下次開啟還原；〔多視窗〕主視窗存在 settings.json 的 `Layout`、另外開的視窗存在 `Windows[].Layout`，彈出視窗不存）。**哪些對話開著分頁一律以 `open`／`close` 告知、哪些正顯示在畫面上以 `visible` 告知**，宿主不從 layout 推算 |
| `newWindow` | （無） | 〔多視窗〕只有桌面的完整視窗（Ctrl+Shift+N）：開一個新的完整視窗（從這個視窗錯開一點、一樣大），新視窗沒有版面、照常開一個空白的新對話。系統匣選單的「開新視窗」與再開一次 app（主視窗看得到時）也一樣。瀏覽器版是 `desktop_only` |
| `tabDrag` | `conversationId`、`text`（輸入框還沒送出的字） | 〔多視窗〕只有桌面的完整視窗：開始拖這個分頁（`dragstart`）。宿主記下來源與字（15 秒內有效），並對其他完整視窗送 `tabDrag`（第一節）。桌面的分頁拖曳不放 `text/plain`（拖到其他程式不會插入標題）。瀏覽器版是 `desktop_only` |
| `tabDragEnd` | `conversationId`、`dropped`（布林：`dragend` 的 `dropEffect` 不是 `none`＝有人接住） | 〔多視窗〕只有桌面的完整視窗：拖完了。宿主對其他完整視窗送 `tabDrag:null`；`dropped` 是 false 時看游標在哪裡：在 app 的另一個完整視窗上就搬過去（同 `tabMoveIn`，放在聚焦窗格）、在 app 的視窗以外（桌面、其他程式）就在游標的位置開新視窗搬進去、還在這個視窗或彈出視窗上（或按 Esc 取消）就不處理。瀏覽器版是 `desktop_only` |
| `tabMoveIn` | `conversationId`、`paneId`（放在哪個窗格；可為 null） | 〔多視窗〕只有桌面的完整視窗：接住別的視窗拖來的分頁（放在分頁列、窗格或視窗的任何地方）。宿主先讓這個視窗開著它、回 `conversation`（帶 `paneId`；拖曳時記下的字以 `draft` 帶過來）與 `focusConversation`，再對原本開著它的其他完整視窗與拖曳的來源送 `conversationMoved`，並把這個視窗帶到前面；草稿也能搬（不會在途中被丟掉）。網頁自己記著放開的位置，`conversation` 到時放在那裡。瀏覽器版是 `desktop_only` |
| `moveToNewWindow` | `conversationId`、`text` | 〔多視窗〕只有桌面的完整視窗：對話標頭「⋯」選單的「移到新視窗」。宿主開一個新的完整視窗，新視窗的 `init.movedIn` 列出這個對話、就緒後搬過去（同 `tabMoveIn`）。瀏覽器版是 `desktop_only` |
| `closeWindow` | （無） | 〔多視窗〕只有桌面另外開的視窗：搬走最後一個分頁之後關掉自己（`conversationMoved`）；主視窗送來不處理。使用者自己關掉的視窗從 settings.json 移除、下次不再開回來（結束 app 時開著的視窗下次啟動照樣開回來，含 app 更新）。瀏覽器版是 `desktop_only` |
| `newConversation` | `accountId`（可為 null＝用上次的帳戶）、`paneId`；〔遠端〕選填的 `deviceId`（新對話要在哪台電腦建立，WP4.5 起） | 開新對話（草稿）；宿主回 `conversation`（`header.isDraft=true`） |
| `listExternalConversations` | `requestId` | 只有桌面：側欄對話清單標題列的「匯入本機 CLI 歷史對話」開啟對話框時送出，宿主在背景以唯讀共享模式（`FileShare.ReadWrite \| FileShare.Delete`）掃描這台電腦上四家 CLI（Claude Code、Codex、Antigravity、Grok）的外部對話紀錄檔，回 `externalConversations`（第一節，只給送指令的視窗）。瀏覽器版是 `desktop_only` |
| `importExternalConversation` | `sourceKey`、選填的 `paneId` | 只有桌面：匯入對話框點選一筆外部對話（`sourceKey` 只認最近一次 `listExternalConversations` 快取的雜湊鍵）：宿主在背景讀取完整歷史訊息與工具紀錄、確認工作資料夾存在性後建立 `SessionStarted=true` 的正式對話並寫入 `ConversationStore`，回 `conversation`＋`focusConversation`，之後使用者可直接輸入訊息以原 CLI session 續接；已在清單中的直接開啟原對話。瀏覽器版是 `desktop_only` |
| `open` | `conversationId` | 在這個 view 開這個對話的分頁（還原版面時背景分頁也會送）；宿主回 `conversation` |
| `close` | `conversationId` | 這個 view 不再顯示它（關分頁）；草稿會被丟棄（〔熱更新〕第一則因 CLI 更新被延後的草稿已轉成正式對話〔`header.isDraft=false`、出現在清單〕，不會被丟，延後的訊息更新完照常送出）。執行中的對話**繼續在背景執行** |
| `visible` | `conversationIds: [對話 id…]` | 這個 view 目前顯示在畫面上的對話（各窗格的作用中分頁；切到儀表板時為空陣列）。分頁切換、開關、搬移、切換檢視時送（內容沒變不重送），視窗取得焦點時一律重送。桌面的視窗最小化或藏到系統匣（`document.hidden`）時送空陣列、回到畫面時重送（宿主照背景分頁節流項目與標頭，每 500 ms 最多一次）；瀏覽器版不因此送空陣列。宿主據此判斷使用者看不看得到：沒顯示在前景視窗畫面上的對話完成、提問或要核准時標未讀並通知；視窗在前景時，顯示中的對話清掉未讀。〔遠端〕瀏覽器版由伺服器判斷：這份清單加上 `ClientState` 的 `visible`、`focused`、`openConversationIds` 決定「看得到」（不收 `notify`、伺服器請電腦送即時更新）與「看見」（清未讀），定義在第六節第 8 點 |
| `monitor` | `on`（布林） | 〔電腦狀態〕右側面板看得到（顯示中、對話檢視、不是彈出視窗、頁面沒有被藏起來）時 `true`，否則 `false`；改變時送，**每則 `init` 之後依目前狀態再送一次**（宿主在 `ready`、重連時會忘掉）。宿主只對最後一次是 `on:true` 的視窗（桌面：每個 view 各自；只有 `on` 的視窗在時才每秒取樣這台）或連線（瀏覽器：伺服器以連線為單位記、不跨重連）送 `deviceStats`（第一節）。瀏覽器版斷線期間不送，重連後的 `init` 之後再送；沒有回覆 |
| `clientError` | `kind`（"error"＝沒被接住的例外／"rejection"＝沒處理的 Promise 拒絕／"media"＝圖片或影片載入失敗）、`message`（字串）、選填的 `source`（例外是「檔案:行:欄」、媒體是載入失敗的網址）、`detail`（呼叫堆疊） | 網頁的錯誤回報（2026-10-23；`Web/errorreport.js`）：使用者在畫面上看到的破圖、沒被接住的例外原本都不會留下紀錄。網頁對同一則（種類＋訊息＋來源）10 分鐘內只送一次、每次載入頁面最多送 100 則，送之前先截短（訊息 500、來源 500、堆疊 2000 字）；不送使用者輸入的內容。桌面寫進這台的記錄檔（`logs\yyyy-MM-dd.log`，WARN），瀏覽器版由伺服器寫進它的記錄（Warning）；兩邊都以 `ClientErrorRules` 驗證（種類不認得、`message` 不是字串、`source`／`detail` 不是字串也不是 null 就略過；伺服器回 `invalid`），去掉控制字元（換行換成「 ⏎ 」）、超過上限截短，同一件事 10 分鐘內只記一次。沒有回覆 |
| `loadOlder` | `conversationId`、`beforeId` | 宿主回 `olderItems`（`beforeId` 之前最多 80 筆；找不到 `beforeId` 時回最後一頁；`beforeId` 原樣帶回，網頁只採用還在等的那一頁） |
| `send` | `conversationId`、`text` | 送出輸入框內容（含已附加的檔案）。代理執行中送出的：支援插入的代理（`header.steer`）直接插進這一輪——先排進佇列（`composer.queue`，`composer.steering` 標示正在插入的那則），插進去就移出佇列、加成使用者訊息，代理下一步就讀到；插不進去（按了停止、換版中、代理不接受）或不支援插入的代理就留在佇列，這一輪完成後自動送出；〔遠端〕從瀏覽器版或其他電腦送來的也直接插進這一輪：插之前以當時的模式與政策重查（同送出前的 HoldRemoteSend），不允許的、已被留下等本機確認的不插，留在佇列等這一輪結束照舊處理，排在它後面的也跟著等。上一輪被停止或失敗時佇列會暫停：此時 `text` 為空字串、輸入區也沒有附件，宿主就從佇列第一則接著送。`text` 是 `/clear`（前後空白不算）時清除對話：草稿不做事（提示「還沒有內容」）；執行中不插入也不排隊（`sendRejected`＋提示）；Claude 已開始的 session 照常送給 CLI（不帶輸入區的附件；CLI 自己換新的 session，程序與 Remote Control 照舊），這一輪正常結束後宿主加一則 `noticeKind: "clear"` 的 Notice；其他代理（與 Claude 還沒開始的 session）由宿主直接關程序、換新的 session（不送給代理），等著回答的卡片作廢，加同樣的 Notice。項目不刪，之後的前文節錄只收這則 Notice 之後的。`text` 是 `/btw 問題`（2026-10-24；指令名稱後面接空白或結束）時是旁支提問：宿主接住、不送給代理、不排佇列、不插入這一輪（執行中、閒置都一樣），以 Claude 的 `side_question` 用目前對話的上下文問（沒有程序時以續接開一個，不送訊息），問答不進對話的上下文與 CLI 的逐字稿；對話加一則 `noticeKind: "sideQuestion"` 的 Notice（問題是引用區塊，下面先是「*思考中…*」，有答案或失敗時以同一則 `item` 更新，status 從 Running 變 Done／Failed；答案回來之前 app 就結束了的，下次載入時改寫成「沒有回答」）。不帶輸入區的附件（附件留著）。不是 Claude、草稿、問題是空的或超過 8000 字時 `sendRejected`＋提示。網頁不先畫「送出中」（`sendrules.showsSending`）。〔遠端〕瀏覽器版與其他電腦送來的 `/btw` 走同一條，結果是那則 Notice（`done`） |
| `cancelSteer` | `conversationId`、`id`（`composer.steered.id`） | 只有桌面的 Claude 本機對話（2026-10-25）：以 `cancel_async_message` 取消插進這一輪的那則。CLI 還沒讀走（只在有工具執行中時試，protocol-notes）就把那則從對話移除（`removeItem`）、附件還回輸入區，並只對送指令的視窗送 `steerCancelled`（網頁把文字放回輸入框）；讀走了只送 `toast`。瀏覽器版是 `desktop_only` |
| `quotaWaitCancel` | `conversationId` | 只有桌面這台的對話（2026-10-26）：取消「等額度重置後自動接著做」（標頭的 `quotaWait`）。宿主清掉等待、加一則提示「已取消等額度重置…」；沒有在等時什麼都不做。自己送出訊息、換帳戶也會取消等待（不必送這個）。瀏覽器版是 `desktop_only` |
| `rewindPreview` | `conversationId`、`itemId`（使用者訊息）、`requestId` | 只有桌面的 Claude 本機對話（2026-10-25）：回溯前的預覽，以 `rewindPreview` 只回給送指令的視窗。宿主在 Claude 的逐字稿找這則之前的分支點、以 `rewind_files` 的 dry run 問檔案會改哪些（沒有程序時開一個、不送訊息）。執行中、有背景工作、不是這個版本起送的訊息時回 `error` |
| `rewind` | `conversationId`、`itemId`、`conversation`（布林）、`files`（布林） | 只有桌面的 Claude 本機對話（2026-10-25）：先還原檔案（`rewind_files`；失敗就 `toast`、對話不動），再回溯對話：這則與之後的項目移除、附件還回輸入區、對送指令的視窗送 `rewound`（網頁把文字放回輸入框），記下分支點（meta 的 `ResumeAt`；這則是第一則時改開新的 session）並關掉程序，下一則訊息以 `--resume-session-at`＋`--fork-session` 從那裡分支。最後加一則提示說明做了什麼。執行中、有背景工作時只送 `toast`。瀏覽器版是 `desktop_only` |
| `forkConversation` | `conversationId`、`paneId`（網頁自訂的記號，回覆照原樣帶回） | 只有桌面這台的正式對話（2026-10-25 使用者要求）：對話標頭「⋯」選單與側欄右鍵選單的「延伸成新對話」。宿主複製這個對話的項目（新的 id；延伸時還在執行的工具與回覆算已中斷、等回應的卡片失效）與附件資料夾，開一個帳戶、模型、推理強度、權限模式、同步政策與工作資料夾都相同的新對話（標題「原標題（延伸）」，再延伸是「（延伸 2）」…；原對話用自己的工作區時指到那裡），最後加一則 noticeKind `fork` 的提示，回 `conversation`（帶 `paneId`）＋`focusConversation`。之後兩個對話各自進行：Claude 對話把原對話的逐字稿當下複製一份到新對話資料夾的 `fork-source\`，新對話第一次啟動以 `--resume <副本> --fork-session --session-id <新的 sid>` 分支（新的代理帶著完整的前文，原對話之後的內容不會跑進來；新的 session 開始後刪掉副本）；Grok 已建立過的對話以 `_x.ai/session/fork` 整份分叉、新對話續接回傳的 session（2026-11-01，protocol-notes「分叉」）；其他代理、找不到逐字稿的 Claude 對話與分叉不了的 Grok 對話，第一則訊息前面附前文節錄（同換帳戶，開頭說明是延伸的、上限 3 萬字）。原對話執行中也可以延伸。網頁收到帶這個 `paneId` 的 `conversation` 才在原對話的窗格旁邊開新窗格（窗格滿了就開在原窗格），宿主失敗時只送 `toast`、不留空窗格。草稿、副本不處理；瀏覽器版是 `desktop_only` |
| `cancelQueued` | `conversationId`、`id` | 從佇列移除一則（帶的附件還回輸入區；取消的是執行中排隊的提問回答時，那張問卷卡回到可作答）。取消正在插入的那則時只能取消插入前的等待，已經送到代理的照樣加成使用者訊息 |
| `stop` | `conversationId` | 停止這一輪 |
| `answer` | `conversationId`、`itemId`、`answers: {題目id: [字串…]}`；`notes`（選用）`: {題目id: [字串]}` | 每題的補充說明（2026-11-05：選了選項之後另外補一段；網頁只在有填時才帶，舊版的電腦不認得這個欄位）：Claude、Grok 以作答的 `annotations.notes` 交給 CLI，Codex 與答案當下一則訊息的併成答案多一筆「補充說明：…」，項目的 `answers` 也多這一筆（網頁另起一行顯示）；密碼類題目不收。回答問卷（網頁送出後先鎖住卡片，等宿主送回更新的 item）。非同步提問（`answerAsMessage`）的答案當作下一則訊息送出：代理執行中作答就同 `send` 插進這一輪，插不進去就排進佇列，這一輪正常完成後送出。Codex 在執行中送出非同步提問時宿主停下這一輪等回答（不算「已停止」；停下途中作答排進佇列、停下後送出），略過這張卡就送「我略過了你剛才的問題，請依你的判斷繼續。」讓代理接著做 |
| `skipQuestion` | `conversationId`、`itemId` | 略過問卷 |
| `approve` | `conversationId`、`itemId`、`choice`（"allow"／"allowSession"／"deny"）、`message`（拒絕時可附給代理的說明，可省略） | 回應核准卡 |
| `approvePlan` | `conversationId`、`itemId`、`choice`（"acceptEdits"／"manual"／"keepPlanning"）、`message` | 回應計畫確認卡（Claude 規劃模式結束時的 ExitPlanMode） |
| `rename` | `conversationId`、`title` | 改對話標題（去頭尾、最多 80 字）。Claude 的工作階段名稱跟著改（`rename_session`：claude.ai／Claude App 上的 Remote Control 工作階段）；反過來在 claude.ai／Claude App 改了名稱，宿主也改對話標題（標頭與清單照常更新、同步到其他電腦） |
| `autoTitle` | `conversationId` | 對話標題後面的自動重新命名圖示（點了直接產生，不開選單），與標題、側欄對話清單右鍵選單的「自動重新命名」：宿主產生這個對話目前主要任務的簡短摘要當標題（指示約 20 字、太長時只留第一個子句、最多 40 字）後照 `rename` 改名。模型讀最近的工作，判斷最新的要求屬於哪一件事（補充、修正、回答選項、提交、推上 main、發佈這類只是那件事的一個步驟），寫整件事而不是照抄最新的指令。節錄是最後一次 /clear 之後最近 4 個要求（「繼續」「好」「1」這類回應不算）、各輪最後的代理回覆、已回答的問卷（秘密答案不送）、最新的待辦清單、最新要求之後的代理回覆與工具，另附少量更早的要求與結論當背景。先用這個對話的帳戶跑一次模型（Claude：`claude -p` sonnet、不帶工具、不留紀錄；Codex：`codex exec --ephemeral`、唯讀沙箱、低推理強度；Antigravity：短命 `agy -p` `--output-format json --disable-slash-commands --effort low`，跑完清掉單發紀錄檔；Grok：短命 `grok agent --no-leader stdio` 低推理強度單發查詢，跑完清掉暫存 session 目錄），不能用（帳戶已移除、未登入、額度用完、CLI 不在或更新中）或失敗時借用其他已登入訂閱、還有額度的四家帳戶（同種代理優先，最多試 3 個帳戶），產生期間 `header.titling` 與清單的 `titling` 為 true；沒有能用的帳戶時不呼叫模型，直接用最新任務之後的待辦清單裡進行中的步驟、沒有就用最新任務那則訊息的第一行；每個帳戶都失敗時也改用它並以 toast 說明原因。產生期間標題被改過（手動改名、在 claude.ai 改名）就不蓋掉。草稿、副本（其他電腦的對話）不處理；網頁只在桌面這台的正式對話顯示標題後面的圖示、在選單列出這一項（瀏覽器版是 `desktop_only`） |
| `delete` | `conversationId`、`stopPrograms?` | 刪除對話（網頁先確認）；宿主送 `conversationClosed`＋`conversations`。`stopPrograms: true`（只有桌面這台的對話，2026-10-24）：確認框勾了「一併停止這個對話另開、還在跑的程式」（`liverules.deleteProgramsCheckbox`，`Header.programs` 或 `ConversationSummary.programCount` 大於 0 才問、預設勾），刪除前照 `programStop` 停止它列出的每個程式 |
| `setOption` | `conversationId`、`key`（"model"／"effort"／"permission"）、`value` | 只改這個對話（新對話一律用 `Settings.conversationDefaults`）；model 另外記成這個帳戶上次選的模型（設定頁這一家的預設模型是「沿用上次選擇」時，這個帳戶的新對話用它）。value 為 "default" 或空字串：model＝帳戶預設模型、effort＝CLI 預設、permission＝設定頁這一家的預設模式。換模型時推理強度換成新模型在設定頁的預設；沒有設定就沿用目前的，新模型的 `efforts` 沒有它（含不支援推理強度的模型）就清成 CLI 預設。執行中換 permission：Claude 立即套用到這一輪；做不到時（Codex）宿主送 toast 說明新的模式從下一則訊息開始生效。執行中換 model／effort：Claude、Grok 立即套用到這一輪（從下一次模型呼叫起；Claude 閒置的程序也即時套用，下一輪不必重開程序）；做不到時（Codex、Antigravity，或 CLI 拒絕）宿主送 toast 說明新的模型（強度）從下一則訊息開始生效。瀏覽器版與桌面的副本在那台執行中也照樣能換（網頁不因執行中停用模型與推理強度的選單；那台的中樞同樣立即套用，做不到時 toast 只給發起的檢視）。〔遠端〕瀏覽器版與桌面的副本送到擁有者電腦（只改這個對話，不記成帳戶的預設，WP4.4 D23）：網頁先樂觀換掉標籤，結果是 `rejected`／`unknown` 時（那台沒改、不會有新的標頭）標籤回到原本的值（`composer.revertOption`；那之前已收到新的標頭就以標頭為準） |
| `setAgent` | `conversationId`、`agent`（"default" 或專案自訂代理名稱） | 只有桌面：Antigravity 對話首輪建立前（`header.sessionStarted` 為 false）指定 `--agent` 自訂代理（掃描自工作資料夾 `.agents/agents/*.md` 與設定資料夾）；已建立 session 的對話或開啟精簡模式時送 toast 說明、不改。Claude 對話（2026-10-25）隨時可以換：只收這個帳戶的 CLI 回報過的代理（還沒回報過照收），下一則訊息起生效（程序以 `--resume` 加新的 `--agent` 重開、同一個 session）；執行中換時 toast 說明。瀏覽器版是 `desktop_only` |
| `promptHistory` | `conversationId`、`requestId` | 只有桌面（2026-10-25）：「最近的指令」那一行的「歷史指令」鈕。宿主以 `promptHistory`（第一節）只回給送指令的視窗：這個對話最近 10 則使用者訊息的完整文字。瀏覽器版是 `desktop_only`（網頁從已載入的項目找） |
| `taskDetail` | `conversationId`、`key`（`header.background.tasks[].key`）、`requestId` | 只有桌面這台的本機對話（2026-10-25 使用者要求，取代同一天的 `taskOutput`）：等背景工作回報那一行的「詳細」。宿主只認目前回報的背景工作，回它在做什麼、指令或交代的內容與目前的輸出（Claude 以 `get_task_output` 問活著的程序，沒有程序不開新的；Grok 讀通知帶的本機輸出檔），結果以 `taskDetail` 只回給送指令的視窗。瀏覽器版是 `desktop_only` |
| `taskStop` | `conversationId`、`stopId?`（`header.background.tasks[].stopId`） | 只有桌面這台的本機對話（2026-10-25 使用者要求）：等背景工作回報那一行的「停止」與「詳細」對話框的停止鈕，強制停止背景工作。帶 `stopId` 停那一個（宿主只認目前回報的背景工作），不帶是全部（含對不到工具卡、只算進 `count` 的）。Claude 以 `stop_task` 逐一停止（CLI 不為此開一輪）；Codex 以實驗性的 `thread/backgroundTerminals/terminate`（全部是 `clean`），停掉的指令卡片是「已停止」、不接續（protocol-notes「Codex 的背景指令」）；Antigravity、Grok 沒有逐一停止的協定（沒有 `stopId`）：閒置時結束 CLI 的程序樹，背景工作跟著結束，下次送出照常續接，執行中回錯誤。停了就在對話加一則 noticeKind `backgroundTask` 的 Notice（「已強制停止背景工作：執行指令：npm test（代理不會自動接著處理）。」，`BackgroundWorkRules.StoppedNotice`），停不了的原因只對送指令的視窗送 `toast`。副本與草稿忽略，瀏覽器版是 `desktop_only` |
| `goalClear` | `conversationId` | 只有桌面的 Claude 本機對話（2026-10-25）：標頭有 `goal` 時照一般的訊息送出 `/goal clear`（看得到、CLI 回「Goal cleared: …」）；這一輪還在執行時只送 `toast`（先按停止：插進這一輪的斜線指令不會被當成指令）。瀏覽器版是 `desktop_only` |
| `codexControl` | `conversationId`、`requestId`、`action` 與下述參數 | 桌面本機 Codex 對話的目標、技能與速度控制（2026-10-26）；只接受固定動作，不接受任意 RPC。回 `codexControl`。瀏覽器版與遠端副本不提供 |
| `setOutputStyle` | `conversationId`、`style`（輸出風格名稱，null 或空字串＝照帳戶設定） | 只有桌面的 Claude 本機對話（2026-10-25）：只收 `header.outputStyles` 裡的（這個帳戶還沒回報過清單時照收）。有活著的程序時以 `apply_flag_settings` 立即套用（下一輪起；實測見 protocol-notes），沒有程序或沒套上的下一次啟動以 `--settings` 的 `outputStyle` 帶；改回「照設定」時下一則訊息重開程序。換到另一家的帳戶時清掉。瀏覽器版是 `desktop_only` |
| `setAccount` | `conversationId`、`accountId`、`when?` | 換帳戶。草稿直接換；已開始的對話改由新帳戶以前文節錄續接。宿主回 `conversation`。執行中（使用者 2026-10-23）：可以換到另一個帳戶（2026-10-24 起四種代理之間都可以互換：換到另一種代理時新的一方開新對話、以前文節錄接續，接續訊息寫成換了代理；模型、推理強度與權限模式照換到另一家的規則）——宿主先停下這一輪（同 `stop`），這一輪結束時換過去、對話加 noticeKind `accountSwitch` 的提示，隱藏送出前文節錄＋接續訊息讓新帳戶從中斷處接著做（停止生效前這一輪就自己做完時只換帳戶、不接續），排隊的訊息不暫停；還不能在執行中換的種類（目前沒有，`AccountSwitchRules.WhileRunningBlock`）送 toast、不停下（網頁的帳戶選單照 `accountrules.runningAccountMenu`：執行中列四種代理的帳戶、同一種代理在前、其他照 `init` 的 `providers`，選了再選時機，`ui.choiceDialog`）。`when`（2026-10-24）：`"turnEnd"`＝目前步驟完成後換（2026-10-27 起；沿用原協定值）——宿主記下目標、送 toast、標頭的 `pendingAccount` 標出；等會話已開始且工具、背景工作、待回應請求都結束，就主動停下這一輪，換帳戶並以原本前文接續剩餘工作，不等整個任務完成。安全點以會話即時狀態為準，不能只信排隊中的事件，工具結果進入前文後才交接。若在安全點交接之前這一輪先自行結束（做完、失敗或按了停止）照一般的結束收尾（停止與失敗照常暫停佇列、加「已停止」）後只換帳戶、加一般的換帳戶提示，不送接續訊息，正常結束時排隊的訊息改在新帳戶送；只有這一輪因為原帳戶額度用完而失敗（`autoSwitchAccount` 開著、沒按停止）時換到選的帳戶接著做（提示寫「改用你選的」、接續訊息同額度用完）。沒帶、`"now"` 或其他值＝立即（上面的停下再換）；已經為了換帳戶在停下時再選 `turnEnd` 照立即（停止收不回來，只換目標）。執行中選回目前的帳戶＝取消待換：`turnEnd` 的照常做完、送 toast，立即的這一輪照樣停下（網頁的選單另有「取消換帳戶」，送目前的帳戶）。不在執行中時 `when` 只在這一輪結束了、背景工作還在跑時有意義（2026-10-27：換帳戶會關掉程序、背景工作跟著停，以前在一輪結束時直接換過去，背景工作被停掉、代理不知道）：`"now"`＝使用者確認了現在就換——照換，對話加 noticeKind `accountSwitch` 的提示，隱藏送出前文節錄＋接續訊息（列出被停掉的背景工作、請新帳戶確認做到哪裡再接著做，`AccountSwitchRules.BuildIdleSwitchContinuation`）；`"turnEnd"` 或沒帶（瀏覽器版與其他電腦看不到這台的背景工作）＝等背景工作都結束、代理處理完結果、對話閒置時才換（記在 meta 的 `DeferredAccount`，app 換版重開後照樣換；對話加一則 noticeKind `accountSwitch` 的提示，標頭的 `pendingAccount` 帶 `background: true`；維護每 30 秒與使用者送出之前檢查，換過去時同一般的換帳戶、不送接續訊息；選回目前的帳戶＝取消、送 toast；選的帳戶登出、移除了就不換、加提示）。執行中選的「這一輪結束後再換」在這一輪結束時還有背景工作的也照這樣等；立即換的這一輪在停止生效前自己做完、還有背景工作時照換並送上面的接續訊息。網頁在桌面這台的對話有 `background` 時先問（`accountrules.backgroundSwitchConfirm`：現在換或背景工作結束後再換，後者是預設），一律帶 `when`（`backgroundSwitchCommand`）。網頁只在選了等目前步驟完成時帶 `when`（還沒更新的電腦收到不認得的鍵會以 `invalid` 拒絕遠端指令）。〔遠端〕同本機，不能換時回 `conflict`（附原因），遠端換的不改「上次使用的帳戶」；遠端型錄的 `when` 只收 `now`、`turnEnd` 或 null。舊桌面仍將 `turnEnd` 解讀為等整輪結束，需更新桌面才有步驟間交接。〔遠端〕〔桌面遠端操作〕其他電腦的對話（瀏覽器版已開始的對話、桌面可以操作的副本；2026-10-25 使用者要求：桌面也能換，但只能換成那台已登入的帳戶）：網頁的帳戶選單只列那台已登入可用的帳戶（`AccountDeviceView` 的 `status` 是 `ready`、`identityOk` 不是 false；`liverules.remoteAccountMenu`／`deviceAccounts`），`accountId` 帶邏輯帳戶（伺服器換成那台的本機 id），目前的帳戶照 `liverules.headerAccountId`（副本是標頭的 `replica.accountId`）打勾；那台離線、政策不是 Control、沒連線時點了只說明原因，副本不能從這台操作（同步沒連上、不支援）時帳戶晶片停用。執行中看那台的（`liverules.ownerRunning`），一樣先選時機。那台也只接受已登入的帳戶：沒登入、沒安裝 CLI 的回 `conflict`（畫面可能是舊的）。副本的 `setAccount` 由同步送給那台，宿主不回 `conversation`（結果是 `commandResult`，換好之後副本的標頭跟著那台的中繼資料更新），網頁不記在等回覆 |
| `chooseFolder` | `conversationId` | 宿主開資料夾對話框，選好後更新 header |
| `setFolder` | `conversationId`、`path` | 從最近使用的資料夾清單直接設定 |
| `resetFolder` | `conversationId` | 恢復預設工作資料夾（對話自己的 workspace） |
| `openFolder` | `conversationId`、`which`（"working"／"attachments"） | 用 Windows 檔案總管開啟 |
| `attach` | `conversationId` | 宿主開選檔對話框 |
| `addFiles` | `conversationId`（以 `postMessageWithAdditionalObjects` 附 File 物件） | 拖放或貼上的檔案 |
| `pasteImage` | `conversationId`、`dataUrl`、`name` | 剪貼簿圖片 |
| `deviceBrowse` | `deviceId`（必帶，其他電腦）、`requestId`、`path`（null＝磁碟與常用資料夾） | 〔電腦狀態〕電腦狀態面板的檔案傳輸（2026-11-09，sync-protocol 14.15）：列那台的一個資料夾，回 `files`（第一節的電腦狀態一點）。型錄的 Device 指令、層級 2：那台的遠端操作政策要是「可以操作」、app 不是以系統管理員執行、版本有 `xfer1`（否則伺服器立即拒絕 unsupported）；桌面照 `liverules.PEER_COMMANDS` 帶 commandId |
| `deviceFetch` | `deviceId`（必帶）、`requestId`、`path` | 〔電腦狀態〕那台一個檔的下載參照，回 `fileRef`（`conversationId` 是 null；`url` 是 `/f/{key}`，2 小時內有效）。沒有大小上限、不掃文字裡的憑證；憑證、金鑰類的檔名一律 `error`。桌面拿到之後送 `peerXferSave`，瀏覽器以 `<a download>` 交給瀏覽器（伺服器整份接成一個 200，≤4 GB） |
| `devicePut` | `deviceId`（必帶）、`requestId`、`folder`（那台的完整路徑）、`uploads`（同 `addUploaded`，1～20 個、每個 ≤512 MB） | 〔電腦狀態〕把已上傳到伺服器的檔存進那台的 `folder`（同名自動改名、不覆寫、標成從網際網路下載）。`done` 只表示那台接下了；接收的進度與結果看那台 `deviceStats` 的 `xfers`（第三節 DeviceStatsView）。資料夾不存在 `conflict`、禁止的資料夾 `policy`。選用的 `subfolder`（2026-11-09，整個資料夾的上傳；版本要有 `xfer2`，否則伺服器立即拒絕 unsupported）：`folder` 底下的相對資料夾（`photos\2024`，斜線也收），那台逐段清理名稱（不合法的字元換成底線）、建好再存；有 `.`／`..` 回 `invalid` |
| `deviceTree` | `deviceId`（必帶）、`requestId`、`path`（那台的資料夾） | 〔電腦狀態〕整個資料夾的下載（2026-11-09，版本要有 `xfer2`）：列那個資料夾底下所有的檔（逐層、相對路徑與大小）與資料夾，回 `fileTree`（第一節）。不進重新解析點與禁止清單的資料夾、不列憑證類檔名；最多 5000 個檔、5000 個資料夾、約 18 秒，超過標 `truncated` |
| `deviceFetchMany` | `deviceId`（必帶）、`requestId`、`root`（`fileTree` 的 `root`）、`pathsJson`（相對路徑的 JSON 陣列字串，1～200 個） | 〔電腦狀態〕整個資料夾的下載：一次發多個檔的下載參照，回 `fileRefs`（第一節）；每個檔照 `deviceFetch` 的守門開檔，開不了的那一項帶 `error`。網頁每 200 個一批 |
| `deskOpen` | `deviceId`（必帶）、`requestId`（看的一方產生的工作階段 id，8～64 個英數字與 `._:-`） | 〔電腦狀態〕遠端桌面（2026-11-09，docs/sync-protocol.md 14.16）：開始看與操作那台的畫面。型錄的 Device 指令、層級 2：那台要「可以操作」、app 不是以系統管理員執行、同時只能一個人看（`conflict`）、版本有 `desk1`（否則伺服器立即拒絕 unsupported）。`done` 之後那台送 `desk` 影格；桌面照 `liverules.PEER_COMMANDS` 帶 commandId |
| `desk` | `sessionId`、`kind`（`input`／`ack`／`config`／`close`）、`seq`、`body` | 〔電腦狀態〕遠端桌面看的一方的封包（`DeskRules`）：`input {events?, ack?}`（事件見 sync-protocol 14.16；確認併在輸入裡）、`ack {seq}`、`config {quality?, screen?, maxWidth?, key?}`、`close`。瀏覽器版由伺服器轉給這條連線開的工作階段的那台（不經型錄、不簽信封；每秒的指令數上限照舊，網頁每 66 ms 最多送一包），桌面由宿主交給同步。工作階段已經不在時回一則 `desk` 的 `end` |
| `peerXferSave` | `transferId`（UploadIdRules）、`url`（`deviceFetch` 回的 `/f/{key}`）、`name`、`folder`（這台的完整路徑；null＝「下載」）；`subfolder`（選用） | 〔電腦狀態〕只有桌面：在背景以裝置的存取權杖分段（每段 ≤8 MB）下載進這台的資料夾，回 `xferProgress`（第一節）。`url` 要是這個視窗收過的 `fileRef` 的網址，或 `fileRefs` 裡的一個。整個資料夾的下載帶 `subfolder`（資料夾名稱＋檔案所在的相對資料夾，`相簿\2024`）：存進 `folder` 底下的這個子資料夾（逐段清理名稱、沒有就建；有 `.`／`..` 回 `failed`） |
| `peerXferCancel` | `transferId` | 〔電腦狀態〕只有桌面：取消還在下載的 `peerXferSave` |
| `peerXferReveal` | `transferId` | 〔電腦狀態〕只有桌面：在檔案總管顯示下載好的檔（只認這次執行下載過的） |
| `xferPickFolder` | `requestId`、`initial`（選用，起始資料夾） | 〔電腦狀態〕只有桌面：開這台的資料夾選擇視窗，回 `xferFolder` |
| `peerUpload` | `uploadId`（UploadIdRules）、`conversationId`；以 `postMessageWithAdditionalObjects` 附一個 File 物件，或帶 `dataUrl`、`name`（剪貼簿的圖片沒有本機路徑，≤30 MB） | 〔桌面遠端操作〕只有桌面：可以操作的副本的附件，宿主以裝置的存取權杖上傳到伺服器（`PUT /api/v1/device/uploads/{uploadId}`，`docs/sync-protocol.md` 14.7），回 `peerUpload`（第一節）；附 File 卻沒有本機路徑時回 `no_path`，網頁改帶 `dataUrl` 重送一次。〔電腦狀態〕電腦狀態面板的上傳改帶 `deviceId`（另一台已配對的電腦）而不帶 `conversationId`，上傳期間另回 `peerUploadProgress` |
| `peerUploadCancel` | `uploadId` | 〔桌面遠端操作〕只有桌面：取消還在上傳的（宿主之後回 `status: 0`） |
| `draftSetDevice` | `conversationId`（桌面的草稿）、`deviceId`（null 或這台＝回到本機草稿）、`accountId`（那台可用的邏輯帳戶 `la_…`）、`provider` | 〔桌面遠端操作〕只有桌面：草稿改開在其他電腦（`docs/sync-protocol.md` 14.7）。宿主回 `header`（`deviceId`、那台的帳戶、預設的模型選單，`workingDirectory`／`customFolder` 是選的那台的資料夾〔沒選是 null／false＝那台對話自己的工作區〕、`recentFolders` 是那台最近用過的資料夾〔那台的副本的自訂資料夾，由新到舊〕、`peerCreating` 是建立中）。遠端草稿的第一則 `send` 要帶 `commandId`：宿主組 `conversationCreate` 經伺服器送給那台，結果以 `commandResult` 回；那台建好、副本出現時送 `conversationClosed {conversationId: 草稿, replacedBy: 副本}`，被拒時送 `sendRejected` 把文字還回草稿。遠端草稿的 `setFolder {path}`（本機磁碟的完整路徑，宿主去掉前後空白與引號；不合格跳提示）、`resetFolder` 只記在草稿上，第一則 `send` 的 `conversationCreate` 帶 `folder`，由那台建立時檢查（找不到、不能從遠端用就整則被拒、文字還回草稿）；換到另一台電腦、回到這台時清掉。遠端草稿不能用這台的資料夾對話框（`chooseFolder`）、附加檔案、換這台的帳戶（提示「送出第一則訊息之後才能…」） |
| `peerFileSave` | `conversationId`、`url`（`fileRef` 的 `/f/{鍵}`）、`name`；`open`（選用，布林） | `open: true`（2026-11-05：網頁對 Word、Excel、PowerPoint、PDF 帶）時下載完用這台的預設程式開（同 `openPath` 的白名單，白名單以外照舊在檔案總管顯示）。〔桌面遠端操作〕只有桌面：把可以操作的副本的 `fileFetch` 結果下載到這台的「下載」資料夾（不覆寫，同名加「 (2)」），在檔案總管顯示並以 `toast` 提示；網址要是伺服器轉來、宿主登記過的鍵 |
| `addPathAttachment` | `conversationId`、`path` | 從檔案瀏覽器把一個本機檔案加進附件 |
| `removeAttachment` | `conversationId`、`id` | |
| `listFiles` | `requestId`、`conversationId`、`path`（null＝工作資料夾）；〔遠端〕`dirsOnly`（選用，布林） | 宿主回 `files`。〔遠端〕`dirsOnly: true`＝只列資料夾，給瀏覽器模式的遠端資料夾挑選器（取代 `chooseFolder`，WP4.6；層級 2）；桌面本機的檔案瀏覽器不送，本機的宿主忽略這個欄位。〔遠端〕WP4.7 起轉給對話的擁有者電腦（工作資料夾內層級 1、`dirsOnly` 層級 2；第一節 `files` 的瀏覽器版）；新對話（伺服器草稿）的 `dirsOnly` 由伺服器改成 `listFolders` 轉給草稿的電腦（第一節 `files` 的新對話一點） |
| `fileSearch` | `requestId`、`conversationId`、`query` | 宿主回 `fileSearch`。〔遠端〕WP4.7 起轉給對話的擁有者電腦（層級 1） |
| `searchConversations` | `requestId`、`query` | 側欄的對話搜尋（2026-10-23）：網頁即時比對標題，打字停下 250 ms 後請宿主搜內容，宿主回 `conversationSearch`（第一節）；搜尋中對話清單有變時最多每 1.5 秒重搜一次。〔遠端〕瀏覽器版由伺服器搜（第六節第 9 點的伺服器處理，被拒不提示） |
| `gitRefresh` | `conversationId` | 只有桌面：git 晶片選單的「重新檢查」，宿主重讀這個對話工作資料夾的 git 狀態並問遠端（`ls-remote`），結果以 `gitStatus` 回來（第一節）。副本、要開在其他電腦的草稿、沒有 git 時忽略 |
| `launchAction` | `conversationId`、`targetId`、`action`（"start"／"pause"／"resume"／"stop"／"restart"） | 只有桌面：執行列的按鈕（第一節 `launchStatus`）。`targetId` 只認這個對話的資料夾列出的（`LaunchView.targets`），同時把它設成選的目標。閒置時 `start`＝建置並執行、暫停中的 `start`＝繼續；`stop` 在建置中、執行中、暫停中都結束整個程序樹；`restart` 停掉（建置中的也停）、等結束後重新建置並執行，閒置時同 `start`。不合時宜的（執行中按 `start`）忽略；暫停或繼續失敗時送 toast。結果以 `launchStatus` 回來。副本、要開在其他電腦的草稿與遠端指令一律不處理 |
| `launchSelect` | `conversationId`、`targetId`、`mode`（選用："debug"／"release"） | 只有桌面：執行列換選的目標（同一個資料夾的對話一起換；記在 `launch\prefs.json`，app 重開後照舊）；帶 `mode` 時一併換這個目標的執行模式（列出這個目標的對話一起換；執行中的下一次啟動才套用；不認得的值不理） |
| `launchLog` | `conversationId`、`targetId`、`epoch`（網頁已有的；沒有是 -1）、`after`（網頁已有的最後一行序號；沒有是 0） | 只有桌面：輸出面板開著時拿這個目標的輸出，宿主回 `launchLog`（第一節）。網頁在面板打開、`LaunchView` 的 `logSeq` 或 `logEpoch` 變了時再拿（同時只等一則） |
| `launchRescan` | `conversationId` | 只有桌面：目標選單的「重新偵測專案」 |
| `mcpCheck` | 選填的 `force`（布林） | 只有桌面：儀表板各 Claude 帳戶的 MCP（2026-10-24，第一節 `mcp`）。切到儀表板與打開明細時送（不帶 `force`：5 分鐘內查過就只重送 `mcp`），明細的「重新檢查」帶 `force: true`。宿主對每個不是精簡模式的 Claude 帳戶開一個檢查用的程序（中立的資料夾 `cache\mcp-check`、不送訊息、不耗額度）只問 `mcp_status`，問完就關；正在檢查或授權的帳戶不重查。瀏覽器版不送 |
| `mcpAction` | `accountId`、`server`（`mcp.accounts[帳戶].servers[].name`）、`action`（"authenticate"／"reconnect"） | 只有桌面：儀表板 MCP 明細的按鈕（2026-10-24）。宿主只認這個帳戶最近一次查到的伺服器名稱；正在檢查或授權時只送 `toast`。`authenticate`：在檢查用的程序送 `mcp_authenticate`、以預設瀏覽器開 CLI 給的授權網址（只收 http、https），程序開著等瀏覽器回到 CLI 的本機回呼，每 2 秒問一次狀態、最多 5 分鐘；連上之後對這個帳戶開著程序的 Claude 對話都送 `mcp_reconnect`（用上新的權杖，不開新的程序）。`reconnect`：在檢查用的程序重新連線並重新檢查，連得上就同樣讓開著的對話重新連線。過程中 `mcp` 的 `authorizing`／`checking` 標出，結果只對送指令的視窗送 `toast`。沒有啟用／停用：CLI 的停用記在 `.claude.json` 的 `projects[工作資料夾].disabledMcpServers`，是逐資料夾的設定（protocol-notes） |
| `programStop` | `conversationId`、`programId`（`Header.programs[].id`） | 只有桌面：標頭「背景程式」選單的停止（2026-10-24）。宿主只認這個對話最近一次掃描列出的 id（PID＋建立時間，網頁不能指定別的程序），以 TerminateProcess 結束那個程式與它的子孫（同執行列的停止，不送 Ctrl+C），之後立刻重掃、以 `header` 回新的清單；已經不在清單、或結束不了時只對送指令的視窗送 `toast`。副本與草稿忽略 |
| `healthCheck` | 選填的 `force`（布林） | 只有桌面：儀表板的健康度（第一節 `health`）。切到儀表板時送（不帶 `force`），「重新分析」帶 `force: true`；宿主以 `health` 回。只讀，不改任何檔；瀏覽器版是 `desktop_only` |
| `healthFix` | `task`（"merge"＝合併電腦層級的指示檔／"trim"＝精簡一個帳戶的全域指示／"memory"＝整理一個帳戶超過或接近上限的自動記憶索引）、`accountId`（用哪個帳戶開對話，這台已登入的）、`targetAccountId`（trim、memory：要精簡或整理的帳戶）、`paneId` | 只有桌面：「用代理合併…」「用代理精簡…」「用代理整理…」。宿主以 `accountId` 的帳戶開草稿（工作資料夾是使用者資料夾），回 `conversation`（帶 `paneId`，`draft` 是預先填好的說明：各檔的位置與分析結果、先給計畫等確認才寫、寫前備份），不自動送出。還沒分析過、沒有可合併的（`instructions.mergeable` 是 false）、要精簡的帳戶沒有全域指示、要整理的帳戶沒有超過或接近上限的記憶索引、帳戶不在時只對送指令的視窗送 `toast`。整理記憶索引的說明另約定：索引一行一條、細節搬到主題檔、重複的合併、過時的查證後才刪，寫入前把整個 `memory` 資料夾複製成同一層的 `memory.bak-<時間>`。瀏覽器版是 `desktop_only` |
| `scheduleWatch` | `on`（布林） | 只有桌面：排程頁顯示中（`true`）或離開（`false`）。有視窗開著時宿主每 30 秒重讀這台的排程（上次讀取超過 30 秒就馬上讀，否則只對這個視窗重送 `schedules`），都沒開時每 5 分鐘。瀏覽器版是 `desktop_only` |
| `scheduleRefresh` | 無 | 只有桌面：排程頁的「重新掃描」，馬上讀一次（讀取中就讀完再讀）。瀏覽器版是 `desktop_only` |
| `scheduleAction` | 選填的 `deviceId`（其他電腦：只收 run／stop／enable／disable，S3 的遠端指令，那台執行）、`path`、`action`（"run"／"stop"／"enable"／"disable"＝在工作排程器上做；"ignore"／"unignore"＝不再列出／恢復；"adopt"／"unadopt"＝納入管理／不納入；"autoDiagnose"＝這個排程失敗時要不要自動診斷，帶 `on`；"importOrigin"＝出處是外部的工作階段時匯入成 Myriagent 對話，宿主回 `conversation`，同匯入外部對話） | 只有桌面：排程頁的快速操作（擁有者 2026-10-26 決定：這幾個直接做，刪除、改時間、改內容一律交給代理）。前四個在背景經工作排程器做（權限不足、找不到時只對送指令的視窗送 `toast`），做完重讀；`entries[].busy` 標出進行中。其他只改這台的 `state.json`，重送 `schedules`。`path` 不在最近一次讀到的清單時送 `toast`。瀏覽器版：帶 `deviceId` 時由伺服器轉送那台（S3，sync-protocol 14.12） |
| `timedSave` | 選填的 `id`（修改；沒有＝新增）、`conversationId`（這台的正式對話）、`newConversation`（布林）、`when`（`{kind, value}`：kind 是 "daily"／"weekdays"＝HH:mm、"every"＝15m、2h（分鐘要整除 60 且至少 5、小時要整除 24）、"at"＝「2026-10-27 14:00」「10/27 14:00」「14:00」、"cron"＝五欄）、`text`、選填的 `label` | 只有桌面：排程頁「新增定時訊息」與明細的「編輯」（S4）。宿主檢查時間、文字（最多 2 萬字）、對話（草稿不行，除非每次開新對話），成功時只對這個視窗送「已儲存定時訊息」的 `toast`、對所有視窗重送 `schedules`，失敗送原因的 `toast`。時間一改，下一次從現在之後算（不補改之前的）。每次開新對話時帳戶、模型、推理強度、權限模式、資料夾照 `conversationId` 那個對話記下。瀏覽器版是 `desktop_only` |
| `timedAction` | `id`、`action`（"run"＝立即送一次，不動下一次的時間／"pause"／"resume"＝繼續，下一次從現在之後算／"delete"） | 只有桌面：排程頁定時訊息的按鈕（S4）。找不到時送 `toast`。瀏覽器版是 `desktop_only` |
| `scheduleAgent` | 選填的 `deviceId`（其他電腦：在那台的管理對話直接送出，可帶 `note`；S3 的遠端指令）、`path`（`task` 是 create 時 null）、`task`（"diagnose"／"explain"／"retime"／"retire"／"custom"＝診斷最近一次失敗／說明它在做什麼／改執行時間／停用並清理／自訂；"create"＝新增排程）、選填的 `accountId`、`folder`、`paneId` | 只有桌面：排程頁的「交給代理」。有管理對話（`entries[].management`）就開它、輸入框預先填好說明（`conversation` 的 `draft`，輸入框是空的才填）；沒有就以 `accountId`（沒給時用上次的、第一個可用的 Claude、第一個可用的）開草稿，資料夾是排程的工作資料夾，記成這個排程的管理對話。說明包含排程的資料、最近的執行、變更紀錄、完整 XML 與固定的規則（改排程用 schtasks 或 ScheduledTasks 模組、刪除與提高權限先問）；不自動送出。`create` 在選的帳戶開草稿、預先填好建立排程的說明。沒有可用的帳戶、找不到排程時送 `toast`。瀏覽器版：帶 `deviceId` 時由伺服器轉送那台（S3，sync-protocol 14.12） |
| `usageSegments` | （無） | 只有桌面：儀表板的「每週額度每 5% 的 token」（2026-10-25，第一節 `usageSegments`）。切到儀表板與在儀表板時每分鐘送；宿主只對送指令的視窗回 `usageSegments`，之後結了新的一筆也主動送給它。只讀；瀏覽器版是 `desktop_only` |
| `openLink` | `href` | 回覆中的連結（網址用預設瀏覽器、本機檔案用預設程式） |
| `openPath` | `path`；〔遠端〕`conversationId`（選用，WP4.6 起桌面的網頁帶） | 用預設程式開本機檔案（白名單外的類型只在檔案總管選取）。〔遠端〕帶的 `conversationId` 是其他電腦的副本時不開，只對送指令的視窗提示副本唯讀（路徑是那台電腦的，WP4.3 的副本白名單） |
| `revealPath` | `path`；〔遠端〕`conversationId`（選用，同 `openPath`） | 在檔案總管中選取。〔遠端〕副本同 `openPath` |
| `saveFile` | `path`；`conversationId`（選用，同 `openPath`） | 「另存新檔」：回覆中的本機檔案連結（`/open?path=`）與燈箱、影片播放器的下載。宿主只收畫面上出現過的本機檔（回覆渲染過的檔案連結、`/media` 登記過的媒體；UNC 一律不收），背景確認存在後開另存對話框（預設檔名是原檔名、取消就什麼都不做），複製到目的資料夾的暫存檔再取代目的檔（原檔不動，寫入失敗時已有的目的檔保留）。成功、失敗都只對送指令的視窗送 `toast`。副本同 `openPath` |
| `copyMedia` | `path`；`conversationId`（選用，同 `openPath`） | 「複製」：圖片與影片的右鍵選單（觸控長按也是，`Web/mediamenu.js`；2026-11-01）。影片一律送（瀏覽器的剪貼簿放不了影片），圖片在網頁自己以 PNG 寫進剪貼簿（`navigator.clipboard.write`）失敗時才送。宿主只收同 `saveFile` 的檔（畫面上出現過的本機檔，UNC 一律不收），背景確認存在後以檔案清單放進剪貼簿（貼到檔案總管或其他程式就是複製這個檔；原檔不動）。成功、失敗都只對送指令的視窗送 `toast`。副本照 `saveFile` 回「唯讀」提示（副本的路徑是另一台的） |
| `copyText` | `text` | 宿主寫入剪貼簿（網頁的 Clipboard API 可能被擋時的備援） |
| `voiceMessage` | `conversationId`；本機（桌面這台的對話）帶 `dataUrl`（`data:audio/…;base64,…`，≤20 MB）與選用的 `wave`（錄音時記下的麥克風音量，一格一個 0～9 的數字、最多 64 格；存在錄音附件上、泡泡裡畫成波形，見第一節附件的 `wave`；遠端不帶），〔遠端〕瀏覽器版與桌面操作其他電腦的對話帶 `uploads`（剛好一筆，同 `addUploaded`；檔名是「語音訊息_yyyyMMdd_HHmmss」加 `.weba`／`.m4a`／`.ogg`／`.wav`） | 輸入區的語音訊息（2026-10-23；`Web/voicemsg.js`、`voicemsgrules.js`）：網頁只錄音（MediaRecorder，WebM／Opus 32 kbps，最長 5 分鐘、短於 0.6 秒不送），不呼叫 Windows 的語音輸入。手機與平板的瀏覽器版（2026-10-24 起）麥克風鈕改用系統的語音辨識（Web Speech API，zh-TW、連續辨識），說的話只寫進輸入框、照一般的 `send` 送出，不送這個指令；瀏覽器沒有語音辨識，或系統不讓瀏覽器用（`service-not-allowed`、`language-not-supported`）時那一頁改回錄音、送這個指令。對話所在的電腦把錄音存進附件（WebM 存成 `.weba`，不當成影片），以本機的 faster-whisper 轉成逐字稿（`Voice/WhisperVoiceTranscriber.cs`：常駐的 Python 程序，GPU 的 large-v3、不行就 CPU 的 small，閒置 10 分鐘結束），再當成一則訊息送進代理：代理收到「這是語音訊息、逐字稿可能有誤、先用文字複誦使用者說了什麼再照做」的說明、逐字稿與錄音檔的路徑（`VoiceMessageRules.AgentText`），畫面、紀錄、同步與佇列看的是「語音訊息（0:12）」＋錄音附件（`DisplayText`）；輸入區原本的附件一起送出；新對話的標題用逐字稿。辨識期間輸入區顯示「正在辨識語音…」、送出被拒（附件處理中）。辨識失敗時錄音留在輸入區的附件並提示原因；沒聽到內容時刪掉錄音、提示再錄一次。桌面只有 `init.voiceInput` 是 true（這台接上了辨識程序）才顯示麥克風鈕。〔遠端〕型錄路由 Owner（層級同 `send`）：那台照 `addUploaded` 下載匯入，那一批處理完才辨識，再照遠端 `send` 送出（同樣的閘門、來源與結果，`done`＝使用者項目已建立或已排進佇列）；那台沒有辨識程序回 `unsupported`，不是一個語音檔回 `invalid`，辨識失敗回 `unsupported`、沒聽到內容回 `invalid`（說明另以 `toast` 給發起的檢視）；要在其他電腦開的新對話草稿與瀏覽器版的伺服器草稿不收（送出第一則之後才能用） |
| `copyItem` | `conversationId`、`itemId` | 代理回覆的「複製」：宿主把這則（Assistant）的 Markdown 原文寫入剪貼簿（網頁只有轉好的 HTML） |
| `popout` | `conversationId` | 在新視窗開這個對話。草稿不能彈出（宿主忽略；網頁對草稿停用這個按鈕與選單項目） |
| `accountAdd` | `provider`（"Claude"／"Codex"／"Antigravity"／"Grok"）、`label`、`color`；〔同步〕`logicalAccountId`（選用） | 新增獨立帳戶（建立隔離的設定資料夾）並立即開始登入。不能新增隔離帳戶的（`ProviderView.isolatedAccounts` 為 false：Antigravity）改為加入本機預設帳戶，檢查登入狀態、沒登入才開始登入；已經有這種帳戶時忽略（宿主送 toast 說明）。〔同步〕帶 `logicalAccountId`（不是 JSON null）＝「在這台登入」目錄裡有、這台還沒有的帳戶（`AccountView.remoteOnly`），`provider`、`label`、`color` 一律不看：宿主依序檢查格式（`la_`＋26 字）、這台已配對、目錄裡有、種類是 Claude 或 Codex、這台還沒有本機帳戶連結到它、也還沒有別的本機帳戶宣告它，通過才以目錄的名稱（照目錄的規則清理，上限 60 字）與顏色建一個隔離帳戶、存下宣告並開始登入（Claude 帶目錄的預期信箱 `--email`；信箱不是單純的 ASCII 信箱就不帶），之後由 `link.upsert` 連結到它（登入的身分不符時標成身分不符，不自動連結到別的）。不合格時只對送指令的視窗送 `toast`（error）說明原因，**不會落回一般新增** |
| `accountLogin` | `accountId`；〔帳戶目錄〕`method`（選用） | 登入或重新登入。〔同步〕Claude 帳戶已對應（目錄的連結）或宣告了目錄的邏輯帳戶時，登入帶目錄的預期信箱 `--email`（不合格的信箱不帶）。〔帳戶目錄〕`method:"deviceCode"`＝Codex 的裝置碼登入（只在桌面、本機發起；`AccountView.deviceCodeLogin` 為 true 時才提供）：宿主以 app-server 取得驗證碼（網址主機一定是 `auth.openai.com`，不合格就取消），顯示在 `accountLogin` 訊息的 `message` 與 `deviceCode`，不開瀏覽器；取消或逾時（最長 15 分鐘）就撤回；不支援、失敗都不退回一般登入。帳戶不是 Codex，或是已登入（狀態不是 notLoggedIn）的系統預設 Codex 帳戶時，直接以 `accountLogin`（`done:true`、`success:false`）說明原因。`method` 沒帶或是 null＝原本的登入；其他值整個指令忽略（宿主記一筆）。使用前要先在 ChatGPT 的安全性設定開啟裝置碼登入。〔遠端〕瀏覽器發起（WP4.8 起，層級 2；網頁一律送 `accountLoginDevice`，兩者在電腦上是同一份處理）：Codex 一定要帶 `method:"deviceCode"`、Claude 不能帶（否則 `invalid`）；Codex 只登 app 受管的隔離資料夾（系統預設、或資料夾不是 app 受管的帳戶回 `policy`，不起 app-server）；已有可用的登入（`ready` 而且 Claude 的登入沒有到期、API Key、檢查中）、這個帳戶正在登入、這台正在登入別的帳戶、CLI 正在更新或沒安裝時回 `conflict`，都在起任何程序之前判斷、什麼都不改；25 秒內 Codex 沒拿到驗證碼、Claude 沒給出登入網址（或開啟登入視窗）就取消這次登入、回 `conflict`（被拒時這個指令新建的帳戶一併移除）。`done`＝Codex 已拿到驗證碼、Claude 已給出登入網址（不是登入完成）。Claude 的登入（2026-10-26 起，`ClaudeLoginRules`）先試 app 內登入：不送訊息的程序以 `claude_authenticate` 取得網址，本機登入開瀏覽器到本機回呼的網址、不開主控台；遠端登入把手動的網址以 `accountLogin` 的 `loginUrl` 給發起的連線，使用者在手機登入後把授權碼以 `accountLoginCode` 貼回來。CLI 太舊、程序起不來時退回主控台的 `claude auth login`（`done`＝登入視窗已開啟，帶目錄的預期信箱 `--email`）。登入完成後那台以信箱比對帳戶目錄的身分（Codex 以 `codex:{信箱}`、Claude 以 CLI 設定檔的信箱；org 不拿來判斷），不同就在同一個資料夾自動登出，那一格的 `statusText` 寫明「上次遠端登入的帳號和目錄不同，已在這台自動登出」（之後在那台查到已登入時清掉，例如在終端機重新登入；登出沒成功時另外寫明、保留到下一次登入或登出，伺服器也會看到身分不符），那台另跳一則系統匣通知。Antigravity、Grok 回 `policy` |
| `accountLoginCancel` | `accountId` | 取消進行中的登入（`loginBusy` 時）：關掉登入視窗、取消 app 內登入；還在排隊等其他帳戶的直接取消。宿主之後送 `accountLogin`（`done:true`） |
| `accountLogout` | `accountId` | 登出。〔遠端〕瀏覽器（WP4.8 起，層級 2）：帶 `deviceId`＝那一台；不帶＝在所有電腦登出（durable：伺服器轉給每台有這個帳戶的電腦，離線的在 7 天內上線才執行，`busy`、`expired` 由伺服器重送——同一條連線先每 5 秒重送 3 次，之後每 60 秒的重掃再送一次，直到完成或 7 天到期；網頁只收到一則 `done` 與伺服器的提示，各台的結果另以提示告訴發起的連線）。電腦回報真的結果：不進同步的種類（Antigravity、Grok）`policy`；那台的 CLI 正在更新、這個帳戶正在那台登入 `busy`（什麼都沒執行）；沒安裝 CLI `conflict`；本來就未登入直接 `done`（不跑 CLI）；其他在這個帳戶的設定資料夾跑 CLI 的登出、最多等 20 秒：結束碼 0 是 `done`，其他與逾時是 `unknown`（可能已經登出）。有執行中的對話照樣登出（網頁的確認框寫明執行中的對話數）；用電腦預設資料夾（`~/.claude`、`~/.codex`）的帳戶，那台的終端機與 VS Code 擴充也會一起登出 |
| `accountUpdate` | `accountId`、`label`、`color`、`safeMode`、`shareCustomizations`（任一可省略） | `shareCustomizations`：隔離帳戶要不要共用系統預設帳戶的全域指示、自動記憶、MCP、skills／agents／commands（登入各自分開；設計 `docs/plans/2026-10-22-shared-customizations.md`）。只對 `ProviderView.sharedCustomizations` 為 true 的 CLI 的隔離帳戶有作用，系統預設帳戶忽略；改了之後下次送出時程序重開才生效，關掉時宿主在背景拿掉指向來源的連結、移回共用前的原件 |
| `accountRemove` | `accountId`、`deleteData`（是否刪除隔離設定資料夾，內含登入憑證與 CLI 對話紀錄） | 網頁先確認；執行中的對話會被停止。〔同步〕已連結到帳戶目錄（`logicalId` 不是 null）時只移除這台的本機帳戶與連結，目錄裡的邏輯帳戶還在（設計 6.7），之後以 `remoteOnly` 回來：確認框先說明，要所有電腦都不再顯示得到網頁版「從目錄刪除」；`devices` 裡沒有其他電腦有這個帳戶時，網頁說「目前沒有電腦登入」而不是「在其他電腦登入過」 |
| `accountReorder` | `ids: [accountId…]` | 側欄拖曳排序。〔同步〕瀏覽器模式：沒列到的接在後面並維持原相對順序，已不存在的 id 略過 |
| `accountRefresh` | `accountId`（null＝全部） | 立即重新檢查登入狀態與用量 |
| `accountOpenFolder` | `accountId` | 用檔案總管開帳戶的設定資料夾 |
| `accountResetCredit` | `accountId` | 使用一次 Codex 的免費重置（2026-10-31；`account/rateLimitResetCredit/consume`，**會真的用掉一次**，網頁先確認）。帳戶不是這台已登入的 Codex、`usage.resetCredits` 是 0、正在使用中、CLI 沒安裝或正在更新時什麼都不做，只對送出的視窗送 `toast` 說明。送出期間 `AccountView.resetCreditBusy` 是 true；結果（已重置、沒有需要重置的額度、沒有次數、發生問題）以 `toast` 告訴送出的視窗，之後宿主以同一個 app-server 重讀用量（不經用量閘門）更新 `usage`。桌面專屬 |
| `cliCheck` | — | 立即檢查每種 CLI（`providers`）的版本與更新。宿主不送「檢查中」狀態，但每個 CLI 檢查完都會更新它的 `checkedAt`（查不到最新版本也會；`lastCheckedAt` 只在查到時更新）：網頁按下後顯示「檢查中…」，等每個 `checkedAt` 都變了才算完成並提示結果。〔遠端〕瀏覽器（帶 `deviceId`）照舊走 WP4.4 的退路（那台收到就開始檢查、回 `done`）：那台開了 CLI 自動更新時，查到新版會接著更新（網頁的按鈕說明寫明）；網頁看 `devicesCli` 的 `checkedAt`、同樣 3 分鐘逾時，被拒時立刻恢復按鈕（WP4.8） |
| `cliInstall` | `provider` | 安裝 CLI（〔熱更新〕`installBroken` 時的「重新安裝」也送這個）。〔遠端〕瀏覽器（WP4.8 起，層級 2，帶 `deviceId`）：只收 Claude、Codex（其他 `policy`）；那台的這種 CLI 正在檢查、安裝或更新時 `conflict`；已安裝而且沒有損壞 `conflict`（要換新版用 `cliUpdate`）；其他開始安裝，`done`＝已開始（結果看 `devicesCli` 的 `installed`、`version`、`checkedAt`）。Claude 的安裝會在那台開看得見的安裝視窗，要有人在那台完成並關掉 |
| `cliUpdate` | `provider` | 更新 CLI。〔遠端〕瀏覽器（WP4.8 起，層級 2，帶 `deviceId`）：只收 Claude、Codex（其他 `policy`）；那台的這種 CLI 正在檢查、安裝或更新，或還沒安裝時 `conflict`（不會落進安裝）；其他照本機的更新開始，`done`＝已開始（結果看 `devicesCli`）：那台執行中的對話在安全點停下、換版後接續，停不下來時要在那台選擇（`cliApplyNow`、`cliContinue`、`cliCancelUpdate` 沒有遠端版本） |
| `cliApplyNow` | `provider`（"Claude"／"Codex"／"Antigravity"／"Grok"）或 `conversationId`，擇一 | 〔熱更新〕「立即套用 CLI 更新」：不等安全點，立刻以目前安裝的版本重開（被打斷的工具結果未知，接續訊息會說明；待回應的卡片作廢，代理會以新的請求重提）。`provider`＝這種 CLI 所有待重開或還在跑舊版的對話（CLI 卡的按鈕）；更新屏障排空中（`CliView.update.phase` 為 `draining`）就是「現在中斷」。`conversationId`＝只有這個對話（`Header.restart.canApplyNow` 的按鈕）。沒有可套用的就忽略 |
| `cliContinue` | `conversationId` | 〔熱更新〕「繼續」（`Header.restart.canContinue`）：換版後沒有自動接續的對話，宿主送出接續訊息（不顯示成使用者訊息，對話加一則 Notice：`noticeKind: "cliUpdate"`；app 更新後、app 重新啟動後沒有接續的對話是 `"appUpdate"`），之後跟一般的一輪一樣。對話不在可繼續的狀態就忽略 |
| `cliCancelUpdate` | `provider` | 〔熱更新〕更新屏障排空中（`CliView.update.phase` 為 `draining`）的「取消更新」：不跑更新，已停下的對話以目前的版本重開並接續。換檔已開始（其他 phase）就忽略——npm 半途不能中斷 |
| `draftSnapshot` | `requestId`、`drafts: [{conversationId, text}]` | 回應 `draftSnapshotRequest`：這個視窗每個開著的對話（含藏在後面的分頁）輸入框裡還沒送出的字，空白的不列。宿主存進 `resume.json`，新版啟動後以 `conversation.draft` 還回來 |
| `appUpdateCheck` | — | 設定頁「立即檢查」app 的新版（結果看 `appUpdate`） |
| `appUpdateRestartNow` | — | 「立即重啟」：已備妥的新版不等使用者閒置（也不等開著 Claude Remote Control 的對話沒有動靜；自動開始時這也算使用者在用，`blocker` 會說明）、也不管取消後的冷卻，馬上開始套用（排空、換版、重啟後接續）；CLI 正在更新、登入視窗開著、正在安裝到這台電腦時仍等它結束 |
| `appUpdateForce` | — | 「強制更新」（2026-10-28 起）：同「立即重啟」，但不等背景工作結束（「立即重啟」與自動套用都等背景工作都結束，`blocker` 說明）；排空中按下就把這次改成強制（交棒不提供）。對話照常陸續到安全點停下，按下後滿 10 分鐘還沒停下的（在等背景工作、前景的指令、你的回應）一律照「立即套用」停下，被停掉的背景工作新版會請代理確認。CLI 正在更新、登入視窗開著、正在安裝到這台電腦時仍等它結束；已經強制或不在可以開始的階段就忽略 |
| `appUpdateCancel` | — | 排空中的「取消這次」：放棄這次套用（已停下的對話就地接續），6 小時內不再自動套用；還沒開始排空時只延後 6 小時 |
| `appInstallHere` | — | 「安裝到這台電腦」（`AppUpdateView.canInstall` 時）：複製到受管理的安裝位置、建立捷徑與移除項目，啟動安裝好的那份後結束自己。不能安裝或已在安裝就忽略 |
| `setting` | `key`、`value` | 改 app 設定（見 Settings；含 app 自我更新的 `autoUpdateApp`）。〔同步〕瀏覽器模式只接受四個漫遊鍵（`theme`、`fontSize`、`enterToSend`、`notifications`），桌面改這四個時也同步到其他電腦 |
| `deviceRename` | `deviceId`、`name`（1～60 字） | 〔同步〕改裝置的顯示名稱（配對時的名稱不變）。桌面經同步送出（`device.update`）、瀏覽器由伺服器直接套用；結果以 `devices` 回來 |
| `collabCreate` | `requestId`、`userName`（3～32 個英數字與 `._-@`）、`displayName`（1～32 字）、`password`（8～128 字） | 多人協作（2026-10-30，docs/plans/2026-10-30-collaborators.md）：擁有者新增一位協作人。宿主只對這個視窗回 `collabResult`；清單的變動以 `collaborators` 推回來。桌面經同步送給伺服器（裝置 hub 的 `CollabCmd`，sync-protocol 14.14），瀏覽器版由伺服器處理（分類照樣是 not_available，由第 4 階段的處理者接手）；協作人送了回 `invalid` |
| `collabUpdate` | `requestId`、`userId`、選填的 `displayName`、`disabled`（布林）、`password`（重設密碼；null＝不改） | 多人協作：改名稱、停用或啟用、重設密碼。停用與重設密碼時伺服器撤銷那位所有的工作階段（他被登出）。回覆同 `collabCreate` |
| `collabDelete` | `requestId`、`userId` | 多人協作：刪除協作人（Identity 帳號、名冊與分享一起刪，撤銷他的工作階段；對話裡他送過的訊息照留）。回覆同 `collabCreate` |
| `collabShare` | `requestId`、`conversationId`、`userIds`（整份取代，≤100 位） | 多人協作：這個對話分享給誰（對話的「⋯」選單「協作人…」）。對話要已經同步到伺服器（桌面的對話 id 由宿主換成線上的 id；不在伺服器的對話目錄回 `not_found`）。取消分享時伺服器關掉那位開著的分頁（`conversationClosed`）、從他的清單拿掉。回覆同 `collabCreate` |
| `syncPair` | `deviceName`；`server`（選用，預設 `https://hub.example.com`，開發時指向本機的 MyriagentHub） | 〔同步〕只有桌面：開始配對（產生裝置金鑰、取得配對碼），進度看 `sync.pairing`。已配對就忽略 |
| `syncCancelPair` | — | 〔同步〕只有桌面：取消進行中的配對 |
| `syncUnpair` | — | 〔同步〕只有桌面：解除配對（刪除權杖與信任資料、停止同步；本機的帳戶與對話不動）。網頁先確認，並提示到網頁撤銷這台 |
| `syncPause` | `paused`（布林） | 〔同步〕只有桌面：暫停／恢復同步（暫停時不連線，outbox 照常累積） |
| `syncRetry` | — | 〔同步〕只有桌面：「立即重試」（略過退避等待；因權杖撤銷、要重新配對而停止的不會重試） |
| `syncConfirm` | `kind`（`hubIdentity`）、`fingerprint` | 〔同步〕只有桌面：使用者比對過指紋後確認 `sync.confirm` 的事（伺服器身分金鑰換了）。`fingerprint` 必須等於 `sync.confirm.fingerprint`，否則忽略 |
| `syncSetPolicy` | `remoteControlLevel`（`Off`／`View`／`Control`）、`allowRemoteFullAccess`（布林），任一可省略；〔遠端〕`notifyRemoteActions`（布林，WP4.6 起）；〔遠端執行指令〕`allowRemoteExec`（布林，2026-10-25 起；不是布林時整個指令忽略） | 〔同步〕只有桌面：這台的遠端操作政策（只能在這台改；馬上生效）。〔遠端〕預設 `Off`；改成 `Control`、或打開 `allowRemoteFullAccess` 時網頁先跳確認框，說明知道擁有者密碼或拿到瀏覽器 cookie 的人能做什麼（設計 14.11 D26）。`notifyRemoteActions`（預設 true）：遠端執行的層級 1、2 動作在這台顯示通知（網頁自動送的 `listFiles`、`fileSearch` 只記錄、不通知，WP4.7 起）。`allowRemoteExec`（預設 false）：允許其他電腦對話裡的代理以 `myriagent-remote` 在這台執行主控台指令（docs/sync-protocol.md 14.11；另要 `Control`，打開時網頁先跳確認框；高風險的指令另在這台跳確認視窗） |
| `setConversationDefault` | `provider`（"Claude"／"Codex"／"Antigravity"／"Grok"）、`key`（"model"／"permission"／"effort"）、`value`；effort 另帶 `model`（模型的 value，"default"＝帳戶預設模型） | 設定頁「模型設定」。model 的 value 為空字串（或 null）＝沿用上次選擇，其他值要在這一家的 `models` 裡（"default"＝帳戶預設模型）；permission 只收這一家 `permissionModes` 裡的值；effort 的 value 為 "default" 或空字串＝清除（CLI 預設），其他值要在那個模型的 `efforts` 裡；`key` 為 "autoCompact"＝自動壓縮上限（value 是 token 數，可帶 k／m 與千分位，例如 "400k"；空字串＝CLI 預設），只收有 `autoCompactRange` 的代理；"autoCompactPercent"＝自動壓縮門檻（上下文視窗的百分比，整數可帶 %，例如 "70"），只收有 `autoCompactPercentRange` 的代理；超出範圍的夾進範圍。不合法就忽略（不重送設定）；成功後宿主廣播 `settings`。已開的對話不受影響（autoCompact、autoCompactPercent 除外：下一則訊息起套用，程序重開） |
| `addUploaded` | `conversationId`、`uploads: [{uploadId, name, size, sha256}]` | 〔遠端〕只有瀏覽器：把已經 `PUT /api/v1/uploads/{uploadId}` 上傳到伺服器的檔案加進輸入區的附件（設計 4.10；取代瀏覽器模式的 `addFiles`／`pasteImage`）。擁有者電腦下載後照 `addFiles` 處理，上傳的檔只會成為附件、永遠不會被執行。WP4.7 起開放（層級 1），之前 `not_available`；桌面不會收到 |
| `accountLoginDevice` | `logicalAccountId`、`deviceId`；`method`（選用，同 `accountLogin`） | 〔遠端〕只有瀏覽器：在指定的電腦登入目錄裡的帳戶（設計 6.4）：那台已有連結的本機帳戶就 `accountLogin`，沒有就照 `accountAdd {logicalAccountId}` 建立再登入。裝置碼登入的驗證碼經 `accountLogin` 訊息的 `deviceCode` 只回給發起的連線（第一節 `accountLogin`）。登入方式、前置條件、`done` 的意思（Codex 拿到驗證碼、Claude 給出登入網址或登入視窗已開啟）與 25 秒的期限同 `accountLogin` 的〔遠端〕：Codex 帶 `method:"deviceCode"`、Claude 不帶；那台還沒有這個帳戶時新建的帳戶不自動登入，被拒時移除（`rejected` 一定什麼都沒改）。完成後那台以信箱比對帳戶目錄的身分，不同就在同一個資料夾自動登出，那一格的 `statusText` 寫明原因（設計 14.18；取代 14.11 D23 的「標成身分已變更、本機確認前不用它開對話」）。WP4.8 起的桌面處理（層級 2）：伺服器照型錄轉給指定的電腦（`Cmd` 收下，WP4.5），輸出的 `accountId` 換成 `logicalAccountId`；更舊的桌面回 `commandResult` 的 `rejected`／`unsupported`（那台的政策不允許時先回 `policy`）。桌面的網頁不會送它 |
| `accountLoginCode` | `logicalAccountId`、`deviceId`、`code`（≤2048 字） | 〔遠端〕只有瀏覽器（2026-10-26）：Claude 的遠端登入在那台給了登入網址（`accountLogin` 的 `loginUrl`）之後，把登入頁上顯示的授權碼（「授權碼#state」整串或只有前半）交給那台進行中的 app 內登入（`claude_oauth_callback`）。型錄的 Device 指令、層級 2（同 `accountLoginDevice`）。`done`＝CLI 收下了授權碼（之後那台照常收尾、比對身分、重新檢查，網頁照 `loginBusy` 與那一格的狀態看結果）；那台沒有這個帳戶進行中的 Claude 登入、state 和這次的不同、授權碼不對或過期（CLI 回 400，這次登入隨即結束）回 `conflict` 與原因。授權碼不進記錄檔與同步 payload（和其他指令的參數一樣留在信封裡；只能用一次、沒有那台 CLI 的 PKCE verifier 換不到權杖）。網頁在帳戶卡的「打開 Claude 登入頁」連結旁邊給輸入框與「送出授權碼」（`remoterules.claudeLoginUrlView`、`loginCodeInput`）。桌面的網頁不會送它 |
| `fileFetch` | `requestId`、`conversationId`、`path` | 〔遠端〕只有瀏覽器：預覽或下載對話工作資料夾（或對話的 `attachments` 資料夾）裡的非媒體檔；擁有者電腦回 `fileRef`（第一節）。範圍與拒絕清單照設計 8.7 的 `file`（`RemoteFolderRules.IsForbidden`）；可能是文字的檔（文字類副檔名、開頭 64 KB 是合格的 UTF-8 而且沒有 NUL、或有 UTF-16／UTF-32 的 BOM）整份掃權杖、單檔 ≤20 MB，開頭就證明是二進位的（簡報、圖片、壓縮檔…）不掃、單檔 ≤512 MB，層級 2、政策 View 不給。瀏覽器版回覆中的本機檔案連結（`href="#local-file=…"`，第三節 Item 的 `html`）也送這個指令。WP4.7 起開放，之前 `not_available`；桌面不會收到 |
| `syncSetConversationPolicy` | `conversationId`、`policy`（`full`／`metadata`／`localOnly`） | 〔遠端〕只有桌面：這個對話的同步政策（設計 14.11 D37；沒設過＝`full`）。`metadata` 只同步標題與狀態、`localOnly` 什麼都不送（連標題都不送）。`paused` 只由權杖 DLP 命中時設定（設計 8.8），不能用這個指令設；選 `full`、`metadata`、`localOnly` 就是解除。Antigravity、Grok 的對話一律 `localOnly`（不收其他值）。已上傳過的對話改成 `localOnly`（含 Antigravity、Grok 的對話）等於從伺服器刪除，之後選 `full`、`metadata` 會被拒（跳 toast「這個對話已從伺服器刪除，不能再同步；要同步請開新對話」），不能再同步。草稿、副本、找不到的對話略過（沒有回覆）。WP4.3 起處理 |
| `syncLoadRemoteAudit` | `before`、`beforeSeq`、`limit`（都可省略，意思見第一節 `remoteAudit`） | 〔遠端〕只有桌面：讀這台遠端操作紀錄的一頁，宿主回 `remoteAudit`（只給送指令的視窗）。參數不合格整個指令忽略、不回覆。WP4.6 起處理 |
| `syncExportRemoteAudit` | — | 〔遠端〕只有桌面：把這台的遠端操作紀錄匯出成檔案（設定頁的「匯出」），宿主回 `remoteAuditExport`（只給送指令的視窗）。WP4.6 起處理 |
| `syncListTrash` | — | 〔遠端〕只有桌面：列出這台的對話回收區，宿主回 `trash`（只給送指令的視窗）。配對與否都可以（解除配對之後回收區照樣在）。WP4.6 起處理 |
| `syncRestoreTrash` | `conversationId` | 〔遠端〕只有桌面：從回收區還原這個對話（只在這台，不再發布）。成功、失敗各對送指令的視窗送一則 `toast`，之後重送 `trash`；清單裡已有同一個 id、對話資料夾已存在、回收區沒有它時失敗。`conversationId` 不是字串就整個忽略。WP4.6 起處理 |

〔同步〕`syncPair`、`syncCancelPair`、`syncUnpair`、`syncPause`、`syncRetry`、`syncConfirm`、`syncSetPolicy`（〔遠端〕以及 `syncSetConversationPolicy`、`syncLoadRemoteAudit`、`syncExportRemoteAudit`、`syncListTrash`、`syncRestoreTrash`）只接受桌面本機 WebView 的 view；瀏覽器模式的伺服器一律拒絕（`desktop_only`），遠端指令的閘門也一律不收（設計 14.11 D11 的 5a）。

〔遠端〕第 5 階段的 `takeover`（換機接手）、`handoffCancel` 還不在這張表：瀏覽器模式回 `not_available`（第六節第 9 點），桌面不會收到。形狀見第七節〔換機〕（WP5.5 起加進這張表）。

〔遠端〕瀏覽器模式的遠端指令（第 4 階段由伺服器轉送給擁有者電腦的 `send`、`approve` 等）另有三條規則（設計 14.11、`docs/sync-protocol.md` 第 14 節）：
- 結果以 `commandResult`（第一節）只回給發起的連線；指令執行時中樞回給「這個視窗」的訊息（`sendRejected`、`toast`、`files`、`fileSearch`、`accountLogin`）也只回給發起的連線。
- `approve`、`approvePlan`、`answer` 要原樣帶回卡片的 `requestDigest`（瀏覽器模式與桌面副本的 Item 才有，第三節 Item）：沒帶或與電腦上目前的卡片不同時回 `stale`（卡片已被別處處理或內容變了），本機 WebView 照舊不必帶。
- 遠端不寫這台電腦的持久設定：`setOption` 只改這個對話（不記成帳戶下次的模型）、`setFolder` 不寫進最近資料夾、`accountRemove` 一律保留資料夾（`deleteData` 當成 false）、新對話的工作資料夾不取最近資料夾（設計 14.11 D15）。

〔桌面遠端操作〕（`docs/sync-protocol.md` 14.7；擁有者 2026-10-22 決定「已配對就能操作」）桌面網頁對**副本**（其他電腦的對話）送型錄裡路由 Owner 的指令、或對其他電腦送路由 Device 的帳戶與 CLI 指令（`deviceId` 不是這台）時，`store.js` 的 `post` 先帶上 `commandId`（ULID，`liverules.wantsPeerId`），`post` 回傳它；宿主照型錄（`PeerHubRules.Forwardable`）交給同步經伺服器送給那台，不再一律唯讀擋掉（沒帶 `commandId` 的照舊擋下並提示唯讀）。結果與輸出照瀏覽器模式的三條規則：`commandResult`、`sendRejected`、`toast` 與型錄的輸出白名單只回給發起的視窗（宿主等結果 160 秒、結果到了之後再收 30 秒的輸出）；副本的卡片 Item 帶 `requestDigest`（第三節 Item），`approve`、`approvePlan`、`answer` 要原樣帶回；遠端不寫那台的持久設定。網頁以 `sync.peer`（第一節 `sync`）決定副本能不能操作：能操作時送出、卡片、權限選單的閘門比照瀏覽器版的遠端對話（那台離線、政策不是 Control、完全自動沒允許時擋下並說明），還沒連上時擋下並說明，伺服器或這台不支援時照舊唯讀。

### Codex 對話工具（2026-10-26）

`codexControl` 的 `action` 為下列其中一種；回應 `{type:"codexControl", conversationId, requestId, result}`，失敗改帶 `error`。耗時操作以 `header.codex.busy` 表示，同一個對話一次只接受一個控制請求。

- `inspect`：讀取目前模型的 `tiers`、工作資料夾的 `skills`、已存在對話的 `goal`。個別查詢失敗回 `tiersError`／`skillsError`／`goalError`；技能載入警告為 `skillErrors`。不呼叫 `thread/resume`，不開始模型工作。
- `goalSet {objective,tokenBudget?}`：1～4,000 字，預算為正整數。留空沿用既有預算，新目標無上限。`goalResume` 繼續、`goalPause` 暫停並中斷目前這一輪、`goalClear` 清除但不停止正在進行的一輪。只有已送過訊息的正式對話能操作；執行中只能暫停或清除。
- `skillSelect {paths:[...]}`：最多 12 個，由目前 cwd 的 `skills/list` 重新驗證，回 `selectedSkills:[{name,path,...}]`。停用或消失的項目可以在面板取消。持久化於對話 metadata，下一輪以 `type:"skill"` 傳給 CLI。
- `speed {serviceTier}`：標準為 `default`，其他值必須在目前模型回報的清單中。回 `serviceTier` 與 `tiers`；保存於對話 metadata，下一輪起生效。初始 null 表示未覆寫 CLI 設定，操作時 null 正規化為標準。

本機 Codex `header` 增加 `sessionStarted` 與 `codex:{goal,busy,serviceTier,selectedSkills}`。`goal` 是 CLI 回報的完整目標（`objective/status/tokenBudget/tokensUsed/timeUsedSeconds` 等），沒有是 null；`thread/goal/updated`／`cleared` 即時更新。目標本身由 Codex 保存，重新開面板會查詢，app 不另存一份當作權威狀態。

輸入列「Codex 工具」也提供審查與壓縮：沿用 `send` 與一輪的執行、停止、錯誤處理；CLI 層把 `/compact` 轉為 `thread/compact/start`，`/review` 轉為 `review/start`（inline）。`/review commit <sha>`、`/review base <branch>`、`/review <說明>` 分別使用原生 commit、baseBranch、custom 範圍。禁止附件與插入執行中的一輪；有 active 目標時先提示暫停，避免載入舊 thread 就自動續跑。介面操作保留正在撰寫的草稿。

Codex 子代理用既有 `toolKind:"agent"` 工具卡顯示（id 前綴 `codex-child:`）：標題帶名稱、模型及推理強度，展開可看任務、文字、指令及輸出。狀態來自子代理的 turn／activity，主對話的停止 id、結束與每輪用量仍獨立；帳戶 token 累計照原本分模型計數。

## 三、資料形狀

### AccountView
```jsonc
{
  "id": "a1",
  "provider": "Claude",                 // "Claude" | "Codex" | "Antigravity" | "Grok"
  "label": "工作用 Max",
  "color": "#D97757",                   // 帳戶代表色（分頁、卡片、圓環用）
  "isSystemDefault": true,              // 使用這台電腦預設的設定資料夾（~/.claude、~/.codex），不是 app 建立的隔離帳戶
  "configDirectory": "C:\\Users\\U\\.claude",
  "email": "me@example.com" | null,
  "plan": "Claude Max" | "Pro" | null,
  "status": "ready",                    // "checking" | "ready" | "notLoggedIn" | "apiKey" | "notInstalled" | "error"
  "statusText": "已登入",               // 給人看的狀態說明
  "safeMode": false,                    // 精簡模式：不載入外掛、hooks、MCP、CLAUDE.md（ProviderView.safeMode 為 false 的 CLI 一律 false，網頁不顯示這個選項）
  "shareCustomizations": true,          // 隔離帳戶共用系統預設帳戶的設定（預設 true）；系統預設帳戶、ProviderView.sharedCustomizations 為 false 的 CLI 一律 false。帳戶選單的「共用預設帳戶的設定」只在 sharedCustomizations 為 true 的 CLI 的隔離帳戶顯示（只有桌面）
  "resetCreditBusy": false,             // 正在使用 Codex 的免費重置（accountResetCredit）；瀏覽器版沒有這個欄位
  "loginBusy": false,                   // 登入流程進行中（網頁的登入鈕改成「取消登入」，送 accountLoginCancel）；登入後的重新查詢做完才變回 false（WP4.8）。〔同步〕瀏覽器模式是所有電腦的 Any（看不出是哪台）
  "deviceCodeLogin": false,             // 〔帳戶目錄〕帳戶選單可以多一項「用裝置碼登入（可用手機核准）」（送 accountLogin {method:"deviceCode"}）：Codex 的隔離帳戶，或狀態是 notLoggedIn 的系統預設 Codex 帳戶，而且不在登入中；其他種類一律 false。只有桌面
  "loginExpiresAt": 1794000000000 | null, // 登入的到期（毫秒）：Claude 憑證檔的 refreshTokenExpiresAt，只在 status 是 ready 時給；其他種類一律 null。
                                        //   CLI 換發存取權杖不會延後它，重新登入（accountLogin）才重新起算。網頁（loginrules.js）在帳戶頁的卡片寫出到期日，
                                        //   剩 3 天內側欄帳戶卡多一個琥珀色標示（已到期紅色），點一下送 accountLogin。只有桌面（瀏覽器版沒有這個欄位，看 devices 的 loginExpiresAt）
  "usage": Usage | null,
  "sessions": { "open": 3, "running": 1, "waiting": 0, "background": 1, "programs": 1, "subagents": 2 },  // 這台的對話數（桌面的同步把它當這台的數字上傳，link.upsert）；waiting＝有卡片等使用者回應的對話數（判定同 ConversationSummary.waiting）；
                                        //   subagents（2026-10-28）＝執行中的子代理「個數」（不是對話數）：執行中的 agent 種類工具卡（Claude 的 Agent、Codex 的子代理、
                                        //   Antigravity 的 invoke_subagent）與 Claude 還沒結束的 local_agent 工作（在背景跑的子代理：工具卡一開始就結束了）的聯集，以 tool_use_id 對齊
                                        //   （Hub/SubagentCountRules.cs）；程序結束或換了會話就歸零。側欄帳戶卡在執行中後面以青色的子代理圖示＋個數顯示、儀表板大卡列「子代理 N」；
                                        //   舊版的宿主與伺服器沒有這個欄位，網頁當成 0；
                                        //   background＝代理這一輪已回覆完畢、背景工作還在跑的對話數（不在執行中、ConversationSummary.background 不是 null；2026-10-24 起，
                                        //   舊版的宿主與伺服器沒有這個欄位，網頁當成 0）。側欄帳戶卡在執行中與等你回應之間以虛線圈＋數字顯示
                                        //   programs（2026-10-24）＝這一輪結束了、但代理開的程式脫離 CLI 還在跑（背景程式）的對話數（執行中的不算）：app 掃描程序找到
                                        //   帶這個對話標記、不在 CLI 底下的主控台程式（CLI 的環境變數 MYRIAGENT_AGENT_CONVERSATION＝對話 id，代理以 nohup、Start-Process
                                        //   開出的程序都繼承；每 15 秒掃一次、一輪結束時立刻補掃，只讀，Background/BackgroundProgramRules），而且撐過了一輪的結束
                                        //   （一輪結束之後才開始的掃描裡還在；Git Bash 經 timeout、env 開的前景指令和 CLI 的父子關係會斷掉，看起來也像脫離了 CLI）。
                                        //   CLI 自己追蹤的背景工作算在 background、不算這裡（命令列對得上 CLI 回報的背景工作的也不算，IsCliTask）；
                                        //   同一個對話兩種都有時兩個都算。舊版沒有這個欄位，網頁當成 0
  "programs": [ { "conversationId": "…", "title": "開發工作六", "programs": ["bash chain7.sh"], "more": 0 } ],
  "subagentList": [ { "conversationId": "…", "title": "平行查三件事", "count": 2 } ],
                                        // 有執行中子代理的對話（第十一批 2026-11-08；只有 sessions.subagents > 0 才帶）：依子代理數多到少、一樣多時新的在前，最多 5 個；
                                        //   帳戶卡的子代理徽章滑過列出（remoterules.subagentsTooltip），其他電腦的只有 otherSessions.subagents 的個數。只有這台的

                                        // 有背景程式的對話（依更新時間新到舊；只有 sessions.programs > 0 才帶）：programs 是程式的命令列
                                        //   （執行檔去掉路徑與 .exe，最多 80 字；每個對話最多 4 個，more＝還有幾個沒列）。側欄帳戶卡名稱那一行在等你回應之後有
                                        //   靜態的終端機徽章（sessions.programs＋otherSessions.programs），滑過列出這些（remoterules.js 的 programsTitle）。只有桌面

  // 〔同步〕（未配對時省略）
  "logicalId": "la_…" | null,           // 連結到的邏輯帳戶；null＝這個本機帳戶還沒連結（身分未知或不符）
  "remoteOnly": false,                  // true＝目錄裡有、這台沒有本機帳戶（側欄灰色「這台未登入」）：id 等於 logicalId、status 為 notLoggedIn、
                                        //   configDirectory 為 null、usage 是共享的用量；「在這台登入」送 accountAdd {logicalAccountId}
  "devices": [AccountDeviceView] | null, // 帳戶 × 電腦矩陣的這一列（儀表板）；沒連結為 null
  "otherSessions": { "open": 2, "running": 1, "waiting": 1, "background": 0 } | null
                                        // 其他電腦上這個帳戶的對話數合計（2026-10-23）：目錄裡各台最後一次回報的 sessions，不算這台、撤銷的與離線的
                                        //   （離線的那台數字停在當時）；沒連結為 null。側欄帳戶卡的執行中、等背景工作回報、等你回應顯示 sessions＋otherSessions
                                        //   （remoterules.js 的 sessionTotals，滑過的說明寫其中幾個在其他電腦）；儀表板的帳戶大卡（含開著，執行中帶動卡片的
                                        //   執行中動畫）與摘要的對話數也是（2026-10-24 起；以前只看 sessions，其他電腦在跑時大卡不動）。
                                        //   登出、移除等只影響這台的確認照舊只看 sessions。只有桌面
}
```
〔同步〕瀏覽器模式的 `accounts` 一律是邏輯帳戶（`id` 等於 `logicalId`、`remoteOnly` 為 false、`isSystemDefault` 為 false、`configDirectory` 為 null），帳戶在各台的實際狀態看 `devices`。瀏覽器模式的 `devices` 一律是陣列（每台未撤銷的電腦一格，沒有本機帳戶的是 `absent`）、`safeMode` 為 false；`status` 的彙總規則見第六節。瀏覽器模式的 `sessions` 是各台的合計，只算在線的電腦（2026-10-23 起；同桌面的 otherSessions；`background`、`programs`、`subagents` 也是），沒有 `otherSessions` 與 `programs` 的說明（側欄的徽章只寫個數）。

### Tokens
側欄帳戶卡的 token 計數（宿主的 `TokenTallyStore`，存在 `usage-tokens.json`、保留 30 天）：這台電腦上 app 經手的對話用掉的 token，只記 CLI 回報的真實用量
（Claude 主對話每次 API 呼叫的 `message_start`／`message_delta`、子代理讀它的逐字稿即時記、一輪結束時只補 `modelUsage` 裡兩者都看不到的呼叫；Codex `thread/tokenUsage/updated` 的 total 差，含子代理；
Antigravity 步驟的 usage；Grok 一輪結束的 usage）。每一筆只由會話發一次，中樞不以一輪的用量補差額（2026-10-25 以前會，中樞先收尾的那一輪會被再記一次）；算不出增量時寧可少算。直接在終端機用 CLI 的看不到。和 CLI 本機紀錄的核對見 protocol-notes「側欄 token 計數的核對」。
```jsonc
{
  "day": "2026-10-22",                  // 計數依這個本機日期（today 是這一天）
  "accounts": {                         // 本機帳戶（AccountView.id）→ 計數；沒有的帳戶當成 0
    "a1": {
      "today": TokenCount,              // 今天
      "week": TokenCount,               // 近 7 天（含今天）
      "month": TokenCount,              // 近 30 天（含今天）
      "here": TokenCount                // 〔同步〕這台今天的；有它＝today／week／month 已加上其他電腦的（所有電腦的合計），沒有＝只有這台
    }
  }
}
```
〔同步〕桌面收到過伺服器的其他電腦計數（`tok2`，sync-protocol 14.10）之後（2026-10-23）：連結到帳戶目錄的本機帳戶，三個數字都加上其他電腦這個邏輯帳戶的格（天數依這台的本機日期），並帶 `here`；沒連結的照舊只有這台；
remoteOnly 的邏輯帳戶（鍵是邏輯帳戶 id）在其他電腦有格時也列（`here` 是 0；卡片照舊顯示「這台未登入」，其他電腦今天的 in／out 寫在滑過的提示）。不設 `scope`（網頁照 `here` 判斷）。
伺服器沒有 `tok2`（回滾）、還沒連上時沒有 `here`，和以前一樣只算這台；連線斷了沿用最後收到的其他電腦計數（計數只會變大）。滑過的說明多一行「其中這台電腦：in …・out …」，最後一行寫「所有電腦上 Clode Myriagent 的對話合計」。
TokenCount：`{ "input": 1226418, "cached": 1018772, "output": 57913 }`——`input` 含快取（Claude 是 input＋cache_creation＋cache_read），`cached` 是其中的快取讀取；總數＝`input`＋`output`。
〔遠端〕瀏覽器版的 Tokens 由伺服器組（`TokenTallySyncRules.View`，sync-protocol 14.9）：多一個 `"scope": "all"`（各電腦的合計），`day` 是伺服器的本機日期，`accounts` 的鍵是邏輯帳戶（＝瀏覽器的 `AccountView.id`），只列 30 天內有電腦回報過的帳戶；只算協商出 `tok1` 的桌面（更新前的版本不回報）。網頁對 `scope:"all"` 只在帳戶有列出時顯示 token 列（沒列的照舊顯示方案名稱，不顯示 0），滑過的說明寫「所有電腦上 Clode Myriagent 的對話合計」。
網頁（`tokenrules.js`、`tokenfx.js`）：已登入的本機帳戶在狀態那一行（原本方案名稱的位置）顯示今天的 in（`input`，含快取）與 out（`output`）兩個數字（卡片與滑過的說明都不寫「今日」兩字）（藍色等寬字；`fmtShort` 精簡格式：1 萬以下千分位、以上 4 位有效數字加 K／M／B，無條件捨去，最多 6 個字），同一行還有過時標示或各電腦小點而放不下 in／out 標籤時（容器查詢）收起標籤、改用斜線隔開（`1.226M/57.91K`）；精確值、快取讀取、近 7／30 天，以及 Grok 的用量在一輪結束時才計入，寫在滑過的說明。方案名稱只留在滑過卡片的提示（卡片背景不墊方案名稱）；沒登入、出錯時那一行照舊顯示狀態。數字變了只把變動的位數先顯示成方塊亂碼（不用數字；小數點、千分位、單位不變成亂碼）、再由左到右逐位鎖定成新值，旁邊不另外顯示增量，不顯示推估或內插的數字；有對話在執行時數字底下有一道藍光掃過（系統要求減少動態時都沒有）。

### ProviderView
支援的一種 CLI 與它能做的事（宿主的 `AccountRules.SupportsIsolatedAccounts`／`SupportsSafeMode`／`SupportsSharedCustomizations`）。
```jsonc
{
  "key": "Antigravity",                 // 與 AccountView.provider、cli 的鍵相同
  "name": "Antigravity",                // 產品名稱（"Claude Code"／"Codex"／"Antigravity"／"Grok Build"）
  "isolatedAccounts": false,            // 能新增隔離帳戶（各自的設定資料夾與登入）；false＝只有本機預設帳戶，一台電腦一個（agy 的登入存在 Windows 認證管理員）
  "safeMode": true,                     // 有精簡模式（Claude、Codex、Antigravity；Grok 為 false）
  "sharedCustomizations": false         // 隔離帳戶能共用系統預設帳戶的設定（Claude、Codex、Grok；Antigravity 沒有隔離帳戶）
}
```
〔同步〕瀏覽器模式只列 Claude、Codex，`safeMode` 一律 false，不帶 `sharedCustomizations`（網頁當成 false）。

### AccountDeviceView〔同步〕
帳戶 × 電腦矩陣的一格（設計 6.8；伺服器組成、以 `account.upsert` 發布）。
```jsonc
{
  "deviceId": "d_…",
  "deviceName": "OFFICE-PC",
  "self": false,                        // 這一格是目前這台（桌面）
  "status": "ready",                    // 同 AccountView.status；那台沒有對應的本機帳戶為 "absent"（「尚未加入」）
  "statusText": "已登入",
  "email": "me@example.com" | null,
  "plan": "Claude Max" | null,
  "accessExpiresAt": 1791580000000 | null,   // Claude 存取權杖到期（「權杖待更新」）
  "loginExpiresAt": 1791580000000 | null,    // Claude 登入到期（≤3 天時「登入即將到期」）
  "identityOk": true,                   // false＝登入的身分不是這個邏輯帳戶（「身分不符」）
  "usagePoller": true,                  // 這台負責查這個帳戶的用量
  "online": true,                       // 那台電腦目前連著伺服器；離線時其他欄位是最後已知的狀態
  "checkedAt": 1791580000000 | null
}
```

### Usage
```jsonc
{
  "fetchedAt": 1791580000000 | null,    // 這份資料取得的時間；從沒查到過為 null
  "stale": false,                       // true＝資料可能過時（權杖過期、查詢失敗），畫面要標示；fetchedAt 為 null 時沒有資料可標示，只顯示 error
  "source": "api",                      // "api"（官方用量端點，含 Antigravity `agy -p "/usage"`、Grok `grok usage`）| "session"（對話程序回報）| "cache" | "none"（沒有額度可查，只有 note，fetchedAt 為 null）
                                        //   | "shared"〔同步〕（別台查到、經伺服器共享的；這台沒被指派查詢時顯示它，sync-protocol 6.4、12.5）
  "error": null,                        // 最近一次查詢失敗的原因
  "windows": [UsageWindow],             // 依重要性排序：第一個是主要時間窗
  "credits": "12345.68" | null,         // Codex 額外點數餘額
  "note": null                          // 其他說明（例如「已啟用額外用量」、Codex 額度被擋的原因「工作區的點數已用完」）
}
```

### UsageWindow
```jsonc
{
  "key": "session",                     // Claude："session"（5 小時）| "weekly" | "weekly:Fable"（依模型）；Codex："{limitId}:{分鐘數}"，例如 "codex:10080"（每週）、"codex:300"（5 小時）；Antigravity："agy-gemini-5h"、"agy-gemini-weekly"、"agy-3p-5h"、"agy-3p-weekly"；Grok："grok-*"
  "label": "5 小時",                    // 給人看的名稱：「5 小時」「每週」「每週・Fable」
  "percent": 29.0,                      // 0～100
  "resetsAt": 1791959804000 | null,
  "severity": "ok",                     // "ok"（<50）| "warn"（50～80）| "high"（80～95）| "max"（≥95 或已鎖定）
  "history": [[1791500000000, 12.0], [1791503600000, 15.0]]   // 歷史點（最多 168 點，時間遞增），畫走勢線用；可為空陣列
}
```

### ConversationSummary
```jsonc
{
  "id": "c1",
  "accountId": "a1",
  "provider": "Claude",
  "title": "修正登入流程",
  "group": "今天",                      // 今天／昨天／過去 7 天／過去 30 天／月份
  "updatedAt": 1791580000000,
  "workingDirectory": "C:\\repo\\proj",
  "customFolder": true,                 // false＝對話自己的預設 workspace（刪除對話會連同裡面的檔案一起刪除，確認框要明講）
  "running": true,
  "waiting": false,                     // 有等待使用者回應的問卷、核准或計畫確認（含 Codex 非同步提問：這一輪結束後仍可回答；與網頁分頁的「!」同一個判定）
  "unread": false,                      // 完成（或提問、要核准）時沒顯示在前景視窗的畫面上，之後還沒看到（見 `visible`）
  "background": false,                  // 有存活的背景工作（同 Header.background 不是 null）：running 是 false 時分頁與側欄這一列以「等背景工作回報」的虛線圈顯示（liverules.backgroundWaiting）。只有桌面這台的對話有（副本、瀏覽器版省略＝false）
  "programCount": 0,                    // 這個對話另開、還在跑的程式數（同 Header.programs 的個數，2026-10-24）：側欄刪除前問要不要一併停止。只有桌面這台的對話有（副本是 0、瀏覽器版省略）
  "titling": false,                     // 正在依目前的工作產生標題（autoTitle）：這一列閃爍自動重新命名的圖示、右鍵選單的「自動重新命名」停用。只有桌面的清單有（瀏覽器版省略＝false）
  "parentId": "c0",                     // 帳戶委派（docs/account-delegation.md）建立的子對話：主對話的 id（meta 的 ParentConversationId）。側欄把它掛在主對話底下、
                                        //   主對話的委派工具卡以它找到子對話（狀態、開啟）、子對話的標頭寫「由某對話委派」（第十批，2026-10-27）。不是子對話就省略；只有桌面的清單有
  "forkedFrom": "c0",                   // 延伸成新對話（forkConversation）的來源對話 id 與當時的標題（meta 的 ForkedFrom／ForkedFromTitle）：標頭寫「延伸自某對話」、
  "forkedFromTitle": "修正登入流程",     //   來源還在清單裡時點了開啟它（第十批）。不是延伸出來的就省略；只有桌面的清單有
  "deviceId": "d_…" | null,             // 〔遠端〕擁有者電腦（P5）：瀏覽器版一律有值；桌面自己的對話是 null（或省略），其他電腦的副本由 WP4.6 填
  "deviceName": "OFFICE-PC" | null,     // 〔遠端〕擁有者電腦的名稱（不可信字串，textContent）
  "deviceOnline": true | null           // 〔遠端〕擁有者電腦在不在線；重新配對留下的舊身分永遠是 false
}
```
- 〔遠端〕瀏覽器的 `unread` 由伺服器記（WP4.2；sync.db 的 `conversation_unread`，伺服器重開也還在）：擁有者電腦新送來的完成、提問、要核准、計畫確認或失敗（`conv.attention`），或一輪結束（`conv.turn` 的 `completed`），那一刻**沒有任何連線「看見」**這個對話就標未讀；之後任何一條連線開始看見就清掉。「看見」＝那條連線的 `ClientState.visible` 與 `focused` 都為真，而且對話在它最後一次 `visible` 指令的清單或 `openConversationIds` 裡（第六節第 8 點「看得到」再加 focus；只是 `open` 不算）。這對應桌面「顯示在前景視窗的畫面上」。對帳重送與物化表重建不會標未讀；刪除對話時一起清掉。桌面不送未讀狀態，各電腦的未讀互不影響。
- 〔遠端〕瀏覽器的 `running`：擁有者電腦不在線時一律 false（標頭的離線投影，見 Header）。
- 〔遠端〕桌面清單裡其他電腦的副本（WP4.6 起；已配對而且引擎發布對話時才有）：`deviceId` 是擁有者電腦，`running`、`waiting` 是擁有者那邊的（只供顯示，這台的執行中計數、系統匣與關閉前的確認不算它）；`deviceName`、`deviceOnline` 桌面不填（中樞看不到裝置目錄），網頁照 `deviceId` 從 `remote.devices` 查；`customFolder`、`workingDirectory` 是擁有者那邊的（同下方 Header 的副本）。本機對話沒有 `deviceId` 這個鍵，其他鍵與裝飾之前逐鍵相同。副本另有上限，不會把本機對話擠出清單。

### Header
```jsonc
{
  "id": "c1",
  "accountId": "a1",
  "provider": "Claude",
  "title": "修正登入流程",
  "model": "default",                   // "default"＝帳戶預設
  "effort": "default",                  // "default"＝帳戶預設；不在目前模型的 efforts 內時，網頁如實標示「不支援」並提供改回預設
  "permission": "Ask",
  "permissionModes": [ { "value": "Ask", "label": "每步詢問", "description": "執行指令、修改檔案前先問你" } ],  // 這個代理可用的權限模式（Claude 5 種、Codex 3 種）
  "models": [ { "value": "default", "label": "預設（建議）", "description": "帳戶預設模型", "resolved": "Opus 5.5" | null, "efforts": ["low","medium","high","xhigh","max","ultracode"] } ],   // resolved 只有 "default" 那一項有：「預設」實際是哪個模型（Hub/DefaultModelRules：Claude 取 CLI 的說明、Grok 取 currentModelId、Codex 取帳戶 config.toml 的 model 否則 isDefault；Antigravity 與其他電腦的 Codex 對話是 null），網頁顯示成「預設（Opus 5.5）」（settingsrules.modelText）。Claude 的 efforts 最後可能有 "ultracode"（xhigh＋動態工作流程；帳戶能用、模型支援 xhigh 時才有，protocol-notes「ultracode」），網頁標成 Ultracode 並附說明
  "commands": [ { "name": "compact", "description": "壓縮對話", "argumentHint": "" } ],   // 「/」指令選單（Claude initialize 回報；Codex 為空）；沒有 clear 時宿主補在最後（/clear 一律由宿主接住，見 send）
  "workingDirectory": "C:\\repo\\proj",
  "customFolder": true,                 // false＝對話自己的預設 workspace（刪除對話會連同裡面的檔案一起刪除）
  "recentFolders": ["C:\\repo\\proj", "D:\\work"],
  "isDraft": false,
  "running": true,
  "steer": true,                        // 執行中送出的訊息直接插進這一輪（Claude、Codex）；false＝排進佇列等這一輪結束（Antigravity、Grok）。網頁據此寫送出鈕與提示；副本（其他電腦的對話）與瀏覽器版也照它寫（瀏覽器版的標頭由伺服器照代理種類給）：從這裡送進去的是遠端來源的訊息，那台插之前以當時的模式與政策重查，不允許才排隊
  "status": "執行指令：npm test",       // 執行中的一行狀態
  "titling": false,                     // 正在依目前的工作產生標題（autoTitle）：標題後面轉圈（自動重新命名的圖示藏起來）、右鍵選單的「自動重新命名」停用
  "startedAt": 1791580000000 | null,    // 這一輪開始時間（顯示經過秒數）
  "background": { "count": 2, "startedAt": 1761300000000, "tasks": [ { "key": "t3f9a0c12b7de", "tool": "執行指令", "title": "npm test", "startedAt": 1761300000000, "taskId": "bv1d7xyjl", "stopId": "bv1d7xyjl" } ] } | null,   // 存活的背景工作，見下方；沒有是 null。只有桌面這台的對話。key（2026-10-25）：網頁的「詳細」以它送 taskDetail（工具名稱＋一行摘要的雜湊）。taskId（選用，2026-10-25）：Claude 的背景指令與 Monitor 才有（能以 get_task_output 看輸出）。stopId（選用，2026-10-25）：能單獨停止的（Claude 是 task_id、Codex 是工具卡 id；Antigravity、Grok 沒有），網頁的「停止」以它送 taskStop
  "programs": [ { "id": "4321-133000000000000000", "pid": 4321, "name": "node.exe", "command": "node server.js --port 5173", "startedAt": 1791580000000 | null } ] | null,
                                        // 代理另開、脫離 CLI 還在跑的程式（背景程式，2026-10-24），見下方；沒有是 null。只有桌面這台的對話
  "lastTurn": "輸入 12,345・輸出 2,100 tokens" | null,   // 上一輪的用量（桌面與伺服器同一個格式）：網頁認得這個格式（fxrules.parseTurnTokens）時以側欄同款的計數器顯示、數字變了先解碼再鎖定，其他格式照原文。
                                                     // 桌面這台的對話在一輪進行中是這一輪到目前為止 CLI 即時回報的累計（每次模型呼叫的增量加總，還沒收到時照舊是上一輪；TurnTokenRules），
                                                     // 一輪結束換成 CLI 回報的這一輪用量（沒回報時用即時記到的）。伺服器（瀏覽器版）照舊只在一輪結束時更新
  "turnLive": true | false,             // lastTurn 是不是這一輪進行中的即時累計（網頁的 .ci-turn.live）；伺服器不帶（＝false）
  "context": { "used": 22113, "window": 1000000, "percent": 2.2, "autoCompactAt": 967000 | null, "severity": "ok" } | null,   // 上下文用量提示，見下方；只有桌面的本機對話
  "accountMissing": false,              // 對話的帳戶已被移除（要先換帳戶才能繼續）
  "accountDelegation": true,            // 輸入 @ 可選其他已登入的本機 AI 帳戶委派工作（docs/account-delegation.md）：只有本機 Claude／Codex 一般模式的主對話；精簡模式、委派的子對話與副本為 false，瀏覽器版省略（視同 false）
  "pendingAccount": { "accountId": "acc-claude2", "label": "Claude 副帳戶", "when": "turnEnd" } | null,   // 執行中選了另一個帳戶、還沒換過去（setAccount；when 是 "now"＝正在停下、"turnEnd"＝等目前步驟完成後交接）；選了帳戶、在等背景工作結束才換的另帶 "background": true（when 是 "turnEnd"，閒置與執行中都有，2026-10-27）；都沒有是 null。只有桌面這台的對話（瀏覽器版與副本沒有）：網頁在帳戶晶片後面以時鐘＋名稱標出（accountrules.pendingSwitchView），等目前步驟完成與等背景工作結束的在選單給「取消換帳戶」（閒置時的選單也有）
  "restart": HeaderRestart | null,      // 〔熱更新〕這個對話的 CLI 換版狀態；沒有換版要處理為 null（或省略）
  "lastPrompt": { "id": "i7", "seq": 7, "time": 1791580000000, "text": "把報表改成月結", "attachments": 1 } | null,   // 對話上方常駐的「最近的指令」，見下方
  "agent": "default",                   // 選用（只有桌面這台的 Antigravity、Claude 對話且未開精簡模式）：自訂主代理（"default"＝內建主代理）。Antigravity 首輪建立時帶給 `agy --agent`；Claude 每次啟動程序帶 `--agent`（2026-10-25）
  "sessionStarted": false,              // 選用（只有 Antigravity）：是否已建立 CLI session（true 時網頁將自訂代理晶片改為唯讀顯示，點了說明要開新對話）
  "agents": [ { "value": "default", "label": "預設", "description": "Antigravity 內建主代理" }, { "value": "reviewer", "label": "Reviewer", "description": "程式碼審查代理" } ],
                                        // 選用（同上；Antigravity 是工作資料夾 `.agents/agents/*.md` 或設定資料夾有自訂代理、Claude 是這個帳戶的 CLI 在 initialize 回報過代理，或已指定非預設代理時才帶）
  "outputStyle": "Explanatory" | null,  // 選用（只有桌面這台的 Claude 對話且未開精簡模式，2026-10-25）：輸出風格；null＝照帳戶設定（不帶 outputStyle）
  "goal": { "condition": "工作資料夾裡有 z1.txt 到 z9.txt 九個檔案" } | null,   // 選用（只有桌面這台的 Claude 對話，2026-10-25）：進行中的持續目標（/goal），沒有就省略，見下方
  "outputStyles": [ "default", "Proactive", "Concise", "Explanatory", "Learning" ],   // 選用（同上）：這個帳戶的 CLI 在 initialize 回報的 available_output_styles（還沒回報過是空陣列）
  "replica": { "running": true, "waiting": false, "itemsAvailable": true, "accountId": "la_…" | null },   // 〔遠端〕只有桌面的副本（WP4.6 起；accountId 2026-10-25 起）；本機對話省略
  "conversationSync": { "policy": "full", "choices": ["full", "metadata", "localOnly"], "serverDeleted": false },   // 〔遠端〕只有桌面、有同步接收者時的本機正式對話（WP4.6 起）
  "deviceId": "d_…" | null,             // 〔遠端〕同 ConversationSummary（P5）
  "deviceName": "OFFICE-PC" | null,
  "deviceOnline": true | null
}
```
`lastPrompt`（桌面）：最後一則使用者訊息（`AgentHubRules.LastPrompt`，含從 claude.ai、其他電腦送來的），它之後有清除提示（`/clear`）或沒有使用者訊息時是 null。`text` 是畫面上那則的純文字（秘密答案已遮蔽），超過 2000 字截斷並加「…」；`attachments` 是附件數。網頁只載入最後一頁，長的一輪做完之後重開對話時那則可能不在已載入的項目裡，所以由標頭附上；網頁以已載入的項目為準（由新到舊找使用者訊息，先遇到清除提示就沒有，`liverules.latestPrompt`），已載入的都沒有時才用它。那一行只顯示一行（2026-10-25 使用者要求；換行當成空白），放不下時用跑馬燈來回捲動（同標題；減少動態、關掉動態效果時照舊「…」），點一下展開全文；行尾的「歷史指令」鈕展開最近 10 則（`promptHistory`）。〔遠端〕瀏覽器版的標頭沒有這個鍵：已載入的都沒有而且 `hasMore` 時，網頁顯示「在較早的訊息裡」，按了往前載入到找到為止。

`context`（桌面，2026-10-23 起）：這個對話目前占了模型上下文視窗多少，網頁在輸入框的送出鈕左邊畫一個小圓環＋百分比（`contextrules.js`），滑過看明細。`used` 是最近一次模型呼叫的輸入（含快取）加輸出（也就是下一次呼叫的起點），`window` 是目前模型的上下文視窗，`percent`＝`used`／`window`×100（0～100，一位小數），`autoCompactAt` 是 CLI 自動壓縮的門檻（只有 Claude 回報，其他是 null），`severity` 是 `"ok"`（未達 70%）| `"warn"`（70～85%）| `"high"`（85～95%）| `"max"`（95% 以上），比例的分母是 `autoCompactAt`（沒有就是 `window`）。宿主依會話的 `AgentContextUsageEvent` 以 `ContextUsageRules` 合併、只放在記憶體（不存檔、不同步）：app 重開、`/clear`、換帳戶、Claude 壓縮之後是 null，等下一次模型呼叫。用量或視窗不明、Antigravity（串流沒有視窗大小）、草稿與副本也是 null，網頁不顯示。欄位來源與實測見 `docs/protocol-notes.md`「上下文用量」。

`programs`（桌面，2026-10-24 起）：這個對話的背景程式——代理以 nohup、Start-Process 等另開、脫離 CLI 還在跑的程式（和帳戶卡的 `sessions.programs` 同一次掃描，`AccountView` 一節；CLI 自己追蹤的背景工作是上面的 `background`，不算這裡）。依建立時間舊到新、最多 20 個，每棵程序樹只列根；`id` 是 PID 加建立時間（停止時帶回來）、`command` 是去掉路徑與 .exe 的命令列（最多 240 字）、`startedAt` 是程式的建立時間。只列撐過一輪結束的（2026-10-24 起：一輪結束之後才開始的掃描裡、對話不在執行中時還在的，以及接續它們的子程序）——這一輪開出、還沒撐過這一輪的不列，那可能是前景指令（Git Bash 經 timeout、env、bash -c 開的程式和 CLI 的父子關係會斷掉）；之前的輪次留下的，執行中也列（帳戶卡才把執行中的算成執行中）。命令列對得上 CLI 回報的背景工作的（`Header.background` 的工具，`BackgroundProgramRules.IsCliTask`）不列。視窗程式只算沒有視窗的背景執行器（pythonw、javaw、wscript）與自動化用的瀏覽器（帶 `--remote-debugging-port`／`--remote-debugging-pipe`／`--headless` 的 Chromium 系列、帶 `--marionette`／`-headless` 的 Firefox；`BackgroundProgramRules.IsBackgroundGuiProgram`，2026-10-24 起），一般開給人看的視窗程式不列。建置工具留著的常駐程序（MSBuild 的節點重用、VBCSCompiler、Razor 編譯伺服器、Gradle／Kotlin 的 daemon、git fsmonitor、adb 伺服器、LocalDB）與它們開出的程序（MSBuild 節點開的 csc、vstest）不列（`BackgroundProgramRules.IsToolDaemon`）。網頁在標頭的晶片列（git 晶片後面）顯示綠色的終端機晶片「背景程式」／「N 個背景程式」（和等背景工作回報的虛線圈分開；`liverules.programsView`），點了列出每個程式（命令列、已執行多久、PID），點那一列送 `programStop` 停止它與它的子孫。代理在一輪裡直接開、撐過那一輪的程式自己結束時（掃描之間消失、不是它的子程序接著當根）宿主在對話加一則 noticeKind `backgroundProgram` 的 Notice（「代理另開的程式「…」已結束（結束碼 1，執行了 12 分鐘）」），不叫回代理；使用者按停止的不加；這個對話還有 CLI 背景工作在等、或這一輪是 CLI 自己開的（背景工作跑完叫回代理）時也不加（CLI 會告訴代理，以前等於反應兩次）。結束碼 -1 寫「被強制結束」、0xC000013A 寫「被中斷（Ctrl+C）」（`BackgroundProgramRules.ExitCodeText`）。對話閒置中才開出來的程式是別的背景工作的步驟（nohup bash chain.sh 這種 MSYS 迴圈本身不帶對話標記、看不到，看得到的是它一步步開的原生程式）：CLI 有背景工作在跑時歸 CLI（不列），否則照樣列出、但結束時不加提示。停止只結束那個程式與父子關係還連著的子孫：Git Bash 經 timeout 開的子孫、看不到的 MSYS 迴圈不會跟著停。app 啟動後第一次掃描只列、不判定結束（app 重開之前就結束的不提示）。副本、草稿、瀏覽器版沒有這個鍵。

`background`（桌面，2026-10-23 起）：這個對話存活的背景工作（Claude 以 `run_in_background` 開始的指令、背景子代理，來自 CLI 的 `background_tasks_changed`；Codex 是這一輪正常結束時還在跑的指令，以及這一輪中途已讓出給模型、留在終端機裡繼續跑的指令〔2026-10-24 起〕，docs/protocol-notes.md「Codex 的背景指令」；Antigravity 是 `run_command` 非同步背景任務、`schedule` 計時器／排程與 `invoke_subagent` 子代理，直到 `manage_task`／`manage_subagents` 終止或 `<SYSTEM_MESSAGE>` 回報完成為止；都經會話的 `AgentBackgroundWorkEvent`），沒有是 null。`count` 是存活的工作數；`tasks` 是對到工具卡的那些（`BackgroundWorkRules.View`：開始的順序、同一個說明只列一次、最多 5 個），`tool` 是工具的顯示名稱、`title` 是一行摘要（沒有是 null），對不到工具的只算進 `count`。`startedAt`（2026-10-25 起）是開始的時間（Unix 毫秒，不明是 null）：工作的是它自己的（同一個說明的取最早的），外層的是最早開始、還在跑的那一個（列不下的也算；都對不到工具時是這個會話開始有背景工作的時間）。宿主在會話第一次回報這個工作時記下（`BackgroundWorkRules.Starts`：之後的回報說明相同的依序沿用；會話帶了和工具同順序的卡片 id〔Codex，Grok 每個都有 id 時〕就用工具卡建立的時間——Codex 的指令讓出給模型之後才算背景工作；換了會話從頭算），只在記憶體。代理這一輪結束了（`running` false）而它不是 null 時，對話仍算在進行，只是樣貌和執行中不同（`liverules.backgroundLine`）：狀態列是虛線圈＋「等背景工作回報：npm test」（多個時「等 N 個背景工作回報：… 等」，滑過列出每一個與各自已執行多久），後面是外層 `startedAt` 起算的經過時間（同執行中的計時 m:ss／h:mm:ss，每秒更新；2026-10-25 使用者要求），對話框加 `bg-wait`（輸入框的角框與燈慢慢呼吸，沒有掃描光與音量計），分頁與側欄那一列是虛線圈（執行中是實心轉圈），不播「完成」的餘暉；這時照常可以送出（代理沒有在執行，不排隊）。這一行在桌面這台的對話另有「詳細」（2026-10-25 使用者要求：開對話框說明這個背景工作在做什麼、指令與目前的輸出，裡面也有停止鈕；`taskDetail`，多個時先選一個）與「停止」（`liverules.backgroundStopMenu`）：選單列出有 `stopId` 的每一個（說明、已執行多久）與「全部停止（N 個背景工作）」（只有一個、而且能單獨停止時不列；Antigravity、Grok 只有「停止背景工作」），選了送 `taskStop`。背景工作完成後 Claude 與 Antigravity 的 CLI 自己開一輪（Antigravity 在閒置時收到 CLI 主動喚醒的步驟即發 `AgentIdleTurnStartedEvent` 轉回執行中）；Codex 不會，宿主在閒置時隱藏送出接續訊息（對話加一則 noticeKind `backgroundTask` 的 Notice，執行中跑完的等這一輪正常結束再接續）；接續的那一輪照執行中顯示。Codex 的背景指令卡片在這一輪結束後仍是 Running，跑完才更新。執行中時也照樣帶著（網頁照執行中顯示）。宿主另在系統匣（「等背景工作」計數）、結束 app 的確認（結束會中斷背景工作，下次開啟時請代理確認後接著做）與完成的通知（「代理已回覆，背景工作還在執行」）算它。只放在記憶體；程序結束或關閉時會話回報 0、換了會話就不算。副本與瀏覽器版沒有這個鍵（同步不送）。

〔遠端〕瀏覽器版的標頭由伺服器組（WP4.2）：
- 沒有 `context`（上下文用量只在桌面的記憶體，不同步），網頁不顯示上下文提示。
- `models`：擁有者電腦發布了這個對話的帳戶可選的模型（對話中繼資料的 `models`，2026-10-23 起；sync-protocol 14.2）時照它列，同桌面的標頭：最前面沒有 `default` 就補「預設」（`efforts` 用第一項的），目前的模型不在清單裡時加在最後（「目前使用中」，`efforts` 只有目前的強度）；目前的強度不在那個模型的 `efforts` 裡時網頁照實標成不支援（同桌面）。沒發布時（舊版電腦）只有「預設」與目前的模型（不是預設時），兩者的 `efforts` 只有目前的強度（不是預設時）：目前的值一定在選單裡、不會被標成不支援。`commands`、`recentFolders` 是空陣列，`restart` 是 null，`isDraft` 是 false（伺服器草稿是 true）。`permissionModes` 照代理種類（同桌面）。
- `status` 依序：物化表重建中「伺服器正在整理這個對話」→ 擁有者電腦不在線「電腦離線」（`running` 也是 false、`startedAt` 是 null）→ 伺服器還原之後還沒重新上傳「等待電腦重新上傳」→ 電腦送的狀態文字。
- `workingDirectory` 只有自訂資料夾（`customFolder: true`）才有；對話自己的 workspace 不送路徑。
- 伺服器草稿（`isDraft: true`）的 `deviceId` 是 `newConversation` 選填的電腦（WP4.6 起；沒選是 null），`deviceName` 是 null、`deviceOnline` 是 false、`status` 是 null（草稿還沒有對話在那台）：網頁照 `deviceId` 從 `remote.devices` 查名稱與在線，顯示「電腦：OFFICE-PC」。

〔遠端〕桌面的標頭（WP4.6 起）：
- 其他電腦的副本：`deviceId` 是擁有者電腦（`deviceName`、`deviceOnline` 不填，同 ConversationSummary）；`replica` 是擁有者那邊的 `running`、`waiting`，以及有沒有同步項目（`itemsAvailable`：擁有者的政策是 `full` 才有，`metadata`、`paused` 只有標題與狀態）。標頭自己的 `running` 一律是這台的（副本沒有程序，是 false）；網頁的分頁轉圈、對話畫面的執行中動畫與狀態列（`liverules.statusLine`，沒有狀態文字時寫「執行中…」）照 `replica.running`；可以操作的副本換資料夾（送給那台的 `setFolder`、`resetFolder`、「瀏覽資料夾…」）也看 `replica.running`（`liverules.ownerRunning`：那台執行中時那台會拒絕）。`replica.accountId`（2026-10-25）是那台執行這個對話的帳戶連結到的邏輯帳戶（對話中繼資料只帶那台的本機帳戶 id，中樞以帳戶目錄的連結換算；目錄還沒到、沒有連結時 null，目錄換了開著的副本重送標頭）：帳戶晶片照它顯示帳戶名稱與顏色（這台有連結時是本機那一筆，否則是 remoteOnly 的），可以操作的副本點晶片換成那台已登入的帳戶（見 `setAccount`）；標頭自己的 `accountId` 照舊是空字串。`accountMissing` 一律 false（副本不是這台的帳戶，不是帳戶被移除）、`recentFolders` 與 `commands` 是空陣列。`customFolder`、`workingDirectory` 是擁有者發布的（對話中繼資料的 `customFolder`、`workingDirectory`，同瀏覽器版的標頭）：自訂資料夾帶那台電腦上的完整路徑（只供顯示，不當成這台的資料夾；中樞的 Meta.WorkingDirectory 照舊是 null，項目裡的相對路徑也不以它解析），預設 workspace 是 `customFolder:false`、`workingDirectory` null。2026-10-23 之前一律是 `customFolder:false`、null：從這台換了那台的資料夾，標頭仍寫「預設工作區」、「恢復預設工作區」一直停用。副本沒有 `conversationSync`（政策只能在擁有者那台改）。`models` 是擁有者發布的那個帳戶可選的模型（對話中繼資料的 `models`，2026-10-23 起；最前面沒有 `default` 時中樞補「預設」，同本機的對話），擁有者沒發布時（舊版）是這台內建的預設選單（Codex 只有「預設」）：可以操作的副本從這台改那台的模型（送給那台的 `setOption`），選單列的是那台實際能選的。
- 本機的正式對話在這台已配對、引擎發布對話時帶 `conversationSync`：`policy` 是目前的同步政策（原樣，含權杖 DLP 設的 `paused`）；`choices` 是 `syncSetConversationPolicy` 收的值：可同步而且伺服器沒刪除時是 `full`、`metadata`、`localOnly`，伺服器已刪除（`serverDeleted` true）或這種代理不能同步（Antigravity、Grok）時只有 `localOnly`。政策改了、伺服器刪除或 DLP 暫停時，開著的視窗收到新的標頭；接收者掛上或拿掉（配對、解除配對）時開著的本機對話都重送標頭。草稿、未配對時沒有這個鍵。

### HeaderRestart〔熱更新〕
這個對話正在等、正在做或剛做完的 CLI 換版（宿主的重開協調產生）。狀態改變時宿主送這個對話的 `header`。
```jsonc
{
  "state": "waitingTool",               // 見下表
  "from": "2.1.295" | null,             // 這個對話的程序原本跑的版本（不明為 null）；app 更新的狀態（appUpdate、app 更新後的 notContinued）為 null
  "to": "2.1.297" | null,               // 要換到（或已換到）的版本；app 更新的狀態（同上）為 null。和 from 相同時（CLI 更新取消、失敗或沒換版，放棄 app 更新後就地接續）兩個都是 null，網頁不顯示版本晶片
  "detail": "CLI 已更新到 2.1.297，等目前的指令（npm test）完成後切換",   // 宿主產生的一行說明（純文字，用 textContent）
  "canApplyNow": true,                  // 顯示「立即套用」：送 cliApplyNow {conversationId}（等待類狀態才可能為 true）
  "canContinue": false,                 // 顯示「繼續」：送 cliContinue {conversationId}（notContinued 才可能為 true）
  "since": 1791580000000 | null         // 開始等待的時間（排定換版的時候，同背景工作 30 分鐘上限的起算點）；還在等的狀態（waitingTool、waitingUser、waitingBackground、waitingTurn）與已停下等更新的（waitingUpdate、appUpdate）才有，其他為 null。網頁在說明後面每秒更新「已等 3:25」（宿主不為了計時重送標頭）；舊宿主沒有這個鍵＝不顯示
}
```

| state | 意義 | 之後 |
|---|---|---|
| `waitingTool` | 有工具／指令在執行：等它結束（安全點）再以新版重開並自動接續 | 工具結束後 ≤5 秒 → `restarting` |
| `waitingUser` | 等你回應（核准、問卷、計畫確認）：回應後到下一個安全點才重開（舊卡在重開後會失效） | 同上 |
| `waitingBackground` | 有背景工作在跑（重開會中斷它）：等它結束，最多等 30 分鐘（從排定換版起算，`CliRestartRules.BackgroundWaitLimit`；2026-10-24 起，以前是滿 30 分鐘只問「要現在重開嗎？」），超過就照立即套用停下（背景工作跟著停止），以新版開始後馬上隱藏送出確認訊息，請代理確認背景工作做到哪裡、需要的話重新執行（noticeKind `cliUpdate`；被停掉的是高風險操作時不自動送，改成 `notContinued`＋`canContinue`）。`canApplyNow` 一律為 true（不想等就自己按，結果同上） | 同上 |
| `waitingTurn` | 政策是「等這一輪結束再切換」 | 這一輪結束時關掉舊程序 → null（下一則訊息用新版） |
| `waitingUpdate` | app 正在更新這種 CLI（npm 屏障）：已停下，等更新完成 | 更新結束 → `restarting`（安裝損壞時 → `failed`） |
| `restarting` | 正在以新版重新啟動 | → `continued`、`notContinued` 或 `failed` |
| `continued` | 已用新版自動接續，這一輪還在跑 | 這一輪結束 → null |
| `notContinued` | 已換新版，但這一輪沒有自動接續（原因在 `detail` 與 Notice；例如被打斷的是 git push 這類不可重複的操作）；app 更新後沒有接續的（含上次 app 更新沒有完成、程式在換版前結束）與 app 重新啟動後沒有接續的（上次結束、當掉時這一輪還在執行，見下方）`from`／`to` 為 null，原因在 `detail` | 送出新訊息或按「繼續」→ null |
| `failed` | 以新版重開失敗（`detail` 是原因；這一輪已結束、佇列暫停） | 下一次送出 → null |
| `nextSend` | 政策是「下次送出時才用新版」：程序還在跑舊版 | 下一次送出 → null |
| `appUpdate` | 已停下，等 Clode Myriagent 更新後自動接續（app 自我更新的排空）；`from`／`to` 為 null | 新版啟動後接續：對話加一則 `noticeKind: "appUpdate"` 的 Notice、照常執行；沒有自動接續的 → `notContinued`。取消這次更新 → 在原本的程序就地接續 |

閒置（沒有在執行）的對話換版時，宿主直接關掉舊程序、下一則訊息自然用新版，不出現任何 `restart` 狀態，也不送接續訊息。

app 結束（使用者結束、Windows 登出或重新開機）或當掉時還在執行的一輪，下次啟動主視窗第一次就緒時由宿主接續（docs/plans/2026-10-23-interrupted-turn-resume.md）：可以自動接續的照常執行、對話加一則 `noticeKind: "appUpdate"` 的 Notice（接續訊息不顯示成使用者訊息），不出現 `restart` 狀態；中斷超過 1 小時、被打斷的是高風險操作、連續自動接續達上限的 → `notContinued`。

### Item
```jsonc
{
  "id": "i1",
  "kind": "User",                       // User | Assistant | Thinking | Tool | Question | Approval | PlanApproval | Plan | Notice | Error
  "status": "Done",                     // Running | Waiting | Done | Failed | Declined | Cancelled
  "time": 1791580000000,                // 建立當下的系統時間（只供顯示；時鐘可能被往回調，不能拿來排順序）
  "seq": 42,                            // 這個對話裡的建立順序（只增不減）：只載入最後一頁時，網頁靠它分辨新項目與還沒載入的較早項目
  "html": "<p>…</p>",                   // User：純文字轉的 HTML；Assistant／Thinking／Notice／Error／Plan（計畫說明）：Markdown 轉的 HTML；Tool／Approval／Question／PlanApproval 為空字串
  "noticeKind": "cliUpdate" | "appUpdate" | "clear" | "accountSwitch" | "backgroundTask" | "backgroundProgram" | "overloadRetry" | "sideQuestion" | "fork" | null,   // 〔熱更新〕Notice 的種類（選用）：cliUpdate＝CLI 換版的說明（網頁加 CLI 圖示）；appUpdate＝app 自我更新、app 重新啟動後的接續說明（網頁加下載圖示）；accountSwitch＝額度用完、自動改用另一個帳戶繼續，或原帳戶的額度恢復後換回（網頁加人像圖示；見 Settings 的 autoSwitchAccount）；backgroundTask＝Codex 的背景指令跑完、代理接著處理（或說明這次為什麼沒有自動接續；網頁加時鐘圖示；見 Header.background）；四者同樣式、文字照 html；backgroundProgram＝代理另開的背景程式自己結束了（2026-10-24；同樣的框、綠色、終端機圖示；見 Header.programs）；overloadRetry＝API 過載或暫時無法服務（Claude 的 529、Antigravity 的 503）、排定了自動重試（2026-10-24；「60 秒後自動重試」；網頁從這則的 time 起算 60 秒倒數〔fxrules.retryCountdown〕，倒數將盡時開始執行＝重試開始、閃一下，提早開始執行＝重試取消、倒數條淡掉）；sideQuestion＝/btw 旁支提問的問與答（2026-10-24；見 send；虛線框、問號圖示、上方標「旁支提問・不在對話的上下文」，status 是 Running 時答案還沒來）；fork＝延伸出來的新對話的開頭說明（2026-10-25；見 forkConversation；同 cliUpdate 的框、分支圖示）；不認得的種類當一般提示。clear＝/clear 清除了對話（見 send）：網頁把最後一則 clear 之前的項目藏起來、不往前載入，上方的按鈕改成「顯示清除前的對話」（按了這個分頁就照舊全部顯示、照舊往前載入；又清除一次才再藏；liverules.clearBoundary）。一般提示省略
  "source": "remote" | null,            // User 的來源（選用）：remote＝從 claude.ai 或 Claude App 送來（Claude 的 Remote Control；網頁在氣泡下一直標示「從 claude.ai／Claude App 送出」，附圖只在文字裡註明張數）；在 app 送出的省略
  "author": "小明",                      // 〔多人協作〕User 是哪位協作人送的（顯示名稱，選用；擁有者送的省略）：網頁在氣泡下標「協作人 小明」，協作人看擁有者送的標「管理員」（collabrules.authorLabel）
  "resolvedBy": "小明",                  // 〔多人協作〕Approval／Question／PlanApproval 是哪位協作人回應的（選用；擁有者回應的省略）

  // Tool / Approval
  "toolKind": "bash",                   // bash | read | write | edit | search | web | agent | mcp | todo | image | plan | other（決定圖示）
  "toolName": "執行指令",
  "title": "npm test",                  // 一行摘要（指令、檔案路徑…）
  "detail": "…",                        // 展開後的輸入（純文字）
  "output": "…",                        // Tool：工具輸出尾段（純文字）；Approval／PlanApproval：拒絕或「繼續規劃」時附給代理的說明（「你的說明：…」），已決定的卡片顯示在結果下方；〔熱更新〕產生這張卡的 CLI 程序重開後，卡片改為 Cancelled、output 為「CLI 已重新啟動，這張卡已失效；代理會視需要重新提出」（它對到的還沒核准的 Tool 卡：「CLI 已重新啟動，這個操作還沒有核准，沒有執行；代理會視需要重新提出」）
  "diff": [ { "t": "hunk", "s": "@@ -1,3 +1,4 @@" }, { "t": "del", "s": "舊的一行" }, { "t": "add", "s": "新的一行" }, { "t": "ctx", "s": "不變的一行" } ] | null,
  "diffStats": { "add": 3, "del": 1 } | null,   // 整份差異的統計：diff 超過 800 行會截斷（最後一行 hunk「⋯ 還有 N 行未顯示（+A −D）」），統計仍含截掉的行
  "filePath": "C:\\repo\\proj\\a.cs" | null,   // 讀寫的檔案（點了用 openPath／revealPath）
  "decision": "allow",                  // Approval：allow | allowSession | deny；Question：answered | skip；PlanApproval：acceptEdits | manual | keepPlanning
  "canAllowForSession": true,
  "allowSessionLabel": "本對話改為自動接受編輯（檔案修改與 mkdir、rm、mv 等檔案指令不再詢問）",   // 選用：「本對話都允許」按鈕要顯示的文字，照 CLI 的建議說明放行範圍：每步詢問下的編輯是改為自動接受編輯（header.permission 跟著改；工作資料夾外與 .claude、.git 等敏感檔 CLI 仍會詢問），指令類是 CLI 建議的規則或資料夾（例「本對話都允許 Bash(npm test:*)」），沒有建議時只放行完全相同的請求。同意的規則與資料夾在 app 執行期間有效（程序閒置回收或重開時會帶回），重開 app 後要重新允許
  "detailHtml": "<p>…</p>" | null,      // PlanApproval：計畫內容；Tool（write／edit 帶 Antigravity Artifact 內容時）：Artifact 的 Markdown 轉 HTML 預覽卡；Question（帶計畫／Artifact 摘要時）：摘要 Markdown 轉 HTML。一般 Markdown 支援 GitHub Alerts（`> [!NOTE]` 等）與 `mermaid` 離線 SVG 圖表渲染（`Web/mermaidrules.js`）

  // Question
  "questions": [ { "id": "q1", "header": "彙總", "text": "要怎麼彙總？", "multi": false, "allowOther": true, "secret": false,
                   "options": [ { "label": "依日期", "description": "…", "preview": null } ] } ],
  "answers": { "q1": ["依日期"] },
  "answerAsMessage": false,             // true＝答案會當作下一則訊息送出（Codex 非同步提問）
  "toolName": "MCP：github",            // 選用（2026-10-24）：不是代理自己問的——Claude 的 MCP 伺服器要使用者輸入（elicitation）。網頁的卡片標頭寫「MCP：github 想問你」、detailHtml 的標籤寫「說明」；代理問的省略。
                                        // 表單的每個欄位一題（id＝欄位名稱；布林是「是／否」、列舉是選項、文字與數字自行輸入，選填的多一個「不填」），網址模式一題「已完成／不要」、網址在 detailHtml 裡（點了用預設瀏覽器開）；
                                        // 宿主依欄位型別把答案轉成 JSON 回給 CLI（McpElicitationRules），轉不成（數字填了文字、必填的選了不填）時婉拒並加一則錯誤提示。略過＝婉拒、按停止＝取消

  // Plan（待辦清單：Claude TodoWrite 或 TaskCreate／TaskUpdate、Codex turn/plan）
  "plan": [ { "text": "讀取報表", "status": "completed" } ],   // pending | inProgress | completed

  // 附件與媒體
  "attachments": [Attachment],
  "media": [ { "url": "https://myriagent.local/media?path=…", "kind": "image" | "video", "path": "C:\\…\\chart.png", "name": "chart.png" } ]
}
```
〔遠端〕瀏覽器模式的 Approval、Question、PlanApproval 另帶兩個欄位（伺服器的渲染加，WP4.2 起；桌面的 Item 不變，`ItemRenderer` 的黃金檔不動）：
- `requestDigest`：卡片內容的摘要（`rd1:` 開頭，`docs/sync-protocol.md` 14.5）。網頁回應卡片時原樣帶回（第二節的遠端指令規則）。〔桌面遠端操作〕桌面的**副本**卡片也帶它（`PeerHubRules.DecorateReplicaCard`，副本的渲染之後才加；本機對話的 Item 不變，黃金檔不動）。
- `risk`：`"normal"` | `"high"` | 省略。`high` 時卡片標示「高風險」，`allow` 是層級 2；省略（電腦沒判斷）時網頁照一般顯示，電腦端仍當成高風險算層級（設計 14.11 D9）。

瀏覽器模式的 `media[].url`、`attachments[].url`／`thumbUrl` 是伺服器的相對網址（`/m/{key}`、`/t/{key}?s=320`；`key` 是 `{deviceId}-{ref}`），沒有 `path`（本機路徑不離開電腦）。
- 〔遠端〕**媒體網址的回應**（WP4.7 起）：`/m/` 支援單一區間 Range（開放式或超過 8 MB 的回 206 與實際的 `Content-Range`，影片照常拖曳）；沒帶 Range 時總長 ≤64 MB 回 200、更大的回 413。來源電腦離線、沒有媒體功能或電腦的遠端政策是 Off／暫停（設計 Q1-A）時：圖片改回 320 px 的縮圖（伺服器有縮圖時：200、`Cache-Control: no-store`、標頭 `X-Myriagent-Fallback: thumb`；離線時沒有型別，`Sec-Fetch-Dest` 是 `video`／`audio` 的不算圖片），影片照回錯誤（503 `offline`，政策是 403 `policy`）不能播放；改成 View 或 Control 才轉送原檔。原檔已經不在（來源電腦回 404：回覆引用的截圖被刪掉、又沒有留存的副本；2026-10-27 起）時同樣先改回縮圖，沒有縮圖的瀏覽器 `<img>`（`/m/`、`/t/`）回一張「圖片已不存在」的 SVG 佔位圖（200、`Cache-Control: private, max-age=3600`、`X-Myriagent-Fallback: missing`），不再是破圖、也不觸發「媒體載入失敗」的回報。其他錯誤：404（參照不在、對話已刪除或不是完整同步）、503 `busy`（`Retry-After: 2`；同一台同時的轉送滿了先在伺服器排隊，等滿 10 秒還沒有名額才回）、504 `timeout`（電腦 15 秒沒回）。回應帶媒體專用的 sandbox CSP 與 `nosniff`，型別只收可顯示的圖片與可播放的影片（SVG、HTML 一律下載）。網頁（WP4.6）：對話所在電腦的 `remoteControlLevel` 是 `Off`（暫停也回報成 `Off`）時，影片載入失敗在影片下方（與影片檢視器）顯示「這台電腦的遠端政策是關閉，只能看縮圖」；其他時候照舊（項目裡不另外說明，影片檢視器是原本的「影片無法播放…」）。
- 〔遠端〕**沒有網址的媒體**（WP4.2）：伺服器在 WP4.7 之前一律沒有媒體網址；WP4.7 起擁有者電腦發了參照的才有（文件附件沒有參照，要經 `fileFetch`）。沒有網址的 `media` 不輸出，改成 `attachments` 裡只有檔名的一項（`kind:"Document"`、`url`／`thumbUrl`／`size` 是 null、id 是 `media-N`）；沒有網址的影片附件也改成 `Document`（沒有縮圖與影片資訊）。網頁因此不會畫出空的 `img`／`video` 或按了沒反應的播放鈕。Markdown 裡引用本機圖片被淨化成 alt 文字；本機檔案的連結是 `#local-file=<完整路徑，encodeURIComponent>`（以前是 `#`）：網頁攔下點擊送 `fileFetch`（第二節），可直接顯示的圖片、影片收到 `fileRef` 後直接在燈箱或播放器顯示（附「下載」），其他檔案在框裡給「開啟」；路徑不是磁碟代號開頭（UNC 等）的標記不處理。
- 〔遠端〕瀏覽器版的 `html`／`detailHtml` 另經伺服器的白名單淨化（設計 8.4；`p`、`br`、`hr`、`h1`～`h6`、`strong`、`em`、`del`、`code`、`pre`、`blockquote`、清單、表格、`a`、`img`、`span`、待辦清單的核取方塊），`a` 一律 `rel="noopener noreferrer" target="_blank"`，`href` 只有 `https:`、`http:`、`mailto:`、`#` 與本站的 `/m/`、`/f/`，`src` 只有本站的 `/m/`、`/t/`；不合 Markdig 輸出形狀的整段改成純文字。真的瀏覽器裡的驗證是 `tools/ui-probe/conv-probe.mjs`。
- 〔遠端〕單筆太大（渲染後 >256 KB）或內容讀不到（WP4.7 之前的大欄位 blob）時，伺服器送一則同 id 的佔位項目（文字類照原本的種類、其他改成 Notice），內容是「內容太大，暫時無法在瀏覽器顯示」之類的說明。

### Composer
```jsonc
{
  "attachments": [Attachment],
  "busy": false,                        // 附件處理中（不能送出）
  "busyText": null,
  "queue": [ { "id": "q1", "text": "接著幫我跑測試" } ],  // 排隊中、這一輪完成後才送出的訊息（執行中送出的先排在這裡，插進這一輪就移出）
  "steering": "q1" | null,              // 正在插進這一輪的那則（佇列最前面；網頁寫「正在插入這一輪…」）；〔遠端〕瀏覽器版沒有（省略）
  "steered": { "id": "i42", "text": "順便跑一下測試" } | null,   // 只有桌面（2026-10-25）：最近插進這一輪、還可以試著取消的那則（Claude、這台本機送的；這一輪結束、又插一則、試過取消就沒了）。網頁在輸入區上方顯示「已插入這一輪：…」與「取消插入」（cancelSteer）
  "quotaWait": { "until": 1792000900000, "account": "Claude 主帳戶" } | null,   // 只有桌面這台的對話（2026-10-26）：額度用完、沒有帳戶可以接手，等這個帳戶在 until（毫秒；重置時間＋1 分鐘）之後自動接著做（設定 autoResumeAfterQuota）。網頁在輸入區上方顯示「額度用完：等…重置後自動接著做」與「取消等待」（quotaWaitCancel；claudeoptrules.quotaWaitView）
  "subagents": { "i17": { "tokens": 15500, "tools": 2, "ms": 3200, "last": "Grep", "done": false } } | null,   // Claude 的子代理即時用量（2026-10-26；task_progress、task_notification）：鍵是 Agent 工具卡的項目 id，最多 30 個（最近更新的）；網頁畫在卡片標題列（items.paintAgentUsage）。副本與瀏覽器版沒有
  "idleSince": 1792000000000 | null,   // Claude 本機對話閒置（不在執行中）的開始時間（毫秒，最後一輪結束；2026-10-26）：網頁送出前以 claudeoptrules.cacheResumeWarning 判斷提示快取（1 小時）過期、上下文 10 萬 tokens 以上時先問（設定 claudeIdleResumeWarning）。執行中、草稿、副本沒有
  "queueMore": 3                        // 〔遠端〕選用（P11）：佇列裡還有幾則沒帶到（瀏覽器版最多帶 50 則）；省略＝0，桌面不送
}
```
〔遠端〕瀏覽器版（WP4.2）：`attachments` 一律是空的（伺服器沒有本機附件），`busy` 一律是 false、`busyText` 一律是 null（電腦不送附件處理的狀態：`conv.queue` 只有 `entries` 與 `more`；要顯示時第 5 階段另外擴充契約，sync-protocol 與本文件一起改），`queue` 的 `text` 是電腦送的顯示文字（秘密回答已遮蔽、每則最多 600 字）。`queueMore` 不是一則佇列項目，網頁不要對它送 `cancelQueued`。
- 〔遠端〕網頁的上傳晶片（WP4.6）：因為 `attachments` 一律是空的，瀏覽器版的附件由網頁自己管。迴紋針、拖放、貼上的檔案先在本機檢查（單檔 >512 MB 直接成為失敗的晶片、不發 `PUT`；一則最多 20 個，多的不收、提示一次），再以 `PUT /api/v1/uploads/{uploadId}`（`uploadId` 是新的 ULID、標頭 `X-Myriagent-Csrf: 1`，同時最多 3 個）上傳；回應要是 200／201 而且 `uploadId` 相同、`sha256` 是 64 位小寫十六進位才算成功，其他回應碼照 413、507、網路中斷等在晶片上說明。這一批都結束後，成功的一次送 `addUploaded`（`size`、`sha256` 用伺服器回的）：`done` 或不明（可能已收下）時晶片成為「已附加」，被拒或沒送出時成為失敗的晶片「沒有加入輸入區，請重新附加」。失敗的晶片可以按 × 拿掉；上傳中的晶片的 × 是「取消上傳」（WP4.6 合併前審查 PERF-2）：中止那個 `PUT`（伺服器收到中斷就釋放預留），晶片拿掉；關掉分頁、`conversationClosed` 時還在上傳的也一樣中止、不再送 `addUploaded`（加入中的不再處理，成為失敗的晶片「已取消上傳」）。已附加的沒有移除鈕（電腦那邊沒有對應的指令）。
- 還在上傳或加進輸入區中時送出鈕停用、說明「附件上傳中，完成後才能送出」，`hasUnsentInput`（伺服器換版本時是否自動重新載入，第一節 `init`）也算它；只有已附加的晶片、沒有文字時照樣可以送出（空送帶附件）。`send` 是 `done` 時清掉這次送出的晶片，被拒或不明時保留（`policy` 被拒時附件回到那台的輸入區，第三節）；`conversationClosed` 時全部清掉。迴紋針在伺服器草稿（第一則送出之前）停用，說明「送出第一則訊息後才能附加檔案」；沒連線、電腦離線、政策不允許時也停用並說明原因。
- 已附加的晶片存在 sessionStorage `myriagent.app.uploaded.v1:{conversationId}`（`[{uploadId, name, size, sha256}]`，最多 20 筆；重新載入後還原，內容不合格或儲存不能用時當成沒有）。
- 輸入紀錄（`Web/historyrules.js`，網頁自己管、不經宿主、不進同步）：送出的文字（含 `/` 指令；空白與超過 10 萬字的不記）**每個對話各記一份**，存在 localStorage `myriagent.app.inputHistory.v2`（`[{ id: 對話 id, list: 字串陣列 }]`，最近在裡面送出過的對話在前；每份最新的在前、同樣內容只留最新的、最多 10 則；最多 200 個對話、全部加起來最多 100 萬字，超過時從最久沒送出的對話丟起），同一個對話的各個視窗共用，瀏覽器版存在那個瀏覽器；〔遠端〕草稿換成正式對話（`conversationClosed` 的 `replacedBy`）時草稿的紀錄搬到新的 id。舊版所有對話共用的 `myriagent.app.inputHistory.v1` 分不出是哪個對話的，不搬，寫入新的一份時刪掉。在輸入框按 ↑（沒有 Shift／Ctrl／Alt）：游標在最前面（含空的輸入框）時換成這個對話最近的一則，正在看一則沒改過的紀錄而且游標在最後（帶入時的位置）或最前面時換成更早的一則；↓ 在看沒改過的紀錄、游標在最後時換成較新的一則，最新的再按 ↓ 還原開始翻之前的文字。其他情況（游標在中間、有選取範圍、改過帶入的文字、`@`／`/` 提示開著）照原本移動游標或選提示；改過的文字成為新的草稿。

### Attachment
```jsonc
{
  "id": "x1", "name": "截圖.png", "kind": "Image" | "Video" | "Document", "size": "182 KB",
  "path": "C:\\…\\截圖.png",
  "thumbUrl": "https://myriagent.local/thumb?path=…&size=160" | null,
  "url": "https://myriagent.local/media?path=…" | null,      // 可直接顯示／播放時才有
  "videoInfo": "0:35・6 張畫面" | null,
  "wave": "0136985310"                  // 選用：語音訊息的錄音波形（AgentAttachment.Waveform，一格一個 0～9 的數字；只有桌面這台錄的才有、不經同步）。沒有就沒有這個鍵
}
```

### FileEntry
```jsonc
{
  "name": "logo.png",
  "path": "C:\\repo\\proj\\assets\\logo.png",
  "kind": "dir" | "image" | "video" | "audio" | "code" | "doc" | "archive" | "file",
  "size": 48213 | null,                 // 資料夾為 null
  "modified": 1791580000000,
  "thumbUrl": "https://myriagent.local/thumb?path=…&size=160&v=…" | null,   // 圖片與影片才有；v＝檔案版本，檔案被覆寫後網址跟著變
  "url": "https://myriagent.local/media?path=…" | null,                 // 可在 app 內顯示（圖片）或播放（影片）才有
  "hidden": false
}
```

### GitStatusView
`gitStatus` 的 `git`（宿主 `Git/GitStatusRules.cs` 的 `Render`；網頁的顯示規則在 `Web/gitrules.js`）。
```jsonc
{
  "root": "C:\\repo\\proj",            // repo 的最上層資料夾（工作資料夾可能是它的子資料夾）
  "branch": "main" | null,              // 分離的 HEAD 是 null
  "head": "1a2b3c4" | null,             // HEAD 的提交（前 7 碼）；還沒有任何提交是 null
  "upstream": "origin/main" | null,     // 上游分支；沒設是 null
  "upstreamGone": false,                // 設了上游但這台已經沒有那個分支（遠端刪除後 fetch --prune 過）
  "ahead": 2 | null, "behind": 0 | null,          // 和上游比：未推送、未拉取（上次 fetch 時的遠端）；沒有上游或已刪除是 null
  "base": "origin/main" | null,         // 遠端的預設分支（遠端/HEAD 指向的，沒有就 main、master）；就是上游時是 null
  "baseAhead": 4 | null, "baseBehind": 1 | null,  // 和 base 比：本分支獨有、預設分支獨有的提交；只表示歷史差異，不表示需要合併
  "changed": 3,                         // 有變更的已追蹤檔案
  "staged": 1, "unstaged": 2,           // 已暫存、未暫存（同一個檔案可能兩邊都算）
  "untracked": 1,                       // 未追蹤（被忽略的不算）
  "conflicts": 0,                       // 合併衝突還沒解決的檔案
  "stashes": 0,
  "operation": "merge" | "rebase" | "cherryPick" | "revert" | "bisect" | null,   // 還沒完成的操作
  "remote": "origin" | null,            // 問遠端時問的遠端（有上游問上游，否則問 base）；沒有可問的是 null
  "remoteNew": true | false | null,     // 遠端那個分支現在的提交和這台記著的不同（有還沒下載的提交）；還沒問、問不到、遠端沒有那個分支是 null
  "remoteCheckedAt": 1791580000000 | null,   // 問遠端的時間：同一個 repository（git 共用資料夾：子資料夾、各個 worktree）的同一個遠端分支共用一次查詢，最多每 5 分鐘一次
  "checkedAt": 1791580000000,
  "autoPull": {                         // 自動拉取（宿主 Git/GitAutoPullRules.cs）；沒有要說的是 null
    "state": "pulling" | "pulled" | "skipped" | "failed",
    "text": "已從 origin/main 快轉拉取 2 個提交",   // 或「這次不拉：有 2 個已追蹤的檔案還沒提交」「失敗：fatal: …」（網址裡的帳密已遮掉）
    "at": 1791580000000
  } | null
}
```
網頁：未推送／未拉取只和仍存在、且成功取得差異的上游比。沒有上游、上游已刪除或差異未知時，明細顯示「無法判定推送／拉取狀態」，不改用 `base` 推斷；未設定上游也不代表從未推送。`baseAhead`／`baseBehind` 都已知時，另列中性的「與預設分支差異」，標出比較對象及雙方獨有提交數，並說明差異不代表需要合併；不產生「未合併」徽章、不改變狀態色調。這適用於長期獨立分支、功能分支與分離的 HEAD，不依名稱猜用途。沒有上游時，`remoteNew` 只列預設分支的遠端變更明細。標頭說明與無障礙文字共用摘要：乾淨且與上游同步，或乾淨但同步狀態未確認。

自動拉取：有未拉取的提交（未拉取大於 0，或 `remoteNew`）時宿主只做快轉——遠端有新提交時先 `git fetch` 那一個分支到它的追蹤分支，再 `git merge --ff-only`；有上游的分支和上游比、分離的 HEAD 和 `base` 比，沒有上游的分支不拉。有進行中的操作、衝突、這裡有不在目標上的提交（不能快轉）、已追蹤的檔案有未提交的變更（未追蹤的不算）、這個 repo 裡有對話正在執行時不拉（`skipped`，`text` 寫原因）；執行前再讀一次，HEAD 變了也不拉。失敗後 15 分鐘內不再自動試（`gitRefresh` 照樣試），兩次自動拉取至少隔 1 分鐘。網頁在明細列出「自動拉取」一列（`pulled`、`failed` 帶時間）。

### LaunchView
`launchStatus` 的 `launch`（宿主 `Launch/LaunchRules.cs` 的 `Render`；網頁的顯示規則在 `Web/launchrules.js`）。字串都是不可信的（專案檔裡的名稱），網頁一律 `textContent`。
```jsonc
{
  "folder": "C:\\repo\\proj",               // 偵測的工作資料夾
  "selected": "t1a2b3c4d5e6",               // 選的目標（下面的狀態都是它的）
  "targets": [ {
    "id": "t1a2b3c4d5e6",                    // 不透明（專案檔完整路徑的雜湊）
    "name": "Myriagent",                  // AssemblyName、package.json 的 name，沒有就是檔名或資料夾名
    "kind": "dotnet" | "electron",
    "path": "src\\Myriagent\\Myriagent.csproj",   // 相對於 folder
    "detail": "WinForms · net10.0-windows",  // 或「Electron · npm run start」
    "phase": "idle" | "building" | "running" | "paused" | "stopping"   // 這個目標自己的（選單標出哪些正在執行）
  } ],
  "phase": "idle" | "building" | "running" | "paused" | "stopping",
  "mode": "debug" | "release",               // 選的目標選的執行模式（沒選過是 debug；下一次啟動用這個）
  "runMode": "debug" | "release" | null,     // 這一次的執行模式（閒置是 null；接回的一定是 release）
  "since": 1791580000000 | null,             // 這次開始的時間（建置與執行合起來；閒置是 null）
  "last": {                                  // 上一次怎麼結束的；這次開始時清掉
    "kind": "exited" | "stopped" | "buildFailed" | "startFailed",
    "code": 1 | null,                        // 結束碼（停止、開不起來是 null）
    "text": "Form1.cs(12,5): CS1002 必須是 ;",   // 給人看的：「已結束（結束碼 1）」「已停止」、建置的第一個錯誤、「無法啟動：…」
    "at": 1791580000000
  } | null,
  "logSeq": 120,                             // 這次輸出的最後一行序號（沒有輸出是 0）
  "logEpoch": 3                              // 第幾次執行（每次啟動加一）；沒執行過是 0
}
```
網頁：目標晶片後面是執行模式晶片（`Debug`／`Release`，Release 用強調色；很窄的窗格只留第一個字母），點了開選單換模式（送 `launchSelect`＋`mode`）；滑過的說明寫這個模式會不會跟著 Clode Myriagent 關閉，執行中換了模式時另外說明下一次才套用（`launchrules.js` 的 `modeChip`）；狀態的滑過說明帶 `runMode`。按鈕依 `phase`（`launchrules.js` 的 `launchButtons`）——閒置只有啟動能按，建置中可以停止、重新建置並執行，執行中可以暫停、停止、重新建置並執行，暫停中可以繼續、停止、重新建置並執行，停止中只能重新建置並執行。快捷鍵（焦點在這個對話的窗格裡、執行列看得到時）：F5 啟動／繼續、Shift+F5 停止、Ctrl+Shift+F5 重新建置並執行。`last.kind` 是 `buildFailed` 或 `startFailed` 時自動打開輸出面板，面板有「把錯誤貼到輸入框」（只貼錯誤行，最多 20 行）。

### HealthView
`health` 與 `init.health`（宿主 `Hub/AgentHub.Health.cs` 的 `RenderHealth`）。
```jsonc
{
  "running": false,                     // 正在分析（「重新分析」鈕停用、顯示分析中）
  "checkedAt": 1791580000000 | null,    // 上次分析完的時間；還沒分析過是 null
  "accounts": { "<帳戶 id>": AccountHealthView },   // 這台的本機帳戶（remoteOnly 沒有）；還沒分析過是 {}
  "instructions": InstructionsView | null
}
```

### AccountHealthView
```jsonc
{
  "level": "ok" | "info" | "warn" | "bad",   // 整體＝各項最差的（info 不拉低）；網頁不認得的等級當 info
  "summary": "CLAUDE.md 偏大",               // 最差那一級的項目短句（最多兩個，以「、」連接）；良好時是「良好」
  "load": "約 1.3 萬 token 指示＋約 6,300 token skill 清單" | null,   // 每次對話開始前固定載入的量
  "canTrim": true,                      // 全域指示偏大或過大：明細的「全域指示」一項顯示「用代理精簡…」
  "canTrimMemory": true,                // 有自動記憶索引超過或接近上限：明細的「自動記憶索引」一項顯示「用代理整理…」（舊宿主沒有這個鍵＝不顯示）
  "items": [{
    "key": "instructions" | "memory" | "skills" | "mcp" | "plugins" | "settings",
    "title": "全域指示",
    "level": "warn",
    "value": "84 行・約 1.3 萬 token",   // 量測的摘要
    "detail": "每次對話開始都會載入…\n建議：…",   // 說明與建議，多段以換行分開
    "files": [{ "path": "C:\\Users\\u\\.claude\\CLAUDE.md", "label": "主檔　CLAUDE.md", "note": "84 行・約 1.3 萬 token", "level": "warn", "target": "C:\\repo\\dotfiles\\claude\\CLAUDE.md" | null }]
  }]
}
```
- `instructions`（全域指示）：每次對話都會載入的檔——Claude 是設定資料夾的 CLAUDE.md、它的 `@` 匯入（最多四跳）與 `rules\` 底下沒有 `paths` 的規則；Codex 是 `AGENTS.override.md`（有內容時）或 `AGENTS.md`；Grok 是 `GROK_HOME` 那一層的 AGENTS.md 等檔、`rules\`，Claude 相容沒關時另加 `~\.claude\CLAUDE.md`；Antigravity 是 `~\.gemini\GEMINI.md`。合計超過 5,000 token 或單檔超過 200 行是 warn；超過 15,000 token、Claude 單檔超過 40,000 字元（CLI 啟動時警告）、Antigravity 單檔超過 24,000 位元組（截斷）是 bad。精簡模式的 Claude、Codex 是 info（不載入）。
- `memory`（只有 Claude）：每個專案的 `memory\MEMORY.md`（共用設定時是來源的）只載入前 200 行或 25KB，超過是 bad、到九成是 warn。
- `skills`（Claude、Codex）：skill 與斜線指令的名稱＋描述（單筆最多 1,536 字元）；Claude 超過 8,000 字元（200K 脈絡的 1%）是 warn。
- `mcp`（Claude、Codex）：使用者層與外掛的 MCP 伺服器，10 個以上是 warn。`plugins`（只有 Claude）：啟用的外掛帶來的東西，啟用了卻沒安裝是 warn。`settings`：settings.json 不是有效的 JSON 是 bad、Claude 的 `.claude.json` 超過 5 MB 是 warn。

### InstructionsView
電腦層級的指示檔：各 CLI 使用者層的那一份（系統預設帳戶的、不共用設定的隔離帳戶各自的；共用設定的隔離帳戶每次啟動前與來源同步，不另外列）。
```jsonc
{
  "verdict": "none" | "single" | "same" | "needsSync" | "diverged",   // 都沒有內容／只有一種 CLI／一致／有空的要同步／互有缺漏
  "level": "ok" | "info" | "warn",
  "summary": "Codex 的 AGENTS.md 是空的，拿不到其他 CLI 的 120 條指示；建議合併並同步。",
  "mergeable": true,                    // needsSync 或 diverged：顯示「用代理合併…」
  "docs": [{
    "key": "c:\\users\\u\\.claude\\claude.md",   // 路徑的小寫
    "provider": "Claude", "label": "Claude Code",        // 隔離帳戶是「Codex・工作帳戶」
    "path": "C:\\Users\\u\\.claude\\CLAUDE.md", "target": "C:\\repo\\dotfiles\\claude\\CLAUDE.md" | null,   // target：符號連結指向的實際檔案
    "exists": true,
    "status": "complete" | "partial" | "empty" | "missing",
    "note": "84 行・約 1.3 萬 token" | "空的" | "不存在",
    "overLimit": false,                 // 超過這個 CLI 的單檔上限（Antigravity 24,000 位元組）
    "units": 120, "covered": 120, "union": 135, "coverage": 0.889,   // 自己的條數、所有條目（聯集）中拿得到的、聯集的條數
    "missingCount": 15, "missing": [{ "section": "部署", "text": "push 後驗證部署…" }],   // 最多 12 條（原文最多 120 字）
    "uniqueCount": 3, "unique": [{ "section": "", "text": "…" }],
    "alsoLoads": "Claude Code" | null,  // Grok 的 Claude 相容會一併讀的那一份
    "duplicates": 0,                    // 自己有、經相容又讀了一次的條數
    "accounts": ["acc-claude"]
  }]
}
```
比對方式：每份切成一條條指示（清單項目、表格的一列、一段文字、一個程式碼區塊；HTML 註解去掉），正規化（小寫、去掉 Markdown 符號與多餘空白）後相同、或字元雙連字的相似度到 0.8 以上算同一條。比對只看文字：某個 CLI 專屬的指示也會算成「缺少」，由代理合併時判斷。

### UsageSegmentsView
```jsonc
{
  "path": "C:\\…\\usage-segments\\records.jsonl",   // 全部紀錄的檔（「在檔案總管顯示」）
  "total": 37,                                     // records.jsonl 的筆數
  "summary": [                                     // 彙總：只算 complete 的、使用率有前進的；帳戶 × 額度窗 × 主要模型（輸入＋輸出佔九成以上的模型，否則「（混用）」）
    { "accountId": "…", "accountLabel": "Claude example-user", "provider": "Claude", "windowKey": "weekly:Opus", "windowLabel": "每週・Opus",
      "model": "claude-opus-5-5", "records": 6, "percent": 30.4,            // 段數、實際前進的百分點合計
      "tokens": { "input": 0, "cached": 0, "cacheWrite": 0, "output": 0 }, // 合計（輸入含快取讀取與快取寫入）
      "per5": { "input": 0, "cached": 0, "cacheWrite": 0, "output": 0 },   // 每 5%＝合計 × 5 ÷ percent（不假設每筆剛好 5%）
      "minPer5": 0, "maxPer5": 0,                                          // 各筆每 5% 的輸入＋輸出：最少、最多
      "lastAt": 1793000000000 }
  ],
  "open": [                                        // 正在記的各段（這台的帳戶 × 每週額度窗）
    { "accountId": "…", "accountLabel": "…", "provider": "Codex", "windowKey": "codex:10080", "windowLabel": "每週",
      "bucket": 15, "startPercent": 15.2, "lastPercent": 17, "startedAt": 0, "lastSeenAt": 0, "complete": true,
      "models": [ { "model": "gpt-6-astra", "input": 0, "cached": 0, "cacheWrite": 0, "output": 0 } ] }   // 輸入＋輸出多的在前
  ],
  "records": [                                     // 最近的紀錄（新的在前，最多 200 筆）
    { "id": "…", "accountId": "…", "accountLabel": "…", "provider": "Claude", "windowKey": "weekly", "windowLabel": "每週",
      "fromBucket": 10, "toBucket": 15, "startPercent": 12.3, "endPercent": 15.2, "startedAt": 0, "endedAt": 0, "complete": false,
      "model": "claude-opus-5-5",                  // 主要模型（同 summary）
      "models": [ { "model": "…", "input": 0, "cached": 0, "cacheWrite": 0, "output": 0 } ] }
  ]
}
```
時間一律是 Unix 毫秒。`accountLabel` 是帳戶目前的名稱（移除了的帳戶用記錄當時的名稱）。Claude 的每週・模型那一窗只算名稱相符的模型：網頁把其他模型的列畫淡（`usagesegrules.modelCounts`）。

### CliView
```jsonc
{
  "provider": "Codex",
  "installed": true,                    // 找到執行檔且查得到版本
  "checking": false,                    // 找到執行檔但還沒查過版本（啟動後第一次檢查完成前、app 開著時才裝好的）：installed 為 false 時網頁顯示「檢查中…」，不顯示「未安裝」與安裝按鈕
  "version": "0.160.1" | null,
  "latest": "0.162.0" | null,           // 上次成功查到的最新版本
  "latestError": "無法查詢最新版本：連線逾時" | null,   // 最近一次查最新版本失敗（離線、registry 被擋）：latest 可能是舊的，網頁不顯示「已是最新版」、顯示這段文字
  "updateAvailable": true,
  "installKind": "npm" | "native" | "winget" | "managed" | "other" | null,   // winget：WinGet 的 portable 套件，更新跑 winget upgrade（先排空這種 CLI 的對話）
  "path": "C:\\Users\\U\\AppData\\Roaming\\npm\\codex.cmd" | null,
  "autoUpdate": true,
  "busy": false,
  "progress": "下載中 45%" | null,
  "lastCheckedAt": 1791580000000 | null, // 上次成功查到最新版本的時間（查不到時不更新）：CLI 卡的「上次檢查」
  "checkedAt": 1791580000000 | null,     // 最近一次檢查完成的時間（查不到最新版本也更新）：網頁的「立即檢查」以它判斷完成
  "error": null,                        // 安裝／更新失敗或沒有換版的原因，或執行檔跑不起來的原因

  // 〔熱更新〕安裝狀態（取自 CliManager）
  "changing": false,                    // 安裝正在變動（偵測到換檔、npm 正在替換套件，或 app 自己正在換檔）：宿主暫緩這種 CLI 的新啟動，送出的訊息排進佇列；網頁顯示「安裝變動中…」
  "installBroken": "Codex 安裝不一致（package.json 0.162.0、執行檔 0.160.1），請修復" | null,   // 安裝損壞（版本來源互相矛盾）：宿主不重開任何對話；網頁顯示這段文字與「重新安裝」按鈕（cliInstall）
  "lastChange": {                       // 最近一次已穩定的換版；沒有為 null
    "from": "2.1.295" | null,
    "to": "2.1.297",
    "origin": "app" | "external",       // external＝不是 app 做的（你在終端機更新、互動式 claude 的背景更新…）：CLI 卡寫「外部更新」
    "at": 1791580000000
  } | null,
  "update": {                           // app 觸發的更新進度（npm 屏障）；沒有在更新為 null
    "phase": "draining" | "waitingProcesses" | "installing" | "verifying",   // 等對話停下｜等其他程序結束與清理殘留｜換檔中｜驗證新版
    "held": 1,                          // 已停下、等更新完成後以新版接續的對話數（取自中樞）
    "waiting": 2,                       // 還在執行、等安全點才停得下來的對話數（取自中樞）
    "overdue": false,                   // 排空超過 10 分鐘（DrainGrace）：網頁問「還有 2 個對話在執行工具，要等還是現在中斷？」——「現在中斷」送 cliApplyNow {provider}、「取消更新」送 cliCancelUpdate {provider}、「繼續等」什麼都不送
    "startedAt": 1791580000000
  } | null,

  // 〔熱更新〕各對話的換版彙總（取自中樞）
  "restartPolicy": "SafePoint" | "AfterTurn" | "NextSend",   // 同 Settings.cliRestartPolicy（兩個 CLI 相同）
  "runningOnOldVersion": 2,             // 程序還活著、但跑的不是目前安裝版本的對話數
  "restartPending": 3,                  // 已排定以新版重開、還沒完成的對話數（＝restartWaiting 的筆數）
  "restartWaiting": [                   // 每個待重開的對話；state 同 HeaderRestart.state（waiting* 與 restarting）
    { "conversationId": "c1", "state": "waitingTool", "detail": "npm test" | null }
  ],
  "restartDone": { "continued": 1, "restarted": 2, "notContinued": 0, "failed": 0 } | null   // 最近一波換版已完成的對話數（restarted＝閒置的只關程序；新的一波開始時歸零）；沒有換版過為 null
}
```
CLI 卡的「立即套用」（cliApplyNow {provider}）在 `runningOnOldVersion` 或 `restartPending` 大於 0 時顯示。

### DeviceCliView〔同步〕
一台電腦上一種 CLI 的狀態（`cli.state` 事件的內容；CliView 的子集）。
```jsonc
{ "installed": true, "version": "2.1.297" | null, "installKind": "native" | null, "latest": "2.1.297" | null,
  "updateAvailable": false, "runningOnOldVersion": 0, "restartPending": 0, "installBroken": null, "checkedAt": 1791580000000 | null }
```

### Remote〔同步〕
```jsonc
{
  "mode": "desktop" | "browser",        // 這份網頁跑在哪種宿主
  "deviceId": "d_…" | null,             // 桌面：這台的 deviceId（配對完成前 null）；瀏覽器：null
  "devices": [DeviceView],              // 所有已配對的電腦（含已撤銷的，排在最後）
  "sync": SyncStatus | null,            // 只有桌面
  "reconnectGraceMs": 5000              // 選用：剛斷線的電腦標「重新連線中」的時間（毫秒，1000～60000；DeviceView 的 online）。只有 Development、Testing 環境的伺服器
                                        //   照 Hub:Browser:ReconnectGraceMs 給（端到端縮短等待）；沒有或不合格時網頁照內建的 60 秒（remoterules.reconnectGraceOf）
}
```
〔遠端〕`Remote` 不加 `control`：第 4 階段沒有可重複使用的遠端授權（每個指令由伺服器各簽一個信封，設計 14.11 D3），遠端操作的驗證狀態是每條連線各自的 `control` 訊息（第一節）。

### DeviceView〔同步〕
```jsonc
{
  "id": "d_…",
  "name": "OFFICE-PC",                  // 顯示名稱（deviceRename 改的）
  "signedName": "OFFICE-PC",            // 配對時的名稱（伺服器的裝置名冊）
  "self": false,                        // 是目前這台（桌面）
  "machineName": "OFFICE-PC" | null,
  "appVersion": "0.3.0" | null,
  "fingerprint": "XXXX-XXXX",           // 裝置金鑰指紋（8 字；配對時桌面與核准頁顯示的同一個值）
  "keyStorage": "tpm" | "software",
  "online": true,                       // false 而剛斷線（網頁看到它從 true 變 false，或 lastSeenAt，60 秒內）時網頁顯示「重新連線中」而不是「離線」：
                                        //   remoterules.trackDeviceLinks 在網頁裡另標 offlineSeenAt、reconnecting（不是線上的欄位）；只換文字，閘門照樣擋
  "lastSeenAt": 1791580000000 | null,
  "remoteControlLevel": "Off" | "View" | "Control",   // 那台的本機政策（只供顯示；不允許的按鈕變灰並說明「在 OFFICE-PC 的設定中允許」）
  "allowRemoteFullAccess": false,
  "allowRemoteExec": false,             // 〔遠端執行指令〕那台允許其他電腦的代理在那台執行指令（Hello 回報的政策，層級是 Control 才會是 true；只供顯示）
  "verified": true | false | null,      // 桌面：這台採用的名冊裡有沒有這一筆（14.4 起名冊由伺服器授權，只檢查格式）；瀏覽器：null
  "revoked": null | { "at": 1791580000000, "reason": "retired" | "compromised" }
}
```

### SyncStatus〔同步〕（只有桌面）
```jsonc
{
  "state": "pairing" | "connecting" | "online" | "offline" | "paused" | "stopped",
  "detail": "伺服器連不上，30 秒後重試" | null,   // 給人看的說明；stopped 的原因（權杖已撤銷請重新配對、同步資料庫倒退請重新配對、請更新 app…）
  "server": "https://hub.example.com",
  "pending": 12,                        // outbox 待送筆數（標題列「同步：離線（12 筆待送）」）
  "lastSyncAt": 1791580000000 | null,   // 最近一次和伺服器同步完成（Live）的時間
  "clockSkewMs": 185000 | null,         // 和伺服器的時差超過 2 分鐘時的差值（狀態列警告「請同步 Windows 時間」）
  "deviceId": "d_…" | null,             // 這台的 deviceId（配對完成前 null；init.remote.deviceId 配對後不更新，看這裡）
  "deviceFingerprint": "XXXX-XXXX" | null,             // 這台裝置金鑰的指紋（8 字）
  "hubFingerprint": "XXXX-XXXX-XXXX-XXXX" | null,       // 釘選的伺服器身分金鑰指紋（16 字）
  "pairing": {                          // 配對中才有
    "userCode": "MTLZ-XFCH",
    "verificationUri": "https://hub.example.com/#pair",
    "verificationUriComplete": "https://hub.example.com/#pair=MTLZ-XFCH",   // QR 碼的內容
    "expiresAt": 1791580600000,
    "error": "配對碼已過期" | "已拒絕" | null,
    "openable": true                    // WP3.4：網址合格才是 true（https、沒有帳號密碼與查詢字串、路徑是 /、片段以 #pair 開頭、
                                        //   主機與埠和 server 相同；兩者都是迴路主機時也算、不比埠）。true 才給「在瀏覽器開啟」
                                        //   （openLink）；網址文字一律照常顯示
  } | null,
  "confirm": {                          // 要使用者在本機比對指紋後確認（syncConfirm）；確認前同步停止
    "kind": "hubIdentity",          // 第 3 階段起只有這一種（擁有者改成帳號密碼，沒有擁有者金鑰；設計 14.4）
    "fingerprint": "XXXX-XXXX-XXXX-XXXX"   // 新的伺服器身分指紋（16 字）。畫面要請使用者與伺服器主控台（Hub 主機上的
                                          //   `MyriagentHub.exe admin status`）比對，並明說**不要**拿網頁上的比：伺服器被冒充時網頁也是對方的
  } | null,
  "local": {                            // WP3.4：這台的本機同步設定（設定頁的同步區照它顯示，不等伺服器的回音；離線時也有）
    "paused": false,                    //   syncPause 的值
    "remotePaused": false,              //   系統匣「暫停遠端操作」（這時回報給伺服器的政策是 Off，這裡仍是本機的設定）
    "remoteControlLevel": "Off" | "View" | "Control",   // 和 syncSetPolicy 收的值相同
    "allowRemoteFullAccess": false,
    "notifyRemoteActions": true,        //   〔遠端〕選用（WP4.6 起）：syncSetPolicy 的值；省略＝true
    "allowRemoteExec": false            //   〔遠端執行指令〕選用（2026-10-25 起；接得住遠端執行指令、有引擎時才送）：syncSetPolicy 的值。
                                        //   設定頁有這個鍵才顯示開關，層級不是 Control 時停用
  },
  "backfill": { "conversationsLeft": 12, "bytesLeft": 52428800 } | null,   // 〔遠端〕選用（WP4.6 起；數字由 WP4.3 的發布者產生）：舊對話回補到伺服器的進度
                                        //   （設計 14.2 回補規則 3）；沒有在回補（完成或還沒開始）時省略
  "remoteSupported": true,              // 〔遠端〕選用（WP4.4 起）：這台的程式能執行遠端指令；省略＝還不能（設定頁的遠端控制政策照常可改，
                                        //   旁邊說明「這一版還不會執行遠端指令」）
  "peer": true                          // 〔桌面遠端操作〕選用（有引擎時一律送）：這台目前能對其他電腦下指令（連線交握完成、協商出 peer1，
                                        //   docs/sync-protocol.md 14.7）；false 而 state 是 online＝伺服器或這台不支援（副本照舊唯讀），其他 false＝還沒連上
}
```
`local` 只有桌面宿主送（瀏覽器模式沒有 SyncStatus）。暫停、遠端暫停、`syncSetPolicy` 改了之後，有狀態時宿主立刻重送 `sync`；未配對時不送。
〔遠端〕WP4.6 起宿主實際送 `local.notifyRemoteActions`（有引擎時；配對中還沒有引擎時省略）與 `backfill`：回補的進度或暫停改變時重送 `sync`。因磁碟保護暫停回補時 `backfill` 的形狀不變，「回補已暫停」只在系統匣顯示。`syncSetPolicy` 的 `notifyRemoteActions` 不是布林（含 JSON null）時整個指令忽略，政策也不改。

### Layout（網頁擁有、宿主只負責保存）
```jsonc
{
  "panes": [ { "id": "p1", "tabs": ["c1", "c2"], "active": "c1", "tabsSide": "left" }, { "id": "p2", "tabs": ["c3"], "active": "c3" } ],
                                        // tabsSide（選用，2026-11-05）："left"＝這個窗格的分頁放在左側（窗格按鈕列的切換鈕）；省略＝上方的橫排（舊版忽略）。
                                        //   窗格不到 460px 寬時照用橫排（tabdragrules.sideTabsOn），偏好不變；分割出來的窗格沿用。
                                        //   tabsWidth（選用，整數）：拖曳分頁欄右緣的把手調過的寬度（120～360，顯示時另依窗格寬度夾住、對話區至少留 276px；
                                        //   tabdragrules.clampSideWidth）；沒調過或雙擊恢復預設（184）時省略
  "focusedPane": "p1",
  "sidebarCollapsed": false,
  "view": "chat",                       // "chat" | "dashboard" | "schedules"（2026-10-26 起；舊版讀到不認得的當成 chat，宿主沒掛排程讀取器時網頁也當成 chat）
  "historyScope": "all",                // 〔遠端〕"all" | "local"（WP4.6 起；側欄的電腦篩選選「這台電腦」是 local，其他是 all；省略＝all）
  "historyDevice": "d_…",               // 〔遠端〕選用（2026-10-23 起）：側欄的電腦篩選選了某一台時那台的 deviceId（historyScope 這時是 all，舊版讀成全部）；
                                        //   電腦下拉只在有其他電腦的對話時出現（桌面有副本、瀏覽器有兩台以上），那台沒有對話時不篩、選擇照樣留著
  "monitor": "open"                     // 〔電腦狀態〕"open" | "closed"：使用者選過右側面板顯示或收起才有；省略＝視窗至少 1200 px 寬才顯示（窄螢幕 ≤720 px 一律預設收起、打開時疊在右邊，不記進版面）
}
```
窗格最多 12 個，排成最多 4 欄 × 3 排的格狀：欄數＝⌈√n⌉（最多 4）、排數＝⌈n／欄數⌉（1 個；2 個並排；3～4 個 2×2；5～6 個 3×2；7～9 個 3×3；10～12 個 4×3），格子沒排滿時第一個窗格往下多佔幾排補滿。舊版只還原前 4 個窗格。

### DeviceStatsView〔電腦狀態〕
一台電腦的狀態（`deviceStats` 的 `devices`）。網頁容忍缺欄位：數字不是有限值當成沒有值、百分比夾在 0～100、用量大於總量的夾到總量。
```jsonc
{
  "id": "d_…" | null,                   // 這台（桌面）未配對時是 null；self 是 false 時一定有
  "self": false,                        // 是目前這台（只有桌面的第一筆）
  "name": "OFFICE-PC" | null,           // 只有 self：電腦名稱（已配對時網頁優先用 remote.devices 的顯示名稱）
  "seq": 42,                            // 每台各自遞增；沒變＝同一筆樣本
  "ageMs": 850,                         // 送出當下這筆樣本已經多舊
  "cpu": 23.5,                          // 0～100（%，一位小數）
  "cores": 16 | null,                   // 邏輯處理器數
  "cpuName": "AMD Ryzen 9 7950X 16-Core Processor" | null,
  "memUsed": 21474836480, "memTotal": 34359738368,          // 位元組
  "disks": [ { "name": "C:", "used": 657129996288, "total": 999653638144 } ],   // 固定磁碟（位元組）；沒有名稱或總量不是正數的略過
  "netRx": 2621440, "netTx": 393216,                         // 下載／上傳，位元組／秒（所有網卡合計）
  "rttMs": 42 | null,                   // 與伺服器的往返延遲（毫秒）；沒有連線是 null（未配對的這台顯示「未配對」）
  "uptimeSec": 273600 | null,           // 開機多久
  "gpus": [ { "name": "NVIDIA GeForce RTX 4070", "util": 23.5,      // 顯示卡（2026-10-23）：使用率 0～100（各引擎中最忙的，同工作管理員）
              "memUsed": 6442450944 | null, "memTotal": 17179869184 | null } ],   // 專用記憶體（位元組；兩個都有才顯示）。拿不到（伺服器主機、舊版）是空陣列
  "cpuTemp": 67.8 | null,               // CPU 溫度（攝氏，一位小數；2026-10-23）：讀不到是 null（沒有監控工具在跑的桌機）
  "cpuTempSource": "hwinfo" | null,     // 溫度從哪裡讀的：hwinfo、lhm（LibreHardwareMonitor）、ohm（OpenHardwareMonitor）、acpi（ACPI 熱區）；不認得的照原樣顯示
  "xfers": [ { "id": "01J9Q…", "name": "報告.docx", "size": 52428800, "done": 10485760,   // 電腦狀態面板的檔案傳輸（2026-11-09）：這台正在接收（devicePut）與剛收完的檔，
               "state": "receiving" | "saved" | "failed", "saved": "報告 (2).docx" | null } ]   // id 是 uploadId，saved 是實際存成的檔名；結束的留 10 分鐘、最多 32 筆；沒有時省略
}
```
網頁的分級（只影響顏色與文字，嚴重度一律同時有文字）：CPU ≥70% 偏高、≥90% 滿載；記憶體 ≥80% 偏高、≥92% 吃緊；磁碟 ≥85% 偏高、≥95% 快滿；顯示卡同 CPU；CPU 溫度 ≥80°C 偏高、≥90°C 過熱；延遲 ≤60 ms 良好、≤150 普通、≤400 偏慢、更慢是很慢。
CPU 溫度與顯示卡在量表下面各一列（同磁碟的分段 LED 條；溫度條 0～100°C）：溫度列尾是來源的簡稱、說明寫完整來源（ACPI 註明未必是 CPU 核心的溫度）；顯示卡只有一張時標籤是「GPU」，多張時是廠牌（同廠牌加編號，看不出廠牌的是 GPU1、GPU2…，`monitorrules.js` 的 `gpuLabels`），列尾是專用記憶體、說明是完整名稱。沒有的不列。

### HubStatsView〔電腦狀態〕
MyriagentHub 主機（伺服器）自己的負載（`deviceStats` 的 `hub`）：數值欄位同 DeviceStatsView，沒有 `id`、`self`、`rttMs`（伺服器不讀顯示卡與溫度：`gpus` 是空陣列、`cpuTemp` 是 null）。
```jsonc
{ "name": "Hub 主機",                    // 伺服器的電腦名稱（卡片副標）
  "seq": 7, "ageMs": 600, "cpu": 12.5, "cores": 12, "cpuName": "…" | null, "memUsed": 0, "memTotal": 0,
  "disks": [ { "name": "C:", "used": 0, "total": 0 } ], "netRx": 0, "netTx": 0, "uptimeSec": 0 | null,
  "devices": 3,                         // 目前在線的電腦數
  "browsers": 2 }                       // 目前連著的瀏覽器連線數
```

### Settings
〔同步〕瀏覽器模式只有 `theme`、`fontSize`、`enterToSend`、`notifications` 四個鍵。瀏覽器模式的設定頁只顯示這四個鍵（`settings.js` 的 `ROAMING_KEYS`），外加這個瀏覽器自己的語音通知開關與它底下的語速、音調（`data-setting="voiceLocal"`，存在 `localStorage`、不送 `setting`，見 `speak`），以及輸入區「繼續」鈕的指令（`data-setting="continueLocal"`，存在 `localStorage` 的 `myriagent.continueText`、不送 `setting`；沒存過＝預設）：沒有列的區塊、沒有區塊的分頁不建；對話框開著時模式變了就重建。
```jsonc
{
  "theme": "dark" | "light" | "system",
  "fontSize": 14,
  "visualEffects": true,                // 介面的持續動態效果（執行中的掃描光、圓環轉動、電腦狀態面板的雷達等）；false 時網頁在根元素設 data-fx="off"，照「減少動態」的規則停掉持續的動畫（載入中的轉圈照轉），系統要求減少動態時一律停。桌面專屬（瀏覽器版的設定頁不顯示，伺服器當成其他鍵回 invalid）
  "closeToTray": true,                  // 關閉視窗時縮到系統匣（背景繼續執行）
  "autoStart": false,                   // 開機時自動啟動：登入 Windows 時由目前使用者的 Run 機碼執行 current\Myriagent.exe --autostart（「關閉視窗時縮到系統匣」開著時主視窗藏著、只留系統匣圖示）。狀態以登錄為準（工作管理員停用也算關閉），不存 settings.json、每次送設定時重讀；null＝這次執行不能設定（不是受管理的安裝、用 --data 執行或端到端測試），網頁停用開關並說明原因。以 setting 修改（只接受布林），不論成敗都重送設定、失敗時另送 toast。桌面專屬（瀏覽器版的設定頁不顯示，伺服器當成其他鍵回 invalid）
  "notifications": true,                // 代理提問、核准、完成時系統通知
  "voiceNotifications": true,           // 語音通知：對話完成、失敗，或代理提問、要求核准、計畫等確認時，用 Windows 內建的雅婷語音（語速與音調照 voiceSpeed、voicePitch，SpeechAudioRules）念一句說明（對話標題＋完成了什麼／問什麼／要核准什麼，VoiceNotifyRules；沒有字數上限；視窗在前景也念；使用者按停止的那一輪不念），開始念時送帶 speaking 的 flash（不帶 speaking 的 flash 不管這個開關，事情一發生就送）；以 setting 打開時宿主念一句試聽、關掉時停下正在念的。桌面專屬（瀏覽器版的設定頁不顯示，伺服器當成其他鍵回 invalid）
  "voiceSpeed": 1.5,                    // 語音通知的語速（總倍速，0.5～2.5、間距 0.1；設定頁「語音通知」底下的滑桿，放開時送）：宿主限制在範圍內、取到 0.1，不是數字的值不改也不重送設定；值變了而且語音通知開著時，先停下正在念的、以新的語速與音調念一句試聽（VoiceNotifyRules.TuningSample）。SAPI 的語速只有整數級，零頭以不改音高的時間伸縮補上（SpeechAudioRules.Plan）。桌面專屬（同 voiceNotifications）
  "voicePitch": 2,                      // 語音通知的音調（升幾個半音，-6～6 的整數，負的是降）：以較高的播放取樣率升調（聲音特質一起升），升調帶來的加速由較慢的 SAPI 語速抵掉，語速照 voiceSpeed；其他同 voiceSpeed
  "scheduleAutoDiagnose": true | null, // 排程頁：列入管理的排程新的一串失敗開始時，自動在它的管理對話請代理診斷（只診斷不修改；ScheduleRunRules 的次數與額度上限）。宿主沒掛排程讀取器時是 null（設定頁不顯示）。桌面專屬（同 voiceNotifications）
  "autoUpdateCli": true,                // 偵測到 CLI 新版且沒有執行中的對話時自動更新
  "autoUpdateApp": true,                // app 自我更新：有新版時在背景下載、使用者閒置時重啟套用（只有從受管理的安裝位置執行時有效；AppUpdateView.managed 為 false 時網頁停用這個開關）
  "cliRestartPolicy": "SafePoint",      // 〔熱更新〕CLI 更新後：SafePoint＝在安全點立即切換並自動接續（預設）｜AfterTurn＝等這一輪結束再切換｜NextSend＝下次送出時才用新版；以 setting 修改，只接受這三個值
  "enterToSend": true,                  // Enter 送出（false＝Ctrl+Enter 送出）
  "claudeRemoteControl": true,          // Claude 對話的程序啟動時開啟 Remote Control（可從 claude.ai/code、Claude App 接手；開著的程序閒置 2 小時才回收，app 換版時也只重開 2 小時內有動靜的）；以 setting 修改，立即套用到活著的程序
  "claudeFallbackModel": "sonnet" | null,   // Claude 的備援模型（2026-10-25）：主模型過載或不能用時 CLI 自動改用（啟動時帶 `--fallback-model`，和對話的模型相同時不帶）；null＝不用。以 setting 修改，值要是 `conversationDefaults.Claude.models` 的其中一個（空字串＝不用），改了下一則訊息重開程序。設定頁在「模型設定」的 Claude Code 子分頁。桌面專屬
  "claudeIdleTokenRefresh": true,       // 閒置的 Claude 帳戶權杖過期、借不到同身分的新鮮權杖時，開一個不送訊息的 Claude 程序以 get_usage 代查用量（CLI 查的時候自己換發權杖，2026-10-26 起；不用額度）（每帳戶最多每小時一次）；以 setting 修改，打開時資料過時的 Claude 帳戶馬上再查。桌面專屬（瀏覽器版的設定頁不顯示，伺服器當成其他鍵回 invalid）
  "claudeIdleResumeWarning": true,      // Claude 對話閒置超過 1 小時（提示快取過期）、上下文 10 萬 tokens 以上時，送出前先問一聲（2026-10-26；標頭 idleSince）；確認框勾「以後不再提醒」就以 setting 關掉。設定頁「輸入」。桌面專屬
  "autoResumeAfterQuota": true,         // 額度用完、又沒有帳戶可以接手時，等這個帳戶的額度重置（12 小時內的）後自動送出接續訊息（2026-10-26；標頭 quotaWait、quotaWaitCancel）；接著做又馬上用完最多重來 3 次，等待期間有帳戶恢復就照 autoSwitchAccount 換過去。設定頁「額度」。桌面專屬
  "autoSwitchAccount": true,            // 額度用完時自動換帳戶繼續（預設開；支援四種代理同種備援）：一輪因為訂閱額度用完而失敗或完成時（Claude 合成訊息或 result 的 api_error usage_limit_reached、Codex 的 codexErrorInfo usageLimitExceeded、Antigravity 非重試的額度或點數耗盡；Grok 沒有帶內額度錯誤，輪末查 _x.ai/billing，鎖定才發 AgentQuotaExhaustedEvent，失敗或完成都會換；暫時性的 429 不算），宿主改用同一種代理、能用同一個模型（帳戶回報過模型清單才比）、已登入訂閱（不含 API Key 計費）、適用的時間窗都還沒用滿的帳戶，挑下一次重置最早的（不知道的排最後）；同種代理沒有可用帳戶、且 autoSwitchCrossProvider 開著時，再挑其他代理種類可用且最快重置的訂閱帳戶接手。換帳戶同 setAccount（新 session＋前文節錄），接著隱藏送出接續訊息、對話加一則 noticeKind accountSwitch 的 Notice；排隊的訊息不暫停，等接續的一輪正常結束照常送。用完的帳戶到重置為止（不知道就 30 分鐘）不再被選。沒有可以接手的帳戶而有其他候選帳戶時加一則一般提示。使用者按了停止就不換。原帳戶的額度恢復後自動換回（2026-10-26）：對話 meta 記著原帳戶（連續換了好幾個時是第一個；使用者自己換了帳戶就不記），重置時間過了 1 分鐘（不知道的等用完之後 30 分鐘、而且之後查到的用量有額度）、已登入訂閱、用量顯示這個模型還有額度時，閒置的對話由每 30 秒的維護換回、或在下一則一般訊息送出之前換回（斜線指令、問卷的答案、隱藏的接續不換）；執行中、有存活的背景工作、有等你回應的卡片、換版中、排著過載重試、有回溯點時不換，閒置時另外避開 10 分鐘內有動靜的 Remote Control。換回同 setAccount（新 session＋前文節錄），跨代理換回的還原原本的模型、推理強度、權限模式、自訂代理與輸出風格，對話加一則 noticeKind accountSwitch 的 Notice。以 setting 修改。桌面專屬（瀏覽器版的設定頁不顯示，伺服器當成其他鍵回 `desktop_only`）
  "autoSwitchCrossProvider": false,     // 額度用完而同種代理沒有可用帳戶時，允許自動改用其他代理種類（Claude／Codex／Antigravity／Grok）的可用訂閱帳戶以前文節錄接續（預設關；需同時開啟 autoSwitchAccount）。以 setting 修改。桌面專屬（瀏覽器版回 `desktop_only`）
  "shareCrossCliCustomizations": false, // 跨四家 CLI 共用全域指示與 Skills（預設關）：啟動任一家代理程序前，在背景將 Claude（~/.claude）、Codex（~/.codex）、Antigravity（~/.gemini 與 ~/.gemini/antigravity-cli/skills）、Grok（~/.grok）的全域指示檔與使用者 Skills 雙向增量合併／目錄連結同步（保留原檔備份，登入憑證絕不共用）。以 setting 修改。桌面專屬（瀏覽器版回 `desktop_only`）
  "instructions": "…",                  // 附加給代理的說明（{cwd}、{attachments}、{questionTool}、{backgroundTool} 會被代換；AgentHubRules.BuildAgentInstructions）
  "continueText": "進度如何呢如果還沒做完就繼續", // 輸入區「繼續」鈕送給代理的那一句（setting 收字串：去頭尾空白、最多 2000 字元，空的或等於預設存成預設；AgentHubRules.NormalizeContinueText）。桌面專屬，瀏覽器版改存在這個瀏覽器（continueLocal）
  "conversationDefaults": {             // 設定頁「模型設定」：新對話（與換到另一家的帳戶）一律套用；以 setConversationDefault 修改
    "Claude": {
      "model": "opus" | null,            // 預設模型（models 的 value，"default"＝帳戶預設模型）；null＝沿用上次選擇（每個帳戶上次在對話裡選的，沒設定過就是它）。帳戶的模型清單沒有這個模型時，那個帳戶的新對話用帳戶預設模型
      "permission": "Auto",              // 預設權限模式（沒設定過＝Ask）
      "permissionModes": [ { "value": "Ask", "label": "每步詢問", "description": "…" } ],   // 同 Header.permissionModes
      "models": [                         // 這一家各帳戶 CLI 回報的聯集（依帳戶順序去重，最前面一定有 "default"；都沒回報用內建清單），清單變了宿主就重送 settings；"default" 另帶 resolved（同 Header.models，以這一家第一個帳戶算）
        { "value": "opus", "label": "Opus", "description": "…", "efforts": ["low", "medium", "high", "xhigh", "max"], "effort": "max" | null }   // effort null＝CLI 預設
      ],
      "autoCompact": 400000 | null,      // 自動壓縮上限（上下文到這麼多 token 時 CLI 自動壓縮）；null＝CLI 預設。這一家的所有對話下次啟動程序時套用
      "autoCompactRange": { "min": 67000, "max": 967000 } | null,  // 可設定的範圍；null＝這一家不以 token 數設定（Antigravity、Grok）。Codex 是 20000～2000000
      "autoCompactPercent": 70 | null,   // Grok 的自動壓縮門檻（上下文視窗的百分比）；null＝CLI 預設（模型的設定，通常 80%）
      "autoCompactPercentRange": { "min": 20, "max": 95 } | null   // 只有 Grok 有；兩種範圍都是 null 的代理（Antigravity）網頁不顯示那一列
    },
    "Codex": { … }                       // 同上
  }
}
```

### AppUpdateView
app 自我更新的狀態（`init.appUpdate`、`appUpdate` 訊息；設計 9.7）。由宿主的更新協調器產生，欄位都可能為 null，網頁不能假設存在。

| 欄位 | 型別 | 說明 |
|---|---|---|
| `version`、`build`、`display` | string | 目前版本、build（開發版為 null）、顯示用「0.2.1523（ab12cd3）」 |
| `commitAt` | number | 目前版本的提交時間（Unix 毫秒，同 `init.app.commitAt`）；建置時沒有 git 為 null |
| `managed` | bool | 從受管理的安裝執行（false＝開發版或還沒安裝） |
| `canInstall` | bool | 可以「安裝到這台電腦」 |
| `phase` | string | `idle`、`checking`、`downloading`、`staged`、`blocked`、`failed`、`disabled`、`draining`、`restarting`、`installing`。`staged` 只在啟用條件（設計 8.2）都成立時出現：關掉自動更新、以 `--data` 執行時，有已備妥的版本也是 `disabled`（`staged` 欄位照樣帶版本） |
| `available`、`staged` | string | 有的新版、已備妥的版本（版本號） |
| `downloaded`、`total` | number | 下載進度（位元組）：`downloaded` 是 zip 已下載的位元組（宿主最多每 300 毫秒更新一次），`total` 是解壓後的總大小（manifest 的 `totalBytes`）。兩者單位不同、不能相除，網頁只顯示已下載的 MB |
| `detail` | string | blocked／failed／disabled 的原因。idle 時可能是檢查結果（已是最新版／沒有可用的發佈／已取消），網頁只顯示它、不和「已是最新版」重複；其他階段網頁不顯示（staged 時可能是較新版的檢查問題，以已備妥的版本為準） |
| `blocker` | string | 已備妥但現在不會自動重啟的原因（CLI 正在更新、登入中、已取消 6 小時、開著 Claude Remote Control 的對話正在使用或等你回應…）；網頁只在 `staged` 時顯示 |
| `waiting` | number | 排空中還沒停下的對話數 |
| `forced`、`forceAt` | bool、number | 這次排空是「強制更新」（`appUpdateForce`）；`forceAt` 是還沒停下的對話會被強制停下的時間（Unix 毫秒，按下後 10 分鐘）。排空中才有，其他為 null |
| `handoverTo` | string | 新版自動接手舊版：使用者開了較新的版本，這一份正在排空、之後換成它（版本號）；`draining`／`restarting` 時才有，其他為 null。取消用同一個 `appUpdateCancel`（只放棄這次切換，不影響 app 自動更新） |
| `lastCheckAt`、`lastResultAt` | number | Unix 毫秒 |
| `lastResult` | string | 最近一次更新或退回的結果 |

網頁怎麼用（`Web/appupdate.js`）：
- **橫幅**（主視窗頂部列下方；彈出視窗不顯示）：`staged`＝「新版 X 已下載，閒置時自動重啟套用」（有 `blocker` 時改說明原因）＋「立即重啟」「強制更新」；`draining`＝「正在套用更新：等 N 個對話到安全點」＋「強制更新」（還沒強制、不是交棒時）「取消這次」（強制時是「正在強制更新：等 N 個對話到安全點（HH:MM 起還沒停下的強制停下）」）（有 `handoverTo` 時是「正在切換到新開的 X：等 N 個對話到安全點」）；`restarting`＝「正在重新啟動以套用更新…」（有 `handoverTo` 時是「正在切換到 X…」）；`installing`＝「正在安裝到這台電腦…」；`blocked`＝「新版 X 無法安裝：原因」（可關閉）；其他階段在 `canInstall` 時提供「安裝到這台電腦」（可關閉）。關閉只在這次啟動有效。
- **設定頁「版本與更新」分頁（Clode Myriagent 區塊）**：目前版本（`commitAt` 的完整日期時間接 `display`：「2026/10/23 14:05 · 0.2.1523（ab12cd3）」；沒有 `display` 時用 `init.app` 的 `version` 與 `commitAt`）、「自動更新」開關（`Settings.autoUpdateApp`；`managed` 為 false 時停用）、狀態與上次檢查時間、最近一次結果、「立即檢查」（`managed` 為 false 或檢查中時停用）、`staged` 時的「立即重啟」、`canInstall` 時的「安裝到這台電腦」。狀態列：`staged` 同橫幅，有 `blocker` 時說明原因、不說「閒置時自動重啟套用」（設定對話框蓋住橫幅時才看得到為什麼一直沒重啟）；`downloading` 顯示「下載中 X（已下載 N MB）」；`blocked`／`failed`／`disabled` 接上 `detail`；`idle` 有 `detail` 時只顯示 `detail`。
- **重畫**：橫幅的結構（色調、按鈕、能不能關）與設定頁的按鈕組成沒變時只換文字與 `disabled`，不重建按鈕（下載進度、排空的數字一直更新時，焦點與點擊不會落空，`role="status"` 也只在文字變了才重新朗讀）；`appUpdate` 訊息只更新設定頁的這個區塊與頂部列的版號。
- **頂部列的版號**（品牌名旁的 `#appVer`，`Web/versionrules.js`）：版號前面是這一版的日期時間（`commitAt`，使用者的當地時間「10/23 14:05」，不是今年時加年份；提示另起一行「更新於 2026/10/23 14:05」，讀屏的那句也帶完整日期時間；沒有 `commitAt` 時不顯示時間；手機寬度〔≤480px〕放不下時只留日期時間、日期與時刻疊成兩行，版號只在提示與讀屏）。桌面顯示 `init.app.version`（沒有時版本、build、時間整組用 `AppUpdateView` 的），提示是「Clode Myriagent 版本（build；`dev` 是開發版）」加上更新狀態的一句話；`staged`（其次 `available`）和目前版本不同時換色調、讀屏多說「有新版 X」。`idle` 沒有 `detail` 也沒有 `lastCheckAt` 時說「還沒檢查更新」，不說「已是最新版」。點了開設定頁的「版本與更新」分頁。瀏覽器版顯示「網頁版 ＋這次載入第一則 `init` 的 `app.commitAt` 與 `app.build`」（伺服器的 `app.version` 固定，只有 build 會變），之後的 `init` 的 build 不同（含從有變成沒有）時換成警告色調，提示寫出新的 build（重新載入的橫幅照舊由 `Web/liveview.js` 顯示）；點了在新分頁開伺服器的「版本與更新」頁（`/#releases`）。兩個 build 都沒有時不顯示。

## 四、宿主提供的網址

| 網址 | 說明 |
|---|---|
| `https://myriagent.local/index.html`（`?mode=popout&conversation=c1`） | 網頁本體；宿主送出時把 `<html data-theme>` 換成目前主題（`dark`／`light`，跟隨系統時是當下的結果），網頁在 `init` 到達前就沿用它，淺色主題不會先畫一次深色 |
| `https://myriagent.local/app.css`、`/app.js`、`/icons.svg` 等 | 嵌入資源（白名單） |
| `https://myriagent.local/media?path=<完整路徑>` | 本機圖片／影片（只給宿主登記過的檔案；支援 HTTP Range，影片可拖曳進度） |
| `https://myriagent.local/thumb?path=<完整路徑>&size=<px>`（`&v=<版本>`） | 縮圖（Windows 外殼縮圖，圖片與影片都有；PNG，保留透明；可快取 5 分鐘）。檔案瀏覽器列出的檔案另帶 `v`（修改時間＋大小）：檔案被覆寫後網址跟著變，不會沿用快取的舊縮圖；宿主不讀 `v` |
| `https://myriagent.local/open?path=<完整路徑>` | 回覆中的本機檔案連結：Markdown 連結，以及文字裡直接寫出、或整段行內程式碼就是的完整路徑（渲染器自動變成連結，顯示原文、網址去掉 `:42` 這類行號；別台電腦的對話沒有網址，照原文顯示）（網頁攔下點擊自己處理：可直接顯示的圖片、影片開燈箱或播放器〔改用 `/media?path=`，宿主照樣只給登記過的檔〕，其他檔案開「下載或開啟檔案」框，送 `saveFile` 或 `openPath`；不再送 `openLink`，宿主的 `openLink` 照舊認這個網址） |

〔同步〕瀏覽器模式的網址見第六節。

## 五、CLI 熱更新的呈現〔熱更新〕

CLI 在執行中被更新（app 觸發、使用者在終端機更新、互動式 claude 的背景更新、npm）後，宿主讓所有對話改用新版：閒置的直接關掉舊程序（下一則訊息自然用新版、不送接續訊息）；執行中的在安全點（沒有工具在執行、沒有等你回應、沒有存活的背景工作）以新版重開並自動接續。網頁只負責顯示宿主給的狀態。

| 欄位 | 產生者（宿主內） | 網頁怎麼用 |
|---|---|---|
| `CliView.changing`、`installBroken`、`lastChange`、`update.phase`、`update.startedAt` | CliManager（安裝偵測與更新流程） | CLI 卡的狀態列與「重新安裝」 |
| `CliView.update.held`／`waiting`／`overdue`、`restartPolicy`、`runningOnOldVersion`、`restartPending`、`restartWaiting`、`restartDone` | 中樞的重開協調 | CLI 卡的換版彙總、「立即套用」「現在中斷」「取消更新」 |
| `Header.restart`、`Header.status` | 中樞的重開協調 | 對話標頭的換版狀態列、「立即套用」「繼續」 |
| `Item.noticeKind`、Notice 文字、失效卡片的 `output` | 中樞 | 對話內的提示 |
| `Settings.cliRestartPolicy` | 設定（只能在這台電腦改） | 設定頁「CLI 更新後」 |

**CLI 卡**（設定頁、儀表板）：例如「Claude Code 2.1.297 已安裝（外部更新）・3 個對話換版中：1 個等工具結束、1 個等你回應、1 個已接續」——前半取 `version`＋`lastChange.origin`，後半依 `restartWaiting[].state` 計數、加上 `restartDone.continued`。`update` 不為 null 時顯示進度（`progress` 文字）；`update.phase` 為 `draining` 時顯示「現在中斷」，`overdue` 時改成問句並加「取消更新」。`installBroken` 不為 null 時以錯誤樣式顯示它與「重新安裝」。

**頂部列的 CLI 徽章**（點了開設定的 CLI 區）：與 CLI 卡的狀態膠囊同一套規則（`Web/hotupdate.js` 的 `cliHotState`／`cliBadgeState`），優先順序是安裝不一致（`installBroken`，錯誤樣式）＞更新中（`update` 或 `busy`）＞安裝變動中（`changing`）＞換版中（`restartPending`）＞有新版；兩種 CLI 同一個狀態時寫「CLI …」，各對話的狀態放在提示文字。

**對話標頭**：`restart` 不為 null 時在標頭顯示一行狀態（`detail`），依 `canApplyNow`／`canContinue` 顯示按鈕；`running` 在重開期間維持 true（使用者看到的是同一輪，佇列照常排隊）。`status` 例：「CLI 已更新，等目前的指令完成後切換」「正在以新版重新啟動…」「等待 Codex 更新完成…」「已用新版自動接續…」。

**Notice**（`noticeKind: "cliUpdate"`，文字由宿主產生）例：
- 「已用 Claude Code 2.1.297 自動接續（中斷時正在執行：npm test，已執行 42 秒）」／「已用 Codex 0.162.0 自動接續（重啟時沒有工具在執行）」
- 「CLI 已更新到 2.1.297；這一輪沒有自動接續，原因：上一個操作〈git push〉結果未知，請確認後按「繼續」或送出新訊息」
- 「CLI 已更新到 2.1.297；切換時停止了還在跑的背景工作，已請代理確認背景工作做到哪裡、需要的話重新執行」（等背景工作滿 30 分鐘或按了「立即套用」；緊接著的一輪是隱藏送出的確認訊息）／被停掉的是高風險操作時：「CLI 已更新到 2.1.297；切換時停止了還在跑的背景工作，沒有自動請代理確認，原因：上一個操作〈git push〉結果未知，請確認後按「繼續」或送出新訊息」
- 「以新版 CLI 重新啟動失敗：…；按送出繼續」
- 接續訊息（自動接續時 Claude 的 `CLAUDE_CODE_RESUME_PROMPT`、Codex 的 `turn/start` 文字，以及按「繼續」時宿主送出的那則）一律不顯示成使用者訊息；按「繼續」時改加一則 Notice：「已請代理從中斷處繼續（CLI 更新後的接續訊息，不是你送的）」

**卡片失效**：產生 Approval／Question／PlanApproval 卡的 CLI 程序重開後，宿主把那些還在等待的卡片改為 `Cancelled`，`output`＝「CLI 已重新啟動，這張卡已失效；代理會視需要重新提出」（代理接續後會以新的請求重提，絕不把舊卡的回覆送給新程序）。Codex 的非同步提問（`answerAsMessage`）不受影響。那張核准對到的 Tool 卡（還沒核准、沒有執行）同樣改為 `Cancelled`，`output`＝「CLI 已重新啟動，這個操作還沒有核准，沒有執行；代理會視需要重新提出」，不是「被中斷、結果未知」；執行到一半才要求的核准（Codex 的網路存取、擴大權限）除外，那個工具照被中斷處理。

**系統匣**：同一波換版的所有對話都處理完後，宿主送一則彙總通知（不逐一跳），不經網頁。

## 六、瀏覽器模式的傳輸〔同步〕

本節是 WP3.5（網頁傳輸層）與 WP3.6（伺服器的瀏覽器閘道）的契約；兩邊一起改。與設計文件的差異見 `docs/plans/2026-10-10-multi-device-sync.md` 14.6。

1. **網址**：網頁 `https://hub.example.com/app/`（`/app` 回 302 到 `/app/`，查詢字串照帶）；彈出視窗 `/app/?mode=popout&conversation=…`；登入頁 `/`（未登入或工作階段失效時導向這裡）。網頁匿名可下載（設計 8.3），hub 連線才要登入。
2. **伺服器插入的腳本**：伺服器在 `index.html` 的 `<script type="module" src="app.js"></script>` 之前依序插入 `<script src="/lib/signalr-x.y.z.min.js"></script>` 與 `<script src="/transport-hub.js"></script>`。兩支都是**傳統（非 module）腳本**，在 `app.js` 之前執行，用來在 `app.js` 匯入 `store.js`／`transport.js` 之前把傳輸層掛好。只在 `wwwroot/lib/` 恰好一個 `signalr-<主>.<次>.<修>.min.js` 而且 `wwwroot/transport-hub.js` 存在時才插入；兩者都不在就不插（網頁照舊顯示「這個頁面要在 Clode Myriagent 裡開啟」）；組合不合法（程式庫不只一個、有 `transport-hub.js` 卻沒有程式庫）時 `/app/*` 回 503，其他端點照常。`index.html` 根元素的 `data-theme`：已登入而且漫遊偏好是 `light` 時是 `light`，其他一律 `dark`（`system` 交給 `theme.js`）。
3. **hub**：`wss://hub.example.com/hubs/v1/browser`，只允許 WebSockets（可以 `skipNegotiation: true`），JSON 協定（伺服器以 Newtonsoft 處理，線上格式與一般 JSON 協定相同），cookie 由瀏覽器自動帶，`Origin` 必須是本站。**不開 stateful reconnect**：不要依賴 `withStatefulReconnect()`（呼叫了也無害，伺服器不同意時照常連線）。換了新連線一律先在網頁送 `connection online` 再送 `ready`。
4. **方法**：
   - `invoke("Cmd", {commandId?, envelope?, cmd})` → `{accepted, commandId?, error?}`（null 欄位省略；`commandId` 有帶就原樣帶回）。`cmd` 就是第二節的網頁指令物件（`{cmd: "setting", key, value}`）。**頂層只能有這三個鍵**，多出任何鍵整筆回 `invalid`（`cmd` 物件內不認得的欄位則忽略）。帶 `envelope` 一律 `not_available`：〔遠端〕第 4 階段的指令信封由伺服器簽（`docs/sync-protocol.md` 14.4），網頁永遠不帶，這個鍵保留不用。`Cmd` 在伺服器本機提交（sync.db）之後才回傳，只要幾毫秒，絕不等任何電腦；〔遠端〕轉送給電腦的指令的結果另以 `commandResult` 送回發起的連線。
   - `invoke("ClientState", {visible, focused, openConversationIds, tzOffsetMinutes})`：分頁休眠回來、切換可見度時送；不合格時丟 `HubException: invalid`。第 3 階段伺服器只驗證並保存；〔遠端〕WP4.2 起拿它決定清單的日期分組（`tzOffsetMinutes`）、「看得到」與「看見」（第 8 點；`openConversationIds` 只取對話 id 形狀的，其他忽略）。四個欄位都必填：`visible`、`focused` 是布林；`openConversationIds` 是非空字串的陣列；`tzOffsetMinutes` 是 JSON 整數，**照 JS `new Date().getTimezoneOffset()`**（UTC 減本地時間的分鐘數，臺灣是 `-480`），不要自己反號。選用的 `features`（2026-10-23 起）：這個網頁收得了的伺服器訊息，非空字串的陣列、最多 16 個、每個最多 32 字（不合格也是 `invalid`）；不認得的名稱忽略，目前只有 `patch1`（收 `itemPatch`，第一節）。其他欄位忽略。
   - 伺服器→網頁：`on("Msg", msg)`，`msg` 就是第一節的宿主訊息。
5. **commandId**：ULID，26 字、大寫 Crockford Base32（字母表 `0123456789ABCDEFGHJKMNPQRSTVWXYZ`）。產生方式：前 10 字是 `Date.now()` 的 48 位元（大端、每 5 位元一字，第一字自然 ≤`7`），後 16 字是 `crypto.getRandomValues(new Uint8Array(10))` 的 80 位元。格式不對時整個 `Cmd` 回 `invalid`。第 3 階段只原樣帶回、不去重。〔遠端〕轉送給電腦的指令另由伺服器產生自己的指令 id（ULID，信封的 `cid`，電腦以它去重），網頁的 commandId 只用來對應 `commandResult`（伺服器另存，設計 14.11 D17）。
6. **錯誤代碼與送達管道**：

   | 代碼 | 怎麼送到 | 網頁怎麼做 |
   |---|---|---|
   | `invalid` | `Cmd` 結果的 `error`；`ClientState` 丟 `HubException: invalid` | 網頁的錯（欄位、型別、長度）；記錄，不重送 |
   | `desktop_only` | `Cmd` 結果 | 這個動作只能在電腦上做（伺服器另送一則 `toast`，見第 9 點） |
   | `not_available` | `Cmd` 結果 | 網頁版還沒開放（第 3 階段的遠端指令；〔遠端〕第 4 階段各工作包開放之前也是；同上） |
   | `conflict` | `Cmd` 結果 | 目標已刪除（例如帳戶已從目錄刪除） |
   | `unavailable` | `Cmd` 結果 | 伺服器暫時不能寫入（停機中）；稍後再試 |
   | `rate_limited` | `Cmd` 結果；`ClientState` 丟 `HubException: rate_limited`；negotiate／升級是 HTTP 429 `{"error":"rate_limited"}`＋`Retry-After` | 退避後重試 |
   | `too_many_connections` | 連線建立後立刻被關閉，關閉錯誤是 `Connection closed with an error. HubException: too_many_connections` | 退避重連（30 秒起），提示「開太多分頁」 |
   | HTTP 401 | negotiate／升級 | 未登入或工作階段已失效：導向 `/` |
   | HTTP 403 `forbidden_origin` | negotiate／升級 | 來源不是本站（不應發生） |

7. **伺服器主動關閉連線**（不帶原因）：工作階段被撤銷、登出、改密碼、閒置或絕對期限到了（重連時 negotiate 401 → 導向 `/`）；送出佇列 60 秒內第 3 次溢出，或單則訊息 15 秒送不出去、完全卡住不讀時 SignalR 傳輸層的送出逾時（`TransportSendTimeout`，MyriagentHub 沒設、預設 10 秒，通常比 15 秒先到，伺服器記錄沒有 `slow_client`；設計 14.20）（慢用戶端，重連即可）；停機（照退避重連；停機開始後連上的新連線也會立刻被關閉）。
8. **伺服器送的訊息**（第 3 階段）：`init`、`accounts`、`devices`、`cli`、`settings`、`toast`；不送 `connection`（傳輸層自己產生）與 `sync`（只有桌面）。`init` 的內容：`mode` 照 `ready`、`popoutConversationId:null`、`app.version`＝伺服器版本、`settings` 只有四個鍵、`accounts`（瀏覽器形式，第三節 AccountView）、`conversations:[]`、`cli:{}`、`providers`（只有 Claude、Codex，`safeMode:false`）、`devicesCli`（`{deviceId: {Claude?, Codex?}}`：回報過 CLI 的每台未撤銷電腦，沒有就是 `{}`）、`layout:null`（由傳輸層從 localStorage 注入）、`remote:{mode:"browser", deviceId:null, devices, sync:null}`。`accounts`、`devices`、`cli`（`{cli:{}, devicesCli}`）、`settings` 都是整份取代、最多每 500 ms 一次、內容沒變不重送。**收到第二則 `init` 要整份取代畫面狀態**（伺服器的 resync：送出佇列溢出時伺服器清空佇列、重送 `init`）。`init` 入列時清掉之前排著、還沒送出的快照類訊息（`init`、`accounts`、`devices`、`cli`、`settings`），**排著的 `toast` 保留、依原順序排到 `init` 後面**：網頁可能在第二則 `init` 之後才收到之前指令的 toast（溢出時照樣整個清空）。
   〔遠端〕第 4 階段另送（各工作包開始送時才出現；WP4.1 不送任何新訊息）：**已送**（WP4.2 起，沒有開關，伺服器部署就送）：`conversations`、`conversation`、`olderItems`、`header`、`item`、`removeItem`、`composer`、`conversationClosed`、`resync`、`notify`、`clipboard`，`init.conversations` 也不再是空的；**已送**（WP4.5 起，同樣沒有開關）：`commandResult`、`control`，以及電腦的指令輸出 `sendRejected`、`accountLogin`、`toast`（只給發起的連線，伺服器照型錄的輸出白名單過濾、不保存）；**已送**（WP4.7 起，同樣沒有開關）：電腦的指令輸出 `files`、`fileSearch`、`fileRef`（只給發起的連線；伺服器投影成第一節的形狀、`requestId` 與 `conversationId` 要等於指令的，媒體參照換成本站網址，不合格的整則丟掉、計進 `ops/status` 的 `commands.droppedOutputs`）。**通知類**（`toast`、`commandResult`、`notify`、`clipboard`、`fileRef`、指令輸出的 `sendRejected`、`accountLogin`）和 `toast` 一樣在 `init` 屏障保留、在連線鎖裡檢查「已送過 `init`」再入列（設計 14.10）；**溢出時整個佇列清空，通知類也一起遺失**（同第 3 階段的 `toast`；伺服器接著重送 `init`）。`control` 不是通知類：每則 `init` 之後在同一輪重送一則（合併鍵 `control`，排著的被新的取代、被 `init` 清掉），擁有者密碼狀態改變時另送；`resync` 與對話的快照、增量照快照類處理（`init` 之後網頁重新 `open`）。伺服器入列 `init` 時同時忘掉那條連線開著的對話與還沒處理的 `open`／`loadOlder`（`visible` 的清單保留），網頁收到 `init` 要重新 `open` 畫面上的分頁。
   〔電腦狀態〕`deviceStats`（第一節）：只送給最後一次 `monitor` 是 `on:true` 的連線（每條連線各自訂閱、不跨重連：`init` 之後網頁依目前狀態重送 `monitor`），最多約每秒一則、合併鍵 `deviceStats`（排著的被新的取代，被 `init` 清掉）；`devices` 是在線電腦經裝置 hub 回報的狀態（沒有 `self:true` 的項目），`hub` 是伺服器主機自己。
   〔遠端〕**觀看的三個層次**（WP4.2；伺服器以連線為單位記，同一個工作階段的分頁各算各的）：
   - **開著**：這條連線 `open` 過、還沒 `close`。只決定伺服器推不推那個對話的增量（`header`、`item`、`removeItem`、`composer`、`resync`）。
   - **看得到**：`ClientState.visible` 為真，而且對話在這條連線最後一次 `visible` 指令的清單裡，或在 `openConversationIds` 裡（只取對話 id 形狀的）；只是開著不算。看得到的連線不收 `notify`；擁有者電腦只對有人看得到的對話送即時更新（`docs/sync-protocol.md` 14.3 的 `WatchChanged`）。
   - **看見**：看得到，而且 `ClientState.focused` 也為真。決定未讀的標與清（第三節 ConversationSummary 的 `unread`）。
9. **指令分類**（`Cmd` 的 `cmd.cmd`；大小寫有別）：

   | 類別 | 指令 | 結果 |
   |---|---|---|
   | 伺服器處理 | `ready`、`setting`（只收四個漫遊鍵）、`accountUpdate`（只收 `label`、`color`）、`accountReorder`、`deviceRename`；〔遠端〕WP4.2 起另有 `newConversation`（伺服器草稿）、`copyItem`（回 `clipboard`），2026-10-23 起另有 `searchConversations`（回 `conversationSearch`），見表下的說明；〔電腦狀態〕`monitor`（記在這條連線，第 8 點）；`clientError`（寫進伺服器的記錄，不寫資料庫、不稽核、被拒也不送 toast） | 本機提交後 `accepted:true` |
   | 接受、不處理 | `layout`、`open`、`close`、`visible`、`loadOlder`（〔遠端〕WP4.2 起 `open`、`close`、`visible`、`loadOlder` 由伺服器處理，見表下的說明） | `accepted:true` |
   | 桌面專屬（`desktop_only`） | 所有 `sync` 開頭的指令（`syncPair`、`syncCancelPair`、`syncUnpair`、`syncPause`、`syncRetry`、`syncConfirm`、`syncSetPolicy`，〔遠端〕`syncSetConversationPolicy`、`syncLoadRemoteAudit`）、`setConversationDefault`、`cliApplyNow`、`cliContinue`、`cliCancelUpdate`、`chooseFolder`、`attach`、`addFiles`、`pasteImage`、`addPathAttachment`、`openFolder`、`openPath`、`revealPath`、`saveFile`、`copyMedia`、`openLink`、`copyText`、`popout`、〔多視窗〕`newWindow`、`tabDrag`、`tabDragEnd`、`tabMoveIn`、`moveToNewWindow`、`closeWindow`、`accountOpenFolder`、`accountResetCredit`、`autoTitle`、`setAgent`、`setOutputStyle`、`codexControl`、`goalClear`、`taskDetail`、`taskStop`、`promptHistory`、`cancelSteer`、`quotaWaitCancel`、`rewindPreview`、`rewind`、`forkConversation`、`listExternalConversations`、`importExternalConversation`、`gitRefresh`、`launchAction`、`launchSelect`、`launchLog`、`launchRescan`、`programStop`、`mcpCheck`、`mcpAction`、`healthCheck`、`healthFix`、排程頁的 `scheduleWatch`、`scheduleRefresh`、`timedSave`、`timedAction`、`peerUpload`、`peerUploadCancel`、`peerFileSave`、`draftSetDevice`、電腦狀態面板的 `peerXferSave`、`peerXferCancel`、`peerXferReveal`、`xferPickFolder`、app 自我更新的 `draftSnapshot`、`appUpdateCheck`、`appUpdateRestartNow`、`appUpdateForce`、`appUpdateCancel`、`appInstallHere`；`setting` 的 `instructions`、`closeToTray`、`autoUpdateCli`、`autoUpdateApp`、`cliRestartPolicy`、`claudeRemoteControl`、`autoSwitchAccount`、`autoSwitchCrossProvider`、`shareCrossCliCustomizations`、`claudeFallbackModel`、`claudeIdleResumeWarning`、`autoResumeAfterQuota`；帶 `safeMode` 或 `shareCustomizations` 的 `accountUpdate`（整筆不套用） | `accepted:false` |
   | 〔遠端〕轉送電腦（WP4.5 起） | 轉給對話的擁有者：`send`、`voiceMessage`（2026-10-23）、`cancelQueued`、`stop`、`answer`、`skipQuestion`、`approve`、`approvePlan`、`rename`、`delete`、`setOption`、`setAccount`、`setFolder`、`resetFolder`、`removeAttachment`，WP4.7 起另有 `listFiles`、`fileSearch`、`addUploaded`、`fileFetch`（輸出 `files`、`fileSearch`、`fileRef` 經伺服器投影與改寫，見第一節；`addUploaded` 先查上傳，見第 12 點的 E4）；轉給指定的電腦：`accountAdd`、`accountLogin`、`accountLoginCancel`、`accountLogout`、`accountRemove`、`accountRefresh`、`cliCheck`、`cliInstall`、`cliUpdate`、`accountLoginDevice`、`accountLoginCode`（2026-10-26）、排程頁的 `scheduleAction`、`scheduleAgent`（2026-10-26，S3）、電腦狀態面板的 `deviceBrowse`、`deviceFetch`、`devicePut`（2026-11-09；輸出 `files`、`fileRef` 的 `conversationId` 是 null，`fileRef` 的參照是 `xfer`）與整個資料夾的 `deviceTree`、`deviceFetchMany`（輸出 `fileTree`、`fileRefs`，`fileRefs` 的參照同樣是 `xfer`）、遠端桌面的 `deskOpen`（之後的影格與輸入走 `desk`，見第一節） | 一律要帶 commandId（沒帶回 `invalid`）。伺服器簽好信封、寫進指令列之後回 `accepted:true`，結果一律看 `commandResult`（第一節；電腦不在線、不支援、不是擁有者等也是 `commandResult rejected`）。`Cmd` 只回伺服器自己判得出的錯：`invalid`（形狀不合格、指定的電腦不存在或已撤銷）、`conflict`（對話或帳戶已刪除，另送 `conversationClosed` 或 toast）、`unavailable`（寫不進去）。仍是 `not_available` 的：沒帶 `deviceId` 而判不出目標的指令（沒有帳戶的 `cliCheck`、`accountRefresh {accountId:null}`、帳戶連結到兩台以上、`accountAdd` 沒有 `deviceId`；「所有電腦登出」〔`accountLogout` 不帶 `deviceId`〕例外，見第一節 `commandResult`），以及 `conversationId` 是第 3 階段網頁的本機 id 的。`conversationId` 是伺服器草稿的見表下的說明 |
   | 尚未提供（`not_available`） | 第 5 階段的 `takeover`、`handoffCancel`（`listFiles`、`fileSearch`、`addUploaded`、`fileFetch` 在 WP4.7 合併前也在這裡，WP4.7 起轉送電腦） | `accepted:false`。程式的分類（`BrowserCommandRules`）照舊把上一列的指令與 `newConversation`、`copyItem`、`searchConversations` 也列成 not_available（和型錄的一致性測試對齊），由 WP4.2 的 `IBrowserConversationCommands` 與 WP4.5 的 `IBrowserCommandRouter` 接手（`src/MyriagentHub/Realtime/BrowserCommandSeams.cs`）；WP4.5 的 `CommandRouteRules.Deferred`（延後、照舊回 not_available 的轉送指令）在 WP4.7 合併後是空的 |
   | 其他 | 不認得的名稱、`setting` 的其他鍵、不合格的值 | `invalid` |

   被拒時伺服器另送 `toast`（`level:"error"`）的條件：指令名稱認得（不認得的名稱只回 `invalid`）、不是 `rate_limited`、不是網頁自動送的（`newConversation`、`listFiles`、`fileSearch`、`searchConversations`、回應宿主的 `draftSnapshot` 與「接受、不處理」那一列）、這條連線已送過 `init`（伺服器把 `init` 入列與標成送過在同一把鎖裡一起做，toast 也在同一把鎖裡檢查再入列：網頁一收到 `init` 就送的指令也照這些條件送 toast，toast 一定在 `init` 之後才送到）、同一 cmd 10 秒內沒送過。〔遠端〕轉送電腦的指令被拒（`commandResult` 是 `rejected` 或 `unknown`）時，伺服器在 `commandResult` 之後照同樣的條件另送一則 toast（文字是電腦的 `message`，沒有就照代碼；電腦已經轉來過 toast 就不再送；「所有電腦登出」送出時是一則 success toast，之後各台的結果每台各一則、不節流）。WP3.5 在瀏覽器模式最好直接隱藏或停用這些按鈕；`openLink`、`copyText`、`popout` 依設計 4.11 由網頁自己處理，不必送到伺服器。

   〔遠端〕WP4.2 起伺服器處理的對話指令（`Cmd` 都在伺服器本機處理完就回，不等電腦）：
   - `open {conversationId}`：對話 id 形狀的（32 個小寫十六進位）查伺服器的對話目錄，找得到就記下請求、之後送 `conversation`；不存在或已刪除回 `invalid` 並送 `conversationClosed`；同一條連線排著的請求滿了（128 個）回 `rate_limited`。伺服器草稿的 id 在同一個工作階段回同一份草稿；已經換成正式對話的草稿在墓碑還在時再送一次帶 `replacedBy` 的 `conversationClosed`、不算錯誤（第一節 `conversationClosed`，合併前審查 LIVE-1）。其他形狀的 id（第 3 階段網頁的本機 id）照舊接受、不處理。伺服器上還沒有任何項目的完整同步對話，被 open 時伺服器請擁有者電腦插隊送整份（同一個對話 10 分鐘最多一次）。
   - `loadOlder {conversationId, beforeId}`：同 `open` 查目錄（`beforeId` 要是項目 id 的形狀），回 `olderItems`（`beforeId` 之前，頁大小同 `open`；`beforeId` 原樣帶回，合併前審查 LIVE-2）；草稿沒有項目，接受、不處理。
   - `close {conversationId}`：不再開著；伺服器草稿丟棄（送 `conversationClosed`）。
   - `visible {conversationIds}`：記下這條連線最後一次的清單（最多 100 個對話 id），用在「看得到」與「看見」（第 8 點）。
   - `newConversation {accountId, paneId}`：開一份只存在伺服器記憶體的草稿（id 是 `d` 加 26 字 ULID；不進清單、不通知、不算未讀），帳戶是要求的邏輯帳戶，沒有就用第一個，一個都沒有回 `not_available`；回 `conversation`（`header.isDraft=true`、標題「新對話」、沒有擁有者電腦〔WP4.6 起 `header.deviceId` 是選填的 `deviceId`〕、`paneId` 原樣送回）只給發起的連線。每個工作階段最多 8 份（超過丟最久沒用的），工作階段的最後一條連線斷了 2 分鐘內重新連上還在，伺服器重開就丟。WP4.5 起另收選填的 `deviceId`（裝置 id 的形狀才算；新對話要在哪台電腦建立，第一次 `send` 時才檢查那台存不存在）。
   - 〔遠端〕草稿上的指令（WP4.5 起；`conversationId` 是草稿 id）：找不到草稿（別的工作階段的、過期、被丟、已關閉）回 `invalid` 並送 `conversationClosed`。
     - `setOption`、`setAccount`、`setFolder`、`resetFolder`、`rename` 只改伺服器記憶體裡的草稿（同桌面草稿的語意：模型、強度、權限模式的 `default` 或空字串是預設；權限模式只收 `header.permissionModes` 裡的；`setAccount` 只收 `accounts` 裡的帳戶，換到另一家代理時模型、強度、權限模式回到預設；資料夾只收本機磁碟的完整路徑；標題去掉前後空白、最多 80 字，空的不改），接著對這個工作階段的每一條連線送草稿的 `header`，發起的連線另收一則 `commandResult done`；不合格回 `invalid`。不寫指令列、不轉給電腦、不寫稽核。
     - 第一次 `send`：伺服器組成 `conversationCreate`（帳戶、權限模式〔沒選是每步詢問〕、模型、強度、資料夾、標題照草稿，`text` 是空字串時省略；草稿的 `send` 只帶 `text`，組出的 `conversationCreate` 沒有 `uploads`——草稿上的 `addUploaded` 回 `invalid`，第 4 階段的新對話在送出第一則之前不能附檔，網頁在草稿停用附加，第一則送出、對話建立之後再 `addUploaded`），送到草稿的 `deviceId`，沒有就送到這個帳戶連結狀態正常、在線而且支援遠端指令、最近連上的那台；之後和一般的遠端指令一樣看 `commandResult`。新對話的 id 是伺服器的指令 id 推得的（`docs/sync-protocol.md` 14.4），出現在 `conversations` 清單裡；被拒（`rejected`、`Cmd` 的 `invalid`／`unavailable`）時以 `sendRejected`（`conversationId` 是草稿 id）把文字還回草稿的輸入框，可以再送。送出之後、還沒被拒之前再送一次 `send`：`commandResult rejected busy`「新對話還在建立中，請稍候再送」並還原文字。新對話出現在伺服器之前（電腦還沒建立；`done` 之後也一樣）草稿留著，同一份再送一樣回 `busy`。新對話出現時伺服器把草稿換成它（WP4.6 起：對這個工作階段的每一條連線送 `conversationClosed` 帶 `replacedBy`、丟掉草稿，見第一節），之後對草稿 id 的指令同找不到的草稿。
     - 草稿上的其他指令（`stop`、`approve`、`delete`…）回 `invalid`。
   - `copyItem {conversationId, itemId}`：見第一節的 `clipboard`。
   - `searchConversations {requestId, query}`：見第一節的 `conversationSearch`。
10. **語意**：
    - `accountReorder {ids}`：1～500 個不重複的 `la_…`；先排請求裡列到而且存在的帳戶，沒列到的接在後面並維持原本的相對順序，已不存在的 id 略過。
    - `deviceRename {deviceId, name}`：名稱清理後 1～60 字；已撤銷的裝置回 `invalid`；結果以 `devices` 回來（`name` 變、`signedName` 不變）。
    - `setting`：值照第三節 Settings 的型別（`fontSize` 是 12～18 的整數）。
    - 瀏覽器的 AccountView 彙總各台的狀態：任一台 `ready` 而且身分相符就是 `ready`（不論那台在不在線），`statusText` 是「{n} 台已登入（{m} 台在線）」；否則依 `checking`（「檢查中」）＞`error`（含 ready 但身分不符與不認得的狀態，「{k} 台有問題」）＞`apiKey`（「API Key 登入」）＞`notLoggedIn`（「沒有電腦登入」）＞`notInstalled`（「未安裝 CLI」）；沒有任何電腦連結到這個帳戶是 `notLoggedIn`（「沒有電腦使用這個帳戶」）。不依各電腦的 `checkedAt`（電腦的時鐘）選。
11. **上限**：單則訊息 ≤128 KB（〔遠端〕WP4.5 起，之前是 64 KB；送出前自檢，超過會讓伺服器關掉整條連線）；JSON 深度 ≤8、節點 ≤4096、屬性名 ≤4096 字。單一字串值依指令（〔遠端〕WP4.5 起，之前一律 4096）：轉送給電腦的指令（型錄裡由電腦執行的；WP4.7 起含 `addUploaded`、`listFiles`、`fileSearch`、`fileFetch`）≤16,000 字，而且整個 `cmd` 物件（不縮排的 JSON）≤98,304 位元組（`MaxBodyBytes`），各欄位的上限另照指令型錄（`docs/sync-protocol.md` 14.4；`send.text` 是 16,000，其他文字欄位多半是 4096）；其他指令與 `ClientState` 照舊 ≤4096 字。伺服器分兩段檢查：讀指令名稱之前以字串 16,000、屬性名 4096 擋，認出指令之後再照上面的分類檢查，超過都回 `invalid`；`openConversationIds` ≤100；`tzOffsetMinutes` 在 ±840；`Cmd` 與 `ClientState` 合計每個工作階段每秒 20 次（突發 50）；連線每個工作階段 8 條、全部 32 條；negotiate 匿名每 IP 每分鐘 30 次、已登入每個工作階段每分鐘 60 次。
12. **HTTP 端點**（擁有者 cookie＋`Origin` 本站＋`X-Myriagent-Csrf: 1`）：撤銷電腦、移除電腦與刪除帳戶要近期驗證（403 `{"error":"step_up_required","stepUp":true}` 時先 `POST /api/v1/auth/stepup {password}` 再重送）；〔遠端〕上傳（E4）與第 4 階段轉送給電腦的遠端操作，依擁有者 2026-10-19 指示（自用、伺服器可信任）不要求近期驗證：
    - `POST /api/v1/devices/{id}/revoke {reason: "retired"|"compromised"}` → 200 `{ok:true}`；400 `invalid_request`（原因不對）、404 `not_found`、409 `already_revoked`、503 `unavailable`（伺服器暫時無法處理；可能已經撤銷，重新整理裝置清單再決定要不要重送，重送已撤銷的回 409）。結果以 `devices`（已撤銷的排最後、帶 `revoked`）與 `accounts` 回來。
    - `POST /api/v1/devices/{id}/remove`（2026-10-23，設計 14.22；本文不用，可以不帶）→ 200 `{ok:true, conversations}`（`conversations`＝這次在伺服器上刪掉的對話數）；404 `not_found`、409 `not_revoked`（只有已撤銷的才能移除）、409 `already_removed`、503 `unavailable`（可能已經移除，等裝置清單更新再決定要不要重送）。移除之後那台不在 `devices`（瀏覽器與桌面都不列）、它擁有的對話從清單消失（伺服器上刪除，其他電腦的副本跟著丟掉）。網頁只在已撤銷的電腦卡片上給「移除…」，確認框要說明它同步到伺服器的對話會一起刪除、那台電腦本機的資料不受影響。
    - `POST /api/v1/accounts/{id}/delete` → 200 `{ok:true}`；404 `not_found`；503 `unavailable`。結果以 `accounts` 回來。刪除確認框要說明「電腦之後再回報同一個登入身分時，這個帳戶會再出現」。
    - 〔遠端〕`PUT /api/v1/uploads/{uploadId}`（E4，WP4.7 起；本文是檔案的位元組，一次 PUT、≤512 MB）：`uploadId` 由網頁自己配（1～64 個英數字、`_`、`-`；UUID 去掉大括號或 ULID）。201 新的、200 同一個 id 同一份內容（重傳冪等），都回 `{uploadId, sha256, size}`；400 `invalid`（id 不合格）、409 `conflict`（同一個 id 正在上傳，或已上傳過不同的內容：換一個新的 id）、413 `too_large`（超過 512 MB，讀本文之前就回）、507 `pending_full`（還沒成為附件的上傳合計超過 4 GB）或 `insufficient_storage`（伺服器的 blob 配額或磁碟空間不足）、503 `unavailable`。上傳完 24 小時內以 `addUploaded {conversationId, uploads: [{uploadId, name, size, sha256}]}`（`size`、`sha256` 照回應的值）加進輸入區；伺服器在轉給電腦之前查 `{uploadId, size, sha256}` 都是這台上傳完、沒過期的，不符的 `commandResult rejected invalid`「上傳的檔案在伺服器上找不到、內容對不上或已過期，請重新上傳」。電腦的 `done` 只表示收下：下載與匯入在背景，完成後成為電腦輸入區的附件（瀏覽器版的 `composer.attachments` 照舊是空的，第三節 Composer；這段期間遠端 `send` 回 `conflict`「附件還在處理中，請稍候」，網頁晚一點再送）；失敗時對話裡多一則 Notice。遠端 `send` 只帶網頁上傳、還在輸入區的附件，電腦本機使用者自己附的不帶。
13. **傳輸層的行為（WP3.5）**：
    - 全域：`transport-hub.js` 掛 `window.myriagentHubTransport`（凍結物件：`version:1`、`ok`、`rules`、`post`、`onMessage`、`status`、`retryNow`）；`transport.js` 只在 `version === 1 && ok === true` 時是 `hub`，不看主機名。`status()` 回唯讀快照 `{state, reason, attempt, retryAt, lastCloseAt, hubInit, generation}`（還沒開始連線時 `state` 是 null），`retryNow()` 在已經開始、沒連上、不在連線中、也沒有被導向 `/` 時立刻再試（狀態列的「立即重試」、探針與端到端工具用）。
    - 頁面送出第一則 `ready` 時才開始第一次連線；那則 `ready` 由傳輸層留住，每條新連線依序送 `connection online`、`ready`、`ClientState`。`onMessage` 可以有多個訂閱者（依註冊順序，各自攔例外）。
    - 每次嘗試連線前 `GET /api/v1/auth/me`：200 才以 `skipNegotiation:true`＋WebSockets 連線；401 → `location.replace('/')`；429 照 `Retry-After`（最多 15 分鐘）與退避取大者；其他照退避。`auth/me` 15 秒沒回應當成網路錯誤、`start()` 30 秒沒完成就放棄這一次（照退避再試；WebSocket 還在 CONNECTING 時直接關掉它：程式庫的 `stop()` 結束不了卡在開啟握手的 `start()`，傳輸層以 `withUrl` 的 `WebSocket` 選項包一層記下 socket）；連線設 `withServerTimeout(30000)`、`withKeepAliveInterval(15000)`，不用 `withAutomaticReconnect`，每次嘗試都是新的 `HubConnection`。WebSocket 壓縮（2026-10-23 起）：瀏覽器本來就會提出 permessage-deflate，伺服器照 `Hub:Browser:WebSocketCompression`（預設 `noContextTakeover`：每則訊息各自壓縮、不保留前後訊息的字典；`contextTakeover`、`off`）回應，網頁不必改；裝置 hub 不壓縮（設計 2026-10-23-live-delta 第 5 節）。
    - 退避 0、1、2、5、10、30、30…秒（大於 0 的乘 0.8～1.0）；`too_many_connections` 30～36 秒；收到 `init` 才把次數歸零；`navigator.onLine` 為 false 或連續失敗到 30 秒那一階時 `connection` 是 `offline`，其他是 `reconnecting`；分頁回到可見或收到 `online` 事件時若沒連上立刻重試。
    - `online` 之後 20 秒沒有 `init` 就關掉重連。錯誤代碼一律以「包含 `HubException: <代碼>`」比對。
    - `Cmd` 一律帶 commandId。`rate_limited`：同一個合併鍵只留最新一筆，1、2、4 秒後重送（沿用原本的 commandId），最多 3 次，還被限流就提示「伺服器忙碌，這個變更沒有送出」；`ready` 在 `init` 之前也照樣重送（只要原本那條連線還連著）；`ClientState` 被限流時 2 秒後重送一次。`accountUpdate` 是部分更新（改名只帶 `label`、改色只帶 `color`）：同一個帳戶還沒確定送達的那一筆（等待重送或結果還沒回來）的 `label`、`color` 併進新的一筆（新值優先），舊的不再單獨重送；被接受或被拒（不是 `rate_limited`）之後就不再併。傳輸層自己的提示以 `toast` 訊息（形狀同第一節）交給網頁，同一則 10 秒內一次。
    - 網頁的設定頁先改畫面再送 `setting`：這一筆被伺服器拒絕（`rate_limited` 以外）、限流重送放棄、太大或沒有連線而沒有送出，而且同一個鍵沒有更新的一筆在路上或等待重送時，傳輸層以伺服器最近一次給的值（`init.settings`、之後的 `settings` 推送）送一則只有這個鍵的 `settings` 給網頁，畫面改回和伺服器一致。
    - 已連線而且這條連線收到過 `init` 才送指令；否則使用者操作的指令不送、網頁提示「目前沒有連線，這個變更沒有送出」，網頁自動送的（`visible`、`open`、`close`、`loadOlder`、`newConversation`、`listFiles`、`fileSearch`）靜默丟棄。沒有離線佇列。
    - `ClientState`：每條新連線在 `ready` 之後送一次；可見度與焦點改變時以 200 ms 合併、沒變不送；〔遠端〕`openConversationIds`＝最近一次 `visible` 指令的對話 id（只留對話 id 形狀的、去重、最多 100 個；送不送得出去都記，新連線的第一則就帶），清單變了也以 200 ms 合併重送 ClientState（第 3 階段一律 `[]`）。
    - 〔遠端〕`post(msg)` 回傳這一筆的 commandId（網頁以它對應 `commandResult`）；沒有送出（`ready`、在瀏覽器裡處理的、序列化不了、沒有連線、超過第 11 點的上限）回 `null`。`version` 仍是 1（只加不改）。
    - 〔遠端〕合成的 `commandResult`：`{type:"commandResult", commandId, state:"rejected", code, message:null, tier:null, origin:"transport"}`，只在伺服器沒有處理、傳輸層也不會再送這一筆時給網頁：`Cmd` 最終被拒（`code` 是它的 `error`，`rate_limited` 以外）、限流重送放棄（`rate_limited`）、被限流之後沒有重送就被丟掉（`not_sent`：等待重送時斷線、連線換了，或同一個合併鍵有更新的一筆）。不另外提示（伺服器已另送 `toast` 或 `sendRejected`；限流放棄的「伺服器忙碌」照舊一次）。`Cmd` 呼叫因斷線被取消時不合成（伺服器可能已經執行），網頁在下一則 `init` 時把還在等的當成 unknown。
    - 〔遠端〕`openLink` 指向本站的 `/m/`、`/t/`、`/f/`（媒體、縮圖、檔案預覽）時開新分頁（`noopener,noreferrer`），其他同源網址照舊在這個分頁開；`copyText` 不提示，改交給網頁一則 `copyFallback`（第一節）。
    - `layout` 不送伺服器：傳輸層存在 `localStorage['myriagent.app.layout.v1']`，`init.layout` 是 null 時換成存下的；彈出視窗不存。`openLink`、`copyText`、`popout` 由傳輸層在瀏覽器裡處理，不送伺服器。
    - 送出前自檢第 11 點的上限（以整個 SignalR 呼叫框＋0x1E 的 UTF-8 位元組計，128 KB；字串值 16,000 字、屬性名 4096 字，不分指令）；超過就不送。不轉送的指令字串在 4097～16,000 字之間時自檢放行、由伺服器回 `invalid`（伺服器是唯一的權威）。

## 七、換機〔換機〕（第 5 階段；WP5.1 只立形狀）

標示 **〔換機〕** 的欄位、訊息與指令是第 5 階段「換機接手與離線續接」的網頁那一側（計畫 `docs/plans/2026-10-23-phase5-handoff.md` 第 5.8、10 節；裝置↔伺服器那一段在 `docs/sync-protocol.md` 第 16 節）。**`hand1` 啟用（WP5.10）之前都不會出現**：WP5.1 只寫下形狀，宿主（桌面與伺服器）不送這些欄位與訊息，網頁也還沒有元件（WP5.9 加）。網頁的規則是：標頭沒有 `handoff`（舊宿主、開關關著）、或 `handoff.can` 是 false 時不顯示任何換電腦的東西。第二節的指令表此時**不加**新指令（WP5.5 起加 `takeover`、`handoffCancel`、`handoffPreview`）。

- `Header` 加 `handoff`、`forkedFrom`；`ConversationSummary` 加 `handoffState`（同 `handoff.state`）、`forkedFrom`（只有 id）：

```jsonc
"handoff": { "can": true, "why": null,          // why：provider | policy | unavailable；can=false 時網頁不顯示換電腦的動作
  "state": null,                               // requested | accepted | draining | ready | importing | needsFolder | importFailed | fenced
  "handoffId": null, "kind": null, "fromDeviceId": null, "toDeviceId": null, "mode": null, "deadlineAt": null,
  "error": null, "detail": null, "candidates": null,          // needsFolder 的候選資料夾
  "transcript": null },                        // B 上：missing | held | unportable | damaged（以前文節錄續接）
"forkedFrom": { "conversationId": "…", "title": "…", "deviceName": "OFFICE-PC" }
```

- `why: provider`＝Grok、Antigravity 的對話（擁有者決定：只有 Claude、Codex 的對話能換電腦）。`transcript` 是 `held` 時的說明：「這個對話的 CLI 紀錄含這台的 Claude／Codex 登入憑證，沒有上傳」（一般像權杖的字串照樣上傳到伺服器，只有這台的登入憑證會擋，計畫 D11）。
- 新的宿主→網頁訊息：`handoff`（`conversationId`＋上面 `handoff` 的欄位，部分更新）、`handoffPreview`（`conversationId`＋`docs/sync-protocol.md` 16.5 的 `HandoffPreview` 結果）。
- 新指令：`takeover {conversationId, toDeviceId, mode, force, autoContinue}`、`handoffCancel {conversationId, handoffId}`、`handoffPreview {conversationId}`（瀏覽器由伺服器處理；桌面由中樞轉呼叫 `HandoffPreview`／`HandoffStart`／`HandoffCancel`）；桌面本機專用：`handoffRetry {conversationId, accountId?}`、`syncPathMaps`、`syncPathMapRemove {repoKey}`、`syncProjectRoots {roots}`（遠端的 view 一律拒絕）。它們不進 `RemoteCommandCatalog`（不是送到電腦執行的信封指令）。`commandResult` 的錯誤代碼是 `docs/sync-protocol.md` 16.8 的交接代碼（`busy`、`owner_online`、`owner_offline`、`target_offline`、`target_outdated`、`no_account`、`policy`、`unsupported_provider`、`not_full`、`not_found`、`deleted`、`same_device`、`log_mismatch`、`events_missing`、`log_damaged`、`expired`、`cancelled`、`server_restart`、`not_available`），網頁的文字對照在 `Web/handoffrules.js`（WP5.9）。
- 交接後同一個 id 的分頁要重新載入：宿主送 `conversationClosed {conversationId, replacedBy: 同一個 id, reason: "handoff"}`，網頁照 `replacedBy` 重開（WP5.9 確認現行程式對同 id 的處理）。
- `Item` 的 `noticeKind` 多兩個值：`handoff`（「已從 OFFICE-PC 交接到 HOME-PC」，WP5.7）、`fork`（「離線期間在 OFFICE-PC 的 N 輪另存成這個分支」，WP5.8）。不認得的值照一般提示顯示。
