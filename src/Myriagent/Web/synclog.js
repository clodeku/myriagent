// 〔遠端〕桌面設定頁「多電腦同步」的遠端操作紀錄與回收區（WP4.6 計畫步 10，D27、D28、D44）：兩個對話框，資料由宿主回
// （syncLoadRemoteAudit → remoteAudit、syncExportRemoteAudit → remoteAuditExport、syncListTrash／syncRestoreTrash → trash；都只送給發問的視窗，
// 格式見 docs/web-protocol.md 第一節）。liveview.js 把這三種訊息交給 handle。
// 發起者、標題、指令名稱都是其他電腦或網頁給的字串，一律 textContent；要顯示的文字由 liverules（auditView、auditNextPage、trashView）產生
import { h, replayClass } from './util.js';
import { icon } from './icons.js';
import { post, state } from './store.js';
import { openDialog, spinner } from './ui.js';
import { auditView, auditNextPage, trashView } from './liverules.js';
import { LIST_ENTRANCE_MAX, LIST_STAGGER_MS } from './fxrules.js';

/** 紀錄一頁幾筆（宿主收 1～200，預設 50） */
const PAGE = 50;
/** 宿主多久沒回就不等了（舊版的宿主不認得這些指令，不會回） */
const NO_REPLY_MS = 10000;

let audit = null; // 開著的紀錄對話框
let trash = null; // 開著的回收區對話框

/** liveview 轉來的桌面訊息；對話框沒開時略過 */
export function handle(m) {
  if (!m || typeof m !== 'object') return;
  switch (m.type) {
    case 'remoteAudit': onAudit(m); break;
    case 'remoteAuditExport': onExport(m); break;
    case 'trash': onTrash(m); break;
    default: break;
  }
}

const pad2 = (n) => (n < 10 ? '0' : '') + n;

/** 完整的日期時間（紀錄保留 180 天，要有年與秒） */
function fullTime(ms) {
  if (typeof ms !== 'number' || !Number.isFinite(ms)) return '時間不明';
  const d = new Date(ms);
  return d.getFullYear() + '/' + (d.getMonth() + 1) + '/' + d.getDate() + ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes()) + ':' + pad2(d.getSeconds());
}

/** 狀態列：讀取中（轉圈）、說明文字或隱藏 */
function setStatus(el, text, busy) {
  el.textContent = '';
  el.hidden = !text;
  if (!text) return;
  if (busy) el.append(spinner(), ' ');
  el.append(h('span', { text }));
}

// ───── 遠端操作紀錄 ─────

/** 紀錄的對話框：打開時讀第一頁；「載入更早的」以這一頁最舊一筆的 (at, seq) 讀下一頁；「匯出」寫成檔案 */
export function openAuditLog() {
  if (audit) audit.d.close();
  const list = h('div', { class: 'slog-list', role: 'list', dataset: { field: 'auditList' } });
  const status = h('p', { class: 'slog-status', role: 'status', dataset: { field: 'auditStatus' } });
  const more = h('button', { type: 'button', class: 'btn sm', hidden: true, dataset: { act: 'auditMore' }, text: '載入更早的' });
  const exportBtn = h('button', { type: 'button', class: 'btn sm', dataset: { act: 'auditExport' } }, icon('download'), h('span', { text: '匯出' }));
  const exportText = h('span', { class: 'slog-export-text', dataset: { field: 'auditExportText' } });
  const reveal = h('button', { type: 'button', class: 'btn sm', hidden: true, dataset: { act: 'auditReveal' }, text: '在檔案總管顯示' });
  const exportBox = h('div', { class: 'slog-export', hidden: true, role: 'status', dataset: { field: 'auditExport' } }, exportText, reveal);
  const body = h('div', { class: 'slog' },
    h('p', { class: 'set-desc', text: '其他電腦與網頁在這台送過的遠端指令，新的在前，保留 180 天。代理輸入的文字與回答的內容不記原文。' }),
    list, status,
    h('div', { class: 'slog-actions' }, more, h('span', { class: 'grow' }), exportBtn),
    exportBox);
  const close = h('button', { type: 'button', class: 'btn', text: '關閉' });
  const d = openDialog({
    title: '遠端操作紀錄', body, footer: [close], className: 'dlg-slog', width: 720,
    onClose: () => { if (audit && audit.d === d) { clearTimeout(audit.timer); audit = null; } },
  });
  close.addEventListener('click', () => d.close());
  const a = { d, list, status, more, exportBtn, exportBox, exportText, reveal, entries: [], seqs: new Set(), waiting: null, timer: 0, exporting: false, exportPath: null };
  audit = a;
  more.addEventListener('click', () => {
    const next = auditNextPage(a.entries);
    if (!next || a.waiting) return;
    requestAudit(a, 'more', { before: next.before, beforeSeq: next.beforeSeq });
  });
  exportBtn.addEventListener('click', () => {
    if (a.exporting) return;
    a.exporting = true;
    exportBtn.disabled = true;
    exportBox.hidden = false;
    exportBox.dataset.tone = '';
    reveal.hidden = true;
    exportText.textContent = '';
    exportText.append(spinner(), ' 匯出中…');
    post({ cmd: 'syncExportRemoteAudit' });
  });
  reveal.addEventListener('click', () => { if (a.exportPath) post({ cmd: 'revealPath', path: a.exportPath }); });
  requestAudit(a, 'first', {});
  return d;
}

