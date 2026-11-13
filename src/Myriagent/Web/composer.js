// 輸入區：自動長高、附件、排隊訊息、權限模式（Shift+Tab）、模型、推理強度、@ 檔案、/ 指令、貼上、↑↓ 帶入最近送出的訊息
// 〔遠端〕瀏覽器版的附加（迴紋針、貼上、拖放）走上傳（E4，hubapi.uploadFile）→ addUploaded；附件晶片由網頁自己記（伺服器的 Composer 沒有附件，D32）
import { h, iconButton, debounce, highlight, reconcile, extOf, fmtBytes, fmtReset, replayClass, setHidden, setAcc } from './util.js';
import { icon, FILE_ICON } from './icons.js';
import { post, postWithFiles, state, accountOf, EFFORT_LABEL, EFFORT_DESC, permissionTone, nextRequestId, isHub, isCollaborator, newId, peerId } from './store.js';
import { modelText, effortText } from './settingsrules.js';
import { openMenu, openDialog, confirmDialog, toast, spinner, hasLayer } from './ui.js';
import { codexShown } from './codexrules.js';
import { openCodexMenu, refreshCodexPanel } from './codexcontrols.js';
import { attachmentTile, planRow } from './items.js';
import { uploadFile } from './hubapi.js';
import {
  permissionChoices, sendRestorePlan, settleText, replicaWebUrl, isConversationId, isDraftId, chipsAfter, uploadBatch, uploadName, uploadResult,
  uploadsKey, storedUploads, UPLOAD_LIMIT_TEXT, controllable,
} from './liverules.js';
import { track } from './cmdtrack.js';
import { accountSuggestions, accountMention, mentionPlain } from './accountmentions.js';
import { contextShown, contextPercent, contextLabel, contextSeverity, contextMark, contextSummary, contextTitle } from './contextrules.js';
import { HISTORY_KEY, LEGACY_HISTORY_KEY, parseStore, historyOf, pushStore, moveStore, idleNav, historyMove } from './historyrules.js';
import { tokenCounter } from './tokenfx.js';
import { createDraftSync, noteSent } from './draftsync.js';
import { parseTurnTokens, gaugeFx, meterCells, infoWindows, rttLabel, tokenRate, pruneRateSamples, rateLabel, compactDrop, fmtTokK, byteRate, uploadReadout, smoothRate, delegateMeta } from './fxrules.js';
import { sessionTotals } from './remoterules.js';
import { MATRIX_GLYPHS, fmtShort, fmtTokens } from './tokenrules.js';
import { showsSending, failureOf, CONTINUE_TEXT, continueTextOf, continueTitle, enterSends } from './sendrules.js';
import { isHandheld } from './voicemsgrules.js';
import { calmMotion } from './theme.js';
import { createVoiceMessage } from './voicemsg.js';
import { LEDS, LED_LABEL, LED_HOLD_MS, LED_HOLD_CALM_MS, ledPulse, ledTitle, CPU_WAVE_STEP, CPU_WAVE_HEIGHT, CPU_WAVE_CANVAS, CPU_STALE_MS, CPU_SLOW_DRAW_MS, cpuOf, pushCpu, cpuWaveScale, cpuWavePoints, cpuColor, cpuSlideFrom, cpuSlideMs, cpuText, cpuTitle, memOf, memText } from './activityrules.js';
import { styleLabel, styleMenu, showStyle, agentLocked, defaultAgentDesc, steeredLabel, agentChipShown, styleChipShown, claudeMenuEntries, cacheResumeWarning, quotaWaitView, cacheCountdown, quotaWaitCells } from './claudeoptrules.js';

const MAX_PASTE = 30 * 1024 * 1024;
/** Chromium 把剪貼簿裡的點陣圖（截圖、Office 複製時附帶的 CF_DIB）包成這個檔名的 image/png */
const CLIPBOARD_IMAGE_NAME = 'image.png';
/** 與 chat.css 的 .cinput min-height 相同 */
const MIN_INPUT_HEIGHT = 46;
const ATTACH_TITLE = '附加檔案（圖片、影片、文件）';
/** 〔遠端〕同時上傳幾個檔 */
const UPLOAD_CONCURRENCY = 3;
/** 〔遠端〕往返讀數顯示多久（fx.css 的 .ci-rtt.fx-rtt 同一段時間淡出） */
const RTT_SHOW_MS = 2400;
/** 離開佇列（送出、取消）的那一列往上淡出多久才移除（fx.css 第八批的 .queue-row.fx-leave） */
const QUEUE_LEAVE_MS = 260;
/** 自動壓縮的提示顯示多久（fx.css 的 .ctxm.fx-compact 同一段時間） */
const COMPACT_NOTE_MS = 3600;
/** 〔遠端〕addUploaded 被拒或沒送出時晶片上的說明（原因看伺服器或傳輸層的提示） */
const NOT_ADDED_TEXT = '沒有加入輸入區，請重新附加';
/** 〔遠端〕上傳被使用者取消（晶片的 ×、關掉分頁、伺服器關閉對話） */
const UPLOAD_CANCELLED_TEXT = '已取消上傳';
/** 〔遠端〕晶片的 ×：失敗的拿掉、上傳中的取消 */
const DISMISS_TITLE = '拿掉這個沒有附加成功的檔案';
const CANCEL_UPLOAD_TITLE = '取消上傳';
/** 〔遠端〕「@」檔案搜尋不能用的說明（伺服器草稿、沒送出、被拒、等不到回覆） */
const SEARCH_DRAFT_TEXT = '送出第一則訊息後才能搜尋檔案';
const SEARCH_OFFLINE_TEXT = '目前沒有連線，連上之後才能搜尋檔案';
const SEARCH_REJECTED_TEXT = '現在不能搜尋這台電腦上的檔案';
const SEARCH_UNKNOWN_TEXT = '沒有收到那台電腦的回覆，請再輸入一次';
/** 〔遠端〕所有輸入區正在上傳或加進輸入區的晶片（伺服器換版時判斷有沒有未送出的東西，liveview.onInit） */
/** 主要指標是觸控（平板接了鍵盤、觸控板時會變，所以每次都問） */
const coarseMq = typeof window.matchMedia === 'function' ? window.matchMedia('(pointer: coarse)') : null;
/** 手機或平板（觸控操作）：Enter 一律換行、用送出鈕送出（sendrules.enterSends） */
const handheld = () => isHandheld({ coarsePointer: !!(coarseMq && coarseMq.matches), userAgent: navigator.userAgent, maxTouchPoints: navigator.maxTouchPoints });

const inFlight = new Set();
let localSeq = 0;
/** 開著的輸入區（瀏覽器版改了「繼續」鈕的指令時重算每一顆的說明） */
const live = new Set();

// ───── 「繼續」鈕的指令 ─────
// 桌面：宿主的設定 continueText（設定頁改、宿主重送 settings 時 app.js 對每個輸入區 updateSend）。
// 〔遠端〕瀏覽器版：伺服器的偏好只收四個漫遊鍵，改存在這個瀏覽器（同語音通知的開關，不跟其他裝置同步）
const CONTINUE_KEY = 'myriagent.continueText';

/** 〔瀏覽器版〕這個瀏覽器存的「繼續」指令；沒存過（或讀不到）是 null＝預設 */
export function continueLocal() {
  try { return localStorage.getItem(CONTINUE_KEY); } catch { return null; }
}

/** 〔瀏覽器版〕存「繼續」指令（空的或等於預設就清掉），開著的輸入區跟著換說明；回傳實際的那一句 */
export function setContinueLocal(text) {
  const t = continueTextOf(text);
  try {
    if (t === CONTINUE_TEXT) localStorage.removeItem(CONTINUE_KEY);
    else localStorage.setItem(CONTINUE_KEY, t);
  } catch { /* 無痕模式等存不了：這次執行照樣用預設 */ }
  for (const c of live) c.updateSend();
  return t;
}

// ───── 〔桌面遠端操作〕宿主代傳的上傳（web-protocol 的 peerUpload，sync-protocol 14.7） ─────
// 桌面對可以操作的副本附加檔案：postWithFiles 送 peerUpload {uploadId, conversationId}（WebView2 給本機路徑），宿主以裝置的存取權杖上傳到伺服器、
// 回 peerUpload {uploadId, status, json}；剪貼簿的圖片沒有路徑（宿主回 no_path），改帶 dataUrl 重送一次（≤30 MB，同本機的貼上）。
// 回傳值的形狀同 hubapi.uploadFile（{ ok, status（0＝取消或連不上）, json }），之後照瀏覽器版送 addUploaded
const hostUploads = new Map();

function hostUpload(uploadId, file, conversationId, signal) {
  return new Promise((resolve) => {
    const entry = { resolve, file, conversationId, triedDataUrl: false };
    hostUploads.set(uploadId, entry);
    if (signal) {
      if (signal.aborted) { finishHostUpload(uploadId, 0, null); return; }
      signal.addEventListener('abort', () => { post({ cmd: 'peerUploadCancel', uploadId }); finishHostUpload(uploadId, 0, null); }, { once: true });
    }
    if (!postWithFiles({ cmd: 'peerUpload', uploadId, conversationId }, [file])) sendHostDataUrl(uploadId, entry);
  });
}

function sendHostDataUrl(uploadId, entry) {
  entry.triedDataUrl = true;
  if (!entry.file || entry.file.size > MAX_PASTE) { finishHostUpload(uploadId, 413, { error: 'too_large' }); return; }
  const r = new FileReader();
  r.onload = () => post({ cmd: 'peerUpload', uploadId, conversationId: entry.conversationId, dataUrl: r.result, name: entry.file.name || '' });
  r.onerror = () => finishHostUpload(uploadId, 0, null);
  r.readAsDataURL(entry.file);
}

function finishHostUpload(uploadId, status, json) {
  const e = hostUploads.get(uploadId);
  if (!e) return;
  hostUploads.delete(uploadId);
  const st = typeof status === 'number' ? status : 0;
  e.resolve({ ok: st >= 200 && st < 300, status: st, json });
}

/** 宿主的 peerUpload 回覆（app.js 轉來） */
export function onPeerUpload(m) {
  if (!m || typeof m.uploadId !== 'string') return;
  const e = hostUploads.get(m.uploadId);
  if (!e) return;
  let json = null;
  try {
    json = typeof m.json === 'string' && m.json ? JSON.parse(m.json) : null;
  } catch {
    json = null;
  }
  if (m.status === 400 && json && json.error === 'no_path' && !e.triedDataUrl) { sendHostDataUrl(m.uploadId, e); return; }
  finishHostUpload(m.uploadId, m.status, json);
}

// ───── 輸入紀錄（localStorage，每個對話一份；只是方便，讀寫失敗一律當成沒有） ─────
function readStore() {
  try {
    return parseStore(localStorage.getItem(HISTORY_KEY));
  } catch {
    return [];
  }
}

function writeStore(before, after) {
  if (after === before) return;
  try {
    localStorage.removeItem(LEGACY_HISTORY_KEY); // 舊版所有對話共用的一份（分不出是哪個對話的）
    localStorage.setItem(HISTORY_KEY, JSON.stringify(after));
  } catch { /* 儲存不能用或額度滿了：這次不記 */ }
}

/** 記下在這個對話送出的這一則（每次重新讀：別的視窗或分頁也在記） */
function rememberInput(conversationId, text) {
  const before = readStore();
  writeStore(before, pushStore(before, conversationId, text));
}

/** 〔遠端〕草稿換成正式對話（workspace.replaceDraft）：草稿裡送出的紀錄搬到新的對話 */
export function moveInputHistory(from, to) {
  const before = readStore();
  writeStore(before, moveStore(before, from, to));
}

/** 〔遠端〕正在上傳或加進輸入區的檔案數（所有對話） */
export function uploadsInProgress() {
  return inFlight.size;
}

export class Composer {
  constructor(conv) {
    this.conv = conv;
    this.state = { attachments: [], busy: false, busyText: null, queue: [] };
    // 〔遠端〕網頁上傳的附件晶片：{ id, uploadId, file, name, size, state: uploading|adding|attached|error, loaded, total, error, entry }
    this.uploads = [];
    this.destroyed = false;
    this.todoOpen = false;
    this.sugg = null; // { type: 'file'|'cmd', start, query, items, sel, loading }
    this.dismissedAt = -1;
    this.searchReq = null;
    this.lastQuery = null;
    this.hist = idleNav(); // ↑↓ 翻輸入紀錄的位置（historyrules.js）
    this.build();
  }

