// 網頁純規則的單元測試（node:test，不開瀏覽器）：(cd tools/ui-probe && node --test rules.test.mjs)
// 規則模組沒有 import，以 data: URL 匯入（不依賴 Node 怎麼判定 .js 是不是 module）；每次匯入尾端加不同的註解，避開 module 快取。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import { webcrypto } from 'node:crypto';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
const webDir = path.join(repo, 'src', 'Myriagent', 'Web');

let importSeq = 0;
/** 以 data: URL 匯入 src/Myriagent/Web 底下的模組（沒有 import 的規則模組才能這樣做） */
async function importWeb(name) {
  const source = readFileSync(path.join(webDir, name), 'utf8') + `\n// rules.test ${++importSeq}\n`;
  return import('data:text/javascript;base64,' + Buffer.from(source, 'utf8').toString('base64'));
}

const R = await importWeb('remoterules.js');
const V = await importWeb('versionrules.js');
const T = await importWeb('tokenrules.js');
const IH = await importWeb('historyrules.js');
const L = await importWeb('loginrules.js');
const FX = await importWeb('fxrules.js');
const CX = await importWeb('contextrules.js');
const TT = await importWeb('titlerules.js');

const NOW = 1_791_580_000_000;
const MIN = 60_000;
const DAY = 24 * 60 * MIN;

// ───── Remote ─────

test('remoteFromInit_null與undefined', () => {
  assert.equal(R.remoteFromInit({ remote: null }), null);
  assert.equal(R.remoteFromInit({}), null);
  assert.equal(R.remoteFromInit(null), null);
  assert.equal(R.remoteFromInit(undefined), null);
  assert.equal(R.remoteFromInit({ remote: 'x' }), null);
});

test('remoteFromInit_整理欄位', () => {
  const r = R.remoteFromInit({ remote: { mode: 'browser', deviceId: null, devices: [{ id: 'd_1' }], sync: null } });
  assert.deepEqual(r, { mode: 'browser', deviceId: null, devices: [{ id: 'd_1' }], sync: null });
  const d = R.remoteFromInit({ remote: { mode: 'desktop', deviceId: 'd_old', devices: 'x', sync: { state: 'online', deviceId: 'd_new' } } });
  assert.equal(d.deviceId, 'd_new', '這台的 deviceId 以 sync 為準');
  assert.deepEqual(d.devices, []);
});

test('applySync_null時remote為null', () => {
  const remote = { mode: 'desktop', deviceId: 'd_1', devices: [{ id: 'd_1' }], sync: { state: 'online' } };
  assert.equal(R.applySync(remote, null), null);
  assert.equal(R.applySync(undefined, null), null);
});

test('init沒有remote後收到sync就自建remote', () => {
  const sync = { state: 'pairing', deviceId: null, pending: 0 };
  assert.deepEqual(R.applySync(null, sync), { mode: 'desktop', deviceId: null, devices: [], sync });
  assert.deepEqual(R.applySync(undefined, sync), { mode: 'desktop', deviceId: null, devices: [], sync });
  const paired = { state: 'online', deviceId: 'd_9' };
  assert.deepEqual(R.applySync(null, paired), { mode: 'desktop', deviceId: 'd_9', devices: [], sync: paired });
});

test('deviceId以sync為準', () => {
  const remote = { mode: 'desktop', deviceId: null, devices: [{ id: 'd_2' }], sync: { state: 'pairing', deviceId: null } };
  const next = R.applySync(remote, { state: 'online', deviceId: 'd_2' });
  assert.equal(next.deviceId, 'd_2');
  assert.deepEqual(next.devices, [{ id: 'd_2' }], 'devices 不受 sync 影響');
  assert.equal(remote.deviceId, null, '不改傳入的物件');
});

test('applySync_瀏覽器的remote不受影響', () => {
  const remote = { mode: 'browser', deviceId: null, devices: [], sync: null };
  assert.equal(R.applySync(remote, null), remote);
  assert.equal(R.applySync(remote, { state: 'online' }), remote);
});

test('applyDevices_remote為null不變', () => {
  assert.equal(R.applyDevices(null, [{ id: 'd_1' }]), null);
  assert.equal(R.applyDevices(undefined, [{ id: 'd_1' }]), null);
  const remote = { mode: 'browser', deviceId: null, devices: [{ id: 'd_1' }], sync: null };
  assert.deepEqual(R.applyDevices(remote, [{ id: 'd_2' }]).devices, [{ id: 'd_2' }]);
  assert.deepEqual(R.applyDevices(remote, null).devices, []);
  assert.deepEqual(remote.devices, [{ id: 'd_1' }], '不改傳入的物件');
});

// ───── 短暫斷線 ─────

test('trackDeviceLinks_看到從線上變離線的電腦_寬限期內是重新連線中', () => {
  const old = NOW - 10 * MIN;
  const prev = [{ id: 'd_a', online: true, lastSeenAt: old }, { id: 'd_b', online: false, lastSeenAt: old }, { id: 'd_c', online: true, lastSeenAt: old }];
  const next = [{ id: 'd_a', online: false, lastSeenAt: old }, { id: 'd_b', online: false, lastSeenAt: old }, { id: 'd_c', online: true, lastSeenAt: old }];
  const [a, b, c] = R.trackDeviceLinks(prev, next, NOW);
  assert.deepEqual([a.offlineSeenAt, a.reconnecting], [NOW, true], '這一刻看到它斷線');
  assert.deepEqual([b.offlineSeenAt, b.reconnecting], [null, false], '本來就離線、最後上線很久以前');
  assert.equal(c, next[2], '線上的照原樣');
  assert.equal(next[0].reconnecting, undefined, '不改傳入的物件');

  const later = R.trackDeviceLinks([a, b, c], next, NOW + 30_000);
  assert.deepEqual([later[0].offlineSeenAt, later[0].reconnecting], [NOW, true], '沿用第一次看到的時間');
  assert.equal(R.nextLinkChange(later, NOW + 30_000), 30_000, '還有 30 秒變成離線');
  const expired = R.trackDeviceLinks(later, later, NOW + R.RECONNECT_GRACE_MS);
  assert.equal(expired[0].reconnecting, false, '滿一分鐘就是離線');
  assert.equal(R.nextLinkChange(expired, NOW + R.RECONNECT_GRACE_MS), null);
  assert.equal(R.trackDeviceLinks(expired, [{ id: 'd_a', online: true }], NOW + 2 * MIN)[0].reconnecting, undefined, '連回來就沒有標記');
});

test('reconnectGraceOf_init的reconnectGraceMs只能縮短_不合格照60秒_寬限期照它算', () => {
  assert.equal(R.reconnectGraceOf(null), R.RECONNECT_GRACE_MS);
  assert.equal(R.reconnectGraceOf({ mode: 'browser' }), R.RECONNECT_GRACE_MS, '正式站不送');
  assert.equal(R.reconnectGraceOf({ reconnectGraceMs: 5000 }), 5000);
  for (const bad of [999, 60_001, 2.5, '5000', -1, null]) assert.equal(R.reconnectGraceOf({ reconnectGraceMs: bad }), R.RECONNECT_GRACE_MS, String(bad));
  assert.equal(R.remoteFromInit({ remote: { mode: 'browser', devices: [], reconnectGraceMs: 5000 } }).reconnectGraceMs, 5000, 'init 的欄位留在 remote 上');
  assert.equal(R.applyDevices({ mode: 'browser', reconnectGraceMs: 5000 }, []).reconnectGraceMs, 5000, '換裝置清單不丟');

  const prev = [{ id: 'd_a', online: true, lastSeenAt: NOW - 10 * MIN }];
  const next = [{ id: 'd_a', online: false, lastSeenAt: NOW - 10 * MIN }];
  const [a] = R.trackDeviceLinks(prev, next, NOW, 5000);
  assert.equal(a.reconnecting, true);
  assert.equal(R.nextLinkChange([a], NOW + 1000, 5000), 4000, '還有 4 秒變成離線');
  assert.equal(R.trackDeviceLinks([a], next, NOW + 5000, 5000)[0].reconnecting, false, '5 秒的寬限期滿了就是離線');
  assert.equal(R.trackDeviceLinks([a], next, NOW + 5000)[0].reconnecting, true, '沒給寬限期照 60 秒');
});

test('trackDeviceLinks_沒看過它上線時_以伺服器的lastSeenAt起算', () => {
  // 頁面剛開（或瀏覽器剛重連）：沒有上一份
  const [fresh, stale, never, revoked] = R.trackDeviceLinks(null, [
    { id: 'd_a', online: false, lastSeenAt: NOW - 20_000 },
    { id: 'd_b', online: false, lastSeenAt: NOW - 2 * MIN },
    { id: 'd_c', online: false, lastSeenAt: null },
    { id: 'd_d', online: false, lastSeenAt: NOW - 1000, revoked: { at: NOW, reason: 'retired' } },
  ], NOW);
  assert.equal(fresh.reconnecting, true);
  assert.equal(R.nextLinkChange([fresh], NOW), 40_000);
  assert.equal(stale.reconnecting, false);
  assert.equal(never.reconnecting, false);
  assert.equal(revoked.reconnecting, false, '已撤銷的一律不是');
  assert.equal(R.isReconnecting({ online: false, lastSeenAt: NOW + 20_000 }, NOW), true, '伺服器時間稍微超前也算');
  assert.equal(R.isReconnecting({ online: false, lastSeenAt: NOW + 10 * MIN }, NOW), false, '差太多（時鐘不準）當成離線');
  assert.equal(R.isReconnecting({ online: null, lastSeenAt: NOW }, NOW), false, '不知道在不在線的不算');
  assert.equal(R.isReconnecting({ online: false, lastSeenAt: NOW }, undefined), false);
  assert.deepEqual(R.trackDeviceLinks(undefined, undefined, NOW), []);
});

test('剛斷線的標記帶到電腦頁_各電腦CLI_矩陣格與裝置小點', () => {
  const d = { id: 'd_a', name: 'OFFICE-PC', online: false, reconnecting: true, lastSeenAt: NOW - 10_000 };
  assert.equal(R.deviceView(d).reconnecting, true);
  assert.equal(R.deviceView(Object.assign({}, d, { revoked: { at: NOW, reason: 'retired' } })).reconnecting, false);
  assert.equal(R.deviceCliModel({ d_a: { Claude: {} } }, { devices: [d] }, ['Claude'])[0].reconnecting, true);
  const account = { id: 'a', devices: [{ deviceId: 'd_a', deviceName: 'OFFICE-PC', status: 'ready', statusText: '已登入', online: false }] };
  const v = R.cellView(account.devices[0], d, NOW, {});
  assert.deepEqual([v.dim, v.reconnecting], [true, true]);
  assert.match(R.deviceDots(account, [d], NOW)[0].title, /・重新連線中$/);
  assert.match(R.deviceDots(account, [Object.assign({}, d, { reconnecting: false })], NOW)[0].title, /・離線$/);
});

// ───── 矩陣 ─────

const dev = (id, extra) => Object.assign({ id, name: id.toUpperCase(), signedName: id.toUpperCase(), online: true, lastSeenAt: NOW - 5 * MIN, revoked: null }, extra);
const cell = (deviceId, extra) => Object.assign({ deviceId, deviceName: deviceId.toUpperCase(), self: false, status: 'ready', statusText: '已登入', email: 'me@example.test', plan: null,
  accessExpiresAt: null, loginExpiresAt: null, identityOk: true, usagePoller: false, online: true, checkedAt: null }, extra);

test('matrix_欄跳過撤銷且照順序', () => {
  const remote = { mode: 'browser', deviceId: null, sync: null,
    devices: [dev('d_b'), dev('d_a', { online: false }), dev('d_x', { revoked: { at: NOW, reason: 'retired' } })] };
  const accounts = [{ id: 'la_1', provider: 'Claude', devices: [cell('d_a', { online: false }), cell('d_b', { status: 'absent' }), cell('d_x')] }];
  const m = R.matrixModel(accounts, remote, NOW);
  assert.deepEqual(m.columns.map((d) => d.id), ['d_b', 'd_a']);
  assert.equal(m.rows.length, 1);
  assert.deepEqual(m.rows[0].cells.map((c) => c && c.kind), ['absent', 'ready']);
  assert.equal(m.rows[0].cells[1].dim, true, 'd_a 離線');
});

test('matrix_devices不是陣列不畫列', () => {
  const remote = { mode: 'desktop', deviceId: 'd_a', sync: { state: 'online' }, devices: [dev('d_a', { self: true })] };
  const accounts = [
    { id: 'a1', provider: 'Claude', devices: null },
    { id: 'a2', provider: 'Codex' },
    { id: 'a3', provider: 'Claude', devices: [cell('d_a', { self: true })] },
    { id: 'a4', provider: 'Codex', devices: [] },
  ];
  const m = R.matrixModel(accounts, remote, NOW);
  assert.deepEqual(m.rows.map((r) => r.account.id), ['a3', 'a4']);
  assert.deepEqual(m.rows[1].cells, [null], '那台沒有格子的是 null');
  assert.deepEqual(R.matrixModel(accounts, null, NOW).columns, []);
});

test('cell_absent先於identityOk', () => {
  const v = R.cellView(cell('d_a', { status: 'absent', identityOk: false, statusText: '尚未加入' }), dev('d_a'), NOW, {});
  assert.equal(v.kind, 'absent');
  assert.equal(v.text, '尚未加入');
  assert.equal(v.action, null);
});

test('cell_優先順序表逐列', () => {
  const d = dev('d_a', { name: 'OFFICE-PC' });
  const cases = [
    [{ status: 'absent' }, {}, 'absent', '尚未加入', 'none', null],
    [{ status: 'ready', identityOk: false, email: 'other@example.test' }, {}, 'identity', '身分不符', 'bad', '那台登入的是 other@example.test'],
    [{ status: 'notInstalled', identityOk: false }, {}, 'identity', '身分不符', 'bad', '那台登入的是 me@example.test'],
    [{ status: 'notInstalled', statusText: 'x' }, {}, 'notInstalled', '未安裝 CLI', 'muted', null],
    [{ status: 'checking', statusText: '檢查中…' }, {}, 'checking', '檢查中…', 'info', null],
    [{ status: 'error', statusText: 'CLI 跑不起來' }, {}, 'error', 'CLI 跑不起來', 'bad', null],
    [{ status: 'apiKey', statusText: '' }, {}, 'apiKey', 'API Key 登入', 'warn', null],
    [{ status: 'notLoggedIn', statusText: '尚未登入' }, { provider: 'Claude' }, 'notLoggedIn', '尚未登入', 'muted', '到 OFFICE-PC 完成登入'],
    [{ status: 'notLoggedIn', statusText: '尚未登入' }, { provider: 'Codex' }, 'notLoggedIn', '尚未登入', 'muted', '到 OFFICE-PC 登入，或用裝置碼從網頁登入'],
    [{ status: 'notLoggedIn', statusText: '' }, { provider: 'Claude', self: true }, 'notLoggedIn', '尚未登入', 'muted', '到這台完成登入'],
    [{ status: 'ready' }, {}, 'ready', '已登入', 'ok', 'me@example.test'],
    [{ status: 'weird', statusText: '' }, {}, 'error', '發生錯誤', 'bad', null],
  ];
  for (const [extra, opts, kind, text, tone, hint] of cases) {
    const v = R.cellView(cell('d_a', extra), d, NOW, opts);
    const label = JSON.stringify(extra) + JSON.stringify(opts);
    assert.equal(v.kind, kind, label);
    assert.equal(v.text, text, label);
    assert.equal(v.tone, tone, label);
    assert.equal(v.hint, hint, label);
    assert.equal(v.action, null, label);
  }
});

test('cell_到期門檻邊界', () => {
  const d = dev('d_a');
  const at = (extra) => R.cellView(cell('d_a', extra), d, NOW, {});
  assert.equal(at({ loginExpiresAt: NOW + 3 * DAY + 1 }).expiring, null);
  const soon = at({ loginExpiresAt: NOW + 3 * DAY });
  assert.equal(soon.expiring, 'soon');
  assert.equal(soon.tone, 'warn');
  assert.equal(soon.hint, 'me@example.test・登入即將到期');
  assert.equal(at({ loginExpiresAt: NOW + 3 * DAY - 1 }).expiring, 'soon');
  assert.equal(at({ loginExpiresAt: NOW + 1 }).expiring, 'soon');
  const expired = at({ loginExpiresAt: NOW });
  assert.equal(expired.expiring, 'expired');
  assert.equal(expired.text, '登入已到期');
  assert.equal(expired.tone, 'bad');
  assert.equal(at({ loginExpiresAt: NOW - 1 }).expiring, 'expired');

  assert.equal(at({ accessExpiresAt: NOW + 10 * MIN + 1 }).tokenStale, false);
  const stale = at({ accessExpiresAt: NOW + 10 * MIN });
  assert.equal(stale.tokenStale, true);
  assert.equal(stale.tone, 'warn');
  assert.equal(stale.hint, 'me@example.test・權杖待更新');
  assert.equal(stale.action, null, '權杖待更新不給動作');
  assert.equal(at({ accessExpiresAt: NOW + 10 * MIN - 1 }).tokenStale, true);
  assert.equal(at({ accessExpiresAt: NOW - DAY }).tokenStale, true, '已過期也算');
  const both = at({ accessExpiresAt: NOW - 1, loginExpiresAt: NOW - 1 });
  assert.equal(both.hint, 'me@example.test', '登入已到期時不另寫權杖待更新');
  assert.equal(R.cellView(cell('d_a', { loginExpiresAt: NOW - 1 }), d, undefined, {}).expiring, null, '沒有現在時間就不判斷到期');
});

test('cell_離線調暗帶lastSeenAt', () => {
  const v = R.cellView(cell('d_a', { online: false }), dev('d_a', { online: false, lastSeenAt: NOW - DAY }), NOW, {});
  assert.equal(v.kind, 'ready');
  assert.equal(v.dim, true);
  assert.equal(v.lastSeenAt, NOW - DAY);
  const on = R.cellView(cell('d_a'), dev('d_a'), NOW, {});
  assert.equal(on.dim, false);
  assert.equal(on.lastSeenAt, null);
  const fromDevice = R.cellView(cell('d_a', { online: undefined }), dev('d_a', { online: false }), NOW, {});
  assert.equal(fromDevice.dim, true, '格子沒有 online 時看裝置');
});

test('cell_poller標記', () => {
  assert.equal(R.cellView(cell('d_a', { usagePoller: true }), dev('d_a'), NOW, {}).poller, true);
  assert.equal(R.cellView(cell('d_a', { usagePoller: true, status: 'absent' }), dev('d_a'), NOW, {}).poller, true);
  assert.equal(R.cellView(cell('d_a'), dev('d_a'), NOW, {}).poller, false);
});

test('cell_remoteOnly的self格給loginHere', () => {
  const absent = cell('d_a', { status: 'absent', self: true });
  const v = R.cellView(absent, dev('d_a', { self: true }), NOW, { remoteOnly: true, self: true });
  assert.equal(v.action, 'loginHere');
  assert.equal(R.cellView(absent, dev('d_a'), NOW, { remoteOnly: true, self: false }).action, null, '不是 self 的 absent 格沒有動作');
  assert.equal(R.cellView(absent, dev('d_a'), NOW, { remoteOnly: false, self: true }).action, null, '本機帳戶的 absent 格沒有動作');
  const remote = { mode: 'desktop', deviceId: 'd_a', sync: { state: 'online', deviceId: 'd_a' }, devices: [dev('d_a'), dev('d_b')] };
  const m = R.matrixModel([{ id: 'la_9', provider: 'Codex', remoteOnly: true, devices: [cell('d_a', { status: 'absent' }), cell('d_b')] }], remote, NOW);
  assert.equal(m.rows[0].cells[0].action, 'loginHere', 'remote.deviceId 也認得這台');
  assert.equal(m.rows[0].cells[1].action, null);
});

test('deviceDots_absent不畫_各色調', () => {
  const devices = new Map([
    ['d_ok', dev('d_ok')],
    ['d_off', dev('d_off', { online: false })],
    ['d_soon', dev('d_soon')],
    ['d_tok', dev('d_tok')],
    ['d_id', dev('d_id', { online: false })],
    ['d_err', dev('d_err')],
    ['d_abs', dev('d_abs')],
    ['d_rev', dev('d_rev', { revoked: { at: NOW, reason: 'retired' } })],
    ['d_nl', dev('d_nl')],
  ]);
  const account = { id: 'la_1', provider: 'Claude', devices: [
    cell('d_ok'), cell('d_off', { online: false }), cell('d_soon', { loginExpiresAt: NOW + DAY }), cell('d_tok', { accessExpiresAt: NOW + MIN }),
    cell('d_id', { identityOk: false, online: false }), cell('d_err', { status: 'error', statusText: '壞了' }), cell('d_abs', { status: 'absent' }),
    cell('d_rev'), cell('d_nl', { status: 'notLoggedIn' }), cell('d_ok', { status: 'error' }),
  ] };
  const dots = R.deviceDots(account, devices, NOW);
  assert.deepEqual(dots.map((d) => [d.deviceId, d.tone]), [
    ['d_ok', 'ok'], ['d_off', 'off'], ['d_soon', 'warn'], ['d_tok', 'warn'], ['d_id', 'bad'], ['d_err', 'bad'], ['d_nl', 'muted'],
  ]);
  assert.equal(dots[0].title, 'D_OK：已登入（me@example.test）');
  assert.match(dots[1].title, /・離線$/);
  assert.equal(dots[5].title, 'D_ERR：壞了');
  // 物件與陣列形式的 devicesById 也可以
  const asObj = Object.fromEntries(devices);
  assert.deepEqual(R.deviceDots(account, asObj, NOW).map((d) => d.tone), dots.map((d) => d.tone));
  assert.deepEqual(R.deviceDots(account, Array.from(devices.values()), NOW).map((d) => d.tone), dots.map((d) => d.tone));
  assert.deepEqual(R.deviceDots({ id: 'a', devices: null }, devices, NOW), []);
  assert.deepEqual(R.deviceDots(null, devices, NOW), []);
});

// ───── 標題列同步小點 ─────

test('syncDot_每個狀態', () => {
  assert.equal(R.syncDot(null), null);
  assert.equal(R.syncDot(undefined), null);
  const t = (sync) => { const v = R.syncDot(sync); return [v.state, v.text, v.tone]; };
  assert.deepEqual(t({ state: 'pairing' }), ['pairing', '同步：配對中', 'info']);
  assert.deepEqual(t({ state: 'connecting' }), ['connecting', '同步：連線中', 'info']);
  assert.deepEqual(t({ state: 'online', pending: 0 }), ['online', '同步：已連線', 'ok']);
  assert.deepEqual(t({ state: 'offline', pending: 0 }), ['offline', '同步：離線', 'warn']);
  assert.deepEqual(t({ state: 'offline', pending: 12 }), ['offline', '同步：離線（12 筆待送）', 'warn']);
  assert.deepEqual(t({ state: 'paused' }), ['paused', '同步：已暫停', 'muted']);
  assert.deepEqual(t({ state: 'stopped', detail: '權杖已撤銷，請重新配對' }), ['stopped', '同步：已停止（權杖已撤銷，請重新配對）', 'bad']);
  assert.deepEqual(t({ state: 'stopped', detail: null }), ['stopped', '同步：已停止', 'bad']);
  assert.deepEqual(t({ state: 'online', confirm: { kind: 'hubIdentity', fingerprint: 'AAAA-BBBB-CCCC-DDDD' } }), ['online', '同步：要在這台確認伺服器身分', 'bad']);
  const skew = R.syncDot({ state: 'online', clockSkewMs: -185000 });
  assert.equal(skew.tone, 'warn');
  assert.match(skew.title, /請同步 Windows 時間/);
  assert.match(skew.title, /約 3 分鐘/);
  assert.doesNotMatch(R.syncDot({ state: 'online', clockSkewMs: null }).title, /Windows 時間/);
  const offline = R.syncDot({ state: 'offline', pending: 2, detail: '伺服器連不上，30 秒後重試' });
  assert.equal(offline.title, '同步：離線（2 筆待送）\n伺服器連不上，30 秒後重試');
  assert.match(R.syncDot({ state: 'pairing', pairing: { error: '配對碼已過期' } }).title, /配對碼已過期/);
});

// ───── 電腦頁 ─────

test('deviceView_簽署名稱不同才另標', () => {
  const same = R.deviceView(dev('d_a', { name: 'OFFICE-PC', signedName: 'OFFICE-PC', keyStorage: 'tpm', remoteControlLevel: 'Off', verified: true }));
  assert.equal(same.signedNameNote, null);
  assert.equal(same.keyStorageText, 'TPM');
  assert.equal(same.policyText, '其他電腦的遠端操作：不接受');
  assert.equal(same.verifiedWarning, null);
  assert.equal(same.revoked, null);
  assert.equal(same.canRename, true);
  assert.equal(same.canRemove, false, '沒撤銷的不能移除');
  const renamed = R.deviceView(dev('d_a', { name: '辦公室', signedName: 'OFFICE-PC', keyStorage: 'software', remoteControlLevel: 'Control', allowRemoteFullAccess: true }));
  assert.equal(renamed.signedNameNote, '配對時名稱：OFFICE-PC');
  assert.equal(renamed.keyStorageText, '軟體');
  assert.equal(renamed.policyText, '其他電腦的遠端操作：可以操作（含完全自動）');
  assert.equal(R.deviceView(dev('d_a', { remoteControlLevel: 'View' })).policyText, '其他電腦的遠端操作：只能查看');
  assert.equal(R.deviceView(dev('d_a', { remoteControlLevel: 'Off', allowRemoteFullAccess: true })).policyText, '其他電腦的遠端操作：不接受');
  assert.equal(R.deviceView(dev('d_a', { remoteControlLevel: 'Root' })).policyText, '其他電腦的遠端操作：不接受', '不認得的層級當成 Off');
  const retired = R.deviceView(dev('d_a', { revoked: { at: NOW, reason: 'retired' } }));
  assert.deepEqual(retired.revoked, { at: NOW, reason: 'retired', text: '已撤銷・退役' });
  assert.equal(retired.canRename, false);
  assert.equal(retired.canRemove, true, '已撤銷的才能移除（設計 14.22）');
  assert.equal(retired.online, false, '已撤銷的不算在線');
  assert.equal(R.deviceView(dev('d_a', { revoked: { at: NOW, reason: 'compromised' } })).revoked.text, '已撤銷・疑似遭入侵');
  assert.equal(R.deviceView(dev('d_a', { revoked: { at: NOW, reason: '??' } })).revoked.text, '已撤銷');
  assert.match(R.deviceView(dev('d_a', { verified: false })).verifiedWarning, /名冊/);
  assert.equal(R.deviceView(dev('d_a', { verified: null })).verifiedWarning, null, '瀏覽器的 verified 是 null，不警告');
  assert.equal(R.deviceView(dev('d_a', { name: '', signedName: 'SIGNED' })).name, 'SIGNED');
  assert.equal(R.deviceView(null), null);
});