/** 讀一頁：first＝整份換掉，more＝接在後面 */
function requestAudit(a, kind, page) {
  a.waiting = kind;
  a.more.disabled = true;
  setStatus(a.status, '讀取中…', true);
  clearTimeout(a.timer);
  a.timer = setTimeout(() => {
    if (audit !== a || !a.waiting) return;
    a.waiting = null;
    a.more.disabled = false;
    setStatus(a.status, '讀不到紀錄：這台的程式沒有回應，請關閉後再開一次', false);
  }, NO_REPLY_MS);
  post(Object.assign({ cmd: 'syncLoadRemoteAudit', limit: PAGE }, page));
}

function onAudit(m) {
  const a = audit;
  if (!a || !a.waiting) return; // 沒在等（對話框關了、已逾時）：略過
  const kind = a.waiting;
  a.waiting = null;
  clearTimeout(a.timer);
  const rows = Array.isArray(m.entries) ? m.entries.filter((e) => e && typeof e === 'object') : [];
  if (kind === 'first') {
    a.entries = [];
    a.seqs = new Set();
    a.list.textContent = '';
  }
  const fresh = [];
  for (const e of rows) {
    // 同一筆不重複（以 sync.db 的流水號為鍵）
    if (typeof e.seq === 'number' && a.seqs.has(e.seq)) continue;
    if (typeof e.seq === 'number') a.seqs.add(e.seq);
    a.entries.push(e);
    const row = auditRow(auditView(e), e.state);
    fresh.push(row);
    a.list.append(row);
  }
  // 讀進來的這一頁滑進來（前 12 列錯開，同側欄新出現的列；fx.css 的 .fx-row-in）
  fresh.slice(0, LIST_ENTRANCE_MAX).forEach((row, i) => { row.style.setProperty('--fx-i', i); replayClass(row, 'fx-row-in', 260 + i * LIST_STAGGER_MS); });
  a.more.hidden = m.hasMore !== true || !auditNextPage(a.entries);
  a.more.disabled = false;
  setStatus(a.status, a.entries.length ? null : '還沒有遠端操作紀錄', false);
}

/** 狀態的色調（同設定頁的狀態膠囊） */
const STATE_TONE = { done: 'st-ready', rejected: 'st-error', unknown: 'st-notLoggedIn', executing: 'st-notLoggedIn' };

/** 紀錄的一列：時間、指令、結果（色調照原本的 state）；來源、層級、政策與其他細節（全部 textContent） */
function auditRow(v, rawState) {
  const conv = v.conversationId && state.convMap ? state.convMap.get(v.conversationId) : null;
  const meta = [
    h('span', { dataset: { field: 'auditIssuer' }, text: '來源 ' + v.issuer }),
    h('span', { text: v.tierText }),
    h('span', { text: '政策 ' + v.policyText }),
  ];
  if (v.fullAccess) meta.push(h('span', { text: '完全自動' }));
  if (v.codeText) meta.push(h('span', { text: '原因 ' + v.codeText }));
  if (v.count > 1) meta.push(h('span', { text: '收到 ' + v.count + ' 次' }));
  if (v.conversationId) meta.push(h('span', { class: 'slog-conv', title: v.conversationId, text: '對話 ' + ((conv && conv.title) || v.conversationId.slice(0, 8)) }));
  // 依結果一顆燈（fx.css）：已執行綠、已拒絕琥珀、結果不明紅、執行中藍
  const led = Object.prototype.hasOwnProperty.call(STATE_TONE, rawState) ? rawState : 'unknown';
  const row = h('div', { class: 'slog-row', role: 'listitem', dataset: { seq: v.seq === null ? '' : String(v.seq), led } },
    h('div', { class: 'slog-head' },
      h('span', { class: 'slog-time', text: fullTime(v.at) }),
      h('span', { class: 'slog-cmd', text: v.cmdLabel }),
      v.defaultPassword ? h('span', { class: 'tag warn', title: '發出這個指令時擁有者的密碼還是預設值', text: '預設密碼' }) : null,
      h('span', { class: 'pill ' + (Object.prototype.hasOwnProperty.call(STATE_TONE, rawState) ? STATE_TONE[rawState] : 'st-checking'), dataset: { field: 'auditState' }, text: v.stateText })),
    h('div', { class: 'slog-meta' }, ...meta));
  return row;
}