  build() {
    // 待辦面板與佇列的節點只建一次、之後只改文字：執行中 header／項目每 50ms 更新一次，
    // 重建按鈕會讓按下到放開之間的點擊被吞掉（Chromium 不對已移除的節點派送 click），展開的清單也會被捲回頂端
    // 完成數與總數用側欄同款的計數器（tokenfx.js）：數字變了先閃過亂碼再鎖定
    // 只在這個對話顯示中時解碼（背景分頁直接換成新值，tokenfx.js）
    this.todoDone = tokenCounter(String, () => this.conv.visible);
    this.todoTotal = tokenCounter(String, () => this.conv.visible);
    this.todoCount = h('span', { class: 'todo-count' }, this.todoDone.num, '／', this.todoTotal.num);
    this.todoCur = h('span', { class: 'todo-cur' });
    this.todoChev = syncIcon(null, 'chevUp', 'todo-chev');
    this.todoHead = h('button', { type: 'button', class: 'todo-head', 'aria-expanded': 'false' }, icon('checklist', 'todo-ic'), this.todoCount, this.todoCur, this.todoChev);
    this.todoHead.addEventListener('click', () => { this.todoOpen = !this.todoOpen; this.renderTodo(this.todoPlan, this.todoRunning); });
    this.todoList = h('ol', { class: 'plan-list todo-list', hidden: true });
    this.todo = h('div', { class: 'todo', hidden: true }, this.todoHead, this.todoList);
    this.queueList = h('div', { class: 'queue-list' });
    this.queueIc = syncIcon(null, 'clock');
    this.queueSum = h('span', { class: 'queue-sum' });
    this.queueChev = syncIcon(null, 'chevDown', 'queue-chev');
    this.queueHead = h('button', { type: 'button', class: 'queue-head' }, this.queueIc, this.queueSum, this.queueChev);
    this.queueHead.addEventListener('click', () => { this.queueOpen = !this.queueIsOpen(); this.renderQueue(); });
    this.queueOpen = null; // null＝自動：窗格夠高或只有一則時展開
    // 〔遠端〕伺服器只帶佇列的前幾則（queueMore＝還有幾則沒帶到）：只顯示數量，沒有取消鈕
    this.queueMoreEl = h('div', { class: 'queue-more', hidden: true, dataset: { field: 'queueMore' } });
    this.queueEl = h('div', { class: 'queue', hidden: true }, this.queueHead, this.queueList, this.queueMoreEl);
    // 已插進這一輪的那則（composer.steered）：CLI 還沒讀走前可以取消
    this.steerText = h('span', { class: 'steer-sum' });
    this.steerCancelBtn = h('button', { type: 'button', class: 'btn sm steer-cancel', text: '取消插入', title: '代理還沒讀到這則時撤回，文字放回輸入框' });
    this.steerCancelBtn.addEventListener('click', () => {
      const s = this.state.steered;
      if (!s) return;
      this.state.steered = null;
      this.renderSteered();
      post({ cmd: 'cancelSteer', conversationId: this.conv.id, id: s.id });
    });
    // 自己的類別名（不用 .queue／.queue-head：排在佇列前面，會被找佇列的選擇器先抓到）
    this.steerEl = h('div', { class: 'steered', hidden: true, dataset: { field: 'steered' } },
      h('div', { class: 'steer-head' }, icon('insert', 'steer-ic'), this.steerText, this.steerCancelBtn));
    this.chips = h('div', { class: 'atts in-composer', hidden: true });
    // 〔遠端〕網頁上傳的晶片（上傳中、加入中、已附加、失敗）與迴紋針開的檔案選擇（隱藏的 input）
    this.upChips = h('div', { class: 'atts in-composer up-chips', hidden: true, dataset: { field: 'uploads' } });
    this.fileInput = h('input', { type: 'file', multiple: true, hidden: true, tabIndex: -1, 'aria-hidden': 'true', dataset: { field: 'attachInput' } });
    this.fileInput.addEventListener('change', () => {
      const files = Array.from(this.fileInput.files || []);
      this.fileInput.value = '';
      if (files.length) this.addFiles(files);
    });
    this.busyEl = h('div', { class: 'cbusy', hidden: true }, spinner(), h('span', { class: 'cbusy-text' }));
    this.ta = h('textarea', { class: 'cinput', rows: 1, spellcheck: 'false', 'aria-label': '訊息' });
    this.attachBtn = iconButton('paperclip', ATTACH_TITLE, () => this.attach());
    this.attachBtn.dataset.field = 'attach';
    this.atBtn = iconButton('at', '提及 AI 帳戶或檔案（@）', () => this.insertAt(), 'at');
    // 權限、模型、推理強度在 header 到達後才顯示（renderToolbar 決定）：對話還在載入時沒有東西可選
    this.permBtn = h('button', { type: 'button', class: 'perm', 'aria-haspopup': 'menu', title: '權限模式（Shift+Tab 切換）', hidden: true },
      h('span', { class: 'perm-dot' }), h('span', { class: 'perm-label' }), icon('chevDown', 'perm-chev'));
    this.permBtn.addEventListener('click', () => this.openPermissionMenu());
    this.modelBtn = h('button', { type: 'button', class: 'pick', 'aria-haspopup': 'menu', title: '模型', hidden: true }, h('span', { class: 'pick-label' }), icon('chevDown', 'pick-chev'));
    this.modelBtn.addEventListener('click', () => this.openModelMenu());
    this.codexBtn = h('button', { type: 'button', class: 'pick codex-tools', 'aria-haspopup': 'menu', title: 'Codex 對話工具', hidden: true }, h('span', { class: 'pick-label', text: 'Codex 工具' }), icon('chevDown', 'pick-chev'));
    this.codexBtn.addEventListener('click', () => openCodexMenu(this, this.codexBtn));
    this.effortBtn = h('button', { type: 'button', class: 'pick', 'aria-haspopup': 'menu', title: '推理強度', hidden: true }, h('span', { class: 'pick-pre', text: '推理' }), h('span', { class: 'pick-label' }), icon('chevDown', 'pick-chev'));
    this.effortBtn.addEventListener('click', () => this.openEffortMenu());
    this.agentBtn = h('button', { type: 'button', class: 'pick', 'aria-haspopup': 'menu', title: '自訂代理', hidden: true, dataset: { field: 'agent' } }, h('span', { class: 'pick-pre', text: '代理' }), h('span', { class: 'pick-label' }), icon('chevDown', 'pick-chev'));
    this.agentBtn.addEventListener('click', () => this.openAgentMenu());
    // Claude 的輸出風格（Header.outputStyle／outputStyles）：換了立即套用到活著的程序
    this.styleBtn = h('button', { type: 'button', class: 'pick', 'aria-haspopup': 'menu', title: '輸出風格', hidden: true, dataset: { field: 'outputStyle' } }, h('span', { class: 'pick-pre', text: '風格' }), h('span', { class: 'pick-label' }), icon('chevDown', 'pick-chev'));
    this.styleBtn.addEventListener('click', () => this.openStyleMenu());
    this.stopBtn = h('button', { type: 'button', class: 'cbtn stop', title: '停止這一輪（Esc）', 'aria-label': '停止', hidden: true }, icon('stop'));
    this.stopBtn.addEventListener('click', () => this.stop());
    this.sendBtn = h('button', { type: 'button', class: 'cbtn send', 'aria-label': '送出' }, icon('send'));
    this.sendBtn.addEventListener('click', () => this.send());
    // 上下文用量（Header.context）：送出鈕左邊的小圓環＋百分比，滑過看明細；宿主沒給就藏起來
    // 外圈（第九批）：Claude 的提示快取還剩多久（claudeoptrules.cacheCountdown）。viewBox 外擴 2、CSS 以負邊距抵掉，圓環本身的大小與版面不變
    this.ctxRing = svgEl('svg', { viewBox: '-2 -2 20 20', class: 'ctxm-ring', 'aria-hidden': 'true' });
    this.ctxCache = svgEl('circle', { cx: 8, cy: 8, r: 9.2, class: 'ctxm-cache', pathLength: 100, transform: 'rotate(-90 8 8)' });
    this.ctxArc = svgEl('circle', { cx: 8, cy: 8, r: 6, class: 'ctxm-arc', pathLength: 100, transform: 'rotate(-90 8 8)' });
    this.ctxTick = svgEl('circle', { cx: 8, cy: 8, r: 6, class: 'ctxm-mark', pathLength: 100, transform: 'rotate(-90 8 8)' });
    this.ctxRing.append(this.ctxCache, svgEl('circle', { cx: 8, cy: 8, r: 6, class: 'ctxm-track' }), this.ctxArc, this.ctxTick);
    this.ctxNum = h('span', { class: 'ctxm-n' });
    // 自動壓縮上下文的那一下：圓環上方浮出「已壓縮 412K → 96K」（壓縮前後的真實用量，fxrules.compactDrop）
    this.ctxNote = h('span', { class: 'ctxm-note', hidden: true, role: 'status' });
    this.ctxEl = h('span', { class: 'ctxm', role: 'img', hidden: true, dataset: { field: 'context' } }, this.ctxRing, this.ctxNum, this.ctxNote);
    // 這一輪的輸出速度（輸入框下方 token 數的後面；fxrules.tokenRate：最近 10 秒的輸出增量）：只在一輪進行中顯示，每秒更新（tickRate）
    this.rateEl = h('span', { class: 'ci-rate', hidden: true, dataset: { field: 'tokenRate' } }, h('i', { 'aria-hidden': 'true' }), h('span', { class: 'ci-rate-n' }));
    this.rateSamples = [];
    // 這個對話的 CPU（token 數與輸出速度的後面，2026-10-26 使用者要求）：程序樹的處理器使用率畫成依負載變色的波形＋百分比。
    // 只有桌面這台的本機對話、宿主量了程序樹時（執行中或有背景工作）才有；canvas 在每次 activity 時畫一格（drawCpu），兩格之間由合成器平移，
    // 窗口（.ci-cpu-win）裁掉 canvas 左右多出來的各一格
    this.cpuCanvas = h('canvas', { class: 'ci-cpu-wave', 'aria-hidden': 'true' });
    this.cpuAnim = null;
    this.cpuNum = h('span', { class: 'ci-cpu-n' });
    // 記憶體（2026-10-26 使用者要求）：和 CPU 同一組程序的私人工作集加總，寫在百分比後面；跟著 CPU 一起畫（drawCpu）
    this.memNum = h('span', { class: 'ci-mem-n' });
    // 標籤寫 MEM（2026-10-26 使用者要求，省空間）；無障礙標籤與滑過的說明照樣寫「記憶體」
    this.memEl = h('span', { class: 'ci-mem', hidden: true }, h('span', { class: 'ci-k', text: 'MEM' }), this.memNum);
    this.cpuMem = null;
    this.cpuEl = h('span', { class: 'ci-cpu', role: 'img', dataset: { field: 'cpu' } },
      h('span', { class: 'ci-k', text: 'CPU' }), h('span', { class: 'ci-cpu-win' }, this.cpuCanvas), this.cpuNum, this.memEl);
    this.cpuEl.addEventListener('mouseenter', () => { this.cpuEl.title = cpuTitle(this.cpuHist, this.cpuMem); });
    this.cpuHist = [];
    this.cpuScale = 0;
    this.cpuShown = false;
    this.cpuDrawAt = 0;
    // 語音訊息（voicemsg.js）：錄音交給對話所在的電腦，在那台轉成逐字稿、連錄音檔交給代理（代理先用文字複誦再照做）；
    // 手機與平板的瀏覽器版改用系統的語音辨識，說的話直接打進輸入框
    this.voice = createVoiceMessage(this);
    // 「繼續」：一鍵送出設定的那一句（預設 sendrules.CONTINUE_TEXT：問代理進度、還沒做完就接著做）；輸入框裡打到一半的字不動
    this.contBtn = iconButton('continue', continueTitle({ text: this.continueText() }), () => this.sendContinue(), 'cont');
    this.contBtn.dataset.field = 'continue';
    this.bar = h('div', { class: 'cbar' }, this.attachBtn, this.atBtn, this.permBtn, this.modelBtn, this.effortBtn, this.agentBtn, this.styleBtn, this.codexBtn, h('span', { class: 'grow' }), this.ctxEl, this.contBtn, this.voice.el, this.stopBtn, this.sendBtn);
    this.suggest = h('div', { class: 'suggest', hidden: true, role: 'listbox' });
    this.box = h('div', { class: 'cbox' }, this.suggest, this.chips, this.upChips, this.busyEl, this.ta, this.bar, this.fileInput);
    this.info = h('div', { class: 'cinfo' });
    // 輸入框下方那一行最前面的燈號（activityrules.js）：運行跟著執行中；資料流、網路、硬碟只在宿主量到真的數字時亮（activity）。
    // 節點只建一次（renderInfo 每次重排這一行時放回去），亮與暗由 applyActivity 控制
    this.leds = {};
    this.ledLast = {};
    for (const k of LEDS) this.leds[k] = h('span', { class: 'ci-led', dataset: { led: k } }, h('i'), h('span', { text: LED_LABEL[k] }));
    // 〔遠端〕往返讀數：送出、回答卡片時，從送出到那台電腦回報結果花了多久（cmdtrack 實際量到的），亮一下就收起（showRtt）
    this.rttEl = h('span', { class: 'ci-rtt', hidden: true, dataset: { field: 'rtt' } }, h('i', { 'aria-hidden': 'true' }), h('span', { class: 'ci-rtt-n' }));
    this.ledBox = h('span', { class: 'ci-leds', role: 'group', 'aria-label': '燈號' }, ...LEDS.map((k) => this.leds[k]), this.rttEl);
    // 〔遠端〕送出的結果不明（輸入區上方）與送出的閘門說明（輸入框下方），文字一律 textContent
    this.settleEl = h('div', { class: 'csettle', hidden: true, role: 'status', dataset: { field: 'sendSettle' } }, icon('info'), h('span', { class: 'csettle-text' }));
    this.gateEl = h('div', { class: 'cgate', hidden: true, dataset: { field: 'sendGate' } }, icon('info'), h('span', { class: 'cgate-text' }));
    // 〔同步〕桌面上其他電腦的副本（D22）：輸入框與按鈕整個拿掉，換成唯讀說明＋「在網頁版開啟」（伺服器的 /app/ 彈出視窗）
    this.webBtn = h('button', { type: 'button', class: 'btn sm', hidden: true, dataset: { field: 'openInWeb' } }, icon('globe'), h('span', { text: '在網頁版開啟' }));
    this.webBtn.addEventListener('click', () => this.openInWeb());
    this.roEl = h('div', { class: 'creadonly', hidden: true, dataset: { field: 'replicaReadOnly' } }, icon('eye'), h('span', { class: 'creadonly-text' }), this.webBtn);
    // 執行列的輸出面板插在佇列前面（conversation.js 的 queueEl.before）：插入的那一行排在佇列後面，輸出面板才是最上面一個
    // 等額度重置後自動接著做（Header.quotaWait）：取消（quotaWaitCancel）
    this.quotaText = h('span', { class: 'steer-sum' });
    this.quotaCancelBtn = h('button', { type: 'button', class: 'btn sm steer-cancel', text: '取消等待' });
    this.quotaCancelBtn.addEventListener('click', () => post({ cmd: 'quotaWaitCancel', conversationId: this.conv.id }));
    // 分格倒數（第九批）：12 格，剩下的時間佔開始等的時候的比例（claudeoptrules.quotaWaitCells），每分鐘更新（tickQuota）
    this.quotaCells = h('span', { class: 'qw-cells', 'aria-hidden': 'true' }, ...Array.from({ length: 12 }, () => h('i')));
    this.quotaUntil = null;
    this.quotaSpan = 0;
    this.quotaSig = '';
    this.quotaEl = h('div', { class: 'steered quotawait', hidden: true, dataset: { field: 'quotaWait' } },
      h('div', { class: 'steer-head' }, icon('clock', 'steer-ic'), this.quotaText, this.quotaCells, this.quotaCancelBtn));
    // 輸入框草稿的同步（draftsync.js）：別處有不同的草稿、這裡又正在打時，在輸入框上方提示「換成它／保留這裡的」
    this.draftSync = createDraftSync(this);
    this.el = h('div', { class: 'dock' }, this.queueEl, this.steerEl, this.quotaEl, this.todo, this.draftSync.el, this.settleEl, this.box, this.roEl, this.gateEl, this.info);

    this.ta.addEventListener('input', () => { this.draftSync.input(); this.autosize(); this.updateSend(); this.checkTrigger(); });
    this.ta.addEventListener('keydown', (e) => this.onKeyDown(e));
    this.ta.addEventListener('keyup', (e) => { if (e.key === 'ArrowLeft' || e.key === 'ArrowRight' || e.key === 'Home' || e.key === 'End') this.checkTrigger(); });
    this.ta.addEventListener('click', () => this.checkTrigger());
    this.ta.addEventListener('blur', () => setTimeout(() => { if (!this.el.contains(document.activeElement)) this.closeSuggest(); }, 120));
    this.ta.addEventListener('paste', (e) => this.onPaste(e));
    this.suggest.addEventListener('pointerdown', (e) => e.preventDefault()); // 不讓輸入框失焦
    this.doSearch = debounce((q) => {
      const req = nextRequestId('s');
      this.searchReq = req;
      const cid = post({ cmd: 'fileSearch', requestId: req, conversationId: this.conv.id, query: q });
      // 〔遠端〕瀏覽器版：沒送出、被拒（政策、電腦離線…）或結果不明時不會有 fileSearch 回覆：不再轉圈、說明原因（合併前審查 P2）
      if (!isHub) return;
      if (cid === null) this.searchFailed(req, SEARCH_OFFLINE_TEXT);
      else if (typeof cid === 'string') {
        track(cid, { onSettle: (st) => { if (st !== 'done') this.searchFailed(req, st === 'unknown' ? SEARCH_UNKNOWN_TEXT : SEARCH_REJECTED_TEXT); } });
      }
    }, 120);
    this.conv.root.addEventListener('keydown', (e) => this.onPaneKeyDown(e));
    // 窗格高度、輸入框寬度或輸入區其他部分（佇列、待辦、附件）的高度改變時重算輸入框高度：
    // 分割窗格、側欄、檔案瀏覽器、視窗變矮、分頁從隱藏變成顯示都會碰到。輸入框自己長高不會再觸發；
    // 放到下一個影格做，避免在觀察回呼裡改到被觀察的元素
    this.sizeKey = '';
    this.sizeRaf = 0;
    this.sizeObs = new ResizeObserver(() => {
      const key = [this.conv.root.clientHeight, this.ta.clientWidth, this.el.offsetHeight - this.ta.offsetHeight].join('x');
      if (key === this.sizeKey) return;
      this.sizeKey = key;
      if (!this.sizeRaf) this.sizeRaf = requestAnimationFrame(() => { this.sizeRaf = 0; this.autosize(); });
    });
    this.sizeObs.observe(this.conv.root);
    this.sizeObs.observe(this.el);
    live.add(this);
    this.restoreUploads();
    this.updatePlaceholder();
    this.updateSend();
  }

