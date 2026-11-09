// 電腦狀態面板（對話檢視的右側）：每台連線電腦的 CPU、記憶體、CPU 溫度、顯示卡、磁碟、與伺服器的延遲、網路流量；最上面是 MyriagentHub 主機（伺服器）自己的負載。
// 資料只有宿主送的 deviceStats（整份取代；桌面是這台＋伺服器轉來的其他電腦，瀏覽器版全部來自伺服器；hub 是伺服器主機，拿不到時是 null），
// 名稱與在線看 state.remote.devices。
// 面板看得到（顯示中、對話檢視、頁面沒有被藏起來）時送 monitor {on:true}，否則 {on:false}；每則 init 之後依目前狀態再送一次（宿主在 ready、重連時會忘掉）。
// 規則在 monitorrules.js；這裡只建 DOM 與更新：卡片、量表、走勢圖都只建一次，之後每秒的更新只改有變的節點（滑鼠停在走勢圖上時提示不閃）。
// 文字一律 textContent、SVG 一律 createElementNS（CSP 與 live-rules.test.mjs 的 HTML 寫入點掃描）。
import { h, setHidden } from './util.js';
import { icon } from './icons.js';
import { state, post, isHub } from './store.js';
import { showTip, hideTip } from './ui.js';
import { calmMotion, onMotionChange } from './theme.js';
import * as R from './monitorrules.js';
import { openCardMenu } from './devicefiles.js';

const NS = 'http://www.w3.org/2000/svg';
/** 走勢圖的座標系（preserveAspectRatio="none" 拉滿寬度；線用 non-scaling-stroke） */
const CW = 240;
const NET_H = 31;
const RTT_H = 18;

let root = null;
let listEl = null;
let countEl = null;
let footEl = null;
let headEl = null;    // 面板標頭（掃光的轉速跟著最忙那台的 CPU）
let headRate;         // 標頭掃光目前的倍速（monitorrules.easeRate）
let onCloseFn = null;
/** 最後一則 deviceStats（整理過）與收到的時間 */
let entries = [];
/** 最後一則 deviceStats 的 hub（MyriagentHub 主機；沒有是 null） */
let hub = null;
let receivedAt = 0;
const history = new Map();
/** 鍵（'self' 或 deviceId）→ 卡片 */
const cards = new Map();
let shown = false;
/** 上一次送出的 monitor.on（null＝這次 init 之後還沒送過） */
let sentOn = null;
let uid = 0;

function svgEl(tag, attrs, ...kids) {
  const e = document.createElementNS(NS, tag);
  if (attrs) for (const k in attrs) e.setAttribute(k, attrs[k]);
  for (const k of kids) if (k) e.append(k);
  return e;
}
const setText = (el, t) => { if (el.textContent !== t) el.textContent = t; };
const setData = (el, k, v) => { if (el.dataset[k] !== v) el.dataset[k] = v; };
const setAttr = (el, k, v) => { if (el.getAttribute(k) !== v) el.setAttribute(k, v); };
const setStyle = (el, k, v) => { if (el.style.getPropertyValue(k) !== v) el.style.setProperty(k, v); };
/** 分格長條的填色（monitor.css 的 .mon-seg-fill）：以 transform 從左邊縮放到百分比，不改 width（不重排版面，轉場交給合成器） */
const setSegFill = (el, pct) => setStyle(el, 'transform', 'scaleX(' + Math.round(Math.min(100, Math.max(0, pct)) * 10) / 1000 + ')');

/**
 * 建面板（插在 anchor 之後，一開始是 hidden；顯示與否由 app.js 的 applyChrome 決定）。onClose：標題列的 ×
 */
export function initMonitor(anchor, onClose) {
  onCloseFn = onClose;
  const close = h('button', { type: 'button', class: 'ibtn mon-close', title: '隱藏電腦狀態（Ctrl+Shift+M）', 'aria-label': '隱藏電腦狀態' }, icon('x'));
  close.addEventListener('click', () => { if (onCloseFn) onCloseFn(); });
  countEl = h('span', { class: 'mon-count', 'aria-live': 'polite' });
  const head = headEl = h('div', { class: 'mon-head' },
    h('span', { class: 'mon-head-mark', 'aria-hidden': 'true' }, icon('pulse')),
    h('span', { class: 'mon-title', text: '電腦狀態' }), countEl, h('span', { class: 'grow' }), close);
  listEl = h('div', { class: 'mon-list', role: 'list' });
  footEl = h('div', { class: 'mon-foot' });
  footEl.hidden = true;
  root = h('aside', { id: 'monitor', class: 'mon', 'aria-label': '電腦狀態' }, head, listEl, footEl);
  root.hidden = true;
  anchor.after(root);
  onMotionChange(() => { for (const c of cards.values()) resetSpin(c); });
  render(Date.now());
  return root;
}

