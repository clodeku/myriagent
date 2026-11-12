// 左側欄：帳戶精簡卡（佔兩行的用量圓環、執行中數、今日 token 計數、拖曳排序）＋對話歷史（分組、右鍵選單；清單下方、側欄最左下角是搜尋框、「全部」與篩選下拉）
// 手機版（瀏覽器版的窄螢幕）與平板（2026-10-24 使用者要求；app.js 的 setAccountsFoldable）：帳戶區預設收起、只留對話清單，
// 點「帳戶」展開或收起；收起時標題旁是執行中、等你回應的合計。
// 在清單點了對話或開新對話時送 sidebar-picked（app.js 收起抽屜）
// 搜尋：標題在網頁即時比對；桌面另外請宿主搜內容（searchConversations），結果回來後補上只有內容符合的對話，並在標題下顯示符合處的片段
// 〔同步〕（WP3.5）：連結到目錄的帳戶另有各電腦的登入小點；remoteOnly 帳戶（目錄裡有、這台沒有）的卡是灰的，
// 點擊、Enter、右鍵都只開「在這台登入」，沒有＋按鈕、不能拖曳，也不出現在帳戶篩選與拖曳排序的 ids 裡
import { h, iconButton, reconcile, setAcc, setPulse, setTempo, safeColor, pctText, fmtSpanShort, fmtReset, fmtRelative, fmtDateTime, replayClass, setHidden } from './util.js';
import { gaugeFx, titleDecodeFx, listEntranceCount, LIST_STAGGER_MS, quotaLocked, accountPulse, pulsePhase } from './fxrules.js';
import { icon } from './icons.js';
import { post, state, accountOf, STATUS_LABEL, isHub, isCollaborator, on, emit } from './store.js';
import { openMenu, spinner } from './ui.js';
import { confirmDeleteConversation } from './conversation.js';
import { deviceStrip, deviceStripSig } from './remote.js';
import { remoteOnlyElsewhere, sessionTotals, sessionTitle, programsTitle, subagentsTitle, subagentsTooltip } from './remoterules.js';
import { shareBadge } from './collabrules.js';
import { deviceOf, originOf, filterHistory, deviceFilterChoices, folderFilterChoices, filterFolder, folderCounts, backgroundWaiting, nestFamilies } from './liverules.js';
import { tokenCounter } from './tokenfx.js';
import { setDecodedText } from './textfx.js';
import { tokensOf, tokenSplit, showTokens, tokensTitle, fmtShort, fmtTokens, MATRIX_GLYPHS } from './tokenrules.js';
import { searchTerms, searchKey, filterConversations, hitsFrom, markParts, MAX_QUERY } from './searchrules.js';
import { loginExpiry, loginExpiryText } from './loginrules.js';
import { sidebarWindows } from './accountrules.js';
import { openImportDialog } from './importconv.js';
import { isFlashing } from './attnflash.js';

const isRemoteOnly = (a) => !!a && a.remoteOnly === true;

let ws = null;          // workspace API
let dash = null;        // dashboard API（新增帳戶對話框）
let els = null;
let renaming = null;    // 正在重新命名的對話 id
let historyStale = false; // 改名期間、按著清單時有清單更新，改名結束、放開後再重畫
let pressing = false;   // 滑鼠或手指按在對話清單上（holdList）
let pressTimer = 0;
const PRESS_HOLD_MAX = 3000; // 按著清單最多這麼久不重排（放開的事件萬一沒到，清單也不會一直停在舊的）
let foldable = false;   // 帳戶區可以收起：手機版、平板（setAccountsFoldable）
let acctsOpen = false;  // 可以收起時帳戶區展開了沒（只記在這一頁；每次變成可以收起都從收起開始）

// 內容搜尋（桌面問中樞、瀏覽器版問伺服器）：key＝目前查詢的 searchKey、hits＝這個 key 的宿主結果（null＝還沒來）、
// reqId／reqKey＝最後送出的請求與它的 key（回覆的 requestId 不是它就丟掉）、pending＝這個 key 的第一次結果還沒來、
// note＝瀏覽器版沒能搜內容的原因（'offline' 沒有連線、'noReply' 等不到回覆；這時只比對標題，重連的 init 再搜一次）
const SEARCH_DELAY = 250;       // 打字停下多久才請宿主搜
const SEARCH_REFRESH = 1500;    // 清單有變（新的訊息、改名）時，搜尋中的結果最快多久重搜一次
const SEARCH_NO_REPLY = 20000;  // 瀏覽器版：送出後這麼久還沒有結果，就當成等不到（回覆只送給發出的連線，斷線重連就收不到）
const content = { key: '', hits: null, reqId: null, reqKey: '', pending: false, note: null, timer: 0, refresh: 0, wait: 0, seq: 0 };
const NOTE_TEXT = { offline: '目前沒有連線，只比對了標題；連上之後會再搜內容', noReply: '沒有收到伺服器的回覆，只比對了標題' };

export function initSidebar(root, api) {
  ws = api.workspace;
  dash = api.dashboard;
  const accList = h('div', { class: 'sb-accts' });
  const addBtn = h('button', { type: 'button', class: 'sb-add' }, icon('plus'), h('span', { text: '新增帳戶' }));
  addBtn.addEventListener('click', () => dash.openAddAccount());
  const searchHint = '搜尋對話的標題與內容';
  const search = h('input', { type: 'search', class: 'input sb-search-input', placeholder: '搜尋標題與內容', autocomplete: 'off', spellcheck: 'false', 'aria-label': searchHint, title: searchHint });
  search.addEventListener('input', () => { state.search = search.value; searchChanged(); });
  search.addEventListener('keydown', (e) => { if (e.key === 'Escape' && search.value) { e.stopPropagation(); search.value = ''; state.search = ''; searchChanged(); } });
  // 帳戶、電腦與工作區的篩選：各一個下拉（選項多時不會把側欄塞滿），只有一個選項可選時不顯示那一個；一排最多兩個
  const acctPick = filterPick('historyAccount', 'user', '依帳戶篩選對話', openAccountFilter);
  const devPick = filterPick('historyDevice', 'desktop', '依電腦篩選對話', openDeviceFilter);
  const folderPick = filterPick('historyFolder', 'folder', '依工作區篩選對話', openFolderFilter);
  const filters = h('div', { class: 'sb-filters', hidden: true }, acctPick, devPick, folderPick);
  // 「全部」：一次清掉三個篩選與搜尋；在搜尋框右邊（不讓篩選下拉多佔一排），有篩選下拉時才出現，沒有篩選時是按下的樣子
  const allBtn = h('button', { type: 'button', class: 'sb-all', hidden: true, 'aria-pressed': 'true', text: '全部' });
  allBtn.addEventListener('click', showAll);
  // 搜尋時找到幾個對話（搜尋框裡、全部鈕的左邊）：數字變了跳一下
  const hitCount = h('span', { class: 'sb-count', hidden: true, role: 'status', 'aria-live': 'polite' });
  const list = h('div', { class: 'sb-list', role: 'list' });
  const empty = h('div', { class: 'sb-empty', hidden: true });
  // 內容搜尋的狀態：結果還沒來時「搜尋內容中…」（轉圈）；瀏覽器版沒能搜內容時說明原因（不轉圈）
  const searchingSpin = spinner('sb-searching-spin');
  const searchingText = h('span', { text: '搜尋內容中…' });
  const searching = h('div', { class: 'sb-searching', hidden: true, role: 'status' }, searchingSpin, searchingText);
  // 〔遠端〕瀏覽器版沒有「重新檢查所有帳戶」（沒有電腦的指令伺服器一律不接受，各台的重新檢查在儀表板）與「新增帳戶」（同儀表板）
  // 手機版與平板的「帳戶」是展開／收起的按鈕（整列都按得到），收起時旁邊是執行中、等你回應的合計；其他時候是一般的標題
  const accTitle = h('span', { class: 'sb-title', text: '帳戶' });
  const accSum = h('span', { class: 'sb-fold-sum' });
  accList.id = 'sbAccts';
  const accFold = h('button', { type: 'button', class: 'sb-fold', hidden: true, 'aria-controls': 'sbAccts', 'aria-expanded': 'false' },
    h('span', { class: 'sb-title', text: '帳戶' }), accSum, icon('chevDown', 'sb-fold-chev'));
  accFold.addEventListener('click', () => { acctsOpen = !acctsOpen; applyAcctsFold(); });
  const accSec = h('section', { class: 'sb-sec sb-sec-accts owner-only' },
    h('div', { class: 'sb-head' }, accTitle, accFold,
      isHub ? null : iconButton('refresh', '重新檢查所有帳戶的登入與用量', () => post({ cmd: 'accountRefresh', accountId: null }), 'sm')),
    accList, isHub ? null : addBtn);
  // 搜尋與篩選放在清單下方、側欄的最左下角（2026-10-23 使用者要求；手機版拇指搆得到）：搜尋框與「全部」在上、篩選下拉在最下面，選單往上開
  const histSec = h('section', { class: 'sb-sec sb-sec-hist' },
    h('div', { class: 'sb-head' }, h('span', { class: 'sb-title', text: '對話' }),
      isHub ? null : iconButton('download', '匯入本機 CLI 歷史對話', () => openImportDialog(ws), 'sm'),
      iconButton('plus', '新對話（Ctrl+N）', newFromSidebar, 'sm owner-only')),
    h('div', { class: 'sb-scroll' }, list, empty, searching),
    h('div', { class: 'sb-tools' }, h('div', { class: 'sb-search' }, icon('search', 'sb-search-ic'), search, hitCount, allBtn), filters));
  root.append(accSec, histSec);
  els = { root, accSec, accList, accTitle, accFold, accSum, addBtn: isHub ? null : addBtn, list, filters, acctPick, devPick, folderPick, allBtn, search, hitCount, empty, searching, searchingSpin, searchingText, dev: { choices: [], value: null }, folder: { choices: [], value: null } };
  // 按著清單的期間不重排：按下到放開之間清單重排（這一輪結束、對話跳到最上面），按著的那一列被搬走或換了位置，
  // Chromium 就不送 click，要點第二下才切得過去（2026-10-26 使用者回報）。放開的事件之後才送 click，等它送完再補畫
  list.addEventListener('pointerdown', (ev) => { if (ev.isPrimary) holdList(true); });
  const release = () => { if (pressing) setTimeout(() => holdList(false), 0); };
  window.addEventListener('pointerup', release, true);
  window.addEventListener('pointercancel', release, true);
  window.addEventListener('blur', release);
  bindAccountDrag(accList);
  // 〔同步〕電腦改名、上下線：清單的電腦徽章跟著更新（只在名稱、在線或「重新連線中」變了時重畫；sync 的推送很頻繁）
  let devSig = '';
  on('remote-changed', () => {
    const list = state.remote && Array.isArray(state.remote.devices) ? state.remote.devices : [];
    const sig = JSON.stringify(list.map((d) => d && [d.id, d.name, d.online, d.reconnecting === true]));
    if (sig !== devSig) { devSig = sig; renderHistory(); }
  });
}

