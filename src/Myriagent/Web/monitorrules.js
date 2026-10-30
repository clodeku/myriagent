// 電腦狀態面板（monitor.js）的純規則：格式化、嚴重度與延遲分級、顯示卡與 CPU 溫度、每台的歷史、要畫的電腦清單、走勢圖的路徑。
// 協定見 docs/web-protocol.md（第一節 deviceStats、第二節 monitor）。
// 沒有 import、不碰 DOM、不讀時鐘：現在時間一律由呼叫端傳入（tools/ui-probe/monitorrules.test.mjs 以 data: URL 直接匯入測試）。
// 樣本時間只拿來顯示「幾秒前」與走勢圖的橫軸，不拿來排序；新舊只看每台各自的 seq（同一份資料會在別台更新時重送）。

/** 每台留幾個樣本（本機約每秒一個、其他電腦約每 2 秒一個：走勢圖約一到三分鐘） */
export const HISTORY_MAX = 90;
/** 走勢圖的橫軸：最新樣本往前最多多久 */
export const WINDOW_MS = 120000;
/** 歷史還不到 WINDOW_MS 時橫軸拉滿現有的長度，但至少這麼長（剛開始的兩三點不會被拉成整片） */
export const MIN_SPAN_MS = 30000;
/** 樣本超過多久標「資料過時」 */
export const STALE_MS = 20000;
/** 速率圖的刻度至少到這麼多（位元組／秒）：閒置時的幾十 B/s 不會被放大成滿版的鋸齒 */
export const MIN_RATE_SCALE = 8 * 1024;
/** 延遲圖的刻度至少到這麼多（毫秒） */
export const MIN_RTT_SCALE = 100;

const isObj = (v) => !!v && typeof v === 'object' && !Array.isArray(v);
const str = (v) => (typeof v === 'string' && v ? v : null);
const num = (v) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const nonNeg = (v) => { const n = num(v); return n !== null && n >= 0 ? n : null; };

// ───── 格式化 ─────

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];

/** 位元組（1024 進位）：512 B、1.5 KB、12 MB、931 GB；小於 10 留一位小數。不是有限的非負數回 '—' */
export function fmtBytes(n) {
  const v = nonNeg(n);
  if (v === null) return '—';
  let i = 0, x = v;
  while (x >= 1024 && i < UNITS.length - 1) { x /= 1024; i++; }
  if (i === 0) return Math.round(x) + ' B';
  const t = x < 10 ? x.toFixed(1) : String(Math.round(x));
  return t.replace(/\.0$/, '') + ' ' + UNITS[i];
}

/** 速率（位元組／秒）：0 B/s、980 B/s、1.2 KB/s、12 MB/s */
export function fmtRate(bps) {
  const t = fmtBytes(bps);
  return t === '—' ? '—' : t + '/s';
}

/** 一對容量：同單位時只寫一次（512 / 931 GB），不同時各自寫（800 MB / 1.5 GB） */
export function fmtPair(used, total) {
  const a = fmtBytes(used), b = fmtBytes(total);
  if (a === '—' || b === '—') return '—';
  const ua = a.split(' ')[1], ub = b.split(' ')[1];
  return ua === ub ? a.split(' ')[0] + ' / ' + b : a + ' / ' + b;
}

/** 百分比的數字（不含 %）：小於 10 留一位小數（3.4），其他取整數；null 回 '—' */
export function fmtPct(p) {
  const v = num(p);
  if (v === null) return '—';
  const c = Math.min(100, Math.max(0, v));
  return c < 10 && c > 0 ? c.toFixed(1).replace(/\.0$/, '') : String(Math.round(c));
}

/** 開機時間：12 分鐘、5 小時 12 分、3 天 4 小時；null 或負數回 null */
export function fmtUptime(sec) {
  const s = nonNeg(sec);
  if (s === null) return null;
  const m = Math.floor(s / 60), hr = Math.floor(m / 60), d = Math.floor(hr / 24);
  if (d >= 1) return d + ' 天' + (hr % 24 ? ' ' + (hr % 24) + ' 小時' : '');
  if (hr >= 1) return hr + ' 小時' + (m % 60 ? ' ' + (m % 60) + ' 分' : '');
  return Math.max(1, m) + ' 分鐘';
}

