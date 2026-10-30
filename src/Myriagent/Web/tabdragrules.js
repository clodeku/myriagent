// 分頁拖放的純規則（workspace.js）：拖曳期間要不要先排著畫面的更新、放開在分頁列的第幾個位置、插入記號畫在哪裡。
// 沒有 import（tools/ui-probe 的 tabdragrules.test.mjs 以 data: URL 匯入）。
// 2026-10-25 實機：15 個對話同時執行時主視窗的渲染程序一直超過一顆核心，拖分頁時 dragover 排在串流的畫面更新後面，
// 換位置、搬到別的窗格或視窗都一頓一頓、放開後要等

/** 這個視窗自己拖的分頁最多算多久（dragend 漏了，排著的畫面更新也會在這之後補上） */
export const OWN_DRAG_HOLD_MS = 15000;

/** 別的視窗在拖的分頁（宿主的 tabDrag）最多算多久：同宿主的 WindowRules.TabDragTtl，結束的通知漏了也不會一直當成在拖 */
export const EXTERNAL_DRAG_MS = 15000;

/**
 * 拖曳分頁期間：ownStartedAt＝這個視窗開始拖的時間（沒有在拖是 null）、externalAt＝宿主通知別的視窗開始拖的時間（null＝沒有）。
 * active＝還在拖（先排著串流的畫面更新與整份清單，主執行緒留給拖放）；recheckMs＝到期前多久要再看一次（不是 active 時是 0）。
 * 時鐘往回跳（now 比開始的時間早）時以上限計
 */
export function tabDragHold(ownStartedAt, externalAt, now) {
  const left = (at, limit) => (typeof at === 'number' && Number.isFinite(at) ? Math.min(limit, limit - (now - at)) : 0);
  const ms = Math.max(left(ownStartedAt, OWN_DRAG_HOLD_MS), left(externalAt, EXTERNAL_DRAG_MS));
  return ms > 0 ? { active: true, recheckMs: Math.ceil(ms) } : { active: false, recheckMs: 0 };
}

/** 放開在分頁列的第幾個位置：游標在第幾個分頁的中線左邊（mids：各分頁中線的 x，由左到右）；都在右邊就是最後 */
export function dropIndexAt(mids, x) {
  for (let i = 0; i < mids.length; i++) if (x < mids[i]) return i;
  return mids.length;
}

/**
 * 插入記號的 x（相對分頁列的左緣，tabs：各分頁的 { left, right }，同一個基準）：插在第 i 個分頁前面是它的左緣，
 * 插在最後是最後一個的右緣，沒有分頁是 0；記號寬 3px，往左 2px 置中、不小於 0、取整數
 */
export function insertMarkX(tabs, i) {
  const x = tabs[i] ? tabs[i].left : tabs.length ? tabs[tabs.length - 1].right : 0;
  return Math.round(Math.max(0, x - 2));
}
