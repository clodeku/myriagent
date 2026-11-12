// 對話工作區：最多 12 個窗格（最多 4 欄 × 3 排的格狀，排法在 app.css 的 .ws），每個窗格有自己的分頁列；分頁可拖曳排序與搬到別的窗格。
// 〔多視窗〕桌面的分頁也能拖到 app 的另一個視窗（放在分頁列、窗格或視窗的任何地方），拖到視窗外就開新視窗（宿主看游標在哪裡，AgentHub.Windows.cs）
import { h, iconButton, debounce, reconcile, setAcc, safeColor, replayClass, setHidden } from './util.js';
import { icon } from './icons.js';
import { post, state, accountOf, summaryOf, emit, on as onEvent, isHub, isCollaborator } from './store.js';
import { spinner } from './ui.js';
import { ConvView } from './conversation.js';
import { moveInputHistory } from './composer.js';
import { keepOnRestore, awaitsList, isDraftId, draftCarryOver, layoutDeviceFields, backgroundWaiting, openOrder } from './liverules.js';
import * as live from './liveview.js';
import { isFlashing, stopFlash } from './attnflash.js';
import { tabDragHold, dropIndexAt, insertMarkX, EXTERNAL_DRAG_MS, sideTabsOn, clampSideWidth, SIDE_TABS_MIN, SIDE_TABS_MAX } from './tabdragrules.js';
import { shareBadge } from './collabrules.js';
import { zoomPlan } from './panezoomrules.js';

const MAX_PANES = 12;
const TAB_MIME = 'application/x-myriagent-tab';
const TAB_FX_MS = 360; // 新分頁的進場（fx.css 的 .tab.fx-tab-in）
const BADGE_FX_MS = 520; // 分頁上的「!」與未讀點出現時彈出（fx.css 的 .fx-badge-in，同側欄）
const TAB_TURN_FX_MS = 1400; // 背景分頁一輪結束的閃燈（fx.css 的 .tab.fx-tab-ok／fx-tab-err，1.3 秒）
const DROP_FX_MS = 420; // 放下分頁時窗格的角框收合（fx.css 的 .pane.fx-dropped）

let root = null;
const panes = [];               // { id, tabs: [], active, el, tabsEl, body, emptyEl, splitBtn, closeBtn }
const convs = new Map();        // conversationId → ConvView
let focusedId = null;
let dragTab = null;
let dragStartedAt = null;       // 這個視窗開始拖分頁的時間（tabDragHold）
let dragGen = 0;                // 每次開始拖曳（這個視窗的、別的視窗的）加一：分頁列的幾何重新量（stripGeometry）
// 〔多視窗〕別的視窗正在拖的分頁（宿主的 tabDrag）：{ id, at }。WebView2 之間不一定帶得過拖曳資料，靠它認出拖進來的是分頁
// （EXTERNAL_DRAG_MS 內有效，同宿主的 WindowRules.TabDragTtl：通知漏了也不會一直把別的拖曳當成分頁）
let externalDrag = null;
let dragHolding = false;        // 目前算不算拖曳期間（tabDragActive；變了就送 tab-drag 事件）
let dragHoldTimer = 0;
// 標著插入位置、目標窗格的元素：清掉時只清這些（以前每個 dragover 都以 querySelectorAll 掃整份文件三次）
const dropMarked = new Set();
// 〔多視窗〕從別的視窗接住、還在等宿主送 conversation 的分頁：conversationId → { paneId, index }（放在放開的位置）
const incoming = new Map();
let pendingSwitch = null;       // setAccount 後可能回來新的對話 id
// 送出 open／setAccount 後還沒收到 conversation 回覆的次數。回覆到達前就關掉分頁時改記成「要丟掉的回覆」：
// 宿主處理 close 後就不再送這個對話的更新，晚到的回覆若照常開成分頁，會變成一個停住不動的分頁
const awaitingReply = new Map(); // conversationId → 次數
const staleReplies = new Map();  // conversationId → 要丟掉的回覆數
// 〔遠端〕瀏覽器版：reopenAll 重送 open、還沒收到回覆的草稿（伺服器重開過就沒有這份草稿，回 conversationClosed）；已經重建過一次的草稿
const reopenedDrafts = new Set();
const recreatedDrafts = new Set();
// 桌面還原版面時沒有送 open 的背景分頁：切過去（activateTab）才送。宿主收到 open 要在 UI 執行緒讀完整個 items.json、渲染最後一頁，
// 以前還原時每個分頁都送（28 個分頁、合計約 20 MB 的紀錄，開窗後卡一兩秒），之後在背景串流的分頁也照樣每 50 ms 收到更新
const deferredOpen = new Set();
// 〔同步〕桌面還原版面時還不在清單裡、留著等清單的分頁（其他電腦的對話；liverules.awaitsList）：不送 open，清單到了由 settleAwaiting 接上或關掉
const awaitingList = new Set();
// 〔遠端〕等新草稿回來的換位：伺服器重開後重建的草稿（只還原字）與草稿換電腦（oldId、carry：舊草稿的標頭）；
// { paneId, index, text, deviceId, at, oldId?, carry? }
const draftSwaps = [];
const DRAFT_SWAP_MS = 20000;
// 「延伸成新對話」（forkConversation）送出、還在等宿主回 conversation 的：記號（送給宿主的 paneId）→ { afterId, at }。
// 回覆到了才在原對話的窗格旁邊開新窗格：宿主失敗（只送 toast）時不留空窗格
const pendingForks = new Map();
const FORK_WAIT_MS = 120000;
// 嵌在別的頁面裡的對話（臨時對話的浮動面板，2026-11-11）：記號（送給宿主的 paneId）→ { el, id, open, shown, onChange }。
// 對話照樣在 convs（宿主的更新照常送到），只是不在任何窗格：不進分頁列與版面。open＝面板開著（宿主回的對話才接），shown＝看得到（算進 visible）
const docks = new Map();

function bump(map, id, delta) {
  const n = (map.get(id) || 0) + delta;
  if (n > 0) map.set(id, n); else map.delete(id);
}

function postOpen(id) {
  bump(awaitingReply, id, 1);
  post({ cmd: 'open', conversationId: id });
}

