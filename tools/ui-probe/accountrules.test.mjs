// 網頁版遠端帳戶與 CLI 操作的純規則（WP4.8 步 2，src/Myriagent/Web/accountrules.js）的單元測試（node:test，不開瀏覽器）：
// (cd tools/ui-probe && node --test accountrules.test.mjs)
// 規則模組沒有 import，以 data: URL 匯入（同 rules.test.mjs）；每次匯入尾端加不同的註解，避開 module 快取。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
const webDir = path.join(repo, 'src', 'Myriagent', 'Web');

let importSeq = 0;
/** 以 data: URL 匯入 src/Myriagent/Web 底下的模組（沒有 import 的規則模組才能這樣做） */
async function importWeb(name) {
  const source = readFileSync(path.join(webDir, name), 'utf8') + `\n// accountrules.test ${++importSeq}\n`;
  return import('data:text/javascript;base64,' + Buffer.from(source, 'utf8').toString('base64'));
}

const A = await importWeb('accountrules.js');
const R = await importWeb('remoterules.js');
const L = await importWeb('liverules.js');

const NOW = 1_791_580_000_000;
const MIN = 60_000;
const DAY = 24 * 60 * MIN;
const XSS = '<img src=x onerror=window.__xss=1>';
/** 瀏覽器版 post 回的 commandId（ULID 形狀；步 12） */
const CID = '01JZ0000000000000000000001';

/** 可以操作的電腦（在線、Control） */
const devA = (extra) => Object.assign({ id: 'd_A', name: 'OFFICE-PC', online: true, remoteControlLevel: 'Control', allowRemoteFullAccess: false, revoked: null }, extra);
const ctxOf = (provider, extra) => Object.assign({ provider, accountId: 'la_x1', deviceId: 'd_A', deviceName: 'OFFICE-PC', device: devA(), pendingHere: false, loginBusy: false, running: 0 }, extra);
/** 伺服器的格子（AccountDeviceView）經 remoterules.cellView */
const viewOf = (status, extra, provider = 'Codex') => R.cellView(Object.assign({ deviceId: 'd_A', deviceName: 'OFFICE-PC', status, statusText: '', identityOk: true, online: true }, extra), devA(), NOW, { provider });
const cmds = (actions) => actions.map((a) => a.cmd);

// ───── remoteAllowed ─────

test('remoteAllowed_在線而且Control才可以_原因逐字', () => {
  assert.deepEqual(A.remoteAllowed(devA()), { ok: true, reason: null });
  assert.deepEqual(A.remoteAllowed(devA({ online: false })), { ok: false, reason: 'OFFICE-PC 目前離線' });
  assert.deepEqual(A.remoteAllowed(devA({ online: false, reconnecting: true })), { ok: false, reason: 'OFFICE-PC 重新連線中' }, '剛斷線：照樣擋，文字不同');
  assert.deepEqual(A.remoteAllowed(devA({ remoteControlLevel: 'View' })), { ok: false, reason: '在 OFFICE-PC 的設定中允許遠端操作' });
  assert.deepEqual(A.remoteAllowed(devA({ remoteControlLevel: 'Off' })), { ok: false, reason: '在 OFFICE-PC 的設定中允許遠端操作' });
  assert.deepEqual(A.remoteAllowed(devA({ remoteControlLevel: undefined })), { ok: false, reason: '在 OFFICE-PC 的設定中允許遠端操作' }, '沒有政策當成 Off');
  assert.equal(A.remoteAllowed(devA({ online: false, remoteControlLevel: 'Off' })).reason, 'OFFICE-PC 目前離線', '離線先於政策');
  assert.equal(A.remoteAllowed(devA({ revoked: { at: NOW, reason: 'retired' } })).ok, false, '已撤銷');
  assert.equal(A.remoteAllowed(null).ok, false, '不知道是哪台');
  assert.equal(A.remoteAllowed(devA({ online: 'yes' })).ok, false, 'online 一定要是 true');
});

test('remoteAllowed的政策說明和liverules相同', () => {
  // WP4.6 的送出閘門與卡片閘門（P46 D14 第 4 點）和本包的停用說明是同一句（計畫 D24、A13）：改了其中一邊的字就紅。
  // 兩邊以同一台電腦（在線、政策不是 Control）各自組自己要的輸入
  for (const level of ['Off', 'View']) {
    for (const name of ['OFFICE-PC', '筆電 ' + XSS]) {
      const device = devA({ name, remoteControlLevel: level });
      const mine = A.remoteAllowed(device);
      const dev = L.deviceOf(device.id, { mode: 'browser', devices: [device] }, null);
      const composer = L.composerGate({ mode: 'hub', origin: 'remote', header: null, device: dev, live: true, uploading: false });
      const card = L.cardGate({ mode: 'hub', origin: 'remote', device: dev, live: true });
      const what = `${level}／${name}`;
      assert.equal(mine.ok, false, what);
      assert.equal(composer.canSend, false, what);
      assert.equal(card.enabled, false, what);
      assert.equal(mine.reason, composer.reason, what + '：送出閘門');
      assert.equal(mine.reason, card.reason, what + '：卡片閘門');
    }
  }
  // 對照：同一台改成 Control 兩邊都放行（比到的確實是政策那一條）
  const ok = devA();
  const dev = L.deviceOf(ok.id, { mode: 'browser', devices: [ok] }, null);
  assert.equal(A.remoteAllowed(ok).ok, true);
  assert.equal(L.composerGate({ mode: 'hub', origin: 'remote', header: null, device: dev, live: true, uploading: false }).canSend, true);
  assert.equal(L.cardGate({ mode: 'hub', origin: 'remote', device: dev, live: true }).enabled, true);
});

// ───── cellActions ─────