test('deviceList_撤銷的排最後_桌面認得這台', () => {
  const remote = { mode: 'desktop', deviceId: 'd_b', sync: { state: 'online' }, devices: [
    dev('d_r', { revoked: { at: NOW, reason: 'retired' } }), dev('d_a'), null, dev('d_b'), { name: '沒有 id' }, dev('d_c', { revoked: { at: NOW, reason: 'compromised' } }),
  ] };
  const list = R.deviceList(remote);
  assert.deepEqual(list.map((v) => v.id), ['d_a', 'd_b', 'd_r', 'd_c']);
  assert.deepEqual(list.map((v) => v.self), [false, true, false, false]);
  assert.equal(list[2].revoked.text, '已撤銷・退役');
  assert.deepEqual(R.deviceList(null), []);
  assert.deepEqual(R.deviceList({ mode: 'browser', devices: 'x' }), []);
});

// ───── 各電腦 CLI ─────

test('deviceCliView', () => {
  const base = { installed: true, version: '2.1.297', installKind: 'native', latest: '2.1.297', updateAvailable: false, runningOnOldVersion: 0, restartPending: 0, installBroken: null, checkedAt: NOW };
  const ok = R.deviceCliView(base);
  assert.deepEqual([ok.kind, ok.text, ok.tone, ok.notes], ['ok', '2.1.297', 'ok', []]);
  assert.equal(ok.installKind, 'native');
  assert.equal(ok.checkedAt, NOW);
  const none = R.deviceCliView(Object.assign({}, base, { installed: false, version: null, runningOnOldVersion: 2 }));
  assert.deepEqual([none.kind, none.text, none.tone, none.notes], ['notInstalled', '未安裝', 'muted', []]);
  const upd = R.deviceCliView(Object.assign({}, base, { latest: '2.1.300', updateAvailable: true }));
  assert.deepEqual([upd.kind, upd.text, upd.tone], ['update', '2.1.297 → 2.1.300（有新版）', 'warn']);
  const old = R.deviceCliView(Object.assign({}, base, { runningOnOldVersion: 2 }));
  assert.deepEqual(old.notes, ['2 個對話仍在用舊版執行']);
  const pend = R.deviceCliView(Object.assign({}, base, { restartPending: 3, runningOnOldVersion: 1 }));
  assert.deepEqual(pend.notes, ['1 個對話仍在用舊版執行', '3 個對話待換版']);
  const broken = R.deviceCliView(Object.assign({}, base, { installBroken: 'Claude 安裝不一致，請修復' }));
  assert.deepEqual([broken.kind, broken.text, broken.tone], ['broken', 'Claude 安裝不一致，請修復', 'bad']);
  const unknown = R.deviceCliView(undefined);
  assert.deepEqual([unknown.kind, unknown.text], ['unknown', '沒有回報']);
});

// ───── 連線狀態列 ─────

test('connectionBanner_每個情境', () => {
  const b = (conn, hubInit, now = NOW) => R.connectionBanner(conn, hubInit, now);
  assert.deepEqual([b(null, false).kind, b(null, false).text, b(null, false).retry], ['connecting', '連線中…', false]);
  assert.equal(b({ state: 'online', reason: null, retryAt: null }, false).text, '載入中…');
  assert.equal(b({ state: 'online', reason: null, retryAt: null }, true), null);
  const rec = b({ state: 'reconnecting', reason: null, retryAt: NOW + 4200 }, true);
  assert.deepEqual([rec.kind, rec.text, rec.retry, rec.retryAt], ['reconnecting', '重新連線中…（5 秒後重試）', true, NOW + 4200]);
  assert.equal(b({ state: 'reconnecting', reason: null, retryAt: NOW + 1000 }, true).text, '重新連線中…（1 秒後重試）');
  assert.equal(b({ state: 'reconnecting', reason: null, retryAt: NOW + 1 }, true).text, '重新連線中…（1 秒後重試）', '無條件進位');
  assert.equal(b({ state: 'reconnecting', reason: null, retryAt: NOW - 500 }, true).text, '重新連線中…', '時間到了不寫秒數');
  assert.equal(b({ state: 'reconnecting', reason: null, retryAt: null }, true).text, '重新連線中…');
  const many = b({ state: 'reconnecting', reason: 'tooManyConnections', retryAt: NOW + 30000 }, true);
  assert.equal(many.kind, 'tooMany');
  assert.equal(many.text, '這個瀏覽器開了太多分頁（每個登入最多 8 個），關掉一些後會自動重新連線（30 秒後重試）');
  assert.equal(many.retry, true);
  const net = b({ state: 'offline', reason: 'offline', retryAt: null }, true);
  assert.deepEqual([net.kind, net.text, net.retry], ['offline', '離線：網路中斷', false]);
  const srv = b({ state: 'offline', reason: 'server', retryAt: NOW + 29001 }, true);
  assert.deepEqual([srv.kind, srv.text, srv.retry], ['server', '連不上伺服器（30 秒後重試）', true]);
  assert.equal(b({ state: 'offline', reason: null, retryAt: null }, true).text, '連不上伺服器');
});

// ───── 擁有者操作的錯誤文字 ─────

test('ownerErrorText', () => {
  assert.equal(R.ownerErrorText('revoke', 400, 'invalid_request'), '撤銷原因不正確');
  assert.equal(R.ownerErrorText('revoke', 404, 'not_found'), '找不到這台電腦（可能已經刪除）');
  assert.equal(R.ownerErrorText('revoke', 409, 'already_revoked'), '這台電腦已經撤銷了');
  assert.match(R.ownerErrorText('revoke', 503, 'unavailable'), /可能已經撤銷/);
  assert.equal(R.ownerErrorText('revoke', 429, 'rate_limited', 30), '嘗試太頻繁，30 秒後再試');
  assert.equal(R.ownerErrorText('revoke', 429, 'rate_limited', null), '嘗試太頻繁，請稍後再試');
  assert.equal(R.ownerErrorText('remove', 404, 'not_found'), '找不到這台電腦（可能已經移除）');
  assert.equal(R.ownerErrorText('remove', 409, 'not_revoked'), '要先撤銷這台電腦才能移除');
  assert.equal(R.ownerErrorText('remove', 409, 'already_removed'), '這台電腦已經移除了');
  assert.match(R.ownerErrorText('remove', 503, 'unavailable'), /可能已經移除/);
  assert.equal(R.ownerErrorText('delete', 404, 'not_found'), '找不到這個帳戶（可能已經從目錄刪除）');
  assert.equal(R.ownerErrorText('delete', 503, 'unavailable'), '伺服器暫時無法處理，請稍後再試');
  assert.equal(R.ownerErrorText('stepup', 403, 'password_failed'), '密碼錯誤');
  assert.equal(R.ownerErrorText('stepup', 429, 'rate_limited', 12.2), '嘗試太頻繁，13 秒後再試');
  assert.equal(R.ownerErrorText('revoke', 0, null), '連不上伺服器，請確認網路後再試');
  assert.equal(R.ownerErrorText('delete', undefined, null), '連不上伺服器，請確認網路後再試');
  assert.equal(R.ownerErrorText('revoke', 401, 'unauthenticated'), '登入已失效，請重新登入');
  assert.equal(R.ownerErrorText('revoke', 403, 'forbidden_origin'), '請求來源不符，請重新整理頁面');
  assert.equal(R.ownerErrorText('delete', 500, null), '伺服器發生錯誤（500），請稍後再試');
  assert.equal(R.ownerErrorText('delete', 418, null), '伺服器回應 418');
});

test('countDeviceConversations_只算擁有者是這台的', () => {
  const list = [{ id: 'c1', deviceId: 'd_a' }, { id: 'c2', deviceId: 'd_b' }, { id: 'c3', deviceId: 'd_a' }, null, { id: 'c4' }];
  assert.equal(R.countDeviceConversations(list, 'd_a'), 2);
  assert.equal(R.countDeviceConversations(list, 'd_c'), 0);
  assert.equal(R.countDeviceConversations(null, 'd_a'), 0);
  assert.equal(R.countDeviceConversations(list, ''), 0);
});

// ───── 其他 ─────

test('validDeviceName', () => {
  assert.equal(R.validDeviceName('OFFICE-PC'), true);
  assert.equal(R.validDeviceName('  辦公室  '), true);
  assert.equal(R.validDeviceName(''), false);
  assert.equal(R.validDeviceName('   '), false);
  assert.equal(R.validDeviceName('a'.repeat(60)), true);
  assert.equal(R.validDeviceName('  ' + 'a'.repeat(60) + '  '), true, '去頭尾空白後才算長度');
  assert.equal(R.validDeviceName('a'.repeat(61)), false);
  assert.equal(R.validDeviceName(null), false);
  assert.equal(R.validDeviceName(42), false);
  // 控制字元與孤立的代理字元照伺服器的 LabelRules.Clean 去掉
  const ctrl = String.fromCharCode(7);
  const lone = String.fromCharCode(0xd800);
  assert.equal(R.cleanDeviceName('A' + ctrl + 'B' + lone), 'AB');
  assert.equal(R.validDeviceName(ctrl + lone), false);
  assert.equal(R.validDeviceName('a'.repeat(60) + ctrl), true);
});

test('countLocalAccounts_不算remoteOnly', () => {
  assert.equal(R.countLocalAccounts([{ id: 'a1' }, { id: 'la_2', remoteOnly: true }, { id: 'a3', remoteOnly: false }]), 2);
  assert.equal(R.countLocalAccounts([]), 0);
  assert.equal(R.countLocalAccounts(null), 0);
});

// ───── 儀表板的區塊（步驟 9）─────

test('matrixVisible_瀏覽器一律_桌面要有連結的帳戶', () => {
  const browser = { mode: 'browser', deviceId: null, devices: [], sync: null };
  const desktop = { mode: 'desktop', deviceId: 'd_a', devices: [dev('d_a')], sync: { state: 'online' } };
  assert.equal(R.matrixVisible([], browser), true, '瀏覽器沒有帳戶也顯示（說明還沒有帳戶）');
  assert.equal(R.matrixVisible([{ id: 'a1', devices: null }], desktop), false, '桌面沒有連結的帳戶不顯示');
  assert.equal(R.matrixVisible([{ id: 'a1', devices: null }, { id: 'a2', devices: [] }], desktop), true);
  assert.equal(R.matrixVisible([{ id: 'a1', devices: [] }], null), false, '未配對不顯示');
  assert.equal(R.matrixVisible([{ id: 'a1', devices: [] }], undefined), false, '還沒收到 init 不顯示');
  assert.equal(R.matrixVisible(null, desktop), false);
});

test('deviceCliModel_照電腦順序_跳過撤銷_只列有回報的種類', () => {
  const remote = { mode: 'browser', deviceId: null, sync: null, devices: [
    dev('d_b', { name: '筆電', online: false, lastSeenAt: NOW - DAY }), dev('d_a', { name: '桌機', self: true }), dev('d_r', { revoked: { at: NOW, reason: 'retired' } }),
  ] };
  const v = (o) => Object.assign({ installed: true, version: '1.0.0', latest: '1.0.0', updateAvailable: false, runningOnOldVersion: 0, restartPending: 0, installBroken: null, installKind: 'npm', checkedAt: NOW }, o);
  const devicesCli = {
    d_a: { Claude: v({ version: '2.1.0' }) },
    d_x: { Codex: v({ installed: false }) },
    d_r: { Claude: v() },
    d_b: { Claude: v({ updateAvailable: true, latest: '2.1.0', version: '2.0.9' }), Codex: v({ version: '0.50.0' }) },
  };
  const m = R.deviceCliModel(devicesCli, remote, ['Claude', 'Codex', 'Antigravity', 'Grok']);
  assert.deepEqual(m.map((d) => d.deviceId), ['d_b', 'd_a', 'd_x'], 'remote 的順序、已撤銷的不列、remote 沒有的接在後面');
  assert.deepEqual(m[0].clis.map((c) => c.provider), ['Claude', 'Codex'], '沒有任何電腦回報的種類不列');
  assert.deepEqual(m[0].clis.map((c) => c.view.kind), ['update', 'ok']);
  assert.deepEqual([m[0].name, m[0].online, m[0].lastSeenAt, m[0].self], ['筆電', false, NOW - DAY, false]);
  assert.deepEqual([m[1].name, m[1].self, m[1].online], ['桌機', true, true]);
  assert.deepEqual(m[1].clis.map((c) => c.view.kind), ['ok', 'unknown'], '那台沒回報的種類是 unknown');
  assert.deepEqual([m[2].name, m[2].online, m[2].self], ['d_x', null, false], 'remote 沒有的電腦用 id、在不在線不知道');
  const desk = R.deviceCliModel({ d_a: { Claude: v() } }, { mode: 'desktop', deviceId: 'd_a', devices: [dev('d_a')], sync: {} }, ['Claude']);
  assert.equal(desk[0].self, true, 'remote.deviceId 也認得這台');
  assert.deepEqual(R.deviceCliModel(null, remote, ['Claude']), []);
  assert.deepEqual(R.deviceCliModel({}, remote, ['Claude']), []);
  assert.deepEqual(R.deviceCliModel({ d_a: 'x' }, null, ['Claude']), [], '內容不是物件的電腦不列');
});

test('reorderIds_上移下移_不含remoteOnly', () => {
  const accounts = [{ id: 'la_1' }, { id: 'la_2' }, { id: 'la_ro', remoteOnly: true }, { id: 'la_3' }];
  assert.deepEqual(R.reorderIds(accounts, 'la_2', -1), ['la_2', 'la_1', 'la_3']);
  assert.deepEqual(R.reorderIds(accounts, 'la_2', 1), ['la_1', 'la_3', 'la_2'], '跳過 remoteOnly');
  assert.equal(R.reorderIds(accounts, 'la_1', -1), null, '第一個不能上移');
  assert.equal(R.reorderIds(accounts, 'la_3', 1), null, '最後一個不能下移');
  assert.equal(R.reorderIds(accounts, 'la_ro', -1), null, 'remoteOnly 不能排');
  assert.equal(R.reorderIds(accounts, 'nope', 1), null);
  assert.equal(R.reorderIds(accounts, 'la_1', 0), null);
  assert.equal(R.reorderIds(null, 'la_1', 1), null);
  assert.deepEqual(accounts.map((a) => a.id), ['la_1', 'la_2', 'la_ro', 'la_3'], '不改傳入的陣列');
});

test('claudeLoginUrlView_只認https的claude網域', () => {
  assert.deepEqual(R.claudeLoginUrlView('https://claude.com/cai/oauth/authorize?code=true&state=s1'), { link: 'https://claude.com/cai/oauth/authorize?code=true&state=s1' });
  assert.ok(R.claudeLoginUrlView('https://CLAUDE.AI/oauth/authorize?state=x'));
  for (const bad of ['http://claude.com/x', 'https://claude.com:8443/x', 'https://u:p@claude.com/x', 'https://claude.com.evil.example/x',
    'https://platform.claude.com/x', 'javascript:alert(1)', '', null, 42, 'https://claude.com/' + 'x'.repeat(4100)]) {
    assert.equal(R.claudeLoginUrlView(bad), null, String(bad).slice(0, 40));
  }
});

test('loginCodeInput_去頭尾空白_空的太長或有空白不收', () => {
  assert.equal(R.loginCodeInput('  abc123#st_1 \n'), 'abc123#st_1');
  assert.equal(R.loginCodeInput('abc 123'), null);
  assert.equal(R.loginCodeInput('   '), null);
  assert.equal(R.loginCodeInput('x'.repeat(2049)), null);
  assert.equal(R.loginCodeInput('x'.repeat(2048)).length, 2048);
  assert.equal(R.loginCodeInput(null), null);
});

test('loginCodeView_驗證碼與有效時間', () => {
  const ok = R.loginCodeView({ userCode: ' WXYZ-1234 ', verificationUrl: 'https://auth.openai.com/codex/device', expiresAt: NOW + 15 * MIN }, NOW);
  assert.deepEqual(ok, { userCode: 'WXYZ-1234', verificationUrl: 'https://auth.openai.com/codex/device', link: 'https://auth.openai.com/codex/device', expiresAt: NOW + 15 * MIN, leftMs: 15 * MIN, expired: false });
  const notHttps = R.loginCodeView({ userCode: 'A', verificationUrl: 'javascript:alert(1)', expiresAt: null }, NOW);
  assert.deepEqual([notHttps.link, notHttps.verificationUrl, notHttps.leftMs, notHttps.expired], [null, 'javascript:alert(1)', null, false], '不是 https 的網址只顯示文字');
  assert.equal(R.loginCodeView({ userCode: 'A', expiresAt: NOW }, NOW).expired, true);
  assert.equal(R.loginCodeView({ userCode: 'A', expiresAt: NOW + 1 }, NOW).expired, false);
  assert.equal(R.loginCodeView({ userCode: '' }, NOW), null);
  assert.equal(R.loginCodeView({ userCode: 42 }, NOW), null);
  assert.equal(R.loginCodeView(null, NOW), null);
  assert.equal(R.loginCodeView({ userCode: 'x'.repeat(65) }, NOW), null, '太長的驗證碼不顯示');
});

// ───── 傳輸抽象（Web/transport.js）─────
// transport.js 在模組求值時讀 window.chrome.webview 與 window.myriagentHubTransport：每個案例換一個 window 樁、匯入一份新的模組

async function importTransport(win) {
  const had = Object.prototype.hasOwnProperty.call(globalThis, 'window');
  const saved = globalThis.window;
  globalThis.window = win;
  try {
    return await importWeb('transport.js');
  } finally {
    if (had) globalThis.window = saved;
    else delete globalThis.window;
  }
}

function fakeHub(extra) {
  const calls = { post: [], onMessage: [], retryNow: 0 };
  const hub = Object.assign({
    version: 1,
    ok: true,
    post: (m) => calls.post.push(m),
    onMessage: (fn) => calls.onMessage.push(fn),
    status: () => ({ state: 'online', hubInit: true }),
    retryNow: () => { calls.retryNow++; },
  }, extra);
  return { hub, calls };
}

test('transport_kind_webview', async () => {
  const posted = [];
  const listeners = [];
  const webview = {
    postMessage: (m) => posted.push(m),
    addEventListener: (type, fn) => listeners.push([type, fn]),
    postMessageWithAdditionalObjects: (m, files) => posted.push({ withFiles: m, files }),
  };
  const { hub, calls } = fakeHub();
  const T = await importTransport({ chrome: { webview }, myriagentHubTransport: hub });
  assert.equal(T.kind, 'webview', 'WebView2 優先（伺服器的腳本不會出現在桌面，萬一有也不用）');
  assert.equal(T.available, true);
  T.post({ cmd: 'ready', mode: 'main' });
  assert.deepEqual(posted, [{ cmd: 'ready', mode: 'main' }]);
  const got = [];
  T.onMessage((m) => got.push(['a', m]));
  T.onMessage((m) => got.push(['b', m]));
  assert.equal(listeners.length, 2);
  for (const [type, fn] of listeners) {
    assert.equal(type, 'message');
    fn({ data: { type: 'init' } });
  }
  assert.deepEqual(got, [['a', { type: 'init' }], ['b', { type: 'init' }]], '兩個訂閱者都收到');
  assert.equal(T.postWithFiles({ cmd: 'addFiles' }, ['f1']), true);
  assert.deepEqual(posted[1], { withFiles: { cmd: 'addFiles' }, files: ['f1'] });
  assert.equal(T.status(), null);
  T.retryNow();
  assert.deepEqual(calls, { post: [], onMessage: [], retryNow: 0 }, '沒有碰 hub');
});

test('transport_kind_hub', async () => {
  const { hub, calls } = fakeHub();
  const T = await importTransport({ myriagentHubTransport: hub });
  assert.equal(T.kind, 'hub');
  assert.equal(T.available, true);
  T.post({ cmd: 'ready', mode: 'main' });
  assert.deepEqual(calls.post, [{ cmd: 'ready', mode: 'main' }]);
  const a = () => {};
  const b = () => {};
  T.onMessage(a);
  T.onMessage(b);
  assert.deepEqual(calls.onMessage, [a, b], '訂閱者照註冊順序交給 transport-hub');
  assert.deepEqual(T.status(), { state: 'online', hubInit: true });
  T.retryNow();
  assert.equal(calls.retryNow, 1);
});

test('transport_kind_none', async () => {
  const stubs = [{ ok: false }, { version: 2 }, { version: '1' }, { ok: 'true' }].map((extra) => [JSON.stringify(extra), fakeHub(extra)]);
  const cases = [
    ...stubs.map(([label, s]) => [label, { myriagentHubTransport: s.hub }, s.calls]),
    ['myriagentHubTransport: null', { myriagentHubTransport: null }, null],
    ['沒有任何全域', {}, null],
    ['chrome 沒有 webview', { chrome: {} }, null],
  ];
  for (const [label, win, calls] of cases) {
    const T = await importTransport(win);
    assert.equal(T.kind, 'none', label);
    assert.equal(T.available, false, label);
    T.post({ cmd: 'ready' });
    T.onMessage(() => {});
    T.retryNow();
    assert.equal(T.status(), null, label);
    assert.equal(T.postWithFiles({ cmd: 'addFiles' }, []), false, label);
    if (calls) assert.deepEqual(calls, { post: [], onMessage: [], retryNow: 0 }, label + '：不合格的 transport-hub 一律不用');
  }
});

test('postWithFiles_hub回false', async () => {
  const { hub, calls } = fakeHub();
  const T = await importTransport({ myriagentHubTransport: hub });
  assert.equal(T.postWithFiles({ cmd: 'addFiles', conversationId: 'c1' }, ['f']), false, '瀏覽器版的上傳是第 4 階段');
  assert.deepEqual(calls.post, [], '也不改送沒有檔案的指令');
});

// ───── 瀏覽器版的傳輸層（src/MyriagentHub/wwwroot/transport-hub.js，傳統腳本）─────
// 以 node:vm 載入：沙箱只有 window、self（同一個物件）、navigator、crypto、TextEncoder、URL、計時器與 console，
// 沒有 document、addEventListener、signalR——載入不丟例外就證明最上層沒有碰 DOM。
// vm 裡建的物件與陣列是另一個 realm 的（原型不同），比對前先以 plain() 做 JSON 往返

const hubFile = path.join(repo, 'src', 'MyriagentHub', 'wwwroot', 'transport-hub.js');
const plain = (v) => JSON.parse(JSON.stringify(v));

function loadTransportHub(extra) {
  const sandbox = Object.assign({ navigator: { onLine: true }, crypto: webcrypto, TextEncoder, URL, setTimeout, clearTimeout, console }, extra);
  sandbox.window = sandbox;
  sandbox.self = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(readFileSync(hubFile, 'utf8'), sandbox, { filename: 'transport-hub.js' });
  return { T: sandbox.myriagentHubTransport, sandbox };
}

const H = loadTransportHub().T;
const HR = H.rules;
const ULID_RE = /^[0-7][0-9A-HJKMNP-TV-Z]{25}$/;

test('transportHub_載入不碰DOM', () => {
  const { T, sandbox } = loadTransportHub();
  assert.equal(T.version, 1);
  assert.equal(T.ok, false, '沒有 SignalR 程式庫（或還不連線）時 ok 是 false');
  assert.ok(Object.isFrozen(T), 'myriagentHubTransport 是凍結的');
  assert.ok(Object.isFrozen(T.rules), 'rules 是凍結的');
  assert.equal(sandbox.window.myriagentHubTransport, T);
  for (const name of ['post', 'onMessage', 'status', 'retryNow']) assert.equal(typeof T[name], 'function', name);
  assert.equal(HR.LAYOUT_KEY, 'myriagent.app.layout.v1');
});

test('ulid_向量', () => {
  const zeros = new Uint8Array(10);
  assert.equal(HR.ulid(0, zeros), '0'.repeat(26));
  assert.equal(HR.ulid(1, zeros), '0000000001' + '0'.repeat(16));
  const max = HR.ulid(2 ** 48 - 1, zeros);
  assert.equal(max[0], '7', '48 位元的首字最多是 7');
  assert.equal(max.slice(0, 10), '7ZZZZZZZZZ');
  assert.equal(HR.ulid(0, new Uint8Array(10).fill(255)).slice(10), 'Z'.repeat(16), '後 16 字是 80 位元');
  assert.equal(HR.ulid(0, Uint8Array.from([0, 0, 0, 0, 0, 0, 0, 0, 0, 1])).slice(10), '0'.repeat(15) + '1');
  // 現在的時間（Date.now() 的大小）照樣是合格的 ULID
  for (let i = 0; i < 20; i++) assert.match(HR.ulid(Date.now()), ULID_RE);
  assert.match(HR.ulid(NOW, Uint8Array.from([1, 2, 3, 4, 5, 6, 7, 8, 9, 10])), ULID_RE);
  assert.notEqual(HR.ulid(NOW), HR.ulid(NOW), '沒給位元組時用 crypto.getRandomValues');
});

test('backoff_序列與抖動範圍', () => {
  assert.equal(HR.backoffDelayMs(0, null, 0), 0);
  assert.equal(HR.backoffDelayMs(0, null, 0.999), 0, '第一次重試不等');
  const steps = [1000, 2000, 5000, 10000, 30000];
  steps.forEach((base, i) => {
    for (const r of [0, 0.5, 0.999]) {
      const d = HR.backoffDelayMs(i + 1, null, r);
      assert.ok(d >= base * 0.8 && d <= base, `attempt ${i + 1}、rand ${r}：${d} 在 ${base * 0.8}～${base}`);
    }
    assert.equal(HR.backoffDelayMs(i + 1, null, 0), base * 0.8, '只往下抖');
  });
  for (const attempt of [5, 6, 7, 20, 1000]) {
    for (const r of [0, 0.5, 0.999, 1]) assert.ok(HR.backoffDelayMs(attempt, null, r) <= 30000, `attempt ${attempt} 最慢 30 秒`);
  }
  for (const r of [0, 0.5, 0.999, 1]) {
    const d = HR.backoffDelayMs(0, 'tooManyConnections', r);
    assert.ok(d >= 30000 && d <= 36000, `tooManyConnections ${d} 在 30～36 秒`);
    assert.ok(HR.backoffDelayMs(9, 'tooManyConnections', r) >= 30000, '與次數無關');
  }
  assert.equal(HR.backoffDelayMs(-3, null, 0.5), 0, '負數當 0');
  for (let i = 0; i < 50; i++) {
    const d = HR.backoffDelayMs(2, null);
    assert.ok(d >= 1600 && d <= 2000, '沒給亂數時用 Math.random');
  }
});

