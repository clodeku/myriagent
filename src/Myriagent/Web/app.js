/* Clode Myriagent 介面網頁：純顯示層，狀態全在 C#（AgentHub；瀏覽器版是伺服器）。協定見 docs/web-protocol.md。
 * 收送經 store.js → transport.js（桌面 WebView2／瀏覽器 hub）。
 * 宿主 → 網頁：init／accounts／conversations／conversation／olderItems／header／item／itemPatch（〔遠端〕即時更新的增量）／removeItem／composer／conversationClosed／
 *              focusConversation／showView／files／fileSearch／cli／settings／sendRejected／toast／accountLogin／draftSnapshotRequest／appUpdate／
 *              tokens（側欄帳戶卡的今日 token 計數）／conversationSearch（側欄搜尋的內容結果，sidebar.js）／
 *              health（只有桌面：儀表板的健康度，health.js）／usageSegments（只有桌面：每週額度每 5% 的 token，usageseg.js）／launchStatus、launchLog（只有桌面：標題列第二行的執行列，launchbar.js）／
 *              activity（只有桌面：輸入框下方那一行的燈號，composer.js 的 applyActivity）
 * 〔電腦狀態〕 deviceStats（整份取代；只送給 monitor 開著的視窗或連線）：交給 monitor.js（對話檢視右側的面板）
 * 〔同步〕     devices（裝置清單）／sync（只有桌面：這台的同步狀態）／connection（只有瀏覽器：傳輸層自己產生的連線狀態）；
 *              init.remote 與 init／cli 的 devicesCli 存進 state，畫面由儀表板、側欄、設定頁依 remote-changed 事件更新；
 *              桌面頂部列的同步小點（#syncDot，CLI 徽章之前）取 remote.sync
 * 版號：品牌名旁的 #appVer（versionrules.js；版號前面是這一版的日期時間）；桌面取 init.app 與 appUpdate，瀏覽器版取這次載入第一則 init 的 app.build、app.commitAt 與目前的 init.app
 * 瀏覽器版（伺服器的 /app/，store.js 的 isHub）：WP4.6 起和桌面一樣開對話、還原分頁（不自動開草稿、保留清單外的對話 id 與草稿 id）；
 *              之後的 init（重連、伺服器 resync）不重建窗格，只對每個分頁重送 open、重送 visible；頂部列下方的連線狀態列取 connection；
 *              第一則 init 之前工作區、側欄 inert、檢視切換與設定鈕停用，init 之前與斷線時儀表板 inert；「帳號與安全」連回登入頁 /；
 *              app 自我更新是這台的（伺服器的 init 沒有 appUpdate）：沒有更新橫幅與設定頁的「Clode Myriagent」區塊；
 *              不認得的訊息（commandResult 等）交給 liveview.js
 * 網頁 → 宿主：ready／layout／newConversation／open／close／visible／loadOlder／send／cancelQueued／stop／answer／skipQuestion／approve／approvePlan／
 *              rename／autoTitle／delete／setOption／setAccount／chooseFolder／setFolder／resetFolder／openFolder／attach／addFiles／pasteImage／
 *              addPathAttachment／removeAttachment／listFiles／fileSearch／openLink／openPath／revealPath／copyText／copyItem／popout／
 *              accountAdd／accountLogin／accountLoginCancel／accountLogout／accountUpdate／accountRemove／accountReorder／accountRefresh／accountOpenFolder／
 *              cliCheck／cliInstall／cliUpdate／setting／draftSnapshot／appUpdateCheck／appUpdateRestartNow／appUpdateForce／appUpdateCancel／appInstallHere／
 *              monitor（電腦狀態面板看得到才 on；每則 init 之後依目前狀態再送一次，monitor.js）
 *              clientError（沒被接住的例外、沒處理的 Promise 拒絕、圖片與影片載入失敗，給宿主記錄；errorreport.js）
 *              searchConversations（側欄搜尋打字停下後請宿主搜對話內容；瀏覽器版由伺服器搜，sidebar.js）
 *              healthCheck／healthFix（只有桌面：儀表板的健康度「重新分析」「用代理合併／精簡」，health.js）
 *              mcpCheck／mcpAction（只有桌面：儀表板各 Claude 帳戶的 MCP 伺服器「重新檢查」「授權」「重新連線」，mcp.js）
 *              usageSegments（只有桌面：儀表板的「每週額度每 5% 的 token」，切到儀表板與每分鐘，usageseg.js）
 *              launchAction／launchSelect／launchLog／launchRescan（只有桌面：執行列的按鈕、選目標、輸出、重新偵測，launchbar.js）
 * 效能：header／item／removeItem／composer 以 requestAnimationFrame 合併同一影格的更新；倒數與經過時間用單一 1 秒計時器、只更新可見元素。
 */
import { h, debounce, replayClass } from './util.js';
import { icon, brandMark } from './icons.js';
import { post, onHostMessage, state, setAccounts, setConversations, setProviders, providerKeys, on, emit, hasBridge, isHub } from './store.js';
import { retryNow } from './transport.js';
import { remoteFromInit, applySync, applyDevices, trackDeviceLinks, nextLinkChange, reconnectGraceOf, connectionBanner, syncDot } from './remoterules.js';
import { layoutDeviceFilter, applyItemPatch, patchReopenDue, mergeHeaderLists } from './liverules.js';
import { failureOf } from './sendrules.js';
import { isAppleTouch, isHandheld } from './voicemsgrules.js';
import { pageTitle } from './titlerules.js';
import { applySettings } from './theme.js';
import { toast, handleEscape, hasLayer } from './ui.js';
import * as ws from './workspace.js';
import { refreshListLinks } from './conversation.js';
import { initSidebar, renderAccounts, renderHistory, renderTokens, markActive, tickMinute as tickSidebar, applyConversationSearch, conversationsChanged, refreshContentSearch, setAccountsFoldable } from './sidebar.js';
import { initDrawer } from './drawer.js';
import * as dash from './dashboard.js';
import * as sched from './schedules.js';
import { badgeCount as scheduleBadgeCount } from './schedulerules.js';
import { openSettings, refreshSettings, refreshAppSettings } from './settings.js';
import { cliBadgeState } from './hotupdate.js';
import { renderAppBanner } from './appupdate.js';
import { desktopVersionBadge, hubVersionBadge } from './versionrules.js';
import * as live from './liveview.js';
import * as cmdtrack from './cmdtrack.js';
import { onPeerUpload } from './composer.js';
import { openFileLink } from './filelinks.js';
import * as mon from './monitor.js';
import { installErrorReporting } from './errorreport.js';
import { startFlash, isFlashing, forgetFlash } from './attnflash.js';
import { onExternalConversations } from './importconv.js';
import { updateTabBadge } from './favbadge.js';
import { applyCollaborators, onCollabResult, applyViewerChrome, isCollaborator } from './collab.js';
import { initSpotlight } from './spotlight.js';

