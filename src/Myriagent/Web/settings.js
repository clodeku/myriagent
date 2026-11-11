// 設定對話框：左側是分類（直排的分頁，窄視窗換成上方的橫排）與搜尋，右側是選取的那一頁（頁首的標題與說明、各區塊的卡片）。
// 分頁「一般」（外觀、系統、通知）、「對話」（輸入、額度用完時、Remote Control、附加給代理的說明）、
// 「模型設定」（每種代理一個子分頁：新對話的預設、各模型的推理強度）、「CLI」（已安裝的 CLI、〔熱更新〕更新、共用與權杖）、
// 〔同步〕「多電腦同步」（WP3.4：配對、狀態、解除、伺服器身分確認、其他電腦的遠端操作、這台改名、已配對的電腦）、
// 「版本與更新」（Clode Myriagent〔app 自我更新〕）。搜尋從畫面上的設定列找（settingsrules.js 比對與排序），點了切到那一頁、捲過去並標一下。
// 每項改了就送 setting（模型設定送 setConversationDefault，等宿主重送設定才更新畫面）。
// 〔同步〕瀏覽器模式（WP3.6 閘道）只建四個漫遊鍵（ROAMING_KEYS）的列，沒有列的區塊與分頁都不建（剩「一般」與「對話」；app 自我更新是這台的，也不建）；
// 例外是瀏覽器自己的語音通知開關與語速、音調（data-setting="voiceLocal"，存在這個瀏覽器、不送 setting，voice.js），
// 以及「繼續」鈕的指令（桌面是 continueText；瀏覽器版是 data-setting="continueLocal"，存在這個瀏覽器，composer.js）。
// 代理、伺服器與其他電腦給的文字一律以 textContent 放入（h() 的 text 與字串子節點），這個檔案不把任何字串當成 HTML 解析。
import { h, reconcile, copyToClipboard, fmtRelative, fmtElapsed, replayClass, setHidden } from './util.js';
import { icon, providerMark } from './icons.js';
import { post, state, onHostMessage, EFFORT_LABEL, EFFORT_DESC, permissionTone } from './store.js';
import { openDialog, openMenu, segmented, switchControl, confirmDialog, spinner } from './ui.js';
import { confirmFullAccess, toneColor, continueLocal, setContinueLocal } from './composer.js';
import { CONTINUE_TEXT, CONTINUE_MAX, continueTextOf } from './sendrules.js';
import { renderCli, cliCheckButton } from './dashboard.js';
import { applySettings } from './theme.js';
import { RESTART_POLICIES, policyOf } from './hotupdate.js';
import { appSection } from './appupdate.js';
import { backfillText } from './liverules.js';
import { openAuditLog, openTrash } from './synclog.js';
import { voiceSupported, voiceOn, setVoiceOn, voiceTuning, setVoiceTuning } from './voice.js';
import { SPEED_MIN, SPEED_MAX, SPEED_STEP, PITCH_MIN, PITCH_MAX, normSpeed, normPitch, fmtSpeed, fmtPitch } from './voicerules.js';
import { pairCountdown, countdownCells, deviceLed, rangeFill, rangeTicks } from './fxrules.js';
import { setDecodedText } from './textfx.js';
import { SETTING_KEYWORDS, searchSettings, searchTerms, highlightParts, snippet, tabKeyTarget, tabHitCounts, normalizeHubUrl, parseTokens, fmtTokens, parsePercent, fmtPercent, modelText, effortText } from './settingsrules.js';
import { collaboratorsSection, myAccountSection, canManageCollaborators, isCollaborator } from './collab.js';

let open = null; // { d, refresh, browser, refreshApp }
let lastTab = 'general';  // 再開設定時回到上次看的分頁（只記在這次執行）
let lastProvider = null;  // 「模型設定」上次看的代理子分頁（同上）
let seq = 0;              // 分頁與面板的 id（關閉中的舊對話框淡出時還在畫面上，id 不能撞）

/**
 * 分頁與各分頁裡的區塊（區塊 id 是 openSettings({ section }) 的目標；只有一個區塊的分頁不重複顯示區塊標題）。
 * desc 是頁首的說明（browserDesc：瀏覽器模式只剩部分區塊時的說法）
 */
const TABS = [
  { id: 'general', label: '一般', icon: 'gear', desc: '外觀、開機與系統匣、通知。', browserDesc: '外觀與通知。', secs: ['look', 'system', 'notify'] },
  { id: 'chat', label: '對話', icon: 'chat', desc: '輸入方式、額度用完時怎麼接手、Remote Control，以及每個新對話附加給代理的說明。', browserDesc: '輸入方式與「繼續」鈕。', secs: ['chat', 'quota', 'remote', 'instr'] },
  { id: 'defaults', label: '模型設定', icon: 'sparkle', desc: '新對話開始時的模型、權限模式與推理強度，每種代理各自設定；在對話裡改只影響那個對話。', secs: ['defaults'] },
  { id: 'cli', label: 'CLI', icon: 'terminal', desc: '這台電腦的 Claude Code、Codex、Antigravity 與 Grok Build：版本、更新方式與共用的設定。', secs: ['cli', 'cliUpdate', 'cliShare'] },
  { id: 'sync', label: '多電腦同步', icon: 'link', desc: '透過你自己架設的 MyriagentHub 在多台電腦與網頁之間同步帳戶、用量與對話；登入憑證不會離開這台電腦。', secs: ['sync'] },
  { id: 'app', label: '版本與更新', icon: 'download', desc: 'Clode Myriagent 的版本與自動更新。', secs: ['app'] },
  // 多人協作（collab.js）：擁有者管理協作人（桌面要已配對）；協作人自己的帳號（改密碼、登出）
  { id: 'collab', label: '協作人', icon: 'user', desc: '建立協作人的帳號密碼，在對話的「⋯」選單把對話分享給他們；協作人從網頁版登入，只看得到分享給他的對話。', secs: ['collab'] },
  { id: 'account', label: '我的帳號', icon: 'shield', desc: '你的協作人帳號：改密碼、登出。', secs: ['account'] },
];

/** 設定列；setting＝這一列改的 Settings 鍵（data-setting，瀏覽器模式以它篩選，探針也看它）；kw＝不是 Settings 鍵的列在搜尋時查的同義詞鍵 */
const row = (label, desc, control, setting, kw) => h('div', { class: 'set-row', dataset: { setting, kw } }, h('div', { class: 'set-text' }, h('div', { class: 'set-label', text: label }), desc ? h('div', { class: 'set-desc', text: desc }) : null), h('div', { class: 'set-ctl' }, control));
/** 區塊：標題（可省略）＋卡片；bare＝內容自己有框（CLI 卡、子分頁、同步區），不再包一層卡片 */
const sec = (id, title, bare, kids) => h('section', { class: 'set-sec', dataset: { sec: id } },
  title ? h('h4', { class: 'set-h', text: title }) : null,
  ...(bare ? kids : [h('div', { class: 'set-card' }, ...kids)]));
/** 卡片：一組設定列（同步區、模型設定的子分頁裡用） */
const card = (...kids) => h('div', { class: 'set-card' }, ...kids);

/** 〔同步〕瀏覽器模式（WP3.6 閘道）只有這四個漫遊鍵（WPR Settings；其他的伺服器回 desktop_only） */
export const ROAMING_KEYS = Object.freeze(['theme', 'fontSize', 'enterToSend', 'notifications']);

/** 這份網頁跑在瀏覽器（伺服器的 /app/）：init 的 remote.mode 是 browser */
export function isBrowserMode() {
  return syncPhase(currentRemote()) === 'browser';
}