/** 面板看不看得到（app.js 的 applyChrome：顯示中、對話檢視、不是彈出視窗）；變了就同步 monitor */
export function setShown(v) {
  const next = !!v;
  if (next === shown) return;
  shown = next;
  if (!shown) hideTip();
  syncMonitor();
  if (shown) render(Date.now());
}

/**
 * 依目前狀態送 monitor（值變了才送；force＝不管上一次送了什麼）。第一則 init 之前不送；
 * 瀏覽器版只在連線可用（online、這條連線收到過 init）時送——斷線期間的變化等下一則 init 之後的 afterInit 再送
 */
export function syncMonitor(force = false) {
  if (!state.ready) return;
  if (isHub && !(state.connection && state.connection.state === 'online' && state.hubInit)) return;
  const on = shown && !document.hidden;
  if (!force && on === sentOn) return;
  sentOn = on;
  post({ cmd: 'monitor', on });
}

/** 每則 init 之後（app.js 的 onInit 最後）：宿主忘了上一次的 monitor，照目前狀態再送一次 */
export function afterInit() {
  sentOn = null;
  syncMonitor(true);
  render(Date.now());
}

/** 宿主訊息 deviceStats（整份取代） */
export function applyStats(m) {
  receivedAt = Date.now();
  entries = R.parseDeviceStats(m && m.devices);
  hub = R.normalizeHub(m && m.hub);
  const changed = R.mergeHistory(history, hub ? entries.concat([hub]) : entries, receivedAt);
  render(receivedAt, new Set(changed));
}

/** remote 變了（init、devices、sync）：名稱、在線、這台的 id 可能都變了 */
export function remoteChanged() {
  render(Date.now());
}

/** 每秒（app.js 的 1 秒計時器，面板看得到時）：幾秒前、資料過時、量表外圈的轉動 */
export function tick(now) {
  if (!shown || !root) return;
  render(now);
  for (const c of cards.values()) spin(c);
  // 各張卡片與標頭要改的倍速先收集起來再一次改（setAnimRates）
  const rates = [];
  for (const c of cards.values()) tempo(c, rates);
  headTempo(rates);
  setAnimRates(rates);
}

// ───── 清單 ─────

/** changedKeys：這次有新樣本的電腦（只有 applyStats 給），只重畫它們的走勢圖；其他時候（每秒、remote 變了）只畫還沒畫過的 */
function render(now, changedKeys = null) {
  if (!root) return;
  const rows = R.buildRows({ remote: state.remote, entries, hub, receivedAt, now, isHub });
  const keep = new Set();
  rows.forEach((row, i) => {
    let c = cards.get(row.key);
    if (!c) { c = createCard(!!row.hub); cards.set(row.key, c); }
    keep.add(row.key);
    updateCard(c, row, now, !!(changedKeys && changedKeys.has(row.key)));
    if (listEl.children[i] !== c.el) listEl.insertBefore(c.el, listEl.children[i] || null);
  });
  for (const [key, c] of cards) {
    if (keep.has(key)) continue;
    c.el.remove();
    cards.delete(key);
  }
  const sum = R.onlineSummary(rows);
  setText(countEl, sum.text);
  let foot = '';
  if (!isHub && !(state.remote && state.remote.deviceId)) foot = '配對之後，這裡也會顯示其他電腦的狀態。';
  else if (isHub && !rows.length) foot = state.ready ? '還沒有已配對的電腦。' : '';
  setText(footEl, foot);
  setHidden(footEl, !foot);
}

// ───── 卡片 ─────

