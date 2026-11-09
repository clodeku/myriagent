// 電腦狀態面板的檔案傳輸（sync-protocol 14.15）：右鍵（觸控是長按）點一台電腦，
// 瀏覽那台整台本機磁碟的資料夾與檔案（deviceBrowse）、多選下載（deviceFetch → 桌面 peerXferSave 寫進這台的資料夾／瀏覽器交給瀏覽器下載）、
// 上傳（桌面 peerUpload {deviceId}／瀏覽器 E4 → devicePut；那台接收的進度看 deviceStats 的 xfers）。
// 規則在 devicefilesrules.js；文字一律 textContent，class 一律 df- 前綴。
import { h, setHidden, fmtBytes, fmtRelative } from './util.js';
import { icon, FILE_ICON } from './icons.js';
import { state, post, postWithFiles, isHub, nextRequestId, newId, peerId } from './store.js';
import { openDialog, openMenu, toast, spinner } from './ui.js';
import { track } from './cmdtrack.js';
import { uploadFile } from './hubapi.js';
import { uploadResult, peerOf } from './liverules.js';
import * as R from './devicefilesrules.js';

const FOLDER_KEY = 'myriagent.app.xfer.folder.v1';

/** deviceBrowse 的 requestId → 對話框的工作階段 */
const browses = new Map();
/** deviceFetch 的 requestId → 傳輸 */
const fetches = new Map();
/** 桌面代傳的上傳（uploadId → 傳輸） */
const hostUploads = new Map();
/** xferPickFolder 的 requestId → 回呼 */
const folderPicks = new Map();
/** 全部的傳輸（依加入的順序；結束的留著直到清掉） */
const transfers = [];
let session = null;
let seq = 0;
let browserDownloadAt = 0;

// ───── 電腦卡片的右鍵選單（monitor.js 呼叫） ─────

/** 卡片的選單項目（這台與 Hub 主機回 null：沒有選單） */
export function cardMenuItems(row) {
  const device = deviceOf(row && row.id);
  const avail = R.transferMenu({ row, device, hub: isHub, peer: peerOf(state.remote, isHub ? 'hub' : 'desktop') });
  if (!avail) return null;
  const why = avail.enabled ? null : avail.reason;
  return [
    { type: 'header', label: row.name || '電腦' },
    { label: '瀏覽與下載檔案…', icon: 'download', disabled: !avail.enabled, desc: why, onSelect: () => openDeviceFiles(row.id) },
    { label: '上傳檔案到這台…', icon: 'arrowUp', disabled: !avail.enabled, desc: why, onSelect: () => openDeviceFiles(row.id, { upload: true }) },
  ];
}

/** 卡片上按右鍵或長按：開選單（沒有選單的卡片不處理） */
export function openCardMenu(e, row) {
  const items = cardMenuItems(row);
  if (!items) return false;
  e.preventDefault();
  openMenu(null, items, { at: { x: e.clientX, y: e.clientY }, className: 'df-menu', minWidth: 220 });
  return true;
}

function deviceOf(id) {
  const list = state.remote && Array.isArray(state.remote.devices) ? state.remote.devices : [];
  return id ? list.find((d) => d && d.id === id) || null : null;
}

function nameOf(id) {
  const d = deviceOf(id);
  return (d && (d.name || d.machineName)) || '那台電腦';
}

// ───── 對話框 ─────

/** 開那台的檔案對話框（upload：一打開就跳出選檔，選完上傳到使用者接著選的資料夾） */
export function openDeviceFiles(deviceId, { upload = false } = {}) {
  if (session && session.deviceId === deviceId && session.d) {
    if (upload) pickUploads();
    return;
  }
  if (session && session.d) session.d.close();
  const s = { deviceId, path: null, parent: null, entries: [], selected: new Set(), anchor: null, loading: false, error: null, truncated: false, requestId: null };
  session = s;
  buildDialog(s);
  browse(s, null);
  if (upload) toast('先開到要放檔案的資料夾，再按「上傳到這裡」或把檔案拖進清單', 'info');
}