// ───── 帳戶精簡卡 ─────
export function renderAccounts() {
  if (!els) return;
  reconcile(els.accList, state.accounts, (a) => a.id, createMiniCard, updateMiniCard);
  updateAccountPick();
  renderFoldSummary();
  markAnyRunning();
}

/** 帳戶區可不可以收起（手機版、平板；app.js 的 applyChrome 每次都呼叫）：變成可以收起時從收起開始，變回不行時照常顯示 */
export function setAccountsFoldable(on) {
  if (!els || foldable === !!on) return;
  foldable = !!on;
  acctsOpen = false;
  applyAcctsFold();
}

function applyAcctsFold() {
  const folded = foldable && !acctsOpen;
  els.accTitle.hidden = foldable;
  els.accFold.hidden = !foldable;
  els.accFold.setAttribute('aria-expanded', String(!folded));
  els.accFold.title = folded ? '顯示帳戶' : '收起帳戶';
  els.accSec.classList.toggle('foldable', foldable);
  els.accSec.classList.toggle('folded', folded);
  els.accList.hidden = folded;
  if (els.addBtn) els.addBtn.hidden = folded; // 桌面在平板上：「新增帳戶」跟著帳戶清單收起
  renderFoldSummary();
}

/** 收起的帳戶區標題旁的合計：帳戶數、執行中、等背景工作回報、等你回應、背景程式（同帳戶卡的算法，含其他在線電腦的） */
function renderFoldSummary() {
  if (!els || !foldable) return;
  let running = 0;
  let waiting = 0;
  let background = 0;
  let programs = 0;
  let subagents = 0;
  for (const a of state.accounts) {
    const s = sessionTotals(a);
    running += s.running;
    waiting += s.waiting;
    background += s.background;
    programs += s.programs;
    subagents += s.subagents;
  }
  const n = state.accounts.length;
  const sig = n + '|' + running + '|' + waiting + '|' + background + '|' + programs + '|' + subagents;
  if (els.accSum._sig === sig) return;
  els.accSum._sig = sig;
  els.accSum.textContent = '';
  els.accSum.append(h('span', { class: 'sb-fold-n', text: String(n) }));
  if (running) els.accSum.append(h('span', { class: 'macct-run', title: running + ' 個對話執行中' }, spinner('macct-spin'), h('span', { text: String(running) })));
  if (subagents) els.accSum.append(h('span', { class: 'macct-agents', title: subagentsTitle(subagents, 0) }, icon('bot'), h('span', { text: String(subagents) })));
  if (background) els.accSum.append(h('span', { class: 'macct-bg', title: background + ' 個對話等背景工作回報' }, h('span', { class: 'bg-ring macct-bg-ring', 'aria-hidden': 'true' }), h('span', { text: String(background) })));
  if (waiting) els.accSum.append(h('span', { class: 'macct-wait', title: waiting + ' 個對話等待你回應' }, h('span', { text: String(waiting) })));
  if (programs) els.accSum.append(h('span', { class: 'macct-prog', title: programs + ' 個對話的背景程式還在跑' }, icon('terminal'), h('span', { text: String(programs) })));
}

/**
 * 頂部列品牌圖示外圈的繞圈（fx.css 的 .app.acct-running）：有任何帳戶卡顯示「執行中」時亮。
 * 以前寫成 .app:has(.sidebar .macct-run:not([hidden]))——掛在最外層的 :has() 讓畫面上任何變動（串流的文字、按鈕的顯示隱藏）
 * 都要重新檢查，好幾個對話同時串流時樣式重算多一倍以上（2026-10-23 壓力探針）。帳戶卡的膠囊只在 updateMiniCard 改，所以由 renderAccounts 切換。
 * 只看帳戶卡、不看收起時的合計：合計只在可以收起時重畫，離開手機版後留著舊的「執行中」膠囊（展開鈕隱藏、膠囊本身沒有 hidden），
 * 以前的選擇器因此在對話都停了之後照樣轉圈（2026-10-24 修正；合計是帳戶卡的加總，帳戶區收起時卡片的膠囊照樣更新）
 */
function markAnyRunning() {
  const app = els.root.closest('.app');
  if (app) app.classList.toggle('acct-running', !!els.accList.querySelector('.macct-run:not([hidden])'));
  // 頂部品牌圖示外圈的方塊環：全部帳戶執行中的對話越多轉越快（同帳戶卡的 accountPulse，換速時保住進度）
  const mark = app && app.querySelector('.brand-mark');
  if (mark) setTempo(mark, accountPulse('ready', false, state.accounts.reduce((n, a) => n + sessionTotals(a).running, 0)).tempo);
}

const BADGE_FX_MS = 520; // 徽章出現時彈出一下（fx.css 的 .fx-badge-in）

// 帳戶卡每次 accounts、tokens 訊息都整張更新：值沒變不寫。卡片有 `.macct:has(.macct-run:not([hidden]))` 的規則，
// 以 textContent 換掉文字節點（值一樣也換）、重寫 title 與 draggable 都會讓 Chrome 重新判斷 :has() 並重算卡片裡的元素，
// 用量圓環正在跑的動畫還會在合成器上被重開一次、重開之後又多畫一格
const setTitle = (el, v) => { if (el.title !== v) el.title = v; };
const setText = (el, v) => { if (el.textContent !== v) el.textContent = v; };

// ───── 帳戶名稱的跑馬燈（同標題列的 fitHeader）─────
// 名稱的框變寬變窄（滑過時出現＋、執行中與等你回應的膠囊、背景程式的徽章、登入到期的標示、側欄拉寬、手機版展開）時 ResizeObserver 通知，
// 名稱改了由 updateMiniCard 通知；都在下一影格一起量（ResizeObserver 回呼裡改 class 可能觸發 ResizeObserver loop 錯誤）
let nameObs = null;
const namePending = new Set();
let nameFrame = 0;

function scheduleFitName(box) {
  namePending.add(box);
  if (!nameFrame) nameFrame = requestAnimationFrame(fitNames);
}

/**
 * 名稱放不下時改成跑馬燈：在框裡來回捲動、兩端停一下，捲動的距離與時間照超出的寬度算（每秒約 30px），放得下就照常顯示。
 * 先全部量完再改 class（不逐張強制排版）；動畫只在 CSS 允許動態時才有（減少動態、設定關掉動態效果時照舊以「…」截斷）
 */
