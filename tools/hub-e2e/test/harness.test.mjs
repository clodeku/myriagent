// lib/harness-rules.mjs 的單元測試（node:test；WP4.9 步 14）：參數組裝、劇本、命令列、控制通道的一行、功能檢查、新鮮度。
// 不啟動任何程序、不開瀏覽器、不碰檔案
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  cleanupAction, controlLine, DEFAULT_DEVICES, deviceScript, FAKE_CODEX_EMAIL, missingFeatures, normalizeDevices, OWNER_PASSWORD_ENV, pairArgs, parseFullFlowArgs, parseOutputLine,
  PLANNED_SCENARIOS, REQUIRED_FEATURES, restartScript, ROLLBACK_BUILDS, rollbackBuilds, runArgs, SCENARIOS, staleOutputs, statusArgs, unpairArgs,
} from '../lib/harness-rules.mjs';

test('normalizeDevices_預設兩台_OFFICE允許完全存取_HOME不允許_種子不同', () => {
  const [office, home] = normalizeDevices();
  assert.equal(office.name, 'OFFICE');
  assert.equal(home.name, 'HOME');
  assert.deepEqual(office.policy, { level: 'Control', fullAccess: true, notify: false });
  assert.deepEqual(home.policy, { level: 'Control', fullAccess: false, notify: false });
  assert.notEqual(office.seed.seed, home.seed.seed);
  assert.equal(office.alias, 'office-claude');
  assert.equal(office.account.provider, 'Claude');
  assert.equal(office.codexAccount, null);
  assert.equal(office.fakeCodex, null);
  assert.equal(office.seed.baseTimeUtc, '2026-10-20T00:00:00Z', '時間只由 baseTimeUtc 決定（可重現）');
  assert.equal(DEFAULT_DEVICES.length, 2);
});

test('normalizeDevices_seeds與fakeCodex蓋過預設', () => {
  const [office, home] = normalizeDevices(['OFFICE', 'HOME'], { seeds: { OFFICE: { count: 300, olderThanDays: 120 } }, fakeCodex: { HOME: 'ok' } });
  assert.equal(office.seed.count, 300);
  assert.equal(office.seed.olderThanDays, 120);
  assert.equal(office.seed.seed, 20261101, '沒給的沿用預設');
  assert.equal(home.fakeCodex, 'ok');
  assert.equal(home.codexAccount.provider, 'Codex');
  assert.equal(home.codexAccount.alias, 'home-codex');
  const [other] = normalizeDevices([{ name: 'OFFICE2', seed: { count: 1, seed: 7 }, policy: { level: 'View' }, appVersion: '0.2.0' }]);
  assert.equal(other.policy.level, 'View');
  assert.equal(other.policy.fullAccess, false);
  assert.equal(other.appVersion, '0.2.0');
});

test('normalizeDevices_不合格就丟例外', () => {
  assert.throws(() => normalizeDevices([]), /至少一台/);
  assert.throws(() => normalizeDevices(['office']), /大寫/);
  assert.throws(() => normalizeDevices(['OFFICE', 'OFFICE']), /重複/);
  // 同種子的第一個對話 id 相同：後上傳的那台 not_owner（計畫步 14 第 4 點）
  assert.throws(() => normalizeDevices(['OFFICE', 'HOME'], { seeds: { HOME: { seed: 20261101 } } }), /種子相同/);
  assert.doesNotThrow(() => normalizeDevices(['OFFICE', 'HOME'], { seeds: { HOME: { seed: 20261101, count: 0 } } }), '不種對話時種子相同沒關係');
  assert.throws(() => normalizeDevices(['HOME'], { fakeCodex: { HOME: 'real' } }), /情境/);
  assert.throws(() => normalizeDevices([{ name: 'HOME', policy: { level: 'Full' } }]), /政策/);
  assert.throws(() => normalizeDevices([{ name: 'HOME', seed: { count: -1 } }]), /0～5000/);
  assert.throws(() => normalizeDevices([{ name: 'HOME', appVersion: '1 2' }]), /appVersion/);
});

