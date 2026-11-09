// 電腦狀態面板的檔案傳輸（sync-protocol 14.15）的純規則：右鍵選單能不能用、路徑列、選取、排程、進度文字、
// 那台回報的接收進度（deviceStats 的 xfers）。不碰 DOM，tools/ui-probe/devicefilesrules.test.mjs 測

/** 同時跑幾個下載、幾個上傳 */
export const MAX_ACTIVE_DOWNLOADS = 3;
export const MAX_ACTIVE_UPLOADS = 2;

/** 單檔上傳上限（伺服器 E4 的 512 MB） */
export const MAX_UPLOAD_BYTES = 512 * 1024 * 1024;

/** 瀏覽器版連續交給瀏覽器下載時，每個之間隔多久（太密瀏覽器會擋「多個下載」） */
export const BROWSER_DOWNLOAD_GAP_MS = 400;

/** 送出 devicePut 之後，多久沒在那台的狀態裡看到這個檔就提示「還沒有回報」 */
export const PUT_REPORT_WAIT_MS = 90_000;

const isObj = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const str = (v) => (typeof v === 'string' && v ? v : null);
const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);

/**
 * 電腦卡片的右鍵選單：這台自己與 Hub 主機沒有選單（回 null）；其他電腦回 { enabled, reason }。
 * 離線、政策不是「可以操作」、桌面沒有連上伺服器（peer 不是 'live'）時停用並說明原因。
 * row：monitorrules.buildRows 的一列；device：state.remote.devices 裡那台；peer：liverules.peerOf（瀏覽器版是 'off'）
 */
export function transferMenu({ row, device, hub, peer }) {
  if (!isObj(row) || row.self || row.hub || !str(row.id)) return null;
  if (!hub && peer !== 'live') return { enabled: false, reason: peer === 'unsupported' ? '這台或伺服器的版本還不支援，請先更新' : '目前沒有連上伺服器' };
  if (!row.online) return { enabled: false, reason: '那台電腦離線' };
  const level = isObj(device) ? device.remoteControlLevel : null;
  if (level === 'Off' || level === 'View') return { enabled: false, reason: '那台的遠端操作政策不是「可以操作」（要在那台的設定頁改）' };
  return { enabled: true, reason: null };
}

/** 網路芳鄰：「網路」這一層的路徑 */
export const NETWORK_ROOT = '\\\\';

/** 網路路徑（「網路」、\\電腦、\\電腦\分享\…） */
export const isNetworkPath = (p) => typeof p === 'string' && p.startsWith(NETWORK_ROOT);

/**
 * 使用者輸入的路徑：去掉前後空白與引號、斜線換成反斜線、「C:」補成「C:\」、資料夾結尾的反斜線去掉（磁碟根目錄除外）；空的回 null。
 * 網路路徑留著開頭的 \\、中間重複的反斜線合成一個；只有 \\ 是「網路」這一層
 */
