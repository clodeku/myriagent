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

/** 放開在分頁列的第幾個位置：游標在第幾個分頁的中線之前（mids：各分頁中線的座標，橫排是 x 由左到右、側排是 y 由上到下）；都在後面就是最後 */
export function dropIndexAt(mids, x) {
  for (let i = 0; i < mids.length; i++) if (x < mids[i]) return i;
  return mids.length;
}

/**
 * 插入記號的位置（相對分頁列的起點，tabs：各分頁的 { left, right }，同一個基準；側排時傳 { left: 上緣, right: 下緣 }）：插在第 i 個分頁前面是它的起點，
 * 插在最後是最後一個的終點，沒有分頁是 0；記號粗 3px，往前 2px 置中、不小於 0、取整數
 */
export function insertMarkX(tabs, i) {
  const x = tabs[i] ? tabs[i].left : tabs.length ? tabs[tabs.length - 1].right : 0;
  return Math.round(Math.max(0, x - 2));
}

/** 分頁放在窗格左側（2026-11-05 使用者要求：窗格上方的橫式分頁也可以改成左邊側排）時那一欄的寬度 */
export const SIDE_TABS_WIDTH = 184;

/** 窗格至少這麼寬才側排（扣掉分頁欄，對話區還有約 276px；1440 寬的視窗並排兩格約 480px）：再窄的話對話區被擠得太窄（手機、分很多格時），照用上方的橫排 */
export const SIDE_TABS_MIN_WIDTH = 460;

/** 拖曳調整分頁欄的寬度（2026-11-05 使用者要求）：最窄、最寬 */
export const SIDE_TABS_MIN = 120;
export const SIDE_TABS_MAX = 360;

/** 側排時至少留給對話區的寬度（SIDE_TABS_MIN_WIDTH 減掉預設的分頁欄寬） */
export const SIDE_CHAT_MIN = SIDE_TABS_MIN_WIDTH - SIDE_TABS_WIDTH;

/**
 * 分頁欄的寬度：w＝想要的（拖曳中游標的位置、存下的值；不是有限的數字就用預設），paneWidth＝窗格寬度（0＝不知道，只夾在最窄最寬之間）。
 * 夾在 SIDE_TABS_MIN～SIDE_TABS_MAX，而且留給對話區至少 SIDE_CHAT_MIN；取整數
 */
export function clampSideWidth(w, paneWidth) {
  const want = Number.isFinite(w) ? w : SIDE_TABS_WIDTH;
  const max = paneWidth > 0 ? Math.min(SIDE_TABS_MAX, paneWidth - SIDE_CHAT_MIN) : SIDE_TABS_MAX;
  return Math.round(Math.max(SIDE_TABS_MIN, Math.min(max, want)));
}

/**
 * 這個窗格現在要不要側排：pref＝使用者選的（'left' 或 'top'）、width＝窗格寬度（看不到時是 0：沿用 was，
 * 切到儀表板再回來時不會先變橫排又變回來）。使用者拉寬的分頁欄在窗格變窄時先縮窄（clampSideWidth），不到 SIDE_TABS_MIN_WIDTH 才改回橫排
 */
export function sideTabsOn(pref, width, was) {
  if (pref !== 'left') return false;
  if (!(width > 0)) return !!was;
  return width >= SIDE_TABS_MIN_WIDTH;
}