test('cellActions_每種kind的按鈕與指令', () => {
  const rows = [
    // [名稱, view, provider, 期望的 key, 期望的 cmd]
    ['ready', viewOf('ready'), 'Codex', ['logout'], [{ cmd: 'accountLogout', accountId: 'la_x1', deviceId: 'd_A' }]],
    ['ready即將到期', viewOf('ready', { loginExpiresAt: NOW + DAY }, 'Claude'), 'Claude', ['logout'], [{ cmd: 'accountLogout', accountId: 'la_x1', deviceId: 'd_A' }]],
    ['notLoggedIn_Codex', viewOf('notLoggedIn'), 'Codex', ['login'],
      [{ cmd: 'accountLoginDevice', logicalAccountId: 'la_x1', deviceId: 'd_A', method: 'deviceCode' }]],
    ['notLoggedIn_Claude', viewOf('notLoggedIn', {}, 'Claude'), 'Claude', ['login'], [{ cmd: 'accountLoginDevice', logicalAccountId: 'la_x1', deviceId: 'd_A' }]],
    ['absent_Codex', viewOf('absent'), 'Codex', ['create'], [{ cmd: 'accountLoginDevice', logicalAccountId: 'la_x1', deviceId: 'd_A', method: 'deviceCode' }]],
    ['absent_Claude', viewOf('absent', {}, 'Claude'), 'Claude', ['create'], [{ cmd: 'accountLoginDevice', logicalAccountId: 'la_x1', deviceId: 'd_A' }]],
    ['notInstalled_Codex', viewOf('notInstalled'), 'Codex', ['install'], [{ cmd: 'cliInstall', provider: 'Codex', deviceId: 'd_A' }]],
    ['notInstalled_Claude', viewOf('notInstalled', {}, 'Claude'), 'Claude', ['install'], [{ cmd: 'cliInstall', provider: 'Claude', deviceId: 'd_A' }]],
    ['error', viewOf('error', { statusText: 'CLI 跑不起來' }), 'Codex', ['recheck'], [{ cmd: 'accountRefresh', accountId: 'la_x1', deviceId: 'd_A' }]],
    ['checking', viewOf('checking'), 'Claude', ['recheck'], [{ cmd: 'accountRefresh', accountId: 'la_x1', deviceId: 'd_A' }]],
    ['identity', viewOf('ready', { identityOk: false, email: 'other@example.test' }), 'Codex', ['logout'], [{ cmd: 'accountLogout', accountId: 'la_x1', deviceId: 'd_A' }]],
    ['apiKey', viewOf('apiKey'), 'Claude', [], []],
    ['不認得的kind', { kind: 'weird' }, 'Codex', [], []],
  ];
  for (const [name, view, provider, keys, expected] of rows) {
    const actions = A.cellActions(view, ctxOf(provider));
    assert.deepEqual(actions.map((a) => a.key), keys, name);
    assert.deepEqual(cmds(actions), expected, name);
    for (const a of actions) {
      assert.equal(a.cmd.deviceId, 'd_A', name + '：一定帶 deviceId');
      if (a.cmd.cmd === 'accountLoginDevice' && provider === 'Codex') assert.equal(a.cmd.method, 'deviceCode', name + '：Codex 一定帶 method');
      if (a.cmd.cmd === 'accountLoginDevice' && provider === 'Claude') assert.equal('method' in a.cmd, false, name + '：Claude 不帶 method');
      assert.equal(a.disabled, false, name);
      assert.equal(a.reason, null, name);
      assert.equal(typeof a.label, 'string', name);
    }
  }
  // 標籤與確認框
  const [logout] = A.cellActions(viewOf('ready'), ctxOf('Codex', { running: 3 }));
  assert.equal(logout.label, '登出這台');
  assert.match(logout.confirm.title, /OFFICE-PC/);
  assert.match(logout.confirm.detail, /3 個對話執行中（所有電腦合計）/, '登出寫明執行中的對話數');
  assert.equal(A.cellActions(viewOf('ready', { identityOk: false }), ctxOf('Codex'))[0].label, '登出那台');
  assert.equal(A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex'))[0].label, '用裝置碼登入');
  assert.equal(A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex'))[0].confirm, null, '裝置碼登入不用確認');
  const claudeLogin = A.cellActions(viewOf('notLoggedIn', {}, 'Claude'), ctxOf('Claude'))[0];
  assert.equal(claudeLogin.label, '在那台登入');
  assert.match(claudeLogin.confirm.message, /授權碼貼回帳戶卡/, 'Claude 的遠端登入：網址＋貼回授權碼（2026-10-26）');
  assert.match(claudeLogin.confirm.detail, /要有人在那台電腦前完成/, 'CLI 太舊時退回登入視窗');
  assert.equal(A.cellActions(viewOf('absent'), ctxOf('Codex'))[0].label, '在那台建立並登入');
  assert.match(A.cellActions(viewOf('notInstalled', {}, 'Claude'), ctxOf('Claude'))[0].confirm.message, /安裝視窗/, 'Claude 的安裝要確認');
  assert.equal(A.cellActions(viewOf('notInstalled'), ctxOf('Codex'))[0].confirm, null, 'Codex 的安裝不用確認');
});

test('cellActions_離線或政策不是Control_停用並寫原因', () => {
  for (const [device, reason] of [
    [devA({ online: false }), 'OFFICE-PC 目前離線'],
    [devA({ remoteControlLevel: 'Off' }), '在 OFFICE-PC 的設定中允許遠端操作'],
    [devA({ remoteControlLevel: 'View' }), '在 OFFICE-PC 的設定中允許遠端操作'],
  ]) {
    for (const [view, provider] of [[viewOf('ready'), 'Codex'], [viewOf('notLoggedIn'), 'Codex'], [viewOf('absent', {}, 'Claude'), 'Claude'], [viewOf('notInstalled'), 'Codex'], [viewOf('error'), 'Codex']]) {
      const actions = A.cellActions(view, ctxOf(provider, { device }));
      assert.ok(actions.length > 0, view.kind);
      for (const a of actions) {
        assert.equal(a.disabled, true, view.kind);
        assert.equal(a.reason, reason, view.kind);
        assert.ok(a.cmd && a.cmd.deviceId === 'd_A', '停用的按鈕仍帶指令（畫面只是不讓按）');
      }
    }
  }
});

test('cellActions_Claude登入已到期_有重新登入', () => {
  const view = viewOf('ready', { loginExpiresAt: NOW - MIN }, 'Claude');
  assert.equal(view.expiring, 'expired');
  const actions = A.cellActions(view, ctxOf('Claude'));
  assert.deepEqual(actions.map((a) => [a.key, a.label]), [['relogin', '在那台重新登入'], ['logout', '登出這台']]);
  assert.deepEqual(actions[0].cmd, { cmd: 'accountLoginDevice', logicalAccountId: 'la_x1', deviceId: 'd_A' });
  assert.match(actions[0].confirm.message, /授權碼貼回帳戶卡/);
});

test('cellActions_這個分頁發起的登入_換成取消登入', () => {
  const busy = A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { pendingHere: true, loginBusy: true }));
  assert.deepEqual(busy.map((a) => a.key), ['cancel']);
  assert.equal(busy[0].label, '取消登入');
  assert.deepEqual(busy[0].cmd, { cmd: 'accountLoginCancel', accountId: 'la_x1', deviceId: 'd_A' });
  const waiting = A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { pendingHere: true, loginBusy: false }));
  assert.equal(waiting.length, 1);
  assert.equal(waiting[0].cmd, null, '那台還沒開始時不給會送出的按鈕');
  assert.equal(waiting[0].disabled, true);
  // 不是這個分頁發起的：登入照常（另外多一顆取消登入，見下一個測試）
  assert.deepEqual(A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { pendingHere: false, loginBusy: true })).map((a) => a.key), ['login', 'cancel']);
});

test('cellActions_帳戶在登入中但這個分頁沒有追蹤_在線的格子多一顆取消登入_登入照常', () => {
  // 審查 F4：重新整理之後、別的分頁、那台本機發起的登入，網頁上也要能取消；loginBusy 是帳戶層級（看不出是哪台）
  const acts = A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { loginBusy: true }));
  assert.deepEqual(acts.map((a) => [a.key, a.disabled]), [['login', false], ['cancel', false]], '登入不因帳戶層級的 busy 停用（離線電腦留下的 busy 會讓每一格都不能登入）');
  const cancel = acts[1];
  assert.equal(cancel.label, '取消登入');
  assert.deepEqual(cancel.cmd, { cmd: 'accountLoginCancel', accountId: 'la_x1', deviceId: 'd_A' });
  assert.equal(cancel.confirm, null);
  assert.match(cancel.hint, /OFFICE-PC 沒有在登入時什麼都不做/);
  assert.match(cancel.hint, /帳戶卡/);
  // 已登入、到期、錯誤的格子也可能在登入（本機重新登入、Claude 重新登入）：都多一顆
  assert.deepEqual(A.cellActions(viewOf('ready'), ctxOf('Codex', { loginBusy: true })).map((a) => a.key), ['logout', 'cancel']);
  assert.deepEqual(A.cellActions(viewOf('ready', { loginExpiresAt: NOW - MIN }, 'Claude'), ctxOf('Claude', { loginBusy: true })).map((a) => a.key), ['relogin', 'logout', 'cancel']);
  assert.deepEqual(A.cellActions(viewOf('error'), ctxOf('Codex', { loginBusy: true })).map((a) => a.key), ['recheck', 'cancel']);
  // 不會在登入的格子（尚未加入、沒裝 CLI）、離線的那台：不多
  assert.deepEqual(A.cellActions(viewOf('absent'), ctxOf('Codex', { loginBusy: true })).map((a) => a.key), ['create']);
  assert.deepEqual(A.cellActions(viewOf('notInstalled'), ctxOf('Codex', { loginBusy: true })).map((a) => a.key), ['install']);
  assert.deepEqual(A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { loginBusy: true, device: devA({ online: false }) })).map((a) => a.key), ['login']);
  // 這個分頁正在別台登入這個帳戶（busy 是那一次的）：不多
  assert.deepEqual(A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { loginBusy: true, pendingElsewhere: 'HOME-PC' })).map((a) => a.key), ['login']);
  // 政策不允許：照樣列出、停用並寫原因（同其他按鈕）
  const policy = A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { loginBusy: true, device: devA({ remoteControlLevel: 'View' }) }));
  assert.deepEqual(policy.map((a) => [a.key, a.disabled, a.reason]), [['login', true, '在 OFFICE-PC 的設定中允許遠端操作'], ['cancel', true, '在 OFFICE-PC 的設定中允許遠端操作']]);
  // 沒在登入：不多
  assert.deepEqual(A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex')).map((a) => a.key), ['login']);
});

