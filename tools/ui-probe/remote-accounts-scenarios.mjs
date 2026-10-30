// 〔遠端〕瀏覽器版的帳戶與 CLI 操作（WP4.8 步 9）的介面探針情境。remote-probe.mjs 只有接縫：import、組 ctx、SCENARIOS 一列；
// 夾具與檢查都在這個檔案（remote-fixtures.mjs 不動：以 RF.browserInit 為基礎，OFFICE-PC 改成「可以操作」、另加幾列帳戶）。
// 檢查名稱見 docs/plans/2026-10-19-wp4.8-remote-accounts.md 步 9；結果一樣寫進 out-remote/remote-report.json。
// 假 hub 對每個指令都回 accepted（步 12 的情境另以 onCmd／rejectNext 推 commandResult、讓 Cmd 被拒），所以這裡驗的是「網頁送出什麼、怎麼顯示與清除」；
// 電腦端的結果由 C# 的測試驗。
// ctx：{ openHub, openDesktop, showDashboard, waitLive, check, closePage, until, sleep, shot, openCardMenu, clickMenuItem, closeMenus, readMatrix, RF, NOW }

export const REMOTE_ACCOUNT_CHECKS = [
  'hubAcctLoginDeviceCode', 'hubAcctDeviceCodeShown', 'hubAcctCodeCopyBrowser', 'hubAcctCodeClearsOnLoginBusyFalse', 'hubAcctCodeClearsOnReady',
  'hubAcctCodeClearsOnExpiry', 'hubAcctLoginCancel', 'hubAcctWatchRunsOutsideDashboard', 'hubAcctLogoutThis', 'hubAcctIdentityLogout', 'hubAcctRecheck',
  'hubAcctClaudeOpenLogin', 'hubAcctClaudeExpiredRelogin', 'hubAcctAbsentCreate', 'hubAcctInstall', 'hubAcctApiKeyNoButton', 'hubAcctLogoutAllConfirm',
  'hubAcctSentToast', 'hubAcctDeviceCliUpdateInstall', 'hubAcctXss', 'hubAcctLoginRejectedAfter60s', 'hubAcctDeviceCliCheck', 'hubAcctPolicyDisabled',
  'hubAcct390NoHScroll', 'desktopNoRemoteButtons',
  // 步 12：WP4.6 的指令結果（cmdtrack）與 post 的回傳值
  'hubAcctLoginRejectedImmediate', 'hubAcctCliCheckRejectedImmediate', 'hubAcctLoginUnknownKeepsWatching', 'hubAcctPostNullNoPending',
  // 合併前審查：這個分頁沒有追蹤的登入也能取消（F4）、別台留下的 loginBusy 不擋成功的判斷（F2）
  'hubAcctCancelUntrackedLogin', 'hubAcctLoginStaleBusyElsewhere',
  // Claude 的遠端登入改成網址＋貼回授權碼（2026-10-26）
  'hubAcctClaudeLoginCode', 'hubAcctClaudeLoginUrlRejected',
];

const MIN = 60e3;
const HOUR = 3600e3;
const DAY = 86400e3;
const VERIFY_URL = 'https://auth.openai.com/codex/device';
const USER_CODE = 'ABCD-EFGH';
/** 身分不符時那一格的說明（桌面 RemoteAccountRules.MismatchNote(true)，經 link.upsert 的 statusText 到格子） */
const NOTE = '上次遠端登入的帳號和目錄不同，已在這台自動登出';
const POLICY_REASON = '在 OFFICE-PC 的設定中允許遠端操作';
/** 沒有連線時使用者操作的指令送不出去，傳輸層的提示（transport-hub.js 的 TEXT_OFFLINE） */
const TEXT_OFFLINE = '目前沒有連線，這個變更沒有送出';

// ───── 夾具 ─────

/** 一格（AccountDeviceView，同 remote-fixtures 的 cell；那個函式沒有匯出） */
function cellOf(device, status, extra = {}) {
  return Object.assign({
    deviceId: device.id, deviceName: device.name, self: false, status,
    statusText: { ready: '已登入', notLoggedIn: '尚未登入', notInstalled: '未安裝 CLI', checking: '檢查中', error: '發生錯誤', apiKey: 'API Key 登入', absent: '尚未加入' }[status] || status,
    email: null, plan: null, accessExpiresAt: null, loginExpiresAt: null, identityOk: true, usagePoller: false, online: device.online, checkedAt: null,
  }, extra);
}

/** OFFICE-PC（d_A）在線、可以操作；筆電（d_B）離線、只能查看；舊桌機已撤銷 */
function controlDevices(RF, now) {
  const [A, B, R] = RF.browserDevices(now);
  return [Object.assign({}, A, { remoteControlLevel: 'Control' }), B, R];
}

/** RF.browserAccounts 的 6 列，加上 Claude 未登入、Claude 登入已到期、Codex 尚未加入、Claude 未安裝各一列（都在 d_A） */
function controlAccounts(RF, now) {
  const [A, B] = RF.browserDevices(now);
  const acct = (id, provider, label, color, devices) => ({
    id, provider, label, color, isSystemDefault: false, configDirectory: null, email: null, plan: null, status: 'notLoggedIn', statusText: '沒有電腦登入',
    safeMode: false, loginBusy: false, usage: null, sessions: { open: 0, running: 0, waiting: 0 }, logicalId: id, remoteOnly: false, devices,
  });
  return RF.browserAccounts(now).concat([
    acct('la_c6', 'Claude', '個人 Claude', '#C678DD', [cellOf(A, 'notLoggedIn'), cellOf(B, 'ready', { email: 'me@example.test' })]),
    acct('la_c7', 'Claude', '到期的 Claude', '#56B6C2', [cellOf(A, 'ready', { email: 'old@example.test', loginExpiresAt: now - HOUR }), cellOf(B, 'absent')]),
    acct('la_x3', 'Codex', '新的 Codex', '#98C379', [cellOf(A, 'absent'), cellOf(B, 'notLoggedIn')]),
    acct('la_c8', 'Claude', '沒裝 CLI 的 Claude', '#E5C07B', [cellOf(A, 'notInstalled'), cellOf(B, 'ready', { email: 'cli@example.test' })]),
  ]);
}

/** d_A 的 Claude CLI 沒裝（卡上的「安裝」）、Codex 有新版（「更新」）；d_B 照 remote-fixtures */
function controlDevicesCli(RF, now) {
  const base = RF.browserDevicesCli(now);
  return Object.assign({}, base, {
    d_A: Object.assign({}, base.d_A, { Claude: Object.assign({}, base.d_A.Claude, { installed: false, version: null, latest: null, installKind: null }) }),
  });
}

/** 換掉一個帳戶的欄位、與它某一台的格子（回新陣列） */
function patch(list, id, fields, deviceId, cellFields) {
  return list.map((a) => {
    if (a.id !== id) return a;
    const devices = deviceId ? a.devices.map((c) => (c.deviceId === deviceId ? Object.assign({}, c, cellFields) : c)) : a.devices;
    return Object.assign({}, a, fields || {}, { devices });
  });
}

// ───── 讀畫面、操作 ─────

const cellSel = (accountId, deviceId) => `#dashboard .mx tr[data-account="${accountId}"] td.mx-cell[data-device="${deviceId}"]`;

/** 一格的按鈕：[{ act, label, disabled, title }]；沒有 .ra-acts 回 null */
function cellButtons(page, accountId, deviceId) {
  return page.evaluate((sel) => {
    const td = document.querySelector(sel);
    const acts = td && td.querySelector('.ra-acts');
    return acts ? [...acts.querySelectorAll('button')].map((b) => ({ act: b.dataset.act, label: b.textContent, disabled: b.disabled, title: b.title })) : null;
  }, cellSel(accountId, deviceId));
}

const clickCell = (page, accountId, deviceId, act) => page.click(`${cellSel(accountId, deviceId)} .ra-acts button[data-act="${act}"]`);

