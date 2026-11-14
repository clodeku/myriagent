// 網頁錯誤回報（web-protocol 第二節 clientError，2026-10-23）：沒被接住的例外、沒處理的 Promise 拒絕、圖片與影片載入失敗回報給宿主，
// 桌面寫進這台的記錄檔、瀏覽器版由伺服器寫進它的記錄（使用者在畫面上看到的破圖原本不會留下任何紀錄）。規則與節流在 errorrules.js。
// 媒體的 error 事件不冒泡：在 window 以捕獲階段聽，這份文件的 img、video 都收得到（含項目的媒體格、Markdown 圖片、檢視器）；
// 放在 iframe 裡的影片（framedvideo.js）收不到，由它呼叫 reportMediaError。
// 對話欄裡的圖片載入失敗會自動重試（mediaretry.js）：第一次失敗回報一次（帶當下的情況），重試都不行再回報一次
import { post } from './store.js';
import * as R from './errorrules.js';
import { handleImageError } from './mediaretry.js';

let installed = false;
let send = null;

/**
 * 網頁自己發現的異常（不是例外）：照 error 的種類回報（宿主與伺服器不必認得新的種類），同樣受節流管。
 * 例：對話一直沒有載入完成（conversation.js 的載入看門狗）
 */
export function reportIssue(message, detail) {
  if (send) send({ kind: 'error', message: String(message || ''), source: null, detail: detail == null ? null : String(detail) });
}

/** iframe 裡的影片（framedvideo.js）：error 事件到不了這裡的 window，由它直接回報。detail、note 見 errorrules.fromMedia */
export function reportMediaError(tag, src, detail = null, note = null) {
  if (send) send(R.fromMedia(tag, src, location.href, detail, note));
}

export function installErrorReporting() {
  if (installed) return;
  installed = true;
  const allow = R.createLimiter();
  let sending = false;
  send = (r) => {
    // 回報途中又出錯（傳輸層丟例外）不再回報，避免繞圈
    if (!r || sending || !allow(r)) return;
    sending = true;
    try {
      post({ cmd: 'clientError', kind: r.kind, message: r.message, source: r.source, detail: r.detail });
    } catch {
      // 送不出去就算了
    } finally {
      sending = false;
    }
  };
  window.addEventListener('error', (e) => {
    const t = e.target;
    if (t && t !== window && t.nodeType === 1) {
      const tag = String(t.tagName || '').toLowerCase();
      if (tag === 'img') handleImageError(t, reportMediaError);
      else if (tag === 'video' || tag === 'audio' || tag === 'source') send(R.fromMedia(tag, t.currentSrc || t.src || '', location.href));
      return;
    }
    send(R.fromError(e));
  }, true);
  window.addEventListener('unhandledrejection', (e) => send(R.fromRejection(e.reason)));
}