test('cellActions_取消登入的說明寫驗證碼在那台的帳戶卡', () => {
  const [cancel] = A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { pendingHere: true, loginBusy: true }));
  assert.match(cancel.hint, /OFFICE-PC/);
  assert.match(cancel.hint, /那台的帳戶卡/, '斷線之後看不到驗證碼時去哪裡找（步 9）');
});

test('cellActions_這個分頁正在別台登入這個帳戶_登入類停用_登出照常', () => {
  const login = A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { pendingElsewhere: 'HOME-PC' }));
  assert.equal(login.length, 1);
  assert.equal(login[0].disabled, true);
  assert.match(login[0].reason, /正在 HOME-PC 登入/);
  assert.ok(login[0].cmd && login[0].cmd.deviceId === 'd_A', '停用的按鈕仍帶指令');
  const absent = A.cellActions(viewOf('absent', {}, 'Claude'), ctxOf('Claude', { pendingElsewhere: 'HOME-PC' }));
  assert.equal(absent[0].disabled, true);
  // Claude 登入已到期：重新登入停用、登出照常可以按
  const expired = A.cellActions(viewOf('ready', { loginExpiresAt: NOW - MIN }, 'Claude'), ctxOf('Claude', { pendingElsewhere: 'HOME-PC' }));
  assert.deepEqual(expired.map((a) => [a.key, a.disabled]), [['relogin', true], ['logout', false]]);
  // 離線、政策的原因優先
  const off = A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { pendingElsewhere: 'HOME-PC', device: devA({ online: false }) }));
  assert.equal(off[0].reason, 'OFFICE-PC 目前離線');
  // 沒有別台的追蹤：照常
  assert.equal(A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { pendingElsewhere: null }))[0].disabled, false);
});

test('cellActions_缺欄位與不認得的種類_沒有按鈕', () => {
  assert.deepEqual(A.cellActions(viewOf('notLoggedIn'), ctxOf('Gemini')), [], '不認得的種類');
  assert.deepEqual(A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { deviceId: null })), [], '沒有 deviceId 不畫（絕不送沒有 deviceId 的單台指令）');
  assert.deepEqual(A.cellActions(viewOf('notLoggedIn'), ctxOf('Codex', { accountId: '' })), []);
  assert.deepEqual(A.cellActions(null, ctxOf('Codex')), []);
});

test('cellActions_不改傳入的參數', () => {
  const view = viewOf('ready');
  const ctx = ctxOf('Codex', { running: 2 });
  const before = JSON.stringify([view, ctx]);
  A.cellActions(view, ctx);
  assert.equal(JSON.stringify([view, ctx]), before);
});

// ───── 所有電腦登出 ─────

test('logoutAllPlan_沒有deviceId_確認文字有7天_預設資料夾_執行中數', () => {
  const plan = A.logoutAllPlan({ id: 'la_x1', label: '工作用 Codex', provider: 'Codex', sessions: { open: 3, running: 2, waiting: 0 } });
  assert.deepEqual(plan.cmd, { cmd: 'accountLogout', accountId: 'la_x1' });
  assert.equal('deviceId' in plan.cmd, false, '沒有 deviceId＝所有電腦');
  const text = [plan.title, plan.message, plan.detail].join('\n');
  assert.match(plan.title, /工作用 Codex/);
  assert.match(text, /7 天/);
  assert.match(text, /預設的設定資料夾/);
  assert.match(text, /2 個對話執行中/);
  assert.match(A.logoutAllPlan({ id: 'la_x1', label: 'x' }).detail, /執行中/, '沒有 sessions 也寫明');
  assert.equal(A.logoutAllPlan(null), null);
  assert.equal(A.logoutAllPlan({ label: 'x' }), null);
});

// ───── 各電腦 CLI ─────

test('deviceCliActions_卡頭立即檢查與每列的動作', () => {
  const entry = R.deviceCliModel({
    d_A: {
      Claude: { installed: true, version: '2.1.300', updateAvailable: true, latest: '2.1.301', checkedAt: NOW - MIN },
      Codex: { installed: false, checkedAt: NOW - MIN },
    },
  }, { mode: 'browser', devices: [devA()] }, ['Claude', 'Codex'])[0];
  const r = A.deviceCliActions(entry, devA(), { checking: false });
  assert.deepEqual(r.check.cmd, { cmd: 'cliCheck', deviceId: 'd_A' });
  assert.equal(r.check.label, '立即檢查');
  assert.equal(r.check.disabled, false);
  assert.match(r.check.hint, /自動更新/, '那台開了自動更新時會接著更新（D18）');
  assert.deepEqual(r.rows.Claude.cmd, { cmd: 'cliUpdate', provider: 'Claude', deviceId: 'd_A' });
  assert.match(r.rows.Claude.confirm.message, /安全點停下/);
  assert.match(r.rows.Claude.confirm.message, /要在那台選擇/);
  assert.deepEqual(r.rows.Codex.cmd, { cmd: 'cliInstall', provider: 'Codex', deviceId: 'd_A' });

  const checking = A.deviceCliActions(entry, devA(), { checking: true });
  assert.equal(checking.check.label, '檢查中…');
  assert.equal(checking.check.disabled, true);

  const off = A.deviceCliActions(entry, devA({ remoteControlLevel: 'View' }), {});
  assert.equal(off.check.disabled, true);
  assert.equal(off.check.reason, '在 OFFICE-PC 的設定中允許遠端操作');
  assert.equal(off.rows.Claude.disabled, true);

  const other = A.deviceCliActions({ deviceId: 'd_A', name: 'OFFICE-PC', clis: [
    { provider: 'Claude', view: R.deviceCliView({ installed: true, installBroken: '找不到執行檔' }) },
    { provider: 'Codex', view: R.deviceCliView({ installed: true, version: '0.163.0' }) },
    { provider: 'Grok', view: R.deviceCliView({ installed: false }) },
  ] }, devA(), {});
  assert.equal(other.rows.Claude.key, 'reinstall');
  assert.deepEqual(other.rows.Claude.cmd, { cmd: 'cliInstall', provider: 'Claude', deviceId: 'd_A' });
  assert.match(other.rows.Claude.confirm.message, /安裝視窗/);
  assert.equal(other.rows.Codex, null, '已是最新版沒有動作');
  assert.deepEqual(other.rows.Grok.cmd, { cmd: 'cliInstall', provider: 'Grok', deviceId: 'd_A' }, 'P3 起 Grok 也能從網頁安裝');
  assert.match(other.rows.Grok.confirm.message, /安裝視窗/, 'Grok 的安裝會在那台開視窗');
  assert.deepEqual(A.deviceCliActions(null, devA(), {}), { check: null, rows: {} });
});

test('cliCheckDone_有一種沒變_還沒完成', () => {
  const cli = { Claude: { checkedAt: 100 }, Codex: { checkedAt: 200 } };
  const before = A.cliCheckSnapshot(cli);
  assert.deepEqual(before, { Claude: 100, Codex: 200 });
  assert.equal(A.cliCheckDone(before, cli), false, '都沒變');
  assert.equal(A.cliCheckDone(before, { Claude: { checkedAt: 150 }, Codex: { checkedAt: 200 } }), false, 'Codex 還沒變');
  assert.equal(A.cliCheckDone(before, { Claude: { checkedAt: 150 } }), false, 'Codex 不見了');
  assert.equal(A.cliCheckDone(before, { Claude: { checkedAt: 150 }, Codex: { checkedAt: 250 } }), true);
  assert.equal(A.cliCheckDone({ Claude: null }, { Claude: { checkedAt: 150 } }), true, '原本沒有 checkedAt、現在有');
  assert.equal(A.cliCheckDone({ Claude: null }, { Claude: { checkedAt: null } }), false);
  assert.equal(A.cliCheckDone(null, cli), false);
});