// 錯誤回報最先裝上（之後的初始化出錯也收得到）
installErrorReporting();

const $ = (id) => document.getElementById(id);
const appEl = $('app');
const sidebarEl = $('sidebar');
const workspaceEl = $('workspace');
const dashboardEl = $('dashboard');
const schedulesEl = $('schedules');
const cliBadge = $('cliBadge');
/** 電腦狀態面板沒有選過顯示或收起（layout.monitor 沒有值）時，視窗至少這麼寬才顯示 */
const MONITOR_AUTO_MIN_WIDTH = 1200;
/** 窄螢幕（手機、很窄的視窗）：電腦狀態面板改成疊在右邊的抽屜，預設收起、不記進版面 */
const narrowMq = window.matchMedia('(max-width: 720px)');
/** 手機版（瀏覽器版的窄螢幕）：側欄是蓋在對話上的抽屜（app.css），左右滑收起與展開、點對話那一側或清單裡的對話就收起（drawer.js）；帳戶區預設收起（sidebar.js） */
const phoneDrawer = () => isHub && narrowMq.matches;
/** 主要指標是觸控（手機、平板；平板接了鍵盤或拆下時會變） */
const coarseMq = window.matchMedia('(pointer: coarse)');
/**
 * 手機或平板（voicemsgrules 的 isHandheld：主要指標是觸控，或使用者代理是手機、平板；觸控筆電、桌機不算）：
 * 側欄照常並排，只有帳戶區和手機版一樣預設收起（2026-10-24 使用者要求；瀏覽器版與桌面都是）
 */
const handheld = () => isHandheld({ coarsePointer: coarseMq.matches, userAgent: navigator.userAgent, maxTouchPoints: navigator.maxTouchPoints });
// iPhone、iPad：app.css 依 data-os="ios" 讓開狀態列底下的模糊漸層、縮小手勢列上方的留白
if (isAppleTouch({ userAgent: navigator.userAgent, maxTouchPoints: navigator.maxTouchPoints })) document.documentElement.dataset.os = 'ios';

applySettings(state.settings);
// 瀏覽器版：一開始（applyChrome 之前）就標 mode-hub（沒有這台的 CLI 徽章、同步小點）
if (isHub) document.body.classList.add('mode-hub');

// ───── 頂部列 ─────
$('brandMark').replaceWith(brandMark('brand-mark'));
const btnSidebar = $('btnSidebar');
btnSidebar.append(icon('sidebar'));
btnSidebar.addEventListener('click', () => toggleSidebar());
$('btnSettings').append(icon('gear'));
$('btnSettings').addEventListener('click', () => openSettings());
/** 檢視鈕的圖示：各頁各一個（手機寬度只剩圖示，app.css 的 .view-switch，要分得出來） */
const VIEW_ICON = { chat: 'chat', dashboard: 'gauge', schedules: 'clock' };
for (const b of document.querySelectorAll('#viewSwitch [data-view]')) {
  b.prepend(icon(VIEW_ICON[b.dataset.view] || 'gauge'));
  // 只剩圖示時的名稱（提示）；排程的提示之後由 renderScheduleSwitch 換成帶數字的
  if (!b.title) b.title = b.textContent.trim();
  b.addEventListener('click', () => setView(b.dataset.view));
}
cliBadge.addEventListener('click', () => openSettings({ section: 'cli' }));

// 〔同步〕桌面的同步小點（CLI 徽章之前；index.html 不動，這裡動態插入）：sync 是 null（未配對、沒開同步）時不顯示。
// 點了開設定的「多電腦同步」（分頁與區塊 id 是 sync；那個分頁還沒有時 openSettings 回到上次的分頁）
const syncDotEl = isHub ? null : h('button', { type: 'button', id: 'syncDot', class: 'sync-dot', hidden: true },
  h('span', { class: 'sync-dot-ic', 'aria-hidden': 'true' }), h('span', { class: 'sync-dot-text' }));
if (syncDotEl) {
  cliBadge.before(syncDotEl);
  syncDotEl.addEventListener('click', () => openSettings({ section: 'sync' }));
}

// 品牌名旁的版號（versionrules.js；index.html 不動，這裡動態插入），init 之前不顯示。桌面點了開設定的「Clode Myriagent」區塊
// （版本、更新狀態、立即檢查）；瀏覽器版是伺服器「版本與更新」頁的連結，開在新分頁（不離開這頁，打到一半的字不會不見）
const verEl = isHub
  ? h('a', { id: 'appVer', class: 'app-ver', href: '/#releases', target: '_blank', rel: 'noopener', hidden: true })
  : h('button', { type: 'button', id: 'appVer', class: 'app-ver', hidden: true });
// 日期時間分成日期與時刻兩段（中間的空白是文字節點）：手機寬度時疊成兩行（app.css）
verEl.append(h('span', { class: 'app-ver-pre' }),
  h('span', { class: 'app-ver-time' }, h('span', { class: 'app-ver-date' }), ' ', h('span', { class: 'app-ver-clock' })),
  h('span', { class: 'app-ver-num' }));
document.querySelector('.brand').append(verEl);
// 下載新版時版號旁的讀數（appupdate.js 的 renderDownloadChip）：「⇣ 12.4 MB・3.4 MB/s」，每收到一次進度燈閃一下
if (!isHub) document.querySelector('.brand').append(h('span', { id: 'appDl', class: 'app-dl', hidden: true, role: 'status' }, h('i', { 'aria-hidden': 'true' }), h('span', { class: 'app-dl-t' })));
if (!isHub) verEl.addEventListener('click', () => openSettings({ section: 'app' }));

// 瀏覽器版：「帳號與安全」（回登入頁 /：改密碼、登出、其他瀏覽器的登入）與頂部列下方的連線狀態列
const hubStatus = isHub ? buildHubStatus() : null;
if (isHub) {
  const accountLink = h('a', { href: '/', class: 'btn sm ghost hub-account', title: '登入的帳號、改密碼、登出', 'aria-label': '帳號與安全' }, icon('shield'), h('span', { class: 'hub-account-text', text: '帳號與安全' }));
  // 多人協作：協作人沒有帳戶頁（/ 只給擁有者），改密碼與登出在設定的「我的帳號」
  accountLink.addEventListener('click', (e) => { if (isCollaborator()) { e.preventDefault(); openSettings({ section: 'account' }); } });
  $('btnSettings').before(accountLink);
  document.querySelector('.topbar').after(hubStatus.el);
  // 彈出視窗一開始就是對話檢視（store.js 的預設是儀表板）：init 之前不畫儀表板
  if (state.mode === 'popout') state.view = 'chat';
}