export function normalizePath(input) {
  if (typeof input !== 'string') return null;
  let p = input.trim().replace(/^"+|"+$/g, '').trim().replace(/\//g, '\\');
  if (!p) return null;
  if (p.startsWith(NETWORK_ROOT)) return NETWORK_ROOT + p.slice(2).split('\\').filter(Boolean).join('\\');
  if (/^[A-Za-z]:$/.test(p)) p += '\\';
  if (/^[A-Za-z]:\\$/.test(p)) return p[0].toUpperCase() + ':\\';
  p = p.replace(/\\+$/, '');
  if (/^[a-z]:/.test(p)) p = p[0].toUpperCase() + p.slice(1);
  return p;
}

/** 路徑列：[{ label, path }]，第一段是磁碟（「C:」→「C:\」）；null（虛擬根）是 [] */
export function crumbs(path) {
  const p = normalizePath(path);
  if (p && isNetworkPath(p)) {
    // 網路芳鄰：網路 → 電腦 → 分享 → …
    const out = [{ label: '網路', path: NETWORK_ROOT }];
    let acc = NETWORK_ROOT;
    for (const part of p.slice(2).split('\\').filter(Boolean)) {
      acc = acc === NETWORK_ROOT ? acc + part : acc + '\\' + part;
      out.push({ label: part, path: acc });
    }
    return out;
  }
  if (!p || !/^[A-Za-z]:\\/.test(p)) return [];
  const parts = p.slice(3).split('\\').filter(Boolean);
  const out = [{ label: p.slice(0, 2), path: p.slice(0, 3) }];
  let acc = p.slice(0, 3);
  for (const part of parts) {
    acc = acc.endsWith('\\') ? acc + part : acc + '\\' + part;
    out.push({ label: part, path: acc });
  }
  return out;
}

/** 這一層能不能放檔案：磁碟與常用資料夾、「網路」、\\電腦（分享的清單）都不行，要開到資料夾（本機或 \\電腦\分享 以下） */
export function canUploadHere(path) {
  if (typeof path !== 'string' || !path) return false;
  if (!isNetworkPath(path)) return true;
  return path.slice(2).split('\\').filter(Boolean).length >= 2;
}

/** 磁碟根目錄（「X:\」） */
export const isDriveRoot = (p) => typeof p === 'string' && /^[A-Za-z]:\\$/.test(p);

/** 清單排序：資料夾在前，再依名稱（中文、數字自然排序） */
export function sortEntries(entries) {
  const list = Array.isArray(entries) ? entries.filter((e) => isObj(e) && str(e.name) && str(e.path)) : [];
  const coll = new Intl.Collator('zh-Hant-TW', { numeric: true, sensitivity: 'base' });
  return list.slice().sort((a, b) => {
    const da = a.kind === 'dir' ? 0 : 1;
    const db = b.kind === 'dir' ? 0 : 1;
    return da - db || coll.compare(a.name, b.name);
  });
}

/**
 * 點選一列（清單的索引）之後的選取：plain＝只選這一列；toggle（Ctrl、勾選框）＝加入或拿掉；range（Shift）＝從錨點到這一列。
 * 資料夾也可以選（整個資料夾的傳輸，2026-11-09）：資料夾直接點是進入（畫面處理、不經這裡），Ctrl、Shift 與勾選框才選。回 { selected: Set<path>, anchor }
 */
export function selectAt(entries, selected, anchor, index, mode) {
  const list = Array.isArray(entries) ? entries : [];
  const target = list[index];
  const next = new Set(selected instanceof Set ? selected : []);
  if (!target || (target.kind === 'dir' && mode === 'plain')) return { selected: next, anchor };
  if (mode === 'range' && typeof anchor === 'number' && list[anchor]) {
    const [a, b] = anchor < index ? [anchor, index] : [index, anchor];
    for (let i = a; i <= b; i++) if (list[i]) next.add(list[i].path);
    return { selected: next, anchor };
  }
  if (mode === 'toggle') {
    if (next.has(target.path)) next.delete(target.path); else next.add(target.path);
    return { selected: next, anchor: index };
  }
  return { selected: new Set([target.path]), anchor: index };
}

/** 全選（檔案與資料夾）；已經全選了就全部取消 */
export function toggleAll(entries, selected) {
  const files = (Array.isArray(entries) ? entries : []).filter((e) => e && typeof e.path === 'string').map((e) => e.path);
  const all = files.length > 0 && files.every((p) => selected instanceof Set && selected.has(p));
  return all ? new Set() : new Set(files);
}

/** 選取的檔案（依清單順序；不在清單裡的略過） */
export function selectedFiles(entries, selected) {
  return (Array.isArray(entries) ? entries : []).filter((e) => e && e.kind !== 'dir' && selected instanceof Set && selected.has(e.path));
}

/**
 * 下一批要開始的傳輸：同一個方向（'down'／'up'）還在跑的不到上限時，依加入的順序挑 queued 的。
 * 跑著的狀態：down 的 'fetching'、'running'；up 的 'uploading'、'sending'
 */
export function nextToStart(transfers, dir) {
  const list = (Array.isArray(transfers) ? transfers : []).filter((t) => t && t.kind !== 'folder');
  const activeStates = dir === 'down' ? ['fetching', 'running'] : ['uploading', 'sending'];
  const max = dir === 'down' ? MAX_ACTIVE_DOWNLOADS : MAX_ACTIVE_UPLOADS;
  const active = list.filter((t) => t.dir === dir && activeStates.includes(t.state)).length;
  return list.filter((t) => t.dir === dir && t.state === 'queued').slice(0, Math.max(0, max - active));
}

/** 已經結束的狀態（清單可以清掉） */
export const isFinished = (t) => isObj(t) && ['done', 'saved', 'failed', 'cancelled'].includes(t.state);

/** 一筆傳輸的百分比（0～100；不知道總長是 null） */
export function percent(done, total) {
  const d = num(done);
  const t = num(total);
  if (t === null || t <= 0 || d === null) return t === 0 ? 100 : null;
  return Math.max(0, Math.min(100, Math.floor((d / t) * 100)));
}

/** 傳輸清單的狀態文字 */
export function stateText(t, deviceName) {
  if (!isObj(t)) return '';
  const who = deviceName || '那台';
  const pct = percent(t.done, t.total);
  const p = pct === null ? '' : ' ' + pct + '%';
  switch (t.state) {
    case 'queued': return '排隊中';
    case 'fetching': return '向' + who + '要檔案…';
    case 'running': return '下載中' + p;
    case 'done': return t.dir === 'down' ? (t.browser ? '已交給瀏覽器下載' : '已下載') : '完成';
    case 'uploading': return '上傳到伺服器' + p;
    case 'sending': return '交給' + who + '…';
    case 'receiving': return t.late ? who + '還沒有回報進度（面板開著才會更新）' : who + '接收中' + p;
    case 'saved': return t.savedName && t.savedName !== t.name ? '已存成「' + t.savedName + '」' : '已存到' + who;
    case 'cancelled': return '已取消';
    case 'failed': return t.error || '失敗';
    default: return '';
  }
}

/** deviceStats 一台的 xfers（那台正在接收與剛收完的檔）：不合格的略過 */
export function parseXfers(list) {
  if (!Array.isArray(list)) return [];
  const out = [];
  for (const x of list) {
    if (!isObj(x) || !str(x.id) || !['receiving', 'saved', 'failed'].includes(x.state)) continue;
    const size = num(x.size);
    const done = num(x.done);
    out.push({ id: x.id, name: str(x.name) || '', size: size ?? 0, done: done ?? 0, state: x.state, saved: str(x.saved) });
  }
  return out;
}

/**
 * 那台回報的接收進度套到自己送的上傳（uploadId 對上的）：回要改的欄位（沒有變化回 null）。
 * receiving 更新 done、total；saved、failed 結束這一筆
 */
export function applyXfer(t, x) {
  if (!isObj(t) || !isObj(x) || t.uploadId !== x.id || isFinished(t)) return null;
  if (x.state === 'saved') return { state: 'saved', done: x.size, total: x.size, savedName: x.saved || x.name, late: false };
  if (x.state === 'failed') return { state: 'failed', error: '那台接收失敗（下載或存檔出錯）', late: false };
  if (t.state === 'receiving' && t.done === x.done && !t.late) return null;
  return { state: 'receiving', done: x.done, total: x.size, late: false };
}

/** 檔案大小的上傳前檢查：超過上限回說明，否則 null */
export function uploadProblem(file) {
  if (!isObj(file) && typeof file !== 'object') return '不是檔案';
  const size = num(file && file.size);
  if (size === null) return '讀不到檔案大小';
  if (size > MAX_UPLOAD_BYTES) return '超過 512 MB，不能經伺服器傳送（請先壓縮或分割）';
  return null;
}

/** 對話框標題列的路徑說明：虛擬根是「磁碟與常用資料夾」 */
export function locationLabel(path) {
  return path ? path : '磁碟與常用資料夾';
}

// ───── 整個資料夾（2026-11-09） ─────

/** 一次 deviceFetchMany 最多幾個檔（同那台的上限） */
export const FETCH_MANY_BATCH = 200;

/** 選取的資料夾（依清單順序） */
export function selectedFolders(entries, selected) {
  return (Array.isArray(entries) ? entries : []).filter((e) => e && e.kind === 'dir' && selected instanceof Set && selected.has(e.path));
}

/** 分批（每批 size 個） */
export function chunk(list, size) {
  const out = [];
  const arr = Array.isArray(list) ? list : [];
  for (let i = 0; i < arr.length; i += size) out.push(arr.slice(i, i + size));
  return out;
}

/** 相對路徑的資料夾部分與檔名（a\b\c.txt → { dir: 'a\\b', name: 'c.txt' }；斜線也收） */
export function splitRelative(rel) {
  const parts = String(rel || '').replace(/\//g, '\\').split('\\').filter(Boolean);
  const name = parts.pop() || '';
  return { dir: parts.join('\\'), name };
}

/** 下載資料夾裡一個檔要存進的子資料夾：資料夾名稱，再加上檔案所在的相對資料夾 */
export function downloadSubfolder(folderName, rel) {
  const { dir } = splitRelative(rel);
  return dir ? folderName + '\\' + dir : folderName;
}

/** 上傳資料夾裡一個檔的子資料夾：瀏覽器的 webkitRelativePath（photos/2024/a.jpg）去掉檔名，換成反斜線；沒有是 '' */
export function uploadSubfolder(relativePath) {
  return splitRelative(relativePath).dir;
}

/**
 * 一個資料夾的整體進度（kind 'folder' 的傳輸；children 是它底下的各檔）：
 * { files, finished, failed, done（位元組）, total（位元組）, state }。state：listing（還在列清單）、running、done（都結束、有成功的）、failed（都失敗）
 */
export function folderProgress(group, children) {
  const list = Array.isArray(children) ? children : [];
  const finished = list.filter(isFinished).length;
  const failed = list.filter((t) => t.state === 'failed').length;
  const cancelled = list.filter((t) => t.state === 'cancelled').length;
  const total = list.reduce((a, t) => a + (num(t.size) ?? 0), 0);
  const done = list.reduce((a, t) => a + (isFinished(t) && t.state !== 'failed' && t.state !== 'cancelled' ? (num(t.size) ?? 0) : Math.min(num(t.done) ?? 0, num(t.size) ?? 0)), 0);
  let state = 'running';
  if (isObj(group) && (group.state === 'listing' || group.state === 'failed' || group.state === 'cancelled') && !list.length) state = group.state;
  else if (list.length && finished === list.length) state = failed + cancelled === list.length ? (cancelled === list.length ? 'cancelled' : 'failed') : 'done';
  return { files: list.length, finished, failed, done, total, state };
}

/** 資料夾那一列的狀態文字 */
export function folderText(group, progress) {
  if (!isObj(group) || !isObj(progress)) return '';
  if (progress.state === 'listing') return group.dir === 'down' ? '列出資料夾裡的檔案…' : '準備上傳…';
  if (progress.state === 'failed' && !progress.files) return group.error || '失敗';
  if (progress.state === 'cancelled') return '已取消';
  const failed = progress.failed ? '，' + progress.failed + ' 個失敗' : '';
  const more = group.truncated ? '（檔案太多，只傳了前 ' + progress.files + ' 個）' : '';
  if (progress.state === 'done' || progress.state === 'failed') return '完成 ' + (progress.finished - progress.failed) + ' / ' + progress.files + ' 個檔' + failed + more;
  return (group.dir === 'down' ? '下載中 ' : '上傳中 ') + progress.finished + ' / ' + progress.files + ' 個檔' + failed + more;
}

/** 瀏覽器版能不能下載整個資料夾（要能選一個資料夾寫進去：Chrome、Edge 的 showDirectoryPicker） */
export function browserFolderDownloadProblem(hasDirectoryPicker) {
  return hasDirectoryPicker ? null : '這個瀏覽器不能把整個資料夾存下來（手機與 Safari 不支援）：請用電腦的 Chrome 或 Edge，或進到資料夾裡選檔案下載';
}

/** 瀏覽器寫檔用的不重複檔名：已經有的話加「 (2)」、「 (3)」…（exists(name) 回 true 表示有） */
export async function uniqueBrowserName(name, exists) {
  const dot = name.lastIndexOf('.');
  const stem = dot > 0 ? name.slice(0, dot) : name;
  const ext = dot > 0 ? name.slice(dot) : '';
  for (let n = 1; n < 1000; n++) {
    const candidate = n === 1 ? name : stem + ' (' + n + ')' + ext;
    if (!(await exists(candidate))) return candidate;
  }
  return null;
}