test('cliCheckResult_照本機的說法_依種類順序', () => {
  const v = (o) => Object.assign({ installed: true, version: '1.0.0', latest: '1.0.0', updateAvailable: false, installBroken: null, checkedAt: NOW }, o);
  const ok = A.cliCheckResult('OFFICE-PC', { Codex: v({ latest: '0.51.0', version: '0.50.0', updateAvailable: true }), Claude: v() }, ['Claude', 'Codex']);
  assert.deepEqual(ok, { text: 'OFFICE-PC 檢查完成：Claude Code 已是最新版；Codex 有新版 0.51.0', level: 'info' });
  assert.deepEqual(A.cliCheckResult('OFFICE-PC', { Claude: v() }, ['Claude', 'Codex']), { text: 'OFFICE-PC 檢查完成：Claude Code 已是最新版', level: 'success' }, '沒回報的種類略過');
  assert.equal(A.cliCheckResult('OFFICE-PC', { Claude: v({ installed: false, version: null, latest: null }) }, ['Claude']).text, 'OFFICE-PC 檢查完成：Claude Code 尚未安裝');
  const broken = A.cliCheckResult('OFFICE-PC', { Claude: v({ installBroken: '找不到執行檔' }) }, ['Claude']);
  assert.deepEqual(broken, { text: 'OFFICE-PC 檢查完成：Claude Code 無法執行', level: 'error' });
  const noLatest = A.cliCheckResult('OFFICE-PC', { Codex: v({ latest: null }) }, ['Codex']);
  assert.deepEqual(noLatest, { text: 'OFFICE-PC 檢查完成：Codex 查不到最新版本', level: 'error' }, '查不到最新版不能說已是最新版');
  assert.equal(A.cliCheckResult(XSS, { Claude: v() }, ['Claude']).text, XSS + ' 檢查完成：Claude Code 已是最新版', '名稱照原樣');
  assert.equal(A.cliCheckResult('OFFICE-PC', null, ['Claude']).text, 'OFFICE-PC 檢查完成');
});

// ───── 登入追蹤（D25）─────

const acct = (extra) => Object.assign({ id: 'la_x1', label: '工作用 Codex', provider: 'Codex', loginBusy: false }, extra);
const watchCell = (status, extra, provider = 'Codex') => {
  const raw = Object.assign({ deviceId: 'd_A', deviceName: 'OFFICE-PC', status, statusText: '', identityOk: true, online: true }, extra);
  return Object.assign(R.cellView(raw, devA(), NOW, { provider }), { statusText: raw.statusText });
};
const pendingOf = (view, extra, provider = 'Codex') => Object.assign(A.loginPending(ctxOf(provider), view, NOW, 'login'), extra);

test('loginPending_記下送出當下的事實', () => {
  const p = A.loginPending(ctxOf('Codex'), watchCell('notLoggedIn'), NOW, 'login');
  assert.deepEqual(p, { logicalId: 'la_x1', deviceId: 'd_A', deviceName: 'OFFICE-PC', provider: 'Codex', kind: 'login', at: NOW, fromKind: 'notLoggedIn', commandId: null, started: false, canceled: false });
  assert.equal(A.loginPending(ctxOf('Claude'), watchCell('ready', { loginExpiresAt: NOW - MIN }, 'Claude'), NOW, 'relogin').fromKind, 'expired', '已到期的 ready 另記');
  // 步 12：記下 post 回的 commandId（對 cmdtrack 的結果用）；桌面的 undefined、空字串都是 null
  assert.equal(A.loginPending(ctxOf('Codex'), watchCell('notLoggedIn'), NOW, 'login', CID).commandId, CID);
  assert.equal(A.loginPending(ctxOf('Codex'), watchCell('notLoggedIn'), NOW, 'login', undefined).commandId, null);
  assert.equal(A.loginPending(ctxOf('Codex'), watchCell('notLoggedIn'), NOW, 'login', '').commandId, null);
});

// ───── 指令結果（步 12，WP4.6 的 cmdtrack）─────

test('loginSettle_同一次送出被拒_立刻結束而且不提示', () => {
  const p = pendingOf(watchCell('notLoggedIn'), { commandId: CID });
  const r = A.loginSettle(p, CID, 'rejected');
  assert.deepEqual(r, { state: 'rejected', pending: null, clearCode: true, toast: null });
  // 那台已經開始登入了（started）也一樣：被拒＝那台沒有執行
  assert.equal(A.loginSettle(Object.assign({}, p, { started: true }), CID, 'rejected').state, 'rejected');
});

test('loginSettle_done與unknown_繼續追蹤', () => {
  const p = pendingOf(watchCell('notLoggedIn'), { commandId: CID });
  for (const st of ['done', 'unknown', 'weird', null]) {
    const r = A.loginSettle(p, CID, st);
    assert.equal(r.state, 'pending', String(st));
    assert.equal(r.pending, p, String(st) + '：還是同一筆（不換新物件）');
    assert.equal(r.clearCode, false, String(st));
    assert.equal(r.toast, null, String(st));
  }
});

test('loginSettle_別次送出的結果與沒有追蹤_不影響', () => {
  const p = pendingOf(watchCell('notLoggedIn'), { commandId: CID });
  assert.equal(A.loginSettle(p, '01JZ0000000000000000000002', 'rejected').state, 'pending', '同一個帳戶之前那一次的結果');
  assert.equal(A.loginSettle(p, null, 'rejected').state, 'pending');
  assert.equal(A.loginSettle(pendingOf(watchCell('notLoggedIn')), CID, 'rejected').state, 'pending', '沒有 commandId 的追蹤（桌面）不會被結束');
  assert.deepEqual(A.loginSettle(null, CID, 'rejected'), { state: 'none', pending: null, clearCode: false, toast: null });
});

test('loginWatch_loginBusy變true只記started_不是結束', () => {
  const p = pendingOf(watchCell('notLoggedIn'));
  const r = A.loginWatch(p, acct({ loginBusy: true }), watchCell('notLoggedIn'), NOW + 5_000);
  assert.equal(r.state, 'pending');
  assert.equal(r.clearCode, false);
  assert.equal(r.toast, null);
  assert.equal(r.pending.started, true);
  assert.equal(p.started, false, '不改傳入的 pending');
  assert.notEqual(r.pending, p);
});

test('loginWatch_started之後loginBusy變false_notLoggedIn是ended_提示用statusText', () => {
  const p = pendingOf(watchCell('notLoggedIn'), { started: true });
  const note = '上次遠端登入的帳號和目錄不同，已在這台自動登出';
  const r = A.loginWatch(p, acct({ loginBusy: false }), watchCell('notLoggedIn', { statusText: note }), NOW + 60_000);
  assert.equal(r.state, 'ended');
  assert.equal(r.clearCode, true);
  assert.equal(r.pending, null);
  assert.equal(r.toast.level, 'error');
  assert.equal(r.toast.text, 'OFFICE-PC 的登入沒有完成：' + note);
  // 沒有 statusText：寫常見原因
  const bare = A.loginWatch(p, acct(), watchCell('notLoggedIn'), NOW + 60_000);
  assert.equal(bare.state, 'ended');
  assert.match(bare.toast.text, /常見原因/);
  assert.match(bare.toast.text, /裝置碼登入/);
  // 那台的預設說明（AccountRules.DescribeStatus 的「尚未登入 ChatGPT 帳戶」、網頁的「尚未登入」）說不出原因：也寫常見原因
  for (const generic of ['尚未登入 ChatGPT 帳戶', '尚未登入']) {
    const r2 = A.loginWatch(p, acct(), watchCell('notLoggedIn', { statusText: generic }), NOW + 60_000);
    assert.match(r2.toast.text, /常見原因/, generic);
    assert.equal(r2.toast.text.includes(generic), false, generic);
  }
  // 錯誤的說明照原樣
  const err = A.loginWatch(p, acct(), watchCell('error', { statusText: '讀不到憑證' }), NOW + 60_000);
  assert.equal(err.toast.text, 'OFFICE-PC 的登入沒有完成：讀不到憑證');
});

test('loginWatch_started之後格子ready_成功', () => {
  const p = pendingOf(watchCell('notLoggedIn'), { started: true });
  const r = A.loginWatch(p, acct(), watchCell('ready'), NOW + 60_000);
  assert.equal(r.state, 'ready');
  assert.equal(r.clearCode, true);
  assert.equal(r.toast.level, 'success');
  assert.match(r.toast.text, /OFFICE-PC/);
  // 帳戶層級的 loginBusy 還是 true 也算成功（可能是別台留下的 busy，審查 F2）
  assert.equal(A.loginWatch(p, acct({ loginBusy: true }), watchCell('ready'), NOW + 60_000).state, 'ready');
});

