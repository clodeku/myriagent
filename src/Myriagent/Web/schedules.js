// 排程頁（和對話、儀表板同位階的整頁檢視；設計 docs/plans/2026-10-26-schedule-manager.md）：這台的 Windows 排程，
// 代理建立、疑似代理建立的列出來（系統與廠商的要點了篩選才看）。資料是宿主的 schedules（web-protocol 的 ScheduleView，AgentHub.Schedules.cs），
// 顯示中送 scheduleWatch on（宿主每 30 秒重讀），離開送 off。立即執行、停止、啟用、停用直接做（scheduleAction）；
// 刪除、改時間、改內容一律「交給代理」：管理對話（沒有就開草稿）的輸入框預先填好說明，不自動送出（workspace.openScheduleAgent）。
// 只有桌面（瀏覽器版還沒有：S3）。純規則在 schedulerules.js
import { h, iconButton, fmtRelative, fmtDateTime } from './util.js';
import { tokenCounter } from './tokenfx.js';
import { icon, providerMark } from './icons.js';
import { post, state, providerInfo, isHub, emit } from './store.js';
import { openMenu } from './ui.js';
import { fixCandidates } from './healthrules.js';
import {
  ORIGIN_LABEL, ORIGIN_ORDER, DEFAULT_ORIGINS, STATE_FILTERS, AGENT_TASKS, entriesOf, filterEntries, sortEntries, originCounts, summaryOf,
  historyBars, timelineOf, timelineTicks, lastText, ledOf, primaryTask, provenanceText, providerKeyOf, pickSelection, quickActions, listActions, isManaged,
  timedOf, timedAsEntry, timedStateText, WHEN_KINDS, whenOf, localConversations,
  hourTicks, isSoon, soonestPoint, soonText, freshRuns,
} from './schedulerules.js';
import { openDialog, toast } from './ui.js';
import { deviceOf } from './liverules.js';
import { track } from './cmdtrack.js';
import { devicesOf, keyOf } from './schedulerules.js';

const DAY = 24 * 3600000;

let root = null;
let api = null;
let dirty = true;
let shown = false;
let selected = null;
let xmlOpen = false;
const filter = { origins: DEFAULT_ORIGINS.slice(), state: 'all', device: 'all' };
// 第九批視覺效果（2026-10-26）：每次重畫整頁重建，所以「哪個排程有了新的一次結果」要和上一次比（schedulerules.freshRuns），
// 摘要的數字用跨重畫保留的計數器（數字變了逐字解碼，tokenfx.js）
let runSeen = null;
let fresh = {};
const pillCounters = {};
let pendingCounts = [];

export function initSchedules(el, apis) {
  root = el;
  api = apis;
}

/** 有排程頁可用（宿主掛了讀取器；瀏覽器版還沒有） */
export function available() {
  return !!state.schedules;
}

export function markDirty() { dirty = true; }

/** schedules 訊息到了 */
export function changed() {
  dirty = true;
  if (shown) render();
}

/** 切到排程頁：宿主開始每 30 秒重讀（太久沒讀就馬上讀） */
export function onShown() {
  shown = true;
  if (!isHub) post({ cmd: 'scheduleWatch', on: true });
  render(true);
}

/** 離開排程頁 */
export function onHidden() {
  if (!shown) return;
  shown = false;
  if (!isHub) post({ cmd: 'scheduleWatch', on: false });
}

/** 每分鐘：相對時間與時間軸的「現在」往前走 */
export function tickMinute() {
  if (shown) render(true);
}

