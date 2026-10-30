// 視窗標題（document.title）的純規則（app.js 用；tools/ui-probe/rules.test.mjs）。桌面 app 的視窗標題跟著它（宿主的 ShellRules.WindowTitle），
// 瀏覽器版就是分頁標題。app 名稱一律在最前面、後面才接目前對話的標題（2026-10-24 使用者要求），工作列與 Alt+Tab 一眼認得出是哪個程式

/** app 名稱（同宿主的 ShellRules.AppTitle） */
export const APP_TITLE = 'Clode Myriagent';

/** app 名稱與後面那段之間的分隔 */
export const TITLE_SEPARATOR = ' － ';

/**
 * 視窗標題：儀表板是「Clode Myriagent － 儀表板」、排程頁是「Clode Myriagent － 排程」；對話是「Clode Myriagent － 對話標題」（草稿是「新對話」）；
 * 沒有開著的對話、或標題是空白時只有 app 名稱
 */
export function pageTitle(view, header) {
  let detail = '';
  if (view === 'dashboard') detail = '儀表板';
  else if (view === 'schedules') detail = '排程';
  else if (header) detail = header.isDraft ? '新對話' : String(header.title ?? '').trim();
  return detail ? APP_TITLE + TITLE_SEPARATOR + detail : APP_TITLE;
}

/**
 * 瀏覽器版分頁圖示的燈（第七批視覺效果；favbadge.js）：有對話等你回應時是琥珀色並寫數字（最多寫到 9+），
 * 有對話執行中時是綠點，都沒有回 null。等你回應優先（同桌面的工作列與視窗外框）
 */
export function tabBadge(list) {
  let run = 0, wait = 0;
  for (const c of Array.isArray(list) ? list : []) {
    if (!c) continue;
    if (c.waiting) wait++;
    else if (c.running) run++;
  }
  if (wait > 0) return { kind: 'wait', n: wait, text: wait > 9 ? '9+' : String(wait) };
  if (run > 0) return { kind: 'run', n: run, text: '' };
  return null;
}

/** 分頁圖示要不要重畫：樣子一樣（同一種、同一個字）就不重畫 */
export function tabBadgeKey(badge) {
  return badge ? badge.kind + ':' + badge.text : '';
}
