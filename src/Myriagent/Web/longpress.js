// 觸控長按＝右鍵（2026-11-01 使用者要求：圖片與影片上按右鍵或觸控長按就能複製、另存新檔）。
// iOS Safari 長按不送 contextmenu（只有它自己的「儲存影像」浮出選單，已用 -webkit-touch-callout: none 關掉）；Android、Windows 觸控會送，但時間點不一定。
// 所以這裡自己認長按（按著不動 LONG_PRESS_MS），在按著的元素上派送一則 contextmenu，頁面只要處理 contextmenu 一種事件：
// - 原生的比自己的先到：自己的不送；自己送過之後 1 秒內原生的又到：略過（duplicateContextMenu），不會開兩次選單
// - 長按之後放開手指時瀏覽器可能再送一次 click（iOS）：吃掉長按的那個元素上的 click，不會接著打開燈箱或播放影片（選單項目的點擊照常）
// 只認 isTrusted 的原生事件：自己送的與 framedvideo.js 從 iframe 轉出來的 contextmenu 不攔
import { LONG_PRESS_MS, longPressMoved, duplicateContextMenu } from './viewerrules.js';

/**
 * 在 doc（頁面，或 framedvideo.js 的 iframe 文件）上認長按。accept(target) 為 true 的元素才算（觸控與觸控筆；滑鼠照常右鍵）。
 * 回傳 cancel()：要中止目前的長按（例如手勢已經變成別的動作）時呼叫
 */
export function longPressAsContextMenu(doc, accept) {
  const win = doc.defaultView || window;
  let pend = null; // { id, x, y, sx, sy, target, timer }
  let firedAt = -Infinity;
  let swallowClickUntil = 0;
  let swallowTarget = null;
  const swallowOn = (t) => { swallowClickUntil = Infinity; swallowTarget = t; }; // 放開手指之後再算 400 毫秒
  const cancel = () => {
    if (!pend) return;
    clearTimeout(pend.timer);
    pend = null;
  };
  const fire = (p) => {
    pend = null;
    if (!p.target.isConnected) return;
    firedAt = performance.now();
    swallowOn(p.target);
    p.target.dispatchEvent(new win.MouseEvent('contextmenu', {
      bubbles: true, cancelable: true, composed: true, button: 2, buttons: 0,
      clientX: p.x, clientY: p.y, screenX: p.sx, screenY: p.sy,
    }));
  };
  doc.addEventListener('pointerdown', (e) => {
    cancel();
    if (!e.isTrusted || e.pointerType === 'mouse' || !e.isPrimary) return;
    const t = e.target;
    if (!t || !t.closest || !accept(t)) return;
    const p = { id: e.pointerId, x: e.clientX, y: e.clientY, sx: e.screenX, sy: e.screenY, target: t, timer: 0 };
    p.timer = setTimeout(() => fire(p), LONG_PRESS_MS);
    pend = p;
  }, true);
  doc.addEventListener('pointermove', (e) => {
    if (pend && e.pointerId === pend.id && longPressMoved(e.clientX - pend.x, e.clientY - pend.y)) cancel();
  }, { capture: true, passive: true });
  const up = (e) => {
    if (pend && e.pointerId === pend.id) cancel();
    if (swallowClickUntil === Infinity) swallowClickUntil = performance.now() + 400;
  };
  doc.addEventListener('pointerup', up, true);
  doc.addEventListener('pointercancel', up, true);
  // 第二指放下（捏合）、捲動：不是長按
  doc.addEventListener('scroll', cancel, { capture: true, passive: true });
  doc.addEventListener('contextmenu', (e) => {
    if (!e.isTrusted) return;
    if (pend) {
      // 瀏覽器自己的長按先到：照它的開，自己的不送
      const t = pend.target;
      cancel();
      firedAt = performance.now();
      swallowOn(t);
      return;
    }
    if (duplicateContextMenu(firedAt, performance.now())) {
      e.preventDefault();
      e.stopImmediatePropagation();
    }
  }, true);
  doc.addEventListener('click', (e) => {
    if (performance.now() >= swallowClickUntil) return;
    const t = swallowTarget;
    if (!t || !(t === e.target || t.contains(e.target))) return;
    swallowClickUntil = 0;
    swallowTarget = null;
    e.preventDefault();
    e.stopImmediatePropagation();
  }, true);
  return { cancel };
}
