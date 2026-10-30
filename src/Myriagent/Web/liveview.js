// 〔遠端〕瀏覽器版的即時操作（WP4.6）：app.js 不認得的訊息（commandResult、control、notify、speak、resync、clipboard、copyFallback、fileRef）交給這裡；
// 新對話開在哪台電腦（D16）；頂部列下方的常駐提示（預設密碼、伺服器換版、開啟瀏覽器通知、點一下畫面才能聽到語音）與 init 的 build 比對（D13）；
// 檔案預覽（fileFetch → fileRef 的頁內對話框，D34）。
// 電腦名稱、帳戶名稱、對話標題、要複製的文字、檔名都是其他電腦或伺服器給的字串，一律 textContent（文字框用 value）。
// workspace.js 也匯入這支（迴圈匯入）：這裡只在函式裡用 ws.*，模組載入時不碰它
import { h, iconButton, copyToClipboard, baseName, fmtClock } from './util.js';
import { icon } from './icons.js';
import { state, post, isHub, newId, peerId } from './store.js';
import { openDialog, toast, hasLayer, spinner } from './ui.js';
import { pickDevice, notifyPlan, notifyText, controlBanner, normBuild, buildChanged, reloadPlan, reloadDraftsJson, parseReloadDrafts, RELOAD_CHECK_MS, clipboardAccept, CLIPBOARD_MAX_MS, fileRefView, originOf, peerOf, controllable } from './liverules.js';
import * as cmdtrack from './cmdtrack.js';
import * as ws from './workspace.js';
import * as synclog from './synclog.js';
import { uploadsInProgress } from './composer.js';
import * as voice from './voice.js';

/** app.js 不認得的宿主訊息；commandResult 以外的只有瀏覽器版有，遠端操作紀錄與回收區只有桌面有（設定頁的對話框，synclog.js） */
export function handle(m) {
  if (!m || typeof m !== 'object') return;
  switch (m.type) {
    case 'commandResult': cmdtrack.onResult(m); break; // 只處理 cmdtrack 登記過的 id（D5）
    case 'control': if (isHub) onControl(m); break;
    case 'notify': if (isHub) onNotify(m); break;
    case 'speak': if (isHub) voice.onSpeak(m); break; // 語音通知（這個瀏覽器的開關開著才念，voice.js）
    case 'resync': if (isHub) onResync(m); break;
    case 'clipboard': if (isHub) onClipboard(m); break;
    case 'copyFallback': if (isHub) onCopyFallback(m); break;
    case 'fileRef': if (isHub || (m && fetches.has(m.requestId))) onFileRef(m); break;
    case 'remoteAudit':
    case 'remoteAuditExport':
    case 'trash':
      if (!isHub) synclog.handle(m);
      break;
    default: break;
  }
}

// ───── 頂部列下方的常駐提示（D13、D19、D20） ─────

/** 提示的順序：預設密碼、伺服器換版、開啟瀏覽器通知、點一下畫面才能聽到語音 */
const NOTICE_ORDER = ['control', 'build', 'notify', 'voice'];
const notices = new Map(); // 種類 → 元素
let noticesEl = null;

/** 提示區：第一次要顯示時才建，放在連線狀態列（沒有時是頂部列）下方 */
function noticeHost() {
  if (noticesEl && noticesEl.isConnected) return noticesEl;
  noticesEl = h('div', { class: 'live-notices' });
  const anchor = document.querySelector('.hub-status') || document.querySelector('.topbar');
  if (anchor) anchor.after(noticesEl);
  else document.body.prepend(noticesEl);
  return noticesEl;
}

/** 顯示（el）或拿掉（null）某一種提示，照固定順序排 */
function setNotice(kind, el) {
  const old = notices.get(kind);
  if (old) {
    old.remove();
    notices.delete(kind);
  }
  if (!el) return;
  el.dataset.notice = kind;
  notices.set(kind, el);
  const host = noticeHost();
  const next = NOTICE_ORDER.slice(NOTICE_ORDER.indexOf(kind) + 1).map((k) => notices.get(k)).find(Boolean) || null;
  host.insertBefore(el, next);
}

