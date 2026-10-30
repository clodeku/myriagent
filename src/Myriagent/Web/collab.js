// 多人協作的畫面（docs/plans/2026-10-30-collaborators.md 第 4 節；web-protocol 的 collaborators、collab*、collabResult）：
// - 擁有者：設定頁的「協作人」區（新增、修改、停用、重設密碼、刪除）與對話選單的「協作人…」（勾選這個對話分享給誰）。
//   桌面與瀏覽器版都一樣：網頁送 collab* 指令（帶 requestId），宿主（桌面經伺服器、瀏覽器版由伺服器）回 collabResult，清單的變動以 collaborators 推回來。
// - 協作人：設定頁的「我的帳號」區（改自己的密碼、登出；直接打伺服器的 auth 端點）。
// 協作人帳號、名稱都是伺服器給的文字：一律以 textContent 放入（h() 的 text），密碼只放在輸入框裡、送出後清空
import { h, fmtRelative } from './util.js';
import { icon } from './icons.js';
import { post, state, on, emit, isHub, isCollaborator } from './store.js';
import { openDialog, openMenu, confirmDialog, toast } from './ui.js';
import { ownerPost } from './hubapi.js';
import {
  cleanUserName, cleanDisplayName, passwordProblem, errorText, listOf, sharedWith, shareCount, statusText, shareIds,
  MAX_USER_NAME, MAX_DISPLAY_NAME, MAX_PASSWORD,
} from './collabrules.js';

export { isCollaborator };

let nextRequest = 0;
const pending = new Map(); // requestId → resolve(collabResult)

/** 能不能管理協作人（擁有者、而且宿主給了協作人狀態：桌面要已配對、伺服器要夠新） */
export function canManageCollaborators() {
  return !isCollaborator() && state.collaborators != null;
}

/** 宿主的 collaborators 訊息（app.js 轉來）：整份取代 */
export function applyCollaborators(value) {
  state.collaborators = value && typeof value === 'object' ? value : null;
  emit('collaborators-changed');
}

/** 宿主的 collabResult（app.js 轉來）：交給等它的那一則 */
export function onCollabResult(m) {
  const id = m && typeof m.requestId === 'string' ? m.requestId : null;
  const resolve = id ? pending.get(id) : null;
  if (!resolve) return;
  pending.delete(id);
  resolve(m);
}

/** 送一則管理指令、等它的 collabResult（60 秒沒回當成伺服器連不上） */
function send(cmd, fields) {
  const requestId = 'c' + Date.now().toString(36) + '-' + (++nextRequest);
  return new Promise((resolve) => {
    const timer = setTimeout(() => { if (pending.delete(requestId)) resolve({ ok: false, code: 'unavailable' }); }, 60_000);
    pending.set(requestId, (m) => { clearTimeout(timer); resolve(m); });
    post({ cmd, requestId, ...fields });
  });
}

// ───── 擁有者：設定頁的「協作人」區 ─────

/** 設定頁的區塊 → { node, refresh, dispose }；不能管理時是說明 */
export function collaboratorsSection() {
  const list = h('div', { class: 'collab-list' });
  const addBtn = h('button', { type: 'button', class: 'btn acc sm', onClick: () => openEditDialog(null) }, icon('plus'), h('span', { text: '新增協作人' }));
  const hint = h('p', { class: 'set-desc collab-hint' });
  const node = h('div', { class: 'set-card collab-card' },
    h('div', { class: 'set-row set-row-block' },
      h('div', { class: 'set-text' },
        h('div', { class: 'set-desc', text: '協作人用你建立的帳號密碼登入你的 Hub 網頁（只有網頁版），只看得到你分享給他的對話，可以在裡面和代理對話、附檔、回答問題與核准工具；不能改模型、權限、帳戶與資料夾，也看不到儀表板與其他設定。每則訊息會標出是誰送的。分享對話在對話的「⋯」選單選「協作人…」。' }),
        hint)),
    list,
    h('div', { class: 'set-actions' }, addBtn));
  const refresh = () => {
    const available = canManageCollaborators();
    addBtn.disabled = !available;
    hint.textContent = available ? '' : isHub ? '伺服器還沒送來協作人的清單，請稍候或重新整理頁面。' : '要先在「多電腦同步」配對到伺服器，才能管理協作人。';
    hint.hidden = available;
    list.textContent = '';
    const all = listOf(state.collaborators);
    if (available && !all.length) list.append(h('p', { class: 'collab-empty', text: '還沒有協作人。' }));
    for (const c of all) list.append(collaboratorRow(c));
  };
  const off = on('collaborators-changed', refresh);
  refresh();
  return { node, refresh, dispose: () => off && off() };
}

