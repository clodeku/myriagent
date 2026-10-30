// 主題、字級與動態效果：settings.theme（dark／light／system）、settings.fontSize（12～18，影響對話內容）、
// settings.visualEffects（false＝根元素 data-fx="off"，app.css 照「減少動態」的規則停掉持續的動畫；瀏覽器版沒有這個鍵，一律開）
import { themeWipe } from './fxrules.js';

const mq = window.matchMedia('(prefers-color-scheme: dark)');
const motionMq = window.matchMedia('(prefers-reduced-motion: reduce)');
const motionListeners = new Set();
let current = 'dark';
/** 目前套用的深淺色（'dark'／'light'；還沒套用是 null）：換了才掃過去換色（fxrules.themeWipe） */
let appliedTheme = null;
/** 正在播的主題掃描（View Transition）；播完是 null */
let themeTransition = null;

export function applySettings(s) {
  current = (s && s.theme) || 'dark';
  applyTheme();
  const fs = Math.max(12, Math.min(18, Number(s && s.fontSize) || 14));
  document.documentElement.style.setProperty('--fs-chat', fs + 'px');
  const fx = s && s.visualEffects === false ? 'off' : 'on';
  if (document.documentElement.dataset.fx !== fx) {
    document.documentElement.dataset.fx = fx;
    notifyMotion();
  }
}

/** 持續的動態要不要停：系統要求減少動態，或設定關掉了動態效果（tokenfx.js 的解碼、monitor.js 的刻度環轉動照這個） */
export function calmMotion() {
  return motionMq.matches || document.documentElement.dataset.fx === 'off';
}

/** calmMotion() 可能改變時呼叫（系統設定或 app 設定改了） */
export function onMotionChange(fn) {
  motionListeners.add(fn);
}

function notifyMotion() {
  for (const fn of motionListeners) {
    try { fn(); } catch (e) { console.error(e); }
  }
}

motionMq.addEventListener('change', notifyMotion);

/**
 * 套用深淺色。換了深淺色（設定頁、系統跟著變）時以 View Transitions 由上往下掃過去換成新的（fx.css 的 .vt-theme：
 * 瀏覽器拍下舊畫面，新畫面以 clip-path 由上往下露出來，只在切換那一下）；第一次套用、沒有要求減少動態、關掉動態效果、
 * 頁面看不到或瀏覽器不支援時直接換
 */
function applyTheme() {
  const next = (current === 'system' ? mq.matches : current !== 'light') ? 'dark' : 'light';
  const root = document.documentElement;
  const set = () => {
    root.dataset.theme = next;
    root.style.colorScheme = next;
  };
  // 上一次的掃描還沒播完（連續切換、設定被改回）就直接換：新的轉場會跳過舊的，舊的 ready 等承諾會以「skipped」拒絕
  const wipe = themeWipe(appliedTheme, next) && !themeTransition && typeof document.startViewTransition === 'function' && !calmMotion() && document.visibilityState === 'visible';
  appliedTheme = next;
  if (!wipe) { set(); return; }
  root.classList.add('vt-theme');
  try {
    const t = document.startViewTransition(set);
    themeTransition = t;
    const quiet = () => {};
    t.ready.catch(quiet);
    t.updateCallbackDone.catch(quiet);
    t.finished.catch(quiet).finally(() => {
      if (themeTransition === t) themeTransition = null;
      root.classList.remove('vt-theme');
    });
  } catch {
    themeTransition = null;
    root.classList.remove('vt-theme');
    set();
  }
}

mq.addEventListener('change', () => { if (current === 'system') applyTheme(); });

// 視窗沒有焦點（切到別的程式、或在 app 的另一個視窗）：根元素 data-window="inactive"，fx.css 停掉裝飾性的持續動畫（掃光、旋轉、
// 音量計、電腦狀態面板的雷達），轉圈、游標、執行中的小點這類狀態指示照常（2026-10-24 擁有者決定：還有動畫在跑，畫面就每一幀都要畫；
// 四窗格兩個執行中時主執行緒約少一半）。只認明確的 blur／focus 與頁面上的點按、按鍵，不在載入時以 document.hasFocus() 猜——
// WebView2 剛建好時可能還沒拿到焦點，你正在看卻停下來
function setWindowActive(active) {
  if (active) {
    if (document.documentElement.dataset.window) delete document.documentElement.dataset.window;
  } else if (document.documentElement.dataset.window !== 'inactive') {
    document.documentElement.dataset.window = 'inactive';
  }
}
// 焦點移進影片的 iframe（framedvideo.js）時 window 也會收到 blur，但視窗其實還有焦點：下一個工作再看焦點在哪裡
window.addEventListener('blur', () => setTimeout(() => {
  const a = document.activeElement;
  setWindowActive(!!(a && a.matches && a.matches('iframe.vframe')) && document.hasFocus());
}, 0));
window.addEventListener('focus', () => setWindowActive(true));
document.addEventListener('pointerdown', () => setWindowActive(true), true);
document.addEventListener('keydown', () => setWindowActive(true), true);
