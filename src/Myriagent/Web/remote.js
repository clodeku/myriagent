// 同步畫面（WP3.5）：儀表板的「帳戶 × 電腦」矩陣、「電腦」（改名、撤銷、移除已撤銷的）、「各電腦 CLI」、帳戶卡上的裝置小點、從目錄刪除帳戶。
// 判斷都在 remoterules.js（純函式）；這裡只把結果畫成 DOM。帳戶名、email、statusText、裝置名都來自其他電腦，一律 textContent。
// 畫面以「內容簽章」判斷要不要重畫：推送很頻繁，內容沒變就不動 DOM（滑鼠按下中的按鈕、矩陣的水平捲動位置才不會被重設）。
// 撤銷、移除與刪除走擁有者 HTTP（hubapi.js，只有瀏覽器版）；成功之後不自己改畫面，等伺服器推回 devices／accounts。
// remoteOnly 帳戶（目錄裡有、這台沒有；只有桌面）唯一的操作是「在這台登入」（loginHere）。
import { h, setAcc, fmtRelative, fmtDateTime } from './util.js';
import { state, providerInfo, providerKeys, post, isHub } from './store.js';
import { openDialog, confirmDialog, toast } from './ui.js';
import { matrixModel, deviceDots, deviceCliModel, deviceList, validDeviceName, cleanDeviceName, ownerErrorText, countDeviceConversations } from './remoterules.js';
import { revokeDevice, removeDevice, deleteAccount } from './hubapi.js';
// 〔遠端〕瀏覽器版的帳戶與 CLI 操作（WP4.8）：按鈕都在 .ra-acts 裡，桌面不畫
import { cellActionNodes, cellActionSig, deviceCliHeadNode, deviceCliRowNode, deviceCliSig } from './remoteaccounts.js';

const CELL_TONES = new Set(['ok', 'warn', 'bad', 'info', 'muted', 'none']);
const tone = (t) => (CELL_TONES.has(t) ? t : 'muted');

/** remote.devices 依 id 查（帳戶卡的小點用） */
function devicesById() {
  const r = state.remote;
  return new Map((r && Array.isArray(r.devices) ? r.devices : []).filter((d) => d && d.id).map((d) => [d.id, d]));
}

/** 離線的「最後已知」：每分鐘由 dashboard.tickMinute 依 data-rel-at 更新 */
function seenNode(prefix, at, cls) {
  if (!at) return h('span', { class: cls, text: '離線' });
  return h('span', { class: cls, dataset: { relAt: String(at), relPrefix: prefix }, title: fmtDateTime(at), text: prefix + fmtRelative(at) });
}

/** 離線的電腦：剛斷線（remoterules.trackDeviceLinks 標的 reconnecting）是「重新連線中…」，其他是「離線・最後上線 …」 */
function offlineNode(reconnecting, at, cls) {
  if (reconnecting) return h('span', { class: cls, dataset: { link: 'reconnecting' }, title: at ? '最後上線 ' + fmtDateTime(at) : null, text: '重新連線中…' });
  return seenNode('離線・最後上線 ', at, cls);
}

// ───── 帳戶 × 電腦（設計 6.8）─────

/**
 * 畫矩陣到 wrap（.mx-wrap，窄螢幕只在這裡水平捲動）：欄＝未撤銷的電腦（「這台」另標），列＝有 devices 的帳戶。
 * 格子的 data-kind／data-tone 照 cellView；離線調暗並附最後上線時間；負責查用量的格子標「查用量」
 */