function collaboratorRow(c) {
  const count = shareCount(state.collaborators, c.id);
  const more = h('button', { type: 'button', class: 'ibtn sm', title: '更多', 'aria-label': '更多' }, icon('more'));
  more.addEventListener('click', () => openMenu(more, [
    { label: '修改名稱或密碼…', icon: 'edit', onSelect: () => openEditDialog(c) },
    { label: c.disabled ? '啟用' : '停用', icon: c.disabled ? 'check' : 'ban', desc: c.disabled ? null : '停用後立刻登出、不能再登入，分享照留',
      onSelect: () => setDisabled(c, !c.disabled) },
    { type: 'sep' },
    { label: '刪除…', icon: 'trash', danger: true, onSelect: () => confirmRemove(c) },
  ], { placement: 'bottom-end', minWidth: 220 }));
  return h('div', { class: 'collab-row' + (c.disabled ? ' is-off' : ''), dataset: { collab: c.id } },
    h('span', { class: 'collab-dot' + (c.online ? ' on' : ''), 'aria-hidden': 'true' }),
    h('div', { class: 'collab-text' },
      h('div', { class: 'collab-name' }, h('span', { text: c.displayName || c.userName }), h('span', { class: 'collab-user', text: c.userName })),
      h('div', { class: 'collab-meta', text: statusText(c, (t) => fmtRelative(t)) + '・' + (count ? count + ' 個分享的對話' : '沒有分享的對話') })),
    more);
}

/** 新增（c 是 null）或修改一位協作人的對話框 */
function openEditDialog(c) {
  const creating = !c;
  // 新增協作人的帳號密碼不是你的：autocomplete 用 off／new-password，免得瀏覽器把它存成你的登入
  const userInput = h('input', { class: 'input', autocomplete: 'off', spellcheck: 'false', autocapitalize: 'off', maxlength: String(MAX_USER_NAME), value: c ? c.userName : '', disabled: !creating });
  const nameInput = h('input', { class: 'input', autocomplete: 'off', maxlength: String(MAX_DISPLAY_NAME), value: c ? c.displayName : '' });
  const passInput = h('input', { class: 'input', type: 'password', autocomplete: 'new-password', maxlength: String(MAX_PASSWORD), spellcheck: 'false' });
  const showPass = h('input', { type: 'checkbox' });
  showPass.addEventListener('change', () => { passInput.type = showPass.checked ? 'text' : 'password'; });
  const err = h('p', { class: 'stepup-err', role: 'alert', hidden: true });
  const form = h('form', { class: 'form collab-form', novalidate: 'novalidate' },
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: '帳號' }), userInput,
      h('span', { class: 'field-hint', text: creating ? '3～32 個英數字與 . _ - @，登入時不分大小寫；建立後不能改' : '帳號建立後不能改' })),
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: '顯示名稱' }), nameInput,
      h('span', { class: 'field-hint', text: '對話裡標在他送的訊息上，例如「小明」' })),
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: creating ? '密碼' : '新密碼（不改就留空）' }), passInput,
      h('label', { class: 'collab-show' }, showPass, h('span', { text: '顯示密碼' })),
      h('span', { class: 'field-hint', text: creating ? '至少 8 個字；請自己告訴他（伺服器不會寄信）' : '重設密碼後他會被登出，要用新密碼重新登入' })),
    err);
  const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
  const ok = h('button', { type: 'button', class: 'btn primary', text: creating ? '新增' : '儲存' });
  const d = openDialog({ title: creating ? '新增協作人' : '修改協作人', body: form, footer: [cancel, ok], width: 440, className: 'dlg-collab',
    initialFocus: creating ? userInput : nameInput, onClose: () => { passInput.value = ''; } });
  const showError = (text) => { err.textContent = text; err.hidden = false; };
  let busy = false;
  const submit = async (e) => {
    if (e) e.preventDefault();
    if (busy) return;
    const userName = creating ? cleanUserName(userInput.value) : c.userName;
    const displayName = cleanDisplayName(nameInput.value);
    const password = passInput.value;
    if (creating && !userName) { showError('帳號要 3～32 個英數字與 . _ - @，第一個字是英數字，不能是 admin、owner'); userInput.focus(); return; }
    if (!displayName) { showError('顯示名稱要 1～32 個字'); nameInput.focus(); return; }
    if ((creating || password) && passwordProblem(password)) { showError(errorText(passwordProblem(password))); passInput.focus(); return; }
    busy = true;
    ok.disabled = true;
    err.hidden = true;
    const r = creating
      ? await send('collabCreate', { userName, displayName, password })
      : await send('collabUpdate', { userId: c.id, displayName, password: password || null });
    busy = false;
    ok.disabled = false;
    if (r.ok) {
      passInput.value = '';
      d.close();
      toast(creating ? `已新增協作人「${displayName}」` : '已儲存', 'success');
      return;
    }
    showError(errorText(r.code));
  };
  form.addEventListener('submit', submit);
  ok.addEventListener('click', submit);
  for (const input of [userInput, nameInput, passInput]) input.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing && e.keyCode !== 229) submit(e); });
  cancel.addEventListener('click', () => d.close());
}

