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
    case 'paused': return pausedText(t);
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

// ───── 速度、經過與尚需時間、暫停（2026-11-10 使用者要求：顯示上下載速度、已經過多少時間／尚需多少時間，可以暫停、繼續、中止，在背景執行） ─────

/** 算速度看最近多久的進度 */
export const RATE_WINDOW_MS = 5000;

/** 計時（已經過）的狀態：真的在傳或等那台的時候才算，排隊與暫停不算 */
export const CLOCK_STATES = Object.freeze(['fetching', 'running', 'uploading', 'sending', 'receiving']);

/** 算得出速度與尚需時間的狀態（位元組在動的） */
export const RATE_STATES = Object.freeze(['running', 'uploading', 'receiving']);

/**
 * 記一筆進度：samples 是 [{ at, done }]（依時間）。done 倒退（重新開始、從頭重傳）就重來；只留 RATE_WINDOW_MS 內的（最少留一筆）。
 * 回新的陣列，不改傳入的
 */
export function pushSample(samples, at, done) {
  const list = Array.isArray(samples) ? samples.slice() : [];
  if (num(at) === null || num(done) === null) return list;
  const last = list[list.length - 1];
  if (last && done < last.done) list.length = 0;
  if (last && list.length && last.at === at) list[list.length - 1] = { at, done };
  else list.push({ at, done });
  while (list.length > 1 && at - list[0].at > RATE_WINDOW_MS) list.shift();
  return list;
}

/**
 * 每秒幾個位元組：最近 RATE_WINDOW_MS 內第一筆到 now 的平均（最後一筆之後沒有進度也算進時間，卡住時速度會降下來）；
 * 不到 1 秒的資料、或最後一筆已經超過視窗回 null（算不出來）
 */
export function rateOf(samples, now) {
  const list = Array.isArray(samples) ? samples : [];
  if (list.length < 2 || num(now) === null) return list.length === 1 && now - list[0].at > RATE_WINDOW_MS ? 0 : null;
  const first = list[0];
  const last = list[list.length - 1];
  if (now - last.at > RATE_WINDOW_MS) return 0;
  const span = Math.max(now, last.at) - first.at;
  if (span < 1000) return null;
  return Math.max(0, (last.done - first.done) / (span / 1000));
}

/**
 * 計時：在 CLOCK_STATES 時從 since 起算，離開時把這一段加進 spent（毫秒）。回 { spent, since }（不改傳入的）
 */
export function tickClock(clock, state, now) {
  const spent = num(clock && clock.spent) ?? 0;
  const since = num(clock && clock.since);
  const running = CLOCK_STATES.includes(state);
  if (running) return { spent, since: since ?? now };
  return { spent: since === null ? spent : spent + Math.max(0, now - since), since: null };
}

/** 已經過的毫秒（計時中的那一段算到 now） */
export function elapsedMs(clock, now) {
  const spent = num(clock && clock.spent) ?? 0;
  const since = num(clock && clock.since);
  return spent + (since === null ? 0 : Math.max(0, now - since));
}

/** 尚需幾毫秒：剩下的位元組除以速度；速度算不出來或是 0、不知道總長回 null */
export function remainingMs(done, total, rate) {
  const d = num(done);
  const t = num(total);
  const r = num(rate);
  if (d === null || t === null || t <= 0 || r === null || r <= 0) return null;
  return Math.max(0, Math.round(((t - Math.min(d, t)) / r) * 1000));
}

/** 速度的文字：「12.3 MB/s」（B/s、KB/s、MB/s、GB/s；1024 進位、三位有效數字左右） */
export function fmtRate(rate) {
  const r = num(rate);
  if (r === null) return '';
  const units = ['B/s', 'KB/s', 'MB/s', 'GB/s'];
  let v = Math.max(0, r);
  let i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  const text = i === 0 ? String(Math.round(v)) : v >= 100 ? v.toFixed(0) : v >= 10 ? v.toFixed(1) : v.toFixed(2);
  return text + ' ' + units[i];
}

