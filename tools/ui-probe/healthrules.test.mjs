// 儀表板健康度的純規則（src/Myriagent/Web/healthrules.js）：等級、帳戶卡的一行、指示檔區塊何時顯示、狀態膠囊、涵蓋率、
// 「用代理合併／精簡／整理」可以選的帳戶與明細各項的按鈕，以及 healthrules.js 沒有 import、沒有 HTML 寫入點。規則模組沒有 import，以 data: URL 匯入（同 gitrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'healthrules.js'), 'utf8');
const R = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// healthrules.test\n', 'utf8').toString('base64'));

test('規則模組沒有 import、沒有 HTML 寫入點', () => {
  assert.doesNotMatch(source, /^\s*import\s/m);
  assert.doesNotMatch(source, /innerHTML|outerHTML|insertAdjacentHTML|document\.write/);
});

test('等級：不認得的當說明', () => {
  assert.equal(R.levelLabel('ok'), '良好');
  assert.equal(R.levelLabel('warn'), '注意');
  assert.equal(R.levelLabel('bad'), '問題');
  assert.equal(R.levelOf('fatal'), 'info');
  assert.equal(R.levelOf('__proto__'), 'info');
  assert.equal(R.levelLabel(undefined), '說明');
});

test('帳戶的結果與卡片上的一行', () => {
  const v = { level: 'warn', summary: ' CLAUDE.md 偏大 ', items: [] };
  const health = { accounts: { a1: v, a2: { level: 'ok' }, a3: null } };
  assert.equal(R.accountHealthOf(health, 'a1'), v);
  assert.equal(R.accountHealthOf(health, 'a2'), null, '沒有 items 不算');
  assert.equal(R.accountHealthOf(health, 'a3'), null);
  assert.equal(R.accountHealthOf(health, 'toString'), null, '原型上的名稱不算');
  assert.equal(R.accountHealthOf(null, 'a1'), null);
  assert.equal(R.stripText(v), 'CLAUDE.md 偏大');
  assert.equal(R.stripText({ level: 'bad', summary: '' }), '問題');
});

test('指示檔區塊：兩份以上，或單一份超過上限時才顯示', () => {
  const doc = (o) => Object.assign({ status: 'complete', overLimit: false }, o);
  assert.equal(R.instructionsVisible(null), false);
  assert.equal(R.instructionsVisible({ instructions: null }), false);
  assert.equal(R.instructionsVisible({ instructions: { docs: [doc()] } }), false);
  assert.equal(R.instructionsVisible({ instructions: { docs: [doc({ overLimit: true })] } }), true);
  assert.equal(R.instructionsVisible({ instructions: { docs: [doc(), doc()] } }), true);
});

test('狀態膠囊與涵蓋率', () => {
  assert.deepEqual(R.docStatus({ status: 'empty' }), { text: '空的', tone: 'warn' });
  assert.deepEqual(R.docStatus({ status: 'missing' }), { text: '不存在', tone: 'warn' });
  assert.deepEqual(R.docStatus({ status: 'partial', missingCount: 3 }), { text: '缺 3 條', tone: 'warn' });
  assert.deepEqual(R.docStatus({ status: 'complete' }), { text: '完整', tone: 'ok' });
  assert.deepEqual(R.docStatus({ status: 'complete', overLimit: true }), { text: '超過上限', tone: 'bad' });
  assert.equal(R.coveragePct({ covered: 51, union: 52 }), 98);
  assert.equal(R.coveragePct({ covered: 0, union: 0 }), 100);
  assert.equal(R.coveragePct({ covered: 9, union: 3 }), 100, '不超過 100');
  assert.equal(R.coverageText({ covered: 0, union: 52 }), '涵蓋 0／52 條（0%）');
});

test('用代理合併／精簡可以選的帳戶：這台已登入的，指定的排第一', () => {
  const accounts = [
    { id: 'a1', status: 'ready' }, { id: 'a2', status: 'notLoggedIn' }, { id: 'a3', status: 'ready', remoteOnly: true }, { id: 'a4', status: 'ready' },
  ];
  assert.deepEqual(R.fixCandidates(accounts).map((a) => a.id), ['a1', 'a4']);
  assert.deepEqual(R.fixCandidates(accounts, 'a4').map((a) => a.id), ['a4', 'a1']);
  assert.deepEqual(R.fixCandidates(accounts, 'a2').map((a) => a.id), ['a1', 'a4'], '沒登入的指定了也不列');
  assert.deepEqual(R.fixCandidates(null), []);
});

