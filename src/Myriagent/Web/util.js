// 共用小工具：建立元素、格式化、節流、顏色
import { icon } from './icons.js';

/**
 * 建立元素：h('div', { class, text, title, style: { '--acc': c }, dataset, onClick }, ...子節點)
 * 文字一律以 textContent／文字節點放入（安全）；子節點可以是 Node、字串、陣列、null。
 */
export function h(tag, props, ...kids) {
  const el = document.createElement(tag);
  if (props) {
    for (const k in props) {
      const v = props[k];
      if (v === undefined || v === null || v === false) continue;
      if (k === 'class') el.className = v;
      else if (k === 'text') el.textContent = v;
      else if (k === 'style') { for (const s in v) if (v[s] !== undefined && v[s] !== null) el.style.setProperty(s, v[s]); }
      else if (k === 'dataset') { for (const d in v) if (v[d] !== undefined && v[d] !== null) el.dataset[d] = v[d]; }
      else if (k.length > 2 && k[0] === 'o' && k[1] === 'n' && k[2] === k[2].toUpperCase()) el.addEventListener(k.slice(2).toLowerCase(), v);
      else if (typeof v === 'boolean' || k === 'value' || k === 'tabIndex') el[k] = v;
      else el.setAttribute(k, v);
    }
  }
  appendKids(el, kids);
  return el;
}

function appendKids(el, kids) {
  for (const c of kids) {
    if (c === undefined || c === null || c === false) continue;
    if (Array.isArray(c)) appendKids(el, c);
    else el.append(c.nodeType ? c : String(c));
  }
}

/** 圖示按鈕 */
export function iconButton(name, title, onClick, cls) {
  const b = h('button', { type: 'button', class: 'ibtn' + (cls ? ' ' + cls : ''), title, 'aria-label': title });
  b.append(icon(name));
  if (onClick) b.addEventListener('click', onClick);
  return b;
}

export const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

export function debounce(fn, ms) {
  let t = 0;
  const d = (...args) => { clearTimeout(t); t = setTimeout(() => fn(...args), ms); };
  d.cancel = () => clearTimeout(t);
  d.flush = (...args) => { clearTimeout(t); fn(...args); };
  return d;
}

/**
 * 一次性的動畫（fx.css）：加上 cls、ms 毫秒後拿掉。還在播時先拿掉、強制重算一次樣式才加回去（才會從頭再播）；
 * 沒在播時直接加，不強制排版（串流中每個新項目都會呼叫）
 */
export function replayClass(el, cls, ms) {
  if (!el) return;
  const timers = el._fxTimers || (el._fxTimers = {});
  clearTimeout(timers[cls]);
  if (el.classList.contains(cls)) { el.classList.remove(cls); void el.offsetWidth; }
  el.classList.add(cls);
  timers[cls] = setTimeout(() => { el.classList.remove(cls); delete timers[cls]; }, ms);
}

/**
 * 顯示或藏起來：值沒變就不寫。Chrome 不比對 hidden 的新舊值，每寫一次都把那個元素標成要重算樣式（2026-10-25 失效追蹤：
 * 串流時每一批都重設輸入區上方的提示、待辦、回覆的動作列、捲動位置、側欄的裝置徽章與 token 列，六窗格串流時約佔重算元素的 4%）
 */
export function setHidden(el, hidden) {
  if (el.hidden !== hidden) el.hidden = hidden;
}

/**
 * 圖片載入完成才淡入（fx.css 的 .fx-img）：還沒載好時先透明，不會一下子跳出來；已經在快取裡（complete）的照常直接顯示，
 * 載入失敗拿掉 class（照常顯示破圖與替代文字）。減少動態與關掉動態效果時 CSS 不套，照常顯示
 */
export function fadeInImg(img) {
  if (!img || img.complete) return img;
  img.classList.add('fx-img');
  img.addEventListener('load', () => img.classList.add('fx-img-in'), { once: true });
  img.addEventListener('error', () => img.classList.remove('fx-img'), { once: true });
  return img;
}