/** 多久以前（毫秒）：剛剛、12 秒前、3 分鐘前、2 小時前、3 天前 */
export function fmtAgo(ms) {
  const v = nonNeg(ms);
  if (v === null) return '';
  const s = Math.floor(v / 1000);
  if (s < 3) return '剛剛';
  if (s < 60) return s + ' 秒前';
  if (s < 3600) return Math.floor(s / 60) + ' 分鐘前';
  if (s < 86400) return Math.floor(s / 3600) + ' 小時前';
  return Math.floor(s / 86400) + ' 天前';
}

/** 使用量的百分比（0～100）；總量不是正數回 null */
export function pct(used, total) {
  const u = nonNeg(used), t = num(total);
  if (u === null || t === null || t <= 0) return null;
  return Math.min(100, (u / t) * 100);
}

// ───── 分級（嚴重度一律同時給文字：不只靠顏色）─────

function level(p, warnAt, maxAt, texts) {
  const v = num(p);
  if (v === null) return { sev: 'none', text: '' };
  if (v >= maxAt) return { sev: 'max', text: texts[2] };
  if (v >= warnAt) return { sev: 'warn', text: texts[1] };
  return { sev: 'ok', text: texts[0] };
}

/** CPU：≥70 偏高、≥90 滿載 */
export const cpuLevel = (p) => level(p, 70, 90, ['正常', '偏高', '滿載']);
/** 記憶體：≥80 偏高、≥92 吃緊 */
export const memLevel = (p) => level(p, 80, 92, ['正常', '偏高', '吃緊']);
/** 磁碟：≥85 偏高、≥95 快滿 */
export const diskLevel = (p) => level(p, 85, 95, ['正常', '偏高', '快滿']);
/** 顯示卡：同 CPU（≥70 偏高、≥90 滿載） */
export const gpuLevel = (p) => level(p, 70, 90, ['正常', '偏高', '滿載']);
/** CPU 溫度（攝氏）：≥80 偏高、≥90 過熱 */
export const tempLevel = (c) => level(c, 80, 90, ['正常', '偏高', '過熱']);

// ───── 顯示卡與 CPU 溫度 ─────

/** 溫度：52°C（取整數）；不是有限數回 '—' */
export function fmtTemp(c) {
  const v = num(c);
  return v === null ? '—' : Math.round(v) + '°C';
}

/** 溫度條的長度：0～100 °C 對應 0～100% */
export function tempPct(c) {
  const v = num(c);
  return v === null ? null : Math.min(100, Math.max(0, v));
}

/** 溫度的來源（DeviceStats 的 cpuTempSource）：short 放在列尾、long 放在說明；不認得的照原樣（截短） */
const TEMP_SOURCES = new Map([
  ['hwinfo', { short: 'HWiNFO', long: 'HWiNFO' }],
  ['lhm', { short: 'LHM', long: 'LibreHardwareMonitor' }],
  ['ohm', { short: 'OHM', long: 'OpenHardwareMonitor' }],
  ['acpi', { short: 'ACPI', long: 'ACPI 熱區（主機板回報的，未必是 CPU 核心的溫度）' }],
]);
export function tempSource(src) {
  const s = str(src);
  if (!s) return null;
  return TEMP_SOURCES.get(s) || { short: s.slice(0, 8), long: s };
}

/** 顯示卡名稱看得出的廠牌（列首的標籤）；看不出回 null */
export function gpuVendor(name) {
  const n = (str(name) || '').toLowerCase();
  if (/nvidia|geforce|quadro|tesla|\brtx\b|\bgtx\b/.test(n)) return 'NVIDIA';
  if (/\bamd\b|radeon|\bati\b/.test(n)) return 'AMD';
  if (/intel|\barc\b|\biris\b|\buhd\b/.test(n)) return 'Intel';
  if (/qualcomm|adreno/.test(n)) return 'Adreno';
  return null;
}

