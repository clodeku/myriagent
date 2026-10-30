// 排程頁（schedules.js）的純規則：分類與篩選、摘要、燈號、最近幾次的長條、時間軸、交給代理的選項（tools/ui-probe/schedulerules.test.mjs）。
// 資料是宿主的 schedules（web-protocol 的 ScheduleView）；時間一律是毫秒

/** 來源分類的名稱（宿主的 ScheduleOrigin） */
export const ORIGIN_LABEL = {
  agent: '代理建立',
  adopted: '納入管理',
  suspected: '疑似代理',
  other: '其他',
  ignored: '已忽略',
  timed: 'Myriagent 定時訊息',
};

/** 篩選列上來源的順序 */
export const ORIGIN_ORDER = ['agent', 'adopted', 'suspected', 'timed', 'other', 'ignored'];

/** 預設顯示的來源：代理建立、納入管理、疑似（系統與廠商、已忽略的要點了才看） */
export const DEFAULT_ORIGINS = ['agent', 'adopted', 'suspected', 'timed'];

/** 狀態篩選 */
export const STATE_FILTERS = [
  { key: 'all', label: '全部' },
  { key: 'fail', label: '失敗' },
  { key: 'running', label: '執行中' },
  { key: 'disabled', label: '停用' },
];

/** 交給代理的選項（宿主 ScheduleAgentPromptRules 的 task） */
export const AGENT_TASKS = [
  { task: 'diagnose', label: '診斷最近一次失敗', icon: 'warning' },
  { task: 'explain', label: '說明它在做什麼', icon: 'info' },
  { task: 'retime', label: '改執行時間', icon: 'clock' },
  { task: 'retire', label: '停用並清理', icon: 'trash' },
  { task: 'custom', label: '自訂…', icon: 'edit' },
];

const list = (v) => (Array.isArray(v) ? v : []);

/** 宿主送來的 entries（防呆：不是陣列、缺 path 的略過） */
export function entriesOf(schedules) {
  return list(schedules && schedules.entries).filter((e) => e && typeof e.path === 'string' && e.path);
}

/** 最近一次結果的等級（ok、info、warn、fail；沒有是 null） */
export function lastLevel(e) {
  return e && e.last && typeof e.last.level === 'string' ? e.last.level : null;
}

/** 列入管理（失敗會通知、自動診斷） */
export function isManaged(e) {
  return !!(e && (e.origin === 'agent' || e.origin === 'adopted'));
}

/** 燈號：執行中 run、停用 dis、最近失敗 fail、警告或錯過 warn、成功 ok、其他（還沒執行過、讀不到）info */
export function ledOf(e) {
  if (!e) return 'info';
  if (e.state === 'error') return 'warn';
  if (e.state === 'running') return 'run';
  if (e.enabled === false || e.state === 'disabled') return 'dis';
  const level = lastLevel(e);
  if (level === 'fail') return 'fail';
  if (level === 'warn' || (e.missed > 0)) return 'warn';
  if (level === 'ok') return 'ok';
  return 'info';
}

/** 篩選：來源在選的裡面、狀態符合 */
export function filterEntries(entries, filter) {
  const origins = new Set(filter && Array.isArray(filter.origins) ? filter.origins : DEFAULT_ORIGINS);
  const st = filter && filter.state ? filter.state : 'all';
  return list(entries).filter((e) => {
    if (!origins.has(e.origin)) return false;
    if (st === 'fail') return lastLevel(e) === 'fail';
    if (st === 'running') return e.state === 'running';
    if (st === 'disabled') return e.enabled === false || e.state === 'disabled';
    return true;
  });
}

/** 排序：失敗的在前、執行中、警告，再來啟用的在停用的前面，同一類照名稱 */
export function sortEntries(entries) {
  const rank = { fail: 0, run: 1, warn: 2, ok: 3, info: 3, dis: 5 };
  return list(entries).slice().sort((a, b) => {
    const ra = rank[ledOf(a)] ?? 4, rb = rank[ledOf(b)] ?? 4;
    if (ra !== rb) return ra - rb;
    return String(a.name || a.path).localeCompare(String(b.name || b.path), 'zh-Hant');
  });
}

