// 本機生成檔案的操作：桌面另存／開啟與媒體預覽；網頁向對話所在的電腦要求短效下載連結。
import { h, baseName } from './util.js';
import { icon } from './icons.js';
import { post, isHub } from './store.js';
import { openDialog, toast } from './ui.js';
import { openLightbox, openVideo } from './viewer.js';
import { fetchFile } from './liveview.js';
import { fileLink, filePreviewKind, localMediaUrl, isOfficeOrPdf } from './filelinkrules.js';

/** 處理成功回 true；一般連結交回 app.js 原本的連結流程。 */
export function openFileLink(anchor) {
  const link = fileLink(anchor.getAttribute('href'));
  if (!link) return false;
  const conversationId = anchor.closest('.conv')?.dataset.conv || null;
  const name = baseName(link.path);
  const preview = filePreviewKind(link.path);
  if (isHub) {
    // 圖片與影片拿到短效網址後直接顯示（附「下載」），其他檔案在框裡給「開啟」
    const onReady = preview ? (v) => {
      const media = { url: v.url, name: v.name, conversationId, onDownload: () => downloadUrl(v.url, v.name) };
      if (preview === 'image') openLightbox([media], 0);
      else openVideo(media);
    } : null;
    if (!conversationId || !fetchFile(conversationId, link.path, name, { onReady })) toast('目前無法讀取檔案，請稍後再試', 'error');
    return true;
  }
  // 〔桌面遠端操作〕可以操作的副本：檔案在那台電腦上，照瀏覽器版以 fileFetch 讀取（框裡的「下載到這台」，liveview.fetchFile 判斷是不是副本）
  if (conversationId && fetchFile(conversationId, link.path, name)) return true;
  // 伺服器標記只能由瀏覽器的 fileFetch 處理，不在桌面開同名的本機檔案。
  if (link.remote) return true;
  const media = { url: localMediaUrl(link.path), path: link.path, name, conversationId };
  if (preview === 'image') openLightbox([media], 0);
  else if (preview === 'video') openVideo(media);
  // Word、Excel、PowerPoint、PDF：不問另存或開啟，直接用這台的預設程式開（宿主照白名單決定開或只在檔案總管選取）
  else if (isOfficeOrPdf(link.path)) post(conversationId ? { cmd: 'openPath', path: link.path, conversationId } : { cmd: 'openPath', path: link.path });
  else openLocalFile(link.path, conversationId);
  return true;
}

/**
 * 瀏覽器版下載 fileRef 的同站網址（/f/…）：沒掛進文件的 <a download>，點擊不會冒泡到 app.js 的連結處理；
 * 伺服器對二進位檔回 attachment，檔名以伺服器的 Content-Disposition 為準
 */
function downloadUrl(url, name) {
  const a = document.createElement('a');
  a.href = url;
  a.download = name || '';
  a.rel = 'noopener';
  a.click();
}

function openLocalFile(path, conversationId) {
  const name = baseName(path);
  const body = h('div', { class: 'file-ref' },
    h('div', { class: 'file-ref-row' }, icon('file'), h('span', { class: 'file-ref-name', text: name, title: name })),
    h('p', { class: 'confirm-detail', text: '另存檔案到指定位置，或使用這台電腦的預設程式開啟。' }));
  const close = h('button', { type: 'button', class: 'btn', text: '關閉' });
  const open = h('button', { type: 'button', class: 'btn', dataset: { field: 'localFileOpen' }, text: '用預設程式開啟' });
  const save = h('button', { type: 'button', class: 'btn primary', dataset: { field: 'localFileSave' } }, icon('download'), h('span', { text: '另存新檔' }));
  const dialog = openDialog({ title: '下載或開啟檔案', body, footer: [close, open, save], className: 'dlg-local-file', width: 520, initialFocus: save });
  close.addEventListener('click', () => dialog.close());
  const send = (cmd) => {
    post(conversationId ? { cmd, path, conversationId } : { cmd, path });
    dialog.close();
  };
  open.addEventListener('click', () => send('openPath'));
  save.addEventListener('click', () => send('saveFile'));
}
