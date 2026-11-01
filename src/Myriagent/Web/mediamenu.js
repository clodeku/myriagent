// 圖片與影片的右鍵選單（觸控長按也是，longpress.js 轉成 contextmenu；2026-11-01 使用者要求）：複製、另存新檔（瀏覽器版是下載），
// 這台電腦的檔案另有用預設程式開啟、在檔案總管顯示。對話裡的縮圖、回覆裡的圖片與影片（conversation.js）與燈箱、影片播放器（viewer.js）共用。
// 複製圖片：網頁把圖片（在 canvas 上）轉成 PNG 寫進剪貼簿（navigator.clipboard.write；ClipboardItem 的值是 Promise，iOS Safari 才認得是使用者操作裡寫的）；
// 寫不進去而且是這台的檔案時交給宿主（copyMedia，剪貼簿放檔案）。複製影片：瀏覽器的剪貼簿放不了影片，只有桌面這台的檔案能複製（copyMedia）
import { post, isHub } from './store.js';
import { openMenu, toast } from './ui.js';

/**
 * openPath／revealPath／saveFile／copyMedia 的訊息：對話裡開的帶 conversationId（WP4.6 步 10），
 * 宿主才認得出是其他電腦的副本、不開這台的同名路徑
 */
export function mediaPathCmd(cmd, path, conversationId) {
  return conversationId ? { cmd, path, conversationId } : { cmd, path };
}

/** 對話裡的媒體元素：圖片是燈箱用的 data-lb-*，影片是 data-media-*（items.js） */
const MEDIA_SELECTOR = '[data-lb-url], [data-media-url]';

/** 長按要轉成右鍵的元素（頁面）：對話裡的圖片與影片縮圖、燈箱的圖片。iframe 裡的影片由 framedvideo.js 自己認，轉出來的 pointerdown 不算 */
export function isMediaTarget(t) {
  return !!(t && t.closest && t.tagName !== 'IFRAME' && t.closest(MEDIA_SELECTOR + ', .lb-img'));
}

/** 右鍵的目標是哪個媒體：{ kind: 'image'|'video', url, name, path }；不是媒體回 null */
export function mediaOfTarget(t) {
  const n = t && t.closest ? t.closest(MEDIA_SELECTOR) : null;
  if (!n) return null;
  const d = n.dataset;
  if (d.lbUrl) return { kind: 'image', url: d.lbUrl, name: d.lbName || '', path: d.lbPath || null };
  if (!d.mediaUrl) return null;
  return { kind: d.mediaKind === 'video' ? 'video' : 'image', url: d.mediaUrl, name: d.mediaName || '', path: d.mediaPath || null };
}

/** 能不能另存（下載）：自己有下載方法的、桌面這台的檔案、瀏覽器版有網址的（同站網址，以 <a download> 下載） */
export function canSaveMedia(m) {
  return !!m && (!!m.onDownload || (isHub ? !!m.url : !!m.path));
}

/** 另存新檔（桌面：宿主的另存對話框）／下載（瀏覽器版） */
export function saveMedia(m) {
  if (!m) return;
  if (m.onDownload) m.onDownload();
  else if (isHub) { if (m.url) downloadUrl(m.url, m.name); }
  else if (m.path) post(mediaPathCmd('saveFile', m.path, m.conversationId));
}

/** 瀏覽器版下載同站網址：沒掛進文件的 <a download>，點擊不會冒泡到 app.js 的連結處理 */
export function downloadUrl(url, name) {
  const a = document.createElement('a');
  a.href = url;
  a.download = name || '';
  a.rel = 'noopener';
  a.click();
}

/** 選單項目（openMenu 的格式） */
export function mediaMenuItems(m) {
  const local = !isHub && !!m.path;
  const items = [];
  if (m.kind === 'video') {
    items.push(local
      ? { label: '複製影片', icon: 'copy', desc: '貼到檔案總管或其他程式', onSelect: () => post(mediaPathCmd('copyMedia', m.path, m.conversationId)) }
      : { label: '複製影片', icon: 'copy', disabled: true, desc: isHub ? '瀏覽器不能複製影片，請改用下載' : '這部影片不在這台電腦上，不能複製' });
  } else {
    items.push({ label: '複製圖片', icon: 'copy', onSelect: () => copyImage(m) });
  }
  if (canSaveMedia(m)) items.push({ label: isHub ? '下載' : '另存新檔', icon: 'download', cls: 'mi-save', onSelect: () => saveMedia(m) });
  if (local) {
    items.push({ type: 'sep' });
    items.push({ label: '用預設程式開啟', icon: 'popout', onSelect: () => post(mediaPathCmd('openPath', m.path, m.conversationId)) });
    items.push({ label: '在檔案總管顯示', icon: 'folderOpen', onSelect: () => post(mediaPathCmd('revealPath', m.path, m.conversationId)) });
  }
  return items;
}

/** 在 at（{x, y}，視窗座標）開媒體選單 */
export function openMediaMenu(m, at) {
  if (!m || !m.url) return null;
  return openMenu(null, mediaMenuItems(m), { at, className: 'media-menu', minWidth: 200 });
}

/** 複製圖片：網頁寫剪貼簿，不行時交給宿主（這台的檔案） */
export function copyImage(m) {
  let p;
  try {
    p = writeImageToClipboard(m.url);
  } catch (e) {
    p = Promise.reject(e);
  }
  p.then(() => toast('已複製圖片', 'success'), () => {
    if (!isHub && m.path) post(mediaPathCmd('copyMedia', m.path, m.conversationId));
    else toast(isHub ? '這個瀏覽器不能複製圖片，請改用下載' : '無法複製圖片', 'error');
  });
}

/** 把網址的圖片以 PNG 寫進剪貼簿（剪貼簿只普遍接受 PNG：其他格式在 canvas 上轉） */
export function writeImageToClipboard(url) {
  const cb = navigator.clipboard;
  if (!url || !cb || typeof cb.write !== 'function' || typeof ClipboardItem === 'undefined') return Promise.reject(new Error('unsupported'));
  // ClipboardItem 在這個使用者操作裡同步建立、值給 Promise：iOS Safari 要這樣才不拒絕
  return cb.write([new ClipboardItem({ 'image/png': pngBlobOf(url) })]);
}

/**
 * 圖片轉成 PNG：以 <img> 載入同站網址（網頁的網路請求只准在 hubapi.js，WebTransportFileTests；圖片本來就以 img 顯示）再畫到 canvas。
 * 同站的圖片不會讓 canvas 被標成跨來源，toBlob 照常可用
 */
async function pngBlobOf(url) {
  const img = new Image();
  img.decoding = 'async';
  img.src = url;
  await img.decode();
  const w = img.naturalWidth, hh = img.naturalHeight;
  if (!w || !hh) throw new Error('no size');
  const c = document.createElement('canvas');
  c.width = w;
  c.height = hh;
  c.getContext('2d').drawImage(img, 0, 0);
  return new Promise((resolve, reject) => c.toBlob((b) => (b ? resolve(b) : reject(new Error('toBlob'))), 'image/png'));
}