function fitNames() {
  nameFrame = 0;
  const boxes = Array.from(namePending);
  namePending.clear();
  const overs = boxes.map((box) => (box.isConnected && box.clientWidth ? box.firstElementChild.offsetWidth - box.clientWidth : 0));
  boxes.forEach((box, i) => {
    if (!box.isConnected) { nameObs.unobserve(box); return; } // 帳戶移除了（reconcile 拿掉卡片）
    const over = overs[i];
    const on = over > 1;
    box.classList.toggle('marquee', on);
    if (!on) return;
    box.style.setProperty('--mq-dist', -Math.ceil(over) + 'px');
    box.style.setProperty('--mq-dur', Math.max(5, over / 21).toFixed(1) + 's');
  });
}

function createMiniCard(a) {
  const card = h('div', { class: 'macct', role: 'button', tabIndex: 0, draggable: true });
  // .macct-name 是裁切的框，名稱在裡面的 .macct-name-run（跑馬燈捲動的是它）
  const nameRun = h('span', { class: 'macct-name-run' });
  const name = h('span', { class: 'macct-name' }, nameRun);
  if (!nameObs) nameObs = new ResizeObserver((ents) => { for (const en of ents) scheduleFitName(en.target); });
  nameObs.observe(name);
  // 執行中、等背景工作回報、背景程式的對話數用側欄同款的計數器（tokenfx.js）：數字變了先閃過亂碼再鎖定
  const runN = tokenCounter(String), bgN = tokenCounter(String), progN = tokenCounter(String), agentsN = tokenCounter(String);
  const run = h('span', { class: 'macct-run', hidden: true }, spinner('macct-spin'), h('span', { class: 'macct-run-n' }, runN.num));
  // 執行中的子代理（2026-10-28）：個數不是對話數，緊接在執行中後面；同工具卡的子代理圖示
  const agents = h('span', { class: 'macct-agents', hidden: true }, icon('bot'), h('span', { class: 'macct-agents-n' }, agentsN.num));
  // 等背景工作回報（代理這一輪已回覆完畢、背景工作還在跑）：虛線圈＋對話數，和分頁、對話列的樣貌一致
  const bg = h('span', { class: 'macct-bg', hidden: true }, h('span', { class: 'bg-ring macct-bg-ring', 'aria-hidden': 'true' }), h('span', { class: 'macct-bg-n' }, bgN.num));
  const wait = h('span', { class: 'macct-wait', hidden: true, title: '等待你回應' }, h('span', { text: '!' }));
  // 背景程式（2026-10-24）：這一輪結束了、但代理開的程式脫離 CLI 還在跑的對話數（CLI 不會回報、跑完也不叫回代理，和等背景工作回報分開）；
  // 靜態的終端機圖示，滑過列出對話與程式
  const prog = h('span', { class: 'macct-prog', hidden: true }, icon('terminal'), h('span', { class: 'macct-prog-n' }, progN.num));
  const newBtn = iconButton('plus', '用這個帳戶開新對話', (e) => { e.stopPropagation(); ws.newConversation(card.dataset.key); emit('sidebar-picked'); }, 'macct-new sm');
  // 用量圓環在卡片右側，佔名稱與狀態兩行的高度
  const meters = h('span', { class: 'macct-meters' });
  const stale = h('span', { class: 'macct-stale', hidden: true });
  // 〔同步〕各電腦的登入小點（devices 是陣列的帳戶才有）
  const devs = h('span', { class: 'macct-devs', hidden: true });
  // 今日 token 計數（宿主的 tokens 訊息；瀏覽器版是各電腦的合計）放在狀態那一行、取代方案名稱（方案名稱在滑過卡片的提示裡）：
  // in（輸入，含快取）與 out（輸出）分開，精簡格式（1.264M）；精確值與快取讀取在滑過的說明。
  // 同一行還有過時標示、各電腦小點而放不下標籤時，CSS（容器查詢）收起 in／out 標籤、改用斜線隔開
  // 今日 token：帳戶色系的計數器（fx.css 由卡片的 --acc 算出 --tk，out 是主色、in 退一階——掛 .mtok-in；亂碼是駭客任務風格的半形片假名）
  const tokIn = tokenCounter(fmtShort, null, MATRIX_GLYPHS);
  const tokOut = tokenCounter(fmtShort, null, MATRIX_GLYPHS);
  const tok = h('span', { class: 'macct-tok', hidden: true },
    h('span', { class: 'mtok-tag mtok-in', text: 'in' }), h('span', { class: 'mtok-v mtok-in' }, tokIn.num),
    h('span', { class: 'mtok-sep', text: '/', 'aria-hidden': 'true' }),
    h('span', { class: 'mtok-tag mtok-out', text: 'out' }), h('span', { class: 'mtok-v' }, tokOut.num));
  const subText = h('span', { class: 'macct-sub-text' });
  // 登入即將到期（剩 3 天內，loginrules.js）：名稱那一行的琥珀色小膠囊（登入圖示＋剩餘時間），已到期是紅色；點一下重新登入（不切換篩選）。
  // 放在名稱那一行（名稱會省略）而不是狀態那一行：那一行已經有 token 數、過時的時鐘與裝置小點，再放會把 token 數擠到裁切
  const login = h('button', { type: 'button', class: 'macct-login', hidden: true });
  login.addEventListener('click', (ev) => {
    ev.stopPropagation();
    const acc = accountOf(card.dataset.key);
    if (acc && !acc.loginBusy) post({ cmd: 'accountLogin', accountId: acc.id });
  });
  const sub = h('span', { class: 'macct-sub' }, h('span', { class: 'macct-dot' }), subText, tok, stale, devs);
  const top = h('span', { class: 'macct-top' }, name, run, agents, bg, wait, prog, login, newBtn);
  card.append(h('span', { class: 'macct-bar' }), top, sub, meters);
  card._els = { name, nameRun, run, agents, bg, wait, prog, runN, bgN, progN, agentsN, sub, subText, meters, stale, devs, top, newBtn, tok, tokIn, tokOut, login };
  // remoteOnly 卡：點擊與 Enter 開「在這台登入」（沒有對話可以篩選）；本機帳戶照舊切換篩選
  const activate = () => {
    const acc = accountOf(card.dataset.key);
    if (isRemoteOnly(acc)) dash.accountMenu(acc, card);
    else toggleFilter(card.dataset.key);
  };
  card.addEventListener('click', activate);
  card.addEventListener('keydown', (e) => {
    if (e.target !== card) return; // 卡片內「＋」按鈕的 Enter／空白鍵交給按鈕自己
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); activate(); }
  });
  card.addEventListener('contextmenu', (e) => { e.preventDefault(); const acc = accountOf(card.dataset.key); if (acc) dash.accountMenu(acc, null, { x: e.clientX, y: e.clientY }); });
  updateMiniCard(card, a);
  return card;
}

/** 徽章顯示與否：從沒有變成有時彈出一下（target 是要彈的那個元素，預設徽章本身；first＝這張卡第一次畫，不播） */
function showBadge(el, show, first, target = el) {
  const appear = show && el.hidden && !first;
  setHidden(el, !show);
  if (appear) replayClass(target, 'fx-badge-in', BADGE_FX_MS);
}

