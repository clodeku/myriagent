// 影片與圖片載入失敗之後的自動重試（DOM 的部分；規則與由來在 mediaretryrules.js）。
// 這裡管兩件事：
//   1. 「外面的訊號」：頁面回到前景（visibilitychange、pageshow、focus）、瀏覽器重新連上伺服器（hub-live）、電腦上下線（remote-changed）。
//      整頁只掛一組監聽，等著的影片與圖片登記在這裡（awaitSignal），平常沒有東西在等、訊號來了什麼都不做。
//   2. 對話欄裡的圖片（errorreport.js 在 window 的捕獲階段收到 img 的 error 時交過來）。影片在 framedvideo.js（它在 iframe 裡，error 到不了 window）。
import { on } from './store.js';
import * as MR from './mediaretryrules.js';

const waiting = new Set();
let installed = false;

function install() {
  if (installed) return;
  installed = true;
  const fire = () => { for (const entry of Array.from(waiting)) entry.signal(); };
  document.addEventListener('visibilitychange', () => { if (!document.hidden) fire(); });
  window.addEventListener('pageshow', fire);
  window.addEventListener('focus', fire);
  on('hub-live', (live) => { if (live) fire(); });
  on('remote-changed', fire);
}

/** 登記一個等訊號的（entry.signal() 在訊號來時被呼叫）；回取消登記的函式 */
export function awaitSignal(entry) {
  install();
  waiting.add(entry);
  return () => waiting.delete(entry);
}

/** 目前有幾個在等訊號（探針用） */
export function waitingCount() {
  return waiting.size;
}

const images = new WeakMap();

function drop(rec) {
  clearTimeout(rec.timer);
  rec.timer = 0;
  rec.wantRetry = false;
  if (rec.unwait) { rec.unwait(); rec.unwait = null; }
}

function reloadImage(img, rec, counted) {
  rec.timer = 0;
  // 圖已經不在頁面上、或換成別的網址了：不管它
  if (!img.isConnected || (img.currentSrc || img.src) !== rec.url) { drop(rec); return; }
  if (document.hidden) { rec.wantRetry = true; return; }
  rec.wantRetry = false;
  if (counted) MR.noteRetry(rec.state, Date.now());
  // 同一個網址重新指派，瀏覽器會重新載入
  img.src = rec.url;
}

/**
 * 圖片的 error（errorreport.js 交過來）：對話欄裡的圖自動重試；report(tag, url, detail, note) 是回報用的函式。
 * 不重試的（檢視器的圖、data:、blob:）照原本的回報一次
 */
export function handleImageError(img, report) {
  const url = img.currentSrc || img.src || '';
  if (!MR.imageRetryable(url, !!(img.closest && img.closest('.it')))) {
    report('img', url, null, null);
    return;
  }
  let rec = images.get(img);
  if (!rec || rec.url !== url) {
    if (rec) drop(rec);
    rec = { state: MR.newRetryState(), url, timer: 0, wantRetry: false, unwait: null, hooked: !!(rec && rec.hooked) };
    images.set(img, rec);
  }
  if (!rec.hooked) {
    rec.hooked = true;
    img.addEventListener('load', () => {
      const current = images.get(img);
      if (!current) return;
      MR.onMediaLoaded(current.state);
      drop(current);
    });
  }
  const step = MR.onMediaError(rec.state, Date.now());
  const detail = MR.describeMediaError({ hidden: document.hidden, online: navigator.onLine, attempt: rec.state.attempt, willRetry: step.kind === 'retry' });
  if (step.kind === 'retry') {
    if (step.first) report('img', url, detail, null);
    clearTimeout(rec.timer);
    rec.timer = setTimeout(() => reloadImage(img, rec, true), step.delay);
  } else if (step.first) {
    report('img', url, detail, MR.FINAL_NOTE);
  }
  if (!rec.unwait) {
    rec.unwait = awaitSignal({
      signal() {
        if (!img.isConnected) { drop(rec); return; }
        if (document.hidden) return;
        if (rec.wantRetry) { reloadImage(img, rec, true); return; }
        if (rec.timer) return;
        if (MR.onSignal(rec.state, Date.now())) reloadImage(img, rec, false);
      },
    });
  }
}