// 電腦狀態面板的開關（index.html 不動，這裡動態插入；設定鈕之前）
const btnMonitor = h('button', { type: 'button', id: 'btnMonitor', class: 'ibtn mon-toggle' }, icon('pulse'));
btnMonitor.addEventListener('click', () => toggleMonitor());
$('btnSettings').before(btnMonitor);

function buildHubStatus() {
  const text = h('span', { class: 'hub-status-text' });
  const retry = h('button', { type: 'button', class: 'btn sm hub-status-retry' }, icon('refresh'), h('span', { text: '立即重試' }));
  retry.addEventListener('click', () => retryNow());
  const el = h('div', { class: 'hub-status', role: 'status', 'aria-live': 'polite' }, icon('info', 'hub-status-ic'), text, retry);
  el.hidden = true;
  return { el, text, retry };
}

/** 連線狀態列（connectionBanner）：已連線而且收到 init 時隱藏；倒數只改文字節點 */
function renderHubStatus(now = Date.now()) {
  if (!hubStatus) return;
  const b = connectionBanner(state.connection, state.hubInit, now);
  hubStatus.el.hidden = !b;
  if (!b) return;
  hubStatus.el.dataset.tone = b.tone;
  hubStatus.el.dataset.kind = b.kind;
  if (hubStatus.text.textContent !== b.text) hubStatus.text.textContent = b.text;
  hubStatus.retry.hidden = !b.retry;
}

/**
 * 瀏覽器版：已連線而且這條連線收到過 init 才能操作儀表板（D26）。第一則 init 之前工作區與側欄也 inert、檢視切換與設定鈕停用
 * （那時送的 open 會被丟掉，WP4.6 D12）；之後斷線時工作區照常可以打字，送出由閘門擋（D14），每則 init 都重新 open（D9）。
 * 狀態變了通知對話（hub-live）更新送出與卡片的閘門
 */
function applyHubAvailability() {
  if (!isHub) return;
  const live = !!(state.connection && state.connection.state === 'online' && state.hubInit);
  dashboardEl.inert = !live;
  workspaceEl.inert = !state.ready;
  sidebarEl.inert = !state.ready;
  for (const b of document.querySelectorAll('#viewSwitch [data-view]')) b.disabled = !state.ready;
  $('btnSettings').disabled = !state.ready;
  renderHubStatus();
  emit('hub-live', live);
}

// ───── 模組初始化 ─────
ws.initWorkspace(workspaceEl);
const monitorEl = mon.initMonitor(workspaceEl, () => toggleMonitor(false));
const api = { workspace: ws.api, dashboard: { openAddAccount: dash.openAddAccount, accountMenu: dash.accountMenu } };
initSidebar(sidebarEl, api);
dash.initDashboard(dashboardEl, api);
sched.initSchedules(schedulesEl, api);
// 整頁不縮放（app.css 的 touch-action 一節）：iOS Safari 的兩指捏合只能擋它自己的 gesture 事件（viewport 的 user-scalable=no 新版 iOS 不理）；
// 燈箱的捏合放大走 pointer 事件、不受影響。其他瀏覽器沒有這幾個事件
for (const type of ['gesturestart', 'gesturechange', 'gestureend']) document.addEventListener(type, (e) => e.preventDefault(), { passive: false });
// 觸控的左右滑（drawer.js）：手機、平板、觸控筆電上的桌面 app 都一樣，往右滑叫出側欄、往左滑叫出電腦狀態，在面板上往外滑收起
const drawer = initDrawer({
  app: appEl, workspace: workspaceEl,
  active: () => state.ready && state.mode !== 'popout' && state.view === 'chat',
  refresh: () => applyChrome(),
  left: { el: sidebarEl, slidesInCss: phoneDrawer, isOpen: () => !state.sidebarCollapsed, setOpen: (open) => toggleSidebar(!open) },
  right: { el: monitorEl, slidesInCss: () => false, isOpen: monitorShown, setOpen: (open) => toggleMonitor(open), peek: () => mon.setShown(true) },
});
// 手機版：在側欄點了對話或開新對話就收起抽屜，直接看到對話
on('sidebar-picked', () => { if (phoneDrawer() && !state.sidebarCollapsed) toggleSidebar(true); });

on('want-view', (v) => setView(v));
on('active-changed', () => { markActive(); updateTitle(); });
on('conv-header', (id) => { ws.refreshTab(id); if (id === ws.activeConversationId()) updateTitle(); });

/** 換頁的進場（區塊最多錯開 5 × 40ms，各 300ms） */
const VIEW_FX_MS = 560;

function setView(v) {
  if (state.mode === 'popout') v = 'chat';
  // 多人協作：協作人只有對話（儀表板、排程都是擁有者的）
  if (isCollaborator() && v !== 'chat') return;
  if (v !== 'chat' && v !== 'dashboard' && v !== 'schedules') return;
  // 排程頁只在宿主掛了排程讀取器時有（瀏覽器版還沒有）
  if (v === 'schedules' && !sched.available()) return;
  if (state.view === v) return;
  if (state.view === 'schedules') sched.onHidden();
  state.view = v;
  applyChrome();
  ws.setChatVisible(v === 'chat');
  if (v === 'dashboard') { dash.render(true); dash.onShown(); }
  if (v === 'schedules') sched.onShown();
  // 第十批：換頁時新的一頁淡入、往上滑一點，儀表板與排程頁的區塊依序進場（fx.css 的 .fx-view-in；一次性，只動 transform／opacity）
  replayClass(v === 'dashboard' ? dashboardEl : v === 'schedules' ? schedulesEl : workspaceEl, 'fx-view-in', VIEW_FX_MS);
  ws.refreshTabs();
  ws.scheduleLayout();
  updateTitle();
}

function toggleSidebar(force) {
  if (state.mode === 'popout' || state.view !== 'chat') return;
  state.sidebarCollapsed = force === undefined ? !state.sidebarCollapsed : !!force;
  applyChrome();
  ws.scheduleLayout();
}

/** 電腦狀態面板現在顯示中（對話檢視、不是彈出視窗、要顯示） */
function monitorShown() {
  return state.mode !== 'popout' && state.view === 'chat' && monitorOpen();
}

/** 電腦狀態面板要不要顯示（不看檢視）：窄螢幕看這次開過沒有；否則照版面記的，沒記過時看視窗寬度 */
function monitorOpen() {
  if (isCollaborator()) return false; // 多人協作：電腦狀態是擁有者的
  if (narrowMq.matches) return monitorNarrowOpen;
  if (state.monitorPref === 'open') return true;
  if (state.monitorPref === 'closed') return false;
  return window.innerWidth >= MONITOR_AUTO_MIN_WIDTH;
}
let monitorNarrowOpen = false;