/** 一列提示：圖示、說明（textContent）、按鈕 */
function noticeRow(tone, iconName, text, field, ...actions) {
  return h('div', { class: 'live-notice', dataset: { tone } },
    icon(iconName, 'live-notice-ic'), h('span', { class: 'live-notice-text', dataset: { field }, text }), ...actions);
}

/** control（D20）：密碼還是預設值時常駐警告＋連到登入頁的「改密碼」；之後的 control 不是 true 就拿掉。時間窗不顯示 */
function onControl(m) {
  const b = controlBanner(m);
  if (!b) {
    setNotice('control', null);
    return;
  }
  if (notices.has('control')) return;
  const link = h('a', { href: b.href, class: 'btn sm', dataset: { field: 'changePassword' }, text: b.linkText });
  const row = noticeRow('warn', 'warning', b.text, 'defaultPasswordBanner', link);
  row.setAttribute('role', 'alert');
  setNotice('control', row);
}

// ───── 通知（D19）與 resync（D21） ─────

/** 瀏覽器有沒有 Notification、權限是什麼（不支援是 'unsupported'） */
function notificationPermission() {
  try {
    return typeof Notification === 'function' ? Notification.permission : 'unsupported';
  } catch {
    return 'unsupported';
  }
}

/** notify：設定關閉不顯示；分頁在背景而且已授權用系統通知（點了回到這個分頁、聚焦那個對話）；其他是頁內提示 */
function onNotify(m) {
  if (typeof m.conversationId !== 'string' || !m.conversationId) return;
  const permission = notificationPermission();
  const plan = notifyPlan(m, { enabled: state.settings.notifications !== false, hidden: document.hidden, permission });
  if (plan === 'none') return;
  const t = notifyText(m);
  if (plan === 'system' && showSystemNotification(m.conversationId, t)) return;
  toast(t.title + '・' + t.body, m.kind === 'failed' ? 'error' : 'info');
  if (permission === 'default') offerNotifications();
}

function showSystemNotification(conversationId, t) {
  try {
    const n = new Notification(t.title, { body: t.body, tag: 'myriagent-' + conversationId });
    n.onclick = () => {
      try { window.focus(); } catch { /* 忽略 */ }
      try { n.close(); } catch { /* 忽略 */ }
      ws.focusConversation(conversationId);
    };
    return true;
  } catch {
    return false; // 權限剛被收回之類：改用頁內提示
  }
}

let notifyOffered = false;

/** 第一次收到 notify 而且權限還沒決定：提示區出現「開啟瀏覽器通知」，使用者按下才要求授權（載入時不要求） */
function offerNotifications() {
  if (notifyOffered) return;
  notifyOffered = true;
  const btn = h('button', { type: 'button', class: 'btn sm', dataset: { field: 'enableNotifications' }, text: '開啟瀏覽器通知' });
  btn.addEventListener('click', () => requestNotifications());
  const dismiss = iconButton('x', '不用了', () => setNotice('notify', null), 'live-notice-x');
  setNotice('notify', noticeRow('info', 'info', '分頁在背景時，可以用瀏覽器的系統通知提醒你對話完成、提問或要核准', 'notifyPermission', btn, dismiss));
}

/** 語音通知被瀏覽器擋下（還沒點過這個頁面）：提示點一下畫面；按「播放」或點畫面任何地方就補念，之後就不再擋 */
voice.onBlockedChange((blocked) => {
  if (!blocked) {
    setNotice('voice', null);
    return;
  }
  const btn = h('button', { type: 'button', class: 'btn sm', dataset: { field: 'playVoice' }, text: '播放' });
  btn.addEventListener('click', () => voice.playPending());
  const dismiss = iconButton('x', '不用了', () => setNotice('voice', null), 'live-notice-x');
  setNotice('voice', noticeRow('info', 'info', '瀏覽器要先點一下畫面才能播放語音通知', 'voiceBlocked', btn, dismiss));
});