/**
 * 每張顯示卡那一列的標籤：只有一張是「GPU」；多張時用廠牌（同廠牌的加編號：NVIDIA1、NVIDIA2），看不出廠牌的是 GPU1、GPU2…（依清單位置）
 */
export function gpuLabels(gpus) {
  const list = Array.isArray(gpus) ? gpus : [];
  if (list.length <= 1) return list.map(() => 'GPU');
  const tags = list.map((g) => gpuVendor(g && g.name));
  const total = new Map();
  for (const t of tags) if (t) total.set(t, (total.get(t) || 0) + 1);
  const seen = new Map();
  return tags.map((t, i) => {
    if (!t) return 'GPU' + (i + 1);
    if (total.get(t) === 1) return t;
    seen.set(t, (seen.get(t) || 0) + 1);
    return t + seen.get(t);
  });
}

/**
 * 與伺服器的往返延遲：≤60ms 良好（4 格）、≤150 普通（3 格）、≤400 偏慢（2 格）、更慢（1 格）；
 * null 是沒有連線（未配對的這台是「未配對」）
 */
export function latencyLevel(rtt, unpaired = false) {
  const v = nonNeg(rtt);
  if (v === null) return { tier: 'none', text: unpaired ? '未配對' : '未連線', bars: 0 };
  if (v <= 60) return { tier: 'good', text: '良好', bars: 4 };
  if (v <= 150) return { tier: 'fair', text: '普通', bars: 3 };
  if (v <= 400) return { tier: 'slow', text: '偏慢', bars: 2 };
  return { tier: 'slow', text: '很慢', bars: 1 };
}

/** 延遲的數字（不含單位）：小於 10 留一位小數 */
export function fmtRtt(rtt) {
  const v = nonNeg(rtt);
  if (v === null) return '—';
  return v < 10 ? v.toFixed(1).replace(/\.0$/, '') : String(Math.round(v));
}

/** 環形量表外圈刻度環每秒轉幾度：閒置約 60 秒一圈、滿載約 3 秒一圈；沒有資料不轉 */
export function spinRate(cpu) {
  const v = num(cpu);
  if (v === null) return 0;
  return 6 + Math.min(100, Math.max(0, v)) * 1.14;
}

// ───── deviceStats（web-protocol 第一節）─────

/**
 * 一筆 DeviceStatsView 整理成畫面用的形狀（數字不是有限值的換成 null、百分比夾在 0～100、磁碟只留名稱是字串而且總量是正數的）。
 * 不是物件、或 id 與 self 都不能用（不是這台而且沒有 id）的回 null
 */
export function normalizeStats(v) {
  if (!isObj(v)) return null;
  const self = v.self === true;
  const id = str(v.id);
  if (!self && !id) return null;
  const memTotal = num(v.memTotal);
  const memUsed = nonNeg(v.memUsed);
  const disks = Array.isArray(v.disks) ? v.disks.filter((d) => isObj(d) && str(d.name) && num(d.total) > 0)
    .map((d) => ({ name: d.name, used: Math.min(nonNeg(d.used) ?? 0, d.total), total: d.total })) : [];
  const cpu = num(v.cpu);
  const gpus = Array.isArray(v.gpus) ? v.gpus.filter((g) => isObj(g) && str(g.name)).map((g) => {
    const util = num(g.util);
    const total = num(g.memTotal);
    const used = nonNeg(g.memUsed);
    const mem = total !== null && total > 0 && used !== null;
    return { name: g.name, util: util === null ? null : Math.min(100, Math.max(0, util)), memUsed: mem ? Math.min(used, total) : null, memTotal: mem ? total : null };
  }) : [];
  const temp = num(v.cpuTemp);
  return {
    id,
    self,
    name: str(v.name),
    seq: num(v.seq),
    ageMs: nonNeg(v.ageMs) ?? 0,
    cpu: cpu === null ? null : Math.min(100, Math.max(0, cpu)),
    cores: num(v.cores) > 0 ? Math.round(v.cores) : null,
    cpuName: str(v.cpuName),
    memUsed: memTotal !== null && memTotal > 0 && memUsed !== null ? Math.min(memUsed, memTotal) : null,
    memTotal: memTotal !== null && memTotal > 0 ? memTotal : null,
    disks,
    netRx: nonNeg(v.netRx),
    netTx: nonNeg(v.netTx),
    rttMs: nonNeg(v.rttMs),
    uptimeSec: nonNeg(v.uptimeSec),
    gpus,
    cpuTemp: temp !== null && temp > -100 && temp < 200 ? temp : null,
    cpuTempSource: temp !== null ? str(v.cpuTempSource) : null,
  };
}