/** CLI 卡（那台）的按鈕：head＝卡頭的「立即檢查」，rows＝{ provider: 那一列的按鈕 } */
function cliButtons(page, deviceId) {
  return page.evaluate((id) => {
    const card = document.querySelector(`#dashboard .dcli[data-device="${id}"]`);
    if (!card) return null;
    const read = (b) => (b ? { act: b.dataset.act, label: b.textContent, disabled: b.disabled, title: b.title } : null);
    return {
      head: read(card.querySelector('.dcli-head .ra-acts button')),
      rows: Object.fromEntries([...card.querySelectorAll('.dcli-row')].map((r) => [r.dataset.provider, read(r.querySelector('.ra-acts button'))])),
    };
  }, deviceId);
}

/** 等確認框（.dlg-ra）出現，回傳它的文字；ok＝按確認（否則按取消） */
async function answerConfirm(page, ok = true) {
  // 只看開著的那一個（剛關掉的框還在淡出、120 毫秒後才移除）
  const sel = '.dlg-back:not(.leaving) .dlg.dlg-ra';
  await page.waitForSelector(sel, { timeout: 3000 });
  const { text, title } = await page.evaluate((s) => { const d = document.querySelector(s); return { text: d.textContent, title: d.querySelector('.dlg-title').textContent }; }, sel);
  await page.click(sel + (ok ? ' .dlg-foot .btn:last-child' : ' .dlg-foot .btn:first-child'));
  await page.waitForFunction((s) => !document.querySelector(s), sel, { timeout: 3000 }).catch(() => {});
  return { text, title };
}

/** 頁面送出的最後一則某指令（cmd 本體）；n＝之前已有幾則（只看之後的） */
const cmdAfter = (hub, name, n) => { const list = hub.cmds(name); return list.length > n ? list[list.length - 1].cmd : null; };
const sameJson = (a, b) => JSON.stringify(a) === JSON.stringify(b);

/** 畫面上出現過的每一則提示（MutationObserver 記下來：提示會自己消失，最多同時 4 則） */
async function watchToasts(page) {
  await page.evaluate(() => {
    window.__raToasts = [];
    const seen = new WeakSet();
    const scan = () => {
      for (const t of document.querySelectorAll('.toasts .toast')) {
        if (seen.has(t)) continue;
        seen.add(t);
        const x = t.querySelector('.toast-text');
        window.__raToasts.push({ text: x ? x.textContent : '', level: t.classList.contains('error') ? 'error' : t.classList.contains('success') ? 'success' : 'info' });
      }
    };
    new MutationObserver(scan).observe(document.body, { childList: true, subtree: true });
    scan();
  });
}
const toastsOf = (page) => page.evaluate(() => (window.__raToasts || []).slice());

/** 帳戶卡上的登入區（驗證碼、「在〈電腦〉」、網址） */
function cardLogin(page, accountId) {
  return page.evaluate((id) => {
    const c = document.querySelector(`#dashboard article.acard[data-key="${id}"]`);
    const box = c && c.querySelector('.acard-login');
    if (!box || box.hidden) return null;
    const code = box.querySelector('.login-code-val');
    const url = box.querySelector('.login-code-url');
    const origin = box.querySelector('.login-origin');
    const claude = box.querySelector('.login-claude .login-url');
    return {
      msg: (box.querySelector('.acard-login-msg') || {}).textContent || null,
      code: code ? code.textContent : null, size: code ? parseFloat(getComputedStyle(code).fontSize) : 0,
      origin: origin ? origin.textContent : null,
      urlTag: url ? url.tagName : null, href: url && url.tagName === 'A' ? url.getAttribute('href') : null,
      claudeUrl: claude ? claude.getAttribute('href') : null, claudeTarget: claude ? claude.getAttribute('target') : null,
      hasInput: !!box.querySelector('.login-claude .login-code-input'),
    };
  }, accountId);
}

const storeLoginHas = (page, id) => page.evaluate((x) => import('/app/store.js').then((m) => m.state.login.has(x)), id);

function deviceCode(expiresAt) {
  return { userCode: USER_CODE, verificationUrl: VERIFY_URL, expiresAt };
}

// ───── 情境 ─────

export async function runRemoteAccounts(ctx) {
  const cliCheck = await runControl(ctx);
  const timeout = await runClock(ctx);
  ctx.check('hubAcctDeviceCliCheck', cliCheck.ok && timeout.cliTimeoutOk, { done: cliCheck, timeout: timeout.cli });
  await runResults(ctx);
  await runPolicyOff(ctx);
  await runPhone(ctx);
  await runDesktop(ctx);
}

