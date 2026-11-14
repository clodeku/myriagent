// 圖片燈箱（滾輪以游標為中心縮放、拖曳平移、雙擊切換符合視窗／100%、左右切換；觸控：兩指捏合縮放、單指平移、雙點切換、
// 沒有放大時單指拖到旁邊放開就關閉）與影片播放器（觸控同樣拖到旁邊關閉）。右上角一律有關閉鈕（窄的畫面工具列收成只有圖示，關閉鈕不會被擠出去）；
// 圖片與影片上按右鍵或觸控長按開媒體選單（mediamenu.js：複製、另存新檔）
import { h, iconButton, clamp, replayClass } from './util.js';
import { icon } from './icons.js';
import { post, isHub } from './store.js';
import { pushLayer, removeLayer } from './ui.js';
import { calmMotion } from './theme.js';
import { zoomRange, tapSlop, pinchStart, pinchMove, snapBackAfterPinch, isDoubleTap, imageInfoLabel,
  canSwipeDismiss, dismissProgress, shouldDismiss, releaseVelocity, flingOffset } from './viewerrules.js';
import { framedVideo } from './framedvideo.js';
import { mediaPathCmd as pathCmd, openMediaMenu, canSaveMedia, saveMedia } from './mediamenu.js';

// ───── 共用 ─────
/** 拖曳關閉時背景跟著變淡、工具列與左右鈕跟著收（progress 0～1；null＝還原） */
function swipeFade(el, fading, progress) {
  if (progress == null) {
    el.style.removeProperty('background-color');
    for (const n of fading) n.style.removeProperty('opacity');
    return;
  }
  el.style.setProperty('background-color', 'rgba(9, 9, 10, ' + (0.965 * (1 - 0.8 * progress)).toFixed(3) + ')');
  for (const n of fading) n.style.setProperty('opacity', (1 - progress).toFixed(3));
}

/** 放開前的軌跡：只留最後幾點（releaseVelocity 只看最後 100 毫秒） */
function pushSample(samples, t, x, y) {
  samples.push({ t, x, y });
  if (samples.length > 12) samples.shift();
}

/** 工具列的文字按鈕：窄的畫面只留圖示（media.css），所以 title、aria-label 一律帶文字 */
function lbBtn(iconName, text, field) {
  const b = h('button', { type: 'button', class: 'lb-btn', title: text, 'aria-label': text }, icon(iconName), h('span', { text }));
  if (field) b.dataset.field = field;
  return b;
}

/** 右上角的關閉鈕：在工具列之外、固定在最右邊（工具列放不下時收的是工具列，不是它） */
function closeButton(onClick) {
  return iconButton('x', '關閉（Esc）', onClick, 'lb-close');
}

/** 檢視器開著時把背後的介面設成 inert：Tab 不會跑到被遮罩蓋住、看不見的按鈕上 */
function setBackdropInert(on) {
  const app = document.getElementById('app');
  if (app) app.inert = on;
}

/**
 * 用雙擊開啟檢視器時，第二下會落在剛出現的檢視器上（mousedown／click 的 detail 是 2，pointer 事件的 detail 一律是 0）。
 * 這串點擊不是在檢視器裡開始的：整串攔下，不當成點背景關閉、不切換縮放、不讓影片暫停或全螢幕、不按到工具列。
 * 鍵盤觸發的 click（detail 0）不受影響。每次開啟時呼叫 reset()。
 */
function guardOpeningClicks(el) {
  let own = false;
  const swallow = (e) => { if (!own && e.detail > 1) { e.preventDefault(); e.stopPropagation(); } };
  el.addEventListener('mousedown', (e) => { if (e.detail <= 1) own = true; swallow(e); }, true);
  for (const type of ['mouseup', 'click', 'dblclick']) el.addEventListener(type, swallow, true);
  return { reset: () => { own = false; }, isOwn: () => own };
}

// ───── 燈箱 ─────
let lb = null;

