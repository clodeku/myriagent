// 電腦狀態面板的檔案傳輸（sync-protocol 14.15）：右鍵（觸控是長按）點一台電腦，
// 瀏覽那台整台本機磁碟的資料夾與檔案（deviceBrowse）、多選下載（deviceFetch → 桌面 peerXferSave 寫進這台的資料夾／瀏覽器交給瀏覽器下載）、
// 上傳（桌面 peerUpload {deviceId}／瀏覽器 E4 → devicePut；那台接收的進度看 deviceStats 的 xfers）。
// 規則在 devicefilesrules.js；文字一律 textContent，class 一律 df- 前綴。
import { h, setHidden, fmtBytes, fmtRelative } from './util.js';
import { icon, FILE_ICON } from './icons.js';
import { state, post, postWithFiles, isHub, nextRequestId, newId, peerId } from './store.js';
import { openDialog, openMenu, toast, spinner } from './ui.js';
import { track } from './cmdtrack.js';
import { uploadFile, fetchTransfer } from './hubapi.js';
import { uploadResult, peerOf } from './liverules.js';
import * as R from './devicefilesrules.js';
import { openRemoteDesk } from './remotedesk.js';

const FOLDER_KEY = 'myriagent.app.xfer.folder.v1';

/** deviceBrowse 的 requestId → 對話框的工作階段 */
const browses = new Map();
/** deviceFetch 的 requestId → 傳輸 */
const fetches = new Map();
/** 桌面代傳的上傳（uploadId → 傳輸） */
const hostUploads = new Map();
/** xferPickFolder 的 requestId → 回呼 */
const folderPicks = new Map();
/** deviceTree 的 requestId → 資料夾的傳輸（kind 'folder'） */
const trees = new Map();
/** deviceFetchMany 的 requestId → 那一批的資料夾傳輸 */
const refBatches = new Map();
/** 全部的傳輸（依加入的順序；結束的留著直到清掉）。整個資料夾是一筆 kind 'folder'，底下的各檔帶 group（不另外列在清單上） */
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
    // 遠端桌面（2026-11-09）：條件同檔案傳輸（那台的「可以操作」）；版本太舊的由伺服器說明
    { label: '遠端桌面…', icon: 'desktop', disabled: !avail.enabled, desc: why, onSelect: () => openRemoteDesk(row.id, row.name || nameOf(row.id)) },
    // 檔案傳輸（2026-11-10）：所有電腦的傳輸，可以暫停、繼續、中止；有傳輸時才列
    transfers.length && { type: 'sep' },
    transfers.length && { label: '檔案傳輸…', icon: 'download', desc: R.dockText(R.dockSummary(summaryRows(Date.now()))) || '看傳輸的進度、暫停或中止', onSelect: () => openTransfers() },
  ].filter(Boolean);
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
  s.xferHead = h('div', { class: 'df-xfers-head' }, h('span', { text: '傳輸' }),
    h('span', { class: 'df-hint', text: '關掉這個視窗，傳輸會在背景繼續' }), h('span', { class: 'grow' }),
    h('button', { type: 'button', class: 'btn btn-sm', text: '清除已完成', onClick: () => { clearFinished(); renderXfers(); } }));
  s.xferWrap = h('div', { class: 'df-xfer-wrap' }, s.xferHead, s.xfers);
  s.fileInput = h('input', { type: 'file', multiple: true, hidden: true });
  s.fileInput.addEventListener('change', () => {
    const files = Array.from(s.fileInput.files || []);
    s.fileInput.value = '';
    if (files.length) queueUploads(s, files.map((file) => ({ file, rel: '' })));
  });
  // 整個資料夾的上傳（2026-11-09）：選一個資料夾，連子資料夾一起
  s.folderInput = h('input', { type: 'file', multiple: true, hidden: true });
  s.folderInput.setAttribute('webkitdirectory', '');
  s.folderInput.addEventListener('change', () => {
    const files = Array.from(s.folderInput.files || []);
    s.folderInput.value = '';
    if (files.length) queueUploads(s, files.map((file) => ({ file, rel: file.webkitRelativePath || file.name })));
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
    droppedFiles(e.dataTransfer).then((items) => { if (items.length) queueUploads(s, items); });
  });
  s.list.addEventListener('keydown', (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'a') { e.preventDefault(); s.selected = R.toggleAll(s.entries, s.selected); renderList(s); }
    else if (e.key === 'Backspace' && s.path) { e.preventDefault(); browse(s, s.parent); }
  });
  const body = h('div', { class: 'df-body' },
    h('div', { class: 'df-bar' }, s.home, s.up, s.input, s.refresh),
    s.crumbs, s.list, s.status, s.xferWrap, s.fileInput, s.folderInput);

  s.selInfo = h('span', { class: 'df-sel' });
  s.dest = h('button', { type: 'button', class: 'btn df-dest', title: '下載的檔案存在這台的哪個資料夾' });
  s.dest.addEventListener('click', () => pickDestFolder());
  s.uploadBtn = h('button', { type: 'button', class: 'btn' }, icon('arrowUp'), h('span', { text: '上傳到這裡…' }));
  s.uploadBtn.addEventListener('click', () => {
    if (!R.canUploadHere(s.path)) { pickUploads(); return; }
    openMenu(s.uploadBtn, [
      { label: '上傳檔案…', icon: 'file', onSelect: () => pickUploads() },
      { label: '上傳整個資料夾…', icon: 'folder', onSelect: () => pickUploads(true) },
    ], { placement: 'top-end', className: 'df-menu' });
  });
  s.downloadBtn = h('button', { type: 'button', class: 'btn primary' }, icon('download'), h('span', { class: 'df-dl-text', text: '下載' }));
  s.downloadBtn.addEventListener('click', () => {
    const files = R.selectedFiles(s.entries, s.selected);
    const folders = R.selectedFolders(s.entries, s.selected);
    if (!files.length && !folders.length) return;
    const go = (dirHandle) => {
      if (files.length) queueDownloads(s, files);
      for (const f of folders) queueFolderDownload(s, f, dirHandle);
      s.selected = new Set();
      renderList(s);
    };
    if (!folders.length || !isHub) { go(null); return; }
    // 瀏覽器版：整個資料夾要寫進使用者選的資料夾（Chrome、Edge；要在按鈕的點擊裡開選擇視窗）
    const problem = R.browserFolderDownloadProblem(typeof window.showDirectoryPicker === 'function');
    if (problem) { toast(problem, 'error'); return; }
    window.showDirectoryPicker({ mode: 'readwrite' }).then(go, () => { /* 按了取消 */ });
  });
  const footer = [s.selInfo, h('span', { class: 'grow' })];
  if (!isHub) footer.push(s.dest);
  footer.push(s.uploadBtn, s.downloadBtn);
  s.d = openDialog({
    title: '「' + nameOf(s.deviceId) + '」的檔案', body, footer, className: 'df-dlg', width: 860, initialFocus: s.list,
    // 關掉之後傳輸在背景繼續：右下角顯示摘要（renderDock）
    onClose: () => { if (session === s) session = null; s.d = null; renderXfers(); },
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

function pickUploads(folder = false) {
  const s = session;
  if (!s) return;
  if (!R.canUploadHere(s.path)) { toast('先開到要放檔案的資料夾（磁碟與常用資料夾、網路、電腦這幾層不能放檔案）', 'info'); return; }
  (folder ? s.folderInput : s.fileInput).click();
}

/**
 * 拖進清單的東西 → [{ file, rel }]：有資料夾時照 webkitGetAsEntry 逐層讀（rel 是含資料夾名稱的相對路徑），一般的檔 rel 是空的。
 * 拿不到項目時（舊瀏覽器）照 files
 */
async function droppedFiles(dt) {
  const items = Array.from((dt && dt.items) || []).filter((i) => i.kind === 'file');
  const entries = items.map((i) => (typeof i.webkitGetAsEntry === 'function' ? i.webkitGetAsEntry() : null));
  if (!entries.length || entries.some((e) => !e)) return Array.from((dt && dt.files) || []).map((file) => ({ file, rel: '' }));
  const out = [];
  const readAll = (reader) => new Promise((resolve) => {
    const all = [];
    const next = () => reader.readEntries((batch) => { if (!batch.length) resolve(all); else { all.push(...batch); next(); } }, () => resolve(all));
    next();
  });
  const walk = async (entry, prefix) => {
    if (entry.isFile) {
      const file = await new Promise((resolve) => entry.file(resolve, () => resolve(null)));
      if (file) out.push({ file, rel: prefix ? prefix + '/' + file.name : '' });
    } else if (entry.isDirectory) {
      const path = prefix ? prefix + '/' + entry.name : entry.name;
      for (const child of await readAll(entry.createReader())) await walk(child, path);
    }
  };
  for (const entry of entries) await walk(entry, '');
  return out;
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
    const box = h('input', { type: 'checkbox', class: 'df-check', checked, tabIndex: -1, 'aria-label': '選取 ' + e.name });
    const size = dir ? '' : (typeof e.size === 'number' ? fmtBytes(e.size) : '');
    const when = typeof e.modified === 'number' ? fmtRelative(e.modified) : '';
    row.append(box, h('span', { class: 'df-ic' }, icon(FILE_ICON[e.kind] || 'file')),
      h('span', { class: 'df-name', text: e.name }), h('span', { class: 'df-size', text: size }), h('span', { class: 'df-when', text: when }));
    row.addEventListener('click', (ev) => {
      const mode = ev.shiftKey ? 'range' : (ev.ctrlKey || ev.metaKey || ev.target === box) ? 'toggle' : 'plain';
      // 資料夾：直接點是進入；勾選框、Ctrl、Shift 是選（整個資料夾的下載）
      if (dir && mode === 'plain') { browse(s, e.path); return; }
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
  const folders = R.selectedFolders(s.entries, s.selected);
  const bytes = files.reduce((a, f) => a + (typeof f.size === 'number' ? f.size : 0), 0);
  const parts = [];
  if (folders.length) parts.push(folders.length + ' 個資料夾');
  if (files.length) parts.push(files.length + ' 個檔案（' + fmtBytes(bytes) + '）');
  s.selInfo.textContent = parts.length ? '已選 ' + parts.join('、') : '點檔案選取，勾選資料夾可整個下載；Shift 連選、Ctrl 加選，雙擊直接下載';
  s.downloadBtn.disabled = !files.length && !folders.length;
  s.downloadBtn.querySelector('.df-dl-text').textContent = parts.length ? '下載 ' + (folders.length ? folders.length + ' 個資料夾' + (files.length ? '與 ' : '') : '') + (files.length ? files.length + ' 個檔案' : '') : '下載';
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
  dockDismissed = false;
  for (const f of files) {
    transfers.push({ id: ++seq, dir: 'down', deviceId: s.deviceId, name: f.name, path: f.path, size: typeof f.size === 'number' ? f.size : null, state: 'queued', done: 0, total: f.size || 0 });
  }
  pump();
}

function startFetch(t) {
  if (t.url) { saveDownload(t, t.url, t.name); return; }
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
  if (typeof m.name === 'string') t.name = m.name;
  // 要網址的期間按了暫停：拿到網址先停著，繼續時照常開始
  if (t.hold) { t.url = m.url; t.hold = false; t.state = 'paused'; t.pauseMode = 'hold'; renderXfers(); pump(); return true; }
  saveDownload(t, m.url, t.name);
  return true;
}

/** 拿到 /f/ 網址之後存下來：桌面交給宿主（peerXferSave，資料夾裡的檔帶 subfolder）；瀏覽器版的資料夾寫進選的資料夾，單檔交給瀏覽器下載 */
function saveDownload(t, url, name) {
  if (isHub && t.dirHandle) { writeToDirectory(t, url, name); return; }
  if (isHub) {
    // 瀏覽器版：交給瀏覽器下載（間隔一點時間，瀏覽器才不會擋多個下載）
    const wait = Math.max(0, browserDownloadAt + R.BROWSER_DOWNLOAD_GAP_MS - Date.now());
    browserDownloadAt = Date.now() + wait;
    setTimeout(() => {
      const a = document.createElement('a');
      a.href = url;
      a.download = name;
      a.rel = 'noopener';
      a.click();
      t.state = 'done';
      t.browser = true;
      t.done = t.total;
      renderXfers();
      pump();
    }, wait);
    return;
  }
  t.state = 'running';
  t.transferId = peerId();
  const msg = { cmd: 'peerXferSave', transferId: t.transferId, url, name, folder: destFolder() };
  if (t.subfolder) msg.subfolder = t.subfolder;
  post(msg);
  renderXfers();
}

/**
 * 瀏覽器版：把資料夾裡的一個檔寫進選的資料夾（照相對路徑建子資料夾；同名加「 (2)」）。一段一段（≤8 MB 的 Range）要，
 * 每段核對起點、總長與 ETag；暫停（t.pauseRequested）時把已經寫的留著，繼續時從 t.got 接著要（2026-11-10）。中止或失敗刪掉寫到一半的檔
 */
async function writeToDirectory(t, url, name) {
  t.state = 'running';
  t.abortCtl = new AbortController();
  renderXfersSoon();
  let writable = null;
  try {
    let dir = t.dirHandle;
    for (const part of String(t.subfolder || '').split('\\').filter(Boolean)) dir = await dir.getDirectoryHandle(part, { create: true });
    t.dirResolved = dir;
    if (!t.finalName) {
      const exists = async (n) => { try { await dir.getFileHandle(n); return true; } catch { return false; } };
      t.finalName = await R.uniqueBrowserName(name, exists);
      if (!t.finalName) throw new Error('同名的檔太多');
    }
    const handle = await dir.getFileHandle(t.finalName, { create: true });
    const got = t.got || 0;
    // 接著下載：保留已經寫的、截到確實寫進去的長度，從尾端接著寫
    writable = await handle.createWritable({ keepExistingData: got > 0 });
    if (got > 0) { await writable.truncate(got); await writable.seek(got); }
    const w = writable;
    const fin = await R.streamSegments({
      got, total: t.total, etag: t.etag, signal: t.abortCtl.signal,
      fetchRange: (range, signal) => fetchTransfer(url, signal, range),
      write: (chunk) => w.write(chunk),
      onProgress: (g, total, tag) => { t.got = g; t.done = g; t.total = total; t.etag = tag; renderXfersSoon(); },
    });
    t.total = fin.total;
    await writable.close();
    writable = null;
    t.state = 'done';
    t.done = t.total;
  } catch (e) {
    if (writable) { try { await writable.close(); } catch { /* 已經關了 */ } }
    if (t.pauseRequested) {
      t.pauseRequested = false;
      t.state = 'paused';
      t.pauseMode = 'stream';
    } else {
      if (t.state !== 'cancelled') { t.state = 'failed'; t.error = '存不進選的資料夾：' + (e && e.message ? e.message : '寫入失敗'); }
      removePartial(t);
    }
  }
  t.abortCtl = null;
  renderXfers();
  pump();
}

/** 瀏覽器版：刪掉寫到一半的檔（中止、失敗） */
function removePartial(t) {
  if (!t.dirResolved || !t.finalName) return;
  const dir = t.dirResolved;
  const name = t.finalName;
  t.got = 0;
  t.finalName = null;
  dir.removeEntry(name).catch(() => { /* 已經沒有了 */ });
}

// ───── 整個資料夾的下載（deviceTree → deviceFetchMany → 各檔照上面的方式存） ─────

function queueFolderDownload(s, folder, dirHandle) {
  dockDismissed = false;
  const g = { id: ++seq, kind: 'folder', dir: 'down', deviceId: s.deviceId, name: folder.name, path: folder.path, state: 'listing', dirHandle };
  transfers.push(g);
  const rid = nextRequestId('dt');
  trees.set(rid, g);
  const cid = post({ cmd: 'deviceTree', deviceId: s.deviceId, requestId: rid, path: folder.path });
  if (cid === null) { trees.delete(rid); failGroup(g, '沒有送出：目前沒有連上伺服器'); return; }
  track(cid, { onSettle: (st, code) => { if (st !== 'done' && trees.delete(rid)) failGroup(g, failText(code, s.deviceId)); } });
  renderXfers();
}

function failGroup(g, text) {
  g.state = 'failed';
  g.error = text;
  renderXfers();
}

/** 宿主的 fileTree（deviceTree 的回覆），回 true */
export function onFileTree(m) {
  const g = m && trees.get(m.requestId);
  if (!g) return false;
  trees.delete(m.requestId);
  if (m.error || typeof m.root !== 'string') { failGroup(g, m.error || '列不出這個資料夾'); return true; }
  const files = Array.isArray(m.files) ? m.files.filter((f) => f && typeof f.path === 'string') : [];
  g.root = m.root;
  g.truncated = !!m.truncated;
  g.state = 'running';
  const folderName = typeof m.name === 'string' && m.name ? m.name : g.name;
  if (!files.length) { g.state = 'done'; g.empty = true; renderXfers(); return true; }
  const children = files.map((f) => {
    const { name } = R.splitRelative(f.path);
    const t = { id: ++seq, group: g.id, dir: 'down', deviceId: g.deviceId, name, rel: f.path, path: m.root + '\\' + f.path, size: f.size, total: f.size, done: 0,
      state: 'waiting', subfolder: R.downloadSubfolder(folderName, f.path), dirHandle: g.dirHandle };
    transfers.push(t);
    return t;
  });
  for (const batch of R.chunk(children, R.FETCH_MANY_BATCH)) requestRefs(g, batch);
  renderXfers();
  return true;
}

function requestRefs(g, batch) {
  const rid = nextRequestId('dm');
  refBatches.set(rid, { g, batch });
  const cid = post({ cmd: 'deviceFetchMany', deviceId: g.deviceId, requestId: rid, root: g.root, pathsJson: JSON.stringify(batch.map((t) => t.rel)) });
  const failAll = (text) => { for (const t of batch) if (t.state === 'waiting') { t.state = 'failed'; t.error = text; } renderXfers(); };
  if (cid === null) { refBatches.delete(rid); failAll('沒有送出：目前沒有連上伺服器'); return; }
  track(cid, { onSettle: (st, code) => { if (st !== 'done' && refBatches.delete(rid)) failAll(failText(code, g.deviceId)); } });
}

/** 宿主的 fileRefs（deviceFetchMany 的回覆）：對上相對路徑，拿到網址的排進下載，回 true */
export function onFileRefs(m) {
  const entry = m && refBatches.get(m.requestId);
  if (!entry) return false;
  refBatches.delete(m.requestId);
  const byRel = new Map((Array.isArray(m.refs) ? m.refs : []).filter((r) => r && typeof r.path === 'string').map((r) => [r.path, r]));
  for (const t of entry.batch) {
    if (t.state !== 'waiting') continue;
    const r = byRel.get(t.rel);
    if (r && typeof r.url === 'string' && r.url.startsWith('/f/')) {
      t.url = r.url;
      if (typeof r.size === 'number') { t.size = r.size; t.total = r.size; }
      // 整個資料夾按了暫停：拿到網址的也先停著
      if (t.hold) { t.hold = false; t.state = 'paused'; t.pauseMode = 'hold'; } else t.state = 'queued';
    } else {
      t.state = 'failed';
      t.error = (r && r.error) || '那台不給這個檔案';
    }
  }
  pump();
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
  // 暫停（宿主保留下載到一半的檔）；繼續被擋（同時下載太多）時也回 paused 並帶說明
  else if (m.state === 'paused') { t.state = 'paused'; t.pauseMode = 'host'; t.pausing = false; if (m.error) toast(m.error, 'info'); }
  else if (m.state === 'running' && t.state === 'paused') t.state = 'running';
  renderXfers();
  if (m.state !== 'running') pump();
  return true;
}

// ───── 上傳（peerUpload／E4 → devicePut） ─────

/** items：[{ file, rel }]（rel 是資料夾裡的相對路徑，例如 photos/2024/a.jpg；一般的檔是空的） */
function queueUploads(s, items) {
  const folder = s.path;
  if (!R.canUploadHere(folder)) return;
  dockDismissed = false;
  // 同一個最上層資料夾的歸成一筆
  const groups = new Map();
  for (const { file, rel } of items) {
    const sub = rel ? R.uploadSubfolder(rel) : '';
    let group = null;
    if (sub) {
      const top = sub.split('\\')[0];
      group = groups.get(top);
      if (!group) {
        group = { id: ++seq, kind: 'folder', dir: 'up', deviceId: s.deviceId, name: top, folder, state: 'running' };
        groups.set(top, group);
        transfers.push(group);
      }
    }
    const t = { id: ++seq, group: group ? group.id : undefined, dir: 'up', deviceId: s.deviceId, name: file.name, size: file.size, file, folder, subfolder: sub, state: 'queued', done: 0, total: file.size };
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
  if (!postWithFiles({ cmd: 'peerUpload', uploadId: t.uploadId, deviceId: t.deviceId }, [t.file])) {
    // 不在磁碟上的檔（網頁拖來的圖片）宿主拿不到路徑：說明，不一直等宿主的回覆（2026-11-13）
    hostUploads.delete(t.uploadId);
    fail(t, '這個檔案不在磁碟上，請先存成檔案再上傳');
  }
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
  // 中止或暫停（上傳停下來；繼續時這個檔從頭上傳）的結果不理
  if (t.state === 'cancelled' || t.state === 'paused') { pump(); return; }
  if (json && json.error === 'no_path') { fail(t, '這個檔案沒有本機路徑（請改用「上傳到這裡…」選檔）'); return; }
  const res = uploadResult(status, json, t.uploadId, t.name);
  if (!res.ok) { fail(t, res.error); return; }
  t.file = null;
  t.state = 'sending';
  renderXfers();
  const rid = nextRequestId('dp');
  const put = { cmd: 'devicePut', deviceId: t.deviceId, requestId: rid, folder: t.folder, uploads: [res.entry] };
  if (t.subfolder) put.subfolder = t.subfolder;
  const cid = post(put);
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
  if (t.kind === 'folder') {
    t.cancelled = true;
    t.paused = false;
    for (const c of transfers) if (c.group === t.id && !R.isFinished(c)) { if (c.state === 'waiting') c.state = 'cancelled'; else cancel(c); }
    if (t.state === 'listing') t.state = 'cancelled';
    renderXfers();
    return;
  }
  if (R.isFinished(t)) return;
  // 桌面的下載（下載中或暫停中）交給宿主：它刪掉暫存檔、回 cancelled
  if (t.dir === 'down' && t.transferId && (t.state === 'running' || (t.state === 'paused' && t.pauseMode === 'host'))) {
    post({ cmd: 'peerXferCancel', transferId: t.transferId });
    return;
  }
  if (t.dir === 'up' && t.state === 'uploading') {
    if (t.abort) t.abort.abort();
    else if (t.uploadId) post({ cmd: 'peerUploadCancel', uploadId: t.uploadId });
  }
  if (t.state === 'receiving' || t.state === 'sending') return; // 那台已經接下了：取消不了
  const wasStreaming = t.state === 'running' && t.abortCtl;
  t.state = 'cancelled';
  t.file = null;
  t.hold = false;
  // 瀏覽器版寫進資料夾的：下載中的停下來（收尾時刪掉寫到一半的檔），暫停中的直接刪
  if (wasStreaming) t.abortCtl.abort();
  else removePartial(t);
  renderXfers();
  pump();
}

/** 暫停一筆或整個資料夾（devicefilesrules.pauseMode：還沒開始的停著、桌面下載交給宿主、瀏覽器的資料夾檔停在目前的位置、上傳停下來之後從頭來） */
function pause(t) {
  if (t.kind === 'folder') {
    t.paused = true;
    for (const c of childrenOf(t)) if (R.pauseMode(c, isHub)) pause(c);
    renderXfers();
    pump();
    return;
  }
  const mode = R.pauseMode(t, isHub);
  if (!mode) return;
  if (mode === 'hold') {
    // 等網址的（資料夾裡的、向那台要檔案中）拿到網址才停；排隊中的直接停
    if (t.state === 'queued') { t.state = 'paused'; t.pauseMode = 'hold'; } else t.hold = true;
  } else if (mode === 'host') {
    t.pausing = true;
    post({ cmd: 'peerXferPause', transferId: t.transferId });
  } else if (mode === 'stream') {
    t.pauseRequested = true;
    if (t.abortCtl) t.abortCtl.abort();
  } else if (mode === 'restart') {
    t.state = 'paused';
    t.pauseMode = 'restart';
    if (t.abort) t.abort.abort();
    else if (t.uploadId) { hostUploads.delete(t.uploadId); post({ cmd: 'peerUploadCancel', uploadId: t.uploadId }); }
  }
  renderXfers();
  pump();
}

/** 繼續一筆或整個資料夾：桌面的下載請宿主接著下載，其他排回佇列（上傳從頭來、瀏覽器的資料夾檔從停下的位置接著要） */
function resume(t) {
  if (t.kind === 'folder') {
    t.paused = false;
    for (const c of childrenOf(t)) { c.hold = false; if (c.state === 'paused') resume(c); }
    renderXfers();
    pump();
    return;
  }
  t.hold = false;
  if (t.state !== 'paused') return;
  if (t.pauseMode === 'host' && t.transferId) {
    t.state = 'running';
    post({ cmd: 'peerXferResume', transferId: t.transferId });
  } else {
    if (t.pauseMode === 'restart') { t.done = 0; t.uploadId = null; t.abort = null; }
    t.state = 'queued';
  }
  t.pauseMode = null;
  renderXfers();
  pump();
}

function clearFinished() {
  const doneGroups = new Set(transfers.filter((t) => t.kind === 'folder' && groupFinished(t)).map((t) => t.id));
  for (let i = transfers.length - 1; i >= 0; i--) {
    const t = transfers[i];
    if (t.kind === 'folder' ? doneGroups.has(t.id) : (t.group === undefined ? R.isFinished(t) : doneGroups.has(t.group))) transfers.splice(i, 1);
  }
}

const childrenOf = (g) => transfers.filter((c) => c.group === g.id);
const groupFinished = (g) => ['done', 'failed', 'cancelled'].includes(R.folderProgress(g, childrenOf(g)).state);

let xferTimer = 0;
function renderXfersSoon() {
  if (xferTimer) return;
  xferTimer = setTimeout(() => { xferTimer = 0; renderXfers(); }, 200);
}

// ───── 速度、經過與尚需時間（2026-11-10） ─────

/** 一筆傳輸的速度與時間：順便更新它的計時（排隊、暫停不算）與最近的進度樣本 */
function metricsOf(t, now) {
  t.clock = R.tickClock(t.clock, t.state, now);
  if (R.RATE_STATES.includes(t.state)) {
    if (t.sampledDone !== t.done || t.sampledState !== t.state) {
      // 換了階段（上傳到伺服器 → 那台接收）進度從 0 重來：樣本也重來
      t.samples = R.pushSample(t.sampledState === t.state ? t.samples : [], now, t.done ?? 0);
      t.sampledDone = t.done;
      t.sampledState = t.state;
    }
  } else {
    t.samples = [];
    t.sampledDone = undefined;
    t.sampledState = undefined;
  }
  const rate = R.RATE_STATES.includes(t.state) ? R.rateOf(t.samples, now) : null;
  return { rate, elapsed: R.elapsedMs(t.clock, now), remaining: R.remainingMs(t.done, t.total, rate) };
}

/** 資料夾：底下各檔合計的位元組進度、有任何一個在傳就計時 */
function folderMetrics(g, p, children, now) {
  let rate = null;
  for (const c of children) {
    const m = metricsOf(c, now);
    if (m.rate !== null) rate = (rate ?? 0) + m.rate;
  }
  const active = children.some((c) => R.CLOCK_STATES.includes(c.state));
  g.clock = R.tickClock(g.clock, active ? 'running' : 'queued', now);
  return { rate, elapsed: R.elapsedMs(g.clock, now), remaining: R.remainingMs(p.done, p.total, rate) };
}

/** 資料夾底下還沒結束的都暫停了（含等網址時按了暫停的） */
function folderPaused(g, children) {
  const open = children.filter((c) => !R.isFinished(c));
  return open.length > 0 && open.every((c) => c.state === 'paused' || (c.hold && (c.state === 'waiting' || c.state === 'fetching')));
}

// ───── 傳輸清單（檔案對話框裡這台的、「檔案傳輸」視窗裡全部的） ─────

function renderXfers() {
  const now = Date.now();
  const s = session;
  if (s && s.xfers) {
    const mine = transfers.filter((t) => t.deviceId === s.deviceId && t.group === undefined);
    setHidden(s.xferWrap, !mine.length);
    s.xfers.textContent = '';
    for (const t of mine.slice().reverse()) s.xfers.append(t.kind === 'folder' ? folderRow(t, now, false) : fileRow(t, now, false));
  }
  if (panel && panel.list) {
    const all = transfers.filter((t) => t.group === undefined);
    panel.list.textContent = '';
    if (!all.length) panel.list.append(h('div', { class: 'df-x-empty', text: '沒有傳輸' }));
    for (const t of all.slice().reverse()) panel.list.append(t.kind === 'folder' ? folderRow(t, now, true) : fileRow(t, now, true));
  }
  renderDock(now);
  scheduleTick();
}

/** 一列的按鈕：暫停／繼續、中止（可以的才有）、桌面下載完的在檔案總管顯示 */
function rowActions(t, children) {
  const actions = h('span', { class: 'df-x-act' });
  const folder = t.kind === 'folder';
  if (R.canResume(t, children)) actions.append(iconBtn('play', folder ? '繼續整個資料夾' : '繼續', () => resume(t)));
  else if (R.canPause(t, isHub, children) && !t.pausing) actions.append(iconBtn('pause', folder ? '暫停整個資料夾' : (t.dir === 'up' ? '暫停（繼續時這個檔從頭上傳）' : '暫停'), () => pause(t)));
  const finished = folder ? ['done', 'failed', 'cancelled'].includes(R.folderProgress(t, children).state) : R.isFinished(t);
  if (!finished && t.state !== 'receiving' && t.state !== 'sending') actions.append(iconBtn('x', folder ? '中止整個資料夾' : '中止', () => cancel(t)));
  if (!folder && t.dir === 'down' && t.state === 'done' && t.transferId && !isHub) actions.append(iconBtn('folderOpen', '在檔案總管顯示', () => post({ cmd: 'peerXferReveal', transferId: t.transferId })));
  return actions;
}

function progressBar(pct) {
  const b = h('div', { class: 'df-bar-track' }, h('div', { class: 'df-bar-fill' }));
  b.firstChild.style.transform = 'scaleX(' + ((pct ?? 0) / 100) + ')';
  return b;
}

const BS = String.fromCharCode(92);

/** 一個檔那一列。withDevice：「檔案傳輸」視窗裡全部電腦的，名稱前面標是哪台 */
function fileRow(t, now, withDevice) {
  const m = metricsOf(t, now);
  const state = t.pausing ? '暫停中…' : R.stateText(t, '「' + nameOf(t.deviceId) + '」');
  return h('div', { class: 'df-x df-x-' + t.state, dataset: { dir: t.dir } },
    h('span', { class: 'df-x-ic' }, icon(t.dir === 'down' ? 'download' : 'arrowUp')),
    h('span', { class: 'df-x-name', text: (withDevice ? nameOf(t.deviceId) + '：' : '') + t.name, title: t.dir === 'down' ? t.path : t.folder + BS + (t.subfolder ? t.subfolder + BS : '') + t.name }),
    h('span', { class: 'df-x-size', text: typeof t.size === 'number' ? fmtBytes(t.size) : '' }),
    h('span', { class: 'df-x-state', text: state }),
    rowActions(t, null),
    h('span', { class: 'df-x-metrics', text: R.metricsText({ state: t.state, ...m }) }),
    progressBar(R.percent(t.done, t.total)));
}

/** 整個資料夾那一列：第幾個檔／共幾個、合計的速度與時間、位元組的進度條；可以暫停、繼續、中止還沒做完的 */
function folderRow(g, now, withDevice) {
  const children = childrenOf(g);
  const p = R.folderProgress(g, children);
  const pct = p.total > 0 ? R.percent(p.done, p.total) : (p.files ? R.percent(p.finished, p.files) : null);
  const paused = p.state === 'running' && folderPaused(g, children);
  const m = folderMetrics(g, p, children, now);
  const text = g.empty ? '資料夾是空的' : paused ? '已暫停・' + p.finished + ' / ' + p.files + ' 個檔' : R.folderText(g, p);
  const cls = paused ? 'paused' : p.state === 'listing' ? 'queued' : p.state === 'running' ? 'running' : p.state;
  return h('div', { class: 'df-x df-x-' + cls, dataset: { dir: g.dir, kind: 'folder' } },
    h('span', { class: 'df-x-ic' }, icon('folder')),
    h('span', { class: 'df-x-name', text: (withDevice ? nameOf(g.deviceId) + '：' : '') + g.name, title: g.dir === 'down' ? (g.path || '') : (g.folder || '') + BS + g.name }),
    h('span', { class: 'df-x-size', text: p.total ? fmtBytes(p.total) : '' }),
    h('span', { class: 'df-x-state', text }),
    rowActions(g, children),
    h('span', { class: 'df-x-metrics', text: R.metricsText({ state: paused ? 'paused' : p.state === 'running' ? 'running' : p.state, ...m }) }),
    progressBar(pct));
}

// ───── 背景傳輸：檔案對話框關著時右下角的摘要，點了開「檔案傳輸」視窗（2026-11-10） ─────

let dock = null;
let panel = null;
/** 背景的傳輸都結束了：摘要改成「傳輸完成」留著，直到點掉 */
let dockFinished = null;
/** 使用者關掉了摘要：這一批傳輸不再顯示（有新的傳輸加進來才再出現） */
let dockDismissed = false;

/** 摘要的列：一筆傳輸（資料夾算一筆）的狀態、進度、速度與尚需時間 */
function summaryRows(now) {
  return transfers.filter((t) => t.group === undefined).map((t) => {
    if (t.kind !== 'folder') {
      const m = metricsOf(t, now);
      return { state: t.state, done: t.done, total: t.total, rate: m.rate, remaining: m.remaining };
    }
    const children = childrenOf(t);
    const p = R.folderProgress(t, children);
    const m = folderMetrics(t, p, children, now);
    const state = p.state === 'running' && folderPaused(t, children) ? 'paused' : p.state === 'listing' ? 'queued' : p.state;
    return { state, done: p.done, total: p.total, rate: m.rate, remaining: m.remaining };
  });
}

function renderDock(now) {
  const rows = summaryRows(now);
  const sum = R.dockSummary(rows);
  if (sum) dockFinished = null;
  else if (dock && !dock.hidden && !dockFinished && rows.length) {
    const failed = rows.filter((r) => r.state === 'failed').length;
    dockFinished = '傳輸完成' + (failed ? '（' + failed + ' 個失敗）' : '');
  }
  // 檔案對話框或「檔案傳輸」視窗開著時不必另外顯示；使用者關掉的這一批不再顯示
  const show = !session && !panel && !dockDismissed && (sum || dockFinished);
  if (!show) { if (dock) setHidden(dock, true); return; }
  if (!dock) {
    dock = h('div', { class: 'df-dock', role: 'status', 'aria-live': 'polite' },
      h('button', { type: 'button', class: 'df-dock-main', title: '開啟檔案傳輸（可以暫停、繼續、中止）', onClick: () => openTransfers() },
        h('span', { class: 'df-dock-ic' }, icon('download')), h('span', { class: 'df-dock-text' }), progressBar(0)),
      h('button', { type: 'button', class: 'df-dock-close', title: '關閉（傳輸照常在背景進行）', 'aria-label': '關閉', onClick: () => { dockDismissed = true; dockFinished = null; setHidden(dock, true); } }, icon('x')));
    document.body.append(dock);
  }
  setHidden(dock, false);
  dock.classList.toggle('df-dock-done', !sum);
  dock.querySelector('.df-dock-text').textContent = sum ? R.dockText(sum) : dockFinished;
  dock.querySelector('.df-bar-fill').style.transform = 'scaleX(' + ((sum ? sum.pct ?? 0 : 100) / 100) + ')';
}

/** 「檔案傳輸」視窗：所有電腦的傳輸，可以暫停、繼續、中止、清除已完成 */
export function openTransfers() {
  if (panel && panel.d) return;
  dockFinished = null;
  const list = h('div', { class: 'df-xfers df-xfers-all' });
  const clear = h('button', { type: 'button', class: 'btn', text: '清除已完成', onClick: () => { clearFinished(); renderXfers(); } });
  const p = { list };
  panel = p;
  p.d = openDialog({
    title: '檔案傳輸', className: 'df-dlg df-xfer-dlg', width: 720,
    body: h('div', { class: 'df-body' }, h('div', { class: 'df-hint', text: '關掉這個視窗，傳輸會在背景繼續，右下角會顯示進度' }), list),
    footer: [h('span', { class: 'grow' }), clear],
    onClose: () => { if (panel === p) panel = null; renderXfers(); },
  });
  renderXfers();
}

/** 有傳輸在動時每秒重畫一次（經過時間、尚需時間、速度在沒有新進度時也要更新）；都停了就不排 */
let tickTimer = 0;
function scheduleTick() {
  const active = transfers.some((t) => R.CLOCK_STATES.includes(t.state) || t.state === 'listing');
  if (!active) {
    if (tickTimer) { clearTimeout(tickTimer); tickTimer = 0; }
    return;
  }
  if (tickTimer) return;
  tickTimer = setTimeout(() => { tickTimer = 0; renderXfers(); }, 1000);
}
