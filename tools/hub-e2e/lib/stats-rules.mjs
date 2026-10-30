// 統計與比對的純函式（WP4.9 步 7；full-flow 的報告與情境用）：百分位數、時間標記、摘要、兩次結果的比對、由指令 id 推對話 id。
// 不碰檔案、網路與時鐘。

/** 百分位數（nearest-rank：排序後第 ceil(p/100 × N) 個）；空陣列丟例外 */
export function percentile(values, p) {
  if (!Array.isArray(values) || values.length === 0) throw new Error('percentile：沒有樣本');
  if (!(p > 0 && p <= 100)) throw new Error(`percentile：p 要在 (0, 100]，收到 ${p}`);
  const sorted = values.map(Number).sort((a, b) => a - b);
  const rank = Math.ceil((p / 100) * sorted.length);
  return sorted[Math.min(sorted.length, Math.max(1, rank)) - 1];
}

// 時間標記（計畫 D8）：全形括號裡剛好 13 位數字（UTC 毫秒）。全形括號不是權杖字元，13 位數字少於 base64 樣式的 16 字，DLP 不會拖住或遮掉
const STAMP = /〔(\d{13})〕/gu;

/** 文字裡的時間標記（UTC 毫秒，依出現順序）；不是剛好 13 位數字的不算 */
export function parseStamps(text) {
  if (typeof text !== 'string' || text.length === 0) return [];
  const out = [];
  for (const m of text.matchAll(STAMP)) out.push(Number(m[1]));
  return out;
}

/** 摘要：{count, min, max, mean, p50, p95, p99}（沒有樣本時只有 count: 0） */
export function summarize(samples) {
  const values = (samples ?? []).map(Number).filter((v) => Number.isFinite(v));
  if (values.length === 0) return { count: 0 };
  const sum = values.reduce((a, b) => a + b, 0);
  return {
    count: values.length,
    min: Math.min(...values),
    max: Math.max(...values),
    mean: Math.round((sum / values.length) * 10) / 10,
    p50: percentile(values, 50),
    p95: percentile(values, 95),
    p99: percentile(values, 99),
  };
}

// 比對兩次結果時略過的欄位：時間點、耗時、程序與埠（同一個情境重跑一定不同，判斷「結果相同」時不看）
const TIME_KEYS = /(?:At|Ms|Seconds|Time|Pid|Port)$|^(?:at|ms|elapsed|duration|time|pid|port|startedAt|finishedAt)$/u;

function strip(value) {
  if (Array.isArray(value)) return value.map(strip);
  if (value !== null && typeof value === 'object') {
    const out = {};
    for (const key of Object.keys(value).sort()) {
      if (TIME_KEYS.test(key)) continue;
      out[key] = strip(value[key]);
    }
    return out;
  }
  return value;
}

/** 兩次結果是不是同一個結果（不看時間、耗時、PID、埠；物件的鍵不看順序） */
export function sameOutcome(a, b) {
  return JSON.stringify(strip(a)) === JSON.stringify(strip(b));
}

const CROCKFORD = '0123456789ABCDEFGHJKMNPQRSTVWXYZ';

/**
 * 瀏覽器建立的新對話的線上 id（照 C# 的 RemoteConversationIdRules.FromCommandId，設計:3264）：ULID（26 字大寫 Crockford Base32、
 * 第一個字不大於 7）的 128 位元依大端轉成 32 個小寫十六進位；不是 ULID 回 null
 */
export function idFromCommandId(cid) {
  if (typeof cid !== 'string' || cid.length !== 26 || cid[0] > '7') return null;
  let value = 0n;
  for (const c of cid) {
    const digit = CROCKFORD.indexOf(c);
    if (digit < 0) return null;
    value = (value << 5n) | BigInt(digit);
  }
  return value.toString(16).padStart(32, '0');
}