/**
 * deviceStats 的 hub（MyriagentHub 主機自己的負載；HubStatsView）：數值欄位同 DeviceStatsView（沒有 id、self、rttMs），另有 devices（在線的電腦數）、
 * browsers（連著的瀏覽器連線數）。不是物件（null、省略：未配對、伺服器較舊、還沒取樣）回 null
 */
export function normalizeHub(v) {
  if (!isObj(v)) return null;
  const s = normalizeStats(Object.assign({}, v, { id: 'hub', self: false }));
  const count = (x) => (nonNeg(x) === null ? null : Math.round(x));
  return Object.assign(s, { id: null, hub: true, rttMs: null, devices: count(v.devices), browsers: count(v.browsers) });
}

/** deviceStats 的 devices 整份：整理過的清單（不合格的略過；同一台出現兩次只留第一筆） */
export function parseDeviceStats(list) {
  const out = [];
  const seen = new Set();
  for (const raw of Array.isArray(list) ? list : []) {
    const s = normalizeStats(raw);
    if (!s) continue;
    const key = statsKey(s);
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(s);
  }
  return out;
}

/** 歷史與卡片的鍵：MyriagentHub 主機是 'hub'；這台一律是 'self'（未配對時沒有 id，配對完成後 id 才出現，鍵不變），其他電腦是 id */
export function statsKey(s) {
  if (s && s.hub) return 'hub';
  return s && s.self ? 'self' : (s && s.id) || '';
}

/** 一筆樣本的取樣時間（收到時間 − ageMs） */
export const sampleAt = (s, receivedAt) => receivedAt - ((s && s.ageMs) || 0);

/**
 * 把一則 deviceStats 併進每台的歷史（Map：鍵 → { seq, points: [{ t, cpu, mem, rx, tx, rtt }] }）：seq 和上一筆不同（含 null、變小）才加一點，
 * 每台最多 HISTORY_MAX 點；這則沒有的電腦保留歷史（離線再回來時接著畫）。回傳有加點的鍵
 */
export function mergeHistory(history, entries, receivedAt) {
  const changed = [];
  for (const s of entries) {
    const key = statsKey(s);
    if (!key) continue;
    let rec = history.get(key);
    if (!rec) { rec = { seq: undefined, points: [] }; history.set(key, rec); }
    if (rec.points.length && rec.seq === s.seq && s.seq !== null) continue;
    rec.seq = s.seq;
    const t = sampleAt(s, receivedAt);
    // 時間往回走（宿主換了一次啟動、時鐘調整）：舊的點不再接得上，從頭畫
    if (rec.points.length && t < rec.points[rec.points.length - 1].t) rec.points = [];
    rec.points.push({ t, cpu: s.cpu, mem: pct(s.memUsed, s.memTotal), rx: s.netRx, tx: s.netTx, rtt: s.rttMs });
    if (rec.points.length > HISTORY_MAX) rec.points.splice(0, rec.points.length - HISTORY_MAX);
    changed.push(key);
  }
  return changed;
}