/** 各來源的數量（篩選列的數字） */
export function originCounts(entries) {
  const out = {};
  for (const k of ORIGIN_ORDER) out[k] = 0;
  for (const e of list(entries)) if (e.origin in out) out[e.origin]++;
  return out;
}

/**
 * 頁首的摘要：列出的（代理建立、納入管理、疑似）幾個、執行中、最近一次失敗、錯過執行、接下來一小時預定幾次。
 * 只算列出來的那幾類（系統與廠商的不算）
 */
export function summaryOf(entries, now) {
  const shown = list(entries).filter((e) => DEFAULT_ORIGINS.includes(e.origin));
  let running = 0, failing = 0, missed = 0, nextHour = 0;
  for (const e of shown) {
    if (e.state === 'running') running++;
    if (lastLevel(e) === 'fail') failing++;
    if (e.missed > 0) missed++;
    for (const t of list(e.upcoming)) if (t >= now && t < now + 3600000) nextHour++;
  }
  return { count: shown.length, running, failing, missed, nextHour };
}

/** 頂端「排程」按鈕的數字：列入管理、最近一次失敗的（宿主算好的 failing；沒有時自己算），加上其他電腦回報的 */
export function badgeCount(schedules) {
  if (!schedules) return 0;
  const own = typeof schedules.failing === 'number' ? schedules.failing : entriesOf(schedules).filter((e) => isManaged(e) && lastLevel(e) === 'fail').length;
  return own + devicesOf(schedules).reduce((n, d) => n + (typeof d.snapshot.failing === 'number' ? d.snapshot.failing : 0), 0);
}

/** 其他電腦最後回報的（S3；ScheduleView.devices）：缺 deviceId 或摘要的略過 */
export function devicesOf(schedules) {
  return list(schedules && schedules.devices).filter((d) => d && typeof d.deviceId === 'string' && d.deviceId && d.snapshot && typeof d.snapshot === 'object');
}

/** 選取的鍵：這台的是路徑（或定時訊息的 id），其他電腦的前面加 deviceId */
export function keyOf(id, deviceId) {
  return deviceId ? deviceId + '::' + id : id;
}

/** 最近 n 次的長條（舊的在左）：等級與提示文字；不足 n 次就只有那幾次 */
export function historyBars(e, n = 14) {
  return list(e && e.runs).slice(0, n).reverse().map((r) => ({ level: r.level || 'info', at: r.at, text: r.text || '', gap: !!r.gap }));
}

/**
 * 時間軸上的點：[from, from+span) 之間的預定執行，x 是 0～1 的位置。每個排程最多 maxPerEntry 個點，
 * 超過時（每分鐘一次這類）改成一條帶狀（band：true，x1 是最後一次的位置）
 */
export function timelineOf(entries, from, span, maxPerEntry = 48) {
  const out = [];
  for (const e of list(entries)) {
    const times = list(e.upcoming).filter((t) => t >= from && t < from + span);
    if (!times.length) continue;
    if (times.length > maxPerEntry) {
      out.push({ path: e.path, band: true, x: (times[0] - from) / span, x1: (times[times.length - 1] - from) / span, count: times.length, led: ledOf(e) });
      continue;
    }
    for (const t of times) out.push({ path: e.path, band: false, x: (t - from) / span, at: t, led: ledOf(e) });
  }
  return out;
}

/** 時間軸的刻度：從 from 起每 step 小時一格（對齊整點），回 { x, label } */
export function timelineTicks(from, span, stepHours = 4) {
  const ticks = [];
  const step = stepHours * 3600000;
  const first = new Date(from);
  first.setMinutes(0, 0, 0);
  let t = first.getTime();
  while (t < from) t += 3600000;
  // 對齊到 stepHours 的倍數
  while (new Date(t).getHours() % stepHours !== 0) t += 3600000;
  for (; t < from + span; t += step) {
    const d = new Date(t);
    ticks.push({ x: (t - from) / span, label: String(d.getHours()).padStart(2, '0') + ':00' });
  }
  return ticks;
}

/** 「上次」的文字：結果（失敗時是結束碼的說明） */
export function lastText(e) {
  if (!e) return '';
  if (e.state === 'running') return '執行中';
  if (e.state === 'error') return '讀不到';
  if (e.last && e.last.text) return e.last.text;
  return e.lastRun ? '' : '還沒執行過';
}