function requestNotifications() {
  setNotice('notify', null);
  let settled = false;
  const done = (p) => {
    if (settled) return;
    settled = true;
    if (p === 'granted') toast('已開啟瀏覽器通知', 'success');
    else if (p === 'denied') toast('瀏覽器拒絕了通知，之後改用頁內提示', 'info');
  };
  try {
    // 舊版瀏覽器只支援回呼、新版回 Promise：兩種都接，只處理一次
    const r = Notification.requestPermission(done);
    if (r && typeof r.then === 'function') r.then(done, () => {});
  } catch {
    done('unsupported');
  }
}

/** resync：開著那個對話時重送一次 open（之後的 conversation 整份重建）；null 什麼都不做（伺服器另送 init） */
function onResync(m) {
  if (typeof m.conversationId === 'string' && m.conversationId) ws.reopen(m.conversationId);
}

// ───── 剪貼簿（D6、D7、D42） ─────

/** 送出 copyItem 之後在等的：commandId → {conversationId, itemId, at, timer} */
const copies = new Map();

/**
 * Assistant 的「複製」（瀏覽器版）：送 copyItem 並記下在等哪個對話的哪個項目；伺服器回 clipboard 才寫入（D42）。
 * 沒送出回 false（按鈕不改成「複製中…」）
 */
export function requestCopy(conversationId, itemId) {
  if (!isHub) return false;
  const cid = post({ cmd: 'copyItem', conversationId, itemId });
  if (typeof cid !== 'string' || !cid) return false;
  const entry = { conversationId, itemId, at: Date.now(), timer: 0 };
  // 30 秒後還沒回來就不等了（照真實時間；clipboard 到的時候另以 Date.now 判斷過期）
  entry.timer = setTimeout(() => dropCopy(cid), CLIPBOARD_MAX_MS + 1000);
  copies.set(cid, entry);
  // 被拒（伺服器說項目不在、原文還讀不到，或傳輸層沒送出）：不等了；結果不明（新的 init、過期）照等 clipboard（它跨 init 保留）
  cmdtrack.track(cid, { onSettle: (st) => { if (st === 'rejected') dropCopy(cid); } });
  return true;
}

/** 不等這一筆了：按鈕回到「複製」 */
function dropCopy(cid) {
  const e = copies.get(cid);
  if (!e) return;
  clearTimeout(e.timer);
  copies.delete(cid);
  markCopied(e.conversationId, e.itemId, false);
}

function markCopied(conversationId, itemId, ok) {
  const cv = ws.getConv(conversationId);
  if (cv) cv.markCopied(itemId, ok);
}

/** clipboard：只收 30 秒內自己要的（clipboardAccept），寫進剪貼簿；寫不進去開退路框。不是自己要的一律略過（不寫、不開框） */
function onClipboard(m) {
  const now = Date.now();
  // 收這一則的是最早一筆對得上、沒過期的等待（同一個項目按了兩次時各收一則）
  let cid = null;
  for (const [k, e] of copies) {
    if (clipboardAccept([e], m, now)) {
      cid = k;
      break;
    }
  }
  // 過期的順便丟掉（按鈕回到「複製」）
  for (const [k, e] of copies) if (k !== cid && now - e.at > CLIPBOARD_MAX_MS) dropCopy(k);
  if (!cid) return;
  const e = copies.get(cid);
  clearTimeout(e.timer);
  copies.delete(cid);
  if (typeof m.text !== 'string') {
    markCopied(e.conversationId, e.itemId, false);
    return;
  }
  copyToClipboard(m.text).then((ok) => {
    if (!ok) openCopyFallback(m.text);
    markCopied(e.conversationId, e.itemId, ok);
  });
}

/** copyFallback（傳輸層產生）：網頁的 copyText 在瀏覽器版改開退路框 */
function onCopyFallback(m) {
  if (typeof m.text === 'string') openCopyFallback(m.text);
}

let fallbackDialog = null;