export function initWorkspace(container) {
  root = container;
  // 〔多視窗〕別的視窗拖來的分頁放在窗格以外的地方（側欄、儀表板、標頭）：一樣接住，放進聚焦的窗格（窗格自己的處理先到，已處理的不重複）
  document.addEventListener('dragover', (e) => {
    if (e.defaultPrevented || !isExternalTabDrag(e)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
  });
  document.addEventListener('drop', (e) => {
    if (e.defaultPrevented || !isExternalTabDrag(e)) return;
    e.preventDefault();
    const id = draggedId(e);
    clearDropMarks();
    if (id) acceptExternalTab(id, null);
  });
  // 視窗大小變了：分頁列的位置跟著變，拖曳中量過的幾何作廢
  window.addEventListener('resize', () => { for (const p of panes) p.dragGeo = null; });
  // 宿主以 visible 判斷使用者看不看得到對話（未讀與通知）；回到這個視窗時重送，宿主把畫面上的對話當成看過了
  onEvent('active-changed', () => reportVisible(false));
  window.addEventListener('focus', () => reportVisible(true));
  document.addEventListener('visibilitychange', () => reportVisible(!document.hidden));
  // 看不到的對話（背景分頁、另一個窗格藏著）一輪結束了：分頁本身閃一下綠或紅（conversation.js 的 scheduleTurnEnd；fx.css 的 .tab.fx-tab-ok／fx-tab-err）
  onEvent('turn-end', (e) => {
    if (!e || !e.id || !root) return;
    for (const t of root.querySelectorAll('.tab')) if (t.dataset.key === e.id) replayClass(t, e.tone === 'err' ? 'fx-tab-err' : 'fx-tab-ok', TAB_TURN_FX_MS);
  });
}

let lastVisible = null;
/** 告訴宿主目前畫面上顯示的對話（各窗格的作用中分頁；儀表板時為空） */
function reportVisible(force) {
  // 〔桌面〕視窗最小化或藏到系統匣（宿主把 WebView2 設成看不見，document.hidden）：畫面上沒有對話——宿主照背景分頁節流
  // （HiddenFlushRules），這段時間完成的標未讀，回到畫面時（visibilitychange、focus）重送、清掉。
  // 瀏覽器版不這樣送：伺服器另以 ClientState 的 visible 判斷分頁在不在前景（web-protocol 第六節第 8 點）
  const ids = !isHub && document.hidden ? [] : visibleConvs().map((cv) => cv.id);
  const key = ids.join('\n');
  if (!force && key === lastVisible) return;
  lastVisible = key;
  post({ cmd: 'visible', conversationIds: ids });
}

// ───── 查詢 ─────
export const getConv = (id) => convs.get(id) || null;
export const allConvs = () => convs.values();
export const paneById = (id) => panes.find((p) => p.id === id) || null;
export const paneOf = (convId) => panes.find((p) => p.tabs.includes(convId)) || null;
export const focusedPane = () => paneById(focusedId) || panes[0] || null;
export function activeConversationId() {
  const p = focusedPane();
  return p ? p.active : null;
}
export function openConversationIds() { return new Set(convs.keys()); }
/** app 更新前的草稿快照：每個開著的對話輸入框裡還沒送出的字 */
export function draftTexts() {
  const drafts = [];
  for (const cv of convs.values()) {
    const text = cv.composer.ta.value;
    if (text.trim()) drafts.push({ conversationId: cv.id, text });
  }
  return drafts;
}
/**
 * 〔遠端〕等你回應的卡片裡打到一半的東西（合併前審查 LIVE-5）：核准卡與計畫卡的說明欄有字、問卷選了選項或「其他」有字。
 * 伺服器換版時算成未送出的輸入（不自動重新載入）
 */
export function cardInputs() {
  const out = [];
  for (const cv of convs.values()) {
    const texts = cv.ctx.cards && cv.ctx.cards.texts;
    if (texts) for (const [itemId, text] of texts) if (text && text.trim() && waitingItem(cv, itemId)) out.push({ conversationId: cv.id, itemId });
    for (const [itemId, st] of cv.qstate) {
      if (!waitingItem(cv, itemId) || !st) continue;
      const other = Object.values(st.other || {}).some((v) => typeof v === 'string' && v.trim());
      const sel = Object.values(st.sel || {}).some((v) => Array.isArray(v) && v.length > 0);
      if (other || sel) out.push({ conversationId: cv.id, itemId });
    }
  }
  return out;
}
function waitingItem(cv, itemId) {
  const it = cv.items.get(itemId);
  return !!it && it.status === 'Waiting';
}
export function visibleConvs() {
  const out = [];
  if (state.view === 'chat') for (const p of panes) { const cv = p.active && convs.get(p.active); if (cv && cv.visible) out.push(cv); }
  // 嵌在別頁（臨時對話的面板）而且看得到的
  for (const d of docks.values()) { const cv = d.shown && d.id ? convs.get(d.id) : null; if (cv && cv.visible) out.push(cv); }
  return out;
}

function newPaneId() {
  for (let i = 1; i <= MAX_PANES; i++) if (!paneById('p' + i)) return 'p' + i;
  return 'p' + Date.now();
}

// ───── 窗格 ─────
function createPane(id) {
  const p = { id, tabs: [], active: null, mru: [], insAt: -1, dragGeo: null, tabsSide: 'top', tabsWidth: null, width: 0 };
  p.tabsEl = h('div', { class: 'tabs', role: 'tablist', 'aria-label': '分頁' });
  p.newBtn = iconButton('plus', '在這個窗格開新對話（Ctrl+N）', () => newConversation(currentAccountOf(p), p.id));
  p.newBtn.classList.add('owner-only'); // 多人協作：協作人不能開新對話
  p.splitBtn = iconButton('split', '分割窗格（Ctrl+\\）', () => splitPane(p.id));
  p.closeBtn = iconButton('x', '關閉窗格', () => closePane(p.id));
  // 分頁放上方或左側（2026-11-05 使用者要求）：每個窗格各自記在版面（layout 的 panes[].tabsSide）
  p.sideBtn = iconButton('sidebar', '分頁改到左側', () => toggleTabsSide(p.id), 'strip-side');
  // 側排時分頁欄右緣的把手：左右拖曳調整寬度（雙擊恢復預設、左右鍵每次 8px；2026-11-05 使用者要求）
  p.sideGrip = h('div', { class: 'strip-grip', role: 'separator', 'aria-orientation': 'vertical', 'aria-label': '分頁欄寬度',
    'aria-valuemin': String(SIDE_TABS_MIN), 'aria-valuemax': String(SIDE_TABS_MAX), tabIndex: 0, title: '拖曳調整分頁欄的寬度（雙擊恢復預設）' });
  p.strip = h('div', { class: 'strip' }, p.tabsEl, h('div', { class: 'strip-actions' }, p.newBtn, p.sideBtn, p.splitBtn, p.closeBtn), p.sideGrip);
  bindSideGrip(p);
  p.emptyEl = h('div', { class: 'pane-empty', hidden: true },
    h('div', { class: 'pane-empty-title', text: '這個窗格沒有開著的對話' }),
    h('div', { class: 'pane-empty-sub', text: isCollaborator() ? '從左側選一個分享給你的對話。' : '從左側歷史選一個對話，或開始新的對話。' }),
    h('button', { type: 'button', class: 'btn acc owner-only', text: '新對話', onClick: () => newConversation(null, p.id) }));
  p.body = h('div', { class: 'pane-body' }, p.emptyEl);
  p.el = h('section', { class: 'pane', dataset: { pane: id } }, p.strip, p.body);
  p.el.addEventListener('pointerdown', () => focusPane(p.id), true);
  // 對話標頭右上角的放大鈕（conversation.js 的 zoomBtn；每個分頁一顆，窗格這裡接一次）
  p.el.addEventListener('click', (e) => {
    if (e.target.closest && e.target.closest('[data-field="paneZoom"]')) togglePaneZoom(p);
  });
  p.el.addEventListener('focusin', () => focusPane(p.id));
  // 分頁多到溢出時（捲軸是隱藏的）：滾輪上下捲＝分頁列左右捲
  p.tabsEl.addEventListener('wheel', (e) => {
    const el = p.tabsEl;
    if (sideTabs(p)) return; // 側排：原生的上下捲
    if (e.ctrlKey || !e.deltaY || Math.abs(e.deltaX) > Math.abs(e.deltaY) || el.scrollWidth <= el.clientWidth) return;
    e.preventDefault();
    el.scrollLeft += e.deltaY;
  }, { passive: false });
  p.tabsEl.addEventListener('scroll', () => { p.dragGeo = null; if (p.sideMark && !p.sideMark.hidden) placeSideMark(p, true); }, { passive: true });
  bindTabDrop(p);
  paneSizes.observe(p.el);
  applyTabsSide(p);
  return p;
}

// ───── 分頁側排（2026-11-05） ─────
/** 窗格的寬度變了：窄到放不下就改回上方的橫排（tabdragrules.sideTabsOn） */
const paneSizes = new ResizeObserver((entries) => {
  for (const en of entries) {
    const p = panes.find((x) => x.el === en.target);
    if (!p) continue;
    p.width = en.contentRect.width;
    applyTabsSide(p);
  }
});

/** 依偏好與窗格寬度切換側排（.pane.tabs-left）；切換鈕的說明跟著偏好；分頁欄的寬度依窗格寬度夾住（--side-w） */
function applyTabsSide(p) {
  const left = p.tabsSide === 'left';
  const on = sideTabsOn(p.tabsSide, p.width, p.el.classList.contains('tabs-left'));
  applySideWidth(p);
  p.sideBtn.title = left ? '分頁改回上方' : '分頁改到左側';
  p.sideBtn.setAttribute('aria-label', p.sideBtn.title);
  p.sideBtn.setAttribute('aria-pressed', String(left));
  p.sideBtn.classList.toggle('on', left);
  if (p.el.classList.contains('tabs-left') === on) return;
  p.el.classList.toggle('tabs-left', on);
  p.dragGeo = null;
  p.revealed = null;
  revealActiveTab(p);
  placeSideMark(p);
}

/** 分頁欄顯示的寬度：使用者調的（沒調過是預設）依窗格寬度夾住；值沒變不寫（每次寫 style 都要重算樣式） */
function applySideWidth(p) {
  const w = clampSideWidth(p.tabsWidth ?? NaN, p.width);
  if (p.sideW === w) return;
  p.sideW = w;
  p.el.style.setProperty('--side-w', w + 'px');
  p.sideGrip.setAttribute('aria-valuenow', String(w));
  p.sideGrip.dataset.w = w + 'px'; // 拖曳中把手旁邊的讀數（第十一批，fx.css 的 .strip-grip.on::before）
  p.dragGeo = null;
  if (p.sideMark && !p.sideMark.hidden) placeSideMark(p, true);
}

/** 設定使用者調的寬度（null＝恢復預設）並記進版面（save＝false：拖曳中，放開才存） */
function setSideWidth(p, w, save) {
  p.tabsWidth = w == null ? null : clampSideWidth(w, p.width);
  applySideWidth(p);
  if (save) scheduleLayout();
}

function bindSideGrip(p) {
  const g = p.sideGrip;
  let drag = null;
  g.addEventListener('pointerdown', (e) => {
    if (e.button !== 0 || !sideTabs(p)) return;
    e.preventDefault();
    g.setPointerCapture(e.pointerId);
    drag = { x: e.clientX, w: p.sideW };
    g.classList.add('on');
    document.body.classList.add('side-resizing');
  });
  g.addEventListener('pointermove', (e) => { if (drag) setSideWidth(p, drag.w + (e.clientX - drag.x), false); });
  const end = () => {
    if (!drag) return;
    drag = null;
    g.classList.remove('on');
    document.body.classList.remove('side-resizing');
    scheduleLayout();
  };
  g.addEventListener('pointerup', end);
  g.addEventListener('pointercancel', end);
  g.addEventListener('lostpointercapture', end);
  g.addEventListener('dblclick', () => setSideWidth(p, null, true));
  g.addEventListener('keydown', (e) => {
    const step = e.key === 'ArrowLeft' ? -8 : e.key === 'ArrowRight' ? 8 : 0;
    if (!step) return;
    e.preventDefault();
    setSideWidth(p, p.sideW + (e.shiftKey ? step * 4 : step), true);
  });
}

export function toggleTabsSide(id) {
  const p = paneById(id);
  if (!p) return;
  p.tabsSide = p.tabsSide === 'left' ? 'top' : 'left';
  applyTabsSide(p);
  scheduleLayout();
}

/** 這個窗格的分頁現在是側排（拖放與捲動依上下位置） */
function sideTabs(p) {
  return p.el.classList.contains('tabs-left');
}

// ───── 放大窗格（2026-11-08 使用者要求；2026-11-10 改成只由按鈕切換；panezoomrules.zoomPlan）─────
/** 放大中的窗格 id（同時只有一個） */
let zoomedId = null;

/** 放大時工作區的大小（zoomWatch 比對）：放大的位置是以它算的 */
let zoomRootSize = '';
const rootSize = () => root.clientWidth + 'x' + root.clientHeight;
// 工作區大小變了（視窗、側欄）：放大的位置不再準，先恢復（再按一次照新的大小放大）。換到儀表板時工作區隱藏、回來大小一樣，不算
const zoomWatch = new ResizeObserver(() => {
  if (root && root.clientWidth && rootSize() !== zoomRootSize) unzoomPane();
});

function togglePaneZoom(p) {
  if (zoomedId === p.id) unzoomPane();
  else zoomPane(p);
}

/**
 * 同時開著好幾個窗格時放大其中一個：上下還有空間就拉長成兩倍（下面不夠往上補）、已經滿高就加寬，
 * 不超過工作區；疊在其他窗格上面（app.css 的 .pane.zoomed），其他窗格不移位。再按一次按鈕才恢復，同時只放大一個
 */
function zoomPane(p) {
  if (panes.length < 2 || state.mode === 'popout' || !root) return;
  unzoomPane();
  const plan = zoomPlan(p.el.getBoundingClientRect(), root.getBoundingClientRect());
  if (!plan) return;
  zoomedId = p.id;
  p.el.classList.add('zoomed', plan.axis === 'y' ? 'zoom-y' : 'zoom-x');
  p.el.style.setProperty('--zoom-size', plan.size + 'px');
  p.el.style.setProperty('--zoom-shift', -plan.shift + 'px');
  zoomRootSize = rootSize();
  zoomWatch.observe(root);
}

export function unzoomPane() {
  const p = zoomedId ? paneById(zoomedId) : null;
  zoomedId = null;
  zoomWatch.disconnect();
  if (!p) return;
  p.el.classList.remove('zoomed', 'zoom-y', 'zoom-x');
  p.el.style.removeProperty('--zoom-size');
  p.el.style.removeProperty('--zoom-shift');
}

function addPane(afterId) {
  unzoomPane();
  const p = createPane(newPaneId());
  const i = afterId ? panes.findIndex((x) => x.id === afterId) : -1;
  if (i >= 0) panes.splice(i + 1, 0, p); else panes.push(p);
  renderPanes();
  return p;
}

function renderPanes() {
  if (!root) return;
  const els = panes.map((p) => p.el);
  els.forEach((el, i) => { if (root.children[i] !== el) root.insertBefore(el, root.children[i] || null); });
  for (const child of Array.from(root.children)) if (!els.includes(child)) child.remove();
  root.dataset.count = String(panes.length);
  for (const p of panes) {
    p.splitBtn.disabled = panes.length >= MAX_PANES;
    p.splitBtn.title = panes.length >= MAX_PANES ? `最多 ${MAX_PANES} 個窗格` : '分割窗格（Ctrl+\\）';
    p.closeBtn.hidden = panes.length < 2;
    // 多窗格時焦點換到這一格：角框閃一下（看得出打字會進哪一格；fx.css 的 .pane.fx-focus）
    const was = p.el.classList.contains('focused');
    p.el.classList.toggle('focused', p.id === focusedId && panes.length > 1);
    if (!was && p.el.classList.contains('focused')) replayClass(p.el, 'fx-focus', 800);
  }
}

export function focusPane(id) {
  if (focusedId === id) return;
  focusedId = id;
  renderPanes();
  for (const p of panes) renderTabs(p);
  emit('active-changed');
  scheduleLayout();
}

export function splitPane(fromId) {
  if (panes.length >= MAX_PANES) return;
  const from = paneById(fromId) || focusedPane();
  const p = addPane(from ? from.id : null);
  if (from && from.tabsSide === 'left') { p.tabsSide = 'left'; p.tabsWidth = from.tabsWidth; applyTabsSide(p); }
  focusPane(p.id);
  newConversation(currentAccountOf(from), p.id);
  scheduleLayout();
}

export function closePane(id) {
  unzoomPane();
  const p = paneById(id);
  if (!p || panes.length < 2) return;
  for (const cid of p.tabs.slice()) closeTab(cid);
  if (paneById(id)) removePane(id);
}

function removePane(id) {
  unzoomPane(); // 窗格少一格，格線重排：放大的位置不再準
  unzoomPane();
  const i = panes.findIndex((p) => p.id === id);
  if (i < 0 || panes.length < 2) return;
  const [p] = panes.splice(i, 1);
  for (const cid of p.tabs) { const cv = convs.get(cid); if (cv) cv.destroy(); convs.delete(cid); }
  p.el.remove();
  if (focusedId === id) focusedId = (panes[Math.max(0, i - 1)] || panes[0]).id;
  renderPanes();
  for (const x of panes) renderTabs(x);
  emit('active-changed');
  scheduleLayout();
}

function currentAccountOf(p) {
  const cv = p && p.active && convs.get(p.active);
  return (cv && cv.header && cv.header.accountId) || null;
}

// ───── 分頁 ─────
function renderTabs(p) {
  // 開新分頁（或從別的窗格拖過來）：新的那幾個分頁往上滑進來（fx.css 的 .tab.fx-tab-in）；窗格第一次畫、還原版面時不播
  const had = p.tabsEl.childElementCount;
  const fresh = [];
  reconcile(p.tabsEl, p.tabs, (id) => id, (id) => { const t = createTab(id); fresh.push(t); return t; }, (el, id) => updateTab(el, id, p));
  if (had > 0) for (const t of fresh.slice(0, 3)) replayClass(t, 'fx-tab-in', TAB_FX_MS);
  for (const el of p.tabsEl.children) updateTab(el, el.dataset.key, p);
  setHidden(p.emptyEl, p.tabs.length > 0);
  revealActiveTab(p);
  placeSideMark(p);
}

/**
 * 側排時作用中分頁左緣的亮線（第十一批）：分頁欄裡一條獨立的線（不放進分頁清單，拖放與 reconcile 只看分頁），換分頁時以 transform 滑過去
 * （fx.css 的 .side-mark，一次性轉場）；分頁欄捲動時跟著移、不轉場。橫排、沒有作用中的分頁、看不到時藏起來
 */
function placeSideMark(p, scrolling) {
  const on = sideTabs(p) && !!p.active;
  if (!on) { if (p.sideMark) setHidden(p.sideMark, true); p.sideMarkKey = null; return; }
  if (!p.sideMark) { p.sideMark = h('span', { class: 'side-mark', 'aria-hidden': 'true', hidden: true }); p.strip.append(p.sideMark); }
  const idx = p.tabs.indexOf(p.active);
  const at = p.tabsEl.children[idx];
  const el = at && at.dataset.key === p.active ? at : Array.from(p.tabsEl.children).find((t) => t.dataset.key === p.active);
  // 亮線的位置只看作用中的是第幾個分頁（側排的分頁與上面的按鈕列高度固定）與分頁欄捲到哪（捲動時 scrolling 會再叫一次）：
  // 都沒變就不讀版面——renderTabs 每次對話清單、帳戶更新都會到這裡，前面剛改過 DOM，一讀 offsetTop 就強迫整頁重算樣式與版面
  const key = el ? p.active + '|' + idx + '|' + el.style.getPropertyValue('--acc') : null;
  if (!scrolling && key !== null && p.sideMarkKey === key && !p.sideMark.hidden) return;
  p.sideMarkKey = null;
  if (!el || !el.offsetHeight) { setHidden(p.sideMark, true); return; }
  // 分頁的 offsetParent 是分頁欄（.strip 有定位）：offsetTop 已經含上面按鈕列的高度，只扣分頁清單捲動的量
  const y = el.offsetTop - p.tabsEl.scrollTop;
  const sig = y + '|' + el.offsetHeight + '|' + el.style.getPropertyValue('--acc');
  if (!p.sideMark.hidden && p.sideMark._sig === sig) { p.sideMarkKey = key; return; } // 位置與顏色沒變：不寫樣式
  p.sideMark._sig = sig;
  const first = p.sideMark.hidden;
  p.sideMark.classList.toggle('instant', !!scrolling || first);
  p.sideMark.style.transform = 'translateY(' + y + 'px)';
  p.sideMark.style.height = el.offsetHeight + 'px';
  p.sideMark.style.setProperty('--acc', el.style.getPropertyValue('--acc'));
  setHidden(p.sideMark, false);
  p.sideMarkKey = key;
}

/** 作用中的分頁換了就捲進分頁列的可視範圍（只在換分頁時捲，不跟使用者自己的捲動搶） */
function revealActiveTab(p) {
  if (p.revealed === p.active) return;
  const el = Array.from(p.tabsEl.children).find((t) => t.dataset.key === p.active);
  const box = p.tabsEl.getBoundingClientRect();
  if (!el || !box.width || !box.height) return; // 儀表板檢視時工作區是隱藏的：等看得到再捲
  p.revealed = p.active;
  const r = el.getBoundingClientRect();
  if (sideTabs(p)) {
    if (r.top < box.top) p.tabsEl.scrollTop -= box.top - r.top;
    else if (r.bottom > box.bottom) p.tabsEl.scrollTop += Math.min(r.bottom - box.bottom, r.top - box.top);
    return;
  }
  if (r.left < box.left) p.tabsEl.scrollLeft -= box.left - r.left;
  else if (r.right > box.right) p.tabsEl.scrollLeft += Math.min(r.right - box.right, r.left - box.left);
}

function createTab(id) {
  const t = h('div', { class: 'tab', role: 'tab', draggable: true, tabIndex: -1 });
  const x = h('button', { type: 'button', class: 'tab-x', title: '關閉（Ctrl+W）', 'aria-label': '關閉分頁', tabIndex: -1 }, icon('x'));
  // 分享給協作人（第十一批）：標題後面一個小人像（collabrules.shareBadge）
  t.append(h('span', { class: 'tab-dot' }), h('span', { class: 'tab-title' }), h('span', { class: 'tab-share', hidden: true }, icon('user')), h('span', { class: 'tab-state' }), x);
  t.addEventListener('click', (e) => { if (e.target.closest('.tab-x')) return; const p = paneOf(t.dataset.key); if (p) activateTab(p.id, t.dataset.key, true); });
  t.addEventListener('auxclick', (e) => { if (e.button === 1) { e.preventDefault(); closeTab(t.dataset.key); } });
  t.addEventListener('mousedown', (e) => { if (e.button === 1) e.preventDefault(); });
  x.addEventListener('click', (e) => { e.stopPropagation(); closeTab(t.dataset.key); });
  t.addEventListener('dragstart', (e) => {
    dragTab = t.dataset.key;
    dragStartedAt = Date.now();
    dragGen++;
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData(TAB_MIME, dragTab);
    // 〔多視窗〕桌面不放純文字：拖到其他程式（記事本）不會插入標題，放開時沒有人接住，宿主才開新視窗
    if (!crossWindow()) e.dataTransfer.setData('text/plain', t.querySelector('.tab-title').textContent);
    t.classList.add('dragging');
    document.body.classList.add('tab-dragging');
    // 拖著的分頁的帳戶色（updateTab 以 setAcc 設在分頁上）：經過的窗格的角框與插入位置用它（別的視窗拖來的沒有，用焦點色）
    setDragAcc(t.style.getPropertyValue('--acc').trim());
    // 〔多視窗〕告訴宿主拖的是哪個分頁（其他視窗據此接住；輸入框還沒送出的字一起帶過去）
    if (crossWindow()) { const cv = convs.get(dragTab); post({ cmd: 'tabDrag', conversationId: dragTab, text: cv ? cv.composer.ta.value : '' }); }
    updateDragHold();
  });
  t.addEventListener('dragend', (e) => {
    const id = dragTab;
    dragTab = null;
    dragStartedAt = null;
    t.classList.remove('dragging');
    document.body.classList.remove('tab-dragging');
    clearDropMarks();
    // 〔多視窗〕沒有人接住（放在視窗外、按了 Esc、對方沒接）：宿主看游標在哪裡——另一個視窗就搬過去、視窗外就開新視窗
    if (crossWindow() && id) post({ cmd: 'tabDragEnd', conversationId: id, dropped: !!e.dataTransfer && e.dataTransfer.dropEffect !== 'none' });
    updateDragHold();
  });
  return t;
}

function updateTab(el, id, p) {
  const cv = convs.get(id);
  const sum = summaryOf(id);
  const hd = cv && cv.header;
  const title = hd ? (hd.isDraft ? '新對話' : (hd.title || '未命名對話')) : ((sum && sum.title) || '載入中…');
  const accId = (hd && hd.accountId) || (sum && sum.accountId);
  const acc = accountOf(accId);
  const provider = (hd && hd.provider) || (sum && sum.provider);
  setAcc(el, acc ? acc.color : safeColor(null, provider), provider);
  const titleEl = el.querySelector('.tab-title');
  if (titleEl.textContent !== title) titleEl.textContent = title;
  const active = p.active === id;
  el.classList.toggle('active', active);
  el.classList.toggle('focused', active && p.id === focusedId);
  el.setAttribute('aria-selected', String(active));
  // 〔同步〕桌面上其他電腦的副本（D22）：header.running 是這台的（副本沒有程序，一律 false），擁有者那邊的在 header.replica（合併前審查 LIVE-7）
  const replica = hd && hd.replica && typeof hd.replica === 'object' ? hd.replica : null;
  const running = hd ? !!(replica ? replica.running : hd.running) : !!(sum && sum.running);
  const waiting = !!(sum && sum.waiting) || !!(cv && cv.hasWaiting()) || !!(replica && replica.waiting);
  const unread = !!(sum && sum.unread) && !(active && state.view === 'chat');
  // 代理這一輪結束了、背景工作還在跑：還算在進行，用虛線圈（和執行中的實心轉圈分得出來）
  const bgWait = !running && backgroundWaiting(hd || sum);
  const key = (running ? 'r' : '') + (bgWait ? 'b' : '') + (waiting ? 'w' : '') + (unread ? 'u' : '');
  const st = el.querySelector('.tab-state');
  if (st._key !== key) {
    const prevKey = st._key;
    st._key = key;
    st.textContent = '';
    if (waiting) st.append(h('span', { class: 'badge-wait', text: '!', title: '等待你回應' }));
    else if (running) st.append(spinner('tab-spin'));
    else if (bgWait) st.append(h('span', { class: 'bg-ring tab-bg', title: '等背景工作回報' }));
    else if (unread) st.append(h('span', { class: 'dot-unread', title: '有新的回覆' }));
    // 「!」與未讀點從沒有變成有：彈出一下（同側欄的列；分頁第一次畫的時候不播）
    const badge = st.querySelector('.badge-wait, .dot-unread');
    if (badge && prevKey !== undefined && !prevKey.includes(waiting ? 'w' : 'u')) replayClass(badge, 'fx-badge-in', BADGE_FX_MS);
  }
  el.classList.toggle('has-state', !!key);
  // 側排時右緣的狀態刻度（第十一批，fx.css）：等你回應＞執行中＞等背景工作＞未讀
  const stKind = waiting ? 'wait' : running ? 'run' : bgWait ? 'bg' : unread ? 'unread' : '';
  if ((el.dataset.st || '') !== stKind) { if (stKind) el.dataset.st = stKind; else delete el.dataset.st; }
  const share = shareBadge(state.collaborators, id);
  const shareEl = el.querySelector('.tab-share');
  setHidden(shareEl, !share);
  if (share && shareEl.title !== share.title) shareEl.title = share.title;
  el.classList.toggle('attn-flash', isFlashing(id)); // 要你回來看：一直閃到點了它（attnflash.js；新建的分頁照狀態補上）
  el.title = title + (acc ? '\n' + acc.label : '') + (running ? '\n執行中' : '') + (bgWait ? '\n等背景工作回報' : '') + (waiting ? '\n等待你回應' : '');
}

export function refreshTabs() { for (const p of panes) renderTabs(p); }

export function refreshTab(id) {
  const p = paneOf(id);
  if (!p) return;
  const el = Array.from(p.tabsEl.children).find((t) => t.dataset.key === id);
  if (el) updateTab(el, id, p);
}

/** 在指定窗格加分頁（不送 open；呼叫端決定） */
function addTab(p, id, index) {
  // 嵌在別頁的對話搬進窗格（側欄點了它）：面板放開它，ConvView 照用
  const tk = dockOf(id);
  if (tk) undock(tk);
  let cv = convs.get(id);
  if (!cv) {
    cv = new ConvView(id, api);
    convs.set(id, cv);
  }
  p.body.append(cv.root);
  if (index === undefined || index < 0 || index > p.tabs.length) p.tabs.push(id); else p.tabs.splice(index, 0, id);
  return cv;
}

/** 窗格內最近使用的順序：關掉作用中分頁時回到上一個用過的（像 VS Code） */
function touchMru(p, id) {
  p.mru = [id].concat(p.mru.filter((x) => x !== id && p.tabs.includes(x)));
}

function nextAfterClose(p, closedIndex) {
  const mru = p.mru.filter((x) => p.tabs.includes(x));
  return mru[0] || p.tabs[Math.min(closedIndex, p.tabs.length - 1)] || null;
}

/**
 * 作用中的分頁離開之後（關掉、拖到別的窗格），窗格換成顯示的那個分頁：還原版面時沒送過 open 的背景分頁（deferredOpen）先送，
 * 否則它一直停在「載入對話中…」，要等載入看門狗 6 秒後補送（2026-11-10 實機的記錄：換版重開後關掉作用中的分頁）
 */
function showActive(p) {
  touchMru(p, p.active);
  if (deferredOpen.delete(p.active)) postOpen(p.active);
  const next = convs.get(p.active);
  if (next && state.view === 'chat') next.show();
}

export function activateTab(paneId, id, focusInput) {
  const p = paneById(paneId);
  if (!p || !p.tabs.includes(id)) return;
  p.active = id;
  touchMru(p, id);
  if (deferredOpen.delete(id)) postOpen(id);
  for (const cid of p.tabs) {
    const cv = convs.get(cid);
    if (!cv) continue;
    if (cid === id && state.view === 'chat') cv.show(); else if (cid !== id) cv.hide();
  }
  focusedId = p.id;
  renderPanes();
  for (const x of panes) renderTabs(x);
  emit('active-changed');
  scheduleLayout();
  if (focusInput) { const cv = convs.get(id); if (cv) requestAnimationFrame(() => cv.focusInput()); }
}

/** 側欄點對話：已開著就切過去，否則在聚焦窗格開新分頁並送 open */
export function openConversation(id, paneId) {
  if (state.view !== 'chat') emit('want-view', 'chat');
  const existing = paneOf(id);
  if (existing) { activateTab(existing.id, id, true); return; }
  const p = paneById(paneId) || focusedPane() || addPane();
  const at = p.active ? p.tabs.indexOf(p.active) + 1 : p.tabs.length;
  addTab(p, id, at);
  postOpen(id);
  activateTab(p.id, id, true);
}

/** 對話的標題列閃幾下（側欄點了它，窗格多的時候好找）；沒開著就不做 */
export function flashConversation(id) {
  const cv = convs.get(id);
  if (cv && paneOf(id)) cv.flashHead();
}

/**
 * 開新對話（草稿）：宿主回 conversation（帶 paneId）。〔遠端〕deviceId：瀏覽器版的草稿開在哪台電腦；
 * 瀏覽器版沒給時先選電腦（liveview.chooseTarget，D16：聚焦對話的那台、上次用的那台、只有一台直接用、多台要問），一律帶 deviceId。
 * preferDeviceId（側欄篩了某台電腦）取代「聚焦對話的那台」，那台不能開時照常選
 */
export function newConversation(accountId, paneId, deviceId, preferDeviceId) {
  // 多人協作：協作人不能開新對話（只有擁有者分享給他的對話）
  if (isCollaborator()) return;
  if (isHub && !deviceId) {
    const cv = convs.get(activeConversationId());
    const focused = typeof preferDeviceId === 'string' && preferDeviceId ? preferDeviceId
      : cv && cv.header && typeof cv.header.deviceId === 'string' ? cv.header.deviceId : null;
    live.chooseTarget(accountId, focused).then((t) => { if (t) openNew(t.accountId, paneId, t.deviceId); });
    return;
  }
  openNew(accountId, paneId, deviceId);
}

/**
 * 儀表板健康度的「用代理合併／精簡」（health.js）：切到對話檢視，在聚焦的窗格開草稿；宿主回 conversation，輸入框已預先填好說明（不會自動送出）
 */
export function openHealthFix(task, accountId, targetAccountId) {
  if (state.view !== 'chat') emit('want-view', 'chat');
  const p = focusedPane() || addPane();
  post({ cmd: 'healthFix', task, accountId, targetAccountId: targetAccountId || null, paneId: p.id });
}

/**
 * 排程頁的「交給代理」（schedules.js）：切到對話檢視。有管理對話而且開著的就切到那個分頁、窗格用它所在的；
 * 宿主回 conversation（管理對話或新草稿），輸入框已預先填好說明（不會自動送出）。task 是 create 時 path 是 null、folder 是新排程的資料夾
 */
export function openScheduleAgent(path, task, accountId, conversationId, folder) {
  if (state.view !== 'chat') emit('want-view', 'chat');
  const existing = conversationId ? paneOf(conversationId) : null;
  if (existing) activateTab(existing.id, conversationId, true);
  const p = existing || focusedPane() || addPane();
  post({ cmd: 'scheduleAgent', path: path || null, task, accountId: accountId || null, folder: folder || null, paneId: p.id });
}

/**
 * 延伸成新對話（對話標頭「⋯」選單、側欄右鍵選單）：宿主複製這個對話開一個由新代理接手的對話，回 conversation（帶這裡給的記號當 paneId）。
 * 新對話開在原對話的窗格旁邊的新窗格（兩個代理並排各自進行；窗格滿了就開在原窗格，原對話沒開著時以聚焦的窗格為準）
 */
export function forkConversation(id) {
  if (state.view !== 'chat') emit('want-view', 'chat');
  const from = paneOf(id) || focusedPane();
  const token = 'fork-' + Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 8);
  pendingForks.set(token, { afterId: from ? from.id : null, at: Date.now() });
  post({ cmd: 'forkConversation', conversationId: id, paneId: token });
}

