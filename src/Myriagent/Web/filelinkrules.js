// 回覆中的本機檔案連結：只辨識渲染器產生的標記與桌面 /open 網址，不依連結文字猜測用途。
const LOCAL_ORIGIN = 'https://myriagent.local';
const LOCAL_MARKER = '#local-file=';

/** 本機磁碟的完整路徑；UNC 與其他協定不交給宿主。 */
function localPath(path) {
  return typeof path === 'string' && /^[a-z]:[\\/]/i.test(path) && !/[\u0000-\u001f]/.test(path) ? path : null;
}

/** 回傳 { path, remote }，一般網址或無效的檔案標記回 null。 */
export function fileLink(href) {
  if (typeof href !== 'string') return null;
  if (href.startsWith(LOCAL_MARKER)) {
    try {
      const path = localPath(decodeURIComponent(href.slice(LOCAL_MARKER.length)));
      return path ? { path, remote: true } : null;
    } catch { return null; }
  }
  try {
    const url = new URL(href, LOCAL_ORIGIN);
    if (url.origin !== LOCAL_ORIGIN || url.username || url.password || url.pathname !== '/open') return null;
    const path = localPath(url.searchParams.get('path'));
    return path ? { path, remote: false } : null;
  } catch { return null; }
}

/** 和宿主可直接顯示的媒體格式相同；SVG 等主動內容保持一般檔案。 */
export function filePreviewKind(path) {
  if (/\.(png|jpe?g|gif|webp|bmp|ico|avif)$/i.test(path || '')) return 'image';
  if (/\.(mp4|m4v|webm|mov)$/i.test(path || '')) return 'video';
  return null;
}

export function localMediaUrl(path) {
  return LOCAL_ORIGIN + '/media?path=' + encodeURIComponent(path);
}