/** hubCard：MyriagentHub 主機的卡片（伺服器圖示、另一個強調色；第三格是連線數、沒有延遲走勢） */
function createCard(hubCard) {
  const c = { angleCpu: 0, angleMem: 0, hub: hubCard };
  c.led = h('span', { class: 'mon-led', 'aria-hidden': 'true' });
  c.name = h('span', { class: 'mon-name' });
  c.badge = h('span', { class: 'mon-badge', text: '這台' });
  c.meta = h('span', { class: 'mon-meta' });
  c.sub = h('div', { class: 'mon-sub' });
  c.msg = h('div', { class: 'mon-msg' });
  c.cpu = gauge('CPU', 'cpu');
  c.mem = gauge('記憶體', 'mem');
  c.lat = hubCard ? null : latencyDial();
  c.conn = hubCard ? connectionsDial() : null;
  c.net = chart('net', NET_H);
  c.rtt = hubCard ? null : chart('rtt', RTT_H);
  c.disks = h('div', { class: 'mon-disks' });
  c.diskRows = new Map();
  c.hw = h('div', { class: 'mon-hw' });
  c.hwRows = new Map();
  c.body = h('div', { class: 'mon-body' }, h('div', { class: 'mon-gauges' }, c.cpu.el, c.mem.el, (c.lat || c.conn).el), c.hw, c.net.el, c.rtt && c.rtt.el, c.disks);
  c.el = h('section', { class: 'mon-dev', role: 'listitem', dataset: { kind: hubCard ? 'hub' : 'device' } },
    h('div', { class: 'mon-dev-head' }, c.led, hubCard ? icon('server', 'mon-kind') : null, c.name, c.badge, c.meta), c.sub, c.msg, c.body);
  // 電腦狀態面板的檔案傳輸（devicefiles.js）：其他電腦的卡片按右鍵（觸控長按）開「瀏覽與下載檔案」「上傳檔案到這台」
  if (!hubCard) c.el.addEventListener('contextmenu', (e) => { if (c.row) openCardMenu(e, c.row); });
  return c;
}

const STATE_TEXT = {
  waiting: '正在讀取這台電腦的狀態…',
  nodata: '沒有狀態資料（版本較舊或剛連上）',
  offline: '離線',
};

function updateCard(c, row, now, redrawCharts) {
  c.row = row;
  setData(c.el, 'state', row.state);
  setData(c.el, 'self', String(!!row.self));
  // MyriagentHub 主機：名稱是「Hub 主機」（伺服器圖示），伺服器的電腦名稱在副標
  setText(c.name, row.name);
  c.name.title = row.hub && row.machine ? row.name + ' · ' + row.machine : row.name;
  setHidden(c.badge, !row.self);
  const s = row.stats;
  const hasBody = !!s && (row.state === 'live' || row.state === 'stale');
  setHidden(c.body, !hasBody);

  // 右上角：開機時間（有資料時）、離線、在線（沒資料）
  let meta = '';
  if (hasBody) { const up = R.fmtUptime(s.uptimeSec); meta = up ? '開機 ' + up : ''; }
  else if (row.state === 'offline') meta = row.lastSeenAt ? R.fmtAgo(Math.max(0, now - row.lastSeenAt)) + '上線' : '';
  else if (row.state === 'nodata') meta = '在線';
  setText(c.meta, meta);
  c.meta.title = row.state === 'offline' && row.lastSeenAt ? '最後上線：' + new Date(row.lastSeenAt).toLocaleString('zh-TW') : '';

  const sub = hasBody ? R.cpuSubtitle(s, row.hub ? row.machine : null) : '';
  setText(c.sub, sub);
  setHidden(c.sub, !sub);

  let msg = STATE_TEXT[row.state] || '';
  if (row.state === 'stale') msg = '資料過時 · 最後更新 ' + R.fmtAgo(Math.max(0, now - row.sampleAt));
  setText(c.msg, msg);
  setHidden(c.msg, !msg);
  if (!hasBody) {
    // 沒有資料的卡片：下次有資料時整個重畫（走勢圖可能是很久以前的）
    c.charted = false;
    resetSpin(c);
    return;
  }

  // CPU、記憶體、延遲
  const cpuLv = R.cpuLevel(s.cpu);
  setGauge(c.cpu, s.cpu, cpuLv, s.cores ? s.cores + ' 核心' : '', 'CPU 使用率');
  const memPct = R.pct(s.memUsed, s.memTotal);
  setGauge(c.mem, memPct, R.memLevel(memPct), R.fmtPair(s.memUsed, s.memTotal), '記憶體使用率');
  if (c.lat) setLatency(c.lat, s.rttMs, row.self && !row.paired);
  if (c.conn) setConnections(c.conn, s);

  // CPU 溫度、顯示卡、磁碟
  updateHardware(c, s);
  updateDisks(c, s.disks);

  // 走勢圖：只在這台有新樣本時重畫（同一份資料會隨別台的更新重送）
  const rec = history.get(row.key);
  const points = rec ? rec.points : [];
  if (redrawCharts || c.charted !== true) {
    drawChart(c.net, points, ['rx', 'tx'], R.MIN_RATE_SCALE, R.fmtRate, R.niceRate);
    if (c.rtt) drawChart(c.rtt, points, ['rtt'], R.MIN_RTT_SCALE, (v) => R.fmtRtt(v) + ' ms', R.niceMax);
    c.charted = true;
  }
  setText(c.net.legend.rx, R.fmtRate(s.netRx));
  setText(c.net.legend.tx, R.fmtRate(s.netTx));
  refreshHover(c.net, now);
  if (c.rtt) {
    // 從來沒有延遲（未配對的這台、一直沒連上）：不畫空的延遲走勢
    setHidden(c.rtt.el, s.rttMs === null && !points.some((p) => p.rtt !== null && p.rtt !== undefined));
    setText(c.rtt.legend.rtt, s.rttMs === null ? R.latencyLevel(null, row.self && !row.paired).text : R.fmtRtt(s.rttMs) + ' ms');
    refreshHover(c.rtt, now);
  }
}