/** 延伸的回覆到了（conversation 的 paneId 是 forkConversation 的記號）：開好要放的窗格；不是延伸的回覆、等太久了回 null（照一般的放法） */
function takeForkPane(token) {
  const f = typeof token === 'string' ? pendingForks.get(token) : null;
  if (!f) return null;
  pendingForks.delete(token);
  if (Date.now() - f.at > FORK_WAIT_MS) return null;
  const after = paneById(f.afterId);
  if (panes.length >= MAX_PANES || state.mode !== 'main') return after;
  const p = addPane(after ? after.id : null);
  // 第十批：新的窗格從原窗格那一側滑進來、靠原窗格的那一邊閃一道分支線（fx.css 的 .pane.fx-fork-in）
  replayClass(p.el, 'fx-fork-in', 900);
  focusPane(p.id);
  scheduleLayout();
  return p;
}

function openNew(accountId, paneId, deviceId) {
  if (state.view !== 'chat') emit('want-view', 'chat');
  const p = paneById(paneId) || focusedPane() || addPane();
  sendNewConversation(accountId, p.id, deviceId);
}

function sendNewConversation(accountId, paneId, deviceId) {
  const msg = { cmd: 'newConversation', accountId: accountId || null, paneId };
  if (typeof deviceId === 'string' && deviceId) msg.deviceId = deviceId;
  post(msg);
}