function updateMiniCard(card, a) {
  const e = card._els;
  const remoteOnly = isRemoteOnly(a);
  setAcc(card, a.color, a.provider);
  card.classList.toggle('remote-only', remoteOnly);
  if (card.draggable !== !remoteOnly) card.draggable = !remoteOnly;
  // remoteOnly 沒有＋按鈕（不能用它開對話）：從 DOM 拿掉，不只是藏起來
  if (remoteOnly && e.newBtn.isConnected) e.newBtn.remove();
  else if (!remoteOnly && !e.newBtn.isConnected) e.top.append(e.newBtn);
  if (e.nameRun.textContent !== a.label) {
    e.nameRun.textContent = a.label;
    scheduleFitName(e.name); // 框的寬度不跟著名稱變（ResizeObserver 不會通知），自己重量
  }
  // 執行中、等背景工作回報、等你回應：〔同步〕加上其他在線電腦的（otherSessions），滑過的說明寫其中幾個在其他電腦
  const s = sessionTotals(a);
  // 持續動畫：同儀表板的大卡（已登入沒在跑是怠速，在跑時執行中的對話越多越快）
  setPulse(card, accountPulse(a.status, remoteOnly, s.running), pulsePhase(a.id));
  // 徽章從沒有變成有：彈出一下（fx.css 的 .fx-badge-in；卡片第一次畫的時候不播）；數字變了解碼（計數器隱藏時也照樣記住數字）
  const first = !card._badgesShown;
  card._badgesShown = true;
  showBadge(e.run, !!s.running, first);
  e.runN.set(s.running);
  setTitle(e.run, sessionTitle(s.running, s.otherRunning, '執行中'));
  showBadge(e.agents, !!s.subagents, first);
  e.agentsN.set(s.subagents);
  setTitle(e.agents, subagentsTooltip(a));
  showBadge(e.bg, !!s.background, first);
  e.bgN.set(s.background);
  setTitle(e.bg, sessionTitle(s.background, s.otherBackground, '等背景工作回報'));
  showBadge(e.wait, !!s.waiting, first, e.wait.firstElementChild);
  setTitle(e.wait, sessionTitle(s.waiting, s.otherWaiting, '等待你回應'));
  showBadge(e.prog, !!s.programs, first);
  e.progN.set(s.programs);
  setTitle(e.prog, programsTitle(a));
  if (card.dataset.status !== (a.status || 'checking')) card.dataset.status = a.status || 'checking';
  const statusText = remoteOnly ? '這台未登入' : a.status === 'ready' ? (a.plan || a.provider) : (a.statusText || STATUS_LABEL[a.status] || '');
  setText(e.subText, statusText);
  // email 不佔卡片的位置（儀表板的帳戶大卡才顯示），只放在滑過的提示；登入到期（Claude）也寫在這裡
  const lx = updateLogin(card, a, Date.now());
  setTitle(card, remoteOnly
    ? remoteOnlyTitle(a)
    : a.label + '（' + a.provider + (a.plan ? '・' + a.plan : '') + '）\n' + (a.email ? a.email + '\n' : '') + (a.statusText || '')
      + (lx ? '\n' + loginExpiryText(lx, fmtDateTime(lx.at)) : '') + '\n點一下只顯示這個帳戶的對話');
  card.classList.toggle('filtered', !remoteOnly && state.filterAccount === a.id);
  updateTokens(card, a);
  // 〔同步〕裝置小點：內容沒變不重畫
  const devSig = Array.isArray(a.devices) ? deviceStripSig(a) : '';
  if (e.devs._sig !== devSig) {
    e.devs._sig = devSig;
    e.devs.textContent = '';
    const strip = devSig ? deviceStrip(a) : null;
    if (strip) e.devs.append(strip);
    e.devs.hidden = !strip;
  }
  // 用量圓環：前兩個時間窗（Claude Code 略過每週・Fable），百分比與重置倒數寫在圈內。資料過時（例如權杖過期查不到）時圓環變灰，狀態那一行加時鐘與資料的年紀。
  // 圓環就地更新（數值變了弧線滑過去），時間窗的個數變了才重建
  const u = a.usage;
  const wins = sidebarWindows(a.provider, u && u.windows);   // Claude Code 不列「每週・Fable」
  const stale = !!(u && u.stale);
  const staleAt = stale && u.fetchedAt ? u.fetchedAt : 0;
  const sig = JSON.stringify(wins.map((w) => [w.label, Math.round(w.percent || 0), w.severity, w.resetsAt || 0]))
    + (stale ? JSON.stringify(['s', staleAt, u.error || '']) : '');
  if (e.meters._sig !== sig) {
    e.meters._sig = sig;
    if (e.meters.children.length !== wins.length) {
      e.meters.textContent = '';
      for (let i = 0; i < wins.length; i++) e.meters.append(createGauge());
    }
    wins.forEach((w, i) => updateGauge(e.meters.children[i], w, staleAt, stale ? u.error : null));
    e.meters.classList.toggle('stale', stale);
    e.stale.textContent = '';
    if (staleAt && wins.length) e.stale.append(staleBadge(staleAt, u.error));
    e.stale.hidden = !e.stale.firstChild;
  }
}

/** remoteOnly 卡（這台未登入）的提示：〔同步〕其他電腦今天用掉的 token 也寫在這裡（狀態那一行照舊是「這台未登入」） */
function remoteOnlyTitle(a) {
  const t = tokensOf(state.tokens, a.id);
  const today = t ? tokenSplit(t.today) : null;
  return a.label + '（' + a.provider + '）\n這台未登入，' + (remoteOnlyElsewhere(a) ? '只在其他電腦登入過' : '在帳戶目錄裡，目前沒有電腦登入')
    + (today ? '\n其他電腦今天的 token：in ' + fmtTokens(today.input) + '・out ' + fmtTokens(today.output) : '')
    + '\n點一下可以在這台登入';
}

/**
 * 登入到期的標示（只有桌面；瀏覽器版的帳戶沒有 loginExpiresAt）：剩 3 天內或已到期才顯示，登入進行中先收起來。
 * 名稱那一行已經有執行中或等待你的標示時只顯示圖示（剩餘時間在提示裡），名稱才不會被擠到只剩一個字。
 * 回傳 loginExpiry 的結果（還久的也回，卡片的提示要寫到期日）
 */
function updateLogin(card, a, now) {
  const e = card._els;
  const x = isHub ? null : loginExpiry(a, now);
  const show = !!x && x.level !== 'ok' && !a.loginBusy;
  const s = sessionTotals(a);
  const compact = show && !!(s.running || s.waiting || s.background || s.programs || s.subagents);
  const sig = show ? x.level + '|' + x.short + (compact ? '|c' : '') : '';
  if (e.login._sig !== sig) {
    e.login._sig = sig;
    e.login.textContent = '';
    if (show) e.login.append(icon('login'), h('span', { class: 'mlogin-left', text: x.short }));
    e.login.classList.toggle('expired', show && x.level === 'expired');
    e.login.classList.toggle('compact', compact);
  }
  setHidden(e.login, !show);
  if (show) {
    const tip = loginExpiryText(x, fmtDateTime(x.at)) + '\n點一下重新登入';
    if (e.login.title !== tip) { e.login.title = tip; e.login.setAttribute('aria-label', tip); }
  }
  return x;
}

/** 今日 token 計數：顯示時取代狀態那一行的方案名稱；數字變了就解碼成新值（tokenfx.js） */
function updateTokens(card, a) {
  const e = card._els;
  if (isRemoteOnly(a)) setTitle(card, remoteOnlyTitle(a));
  const show = showTokens(a, state.tokens);
  setHidden(e.tok, !show);
  setHidden(e.subText, show);
  card.classList.toggle('tok-on', show);
  if (show) {
    const t = tokensOf(state.tokens, a.id);
    const today = tokenSplit(t && t.today);
    e.tokIn.set(today.input);
    e.tokOut.set(today.output);
    setTitle(e.tok, tokensTitle(t, state.tokens.scope, a.provider));
  }
}

/** tokens 訊息：只更新各卡的 token 列（不重畫整張卡） */
export function renderTokens() {
  if (!els) return;
  for (const card of els.accList.children) {
    const a = card._els ? accountOf(card.dataset.key) : null;
    if (a) updateTokens(card, a);
  }
}

/** 過時標示：時鐘＋資料的年紀（8h），滑過看取得時間與原因 */
function staleBadge(at, error) {
  const b = h('span', { class: 'mstale' }, icon('clock'), h('span', { class: 'mstale-age' }));
  b.dataset.staleAt = String(at);
  if (error) b.dataset.staleErr = error;
  setStaleAge(b, Date.now());
  return b;
}

function setStaleAge(b, now) {
  const at = Number(b.dataset.staleAt) || 0;
  b.querySelector('.mstale-age').textContent = fmtSpanShort(now - at) || '<1m';
  b.title = staleText(b, now);
}

function staleText(el, now) {
  const at = Number(el.dataset.staleAt) || 0;
  return '用量資料可能過時，取得於 ' + fmtRelative(at, now) + (el.dataset.staleErr ? '\n' + el.dataset.staleErr : '');
}

const SVG_NS = 'http://www.w3.org/2000/svg';
function svgEl(tag, attrs) {
  const el = document.createElementNS(SVG_NS, tag);
  for (const k in attrs) el.setAttribute(k, attrs[k]);
  return el;
}

/** 用量圓環：軌道與弧線，圈內上下排百分比與重置倒數；外圈的小刻度與執行中的繞圈、雷達掃描畫在 fx.css 的偽元素 */
function createGauge() {
  const svg = svgEl('svg', { viewBox: '0 0 40 40', class: 'mring', 'aria-hidden': 'true' });
  const arc = svgEl('circle', { cx: 20, cy: 20, r: 15.5, class: 'mring-arc', pathLength: 100, transform: 'rotate(-90 20 20)' });
  svg.append(svgEl('circle', { cx: 20, cy: 20, r: 15.5, class: 'mring-track' }), arc);
  const num = h('span', { class: 'mmeter-num' });
  const pct = h('span', { class: 'mmeter-pct', text: '%' });
  const n = h('span', { class: 'mmeter-n' }, num, pct);
  const m = h('span', { class: 'mmeter' }, svg, h('span', { class: 'mmeter-txt' }, n, h('span', { class: 'mmeter-reset' })));
  m._els = { arc, n, num, pct };
  // 第一次出現：弧線從 0 長到目前的數值。兩個影格後寫入當下最新的目標值，這段期間到的更新不會被初值蓋回去
  arc.style.strokeDasharray = '0 100';
  requestAnimationFrame(() => requestAnimationFrame(() => { m._grown = true; arc.style.strokeDasharray = m._dash; }));
  return m;
}