// ───── 環形量表（CPU、記憶體） ─────

function gauge(label, kind) {
  // 外圈刻度環在自己的 svg，轉的是包住它的 HTML 元素（合成器處理、不重畫）；值的弧在另一個 svg。
  // 不直接轉 svg：svg 元素的 transform 轉場不能交給合成器（Chrome：SVG 目標有獨立的 transform 屬性），每秒一次、1 秒線性的轉場等於一直在轉，
  // 主執行緒每一幀都要重算樣式（2026-10-24 量到面板開著時每秒約 11 幀都是它）
  const spinEl = h('span', { class: 'mon-g-spin', 'aria-hidden': 'true' },
    svgEl('svg', { viewBox: '0 0 72 72', focusable: 'false' },
      svgEl('circle', { cx: 36, cy: 36, r: 33.5, class: 'mon-g-ticks' }),
      svgEl('circle', { cx: 36, cy: 36, r: 33.5, class: 'mon-g-comet', pathLength: 100 })));
  const arc = svgEl('circle', { cx: 36, cy: 36, r: 27, class: 'mon-g-arc', pathLength: 100, 'stroke-dasharray': '100 100', 'stroke-dashoffset': '100', transform: 'rotate(-90 36 36)' });
  const main = svgEl('svg', { viewBox: '0 0 72 72', class: 'mon-g-main', 'aria-hidden': 'true', focusable: 'false' },
    svgEl('circle', { cx: 36, cy: 36, r: 27, class: 'mon-g-track' }), arc,
    svgEl('circle', { cx: 36, cy: 36, r: 21, class: 'mon-g-inner' }));
  const num = h('span', { class: 'mon-g-num' });
  const center = h('div', { class: 'mon-g-center', 'aria-hidden': 'true' }, h('span', { class: 'mon-g-val' }, num, h('span', { class: 'mon-g-unit', text: '%' })));
  const sev = h('span', { class: 'mon-g-sev' });
  const detail = h('div', { class: 'mon-g-detail' });
  const el = h('div', { class: 'mon-g', role: 'img', dataset: { kind } },
    h('div', { class: 'mon-g-ring' }, spinEl, main, center),
    h('div', { class: 'mon-g-label' }, h('span', { text: label }), sev), detail);
  return { el, spinEl, arc, num, sev, detail, label };
}

function setGauge(g, p, lv, detail, aria) {
  setData(g.el, 'sev', lv.sev);
  const v = p === null ? 0 : Math.min(100, Math.max(0, p));
  setStyle(g.arc, 'stroke-dashoffset', String(Math.round((100 - v) * 10) / 10));
  setHidden(g.arc, p === null);
  setText(g.num, R.fmtPct(p));
  const sevText = lv.sev === 'warn' || lv.sev === 'max' ? lv.text : '';
  setText(g.sev, sevText);
  setHidden(g.sev, !sevText);
  setText(g.detail, detail || '');
  g.detail.title = detail || '';
  setAttr(g.el, 'aria-label', aria + ' ' + R.fmtPct(p) + '%' + (sevText ? '，' + sevText : '') + (detail ? '（' + detail + '）' : ''));
}

// ───── 延遲（雷達＋訊號格） ─────

function latencyDial() {
  const num = h('span', { class: 'mon-g-num' });
  const unit = h('span', { class: 'mon-g-unit', text: 'ms' });
  const radar = h('div', { class: 'mon-radar', 'aria-hidden': 'true' },
    h('span', { class: 'mon-radar-grid' }), h('span', { class: 'mon-radar-sweep' }), h('span', { class: 'mon-radar-ping' }), h('span', { class: 'mon-radar-ping late' }));
  const bars = h('span', { class: 'mon-bars', 'aria-hidden': 'true' }, h('i'), h('i'), h('i'), h('i'));
  const tier = h('span', { class: 'mon-lat-text' });
  const el = h('div', { class: 'mon-g mon-lat', role: 'img' },
    h('div', { class: 'mon-g-ring' }, radar, h('div', { class: 'mon-g-center', 'aria-hidden': 'true' }, h('span', { class: 'mon-g-val' }, num, unit))),
    h('div', { class: 'mon-g-label' }, h('span', { text: '延遲' })),
    h('div', { class: 'mon-g-detail' }, bars, tier));
  return { el, num, unit, bars, tier, radar };
}