/** 退路框：唯讀的文字框（以 value 放文字）、自動全選、「關閉」；同時只開一個 */
export function openCopyFallback(text) {
  if (fallbackDialog) fallbackDialog.close();
  const ta = h('textarea', { class: 'copy-fallback-text', readOnly: true, rows: '10', spellcheck: 'false', 'aria-label': '要複製的文字', dataset: { field: 'copyFallbackText' } });
  ta.value = text;
  ta.addEventListener('focus', () => ta.select());
  const body = h('div', { class: 'copy-fallback' },
    h('p', { class: 'confirm-detail', text: '瀏覽器不讓網頁直接寫入剪貼簿。文字已經全選，請按 Ctrl+C（Mac 按 Command+C）自己複製。' }), ta);
  const close = h('button', { type: 'button', class: 'btn primary', text: '關閉' });
  const d = openDialog({
    title: '複製文字', body, footer: [close], className: 'dlg-copy-fallback', width: 560, initialFocus: ta,
    onClose: () => { if (fallbackDialog === d) fallbackDialog = null; },
  });
  fallbackDialog = d;
  close.addEventListener('click', () => d.close());
  requestAnimationFrame(() => {
    try {
      ta.focus({ preventScroll: true });
      ta.select();
    } catch { /* 忽略 */ }
  });
}

// ───── 檔案預覽（D34：fileFetch → fileRef） ─────

/** 送出 fileFetch 之後在等的：requestId → { conversationId, body, dialog } */
const fetches = new Map();
/** fileFetch 的結果不明時框裡的說明 */
const FILE_REF_UNKNOWN_TEXT = '連線中斷，結果不明，請關閉後再試一次';

/**
 * 預覽或下載對話所在電腦上的檔案（瀏覽器版）：送 fileFetch {requestId（ULID）, conversationId, path}，先開「讀取中」的對話框；
 * 收到 fileRef 才在框裡放「開啟」連結（使用者點了才開新分頁，沒有手勢的 window.open 會被擋）。被拒（原因看伺服器的提示）時關掉框。
 * opts.onReady(view)：可直接顯示的圖片與影片（回覆中的檔案連結）——成功時關掉框、改交給它（燈箱或播放器），失敗照樣在框裡說明。
 * 沒送出回 false
 */
export function fetchFile(conversationId, path, name, opts) {
  if ((!isHub && !desktopRemote(conversationId)) || typeof path !== 'string' || !path) return false;
  const requestId = isHub ? newId() : peerId();
  if (!requestId) return false;
  const cid = post({ cmd: 'fileFetch', requestId, conversationId, path });
  if (typeof cid !== 'string' || !cid) return false;
  const label = name || baseName(path);
  const body = h('div', { class: 'file-ref', dataset: { field: 'fileRef' } },
    h('div', { class: 'file-ref-row' }, icon('file'), h('span', { class: 'file-ref-name', text: label, title: label })),
    h('div', { class: 'file-ref-wait', dataset: { field: 'fileRefWait' } }, spinner(), h('span', { text: '向電腦讀取檔案中…' })));
  const close = h('button', { type: 'button', class: 'btn', text: '關閉' });
  const entry = { conversationId, body, dialog: null, onReady: opts && typeof opts.onReady === 'function' ? opts.onReady : null };
  const d = openDialog({
    title: '預覽或下載檔案', body, footer: [close], className: 'dlg-file-ref', width: 440, initialFocus: close,
    onClose: () => { if (fetches.get(requestId) === entry) fetches.delete(requestId); },
  });
  entry.dialog = d;
  fetches.set(requestId, entry);
  close.addEventListener('click', () => d.close());
  cmdtrack.track(cid, {
    onSettle: (st) => {
      if (fetches.get(requestId) !== entry) return;
      if (st === 'rejected') { d.close(); return; }
      // 結果不明（重連後的 init、150 秒過期）：fileRef 只送回發出的那條連線，重連後等不到了；框裡改成說明（晚到的 fileRef 照樣換成「開啟」，合併前審查 LIVE-6）
      if (st === 'unknown') {
        const wait = body.querySelector('[data-field="fileRefWait"]');
        if (wait) wait.remove();
        if (!body.querySelector('.file-ref-result')) {
          body.append(h('p', { class: 'file-ref-result file-ref-err', role: 'alert', dataset: { field: 'fileRefError' }, text: FILE_REF_UNKNOWN_TEXT }));
        }
      }
    },
  });
  return true;
}