export function closeTab(id, opts = {}) {
  // 嵌在別頁的對話（臨時對話的面板）：捨棄草稿、宿主關掉或刪除
  const tk = dockOf(id);
  if (tk) { releaseDock(tk, opts.notify !== false); return; }
  const p = paneOf(id);
  const cv = convs.get(id);
  if (!p) { if (cv) { cv.destroy(); convs.delete(id); } return; }
  const i = p.tabs.indexOf(id);
  p.tabs.splice(i, 1);
  if (cv) { cv.destroy(); convs.delete(id); }
  const waiting = awaitingReply.get(id);
  if (waiting) { awaitingReply.delete(id); bump(staleReplies, id, waiting); }
  // 還沒送過 open 的背景分頁、還在等清單的分頁：宿主不知道它開著，不必送 close
  const notOpenedYet = deferredOpen.delete(id);
  const awaited = awaitingList.delete(id);
  if (!notOpenedYet && !awaited && opts.notify !== false) post({ cmd: 'close', conversationId: id });
  p.mru = p.mru.filter((x) => x !== id);
  if (p.active === id) {
    p.active = nextAfterClose(p, i);
    if (p.active) showActive(p);
  }
  if (!p.tabs.length && panes.length > 1 && state.mode !== 'popout' && !opts.keepPane) { removePane(p.id); return; }
  renderTabs(p);
  emit('active-changed');
  scheduleLayout();
}

