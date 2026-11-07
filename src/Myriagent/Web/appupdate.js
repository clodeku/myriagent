// app 自我更新（設計 9.7）：頂部列下方的橫幅、設定頁的「Clode Myriagent」區塊。狀態全由宿主送（AppUpdateView），網頁只顯示與送指令
import { byteRate, fmtByteRate, drainProgress, forceCountdown, FORCE_CELLS } from './fxrules.js';
import { h, replayClass } from './util.js';
import { icon } from './icons.js';
import { post, state } from './store.js';
import { switchControl } from './ui.js';
import { versionWithTime } from './versionrules.js';

const PHASE_TEXT = {
  idle: '已是最新版', checking: '正在檢查…', downloading: '下載中', staged: '新版已下載', blocked: '無法更新',
  failed: '更新失敗', disabled: '不自動更新', draining: '正在套用更新', restarting: '正在重新啟動', installing: '正在安裝',
};

/** detail 是原因的階段：狀態列接在階段後面 */
const DETAIL_PHASES = new Set(['blocked', 'failed', 'disabled']);

let bannerDismissed = false;

/** 目前橫幅的結構（色調、按鈕、能不能關）；null＝沒有顯示 */
let bannerKey = null;

/** 可以「安裝到這台電腦」：安裝進行中（installing）時宿主仍送 canInstall，不再提供按鈕 */
const installable = (a) => !!a.canInstall && a.phase !== 'installing';

/** 文字真的變了才換（換掉 role=status 的內容，螢幕閱讀器會重新朗讀） */
const setText = (el, text) => { if (el.textContent !== text) el.textContent = text; };

/**
 * 已備妥時的一句話（橫幅與設定頁共用）：現在不能重啟（CLI 更新、冷卻、開著 Remote Control 的對話在用…）就說明原因，
 * 不承諾閒置時重啟
 */
const stagedText = (a) => (a.blocker ? `新版 ${a.staged} 已下載；${a.blocker}` : `新版 ${a.staged} 已下載，閒置時自動重啟套用`);

/**
 * 「強制更新」（2026-10-28 使用者要求）：不等背景工作結束就開始套用，對話陸續到安全點停下，按下後 10 分鐘還沒停下的強制停下。
 * 已備妥時與排空中（還沒強制、不是交棒）提供
 */
export const FORCE_ACTION = {
  label: '強制更新', cmd: 'appUpdateForce',
  title: '不等背景工作結束就開始套用：對話陸續到安全點停下，10 分鐘後還沒停下的強制停下（被停掉的背景工作，新版會請代理確認）',
};

/** 排空中還能改成強制更新：還沒強制、不是交棒（使用者開了新版） */
const forceable = (a) => a.phase === 'staged' || (a.phase === 'draining' && !a.forced && !a.handoverTo);

/** 強制停下的時間（時:分） */
const clockText = (ms) => new Date(ms).toLocaleTimeString('zh-TW', { hour: '2-digit', minute: '2-digit', hour12: false });

/** 狀態的一句話（設定頁） */
export function appPhaseText(a) {
  if (!a) return '';
  const base = PHASE_TEXT[a.phase] || '處理中';
  // downloaded 是 zip 已下載的位元組，total 是解壓後的總大小（manifest 的 totalBytes）：單位不同不能相除，只顯示已下載的 MB
  if (a.phase === 'downloading') return `${base} ${a.available || ''}`.trim() + (a.downloaded > 0 ? `（已下載 ${Math.round(a.downloaded / 1048576)} MB）` : '');
  if (a.phase === 'staged') return stagedText(a);
  if (a.phase === 'draining') {
    // 交棒（新版自動接手舊版）：使用者剛開了較新的版本，這一份停下對話後換成它
    const head = a.handoverTo ? `正在切換到新開的 ${a.handoverTo}` : a.forced ? '正在強制更新' : '正在套用更新';
    const force = a.forced && a.forceAt ? `（${clockText(a.forceAt)} 起還沒停下的強制停下）` : '';
    return a.waiting > 0 ? `${head}：等 ${a.waiting} 個對話到安全點${force}` : `${head}…`;
  }
  if (a.phase === 'restarting' && a.handoverTo) return `正在切換到 ${a.handoverTo}…`;
  return base;
}

/**
 * 設定頁的狀態列：blocked／failed／disabled 接上原因（detail）。
 * idle 的 detail 是檢查結果（已是最新版／沒有可用的發佈／已取消），只顯示它，不和「已是最新版」重複；其他階段不接
 */
function statusText(a) {
  if (!a) return '';
  if (a.phase === 'idle' && a.detail) return a.detail;
  return appPhaseText(a) + (DETAIL_PHASES.has(a.phase) && a.detail ? `：${a.detail}` : '');
}

