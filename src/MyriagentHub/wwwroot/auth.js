// Myriagent Hub 的登入、改密碼與工作階段管理頁（單一擁有者帳號、帳號密碼登入，設計 14.4）。
// 嚴格 CSP（script-src 'self'，沒有 inline script、沒有 CDN）：所有伺服器與使用者提供的字串一律 textContent。
// 改狀態的 API 一律帶 X-Myriagent-Csrf: 1（表單送不出自訂標頭）；同源 fetch 會帶 Origin，伺服器兩者都檢查。
// WP3.5：在這頁登入成功之後到網頁版 /app/；網址是 /#pair 或 /#pair=配對碼 時改顯示配對核准頁（登入後留在這裡核准）。
// 核准一律先再輸入一次密碼，核准鈕在擁有者真的操作過這個視窗之前不能按（防 DoubleClickjacking）。
// app 自我更新 U2：網址是 /#releases 時顯示版本頁（releases.js），登入後也留在這裡。
// 判斷都在 authrules.js、releaserules.js（純函式）。
// 版本頁的兩個模組以動態 import 載入：靜態 import 的模組一個失敗，整個 auth.js 就不執行，登入頁停在「載入中…」、配對核准頁也打不開
// （擁有者進來與核准電腦的唯一入口不能被次要的頁面拖垮）。releaserules.js 一開始就載（判斷網址片段要用），失敗時 #releases 當成一般的片段；
// releases.js 第一次顯示版本頁才載，失敗（或 initReleases 丟例外）時版本頁只顯示載入失敗。hub-e2e.mjs 第 12.5 步以攔下模組請求驗證。

import { parsePairHash, afterLoginTarget, isCollaboratorMe, pairErrorText, formatUserCode, cleanPairCode, canArmApprove, decodeFrame, decodeLocked, decodeSteps } from '/authrules.js';

const $ = (id) => document.getElementById(id);

// ───── 一次性的視覺效果（第八批，auth.css 的 HUD 段落）：系統要求減少動態時不播 ─────
const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;

/** 加上 class、時間到拿掉；還在播時先拿掉並強制重算一次才加回去（從頭再播） */
function replayClass(el, cls, ms) {
  if (!el) return;
  clearTimeout(el._fxTimer);
  if (el.classList.contains(cls)) {
    el.classList.remove(cls);
    void el.offsetWidth;
  }
  el.classList.add(cls);
  el._fxTimer = setTimeout(() => el.classList.remove(cls), ms);
}

/** 停下逐字解碼（亂碼拿掉，留下真的文字） */
function stopDecode(el) {
  clearTimeout(el._decodeTimer);
  el.classList.remove('fx-decoding');
  delete el.dataset.scramble;
}

/**
 * 文字先閃過亂碼再由左往右鎖定（authrules 的 decodeFrame；每格 40ms、最多 18 格）；減少動態時直接顯示。
 * 元素裡一直是真的文字（讀屏、複製、探針讀到的都是原文），亂碼只畫在 ::after（auth.css 的 .fx-decoding，content: attr(data-scramble)），
 * 解碼期間真的字透明
 */
function decodeInto(el, text) {
  stopDecode(el);
  el.textContent = text;
  const length = Array.from(text).length;
  const steps = decodeSteps(length);
  if (reducedMotion() || steps === 0) return;
  let step = 0;
  const tick = () => {
    step++;
    if (step >= steps) {
      stopDecode(el);
      return;
    }
    el.dataset.scramble = decodeFrame(text, decodeLocked(length, step, steps), Math.random);
    el._decodeTimer = setTimeout(tick, 40);
  };
  el.dataset.scramble = decodeFrame(text, 0, Math.random);
  el.classList.add('fx-decoding');
  el._decodeTimer = setTimeout(tick, 40);
}

const releaseRules = import('/releaserules.js').catch((e) => {
  console.error('版本頁的規則（releaserules.js）載入失敗', e);
  return null;
});
let releasesPage = null;    // 載入並初始化過的 releases.js（第一次顯示版本頁之後）
let releasesLoading = null; // 載入中或已經結束的那一次（失敗也留著：瀏覽器不會重新要失敗過的模組，要重新整理頁面）

