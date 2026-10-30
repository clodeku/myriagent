// 全流程 FF1～FF3 的純函式（WP4.9 步 15；flows/ff1.mjs～ff3.mjs 用，test/flow-rules.test.mjs 測）：
// S7 的樣本與判定、回補的時間窗、抽樣、項目 id 與 Ord 的比對、計數與多重集合的比對、commandResult 的計數、liveDropped 的增量、ULID。
// 不碰檔案、網路、程序與時鐘（ULID 的時間與亂數由呼叫端給）。
import { summarize } from './stats-rules.mjs';

/** S7 的門檻（設計 1.3：擁有者產生事件到瀏覽器顯示 p95 ≤500 ms、p99 ≤1 秒，每方向 20 ms 延遲） */
export const S7_P95_MS = 500;
export const S7_P99_MS = 1000;

/**
 * 一條路的延遲樣本（毫秒）：records 是頁面記錄器的標記紀錄 [{ c, s（標記：13 位數字的字串或數字）, ws, wsDone, dom, domDone }]。
 * path 'live'＝即時那一條（第一次帶著標記出現，不論狀態）、'persistent'＝持久那一條（第一次出現在完成的項目裡）；
 * source 'ws'＝傳輸層收到的時間、'dom'＝畫面上第一次出現（MutationObserver）。conversations 給了就只算這些對話；
 * window 給了（{ from, to }，毫秒；null 是那一邊不限）就只算標記時間落在裡面的（FF1 的「回補期間」）。沒有那個時間的紀錄不算樣本，另以 missing 回報
 */
export function latencySamples(records, { path, source, conversations = null, window = null }) {
  if (path !== 'live' && path !== 'persistent') throw new Error(`path 只收 live、persistent：${path}`);
  if (source !== 'ws' && source !== 'dom') throw new Error(`source 只收 ws、dom：${source}`);
  const field = path === 'live' ? source : `${source}Done`;
  const only = conversations ? new Set(conversations) : null;
  const samples = [];
  let missing = 0;
  for (const r of records ?? []) {
    if (only && !only.has(r.c)) continue;
    const stamp = Number(r.s);
    if (!Number.isFinite(stamp)) continue;
    if (window && ((typeof window.from === 'number' && stamp < window.from) || (typeof window.to === 'number' && stamp > window.to))) continue;
    const t = r[field];
    if (typeof t !== 'number' || !Number.isFinite(t)) {
      missing++;
      continue;
    }
    samples.push(t - stamp);
  }
  return { samples, missing };
}

/** 同 latencySamples 的條件，延遲最大的 n 個：[{ c, s（標記，數字）, ms }]（大到小；同樣大時標記早的在前） */
export function worstSamples(records, opts, n = 10) {
  const { path, source, conversations = null, window = null } = opts;
  const field = path === 'live' ? source : `${source}Done`;
  const only = conversations ? new Set(conversations) : null;
  const out = [];
  for (const r of records ?? []) {
    if (only && !only.has(r.c)) continue;
    const stamp = Number(r.s);
    if (!Number.isFinite(stamp)) continue;
    if (window && ((typeof window.from === 'number' && stamp < window.from) || (typeof window.to === 'number' && stamp > window.to))) continue;
    if (typeof r[field] !== 'number') continue;
    out.push({ c: r.c, s: stamp, ms: r[field] - stamp });
  }
  return out.sort((a, b) => b.ms - a.ms || a.s - b.s).slice(0, n);
}

/**
 * S7 的判定：{ ok, summary, reason }。樣本不到 minSamples 個、或有標記沒有對應的時間（missing）都不算通過；
 * 否則 p95 ≤500 而且 p99 ≤1000 才通過（剛好等於算通過）
 */