/** origin 是點的縮圖（有的話打開時圖片從它的位置放大、關閉時縮回去） */
export function openLightbox(list, index, origin = null) {
  if (!list || !list.length) return;
  if (!lb) lb = createLightbox();
  lb.list = list;
  lb.index = clamp(index | 0, 0, list.length - 1);
  lb.origin = origin;
  lb.originIndex = lb.index;
  lb.zoomIn = !!origin;
  lb.attach();
  lb.show();
  lb.el.focus({ preventScroll: true });
}

function createLightbox() {
  const nameEl = h('span', { class: 'lb-name' });
  const countEl = h('span', { class: 'lb-count' });
  // 影像資訊（viewerrules.imageInfoLabel：實際解析度與格式），圖片載入後才有
  const resEl = h('span', { class: 'lb-res' });
  const zoomLabel = h('button', { type: 'button', class: 'lb-zoom', title: '在符合視窗與 100% 之間切換' });
  const img = h('img', { class: 'lb-img', alt: '', draggable: 'false' });
  const stage = h('div', { class: 'lb-stage' }, img);
  const loadingEl = h('div', { class: 'lb-loading', hidden: true }, h('span', { class: 'spin' }));
  const errEl = h('div', { class: 'lb-error', hidden: true }, icon('warning'), h('span', { text: '圖片無法載入' }));
  const prev = h('button', { type: 'button', class: 'lb-nav prev', title: '上一張（←）', 'aria-label': '上一張' }, icon('chevLeft'));
  const next = h('button', { type: 'button', class: 'lb-nav next', title: '下一張（→）', 'aria-label': '下一張' }, icon('chevRight'));
  const openBtn = lbBtn('popout', '用預設程式開啟');
  const saveBtn = lbBtn('download', isHub ? '下載' : '另存新檔', 'mediaSave');
  const revealBtn = lbBtn('folderOpen', '在檔案總管顯示');
  const bar = h('div', { class: 'lb-bar' },
    h('div', { class: 'lb-info' }, nameEl, countEl, resEl),
    h('div', { class: 'lb-tools' },
      iconButton('zoomOut', '縮小（-）', () => S.zoomBy(1 / 1.25), 'lb-zbtn'), zoomLabel, iconButton('zoomIn', '放大（+）', () => S.zoomBy(1.25), 'lb-zbtn'),
      iconButton('fit', '符合視窗（0）', () => S.fit(), 'lb-zbtn'),
      h('span', { class: 'lb-div' }), saveBtn, openBtn, revealBtn),
    closeButton(() => S.close()));
  const el = h('div', { class: 'lb', role: 'dialog', 'aria-modal': 'true', 'aria-label': '圖片檢視', tabIndex: -1 }, bar, stage, loadingEl, errEl, prev, next);
  const fading = [bar, prev, next];
  const guard = guardOpeningClicks(el);
  // 按著的指標（舞台上的，以及觸控落在左右鈕上的；pointerId → 相對舞台的 { x, y }）；單指拖曳、兩指捏合、這一串有沒有用過兩指、
  // 上一下點擊（觸控雙點用）、最後按下的指標種類
  const pts = new Map();
  let drag = null, pinch = null, multi = false, lastTap = null, lastType = 'mouse';

  const S = {
    el, list: [], index: 0, scale: 1, fitScale: 1, tx: 0, ty: 0, nw: 0, nh: 0, prevFocus: null, open: false, closeTimer: 0,
    origin: null, originIndex: -1, zoomIn: false,
    layer: { dismissable: true, close: () => S.close() },
    /** 掛上燈箱；還在關閉的淡出動畫裡（元素尚未移除）就取消移除，重新放回圖層堆疊 */
    attach() {
      if (S.open) return;
      S.open = true;
      clearTimeout(S.closeTimer);
      if (!el.contains(document.activeElement)) S.prevFocus = document.activeElement;
      if (!el.isConnected) document.body.append(el);
      el.classList.remove('leaving', 'zoom-back', 'flinging', 'swipe-back');
      swipeFade(el, fading, null);
      for (const a of img.getAnimations()) a.cancel();
      pushLayer(S.layer);
      setBackdropInert(true);
      guard.reset();
      pts.clear(); drag = pinch = lastTap = null; multi = false;
      stage.classList.remove('dragging');
    },
    show() {
      const it = S.list[S.index];
      nameEl.textContent = it.name || '';
      nameEl.title = it.path || it.name || '';
      countEl.textContent = S.list.length > 1 ? '第 ' + (S.index + 1) + '／' + S.list.length + ' 張' : '';
      prev.hidden = next.hidden = S.list.length < 2;
      openBtn.disabled = revealBtn.disabled = !it.path;
      saveBtn.hidden = !canSaveMedia(it);
      errEl.hidden = true;
      resEl.textContent = '';
      loadingEl.hidden = false;
      img.style.visibility = 'hidden';
      img.onload = () => {
        loadingEl.hidden = true;
        S.nw = img.naturalWidth; S.nh = img.naturalHeight;
        resEl.textContent = imageInfoLabel(S.nw, S.nh, it.name || it.url);
        replayClass(stage, 'fx-lb-lock', 640); // 舞台的角框從外面收進來鎖定一次（fx.css）
        S.fit();
        img.style.visibility = '';
        // 剛打開：從縮圖的位置放大到符合視窗（只動 transform；減少動態、縮圖不在畫面上時不做）
        const r = S.zoomIn ? S.originRect() : null;
        S.zoomIn = false;
        if (r) img.animate([{ transform: S.originTransform(r), opacity: 0.4 }, { transform: img.style.transform, opacity: 1 }], { duration: 240, easing: 'cubic-bezier(.2, .8, .2, 1)' });
      };
      img.onerror = () => { loadingEl.hidden = true; errEl.hidden = false; };
      img.src = it.url;
      img.alt = it.name || '';
      // 預先載入前後張
      for (const d of [1, -1]) {
        const n = S.list[(S.index + d + S.list.length) % S.list.length];
        if (n && n !== it) { const p = new Image(); p.src = n.url; }
      }
    },
    go(d) {
      if (S.list.length < 2) return;
      S.index = (S.index + d + S.list.length) % S.list.length;
      S.zoomIn = false;
      S.show();
    },
    stageSize() { const r = stage.getBoundingClientRect(); return { w: r.width, h: r.height, x: r.left, y: r.top }; },
    /** 縮圖在畫面上的位置：換過張、縮圖不在畫面上、減少動態時回 null（不做放大與縮回） */
    originRect() {
      const o = S.origin;
      if (!o || !o.isConnected || S.index !== S.originIndex || !S.nw || calmMotion()) return null;
      const r = (o.tagName === 'IMG' ? o : o.querySelector('img') || o).getBoundingClientRect();
      if (r.width < 4 || r.height < 4 || r.bottom < 0 || r.right < 0 || r.top > innerHeight || r.left > innerWidth) return null;
      return r;
    },
    /** 圖片疊在縮圖上的 transform（以寬度對齊；圖片的 transform-origin 是舞台的左上角） */
    originTransform(r) {
      const st = S.stageSize();
      return 'translate(' + (r.left - st.x).toFixed(1) + 'px,' + (r.top - st.y).toFixed(1) + 'px) scale(' + (r.width / S.nw).toFixed(4) + ')';
    },
    fit() {
      const st = S.stageSize();
      if (!S.nw || !S.nh) return;
      S.fitScale = Math.min(1, (st.w - 48) / S.nw, (st.h - 48) / S.nh);
      if (!(S.fitScale > 0)) S.fitScale = 1;
      S.scale = S.fitScale;
      S.tx = (st.w - S.nw * S.scale) / 2;
      S.ty = (st.h - S.nh * S.scale) / 2;
      S.apply();
    },
    zoomAt(newScale, px, py) {
      const range = zoomRange(S.fitScale);
      newScale = clamp(newScale, range.min, range.max);
      const k = newScale / S.scale;
      S.tx = px - (px - S.tx) * k;
      S.ty = py - (py - S.ty) * k;
      S.scale = newScale;
      S.constrain();
      S.apply();
    },
    zoomBy(f) { const st = S.stageSize(); S.zoomAt(S.scale * f, st.w / 2, st.h / 2); },
    /** 圖片比視窗小時置中；比視窗大時不讓邊緣離開視窗 */
    constrain() {
      const st = S.stageSize();
      const w = S.nw * S.scale, hh = S.nh * S.scale;
      S.tx = w <= st.w ? (st.w - w) / 2 : clamp(S.tx, st.w - w, 0);
      S.ty = hh <= st.h ? (st.h - hh) / 2 : clamp(S.ty, st.h - hh, 0);
    },
    apply() {
      img.style.transform = 'translate(' + S.tx.toFixed(1) + 'px,' + S.ty.toFixed(1) + 'px) scale(' + S.scale.toFixed(4) + ')';
      zoomLabel.textContent = Math.round(S.scale * 100) + '%';
      stage.classList.toggle('zoomed', S.scale > S.fitScale + 0.001);
    },
    toggleZoom(px, py) {
      if (Math.abs(S.scale - S.fitScale) < 0.001) S.zoomAt(S.fitScale >= 1 ? 2 : 1, px, py);
      else S.fit();
    },
    /** 拖曳關閉沒拖夠遠（或第二指放下改成捏合）：圖片回到拖曳開始的位置、背景與工具列還原 */
    swipeBack(d, animate = true) {
      const from = img.style.transform;
      S.tx = d.tx; S.ty = d.ty;
      S.apply();
      swipeFade(el, fading, null);
      if (!animate || calmMotion()) return;
      img.animate([{ transform: from }, { transform: img.style.transform }], { duration: 180, easing: 'cubic-bezier(.2, .8, .2, 1)' });
      replayClass(el, 'swipe-back', 200);
    },
    /** opts.flingTo：拖曳關閉時圖片沿著拖的方向飛出去的位置（{ tx, ty }） */
    close(opts = {}) {
      if (!S.open) return;
      S.open = false;
      removeLayer(S.layer);
      setBackdropInert(false); // 先解除 inert，焦點才放得回縮圖
      const fling = opts.flingTo && !calmMotion() ? opts.flingTo : null;
      // 沒有放大過、還是打開的那一張、縮圖還在畫面上：圖片縮回縮圖的位置、底色淡掉（media.css 的 .lb.zoom-back）；拖曳關閉的往外飛；否則整個淡出
      const r = !opts.flingTo && Math.abs(S.scale - S.fitScale) < 1e-3 && img.style.visibility !== 'hidden' ? S.originRect() : null;
      el.classList.add('leaving');
      if (fling) {
        el.classList.add('flinging');
        img.animate([{ transform: img.style.transform }, { transform: 'translate(' + fling.tx.toFixed(1) + 'px,' + fling.ty.toFixed(1) + 'px) scale(' + S.scale.toFixed(4) + ')' }],
          { duration: 200, easing: 'cubic-bezier(.4, 0, 1, 1)', fill: 'forwards' });
      } else if (r) {
        el.classList.add('zoom-back');
        img.animate([{ transform: img.style.transform }, { transform: S.originTransform(r), opacity: 0.5 }], { duration: 200, easing: 'cubic-bezier(.4, 0, .8, .6)', fill: 'forwards' });
      }
      S.closeTimer = setTimeout(() => {
        el.remove();
        el.classList.remove('zoom-back', 'flinging');
        swipeFade(el, fading, null);
        for (const a of img.getAnimations()) a.cancel();
        img.removeAttribute('src');
      }, r || fling ? 210 : 140);
      if (S.prevFocus && S.prevFocus.isConnected) { try { S.prevFocus.focus({ preventScroll: true }); } catch { /* 忽略 */ } }
    },
  };

  stage.addEventListener('wheel', (e) => {
    e.preventDefault();
    const st = S.stageSize();
    const f = Math.exp(-e.deltaY * (e.deltaMode === 1 ? 0.05 : 0.0016));
    S.zoomAt(S.scale * f, e.clientX - st.x, e.clientY - st.y);
  }, { passive: false });
  // 觸控的雙點自己認（pointerup，見 endPointer）：瀏覽器另外送的 dblclick 不理，否則一次雙點切換兩次、等於沒動
  stage.addEventListener('dblclick', (e) => {
    if (lastType !== 'mouse') return;
    const st = S.stageSize();
    S.toggleZoom(e.clientX - st.x, e.clientY - st.y);
  });
  // iOS Safari 的捏合另有 gesture 事件：擋下，整頁才不會跟著放大（其他瀏覽器沒有這兩個事件）
  for (const type of ['gesturestart', 'gesturechange']) el.addEventListener(type, (e) => e.preventDefault());
  const local = (e) => { const st = S.stageSize(); return { x: e.clientX - st.x, y: e.clientY - st.y }; };
  /** 從某一指（相對舞台的位置）開始單指拖曳；moved 為 true 時放開不算點一下 */
  const startDrag = (p, moved) => { drag = { x: p.x, y: p.y, tx: S.tx, ty: S.ty, moved }; };
  /** 以目前按著的前兩指開始（或重新開始）捏合 */
  const startPinch = () => { const [a, b] = pts.values(); pinch = pinchStart(S, a, b); };
  /**
   * 指標事件掛在燈箱上：舞台上按下的都算；觸控與觸控筆另含左右鈕（手機上捏合時常有一指落在畫面兩側的鈕上）。
   * 從鈕開始的單指不拖曳、不算點擊（照常是按鈕），第二指放下時才一起捏合；觸控本來就隱含擷取，事件照樣冒泡到這裡
   */
  const onNav = (e) => e.pointerType !== 'mouse' && (prev.contains(e.target) || next.contains(e.target));
  el.addEventListener('pointerdown', (e) => {
    if (e.button !== 0) return;
    const onStage = stage.contains(e.target);
    if (!onStage && !onNav(e)) return;
    lastType = e.pointerType;
    if (onStage) { try { stage.setPointerCapture(e.pointerId); } catch { /* 忽略 */ } }
    pts.set(e.pointerId, local(e));
    stage.classList.add('dragging');
    if (pts.size === 1) {
      multi = false;
      drag = null;
      if (onStage) {
        startDrag(pts.get(e.pointerId), false);
        // 觸控、沒有放大：單指拖曳是拖曳關閉（拖到旁邊放開就關）
        if (e.pointerType !== 'mouse' && canSwipeDismiss(S.scale, S.fitScale)) {
          drag.dismiss = true;
          drag.samples = [];
          pushSample(drag.samples, e.timeStamp, drag.x, drag.y);
        }
      }
    } else if (pts.size === 2) {
      // 第二指放下：從拖曳改成捏合，這一串放開時不算點一下（拖曳關閉拖到一半的先放回原位）
      multi = true;
      if (drag && drag.dismiss && drag.moved) S.swipeBack(drag, false);
      drag = null;
      startPinch();
    }
  });
  el.addEventListener('pointermove', (e) => {
    if (!pts.has(e.pointerId)) return;
    const p = local(e);
    pts.set(e.pointerId, p);
    if (pts.size >= 2) {
      if (!pinch) startPinch(); // 兩指放在同一點時還算不出來：分開之後才開始
      if (!pinch) return;
      const [a, b] = pts.values();
      const v = pinchMove(pinch, a, b, zoomRange(S.fitScale));
      S.scale = v.scale; S.tx = v.tx; S.ty = v.ty;
      S.constrain();
      S.apply();
      return;
    }
    if (!drag) return;
    const dx = p.x - drag.x, dy = p.y - drag.y;
    if (Math.abs(dx) + Math.abs(dy) > tapSlop(e.pointerType)) drag.moved = true;
    if (drag.dismiss) {
      // 拖曳關閉：圖片跟著手指走（不受 constrain 限制），背景跟著變淡；還在點一下的範圍內不動（點背景關閉、雙點、長按照舊）
      pushSample(drag.samples, e.timeStamp, p.x, p.y);
      if (!drag.moved) return;
      const st = S.stageSize();
      S.tx = drag.tx + dx; S.ty = drag.ty + dy;
      S.apply();
      swipeFade(el, fading, dismissProgress(dx, dy, st.w, st.h));
      return;
    }
    S.tx = drag.tx + dx; S.ty = drag.ty + dy;
    S.constrain();
    S.apply();
  });
  const endPointer = (e) => {
    if (!pts.delete(e.pointerId)) return;
    try { stage.releasePointerCapture(e.pointerId); } catch { /* 忽略 */ }
    if (multi && pts.size < 2) {
      // 剩一指或全部放開：捏得比符合視窗小就彈回，剩下那一指接著拖
      if (pinch) { pinch = null; if (snapBackAfterPinch(S.scale, S.fitScale)) S.fit(); }
      if (pts.size === 1) startDrag(pts.values().next().value, true);
    } else if (pts.size >= 2) {
      startPinch(); // 還有兩指以上：換成剩下的兩指繼續捏
      return;
    }
    if (pts.size > 0) return;
    stage.classList.remove('dragging');
    if (drag && drag.dismiss && drag.moved) {
      // 拖曳關閉放開：拖得夠遠或往外甩就關（圖片沿著拖的方向飛出去），否則放回原位
      const d = drag;
      drag = null;
      lastTap = null;
      const st = S.stageSize();
      const dx = S.tx - d.tx, dy = S.ty - d.ty;
      const v = releaseVelocity(d.samples);
      if (e.type === 'pointerup' && shouldDismiss(dx, dy, v.vx, v.vy, st.w, st.h)) {
        const f = flingOffset(dx, dy, st.w, st.h);
        S.close({ flingTo: { tx: d.tx + f.x, ty: d.ty + f.y } });
      } else S.swipeBack(d);
      return;
    }
    const wasClick = !!drag && !drag.moved && !multi && e.type === 'pointerup';
    drag = null;
    if (!wasClick) { lastTap = null; return; }
    // 觸控與觸控筆的雙點：在點的位置切換符合視窗／100%（滑鼠用 dblclick）
    if (e.pointerType !== 'mouse') {
      const p = local(e);
      const tap = { t: e.timeStamp, x: p.x, y: p.y };
      if (isDoubleTap(lastTap, tap)) { lastTap = null; S.toggleZoom(p.x, p.y); return; }
      lastTap = tap;
    }
    // 點背景（不是圖片）且沒拖曳＝關閉；雙擊開啟燈箱時的第二下不算（滑鼠才有連點次數，觸控照舊）
    if (e.target === stage && (e.pointerType !== 'mouse' || guard.isOwn())) {
      const r = img.getBoundingClientRect();
      if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) S.close();
    }
  };
  el.addEventListener('pointerup', endPointer);
  el.addEventListener('pointercancel', endPointer);
  prev.addEventListener('click', () => S.go(-1));
  next.addEventListener('click', () => S.go(1));
  zoomLabel.addEventListener('click', () => { const st = S.stageSize(); S.toggleZoom(st.w / 2, st.h / 2); });
  openBtn.addEventListener('click', () => { const it = S.list[S.index]; if (it.path) post(pathCmd('openPath', it.path, it.conversationId)); });
  saveBtn.addEventListener('click', () => saveMedia(S.list[S.index]));
  // 圖片上按右鍵或觸控長按（longpress.js 轉成 contextmenu）：媒體選單。長按時這一串手勢不再拖曳、放開也不算點一下
  el.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    if (e.target !== img || img.style.visibility === 'hidden' || !errEl.hidden) return;
    if (drag && drag.dismiss && drag.moved) S.swipeBack(drag, false);
    drag = null;
    lastTap = null;
    const it = S.list[S.index];
    openMediaMenu(Object.assign({ kind: 'image' }, it), { x: e.clientX, y: e.clientY });
  });
  revealBtn.addEventListener('click', () => { const it = S.list[S.index]; if (it.path) post(pathCmd('revealPath', it.path, it.conversationId)); });
  el.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowLeft') { e.preventDefault(); S.go(-1); }
    else if (e.key === 'ArrowRight') { e.preventDefault(); S.go(1); }
    else if (e.key === '+' || e.key === '=') { e.preventDefault(); S.zoomBy(1.25); }
    else if (e.key === '-' || e.key === '_') { e.preventDefault(); S.zoomBy(1 / 1.25); }
    else if (e.key === '0') { e.preventDefault(); S.fit(); }
    else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); S.close(); }
  });
  window.addEventListener('resize', () => { if (el.isConnected && S.nw) S.fit(); });
  return S;
}

