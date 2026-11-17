// 手機上的儲存與分享（2026-11-18 使用者回報：手機上無法直接下載影片；並要求圖片、影片、檔案能直接轉發到手機上的其他 app）。
// 把本站的媒體或檔案抓下來（hubapi.fetchMediaBlob，有進度、可以取消），交給系統的分享選單（navigator.share 的 files）：
// iPhone、iPad 的分享選單有「儲存影片／儲存影像」「儲存到檔案」，也能直接傳到 LINE 等其他 app。
// 分享選單只能在使用者點按的當下叫出來：抓檔要時間，抓完時點按的效力還在就直接叫出（navigator.userActivation），
// 不在了（iOS 抓大檔時）就在框裡換成「分享」或「儲存」鈕，請使用者再點一下。純規則在 sharerules.js
import { h } from './util.js';
import { icon } from './icons.js';
import { isHub } from './store.js';
import { openDialog, toast } from './ui.js';
import { fetchMediaBlob } from './hubapi.js';
import { isHandheld, isAppleTouch } from './voicemsgrules.js';
import { SHARE_MAX_BYTES, shareableUrl, saveVia, shareOffered, mimeFor, fileNameFor, progressText, readyHint } from './sharerules.js';

/** 瀏覽器能不能分享檔案（Web Share API level 2） */
export function canShareFiles() {
  if (typeof navigator === 'undefined' || typeof navigator.share !== 'function' || typeof navigator.canShare !== 'function') return false;
  try {
    return navigator.canShare({ files: [new File([''], 'a.txt', { type: 'text/plain' })] });
  } catch {
    return false;
  }
}

function env() {
  const coarse = typeof window.matchMedia === 'function' && window.matchMedia('(pointer: coarse)').matches;
  const ua = navigator.userAgent, touch = navigator.maxTouchPoints;
  return { hub: isHub, handheld: isHandheld({ coarsePointer: coarse, userAgent: ua, maxTouchPoints: touch }),
    appleTouch: isAppleTouch({ userAgent: ua, maxTouchPoints: touch }), canShareFiles: canShareFiles() };
}

/** 「下載」要不要改走分享選單（iPhone、iPad 的瀏覽器版） */
export function saveViaShare() {
  return saveVia(env()) === 'share';
}

/** 要不要給「分享到其他 App」（瀏覽器版的手機與平板） */
export function shareAvailable() {
  return shareOffered(env());
}

/**
 * 抓 url 的檔案、交給分享選單。mode：'save'（下載鈕：說明要選「儲存影片」）或 'share'；kind：'image'|'video'|'file'。
 * 回 Promise（結束、取消、失敗都會 resolve；失敗以提示說明）
 */
export function shareMedia({ url, name, kind = 'file', mode = 'share' }) {
  if (!shareableUrl(url)) { toast('這個檔案不能從這裡分享', 'error'); return Promise.resolve(false); }
  const ctrl = new AbortController();
  const verb = mode === 'save' ? '儲存' : '分享';
  const title = h('div', { class: 'share-name', text: name || '' });
  const bar = h('div', { class: 'share-bar' }, h('div', { class: 'share-fill' }));
  const status = h('div', { class: 'share-status', dataset: { field: 'shareStatus' }, text: progressText(0, 0) });
  const go = h('button', { type: 'button', class: 'btn primary', hidden: true, dataset: { field: 'shareGo' } }, icon(mode === 'save' ? 'download' : 'share'), h('span', { text: verb }));
  const cancel = h('button', { type: 'button', class: 'btn', dataset: { field: 'shareCancel' }, text: '取消' });
  let file = null, done = false;
  const d = openDialog({
    title: mode === 'save' ? '儲存到手機' : '分享到其他 App', width: 380, className: 'share-dlg',
    body: h('div', { class: 'share-body' }, title, bar, status), footer: [cancel, go],
    onClose: () => { if (!done) ctrl.abort(); },
  });
  cancel.addEventListener('click', () => d.close());

  const finish = () => { done = true; d.close(); };
  const open = async () => {
    try {
      await navigator.share({ files: [file] });
      finish();
    } catch (e) {
      if (e && e.name === 'AbortError') { finish(); return; } // 使用者在分享選單按了取消
      if (e && e.name === 'NotAllowedError') { showGo(); return; } // 點按的效力過了：等使用者再點一下
      finish();
      toast('無法' + verb + '：' + ((e && e.message) || '分享選單打不開'), 'error');
    }
  };
  const showGo = () => {
    status.textContent = readyHint(mode, kind);
    go.hidden = false;
    try { go.focus({ preventScroll: true }); } catch { /* 忽略 */ }
  };
  go.addEventListener('click', () => { go.disabled = true; open().finally(() => { go.disabled = false; }); });

  return fetchMediaBlob(url, {
    signal: ctrl.signal, maxBytes: SHARE_MAX_BYTES,
    onProgress: (loaded, total) => {
      status.textContent = progressText(loaded, total);
      if (total) bar.firstElementChild.style.transform = 'scaleX(' + Math.min(1, loaded / total).toFixed(3) + ')';
    },
  }).then(({ blob, type }) => {
    const mime = mimeFor(name, type || blob.type);
    file = new File([blob], fileNameFor(name, mime, kind), { type: mime });
    bar.firstElementChild.style.transform = 'scaleX(1)';
    if (!navigator.canShare({ files: [file] })) {
      finish();
      toast('這個裝置不能' + verb + '這種檔案', 'error');
      return false;
    }
    // 點按的效力還在（小檔、快取過的）就直接叫出分享選單，否則等使用者再點一下
    const ua = navigator.userActivation;
    if (ua && ua.isActive) return open().then(() => true);
    showGo();
    return true;
  }, (e) => {
    if (ctrl.signal.aborted) return false;
    finish();
    toast(e && e.message === 'too-large' ? '檔案超過 1 GB，手機無法一次' + verb : '下載失敗，請稍後再試', 'error');
    return false;
  });
}