function updateGauge(m, w, staleAt, staleErr) {
  const x = m._els;
  const p = Math.max(0, Math.min(100, Number(w.percent) || 0));
  const sev = w.severity || 'ok';
  // 嚴重度升高時圓環彈一下、額度重置（百分比大降）時倒轉一圈（fxrules.gaugeFx）
  const gfx = gaugeFx(m._sev, sev, m._pct, p);
  m._sev = sev;
  m._pct = p;
  if (gfx) replayClass(m, 'fx-g-' + gfx, 800);
  // 用完了（鎖住）：圓環加上鎖定的樣貌，從沒鎖變成鎖住的那一下鎖上一次（第一次畫不播）
  const locked = quotaLocked(w);
  if (locked && m._locked === false) replayClass(m, 'fx-q-lock', 900);
  m._locked = locked;
  m.classList.toggle('locked', locked);
  m.dataset.sev = sev;
  m._dash = p.toFixed(1) + ' 100';
  if (m._grown) x.arc.style.strokeDasharray = m._dash;
  x.arc.style.opacity = p > 0 ? '' : '0';
  const text = pctText(w.percent);
  x.num.textContent = text;
  x.pct.hidden = text === '–';
  x.n.className = 'mmeter-n sev-' + sev;
  m.dataset.label = w.label || '';
  m.dataset.pct = text;
  if (w.resetsAt) m.dataset.resetAt = String(w.resetsAt); else delete m.dataset.resetAt;
  if (staleAt) m.dataset.staleAt = String(staleAt); else delete m.dataset.staleAt;
  if (staleAt && staleErr) m.dataset.staleErr = staleErr; else delete m.dataset.staleErr;
  setMeterReset(m, Date.now());
}

/** 圈內百分比下方的重置倒數（3d／12h／45m）與提示；已過或沒有重置時間就不顯示倒數 */
function setMeterReset(m, now) {
  const at = Number(m.dataset.resetAt) || 0;
  const short = at ? fmtSpanShort(at - now) : '';
  const reset = m.querySelector('.mmeter-reset');
  reset.textContent = short;
  reset.hidden = !short;
  m.title = m.dataset.label + '：' + m.dataset.pct + '%' + (at ? '，' + fmtReset(at, now) : '') + (m.dataset.staleAt ? '\n' + staleText(m, now) : '');
}

/** 每分鐘：帳戶卡的重置倒數、過時資料的年紀與登入到期的標示（帳戶資料沒變也會跨過 3 天的門檻） */
export function tickMinute(now) {
  if (!els) return;
  for (const m of els.accList.querySelectorAll('.mmeter[data-reset-at], .mmeter[data-stale-at]')) setMeterReset(m, now);
  for (const b of els.accList.querySelectorAll('.mstale')) setStaleAge(b, now);
  for (const card of els.accList.children) {
    const a = card._els ? accountOf(card.dataset.key) : null;
    if (a) updateLogin(card, a, now);
  }
}

function toggleFilter(id) {
  setAccountFilter(state.filterAccount === id ? null : id);
}

// ───── 拖曳排序帳戶 ─────
function bindAccountDrag(list) {
  let dragId = null;
  list.addEventListener('dragstart', (e) => {
    const card = e.target.closest('.macct');
    if (!card || card.classList.contains('remote-only')) return;
    dragId = card.dataset.key;
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('application/x-myriagent-account', dragId);
    card.classList.add('dragging');
  });
  list.addEventListener('dragover', (e) => {
    if (!dragId) return;
    e.preventDefault();
    const over = localCard(e.target);
    list.querySelectorAll('.drop-above,.drop-below').forEach((n) => n.classList.remove('drop-above', 'drop-below'));
    if (over && over.dataset.key !== dragId) {
      const r = over.getBoundingClientRect();
      over.classList.add(e.clientY < r.top + r.height / 2 ? 'drop-above' : 'drop-below');
    }
  });
  list.addEventListener('drop', (e) => {
    if (!dragId) return;
    e.preventDefault();
    const over = localCard(e.target);
    // 只排本機帳戶（D28）：remoteOnly 的 id 是邏輯帳戶 id，不放進 accountReorder 的 ids
    const ids = state.accounts.filter((a) => !isRemoteOnly(a)).map((a) => a.id).filter((x) => x !== dragId);
    if (over && over.dataset.key !== dragId) {
      const r = over.getBoundingClientRect();
      let i = ids.indexOf(over.dataset.key);
      if (e.clientY >= r.top + r.height / 2) i++;
      ids.splice(i, 0, dragId);
      // 先在畫面上排好（remoteOnly 留在原來的位置），宿主之後送來的 accounts 為準
      const map = state.accountMap;
      let k = 0;
      state.accounts = state.accounts.map((a) => (isRemoteOnly(a) ? a : map.get(ids[k++]))).filter(Boolean);
      renderAccounts();
      post({ cmd: 'accountReorder', ids });
    }
    cleanup();
  });
  list.addEventListener('dragend', cleanup);
  function cleanup() {
    dragId = null;
    list.querySelectorAll('.dragging,.drop-above,.drop-below').forEach((n) => n.classList.remove('dragging', 'drop-above', 'drop-below'));
  }
  /** 拖曳的落點只能是本機帳戶的卡 */
  function localCard(target) {
    const card = target && target.closest ? target.closest('.macct') : null;
    return card && !card.classList.contains('remote-only') ? card : null;
  }
}

// ───── 帳戶、電腦與工作區的篩選下拉 ─────
const historyMode = () => (isHub ? 'hub' : 'desktop');
/** 可以篩選的帳戶：remoteOnly 在這台沒有對話，不列 */
const filterAccounts = () => state.accounts.filter((a) => !isRemoteOnly(a));
/** 實際生效的帳戶篩選（帳戶不在了＝所有帳戶，選擇照樣留著） */
const activeAccount = () => (state.filterAccount && state.accountMap.has(state.filterAccount) ? state.filterAccount : null);

/** 套用三個篩選後的清單；skip 略過其中一個（'account'、'device'、'folder'）：各下拉的數字照另外兩個篩選算 */
function scopedList(skip) {
  const acc = skip === 'account' ? null : activeAccount();
  let list = acc ? state.conversations.filter((c) => c.accountId === acc) : state.conversations;
  if (skip !== 'device') list = filterHistory(list, els.dev.value, historyMode());
  if (skip !== 'folder') list = filterFolder(list, els.folder.value, historyMode());
  return list;
}

/** 下拉按鈕：圖示（所有帳戶、所有電腦、這台電腦、工作區）或色點（選了某個帳戶、某台電腦），接著名稱與向下箭頭 */
function filterPick(field, ic, what, onOpen) {
  const b = h('button', { type: 'button', class: 'sb-pick', 'aria-haspopup': 'menu', 'aria-expanded': 'false', hidden: true, dataset: { field } },
    icon(ic, 'sb-pick-ic'), h('span', { class: 'sb-pick-dot', hidden: true }), h('span', { class: 'sb-pick-label' }), icon('chevDown', 'sb-pick-chev'));
  b._what = what;
  b.addEventListener('click', (e) => onOpen(b, e.detail === 0));
  return b;
}

/** label：按鈕上的名稱；dot：色點的顏色（null＝顯示圖示）；on：有篩選（不是「所有…」）；tip：滑過的說明（省略＝label） */
function setPick(b, label, dot, on, tip) {
  b.querySelector('.sb-pick-label').textContent = label;
  b.querySelector('.sb-pick-ic').hidden = !!dot;
  const d = b.querySelector('.sb-pick-dot');
  d.hidden = !dot;
  if (dot) d.style.setProperty('--acc', dot);
  b.classList.toggle('on', on);
  b.title = b._what + '：' + (tip || label);
  b.setAttribute('aria-label', b.title);
}

/** 電腦的在線色點（同清單的電腦徽章：線上綠色，其他灰色）；這台電腦沒有點 */
const onlineColor = (c) => (c.value === 'local' ? null : c.online === true ? 'var(--green)' : 'var(--text-3)');
const onlineText = (c) => (c.online === true ? '線上' : c.online === false ? (c.reconnecting ? '重新連線中' : '離線') : '');

function updateAccountPick() {
  const accounts = filterAccounts();
  const b = els.acctPick;
  b.hidden = accounts.length < 2;
  const acc = activeAccount() ? accountOf(activeAccount()) : null;
  setPick(b, acc ? acc.label : '所有帳戶', acc ? safeColor(acc.color, acc.provider) : null, !!acc);
  updateFiltersRow();
}

function updateDevicePick() {
  const r = els.dev;
  const b = els.devPick;
  b.hidden = r.choices.length === 0;
  const c = r.value ? r.choices.find((x) => x.value === r.value) : null;
  const status = c ? onlineText(c) : '';
  setPick(b, c ? c.name : '所有電腦', c ? onlineColor(c) : null, !!c, c && status ? c.name + '・' + status : null);
  updateFiltersRow();
}