test('deviceScript_種子在最前面_帳戶_政策_最後等outbox清空', () => {
  const [, home] = normalizeDevices(['OFFICE', 'HOME'], { fakeCodex: { HOME: 'ok' } });
  const { steps } = deviceScript(home);
  assert.deepEqual(steps.map((s) => s.op), ['seedConversations', 'addAccount', 'addAccount', 'policy', 'waitDrained']);
  assert.equal(steps[0].account, home.alias, '種子對話的帳戶別名和 addAccount 的相同');
  assert.equal(steps[0].seed, home.seed.seed);
  assert.equal(steps[1].alias, home.alias);
  assert.equal(steps[2].provider, 'Codex');
  assert.deepEqual(steps[3], { op: 'policy', level: 'Control', fullAccess: false, notify: false });
  const [none] = normalizeDevices([{ name: 'X', seed: { count: 0 } }]);
  assert.deepEqual(deviceScript(none).steps.map((s) => s.op), ['addAccount', 'policy', 'waitDrained'], '不種對話就沒有 seedConversations');
});

test('pairArgs_經代理的網址也是Origin_密碼只給環境變數名稱', () => {
  const [office] = normalizeDevices();
  const base = 'https://127.0.0.1:50001';
  const a = pairArgs({ dataDir: 'D', server: base, device: office });
  assert.deepEqual(a.slice(0, 3), ['--data', 'D', 'pair']);
  const at = (k) => a[a.indexOf(k) + 1];
  assert.equal(at('--server'), base);
  assert.equal(at('--owner-origin'), base, 'aud 與非 GET 的 Origin 都是 PublicOrigin＝代理的網址');
  assert.equal(at('--owner-password-env'), OWNER_PASSWORD_ENV);
  assert.equal(at('--name'), 'OFFICE');
  assert.ok(a.includes('--dev-trust'));
  assert.ok(!a.some((x) => /changeme2026/u.test(x)), '命令列不帶密碼');
  assert.throws(() => pairArgs({ dataDir: 'D', device: office }), /伺服器/);
  const [v] = normalizeDevices([{ name: 'V', appVersion: '9.9.9' }]);
  assert.deepEqual(pairArgs({ dataDir: 'D', server: base, device: v }).slice(0, 5), ['--data', 'D', '--app-version', '9.9.9', 'pair']);
});

test('runArgs_控制通道_假Codex只在有情境時', () => {
  const [office, home] = normalizeDevices(['OFFICE', 'HOME'], { fakeCodex: { HOME: 'evil_host' } });
  const a = runArgs({ dataDir: 'D', scriptFile: 'S.json', device: office });
  assert.deepEqual(a.slice(0, 5), ['--data', 'D', 'run', '--script', 'S.json']);
  assert.ok(a.includes('--control-stdin'));
  assert.ok(a.includes('--dev-trust'));
  assert.ok(!a.includes('--fake-codex'));
  const b = runArgs({ dataDir: 'D', scriptFile: 'S.json', device: home });
  assert.equal(b[b.indexOf('--fake-codex') + 1], 'evil_host');
  assert.throws(() => runArgs({ dataDir: 'D', device: office }), /劇本/);
  assert.deepEqual(unpairArgs({ dataDir: 'D', device: office }), ['--data', 'D', 'unpair']);
  assert.throws(() => unpairArgs({ device: office }), /資料根/);
  assert.deepEqual(statusArgs({ dataDir: 'D', device: office }), ['--data', 'D', 'status']);
  assert.throws(() => statusArgs({ device: office }), /資料根/);
});

test('cleanupAction_有device.json解除配對_只剩pending-key.json跑status清掉配對到一半的金鑰', () => {
  // 配對在逾時被以 PID 結束時 finally 的 PairingRecovery.Abandon 跑不到：只剩 pending-key.json，要 status 讓 LoadAsync 的 Cleanup 刪金鑰（WP4.9 合併前審查）
  assert.equal(cleanupAction({ hasDevice: true, hasPending: false }), 'unpair');
  assert.equal(cleanupAction({ hasDevice: true, hasPending: true }), 'unpair');
  assert.equal(cleanupAction({ hasDevice: false, hasPending: true }), 'status');
  assert.equal(cleanupAction({ hasDevice: false, hasPending: false }), null);
});