function buildDialog(s) {
  const name = nameOf(s.deviceId);
  s.up = iconBtn('up', '上一層', () => browse(s, s.parent));
  s.home = iconBtn('desktop', '磁碟與常用資料夾', () => browse(s, null));
  s.input = h('input', { type: 'text', class: 'df-path', spellcheck: false, placeholder: '輸入路徑（例如 D:\\資料）後按 Enter', 'aria-label': '路徑' });
  s.input.addEventListener('keydown', (e) => {
    if (e.key !== 'Enter' || e.isComposing || e.keyCode === 229) return;
    e.preventDefault();
    const p = R.normalizePath(s.input.value);
    browse(s, p);
  });
  s.refresh = iconBtn('refresh', '重新整理', () => browse(s, s.path));
  s.crumbs = h('div', { class: 'df-crumbs', role: 'navigation', 'aria-label': '路徑' });
  s.list = h('div', { class: 'df-list', role: 'listbox', 'aria-multiselectable': 'true', tabIndex: 0 });
  s.status = h('div', { class: 'df-status', 'aria-live': 'polite' });
  s.xfers = h('div', { class: 'df-xfers' });
  s.xferHead = h('div', { class: 'df-xfers-head' }, h('span', { text: '傳輸' }), h('span', { class: 'grow' }),
    h('button', { type: 'button', class: 'btn btn-sm', text: '清除已完成', onClick: () => { clearFinished(); renderXfers(); } }));
  s.xferWrap = h('div', { class: 'df-xfer-wrap' }, s.xferHead, s.xfers);
  s.fileInput = h('input', { type: 'file', multiple: true, hidden: true });
  s.fileInput.addEventListener('change', () => {
    const files = Array.from(s.fileInput.files || []);
    s.fileInput.value = '';
    if (files.length) queueUploads(s, files);
  });
  s.list.addEventListener('dragover', (e) => {
    if (!hasFiles(e) || !R.canUploadHere(s.path)) return;
    e.preventDefault();
    e.stopPropagation();
    s.list.classList.add('df-drop');
  });
  s.list.addEventListener('dragleave', () => s.list.classList.remove('df-drop'));
  s.list.addEventListener('drop', (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    e.stopPropagation();
    s.list.classList.remove('df-drop');
    if (!R.canUploadHere(s.path)) { toast('先開到一個資料夾再把檔案拖進來', 'info'); return; }
    queueUploads(s, Array.from(e.dataTransfer.files || []));
  });
  s.list.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'a') { e.preventDefault(); s.selected = R.toggleAll(s.entries, s.selected); renderList(s); }
    else if (e.key === 'Backspace' && s.path) { e.preventDefault(); browse(s, s.parent); }
  });
  const body = h('div', { class: 'df-body' },
    h('div', { class: 'df-bar' }, s.home, s.up, s.input, s.refresh),
    s.crumbs, s.list, s.status, s.xferWrap, s.fileInput);

  s.selInfo = h('span', { class: 'df-sel' });
  s.dest = h('button', { type: 'button', class: 'btn df-dest', title: '下載的檔案存在這台的哪個資料夾' });
  s.dest.addEventListener('click', () => pickDestFolder());
  s.uploadBtn = h('button', { type: 'button', class: 'btn' }, icon('arrowUp'), h('span', { text: '上傳到這裡…' }));
  s.uploadBtn.addEventListener('click', () => pickUploads());
  s.downloadBtn = h('button', { type: 'button', class: 'btn primary' }, icon('download'), h('span', { class: 'df-dl-text', text: '下載' }));
  s.downloadBtn.addEventListener('click', () => {
    const files = R.selectedFiles(s.entries, s.selected);
    if (!files.length) return;
    queueDownloads(s, files);
    s.selected = new Set();
    renderList(s);
  });
  const footer = [s.selInfo, h('span', { class: 'grow' })];
  if (!isHub) footer.push(s.dest);
  footer.push(s.uploadBtn, s.downloadBtn);
  s.d = openDialog({
    title: '「' + nameOf(s.deviceId) + '」的檔案', body, footer, className: 'df-dlg', width: 860, initialFocus: s.list,
    onClose: () => { if (session === s) session = null; s.d = null; },
  });
  s.name = name;
  renderDest(s);
  renderXfers();
}

