// 背景工作的「詳細」（2026-10-25；web-protocol 的 taskDetail）：等背景工作回報那一行的「詳細」開的對話框——
// 這個背景工作在做什麼（宿主依指令寫的白話說明、代理自己寫的說明）、指令或交代的內容、目前的輸出，以及停止鈕（taskStop）。
// 開著時每 3 秒問一次（輸出、還在不在），輸出貼在最底下（捲到底時跟著新的輸出走）；結束了（含按了停止）之後最後的內容留著
import { h } from './util.js';
import { icon } from './icons.js';
import { post, onHostMessage, nextRequestId } from './store.js';
import { openDialog } from './ui.js';
import { taskDetailView, taskStopAction, TASK_DETAIL_POLL_MS } from './liverules.js';

let current = null;

onHostMessage((msg) => {
  if (!msg || msg.type !== 'taskDetail' || !current || msg.requestId !== current.requestId) return;
  current.render(msg);
});

/** 開對話框（同時只開一個）：conversationId、task（Header.background.tasks 的一項，要有 key）、background（整個 Header.background，停止全部時寫數量） */
export function openTaskDetail(conversationId, task, background) {
  if (!task || !task.key) return;
  if (current) current.dlg.close();
  const explain = h('p', { class: 'td-explain', text: '讀取中…' });
  const note = h('p', { class: 'td-note', hidden: true });
  const facts = h('div', { class: 'td-facts' });
  const contentLabel = h('div', { class: 'td-label' });
  const content = h('pre', { class: 'pre td-content' });
  const contentSec = h('div', { class: 'td-sec', hidden: true }, contentLabel, content);
  const meta = h('span', { class: 'td-meta' });
  const pre = h('pre', { class: 'pre out td-out', hidden: true });
  const refresh = h('button', { type: 'button', class: 'btn sm', text: '重新整理' });
  const action = taskStopAction(task, background);
  const stop = h('button', { type: 'button', class: 'btn sm td-stop', title: action.title }, icon('stop'), h('span', { text: action.label }));
  const dlg = openDialog({
    title: '背景工作：' + (task.title || task.tool || '背景工作'),
    className: 'dlg-task-detail', width: 760,
    body: h('div', { class: 'td-body' },
      h('div', { class: 'td-sec' }, h('div', { class: 'td-label', text: '在做什麼' }), explain, note, facts),
      contentSec,
      h('div', { class: 'td-sec' }, h('div', { class: 'td-label' }, h('span', { text: '輸出' }), meta), pre)),
    footer: [stop, refresh],
    onClose: () => { clearTimeout(state.timer); if (current === state) current = null; },
  });
  const state = {
    dlg, requestId: null, timer: 0, ended: false, loaded: false,
    render(msg) {
      const v = taskDetailView(msg, task, Date.now());
      // 結束了（含按了停止）之後問不到：說明、指令與最後一次的輸出都留著，停止鈕跟著停用
      if (!v.ended || !state.loaded) {
        explain.textContent = v.explain || (v.ended ? v.meta : '');
        note.textContent = v.agentNote ? '代理的說明：' + v.agentNote : '';
        note.hidden = !v.agentNote;
        facts.textContent = v.facts;
        contentLabel.textContent = v.contentLabel;
        content.textContent = v.content || '';
        contentSec.hidden = !v.content;
        state.loaded = state.loaded || !v.ended;
      }
      meta.textContent = v.meta;
      meta.dataset.tone = v.ended ? 'bad' : '';
      const atBottom = pre.scrollHeight - pre.scrollTop - pre.clientHeight < 24;
      if (v.hasOutput) pre.textContent = v.text;
      pre.hidden = !(v.hasOutput || (v.ended && pre.textContent));
      if (atBottom) pre.scrollTop = pre.scrollHeight;
      if (v.ended) state.ended = true;
      if (state.ended) stop.disabled = true;
      clearTimeout(state.timer);
      if (!v.ended) state.timer = setTimeout(ask, TASK_DETAIL_POLL_MS);
    },
  };
  function ask() {
    state.requestId = nextRequestId('taskdetail');
    post({ cmd: 'taskDetail', conversationId, key: task.key, requestId: state.requestId });
  }
  refresh.addEventListener('click', ask);
  stop.addEventListener('click', () => {
    // 送出後先停用：停了的話下一次問就是「已經結束了」；停不了（宿主只提示原因）時 5 秒後可以再按
    stop.disabled = true;
    setTimeout(() => { stop.disabled = state.ended; }, 5000);
    post(action.stopId ? { cmd: 'taskStop', conversationId, stopId: action.stopId } : { cmd: 'taskStop', conversationId });
    clearTimeout(state.timer);
    state.timer = setTimeout(ask, 500);
  });
  current = state;
  ask();
}