test('controlLine_一行JSON_換行結尾_參數不能蓋掉id與op', () => {
  const line = controlLine(3, 'stream', { conversation: 'a', segments: 2 });
  assert.ok(line.endsWith('\n'));
  assert.equal(line.indexOf('\n'), line.length - 1, '只有結尾一個換行');
  assert.deepEqual(JSON.parse(line), { id: 3, op: 'stream', conversation: 'a', segments: 2 });
  assert.deepEqual(JSON.parse(controlLine(1, 'status')), { id: 1, op: 'status' });
  assert.throws(() => controlLine(1, ''), /op/);
  assert.throws(() => controlLine(1, 'status', { id: 2 }), /id 或 op/);
  assert.throws(() => controlLine(1, 'status', { op: 'quit' }), /id 或 op/);
  // 多行文字也只佔一行（JSON 跳脫換行）
  assert.equal(controlLine(1, 'setTitle', { title: 'a\nb' }).split('\n').length, 2);
});

test('parseOutputLine_只收JSON物件', () => {
  assert.deepEqual(parseOutputLine('{"event":"ready","pid":1}\r'), { event: 'ready', pid: 1 });
  assert.equal(parseOutputLine(''), null);
  assert.equal(parseOutputLine('守門：…'), null);
  assert.equal(parseOutputLine('{壞掉'), null);
  assert.equal(parseOutputLine('[1,2]'), null);
  assert.equal(parseOutputLine(null), null);
});

test('missingFeatures_四個功能_多的不管', () => {
  assert.deepEqual(REQUIRED_FEATURES, ['conv1', 'live1', 'cmd1', 'media1']);
  assert.deepEqual(missingFeatures(['conv1', 'live1', 'cmd1', 'media1', 'update1']), []);
  assert.deepEqual(missingFeatures(['conv1', 'live1', 'cmd1']), ['media1']);
  assert.deepEqual(missingFeatures(null), REQUIRED_FEATURES);
});

test('staleOutputs_輸出不存在或有原始檔比它新就算舊_一樣新不算', () => {
  assert.deepEqual(staleOutputs([{ label: 'a.dll', mtime: 100, sources: [{ path: 'x.cs', mtime: 100 }, { path: 'y.cs', mtime: 50 }] }]), { ok: true, stale: [] });
  const r = staleOutputs([
    { label: 'a.dll', mtime: 100, sources: [{ path: 'x.cs', mtime: 101 }, { path: 'y.cs', mtime: null }] },
    { label: 'b.dll', mtime: null, sources: [] },
  ]);
  assert.equal(r.ok, false);
  assert.deepEqual(r.stale, [
    { label: 'a.dll', missing: false, newer: ['x.cs'], newerCount: 1 },
    { label: 'b.dll', missing: true, newer: [], newerCount: 0 },
  ]);
  const many = staleOutputs([{ label: 'c', mtime: 1, sources: Array.from({ length: 15 }, (_, i) => ({ path: `s${i}`, mtime: 2 })) }]);
  assert.equal(many.stale[0].newer.length, 10);
  assert.equal(many.stale[0].newerCount, 15);
  assert.deepEqual(staleOutputs([]), { ok: true, stale: [] });
});