/** 主要情境：OFFICE-PC 可以操作；每種格子的按鈕與送出的指令、驗證碼的顯示與清除、所有電腦登出、各電腦 CLI */
async function runControl(ctx) {
  const { openHub, waitLive, check, closePage, until, sleep, shot, openCardMenu, clickMenuItem, closeMenus, RF, NOW } = ctx;
  let accounts = controlAccounts(RF, NOW);
  const devices = controlDevices(RF, NOW);
  const devicesCli = controlDevicesCli(RF, NOW);
  const app = await openHub('hub-acct', { hub: (hub) => hub.onReady(() => RF.browserInit(NOW, { accounts, devices, devicesCli })) });
  const { page, hub } = app;
  await waitLive(app);
  await watchToasts(page);
  const push = async (list) => { accounts = list; hub.push({ type: 'accounts', accounts }); await sleep(120); };
  const pushCode = async (id, expiresAt, url) => {
    hub.push({ type: 'accountLogin', accountId: id, message: '請用手機開啟網址並輸入驗證碼', done: false, success: null, deviceCode: Object.assign(deviceCode(expiresAt), url ? { verificationUrl: url } : {}) });
    await sleep(120);
  };
  const buttonsAre = (id, dev, acts) => until(async () => { const b = await cellButtons(page, id, dev); return b && sameJson(b.map((x) => x.act), acts) ? b : null; }, 4000);
  await page.evaluate(() => document.querySelector('#dashboard .dash-sec-mx').scrollIntoView());
  await shot(page, 'hub-acct-matrix-1440');
  await page.evaluate(() => document.querySelector('#dashboard .dash-sec-dcli').scrollIntoView());
  await shot(page, 'hub-acct-dcli-1440');

  // ── 裝置碼登入：送 accountLoginDevice（帶 deviceId 與 method），送出後那一格變成「登入中…」；送出時不另外提示（等驗證碼）
  const t0 = (await toastsOf(page)).length;
  const before = await cellButtons(page, 'la_x1', 'd_A');
  let n = hub.cmds('accountLoginDevice').length;
  await clickCell(page, 'la_x1', 'd_A', 'login');
  const sent = await until(() => cmdAfter(hub, 'accountLoginDevice', n), 3000);
  const waiting = await buttonsAre('la_x1', 'd_A', ['waiting']);
  check('hubAcctLoginDeviceCode', !!before && sameJson(before.map((b) => [b.act, b.label, b.disabled]), [['login', '用裝置碼登入', false]])
    && sameJson(sent, { cmd: 'accountLoginDevice', logicalAccountId: 'la_x1', deviceId: 'd_A', method: 'deviceCode' })
    && !!waiting && waiting[0].disabled && waiting[0].label === '登入中…' && (await toastsOf(page)).length === t0,
    { before, sent, waiting, toasts: (await toastsOf(page)).slice(t0) });

  // ── 驗證碼：大字、「在 OFFICE-PC」、https 才是連結，點了經 openLink 在瀏覽器開新分頁（不送伺服器）
  await pushCode('la_x1', Date.now() + 15 * MIN);
  const shown = await until(async () => { const c = await cardLogin(page, 'la_x1'); return c && c.code ? c : null; }, 3000);
  await page.evaluate(() => document.querySelector('#dashboard article.acard[data-key="la_x1"]').scrollIntoView({ block: 'center' }));
  await shot(page, 'hub-acct-code-1440');
  const opened0 = await page.evaluate(() => window.__opened.length);
  await page.click('#dashboard article.acard[data-key="la_x1"] a.login-code-url');
  const opened = await until(() => page.evaluate((k) => (window.__opened.length > k ? window.__opened[window.__opened.length - 1] : null), opened0), 3000);
  await pushCode('la_x1', Date.now() + 15 * MIN, 'http://auth.openai.com/codex/device');
  const plain = await until(async () => { const c = await cardLogin(page, 'la_x1'); return c && c.urlTag === 'SPAN' ? c : null; }, 3000);
  await pushCode('la_x1', Date.now() + 15 * MIN);
  check('hubAcctDeviceCodeShown', !!shown && shown.code === USER_CODE && shown.size >= 20 && shown.origin === '在 OFFICE-PC' && shown.urlTag === 'A' && shown.href === VERIFY_URL
    && sameJson(opened, [VERIFY_URL, '_blank', 'noopener,noreferrer']) && hub.cmds('openLink').length === 0 && !!plain && plain.href === null,
    { shown, opened, openLinkFrames: hub.cmds('openLink').length, plain });

  // ── 複製：瀏覽器的剪貼簿寫入被拒 → 交給傳輸層的 copyText（不送伺服器）；不會誤提示「已複製驗證碼」
  await page.evaluate(() => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: () => Promise.reject(new DOMException('探針：拒絕寫入剪貼簿', 'NotAllowedError')) } });
  });
  const t1 = (await toastsOf(page)).length;
  await page.click('#dashboard article.acard[data-key="la_x1"] .login-code-copy');
  // WP4.6 起傳輸層把 copyText 交給網頁的 copyFallback：開可選取的唯讀文字框，裡面是這組驗證碼（步 9 時還只有傳輸層的提示）
  const fallback = await until(() => page.evaluate((code) => [...document.querySelectorAll('.dlg textarea.copy-fallback-text')].some((t) => t.value === code) || null, USER_CODE), 3000);
  const copyToasts = (await toastsOf(page)).slice(t1).map((t) => t.text);
  check('hubAcctCodeCopyBrowser', !!fallback && !copyToasts.includes('已複製驗證碼') && hub.cmds('copyText').length === 0, { fallback, toasts: copyToasts, frames: hub.cmds('copyText').length });
  await page.keyboard.press('Escape');

  // ── 清除（一）：那台開始登入（loginBusy）後變回 false、格子仍未登入 → 結束，提示用格子的 statusText（D15 的說明）
  await push(patch(accounts, 'la_x1', { loginBusy: true }));
  const startedBtns = await buttonsAre('la_x1', 'd_A', ['cancel']);
  const t2 = (await toastsOf(page)).length;
  await push(patch(accounts, 'la_x1', { loginBusy: false }, 'd_A', { statusText: NOTE }));
  const cleared1 = await until(async () => ((await cardLogin(page, 'la_x1')) === null && !(await storeLoginHas(page, 'la_x1')) ? true : null), 3000);
  const ended = (await toastsOf(page)).slice(t2);
  const back1 = await buttonsAre('la_x1', 'd_A', ['login']);
  check('hubAcctCodeClearsOnLoginBusyFalse', !!startedBtns && !!cleared1 && !!back1
    && ended.some((t) => t.text === 'OFFICE-PC 的登入沒有完成：' + NOTE && t.level === 'error'), { startedBtns, cleared: !!cleared1, toasts: ended, back: back1 });
  await push(patch(accounts, 'la_x1', {}, 'd_A', { statusText: '尚未登入' }));

  // ── 清除（二）：格子變成 ready → 成功
  await clickCell(page, 'la_x1', 'd_A', 'login');
  await buttonsAre('la_x1', 'd_A', ['waiting']);
  await pushCode('la_x1', Date.now() + 15 * MIN);
  await push(patch(accounts, 'la_x1', { loginBusy: true }));
  await buttonsAre('la_x1', 'd_A', ['cancel']);
  const t3 = (await toastsOf(page)).length;
  await push(patch(accounts, 'la_x1', { loginBusy: false }, 'd_A', { status: 'ready', statusText: '已登入', email: 'codex@example.test' }));
  const cleared2 = await until(async () => ((await cardLogin(page, 'la_x1')) === null && !(await storeLoginHas(page, 'la_x1')) ? true : null), 3000);
  const ok2 = (await toastsOf(page)).slice(t3);
  check('hubAcctCodeClearsOnReady', !!cleared2 && ok2.some((t) => t.text === 'OFFICE-PC 已登入「Codex Pro」' && t.level === 'success'), { cleared: !!cleared2, toasts: ok2 });
  await push(patch(accounts, 'la_x1', {}, 'd_A', { status: 'notLoggedIn', statusText: '尚未登入', email: null }));
  await buttonsAre('la_x1', 'd_A', ['login']);

  // ── 清除（三）：驗證碼到期 → 結束（每秒的計時器）
  await clickCell(page, 'la_x1', 'd_A', 'login');
  await buttonsAre('la_x1', 'd_A', ['waiting']);
  const t4 = (await toastsOf(page)).length;
  await pushCode('la_x1', Date.now() + 1500);
  await push(patch(accounts, 'la_x1', { loginBusy: true }));
  const cleared3 = await until(async () => ((await cardLogin(page, 'la_x1')) === null || !(await storeLoginHas(page, 'la_x1')) ? true : null), 6000);
  const exp = (await toastsOf(page)).slice(t4);
  check('hubAcctCodeClearsOnExpiry', !!cleared3 && !(await storeLoginHas(page, 'la_x1')) && exp.some((t) => t.text === 'OFFICE-PC 的登入沒有在時限內完成' && t.level === 'error'),
    { cleared: !!cleared3, toasts: exp });
  await push(patch(accounts, 'la_x1', { loginBusy: false }));
  await buttonsAre('la_x1', 'd_A', ['login']);

  // ── 取消登入：那台開始登入後格子只有「取消登入」（title 寫明驗證碼在那台的帳戶卡），送 accountLoginCancel {accountId, deviceId}
  await clickCell(page, 'la_x1', 'd_A', 'login');
  await buttonsAre('la_x1', 'd_A', ['waiting']);
  await push(patch(accounts, 'la_x1', { loginBusy: true }));
  const cancelBtns = await buttonsAre('la_x1', 'd_A', ['cancel']);
  n = hub.cmds('accountLoginCancel').length;
  const t5 = (await toastsOf(page)).length;
  await clickCell(page, 'la_x1', 'd_A', 'cancel');
  const cancelSent = await until(() => cmdAfter(hub, 'accountLoginCancel', n), 3000);
  await push(patch(accounts, 'la_x1', { loginBusy: false }));
  const back5 = await buttonsAre('la_x1', 'd_A', ['login']);
  await sleep(1300);
  const cancelToasts = (await toastsOf(page)).slice(t5);
  check('hubAcctLoginCancel', !!cancelBtns && /帳戶卡/.test(cancelBtns[0].title) && sameJson(cancelSent, { cmd: 'accountLoginCancel', accountId: 'la_x1', deviceId: 'd_A' })
    && !!back5 && sameJson(cancelToasts.map((t) => t.text), ['已要求 OFFICE-PC 取消登入']), { cancelBtns, cancelSent, back: back5, toasts: cancelToasts });

  // ── 檢視不是儀表板時照樣結束：在 app 處理完 accounts 的同一個工作裡取樣（不靠每秒的計時器）
  await clickCell(page, 'la_x1', 'd_A', 'login');
  await buttonsAre('la_x1', 'd_A', ['waiting']);
  await pushCode('la_x1', Date.now() + 15 * MIN);
  await push(patch(accounts, 'la_x1', { loginBusy: true }));
  await buttonsAre('la_x1', 'd_A', ['cancel']);
  const hadCode = await storeLoginHas(page, 'la_x1');
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    window.__raStore = store;
    window.__raSample = null;
    store.state.view = 'chat';
    // 在 app.js 的訂閱者之後登記：傳輸層依序同步呼叫，取樣時 app 已經處理完這一則
    window.myriagentHubTransport.onMessage((m) => {
      if (m && m.type === 'accounts' && window.__raSample === null) window.__raSample = { view: window.__raStore.state.view, login: window.__raStore.state.login.has('la_x1') };
    });
  });
  const t6 = (await toastsOf(page)).length;
  await push(patch(accounts, 'la_x1', { loginBusy: false }, 'd_A', { status: 'ready', statusText: '已登入', email: 'codex@example.test' }));
  const sample = await until(() => page.evaluate(() => window.__raSample), 3000);
  const outside = (await toastsOf(page)).slice(t6);
  await page.evaluate(() => { window.__raStore.state.view = 'dashboard'; });
  check('hubAcctWatchRunsOutsideDashboard', hadCode && !!sample && sample.view === 'chat' && sample.login === false
    && outside.some((t) => t.text === 'OFFICE-PC 已登入「Codex Pro」'), { hadCode, sample, toasts: outside });
  await push(patch(accounts, 'la_x1', {}, 'd_A', { status: 'notLoggedIn', statusText: '尚未登入', email: null }));
  await buttonsAre('la_x1', 'd_A', ['login']);

  // ── 帳戶在登入中、這個分頁沒有追蹤（重新整理之後、別的分頁發起的；審查 F4）：在線的格子多一顆「取消登入」，登入照常；送 accountLoginCancel
  await push(patch(accounts, 'la_x1', { loginBusy: true }));
  const untracked = await buttonsAre('la_x1', 'd_A', ['login', 'cancel']);
  const untrackedOffline = await cellButtons(page, 'la_x1', 'd_B');
  n = hub.cmds('accountLoginCancel').length;
  const tUntracked = (await toastsOf(page)).length;
  await clickCell(page, 'la_x1', 'd_A', 'cancel');
  const untrackedSent = await until(() => cmdAfter(hub, 'accountLoginCancel', n), 3000);
  await sleep(200);
  const untrackedToasts = (await toastsOf(page)).slice(tUntracked).map((t) => t.text);
  await push(patch(accounts, 'la_x1', { loginBusy: false }));
  const untrackedBack = await buttonsAre('la_x1', 'd_A', ['login']);
  check('hubAcctCancelUntrackedLogin', !!untracked && !untracked[0].disabled && !untracked[1].disabled && /帳戶卡/.test(untracked[1].title)
    && !(untrackedOffline || []).some((b) => b.act === 'cancel')
    && sameJson(untrackedSent, { cmd: 'accountLoginCancel', accountId: 'la_x1', deviceId: 'd_A' }) && untrackedToasts.includes('已要求 OFFICE-PC 取消登入') && !!untrackedBack,
    { untracked, offline: untrackedOffline, sent: untrackedSent, toasts: untrackedToasts, back: untrackedBack });

  // ── 別台留下的 loginBusy 一直是 true（帳戶層級是所有電腦的 Any；審查 F2）：目標那一格變 ready 就提示成功、清掉驗證碼，不等 busy 變回 false
  await clickCell(page, 'la_x1', 'd_A', 'login');
  await buttonsAre('la_x1', 'd_A', ['waiting']);
  await pushCode('la_x1', Date.now() + 15 * MIN);
  await push(patch(accounts, 'la_x1', { loginBusy: true }));
  await buttonsAre('la_x1', 'd_A', ['cancel']);
  const tStale = (await toastsOf(page)).length;
  await push(patch(accounts, 'la_x1', { loginBusy: true }, 'd_A', { status: 'ready', statusText: '已登入', email: 'codex@example.test' }));
  // 帳戶層級的 loginBusy 還是 true：帳戶卡照舊顯示「登入中…」（沒有驗證碼），驗證碼已清掉
  const staleCleared = await until(async () => {
    const c = await cardLogin(page, 'la_x1');
    return (!c || c.code === null) && !(await storeLoginHas(page, 'la_x1')) ? true : null;
  }, 3000);
  const staleToasts = (await toastsOf(page)).slice(tStale);
  check('hubAcctLoginStaleBusyElsewhere', !!staleCleared && staleToasts.some((t) => t.text === 'OFFICE-PC 已登入「Codex Pro」' && t.level === 'success')
    && !staleToasts.some((t) => t.level === 'error'), { cleared: !!staleCleared, toasts: staleToasts });
  await push(patch(accounts, 'la_x1', { loginBusy: false }, 'd_A', { status: 'notLoggedIn', statusText: '尚未登入', email: null }));
  await buttonsAre('la_x1', 'd_A', ['login']);

  // ── 登出這台：確認框寫執行中的對話數；取消就不送；確定送 accountLogout {accountId, deviceId}，送出時提示「已要求…」
  const logoutBtns = await cellButtons(page, 'la_c1', 'd_A');
  n = hub.cmds('accountLogout').length;
  await clickCell(page, 'la_c1', 'd_A', 'logout');
  await answerConfirm(page, false);
  await sleep(200);
  const afterCancel = hub.cmds('accountLogout').length;
  const t7 = (await toastsOf(page)).length;
  await clickCell(page, 'la_c1', 'd_A', 'logout');
  const logoutDlg = await answerConfirm(page, true);
  const logoutSent = await until(() => cmdAfter(hub, 'accountLogout', n), 3000);
  await sleep(200);
  const logoutToasts = (await toastsOf(page)).slice(t7).map((t) => t.text);
  check('hubAcctLogoutThis', !!logoutBtns && sameJson(logoutBtns.map((b) => [b.act, b.label, b.disabled]), [['logout', '登出這台', false]]) && afterCancel === n
    && logoutDlg.title.includes('OFFICE-PC') && logoutDlg.text.includes('1 個對話執行中') && logoutDlg.text.includes('預設的設定資料夾')
    && sameJson(logoutSent, { cmd: 'accountLogout', accountId: 'la_c1', deviceId: 'd_A' }), { logoutBtns, afterCancel, dialog: logoutDlg, sent: logoutSent });

  // ── 身分不符：「登出那台」
  const idBtns = await cellButtons(page, 'la_c2', 'd_A');
  n = hub.cmds('accountLogout').length;
  const t8 = (await toastsOf(page)).length;
  await clickCell(page, 'la_c2', 'd_A', 'logout');
  await answerConfirm(page, true);
  const idSent = await until(() => cmdAfter(hub, 'accountLogout', n), 3000);
  await sleep(200);
  const idToasts = (await toastsOf(page)).slice(t8).map((t) => t.text);
  check('hubAcctIdentityLogout', !!idBtns && sameJson(idBtns.map((b) => [b.act, b.label, b.disabled]), [['logout', '登出那台', false]])
    && sameJson(idSent, { cmd: 'accountLogout', accountId: 'la_c2', deviceId: 'd_A' }), { idBtns, sent: idSent, toasts: idToasts });

  // ── 錯誤、檢查中：「重新檢查」（不用確認）；離線的那台停用並寫原因
  const recheckA = await cellButtons(page, 'la_c3', 'd_A');
  const recheckB = await cellButtons(page, 'la_c3', 'd_B');
  n = hub.cmds('accountRefresh').length;
  await clickCell(page, 'la_c3', 'd_A', 'recheck');
  const recheckSent = await until(() => cmdAfter(hub, 'accountRefresh', n), 3000);
  check('hubAcctRecheck', !!recheckA && sameJson(recheckA.map((b) => [b.act, b.label, b.disabled]), [['recheck', '重新檢查', false]])
    && !!recheckB && recheckB[0].disabled && recheckB[0].title === '筆電 ' + RF.XSS + ' 目前離線'
    && sameJson(recheckSent, { cmd: 'accountRefresh', accountId: 'la_c3', deviceId: 'd_A' }), { recheckA, recheckB, sent: recheckSent });

  // ── Claude 未登入：「在那台登入」（確認：網址＋貼回授權碼），不帶 method；送出時提示；帳戶卡寫「在 OFFICE-PC」
  //    那台給了登入網址（loginUrl）：帳戶卡有登入頁的連結與貼授權碼的框，送出 accountLoginCode（2026-10-26）；不允許的網址不給連結
  const claudeBtns = await cellButtons(page, 'la_c6', 'd_A');
  n = hub.cmds('accountLoginDevice').length;
  const t9 = (await toastsOf(page)).length;
  await clickCell(page, 'la_c6', 'd_A', 'login');
  const claudeDlg = await answerConfirm(page, true);
  const claudeSent = await until(() => cmdAfter(hub, 'accountLoginDevice', n), 3000);
  const loginUrl = 'https://claude.com/cai/oauth/authorize?code=true&client_id=x&state=st_1';
  hub.push({ type: 'accountLogin', accountId: 'la_c6', message: '請打開登入網址登入 Claude（可以用這支手機），登入後把頁面上的授權碼貼回這裡', done: false, success: null, loginUrl });
  const claudeCard = await until(async () => { const c = await cardLogin(page, 'la_c6'); return c && c.origin && c.claudeUrl ? c : null; }, 3000);
  const claudeToasts = (await toastsOf(page)).slice(t9).map((t) => t.text);
  check('hubAcctClaudeOpenLogin', !!claudeBtns && sameJson(claudeBtns.map((b) => [b.act, b.label, b.disabled]), [['login', '在那台登入', false]])
    && claudeDlg.text.includes('授權碼貼回帳戶卡') && sameJson(claudeSent, { cmd: 'accountLoginDevice', logicalAccountId: 'la_c6', deviceId: 'd_A' })
    && claudeToasts.includes('已要求 OFFICE-PC 開始「個人 Claude」的登入') && !!claudeCard && claudeCard.origin === '在 OFFICE-PC' && claudeCard.code === null
    && claudeCard.claudeUrl === loginUrl && claudeCard.claudeTarget === '_blank' && claudeCard.hasInput,
    { claudeBtns, dialog: claudeDlg.text, sent: claudeSent, toasts: claudeToasts, card: claudeCard });
  await page.evaluate(() => document.querySelector('#dashboard article.acard[data-key="la_c6"]').scrollIntoView({ block: 'center' }));
  await shot(page, 'hub-acct-claude-login-1440');
  // 空的不送（提示）；貼上授權碼按 Enter 送 accountLoginCode 到那台
  n = hub.cmds('accountLoginCode').length;
  const tCode = (await toastsOf(page)).length;
  await page.click('#dashboard article.acard[data-key="la_c6"] .login-claude .login-code-send');
  const emptyToasts = await until(async () => { const t = (await toastsOf(page)).slice(tCode).map((x) => x.text); return t.length ? t : null; }, 3000);
  const emptySent = cmdAfter(hub, 'accountLoginCode', n);
  await page.fill('#dashboard article.acard[data-key="la_c6"] .login-claude .login-code-input', '  abc123#st_1  ');
  await page.press('#dashboard article.acard[data-key="la_c6"] .login-claude .login-code-input', 'Enter');
  const codeSent = await until(() => cmdAfter(hub, 'accountLoginCode', n), 3000);
  check('hubAcctClaudeLoginCode', !emptySent && !!emptyToasts && emptyToasts.some((t) => t.includes('授權碼'))
    && sameJson(codeSent, { cmd: 'accountLoginCode', logicalAccountId: 'la_c6', deviceId: 'd_A', code: 'abc123#st_1' }),
    { emptyToasts, emptySent, codeSent });
  // 不是 claude.com／claude.ai 的網址：不給連結與輸入框（伺服器也會整則丟掉）
  hub.push({ type: 'accountLogin', accountId: 'la_c6', message: 'x', done: false, success: null, loginUrl: 'https://evil.example/login' });
  const badCard = await until(async () => { const c = await cardLogin(page, 'la_c6'); return c && c.msg === 'x' ? c : null; }, 3000);
  check('hubAcctClaudeLoginUrlRejected', !!badCard && badCard.claudeUrl === null && !badCard.hasInput, { badCard });
  await push(patch(accounts, 'la_c6', { loginBusy: true }));
  await buttonsAre('la_c6', 'd_A', ['cancel']);
  await push(patch(accounts, 'la_c6', { loginBusy: false }, 'd_A', { status: 'ready', statusText: '已登入', email: 'me@example.test' }));
  await until(async () => ((await cardLogin(page, 'la_c6')) === null ? true : null), 3000);

  // ── Claude 登入已到期：「在那台重新登入」（確認）＋「登出這台」
  const expBtns = await cellButtons(page, 'la_c7', 'd_A');
  n = hub.cmds('accountLoginDevice').length;
  await clickCell(page, 'la_c7', 'd_A', 'relogin');
  const expDlg = await answerConfirm(page, true);
  const expSent = await until(() => cmdAfter(hub, 'accountLoginDevice', n), 3000);
  check('hubAcctClaudeExpiredRelogin', !!expBtns && sameJson(expBtns.map((b) => [b.act, b.label, b.disabled]), [['relogin', '在那台重新登入', false], ['logout', '登出這台', false]])
    && expDlg.text.includes('授權碼貼回帳戶卡') && sameJson(expSent, { cmd: 'accountLoginDevice', logicalAccountId: 'la_c7', deviceId: 'd_A' }),
    { expBtns, dialog: expDlg.text, sent: expSent });

  // ── 尚未加入：「在那台建立並登入」（Codex 帶 method）；離線的那台停用
  const absentA = await cellButtons(page, 'la_x3', 'd_A');
  const absentB = await cellButtons(page, 'la_x1', 'd_B');
  n = hub.cmds('accountLoginDevice').length;
  await clickCell(page, 'la_x3', 'd_A', 'create');
  const absentSent = await until(() => cmdAfter(hub, 'accountLoginDevice', n), 3000);
  check('hubAcctAbsentCreate', !!absentA && sameJson(absentA.map((b) => [b.act, b.label, b.disabled]), [['create', '在那台建立並登入', false]])
    && !!absentB && sameJson(absentB.map((b) => [b.act, b.disabled]), [['create', true]]) && absentB[0].title === '筆電 ' + RF.XSS + ' 目前離線'
    && sameJson(absentSent, { cmd: 'accountLoginDevice', logicalAccountId: 'la_x3', deviceId: 'd_A', method: 'deviceCode' }), { absentA, absentB, sent: absentSent });

  // ── 未安裝 CLI：「安裝」（Codex 不用確認；Claude 確認：會在那台開安裝視窗），送 cliInstall {provider, deviceId}
  n = hub.cmds('cliInstall').length;
  const t10 = (await toastsOf(page)).length;
  const instCodex = await cellButtons(page, 'la_x2', 'd_A');
  await clickCell(page, 'la_x2', 'd_A', 'install');
  const instCodexSent = await until(() => cmdAfter(hub, 'cliInstall', n), 3000);
  const n2 = hub.cmds('cliInstall').length;
  await clickCell(page, 'la_c8', 'd_A', 'install');
  const instDlg = await answerConfirm(page, true);
  const instClaudeSent = await until(() => cmdAfter(hub, 'cliInstall', n2), 3000);
  await sleep(200);
  const instToasts = (await toastsOf(page)).slice(t10).map((t) => t.text);
  check('hubAcctInstall', !!instCodex && sameJson(instCodex.map((b) => [b.act, b.label, b.disabled]), [['install', '安裝', false]])
    && sameJson(instCodexSent, { cmd: 'cliInstall', provider: 'Codex', deviceId: 'd_A' }) && instDlg.text.includes('安裝視窗')
    && sameJson(instClaudeSent, { cmd: 'cliInstall', provider: 'Claude', deviceId: 'd_A' })
    && instToasts.includes('已要求 OFFICE-PC 安裝 Codex CLI') && instToasts.includes('已要求 OFFICE-PC 安裝 Claude Code CLI'),
    { instCodex, codex: instCodexSent, claude: instClaudeSent, dialog: instDlg.text, toasts: instToasts });

  // ── API Key：沒有按鈕（那台的 API Key 從網頁不能換成訂閱登入）
  const apiA = await cellButtons(page, 'la_c4', 'd_A');
  const apiB = await cellButtons(page, 'la_c4', 'd_B');
  check('hubAcctApiKeyNoButton', apiA === null && !!apiB && sameJson(apiB.map((b) => [b.act, b.disabled]), [['relogin', true], ['logout', true]]), { apiA, apiB });

  // ── 在所有電腦登出：帳戶選單 → 確認（7 天、預設的設定資料夾、執行中）→ accountLogout 沒有 deviceId；不另外提示（伺服器提示）
  const menu = await openCardMenu(page, 'la_x1');
  n = hub.cmds('accountLogout').length;
  const t11 = (await toastsOf(page)).length;
  let allDlg = null;
  if (menu.some((m) => m.label === '在所有電腦登出…')) {
    await clickMenuItem(page, '在所有電腦登出…');
    await page.waitForSelector('.dlg-back:not(.leaving) .dlg.dlg-ra', { timeout: 3000 }).catch(() => {});
    await shot(page, 'hub-acct-logout-all-1440');
    allDlg = await answerConfirm(page, true);
  } else await closeMenus(page);
  const allSent = await until(() => cmdAfter(hub, 'accountLogout', n), 3000);
  await sleep(300);
  const allToasts = (await toastsOf(page)).slice(t11).map((t) => t.text);
  check('hubAcctLogoutAllConfirm', !!allDlg && allDlg.title.includes('Codex Pro') && allDlg.text.includes('7 天') && allDlg.text.includes('預設的設定資料夾') && allDlg.text.includes('執行中')
    && sameJson(allSent, { cmd: 'accountLogout', accountId: 'la_x1' }), { menu: menu.map((m) => m.label), dialog: allDlg, sent: allSent });
  check('hubAcctSentToast', logoutToasts.includes('已要求 OFFICE-PC 登出「工作用 Max」') && !allToasts.some((t) => t.startsWith('已要求')),
    { single: logoutToasts, all: allToasts });

  // ── 各電腦 CLI：卡頭「立即檢查」→ cliCheck {deviceId}、檢查中；推 checkedAt 變了的 devicesCli → 提示結果、恢復
  const cli0 = await cliButtons(page, 'd_A');
  n = hub.cmds('cliCheck').length;
  await page.click('#dashboard .dcli[data-device="d_A"] .dcli-head .ra-acts button');
  const checkSent = await until(() => cmdAfter(hub, 'cliCheck', n), 3000);
  const checking = await until(async () => { const c = await cliButtons(page, 'd_A'); return c && c.head && c.head.label === '檢查中…' ? c : null; }, 3000);
  const t12 = (await toastsOf(page)).length;
  const bumped = JSON.parse(JSON.stringify(devicesCli));
  for (const p of Object.keys(bumped.d_A)) bumped.d_A[p].checkedAt = Date.now();
  hub.push({ type: 'cli', cli: {}, devicesCli: bumped });
  const doneBtn = await until(async () => { const c = await cliButtons(page, 'd_A'); return c && c.head && c.head.label === '立即檢查' && !c.head.disabled ? c : null; }, 4000);
  const checkToasts = (await toastsOf(page)).slice(t12);
  const cliCheck = {
    ok: !!cli0 && !!cli0.head && cli0.head.label === '立即檢查' && !cli0.head.disabled && /自動更新/.test(cli0.head.title)
      && sameJson(checkSent, { cmd: 'cliCheck', deviceId: 'd_A' }) && !!checking && checking.head.disabled && !!doneBtn
      && checkToasts.some((t) => t.text === 'OFFICE-PC 檢查完成：Claude Code 尚未安裝；Codex 有新版 0.51.0'),
    cli0, sent: checkSent, checking: !!checking, done: !!doneBtn, toasts: checkToasts,
  };

  // ── 各電腦 CLI 的「更新」（確認：安全點、要在那台選擇）與「安裝」（Claude 確認：安裝視窗）；離線的那台停用
  const rowsA = (await cliButtons(page, 'd_A')).rows;
  const rowsB = (await cliButtons(page, 'd_B')).rows;
  n = hub.cmds('cliUpdate').length;
  const t13 = (await toastsOf(page)).length;
  await page.click('#dashboard .dcli[data-device="d_A"] .dcli-row[data-provider="Codex"] .ra-acts button');
  const updDlg = await answerConfirm(page, true);
  const updSent = await until(() => cmdAfter(hub, 'cliUpdate', n), 3000);
  const n3 = hub.cmds('cliInstall').length;
  await page.click('#dashboard .dcli[data-device="d_A"] .dcli-row[data-provider="Claude"] .ra-acts button');
  const cliInstDlg = await answerConfirm(page, true);
  const cliInstSent = await until(() => cmdAfter(hub, 'cliInstall', n3), 3000);
  await sleep(200);
  const updToasts = (await toastsOf(page)).slice(t13).map((t) => t.text);
  check('hubAcctDeviceCliUpdateInstall', !!rowsA.Codex && rowsA.Codex.act === 'update' && !!rowsA.Claude && rowsA.Claude.act === 'install'
    && updDlg.text.includes('安全點停下') && updDlg.text.includes('要在那台選擇') && sameJson(updSent, { cmd: 'cliUpdate', provider: 'Codex', deviceId: 'd_A' })
    && cliInstDlg.text.includes('安裝視窗') && sameJson(cliInstSent, { cmd: 'cliInstall', provider: 'Claude', deviceId: 'd_A' })
    && updToasts.includes('已要求 OFFICE-PC 更新 Codex CLI') && !!rowsB.Claude && rowsB.Claude.disabled && rowsB.Claude.title === '筆電 ' + RF.XSS + ' 目前離線',
    { rowsA, rowsB, update: updSent, install: cliInstSent, toasts: updToasts, updateDialog: updDlg.text, installDialog: cliInstDlg.text });

  // ── 不可信的字串：帳戶名、電腦名、statusText 照字面（確認框的標題、提示、停用原因），沒有執行、沒有新元素
  await openCardMenu(page, 'la_c2');
  await clickMenuItem(page, '在所有電腦登出…');
  const xssDlg = await answerConfirm(page, false);
  const xss = await page.evaluate(() => ({ flag: typeof window.__xss, imgs: document.querySelectorAll('img[src="x"]').length }));
  const allText = (await toastsOf(page)).map((t) => t.text);
  check('hubAcctXss', xss.flag === 'undefined' && xss.imgs === 0 && xssDlg.title === `在所有電腦登出「XSS ${RF.XSS}」？`
    && idToasts.includes(`已要求 OFFICE-PC 登出「XSS ${RF.XSS}」`) && recheckB[0].title === '筆電 ' + RF.XSS + ' 目前離線' && allText.length > 0,
    { xss, dialogTitle: xssDlg.title, idToasts });
  await closePage(app);
  return cliCheck;
}