/** 顯示／收起電腦狀態面板（Ctrl+Shift+M、頂部列的開關、面板的 ×）：只在對話檢視；窄螢幕不記進版面 */
function toggleMonitor(force) {
  if (state.mode === 'popout' || state.view !== 'chat' || isCollaborator()) return;
  const next = force === undefined ? !monitorOpen() : !!force;
  if (narrowMq.matches) monitorNarrowOpen = next;
  else {
    state.monitorPref = next ? 'open' : 'closed';
    ws.scheduleLayout();
  }
  applyChrome();
}

function applyChrome() {
  const popout = state.mode === 'popout';
  document.body.classList.toggle('mode-popout', popout);
  // 排程頁和儀表板一樣是整頁（不顯示側欄、側欄鈕、電腦狀態的開關）
  appEl.classList.toggle('view-dashboard', state.view === 'dashboard' || state.view === 'schedules');
  appEl.classList.toggle('view-schedules', state.view === 'schedules');
  appEl.classList.toggle('sidebar-collapsed', !!state.sidebarCollapsed);
  dashboardEl.hidden = state.view !== 'dashboard';
  schedulesEl.hidden = state.view !== 'schedules';
  workspaceEl.hidden = state.view !== 'chat';
  // 手機版收起時不拿掉側欄：往左滑出畫面後才藏起來（app.css 的 visibility），遮罩跟著淡出
  const phone = phoneDrawer();
  // 觸控叫出中（拖曳中或落定前，drawer.js）的面板先不藏
  const held = (el) => !!drawer && drawer.holds(el);
  sidebarEl.hidden = (popout || state.view !== 'chat' || (!!state.sidebarCollapsed && !phone)) && !held(sidebarEl);
  if (drawer) drawer.scrim.hidden = sidebarEl.hidden || !phone;
  if (phone && state.sidebarCollapsed && sidebarEl.contains(document.activeElement)) document.activeElement.blur(); // 鍵盤不留在看不到的搜尋框
  setAccountsFoldable(phone || handheld());
  btnSidebar.setAttribute('aria-pressed', String(!state.sidebarCollapsed));
  btnSidebar.title = (state.sidebarCollapsed ? '顯示側欄' : '隱藏側欄') + '（Ctrl+B）';
  const monShown = monitorShown();
  monitorEl.hidden = !monShown && !held(monitorEl);
  btnMonitor.setAttribute('aria-pressed', String(monShown));
  btnMonitor.title = (monShown ? '隱藏電腦狀態' : '顯示電腦狀態') + '（Ctrl+Shift+M）';
  btnMonitor.setAttribute('aria-label', monShown ? '隱藏電腦狀態' : '顯示電腦狀態');
  mon.setShown(monShown || held(monitorEl));
  for (const b of document.querySelectorAll('#viewSwitch [data-view]')) {
    const on = b.dataset.view === state.view;
    b.classList.toggle('on', on);
    b.setAttribute('aria-selected', String(on));
  }
  renderScheduleSwitch();
}

/** 頂端的「排程」鈕：宿主掛了排程讀取器才顯示（彈出視窗、瀏覽器版沒有）；列入管理的排程最近一次失敗幾個寫在右上角 */
function renderScheduleSwitch() {
  const btn = document.querySelector('#viewSwitch [data-view="schedules"]');
  if (!btn) return;
  btn.hidden = state.mode === 'popout' || !sched.available();
  const n = scheduleBadgeCount(state.schedules);
  const badge = $('schedBadge');
  badge.hidden = !n;
  badge.textContent = n > 9 ? '9+' : String(n);
  // 失敗數變多時彈一下（第九批；第一次顯示不彈）
  if (badge._n !== undefined && n > badge._n) replayClass(badge, 'fx-pop-badge', 700);
  badge._n = n;
  btn.title = n ? `排程：${n} 個代理排程最近一次失敗` : '排程';
}

/** 視窗標題：app 名稱在前、目前對話的標題在後（titlerules.js；桌面的視窗標題跟著它） */
function updateTitle() {
  const cv = state.view !== 'chat' ? null : ws.getConv(ws.activeConversationId());
  document.title = pageTitle(state.view, cv && cv.header);
}

/**
 * 頂部列的 CLI 徽章：〔熱更新〕安裝不一致、更新中、安裝變動中、換版中與 CLI 卡同一套規則（hotupdate.js 的 cliBadgeState），再來是有新版
 */
function updateCliBadge() {
  const b = cliBadgeState(state.cli);
  cliBadge.hidden = !b;
  if (!b) { cliBadge.title = ''; return; }
  // 只改圖示與文字、不整個重建：下載進度約每 300ms 更新一次，重建會把按下中的目標移出 DOM，點了沒反應
  dash.setButtonContent(cliBadge, b.icon, b.text);
  cliBadge.dataset.tone = b.tone;
  cliBadge.dataset.state = b.key;
  cliBadge.title = b.title || '';
}

/** 同步小點：狀態、色調、文字與提示都取 remoterules.syncDot；只改有變的部分（推送頻繁，按下中的按鈕不能被換掉） */
function updateSyncDot() {
  if (!syncDotEl) return;
  const v = syncDot(state.remote && state.remote.sync);
  syncDotEl.hidden = !v;
  if (!v) { syncDotEl.title = ''; syncDotEl.removeAttribute('aria-label'); return; }
  syncDotEl.dataset.state = v.state;
  syncDotEl.dataset.tone = v.tone;
  const text = syncDotEl.querySelector('.sync-dot-text');
  if (text.textContent !== v.text) text.textContent = v.text;
  syncDotEl.title = v.title;
  syncDotEl.setAttribute('aria-label', v.text);
}

/** 品牌名旁的版號：桌面看 init.app 與 appUpdate，瀏覽器版看這次載入第一則 init 的 build、時間與目前的 init.app；只改有變的部分 */
function updateVersion() {
  const v = isHub ? hubVersionBadge(live.pageBuild(), state.app, live.pageCommitAt()) : desktopVersionBadge(state.app, state.appUpdate);
  verEl.hidden = !v;
  if (!v) { verEl.title = ''; verEl.removeAttribute('aria-label'); return; }
  const pre = verEl.querySelector('.app-ver-pre');
  const time = verEl.querySelector('.app-ver-time');
  const num = verEl.querySelector('.app-ver-num');
  if (pre.textContent !== v.prefix) pre.textContent = v.prefix;
  pre.hidden = !v.prefix;
  if (time.textContent !== v.time) {
    // versionTime 的 short 一律是「日期 時刻」：以最後一個空白分開
    const cut = v.time.lastIndexOf(' ');
    time.querySelector('.app-ver-date').textContent = cut > 0 ? v.time.slice(0, cut) : v.time;
    time.querySelector('.app-ver-clock').textContent = cut > 0 ? v.time.slice(cut + 1) : '';
  }
  time.hidden = !v.time;
  if (num.textContent !== v.text) num.textContent = v.text;
  verEl.dataset.tone = v.tone;
  verEl.title = v.title;
  verEl.setAttribute('aria-label', v.label);
}

