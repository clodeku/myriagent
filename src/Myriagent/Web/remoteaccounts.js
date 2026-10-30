// 網頁版（瀏覽器）的遠端帳戶與 CLI 操作（WP4.8）：矩陣格子的按鈕、各電腦 CLI 卡的「立即檢查」與「安裝／更新」、帳戶選單的「在所有電腦登出…」、
// 驗證碼上方的「在〈電腦〉」、分頁自己追蹤的遠端登入（計畫 D25）。判斷與文字都在 accountrules.js（純函式），這裡只畫 DOM、送指令、提示。
// 瀏覽器版（isHub）有按鈕；〔桌面遠端操作〕桌面在 remote.sync.peer 是 true 時，其他電腦那一欄也有（指令帶邏輯帳戶 id，經 PeerCmd 送；這台那一欄照舊用桌面自己的按鈕）。電腦名稱、帳戶名稱、statusText 都來自其他電腦，一律 textContent。
// 送出後的追蹤與遠端「立即檢查」只在這個模組的記憶體（每個分頁一份，不寫進瀏覽器的儲存）；每秒的計時器只在有追蹤時開、清空時關。
// 結果：伺服器只在 rejected／unknown 另送提示（done 沒有），所以單台的操作送出時由這裡提示「已要求〈電腦〉…」（D33）；登入的結果由追蹤判斷後提示。
// 指令結果（WP4.6 的 cmdtrack，步 12）：post 回 null（沒有送出，傳輸層已提示）時不記追蹤、不提示；登入與遠端「立即檢查」被拒（rejected）時立刻結束，
// done、unknown（新的 init、150 秒過期）照格子與 checkedAt 繼續判斷（可能已經在那台開始）。
import { h } from './util.js';
import { state, post, isHub, providerKeys, emit } from './store.js';
import { openDialog, toast } from './ui.js';
import { cellView } from './remoterules.js';
import { peerOf } from './liverules.js';
import { track } from './cmdtrack.js';
import {
  cellActions, logoutAllPlan, deviceCliActions, cliCheckSnapshot, cliCheckDone, cliCheckResult, loginWatch, loginSettle, loginPending, sentText, originLabel, productName,
  CLI_CHECK_TIMEOUT_MS,
} from './accountrules.js';

/** 追蹤的狀態變了（登入結束、檢查完成或逾時、送出新的登入）：儀表板訂閱它重畫 */
export const REMOTE_ACCOUNTS_CHANGED = 'remote-accounts-changed';

/** 這個分頁發起、還在追蹤的遠端登入：邏輯帳戶 id（la_…）→ accountrules.loginPending 的結果（一個帳戶同時只追蹤一次） */
const pending = new Map();
/** 這個分頁發起、還沒完成的遠端「立即檢查」：deviceId → { before（按下時的 checkedAt）, at, name } */
const checks = new Map();
/** 確認框開著的動作：同一顆按鈕連點只開一個框 */
const asking = new Set();
let timer = 0;

/** 〔桌面遠端操作〕這台能對那台電腦下帳戶與 CLI 指令：瀏覽器版一律可以；桌面要是其他電腦而且 remote.sync.peer 是 true */
function canRemote(deviceId) {
  if (isHub) return true;
  const r = state.remote;
  const own = r && typeof r === 'object' ? r.deviceId : null;
  return typeof deviceId === 'string' && !!deviceId && typeof own === 'string' && !!own && deviceId !== own && peerOf(r, 'desktop') === 'live';
}

/** 指令用的帳戶：瀏覽器版的 id 就是邏輯帳戶 id；桌面改用 logicalId（沒有連結進目錄的帳戶沒有，不畫按鈕） */
function remoteAccount(account) {
  if (isHub || !account) return account;
  return typeof account.logicalId === 'string' && account.logicalId ? Object.assign({}, account, { id: account.logicalId }) : null;
}

/** remote.devices 裡的那台（DeviceView：在線、遠端操作政策）；不知道是哪台回 null */
function deviceOf(id) {
  const r = state.remote;
  const list = r && Array.isArray(r.devices) ? r.devices : [];
  return list.find((d) => d && d.id === id) || null;
}

function deviceNameOf(id, fallback) {
  const d = deviceOf(id);
  return (d && typeof d.name === 'string' && d.name) || fallback || id;
}

// ───── 確認框（多行說明一行一段）─────