export function moveTab(id, toPaneId, index) {
  const from = paneOf(id);
  const to = paneById(toPaneId);
  if (!from || !to) return;
  const cv = convs.get(id);
  if (from === to) {
    const old = from.tabs.indexOf(id);
    from.tabs.splice(old, 1);
    let at = index === undefined ? from.tabs.length : index;
    if (at > old) at--;
    from.tabs.splice(Math.max(0, Math.min(at, from.tabs.length)), 0, id);
    renderTabs(from);
    scheduleLayout();
    return;
  }
  const old = from.tabs.indexOf(id);
  from.tabs.splice(old, 1);
  from.mru = from.mru.filter((x) => x !== id);
  if (from.active === id) {
    from.active = nextAfterClose(from, old);
    if (from.active) showActive(from);
  }
  const at = index === undefined ? to.tabs.length : Math.max(0, Math.min(index, to.tabs.length));
  to.tabs.splice(at, 0, id);
  if (cv) { cv.hide(); to.body.append(cv.root); }
  activateTab(to.id, id, false);
  if (!from.tabs.length && panes.length > 1) removePane(from.id);
  else renderTabs(from);
}

export function cycleTab(dir) {
  const p = focusedPane();
  if (!p || p.tabs.length < 2) return;
  const i = p.tabs.indexOf(p.active);
  activateTab(p.id, p.tabs[(i + dir + p.tabs.length) % p.tabs.length], true);
}

export function closeActiveTab() {
  const p = focusedPane();
  if (p && p.active) closeTab(p.active);
}

// ───── 拖放分頁 ─────
const dragTypes = (e) => (e.dataTransfer ? Array.from(e.dataTransfer.types || []) : []);
/** 〔多視窗〕分頁能不能拖到別的視窗：只有桌面的完整視窗（彈出視窗與瀏覽器版不行） */
const crossWindow = () => !isHub && state.mode === 'main';

/**
 * 〔多視窗〕別的視窗拖來的分頁：拖曳資料帶了分頁的型別（實測 WebView2 之間帶得過來），或宿主通知了別的視窗正在拖分頁而且拖的不是檔案或文字
 * （桌面的分頁拖曳不放 text/plain；從其他程式拖來的字與檔案一律不當成分頁）
 */
function isExternalTabDrag(e) {
  if (dragTab || !crossWindow()) return false;
  const types = dragTypes(e);
  if (types.includes(TAB_MIME)) return true;
  return !!externalDrag && Date.now() - externalDrag.at < EXTERNAL_DRAG_MS && !types.some((t) => t === 'Files' || t.startsWith('text/'));
}

function isTabDrag(e) { return (!!dragTab && dragTypes(e).includes(TAB_MIME)) || isExternalTabDrag(e); }

/** 放開的分頁是哪個對話：這個視窗自己拖的、拖曳資料裡的、宿主通知的 */
function draggedId(e) {
  if (dragTab) return dragTab;
  let id = '';
  try { id = e.dataTransfer ? e.dataTransfer.getData(TAB_MIME) : ''; } catch { id = ''; }
  return id || (externalDrag && externalDrag.id) || null;
}

/** 〔多視窗〕宿主的 tabDrag：別的視窗開始拖分頁（id）或拖完了（null） */
export function setExternalDrag(id) {
  externalDrag = typeof id === 'string' && id ? { id, at: Date.now() } : null;
  if (externalDrag) {
    dragGen++;
    setDragAcc(''); // 別的視窗拖來的沒有帳戶色：角框與插入位置用焦點色（這個視窗上次自己拖的顏色不留著）
  } else clearDropMarks();
  updateDragHold();
}

/**
 * 拖曳分頁期間（這個視窗自己拖的、宿主通知別的視窗在拖的；tabdragrules.tabDragHold）。2026-10-25 實機：十幾個對話同時執行時，
 * 主視窗的渲染程序一直超過一顆核心，拖分頁時 dragover、放開都排在串流的畫面更新後面，一頓一頓、放開後要等。
 * 這段期間 app.js 先排著串流的畫面更新與整份清單（同一則項目只留最新的一版），主執行緒留給拖放；結束時送 tab-drag（false）一次補上
 */
export function tabDragActive() {
  return tabDragHold(dragTab ? dragStartedAt : null, externalDrag ? externalDrag.at : null, Date.now()).active;
}

/** 拖曳開始、結束時（與到期時）重新判斷：變了就送 tab-drag（true／false）。到期前再看一次：結束的事件或通知漏了也會補上畫面 */
function updateDragHold() {
  clearTimeout(dragHoldTimer);
  dragHoldTimer = 0;
  const hold = tabDragHold(dragTab ? dragStartedAt : null, externalDrag ? externalDrag.at : null, Date.now());
  if (hold.active) dragHoldTimer = setTimeout(updateDragHold, hold.recheckMs);
  if (hold.active === dragHolding) return;
  dragHolding = hold.active;
  emit('tab-drag', dragHolding);
}

let dragAccRule = null;
/**
 * 拖著的分頁的帳戶色（--drag-acc）只給用到它的元素：插入記號與窗格角框的 ::after（fx.css）。放在一條自己的樣式規則裡，
 * 改它只重算那幾個元素——以前設在 body 上，自訂屬性會繼承，開始拖的那一刻整份文件都要重算樣式（對話多的時候要上百毫秒）。
 * 空字串＝不設（用焦點色）
 */
function setDragAcc(acc) {
  if (!dragAccRule) {
    if (!acc) return;
    const el = document.createElement('style');
    el.textContent = '.tab-ins, .pane-body::after {}';
    document.head.append(el);
    dragAccRule = el.sheet && el.sheet.cssRules[0];
    if (!dragAccRule) return;
  }
  if (acc) dragAccRule.style.setProperty('--drag-acc', acc); else dragAccRule.style.removeProperty('--drag-acc');
}

/**
 * 〔多視窗〕接住別的視窗拖來的分頁：記下要放的窗格與位置，請宿主把它搬過來（tabMoveIn；宿主回 conversation、請原本的視窗拿掉）。
 * 這個視窗已經開著同一個對話時只切過去，一樣請宿主讓原本的視窗拿掉
 */