async function setDisabled(c, disabled) {
  const r = await send('collabUpdate', { userId: c.id, disabled });
  if (!r.ok) toast(errorText(r.code), 'error');
  else toast(disabled ? `已停用「${c.displayName}」，他已被登出` : `已啟用「${c.displayName}」`, 'success');
}

async function confirmRemove(c) {
  const r = await confirmDialog({
    title: '刪除協作人？', message: `刪除「${c.displayName}」（${c.userName}）？`,
    detail: '他會立刻被登出、不能再登入，分享給他的對話也一併取消。對話裡他送過的訊息照留。', confirmLabel: '刪除', danger: true,
  });
  if (!r.ok) return;
  const result = await send('collabDelete', { userId: c.id });
  if (!result.ok) toast(errorText(result.code), 'error');
  else toast(`已刪除協作人「${c.displayName}」`, 'success');
}

// ───── 擁有者：對話的分享 ─────

/** 對話選單的「協作人…」：勾選這個對話分享給誰。syncHint：對話沒有完整同步到伺服器時的說明（協作人看不到內容） */
export function openShareDialog(conversationId, title, syncHint) {
  if (!canManageCollaborators()) return;
  const all = listOf(state.collaborators);
  const current = new Set(sharedWith(state.collaborators, conversationId).map((c) => c.id));
  const boxes = new Map();
  const listEl = h('div', { class: 'collab-share-list' });
  if (!all.length) listEl.append(h('p', { class: 'collab-empty', text: '還沒有協作人：先到設定頁的「協作人」新增。' }));
  for (const c of all) {
    const box = h('input', { type: 'checkbox', checked: current.has(c.id) });
    boxes.set(c.id, box);
    listEl.append(h('label', { class: 'collab-share-row' + (c.disabled ? ' is-off' : '') }, box,
      h('span', { class: 'collab-name' }, h('span', { text: c.displayName || c.userName }), h('span', { class: 'collab-user', text: c.userName })),
      h('span', { class: 'collab-meta', text: statusText(c, (t) => fmtRelative(t)) })));
  }
  const err = h('p', { class: 'stepup-err', role: 'alert', hidden: true });
  const body = h('div', { class: 'collab-share' },
    h('p', { class: 'confirm-detail', text: `「${title || '這個對話'}」分享給勾選的協作人：他們登入網頁版後可以看到這個對話、和代理對話、回答問題與核准工具。` }),
    syncHint ? h('p', { class: 'collab-warn' }, icon('warning'), h('span', { text: syncHint })) : null,
    listEl, err);
  const cancel = h('button', { type: 'button', class: 'btn', text: '取消' });
  const ok = h('button', { type: 'button', class: 'btn primary', text: '儲存', disabled: !all.length });
  const d = openDialog({ title: '協作人', body, footer: [cancel, ok], width: 460, className: 'dlg-collab-share' });
  ok.addEventListener('click', async () => {
    ok.disabled = true;
    const ids = shareIds(state.collaborators, [...boxes].filter(([, b]) => b.checked).map(([id]) => id));
    const r = await send('collabShare', { conversationId, userIds: ids });
    ok.disabled = false;
    if (r.ok) {
      d.close();
      toast(ids.length ? `已分享給 ${ids.length} 位協作人` : '已取消分享', 'success');
      return;
    }
    err.textContent = errorText(r.code);
    err.hidden = false;
  });
  cancel.addEventListener('click', () => d.close());
}