/** 時間長度：「0:42」、「12:05」、「1:02:03」（無條件捨去到秒） */
export function fmtDuration(ms) {
  const v = num(ms);
  if (v === null || v < 0) return '';
  const s = Math.floor(v / 1000);
  const hh = Math.floor(s / 3600);
  const mm = Math.floor((s % 3600) / 60);
  const ss = s % 60;
  const two = (n) => String(n).padStart(2, '0');
  return hh > 0 ? hh + ':' + two(mm) + ':' + two(ss) : mm + ':' + two(ss);
}

/**
 * 速度與時間那一行：「12.3 MB/s・已經過 0:42・尚需 3:10」。沒有在跑也沒有經過時間回 ''；
 * 在跑但還算不出速度時只有經過時間；卡住（速度 0）時寫「等待中」
 */
export function metricsText({ state, rate, elapsed, remaining }) {
  const parts = [];
  const moving = RATE_STATES.includes(state);
  if (moving && num(rate) !== null) parts.push(rate > 0 ? fmtRate(rate) : '等待中');
  if (num(elapsed) !== null && elapsed >= 1000) parts.push('已經過 ' + fmtDuration(elapsed));
  if (moving && num(remaining) !== null) parts.push('尚需 ' + fmtDuration(remaining));
  return parts.join('・');
}

/**
 * 暫停一筆傳輸要怎麼做：
 * 'hold'（還沒開始：排隊中、資料夾裡還在等網址的、向那台要檔案中——拿到網址就停著）、
 * 'host'（桌面的下載：宿主保留下載到一半的檔，繼續時接著下載）、
 * 'stream'（瀏覽器版下載資料夾裡的檔：一段一段寫進選的資料夾，繼續時接著要）、
 * 'restart'（上傳到伺服器：伺服器只收一次送完的檔，暫停是停下來，繼續時這個檔從頭上傳）；
 * 不能暫停（已經交給那台、結束了、交給瀏覽器下載的）回 null
 */
export function pauseMode(t, hub) {
  if (!isObj(t) || t.kind === 'folder' || isFinished(t) || t.state === 'paused') return null;
  if (t.state === 'queued' || t.state === 'waiting' || t.state === 'fetching') return 'hold';
  if (t.dir === 'down' && t.state === 'running') return hub ? (t.dirHandle ? 'stream' : null) : (t.transferId ? 'host' : null);
  if (t.dir === 'up' && t.state === 'uploading') return 'restart';
  return null;
}

/** 這筆可以暫停嗎（資料夾：底下有可以暫停的） */
export function canPause(t, hub, children) {
  if (isObj(t) && t.kind === 'folder') return !t.paused && (Array.isArray(children) ? children : []).some((c) => pauseMode(c, hub) !== null);
  return pauseMode(t, hub) !== null;
}

/** 這筆可以繼續嗎（資料夾：暫停過、底下還有暫停的） */
export function canResume(t, children) {
  if (isObj(t) && t.kind === 'folder') return !!t.paused || (Array.isArray(children) ? children : []).some((c) => c && c.state === 'paused');
  return isObj(t) && t.state === 'paused';
}

/** 暫停中的說明（狀態文字） */
export function pausedText(t) {
  if (!isObj(t)) return '已暫停';
  const pct = percent(t.done, t.total);
  const p = pct === null || !t.done ? '' : ' ' + pct + '%';
  return '已暫停' + p + (t.pauseMode === 'restart' ? '（繼續時從頭上傳）' : '');
}

/**
 * 背景的傳輸摘要（對話框關著時右下角那一塊）：還沒結束的筆數（資料夾算一筆）、合計速度、總進度、最長的尚需時間。
 * rows：[{ state, done, total, rate, remaining, paused }]；沒有還沒結束的回 null
 */
export function dockSummary(rows) {
  const list = (Array.isArray(rows) ? rows : []).filter((r) => isObj(r) && !['done', 'saved', 'failed', 'cancelled'].includes(r.state));
  if (!list.length) return null;
  const paused = list.filter((r) => r.state === 'paused' || r.paused).length;
  const rate = list.reduce((a, r) => a + (num(r.rate) ?? 0), 0);
  const done = list.reduce((a, r) => a + (num(r.done) ?? 0), 0);
  const total = list.reduce((a, r) => a + (num(r.total) ?? 0), 0);
  const remaining = list.reduce((a, r) => (num(r.remaining) === null ? a : Math.max(a ?? 0, r.remaining)), null);
  return { count: list.length, paused, rate, done, total, remaining, pct: percent(done, total) };
}