/** 假時鐘（Date.now 跳到之後，計時器照常跑）：送出 60 秒都沒看到那台開始登入 → 當成被拒（不另外提示）；遠端「立即檢查」3 分鐘逾時恢復 */
async function runClock(ctx) {
  const { openHub, waitLive, check, closePage, until, sleep, RF, NOW } = ctx;
  const accounts = controlAccounts(RF, NOW);
  const devices = controlDevices(RF, NOW);
  const devicesCli = controlDevicesCli(RF, NOW);
  const app = await openHub('hub-acct-clock', { hub: (hub) => hub.onReady(() => RF.browserInit(NOW, { accounts, devices, devicesCli })) });
  const { page, hub } = app;
  await waitLive(app);
  await watchToasts(page);
  await clickCell(page, 'la_x1', 'd_A', 'login');
  const waiting = await until(async () => { const b = await cellButtons(page, 'la_x1', 'd_A'); return b && b[0].act === 'waiting' ? b : null; }, 3000);
  await page.click('#dashboard .dcli[data-device="d_A"] .dcli-head .ra-acts button');
  await until(async () => { const c = await cliButtons(page, 'd_A'); return c && c.head && c.head.label === '檢查中…' ? c : null; }, 3000);
  const t0 = (await toastsOf(page)).length;
  const base = await page.evaluate(() => Date.now());
  await page.clock.setFixedTime(base + 61_000);
  const rejected = await until(async () => { const b = await cellButtons(page, 'la_x1', 'd_A'); return b && b[0].act === 'login' && !b[0].disabled ? b : null; }, 5000);
  const stillChecking = await cliButtons(page, 'd_A');
  const rejToasts = (await toastsOf(page)).slice(t0);
  check('hubAcctLoginRejectedAfter60s', !!waiting && !!rejected && rejToasts.length === 0 && hub.cmds('accountLoginDevice').length === 1
    && !!stillChecking && stillChecking.head.label === '檢查中…', { waiting, rejected, toasts: rejToasts, checking: stillChecking && stillChecking.head });
  await page.clock.setFixedTime(base + 182_000);
  const restored = await until(async () => { const c = await cliButtons(page, 'd_A'); return c && c.head && c.head.label === '立即檢查' && !c.head.disabled ? c : null; }, 5000);
  await sleep(300);
  const cliToasts = (await toastsOf(page)).slice(t0).map((t) => t.text);
  await closePage(app);
  return { cliTimeoutOk: !!restored && !cliToasts.some((t) => t.includes('檢查完成')), cli: { restored: !!restored, toasts: cliToasts } };
}

