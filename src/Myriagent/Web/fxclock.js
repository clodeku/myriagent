// 解碼動畫（tokenfx.js 的數字、textfx.js 的標題）共用的節拍：每個登記的回呼說好下一次要在什麼時候（performance.now() 的時間）被叫，
// 時間到了才要一個影格，在那個影格裡叫它——換字的時刻和以前一樣是「到點之後的第一個影格」。
// 以前每個計數器自己 requestAnimationFrame、每個影格都要求重畫，但只有每 45 ms 才真的換字（2026-10-24 量到：背景分頁執行的情境裡
// 每秒 300 次影格回呼，主執行緒因此每秒 60 幀；每一幀都要更新所有動畫元素的樣式、圖層化、繪製），改成只在要換字的影格要影格。
// 視窗隱藏時 requestAnimationFrame 不觸發，回呼跟以前一樣等到顯示出來才繼續。
//
// 搭便車（2026-11-08）：這一格本來就要畫的時候（宿主的更新〔app.js 呼叫 ride〕、另一個回呼到點），再過 RIDE_MS 內就到點的回呼也在這一格叫；
// 宿主正在連續更新（HOST_ACTIVE_MS 內 ride 過）時，到點的回呼最多再等 LATE_MS 搭宿主的下一格，等不到才自己要影格。
// 窗格與分頁很多、好幾個對話在執行時，每一格畫面的固定成本很高（圖層化、繪製、預繪、所有動畫的樣式），很多格只是因為 token 數字換字。
// 宿主每 50 ms 合併送一次更新，換字每 45 ms：只提早不等的話，換字被推到宿主那格的前面 5 ms、永遠錯開，所以要能晚一點等宿主——
// 宿主連續更新時換字就鎖在它的 50 ms 節拍上（以前是 45～62 ms），宿主沒在更新時照舊到點之後的第一個影格才換；解碼的總長照經過的時間算、不變。

/** 計時器提早幾毫秒醒來再要影格：換字仍落在「到點之後的第一個影格」，計時器晚一點觸發（忙碌時常見）也不會多等一個影格 */
const EARLY_MS = 8;

/** 搭便車的範圍：本來就要畫的這一格，再過幾毫秒就到點的回呼也在這一格叫（最多提早這麼多；要比 45 ms 的換字間隔小很多） */
export const RIDE_MS = 12;

/** 宿主正在連續更新時，到點的回呼最多再等幾毫秒搭宿主的下一格（宿主每 50 ms 一次：45 ms 的換字到點後 5 ms 內就等到） */
export const LATE_MS = 20;

/** 最近一次 ride 之後多久內算宿主正在連續更新（比宿主的 50 ms 間隔長一點） */
export const HOST_ACTIVE_MS = 70;

/** 最近一次 ride 的時間 */
let lastRide = -Infinity;

/** 回呼 → 下一次要被叫的時間 */
const subs = new Map();
let timer = 0;
let timerAt = 0;
let raf = 0;

/** 叫 now＋slack 之前到點的回呼（slack＝0：只叫已經到點的） */
function run(now, slack) {
  for (const [fn, at] of Array.from(subs)) {
    if (at - now > slack || subs.get(fn) !== at) continue;
    const next = fn(now);
    if (typeof next === 'number') subs.set(fn, next);
    else subs.delete(fn);
  }
}

function frame(now) {
  raf = 0;
  // 有回呼到點才叫（提早醒來的那一幀什麼都不做，下一幀再看）；到點的這一格順便叫快到點的（搭便車）
  let due = false;
  for (const at of subs.values()) if (now >= at) { due = true; break; }
  if (due) run(now, RIDE_MS);
  arm();
}

function arm() {
  if (!subs.size) {
    if (timer) { clearTimeout(timer); timer = 0; }
    if (raf) { cancelAnimationFrame(raf); raf = 0; }
    return;
  }
  let at = Infinity;
  for (const v of subs.values()) if (v < at) at = v;
  const now = performance.now();
  // 宿主正在連續更新：先等它的下一格（ride 會叫），等到 at＋LATE_MS 還沒來才自己要影格
  const target = at + (now - lastRide < HOST_ACTIVE_MS ? LATE_MS : 0);
  const wait = target - EARLY_MS - now;
  if (raf) {
    if (wait <= 0) return; // 要的那一格就是要給它的
    // 搭便車叫過了、最早的回呼還早：取消那一格（不然多畫一格什麼都不換的影格）
    cancelAnimationFrame(raf);
    raf = 0;
  }
  if (timer) {
    if (timerAt === target) return;
    clearTimeout(timer);
    timer = 0;
  }
  if (wait <= 0) { raf = requestAnimationFrame(frame); return; }
  timerAt = target;
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
  arm();
}

/**
 * 這一格本來就要畫（app.js：套用宿主的更新時）：RIDE_MS 內就要到點的回呼現在就叫，和這次的更新畫在同一格。
 * now 是影格時間（requestAnimationFrame 的參數）或 performance.now()
 */
export function ride(now = performance.now()) {
  lastRide = now;
  if (!subs.size) return;
  run(now, RIDE_MS);
  arm();
}