/**
 * 要畫的電腦（每台一列）：
 * - MyriagentHub 主機（opts.hub 不是 null 時）一律在最上面，名稱是「MyriagentHub 主機」、machine 是伺服器的電腦名稱（副標）；超過 STALE_MS 一樣是 stale。
 * - 桌面：這台一定是第一列（還沒收到資料時是 waiting）。名稱優先用 remote.devices 裡這台的顯示名稱，再來是宿主送的電腦名稱。
 * - 其他電腦照 remote.devices 的順序：在線的在前、離線的在後；已撤銷的不列。在線而有統計的是 live（超過 STALE_MS 是 stale），
 *   在線卻沒有統計的是 nodata，離線的是 offline（不看統計：伺服器在斷線時就不再轉）。
 * - 有統計、卻不在 remote.devices 裡的（清單還沒更新）排在在線的後面，名稱用 id 的開頭。
 * opts：{ remote, entries（parseDeviceStats 的結果）, hub（normalizeHub 的結果）, receivedAt, now, isHub }
 */
export function buildRows(opts) {
  const remote = isObj(opts && opts.remote) ? opts.remote : null;
  const entries = Array.isArray(opts && opts.entries) ? opts.entries : [];
  const now = num(opts && opts.now) ?? 0;
  const receivedAt = num(opts && opts.receivedAt) ?? now;
  const isHub = !!(opts && opts.isHub);
  const ownId = remote && !isHub ? str(remote.deviceId) : null;
  const devices = remote && Array.isArray(remote.devices) ? remote.devices.filter((d) => isObj(d) && str(d.id)) : [];
  const byId = new Map(devices.map((d) => [d.id, d]));
  const statsById = new Map();
  let selfStats = null;
  for (const s of entries) {
    if (s.self) { if (!isHub && !selfStats) selfStats = s; continue; }
    if (!statsById.has(s.id)) statsById.set(s.id, s);
  }
  const liveRow = (s, base) => {
    const at = sampleAt(s, receivedAt);
    return Object.assign(base, { stats: s, sampleAt: at, state: now - at > STALE_MS ? 'stale' : 'live' });
  };

  const rows = [];
  const hub = opts && opts.hub && opts.hub.hub ? opts.hub : null;
  if (hub) rows.push(liveRow(hub, { key: 'hub', id: null, self: false, hub: true, name: 'Hub 主機', machine: hub.name, online: true, lastSeenAt: null, paired: true, stats: null, sampleAt: null }));
  if (!isHub) {
    const mine = ownId ? byId.get(ownId) : null;
    const name = (mine && str(mine.name)) || (selfStats && selfStats.name) || '這台電腦';
    const base = { key: 'self', id: ownId, self: true, name, online: true, lastSeenAt: null, paired: !!(remote && ownId), stats: null, sampleAt: null };
    rows.push(selfStats ? liveRow(selfStats, base) : Object.assign(base, { state: 'waiting' }));
  }

  const online = [], unknown = [], offline = [];
  for (const d of devices) {
    if (d.revoked || d.id === ownId || d.self === true) continue;
    const base = { key: d.id, id: d.id, self: false, name: str(d.name) || str(d.machineName) || shortId(d.id), online: d.online === true,
      lastSeenAt: num(d.lastSeenAt), paired: true, stats: null, sampleAt: null };
    if (!base.online) { offline.push(Object.assign(base, { state: 'offline' })); continue; }
    const s = statsById.get(d.id);
    online.push(s ? liveRow(s, base) : Object.assign(base, { state: 'nodata' }));
  }
  for (const [id, s] of statsById) {
    if (byId.has(id) || id === ownId) continue;
    unknown.push(liveRow(s, { key: id, id, self: false, name: shortId(id), online: true, lastSeenAt: null, paired: true, stats: null, sampleAt: null }));
  }
  return rows.concat(online, unknown, offline);
}

/** 沒有名稱時顯示的 id 開頭（d_7K3M…） */
export function shortId(id) {
  const s = str(id) || '';
  return s.length > 8 ? s.slice(0, 6) + '…' : s;
}