function iconBtn(name, title, onClick) {
  const b = h('button', { type: 'button', class: 'ibtn df-ibtn', title, 'aria-label': title }, icon(name));
  b.addEventListener('click', onClick);
  return b;
}

const hasFiles = (e) => e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');

function pickUploads() {
  const s = session;
  if (!s) return;
  if (!R.canUploadHere(s.path)) { toast('先開到要放檔案的資料夾（磁碟與常用資料夾、網路、電腦這幾層不能放檔案）', 'info'); return; }
  s.fileInput.click();
}

// ───── 瀏覽（deviceBrowse） ─────

function browse(s, path) {
  const rid = nextRequestId('df');
  s.requestId = rid;
  s.loading = true;
  s.error = null;
  browses.set(rid, s);
  renderStatus(s);
  const cid = post({ cmd: 'deviceBrowse', deviceId: s.deviceId, requestId: rid, path: path || null });
  if (cid === null) {
    browses.delete(rid);
    failBrowse(s, rid, '沒有送出：目前沒有連上伺服器');
    return;
  }
  track(cid, {
    onSettle: (st, code) => {
      if (st === 'done') return;
      browses.delete(rid);
      failBrowse(s, rid, failText(code, s.deviceId));
    },
  });
}

function failBrowse(s, rid, text) {
  if (s.requestId !== rid) return;
  s.loading = false;
  s.error = text;
  renderStatus(s);
}

/** 指令被拒或沒有結果時的說明（伺服器另外會送那台說的原因的 toast） */
function failText(code, deviceId) {
  const who = '「' + nameOf(deviceId) + '」';
  switch (code) {
    case 'policy': return who + '不允許這個路徑或操作（禁止的資料夾、政策不是「可以操作」，或那台以系統管理員執行）';
    case 'unsupported': return who + '的版本太舊或沒有連上伺服器，請先更新那台';
    case 'offline': return who + '離線';
    case 'conflict': return '那個資料夾不存在或讀不到';
    case 'not_sent': return '沒有送出：目前沒有連上伺服器';
    case 'busy': return who + '正在忙，請稍候再試';
    case null: case undefined: return who + '沒有回應（結果不明）';
    default: return who + '拒絕了這個要求（' + code + '）';
  }
}

/** 宿主的 files：deviceBrowse 的回覆（conversationId 是 null、requestId 是自己送的）才收，回 true */
export function onFiles(m) {
  if (!m || m.conversationId || typeof m.requestId !== 'string' || !browses.has(m.requestId)) return false;
  const s = browses.get(m.requestId);
  browses.delete(m.requestId);
  if (s.requestId !== m.requestId) return true;
  s.loading = false;
  // 有錯誤也切到那一層（路徑列、上一層、空的清單），錯誤寫在清單下方：網路芳鄰的「網路」找不到電腦時要看得出已經在「網路」、可以直接輸入路徑
  // （2026-11-09 實機：以前留在上一層的清單，看起來像沒反應）；沒帶路徑的錯誤（舊版）照舊只顯示錯誤
  s.error = m.error || null;
  if (m.error && typeof m.path !== 'string') { renderStatus(s); return true; }
  s.path = typeof m.path === 'string' ? m.path : null;
  s.parent = s.path ? (typeof m.parent === 'string' ? m.parent : null) : null;
  s.entries = R.sortEntries(m.entries);
  s.truncated = !!m.truncated;
  s.selected = new Set();
  s.anchor = null;
  s.input.value = s.path || '';
  renderCrumbs(s);
  renderList(s);
  renderStatus(s);
  return true;
}