export function judgeS7({ samples, missing }, { minSamples = 20 } = {}) {
  const summary = summarize(samples);
  if (summary.count < minSamples) return { ok: false, summary, reason: `樣本只有 ${summary.count} 個（要 ≥${minSamples}）` };
  if (missing > 0) return { ok: false, summary, reason: `${missing} 個標記沒有收到或沒有顯示` };
  if (summary.p95 > S7_P95_MS) return { ok: false, summary, reason: `p95 ${summary.p95} ms 超過 ${S7_P95_MS} ms` };
  if (summary.p99 > S7_P99_MS) return { ok: false, summary, reason: `p99 ${summary.p99} ms 超過 ${S7_P99_MS} ms` };
  return { ok: true, summary, reason: null };
}

/**
 * 回補的時間窗：timeline 是依時間的取樣 [{ t, left（還剩幾個對話要回補；沒有在回補是 null）}]。
 * from＝第一個 left > 0 的取樣，to＝之後第一個 left 是 null 的取樣（到最後都還在回補時是 null）；從沒看到回補是 null
 */
export function backfillWindow(timeline) {
  let from = null;
  for (const p of timeline ?? []) {
    if (from === null) {
      if (typeof p.left === 'number' && p.left > 0) from = p.t;
    } else if (p.left === null || p.left === undefined) {
      return { from, to: p.t };
    }
  }
  return from === null ? null : { from, to: null };
}

/** 從排好的清單平均挑 n 個（第 0 個、第 ⌊i×len/n⌋ 個…；不夠就全部）；結果依原本的順序、不重複 */
export function pickEvenly(list, n) {
  const items = [...(list ?? [])];
  if (n <= 0) return [];
  if (items.length <= n) return items;
  const out = [];
  for (let i = 0; i < n; i++) out.push(items[Math.floor((i * items.length) / n)]);
  return out;
}

/**
 * 瀏覽器拿到的項目（[{ id, seq }]，瀏覽器版的 seq 填的是 Ord）和假裝置的 ords（[{ id, ord }]）：id 與 Ord 的集合是否相同。
 * 回 { equal, browser, device, onlyBrowser, onlyDevice, ordMismatch, duplicates, withoutOrd }（後面幾個列最多 10 筆）
 */
export function compareIdOrd(browserItems, deviceItems) {
  const b = new Map();
  const duplicates = [];
  for (const i of browserItems ?? []) {
    if (b.has(i.id)) duplicates.push(i.id);
    b.set(i.id, i.seq);
  }
  const d = new Map();
  let withoutOrd = 0;
  for (const i of deviceItems ?? []) {
    if (i.ord === null || i.ord === undefined) withoutOrd++;
    d.set(i.id, i.ord);
  }
  const onlyBrowser = [...b.keys()].filter((id) => !d.has(id));
  const onlyDevice = [...d.keys()].filter((id) => !b.has(id));
  const ordMismatch = [...b.entries()].filter(([id, seq]) => d.has(id) && d.get(id) !== seq).map(([id, seq]) => ({ id, browser: seq, device: d.get(id) }));
  const equal = onlyBrowser.length === 0 && onlyDevice.length === 0 && ordMismatch.length === 0 && duplicates.length === 0 && withoutOrd === 0 && b.size > 0;
  return { equal, browser: b.size, device: d.size, onlyBrowser: onlyBrowser.slice(0, 10), onlyDevice: onlyDevice.slice(0, 10), ordMismatch: ordMismatch.slice(0, 10), duplicates: duplicates.slice(0, 10), withoutOrd };
}

/** 依 key 計數：{ 值: 次數 }（鍵依字典順序） */
export function countBy(list, key) {
  const out = {};
  for (const x of list ?? []) {
    const k = String(typeof key === 'function' ? key(x) : x[key]);
    out[k] = (out[k] ?? 0) + 1;
  }
  return Object.fromEntries(Object.entries(out).sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0)));
}