test('parseFullFlowArgs_拒絕base_還沒有的情境給清楚的錯誤', () => {
  assert.deepEqual(parseFullFlowArgs([]), {
    only: ['smoke', 'FF1', 'FF2', 'FF3', 'FF4', 'FF5', 'FF6', 'FF7', 'FF8', 'FF9', 'FF10', 'FF11'], repeat: 1, work: null, out: null, hub: null, fakeDevice: null, headed: false, prevRef: null,
  }, '沒給 --only 時跑全部已有的（步 15 起有 FF1～FF3、步 16 起有 FF4～FF6、步 17 起有 FF7、FF8、FF10、步 18 起有 FF9、P5 起有 FF11）');
  assert.deepEqual(SCENARIOS, ['smoke', 'FF1', 'FF2', 'FF3', 'FF4', 'FF5', 'FF6', 'FF7', 'FF8', 'FF9', 'FF10', 'FF11']);
  assert.deepEqual(PLANNED_SCENARIOS, [], '步 18 之後沒有還沒做的情境');
  assert.ok(!PLANNED_SCENARIOS.some((s) => SCENARIOS.includes(s)), '之後才有的不和已有的重疊');
  assert.equal(parseFullFlowArgs(['--only', 'FF9', '--prev-ref', '7001273']).prevRef, '7001273', 'FF9 的 DEPLOYED_PREV 是哪個提交（計畫 D12）');
  assert.throws(() => parseFullFlowArgs(['--prev-ref', '7001']), /--prev-ref/, '太短');
  assert.throws(() => parseFullFlowArgs(['--prev-ref', '7001273G']), /--prev-ref/, '不是十六進位');
  assert.throws(() => parseFullFlowArgs(['--prev-ref']), /後面要有值/);
  const a = parseFullFlowArgs(['--only', 'smoke', '--repeat', '2', '--work', 'W', '--fake-device', 'F.exe', '--headed']);
  assert.deepEqual([a.only, a.repeat, a.work, a.fakeDevice, a.headed], [['smoke'], 2, 'W', 'F.exe', true]);
  assert.deepEqual(parseFullFlowArgs(['--only', 'FF1,FF2,FF3', '--repeat', '2']).only, ['FF1', 'FF2', 'FF3']);
  assert.deepEqual(parseFullFlowArgs(['--only', 'FF4,FF5,FF6', '--repeat', '2']).only, ['FF4', 'FF5', 'FF6']);
  assert.deepEqual(parseFullFlowArgs(['--only', 'FF7,FF8,FF10', '--repeat', '2']).only, ['FF7', 'FF8', 'FF10']);
  assert.throws(() => parseFullFlowArgs(['--base', 'https://127.0.0.1:1']), /--base/);
  assert.throws(() => parseFullFlowArgs(['--only', 'FF12'], { planned: ['FF12'] }), /FF12 還沒有實作/, '之後才有的情境（目前沒有）給清楚的錯誤');
  assert.deepEqual(parseFullFlowArgs(['--only', 'FF7,FF8,FF9,FF10', '--repeat', '2']).only, ['FF7', 'FF8', 'FF9', 'FF10']);
  assert.throws(() => parseFullFlowArgs(['--only', 'FF99']), /不認得的情境/);
  assert.throws(() => parseFullFlowArgs(['--only', 'smoke,smoke']), /重複/);
  assert.throws(() => parseFullFlowArgs(['--repeat', '0']), /1～20/);
  assert.throws(() => parseFullFlowArgs(['--repeat']), /後面要有值/);
  assert.throws(() => parseFullFlowArgs(['--nope']), /不認得的選項/);
  assert.throws(() => parseFullFlowArgs(['smoke']), /多出來的參數/);
  // 之後的步驟把情境加進 available 時照常
  assert.deepEqual(parseFullFlowArgs(['--only', 'FF12,smoke'], { available: ['smoke', 'FF12'] }).only, ['FF12', 'smoke']);
  assert.deepEqual(parseFullFlowArgs(['--only', 'FF11']).only, ['FF11'], 'P5 的 Grok 與 Antigravity');
});

