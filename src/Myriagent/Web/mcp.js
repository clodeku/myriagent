// 儀表板的 MCP（只有桌面；web-protocol 的 mcp／mcpCheck／mcpAction，2026-10-24）：Claude 帳戶卡上的「MCP」一行（點了開明細），
// 明細列出這個帳戶的 MCP 伺服器（使用者層、外掛、claude.ai 連接器）與狀態，需要授權的在瀏覽器授權、連不上的重新連線。
// MCP 的設定與授權的權杖都在帳戶的設定資料夾，所以以帳戶為單位；所有狀態由宿主查好，這裡只負責顯示
import { h, fmtRelative, fmtDateTime, replayClass } from './util.js';
import { icon } from './icons.js';
import { post, state, accountOf, isHub } from './store.js';
import { openDialog, spinner } from './ui.js';
import { accountMcpOf, mcpStrip, mcpRows, LEVEL_LABEL, mcpLeds, mcpNewlyConnected } from './mcprules.js';

/** 開著的明細對話框：{ accountId, render, dialog }（mcp 訊息到了重畫） */
let openDlg = null;

const LEVEL_ICON = { ok: 'check', info: 'info', warn: 'warning', bad: 'error' };
const ACTION_ICON = { authenticate: 'login', reconnect: 'refresh' };

/** 請宿主檢查（切到儀表板時送：5 分鐘內查過就只重送；force＝「重新檢查」） */
export function requestMcp(force) {
  if (isHub || state.mode === 'popout') return;
  post(force ? { cmd: 'mcpCheck', force: true } : { cmd: 'mcpCheck' });
}

/** 這個帳戶的 mcp 項目：只有桌面這台的 Claude 帳戶（瀏覽器版、只在其他電腦登入的帳戶沒有） */
function entryOf(a) {
  return !isHub && a && a.remoteOnly !== true && ['Claude', 'Codex'].includes(a.provider) ? accountMcpOf(state.mcp, a.id) : null;
}

// ───── 帳戶卡的一行 ─────
/** 帳戶卡上的「MCP」按鈕（dashboard.js 的 createCard 建一次；樣式同健康度那一行） */
export function createMcpStrip(card) {
  const btn = h('button', { type: 'button', class: 'acard-mcp', hidden: true });
  btn.addEventListener('click', () => { const a = accountOf(card.dataset.key); if (a) openMcpDialog(a); });
  return btn;
}

/** 依目前的 state.mcp 更新卡片上的一行；這個帳戶沒有項目（不是 Claude、瀏覽器版、還沒查）就藏起來 */
export function updateMcpStrip(btn, a) {
  const entry = entryOf(a);
  const v = mcpStrip(entry);
  // 每個伺服器一顆燈（mcprules.mcpLeds）；從別的狀態變成已連線的那一顆閃一下綠（第一次畫不算）
  const leds = v ? mcpLeds(entry) : [];
  const fresh = mcpNewlyConnected(btn._prevSt || null, leds);
  btn._prevSt = new Map(leds.map((l) => [l.name, l.status]));
  const sig = v ? JSON.stringify([v.level, v.text, v.busy, leds.map((l) => l.name + ':' + l.status)]) : '';
  if (btn._sig === sig) return;
  btn._sig = sig;
  btn.textContent = '';
  btn.hidden = !v;
  if (!v) return;
  btn.dataset.level = v.level;
  btn.title = 'MCP 伺服器：' + v.text + '\n點一下看明細、授權或重新連線';
  const badge = LEVEL_LABEL[v.level];
  btn.append(...[icon('plug', 'ah-ic'), h('span', { class: 'ah-label', text: 'MCP' }),
    badge ? h('span', { class: 'ah-badge', dataset: { level: v.level } }, icon(LEVEL_ICON[v.level], 'ah-badge-ic'), h('span', { text: badge })) : null,
    leds.length ? h('span', { class: 'mcp-leds', 'aria-hidden': 'true' }, ...leds.map((l) => h('i', { dataset: { st: l.status, name: l.name }, title: l.name + '：' + l.text }))) : null,
    h('span', { class: 'ah-text', text: v.text }),
    v.busy ? spinner('ah-spin') : null,
    icon('chevRight', 'ah-chev')].filter(Boolean));
  if (fresh.size) for (const i of btn.querySelectorAll('.mcp-leds > i')) if (fresh.has(i.dataset.name)) replayClass(i, 'fx-led-ok', 900);
}