  destroy() {
    // 分頁關了：還在上傳的中止（不在背景一路傳完、佔掉伺服器的待處理額度，合併前審查 PERF-2），也不再加進輸入區
    // （已附加的留在 sessionStorage，再開這個對話時還原）
    this.cancelUploads(this.uploads);
    this.voice.cancel(); // 分頁關了：錄到一半的不送、關掉麥克風
    clearTimeout(this.rttTimer);
    clearTimeout(this.ctxNoteTimer);
    this.draftSync.flush(); // 分頁關了：還沒送的草稿馬上送
    this.draftSync.destroy();
    this.destroyed = true;
    live.delete(this);
    for (const c of this.uploads) inFlight.delete(c);
    this.sizeObs.disconnect();
    if (this.sizeRaf) cancelAnimationFrame(this.sizeRaf);
    this.closeSuggest();
  }

  get header() { return this.conv.header; }

  focus() {
    try { this.ta.focus({ preventScroll: true }); } catch { /* 忽略 */ }
  }

  // ───── 宿主資料 ─────
  setHeader(hd) {
    this.renderQuotaWait(hd);
    this.renderToolbar();
    this.renderInfo();
    this.updatePlaceholder();
    this.renderQueue(); // 執行中／暫停會改變佇列的說明
    this.updateSend();
    this.box.classList.toggle('disabled', !!(hd && hd.accountMissing && !this.isReplica()));
  }

  /** 〔遠端〕附件走上傳（瀏覽器版、桌面可以操作的副本）；桌面這台的對話走宿主的 addFiles、attach */
  remoteUploads() {
    if (isHub) return true;
    const info = this.conv.liveInfo ? this.conv.liveInfo() : null;
    return !!info && controllable(info.origin, info.peer);
  }

  /** 〔同步〕桌面上其他電腦的副本（不是這台的帳戶，不看 accountMissing，D22） */
  isReplica() {
    const info = this.conv.liveInfo ? this.conv.liveInfo() : null;
    return !!info && info.origin === 'replica';
  }

  /**
   * 這個對話正在執行：header.running；〔桌面遠端操作〕副本是擁有者那邊的 header.replica.running（副本的 header.running 是這台的，一律 false），
   * 停止鈕與「加入佇列」照它
   */
  runningNow() {
    const hd = this.header;
    if (!hd) return false;
    return !!hd.running || !!(hd.replica && typeof hd.replica === 'object' && hd.replica.running === true);
  }

  /**
   * 執行中送出的訊息會直接插進這一輪（header.steer）。副本與瀏覽器版也一樣：從這裡送進其他電腦的是遠端來源的訊息，
   * 那台插之前以當時的模式與政策重查，不允許才留在佇列等這一輪結束
   */
  steers() {
    return !!(this.header && this.header.steer);
  }

  /** 佇列暫停：上一輪被停止或失敗後還有排隊訊息、而且沒有在執行 */
  queuePaused() {
    return this.state.queue.length > 0 && !this.runningNow();
  }

  setComposer(c) {
    this.state = {
      attachments: (c && c.attachments) || [],
      busy: !!(c && c.busy),
      busyText: (c && c.busyText) || null,
      queue: (c && c.queue) || [],
      // 〔遠端〕伺服器的佇列只帶前幾則，queueMore＝還有幾則
      queueMore: c && typeof c.queueMore === 'number' && c.queueMore > 0 ? Math.floor(c.queueMore) : 0,
      steering: (c && c.steering) || null,
      // 已插進這一輪、還可以取消的那則（composer.steered；只有桌面這台本機送的）
      steered: (c && c.steered && c.steered.id) ? c.steered : null,
    };
    this.renderSteered();
    this.renderAttachments();
    this.renderQueue();
    this.updatePlaceholder();
    this.busyEl.hidden = !this.state.busy;
    this.busyEl.querySelector('.cbusy-text').textContent = this.state.busyText || '正在處理附件…';
    this.updateSend();
  }

  renderAttachments() {
    const list = this.state.attachments;
    this.chips.hidden = !list.length;
    const ctx = this.conv.ctx;
    const existing = new Map(Array.from(this.chips.children).map((n) => [n.dataset.key, n]));
    const keep = new Set();
    let cursor = this.chips.firstElementChild;
    for (const a of list) {
      keep.add(a.id);
      let node = existing.get(a.id);
      const fresh = !node;
      const sig = JSON.stringify(a);
      if (node && node._sig !== sig) { node.remove(); node = null; }
      if (!node) {
        node = attachmentTile(a, ctx, { composer: true, onRemove: (x) => post({ cmd: 'removeAttachment', conversationId: this.conv.id, id: x.id }) });
        node._sig = sig;
        // 剛加進輸入框的附件淡入（fx.css 的 .atts.in-composer > .fx-in）；換了縮圖等重建的、打開對話時還原的不播
        if (fresh && this.chipsSeen) replayClass(node, 'fx-in', 300);
      }
      if (node !== cursor) this.chips.insertBefore(node, cursor); else cursor = cursor.nextElementSibling;
    }
    for (const [k, n] of existing) if (!keep.has(k)) n.remove();
    this.chipsSeen = true;
  }

  /** 輸入區上方「額度用完：等…重置後自動接著做」（Header.quotaWait；只有桌面這台的對話） */
  renderQuotaWait(hd) {
    const now = Date.now();
    const qw = hd && hd.quotaWait;
    const v = isHub || this.isReplica() ? null : quotaWaitView(qw, now);
    if (!v) {
      // 時間到、自動接著做（宿主拿掉 quotaWait）：輸入框閃一下綠（第九批）；按取消、自己送出（還沒到）不閃
      if (this.quotaUntil !== null && now >= this.quotaUntil - 60000) replayClass(this.box, 'fx-qw-go', 900);
      this.quotaUntil = null;
      this.quotaSig = '';
      setHidden(this.quotaEl, true);
      return;
    }
    setHidden(this.quotaEl, false);
    if (this.quotaUntil !== qw.until) { this.quotaUntil = qw.until; this.quotaSpan = Math.max(60000, qw.until - now); }
    const lit = quotaWaitCells(qw.until, now, this.quotaSpan, this.quotaCells.childElementCount);
    const sig = v.text + '|' + lit;
    if (sig === this.quotaSig) return;
    this.quotaSig = sig;
    setText(this.quotaText, v.text);
    this.quotaEl.title = v.title;
    Array.from(this.quotaCells.children).forEach((c, i) => c.classList.toggle('lit', i < lit));
  }

  /** 每秒（conversation.tick）：等額度重置的倒數與文字（值沒變不寫）、提示快取的外圈 */
  tickQuota(now) {
    if (this.quotaUntil !== null) this.renderQuotaWait(this.header);
    this.renderCache(now);
  }

  /**
   * 提示快取的外圈（第九批，claudeoptrules.cacheCountdown）：剩下的比例畫成弧、最後 10 分鐘琥珀、過期是灰的虛線；說明接在上下文說明後面。
   * 只有分鐘數或狀態變了才寫（每秒的 tick 叫，但一分鐘才改一次）
   */
  renderCache(now) {
    const v = this.ctxEl.hidden || isHub || this.isReplica() ? null : cacheCountdown(this.header, now);
    const sig = v ? v.state + ':' + v.mins : '';
    if (sig === this.cacheSig) return;
    this.cacheSig = sig;
    for (const st of ['fresh', 'low', 'expired']) this.ctxEl.classList.toggle('cache-' + st, !!v && v.state === st);
    this.ctxCache.style.strokeDasharray = v && v.state !== 'expired' ? (v.frac * 100).toFixed(1) + ' 100' : '';
    this.ctxEl.title = (this.ctxBaseTitle || '') + (v ? '\n' + v.title : '');
  }

  /**
   * 送出前的快取提醒（claudeoptrules.cacheResumeWarning）：要提醒時先問，照樣送出才再送一次（這次不再問）；
   * 勾了「以後不再提醒」就關掉設定。回 true＝已經接手（呼叫端不要再送）
   */
  holdForCacheWarning(resend) {
    if (this.cacheWarnSkip) { this.cacheWarnSkip = false; return false; }
    const w = isHub || this.isReplica() ? null : cacheResumeWarning(this.header, Date.now(), state.settings);
    if (!w) return false;
    confirmDialog({ title: w.title, message: w.message, detail: w.detail, confirmLabel: '照樣送出', checkbox: { label: '以後不再提醒' } }).then((r) => {
      if (r.checked) {
        if (state.settings) state.settings.claudeIdleResumeWarning = false;
        post({ cmd: 'setting', key: 'claudeIdleResumeWarning', value: false });
      }
      if (!r.ok) return;
      this.cacheWarnSkip = true;
      resend();
    });
    return true;
  }

  /** 輸入區上方「已插入這一輪：…」與取消（cancelSteer）：宿主說取消了才把文字放回輸入框（steerCancelled） */
  renderSteered() {
    const s = this.state.steered;
    this.steerEl.hidden = !s || isHub;
    if (!s || isHub) return;
    setText(this.steerText, steeredLabel(s));
    this.steerEl.title = s.text || '';
  }

  queueIsOpen() {
    if (this.queueOpen !== null) return this.queueOpen;
    const tall = (this.conv.root.clientHeight || window.innerHeight) >= 720;
    return tall || this.state.queue.length < 2;
  }

  renderQueue() {
    const q = this.state.queue;
    const more = this.state.queueMore || 0;
    this.queueEl.hidden = !q.length;
    if (!q.length) return;
    const open = this.queueIsOpen();
    const paused = this.queuePaused();
    const total = q.length + more;
    this.queueEl.classList.toggle('collapsed', !open);
    this.queueEl.classList.toggle('paused', paused);
    this.queueHead.setAttribute('aria-expanded', String(open));
    let sum;
    if (paused) sum = open ? '佇列已暫停，按送出繼續' : '佇列已暫停，按送出繼續（' + total + ' 則）：' + (mentionPlain(q[0].text) || '（附件）');
    else if (this.state.steering && q[0].id === this.state.steering) sum = open ? '正在插入這一輪…' : '正在插入這一輪：' + (mentionPlain(q[0].text) || '（附件）');
    else sum = open ? '排隊中，這一輪完成後依序送出' : '排隊中 ' + total + ' 則，這一輪完成後送出：' + (mentionPlain(q[0].text) || '（附件）');
    this.queueIc = syncIcon(this.queueIc, paused ? 'info' : 'clock');
    setText(this.queueSum, sum);
    this.queueChev = syncIcon(this.queueChev, open ? 'chevDown' : 'chevUp', 'queue-chev');
    this.queueList.hidden = !open;
    this.queueMoreEl.hidden = !open || !more;
    if (open && more) setText(this.queueMoreEl, '還有 ' + more + ' 則在佇列中');
    if (!open) return;
    // 離開佇列的那幾列（送出或取消）：先記下位置，reconcile 拿掉之後疊回原處往上淡出一次再移除——絕對定位、不佔位置、不接受點擊、
    // 排在清單最後（序號的計數器不算它們）：後面的列立刻遞補上來，連按兩下 ×，第二下照樣按到遞補上來的那一列
    const keep = new Set(q.map((m) => m.id));
    const leaving = this.queueShown && !calmMotion()
      ? Array.from(this.queueList.children).filter((r) => r.dataset.key && !keep.has(r.dataset.key) && !r.classList.contains('fx-leave'))
        .map((r) => ({ row: r, top: r.offsetTop }))
      : [];
    // 依 id 保留每一列（與「×」鈕），只更新文字
    reconcile(this.queueList, q, (m) => m.id, (m) => {
      const x = iconButton('x', '取消這則排隊訊息', () => post({ cmd: 'cancelQueued', conversationId: this.conv.id, id: m.id }), 'sm');
      const row = h('div', { class: 'queue-row' }, h('span', { class: 'queue-text' }), x);
      updateQueueRow(row, m);
      if (this.queueShown) replayClass(row, 'fx-in', 700); // 剛排進來的那一列滑進來（打開對話時已經在的不動）
      return row;
    }, updateQueueRow);
    for (const { row, top } of leaving) {
      delete row.dataset.key; // 下一次 reconcile 不把它當成還在的列
      row.classList.add('fx-leave');
      row.style.top = top + 'px';
      this.queueList.append(row);
      setTimeout(() => row.remove(), QUEUE_LEAVE_MS);
    }
    this.queueShown = true;
  }

  /** 待辦小面板：執行中時顯示最新一個 Plan 項目（內容沒變就不動 DOM） */
  renderTodo(planItem, running) {
    this.todoPlan = planItem;
    this.todoRunning = running;
    const steps = (planItem && planItem.plan) || [];
    const show = !!(running && steps.length);
    setHidden(this.todo, !show);
    if (!show) return;
    const done = steps.filter((s) => s.status === 'completed').length;
    const cur = steps.find((s) => s.status === 'inProgress') || steps.find((s) => s.status !== 'completed') || steps[steps.length - 1];
    this.todoDone.set(done);
    this.todoTotal.set(steps.length);
    setText(this.todoCur, cur ? cur.text || '' : '');
    this.todoHead.setAttribute('aria-expanded', String(this.todoOpen));
    this.todoChev = syncIcon(this.todoChev, this.todoOpen ? 'chevDown' : 'chevUp', 'todo-chev');
    this.todo.classList.toggle('open', this.todoOpen);
    this.todoList.hidden = !this.todoOpen;
    if (!this.todoOpen) return;
    const sig = JSON.stringify(steps.map((s) => [s.text, s.status]));
    if (this.todoList._sig === sig) return;
    this.todoList._sig = sig;
    const top = this.todoList.scrollTop;
    this.todoList.replaceChildren(...steps.map((s) => planRow(s)));
    this.todoList.scrollTop = top; // 步驟狀態改變時維持使用者捲到的位置
  }