function setLatency(d, rtt, unpaired) {
  const lv = R.latencyLevel(rtt, unpaired);
  setData(d.el, 'tier', lv.tier);
  setData(d.bars, 'bars', String(lv.bars));
  setText(d.num, R.fmtRtt(rtt));
  setHidden(d.unit, rtt === null);
  setText(d.tier, lv.text);
  setAttr(d.el, 'aria-label', '與伺服器的延遲 ' + (rtt === null ? lv.text : R.fmtRtt(rtt) + ' 毫秒，' + lv.text));
}

// ───── MyriagentHub 主機的連線數（同延遲的雷達，數字是在線的電腦數） ─────

function connectionsDial() {
  const num = h('span', { class: 'mon-g-num' });
  const radar = h('div', { class: 'mon-radar', 'aria-hidden': 'true' },
    h('span', { class: 'mon-radar-grid' }), h('span', { class: 'mon-radar-sweep' }), h('span', { class: 'mon-radar-ping' }), h('span', { class: 'mon-radar-ping late' }));
  const browsers = h('span', { class: 'mon-lat-text' });
  const el = h('div', { class: 'mon-g mon-lat mon-conn', role: 'img' },
    h('div', { class: 'mon-g-ring' }, radar, h('div', { class: 'mon-g-center', 'aria-hidden': 'true' }, h('span', { class: 'mon-g-val' }, num, h('span', { class: 'mon-g-unit', text: '台' })))),
    h('div', { class: 'mon-g-label' }, h('span', { text: '連線' })),
    h('div', { class: 'mon-g-detail' }, browsers));
  return { el, num, browsers, radar };
}

function setConnections(d, s) {
  setText(d.num, s.devices === null ? '—' : String(s.devices));
  setText(d.browsers, s.browsers === null ? '' : s.browsers + ' 個瀏覽器');
  const text = R.hubConnections(s);
  setAttr(d.el, 'aria-label', '連線：' + (text || '不明'));
  d.el.title = text ? '連線：' + text : '';
}

// ───── CPU 溫度與顯示卡（同磁碟的分段 LED 條；拿不到的不列：伺服器主機、舊版、沒有溫度來源的電腦） ─────

function hwRow(c, key) {
  let r = c.hwRows.get(key);
  if (!r) {
    r = { name: h('span', { class: 'mon-hw-name' }), fill: h('span', { class: 'mon-seg-fill' }), val: h('span', { class: 'mon-disk-pct' }), extra: h('span', { class: 'mon-disk-size' }) };
    r.el = h('div', { class: 'mon-hwrow', role: 'img', dataset: { kind: key === 'temp' ? 'temp' : 'gpu' } }, r.name, h('span', { class: 'mon-seg' }, r.fill), r.val, r.extra);
    c.hwRows.set(key, r);
  }
  return r;
}

function setHwRow(r, o) {
  setText(r.name, o.name);
  setData(r.el, 'sev', o.lv.sev);
  setSegFill(r.fill, o.pct ?? 0);
  setText(r.val, o.val);
  setText(r.extra, o.extra);
  setAttr(r.el, 'aria-label', o.label);
  r.el.title = o.label;
}

function updateHardware(c, s) {
  const keys = [];
  if (s.cpuTemp !== null) {
    keys.push('temp');
    const lv = R.tempLevel(s.cpuTemp);
    const src = R.tempSource(s.cpuTempSource);
    const note = lv.sev === 'warn' || lv.sev === 'max' ? '，' + lv.text : '';
    setHwRow(hwRow(c, 'temp'), { name: '溫度', lv, pct: R.tempPct(s.cpuTemp), val: R.fmtTemp(s.cpuTemp), extra: src ? src.short : '',
      label: 'CPU 溫度 ' + R.fmtTemp(s.cpuTemp) + note + (src ? '（來源：' + src.long + '）' : '') });
  }
  const labels = R.gpuLabels(s.gpus);
  s.gpus.forEach((g, i) => {
    const key = 'gpu' + i;
    keys.push(key);
    const lv = R.gpuLevel(g.util);
    const mem = g.memTotal !== null ? R.fmtPair(g.memUsed, g.memTotal) : '';
    const note = lv.sev === 'warn' || lv.sev === 'max' ? '，' + lv.text : '';
    setHwRow(hwRow(c, key), { name: labels[i], lv, pct: g.util, val: R.fmtPct(g.util) + '%', extra: mem,
      label: g.name + ' 使用率 ' + R.fmtPct(g.util) + '%' + note + (mem ? '（專用記憶體 ' + mem + '）' : '') });
  });
  keys.forEach((key, i) => {
    const el = c.hwRows.get(key).el;
    if (c.hw.children[i] !== el) c.hw.insertBefore(el, c.hw.children[i] || null);
  });
  for (const [key, r] of c.hwRows) {
    if (keys.includes(key)) continue;
    r.el.remove();
    c.hwRows.delete(key);
  }
  setHidden(c.hw, !keys.length);
}

