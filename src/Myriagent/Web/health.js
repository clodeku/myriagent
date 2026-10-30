// 儀表板的健康度（只有桌面；web-protocol 的 health／healthCheck／healthFix）：
// 帳戶卡上的「健康度」一行（點了開明細）、明細對話框（全域指示、自動記憶索引、skill、MCP、外掛、設定檔，各自的量測與建議），
// 以及「電腦層級的指示檔」區塊（各 CLI 使用者層的 CLAUDE.md／AGENTS.md／GEMINI.md 互相缺哪些、要不要合併與同步）。
// 「用代理合併」與各項裡的「用代理精簡」（全域指示）、「用代理整理」（自動記憶索引）開新對話、輸入框預先填好說明，由使用者看過再送出。
// 所有數字與句子都由宿主算好，這裡只負責顯示
import { h, iconButton, fmtRelative, fmtDateTime, replayClass } from './util.js';
import { icon, providerMark } from './icons.js';
import { post, state, accountOf, providerInfo, isHub } from './store.js';
import { openMenu, openDialog, spinner } from './ui.js';
import { levelOf, levelLabel, accountHealthOf, stripText, instructionsVisible, docStatus, coveragePct, coverageText, fixCandidates, FIX_TASKS, itemFixTask, healthCells } from './healthrules.js';

let ws = null;
/** 開著的明細對話框：{ accountId, render }（health 訊息到了重畫） */
let openDlg = null;

/** dashboard.js 初始化時給 workspace 的 api（openHealthFix） */
export function setHealthWorkspace(api) { ws = api; }

/** 請宿主分析（切到儀表板時送；force＝「重新分析」） */
export function requestHealth(force) {
  if (isHub || state.mode === 'popout') return;
  post(force ? { cmd: 'healthCheck', force: true } : { cmd: 'healthCheck' });
}

/** 分析做完時健康度那一行的分格由左往右亮一次（fx.css 的 .acard-health.fx-ah-scan） */
const AH_SCAN_FX_MS = 900;

const LEVEL_ICON = { ok: 'check', info: 'info', warn: 'warning', bad: 'error' };

function levelIcon(level, cls) {
  return icon(LEVEL_ICON[levelOf(level)], cls);
}

// ───── 帳戶卡的一行 ─────
/** 帳戶卡上的「健康度」按鈕（dashboard.js 的 createCard 建一次） */
export function createHealthStrip(card) {
  const btn = h('button', { type: 'button', class: 'acard-health', hidden: true });
  btn.addEventListener('click', () => { const a = accountOf(card.dataset.key); if (a) openHealthDialog(a); });
  return btn;
}

/** 依目前的 state.health 更新卡片上的一行；這個帳戶沒有結果（瀏覽器版、remoteOnly、還沒分析）就藏起來 */
export function updateHealthStrip(btn, a) {
  const v = !isHub && a.remoteOnly !== true ? accountHealthOf(state.health, a.id) : null;
  const running = !!(state.health && state.health.running);
  // 分格量表（healthrules.healthCells）：每個檢查項目一格、依等級上色；分析剛做完（running 從 true 變 false）時由左往右掃一次
  const cells = v ? healthCells(v) : [];
  const finished = btn._running === true && !running;
  btn._running = running;
  const sig = v ? JSON.stringify([v.level, stripText(v), v.load || '', running, cells.map((c) => c.level + ':' + c.title + ':' + c.value)]) : '';
  if (btn._sig === sig) { if (finished && cells.length) replayClass(btn, 'fx-ah-scan', AH_SCAN_FX_MS); return; }
  btn._sig = sig;
  btn.textContent = '';
  btn.hidden = !v;
  if (!v) return;
  const level = levelOf(v.level);
  btn.dataset.level = level;
  btn.title = '健康度：' + levelLabel(level) + '\n' + stripText(v) + (v.load ? '\n每次對話固定載入' + v.load : '') + '\n點一下看明細';
  // 原生 append 會把 null 寫成文字，沒有的部分先濾掉
  btn.append(...[icon('pulse', 'ah-ic'), h('span', { class: 'ah-label', text: '健康度' }),
    h('span', { class: 'ah-badge', dataset: { level } }, levelIcon(level, 'ah-badge-ic'), h('span', { text: levelLabel(level) })),
    cells.length ? h('span', { class: 'ah-segs', 'aria-hidden': 'true' }, ...cells.map((c, i) => h('i', {
      dataset: { level: c.level }, style: { '--fx-i': String(i) },
      title: c.title + '：' + levelLabel(c.level) + (c.value ? '（' + c.value + '）' : ''),
    }))) : null,
    h('span', { class: 'ah-text', text: level === 'ok' ? (v.load ? '每次對話固定載入' + v.load : '良好') : stripText(v) }),
    running ? spinner('ah-spin') : null,
    icon('chevRight', 'ah-chev')].filter(Boolean));
  if (finished && cells.length) replayClass(btn, 'fx-ah-scan', AH_SCAN_FX_MS);
}

