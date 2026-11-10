// 儀表板：摘要、帳戶大卡（每個時間窗一個大圓環＋走勢線、健康度的一行）、新增帳戶、電腦層級的指示檔（health.js）、CLI 區（含〔熱更新〕的換版狀態）；帳戶相關對話框
// 〔同步〕（WP3.5）：「帳戶 × 電腦」矩陣與「各電腦 CLI」（remote.js）、帳戶卡的裝置小點；桌面的 Codex 裝置碼登入入口與大字驗證碼。
// 瀏覽器版（isHub）只畫伺服器接受的操作：沒有新增帳戶、新對話、更新用量、登入、這台的 CLI 區；帳戶選單只有改名、改色、上移、下移、
// 〔遠端〕在所有電腦登出（WP4.8）與從目錄刪除；對指定電腦的登入、登出與 CLI 操作在矩陣與各電腦 CLI 卡上（remoteaccounts.js）
// remoteOnly 帳戶（桌面：目錄裡有、這台沒有）：灰色的卡，按鈕與選單都只有「在這台登入」，不算進摘要的帳戶數與新增帳戶的計數
// （卡上與摘要的對話數照算其他在線電腦的 otherSessions：其他電腦在跑時一樣顯示執行中）
import { h, iconButton, reconcile, setAcc, setPulse, setTempo, safeColor, fmtReset, fmtRelative, fmtDateTime, fmtSpan, pctText, setHidden, copyToClipboard, DEFAULT_COLORS, replayClass } from './util.js';
import { icon, providerMark } from './icons.js';
import { post, state, accountOf, summaryOf, STATUS_LABEL, providerKeys, providerInfo, isHub, on } from './store.js';
import { openMenu, openDialog, confirmDialog, toast, spinner, showTip, hideTip, switchControl, handleEscape } from './ui.js';
import {
  cliName, enumKey, restartStateOf, restartIcon, versionArrow, cliRestartSummary, cliRestartDoneText, cliHotState, UPDATE_PHASES, updatePhaseInfo,
  overdueQuestion, RESTART_POLICIES, policyOf, confirmApplyNowProvider,
} from './hotupdate.js';
import { renderMatrix, renderDeviceCli, renderDevices, deviceStrip, deviceStripSig, confirmDeleteAccount, syncServer, loginHere } from './remote.js';
import { matrixVisible, reorderIds, loginCodeView, claudeLoginUrlView, loginCodeInput, countLocalAccounts, remoteOnlyElsewhere, sessionTotals, sessionTitle, subagentsTitle, subagentsTooltip } from './remoterules.js';
// 〔遠端〕瀏覽器版發起的帳戶操作（WP4.8）：登入追蹤、驗證碼上方的「在〈電腦〉」、帳戶選單的「在所有電腦登出…」
import { tickRemoteLogins, originLabelOf, loginDeviceOf, logoutAllMenuItem, REMOTE_ACCOUNTS_CHANGED } from './remoteaccounts.js';
// 健康度（只有桌面）：帳戶卡的一行與明細、電腦層級的指示檔
import { setHealthWorkspace, requestHealth, createHealthStrip, updateHealthStrip, refreshHealthDialog, createInstructionsSection, renderInstructionsSection } from './health.js';
import { requestMcp, createMcpStrip, updateMcpStrip, refreshMcpDialog } from './mcp.js';
import { compactPath } from './healthrules.js';
// 每週額度每 5% 的 token（只有桌面）
import { requestUsageSegments, createUsageSegmentsSection, renderUsageSegmentsSection } from './usageseg.js';
import { loginExpiry, loginExpiryText } from './loginrules.js';
import { tokenCounter } from './tokenfx.js';
import { quotaLocked, resetCountdown, countdownCells, accountPulse, pulsePhase, burnForecast, resetCreditsInfo, resetCreditAction, creditPips, creditExpiryCells, creditUsed, CREDIT_EXPIRY_CELLS } from './fxrules.js';

const NS = 'http://www.w3.org/2000/svg';
const RING_R = 52;
const RING_SCALE_R = 44;
const Q_LOCK_FX_MS = 900; // 額度用完那一下圓環鎖上（fx.css 的 .ring-col.fx-q-lock）
const CD_CELLS = 20;      // 重置倒數條的格數（fx.css 的 .ring-cd 同樣 20 格）

export const PALETTE = [
  { c: '#D97757', n: '珊瑚' }, { c: '#E0A33B', n: '琥珀' }, { c: '#8DB255', n: '橄欖' }, { c: '#1FAE8E', n: '青綠' }, { c: '#2FA6C8', n: '湖水' },
  { c: '#4C8DF6', n: '天藍' }, { c: '#7A7CF0', n: '靛藍' }, { c: '#A872E0', n: '紫' }, { c: '#D466A8', n: '洋紅' }, { c: '#8A94A6', n: '石板灰' },
];

let ws = null;
let els = null;
let dirty = true;

export function initDashboard(container, api) {
  ws = api.workspace;
  setHealthWorkspace(ws);
  const summary = h('div', { class: 'dash-summary', role: 'status' });
  const accGrid = h('div', { class: 'acct-grid' });
  // 〔同步〕帳戶 × 電腦（瀏覽器版一律有；桌面在已配對、有連結到目錄的帳戶時才出現）與各電腦 CLI
  const mxWrap = h('div', { class: 'mx-wrap' });
  const mxSec = h('section', { class: 'dash-sec dash-sec-mx', 'aria-label': '帳戶 × 電腦', hidden: true },
    h('div', { class: 'dash-sec-head dash-sec-gap' }, h('h2', { class: 'dash-h', text: '帳戶 × 電腦' })), mxWrap);
  // 〔同步〕電腦：改名；瀏覽器版另可撤銷、移除已撤銷的、新增電腦（輸入配對碼）；桌面寫明撤銷與移除要到網頁版
  const devGrid = h('div', { class: 'dev-grid' });
  const devNote = h('p', { class: 'dev-note', hidden: true });
  const devSec = h('section', { class: 'dash-sec dash-sec-dev', 'aria-label': '電腦', hidden: true },
    h('div', { class: 'dash-sec-head dash-sec-gap' }, h('h2', { class: 'dash-h', text: '電腦' })), devNote, devGrid);
  const dcliGrid = h('div', { class: 'dcli-grid' });
  const dcliSec = h('section', { class: 'dash-sec dash-sec-dcli', 'aria-label': '各電腦 CLI', hidden: true },
    h('div', { class: 'dash-sec-head dash-sec-gap' }, h('h2', { class: 'dash-h', text: '各電腦 CLI' })), dcliGrid);
  if (isHub) {
    // 瀏覽器版：伺服器不接受新增帳戶、更新用量與 CLI 的操作（web-protocol 第六節第 9 點），這些按鈕一個都不建
    container.append(h('div', { class: 'dash-inner' }, summary, h('div', { class: 'dash-sec-head' }, h('h2', { class: 'dash-h', text: '帳戶' })), accGrid, mxSec, devSec, dcliSec));
    els = { container, summary, accGrid, addCard: null, cliGrid: null, autoSw: null, policyBtn: null, hxSec: null, usSec: null, mxSec, mxWrap, devSec, devNote, devGrid, dcliSec, dcliGrid };
    return;
  }
  const addCard = h('button', { type: 'button', class: 'acard-add' }, h('span', { class: 'acard-add-ic' }, icon('plus')), h('span', { class: 'acard-add-title', text: '新增帳戶' }),
    h('span', { class: 'acard-add-sub', text: '另一個 Claude、Codex 或 Grok 訂閱（使用獨立的設定資料夾），或這台電腦的 Antigravity' }));
  addCard.addEventListener('click', () => openAddAccount());
  const cliGrid = h('div', { class: 'cli-grid' });
  const autoSw = switchControl({ checked: state.settings.autoUpdateCli !== false, label: '自動更新 CLI', onChange: (v) => setSetting('autoUpdateCli', v) });
  const checkBtn = cliCheckButton();
  // 〔熱更新〕CLI 更新後的切換時機（與設定對話框的「CLI 更新後」相同）：下拉晶片，點了開選單
  const policyBtn = h('button', { type: 'button', class: 'chip cli-policy', 'aria-haspopup': 'menu' },
    icon('refresh', 'chip-ic'), h('span', { class: 'chip-label' }), icon('chevDown', 'chip-chev'));
  policyBtn.addEventListener('click', () => openPolicyMenu(policyBtn));
  const refreshAll = h('button', { type: 'button', class: 'btn sm' }, icon('refresh'), h('span', { text: '更新全部用量' }));
  refreshAll.addEventListener('click', () => post({ cmd: 'accountRefresh', accountId: null }));
  const hxSec = createInstructionsSection();
  const usSec = createUsageSegmentsSection();
  const inner = h('div', { class: 'dash-inner' },
    summary,
    h('div', { class: 'dash-sec-head' }, h('h2', { class: 'dash-h', text: '帳戶' }), h('span', { class: 'grow' }), refreshAll),
    accGrid,
    usSec,
    hxSec,
    mxSec,
    devSec,
    h('div', { class: 'dash-sec-head cli-head' }, h('h2', { class: 'dash-h', text: 'CLI' }), h('span', { class: 'grow' }),
      policyBtn, h('label', { class: 'inline-switch' }, autoSw, h('span', { text: '沒有對話執行時自動更新' })), checkBtn),
    cliGrid,
    dcliSec);
  container.append(inner);
  els = { container, summary, accGrid, addCard, cliGrid, autoSw, policyBtn, hxSec, usSec, mxSec, mxWrap, devSec, devNote, devGrid, dcliSec, dcliGrid };
  syncPolicyButton();
}

/** 〔同步〕帳戶 × 電腦與各電腦 CLI：瀏覽器版一律顯示（沒有資料時寫說明）；桌面在已配對而且有內容時才出現 */
function renderRemoteSections(now = Date.now()) {
  const showMatrix = matrixVisible(state.accounts, state.remote);
  els.mxSec.hidden = !showMatrix;
  if (showMatrix) renderMatrix(els.mxWrap, now);
  // 電腦：瀏覽器版一律有（至少有「新增電腦」）；桌面在已配對、有電腦清單時才出現
  const showDevices = !!state.remote && (isHub || (Array.isArray(state.remote.devices) && state.remote.devices.length > 0));
  els.devSec.hidden = !showDevices;
  if (showDevices) {
    renderDevices(els.devGrid);
    if (!isHub) renderDevNote();
  }
  const n = renderDeviceCli(els.dcliGrid);
  els.dcliSec.hidden = !isHub && !n;
}

/** 桌面的電腦區：撤銷與移除要到網頁版的電腦頁（伺服器網址取同步設定，不寫死；https 才做成連結） */
function renderDevNote() {
  const server = syncServer();
  const note = els.devNote;
  if (note._server === server && !note.hidden) return;
  note._server = server;
  note.textContent = '';
  let link = null;
  if (server && /^https:\/\//i.test(server)) {
    try { link = new URL('/app/', server).href; } catch { link = null; }
  }
  note.append(h('span', { text: '要撤銷或移除電腦，請到網頁版的電腦頁操作' }),
    server ? h('span', null, h('span', { text: '（' }), link ? h('a', { href: link, text: server }) : h('span', { text: server }), h('span', { text: '）' })) : null,
    h('span', { text: '。' }));
  note.hidden = false;
}

/** 儀表板 CLI 區的「CLI 更新後：…」：顯示目前的政策，點了開選單切換（送 setting cliRestartPolicy） */
function syncPolicyButton() {
  if (!els || !els.policyBtn) return;
  const p = policyOf(state.settings.cliRestartPolicy);
  const label = els.policyBtn.querySelector('.chip-label');
  const text = 'CLI 更新後：' + p.short;
  if (label.textContent !== text) label.textContent = text;
  els.policyBtn.title = 'CLI 在對話執行中被更新時，什麼時候換成新版\n目前：' + p.label;
}

function openPolicyMenu(anchor) {
  const cur = policyOf(state.settings.cliRestartPolicy).value;
  openMenu(anchor, [{ type: 'header', label: 'CLI 更新後' }].concat(RESTART_POLICIES.map((p) => ({
    label: p.label + (p.tag ? '（' + p.tag + '）' : ''), desc: p.desc, checked: p.value === cur,
    onSelect: () => { if (p.value !== cur) { setSetting('cliRestartPolicy', p.value); syncPolicyButton(); } },
  }))), { placement: 'bottom-end', minWidth: 320, className: 'menu-policy' });
}

