// 網頁錯誤回報（web-protocol 第二節 clientError）的純規則：從事件組出回報、截短、節流。
// 同一則（種類＋訊息＋來源）10 分鐘內只送一次、每次載入頁面最多送 100 則；宿主（桌面、伺服器）另外以 ClientErrorRules 驗證與節流。
// 沒有 import（live-rules.test.mjs 以 data: URL 匯入）。

export const MAX_MESSAGE = 500;
export const MAX_SOURCE = 500;
export const MAX_DETAIL = 2000;
export const WINDOW_MS = 10 * 60 * 1000;
export const MAX_REPORTS = 100;

/** 不回報的例外訊息：瀏覽器自己的無害警告、跨來源指令碼沒有內容的「Script error.」 */
const IGNORED_MESSAGES = [/^ResizeObserver loop/, /^Script error\.?$/];

/** 去頭尾空白、超過 max 截短加「…」；不是字串或空的回 null */
export function clip(text, max) {
  if (typeof text !== 'string') return null;
  const t = text.trim();
  if (!t) return null;
  return t.length > max ? t.slice(0, max) + '…' : t;
}

/** 組一則回報；訊息是空的回 null */
export function report(kind, message, source, detail) {
  const m = clip(message, MAX_MESSAGE);
  if (!m) return null;
  return { kind, message: m, source: clip(source, MAX_SOURCE), detail: clip(detail, MAX_DETAIL) };
}

/** 丟出的東西 → 訊息與堆疊（Error 寫「名稱: 訊息」；其他照字串） */
function describeThrown(thrown) {
  if (thrown && typeof thrown === 'object' && typeof thrown.message === 'string') {
    const name = typeof thrown.name === 'string' && thrown.name ? thrown.name : 'Error';
    return { message: `${name}: ${thrown.message}`, stack: typeof thrown.stack === 'string' ? thrown.stack : null };
  }
  let text;
  try { text = String(thrown); } catch { text = '（無法轉成文字）'; }
  return { message: text, stack: null };
}

/** 沒被接住的例外（ErrorEvent 的 message、filename、lineno、colno、error）→ 回報；不回報的訊息回 null */
export function fromError(e) {
  if (!e) return null;
  const thrown = e.error != null ? describeThrown(e.error) : { message: typeof e.message === 'string' ? e.message : '', stack: null };
  if (IGNORED_MESSAGES.some((re) => re.test(thrown.message)) || IGNORED_MESSAGES.some((re) => re.test(String(e.message || '')))) return null;
  const source = e.filename ? `${e.filename}:${e.lineno || 0}:${e.colno || 0}` : null;
  return report('error', thrown.message, source, thrown.stack);
}

/** 沒處理的 Promise 拒絕的 reason → 回報 */
export function fromRejection(reason) {
  const thrown = describeThrown(reason);
  if (IGNORED_MESSAGES.some((re) => re.test(thrown.message))) return null;
  return report('rejection', thrown.message, null, thrown.stack);
}

/**
 * 載入失敗的媒體元素 → 回報：tag 是元素名稱（img、video、audio、source），url 是它要載入的網址。
 * 沒有網址、網址就是頁面本身（src 清空時會解析成頁面網址）不回報；data:、blob: 只寫通訊協定（內容可能很長）
 */
export function fromMedia(tag, url, pageUrl) {
  if (typeof url !== 'string' || !url || url === pageUrl) return null;
  const lower = url.slice(0, 5).toLowerCase();
  const source = lower === 'data:' ? 'data:…' : lower === 'blob:' ? 'blob:…' : url;
  return report('media', `${tag} 載入失敗`, source, null);
}

/** 節流：回傳 (回報) => 要不要送；同一則 WINDOW_MS 內只送一次、總共最多 MAX_REPORTS 則（now 給測試換時鐘） */
export function createLimiter(now = () => Date.now()) {
  const last = new Map();
  let sent = 0;
  return (r) => {
    if (!r || sent >= MAX_REPORTS) return false;
    const key = `${r.kind}|${r.message}|${r.source || ''}`;
    const t = now();
    const prev = last.get(key);
    if (prev !== undefined && t - prev < WINDOW_MS) return false;
    last.set(key, t);
    sent++;
    return true;
  };
}