test('明細各項的「用代理…」：全域指示看 canTrim、記憶索引看 canTrimMemory', () => {
  const v = { canTrim: true, canTrimMemory: true, items: [] };
  assert.equal(R.itemFixTask(v, { key: 'instructions' }), 'trim');
  assert.equal(R.itemFixTask(v, { key: 'memory' }), 'memory');
  assert.equal(R.itemFixTask(v, { key: 'skills' }), null);
  assert.equal(R.itemFixTask({ canTrim: false }, { key: 'instructions' }), null);
  assert.equal(R.itemFixTask({ canTrim: true }, { key: 'memory' }), null, '舊宿主沒有 canTrimMemory：不顯示');
  assert.equal(R.itemFixTask({ canTrimMemory: 'yes' }, { key: 'memory' }), null, '只認 true');
  assert.equal(R.itemFixTask(null, { key: 'memory' }), null);
  for (const task of ['merge', 'trim', 'memory']) {
    assert.ok(R.FIX_TASKS[task].label.startsWith('用代理'), task);
    assert.ok(R.FIX_TASKS[task].menu.startsWith('用哪個帳戶'), task);
  }
  assert.equal(R.FIX_TASKS.memory.label, '用代理整理…');
});

test('逐一的 skill：正規化、來源、篩選、數字', () => {
  assert.equal(R.skillRows({ key: 'mcp' }), null, '沒有 skills 的項目（舊宿主）');
  const rows = R.skillRows({
    skills: [
      { name: 'mine', label: 'mine', kind: 'skill', source: 'user', purpose: '整理訂單異常。', description: '整理訂單異常。每天早上用。', tokens: 40, listed: true, level: 'warn', issues: [{ level: 'warn', text: '本文 600 行' }, { level: 'warn' }] },
      { name: 'deploy', label: '/deploy', kind: 'command', source: 'user', tokens: 0, level: 'ok', issues: [] },
      { name: 'p:x', label: 'p:x', kind: 'skill', source: 'plugin', plugin: 'p', purpose: 'Draw charts.', tokens: 1234.4, listed: false, level: 'ok', issues: [{ level: 'info', text: '沒有列給模型' }] },
      { name: 'anthropic-skills:docx', label: 'docx', source: 'synced', level: 'mystery', issues: 'x' },
      { label: '沒有名稱的不列' }, null,
    ],
  });
  assert.equal(rows.length, 4);
  assert.deepEqual(rows[0].issues, [{ level: 'warn', text: '本文 600 行' }], '沒有文字的發現不列');
  assert.equal(rows[2].tokens, 1234);
  assert.equal(rows[2].listed, false);
  assert.equal(rows[3].level, 'info', '不認得的等級當說明');
  assert.deepEqual(rows[3].issues, []);
  assert.equal(rows[3].kind, 'skill');
  assert.deepEqual(rows.map(R.skillSourceLabel), ['自己的', '斜線指令', '外掛・p', 'claude.ai']);
  assert.equal(R.skillTokensText(1234), '約 1,234 token');
  assert.equal(R.skillTokensText(0), '');
  assert.deepEqual(R.skillCounts(rows), { all: 4, flagged: 1 });
  assert.deepEqual(R.filterSkills(rows, 'flagged', '').map((r) => r.name), ['mine']);
  assert.deepEqual(R.filterSkills(rows, 'all', 'CHARTS').map((r) => r.name), ['p:x'], '不分大小寫、比對用途');
  assert.deepEqual(R.filterSkills(rows, 'all', '外掛 draw').map((r) => r.name), ['p:x'], '每個字都要有，來源也算');
  assert.deepEqual(R.filterSkills(rows, 'all', '  ').length, 4);
  assert.deepEqual(R.filterSkills(rows, 'all', '600 行').map((r) => r.name), ['mine'], '發現的文字也算');
  assert.deepEqual(R.filterSkills(null, 'all', ''), []);
});

test('帳戶卡的設定資料夾：太長從中間省略、留最後兩層', () => {
  assert.equal(R.compactPath('C:\\Users\\Lin\\.claude'), 'C:\\Users\\Lin\\.claude');
  assert.equal(R.compactPath('C:\\Users\\Lin\\AppData\\Local\\Myriagent\\accounts\\0123456789abcdef0123456789abcdef'),
    'C:\\Users\\Lin\\…\\accounts\\0123456789abcdef0123456789abcdef');
  assert.equal(R.compactPath('C:\\Users\\Linda.Chen\\AppData\\Local\\Myriagent\\accounts\\0123456789abcdef0123456789abcdef'),
    'C:\\Users\\…\\accounts\\0123456789abcdef0123456789abcdef', '放不下使用者名稱就少留一層');
  assert.equal(R.compactPath('C:\\Users\\Lin\\AppData\\Local\\Myriagent\\accounts\\a2', 30), 'C:\\Users\\Lin\\…\\accounts\\a2');
  assert.equal(R.compactPath('/home/lin/.local/share/clode/accounts/a2', 30), '/home/lin/…/accounts/a2');
  assert.ok(R.compactPath('C:\\' + 'x'.repeat(80), 40).length <= 40);
  assert.equal(R.compactPath(null), '');
});