test('normalizeDevices_sameSeedAs_步18_刻意照抄另一台的種子_其他的照樣不能撞', () => {
  const [office] = normalizeDevices(['OFFICE']);
  // 後加的裝置（existing 是已經在跑的）：沒給 seed 時照抄那台的每一段
  const [twin] = normalizeDevices([{ name: 'OFFICE2', sameSeedAs: 'OFFICE' }], { existing: [office] });
  assert.equal(twin.sameSeedAs, 'OFFICE');
  assert.deepEqual(twin.seedParts, office.seedParts);
  assert.equal(twin.seed.count, office.seed.count);
  assert.notEqual(twin.alias, office.alias, '帳戶別名照自己的名稱');
  // 同一份清單裡、排在後面的也可以
  const [, b] = normalizeDevices(['OFFICE', { name: 'OFFICE2', sameSeedAs: 'OFFICE' }]);
  assert.deepEqual(b.seedParts.map((p) => p.seed), [20261101]);
  // 沒有 sameSeedAs 的還是不能撞到 existing 的種子
  assert.throws(() => normalizeDevices([{ name: 'X', seed: { count: 2, seed: 20261101 } }], { existing: [office] }), /OFFICE 與 X 的種子相同/);
  // sameSeedAs 只能照抄那一台：給了別的種子、或那台不存在、或那台沒種子都不行
  assert.throws(() => normalizeDevices([{ name: 'OFFICE2', sameSeedAs: 'OFFICE', seed: { count: 2, seed: 5 } }], { existing: [office] }), /不是 OFFICE 的/);
  assert.throws(() => normalizeDevices([{ name: 'OFFICE2', sameSeedAs: 'NOPE' }], { existing: [office] }), /sameSeedAs/);
  const [empty] = normalizeDevices([{ name: 'EMPTY' }]);
  assert.throws(() => normalizeDevices([{ name: 'E2', sameSeedAs: 'EMPTY' }], { existing: [empty] }), /沒有種子/);
  // 名稱不能和 existing 重複
  assert.throws(() => normalizeDevices(['OFFICE'], { existing: [office] }), /名稱重複/);
  // 一般的裝置 sameSeedAs 是 null
  assert.equal(office.sameSeedAs, null);
});

test('restartScript_步18_只設政策_預設等清空', () => {
  const [, home] = normalizeDevices();
  assert.deepEqual(restartScript(home).steps, [{ op: 'policy', level: 'Control', fullAccess: false, notify: false }, { op: 'waitDrained', seconds: 180 }]);
  assert.deepEqual(restartScript(home, { drain: false }).steps.map((s) => s.op), ['policy'], '刻意留著待送列（或連不上）時不等');
  assert.ok(!restartScript(home).steps.some((s) => s.op === 'seedConversations' || s.op === 'addAccount'), '不再種對話、不再加帳戶（都在資料根裡）');
});

test('rollbackBuilds_步18_三個舊組建_沒設的記為什麼', () => {
  assert.deepEqual(ROLLBACK_BUILDS.map((b) => [b.key, b.variable, b.commit]), [
    ['OLD', 'MYRIAGENT_SYNC_OLD_HUB', '1724718'], ['PREV', 'MYRIAGENT_SYNC_PREV_HUB', '5538705'], ['DEPLOYED_PREV', 'MYRIAGENT_SYNC_DEPLOYED_PREV_HUB', null]]);
  const none = rollbackBuilds({}, null);
  assert.deepEqual(none.map((b) => b.missing), ['沒設 MYRIAGENT_SYNC_OLD_HUB', '沒設 MYRIAGENT_SYNC_PREV_HUB', '沒設 MYRIAGENT_SYNC_DEPLOYED_PREV_HUB']);
  const all = rollbackBuilds({ MYRIAGENT_SYNC_OLD_HUB: ' C:/a.exe ', MYRIAGENT_SYNC_PREV_HUB: 'C:/b.exe', MYRIAGENT_SYNC_DEPLOYED_PREV_HUB: 'C:/c.exe' }, '7001273');
  assert.deepEqual(all.map((b) => [b.exe, b.commit, b.missing]), [['C:/a.exe', '1724718', null], ['C:/b.exe', '5538705', null], ['C:/c.exe', '7001273', null]]);
  const noRef = rollbackBuilds({ MYRIAGENT_SYNC_DEPLOYED_PREV_HUB: 'C:/c.exe' }, null);
  assert.equal(noRef[2].missing, '沒給 --prev-ref <提交>', '有組建但不知道是哪個提交：不算');
  assert.equal(rollbackBuilds({ MYRIAGENT_SYNC_OLD_HUB: '  ' })[0].missing, '沒設 MYRIAGENT_SYNC_OLD_HUB', '空白當成沒設');
});