/**
 * 步 12：WP4.6 的指令結果（cmdtrack）與 post 的回傳值。被拒（伺服器推的 commandResult、傳輸層合成的）立刻結束、不另提示；
 * 新的 init（結果不明）不結束、之後照格子判斷；沒有連線時 post 回 null：不記追蹤、不顯示「已要求…」
 */
async function runResults(ctx) {
  const { openHub, waitLive, check, closePage, until, sleep, RF, NOW } = ctx;
  let accounts = controlAccounts(RF, NOW);
  const devices = controlDevices(RF, NOW);
  const devicesCli = controlDevicesCli(RF, NOW);
  const init = () => RF.browserInit(NOW, { accounts, devices, devicesCli });
  const app = await openHub('hub-acct-results', { hub: (hub) => hub.onReady(() => init()) });
  const { page, hub } = app;
  await waitLive(app);
  await watchToasts(page);
  const push = async (list) => { accounts = list; hub.push({ type: 'accounts', accounts }); await sleep(120); };
  const buttonsAre = (id, dev, acts) => until(async () => { const b = await cellButtons(page, id, dev); return b && sameJson(b.map((x) => x.act), acts) ? b : null; }, 4000);

  // ── 被拒（伺服器推 commandResult rejected，400 ms 後）：「登入中…」立刻恢復成「用裝置碼登入」，網頁自己不提示（伺服器另送的提示這裡不推）
  hub.onCmd('accountLoginDevice', (cmd, arg) => ({
    push: [{ type: 'commandResult', commandId: arg.commandId, state: 'rejected', code: 'conflict', message: '那台正在登入另一個帳戶，請等它完成後再試', tier: null }],
    delayMs: 400,
  }));
  const t0 = (await toastsOf(page)).length;
  const n0 = hub.cmds('accountLoginDevice').length;
  const at0 = Date.now();
  await clickCell(page, 'la_x1', 'd_A', 'login');
  const waiting = await until(async () => { const b = await cellButtons(page, 'la_x1', 'd_A'); return b && b[0].act === 'waiting' ? b : null; }, 3000, 20);
  const back = await until(async () => { const b = await cellButtons(page, 'la_x1', 'd_A'); return b && b[0].act === 'login' && !b[0].disabled ? b : null; }, 5000, 20);
  const backMs = Date.now() - at0;
  await sleep(300);
  const rejToasts = (await toastsOf(page)).slice(t0);
  const sentFrame = hub.cmds('accountLoginDevice').slice(n0);
  check('hubAcctLoginRejectedImmediate', !!waiting && !!back && backMs < 5000 && rejToasts.length === 0 && sentFrame.length === 1
    && typeof sentFrame[0].commandId === 'string' && !(await storeLoginHas(page, 'la_x1')),
    { waiting: !!waiting, back, backMs, toasts: rejToasts, frames: sentFrame.length });
  hub.onCmd('accountLoginDevice', null);

  // ── 遠端「立即檢查」的 Cmd 被伺服器拒絕（傳輸層合成 rejected）：按鈕立刻恢復成「立即檢查」，不等 3 分鐘、不提示「檢查完成」
  hub.rejectNext('cliCheck', 'conflict');
  const t1 = (await toastsOf(page)).length;
  const c0 = hub.cmds('cliCheck').length;
  const at1 = Date.now();
  await page.click('#dashboard .dcli[data-device="d_A"] .dcli-head .ra-acts button');
  const sentCheck = await until(() => (hub.cmds('cliCheck').length > c0 ? true : null), 3000);
  await sleep(150);
  const restored = await until(async () => { const c = await cliButtons(page, 'd_A'); return c && c.head && c.head.label === '立即檢查' && !c.head.disabled ? c.head : null; }, 3000, 20);
  const restoredMs = Date.now() - at1;
  const checkToasts = (await toastsOf(page)).slice(t1).map((t) => t.text);
  check('hubAcctCliCheckRejectedImmediate', !!sentCheck && !!restored && restoredMs < 4000 && !checkToasts.some((t) => t.includes('檢查完成')),
    { sent: !!sentCheck, restored, restoredMs, toasts: checkToasts });

  // ── 結果不明（第二則 init：伺服器 resync、重連；cmdtrack 把還在等的當成 unknown）：「登入中…」不結束；之後那台開始登入、完成 → 照格子判斷成功
  const t2 = (await toastsOf(page)).length;
  await clickCell(page, 'la_x1', 'd_A', 'login');
  const waiting2 = await buttonsAre('la_x1', 'd_A', ['waiting']);
  await page.evaluate(() => {
    window.__raInits = 0;
    // 比 app.js 晚訂閱：同一則 init 依序同步處理，計數加一時 app 已經處理完（含 cmdtrack.abandonAll）
    window.myriagentHubTransport.onMessage((m) => { if (m && m.type === 'init') window.__raInits++; });
  });
  hub.push(init());
  const reinit = await until(() => page.evaluate(() => (window.__raInits > 0 ? true : null)), 3000);
  await sleep(500);
  const still = await cellButtons(page, 'la_x1', 'd_A');
  await push(patch(accounts, 'la_x1', { loginBusy: true }));
  const busyBtns = await buttonsAre('la_x1', 'd_A', ['cancel']);
  await push(patch(accounts, 'la_x1', { loginBusy: false }, 'd_A', { status: 'ready', statusText: '已登入', email: 'codex@example.test' }));
  const done = await buttonsAre('la_x1', 'd_A', ['logout']);
  await sleep(200);
  const unkToasts = (await toastsOf(page)).slice(t2);
  check('hubAcctLoginUnknownKeepsWatching', !!waiting2 && !!reinit && !!still && sameJson(still.map((b) => b.act), ['waiting']) && !!busyBtns && !!done
    && unkToasts.some((t) => t.text === 'OFFICE-PC 已登入「Codex Pro」' && t.level === 'success'),
    { waiting: !!waiting2, reinit: !!reinit, still, busy: !!busyBtns, done: !!done, toasts: unkToasts });

  // ── 沒有連線時 post 回 null：斷線時儀表板是 inert（按不到格子），會發生的是確認框開著時斷線（框不在儀表板裡）。
  // 新連線一直等不到 init（holdInit）＝連著但不可送：按確定之後傳輸層提示一次，格子不進「登入中…」、不提示「已要求…」、沒有送出任何框
  const offline = async (label, open, after) => {
    await open();
    await page.waitForSelector('.dlg-back:not(.leaving) .dlg.dlg-ra', { timeout: 3000 });
    hub.holdInit(Infinity);
    hub.drop();
    const down = await until(() => page.evaluate(() => {
      const st = window.myriagentHubTransport.status();
      return st && (st.state !== 'online' || st.hubInit !== true) ? Object.assign({}, st) : null;
    }), 5000, 30);
    const tN = (await toastsOf(page)).length;
    const fN = hub.cmds().length;
    const dlg = await answerConfirm(page, true);
    await sleep(400);
    const state = await after();
    const toasts = (await toastsOf(page)).slice(tN).map((t) => t.text);
    const frames = hub.cmds().slice(fN).map((a) => a.cmd && a.cmd.cmd).filter((c) => c !== 'ready' && c !== 'visible');
    // 恢復連線：再斷一次（holdInit 只擋一次 ready），下一條照常收到 init
    hub.drop();
    const live = await waitLive(app, 8000);
    return { label, down: !!down && down.hubInit !== true, dialog: !!dlg, state, toasts, frames, live: !!live };
  };
  const claude = await offline('Claude 開登入視窗', () => clickCell(page, 'la_c6', 'd_A', 'login'),
    async () => ({ buttons: await cellButtons(page, 'la_c6', 'd_A'), code: await storeLoginHas(page, 'la_c6'), origin: (await cardLogin(page, 'la_c6')) }));
  const update = await offline('Codex CLI 更新', () => page.click('#dashboard .dcli[data-device="d_A"] .dcli-row[data-provider="Codex"] .ra-acts button'),
    async () => ({ row: (await cliButtons(page, 'd_A')).rows.Codex }));
  // 傳輸層的「目前沒有連線…」10 秒內只提示一次（WP3.5）：第二輪不要求那一則
  const okRound = (r, offlineToast) => r.down && r.dialog && r.live && (!offlineToast || r.toasts.includes(TEXT_OFFLINE))
    && !r.toasts.some((t) => t.startsWith('已要求')) && r.frames.length === 0;
  check('hubAcctPostNullNoPending', okRound(claude, true) && !!claude.state.buttons && sameJson(claude.state.buttons.map((b) => b.act), ['login'])
    && claude.state.code === false && claude.state.origin === null
    && okRound(update, false) && !!update.state.row && update.state.row.act === 'update' && !update.state.row.disabled,
    { claude, update });
  await closePage(app);
}

