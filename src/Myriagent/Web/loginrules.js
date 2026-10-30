// 帳戶登入的到期（AccountView.loginExpiresAt；目前只有 Claude：憑證檔的 refreshTokenExpiresAt）：純規則。
// 沒有 import、不碰 DOM、不讀時鐘：現在時間一律由呼叫端傳入，日期文字也由呼叫端格式化（tools/ui-probe/rules.test.mjs 直接匯入測試）。
// 到期由 Anthropic 決定、app 不能延長；CLI 自動換發 8 小時的存取權杖不會讓它往後延（2026-10-23 實測），重新登入才會重新起算。
// Claude CLI 自己在剩 3 天時提醒「run /login to renew」，這裡用同一個門檻。

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** 剩多久開始提醒（同 Claude CLI 與 remoterules.LOGIN_EXPIRING_MS） */
export const LOGIN_SOON_MS = 3 * DAY;

/**
 * 本機已登入帳戶的登入到期：{ at, left, level, short, phrase }，沒有到期資料、不是已登入的本機帳戶回 null。
 * level：'ok'（還久）｜'soon'（剩 3 天內）｜'expired'（已過，CLI 還沒發現：下次換發權杖會失敗、變成未登入）；
 * short：帳戶卡上的剩餘時間（2d／5h／45m／<1m，無條件捨去；已過是「到期」）；phrase：說明裡的「2 天後」「5 小時後」（已過是空字串）
 */
export function loginExpiry(account, now) {
  if (!account || typeof account !== 'object' || account.remoteOnly === true || account.status !== 'ready') return null;
  const at = account.loginExpiresAt;
  if (typeof at !== 'number' || !Number.isFinite(at) || at <= 0 || typeof now !== 'number' || !Number.isFinite(now)) return null;
  const left = at - now;
  const level = left <= 0 ? 'expired' : left <= LOGIN_SOON_MS ? 'soon' : 'ok';
  return { at, left, level, short: shortLeft(left), phrase: phraseLeft(left) };
}

function shortLeft(left) {
  if (left <= 0) return '到期';
  if (left >= DAY) return Math.floor(left / DAY) + 'd';
  if (left >= HOUR) return Math.floor(left / HOUR) + 'h';
  if (left >= MINUTE) return Math.floor(left / MINUTE) + 'm';
  return '<1m';
}

function phraseLeft(left) {
  if (left <= 0) return '';
  if (left >= DAY) return Math.floor(left / DAY) + ' 天後';
  if (left >= HOUR) return Math.floor(left / HOUR) + ' 小時後';
  if (left >= MINUTE) return Math.floor(left / MINUTE) + ' 分鐘後';
  return '不到 1 分鐘後';
}

/** 說明文字（帳戶頁的一行、側欄卡片與標示的提示）；x＝loginExpiry 的結果，dateText＝呼叫端格式化的到期時間（例如 11/12 11:40） */
export function loginExpiryText(x, dateText) {
  if (!x) return '';
  const when = dateText ? String(dateText) : '';
  if (x.level === 'expired') return '登入已到期' + (when ? '（' + when + '）' : '') + '，請重新登入';
  if (x.level === 'soon') return '登入將在 ' + x.phrase + '到期' + (when ? '（' + when + '）' : '') + '，重新登入後會重新起算';
  return '登入到期：' + (when ? when + '（' + x.phrase + '）' : x.phrase);
}