export function markDirty() { dirty = true; }

/**
 * 切到儀表板（含一開始就在儀表板）：請宿主分析健康度（宿主 30 秒內分析過、帳戶沒變就只重送結果）、檢查各 Claude 帳戶的 MCP
 * （宿主 5 分鐘內查過就只重送）
 */
export function onShown() { requestHealth(false); requestMcp(false); requestUsageSegments(); revealSparks(); }

const SPARK_FX_MS = 1100; // 走勢線畫出來（fx.css 的 .spark.fx-wipe：0.7 秒＋錯開＋末端的點淡入）
/**
 * 打開儀表板：走勢線由左往右畫出來（fx.css 的 .spark.fx-wipe：外層與走勢線反方向位移，看起來線不動、露出的範圍往右長，只動 transform）；
 * 各張卡錯開一點。減少動態、關掉動態效果時 CSS 不播
 */
function revealSparks() {
  if (!els) return;
  let i = 0;
  for (const b of els.container.querySelectorAll('.spark')) {
    if (!b._spark) continue;
    const k = Math.min(i++, 8);
    b.style.setProperty('--fx-i', k);
    replayClass(b, 'fx-wipe', SPARK_FX_MS + k * 40);
  }
}

/** mcp 訊息到了：帳戶卡的 MCP 一行、開著的明細對話框 */
export function mcpChanged() {
  dirty = true;
  render();
  refreshMcpDialog();
}

/** usageSegments 訊息到了：每週額度每 5% 的 token */
export function usageSegmentsChanged() {
  if (!els || !els.usSec) return;
  if (state.view === 'dashboard') renderUsageSegmentsSection(els.usSec);
  else dirty = true;
}

/** health 訊息到了：帳戶卡的一行、電腦層級的指示檔、開著的明細對話框 */
export function healthChanged() {
  dirty = true;
  render();
  refreshHealthDialog();
}

// 〔遠端〕登入追蹤或遠端「立即檢查」結束（remoteaccounts.js 每秒的計時器、按下按鈕）：重畫
on(REMOTE_ACCOUNTS_CHANGED, () => { dirty = true; render(); });

export function render(force) {
  if (!els) return;
  // 每則 cli 訊息都會呼叫 render：「立即檢查」是否完成不論目前在哪個檢視都要判斷（設定對話框也有這顆按鈕）
  settleCliCheck();
  // 〔遠端〕這個分頁發起的遠端登入與「立即檢查」也一樣：不論目前在哪個檢視都要判斷（清掉驗證碼、提示結果）
  if (tickRemoteLogins()) dirty = true;
  if (!force && state.view !== 'dashboard' && !dirty) return;
  if (state.view !== 'dashboard' && !force) { dirty = true; return; }
  dirty = false;
  renderSummary();
  // 瀏覽器版沒有「新增帳戶」卡（伺服器不接受 accountAdd）
  const entries = state.accounts.map((a) => ({ a }));
  if (els.addCard) entries.push({ add: true });
  reconcile(els.accGrid, entries, (x) => (x.add ? '+add' : x.a.id),
    (x) => (x.add ? els.addCard : createCard(x.a)), (node, x) => { if (!x.add) updateCard(node, x.a); });
  renderInstructionsSection(els.hxSec);
  renderUsageSegmentsSection(els.usSec);
  renderRemoteSections();
  if (isHub) return;
  renderCli(els.cliGrid);
  els.autoSw.input.checked = state.settings.autoUpdateCli !== false;
  syncPolicyButton();
}

/** 每分鐘：倒數與相對時間（矩陣的到期提示也跟著時間變） */
export function tickMinute(now) {
  if (!els || state.view !== 'dashboard') return;
  for (const t of els.container.querySelectorAll('[data-reset-at]')) t.textContent = fmtReset(Number(t.dataset.resetAt), now);
  for (const col of els.container.querySelectorAll('.ring-col.locked')) setRingCountdown(col, now);
  for (const col of els.container.querySelectorAll('.ring-col')) if (col._els) setRingBurn(col, now);
  for (const t of els.container.querySelectorAll('[data-rel-at]')) t.textContent = (t.dataset.relPrefix || '') + fmtRelative(Number(t.dataset.relAt), now);
  for (const t of els.container.querySelectorAll('[data-code-exp]')) t.textContent = codeExpiryText(Number(t.dataset.codeExp), now);
  renderSummary(now);
  renderRemoteSections(now);
  // 正在記的各段的 token 一直在變：每分鐘要一次（結了新的一筆宿主會主動送）
  requestUsageSegments();
}

// ───── 摘要：一行句子 ─────
// 各項的數字用側欄同款的計數器（tokenfx.js），整段重建時沿用同一個：數字變了先閃過亂碼再鎖定（第一次直接顯示）
const sumCounters = new Map();
function sumCounter(key) {
  let c = sumCounters.get(key);
  if (!c) { c = tokenCounter(String); sumCounters.set(key, c); }
  return c;
}

function renderSummary(now = Date.now()) {
  // remoteOnly（帳戶目錄裡有、這台沒有）不算這台的帳戶：帳戶數、接近上限、下一次重置都只看本機帳戶。
  // 對話數（執行中、等背景工作回報、等你回應）是各帳戶卡的加總：〔同步〕含其他在線電腦的（同側欄收起時的合計），remoteOnly 卡上的也算
  const accts = state.accounts.filter((a) => a.remoteOnly !== true);
  const remoteOnly = state.accounts.length - countLocalAccounts(state.accounts);
  let running = 0, waiting = 0, background = 0, near = 0;
  let otherRunning = 0, otherWaiting = 0, otherBackground = 0;
  let next = null;
  for (const a of state.accounts) {
    const s = sessionTotals(a);
    running += s.running;
    waiting += s.waiting;
    background += s.background;
    otherRunning += s.otherRunning;
    otherWaiting += s.otherWaiting;
    otherBackground += s.otherBackground;
  }
  for (const a of accts) {
    const wins = (a.usage && a.usage.windows) || [];
    if (wins.some((w) => w.severity === 'high' || w.severity === 'max')) near++;
    for (const w of wins) if (w.resetsAt && w.resetsAt > now && (!next || w.resetsAt < next.at)) next = { at: w.resetsAt, a, w };
  }
  const s = els.summary;
  s.textContent = '';
  const values = [];
  const stat = (key, n, unit, tone, title) => {
    const c = sumCounter(key);
    values.push([c, n]);
    return h('span', { class: 'sum-item' + (tone && n ? ' ' + tone : ''), title: title || null }, h('b', { class: 'sum-n' }, c.num), h('span', { text: unit }));
  };
  const total = stat('total', countLocalAccounts(accts), '個帳戶');
  // 不逐張判斷有沒有其他電腦登入（從這台移除的帳戶也會以 remoteOnly 回來），寫成中性的說法
  if (remoteOnly > 0) total.append(h('span', { class: 'sum-dim sum-remote', text: '（另有 ' + remoteOnly + ' 個在帳戶目錄、這台未登入）' }));
  // 摘要底下刻度的那道光：執行中的對話越多跑得越快（同帳戶卡的 accountPulse，換速時保住進度）
  setTempo(s, accountPulse('ready', false, running).tempo);
  s.append(total, stat('run', running, '個對話執行中', 'run', sessionTitle(running, otherRunning, '執行中')));
  // 等背景工作回報（代理已回覆完畢、背景工作還在跑）：有才列，平常不多佔一格
  if (background) s.append(stat('bg', background, '個等背景工作回報', 'bg', sessionTitle(background, otherBackground, '等背景工作回報')));
  s.append(stat('wait', waiting, '個等你回應', 'wait', sessionTitle(waiting, otherWaiting, '等待你回應')), stat('near', near, '個帳戶接近上限', 'near'));
  // 〔熱更新〕有對話在等著換新版時才出現（restartPending＝已排定以新版重開、還沒完成的對話數）
  const pending = providerKeys().reduce((n, p) => n + (state.cli[p] && Number(state.cli[p].restartPending) > 0 ? Number(state.cli[p].restartPending) : 0), 0);
  if (pending) s.append(stat('hot', pending, '個對話換版中', 'hot'));
  if (next) {
    s.append(h('span', { class: 'sum-item sum-next', title: fmtDateTime(next.at) },
      h('span', { text: '下一次重置在' }),
      h('span', { class: 'sum-join' }, h('b', { class: 'sum-n', text: fmtSpan(next.at - now) }), h('span', { text: '後' })),
      h('span', { class: 'sum-dim', text: next.a.label + '・' + (next.w.label || '') })));
  }
  for (const [c, n] of values) c.set(n);
}

// ───── 帳戶大卡 ─────
function createCard(a) {
  const card = h('article', { class: 'acard' });
  const mark = h('span', { class: 'acard-mark' });
  const name = h('h3', { class: 'acard-name' });
  const sub = h('div', { class: 'acard-sub' });
  const pill = h('span', { class: 'pill' });
  const more = iconButton('more', '帳戶動作', () => accountMenu(accountOf(card.dataset.key), more), 'acard-more');
  // 〔同步〕各電腦的登入狀態小點（devices 是陣列的帳戶才有）
  const strip = h('div', { class: 'acard-strip', hidden: true });
  // 帳戶的設定資料夾（profile；CLAUDE_CONFIG_DIR、CODEX_HOME、GROK_HOME…）：只有桌面這台的帳戶，點了用檔案總管開（accountOpenFolder）
  const path = h('button', { type: 'button', class: 'acard-path', hidden: true }, icon('folderOpen', 'acard-path-ic'), h('span', { class: 'acard-path-text mono' }));
  path.addEventListener('click', () => post({ cmd: 'accountOpenFolder', accountId: card.dataset.key }));
  const head = h('div', { class: 'acard-head' }, mark, h('div', { class: 'acard-id' }, h('div', { class: 'acard-line' }, name, pill), sub, path, strip), more);
  const rings = h('div', { class: 'rings' });
  const notes = h('div', { class: 'acard-notes' });
  const login = h('div', { class: 'acard-login', hidden: true });
  // 健康度（只有桌面；點了開明細）
  const health = createHealthStrip(card);
  // MCP 伺服器（只有桌面這台的 Claude 帳戶；點了開明細、授權、重新連線，mcp.js）
  const mcp = createMcpStrip(card);
  const sessions = h('div', { class: 'acard-sess' });
  // 瀏覽器版不建新對話、更新用量、登入（伺服器不接受，web-protocol 第六節第 9 點）
  let newBtn = null, refBtn = null, loginBtn = null, hereBtn = null;
  if (!isHub) {
    // remoteOnly 卡唯一的按鈕（其他按鈕藏起來）
    hereBtn = h('button', { type: 'button', class: 'btn sm primary acard-here', hidden: true }, icon('login'), h('span', { text: '在這台登入' }));
    hereBtn.addEventListener('click', () => loginHere(accountOf(card.dataset.key)));
    newBtn = h('button', { type: 'button', class: 'btn sm acc' }, icon('plus'), h('span', { text: '新對話' }));
    newBtn.addEventListener('click', () => ws.newConversation(card.dataset.key));
    refBtn = h('button', { type: 'button', class: 'btn sm acard-ref', title: '更新用量：立即重新檢查登入狀態與用量', 'aria-label': '更新用量' }, icon('refresh'), h('span', { text: '更新用量' }));
    refBtn.addEventListener('click', () => { post({ cmd: 'accountRefresh', accountId: card.dataset.key }); refBtn.classList.add('spinning'); setTimeout(() => refBtn.classList.remove('spinning'), 1200); });
    loginBtn = h('button', { type: 'button', class: 'btn sm' });
    // 登入中再按＝取消這次登入（瀏覽器分頁被關掉、想換帳號時不必等 10 分鐘逾時）
    loginBtn.addEventListener('click', () => {
      const acc = accountOf(card.dataset.key);
      post({ cmd: acc && acc.loginBusy ? 'accountLoginCancel' : 'accountLogin', accountId: card.dataset.key });
    });
  }
  const foot = h('div', { class: 'acard-foot' }, sessions, h('span', { class: 'grow' }), loginBtn, refBtn, newBtn, hereBtn);
  // HUD 的背景層（帳戶色的光暈、網格、側邊刻度、開場的掃描線；fx.css）：畫在內容底下，不接收滑鼠
  card.append(h('div', { class: 'acard-fx', 'aria-hidden': 'true' }), head, rings, notes, health, mcp, login, foot);
  card._els = { mark, name, sub, path, pill, strip, rings, notes, health, mcp, login, sessions, newBtn, refBtn, loginBtn, hereBtn, provider: null };
  updateCard(card, a, true);
  return card;
}