export function openSettings(opts = {}) {
  if (open) { open.d.close(); open = null; }
  const s = state.settings;
  // 瀏覽器模式只建四個漫遊鍵的列：伺服器只給這四個（其他鍵 state.settings 裡是預設值，按了只會收到 desktop_only），
  // 沒有可見列的區塊（模型設定、CLI、附加說明、Remote Control、多電腦同步、系統匣）與沒有區塊的分頁都不建。桌面全部照舊
  const browser = isBrowserMode();
  const has = (key) => !browser || ROAMING_KEYS.includes(key);
  const set = (key, value) => {
    state.settings[key] = value;
    applySettings(state.settings);
    post({ cmd: 'setting', key, value });
  };

  const theme = segmented([
    { value: 'dark', label: '深色' }, { value: 'light', label: '淺色' }, { value: 'system', label: '跟隨系統' },
  ], s.theme || 'dark', (v) => set('theme', v));

  const sizeVal = h('span', { class: 'range-val' });
  const size = h('input', { type: 'range', class: 'range', min: '12', max: '18', step: '1', value: String(s.fontSize || 14), 'aria-label': '對話字級' });
  const syncSize = () => { sizeVal.textContent = size.value + ' px'; paintRange(size); };
  size.addEventListener('input', () => { syncSize(); state.settings.fontSize = Number(size.value); applySettings(state.settings); });
  size.addEventListener('change', () => set('fontSize', Number(size.value)));
  syncSize();

  const sw = (key, label) => switchControl({ checked: s[key] !== false, label, onChange: (v) => set(key, v) });
  // 動態效果：桌面專屬（瀏覽器模式是 null，不建）
  const fxSw = has('visualEffects') ? sw('visualEffects', '動態效果') : null;
  const enterSw = switchControl({ checked: s.enterToSend !== false, label: 'Enter 送出', onChange: (v) => set('enterToSend', v) });
  const notifySw = sw('notifications', '系統通知');
  // 排程失敗時自動請代理診斷（桌面、掛了排程讀取器才有；宿主送 null 時不建）
  const schedDiagSw = s.scheduleAutoDiagnose != null ? sw('scheduleAutoDiagnose', '排程失敗時自動請代理診斷') : null;
  // 以下是桌面專屬（瀏覽器模式是 null，不建）
  const traySw = has('closeToTray') ? sw('closeToTray', '關閉視窗時縮到系統匣') : null;
  // 開機時自動啟動：狀態在登錄；null＝這次執行不能設定（不是安裝版、或用 --data 執行），開關停用並說明
  const bootSw = has('autoStart') ? switchControl({ checked: s.autoStart === true, disabled: s.autoStart == null, label: '開機時自動啟動', onChange: (v) => set('autoStart', v) }) : null;
  const bootRow = bootSw && row('開機時自動啟動', autoStartDesc(s), bootSw, 'autoStart');
  // 語音通知與它底下的語速、音調（桌面：宿主的設定；瀏覽器：存在這個瀏覽器〔voice.js〕，不經伺服器、不跟其他裝置同步）
  const deskTune = has('voiceNotifications')
    ? voiceTune(() => ({ speed: normSpeed(state.settings.voiceSpeed), pitch: normPitch(state.settings.voicePitch) }),
      (key, v) => (key === 'speed' ? set('voiceSpeed', normSpeed(v)) : set('voicePitch', normPitch(v))))
    : null;
  const voiceSw = deskTune ? switchControl({ checked: s.voiceNotifications !== false, label: '語音通知', onChange: (v) => { set('voiceNotifications', v); deskTune.refresh(v); } }) : null;
  const localTune = browser && voiceSupported() ? voiceTune(voiceTuning, setVoiceTuning) : null;
  const localVoiceSw = localTune ? switchControl({ checked: voiceOn(), label: '語音通知', onChange: (v) => { setVoiceOn(v); localTune.refresh(v); } }) : null;
  if (deskTune) deskTune.refresh(s.voiceNotifications !== false);
  if (localTune) localTune.refresh(voiceOn());
  const autoSw = has('autoUpdateCli') ? sw('autoUpdateCli', '自動更新 CLI') : null;
  const remoteSw = has('claudeRemoteControl') ? sw('claudeRemoteControl', 'Claude 對話開啟 Remote Control') : null;
  const tokenSw = has('claudeIdleTokenRefresh') ? sw('claudeIdleTokenRefresh', '閒置的 Claude 帳戶自動換發權杖') : null;
  const resumeSw = has('autoResumeAfterQuota') ? sw('autoResumeAfterQuota', '沒有帳戶可換時等重置後自動接著做') : null;
  const idleWarnSw = has('claudeIdleResumeWarning') ? sw('claudeIdleResumeWarning', '閒置很久的 Claude 對話送出前提醒') : null;
  const crossSw = has('autoSwitchCrossProvider')
    ? switchControl({ checked: s.autoSwitchCrossProvider === true, disabled: s.autoSwitchAccount === false, label: '同種代理都沒額度時跨代理備援', onChange: (v) => set('autoSwitchCrossProvider', v) })
    : null;
  const switchSw = has('autoSwitchAccount')
    ? switchControl({ checked: s.autoSwitchAccount !== false, label: '額度用完時自動換帳戶繼續', onChange: (v) => { set('autoSwitchAccount', v); if (crossSw) crossSw.input.disabled = !v; } })
    : null;
  const shareCliSw = has('shareCrossCliCustomizations')
    ? switchControl({ checked: s.shareCrossCliCustomizations === true, label: '跨四家 CLI 共用技能與全域指示', onChange: (v) => set('shareCrossCliCustomizations', v) })
    : null;
  const policy = has('cliRestartPolicy') ? policyPicker(policyOf(s.cliRestartPolicy).value, (v) => set('cliRestartPolicy', v)) : null;

  let instr = null;
  let lastInstr = '';
  if (has('instructions')) {
    instr = h('textarea', { class: 'input instr', rows: 6, spellcheck: 'false', placeholder: '例如：回覆一律用臺灣繁體中文。完成後列出改了哪些檔案。' });
    instr.value = s.instructions || '';
    lastInstr = instr.value;
    instr.addEventListener('blur', () => {
      if (instr.value === lastInstr) return;
      lastInstr = instr.value;
      state.settings.instructions = instr.value;
      post({ cmd: 'setting', key: 'instructions', value: instr.value });
    });
  }

  // 「繼續」鈕的指令：桌面送 setting continueText（宿主存），瀏覽器版存在這個瀏覽器（伺服器的偏好只收四個漫遊鍵）；
  // 離開輸入框或關掉設定時儲存，清空就回到預設
  const cont = continueEditor(browser);

  const defaults = has('conversationDefaults') ? conversationDefaults() : null;
  // CLI 版本與「立即檢查」是這台的 CLI；同步狀態只有桌面宿主有（瀏覽器模式沒有 SyncStatus）
  const cliGrid = browser ? null : h('div', { class: 'cli-grid compact' });
  const checkBtn = browser ? null : cliCheckButton();
  const sync = browser ? null : syncSection();
  // 多人協作：擁有者（有協作人狀態時：瀏覽器版、已配對的桌面）管理協作人；協作人看自己的帳號
  const collab = !isCollaborator() && (canManageCollaborators() || !browser) ? collaboratorsSection() : null;
  const account = isCollaborator() ? myAccountSection() : null;
  // app 自我更新（版本、自動更新開關、狀態與按鈕）只有桌面宿主有（瀏覽器模式沒有 AppUpdateView）
  const appSec = has('autoUpdateApp') ? appSection(set, row) : null;

  const secs = {};
  /** 只有至少一個子節點（null 是不建的列）時才建區塊 */
  const put = (id, title, ...kids) => {
    const shown = kids.filter(Boolean);
    if (shown.length) secs[id] = sec(id, title, false, shown);
  };
  /** 同上，內容自己有框（不包卡片） */
  const putBare = (id, title, ...kids) => {
    const shown = kids.filter(Boolean);
    if (shown.length) secs[id] = sec(id, title, true, shown);
  };
  put('look', '外觀',
    row('主題', '跟隨系統會依 Windows 的深淺色設定切換。', theme, 'theme'),
    row('對話字級', '訊息與輸入框的文字大小。', h('div', { class: 'range-wrap' }, size, sizeVal), 'fontSize'),
    fxSw && row('動態效果', '執行中的掃描光、圓環轉動、電腦狀態面板的雷達等持續的動畫。關掉可以減輕處理器與顯示卡的負擔；Windows 設定了減少動畫效果時一律不播。', fxSw, 'visualEffects'));
  put('system', '系統',
    bootRow,
    traySw && row('關閉視窗時縮到系統匣', '對話會繼續在背景執行，從系統匣圖示叫回視窗。', traySw, 'closeToTray'));
  put('notify', '通知',
    row('系統通知', '代理提問、要求核准或完成工作時通知你。', notifySw, 'notifications'),
    voiceSw && withTune(row('語音通知', '對話完成、失敗，或代理提問、要求核准時，用 Windows 內建的雅婷語音念一句說明（例如：「整理報表」已完成：月報已匯出成 Excel），同時讓那個對話的分頁與標頭閃幾下。視窗開著也會念；打開時先念一句試聽，調了語速或音調也會念一句。', voiceSw, 'voiceNotifications'), deskTune),
    localVoiceSw && withTune(row('語音通知', '對話完成、失敗，或代理提問、要求核准時，用這台裝置的中文語音念一句說明，同時讓那個對話閃幾下。開關與語速、音調只存在這個瀏覽器；手機要開著這個頁面才念得出來，第一次要先點一下畫面。', localVoiceSw, 'voiceLocal'), localTune),
    schedDiagSw && row('排程失敗時自動請代理診斷', '排程頁裡代理建立或納入管理的排程開始失敗時，自動在它的管理對話請代理找原因（只診斷、不修改，最後列出建議的修法等你決定）。同一個排程 24 小時內最多一次、每天最多 10 次，帳戶的每週額度剩不到 10% 時只通知不診斷。失敗時的語音與系統通知照上面的開關。', schedDiagSw, 'scheduleAutoDiagnose'));
  put('app', null, appSec && appSec.node);
  put('chat', '輸入',
    row('Enter 送出', '關閉時改用 Ctrl+Enter 送出，Enter 換行。手機與平板的 Enter 一律換行，用送出鈕送出。', enterSw, 'enterToSend'),
    idleWarnSw && row('閒置很久的 Claude 對話送出前提醒', 'Claude 的提示快取保留 1 小時：閒置超過 1 小時的對話，下一則會把整份上下文重算一次。開著時，Claude 估計這一則會用掉 5 小時額度的 5% 以上（Claude 給的門檻）才在送出前先問一聲。', idleWarnSw, 'claudeIdleResumeWarning'),
    cont.node);
  put('quota', '額度用完時',
    switchSw && row('自動換帳戶繼續', '對話執行到一半碰到用量上限（例如 Claude 的 5 小時額度）時，自動改用同一種代理、能用同一個模型、還有額度的其他帳戶接著做；有好幾個時選最快重置的（先用掉快要重置的額度）。新帳戶會收到先前對話的節錄，從中斷處繼續，但看不到先前的工具結果。原本的帳戶額度恢復後，對話閒置時或下一則訊息送出之前會自動換回原帳戶（你自己換過帳戶的對話不換回）。支援 Claude Code、Codex、Antigravity 與 Grok Build；關掉時照舊停下，等你手動換帳戶或等額度重置。', switchSw, 'autoSwitchAccount'),
    resumeSw && row('沒有帳戶可換時等重置後自動接著做', '額度用完、又沒有其他帳戶可以接手時，等這個帳戶的額度重置（12 小時內的）後自動送出接續訊息，從中斷處接著做；等待期間有其他帳戶恢復就換過去。輸入框上方可以取消，你自己送出訊息也會取消等待。', resumeSw, 'autoResumeAfterQuota'),
    crossSw && row('同種代理都沒額度時跨代理備援', '開啟「自動換帳戶繼續」時，若同一種代理沒有其他可用帳戶（例如只有一個 Antigravity 帳戶，或同種帳戶額度都滿了），自動改用其他已登入訂閱且還有額度的代理種類（Claude Code、Codex、Antigravity、Grok Build）開新對話並帶著前文節錄接手。預設關閉。', crossSw, 'autoSwitchCrossProvider'));
  put('remote', 'Remote Control',
    remoteSw && row('Claude 對話開啟 Remote Control', '執行中的 Claude 對話可從 claude.ai/code 或 Claude App 接手，以對話標題命名；在那邊送的訊息也會顯示在這裡。開著的對話閒置 2 小時後才關閉程序（每個約占 250～500 MB 記憶體），之後在這台送出時重新開啟。', remoteSw, 'claudeRemoteControl'));
  put('instr', '附加給代理的說明',
    instr && h('div', { class: 'set-row set-row-block', dataset: { setting: 'instructions' } },
      h('div', { class: 'set-text' }, h('div', { class: 'set-desc', text: '每個新對話都會把這段說明交給代理。{cwd} 會換成工作資料夾，{attachments} 會換成附件資料夾，{backgroundTool} 會換成各代理在背景執行長時間指令的方式。離開輸入框時儲存。' })),
      instr));
  putBare('defaults', null, defaults && defaults.node);
  putBare('cli', '已安裝的 CLI',
    cliGrid,
    checkBtn && h('div', { class: 'set-actions' }, checkBtn));
  put('cliUpdate', '更新',
    autoSw && row('自動更新 CLI', '偵測到新版、而且沒有對話在執行時自動更新。', autoSw, 'autoUpdateCli'),
    policy && h('div', { class: 'set-row set-row-block', dataset: { setting: 'cliRestartPolicy' } },
      h('div', { class: 'set-text' }, h('div', { class: 'set-label', text: 'CLI 更新後' }),
        h('div', { class: 'set-desc', text: '對話執行中 CLI 被更新（app 更新、你在終端機更新、背景自動更新）時，什麼時候換成新版。閒置的對話一律在下一則訊息用新版。' })),
      policy));
  put('cliShare', '共用與權杖',
    shareCliSw && row('跨四家 CLI 共用技能與全域指示', '在 Claude Code、Codex、Antigravity 與 Grok Build 的系統預設目錄之間同步共用全域技能（skills）與全域指示檔（CLAUDE.md／AGENTS.md／GEMINI.md），原本各自的檔案會備份保留，關閉時自動還原。', shareCliSw, 'shareCrossCliCustomizations'),
    tokenSw && row('閒置的 Claude 帳戶自動換發權杖', 'Claude 的登入權杖約 8 小時沒用就過期，用量會停在過期前的數字。過期時先借用同一個帳號在這台電腦的其他登入查用量；借不到就請 Claude CLI 代查用量（開一個不送訊息的 Claude 程序，CLI 會自己換發權杖；不用額度、不會開始新的 5 小時時間窗；每個帳戶最多每小時一次）。關掉時要等下次用這個帳戶對話才會更新。', tokenSw, 'claudeIdleTokenRefresh'));
  putBare('sync', null, sync && sync.node);
  putBare('collab', null, collab && collab.node);
  putBare('account', null, account && account.node);

  // 只留有區塊的分頁（桌面六個都在；瀏覽器模式剩「一般」與「對話」）
  const tabList = TABS.map((t) => ({ id: t.id, label: t.label, icon: t.icon, desc: (browser && t.browserDesc) || t.desc, secs: t.secs.filter((id) => secs[id]) })).filter((t) => t.secs.length > 0);
  const target = opts.section ? tabList.find((t) => t.secs.includes(opts.section)) : null;
  const tabs = settingsTabs(tabList, target ? target.id : lastTab, secs, defaults);
  const body = h('div', { class: 'settings' }, tabs.nav, tabs.scroller);
  body.addEventListener('keydown', tabs.onKey);

  const d = openDialog({
    title: '設定', body, width: 880, className: 'dlg-settings', initialFocus: tabs.current(),
    onClose: () => {
      if (instr && instr.value !== lastInstr) { state.settings.instructions = instr.value; post({ cmd: 'setting', key: 'instructions', value: instr.value }); }
      cont.save();
      if (sync) sync.dispose();
      if (collab) collab.dispose();
      open = null;
    },
  });
  if (cliGrid) renderCli(cliGrid);

  const refresh = () => {
    const cur = state.settings;
    theme.setValue(cur.theme || 'dark');
    if (document.activeElement !== size) { size.value = String(cur.fontSize || 14); syncSize(); }
    enterSw.input.checked = cur.enterToSend !== false;
    notifySw.input.checked = cur.notifications !== false;
    if (traySw) traySw.input.checked = cur.closeToTray !== false;
    if (bootSw) {
      bootSw.input.checked = cur.autoStart === true;
      bootSw.input.disabled = cur.autoStart == null;
      bootRow.querySelector('.set-desc').textContent = autoStartDesc(cur);
    }
    if (voiceSw) voiceSw.input.checked = cur.voiceNotifications !== false;
    if (schedDiagSw) schedDiagSw.input.checked = cur.scheduleAutoDiagnose !== false;
    if (deskTune) deskTune.refresh(cur.voiceNotifications !== false);
    if (localVoiceSw) localVoiceSw.input.checked = voiceOn();
    if (localTune) localTune.refresh(voiceOn());
    if (fxSw) fxSw.input.checked = cur.visualEffects !== false;
    if (autoSw) autoSw.input.checked = cur.autoUpdateCli !== false;
    if (remoteSw) remoteSw.input.checked = cur.claudeRemoteControl !== false;
    if (tokenSw) tokenSw.input.checked = cur.claudeIdleTokenRefresh !== false;
    if (resumeSw) resumeSw.input.checked = cur.autoResumeAfterQuota !== false;
    if (idleWarnSw) idleWarnSw.input.checked = cur.claudeIdleResumeWarning !== false;
    if (switchSw) switchSw.input.checked = cur.autoSwitchAccount !== false;
    if (crossSw) {
      crossSw.input.checked = cur.autoSwitchCrossProvider === true;
      crossSw.input.disabled = cur.autoSwitchAccount === false;
    }
    if (shareCliSw) shareCliSw.input.checked = cur.shareCrossCliCustomizations === true;
    if (policy) policy.setValue(policyOf(cur.cliRestartPolicy).value);
    if (instr && document.activeElement !== instr && instr.value === lastInstr) { instr.value = cur.instructions || ''; lastInstr = instr.value; }
    cont.refresh();
    if (defaults) defaults.refresh();
    if (appSec) appSec.refresh();
    if (cliGrid) renderCli(cliGrid);
    if (sync) sync.refresh();
  };
  // refreshApp：appUpdate 訊息只更新「Clode Myriagent」區塊（瀏覽器模式沒有這個區塊）
  open = { d, refresh, browser, refreshApp: () => { if (appSec) appSec.refresh(); } };
  // 要找的區塊不是分頁的第一塊時捲過去（分頁本身已經切好；瀏覽器模式沒建的區塊找不到分頁，停在上次的分頁）
  if (target && target.secs[0] !== opts.section) {
    const el = secs[opts.section];
    requestAnimationFrame(() => el.scrollIntoView({ block: 'start' }));
  }
  return d;
}