// ───── 宿主訊息 ─────
const pending = new Map();
let flushTimer = 0;
let rafId = 0;

function queue(id) {
  let b = pending.get(id);
  if (!b) { b = { header: null, composer: null, ops: new Map() }; pending.set(id, b); }
  if (!rafId && !flushTimer && !ws.tabDragActive()) scheduleFlush();
  return b;
}

function scheduleFlush() {
  // 視窗隱藏時 rAF 不會觸發，改用計時器讓狀態維持最新
  if (document.hidden) flushTimer = setTimeout(flush, 250);
  else rafId = requestAnimationFrame(flush);
}

function flush() {
  if (rafId) { cancelAnimationFrame(rafId); rafId = 0; }
  if (flushTimer) { clearTimeout(flushTimer); flushTimer = 0; }
  // 拖曳分頁期間先排著（workspace.js 的 tabDragActive）：同一則項目只留最新的一版，拖完（tab-drag）一次補上
  if (ws.tabDragActive()) return;
  const batches = Array.from(pending.entries());
  pending.clear();
  for (const [id, b] of batches) {
    const cv = ws.getConv(id);
    if (!cv || !cv.loaded) continue;
    cv.applyBatch(b);
    if (b.header) ws.refreshTab(id);
  }
}

// 〔遠端〕即時更新的版本（itemPatch，patch1）：每個對話 id → 項目 id → 最後收到的即時版本 rv（item 帶 rv 時記下，持久的 item、removeItem、
// conversation、init 清掉）；增量對不上時重送 open 的時間（每個對話 10 秒最多一次）
const liveRv = new Map();
const patchReopenAt = new Map();

function noteRv(id, itemId, rv) {
  let t = liveRv.get(id);
  if (typeof rv === 'number') {
    if (!t) liveRv.set(id, (t = new Map()));
    t.set(itemId, rv);
  } else if (t) {
    t.delete(itemId);
    if (!t.size) liveRv.delete(id);
  }
}

/** itemPatch：基底是排著還沒套用的那一筆或畫面上那一筆；套用成功照 item 的路徑排進同一批，對不上就丟、重送 open（伺服器回整份 conversation） */
function onItemPatch(m) {
  const id = m.conversationId;
  const cv = ws.getConv(id);
  if (!cv || !cv.loaded || !m.id) return;
  const b = pending.get(id);
  const base = b && b.ops.has(m.id) ? b.ops.get(m.id) : cv.items.get(m.id);
  const t = liveRv.get(id);
  const next = applyItemPatch(base, t ? t.get(m.id) : undefined, m);
  if (next) {
    queue(id).ops.set(m.id, next);
    noteRv(id, m.id, m.rv);
    return;
  }
  noteRv(id, m.id, undefined);
  const now = Date.now();
  if (patchReopenDue(patchReopenAt.get(id), now)) {
    patchReopenAt.set(id, now);
    ws.reopen(id);
  }
}

function flushConv(id) {
  if (pending.has(id)) {
    const b = pending.get(id);
    pending.delete(id);
    const cv = ws.getConv(id);
    if (cv && cv.loaded) cv.applyBatch(b);
  }
}

// 拖曳分頁期間只留最新的一份、拖完再套用的整份清單：側欄、帳戶卡與分頁列重畫會跟拖放搶主執行緒（type → 訊息）
const HELD_WHILE_DRAGGING = new Set(['conversations', 'accounts']);
const heldWhileDragging = new Map();

// 拖曳分頁結束（或到期）：排著的整份清單與畫面更新一次補上
on('tab-drag', (dragging) => {
  if (dragging) return;
  const held = Array.from(heldWhileDragging.values());
  heldWhileDragging.clear();
  for (const m of held) handle(m);
  if (pending.size && !rafId && !flushTimer) scheduleFlush();
});