function confirmAction(c, confirmLabel, danger) {
  return new Promise((resolve) => {
    let settled = false;
    const settle = (ok) => { if (settled) return; settled = true; resolve(ok); };
    const lines = String(c.detail || '').split('\n').filter(Boolean);
    const body = h('div', { class: 'confirm' },
      c.message ? h('p', { class: 'confirm-msg', text: c.message }) : null,
      lines.map((t) => h('p', { class: 'confirm-detail', text: t })));
    const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
    const ok = h('button', { type: 'button', class: 'btn ' + (danger ? 'danger-solid' : 'primary'), text: confirmLabel });
    const d = openDialog({ title: c.title, body, footer: [cancel, ok], className: 'dlg-confirm dlg-ra', width: 460, initialFocus: danger ? cancel : ok, onClose: () => settle(false) });
    ok.addEventListener('click', () => { settle(true); d.close(); });
    cancel.addEventListener('click', () => { settle(false); d.close(); });
  });
}

/** 先確認（有確認框時）；同一個 key 的框已經開著就不再開。回傳要不要送出 */
async function confirmed(key, a, confirmLabel) {
  if (!a.confirm) return true;
  if (asking.has(key)) return false;
  asking.add(key);
  try {
    return await confirmAction(a.confirm, confirmLabel || a.label, a.cmd.cmd === 'accountLogout');
  } finally {
    asking.delete(key);
  }
}

/** 一顆動作按鈕：停用時 title 寫原因，可以按時寫說明（hint）；data-act 是動作的 key */
function actionButton(a, onClick) {
  const off = a.disabled || !a.cmd;
  const cls = a.cmd && a.cmd.cmd === 'accountLogout' ? 'btn sm ghost ra-btn' : 'btn sm ra-btn';
  const b = h('button', { type: 'button', class: cls, dataset: { act: a.key }, disabled: off, title: (off ? a.reason : a.hint) || null, text: a.label });
  b.addEventListener('click', () => { if (!b.disabled) onClick(); });
  return b;
}

function changed() {
  syncTimer();
  emit(REMOTE_ACCOUNTS_CHANGED);
}

// ───── 矩陣的格子 ─────

/** 一格的 ctx（accountrules.cellActions 用）：col＝renderMatrix 的欄（id、name） */
function cellCtx(account, col) {
  const p = pending.get(account.id) || null;
  const device = deviceOf(col.id);
  return {
    provider: account.provider,
    accountId: account.id,
    deviceId: col.id,
    deviceName: deviceNameOf(col.id, col.name),
    device,
    pendingHere: !!p && p.deviceId === col.id,
    pendingElsewhere: p && p.deviceId !== col.id ? p.deviceName : null,
    loginBusy: account.loginBusy === true,
    running: account.sessions ? account.sessions.running : 0,
  };
}

function cellActionsOf(account, view, col) {
  const acc = remoteAccount(account);
  if (!acc || !view || !col || !canRemote(col.id)) return [];
  return cellActions(view, cellCtx(acc, col));
}

/** 給 renderMatrix 的內容簽章：按鈕、停用與原因、這個分頁的登入追蹤改變時重畫（桌面是空字串） */
export function cellActionSig(account, view, col) {
  const acts = cellActionsOf(account, view, col);
  return acts.length ? JSON.stringify(acts.map((a) => [a.key, a.label, a.disabled, a.reason])) : '';
}

/** 矩陣一格的按鈕（.ra-acts）；沒有按鈕（桌面、apiKey、沒有這個帳戶的格子）回 null */
export function cellActionNodes(account, view, col) {
  const acts = cellActionsOf(account, view, col);
  if (!acts.length) return null;
  const acc = remoteAccount(account);
  const ctx = cellCtx(acc, col);
  return h('div', { class: 'ra-acts' }, acts.map((a) => actionButton(a, () => runCellAction(a, acc.id, view, ctx))));
}