test('loginWatch_別台留下的loginBusy一直是true_目標格子變ready仍是成功', () => {
  // 審查 F2：HOME-PC 登入到一半 app 被關掉，伺服器留著它的 login_busy；帳戶層級的 loginBusy 是所有電腦的 Any，一直是 true
  const sent = pendingOf(watchCell('notLoggedIn'));
  const first = A.loginWatch(sent, acct({ loginBusy: true }), watchCell('notLoggedIn'), NOW + 1_000);
  assert.equal(first.state, 'pending');
  assert.equal(first.pending.started, true, '送出時 busy 已經是 true：第一次就記成 started');
  const r = A.loginWatch(first.pending, acct({ loginBusy: true }), watchCell('ready'), NOW + 3 * MIN);
  assert.equal(r.state, 'ready', '沒有等到 busy 變回 false 才判斷');
  assert.equal(r.clearCode, true);
  assert.deepEqual(r.toast, { text: 'OFFICE-PC 已登入「工作用 Codex」', level: 'success' });
  // 送出時已經是 ready（只有規則測試會構造）：busy 是 true 也不算成功
  const ready = pendingOf(watchCell('ready'), { started: true });
  assert.equal(A.loginWatch(ready, acct({ loginBusy: true }), watchCell('ready'), NOW + MIN).state, 'pending');
  // 沒有完成（格子仍未登入）：busy 一直是 true，到驗證碼到期才結束（提示稍晚，不會報成成功）
  const login = { message: '', done: false, success: null, at: NOW, deviceCode: { userCode: 'ABCD-EFGH', verificationUrl: 'https://auth.openai.com/codex/device', expiresAt: NOW + 15 * MIN } };
  assert.equal(A.loginWatch(first.pending, acct({ loginBusy: true }), watchCell('notLoggedIn'), NOW + 15 * MIN, login).state, 'expired');
});

test('loginWatch_沒started但格子由非ready變ready_成功', () => {
  const p = pendingOf(watchCell('absent'));
  const r = A.loginWatch(p, acct(), watchCell('ready'), NOW + 10_000);
  assert.equal(r.state, 'ready');
  assert.equal(r.toast.level, 'success');
});

test('loginWatch_fromKind是ready_沒started時不算成功', () => {
  // 只有規則測試會構造：送出時格子已經是可用的 ready，之後還是 ready 不代表這次登入成功
  const p = pendingOf(watchCell('ready'));
  assert.equal(p.fromKind, 'ready');
  assert.equal(A.loginWatch(p, acct(), watchCell('ready'), NOW + 10_000).state, 'pending');
});

test('loginWatch_Claude已到期的ready格子在started之前不算成功', () => {
  const expired = watchCell('ready', { loginExpiresAt: NOW - MIN }, 'Claude');
  const p = pendingOf(expired, {}, 'Claude');
  assert.equal(p.fromKind, 'expired');
  assert.equal(A.loginWatch(p, acct({ provider: 'Claude' }), expired, NOW + 10_000).state, 'pending', '還是到期的 ready 不是成功');
  // 重新登入完成（到期日更新）：沒趕上 loginBusy 也算成功
  const renewed = watchCell('ready', { loginExpiresAt: NOW + 300 * DAY }, 'Claude');
  assert.equal(A.loginWatch(p, acct({ provider: 'Claude' }), renewed, NOW + 10_000).state, 'ready');
  // started 之後 loginBusy 回 false、格子仍是到期的 ready：ended
  const ended = A.loginWatch(Object.assign({}, p, { started: true }), acct({ provider: 'Claude' }), expired, NOW + 10_000);
  assert.equal(ended.state, 'ended');
});

test('loginWatch_60秒沒變忙_rejected而且沒有提示', () => {
  const p = pendingOf(watchCell('notLoggedIn'));
  assert.equal(A.loginWatch(p, acct(), watchCell('notLoggedIn'), NOW + A.LOGIN_REJECT_GUESS_MS - 1).state, 'pending');
  const r = A.loginWatch(p, acct(), watchCell('notLoggedIn'), NOW + A.LOGIN_REJECT_GUESS_MS);
  assert.equal(r.state, 'rejected');
  assert.equal(r.toast, null, '伺服器已經提示過');
  assert.equal(r.clearCode, true);
  // 已經 started 的不會被當成被拒
  assert.equal(A.loginWatch(Object.assign({}, p, { started: true }), acct({ loginBusy: true }), watchCell('notLoggedIn'), NOW + 5 * MIN).state, 'pending');
});

test('loginWatch_驗證碼到期_expired', () => {
  const p = pendingOf(watchCell('notLoggedIn'), { started: true });
  const login = { message: '', done: false, success: null, at: NOW, deviceCode: { userCode: 'ABCD-EFGH', verificationUrl: 'https://auth.openai.com/codex/device', expiresAt: NOW + 15 * MIN } };
  assert.equal(A.loginWatch(p, acct({ loginBusy: true }), watchCell('notLoggedIn'), NOW + 15 * MIN - 1, login).state, 'pending');
  const r = A.loginWatch(p, acct({ loginBusy: true }), watchCell('notLoggedIn'), NOW + 15 * MIN, login);
  assert.equal(r.state, 'expired');
  assert.equal(r.clearCode, true);
  assert.equal(r.toast.text, 'OFFICE-PC 的登入沒有在時限內完成');
  // 沒有驗證碼（Claude、或斷線沒收到）：送出超過 16 分鐘
  assert.equal(A.loginWatch(p, acct({ loginBusy: true }), watchCell('notLoggedIn'), NOW + A.LOGIN_MAX_MS - 1).state, 'pending');
  assert.equal(A.loginWatch(p, acct({ loginBusy: true }), watchCell('notLoggedIn'), NOW + A.LOGIN_MAX_MS).state, 'expired');
});

test('loginWatch_取消_canceled', () => {
  const p = pendingOf(watchCell('notLoggedIn'), { started: true, canceled: true });
  assert.equal(A.loginWatch(p, acct({ loginBusy: true }), watchCell('notLoggedIn'), NOW + MIN).state, 'pending', '那台還在登入');
  const r = A.loginWatch(p, acct({ loginBusy: false }), watchCell('notLoggedIn'), NOW + MIN);
  assert.equal(r.state, 'canceled');
  assert.equal(r.clearCode, true);
  assert.equal(r.toast, null);
  // 取消了但那台其實已經登入完成：照實說成功
  assert.equal(A.loginWatch(p, acct({ loginBusy: false }), watchCell('ready'), NOW + MIN).state, 'ready');
});

test('loginWatch_沒有追蹤_none', () => {
  assert.deepEqual(A.loginWatch(null, acct(), watchCell('ready'), NOW), { state: 'none', pending: null, clearCode: false, toast: null });
});

// ───── 送出時的提示、標示 ─────

test('sentText_所有電腦登出與裝置碼登入回null', () => {
  assert.equal(A.sentText({ cmd: 'accountLogout', accountId: 'la_x1' }, 'OFFICE-PC', '工作用 Codex'), null, '所有電腦登出：伺服器另送提示');
  assert.equal(A.sentText({ cmd: 'accountLoginDevice', logicalAccountId: 'la_x1', deviceId: 'd_A', method: 'deviceCode' }, 'OFFICE-PC', '工作用 Codex'), null, '等驗證碼');
  assert.equal(A.sentText({ cmd: 'cliCheck', deviceId: 'd_A' }, 'OFFICE-PC', null), null, '按鈕本身顯示檢查中');
  assert.equal(A.sentText({ cmd: 'weird' }, 'OFFICE-PC', 'x'), null);
  assert.equal(A.sentText(null, 'OFFICE-PC', 'x'), null);

  assert.equal(A.sentText({ cmd: 'accountLogout', accountId: 'la_x1', deviceId: 'd_A' }, 'OFFICE-PC', '工作用 Codex'), '已要求 OFFICE-PC 登出「工作用 Codex」');
  assert.equal(A.sentText({ cmd: 'accountLoginDevice', logicalAccountId: 'la_x1', deviceId: 'd_A' }, 'OFFICE-PC', '工作用 Claude'), '已要求 OFFICE-PC 開始「工作用 Claude」的登入');
  assert.equal(A.sentText({ cmd: 'cliInstall', provider: 'Claude', deviceId: 'd_A' }, 'OFFICE-PC', A.productName('Claude')), '已要求 OFFICE-PC 安裝 Claude Code CLI');
  assert.equal(A.sentText({ cmd: 'cliUpdate', provider: 'Codex', deviceId: 'd_A' }, 'OFFICE-PC', A.productName('Codex')), '已要求 OFFICE-PC 更新 Codex CLI');
  assert.match(A.sentText({ cmd: 'accountLoginCancel', accountId: 'la_x1', deviceId: 'd_A' }, 'OFFICE-PC', 'x'), /^已要求 OFFICE-PC /);
  assert.match(A.sentText({ cmd: 'accountRefresh', accountId: 'la_x1', deviceId: 'd_A' }, 'OFFICE-PC', 'x'), /^已要求 OFFICE-PC /);
});

