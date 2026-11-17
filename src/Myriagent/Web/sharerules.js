// 手機上的儲存與分享（share.js，2026-11-18 使用者回報：手機上無法直接下載影片；並要求圖片、影片、檔案能直接轉發到手機上的其他 app）的純規則。
// iPhone、iPad 加到主畫面的網頁 app 不支援 <a download>（點了沒反應），改把檔案抓下來交給系統的分享選單（Web Share API 的 files）：
// 那裡有「儲存影片／儲存影像」「儲存到檔案」，也能直接傳到 LINE 等其他 app。

/** 一次最多抓多大的檔來分享（整個檔放在記憶體裡，太大的手機會撐不住）：1 GB */
export const SHARE_MAX_BYTES = 1024 * 1024 * 1024;

/** 只抓本站的媒體與檔案網址（/m/ 原檔、/t/ 縮圖、/f/ 下載參照；相對網址，不能是 // 開頭的其他網站） */
export function shareableUrl(url) {
  return typeof url === 'string' && /^\/(m|t|f)\//.test(url);
}

/**
 * 「下載」要怎麼做：'share'＝交給分享選單（iPhone、iPad：<a download> 在主畫面 app 沒反應，分享選單有「儲存影片」「儲存到檔案」）；
 * 'download'＝照舊 <a download>（電腦、Android 的瀏覽器下載得了）
 */
export function saveVia({ hub, appleTouch, canShareFiles } = {}) {
  return hub && appleTouch && canShareFiles ? 'share' : 'download';
}

/** 選單與工具列要不要有「分享」：瀏覽器版的手機與平板、瀏覽器能分享檔案 */
export function shareOffered({ hub, handheld, canShareFiles } = {}) {
  return !!(hub && handheld && canShareFiles);
}

const MIME = {
  jpg: 'image/jpeg', jpeg: 'image/jpeg', png: 'image/png', gif: 'image/gif', webp: 'image/webp', heic: 'image/heic', heif: 'image/heif',
  bmp: 'image/bmp', svg: 'image/svg+xml', avif: 'image/avif',
  mp4: 'video/mp4', m4v: 'video/mp4', mov: 'video/quicktime', webm: 'video/webm', mkv: 'video/x-matroska', avi: 'video/x-msvideo',
  mp3: 'audio/mpeg', m4a: 'audio/mp4', wav: 'audio/wav', ogg: 'audio/ogg', aac: 'audio/aac',
  pdf: 'application/pdf', txt: 'text/plain', csv: 'text/csv', json: 'application/json', zip: 'application/zip',
  doc: 'application/msword', docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  xls: 'application/vnd.ms-excel', xlsx: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  ppt: 'application/vnd.ms-powerpoint', pptx: 'application/vnd.openxmlformats-officedocument.presentationml.presentation',
};
const EXT = { 'image/jpeg': 'jpg', 'image/png': 'png', 'image/gif': 'gif', 'image/webp': 'webp', 'image/heic': 'heic', 'video/mp4': 'mp4',
  'video/quicktime': 'mov', 'video/webm': 'webm', 'audio/mpeg': 'mp3', 'application/pdf': 'pdf' };

/** 副檔名（小寫、不含點；沒有是空字串） */
export function extOf(name) {
  const m = /\.([A-Za-z0-9]{1,8})$/.exec(typeof name === 'string' ? name : '');
  return m ? m[1].toLowerCase() : '';
}

/**
 * 分享出去的檔案類型：伺服器回的類型是具體的就用它，否則照副檔名猜；都不知道是 application/octet-stream。
 * iOS 的分享選單看類型決定給不給「儲存影片」，所以影片、圖片一定要有對的類型
 */
export function mimeFor(name, served) {
  const s = typeof served === 'string' ? served.split(';')[0].trim().toLowerCase() : '';
  if (s && s !== 'application/octet-stream' && s !== 'binary/octet-stream') return s;
  return MIME[extOf(name)] || (s || 'application/octet-stream');
}

/**
 * 分享出去的檔名：用顯示的名稱（去掉路徑與檔名不能有的字元）；沒有名稱就用預設的；沒有副檔名時依類型補上
 * （iOS 依副檔名認檔案，沒有副檔名的影片存不進相簿）
 */
export function fileNameFor(name, mime, kind) {
  let n = typeof name === 'string' ? name.split(/[\\/]/).pop().replace(/[\u0000-\u001f<>:"|?*]/g, '').trim() : '';
  if (!n) n = kind === 'video' ? '影片' : kind === 'image' ? '圖片' : '檔案';
  if (!extOf(n)) {
    const ext = EXT[typeof mime === 'string' ? mime.split(';')[0].trim().toLowerCase() : ''];
    if (ext) n += '.' + ext;
  }
  return n.length > 120 ? n.slice(0, 120 - (extOf(n).length + 1)) + (extOf(n) ? '.' + extOf(n) : '') : n;
}

/** 進度的說法：「12.3 MB／45.6 MB（27%）」；不知道總大小時只有已下載的量 */
export function progressText(loaded, total) {
  const mb = (n) => (n >= 1024 * 1024 ? (n / 1024 / 1024).toFixed(1) + ' MB' : Math.max(1, Math.round(n / 1024)) + ' KB');
  const l = Number(loaded) > 0 ? Number(loaded) : 0;
  const t = Number(total) > 0 ? Number(total) : 0;
  if (!t) return l ? '已下載 ' + mb(l) : '準備中…';
  return mb(l) + '／' + mb(t) + '（' + Math.min(100, Math.floor((l / t) * 100)) + '%）';
}

/** 準備好之後的說明：儲存時告訴使用者在分享選單選哪一項 */
export function readyHint(mode, kind) {
  if (mode !== 'save') return '按「分享」選要傳到哪個 App。';
  if (kind === 'video') return '按「儲存」後，在選單選「儲存影片」存到相簿，或「儲存到檔案」。';
  if (kind === 'image') return '按「儲存」後，在選單選「儲存影像」存到相簿，或「儲存到檔案」。';
  return '按「儲存」後，在選單選「儲存到檔案」。';
}