test('connectionState', () => {
  assert.equal(HR.connectionState(true, true, 0), 'online');
  assert.equal(HR.connectionState(true, false, 9), 'online', '連著就是 online');
  assert.equal(HR.connectionState(false, false, 0), 'offline', 'navigator.onLine 為 false');
  for (const a of [0, 1, 2, 3, 4]) assert.equal(HR.connectionState(false, true, a), 'reconnecting', `attempt ${a}`);
  for (const a of [5, 6, 30]) assert.equal(HR.connectionState(false, true, a), 'offline', `attempt ${a} 起（30 秒那一階）`);
});

test('hubErrorCode', () => {
  assert.equal(HR.hubErrorCode('Server returned an error on close: Connection closed with an error. HubException: too_many_connections'), 'too_many_connections');
  assert.equal(HR.hubErrorCode(new Error("An unexpected error occurred invoking 'ClientState' on the server. HubException: rate_limited")), 'rate_limited');
  assert.equal(HR.hubErrorCode({ message: 'HubException: invalid' }), 'invalid');
  assert.equal(HR.hubErrorCode('WebSocket closed with status code: 1006 (no reason given).'), null);
  assert.equal(HR.hubErrorCode(undefined), null);
  assert.equal(HR.hubErrorCode(null), null);
  assert.equal(HR.hubErrorCode(new Error('')), null);
  assert.equal(HR.hubErrorCode(42), null);
});

/** n 層巢狀陣列，最裡面放 1 */
function nested(n) {
  let v = 1;
  for (let i = 0; i < n; i++) v = [v];
  return v;
}

test('shapeFits_與MessageGuard相同的邊界', () => {
  assert.equal(HR.shapeFits(nested(8)), true, '深度 8');
  assert.equal(HR.shapeFits(nested(9)), false, '深度 9');
  let obj = { v: 1 };
  for (let i = 1; i < 8; i++) obj = { o: obj };
  assert.equal(HR.shapeFits(obj), true, '物件 8 層');
  assert.equal(HR.shapeFits({ o: obj }), false, '物件 9 層');
  assert.equal(HR.shapeFits(new Array(4095).fill(0)), true, '根陣列＋4095 個值＝4096 個節點');
  assert.equal(HR.shapeFits(new Array(4096).fill(0)), false, '4097 個節點');
  assert.equal(HR.shapeFits({ a: new Array(4094).fill(null) }), true, '根物件＋陣列＋4094 個 null＝4096（屬性名不算節點）');
  assert.equal(HR.shapeFits({ a: new Array(4095).fill(null) }), false);
  // 字串值照伺服器第一段的 16,000（轉送給電腦的指令的上限，WP4.5 D31）；屬性名仍是 4096
  assert.equal(HR.shapeFits({ s: 'x'.repeat(16000) }), true, '字串 16000');
  assert.equal(HR.shapeFits({ s: 'x'.repeat(16001) }), false, '字串 16001');
  assert.equal(HR.shapeFits({ s: '中'.repeat(16000) }), true, '以 UTF-16 單位計，不是位元組');
  assert.equal(HR.shapeFits({ ['k'.repeat(4096)]: 1 }), true, '屬性名 4096');
  assert.equal(HR.shapeFits({ ['k'.repeat(4097)]: 1 }), false, '屬性名 4097（不跟著值放寬）');
  const withUndefined = { a: new Array(4094).fill(null), b: undefined, c: undefined };
  assert.equal(HR.shapeFits(withUndefined), true, 'undefined 欄位不計（JSON 往返後消失）');
  assert.equal(HR.shapeFits('x'.repeat(16000)), true, '根是字串');
  assert.equal(HR.shapeFits('x'.repeat(16001)), false, '根是字串，超過');
  const cyclic = {};
  cyclic.self = cyclic;
  assert.equal(HR.shapeFits(cyclic), false, '循環參照序列化不了');
});

const ZERO_ULID = '0'.repeat(26);

/** 呼叫框（invoke('Cmd', arg)）剛好 total 位元組的 setting 指令：前段是中文（UTF-8 每字 3 位元組），最後補 ASCII */
function argOfSize(total) {
  const parts = Array.from({ length: 15 }, () => '中'.repeat(1000));
  const arg = () => ({ commandId: ZERO_ULID, cmd: { cmd: 'setting', key: 'theme', value: parts } });
  for (;;) {
    parts.push('');
    const left = total - HR.frameBytes('Cmd', arg());
    assert.ok(left >= 0, '夾具算錯');
    if (left <= 4096) {
      parts[parts.length - 1] = 'a'.repeat(left);
      return arg();
    }
    parts[parts.length - 1] = 'a'.repeat(Math.min(4096, left - 4));
  }
}

test('fits_128KB邊界', () => {
  assert.equal(HR.frameBytes('Cmd', { s: '中' }) - HR.frameBytes('Cmd', { s: '' }), 3, '中文以 UTF-8 計 3 位元組');
  const empty = HR.frameBytes('Cmd', {});
  assert.equal(empty, Buffer.byteLength(JSON.stringify({ type: 1, invocationId: '0000000000', target: 'Cmd', arguments: [{}] }), 'utf8') + 1, '整個呼叫框＋0x1E');
  const exact = argOfSize(131072);
  assert.equal(HR.frameBytes('Cmd', exact), 131072);
  assert.equal(HR.fits('Cmd', exact), true, '剛好 131,072 位元組過');
  const over = argOfSize(131073);
  assert.equal(HR.frameBytes('Cmd', over), 131073);
  assert.equal(HR.fits('Cmd', over), false, '131,073 不過');
  // 放寬的理由（D32）：16,000 個要跳脫成六個字元的控制字元就有 96,000 位元組，64 KB 放不下上限內的 send
  const escaped = { commandId: ZERO_ULID, cmd: { cmd: 'send', conversationId: 'c1', text: String.fromCharCode(1).repeat(16000) } };
  assert.ok(HR.frameBytes('Cmd', escaped) > 96000, '跳脫後超過 96,000 位元組（64 KB 放不下）');
  assert.equal(HR.fits('Cmd', escaped), true, '上限內的 send 照送');
  assert.equal(HR.shapeFits(over), true, '不過是因為大小，不是形狀');
  assert.equal(HR.fits('Cmd', { commandId: ZERO_ULID, cmd: { cmd: 'setting', key: 'theme', value: nested(8) } }), false, '形狀不合格也不過（根、cmd 之外再 8 層）');
  assert.equal(HR.fits('Cmd', { commandId: ZERO_ULID, cmd: { cmd: 'setting', key: 'theme', value: nested(6) } }), true);
  assert.equal(HR.fits('ClientState', HR.clientState(true, true, -480)), true);
  assert.equal(HR.MAX_FRAME_BYTES, 131072);
});

test('mergeKey、isAutoCommand', () => {
  assert.equal(HR.mergeKey({ cmd: 'setting', key: 'theme', value: 'light' }), 'setting:theme');
  assert.equal(HR.mergeKey({ cmd: 'accountUpdate', accountId: 'la_1', label: 'x' }), 'accountUpdate:la_1');
  assert.equal(HR.mergeKey({ cmd: 'accountReorder', ids: ['la_1'] }), 'accountReorder');
  assert.equal(HR.mergeKey({ cmd: 'deviceRename', deviceId: 'd_1', name: 'x' }), 'deviceRename:d_1');
  for (const m of [{ cmd: 'ready' }, { cmd: 'visible' }, { cmd: 'setting' }, { cmd: 'accountUpdate' }, { cmd: 'deviceRename', deviceId: 3 }, null, 'setting', {}]) {
    assert.equal(HR.mergeKey(m), null, JSON.stringify(m));
  }
  for (const name of ['visible', 'open', 'close', 'loadOlder', 'newConversation', 'listFiles', 'fileSearch', 'searchConversations']) {
    assert.equal(HR.isAutoCommand({ cmd: name }), true, name);
    assert.equal(HR.isAutoCommand(name), true, name + '（名稱）');
  }
  for (const name of ['ready', 'setting', 'accountUpdate', 'accountReorder', 'deviceRename', 'layout', 'openLink', 'send', 'Visible', '']) {
    assert.equal(HR.isAutoCommand({ cmd: name }), false, name);
  }
  assert.equal(HR.isAutoCommand(null), false);
  assert.equal(HR.isAutoCommand({}), false);
});

test('parseLayout', () => {
  const good = { panes: [{ id: 'p1', tabs: ['c1', 'c2'], active: 'c1' }, { id: 'p2', tabs: [], active: null }], focusedPane: 'p1', sidebarCollapsed: false, view: 'dashboard' };
  assert.deepEqual(plain(HR.parseLayout(JSON.stringify(good))), good, '合格的原樣回來');
  assert.deepEqual(plain(HR.parseLayout(JSON.stringify({ panes: [] }))), { panes: [] });
  const twelvePanes = { panes: Array.from({ length: 12 }, (_, i) => ({ id: 'p' + (i + 1), tabs: [], active: null })) };
  assert.deepEqual(plain(HR.parseLayout(JSON.stringify(twelvePanes))), twelvePanes, '12 個窗格（4 欄 × 3 排）');
  const bad = [
    '{', '', 'null', '[]', '"x"', '42',
    JSON.stringify({ panes: Array.from({ length: 13 }, (_, i) => ({ id: 'p' + (i + 1), tabs: [] })) }),
    JSON.stringify({}),
    JSON.stringify({ panes: 'p1' }),
    JSON.stringify({ panes: [{ id: 1, tabs: [] }] }),
    JSON.stringify({ panes: [{ id: 'p1', tabs: 'c1' }] }),
    JSON.stringify({ panes: [{ id: 'p1', tabs: [1] }] }),
    JSON.stringify({ panes: [{ id: 'p1', tabs: [], active: 3 }] }),
    JSON.stringify({ panes: [null] }),
    JSON.stringify({ panes: [], focusedPane: 1 }),
    JSON.stringify({ panes: [], sidebarCollapsed: 'yes' }),
    JSON.stringify({ panes: [], view: 'settings' }),
  ];
  for (const text of bad) assert.equal(HR.parseLayout(text), null, text);
  assert.equal(HR.parseLayout(null), null);
  assert.equal(HR.parseLayout(undefined), null);
  assert.equal(HR.parseLayout({ panes: [] }), null, '只收字串（localStorage 的內容）');
});

test('localAction', () => {
  const origin = 'https://hub.probe.local';
  for (const href of ['javascript:alert(1)', 'JavaScript:alert(1)', 'file:///C:/Windows/win.ini', 'data:text/html,x', 'mailto:a@example.test', 'vbscript:x']) {
    assert.deepEqual(plain(HR.localAction({ cmd: 'openLink', href }, origin)), { kind: 'reject' }, href);
  }
  assert.deepEqual(plain(HR.localAction({ cmd: 'openLink', href: 'https://hub.probe.local/app/x?y=1' }, origin)),
    { kind: 'openLink', url: 'https://hub.probe.local/app/x?y=1', sameOrigin: true });
  assert.deepEqual(plain(HR.localAction({ cmd: 'openLink', href: '/' }, origin)), { kind: 'openLink', url: 'https://hub.probe.local/', sameOrigin: true }, '相對網址以本站解析');
  assert.deepEqual(plain(HR.localAction({ cmd: 'openLink', href: 'https://example.com/a?b=1' }, origin)), { kind: 'openLink', url: 'https://example.com/a?b=1', sameOrigin: false });
  assert.deepEqual(plain(HR.localAction({ cmd: 'openLink', href: 'http://example.com/' }, origin)), { kind: 'openLink', url: 'http://example.com/', sameOrigin: false });
  assert.equal(HR.localAction({ cmd: 'openLink', href: 'https://hub.probe.local:8443/' }, origin).sameOrigin, false, '埠不同不是同源');
  for (const href of [undefined, '', 42, 'http://[bad']) assert.deepEqual(plain(HR.localAction({ cmd: 'openLink', href }, origin)), { kind: 'reject' }, String(href));
  assert.deepEqual(plain(HR.localAction({ cmd: 'popout', conversationId: 'c 1' }, origin)), { kind: 'popout', url: '/app/?mode=popout&conversation=c%201' });
  assert.deepEqual(plain(HR.localAction({ cmd: 'popout', conversationId: 'a&b=#' }, origin)), { kind: 'popout', url: '/app/?mode=popout&conversation=a%26b%3D%23' });
  assert.deepEqual(plain(HR.localAction({ cmd: 'popout' }, origin)), { kind: 'reject' });
  const layout = { panes: [{ id: 'p1', tabs: [], active: null }], focusedPane: 'p1', sidebarCollapsed: false, view: 'dashboard' };
  assert.deepEqual(plain(HR.localAction({ cmd: 'layout', layout }, origin)), { kind: 'layout', layout });
  assert.deepEqual(plain(HR.localAction({ cmd: 'copyText', text: 'x' }, origin)), { kind: 'copyText' });
  for (const m of [{ cmd: 'setting', key: 'theme', value: 'light' }, { cmd: 'ready' }, { cmd: 'visible' }, null, {}, 'openLink']) {
    assert.equal(HR.localAction(m, origin), null, JSON.stringify(m));
  }
});

test('authDecision', () => {
  assert.equal(HR.authDecision(200), 'connect');
  assert.equal(HR.authDecision(401), 'redirect');
  assert.equal(HR.authDecision(429), 'retryAfter');
  for (const s of [0, 500, 503, 403, 204, undefined]) assert.equal(HR.authDecision(s), 'retry', String(s));
});

test('clientState', () => {
  const s = HR.clientState(true, false, -480);
  assert.deepEqual(Object.keys(s), ['visible', 'focused', 'openConversationIds', 'tzOffsetMinutes', 'features']);
  assert.deepEqual(plain(s), { visible: true, focused: false, openConversationIds: [], tzOffsetMinutes: -480, features: ['patch1'] }, '臺灣是 −480，不反號');
  assert.deepEqual(plain(HR.clientState(false, true, 0)), { visible: false, focused: true, openConversationIds: [], tzOffsetMinutes: 0, features: ['patch1'] });
  assert.equal(HR.clientState(true, true, 300).tzOffsetMinutes, 300);
  assert.equal(HR.clientState('yes', 1, -480).visible, false, '只認布林 true');
  assert.equal(HR.clientState(true, true, NaN).tzOffsetMinutes, 0);
  assert.equal(HR.clientState(true, true, 9999).tzOffsetMinutes, 840, '伺服器只收 ±840');
});

// ───── transport-hub 的狀態機（假的 SignalR、假時鐘；node:vm）─────
// 假的 HubConnection 照 signalr 10.0.11 實測的行為：start() 經 withUrl 的 WebSocket 選項建 socket，onopen 才成功、
// 還沒開啟就關閉才失敗；WebSocket 還在 CONNECTING 時 stop() 結束不了 start()（程式庫的 stop 先等 start 的內部流程）。
// 時鐘是假的（setTimeout、Date.now 照 advance 前進），所以 1、2、4 秒的重送與 30 秒的逾時都不用真的等

const flushAsync = async () => {
  for (let i = 0; i < 30; i++) await new Promise((r) => setImmediate(r));
};

/**
 * o.hangSockets：WebSocket 一直停在 CONNECTING（不開啟）。h.respond(target, arg) 決定 invoke 的結果：
 * 回 {hold:true} 時留著不回（之後以 h.pending 裡的 resolve 回）；預設 Cmd 回 {accepted:true, commandId}
 */
function hubHarness(o = {}) {
  let now = NOW;
  let timerSeq = 0;
  const timers = [];
  const fakeSetTimeout = (fn, ms) => {
    const id = ++timerSeq;
    timers.push({ id, at: now + Math.max(0, Number(ms) || 0), fn });
    return id;
  };
  const fakeClearTimeout = (id) => {
    const i = timers.findIndex((t) => t.id === id);
    if (i >= 0) timers.splice(i, 1);
  };
  class FakeDate extends Date {
    constructor(...args) {
      if (args.length) super(...args);
      else super(now);
    }
    static now() { return now; }
  }
  const sockets = [];
  class FakeWebSocket {
    constructor(url, protocols) {
      this.url = url;
      this.protocols = protocols;
      this.argCount = arguments.length;
      this.readyState = 0;
      this.closeCalls = 0;
      this.onopen = null;
      this.onclose = null;
      sockets.push(this);
    }
    open() {
      if (this.readyState !== 0) return;
      this.readyState = 1;
      if (this.onopen) this.onopen();
    }
    close() {
      this.closeCalls++;
      if (this.readyState >= 2) return;
      this.readyState = 3;
      if (this.onclose) this.onclose({ code: 1006 });
    }
  }
  FakeWebSocket.CONNECTING = 0;
  FakeWebSocket.OPEN = 1;
  FakeWebSocket.CLOSING = 2;
  FakeWebSocket.CLOSED = 3;
  const h = { calls: [], pending: [], msgs: [], warns: [], conns: [], sockets, fetches: 0, respond: null };
  class FakeConnection {
    constructor(options) {
      this.options = options;
      this.handlers = {};
      this.closeHandlers = [];
      this.ws = null;
      this.stopCalls = 0;
      h.conns.push(this);
    }
    on(name, fn) { this.handlers[name] = fn; }
    onclose(fn) { this.closeHandlers.push(fn); }
    start() {
      return new Promise((resolve, reject) => {
        // 沒給 WebSocket 選項時程式庫用全域的 WebSocket
        const Ctor = (this.options && this.options.WebSocket) || win.WebSocket;
        const ws = new Ctor('wss://hub.test/hubs/v1/browser');
        this.ws = ws;
        ws.onopen = () => resolve();
        ws.onclose = () => reject(new Error('WebSocket closed before it opened'));
        if (!o.hangSockets) Promise.resolve().then(() => ws.open());
      });
    }
    stop() {
      this.stopCalls++;
      // 開著的連線：關掉並通知 onclose；還在 CONNECTING：什麼都不做（實測的程式庫行為）
      if (this.ws && this.ws.readyState === 1) {
        this.ws.readyState = 3;
        for (const fn of this.closeHandlers) fn(undefined);
      }
      return Promise.resolve();
    }
    invoke(target, arg) {
      const call = { target, arg: JSON.parse(JSON.stringify(arg)), at: now, conn: this };
      h.calls.push(call);
      const r = h.respond ? h.respond(target, call.arg, call) : undefined;
      if (r && r.hold) {
        return new Promise((resolve, reject) => h.pending.push({ call, resolve, reject }));
      }
      if (r !== undefined) return Promise.resolve(r);
      return Promise.resolve(target === 'Cmd' ? { accepted: true, commandId: arg.commandId } : undefined);
    }
  }
  class Builder {
    withUrl(url, options) { this.options = options; return this; }
    configureLogging() { return this; }
    withServerTimeout() { return this; }
    withKeepAliveInterval() { return this; }
    build() { return new FakeConnection(this.options); }
  }
  const noop = () => {};
  const win = {
    signalR: { HubConnectionBuilder: Builder, HttpTransportType: { WebSockets: 1 }, LogLevel: { None: 6 } },
    WebSocket: FakeWebSocket,
    document: { visibilityState: 'visible', hasFocus: () => true, addEventListener: noop },
    navigator: { onLine: true },
    location: { origin: 'https://hub.test', search: '', replace: noop, assign: noop },
    localStorage: { getItem: () => null, setItem: noop },
    addEventListener: noop,
    open: noop,
    crypto: webcrypto,
    reportError: (e) => { throw e; },
  };
  const sandbox = Object.assign(win, {
    console: { warn: (m) => h.warns.push(String(m)), log: noop, error: noop },
    setTimeout: fakeSetTimeout, clearTimeout: fakeClearTimeout, Date: FakeDate,
    TextEncoder, URL, URLSearchParams, AbortController,
    fetch: async () => { h.fetches++; return { status: 200, headers: { get: () => null } }; },
  });
  sandbox.window = sandbox;
  sandbox.self = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(readFileSync(hubFile, 'utf8'), sandbox, { filename: 'transport-hub.js' });
  h.T = sandbox.myriagentHubTransport;
  h.T.onMessage((m) => h.msgs.push(plain(m)));
  /** 時鐘前進 ms：照時間順序執行到期的計時器，每一步之間讓 Promise 跑完 */
  h.advance = async (ms) => {
    const end = now + ms;
    for (;;) {
      await flushAsync();
      timers.sort((a, b) => a.at - b.at || a.id - b.id);
      const t = timers[0];
      if (!t || t.at > end) break;
      timers.shift();
      now = t.at;
      t.fn();
    }
    now = end;
    await flushAsync();
  };
  h.now = () => now;
  /** 伺服器推一則訊息給目前的連線 */
  h.push = (m) => h.conns.at(-1).handlers.Msg(m);
  h.cmds = (name) => h.calls.filter((c) => c.target === 'Cmd' && (!name || c.arg.cmd.cmd === name)).map((c) => c.arg);
  h.toasts = () => h.msgs.filter((m) => m.type === 'toast').map((m) => m.text);
  h.settingsMsgs = () => h.msgs.filter((m) => m.type === 'settings').map((m) => m.settings);
  return h;
}

const INIT_SETTINGS = { theme: 'dark', fontSize: 14, enterToSend: true, notifications: true };

/** 連上並收到 init（ready 被接受之後由測試推 init） */
async function liveHub(o) {
  const h = hubHarness(o);
  h.T.post({ cmd: 'ready', mode: 'main' });
  await h.advance(0);
  h.push({ type: 'init', mode: 'main', layout: null, settings: Object.assign({}, INIT_SETTINGS), accounts: [] });
  await h.advance(0);
  assert.equal(h.T.status().state, 'online');
  assert.equal(h.T.status().hubInit, true);
  return h;
}

const RATE_LIMITED = (arg) => ({ accepted: false, commandId: arg.commandId, error: 'rate_limited' });

test('transportHub_accountUpdate改名被限流後1秒內改色_最後送出的帶名稱與顏色', async () => {
  const h = await liveHub();
  let first = true;
  h.respond = (target, arg) => {
    if (target === 'Cmd' && arg.cmd.cmd === 'accountUpdate' && first) {
      first = false;
      return RATE_LIMITED(arg);
    }
    return undefined;
  };
  h.T.post({ cmd: 'accountUpdate', accountId: 'la_X', label: '新名' });
  await h.advance(300);
  h.T.post({ cmd: 'accountUpdate', accountId: 'la_X', color: '#112233' });
  await h.advance(8000);
  const sent = h.cmds('accountUpdate').map((a) => a.cmd);
  assert.deepEqual(sent, [
    { cmd: 'accountUpdate', accountId: 'la_X', label: '新名' },
    { cmd: 'accountUpdate', accountId: 'la_X', color: '#112233', label: '新名' },
  ], '等待重送的改名併進改色；舊的重送取消，不再單獨送');
  assert.deepEqual(h.toasts(), []);
});

test('transportHub_accountUpdate改名結果晚到前改色_改名被限流也不遺失', async () => {
  const h = await liveHub();
  h.respond = (target, arg) => (target === 'Cmd' && arg.cmd.cmd === 'accountUpdate' && arg.cmd.label === '新名' && !('color' in arg.cmd) ? { hold: true } : undefined);
  h.T.post({ cmd: 'accountUpdate', accountId: 'la_X', label: '新名' });
  await h.advance(0);
  h.T.post({ cmd: 'accountUpdate', accountId: 'la_X', color: '#112233' });
  await h.advance(0);
  // 改名的 rate_limited 現在才到：它不是最新的（不重送），但它的欄位已經在改色那一筆裡
  const rename = h.pending.shift();
  rename.resolve(RATE_LIMITED(rename.call.arg));
  await h.advance(8000);
  const sent = h.cmds('accountUpdate').map((a) => a.cmd);
  assert.deepEqual(sent, [
    { cmd: 'accountUpdate', accountId: 'la_X', label: '新名' },
    { cmd: 'accountUpdate', accountId: 'la_X', color: '#112233', label: '新名' },
  ]);
  // 之後的改色只帶顏色（前一筆已經確定送達）
  h.T.post({ cmd: 'accountUpdate', accountId: 'la_X', color: '#445566' });
  await h.advance(0);
  assert.deepEqual(h.cmds('accountUpdate').at(-1).cmd, { cmd: 'accountUpdate', accountId: 'la_X', color: '#445566' });
  // 別的帳戶、新值優先
  h.respond = (target, arg) => (target === 'Cmd' && arg.cmd.cmd === 'accountUpdate' && arg.cmd.accountId === 'la_Y' && arg.cmd.label === '甲' ? RATE_LIMITED(arg) : undefined);
  h.T.post({ cmd: 'accountUpdate', accountId: 'la_Y', label: '甲' });
  await h.advance(100);
  h.T.post({ cmd: 'accountUpdate', accountId: 'la_Y', label: '乙' });
  await h.advance(8000);
  assert.deepEqual(h.cmds('accountUpdate').filter((a) => a.cmd.accountId === 'la_Y').map((a) => a.cmd.label), ['甲', '乙'], '新名稱蓋過等待重送的舊名稱');
});

test('transportHub_accountUpdate被拒絕_之後的指令不帶它的欄位', async () => {
  const h = await liveHub();
  h.respond = (target, arg) => (target === 'Cmd' && arg.cmd.label === 'x'.repeat(3) ? { accepted: false, commandId: arg.commandId, error: 'invalid' } : undefined);
  h.T.post({ cmd: 'accountUpdate', accountId: 'la_X', label: 'xxx' });
  await h.advance(100);
  h.T.post({ cmd: 'accountUpdate', accountId: 'la_X', color: '#112233' });
  await h.advance(100);
  assert.deepEqual(h.cmds('accountUpdate').at(-1).cmd, { cmd: 'accountUpdate', accountId: 'la_X', color: '#112233' });
});

test('transportHub_ready被限流_1秒後在同一條連線以同一個commandId重送', async () => {
  const h = hubHarness();
  let first = true;
  h.respond = (target, arg) => {
    if (target === 'Cmd' && arg.cmd.cmd === 'ready' && first) {
      first = false;
      return RATE_LIMITED(arg);
    }
    return undefined;
  };
  h.T.post({ cmd: 'ready', mode: 'main' });
  await h.advance(0);
  assert.equal(h.cmds('ready').length, 1);
  await h.advance(999);
  assert.equal(h.cmds('ready').length, 1, '1 秒之前不重送');
  await h.advance(1);
  const readies = h.calls.filter((c) => c.target === 'Cmd' && c.arg.cmd.cmd === 'ready');
  assert.equal(readies.length, 2, 'init 之前也重送 ready');
  assert.equal(readies[1].arg.commandId, readies[0].arg.commandId, '沿用原本的 commandId');
  assert.equal(readies[1].conn, readies[0].conn, '同一條連線');
  assert.equal(h.T.status().generation, 1, '沒有換連線');
  assert.equal(h.fetches, 1);
  h.push({ type: 'init', mode: 'main', layout: null, settings: INIT_SETTINGS, accounts: [] });
  await h.advance(0);
  assert.equal(h.T.status().hubInit, true);
  await h.advance(30000);
  assert.equal(h.T.status().generation, 1, 'init 看門狗沒有觸發');
});