function onExport(m) {
  const a = audit;
  if (!a || !a.exporting) return;
  a.exporting = false;
  a.exportBtn.disabled = false;
  const ok = (m.error === null || m.error === undefined) && typeof m.path === 'string' && m.path.length > 0;
  a.exportPath = ok ? m.path : null;
  a.exportBox.dataset.tone = ok ? 'ok' : 'err';
  a.exportText.textContent = ok
    ? '已匯出 ' + (typeof m.count === 'number' && m.count >= 0 ? m.count : 0) + ' 筆：' + m.path
    : (typeof m.error === 'string' && m.error ? m.error : '匯出失敗');
  a.reveal.hidden = !ok;
}

// ───── 回收區 ─────

/** 回收區的對話框：打開時讀清單；「還原」之後宿主提示結果並重送清單 */
export function openTrash() {
  if (trash) trash.d.close();
  const list = h('div', { class: 'slog-list', role: 'list', dataset: { field: 'trashList' } });
  const status = h('p', { class: 'slog-status', role: 'status', dataset: { field: 'trashStatus' } });
  const body = h('div', { class: 'slog' },
    h('p', { class: 'set-desc', text: '其他電腦或網頁遠端刪除的對話，在這台保留 30 天，到期自動清掉。還原的對話只留在這台，不再同步到伺服器。' }),
    list, status);
  const close = h('button', { type: 'button', class: 'btn', text: '關閉' });
  const d = openDialog({
    title: '回收區', body, footer: [close], className: 'dlg-slog', width: 640,
    onClose: () => { if (trash && trash.d === d) { clearTimeout(trash.timer); trash = null; } },
  });
  close.addEventListener('click', () => d.close());
  const t = { d, list, status, timer: 0, loaded: false };
  trash = t;
  setStatus(status, '讀取中…', true);
  t.timer = setTimeout(() => {
    if (trash === t && !t.loaded) setStatus(status, '讀不到回收區：這台的程式沒有回應，請關閉後再開一次', false);
  }, NO_REPLY_MS);
  post({ cmd: 'syncListTrash' });
  return d;
}

function onTrash(m) {
  const t = trash;
  if (!t) return;
  t.loaded = true;
  clearTimeout(t.timer);
  const now = Date.now();
  const rows = (Array.isArray(m.entries) ? m.entries : []).map((e) => trashView(e, now)).filter((v) => v.conversationId);
  t.list.replaceChildren(...rows.map((v) => trashRow(v)));
  setStatus(t.status, rows.length ? null : '回收區是空的', false);
}

/** 回收區的一列：標題、刪除時間、發起者、剩幾天、「還原」 */
function trashRow(v) {
  const restore = h('button', { type: 'button', class: 'btn sm', dataset: { act: 'trashRestore', conversationId: v.conversationId }, text: '還原' });
  restore.addEventListener('click', () => {
    restore.disabled = true;
    restore.textContent = '還原中…';
    post({ cmd: 'syncRestoreTrash', conversationId: v.conversationId });
  });
  return h('div', { class: 'slog-row slog-trash', role: 'listitem', dataset: { conversationId: v.conversationId } },
    h('div', { class: 'slog-head' },
      h('span', { class: 'slog-cmd slog-title', dataset: { field: 'trashTitle' }, text: v.title }),
      restore),
    h('div', { class: 'slog-meta' },
      h('span', { text: '刪除於 ' + fullTime(v.trashedAt) }),
      h('span', { dataset: { field: 'trashIssuer' }, text: '發起者 ' + v.issuer }),
      v.daysText ? h('span', { dataset: { field: 'trashDays' }, text: v.daysText }) : null));
}