export function render(force) {
  if (!root || (!dirty && !force)) return;
  dirty = false;
  const s = state.schedules;
  const sideScroll = root.querySelector('.sch-side')?.scrollTop || 0;
  const listScroll = root.querySelector('.sch-main')?.scrollTop || 0;
  root.replaceChildren();
  if (!s) return;
  const now = Date.now();
  const localShown = !isHub && (filter.device === 'all' || filter.device === 'local');
  const all = entriesOf(s);
  const visible = localShown ? sortEntries(filterEntries(all, filter)) : [];
  const timed = timedOf(s);
  const timedShown = localShown && filter.origins.includes('timed') ? timed : [];
  // 其他電腦（S3）：每台最後回報的摘要，選的電腦篩選
  const remotes = devicesOf(s).map((d) => {
    const snap = d.snapshot || {};
    const info = deviceOf(d.deviceId, state.remote, null);
    return { d, info, all: entriesOf(snap), timed: timedOf(snap) };
  });
  const remotesShown = remotes.filter((r) => filter.device === 'all' || filter.device === r.d.deviceId).map((r) => Object.assign({}, r, {
    visible: sortEntries(filterEntries(r.all, filter)),
    timedShown: filter.origins.includes('timed') ? r.timed : [],
  }));
  const keyed = visible.map((e) => ({ path: keyOf(e.path, null), last: e.last }))
    .concat(timedShown.map((t) => ({ path: keyOf(t.id, null), last: t.last })))
    .concat(...remotesShown.map((r) => r.visible.map((e) => ({ path: keyOf(e.path, r.d.deviceId), last: e.last }))
      .concat(r.timedShown.map((t) => ({ path: keyOf(t.id, r.d.deviceId), last: t.last })))));
  selected = pickSelection(keyed, selected);
  const everyEntry = all.concat(...remotes.map((r) => r.all));
  const timelineEntries = visible.map((e) => Object.assign({}, e, { path: keyOf(e.path, null) }))
    .concat(timedShown.map((t) => Object.assign(timedAsEntry(t), { path: keyOf(t.id, null) })))
    .concat(...remotesShown.map((r) => r.visible.map((e) => Object.assign({}, e, { path: keyOf(e.path, r.d.deviceId), name: e.name + '（' + r.info.name + '）' }))
      .concat(r.timedShown.map((t) => Object.assign(timedAsEntry(t), { path: keyOf(t.id, r.d.deviceId) })))));
  // 新的一次執行結果：這一列閃一下（成功綠、失敗紅），最近 14 次的長條最右邊那格長出來（只在比上一次多了一次時）
  const runItems = all.map((e) => ({ key: keyOf(e.path, null), e })).concat(timed.map((t) => ({ key: keyOf(t.id, null), e: timedAsEntry(t) })))
    .concat(...remotes.map((r) => r.all.map((e) => ({ key: keyOf(e.path, r.d.deviceId), e }))
      .concat(r.timed.map((t) => ({ key: keyOf(t.id, r.d.deviceId), e: timedAsEntry(t) })))));
  const fr = freshRuns(runSeen, runItems);
  runSeen = fr.seen;
  fresh = fr.fresh;
  const loaded = isHub || s.loaded;
  const main = h('div', { class: 'sch-main' },
    headerOf(s, now),
    pillsOf(everyEntry, now),
    filtersOf(everyEntry, timed.length + remotes.reduce((n, r) => n + r.timed.length, 0), remotes),
    localShown && s.error && !all.length ? h('div', { class: 'sch-empty err' }, icon('error'), h('span', { text: '讀不到工作排程器：' + s.error })) : null,
    !loaded ? h('div', { class: 'sch-empty' }, h('span', { text: '正在讀取這台電腦的排程…' })) : null,
    loaded ? timelineOf2(timelineEntries, now) : null,
    localShown && s.loaded ? groupOf(s, visible, now) : null,
    timedShown.length || (localShown && filter.origins.includes('timed')) ? timedGroupOf(timedShown, now, null) : null,
    remotesShown.map((r) => remoteGroupOf(r, now)),
    isHub && !remotes.length ? h('div', { class: 'sch-empty', text: '還沒有電腦回報排程：桌面的 Clode Myriagent 更新到新版、連上伺服器之後就會出現' }) : null);
  const sel = all.find((e) => keyOf(e.path, null) === selected) || null;
  const selTimed = timed.find((t) => keyOf(t.id, null) === selected) || null;
  let detail = sel ? detailOf(sel, s, now, null) : selTimed ? timedDetailOf(selTimed, now, null) : null;
  for (const r of remotes) {
    if (detail) break;
    const re = r.all.find((e) => keyOf(e.path, r.d.deviceId) === selected);
    const rt = r.timed.find((t) => keyOf(t.id, r.d.deviceId) === selected);
    if (re) detail = detailOf(re, Object.assign({}, r.d.snapshot, { machine: r.info.name }), now, r.d.deviceId);
    else if (rt) detail = timedDetailOf(rt, now, r.d.deviceId);
  }
  const side = h('aside', { class: 'sch-side', 'aria-label': '排程明細' }, detail || h('div', { class: 'sch-empty', text: '選一個排程看明細' }));
  root.append(main, side);
  side.scrollTop = sideScroll;
  main.scrollTop = listScroll;
  // 放進畫面之後才換值：計數器在畫面上才解碼（第一次直接顯示）
  for (const [c, n] of pendingCounts) c.set(n);
  pendingCounts = [];
}

/** 一列的第九批標示：新的一次結果閃一下、長條最右邊那格長出來；下次執行在一小時內標琥珀 */
function decorateRow(row, key, nextAt, now) {
  const lv = fresh[key];
  if (lv) {
    row.classList.add(lv === 'fail' ? 'fx-sch-fail' : 'fx-sch-ok');
    const last = row.querySelector('.sch-hist > i:last-child');
    if (last) last.classList.add('fx-new');
  }
  if (isSoon(nextAt, now)) row.querySelector('.sch-next')?.classList.add('soon');
  return row;
}

// ───── 頁首、摘要、篩選 ─────
function headerOf(s, now) {
  const refresh = h('button', { type: 'button', class: 'btn sm', disabled: !!s.scanning, title: '重新讀取這台電腦的排程' }, icon('refresh'), h('span', { text: s.scanning ? '讀取中…' : '重新掃描' }));
  refresh.addEventListener('click', () => post({ cmd: 'scheduleRefresh' }));
  const create = h('button', { type: 'button', class: 'btn sm acc', title: '開一個對話請代理建立排程（先在輸入框寫要做什麼）' }, icon('plus'), h('span', { text: '新增排程（交給代理）' }));
  create.addEventListener('click', () => chooseAccount(create, null, 'create'));
  const timedBtn = h('button', { type: 'button', class: 'btn sm', title: '在指定的時間對某個對話送一則訊息（app 開著時才送）' }, icon('clock'), h('span', { text: '新增定時訊息' }));
  timedBtn.addEventListener('click', () => openTimedDialog(null));
  return h('div', { class: 'sch-head' },
    h('h1', { text: '排程' }),
    h('span', { class: 'sch-sub', text: isHub ? '各台電腦最後回報的排程' : (s.machine || '這台') + (s.scannedAt ? '・' + fmtRelative(s.scannedAt, now) + '讀取' : '') }),
    h('div', { class: 'grow' }),
    isHub ? null : refresh, isHub ? null : timedBtn, isHub ? null : create);
}

function pillsOf(all, now) {
  const sum = summaryOf(all, now);
  // 數字是跨重畫保留的計數器：變了先閃過亂碼再逐位鎖定（同儀表板摘要）
  const count = (key, n) => {
    const c = pillCounters[key] || (pillCounters[key] = tokenCounter((v) => String(v)));
    pendingCounts.push([c, n]);
    return h('b', null, c.num);
  };
  const pill = (n, label, cls, key) => h('span', { class: 'sch-pill' + (cls && n ? ' ' + cls : '') }, count(key, n), h('span', { text: label }));
  return h('div', { class: 'sch-pills' },
    pill(sum.count, '個列出的排程', null, 'count'),
    pill(sum.running, '執行中', 'run', 'running'),
    pill(sum.failing, '最近一次失敗', 'fail', 'failing'),
    pill(sum.missed, '錯過執行', 'warn', 'missed'),
    h('span', { class: 'sch-pill' }, h('span', { text: '接下來 1 小時' }), count('nextHour', sum.nextHour), h('span', { text: '次' })));
}