function renderCrumbs(s) {
  s.crumbs.textContent = '';
  const root = h('button', { type: 'button', class: 'df-crumb', text: '磁碟與常用資料夾' });
  root.addEventListener('click', () => browse(s, null));
  s.crumbs.append(root);
  for (const c of R.crumbs(s.path)) {
    s.crumbs.append(h('span', { class: 'df-crumb-sep', 'aria-hidden': 'true' }, icon('chevRight')));
    const b = h('button', { type: 'button', class: 'df-crumb', text: c.label, title: c.path });
    b.addEventListener('click', () => browse(s, c.path));
    s.crumbs.append(b);
  }
  setHidden(s.up, !s.path);
}

function renderList(s) {
  s.list.textContent = '';
  s.entries.forEach((e, i) => {
    const dir = e.kind === 'dir';
    const checked = s.selected.has(e.path);
    const row = h('div', {
      class: 'df-row' + (dir ? ' df-dir' : '') + (checked ? ' df-on' : ''), role: 'option', 'aria-selected': String(checked),
      title: e.path, dataset: { index: String(i) },
    });
    const box = h('input', { type: 'checkbox', class: 'df-check', checked, disabled: dir, tabIndex: -1, 'aria-label': '選取 ' + e.name });
    const size = dir ? '' : (typeof e.size === 'number' ? fmtBytes(e.size) : '');
    const when = typeof e.modified === 'number' ? fmtRelative(e.modified) : '';
    row.append(box, h('span', { class: 'df-ic' }, icon(FILE_ICON[e.kind] || 'file')),
      h('span', { class: 'df-name', text: e.name }), h('span', { class: 'df-size', text: size }), h('span', { class: 'df-when', text: when }));
    row.addEventListener('click', (ev) => {
      if (dir) { browse(s, e.path); return; }
      const mode = ev.shiftKey ? 'range' : (ev.ctrlKey || ev.metaKey || ev.target === box) ? 'toggle' : 'plain';
      const next = R.selectAt(s.entries, s.selected, s.anchor, i, mode);
      s.selected = next.selected;
      s.anchor = next.anchor;
      renderList(s);
    });
    row.addEventListener('dblclick', () => { if (!dir) queueDownloads(s, [e]); });
    s.list.append(row);
  });
  renderFooter(s);
}

function renderStatus(s) {
  s.status.textContent = '';
  if (s.loading) s.status.append(spinner('df-spin'), h('span', { text: '讀取中…' }));
  else if (s.error) s.status.append(icon('warning'), h('span', { class: 'df-err', text: s.error }));
  else if (!s.entries.length) s.status.append(h('span', { text: s.path ? '這個資料夾是空的' : '沒有可以瀏覽的磁碟' }));
  else if (s.truncated) s.status.append(h('span', { text: '項目太多，只列出前面的一部分' }));
  setHidden(s.status, !s.status.childNodes.length);
  s.list.classList.toggle('df-busy', !!s.loading);
}

function renderFooter(s) {
  const files = R.selectedFiles(s.entries, s.selected);
  const bytes = files.reduce((a, f) => a + (typeof f.size === 'number' ? f.size : 0), 0);
  s.selInfo.textContent = files.length ? '已選 ' + files.length + ' 個檔案（' + fmtBytes(bytes) + '）' : '點檔案選取，Shift 連選、Ctrl 加選，雙擊直接下載';
  s.downloadBtn.disabled = !files.length;
  s.downloadBtn.querySelector('.df-dl-text').textContent = files.length ? '下載 ' + files.length + ' 個檔案' : '下載';
  s.uploadBtn.disabled = !R.canUploadHere(s.path);
}