test('originLabel_電腦名稱是XSS語料時原樣回傳字串', () => {
  assert.equal(A.originLabel({ deviceName: 'OFFICE-PC' }), '在 OFFICE-PC');
  assert.equal(A.originLabel({ deviceName: '筆電 ' + XSS }), '在 筆電 ' + XSS, '照原樣（之後一律 textContent）');
  assert.equal(A.originLabel(null), null);
  assert.equal(A.originLabel({ deviceName: '' }), null);
  // 其他文字也照原樣帶進來，不轉義（畫面以 textContent 顯示）
  assert.equal(A.remoteAllowed(devA({ name: XSS, remoteControlLevel: 'Off' })).reason, `在 ${XSS} 的設定中允許遠端操作`);
  assert.equal(A.logoutAllPlan({ id: 'la_x1', label: XSS }).title, `在所有電腦登出「${XSS}」？`);
});

/** HTML 寫入點（D26）：出現就算（連註解也不准寫這幾個字，掃描才不必分辨） */
const HTML_SINKS = ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'createContextualFragment', 'DOMParser'];
const htmlSinksIn = (source) => HTML_SINKS.filter((s) => source.includes(s));

test('新檔不用innerHTML', () => {
  // WP4.8 的網頁新檔只用 h() 與 textContent（D26）；步 9 起兩個檔都要在
  const files = ['accountrules.js', 'remoteaccounts.js'];
  for (const f of files) assert.ok(existsSync(path.join(webDir, f)), f + ' 要存在（掃描不能空轉）');
  for (const f of files) {
    const source = readFileSync(path.join(webDir, f), 'utf8');
    assert.deepEqual(htmlSinksIn(source), [], `${f} 不可以有 HTML 寫入點`);
  }
  // 陽性對照：同一個掃描抓得到每一種寫法
  for (const s of HTML_SINKS) assert.deepEqual(htmlSinksIn(`el.${s}(x);`), [s], s);
  assert.deepEqual(htmlSinksIn("node.innerHTML = '<b>' + name;"), ['innerHTML']);
  // 規則模組沒有 import（才能以 data: URL 匯入、不依賴 DOM）
  assert.equal(/^\s*import\s/m.test(readFileSync(path.join(webDir, 'accountrules.js'), 'utf8')), false);
});

test('remoteaccounts_追蹤只在記憶體_只經store與ui', () => {
  const source = readFileSync(path.join(webDir, 'remoteaccounts.js'), 'utf8');
  // 登入追蹤與遠端「立即檢查」每個分頁一份，不進瀏覽器儲存（D25）
  for (const banned of ['localStorage', 'sessionStorage', 'indexedDB', 'document.cookie']) assert.equal(source.includes(banned), false, banned);
  // 判斷都在純規則（accountrules、remoterules；桌面遠端操作的 peerOf 在 liverules）：DOM 模組只另外依賴 store、ui、util 與指令結果的追蹤（WP4.6 的 cmdtrack，步 12）
  const imports = [...source.matchAll(/from '\.\/([a-z]+\.js)'/g)].map((m) => m[1]).sort();
  assert.deepEqual(imports, ['accountrules.js', 'cmdtrack.js', 'liverules.js', 'remoterules.js', 'store.js', 'ui.js', 'util.js']);
});

test('productName_四種都有產品名稱_不認得的照原樣', () => {
  assert.equal(A.productName('Claude'), 'Claude Code');
  assert.equal(A.productName('Codex'), 'Codex');
  assert.equal(A.productName('Grok'), 'Grok Build', '2026-10-22 起 Grok 也同步，名稱同宿主的 AgentProviders.ProductName');
  assert.equal(A.productName('Antigravity'), 'Antigravity');
  assert.equal(A.productName('Gemini'), 'Gemini');
  assert.equal(A.productName(null), '');
});

test('cellActions_Grok比照Codex用裝置碼_Antigravity在那台開視窗_沒有登出也不能在那台建立', () => {
  const grok = A.cellActions(viewOf('notLoggedIn'), ctxOf('Grok'));
  assert.equal(grok.length, 1);
  assert.equal(grok[0].label, '用裝置碼登入');
  assert.equal(grok[0].cmd.method, 'deviceCode');
  assert.equal(grok[0].confirm, null);
  assert.equal(A.cellActions(viewOf('absent'), ctxOf('Grok'))[0].key, 'create', 'Grok 可以在那台建立隔離帳戶並登入');
  assert.equal(A.cellActions(viewOf('ready'), ctxOf('Grok'))[0].key, 'logout');

  const agy = A.cellActions(viewOf('notLoggedIn'), ctxOf('Antigravity'));
  assert.equal(agy.length, 1);
  assert.equal(agy[0].label, '在那台開登入視窗');
  assert.equal(agy[0].cmd.method, undefined, '不是裝置碼');
  assert.match(agy[0].confirm.message, /有人在那台電腦前完成 Google 登入/);
  assert.deepEqual(A.cellActions(viewOf('ready'), ctxOf('Antigravity')), [], 'agy 沒有登出指令（D4）');
  const mismatch = viewOf('ready', { identityOk: false }, 'Antigravity');
  assert.equal(mismatch.kind, 'identity');
  assert.deepEqual(A.cellActions(mismatch, ctxOf('Antigravity')), [], '身分不符也沒有登出按鈕');
  assert.deepEqual(A.cellActions(viewOf('absent'), ctxOf('Antigravity')), [], '一台只有一個 agy 登入');
  assert.equal(A.logoutAllPlan({ id: 'la_1', provider: 'Antigravity', label: 'agy' }), null, '沒有「在所有電腦登出」');
  assert.notEqual(A.logoutAllPlan({ id: 'la_2', provider: 'Grok', label: 'grok' }), null);
});

// ───── 對話標頭的帳戶選單在執行中（使用者 2026-10-23 起；2026-10-24 起四種代理之間都能互換） ─────

test('switchableWhileRunning_四種都可以_不認得的種類不行', () => {
  assert.deepEqual(['Claude', 'Codex', 'Grok', 'Antigravity', 'Gemini', null, 3].map(A.switchableWhileRunning), [true, true, true, true, false, false, false]);
});