/**
 * fileRef：只收自己送過、對話相同的（其他略過）。內容照 liverules.fileRefView：有錯誤或網址不是 /f/ 的相對網址時一律
 * 「這個檔案不能從網頁讀取」；否則名稱、大小、到期時間與 <a target=_blank rel="noopener noreferrer"> 的「開啟」
 * （點了由 app.js 的連結處理交給傳輸層開新分頁）
 */
function onFileRef(m) {
  const e = typeof m.requestId === 'string' ? fetches.get(m.requestId) : null;
  if (!e || m.conversationId !== e.conversationId) return;
  const v = fileRefView(m);
  const wait = e.body.querySelector('[data-field="fileRefWait"]');
  if (wait) wait.remove();
  for (const old of e.body.querySelectorAll('.file-ref-result')) old.remove();
  if (!v.ok) {
    e.body.append(h('p', { class: 'file-ref-result file-ref-err', role: 'alert', dataset: { field: 'fileRefError' }, text: v.error }));
    return;
  }
  if (e.onReady) {
    e.dialog.close();
    e.onReady(v);
    return;
  }
  const name = e.body.querySelector('.file-ref-name');
  if (name) { name.textContent = v.name; name.title = v.name; }
  if (!isHub) {
    // 〔桌面遠端操作〕/f/ 要擁有者的 cookie，桌面改由宿主以裝置的存取權杖下載到「下載」資料夾並在檔案總管顯示（peerFileSave）
    const save = h('button', { type: 'button', class: 'btn primary', dataset: { field: 'fileRefSave' } }, icon('download'), h('span', { text: '下載到這台' }));
    save.addEventListener('click', () => {
      post({ cmd: 'peerFileSave', conversationId: e.conversationId, url: v.url, name: v.name });
      e.dialog.close();
    });
    e.body.append(h('div', { class: 'file-ref-result' },
      h('p', { class: 'confirm-detail', dataset: { field: 'fileRefInfo' }, text: (v.sizeText ? v.sizeText + '・' : '') + '會下載到這台的「下載」資料夾，並在檔案總管顯示（15 分鐘內有效）。' }),
      h('div', { class: 'file-ref-acts' }, save)));
    return;
  }
  const link = h('a', { class: 'btn primary', href: v.url, target: '_blank', rel: 'noopener noreferrer', dataset: { field: 'fileRefOpen' } }, icon('popout'), h('span', { text: '開啟' }));
  link.addEventListener('click', () => setTimeout(() => e.dialog.close(), 0));
  const until = v.expiresAt ? '連結在 ' + fmtClock(v.expiresAt) + ' 前有效' : '連結 15 分鐘內有效';
  e.body.append(h('div', { class: 'file-ref-result' },
    h('p', { class: 'confirm-detail', dataset: { field: 'fileRefInfo' }, text: (v.sizeText ? v.sizeText + '・' : '') + until + '；文字檔在新分頁預覽，其他檔案會下載。' }),
    h('div', { class: 'file-ref-acts' }, link)));
}

/** 〔桌面遠端操作〕桌面上可以操作的副本（liverules.controllable） */
function desktopRemote(conversationId) {
  if (isHub || typeof conversationId !== 'string') return false;
  const conv = state.convMap.get(conversationId);
  return !!conv && controllable(originOf(conv, 'desktop'), peerOf(state.remote, 'desktop'));
}

// ───── 伺服器換版（D13） ─────

/** 上次自動重新載入的時間（只是防止重新載入的循環；讀寫失敗一律退成橫幅） */
const RELOAD_AT_KEY = 'myriagent.app.reloadAt.v1';
/** 重新載入前輸入框沒送出的字（[{conversationId, text, accountId?, deviceId?}]；這次載入時讀出一次就刪，各對話建起來時填回） */
const RELOAD_DRAFTS_KEY = 'myriagent.app.reloadDrafts.v1';
/** 這次載入第一則 init 的 build（normBuild 之後；undefined＝還沒收到） */
let firstBuild;
/** 這次載入第一則 init 的 app.commitAt（原值，versionrules.js 再正規化；還沒收到或沒有是 null） */
let firstCommitAt = null;
/** 伺服器換了版、還沒重新載入（還不能載入時每 RELOAD_CHECK_MS 重查一次） */
let reloadPending = false;
let reloadTimer = 0;
/** 最後一次操作（按鍵、點、滾輪、觸控、輸入）的時間：使用者還在用時先不重新載入；沒有操作過是 null */
let lastInputAt = null;
let inputWatched = false;
/** 重新載入前存下的草稿（對話 id → {text, accountId, deviceId}；第一次要用時才讀） */
let savedDrafts = null;