/** 摘要的文字：「傳輸中 3 個・12.3 MB/s・尚需 4:10」；全部暫停時「已暫停 2 個」 */
export function dockText(sum) {
  if (!isObj(sum)) return '';
  if (sum.paused === sum.count) return '已暫停 ' + sum.count + ' 個傳輸';
  const parts = ['傳輸中 ' + (sum.count - sum.paused) + ' 個'];
  if (sum.paused) parts.push('暫停 ' + sum.paused + ' 個');
  if (sum.rate > 0) parts.push(fmtRate(sum.rate));
  if (num(sum.remaining) !== null) parts.push('尚需 ' + fmtDuration(sum.remaining));
  return parts.join('・');
}

/** 瀏覽器版下載資料夾裡的檔：一次要多少（伺服器 /f/ 帶 Range 時一次最多給 8 MB） */
export const BROWSER_SEGMENT_BYTES = 8 * 1024 * 1024;

/** 下一段的 Range：從 got 起、最多 BROWSER_SEGMENT_BYTES；知道總長時不超過最後一個位元組 */
export function segmentRange(got, total) {
  const from = Math.max(0, num(got) ?? 0);
  let to = from + BROWSER_SEGMENT_BYTES - 1;
  const t = num(total);
  if (t !== null && t > 0) to = Math.min(to, t - 1);
  return 'bytes=' + from + '-' + to;
}

/** Content-Range「bytes 0-99/1000」→ { from, to, size }；「bytes *\/0」（空的檔）→ { from: null, to: null, size: 0 }；不合格回 null */
export function parseContentRange(value) {
  if (typeof value !== 'string') return null;
  const m = /^bytes\s+(?:(\d+)-(\d+)|\*)\/(\d+)$/.exec(value.trim());
  if (!m) return null;
  const size = Number(m[3]);
  if (m[1] === undefined) return { from: null, to: null, size };
  const from = Number(m[1]);
  const to = Number(m[2]);
  return to >= from ? { from, to, size } : null;
}

/**
 * 瀏覽器版下載資料夾裡的一個檔：從 got 起一段一段要（fetchRange(range, signal) 回 Response；≤8 MB 的 Range）、核對起點、總長、ETag，
 * 每塊交給 write(chunk)，每塊之後 onProgress(got, total, etag)（暫停時拿它記下的位置繼續）。要完回 { got, total, etag }；
 * 中斷（signal）時丟 AbortError，檔案變了、本文提早結束、HTTP 不是 206 時丟 Error（訊息給人看）
 */
export async function streamSegments({ got = 0, total = null, etag = null, fetchRange, write, onProgress, signal }) {
  let pos = num(got) ?? 0;
  let size = num(total);
  let tag = str(etag);
  for (;;) {
    if (size !== null && pos >= size) break;
    const res = await fetchRange(segmentRange(pos, size), signal);
    const range = parseContentRange(res.headers.get('Content-Range'));
    if (res.status === 416 && pos === 0 && range && range.size === 0) { size = 0; break; } // 空的檔
    if (res.status !== 206 || !range || range.from !== pos || !res.body) throw new Error('HTTP ' + res.status);
    if (size !== null && size > 0 && range.size !== size) throw new Error('檔案在下載途中變了');
    const next = res.headers.get('ETag');
    if (tag && next && next !== tag) throw new Error('檔案在下載途中變了');
    tag = tag || next || null;
    size = range.size;
    const reader = res.body.getReader();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      await write(value);
      pos += value.byteLength;
      if (onProgress) onProgress(pos, size, tag);
    }
    if (pos < range.to + 1) throw new Error('本文提早結束');
    if (pos >= size) break;
  }
  return { got: pos, total: size ?? pos, etag: tag };
}
