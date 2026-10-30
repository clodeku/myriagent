// 執行列的純規則（src/Myriagent/Web/launchrules.js）：按鈕在各階段能不能按、快捷鍵、狀態文字與色調、執行模式（Debug／Release）、失敗時自動打開輸出、
// 輸出的合併與再拿、錯誤行與「把錯誤貼到輸入框」，以及 launchrules.js 沒有 import、沒有 HTML 寫入點、launchbar.js 沒有 HTML 寫入點。
// 規則模組沒有 import，以 data: URL 匯入（同 gitrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'launchrules.js'), 'utf8');
const L = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// launchrules.test\n', 'utf8').toString('base64'));

const target = (o = {}) => Object.assign({ id: 't1', name: 'Myriagent', kind: 'dotnet', path: 'src\\Myriagent\\Myriagent.csproj', detail: 'WinForms · net10.0-windows', phase: 'idle' }, o);
const launch = (o = {}) => Object.assign({ folder: 'C:\\repo\\myriagent', selected: 't1', targets: [target()], phase: 'idle', since: null, last: null, logSeq: 0, logEpoch: 0 }, o);
const enabled = (phase) => Object.entries(L.launchButtons(phase)).filter(([, b]) => b.enabled).map(([k]) => k);

test('按鈕：閒置只有啟動；建置中停止與重新建置；執行中暫停、停止、重新建置；暫停中繼續（送 resume）；停止中只有重新建置', () => {
  assert.deepEqual(enabled('idle'), ['start']);
  assert.deepEqual(enabled('building'), ['stop', 'restart']);
  assert.deepEqual(enabled('running'), ['pause', 'stop', 'restart']);
  assert.deepEqual(enabled('paused'), ['start', 'stop', 'restart']);
  assert.deepEqual(enabled('stopping'), ['restart']);
  assert.equal(L.launchButtons('paused').start.action, 'resume');
  assert.equal(L.launchButtons('paused').start.label, '繼續');
  assert.equal(L.launchButtons('idle').start.action, 'start');
  assert.equal(L.buttonTitle(L.launchButtons('idle').start), '建置並執行（F5）');
  assert.equal(L.buttonTitle(L.launchButtons('idle').pause), '暫停');
  assert.equal(L.buttonTitle(L.launchButtons('running').restart), '重新建置並執行（Ctrl+Shift+F5）');
});

test('不認得的階段當閒置；選的目標找不到用第一個', () => {
  assert.equal(L.phaseOf(launch({ phase: 'weird' })), 'idle');
  assert.equal(L.phaseOf(null), 'idle');
  assert.equal(L.selectedTarget(launch({ selected: 'gone' })).id, 't1');
  assert.equal(L.selectedTarget(launch({ targets: [] })), null);
  assert.equal(L.selectedTarget(null), null);
});

test('快捷鍵：F5 啟動／繼續、Shift+F5 停止、Ctrl+Shift+F5 重新建置（閒置時等於啟動）；不能做的回 null', () => {
  const k = (o) => Object.assign({ key: 'F5', shiftKey: false, ctrlKey: false, altKey: false, metaKey: false }, o);
  assert.equal(L.keyAction(k({}), 'idle'), 'start');
  assert.equal(L.keyAction(k({}), 'paused'), 'resume');
  assert.equal(L.keyAction(k({}), 'running'), null);
  assert.equal(L.keyAction(k({ shiftKey: true }), 'running'), 'stop');
  assert.equal(L.keyAction(k({ shiftKey: true }), 'idle'), null);
  assert.equal(L.keyAction(k({ ctrlKey: true, shiftKey: true }), 'running'), 'restart');
  assert.equal(L.keyAction(k({ ctrlKey: true, shiftKey: true }), 'idle'), 'start');
  assert.equal(L.keyAction(k({ ctrlKey: true }), 'idle'), null);
  assert.equal(L.keyAction(k({ altKey: true }), 'idle'), null);
  assert.equal(L.keyAction(k({ key: 'F6' }), 'idle'), null);
});

