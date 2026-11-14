// 有控制列的影片一律放在同源的空白 iframe 裡（about:blank；影片由這裡的 JS 在 iframe 的文件建立，iframe 自己沒有腳本，CSP 照舊）。
// 原因（2026-10-24 實測，Chrome 156）：內建控制列的「載入中」轉圈在影片載入中繼資料或緩衝時，對影片所在的文件註冊 animationiteration 監聽；
// 文件「有人在聽 animationiteration」的旗標一設就到文件結束都不會清掉（影片移除了也一樣），從此每個 CSS 動畫每轉一圈主執行緒都要醒來一次、
// 整頁的持續動畫跟著重算樣式——只動合成屬性的動畫也一樣（1920×1080 四窗格、兩個執行中：主執行緒 134 → 48 ms／秒）。
// 放在 iframe 裡旗標設在 iframe 的文件，主文件的動畫照樣全交給合成器。
// 外觀不變：iframe 套原本影片的框（寬、最大高度、圓角、黑底），長寬比在拿到中繼資料後照影片設（之前同 video 元素預設的 300×150）。
// iframe 被搬到別的位置（例如分頁拖到別的窗格）時瀏覽器會換一份新的文件：在 load 時重建影片，接著上次的播放位置（影片被移出頁面本來就會暫停）。
// 行為不變：iframe 裡的事件不會穿出來，頁面靠它們的在 iframe 元素上重新派送一份——按鍵（Esc 關閉播放器、Ctrl 的快捷鍵）、
// 指標按下（收起選單、換焦點窗格）、滾輪（往上捲停下自動捲到底）、右鍵（不出瀏覽器的選單）、拖放（拖檔案進來照樣出附加遮罩，iframe 不開檔），
// 頁面的處理者 preventDefault 時 iframe 裡的也取消；媒體的 error 交給 onError 並照常回報（errorreport.js）。
// 載入失敗會自動重試（mediaretryrules.js：接著試三次、頁面看不到時等看得到才試；都不行之後頁面回到前景、重新連上伺服器時再試）：
// 伺服器重啟那幾十秒裡載入的影片、iPhone 選照片時整頁一起出錯的影片，不必重新整理頁面就會好。救回來時呼叫 onRecovered
// 焦點移進 iframe 時頁面的 window 會收到 blur：theme.js 看焦點是不是在這裡的 iframe；iframe 失焦而整個視窗也沒焦點時轉一個 blur 給頁面
import { h } from './util.js';
import { reportMediaError } from './errorreport.js';
import { awaitSignal } from './mediaretry.js';
import * as MR from './mediaretryrules.js';
import { longPressAsContextMenu } from './longpress.js';

/** 開啟時的連點（雙擊開啟播放器的第二下）落在 iframe 裡的影片上會切換播放：這段時間內 iframe 裡的點按不算 */
const OPEN_GUARD_MS = 500;

/**
 * 建立放在 iframe 裡的影片。o：src、cls（iframe 的 class）、title、autoplay、playsinline、time（從哪一秒開始）、
 * outline（false：影片不畫焦點框）、guardOpen（開啟時的連點不算）、onError(video)（載入失敗：一回只叫一次，之後照樣自動重試）、
 * onRecovered(video)（叫過 onError 之後重試成功）、bindDoc(doc, video)（每次建好影片時呼叫：在 iframe 的文件上掛自己的事件）。
 * 觸控長按影片一律轉成右鍵（longpress.js：在 iframe 裡派送 contextmenu，再照下面的轉送到 iframe 元素上），頁面的媒體選單照常處理
 * 回傳 { el: iframe, video(): 目前的影片（還沒建立時 null）, currentTime() }
 */