export function renderMatrix(wrap, now = Date.now()) {
  const model = matrixModel(state.accounts, state.remote, now);
  const selfId = state.remote ? state.remote.deviceId : null;
  const cols = model.columns.map((d) => ({ id: d.id, name: d.name || d.id, self: d.self === true || (!!selfId && d.id === selfId), online: d.online === true, reconnecting: d.reconnecting === true, lastSeenAt: d.lastSeenAt || null }));
  const rows = model.rows.map((r) => ({
    id: r.account.id, label: r.account.label || '', color: r.account.color, provider: r.account.provider, remoteOnly: r.account.remoteOnly === true, cells: r.cells,
    acts: r.cells.map((v, i) => cellActionSig(r.account, v, cols[i])),
  }));
  const sig = JSON.stringify([cols, rows]);
  if (wrap._sig === sig) return;
  wrap._sig = sig;
  const left = wrap.scrollLeft;
  wrap.textContent = '';
  if (!rows.length || !cols.length) {
    wrap.append(h('div', { class: 'mx-empty', text: !cols.length ? '還沒有配對的電腦。' : '還沒有帳戶。' }));
    return;
  }
  const head = h('tr', null, h('th', { scope: 'col', class: 'mx-corner', text: '帳戶' }),
    cols.map((c) => h('th', { scope: 'col', class: 'mx-dev', dataset: { device: c.id, online: String(c.online) } },
      h('span', { class: 'mx-dev-line' }, h('span', { class: 'mx-dev-dot', 'aria-hidden': 'true' }), h('span', { class: 'mx-dev-name', text: c.name, title: c.name }),
        c.self ? h('span', { class: 'tag mx-self', text: '這台' }) : null),
      c.online ? h('span', { class: 'mx-dev-state', text: '在線' }) : offlineNode(c.reconnecting, c.lastSeenAt, 'mx-dev-state'))));
  const body = rows.map((r, ri) => {
    const th = h('th', { scope: 'row', class: 'mx-acct' },
      h('span', { class: 'mx-acct-dot', 'aria-hidden': 'true' }),
      h('span', { class: 'mx-acct-text' }, h('span', { class: 'mx-acct-name', text: r.label, title: r.label }), h('span', { class: 'mx-acct-prov', text: providerInfo(r.provider).name })));
    setAcc(th, r.color, r.provider);
    const tr = h('tr', { dataset: { account: r.id } }, th, r.cells.map((v, i) => matrixCell(v, cols[i], model.rows[ri].account)));
    if (r.remoteOnly) tr.classList.add('remote-only');
    return tr;
  });
  wrap.append(h('table', { class: 'mx' }, h('thead', null, head), h('tbody', null, body)));
  wrap.scrollLeft = left;
  bindMatrixCrosshair(wrap);
}

/**
 * 矩陣的十字準線：滑過的那一格所在的那一行（同一台電腦的表頭與各格，data-device）加上 .mx-hc；那一列由 CSS 的 tr:hover 處理。
 * 只比對 data 屬性的字串，不組選擇器；監聽器在外框上只綁一次（表格每次重畫都換新的）
 */
function bindMatrixCrosshair(wrap) {
  if (wrap._mxHover) return;
  wrap._mxHover = true;
  const clear = () => { for (const x of wrap.querySelectorAll('.mx-hc')) x.classList.remove('mx-hc'); };
  wrap.addEventListener('pointerover', (e) => {
    const c = e.target instanceof Element ? e.target.closest('td[data-device], th[data-device]') : null;
    const dev = c ? c.dataset.device : null;
    if (dev === wrap._hc) return;
    wrap._hc = dev;
    clear();
    if (dev) for (const x of wrap.querySelectorAll('td[data-device], th[data-device]')) if (x.dataset.device === dev) x.classList.add('mx-hc');
  });
  wrap.addEventListener('pointerleave', () => { wrap._hc = null; clear(); });
}