/** 網址片段是不是版本頁（releaserules.isReleasesHash）；規則載入失敗時一律不是 */
async function isReleasesPage() {
  const rules = await releaseRules;
  return rules !== null && rules.isReleasesHash(location.hash);
}

/** 第一次呼叫時載入 releases.js 並交出 HTTP 函式與重新整理（initReleases 只呼叫一次）；之後回同一個結果 */
function loadReleasesPage() {
  releasesLoading ??= import('/releases.js').then((page) => {
    page.initReleases({ request, api, ApiError, refresh });
    releasesPage = page;
    return page;
  });
  return releasesLoading;
}

// 給端到端測試（tools/hub-e2e）用的掛勾：CSP 違規紀錄與各個流程
const probe = (window.__hubProbe = { ready: false, view: 'loading', violations: [] });
document.addEventListener('securitypolicyviolation', (e) => {
  probe.violations.push({ directive: e.violatedDirective, blocked: e.blockedURI, sample: e.sample });
});

const MESSAGES = {
  login_failed: '帳號或密碼錯誤',
  password_failed: '密碼錯誤',
  password_too_short: '新密碼至少要 8 個字',
  password_too_long: '新密碼最多 128 個字',
  password_invalid: '新密碼不能有控制字元',
  password_unchanged: '新密碼和目前的密碼一樣',
  password_rejected: '新密碼不符合規則',
  session_not_found: '找不到這個工作階段（可能已經登出）',
  session_revoked: '這個工作階段已被撤銷，請重新登入',
  rate_limited: '嘗試太頻繁，請稍後再試',
  forbidden_origin: '請求來源不符，請重新整理頁面',
  step_up_required: '這個操作要先再輸入一次密碼',
  unauthenticated: '請先登入',
};

class ApiError extends Error {
  constructor(status, code) {
    super(MESSAGES[code] || `伺服器回應 ${status}`);
    this.status = status;
    this.code = code;
  }
}

async function request(method, path, body) {
  const headers = {};
  if (method !== 'GET') {
    headers['X-Myriagent-Csrf'] = '1';
    headers['Content-Type'] = 'application/json';
  }
  const res = await fetch(path, {
    method,
    headers,
    body: method === 'GET' ? undefined : JSON.stringify(body ?? {}),
    credentials: 'same-origin',
    cache: 'no-store',
  });
  const text = await res.text();
  let data = null;
  try {
    data = text ? JSON.parse(text) : null;
  } catch {
    data = null;
  }
  if (!res.ok) throw new ApiError(res.status, data && data.error);
  return data;
}

// 敏感操作回 403 step_up_required 時：請擁有者再輸入一次密碼，通過後重試一次
async function api(method, path, body) {
  try {
    return await request(method, path, body);
  } catch (e) {
    if (e instanceof ApiError && e.code === 'step_up_required') {
      await stepUp();
      return request(method, path, body);
    }
    throw e;
  }
}

// 再驗證的密碼對話框：送出回傳輸入的密碼，取消（含 Esc）丟「已取消」
function askPassword() {
  const dialog = $('dlg-stepup');
  const form = $('form-stepup');
  const input = $('stepup-password');
  const cancel = $('btn-stepup-cancel');
  return new Promise((resolve, reject) => {
    const done = () => {
      form.removeEventListener('submit', onSubmit);
      cancel.removeEventListener('click', onCancel);
      dialog.removeEventListener('cancel', onCancel);
      if (dialog.open) dialog.close();
    };
    const onSubmit = (e) => {
      e.preventDefault();
      const value = input.value;
      input.value = '';
      done();
      resolve(value);
    };
    const onCancel = (e) => {
      if (e) e.preventDefault();
      input.value = '';
      done();
      reject(new Error('已取消'));
    };
    form.addEventListener('submit', onSubmit);
    cancel.addEventListener('click', onCancel);
    dialog.addEventListener('cancel', onCancel);
    input.value = '';
    dialog.showModal();
    input.focus();
  });
}