function renderDest(s) {
  if (isHub || !s.dest) return;
  const folder = destFolder();
  s.dest.textContent = '';
  s.dest.append(icon('folder'), h('span', { text: '存到：' + (folder ? baseLabel(folder) : '下載') }));
  s.dest.title = (folder || '「下載」資料夾') + '（按一下換這台的別的資料夾）';
}

const baseLabel = (p) => { const parts = String(p).split('\\').filter(Boolean); return parts.length ? parts[parts.length - 1] : p; };

function destFolder() {
  try { return localStorage.getItem(FOLDER_KEY) || null; } catch { return null; }
}

function pickDestFolder() {
  const rid = nextRequestId('xf');
  folderPicks.set(rid, (path) => {
    if (!path) return;
    try { localStorage.setItem(FOLDER_KEY, path); } catch { /* 忽略 */ }
    if (session) renderDest(session);
  });
  post({ cmd: 'xferPickFolder', requestId: rid, initial: destFolder() });
}

/** 宿主的 xferFolder（xferPickFolder 的結果），回 true */
export function onXferFolder(m) {
  const done = m && folderPicks.get(m.requestId);
  if (!done) return false;
  folderPicks.delete(m.requestId);
  done(typeof m.path === 'string' ? m.path : null);
  return true;
}

// ───── 下載（deviceFetch → peerXferSave／瀏覽器下載） ─────

function queueDownloads(s, files) {
  for (const f of files) {
    transfers.push({ id: ++seq, dir: 'down', deviceId: s.deviceId, name: f.name, path: f.path, size: typeof f.size === 'number' ? f.size : null, state: 'queued', done: 0, total: f.size || 0 });
  }
  pump();
}

function startFetch(t) {
  t.state = 'fetching';
  const rid = nextRequestId('dx');
  t.requestId = rid;
  fetches.set(rid, t);
  const cid = post({ cmd: 'deviceFetch', deviceId: t.deviceId, requestId: rid, path: t.path });
  if (cid === null) { fetches.delete(rid); fail(t, '沒有送出：目前沒有連上伺服器'); return; }
  track(cid, {
    onSettle: (st, code) => {
      if (st === 'done' || t.state !== 'fetching') return;
      fetches.delete(rid);
      fail(t, failText(code, t.deviceId));
    },
  });
}

/** 宿主的 fileRef：deviceFetch 的回覆（requestId 是自己送的）才收，回 true */
export function onFileRef(m) {
  if (!m || typeof m.requestId !== 'string' || !fetches.has(m.requestId)) return false;
  const t = fetches.get(m.requestId);
  fetches.delete(m.requestId);
  if (t.state !== 'fetching') return true;
  if (m.error || typeof m.url !== 'string' || !m.url.startsWith('/f/')) { fail(t, m.error || '那台不給這個檔案（憑證、金鑰類的檔名不能下載）'); return true; }
  if (typeof m.size === 'number') t.total = m.size;
  if (isHub) {
    // 瀏覽器版：交給瀏覽器下載（間隔一點時間，瀏覽器才不會擋多個下載）
    const wait = Math.max(0, browserDownloadAt + R.BROWSER_DOWNLOAD_GAP_MS - Date.now());
    browserDownloadAt = Date.now() + wait;
    setTimeout(() => {
      const a = document.createElement('a');
      a.href = m.url;
      a.download = typeof m.name === 'string' ? m.name : t.name;
      a.rel = 'noopener';
      a.click();
      t.state = 'done';
      t.browser = true;
      t.done = t.total;
      renderXfers();
      pump();
    }, wait);
    return true;
  }
  t.state = 'running';
  t.transferId = peerId();
  post({ cmd: 'peerXferSave', transferId: t.transferId, url: m.url, name: typeof m.name === 'string' ? m.name : t.name, folder: destFolder() });
  renderXfers();
  return true;
}

