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

// ───── 拖曳關閉（觸控：沒有放大時單指把圖片或影片拖到旁邊放開就關閉）─────

/** 拖曳關閉只在沒有放大時（符合視窗或更小）：放大後單指拖曳照舊是平移 */
export function canSwipeDismiss(scale, fitScale) {
  return scale <= fitScale + 0.001;
}

/** 拖到這麼遠才算數的參考距離：舞台短邊的 40%，至少 120px */
function dismissRef(w, h) {
  return Math.max(120, Math.min(w, h) * 0.4);
}

/** 拖曳關閉的進度 0～1（背景跟著變淡、工具列跟著收）：拖動距離對參考距離的比例 */
export function dismissProgress(dx, dy, w, h) {
  const d = Math.hypot(dx, dy);
  return d > 0 ? Math.min(1, d / dismissRef(w, h)) : 0;
}

/** 甩出去：放開前的速度至少這麼快（px／ms），而且已經拖了一段距離、方向和位移相同 */
export const FLING_SPEED = 0.6;
export const FLING_MIN_PX = 24;

/**
 * 放開時要不要關閉：拖過舞台短邊的 18%（至少 80px），或往拖的方向甩出去。
 * dx、dy 是放開時的位移，vx、vy 是放開前的速度（px／ms，releaseVelocity），w、h 是舞台大小
 */
export function shouldDismiss(dx, dy, vx, vy, w, h) {
  const d = Math.hypot(dx, dy);
  if (d >= Math.max(80, Math.min(w, h) * 0.18)) return true;
  if (d < FLING_MIN_PX) return false;
  const speed = Math.hypot(vx, vy);
  // 速度在位移方向上的分量：往回拉的不算甩出去
  const along = (vx * dx + vy * dy) / d;
  return speed >= FLING_SPEED && along >= FLING_SPEED * 0.7;
}

/**
 * 放開前的速度（px／ms）：samples 是依時間排序的 { t, x, y }，取最後 100 毫秒內的頭尾相減；
 * 停在原地超過 100 毫秒才放開（只剩一點）是 0
 */
export function releaseVelocity(samples, windowMs = 100) {
  if (!samples || samples.length < 2) return { vx: 0, vy: 0 };
  const last = samples[samples.length - 1];
  let first = last;
  for (let i = samples.length - 2; i >= 0; i--) {
    if (last.t - samples[i].t > windowMs) break;
    first = samples[i];
  }
  const dt = last.t - first.t;
  if (!(dt > 0)) return { vx: 0, vy: 0 };
  return { vx: (last.x - first.x) / dt, vy: (last.y - first.y) / dt };
}

/** 關閉時沿著拖曳的方向飛出舞台：從原位算起的總位移（距離是舞台的對角線，一定飛得出去）；沒有位移時往下 */
export function flingOffset(dx, dy, w, h) {
  const d = Math.hypot(dx, dy);
  const far = Math.hypot(w, h);
  if (!(d > 0)) return { x: 0, y: far };
  return { x: dx / d * far, y: dy / d * far };
}

// ───── 長按（觸控長按＝右鍵選單）─────

/** 按著不動這麼久（毫秒）就是長按 */
export const LONG_PRESS_MS = 500;

/** 長按期間手指移動超過這個距離（|dx|+|dy|，同觸控的 tapSlop）就不是長按（是捲動或拖曳） */
export function longPressMoved(dx, dy) {
  return Math.abs(dx) + Math.abs(dy) > tapSlop('touch');
}

/**
 * 這次的 contextmenu 要不要略過：長按時自己送過一次（firedAt），瀏覽器（Android、Windows 觸控）在 windowMs 內又送一次原生的。
 * firedAt 是上次自己送的時間（沒有時 -Infinity），now 是現在
 */
export function duplicateContextMenu(firedAt, now, windowMs = 1000) {
  return now - firedAt >= 0 && now - firedAt < windowMs;
}