function matrixCell(v, col, account) {
  if (!v) return h('td', { class: 'mx-cell', dataset: { kind: 'none', device: col.id }, title: col.name + '：沒有這個帳戶' }, h('span', { class: 'mx-text dim', text: '—' }));
  // remoteOnly 帳戶在「這台」那一格（cellView 的 action:'loginHere'）：「在這台登入」，和帳戶卡送同一個指令
  let login = null;
  if (v.action === 'loginHere' && !isHub) {
    login = h('button', { type: 'button', class: 'btn sm primary mx-login', text: '在這台登入' });
    login.addEventListener('click', () => loginHere(state.accountMap.get(account.id)));
  }
  const td = h('td', { class: 'mx-cell' + (v.dim ? ' dim' : ''), dataset: { kind: v.kind, tone: tone(v.tone), device: col.id } },
    h('span', { class: 'mx-text', text: v.text }),
    v.hint ? h('span', { class: 'mx-hint', text: v.hint }) : null,
    v.poller ? h('span', { class: 'mx-poller', title: '這台負責查這個帳戶的用量', text: '查用量' }) : null,
    v.dim ? seenNode(v.reconnecting ? '重新連線中・最後已知 ' : '離線・最後已知 ', v.lastSeenAt, 'mx-seen') : null,
    login,
    cellActionNodes(account, v, col));
  td.title = [col.name + '：' + v.text, v.hint, v.poller ? '這台負責查用量' : null, v.dim ? (v.reconnecting ? '重新連線中' : '離線') + '（顯示的是最後已知的狀態）' : null].filter(Boolean).join('\n');
  return td;
}

// ───── remoteOnly：在這台登入 ─────

/** 「在這台登入」送出的時間（依邏輯帳戶）：連點、帳戶卡與矩陣各按一次都只送一次，避免在這台建兩個帳戶 */
const loginHereAt = new Map();
const LOGIN_HERE_GAP = 10000;

/**
 * remoteOnly 帳戶的「在這台登入」（桌面）：照目錄裡的名稱與顏色在這台新增帳戶並開始登入，帶 logicalAccountId 讓宿主連結回同一個邏輯帳戶。
 * 只送 accountAdd：remoteOnly 的 id 是邏輯帳戶 id（la_…），不能當成本機帳戶的 accountId 送任何指令
 */
export function loginHere(a) {
  if (isHub || !a || a.remoteOnly !== true) return;
  const logicalAccountId = a.logicalId || a.id;
  const now = Date.now();
  if (now - (loginHereAt.get(logicalAccountId) || 0) < LOGIN_HERE_GAP) return;
  loginHereAt.set(logicalAccountId, now);
  post({ cmd: 'accountAdd', provider: a.provider, label: a.label, color: a.color, logicalAccountId });
  toast('正在這台加入「' + a.label + '」，請在開啟的視窗完成登入', 'success');
}

// ───── 各電腦 CLI（DeviceCliView）─────

/** 畫各電腦的 CLI 到 grid（.dcli-grid）：每台一張卡、每種 CLI 一列；沒有任何電腦回報時寫說明 */
export function renderDeviceCli(grid) {
  const model = deviceCliModel(state.devicesCli, state.remote, providerKeys());
  const sig = JSON.stringify([model, model.map((d) => deviceCliSig(d))]);
  if (grid._sig === sig) return model.length;
  grid._sig = sig;
  grid.textContent = '';
  if (!model.length) {
    grid.append(h('div', { class: 'mx-empty', text: '還沒有電腦回報 CLI 的狀態。' }));
    return 0;
  }
  for (const d of model) {
    const st = d.online === true ? h('span', { class: 'dcli-state on', text: '在線' })
      : d.online === false ? offlineNode(d.reconnecting, d.lastSeenAt, 'dcli-state') : null;
    const rows = d.clis.map((c) => {
      const v = c.view;
      return h('div', { class: 'dcli-row', dataset: { provider: c.provider, kind: v.kind, tone: tone(v.tone) } },
        h('dt', { class: 'dcli-prov', text: providerInfo(c.provider).name }),
        h('dd', { class: 'dcli-val' },
          h('span', { class: 'dcli-text', text: v.text, title: v.text }),
          v.notes.map((n) => h('span', { class: 'dcli-note', text: n })),
          deviceCliRowNode(d, c)));
    });
    grid.append(h('article', { class: 'dcli', dataset: { device: d.deviceId } },
      h('div', { class: 'dcli-head' }, h('span', { class: 'dcli-name', text: d.name, title: d.name }), d.self ? h('span', { class: 'tag', text: '這台' }) : null, h('span', { class: 'grow' }), st,
        deviceCliHeadNode(d)),
      h('dl', { class: 'dcli-rows' }, rows)));
  }
  return model.length;
}

