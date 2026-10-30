# 背景活動日誌

2026-10-27 使用者要求：app 常常要更新重開，重開時常被背景程式卡住、背景工作被強迫中止、背景工作無限跑讓代理一直等。問題的狀況很多，所以先把每個對話開的子代理、背景工作、背景程式，以及換版排空的經過都記下來，之後除錯與修正都從這份日誌看。

目標（之後依日誌修正）：每次換版都能順利重開、不破壞大部分的背景工作；盡量等背景工作跑完停下再換版，不要讓背景工作跑到一半被強迫中止、重開後又要重跑。

日誌只記錄，不改任何行為。

## 位置與格式

- `<資料根目錄>\logs\background\yyyy-MM-dd.jsonl`（`AppPaths.BackgroundJournalDirectory`；一般使用者是 `%LOCALAPPDATA%\Myriagent\logs\background\`）。
- 一行一筆 JSON（UTF-8、不帶 BOM）。檔案依寫入那一刻的日期分檔。
- 單檔超過 20 MB 改名成 `yyyy-MM-dd.1.jsonl`（取代上一份）再寫新檔。
- 保留 30 天（一般記錄 `logs\*.log` 只留 7 天）。
- 寫入端是 `Core/BackgroundJournalFile.cs`：
  - 背景執行緒寫，不擋 UI 執行緒。
  - 程序結束時（`ProcessExit`、`AppController` 收尾）同步寫出佇列裡還沒落盤的。換版前最後幾筆最重要。
- 掛鉤都在 `Hub/AgentHub.Journal.cs`：
  - 中樞預設沒接，只有 `AppController` 以 `AttachBackgroundJournal` 接上。
  - 測試接 `tests/.../Hub/FakeJournal.cs`。
- 純函式規則在 `Hub/BackgroundJournalRules.cs`，包括形狀、文字截短與遮蔽、清單差異、檔名與保留。

## 每一筆的共同欄位

依序如下（值是 null 的欄位不寫）：

| 欄位 | 說明 |
|---|---|
| `t` | 本機時間＋時差，毫秒，例如 `2026-10-27T09:08:07.654+08:00` |
| `ev` | 事件名稱，見下表 |
| `conv`、`convTitle`、`prov`、`acct` | 對話的事件才有：對話 id、標題（草稿沒有）、代理種類、帳戶 id。事件自己的欄位和這些同名時改寫成 `data.名稱`，不會蓋掉 |
| 事件自己的欄位 | 見下表 |
| `appPid`、`ver` | app 的程序 id 與版本。換版前後是不同的程序，以它分出是哪一次執行 |

文字欄位的處理：

- 標題、說明、命令列、錯誤一律先遮掉疑似憑證（`CredentialLeakRules.Mask`）。
- 換行換成「⏎」。
- 太長截短加「…」：說明 300 字、命令列與提示詞 600 字。

秒數欄位（`sec`、`waitedSec`…）取小數一位。

## 事件

### 背景工作（各家 CLI 回報的）

| ev | 何時 | 主要欄位 |
|---|---|---|
| `bg.start` | 背景工作的清單多了一個 | `tool`、`kind`、`title`、`detail`（命令列）、`taskId`（Claude）、`stopId`、`count`（目前總數）、`running`（這一輪還在跑時開始的） |
| `bg.end` | 清單少了一個 | 同上，另有 `sec`（跑了多久）。`how: "sessionReplaced"`＝換了會話，舊程序的背景工作跟著沒了；`how: "accountSwitch"`＝換帳戶關掉了程序（當下記，另帶 `to`：換到的帳戶）；這時帶 `tools`、`count` |
| `bg.count` | 清單沒變、只有數量變了（對不到工具卡的背景工作） | `count`、`before`、`listed` |
| `bg.finished` | Codex 或 Grok 的背景指令跑完（中樞會接續） | `toolId`、`title`、`status`、`exitCode`、`sec`、`stoppedByUser` |
| `bg.continue` | 背景指令跑完，中樞隱藏送出接續訊息 | `continues`（第幾次）、`finished`、`stillRunning` |
| `bg.notContinued` | 背景指令跑完，但不接續 | `why`：`queuePaused` 或 `limit`（自動接續上限 10 次） |
| `bg.stop` | 使用者按了停止背景工作 | `all`、`tool`、`count`、`stopped`、`error` |
| `bg.check` | CLI 換版停掉了等背景工作的對話的背景工作，之後請代理確認 | `phase`：`queued`／`sent`／`notChecked`（高風險操作不自動確認）、`risk`、`bg` |
| `acct.defer` | 使用者選了要換的帳戶、還有背景工作在跑（2026-10-27：換帳戶會關掉程序），等背景工作結束才換 | `phase`：`waiting`（開始等，帶 `bgCount`、`bg`）／`applied`（背景工作都結束、換過去了）／`cancelled`（選回目前的帳戶）／`replaced`（執行中又選了帳戶）／`switched`（換成別的帳戶）／`unusable`（選的帳戶登出、移除）／`same`；`to`：選的帳戶 |

### Claude 的工作

| ev | 何時 | 主要欄位 |
|---|---|---|
| `task.start` | CLI 的 `task_started`。前景、背景的指令與子代理都會發 | `taskId`、`type`（`local_bash`、`local_agent`、`monitor`…）、`desc`、`toolId`、`background` |
| `task.end` | `task_notification` | `taskId`、`type`、`status`（`completed`／`stopped`／`failed`／`killed`）、`summary`、`background` |

重開時被丟下的背景工作，新程序 `--resume` 後回報成 `status: stopped`，摘要是「Orphaned by a previous Claude Code process exit…」。但 CLI 會把逐字稿裡沒看到結束通知的背景工作整批這樣回報，其中大多早就結束了（2026-10-27 的 27 筆全是，多半是滿 30 分鐘被停掉的監看）：同一個 `conv` 的 `taskId` 之前已經有 `task.end` 的不是被丟下，日誌裡有 `task.start` 卻沒有結束的才是被強迫中止的證據（`bg_journal.py` 照這樣分開列）。

### 子代理（任何代理種類）

| ev | 何時 | 主要欄位 |
|---|---|---|
| `sub.start` | 子代理的工具卡開始 | `toolId`、`tool`、`title`、`prompt`、`open`（同時開著的子代理數） |
| `sub.end` | 子代理的工具卡結束 | `toolId`、`title`、`status`、`sec`、`result` |
| `sub.usage` | Claude 子代理結束時回報用量 | `toolId`、`tokens`、`toolUses`、`sec` |

上表的工具卡指 ToolKind 是 `agent` 的卡：Claude 的 Agent／Task、Codex 的子代理與 collabAgentToolCall、Antigravity 的 invoke_subagent。

`sub.end` 的 `status` 是 `processExited` 時，表示程序結束時子代理還開著，跟著結束了。

### 一輪與程序

| ev | 何時 | 主要欄位 |
|---|---|---|
| `turn.start` | 一輪開始 | `byCli`（CLI 自己開的，例如背景工作完成的通知）、`bgCount` |
| `turn.end` | 一輪結束 | `outcome`、`sec`、`bgCount`／`bg`（結束時還在跑的背景工作：代理接下來就在等它們）、`programs`、`subagents` |
| `turn.pauseForQuestion` | 代理在執行中送出非同步提問（Codex），中樞停下這一輪等使用者回答（結束的那筆 `turn.end` 是 interrupted） | `question`（第一題） |
| `turn.cliStart` | 閒置時 CLI 自己開了一輪 | `afterBackground`、`threadGoal`、`alreadyRunning` |
| `proc.start` | CLI 程序啟動 | `instance`、`cli`（版本） |
| `proc.exit` | CLI 程序結束 | `instance`、`exitCode`、`expected`、`running`、`bgCount`／`bg`（結束時還活著的背景工作）、`subagents`、`stderr`（不是預期的結束才帶） |
| `proc.close` | 中樞自己關掉 CLI 程序（2026-10-27；這時會話不發結束事件，不會有 `proc.exit`） | `why`：`idle`（閒置回收）／`accountSwitch`（另帶 `to`）／`delete`／`clear`／`rewind`／`accountRemoved`；`instance`、`running`、`idleSec`、`bgCount`／`bg`、`subagents`（還開著的子代理另記 `sub.end`，`status: "processClosed"`）、`remoteControl`。換版重開記在 `restart.*`、app 結束記在 `app.exit` |

### 背景程式（對話另開、脫離 CLI 的程式）

| ev | 何時 | 主要欄位 |
|---|---|---|
| `prog.found` | 掃描到的程式被分類 | `pid`、`name`、`cmd`、`parentPid`、`startedAt`，以及 `class` |
| `prog.exit` | 程式結束 | 同上，另有 `exitCode`、`sec`、`stoppedByUser`。分類過的一律記；沒分類的跑了 30 秒以上才記 |
| `prog.stop` | 停止一個程式 | `by`：`user`（使用者按停止）／`delete`（刪除對話時一併停止） |

`class` 的值：

- `confirmed`：撐過一輪、列在畫面上。
- `cliTask`：CLI 背景工作的程序。
- `quiet`：閒置中開的，結束時不提示。

### 換版（CLI 熱更新、app 自我更新）

| ev | 何時 | 主要欄位 |
|---|---|---|
| `update.state` | app 更新的階段或阻擋原因變了 | `stage`、`blocker`、`staged`、`userRequested` |
| `update.drain` | 開始 app 更新的排空 | `conversations`：每個有程序的對話在做什麼（`running`、`waitingUser`、`remoteControl`、`bg`、`programs`） |
| `barrier.begin`／`barrier.ready`／`barrier.end` | 每種代理的更新屏障開始、全部停下、結束 | `provider`、`appUpdate`、`closedIdle`、`held`、`outcome`、`sec` |
| `restart.wait` | 某個對話的換版改了在等什麼 | `state`、`subject`、`waitedSec`、`running`、`bg` |
| `restart.expired` | 等背景工作滿 30 分鐘，照立即套用停下 | `waitedSec`、`bg`、`programs` |
| `restart.call` | 停下或重開一個對話的程序 | `stopOnly`、`reason`、`mode`、`autoContinue`、`noContinue`、`interrupted`（被打斷的工具）、`pending`（沒回應的請求）、`hadBackground`、`bg`（被停掉的背景工作）、`subagents`、`risk` |
| `restart.result` | 停下或重開的結果 | `outcome`、`error` |
| `restart.snapshot` | 排空中每 2 分鐘記一次 | `barriers`；`conversations` 列出還在等的對話：`state`、`subject`、`waitedSec`、`bg` |
| `update.abort` | 放棄這次 app 更新 | `reason` |
| `update.handoff` | 交棒給新版之前 | `conversations`：每個停下的對話的 `mode`、`whyNot`、`backgroundStopped` 等，也就是 resume.json 的摘要；另有 `drainSec` |

`restart.wait` 的 `state` 有 `WaitingTool`、`WaitingBackground`、`WaitingUser`…

### app 與接續

| ev | 何時 | 主要欄位 |
|---|---|---|
| `app.start` | app 啟動 | `release`、`build`、`afterUpdate` |
| `app.exit` | app 結束（含換版） | `conversations`：還在執行或在等背景工作、有背景程式或子代理的對話。結束會停掉的都在這裡 |
| `app.exitQuery` | Windows 問能不能登出關機時的同一份快照 | 同 `app.exit` |
| `resume.app` | app 更新後接續 | `result`：`continued`／`notContinued`；另有 `reason`、`detail`、`backgroundStopped` |
| `resume.turn` | 上次結束時還在跑的一輪接續 | `result`、`waitingBackground`、`closedByApp`、`autoResumes`、`bg` |

### 平常的快照

| ev | 何時 | 主要欄位 |
|---|---|---|
| `bg.snapshot` | 有背景工作、背景程式或子代理時，每 10 分鐘記一次 | `conversations`：`bgSec`（從開始有背景工作到現在）、`bg`、`programs`（每個程式帶 `sec`）、`subagents` |

## 常見的查法

`tools/probes/bg_journal.py` 會整理成報告：

- 每次執行的摘要。
- 每次換版排空等了誰、多久、停掉了什麼。
- 結束時還在跑的背景工作。
- 跑超過門檻的背景工作。

```bash
python -X utf8 tools/probes/bg_journal.py                       # 預設讀 %LOCALAPPDATA%\Myriagent\logs\background 最近 3 天
python -X utf8 tools/probes/bg_journal.py --days 7 --long 60    # 最近 7 天、列出跑超過 60 分鐘的背景工作
python -X utf8 tools/probes/bg_journal.py --dir <別的資料根目錄>\logs\background
```

各種問題該看哪些事件：

- **換版為什麼一直沒開始**：看 `update.state` 的 `blocker` 怎麼變。
- **排空卡在哪個對話**：看 `restart.wait`、`restart.snapshot`（`subject` 是在等的工具或卡片）。
- **哪些背景工作被強迫中止**：
  - 看 `restart.expired`，以及之後同一個 `conv` 的 `restart.call`，它的 `bg` 就是被停掉的。
  - 看 `app.exit` 列的 `bg`、`programs`。
  - 新版那邊的 `task.end` 會是 `status: stopped` 或 `killed`＋「Orphaned…」（之前已經結束過的是 CLI 的再回報，不算）。
- **無限跑的背景工作**：
  - 看 `bg.snapshot` 的 `bgSec`、`programs[].sec`。
  - 看 `bg.start` 之後一直沒有對應的 `bg.end`。Claude 以 `taskId`、Codex 以 `stopId` 對。
- **重開後有沒有接續、接續了什麼**：看 `resume.app`、`resume.turn`，以及接著的 `turn.start`（`byCli`）。

## 不記的

- 給人看的即時畫面更新、工具卡的輸出內容。
- 沒分類、跑不到 30 秒的程序：多半是 Git Bash 前景指令中途的原生程式。
- 其他電腦的對話（副本）。
