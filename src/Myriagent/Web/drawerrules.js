// 觸控左右滑叫出與收起兩側面板（左邊的側欄、右邊的電腦狀態）的純規則：左右滑的方向判斷、對話那一側往哪邊滑叫出哪一個、
// 拖曳中的展開比例與位置、放開時開或關、哪些觸控讓給捲動或打字。side 是 'left'（側欄，往右滑展開）或 'right'（電腦狀態，往左滑展開），省略是 'left'。
// 沒有 import、沒有 DOM（tools/ui-probe/drawerrules.test.mjs）；手勢的接線在 drawer.js

/** 手指移動超過這麼多（px）才判斷方向 */
export const SLOP = 10;
/** 水平位移要比垂直多這麼多倍才算左右滑（斜著滑交給捲動） */
export const AXIS_RATIO = 1.2;
/** 放開時的速度（px/ms）達到這個就照甩的方向開或關，不看拉了多遠 */
export const FLING = 0.35;
/** 速度只看最後這段時間（ms）的移動 */
export const VELOCITY_WINDOW = 100;
/** 並排的欄位與手機版的電腦狀態：放開後自己滑到開或關的位置要多久（ms），之後才改狀態 */
export const SETTLE_MS = 240;

/**
 * 方向鎖定：移動還沒超過 SLOP 回 null（再等）；水平明顯較多回 'x'，否則 'y'（這次觸控交給捲動，不再接手）
 */
export function lockAxis(dx, dy) {
  const ax = Math.abs(Number(dx) || 0);
  const ay = Math.abs(Number(dy) || 0);
  if (Math.max(ax, ay) < SLOP) return null;
  return ax > ay * AXIS_RATIO ? 'x' : 'y';
}

/** 往展開的方向是正：左邊的面板往右、右邊的面板往左 */
const sign = (side) => (side === 'right' ? -1 : 1);

/** 這次左右滑要不要接手：面板開著只接往它那一邊滑（收起），收著只接往對話那一邊滑（展開） */
export function wantsDrag(open, dx, side = 'left') {
  const d = sign(side) * (Number(dx) || 0);
  return open ? d < 0 : d > 0;
}

/**
 * 對話那一側的左右滑叫出哪一個面板：往右滑是左邊的側欄、往左滑是右邊的電腦狀態；那一個已經開著就不接手（回 null，收起要在面板上滑）
 */
export function workspaceTarget(dx, leftOpen, rightOpen) {
  const d = Number(dx) || 0;
  if (d > 0) return leftOpen ? null : 'left';
  if (d < 0) return rightOpen ? null : 'right';
  return null;
}

/** 拖曳中的展開比例（0＝收起、1＝全開）：開著從 1 往收起的方向扣、收著從 0 往展開的方向加，以面板寬度換算 */
export function dragProgress(startOpen, dx, width, side = 'left') {
  const base = startOpen ? 1 : 0;
  if (!(width > 0)) return base;
  const p = base + sign(side) * (Number(dx) || 0) / width;
  return Math.min(1, Math.max(0, p));
}

/** 展開比例對應的位移（translateX 的百分比，面板自己的寬度）：左邊的面板收起在 -100%、右邊的在 100%，全開都是 0 */
export function panelOffset(progress, side = 'left') {
  const p = Math.min(1, Math.max(0, Number(progress) || 0));
  return (side === 'right' ? 1 - p : p - 1) * 100;
}

/**
 * 放開時的速度（px/ms，正＝往右）：samples 是依時間排序的 [{ t, x }]，從最後一筆往前找，最多回推 VELOCITY_WINDOW 毫秒
 * （至少用上倒數第二筆）；endT 是放開的時間，停住超過 VELOCITY_WINDOW 才放開（最後一次移動太久以前）算 0
 */
export function velocity(samples, endT = null) {
  if (!Array.isArray(samples) || samples.length < 2) return 0;
  const last = samples[samples.length - 1];
  if (typeof endT === 'number' && endT - last.t > VELOCITY_WINDOW) return 0;
  let first = samples[samples.length - 2];
  for (let i = samples.length - 2; i >= 0; i--) {
    if (last.t - samples[i].t > VELOCITY_WINDOW) break;
    first = samples[i];
  }
  const dt = last.t - first.t;
  return dt > 0 ? (last.x - first.x) / dt : 0;
}

/** 放開時要開著還是收起：甩得夠快照甩的方向（v 是 velocity 的結果，正＝往右），否則看拉過一半沒有 */
export function releaseOpen(progress, v, side = 'left') {
  const d = sign(side) * (Number(v) || 0);
  if (d >= FLING) return true;
  if (d <= -FLING) return false;
  return progress >= 0.5;
}

/**
 * 觸控起點往上的各層（近的在前）有沒有要自己用這次左右滑的：
 * 打字的地方（輸入框、可編輯）一律讓給它（選字、移游標）；不接受左右平移的（touch-action 沒有 pan-x，例如走勢圖）讓給它；
 * 橫向捲動區（程式碼、表格、分頁列）還能往手指的方向捲時讓它捲——手指往右（dx > 0）是內容往左捲，要 scrollLeft > 0。
 * chain 的每一層：{ edit, noPanX, max（可捲動的寬度，scrollWidth − clientWidth；不能橫向捲是 0）, left（scrollLeft） }
 */
export function yieldsTo(chain, dx) {
  if (!Array.isArray(chain)) return false;
  for (const n of chain) {
    if (!n) continue;
    if (n.edit || n.noPanX) return true;
    const max = Number(n.max) || 0;
    const left = Number(n.left) || 0;
    if (max > 0.5 && (dx > 0 ? left > 0.5 : left < max - 0.5)) return true;
  }
  return false;
}

/** touch-action 的計算值准不准左右平移（auto、manipulation、含 pan-x 的組合才准） */
export function allowsPanX(touchAction) {
  const v = String(touchAction || 'auto').trim();
  if (v === 'auto' || v === 'manipulation') return true;
  return /\bpan-x\b/.test(v);
}
