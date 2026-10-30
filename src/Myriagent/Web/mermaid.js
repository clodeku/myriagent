// Mermaid 圖表離線繪製：將 enhanceMarkdown 包好的 mermaid 程式碼區塊轉為主題自適應的 SVG 圖表，
// 並在標題列提供「原始碼／圖表」切換鈕；語法未完成或不支援時保留原本的程式碼框。
import { h, replayClass } from './util.js';
import { icon } from './icons.js';
import { parseMermaid } from './mermaidrules.js';
import { calmMotion } from './theme.js';

const SVG_NS = 'http://www.w3.org/2000/svg';
const CACHE_MAX = 120;
const SETTLE_MS = 250; // 串流中最後一段靜置這麼久沒被換掉才排版 SVG
const DROP_MS = 5000;
/** 第一次捲進畫面時依序畫出（fx.css 的 .code-diagram.fx-mm-draw）：圖元太多的圖直接出現；畫完多久拿掉 class */
const DRAW_MAX_SHAPES = 160;
const DRAW_FX_MS = 1600;
const DRAW_STAGGER_MAX = 14;
const layoutCache = new Map();
const pending = new Map(); // block -> { code, at }
let settleTimer = 0;

function cachedParse(source) {
  let res = layoutCache.get(source);
  if (res) {
    layoutCache.delete(source);
    layoutCache.set(source, res);
    return res;
  }
  res = parseMermaid(source);
  layoutCache.set(source, res);
  if (layoutCache.size > CACHE_MAX) layoutCache.delete(layoutCache.keys().next().value);
  return res;
}

function svgNode(tag, attrs) {
  const el = document.createElementNS(SVG_NS, tag);
  for (const k in attrs) {
    const v = attrs[k];
    if (v !== null && v !== undefined && v !== '') el.setAttribute(k, String(v));
  }
  return el;
}

/** 依 parseMermaid 產出的幾何圖元建立純 SVG 節點（零樣式屬性，色彩全走 chat.css 主題變數） */
export function buildMermaidSvg(plan) {
  const svg = svgNode('svg', {
    class: 'mm-svg',
    viewBox: plan.viewBox,
    width: String(plan.width),
    height: String(plan.height),
  });
  const frag = document.createDocumentFragment();
  let nodeSeq = 0, edgeSeq = 0;
  const nodeOrder = new Map(), edgeOrder = new Map();
  for (const s of plan.shapes) {
    const before = frag.childNodes.length;
    if (s.tag === 'rect') {
      frag.append(svgNode('rect', { class: s.cls, x: s.x, y: s.y, width: s.width, height: s.height, rx: s.rx }));
    } else if (s.tag === 'circle') {
      frag.append(svgNode('circle', { class: s.cls, cx: s.cx, cy: s.cy, r: s.r }));
    } else if (s.tag === 'ellipse') {
      frag.append(svgNode('ellipse', { class: s.cls, cx: s.cx, cy: s.cy, rx: s.rx, ry: s.ry }));
    } else if (s.tag === 'polygon') {
      frag.append(svgNode('polygon', { class: s.cls, points: s.points }));
    } else if (s.tag === 'path') {
      frag.append(svgNode('path', { class: s.cls, d: s.d }));
    } else if (s.tag === 'line') {
      frag.append(svgNode('line', { class: s.cls, x1: s.x1, y1: s.y1, x2: s.x2, y2: s.y2 }));
    } else if (s.tag === 'text') {
      const textEl = svgNode('text', {
        class: s.cls,
        x: s.x,
        y: s.y,
        'text-anchor': s.anchor || 'middle',
        'dominant-baseline': 'central',
      });
      for (const ln of s.lines) {
        const tspan = svgNode('tspan', { x: s.x, dy: ln.dy });
        tspan.textContent = ln.text;
        textEl.append(tspan);
      }
      frag.append(textEl);
    }
    // 節點與邊的關係（mermaidrules 的 nid、from／to）：滑過節點時亮起相連的線；依序畫出時同一個節點、同一條邊的圖元一起出現
    if (frag.childNodes.length > before) {
      const el = frag.lastChild;
      if (s.nid !== undefined) {
        const k = String(s.nid);
        if (!nodeOrder.has(k)) nodeOrder.set(k, nodeSeq++);
        el.setAttribute('data-n', k);
        el.style.setProperty('--fx-i', String(Math.min(DRAW_STAGGER_MAX, nodeOrder.get(k))));
      } else if (s.from !== undefined) {
        const k = String(s.from) + ' -> ' + String(s.to);
        if (!edgeOrder.has(k)) edgeOrder.set(k, edgeSeq++);
        el.setAttribute('data-from', String(s.from));
        el.setAttribute('data-to', String(s.to));
        el.style.setProperty('--fx-i', String(Math.min(DRAW_STAGGER_MAX, edgeOrder.get(k))));
        // 實線的邊依序「畫」出來（stroke-dashoffset 以 pathLength 1 為單位，只在畫出的那一下）；虛線有自己的 dasharray，只淡入
        if ((s.tag === 'path' || s.tag === 'line') && /(^| )mm-edge( |$)/.test(s.cls || '') && !/mm-dotted/.test(s.cls || '')) el.setAttribute('pathLength', '1');
      }
    }
  }
  svg.append(frag);
  return svg;
}