/** 裝置碼的有效時間：「12 分內有效」；過了「驗證碼已過期」 */
function codeExpiryText(expiresAt, now = Date.now()) {
  if (!expiresAt) return '';
  const left = expiresAt - now;
  return left > 0 ? '驗證碼 ' + fmtSpan(left) + '內有效' : '驗證碼已過期';
}

/** 登入提示裡的裝置碼（桌面的 Codex 裝置碼登入）：大字驗證碼、「複製」、網址、有效時間 */
function loginCodeBlock(code) {
  const copy = h('button', { type: 'button', class: 'btn sm login-code-copy' }, icon('copy'), h('span', { text: '複製' }));
  copy.addEventListener('click', async () => {
    // navigator.clipboard 不能用時交給宿主（copyText）；宿主複製完會自己提示
    if (await copyToClipboard(code.userCode, (t) => post({ cmd: 'copyText', text: t }))) toast('已複製驗證碼', 'success');
  });
  const url = code.link
    ? h('a', { class: 'login-code-url', href: code.link, text: code.verificationUrl, title: '用瀏覽器開啟（也可以用手機開這個網址）' })
    : code.verificationUrl ? h('span', { class: 'login-code-url', text: code.verificationUrl }) : null;
  return h('div', { class: 'login-code' },
    h('span', { class: 'login-code-label', text: '驗證碼' }),
    h('span', { class: 'login-code-val mono', text: code.userCode }),
    copy,
    url ? h('span', { class: 'login-code-at' }, h('span', { text: '到 ' }), url, h('span', { text: ' 輸入' })) : null,
    code.expiresAt ? h('span', { class: 'login-code-exp', dataset: { codeExp: String(code.expiresAt) }, text: codeExpiryText(code.expiresAt) }) : null);
}

/** Claude 的遠端登入（瀏覽器版）：打開登入網址、登入後把頁面上的授權碼貼回來（accountLoginCode 送到那台） */
function claudeLoginBlock(a, view, deviceId) {
  const input = h('input', { type: 'text', class: 'login-code-input mono', placeholder: '登入後把授權碼貼在這裡', autocomplete: 'off', spellcheck: 'false', 'aria-label': 'Claude 的授權碼' });
  const send = h('button', { type: 'button', class: 'btn sm primary login-code-send', text: '送出授權碼' });
  const submit = () => {
    const code = loginCodeInput(input.value);
    if (!code) { toast('請把登入頁上顯示的整串授權碼貼過來', 'error'); input.focus(); return; }
    send.disabled = true;
    post({ cmd: 'accountLoginCode', logicalAccountId: a.id, deviceId, code });
    toast('已送出授權碼，等那台完成登入', 'info');
  };
  send.addEventListener('click', submit);
  input.addEventListener('keydown', (ev) => { if (ev.key === 'Enter') { ev.preventDefault(); submit(); } });
  return h('div', { class: 'login-code login-claude' },
    h('a', { class: 'btn sm login-url', href: view.link, target: '_blank', rel: 'noopener noreferrer', title: '在這台裝置（例如手機）打開 Claude 的登入頁' }, icon('login'), h('span', { text: '打開 Claude 登入頁' })),
    input, send);
}

function updateCard(card, a, first) {
  const e = card._els;
  const color = setAcc(card, a.color, a.provider);
  // 瀏覽器版的帳戶都是邏輯帳戶，沒有「這台」：remoteOnly 只在桌面有意義
  const remoteOnly = !isHub && a.remoteOnly === true;
  card.classList.toggle('remote-only', remoteOnly);
  if (e.provider !== a.provider) { e.mark.textContent = ''; e.mark.append(providerMark(a.provider)); e.provider = a.provider; }
  e.name.textContent = a.label;
  e.sub.textContent = '';
  const subParts = [providerInfo(a.provider).name, a.email, a.plan].filter(Boolean);
  subParts.forEach((t, i) => { if (i) e.sub.append(h('span', { class: 'sep', text: '・' })); e.sub.append(h('span', { text: t })); });
  if (a.isSystemDefault) e.sub.append(h('span', { class: 'tag', text: '本機預設', title: a.configDirectory || '' }));
  if (a.safeMode && providerInfo(a.provider).safeMode) e.sub.append(h('span', { class: 'tag', text: '精簡模式', title: '不載入外掛、hooks、MCP、' + instructionFile(a.provider) }));
  // 設定資料夾的路徑（瀏覽器版的邏輯帳戶、remoteOnly 沒有；資料夾不存在時宿主會先建立）
  const dir = !isHub && !remoteOnly && typeof a.configDirectory === 'string' ? a.configDirectory : '';
  setHidden(e.path, !dir);
  if (dir && e.path._dir !== dir) {
    e.path._dir = dir;
    e.path.lastChild.textContent = compactPath(dir);
    e.path.title = '設定資料夾（點一下用檔案總管開啟）\n' + dir;
    e.path.setAttribute('aria-label', '用檔案總管開啟設定資料夾 ' + dir);
  }
  e.pill.className = 'pill st-' + (a.status || 'checking');
  e.pill.textContent = '';
  if (a.status === 'checking') e.pill.append(spinner('pill-spin'));
  e.pill.append(h('span', { text: a.statusText || STATUS_LABEL[a.status] || a.status || '' }));

  // 〔同步〕裝置小點：內容沒變不重畫
  const stripSig = Array.isArray(a.devices) ? deviceStripSig(a) : '';
  if (e.strip._sig !== stripSig) {
    e.strip._sig = stripSig;
    e.strip.textContent = '';
    const strip = stripSig ? deviceStrip(a) : null;
    if (strip) e.strip.append(strip);
    e.strip.hidden = !strip;
  }

  // 圓環
  const wins = (a.usage && a.usage.windows) || [];
  const keys = wins.map((w) => w.key || w.label).join('|');
  if (e.rings._keys !== keys) {
    e.rings._keys = keys;
    e.rings.textContent = '';
    e.rings._cols = new Map();
    for (const w of wins) { const col = createRingCol(w, color, first); e.rings._cols.set(w.key || w.label, col); e.rings.append(col); }
  }
  for (const w of wins) { const col = e.rings._cols.get(w.key || w.label); if (col) updateRingCol(col, w, color); }
  e.rings.dataset.count = String(wins.length);
  e.rings.hidden = !wins.length;

  // 說明
  e.notes.textContent = '';
  const u = a.usage;
  // 已登入、沒有時間窗但有附註說明（例如純計次或無時間窗上限的方案）：只顯示說明，不說「還沒有用量資料」
  if (!wins.length && !(a.status === 'ready' && u && u.note)) {
    let t = '還沒有用量資料';
    // 瀏覽器版：狀態是各電腦的彙總（「沒有電腦登入」「2 台已登入（1 台在線）」），這台的說明（安裝 CLI、登入）不適用
    if (isHub) t = a.statusText || STATUS_LABEL[a.status] || t;
    // remoteOnly：只有在沒有其他電腦共享的用量時才寫這句（有用量就照一般圓環顯示，來源標「其他電腦回報」）。
    // 沒有任何其他電腦有這個帳戶（例如只在這台用過、從這台移除之後留在目錄裡）時不說「在其他電腦登入過」
    else if (remoteOnly && !claimableHere(a)) t = AGY_ONE_LOGIN;
    else if (remoteOnly) t = remoteOnlyElsewhere(a)
      ? '這台未登入。這個帳戶在其他電腦登入過，按「在這台登入」就能在這台使用。'
      : '這台未登入。這個帳戶還在帳戶目錄裡，目前沒有電腦登入；按「在這台登入」就能在這台使用，要從目錄拿掉請到網頁版的帳戶選單「從目錄刪除」。';
    else if (a.status === 'notLoggedIn') t = '尚未登入。登入後就能看到這個帳戶的用量。';
    else if (a.status === 'apiKey') t = '這個帳戶以 API Key 登入，按用量計費，沒有訂閱額度可以顯示。';
    else if (a.status === 'notInstalled') t = '這台電腦還沒有安裝 ' + providerInfo(a.provider).name + ' CLI，可以在下方 CLI 區安裝。';
    else if (a.status === 'checking') t = '正在檢查登入狀態與用量…';
    else if (a.status === 'error') t = a.statusText || '檢查時發生錯誤';
    e.notes.append(h('div', { class: 'note-empty' }, icon(a.status === 'notLoggedIn' ? 'login' : 'gauge'), h('span', { text: t })));
  }
  // 從沒取得過用量（fetchedAt 為 null）就沒有過時的資料可標示：失敗原因由下面的錯誤說明
  if (u && u.stale && u.fetchedAt) {
    e.notes.append(h('div', { class: 'note warn' }, icon('clock'),
      h('span', { text: '資料可能過時，' }), h('span', { dataset: { relAt: String(u.fetchedAt), relPrefix: '取得於 ' }, text: '取得於 ' + fmtRelative(u.fetchedAt) })));
  } else if (u && u.fetchedAt) {
    e.notes.append(h('div', { class: 'note dim' }, h('span', { dataset: { relAt: String(u.fetchedAt), relPrefix: '更新於 ' }, text: '更新於 ' + fmtRelative(u.fetchedAt) }),
      u.source ? h('span', { class: 'note-src', text: { api: '官方用量端點', session: '對話回報', cache: '快取', shared: '其他電腦回報' }[u.source] || u.source }) : null));
  }
  if (u && u.error) e.notes.append(h('div', { class: 'note err' }, icon('warning'), h('span', { text: u.error })));
  if (u && u.credits) e.notes.append(h('div', { class: 'note' }, icon('sparkle'), h('span', { text: '額外點數 ' }), h('b', { text: fmtCredits(u.credits) })));
  // Codex 的免費重置次數（rateLimitResetCredits）：沒有可用的也列（灰字），有的話標最早到期的時間
  const rc = resetCreditsInfo(u, Date.now());
  if (rc) {
    // 第十一批：剩幾次畫成一排小方塊燈、最早到期的那次一格一天的倒數（fxrules.creditPips、creditExpiryCells）
    const pips = creditPips(rc.count);
    const exp = rc.expiresAt ? creditExpiryCells(rc.expiresAt, Date.now()) : null;
    const row = h('div', { class: 'note credit-note' + (rc.count ? '' : ' dim'), title: '可以把用完的額度提前重置的次數（Codex 回報）' }, icon('refresh'),
      h('span', { text: '免費重置 ' }), h('b', { text: rc.count + ' 次' }),
      pips.shown ? h('span', { class: 'credit-pips', 'aria-hidden': 'true' }, ...Array.from({ length: pips.shown }, () => h('i')),
        pips.more ? h('span', { class: 'credit-more', text: '+' + pips.more }) : null) : null,
      rc.expiresAt ? h('span', { text: '・最早 ' + fmtDateTime(rc.expiresAt) + ' 到期' }) : null,
      exp ? h('span', { class: 'credit-exp' + (exp.soon ? ' soon' : ''), title: '最早到期的那次還剩 ' + exp.days + ' 天', 'aria-hidden': 'true' },
        ...Array.from({ length: CREDIT_EXPIRY_CELLS }, (_, i) => h('i', { class: i < exp.lit ? 'on' : null }))) : null);
    // 用掉了一次（次數變少）：這張卡的圓環外面一道光掃一圈（fx.css 的 .credit-sweep，一次性；圓環自己的偽元素是執行中的動畫在用）
    if (creditUsed(e.prevCredits, rc.count)) {
      for (const w of card.querySelectorAll('.ring-wrap')) {
        const sweep = h('span', { class: 'credit-sweep', 'aria-hidden': 'true' });
        w.append(sweep);
        setTimeout(() => sweep.remove(), 1000);
      }
    }
    e.prevCredits = rc.count;
    // 「使用重置」：說明列每次重畫，按鈕是同一個元素（按到一半帳戶清單更新也不會掉）
    const act = resetCreditAction(a, rc, isHub);
    if (act) {
      if (!e.resetBtn) {
        e.resetBtn = h('button', { type: 'button', class: 'btn sm ghost reset-credit-btn' });
        e.resetBtn.addEventListener('click', () => confirmResetCredit(e.resetBtn._acc));
      }
      e.resetBtn._acc = a;
      e.resetBtn.disabled = act.busy;
      setButtonContent(e.resetBtn, act.busy ? 'clock' : 'refresh', act.busy ? '重置中…' : '使用重置');
      row.append(e.resetBtn);
    }
    e.notes.append(row);
  }
  if (u && u.note) e.notes.append(h('div', { class: 'note dim' }, icon('info'), h('span', { text: u.note })));
  // 登入到期（Claude，loginrules.js）：平常是一行灰字，剩 3 天內琥珀色、已到期紅色，下面的「重新登入」改成主要按鈕
  const lx = isHub ? null : loginExpiry(a, Date.now());
  if (lx) {
    const tone = lx.level === 'expired' ? ' err' : lx.level === 'soon' ? ' warn' : ' dim';
    e.notes.append(h('div', { class: 'note login-exp' + tone }, icon(lx.level === 'ok' ? 'login' : 'warning'), h('span', { text: loginExpiryText(lx, fmtDateTime(lx.at)) })));
  }
  e.notes.hidden = !e.notes.childElementCount;
  updateHealthStrip(e.health, a);
  updateMcpStrip(e.mcp, a);

  // 登入提示（Codex 裝置碼登入時另有大字驗證碼與「複製」）：內容沒變不重畫，按下中的「複製」才不會被移出 DOM
  const lm = state.login.get(a.id);
  const done = !!(lm && lm.done);
  const code = lm && !done ? loginCodeView(lm.deviceCode, Date.now()) : null;
  // Claude 的遠端登入：登入網址與貼回授權碼（瀏覽器版、知道是在哪台登入的才有）
  const claudeUrl = lm && !done && isHub ? claudeLoginUrlView(lm.loginUrl) : null;
  const claudeDevice = claudeUrl ? loginDeviceOf(a) : null;
  // 〔遠端〕瀏覽器版：這個分頁正在哪台登入這個帳戶（「在 OFFICE-PC」，寫在驗證碼上方）
  const origin = lm && !done ? originLabelOf(a) : null;
  const loginSig = lm || a.loginBusy ? JSON.stringify([done, lm ? lm.success : null, (lm && lm.message) || '', code && [code.userCode, code.verificationUrl, code.expiresAt], origin, claudeUrl && claudeUrl.link]) : '';
  if (e.login._sig !== loginSig) {
    e.login._sig = loginSig;
    e.login.textContent = '';
    if (loginSig) {
      e.login.className = 'acard-login' + (done ? (lm.success === false ? ' fail' : ' ok') : '') + (code || origin ? ' has-code' : '');
      e.login.append(done ? icon(lm.success === false ? 'warning' : 'check') : spinner(), h('span', { class: 'acard-login-msg', text: (lm && lm.message) || '登入中…' }));
      if (origin) e.login.append(h('span', { class: 'login-origin', text: origin }));
      if (code) e.login.append(loginCodeBlock(code));
      if (claudeUrl && claudeDevice) e.login.append(claudeLoginBlock(a, claudeUrl, claudeDevice));
    }
    e.login.hidden = !loginSig;
  }

  // 對話數：〔同步〕加上其他在線電腦的（otherSessions，同側欄帳戶卡）——其他電腦在跑時這張卡照樣是執行中（.sess.run 帶動卡片的執行中動畫），
  // 滑過的說明寫其中幾個在其他電腦
  const s = sessionTotals(a);
  // 持續動畫：已登入沒在跑是怠速，在跑時執行中的對話越多越快（fx.css 的 data-pulse）
  setPulse(card, accountPulse(a.status, remoteOnly, s.running), pulsePhase(a.id));
  e.sessions.textContent = '';
  e.sessions.append(sess('開著', s.open, null, sessionTitle(s.open, s.otherOpen, '開著')),
    sess('執行中', s.running, 'run', sessionTitle(s.running, s.otherRunning, '執行中')));
  // 執行中的子代理：有才列（個數，不是對話數）
  if (s.subagents) e.sessions.append(sess('子代理', s.subagents, 'sub', subagentsTooltip(a)));
  // 等背景工作回報：有才列（卡片底部的空間有限）
  if (s.background) e.sessions.append(sess('等背景', s.background, 'bg', sessionTitle(s.background, s.otherBackground, '等背景工作回報')));
  e.sessions.append(sess('等待你', s.waiting, 'wait', sessionTitle(s.waiting, s.otherWaiting, '等待你回應')));

  // 按鈕（瀏覽器版沒有）；remoteOnly 只有「在這台登入」
  if (e.hereBtn) {
    e.hereBtn.hidden = !remoteOnly || !claimableHere(a);
    e.loginBtn.hidden = remoteOnly;
    e.refBtn.hidden = remoteOnly;
    e.newBtn.hidden = remoteOnly;
  }
  if (e.loginBtn) {
    const needLogin = a.status === 'notLoggedIn' || a.status === 'apiKey' || a.status === 'error';
    const renew = !!lx && lx.level !== 'ok';
    e.loginBtn.disabled = !a.loginBusy && a.status === 'notInstalled';
    e.loginBtn.className = 'btn sm' + ((needLogin || renew) && !a.loginBusy ? ' primary' : ' ghost');
    const loginText = a.loginBusy ? '取消登入' : needLogin ? '登入' : '重新登入';
    setButtonContent(e.loginBtn, a.loginBusy ? 'x' : 'login', loginText);
    // 窄的帳戶卡（一排三張）只顯示圖示：名稱與提示照舊
    e.loginBtn.setAttribute('aria-label', loginText);
    e.loginBtn.title = loginText;
    e.newBtn.disabled = a.status === 'notInstalled';
  }
  card.classList.toggle('inactive', a.status !== 'ready' && a.status !== 'checking');
}