// ───── 明細對話框 ─────
export function openMcpDialog(a) {
  const body = h('div', { class: 'hd mcpd' });
  const refresh = h('button', { type: 'button', class: 'btn sm ghost' });
  refresh.addEventListener('click', () => requestMcp(true));
  const close = h('button', { type: 'button', class: 'btn', text: '關閉' });
  const d = openDialog({
    title: '「' + a.label + '」的 MCP 伺服器', body, footer: [refresh, h('span', { class: 'grow' }), close], width: 600, className: 'dlg-health dlg-mcp',
    onClose: () => { if (openDlg && openDlg.dialog === d) openDlg = null; },
  });
  close.addEventListener('click', () => d.close());
  const render = () => {
    const entry = entryOf(accountOf(a.id) || a);
    const v = mcpStrip(entry);
    const checking = !!(entry && entry.checking);
    refresh.disabled = checking || !!(entry && entry.authorizing) || !!(entry && entry.safeMode);
    refresh.textContent = '';
    refresh.append(checking ? spinner() : icon('refresh'), h('span', { text: checking ? '檢查中…' : '重新檢查' }));
    body.textContent = '';
    if (!v) {
      body.append(h('p', { class: 'hd-empty', text: '還沒有這個帳戶的 MCP 狀態。' }));
      return;
    }
    const at = entry.checkedAt;
    body.append(h('div', { class: 'hd-top', dataset: { level: v.level } },
      LEVEL_LABEL[v.level] ? h('span', { class: 'hd-badge', dataset: { level: v.level } }, icon(LEVEL_ICON[v.level], 'hd-badge-ic'), h('span', { text: LEVEL_LABEL[v.level] })) : null,
      h('div', { class: 'hd-top-body' },
        h('div', { class: 'hd-summary', text: v.text }),
        h('div', { class: 'hd-meta' },
          at ? h('span', { title: fmtDateTime(at), text: '檢查於 ' + fmtRelative(at) }) : null,
          entry.servers && entry.error ? h('span', { text: '上次檢查失敗：' + entry.error }) : null))));
    for (const r of mcpRows(entry)) body.append(serverBlock(a.id, r));
    body.append(h('p', { class: 'hd-foot', text: (a.provider === 'Codex' ? '這裡列這個 Codex 帳戶設定的 MCP 伺服器與外掛；專案層設定以實際工作資料夾的對話為準。' : '這裡列這個帳戶的使用者層、外掛與 claude.ai 連接器的伺服器；專案資料夾的 .mcp.json 只在那個資料夾開的對話載入。')
      + '授權存在帳戶裡，授權後這個帳戶開著的對話會自動重新連線。' + (a.provider === 'Codex' ? '重新連線會重載這個帳戶的 MCP 設定；停用請修改 Codex 設定。' : '停用要在對話的工作資料夾設定（CLI 的停用是逐資料夾的）。') }));
  };
  openDlg = { accountId: a.id, render, dialog: d };
  render();
  requestMcp(false);
}

/** 明細的一個伺服器：狀態圖示、名稱、狀態與來源、錯誤原因或工具數、動作按鈕 */
function serverBlock(accountId, r) {
  const buttons = r.actions.map((x) => {
    const btn = h('button', { type: 'button', class: 'btn sm', title: x.title, disabled: x.disabled, dataset: { action: x.action } },
      icon(ACTION_ICON[x.action] || 'refresh'), h('span', { text: x.label }));
    btn.addEventListener('click', () => post({ cmd: 'mcpAction', accountId, server: r.name, action: x.action }));
    return btn;
  });
  return h('section', { class: 'hd-item', dataset: { level: r.level, server: r.name } },
    h('div', { class: 'hd-item-head' },
      icon(LEVEL_ICON[r.level] || 'info', 'hd-item-ic'),
      h('h3', { class: 'hd-item-title mcpd-name', text: r.name, title: r.name }),
      h('span', { class: 'hd-item-value', text: r.text + (r.source ? '・' + r.source : '') })),
    r.detail.length ? h('div', { class: 'hd-item-detail' }, r.detail.map((t) => h('p', { text: t }))) : null,
    buttons.length ? h('div', { class: 'hd-item-actions' }, buttons) : null);
}

/** mcp 訊息到了：開著的明細對話框重畫 */
export function refreshMcpDialog() {
  if (openDlg) openDlg.render();
}
