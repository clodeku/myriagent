// 〔遠端〕指令結果的追蹤（WP4.6 D5）：只追有畫面要改的指令（卡片的鎖定、送出、刪除），規則在 liverules.js。
// 瀏覽器版的 post 回 commandId（桌面回 undefined、沒送出回 null）；追蹤的指令在結果到了（commandResult）、新的 init、
// 150 秒還等不到時各呼叫一次 info.onSettle(state, code, origin)：state 是 done／rejected／unknown，新的 init 與過期是 ('unknown', null, null)。
// 沒有登記的 commandId 一律略過（網頁沒有通用的結果處理）；unknown 不自動重送（可能已執行）。
// 結果到了（commandResult）時 onSettle 另帶第四個參數：送出到結果回來的毫秒數（輸入框下方的往返讀數，fxrules.rttLabel）；其他情況是 null。
// WP4.8 步 12 與第 5 階段照用：import { track } from './cmdtrack.js'
import { trackNew, trackAdd, trackSettle, trackAbandon, trackExpire } from './liverules.js';

const table = trackNew();
/** 送出的時間（performance.now；commandId → 毫秒）：結果回來時算往返時間 */
const sentAt = new Map();

/** 追蹤一個送出的指令；commandId 不是非空字串（桌面、沒送出）或已登記過時不追、回 false */
export function track(commandId, info) {
  const ok = trackAdd(table, commandId, info, Date.now());
  if (ok) sentAt.set(commandId, performance.now());
  return ok;
}

/** commandResult：認得的 id 交給它的 onSettle、回 true；不認得、已結束的略過、回 false */
export function onResult(msg) {
  const t0 = msg && typeof msg.commandId === 'string' ? sentAt.get(msg.commandId) : undefined;
  const s = trackSettle(table, msg);
  if (!s) return false;
  sentAt.delete(msg.commandId);
  settle(s.info, s.state, s.code, s.origin, typeof t0 === 'number' ? performance.now() - t0 : null);
  return true;
}

/** 新的 init（重連、伺服器 resync）：還在等的全部當成結果不明 */
export function abandonAll() {
  sentAt.clear();
  for (const info of trackAbandon(table)) settle(info, 'unknown', null, null);
}

/** 還在等結果的指令數（伺服器換版時有指令在路上就先不重新載入，D13） */
export function pendingCount() {
  return table.items.size;
}

/** 每秒呼叫（分頁隱藏也要呼叫，D5）：登記超過 150 秒的當成結果不明 */
export function sweep(now) {
  for (const info of trackExpire(table, now)) settle(info, 'unknown', null, null);
  // 過期的已移出追蹤表：送出時間也一起丟（多留幾秒，不和 trackExpire 的邊界搶）
  const t = performance.now();
  for (const [id, at] of sentAt) if (t - at > SENT_KEEP_MS) sentAt.delete(id);
}

const SENT_KEEP_MS = 160_000;

function settle(info, state, code, origin, elapsed = null) {
  if (!info || typeof info.onSettle !== 'function') return;
  try {
    info.onSettle(state, code, origin, elapsed);
  } catch (e) {
    // 一個畫面的處理出錯不影響其他追蹤；照常報出（主控台看得到）
    if (typeof reportError === 'function') reportError(e); else setTimeout(() => { throw e; });
  }
}
