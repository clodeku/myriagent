// 臨時對話（2026-11-11 使用者要求：右上角的對話鈕開一個浮動面板，問日常、不屬於任何專案的問題；可以選帳戶、模型與推理強度，
// 不用選工作資料夾，問完不留紀錄；回覆可以按「朗讀」念出來）。帳戶在面板裡對話標頭的帳戶晶片換（同一般對話），模型與推理強度在輸入區。
// 面板裡嵌的是一般的對話畫面（workspace 的 dock）：宿主以 quickChat 開草稿（header.ephemeral），第一次送出才建立；
// 關掉面板送 quickChatEnd（宿主刪掉對話與 CLI 的逐字稿，還沒送出的草稿直接丟掉）。
// 朗讀：桌面交給宿主以語音通知同一個 SAPI 念（speakItem）；瀏覽器版從畫面上的回覆取文字、以瀏覽器的語音念（voice.js 的 readAloud）。
// 純規則在 quickchatrules.js
import { h, setHidden } from './util.js';
import { icon } from './icons.js';
import { post, state, isHub, isCollaborator } from './store.js';
import { toast } from './ui.js';
import { readAloud, stopReadAloud } from './voice.js';
import { isDraftId } from './liverules.js';
import * as ws from './workspace.js';
import { QUICK_DOCK, QUICK_RECT_KEY, QUICK_ACCOUNT_KEY, QUICK_MIN_W, QUICK_MIN_H, panelRect, speechChunks, CODE_BLOCK_SPOKEN } from './quickchatrules.js';

let panel = null;
let open = false;
let pending = false; // 送了 quickChat、還沒嵌進來

const read = (key) => { try { return localStorage.getItem(key); } catch { return null; } };
const write = (key, v) => { try { if (v == null) localStorage.removeItem(key); else localStorage.setItem(key, v); } catch { /* 存不了就算了 */ } };

/** 頂端列的對話鈕（app.js 插在設定鈕之前） */
export function quickChatButton() {
  const btn = h('button', { type: 'button', id: 'btnQuickChat', class: 'ibtn qc-toggle', title: '臨時對話（不屬於任何專案、不留紀錄）', 'aria-label': '臨時對話', 'aria-pressed': 'false' }, icon('chat'));
  btn.addEventListener('click', () => toggleQuickChat());
  return btn;
}

/** init 與帳戶清單變了（app.js）：協作人與彈出視窗不顯示對話鈕；面板上的帳戶名稱跟著更新 */
export function refreshQuickChat() {
  const btn = document.getElementById('btnQuickChat');
  const hide = isCollaborator() || state.mode === 'popout';
  if (btn) setHidden(btn, hide);
  if (hide && open) closePanel();
  paintAccount();
}

export function toggleQuickChat(force) {
  const want = typeof force === 'boolean' ? force : !open;
  if (want) openPanel(); else closePanel();
}

function usableAccounts() {
  return (state.accounts || []).filter((a) => a && a.status === 'ready');
}

function currentAccountId() {
  const cv = ws.dockedConv(QUICK_DOCK);
  return (cv && cv.header && cv.header.accountId) || read(QUICK_ACCOUNT_KEY);
}

function build() {
  const title = h('span', { class: 'qc-title', text: '臨時對話' });
  const btnNew = h('button', { type: 'button', class: 'ibtn sm', title: '清掉，重新開始（這一段不留紀錄）', 'aria-label': '重新開始', dataset: { field: 'quickRestart' } }, icon('refresh'));
  btnNew.addEventListener('click', () => restart(currentAccountId()));
  const btnStop = h('button', { type: 'button', class: 'ibtn sm', title: '停止朗讀', 'aria-label': '停止朗讀', dataset: { field: 'quickStopSpeak' } }, icon('stop'));
  btnStop.addEventListener('click', () => stopSpeaking());
  const btnClose = h('button', { type: 'button', class: 'ibtn sm', title: '關掉（刪掉這段對話，不留紀錄）', 'aria-label': '關閉', dataset: { field: 'quickClose' } }, icon('x'));
  btnClose.addEventListener('click', () => closePanel());
  const head = h('div', { class: 'qc-head' }, icon('chat', 'qc-ic'), title, h('span', { class: 'grow' }), btnNew, btnStop, btnClose);
  const body = h('div', { class: 'qc-body' });
  const empty = h('div', { class: 'qc-empty', hidden: true, dataset: { field: 'quickEmpty' } });
  const el = h('div', { class: 'qc', hidden: true, role: 'dialog', 'aria-label': '臨時對話', dataset: { field: 'quickChat' } }, head, body, empty);
  document.body.append(el);
  ws.registerDock(QUICK_DOCK, body, onDock);
  dragHead(el, head);
  const ro = new ResizeObserver(() => { if (open && !el.classList.contains('full')) saveRect(el); });
  ro.observe(el);
  window.addEventListener('resize', () => { if (open) place(el); });
  return { el, body, empty };
}

function onDock(id, reason) {
  if (reason === 'mounted') pending = false;
  if (reason === 'released' && open && !pending) {
    // 宿主關掉或刪除了（例如在別的視窗結束）：面板開著就開一個新的
    request(currentAccountId());
  }
  paintAccount();
}

function paintAccount() {
  if (!panel) return;
  const none = !usableAccounts().length;
  setHidden(panel.empty, !none);
  if (none) panel.empty.textContent = '沒有可用的帳戶：請先在儀表板登入一個帳戶。';
}