function filtersOf(all, timedCount, remotes) {
  const counts = Object.assign(originCounts(all), { timed: timedCount });
  const chips = ORIGIN_ORDER.map((k) => {
    const on = filter.origins.includes(k);
    const b = h('button', { type: 'button', class: 'sch-chip' + (on ? ' on' : ''), 'aria-pressed': String(on) }, h('span', { text: ORIGIN_LABEL[k] }), h('span', { class: 'n', text: String(counts[k] || 0) }));
    b.addEventListener('click', () => {
      filter.origins = on ? filter.origins.filter((x) => x !== k) : filter.origins.concat(k);
      render(true);
    });
    return b;
  });
  const states = STATE_FILTERS.map((f) => {
    const on = filter.state === f.key;
    const b = h('button', { type: 'button', class: 'sch-chip' + (on ? ' on' : ''), 'aria-pressed': String(on), text: f.label });
    b.addEventListener('click', () => { filter.state = f.key; render(true); });
    return b;
  });
  // 電腦：全部、這台（桌面）、各台（S3）
  const devs = [{ key: 'all', label: '全部' }].concat(isHub ? [] : [{ key: 'local', label: '這台' }])
    .concat((remotes || []).map((r) => ({ key: r.d.deviceId, label: r.info.name + (r.info.online === false ? '・離線' : ''), off: r.info.online === false })));
  const devChips = devs.length > 2 || (isHub && devs.length > 1) ? devs.map((d) => {
    const on = filter.device === d.key;
    const b = h('button', { type: 'button', class: 'sch-chip' + (on ? ' on' : '') + (d.off ? ' dim' : ''), 'aria-pressed': String(on), text: d.label });
    b.addEventListener('click', () => { filter.device = d.key; render(true); });
    return b;
  }) : null;
  return h('div', { class: 'sch-filters' },
    devChips ? h('div', { class: 'sch-fgroup' }, h('span', { class: 'sch-flabel', text: '電腦' }), devChips) : null,
    h('div', { class: 'sch-fgroup' }, h('span', { class: 'sch-flabel', text: '來源' }), chips),
    h('div', { class: 'sch-fgroup' }, h('span', { class: 'sch-flabel', text: '狀態' }), states));
}

// ───── 接下來 24 小時 ─────
function timelineOf2(visible, now) {
  const points = timelineOf(visible, now, DAY);
  // 一小時內最近的那次：點標琥珀、旁邊寫「12 分後」（每分鐘重畫一次，沒有動畫）
  const soon = soonestPoint(points, now);
  const track = h('div', { class: 'sch-track' });
  for (const p of points) {
    const e = visible.find((x) => x.path === p.path);
    const title = (e ? e.name : p.path) + (p.band ? `：接下來 24 小時 ${p.count} 次` : '：' + fmtDateTime(p.at));
    if (p.band) {
      track.append(h('i', { class: 'band led-' + p.led, title, style: { left: (p.x * 100).toFixed(2) + '%', width: Math.max(0.5, (p.x1 - p.x) * 100).toFixed(2) + '%' } }));
    } else {
      const isSoonDot = soon && p === soon;
      const dot = h('i', { class: 'dot led-' + p.led + (p.path === selected ? ' sel' : '') + (isSoonDot ? ' soon' : ''), title, style: { left: (p.x * 100).toFixed(2) + '%' } });
      dot.addEventListener('click', () => { selected = p.path; render(true); });
      track.append(dot);
    }
  }
  if (soon) track.append(h('span', { class: 'sch-soon-tag', text: soonText(soon.at, now), style: { left: (soon.x * 100).toFixed(2) + '%' } }));
  // 刻度尺：每小時一格細刻度、0／6／12／18 點粗刻度（對齊整點）
  const hours = h('div', { class: 'sch-hours', 'aria-hidden': 'true' },
    hourTicks(now, DAY).map((t) => h('i', { class: t.major ? 'major' : '', style: { left: (t.x * 100).toFixed(2) + '%' } })));
  const d = new Date(now);
  const nowText = String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
  const ticks = timelineTicks(now, DAY).map((t) => h('span', { class: 'tick', text: t.label, style: { left: (t.x * 100).toFixed(2) + '%' } }));
  return h('section', { class: 'sch-tl', 'aria-label': '接下來 24 小時' },
    h('div', { class: 'sch-tl-cap' }, h('span', null, h('span', { text: '接下來 24 小時' }), h('span', { class: 'sch-tl-now', text: '・現在 ' + nowText })),
      h('span', { class: 'sch-tl-legend', text: points.length ? '點一下看那個排程' : '列出的排程接下來 24 小時沒有預定的執行' })),
    h('div', { class: 'sch-tl-body' }, hours, h('span', { class: 'now', title: '現在 ' + nowText }), track),
    h('div', { class: 'sch-tl-axis' }, ticks));
}

// ───── 清單 ─────
function groupOf(s, visible, now) {
  const rows = visible.map((e) => rowOf(e, now));
  return h('section', { class: 'sch-group' },
    h('div', { class: 'sch-gh' }, h('span', { class: 'gdot' }), h('b', { text: s.machine || '這台' }), h('span', { class: 'sch-sub', text: `這台・${visible.length} 個` })),
    rows.length
      ? h('div', { class: 'sch-table', role: 'table' },
        h('div', { class: 'sch-row sch-th', role: 'row' }, ['', '名稱', '何時', '上次', '最近 14 次', '下次', '誰建立的', ''].map((t) => h('span', { role: 'columnheader', text: t }))),
        rows)
      : h('div', { class: 'sch-empty', text: '沒有符合篩選的排程。代理在對話裡建立的 Windows 排程會自動出現在這裡' }));
}