/**
 * 登入成功後請瀏覽器記住帳號密碼（Credential Management API；Chrome、Edge 有，Safari、Firefox 沒有就照一般表單的「儲存密碼」提示）。
 * 失敗、使用者拒絕都不影響登入
 */
async function rememberCredential(userName, password) {
  try {
    if (typeof window.PasswordCredential !== 'function' || !navigator.credentials || !userName || !password) return;
    await navigator.credentials.store(new window.PasswordCredential({ id: userName, password, name: userName }));
  } catch {
    // 沒有權限、使用者關掉了密碼管理員
  }
}

// ───── 流程 ─────
async function login(userName, password) {
  const result = await request('POST', '/api/v1/auth/login', { userName, password });
  // 協作人（多人協作）沒有帳戶頁：由呼叫端記住密碼之後直接到網頁版
  if (!(result && result.role === 'collaborator')) await refresh();
  return result;
}

// 端到端測試可以直接給密碼；網頁上由對話框輸入
async function stepUp(password) {
  const value = password ?? (await askPassword());
  return request('POST', '/api/v1/auth/stepup', { password: value });
}

async function logout() {
  await request('POST', '/api/v1/auth/logout', {});
  await refresh();
}

async function changePassword(currentPassword, newPassword) {
  const result = await request('POST', '/api/v1/auth/password', { currentPassword, newPassword });
  await renderAccount();
  return result;
}

async function revokeSession(id) {
  const result = await api('POST', `/api/v1/sessions/${encodeURIComponent(id)}/revoke`, {});
  if (result && result.self) await refresh();
  else await renderAccount();
  return result;
}

// ───── 畫面 ─────
function setStatus(text, tone) {
  const el = $('status');
  el.textContent = text || '';
  el.dataset.tone = tone || '';
  el.hidden = !text;
}

function show(view) {
  probe.view = view;
  $('view-login').hidden = view !== 'login';
  $('view-account').hidden = view !== 'account';
  $('view-pair').hidden = view !== 'pair';
  $('view-releases').hidden = view !== 'releases';
}

const fmt = new Intl.DateTimeFormat('zh-TW', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false });
const when = (ms) => (ms ? fmt.format(new Date(ms)) : '—');

function describeAgent(ua) {
  if (!ua) return '不明的瀏覽器';
  const browser = /Edg\//.test(ua) ? 'Edge' : /Chrome\//.test(ua) ? 'Chrome' : /Firefox\//.test(ua) ? 'Firefox' : /Safari\//.test(ua) ? 'Safari' : '瀏覽器';
  const os = /Windows/.test(ua) ? 'Windows' : /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Mac OS X/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : '';
  return os ? `${browser}／${os}` : browser;
}

function item(title, meta, action) {
  const li = document.createElement('li');
  li.className = 'item';
  const text = document.createElement('div');
  text.className = 'item-text';
  const t = document.createElement('div');
  t.className = 'item-title';
  t.textContent = title;
  const m = document.createElement('div');
  m.className = 'item-meta';
  m.textContent = meta;
  text.append(t, m);
  li.append(text);
  if (action) li.append(action);
  return li;
}

function button(label, cls, onClick) {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = `btn sm ${cls || ''}`.trim();
  b.textContent = label;
  b.addEventListener('click', () => run(b, onClick));
  return b;
}

