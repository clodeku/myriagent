// 圖片燈箱的縮放範圍與觸控手勢（兩指捏合、雙點、點一下與拖曳的分辨）：viewer.js 接指標事件，這裡只算數字。
// 純函式、沒有 import（tools/ui-probe/viewerrules.test.mjs 以 data: URL 匯入）；座標一律相對舞台（.lb-stage）的左上角

/** 雙點：兩下的間隔（毫秒）與距離（像素）上限 */
export const DOUBLE_TAP_MS = 300;
export const DOUBLE_TAP_PX = 30;

/** 縮放的範圍：最小是符合視窗與 10% 較小的那個，最大 800% */
export function zoomRange(fitScale) {
  return { min: Math.min(fitScale, 0.1), max: 8 };
}

/** 移動超過這個距離（|dx|+|dy|）就是拖曳、不是點一下：滑鼠 3px；手指會抖，觸控與觸控筆放寬到 10px */
export function tapSlop(pointerType) {
  return pointerType === 'mouse' ? 3 : 10;
}

/**
 * 兩指捏合開始：記下兩指的距離、當時的倍率，以及兩指中點底下是圖片的哪一點（圖片的原始像素座標）。
 * view 是 { scale, tx, ty }（圖片左上角在舞台上的位置與倍率），a、b 是兩指的 { x, y }；兩指疊在同一點時回 null
 */
export function pinchStart(view, a, b) {
  const dist = Math.hypot(b.x - a.x, b.y - a.y);
  if (!(dist > 0) || !(view.scale > 0)) return null;
  const mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
  return { dist, scale: view.scale, ix: (mx - view.tx) / view.scale, iy: (my - view.ty) / view.scale };
}

/**
 * 捏合中：倍率照兩指距離的比例縮放（夾在 range 內），開始時中點底下的那一點跟著現在的中點走（兩指一起移動＝平移）。
 * 回 { scale, tx, ty }；不讓圖片離開視窗與置中由燈箱的 constrain 處理
 */
export function pinchMove(start, a, b, range) {
  const dist = Math.hypot(b.x - a.x, b.y - a.y);
  const scale = Math.min(range.max, Math.max(range.min, start.scale * dist / start.dist));
  const mx = (a.x + b.x) / 2, my = (a.y + b.y) / 2;
  return { scale, tx: mx - start.ix * scale, ty: my - start.iy * scale };
}

/** 捏合放開時比符合視窗還小：彈回符合視窗 */
export function snapBackAfterPinch(scale, fitScale) {
  return scale < fitScale - 0.001;
}

/** 這一下和上一下算不算雙點（prev、tap 是 { t, x, y }，t 是毫秒；prev 為 null 時不算） */
export function isDoubleTap(prev, tap) {
  if (!prev || !tap) return false;
  const dt = tap.t - prev.t;
  return dt >= 0 && dt <= DOUBLE_TAP_MS && Math.hypot(tap.x - prev.x, tap.y - prev.y) <= DOUBLE_TAP_PX;
}

/** 燈箱左上的影像資訊：「1920 × 1080・PNG」（實際解析度與副檔名；沒有尺寸回空字串，認不出格式只寫解析度） */
export function imageInfoLabel(nw, nh, nameOrUrl) {
  if (!(nw > 0) || !(nh > 0)) return '';
  const m = /\.([a-z0-9]{2,5})(?:$|[?#])/i.exec(typeof nameOrUrl === 'string' ? nameOrUrl : '');
  return nw + ' × ' + nh + (m ? '・' + m[1].toUpperCase() : '');
}