/** 交給代理時預設的那一項：最近失敗就是診斷，否則說明 */
export function primaryTask(e) {
  return lastLevel(e) === 'fail' ? 'diagnose' : 'explain';
}

/** 「誰建立的」：建立它的對話標題（代理種類另外畫圖示），沒有證據時說明分類 */
export function provenanceText(e) {
  const p = e && e.provenance;
  if (p) {
    const where = p.source === 'transcript' ? '外部 CLI 的工作階段' : '';
    return p.title ? p.title : where || '代理建立';
  }
  if (!e) return '';
  if (e.origin === 'suspected') return '疑似（腳本在工作資料夾）';
  if (e.origin === 'adopted') return '納入管理';
  if (e.origin === 'ignored') return '已忽略';
  return '系統或廠商';
}

/** 代理種類的名稱（宿主送 AgentProvider 的名稱）→ 圖示的鍵 */
export function providerKeyOf(name) {
  return ['Claude', 'Codex', 'Antigravity', 'Grok'].includes(name) ? name : null;
}

/** 選取：還在清單裡就留著，否則挑第一個失敗的、再來第一個 */
export function pickSelection(entries, current) {
  const l = list(entries);
  if (current && l.some((e) => e.path === current)) return current;
  const fail = l.find((e) => lastLevel(e) === 'fail');
  return fail ? fail.path : (l[0] ? l[0].path : null);
}

// ───── Myriagent 的定時訊息（ScheduleView.timed） ─────

/** 宿主送來的定時訊息（防呆：缺 id 的略過） */
export function timedOf(schedules) {
  return list(schedules && schedules.timed).filter((t) => t && typeof t.id === 'string' && t.id);
}

/** 定時訊息換成清單、時間軸共用的形狀（path 是 id；停用或做完的算停用） */
export function timedAsEntry(t) {
  return { path: t.id, name: t.label || '定時訊息', enabled: t.enabled !== false && !t.done, state: t.enabled !== false && !t.done ? 'ready' : 'disabled',
    last: t.last || null, missed: 0, upcoming: list(t.upcoming), runs: list(t.runs), timed: true };
}

/** 定時訊息的狀態文字：做完、暫停、下一次 */
export function timedStateText(t) {
  if (t.done) return '已完成';
  if (t.enabled === false) return '已暫停';
  return '';
}

/** 新增定時訊息的時間寫法（宿主 TimedMessageRules.ParseWhen 的 kind） */
export const WHEN_KINDS = [
  { kind: 'daily', label: '每天', placeholder: '09:00' },
  { kind: 'weekdays', label: '平日（週一到週五）', placeholder: '09:00' },
  { kind: 'every', label: '每隔', placeholder: '30m 或 2h' },
  { kind: 'at', label: '一次', placeholder: '2026-10-27 14:00' },
  { kind: 'cron', label: 'cron（分 時 日 月 星期）', placeholder: '0 9 * * 1-5' },
];

/** 從 cron 推回對話框的時間寫法（編輯時預填）：每天、平日、每隔幾分鐘或幾小時，其他是 cron；一次性的是 at */
export function whenOf(t) {
  if (!t) return { kind: 'daily', value: '09:00' };
  if (!t.cron) {
    if (!t.at) return { kind: 'at', value: '' };
    const d = new Date(t.at);
    const p = (n) => String(n).padStart(2, '0');
    return { kind: 'at', value: `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}` };
  }
  const f = String(t.cron).trim().split(/\s+/);
  if (f.length === 5 && /^\d+$/.test(f[0]) && /^\d+$/.test(f[1]) && f[2] === '*' && f[3] === '*') {
    const hm = String(f[1]).padStart(2, '0') + ':' + String(f[0]).padStart(2, '0');
    if (f[4] === '*') return { kind: 'daily', value: hm };
    if (f[4] === '1-5') return { kind: 'weekdays', value: hm };
  }
  let m = /^\*\/(\d+)$/.exec(f[0]);
  if (f.length === 5 && m && f.slice(1).every((x) => x === '*')) return { kind: 'every', value: m[1] + 'm' };
  m = /^\*\/(\d+)$/.exec(f[1]);
  if (f.length === 5 && f[0] === '0' && m && f.slice(2).every((x) => x === '*')) return { kind: 'every', value: m[1] + 'h' };
  return { kind: 'cron', value: t.cron };
}