function acceptExternalTab(id, paneId, index) {
  externalDrag = null;
  updateDragHold();
  if (state.view !== 'chat') emit('want-view', 'chat');
  const local = paneOf(id);
  let target = local;
  if (local) activateTab(local.id, id, true);
  else {
    target = paneById(paneId) || focusedPane() || addPane();
    incoming.set(id, { paneId: target.id, index });
    bump(awaitingReply, id, 1);
  }
  post({ cmd: 'tabMoveIn', conversationId: id, paneId: target ? target.id : null });
}

/** 〔多視窗〕宿主的 conversationMoved：這個分頁搬到別的視窗了。另外開的視窗搬走最後一個分頁時關掉自己（像 Chrome） */
export function handleMoved(id) {
  if (state.mode === 'popout') return;
  if (!paneOf(id) && !convs.has(id)) return;
  closeTab(id, { notify: false });
  if (state.windowRole === 'extra' && panes.every((p) => !p.tabs.length)) post({ cmd: 'closeWindow' });
}

function markDrop(el, cls) {
  el.classList.add(cls);
  dropMarked.add(el);
}

function clearDropMarks() {
  for (const el of dropMarked) el.classList.remove('ins-before', 'ins-after', 'tab-target', 'ins-on');
  dropMarked.clear();
  for (const p of panes) p.insAt = -1;
}

/**
 * 分頁列的幾何：各分頁相對分頁列左緣的 { left, right } 與中線的 x（視窗座標）。dragover 每秒幾十次，每次量位置都會強制排版，
 * 串流中排著的變動也得跟著先算完；所以一次拖曳只在第一次經過這一列時量，開始新的拖曳、分頁數變了、分頁列捲動或視窗大小變了才重量
 */
function stripGeometry(p) {
  const g = p.dragGeo;
  const vertical = sideTabs(p);
  if (g && g.gen === dragGen && g.n === p.tabsEl.childElementCount && g.vertical === vertical) return g;
  // 側排：同一套算法換成上下（left／right 存的是上緣、下緣，mids 是 y）
  const box = p.strip.getBoundingClientRect();
  const start = vertical ? box.top : box.left;
  const tabs = Array.from(p.tabsEl.children, (t) => {
    const r = t.getBoundingClientRect();
    return vertical ? { left: r.top - start, right: r.bottom - start } : { left: r.left - start, right: r.right - start };
  });
  p.dragGeo = { gen: dragGen, n: tabs.length, vertical, tabs, mids: tabs.map((t) => start + (t.left + t.right) / 2) };
  return p.dragGeo;
}

/**
 * 插入位置（第七批視覺效果）：分頁列上一排發光的小方塊，跟著游標移到兩個分頁之間（只動 transform；
 * 以前是分頁邊上 2px 的線，ins-before／ins-after 照舊標著）
 */
function showInsertMark(p, i) {
  if (!p.insMark) { p.insMark = h('span', { class: 'tab-ins', 'aria-hidden': 'true' }); p.strip.append(p.insMark); }
  const g = stripGeometry(p);
  p.insMark.style.transform = (g.vertical ? 'translateY(' : 'translateX(') + insertMarkX(g.tabs, i) + 'px)';
  markDrop(p.strip, 'ins-on');
}

/** 放下分頁：那個窗格的角框收合一次（fx.css 的 .pane.fx-dropped） */
function dropSettle(p) {
  replayClass(p.el, 'fx-dropped', DROP_FX_MS);
}

function dropIndex(p, e) {
  const g = stripGeometry(p);
  return dropIndexAt(g.mids, g.vertical ? e.clientY : e.clientX);
}

function bindTabDrop(p) {
  p.strip.addEventListener('dragover', (e) => {
    if (!isTabDrag(e)) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    const i = dropIndex(p, e);
    if (i === p.insAt) return; // 位置沒變：不動 DOM（dragover 每秒幾十次）
    clearDropMarks();
    const tabs = p.tabsEl.children;
    if (tabs[i]) markDrop(tabs[i], 'ins-before'); else if (tabs.length) markDrop(tabs[tabs.length - 1], 'ins-after');
    showInsertMark(p, i);
    p.insAt = i;
  });
  p.strip.addEventListener('drop', (e) => {
    if (!isTabDrag(e)) return;
    e.preventDefault();
    const id = draggedId(e);
    const i = dropIndex(p, e);
    clearDropMarks();
    if (!id) return;
    dropSettle(p);
    if (dragTab) moveTab(id, p.id, i); else acceptExternalTab(id, p.id, i);
  });
  p.body.addEventListener('dragover', (e) => {
    if (!isTabDrag(e)) return;
    const from = dragTab ? paneOf(dragTab) : null;
    if (from === p) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    if (!p.el.classList.contains('tab-target')) { clearDropMarks(); markDrop(p.el, 'tab-target'); }
  });
  p.body.addEventListener('dragleave', (e) => { if (!p.body.contains(e.relatedTarget)) p.el.classList.remove('tab-target'); });
  p.body.addEventListener('drop', (e) => {
    if (!isTabDrag(e)) return;
    e.preventDefault();
    const id = draggedId(e);
    clearDropMarks();
    if (!id) return;
    dropSettle(p);
    if (dragTab) moveTab(id, p.id); else acceptExternalTab(id, p.id);
  });
}

// ───── 宿主訊息 ─────
/** conversation：有分頁就載入；沒有就開在 paneId（或聚焦窗格）。reload＝宿主主動重送（不是 open 的回覆）：沒有分頁就丟掉、不算回覆 */
export function handleConversation(msg) {
  const id = msg.conversationId;
  // 回給嵌在別頁的面板（quickChat 帶面板的記號當 paneId）
  if (typeof msg.paneId === 'string' && docks.has(msg.paneId)) { mountDock(msg.paneId, msg); return; }
  // 〔遠端〕面板裡的草稿換成了正式對話（swapDock）：open 的回覆沒有 paneId，照等著的 id 嵌回面板
  for (const [tk, d] of docks) if (d.expect === id) { d.expect = null; mountDock(tk, msg); return; }
  let cv = convs.get(id);
  if (!cv) {
    // 已經關掉的分頁不因主動重送而重開（第二輪合併前審查 R1）
    if (msg.reload) return;
    if (state.mode === 'popout' && state.popoutId && id !== state.popoutId) return;
    // 回覆到達前就關掉的分頁（newConversation 的回覆一定帶 paneId，不受影響）
    if (!msg.paneId && staleReplies.has(id)) { bump(staleReplies, id, -1); return; }
    // 〔遠端〕伺服器主動重送的整份（F1 流量控制、通知溢出的重讀；不帶 reload 也沒有 paneId）：沒有分頁、也沒在等這個對話的回覆
    // （open、換帳戶）就是剛關掉的分頁，丟掉，不重開成停住不動的分頁（合併前審查 LIVE-3）
    if (isHub && !msg.paneId && !awaitingReply.has(id) && !(pendingSwitch && Date.now() - pendingSwitch.at < 8000)) return;
    let p = paneById(msg.paneId) || takeForkPane(msg.paneId);
    let at;
    // 〔多視窗〕從別的視窗接住的分頁：放在放開的位置
    const inc = incoming.get(id);
    if (inc) {
      incoming.delete(id);
      if (p && p.id === inc.paneId && typeof inc.index === 'number') at = Math.max(0, Math.min(inc.index, p.tabs.length));
    }
    const swap = msg.paneId && msg.header && msg.header.isDraft ? takeDraftSwap(msg) : null;
    if (swap) {
      // 換電腦：放在舊草稿的位置（舊草稿稍後關掉）；伺服器重開後的重建：放回原本的位置
      const oldAt = swap.oldId ? p.tabs.indexOf(swap.oldId) : -1;
      at = oldAt >= 0 ? oldAt : Math.min(swap.index, p.tabs.length);
    }
    if (!p && pendingSwitch && Date.now() - pendingSwitch.at < 8000 && paneOf(pendingSwitch.id)) {
      p = paneOf(pendingSwitch.id);
      at = p.tabs.indexOf(pendingSwitch.id) + 1;
    }
    if (!p) p = focusedPane() || addPane();
    if (at === undefined) at = p.active ? p.tabs.indexOf(p.active) + 1 : p.tabs.length;
    cv = addTab(p, id, at);
    cv.load(msg);
    activateTab(p.id, id, true);
    pendingSwitch = null;
    if (swap && swap.oldId) finishDraftSwitch(swap, id, msg.header);
    else if (swap && swap.text) cv.restoreText(swap.text);
  } else {
    cv.load(msg);
    if (!msg.reload && pendingSwitch && pendingSwitch.id === id) pendingSwitch = null;
  }
  reopenedDrafts.delete(id);
  // 主動重送不抵掉 open 回覆的計數（否則之後真正的回覆會被當成新的 open）
  if (!msg.reload) bump(awaitingReply, id, -1);
  refreshTab(id);
}

/**
 * 〔遠端〕新草稿（newConversation 的回覆，帶 paneId）是不是在等的換位：同一個窗格、20 秒內、電腦相同（標頭沒帶 deviceId 時不比）；
 * 是就從清單拿走並回傳。過期的順便丟掉
 */
function takeDraftSwap(msg) {
  const now = Date.now();
  for (let i = draftSwaps.length - 1; i >= 0; i--) if (now - draftSwaps[i].at > DRAFT_SWAP_MS) draftSwaps.splice(i, 1);
  const dev = msg.header.deviceId || null;
  const i = draftSwaps.findIndex((s) => s.paneId === msg.paneId && paneById(s.paneId) && (!dev || !s.deviceId || dev === s.deviceId));
  return i >= 0 ? draftSwaps.splice(i, 1)[0] : null;
}

/**
 * 〔遠端〕草稿換電腦的新草稿到了（D16）：先把舊草稿非預設的設定送到新草稿（draftCarryOver：setOption、setFolder、rename），
 * 再關掉舊草稿（送 close）、把舊草稿輸入框裡的字搬過來
 */