// ───── 格式化（時間一律 Unix 毫秒） ─────
const pad2 = (n) => (n < 10 ? '0' : '') + n;

export function fmtClock(ms) {
  if (!ms) return '';
  const d = new Date(ms);
  return pad2(d.getHours()) + ':' + pad2(d.getMinutes());
}

export function fmtDateTime(ms) {
  if (!ms) return '';
  const d = new Date(ms);
  return (d.getMonth() + 1) + '/' + pad2(d.getDate()) + ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes());
}

/** 相對時間：剛剛／3 分鐘前／2 小時前／昨天 14:05／10/03 */
export function fmtRelative(ms, now = Date.now()) {
  if (!ms) return '';
  const s = Math.round((now - ms) / 1000);
  if (s < 45) return '剛剛';
  if (s < 3600) return Math.max(1, Math.round(s / 60)) + ' 分鐘前';
  if (s < 6 * 3600) return Math.round(s / 3600) + ' 小時前';
  const d = new Date(ms), n = new Date(now);
  const startToday = new Date(n.getFullYear(), n.getMonth(), n.getDate()).getTime();
  if (ms >= startToday) return '今天 ' + fmtClock(ms);
  if (ms >= startToday - 86400000) return '昨天 ' + fmtClock(ms);
  if (d.getFullYear() === n.getFullYear()) return (d.getMonth() + 1) + '/' + pad2(d.getDate());
  return d.getFullYear() + '/' + (d.getMonth() + 1) + '/' + pad2(d.getDate());
}

/** 剩餘時間（不含「後重置」字樣）：3 天 4 小時／2 小時 13 分／12 分／不到 1 分 */
export function fmtSpan(msLeft) {
  if (msLeft <= 0) return '';
  const totalMin = Math.floor(msLeft / 60000);
  const d = Math.floor(totalMin / 1440), hr = Math.floor((totalMin % 1440) / 60), mi = totalMin % 60;
  if (d > 0) return d + ' 天' + (hr ? ' ' + hr + ' 小時' : '');
  if (hr > 0) return hr + ' 小時' + (mi ? ' ' + mi + ' 分' : '');
  if (mi > 0) return mi + ' 分';
  return '不到 1 分';
}

/** 剩餘時間的短格式（側欄迷你圓環用，無條件捨去）：3d／12h／45m／<1m；已過回空字串 */
export function fmtSpanShort(msLeft) {
  if (!(msLeft > 0)) return '';
  const totalMin = Math.floor(msLeft / 60000);
  if (totalMin >= 1440) return Math.floor(totalMin / 1440) + 'd';
  if (totalMin >= 60) return Math.floor(totalMin / 60) + 'h';
  if (totalMin >= 1) return totalMin + 'm';
  return '<1m';
}

/** 重置倒數：「2 小時 13 分後重置」；已過時間顯示「已重置，等待更新」 */
export function fmtReset(resetsAt, now = Date.now()) {
  if (!resetsAt) return '';
  const left = resetsAt - now;
  if (left <= 0) return '已重置，等待更新';
  return fmtSpan(left) + '後重置';
}

/** 經過時間 m:ss（超過 1 小時 h:mm:ss） */
export function fmtElapsed(ms) {
  const s = Math.max(0, Math.floor(ms / 1000));
  const hr = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  return hr > 0 ? hr + ':' + pad2(m) + ':' + pad2(sec) : m + ':' + pad2(sec);
}

export function fmtBytes(n) {
  if (n === null || n === undefined) return '';
  if (n < 1024) return n + ' B';
  const units = ['KB', 'MB', 'GB', 'TB'];
  let v = n / 1024, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return (v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)) + ' ' + units[i];
}

export function pctText(p) {
  if (typeof p !== 'number' || !isFinite(p)) return '–';
  return String(Math.round(clamp(p, 0, 100)));
}

