// 瀏覽器版分頁圖示的燈（第七批視覺效果，app.js 在對話清單更新時呼叫；只有瀏覽器版，桌面 app 有工作列與視窗外框）：
// 有對話執行中時圖示右下角一顆綠點，有對話等你回應時是琥珀色的圓並寫數字（titlerules.js 的 tabBadge）。
// 樣子變了才重畫一次小圖示（canvas 畫好換掉 <link rel="icon"> 的網址；CSP 的 img-src 允許 data:），沒有任何動畫。
// 手機加到主畫面、系統支援 App Badging 時，等你回應的數字另外放在 App 圖示上（不支援就略過）

import { tabBadge, tabBadgeKey } from './titlerules.js';

const BASE_HREF = 'favicon.ico';
const SIZE = 64;
const GREEN = '#4cc38a';
const AMBER = '#e2b340';
const RIM = '#18181b';

let lastKey = '';
let base = null;
let baseLoading = null;
let pending = null;

function iconLink() {
  let link = document.querySelector('link[rel="icon"]');
  if (!link) {
    link = document.createElement('link');
    link.rel = 'icon';
    document.head.appendChild(link);
  }
  return link;
}

/** 原本的圖示（只載入一次；載不到就只畫燈） */
function loadBase() {
  if (base || baseLoading) return baseLoading || Promise.resolve(base);
  baseLoading = new Promise((resolve) => {
    const img = new Image();
    img.onload = () => { base = img; baseLoading = null; resolve(img); };
    img.onerror = () => { baseLoading = null; resolve(null); };
    img.src = BASE_HREF;
  });
  return baseLoading;
}

function draw(badge) {
  const canvas = document.createElement('canvas');
  canvas.width = SIZE;
  canvas.height = SIZE;
  const g = canvas.getContext('2d');
  if (!g) return null;
  if (base) g.drawImage(base, 0, 0, SIZE, SIZE);
  const wait = badge.kind === 'wait';
  const r = wait ? 19 : 13;
  const cx = SIZE - r - 1, cy = SIZE - r - 1;
  g.beginPath();
  g.arc(cx, cy, r + 3, 0, Math.PI * 2);
  g.fillStyle = RIM;
  g.fill();
  g.beginPath();
  g.arc(cx, cy, r, 0, Math.PI * 2);
  g.fillStyle = wait ? AMBER : GREEN;
  g.fill();
  if (wait && badge.text) {
    g.fillStyle = RIM;
    g.font = `bold ${badge.text.length > 1 ? 22 : 28}px "Segoe UI", system-ui, sans-serif`;
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.fillText(badge.text, cx, cy + 1);
  }
  return canvas.toDataURL('image/png');
}

/** App 圖示上的數字（加到主畫面的網頁 App；不支援就略過） */
function appBadge(badge) {
  try {
    if (badge && badge.kind === 'wait' && typeof navigator.setAppBadge === 'function') navigator.setAppBadge(badge.n).catch(() => {});
    else if (typeof navigator.clearAppBadge === 'function') navigator.clearAppBadge().catch(() => {});
  } catch { /* 不支援 */ }
}

/** 對話清單更新時呼叫：樣子變了才重畫 */
export function updateTabBadge(list) {
  const badge = tabBadge(list);
  const key = tabBadgeKey(badge);
  if (key === lastKey) return;
  lastKey = key;
  appBadge(badge);
  const link = iconLink();
  if (!badge) {
    pending = null;
    link.href = BASE_HREF;
    link.removeAttribute('data-badge');
    return;
  }
  pending = badge;
  loadBase().then(() => {
    // 載入原本的圖示期間又變了：只畫最後的樣子
    if (pending !== badge) return;
    const url = draw(badge);
    if (!url) return;
    link.href = url;
    link.dataset.badge = key;
  });
}