function finishDraftSwitch(swap, newId, newHeader) {
  for (const c of draftCarryOver(swap.carry, newHeader, newId)) post(c);
  const old = convs.get(swap.oldId);
  const text = old ? old.composer.ta.value : swap.text;
  if (paneOf(swap.oldId)) closeTab(swap.oldId);
  const cv = convs.get(newId);
  if (cv && text) cv.restoreText(text);
}

/**
 * 〔遠端〕草稿換電腦（D16）：在同一個窗格以同帳戶、新電腦開新草稿；新草稿回來之後才搬設定、關掉舊草稿、搬字（finishDraftSwitch）。
 * 舊草稿在新草稿回來之前照常可用；同一個草稿再換一次時只等最新的那次
 */
export function switchDraftDevice(id, deviceId) {
  const cv = convs.get(id);
  const p = paneOf(id);
  const hd = cv && cv.header;
  if (!isHub || !p || !hd || !hd.isDraft || typeof deviceId !== 'string' || !deviceId || deviceId === hd.deviceId) return;
  for (let i = draftSwaps.length - 1; i >= 0; i--) if (draftSwaps[i].oldId === id) draftSwaps.splice(i, 1);
  draftSwaps.push({ paneId: p.id, index: p.tabs.indexOf(id), text: '', deviceId, at: Date.now(), oldId: id, carry: Object.assign({}, hd) });
  live.rememberDevice(hd.accountId, deviceId);
  sendNewConversation(hd.accountId || null, p.id, deviceId);
}

/** 換帳戶前呼叫（宿主會回 conversation） */
export function notePendingSwitch(id) {
  pendingSwitch = { id, at: Date.now() };
  bump(awaitingReply, id, 1);
}

/** conversationClosed：〔遠端〕帶 replacedBy 時草稿換成正式對話（D17）；伺服器重開後還有字的草稿重建；其他關掉分頁 */
export function handleClosed(id, replacedBy) {
  // 〔遠端〕伺服器關掉這個對話：網頁記的附件晶片（含 sessionStorage）一起清掉（D32）
  const closing = convs.get(id);
  if (closing) closing.composer.uploadsClosed();
  if (state.mode === 'popout') {
    const cv = convs.get(id);
    if (cv) { cv.root.classList.add('closed'); cv.root.replaceChildren(h('div', { class: 'conv-closed' }, icon('info'), h('span', { text: '這個對話已經關閉或刪除。' }))); }
    return;
  }
  if (replaceDraft(id, replacedBy)) return;
  if (swapDock(id, replacedBy)) return;
  if (recreateDraft(id)) return;
  reopenedDrafts.delete(id);
  closeTab(id, { notify: false });
}

/**
 * 〔遠端〕草稿第一則送出後換成正式對話（D17）：草稿的分頁在同一個位置換成新 id（作用中的照樣作用中）、送 open，輸入紀錄搬到新 id；
 * 新 id 已經開在別的分頁時只關掉草稿、聚焦那個分頁。回 true＝已處理
 */
function replaceDraft(id, newId) {
  if (typeof newId !== 'string' || !newId || newId === id) return false;
  moveInputHistory(id, newId); // ↑↓ 的輸入紀錄跟著草稿換成新的 id（每個對話一份）
  const p = paneOf(id);
  if (!p) return false;
  reopenedDrafts.delete(id);
  const other = paneOf(newId);
  if (other) {
    closeTab(id, { notify: false });
    const q = paneOf(newId);
    if (q) activateTab(q.id, newId, true);
    return true;
  }
  const old = convs.get(id);
  const text = old ? old.composer.ta.value : '';
  const wasActive = p.active === id;
  const cv = addTab(p, newId, p.tabs.indexOf(id));
  closeTab(id, { notify: false, keepPane: true });
  postOpen(newId);
  if (wasActive) activateTab(p.id, newId, true);
  else renderTabs(p);
  if (text.trim()) cv.restoreText(text);
  return true;
}

/**
 * 〔遠端〕伺服器重開過（草稿只在伺服器記憶體）：重新 init 之後重送 open 的草稿收到 conversationClosed、輸入框又有字，
 * 就在同一個窗格、同一個位置以同帳戶、同電腦開新草稿，回來之後還原字（每個草稿最多一次）。回 true＝已處理
 */
function recreateDraft(id) {
  const cv = convs.get(id);
  const p = paneOf(id);
  if (!isHub || !isDraftId(id) || !reopenedDrafts.has(id) || recreatedDrafts.has(id) || !cv || !p) return false;
  const text = cv.composer.ta.value;
  if (!text.trim()) return false;
  // 重新載入之後（伺服器換版）標頭還沒到就收到 conversationClosed：用重新載入前記下的帳戶與電腦（ConvView 的 reloadHeader）
  const hd = cv.header || cv.reloadHeader || {};
  reopenedDrafts.delete(id);
  recreatedDrafts.add(id);
  draftSwaps.push({ paneId: p.id, index: p.tabs.indexOf(id), text, deviceId: hd.deviceId || null, at: Date.now() });
  closeTab(id, { notify: false, keepPane: true });
  sendNewConversation(hd.accountId || null, p.id, hd.deviceId || null);
  return true;
}

export function focusConversation(id) {
  stopFlash(id); // 點了系統通知切過來：算點過了
  if (state.mode === 'popout') return;
  const p = paneOf(id);
  emit('want-view', 'chat');
  if (p) activateTab(p.id, id, true);
  else openConversation(id);
}

/** 〔遠端〕resync（WP4.6 D21）：這個對話開著（有分頁）時重送一次 open，之後的 conversation 整份重建；沒開著回 false */
export function reopen(id) {
  if (!paneOf(id)) return false;
  if (deferredOpen.has(id)) return true; // 還沒載入的背景分頁：切過去時才 open
  postOpen(id);
  return true;
}

// ───── 版面還原與儲存 ─────
/**
 * 還原存下的版面（init 的 layout）。restoredDrafts＝init 的 restoredDrafts：app 更新後宿主還原的草稿對話，不在對話清單裡，
 * 宿主在 init 之後緊接著送它們的 conversation——照存下的位置放回（作用中的分頁照舊）、不送 open；版面裡沒有的
 * （例如原本顯示在彈出視窗）放在聚焦窗格的最後，不搶作用中的分頁。版面裡有任何分頁（含還原的草稿）就不另開空白的新對話。
 * 〔遠端〕瀏覽器版（WP4.6 D9）：清單外的對話 id 與草稿 id 也保留（清單最多 1000 筆；不在了伺服器回 conversationClosed）；
 * opts.autoNew 是 false 時沒有分頁也不開草稿
 */
export function restore(layout, restoredDrafts, opts = {}) {
  zoomedId = null; // 窗格整個重建：放大的那一個也沒了
  const mode = isHub ? 'hub' : 'desktop';
  panes.splice(0, panes.length);
  convs.clear();
  deferredOpen.clear();
  awaitingList.clear();
  root.textContent = '';
  const restoring = new Set(Array.isArray(restoredDrafts) ? restoredDrafts.filter((id) => typeof id === 'string' && id) : []);
  const seen = new Set();
  const src = layout && Array.isArray(layout.panes) ? layout.panes.slice(0, MAX_PANES) : [];
  for (const lp of src) {
    const p = createPane(lp.id && !paneById(lp.id) ? String(lp.id) : newPaneId());
    if (Number.isFinite(lp.tabsWidth)) p.tabsWidth = clampSideWidth(lp.tabsWidth, 0);
    if (lp.tabsSide === 'left') { p.tabsSide = 'left'; applyTabsSide(p); }
    const inList = (id) => state.convMap.has(id) || restoring.has(id);
    const tabs = (lp.tabs || []).filter((id) => keepOnRestore(id, inList(id), mode, opts.replicasPending) && !seen.has(id));
    tabs.forEach((id) => { seen.add(id); if (awaitsList(id, inList(id), mode, opts.replicasPending)) awaitingList.add(id); });
    panes.push(p);
    for (const id of tabs) addTab(p, id);
    p.active = tabs.includes(lp.active) ? lp.active : (tabs[0] || null);
  }
  // 丟掉空的窗格（至少留一個）
  for (let i = panes.length - 1; i >= 0 && panes.length > 1; i--) if (!panes[i].tabs.length) panes.splice(i, 1);
  if (!panes.length) panes.push(createPane('p1'));
  focusedId = layout && paneById(layout.focusedPane) ? layout.focusedPane : panes[0].id;
  const fp = focusedPane();
  for (const id of restoring) {
    if (seen.has(id)) continue;
    seen.add(id);
    addTab(fp, id);
    if (!fp.active) fp.active = id;
  }
  renderPanes();
  // 還原的草稿由宿主自己送 conversation：不送 open，只記著在等回覆（回覆到達前就關掉分頁時，晚到的回覆照常丟掉）。
  // 桌面只 open 各窗格作用中的分頁，其他的切過去才 open（deferredOpen）；瀏覽器版照舊全部 open（伺服器以 open 追蹤這條連線開著的對話），
  // 看得到的先送（liverules.openOrder：伺服器照順序回）
  const activeIds = new Set(panes.map((p) => p.active));
  for (const id of openOrder(panes, focusedId)) {
    if (restoring.has(id)) bump(awaitingReply, id, 1);
    else if (awaitingList.has(id)) continue; // 等清單：到了由 settleAwaiting 決定送 open 或等切過去
    else if (!isHub && !activeIds.has(id)) deferredOpen.add(id);
    else {
      // 〔遠端〕存下的版面裡的草稿：伺服器重開過（換版後網頁自動重新載入）就不在了，輸入框有字時照 recreateDraft 重開
      if (isHub && isDraftId(id)) reopenedDrafts.add(id);
      postOpen(id);
    }
  }
  for (const p of panes) {
    renderTabs(p);
    if (p.active) activateTab(p.id, p.active, false);
  }
  if (!seen.size && opts.autoNew !== false) newConversation(null, panes[0].id);
  focusedId = layout && paneById(layout.focusedPane) ? layout.focusedPane : panes[0].id;
  renderPanes();
  emit('active-changed');
}

/**
 * 〔遠端〕瀏覽器版之後的 init（重連、伺服器 resync；伺服器清掉了這條連線開著的對話，WP4.6 D9）：不重建窗格，
 * 每個分頁（含草稿）重送 open，再重送一次 visible（內容相同也送：新的連線還不知道這條連線看得到哪些）
 */