test('transportHub_setting被拒絕_畫面改回伺服器的值', async () => {
  const h = await liveHub();
  h.respond = (target, arg) => (target === 'Cmd' && arg.cmd.cmd === 'setting' ? { accepted: false, commandId: arg.commandId, error: 'unavailable' } : undefined);
  h.T.post({ cmd: 'setting', key: 'theme', value: 'light' });
  await h.advance(0);
  assert.deepEqual(h.settingsMsgs(), [{ theme: 'dark' }], '只帶被拒的那個鍵、值是 init 的');
  // 伺服器推了新的設定：之後以它為準
  h.push({ type: 'settings', settings: Object.assign({}, INIT_SETTINGS, { theme: 'system' }) });
  h.T.post({ cmd: 'setting', key: 'theme', value: 'light' });
  await h.advance(0);
  assert.deepEqual(h.settingsMsgs().at(-1), { theme: 'system' });
  // 被接受的不改回
  h.respond = null;
  const n = h.settingsMsgs().length;
  h.T.post({ cmd: 'setting', key: 'fontSize', value: 16 });
  await h.advance(0);
  assert.equal(h.settingsMsgs().length, n, '被接受的不送 settings');
});

test('transportHub_setting同一個鍵有更新的在路上_舊的被拒不改回', async () => {
  const h = await liveHub();
  h.respond = (target) => (target === 'Cmd' ? { hold: true } : undefined);
  h.T.post({ cmd: 'setting', key: 'theme', value: 'light' });
  h.T.post({ cmd: 'setting', key: 'theme', value: 'system' });
  await h.advance(0);
  const [light, system] = h.pending.splice(0, 2);
  light.resolve({ accepted: false, commandId: light.call.arg.commandId, error: 'unavailable' });
  await h.advance(0);
  assert.deepEqual(h.settingsMsgs(), [], '較新的 system 還在路上');
  system.resolve({ accepted: true, commandId: system.call.arg.commandId });
  await h.advance(0);
  assert.deepEqual(h.settingsMsgs(), []);
});

test('transportHub_setting限流放棄或太大_畫面改回', async () => {
  const h = await liveHub();
  h.respond = (target, arg) => (target === 'Cmd' && arg.cmd.cmd === 'setting' ? RATE_LIMITED(arg) : undefined);
  h.T.post({ cmd: 'setting', key: 'theme', value: 'light' });
  await h.advance(0);
  await h.advance(6999);
  assert.deepEqual(h.settingsMsgs(), [], '還在重送（1、2、4 秒）');
  await h.advance(1);
  assert.equal(h.cmds('setting').length, 4);
  assert.deepEqual(h.toasts(), ['伺服器忙碌，這個變更沒有送出']);
  assert.deepEqual(h.settingsMsgs(), [{ theme: 'dark' }]);
  h.respond = null;
  h.T.post({ cmd: 'setting', key: 'fontSize', value: 'x'.repeat(70000) });
  await h.advance(0);
  assert.deepEqual(h.settingsMsgs().at(-1), { fontSize: 14 }, '太大沒有送出');
  assert.equal(h.cmds('setting').length, 4);
});

test('transportHub_WebSocket開啟握手卡住_30秒放棄並照退避重試', async () => {
  const h = hubHarness({ hangSockets: true });
  h.T.post({ cmd: 'ready', mode: 'main' });
  await h.advance(0);
  assert.equal(h.sockets.length, 1);
  assert.equal(h.sockets[0].argCount, 1, '參數原樣轉傳（程式庫只傳 url）');
  assert.equal(h.sockets[0].url, 'wss://hub.test/hubs/v1/browser');
  await h.advance(29999);
  assert.equal(h.T.status().state, null, '30 秒之前還在第一次嘗試');
  assert.equal(h.sockets[0].closeCalls, 0);
  await h.advance(1);
  assert.equal(h.sockets[0].closeCalls, 1, '還在 CONNECTING 的 socket 直接關掉');
  const st = h.T.status();
  assert.equal(st.state, 'reconnecting', '離開「連線中」');
  assert.equal(st.attempt, 1);
  assert.equal(h.fetches, 2, '第一次失敗後不等（退避 0 秒），立刻問 auth/me 再連');
  assert.equal(h.sockets.length, 2);
  // 第二次也卡住：30 秒後放棄，這次等 1 秒那一階
  await h.advance(30000);
  assert.equal(h.sockets[1].closeCalls, 1);
  assert.equal(h.T.status().attempt, 2);
  assert.equal(h.fetches, 2, '退避期間不連');
  // 退避期間「立即重試」立刻問 auth/me（卡在 connecting 時這個按鈕沒有作用）
  h.T.retryNow();
  await h.advance(0);
  assert.equal(h.fetches, 3);
  assert.equal(h.msgs.filter((m) => m.type === 'connection').length, 2, '晚到的 reject 不會再處理一次失敗');
});

// ───── remoteOnly 的說明文字（WP3.5 合併前審查）─────

test('remoteOnlyElsewhere_只有這台的absent格是false', () => {
  const self = cell('d_a', { self: true, status: 'absent' });
  assert.equal(R.remoteOnlyElsewhere({ id: 'la_1', remoteOnly: true, devices: [self] }), false, '只有這台、而且這台沒有');
  assert.equal(R.remoteOnlyElsewhere({ id: 'la_1', remoteOnly: true, devices: [self, cell('d_b', { status: 'absent' })] }), false, '其他電腦也沒有');
  assert.equal(R.remoteOnlyElsewhere({ id: 'la_1', remoteOnly: true, devices: [self, cell('d_b', { status: 'notLoggedIn' })] }), true, '另一台有這個帳戶（未登入）');
  assert.equal(R.remoteOnlyElsewhere({ id: 'la_1', remoteOnly: true, devices: [self, cell('d_b', { status: 'ready' })] }), true);
  assert.equal(R.remoteOnlyElsewhere({ id: 'la_1', remoteOnly: true, devices: [cell('d_a', { status: 'ready', self: true })] }), false, '這台自己的格子不算「其他電腦」');
  assert.equal(R.remoteOnlyElsewhere({ id: 'la_1', remoteOnly: true, devices: null }), false);
  assert.equal(R.remoteOnlyElsewhere({ id: 'la_1', remoteOnly: true }), false);
  assert.equal(R.remoteOnlyElsewhere({ id: 'la_1', remoteOnly: true, devices: [null, 'x', cell('d_b', { status: 'ready' })] }), true, '壞的格子跳過');
  assert.equal(R.remoteOnlyElsewhere(null), false);
});

// ───── 頂部列的版號（versionrules.js）─────

const APP = { version: '0.2.470', build: 'ab12cd3', release: '0.2.470-ab12cd3' };

test('desktopVersionBadge_版號與完整版本', () => {
  const v = V.desktopVersionBadge(APP, { phase: 'idle', detail: '已是最新版', lastCheckAt: NOW });
  assert.equal(v.text, '0.2.470');
  assert.equal(v.prefix, '');
  assert.equal(v.tone, 'plain');
  assert.equal(v.title, 'Clode Myriagent 0.2.470（ab12cd3）\n已是最新版\n點一下開啟設定的「Clode Myriagent」');
  assert.equal(v.label, '版本 0.2.470');
});

test('desktopVersionBadge_開發版與沒有版本', () => {
  const dev = V.desktopVersionBadge({ version: '0.2.0', build: 'dev', release: '0.2.0-dev' }, null);
  assert.equal(dev.title, 'Clode Myriagent 0.2.0（開發版）\n點一下開啟設定的「Clode Myriagent」', '宿主還沒送更新狀態：沒有狀態那一行');
  assert.match(V.desktopVersionBadge({ version: '0.2.0' }, null).title, /（開發版）/, '沒有 build 也是開發版');
  assert.equal(V.desktopVersionBadge({}, null), null);
  assert.equal(V.desktopVersionBadge(null, undefined), null);
  assert.equal(V.desktopVersionBadge({ version: '' }, null), null, '空字串不是版本');
  assert.equal(V.desktopVersionBadge({ version: 470 }, null), null, '數字不是版本');
  const fromUpdate = V.desktopVersionBadge({}, { version: '0.2.471', build: 'ef56789', phase: 'idle' });
  assert.equal(fromUpdate.text, '0.2.471', 'init.app 沒有時用 AppUpdateView 的版本');
  assert.match(fromUpdate.title, /（ef56789）/);
  const noMix = V.desktopVersionBadge({ version: '0.2.470' }, { version: '0.2.471', build: 'ef56789', phase: 'idle' });
  assert.match(noMix.title, /^Clode Myriagent 0\.2\.470（開發版）\n/, '版本與 build 取同一個來源，不拿 AppUpdateView 的 build 補');
});

test('desktopVersionBadge_有新版時換色調', () => {
  const avail = V.desktopVersionBadge(APP, { phase: 'idle', available: '0.2.480' });
  assert.equal(avail.tone, 'update');
  assert.equal(avail.label, '版本 0.2.470，有新版 0.2.480');
  assert.match(avail.title, /\n有新版 0\.2\.480\n/);
  const staged = V.desktopVersionBadge(APP, { phase: 'staged', staged: '0.2.481', available: '0.2.482' });
  assert.equal(staged.tone, 'update');
  assert.match(staged.title, /新版 0\.2\.481 已下載，重新啟動後套用/, '以已備妥的版本為準');
  const same = V.desktopVersionBadge(APP, { phase: 'idle', available: '0.2.470', detail: '已是最新版' });
  assert.equal(same.tone, 'plain', '和目前相同的版本不算新版');
  assert.match(same.title, /\n已是最新版\n/);
});

test('desktopUpdateStatus_各階段', () => {
  const s = (u) => V.desktopUpdateStatus('0.2.470', u);
  assert.deepEqual(s(null), { text: '', newer: null });
  assert.deepEqual(s({ phase: 'idle' }), { text: '還沒檢查更新', newer: null }, '還沒檢查過不說已是最新版');
  assert.deepEqual(s({ phase: 'idle', lastCheckAt: NOW }), { text: '已是最新版', newer: null });
  assert.deepEqual(s({ phase: 'idle', detail: '沒有可用的發佈', lastCheckAt: NOW }), { text: '沒有可用的發佈', newer: null });
  assert.deepEqual(s({ phase: 'checking' }), { text: '正在檢查更新…', newer: null });
  assert.deepEqual(s({ phase: 'downloading', available: '0.2.480' }), { text: '有新版 0.2.480，下載中', newer: '0.2.480' });
  assert.deepEqual(s({ phase: 'staged', staged: '0.2.480' }), { text: '新版 0.2.480 已下載，重新啟動後套用', newer: '0.2.480' });
  assert.deepEqual(s({ phase: 'draining', staged: '0.2.480' }), { text: '正在套用新版 0.2.480', newer: '0.2.480' });
  assert.deepEqual(s({ phase: 'restarting', staged: '0.2.480' }), { text: '正在套用新版 0.2.480', newer: '0.2.480' });
  assert.deepEqual(s({ phase: 'installing' }), { text: '正在安裝到這台電腦', newer: null });
  assert.deepEqual(s({ phase: 'blocked', available: '0.2.480', detail: '磁碟空間不足' }), { text: '有新版 0.2.480，無法安裝：磁碟空間不足', newer: '0.2.480' });
  assert.deepEqual(s({ phase: 'failed', detail: '連不上伺服器' }), { text: '更新失敗：連不上伺服器', newer: null });
  assert.deepEqual(s({ phase: 'disabled', detail: '不是從安裝位置執行' }), { text: '不自動更新：不是從安裝位置執行', newer: null }, '開發版');
  assert.deepEqual(s({ phase: 'disabled', staged: '0.2.480' }), { text: '有新版 0.2.480，不自動更新', newer: '0.2.480' }, '關掉自動更新時已備妥的版本照樣帶');
  assert.deepEqual(s({ phase: 'weird', available: '0.2.480' }), { text: '有新版 0.2.480', newer: '0.2.480' }, '不認得的階段照 idle');
});

test('hubVersionBadge_同一個版本', () => {
  const v = V.hubVersionBadge('ab12cd3', { version: '0.2.0', build: 'ab12cd3' });
  assert.equal(v.prefix, '網頁版');
  assert.equal(v.text, 'ab12cd3');
  assert.equal(v.tone, 'plain');
  assert.equal(v.label, '網頁版 build ab12cd3');
  assert.equal(v.title, 'Myriagent Hub 0.2.0（ab12cd3）\n這個網頁與伺服器是同一個版本\n點一下開啟「版本與更新」：目前提供的桌面版與各電腦的版本');
  assert.match(V.hubVersionBadge('ab12cd3', { build: 'ab12cd3' }).title, /^Myriagent Hub build ab12cd3\n/, '沒有伺服器版本號');
});

test('hubVersionBadge_伺服器已換版', () => {
  const v = V.hubVersionBadge('ab12cd3', { version: '0.2.0', build: 'ef56789' });
  assert.equal(v.text, 'ab12cd3', '顯示的是這個網頁的版本');
  assert.equal(v.tone, 'warn');
  assert.equal(v.label, '網頁版 build ab12cd3，伺服器已換版');
  assert.match(v.title, /^這個網頁是伺服器 build ab12cd3 提供的\n伺服器已換成 build ef56789，重新載入以取得對應的網頁\n/);
  const gone = V.hubVersionBadge('ab12cd3', { version: '0.2.0' });
  assert.equal(gone.tone, 'warn', '從有變成沒有（回滾到沒有這個欄位的舊伺服器）也算換版');
  assert.match(gone.title, /\n伺服器已換版，重新載入以取得對應的網頁\n/);
  const appeared = V.hubVersionBadge(null, { version: '0.2.0', build: 'ef56789' });
  assert.equal(appeared.text, 'ef56789', '網頁那時沒有 build：顯示目前伺服器的');
  assert.equal(appeared.tone, 'warn');
});

test('hubVersionBadge_沒有build不顯示', () => {
  assert.equal(V.hubVersionBadge(null, { version: '0.2.0' }), null);
  assert.equal(V.hubVersionBadge(undefined, null), null);
  assert.equal(V.hubVersionBadge('', { build: '' }), null, '空字串不是 build');
  assert.equal(V.hubVersionBadge(null, { build: 7 }), null, '數字不是 build');
});

// 版號前面的日期時間（commitAt）：以當地時間組時刻，測試在哪個時區跑都一樣
const AT = new Date(2026, 9, 23, 14, 5, 12).getTime();
const AT2 = new Date(2026, 9, 24, 9, 3).getTime();
const NOW_2026 = new Date(2026, 11, 31, 23, 59).getTime();

test('versionTime_今年省略年份_完整的一律有年份', () => {
  assert.deepEqual(V.versionTime(AT, NOW_2026), { short: '10/23 14:05', full: '2026/10/23 14:05' });
  assert.deepEqual(V.versionTime(new Date(2026, 0, 5, 9, 3).getTime(), NOW_2026), { short: '01/05 09:03', full: '2026/01/05 09:03' }, '補零');
  assert.deepEqual(V.versionTime(AT, new Date(2027, 0, 1).getTime()), { short: '2026/10/23 14:05', full: '2026/10/23 14:05' }, '不是今年加年份');
  for (const bad of [null, undefined, 0, -1, NaN, Infinity, '1792732512000', {}]) assert.equal(V.versionTime(bad, NOW_2026), null, String(bad));
  assert.equal(V.normCommitAt(AT), AT);
  assert.equal(V.normCommitAt('x'), null);
});

test('versionWithTime_設定頁的目前版本', () => {
  assert.equal(V.versionWithTime('0.2.470（ab12cd3）', AT), '2026/10/23 14:05 · 0.2.470（ab12cd3）');
  assert.equal(V.versionWithTime('0.2.470（ab12cd3）', null), '0.2.470（ab12cd3）', '沒有時間（舊宿主、建置時沒有 git）只顯示版本');
  assert.equal(V.versionWithTime('', AT), '', '沒有版本就不顯示時間');
  assert.equal(V.versionWithTime(undefined, AT), '');
});

test('desktopVersionBadge_版號前面是日期時間', () => {
  const v = V.desktopVersionBadge(Object.assign({}, APP, { commitAt: AT }), { phase: 'idle', detail: '已是最新版', lastCheckAt: NOW }, NOW_2026);
  assert.equal(v.time, '10/23 14:05');
  assert.equal(v.text, '0.2.470');
  assert.equal(v.title, 'Clode Myriagent 0.2.470（ab12cd3）\n更新於 2026/10/23 14:05\n已是最新版\n點一下開啟設定的「Clode Myriagent」');
  assert.equal(v.label, '版本 0.2.470（2026/10/23 14:05）');
  const newer = V.desktopVersionBadge(Object.assign({}, APP, { commitAt: AT }), { phase: 'idle', available: '0.2.480' }, NOW_2026);
  assert.equal(newer.label, '版本 0.2.470（2026/10/23 14:05），有新版 0.2.480');
  assert.equal(V.desktopVersionBadge(APP, null, NOW_2026).time, '', '沒有 commitAt 不顯示時間');
  const fromUpdate = V.desktopVersionBadge({}, { version: '0.2.471', build: 'ef56789', commitAt: AT2, phase: 'idle' }, NOW_2026);
  assert.equal(fromUpdate.time, '10/24 09:03', 'init.app 沒有版本時整組用 AppUpdateView 的');
  const noMix = V.desktopVersionBadge({ version: '0.2.470' }, { version: '0.2.471', commitAt: AT2, phase: 'idle' }, NOW_2026);
  assert.equal(noMix.time, '', '不拿 AppUpdateView 的時間補 init.app');
});

test('hubVersionBadge_版號前面是這個網頁的日期時間', () => {
  const same = V.hubVersionBadge('ab12cd3', { version: '0.2.0', build: 'ab12cd3', commitAt: AT }, AT, NOW_2026);
  assert.equal(same.time, '10/23 14:05');
  assert.equal(same.title, 'Myriagent Hub 0.2.0（ab12cd3）\n更新於 2026/10/23 14:05\n這個網頁與伺服器是同一個版本\n點一下開啟「版本與更新」：目前提供的桌面版與各電腦的版本');
  assert.equal(same.label, '網頁版 build ab12cd3（2026/10/23 14:05）');
  const changed = V.hubVersionBadge('ab12cd3', { version: '0.2.0', build: 'ef56789', commitAt: AT2 }, AT, NOW_2026);
  assert.equal(changed.time, '10/23 14:05', '顯示的是這個網頁的時間，不是伺服器新的');
  assert.match(changed.title, /^這個網頁是伺服器 build ab12cd3（2026\/10\/23 14:05） 提供的\n伺服器已換成 build ef56789（2026\/10\/24 09:03），重新載入以取得對應的網頁\n/);
  assert.equal(changed.label, '網頁版 build ab12cd3（2026/10/23 14:05），伺服器已換版');
  assert.equal(V.hubVersionBadge(null, { version: '0.2.0', build: 'ef56789', commitAt: AT2 }, null, NOW_2026).time, '10/24 09:03', '網頁那時沒有 build：時間也用目前伺服器的');
  assert.equal(V.hubVersionBadge('ab12cd3', { version: '0.2.0', build: 'ab12cd3', commitAt: AT }, null, NOW_2026).time, '', '第一則 init 沒有時間（舊伺服器）就不顯示');
});

// ───── 側欄的 token 計數（tokenrules.js）─────
test('tokenrules：總數、格式、要不要顯示', () => {
  assert.equal(T.tokenTotal({ input: 1_226_418, cached: 1_018_772, output: 57_913 }), 1_284_331, '輸入（含快取）＋輸出');
  assert.equal(T.tokenTotal(null), 0);
  assert.equal(T.tokenTotal({ input: -5, output: 'x' }), 0, '不是正數當 0');
  assert.equal(T.fmtTokens(1_284_331), '1,284,331');
  assert.equal(T.fmtTokens(999), '999');
  assert.equal(T.fmtTokens(0), '0');
  assert.equal(T.fmtTokens(12.9), '12', '只顯示整數，不四捨五入成假的數字');
  assert.equal(T.fmtDelta, undefined, '不顯示增量（2026-10-23 使用者要求拿掉）');
  assert.deepEqual(T.tokenSplit({ input: 1_226_418, cached: 1_018_772, output: 57_913 }), { input: 1_226_418, output: 57_913 }, 'in 含快取');
  assert.deepEqual(T.tokenSplit(null), { input: 0, output: 0 });
  assert.deepEqual(T.tokenSplit({ input: -5, output: 'x' }), { input: 0, output: 0 }, '不是正數當 0');
});

test('tokenrules：帳戶卡的精簡格式（in、out 並排）', () => {
  const cases = [
    [0, '0'], [880, '880'], [1_623, '1,623'], [9_999, '9,999'],
    [10_000, '10.00K'], [35_073, '35.07K'], [104_126, '104.1K'], [999_999, '999.9K'],
    [1_000_000, '1.000M'], [1_264_661, '1.264M'], [8_911_767, '8.911M'], [366_048_770, '366.0M'], [999_999_999, '999.9M'],
    [1_000_000_000, '1.000B'], [12_345_678_901, '12.34B'], [1_500_000_000_000, '1.500T'], [1_500_000_000_000_000, '1500T'],
    [-5, '0'], ['x', '0'], [12_999.9, '12.99K'],
  ];
  for (const [n, s] of cases) assert.equal(T.fmtShort(n), s, String(n));
  // 無條件捨去：顯示的值不會比真實值大（1,999,999 不是 2.000M）
  assert.equal(T.fmtShort(1_999_999), '1.999M');
  assert.equal(T.fmtShort(99_995), '99.99K');
  // 最多 6 個字（帳戶卡的寬度）
  for (let n = 1; n < 1e13; n = Math.floor(n * 1.37) + 1) assert.ok(T.fmtShort(n).length <= 6, `${n} → ${T.fmtShort(n)}`);

  const tokens = { day: '2026-10-22', accounts: { a1: { today: { input: 1, cached: 0, output: 1 }, month: { input: 1, cached: 0, output: 1 } }, a4: { today: {}, month: { input: 0, output: 0 } } } };
  assert.deepEqual(T.tokensOf(tokens, 'a1').today, { input: 1, cached: 0, output: 1 });
  assert.equal(T.tokensOf(tokens, 'zz'), null);
  assert.equal(T.tokensOf(null, 'a1'), null);
  assert.equal(T.tokensOf({ accounts: { toString: 1 } }, 'hasOwnProperty'), null, '原型上的名稱不算');
  assert.equal(T.showTokens({ id: 'a1', status: 'notLoggedIn' }, tokens), false, '沒登入時那一行照舊顯示狀態（即使近 30 天用過）');
  assert.equal(T.showTokens({ id: 'a1', status: 'error' }, tokens), false);
  assert.equal(T.showTokens({ id: 'a1', status: 'ready' }, tokens), true);
  assert.equal(T.showTokens({ id: 'a9', status: 'ready' }, tokens), true, '已登入的新帳戶顯示 0');
  assert.equal(T.showTokens({ id: 'a1', status: 'ready', remoteOnly: true }, tokens), false, '這台沒有的帳戶不顯示');
  assert.equal(T.showTokens({ id: 'a1', status: 'ready' }, null), false, '舊宿主沒有 tokens');

  // 瀏覽器版（scope 'all'）：伺服器只列有電腦回報過的帳戶，沒列的照舊顯示方案名稱、不顯示 0
  const merged = { day: '2026-10-23', scope: 'all', accounts: { la_1: { today: { input: 5, cached: 0, output: 1 } }, la_2: { today: {}, month: { input: 9, output: 1 } } } };
  assert.equal(T.showTokens({ id: 'la_1', status: 'ready' }, merged), true);
  assert.equal(T.showTokens({ id: 'la_2', status: 'ready' }, merged), true, '近 30 天有回報、今天 0 也顯示');
  assert.equal(T.showTokens({ id: 'la_9', status: 'ready' }, merged), false, '沒有電腦回報過（例如還是舊版）');
  assert.equal(T.showTokens({ id: 'la_1', status: 'notLoggedIn' }, merged), false, '沒有電腦登入時照舊顯示狀態');
});

test('tokenrules：解碼從第一個變動的位數開始，只顯示亂碼或真實的新值', () => {
  assert.equal(T.decodeStart('1,284,331', '1,331,204'), 2);
  assert.equal(T.decodeStart('1,284,331', '1,284,336'), 8);
  assert.equal(T.decodeStart('999,999', '1,000,042'), 0, '位數變了從頭');
  assert.equal(T.decodeStart('', '0'), 0);
  assert.equal(T.decodeStart('42', '42'), -1);

  const text = '1,331,204';
  const start = T.decodeStart('1,284,331', text);
  const fixed = () => 0; // 亂碼固定取第一個字
  const at0 = T.decodeCells(text, start, 0, fixed);
  assert.equal(at0.map((c) => c.ch).join(''), '1,▖▖▖,▖▖▖', '沒變的位數與逗號照舊，其他是亂碼');
  assert.ok(!/[0-9]/.test(T.GLYPHS), '亂碼不用數字，才不會被看成某個值');
  assert.deepEqual(at0.map((c) => c.state), ['done', 'done', 'scramble', 'scramble', 'scramble', 'done', 'scramble', 'scramble', 'scramble']);
  const firstLock = T.lockAt(start, start);
  assert.equal(firstLock, T.DECODE_BASE_MS);
  const mid = T.decodeCells(text, start, firstLock + 1, fixed);
  assert.equal(mid.map((c) => c.ch).join(''), '1,3▖▖,▖▖▖', '由左到右逐位鎖定');
  assert.equal(mid[2].state, 'lock', '剛鎖定的那一格閃一下');
  const end = T.decodeCells(text, start, T.decodeDuration(text, start) + 1000, fixed);
  assert.equal(end.map((c) => c.ch).join(''), text, '結束時一定是真實的新值');
  assert.ok(end.every((c) => c.state === 'done'));
  // 解碼途中每一格只會是新值的那個字或亂碼（片假名、數字）
  for (let t = 0; t <= T.decodeDuration(text, start) + 300; t += 17) {
    for (const [i, c] of T.decodeCells(text, start, t).entries()) {
      assert.ok(c.ch === text[i] || (c.state === 'scramble' && T.GLYPHS.includes(c.ch) && !/[0-9]/.test(c.ch)), `t=${t} i=${i} ${c.ch}`);
    }
  }
  assert.equal(T.decodeDuration(text, -1), 0);
  assert.equal(T.decodeCells(text, -1, 0).map((c) => c.ch).join(''), text, '不用動就是新值');

  // 精簡格式：小數點與單位不變成亂碼，只有數字會
  const short = T.decodeCells('1.273M', T.decodeStart('1.226M', '1.273M'), 0, fixed);
  assert.equal(T.decodeStart('1.226M', '1.273M'), 3);
  assert.equal(short.map((c) => c.ch).join(''), '1.2▖▖M');
  assert.deepEqual(short.map((c) => c.state), ['done', 'done', 'done', 'scramble', 'scramble', 'done']);
  assert.equal(T.decodeCells('1.000M', 0, 0, fixed).map((c) => c.ch).join(''), '▖.▖▖▖M', '單位換了（999.9K → 1.000M）從頭，單位直接是新的');
});