/** 快速操作：這台的直接送，其他電腦的帶 deviceId（遠端指令，結果由 cmdtrack 追；被拒時伺服器或傳輸層已經提示） */
function scheduleAction(e, dev, action) {
  if (!dev) { post({ cmd: 'scheduleAction', path: e.path, action }); return; }
  const id = post({ cmd: 'scheduleAction', deviceId: dev, path: e.path, action });
  track(id, { onSettle: (st) => { if (st === 'done') toast(`已在「${deviceOf(dev, state.remote, null).name}」${{ run: '開始執行', stop: '停止', enable: '啟用', disable: '停用' }[action] || '處理'} ${e.name}`, 'success'); } });
}

/** 其他電腦的一組：名稱、在不在線、最後回報的時間；排程與定時訊息各一張表（定時訊息只看，不在遠端改） */
function remoteGroupOf(r, now) {
  const snap = r.d.snapshot || {};
  const rows = r.visible.map((e) => rowOf(e, now, r.d.deviceId));
  const offline = r.info.online === false;
  const sub = `${r.visible.length} 個・${fmtRelative(r.d.updatedAt, now)}回報` + (offline ? '・離線（顯示最後回報的）' : '') + (snap.otherCount ? `・系統與廠商的 ${snap.otherCount} 個不列` : '');
  return h('section', { class: 'sch-group' + (offline ? ' offline' : '') },
    h('div', { class: 'sch-gh' }, h('span', { class: 'gdot' + (offline ? ' off' : '') }), h('b', { text: r.info.name }), h('span', { class: 'sch-sub', text: sub })),
    rows.length
      ? h('div', { class: 'sch-table', role: 'table' }, rows)
      : h('div', { class: 'sch-empty', text: snap.error ? '那台讀不到工作排程器：' + snap.error : '沒有符合篩選的排程' }),
    r.timedShown.length ? timedGroupOf(r.timedShown, now, r.d.deviceId) : null);
}

/** 其他電腦的交給代理：選要做的事、補充說明，送到那台的管理對話（遠端看不到那台的草稿，所以直接送出） */
function remoteAgent(anchor, e, dev) {
  const name = deviceOf(dev, state.remote, null).name;
  openMenu(anchor, [{ type: 'header', label: `交給「${name}」的代理` }].concat(AGENT_TASKS.map((t) => ({
    label: t.label, icon: t.icon, onSelect: () => remoteAgentDialog(e, dev, t, name),
  }))), { placement: 'bottom-end', minWidth: 240, force: true });
}

function remoteAgentDialog(e, dev, t, name) {
  const note = h('textarea', { class: 'input sch-ta', rows: 4, placeholder: t.task === 'retime' ? '新的時間，例如：每天 03:00' : t.task === 'custom' ? '要代理做什麼' : '補充說明（選填）' });
  const send = h('button', { type: 'button', class: 'btn acc' }, h('span', { text: '送出' }));
  const cancel = h('button', { type: 'button', class: 'btn' }, h('span', { text: '取消' }));
  const d = openDialog({
    title: `交給「${name}」的代理：${t.label}`, width: 520, className: 'dlg-timed',
    body: h('div', { class: 'sch-form' },
      h('div', { class: 'sch-sub', text: `排程 ${e.path}。會在那台這個排程的管理對話（沒有就開一個）送出說明，代理照那台的權限模式做事；要系統管理員的操作仍要在那台按同意。` }),
      h('label', { class: 'sch-field' }, h('span', { class: 'k', text: '補充' }), note)),
    footer: [cancel, send],
  });
  cancel.addEventListener('click', () => d.close());
  send.addEventListener('click', () => {
    const id = post({ cmd: 'scheduleAgent', deviceId: dev, path: e.path, task: t.task, note: note.value.trim() || null, accountId: null });
    track(id, { onSettle: (st) => { if (st === 'done') toast(`已交給「${name}」的代理，管理對話在那台`, 'success'); } });
    d.close();
  });
}

function rowOf(e, now, dev) {
  const led = ledOf(e);
  const bars = historyBars(e);
  const prov = e.provenance;
  const pk = prov ? providerKeyOf(prov.provider) : null;
  const acts = quickActions(e);
  const quick = acts.length ? iconButton(acts[0].icon, acts[0].label, (ev) => { ev.stopPropagation(); scheduleAction(e, dev, acts[0].action); }, 'sch-q') : null;
  if (quick && e.busy) quick.disabled = true;
  const agent = h('button', { type: 'button', class: 'btn sm sch-agent', title: '交給代理' }, icon('sparkle'), h('span', { text: '交給代理' }), icon('chevDown'));
  agent.addEventListener('click', (ev) => { ev.stopPropagation(); if (dev) remoteAgent(agent, e, dev); else agentMenu(agent, e); });
  const key = keyOf(e.path, dev);
  const row = h('div', { class: 'sch-row' + (key === selected ? ' sel' : '') + (e.enabled === false ? ' off' : ''), role: 'row', tabIndex: 0, dataset: { path: key, led } },
    h('span', { class: 'led led-' + led, title: ledTitle(led) }),
    h('span', { class: 'sch-name' }, h('b', { text: e.name, title: e.path }), e.workFolder ? h('span', { class: 'mono sch-path', text: e.workFolder }) : null),
    h('span', { class: 'sch-when', text: (e.trigger || '') + (e.enabled === false ? '・已停用' : '') }),
    h('span', { class: 'sch-last' }, h('span', { class: 'res lvl-' + (e.last ? e.last.level : 'info'), text: lastText(e) }), e.lastRun ? h('span', { class: 'mono sch-at', text: fmtRelative(e.lastRun, now) }) : null),
    h('span', { class: 'sch-hist', title: bars.length ? '' : '還沒有觀察到的執行' }, bars.map((b) => h('i', { class: 'lvl-' + b.level + (b.gap ? ' gap' : ''), title: fmtDateTime(b.at) + '　' + b.text + (b.gap ? '（之前有沒觀察到的執行）' : '') }))),
    h('span', { class: 'sch-next', text: e.nextRun ? fmtWhen(e.nextRun, now) : '—' }),
    h('span', { class: 'sch-prov' + (prov ? '' : ' guess') }, pk ? providerMark(pk, 'sch-pm') : h('i', { class: 'sch-pm none' }), h('span', { text: provenanceText(e) })),
    h('span', { class: 'sch-acts' }, quick, agent));
  row.addEventListener('click', () => { selected = key; xmlOpen = false; render(true); });
  row.addEventListener('keydown', (ev) => { if (ev.key === 'Enter') { selected = key; render(true); } });
  return decorateRow(row, key, e.nextRun, now);
}