function handle(m) {
  if (!m || typeof m !== 'object' || !m.type) return;
  if (HELD_WHILE_DRAGGING.has(m.type) && ws.tabDragActive()) { heldWhileDragging.set(m.type, m); return; }
  const id = m.conversationId;
  switch (m.type) {
    case 'init': liveRv.clear(); heldWhileDragging.clear(); onInit(m); break;
    case 'accounts':
      setAccounts(m.accounts);
      renderAccounts();
      renderHistory();
      for (const cv of ws.allConvs()) cv.refreshAccount();
      ws.refreshTabs();
      dash.markDirty();
      dash.render();
      break;
    case 'conversations':
      setConversations(m.items);
      // 瀏覽器版的分頁圖示：執行中綠點、等你回應琥珀色加數字（桌面 app 有工作列與視窗外框）
      if (isHub) updateTabBadge(state.conversations);
      renderHistory();
      conversationsChanged();
      refreshListLinks();
      ws.refreshTabs();
      ws.settleAwaiting(m.replicasPending); // 〔同步〕還原版面時留著等清單的其他電腦的對話分頁
      break;
    case 'conversationSearch': applyConversationSearch(m); break; // 側欄搜尋的內容結果
    case 'collaborators': applyCollaborators(m.collaborators); break; // 多人協作：協作人清單與分享（擁有者）
    case 'collabResult': onCollabResult(m); break; // 多人協作：管理指令的結果（只給發起的視窗）
    case 'externalConversations': onExternalConversations(m); break; // 本機外部 CLI 歷史對話清單（只有桌面）
    case 'conversation':
      pending.delete(id);
      liveRv.delete(id);
      ws.handleConversation(m);
      updateTitle();
      break;
    case 'olderItems': {
      flushConv(id);
      const cv = ws.getConv(id);
      if (cv) cv.prependOlder(m);
      break;
    }
    case 'header':
      // 沒帶指令與模型清單的標頭（宿主沒變就省略，liverules.mergeHeaderLists）：同一批裡排著的那一份帶了就沿用它的
      if (ws.getConv(id) && m.header) { const q = queue(id); q.header = q.header ? mergeHeaderLists(q.header, m.header) : m.header; }
      break;
    case 'item': if (ws.getConv(id) && m.item && m.item.id) { queue(id).ops.set(m.item.id, m.item); noteRv(id, m.item.id, m.rv); } break;
    case 'itemPatch': onItemPatch(m); break;
    case 'removeItem': if (ws.getConv(id) && m.id) { queue(id).ops.set(m.id, null); noteRv(id, m.id, undefined); } break;
    case 'composer': if (ws.getConv(id)) queue(id).composer = m.composer || {}; break;
    case 'conversationClosed': pending.delete(id); liveRv.delete(id); forgetFlash(id); ws.handleClosed(id, m.replacedBy); markActive(); break; // 〔遠端〕replacedBy：草稿換成正式對話（D17）
    case 'focusConversation': ws.focusConversation(id); break;
    case 'conversationMoved': pending.delete(id); liveRv.delete(id); ws.handleMoved(id); markActive(); break; // 〔多視窗〕分頁搬到別的視窗了
    case 'tabDrag': ws.setExternalDrag(id); break; // 〔多視窗〕別的視窗正在拖分頁（null＝拖完了）
    // 〔桌面〕要你回來看的對話：一直閃到使用者點它（attnflash.js）；speaking＝語音通知開始念它，還在閃就讓標頭另外閃三下
    case 'flash': if (!m.speaking) startFlash(id); else if (isFlashing(id)) ws.flashConversation(id); break;
    case 'showView': if (state.mode !== 'popout') setView(m.view); break; // 系統匣選單「帳戶儀表板」
    case 'files': { const cv = ws.getConv(id); if (cv && cv.drawer) cv.drawer.apply(m); break; }
    case 'fileSearch': { const cv = ws.getConv(id); if (cv) cv.composer.applyFileSearch(m); break; }
    case 'gitStatus': { const cv = ws.getConv(id); if (cv) cv.setGit(m.git || null); break; } // 工作資料夾的 git 狀態（只有桌面）
    case 'launchStatus': { const cv = ws.getConv(id); if (cv) cv.setLaunch(m.launch || null); break; } // 執行列（只有桌面）
    case 'launchLog': { const cv = ws.getConv(id); if (cv) cv.applyLaunchLog(m); break; }
    case 'promptHistory': { const cv = ws.getConv(id); if (cv) cv.applyPromptHistory(m); break; } // 「最近的指令」的歷史指令（只有桌面）
    // 輸入框下方那一行的燈號（只有桌面）：資料流與硬碟是各對話的、網路是整台電腦的，每個開著的對話各自挑自己的
    // 燈號是給人看的即時資料：拖曳分頁期間略過（下一筆幾百毫秒後就到）
    case 'activity': if (!ws.tabDragActive()) for (const cv of ws.allConvs()) if (cv.composer) cv.composer.applyActivity(m); break;
    case 'cli':
      state.cli = Object.assign({}, state.cli, m.cli || {});
      // 〔同步〕帶 devicesCli 這個鍵（含 null）就整份取代各電腦的 CLI；沒帶是只有這台的 CLI 變了
      if (Object.prototype.hasOwnProperty.call(m, 'devicesCli')) state.devicesCli = m.devicesCli ?? null;
      updateCliBadge();
      dash.markDirty();
      dash.render();
      refreshSettings();
      break;
    case 'settings':
      state.settings = Object.assign({}, state.settings, m.settings || {});
      applySettings(state.settings);
      for (const cv of ws.allConvs()) { cv.composer.updatePlaceholder(); cv.composer.updateSend(); cv.composer.autosize(); } // 字級改了，多行草稿要重算高度
      refreshSettings();
      dash.markDirty();
      dash.render();
      break;
    // 沒有送出：對話欄裡那則標成沒有送出（桌面帶 reason），字照舊還回輸入框（輸入框是空的才還）
    case 'rewound': { flushConv(id); const cv = ws.getConv(id); if (cv) cv.composer.putBack(m.text || ''); break; }
    case 'steerCancelled': { flushConv(id); const cv = ws.getConv(id); if (cv) cv.composer.putBack(m.text || ''); break; }
    case 'sendRejected': { flushConv(id); const cv = ws.getConv(id); if (cv) { cv.failSending(m.text, failureOf({ reason: m.reason })); cv.restoreText(m.text); } break; }
    case 'peerUpload': onPeerUpload(m); break; // 〔桌面遠端操作〕宿主代傳的上傳結果（composer.js）
    case 'draftSnapshotRequest':
      post({ cmd: 'draftSnapshot', requestId: m.requestId, drafts: ws.draftTexts() });
      break;
    case 'appUpdate':
      state.appUpdate = m.state || null;
      renderAppBanner();
      refreshAppSettings();
      updateVersion();
      break;
    case 'toast': toast(m.text, m.level); break;
    case 'tokens': state.tokens = m.tokens && typeof m.tokens === 'object' ? m.tokens : null; renderTokens(); break;
    // 儀表板的健康度（只有桌面）：帳戶卡的一行、電腦層級的指示檔、開著的明細對話框
    case 'health': state.health = m.health && typeof m.health === 'object' ? m.health : null; dash.healthChanged(); break;
    case 'mcp': state.mcp = m.mcp && typeof m.mcp === 'object' ? m.mcp : null; dash.mcpChanged(); break;
    // 排程頁（只有桌面）：這台的 Windows 排程、頂端按鈕的失敗數
    case 'schedules':
      state.schedules = m.schedules && typeof m.schedules === 'object' ? m.schedules : null;
      renderScheduleSwitch();
      if (state.view === 'schedules' && !sched.available()) setView('chat');
      sched.changed();
      break;
    case 'usageSegments': state.usageSegments = m.usageSegments && typeof m.usageSegments === 'object' ? m.usageSegments : null; dash.usageSegmentsChanged(); break;
    case 'accountLogin': onLogin(m); break;
    case 'devices': state.remote = withDeviceLinks(applyDevices(state.remote, m.devices), state.remote); remoteChanged(); break;
    case 'sync': state.remote = applySync(state.remote, m.sync); remoteChanged(); break;
    case 'connection': onConnection(m); break;
    case 'deviceStats': mon.applyStats(m); break;
    default: live.handle(m); break;
  }
}

// 瀏覽器版：上一條已經可以用的連線斷掉的時間（恢復時超過 3 秒才提示「已重新連線」）
let hubLostAt = 0;

/** 瀏覽器版的連線狀態（transport-hub 產生）：每條新連線先 online、收到 init 才算可以用 */
function onConnection(m) {
  const wasLive = !!(state.connection && state.connection.state === 'online' && state.hubInit);
  state.connection = { state: m.state, reason: m.reason ?? null, retryAt: m.retryAt ?? null };
  if (m.state !== 'online' && wasLive) hubLostAt = Date.now();
  state.hubInit = false;
  applyHubAvailability();
}

/** 〔同步〕remote 變了（init、sync、devices 之後）：儀表板、側欄、設定頁重畫，再通知其他模組 */
function remoteChanged() {
  updateSyncDot();
  dash.markDirty();
  dash.render();
  renderAccounts();
  refreshSettings();
  mon.remoteChanged();
  emit('remote-changed');
}