// ───── 明細對話框 ─────
export function openHealthDialog(a) {
  const body = h('div', { class: 'hd' });
  const refresh = h('button', { type: 'button', class: 'btn sm ghost' });
  refresh.addEventListener('click', () => requestHealth(true));
  const close = h('button', { type: 'button', class: 'btn', text: '關閉' });
  const d = openDialog({
    title: '「' + a.label + '」的健康度', body, footer: [refresh, h('span', { class: 'grow' }), close], width: 640, className: 'dlg-health',
    onClose: () => { if (openDlg && openDlg.dialog === d) openDlg = null; },
  });
  close.addEventListener('click', () => d.close());
  const render = () => {
    const acc = accountOf(a.id) || a;
    const v = accountHealthOf(state.health, a.id);
    const running = !!(state.health && state.health.running);
    refresh.disabled = running;
    refresh.textContent = '';
    refresh.append(running ? spinner() : icon('refresh'), h('span', { text: running ? '分析中…' : '重新分析' }));
    body.textContent = '';
    if (!v) {
      body.append(h('p', { class: 'hd-empty', text: running ? '分析中…' : '還沒有這個帳戶的分析結果。' }));
      return;
    }
    const level = levelOf(v.level);
    const at = state.health && state.health.checkedAt;
    body.append(h('div', { class: 'hd-top', dataset: { level } },
      h('span', { class: 'hd-badge', dataset: { level } }, levelIcon(level, 'hd-badge-ic'), h('span', { text: levelLabel(level) })),
      h('div', { class: 'hd-top-body' },
        h('div', { class: 'hd-summary', text: stripText(v) }),
        h('div', { class: 'hd-meta' },
          h('span', { text: providerInfo(acc.provider).name }),
          v.load ? h('span', { text: '每次對話固定載入' + v.load }) : null,
          at ? h('span', { title: fmtDateTime(at), text: '分析於 ' + fmtRelative(at) }) : null))));
    for (const it of v.items) body.append(itemBlock(it, itemFixTask(v, it), a.id));
    body.append(h('p', { class: 'hd-foot', text: 'token 數是粗估（中文一字約一個、英文約四個字元一個），實際以各 CLI 的 /context 為準。' }));
  };
  openDlg = { accountId: a.id, render, dialog: d };
  render();
  requestHealth(false);
}

/** 明細的一項：等級、標題、量測、說明（多段）、相關檔案；fixTask（trim／memory）時最後放「用代理…」按鈕 */
function itemBlock(it, fixTask, accountId) {
  const level = levelOf(it.level);
  const files = Array.isArray(it.files) ? it.files : [];
  const detail = typeof it.detail === 'string' && it.detail ? it.detail.split('\n') : [];
  let fix = null;
  if (fixTask) {
    const t = FIX_TASKS[fixTask];
    const btn = h('button', { type: 'button', class: 'btn sm hd-fix', title: t.title, dataset: { task: fixTask } }, icon('sparkle'), h('span', { text: t.label }));
    btn.addEventListener('click', () => chooseFixAccount(btn, fixTask, accountId));
    fix = h('div', { class: 'hd-item-actions' }, btn);
  }
  return h('section', { class: 'hd-item', dataset: { level, key: it.key || '' } },
    h('div', { class: 'hd-item-head' },
      levelIcon(level, 'hd-item-ic'),
      h('h3', { class: 'hd-item-title', text: it.title || '' }),
      h('span', { class: 'hd-item-value', text: it.value || '' })),
    detail.length ? h('div', { class: 'hd-item-detail' }, detail.map((t) => h('p', { text: t }))) : null,
    files.length ? h('ul', { class: 'hd-files' }, files.map(fileRow)) : null,
    fix);
}