  renderInfo() {
    const hd = this.header;
    if (!hd) {
      this.info.textContent = '';
      this.infoSig = null;
      return;
    }
    const acc = accountOf(hd.accountId);
    this.renderLeds();
    // 一律單行（chat.css 的 .cinfo 不換行）：帳戶名稱與「帳戶已移除」在標題列的帳戶晶片（與警示列）就看得到，這一行不重複
    const wins = infoWindows(acc && acc.usage && acc.usage.windows);
    // 用量只畫燈號：主要時間窗（通常是 5 小時）與每週各一組 5 格刻度（fxrules.meterCells，顏色跟著各自的嚴重度）；
    // 名稱、百分比、重置時間在滑過的說明裡
    const now = Date.now();
    const tip = (w) => (w.label || '用量') + ' ' + Math.round(w.percent || 0) + '%' + (w.resetsAt ? '・' + fmtReset(w.resetsAt, now) : '');
    const turn = parseTurnTokens(hd.lastTurn);
    // 輸出速度：一輪進行中才有；樣本只在輸出數變了時記
    const live = !!(turn && hd.turnLive);
    // 這一行畫出來的樣子（刻度、說明、token 的有無與即時與否、速度要不要放）沒變就不重建：執行中每次標頭（與每次 accounts）都會叫到這裡，
    // 以前每次整行清掉重建，輸入區的 .dock:has() 跟著失效重算（2026-10-25 失效追蹤）。token 計數器與速度一直是同一個元素，數字照常在下面換
    const sig = JSON.stringify([wins.map((w) => [w.severity || 'ok', meterCells(w.percent), tip(w)]), turn ? (hd.turnLive ? 'live' : 'turn') : hd.lastTurn || '', live, this.cpuShown]);
    if (sig !== this.infoSig) {
      this.infoSig = sig;
      this.info.textContent = '';
      const parts = [this.ledBox];
      if (wins.length) {
        // 上下疊成一欄（2026-10-26 使用者要求：省橫向空間）：上面主要時間窗、下面每週，每條 3px 高；每條只有 3px 不好對準，外框另有兩條合起來的說明
        parts.push(h('span', { class: 'ci-usage', dataset: { field: 'usage' }, title: wins.map(tip).join('\n') }, ...wins.map((w) => {
          const lit = meterCells(w.percent);
          return h('span', { class: 'ci-meter sev-' + (w.severity || 'ok'), role: 'img', 'aria-label': tip(w), title: tip(w) },
            ...Array.from({ length: 5 }, (_, i) => h('i', { class: i < lit ? 'lit' : '' })));
        })));
      }
      // 上一輪的 token 數：認得格式時用側欄同款的計數器（數字變了先閃過亂碼再逐位鎖定，tokenfx.js），其他照原文。
      // 2026-10-26 使用者選了省空間的箭頭寫法：「↑1.234M ↓12.34K」——↑ 送給模型的輸入、↓ 模型產生的輸出（同網路燈的上傳下載），
      // 數字是側欄的精簡格式（一萬以上取 4 位有效數字），精確數字在滑過的說明（renderInfo 每次更新）；箭頭包成 .ci-k（淡色）
      this.turnEl = null;
      if (turn) {
        // 綠色系的計數器（fx.css 的 .ci-turn；亂碼是駭客任務風格的半形片假名）
        if (!this.turnIn) { this.turnIn = tokenCounter(fmtShort, () => this.conv.visible, MATRIX_GLYPHS); this.turnOut = tokenCounter(fmtShort, () => this.conv.visible, MATRIX_GLYPHS); }
        // 一輪進行中（turnLive）是這一輪到目前為止的即時累計：數字更亮、帶光暈（fx.css 的 .ci-turn.live）
        this.turnEl = h('span', { class: 'ci-turn' + (hd.turnLive ? ' live' : '') },
          h('span', { class: 'ci-k ci-arrow', text: '↑', 'aria-label': '輸入' }), this.turnIn.num,
          h('span', { class: 'ci-k ci-arrow ci-out', text: '↓', 'aria-label': '輸出' }), this.turnOut.num);
        parts.push(this.turnEl);
      } else if (hd.lastTurn) parts.push(h('span', { class: 'ci-turn', text: hd.lastTurn }));
      if (live) parts.push(this.rateEl);
      if (this.cpuShown) parts.push(this.cpuEl);
      parts.forEach((p, i) => { if (i) this.info.append(h('span', { class: 'ci-sep', text: '・' })); this.info.append(p); });
    }
    if (live) {
      const last = this.rateSamples[this.rateSamples.length - 1];
      if (!last || last.out !== turn.output) this.rateSamples.push({ t: now, out: turn.output });
    } else this.rateSamples = [];
    this.rateEl.hidden = !live;
    if (live) this.tickRate(now);
    // 放進畫面之後才換值：計數器在畫面上才會解碼（第一次直接顯示）
    if (turn) { this.turnIn.set(turn.input); this.turnOut.set(turn.output); }
    if (turn && this.turnEl) {
      const title = (hd.turnLive ? '這一輪到目前為止的 token（即時）' : '上一輪的 token') + '：↑ 輸入 ' + fmtTokens(turn.input) + '、↓ 輸出 ' + fmtTokens(turn.output);
      if (this.turnEl.title !== title) this.turnEl.title = title;
    }
  }

  /** 每秒（conversation.tick）：輸出速度跟著時間更新（沒有新的輸出時慢慢降到 0） */
  tickRate(now) {
    if (this.rateEl.hidden) return;
    this.rateSamples = pruneRateSamples(this.rateSamples, now);
    const r = tokenRate(this.rateSamples, now);
    const label = r === null ? '—/s' : rateLabel(r);
    const n = this.rateEl.lastChild;
    if (n.textContent !== label) n.textContent = label;
    this.rateEl.classList.toggle('idle', !r);
    this.rateEl.title = '這一輪的輸出速度（最近 10 秒的輸出 token 增量）';
  }

  /**
   * 燈號的顯示：運行跟著執行中（副本照擁有者那邊）；資料流、網路、硬碟只有桌面這台的本機對話才有真的數字（副本與瀏覽器版收不到 activity，藏起來）
   */
  renderLeds() {
    const local = !isHub && !this.isReplica();
    for (const k of ['data', 'net', 'disk']) this.leds[k].hidden = !local;
    const running = this.runningNow();
    this.leds.run.classList.toggle('on', running);
    this.leds.run.title = ledTitle('run', { running }, Date.now());
    for (const k of ['data', 'net', 'disk']) this.leds[k].title = ledTitle(k, this.ledLast[k] || null, Date.now());
  }

  /** 宿主的 activity（AgentHub.Activity.cs）：這個對話的資料流與硬碟、整台電腦的網路，有動靜的燈亮一下（activityrules.ledPulse） */
  applyActivity(message) {
    if (isHub || this.isReplica()) return;
    const pulse = ledPulse(message, this.conv.id);
    const hold = calmMotion() ? LED_HOLD_CALM_MS : LED_HOLD_MS;
    const now = Date.now();
    for (const k of ['data', 'net', 'disk']) {
      const p = pulse[k];
      if (!p.on) continue;
      this.ledLast[k] = { rate: p.rate, rx: p.rx, tx: p.tx, at: now };
      const led = this.leds[k];
      led.classList.add('on');
      clearTimeout(led._off);
      led._off = setTimeout(() => led.classList.remove('on'), hold);
      led.title = ledTitle(k, this.ledLast[k], now);
    }
    const mem = memOf(message, this.conv.id);
    if (mem !== null) this.cpuMem = mem;
    const cpu = cpuOf(message, this.conv.id);
    if (cpu !== null) this.addCpu(cpu, now, message.ms);
  }

  /** 記一格 CPU 並重畫；太久沒有新的一格（這一輪結束、背景工作也沒了）就收起來 */
  addCpu(value, now, ms) {
    this.cpuHist = pushCpu(this.cpuHist, value);
    this.cpuScale = cpuWaveScale(this.cpuScale, this.cpuHist);
    clearTimeout(this.cpuOff);
    this.cpuOff = setTimeout(() => {
      this.cpuShown = false;
      this.cpuHist = [];
      this.cpuScale = 0;
      this.cpuMem = null;
      if (this.cpuAnim) this.cpuAnim.cancel();
      this.cpuAnim = null;
      this.renderInfo();
    }, CPU_STALE_MS);
    if (!this.cpuShown) {
      this.cpuShown = true;
      this.renderInfo();
      this.drawCpu(now, true, ms);
    } else this.drawCpu(now, false, ms);
  }

  /**
   * 畫 CPU 波形（使用者選的「極光面積、依負載變色」，activityrules.js 的說明）：平滑曲線下的填色面積往底部漸隱、頂端一條亮線，
   * 顏色沿著時間依每一格的負載；最左邊淡出。只在收到一格時畫（執行中每秒 4 次），兩格之間的平移交給合成器（canvas 的 transform 動畫，
   * 從目前的位置接著移，cpuSlideFrom）；視窗沒有焦點或要求減少動態時不平移、最多每秒畫一次。右邊寫最新的百分比，顏色同那一格
   */
  drawCpu(now, force, ms) {
    const slow = document.documentElement.dataset.window === 'inactive' || calmMotion();
    if (!force && slow && now - this.cpuDrawAt < CPU_SLOW_DRAW_MS) return;
    this.cpuDrawAt = now;
    const light = document.documentElement.dataset.theme === 'light';
    const theme = light ? 'light' : 'dark';
    const latest = this.cpuHist.length ? this.cpuHist[this.cpuHist.length - 1] : 0;
    const text = cpuText(latest);
    if (this.cpuNum.textContent !== text) this.cpuNum.textContent = text;
    const ink = cpuColor(latest, theme);
    if (this.cpuNum.style.color !== ink) this.cpuNum.style.color = ink;
    const mem = this.cpuMem === null ? '' : memText(this.cpuMem);
    if (this.memNum.textContent !== mem) this.memNum.textContent = mem;
    setHidden(this.memEl, !mem);
    this.cpuEl.setAttribute('aria-label', 'CPU ' + text + (mem ? '，記憶體 ' + mem : ''));

    // 平移做到哪裡：重畫讓每一格往左一格，新的一段從目前的位置加一格開始（沒在移、或移完了是 0）
    const cv = this.cpuCanvas;
    const anim = this.cpuAnim;
    const from = anim ? cpuSlideFrom(anim.effect.getComputedTiming().progress) : 0;
    if (anim) anim.cancel();
    this.cpuAnim = null;

    const width = CPU_WAVE_CANVAS;
    const height = CPU_WAVE_HEIGHT;
    const dpr = window.devicePixelRatio || 1;
    if (cv.width !== Math.round(width * dpr) || cv.height !== Math.round(height * dpr)) {
      cv.width = Math.round(width * dpr);
      cv.height = Math.round(height * dpr);
    }
    const ctx = cv.getContext('2d');
    if (!ctx) return;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, width, height);
    const pts = cpuWavePoints(this.cpuHist, this.cpuScale);
    if (pts.length) {
      // 頂端的線：每一格的高度連成平滑曲線（經過相鄰兩格的中點，二次曲線）
      const line = pts.map((p) => [p.x, height - 0.5 - p.a * (height - 2.5)]);
      const trace = () => {
        ctx.moveTo(line[0][0], line[0][1]);
        for (let i = 1; i < line.length - 1; i++) {
          ctx.quadraticCurveTo(line[i][0], line[i][1], (line[i][0] + line[i + 1][0]) / 2, (line[i][1] + line[i + 1][1]) / 2);
        }
        ctx.lineTo(line[line.length - 1][0], line[line.length - 1][1]);
      };
      const heat = ctx.createLinearGradient(0, 0, width, 0);
      for (const p of pts) heat.addColorStop(Math.min(1, Math.max(0, p.x / width)), cpuColor(p.v, theme));
      ctx.beginPath();
      trace();
      ctx.lineTo(line[line.length - 1][0], height);
      ctx.lineTo(line[0][0], height);
      ctx.closePath();
      ctx.fillStyle = heat;
      ctx.fill();
      // 面積往底部漸隱
      ctx.globalCompositeOperation = 'destination-in';
      const fade = ctx.createLinearGradient(0, 0, 0, height);
      fade.addColorStop(0, light ? 'rgba(0,0,0,.7)' : 'rgba(0,0,0,.85)');
      fade.addColorStop(1, 'rgba(0,0,0,.05)');
      ctx.fillStyle = fade;
      ctx.fillRect(0, 0, width, height);
      ctx.globalCompositeOperation = 'source-over';
      ctx.strokeStyle = heat;
      ctx.lineWidth = 1.2;
      ctx.lineJoin = 'round';
      ctx.beginPath();
      trace();
      ctx.stroke();
      // 最左邊淡出（最舊的幾格）
      ctx.globalCompositeOperation = 'destination-in';
      const left = ctx.createLinearGradient(0, 0, width, 0);
      left.addColorStop(0, 'rgba(0,0,0,0)');
      left.addColorStop(0.3, 'rgba(0,0,0,1)');
      ctx.fillStyle = left;
      ctx.fillRect(0, 0, width, height);
      ctx.globalCompositeOperation = 'source-over';
    }