function updateFolderPick() {
  const r = els.folder;
  const b = els.folderPick;
  b.hidden = r.choices.length === 0;
  const c = r.value ? r.choices.find((x) => x.value === r.value) : null;
  setPick(b, c ? c.name : '所有工作區', null, !!c, c && c.path ? c.path : null);
  updateFiltersRow();
}

/** 三個下拉都不顯示時整列藏起來（「全部」跟著藏：只剩搜尋時搜尋框自己就能清） */
function updateFiltersRow() {
  els.filters.hidden = els.acctPick.hidden && els.devPick.hidden && els.folderPick.hidden;
  els.allBtn.hidden = els.filters.hidden;
}

/** 「全部」按下的樣子＝沒有實際生效的篩選、也沒有搜尋（清單就是全部） */
function updateAllButton(q) {
  const all = !activeAccount() && !els.dev.value && !els.folder.value && !q;
  els.allBtn.classList.toggle('on', all);
  els.allBtn.setAttribute('aria-pressed', String(all));
  els.allBtn.title = all ? '目前顯示全部對話' : '顯示全部對話：清除帳戶、電腦、工作區的篩選與搜尋';
}

function openAccountFilter(btn, keyboard) {
  // 數字照目前的電腦與工作區篩選（選了就看得到的那幾個）
  const base = scopedList('account');
  const count = (id) => String(base.reduce((n, c) => n + (c.accountId === id ? 1 : 0), 0));
  const cur = activeAccount();
  const items = [{ label: '所有帳戶', icon: 'list', hint: String(base.length), checked: !cur, onSelect: () => setAccountFilter(null) }];
  for (const a of filterAccounts()) {
    items.push({ label: a.label, color: safeColor(a.color, a.provider), hint: count(a.id), checked: cur === a.id, title: a.label + '（' + a.provider + '）', onSelect: () => setAccountFilter(a.id) });
  }
  openMenu(btn, items, { minWidth: Math.max(200, btn.offsetWidth), keyboard, placement: 'top-start' });
}

function openDeviceFilter(btn, keyboard) {
  const r = els.dev;
  if (!r.choices.length) return;
  // 數字照目前的帳戶與工作區篩選（各台的數字在 renderHistory 算好）
  const items = [{ label: '所有電腦', icon: 'list', hint: String(scopedList('device').length), checked: !r.value, onSelect: () => setDeviceFilter(null) }];
  for (const c of r.choices) {
    const status = onlineText(c);
    items.push({
      label: c.name, hint: String(c.count), checked: r.value === c.value, title: status ? c.name + '・' + status : null,
      color: onlineColor(c), icon: c.value === 'local' ? 'desktop' : null, onSelect: () => setDeviceFilter(c.value),
    });
  }
  openMenu(btn, items, { minWidth: Math.max(200, btn.offsetWidth), keyboard, placement: 'top-start' });
}

/** 工作區：用到的資料夾照最近的對話排，「預設工作區」（各對話自己的資料夾）在最後；每一項附完整路徑 */
function openFolderFilter(btn, keyboard) {
  const r = els.folder;
  if (!r.choices.length) return;
  // 數字照目前的帳戶與電腦篩選
  const base = scopedList('folder');
  const counts = folderCounts(base, historyMode());
  const items = [{ label: '所有工作區', icon: 'list', hint: String(base.length), checked: !r.value, onSelect: () => setFolderFilter(null) }];
  for (const c of r.choices) {
    const isDefault = c.value === 'default';
    if (isDefault) items.push({ type: 'sep' });
    items.push({
      label: c.name, desc: isDefault ? '各對話自己的資料夾' : c.path, icon: 'folder', hint: String(counts.get(c.value) || 0),
      checked: r.value === c.value, title: c.path, onSelect: () => setFolderFilter(c.value),
    });
  }
  openMenu(btn, items, { minWidth: Math.max(260, btn.offsetWidth), keyboard, className: 'menu-folder', placement: 'top-start' });
}

function setAccountFilter(id) {
  state.filterAccount = id;
  renderAccounts();
  renderHistory();
}

/** 電腦篩選存在版面（historyScope／historyDevice），重開 app 還在 */
function setDeviceFilter(value) {
  state.filterDevice = value;
  renderHistory();
  ws.scheduleLayout();
}

/** 工作區篩選只在這次執行（同帳戶篩選，不存進版面） */
function setFolderFilter(value) {
  state.filterFolder = value;
  renderHistory();
}

/** 「全部」：三個篩選（含版面裡的電腦篩選與留著的選擇）與搜尋一起清掉 */
function showAll() {
  state.filterAccount = null;
  state.filterDevice = null;
  state.filterFolder = null;
  state.search = '';
  els.search.value = '';
  renderAccounts();
  searchChanged(); // 取消等待中的內容搜尋並重畫清單（同搜尋框按 Escape）
  ws.scheduleLayout();
}

/** 側欄的新對話：照帳戶篩選；瀏覽器版篩了某台電腦時優先開在那台（不能開時照常選電腦） */
function newFromSidebar() {
  ws.newConversation(activeAccount(), undefined, undefined, isHub ? els.dev.value : null);
  emit('sidebar-picked');
}

// ───── 對話歷史 ─────
/** 按著／放開對話清單：按著的期間清單的更新先記著（historyStale），放開時補畫 */
function holdList(on) {
  clearTimeout(pressTimer);
  pressTimer = 0;
  pressing = on;
  if (on) pressTimer = setTimeout(() => holdList(false), PRESS_HOLD_MAX);
  else if (historyStale) renderHistory();
}

export function renderHistory() {
  if (!els) return;
  // 改名期間不重排：搬動改名框所在的列會讓 Chromium 觸發 blur，把打到一半的標題送出；按著清單時也不重排（holdList）
  if (renaming || pressing) { historyStale = true; return; }
  historyStale = false;
  const terms = searchTerms(state.search);
  const q = terms.length > 0;
  // 宿主的內容結果只用在同一個查詢（打字途中的舊結果不算）
  const hits = q && content.key === searchKey(terms) ? content.hits : null;
  const filter = activeAccount();
  const mode = historyMode();
  // 工作區下拉在用到兩個以上的工作區時出現；不顯示或那個工作區沒有對話了時不篩（選擇照樣留著）
  els.folder = folderFilterChoices({ list: state.conversations, mode, selected: state.filterFolder });
  // 〔同步〕電腦下拉只在有其他電腦的對話時出現（桌面有副本、瀏覽器有兩台以上）；不顯示或那台沒有對話時不篩（選擇照樣留在版面裡）
  els.dev = deviceFilterChoices({ list: state.conversations, scoped: scopedList('device'), mode, remote: state.remote, selected: state.filterDevice });
  updateAccountPick();
  updateDevicePick();
  updateFolderPick();
  updateAllButton(q);
  const rows = filterConversations(scopedList(null), terms, hits);
  const entries = [];
  let group = null;
  // 第十批：帳戶委派的子對話掛在主對話底下（liverules.nestFamilies；搜尋時照相關的順序、不掛）
  for (const f of nestFamilies(rows, q)) {
    if (f.g !== group) { group = f.g; entries.push({ g: group || '更早' }); }
    entries.push({ c: f.c, child: f.child, last: f.last, snippet: hits ? hits.get(f.c.id) || null : null, terms });
  }
  const prevRows = els.list.childElementCount; // 只看上一次有沒有列（含分組標題）
  const fresh = [];
  reconcile(els.list, entries, (e) => (e.g !== undefined ? 'g:' + e.g : 'c:' + e.c.id), (e) => { const n = createEntry(e); if (e.g === undefined) fresh.push(n); return n; }, updateEntry);
  // 新出現的列（新對話、搜尋結果、換篩選）由上往下錯開淡入；剛啟動的第一批、超過 LIST_ENTRANCE_MAX 列的直接出現（fxrules.listEntranceCount）
  const enter = listEntranceCount(prevRows, fresh.length);
  for (let i = 0; i < enter; i++) { fresh[i].style.setProperty('--fx-i', i); replayClass(fresh[i], 'fx-row-in', 260 + i * LIST_STAGGER_MS); }
  // 找到幾個對話（只在搜尋時）；數字變了跳一下
  const cnt = q ? rows.length : null;
  els.hitCount.hidden = cnt === null;
  if (cnt !== null && els.hitCount._n !== cnt) {
    const first = els.hitCount._n === undefined || els.hitCount._n === null;
    els.hitCount.textContent = cnt + ' 個';
    els.hitCount.title = '找到 ' + cnt + ' 個對話';
    if (!first) replayClass(els.hitCount, 'fx-cnt', 420);
  }
  els.hitCount._n = cnt;
  // 內容的結果還沒來時在清單下面顯示「搜尋內容中…」（沒有列時就只有它，不先說找不到）；瀏覽器版沒能搜內容時說明原因
  const waiting = q && content.pending;
  const note = q && !waiting && content.note ? NOTE_TEXT[content.note] : null;
  els.searching.hidden = !(waiting || note);
  els.searchingSpin.hidden = !waiting;
  const status = waiting ? '搜尋內容中…' : note || '';
  if (els.searchingText.textContent !== status) els.searchingText.textContent = status;
  els.searching.classList.toggle('is-note', !!note);
  els.empty.hidden = rows.length > 0 || waiting;
  els.empty.textContent = '';
  if (!rows.length && !waiting) {
    const shown = state.search.trim();
    els.empty.append(h('span', { text: q ? (note ? '找不到標題含「' + shown + '」的對話' : '找不到標題或內容含「' + shown + '」的對話') : emptyText(filter ? accountOf(filter) : null) }));
    if (!q && !isCollaborator()) {
      const b = h('button', { type: 'button', class: 'btn sm', text: '開始新對話' });
      b.addEventListener('click', newFromSidebar);
      els.empty.append(b);
    }
  }
  markActive();
}