test('tokenrules：滑過的說明列出今天 in、out 的精確值與近 7、30 天', () => {
  const t = { today: { input: 1_226_418, cached: 1_018_772, output: 57_913 }, week: { input: 9_482_113, output: 402_771 }, month: { input: 31_870_552, output: 1_388_410 } };
  const s = T.tokensTitle(t);
  assert.ok(s.startsWith('token 合計：1,284,331\n'), '第一行是今天 in＋out 的合計');
  assert.doesNotMatch(s, /今日/, '不寫「今日」兩字（2026-10-23 使用者要求）');
  assert.match(s, /in（輸入）：1,226,418（其中快取讀取 1,018,772）/);
  assert.match(s, /out（輸出）：57,913/);
  assert.match(s, /近 7 天：in 9,482,113・out 402,771/);
  assert.match(s, /近 30 天：in 31,870,552・out 1,388,410/);
  assert.match(s, /只算這台電腦/);
  assert.doesNotMatch(s, /子代理|Grok/, '沒給代理種類時沒有晚到的說明');
  assert.match(T.tokensTitle(null), /token 合計：0/);
  assert.match(T.tokensTitle(null), /近 7 天：in 0・out 0/);
  // 用量晚到的代理：Grok 整輪在一輪結束時才計入（Claude 的子代理 2026-10-25 起從逐字稿即時記）
  assert.doesNotMatch(T.tokensTitle(t, undefined, 'Claude'), /子代理|一輪結束/, 'Claude 的子代理即時記');
  assert.match(T.tokensTitle(t, undefined, 'Grok'), /Grok 在一輪結束時才回報/);
  assert.doesNotMatch(T.tokensTitle(t, undefined, 'Codex'), /子代理|一輪結束/, 'Codex 每次 API 呼叫都回報（含子代理）');
  const all = T.tokensTitle(t, 'all');
  assert.match(all, /token 合計：1,284,331/);
  assert.match(all, /所有電腦上 Clode Myriagent 的對話合計/, '瀏覽器版是各電腦的合計');
  assert.doesNotMatch(all, /只算這台電腦/);
  assert.doesNotMatch(all, /其中這台電腦/, '瀏覽器版沒有 here');
  // 〔同步〕桌面已加上其他電腦的計數：有 here（這台今天的），說明寫所有電腦的合計與其中這台的
  const merged = T.tokensTitle({ ...t, here: { input: 26_418, cached: 18_772, output: 7_913 } });
  assert.match(merged, /token 合計：1,284,331/);
  assert.match(merged, /其中這台電腦：in 26,418・out 7,913/);
  assert.match(merged, /所有電腦上 Clode Myriagent 的對話合計/);
  assert.doesNotMatch(merged, /只算這台電腦/);
  const lines = merged.split('\n');
  assert.ok(lines.indexOf('其中這台電腦：in 26,418・out 7,913') === 3, '接在 out 那一行之後：' + lines.join(' | '));
});

test('remoterules：側欄與儀表板的執行中、等你回應加上其他電腦的', () => {
  assert.deepEqual(R.sessionTotals({ sessions: { open: 2, running: 1, waiting: 0, background: 1 }, otherSessions: { open: 3, running: 2, waiting: 1, background: 2 } }),
    { running: 3, waiting: 1, background: 3, otherRunning: 2, otherWaiting: 1, otherBackground: 2, programs: 0, otherPrograms: 0, subagents: 0, otherSubagents: 0, open: 5, otherOpen: 3 }, 'background＝等背景工作回報，同樣加上其他在線電腦的');
  assert.deepEqual(R.sessionTotals({ sessions: { running: 2, waiting: 1 } }), { running: 2, waiting: 1, background: 0, otherRunning: 0, otherWaiting: 0, otherBackground: 0, programs: 0, otherPrograms: 0, subagents: 0, otherSubagents: 0, open: 0, otherOpen: 0 },
    '未配對、瀏覽器版沒有 otherSessions；舊版的宿主與伺服器沒有 background');
  assert.deepEqual(R.sessionTotals({ sessions: { running: 1 }, otherSessions: null }), { running: 1, waiting: 0, background: 0, otherRunning: 0, otherWaiting: 0, otherBackground: 0, programs: 0, otherPrograms: 0, subagents: 0, otherSubagents: 0, open: 0, otherOpen: 0 }, '沒連結的是 null');
  assert.deepEqual(R.sessionTotals({ sessions: { running: -1, waiting: 'x', background: -2 }, otherSessions: { running: 1.9, background: 2.5 } }),
    { running: 1, waiting: 0, background: 2, otherRunning: 1, otherWaiting: 0, otherBackground: 2, programs: 0, otherPrograms: 0, subagents: 0, otherSubagents: 0, open: 0, otherOpen: 0 }, '不是正數當 0、小數捨去');
  assert.deepEqual(R.sessionTotals(null), { running: 0, waiting: 0, background: 0, otherRunning: 0, otherWaiting: 0, otherBackground: 0, programs: 0, otherPrograms: 0, subagents: 0, otherSubagents: 0, open: 0, otherOpen: 0 });
  assert.deepEqual(R.sessionTotals({ sessions: { programs: 1, background: 1 }, otherSessions: { programs: 2 } }),
    { running: 0, waiting: 0, background: 1, programs: 3, otherRunning: 0, otherWaiting: 0, otherBackground: 0, otherPrograms: 2, subagents: 0, otherSubagents: 0, open: 0, otherOpen: 0 },
    'programs＝背景程式（脫離 CLI 的程式），和等背景工作回報分開算，也加上其他電腦的');
  assert.equal(R.sessionTitle(3, 2, '執行中'), '3 個對話執行中（其中其他電腦 2 個）');
  assert.equal(R.sessionTitle(2, 2, '等待你回應'), '2 個對話等待你回應（都在其他電腦）');
  assert.equal(R.sessionTitle(1, 0, '執行中'), '1 個對話執行中');
  assert.equal(R.sessionTitle(0, 0, '執行中'), '');
  assert.equal(R.sessionTitle(2, 1, '等背景工作回報'), '2 個對話等背景工作回報（其中其他電腦 1 個）');
  // 執行中的子代理（2026-10-28）：個數不是對話數，同樣加上其他在線電腦的；舊版沒有這個欄位當 0
  const sub = R.sessionTotals({ sessions: { running: 1, subagents: 3 }, otherSessions: { subagents: 2 } });
  assert.equal(sub.subagents, 5);
  assert.equal(sub.otherSubagents, 2);
  assert.equal(R.subagentsTitle(5, 2), '5 個子代理執行中（其中其他電腦 2 個）');
  assert.equal(R.subagentsTitle(2, 2), '2 個子代理執行中（都在其他電腦）');
  assert.equal(R.subagentsTitle(1, 0), '1 個子代理執行中');
  assert.equal(R.subagentsTitle(0, 0), '');
});

test('remoterules：背景程式標示的提示——這台列對話與程式、其他電腦只寫個數', () => {
  assert.equal(R.programsTitle({ sessions: { programs: 0, background: 3 } }), '', '沒有背景程式（等背景工作回報不算）');
  const a = {
    sessions: { programs: 2 }, otherSessions: { programs: 1 },
    programs: [
      { conversationId: 'c1', title: '開發工作六', programs: ['bash chain7.sh', 'python worker6.py --batch'], more: 0 },
      { conversationId: 'c2', title: '', programs: ['node server.js', 'a', 'b', 'c'], more: 3 },
    ],
  };
  const lines = R.programsTitle(a).split('\n');
  assert.equal(lines[0], '3 個對話的背景程式還在跑（這一輪已經結束，代理開的程式還在執行，跑完不會通知代理）');
  assert.equal(lines[1], '・開發工作六：bash chain7.sh、python worker6.py --batch');
  assert.equal(lines[2], '・（沒有標題）：node server.js、a、b、c…等 7 個');
  assert.equal(lines[3], '・其他電腦 1 個對話');
  assert.equal(R.programsTitle({ sessions: { programs: 1 } }), '1 個對話的背景程式還在跑（這一輪已經結束，代理開的程式還在執行，跑完不會通知代理）', '瀏覽器版只有個數');
  const many = { sessions: { programs: 7 }, programs: Array.from({ length: 7 }, (_, i) => ({ title: 't' + i, programs: [] })) };
  const out = R.programsTitle(many).split('\n');
  assert.equal(out.length, 7, '最多列 5 個對話＋一行「還有」');
  assert.equal(out[6], '・還有 2 個對話');
});

// ───── 輸入區的輸入紀錄（historyrules.js）─────
test('historyrules：一份紀錄記下送出的訊息，最新的在前、不重複、最多 10 則', () => {
  let list = [];
  for (let i = 1; i <= 12; i++) list = IH.pushHistory(list, `第 ${i} 則`);
  assert.equal(list.length, IH.HISTORY_LIMIT);
  assert.equal(list[0], '第 12 則');
  assert.equal(list[9], '第 3 則', '超過 10 則丟掉最舊的');
  list = IH.pushHistory(list, '第 5 則');
  assert.deepEqual(list.slice(0, 2), ['第 5 則', '第 12 則'], '再送一次同樣的：移到最前面');
  assert.equal(list.filter((t) => t === '第 5 則').length, 1);
  assert.equal(IH.pushHistory(list, '   '), list, '空白不記');
  assert.equal(IH.pushHistory(list, 'x'.repeat(IH.HISTORY_MAX_CHARS + 1)), list, '太長的不記');
  assert.deepEqual(IH.pushHistory(null, '/clear'), ['/clear'], '指令也記');
  assert.deepEqual(IH.pushHistory([], '第一行\n第二行'), ['第一行\n第二行'], '多行原樣保留');
});

test('historyrules：每個對話各記一份，最近送出過的對話在前', () => {
  let store = [];
  store = IH.pushStore(store, 'c1', '對話一的第一則');
  store = IH.pushStore(store, 'c2', '對話二的第一則');
  store = IH.pushStore(store, 'c1', '對話一的第二則');
  assert.deepEqual(IH.historyOf(store, 'c1'), ['對話一的第二則', '對話一的第一則']);
  assert.deepEqual(IH.historyOf(store, 'c2'), ['對話二的第一則'], '別的對話送出的不混進來');
  assert.deepEqual(IH.historyOf(store, 'c3'), [], '沒送出過的對話');
  assert.deepEqual(store.map((e) => e.id), ['c1', 'c2'], '剛送出的對話移到最前面');
  store = IH.pushStore(store, 'c2', '對話一的第一則');
  assert.deepEqual(IH.historyOf(store, 'c2'), ['對話一的第一則', '對話二的第一則'], '同樣的文字在別的對話照樣記');
  assert.deepEqual(IH.historyOf(store, 'c1'), ['對話一的第二則', '對話一的第一則']);
  for (let i = 1; i <= 12; i++) store = IH.pushStore(store, 'c1', `第 ${i} 則`);
  assert.equal(IH.historyOf(store, 'c1').length, IH.HISTORY_LIMIT, '每個對話最多 10 則');
  assert.equal(IH.historyOf(store, 'c2').length, 2, '不占別的對話的則數');
  assert.equal(IH.pushStore(store, 'c1', '   '), store, '空白不記');
  assert.equal(IH.pushStore(store, '', 'x'), store, '沒有對話 id 不記');
  assert.equal(IH.pushStore(store, null, 'x'), store);
  assert.deepEqual(IH.historyOf(null, 'c1'), []);
});

test('historyrules：對話數與總字數的上限，最久沒送出的先丟', () => {
  let store = [];
  for (let i = 0; i < IH.HISTORY_CONVERSATIONS + 5; i++) store = IH.pushStore(store, 'c' + i, '訊息 ' + i);
  assert.equal(store.length, IH.HISTORY_CONVERSATIONS);
  assert.equal(store[0].id, 'c' + (IH.HISTORY_CONVERSATIONS + 4));
  assert.deepEqual(IH.historyOf(store, 'c0'), [], '最久沒送出的對話被丟掉');
  assert.deepEqual(IH.historyOf(store, 'c5'), ['訊息 5']);
  const big = (n) => String(n).padEnd(IH.HISTORY_MAX_CHARS, '字');
  let s2 = [];
  for (let i = 0; i < 6; i++) s2 = IH.pushStore(s2, 'a', big(i));
  for (let i = 0; i < 6; i++) s2 = IH.pushStore(s2, 'b', big(10 + i));
  const total = s2.reduce((n, e) => n + e.list.reduce((m, t) => m + t.length, 0), 0);
  assert.ok(total <= IH.HISTORY_TOTAL_CHARS, '總字數不超過上限：' + total);
  assert.equal(IH.historyOf(s2, 'b').length, 6, '剛送出的對話整份留著');
  assert.deepEqual(IH.historyOf(s2, 'a').map((t) => t[0]), ['5', '4', '3', '2'], '放不下時從較早的對話最舊的丟起');
  for (let i = 0; i < 10; i++) s2 = IH.pushStore(s2, 'c', big(20 + i));
  assert.equal(IH.historyOf(s2, 'c').length, 10, '10 則都是上限長度也放得下');
  assert.deepEqual(s2.map((e) => e.id), ['c']);
});

test('historyrules：草稿換成正式對話時紀錄搬過去', () => {
  let store = IH.pushStore([], 'other', '別的對話');
  store = IH.pushStore(store, 'draft-1', '草稿送出的第一則');
  const moved = IH.moveStore(store, 'draft-1', 'c9');
  assert.deepEqual(IH.historyOf(moved, 'c9'), ['草稿送出的第一則']);
  assert.deepEqual(IH.historyOf(moved, 'draft-1'), [], '草稿的拿掉');
  assert.deepEqual(moved.map((e) => e.id), ['c9', 'other']);
  assert.equal(IH.moveStore(moved, 'draft-1', 'c9'), moved, '再搬一次（另一個視窗也收到）不動');
  assert.equal(IH.moveStore(store, 'draft-1', 'draft-1'), store);
  const both = IH.moveStore(IH.pushStore(store, 'c9', '新對話已有的'), 'draft-1', 'c9');
  assert.deepEqual(IH.historyOf(both, 'c9'), ['新對話已有的', '草稿送出的第一則'], '新的 id 原本有的在前');
});

test('historyrules：讀回 localStorage 的字串', () => {
  assert.deepEqual(IH.parseStore(null), []);
  assert.deepEqual(IH.parseStore(''), []);
  assert.deepEqual(IH.parseStore('{壞掉'), []);
  assert.deepEqual(IH.parseStore('{"a":1}'), [], '不是陣列');
  assert.deepEqual(IH.parseStore(JSON.stringify(['a', 'b'])), [], '舊版的字串陣列不認（分不出是哪個對話的）');
  const raw = JSON.stringify([
    { id: 'c1', list: ['a', 3, null, '', '  ', 'a', 'b'] },
    { id: 'c1', list: ['重複的對話'] },
    { id: '', list: ['x'] },
    { id: 5, list: ['x'] },
    null,
    { id: 'c2', list: [] },
    { id: 'c3', list: 'abc' },
    { id: 'c4', list: Array.from({ length: 15 }, (_, i) => `t${i}`) },
  ]);
  const store = IH.parseStore(raw);
  assert.deepEqual(store.map((e) => e.id), ['c1', 'c4'], '略過 id 不對、重複與沒有紀錄的對話');
  assert.deepEqual(IH.historyOf(store, 'c1'), ['a', 'b'], '略過不是字串、空白與重複的');
  assert.equal(IH.historyOf(store, 'c4').length, 10, '最多 10 則');
  assert.deepEqual(IH.parseStore(JSON.stringify(store)), store, '寫回去再讀一樣');
  assert.notEqual(IH.HISTORY_KEY, IH.LEGACY_HISTORY_KEY);
});

test('historyrules：空的輸入框按 ↑↓ 依序帶入，↓ 到底還原', () => {
  const stored = ['三', '二', '一'];
  let nav = IH.idleNav();
  const press = (dir, value, sel = value.length, selEnd = sel) => {
    const r = IH.historyMove(nav, dir, value, sel, selEnd, stored);
    if (r) nav = r.nav;
    return r ? r.value : null;
  };
  assert.equal(press('down', ''), null, '沒有在翻紀錄時 ↓ 照預設');
  assert.equal(press('up', ''), '三');
  assert.equal(press('up', '三'), '二', '游標在最後（剛帶入的位置）就繼續往前翻');
  assert.equal(press('up', '二'), '一');
  assert.equal(press('up', '一'), null, '最舊的一則再按 ↑ 照預設');
  assert.equal(press('down', '一'), '二');
  assert.equal(press('down', '二'), '三');
  assert.equal(press('down', '三'), '', '最新的一則再按 ↓：還原開始翻之前的文字');
  assert.equal(nav.index, -1);
  assert.equal(press('down', ''), null);
  assert.equal(IH.historyMove(IH.idleNav(), 'up', '', 0, 0, []), null, '沒有紀錄');
});

test('historyrules：有打字時游標要在最前面才翻，草稿留著', () => {
  const stored = ['上一則', '更早'];
  assert.equal(IH.historyMove(IH.idleNav(), 'up', '草稿', 2, 2, stored), null, '游標不在最前面：照預設移動游標');
  assert.equal(IH.historyMove(IH.idleNav(), 'up', '草稿', 0, 2, stored), null, '有選取範圍');
  const r = IH.historyMove(IH.idleNav(), 'up', '草稿', 0, 0, stored);
  assert.equal(r.value, '上一則');
  assert.equal(r.nav.draft, '草稿');
  const back = IH.historyMove(r.nav, 'down', '上一則', 3, 3, stored);
  assert.equal(back.value, '草稿', '↓ 回到草稿');
  assert.equal(back.nav.index, -1);
});

test('historyrules：多行的一則，游標移開最後就照預設移動游標', () => {
  const stored = ['第一行\n第二行', '更早'];
  const r = IH.historyMove(IH.idleNav(), 'up', '', 0, 0, stored);
  const v = r.value;
  assert.equal(IH.historyMove(r.nav, 'up', v, 2, 2, stored), null, '游標在中間：↑ 在文字裡移動');
  assert.equal(IH.historyMove(r.nav, 'down', v, 2, 2, stored), null, '游標在中間：↓ 在文字裡移動');
  assert.equal(IH.historyMove(r.nav, 'up', v, 0, 0, stored).value, '更早', '移到最前面再按 ↑ 繼續翻');
  assert.equal(IH.historyMove(r.nav, 'down', v, v.length, v.length, stored).value, '', '在最後按 ↓ 繼續翻');
});

test('historyrules：改過帶入的文字之後，改過的就是新的草稿', () => {
  const stored = ['二', '一'];
  const r1 = IH.historyMove(IH.idleNav(), 'up', '', 0, 0, stored);
  const r2 = IH.historyMove(r1.nav, 'up', '二', 1, 1, stored);
  assert.equal(r2.value, '一');
  assert.equal(IH.historyMove(r2.nav, 'down', '一改', 2, 2, stored), null, '改過了：↓ 照預設');
  assert.equal(IH.historyMove(r2.nav, 'up', '一改', 2, 2, stored), null, '改過了、游標不在最前面：↑ 照預設');
  const r3 = IH.historyMove(r2.nav, 'up', '一改', 0, 0, stored);
  assert.equal(r3.value, '二', '從最新的一則重新開始翻');
  assert.equal(r3.nav.draft, '一改');
});

test('historyrules：和目前文字相同的紀錄略過', () => {
  const stored = ['同樣', '另一則'];
  const r = IH.historyMove(IH.idleNav(), 'up', '同樣', 0, 0, stored);
  assert.equal(r.value, '另一則', '帶入和輸入框一樣的文字看起來像沒反應');
  assert.equal(IH.historyMove(r.nav, 'down', '另一則', 3, 3, stored).value, '同樣', '↓ 直接回到草稿');
  const r2 = IH.historyMove(IH.idleNav(), 'up', '', 0, 0, ['三', '草稿x', '一']);
  const draftNav = Object.assign({}, r2.nav, { draft: '草稿x' });
  assert.equal(IH.historyMove(draftNav, 'up', '三', 1, 1, ['三', '草稿x', '一']).value, '一', '翻的途中也略過和草稿相同的');
  assert.equal(IH.historyMove(IH.idleNav(), 'up', '只有', 0, 0, ['只有']), null);
});

test('historyrules：翻的期間用開始時的那份紀錄', () => {
  const r = IH.historyMove(IH.idleNav(), 'up', '', 0, 0, ['b', 'a']);
  const r2 = IH.historyMove(r.nav, 'up', 'b', 1, 1, ['新', 'b', 'a']);
  assert.equal(r2.value, 'a', '別的視窗剛送出的不插進來');
});

// ───── 登入到期（loginrules.js）─────
test('loginrules：只看已登入的本機帳戶，3 天內是 soon、過了是 expired', () => {
  const DAY = 86_400_000;
  const acc = (left, extra) => Object.assign({ id: 'a1', status: 'ready', loginExpiresAt: NOW + left }, extra);
  const ok = L.loginExpiry(acc(19 * DAY + 5 * 3_600_000), NOW);
  assert.deepEqual([ok.level, ok.short, ok.phrase, ok.at], ['ok', '19d', '19 天後', NOW + 19 * DAY + 5 * 3_600_000]);
  assert.equal(L.loginExpiry(acc(3 * DAY + 1), NOW).level, 'ok', '剛好超過 3 天還不提醒');
  assert.equal(L.loginExpiry(acc(3 * DAY), NOW).level, 'soon', '剩 3 天整開始提醒（同 Claude CLI）');
  const soon = L.loginExpiry(acc(2 * DAY + 23 * 3_600_000), NOW);
  assert.deepEqual([soon.level, soon.short, soon.phrase], ['soon', '2d', '2 天後'], '無條件捨去');
  assert.deepEqual([L.loginExpiry(acc(5 * 3_600_000 + 59 * 60_000), NOW).short, L.loginExpiry(acc(5 * 3_600_000), NOW).phrase], ['5h', '5 小時後']);
  assert.deepEqual([L.loginExpiry(acc(45 * 60_000 + 59_000), NOW).short, L.loginExpiry(acc(45 * 60_000), NOW).phrase], ['45m', '45 分鐘後']);
  assert.deepEqual([L.loginExpiry(acc(59_999), NOW).short, L.loginExpiry(acc(1), NOW).phrase], ['<1m', '不到 1 分鐘後']);
  for (const left of [0, -1, -5 * DAY]) {
    const x = L.loginExpiry(acc(left), NOW);
    assert.deepEqual([x.level, x.short, x.phrase], ['expired', '到期', ''], String(left));
  }
  // 沒有到期資料（Codex、Grok、Antigravity、舊宿主、瀏覽器版）、不是已登入、remoteOnly、壞資料一律 null
  for (const a of [null, undefined, 'x', {}, { status: 'ready' }, acc(DAY, { status: 'notLoggedIn' }), acc(DAY, { status: 'checking' }),
    acc(DAY, { remoteOnly: true }), { status: 'ready', loginExpiresAt: '1794000000000' }, { status: 'ready', loginExpiresAt: 0 },
    { status: 'ready', loginExpiresAt: -1 }, { status: 'ready', loginExpiresAt: NaN }, { status: 'ready', loginExpiresAt: Infinity },
    { status: 'ready', loginExpiresAt: null }]) {
    assert.equal(L.loginExpiry(a, NOW), null, JSON.stringify(a));
  }
  assert.equal(L.loginExpiry(acc(DAY), undefined), null, '現在時間由呼叫端給');
  assert.equal(L.loginExpiry(acc(DAY), NaN), null);
});

test('loginrules：說明文字', () => {
  const DAY = 86_400_000;
  const x = (left) => L.loginExpiry({ status: 'ready', loginExpiresAt: NOW + left }, NOW);
  assert.equal(L.loginExpiryText(x(19 * DAY), '11/12 11:40'), '登入到期：11/12 11:40（19 天後）');
  assert.equal(L.loginExpiryText(x(2 * DAY), '10/25 11:40'), '登入將在 2 天後到期（10/25 11:40），重新登入後會重新起算');
  assert.equal(L.loginExpiryText(x(-1), '10/23 11:40'), '登入已到期（10/23 11:40），請重新登入');
  assert.equal(L.loginExpiryText(x(19 * DAY), ''), '登入到期：19 天後', '沒有日期文字只寫相對時間');
  assert.equal(L.loginExpiryText(x(2 * DAY), null), '登入將在 2 天後到期，重新登入後會重新起算');
  assert.equal(L.loginExpiryText(x(-1)), '登入已到期，請重新登入');
  assert.equal(L.loginExpiryText(null, '11/12'), '');
});

// ───── 對話框的視覺效果（fxrules.js）─────
test('fxrules：一批新增的項目不超過上限才做進場動畫', () => {
  assert.equal(FX.ENTRANCE_MAX, 6);
  assert.equal(FX.entranceAllowed(1), true);
  assert.equal(FX.entranceAllowed(6), true);
  assert.equal(FX.entranceAllowed(7), false, '重新連線補上一大批時整批直接出現');
  assert.equal(FX.entranceAllowed(0), false);
  assert.equal(FX.entranceAllowed(-1), false);
  assert.equal(FX.entranceAllowed(1.5), false);
  assert.equal(FX.entranceAllowed(undefined), false);
});