/** 〔同步〕新的裝置清單標上剛斷線的電腦（remoterules.trackDeviceLinks：和上一份比，「重新連線中」） */
function withDeviceLinks(remote, before) {
  if (!remote) return remote;
  const prev = before && Array.isArray(before.devices) ? before.devices : null;
  return Object.assign({}, remote, { devices: trackDeviceLinks(prev, remote.devices, Date.now(), reconnectGraceOf(remote)) });
}

// 〔同步〕「重新連線中」到期時重算、重畫一次（變成「離線」）；remote-changed 之後依最早到期的那台重排
let linkTimer = 0;
on('remote-changed', () => {
  clearTimeout(linkTimer);
  const wait = nextLinkChange(state.remote ? state.remote.devices : null, Date.now(), reconnectGraceOf(state.remote));
  if (wait === null) return;
  linkTimer = setTimeout(() => {
    if (!state.remote) return;
    state.remote = withDeviceLinks(state.remote, state.remote);
    remoteChanged();
  }, wait + 50);
});

/** 開機掃描只播一次（第一則 init；之後重新連線、設定重送的 init 不播） */
let booted = false;

function onInit(m) {
  state.mode = m.mode === 'popout' ? 'popout' : (m.mode === 'main' ? 'main' : state.mode);
  if (m.popoutConversationId) state.popoutId = m.popoutConversationId;
  state.windowRole = m.windowRole === 'primary' || m.windowRole === 'extra' ? m.windowRole : null;
  state.app = m.app || {};
  state.settings = Object.assign({}, state.settings, m.settings || {});
  applySettings(state.settings);
  // 開機掃描（fx.css 的 .app.fx-boot：網格淡出、一道掃描光由上往下掃過一次，約 0.9 秒）：app 打開時播一次，設定套用之後才判斷要不要動
  if (!booted) { booted = true; replayClass(appEl, 'fx-boot', 1100); initSpotlight(); }
  setAccounts(m.accounts);
  setConversations(m.conversations);
  if (isHub) updateTabBadge(state.conversations);
  setProviders(m.providers);
  state.tokens = m.tokens && typeof m.tokens === 'object' ? m.tokens : null;
  state.health = m.health && typeof m.health === 'object' ? m.health : null;
  state.schedules = m.schedules && typeof m.schedules === 'object' ? m.schedules : null; // 排程頁（只有桌面；沒掛讀取器是 null）
  // 多人協作：這份網頁是誰在用（協作人只看分享給他的對話與自己的介面設定）、擁有者看的協作人清單
  state.viewer = m.viewer && typeof m.viewer === 'object' ? m.viewer : null;
  applyCollaborators(m.collaborators ?? null);
  applyViewerChrome();
  state.voiceInput = m.voiceInput === true; // 桌面這台的語音訊息辨識（voicemsg.js）
  state.cli = Object.assign(Object.fromEntries(providerKeys().map((p) => [p, null])), m.cli || {});
  // 〔同步〕第二則 init（伺服器的 resync、瀏覽器重連）整份取代：沒帶 remote 就是沒有同步
  state.remote = withDeviceLinks(remoteFromInit(m), state.remote);
  state.devicesCli = m.devicesCli ?? null;
  updateSyncDot();
  state.appUpdate = m.appUpdate || null;
  renderAppBanner();
  pending.clear();
  cmdtrack.abandonAll(); // 〔遠端〕新的 init：還在等結果的指令都當成結果不明（D5；卡片解鎖並說明，不自動重送）
  if (isHub) live.onInit(m, !state.ready); // 〔遠端〕比對 app.build：伺服器換版時重新載入或顯示橫幅（D13）
  updateVersion(); // 瀏覽器版要在 live.onInit 記下第一則的 build 之後
  if (isHub) state.hubInit = true;
  if (isHub && state.ready) {
    // 瀏覽器版之後的 init（重連、伺服器 resync；伺服器清掉了這條連線開著的對話）：不重建窗格、輸入框的字留著，
    // 對每個分頁（含草稿）重送 open、重送 visible（WP4.6 D9）
    renderAccounts();
    renderHistory();
    ws.reopenAll();
  } else if (state.mode === 'popout') {
    state.view = 'chat';
    state.sidebarCollapsed = true;
    applyChrome();
    renderAccounts();
    renderHistory();
    ws.restorePopout(state.popoutId);
  } else {
    // 瀏覽器版的第一則 init 也走這裡（版面是傳輸層從這個瀏覽器注入的）：沒有存下的版面時是儀表板（D10）、
    // 沒有分頁時不自動開草稿、沒有 app 更新還原的草稿
    const layout = m.layout || null;
    state.sidebarCollapsed = !!(layout && layout.sidebarCollapsed);
    state.filterDevice = layoutDeviceFilter(layout); // 〔同步〕側欄的電腦篩選（WP4.6 D31 的「全部／本機」延伸成下拉）
    state.monitorPref = layout && (layout.monitor === 'open' || layout.monitor === 'closed') ? layout.monitor : null; // 電腦狀態面板
    state.view = isCollaborator() ? 'chat' : (isHub && !layout) || (layout && layout.view === 'dashboard') ? 'dashboard'
      : layout && layout.view === 'schedules' && sched.available() ? 'schedules' : 'chat';
    applyChrome();
    renderAccounts();
    renderHistory();
    // 〔多視窗〕movedIn：為了搬進對話而開的新視窗，那個對話和還原的草稿一樣由宿主在 init 之後送
    ws.restore(layout, isHub ? [] : [...(Array.isArray(m.restoredDrafts) ? m.restoredDrafts : []), ...(Array.isArray(m.movedIn) ? m.movedIn : [])], { autoNew: !isHub, replicasPending: !isHub && m.replicasPending === true });
    if (state.view !== 'chat') ws.setChatVisible(false);
  }
  state.ready = true;
  dash.markDirty();
  dash.render(state.view === 'dashboard');
  if (state.view === 'dashboard') dash.onShown();
  sched.markDirty();
  if (state.view === 'schedules') sched.onShown();
  refreshSettings();
  emit('remote-changed');
  updateCliBadge();
  updateTitle();
  mon.afterInit(); // 宿主在 ready、重連時忘了 monitor：照目前狀態再送一次
  refreshContentSearch(); // 側欄搜尋框有字時重搜內容（瀏覽器版斷線前的請求，結果送不到新的連線）
  appEl.classList.add('ready');
  if (isHub) {
    applyHubAvailability();
    if (hubLostAt && Date.now() - hubLostAt > 3000) toast('已重新連線', 'success');
    hubLostAt = 0;
  }
}