/** 檔案一列：名稱與量測；路徑點了用預設程式開、旁邊的按鈕在檔案總管顯示；符號連結另列實際檔案 */
function fileRow(f) {
  const level = levelOf(f.level);
  const open = h('button', { type: 'button', class: 'hd-path mono', text: f.path, title: '用預設程式開啟\n' + f.path });
  open.addEventListener('click', () => post({ cmd: 'openPath', path: f.path }));
  const reveal = iconButton('folderOpen', '在檔案總管中顯示', () => post({ cmd: 'revealPath', path: f.target || f.path }), 'hd-reveal');
  return h('li', { class: 'hd-file', dataset: { level } },
    h('div', { class: 'hd-file-line' },
      h('span', { class: 'hd-file-dot', dataset: { level } }),
      h('span', { class: 'hd-file-label', text: f.label || '' }),
      h('span', { class: 'hd-file-note', text: f.note || '' })),
    h('div', { class: 'hd-file-path' }, open, reveal),
    f.target ? h('div', { class: 'hd-file-target' }, icon('link', 'hd-link-ic'), h('span', { class: 'mono', text: '實際檔案：' + f.target, title: f.target })) : null);
}

/** health 訊息到了：開著的明細對話框重畫 */
export function refreshHealthDialog() {
  if (openDlg) openDlg.render();
}

// ───── 「用代理合併／精簡／整理」 ─────
/** 選要用哪個帳戶開對話（這台已登入的）；精簡、整理時那個帳戶自己排第一 */
function chooseFixAccount(anchor, task, targetAccountId) {
  const target = task === 'merge' ? null : targetAccountId;
  const list = fixCandidates(state.accounts, target);
  if (!list.length) {
    openMenu(anchor, [{ label: '沒有已登入的帳戶可以用', icon: 'info', disabled: true }], { placement: 'top-end', minWidth: 240 });
    return;
  }
  const go = (accountId) => {
    if (openDlg) openDlg.dialog.close();
    if (ws) ws.openHealthFix(task, accountId, target);
  };
  openMenu(anchor, [{ type: 'header', label: FIX_TASKS[task].menu }].concat(list.map((a) => ({
    label: a.label, color: a.color, desc: providerInfo(a.provider).name + (a.email ? '・' + a.email : ''),
    onSelect: () => go(a.id),
  }))), { placement: 'bottom-end', minWidth: 260 });
}

// ───── 電腦層級的指示檔 ─────
/** 區塊的殼（dashboard.js 放在帳戶區之後）；瀏覽器版不建 */
export function createInstructionsSection() {
  const refresh = h('button', { type: 'button', class: 'btn sm' });
  refresh.addEventListener('click', () => requestHealth(true));
  const verdict = h('div', { class: 'hx-verdict', role: 'status' });
  const grid = h('div', { class: 'hx-grid' });
  const merge = h('button', { type: 'button', class: 'btn sm primary' }, icon('sparkle'), h('span', { text: FIX_TASKS.merge.label }));
  merge.addEventListener('click', () => chooseFixAccount(merge, 'merge', null));
  const actions = h('div', { class: 'hx-actions', hidden: true }, merge,
    h('span', { class: 'hx-hint', text: '會開新對話並填好合併說明，你看過再送出；代理先給合併計畫，等你確認才改檔，改之前會備份。' }));
  const sec = h('section', { class: 'dash-sec dash-sec-hx', 'aria-label': '電腦層級的指示檔', hidden: true },
    h('div', { class: 'dash-sec-head dash-sec-gap' }, h('h2', { class: 'dash-h', text: '電腦層級的指示檔' }),
      h('span', { class: 'dash-sub', text: '各 CLI 每次對話都會載入的全域指示' }), h('span', { class: 'grow' }), refresh),
    verdict, grid, actions);
  sec._els = { refresh, verdict, grid, actions };
  return sec;
}

