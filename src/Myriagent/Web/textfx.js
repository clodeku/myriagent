// 標題的解碼（自動命名產生新標題時；側欄的對話列與標題列）：每個字先閃過方塊亂碼、再由左到右逐字鎖定。
// 解碼期間每個字是一個 span，真的字一直在（透明），亂碼畫在偽元素上（fx.css 的 .tdx-c），所以寬度不跳、讀到的文字一直是新標題；
// 播完換回單一文字節點。規則（何時解碼、時序）在 fxrules.js。系統要求減少動態或設定關掉動態效果時直接換字。
import { h } from './util.js';
import { calmMotion } from './theme.js';
import { titleDecodeCells, titleDecodeDuration } from './fxrules.js';
import { wakeAt, cancelWake } from './fxclock.js';

/** 換亂碼的間隔（毫秒）：同 tokenfx.js */
const FRAME_MS = 45;

function stop(st) {
  if (st.frame) cancelWake(st.frame);
  st.frame = null;
}

/**
 * 把 el 的文字換成 text；animate 為 true 時解碼（第一次、減少動態、元素不在畫面上時直接換）。
 * 文字沒變不動（解碼途中被呼叫也不會重來）；換成別的字時中斷解碼
 */
export function setDecodedText(el, text, animate) {
  const st = el._tdx || (el._tdx = { text: null, frame: null, t0: 0, last: 0, cells: null });
  if (st.text === text && el.textContent === text) return;
  stop(st);
  st.text = text;
  st.cells = null;
  if (!animate || calmMotion() || !el.isConnected || !text) { el.textContent = text; return; }
  const chars = Array.from(text);
  st.cells = chars.map((ch) => h('span', { class: 'tdx-c', text: ch }));
  el.replaceChildren(...st.cells);
  st.t0 = performance.now();
  st.last = 0;
  const end = titleDecodeDuration(text);
  const paint = (now) => {
    const out = titleDecodeCells(text, now - st.t0);
    for (let i = 0; i < out.length; i++) {
      const c = st.cells[i];
      const scr = out[i].state === 'scramble';
      c.classList.toggle('scr', scr);
      c.classList.toggle('lock', out[i].state === 'lock');
      if (scr) c.dataset.g = out[i].g;
      else if (c.dataset.g) delete c.dataset.g;
    }
  };
  // 只在要換亂碼（每 FRAME_MS）或播完的那一幀要影格（fxclock.js；以前每個影格都要一幀）
  const frame = (now) => {
    if (now - st.t0 >= end) { st.frame = null; st.cells = null; el.textContent = text; return null; }
    if (now - st.last >= FRAME_MS) { st.last = now; paint(now); }
    return Math.min(st.last + FRAME_MS, st.t0 + end);
  };
  paint(st.t0);
  st.frame = frame;
  wakeAt(frame, st.last + FRAME_MS);
}
