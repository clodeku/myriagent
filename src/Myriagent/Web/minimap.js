// 對話小地圖（2026-10-24）：捲動軸左邊一條細軌，標出你的訊息、錯誤、等你回應的卡片、改了檔案的工具在對話裡的位置（fxrules.minimapKind），
// 目前看得到的範圍以一個框標出，點標記跳到那一則、點軌道跳到那個比例。
// - 框的位置用 CSS 的捲動時間軸（.scroller 的 scroll-timeline，fx.css），捲動時不跑任何 JS、交給合成器；JS 只在量測時寫框的高度與行程。
// - 量測（讀每個標記項目的位置）最多每 THROTTLE_MS 一次：內容長高時在 ResizeObserver 的回呼裡量（瀏覽器剛排完版，讀幾何不必再排一次），
//   其他變動（卡片回答了、項目被刪）排一個計時器。看不到的對話不量，顯示出來時補量。
// - 畫面外的項目有 content-visibility，高度是估計值，標記的位置是近似的，捲到附近時會在下一次量測校正。
// - 窄窗格（fx.css 的容器查詢）與捲動內容不夠長（fxrules.minimapShown）時不顯示。
import { h, replayClass } from './util.js';
import { minimapKind, minimapMarks, minimapShown } from './fxrules.js';
import { calmMotion } from './theme.js';

const THROTTLE_MS = 800;
/** 跳過去之後那一則閃一下多久（fx.css 的 .it.fx-mmap-hit） */
const HIT_FX_MS = 900;
/** 跳過去時那一則的上緣留多少空間 */
const JUMP_MARGIN = 16;
/** 「目前看得到的範圍」框最矮幾像素（很長的對話也點得到、看得到） */
const VIEW_MIN_PX = 12;
const KIND_TEXT = { user: '你的訊息', err: '錯誤', wait: '等你回應', edit: '改了檔案' };

export class Minimap {
  /** conv：ConvView（用到 scroller、col、order、items、nodes、visible、setStick） */
  constructor(conv) {
    this.conv = conv;
    this.marksEl = h('div', { class: 'mmap-marks' });
    this.viewEl = h('i', { class: 'mmap-view' });
    this.el = h('div', { class: 'mmap', hidden: true, title: '對話小地圖：點標記跳到那一則' }, this.marksEl, this.viewEl);
    this.el.addEventListener('click', (e) => this.onClick(e));
    this.dirty = true;
    this.last = 0;
    this.timer = 0;
    this.sig = '';
  }

  destroy() {
    clearTimeout(this.timer);
    this.timer = 0;
  }

  /** 有變動：標成要重量，最多每 THROTTLE_MS 量一次 */
  request() {
    this.dirty = true;
    if (this.timer || !this.conv.visible) return;
    this.timer = setTimeout(() => { this.timer = 0; if (this.dirty) this.measure(); }, Math.max(0, this.last + THROTTLE_MS - Date.now()));
  }

  /** ResizeObserver 的回呼（剛排完版）：要重量而且離上次夠久就在這裡量，讀幾何不用多排一次版 */
  onLayout() {
    this.dirty = true;
    if (!this.conv.visible) return;
    if (Date.now() - this.last >= THROTTLE_MS) { clearTimeout(this.timer); this.timer = 0; this.measure(); }
    else this.request();
  }

  measure() {
    const conv = this.conv;
    if (!conv.visible) return;
    this.dirty = false;
    this.last = Date.now();
    const sc = conv.scroller;
    const sh = sc.scrollHeight, ch = sc.clientHeight;
    const show = minimapShown(sh, ch);
    this.el.hidden = !show;
    if (!show) return;
    // 軌道對齊捲動區（標題列之下、輸入區之上），貼在捲動軸的左邊
    const st = this.el.style;
    st.top = sc.offsetTop + 'px';
    st.height = ch + 'px';
    st.right = Math.max(0, sc.offsetWidth - sc.clientWidth) + 'px';
    const rail = ch;
    const viewPx = Math.max(VIEW_MIN_PX, Math.round(rail * ch / sh));
    st.setProperty('--mmap-vh', viewPx + 'px');
    st.setProperty('--mmap-travel', Math.max(0, rail - viewPx) + 'px');
    // 標記項目在捲動內容裡的上緣（藏起來的 /clear 之前的項目不算）
    const base = sc.getBoundingClientRect().top - sc.scrollTop;
    const entries = [];
    for (const id of conv.order) {
      const kind = minimapKind(conv.items.get(id));
      if (!kind) continue;
      const n = conv.nodes.get(id);
      if (!n || !n.isConnected || n.classList.contains('pre-clear')) continue;
      entries.push({ id, kind, top: n.getBoundingClientRect().top - base });
    }
    const marks = minimapMarks(entries, sh, rail);
    const sig = rail + '|' + marks.map((m) => m.id + ':' + m.kind + ':' + m.at.toFixed(4) + ':' + m.count).join(',');
    if (sig === this.sig) return;
    this.sig = sig;
    this.marksEl.replaceChildren(...marks.map((m) => h('i', {
      class: 'mmap-mark', dataset: { kind: m.kind, id: m.id },
      style: { top: (m.at * 100).toFixed(3) + '%' },
      title: KIND_TEXT[m.kind] + (m.count > 1 ? '（附近共 ' + m.count + ' 則）' : ''),
    })));
  }

  onClick(e) {
    const conv = this.conv;
    const sc = conv.scroller;
    const mark = e.target.closest('.mmap-mark');
    const node = mark ? conv.nodes.get(mark.dataset.id) : null;
    let top;
    if (node && node.isConnected) top = node.getBoundingClientRect().top - sc.getBoundingClientRect().top + sc.scrollTop - JUMP_MARGIN;
    else {
      const r = this.el.getBoundingClientRect();
      top = (e.clientY - r.top) / Math.max(1, r.height) * sc.scrollHeight - sc.clientHeight / 2;
    }
    conv.setStick(false);
    conv.userScrolled = true;
    sc.scrollTo({ top: Math.max(0, top), behavior: calmMotion() ? 'auto' : 'smooth' });
    if (node) replayClass(node, 'fx-mmap-hit', HIT_FX_MS);
  }
}