/** 面板標題旁的計數：在線的電腦（含這台）／全部（不含已撤銷；MyriagentHub 主機不算） */
export function onlineSummary(rows) {
  const list = (Array.isArray(rows) ? rows : []).filter((r) => !r.hub);
  const total = list.length;
  const on = list.filter((r) => r.state !== 'offline').length;
  return { online: on, total, text: total ? on + ' / ' + total + ' 在線' : '' };
}

/** MyriagentHub 主機的連線數：「3 台電腦、2 個瀏覽器」；兩個都不知道回 '' */
export function hubConnections(s) {
  if (!s) return '';
  const parts = [];
  if (s.devices !== null && s.devices !== undefined) parts.push(s.devices + ' 台電腦');
  if (s.browsers !== null && s.browsers !== undefined) parts.push(s.browsers + ' 個瀏覽器');
  return parts.join('、');
}

/** 卡片副標：CPU 型號（核心數在 CPU 量表下面）；MyriagentHub 主機前面加伺服器的電腦名稱（「Hub 主機 · Intel Xeon E-2386G」）；都沒有回 '' */
export function cpuSubtitle(s, machine) {
  const parts = [];
  if (typeof machine === 'string' && machine) parts.push(machine);
  if (s && s.cpuName) parts.push(s.cpuName);
  return parts.join(' · ');
}

// ───── 走勢圖 ─────

/** 刻度的上限：1、2、5 × 10ⁿ 中第一個 ≥ v 的（至少 floor） */
export function niceMax(v, floor = 1) {
  const x = Math.max(num(v) ?? 0, num(floor) ?? 1, 1e-9);
  const p = Math.pow(10, Math.floor(Math.log10(x)));
  for (const m of [1, 2, 5, 10]) if (m * p >= x - 1e-9) return m * p;
  return 10 * p;
}

/**
 * 速率（位元組／秒）的刻度上限：以 1024 進位的單位（數字不到 500 的最大單位）取 1、2、5、10、20、50、100、200、500 倍（5 MB/s，不是 4.8 MB/s），至少 floor
 */
export function niceRate(v, floor = MIN_RATE_SCALE) {
  const x = Math.max(num(v) ?? 0, num(floor) ?? 1, 1);
  let unit = 1;
  while (x / unit >= 500 && unit < Math.pow(1024, UNITS.length - 1)) unit *= 1024;
  for (const m of [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000]) if (m * unit >= x) return m * unit;
  return 1024 * unit;
}

/**
 * 走勢圖的刻度：同一張圖的線共用同一個 y 軸（上傳／下載一起；延遲另一張圖），上限取視窗內的最大值往上取整數。
 * keys：要一起算的欄位（['rx','tx'] 或 ['rtt']）
 */
export function scaleFor(points, keys, floor, nice = niceMax) {
  let m = 0;
  for (const p of Array.isArray(points) ? points : []) for (const k of keys) { const v = num(p[k]); if (v !== null && v > m) m = v; }
  return nice(m, floor);
}

/** 視窗內的點：最新一點往前 WINDOW_MS（點本身照時間排序） */
export function windowPoints(points) {
  const list = Array.isArray(points) ? points : [];
  if (!list.length) return [];
  const t1 = list[list.length - 1].t;
  return list.filter((p) => p.t >= t1 - WINDOW_MS);
}

/** 橫軸的長度：視窗內第一點到最新一點，夾在 MIN_SPAN_MS～WINDOW_MS */
export function spanOf(list) {
  if (!Array.isArray(list) || !list.length) return MIN_SPAN_MS;
  return Math.min(WINDOW_MS, Math.max(MIN_SPAN_MS, list[list.length - 1].t - list[0].t));
}

/**
 * 一條線的 SVG 路徑（座標系 w × h，y 往下）：x 依時間（最新一點在右緣、往前 spanOf 在左緣），y 依 max；值是 null 的點斷開。
 * 回 { line, area }：area 是線往下封到底的填色（每一段各自封）；沒有任何點回空字串
 */