test('狀態：建置中轉圈帶時間、執行中綠、暫停中琥珀；結束後依怎麼結束', () => {
  const now = 1_800_000_000_000;
  const b = L.statusView(launch({ phase: 'building', since: now - 65_000 }), now);
  assert.deepEqual([b.tone, b.text, b.spin, b.timed], ['busy', '建置中 1:05', true, true]);
  const r = L.statusView(launch({ phase: 'running', since: now - 3_725_000 }), now);
  assert.deepEqual([r.tone, r.text, r.spin], ['ok', '執行中 1:02:05', false]);
  assert.equal(L.statusView(launch({ phase: 'paused', since: now - 1000 }), now).tone, 'paused');
  assert.deepEqual([L.statusView(launch({ phase: 'stopping' }), now).text, L.statusView(launch({ phase: 'stopping' }), now).spin], ['停止中…', true]);
  assert.equal(L.statusView(launch(), now).text, '', '沒執行過不顯示');

  const failed = L.statusView(launch({ last: { kind: 'buildFailed', code: 1, text: 'Form1.cs(3,7): CS1002 必須是 ;', at: now } }), now);
  assert.deepEqual([failed.tone, failed.text, failed.title], ['danger', '建置失敗', '建置失敗\nForm1.cs(3,7): CS1002 必須是 ;']);
  assert.equal(r.title, '執行中 1:02:05\n暫停會凍結它的所有程序（視窗會沒有回應）', '說明以狀態文字開頭（窄窗格只留顏色點）');
  assert.equal(L.statusView(launch({ last: { kind: 'startFailed', code: null, text: '無法啟動：找不到 dotnet', at: now } }), now).text, '無法啟動');
  assert.deepEqual(Object.values(L.statusView(launch({ last: { kind: 'exited', code: 3, text: '已結束（結束碼 3）', at: now } }), now)).slice(0, 2), ['warn', '已結束（結束碼 3）']);
  assert.equal(L.statusView(launch({ last: { kind: 'exited', code: 0, text: '已結束', at: now } }), now).tone, 'idle');
  assert.equal(L.statusView(launch({ last: { kind: 'stopped', code: null, text: '已停止', at: now } }), now).text, '已停止');
  // 執行中的狀態蓋過上一次的結果
  assert.equal(L.statusView(launch({ phase: 'running', since: now, last: { kind: 'buildFailed', text: 'x', at: now } }), now).tone, 'ok');
});

test('失敗時自動打開輸出：建置失敗與無法啟動才有鍵，同一次失敗鍵相同', () => {
  const failed = launch({ last: { kind: 'buildFailed', code: 1, text: 'x', at: 5 }, logEpoch: 2 });
  assert.equal(L.failureKey(failed), 't1:5:2');
  assert.equal(L.failureKey(launch({ last: { kind: 'startFailed', text: 'x', at: 6 }, logEpoch: 3 })), 't1:6:3');
  assert.equal(L.failureKey(launch({ last: { kind: 'exited', code: 1, at: 5 } })), null);
  assert.equal(L.failureKey(Object.assign({}, failed, { phase: 'building' })), null, '重新建置中不再算');
  assert.equal(L.failureKey(null), null);
});

test('目標的說明與選單那一行：執行中的標出來', () => {
  assert.equal(L.targetTitle(target()), 'Myriagent\nWinForms · net10.0-windows\nsrc\\Myriagent\\Myriagent.csproj');
  assert.equal(L.targetDesc(target({ phase: 'running' })), '執行中・WinForms · net10.0-windows・src\\Myriagent\\Myriagent.csproj');
  assert.equal(L.targetDesc(target({ detail: '', kind: 'electron', path: 'package.json' })), 'Electron・package.json');
  assert.equal(L.targetTitle(null), '');
});

test('重畫的鍵：狀態、目標或結果變了才變；輸出的序號不算', () => {
  const a = launch({ logSeq: 1 });
  assert.equal(L.launchKey(a), L.launchKey(launch({ logSeq: 9 })));
  assert.notEqual(L.launchKey(a), L.launchKey(launch({ phase: 'building' })));
  assert.notEqual(L.launchKey(a), L.launchKey(launch({ targets: [target({ phase: 'running' })] })));
  assert.equal(L.launchKey(null), '');
  assert.notEqual(L.launchKey(a), L.launchKey(launch({ mode: 'release' })), '換了模式要重畫晶片');
  assert.notEqual(L.launchKey(launch({ phase: 'running' })), L.launchKey(launch({ phase: 'running', runMode: 'release' })));
});