/** 兩個多重集合（{ 值: 次數 }）的差：{ equal, onlyA: { 值: 多幾次 }, onlyB } */
export function multisetDiff(a, b) {
  const onlyA = {};
  const onlyB = {};
  for (const k of new Set([...Object.keys(a ?? {}), ...Object.keys(b ?? {})])) {
    const d = (a?.[k] ?? 0) - (b?.[k] ?? 0);
    if (d > 0) onlyA[k] = d;
    else if (d < 0) onlyB[k] = -d;
  }
  return { equal: Object.keys(onlyA).length === 0 && Object.keys(onlyB).length === 0, onlyA, onlyB };
}

/** a 的每一個值在 b 裡至少一樣多次（a ⊆ b）：{ ok, missing: { 值: 少幾次 } } */
export function multisetIncludes(b, a) {
  const missing = {};
  for (const [k, n] of Object.entries(a ?? {})) {
    const have = b?.[k] ?? 0;
    if (have < n) missing[k] = n - have;
  }
  return { ok: Object.keys(missing).length === 0, missing };
}

/** 每個 commandId 收到幾則 commandResult：{ 最多幾則, 超過一則的 commandId } */
export function resultsPerCommand(results) {
  const per = countBy(results ?? [], 'commandId');
  const over = Object.entries(per).filter(([, n]) => n > 1).map(([id]) => id);
  return { max: Math.max(0, ...Object.values(per)), over, per };
}

/** 兩次計數之間的增量（每個鍵；沒有的當 0）：ops/status 的 conversations.liveDropped、commands，假裝置的 session、liveRelay */
export function counterDelta(before, after) {
  const out = {};
  for (const k of new Set([...Object.keys(before ?? {}), ...Object.keys(after ?? {})])) out[k] = (after?.[k] ?? 0) - (before?.[k] ?? 0);
  return out;
}

/**
 * liveDropped 的增量裡不是預期原因的（> 0 的）：預期的是 final（持久版本已經完成）與 stale（被持久版本作廢、ver 沒有比較新）——
 * 即時更新只是提示，持久的版本一到就以它為準（web-protocol 的 item）。rate、dlp、notOwner、policy 在 FF2 都不該出現
 */
export function unexpectedDrops(delta, allowed = ['final', 'stale']) {
  return Object.entries(delta ?? {}).filter(([k, n]) => n > 0 && !allowed.includes(k)).map(([k, n]) => ({ reason: k, count: n }));
}

const CROCKFORD = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

/**
 * ULID（web-protocol 第六節第 5 點）：前 10 字是 ms 的 48 位元（大端、每 5 位元一字）、後 16 字是 bytes（10 個位元組）的 80 位元；
 * 大寫 Crockford Base32。ms 不是 0～2^48−1 的整數、bytes 不是 10 個位元組丟例外
 */
export function ulid(ms, bytes) {
  if (!Number.isInteger(ms) || ms < 0 || ms >= 2 ** 48) throw new Error(`ulid：時間要是 48 位元的整數：${ms}`);
  const b = [...(bytes ?? [])];
  if (b.length !== 10 || b.some((x) => !Number.isInteger(x) || x < 0 || x > 255)) throw new Error('ulid：要 10 個位元組');
  let time = BigInt(ms);
  let head = '';
  for (let i = 0; i < 10; i++) {
    head = CROCKFORD[Number(time & 31n)] + head;
    time >>= 5n;
  }
  let rand = 0n;
  for (const x of b) rand = (rand << 8n) | BigInt(x);
  let tail = '';
  for (let i = 0; i < 16; i++) {
    tail = CROCKFORD[Number(rand & 31n)] + tail;
    rand >>= 5n;
  }
  return head + tail;
}

/** 位元組或字串（UTF-8 與 UTF-16LE 兩種編碼）出現在 buf 裡：{ utf8, utf16 } */
export function containsSecret(buf, secret) {
  if (typeof secret !== 'string' || secret.length === 0) throw new Error('要有要找的字串');
  return { utf8: buf.includes(Buffer.from(secret, 'utf8')), utf16: buf.includes(Buffer.from(secret, 'utf16le')) };
}