test('runningAccountMenu_四種代理的帳戶都列_同一種在前_其他照providers的順序_不認得的種類不開選單', () => {
  const accounts = [
    { id: 'c1', provider: 'Claude', label: 'Claude 主帳戶' },
    { id: 'x1', provider: 'Codex', label: 'Codex 帳戶' },
    { id: 'c2', provider: 'Claude', label: XSS },
    { id: 'c3', provider: 'Claude', label: '只在其他電腦', remoteOnly: true },
    { id: 'g1', provider: 'Grok', label: 'Grok 帳戶' },
    { id: 'a1', provider: 'Antigravity', label: 'Antigravity' },
    { id: 'z1', provider: 'Gemini', label: '不認得的種類' },
    null,
  ];
  const before = JSON.stringify(accounts);
  const m = A.runningAccountMenu('Claude', accounts);
  assert.equal(m.allowed, true);
  assert.equal(m.reason, null);
  assert.deepEqual(m.sections.map((s) => [s.provider, s.header, s.accounts.map((a) => a.id)]), [
    ['Claude', 'Claude（立即換或等目前步驟完成後換）', ['c1', 'c2']],
    ['Codex', 'Codex（會以前文節錄開新對話）', ['x1']],
    ['Antigravity', 'Antigravity（會以前文節錄開新對話）', ['a1']],
    ['Grok', 'Grok（會以前文節錄開新對話）', ['g1']],
  ], '同一種代理在前、其他種類各一段（沒給 providers 用內建順序）；remoteOnly 與不認得的種類不列');
  assert.equal(m.empty, null);
  assert.equal(m.cancel, null, '沒有待換的帳戶：沒有取消');
  assert.equal(m.pendingId, null);
  assert.equal(JSON.stringify(accounts), before, '不改傳入的參數');

  const grok = A.runningAccountMenu('Grok', accounts, null, ['Claude', 'Codex', 'Antigravity', 'Grok', 'Gemini']);
  assert.deepEqual(grok.sections.map((s) => s.provider), ['Grok', 'Claude', 'Codex', 'Antigravity'], 'Grok 的對話：Grok 在前，其他照 providers；不認得的不列');
  assert.equal(grok.sections[0].header, 'Grok（立即換或等目前步驟完成後換）');
  const agy = A.runningAccountMenu('Antigravity', accounts, null, ['Codex', 'Claude']);
  assert.deepEqual(agy.sections.map((s) => s.provider), ['Antigravity', 'Codex', 'Claude'], 'providers 只列兩種時只排那兩種');
  const only = A.runningAccountMenu('Antigravity', [accounts[5], { id: 'z2', provider: 'Gemini', label: 'x' }]);
  assert.deepEqual(only.sections.map((s) => s.accounts.map((a) => a.id)), [['a1']]);
  assert.equal(only.empty, '沒有其他帳戶', '只有目前這個：說明沒有可以換的');

  const no = A.runningAccountMenu('Gemini', accounts);
  assert.equal(no.allowed, false, '不認得的種類（之後新增、這版還不知道的）不開選單');
  assert.equal(no.reason, '這一輪執行中不能更換 Gemini 帳戶，請先停止或等它完成');
  assert.deepEqual(no.sections, []);
  assert.deepEqual(A.runningAccountMenu('Claude', null).sections, []);

  // 桌面本機帳戶未安裝 CLI 與尚未登入的保留在選單顯示原因，但標進 disabledIds 不讓點選；目前正在跑的帳戶與瀏覽器邏輯帳戶（logicalId === id）不停用
  const mixedStatus = [
    { id: 'c-cur', provider: 'Claude', label: '目前帳戶', status: 'notLoggedIn' },
    { id: 'c-ok', provider: 'Claude', label: '可用副帳', status: 'ready' },
    { id: 'c-out', provider: 'Claude', label: '未登入副帳', status: 'notLoggedIn' },
    { id: 'la_c9', logicalId: 'la_c9', provider: 'Claude', label: '瀏覽器邏輯帳戶', status: 'notLoggedIn' },
    { id: 'g-no-cli', provider: 'Grok', label: '沒裝 Grok', status: 'notInstalled' },
    { id: 'a-ok', provider: 'Antigravity', label: 'Agy 可用', status: 'ready' },
  ];
  const filtered = A.runningAccountMenu('Claude', mixedStatus, null, null, 'c-cur');
  assert.deepEqual(
    filtered.sections.map((s) => [s.provider, s.accounts.map((a) => a.id)]),
    [['Claude', ['c-cur', 'c-ok', 'c-out', 'la_c9']], ['Antigravity', ['a-ok']], ['Grok', ['g-no-cli']]],
    '所有本機與邏輯帳戶都列出以便顯示狀態說明',
  );
  assert.deepEqual(filtered.disabledIds, ['c-out', 'g-no-cli'], '僅本機 notLoggedIn／notInstalled 標為停用，排除 currentAccountId 與邏輯帳戶');
});

test('runningSwitchConfirm與accountChipHint_文字逐字_名稱照字面_等目前步驟完成是預設', () => {
  assert.deepEqual(A.runningSwitchConfirm('Claude 副帳戶'), {
    title: '改用「Claude 副帳戶」繼續？',
    message: '這一輪還在執行。可以等目前步驟完成後換，或現在就停下，由「Claude 副帳戶」帶著先前對話的節錄從中斷處接著做。',
    detail: '等目前步驟完成：等執行中的工具、背景工作與待回答的請求結束後，自動改由「Claude 副帳戶」接續剩餘工作，不必等整個任務做完。現在停下：正在執行的指令會被中斷。',
    choices: [
      { value: 'now', label: '現在停下並改用' },
      { value: 'turnEnd', label: '目前步驟完成後換', primary: true },
    ],
  });
  assert.ok(A.runningSwitchConfirm(XSS).title.includes(XSS), '名稱照字面（畫面以 textContent 顯示）');
  assert.deepEqual(A.runningSwitchConfirm('Claude 副帳戶', 'Claude', 'Claude'), A.runningSwitchConfirm('Claude 副帳戶'), '同一種代理照舊');
  const cross = A.runningSwitchConfirm('Codex Pro', 'Claude', 'Codex');
  assert.equal(cross.message, '這一輪還在執行。Claude Code 和 Codex 的對話不能直接接續，「Codex Pro」會帶著先前對話的節錄開新的對話；可以等目前步驟完成後換，或現在就停下、由它從中斷處接著做。');
  assert.deepEqual(cross.choices, A.runningSwitchConfirm('Codex Pro').choices);
  assert.equal(A.accountChipHint('Claude', false, false), '點一下更換帳戶');
  assert.equal(A.accountChipHint('Claude', true, false), '執行中也可以換帳戶（立即或等目前步驟完成後換）');
  assert.equal(A.accountChipHint('Grok', true, false), '執行中也可以換帳戶（立即或等目前步驟完成後換）');
  assert.equal(A.accountChipHint('Gemini', true, false), '執行中不能更換帳戶');
  assert.equal(A.runningSwitchConfirm('Antigravity', 'Grok', 'Antigravity').message,
    '這一輪還在執行。Grok Build 和 Antigravity 的對話不能直接接續，「Antigravity」會帶著先前對話的節錄開新的對話；可以等目前步驟完成後換，或現在就停下、由它從中斷處接著做。');
  assert.equal(A.accountChipHint('Claude', true, true), '其他電腦的對話不能更換帳戶');
  const later = A.pendingSwitchView({ running: true, accountId: 'c1', pendingAccount: { accountId: 'c2', label: '副帳戶', when: 'turnEnd' } });
  const now = A.pendingSwitchView({ running: true, accountId: 'c1', pendingAccount: { accountId: 'c2', label: '副帳戶', when: 'now' } });
  assert.equal(A.accountChipHint('Claude', true, false, later), '目前步驟完成後改用「副帳戶」（點一下可以取消）');
  assert.equal(A.accountChipHint('Claude', true, false, now), '停下這一輪後改用「副帳戶」');
  assert.equal(A.accountChipHint('Claude', true, true, later), '其他電腦的對話不能更換帳戶');
});

test('runningSwitchCommand_只有等目前步驟完成才帶when_立即換不帶_舊版的電腦也收', () => {
  assert.deepEqual(A.runningSwitchCommand('c', 'a2', 'turnEnd'), { cmd: 'setAccount', conversationId: 'c', accountId: 'a2', when: 'turnEnd' });
  assert.deepEqual(A.runningSwitchCommand('c', 'a2', 'now'), { cmd: 'setAccount', conversationId: 'c', accountId: 'a2' });
  assert.deepEqual(A.runningSwitchCommand('c', 'a2', 'later'), { cmd: 'setAccount', conversationId: 'c', accountId: 'a2' });
  assert.equal(A.SWITCH_AT_TURN_END, 'turnEnd', '同中樞 AccountSwitchRules.WhenTurnEnd');
});