/** 原夾具（OFFICE-PC 不接受遠端操作、筆電離線）：按鈕都停用並寫原因（和 WP4.6 同一句），按了也不送；「在所有電腦登出」不看政策 */
async function runPolicyOff(ctx) {
  const { openHub, waitLive, check, closePage, until, sleep, openCardMenu, closeMenus, RF } = ctx;
  const app = await openHub('hub-acct-policy');
  const { page, hub } = app;
  await waitLive(app);
  await sleep(200);
  const cols = await page.evaluate(() => {
    const read = (sel) => [...document.querySelectorAll(sel)].map((b) => ({ act: b.dataset.act, disabled: b.disabled, title: b.title }));
    return {
      a: read('#dashboard .mx td.mx-cell[data-device="d_A"] .ra-acts button'),
      b: read('#dashboard .mx td.mx-cell[data-device="d_B"] .ra-acts button'),
      cliA: read('#dashboard .dcli[data-device="d_A"] .ra-acts button'),
    };
  });
  const sent0 = hub.cmds().length;
  // 停用的按鈕點了不會觸發（直接呼叫 click() 也一樣）
  await page.evaluate(() => { for (const b of document.querySelectorAll('#dashboard .ra-acts button')) b.click(); });
  await sleep(400);
  const sentAfter = hub.cmds().slice(sent0).map((a) => a.cmd.cmd).filter((c) => c !== 'visible');
  const menu = await openCardMenu(page, 'la_x1');
  await closeMenus(page);
  const all = menu.find((m) => m.label === '在所有電腦登出…');
  check('hubAcctPolicyDisabled', cols.a.length >= 5 && cols.a.every((b) => b.disabled && b.title === POLICY_REASON)
    && cols.b.length >= 3 && cols.b.every((b) => b.disabled && b.title === '筆電 ' + RF.XSS + ' 目前離線')
    && cols.cliA.length >= 1 && cols.cliA.every((b) => b.disabled && b.title === POLICY_REASON) && sentAfter.length === 0 && !!all && !all.disabled,
    { cols, sentAfter, menu });
  await closePage(app);
}