test('執行模式：沒送或不認得的當 Debug；這一次的模式只在執行中才有；晶片的說明', () => {
  assert.deepEqual(L.MODES, ['debug', 'release']);
  assert.equal(L.modeOf(launch()), 'debug');
  assert.equal(L.modeOf(launch({ mode: 'profile' })), 'debug');
  assert.equal(L.modeOf(launch({ mode: 'release' })), 'release');
  assert.equal(L.modeOf(null), 'debug');
  assert.equal(L.runModeOf(launch({ runMode: 'release' })), null, '閒置沒有這一次的模式');
  assert.equal(L.runModeOf(launch({ phase: 'running', runMode: 'release' })), 'release');
  assert.equal(L.runModeOf(launch({ phase: 'running', runMode: 'x' })), null);
  assert.deepEqual([L.modeLabel('debug'), L.modeLabel('release'), L.modeLabel('?')], ['Debug', 'Release', 'Debug']);
  assert.match(L.modeNote('debug', 'dotnet'), /^Debug 組態建置.*一起關閉$/);
  assert.match(L.modeNote('release', 'dotnet'), /^Release 組態建置.*不影響它/);
  assert.doesNotMatch(L.modeNote('debug', 'electron'), /組態/, 'Electron 沒有建置組態');

  const chip = L.modeChip(launch({ mode: 'release' }));
  assert.equal(chip.mode, 'release');
  assert.equal(chip.label, 'Release');
  assert.ok(chip.title.startsWith('執行模式：Release\n') && chip.title.endsWith('（點一下換模式）'), chip.title);
  assert.doesNotMatch(chip.title, /這次以/);
  // 執行中換了模式：說明下一次才套用
  const pending = L.modeChip(launch({ phase: 'running', mode: 'release', runMode: 'debug' }));
  assert.match(pending.title, /這次以 Debug 模式執行；下次啟動或重新建置並執行時改用 Release/);
});

test('狀態的說明帶這一次的模式（會不會跟著 Clode Myriagent 關閉）', () => {
  const now = 1_800_000_000_000;
  const debug = L.statusView(launch({ phase: 'running', since: now - 1000, runMode: 'debug' }), now);
  assert.equal(debug.text, '執行中 0:01', '狀態文字不變（模式在晶片上）');
  assert.match(debug.title, /\nDebug 模式：.*一起關閉$/);
  assert.match(L.statusView(launch({ phase: 'paused', since: now, runMode: 'release' }), now).title, /\nRelease 模式：.*不影響它/);
  assert.equal(L.statusView(launch({ phase: 'building', since: now, runMode: 'release' }), now).title, '建置中 0:00\n正在建置（dotnet build -c Release）');
  assert.equal(L.statusView(launch({ phase: 'building', since: now }), now).title, '建置中 0:00\n正在建置（dotnet build）', '宿主沒送 runMode');
});

test('輸出：第一次整份、之後接在後面、換目標或 epoch 或 reset 換掉整份、最多 2000 行', () => {
  let s = L.mergeLog(null, { targetId: 't1', epoch: 1, seq: 2, reset: true, lines: ['a', 'b'] });
  assert.deepEqual(s, { targetId: 't1', epoch: 1, seq: 2, lines: ['a', 'b'] });
  s = L.mergeLog(s, { targetId: 't1', epoch: 1, seq: 3, reset: false, lines: ['c'] });
  assert.deepEqual(s.lines, ['a', 'b', 'c']);
  assert.deepEqual(L.mergeLog(s, { targetId: 't1', epoch: 2, seq: 1, reset: false, lines: ['new'] }).lines, ['new']);
  assert.deepEqual(L.mergeLog(s, { targetId: 't2', epoch: 1, seq: 9, reset: false, lines: ['x'] }).lines, ['x']);
  assert.deepEqual(L.mergeLog(s, { targetId: 't1', epoch: 1, seq: 9, reset: true, lines: ['all'] }).lines, ['all']);
  const big = L.mergeLog(null, { targetId: 't1', epoch: 1, seq: 2500, reset: true, lines: Array.from({ length: 2500 }, (_, i) => 'l' + i) });
  assert.equal(big.lines.length, L.LOG_CAPACITY);
  assert.equal(big.lines[0], 'l500');
  assert.deepEqual(L.mergeLog(s, { targetId: 't1', epoch: 1, seq: 4, lines: [null, 3, 'ok'] }).lines.slice(-1), ['ok'], '只收字串');
});

test('面板開著時要不要再拿、要帶什麼', () => {
  const s = { targetId: 't1', epoch: 2, seq: 10, lines: [] };
  assert.equal(L.needsLog(null, launch()), true);
  assert.equal(L.needsLog(s, launch({ logEpoch: 2, logSeq: 10 })), false);
  assert.equal(L.needsLog(s, launch({ logEpoch: 2, logSeq: 11 })), true);
  assert.equal(L.needsLog(s, launch({ logEpoch: 3, logSeq: 1 })), true);
  assert.equal(L.needsLog(s, launch({ selected: 't2', targets: [target(), target({ id: 't2' })] })), true);
  assert.equal(L.needsLog(s, null), false);
  assert.deepEqual(L.logRequest(s, 't1'), { epoch: 2, after: 10 });
  assert.deepEqual(L.logRequest(s, 't2'), { epoch: -1, after: 0 });
  assert.deepEqual(L.logRequest(null, 't1'), { epoch: -1, after: 0 });
});