/** 「使用重置」：會真的用掉一次，先確認；結果由宿主的 toast 說明 */
async function confirmResetCredit(a) {
  const rc = a && resetCreditsInfo(a.usage, Date.now());
  if (!rc || rc.count <= 0) return;
  const r = await confirmDialog({
    title: '使用免費重置',
    message: '用掉「' + (a.label || 'Codex') + '」的 1 次免費重置，把用完的額度提前重置？',
    detail: '目前有 ' + rc.count + ' 次' + (rc.expiresAt ? '，最早一次 ' + fmtDateTime(rc.expiresAt) + ' 到期' : '') + '。用掉之後不能復原。',
    confirmLabel: '使用重置',
  });
  if (r.ok) post({ cmd: 'accountResetCredit', accountId: a.id });
}

function sess(label, n, tone, title) {
  return h('span', { class: 'sess' + (tone && n ? ' ' + tone : ''), title: title || null }, h('span', { text: label }), h('b', { text: String(n) }));
}

function fmtCredits(c) {
  const n = Number(c);
  return isFinite(n) ? n.toLocaleString('zh-TW', { maximumFractionDigits: 2 }) : String(c);
}

// ───── 圓環＋走勢線 ─────
function svgEl(tag, attrs) {
  const e = document.createElementNS(NS, tag);
  for (const k in attrs) e.setAttribute(k, attrs[k]);
  return e;
}

function createRingCol(w, color, animateIn) {
  const svg = svgEl('svg', { viewBox: '0 0 120 120', class: 'ring', 'aria-hidden': 'true' });
  // 圈內的刻度：50 格細刻度＋每 25% 一格粗刻度（圓的起點在 3 點鐘方向，粗刻度剛好落在 12、3、6、9 點）
  const scale = svgEl('circle', { cx: 60, cy: 60, r: RING_SCALE_R, class: 'ring-scale', pathLength: 100, 'stroke-dasharray': '0.5 1.5' });
  const major = svgEl('circle', { cx: 60, cy: 60, r: RING_SCALE_R, class: 'ring-scale-major', pathLength: 100, 'stroke-dasharray': '0.7 24.3', 'stroke-dashoffset': '0.35' });
  const track = svgEl('circle', { cx: 60, cy: 60, r: RING_R, class: 'ring-track' });
  // 燃燒預估（setRingBurn）：照目前速度會在重置前用完時，剩下的部分是琥珀色虛線（整圈虛線墊在弧線底下，已用的部分被弧線蓋住；靜態）
  const burn = svgEl('circle', { cx: 60, cy: 60, r: RING_R, class: 'ring-burn', pathLength: 100, 'stroke-dasharray': '1.4 1.6', transform: 'rotate(-90 60 60)', visibility: 'hidden' });
  const arc = svgEl('circle', { cx: 60, cy: 60, r: RING_R, class: 'ring-arc', pathLength: 100, 'stroke-dasharray': '100 100', 'stroke-dashoffset': '100', transform: 'rotate(-90 60 60)' });
  // 弧線末端的光點：繞圓心轉到目前的百分比，和弧線同一個轉場一起長
  const tip = svgEl('g', { class: 'ring-tip' });
  tip.append(svgEl('circle', { cx: 60, cy: 60 - RING_R, r: 6.5, class: 'ring-tip-halo' }), svgEl('circle', { cx: 60, cy: 60 - RING_R, r: 2.4, class: 'ring-tip-core' }));
  svg.append(scale, major, track, burn, arc, tip);
  const num = h('span', { class: 'ring-num' });
  const center = h('div', { class: 'ring-center' }, h('span', { class: 'ring-val' }, num, h('span', { class: 'ring-pct', text: '%' })));
  const label = h('div', { class: 'ring-label' });
  const reset = h('div', { class: 'ring-reset' });
  // 額度用完（鎖住）時的重置倒數條（fxrules.resetCountdown）：還剩多少時間到重置，20 格、每分鐘更新；只在鎖住時顯示
  const cd = h('div', { class: 'ring-cd', hidden: true }, h('i', { 'aria-hidden': 'true' }));
  const burnNote = h('div', { class: 'ring-burn-note', hidden: true });
  const spark = h('div', { class: 'spark' });
  // 監聽器只綁這一次，處理器讀 renderSpark 放在 spark._spark 的目前資料（每次重畫都再綁一組會無上限累積）
  spark.addEventListener('pointermove', (ev) => sparkMove(spark, ev));
  spark.addEventListener('pointerleave', () => sparkLeave(spark));
  // 雷達掃描（只在有對話執行中的帳戶卡轉，fx.css）：疊在圓環上、百分比底下
  const sweep = h('span', { class: 'ring-sweep', 'aria-hidden': 'true' });
  const col = h('div', { class: 'ring-col' }, h('div', { class: 'ring-wrap' }, svg, sweep, center), h('div', { class: 'ring-meta' }, label, reset, burnNote, cd, spark));
  col._els = { arc, tip, num, label, reset, spark, svg, cd, burn, burnNote };
  col._first = animateIn;
  return col;
}

/** 鎖住的時間窗的重置倒數條：亮的格數＝還剩的時間（20 格，fxrules.countdownCells）；沒鎖、沒有重置時間、認不得時間窗長度就藏起來 */
function setRingCountdown(col, now) {
  const cd = col._els.cd;
  const f = col._locked && col._w ? resetCountdown(col._w, now) : null;
  cd.hidden = f === null;
  if (f === null) return;
  const cells = countdownCells(f, CD_CELLS);
  cd.style.setProperty('--cd-on', String(cells));
  cd.firstChild.style.transform = 'scaleX(' + (cells / CD_CELLS).toFixed(4) + ')';
  cd.title = '額度已用完，' + fmtReset(col._w.resetsAt, now);
}

/**
 * 用量燃燒預估（fxrules.burnForecast）：照這個週期到目前的平均速度、會比重置早用完時，圓環下寫「1 小時 20 分後用完」（琥珀色，前面一顆燈），
 * 圓環剩下的部分換成虛線；來得及重置、資料太少、已經用完時都不顯示。資料更新與每分鐘重算一次
 */
function setRingBurn(col, now) {
  const e = col._els;
  const f = col._w && !col._locked ? burnForecast(col._w, now) : null;
  e.burnNote.hidden = !f;
  e.burn.setAttribute('visibility', f ? 'visible' : 'hidden');
  col.classList.toggle('burning', !!f);
  if (!f) return;
  // 和上一行的重置時間一樣短（三、四個圓環並排時才放得下）；「照目前速度」寫在滑過的說明
  const text = fmtSpan(f.inMs) + '後用完';
  if (e.burnNote.textContent !== text) e.burnNote.textContent = text;
  e.burnNote.title = '照目前速度（這個週期到目前的平均用量）約 ' + fmtSpan(f.inMs) + '後用完，比重置早 ' + fmtSpan(f.earlyMs) + '（約 ' + fmtDateTime(f.at) + '）';
}

