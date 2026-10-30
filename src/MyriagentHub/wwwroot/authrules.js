// Myriagent Hub 登入頁的純函式（module、沒有 import、不碰 DOM）：配對網址片段、登入後要去哪、配對錯誤的說明、配對碼的顯示格式、核准鈕何時解除停用。
// 測試：(cd tools/hub-e2e && node --test authrules.test.mjs)（以 data: URL 匯入）。
// 配對碼的正規化（字母表、長度、加連字號）以伺服器為準（PairingRules.NormalizeUserCode）；這裡只做送出前的清理。

/** 配對碼清理後最多幾個字（伺服器的配對碼是 8 個字；多留一些讓伺服器回「找不到」，而不是在這裡默默截成另一個碼） */
const MAX_CODE = 16;
/** 核准鈕解除停用前至少要等多久（毫秒）：從配對細節出現、視窗取得焦點或回到可見起算 */
const APPROVE_ARM_MS = 1000;

/**
 * 配對核准鈕能不能解除停用（DoubleClickjacking 的防線：惡意網站在雙擊的第一下關掉蓋在上面的彈出視窗，第二下落在核准鈕上）。
 * o：shown（畫面上有配對細節）、trusted（這次是真的使用者操作，isTrusted）、visible（頁面可見）、focused（頁面有焦點）、
 * now 與 since（毫秒；since＝細節出現、取得焦點或回到可見的時間）。全部成立、而且 now − since ≥ 1 秒才回 true
 */
export function canArmApprove(o) {
  if (!o || o.shown !== true || o.trusted !== true || o.visible !== true || o.focused !== true) return false;
  const now = Number(o.now);
  const since = Number(o.since);
  return Number.isFinite(now) && Number.isFinite(since) && now - since >= APPROVE_ARM_MS;
}

/**
 * 使用者輸入或網址帶來的配對碼：只留英文字母與數字（空白、連字號等一律拿掉）、轉大寫、最多 16 字；清完是空的回 null。
 * 先過濾再轉大寫：避免 Unicode 的大寫展開（例如 ß → SS）湊出字母
 */
export function cleanPairCode(raw) {
  if (typeof raw !== 'string') return null;
  const code = raw.replace(/[^A-Za-z0-9]/g, '').toUpperCase().slice(0, MAX_CODE);
  return code || null;
}

/**
 * 網址片段（location.hash）是不是配對核准頁：只認 '#pair' 與 '#pair=碼'（'#pairx'、'#other' 都不是）。
 * '#pair' → { pair: true, code: null }；'#pair=mtlz xfch' → { pair: true, code: 'MTLZXFCH' }；
 * 碼先 decodeURIComponent（壞的編碼當成沒有碼）再清理（cleanPairCode）。其他 → { pair: false }
 */
export function parsePairHash(hash) {
  const h = typeof hash === 'string' ? hash : '';
  if (h === '#pair') return { pair: true, code: null };
  if (!h.startsWith('#pair=')) return { pair: false };
  let raw;
  try {
    raw = decodeURIComponent(h.slice('#pair='.length));
  } catch {
    return { pair: true, code: null };
  }
  return { pair: true, code: cleanPairCode(raw) };
}

/** 登入成功之後去哪裡：網址是配對核准頁就留在這裡（回 null），其他一律到網頁版 '/app/' */
export function afterLoginTarget(hash, role) {
  // 協作人（多人協作）只有網頁版：配對、版本頁都不是他的
  if (role === 'collaborator') return '/app/';
  return parsePairHash(hash).pair ? null : '/app/';
}

/** auth/me 的回應是協作人（多人協作：協作人沒有帳戶頁、配對與版本頁，一律到網頁版） */
export function isCollaboratorMe(me) {
  return !!me && typeof me === 'object' && me.role === 'collaborator';
}

/** 配對 API（pending、approve、deny）的錯誤說明：先看伺服器的錯誤代碼，認不得再看 HTTP 狀態；status 0＝連不上伺服器 */
export function pairErrorText(status, code) {
  switch (code) {
    case 'not_found': return '找不到這個配對碼：可能打錯了、已經過期（配對碼 10 分鐘內有效），或已經核准或拒絕過。';
    case 'invalid_request': return '這個配對請求不能核准（例如那台電腦的金鑰已經配對過）。請在那台電腦重新開始配對。';
    case 'too_many_devices': return '配對過的電腦（含已撤銷的）已達上限，不能再加入新的電腦。請先移除已撤銷的電腦。';
    case 'rate_limited': return '嘗試太頻繁，請稍後再試。';
    case 'forbidden_origin': return '請求來源不符，請重新整理頁面。';
    default: break;
  }
  switch (status) {
    case 0: return '連不上伺服器，請確認網路後再試。';
    case 400: return '這個配對請求不能核准。請在那台電腦重新開始配對。';
    case 403: return '請求被拒絕，請重新整理頁面。';
    case 404: return '找不到這個配對碼：可能打錯了、已經過期，或已經處理過。';
    case 409: return '配對過的電腦已達上限，不能再加入新的電腦。';
    case 429: return '嘗試太頻繁，請稍後再試。';
    default: return Number.isInteger(status) && status > 0 ? `伺服器回應 ${status}，請稍後再試。` : '發生錯誤，請稍後再試。';
  }
}

/** 配對碼的顯示格式：正好 8 個英數字 → 'XXXX-XXXX'；其他照原樣（只用來顯示，送出前一律 cleanPairCode） */
export function formatUserCode(code) {
  const s = typeof code === 'string' ? code : '';
  return /^[A-Z0-9]{8}$/.test(s) ? `${s.slice(0, 4)}-${s.slice(4)}` : s;
}

// ───── 逐字解碼（第八批視覺效果：配對核准頁的裝置指紋與裝置名稱出現時先閃過亂碼再由左往右鎖定，同桌面設定頁的配對碼） ─────
const DECODE_GLYPHS = 'ABCDEFGHJKLMNPQRSTUVWXYZ0123456789';

/** 解碼要幾格：每格多鎖定幾個字，最多 18 格（約 0.7 秒）；空字串 0 格 */
export function decodeSteps(length) {
  const n = Number.isInteger(length) && length > 0 ? length : 0;
  return Math.min(n, 18);
}

/** 第 step 格（1 起）時已鎖定的字數：最後一格一定全部鎖定 */
export function decodeLocked(length, step, steps) {
  if (!(steps > 0) || step >= steps) return length;
  return Math.max(0, Math.min(length, Math.ceil((length * step) / steps)));
}

/**
 * 解碼的一格：前 locked 個字照原文，其餘的英數字換成亂碼（大小寫照原本的）；其他字（中文、空白、標點）照原樣，
 * 寬度不跳（指紋是等寬字）。rand 回 0～1（測試給固定值）；以碼位走，不切到代理對中間
 */
export function decodeFrame(text, locked, rand) {
  const chars = Array.from(typeof text === 'string' ? text : '');
  return chars.map((c, i) => {
    if (i < locked || !/[A-Za-z0-9]/.test(c)) return c;
    const g = DECODE_GLYPHS[Math.min(DECODE_GLYPHS.length - 1, Math.floor(rand() * DECODE_GLYPHS.length))];
    return /[a-z]/.test(c) ? g.toLowerCase() : g;
  }).join('');
}
