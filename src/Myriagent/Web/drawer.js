// 觸控的左右滑叫出與收起兩側面板：左邊的側欄、右邊的電腦狀態。只看有沒有觸控、不看螢幕寬度（手機、平板、觸控筆電上的桌面 app 都一樣），
// 觸控事件本來就只有觸控才送，滑鼠與觸控板照舊。在對話那一側往右滑叫出側欄、往左滑叫出電腦狀態；在側欄上往左滑收起它、在電腦狀態上往右滑收起它；
// 手機版的側欄另有遮罩（點一下或往左滑收起）。開或關仍是 app.js 的狀態（側欄記進版面、電腦狀態照它自己的規則），這裡只管手勢與拖曳中跟著手指的位置。
// - 手機版的側欄（瀏覽器版的窄螢幕，疊在對話上的抽屜）：收起時仍在 DOM、滑到畫面外，開關的滑動是 app.css 的轉場
// - 其他（並排的欄位、手機版的電腦狀態）收起時是 hidden：拖曳中疊在對話上跟著手指（.peek-left／.peek-right），放開後自己滑到開或關的位置
//   （.drawer-settle），落定才改狀態——並排的欄位這時才重排一次，拖曳中對話不跟著每一幀重排
// 規則（方向鎖定、往哪邊滑叫出哪一個、放開時開或關、讓給捲動與打字）在 drawerrules.js
import { h } from './util.js';
import { lockAxis, wantsDrag, workspaceTarget, dragProgress, panelOffset, velocity, releaseOpen, yieldsTo, allowsPanX, SETTLE_MS } from './drawerrules.js';

/**
 * 掛上手勢（桌面與瀏覽器版都呼叫）。遮罩插在側欄之後（手機版的側欄才顯示，app.js 管），回傳 { scrim, holds }。
 * active()：現在能不能滑（收到 init 了、對話檢視、不是彈出視窗）；refresh()：沒改狀態時照狀態重畫（app.js 的 applyChrome）。
 * left（側欄）／right（電腦狀態）：{ el, isOpen(), setOpen(open), slidesInCss()（收起時還在 DOM、開關的滑動由 CSS 做：手機版的側欄）, peek()（拖曳叫出時先顯示） }。
 * holds(el)：這個面板正被叫出來（拖曳中或落定前）——app.js 重畫時別把它藏起來
 */