/** 滑桿的軌道（fx.css 第八批的 .range）：已選的比例與刻度格數交給 CSS 變數（只在值變了時呼叫） */
function paintRange(input) {
  input.style.setProperty('--fill', rangeFill(input.value, input.min, input.max) + '%');
  input.style.setProperty('--ticks', String(rangeTicks(input.min, input.max, input.step)));
}

/**
 * 語音通知列底下的語速與音調滑桿（桌面與瀏覽器共用）：get() 回 { speed, pitch }，放開滑桿（change；方向鍵每按一次也算）時 commit('speed'|'pitch', 值)，
 * 由它存起來並念一句試聽；拖動中只更新右邊的數字。refresh(enabled)：照目前的值重畫（拖動或聚焦中的不蓋掉），語音通知關著時停用（聽不到試聽）
 */
function voiceTune(get, commit) {
  const slider = (key, label, min, max, step, fmt) => {
    const val = h('span', { class: 'range-val' });
    const input = h('input', { type: 'range', class: 'range', min: String(min), max: String(max), step: String(step), 'aria-label': '語音通知的' + label, dataset: { tune: key } });
    const sync = () => { val.textContent = fmt(Number(input.value)); paintRange(input); };
    input.addEventListener('input', sync);
    input.addEventListener('change', () => commit(key, Number(input.value)));
    return { key, input, sync, node: h('div', { class: 'voice-tune-row' }, h('span', { class: 'voice-tune-label', text: label }), input, val) };
  };
  const sliders = [
    slider('speed', '語速', SPEED_MIN, SPEED_MAX, SPEED_STEP, fmtSpeed),
    slider('pitch', '音調', PITCH_MIN, PITCH_MAX, 1, fmtPitch),
  ];
  const node = h('div', { class: 'voice-tune' }, ...sliders.map((x) => x.node));
  const refresh = (enabled) => {
    const cur = get();
    for (const x of sliders) {
      if (document.activeElement !== x.input) x.input.value = String(cur[x.key]);
      x.sync();
      x.input.disabled = !enabled;
    }
    node.classList.toggle('off', !enabled);
  };
  return { node, refresh };
}

/**
 * 「對話 → 輸入」的「繼續」鈕指令：一個多行輸入框＋「恢復預設」。browser＝瀏覽器模式（存在這個瀏覽器、不送 setting）。
 * save()：有改就存（離開輸入框、按恢復預設、關掉設定時），輸入框換成實際的那一句（清空的變回預設）；
 * refresh()：宿主重送設定時照新的值重畫（聚焦中或改到一半的不蓋掉）
 */
function continueEditor(browser) {
  const current = () => continueTextOf(browser ? continueLocal() : state.settings.continueText);
  const input = h('textarea', { class: 'input cont-text', rows: 2, spellcheck: 'false', maxlength: String(CONTINUE_MAX), placeholder: CONTINUE_TEXT, 'aria-label': '「繼續」鈕送出的指令' });
  const reset = h('button', { type: 'button', class: 'btn sm', text: '恢復預設' });
  input.value = current();
  let last = input.value;
  const sync = () => { reset.disabled = continueTextOf(input.value) === CONTINUE_TEXT; };
  const save = () => {
    if (input.value === last) return;
    if (browser) input.value = setContinueLocal(input.value);
    else {
      post({ cmd: 'setting', key: 'continueText', value: input.value });
      state.settings.continueText = continueTextOf(input.value);
      input.value = state.settings.continueText;
    }
    last = input.value;
    sync();
  };
  input.addEventListener('input', sync);
  input.addEventListener('blur', save);
  reset.addEventListener('click', () => { input.value = CONTINUE_TEXT; save(); });
  sync();
  const desc = '輸入框旁「繼續」鈕按下去送給代理的那一句，可以多行。清空或按恢復預設就回到「' + CONTINUE_TEXT + '」。離開輸入框時儲存'
    + (browser ? '；只存在這個瀏覽器。' : '。');
  const node = h('div', { class: 'set-row set-row-block', dataset: { setting: browser ? 'continueLocal' : 'continueText' } },
    h('div', { class: 'set-text' }, h('div', { class: 'set-label', text: '「繼續」鈕的指令' }), h('div', { class: 'set-desc', text: desc })),
    input,
    h('div', { class: 'set-actions' }, reset));
  const refresh = () => {
    if (document.activeElement === input || input.value !== last) return;
    input.value = current();
    last = input.value;
    sync();
  };
  return { node, save, refresh };
}

/** 把語速與音調的滑桿接在設定列底下（換行佔滿整列） */
function withTune(rowNode, tune) {
  rowNode.classList.add('set-row-wrap');
  rowNode.append(tune.node);
  return rowNode;
}

/** 「開機時自動啟動」的說明：不能設定時說原因；能設定時說開機後視窗在哪（跟著「關閉視窗時縮到系統匣」） */
function autoStartDesc(cur) {
  if (cur.autoStart == null) return '只有安裝到這台電腦的 Clode Myriagent 才能設定（開發版或以其他資料夾執行時無法使用）。';
  return cur.closeToTray !== false
    ? '登入 Windows 時自動在背景啟動，只顯示系統匣圖示，點圖示叫出視窗。'
    : '登入 Windows 時自動啟動並開啟視窗。';
}

export function refreshSettings() {
  if (!open) return;
  // 對話框開著時模式變了（例如 init 比使用者打開設定晚到）：照新的模式重建，桌面專屬的列才不會留在瀏覽器模式的畫面上
  if (open.browser !== isBrowserMode()) { openSettings(); return; }
  open.refresh();
}

// ───── 分頁 ─────

/**
 * 左側的分類（直排的分頁列＋搜尋框；窄視窗換成上方的橫排）與右側的捲動區（各分頁一個面板，只顯示選取的那個；面板最上面是頁首）。
 * 鍵盤照 WAI-ARIA 分頁：上下（橫排時左右，兩種都認）在分頁之間移動並直接切換、Home／End 到頭尾，只有選取的分頁在 Tab 順序裡。
 * 搜尋框有字時捲動區換成搜尋結果（settingsrules.searchSettings），點了切到那一頁、捲過去並標一下；Ctrl+F 移到搜尋框。
 * defaults＝模型設定（reveal(el)：要捲過去的列在某個代理的子分頁裡時先切過去）
 */