// ───── 磁碟（分段 LED 條） ─────

function updateDisks(c, disks) {
  const seen = new Set();
  disks.forEach((d, i) => {
    seen.add(d.name);
    let r = c.diskRows.get(d.name);
    if (!r) {
      r = { fill: h('span', { class: 'mon-seg-fill' }), pct: h('span', { class: 'mon-disk-pct' }), size: h('span', { class: 'mon-disk-size' }) };
      r.el = h('div', { class: 'mon-disk', role: 'img' }, h('span', { class: 'mon-disk-name', text: d.name }), h('span', { class: 'mon-seg' }, r.fill), r.pct, r.size);
      c.diskRows.set(d.name, r);
    }
    const p = R.pct(d.used, d.total) ?? 0;
    const lv = R.diskLevel(p);
    setData(r.el, 'sev', lv.sev);
    setSegFill(r.fill, p);
    setText(r.pct, R.fmtPct(p) + '%');
    const sizeText = R.fmtPair(d.used, d.total);
    setText(r.size, sizeText);
    const note = lv.sev === 'warn' || lv.sev === 'max' ? '，' + lv.text : '';
    const label = '磁碟 ' + d.name + ' 已用 ' + R.fmtPct(p) + '%' + note + '（' + sizeText + '，剩 ' + R.fmtBytes(Math.max(0, d.total - d.used)) + '）';
    setAttr(r.el, 'aria-label', label);
    r.el.title = label;
    if (c.disks.children[i] !== r.el) c.disks.insertBefore(r.el, c.disks.children[i] || null);
  });
  for (const [name, r] of c.diskRows) {
    if (seen.has(name)) continue;
    r.el.remove();
    c.diskRows.delete(name);
  }
  setHidden(c.disks, !disks.length);
}

// ───── 走勢圖（同一個 y 軸；滑過時十字線＋說明） ─────

function chart(kind, height) {
  const id = 'mon' + (++uid);
  const series = kind === 'net' ? ['rx', 'tx'] : ['rtt'];
  const defs = svgEl('defs');
  const svg = svgEl('svg', { class: 'mon-chart-svg', viewBox: `0 0 ${CW} ${height}`, preserveAspectRatio: 'none', 'aria-hidden': 'true', focusable: 'false' }, defs,
    svgEl('line', { x1: 0, x2: CW, y1: 0.5, y2: 0.5, class: 'mon-chart-grid' }),
    svgEl('line', { x1: 0, x2: CW, y1: height / 2, y2: height / 2, class: 'mon-chart-grid mid' }),
    svgEl('line', { x1: 0, x2: CW, y1: height - 0.5, y2: height - 0.5, class: 'mon-chart-base' }));
  const paths = {};
  for (const k of series) {
    const grad = svgEl('linearGradient', { id: id + k, x1: 0, x2: 0, y1: 0, y2: 1 },
      svgEl('stop', { offset: '0', class: 'mon-stop-a' }), svgEl('stop', { offset: '1', class: 'mon-stop-b' }));
    grad.dataset.series = k;
    defs.append(grad);
    const area = svgEl('path', { class: 'mon-area', fill: `url(#${id}${k})` });
    const line = svgEl('path', { class: 'mon-line', 'vector-effect': 'non-scaling-stroke' });
    area.dataset.series = k;
    line.dataset.series = k;
    svg.append(area, line);
    paths[k] = { area, line };
  }
  const axis = h('span', { class: 'mon-axis' });
  const cross = h('span', { class: 'mon-cross', hidden: true });
  const dots = {};
  const heads = {};
  for (const k of series) {
    dots[k] = h('span', { class: 'mon-dot', dataset: { series: k }, hidden: true });
    heads[k] = h('span', { class: 'mon-headdot', dataset: { series: k }, hidden: true });
  }
  const plot = h('div', { class: 'mon-plot', style: { height: height + 'px' } }, svg, axis, cross, ...Object.values(heads), ...Object.values(dots));
  const legend = {};
  const head = h('div', { class: 'mon-chart-head' });
  if (kind === 'net') {
    legend.rx = h('span', { class: 'mon-leg-v' });
    legend.tx = h('span', { class: 'mon-leg-v' });
    head.append(h('span', { class: 'mon-chart-title', text: '網路' }),
      h('span', { class: 'mon-leg', dataset: { series: 'rx' } }, h('span', { class: 'mon-sw', 'aria-hidden': 'true' }), h('span', { class: 'mon-leg-k', text: '↓ 下載' }), legend.rx),
      h('span', { class: 'mon-leg', dataset: { series: 'tx' } }, h('span', { class: 'mon-sw', 'aria-hidden': 'true' }), h('span', { class: 'mon-leg-k', text: '↑ 上傳' }), legend.tx));
  } else {
    legend.rtt = h('span', { class: 'mon-leg-v' });
    head.append(h('span', { class: 'mon-chart-title', text: '延遲走勢' }), h('span', { class: 'grow' }), legend.rtt);
  }
  const el = h('div', { class: 'mon-chart', dataset: { kind } }, head, plot);
  const ch = { el, plot, paths, axis, cross, dots, heads, legend, kind, series, height, points: [], max: 1, fmt: String, hover: null };
  plot.addEventListener('pointermove', (e) => { ch.hover = { x: e.clientX }; refreshHover(ch, Date.now()); });
  plot.addEventListener('pointerleave', () => { ch.hover = null; refreshHover(ch, Date.now()); });
  return ch;
}