// ───── 協作人：設定頁的「我的帳號」 ─────

/** 協作人自己的帳號：名稱、改密碼、登出（直接打伺服器的 auth 端點） */
export function myAccountSection() {
  const viewer = state.viewer || {};
  const current = h('input', { class: 'input', type: 'password', name: 'current-password', autocomplete: 'current-password' });
  const next = h('input', { class: 'input', type: 'password', name: 'new-password', autocomplete: 'new-password', maxlength: String(MAX_PASSWORD) });
  const again = h('input', { class: 'input', type: 'password', autocomplete: 'new-password', maxlength: String(MAX_PASSWORD) });
  // 隱藏的使用者名稱讓密碼管理員認得是哪個帳號（改完之後更新存下的密碼）
  const user = h('input', { class: 'sr-only', name: 'username', autocomplete: 'username', value: viewer.userName || '', tabIndex: -1, 'aria-hidden': 'true', readonly: 'readonly' });
  const msg = h('p', { class: 'collab-msg', role: 'status', hidden: true });
  const save = h('button', { type: 'submit', class: 'btn acc sm', text: '改密碼' });
  const form = h('form', { class: 'form collab-pass', novalidate: 'novalidate' }, user,
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: '目前的密碼' }), current),
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: '新密碼' }), next),
    h('label', { class: 'field' }, h('span', { class: 'field-label', text: '再輸入一次新密碼' }), again),
    msg, h('div', { class: 'set-actions' }, save));
  const show = (text, level) => { msg.textContent = text; msg.hidden = false; msg.dataset.level = level; };
  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    if (next.value !== again.value) { show('兩次輸入的新密碼不一樣', 'error'); return; }
    if (passwordProblem(next.value)) { show(errorText(passwordProblem(next.value)), 'error'); return; }
    save.disabled = true;
    const r = await ownerPost('/api/v1/auth/password', { currentPassword: current.value, newPassword: next.value });
    save.disabled = false;
    if (r.ok) {
      current.value = next.value = again.value = '';
      show('密碼已更新：其他瀏覽器的登入都已登出', 'ok');
      return;
    }
    show(passwordErrorText(r), 'error');
  });
  const logout = h('button', { type: 'button', class: 'btn sm', onClick: logoutNow }, icon('logout'), h('span', { text: '登出' }));
  const node = h('div', { class: 'set-card' },
    h('div', { class: 'set-row' },
      h('div', { class: 'set-text' }, h('div', { class: 'set-label', text: viewer.name || viewer.userName || '協作人' }),
        h('div', { class: 'set-desc', text: '帳號 ' + (viewer.userName || '—') + '・協作人只看得到擁有者分享給你的對話' })),
      h('div', { class: 'set-ctl' }, logout)),
    h('div', { class: 'set-row set-row-block' }, form));
  return { node };
}

function passwordErrorText(r) {
  if (r.status === 0) return '連不上伺服器，請稍後再試';
  if (r.status === 429) return '嘗試太多次，請稍後再試';
  if (r.code === 'password_failed') return '目前的密碼不對';
  if (r.code === 'password_unchanged') return '新密碼和目前的一樣';
  return errorText(r.code);
}

async function logoutNow() {
  await ownerPost('/api/v1/auth/logout', {});
  location.replace('/');
}

/** 協作人看不到的東西要不要藏（app.js 在 init 之後呼叫；CSS 以 body.viewer-collab 藏擁有者的功能） */
export function applyViewerChrome() {
  document.body.classList.toggle('viewer-collab', isCollaborator());
}