function openPanel() {
  if (!panel) panel = build();
  open = true;
  setHidden(panel.el, false);
  place(panel.el);
  const btn = document.getElementById('btnQuickChat');
  if (btn) { btn.classList.add('on'); btn.setAttribute('aria-pressed', 'true'); }
  ws.setDock(QUICK_DOCK, true, true);
  const cv = ws.dockedConv(QUICK_DOCK);
  if (cv) requestAnimationFrame(() => cv.focusInput());
  else if (!ws.dockExpecting(QUICK_DOCK)) request(read(QUICK_ACCOUNT_KEY));
  paintAccount();
}

function closePanel() {
  if (!panel) return;
  open = false;
  pending = false;
  stopSpeaking();
  end();
  ws.setDock(QUICK_DOCK, false, false);
  setHidden(panel.el, true);
  const btn = document.getElementById('btnQuickChat');
  if (btn) { btn.classList.remove('on'); btn.setAttribute('aria-pressed', 'false'); }
}

/**
 * 結束面板上的這一個（面板放開它）：桌面送 quickChatEnd（宿主刪掉對話與 CLI 的逐字稿，草稿直接丟掉）；
 * 瀏覽器版還沒送出的伺服器草稿送 close（伺服器丟掉），已經建立的送 delete（那台把臨時對話直接刪掉、不進回收區）
 */
function end() {
  const cv = ws.dockedConv(QUICK_DOCK);
  const id = cv ? cv.id : ws.dockExpecting(QUICK_DOCK);
  if (!id) return;
  // 下次照這次用的帳戶開（在對話標頭的帳戶晶片換過也算）
  if (cv && cv.header && cv.header.accountId) write(QUICK_ACCOUNT_KEY, cv.header.accountId);
  pending = true; // 放開時的 released 不要自己開新的
  ws.releaseDock(QUICK_DOCK, false);
  pending = false;
  if (!isHub) post({ cmd: 'quickChatEnd', conversationId: id });
  else if (isDraftId(id)) post({ cmd: 'close', conversationId: id });
  else post({ cmd: 'delete', conversationId: id });
}

function restart(accountId) {
  if (!usableAccounts().length) { paintAccount(); return; }
  stopSpeaking();
  end();
  request(accountId);
}

function request(accountId) {
  if (!open || pending || !usableAccounts().length) { paintAccount(); return; }
  pending = true;
  if (accountId) write(QUICK_ACCOUNT_KEY, accountId);
  post({ cmd: 'quickChat', paneId: QUICK_DOCK, accountId: accountId || null });
}

// ───── 位置與大小 ─────

function topOf() {
  const bar = document.querySelector('.topbar');
  return bar ? Math.round(bar.getBoundingClientRect().bottom) : 48;
}

function place(el) {
  let saved = null;
  try { saved = JSON.parse(read(QUICK_RECT_KEY) || 'null'); } catch { saved = null; }
  const r = panelRect(saved, window.innerWidth, window.innerHeight, topOf());
  el.classList.toggle('full', r.full);
  el.style.left = r.x + 'px';
  el.style.top = r.y + 'px';
  el.style.width = r.w + 'px';
  el.style.height = r.h + 'px';
}

function saveRect(el) {
  const b = el.getBoundingClientRect();
  if (b.width < QUICK_MIN_W / 2 || b.height < QUICK_MIN_H / 2) return;
  write(QUICK_RECT_KEY, JSON.stringify({ x: Math.round(b.left), y: Math.round(b.top), w: Math.round(b.width), h: Math.round(b.height) }));
}

function dragHead(el, head) {
  head.addEventListener('pointerdown', (e) => {
    if (e.button !== 0 || el.classList.contains('full') || e.target.closest('button')) return;
    const b = el.getBoundingClientRect();
    const dx = e.clientX - b.left, dy = e.clientY - b.top;
    head.setPointerCapture(e.pointerId);
    const move = (ev) => {
      const r = panelRect({ x: ev.clientX - dx, y: ev.clientY - dy, w: b.width, h: b.height }, window.innerWidth, window.innerHeight, topOf());
      el.style.left = r.x + 'px';
      el.style.top = r.y + 'px';
    };
    const up = () => {
      head.removeEventListener('pointermove', move);
      head.removeEventListener('pointerup', up);
      head.removeEventListener('pointercancel', up);
      saveRect(el);
    };
    head.addEventListener('pointermove', move);
    head.addEventListener('pointerup', up);
    head.addEventListener('pointercancel', up);
  });
}

// ───── 朗讀 ─────

/** 回覆的「朗讀」鈕（items.js 經 ConvView 的 ctx 呼叫）：桌面交給宿主念，瀏覽器版自己念畫面上的文字 */
export function speakItem(conversationId, itemId, mdEl) {
  if (!isHub) {
    post({ cmd: 'speakItem', conversationId, itemId });
    return;
  }
  const text = textOf(mdEl);
  if (!text) { toast('這則沒有可以朗讀的文字'); return; }
  if (!readAloud(speechChunks(text))) toast('這個瀏覽器不能朗讀', 'error');
}

function stopSpeaking() {
  if (isHub) stopReadAloud(); else post({ cmd: 'speakStop' });
}

/** 畫面上的回覆 → 念的文字：程式碼區塊換成一句說明（同 C# 的 QuickChatRules.SpeechText） */
function textOf(mdEl) {
  if (!mdEl) return '';
  const copy = mdEl.cloneNode(true);
  for (const pre of copy.querySelectorAll('pre, .code-wrap')) pre.replaceWith(document.createTextNode('\n' + CODE_BLOCK_SPOKEN + '\n'));
  for (const junk of copy.querySelectorAll('button, svg, .code-copy')) junk.remove();
  for (const block of copy.querySelectorAll('p, li, h1, h2, h3, h4, h5, h6, tr, blockquote')) block.append(document.createTextNode('\n'));
  return (copy.textContent || '').replace(/[ \t]+/g, ' ').replace(/\n\s*\n+/g, '\n').trim();
}