async function renderAccount() {
  // 兩個一起查（同以前）；工作階段清單只給擁有者，協作人那一個會被拒，先接住、確認不是協作人再丟
  const [me, sessions] = await Promise.all([request('GET', '/api/v1/auth/me'), request('GET', '/api/v1/sessions').catch((e) => e)]);
  // 協作人（多人協作）：帳戶頁、配對、版本頁都只給擁有者；他的帳號（改密碼、登出）在網頁版的設定裡
  if (isCollaboratorMe(me)) {
    location.replace('/app/');
    return false;
  }
  if (sessions instanceof Error) throw sessions;
  $('domain').textContent = me.serverDomain || '';
  const s = me.session || {};
  $('fact-user').textContent = me.name || '—';
  $('fact-created').textContent = when(s.createdAt);
  $('fact-recent').textContent = s.recentAuth ? `有效到 ${when(s.recentAuthUntil)}` : '需要時會再要求輸入密碼';
  $('fact-expires').textContent = when(s.absoluteExpiresAt);

  const ss = $('sessions');
  ss.replaceChildren(
    ...sessions.sessions.map((x) => {
      const meta = `${x.ipLast || x.ipFirst || '—'}・登入 ${when(x.createdAt)}・最後使用 ${when(x.lastSeenAt)}`;
      const action = button(x.current ? '登出這裡' : '撤銷', x.current ? '' : 'danger', async () => {
        if (!x.current && !window.confirm('撤銷這個瀏覽器的登入？')) return;
        await revokeSession(x.id);
        if (!x.current) setStatus('已撤銷', 'ok');
      });
      return item(`${describeAgent(x.userAgent)}${x.current ? '（這個瀏覽器）' : ''}`, meta, action);
    }),
  );
  return true;
}

// 登入狀態下：網址是配對片段就顯示核准頁、#releases 是版本頁，否則帳戶頁；沒登入時是登入頁（配對片段在時多一行提示）
async function refresh() {
  const pairing = parsePairHash(location.hash);
  try {
    if (!(await renderAccount())) return;
    $('login-pair-hint').hidden = true;
    if (pairing.pair) {
      show('pair');
      setStatus('');
      await openPair(pairing.code);
    } else if (await isReleasesPage()) {
      show('releases');
      setStatus('');
      let page;
      try {
        page = await loadReleasesPage();
      } catch (e) {
        console.error('版本頁（releases.js）載入失敗', e);
        setStatus('版本頁載入失敗，請重新整理頁面', 'error');
        return;
      }
      await page.loadReleases();
    } else {
      show('account');
      setStatus('');
    }
  } catch (e) {
    if (e instanceof ApiError && e.status === 401) {
      show('login');
      $('login-pair-hint').hidden = !pairing.pair;
      setStatus('請登入');
      return;
    }
    throw e;
  }
}

// ───── 配對核准頁（/#pair、/#pair=配對碼）─────
const KEY_STORAGE = { tpm: 'TPM（硬體）', software: '軟體（Windows 使用者設定檔）' };
const PAIR_FIELDS = [
  ['pair-device', (p) => p.deviceName],
  ['pair-machine', (p) => p.machineName],
  ['pair-user', (p) => p.windowsUser],
  ['pair-key', (p) => KEY_STORAGE[p.keyStorage] || p.keyStorage],
  ['pair-version', (p) => p.appVersion],
  ['pair-ip', (p) => p.ip],
  ['pair-created', (p) => (p.createdAt ? when(p.createdAt) : null)],
  ['pair-expires', (p) => (p.expiresAt ? when(p.expiresAt) : null)],
];
// 密碼確認（近期驗證）的錯誤沿用上面的說明；其他交給 authrules.pairErrorText
const STEP_UP_CODES = new Set(['password_failed', 'step_up_required', 'unauthenticated', 'session_revoked']);

let pairShown = null; // 畫面上顯示的配對碼（清理過的）；核准、拒絕送它
let pairSeq = 0;      // 查詢的序號：網址片段連續改變時，只採用最後一次的結果
// 核准鈕的停用（DoubleClickjacking：惡意網站在雙擊的第一下關掉蓋在上面的彈出視窗，第二下落在核准鈕上）：
// 配對細節出現、視窗取得焦點或回到可見都重新起算；1 秒後的一次真的指標或鍵盤操作（authrules.canArmApprove）才解除。
// 不用 disabled 屬性：觸控的點按沒有 pointermove，要靠同一下的 pointerdown 解除，停用的按鈕收不到那一下的 click
let approveArmed = false;
let approveSince = 0;

function disarmApprove() {
  approveArmed = false;
  approveSince = performance.now();
  $('btn-pair-approve').setAttribute('aria-disabled', 'true');
}

function maybeArmApprove(e) {
  if (approveArmed) return;
  const ok = canArmApprove({
    shown: pairShown !== null, trusted: e.isTrusted === true, visible: document.visibilityState === 'visible',
    focused: document.hasFocus(), now: performance.now(), since: approveSince,
  });
  if (!ok) return;
  approveArmed = true;
  $('btn-pair-approve').setAttribute('aria-disabled', 'false');
}