function ledTitle(led) {
  return { run: '執行中', dis: '已停用', fail: '最近一次失敗', warn: '警告或錯過執行', ok: '最近一次成功', info: '還沒有結果' }[led] || '';
}

/** 下次的時間：今天只寫時間，其他寫日期 */
function fmtWhen(ms, now) {
  const d = new Date(ms), n = new Date(now);
  const hhmm = String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
  if (d.toDateString() === n.toDateString()) return hhmm;
  const tomorrow = new Date(n.getFullYear(), n.getMonth(), n.getDate() + 1);
  if (d.toDateString() === tomorrow.toDateString()) return '明天 ' + hhmm;
  return fmtDateTime(ms);
}

// ───── 交給代理 ─────
function agentMenu(anchor, e) {
  const items = [{ type: 'header', label: '交給代理' + (e.management ? '（管理對話：' + e.management.title + '）' : '') }];
  const first = primaryTask(e);
  const ordered = AGENT_TASKS.slice().sort((a, b) => (a.task === first ? -1 : b.task === first ? 1 : 0));
  for (const t of ordered) {
    items.push({ label: t.label, icon: t.icon, onSelect: () => chooseAccount(anchor, e, t.task) });
  }
  openMenu(anchor, items, { placement: 'bottom-end', minWidth: 240 });
}

/** 有管理對話就直接用它；沒有時選帳戶（只有一個可用的就直接用） */
function chooseAccount(anchor, e, task) {
  const management = e && e.management ? e.management : null;
  if (management) {
    go(e, task, null, management.conversationId);
    return;
  }
  const list = fixCandidates(state.accounts, state.schedules && state.schedules.accountId);
  if (!list.length) {
    openMenu(anchor, [{ label: '沒有已登入的帳戶可以用', icon: 'info', disabled: true }], { placement: 'bottom-end', minWidth: 240, force: true });
    return;
  }
  if (list.length === 1) { go(e, task, list[0].id, null); return; }
  openMenu(anchor, [{ type: 'header', label: '用哪個帳戶開對話' }].concat(list.map((a) => ({
    label: a.label, color: a.color, desc: providerInfo(a.provider).name + (a.email ? '・' + a.email : ''),
    onSelect: () => go(e, task, a.id, null),
  }))), { placement: 'bottom-end', minWidth: 260, force: true });
}

function go(e, task, accountId, conversationId) {
  if (api && api.workspace && api.workspace.openScheduleAgent) api.workspace.openScheduleAgent(e ? e.path : null, task, accountId, conversationId, null);
}

function openConv(id) {
  if (id && api && api.workspace && api.workspace.openConversation) api.workspace.openConversation(id);
}