test('pendingSwitchView_執行中而且要換到別的帳戶才有_形狀不對回null', () => {
  const hd = (p, extra) => ({ running: true, accountId: 'c1', pendingAccount: p, ...extra });
  assert.deepEqual(A.pendingSwitchView(hd({ accountId: 'c2', label: XSS, when: 'turnEnd' })),
    { accountId: 'c2', label: XSS, turnEnd: true, background: false, hint: '目前步驟完成後改用「' + XSS + '」' });
  assert.deepEqual(A.pendingSwitchView(hd({ accountId: 'c2', when: 'now' })),
    { accountId: 'c2', label: 'c2', turnEnd: false, background: false, hint: '停下這一輪後改用「c2」' }, '沒有名稱用 id');
  assert.equal(A.pendingSwitchView(hd({ accountId: 'c2', when: 'turnEnd' }, { running: false })), null, '不在執行中');
  assert.equal(A.pendingSwitchView(hd({ accountId: 'c1', when: 'turnEnd' })), null, '就是目前的帳戶');
  assert.equal(A.pendingSwitchView(hd(null)), null);
  assert.equal(A.pendingSwitchView(hd({ accountId: 3 })), null);
  assert.equal(A.pendingSwitchView({ running: true, accountId: 'c1' }), null, '瀏覽器版與舊版的標頭沒有這個欄位');
  assert.equal(A.pendingSwitchView(null), null);

  // 執行中選單：等這一輪結束才換的可以取消，要換過去的那個帳戶標上說明；立即換的不給取消
  const accounts = [{ id: 'c1', provider: 'Claude', label: '主' }, { id: 'c2', provider: 'Claude', label: '副' }];
  const m = A.runningAccountMenu('Claude', accounts, A.pendingSwitchView(hd({ accountId: 'c2', label: '副', when: 'turnEnd' })));
  assert.deepEqual(m.cancel, { label: '取消換帳戶', desc: '目前步驟完成後改用「副」' });
  assert.equal(m.pendingId, 'c2');
  assert.equal(m.pendingHint, '目前步驟完成後改用「副」');
  const stopping = A.runningAccountMenu('Claude', accounts, A.pendingSwitchView(hd({ accountId: 'c2', label: '副', when: 'now' })));
  assert.equal(stopping.cancel, null);
  assert.equal(stopping.pendingHint, '停下這一輪後改用「副」');
  assert.equal(A.runningAccountMenu('Gemini', accounts, A.pendingSwitchView(hd({ accountId: 'c2', when: 'turnEnd' }))).cancel, null, '不認得的種類不開選單');
});

test('pendingSwitchView_等背景工作結束才換的_閒置與執行中都有_算等這一輪結束_可以取消', () => {
  const idle = A.pendingSwitchView({ running: false, accountId: 'c1', pendingAccount: { accountId: 'c2', label: '副', when: 'turnEnd', background: true } });
  assert.deepEqual(idle, { accountId: 'c2', label: '副', turnEnd: true, background: true, hint: '背景工作結束後改用「副」' });
  const running = A.pendingSwitchView({ running: true, accountId: 'c1', pendingAccount: { accountId: 'c2', label: '副', when: 'turnEnd', background: true } });
  assert.deepEqual(running, idle, 'CLI 自己開一輪回報背景工作的結果時照樣標出');
  assert.equal(A.pendingSwitchView({ running: false, accountId: 'c1', pendingAccount: { accountId: 'c2', when: 'turnEnd', background: 'yes' } }), null, 'background 不是 true 時閒置照舊沒有');
  assert.equal(A.pendingSwitchView({ running: false, accountId: 'c2', pendingAccount: { accountId: 'c2', background: true } }), null, '就是目前的帳戶');
  assert.equal(A.accountChipHint('Claude', false, false, idle), '背景工作結束後改用「副」（點一下可以取消或現在就換）');
  assert.equal(A.accountChipHint('Claude', false, false, null), '點一下更換帳戶');
  assert.equal(A.accountChipHint('Claude', true, false, running), '背景工作結束後改用「副」（點一下可以取消）');
  const accounts = [{ id: 'c1', provider: 'Claude', label: '主' }, { id: 'c2', provider: 'Claude', label: '副' }];
  assert.deepEqual(A.runningAccountMenu('Claude', accounts, running).cancel, { label: '取消換帳戶', desc: '背景工作結束後改用「副」' });
});

test('backgroundSwitchConfirm_背景工作還在跑時換帳戶_預設等背景工作結束_列出前兩個', () => {
  const bg = { count: 3, tasks: [{ tool: '執行指令', title: 'npm test' }, { tool: '執行指令', title: XSS }, { tool: 'Agent', title: null }] };
  const d = A.backgroundSwitchConfirm('Claude 副帳戶', 'Claude', 'Claude', bg);
  assert.equal(d.title, '改用「Claude 副帳戶」繼續？');
  assert.equal(d.message, '還有 3 個背景工作在跑（npm test、' + XSS + ' 等）。換帳戶會關掉目前的程序，背景工作會跟著停止。');
  assert.deepEqual(d.choices, [
    { value: 'now', label: '現在換（停掉背景工作）' },
    { value: 'turnEnd', label: '背景工作結束後再換', primary: true },
  ]);
  assert.ok(d.detail.includes('「Claude 副帳戶」會收到先前對話的節錄與被停掉的背景工作'));
  assert.equal(A.backgroundSwitchConfirm('x', 'Claude', 'Claude', { count: 1, tasks: [{ tool: '執行指令', title: 'npm run dev' }] }).message,
    '還有 1 個背景工作在跑（npm run dev）。換帳戶會關掉目前的程序，背景工作會跟著停止。');
  assert.equal(A.backgroundSwitchConfirm('x', 'Claude', 'Claude', { count: 2, tasks: [{ tool: 'Agent', title: null }] }).message,
    '還有 2 個背景工作在跑（Agent 等）。換帳戶會關掉目前的程序，背景工作會跟著停止。', '列不下的也算進「等」，沒有摘要用工具名稱');
  assert.equal(A.backgroundSwitchConfirm('x', 'Claude', 'Claude', null).message, '還有 1 個背景工作在跑。換帳戶會關掉目前的程序，背景工作會跟著停止。');
  assert.ok(A.backgroundSwitchConfirm('Codex Pro', 'Claude', 'Codex', bg).message.endsWith('Claude Code 和 Codex 的對話不能直接接續，「Codex Pro」會帶著先前對話的節錄開新的對話。'));
});

test('backgroundSwitchCommand_一律帶when_現在換是now_其他都是等背景工作結束', () => {
  assert.deepEqual(A.backgroundSwitchCommand('c', 'a2', 'now'), { cmd: 'setAccount', conversationId: 'c', accountId: 'a2', when: 'now' });
  assert.deepEqual(A.backgroundSwitchCommand('c', 'a2', 'turnEnd'), { cmd: 'setAccount', conversationId: 'c', accountId: 'a2', when: 'turnEnd' });
  assert.deepEqual(A.backgroundSwitchCommand('c', 'a2', 'later'), { cmd: 'setAccount', conversationId: 'c', accountId: 'a2', when: 'turnEnd' });
  assert.equal(A.SWITCH_NOW, 'now', '同中樞 AccountSwitchRules.WhenNow');
});

test('側欄的用量圓環：Claude Code 不列每週・Fable，其他照原本的前兩個', () => {
  const session = { key: 'session', label: '5 小時', percent: 20 };
  const weekly = { key: 'weekly', label: '每週', percent: 52 };
  const fable = { key: 'weekly:Fable', label: '每週・Fable', percent: 0 };
  const opus = { key: 'weekly:Opus', label: '每週・Opus', percent: 10 };
  // 每週用完、沒有 5 小時時，以前第二個圓環就是 Fable
  assert.deepEqual(A.sidebarWindows('Claude', [weekly, fable]), [weekly]);
  assert.deepEqual(A.sidebarWindows('Claude', [session, weekly, fable]), [session, weekly]);
  // 其他模型的每週照樣列
  assert.deepEqual(A.sidebarWindows('Claude', [weekly, fable, opus]), [weekly, opus]);
  // 不分大小寫；沒有 key 時看標籤
  assert.deepEqual(A.sidebarWindows('Claude', [weekly, { key: 'WEEKLY:fable', label: 'x' }]), [weekly]);
  assert.deepEqual(A.sidebarWindows('Claude', [weekly, { label: '每週・Fable' }]), [weekly]);
  // 其他代理種類不濾
  const odd = { key: 'weekly:Fable', label: '每週・Fable' };
  assert.deepEqual(A.sidebarWindows('Codex', [weekly, odd, session]), [weekly, odd]);
  // 不是陣列、元素不是物件
  assert.deepEqual(A.sidebarWindows('Claude', null), []);
  assert.deepEqual(A.sidebarWindows('Claude', [null, 3, weekly]), [weekly]);
  const input = [weekly, fable];
  A.sidebarWindows('Claude', input);
  assert.equal(input.length, 2, '不改傳入的陣列');
});