/** 清單空的時候（沒有搜尋）：依帳戶、電腦與工作區的篩選說明 */
function emptyText(acc) {
  // 多人協作：協作人的清單只有擁有者分享給他的對話
  if (isCollaborator()) return '還沒有分享給你的對話：請擁有者在對話的選單選「協作人…」分享給你';
  const v = els.dev.value;
  const c = v ? els.dev.choices.find((x) => x.value === v) : null;
  const where = !c ? null : v === 'local' ? '這台' : '「' + c.name + '」';
  const f = els.folder.value ? els.folder.choices.find((x) => x.value === els.folder.value) : null;
  if (f) {
    // 工作區是從完整清單挑的：清單空了一定還有帳戶或電腦的篩選（「「Claude」在這台的「api」沒有對話」）
    const folder = f.value === 'default' ? '預設工作區' : '「' + f.name + '」';
    const who = acc ? '「' + acc.label + '」' : where ? (v === 'local' ? '這台電腦' : where) : '';
    return who + '在' + (acc && where ? where + '的' : '') + folder + '沒有對話';
  }
  if (acc && where) return '「' + acc.label + '」在' + where + '沒有對話';
  if (acc) return '「' + acc.label + '」還沒有對話';
  if (where) return where + (v === 'local' ? '還沒有對話' : '沒有對話');
  return '還沒有對話';
}

/** 清單裡點了對話：開啟（已開著就切過去），再讓它的標題列閃幾下，窗格多的時候一眼找到它在哪 */
function openFromList(id) {
  ws.openConversation(id);
  ws.flashConversation(id);
  emit('sidebar-picked');
}

function createEntry(e) {
  if (e.g !== undefined) return h('div', { class: 'sb-group', role: 'presentation', text: e.g });
  const row = h('div', { class: 'crow', role: 'listitem', tabIndex: 0 });
  const title = h('span', { class: 'crow-title' });
  const st = h('span', { class: 'crow-state' });
  // 〔同步〕所在電腦的徽章（瀏覽器版每一列都有；桌面只有其他電腦的唯讀副本）：在線小點＋名稱（textContent）
  const dev = h('span', { class: 'crow-dev', hidden: true }, h('span', { class: 'crow-dev-dot', 'aria-hidden': 'true' }), h('span', { class: 'crow-dev-name' }));
  const more = iconButton('more', '更多', (ev) => { ev.stopPropagation(); rowMenu(row._id, more); }, 'crow-more sm');
  // 內容搜尋的片段：只有宿主回了片段時才顯示（換到下一行，與標題對齊）
  const snip = h('span', { class: 'crow-snip', hidden: true });
  // 分享給協作人（第十一批）：小人像＋人數，滑過列出名稱（collabrules.shareBadge）
  const share = h('span', { class: 'crow-share', hidden: true }, icon('user'), h('span', { class: 'crow-share-n' }));
  row.append(h('span', { class: 'crow-dot' }), title, share, dev, st, more, snip);
  row._els = { title, st, dev, snip, share };
  // 點一下就切過去，連點的每一下也一樣（2026-10-26 使用者要求：不再雙擊改名，改名用右鍵選單或 F2）
  row.addEventListener('click', () => { if (renaming !== row._id) openFromList(row._id); });
  row.addEventListener('keydown', (ev) => {
    if (ev.target !== row) return;
    if (ev.key === 'Enter') { ev.preventDefault(); openFromList(row._id); }
    else if (ev.key === 'F2') { ev.preventDefault(); startRename(row._id); }
    else if (ev.key === 'Delete') { ev.preventDefault(); confirmDelete(row._id); }
  });
  row.addEventListener('contextmenu', (ev) => { ev.preventDefault(); rowMenu(row._id, null, { x: ev.clientX, y: ev.clientY }); });
  updateEntry(row, e);
  return row;
}

function updateEntry(row, e) {
  if (e.g !== undefined) return;
  const c = e.c;
  row._id = c.id;
  row.classList.toggle('crow-child', !!e.child);
  row.classList.toggle('crow-last', !!e.last);
  const acc = accountOf(c.accountId);
  setAcc(row, acc ? acc.color : null, c.provider);
  // 自動命名產生了新標題：先閃過亂碼再逐字鎖定（fxrules.titleDecodeFx；第一次顯示、手動改名直接換）
  if (renaming !== c.id) setRowTitle(row._els.title, c.title || '未命名對話', e.terms, titleDecodeFx(row._tprev, c));
  row._tprev = { title: c.title, titling: !!c.titling };
  const d = updateDeviceBadge(row._els.dev, c);
  updateShareBadge(row._els.share, c.id);
  row.classList.toggle('attn-flash', isFlashing(c.id)); // 要你回來看：一直閃到點了它（attnflash.js；新建的列照狀態補上）
  updateSnippet(row, e.snippet, e.terms);
  // 代理這一輪結束了、背景工作還在跑：還算在進行，用虛線圈（和執行中的實心轉圈分得出來）
  const bgWait = backgroundWaiting(c);
  row.title = (c.title || '') + (acc ? '\n' + acc.label : '') + (d ? '\n' + d : '') + (c.workingDirectory ? '\n' + c.workingDirectory : '')
    + (bgWait ? '\n等背景工作回報' : '');
  const st = row._els.st;
  const key = (c.titling ? 't' : '') + (c.running ? 'r' : '') + (bgWait ? 'b' : '') + (c.waiting ? 'w' : '') + (c.unread ? 'u' : '');
  if (st._key !== key) {
    const prevKey = st._key;
    st._key = key;
    st.textContent = '';
    // 自動重新命名正在產生標題（titling）：閃爍的自動重新命名圖示，不用轉圈（看起來像執行中，crow-spin 也會觸發執行中的效果）
    if (c.titling) st.append(Object.assign(icon('autoTitle', 'crow-titling'), { title: '正在依目前的工作產生標題…' }));
    if (c.waiting) st.append(h('span', { class: 'badge-wait', title: '等待你回應', text: '!' }));
    if (c.running) st.append(spinner('crow-spin'));
    else if (bgWait) st.append(h('span', { class: 'bg-ring crow-bg', title: '等背景工作回報' }));
    else if (c.unread && !c.waiting) st.append(h('span', { class: 'dot-unread', title: '有新的回覆' }));
    // 「!」與未讀點從沒有變成有：彈出一下（這一列第一次畫的時候不播）
    if (prevKey !== undefined) {
      if (c.waiting && !prevKey.includes('w')) replayClass(st.querySelector('.badge-wait'), 'fx-badge-in', BADGE_FX_MS);
      else if (c.unread && !prevKey.includes('u')) { const dot = st.querySelector('.dot-unread'); if (dot) replayClass(dot, 'fx-badge-in', BADGE_FX_MS); }
    }
  }
}

/** 分享中的標記：沒有分享就藏起來；內容沒變不動 DOM */
function updateShareBadge(el, id) {
  if (!el) return;
  const b = shareBadge(state.collaborators, id);
  const key = b ? b.key : '';
  if (el._key === key) return;
  el._key = key;
  setHidden(el, !b);
  el.title = b ? b.title : '';
  el.lastChild.textContent = b && b.n > 1 ? String(b.n) : '';
}

/**
 * 對話列的標題：搜尋時符合的關鍵字以 <mark class="hit"> 標示（fx.css 的帳戶色方框；只用文字節點），沒有符合或不在搜尋時照舊
 * （自動命名的新標題照樣解碼）。標示過的標題回到一般時先換回純文字，setDecodedText 才不會以為沒變
 */
function setRowTitle(el, text, terms, animate) {
  const parts = Array.isArray(terms) && terms.length ? markParts(text, terms) : null;
  if (parts && parts.some((p) => p.mark)) {
    const sig = searchKey(terms) + '\n' + text;
    if (el._markSig === sig) return;
    el._markSig = sig;
    if (el._tdx) el._tdx.text = null;
    el.replaceChildren(...parts.map((p) => (p.mark ? h('mark', { class: 'hit', text: p.text }) : p.text)));
    return;
  }
  if (el._markSig) { el._markSig = null; el.textContent = text; }
  setDecodedText(el, text, animate);
}