function settingsTabs(list, initial, secs, defaults) {
  const n = ++seq;
  const bar = h('div', { class: 'set-tabs', role: 'tablist', 'aria-label': '設定分類', 'aria-orientation': 'vertical' });
  const scroller = h('div', { class: 'set-panels' });
  const items = list.map((t) => {
    // 搜尋時這一頁有幾項命中（第九批，renderResults 寫）
    const hits = h('span', { class: 'set-tab-n', hidden: true });
    const tab = h('button', { type: 'button', class: 'set-tab', role: 'tab', id: 'set-tab-' + n + '-' + t.id, 'aria-controls': 'set-panel-' + n + '-' + t.id, dataset: { tab: t.id } },
      icon(t.icon), h('span', { class: 'set-tab-label', text: t.label }), hits);
    const head = h('div', { class: 'set-page-h' },
      h('h3', { class: 'set-page-title', text: t.label }),
      t.desc ? h('p', { class: 'set-page-desc', text: t.desc }) : null);
    const panel = h('div', { class: 'set-panel', role: 'tabpanel', id: 'set-panel-' + n + '-' + t.id, 'aria-labelledby': tab.id, dataset: { tab: t.id } },
      head, ...t.secs.map((id) => secs[id]));
    tab.addEventListener('click', () => { if (query()) clearSearch(); select(t.id, false); });
    bar.append(tab);
    scroller.append(panel);
    return { t, tab, panel, hits };
  });
  // 選取標示（第九批）：一條亮線，換分頁時滑過去（只動 transform、一次性；直排在左緣、橫排在下緣）。位置在版面排好後量（placeInd）
  const ind = h('span', { class: 'set-tab-ind', 'aria-hidden': 'true' });
  bar.classList.add('has-ind');
  bar.append(ind);
  let indPlaced = false;
  function placeInd(animate) {
    if (!cur || !bar.isConnected || !cur.tab.offsetHeight) return;
    const t = cur.tab;
    const vertical = getComputedStyle(bar).flexDirection === 'column';
    ind.classList.toggle('anim', animate && indPlaced);
    ind.style.width = vertical ? '2px' : t.offsetWidth + 'px';
    ind.style.height = vertical ? Math.max(0, t.offsetHeight - 14) + 'px' : '2px';
    ind.style.transform = vertical ? 'translate(0px, ' + (t.offsetTop + 7) + 'px)' : 'translate(' + t.offsetLeft + 'px, ' + (t.offsetTop + t.offsetHeight - 2) + 'px)';
    indPlaced = true;
  }
  // 對話框打開、視窗變寬變窄（直排換橫排）時重量，不滑
  if (typeof ResizeObserver === 'function') new ResizeObserver(() => placeInd(false)).observe(bar);

  // ── 搜尋 ──
  const input = h('input', { type: 'search', class: 'input set-search-input', placeholder: '搜尋設定', spellcheck: 'false', autocomplete: 'off', 'aria-label': '搜尋設定', 'aria-controls': 'set-results-' + n });
  const results = h('div', { class: 'set-results', id: 'set-results-' + n, hidden: true });
  const status = h('div', { class: 'set-results-head', role: 'status', 'aria-live': 'polite' });
  const resultList = h('div', { class: 'set-results-list' });
  results.append(status, resultList);
  scroller.append(results);
  const search = h('div', { class: 'set-search' }, icon('search'), input);
  const nav = h('nav', { class: 'set-nav', 'aria-label': '設定' }, search, bar);
  const query = () => input.value.trim();
  let shown = [];   // 目前列出的結果（entry）

  /** 畫面上看得到的設定（每次搜尋時重新收集：同步區的階段、CLI 卡、模型清單都會變）；子分頁沒選到的代理照樣收（只有沒有設定的代理不收） */
  function collect() {
    const out = [];
    for (const it of items) {
      for (const id of it.t.secs) {
        const secEl = secs[id];
        const secTitle = textOf(secEl.querySelector(':scope > .set-h'));
        const rows = Array.from(secEl.querySelectorAll('.set-row')).filter((r) => !hiddenWithin(r, secEl));
        if (!rows.length) {
          out.push({ el: secEl, tab: it.t.label, tabId: it.t.id, sec: '', label: secTitle || it.t.label, desc: textOf(secEl.querySelector('.set-desc')) || it.t.desc || '', keywords: '' });
          continue;
        }
        for (const r of rows) {
          const path = pathOf(r, secEl);
          if (secTitle) path.unshift(secTitle);
          const label = textOf(r.querySelector('.set-text > .set-label'));
          out.push({
            el: r, tab: it.t.label, tabId: it.t.id, sec: path.join(' › '),
            label: label || path[path.length - 1] || it.t.label,
            desc: textOf(r.querySelector('.set-text > .set-desc')),
            keywords: SETTING_KEYWORDS[r.dataset.setting || r.dataset.kw] || '',
          });
        }
      }
    }
    return out;
  }

  function renderResults() {
    const q = query();
    const searching = !!q;
    scroller.classList.toggle('searching', searching);
    nav.classList.toggle('searching', searching);
    results.hidden = !searching;
    if (!searching) { shown = []; resultList.replaceChildren(); paintHits(null); return; }
    const terms = searchTerms(q);
    shown = searchSettings(collect(), q);
    paintHits(tabHitCounts(shown));
    status.textContent = shown.length ? '找到 ' + shown.length + ' 項設定' : '沒有符合「' + q + '」的設定';
    resultList.replaceChildren(...shown.map((e, i) => {
      const b = h('button', { type: 'button', class: 'set-result', dataset: { i: String(i), tab: e.tabId } },
        h('span', { class: 'set-result-path', text: e.tab + (e.sec ? ' › ' + e.sec : '') }),
        h('span', { class: 'set-result-label' }, ...marked(e.label, terms)),
        e.desc ? h('span', { class: 'set-result-desc' }, ...marked(snippet(e.desc, terms), terms)) : null);
      b.addEventListener('click', () => reveal(e));
      return b;
    }));
    scroller.scrollTop = 0;
  }

  function clearSearch() {
    input.value = '';
    renderResults();
  }

  /** 搜尋時各分類旁寫命中幾項、沒有命中的淡掉（第九批）；counts 是 null（沒在搜尋）時全部收起 */
  function paintHits(counts) {
    for (const it of items) {
      const c = counts ? counts[it.t.id] || 0 : 0;
      setHidden(it.hits, !c);
      if (c && it.hits.textContent !== String(c)) it.hits.textContent = String(c);
      it.tab.classList.toggle('nohit', !!counts && !c);
    }
  }

  /** 切到那一頁（與代理的子分頁）、捲到那一列並標一下，焦點移到那一列的第一個控制項 */
  function reveal(e) {
    clearSearch();
    select(e.tabId, false);
    if (defaults) defaults.reveal(e.el);
    requestAnimationFrame(() => {
      if (!e.el.isConnected) return;
      e.el.scrollIntoView({ block: 'center' });
      replayClass(e.el, 'set-hit', 1600);
      const ctl = e.el.querySelector('button:not([disabled]), input:not([disabled]), textarea:not([disabled]), [tabindex="0"]');
      try { (ctl || cur.tab).focus({ preventScroll: true }); } catch { /* 忽略 */ }
    });
  }

  input.addEventListener('input', renderResults);
  input.addEventListener('keydown', (e) => {
    if (e.isComposing) return;
    if (e.key === 'Escape' && input.value) { e.preventDefault(); clearSearch(); }
    else if (e.key === 'Enter' && shown.length) { e.preventDefault(); reveal(shown[0]); }
    else if (e.key === 'ArrowDown' && shown.length) { e.preventDefault(); resultList.firstElementChild.focus(); }
  });
  // 結果之間上下移動；最上面再往上回到搜尋框；Esc 清掉搜尋、回到搜尋框（不關掉設定）
  resultList.addEventListener('keydown', (e) => {
    const b = e.target.closest('.set-result');
    if (!b) return;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      const next = e.key === 'ArrowDown' ? b.nextElementSibling : b.previousElementSibling;
      (next || (e.key === 'ArrowUp' ? input : b)).focus();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      clearSearch();
      input.focus();
    }
  });

  let cur = null;
  function select(id, focus) {
    const it = items.find((x) => x.t.id === id) || items[0];
    if (focus) it.tab.focus();
    if (cur === it) return;
    cur = it;
    lastTab = it.t.id;
    for (const x of items) {
      const on = x === it;
      x.tab.setAttribute('aria-selected', String(on));
      x.tab.tabIndex = on ? 0 : -1;
      x.panel.hidden = !on;
    }
    scroller.scrollTop = 0;
    placeInd(true);
  }
  bar.addEventListener('keydown', (e) => {
    const next = tabKeyTarget(e.key, items.indexOf(cur), items.length);
    if (next === null) return;
    e.preventDefault();
    if (query()) clearSearch();
    select(items[next].t.id, true);
  });
  select(initial, false);
  /** 整個對話框的 Ctrl+F：移到搜尋框（對話框開著時 app.js 的快捷鍵不處理） */
  const onKey = (e) => {
    if (!e.ctrlKey || e.altKey || e.metaKey || e.shiftKey || (e.key || '').toLowerCase() !== 'f') return;
    e.preventDefault();
    input.focus();
    input.select();
  };
  return { nav, scroller, current: () => cur.tab, onKey };
}

const textOf = (el) => (el ? el.textContent.trim() : '');

/** el 到 root（不含）之間有沒有被藏起來的；代理的子分頁沒選到而藏起來的不算（有設定的代理才算看得到） */
function hiddenWithin(el, root) {
  for (let n = el; n && n !== root; n = n.parentElement) {
    if (n.classList.contains('set-prov')) { if (n.dataset.available !== 'true') return true; continue; }
    if (n.hidden) return true;
  }
  return false;
}

/** 一列在區塊裡的位置：代理的子分頁（代理名稱）、區塊裡的分組標題（同步區的「其他電腦的遠端操作」、子分頁的「推理強度」） */
function pathOf(el, root) {
  const path = [];
  for (let n = el.parentElement; n && n !== root; n = n.parentElement) {
    const head = n.querySelector(':scope > .set-h');
    if (head) path.unshift(textOf(head));
    if (n.classList.contains('set-prov') && n.dataset.name) path.unshift(n.dataset.name);
  }
  return path;
}

/** 文字切成一般的字與 <mark>（命中搜尋的字），一律是文字節點 */
const marked = (text, terms) => highlightParts(text, terms).map((p) => (p.hit ? h('mark', { text: p.text }) : p.text));

/** 只更新「Clode Myriagent」區塊（appUpdate 訊息：下載進度、排空的數字會一直更新，不必每次重畫整個設定頁與 CLI 卡） */
export function refreshAppSettings() { if (open) open.refreshApp(); }

// ───── 模型設定 ─────

/**
 * 每種代理一個子分頁（上方一排代理卡：標誌、名稱、目前的預設模型與權限模式的色點）：
 * 子分頁裡是「新對話的預設」（模型、權限模式；Claude 另有備援模型）與「推理強度」（每個模型一列）兩張卡片。
 * 宿主沒有給設定的代理（state.settings.conversationDefaults 沒有它）不顯示子分頁；選到的不見了就換到上次看的或第一個還在的。
 * 宿主重送設定時就地更新（模型以 value 對齊，開著的選單錨點不會被換掉）。
 * select(provider)：切換子分頁；reveal(el)：el 在某個代理的子分頁裡時切過去（搜尋結果用）
 */
function conversationDefaults() {
  const n = ++seq;
  const bar = h('div', { class: 'set-subtabs', role: 'tablist', 'aria-label': '代理種類' });
  const panels = h('div', { class: 'set-provs' });
  const empty = h('p', { class: 'set-desc', hidden: true, text: '登入任何一種代理的帳戶之後，就可以在這裡設定新對話的預設。' });
  const note = h('p', { class: 'set-desc set-defaults-note', text: '模型選「沿用上次選擇」時，每個帳戶沿用上次在對話裡選的；在對話裡換模型時，推理強度會換成那個模型在這裡設定的預設。' });
  const node = h('div', { class: 'set-defaults' }, bar, empty, panels, note);
  const blocks = state.providers.map((p) => {
    const tab = h('button', { type: 'button', class: 'set-subtab', role: 'tab', id: 'set-subtab-' + n + '-' + p.key, 'aria-controls': 'set-prov-' + n + '-' + p.key, dataset: { provider: p.key } },
      providerMark(p.key, 'set-subtab-mark'),
      h('span', { class: 'set-subtab-text' },
        h('span', { class: 'set-subtab-name', text: p.name }),
        h('span', { class: 'set-subtab-sum' }, h('span', { class: 'set-pick-dot', hidden: true }), h('span', { class: 'set-subtab-model' }))));
    tab.addEventListener('click', () => select(p.key, false));
    bar.append(tab);
    const model = pickButton('set-defmodel');
    const perm = pickButton('set-perm');
    const models = card();
    models.classList.add('set-models');
    // Claude 的備援模型（--fallback-model）：主模型過載或不能用時 CLI 自動改用（設定 claudeFallbackModel）
    let fallback = null;
    if (p.key === 'Claude') {
      fallback = pickButton('set-fallback');
      fallback.addEventListener('click', () => openFallbackMenu(fallback));
    }
    // 自動壓縮上限（宿主的 AutoCompactRules）：只有宿主給了 autoCompactRange 的代理顯示這一列
    const compact = autoCompactEditor(p);
    const block = h('div', { class: 'set-prov', role: 'tabpanel', id: 'set-prov-' + n + '-' + p.key, 'aria-labelledby': tab.id, dataset: { provider: p.key, name: p.name } },
      h('div', { class: 'set-group' },
        h('h4', { class: 'set-h', text: '新對話的預設' }),
        card(
          row('模型', '新對話開始時的模型；帳戶沒有這個模型時用帳戶預設模型。', model, null, 'defaultModel'),
          row('權限模式', '新對話開始時的權限模式。', perm, null, 'defaultPermission'),
          fallback && row('備援模型', '主模型過載或不能用時，CLI 自動改用這個模型接著做（和對話的模型相同時不用）。', fallback, 'claudeFallbackModel', 'fallbackModel'),
          compact.row)),
      h('div', { class: 'set-group' },
        h('h4', { class: 'set-h', text: '推理強度' }),
        h('p', { class: 'set-desc set-group-desc', text: '每個模型在新對話開始時的推理強度；「CLI 預設」由 CLI 依模型與帳戶決定，不支援的模型不能選。' }),
        models));
    model.addEventListener('click', () => openModelMenu(p.key, model));
    perm.addEventListener('click', () => openPermissionMenu(p.key, perm));
    panels.append(block);
    return { p, tab, block, model, perm, models, fallback, compact, available: false };
  });

  let cur = null;
  function select(key, focus) {
    const avail = blocks.filter((b) => b.available);
    const b = avail.find((x) => x.p.key === key) || avail[0] || null;
    if (b && focus) b.tab.focus();
    cur = b;
    if (b) lastProvider = b.p.key;
    for (const x of blocks) {
      const on = x === b;
      x.tab.setAttribute('aria-selected', String(on));
      x.tab.tabIndex = on ? 0 : -1;
      x.block.hidden = !on;
    }
  }
  bar.addEventListener('keydown', (e) => {
    const avail = blocks.filter((b) => b.available);
    const next = tabKeyTarget(e.key, avail.indexOf(cur), avail.length);
    if (next === null) return;
    e.preventDefault();
    select(avail[next].p.key, true);
  });

  const refresh = () => {
    const all = (state.settings && state.settings.conversationDefaults) || {};
    for (const b of blocks) {
      const cd = all[b.p.key];
      b.available = !!cd;
      b.tab.hidden = !cd;
      b.block.dataset.available = String(!!cd);
      if (!cd) continue;
      const cm = cd.model ? (cd.models || []).find((m) => m.value === cd.model) : null;
      const shownModel = !cd.model ? LAST_MODEL : cm ? modelText(cm) : cd.model;
      setPick(b.model, shownModel, b.p.name + ' 的預設模型');
      const mode = (cd.permissionModes || []).find((m) => m.value === cd.permission);
      const permText = mode ? mode.label || mode.value : cd.permission || '';
      const permColor = mode ? toneColor(permissionTone(mode)) : null;
      setPick(b.perm, permText, b.p.name + ' 的預設權限模式', permColor);
      // 子分頁上的摘要：目前的預設模型，前面是權限模式的色點（同權限模式按鈕）
      b.tab.querySelector('.set-subtab-model').textContent = shownModel;
      const dot = b.tab.querySelector('.set-pick-dot');
      dot.hidden = !permColor;
      if (permColor) dot.style.setProperty('--acc', permColor);
      b.tab.title = '模型：' + shownModel + (permText ? '\n權限模式：' + permText : '');
      reconcile(b.models, cd.models || [], (m) => m.value, (m) => createModelRow(b.p.key, m), updateModelRow);
      if (b.fallback) {
        const fb = state.settings.claudeFallbackModel || null;
        const fm = fb ? (cd.models || []).find((m) => m.value === fb) : null;
        setPick(b.fallback, !fb ? '不用' : fm ? fm.label || fm.value : fb, 'Claude 的備援模型');
      }
      b.compact.refresh(cd);
    }
    const any = blocks.some((b) => b.available);
    empty.hidden = any;
    note.hidden = !any;
    // 選到的代理不見了（或還沒選）：換到上次看的或第一個還在的
    if (!cur || !cur.available) select(lastProvider, false);
  };
  refresh();
  const reveal = (el) => {
    const prov = el && el.closest ? el.closest('.set-prov') : null;
    if (prov && panels.contains(prov)) select(prov.dataset.provider, false);
  };
  return { node, refresh, select: (key) => select(key, false), reveal };
}

