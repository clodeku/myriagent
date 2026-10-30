// lib/child-env.mjs 與 lib/sqlite_ro.py 的單元測試（node:test）
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { childEnv, isStripped, STRIP_PREFIXES } from '../lib/child-env.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));

test('childEnv_剝掉代理與同步相關的環境變數_不分大小寫', () => {
  const source = {
    CLAUDE_CONFIG_DIR: 'x', ClaudeCode: '1', ANTHROPIC_API_KEY: 'k', OPENAI_API_KEY: 'k', ANTIGRAVITY_SESSION: 's', GROK_KEY: 'g', XAI_API_KEY: 'x',
    MYRIAGENT_SYNC_IT: '1', codex_home: 'c', ASPNETCORE_ENVIRONMENT: 'Production', DOTNET_ENVIRONMENT: 'Production',
    Path: 'C:\\Windows', TEMP: 'C:\\t', DOTNET_gcServer: '1', COMPlus_gcServer: '1', COMPlus_GCgen0MaxBudget: '0x600000', DOTNET_ROOT: 'C:\\dotnet',
  };
  const env = childEnv({}, source);
  // GC 設定（DOTNET_GC*、COMPlus_*）也剝：會蓋掉 MyriagentHub 的 Workstation GC 與硬上限（合併前審查 F3）；DOTNET_ROOT 之類照留
  assert.deepEqual(Object.keys(env).sort(), ['DOTNET_ROOT', 'Path', 'TEMP']);
  assert.equal(isStripped('claude'), true);
  assert.equal(isStripped('Path'), false);
  assert.equal(isStripped('complus_GCHeapHardLimit'), true);
  assert.ok(STRIP_PREFIXES.includes('MYRIAGENT_'));
});

test('childEnv_GC設定要給就以extra明確放回', () => {
  const env = childEnv({ DOTNET_GCgen0size: '0x400000' }, { DOTNET_GCgen0size: '0x100000', DOTNET_gcServer: '1', TEMP: 'C:\\t' });
  assert.deepEqual(env, { TEMP: 'C:\\t', DOTNET_GCgen0size: '0x400000' });
});

test('childEnv_extra最後蓋上去', () => {
  const env = childEnv({ MYRIAGENT_AGENT_PROBE: '1', TEMP: 'D:\\t' }, { MYRIAGENT_AGENT_PROBE: '0', TEMP: 'C:\\t' });
  assert.deepEqual(env, { TEMP: 'D:\\t', MYRIAGENT_AGENT_PROBE: '1' });
});

test('sqlite_ro_自我測試_只讀開WAL資料庫_檔案不變', (t) => {
  const script = path.resolve(here, '..', 'lib', 'sqlite_ro.py');
  const r = spawnSync('python', ['-X', 'utf8', '-I', script, '--self-test'], { encoding: 'utf8', windowsHide: true, env: childEnv() });
  if (r.error?.code === 'ENOENT') {
    t.skip('沒有 python');
    return;
  }
  assert.equal(r.status, 0, r.stderr);
  const result = JSON.parse(r.stdout);
  assert.equal(result.ok, true);
  assert.equal(result.writable, false);
  assert.equal(result.unchanged, true);
  assert.deepEqual(result.device.gaps, [4]);
});

test('sqlite_ro_參數不對或檔案不存在結束碼2', (t) => {
  const script = path.resolve(here, '..', 'lib', 'sqlite_ro.py');
  const r1 = spawnSync('python', ['-X', 'utf8', '-I', script], { encoding: 'utf8', windowsHide: true, env: childEnv() });
  if (r1.error?.code === 'ENOENT') {
    t.skip('沒有 python');
    return;
  }
  assert.equal(r1.status, 2);
  const r2 = spawnSync('python', ['-X', 'utf8', '-I', script, path.join(here, 'no-such.db'), 'device', 'd1'], { encoding: 'utf8', windowsHide: true, env: childEnv() });
  assert.equal(r2.status, 2);
  assert.match(r2.stderr, /找不到資料庫/);
});