/** 橫幅（null＝不顯示）：已備妥、排空中、重新啟動中、安裝中、擋下、還沒安裝 */
export function bannerOf(a, dismissed) {
  if (!a) return null;
  switch (a.phase) {
    case 'staged':
      return { tone: 'info', text: stagedText(a), actions: [{ label: '立即重啟', cmd: 'appUpdateRestartNow' }, FORCE_ACTION], dismissible: false };
    case 'draining':
      return {
        tone: 'wait', text: appPhaseText(a),
        actions: [forceable(a) ? FORCE_ACTION : null, { label: '取消這次', cmd: 'appUpdateCancel' }].filter(Boolean), dismissible: false,
      };
    case 'restarting':
      return { tone: 'wait', text: a.handoverTo ? `正在切換到 ${a.handoverTo}…` : '正在重新啟動以套用更新…', actions: [], dismissible: false };
    case 'installing':
      return { tone: 'wait', text: '正在安裝到這台電腦…', actions: [], dismissible: false };
    case 'blocked':
      return dismissed ? null : { tone: 'warn', text: `新版 ${a.available || ''} 無法安裝：${a.detail || ''}`, actions: [], dismissible: true };
    default:
      return installable(a) && !dismissed
        ? { tone: 'info', text: '要讓 Clode Myriagent 自動更新，請先把它安裝到這台電腦。', actions: [{ label: '安裝到這台電腦', cmd: 'appInstallHere' }], dismissible: true }
        : null;
  }
}

/** 下載讀數的上一筆（{ bytes, at }）與平滑過的速度 */
let dlPrev = null;
let dlRate = null;

/**
 * 頂部列版號旁的下載讀數（#appDl，app.js 建立）：下載中才有，寫已下載的 MB 與速度（fxrules.byteRate，前後兩筆的增量、平滑），
 * 每收到新的進度燈閃一下。downloaded 與 total 單位不同（壓縮檔與解壓後），不算百分比
 */
function renderDownloadChip() {
  const el = document.getElementById('appDl');
  if (!el) return;
  const a = state.appUpdate;
  if (!a || a.phase !== 'downloading' || state.mode === 'popout') { el.hidden = true; dlPrev = null; dlRate = null; return; }
  const now = Date.now();
  const bytes = Math.max(0, Number(a.downloaded) || 0);
  const cur = { bytes, at: now };
  if (!dlPrev || bytes !== dlPrev.bytes) {
    const r = dlPrev ? byteRate(dlPrev, cur) : null;
    if (r !== null) dlRate = dlRate === null ? r : dlRate * 0.6 + r * 0.4;
    dlPrev = cur;
    if (bytes > 0) replayClass(el.firstChild, 'fx-dl', 320);
  }
  el.hidden = false;
  setText(el.lastChild, '⇣ ' + (bytes / 1048576).toFixed(1) + ' MB' + (dlRate ? '・' + fmtByteRate(dlRate) : ''));
  el.title = '正在下載新版' + (a.available ? ' ' + a.available : '') + '（已下載 ' + (bytes / 1048576).toFixed(1) + ' MB' + (dlRate ? '，' + fmtByteRate(dlRate) : '') + '）';
}

/** 這次排空看過最多幾個對話在等（第十一批：排空進度的總數）；不在排空就歸零 */
let drainPeak = 0;
/** 強制排空期間每 30 秒重畫橫幅（倒數一分鐘一格） */
let forceTimer = 0;

/**
 * 排空的進度（第十一批，fx.css 的 .drain-hud）：一顆一個對話（停下的熄掉、還在等的亮著，fxrules.drainProgress），
 * 強制更新時加上強制停下的倒數（一格一分鐘，fxrules.forceCountdown）；不在排空時是 null
 */
function drainHud(a) {
  if (!a || a.phase !== 'draining') { drainPeak = 0; return null; }
  drainPeak = Math.max(drainPeak, Number.isInteger(a.waiting) ? a.waiting : 0);
  const p = drainProgress(a.waiting, drainPeak);
  const cd = a.forced ? forceCountdown(a.forceAt, Date.now()) : null;
  if (!p && !cd) return null;
  return { p, cd, sig: JSON.stringify([p && [p.total, p.left, p.shown], cd && cd.lit]) };
}

function paintDrainHud(el, hud) {
  let box = el.querySelector('.drain-hud');
  if (!hud) { if (box) box.remove(); return; }
  if (box && box._sig === hud.sig) return;
  if (!box) { box = h('span', { class: 'drain-hud', 'aria-hidden': 'true' }); el.querySelector('.app-banner-text').after(box); }
  box._sig = hud.sig;
  box.replaceChildren();
  if (hud.p) {
    const stopped = hud.p.total - hud.p.left;
    box.title = '已停下 ' + stopped + '／' + hud.p.total + ' 個對話';
    box.append(h('span', { class: 'drain-pips' }, ...Array.from({ length: hud.p.shown }, (_, i) => h('i', { class: i < hud.p.shown - Math.min(stopped, hud.p.shown) ? 'on' : null }))));
  }
  if (hud.cd) {
    box.title = (box.title ? box.title + '；' : '') + '再 ' + hud.cd.minutes + ' 分鐘強制停下還沒停下的';
    box.append(h('span', { class: 'force-cd' + (hud.cd.lit <= 2 ? ' soon' : '') }, ...Array.from({ length: FORCE_CELLS }, (_, i) => h('i', { class: i < hud.cd.lit ? 'on' : null }))));
  }
}