function setPairResult(text, tone) {
  const el = $('pair-result');
  el.textContent = text || '';
  el.dataset.tone = tone || '';
  el.hidden = !text;
}

function pairFailure(e) {
  if (e instanceof ApiError) return STEP_UP_CODES.has(e.code) ? e.message : pairErrorText(e.status, e.code);
  return pairErrorText(0, null);
}

function clearPair() {
  pairShown = null;
  disarmApprove();
  $('pair-detail').hidden = true;
  $('pair-done').hidden = true;
  stopDecode($('pair-fp'));
  stopDecode($('pair-device'));
  $('pair-fp').textContent = '';
  for (const [id] of PAIR_FIELDS) $(id).textContent = '—';
  setPairResult('');
}

/**
 * 進入核准頁：網址帶碼只填進欄位、不自動查（2026-11-07 資安檢查：代理的回覆被誘導放一個帶攻擊者配對碼的連結，
 * 點了就直接出現攻擊者的電腦等著核准）；要使用者對照正在配對的那台電腦畫面上的碼，自己按「查詢」
 */
async function openPair(code) {
  clearPair();
  $('pair-code').value = code ? formatUserCode(code) : '';
  $('pair-link-warn').hidden = !code;
  $('pair-code').focus();
}

async function lookupPair(code) {
  const seq = ++pairSeq;
  clearPair();
  setPairResult('查詢中…');
  try {
    const p = await request('GET', `/api/v1/pair/pending?code=${encodeURIComponent(code)}`);
    if (seq !== pairSeq) return;
    const data = p || {};
    for (const [id, get] of PAIR_FIELDS) {
      const v = get(data);
      $(id).textContent = v === null || v === undefined || v === '' ? '—' : String(v);
    }
    pairShown = code;
    $('pair-detail').hidden = false;
    // 指紋與裝置名稱逐字解碼、指紋框掃過一道光（要對照的兩樣東西）
    decodeInto($('pair-fp'), typeof data.fingerprint === 'string' ? data.fingerprint : '—');
    decodeInto($('pair-device'), $('pair-device').textContent);
    replayClass(document.querySelector('.pair-fp-box'), 'fx-scan', 800);
    disarmApprove(); // 從細節出現起算
    setPairResult('');
  } catch (e) {
    if (seq !== pairSeq) return;
    if (e instanceof ApiError && e.status === 401) {
      await refresh();
      return;
    }
    setPairResult(pairFailure(e), 'error');
  }
}

async function decidePair(approve) {
  const code = pairShown;
  if (!code) return;
  try {
    // 核准一律先再輸入一次密碼（近期驗證還有效也一樣）：輸入密碼沒辦法被點擊劫持。取消時 stepUp 丟「已取消」，下面不提示；拒絕不用
    if (approve) {
      await stepUp();
      await request('POST', '/api/v1/pair/approve', { userCode: code });
    } else {
      await request('POST', '/api/v1/pair/deny', { userCode: code });
    }
  } catch (e) {
    if (!(e instanceof ApiError) && e && e.message === '已取消') return;
    if (e instanceof ApiError && e.status === 401) {
      await refresh();
      return;
    }
    setPairResult(pairFailure(e), 'error');
    return;
  }
  pairShown = null;
  $('pair-detail').hidden = true;
  if (approve) {
    setPairResult('已核准。那台電腦會自動完成配對', 'ok');
    replayClass($('view-pair'), 'fx-ok', 700);
    $('pair-done').hidden = false;
  } else {
    setPairResult('已拒絕。那台電腦的配對不會完成；需要的話請在那台電腦重新開始配對', 'ok');
  }
}

async function run(control, work) {
  if (control) control.disabled = true;
  try {
    return await work();
  } catch (e) {
    setStatus(e.message, 'error');
    return undefined;
  } finally {
    if (control) control.disabled = false;
  }
}