// ───── 影片播放器 ─────
let vp = null;

export function openVideo(m) {
  if (!m || !m.url) return;
  if (vp) vp.close();
  // 〔遠端〕m.errorText：瀏覽器版那台的政策是 Off 時「只能看縮圖」的說明（WP4.6 步 11）
  const errEl = h('div', { class: 'vp-error', hidden: true, dataset: { field: 'videoError' } }, icon('warning'), h('span', { text: m.errorText || '影片無法播放，可以改用預設程式開啟' }));
  // 有控制列的影片放在 iframe 裡（framedvideo.js）；雙擊開啟時的第二下落在 iframe 裡，由它自己擋
  const fv = framedVideo({ src: m.url, cls: 'vp-video', title: '影片：' + (m.name || ''), autoplay: true, time: m.time, outline: false, guardOpen: true,
    onError: () => { errEl.hidden = false; }, onRecovered: () => { errEl.hidden = true; }, bindDoc: (doc, v) => bindFrameSwipe(doc, v) });
  const pauseVideo = () => { const v = fv.video(); if (v) v.pause(); };
  const media = Object.assign({ kind: 'video' }, m);
  const openBtn = lbBtn('popout', '用預設程式開啟');
  const saveBtn = lbBtn('download', isHub ? '下載' : '另存新檔', 'mediaSave');
  const revealBtn = lbBtn('folderOpen', '在檔案總管顯示');
  openBtn.disabled = revealBtn.disabled = !m.path;
  saveBtn.hidden = !canSaveMedia(media);
  openBtn.addEventListener('click', () => { pauseVideo(); post(pathCmd('openPath', m.path, m.conversationId)); });
  saveBtn.addEventListener('click', () => saveMedia(media));
  revealBtn.addEventListener('click', () => post(pathCmd('revealPath', m.path, m.conversationId)));
  const stage = h('div', { class: 'vp-stage' }, fv.el, errEl);
  const bar = h('div', { class: 'lb-bar' }, h('div', { class: 'lb-info' }, icon('video', 'vp-ic'), h('span', { class: 'lb-name', text: m.name || '', title: m.path || '' })),
    h('div', { class: 'lb-tools' }, saveBtn, openBtn, revealBtn), closeButton(() => close()));
  const el = h('div', { class: 'vp', role: 'dialog', 'aria-modal': 'true', 'aria-label': '影片播放', tabIndex: -1 }, bar, stage);
  guardOpeningClicks(el);
  const prevFocus = document.activeElement;
  const layer = { dismissable: true, close: () => close() };
  let closed = false;

  // 觸控拖曳關閉：手指在影片上（控制列以外）或影片外的背景上拖到旁邊放開就關閉。
  // 座標一律用 screenX／screenY：影片的 iframe 跟著手指移動，iframe 裡的 clientX 不會變
  let sw = null; // { id, x, y, moved, samples }
  const swStart = (e) => {
    sw = e.pointerType === 'mouse' || !e.isPrimary ? null : { id: e.pointerId, x: e.screenX, y: e.screenY, moved: false, samples: [] };
    if (sw) pushSample(sw.samples, e.timeStamp, e.screenX, e.screenY);
  };
  const swMove = (e) => {
    if (!sw || e.pointerId !== sw.id) return;
    const dx = e.screenX - sw.x, dy = e.screenY - sw.y;
    pushSample(sw.samples, e.timeStamp, e.screenX, e.screenY);
    if (!sw.moved && Math.abs(dx) + Math.abs(dy) <= tapSlop('touch')) return;
    sw.moved = true;
    const r = stage.getBoundingClientRect();
    fv.el.style.setProperty('transform', 'translate(' + dx.toFixed(1) + 'px,' + dy.toFixed(1) + 'px)');
    swipeFade(el, [bar], dismissProgress(dx, dy, r.width, r.height));
  };
  const swBack = (animate) => {
    const from = fv.el.style.transform;
    fv.el.style.removeProperty('transform');
    swipeFade(el, [bar], null);
    if (animate && from && !calmMotion()) {
      fv.el.animate([{ transform: from }, { transform: 'none' }], { duration: 180, easing: 'cubic-bezier(.2, .8, .2, 1)' });
      replayClass(el, 'swipe-back', 200);
    }
  };
  const swEnd = (e) => {
    if (!sw || e.pointerId !== sw.id) return;
    const s = sw;
    sw = null;
    if (!s.moved) return;
    const dx = e.screenX - s.x, dy = e.screenY - s.y;
    const r = stage.getBoundingClientRect();
    const v = releaseVelocity(s.samples);
    if (e.type === 'pointerup' && shouldDismiss(dx, dy, v.vx, v.vy, r.width, r.height)) close({ fling: flingOffset(dx, dy, r.width, r.height) });
    else swBack(true);
  };
  const swCancel = () => { if (sw && sw.moved) swBack(false); sw = null; };
  /** iframe 裡的影片：控制列（下緣 64px）與全螢幕時不拖，免得搶了進度條 */
  function bindFrameSwipe(doc, v) {
    v.style.setProperty('touch-action', 'none');
    doc.addEventListener('pointerdown', (e) => {
      sw = null;
      if (e.target !== v || doc.fullscreenElement || v.webkitDisplayingFullscreen) return;
      if (e.clientY > doc.documentElement.clientHeight - 64) return;
      swStart(e);
    });
    doc.addEventListener('pointermove', swMove);
    doc.addEventListener('pointerup', swEnd);
    doc.addEventListener('pointercancel', swEnd);
  }
  stage.addEventListener('pointerdown', (e) => {
    if (e.target !== stage) return;
    swStart(e);
    if (sw) { try { stage.setPointerCapture(e.pointerId); } catch { /* 忽略 */ } }
  });
  stage.addEventListener('pointermove', swMove);
  stage.addEventListener('pointerup', swEnd);
  stage.addEventListener('pointercancel', swEnd);
  // 影片上按右鍵或觸控長按（iframe 裡的轉成 iframe 元素上的 contextmenu，framedvideo.js）：媒體選單
  el.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    if (e.target !== fv.el) return;
    swCancel();
    openMediaMenu(media, { x: e.clientX, y: e.clientY });
  });

  /** opts.fling：拖曳關閉時影片沿著拖的方向飛出去的總位移（{ x, y }） */
  function close(opts = {}) {
    if (closed) return;
    closed = true;
    removeLayer(layer);
    setBackdropInert(false); // 先解除 inert，焦點才放得回縮圖
    const video = fv.video();
    const fling = opts.fling && !calmMotion() ? opts.fling : null;
    const release = () => {
      if (!video) return;
      video.removeAttribute('src');
      try { video.load(); } catch { /* 忽略 */ }
    };
    if (video) { try { video.pause(); } catch { /* 忽略 */ } }
    // 飛出去時影片最後一格留在畫面上，移除時才放掉
    if (!fling) release();
    el.classList.add('leaving');
    if (fling) {
      el.classList.add('flinging');
      fv.el.animate([{ transform: fv.el.style.transform || 'none' }, { transform: 'translate(' + fling.x.toFixed(1) + 'px,' + fling.y.toFixed(1) + 'px)' }],
        { duration: 200, easing: 'cubic-bezier(.4, 0, 1, 1)', fill: 'forwards' });
    }
    setTimeout(() => {
      if (fling) release();
      el.remove();
    }, fling ? 210 : 140);
    vp = null;
    if (prevFocus && prevFocus.isConnected) { try { prevFocus.focus({ preventScroll: true }); } catch { /* 忽略 */ } }
  }
  el.addEventListener('keydown', (e) => { if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); } });
  // 點影片外的背景＝關閉。用 mousedown（有連點次數）：雙擊開啟時的第二下已被 guardOpeningClicks 攔下
  stage.addEventListener('mousedown', (e) => { if (e.target === stage) close(); });
  document.body.append(el);
  pushLayer(layer);
  setBackdropInert(true);
  el.focus({ preventScroll: true });
  vp = { el, close };
  return vp;
}