function updateRingCol(col, w, color) {
  const e = col._els;
  const p = Math.max(0, Math.min(100, Number(w.percent) || 0));
  const sev = ['ok', 'warn', 'high', 'max'].includes(w.severity) ? w.severity : 'ok';
  col.dataset.sev = sev;
  e.arc.style.opacity = p > 0 ? '1' : '0';
  e.tip.style.opacity = p > 0 ? '1' : '0';
  const target = String(100 - p);
  col._target = target;
  col._tipTarget = 'rotate(' + (p * 3.6).toFixed(2) + 'deg)';
  if (col._first) {
    col._first = false;
    // 第一次出現：從 0 長到目前數值。兩個影格後寫入的是當下最新的目標值：
    // 這段期間（視窗隱藏時 rAF 暫停，會一直延長）到達的更新不會被第一次的初值蓋回去
    requestAnimationFrame(() => requestAnimationFrame(() => { e.arc.style.strokeDashoffset = col._target; e.tip.style.transform = col._tipTarget; }));
  } else { e.arc.style.strokeDashoffset = target; e.tip.style.transform = col._tipTarget; }
  e.num.textContent = pctText(p);
  e.label.textContent = w.label || w.key || '';
  e.label.title = w.key || '';
  if (w.resetsAt) { e.reset.dataset.resetAt = String(w.resetsAt); e.reset.textContent = fmtReset(w.resetsAt); e.reset.title = '重置時間：' + fmtDateTime(w.resetsAt); }
  else { delete e.reset.dataset.resetAt; e.reset.textContent = '沒有重置時間'; e.reset.title = ''; }
  // 用完了（鎖住）：圓環加上鎖定的樣貌，從沒鎖變成鎖住的那一下鎖上一次（第一次畫不播）；倒數條每分鐘跟著時間縮
  const locked = quotaLocked(w);
  if (locked && col._locked === false) replayClass(col, 'fx-q-lock', Q_LOCK_FX_MS);
  col._locked = locked;
  col.classList.toggle('locked', locked);
  col._w = w;
  setRingCountdown(col, Date.now());
  setRingBurn(col, Date.now());
  const hist = Array.isArray(w.history) ? w.history : [];
  // 橫軸是「到現在為止的 7 天」：資料沒變也每小時重畫一次，讓線跟著時間往左移
  const hour = Math.floor(Date.now() / 3600e3);
  const sig = hist.length + ':' + (hist.length ? hist[0][0] + ',' + hist[hist.length - 1][0] + ',' + hist[hist.length - 1][1] : '') + ':' + sev + ':' + hour;
  if (e.spark._sig !== sig) { e.spark._sig = sig; renderSpark(e.spark, hist, w); }
  col.title = (w.label || '') + '：' + pctText(p) + '%';
}

// 走勢線的橫軸固定是最近 7 天：右端＝現在，沒有資料的前段留白，最後一點太久以前也看得出來
const SPARK_W = 140, SPARK_H = 34, SPARK_PAD = 3;
const SPARK_SPAN = 7 * 86400e3;
/** 宿主每小時給一點：前後兩點相隔超過 4 小時＝這段時間沒有資料（app 沒開、查詢失敗），線在那裡斷開，不畫成斜線 */
const SPARK_GAP = 4 * 3600e3;
/** 168 點畫在 140px 寬會變成雜訊：每 4 小時取最大值（保留尖峰），約每 3px 一點 */
const SPARK_BUCKET = 4 * 3600e3;

/** 最近 7 天的點，在缺漏處分成幾段 */
function sparkSegments(hist, from) {
  const segs = [];
  let prev = null;
  for (const p of hist) {
    if (p[0] < from) continue;
    if (!prev || p[0] - prev[0] > SPARK_GAP) segs.push([]);
    segs[segs.length - 1].push(p);
    prev = p;
  }
  return segs;
}

/** 一段之內每 4 小時取最大值；段落的最後一點一定保留（最後一段的右端＝目前數值） */
function reduceSegment(seg) {
  const out = [];
  let bucket = null;
  for (const p of seg) {
    const b = Math.floor(p[0] / SPARK_BUCKET);
    if (b !== bucket) { out.push(p); bucket = b; }
    else if (p[1] > out[out.length - 1][1]) out[out.length - 1] = p;
  }
  const last = seg[seg.length - 1];
  if (out[out.length - 1] !== last) out.push(last);
  return out;
}

let gradSeq = 0;
function renderSpark(box, rawHist, w) {
  // 滑鼠正停在這條線上：提示框指著舊資料，先收起來（下一次 pointermove 依新資料再顯示）
  if (box._spark && box._spark.hover) hideTip();
  box._spark = null;
  box.textContent = '';
  const W = SPARK_W, H = SPARK_H, pad = SPARK_PAD;
  const from = Date.now() - SPARK_SPAN;
  const raw = sparkSegments(rawHist, from);
  if (raw.reduce((n, s) => n + s.length, 0) < 2) {
    box.removeAttribute('aria-label');
    box.append(h('div', { class: 'spark-empty', text: '還沒有走勢資料' }));
    return;
  }
  const segs = raw.map(reduceSegment);
  const hist = segs.flat();
  const toPt = ([t, v]) => [Math.max(0, Math.min(W, ((t - from) / SPARK_SPAN) * W)), H - pad - (Math.max(0, Math.min(100, v)) / 100) * (H - pad * 2)];
  const xy = (p) => p[0].toFixed(1) + ' ' + p[1].toFixed(1);
  let d = '', fill = '';
  for (const seg of segs) {
    const sp = seg.map(toPt);
    const path = sp.map((p, i) => (i ? 'L' : 'M') + xy(p)).join('');
    d += sp.length > 1 ? path : path + 'h0'; // 只有一點的段落：零長度線段配圓頭，畫成一個點
    fill += path + 'L' + sp[sp.length - 1][0].toFixed(1) + ' ' + H + 'L' + sp[0][0].toFixed(1) + ' ' + H + 'Z';
  }
  const pts = hist.map(toPt);
  const id = 'sg' + (++gradSeq);
  const svg = svgEl('svg', { viewBox: '0 0 ' + W + ' ' + H, preserveAspectRatio: 'none', class: 'spark-svg' });
  const defs = svgEl('defs', {});
  const lg = svgEl('linearGradient', { id, x1: 0, y1: 0, x2: 0, y2: 1 });
  lg.append(svgEl('stop', { offset: '0%', class: 'spark-stop-a' }), svgEl('stop', { offset: '100%', class: 'spark-stop-b' }));
  defs.append(lg);
  const area = svgEl('path', { d: fill, fill: 'url(#' + id + ')', class: 'spark-area' });
  const line = svgEl('path', { d, class: 'spark-line', 'vector-effect': 'non-scaling-stroke' });
  const guide = svgEl('line', { x1: 0, x2: 0, y1: 0, y2: H, class: 'spark-guide', 'vector-effect': 'non-scaling-stroke', visibility: 'hidden' });
  svg.append(defs, area, line, guide);
  const dot = h('span', { class: 'spark-dot', hidden: true });
  const end = pts[pts.length - 1];
  const endDot = h('span', { class: 'spark-end', style: { left: (end[0] / W * 100).toFixed(2) + '%', top: (end[1] / H * 100).toFixed(2) + '%' } });
  // 走勢線包在會裁切的外層裡（打開儀表板時由左往右畫出來，revealSparks）
  box.append(h('span', { class: 'spark-wipe' }, svg), endDot, dot);
  box._spark = { svg, pts, hist, guide, dot, hover: false };
  box.title = '';
  box.setAttribute('aria-label', (w.label || '') + ' 最近 7 天的走勢（' + rawHist.length + ' 個點）');
}

function sparkMove(box, ev) {
  const s = box._spark;
  if (!s) return; // 還沒有走勢資料
  s.hover = true;
  const { svg, pts, hist, guide, dot } = s;
  const r = svg.getBoundingClientRect();
  const x = ((ev.clientX - r.left) / r.width) * SPARK_W;
  let lo = 0, hi = pts.length - 1;
  while (hi - lo > 1) { const mid = (lo + hi) >> 1; if (pts[mid][0] < x) lo = mid; else hi = mid; }
  const i = Math.abs(pts[lo][0] - x) < Math.abs(pts[hi][0] - x) ? lo : hi;
  const [px, py] = pts[i];
  guide.setAttribute('x1', px); guide.setAttribute('x2', px); guide.setAttribute('visibility', 'visible');
  const cx = (px / SPARK_W) * r.width, cy = (py / SPARK_H) * r.height;
  dot.hidden = false;
  dot.style.transform = 'translate(' + cx.toFixed(1) + 'px,' + cy.toFixed(1) + 'px)';
  showTip(fmtDateTime(hist[i][0]) + '　' + Math.round(hist[i][1]) + '%', r.left + cx, r.top + cy);
}

function sparkLeave(box) {
  const s = box._spark;
  if (s) { s.hover = false; s.guide.setAttribute('visibility', 'hidden'); s.dot.hidden = true; }
  hideTip();
}

// ───── 帳戶選單與對話框 ─────
export function accountMenu(a, anchor, at) {
  if (!a) return;
  if (isHub) { hubAccountMenu(a, anchor, at); return; }
  if (a.remoteOnly === true) { remoteOnlyMenu(a, anchor, at); return; }
  openMenu(anchor, [
    { label: '用這個帳戶開新對話', icon: 'plus', disabled: a.status === 'notInstalled', onSelect: () => ws.newConversation(a.id) },
    { label: '更新用量', icon: 'refresh', onSelect: () => post({ cmd: 'accountRefresh', accountId: a.id }) },
    a.loginBusy
      ? { label: '取消登入', icon: 'x', onSelect: () => post({ cmd: 'accountLoginCancel', accountId: a.id }) }
      : { label: a.status === 'ready' ? '重新登入' : '登入', icon: 'login', onSelect: () => post({ cmd: 'accountLogin', accountId: a.id }) },
    // WP3.4：Codex 可以用裝置碼登入（驗證碼顯示在帳戶卡，用手機開網址核准）
    a.deviceCodeLogin === true && !a.loginBusy
      ? { label: '用裝置碼登入（可用手機核准）', icon: 'keyboard', onSelect: () => post({ cmd: 'accountLogin', accountId: a.id, method: 'deviceCode' }) }
      : null,
    { type: 'sep' },
    { label: '重新命名…', icon: 'rename', onSelect: () => openEditAccount(a, 'name') },
    { label: '變更顏色…', icon: 'palette', onSelect: () => openEditAccount(a, 'color') },
    providerInfo(a.provider).safeMode
      ? { label: '精簡模式',
        desc: a.provider === 'Antigravity' ? '停用斜線指令與 Skills 展開（--disable-slash-commands）' : '不載入外掛、hooks、MCP、' + instructionFile(a.provider),
        checked: !!a.safeMode, keepOpen: false,
        onSelect: () => {
          post({ cmd: 'accountUpdate', accountId: a.id, safeMode: !a.safeMode });
          toast(a.provider === 'Antigravity'
            ? (a.safeMode ? '已關閉精簡模式，下次送出時恢復斜線指令與 Skills' : '已開啟精簡模式，下次送出時停用斜線指令與 Skills')
            : (a.safeMode ? '已關閉精簡模式，新的對話會載入外掛與設定' : '已開啟精簡模式，新的對話不載入外掛與設定'), 'success');
        } }
      : null,
    // 隔離帳戶共用系統預設帳戶的全域指示、記憶、MCP、skills（登入各自分開；精簡模式下不載入這些，共用也不作用）
    providerInfo(a.provider).sharedCustomizations && !a.isSystemDefault
      ? { label: '共用預設帳戶的設定',
        desc: a.safeMode ? '精簡模式下不作用' : instructionFile(a.provider) + '、記憶、MCP、skills 沿用系統預設帳戶，登入各自分開',
        checked: !!a.shareCustomizations, keepOpen: false,
        onSelect: () => { post({ cmd: 'accountUpdate', accountId: a.id, shareCustomizations: !a.shareCustomizations }); toast(a.shareCustomizations ? '已改用這個帳戶自己的設定，下次送出訊息時生效' : '已共用預設帳戶的設定，下次送出訊息時生效', 'success'); } }
      : null,
    { label: '開啟設定資料夾', icon: 'folderOpen', desc: a.configDirectory || null, onSelect: () => post({ cmd: 'accountOpenFolder', accountId: a.id }) },
    { type: 'sep' },
    { label: '登出', icon: 'logout', disabled: a.status !== 'ready', onSelect: () => confirmLogout(a) },
    { label: '移除帳戶…', icon: 'trash', danger: true, onSelect: () => confirmRemove(a) },
  ], { at, placement: 'bottom-end', minWidth: 250 });
}