/** 可以送進去的對話：這台的（不是其他電腦的副本），最近更新的在前 */
export function localConversations(conversations) {
  return list(conversations).filter((c) => c && c.id && !c.deviceId).sort((a, b) => (b.updatedAt || 0) - (a.updatedAt || 0));
}

/** 快速操作：執行中可以停止，否則立即執行；啟用的可以停用，停用的可以啟用 */
export function quickActions(e) {
  if (!e || e.state === 'error') return [];
  const acts = [];
  acts.push(e.state === 'running' ? { action: 'stop', label: '停止', icon: 'stop' } : { action: 'run', label: '立即執行', icon: 'play' });
  acts.push(e.enabled === false ? { action: 'enable', label: '啟用', icon: 'check' } : { action: 'disable', label: '停用', icon: 'pause' });
  return acts;
}

/** 「⋯」選單：不再列出／恢復列出、納入管理／不納入（有證據的不能取消） */
export function listActions(e) {
  if (!e) return [];
  const acts = [];
  if (e.origin === 'ignored') acts.push({ action: 'unignore', label: '恢復列出' });
  else acts.push({ action: 'ignore', label: '不再列出' });
  if (e.origin === 'suspected' || e.origin === 'other') acts.push({ action: 'adopt', label: '納入管理（失敗時通知、自動診斷）' });
  if (e.origin === 'adopted') acts.push({ action: 'unadopt', label: '不納入管理' });
  return acts;
}

// ───── 第九批視覺效果（2026-10-26）：時間軸的刻度尺、即將執行、新的執行結果 ─────

/** 時間軸的每小時刻度（對齊整點）：{ x, major }——0、6、12、18 點是粗刻度 */
export function hourTicks(from, span) {
  const out = [];
  const first = new Date(from);
  first.setMinutes(0, 0, 0);
  let t = first.getTime();
  if (t < from) t += 3600000;
  for (; t < from + span; t += 3600000) out.push({ x: (t - from) / span, major: new Date(t).getHours() % 6 === 0 });
  return out;
}

/** 多少時間之內算「即將執行」（時間軸標琥珀、清單的「下次」標琥珀） */
export const SOON_MS = 3600000;

/** 下次執行在一小時內 */
export function isSoon(next, now) {
  return Number.isFinite(next) && next >= now && next - now < SOON_MS;
}

/** 時間軸上一小時內最近的一個點（不含帶狀的），沒有回 null */
export function soonestPoint(points, now) {
  let best = null;
  for (const p of list(points)) {
    if (p.band || !isSoon(p.at, now)) continue;
    if (!best || p.at < best.at) best = p;
  }
  return best;
}

/** 距離下次執行的短說法：不到 1 分鐘「即將執行」，其他「12 分後」 */
export function soonText(at, now) {
  const m = Math.floor((at - now) / 60000);
  return m < 1 ? '即將執行' : m + ' 分後';
}

/** 一個排程最近一次執行的記號（時間＋結果）：比對兩次重畫之間有沒有新的一次；沒有執行過是 null */
export function runStamp(e) {
  const r = list(e && e.runs)[0];
  if (r && Number.isFinite(r.at)) return r.at + ':' + (r.level || '');
  return e && Number.isFinite(e.lastRun) ? e.lastRun + ':' + (lastLevel(e) || '') : null;
}

/**
 * 兩次重畫之間有新的一次執行結果的排程（一次性的回饋：那一列閃一下、最近 14 次的長條長出新的一格）。
 * prev 是上一次記的 { 鍵: 記號 }，第一次（null）只建基準、不回報；items 是 [{ key, e }]。
 * 回 { seen: 這次的記號, fresh: { 鍵: 結果等級 } }——只回報上一次就有記號、而且變了的（剛出現的排程不算）
 */
export function freshRuns(prev, items) {
  const seen = {};
  const fresh = {};
  for (const { key, e } of list(items)) {
    const stamp = runStamp(e);
    seen[key] = stamp;
    if (prev && stamp && prev[key] && prev[key] !== stamp) fresh[key] = lastLevel(e) || 'info';
  }
  return { seen, fresh };
}
