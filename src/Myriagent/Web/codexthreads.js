// Codex 歷史連結只在來源桌面對話查詢。逐頁顯示純文字，不建立可送出訊息的會話。
import { h, fmtDateTime } from './util.js';
import { isHub, nextRequestId, post } from './store.js';
import { openDialog, toast } from './ui.js';

const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}';
const marker = new RegExp('^#codex-thread-(' + uuid + ')$', 'i');
const plain = new RegExp('(?<![\\w/:])codex://threads/(' + uuid + ')(?=$|[\\s，。！？、；：「」『』（）)\\]]|[.,!](?=\\s|$))', 'gi');
let active = null;

export function decorateThreadLinks(root) {
  if (!root.textContent.includes('codex://threads/')) return;
  const walk = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const texts = [];
  for (let t = walk.nextNode(); t; t = walk.nextNode())
    if (t.data.includes('codex://threads/') && !t.parentElement.closest('a, pre code')) texts.push(t);
  for (const t of texts) {
    const parts = [];
    let at = 0;
    for (const m of t.data.matchAll(plain)) {
      parts.push(t.data.slice(at, m.index), h('a', { href: '#codex-thread-' + m[1], text: m[0] }));
      at = m.index + m[0].length;
    }
    if (at) t.replaceWith(...parts, t.data.slice(at));
  }
}

export function openThreadLink(a) {
  const id = marker.exec(a.getAttribute('href') || '')?.[1];
  if (!id) return false;
  const source = a.closest('.conv')?.dataset.conv || a.closest('.codex-thread-history')?.dataset.source;
  if (isHub || !source || a.closest('.conv')?.dataset.codexHistory === 'unavailable') {
    toast('請在來源電腦的 Codex 對話開啟連結');
    return true;
  }
  active?.dialog.close();
  const body = h('div', { class: 'codex-thread-body' });
  const dialog = openDialog({ title: 'Codex 對話歷史（唯讀）', body, width: 680, className: 'codex-thread-history',
    onClose: () => { active = null; } });
  dialog.card.dataset.source = source;
  const state = { dialog, body, source, id, turn: null, requestId: null, list: null, more: null };
  active = state;
  showPage(state, null);
  return true;
}

function showPage(s, turn) {
  s.turn = turn;
  const back = h('button', { type: 'button', class: 'btn sm', text: '返回回合清單', onClick: () => showPage(s, null) });
  s.list = h('div', { class: 'codex-thread-rows' });
  s.more = h('button', { type: 'button', class: 'btn sm' });
  s.body.replaceChildren(...(turn ? [back] : []), s.list, s.more);
  loadPage(s, null);
}

function loadPage(s, cursor) {
  s.requestId = nextRequestId('codexThread');
  s.cursor = cursor;
  s.more.disabled = true;
  s.more.hidden = false;
  s.more.textContent = '載入中…';
  post({ cmd: 'openCodexThread', conversationId: s.source, threadId: s.id, turnId: s.turn, cursor, requestId: s.requestId });
}

const kinds = { User: '使用者', Assistant: '代理', Thinking: '思考', Tool: '工具' };
const statuses = { completed: '已完成', inProgress: '執行中', failed: '失敗', interrupted: '已中斷' };

export function onThreadPage(msg) {
  const s = active;
  if (!s || msg.requestId !== s.requestId) return;
  s.requestId = null;
  if (msg.opened) { s.dialog.close(); return; }
  s.more.disabled = false;
  if (msg.error || !msg.page) {
    s.more.textContent = '重新載入';
    s.more.onclick = () => loadPage(s, s.cursor);
    toast(msg.error || '無法讀取對話歷史', 'error');
    return;
  }
  const page = msg.page;
  s.dialog.card.querySelector('.dlg-title').textContent = (page.title || 'Codex 對話歷史') + '（唯讀）';
  for (const row of page.rows || []) {
    if (!s.turn) {
      s.list.append(h('button', { type: 'button', class: 'btn codex-turn-row', onClick: () => showPage(s, row.id) },
        h('strong', { text: row.title || '查看回合' }), h('small', { text: [row.time ? fmtDateTime(row.time * 1000) : '', statuses[row.status]].filter(Boolean).join(' · ') })));
    } else {
      const entry = h('section', {}, h('strong', { text: row.title || kinds[row.kind] || '項目' }));
      for (const value of [row.text, row.detail, row.output]) if (value) entry.append(h('pre', { text: value }));
      decorateThreadLinks(entry);
      s.list.append(entry);
    }
  }
  if (!s.list.childElementCount && !page.nextCursor) s.list.textContent = s.turn ? '這個回合沒有可顯示的項目' : '這個對話還沒有回合';
  s.more.hidden = !page.nextCursor;
  s.more.textContent = s.turn ? '載入更多項目' : '載入更早的回合';
  s.more.onclick = () => loadPage(s, page.nextCursor);
}