/**
 * 瀏覽器版的帳戶選單：只有伺服器接受的操作（accountUpdate 的名稱與顏色、accountReorder）與擁有者 HTTP 的「從目錄刪除」。
 * 沒有側欄可以拖曳，改以「上移」「下移」排序；不先在畫面上排，等伺服器推回 accounts
 */
function hubAccountMenu(a, anchor, at) {
  const up = reorderIds(state.accounts, a.id, -1);
  const down = reorderIds(state.accounts, a.id, 1);
  openMenu(anchor, [
    { label: '重新命名…', icon: 'rename', onSelect: () => openEditAccount(a, 'name') },
    { label: '變更顏色…', icon: 'palette', onSelect: () => openEditAccount(a, 'color') },
    { type: 'sep' },
    { label: '上移', icon: 'chevUp', disabled: !up, onSelect: () => post({ cmd: 'accountReorder', ids: up }) },
    { label: '下移', icon: 'chevDown', disabled: !down, onSelect: () => post({ cmd: 'accountReorder', ids: down }) },
    { type: 'sep' },
    // 〔遠端〕WP4.8：每台有這個帳戶的電腦都登出（durable，離線的 7 天內上線才執行）
    logoutAllMenuItem(a),
    { label: '從目錄刪除…', icon: 'trash', danger: true, onSelect: () => confirmDeleteAccount(a) },
  ], { at, placement: 'bottom-end', minWidth: 200 });
}

/**
 * remoteOnly 帳戶的選單（帳戶卡的「…」、側欄精簡卡的點擊、Enter、右鍵）：只有「在這台登入」。
 * 它的 id 是邏輯帳戶 id（la_…），不能送 accountRefresh、accountLogin、accountUpdate、accountRemove、accountOpenFolder
 */
function remoteOnlyMenu(a, anchor, at) {
  const item = claimableHere(a)
    ? { label: '在這台登入', icon: 'login', desc: '用「' + a.label + '」的名稱與顏色在這台新增帳戶並登入', onSelect: () => loginHere(a) }
    : { label: '在這台登入', icon: 'login', desc: AGY_ONE_LOGIN, disabled: true };
  openMenu(anchor, [item], { at, placement: 'bottom-end', minWidth: 240 });
}

/**
 * remoteOnly 的帳戶能不能「在這台登入」：要能在這台另外建帳戶的種類（ProviderView.isolatedAccounts）。
 * Antigravity 一台只有一個 agy 登入，從目錄在這台登入會換掉原本的登入（宿主也回不支援，設計 2026-10-22-grok-antigravity-sync 5.2）
 */
function claimableHere(a) {
  return !!providerInfo(a.provider).isolatedAccounts;
}

const AGY_ONE_LOGIN = '這台的 agy 沒有登入這個帳號。agy 一台電腦只有一個登入，要在這台用這個帳號，請在這台的 agy 換成它。';

/** 精簡模式不載入的指示檔 */
function instructionFile(provider) {
  return provider === 'Claude' ? 'CLAUDE.md' : 'AGENTS.md';
}

async function confirmLogout(a) {
  const s = a.sessions || {};
  const info = providerInfo(a.provider);
  const r = await confirmDialog({
    title: '登出「' + a.label + '」？',
    message: '登出後要重新登入才能用這個帳戶送訊息。' + (s.running ? '目前有 ' + s.running + ' 個對話執行中，會無法繼續。' : ''),
    // 本機預設帳戶用的是使用者原本的設定資料夾：登出的是這台電腦上的 CLI 本身，不只是 app 裡的帳戶。
    // agy 沒有登出指令：宿主開 agy 視窗，請使用者在裡面輸入 /logout
    detail: !info.isolatedAccounts
      ? '會開啟 agy 視窗，請在視窗裡輸入 /logout 後按 Enter，再關閉視窗。這台電腦的 agy 登入存在 Windows 認證管理員，終端機等其他地方的 agy 也會一起登出。'
      : a.isSystemDefault
        ? '這個帳戶使用這台電腦預設的設定資料夾' + (a.configDirectory ? '（' + a.configDirectory + '）' : '') + '，終端機、VS Code 等其他地方的 '
          + info.name + ' 也會一起登出。'
        : null,
    confirmLabel: info.isolatedAccounts ? '登出' : '開啟 agy 視窗',
  });
  if (r.ok) post({ cmd: 'accountLogout', accountId: a.id });
}

async function confirmRemove(a) {
  const s = a.sessions || {};
  const r = await confirmDialog({
    title: '移除帳戶「' + a.label + '」？',
    message: '帳戶會從 Clode Myriagent 移除。用這個帳戶的對話要換帳戶才能繼續' + (s.running ? '，執行中的 ' + s.running + ' 個對話會被停止' : '') + '。',
    // 〔同步〕已連結到帳戶目錄：移除的是這台的本機帳戶與連結，目錄裡的邏輯帳戶還在（設計 6.7），會以灰色「這台未登入」卡片回來
    detail: typeof a.logicalId === 'string' && a.logicalId
      ? '這個帳戶仍留在帳戶目錄：移除後會以灰色「這台未登入」卡片留在清單上。要讓所有電腦都不再顯示，請到網頁版的帳戶選單選「從目錄刪除」。'
      : null,
    checkbox: {
      label: '同時刪除這個帳戶的設定資料夾（登入憑證與 CLI 對話紀錄）',
      disabled: !!a.isSystemDefault,
      note: a.isSystemDefault ? '這是這台電腦預設的設定資料夾，不能從這裡刪除：' + (a.configDirectory || '') : (a.configDirectory || ''),
    },
    confirmLabel: '移除', danger: true, width: 480,
  });
  if (r.ok) post({ cmd: 'accountRemove', accountId: a.id, deleteData: !!(r.checked && !a.isSystemDefault) });
}

function palettePicker(value, onPick) {
  const wrap = h('div', { class: 'palette', role: 'radiogroup', 'aria-label': '帳戶顏色' });
  const set = (c) => {
    for (const b of wrap.children) { const on = b.dataset.c.toLowerCase() === String(c).toLowerCase(); b.classList.toggle('on', on); b.setAttribute('aria-checked', String(on)); }
  };
  const swatch = (c, n) => {
    const b = h('button', { type: 'button', class: 'swatch', role: 'radio', title: n, 'aria-label': n, dataset: { c }, style: { '--acc': c } });
    b.addEventListener('click', () => { set(c); onPick(c); });
    wrap.append(b);
  };
  for (const p of PALETTE) swatch(p.c, p.n);
  // 目前的顏色不在色票裡（宿主的預設色、從資料夾還原的帳戶）：多放一個「目前顏色」並選取，看得出現在的顏色，也選得回來
  const cur = String(value || '');
  if (cur && !PALETTE.some((p) => p.c.toLowerCase() === cur.toLowerCase())) swatch(cur, '目前顏色');
  set(value);
  wrap.setValue = set;
  return wrap;
}

/** 新增帳戶對話框每種 CLI 的說明（只能有一個帳戶、已經加入的另外說明） */
const ADD_CHOICE = {
  Claude: { title: 'Claude Code', sub: 'Claude 訂閱' },
  Codex: { title: 'Codex', sub: 'ChatGPT 訂閱' },
  Antigravity: { title: 'Antigravity', sub: 'Google 帳戶' },
  Grok: { title: 'Grok Build', sub: 'SuperGrok 訂閱（grok.com）' },
};

export function openAddAccount() {
  // remoteOnly（只在其他電腦）不算：它不佔這台的設定資料夾，也不擋 Antigravity 一台一個的限制
  const count = (p) => state.accounts.filter((a) => a.provider === p && a.remoteOnly !== true).length;
  // 不能新增隔離帳戶的（Antigravity：登入存在認證管理員，一台電腦一個）已經有了就不能選
  const available = (p) => providerInfo(p).isolatedAccounts || count(p) === 0;
  let provider = providerKeys().find(available) || 'Claude';
  let color = DEFAULT_COLORS[provider] || DEFAULT_COLORS.Claude;
  let colorTouched = false;
  const defName = (p) => p + ' 帳戶' + (providerInfo(p).isolatedAccounts ? ' ' + (count(p) + 1) : '');
  const name = h('input', { type: 'text', class: 'input', placeholder: defName(provider), maxlength: '40', autocomplete: 'off', spellcheck: 'false' });
  const preview = h('span', { class: 'add-preview' });
  const pal = palettePicker(color, (c) => { color = c; colorTouched = true; sync(); });
  const choice = (p) => {
    const c = ADD_CHOICE[p] || { title: providerInfo(p).name, sub: providerInfo(p).name };
    const ok = available(p);
    const b = h('button', { type: 'button', class: 'prov', role: 'radio', dataset: { p }, disabled: !ok, title: ok ? null : '已經加入了：' + p + ' 一台電腦只能登入一個帳戶' },
      providerMark(p), h('span', { class: 'prov-text' }, h('b', { text: c.title }), h('span', { text: ok ? c.sub : '已加入（一台電腦一個）' })));
    b.addEventListener('click', () => { provider = p; if (!colorTouched) { color = DEFAULT_COLORS[p] || color; pal.setValue(color); } name.placeholder = defName(p); sync(); });
    return b;
  };
  const provs = h('div', { class: 'provs', role: 'radiogroup', 'aria-label': '代理' }, providerKeys().map(choice));
  const note = h('p', { class: 'form-note' });
  function sync() {
    for (const b of provs.children) { const on = b.dataset.p === provider; b.classList.toggle('on', on); b.setAttribute('aria-checked', String(on)); b.style.setProperty('--acc', on ? color : (DEFAULT_COLORS[b.dataset.p] || '#8A94A6')); }
    preview.style.setProperty('--acc', color);
    preview.textContent = (name.value.trim() || name.placeholder);
    note.textContent = providerInfo(provider).isolatedAccounts
      ? '會建立一個獨立的設定資料夾給這個帳戶，不影響這台電腦原本的登入。新增後會立刻開始登入流程。'
      : '使用這台電腦 agy 的登入（存在 Windows 認證管理員，一台電腦只能有一個）。新增後會檢查登入狀態，還沒登入就開啟 agy 視窗登入。';
  }
  name.addEventListener('input', sync);
  const body = h('div', { class: 'form' },
    h('div', { class: 'field' }, h('div', { class: 'field-label', text: '代理' }), provs),
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: '名稱' }), name),
    h('div', { class: 'field' }, h('div', { class: 'field-label', text: '顏色' }), pal, h('div', { class: 'field-hint' }, h('span', { text: '分頁、卡片與圓環會用這個顏色：' }), preview)),
    note);
  const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
  const ok = h('button', { type: 'button', class: 'btn primary', text: '新增並登入' });
  const d = openDialog({ title: '新增帳戶', body, footer: [cancel, ok], width: 560, className: 'dlg-add', initialFocus: name });
  cancel.addEventListener('click', () => d.close());
  const submit = () => {
    if (!available(provider)) return;
    const label = name.value.trim() || name.placeholder;
    post({ cmd: 'accountAdd', provider, label, color });
    d.close();
    toast(providerInfo(provider).isolatedAccounts ? '已新增「' + label + '」，請在開啟的視窗完成登入' : '已新增「' + label + '」，還沒登入的話請在開啟的 agy 視窗完成登入', 'success');
  };
  ok.addEventListener('click', submit);
  name.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing && e.keyCode !== 229) { e.preventDefault(); submit(); } });
  sync();
  return d;
}