function drawChart(ch, points, keys, floor, fmt, nice) {
  const win = R.windowPoints(points);
  ch.points = win;
  ch.fmt = fmt;
  ch.max = R.scaleFor(win, keys, floor, nice);
  setText(ch.axis, fmt(ch.max));
  const last = win.length ? win[win.length - 1] : null;
  for (const k of keys) {
    const p = R.sparkPaths(win, k, CW, ch.height, ch.max);
    setAttr(ch.paths[k].line, 'd', p.line);
    setAttr(ch.paths[k].area, 'd', p.area);
    // 最新一點的光點（右緣）
    const v = last ? last[k] : null;
    const head = ch.heads[k];
    setHidden(head, v === null || v === undefined);
    if (v !== null && v !== undefined) setStyle(head, 'top', yPct(v, ch.max) + '%');
  }
}

const yPct = (v, max) => Math.round((1 - Math.min(1, Math.max(0, v / max))) * 1000) / 10;

/** 滑過的十字線與說明：資料更新時照同一個滑鼠位置重算（說明不閃、跟著新資料走） */
function refreshHover(ch, now) {
  const hp = ch.hover && ch.points.length ? hoverAt(ch) : null;
  if (!hp) {
    setHidden(ch.cross, true);
    for (const k of ch.series) setHidden(ch.dots[k], true);
    if (ch.tipShown) { ch.tipShown = false; hideTip(); }
    return;
  }
  const rect = ch.plot.getBoundingClientRect();
  const leftPct = Math.round(hp.frac * 1000) / 10 + '%';
  setHidden(ch.cross, false);
  setStyle(ch.cross, 'left', leftPct);
  const rows = [];
  for (const k of ch.series) {
    const v = hp.point[k];
    const dot = ch.dots[k];
    setHidden(dot, v === null || v === undefined);
    if (v !== null && v !== undefined) { setStyle(dot, 'left', leftPct); setStyle(dot, 'top', yPct(v, ch.max) + '%'); }
    rows.push(h('div', { class: 'mon-tip-row', dataset: { series: k } }, h('span', { class: 'mon-sw', 'aria-hidden': 'true' }),
      h('span', { text: k === 'rx' ? '↓ 下載' : k === 'tx' ? '↑ 上傳' : '延遲' }), h('b', { text: v === null || v === undefined ? '—' : ch.fmt(v) })));
  }
  const tip = h('div', { class: 'mon-tip' }, h('div', { class: 'mon-tip-t', text: R.fmtAgo(Math.max(0, now - hp.point.t)) }), rows);
  showTip(tip, rect.left + hp.frac * rect.width, rect.top);
  ch.tipShown = true;
}

function hoverAt(ch) {
  const rect = ch.plot.getBoundingClientRect();
  if (!rect.width) return null;
  return R.hoverPoint(ch.points, (ch.hover.x - rect.left) / rect.width);
}