test('fxrules：差異比例條依增刪行數分 5 格，小改動只亮幾格', () => {
  const cells = (a, d) => FX.diffRatioCells(a, d).join(' ');
  assert.equal(cells(12, 3), 'add add add add del');
  assert.equal(cells(18, 4), 'add add add add del');
  assert.equal(cells(0, 7), 'del del del del del');
  assert.equal(cells(7, 0), 'add add add add add');
  assert.equal(cells(1, 0), 'add none none none none', '只改一行只亮一格');
  assert.equal(cells(1, 1), 'add del none none none');
  assert.equal(cells(2, 1), 'add add del none none');
  assert.equal(cells(100, 1), 'add add add add del', '兩種都有時少的那種至少一格');
  assert.equal(cells(1, 100), 'add del del del del');
  assert.deepEqual(FX.diffRatioCells(0, 0), [], '沒有增刪不畫');
  assert.deepEqual(FX.diffRatioCells(undefined, null), []);
  assert.equal(FX.diffRatioCells('3', -2).join(' '), 'add add add none none', '字串照數字算、負數當 0');
  for (let a = 0; a <= 40; a++) {
    for (let d = 0; d <= 40; d++) {
      const r = FX.diffRatioCells(a, d);
      if (!a && !d) { assert.equal(r.length, 0); continue; }
      assert.equal(r.length, 5, `${a}/${d}`);
      const na = r.filter((c) => c === 'add').length, nd = r.filter((c) => c === 'del').length;
      assert.equal(na + nd, Math.min(5, a + d), `${a}/${d} 上色的格數`);
      assert.equal(na > 0, a > 0, `${a}/${d} 有增加才有綠格`);
      assert.equal(nd > 0, d > 0, `${a}/${d} 有刪除才有紅格`);
      assert.equal(r.join(' '), [...r].sort((x, y) => ['add', 'del', 'none'].indexOf(x) - ['add', 'del', 'none'].indexOf(y)).join(' '), '依序是綠、紅、灰');
    }
  }
});

test('fxrules：程式碼的行數不算結尾的換行', () => {
  assert.equal(FX.codeLineCount('a\nb\nc\n'), 3);
  assert.equal(FX.codeLineCount('a\nb\nc'), 3);
  assert.equal(FX.codeLineCount('a\r\nb\r\n'), 2);
  assert.equal(FX.codeLineCount('a\n\n'), 2, '只去掉最後一個換行，空白行照算');
  assert.equal(FX.codeLineCount('one'), 1);
  assert.equal(FX.codeLineCount('\n'), 0);
  assert.equal(FX.codeLineCount(''), 0);
  assert.equal(FX.codeLineCount(null), 0);
});

test('fxrules：上一輪的 token 數只認宿主與伺服器的格式，排回去和原文一樣', () => {
  assert.deepEqual(FX.parseTurnTokens('輸入 12,345・輸出 2,100 tokens'), { input: 12345, output: 2100 });
  assert.deepEqual(FX.parseTurnTokens('輸入 0・輸出 0 tokens'), { input: 0, output: 0 });
  assert.deepEqual(FX.parseTurnTokens('輸入 1,234,567・輸出 999 tokens'), { input: 1234567, output: 999 });
  for (const bad of [null, undefined, '', 42, '輸入 12.345・輸出 2.100 tokens', '輸入 12345,6・輸出 1 tokens', '輸入 1・輸出 2 tokens（快取）', ' 輸入 1・輸出 2 tokens', '輸入 ・輸出 2 tokens']) {
    assert.equal(FX.parseTurnTokens(bad), null, String(bad));
  }
  // 計數器以 fmtTokens 排回去，畫面上的文字要和宿主送來的一字不差
  for (const text of ['輸入 12,345・輸出 2,100 tokens', '輸入 9・輸出 1,000 tokens', '輸入 1,234,567・輸出 0 tokens']) {
    const t = FX.parseTurnTokens(text);
    assert.equal(`輸入 ${T.fmtTokens(t.input)}・輸出 ${T.fmtTokens(t.output)} tokens`, text);
  }
});

test('fxrules：一輪結束的燈——錯誤紅、剛按停止不閃、其他綠', () => {
  const now = 1_800_000_000_000;
  assert.equal(FX.turnEndTone('Assistant', undefined, now), 'ok');
  assert.equal(FX.turnEndTone(null, 0, now), 'ok', '沒有項目也算結束');
  assert.equal(FX.turnEndTone('Error', undefined, now), 'err');
  assert.equal(FX.turnEndTone('Assistant', now - 3000, now), null, '剛在這裡按停止');
  assert.equal(FX.turnEndTone('Error', now - 3000, now), null);
  assert.equal(FX.turnEndTone('Assistant', now - FX.STOP_QUIET_MS, now), 'ok', '停止是很久以前的事');
  assert.equal(FX.turnEndTone('Assistant', now + 5000, now), 'ok', '時鐘往回調：不當成剛按過');
});

test('fxrules：工具只有從執行中變成完成或失敗才閃', () => {
  assert.equal(FX.toolSettleFx('Running', 'Done'), 'fx-done');
  assert.equal(FX.toolSettleFx('Running', 'Failed'), 'fx-fail');
  for (const s of ['Declined', 'Cancelled', 'Running', 'Waiting', undefined]) assert.equal(FX.toolSettleFx('Running', s), null, String(s));
  assert.equal(FX.toolSettleFx(undefined, 'Done'), null, '第一次就是完成的（載入、很快做完）不閃');
  assert.equal(FX.toolSettleFx('Waiting', 'Done'), null);
  assert.equal(FX.toolSettleFx('Done', 'Done'), null);
});

test('fxrules：工具做完、思考結束時內容掃過一遍——從執行中變成完成或失敗、或一進來就做完的；展開著的不播', () => {
  assert.equal(FX.peekWanted('Running', 'Done', false, false), true);
  assert.equal(FX.peekWanted('Running', 'Failed', false, false), true);
  assert.equal(FX.peekWanted(undefined, 'Done', true, false), true, '一進來就已經完成（很快就做完的）');
  assert.equal(FX.peekWanted(undefined, 'Failed', true, false), true);
  assert.equal(FX.peekWanted('Running', 'Done', false, true), false, '展開著的內容本來就看得到');
  assert.equal(FX.peekWanted(undefined, 'Done', true, true), false);
  for (const s of ['Declined', 'Cancelled', 'Running', 'Waiting', undefined]) {
    assert.equal(FX.peekWanted('Running', s, false, false), false, String(s));
    assert.equal(FX.peekWanted(undefined, s, true, false), false, '新的 ' + String(s));
  }
  assert.equal(FX.peekWanted('Waiting', 'Done', false, false), false, '核准後才執行的要等它從執行中變過來');
  assert.equal(FX.peekWanted('Done', 'Done', false, false), false, '已經完成的再更新（補媒體、輸出）不重播');
  assert.equal(FX.peekWanted(undefined, 'Done', false, false), false, '還沒載入的較早項目被更新（upsert 丟掉）');
});

test('fxrules：掃過的內容——差異優先、其次輸出、再其次輸入；跳過開頭與結尾的空行、行數與每行的字數有上限', () => {
  const diff = [{ t: 'hunk', s: '@@ -1,2 +1,2 @@' }, { t: 'del', s: 'old' }, { t: 'add', s: 'new' }, { t: 'ctx', s: 'same' }, {}];
  assert.deepEqual(FX.toolPeekLines({ diff, output: '不用這個' }).lines,
    [{ s: '@@ -1,2 +1,2 @@', t: 'hunk' }, { s: 'old', t: 'del' }, { s: 'new', t: 'add' }, { s: 'same', t: null }, { s: '', t: null }]);
  assert.deepEqual(FX.toolPeekLines({ output: '\r\n\n  \na\r\nb\n\n' }).lines.map((l) => l.s), ['a', 'b'], '換行統一、開頭的空行跳過、結尾的空行拿掉');
  assert.deepEqual(FX.toolPeekLines({ output: '  \n', detail: '{"file":"x"}' }).lines, [{ s: '{"file":"x"}', t: null }], '輸出只有空白時看輸入');
  assert.equal(FX.toolPeekLines({ output: '', detail: '   ', diff: [] }), null);
  assert.equal(FX.toolPeekLines({}), null);
  assert.equal(FX.toolPeekLines(null), null);
  const many = FX.toolPeekLines({ output: Array.from({ length: 500 }, (_, i) => 'line ' + i).join('\n') }).lines;
  assert.equal(many.length, FX.PEEK_MAX_LINES + 1);
  assert.deepEqual(many[FX.PEEK_MAX_LINES], { s: '…', t: 'more' }, '後面還有就接一行「…」');
  assert.equal(many[0].s, 'line 0');
  const exact = FX.toolPeekLines({ output: Array.from({ length: FX.PEEK_MAX_LINES }, (_, i) => 'x' + i).join('\n') + '\n' }).lines;
  assert.equal(exact.length, FX.PEEK_MAX_LINES, '剛好上限（結尾換行）不接「…」');
  const wide = FX.toolPeekLines({ output: 'y'.repeat(5000) + '\nz' }).lines;
  assert.equal(wide[0].s.length, FX.PEEK_MAX_COLS);
  assert.equal(wide[1].s, 'z');
  const bigDiff = FX.toolPeekLines({ diff: Array.from({ length: 200 }, () => ({ t: 'add', s: 'q'.repeat(400) })) }).lines;
  assert.equal(bigDiff.length, FX.PEEK_MAX_LINES + 1);
  assert.equal(bigDiff[0].s.length, FX.PEEK_MAX_COLS);
  // 好幾 MB 的輸出只看開頭那一段
  const huge = 'h'.repeat(8 * 1024 * 1024);
  const t0 = Date.now();
  const hl = FX.toolPeekLines({ output: huge }).lines;
  assert.ok(Date.now() - t0 < 200, '只切開頭，不整份處理');
  assert.equal(hl[0].s.length, FX.PEEK_MAX_COLS);
  assert.deepEqual(hl[hl.length - 1], { s: '…', t: 'more' });
});

test('fxrules：思考的 html 拆成段落文字——不經 DOM、標籤拿掉、實體解碼、程式碼框一行一段', () => {
  const html = '<h2>計畫</h2>\n<p>先讀 <code>items.js</code>，再改 <strong>fx.css</strong> &amp; 測試。</p>\n'
    + '<ul>\n<li><p>第一步</p>\n</li>\n<li>第二步 <a href="x">連結</a></li>\n</ul>\n'
    + '<blockquote>\n<p>引用 &lt;a&gt; &#39;單引號&#39; &#x4e2d;&#25991; &nbsp;x &bogus;</p>\n</blockquote>\n'
    + '<pre><code class="language-js">const a = 1;\nconst b = 2;\n</code></pre>\n'
    + '<table><tr><td>甲</td><td>乙</td></tr></table>\n<p>一<br>二</p>';
  const { blocks, cut } = FX.htmlTextBlocks(html);
  assert.equal(cut, false);
  assert.deepEqual(blocks.map((b) => b.trim()).filter(Boolean), [
    '計畫', '先讀 items.js，再改 fx.css & 測試。', '第一步', '第二步 連結',
    "引用 <a> '單引號' 中文  x &bogus;", 'const a = 1;', 'const b = 2;', '甲 乙', '一', '二',
  ]);
  assert.deepEqual(FX.htmlTextBlocks(''), { blocks: [], cut: false });
  assert.deepEqual(FX.htmlTextBlocks(null), { blocks: [], cut: false });
  assert.deepEqual(FX.htmlTextBlocks('<p>&#0; &#x110000; &#55296x;</p>').blocks.map((b) => b.trim()).filter(Boolean), ['&#0; &#x110000; &#55296x;'], '不合法的碼位照原文');
  // 好幾倍上限長的 html 只看開頭；切在標籤中間的那一段拿掉
  const long = '<p>' + 'a'.repeat(FX.PEEK_THINK_MAX_CHARS * 4 - 6) + '</p><p class="x">b</p>';
  const lb = FX.htmlTextBlocks(long);
  assert.equal(lb.cut, true);
  assert.ok(lb.blocks.every((b) => !b.includes('<')), '沒有留下半個標籤');
});

test('fxrules：思考掃過的內容——空白縮成一個、空段落跳過、合計字數與段數有上限、不切在代理對中間', () => {
  assert.deepEqual(FX.thinkPeekLines({ html: '<p>  先   看\n看  </p>\n\n<p></p><p>再做</p>' }).lines,
    [{ s: '先 看', t: null }, { s: '看', t: null }, { s: '再做', t: null }], 'Markdig 的軟換行也分段');
  assert.equal(FX.thinkPeekLines({ html: '<p>   </p>' }), null);
  assert.equal(FX.thinkPeekLines({ html: '' }), null);
  assert.equal(FX.thinkPeekLines({}), null);
  assert.equal(FX.thinkPeekLines(null), null);
  const max = FX.PEEK_THINK_MAX_CHARS;
  const big = FX.thinkPeekLines({ html: '<p>' + 'x'.repeat(max - 10) + '</p><p>' + 'y'.repeat(50) + '</p><p>z</p>' }).lines;
  assert.equal(big.length, 3);
  assert.equal(big[0].s.length + big[1].s.length, max, '合計剛好上限');
  assert.deepEqual(big[2], { s: '…', t: 'more' });
  const exact = FX.thinkPeekLines({ html: '<p>' + 'x'.repeat(max) + '</p>' }).lines;
  assert.equal(exact.length, 1, '剛好上限、後面沒有了不接「…」');
  const paras = FX.thinkPeekLines({ html: Array.from({ length: 100 }, (_, i) => '<p>p' + i + '</p>').join('\n') }).lines;
  assert.equal(paras.length, FX.PEEK_MAX_LINES + 1);
  assert.deepEqual(paras[FX.PEEK_MAX_LINES], { s: '…', t: 'more' });
  // 第 max 個字落在表情符號（代理對）的前半：整個字留到下一段（不截出半個）
  const emoji = FX.thinkPeekLines({ html: '<p>' + 'x'.repeat(max - 1) + String.fromCodePoint(0x1F600) + 'tail</p>' }).lines;
  assert.equal(emoji[0].s, 'x'.repeat(max - 1));
  assert.deepEqual(emoji[1], { s: '…', t: 'more' });
});

test('fxrules：掃過一遍的時間——不到小框行數的不捲、停久一點；捲的時間依行數、有上下限', () => {
  const short = FX.toolPeekTiming(3);
  assert.deepEqual([short.rows, short.steps, short.sweep, short.hold], [3, 0, 0, FX.PEEK_SHORT_HOLD_MS]);
  assert.equal(short.foldAt, FX.PEEK_OPEN_MS + FX.PEEK_SHORT_HOLD_MS);
  assert.equal(short.total, short.foldAt + FX.PEEK_FOLD_MS);
  assert.deepEqual([FX.toolPeekTiming(FX.PEEK_ROWS).rows, FX.toolPeekTiming(FX.PEEK_ROWS).steps], [FX.PEEK_ROWS, 0]);
  const one = FX.toolPeekTiming(FX.PEEK_ROWS + 1);
  assert.deepEqual([one.rows, one.steps, one.sweep, one.hold], [FX.PEEK_ROWS, 1, FX.PEEK_SWEEP_MIN_MS, FX.PEEK_HOLD_MS]);
  const mid = FX.toolPeekTiming(30);
  assert.equal(mid.steps, 30 - FX.PEEK_ROWS);
  assert.equal(mid.sweep, (30 - FX.PEEK_ROWS) * FX.PEEK_MS_PER_LINE);
  const max = FX.toolPeekTiming(FX.PEEK_MAX_LINES + 1);
  assert.equal(max.sweep, FX.PEEK_SWEEP_MAX_MS);
  assert.ok(max.total <= 1400, '最長也不到一秒半');
  assert.equal(max.foldAt, FX.PEEK_OPEN_MS + FX.PEEK_SWEEP_MAX_MS + FX.PEEK_HOLD_MS);
  for (const bad of [0, -3, NaN, undefined, 'x']) assert.deepEqual([FX.toolPeekTiming(bad).rows, FX.toolPeekTiming(bad).steps], [0, 0], String(bad));
});

test('fxrules：執行列換階段——建置完成開始執行綠、建置失敗與無法啟動紅、暫停琥珀、其他回到閒置是餘暉', () => {
  assert.equal(FX.launchFx('building', 'running', null), 'ok');
  assert.equal(FX.launchFx('building', 'idle', 'buildFailed'), 'err');
  assert.equal(FX.launchFx('building', 'idle', 'startFailed'), 'err');
  assert.equal(FX.launchFx('running', 'paused', null), 'pause');
  for (const [prev, kind] of [['running', 'exited'], ['paused', 'stopped'], ['stopping', 'stopped'], ['building', 'stopped']]) {
    assert.equal(FX.launchFx(prev, 'idle', kind), 'cool', prev + '→idle（' + kind + '）');
  }
  assert.equal(FX.launchFx(undefined, 'running', null), null, '第一次顯示不閃');
  assert.equal(FX.launchFx('running', 'running', null), null);
  assert.equal(FX.launchFx('paused', 'running', null), null, '繼續不閃');
  assert.equal(FX.launchFx('running', 'building', null), null, '重新建置不閃');
  assert.equal(FX.launchFx('running', 'stopping', null), null);
});

test('fxrules：卡片有了結果——允許與回答綠、拒絕紅、略過與繼續規劃琥珀；失效、沒從等待中變過來、其他種類不閃', () => {
  assert.equal(FX.cardSettleFx('Approval', 'Waiting', { status: 'Done', decision: 'allow' }), 'fx-c-ok');
  assert.equal(FX.cardSettleFx('Approval', 'Waiting', { status: 'Done', decision: 'allowSession' }), 'fx-c-ok');
  assert.equal(FX.cardSettleFx('Approval', 'Waiting', { status: 'Declined', decision: 'deny' }), 'fx-c-no');
  assert.equal(FX.cardSettleFx('Approval', 'Waiting', { status: 'Declined' }), 'fx-c-no', '沒有 decision 的拒絕');
  assert.equal(FX.cardSettleFx('Question', 'Waiting', { status: 'Done', decision: 'answered' }), 'fx-c-ok');
  assert.equal(FX.cardSettleFx('Question', 'Waiting', { status: 'Declined', decision: 'skip' }), 'fx-c-skip');
  assert.equal(FX.cardSettleFx('PlanApproval', 'Waiting', { status: 'Done', decision: 'acceptEdits' }), 'fx-c-ok');
  assert.equal(FX.cardSettleFx('PlanApproval', 'Waiting', { status: 'Done', decision: 'manual' }), 'fx-c-ok');
  assert.equal(FX.cardSettleFx('PlanApproval', 'Waiting', { status: 'Declined', decision: 'keepPlanning' }), 'fx-c-skip');
  assert.equal(FX.cardSettleFx('PlanApproval', 'Waiting', { status: 'Done' }), null, '不認得的決定不閃');
  for (const k of ['Question', 'Approval', 'PlanApproval']) {
    assert.equal(FX.cardSettleFx(k, 'Waiting', { status: 'Cancelled' }), null, k + '：失效不閃');
    assert.equal(FX.cardSettleFx(k, 'Waiting', { status: 'Waiting' }), null, k + '：還在等');
    assert.equal(FX.cardSettleFx(k, undefined, { status: 'Done', decision: 'allow' }), null, k + '：載入時就有結果');
    assert.equal(FX.cardSettleFx(k, 'Done', { status: 'Done', decision: 'allow' }), null, k + '：重畫');
  }
  assert.equal(FX.cardSettleFx('Tool', 'Waiting', { status: 'Done' }), null);
  assert.equal(FX.cardSettleFx('Approval', 'Waiting', null), null);
});

test('fxrules：側欄新出現的列——上一次沒有列時不做，最多 LIST_ENTRANCE_MAX 列', () => {
  assert.equal(FX.listEntranceCount(0, 30), 0, '剛啟動的第一批');
  assert.equal(FX.listEntranceCount(undefined, 3), 0);
  assert.equal(FX.listEntranceCount(5, 0), 0);
  assert.equal(FX.listEntranceCount(5, 1), 1, '新對話');
  assert.equal(FX.listEntranceCount(1, 40), FX.LIST_ENTRANCE_MAX, '清除搜尋，一大批回來');
  assert.equal(FX.LIST_ENTRANCE_MAX, 12);
});

test('fxrules：徽章的「字＋數字＋字」拆成三段，沒有數字或兩段數字時不拆', () => {
  assert.deepEqual(FX.splitCount('↑3'), { pre: '↑', n: 3, post: '' });
  assert.deepEqual(FX.splitCount('↓12+'), { pre: '↓', n: 12, post: '+' });
  assert.deepEqual(FX.splitCount('●5'), { pre: '●', n: 5, post: '' });
  assert.deepEqual(FX.splitCount('未合併 4'), { pre: '未合併 ', n: 4, post: '' });
  assert.deepEqual(FX.splitCount('7'), { pre: '', n: 7, post: '' });
  for (const t of ['↓+', '上游已刪除', '', '1 和 2', null, undefined, 3]) assert.equal(FX.splitCount(t), null, String(t));
});

test('fxrules：自動命名產生的新標題才解碼——換之前或換之後在產生標題；手動改名、第一次顯示、標題沒變不解碼', () => {
  assert.equal(FX.titleDecodeFx({ title: '新對話', titling: true }, { title: 'Codex 用量欄位解析', titling: false }), true);
  assert.equal(FX.titleDecodeFx({ title: '新對話', titling: true }, { title: 'Codex 用量欄位解析', titling: true }), true, '標題先到、titling 之後才清');
  assert.equal(FX.titleDecodeFx({ title: '舊', titling: false }, { title: '新', titling: true }), true);
  assert.equal(FX.titleDecodeFx({ title: '舊', titling: false }, { title: '新', titling: false }), false, '手動改名、同步來的改名');
  assert.equal(FX.titleDecodeFx(null, { title: '新', titling: false }), false, '第一次顯示');
  assert.equal(FX.titleDecodeFx(undefined, { title: '新', titling: true }), false);
  assert.equal(FX.titleDecodeFx({ title: '同', titling: true }, { title: '同', titling: false }), false, '產生完標題沒變');
  assert.equal(FX.titleDecodeFx({ title: '舊', titling: true }, { title: '', titling: false }), false, '空標題');
  assert.equal(FX.titleDecodeFx({ title: '舊', titling: true }, { titling: false }), false);
});

test('fxrules：標題解碼的時序——由左到右逐字鎖定、長標題擠在上限內、空白不亂碼、碼位為單位、真的字一直在', () => {
  const zero = () => 0;
  assert.equal(FX.titleLockAt(0, 10), FX.TITLE_DECODE_BASE_MS);
  assert.equal(FX.titleLockAt(1, 10) - FX.titleLockAt(0, 10), FX.TITLE_STAGGER_MS, '短標題每個字錯開上限');
  assert.ok(FX.titleLockAt(59, 60) <= FX.TITLE_DECODE_MAX_MS + 1e-9, '長標題最後一個字不超過上限');
  assert.equal(FX.titleLockAt(0, 1), FX.TITLE_DECODE_BASE_MS);
  assert.equal(FX.titleDecodeDuration(''), 0);
  assert.equal(FX.titleDecodeDuration('ab'), FX.titleLockAt(1, 2) + FX.TITLE_LOCK_MS);
  const t = 'Fix 用量 bug';
  const start = FX.titleDecodeCells(t, 0, zero);
  assert.deepEqual(start.map((c) => c.ch).join(''), t, '真的字一直在');
  assert.deepEqual(start.map((c) => c.state), ['scramble', 'scramble', 'scramble', 'done', 'scramble', 'scramble', 'done', 'scramble', 'scramble', 'scramble'], '空白不亂碼');
  assert.ok(start.filter((c) => c.state === 'scramble').every((c) => typeof c.g === 'string' && c.g.length === 1));
  assert.ok(start.filter((c) => c.state !== 'scramble').every((c) => c.g === null));
  const mid = FX.titleDecodeCells(t, FX.titleLockAt(4, 10), zero).map((c) => c.state);
  assert.deepEqual(mid.slice(0, 5), ['lock', 'lock', 'lock', 'done', 'lock'], '鎖到第 5 個字');
  assert.deepEqual(mid.slice(5), ['scramble', 'done', 'scramble', 'scramble', 'scramble']);
  const end = FX.titleDecodeCells(t, FX.titleDecodeDuration(t), zero);
  assert.ok(end.every((c) => c.state === 'done'), '播完全部鎖定');
  const emoji = FX.titleDecodeCells('a😀b', 0, zero);
  assert.equal(emoji.length, 3, '以碼位算，不拆代理字對');
  assert.equal(emoji[1].ch, '😀');
  assert.deepEqual(FX.titleDecodeCells('', 0, zero), []);
});


test('fxrules：過載與網路瞬斷自動重試的倒數——從提示文字解析秒數（預設 60 秒、網路斷線 10 秒）、比例 0～1；快到時間才開始執行是重試開始了，更早是取消；時間不明不畫', () => {
  assert.equal(FX.RETRY_DELAY_MS, 60_000);
  assert.equal(FX.parseRetryDelayMs('API 網路連線中斷，10 秒後自動重試（第 1 次，最多 3 次）；在這之前送出訊息就改送你的訊息'), 10_000);
  assert.equal(FX.parseRetryDelayMs('API 伺服器過載（529 Overloaded），60 秒後自動重試（第 1 次，最多 3 次）'), 60_000);
  assert.equal(FX.parseRetryDelayMs('API 伺服器過載，2 分鐘後自動重試（第 1 次）'), 120_000);
  assert.equal(FX.parseRetryDelayMs('沒有秒數的一般提示'), 60_000);
  assert.equal(FX.parseRetryDelayMs(''), 60_000);
  assert.deepEqual(FX.retryCountdown(1_000, 31_000), { remaining: 30_000, frac: 0.5 });
  assert.deepEqual(FX.retryCountdown(1_000, 6_000, 10_000), { remaining: 5_000, frac: 0.5 }, '10 秒重試過一半');
  assert.deepEqual(FX.retryCountdown(1_000, 1_000), { remaining: 60_000, frac: 1 });
  assert.deepEqual(FX.retryCountdown(1_000, 99_000), { remaining: 0, frac: 0 }, '已經過了時間');
  assert.deepEqual(FX.retryCountdown(1_000, 0), { remaining: 60_000, frac: 1 }, '時鐘比提示早（另一台電腦）：不超過 60 秒');
  for (const t of [0, -1, NaN, undefined, null]) assert.equal(FX.retryCountdown(t, 5), null, String(t));
  assert.equal(FX.retryOutcome(1_000, 61_000), 'go', '時間到了才開始');
  assert.equal(FX.retryOutcome(1_000, 61_000 - FX.RETRY_GO_SLACK_MS), 'go', '差幾秒（計時器排隊）也算');
  assert.equal(FX.retryOutcome(1_000, 11_000, 10_000), 'go', '10 秒重試時間到了');
  assert.equal(FX.retryOutcome(1_000, 4_000, 10_000), 'cancel', '10 秒重試期間使用者提早送出');
  assert.equal(FX.retryOutcome(1_000, 20_000), 'cancel', '使用者提早送出');
  assert.equal(FX.retryOutcome(0, 20_000), null);
});

test('fxrules：用量的 5 格刻度——每 20% 一格無條件進位、有用量至少一格、超過 100% 算滿', () => {
  assert.deepEqual([0, 0.5, 1, 20, 21, 34, 63, 80, 99.9, 100, 140].map((p) => FX.meterCells(p)), [0, 1, 1, 1, 2, 2, 4, 4, 5, 5, 5]);
  assert.deepEqual([null, undefined, 'x', -3, NaN].map((p) => FX.meterCells(p)), [0, 0, 0, 0, 0]);
  assert.equal(FX.meterCells(50, 10), 5);
});