// ───── 明細 ─────
function detailOf(e, s, now, dev) {
  const led = ledOf(e);
  const streak = e.streak > 1 ? h('span', { class: 'sch-badge fail', text: `連續失敗 ${e.streak} 次` }) : null;
  const head = h('div', { class: 'sch-d-head' },
    h('span', { class: 'led led-' + led }), h('h2', { text: e.name, title: e.path }), streak,
    h('span', { class: 'sch-badge origin-' + e.origin, text: ORIGIN_LABEL[e.origin] || e.origin }));
  const meta = h('div', { class: 'sch-d-meta' }, h('span', { text: s.machine || '' }), h('span', { class: 'mono', text: e.path }), e.runAs ? h('span', { text: e.runAs }) : null);

  // 交給代理
  const first = primaryTask(e);
  const tasks = AGENT_TASKS.map((t) => {
    const b = h('button', { type: 'button', class: 'btn sm' + (t.task === first ? ' acc' : '') }, icon(t.icon), h('span', { text: t.label }));
    b.addEventListener('click', () => { if (dev) remoteAgentDialog(e, dev, t, s.machine || ''); else chooseAccount(b, e, t.task); });
    return b;
  });
  const links = [];
  if (e.management) links.push(linkTo('管理對話：' + e.management.title + (e.management.isDraft ? '（草稿）' : ''), e.management.conversationId, true));
  // 其他電腦的：這台不知道那個對話在不在（有同步的話是副本），照樣可以點
  if (e.provenance && e.provenance.conversationId) links.push(linkTo('建立它的對話：' + (e.provenance.title || '對話'), e.provenance.conversationId, dev ? true : e.provenance.exists));
  const diag = [];
  if (isManaged(e) && dev) {
    if (e.lastDiagnosis) diag.push(h('span', { class: 'sch-diag ok' }, icon('check'), h('span', { text: fmtRelative(e.lastDiagnosis.at, now) + ' 已自動請代理診斷（在那台的管理對話）' })));
    if (e.diagnoseBlock) diag.push(h('span', { class: 'sch-diag', text: '沒有自動診斷：' + e.diagnoseBlock }));
  } else if (isManaged(e)) {
    if (e.lastDiagnosis) diag.push(h('span', { class: 'sch-diag ok' }, icon('check'), h('span', { text: fmtRelative(e.lastDiagnosis.at, now) + ' 已自動請代理診斷（只診斷不修改）' }),
      e.lastDiagnosis.exists ? linkTo('看結果', e.lastDiagnosis.conversationId, true) : null));
    if (e.diagnoseBlock) diag.push(h('span', { class: 'sch-diag', text: '沒有自動診斷：' + e.diagnoseBlock }));
    const sw = h('label', { class: 'sch-switch', title: '這個排程失敗時要不要自動請代理診斷（設定頁可以整個關掉）' },
      h('input', { type: 'checkbox', checked: e.autoDiagnose !== false && s.autoDiagnose !== false, disabled: s.autoDiagnose === false }),
      h('span', { text: s.autoDiagnose === false ? '自動診斷（設定頁已關閉）' : '失敗時自動診斷' }));
    sw.querySelector('input').addEventListener('change', (ev) => post({ cmd: 'scheduleAction', path: e.path, action: 'autoDiagnose', on: ev.target.checked }));
    diag.push(sw);
  }
  const agentBox = h('section', { class: 'sch-agentbox' },
    h('div', { class: 'sch-ab-h' }, icon('sparkle'), h('span', { text: '交給代理' }), h('span', { class: 'sch-sub', text: '刪除、改時間、改內容都由代理處理' })),
    h('div', { class: 'sch-ab-tasks' }, tasks),
    links.length ? h('div', { class: 'sch-ab-links' }, links) : null,
    diag.length ? h('div', { class: 'sch-ab-diag' }, diag) : null);

  // 快速操作
  const quick = quickActions(e).map((a) => {
    const b = h('button', { type: 'button', class: 'btn sm', disabled: !!e.busy }, icon(a.icon), h('span', { text: a.label }));
    b.addEventListener('click', () => scheduleAction(e, dev, a.action));
    return b;
  });
  if (e.workFolder && !dev) {
    const b = h('button', { type: 'button', class: 'btn sm', title: e.workFolder }, icon('folderOpen'), h('span', { text: '開啟資料夾' }));
    b.addEventListener('click', () => post({ cmd: 'openPath', path: e.workFolder }));
    quick.push(b);
  }
  const more = iconButton('more', '其他', () => {
    openMenu(more, listActions(e).map((a) => ({ label: a.label, onSelect: () => post({ cmd: 'scheduleAction', path: e.path, action: a.action }) })), { placement: 'bottom-end', minWidth: 220 });
  }, 'sch-more');
  const quickRow = h('div', { class: 'sch-quick' }, quick, dev ? null : more);

  // 定義
  const xml = h('details', { class: 'sch-xml' }, h('summary', { text: '完整定義（XML）' }), h('pre', { class: 'mono', text: e.xml || '' }));
  xml.open = xmlOpen;
  xml.addEventListener('toggle', () => { xmlOpen = xml.open; });
  const kv = (k, v) => (v ? [h('span', { class: 'k', text: k }), v] : []);
  const def = h('section', { class: 'sch-sec' },
    h('div', { class: 'sch-sec-h', text: '定義' }),
    e.error ? h('div', { class: 'sch-err', text: e.error }) : null,
    h('div', { class: 'sch-kv' },
      kv('何時', h('span', { text: e.trigger || '' })),
      kv('下次', e.nextRun ? h('span', { text: fmtWhen(e.nextRun, now) }) : null),
      kv('動作', e.action ? h('code', { class: 'mono sch-code', text: e.action }) : null),
      kv('資料夾', e.workFolder ? h('span', { class: 'mono', text: e.workFolder }) : null),
      kv('條件', e.conditions && e.conditions.length ? h('span', { text: e.conditions.join('・') }) : null),
      kv('作者', e.author ? h('span', { text: e.author }) : null),
      kv('描述', e.description ? h('span', { text: e.description }) : null),
      kv('錯過', e.missed > 0 ? h('span', { class: 'lvl-warn', text: `${e.missed} 次` }) : null)),
    e.xml ? xml : null);

  // 執行紀錄
  const runs = Array.isArray(e.runs) ? e.runs : [];
  const runList = runs.length
    ? h('div', { class: 'sch-runs' }, runs.slice(0, 15).map((r) => h('div', { class: 'sch-run' },
      h('span', { class: 'led led-' + ({ ok: 'ok', fail: 'fail', warn: 'warn' }[r.level] || 'info') }),
      h('span', { class: 'mono', text: fmtDateTime(r.at) }),
      h('span', { class: 'lvl-' + r.level, text: r.text }),
      r.gap ? h('span', { class: 'sch-gap', title: '這一次和前一次之間還有預定的執行，Myriagent 沒有觀察到（兩次讀取之間跑了好幾次，或 app 沒開）', text: '中間有沒觀察到的' }) : null)))
    : h('div', { class: 'sch-sub', text: e.lastRun ? `工作排程器記的上次：${fmtDateTime(e.lastRun)} ${lastText(e)}（Myriagent 之後才會逐次記錄）` : '還沒執行過' });
  const runSec = h('section', { class: 'sch-sec' }, h('div', { class: 'sch-sec-h', text: '執行紀錄（Myriagent 觀察到的）' }), runList);

  // 來源與變更
  const changes = Array.isArray(e.changes) ? e.changes : [];
  const prov = e.provenance;
  const provLine = prov
    ? h('div', { class: 'sch-chg' }, h('span', { class: 'mono', text: fmtDateTime(prov.at) }), h('span', { text: (prov.verb === 'created' ? '建立' : '修改') + '・' }),
      prov.conversationId ? linkTo(prov.title || '對話', prov.conversationId, dev ? true : prov.exists) : h('span', { text: prov.title || '外部 CLI 的工作階段' }),
      prov.provider ? h('span', { class: 'sch-sub', text: '（' + prov.provider + (prov.source === 'transcript' ? '，終端機或 IDE 的工作階段' : '') + '）' }) : null,
      prov.importable && !dev ? importButton(e) : null)
    : h('div', { class: 'sch-sub', text: e.origin === 'suspected' ? '找不到是誰建立的；它執行的是使用者資料夾裡的程式或腳本，看起來是自己開發的' : '找不到是誰建立的' });
  const chgSec = h('section', { class: 'sch-sec' }, h('div', { class: 'sch-sec-h', text: '來源與變更' }), provLine,
    changes.map((c) => h('div', { class: 'sch-chg' }, h('span', { class: 'mono', text: fmtDateTime(c.at) }), h('span', { text: c.text + (c.conversationId ? '・' : '（不是 Myriagent 的對話改的）') }),
      c.conversationId ? linkTo(c.title || '對話', c.conversationId, c.exists) : null)));

  return h('div', { class: 'sch-detail' }, head, meta, agentBox, quickRow, def, runSec, chgSec);
}

