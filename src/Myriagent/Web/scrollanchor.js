// 沒有捲動錨定的瀏覽器（iOS Safari 26 以前）自己做錨定（2026-10-25 使用者回報：手機在對話執行中往上滑、載入歷史時畫面一直閃）：
// 上方的項目變高變矮（延遲載入的圖片載好、捲進畫面才畫的圖表、展開收合）時，在同一個影格補 scrollTop，看著的內容停在原位。
// 瀏覽器自己會錨定的（Chrome、Safari 27 起）第一次回呼時就收掉，不重複補；探針以 overflow-anchor: none 模擬沒有錨定的瀏覽器
import { anchorShift } from './liverules.js';

export class ScrollAnchor {
  /**
   * @param {HTMLElement} scroller 捲動的容器
   * @param {HTMLElement} list 項目的容器（直接的子元素是一則一則的項目）
   * @param {() => boolean} active 現在要不要補（貼底時不補：最底由 ConvView 的 ResizeObserver 維持）
   */
  constructor(scroller, list, active) {
    this.scroller = scroller;
    this.list = list;
    this.active = active;
    this.native = null; // 第一次回呼時才看（元素要在文件裡，getComputedStyle 才有值）
    this.heights = new WeakMap();
    this.ro = new ResizeObserver((entries) => this.onResize(entries));
    this.mo = new MutationObserver((records) => this.onMutate(records));
    this.mo.observe(list, { childList: true });
    for (const el of list.children) this.ro.observe(el);
  }

  /**
   * 呼叫端剛自己補了捲動位置（插入較早的項目、分批補上的項目、顯示清除前的對話）：照現在的排版重記每一則的高度，
   * 這些變動不再補一次；之後才發生的（例如剛插入的那幾則裡的圖片載好）照常補。剛插入、還沒開始觀察的也先記
   */
  resync() {
    if (!this.ro) return;
    for (const el of this.list.children) this.heights.set(el, el.offsetHeight);
  }

  destroy() {
    if (this.ro) this.ro.disconnect();
    if (this.mo) this.mo.disconnect();
    this.ro = null;
    this.mo = null;
  }

  onMutate(records) {
    if (!this.ro) return;
    for (const r of records) {
      // 同一則換了節點（updateItem 回新的節點、replaceWith）：新節點接手舊節點的高度，換掉的那一下照樣補得到
      const replaced = r.removedNodes.length === 1 && r.addedNodes.length === 1 ? this.heights.get(r.removedNodes[0]) : undefined;
      for (const n of r.removedNodes) if (n.nodeType === 1) this.ro.unobserve(n);
      for (const n of r.addedNodes) {
        if (n.nodeType !== 1) continue;
        if (replaced !== undefined) this.heights.set(n, replaced);
        this.ro.observe(n);
      }
    }
  }

  onResize(entries) {
    if (!this.ro) return;
    if (this.native === null) {
      this.native = CSS.supports('overflow-anchor', 'auto') && getComputedStyle(this.scroller).overflowAnchor !== 'none';
      if (this.native) { this.destroy(); return; }
    }
    const changes = [];
    for (const e of entries) {
      const el = e.target;
      // 一律用 offsetHeight（版面的高度，不含 transform；和 resync 記的同一種量法）：回呼時版面已經排好，不會多排一次
      const h = el.offsetHeight;
      const old = this.heights.get(el);
      this.heights.set(el, h);
      // 第一次看到（剛插入：插入的位置由呼叫端補）、已經拿掉的不算
      if (old === undefined || h === old || !el.isConnected) continue;
      changes.push({ el, delta: h - old });
    }
    if (!changes.length || !this.active()) return;
    // 依文件順序：較前面的變動會推動後面的，anchorShift 照順序算出變動之前的位置
    changes.sort((a, b) => (a.el.compareDocumentPosition(b.el) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1));
    const top = this.scroller.getBoundingClientRect().top;
    const shift = anchorShift(changes.map((c) => ({ bottom: c.el.getBoundingClientRect().bottom, delta: c.delta })), top);
    if (shift) this.scroller.scrollTop += shift;
  }
}
