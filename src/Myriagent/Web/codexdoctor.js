import { h } from './util.js';
import { post, onHostMessage, isHub, accountOf } from './store.js';
import { openDialog, spinner } from './ui.js';

const pending = new Map();
let serial = 0;
const STATUS = { ok: '正常', warning: '需要注意', warn: '需要注意', error: '異常', failed: '異常', skipped: '略過' };
const TITLES = { config: '設定', network: '網路', auth: '登入', install: '安裝', installation: '安裝', runtime: '執行環境', git: 'Git', terminal: '終端機', app_server: 'App Server', threads: '對話紀錄' };

onHostMessage(m => {
  if (!m || m.type !== 'codexDoctor') return;
  const done = pending.get(m.requestId);
  if (done) { pending.delete(m.requestId); done(m); }
});

export function createDoctorStrip(card) {
  const button = h('button', { type: 'button', class: 'acard-mcp codex-doctor', text: 'Codex 診斷', hidden: true });
  button.addEventListener('click', () => {
    const account = accountOf(card.dataset.key);
    if (account && account.provider === 'Codex' && !account.remoteOnly && !isHub) openDoctor(account);
  });
  return button;
}

export function updateDoctorStrip(button, account) {
  button.hidden = isHub || account.provider !== 'Codex' || account.remoteOnly === true;
}

function openDoctor(account) {
  const body = h('div', { class: 'codex-panel' });
  const note = h('p', { class: 'codex-note', text: '檢查這個帳戶的 Codex 設定、登入與連線。結果只顯示在這台電腦；網路檢查會連線到服務端。' });
  const result = h('div', { class: 'codex-doctor-result', role: 'status' });
  const all = h('button', { type: 'button', class: 'btn primary', text: '完整診斷' });
  const network = h('button', { type: 'button', class: 'btn', text: '只檢查連線' });
  const close = h('button', { type: 'button', class: 'btn ghost', text: '關閉' });
  body.append(note, result);
  let closed = false;
  let requestId;
  let timer;
  const dialog = openDialog({ title: account.label + ' · Codex 診斷', body, footer: [all, network, h('span', { class: 'grow' }), close], width: 720,
    onClose: () => { closed = true; clearTimeout(timer); pending.delete(requestId); } });
  close.addEventListener('click', () => dialog.close());
  function run(networkOnly) {
    all.disabled = network.disabled = true;
    result.replaceChildren(h('p', {}, spinner(), h('span', { text: '正在檢查，最多約 90 秒…' })));
    requestId = 'doctor-' + Date.now() + '-' + ++serial;
    const finish = m => {
      clearTimeout(timer);
      if (closed) return;
      all.disabled = network.disabled = false;
      result.replaceChildren();
      if (m.error) { result.append(h('p', { class: 'codex-error', text: m.error })); return; }
      const report = m.result || {};
      result.append(h('p', { text: 'Codex ' + (report.version || '') + ' · ' + (STATUS[report.status] || report.status || '已完成') }));
      for (const check of report.checks || []) {
        const label = TITLES[(check.id || '').split('.')[0]] || check.id;
        const details = h('details', { class: 'codex-doctor-check', open: check.status !== 'ok' },
          h('summary', { text: label + ' · ' + (STATUS[check.status] || check.status || '') + '（' + check.id + '）' }),
          h('p', { text: check.summary || '' }));
        if (check.details) details.append(h('pre', { class: 'codex-doctor-detail', text: JSON.stringify(check.details, null, 2) }));
        if (check.remediation) details.append(h('p', { text: typeof check.remediation === 'string' ? check.remediation : JSON.stringify(check.remediation) }));
        result.append(details);
      }
    };
    pending.set(requestId, finish);
    timer = setTimeout(() => { pending.delete(requestId); finish({ error: '診斷尚未回覆，請稍後重試' }); }, 105000);
    post({ cmd: 'codexDoctor', accountId: account.id, requestId, networkOnly });
  }
  all.addEventListener('click', () => run(false));
  network.addEventListener('click', () => run(true));
}