export function renderAppBanner() {
  renderDownloadChip();
  const el = document.getElementById('appBanner');
  if (!el) return;
  const b = state.mode === 'popout' ? null : bannerOf(state.appUpdate, bannerDismissed);
  el.hidden = !b;
  const hud = drainHud(state.appUpdate);
  // 強制排空中：每 30 秒重畫一次，倒數才會前進（沒有強制就不排）
  const forcing = !!(hud && hud.cd);
  if (forcing && !forceTimer) forceTimer = setInterval(renderAppBanner, 30_000);
  else if (!forcing && forceTimer) { clearInterval(forceTimer); forceTimer = 0; }
  if (!b) {
    el.replaceChildren();
    bannerKey = null;
    return;
  }
  // 結構沒變（例如排空中等的對話數變動）只換文字：按鈕不被換掉，鍵盤焦點與點擊不會落空
  const key = JSON.stringify([b.tone, b.actions.map((a) => [a.label, a.cmd]), b.dismissible]);
  if (key === bannerKey) {
    setText(el.querySelector('.app-banner-text'), b.text);
    paintDrainHud(el, hud);
    return;
  }
  bannerKey = key;
  el.replaceChildren();
  el.dataset.tone = b.tone;
  el.append(icon(b.tone === 'warn' ? 'warning' : 'download', 'app-banner-ic'), h('span', { class: 'app-banner-text', text: b.text }));
  paintDrainHud(el, hud);
  for (const a of b.actions) el.append(h('button', { type: 'button', class: 'btn sm', text: a.label, title: a.title, onClick: () => post({ cmd: a.cmd }) }));
  if (b.dismissible) {
    el.append(h('button', { type: 'button', class: 'ibtn app-banner-close', title: '關閉', 'aria-label': '關閉', onClick: () => { bannerDismissed = true; renderAppBanner(); } }, icon('x')));
  }
}

/** 設定頁的區塊：版本、自動更新開關、狀態與上次檢查、最近結果、按鈕。row＝settings.js 的 row(label, desc, control, setting) */
export function appSection(set, row) {
  const autoSw = switchControl({ checked: state.settings.autoUpdateApp !== false, label: '自動更新 Clode Myriagent', onChange: (v) => set('autoUpdateApp', v) });
  const version = h('span', { class: 'set-value' });
  const status = h('div', { class: 'set-desc app-status' });
  const result = h('div', { class: 'set-desc app-result' });
  const actions = h('div', { class: 'set-actions app-actions' });
  const node = h('div', null,
    row('目前版本', null, version),
    row('自動更新', '有新版時在背景下載，你沒在使用時自動重啟套用；進行中的對話會在重啟後接續。', autoSw, 'autoUpdateApp'),
    h('div', { class: 'set-row set-row-block' },
      h('div', { class: 'set-text' }, h('div', { class: 'set-label', text: '狀態' }), status, result)),
    actions);
  const at = (ms) => new Date(ms).toLocaleString('zh-TW');
  let actionsKey = null;   // 目前按鈕的組成（label、cmd）
  const refresh = () => {
    const a = state.appUpdate || {};   // 宿主還沒送過狀態：只顯示版本，狀態留白
    // 版本文字與時間取同一個來源（同 versionrules.js 的頂部列版號）：有 AppUpdateView 的 display 就用它的
    setText(version, a.display ? versionWithTime(a.display, a.commitAt) : versionWithTime(state.app.version, state.app.commitAt));
    autoSw.input.checked = state.settings.autoUpdateApp !== false;
    autoSw.input.disabled = a.managed === false;
    setText(status, statusText(state.appUpdate) + (a.lastCheckAt ? `（上次檢查 ${at(a.lastCheckAt)}）` : ''));
    setText(result, a.lastResult ? `最近一次：${a.lastResult}${a.lastResultAt ? `（${at(a.lastResultAt)}）` : ''}` : '還沒有更新過');
    // 沒有的按鈕先濾掉（replaceChildren 會把 null 變成文字「null」）。組成變了才重建，disabled 就地更新：
    // 下載進度、排空的數字一直更新時按鈕不被換掉，焦點與點擊不會落空
    const buttons = [
      { label: '立即檢查', cmd: 'appUpdateCheck', disabled: a.managed === false || a.phase === 'checking' },
      a.phase === 'staged' ? { label: '立即重啟', cmd: 'appUpdateRestartNow' } : null,
      forceable(a) ? FORCE_ACTION : null,
      installable(a) ? { label: '安裝到這台電腦', cmd: 'appInstallHere' } : null,
    ].filter(Boolean);
    const key = JSON.stringify(buttons.map((b) => [b.label, b.cmd]));
    if (key !== actionsKey) {
      actionsKey = key;
      actions.replaceChildren(...buttons.map((b) => h('button', { type: 'button', class: 'btn sm', text: b.label, title: b.title, onClick: () => post({ cmd: b.cmd }) })));
    }
    buttons.forEach((b, i) => { actions.children[i].disabled = !!b.disabled; });
  };
  refresh();
  return { node, refresh };
}