test('錯誤行：MSBuild 的錯誤與 NETSDK／MSB，去掉重複；警告不算', () => {
  const lines = [
    '> dotnet build C:\\x\\App.csproj -c Debug',
    'C:\\x\\Form1.cs(12,5): warning CS0168: 已宣告變數 [C:\\x\\App.csproj]',
    'C:\\x\\Form1.cs(14,9): error CS1002: 必須是 ; [C:\\x\\App.csproj]',
    'MSBUILD : error MSB1009: 專案檔不存在。',
    '建置失敗。',
    'C:\\x\\Form1.cs(14,9): error CS1002: 必須是 ; [C:\\x\\App.csproj]',
  ];
  assert.deepEqual(L.errorLines(lines), ['C:\\x\\Form1.cs(14,9): error CS1002: 必須是 ; [C:\\x\\App.csproj]', 'MSBUILD : error MSB1009: 專案檔不存在。']);
  assert.equal(L.errorLines(Array.from({ length: 40 }, (_, i) => `a.cs(${i},1): error CS0001: x`)).length, L.MAX_ERROR_LINES);
  assert.deepEqual(L.errorLines(null), []);
});

test('把錯誤貼到輸入框：專案路徑＋錯誤行的程式碼區塊；沒有錯誤行時用 last.text；什麼都沒有回空字串', () => {
  const l = launch({ last: { kind: 'buildFailed', code: 1, text: 'Form1.cs(14,9): CS1002 必須是 ;', at: 1 } });
  assert.equal(L.fixPrompt(l, ['C:\\x\\Form1.cs(14,9): error CS1002: 必須是 ; [p]']),
    '執行列建置 src\\Myriagent\\Myriagent.csproj 失敗，請修正：\n```\nC:\\x\\Form1.cs(14,9): error CS1002: 必須是 ; [p]\n```');
  assert.equal(L.fixPrompt(l, ['沒有錯誤行']), '執行列建置 src\\Myriagent\\Myriagent.csproj 失敗，請修正：\n```\nForm1.cs(14,9): CS1002 必須是 ;\n```');
  assert.equal(L.fixPrompt(launch(), []), '');
});

test('經過時間', () => {
  assert.equal(L.elapsed(0), '0:00');
  assert.equal(L.elapsed(59_999), '0:59');
  assert.equal(L.elapsed(3_600_000), '1:00:00');
  assert.equal(L.elapsed(-5), '0:00');
  assert.equal(L.elapsed(NaN), '0:00');
});

test('launchrules.js 沒有 import、沒有 HTML 寫入點；launchbar.js 也沒有 HTML 寫入點', () => {
  assert.ok(!/^\s*import\s/m.test(source));
  const sink = /innerHTML|outerHTML|insertAdjacentHTML|document\.write|setHtml|patchHtml/;
  assert.ok(!sink.test(source));
  assert.ok(!sink.test(readFileSync(path.join(webDir, 'launchbar.js'), 'utf8')));
});

test('建置的錯誤與警告數：同一行只算一次（MSBuild 的摘要會再列一次）、第一個錯誤優先，沒有錯誤指第一個警告', () => {
  const err = 'C:\\x\\Form1.cs(14,9): error CS1002: 必須是 ; [C:\\x\\p.csproj]';
  const warn = 'C:\\x\\Form1.cs(3,5): warning CS0168: 宣告了變數 e，但從未使用過它 [C:\\x\\p.csproj]';
  const warn2 = 'C:\\x\\Form2.cs(8,1): warning CA1822: 成員可以標記為 static [C:\\x\\p.csproj]';
  assert.deepEqual(L.buildDiagnostics(['> dotnet build', warn, err, '建置失敗。', warn, err, warn2]), { errors: 1, warnings: 2, first: 2 });
  assert.deepEqual(L.buildDiagnostics(['> dotnet build', warn, warn2]), { errors: 0, warnings: 2, first: 1 });
  assert.deepEqual(L.buildDiagnostics(['建置成功。', '    0 個警告']), { errors: 0, warnings: 0, first: -1 });
  assert.deepEqual(L.buildDiagnostics(null), { errors: 0, warnings: 0, first: -1 });
  assert.equal(L.isWarningLine(warn), true);
  assert.equal(L.isWarningLine(err), false);
  assert.equal(L.isWarningLine('warning: 沒有代碼'), false);
});