test('fxrules：輸入框下方那一行的用量燈號——主要時間窗加第一個每週的；主要的就是每週時只畫它', () => {
  const w = (key, label) => ({ key, label, percent: 10 });
  const keys = (list) => FX.infoWindows(list).map((x) => x.key);
  // Claude：5 小時、每週、每週・模型 → 5 小時與每週（不是模型的）
  assert.deepEqual(keys([w('session', '5 小時'), w('weekly', '每週'), w('weekly:Fable', '每週・Fable')]), ['session', 'weekly']);
  assert.deepEqual(keys([w('session', '5 小時'), w('weekly:Opus', '每週・Opus')]), ['session', 'weekly:Opus'], '只有模型的每週也算每週');
  // Codex：key 帶分鐘數
  assert.deepEqual(keys([w('codex:300', '5 小時'), w('codex:10080', '每週')]), ['codex:300', 'codex:10080']);
  assert.deepEqual(keys([w('codex:10080', '每週')]), ['codex:10080'], '只有每週');
  assert.deepEqual(keys([w('codex:primary', '每週'), w('other:10080', '每週・Other')]), ['codex:primary'], '主要的就是每週：不再加第二個每週');
  // Antigravity：兩個群組各有 5 小時與每週 → 第一個 5 小時與第一個每週
  assert.deepEqual(keys([w('agy-gemini-5h', '5 小時・Gemini'), w('agy-gemini-weekly', '每週・Gemini'), w('agy-3p-5h', '5 小時・Claude/GPT'), w('agy-3p-weekly', '每週・Claude/GPT')]),
    ['agy-gemini-5h', 'agy-gemini-weekly']);
  // 認不得長度的主要時間窗照樣畫；沒有每週就只有它
  assert.deepEqual(keys([w('grok-credits', '點數'), w('session', '5 小時')]), ['grok-credits']);
  for (const bad of [null, undefined, [], 'x', [null, 3]]) assert.deepEqual(FX.infoWindows(bad), [], String(bad));
});

test('fxrules：輸出速度的寫法不寫 token 這個字——「42/s」、一千以上「1.2K/s」', () => {
  assert.equal(FX.rateLabel(0), '0/s');
  assert.equal(FX.rateLabel(42.4), '42/s');
  assert.equal(FX.rateLabel(1234), '1.2K/s');
  for (const bad of [-1, NaN, Infinity, null]) assert.equal(FX.rateLabel(bad), null, String(bad));
});

test('fxrules：用量圓環——嚴重度升高彈一下、百分比一次掉 10 點以上倒轉一圈、第一次顯示不動', () => {
  assert.equal(FX.GAUGE_RESET_DROP, 10);
  assert.equal(FX.gaugeFx('ok', 'warn', 60, 72), 'up');
  assert.equal(FX.gaugeFx('warn', 'max', 80, 97), 'up');
  assert.equal(FX.gaugeFx('high', 'warn', 90, 85), null, '嚴重度下降但只掉 5 點');
  assert.equal(FX.gaugeFx('high', 'ok', 90, 3), 'reset', '額度重置');
  assert.equal(FX.gaugeFx('ok', 'ok', 40, 30), 'reset', '剛好掉 10 點（上下文壓縮）');
  assert.equal(FX.gaugeFx('ok', 'ok', 40, 31), null);
  assert.equal(FX.gaugeFx(undefined, 'max', undefined, 99), null, '第一次顯示');
  assert.equal(FX.gaugeFx(null, 'warn', null, 70), null);
  assert.equal(FX.gaugeFx('ok', 'bogus', 10, 12), null, '認不得的嚴重度當 ok');
  assert.equal(FX.gaugeFx('ok', 'ok', NaN, 0), null);
});

test('fxrules：帳戶卡的持續動畫——已登入沒在跑是怠速、在跑時執行中的對話越多越快（有上限）、沒登入不動', () => {
  assert.deepEqual(FX.accountPulse('ready', false, 0), { mode: 'idle', tempo: 1 });
  assert.deepEqual(FX.accountPulse('ready', false, 1), { mode: 'run', tempo: 1 });
  assert.deepEqual(FX.accountPulse('ready', false, 2), { mode: 'run', tempo: 1.5 });
  assert.deepEqual(FX.accountPulse('ready', false, 3), { mode: 'run', tempo: 2 });
  assert.deepEqual(FX.accountPulse('ready', false, 4), { mode: 'run', tempo: 2.5 });
  assert.deepEqual(FX.accountPulse('ready', false, 7), { mode: 'run', tempo: 4 }, '7 個就到最快');
  assert.deepEqual(FX.accountPulse('ready', false, 12), { mode: 'run', tempo: FX.PULSE_TEMPO_MAX }, '最快 4 倍');
  assert.equal(FX.PULSE_TEMPO_MAX, 4);
  for (const st of ['notLoggedIn', 'checking', 'error', 'notInstalled', 'apiKey', undefined]) {
    assert.deepEqual(FX.accountPulse(st, false, 0), { mode: 'off', tempo: 1 }, st + ' 沒有連上：不動');
  }
  assert.deepEqual(FX.accountPulse('ready', true, 0), { mode: 'off', tempo: 1 }, '只在其他電腦登入（remoteOnly）不算這台連上');
  assert.deepEqual(FX.accountPulse('ready', true, 2), { mode: 'run', tempo: 1.5 }, '其他電腦在跑照樣是執行中');
  assert.deepEqual(FX.accountPulse('notLoggedIn', false, 1), { mode: 'run', tempo: 1 }, '這台沒登入、其他電腦在跑');
  assert.deepEqual(FX.accountPulse('ready', false, -3), { mode: 'idle', tempo: 1 }, '負數當 0');
  assert.deepEqual(FX.accountPulse('ready', false, '2'), { mode: 'run', tempo: 1.5 });
  assert.deepEqual(FX.accountPulse('ready', false, NaN), { mode: 'idle', tempo: 1 });
});

test('fxrules：怠速動畫的相位——依帳戶 id 固定、0～1、各帳戶錯開', () => {
  const ids = ['a1', 'a2', 'a3', 'a4', 'a5', 'a6', 'local-antigravity', '3f2a9c1e-0000-4000-8000-000000000000'];
  const ph = ids.map((id) => FX.pulsePhase(id));
  for (const p of ph) assert.ok(p >= 0 && p < 1, String(p));
  assert.equal(FX.pulsePhase('a1'), FX.pulsePhase('a1'), '同一個帳戶（大卡與小卡）相同');
  assert.equal(new Set(ph).size, ph.length, '各帳戶不同');
  assert.equal(FX.pulsePhase(null), FX.pulsePhase(''));
});

// ───── 輸入框的上下文用量提示（contextrules.js；Header.context）─────
test('contextrules：要不要顯示、百分比的字、嚴重度', () => {
  const claude = { used: 412_380, window: 1_000_000, percent: 41.2, autoCompactAt: 967_000, severity: 'ok' };
  assert.equal(CX.contextShown(claude), true);
  assert.equal(CX.contextShown(null), false, '宿主沒給（Antigravity、瀏覽器版、副本）');
  assert.equal(CX.contextShown(undefined), false);
  assert.equal(CX.contextShown({ used: 10, window: 0 }), false, '視窗不明');
  assert.equal(CX.contextShown({ used: null, window: 200_000 }), false, '壓縮後還沒有呼叫');
  assert.equal(CX.contextShown({ used: 0, window: 200_000 }), true);

  assert.equal(CX.contextLabel(claude), '41%');
  assert.equal(CX.contextLabel({ percent: 0.2 }), '<1%', '不到 1% 不寫成 0%');
  assert.equal(CX.contextLabel({ percent: 0 }), '0%');
  assert.equal(CX.contextLabel({ percent: 99.6 }), '100%');
  assert.equal(CX.contextLabel({ percent: 130 }), '100%', '封頂');
  assert.equal(CX.contextPercent({ percent: 'x' }), 0);
  assert.equal(CX.contextPercent({ percent: -3 }), 0);

  assert.equal(CX.contextSeverity({ severity: 'max' }), 'max');
  assert.equal(CX.contextSeverity({ severity: 'weird' }), 'ok', '不認得的當 ok');
  assert.equal(CX.contextSeverity(null), 'ok');
});

test('contextrules：自動壓縮門檻的刻度與滑過的說明', () => {
  const claude = { used: 412_380, window: 1_000_000, percent: 41.2, autoCompactAt: 967_000, severity: 'ok' };
  assert.equal(CX.contextMark(claude), 96.7);
  assert.equal(CX.contextMark({ window: 258_400, autoCompactAt: null }), null, 'Codex、Grok 沒有門檻');
  assert.equal(CX.contextMark({ window: 100_000, autoCompactAt: 167_000 }), null, '門檻不在視窗內');

  assert.equal(CX.contextSummary(claude), '上下文：412,380／1,000,000 tokens（41%）');
  const lines = CX.contextTitle(claude, 'Claude').split('\n');
  assert.deepEqual(lines, ['上下文：412,380／1,000,000 tokens（41%）', '自動壓縮門檻 967,000 tokens，還有 554,620', '最近一次模型呼叫的輸入（含快取）加輸出']);

  const past = CX.contextTitle({ used: 970_000, window: 1_000_000, percent: 97, autoCompactAt: 967_000, severity: 'max' }, 'Claude');
  assert.match(past, /已到自動壓縮門檻（967,000 tokens）/);
  assert.match(past, /可以送 \/compact 壓縮較早的內容，或開新對話/, 'Claude 與 Codex 都支援 /compact');

  const codex = CX.contextTitle({ used: 230_000, window: 258_400, percent: 89, autoCompactAt: null, severity: 'high' }, 'Codex');
  assert.doesNotMatch(codex, /自動壓縮門檻/);
  assert.match(codex, /可以送 \/compact 壓縮較早的內容，或開新對話/);
  assert.doesNotMatch(CX.contextTitle({ used: 150_000, window: 200_000, percent: 75, severity: 'warn' }, 'Grok'), /快滿了/, 'warn 還不建議');
});

test('視窗標題：app 名稱在最前面、後面才接目前對話的標題；儀表板、草稿、沒有對話（titlerules.js）', () => {
  const shell = readFileSync(path.join(repo, 'src', 'Myriagent', 'UI', 'ShellRules.cs'), 'utf8');
  assert.ok(shell.includes('AppTitle = "' + TT.APP_TITLE + '"'), '同宿主的 ShellRules.AppTitle');
  assert.equal(TT.pageTitle('chat', { title: '修正登入流程' }), 'Clode Myriagent － 修正登入流程');
  assert.equal(TT.pageTitle('chat', { title: '  修正登入流程 ' }), 'Clode Myriagent － 修正登入流程');
  assert.equal(TT.pageTitle('dashboard', { title: '修正登入流程' }), 'Clode Myriagent － 儀表板', '儀表板不看對話');
  assert.equal(TT.pageTitle('chat', { isDraft: true, title: '' }), 'Clode Myriagent － 新對話');
  assert.equal(TT.pageTitle('chat', { title: '   ' }), 'Clode Myriagent');
  assert.equal(TT.pageTitle('chat', { title: null }), 'Clode Myriagent');
  assert.equal(TT.pageTitle('chat', null), 'Clode Myriagent', '沒有開著的對話');
  assert.equal(TT.pageTitle(undefined, undefined), 'Clode Myriagent');
});

// ───── 第七批視覺效果（fxrules.js、titlerules.js）─────
test('fxrules：結算單的時間寫法——秒、分秒、時分；首次回應十秒以下寫到小數一位', () => {
  assert.equal(FX.durationLabel(14_000), '14 秒');
  assert.equal(FX.durationLabel(134_000), '2 分 14 秒');
  assert.equal(FX.durationLabel(120_000), '2 分');
  assert.equal(FX.durationLabel(3_720_000), '1 小時 2 分');
  assert.equal(FX.durationLabel(-1), '');
  assert.equal(FX.secondsLabel(1830), '1.8 秒');
  assert.equal(FX.secondsLabel(12_400), '12 秒');
  assert.equal(FX.tokShort(842), '842');
  assert.equal(FX.tokShort(2100), '2.1K');
  assert.equal(FX.tokShort(18_249), '18.2K');
  assert.equal(FX.tokShort(412_000), '412K');
  assert.equal(FX.tokShort(1_200_000), '1.2M');
});

test('fxrules：這一輪的項目——停在開始這一輪的使用者訊息，中途插入的使用者訊息也算這一輪；不知道開始時間時停在最後一則', () => {
  const list = [
    { id: 'u0', kind: 'User', time: 0 }, { id: 'a0', kind: 'Assistant', time: 10 },
    { id: 'u1', kind: 'User', time: 99_000 }, { id: 't1', kind: 'Tool', time: 101_000 },
    { id: 'u2', kind: 'User', time: 105_000 }, { id: 'a1', kind: 'Assistant', time: 106_000 },
  ];
  const get = (id) => list.find((x) => x.id === id);
  const order = list.map((x) => x.id);
  assert.deepEqual(FX.turnItems(order, get, 100_000).map((x) => x.id), ['t1', 'u2', 'a1']);
  assert.deepEqual(FX.turnItems(order, get, null).map((x) => x.id), ['a1']);
});

test('fxrules：結算單——狀態、花了多久、工具次數、改了幾個檔案（同檔只算一次）、token、首次回應；只有狀態時不浮出', () => {
  const items = [
    { id: 'u', kind: 'User', time: 0 }, { id: 'k', kind: 'Thinking', time: 1800 },
    { id: 't1', kind: 'Tool', time: 2000, filePath: 'a.cs', diffStats: { add: 3, del: 1 } },
    { id: 't2', kind: 'Tool', time: 3000, filePath: 'a.cs', diffStats: { add: 2, del: 0 } },
    { id: 't3', kind: 'Tool', time: 4000, filePath: 'b.cs', diffStats: { add: 0, del: 4 } },
    { id: 't4', kind: 'Tool', time: 5000 },
  ];
  const parts = FX.turnSummary({ state: 'ok', startedAt: 0, endedAt: 134_000, items, tokens: { input: 12_345, output: 2100 } });
  assert.deepEqual(parts, [
    { k: 'state', text: '完成' }, { k: 'dur', text: '2 分 14 秒' }, { k: 'tools', text: '工具 4 次' },
    { k: 'files', text: '2 個檔案', add: 5, del: 5 }, { k: 'tok', text: '輸入 12.3K・輸出 2.1K' }, { k: 'ttft', text: '首次回應 1.8 秒' },
  ]);
  assert.equal(FX.turnSummary({ state: 'err', items: [] }), null);
  assert.equal(FX.turnSummary({ state: 'stop', startedAt: 0, endedAt: 5000, items: [] })[0].text, '已停止');
  assert.equal(FX.turnSummary({ state: 'bg', startedAt: 0, endedAt: 5000, items: [] })[0].text, '等背景工作');
  // 開始時間不明：不寫花了多久與首次回應；token 是 0 不寫
  assert.deepEqual(FX.turnSummary({ state: 'ok', startedAt: null, endedAt: 9, items, tokens: { input: 0, output: 0 } }).map((p) => p.k), ['state', 'tools', 'files']);
});

test('fxrules：思考的耗時——以下一個項目的時間推算；下一個是使用者訊息、倒退或太久不寫；不到一秒另寫', () => {
  assert.equal(FX.thinkDuration({ time: 1000 }, { kind: 'Assistant', time: 19_000 }), 18_000);
  assert.equal(FX.thinkDuration({ time: 1000 }, { kind: 'User', time: 19_000 }), null);
  assert.equal(FX.thinkDuration({ time: 1000 }, { kind: 'Tool', time: 500 }), null);
  assert.equal(FX.thinkDuration({ time: 0 }, { kind: 'Tool', time: FX.THINK_MAX_MS + 1 }), null);
  assert.equal(FX.thinkDuration({ time: 0 }, null), null);
  assert.equal(FX.thinkTimeLabel(400), '不到 1 秒');
  assert.equal(FX.thinkTimeLabel(18_000), '18 秒');
});

test('fxrules：差異的變動地圖——短的不畫、增刪各自併成區段、只看畫出來的那些行', () => {
  const lines = (n, f) => Array.from({ length: n }, (_, i) => ({ t: f(i), s: '' }));
  assert.equal(FX.diffMapRuns(lines(FX.DIFF_MAP_MIN_LINES - 1, () => 'add'), 99), null);
  assert.equal(FX.diffMapRuns(lines(60, () => 'ctx'), 60), null);
  const r = FX.diffMapRuns(lines(60, (i) => (i < 5 ? 'del' : i > 50 ? 'add' : 'ctx')), 60);
  assert.deepEqual(r, { add: [[85, 100]], del: [[0, 8.33]] });
  // 只看前 n 行：後面的新增不算
  assert.deepEqual(FX.diffMapRuns(lines(60, (i) => (i < 5 ? 'del' : i > 50 ? 'add' : 'ctx')), 40), { add: [], del: [[0, 12.5]] });
  // 很長的差異分成 DIFF_MAP_BUCKETS 格：每一格至少一行
  const long = FX.diffMapRuns(lines(1000, (i) => (i === 999 ? 'add' : 'ctx')), 1000);
  assert.deepEqual(long.add, [[99.17, 100]]);
  assert.equal(FX.diffMapGradient([[0, 8.33]], 'red'), 'linear-gradient(to bottom, red 0% 8.33%, transparent 8.33% 100%)');
  assert.equal(FX.diffMapGradient([], 'red'), 'linear-gradient(to bottom, transparent 0% 100%)');
});

test('fxrules：上傳讀數——大小、速度；單位相同時前面不寫單位；總大小不明不寫', () => {
  const MB = 1048576;
  assert.equal(FX.uploadReadout(3.2 * MB, 12 * MB, 1.1 * MB), '3.2／12 MB・1.1 MB/s');
  assert.equal(FX.uploadReadout(500 * 1024, 12 * MB, null), '500 KB／12 MB');
  assert.equal(FX.uploadReadout(20 * MB, 12 * MB, 0), '12／12 MB');
  assert.equal(FX.uploadReadout(1, 0, 1), null);
  assert.equal(FX.smoothRate(null, 100), 100);
  assert.equal(FX.smoothRate(100, 200), 140);
  assert.equal(FX.smoothRate(100, null), 100);
});

test('fxrules：Codex 的免費重置次數——不知道回 null；0 次也列；到期時間只在有次數、還沒到時給', () => {
  const now = 10 * 3600e3;
  assert.equal(FX.resetCreditsInfo(null, now), null);
  assert.equal(FX.resetCreditsInfo({ credits: '12' }, now), null, '沒有欄位（Claude、舊版的電腦）');
  assert.equal(FX.resetCreditsInfo({ resetCredits: null }, now), null);
  assert.equal(FX.resetCreditsInfo({ resetCredits: -1 }, now), null);
  assert.equal(FX.resetCreditsInfo({ resetCredits: 1.5 }, now), null);
  assert.deepEqual(FX.resetCreditsInfo({ resetCredits: 0, resetCreditExpiresAt: now + 1 }, now), { count: 0, expiresAt: null });
  assert.deepEqual(FX.resetCreditsInfo({ resetCredits: 2, resetCreditExpiresAt: now + 1 }, now), { count: 2, expiresAt: now + 1 });
  assert.deepEqual(FX.resetCreditsInfo({ resetCredits: 2, resetCreditExpiresAt: now }, now), { count: 2, expiresAt: null }, '已經到期');
  assert.deepEqual(FX.resetCreditsInfo({ resetCredits: 1, resetCreditExpiresAt: null }, now), { count: 1, expiresAt: null }, '不會到期');
});

test('fxrules：「使用重置」只給這台已登入、還有次數的 Codex 帳戶；瀏覽器版與其他電腦的帳戶不給', () => {
  const a = { provider: 'Codex', status: 'ready', remoteOnly: false, resetCreditBusy: false };
  const two = { count: 2, expiresAt: null };
  assert.deepEqual(FX.resetCreditAction(a, two, false), { busy: false });
  assert.deepEqual(FX.resetCreditAction({ ...a, resetCreditBusy: true }, two, false), { busy: true });
  assert.equal(FX.resetCreditAction(a, two, true), null, '瀏覽器版');
  assert.equal(FX.resetCreditAction({ ...a, remoteOnly: true }, two, false), null);
  assert.equal(FX.resetCreditAction({ ...a, status: 'notLoggedIn' }, two, false), null);
  assert.equal(FX.resetCreditAction({ ...a, provider: 'Claude' }, two, false), null);
  assert.equal(FX.resetCreditAction(a, { count: 0, expiresAt: null }, false), null, '沒有次數');
  assert.equal(FX.resetCreditAction(a, null, false), null, '不知道次數');
});

test('fxrules：用量燃燒預估——照這個週期的平均速度會比重置早用完才算；資料太少、已經用完、認不得時間窗不算', () => {
  const now = 10 * 3600e3;
  const H = 3600e3;
  // 5 小時窗，過了 3 小時、用了 70%：每小時約 23.3%，還要約 1.29 小時，比 2 小時後的重置早
  const f = FX.burnForecast({ key: 'session', percent: 70, resetsAt: now + 2 * H }, now);
  assert.ok(f && Math.abs(f.inMs - 30 * 3 * H / 70) < 1 && Math.abs(f.earlyMs - (2 * H - f.inMs)) < 1);
  assert.equal(FX.burnForecast({ key: 'session', percent: 60, resetsAt: now + 2 * H }, now), null, '剛好在重置時用完：來得及');
  assert.equal(FX.burnForecast({ key: 'session', percent: 4, resetsAt: now + 4.9 * H }, now), null, '用量太少');
  assert.equal(FX.burnForecast({ key: 'session', percent: 40, resetsAt: now + 4.8 * H }, now), null, '週期才剛開始');
  assert.equal(FX.burnForecast({ key: 'session', percent: 100, resetsAt: now + H }, now), null, '已經用完');
  assert.equal(FX.burnForecast({ key: 'other', percent: 90, resetsAt: now + H }, now), null, '認不得時間窗長度');
  assert.equal(FX.burnForecast({ key: 'session', percent: 90, resetsAt: now - 1 }, now), null, '已經過了重置時間');
});

test('fxrules：配對碼的倒數條與裝置的燈', () => {
  assert.deepEqual(FX.pairCountdown(600_000, 0, 0), { frac: 1, warn: false });
  assert.deepEqual(FX.pairCountdown(600_000, 0, 300_000), { frac: 0.5, warn: false });
  assert.deepEqual(FX.pairCountdown(600_000, 0, 570_000), { frac: 0.05, warn: true });
  assert.deepEqual(FX.pairCountdown(600_000, 0, 700_000), { frac: 0, warn: true });
  // 配對途中才第一次看到（app 重開）：照有效期算剩下的比例，不是從滿格開始
  assert.deepEqual(FX.pairCountdown(1_000_000, 700_000, 700_000), { frac: 0.5, warn: false });
  // 有效期比 600 秒長：以第一次看到到到期為總長
  assert.deepEqual(FX.pairCountdown(1_200_000, 0, 600_000), { frac: 0.5, warn: false });
  assert.equal(FX.pairCountdown(0, 0, 0), null);
  assert.equal(FX.pairCountdown(NaN, 0, 0), null);
  const now = 1_000_000;
  assert.equal(FX.deviceLed({ online: true }, now), 'on');
  assert.equal(FX.deviceLed({ online: false, lastSeenAt: now - 60_000 }, now), 'recent');
  assert.equal(FX.deviceLed({ online: false, lastSeenAt: now + 30_000 }, now), 'recent', '伺服器時鐘快一點也算');
  assert.equal(FX.deviceLed({ online: false, lastSeenAt: now - FX.DEVICE_RECENT_MS }, now), 'off');
  assert.equal(FX.deviceLed({ online: false }, now), 'off');
  assert.equal(FX.deviceLed({ online: true, revoked: { at: 1 } }, now), 'revoked');
  assert.equal(FX.deviceLed(null, now), 'off');
});

test('titlerules：瀏覽器版分頁圖示的燈——等你回應優先並寫數字（最多 9+）、執行中綠點、都沒有是 null', () => {
  assert.equal(TT.tabBadge([]), null);
  assert.equal(TT.tabBadge(null), null);
  assert.deepEqual(TT.tabBadge([{ running: true }, {}]), { kind: 'run', n: 1, text: '' });
  assert.deepEqual(TT.tabBadge([{ running: true, waiting: true }, { running: true }]), { kind: 'wait', n: 1, text: '1' });
  assert.deepEqual(TT.tabBadge(Array.from({ length: 12 }, () => ({ waiting: true }))), { kind: 'wait', n: 12, text: '9+' });
  assert.equal(TT.tabBadgeKey(null), '');
  assert.equal(TT.tabBadgeKey({ kind: 'wait', text: '3' }), 'wait:3');
});

// ───── 第八批（fxrules.js）─────
test('fxrules：待辦的分格進度照步驟順序，太多步時縮成 24 格且沒做完時留空格', () => {
  const st = (s) => ({ text: s, status: s });
  assert.deepEqual(FX.planCells([st('completed'), st('inProgress'), st('pending')]), ['done', 'run', 'todo']);
  assert.deepEqual(FX.planCells([]), []);
  assert.deepEqual(FX.planCells(null), []);
  const many = Array.from({ length: 48 }, (_, i) => ({ text: 't' + i, status: i < 47 ? 'completed' : 'pending' }));
  const cells = FX.planCells(many);
  assert.equal(cells.length, 24);
  assert.equal(cells[23], 'todo', '還有沒完成的步驟：最後一格是空的');
  const run = Array.from({ length: 30 }, (_, i) => ({ text: 't' + i, status: i === 3 ? 'inProgress' : 'pending' }));
  assert.ok(FX.planCells(run).includes('run'), '進行中至少一格');
});

test('fxrules：只有這次更新才完成的步驟閃燈，第一次畫不閃', () => {
  const prev = [{ text: 'a', status: 'completed' }, { text: 'b', status: 'inProgress' }, { text: 'c', status: 'pending' }];
  const next = [{ text: 'a', status: 'completed' }, { text: 'b', status: 'completed' }, { text: 'c', status: 'inProgress' }];
  assert.deepEqual(FX.planNewlyDone(prev, next), [1]);
  assert.deepEqual(FX.planNewlyDone(null, next), []);
  assert.deepEqual(FX.planNewlyDone([{ text: 'x', status: 'pending' }], [{ text: 'y', status: 'completed' }]), [], '文字換了不算');
});