export function sparkPaths(points, key, w, h, max) {
  const list = windowPoints(points);
  if (!list.length || !(max > 0)) return { line: '', area: '' };
  const t1 = list[list.length - 1].t;
  const span = spanOf(list);
  const x = (t) => (w * (1 - (t1 - t) / span));
  const y = (v) => h - Math.min(1, Math.max(0, v / max)) * h;
  const f = (n) => (Math.round(n * 10) / 10).toString();
  let line = '', area = '', seg = [];
  const close = () => {
    if (!seg.length) return;
    // 只有一點時畫一小段水平線，看得到目前的值
    if (seg.length === 1) seg.push([Math.min(w, seg[0][0] + 1.5), seg[0][1]]);
    line += 'M' + seg.map((p) => f(p[0]) + ' ' + f(p[1])).join('L');
    area += 'M' + f(seg[0][0]) + ' ' + f(h) + 'L' + seg.map((p) => f(p[0]) + ' ' + f(p[1])).join('L') + 'L' + f(seg[seg.length - 1][0]) + ' ' + f(h) + 'Z';
    seg = [];
  };
  for (const p of list) {
    const v = num(p[key]);
    if (v === null) { close(); continue; }
    seg.push([x(p.t), y(v)]);
  }
  close();
  return { line, area };
}

/** 滑過的位置（0～1，左緣到右緣）最接近的點（視窗內）；沒有點回 null */
export function hoverPoint(points, frac) {
  const list = windowPoints(points);
  if (!list.length) return null;
  const t1 = list[list.length - 1].t;
  const span = spanOf(list);
  const t = t1 - (1 - Math.min(1, Math.max(0, num(frac) ?? 1))) * span;
  let best = list[0];
  for (const p of list) if (Math.abs(p.t - t) < Math.abs(best.t - t)) best = p;
  return { point: best, frac: 1 - (t1 - best.t) / span, agoMs: t1 - best.t };
}

// ───── 動畫的轉速跟著負載（2026-10-25 使用者要求：負載越高動畫越快，轉速改變時畫面要連續、不能跳） ─────
// monitor.js 以 Web Animations 的 updatePlaybackRate 改 CSS 動畫的倍速：保留目前的位置、只改之後的速度（改 animation-duration 會讓位置瞬間跳到別處）；
// 倍速每秒只往目標走一段（easeRate），加速、減速都是幾秒內慢慢變

/** 最慢與最快的倍速（閒置時比原本慢一點，滿載時三倍） */
export const RATE_MIN = 0.6;
export const RATE_MAX = 3;

const lerpRate = (t) => Math.round((RATE_MIN + Math.min(1, Math.max(0, t)) * (RATE_MAX - RATE_MIN)) * 1000) / 1000;

/** 百分比的負載（CPU、顯示卡）對應的倍速：0% 是 RATE_MIN、100% 是 RATE_MAX；不明回 1 */
export function loadRate(pct) {
  const v = num(pct);
  return v === null ? 1 : lerpRate(v / 100);
}

/** CPU 溫度（°C）對應的倍速：40°C 以下 RATE_MIN、95°C 以上 RATE_MAX；不明回 1 */
export function tempRate(c) {
  const v = num(c);
  return v === null ? 1 : lerpRate((v - 40) / 55);
}

/** 網路流量（位元組／秒）對應的倍速：以對數，1 KB/s 以下 RATE_MIN、100 MB/s 以上 RATE_MAX；不明回 1 */
export function netRate(bytesPerSec) {
  const v = nonNeg(bytesPerSec);
  if (v === null) return 1;
  return lerpRate(v <= 1024 ? 0 : (Math.log10(v) - Math.log10(1024)) / (Math.log10(100 * 1024 * 1024) - Math.log10(1024)));
}

/** 這一秒的倍速：從目前的往目標走 45%，差不到 0.02 就直接到；目前不明（第一次）直接用目標 */
export function easeRate(cur, target) {
  if (!Number.isFinite(target)) return Number.isFinite(cur) ? cur : 1;
  if (!Number.isFinite(cur)) return target;
  const next = cur + (target - cur) * 0.45;
  return Math.abs(next - target) < 0.02 ? target : Math.round(next * 1000) / 1000;
}