export function renderInstructionsSection(sec) {
  if (!sec) return;
  const e = sec._els;
  const health = state.health;
  const show = !isHub && instructionsVisible(health);
  sec.hidden = !show;
  if (!show) return;
  const ins = health.instructions;
  const running = !!health.running;
  e.refresh.disabled = running;
  const sig = JSON.stringify([ins, running, health.checkedAt]);
  if (sec._sig === sig) return;
  sec._sig = sig;
  e.refresh.textContent = '';
  e.refresh.append(running ? spinner() : icon('refresh'), h('span', { text: running ? '分析中…' : '重新分析' }));

  const level = levelOf(ins.level);
  e.verdict.textContent = '';
  e.verdict.dataset.level = level;
  e.verdict.append(...[levelIcon(level, 'hx-verdict-ic'), h('span', { class: 'hx-verdict-text', text: ins.summary || '' }),
    health.checkedAt ? h('span', { class: 'hx-when', title: fmtDateTime(health.checkedAt), dataset: { relAt: String(health.checkedAt), relPrefix: '分析於 ' }, text: '分析於 ' + fmtRelative(health.checkedAt) }) : null].filter(Boolean));

  e.grid.textContent = '';
  for (const doc of ins.docs) e.grid.append(docCard(doc));
  e.actions.hidden = !ins.mergeable;
}

function docCard(doc) {
  const st = docStatus(doc);
  const pct = coveragePct(doc);
  const open = h('button', { type: 'button', class: 'hx-path mono', text: doc.path, title: doc.exists ? '用預設程式開啟\n' + doc.path : '這個檔案不存在：' + doc.path, disabled: !doc.exists });
  open.addEventListener('click', () => post({ cmd: 'openPath', path: doc.path }));
  const reveal = iconButton('folderOpen', '在檔案總管中顯示', () => post({ cmd: 'revealPath', path: doc.exists ? (doc.target || doc.path) : doc.path.replace(/[\\/][^\\/]*$/, '') }), 'hx-reveal');
  const missing = Array.isArray(doc.missing) ? doc.missing : [];
  const unique = Array.isArray(doc.unique) ? doc.unique : [];
  const card = h('article', { class: 'hx-card', dataset: { tone: st.tone } },
    h('div', { class: 'hx-head' }, h('span', { class: 'hx-mark' }, providerMark(doc.provider)),
      h('div', { class: 'hx-id' }, h('h3', { class: 'hx-name', text: doc.label || doc.provider }), h('span', { class: 'hx-note', text: doc.note || '' })),
      h('span', { class: 'hx-pill', dataset: { tone: st.tone }, text: st.text })),
    h('div', { class: 'hx-file' }, open, reveal),
    doc.target ? h('div', { class: 'hx-target mono', title: doc.target }, icon('link', 'hx-link-ic'), h('span', { text: doc.target })) : null,
    h('div', { class: 'hx-cov', title: coverageText(doc) },
      h('div', { class: 'hx-bar', role: 'meter', 'aria-valuemin': '0', 'aria-valuemax': '100', 'aria-valuenow': String(pct), 'aria-label': '涵蓋率' },
        h('span', { class: 'hx-bar-fill', style: { width: pct + '%' } })),
      h('span', { class: 'hx-cov-text', text: coverageText(doc) })),
    doc.alsoLoads ? h('div', { class: 'hx-also' }, icon('info', 'hx-also-ic'),
      h('span', { text: '另會讀取 ' + doc.alsoLoads + ' 的指示（Claude 相容）' + (Number(doc.duplicates) > 0 ? '，其中 ' + doc.duplicates + ' 條重複載入' : '') })) : null,
    unitList('缺少的指示', Number(doc.missingCount) || 0, missing, 'missing'),
    unitList('只有這份有', Number(doc.uniqueCount) || 0, unique, 'unique'));
  return card;
}

/** 可以展開的條目清單（最多列宿主給的那幾條，其餘寫「還有 N 條」） */
function unitList(title, count, units, kind) {
  if (!count) return null;
  const list = h('ul', { class: 'hx-units' }, units.map((u) => h('li', null,
    u.section ? h('span', { class: 'hx-sec', text: u.section }) : null, h('span', { class: 'hx-unit', text: u.text || '' }))));
  if (count > units.length) list.append(h('li', { class: 'hx-more', text: '還有 ' + (count - units.length) + ' 條' }));
  return h('details', { class: 'hx-det', dataset: { kind } }, h('summary', null, h('span', { text: title }), h('b', { text: String(count) })), list);
}