/** 宿主的 xferProgress（peerXferSave 的進度與結果），回 true */
export function onXferProgress(m) {
  const t = m && transfers.find((x) => x.transferId && x.transferId === m.transferId);
  if (!t) return false;
  if (typeof m.done === 'number') t.done = m.done;
  if (typeof m.total === 'number' && m.total > 0) t.total = m.total;
  if (m.state === 'done') { t.state = 'done'; t.savedPath = m.path || null; t.savedName = m.name || null; }
  else if (m.state === 'failed') fail(t, m.error || '下載失敗');
  else if (m.state === 'cancelled') t.state = 'cancelled';
  renderXfers();
  if (m.state !== 'running') pump();
  return true;
}

// ───── 上傳（peerUpload／E4 → devicePut） ─────

function queueUploads(s, files) {
  const folder = s.path;
  if (!R.canUploadHere(folder)) return;
  for (const file of files) {
    const t = { id: ++seq, dir: 'up', deviceId: s.deviceId, name: file.name, size: file.size, file, folder, state: 'queued', done: 0, total: file.size };
    const problem = R.uploadProblem(file);
    if (problem) { t.state = 'failed'; t.error = problem; }
    transfers.push(t);
  }
  pump();
}

async function startUpload(t) {
  t.state = 'uploading';
  t.uploadId = isHub ? newId() : peerId();
  if (!t.uploadId) { fail(t, '這個環境不能上傳'); return; }
  renderXfers();
  if (isHub) {
    t.abort = new AbortController();
    const r = await uploadFile(t.uploadId, t.file, (d, tot) => { t.done = d; t.total = tot; renderXfersSoon(); }, t.abort.signal);
    finishUpload(t, r.status, r.json);
    return;
  }
  hostUploads.set(t.uploadId, t);
  postWithFiles({ cmd: 'peerUpload', uploadId: t.uploadId, deviceId: t.deviceId }, [t.file]);
}

/** 宿主的 peerUpload（桌面代傳的結果）：自己送的才收，回 true */
export function onPeerUpload(m) {
  const t = m && hostUploads.get(m.uploadId);
  if (!t) return false;
  hostUploads.delete(m.uploadId);
  let json = null;
  try { json = typeof m.json === 'string' && m.json ? JSON.parse(m.json) : null; } catch { json = null; }
  finishUpload(t, m.status, json);
  return true;
}

/** 宿主的 peerUploadProgress，回 true */
export function onPeerUploadProgress(m) {
  const t = m && hostUploads.get(m.uploadId);
  if (!t) return false;
  if (typeof m.sent === 'number') t.done = m.sent;
  if (typeof m.total === 'number') t.total = m.total;
  renderXfersSoon();
  return true;
}

function finishUpload(t, status, json) {
  if (t.state === 'cancelled') { pump(); return; }
  if (json && json.error === 'no_path') { fail(t, '這個檔案沒有本機路徑（請改用「上傳到這裡…」選檔）'); return; }
  const res = uploadResult(status, json, t.uploadId, t.name);
  if (!res.ok) { fail(t, res.error); return; }
  t.file = null;
  t.state = 'sending';
  renderXfers();
  const rid = nextRequestId('dp');
  const cid = post({ cmd: 'devicePut', deviceId: t.deviceId, requestId: rid, folder: t.folder, uploads: [res.entry] });
  if (cid === null) { fail(t, '沒有送出：目前沒有連上伺服器'); return; }
  track(cid, {
    onSettle: (st, code) => {
      if (t.state !== 'sending') return;
      if (st === 'done') {
        t.state = 'receiving';
        t.done = 0;
        t.total = t.size;
        t.sentAt = Date.now();
        renderXfers();
        pump();
        return;
      }
      fail(t, failText(code, t.deviceId));
    },
  });
}

