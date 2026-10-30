// 儀表板 MCP 的純規則（src/Myriagent/Web/mcprules.js）：帳戶卡上那一行的摘要、明細的每個伺服器與能做的事，
// 以及 mcprules.js 沒有 import、沒有 HTML 寫入點。規則模組沒有 import，以 data: URL 匯入（同 healthrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'mcprules.js'), 'utf8');
const R = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// mcprules.test\n', 'utf8').toString('base64'));

test('規則模組沒有 import、沒有 HTML 寫入點', () => {
  assert.doesNotMatch(source, /^\s*import\s/m);
  assert.doesNotMatch(source, /innerHTML|outerHTML|insertAdjacentHTML|document\.write/);
});

test('accountMcpOf：只取物件、帳戶 id 要是字串', () => {
  const mcp = { accounts: { a1: { checking: false, servers: [] }, a2: 'x' } };
  assert.deepEqual(R.accountMcpOf(mcp, 'a1'), { checking: false, servers: [] });
  assert.equal(R.accountMcpOf(mcp, 'a2'), null);
  assert.equal(R.accountMcpOf(mcp, 'a3'), null);
  for (const bad of [null, undefined, {}, { accounts: [] }, 'x']) assert.equal(R.accountMcpOf(bad, 'a1'), null, JSON.stringify(bad));
  assert.equal(R.accountMcpOf(mcp, null), null);
});

test('mcpStrip：精簡模式、還沒查到、查到了的已連線數與要處理的', () => {
  assert.equal(R.mcpStrip(null), null);
  assert.deepEqual(R.mcpStrip({ safeMode: true, checking: true }), { level: 'info', text: '精簡模式不載入 MCP', busy: false });
  assert.deepEqual(R.mcpStrip({ checking: true, servers: null }), { level: 'info', text: '檢查中…', busy: true });
  assert.deepEqual(R.mcpStrip({ checking: false, error: '程序開不起來', servers: null }), { level: 'bad', text: '檢查失敗：程序開不起來', busy: false });
  assert.deepEqual(R.mcpStrip({ servers: null }), { level: 'info', text: '還沒檢查', busy: false });
  assert.deepEqual(R.mcpStrip({ servers: [] }), { level: 'ok', text: '沒有設定 MCP 伺服器', busy: false });
  const all = [{ name: 'a', status: 'connected' }, { name: 'b', status: 'connected' }];
  assert.deepEqual(R.mcpStrip({ servers: all }), { level: 'ok', text: '2/2 個已連線', busy: false });
  assert.deepEqual(R.mcpStrip({ servers: [...all, { name: 'c', status: 'needs-auth' }], authorizing: 'c' }),
    { level: 'warn', text: '2/3 個已連線・1 個要授權', busy: true }, '授權中轉圈');
  assert.deepEqual(R.mcpStrip({ servers: [...all, { name: 'c', status: 'needs-auth' }, { name: 'd', status: 'failed' }, { status: 'failed' }, null] }),
    { level: 'bad', text: '2/4 個已連線・1 個要授權・1 個連不上', busy: false }, '連不上比要授權嚴重；沒有名字的不算');
});

test('mcpRows：狀態、來源、說明、各狀態能做的事；檢查或授權中按鈕停用', () => {
  const entry = {
    checking: false, authorizing: null, servers: [
      { name: 'github', status: 'connected', source: 'user', transport: 'http', tools: 12 },
      { name: 'notion', status: 'needs-auth', source: 'claudeai' },
      { name: 'broken', status: 'failed', source: 'plugin', error: 'Connection closed' },
      { name: 'slow', status: 'pending', source: 'dynamic' },
      { name: 'off', status: 'disabled', source: 'weird' },
      { name: 'odd', status: 'strange' },
    ],
  };
  const rows = R.mcpRows(entry);
  assert.deepEqual(rows.map((r) => [r.name, r.level, r.text, r.source, r.detail, r.actions.map((a) => [a.action, a.label, a.disabled])]), [
    ['github', 'ok', '已連線', '使用者', ['12 個工具'], [['reconnect', '重新連線', false]]],
    ['notion', 'warn', '需要授權', 'claude.ai 連接器', [], [['authenticate', '授權…', false]]],
    ['broken', 'bad', '連不上', '外掛', ['Connection closed'], [['reconnect', '重新連線', false]]],
    ['slow', 'info', '連線中…', '共用設定', [], []],
    ['off', 'info', '已停用', 'weird', [], []],
    ['odd', 'info', 'strange', null, [], []],
  ]);
  const authorizing = R.mcpRows(Object.assign({}, entry, { authorizing: 'notion' }));
  assert.deepEqual(authorizing[1].actions.map((a) => [a.label, a.disabled]), [['授權中…', true]]);
  assert.equal(authorizing[0].actions[0].disabled, true, '授權中別的按鈕也停用');
  assert.ok(R.mcpRows(Object.assign({}, entry, { checking: true })).every((r) => r.actions.every((a) => a.disabled)));
  assert.deepEqual(R.mcpRows(null), []);
  assert.deepEqual(R.mcpRows({ servers: null }), []);
});