test('fxrules：指令輸出的測試與建置總結', () => {
  const dn = '測試回合...\n已通過! - 失敗:     0，通過:    13，略過:     0，總計:    13，持續時間: 72 ms - A.dll (net10.0)\n失敗!  - 失敗:     2，通過:    11，略過:     1，總計:    14 - B.dll\n';
  assert.deepEqual(FX.commandOutcome(dn), { tests: { fail: 2, pass: 24, skip: 1 }, build: null }, '多個組件相加');
  assert.deepEqual(FX.commandOutcome('Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5').tests, { fail: 0, pass: 5, skip: 0 });
  assert.deepEqual(FX.commandOutcome('ℹ tests 19\nℹ pass 18\nℹ fail 1\nℹ skipped 0\n').tests, { pass: 18, fail: 1, skip: 0 });
  assert.deepEqual(FX.commandOutcome('===== 2 failed, 10 passed, 1 skipped in 0.42s =====\n').tests, { pass: 10, fail: 2, skip: 1 });
  assert.deepEqual(FX.commandOutcome('Tests:       1 failed, 12 passed, 13 total\n').tests, { pass: 12, fail: 1, skip: 0 });
  assert.deepEqual(FX.commandOutcome('test result: ok. 7 passed; 0 failed; 2 ignored; 0 measured\n').tests, { pass: 7, fail: 0, skip: 2 });
  assert.deepEqual(FX.commandOutcome('建置成功。\n    3 個警告\n    0 個錯誤\n').build, { errors: 0, warnings: 3 });
  assert.deepEqual(FX.commandOutcome('Build FAILED.\n    1 Warning(s)\n    2 Error(s)\n').build, { errors: 2, warnings: 1 });
  assert.equal(FX.commandOutcome('hello world'), null);
  assert.equal(FX.commandOutcome(''), null);
  assert.equal(FX.commandOutcome('x'.repeat(FX.OUTCOME_TAIL + 10) + '\nℹ pass 3\nℹ fail 0\n').tests.pass, 3, '只看結尾');
});

test('fxrules：結果晶片——測試通過與失敗、建置的錯誤警告、都沒有是建置 ✓', () => {
  assert.deepEqual(FX.outcomeChips({ tests: { pass: 13, fail: 0, skip: 0 }, build: null }).map((c) => [c.kind, c.text]), [['pass', '✓ 13']]);
  assert.deepEqual(FX.outcomeChips({ tests: { pass: 11, fail: 2, skip: 1 }, build: null }).map((c) => [c.kind, c.text]), [['pass-dim', '✓ 11'], ['fail', '✕ 2']]);
  assert.deepEqual(FX.outcomeChips({ tests: null, build: { errors: 2, warnings: 1 } }).map((c) => c.text), ['✕ 2', '! 1']);
  assert.deepEqual(FX.outcomeChips({ tests: null, build: { errors: 0, warnings: 0 } }).map((c) => c.text), ['建置 ✓']);
  assert.deepEqual(FX.outcomeChips(null), []);
});

test('fxrules：輸出裡的錯誤與警告行（位置、種類、上限）', () => {
  const out = 'ok line\r\nsrc/a.cs(3,5): error CS1002: ; expected\nwarning: unused\n  失敗 Ns.Test [12 ms]\nnormal\n';
  const r = FX.diagLineRanges(out);
  assert.deepEqual(r.map((x) => [x.kind, out.slice(x.start, x.end)]), [
    ['err', 'src/a.cs(3,5): error CS1002: ; expected'], ['warn', 'warning: unused'], ['err', '  失敗 Ns.Test [12 ms]']]);
  assert.equal(FX.diagLineRanges('error: x\n'.repeat(FX.DIAG_MARK_MAX + 50)).length, FX.DIAG_MARK_MAX);
  assert.deepEqual(FX.diagLineRanges('the error message was fine\n'), [], '句子中間的 error 不算');
  assert.deepEqual(FX.diagLineRanges(''), []);
});

test('fxrules：搜尋命中數與網頁的網域', () => {
  const tool = (o) => Object.assign({ kind: 'Tool', status: 'Done' }, o);
  assert.deepEqual(FX.toolHits(tool({ toolKind: 'search', toolName: '搜尋檔案', output: 'Found 23 files\na\nb' })), { kind: 'hits', text: '23 個檔案', title: '找到 23 個檔案' });
  assert.equal(FX.toolHits(tool({ toolKind: 'search', toolName: '搜尋內容', output: 'No matches found' })).kind, 'none');
  assert.equal(FX.toolHits(tool({ toolKind: 'search', toolName: '搜尋檔案', output: 'C:/a.cs\nC:/b.cs\n\n' })).text, '2 個檔案');
  assert.equal(FX.toolHits(tool({ toolKind: 'search', toolName: '搜尋內容', status: 'Running', output: 'Found 1 file' })), null, '執行中不算');
  assert.deepEqual(FX.toolHits(tool({ toolKind: 'web', toolName: '讀取網頁', title: 'https://www.example.com/a?b=1' })), { kind: 'host', text: 'example.com', title: '網站：example.com' });
  const ws = FX.toolHits(tool({ toolKind: 'web', toolName: '網路搜尋', title: 'q', output: 'Web search results\n\nLinks: [{"title":"A","url":"https://a.dev/x"},{"title":"B","url":"https://b.io"}]\n' }));
  assert.deepEqual(ws, { kind: 'hits', text: '2 個結果', title: 'a.dev、b.io' });
  assert.equal(FX.urlHost('ftp://x'), null);
  assert.equal(FX.urlHost('https://user@host.tw:8080/p'), 'host.tw');
});

test('fxrules：子代理的步數、錯誤與目前動作', () => {
  const run = '▸ 讀取檔案：a.cs\n💭 思考：嗯\n  ✗ 找不到\n▸ 搜尋內容：foo\n';
  assert.deepEqual(FX.subagentProgress(run, true), { steps: 4, more: false, errors: 1, current: '搜尋內容：foo' });
  assert.equal(FX.subagentProgress(run, false).current, null);
  assert.equal(FX.subagentProgress('…（前 300 字省略）\n▸ a\n', true).more, true);
  const done = '── 子代理執行紀錄（共 57 項步驟，只列最後 3 項）──\n▸ a\n▸ b\n💬 c\n\n── 子代理結論 ──\n▸ 這不是過程\n';
  assert.deepEqual(FX.subagentProgress(done, false), { steps: 57, more: false, errors: 0, current: null });
  assert.equal(FX.subagentProgress('只有結論', false), null);
});

test('fxrules：每一輪的分隔線寫前一段的時間、工具與檔案，第一則不寫', () => {
  const T = 1_000_000;
  const items = [
    { id: 'u1', kind: 'User', time: T },
    { id: 'a1', kind: 'Tool', time: T + 2000, filePath: 'a.cs', diffStats: { add: 3, del: 1 } },
    { id: 'a2', kind: 'Assistant', time: T + 134_000 },
    { id: 'u2', kind: 'User', time: T + 200_000 },
    { id: 'e1', kind: 'Error', time: T + 201_000 },
    { id: 'u3', kind: 'User', time: T + 300_000 },
    { id: 'u4', kind: 'User', time: T + 400_000 },
  ];
  const r = FX.turnRules(items);
  assert.deepEqual(r.map((x) => x.id), ['u2', 'u3'], '第一則與前面沒有回覆的不寫');
  assert.deepEqual(r[0].parts.map((p) => p.k), ['dur', 'tools', 'files']);
  assert.equal(r[0].parts[0].text, '2 分 14 秒');
  assert.deepEqual(r[1].parts.map((p) => p.k), ['state', 'dur'], '失敗照寫');
  assert.deepEqual(FX.turnRules(null), []);
});

test('fxrules：滑桿的已選比例與刻度格數', () => {
  assert.equal(FX.rangeFill(14, 12, 18), 33.3);
  assert.equal(FX.rangeFill(30, 12, 18), 100);
  assert.equal(FX.rangeFill(1, 2, 2), 0);
  assert.equal(FX.rangeTicks(12, 18, 1), 6);
  assert.equal(FX.rangeTicks(0.5, 2, 0.05), 15, '30 步合成 15 格');
  assert.equal(FX.rangeTicks(-12, 12, 1), 24);
  assert.equal(FX.rangeTicks(0, 1, 0), 1);
});

test('第九批：myriagent-remote 的工具卡（電腦名、結束碼的意義）', () => {
  assert.equal(FX.exitCodeOf('Exit code 124\n[myriagent-remote] 逾時'), 124);
  assert.equal(FX.exitCodeOf('Error: Exit code 2\n'), 2);
  assert.equal(FX.exitCodeOf('ok\nExit code 1'), null, '只認開頭');
  assert.equal(FX.exitCodeOf(null), null);
  assert.deepEqual(FX.remoteExecOf('myriagent-remote run 書房桌機 -- Get-Date', ''), { op: 'run', device: '書房桌機', code: null });
  assert.deepEqual(FX.remoteExecOf('myriagent-remote run --shell cmd --cwd "C:\\a b" --timeout 30 "Lab PC" -- dir', 'Exit code 126\n'), { op: 'run', device: 'Lab PC', code: 126 });
  assert.equal(FX.remoteExecOf('& "C:\\Program Files\\Myriagent\\myriagent-remote.exe" run pc2 -- whoami', '').device, 'pc2');
  assert.equal(FX.remoteExecOf('cd x && myriagent-remote run pc -- echo', '[myriagent-remote] 已送到「書房的桌機」\nhi').device, '書房的桌機', '以那台回報的名稱為準');
  assert.deepEqual(FX.remoteExecOf('myriagent-remote list --json', ''), { op: 'list', device: null, code: null });
  assert.equal(FX.remoteExecOf('echo myriagent-remoteX run a', ''), null);
  assert.equal(FX.remoteExecOf('git status', ''), null);
  assert.deepEqual(FX.remoteExecChips({ op: 'run', device: 'pc', code: null }).map((c) => c.kind + ':' + c.text), ['remote:遠端：pc']);
  assert.deepEqual(FX.remoteExecChips({ op: 'run', device: 'pc', code: 124 }).map((c) => c.kind + ':' + c.text), ['remote:遠端：pc', 'fail:124 逾時']);
  assert.deepEqual(FX.remoteExecChips({ op: 'run', device: 'pc', code: 130 }).map((c) => c.kind + ':' + c.text), ['remote:遠端：pc', 'warn:130 已停止']);
  assert.deepEqual(FX.remoteExecChips({ op: 'run', device: 'pc', code: 1 }).length, 1, '那台程式自己的結束碼不另外解釋');
  assert.match(FX.remoteExecChips({ op: 'run', device: 'pc', code: 125 })[1].title, /^myriagent-remote 結束碼 125：沒有送出或被拒/);
  assert.deepEqual(FX.remoteExecChips({ op: 'list', device: null, code: 255 }).map((c) => c.text), ['遠端：列出電腦', '255 結果不明']);
  assert.deepEqual(FX.remoteExecChips(null), []);
  assert.deepEqual(Object.keys(FX.REMOTE_EXIT).map(Number), [2, 124, 125, 126, 130, 255]);
});

test('第九批：@ 委派帳戶選單的用量刻度與執行中', () => {
  const wins = [{ key: 'five_hour', label: '5 小時', percent: 34, severity: 'ok' }, { key: 'seven_day', label: '每週', percent: 81, severity: 'warn' }];
  const m = FX.delegateMeta(wins, 2);
  assert.deepEqual(m.meters, [{ sev: 'ok', lit: 2, tip: '5 小時 34%' }, { sev: 'warn', lit: 5, tip: '每週 81%' }]);
  assert.equal(m.running, 2);
  assert.equal(m.title, '5 小時 34%\n每週 81%\n2 個對話執行中');
  assert.deepEqual(FX.delegateMeta(null, 0), { meters: [], running: 0, title: '' });
  assert.equal(FX.delegateMeta([], -1).running, 0);
});

// ───── 第十批：MCP 工具卡與帳戶委派（fxrules.mcpToolOf、delegationOf、delegateChild、delegationChips） ─────
test('fxrules：MCP 工具卡的伺服器與工具（Claude／Codex 的「MCP：伺服器」＋標題、Grok 的完整名稱），其他工具不算', () => {
  assert.deepEqual(FX.mcpToolOf({ toolKind: 'mcp', toolName: 'MCP：github', title: 'create_issue' }), { server: 'github', tool: 'create_issue' });
  assert.deepEqual(FX.mcpToolOf({ toolKind: 'mcp', toolName: 'MCP：mcp__playwright__browser_click', title: '點按鈕' }), { server: 'playwright', tool: 'browser_click' });
  assert.equal(FX.mcpToolOf({ toolKind: 'command', toolName: 'MCP：x', title: 'y' }), null);
  assert.equal(FX.mcpToolOf({ toolKind: 'mcp', toolName: '其他', title: 'y' }), null);
  assert.equal(FX.mcpToolOf(null), null);
});

test('fxrules：委派工具卡取帳戶、工作說明的第一行、子對話與結果；結果被截斷也認得', () => {
  const detail = '{\n  "accountId": "acc-codex",\n  "prompt": "\\n  請檢查測試並回報\\n第二行"\n}';
  const running = FX.delegationOf({ toolKind: 'mcp', toolName: 'MCP：myriagent_accounts', title: 'delegate_task', detail, status: 'Running' });
  assert.deepEqual(running, { op: 'delegate', accountId: 'acc-codex', prompt: '請檢查測試並回報', childId: null, outcome: null });
  const out = '{"conversationId":"c_child1","accountId":"acc-codex","status":"Completed","result":"很長的結果被截';
  const done = FX.delegationOf({ toolKind: 'mcp', toolName: 'MCP：myriagent_accounts', title: 'delegate_task', detail, output: out, status: 'Done' });
  assert.equal(done.childId, 'c_child1');
  assert.equal(done.outcome, 'Completed');
  // 參數被截在跳脫中間（\u4e 這種）：去掉不完整的尾巴
  const cut = FX.delegationOf({ toolKind: 'mcp', toolName: 'MCP：myriagent_accounts', title: 'delegate_task', detail: '{"accountId":"a1","prompt":"修正登入\\u4e' });
  assert.equal(cut.prompt, '修正登入');
  const long = FX.delegationOf({ toolKind: 'mcp', toolName: 'MCP：myriagent_accounts', title: 'delegate_task', detail: '{"accountId":"a1","prompt":"' + '字'.repeat(200) + '"}' });
  assert.equal(long.prompt.length, 161);
  assert.ok(long.prompt.endsWith('…'));
  assert.deepEqual(FX.delegationOf({ toolKind: 'mcp', toolName: 'MCP：myriagent_accounts', title: 'list_accounts' }), { op: 'list', accountId: null, prompt: '', childId: null, outcome: null });
  assert.equal(FX.delegationOf({ toolKind: 'mcp', toolName: 'MCP：github', title: 'delegate_task' }), null);
  assert.equal(FX.delegationOf({ toolKind: 'mcp', toolName: 'MCP：myriagent_accounts', title: 'other' }), null);
});

test('fxrules：委派的子對話——有 id 就照 id，執行中以主對話＋帳戶找，同時有兩件就不認', () => {
  const list = [
    { id: 'p', accountId: 'acc-claude' },
    { id: 'k1', accountId: 'acc-codex', parentId: 'p', running: true },
    { id: 'k0', accountId: 'acc-codex', parentId: 'p', running: false },
    { id: 'x', accountId: 'acc-codex', parentId: 'other', running: true },
  ];
  const info = { op: 'delegate', accountId: 'acc-codex', childId: null };
  assert.equal(FX.delegateChild(list, 'p', info).id, 'k1', '已結束的舊子對話與別的主對話不算');
  assert.equal(FX.delegateChild(list, 'p', { ...info, childId: 'k0' }).id, 'k0');
  assert.equal(FX.delegateChild(list, 'p', { ...info, childId: 'gone' }), null);
  assert.equal(FX.delegateChild([...list, { id: 'k2', accountId: 'acc-codex', parentId: 'p', waiting: true }], 'p', info), null, '兩件同時在跑：認不出是哪一件');
  assert.equal(FX.delegateChild(list, 'p', { op: 'list' }), null);
  assert.equal(FX.delegateChild(null, 'p', info), null);
});

test('fxrules：委派卡的狀態晶片——執行中看子對話，結束了看結果', () => {
  const info = { op: 'delegate', outcome: null };
  assert.deepEqual(FX.delegationChips(info, { waiting: true, running: true }, 'Running').map((c) => c.kind), ['wait']);
  assert.deepEqual(FX.delegationChips(info, { running: true }, 'Running').map((c) => c.text), ['執行中']);
  assert.deepEqual(FX.delegationChips(info, null, 'Running').map((c) => c.text), ['等待回報']);
  assert.deepEqual(FX.delegationChips({ ...info, outcome: 'Completed' }, null, 'Done').map((c) => c.kind), ['pass']);
  assert.deepEqual(FX.delegationChips({ ...info, outcome: 'Interrupted' }, null, 'Failed').map((c) => c.kind), ['warn']);
  assert.deepEqual(FX.delegationChips({ ...info, outcome: 'Failed' }, null, 'Failed').map((c) => c.kind), ['fail']);
  assert.deepEqual(FX.delegationChips(info, null, 'Cancelled').map((c) => c.kind), ['warn']);
  assert.deepEqual(FX.delegationChips(info, null, 'Done'), []);
  assert.deepEqual(FX.delegationChips({ op: 'list' }, null, 'Done'), []);
});

test('fxrules：換帳戶提示的交接——各種寫法取出舊帳戶與新帳戶，等額度重置（同一個帳戶）不算', () => {
  assert.deepEqual(FX.handoffLabels('「工作用 Max」的額度已用完（10/27 14:00 重置），已自動改用「個人 Pro」（2 小時後重置）繼續這個對話；新帳戶會收到先前對話的節錄，從中斷處接著做。「工作用 Max」的額度恢復後會自動換回去。'),
    { from: '工作用 Max', to: '個人 Pro' });
  assert.deepEqual(FX.handoffLabels('原帳戶的額度已用完，已自動改用「個人 Pro」繼續這個對話'), { from: null, to: '個人 Pro' });
  assert.deepEqual(FX.handoffLabels('已從「個人 Pro」改用「Codex Pro」繼續：這一輪在中途停下，新帳戶會收到先前對話的節錄，從中斷處接著做。'), { from: '個人 Pro', to: 'Codex Pro' });
  assert.deepEqual(FX.handoffLabels('「工作用 Max」的額度已用完，這一輪在中途停下；改用你選的「個人 Pro」繼續，新帳戶會收到先前對話的節錄，從中斷處接著做。'), { from: '工作用 Max', to: '個人 Pro' });
  assert.deepEqual(FX.handoffLabels('「工作用 Max」的額度已恢復，已從「個人 Pro」換回「工作用 Max」繼續這個對話；接下來的訊息會附上先前對話的節錄，讓代理了解前情。'), { from: '個人 Pro', to: '工作用 Max' });
  assert.deepEqual(FX.handoffLabels('「工作用 Max」的額度已恢復，已從備援帳戶換回「工作用 Max」繼續這個對話'), { from: null, to: '工作用 Max' });
  assert.equal(FX.handoffLabels('「工作用 Max」的額度已重置，自動接著做'), null);
  assert.equal(FX.handoffLabels(null), null);
});

test('fxrules：每一輪的分隔線——和前一段最後一則隔了 30 分鐘以上寫在最前面，前面沒有回覆的也寫', () => {
  const T = 1_800_000_000_000;
  const MIN = 60000, HR = 3600000;
  const items = [
    { id: 'u1', kind: 'User', time: T },
    { id: 'a1', kind: 'Assistant', time: T + 5 * MIN },
    { id: 'u2', kind: 'User', time: T + 5 * MIN + 2 * HR },
    { id: 'a2', kind: 'Assistant', time: T + 5 * MIN + 2 * HR + MIN },
    { id: 'u3', kind: 'User', time: T + 5 * MIN + 2 * HR + 2 * MIN },
    { id: 'u4', kind: 'User', time: T + 5 * MIN + 2 * HR + 2 * MIN + 3 * 24 * HR },
  ];
  const r = FX.turnRules(items);
  assert.deepEqual(r.map((x) => x.id), ['u2', 'u3', 'u4']);
  assert.deepEqual(r[0].parts.map((p) => p.k), ['gap', 'dur']);
  assert.equal(r[0].parts[0].text, '隔 2 小時');
  assert.match(r[0].parts[0].title, /^前一則在 \d+\/\d+ \d\d:\d\d，這則在 \d+\/\d+ \d\d:\d\d$/);
  assert.ok(!r[1].parts.some((p) => p.k === 'gap'), '不到 30 分鐘不寫');
  assert.deepEqual(r[2].parts.map((p) => p.text), ['隔 3 天'], '前面沒有回覆、只有間隔也寫');
  assert.equal(FX.gapLabel(29 * MIN), null);
  assert.equal(FX.gapLabel(30 * MIN), '隔 30 分鐘');
  assert.equal(FX.gapLabel(95 * MIN), '隔 1.5 小時');
  assert.equal(FX.gapLabel(14.4 * HR), '隔 14 小時');
  assert.equal(FX.gapLabel(23.6 * HR), '隔 1 天');
  assert.equal(FX.gapLabel(NaN), null);
});

// ───── 第十一批視覺效果（fxrules.js、remoterules.js）─────
test('第十一批：免費重置的次數燈——最多 8 顆，再多寫 +N；不是正整數是 0', () => {
  assert.deepEqual(FX.creditPips(3), { shown: 3, more: 0 });
  assert.deepEqual(FX.creditPips(11), { shown: 8, more: 3 });
  assert.deepEqual(FX.creditPips(0), { shown: 0, more: 0 });
  assert.deepEqual(FX.creditPips(-1), { shown: 0, more: 0 });
  assert.deepEqual(FX.creditPips(1.5), { shown: 0, more: 0 });
});

test('第十一批：免費重置的到期倒數——一格一天、超過 7 天全亮、剩 1 天內 soon、過期是 null', () => {
  const now = 1_000_000_000;
  const DAY = 86_400_000;
  assert.deepEqual(FX.creditExpiryCells(now + 9 * DAY, now), { days: 9, lit: 7, soon: false });
  assert.deepEqual(FX.creditExpiryCells(now + 2.5 * DAY, now), { days: 3, lit: 3, soon: false });
  assert.deepEqual(FX.creditExpiryCells(now + 3 * 3_600_000, now), { days: 1, lit: 1, soon: true });
  assert.equal(FX.creditExpiryCells(now - 1, now), null);
  assert.equal(FX.creditExpiryCells(null, now), null);
});

test('第十一批：用掉一次免費重置——次數比上次少才算，第一次看到不算', () => {
  assert.equal(FX.creditUsed(2, 1), true);
  assert.equal(FX.creditUsed(1, 1), false);
  assert.equal(FX.creditUsed(1, 2), false);
  assert.equal(FX.creditUsed(undefined, 1), false);
  assert.equal(FX.creditUsed(null, 0), false);
});

test('第十一批：換版排空的進度——總數取看過最多的、最多畫 12 顆；都是 0 是 null', () => {
  assert.deepEqual(FX.drainProgress(3, 3), { total: 3, left: 3, shown: 3 });
  assert.deepEqual(FX.drainProgress(1, 3), { total: 3, left: 1, shown: 3 });
  assert.deepEqual(FX.drainProgress(4, 2), { total: 4, left: 4, shown: 4 }, '比看過的多：總數跟著變');
  assert.deepEqual(FX.drainProgress(20, 20), { total: 20, left: 20, shown: 12 });
  assert.deepEqual(FX.drainProgress(0, 5), { total: 5, left: 0, shown: 5 });
  assert.equal(FX.drainProgress(0, 0), null);
  assert.equal(FX.drainProgress(null, undefined), null);
});

test('第十一批：強制停下的倒數——一格一分鐘、無條件進位、最多 10 格；到了或沒有時間是 null', () => {
  const now = 5_000_000;
  assert.deepEqual(FX.forceCountdown(now + 4.5 * 60_000, now), { minutes: 5, lit: 5 });
  assert.deepEqual(FX.forceCountdown(now + FX.FORCE_STOP_MS, now), { minutes: 10, lit: 10 });
  assert.deepEqual(FX.forceCountdown(now + 30 * 60_000, now), { minutes: 30, lit: 10 });
  assert.equal(FX.forceCountdown(now, now), null);
  assert.equal(FX.forceCountdown(undefined, now), null);
});

test('第十一批：行內程式碼裡的路徑——Windows、UNC、相對、家目錄、網址切成片段、最後一段是檔名；指令與太短的不算', () => {
  assert.deepEqual(FX.pathParts('C:\\repo\\proj\\src\\AgentHub.cs'), { parts: ['C:\\', 'repo\\', 'proj\\', 'src\\', 'AgentHub.cs'], base: 4 });
  assert.equal(FX.pathParts('\\\\server\\share\\file.txt').parts.at(-1), 'file.txt');
  assert.deepEqual(FX.pathParts('src/Web/fx.css'), { parts: ['src/', 'Web/', 'fx.css'], base: 2 });
  assert.deepEqual(FX.pathParts('~/.claude/settings.json'), { parts: ['~/', '.claude/', 'settings.json'], base: 2 });
  assert.deepEqual(FX.pathParts('https://example.com/a/b.html'), { parts: ['https://example.com/', 'a/', 'b.html'], base: 2 }, '網址的協定與主機不切開');
  assert.deepEqual(FX.pathParts('tools/ui-probe/'), { parts: ['tools/', 'ui-probe/'], base: 1 }, '資料夾：最後一層加亮');
  assert.equal(FX.pathParts('npm run build'), null, '有空白：指令或句子');
  assert.equal(FX.pathParts('a/b'), null, '太短');
  assert.equal(FX.pathParts('https://example.com'), null, '只有主機');
  assert.equal(FX.pathParts('justaword.txt'), null, '沒有分隔符號');
  assert.equal(FX.pathParts(' src/Web/fx.css'), null, '前後有空白');
});

test('第十一批：子代理徽章的說明——第一行同 subagentsTitle，下面列這台的對話各幾個、其他電腦只有個數', () => {
  const a = { sessions: { subagents: 3 }, otherSessions: { subagents: 1 }, subagentList: [{ conversationId: 'c1', title: '平行查三件事', count: 2 }, { conversationId: 'c2', title: '', count: 1 }, { title: '壞的', count: 0 }] };
  assert.equal(R.subagentsTooltip(a), '4 個子代理執行中（其中其他電腦 1 個）\n・平行查三件事：2 個\n・（沒有標題）：1 個\n・其他電腦 1 個');
  assert.equal(R.subagentsTooltip({ sessions: { subagents: 0 } }), '');
  assert.equal(R.subagentsTooltip({ sessions: { subagents: 2 } }), '2 個子代理執行中', '舊版宿主沒有清單');
});