// ───── 帳戶卡的裝置小點 ─────

/** 帳戶卡（與側欄）的裝置小點：devices 不是陣列（沒連結到目錄）或沒有任何一台有這個帳戶時回 null */
export function deviceStrip(account, now = Date.now()) {
  const dots = deviceDots(account, devicesById(), now);
  if (!dots.length) return null;
  return h('span', { class: 'dstrip', role: 'list', 'aria-label': '各電腦的登入狀態' },
    dots.map((d) => h('span', { class: 'ddot', role: 'listitem', dataset: { tone: d.tone, device: d.deviceId }, title: d.title, 'aria-label': d.title })));
}

/** 小點的內容簽章（帳戶卡判斷要不要重畫） */
export function deviceStripSig(account, now = Date.now()) {
  return JSON.stringify(deviceDots(account, devicesById(), now));
}

// ───── 電腦（DeviceView）：改名、撤銷與移除（瀏覽器版）─────

/** 桌面同步設定的伺服器網址（「到網頁版的電腦頁撤銷」用；不寫死） */
export function syncServer() {
  const sync = state.remote && state.remote.sync;
  return sync && typeof sync.server === 'string' && sync.server ? sync.server : null;
}

/**
 * 畫電腦清單到 grid（.dev-grid）：未撤銷的照順序在前、已撤銷的在最後（寫時間與原因）。
 * 每台：顯示名稱（和配對時的名稱不同才另標）、電腦名稱、版本、裝置指紋、金鑰位置、在線或最後上線、其他電腦的遠端操作政策（只顯示）。
 * 不顯示伺服器身分指紋（要比對的是伺服器主控台，網頁不能拿來比）。瀏覽器版另有「撤銷…」、已撤銷的「移除…」與「新增電腦」；桌面只能改名
 */
export function renderDevices(grid) {
  const list = deviceList(state.remote);
  const sig = JSON.stringify(list);
  if (grid._sig === sig) return list.length;
  grid._sig = sig;
  grid.textContent = '';
  for (const v of list) grid.append(deviceCard(v));
  if (isHub) {
    grid.append(h('article', { class: 'dev dev-add' },
      h('h3', { class: 'dev-name', text: '新增電腦' }),
      h('p', { class: 'dev-add-text', text: '在那台電腦的 Clode Myriagent：設定 → 多電腦同步 → 連接，畫面上會出現配對碼與網址。用那個網址開啟，或在這裡輸入配對碼。' }),
      h('div', { class: 'dev-acts' }, h('a', { class: 'btn sm', href: '/#pair', text: '輸入配對碼' }))));
  }
  return list.length;
}

function devRow(label, value, cls) {
  return value ? [h('dt', { text: label }), h('dd', { class: cls || null, text: value, title: value })] : null;
}