/** 提供這個網頁的伺服器 build（這次載入第一則 init 的；還沒收到或沒有是 null）：頂部列的版號（versionrules.js）用 */
export function pageBuild() {
  return firstBuild ?? null;
}

/** 提供這個網頁的伺服器 build 的提交時間（這次載入第一則 init 的 app.commitAt）：版號前面的日期時間 */
export function pageCommitAt() {
  return firstCommitAt;
}

/**
 * 每則 init（app.js 的 onInit；first＝這次載入的第一則）：記下第一則的 app.build，之後的只要正規化後不同（含從有變成沒有）就是換版，
 * 照 reloadPlan 馬上重新載入、或顯示橫幅並在狀況解除後自動重新載入（checkReload）。build 又和第一則相同時不再等、拿掉橫幅
 */
export function onInit(m, first) {
  if (!isHub) return;
  watchInput();
  const app = m && m.app && typeof m.app === 'object' ? m.app : null;
  const build = normBuild(app ? app.build : null);
  if (first || firstBuild === undefined) {
    firstBuild = build;
    firstCommitAt = app ? app.commitAt ?? null : null;
    if (savedDrafts === null) savedDrafts = readSavedDrafts(); // 重新載入前存下的草稿：一定讀一次就刪（沒有對話要還原時也是）
    return;
  }
  if (!buildChanged(firstBuild, build)) {
    reloadPending = false;
    clearTimeout(reloadTimer);
    reloadTimer = 0;
    setNotice('build', null);
    return;
  }
  reloadPending = true;
  checkReload();
}

/**
 * 換版之後能不能重新載入了（reloadPlan）：可以就先存輸入框的字、記下時間再重新載入；不行就顯示橫幅，'wait' 時 RELOAD_CHECK_MS 後再查。
 * 重新載入會弄丟的東西：等你回應的卡片裡打到一半的字與選項（合併前審查 LIVE-5）、還在上傳（或加進輸入區中）的附件；
 * 輸入框的字存進 sessionStorage、已加進輸入區的晶片本來就記在 sessionStorage，重新載入後都會還原
 */
function checkReload() {
  clearTimeout(reloadTimer);
  reloadTimer = 0;
  if (!reloadPending) return;
  let lastReloadAt = null;
  let storageOk = true;
  try {
    const raw = window.sessionStorage.getItem(RELOAD_AT_KEY);
    const v = raw === null ? NaN : Number(raw);
    lastReloadAt = Number.isFinite(v) ? v : null;
  } catch {
    storageOk = false;
  }
  const now = Date.now();
  const plan = reloadPlan({
    hasUnsentInput: ws.cardInputs().length > 0 || uploadsInProgress() > 0,
    dialogOpen: hasLayer(),
    busy: cmdtrack.pendingCount() > 0,
    editing: editingNow(),
    hidden: document.visibilityState === 'hidden',
    lastInputAt,
    lastReloadAt,
    now,
    storageOk,
  });
  if (plan === 'auto' && saveDrafts()) {
    if (markReload(now)) {
      location.reload();
      return;
    }
    dropSavedDrafts(); // 不載入了：存下的字不能留到之後手動重新載入時才冒出來
  }
  showBuildBanner();
  if (plan === 'wait') reloadTimer = setTimeout(checkReload, RELOAD_CHECK_MS);
}

/** 記下最後一次操作的時間；分頁藏起來時馬上重查（看不到的分頁不必等使用者停手） */
function watchInput() {
  if (inputWatched) return;
  inputWatched = true;
  const mark = () => { lastInputAt = Date.now(); };
  for (const type of ['keydown', 'pointerdown', 'wheel', 'touchstart', 'input']) window.addEventListener(type, mark, { capture: true, passive: true });
  document.addEventListener('visibilitychange', () => { if (reloadPending && document.visibilityState === 'hidden') checkReload(); });
}