export function framedVideo(o) {
  const el = h('iframe', { class: 'vframe' + (o.cls ? ' ' + o.cls : ''), title: o.title || '影片', allow: 'autoplay; fullscreen; picture-in-picture', allowfullscreen: '' });
  const created = performance.now();
  let video = null;
  let time = o.time || 0;

  // ── 載入失敗的自動重試 ──
  const retry = MR.newRetryState();
  let retryTimer = 0;
  /** 該重試的時候頁面看不到：等看得到（訊號）再試 */
  let wantRetry = false;
  /** 重新載入之後要回到的播放位置 */
  let resume = 0;
  /** 這一回已經叫過 onError（重試成功時要叫 onRecovered） */
  let shownError = false;
  let unwait = null;

  function stopWaiting() {
    clearTimeout(retryTimer);
    retryTimer = 0;
    wantRetry = false;
    if (unwait) { unwait(); unwait = null; }
  }

  function reload(v) {
    resume = time;
    // load() 照現有的 src 重新載入（再指派一次 src 也會觸發載入，兩個一起做就是載入兩次、前一次白白中止）
    v.load();
  }

  function timedRetry() {
    retryTimer = 0;
    const v = video;
    // 影片已經不在頁面上（對話關了）：不再管它
    if (!v || !el.isConnected) { stopWaiting(); return; }
    if (document.hidden) { wantRetry = true; return; }
    wantRetry = false;
    MR.noteRetry(retry, Date.now());
    reload(v);
  }

  /** 外面的訊號（mediaretry.js）：頁面回到前景、重新連上伺服器、電腦上下線 */
  function signal() {
    const v = video;
    if (!v || !el.isConnected) { stopWaiting(); return; }
    if (document.hidden) return;
    if (wantRetry) { timedRetry(); return; }
    if (retryTimer) return;
    if (MR.onSignal(retry, Date.now())) reload(v);
  }

  function onVideoError(v) {
    if (v !== video) return;
    const step = MR.onMediaError(retry, Date.now());
    const detail = MR.describeMediaError({
      code: v.error ? v.error.code : undefined, message: v.error ? v.error.message : undefined, networkState: v.networkState,
      hidden: document.hidden, online: navigator.onLine, attempt: retry.attempt, willRetry: step.kind === 'retry',
    });
    const url = v.currentSrc || v.src || '';
    if (step.kind === 'retry') {
      if (step.first) reportMediaError('video', url, detail);
      clearTimeout(retryTimer);
      retryTimer = setTimeout(timedRetry, step.delay);
    } else if (step.first) {
      reportMediaError('video', url, detail, MR.FINAL_NOTE);
    }
    if (!shownError) {
      shownError = true;
      if (o.onError) o.onError(v);
    }
    if (!unwait) unwait = awaitSignal({ signal });
  }

  function onVideoLoaded(v) {
    if (v !== video) return;
    if (resume) {
      try { v.currentTime = resume; } catch { /* 忽略 */ }
      resume = 0;
    }
    MR.onMediaLoaded(retry);
    stopWaiting();
    if (shownError) {
      shownError = false;
      if (o.onRecovered) o.onRecovered(v);
    }
  }

  function build() {
    const doc = el.contentDocument;
    if (!doc || !doc.body || (video && video.ownerDocument === doc)) return;
    const root = doc.documentElement;
    root.style.setProperty('height', '100%');
    root.style.setProperty('background', '#000');
    for (const [k, v] of [['margin', '0'], ['height', '100%'], ['overflow', 'hidden'], ['background', '#000']]) doc.body.style.setProperty(k, v);
    const v = doc.createElement('video');
    for (const [k, val] of [['display', 'block'], ['width', '100%'], ['height', '100%'], ['background', '#000']]) v.style.setProperty(k, val);
    if (o.outline === false) v.style.setProperty('outline', 'none');
    // 長按不出 iOS 的「儲存影片」浮出選單、不選取文字：長按交給頁面的媒體選單
    v.style.setProperty('-webkit-touch-callout', 'none');
    v.style.setProperty('-webkit-user-select', 'none');
    v.style.setProperty('user-select', 'none');
    v.controls = true;
    v.preload = 'metadata';
    if (o.playsinline) v.playsInline = true;
    if (o.autoplay && !video) v.autoplay = true; // 重建（搬動位置）時不自動播放：影片被移出頁面時本來就停了
    v.addEventListener('loadedmetadata', () => {
      if (v.videoWidth && v.videoHeight) el.style.setProperty('aspect-ratio', v.videoWidth + ' / ' + v.videoHeight);
      if (time) { try { v.currentTime = time; } catch { /* 忽略 */ } }
    }, { once: true });
    v.addEventListener('timeupdate', () => { if (v.readyState) time = v.currentTime; });
    v.addEventListener('error', () => onVideoError(v));
    v.addEventListener('loadedmetadata', () => onVideoLoaded(v));
    forwardEvents(el, doc);
    longPressAsContextMenu(doc, (t) => t === v);
    if (o.guardOpen) {
      const swallow = (e) => { if (performance.now() - created < OPEN_GUARD_MS) { e.preventDefault(); e.stopPropagation(); } };
      for (const type of ['mousedown', 'mouseup', 'click', 'dblclick']) doc.addEventListener(type, swallow, true);
    }
    v.src = o.src;
    doc.body.append(v);
    video = v;
    if (o.bindDoc) o.bindDoc(doc, v);
  }

  // 插進頁面時瀏覽器建立 about:blank 並觸發 load（搬到別的位置時也會）
  el.addEventListener('load', build);
  return { el, video: () => video, currentTime: () => (video && video.readyState ? video.currentTime : time) };
}