/**
 * 自動壓縮上限：一個短的輸入框（例如 400k；Grok 是百分比，例如 70%；留空＝CLI 預設）。離開輸入框（Enter 也是）時送 setConversationDefault 的 autoCompact，
 * 宿主夾進範圍後重送設定、輸入框換成宿主的值；看不懂的值標成錯誤、不送。Esc 還原。refresh(cd)：兩種範圍都沒有的代理整列藏起來
 */
function autoCompactEditor(p) {
  const input = h('input', { type: 'text', class: 'input set-tokens', inputmode: 'decimal', spellcheck: 'false', autocomplete: 'off', placeholder: 'CLI 預設' });
  const label = h('div', { class: 'set-label' });
  const desc = h('div', { class: 'set-desc' });
  const node = h('div', { class: 'set-row', dataset: { kw: 'autoCompact' } }, h('div', { class: 'set-text' }, label, desc), h('div', { class: 'set-ctl' }, input));
  node.hidden = true;
  // 兩種單位：token 數（Claude、Codex：autoCompact／autoCompactRange）或上下文視窗的百分比（Grok：autoCompactPercent／autoCompactPercentRange）
  const UNITS = {
    tokens: { key: 'autoCompact', parse: parseTokens, fmt: fmtTokens, label: '自動壓縮上限', inputmode: 'decimal' },
    percent: { key: 'autoCompactPercent', parse: parsePercent, fmt: fmtPercent, label: '自動壓縮門檻', inputmode: 'numeric' },
  };
  let unit = UNITS.tokens;
  let cur = null;
  const show = () => { input.value = unit.fmt(cur); input.removeAttribute('aria-invalid'); };
  const commit = () => {
    const v = unit.parse(input.value);
    if (v === undefined) { input.setAttribute('aria-invalid', 'true'); return; }
    input.removeAttribute('aria-invalid');
    if (v === cur) { show(); return; }
    post({ cmd: 'setConversationDefault', provider: p.key, key: unit.key, value: v == null ? '' : String(v) });
  };
  input.addEventListener('blur', commit);
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); input.blur(); }   // 離開輸入框才儲存，宿主送回來的值才換得上去
    else if (e.key === 'Escape' && input.value !== unit.fmt(cur)) { e.preventDefault(); e.stopPropagation(); show(); }
  });
  input.addEventListener('input', () => input.removeAttribute('aria-invalid'));
  const refresh = (cd) => {
    const tokens = cd && cd.autoCompactRange;
    const percent = cd && cd.autoCompactPercentRange;
    node.hidden = !tokens && !percent;
    if (node.hidden) return;
    unit = tokens ? UNITS.tokens : UNITS.percent;
    const range = tokens || percent;
    cur = cd[unit.key] == null ? null : cd[unit.key];
    label.textContent = unit.label;
    input.inputMode = unit.inputmode;
    input.setAttribute('aria-label', p.name + ' 的' + unit.label);
    desc.textContent = tokens
      ? '上下文用到這麼多 token 時，CLI 自動壓縮較早的對話（例如 400k）；留空是 CLI 預設。可設 '
        + fmtTokens(range.min) + '～' + fmtTokens(range.max) + '，超出的自動調到範圍內；比模型的上下文視窗大時照 CLI 預設。已開的對話下一則訊息起套用。'
      : '上下文用到視窗的這個百分比時，CLI 自動壓縮較早的對話（例如 70%）；留空是 CLI 預設（模型的設定，通常是 80%）。可設 '
        + range.min + '%～' + range.max + '%，超出的自動調到範圍內；太低會每一輪都壓縮。已開的對話下一則訊息起套用。';
    if (document.activeElement !== input) show();
  };
  return { row: node, refresh };
}

function pickButton(cls) {
  return h('button', { type: 'button', class: 'btn sm set-pick ' + cls, 'aria-haspopup': 'menu' },
    h('span', { class: 'set-pick-dot', hidden: true }), h('span', { class: 'set-pick-text' }), icon('chevDown'));
}

function setPick(btn, text, what, color) {
  btn.querySelector('.set-pick-text').textContent = text;
  btn.setAttribute('aria-label', what + '：' + text);
  const dot = btn.querySelector('.set-pick-dot');
  dot.hidden = !color;
  if (color) dot.style.setProperty('--acc', color);
}

function createModelRow(provider, m) {
  const btn = pickButton('set-effort');
  const row = h('div', { class: 'set-row set-model', dataset: { model: m.value, kw: 'effort' } },
    h('div', { class: 'set-text' }, h('div', { class: 'set-label' }), h('div', { class: 'set-desc' })),
    h('div', { class: 'set-ctl' }, btn));
  btn.addEventListener('click', () => openEffortMenu(provider, row._model, btn));
  updateModelRow(row, m);
  return row;
}

function updateModelRow(row, m) {
  row._model = m;
  const name = modelText(m);
  row.querySelector('.set-label').textContent = name;
  row.querySelector('.set-desc').textContent = name !== m.value ? m.value : '';   // 別名與完整 id 並列時分得出來
  const efforts = m.efforts || [];
  const btn = row.querySelector('.set-effort');
  btn.disabled = efforts.length === 0;
  btn.title = efforts.length === 0 ? '這個模型不支援推理強度' : '';
  setPick(btn, efforts.length === 0 ? '不支援' : m.effort ? EFFORT_LABEL[m.effort] || m.effort : cliDefaultText(m), name + ' 的預設推理強度');
}

/** 「CLI 預設」後面附上這個模型不指定強度時實際用的強度（宿主量到的 defaultEffort；不知道就不附） */
function cliDefaultText(m) {
  return 'CLI ' + effortText('default', m.defaultEffort, EFFORT_LABEL);
}

function openEffortMenu(provider, m, btn) {
  const items = [{ label: cliDefaultText(m), desc: '由 CLI 依模型與帳戶決定', checked: !m.effort, onSelect: () => setDefault(provider, 'effort', 'default', m.value) }];
  for (const e of m.efforts || []) items.push({ label: EFFORT_LABEL[e] || e, desc: EFFORT_DESC[e], hint: e, checked: m.effort === e, onSelect: () => setDefault(provider, 'effort', e, m.value) });
  openMenu(btn, items, { placement: 'bottom-end', minWidth: 180 });
}

const LAST_MODEL = '沿用上次選擇';

/** 預設模型：沿用上次選擇（每個帳戶各自記住）或這一家的某個模型（不在清單裡的舊值也列出來，才看得到目前是什麼） */
function openModelMenu(provider, btn) {
  const cd = state.settings.conversationDefaults && state.settings.conversationDefaults[provider];
  if (!cd) return;
  const models = cd.models || [];
  const items = [{ label: LAST_MODEL, desc: '每個帳戶沿用上次在對話裡選的模型', checked: !cd.model, onSelect: () => setDefault(provider, 'model', '') }];
  for (const m of models) items.push({ label: modelText(m), desc: m.description, checked: m.value === cd.model, onSelect: () => setDefault(provider, 'model', m.value) });
  if (cd.model && !models.some((m) => m.value === cd.model)) items.push({ label: cd.model, desc: '目前的設定（這一家的帳戶都沒有回報這個模型）', checked: true, onSelect: () => {} });
  openMenu(btn, items, { placement: 'bottom-end', minWidth: 280 });
}

function openFallbackMenu(btn) {
  const cd = state.settings.conversationDefaults && state.settings.conversationDefaults.Claude;
  if (!cd) return;
  const cur = state.settings.claudeFallbackModel || null;
  const models = (cd.models || []).filter((m) => m.value !== 'default');
  const pick = (v) => { state.settings.claudeFallbackModel = v || null; post({ cmd: 'setting', key: 'claudeFallbackModel', value: v }); };
  const items = [{ label: '不用', desc: '主模型不能用時這一輪就失敗（之後照樣會自動重試過載）', checked: !cur, onSelect: () => pick('') }];
  for (const m of models) items.push({ label: m.label || m.value, desc: m.description, checked: m.value === cur, onSelect: () => pick(m.value) });
  if (cur && !models.some((m) => m.value === cur)) items.push({ label: cur, desc: '目前的設定（Claude 的帳戶都沒有回報這個模型）', checked: true, onSelect: () => {} });
  openMenu(btn, items, { placement: 'bottom-end', minWidth: 280 });
}

function openPermissionMenu(provider, btn) {
  const cd = state.settings.conversationDefaults && state.settings.conversationDefaults[provider];
  if (!cd) return;
  const items = (cd.permissionModes || []).map((m) => ({
    label: m.label || m.value, desc: m.description, checked: m.value === cd.permission, color: toneColor(permissionTone(m)),
    onSelect: async () => {
      if (m.value === cd.permission) return;
      if (permissionTone(m) === 'danger' && (await confirmFullAccess(m, false)) !== 'ok') return;
      setDefault(provider, 'permission', m.value);
    },
  }));
  openMenu(btn, items, { placement: 'bottom-end', minWidth: 280 });
}

function setDefault(provider, key, value, model) {
  post({ cmd: 'setConversationDefault', provider, key, model, value });
}

/**
 * 〔熱更新〕「CLI 更新後」三選一：直排的單選清單（說明比較長，放不進分段控制）。
 * 方向鍵在選項之間移動並直接套用（同原生的單選按鈕群組），只有選取的那一項在 Tab 順序裡
 */
function policyPicker(value, onChange) {
  const wrap = h('div', { class: 'set-radios', role: 'radiogroup', 'aria-label': 'CLI 更新後' });
  const buttons = RESTART_POLICIES.map((p) => {
    const b = h('button', { type: 'button', class: 'set-radio', role: 'radio', dataset: { value: p.value } },
      h('span', { class: 'set-radio-mark' }),
      h('span', { class: 'set-radio-text' },
        h('span', { class: 'set-radio-label' }, h('span', { text: p.label }), p.tag ? h('span', { class: 'tag', text: p.tag }) : null),
        h('span', { class: 'set-radio-desc', text: p.desc })));
    b.addEventListener('click', () => pick(p.value, false));
    wrap.append(b);
    return b;
  });
  let current = null;
  const set = (v) => {
    current = v;
    for (const b of buttons) {
      const on = b.dataset.value === v;
      b.classList.toggle('on', on);
      b.setAttribute('aria-checked', String(on));
      b.tabIndex = on ? 0 : -1;
    }
  };
  function pick(v, focus) {
    if (focus) { const b = buttons.find((x) => x.dataset.value === v); if (b) b.focus(); }
    if (v === current) return;
    set(v);
    onChange(v);
  }
  wrap.addEventListener('keydown', (e) => {
    const step = e.key === 'ArrowDown' || e.key === 'ArrowRight' ? 1 : e.key === 'ArrowUp' || e.key === 'ArrowLeft' ? -1 : 0;
    if (!step) return;
    e.preventDefault();
    const i = Math.max(0, RESTART_POLICIES.findIndex((p) => p.value === current));
    pick(RESTART_POLICIES[(i + step + RESTART_POLICIES.length) % RESTART_POLICIES.length].value, true);
  });
  set(value);
  wrap.setValue = set;
  return wrap;
}