/** 內容搜尋的片段（符合的關鍵字以 <mark> 標示；只用文字節點）：內容沒變不重畫 */
function updateSnippet(row, snippet, terms) {
  const el = row._els.snip;
  const sig = snippet ? searchKey(terms) + '\n' + snippet : '';
  if (el._sig === sig) return;
  el._sig = sig;
  row.classList.toggle('has-snip', !!snippet);
  el.hidden = !snippet;
  el.textContent = '';
  if (!snippet) return;
  for (const part of markParts(snippet, terms)) el.append(part.mark ? h('mark', { text: part.text }) : part.text);
}

/** 搜尋框的內容變了：標題立刻重新比對，內容在打字停下後請宿主搜 */
function searchChanged() {
  const key = searchKey(searchTerms(state.search));
  if (key !== content.key) {
    content.key = key;
    content.hits = null;
    content.pending = !!key;
    content.note = null;
  }
  clearTimeout(content.timer);
  clearTimeout(content.refresh);
  content.timer = 0;
  content.refresh = 0;
  if (content.pending) content.timer = setTimeout(requestContentSearch, SEARCH_DELAY);
  renderHistory();
}

function requestContentSearch() {
  content.timer = 0;
  if (!content.key) return;
  const reqId = 'cs' + (++content.seq);
  content.reqId = reqId;
  content.reqKey = content.key;
  const sent = post({ cmd: 'searchConversations', requestId: reqId, query: state.search.slice(0, MAX_QUERY) });
  if (!isHub) return;
  clearTimeout(content.wait);
  content.wait = 0;
  // 瀏覽器版沒有送出（沒有連線，傳輸層回 null、不提示）：只比對標題並說明，重連的 init 再搜（refreshContentSearch）；已有結果的重搜照舊留著舊的
  if (sent == null) {
    if (content.pending) { content.pending = false; content.note = 'offline'; renderHistory(); }
    return;
  }
  content.wait = setTimeout(() => {
    content.wait = 0;
    if (content.reqId === reqId && content.pending) { content.pending = false; content.note = 'noReply'; renderHistory(); }
  }, SEARCH_NO_REPLY);
}

/** 宿主回的內容搜尋結果（conversationSearch）：只收最後一次請求、而且查詢沒變的 */
export function applyConversationSearch(m) {
  if (!m || m.requestId !== content.reqId || content.reqKey !== content.key) return;
  clearTimeout(content.wait);
  content.wait = 0;
  content.hits = hitsFrom(m);
  content.pending = false;
  content.note = null;
  renderHistory();
}

/** 對話清單更新了（新訊息、改名、新對話）：正在搜尋時隔一下重搜一次內容（節流，不是每次都搜；舊結果先留著） */
export function conversationsChanged() {
  if (!content.key || content.refresh || content.timer) return;
  content.refresh = setTimeout(() => { content.refresh = 0; requestContentSearch(); }, SEARCH_REFRESH);
}

/** 新的 init（瀏覽器版重連、伺服器 resync）：搜尋框有字就重搜一次內容（斷線前送的請求，結果送不到這條新連線） */
export function refreshContentSearch() {
  if (!content.key) return;
  clearTimeout(content.timer);
  clearTimeout(content.refresh);
  content.timer = 0;
  content.refresh = 0;
  if (!content.hits) {
    content.pending = true;
    content.note = null;
    renderHistory();
  }
  requestContentSearch();
}

/**
 * 〔同步〕清單一列的電腦徽章（WP4.6）：瀏覽器版每一列、桌面只有副本（有 deviceId）；名稱與在線先查 remote.devices，
 * 沒有時用清單的 deviceName／deviceOnline。回提示用的一行（「OFFICE-PC・線上」），不顯示時 null
 */
function updateDeviceBadge(el, c) {
  const origin = originOf(c, isHub ? 'hub' : 'desktop');
  const show = origin === 'remote' || origin === 'replica';
  setHidden(el, !show);
  if (!show) return null;
  const dev = deviceOf(c.deviceId, state.remote, c);
  const online = dev.online === true ? 'true' : dev.online === false ? 'false' : 'unknown';
  if (el.dataset.online !== online) el.dataset.online = online;
  const name = el.lastChild;
  if (name.textContent !== dev.name) name.textContent = dev.name;
  const line = dev.name + (dev.online === true ? '・線上' : dev.online === false ? (dev.reconnecting ? '・重新連線中' : '・離線') : '');
  if (el.title !== line) el.title = line;
  return line;
}

/** 標示目前聚焦窗格的作用中對話與所有開著的對話 */
export function markActive() {
  if (!els) return;
  const active = ws.activeConversationId();
  const open = ws.openConversationIds();
  for (const row of els.list.children) {
    if (!row._id) continue;
    row.classList.toggle('active', row._id === active);
    row.classList.toggle('open', open.has(row._id));
  }
}

function rowMenu(id, anchor, at) {
  const c = state.convMap.get(id);
  if (!c) return;
  // 多人協作：協作人只能開啟（改名、刪除、延伸都是擁有者的）
  if (isCollaborator()) {
    openMenu(anchor, [{ label: '開啟', icon: 'chat', onSelect: () => openFromList(id) }], { at });
    return;
  }
  // 自動重新命名（autoTitle，同標題列的右鍵選單）：只有桌面這台的對話（瀏覽器版是 desktop_only，副本不處理），產生期間（清單的 titling）停用
  const autoTitle = originOf(c, isHub ? 'hub' : 'desktop') === 'local';
  openMenu(anchor, [
    { label: '開啟', icon: 'chat', onSelect: () => openFromList(id) },
    { label: '重新命名', icon: 'rename', hint: 'F2', onSelect: () => startRename(id) },
    autoTitle && {
      label: c.titling ? '正在產生標題…' : '自動重新命名', icon: 'autoTitle', desc: '依目前正在做或最後做的事產生標題',
      disabled: !!c.titling, onSelect: () => post({ cmd: 'autoTitle', conversationId: id }),
    },
    { label: '在新視窗開啟', icon: 'popout', onSelect: () => post({ cmd: 'popout', conversationId: id }) },
    // 延伸成新對話（forkConversation，同標頭的「⋯」選單）：只有桌面這台的對話，新對話開在旁邊的窗格
    autoTitle && !isHub && state.mode === 'main' && { label: '延伸成新對話', icon: 'gitBranch', desc: '複製目前的內容，由新的代理接著做', onSelect: () => ws.forkConversation(id) },
    { type: 'sep' },
    { label: '刪除…', icon: 'trash', danger: true, hint: 'Del', onSelect: () => confirmDelete(id) },
  ].filter(Boolean), { at, placement: 'bottom-end', minWidth: autoTitle ? 240 : 190 });
}

function confirmDelete(id) {
  if (isCollaborator()) return; // 多人協作：刪除是擁有者的
  // 和標題列共用確認文字（預設工作區會一起刪除要明講）；對話開著時用它最新的 header，否則用清單的摘要
  const cv = ws.getConv(id);
  confirmDeleteConversation(id, (cv && cv.header) || state.convMap.get(id) || {});
}

function startRename(id) {
  const row = Array.from(els.list.children).find((r) => r._id === id);
  if (!row || renaming || isCollaborator()) return; // 多人協作：改名是擁有者的
  const c = state.convMap.get(id);
  renaming = id;
  const titleEl = row._els.title;
  const input = h('input', { class: 'input crow-input', value: (c && c.title) || '', 'aria-label': '對話標題' });
  titleEl.hidden = true;
  titleEl.after(input);
  row.classList.add('renaming');
  input.focus();
  input.select();
  let done = false;
  const finish = (save) => {
    if (done) return;
    done = true;
    const v = input.value.trim();
    input.remove();
    titleEl.hidden = false;
    row.classList.remove('renaming');
    renaming = null;
    const cur = state.convMap.get(id) || c; // 改名期間清單可能已經換成新的一份
    if (save && v && cur && v !== cur.title) {
      cur.title = v;
      titleEl.textContent = v;
      post({ cmd: 'rename', conversationId: id, title: v });
    } else if (cur) titleEl.textContent = cur.title || '未命名對話';
    if (historyStale) renderHistory();
  };
  input.addEventListener('keydown', (e) => {
    if (e.isComposing || e.keyCode === 229) return;
    e.stopPropagation();
    if (e.key === 'Enter') { e.preventDefault(); finish(true); }
    else if (e.key === 'Escape') { e.preventDefault(); finish(false); }
  });
  input.addEventListener('click', (e) => e.stopPropagation());
  input.addEventListener('blur', () => finish(true));
}