export function openEditAccount(a, focus) {
  // remoteOnly 的名稱與顏色屬於目錄（其他電腦）：這台不能改（accountUpdate 只收本機帳戶），能做的只有「在這台登入」
  if (!isHub && a.remoteOnly === true) return null;
  let color = safeColor(a.color, a.provider);
  const name = h('input', { type: 'text', class: 'input', value: a.label, maxlength: '40', autocomplete: 'off', spellcheck: 'false' });
  const pal = palettePicker(color, (c) => { color = c; });
  const body = h('div', { class: 'form' },
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: '名稱' }), name),
    h('div', { class: 'field' }, h('div', { class: 'field-label', text: '顏色' }), pal));
  const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
  const ok = h('button', { type: 'button', class: 'btn primary', text: '儲存' });
  const d = openDialog({ title: '帳戶「' + a.label + '」', body, footer: [cancel, ok], width: 460, initialFocus: focus === 'color' ? pal.querySelector('.swatch.on') || pal.firstChild : name });
  cancel.addEventListener('click', () => d.close());
  const save = () => {
    const msg = { cmd: 'accountUpdate', accountId: a.id };
    const label = name.value.trim();
    if (label && label !== a.label) msg.label = label;
    if (color.toLowerCase() !== String(a.color || '').toLowerCase()) msg.color = color;
    if (msg.label || msg.color) post(msg);
    d.close();
  };
  ok.addEventListener('click', save);
  name.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing && e.keyCode !== 229) { e.preventDefault(); save(); } });
  if (focus !== 'color') requestAnimationFrame(() => name.select());
}

// ───── CLI 區（儀表板與設定對話框共用） ─────
const INSTALL_KIND = { npm: 'npm 全域套件', native: '原生安裝', winget: 'WinGet', managed: '由 Clode Myriagent 管理', other: '其他方式' };
const ORIGIN_LABEL = { external: '外部更新', app: 'app 更新' };
const ORIGIN_TITLE = {
  external: '不是 Clode Myriagent 做的更新（例如你在終端機更新，或互動式 claude 的背景自動更新）',
  app: 'Clode Myriagent 執行的更新',
};
/** 宿主在更新開始時放的通用進度字樣：已經有階段說明，不再重複顯示 */
const GENERIC_PROGRESS = new Set(['更新中…', '安裝中…', '處理中…']);
/** 換版中的對話清單最多列幾個（其餘顯示「還有 N 個」） */
const RESTART_LIST_MAX = 4;

/** 〔熱更新〕排空逾時的問句按了「繼續等」：provider → 那次更新的 startedAt（同一次更新不再問；新的一次更新照樣問） */
const overdueDismissed = new Map();

export function renderCli(container) {
  const list = providerKeys().map((p) => ({ p, v: state.cli[p] }));
  reconcile(container, list, (x) => x.p, (x) => createCliCard(x.p), (node, x) => updateCliCard(node, x.v));
  for (const node of container.children) updateCliCard(node, state.cli[node.dataset.key]);
}

/**
 * 畫面上這種 CLI 的卡（儀表板與開著的設定對話框各一張）重畫：「繼續等」要讓兩邊同時收起問句。
 * 直接從畫面上找，不另外記：記在模組的集合裡，關掉的設定對話框（連同整棵 DOM）就一直被留著
 */
function rerenderCliCards(provider) {
  for (const card of document.querySelectorAll('article.ccard')) {
    if (card.dataset.key === provider && card._els) updateCliCard(card, state.cli[provider]);
  }
}

function createCliCard(p) {
  const card = h('article', { class: 'ccard', style: { '--acc': DEFAULT_COLORS[p] || '#8A94A6', '--acc-ink': '#fff' } });
  const pill = h('span', { class: 'pill' });
  const ver = h('div', { class: 'ccard-ver' });
  const rows = h('dl', { class: 'ccard-rows' });

  // 〔熱更新〕安裝不一致（「重新安裝」是下方的主要按鈕）
  const brokenText = h('span', { class: 'cc-main' });
  const broken = h('div', { class: 'cc-block cc-broken', dataset: { tone: 'err' }, role: 'alert', hidden: true },
    icon('warning', 'cc-ic'), h('div', { class: 'cc-body' }, brokenText,
      h('span', { class: 'cc-sub', text: '安裝修好之前不會重開任何對話，正在執行的對話照常用原本的程序。' })));
  // 〔熱更新〕安裝正在變動（偵測到換檔）
  const changing = h('div', { class: 'cc-block cc-changing', dataset: { tone: 'busy' }, role: 'status', hidden: true },
    spinner('cc-ic'), h('div', { class: 'cc-body' }, h('span', { class: 'cc-main', text: '安裝變動中…' }),
      h('span', { class: 'cc-sub', text: '偵測到 CLI 正在換檔：新的對話程序等安裝穩定才啟動，這段期間送出的訊息先排進佇列。' })));

  // 〔熱更新〕app 觸發的更新（npm 屏障）：四個階段的進度、排空中的「現在中斷」、逾時的問句與「取消更新」「繼續等」
  const steps = UPDATE_PHASES.map((ph) => h('span', { class: 'cc-step', title: ph.label }));
  const updTitle = h('span', { class: 'cc-main' });
  const updWhen = h('span', { class: 'cc-when' });
  const updDetail = h('span', { class: 'cc-sub' });
  const updProgress = h('span', { class: 'cc-sub', hidden: true });
  const updQ = h('div', { class: 'cc-q', hidden: true });
  const interrupt = h('button', { type: 'button', class: 'btn sm' });
  interrupt.addEventListener('click', async () => {
    if (await confirmApplyNowProvider(p, state.cli[p], true)) post({ cmd: 'cliApplyNow', provider: p });
  });
  const cancelUpd = h('button', { type: 'button', class: 'btn sm ghost' });
  cancelUpd.addEventListener('click', () => post({ cmd: 'cliCancelUpdate', provider: p }));
  // 「繼續等」什麼都不送：只收起這次更新的問句
  const keepWaiting = h('button', { type: 'button', class: 'btn sm ghost' });
  keepWaiting.addEventListener('click', () => {
    const u = state.cli[p] && state.cli[p].update;
    overdueDismissed.set(p, (u && u.startedAt) || null);
    rerenderCliCards(p);
  });
  const updActs = h('div', { class: 'cc-acts' }, interrupt, cancelUpd, keepWaiting);
  const upd = h('div', { class: 'cc-block cc-upd', dataset: { tone: 'busy' }, role: 'status', hidden: true },
    h('div', { class: 'cc-steps', 'aria-hidden': 'true' }, steps),
    h('div', { class: 'cc-line' }, spinner('cc-ic'), h('div', { class: 'cc-body' }, h('span', { class: 'cc-head' }, updTitle, updWhen), updDetail, updProgress)),
    updQ, updActs);

  // 〔熱更新〕各對話的換版彙總：「3 個對話換版中」、各狀態的計數、等著換版的對話（點了開啟）、「立即套用」
  const rsIc = h('span', { class: 'cc-ic-wrap' });
  const rsTitle = h('span', { class: 'cc-main' });
  const rsHint = h('span', { class: 'cc-sub', hidden: true });
  const apply = h('button', { type: 'button', class: 'btn sm' });
  apply.addEventListener('click', async () => {
    if (await confirmApplyNowProvider(p, state.cli[p], false)) post({ cmd: 'cliApplyNow', provider: p });
  });
  const parts = h('div', { class: 'cc-parts' });
  const convs = h('div', { class: 'cc-convs' });
  const restart = h('div', { class: 'cc-block cc-restart', dataset: { tone: 'busy' }, hidden: true },
    h('div', { class: 'cc-line' }, rsIc, h('div', { class: 'cc-body' }, rsTitle, rsHint), apply), parts, convs);

  const msg = h('div', { class: 'ccard-msg', hidden: true });
  const act = h('button', { type: 'button', class: 'btn sm' });
  act.addEventListener('click', () => {
    const v = state.cli[p];
    if (!v) return;
    // 安裝不一致時是「重新安裝」：和安裝走同一個指令
    post({ cmd: v.installBroken || !v.installed ? 'cliInstall' : 'cliUpdate', provider: p });
  });
  const foot = h('div', { class: 'ccard-foot' }, act);
  card.append(h('div', { class: 'ccard-head' }, providerMark(p),
    h('div', { class: 'ccard-id' }, h('div', { class: 'ccard-line' }, h('h3', { class: 'ccard-name', text: cliName(p) }), pill), ver)),
  rows, broken, changing, upd, restart, msg, foot);
  card._els = {
    pill, ver, rows, msg, act, foot,
    broken, brokenText, changing,
    upd, steps, updTitle, updWhen, updDetail, updProgress, updQ, updActs, interrupt, cancelUpd, keepWaiting,
    restart, rsIc, rsTitle, rsHint, apply, parts, convs,
  };
  return card;
}

function updateCliCard(card, v) {
  const e = card._els;
  const p = card.dataset.key;
  e.ver.textContent = '';
  e.rows.textContent = '';
  e.pill.hidden = true;
  e.pill.title = '';
  if (!v) {
    e.ver.append(h('span', { class: 'dim', text: '檢查中…' }));
    for (const b of [e.foot, e.msg, e.broken, e.changing, e.upd, e.restart]) b.hidden = true;
    return;
  }
  // 〔熱更新〕欄位（宿主沒送＝沒有換版要處理）
  const broken = typeof v.installBroken === 'string' && v.installBroken.trim() ? v.installBroken : null;
  const upd = v.update && typeof v.update === 'object' && v.update.phase ? v.update : null;
  const changing = !!v.changing && !upd && !v.busy;
  // 安裝損壞時宿主不重開任何對話：不顯示換版彙總與「立即套用」
  const summary = broken ? null : cliRestartSummary(v);
  const restarting = !!summary && summary.pending > 0;

  // 找到執行檔但還沒查過版本（啟動後第一次檢查完成前）：還不知道能不能用，不能顯示「未安裝」與安裝按鈕
  const checking = !v.installed && !!v.checking;
  if (checking) {
    e.ver.append(h('span', { class: 'dim', text: '檢查中…' }));
  } else if (!v.installed) {
    e.pill.className = 'pill st-notInstalled'; e.pill.textContent = '未安裝'; e.pill.hidden = false;
    e.ver.append(h('span', { class: 'dim', text: v.latest ? '最新版 ' + v.latest : '這台電腦還沒有安裝' }));
  } else {
    e.ver.append(h('span', { class: 'ver', text: v.version || '版本不明' }));
    if (v.updateAvailable && v.latest) {
      e.ver.append(icon('chevRight', 'ver-arrow'), h('span', { class: 'ver new', text: v.latest }));
      e.pill.className = 'pill st-update'; e.pill.textContent = '有新版'; e.pill.hidden = false;
    } else if (v.latest && !v.latestError) { e.pill.className = 'pill st-ready'; e.pill.textContent = '已是最新版'; e.pill.title = '最新版本 ' + v.latest; e.pill.hidden = false; }
  }
  // 換版相關的狀態比「有新版」「已是最新版」優先（與頂部列的 CLI 徽章同一套規則：hotupdate.js 的 cliHotState）
  const hotState = cliHotState(v);
  const hot = hotState ? [hotState.tone === 'err' ? 'st-error' : 'st-busy', hotState.label] : null;
  if (hot) { e.pill.className = 'pill ' + hot[0]; e.pill.textContent = hot[1]; e.pill.title = ''; e.pill.hidden = false; }

  const row = (k, val, cls) => { if (!val) return; e.rows.append(h('dt', { text: k }), h('dd', { class: cls || null, text: val, title: val })); };
  row('安裝方式', v.installKind ? (INSTALL_KIND[v.installKind] || v.installKind) : (v.installed ? '不明' : null));
  row('路徑', v.path, 'mono');
  // 最近一次已穩定的換版：「2.1.295 → 2.1.297 外部更新 3 分鐘前」
  const lc = v.lastChange && typeof v.lastChange === 'object' && v.lastChange.to ? v.lastChange : null;
  if (lc) {
    const origin = ORIGIN_LABEL[enumKey(lc.origin)] || null;
    e.rows.append(h('dt', { text: '最近換版' }), h('dd', { class: 'cc-change', title: [versionArrow(lc.from, lc.to), ORIGIN_TITLE[enumKey(lc.origin)], lc.at ? fmtDateTime(lc.at) : null].filter(Boolean).join('\n') },
      h('span', { class: 'mono', text: versionArrow(lc.from, lc.to) }),
      origin ? h('span', { class: 'cc-tag', text: origin }) : null,
      lc.at ? h('span', { class: 'dim', dataset: { relAt: String(lc.at) }, text: fmtRelative(lc.at) }) : null));
  }
  // 最近一波換版的結果：進行中時在彙總區顯示，結束後留在這裡（句子較長，可以折行）
  const doneText = restarting ? null : cliRestartDoneText(v);
  row('換版結果', doneText, 'wrap');
  if (v.lastCheckedAt) {
    e.rows.append(h('dt', { text: '上次檢查' }), h('dd', { dataset: { relAt: String(v.lastCheckedAt) }, text: fmtRelative(v.lastCheckedAt), title: fmtDateTime(v.lastCheckedAt) }));
  }

  e.broken.hidden = !broken;
  if (broken) e.brokenText.textContent = broken;
  e.changing.hidden = !changing;
  updateCliUpdateBlock(e, p, v, upd);
  updateCliRestartBlock(e, v, summary, !broken && !upd);

  e.msg.textContent = '';
  // 更新屏障的進度另有區塊：不再重複顯示「更新中…」
  if (v.busy && !upd) { e.msg.append(spinner(), h('span', { text: v.progress || '處理中…' })); e.msg.className = 'ccard-msg busy'; e.msg.hidden = false; }
  else if (v.error || v.latestError) { e.msg.append(icon('warning'), h('span', { text: v.error || v.latestError })); e.msg.className = 'ccard-msg err'; e.msg.hidden = false; }
  else e.msg.hidden = true;
  e.foot.hidden = checking || !!upd || (!broken && v.installed && !v.updateAvailable && !v.busy);
  e.act.disabled = !!v.busy;
  if (broken) {
    e.act.className = 'btn sm primary';
    setButtonContent(e.act, v.busy ? 'spinner' : 'download', v.busy ? '安裝中…' : '重新安裝');
  } else {
    e.act.className = 'btn sm' + (v.updateAvailable || !v.installed ? ' primary' : '');
    if (v.busy) setButtonContent(e.act, 'spinner', v.installed ? '更新中…' : '安裝中…');
    else setButtonContent(e.act, 'download', v.installed ? '更新到 ' + (v.latest || '最新版') : '安裝 ' + cliName(p));
  }
  card.classList.toggle('has-update', !!v.updateAvailable && !hot);
  card.classList.toggle('is-hot', !!hot && !broken);
  card.classList.toggle('is-broken', !!broken);
}

