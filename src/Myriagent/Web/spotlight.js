// 滑鼠探照光（2026-10-24）：滑過儀表板的帳戶卡、CLI 卡、電腦卡時，一圈淡淡的帳戶色光暈跟著滑鼠走。
// 光暈是卡片裡的一個裁切層（.fx-spot，第一次滑過那張卡才建立，之後沿用），裡面的圓只以 transform 移動（交給合成器）；
// pointermove 以 requestAnimationFrame 合併成每個影格最多一次。只在有滑鼠（hover: hover、pointer: fine）、
// 沒有要求減少動態、沒有關掉動態效果時才有（calmMotion）；這是純裝飾，不連動任何數據。
import { h } from './util.js';
import { calmMotion } from './theme.js';

const CARDS = '.acard, .ccard, .dcli, .dev';

let inited = false;

export function initSpotlight() {
  if (inited) return;
  inited = true;
  if (!window.matchMedia || !window.matchMedia('(hover: hover) and (pointer: fine)').matches) return;
  let card = null, raf = 0, cx = 0, cy = 0;
  const paint = () => {
    raf = 0;
    if (!card || !card._spot || !card._spot.isConnected) return;
    const r = card.getBoundingClientRect();
    card._spot.firstChild.style.transform = 'translate(' + Math.round(cx - r.left) + 'px, ' + Math.round(cy - r.top) + 'px)';
  };
  const leave = () => {
    if (card) card.classList.remove('fx-spot-on');
    card = null;
  };
  document.addEventListener('pointerover', (e) => {
    const c = e.target instanceof Element ? e.target.closest(CARDS) : null;
    if (c === card) return;
    leave();
    if (!c || calmMotion() || e.pointerType !== 'mouse') return;
    if (!c._spot || !c._spot.isConnected) {
      c._spot = h('span', { class: 'fx-spot', 'aria-hidden': 'true' }, h('i'));
      c.append(c._spot);
    }
    card = c;
    cx = e.clientX;
    cy = e.clientY;
    paint();
    card.classList.add('fx-spot-on');
  }, { passive: true });
  document.addEventListener('pointermove', (e) => {
    if (!card) return;
    cx = e.clientX;
    cy = e.clientY;
    if (!raf) raf = requestAnimationFrame(paint);
  }, { passive: true });
  document.addEventListener('pointerleave', leave);
  window.addEventListener('blur', leave);
}