    // 平移：最新的一格畫在 canvas 右緣（窗口外），這一段把它推進窗口；沒有焦點、減少動態時直接停在推進來的位置
    const end = 'translateX(' + -CPU_WAVE_STEP + 'px)';
    if (slow) {
      if (cv.style.transform !== end) cv.style.transform = end;
      return;
    }
    if (cv.style.transform) cv.style.transform = '';
    this.cpuAnim = cv.animate([{ transform: 'translateX(' + from + 'px)' }, { transform: end }], { duration: cpuSlideMs(ms), easing: 'linear', fill: 'forwards' });
  }

  refreshAccount() { this.renderInfo(); }

  // ───── 工具列 ─────
  currentModel() {
    const hd = this.header;
    const models = (hd && hd.models) || [];
    const v = (hd && hd.model) || 'default';
    return models.find((m) => m.value === v) || models.find((m) => m.value === 'default') || models[0] || null;
  }

  renderToolbar() {
    const hd = this.header;
    if (!hd) return;
    const modes = hd.permissionModes || [];
    const mode = modes.find((m) => m.value === hd.permission) || { value: hd.permission, label: hd.permission || '每步詢問' };
    this.permBtn.dataset.tone = permissionTone(mode);
    this.permBtn.querySelector('.perm-label').textContent = mode.label || mode.value || '';
    this.permBtn.title = (mode.description ? mode.label + '：' + mode.description : (mode.label || '')) + '（Shift+Tab 切換）';
    this.permBtn.hidden = !modes.length && !hd.permission;
    // 第十批：輸入框的角框與四角的燈跟著權限模式（完全自動紅、規劃藍，接受編輯與自動只有燈；fx.css 的 .cbox[data-perm]），換模式時閃一下
    const tone = this.permBtn.hidden ? 'neutral' : permissionTone(mode);
    if (this.box.dataset.perm !== tone) {
      const had = this.box.dataset.perm !== undefined;
      this.box.dataset.perm = tone;
      if (had) replayClass(this.box, 'fx-perm', 700);
    }

    const models = hd.models || [];
    const m = models.find((x) => x.value === (hd.model || 'default'));
    this.modelBtn.querySelector('.pick-label').textContent = m ? modelText(m) : (hd.model && hd.model !== 'default' ? hd.model : '預設模型');
    this.modelBtn.title = '模型：' + (m ? modelText(m) + (m.description ? '（' + m.description + '）' : '') : (hd.model || '預設'));
    this.modelBtn.hidden = !models.length;

    const ef = this.currentEffort();
    const label = effortText(ef.value, ef.defaultEffort, EFFORT_LABEL, ef.supported);
    this.effortBtn.querySelector('.pick-label').textContent = label;
    this.effortBtn.classList.toggle('ultra', ef.value === 'ultracode'); // Ultracode：按鈕也標成帳戶色（fx.css 的 .pick.ultra）
    this.effortBtn.title = '推理強度：' + label + (ef.supported ? '' : '，目前的模型不支援這個強度，可改回預設');
    // 模型不支援推理強度就不顯示；但目前帶著一個不支援的值時要留著，才看得到、也改得回預設
    this.effortBtn.hidden = !ef.efforts.length && ef.supported;

    const agents = Array.isArray(hd.agents) ? hd.agents : [];
    const curAgent = hd.agent && hd.agent !== 'default' ? hd.agent : 'default';
    const optCtx = { isHub, isReplica: this.isReplica() };
    this.codexBtn.hidden = !codexShown(hd, optCtx);
    this.codexBtn.disabled = !!(hd.codex && hd.codex.busy);
    this.codexBtn.querySelector('.pick-label').textContent = hd.codex && hd.codex.mode === 'plan' ? 'Codex · 先規劃' : 'Codex 工具';
    refreshCodexPanel(this);
    const agentShown = agentChipShown(hd, optCtx);
    this.agentBtn.hidden = !agentShown;
    if (agentShown) {
      const ag = agents.find((x) => x.value === curAgent);
      const agLabel = ag ? ag.label : (curAgent === 'default' ? '預設' : curAgent);
      this.agentBtn.querySelector('.pick-label').textContent = agLabel;
      const locked = agentLocked(hd);
      this.agentBtn.title = '自訂代理：' + agLabel + (ag && ag.description ? '（' + ag.description + '）' : '') + (locked ? '・已建立對話，開新對話才能更換' : '');
    }
    const styleShown = styleChipShown(hd, optCtx);
    this.styleBtn.hidden = !styleShown;
    if (styleShown) {
      const sl = styleLabel(hd.outputStyle);
      this.styleBtn.querySelector('.pick-label').textContent = sl;
      this.styleBtn.title = '輸出風格：' + sl + (hd.outputStyle && sl !== hd.outputStyle ? '（' + hd.outputStyle + '）' : '');
    }

    this.renderContext();
  }

  /** 上下文用量的小圓環（規則在 contextrules.js；內容沒變就不動 DOM） */
  renderContext() {
    const hd = this.header;
    const ctx = hd && hd.context;
    const show = contextShown(ctx);
    this.ctxEl.hidden = !show;
    if (!show) { this.renderCache(Date.now()); return; }
    const sig = JSON.stringify([ctx.used, ctx.window, ctx.percent, ctx.autoCompactAt, ctx.severity, hd.provider]);
    if (this.ctxEl._sig === sig) { this.renderCache(Date.now()); return; } // 執行中、閒置換了：外圈照樣跟著
    this.ctxEl._sig = sig;
    const p = contextPercent(ctx);
    const sev = contextSeverity(ctx);
    // 嚴重度升高時圓環彈一下、壓縮後百分比大降時倒轉一圈（fxrules.gaugeFx）
    const gfx = gaugeFx(this.ctxEl._sev, sev, this.ctxEl._pct, p);
    this.ctxEl._sev = sev;
    this.ctxEl._pct = p;
    if (gfx) replayClass(this.ctxEl, 'fx-g-' + gfx, 800);
    // 自動壓縮：同一個上下文視窗的用量大降時浮出壓縮前後的數字（第一次顯示、換了模型〔視窗大小不同〕不算）
    const drop = this.ctxEl._win === ctx.window ? compactDrop(this.ctxEl._used, ctx.used) : null;
    this.ctxEl._used = ctx.used;
    this.ctxEl._win = ctx.window;
    if (drop) this.showCompact(drop);
    this.ctxEl.dataset.sev = sev;
    this.ctxArc.style.strokeDasharray = p.toFixed(1) + ' 100';
    this.ctxArc.style.opacity = p > 0 ? '' : '0';
    const mark = contextMark(ctx);
    this.ctxTick.style.display = mark == null ? 'none' : '';
    if (mark != null) this.ctxTick.style.strokeDashoffset = String(-Math.max(0, mark - 1));
    this.ctxNum.textContent = contextLabel(ctx);
    this.ctxBaseTitle = contextTitle(ctx, hd.provider);
    this.ctxEl.title = this.ctxBaseTitle;
    this.cacheSig = null;
    this.renderCache(Date.now());
    this.ctxEl.setAttribute('aria-label', contextSummary(ctx));
  }

  /** 「已壓縮 412K → 96K」浮出 COMPACT_NOTE_MS 後收起 */
  showCompact(drop) {
    clearTimeout(this.ctxNoteTimer);
    this.ctxNote.textContent = '已壓縮 ' + fmtTokK(drop.from) + ' → ' + fmtTokK(drop.to);
    this.ctxNote.hidden = false;
    replayClass(this.ctxEl, 'fx-compact', COMPACT_NOTE_MS);
    this.ctxNoteTimer = setTimeout(() => { this.ctxNote.hidden = true; }, COMPACT_NOTE_MS);
  }

  /** 目前的推理強度：header.effort 不在模型的清單內時如實回報（supported=false），不當成「預設」 */
  currentEffort() {
    const hd = this.header;
    const cm = this.currentModel();
    const efforts = (cm && cm.efforts) || [];
    const value = hd && hd.effort && hd.effort !== 'default' ? hd.effort : 'default';
    // 選「預設」時實際用的強度：宿主算好的（含 Codex 設定檔指定的）優先，剛換模型、宿主的標頭還沒到時先用那個模型的
    const defaultEffort = (hd && typeof hd.defaultEffort === 'string' && hd.defaultEffort) || (cm && typeof cm.defaultEffort === 'string' ? cm.defaultEffort : null);
    return { value, efforts, defaultEffort, supported: value === 'default' || efforts.includes(value) };
  }

  setOption(key, value) {
    if (isCollaborator()) return; // 多人協作：模型、強度、權限模式、代理與風格只有擁有者能改
    const hd = this.header;
    if (!hd) return;
    const before = { [key]: hd[key], effort: hd.effort };
    hd[key] = value; // 先樂觀更新，宿主的 header 為準
    // 宿主換模型時會把新模型不支援的推理強度改回預設（AgentHub.SetOption）：畫面先照做，不會閃一下「不支援」
    if (key === 'model') hd.defaultEffort = null; // 換了模型：宿主的標頭到之前，「預設（…）」照新模型的預設強度
    if (key === 'model' && !this.currentEffort().supported) hd.effort = 'default';
    const after = { [key]: hd[key], effort: hd.effort };
    this.renderToolbar();
    const cid = post({ cmd: 'setOption', conversationId: this.conv.id, key, value });
    // 〔遠端〕改的是別台電腦的對話（瀏覽器版、桌面的副本）：被拒或結果不明時那台沒改、不會有新的標頭，畫面回到原本的值
    if (typeof cid === 'string') track(cid, { onSettle: (st) => { if (st !== 'done') this.revertOption(hd, before, after); } });
  }

  /** 〔遠端〕setOption 沒成功：標頭還是同一份、也沒再改過時才還原（已經收到新的標頭就以它為準） */
  revertOption(hd, before, after) {
    if (this.header !== hd || Object.keys(after).some((k) => hd[k] !== after[k])) return;
    Object.assign(hd, before);
    this.renderToolbar();
  }

  /** 〔遠端〕權限選單裡停用的模式（liverules.permissionChoices，D15：那台沒允許遠端完全自動時的 FullAccess）：value → 說明 */
  blockedModes() {
    const hd = this.header;
    const info = this.conv.liveInfo ? this.conv.liveInfo() : null;
    const out = new Map();
    if (!hd || !info) return out;
    for (const c of permissionChoices(hd.permissionModes || [], info.device, info.mode, info.origin, info.peer)) if (c.disabled) out.set(c.value, c.reason);
    return out;
  }

  openPermissionMenu(keyboard) {
    if (isCollaborator()) return; // 多人協作：模型、強度、權限模式、代理與風格只有擁有者能改
    const hd = this.header;
    if (!hd) return;
    const modes = hd.permissionModes || [];
    const blocked = this.blockedModes();
    const items = modes.map((m) => ({
      label: m.label || m.value, desc: blocked.has(m.value) ? blocked.get(m.value) : m.description, checked: m.value === hd.permission,
      color: toneColor(permissionTone(m)), disabled: blocked.has(m.value), title: blocked.get(m.value) || null,
      onSelect: () => this.choosePermission(m, false),
    }));
    items.push({ type: 'custom', node: h('div', { class: 'menu-foot' }, h('kbd', { text: 'Shift+Tab' }), h('span', { text: '在輸入框裡依序切換' })) });
    openMenu(this.permBtn, items, { placement: 'top-start', minWidth: 280, keyboard, restoreFocus: this.ta, className: 'menu-perm' });
  }

  async choosePermission(mode, cycling) {
    const hd = this.header;
    if (!hd || mode.value === hd.permission) return;
    if (permissionTone(mode) === 'danger') {
      const r = await confirmFullAccess(mode, cycling);
      if (r === 'ok') this.setOption('permission', mode.value);
      else if (r === 'skip' && cycling) {
        const modes = hd.permissionModes || [];
        const next = modes[(modes.indexOf(mode) + 1) % modes.length];
        if (next && next.value !== hd.permission && permissionTone(next) !== 'danger') this.setOption('permission', next.value);
      }
      this.focus();
      return;
    }
    this.setOption('permission', mode.value);
  }

  cyclePermission() {
    const hd = this.header;
    const modes = (hd && hd.permissionModes) || [];
    if (modes.length < 2) return;
    const blocked = this.blockedModes();
    const i = modes.findIndex((m) => m.value === hd.permission);
    // 〔遠端〕停用的模式（那台沒允許的完全自動）跳過
    for (let k = 1; k < modes.length; k++) {
      const next = modes[(i + k + modes.length) % modes.length];
      if (blocked.has(next.value)) continue;
      this.choosePermission(next, true);
      return;
    }
  }

  openModelMenu() {
    if (isCollaborator()) return; // 多人協作：模型、強度、權限模式、代理與風格只有擁有者能改
    const hd = this.header;
    if (!hd) return;
    const models = (hd.models || []).slice();
    const cur = hd.model || 'default';
    const items = models.map((m) => ({ label: modelText(m), desc: m.description, checked: m.value === cur, onSelect: () => this.setOption('model', m.value) }));
    if (!models.some((m) => m.value === 'default')) items.unshift({ label: '預設', desc: '帳戶預設模型', checked: cur === 'default', onSelect: () => this.setOption('model', 'default') });
    if (cur !== 'default' && !models.some((m) => m.value === cur)) items.push({ label: cur, desc: '目前使用中', checked: true, onSelect: () => {} });
    // Claude 的主代理與輸出風格：選了預設以外的值才有自己的晶片，平常從這裡進入
    const extra = claudeMenuEntries(hd, { isHub, isReplica: this.isReplica() });
    if (extra.length) {
      items.push({ type: 'sep' });
      for (const e of extra) {
        items.push({ label: e.label + '…', icon: e.kind === 'agent' ? 'agent' : 'palette',
          onSelect: () => setTimeout(() => (e.kind === 'agent' ? this.openAgentMenu(this.modelBtn) : this.openStyleMenu(this.modelBtn)), 0) });
      }
    }
    openMenu(this.modelBtn, items, { placement: 'top-start', minWidth: 260, restoreFocus: this.ta });
  }

  openEffortMenu() {
    if (isCollaborator()) return; // 多人協作：模型、強度、權限模式、代理與風格只有擁有者能改
    if (!this.header) return; // 對話還在載入
    const ef = this.currentEffort();
    const items = [{ label: effortText('default', ef.defaultEffort, EFFORT_LABEL), desc: '依模型與帳戶的預設', checked: ef.value === 'default', onSelect: () => this.setOption('effort', 'default') }];
    // Ultracode（xhigh 加上動態工作流程，可以同時派多個代理）和一般強度不同：選單裡標成帳戶色的描邊與記號（fx.css 的 .mi-ultra）
    for (const e of ef.efforts) items.push({ label: EFFORT_LABEL[e] || e, desc: EFFORT_DESC[e], hint: e, checked: ef.value === e, cls: e === 'ultracode' ? 'mi-ultra' : null, onSelect: () => this.setOption('effort', e) });
    if (!ef.supported) items.push({ label: EFFORT_LABEL[ef.value] || ef.value, desc: '目前使用中，這個模型不支援', hint: ef.value, checked: true, onSelect: () => {} });
    openMenu(this.effortBtn, items, { placement: 'top-start', minWidth: 180, restoreFocus: this.ta });
  }

  openAgentMenu(anchor) {
    if (isCollaborator()) return; // 多人協作：模型、強度、權限模式、代理與風格只有擁有者能改
    const hd = this.header;
    if (!hd || isHub || this.isReplica() || (hd.isDraft && hd.deviceId)) return;
    const agents = Array.isArray(hd.agents) && hd.agents.length ? hd.agents.slice() : [{ value: 'default', label: '預設', description: defaultAgentDesc(hd.provider) }];
    const cur = hd.agent && hd.agent !== 'default' ? hd.agent : 'default';
    const locked = agentLocked(hd);
    const items = agents.map((a) => ({
      label: a.label || a.value,
      desc: locked ? '自訂代理只能在送出第一則訊息前選擇' : a.description,
      hint: a.value !== 'default' && a.label !== a.value ? a.value : null,
      checked: a.value === cur,
      disabled: locked && a.value !== cur,
      onSelect: () => {
        if (locked || a.value === cur) return;
        hd.agent = a.value;
        this.renderToolbar();
        post({ cmd: 'setAgent', conversationId: this.conv.id, agent: a.value });
      },
    }));
    if (cur !== 'default' && !agents.some((a) => a.value === cur)) {
      items.push({ label: cur, desc: '目前使用中', checked: true, onSelect: () => {} });
    }
    openMenu(anchor || this.agentBtn, items, { placement: 'top-start', minWidth: 240, restoreFocus: this.ta });
  }

  openStyleMenu(anchor) {
    if (isCollaborator()) return; // 多人協作：模型、強度、權限模式、代理與風格只有擁有者能改
    const hd = this.header;
    if (!hd || !showStyle(hd, { isHub, isReplica: this.isReplica() })) return;
    const items = styleMenu(hd).map((s) => ({
      label: s.label, desc: s.desc, hint: s.hint, checked: s.checked,
      onSelect: () => {
        if (s.checked) return;
        hd.outputStyle = s.value;
        this.renderToolbar();
        post({ cmd: 'setOutputStyle', conversationId: this.conv.id, style: s.value });
      },
    }));
    openMenu(anchor || this.styleBtn, items, { placement: 'top-start', minWidth: 240, restoreFocus: this.ta });
  }

  // ───── 送出 ─────
  updatePlaceholder() {
    const hd = this.header;
    const enter = state.settings.enterToSend !== false || handheld(); // 手機、平板沒有 Ctrl+Enter 可說：一律用送出鈕
    let p;
    if (hd && hd.accountMissing && !this.isReplica()) p = '這個對話的帳戶已被移除，請先更換帳戶';
    else if (this.runningNow()) p = this.steers() ? '執行中，送出的訊息會直接插入這一輪，代理下一步就讀到' : '執行中，可以先輸入下一則訊息';
    else if (this.queuePaused()) p = '佇列已暫停：按送出從第一則繼續，或輸入新的訊息';
    else p = enter ? '要代理做什麼？' : '要代理做什麼？（Ctrl+Enter 送出）';
    this.ta.placeholder = p;
  }

  /** 〔遠端〕送出的閘門（ConvView.gate → liverules.composerGate，D14）；擋著時輸入框下方一行說明 */
  sendGate() {
    const g = this.conv.ctx && this.conv.ctx.gate ? this.conv.ctx.gate() : null;
    const blocked = !!(g && !g.canSend);
    const readOnly = !!(g && g.readOnly);
    this.setReadOnly(readOnly, readOnly ? g.reason : null);
    this.gateEl.hidden = !blocked || readOnly;
    if (blocked && !readOnly) setText(this.gateEl.lastChild, g.reason || '');
    return blocked;
  }

  /**
   * 〔同步〕副本（閘門的 readOnly）：輸入框、附件與送出鈕從輸入區拿掉（不只是停用），換成唯讀說明與「在網頁版開啟」；
   * 不是副本時放回原位置
   */
  setReadOnly(on, reason) {
    this.roEl.hidden = !on;
    if (!on) {
      if (this.box.parentNode !== this.el) this.roEl.before(this.box);
      return;
    }
    if (this.box.parentNode === this.el) {
      this.closeSuggest();
      this.box.remove();
    }
    setText(this.roEl.querySelector('.creadonly-text'), reason || '');
    this.webBtn.hidden = !this.webUrl();
  }

  /** 副本在伺服器網頁版的網址（sync.server 的 /app/ 彈出視窗）；沒有配對的伺服器時 null */
  webUrl() {
    const s = state.remote && state.remote.sync;
    return replicaWebUrl(s && s.server, this.conv.id);
  }

  /** 「在網頁版開啟」：以預設瀏覽器開（openLink；不帶 conversationId，副本只收看的指令） */
  openInWeb() {
    const href = this.webUrl();
    if (href) post({ cmd: 'openLink', href });
  }

  updateSend() {
    if (this.draftSync) this.draftSync.changed(); // 字變了就排著同步（沒變什麼都不做）
    const running = this.runningNow();
    const has = this.ta.value.trim().length > 0 || this.state.attachments.length > 0 || this.attachedUploads().length > 0;
    const resume = !has && this.queuePaused();
    const blocked = this.sendGate();
    this.updateAttach();
    this.stopBtn.hidden = !running;
    this.sendBtn.hidden = running && !has;
    this.sendBtn.disabled = blocked || (!has && !resume) || this.state.busy;
    const enter = state.settings.enterToSend !== false;
    const steer = running && this.steers();
    const sendTitle = handheld() ? '送出' : enter ? '送出（Enter）' : '送出（Ctrl+Enter）';
    this.sendBtn.title = steer ? '插入這一輪（不等這一輪完成，代理下一步就讀到）' : running ? '加入佇列（這一輪完成後送出）' : resume ? '從佇列第一則繼續送出' : sendTitle;
    this.sendBtn.classList.toggle('queueing', running); // 不叫 queue：會套到佇列面板 .queue 的 margin、padding、虛線框（chat.css）
    this.sendBtn.classList.toggle('resume', resume);
    const contTitle = continueTitle({ running, steers: steer, text: this.continueText() });
    if (this.contBtn.title !== contTitle) { this.contBtn.title = contTitle; this.contBtn.setAttribute('aria-label', contTitle); }
    this.contBtn.disabled = blocked || this.state.busy;
  }

  send() {
    this.voice.endDictation(); // 送出之後才到的辨識結果不再寫回輸入框
    const text = this.ta.value;
    // 〔遠端〕網頁上傳、已加進輸入區的附件：只附檔不打字也能送（電腦送出這些附件）；送出完成（done）才清掉這些晶片
    const ups = this.attachedUploads();
    const has = text.trim().length > 0 || this.state.attachments.length > 0 || ups.length > 0;
    // 〔遠端〕閘門擋著（沒連線、電腦離線、政策、完全自動沒允許）：不送，字留著；原因在輸入框下方
    if (this.sendGate()) return;
    // 佇列暫停時，空白送出＝從佇列第一則接著送（宿主處理）
    if (!has && this.queuePaused()) {
      post({ cmd: 'send', conversationId: this.conv.id, text: '' });
      this.conv.stickToBottom();
      return;
    }
    if (!has) return;
    if (this.holdForCacheWarning(() => this.send())) return;
    if (!this.dispatch(text, ups)) return;
    rememberInput(this.conv.id, text);
    noteSent(this.conv.id, text); // 草稿同步：別處傳來剛送出的這句時不填回輸入框（draftsync.js）
    this.hist = idleNav();
    this.sendFx(text);
    this.ta.value = '';
    // 輸入框清空了：先前按 Esc（或不能搜尋時按 Enter 送出）關掉的「@」提示位置不再算數，下一則開頭打「@」照樣提示
    this.dismissedAt = -1;
    this.autosize();
    this.updateSend();
    this.draftSync.flush(); // 送出了：草稿的「清掉」馬上送，不等打字停下的那 0.7 秒（手機送出後可能馬上切走）
    this.closeSuggest();
    this.conv.stickToBottom();
  }

  /**
   * 「繼續」鈕要送的那一句：桌面看宿主的設定，〔遠端〕瀏覽器版（傳輸層是伺服器，或 init 的 remote.mode 是 browser——同設定頁的 isBrowserMode）
   * 看這個瀏覽器存的；沒設定就是預設
   */
  continueText() {
    const browser = isHub || !!(state.remote && state.remote.mode === 'browser');
    return continueTextOf(browser ? continueLocal() : state.settings.continueText);
  }

  /**
   * 「繼續」鈕：送出設定的那一句，和打字送出一樣經 dispatch（附件、執行中插入或排隊、送出中、遠端追蹤都照舊）；
   * 輸入框的字與輸入紀錄不動
   */
  sendContinue() {
    if (this.sendGate()) { toast('現在不能送出，原因在輸入框下方', 'error'); return; }
    if (this.holdForCacheWarning(() => this.sendContinue())) return;
    if (!this.dispatch(this.continueText(), this.attachedUploads())) return;
    replayClass(this.contBtn, 'fx-sent', 600); // 擴散一圈，同送出鈕（fx.css）
    this.conv.stickToBottom();
  }

  /**
   * 送出的手感：送出鈕擴散一圈；送出的前幾行字疊在輸入框上往上滑出、淡掉（只動 transform／opacity，
   * 減少動態與關掉動態效果時不做）。疊的那一層 aria-hidden、不接滑鼠，播完就移除
   */
  sendFx(text) {
    replayClass(this.sendBtn, 'fx-sent', 600);
    const lines = text.trim().split('\n').slice(0, 3).join('\n');
    if (!lines || calmMotion()) return;
    const br = this.box.getBoundingClientRect(), tr = this.ta.getBoundingClientRect();
    if (!tr.width) return;
    const cs = getComputedStyle(this.ta);
    const ghost = h('div', { class: 'send-ghost', 'aria-hidden': 'true', text: lines });
    Object.assign(ghost.style, { left: (tr.left - br.left) + 'px', top: (tr.top - br.top) + 'px', width: tr.width + 'px', padding: cs.padding, font: cs.font, lineHeight: cs.lineHeight });
    this.box.append(ghost);
    const a = ghost.animate([{ transform: 'none', opacity: 1 }, { transform: 'translateY(-18px)', opacity: 0 }], { duration: 280, easing: 'cubic-bezier(.2, .8, .2, 1)' });
    a.onfinish = a.oncancel = () => ghost.remove();
  }

  /**
   * 送出一則（輸入框的 send 與對話欄的「重新送出」共用）：檢查附件與帳戶、送 send、先畫「送出中」（sendrules.js）、追蹤遠端的結果。
   * 回傳有沒有送出（沒送出時字留在原處）
   */
  dispatch(text, ups) {
    if (this.state.busy) { toast('附件還在處理中，請稍候再送出'); return false; }
    if (this.header && this.header.accountMissing) { toast('這個對話的帳戶已被移除，請先更換帳戶', 'error'); return false; }
    // 〔桌面遠端操作〕要在其他電腦開的新對話：帶 commandId（宿主組 conversationCreate 經伺服器送給那台，結果以 commandResult 回）
    const remoteDraft = !isHub && !!(this.header && this.header.isDraft && this.header.deviceId);
    // 送出前的狀態（post 之後宿主的回應可能已經改了輸入區）：附件數、執行中、能不能插入
    const files = this.state.attachments.length + ups.length;
    const sending = showsSending({ text, files, running: this.runningNow(), steers: this.steers(), remote: isHub || this.isReplica() });
    const id = post(Object.assign({ cmd: 'send', conversationId: this.conv.id, text }, remoteDraft ? { commandId: peerId() } : {}));
    if (id === null) return false; // 〔遠端〕瀏覽器版沒送出（沒有連線、太大；傳輸層已提示）：字留著
    // 同樣的文字之前標成沒有送出的收掉；宿主的使用者訊息到之前先畫在對話欄
    if (this.conv.clearFailed) this.conv.clearFailed(text);
    if (sending && this.conv.addSending) this.conv.addSending(text, files, { tracked: typeof id === 'string' });
    this.showSettle(null);
    if (typeof id === 'string' && this.conv.ctx.track) {
      this.conv.ctx.track(id, { onSettle: (state, code, origin, elapsed) => { this.onSendSettled(text, state, code, origin); this.uploadsAfterSend(ups, state); if (state === 'done') this.showRtt(elapsed); } });
    }
    return true;
  }

  /**
   * 對話欄裡沒有送出的那則按「重新送出」：同樣的閘門；輸入框裡剛好是被退回的同一段字時一起清掉（免得之後又送一次）。
   * 附件照輸入區現在的（被拒時宿主把附件還回輸入區）
   */
  resend(text) {
    if (this.sendGate()) { toast('現在不能送出，原因在輸入框下方', 'error'); return false; }
    if (!this.dispatch(text, this.attachedUploads())) return false;
    noteSent(this.conv.id, text);
    if (this.ta.value.trim() === String(text || '').trim()) {
      this.ta.value = '';
      this.autosize();
      this.updateSend();
    }
    this.conv.stickToBottom();
    return true;
  }

  /** 對話欄裡沒有送出的那則按「放回輸入框」：輸入框是空的就放進去，已經有字（或就是這段）就插在游標處／不重複放 */
  putBack(text) {
    const t = String(text || '');
    if (this.ta.value.trim() === t.trim()) { this.focus(); return; }
    if (!this.ta.value.trim()) {
      this.ta.value = t;
      this.autosize();
      this.updateSend();
      this.focus();
      return;
    }
    this.insertText(t);
  }

  /**
   * 〔遠端〕送出的結果（D5）：伺服器的拒絕由伺服器另送的 sendRejected 還原字；傳輸層合成的 rate_limited、not_sent（伺服器沒處理這則）
   * 由這裡在輸入框是空的時候還原一次；unknown 可能已執行，不還原、在輸入區上方說明。對話欄裡那則：done 是已送到那台電腦，
   * 其他標成沒有送出（rejected）或不確定（unknown），留著可以重新送出
   */
  onSendSettled(text, state, code, origin) {
    if (state === 'done') { if (this.conv.acceptSending) this.conv.acceptSending(text); }
    else if (this.conv.failSending) this.conv.failSending(text, failureOf({ state, code }));
    if (sendRestorePlan({ state, code, origin })) { this.restoreText(text); return; }
    if (state === 'unknown') this.showSettle(settleText(state));
  }

  /** 〔遠端〕往返讀數（輸入框下方燈號的後面）：送出到結果回來的毫秒數，RTT_SHOW_MS 後收起；不是有效的時間不顯示 */
  showRtt(ms) {
    const label = rttLabel(ms);
    if (!label || this.destroyed) return;
    clearTimeout(this.rttTimer);
    this.rttEl.lastChild.textContent = label;
    this.rttEl.title = '往返時間：從送出到那台電腦回報結果花了 ' + label;
    this.rttEl.hidden = false;
    replayClass(this.rttEl, 'fx-rtt', RTT_SHOW_MS);
    this.rttTimer = setTimeout(() => { this.rttEl.hidden = true; }, RTT_SHOW_MS);
  }

  showSettle(text) {
    this.settleEl.hidden = !text;
    if (text) setText(this.settleEl.lastChild, text);
  }

  /** sendRejected：輸入框目前是空的才還原 */
  restoreText(text) {
    if (this.ta.value.trim() || !text) return;
    this.ta.value = text;
    this.autosize();
    this.updateSend();
  }

  /**
   * 換掉輸入框的全部文字（「最近的指令」的歷史指令）：經 execCommand，Ctrl+Z 還原得回原本的文字；不支援時直接換。
   * 唯讀（副本、被擋下）時不動
   */
  replaceText(text) {
    const ta = this.ta;
    if (ta.disabled || ta.readOnly || typeof text !== 'string') return;
    this.focus();
    ta.select();
    let ok = false;
    try { ok = document.execCommand('insertText', false, text); } catch { ok = false; }
    if (!ok || ta.value !== text) ta.value = text;
    ta.setSelectionRange(ta.value.length, ta.value.length);
    this.autosize();
    this.updateSend();
  }

  insertText(text) {
    const ta = this.ta;
    const s = ta.selectionStart ?? ta.value.length, e = ta.selectionEnd ?? ta.value.length;
    const before = ta.value.slice(0, s);
    const pad = before && !/\s$/.test(before) ? ' ' : '';
    ta.setRangeText(pad + text, s, e, 'end');
    this.autosize();
    this.updateSend();
    this.focus();
  }

  autosize() {
    const ta = this.ta;
    // 隱藏中（分頁不在前景、儀表板檢視）量不到內容高度：保留原高度，窗格顯示時 ResizeObserver 會再算一次
    if (!ta.clientWidth) return;
    const max = this.maxInputHeight();
    ta.style.height = 'auto';
    ta.style.height = Math.min(ta.scrollHeight, max) + 'px';
    ta.classList.toggle('scrolling', ta.scrollHeight > max);
  }

  /**
   * 輸入框最多長到多高：窗格（訊息串＋輸入區）高度的 40%、最多 360px，而且扣掉輸入區其他部分（佇列、待辦、附件、工具列）後
   * 訊息串至少留 30%。3～4 窗格時窗格只有視窗一半高，用視窗高度算會把訊息串擠成 0、工具列被下一排窗格蓋住
   */
  maxInputHeight() {
    const paneH = (this.conv.main && this.conv.main.clientHeight) || this.conv.root.clientHeight || window.innerHeight;
    const others = Math.max(0, this.el.offsetHeight - this.ta.offsetHeight);
    return Math.round(Math.max(MIN_INPUT_HEIGHT, Math.min(360, paneH * 0.4, paneH * 0.7 - others)));
  }

  onKeyDown(e) {
    // 語音輸入中開始打字（不含按 Enter 送出、方向鍵）：交給鍵盤，之後才到的辨識結果不蓋掉打的字
    if (e.isComposing || e.keyCode === 229) return; // IME 組字中
    if (this.sugg && !this.suggest.hidden) {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        const n = this.sugg.items.length;
        if (n) { this.sugg.sel = (this.sugg.sel + (e.key === 'ArrowDown' ? 1 : -1) + n) % n; this.renderSuggest(); }
        return;
      }
      if ((e.key === 'Enter' || e.key === 'Tab') && !e.shiftKey) {
        if (this.sugg.items.length) {
          e.preventDefault();
          this.acceptSuggest(this.sugg.sel);
          return;
        }
        if (this.sugg.type === 'file' && this.sugg.error && e.key === 'Enter') {
          // 〔遠端〕不能搜尋（伺服器草稿、被拒、等不到回覆）：關掉提示，Enter 照常送出（合併前審查 P2）
          this.dismissedAt = this.sugg.start;
          this.closeSuggest();
        } else if (this.sugg.type === 'file') {
          // 檔案提示開著但還沒有結果：搜尋中就等結果回來插入第一項；找不到就保留原文（按 Esc 關掉提示後才送出）
          e.preventDefault();
          if (this.sugg.loading) this.sugg.acceptFirst = true;
          return;
        }
      }
      if (e.key === 'Escape') {
        e.preventDefault(); e.stopPropagation();
        this.dismissedAt = this.sugg.start;
        this.closeSuggest();
        return;
      }
    }
    if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && !e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey
      && this.recallHistory(e.key === 'ArrowUp' ? 'up' : 'down')) { e.preventDefault(); return; }
    if (e.key === 'Tab' && e.shiftKey && !e.ctrlKey && !e.altKey) { e.preventDefault(); this.cyclePermission(); return; }
    if (e.key === 'Enter' && enterSends({ shiftKey: e.shiftKey, ctrlKey: e.ctrlKey, metaKey: e.metaKey, altKey: e.altKey,
      enterToSend: state.settings.enterToSend, handheld: handheld() })) { e.preventDefault(); this.send(); }
  }

  /**
   * 執行中按 Esc＝停止這一輪（同 CLI 與 VS Code 外掛）。焦點要在訊息串或輸入框；
   * @／/ 提示、選單、對話框、檔案瀏覽器與其他輸入欄位的 Esc 照舊由它們處理。
   */
  onPaneKeyDown(e) {
    if (e.key !== 'Escape' || e.defaultPrevented || e.repeat || e.isComposing || e.keyCode === 229) return;
    if (e.ctrlKey || e.altKey || e.shiftKey || e.metaKey) return;
    if (!this.runningNow() || hasLayer()) return;
    const t = e.target;
    if (!this.conv.col || !this.conv.col.contains(t)) return;
    if (t !== this.ta && t.closest('input, textarea, select, [contenteditable="true"]')) return;
    e.preventDefault();
    this.stop();
  }

  stop() {
    this.stopAt = Date.now(); // 這之後結束的一輪不閃結束的燈（fxrules.turnEndTone）
    post({ cmd: 'stop', conversationId: this.conv.id });
  }

  // ───── 輸入紀錄：↑↓ 帶入這個對話最近送出的訊息（每個對話一份，historyrules.js） ─────
  /** ↑（dir 'up'）／↓（'down'）：輸入框換成紀錄裡的一則、游標放到最後；不處理時回 false（照預設移動游標） */
  recallHistory(dir) {
    const ta = this.ta;
    const r = historyMove(this.hist, dir, ta.value, ta.selectionStart, ta.selectionEnd, historyOf(readStore(), this.conv.id));
    if (!r) return false;
    ta.value = r.value;
    // textarea 會把 CRLF 換成 LF：「沒改過」以輸入框實際的值比對
    this.hist = r.nav.index >= 0 ? Object.assign({}, r.nav, { shown: ta.value }) : r.nav;
    ta.setSelectionRange(ta.value.length, ta.value.length);
    this.dismissedAt = -1;
    this.closeSuggest();
    this.autosize();
    ta.scrollTop = ta.scrollHeight; // 比輸入框高的一則：捲到游標所在的最後一行
    this.updateSend();
    return true;
  }

  // ───── @ 帳戶與檔案提示、/ 指令提示 ─────
  insertAt() {
    this.insertText('@');
    this.dismissedAt = -1;
    this.checkTrigger();
  }

  commands() { return (this.header && this.header.commands) || []; }

  checkTrigger() {
    const ta = this.ta;
    if (ta.selectionStart !== ta.selectionEnd) { this.closeSuggest(); return; }
    const pos = ta.selectionStart;
    const before = ta.value.slice(0, pos);
    const sm = /^\/([^\s/]*)$/.exec(before);
    if (sm && this.commands().length) {
      if (this.dismissedAt === 0) return;
      this.openCommands(sm[1]);
      return;
    }
    const am = /(^|[\s(（「『"'])@([^\s@"]*)$/.exec(before);
    if (am) {
      const start = pos - am[2].length - 1;
      if (this.dismissedAt === start) return;
      this.openFiles(am[2], start);
      return;
    }
    this.dismissedAt = -1;
    this.closeSuggest();
  }

  openCommands(q) {
    const ql = q.toLowerCase();
    const all = this.commands();
    const prefix = all.filter((c) => c.name.toLowerCase().startsWith(ql));
    const rest = ql ? all.filter((c) => !c.name.toLowerCase().startsWith(ql) && (c.name.toLowerCase().includes(ql) || (c.description || '').toLowerCase().includes(ql))) : [];
    const items = prefix.concat(rest).slice(0, 60).map((c) => ({ kind: 'cmd', value: c.name, main: '/' + c.name, sub: c.description || '', hint: c.argumentHint || '' }));
    if (!items.length) { this.closeSuggest(); return; }
    const sel = this.sugg && this.sugg.type === 'cmd' && this.sugg.query === q ? this.sugg.sel : 0;
    this.sugg = { type: 'cmd', start: 0, query: q, items, sel: Math.min(sel, items.length - 1), loading: false };
    this.renderSuggest();
  }

  openFiles(q, start) {
    // 〔遠端〕伺服器草稿只收設定類的指令（fileSearch 一定被拒）：不搜尋，說明送出第一則之後才能搜尋，Enter 照常送出（合併前審查 P2）
    if (isHub && (isDraftId(this.conv.id) || !!(this.header && this.header.isDraft))) {
      this.doSearch.cancel();
      this.searchReq = null;
      this.sugg = { type: 'file', start, query: q, items: [], sel: 0, loading: false, error: SEARCH_DRAFT_TEXT };
      this.renderSuggest();
      return;
    }
    const same = this.sugg && this.sugg.type === 'file' && this.sugg.start === start;
    if (!same || this.sugg.query !== q) {
      // @ AI 帳戶（帳戶委派，docs/account-delegation.md）：header.accountDelegation 為 true 時列在檔案前面；搜尋中先留著上一次的檔案結果
      const accountItems = isHub ? [] : accountSuggestions(state.accounts, this.header, q);
      const files = same ? this.sugg.items.filter((it) => it.kind !== 'account') : [];
      this.sugg = { type: 'file', start, query: q, items: accountItems.concat(files), accountItems, sel: 0, loading: true };
      this.doSearch(q);
    }
    this.renderSuggest();
  }

  /** 〔遠端〕fileSearch 等不到回覆（沒送出、被拒、結果不明）：還在等這一次的請求才改成說明 */
  searchFailed(req, text) {
    const s = this.sugg;
    if (!s || s.type !== 'file' || this.searchReq !== req || !s.loading) return;
    s.loading = false;
    s.items = s.accountItems || [];
    s.acceptFirst = false;
    s.error = text;
    this.renderSuggest();
  }

  /** 宿主回 fileSearch：只採用最新一次的請求 */
  applyFileSearch(msg) {
    if (!this.sugg || this.sugg.type !== 'file' || msg.requestId !== this.searchReq) return;
    this.sugg.loading = false;
    this.sugg.error = null;
    this.sugg.items = (this.sugg.accountItems || []).concat((msg.results || []).map((r) => ({
      kind: r.kind === 'dir' ? 'dir' : 'file', value: r.relativePath || r.fullPath, main: r.relativePath || r.fullPath, sub: '', full: r.fullPath,
    })));
    this.sugg.sel = 0;
    // 搜尋中就按了 Enter／Tab：結果一到就插入第一項（找不到就留著提示，原文不動）
    if (this.sugg.acceptFirst && this.sugg.items.length) { this.acceptSuggest(0); return; }
    this.sugg.acceptFirst = false;
    this.renderSuggest();
  }

  renderSuggest() {
    const s = this.sugg;
    if (!s) { this.closeSuggest(); return; }
    const box = this.suggest;
    box.textContent = '';
    box.dataset.type = s.type;
    if (!s.items.length) {
      const text = s.loading ? '搜尋檔案中…' : s.error ? s.error : (s.query ? '找不到符合「' + s.query + '」的檔案' : '輸入檔名開始搜尋');
      box.append(h('div', { class: 'sg-empty', dataset: s.error ? { field: 'searchError' } : null }, s.loading ? spinner() : null, h('span', { text })));
    }
    s.items.forEach((it, i) => {
      const row = h('div', { class: 'sg-row' + (i === s.sel ? ' sel' : ''), role: 'option', 'aria-selected': String(i === s.sel) });
      if (it.kind === 'cmd') {
        row.append(h('span', { class: 'sg-cmd' }, '/', highlight(it.value, s.query)));
        if (it.hint) row.append(h('span', { class: 'sg-hint', text: it.hint }));
        row.append(h('span', { class: 'sg-sub', text: it.sub }));
      } else if (it.kind === 'account') {
        // 帳戶：代表色圓點＋一般字型的名稱（檔案列是圖示＋等寬路徑），一眼分得出是交辦給帳戶還是引用檔案
        const dot = h('span', { class: 'sg-acc-dot', 'aria-hidden': 'true' });
        setAcc(dot, it.color, it.provider);
        row.append(dot, h('span', { class: 'sg-acc' }, highlight(it.main, s.query)));
        row.append(h('span', { class: 'sg-sub', text: it.sub }));
        // 右邊（第九批）：上下疊的小用量刻度（主要時間窗＋每週）與執行中的對話數，取當下的帳戶（fxrules.delegateMeta）
        const a = (state.accounts || []).find((x) => x.id === it.value);
        const meta = delegateMeta(a && a.usage && a.usage.windows, a ? sessionTotals(a).running : 0);
        if (meta.meters.length || meta.running) {
          const side = h('span', { class: 'sg-acc-meta', title: meta.title });
          if (meta.meters.length) {
            side.append(h('span', { class: 'ci-usage' }, ...meta.meters.map((m) => h('span', { class: 'ci-meter sev-' + m.sev, title: m.tip },
              ...Array.from({ length: 5 }, (_, k) => h('i', { class: k < m.lit ? 'lit' : '' }))))));
          }
          if (meta.running) side.append(h('span', { class: 'sg-acc-run', title: meta.running + ' 個對話執行中' }, h('i', { 'aria-hidden': 'true' }), h('span', { text: String(meta.running) })));
          row.append(side);
        }
      } else {
        row.append(icon(it.kind === 'dir' ? 'folder' : fileIconFor(it.main), 'sg-ic'));
        row.append(h('span', { class: 'sg-path' }, highlight(it.main, s.query)));
      }
      row.addEventListener('pointerenter', () => { if (s.sel !== i) { s.sel = i; this.markSel(); } });
      row.addEventListener('click', () => this.acceptSuggest(i));
      box.append(row);
    });
    box.append(h('div', { class: 'sg-foot', text: s.type === 'cmd' ? '↑↓ 選擇　Enter 插入指令　Esc 關閉' : s.error && !s.items.length ? (handheld() ? 'Esc 關閉' : 'Enter 送出訊息　Esc 關閉') : '↑↓ 選擇　Enter／Tab 插入帳戶或路徑　Esc 關閉' }));
    box.hidden = false;
    // 往上展開的高度不超過輸入框上緣到視窗頂端（留 8px）：2×2 上排窗格放不下 300px 時，最前面（已選取）的幾項不會跑到視窗外
    box.style.maxHeight = Math.max(80, Math.min(300, Math.floor(this.box.getBoundingClientRect().top) - 6 - 8)) + 'px';
    const selRow = box.querySelector('.sg-row.sel');
    if (selRow) selRow.scrollIntoView({ block: 'nearest' });
  }

  markSel() {
    const rows = this.suggest.querySelectorAll('.sg-row');
    rows.forEach((r, i) => { r.classList.toggle('sel', i === this.sugg.sel); r.setAttribute('aria-selected', String(i === this.sugg.sel)); });
  }

  acceptSuggest(i) {
    const s = this.sugg;
    if (!s || !s.items[i]) return;
    const it = s.items[i];
    const ta = this.ta;
    const pos = ta.selectionStart;
    let text;
    if (it.kind === 'cmd') text = '/' + it.value + ' ';
    else if (it.kind === 'account') text = accountMention(it) + ' ';
    else text = '@' + (/\s/.test(it.value) ? '"' + it.value + '"' : it.value) + ' ';
    ta.setRangeText(text, s.start, pos, 'end');
    this.closeSuggest();
    this.autosize();
    this.updateSend();
    this.focus();
  }

  closeSuggest() {
    this.suggest.hidden = true;
    this.sugg = null;
    this.doSearch.cancel();
  }

  // ───── 貼上與拖放 ─────
  onPaste(e) {
    const dt = e.clipboardData;
    const files = Array.from((dt && dt.files) || []);
    if (!files.length) return;
    // Excel、PowerPoint、OneNote 複製時，剪貼簿除了文字還有系統合成的點陣圖：有文字就照常貼上文字，不改成附加圖片。
    // 只有圖片（截圖、複製圖片）或檔案總管複製的檔案才附加
    if (files.every((f) => /^image\//.test(f.type) && f.name === CLIPBOARD_IMAGE_NAME) && (dt.getData('text/plain') || '').trim()) return;
    e.preventDefault();
    // 〔遠端〕瀏覽器版與桌面可以操作的副本：圖片與其他檔案一律上傳（瀏覽器版沒有 30 MB 的貼上限制，上限是 E4 的 512 MB）
    if (this.remoteUploads()) { this.uploadFiles(files); return; }
    const images = files.filter((f) => /^image\//.test(f.type));
    const others = files.filter((f) => !/^image\//.test(f.type));
    for (const f of images) {
      if (f.size > MAX_PASTE) { toast('貼上的圖片超過 30 MB，請改用迴紋針附加', 'error'); continue; }
      const r = new FileReader();
      r.onload = () => post({ cmd: 'pasteImage', conversationId: this.conv.id, dataUrl: r.result, name: f.name || '貼上的圖片.png' });
      r.readAsDataURL(f);
    }
    if (others.length) this.addFiles(others);
  }

  addFiles(files) {
    if (this.remoteUploads()) { this.uploadFiles(Array.from(files || [])); return; }
    if (!postWithFiles({ cmd: 'addFiles', conversationId: this.conv.id }, files)) {
      toast('這些檔案不在磁碟上（例如網頁或剪貼簿裡的），請先存成檔案再用迴紋針附加', 'error');
    }
  }

  // ───── 〔遠端〕瀏覽器版的上傳（E4）與附件晶片（D32） ─────

  /** 迴紋針：桌面請宿主開檔案對話框；瀏覽器版開隱藏的檔案選擇（閘門擋著時說明原因） */
  attach() {
    if (!this.remoteUploads()) {
      // 〔桌面遠端操作〕不能操作的副本：說明原因（宿主照舊擋下）
      const blocked = !isHub && this.isReplica() && this.conv.attachGate ? this.conv.attachGate() : null;
      if (blocked && !blocked.enabled) { toast(blocked.reason || '現在不能附加檔案', 'error'); return; }
      post({ cmd: 'attach', conversationId: this.conv.id });
      return;
    }
    const g = this.conv.attachGate ? this.conv.attachGate() : { enabled: true, reason: null };
    if (!g.enabled) { toast(g.reason || '現在不能附加檔案', 'error'); return; }
    this.fileInput.click();
  }

  /** 迴紋針的停用與說明（瀏覽器版：草稿、沒連線、電腦離線、政策；liverules.attachGate） */
  updateAttach() {
    this.voice.update(); // 語音訊息的閘門同附件（上傳的）
    if (!isHub && !this.isReplica()) return;
    const g = this.conv.attachGate ? this.conv.attachGate() : { enabled: true, reason: null };
    const title = g.enabled ? ATTACH_TITLE : g.reason || ATTACH_TITLE;
    if (this.attachBtn.disabled !== !g.enabled) this.attachBtn.disabled = !g.enabled;
    if (this.attachBtn.title !== title) {
      this.attachBtn.title = title;
      this.attachBtn.setAttribute('aria-label', g.enabled ? ATTACH_TITLE : '附加檔案：' + title);
    }
  }

  /** 已加進輸入區（電腦收下）的網頁上傳 */
  attachedUploads() {
    return this.uploads.filter((u) => u.state === 'attached');
  }

  /** 還在上傳或加進輸入區中（送出要等，D14 的「附件上傳中」） */
  uploading() {
    return this.uploads.some((u) => u.state === 'uploading' || u.state === 'adding');
  }

  /**
   * 上傳一批檔案：閘門擋著時說明、不上傳；太大的在本機就擋（失敗的晶片，沒有發出 PUT）、超過每則 20 個的不收（提示一次）；
   * 其他同時最多 3 個 PUT，全部結束後成功的那些一次送 addUploaded（任何一個失敗都在它的晶片上說明，成功的照樣送）
   */
  uploadFiles(files) {
    const list = Array.from(files || []);
    if (!list.length) return;
    const g = this.conv.attachGate ? this.conv.attachGate() : { enabled: true, reason: null };
    if (!g.enabled) { toast(g.reason || '現在不能附加檔案', 'error'); return; }
    const batch = uploadBatch(list, this.uploads.filter((u) => u.state !== 'error').length);
    for (const t of batch.tooLarge) this.uploads.push(this.newChip(t.file, 'error', t.reason));
    if (batch.overLimit) toast(UPLOAD_LIMIT_TEXT + '，有 ' + batch.overLimit + ' 個沒有加入', 'error');
    const chips = batch.accept.map((f) => this.newChip(f, 'uploading', null));
    this.uploads.push(...chips);
    this.renderUploads();
    this.updateSend();
    if (chips.length) this.runUploads(chips);
  }

  /**
   * 語音訊息的錄音（voicemsg.js）：上傳一個檔，同附件（瀏覽器版 E4，桌面操作其他電腦的對話由宿主代傳），但不做成晶片；
   * 回 { ok, entry }（entry 是 voiceMessage 的 uploads 的一筆）或 { ok: false, error }
   */
  async uploadVoice(file) {
    const uploadId = isHub ? newId() : peerId();
    if (!uploadId) return { ok: false, error: '上傳失敗（沒有上傳的 id）' };
    const r = isHub ? await uploadFile(uploadId, file, () => {}) : await hostUpload(uploadId, file, this.conv.id);
    const res = uploadResult(r.status, r.json, uploadId, file.name);
    return res.ok ? { ok: true, entry: res.entry } : { ok: false, error: res.error };
  }

  newChip(file, st, error) {
    const uploadId = st === 'uploading' ? (isHub ? newId() : peerId()) : null;
    const image = !!file && /^image\//.test(file.type || '');
    return {
      id: uploadId || 'local-' + (++localSeq), uploadId, file: st === 'uploading' ? file : null,
      name: uploadName(file && file.name, image ? '貼上的圖片.png' : '貼上的檔案'),
      size: file && typeof file.size === 'number' ? file.size : 0, state: uploadId || st !== 'uploading' ? st : 'error',
      loaded: 0, total: 0, error: uploadId || st !== 'uploading' ? error : '上傳失敗（沒有上傳的 id）', entry: null,
    };
  }

  async runUploads(chips) {
    const todo = chips.filter((c) => c.state === 'uploading');
    for (const c of todo) inFlight.add(c);
    let next = 0;
    const worker = async () => {
      while (next < todo.length) {
        const c = todo[next++];
        if (this.destroyed || c.state !== 'uploading') { inFlight.delete(c); continue; }
        // 取消（晶片的 ×、關掉分頁、伺服器關閉對話）時中止 PUT（cancelUploads）
        c.ctrl = typeof AbortController === 'function' ? new AbortController() : null;
        // 上傳本身丟例外（不該發生）也要讓晶片變成失敗：以前整個 worker 被拒絕，晶片一直停在上傳中（2026-11-13）
        const r = await this.uploadOne(c).catch(() => ({ ok: false, status: 0, json: null }));
        c.ctrl = null;
        c.file = null;
        if (this.destroyed || c.state !== 'uploading') { inFlight.delete(c); continue; }
        const res = uploadResult(r.status, r.json, c.uploadId, c.name);
        if (res.ok) {
          c.entry = res.entry;
          c.size = res.entry.size;
          c.state = 'adding';
        } else {
          c.state = 'error';
          c.error = res.error;
          inFlight.delete(c);
        }
        this.renderUploads();
      }
    };
    await Promise.all(Array.from({ length: Math.min(UPLOAD_CONCURRENCY, todo.length) }, () => worker()));
    if (this.destroyed) return;
    const ok = todo.filter((c) => c.state === 'adding');
    if (ok.length) this.addUploaded(ok);
    else this.updateSend();
  }

  /** 上傳一個晶片的檔：瀏覽器版 E4、桌面由宿主代傳；回 { ok, status, json }（async：同步丟出的例外也變成被拒絕的 Promise） */
  async uploadOne(c) {
    if (!isHub) return hostUpload(c.uploadId, c.file, this.conv.id, c.ctrl ? c.ctrl.signal : undefined);
    return uploadFile(c.uploadId, c.file, (loaded, total) => {
      // 上傳速度：和上一次記下的進度相隔 0.2 秒以上才算一次（fxrules.byteRate），再和前一次的平均（晶片上的讀數）
      const at = performance.now();
      const rate = byteRate(c.rateMark, { bytes: loaded, at });
      if (rate !== null || !c.rateMark) c.rateMark = { bytes: loaded, at };
      if (rate !== null) c.bps = smoothRate(c.bps, rate);
      c.loaded = loaded;
      c.total = total;
      if (!this.destroyed) this.renderUploads();
    }, c.ctrl ? c.ctrl.signal : undefined);
  }

  /**
   * 送 addUploaded {conversationId, uploads}（size、sha256 用伺服器回的）並追蹤：done（電腦收下，下載與匯入在背景）或不明（可能已收下）
   * → 已附加；被拒或沒送出 → 失敗的晶片（原因看伺服器或傳輸層的提示，網頁不另外跳提示）
   */
  addUploaded(chips) {
    const settle = (st) => {
      for (const c of chips) {
        inFlight.delete(c);
        if (c.state !== 'adding') continue;
        if (st === 'done' || st === 'unknown') c.state = 'attached';
        else { c.state = 'error'; c.error = NOT_ADDED_TEXT; }
      }
      if (this.destroyed) return;
      this.saveUploads();
      this.renderUploads();
      this.updateSend();
    };
    const cid = post({ cmd: 'addUploaded', conversationId: this.conv.id, uploads: chips.map((c) => c.entry) });
    if (typeof cid !== 'string' || !this.conv.ctx.track || !this.conv.ctx.track(cid, { onSettle: (st) => settle(st) })) settle(typeof cid === 'string' ? 'unknown' : 'rejected');
    this.updateSend();
  }

  /** send 的結果（D32）：done 清掉這次送出的晶片（chipsAfter），其他（被拒、不明）保留、不另外說明 */
  uploadsAfterSend(sent, st) {
    if (!sent.length) return;
    const left = chipsAfter(sent, st === 'done' ? 'sendDone' : 'sendRejected');
    const gone = sent.filter((c) => !left.includes(c));
    if (!gone.length) return;
    this.uploads = this.uploads.filter((u) => !gone.includes(u));
    if (this.destroyed) { this.saveUploads(); return; }
    this.saveUploads();
    this.renderUploads();
    this.updateSend();
  }

  /** 〔遠端〕伺服器關閉了這個對話（conversationClosed，D32）：還在上傳的中止、晶片清空、sessionStorage 的也刪 */
  uploadsClosed() {
    this.cancelUploads(this.uploads);
    for (const c of this.uploads) inFlight.delete(c);
    this.uploads = chipsAfter(this.uploads, 'closed');
    this.saveUploads();
    this.renderUploads();
  }

  /** 已附加的晶片存進 sessionStorage（myriagent.app.uploaded.v1:{conversationId}）；不能用時略過（重新載入後看不到，6.6 的限制） */
  saveUploads() {
    if (!isHub || !isConversationId(this.conv.id)) return;
    const list = this.attachedUploads().map((c) => c.entry);
    try {
      const key = uploadsKey(this.conv.id);
      if (list.length) window.sessionStorage.setItem(key, JSON.stringify(list));
      else window.sessionStorage.removeItem(key);
    } catch { /* 儲存不能用 */ }
  }

  /** 重新載入後還原已附加的晶片（讀不到、內容不合格都當成沒有） */
  restoreUploads() {
    if (!isHub || !isConversationId(this.conv.id)) return;
    let raw = null;
    try {
      raw = window.sessionStorage.getItem(uploadsKey(this.conv.id));
    } catch {
      raw = null;
    }
    for (const e of storedUploads(raw)) {
      this.uploads.push({ id: e.uploadId, uploadId: e.uploadId, file: null, name: e.name, size: e.size, state: 'attached', loaded: 0, total: 0, error: null, entry: e });
    }
    this.renderUploads();
  }

  /**
   * 〔遠端〕取消上傳（合併前審查 PERF-2）：上傳中的中止 PUT（伺服器立刻釋放預留）、加入中的不再送 addUploaded；
   * 兩者都改成失敗的晶片（已送出的 addUploaded 回來時 settle 只處理還在加入中的）
   */
  cancelUploads(list) {
    for (const c of list) {
      if (c.state !== 'uploading' && c.state !== 'adding') continue;
      c.state = 'error';
      c.error = UPLOAD_CANCELLED_TEXT;
      c.file = null;
      if (c.ctrl) { try { c.ctrl.abort(); } catch { /* 忽略 */ } }
      inFlight.delete(c);
    }
  }

  /** 拿掉一個晶片：失敗的直接拿掉、上傳中的先取消（已附加的沒有移除鈕：電腦那邊沒有對應的移除指令） */
  dismissUpload(c) {
    if (c.state === 'uploading') this.cancelUploads([c]);
    else if (c.state !== 'error') return;
    this.uploads = this.uploads.filter((u) => u !== c);
    this.renderUploads();
    this.updateSend();
  }

  renderUploads() {
    this.upChips.hidden = !this.uploads.length;
    reconcile(this.upChips, this.uploads, (c) => c.id, (c) => uploadChip(c, () => this.dismissUpload(c)), updateUploadChip);
  }
}

/** 〔遠端〕一個網頁上傳的晶片：副檔名、名稱、狀態（上傳中的百分比、加入中、大小、失敗的原因）；失敗的才有移除鈕 */
function uploadChip(c, onDismiss) {
  const sub = h('span', { class: 'att-size up-sub' });
  const fill = h('span', { class: 'up-fill' });
  const tile = h('div', { class: 'att att-doc up-tile' },
    h('span', { class: 'att-ext', text: (extOf(c.name) || 'file').slice(0, 4).toUpperCase() }),
    h('span', { class: 'att-meta' }, h('span', { class: 'att-name', text: c.name }), sub),
    h('span', { class: 'up-bar', 'aria-hidden': 'true' }, fill));
  const x = iconButton('x', DISMISS_TITLE, () => onDismiss(), 'chip-x');
  const wrap = h('div', { class: 'chip-att up-chip' }, tile, x);
  wrap._sub = sub;
  wrap._fill = fill;
  wrap._x = x;
  wrap._tile = tile;
  updateUploadChip(wrap, c);
  return wrap;
}

function updateUploadChip(wrap, c) {
  // 上傳完成、附加進輸入框的那一下：外框閃一下綠（fx.css 的 .up-chip.fx-up-done）
  if (c.state === 'attached' && (wrap.dataset.state === 'uploading' || wrap.dataset.state === 'adding')) replayClass(wrap, 'fx-up-done', 800);
  if (wrap.dataset.state !== c.state) wrap.dataset.state = c.state;
  const pct = c.total > 0 ? Math.min(100, Math.floor((c.loaded / c.total) * 100)) : 0;
  // 上傳中：「3.2／12 MB・1.1 MB/s」（fxrules.uploadReadout；還沒有進度時照舊寫百分比）
  const sub = c.state === 'uploading' ? (c.loaded > 0 && uploadReadout(c.loaded, c.total, c.bps)) || '上傳中 ' + pct + '%'
    : c.state === 'adding' ? '加入輸入區…' : c.state === 'error' ? c.error || '上傳失敗' : fmtBytes(c.size);
  setText(wrap._sub, sub);
  if (c.state === 'error') wrap._sub.setAttribute('role', 'alert'); else wrap._sub.removeAttribute('role');
  // 進度條只伸縮（transform，交給合成器；以前改寬度，每次進度都要重排版面）
  const frac = c.state === 'uploading' ? (c.total > 0 ? Math.min(1, c.loaded / c.total) : 0) : c.state === 'adding' ? 1 : 0;
  const fill = 'scaleX(' + frac.toFixed(3) + ')';
  if (wrap._fill.style.transform !== fill) wrap._fill.style.transform = fill;
  // 失敗的拿掉、上傳中的取消（合併前審查 PERF-2）；加入中與已附加的沒有 ×
  wrap._x.hidden = !(c.state === 'error' || c.state === 'uploading');
  const xTitle = c.state === 'uploading' ? CANCEL_UPLOAD_TITLE : DISMISS_TITLE;
  if (wrap._x.title !== xTitle) { wrap._x.title = xTitle; wrap._x.setAttribute('aria-label', xTitle); }
  const title = c.name + '（' + sub + '）';
  if (wrap._tile.title !== title) wrap._tile.title = title;
}

/** 只在文字改變時才寫入（同樣的內容不動 DOM） */
const SVG_NS = 'http://www.w3.org/2000/svg';
function svgEl(tag, attrs) {
  const el = document.createElementNS(SVG_NS, tag);
  for (const k in attrs) el.setAttribute(k, attrs[k]);
  return el;
}

function setText(el, text) {
  if (el.textContent !== text) el.textContent = text;
}

/** 換圖示：名稱沒變就保留原節點；el 為 null 時只建立新的（呼叫端負責放進 DOM） */
function syncIcon(el, name, cls) {
  if (el && el._icon === name) return el;
  const next = icon(name, cls);
  next._icon = name;
  if (el) el.replaceWith(next);
  return next;
}

function updateQueueRow(row, m) {
  const t = row.firstElementChild;
  const shown = mentionPlain(m.text);
  setText(t, shown || '（附件）');
  if (t.title !== shown) t.title = shown;
}

export function toneColor(tone) {
  return { neutral: 'var(--tone-neutral)', edit: 'var(--tone-edit)', plan: 'var(--tone-plan)', auto: 'var(--tone-auto)', danger: 'var(--tone-danger)' }[tone] || 'var(--tone-neutral)';
}

function fileIconFor(path) {
  const ext = (String(path).split('.').pop() || '').toLowerCase();
  if (/^(png|jpe?g|gif|webp|bmp|svg|ico|avif)$/.test(ext)) return FILE_ICON.image;
  if (/^(mp4|mov|webm|mkv|avi|m4v)$/.test(ext)) return FILE_ICON.video;
  if (/^(mp3|wav|flac|m4a|ogg)$/.test(ext)) return FILE_ICON.audio;
  if (/^(zip|7z|rar|gz|tar)$/.test(ext)) return FILE_ICON.archive;
  if (/^(md|txt|pdf|docx?|xlsx?|pptx?|csv)$/.test(ext)) return FILE_ICON.doc;
  if (/^(js|mjs|ts|tsx|jsx|cs|py|json|css|html|xml|yml|yaml|sql|sh|ps1|go|rs|java|cpp|c|h)$/.test(ext)) return FILE_ICON.code;
  return FILE_ICON.file;
}

/** 完全自動的確認：回傳 'ok'｜'skip'（循環時略過）｜'cancel'（設定頁「模型設定」也用） */
export function confirmFullAccess(mode, cycling) {
  return new Promise((resolve) => {
    let result = 'cancel';
    const body = h('div', { class: 'confirm' },
      h('p', { class: 'confirm-msg', text: '「' + (mode.label || mode.value) + '」會讓代理不經詢問就執行指令、修改或刪除檔案。' }),
      h('p', { class: 'confirm-detail', text: mode.description || '只在你清楚代理要做什麼、而且工作資料夾有備份時使用。' }));
    const other = h('button', { type: 'button', class: 'btn', text: cycling ? '略過這個模式' : '取消' });
    const ok = h('button', { type: 'button', class: 'btn danger-solid', text: '切換為' + (mode.label || '完全自動') });
    const d = openDialog({ title: '切換到' + (mode.label || '完全自動') + '？', body, footer: [other, ok], className: 'dlg-confirm', width: 440, initialFocus: other, onClose: () => resolve(result) });
    other.addEventListener('click', () => { result = cycling ? 'skip' : 'cancel'; d.close(); });
    ok.addEventListener('click', () => { result = 'ok'; d.close(); });
  });
}