function onLogin(m) {
  if (!m.accountId) return;
  // WP3.4：Codex 裝置碼登入進行中的訊息帶 deviceCode {userCode, verificationUrl, expiresAt}（帳戶卡以大字顯示）；done 時清掉
  const deviceCode = !m.done && m.deviceCode && typeof m.deviceCode === 'object' ? m.deviceCode : null;
  // Claude 的遠端登入（2026-10-26）：第一則帶 loginUrl（帳戶卡給連結與貼授權碼的輸入框）
  const loginUrl = !m.done && typeof m.loginUrl === 'string' ? m.loginUrl : null;
  state.login.set(m.accountId, { message: m.message || '', done: !!m.done, success: m.success, at: Date.now(), deviceCode, loginUrl });
  dash.markDirty();
  dash.render();
  if (m.done) {
    toast(m.message || (m.success === false ? '登入沒有完成' : '登入完成'), m.success === false ? 'error' : 'success');
    const at = state.login.get(m.accountId).at;
    setTimeout(() => {
      const cur = state.login.get(m.accountId);
      if (cur && cur.at === at) { state.login.delete(m.accountId); dash.markDirty(); dash.render(); }
    }, 8000);
  }
}

onHostMessage(handle);

// ───── 全域事件 ─────
// 回覆中的本機檔案提供預覽與下載；一般網址交給宿主或瀏覽器的傳輸層。
// 瀏覽器版由傳輸層在瀏覽器裡處理（同站的「帳號與安全」直接導覽、外站開新分頁，transport-hub.js 的 openLink）
document.addEventListener('click', (e) => {
  const a = e.target.closest && e.target.closest('a[href]');
  if (!a) return;
  e.preventDefault();
  if (openFileLink(a)) { e.stopPropagation(); return; }
  const href = a.getAttribute('href');
  if (!href || href.startsWith('#') || /^javascript:/i.test(href)) return;
  post({ cmd: 'openLink', href: a.href });
}, true);
document.addEventListener('auxclick', (e) => {
  const a = e.target.closest && e.target.closest('a[href]');
  if (!a) return;
  e.preventDefault();
  if (e.button === 1) {
    if (openFileLink(a)) { e.stopPropagation(); return; }
    const href = a.getAttribute('href');
    if (href && !href.startsWith('#') && !/^javascript:/i.test(href)) post({ cmd: 'openLink', href: a.href });
  }
}, true);

// 檔案拖到窗格以外的地方：不讓 WebView 導覽到檔案
const hasFiles = (e) => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');
document.addEventListener('dragover', (e) => { if (hasFiles(e)) e.preventDefault(); });
document.addEventListener('drop', (e) => {
  if (!hasFiles(e)) return;
  e.preventDefault();
  if (!e.target.closest || !e.target.closest('.conv')) toast('把檔案拖到對話窗格上就能附加');
});

// 預設右鍵選單只留給可編輯欄位與選取的文字
document.addEventListener('contextmenu', (e) => {
  const t = e.target;
  if (t.closest && t.closest('input, textarea, [contenteditable="true"]')) return;
  const sel = window.getSelection();
  if (sel && String(sel).trim()) return;
  e.preventDefault();
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && !e.defaultPrevented) {
    if (handleEscape()) { e.preventDefault(); return; }
  }
  if (!e.ctrlKey || e.altKey || e.metaKey) return;
  if (hasLayer()) return;
  const k = e.key.length === 1 ? e.key.toLowerCase() : e.key;
  const popout = state.mode === 'popout';
  if (k === 'n' && e.shiftKey) {
    // 〔多視窗〕開新視窗（像 Chrome 的 Ctrl+N；這裡的 Ctrl+N 是開新對話）：只有桌面的完整視窗
    e.preventDefault();
    if (!isHub && !popout) post({ cmd: 'newWindow' });
  } else if (k === 'n' && !e.shiftKey) {
    e.preventDefault();
    if (!popout && !(isHub && !state.ready)) { // 瀏覽器版：第一則 init 之前沒有帳戶與電腦可選
      const cv = ws.getConv(ws.activeConversationId());
      ws.newConversation(cv && cv.header ? cv.header.accountId : null);
    }
  } else if (k === 'w' && !e.shiftKey) {
    e.preventDefault();
    if (!popout && state.view === 'chat') ws.closeActiveTab();
  } else if (e.key === 'Tab') {
    e.preventDefault();
    if (!popout && state.view === 'chat') ws.cycleTab(e.shiftKey ? -1 : 1);
  } else if (e.key === '\\') {
    e.preventDefault();
    if (!popout && state.view === 'chat') ws.splitPane();
  } else if (k === 'b' && !e.shiftKey) {
    e.preventDefault();
    toggleSidebar();
  } else if (k === 'm' && e.shiftKey) {
    e.preventDefault();
    toggleMonitor();
  } else if (e.key === ',') {
    e.preventDefault();
    if (isHub && !state.ready) return; // 瀏覽器版：第一則 init 之前沒有設定可改
    openSettings();
  }
});

// 單一 1 秒計時器：只更新可見的經過時間；倒數與相對時間每分鐘更新
let lastMinute = Math.floor(Date.now() / 60000);
setInterval(() => {
  cmdtrack.sweep(Date.now()); // 〔遠端〕等不到結果的指令 150 秒後當成結果不明：放在 hidden 判斷之前，背景分頁的卡片也會解鎖（D5）
  if (document.hidden) return;
  const now = Date.now();
  for (const cv of ws.visibleConvs()) cv.tick(now);
  const minute = Math.floor(now / 60000);
  if (minute !== lastMinute) { lastMinute = minute; dash.tickMinute(now); tickSidebar(now); sched.tickMinute(); }
  mon.tick(now); // 電腦狀態面板：幾秒前、資料過時、量表外圈的轉動（面板看不到時直接返回）
  // 瀏覽器版的狀態列：「N 秒後重試」的倒數
  if (hubStatus && !hubStatus.el.hidden && state.connection && state.connection.retryAt) renderHubStatus(now);
}, 1000);

document.addEventListener('visibilitychange', () => {
  if (!document.hidden && pending.size) flush();
  mon.syncMonitor(); // 頁面藏起來時告訴宿主不用再送電腦狀態
});
// 視窗寬度：電腦狀態面板沒選過顯示或收起時跟著寬度自動顯示；跨過窄螢幕的門檻時換成抽屜（或換回來）
const relayoutMonitor = debounce(() => { if (state.monitorPref === null || narrowMq.matches) applyChrome(); }, 150);
window.addEventListener('resize', relayoutMonitor);
narrowMq.addEventListener('change', () => applyChrome());
coarseMq.addEventListener('change', () => applyChrome()); // 平板接上或拆下鍵盤：帳戶區變成可以收起（或換回來）

applyChrome();
if (!hasBridge) {
  workspaceEl.append(h('div', { class: 'no-host' }, icon('info'), h('span', { text: '這個頁面要在 Clode Myriagent 裡開啟。' })));
}
applyHubAvailability();
post(state.mode === 'popout' ? { cmd: 'ready', mode: 'popout', conversationId: state.popoutId } : { cmd: 'ready', mode: 'main' });