test('normalizeDevices_codexEmail與cli_步17_劇本在帳戶之後宣告假CLI', () => {
  assert.equal(FAKE_CODEX_EMAIL, 'device@example.test', '同 FakeCodexAppServerScript 登入完成後 account/read 回的信箱');
  const [office, home] = normalizeDevices([
    { name: 'OFFICE', fakeCodex: 'evil_host', codexEmail: FAKE_CODEX_EMAIL },
    { name: 'HOME', fakeCodex: 'ok', codexEmail: FAKE_CODEX_EMAIL, cli: { Codex: '0.50.0', Claude: '2.1.0' } },
  ]);
  assert.equal(office.codexAccount.email, FAKE_CODEX_EMAIL);
  assert.equal(home.codexAccount.email, FAKE_CODEX_EMAIL, '兩台同一個信箱＝同一個邏輯帳戶');
  assert.deepEqual(office.cli, []);
  assert.deepEqual(home.cli, [{ provider: 'Claude', version: '2.1.0' }, { provider: 'Codex', version: '0.50.0' }], '照 Claude、Codex 的順序');
  const { steps } = deviceScript(home);
  assert.deepEqual(steps.map((s) => s.op), ['seedConversations', 'addAccount', 'addAccount', 'cli', 'cli', 'policy', 'waitDrained']);
  assert.deepEqual(steps.filter((s) => s.op === 'cli'), [{ op: 'cli', provider: 'Claude', version: '2.1.0' }, { op: 'cli', provider: 'Codex', version: '0.50.0' }]);
  const [plain] = normalizeDevices(['HOME'], { fakeCodex: { HOME: 'ok' } });
  assert.equal(plain.codexAccount.email, 'home-codex@example.test', '沒給 codexEmail 時照舊');
  assert.ok(!deviceScript(plain).steps.some((s) => s.op === 'cli'), '沒給 cli 就不宣告');
  assert.throws(() => normalizeDevices([{ name: 'HOME', codexEmail: FAKE_CODEX_EMAIL }]), /沒有假 Codex/);
  assert.throws(() => normalizeDevices([{ name: 'HOME', fakeCodex: 'ok', codexEmail: 'a b@x' }]), /codexEmail/);
  assert.throws(() => normalizeDevices([{ name: 'HOME', cli: { Gemini: '1.0.0' } }]), /只收 Claude、Codex、Grok、Antigravity/);
  assert.throws(() => normalizeDevices([{ name: 'HOME', cli: { Claude: '' } }]), /版本不合格/);
  assert.throws(() => normalizeDevices([{ name: 'HOME', cli: ['Claude'] }]), /cli 要是/);
});

test('normalizeDevices_providers_P5_另外加Grok與Antigravity帳戶_四種假CLI', () => {
  const [office, home] = normalizeDevices([{ name: 'OFFICE', providers: ['Antigravity', 'Grok'], cli: { Antigravity: '1.4.5', Grok: '1.0.52' } }, 'HOME']);
  assert.deepEqual(office.extraAccounts, [
    { alias: 'office-grok', provider: 'Grok', label: 'OFFICE Grok', email: 'office-grok@example.test' },
    { alias: 'office-agy', provider: 'Antigravity', label: 'OFFICE Antigravity', email: 'office-agy@example.test' },
  ], '照 Grok、Antigravity 的順序');
  assert.deepEqual(home.extraAccounts, []);
  assert.deepEqual(office.cli, [{ provider: 'Grok', version: '1.0.52' }, { provider: 'Antigravity', version: '1.4.5' }]);
  const { steps } = deviceScript(office);
  assert.deepEqual(steps.map((s) => s.op), ['seedConversations', 'addAccount', 'addAccount', 'addAccount', 'cli', 'cli', 'policy', 'waitDrained']);
  assert.deepEqual(steps.slice(2, 4).map((s) => s.provider), ['Grok', 'Antigravity'], '另外的帳戶在 Claude（與 Codex）之後');
  assert.throws(() => normalizeDevices([{ name: 'X', providers: ['Codex'] }]), /只收 Grok、Antigravity/);
  assert.throws(() => normalizeDevices([{ name: 'X', providers: ['Grok', 'Grok'] }]), /重複/);
  assert.throws(() => normalizeDevices([{ name: 'X', providers: 'Grok' }]), /陣列/);
});