async function runCellAction(a, accountId, view, ctx) {
  if (a.disabled || !a.cmd) return;
  if (!(await confirmed(accountId + '|' + ctx.deviceId + '|' + a.key, a))) return;
  // 一個帳戶同時只追蹤一次（確認框開著時可能已經從別格送出）
  if (a.cmd.cmd === 'accountLoginDevice' && pending.has(accountId)) return;
  const id = post(a.cmd);
  // 沒有送出（沒有連線、太大；傳輸層已提示）：不記追蹤、不提示「已要求…」
  if (id === null) return;
  switch (a.cmd.cmd) {
    case 'accountLoginDevice':
      pending.set(accountId, loginPending(ctx, view, Date.now(), a.key, id));
      // 被拒立刻結束；done、unknown 照 loginWatch（commandId 不是字串時 cmdtrack 不追）
      track(id, { onSettle: (st) => settleLogin(accountId, id, st) });
      break;
    case 'accountLoginCancel': {
      const p = pending.get(accountId);
      if (p) pending.set(accountId, Object.assign({}, p, { canceled: true }));
      break;
    }
    default:
      break;
  }
  toast(sentText(a.cmd, ctx.deviceName, subjectOf(a.cmd, accountId)), 'info');
  changed();
}

/** 登入指令的結果（cmdtrack）：被拒的立刻結束這一筆追蹤、清掉驗證碼（不提示：伺服器或傳輸層已提示）；其他照 loginWatch 繼續 */
function settleLogin(accountId, commandId, st) {
  const r = loginSettle(pending.get(accountId) || null, commandId, st);
  if (r.state === 'pending' || r.state === 'none') return;
  pending.delete(accountId);
  if (r.clearCode) clearLogin(accountId);
  if (r.toast) toast(r.toast.text, r.toast.level);
  changed();
}

/** 送出提示的主詞：CLI 指令是產品名稱（「Claude Code」），帳戶指令是帳戶名稱 */
function subjectOf(cmd, accountId) {
  switch (cmd.cmd) {
    case 'cliInstall':
    case 'cliUpdate':
      return productName(cmd.provider);
    default: {
      const account = state.accountMap.get(accountId);
      return account ? account.label : null;
    }
  }
}

// ───── 各電腦 CLI ─────

function cliActionsOf(entry) {
  if (!entry || !entry.deviceId || !canRemote(entry.deviceId)) return null;
  return deviceCliActions(entry, deviceOf(entry.deviceId), { checking: checks.has(entry.deviceId) });
}

/** CLI 卡頭的「立即檢查」（.ra-acts）；桌面回 null */
export function deviceCliHeadNode(entry) {
  const r = cliActionsOf(entry);
  if (!r || !r.check) return null;
  return h('div', { class: 'ra-acts ra-head' }, actionButton(r.check, () => startCheck(entry)));
}

/** CLI 卡一列的「安裝／重新安裝／更新」（.ra-acts）；沒有動作或桌面回 null */
export function deviceCliRowNode(entry, cli) {
  const r = cliActionsOf(entry);
  const a = r && cli ? r.rows[cli.provider] : null;
  if (!a) return null;
  return h('div', { class: 'ra-acts' }, actionButton(a, () => runCliAction(a, entry)));
}

/** 給 renderDeviceCli 的內容簽章（停用、檢查中改變時重畫） */
export function deviceCliSig(entry) {
  const r = cliActionsOf(entry);
  if (!r || !r.check) return '';
  return JSON.stringify([r.check.label, r.check.disabled, r.check.reason,
    Object.keys(r.rows).map((p) => [p, r.rows[p] ? [r.rows[p].key, r.rows[p].disabled, r.rows[p].reason] : null])]);
}

function startCheck(entry) {
  const id = entry.deviceId;
  if (checks.has(id)) return;
  const cli = state.devicesCli ? state.devicesCli[id] : null;
  const before = cliCheckSnapshot(cli);
  const cid = post({ cmd: 'cliCheck', deviceId: id });
  // 沒有送出（傳輸層已提示）：按鈕不進「檢查中…」
  if (cid === null) return;
  const c = { before, at: Date.now(), name: deviceNameOf(id, entry.name) };
  checks.set(id, c);
  // 被拒（那台的政策、離線…；伺服器或傳輸層已提示）：立刻恢復按鈕，不等 3 分鐘；done、unknown 照 checkedAt 判斷
  track(cid, {
    onSettle: (st) => {
      if (st !== 'rejected' || checks.get(id) !== c) return;
      checks.delete(id);
      changed();
    },
  });
  changed();
}

async function runCliAction(a, entry) {
  if (a.disabled || !a.cmd) return;
  if (!(await confirmed(entry.deviceId + '|' + a.cmd.provider + '|' + a.key, a))) return;
  // 沒有送出（傳輸層已提示）：不提示「已要求…」
  if (post(a.cmd) === null) return;
  const text = sentText(a.cmd, deviceNameOf(entry.deviceId, entry.name), productName(a.cmd.provider));
  if (text) toast(text, 'info');
}