export function initDrawer({ app, workspace, active, refresh, left, right }) {
  const scrim = h('div', { class: 'sb-scrim', 'aria-hidden': 'true' });
  left.el.after(scrim);
  scrim.addEventListener('click', () => { if (active() && left.slidesInCss() && left.isOpen()) left.setOpen(false); });
  const panels = { left, right };

  let g = null; // 進行中的觸控：{ side, open, el, target, x0, y0, axis, width, p, samples }；對話那一側在鎖定方向之後才知道 side
  let settling = null; // 放開後自己滑到定位：{ side, open, timer }

  const paint = (side, p) => {
    panels[side].el.style.transform = 'translateX(' + panelOffset(p, side).toFixed(2) + '%)';
    if (side === 'left' && left.slidesInCss()) scrim.style.opacity = p.toFixed(3);
  };
  const settled = () => {
    const s = settling;
    settling = null;
    if (!s) return;
    clearTimeout(s.timer);
    const P = panels[s.side];
    P.el.classList.remove('drawer-settle');
    P.el.style.transform = '';
    app.classList.remove('drawer-settling', 'peek-' + s.side);
    // 同一個任務裡拿掉位移並改狀態：收起的欄位直接藏起來、叫出的直接放回欄位，不會先閃回原位
    if (active() && s.open !== P.isOpen()) P.setOpen(s.open);
    else refresh();
  };
  // 拿掉拖曳中的樣式，開或關。手機版的側欄：open 不是現在的狀態就改（同一個任務裡改，CSS 從手指放開的位置接著滑到開或關）；
  // 其他：從放開的位置自己滑到開或關的位置，SETTLE_MS 後才改狀態
  const finish = (open) => {
    const cur = g;
    g = null;
    if (!cur || !cur.axis) return;
    const P = panels[cur.side];
    app.classList.remove('drawer-drag');
    if (P.slidesInCss()) {
      P.el.style.transform = '';
      scrim.style.opacity = '';
      if (active() && open !== P.isOpen()) P.setOpen(open);
      return;
    }
    app.classList.add('drawer-settling');
    P.el.classList.add('drawer-settle');
    P.el.style.transform = 'translateX(' + panelOffset(open ? 1 : 0, cur.side) + '%)';
    settling = { side: cur.side, open, timer: setTimeout(settled, SETTLE_MS + 20) };
  };

  /** 觸控起點往上到 root 的各層：打字的地方、不准左右平移的、橫向捲動區（drawerrules.yieldsTo） */
  const chainOf = (target, root) => {
    const out = [];
    for (let el = target instanceof Element ? target : null; el && el !== root.parentElement; el = el.parentElement) {
      const cs = getComputedStyle(el);
      const ox = cs.overflowX;
      out.push({
        edit: el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName),
        noPanX: !allowsPanX(cs.touchAction),
        max: ox === 'auto' || ox === 'scroll' ? el.scrollWidth - el.clientWidth : 0,
        left: el.scrollLeft,
      });
    }
    return out;
  };

  /** side：這個區域收起哪一個面板（面板本身、遮罩；只在它開著時接手）；null 是對話那一側（往右叫出側欄、往左叫出電腦狀態） */
  const bind = (el, side, passive) => {
    el.addEventListener('touchstart', (e) => {
      if (g && g.axis) finish(g.open); // 上一次沒收到結束（例如被系統手勢搶走）：回到原來的狀態
      g = null;
      if (settling || e.touches.length !== 1 || !active()) return;
      if (side && !panels[side].isOpen()) return;
      const t = e.touches[0];
      g = { side, open: !!side, el, target: e.target, x0: t.clientX, y0: t.clientY, axis: null, width: 0, p: side ? 1 : 0, samples: [] };
    }, { passive: true });
    el.addEventListener('touchmove', (e) => {
      if (!g || g.el !== el) return;
      if (e.touches.length !== 1 || !active()) { finish(g.open); return; }
      const t = e.touches[0];
      if (!g.axis) {
        const dx = t.clientX - g.x0;
        const axis = lockAxis(dx, t.clientY - g.y0);
        if (!axis) return;
        if (axis !== 'x') { g = null; return; }
        if (!g.side) g.side = workspaceTarget(dx, left.isOpen(), right.isOpen());
        if (!g.side || !wantsDrag(g.open, dx, g.side) || yieldsTo(chainOf(g.target, el), dx)) { g = null; return; }
        // 鎖定成左右滑：從這裡開始跟著手指（不把判斷方向那 10px 算進去，面板不會一下跳一段）
        const P = panels[g.side];
        g.axis = 'x';
        g.x0 = t.clientX;
        if (!g.open && !P.slidesInCss()) {
          // 收起的面板是 hidden：先疊在對話上顯示出來（落定前 app.js 重畫也不藏，見 holds）
          app.classList.add('peek-' + g.side);
          P.el.hidden = false;
          if (P.peek) P.peek();
        }
        g.width = P.el.offsetWidth;
        app.classList.add('drawer-drag');
        if (g.open && P.el.contains(document.activeElement)) document.activeElement.blur(); // 收起時鍵盤不留在看不到的搜尋框
      }
      if (!passive && e.cancelable) e.preventDefault(); // 拖面板時裡面的清單不跟著上下捲
      g.p = dragProgress(g.open, t.clientX - g.x0, g.width, g.side);
      g.samples.push({ t: e.timeStamp, x: t.clientX });
      if (g.samples.length > 12) g.samples.shift();
      paint(g.side, g.p);
    }, { passive });
    el.addEventListener('touchend', (e) => {
      if (!g || g.el !== el) return;
      if (!g.axis) { g = null; return; }
      finish(releaseOpen(g.p, velocity(g.samples, e.timeStamp), g.side));
    }, { passive: true });
    el.addEventListener('touchcancel', () => { if (g && g.el === el) finish(g.open); }, { passive: true });
  };

  // 對話那一側的 touchmove 是被動的：不擋對話的捲動（串流中主執行緒忙時也不延遲捲動的開始）；
  // 面板與遮罩（開著時往外滑收起）才攔下捲動，拖面板時裡面的清單不跟著上下動
  bind(workspace, null, true);
  bind(left.el, 'left', false);
  bind(scrim, 'left', false);
  bind(right.el, 'right', false);
  const holds = (el) => {
    const side = g && g.axis ? g.side : settling ? settling.side : null;
    return !!side && panels[side].el === el && !panels[side].slidesInCss();
  };
  return { scrim, holds };
}