// ───── 路徑 ─────
export function baseName(p) {
  if (!p) return '';
  const parts = String(p).split(/[\\/]+/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : String(p);
}

export function extOf(name) {
  const i = (name || '').lastIndexOf('.');
  return i > 0 ? name.slice(i + 1).toLowerCase() : '';
}

/** 把 child 換成相對於 root 的路徑（不在 root 內回 null） */
export function relativeTo(root, child) {
  if (!root || !child) return null;
  const norm = (s) => String(s).replace(/[\\/]+/g, '\\').replace(/\\$/, '');
  const r = norm(root), c = norm(child);
  if (c.toLowerCase() === r.toLowerCase()) return '';
  if (c.toLowerCase().startsWith(r.toLowerCase() + '\\')) return c.slice(r.length + 1);
  return null;
}

// ───── 顏色 ─────
const HEX = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i;
export const DEFAULT_COLORS = { Claude: '#D97757', Codex: '#1FAE8E', Antigravity: '#7C8CF8', Grok: '#A3B83A' };

export function safeColor(c, provider) {
  if (typeof c === 'string' && HEX.test(c.trim())) return c.trim();
  return DEFAULT_COLORS[provider] || '#8A94A6';
}

/** WCAG 相對亮度（0～1）；不是 #rgb／#rrggbb 時回 null */
function luminance(hex) {
  const m = HEX.exec(hex || '');
  if (!m) return null;
  let s = m[1];
  if (s.length === 3) s = s.split('').map((c) => c + c).join('');
  const n = parseInt(s, 16);
  const lin = (v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
  return 0.2126 * lin((n >> 16) & 255) + 0.7152 * lin((n >> 8) & 255) + 0.0722 * lin(n & 255);
}

/** WCAG 對比值（1～21） */
function contrast(a, b) {
  const la = luminance(a), lb = luminance(b);
  if (la === null || lb === null) return 1;
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

const INK_DARK = '#141414';
const INK_LIGHT = '#ffffff';

/** 帳戶色上的文字顏色：深色字與白字取對比較高的一個（只看亮度門檻會讓多數帳戶色配上對比不到 4.5:1 的白字） */
export function inkOn(hex) {
  if (luminance(hex) === null) return INK_LIGHT;
  return contrast(hex, INK_DARK) >= contrast(hex, INK_LIGHT) ? INK_DARK : INK_LIGHT;
}

/** 帳戶卡的持續動畫（fxrules.js 的 accountPulse、pulsePhase）：data-pulse 選怠速或執行中、--fx-tempo 調快慢、--fx-phase 錯開各張卡；沒變不動 */
export function setPulse(el, pulse, phase) {
  const sig = pulse.mode + '|' + pulse.tempo + '|' + phase;
  if (el._pulse === sig) return;
  el._pulse = sig;
  el.dataset.pulse = pulse.mode;
  el.style.setProperty('--fx-phase', String(phase));
  setTempo(el, pulse.tempo);
}

/** --fx-tempo 換值分幾步、每步隔多久（約 0.6 秒走到新的速度） */
const TEMPO_STEPS = 5;
const TEMPO_STEP_MS = 120;

/**
 * --fx-tempo 換值（2026-10-25 使用者要求：轉速改變時畫面要連續、不能跳）：CSS 的動畫長度是「原本的長度 ÷ --fx-tempo」，直接換值時
 * 瀏覽器照新的長度重算進度，位置會瞬間跳到別處。所以分幾步走到新的值，每一步都保住進度（setTempoKeepingPhase）；
 * 新開始的動畫（怠速換成執行中、視窗重新取得焦點）由 CSS 直接用當時的值
 */
export function setTempo(el, target) {
  // 目標沒變（每次重畫都會呼叫）：不重設、不打斷正在分步走的那一次
  if (el._tempoTarget === target) return;
  el._tempoTarget = target;
  clearTimeout(el._tempoTimer);
  const cur = el.style.getPropertyValue('--fx-tempo');
  const from = Number(cur) || 1;
  // 第一次（還沒設過）、沒變、還沒放進畫面：直接設
  if (!cur || from === target || !el.isConnected) {
    el.style.setProperty('--fx-tempo', String(target));
    return;
  }
  let i = 0;
  const step = () => {
    i++;
    const v = i >= TEMPO_STEPS ? target : Math.round((from + ((target - from) * i) / TEMPO_STEPS) * 1000) / 1000;
    setTempoKeepingPhase(el, v);
    if (i < TEMPO_STEPS) el._tempoTimer = setTimeout(step, TEMPO_STEP_MS);
  };
  step();
}

/**
 * 換 --fx-tempo 但每個動畫走到的進度不變：換之前記下各動畫扣掉延遲後走了多久，換之後依新舊長度的比例改 currentTime
 * （長度沒變的不動；getAnimations 會先把樣式算好）
 */
function setTempoKeepingPhase(el, v) {
  const before = el.isConnected ? el.getAnimations({ subtree: true }).map((a) => {
    const t = a.effect && a.effect.getTiming();
    return { a, ct: a.currentTime, dur: t && t.duration, delay: (t && t.delay) || 0 };
  }) : [];
  el.style.setProperty('--fx-tempo', String(v));
  if (!before.length) return;
  el.getAnimations({ subtree: true });
  for (const b of before) {
    if (b.ct === null || typeof b.dur !== 'number' || !(b.dur > 0) || b.a.playState === 'idle') continue;
    const t = b.a.effect.getTiming();
    if (typeof t.duration !== 'number' || t.duration === b.dur) continue;
    b.a.currentTime = (t.delay || 0) + (b.ct - b.delay) * (t.duration / b.dur);
  }
}

/** 在元素上設定帳戶色變數（--acc、--acc-ink） */
export function setAcc(el, color, provider) {
  const c = safeColor(color, provider);
  el.style.setProperty('--acc', c);
  el.style.setProperty('--acc-ink', inkOn(c));
  return c;
}

// ───── 剪貼簿 ─────
export async function copyToClipboard(text, fallbackPost) {
  try {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch { /* 改走宿主備援 */ }
  if (fallbackPost) fallbackPost(text);
  return false;
}

/** 文字中的查詢字高亮（只產生文字節點與 <mark>，不用 innerHTML） */
export function highlight(text, query) {
  const frag = document.createDocumentFragment();
  const q = (query || '').trim();
  if (!q) { frag.append(text); return frag; }
  const lower = text.toLowerCase(), ql = q.toLowerCase();
  const idx = lower.indexOf(ql);
  if (idx >= 0) {
    frag.append(text.slice(0, idx), h('mark', { text: text.slice(idx, idx + q.length) }), text.slice(idx + q.length));
    return frag;
  }
  // 子序列比對：逐字標示
  let qi = 0;
  let buf = '';
  for (let i = 0; i < text.length; i++) {
    if (qi < ql.length && lower[i] === ql[qi]) {
      if (buf) { frag.append(buf); buf = ''; }
      frag.append(h('mark', { text: text[i] }));
      qi++;
    } else buf += text[i];
  }
  if (buf) frag.append(buf);
  return frag;
}

/** 依 key 做最小差異更新：重用既有子節點、只新增／移除／搬移必要的節點 */
export function reconcile(container, entries, keyOf, create, update) {
  const existing = new Map();
  for (const child of Array.from(container.children)) {
    if (child.dataset && child.dataset.key) existing.set(child.dataset.key, child);
  }
  let cursor = container.firstElementChild;
  const used = new Set();
  for (const entry of entries) {
    const key = keyOf(entry);
    let node = existing.get(key);
    if (node) update(node, entry);
    else {
      node = create(entry);
      node.dataset.key = key;
    }
    used.add(key);
    if (node !== cursor) container.insertBefore(node, cursor);
    else cursor = cursor.nextElementSibling;
  }
  for (const [key, node] of existing) if (!used.has(key)) node.remove();
}

