// 要你回來看的對話（完成、失敗、代理提問、要你核准、計畫等確認）：分頁、側欄那一列與對話標頭一直閃，閃到使用者點了那個對話為止
// （2026-10-25 使用者要求；以前是語音通知開始念時閃三下）。開始閃的是桌面宿主的 flash、瀏覽器版的 speak（web-protocol），不管語音通知開不開。
// 「點了」＝在那個對話的分頁、側欄那一列或對話裡按下滑鼠（觸控、筆），在對話裡按鍵，或點了系統通知切到它（workspace.focusConversation）。
// 狀態只在這個頁面的記憶體（重新載入就不閃了）；同一個來源的其他視窗（桌面的彈出視窗、瀏覽器的其他分頁）以 BroadcastChannel 互相通知「點過了」，
// 一個視窗點了，其他視窗的也停。閃的樣子在 app.css（分頁、側欄）與 chat.css（標頭），class 是 attn-flash
const CHANNEL = 'myriagent.attn-flash';

const flashing = new Set(); // 正在閃的對話 id
let channel = null;
try {
  channel = typeof BroadcastChannel === 'function' ? new BroadcastChannel(CHANNEL) : null;
} catch {
  channel = null; // 不安全的來源之類：只停這個視窗
}
if (channel) {
  channel.onmessage = (e) => {
    const id = e && e.data ? e.data.stop : null;
    if (typeof id === 'string') stop(id, false);
  };
}

/** 這個對話正在閃嗎（分頁、側欄的列、對話畫面新建出來時照它加上 attn-flash） */
export function isFlashing(id) {
  return flashing.has(id);
}

/** 開始閃（已經在閃就照舊，不從頭來） */
export function startFlash(id) {
  if (typeof id !== 'string' || !id) return;
  flashing.add(id);
  mark(id, true);
}

/** 使用者點了這個對話：停下，也告訴其他視窗 */
export function stopFlash(id) {
  stop(id, true);
}

/** 對話被刪除或關掉（conversationClosed）：只停這個視窗的（其他視窗也會收到同一則） */
export function forgetFlash(id) {
  stop(id, false);
}

function stop(id, broadcast) {
  if (!flashing.delete(id)) return;
  mark(id, false);
  if (broadcast && channel) {
    try {
      channel.postMessage({ stop: id });
    } catch {
      // 頻道關了：只停這個視窗
    }
  }
}

/** 畫面上這個對話的分頁、側欄那一列與對話標頭 */
function mark(id, on) {
  for (const t of document.querySelectorAll('.tab')) if (t.dataset.key === id) t.classList.toggle('attn-flash', on);
  for (const r of document.querySelectorAll('.crow')) if (r.dataset.key === 'c:' + id) r.classList.toggle('attn-flash', on);
  for (const v of document.querySelectorAll('.conv')) {
    if (v.dataset.conv !== id) continue;
    const head = v.querySelector(':scope > .ch');
    if (head) head.classList.toggle('attn-flash', on);
  }
}

/** 按下的地方屬於哪個對話：分頁、側欄的列或對話畫面；都不是回 null */
function conversationAt(target) {
  const el = target instanceof Element ? target.closest('.tab, .crow, .conv') : null;
  if (!el) return null;
  if (el.classList.contains('conv')) return el.dataset.conv || null;
  const key = el.dataset.key || '';
  if (el.classList.contains('crow')) return key.startsWith('c:') ? key.slice(2) : null;
  return key || null;
}

if (typeof document !== 'undefined') {
  document.addEventListener('pointerdown', (e) => {
    if (!flashing.size) return;
    const id = conversationAt(e.target);
    if (id) stopFlash(id);
  }, { capture: true, passive: true });
  // 鍵盤：在對話裡打字（回答問卷、輸入框），或在分頁、側欄的列上按 Enter／空白鍵開啟
  document.addEventListener('keydown', (e) => {
    if (!flashing.size || !(e.target instanceof Element)) return;
    const inConv = !!e.target.closest('.conv');
    if (!inConv && e.key !== 'Enter' && e.key !== ' ') return;
    const id = conversationAt(e.target);
    if (id) stopFlash(id);
  }, { capture: true });
}
