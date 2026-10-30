// 回溯（2026-10-25；web-protocol 的 rewindPreview、rewind、rewound）：使用者訊息的「回溯」先問宿主能做什麼
// （對話回不回得去、檔案會改哪些），對話框裡勾選回溯對話、還原檔案，再送 rewind
import { h } from './util.js';
import { post, onHostMessage, nextRequestId } from './store.js';
import { openDialog, toast, spinner } from './ui.js';
import { rewindPlan } from './claudeoptrules.js';

let pending = null;

onHostMessage((msg) => {
  if (!msg || msg.type !== 'rewindPreview' || !pending || msg.requestId !== pending.requestId) return;
  const p = pending;
  pending = null;
  p.show(msg);
});

/** 開回溯的對話框：itemId 是使用者訊息，preview 是那則的一行摘要；onClose 在對話框關掉時呼叫（對話欄的回溯預覽收起來） */
export function openRewind(conversationId, itemId, preview, onClose) {
  const requestId = nextRequestId('rewind');
  const body = h('div', { class: 'rewind-body' }, h('div', { class: 'rewind-loading' }, spinner(), h('span', { text: '正在檢查能回溯什麼…' })));
  const go = h('button', { type: 'button', class: 'btn primary', text: '回溯', disabled: true });
  const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
  const dlg = openDialog({
    title: '回溯到這則訊息之前', className: 'dlg-rewind', width: 560,
    body, footer: [cancel, go],
    onClose: () => {
      if (pending && pending.requestId === requestId) pending = null;
      if (onClose) onClose();
    },
  });
  cancel.addEventListener('click', () => dlg.close());
  pending = {
    requestId,
    show(msg) {
      const plan = rewindPlan(msg);
      body.textContent = '';
      if (plan.error) {
        body.append(h('div', { class: 'rewind-note bad', text: plan.error }));
        return;
      }
      body.append(h('div', { class: 'rewind-target', text: '「' + (preview || '這則訊息') + '」' }));
      const conv = option('回溯對話', plan.conversationNote, plan.canConversation, plan.canConversation);
      const files = option('還原檔案', plan.filesNote, plan.canFiles, plan.canFiles && plan.fileCount > 0);
      body.append(conv.row, files.row);
      if (plan.fileList.length) {
        body.append(h('ul', { class: 'rewind-files' }, ...plan.fileList.map((f) => h('li', { text: f })),
          plan.fileCount > plan.fileList.length ? h('li', { class: 'more', text: '另外 ' + (plan.fileCount - plan.fileList.length) + ' 個' }) : null));
      }
      body.append(h('div', { class: 'rewind-note', text: '以指令（Bash、PowerShell）改的檔案、在 app 外面改的檔案不會還原。' }));
      const sync = () => { go.disabled = !conv.input.checked && !files.input.checked; };
      conv.input.addEventListener('change', sync);
      files.input.addEventListener('change', sync);
      sync();
      go.addEventListener('click', () => {
        post({ cmd: 'rewind', conversationId, itemId, conversation: conv.input.checked, files: files.input.checked });
        dlg.close();
        toast('正在回溯…');
      });
    },
  };
  post({ cmd: 'rewindPreview', conversationId, itemId, requestId });
}

function option(label, note, enabled, checked) {
  const input = h('input', { type: 'checkbox', checked: !!checked, disabled: !enabled });
  const row = h('label', { class: 'rewind-opt' + (enabled ? '' : ' off') }, input,
    h('span', { class: 'rewind-opt-text' }, h('span', { class: 'rewind-opt-label', text: label }), h('span', { class: 'rewind-opt-note', text: note })));
  return { row, input };
}