function deviceCard(v) {
  const status = v.revoked
    ? h('span', { class: 'dev-state revoked', text: v.revoked.text + (v.revoked.at ? '・' + fmtDateTime(v.revoked.at) : '') })
    : v.online ? h('span', { class: 'dev-state on', text: '在線' }) : offlineNode(v.reconnecting, v.lastSeenAt, 'dev-state');
  const acts = [];
  if (v.canRename) {
    const b = h('button', { type: 'button', class: 'btn sm dev-rename', text: '改名…' });
    b.addEventListener('click', () => openRenameDevice(v));
    acts.push(b);
  }
  if (isHub && !v.revoked) {
    const b = h('button', { type: 'button', class: 'btn sm ghost dev-revoke', text: '撤銷…' });
    b.addEventListener('click', () => openRevokeDevice(v));
    acts.push(b);
  }
  if (isHub && v.canRemove) {
    const b = h('button', { type: 'button', class: 'btn sm ghost dev-remove', text: '移除…' });
    b.addEventListener('click', () => openRemoveDevice(v));
    acts.push(b);
  }
  return h('article', { class: 'dev' + (v.revoked ? ' is-revoked' : ''), dataset: { device: v.id } },
    h('div', { class: 'dev-head' },
      h('h3', { class: 'dev-name', text: v.name, title: v.name }),
      v.self ? h('span', { class: 'tag', text: '這台' }) : null,
      h('span', { class: 'grow' }), status),
    v.signedNameNote ? h('div', { class: 'dev-signed', text: v.signedNameNote }) : null,
    v.verifiedWarning ? h('div', { class: 'note warn dev-warn', role: 'note' }, h('span', { text: v.verifiedWarning })) : null,
    h('dl', { class: 'dev-rows' }, devRow('電腦名稱', v.machineName), devRow('版本', v.appVersion), devRow('裝置指紋', v.fingerprint, 'mono'), devRow('金鑰位置', v.keyStorageText)),
    h('div', { class: 'dev-policy', text: v.policyText }),
    acts.length ? h('div', { class: 'dev-acts' }, acts) : null);
}

/** 改名：清理後 1～60 字才送 deviceRename（伺服器照同一套規則再清一次）；已撤銷的不給改 */
function openRenameDevice(v) {
  const input = h('input', { type: 'text', class: 'input', value: v.name, maxlength: '60', autocomplete: 'off', spellcheck: 'false', 'aria-label': '電腦名稱' });
  const err = h('div', { class: 'field-hint dev-name-err', role: 'alert', hidden: true, text: '名稱要 1～60 個字' });
  const body = h('div', { class: 'form' },
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: '顯示名稱' }), input),
    err,
    h('p', { class: 'form-note', text: '只改各處顯示的名稱；配對時的名稱不會變。' }));
  const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
  const ok = h('button', { type: 'button', class: 'btn primary', text: '儲存' });
  const d = openDialog({ title: '電腦「' + v.name + '」改名', body, footer: [cancel, ok], width: 420, className: 'dlg-dev-rename', initialFocus: input });
  const sync = () => { const valid = validDeviceName(input.value); ok.disabled = !valid; err.hidden = valid; };
  const save = () => {
    if (!validDeviceName(input.value)) { sync(); return; }
    const name = cleanDeviceName(input.value);
    if (name !== v.name) post({ cmd: 'deviceRename', deviceId: v.id, name });
    d.close();
  };
  input.addEventListener('input', sync);
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing && e.keyCode !== 229) { e.preventDefault(); save(); } });
  ok.addEventListener('click', save);
  cancel.addEventListener('click', () => d.close());
  requestAnimationFrame(() => input.select());
}

const inflight = new Set();

/** 擁有者操作的結果：成功提示一則（畫面等推送），失敗以 ownerErrorText 提示；取消與 401（已導向登入頁）不提示 */
function reportOwner(action, r, okText) {
  if (r.ok) { toast(okText, 'success'); return; }
  if (r.cancelled || r.status === 401) return;
  toast(ownerErrorText(action, r.status, r.code, r.retryAfter), 'error');
}