/** 滑過節點：它、相連的邊與另一端的節點亮起來，其他淡下去（.mm-svg.mm-focus）；離開圖表就恢復。只比對 data 屬性的字串，不組選擇器 */
function bindHighlight(svg) {
  const clear = () => {
    svg._hl = null;
    svg.classList.remove('mm-focus');
    for (const el of svg.querySelectorAll('.mm-hl')) el.classList.remove('mm-hl');
  };
  svg.addEventListener('pointerover', (e) => {
    const t = e.target instanceof Element ? e.target.closest('[data-n]') : null;
    const id = t ? t.getAttribute('data-n') : null;
    if (id === svg._hl) return;
    clear();
    if (id === null) return;
    svg._hl = id;
    svg.classList.add('mm-focus');
    const peers = new Set([id]);
    for (const el of svg.querySelectorAll('[data-from]')) {
      const f = el.getAttribute('data-from'), to = el.getAttribute('data-to');
      if (f !== id && to !== id) continue;
      el.classList.add('mm-hl');
      peers.add(f);
      peers.add(to);
    }
    for (const el of svg.querySelectorAll('[data-n]')) if (peers.has(el.getAttribute('data-n'))) el.classList.add('mm-hl');
  });
  svg.addEventListener('pointerleave', clear);
}

/** 第一次捲進畫面（看得到兩成）時依序畫出一次；之後不再播 */
let drawObs = null;
function drawWhenSeen(wrap, shapes) {
  if (shapes > DRAW_MAX_SHAPES || typeof IntersectionObserver !== 'function') return;
  if (!drawObs) {
    drawObs = new IntersectionObserver((ents) => {
      for (const en of ents) {
        if (!en.isIntersecting) continue;
        drawObs.unobserve(en.target);
        if (!calmMotion()) replayClass(en.target, 'fx-mm-draw', DRAW_FX_MS);
      }
    }, { threshold: 0.2 });
  }
  drawObs.observe(wrap);
}

/**
 * 強化 language-mermaid 程式碼框：解析成功時掛上 SVG 圖表與「原始碼／圖表」切換鈕；
 * 解析失敗（例如串流還沒寫完或未知圖表）時維持原樣不動。
 */
export function enhanceMermaidBlock(block, code) {
  if (!block || !code || block.classList.contains('code-mermaid')) return false;
  pending.delete(block);
  const source = code.textContent || '';
  let plan;
  try {
    plan = cachedParse(source);
  } catch {
    return false;
  }
  if (!plan || !plan.ok) return false;

  const svg = buildMermaidSvg(plan);
  bindHighlight(svg);
  const diagramWrap = h('div', { class: 'code-diagram' }, svg);
  drawWhenSeen(diagramWrap, plan.shapes.length);
  block.classList.add('code-mermaid', 'is-diagram');
  block.append(diagramWrap);

  const head = block.querySelector('.code-head');
  if (head) {
    const label = h('span', { text: '原始碼' });
    const toggle = h('button', { type: 'button', class: 'code-copy mm-toggle', title: '在圖表與原始碼之間切換' },
      icon('fileCode'), label);
    toggle.addEventListener('click', () => {
      const showDiagram = !block.classList.contains('is-diagram');
      block.classList.toggle('is-diagram', showDiagram);
      label.textContent = showDiagram ? '原始碼' : '圖表';
    });
    const copyBtn = head.querySelector('.code-copy');
    if (copyBtn) head.insertBefore(toggle, copyBtn);
    else head.append(toggle);
  }
  return true;
}

/** 串流中還在長的最後一段 mermaid 框：靜置 250ms 還在畫面上才排版，避免每 50ms 重跑拓撲佈局 */
export function queueMermaid(block, code) {
  if (!block || !code || block.classList.contains('code-mermaid')) return;
  pending.set(block, { code, at: performance.now() });
  if (!settleTimer) settleTimer = setTimeout(runPendingMermaid, SETTLE_MS);
}

/** 回合結束時立即補繪這個容器裡還沒轉成圖表的 mermaid 框 */
export function flushMermaid(root) {
  if (!root) return;
  for (const code of root.querySelectorAll('.code:not(.code-mermaid) > pre > code')) {
    const m = /language-([\w+#.-]+)/.exec(code.className || '');
    if (!m || m[1].toLowerCase() !== 'mermaid') continue;
    const block = code.closest('.code');
    if (block) enhanceMermaidBlock(block, code);
  }
}

function runPendingMermaid() {
  settleTimer = 0;
  let next = Infinity;
  for (const [block, entry] of pending) {
    const age = performance.now() - entry.at;
    if (!block.isConnected) {
      if (age > DROP_MS) pending.delete(block);
      else next = Math.min(next, 200);
      continue;
    }
    if (age < SETTLE_MS) {
      next = Math.min(next, SETTLE_MS - age);
      continue;
    }
    pending.delete(block);
    enhanceMermaidBlock(block, entry.code);
  }
  if (next < Infinity) settleTimer = setTimeout(runPendingMermaid, Math.max(16, Math.ceil(next)));
}
