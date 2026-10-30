// 多人協作的純規則（docs/plans/2026-10-30-collaborators.md；web-protocol 的 collaborators、collab*、init.viewer；tools/ui-probe/collabrules.test.mjs）。
// 帳號、顯示名稱、密碼的格式和伺服器的 CollaboratorRules、PasswordRules 相同（網頁先擋，伺服器照樣再驗一次）。不碰 DOM

export const MIN_USER_NAME = 3;
export const MAX_USER_NAME = 32;
export const MAX_DISPLAY_NAME = 32;
export const MIN_PASSWORD = 8;
export const MAX_PASSWORD = 128;

const RESERVED = new Set(['admin', 'owner']);
const USER_CHARS = /^[a-z0-9][a-z0-9._@-]*$/;
// 控制（Cc）、格式（Cf）、行分隔（Zl）、段落分隔（Zp）：同信封標籤的規則（CommandEnvelope.IsValidIssuerLabel）
const BAD_LABEL_CHARS = /[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/u;

/** 這份網頁的使用者是協作人（init.viewer.role；桌面與擁有者的瀏覽器是 owner 或沒有） */
export function isCollaboratorViewer(viewer) {
  return !!viewer && typeof viewer === 'object' && viewer.role === 'collaborator';
}

/** 帳號：去頭尾空白、轉小寫後 3～32 字的英數字與 ._-@，第一個字是英數字，不是 admin／owner；不合格回 null */
export function cleanUserName(raw) {
  if (typeof raw !== 'string') return null;
  const name = raw.trim().toLowerCase();
  if (name.length < MIN_USER_NAME || name.length > MAX_USER_NAME || !USER_CHARS.test(name) || RESERVED.has(name)) return null;
  return name;
}

/** 顯示名稱：連續空白摺成一個、去頭尾後 1～32 個字（以 Unicode 字元算），不能有控制與格式字元；不合格回 null */
export function cleanDisplayName(raw) {
  if (typeof raw !== 'string') return null;
  const text = raw.split(/\s+/u).filter(Boolean).join(' ');
  if (!text || BAD_LABEL_CHARS.test(text) || [...text].length > MAX_DISPLAY_NAME) return null;
  return text;
}

/** 新密碼的問題（同伺服器的錯誤代碼）；合格回 null */
export function passwordProblem(password) {
  if (typeof password !== 'string' || password.length < MIN_PASSWORD) return 'password_too_short';
  if (password.length > MAX_PASSWORD) return 'password_too_long';
  if (/\p{Cc}/u.test(password)) return 'password_invalid';
  return null;
}

/** 管理指令的錯誤代碼（collabResult.code、伺服器的 CollaboratorCodes）→ 給人看的說明 */
export function errorText(code) {
  switch (code) {
    case 'invalid': return '欄位的格式不對（帳號 3～32 個英數字、顯示名稱 1～32 字）';
    case 'user_name_taken': return '這個帳號已經有人用了，換一個';
    case 'not_found': return '找不到這位協作人或這個對話（可能已經刪除，或對話還沒同步到伺服器）';
    case 'too_many': return '協作人已經達到上限';
    case 'password_too_short': return `密碼至少要 ${MIN_PASSWORD} 個字`;
    case 'password_too_long': return `密碼最多 ${MAX_PASSWORD} 個字`;
    case 'password_invalid': return '密碼不能有控制字元';
    case 'unavailable': return '伺服器暫時連不上，設定沒有送出；稍後再試';
    default: return '沒有成功（' + (code || '原因不明') + '）';
  }
}

/** 協作人狀態（init.collaborators／collaborators 訊息）的清單；沒有是空陣列 */
export function listOf(collaborators) {
  return collaborators && Array.isArray(collaborators.list) ? collaborators.list.filter((c) => c && typeof c.id === 'string') : [];
}

/** 這個對話分享給哪幾位（依清單的順序）；沒有分享是空陣列 */
export function sharedWith(collaborators, conversationId) {
  const ids = collaborators && collaborators.shares && Array.isArray(collaborators.shares[conversationId]) ? collaborators.shares[conversationId] : [];
  if (!ids.length) return [];
  const set = new Set(ids);
  return listOf(collaborators).filter((c) => set.has(c.id));
}

/** 這位協作人有幾個分享的對話 */
export function shareCount(collaborators, collaboratorId) {
  if (!collaborators || !collaborators.shares || typeof collaborators.shares !== 'object') return 0;
  let n = 0;
  for (const ids of Object.values(collaborators.shares)) if (Array.isArray(ids) && ids.includes(collaboratorId)) n++;
  return n;
}

/** 清單上的一行狀態：停用 > 在線 > 最後登入時間 > 還沒登入過（fmt 是時間的格式化函式） */
export function statusText(c, fmt) {
  if (!c) return '';
  if (c.disabled) return '已停用';
  if (c.online) return '在線';
  if (typeof c.lastLoginAt === 'number' && c.lastLoginAt > 0) return '最後登入 ' + (fmt ? fmt(c.lastLoginAt) : new Date(c.lastLoginAt).toLocaleString());
  return '還沒登入過';
}

/**
 * 使用者訊息上「誰送的」：協作人送的（item.author）一律標出名稱；協作人看擁有者送的（沒有 author、不是 claude.ai 送的）標「管理員」；
 * 擁有者看自己的不標（回 null）
 */
export function authorLabel(item, viewer) {
  if (!item) return null;
  if (typeof item.author === 'string' && item.author) return isCollaboratorViewer(viewer) ? item.author : '協作人 ' + item.author;
  if (isCollaboratorViewer(viewer) && item.source !== 'remote') return '管理員';
  return null;
}

/** 卡片上「誰回應的」：協作人回應的寫名稱；擁有者看自己回應的不寫 */
export function resolvedLabel(item, viewer) {
  if (!item || typeof item.resolvedBy !== 'string' || !item.resolvedBy) return null;
  return isCollaboratorViewer(viewer) ? item.resolvedBy + ' 回應' : '協作人 ' + item.resolvedBy + ' 回應';
}

/** 分享對話框的勾選 → collabShare 的 userIds（只留清單裡的、依清單的順序、去重） */
export function shareIds(collaborators, checked) {
  const want = new Set(Array.isArray(checked) ? checked : []);
  return listOf(collaborators).filter((c) => want.has(c.id)).map((c) => c.id);
}
