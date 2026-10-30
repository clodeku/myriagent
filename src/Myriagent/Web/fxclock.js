// 解碼動畫（tokenfx.js 的數字、textfx.js 的標題）共用的節拍：每個登記的回呼說好下一次要在什麼時候（performance.now() 的時間）被叫，
// 時間到了才要一個影格，在那個影格裡叫它——換字的時刻和以前一樣是「到點之後的第一個影格」。
// 以前每個計數器自己 requestAnimationFrame、每個影格都要求重畫，但只有每 45 ms 才真的換字（2026-10-24 量到：背景分頁執行的情境裡
// 每秒 300 次影格回呼，主執行緒因此每秒 60 幀；每一幀都要更新所有動畫元素的樣式、圖層化、繪製），改成只在要換字的影格要影格。
// 視窗隱藏時 requestAnimationFrame 不觸發，回呼跟以前一樣等到顯示出來才繼續。

/** 計時器提早幾毫秒醒來再要影格：換字仍落在「到點之後的第一個影格」，計時器晚一點觸發（忙碌時常見）也不會多等一個影格 */
const EARLY_MS = 8;

/** 回呼 → 下一次要被叫的時間 */
const subs = new Map();
let timer = 0;
let timerAt = 0;
let raf = 0;

function frame(now) {
  raf = 0;
  for (const [fn, at] of Array.from(subs)) {
    if (now < at || subs.get(fn) !== at) continue; // 還沒到點（提早醒來的那一幀）：下一幀再看
    const next = fn(now);
    if (typeof next === 'number') subs.set(fn, next);
    else subs.delete(fn);
  }
  arm();
}

function arm() {
  if (raf || !subs.size) return;
  let at = Infinity;
  for (const v of subs.values()) if (v < at) at = v;
  if (timer) {
    if (timerAt <= at) return;
    clearTimeout(timer);
    timer = 0;
  }
  const wait = at - EARLY_MS - performance.now();
  if (wait <= 0) { raf = requestAnimationFrame(frame); return; }
  timerAt = at;
  timer = setTimeout(() => { timer = 0; raf = requestAnimationFrame(frame); }, wait);
}

/**
 * 在 at（performance.now() 的時間）之後的第一個影格叫 fn(影格時間)；fn 回傳下一次的時間就再排，回傳其他（undefined、null）就結束。
 * 同一個 fn 再登記會換掉原本排的時間
 */
export function wakeAt(fn, at) {
  subs.set(fn, at);
  arm();
}

/** 取消 fn（還沒被叫的不再叫） */
export function cancelWake(fn) {
  subs.delete(fn);
}