// ───── 〔同步〕多電腦同步（WP3.4） ─────
// 同步狀態的來源（D9）：WP3.5 的 state.remote 有值（!== undefined）時一律讀它，否則讀這裡自己以 onHostMessage 維護的鏡像。
// 重畫延到這一則訊息的所有監聽者都跑完之後（setTimeout 0，同一輪合併成一次）：WebView2 原生派送訊息時，微任務會在監聽者之間執行，
// queueMicrotask 會比 app.js 的監聽者先跑、讀到還沒更新的 state.remote

// starting＝init.syncStarting（桌面的同步元件還在讀配對資料；這段期間 remote 是 null，但不一定是未配對）：收到任何 sync 就結束。
// 不論 state.remote 是不是由 WP3.5 維護，都由這裡追蹤（remote 是否為 null 的語意不變）
const mirror = { remote: undefined, starting: false };
let refreshQueued = false;

onHostMessage((msg) => {
  const next = applySyncMessage(mirror.remote, msg);
  const starting = applySyncStarting(mirror.starting, msg);
  if (next === mirror.remote && starting === mirror.starting) return;
  mirror.remote = next;
  mirror.starting = starting;
  if (refreshQueued) return;
  refreshQueued = true;
  setTimeout(() => { refreshQueued = false; refreshSettings(); }, 0);
});

/**
 * Remote（WPR）收到一則宿主訊息之後的值：init → msg.remote（沒有就是 null）；sync 是 null → null（回到未配對）；
 * remote 是 null 而 sync 不是 → 自己建 { mode: 'desktop', deviceId: sync.deviceId, devices: [], sync }；sync → 換掉 sync；
 * devices → 換掉 devices（remote 是 null 時不變）；其他訊息回原物件
 */
export function applySyncMessage(remote, msg) {
  if (!msg || typeof msg !== 'object') return remote;
  switch (msg.type) {
    case 'init':
      return msg.remote ?? null;
    case 'sync':
      if (!msg.sync || typeof msg.sync !== 'object') return null;
      if (!remote) return { mode: 'desktop', deviceId: msg.sync.deviceId ?? null, devices: [], sync: msg.sync };
      return Object.assign({}, remote, { sync: msg.sync });
    case 'devices':
      if (!remote) return remote;
      return Object.assign({}, remote, { devices: Array.isArray(msg.devices) ? msg.devices : [] });
    default:
      return remote;
  }
}

/** 收到一則宿主訊息之後是不是「同步元件啟動中」：init → msg.syncStarting === true；sync → false；其他訊息不變 */
export function applySyncStarting(starting, msg) {
  if (!msg || typeof msg !== 'object') return starting;
  switch (msg.type) {
    case 'init':
      return msg.syncStarting === true;
    case 'sync':
      return false;
    default:
      return starting;
  }
}

/** 設定頁看的同步狀態（D9）：WP3.5 的 state.remote 有值時讀它，否則讀鏡像；都沒有是 null */
export function currentRemote() {
  return state.remote !== undefined ? state.remote : (mirror.remote ?? null);
}

/**
 * 同步區要顯示哪一個階段：browser｜starting｜unpaired｜pairingStart｜pairingCode｜pairingError｜confirm｜paired。
 * starting＝同步元件還在啟動（init.syncStarting）：沒有 sync 時顯示「啟動中」而不是未配對的配對表單
 */
export function syncPhase(remote, starting = false) {
  if (remote && remote.mode === 'browser') return 'browser';
  const s = remote && remote.sync;
  if (!s || typeof s !== 'object') return starting ? 'starting' : 'unpaired';
  if (s.pairing) {
    if (s.pairing.error) return 'pairingError';
    if (!s.pairing.userCode) return 'pairingStart';   // 還沒拿到配對碼（宿主送空字串）
    return 'pairingCode';
  }
  if (s.confirm) return 'confirm';
  return 'paired';
}

/** 和伺服器的時間差：不到 2 分鐘不提示 */
export function skewText(ms) {
  if (typeof ms !== 'number' || !isFinite(ms) || Math.abs(ms) < 120000) return null;
  return '這台和伺服器的時間差約 ' + Math.round(Math.abs(ms) / 60000) + ' 分鐘，請同步 Windows 時間';
}

/** SyncStatus.state → 狀態膠囊的文字與樣式（沿用帳戶卡的 pill 色：st-ready 綠、st-busy 藍、st-notLoggedIn 琥珀、st-checking 中性、st-error 紅） */
export const SYNC_STATE_VIEW = Object.freeze({
  online: Object.freeze({ label: '已連線', tone: 'st-ready' }),
  connecting: Object.freeze({ label: '連線中', tone: 'st-busy' }),
  pairing: Object.freeze({ label: '配對中', tone: 'st-busy' }),
  offline: Object.freeze({ label: '離線', tone: 'st-notLoggedIn' }),
  paused: Object.freeze({ label: '已暫停', tone: 'st-checking' }),
  stopped: Object.freeze({ label: '已停止', tone: 'st-error' }),
});

const stateView = (st) => SYNC_STATE_VIEW[st] || { label: String(st || '不明'), tone: 'st-checking' };

/** 上次在配對欄位填的名稱（重新配對時沿用；只記在這次執行） */
let pairName = '';

function syncButton(label, act, onClick, cls) {
  const b = h('button', { type: 'button', class: 'btn sm' + (cls ? ' ' + cls : ''), dataset: { act }, text: label });
  b.addEventListener('click', onClick);
  return b;
}

const actions = (...buttons) => h('div', { class: 'card-actions' }, ...buttons);

/** 上次配對用的 Hub 網址（這個瀏覽器設定檔記著，下次不必再打） */
const PAIR_SERVER_KEY = 'myriagent.pairServer';
let pairServer = (() => { try { return localStorage.getItem(PAIR_SERVER_KEY) || ''; } catch { return ''; } })();
function startPairing() {
  // 配對失敗後的「重新配對」沒有表單：用剛才連的那一台
  const server = normalizeHubUrl(pairServer) || normalizeHubUrl(state.remote && state.remote.sync && state.remote.sync.server);
  // 錯誤提示每次從畫面上找：不在模組裡留著對話框裡的節點（關掉設定之後整個對話框會被它留住）
  const error = document.querySelector('.dlg-settings .sync [data-field="pairServerError"]');
  if (!server) {
    if (error) { error.hidden = false; error.textContent = '請填你的 Hub 網址，例如 https://hub.example.com（只接受 https，不能帶路徑）'; }
    return;
  }
  if (error) error.hidden = true;
  pairServer = server;
  try { localStorage.setItem(PAIR_SERVER_KEY, server); } catch { /* 存不了就下次再打 */ }
  const name = pairName.trim();
  post(name ? { cmd: 'syncPair', server, deviceName: name } : { cmd: 'syncPair', server });
}

const cancelPairing = () => post({ cmd: 'syncCancelPair' });

/** 解除配對：先確認（本機帳戶與對話不動；伺服器上的這台要到網頁的裝置頁撤銷，WPR syncUnpair） */
async function confirmUnpair(title) {
  const r = await confirmDialog({
    title: title || '解除配對',
    message: '這台會停止同步，並刪除這台保存的同步權杖與信任資料。',
    detail: '本機帳戶與對話不動；請到 Hub 網頁的裝置頁撤銷這台。',
    confirmLabel: '解除配對',
    danger: true,
  });
  if (r.ok) post({ cmd: 'syncUnpair' });
}

/**
 * 同步區：依階段建一次畫面，之後的 refresh 只就地更新文字（輸入中的欄位、開著的確認框、倒數的節點都不重建）；
 * 階段改變才換整個畫面。dispose 清掉倒數計時器（對話框關閉時呼叫）
 */
function syncSection() {
  const node = h('div', { class: 'sync' });
  let phase = null;
  let view = null;
  const refresh = () => {
    const remote = currentRemote();
    const next = syncPhase(remote, mirror.starting);
    if (next !== phase || !view) {
      if (view && view.dispose) view.dispose();
      phase = next;
      view = SYNC_VIEWS[next]();
      node.dataset.phase = next;
      node.replaceChildren(view.node);
    }
    view.update(remote);
  };
  refresh();
  return {
    node,
    refresh,
    dispose: () => { if (view && view.dispose) view.dispose(); view = null; phase = null; },
  };
}

const SYNC_VIEWS = {
  // 瀏覽器模式不建同步區（openSettings 不呼叫 syncSection）；這裡只是防禦：伺服器沒有這台的同步狀態
  browser: () => ({ node: h('div'), update() {} }),
  // 已配對的電腦啟動後頭幾秒 init.remote 也是 null：不顯示配對表單（按了也只會被拒），等宿主送 sync
  starting: () => ({ node: h('div', { class: 'sync-phase' }, h('p', { class: 'set-desc' }, spinner(), ' 同步元件啟動中…')), update() {} }),
  unpaired: unpairedView,
  pairingStart: pairingStartView,
  pairingCode: pairingCodeView,
  pairingError: pairingErrorView,
  confirm: () => pairedView(true),
  paired: () => pairedView(false),
};

function unpairedView() {
  const name = h('input', { type: 'text', class: 'input', maxlength: '60', spellcheck: 'false', placeholder: '留空就用電腦名稱', 'aria-label': '這台在同步裡的名稱', dataset: { field: 'pairName' } });
  name.value = pairName;
  name.addEventListener('input', () => { pairName = name.value; });
  name.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing) { e.preventDefault(); startPairing(); } });
  const server = h('input', { type: 'url', class: 'input', maxlength: '200', spellcheck: 'false', placeholder: 'https://hub.example.com', 'aria-label': 'Hub 網址', dataset: { field: 'pairServer' } });
  server.value = pairServer;
  server.addEventListener('input', () => { pairServer = server.value; });
  server.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing) { e.preventDefault(); startPairing(); } });
  const serverError = h('p', { class: 'note err', hidden: true, dataset: { field: 'pairServerError' } });
  const node = h('div', { class: 'sync-phase' },
    h('p', { class: 'set-desc', text: '帳戶清單、用量、CLI 版本會同步到你自己架設的 MyriagentHub；登入憑證不會離開這台電腦。' }),
    card(row('Hub 網址', '你架設的 MyriagentHub 的網址（架設方法見 docs/hub-setup.md），例如 https://hub.example.com。', server),
      row('這台的名稱', '其他電腦與網頁上顯示的名稱，最多 60 字。', name)),
    serverError,
    h('div', { class: 'set-actions' }, syncButton('連接到 Hub', 'pair', startPairing, 'primary')),
    // 〔遠端〕解除配對之後回收區照樣在（保留 30 天），沒配對也能還原（WPR syncListTrash）
    h('div', { class: 'sync-group', dataset: { group: 'trash' } },
      groupHead('回收區'),
      card(row('遠端刪除的對話', '在這台保留 30 天，可以還原。', syncButton('回收區…', 'trash', openTrash)))));
  return { node, update() {} };
}

function pairingStartView() {
  const text = h('span');
  const node = h('div', { class: 'sync-phase' },
    h('div', { class: 'sync-box' }, h('p', { class: 'set-desc' }, spinner(), ' ', text)),
    actions(syncButton('取消', 'cancelPair', cancelPairing)));
  return {
    node,
    update(remote) { text.textContent = '正在向 ' + ((remote.sync && remote.sync.server) || 'Hub') + ' 取得配對碼…'; },
  };
}

