// 匯入本機外部 CLI 歷史對話的對話框（桌面專屬）：列出終端機或擴充套件開過的對話，點選即可匯入並原生續接。
import { h, iconButton, fmtRelative, fmtDateTime, replayClass } from './util.js';
import { icon, providerMark } from './icons.js';
import { openDialog, spinner } from './ui.js';
import { post, nextRequestId, emit } from './store.js';
import {
  IMPORT_PROVIDERS,
  normalizeExternalItems,
  countExternalByProvider,
  filterExternalItems,
  formatExternalFolder,
} from './importrules.js';
import { sizeFrac, LIST_ENTRANCE_MAX, LIST_STAGGER_MS } from './fxrules.js';

let activeModal = null;

/** 宿主回傳 externalConversations 時更新開著的匯入對話框 */
export function onExternalConversations(msg) {
  if (!activeModal || !msg) return;
  if (msg.requestId && msg.requestId !== activeModal.requestId) return;
  activeModal.loading = false;
  activeModal.items = normalizeExternalItems(msg.items);
  activeModal.fresh = true; // 這次掃描的結果：清單由上往下錯開進場一次
  activeModal.render();
}

/** 開啟「匯入本機 CLI 歷史對話」對話框 */
export function openImportDialog(ws) {
  if (activeModal) {
    activeModal.refresh();
    return;
  }

  const searchInput = h('input', {
    type: 'search',
    class: 'input imp-search-input',
    placeholder: '搜尋對話標題、工作資料夾或帳戶…',
    autocomplete: 'off',
    spellcheck: 'false',
    'aria-label': '搜尋外部對話',
  });
  const refreshBtn = iconButton('refresh', '重新掃描本機 CLI 歷史對話', () => refresh(), 'sm');
  const pillsBar = h('div', { class: 'imp-pills', role: 'tablist', 'aria-label': '依代理種類篩選' });
  const listEl = h('div', { class: 'imp-list', role: 'list' });
  const statusEl = h('div', { class: 'imp-status', role: 'status' });

  const body = h('div', { class: 'imp-body' },
    h('p', { class: 'imp-hint', text: '自動唯讀掃描這台電腦上 Claude Code、Codex、Antigravity 與 Grok 尚未匯入的歷史對話；匯入後可直接輸入訊息續接原本的工作階段。' }),
    h('div', { class: 'imp-toolbar' },
      h('div', { class: 'imp-search' }, icon('search', 'imp-search-ic'), searchInput),
      refreshBtn),
    pillsBar,
    statusEl,
    listEl);

  const closeBtn = h('button', { type: 'button', class: 'btn', text: '關閉' });

  const modal = {
    requestId: null,
    loading: true,
    provider: 'all',
    query: '',
    items: [],
    refresh,
    render,
  };
  activeModal = modal;

  const dlg = openDialog({
    title: '匯入本機 CLI 歷史對話',
    body,
    footer: [closeBtn],
    className: 'dlg-import',
    width: 640,
    initialFocus: searchInput,
    onClose: () => {
      if (activeModal === modal) activeModal = null;
    },
  });
  closeBtn.addEventListener('click', () => dlg.close());

  const pillButtons = new Map();
  for (const p of IMPORT_PROVIDERS) {
    const countSpan = h('span', { class: 'imp-pill-count', text: '0' });
    const btn = h('button', {
      type: 'button',
      class: 'imp-pill' + (p.key === 'all' ? ' on' : ''),
      role: 'tab',
      'aria-selected': String(p.key === 'all'),
      dataset: { provider: p.key },
    }, h('span', { text: p.label }), countSpan);
    btn.addEventListener('click', () => {
      modal.provider = p.key;
      render();
    });
    pillButtons.set(p.key, { btn, countSpan });
    pillsBar.append(btn);
  }

  searchInput.addEventListener('input', () => {
    modal.query = searchInput.value;
    render();
  });

  function refresh() {
    modal.loading = true;
    modal.requestId = nextRequestId('ext');
    render();
    post({ cmd: 'listExternalConversations', requestId: modal.requestId });
  }

  function pickItem(item) {
    const focused = ws && typeof ws.focusedPane === 'function' ? ws.focusedPane() : null;
    post({
      cmd: 'importExternalConversation',
      sourceKey: item.sourceKey,
      paneId: focused ? focused.id : null,
    });
    emit('sidebar-picked');
    dlg.close();
  }

  function render() {
    refreshBtn.disabled = modal.loading;
    const counts = countExternalByProvider(modal.items);
    for (const [key, entry] of pillButtons) {
      const on = modal.provider === key;
      entry.btn.classList.toggle('on', on);
      entry.btn.setAttribute('aria-selected', String(on));
      entry.countSpan.textContent = String(counts[key] || 0);
    }

    statusEl.textContent = '';
    listEl.textContent = '';

    if (modal.loading) {
      statusEl.hidden = false;
      listEl.hidden = true;
      // 真的在掃描時才有雷達掃光（fx.css 的 .imp-radar；只動 transform 的狀態指示）
      statusEl.append(h('span', { class: 'imp-radar', 'aria-hidden': 'true' }, h('i')), spinner('imp-spin'), h('span', { text: '正在掃描本機 CLI 對話紀錄…' }));
      return;
    }

    const filtered = filterExternalItems(modal.items, modal.provider, modal.query);
    if (!filtered.length) {
      statusEl.hidden = false;
      listEl.hidden = true;
      const emptyMsg = modal.items.length === 0
        ? '目前沒有找到尚未匯入的本機外部 CLI 對話紀錄'
        : '找不到符合篩選條件的外部對話';
      statusEl.append(h('span', { text: emptyMsg }));
      return;
    }

    statusEl.hidden = true;
    listEl.hidden = false;
    // 訊息數比例條（相對清單裡最多的那一個，fxrules.sizeFrac）；剛掃描完的結果錯開進場（前 LIST_ENTRANCE_MAX 列）
    const maxCount = filtered.reduce((m, it) => (Number.isFinite(it.messageCount) && it.messageCount > m ? it.messageCount : m), 0);
    const enter = modal.fresh ? Math.min(filtered.length, LIST_ENTRANCE_MAX) : 0;
    modal.fresh = false;
    let idx = 0;
    for (const it of filtered) {
      const folderShort = formatExternalFolder(it.workingDirectory);
      const timeStr = it.updatedAt ? fmtRelative(it.updatedAt) : '';
      const fullTime = it.updatedAt ? fmtDateTime(it.updatedAt) : '';
      const row = h('button', {
        type: 'button',
        class: 'imp-row',
        role: 'listitem',
        title: [
          it.title,
          '帳戶：' + it.accountLabel,
          it.workingDirectory ? '工作資料夾：' + it.workingDirectory : '預設工作區',
          fullTime ? '最後更新：' + fullTime : '',
          'Session ID：' + it.sessionId,
        ].filter(Boolean).join('\n'),
      },
      providerMark(it.provider, 'imp-mark'),
      h('div', { class: 'imp-main' },
        h('div', { class: 'imp-top' },
          h('span', { class: 'imp-title', text: it.title }),
          timeStr ? h('span', { class: 'imp-time', text: timeStr }) : null),
        h('div', { class: 'imp-meta' },
          h('span', { class: 'imp-acct', text: it.accountLabel }),
          h('span', { class: 'imp-folder' }, icon('folder', 'imp-meta-ic'), h('span', { text: folderShort })),
          h('span', { class: 'imp-count', text: it.messageCount + ' 則訊息' }))));
      const frac = sizeFrac(it.messageCount, maxCount);
      if (frac !== null) row.append(h('span', { class: 'imp-bar', 'aria-hidden': 'true' }, h('i', { style: { transform: 'scaleX(' + frac.toFixed(3) + ')' } })));
      if (idx < enter) { row.style.setProperty('--fx-i', String(idx)); replayClass(row, 'fx-row-in', 260 + idx * LIST_STAGGER_MS); }
      idx++;
      row.addEventListener('click', () => pickItem(it));
      listEl.append(row);
    }
  }

  refresh();
}