test('normalizeDevices_種子陣列是好幾段_合計_每段要有count與seed_所有段的seed不重複', () => {
  const [home, office] = normalizeDevices([
    { name: 'HOME', seed: { count: 30, seed: 11, itemsPerConversation: 12 } },
    { name: 'OFFICE', seed: [{ count: 100, seed: 12, olderThanDays: 1 }, { count: 200, seed: 13, olderThanDays: 91, itemsPerConversation: 9 }], drain: false },
  ]);
  assert.equal(home.seed.count, 30);
  assert.equal(home.seedParts.length, 1);
  assert.equal(home.seedParts[0].itemsPerConversation, 12);
  assert.equal(home.drain, true, 'drain 預設 true');
  assert.equal(office.seed.count, 300, 'seed.count 是合計');
  assert.equal(office.seed.seed, 12, '其他欄位是第一段的');
  assert.deepEqual(office.seedParts.map((p) => [p.count, p.seed, p.olderThanDays, p.itemsPerConversation, p.baseTimeUtc]),
    [[100, 12, 1, 6, '2026-10-20T00:00:00Z'], [200, 13, 91, 9, '2026-10-20T00:00:00Z']], '每段蓋在預設上（itemsPerConversation 6、同一個 baseTimeUtc）');
  assert.equal(office.drain, false);
  // seeds 參數的陣列也是好幾段（蓋過裝置自己的）
  const [o2] = normalizeDevices(['OFFICE'], { seeds: { OFFICE: [{ count: 2, seed: 5 }, { count: 0, seed: 6 }] } });
  assert.deepEqual(o2.seedParts.map((p) => p.seed), [5], 'count 0 的那段不種');
  assert.equal(o2.seed.count, 2);
  assert.throws(() => normalizeDevices([{ name: 'OFFICE', seed: [] }]), /空的/);
  assert.throws(() => normalizeDevices([{ name: 'OFFICE', seed: [{ count: 1 }] }]), /count 與 seed/);
  assert.throws(() => normalizeDevices([{ name: 'OFFICE', seed: [7] }]), /物件/);
  assert.throws(() => normalizeDevices([{ name: 'OFFICE', seed: [{ count: 1, seed: 1 }, { count: 1, seed: 1 }] }]), /種子相同/, '同一台的兩段也不能同種子');
  assert.throws(() => normalizeDevices([{ name: 'HOME', seed: { count: 1, seed: 1 } }, { name: 'OFFICE', seed: [{ count: 1, seed: 2 }, { count: 1, seed: 1 }] }]), /種子相同/);
  assert.throws(() => normalizeDevices([{ name: 'OFFICE', seed: [{ count: 3000, seed: 1 }, { count: 2001, seed: 2 }] }]), /合計最多 5000/);
  assert.throws(() => normalizeDevices([{ name: 'OFFICE', seed: [{ count: 1, seed: 1, olderThanDays: -1 }] }]), /olderThanDays/);
  assert.throws(() => normalizeDevices([{ name: 'OFFICE', drain: 'no' }]), /drain/);
});

test('deviceScript_每段種子一步_drain是false時不等outbox清空', () => {
  const [office] = normalizeDevices([{ name: 'OFFICE', seed: [{ count: 100, seed: 12, olderThanDays: 1 }, { count: 200, seed: 13, olderThanDays: 91 }], drain: false }]);
  const { steps } = deviceScript(office);
  assert.deepEqual(steps.map((s) => s.op), ['seedConversations', 'seedConversations', 'addAccount', 'policy']);
  assert.deepEqual(steps.slice(0, 2).map((s) => [s.count, s.seed, s.olderThanDays, s.account]), [[100, 12, 1, 'office-claude'], [200, 13, 91, 'office-claude']]);
  const [home] = normalizeDevices(['HOME']);
  assert.equal(deviceScript(home).steps.at(-1).op, 'waitDrained');
});