export function reopenAll() {
  // 看得到的先送（liverules.openOrder）
  for (const id of openOrder(panes, focusedId)) {
    if (isDraftId(id)) reopenedDrafts.add(id);
    postOpen(id);
  }
  lastVisible = null;
  reportVisible(false);
}

/**
 * 〔同步〕清單更新之後（init、conversations）：留著等清單的分頁進了清單就接上（作用中的送 open、其他的等切過去才送）；
 * 宿主說清單已經載完（replicasPending 不是 true）還不在的就關掉，關完一個分頁都沒有時照還原的規則開一個新對話
 */
export function settleAwaiting(replicasPending) {
  if (!awaitingList.size) return;
  for (const id of [...awaitingList]) {
    if (state.convMap.has(id)) {
      awaitingList.delete(id);
      const p = paneOf(id);
      if (!p) continue;
      if (p.active === id) postOpen(id); else deferredOpen.add(id);
    } else if (replicasPending !== true) {
      awaitingList.delete(id);
      closeTab(id, { notify: false });
    }
  }
  if (replicasPending !== true && state.mode !== 'popout' && !panes.some((p) => p.tabs.length)) newConversation(null, panes[0].id);
  refreshTabs();
}

/** 彈出模式：只有一個窗格、一個對話 */
export function restorePopout(id) {
  panes.splice(0, panes.length);
  convs.clear();
  deferredOpen.clear();
  root.textContent = '';
  const p = createPane('p1');
  panes.push(p);
  focusedId = 'p1';
  renderPanes();
  if (id) {
    addTab(p, id);
    p.active = id;
    renderTabs(p);
    postOpen(id);
    activateTab('p1', id, true);
  } else renderTabs(p);
}

export function layoutPayload() {
  return {
    // tabsSide：分頁放左側才帶（'left'；2026-11-05），放上方省略；tabsWidth：拖曳調過的分頁欄寬度（沒調過省略）
    panes: panes.map((p) => ({ id: p.id, tabs: p.tabs.slice(), active: p.active, ...(p.tabsSide === 'left' ? { tabsSide: 'left' } : {}),
      ...(p.tabsWidth != null ? { tabsWidth: p.tabsWidth } : {}) })),
    focusedPane: focusedId,
    sidebarCollapsed: !!state.sidebarCollapsed,
    view: state.view,
    // 〔同步〕側欄歷史的電腦篩選（WP4.6 D31）：historyScope 是 all 或 local（舊版只認它）、選某一台時另有 historyDevice
    ...layoutDeviceFields(state.filterDevice),
    // 電腦狀態面板（monitor.js）：使用者選過才有（open 或 closed），沒選過省略＝視窗夠寬才顯示
    monitor: state.monitorPref === 'open' || state.monitorPref === 'closed' ? state.monitorPref : undefined,
  };
}

export const scheduleLayout = debounce(() => {
  if (state.mode === 'popout' || !state.ready) return;
  post({ cmd: 'layout', layout: layoutPayload() });
}, 300);

/** 切換檢視時顯示／隱藏作用中的對話 */
export function setChatVisible(on) {
  if (!on) unzoomPane();
  for (const p of panes) {
    const cv = p.active && convs.get(p.active);
    if (!cv) continue;
    if (on && deferredOpen.delete(p.active)) postOpen(p.active);
    if (on) cv.show(); else cv.hide();
  }
  reportVisible(false);
}

/**
 * 看得到卻一直沒有載入完成的分頁（ConvView 的載入看門狗）：重送 open。
 * 回傳這個分頁當時的狀態（記進回報，找出是哪一條路掉的）；還在等清單（其他電腦的對話，清單到了才 open）或已經不在窗格裡時不送
 */
function retryOpen(id) {
  const s = {
    deferred: deferredOpen.has(id), awaitingList: awaitingList.has(id), awaitingReply: awaitingReply.get(id) || 0,
    staleReplies: staleReplies.get(id) || 0, inList: state.convMap.has(id), inPane: !!paneOf(id) || !!dockOf(id),
  };
  if (s.awaitingList || !s.inPane) return Object.assign({ sent: false }, s);
  deferredOpen.delete(id);
  postOpen(id);
  return Object.assign({ sent: true }, s);
}

// ───── 嵌在別頁的對話（臨時對話的浮動面板）─────

/**
 * 註冊一個面板：記號（quickChat 的 paneId）、放 ConvView 的元素、嵌著的對話換了時的回呼 onChange(id, reason)：
 * 'mounted'（嵌進來了）、'released'（放開了：關掉面板、宿主關掉或刪除、換成別的）、'moved'（搬進窗格了：id 是 null）
 */
export function registerDock(token, el, onChange) {
  if (!docks.has(token)) docks.set(token, { el, id: null, open: false, shown: false, onChange: onChange || null });
}

/** 面板開著（宿主回的對話才嵌進來）、看得到：嵌著的對話跟著顯示或藏起來，visible 重送 */
export function setDock(token, open, shown) {
  const d = docks.get(token);
  if (!d) return;
  d.open = !!open;
  d.shown = !!open && !!shown;
  const cv = d.id ? convs.get(d.id) : null;
  if (cv) { if (d.shown) cv.show(); else cv.hide(); }
  reportVisible(false);
}

/** 嵌著的對話（沒有是 null） */
export function dockedConv(token) {
  const d = docks.get(token);
  return d && d.id ? convs.get(d.id) || null : null;
}

function dockOf(id) {
  for (const [tk, d] of docks) if (d.id === id) return tk;
  return null;
}

/** 宿主回了要嵌進面板的對話（quickChat 的回覆）：開在窗格裡的先從窗格拿下來（不關掉），面板原本嵌著別的就放開它 */
function mountDock(token, msg) {
  const d = docks.get(token);
  const id = msg.conversationId;
  const p = paneOf(id);
  if (!d.open) {
    // 面板在等回覆的期間關掉了：開在窗格裡的照常更新，否則告訴宿主不看了（草稿就此丟掉）
    if (p) { const pcv = convs.get(id); if (pcv) pcv.load(msg); } else if (!convs.has(id)) post({ cmd: 'close', conversationId: id });
    return;
  }
  if (d.id && d.id !== id) releaseDock(token, true);
  if (p) detachTab(p, id);
  deferredOpen.delete(id);
  let cv = convs.get(id);
  if (!cv) { cv = new ConvView(id, api); convs.set(id, cv); }
  d.el.replaceChildren(cv.root);
  d.id = id;
  cv.load(msg);
  if (d.shown) cv.show(); else cv.hide();
  if (!msg.reload) bump(awaitingReply, id, -1);
  reportVisible(false);
  if (d.onChange) d.onChange(id, 'mounted');
  if (d.shown) requestAnimationFrame(() => cv.focusInput());
}

/**
 * 〔遠端〕面板裡的伺服器草稿第一則送出後換成正式對話（conversationClosed 的 replacedBy；臨時對話的面板）：銷毀草稿的畫面、送 open 新的 id，
 * 回覆到了照等著的 id 嵌回同一個面板（handleConversation）。不通知面板（不是被關掉）。回 true＝已處理
 */
function swapDock(id, newId) {
  const tk = dockOf(id);
  if (!tk || typeof newId !== 'string' || !newId || newId === id) return false;
  const d = docks.get(tk);
  const cv = convs.get(id);
  if (cv) { cv.destroy(); convs.delete(id); }
  d.id = null;
  d.expect = newId;
  d.el.replaceChildren(h('div', { class: 'qc-swap' }, icon('clock'), h('span', { text: '對話建立了，載入中…' })));
  post({ cmd: 'open', conversationId: newId });
  return true;
}

/** 面板正在等它換成的正式對話（swapDock 之後、open 的回覆到之前） */
export function dockExpecting(token) {
  const d = docks.get(token);
  return d ? d.expect || null : null;
}

/** 放開面板嵌著的對話：銷毀 ConvView；notify＝告訴宿主不看了（close：沒送出過的草稿就此丟掉） */
export function releaseDock(token, notify = true) {
  const d = docks.get(token);
  if (d && !d.id && d.expect) { d.expect = null; d.el.replaceChildren(); return; }
  if (!d || !d.id) return;
  const id = d.id;
  d.id = null;
  const cv = convs.get(id);
  if (cv) { cv.destroy(); convs.delete(id); }
  d.el.replaceChildren();
  if (notify) post({ cmd: 'close', conversationId: id });
  reportVisible(false);
  if (d.onChange) d.onChange(null, 'released');
}

/** 嵌著的對話要搬進窗格（addTab）：面板只是放開它，ConvView 留著給窗格用 */
function undock(token) {
  const d = docks.get(token);
  if (!d || !d.id) return;
  d.id = null;
  d.el.replaceChildren();
  if (d.onChange) d.onChange(null, 'moved');
}

/** 把分頁從窗格拿下來、對話留著（搬進面板）：不銷毀、不送 close */
function detachTab(p, id) {
  const i = p.tabs.indexOf(id);
  if (i < 0) return;
  p.tabs.splice(i, 1);
  p.mru = p.mru.filter((x) => x !== id);
  if (p.active === id) {
    p.active = nextAfterClose(p, i);
    if (p.active) { touchMru(p, p.active); const next = convs.get(p.active); if (next && state.view === 'chat') next.show(); }
  }
  if (!p.tabs.length && panes.length > 1 && state.mode !== 'popout') { removePane(p.id); return; }
  renderTabs(p);
  emit('active-changed');
  scheduleLayout();
}

export const api = {
  registerDock, setDock, dockedConv, releaseDock, dockExpecting,
  openConversation, flashConversation, newConversation, openHealthFix, openScheduleAgent, closeTab, activateTab, focusPane, splitPane, closePane, moveTab, cycleTab, closeActiveTab,
  notePendingSwitch, activeConversationId, openConversationIds, refreshTab, getConv, paneCount: () => panes.length,
  switchDraftDevice, scheduleLayout, forkConversation, retryOpen,
};