/** 手機寬（390）：多了按鈕之後，除了矩陣自己的水平捲動框，頁面不水平捲動 */
async function runPhone(ctx) {
  const { openHub, waitLive, check, closePage, sleep, shot, RF, NOW } = ctx;
  const accounts = controlAccounts(RF, NOW);
  const devices = controlDevices(RF, NOW);
  const devicesCli = controlDevicesCli(RF, NOW);
  const app = await openHub('hub-acct-390', { vp: { w: 390, h: 844 }, hub: (hub) => hub.onReady(() => RF.browserInit(NOW, { accounts, devices, devicesCli })) });
  const { page } = app;
  await waitLive(app);
  await sleep(300);
  const m = await page.evaluate(() => {
    const dash = document.getElementById('dashboard');
    const wide = [...document.querySelectorAll('body *')].filter((el) => {
      if (el.closest('.mx-wrap')) return false;
      const r = el.getBoundingClientRect();
      return r.width > 0 && (r.right > window.innerWidth + 0.5 || r.left < -0.5);
    }).slice(0, 8).map((el) => el.className || el.tagName);
    return {
      docScroll: document.documentElement.scrollWidth, innerWidth: window.innerWidth, dashScroll: dash.scrollWidth, dashClient: dash.clientWidth,
      buttons: document.querySelectorAll('#dashboard .ra-acts button').length, wide,
    };
  });
  await page.evaluate(() => document.querySelector('#dashboard .dash-sec-mx').scrollIntoView());
  await shot(page, 'hub-acct-matrix-390');
  await page.evaluate(() => document.querySelector('#dashboard .dash-sec-dcli').scrollIntoView());
  await shot(page, 'hub-acct-dcli-390');
  check('hubAcct390NoHScroll', m.docScroll <= m.innerWidth && m.dashScroll <= m.dashClient && m.wide.length === 0 && m.buttons > 10, m);
  await closePage(app);
}