/** 使用者正在打字：這個視窗有焦點，焦點在有字的輸入框（textarea、文字類的 input、contenteditable） */
function editingNow() {
  if (typeof document.hasFocus === 'function' && !document.hasFocus()) return false;
  const el = document.activeElement;
  if (!el || el === document.body) return false;
  if (el.isContentEditable) return (el.textContent || '').trim().length > 0;
  if (el.tagName === 'TEXTAREA') return el.value.trim().length > 0;
  if (el.tagName !== 'INPUT') return false;
  const type = (el.type || 'text').toLowerCase();
  return ['text', 'search', 'url', 'email', 'password', 'number', 'tel'].includes(type) && el.value.trim().length > 0;
}

/** 記下這次自動重新載入的時間；寫不進去回 false（記不下就不自動重新載入，免得循環） */
function markReload(now) {
  try {
    window.sessionStorage.setItem(RELOAD_AT_KEY, String(now));
    return true;
  } catch {
    return false;
  }
}

/**
 * 重新載入前把每個分頁輸入框沒送出的字存進 sessionStorage（伺服器草稿另記帳戶與電腦：新版的伺服器沒有這個草稿，
 * 載入後要以同帳戶、同電腦重開）。存好（或沒有要存的）回 true；存不下回 false（不自動重新載入，免得字不見）
 */
function saveDrafts() {
  const drafts = ws.draftTexts().map((d) => {
    const cv = ws.getConv(d.conversationId);
    const hd = cv && cv.header;
    return hd && hd.isDraft ? Object.assign({ accountId: hd.accountId, deviceId: hd.deviceId }, d) : d;
  });
  const json = reloadDraftsJson(drafts);
  try {
    if (json) window.sessionStorage.setItem(RELOAD_DRAFTS_KEY, json);
    else window.sessionStorage.removeItem(RELOAD_DRAFTS_KEY);
    return true;
  } catch {
    return json === null;
  }
}

function dropSavedDrafts() {
  try {
    window.sessionStorage.removeItem(RELOAD_DRAFTS_KEY);
  } catch { /* 儲存不能用：本來就沒存下 */ }
}

function readSavedDrafts() {
  try {
    const raw = window.sessionStorage.getItem(RELOAD_DRAFTS_KEY);
    if (raw !== null) window.sessionStorage.removeItem(RELOAD_DRAFTS_KEY);
    return parseReloadDrafts(raw);
  } catch {
    return new Map();
  }
}

/**
 * 重新載入前這個對話輸入框沒送出的字（ConvView 建好時填回輸入框；每個對話只給一次）：{text, accountId, deviceId}，沒有回 null。
 * accountId、deviceId 只有伺服器草稿有（新版的伺服器沒有這個草稿時照它們重開，workspace 的 recreateDraft）
 */
export function takeReloadDraft(id) {
  if (!isHub) return null;
  if (savedDrafts === null) savedDrafts = readSavedDrafts();
  const d = savedDrafts.get(id);
  if (!d) return null;
  savedDrafts.delete(id);
  return d;
}

function showBuildBanner() {
  if (notices.has('build')) return;
  const btn = h('button', { type: 'button', class: 'btn sm', dataset: { field: 'reloadApp' } }, icon('refresh'), h('span', { text: '重新載入' }));
  // 使用者按的：照樣先存輸入框的字（存不下也照樣載入），不記時間
  btn.addEventListener('click', () => { saveDrafts(); location.reload(); });
  const row = noticeRow('info', 'refresh', '伺服器已換版本，重新載入以取得對應的網頁', 'buildChanged', btn);
  row.setAttribute('role', 'status');
  setNotice('build', row);
}

// ───── 選電腦（D16） ─────

/** 每個帳戶上次開新對話用的電腦：{ accountId: deviceId }（只是方便，讀寫失敗一律當成沒有） */
const LAST_DEVICE_KEY = 'myriagent.app.lastDevice.v1';

function readLastDevices() {
  try {
    const raw = localStorage.getItem(LAST_DEVICE_KEY);
    const o = raw ? JSON.parse(raw) : null;
    return o && typeof o === 'object' && !Array.isArray(o) ? o : {};
  } catch {
    return {};
  }
}

