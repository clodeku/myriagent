// 放大窗格的純規則（workspace.js；2026-11-08 使用者要求：同時開著好幾個對話框時暫時拉長其中一個
// 〔預設兩倍、不超過視窗邊界〕，一次讀得到更多內容。2026-11-10 起不再點了就放大，由對話標頭右上角的按鈕切換。桌面與網頁版都一樣）。
// 沒有 import（tools/ui-probe 的 panezoomrules.test.mjs 以 data: URL 匯入）。

/** 預設放大成原本的幾倍 */
export const PANE_ZOOM = 2;

/** 窗格和工作區差不到這麼多就算已經佔滿（格線的 1px 間隔、取整數的誤差） */
const FULL_SLACK = 2;

/**
 * 放大的方式：p＝窗格、ws＝工作區（getBoundingClientRect 的 top／bottom／left／right／width／height）。
 * 上下還有空間（好幾列）就拉長：axis 'y'、size＝新的高度（原本的 factor 倍，不超過工作區）、shift＝往上移多少（下面不夠的部分往上補）；
 * 已經滿高（只有一列並排）就加寬：axis 'x'、shift＝往左移多少；兩個方向都已經佔滿（只有一個窗格）回 null。都取整數
 */
export function zoomPlan(p, ws, factor = PANE_ZOOM) {
  if (!p || !ws || !(p.width > 0) || !(p.height > 0)) return null;
  const f = Number.isFinite(factor) && factor > 1 ? factor : PANE_ZOOM;
  if (ws.height - p.height > FULL_SLACK) return grow('y', p.height, p.bottom, ws.height, ws.bottom, f);
  if (ws.width - p.width > FULL_SLACK) return grow('x', p.width, p.right, ws.width, ws.right, f);
  return null;
}

/** 沿一個方向放大：size 原本的長度、end 原本的終點、room 工作區的長度、limit 工作區的終點 */
function grow(axis, size, end, room, limit, f) {
  const want = Math.min(size * f, room);
  const extra = Math.max(0, want - size);
  const after = Math.max(0, Math.min(extra, limit - end));
  return { axis, size: Math.round(size + extra), shift: Math.round(extra - after) };
}