/** 撤銷（瀏覽器版）：寫明後果、選原因（退役／疑似遭入侵）→ POST revoke（要近期驗證） */
function openRevokeDevice(v) {
  if (inflight.has('revoke:' + v.id)) return;
  let reason = 'retired';
  const choice = (value, label, desc) => {
    const input = h('input', { type: 'radio', name: 'revoke-reason', value, checked: value === reason });
    input.addEventListener('change', () => { if (input.checked) reason = value; });
    return h('label', { class: 'check-row dev-reason' }, input, h('span', { class: 'check-text' }, h('span', { text: label }), h('small', { text: desc })));
  };
  const body = h('div', { class: 'confirm' },
    h('p', { class: 'confirm-msg', text: '撤銷之後，這台電腦會立即斷線、它的權杖作廢；要再同步就得重新配對。' }),
    h('p', { class: 'confirm-detail', text: '那台電腦本機的帳戶與對話不受影響。這個動作要再輸入一次密碼。' }),
    h('div', { class: 'dev-reasons', role: 'radiogroup', 'aria-label': '撤銷原因' },
      choice('retired', '退役', '不再使用這台電腦（換電腦、重灌）'),
      choice('compromised', '疑似遭入侵', '電腦遺失、被盜或懷疑被別人控制')));
  const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
  const ok = h('button', { type: 'button', class: 'btn danger-solid', text: '撤銷' });
  const d = openDialog({ title: '撤銷電腦「' + v.name + '」？', body, footer: [cancel, ok], width: 460, className: 'dlg-confirm dlg-revoke', initialFocus: cancel });
  cancel.addEventListener('click', () => d.close());
  ok.addEventListener('click', async () => {
    d.close();
    inflight.add('revoke:' + v.id);
    try {
      reportOwner('revoke', await revokeDevice(v.id, reason), '已撤銷「' + v.name + '」，那台電腦會立即斷線');
    } finally {
      inflight.delete('revoke:' + v.id);
    }
  });
}

/**
 * 移除已撤銷的電腦（瀏覽器版，設計 14.22）：寫明它同步到伺服器的對話會一起刪除（網頁版與其他電腦上的也會消失）→ POST remove（要近期驗證）。
 * 成功的提示帶伺服器回的實際刪除數
 */
async function openRemoveDevice(v) {
  if (inflight.has('remove:' + v.id)) return;
  const n = countDeviceConversations(state.conversations, v.id);
  const r = await confirmDialog({
    title: '移除電腦「' + v.name + '」？',
    message: '會從電腦清單移除，並刪除它同步到伺服器的' + (n > 0 ? ' ' + n + ' 個' : '') + '對話；網頁版與其他電腦上的這些對話也會跟著消失。',
    detail: '那台電腦本機的帳戶與對話不受影響。移除之後無法復原，要再同步就得重新配對。這個動作要再輸入一次密碼。',
    confirmLabel: '移除', danger: true, width: 460,
  });
  if (!r.ok) return;
  inflight.add('remove:' + v.id);
  try {
    const res = await removeDevice(v.id);
    const deleted = res.ok && res.json && Number.isInteger(res.json.conversations) && res.json.conversations > 0 ? res.json.conversations : 0;
    reportOwner('remove', res, '已移除「' + v.name + '」' + (deleted ? '，刪除了 ' + deleted + ' 個對話' : ''));
  } finally {
    inflight.delete('remove:' + v.id);
  }
}

/** 從目錄刪除帳戶（瀏覽器版的帳戶選單）：確認框寫明電腦之後再回報同一個登入身分時會再出現（web-protocol 第六節第 12 點） */
export async function confirmDeleteAccount(a) {
  if (!a || inflight.has('delete:' + a.id)) return;
  const r = await confirmDialog({
    title: '從目錄刪除「' + a.label + '」？',
    message: '各電腦會解除和這個帳戶的連結；各台本機的帳戶、登入與對話都不受影響。',
    detail: '電腦之後再回報同一個登入身分時，這個帳戶會再出現。這個動作要再輸入一次密碼。',
    confirmLabel: '從目錄刪除', danger: true, width: 460,
  });
  if (!r.ok) return;
  inflight.add('delete:' + a.id);
  try {
    reportOwner('delete', await deleteAccount(a.id), '已從目錄刪除「' + a.label + '」');
  } finally {
    inflight.delete('delete:' + a.id);
  }
}