/** deviceStats（每一則）：那台的 xfers 對上自己送的上傳；完成的檔若在開著的資料夾就重新整理 */
export function onStats(m) {
  const list = m && Array.isArray(m.devices) ? m.devices : [];
  let changed = false;
  let refresh = false;
  const now = Date.now();
  for (const d of list) {
    if (!d || typeof d.id !== 'string') continue;
    const xfers = R.parseXfers(d.xfers);
    for (const t of transfers) {
      if (t.dir !== 'up' || t.deviceId !== d.id || !t.uploadId) continue;
      const x = xfers.find((v) => v.id === t.uploadId);
      const patch = x ? R.applyXfer(t, x) : null;
      if (patch) {
        Object.assign(t, patch);
        changed = true;
        if (patch.state === 'saved' && session && session.deviceId === t.deviceId && session.path === t.folder) refresh = true;
      }
    }
  }
  for (const t of transfers) {
    if (t.state === 'receiving' && !t.late && t.sentAt && now - t.sentAt > R.PUT_REPORT_WAIT_MS && !t.done) { t.late = true; changed = true; }
  }
  if (changed) renderXfers();
  if (refresh && !session.loading) browse(session, session.path);
}

// ───── 傳輸清單 ─────

function pump() {
  for (const t of R.nextToStart(transfers, 'down')) startFetch(t);
  for (const t of R.nextToStart(transfers, 'up')) startUpload(t);
  renderXfers();
}

function fail(t, text) {
  t.state = 'failed';
  t.error = text;
  renderXfers();
  pump();
}

function cancel(t) {
  if (R.isFinished(t)) return;
  if (t.dir === 'down' && t.state === 'running' && t.transferId) { post({ cmd: 'peerXferCancel', transferId: t.transferId }); return; }
  if (t.dir === 'up' && t.state === 'uploading') {
    if (t.abort) t.abort.abort();
    else if (t.uploadId) post({ cmd: 'peerUploadCancel', uploadId: t.uploadId });
  }
  if (t.state === 'receiving' || t.state === 'sending') return; // 那台已經接下了：取消不了
  t.state = 'cancelled';
  t.file = null;
  renderXfers();
  pump();
}

function clearFinished() {
  for (let i = transfers.length - 1; i >= 0; i--) if (R.isFinished(transfers[i])) transfers.splice(i, 1);
}

let xferTimer = 0;
function renderXfersSoon() {
  if (xferTimer) return;
  xferTimer = setTimeout(() => { xferTimer = 0; renderXfers(); }, 200);
}

function renderXfers() {
  const s = session;
  if (!s || !s.xfers) return;
  const mine = transfers.filter((t) => t.deviceId === s.deviceId);
  setHidden(s.xferWrap, !mine.length);
  s.xfers.textContent = '';
  for (const t of mine.slice().reverse()) {
    const pct = R.percent(t.done, t.total);
    const bar = h('div', { class: 'df-bar-track' }, h('div', { class: 'df-bar-fill' }));
    bar.firstChild.style.transform = 'scaleX(' + ((pct ?? 0) / 100) + ')';
    const actions = h('span', { class: 'df-x-act' });
    if (!R.isFinished(t) && t.state !== 'receiving' && t.state !== 'sending') actions.append(iconBtn('x', '取消', () => cancel(t)));
    if (t.dir === 'down' && t.state === 'done' && t.transferId && !isHub) actions.append(iconBtn('folderOpen', '在檔案總管顯示', () => post({ cmd: 'peerXferReveal', transferId: t.transferId })));
    s.xfers.append(h('div', { class: 'df-x df-x-' + t.state, dataset: { dir: t.dir } },
      h('span', { class: 'df-x-ic' }, icon(t.dir === 'down' ? 'download' : 'arrowUp')),
      h('span', { class: 'df-x-name', text: t.name, title: t.dir === 'down' ? t.path : t.folder + '\\' + t.name }),
      h('span', { class: 'df-x-size', text: typeof t.size === 'number' ? fmtBytes(t.size) : '' }),
      h('span', { class: 'df-x-state', text: R.stateText(t, '「' + nameOf(t.deviceId) + '」') }),
      actions, bar));
  }
}