// ───── Myriagent 的定時訊息 ─────
function timedGroupOf(timed, now, dev) {
  const rows = timed.map((t) => {
    const e = timedAsEntry(t);
    const led = ledOf(e);
    const bars = historyBars(e);
    const run = iconButton('play', '立即送一次', (ev) => { ev.stopPropagation(); post({ cmd: 'timedAction', id: t.id, action: 'run' }); }, 'sch-q');
    const pause = iconButton(e.enabled ? 'pause' : 'play', e.enabled ? '暫停' : '繼續', (ev) => { ev.stopPropagation(); post({ cmd: 'timedAction', id: t.id, action: e.enabled ? 'pause' : 'resume' }); }, 'sch-q');
    const pk = providerKeyOf(t.provider);
    const target = t.newConversation ? '每次開新對話' : (t.conversationTitle || '對話已刪除');
    const by = t.createdBy ? (t.createdBy.title || '對話') : '你在排程頁新增';
    const key = keyOf(t.id, dev);
    const row = h('div', { class: 'sch-row' + (key === selected ? ' sel' : '') + (e.enabled ? '' : ' off'), role: 'row', tabIndex: 0, dataset: { path: key, led } },
      h('span', { class: 'led led-' + led }),
      h('span', { class: 'sch-name' }, h('b', { text: t.label, title: t.text }), h('span', { class: 'sch-path', text: '→ ' + target })),
      h('span', { class: 'sch-when', text: t.when + (timedStateText(t) ? '・' + timedStateText(t) : '') }),
      h('span', { class: 'sch-last' }, h('span', { class: 'res lvl-' + (t.last ? t.last.level : 'info'), text: t.last ? t.last.text : '還沒送過' }), t.last ? h('span', { class: 'mono sch-at', text: fmtRelative(t.last.at, now) }) : null),
      h('span', { class: 'sch-hist' }, bars.map((b) => h('i', { class: 'lvl-' + b.level, title: fmtDateTime(b.at) + '　' + b.text }))),
      h('span', { class: 'sch-next', text: t.nextAt ? fmtWhen(t.nextAt, now) : '—' }),
      h('span', { class: 'sch-prov' + (t.createdBy ? '' : ' guess') }, pk ? providerMark(pk, 'sch-pm') : h('i', { class: 'sch-pm none' }), h('span', { text: by })),
      h('span', { class: 'sch-acts' }, dev ? null : run, dev ? null : pause));
    row.addEventListener('click', () => { selected = key; render(true); });
    row.addEventListener('keydown', (ev) => { if (ev.key === 'Enter') { selected = key; render(true); } });
    return decorateRow(row, key, t.nextAt, now);
  });
  return h('section', { class: 'sch-group sch-timed' },
    h('div', { class: 'sch-gh' }, icon('clock'), h('b', { text: 'Myriagent 定時訊息' }), h('span', { class: 'sch-sub', text: `${timed.length} 則・app 開著時才送，錯過的重複訊息只補一次` })),
    rows.length
      ? h('div', { class: 'sch-table', role: 'table' }, rows)
      : h('div', { class: 'sch-empty', text: '還沒有定時訊息。代理可以用 myriagent-schedule 建立，或按上面的「新增定時訊息」' }));
}