/** 桌面：矩陣與各電腦 CLI 沒有遠端按鈕（桌面不能送 accountLoginDevice），Codex 未登入格的提示是新文字，帳戶選單沒有「在所有電腦登出…」 */
async function runDesktop(ctx) {
  const { openDesktop, showDashboard, check, closePage, sleep, openCardMenu, closeMenus, readMatrix, RF, NOW } = ctx;
  const accounts = RF.desktopAccounts(NOW).map((a) => (a.id === 'a2'
    ? Object.assign({}, a, { devices: a.devices.map((c) => (c.deviceId === 'd_B' ? Object.assign({}, c, { status: 'notLoggedIn', statusText: '尚未登入' }) : c)) })
    : a));
  const app = await openDesktop('desktop-acct', { init: RF.desktopInit(NOW, { remote: RF.desktopRemote(NOW), devicesCli: RF.browserDevicesCli(NOW), accounts }) });
  const { page } = app;
  await showDashboard(page);
  await sleep(200);
  const mx = await readMatrix(page);
  const acts = await page.evaluate(() => document.querySelectorAll('#dashboard .ra-acts').length);
  const a2 = mx.rows.find((r) => r.id === 'a2');
  const menu = (await openCardMenu(page, 'a1')).map((m) => m.label);
  await closeMenus(page);
  check('desktopNoRemoteButtons', mx.rows.length > 0 && acts === 0 && !!a2 && a2.cells[1].kind === 'notLoggedIn' && a2.cells[1].hint === '到 筆電 ' + RF.XSS + ' 登入，或用裝置碼從網頁登入'
    && !menu.includes('在所有電腦登出…'), { acts, a2: a2 && a2.cells, menu });
  await closePage(app);
}
