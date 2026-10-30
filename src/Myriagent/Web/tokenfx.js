// 側欄帳戶卡的 token 計數器（藍色科技風；in、out 各一個）：數字變動時，變動的位數先閃過方塊亂碼、再由左到右逐位鎖定成真實的新值
// （不另外顯示增量）。規則（格式、解碼時序、提示）在 tokenrules.js。
// 系統要求減少動態（prefers-reduced-motion）或設定關掉動態效果時不解碼，數字直接換成新值。
import { h } from './util.js';
import { calmMotion } from './theme.js';
import { fmtTokens, decodeStart, decodeDuration, decodeCells } from './tokenrules.js';
import { wakeAt } from './fxclock.js';

const reduced = calmMotion;

/** 解碼時亂碼換字的間隔（毫秒）：太快看不出是字，太慢就不像在跑 */
const SCRAMBLE_FRAME_MS = 45;

/**
 * 一個計數器：num 是整個數字（每個字一格、等寬，亂碼不會讓版面跳動）；set(value) 換成新值（第一次與減少動態時不動畫）。
 * format 把數值換成要顯示的字（預設千分位；帳戶卡用 tokenrules.js 的精簡格式）。
 * shown（選填）回答「現在看不看得到」：看不到（背景分頁、視窗最小化）時直接換成新值，不跑解碼——好幾個對話同時執行時，
 * token 與待辦數字一直在變，以前看不到的分頁也每 45 ms 寫一次亂碼（2026-10-23 剖析：bg 情境自身耗時最多的網頁函式）
 * glyphs（選填）是亂碼用的字（預設方塊；綠色的 token 計數器用 tokenrules.MATRIX_GLYPHS 的半形片假名）
 */
export function tokenCounter(format = fmtTokens, shown = null, glyphs = undefined) {
  const num = h('span', { class: 'mtok-n' });
  let value = null;
  let text = '';
  let start = -1;
  let t0 = 0;
  let ticking = false;
  let lastFrame = 0;

  function cellsFor(s) {
    while (num.childElementCount < s.length) num.append(h('span', { class: 'mtok-c' }));
    while (num.childElementCount > s.length) num.lastElementChild.remove();
    const cells = num.children;
    for (let i = 0; i < s.length; i++) cells[i].classList.toggle('sep', s[i] === ',' || s[i] === '.');
    return cells;
  }

  function paint(now) {
    const cells = cellsFor(text);
    const elapsed = now - t0;
    const out = decodeCells(text, start, elapsed, Math.random, glyphs);
    for (let i = 0; i < out.length; i++) {
      const c = cells[i];
      setChar(c, out[i].ch);
      c.classList.toggle('scr', out[i].state === 'scramble');
      c.classList.toggle('lock', out[i].state === 'lock');
    }
    return elapsed < decodeDuration(text, start) + 260;
  }

  // 每 SCRAMBLE_FRAME_MS 換一次亂碼：只在要換的那一幀要影格（fxclock.js；以前每個影格都要一幀、其中三分之二什麼都不畫）
  function frame(now) {
    lastFrame = now;
    if (!paint(now)) { ticking = false; start = -1; settle(); return null; }
    return now + SCRAMBLE_FRAME_MS;
  }

  function settle() {
    const cells = cellsFor(text);
    for (let i = 0; i < text.length; i++) {
      const c = cells[i];
      setChar(c, text[i]);
      c.classList.remove('scr', 'lock');
    }
  }

  function set(next) {
    const v = Number.isFinite(next) && next > 0 ? Math.floor(next) : 0;
    if (v === value) return;
    const old = text;
    const first = value === null;
    value = v;
    text = format(v);
    num.classList.toggle('zero', v === 0);
    if (first || reduced() || !num.isConnected || document.hidden || (shown && !shown())) {
      start = -1;
      settle();
      return;
    }
    const now = performance.now();
    const from = decodeStart(old, text);
    // 解碼途中又有新值：從兩次之中較前面的位數重新開始，已經鎖定的不必重來
    start = start >= 0 && from >= 0 ? Math.min(start, from) : from;
    t0 = now;
    if (start < 0) { settle(); return; }
    paint(now);
    if (!ticking) { ticking = true; wakeAt(frame, lastFrame + SCRAMBLE_FRAME_MS); }
  }

  return { num, set };
}

/**
 * 換一格的字：改原本那個文字節點的內容（data），不以 textContent 換掉節點——帳戶卡有 `.macct:has(.macct-run:not([hidden]))` 的規則，
 * 卡片底下每插入、移除一個節點，Chrome 都要重新判斷 :has() 並重算受它影響的整張卡（2026-10-25 失效追蹤：解碼期間 2 秒 822 次，
 * 背景分頁執行中的情境主執行緒約三成花在這裡）；改文字節點的內容不會
 */
function setChar(cell, ch) {
  const t = cell.firstChild;
  if (t && t.nodeType === 3 && !t.nextSibling) {
    if (t.data !== ch) t.data = ch;
  } else if (cell.textContent !== ch) cell.textContent = ch;
}