function timedDetailOf(t, now, dev) {
  const e = timedAsEntry(t);
  const led = ledOf(e);
  const head = h('div', { class: 'sch-d-head' }, h('span', { class: 'led led-' + led }), h('h2', { text: t.label }), h('span', { class: 'sch-badge origin-timed', text: 'Myriagent 定時訊息' }),
    timedStateText(t) ? h('span', { class: 'sch-badge', text: timedStateText(t) }) : null);
  const meta = h('div', { class: 'sch-d-meta' }, h('span', { text: t.when }), t.nextAt ? h('span', { text: '下一次 ' + fmtWhen(t.nextAt, now) }) : null);
  const btn = (ic, label, fn, cls) => { const b = h('button', { type: 'button', class: 'btn sm' + (cls ? ' ' + cls : '') }, icon(ic), h('span', { text: label })); b.addEventListener('click', fn); return b; };
  const quick = h('div', { class: 'sch-quick' },
    btn('play', '立即送一次', () => post({ cmd: 'timedAction', id: t.id, action: 'run' }), 'acc'),
    e.enabled ? btn('pause', '暫停', () => post({ cmd: 'timedAction', id: t.id, action: 'pause' })) : btn('play', '繼續', () => post({ cmd: 'timedAction', id: t.id, action: 'resume' })),
    btn('edit', '編輯', () => openTimedDialog(t)),
    btn('trash', '刪除', () => { if (window.confirm(`刪除定時訊息「${t.label}」？`)) post({ cmd: 'timedAction', id: t.id, action: 'delete' }); }));
  const kv = (k, v) => (v ? [h('span', { class: 'k', text: k }), v] : []);
  const target = t.newConversation
    ? h('span', { text: '每次開新對話' + (t.folder ? '（' + t.folder + '）' : '') })
    : t.conversationExists ? linkTo(t.conversationTitle || '對話', t.conversationId, true) : h('span', { class: 'lvl-fail', text: '對話已刪除' });
  const def = h('section', { class: 'sch-sec' }, h('div', { class: 'sch-sec-h', text: '內容' }),
    h('div', { class: 'sch-kv' },
      kv('送進', target),
      kv('何時', h('span', { text: t.when })),
      kv('建立', t.createdBy ? h('span', {}, linkTo(t.createdBy.title || '對話', t.createdBy.conversationId, t.createdBy.exists), h('span', { class: 'sch-sub', text: '（代理以 myriagent-schedule 建立）' })) : h('span', { text: '你在排程頁新增・' + fmtDateTime(t.createdAt) })),
      kv('文字', h('code', { class: 'mono sch-code', text: t.text }))));
  const runs = Array.isArray(t.runs) ? t.runs : [];
  const runSec = h('section', { class: 'sch-sec' }, h('div', { class: 'sch-sec-h', text: '送出紀錄' }),
    runs.length
      ? h('div', { class: 'sch-runs' }, runs.slice(0, 15).map((r) => h('div', { class: 'sch-run' },
        h('span', { class: 'led led-' + ({ ok: 'ok', fail: 'fail', warn: 'warn' }[r.level] || 'info') }),
        h('span', { class: 'mono', text: fmtDateTime(r.at) }),
        r.conversationId && r.exists ? linkTo(r.text, r.conversationId, true) : h('span', { class: 'lvl-' + r.level, text: r.text }),
        r.late ? h('span', { class: 'sch-gap', text: '補送（原定 ' + fmtDateTime(r.scheduledAt) + '）' }) : null)))
      : h('div', { class: 'sch-sub', text: '還沒送過' }));
  return h('div', { class: 'sch-detail' }, head, meta, dev ? null : quick, def, runSec);
}

/** 新增或編輯定時訊息（timedSave）：對話、送進這個對話或每次開新對話、時間、名稱、文字 */
function openTimedDialog(t) {
  const convs = localConversations(state.conversations);
  const conv = h('select', { class: 'input' }, convs.map((c) => h('option', { value: c.id, text: c.title || '（沒有標題）' })));
  if (t && t.conversationId) conv.value = t.conversationId;
  const modeNew = h('input', { type: 'checkbox', checked: !!(t && t.newConversation) });
  const w = whenOf(t);
  const kind = h('select', { class: 'input' }, WHEN_KINDS.map((k) => h('option', { value: k.kind, text: k.label })));
  kind.value = w.kind;
  const value = h('input', { class: 'input mono', value: w.value, placeholder: (WHEN_KINDS.find((k) => k.kind === w.kind) || {}).placeholder || '' });
  kind.addEventListener('change', () => { const k = WHEN_KINDS.find((x) => x.kind === kind.value); value.placeholder = k ? k.placeholder : ''; });
  const label = h('input', { class: 'input', value: t && t.label ? t.label : '', placeholder: '沒填時取文字的第一行' });
  const text = h('textarea', { class: 'input sch-ta', rows: 6, placeholder: '時間到了要送給代理的文字，例如：整理昨天的訂單異常，寫成報表' });
  text.value = t ? t.text || '' : '';
  const field = (k, el, note) => h('label', { class: 'sch-field' }, h('span', { class: 'k', text: k }), el, note ? h('span', { class: 'sch-sub', text: note }) : null);
  const save = h('button', { type: 'button', class: 'btn acc' }, h('span', { text: '儲存' }));
  const cancel = h('button', { type: 'button', class: 'btn' }, h('span', { text: '取消' }));
  const d = openDialog({
    title: t ? '編輯定時訊息' : '新增定時訊息', width: 560, className: 'dlg-timed',
    body: h('div', { class: 'sch-form' },
      convs.length ? field('對話', conv) : h('div', { class: 'sch-err', text: '這台還沒有可以送進去的對話：先開一個對話送出第一則' }),
      h('label', { class: 'sch-switch' }, modeNew, h('span', { text: '每次開新對話（帳戶、模型、權限模式、資料夾照這個對話；適合每天跑的報表，前文不會越堆越長）' })),
      field('時間', h('div', { class: 'sch-when-row' }, kind, value), 'app 開著（含縮到系統匣）時才送；錯過的重複訊息只補一次'),
      field('名稱', label),
      field('文字', text)),
    footer: [cancel, save],
  });
  cancel.addEventListener('click', () => d.close());
  save.addEventListener('click', () => {
    if (!conv.value) return;
    post({ cmd: 'timedSave', id: t ? t.id : null, conversationId: conv.value, newConversation: modeNew.checked, when: { kind: kind.value, value: value.value }, label: label.value, text: text.value });
    d.close();
  });
}

/** 出處是外部的工作階段：匯入成 Myriagent 對話（之後在那個對話接著管理） */
function importButton(e) {
  const b = h('button', { type: 'button', class: 'btn sm', title: '把那個工作階段匯入成 Myriagent 對話，之後可以在那裡接著問代理' }, icon('download'), h('span', { text: '匯入成對話' }));
  b.addEventListener('click', () => { emit('want-view', 'chat'); post({ cmd: 'scheduleAction', path: e.path, action: 'importOrigin' }); });
  return b;
}

function linkTo(text, conversationId, exists) {
  if (!exists) return h('span', { class: 'sch-sub', text: text + '（已刪除）' });
  const a = h('button', { type: 'button', class: 'sch-link', text });
  a.addEventListener('click', () => openConv(conversationId));
  return a;
}
