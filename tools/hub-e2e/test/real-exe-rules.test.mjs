// lib/real-exe-rules.mjs 的單元測試（node:test）：(cd tools/hub-e2e && npm test)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import os from 'node:os';
import path from 'node:path';
import { appArgs, appEnv, hasNoCliSwitch, NO_CLI_VARIABLE, parseRealExeArgs, startupReminder } from '../lib/real-exe-rules.mjs';

const tmp = (...p) => path.join(os.tmpdir(), 'myriagent-tools', 'real-exe-test', ...p);

test('hasNoCliSwitch_只認UTF16LE的字串常值', () => {
  assert.equal(hasNoCliSwitch(Buffer.concat([Buffer.from([0, 1, 2]), Buffer.from(NO_CLI_VARIABLE, 'utf16le'), Buffer.from([3])])), true);
  assert.equal(hasNoCliSwitch(Buffer.from(NO_CLI_VARIABLE, 'utf8')), false, 'UTF-8 的不算（.NET 的字串常值是 UTF-16LE）');
  assert.equal(hasNoCliSwitch(Buffer.from('MYRIAGENT_AGENT_PROBE_NO_CL', 'utf16le')), false);
  assert.equal(hasNoCliSwitch('MYRIAGENT_AGENT_PROBE_NO_CLI'), false, '不是 Buffer');
  assert.equal(hasNoCliSwitch(null), false);
});

test('startupReminder_取最後一筆完整的那一句', () => {
  const log = [
    '10:00:00.000 [INFO] 啟動 Clode Myriagent dev，資料夾 C:\\x（開發版）',
    '10:00:01.000 [INFO] 啟動提醒：遠端操作是 View（允許完全自動：False）',
    '10:00:02.000 [INFO] 其他',
    '10:05:00.000 [INFO] 啟動提醒：遠端操作是 Control（允許完全自動：True）',
  ].join('\r\n');
  const r = startupReminder(log);
  assert.equal(r.level, 'Control');
  assert.equal(r.fullAccess, true);
  assert.equal(r.line, '10:05:00.000 [INFO] 啟動提醒：遠端操作是 Control（允許完全自動：True）');
});

test('startupReminder_沒有或不完整時是null', () => {
  assert.equal(startupReminder(''), null);
  assert.equal(startupReminder(null), null);
  assert.equal(startupReminder('啟動提醒：遠端操作是 Control'), null, '少了括號');
  assert.equal(startupReminder('啟動提醒：遠端操作是 Full（允許完全自動：True）'), null, '不認得的層級');
  assert.equal(startupReminder('啟動時的遠端操作提醒失敗：x'), null, '失敗的那一行不算');
  assert.deepEqual(startupReminder('啟動提醒：遠端操作是 Off（允許完全自動：False）'), { level: 'Off', fullAccess: false, line: '啟動提醒：遠端操作是 Off（允許完全自動：False）' });
});

test('appEnv_放進探針與開發信任_不留本session的變數', () => {
  const env = appEnv({ PATH: 'C:\\tripwire;C:\\Windows', SystemRoot: 'C:\\Windows' }, { probeDir: tmp('probe'), claudeConfigDir: tmp('empty', 'claude'), codexHome: tmp('empty', 'codex') });
  assert.equal(env.PATH, 'C:\\tripwire;C:\\Windows');
  assert.equal(env.MYRIAGENT_AGENT_PROBE, tmp('probe'));
  assert.equal(env.MYRIAGENT_AGENT_PROBE_SEND, '0');
  assert.equal(env.MYRIAGENT_AGENT_PROBE_NO_CLI, '1');
  assert.equal(env.MYRIAGENT_AGENT_PROBE_KEEP, '1');
  assert.equal(env.MYRIAGENT_SYNC_DEV_TRUST, '1');
  assert.equal(env.CLAUDE_CONFIG_DIR, tmp('empty', 'claude'));
  assert.equal(env.CODEX_HOME, tmp('empty', 'codex'));
  assert.equal(Object.keys(env).filter((k) => k.startsWith('MYRIAGENT_')).sort().join(','),
    'MYRIAGENT_AGENT_PROBE,MYRIAGENT_AGENT_PROBE_KEEP,MYRIAGENT_AGENT_PROBE_NO_CLI,MYRIAGENT_AGENT_PROBE_SEND,MYRIAGENT_SYNC_DEV_TRUST', '沒有其他 MYRIAGENT_（例如 MYRIAGENT_SYNC_SERVER）');
});

test('appEnv_base還有該剝掉的變數或路徑不在暫存資料夾_丟例外', () => {
  const dirs = { probeDir: tmp('probe'), claudeConfigDir: tmp('c'), codexHome: tmp('x') };
  for (const leftover of ['CLAUDE_CODE_ENTRYPOINT', 'MYRIAGENT_SYNC_SERVER', 'codex_home', 'ANTHROPIC_API_KEY', 'COMPlus_gcServer']) {
    assert.throws(() => appEnv({ PATH: 'x', [leftover]: '1' }, dirs), /該剝掉/u, leftover);
  }
  assert.throws(() => appEnv({}, { ...dirs, probeDir: 'C:\\Users\\Public\\probe' }), /暫存資料夾/u);
  assert.throws(() => appEnv({}, { ...dirs, claudeConfigDir: path.join(os.homedir(), '.claude') }), /CLAUDE_CONFIG_DIR/u);
  assert.throws(() => appEnv({}, { ...dirs, codexHome: 'relative\\codex' }), /CODEX_HOME/u);
  assert.throws(() => appEnv({}, { ...dirs, probeDir: os.tmpdir() }), /暫存資料夾/u, '暫存資料夾本身不算');
});

test('appArgs_只有data_而且要在暫存資料夾底下', () => {
  assert.deepEqual(appArgs(tmp('env', 'REAL')), ['--data', tmp('env', 'REAL')]);
  assert.throws(() => appArgs(path.join(process.env.LOCALAPPDATA ?? 'C:\\Users\\x\\AppData\\Local', 'Myriagent')), /暫存資料夾/u, '使用者真正的資料夾');
  assert.throws(() => appArgs('REAL'), /完整路徑/u);
  assert.throws(() => appArgs(undefined), /完整路徑/u);
});

test('parseRealExeArgs_預設與各參數', () => {
  assert.deepEqual(parseRealExeArgs([]), { work: null, out: null, app: null, hub: null, fakeDevice: null, headed: false, waitSeconds: 120 });
  const r = parseRealExeArgs(['--work', tmp('w'), '--out', 'x.json', '--app', 'a.exe', '--hub', 'h.exe', '--fake-device', 'f.exe', '--headed', '--wait-seconds', '300']);
  assert.deepEqual(r, { work: tmp('w'), out: 'x.json', app: 'a.exe', hub: 'h.exe', fakeDevice: 'f.exe', headed: true, waitSeconds: 300 });
});

test('parseRealExeArgs_不合格的參數丟例外', () => {
  assert.throws(() => parseRealExeArgs(['--base', 'https://hub.example.com']), /不收 --base/u);
  assert.throws(() => parseRealExeArgs(['--work']), /要有值/u);
  assert.throws(() => parseRealExeArgs(['--app', '--headed']), /要有值/u);
  assert.throws(() => parseRealExeArgs(['--work', 'C:\\repo\\x']), /暫存資料夾/u);
  assert.throws(() => parseRealExeArgs(['--wait-seconds', '5']), /10～600/u);
  assert.throws(() => parseRealExeArgs(['--wait-seconds', '1.5']), /10～600/u);
  assert.throws(() => parseRealExeArgs(['--repeat', '2']), /不認得/u);
});