$('form-login').addEventListener('submit', (e) => {
  e.preventDefault();
  run($('btn-login'), async () => {
    // 密碼留在欄位裡直到換頁：瀏覽器的密碼管理員看送出之後的換頁決定要不要提示「儲存密碼」（多人協作：協作人下次開啟就自動帶入）
    const userName = $('login-user').value;
    const password = $('login-password').value;
    let result;
    try {
      result = await login(userName, password);
    } catch (err) {
      $('login-password').value = '';
      replayClass($('view-login'), 'fx-deny', 600); // 登入失敗：框錯位幾下、角框閃紅（錯誤照常寫在上面的狀態列）
      throw err;
    }
    await rememberCredential(userName, password);
    $('login-password').value = '';
    // 登入成功：一般到網頁版；從配對網址來的留在這頁核准、從版本頁網址來的留在版本頁（login() 已經切過去了）；協作人一律到網頁版
    const role = result && result.role;
    const target = role !== 'collaborator' && (await isReleasesPage()) ? null : afterLoginTarget(location.hash, role);
    if (target) location.assign(target);
  });
});
$('form-pair').addEventListener('submit', (e) => {
  e.preventDefault();
  run($('btn-pair-lookup'), async () => {
    const code = cleanPairCode($('pair-code').value);
    if (!code) {
      clearPair();
      setPairResult('請輸入配對碼（8 個字，例如 MTLZ-XFCH）', 'error');
      return;
    }
    $('pair-code').value = formatUserCode(code);
    await lookupPair(code);
  });
});
$('btn-pair-approve').addEventListener('click', () => {
  // 還沒解除停用（細節剛出現、視窗剛取得焦點或回到可見、之後沒有真的操作）的點擊一律不理
  if (!approveArmed) return;
  run($('btn-pair-approve'), () => decidePair(true));
});
for (const type of ['pointermove', 'pointerdown', 'keydown']) document.addEventListener(type, maybeArmApprove, true);
window.addEventListener('blur', disarmApprove);
window.addEventListener('focus', disarmApprove);
document.addEventListener('visibilitychange', disarmApprove);
$('btn-pair-deny').addEventListener('click', () => run($('btn-pair-deny'), () => decidePair(false)));
// 網址片段改變（帳戶頁的「核准電腦的配對」、手動改網址）：重新決定顯示哪一頁
window.addEventListener('hashchange', () => {
  refresh().catch((err) => setStatus(err.message, 'error'));
});
$('btn-logout').addEventListener('click', () => run($('btn-logout'), logout));
$('form-password').addEventListener('submit', (e) => {
  e.preventDefault();
  run($('btn-password'), async () => {
    const current = $('pw-current').value;
    const next = $('pw-new').value;
    if (next !== $('pw-confirm').value) {
      setStatus('兩次輸入的新密碼不一樣', 'error');
      return;
    }
    const result = await changePassword(current, next);
    for (const id of ['pw-current', 'pw-new', 'pw-confirm']) $(id).value = '';
    const others = result && result.revokedSessions ? `，其他 ${result.revokedSessions} 個瀏覽器已登出` : '';
    setStatus(`已改密碼${others}`, 'ok');
  });
});

Object.assign(probe, {
  login,
  logout,
  stepUp,
  changePassword,
  revokeSession,
  me: () => request('GET', '/api/v1/auth/me'),
  // 版本頁目前畫出的資料（releases、latest、devices；還沒載入過〔含 releases.js 還沒載入或載入失敗〕是 null）
  releases: () => (releasesPage ? releasesPage.renderedReleases() : null),
  // 正向對照：inline script 必須被 CSP 擋下（會產生一筆 violation）
  tryInlineScript: () => {
    const s = document.createElement('script');
    s.textContent = 'window.__inlineRan = true;';
    document.body.appendChild(s);
    return window.__inlineRan === true;
  },
});

fetch('/healthz', { cache: 'no-store' })
  .then((r) => r.json())
  .then((h) => {
    $('build').textContent = h && h.build ? `build ${h.build}` : '';
  })
  .catch(() => {});

refresh()
  .catch((e) => setStatus(e.message, 'error'))
  .finally(() => {
    probe.ready = true;
  });