// ───── 帳戶選單：在所有電腦登出 ─────

/** 瀏覽器版帳戶選單的「在所有電腦登出…」（hubAccountMenu 的一項）；桌面回 null */
export function logoutAllMenuItem(a) {
  if (!isHub || !logoutAllPlan(a)) return null;
  return { label: '在所有電腦登出…', icon: 'logout', onSelect: () => confirmLogoutAll(a.id) };
}

async function confirmLogoutAll(id) {
  const plan = logoutAllPlan(state.accountMap.get(id));
  if (!plan) return;
  const key = 'all|' + id;
  if (asking.has(key)) return;
  asking.add(key);
  let ok = false;
  try {
    ok = await confirmAction(plan, '在所有電腦登出', true);
  } finally {
    asking.delete(key);
  }
  // 結果由伺服器提示（立即一則、各台的結果各一則），這裡不另外提示
  if (ok) post(plan.cmd);
}

// ───── 驗證碼上方的「在〈電腦〉」與登入追蹤 ─────

/** 這個分頁正在哪台登入這個帳戶（Claude 貼回授權碼要送到那台）：deviceId；沒有追蹤或桌面回 null */
export function loginDeviceOf(a) {
  const p = isHub && a ? pending.get(a.id) : null;
  return p && typeof p.deviceId === 'string' && p.deviceId ? p.deviceId : null;
}

/** 帳戶卡的驗證碼上方：這個分頁正在哪台登入這個帳戶（「在 OFFICE-PC」）；沒有追蹤或桌面回 null */
export function originLabelOf(a) {
  return isHub && a ? originLabel(pending.get(a.id)) : null;
}

/** 追蹤用的那一格：cellView 的結果加上伺服器原始的 statusText（D15 的說明在那裡）；帳戶或格子不見了回 null */
function watchCell(a, deviceId, now) {
  const raw = a && Array.isArray(a.devices) ? a.devices.find((c) => c && c.deviceId === deviceId) : null;
  if (!raw) return null;
  return Object.assign(cellView(raw, deviceOf(deviceId), now, { provider: a.provider, remoteOnly: a.remoteOnly === true }), { statusText: raw.statusText });
}

/** 結束時清掉這個帳戶的驗證碼（瀏覽器只會收到登入開始的那一則，之後的進度與結束不會到；done 的由 app.js 自己清） */
function clearLogin(id) {
  const cur = state.login.get(id);
  if (cur && !cur.done) state.login.delete(id);
}

/**
 * 依 loginWatch 判斷每一筆追蹤的遠端登入、依 cliCheckDone 判斷遠端「立即檢查」：結束的清掉驗證碼、提示結果。
 * dashboard.render 開頭（檢視不是儀表板時也會跑）與這個模組自己每秒的計時器呼叫；有任何結束回 true（呼叫端要重畫）
 */
export function tickRemoteLogins(now = Date.now()) {
  let any = false;
  for (const [id, p] of Array.from(pending)) {
    const a = state.accountMap.get(id) || null;
    const r = loginWatch(p, a, watchCell(a, p.deviceId, now), now, state.login.get(id));
    if (r.state === 'pending') {
      pending.set(id, r.pending);
      continue;
    }
    pending.delete(id);
    any = true;
    if (r.clearCode) clearLogin(id);
    if (r.toast) toast(r.toast.text, r.toast.level);
  }
  for (const [id, c] of Array.from(checks)) {
    const cli = state.devicesCli ? state.devicesCli[id] : null;
    if (cliCheckDone(c.before, cli)) {
      checks.delete(id);
      any = true;
      const r = cliCheckResult(c.name, cli, providerKeys());
      toast(r.text, r.level);
    } else if (now - c.at >= CLI_CHECK_TIMEOUT_MS) {
      // 3 分鐘沒有結果：恢復按鈕（同本機的「立即檢查」，逾時不提示）
      checks.delete(id);
      any = true;
    }
  }
  syncTimer();
  return any;
}

function syncTimer() {
  const want = pending.size > 0 || checks.size > 0;
  if (want && !timer) timer = setInterval(onTick, 1000);
  else if (!want && timer) {
    clearInterval(timer);
    timer = 0;
  }
}

function onTick() {
  if (tickRemoteLogins()) emit(REMOTE_ACCOUNTS_CHANGED);
}