/** 〔熱更新〕更新屏障的進度：第幾步、開始多久、排空中的「現在中斷」、逾時的問句（「取消更新」「繼續等」） */
function updateCliUpdateBlock(e, p, v, u) {
  e.upd.hidden = !u;
  if (!u) { overdueDismissed.delete(p); return; }
  const info = updatePhaseInfo(u);
  e.steps.forEach((s, i) => { s.classList.toggle('done', info.index > i); s.classList.toggle('cur', info.index === i); });
  e.updTitle.textContent = (info.index >= 0 ? '第 ' + (info.index + 1) + '／' + UPDATE_PHASES.length + ' 步・' : '') + info.label;
  if (u.startedAt) {
    e.updWhen.dataset.relAt = String(u.startedAt);
    e.updWhen.dataset.relPrefix = '開始於 ';
    e.updWhen.textContent = '開始於 ' + fmtRelative(u.startedAt);
    e.updWhen.title = fmtDateTime(u.startedAt);
    e.updWhen.hidden = false;
  } else { delete e.updWhen.dataset.relAt; e.updWhen.hidden = true; }
  e.updDetail.textContent = info.detail;
  e.updDetail.hidden = !info.detail;
  const prog = typeof v.progress === 'string' ? v.progress.trim() : '';
  e.updProgress.textContent = prog;
  e.updProgress.hidden = !prog || GENERIC_PROGRESS.has(prog) || prog === info.label;

  const draining = info.phase === 'draining';
  const startedAt = u.startedAt || null;
  if (overdueDismissed.has(p) && overdueDismissed.get(p) !== startedAt) overdueDismissed.delete(p);
  const ask = draining && !!u.overdue && !overdueDismissed.has(p);
  e.updQ.hidden = !ask;
  e.updQ.textContent = ask ? overdueQuestion(u) : '';
  e.upd.dataset.tone = ask ? 'user' : 'busy';
  e.updActs.hidden = !draining;
  e.interrupt.hidden = !draining;
  e.interrupt.className = 'btn sm' + (ask ? ' primary' : '');
  setButtonContent(e.interrupt, 'stop', '現在中斷');
  e.interrupt.title = '不等安全點：還在執行的對話立刻停下，更新完成後以新版自動接續';
  e.cancelUpd.hidden = !(draining && u.overdue);
  setButtonContent(e.cancelUpd, 'x', '取消更新');
  e.cancelUpd.title = '不更新了：已停下的對話以目前的版本接續';
  e.keepWaiting.hidden = !ask;
  setButtonContent(e.keepWaiting, 'clock', '繼續等');
}

/** 〔熱更新〕各對話的換版彙總與「立即套用」 */
function updateCliRestartBlock(e, v, summary, canApply) {
  e.restart.hidden = !summary;
  if (!summary) return;
  const active = summary.pending > 0;
  e.restart.dataset.tone = active ? 'busy' : 'info';
  const icKey = active ? 'busy' : 'info';
  if (e.rsIc._key !== icKey) {
    e.rsIc._key = icKey;
    e.rsIc.textContent = '';
    e.rsIc.append(active ? restartIcon('restarting', 'cc-ic') : icon('info', 'cc-ic'));
  }
  e.rsTitle.textContent = summary.title;
  e.rsHint.textContent = summary.hint || '';
  e.rsHint.hidden = !summary.hint;
  // 「立即套用」在 runningOnOldVersion 或 restartPending 大於 0 時顯示（web-protocol 第五節）；更新屏障進行中改由進度區處理
  const pending = Math.max(Number(v.restartPending) || 0, summary.waiting.length);
  e.apply.hidden = !(canApply && (pending > 0 || Number(v.runningOnOldVersion) > 0));
  setButtonContent(e.apply, 'refresh', '立即套用');
  e.apply.title = '不等安全點，現在就以新版重開這些對話（執行中的工具會被中斷）';

  const partsSig = JSON.stringify(summary.parts);
  if (e.parts._sig !== partsSig) {
    e.parts._sig = partsSig;
    e.parts.textContent = '';
    for (const x of summary.parts) e.parts.append(h('span', { class: 'cc-part', dataset: { tone: x.tone }, text: x.text }));
  }
  e.parts.hidden = !summary.parts.length;

  const popout = state.mode === 'popout';
  const shown = summary.waiting.slice(0, RESTART_LIST_MAX);
  const titleOf = (id) => { const s = summaryOf(id); return (s && s.title) || '對話'; };
  const listSig = JSON.stringify([popout, shown.map((w) => [w.conversationId, w.state, w.detail || '', titleOf(w.conversationId)]), summary.waiting.length]);
  if (e.convs._sig !== listSig) {
    e.convs._sig = listSig;
    e.convs.textContent = '';
    for (const w of shown) {
      const st = restartStateOf(w.state);
      const title = titleOf(w.conversationId);
      const kids = [h('span', { class: 'cc-conv-dot' }), h('span', { class: 'cc-conv-title', text: title }),
        h('span', { class: 'cc-conv-state', text: st.short + (w.detail ? '：' + w.detail : '') })];
      let rowEl;
      if (popout) rowEl = h('div', { class: 'cc-conv', dataset: { tone: st.tone, conv: w.conversationId } }, kids);
      else {
        rowEl = h('button', { type: 'button', class: 'cc-conv', dataset: { tone: st.tone, conv: w.conversationId }, title: '開啟「' + title + '」' }, kids);
        rowEl.addEventListener('click', () => openConversationFromCard(rowEl, w.conversationId));
      }
      e.convs.append(rowEl);
    }
    if (summary.waiting.length > shown.length) e.convs.append(h('div', { class: 'cc-more', text: '還有 ' + (summary.waiting.length - shown.length) + ' 個對話' }));
  }
  e.convs.hidden = !shown.length;
}

/** CLI 卡上的換版中對話：開啟那個對話（卡片在設定對話框裡時先關掉對話框） */
function openConversationFromCard(el, id) {
  if (!ws || !id) return;
  if (el.closest('.dlg')) handleEscape();
  ws.openConversation(id);
}

// ───── 「立即檢查」（儀表板與設定對話框共用） ─────
// 宿主檢查時不送「進行中」：按下後由網頁顯示「檢查中…」並停用按鈕，宿主依序檢查每個 CLI，
// 等每個 CliView 的 checkedAt 都更新才算完成，再用提示說明結果（lastCheckedAt 只在查到最新版時更新，離線時不會變，不能拿來判斷）
const CLI_CHECK_TIMEOUT = 3 * 60e3; // 每個 CLI 查版本最多 30 秒、查最新版最多 20 秒；逾時就恢復按鈕
let cliCheck = null; // 進行中的檢查：{ before: { 每種 CLI 的 checkedAt }, timer }

export function cliCheckButton() {
  // 以屬性標記、需要時從畫面上找（不記在模組的集合裡：關掉的設定對話框才不會被留著）
  const btn = h('button', { type: 'button', class: 'btn sm', dataset: { cliCheck: '' } });
  btn.addEventListener('click', startCliCheck);
  syncCliCheckButton(btn);
  return btn;
}

function syncCliCheckButton(btn) {
  btn.disabled = !!cliCheck;
  setButtonContent(btn, cliCheck ? 'spinner' : 'refresh', cliCheck ? '檢查中…' : '立即檢查');
}

function syncCliCheckButtons() {
  for (const btn of document.querySelectorAll('button[data-cli-check]')) syncCliCheckButton(btn);
}

const checkedAt = (p) => (state.cli[p] && state.cli[p].checkedAt) || null;

function startCliCheck() {
  if (cliCheck) return;
  cliCheck = { before: Object.fromEntries(providerKeys().map((p) => [p, checkedAt(p)])), timer: setTimeout(() => finishCliCheck(false), CLI_CHECK_TIMEOUT) };
  post({ cmd: 'cliCheck' });
  syncCliCheckButtons();
}

function settleCliCheck() {
  if (cliCheck && Object.keys(cliCheck.before).every((p) => checkedAt(p) && checkedAt(p) !== cliCheck.before[p])) finishCliCheck(true);
}

function finishCliCheck(done) {
  clearTimeout(cliCheck.timer);
  cliCheck = null;
  syncCliCheckButtons();
  if (done) {
    const r = cliCheckResult();
    toast(r.text, r.level);
  }
}

/** 「檢查完成：Claude Code 已是最新版；Codex 有新版 0.162.0」 */
function cliCheckResult() {
  const parts = [];
  let level = 'success';
  for (const p of providerKeys()) {
    const v = state.cli[p];
    if (!v) continue;
    const name = cliName(p);
    if (!v.installed) {
      // 找得到執行檔卻查不到版本＝無法執行（原因在 CLI 卡上）
      parts.push(name + (v.path ? ' 無法執行' : ' 尚未安裝'));
      if (v.path) level = 'error';
    } else if (v.updateAvailable) {
      parts.push(name + ' 有新版 ' + v.latest);
      if (level === 'success') level = 'info';
    } else if (v.latest && !v.latestError) parts.push(name + ' 已是最新版');
    else {
      // 查最新版失敗（離線、registry 被擋）：不能說已是最新版（原因在 CLI 卡上）
      parts.push(name + ' 查不到最新版本');
      level = 'error';
    }
  }
  return { text: '檢查完成：' + parts.join('；'), level };
}

/**
 * 設定按鈕的圖示與文字（iconName 為 'spinner' 時顯示轉圈）。節點只建立一次，之後只改文字、圖示種類變了才換：
 * 每則更新都整個重建的話，滑鼠按下時的目標（按鈕裡的文字或圖示）會被移出 DOM，放開時就不會觸發 click，按了沒反應。
 */
export function setButtonContent(btn, iconName, text) {
  let c = btn._content;
  if (!c) {
    c = btn._content = { iconName: null, ic: null, label: h('span') };
    btn.textContent = '';
    btn.append(c.label);
  }
  if (c.iconName !== iconName) {
    const ic = iconName === 'spinner' ? spinner() : icon(iconName);
    if (c.ic) c.ic.replaceWith(ic);
    else btn.prepend(ic);
    c.ic = ic;
    c.iconName = iconName;
  }
  if (c.label.textContent !== text) c.label.textContent = text;
}

function setSetting(key, value) {
  state.settings[key] = value;
  post({ cmd: 'setting', key, value });
}