// ───── 外圈刻度環的轉動（每秒一次，transition 1 秒線性；CPU 越高轉越快） ─────

function spin(c) {
  const row = c.row;
  if (calmMotion() || !row || row.state !== 'live' || !row.stats) return;
  c.angleCpu += R.spinRate(row.stats.cpu);
  c.angleMem += R.spinRate(R.pct(row.stats.memUsed, row.stats.memTotal)) * 0.5;
  setStyle(c.cpu.spinEl, 'transform', 'rotate(' + Math.round(c.angleCpu * 10) / 10 + 'deg)');
  setStyle(c.mem.spinEl, 'transform', 'rotate(' + Math.round(c.angleMem * 10) / 10 + 'deg)');
}

// ───── 動畫的轉速跟著負載（monitorrules 的 loadRate／tempRate／netRate／easeRate；2026-10-25 使用者要求） ─────

/**
 * 每個 [el, rate]：el（含它的偽元素與子孫）上正在跑的 CSS 動畫改成 rate 倍速。updatePlaybackRate 保留目前的位置、只改之後的速度，轉速改變時畫面連續；
 * 改 animation-duration 則會讓位置瞬間跳到別處。新開始的動畫（狀態切換、視窗重新取得焦點）是 1 倍，下一秒就套上。
 * 先把每個元素上的動畫都讀出來、再一起改：getAnimations 要最新的樣式，以前讀一個改一個，改了倍速樣式又變髒，下一個 getAnimations 就逼瀏覽器
 * 當場再重算一次（整頁正在跑的動畫跟著算），每秒十幾次（2026-10-26 失效追蹤：四窗格兩個執行中，樣式重算每秒 15 → 31 次）；先讀完只算一次
 */
function setAnimRates(pairs) {
  const batch = [];
  for (const [el, rate] of pairs) {
    if (!el || !el.isConnected || !Number.isFinite(rate)) continue;
    batch.push([el.getAnimations({ subtree: true }), rate]);
  }
  for (const [anims, rate] of batch) {
    for (const a of anims) {
      const cur = a._monRate ?? a.playbackRate;
      if (Math.abs(cur - rate) < 0.005) continue;
      a._monRate = rate;
      if (typeof a.updatePlaybackRate === 'function') a.updatePlaybackRate(rate);
      else a.playbackRate = rate;
    }
  }
}

/**
 * 每秒：各個持續動畫的倍速往負載對應的目標走一段再套上——延遲（或連線）的雷達跟著網路總流量、網路走勢圖端點的擴散跟著各自的上傳下載量、
 * 顯示卡長條的掃光跟著使用率、溫度長條跟著溫度。量表外圈的轉動（spin）本來就依 CPU、記憶體逐秒累加角度
 */
function tempo(c, out) {
  const row = c.row;
  if (calmMotion() || !row || row.state !== 'live' || !row.stats) return;
  const s = row.stats;
  const rates = c.rates || (c.rates = {});
  const step = (key, target) => (rates[key] = R.easeRate(rates[key], target));
  const dial = c.lat || c.conn;
  const total = s.netRx === null && s.netTx === null ? null : (s.netRx || 0) + (s.netTx || 0);
  if (dial) out.push([dial.radar, step('radar', R.netRate(total))]);
  for (const k of ['rx', 'tx']) if (c.net.heads[k]) out.push([c.net.heads[k], step(k, R.netRate(s['net' + (k === 'rx' ? 'Rx' : 'Tx')]))]);
  for (const [key, r] of c.hwRows) {
    const gpu = key.startsWith('gpu') ? s.gpus[Number(key.slice(3))] : null;
    out.push([r.fill, step('hw:' + key, key === 'temp' ? R.tempRate(s.cpuTemp) : R.loadRate(gpu ? gpu.util : null))]);
  }
}

/** 面板標頭的掃光：跟著在線的電腦裡最忙的那台的 CPU */
function headTempo(out) {
  if (calmMotion() || !headEl) return;
  let max = null;
  for (const c of cards.values()) {
    const s = c.row && c.row.state === 'live' ? c.row.stats : null;
    if (s && Number.isFinite(s.cpu) && (max === null || s.cpu > max)) max = s.cpu;
  }
  headRate = R.easeRate(headRate, R.loadRate(max));
  out.push([headEl, headRate]);
}

/** 停在目前的角度（資料過時、離線、要求減少動態或關掉動態效果時不轉） */
function resetSpin(c) {
  if (!calmMotion()) return;
  c.cpu.spinEl.style.removeProperty('transform');
  c.mem.spinEl.style.removeProperty('transform');
}