function pairingCodeView() {
  const code = h('div', { class: 'sync-code mono', 'aria-label': '配對碼' });
  const uri = h('span', { class: 'mono', dataset: { field: 'verificationUri' } });
  const countdown = h('span', { class: 'sync-countdown', 'aria-live': 'off' });
  // 到期倒數條（fxrules.pairCountdown）：20 格、亮的格數＝還剩多少時間，剩一分鐘轉琥珀色
  const bar = h('div', { class: 'sync-cd', hidden: true, 'aria-hidden': 'true' }, h('i'));
  const fp = h('span', { class: 'sync-fp mono', dataset: { field: 'deviceFingerprint' } });
  let current = { uri: '', expiresAt: 0, code: '', startAt: 0 };
  const copy = syncButton('複製', 'copyUri', () => {
    if (current.uri) copyToClipboard(current.uri, (t) => post({ cmd: 'copyText', text: t }));
  });
  const openBtn = syncButton('在瀏覽器開啟', 'openPairing', () => {
    // 按下的當下再看一次宿主的判斷（網址合格才開）
    const remote = currentRemote();
    const p = remote && remote.sync && remote.sync.pairing;
    if (p && p.openable === true && typeof p.verificationUriComplete === 'string' && p.verificationUriComplete)
      post({ cmd: 'openLink', href: p.verificationUriComplete });
  });
  const tick = () => {
    if (!current.expiresAt) { countdown.textContent = ''; bar.hidden = true; return; }
    const now = Date.now();
    const left = current.expiresAt - now;
    countdown.textContent = left > 0 ? '配對碼 ' + fmtElapsed(left) + ' 後過期' : '配對碼已過期，請取消後重新配對';
    const cd = pairCountdown(current.expiresAt, current.startAt, now);
    bar.hidden = !cd;
    if (!cd) return;
    const fill = 'scaleX(' + (countdownCells(cd.frac, 20) / 20).toFixed(3) + ')';
    if (bar.firstChild.style.transform !== fill) bar.firstChild.style.transform = fill;
    bar.classList.toggle('warn', cd.warn);
  };
  const timer = setInterval(() => { if (countdown.isConnected) tick(); }, 1000);
  const node = h('div', { class: 'sync-phase' },
    h('p', { class: 'set-desc', text: '在手機或其他電腦登入你的 Hub 網頁，打開下面的網址並輸入配對碼，核准這台電腦。' }),
    h('div', { class: 'sync-box' }, code, countdown, bar, h('div', { class: 'card-actions' }, uri, copy, openBtn)),
    card(row('這台的指紋', '核准頁會顯示同一組指紋，兩邊相同再核准。', fp)),
    actions(syncButton('取消', 'cancelPair', cancelPairing)));
  return {
    node,
    update(remote) {
      const s = remote.sync || {};
      const p = s.pairing || {};
      // 配對碼出現（或換了一組）時逐字解碼（textfx.js，同自動命名的標題）；剛建好、還沒放進畫面時等下一個影格
      const userCode = p.userCode || '';
      const fresh = userCode !== current.code;
      const show = () => setDecodedText(code, userCode, fresh && !!userCode);
      if (code.isConnected) show(); else requestAnimationFrame(show);
      current = {
        uri: typeof p.verificationUri === 'string' ? p.verificationUri : '', expiresAt: typeof p.expiresAt === 'number' ? p.expiresAt : 0,
        code: userCode, startAt: fresh ? Date.now() : current.startAt,
      };
      uri.textContent = current.uri;
      copy.hidden = !current.uri;
      openBtn.hidden = !(p.openable === true && typeof p.verificationUriComplete === 'string' && p.verificationUriComplete);
      fp.textContent = s.deviceFingerprint || '—';
      tick();
    },
    dispose() { clearInterval(timer); },
  };
}

function pairingErrorView() {
  const msg = h('span');
  const node = h('div', { class: 'sync-phase' },
    h('div', { class: 'note err' }, icon('error'), msg),
    actions(syncButton('重新配對', 'pair', startPairing, 'primary'), syncButton('取消', 'cancelPair', cancelPairing)));
  return {
    node,
    update(remote) { msg.textContent = (remote.sync && remote.sync.pairing && remote.sync.pairing.error) || '配對失敗'; },
  };
}

/**
 * 已配對（confirm＝伺服器身分換了、要本機確認時在最上面多一塊確認區）：狀態、待送、最近同步、時間差、伺服器與兩組指紋；
 * 暫停／繼續（看 local.paused）、立即重試、解除配對（先確認）；之後是其他電腦的遠端操作、這台改名與已配對的電腦
 */
function pairedView(withConfirm) {
  const pill = h('span', { class: 'pill', dataset: { field: 'state' } });
  const detail = h('div', { class: 'set-desc', dataset: { field: 'detail' } });
  const pending = h('span', { dataset: { field: 'pending' } });
  const lastSync = h('span', { dataset: { field: 'lastSyncAt' } });
  const skew = h('div', { class: 'note warn', dataset: { field: 'skew' } }, icon('warning'), h('span'));
  const server = h('span', { class: 'mono', dataset: { field: 'server' } });
  const deviceFp = h('span', { class: 'sync-fp mono', dataset: { field: 'deviceFingerprint' } });
  const hubFp = h('span', { class: 'sync-fp mono', dataset: { field: 'hubFingerprint' } });
  let paused = false;
  const pause = syncButton('暫停同步', 'pause', () => post({ cmd: 'syncPause', paused: !paused }));
  const retry = syncButton('立即重試', 'retry', () => post({ cmd: 'syncRetry' }));
  const unpair = syncButton('解除配對…', 'unpair', () => confirmUnpair());

  const status = h('div', { class: 'set-row' },
    h('div', { class: 'set-text' }, h('div', { class: 'set-label', text: '狀態' }), detail),
    h('div', { class: 'set-ctl' }, pill));
  const confirm = withConfirm ? confirmBox() : null;
  const policy = policyBlock();
  const rename = renameBlock();
  const devices = devicesBlock();
  // 〔遠端〕回補的進度（SyncStatus.backfill；暫停只在系統匣顯示）、遠端操作紀錄與回收區的入口（WP4.6 D28）
  const backfill = h('span', { dataset: { field: 'backfill' } });
  const backfillRow = row('回補到伺服器', '配對之前的對話依序上傳到伺服器，網頁與其他電腦才看得到。', backfill);
  const logs = h('div', { class: 'sync-group', dataset: { group: 'remoteLog' } },
    groupHead('遠端操作紀錄與回收區'),
    h('p', { class: 'set-desc', text: '其他電腦與網頁在這台做過的操作保留 180 天；遠端刪除的對話在這台的回收區保留 30 天，可以還原。' }),
    actions(syncButton('遠端操作紀錄…', 'auditLog', openAuditLog), syncButton('回收區…', 'trash', openTrash)));
  const node = h('div', { class: 'sync-phase' },
    confirm ? confirm.node : null,
    skew,
    card(
      status,
      row('待送', '離線或暫停時累積，連上後依序送出。', pending),
      row('最近同步', null, lastSync),
      backfillRow,
      row('伺服器', null, server),
      row('這台的指紋', null, deviceFp),
      row('伺服器身分指紋', '和 Hub 主機上 MyriagentHub.exe admin status 顯示的相同。', hubFp)),
    actions(pause, retry, unpair),
    policy.node,
    logs,
    rename.node,
    devices.node);
  return {
    node,
    update(remote) {
      const s = remote.sync || {};
      const sv = stateView(s.state);
      pill.className = 'pill ' + sv.tone;
      pill.textContent = sv.label;
      detail.textContent = s.detail || '';
      detail.hidden = !s.detail;
      pending.textContent = (Number(s.pending) || 0) + ' 筆';
      lastSync.textContent = s.lastSyncAt ? fmtRelative(s.lastSyncAt) : '還沒有';
      const sk = skewText(s.clockSkewMs);
      skew.hidden = !sk;
      skew.lastElementChild.textContent = sk || '';
      server.textContent = s.server || '';
      deviceFp.textContent = s.deviceFingerprint || '—';
      hubFp.textContent = s.hubFingerprint || '—';
      // 暫停看這台的本機設定（D14）；舊的宿主沒有 local 時退而看狀態
      paused = s.local && typeof s.local.paused === 'boolean' ? s.local.paused : s.state === 'paused';
      pause.textContent = paused ? '繼續同步' : '暫停同步';
      pause.dataset.act = paused ? 'resume' : 'pause';
      const bt = backfillText(s.backfill);
      backfillRow.hidden = !bt;
      backfill.textContent = bt || '';
      if (confirm) confirm.update(s);
      policy.update(s.local, s);
      rename.update(remote);
      devices.update(remote);
    },
  };
}

/**
 * 伺服器身分換了（WPR SyncStatus.confirm）：請使用者到 Hub 主機的主控台比對。確認框打開時記下畫面上的指紋，
 * 按下確認時宿主的指紋還是它才送 syncConfirm（宿主只認當下的指紋）；變了就不送、請使用者重新比對
 */
function confirmBox() {
  const fp = h('div', { class: 'sync-code mono', dataset: { field: 'confirmFingerprint' } });
  const changed = h('div', { class: 'note err', hidden: true, dataset: { field: 'confirmChanged' } }, icon('error'), h('span', { text: '伺服器身分在確認期間又變了，請重新比對。' }));
  let shown = '';
  const trust = syncButton('指紋相同，信任新的伺服器身分', 'confirmHub', async () => {
    const remembered = shown;
    if (!remembered) return;
    changed.hidden = true;
    const r = await confirmDialog({
      title: '信任新的伺服器身分',
      message: '確定 Hub 主機上 MyriagentHub.exe admin status 顯示的伺服器身分指紋是 ' + remembered + '？',
      detail: '指紋不同時請按取消、改用「不相同，解除配對」。',
      confirmLabel: '信任',
    });
    if (!r.ok) return;
    const now = currentRemote();
    const latest = now && now.sync && now.sync.confirm && now.sync.confirm.fingerprint;
    if (latest === remembered) post({ cmd: 'syncConfirm', kind: 'hubIdentity', fingerprint: remembered });
    else { changed.hidden = false; refreshSettings(); }
  }, 'primary');
  const mismatch = syncButton('不相同，解除配對', 'unpairMismatch', () => confirmUnpair('伺服器身分不相同：解除配對'), 'danger-solid');
  const node = h('div', { class: 'sync-box warn' },
    h('div', { class: 'set-label', text: '伺服器身分變了，同步已停止' }),
    h('p', { class: 'set-desc', text: '新的伺服器身分指紋：' }),
    fp,
    h('p', { class: 'set-desc', text: '請到 Hub 主機執行 MyriagentHub.exe admin status 比對伺服器身分指紋；不要拿網頁上的比，伺服器被冒充時網頁也是對方的。' }),
    changed,
    actions(trust, mismatch));
  return {
    node,
    update(s) {
      shown = (s.confirm && typeof s.confirm.fingerprint === 'string' && s.confirm.fingerprint) || '';
      fp.textContent = shown || '—';
    },
  };
}

/**
 * 其他電腦的遠端操作層級（WPR syncSetPolicy 的 remoteControlLevel；設計 8.10 的裝置本機政策）。
 * 用語和 WP3.5 電腦頁（remoterules.js 的 POLICY_TEXT）相同：不接受／只能查看／可以操作
 */
const SYNC_LEVELS = [
  { value: 'Off', label: '不接受', title: '只同步，不接受其他電腦的任何操作（連停止也不收）' },
  // 〔遠端〕「只能查看」不收任何遠端指令，連停止也不收（WP4.4 的閘門 5e、RemotePolicyRules）
  { value: 'View', label: '只能查看', title: '其他電腦可以打開、看這台的對話，但不能做任何操作（連停止也不收）' },
  { value: 'Control', label: '可以操作', title: '其他電腦可以送訊息、核准、回答與改這台對話的設定' },
];
const SYNC_LEVEL_LABEL = Object.freeze(Object.fromEntries(SYNC_LEVELS.map((o) => [o.value, o.label])));

const groupHead = (title) => h('h4', { class: 'set-h', text: title });
const str = (v) => (typeof v === 'string' ? v : '');

/**
 * 其他電腦的遠端操作（只能在這台改）：目前值一律照 sync.local（D14），不等伺服器的 DeviceView 回音；點了就送 syncSetPolicy，
 * 畫面等宿主重送 sync 再換成新值。改成「可以操作」與打開「允許遠端使用完全自動」之前先確認（WP4.6 D30；不要求輸入），
 * 取消就不送、分段選擇或開關回到本機設定的值；改成其他層級、關閉完全自動直接送。
 * 〔遠端〕「遠端操作時在這台通知」（local.notifyRemoteActions，有引擎時才有這個鍵）；sync.remoteSupported 不是 true 時說明這一版還不會執行。
 * 「允許其他電腦的代理在這台執行指令」（local.allowRemoteExec，接得住遠端執行指令的宿主才有這個鍵；sync-protocol 14.11）：打開之前先確認，
 * 層級不是「可以操作」時停用（那時一律不收）
 */