function lastDeviceOf(accountId) {
  const v = accountId ? readLastDevices()[accountId] : null;
  return typeof v === 'string' && v ? v : null;
}

/** 記下這個帳戶這次用的電腦 */
export function rememberDevice(accountId, deviceId) {
  if (typeof accountId !== 'string' || !accountId || typeof deviceId !== 'string' || !deviceId) return;
  try {
    const all = readLastDevices();
    all[accountId] = deviceId;
    localStorage.setItem(LAST_DEVICE_KEY, JSON.stringify(all));
  } catch { /* 儲存不能用：下次照聚焦的對話或只有一台時直接用 */ }
}

const devicesNow = () => (state.remote && Array.isArray(state.remote.devices) ? state.remote.devices : []);

/** 這個帳戶可以開新對話的電腦（pickDevice；草稿的換電腦選單也用） */
export function deviceChoices(accountId, focusedDeviceId) {
  return pickDevice({ accountId, accounts: state.accounts, devices: devicesNow(), focusedDeviceId, lastDeviceId: lastDeviceOf(accountId) });
}

/**
 * 新對話開在哪個帳戶、哪台電腦（D16）：聚焦對話的那台、上次用的那台是候選就直接用，否則只有一台時直接用、多台時跳選擇框；
 * 沒有候選時提示原因。沒指定帳戶（Ctrl+N、空窗格的「新對話」）時用第一個有電腦可開的帳戶。
 * 回 Promise<{accountId, deviceId}>；沒有候選或取消是 null。網頁一律帶選出的 deviceId（不交給伺服器的預設）
 */
export function chooseTarget(accountId, focusedDeviceId) {
  let id = typeof accountId === 'string' && accountId ? accountId : null;
  let r = id ? deviceChoices(id, focusedDeviceId) : null;
  if (!id) {
    for (const a of state.accounts) {
      const x = deviceChoices(a.id, focusedDeviceId);
      if (x.deviceId || x.ask) { id = a.id; r = x; break; }
    }
    if (!r) r = state.accounts.length ? deviceChoices(state.accounts[0].id, focusedDeviceId) : { deviceId: null, ask: false, reason: '還沒有帳戶' };
  }
  if (r.deviceId) {
    rememberDevice(id, r.deviceId);
    return Promise.resolve({ accountId: id, deviceId: r.deviceId });
  }
  if (!r.ask) {
    toast(r.reason || '沒有可以開新對話的電腦', 'error');
    return Promise.resolve(null);
  }
  return askDevice(id, r.choices).then((deviceId) => {
    if (!deviceId) return null;
    rememberDevice(id, deviceId);
    return { accountId: id, deviceId };
  });
}

/** 選擇框：每台一個按鈕（名稱 textContent）；關掉或取消回 null */
function askDevice(accountId, choices) {
  return new Promise((resolve) => {
    let result = null;
    const acc = state.accounts.find((a) => a.id === accountId);
    const list = h('div', { class: 'pick-dev', role: 'group', 'aria-label': '電腦' });
    const body = h('div', { class: 'confirm' },
      h('p', { class: 'confirm-detail', text: (acc ? '「' + (acc.label || acc.id) + '」' : '這個帳戶') + '有好幾台電腦在線，新對話要開在哪一台？' }), list);
    const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
    const d = openDialog({ title: '選擇電腦', body, footer: [cancel], className: 'dlg-pick-dev', width: 420, onClose: () => resolve(result) });
    for (const c of choices) {
      const b = h('button', { type: 'button', class: 'pick-dev-btn', dataset: { field: 'pickDevice', deviceId: c.deviceId } },
        h('span', { class: 'ch-dev-dot', 'aria-hidden': 'true' }), h('span', { class: 'pick-dev-name', text: c.name }));
      b.dataset.online = 'true';
      b.addEventListener('click', () => { result = c.deviceId; d.close(); });
      list.append(b);
    }
    cancel.addEventListener('click', () => d.close());
    const first = list.querySelector('button');
    if (first) requestAnimationFrame(() => { try { first.focus(); } catch { /* 忽略 */ } });
  });
}