/** iframe 裡頁面靠的事件：在 iframe 元素上重新派送一份（座標換成頁面的） */
function forwardEvents(el, doc) {
  const at = (e) => {
    const r = el.getBoundingClientRect();
    return { clientX: r.left + e.clientX, clientY: r.top + e.clientY, screenX: e.screenX, screenY: e.screenY,
      ctrlKey: e.ctrlKey, shiftKey: e.shiftKey, altKey: e.altKey, metaKey: e.metaKey, bubbles: true, cancelable: true, composed: true };
  };
  const send = (copy, e) => { if (!el.dispatchEvent(copy) && e.cancelable) e.preventDefault(); };
  doc.addEventListener('keydown', (e) => send(new KeyboardEvent('keydown', { key: e.key, code: e.code, location: e.location, repeat: e.repeat,
    ctrlKey: e.ctrlKey, shiftKey: e.shiftKey, altKey: e.altKey, metaKey: e.metaKey, bubbles: true, cancelable: true, composed: true }), e));
  doc.addEventListener('pointerdown', (e) => send(new PointerEvent('pointerdown', Object.assign(at(e),
    { button: e.button, buttons: e.buttons, pointerId: e.pointerId, pointerType: e.pointerType, isPrimary: e.isPrimary })), e), true);
  doc.addEventListener('wheel', (e) => el.dispatchEvent(new WheelEvent('wheel', Object.assign(at(e),
    { deltaX: e.deltaX, deltaY: e.deltaY, deltaZ: e.deltaZ, deltaMode: e.deltaMode }))), { passive: true });
  doc.addEventListener('contextmenu', (e) => send(new MouseEvent('contextmenu', Object.assign(at(e), { button: e.button, buttons: e.buttons })), e));
  // 拖放：iframe 裡一律不讓瀏覽器處理（拖檔案放上去會把 iframe 換成那個檔）；頁面的遮罩出來後會蓋住 iframe，之後的拖放直接到頁面
  for (const type of ['dragenter', 'dragover', 'drop']) {
    doc.addEventListener(type, (e) => {
      e.preventDefault();
      el.dispatchEvent(new DragEvent(type, Object.assign(at(e), { dataTransfer: e.dataTransfer })));
    });
  }
  // 整個視窗失去焦點時焦點正在 iframe 裡：頁面的 window 收不到 blur（焦點進 iframe 時已經送過），轉一個給它；回來時同理
  const win = doc.defaultView;
  win.addEventListener('blur', () => setTimeout(() => { if (!document.hasFocus()) window.dispatchEvent(new Event('blur')); }, 0));
  win.addEventListener('focus', () => window.dispatchEvent(new Event('focus')));
}

/** 暫停 root 底下 iframe 裡正在播的影片（隱藏不會讓影片停下來） */
export function pauseFramedVideos(root) {
  for (const f of root.querySelectorAll('iframe.vframe')) {
    const v = f.contentDocument && f.contentDocument.querySelector('video');
    if (v && !v.paused) v.pause();
  }
}