function policyBlock() {
  let local = null;
  let confirming = false;
  let execConfirming = false;
  let levelConfirming = false;
  const currentLevel = () => (local && SYNC_LEVEL_LABEL[local.remoteControlLevel] ? local.remoteControlLevel : 'Off');
  const chooseLevel = async (v) => {
    if (v !== 'Control' || currentLevel() === 'Control') { post({ cmd: 'syncSetPolicy', remoteControlLevel: v }); return; }
    levelConfirming = true;
    const r = await confirmDialog({
      title: '允許其他電腦操作這台',
      message: '知道擁有者密碼、或拿到瀏覽器登入狀態的人，都能在這台送訊息、核准與回答。',
      detail: '完全自動另外要打開「允許遠端使用完全自動」。',
      confirmLabel: '允許操作',
      danger: true,
    });
    levelConfirming = false;
    if (r.ok) post({ cmd: 'syncSetPolicy', remoteControlLevel: 'Control' });
    else level.setValue(currentLevel());
  };
  const level = segmented(SYNC_LEVELS, 'Off', (v) => { chooseLevel(v); }, 'sync-level');
  level.dataset.field = 'remoteControlLevel';
  level.setAttribute('aria-label', '其他電腦的遠端操作');
  const full = switchControl({
    checked: false,
    label: '允許遠端使用完全自動',
    onChange: async (on) => {
      if (!on) { post({ cmd: 'syncSetPolicy', allowRemoteFullAccess: false }); return; }
      confirming = true;
      const r = await confirmDialog({
        title: '允許遠端使用完全自動',
        message: '打開之後，其他電腦可以要求這台的對話以完全自動執行：代理不再逐步詢問，就直接執行指令、修改檔案。',
        detail: '只在信任所有能登入你的 Hub 的人時打開。',
        confirmLabel: '允許',
        danger: true,
      });
      confirming = false;
      if (r.ok) post({ cmd: 'syncSetPolicy', allowRemoteFullAccess: true });
      else full.input.checked = !!(local && local.allowRemoteFullAccess === true);
    },
  });
  full.input.dataset.field = 'allowRemoteFullAccess';
  const exec = switchControl({
    checked: false,
    label: '允許其他電腦的代理在這台執行指令',
    onChange: async (on) => {
      if (!on) { post({ cmd: 'syncSetPolicy', allowRemoteExec: false }); return; }
      execConfirming = true;
      const r = await confirmDialog({
        title: '允許其他電腦的代理在這台執行指令',
        message: '打開之後，其他已配對電腦的對話裡的代理可以用 myriagent-remote 在這台執行主控台指令（PowerShell、cmd），以這台目前登入的使用者、一般權限執行，輸出會傳回去。',
        detail: '關機、格式化、改系統設定、要系統管理員權限這類高風險指令會先在這台跳出確認，要有人在這台按同意才執行；需要系統管理員的操作另外會跳出 Windows 的 UAC。',
        confirmLabel: '允許',
        danger: true,
      });
      execConfirming = false;
      if (r.ok) post({ cmd: 'syncSetPolicy', allowRemoteExec: true });
      else exec.input.checked = !!(local && local.allowRemoteExec === true);
    },
  });
  exec.input.dataset.field = 'allowRemoteExec';
  const execRow = row('允許其他電腦的代理在這台執行指令',
    '其他電腦的對話裡的代理可以在這台執行主控台指令；高風險的指令要有人在這台按同意。要先把「允許的操作」設成「可以操作」。', exec);
  const notify = switchControl({ checked: true, label: '遠端操作時在這台通知', onChange: (on) => post({ cmd: 'syncSetPolicy', notifyRemoteActions: on }) });
  notify.input.dataset.field = 'notifyRemoteActions';
  const notifyRow = row('遠端操作時在這台通知', '其他電腦或網頁在這台送訊息、核准、回答或改設定時，在這台顯示通知。', notify);
  const paused = h('div', { class: 'note warn', hidden: true, dataset: { field: 'remotePaused' } }, icon('warning'),
    h('span', { text: '已從系統匣暫停遠端操作：其他電腦目前一律不能操作這台，從系統匣恢復後才照這裡的設定。' }));
  const unsupported = h('div', { class: 'note warn', hidden: true, dataset: { field: 'remoteUnsupported' } }, icon('warning'), h('span', { text: '這一版還不會執行遠端指令' }));
  const node = h('div', { class: 'sync-group', dataset: { group: 'policy' } },
    groupHead('其他電腦的遠端操作'),
    h('p', { class: 'set-desc', text: '只能在這台改，馬上生效。' }),
    unsupported,
    paused,
    card(
      row('允許的操作', '「不接受」只同步；「只能查看」可以看對話，但不能做任何操作，連停止也不行；「可以操作」可以送訊息、核准與回答。', level),
      row('允許遠端使用完全自動', '關閉時，其他電腦不能把這台的對話切到完全自動，也不能以完全自動開新對話。', full),
      execRow,
      notifyRow));
  return {
    node,
    update(l, s) {
      local = l && typeof l === 'object' ? l : null;
      // 舊的宿主沒有 local：看不到這台的設定就不顯示（DeviceView 的政策是伺服器的回音，系統匣暫停時也不是本機的值）
      node.hidden = !local;
      if (!local) return;
      if (!levelConfirming) level.setValue(currentLevel());
      if (!confirming) full.input.checked = local.allowRemoteFullAccess === true;
      // 遠端執行指令：宿主沒有這個鍵（舊版、還沒有同步引擎）時不顯示；層級不是「可以操作」時停用
      execRow.hidden = typeof local.allowRemoteExec !== 'boolean';
      if (!execConfirming) exec.input.checked = local.allowRemoteExec === true;
      exec.input.disabled = currentLevel() !== 'Control';
      paused.hidden = local.remotePaused !== true;
      // 通知開關：省略＝true；宿主沒有這個鍵（還沒有同步引擎、舊版）時不顯示
      notifyRow.hidden = typeof local.notifyRemoteActions !== 'boolean';
      notify.input.checked = local.notifyRemoteActions !== false;
      unsupported.hidden = !!(s && s.remoteSupported === true);
    },
  };
}

/**
 * 這台改名（WPR deviceRename）：欄位照已配對的電腦清單裡的這台；改過還沒存時不被宿主的更新蓋掉，存了之後等清單的名稱再變才換
 * （存的當下清單上的舊名稱不會蓋回來）。空白不送（宿主另以 PairingRules.CleanDeviceName 清理）
 */
function renameBlock() {
  let deviceId = null;
  let dirty = false;
  let seen;     // 清單裡這台最近的名稱
  let applied;  // 欄位最後一次照清單填入（或存的當下）的清單名稱
  const input = h('input', { type: 'text', class: 'input', maxlength: '60', spellcheck: 'false', 'aria-label': '這台的名稱', dataset: { field: 'renameInput' } });
  const save = () => {
    const name = input.value.trim();
    if (!name || !deviceId) return;
    post({ cmd: 'deviceRename', deviceId, name });
    input.value = name;
    applied = seen;
    dirty = false;
  };
  input.addEventListener('input', () => { dirty = true; });
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing) { e.preventDefault(); save(); } });
  const btn = syncButton('儲存', 'rename', save);
  const node = h('div', { class: 'sync-group', dataset: { group: 'rename' } },
    groupHead('這台'),
    card(row('這台的名稱', '其他電腦與網頁上顯示的名稱，最多 60 字；配對時的名稱不變。', h('div', { class: 'sync-rename' }, input, btn))));
  return {
    node,
    update(remote) {
      deviceId = str(remote.sync && remote.sync.deviceId) || str(remote.deviceId) || null;
      const self = (Array.isArray(remote.devices) ? remote.devices : []).find((d) => d && d.self === true);
      seen = self ? str(self.name) : '';
      input.placeholder = self ? '' : '連上伺服器後顯示目前的名稱';
      if (!dirty && seen !== applied) { input.value = seen; applied = seen; }
      btn.disabled = !deviceId;
    },
  };
}

/** 撤銷原因（設計 5.5.4、sync-protocol：retired＝退役、compromised＝疑似遭入侵；和 WP3.5 電腦頁的 remoterules.js 相同） */
const REVOKE_REASON = Object.freeze({ retired: '退役', compromised: '疑似遭入侵' });

const syncTag = (text, tone, title) => h('span', { class: 'tag' + (tone ? ' ' + tone : ''), title: title || null, text });

/** 已配對的電腦這一列（DeviceView）：名稱與標籤、上線或最後上線時間、版本、指紋；文字一律 textContent */
function fillDeviceRow(node, d) {
  const revoked = d.revoked && typeof d.revoked === 'object' ? d.revoked : null;
  const tags = [];
  if (d.self === true) tags.push(syncTag('這台', 'self'));
  if (revoked) tags.push(syncTag('已撤銷', 'bad'));
  else if (d.verified === false) tags.push(syncTag('未驗證', 'warn', '伺服器的裝置名冊裡沒有這台，或格式不合格'));
  else if (d.verified === true) tags.push(syncTag('已驗證', null, '在伺服器的裝置名冊裡'));
  let state;
  if (revoked) {
    const reason = REVOKE_REASON[revoked.reason];
    state = h('span', { class: 'sync-dev-seen', text: (revoked.at ? '撤銷於 ' + fmtRelative(revoked.at) : '已撤銷') + (reason ? '（' + reason + '）' : '') });
  }
  else if (d.online === true) state = h('span', { class: 'pill st-ready', text: '在線' });
  else if (d.reconnecting === true) state = h('span', { class: 'sync-dev-seen', dataset: { link: 'reconnecting' }, text: '重新連線中…' });
  else state = h('span', { class: 'sync-dev-seen', text: d.lastSeenAt ? '最後上線 ' + fmtRelative(d.lastSeenAt) : '還沒有上線紀錄' });
  const meta = [h('span', { text: str(d.appVersion) ? '版本 ' + str(d.appVersion) : '版本不明' })];
  if (str(d.fingerprint)) meta.push(h('span', null, '指紋 ', h('span', { class: 'mono', text: str(d.fingerprint) })));
  if (str(d.signedName) && str(d.signedName) !== str(d.name)) meta.push(h('span', { text: '配對時的名稱 ' + str(d.signedName) }));
  // 其他電腦的政策只供參考（那台回報的值）；這台的照上面的本機設定
  if (d.self !== true && !revoked && SYNC_LEVEL_LABEL[d.remoteControlLevel]) meta.push(h('span', { text: '遠端操作：' + SYNC_LEVEL_LABEL[d.remoteControlLevel] }));
  node.dataset.revoked = revoked ? 'true' : 'false';
  // 名稱前面的燈（fxrules.deviceLed）：在線綠、5 分鐘內上線過淡綠、更早的暗、撤銷的紅框
  node.dataset.led = deviceLed(d, Date.now());
  node.replaceChildren(
    h('div', { class: 'sync-dev-head' },
      h('span', { class: 'sync-dev-name', text: str(d.name) || str(d.signedName) || str(d.machineName) || '（沒有名稱）' }),
      ...tags,
      h('span', { class: 'sync-dev-state' }, state)),
    h('div', { class: 'sync-dev-meta' }, ...meta));
  return node;
}

/** 已配對的電腦（唯讀；撤銷與刪除在 Hub 網頁的裝置頁，WP3.5）：宿主已把撤銷的排最後，這裡再穩定地排一次 */
function devicesBlock() {
  const list = h('div', { class: 'sync-devs', role: 'list', dataset: { field: 'devices' } });
  const empty = h('p', { class: 'set-desc', text: '連上伺服器後顯示已配對的電腦。', dataset: { field: 'devicesEmpty' } });
  const node = h('div', { class: 'sync-group', dataset: { group: 'devices' } },
    groupHead('已配對的電腦'),
    h('p', { class: 'set-desc', text: '撤銷或刪除請到 Hub 網頁的裝置頁。' }),
    empty,
    list);
  return {
    node,
    update(remote) {
      const ids = new Set();
      const all = (Array.isArray(remote.devices) ? remote.devices : [])
        .filter((d) => d && typeof d === 'object' && str(d.id) && !ids.has(d.id) && ids.add(d.id));
      const ordered = all.filter((d) => !d.revoked).concat(all.filter((d) => d.revoked));
      empty.hidden = ordered.length > 0;
      list.hidden = ordered.length === 0;
      reconcile(list, ordered, (d) => d.id, (d) => fillDeviceRow(h('div', { class: 'sync-dev', role: 'listitem' }), d), fillDeviceRow);
    },
  };
}
