// 程式碼框的語法上色：items.js 的 enhanceMarkdown 把程式碼排進來，靜置一下還在畫面上才上色——串流中最後一個區塊每 50ms 就被
// patchHtml 換成新的節點，輪不到，串流停了才上色；前面已經完成的程式碼框節點不會再換，一下子就上好。每次最多做 SLICE_MS，
// 載入長對話時分好幾次做完。分詞在 syntaxrules.js（純規則），這裡只建 span、字一律放 textContent，結果依文字快取。
// 工具與核准卡的差異檢視也排進同一個佇列（queueDiff）：每一行換成語法上色與改動的字（diffrules.js），字一樣只放 textContent、接起來和原文一字不差。
import { tokenize, langOf, MAX_HIGHLIGHT } from './syntaxrules.js';
import { diffSegments } from './diffrules.js';

const SETTLE_MS = 250;   // 排進來之後這麼久沒被換掉才上色
const SLICE_MS = 12;     // 每一次最多做這麼久，其餘的下一次再做
const DROP_MS = 5000;    // 一直沒放進畫面的（關掉的對話）這麼久之後丟掉
const CACHE_MAX = 300;   // 分詞結果快取幾份（同一段程式碼重畫時不必再分詞）

const pending = new Map(); // code 元素 → { lang, at }；差異的 .diff 元素 → { diff: 行, lang（可能是 null）, at }；程式碼框（.code）→ { gutter: 行數, at }
const cache = new Map();   // 語言＋文字 → 片段
let timer = 0;

/** 排進上色佇列：語言認不得時不排 */
export function queueHighlight(code, name) {
  const lang = langOf(name);
  if (!code || !lang) return;
  pending.set(code, { lang, at: performance.now() });
  if (!timer) timer = setTimeout(run, SETTLE_MS);
}

/**
 * 排進差異的上色佇列（items.js 的 renderDiff）：name 是檔名或副檔名（認不得的語言只標改動的字）。
 * lines 是畫出來的那幾行（.diff 的前幾個子元素一行一個），靜置後一行一行換掉 .dl-t 的內容
 */
export function queueDiff(box, lines, name) {
  if (!box || !Array.isArray(lines) || !lines.length) return;
  pending.set(box, { diff: lines, lang: langOf(name), at: performance.now() });
  if (!timer) timer = setTimeout(run, SETTLE_MS);
}

/** 排進行號欄的佇列（items.js 的 enhanceMarkdown，兩行以上的程式碼框）：靜置後還在畫面上才加（同語法上色） */
export function queueGutter(block, lines) {
  if (!block || !(lines >= 2)) return;
  pending.set(block, { gutter: lines, at: performance.now() });
  if (!timer) timer = setTimeout(run, SETTLE_MS);
}

/** 程式碼框左邊加行號欄（fx.css 的 .code.has-ln、.code-gutter）：字一律放 textContent */
function paintGutter(block, lines) {
  const pre = block.querySelector(':scope > pre');
  if (!pre || block.classList.contains('has-ln')) return;
  const g = document.createElement('span');
  g.className = 'code-gutter';
  g.setAttribute('aria-hidden', 'true');
  g.textContent = Array.from({ length: lines }, (_, i) => String(i + 1)).join(String.fromCharCode(10));
  block.classList.add('has-ln');
  pre.before(g);
}

function tokensOf(text, lang) {
  const key = lang + '\n' + text;
  let toks = cache.get(key);
  if (toks) { cache.delete(key); cache.set(key, toks); return toks; }
  toks = tokenize(text, lang);
  cache.set(key, toks);
  if (cache.size > CACHE_MAX) cache.delete(cache.keys().next().value);
  return toks;
}

function paint(code, lang) {
  const text = code.textContent;
  if (!text || text.length > MAX_HIGHLIGHT) return;
  const toks = tokensOf(text, lang);
  if (toks.length < 2 && !toks.some(([t]) => t)) return;
  const frag = document.createDocumentFragment();
  for (const [type, s] of toks) {
    if (!type) { frag.append(s); continue; }
    const span = document.createElement('span');
    span.className = 'tk-' + type;
    span.textContent = s;
    frag.append(span);
  }
  code.replaceChildren(frag);
}

/** 差異的每一行換成片段（diffrules.diffSegments）：類別是 span.tk-*，改動的字加 .dx；文字和原本那一行對不上（被換掉了）就不動 */
function paintDiff(box, p) {
  const segs = diffSegments(p.diff, p.lang ? (t) => tokensOf(t, p.lang) : null);
  if (!segs) return;
  const rows = box.children;
  for (let i = 0; i < segs.length && i < rows.length; i++) {
    const pieces = segs[i];
    if (!pieces.some(([type, , chg]) => type || chg)) continue;
    const tx = rows[i].lastElementChild;
    if (!tx || !tx.classList.contains('dl-t') || tx.textContent !== pieces.map((x) => x[1]).join('')) continue;
    const frag = document.createDocumentFragment();
    for (const [type, s, chg] of pieces) {
      if (!type && !chg) { frag.append(s); continue; }
      const span = document.createElement('span');
      span.className = [type ? 'tk-' + type : '', chg ? 'dx' : ''].filter(Boolean).join(' ');
      span.textContent = s;
      frag.append(span);
    }
    tx.replaceChildren(frag);
  }
}

function run() {
  timer = 0;
  const t0 = performance.now();
  let next = Infinity;
  for (const [code, p] of pending) {
    const now = performance.now();
    const age = now - p.at;
    if (!code.isConnected) {
      if (age > DROP_MS) pending.delete(code);
      else next = Math.min(next, 200);
      continue;
    }
    if (age < SETTLE_MS) { next = Math.min(next, SETTLE_MS - age); continue; }
    if (now - t0 > SLICE_MS) { next = 16; break; }
    pending.delete(code);
    if (p.gutter) paintGutter(code, p.gutter);
    else if (p.diff) paintDiff(code, p);
    else paint(code, p.lang);
  }
  if (pending.size) timer = setTimeout(run, Math.max(16, Number.isFinite(next) ? next : SETTLE_MS));
}
