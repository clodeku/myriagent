// importrules.js 純函式單元測試：正規化、代理種類計數、多關鍵字搜尋篩選與工作資料夾簡稱
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  normalizeExternalItems,
  countExternalByProvider,
  filterExternalItems,
  formatExternalFolder,
} from '../../src/Myriagent/Web/importrules.js';

const SAMPLE = normalizeExternalItems([
  {
    sourceKey: 'k1',
    provider: 'Claude',
    accountId: 'a1',
    accountLabel: 'Claude（本機）',
    sessionId: '1111-2222',
    title: '修復登入權杖換發問題',
    workingDirectory: 'C:\\repo\\myriagent',
    updatedAt: 1700000000000,
    messageCount: 6,
  },
  {
    sourceKey: 'k2',
    provider: 'Antigravity',
    accountId: 'a2',
    accountLabel: 'Antigravity（本機）',
    sessionId: 'aaaa-bbbb',
    title: '效能分析與 Mermaid 圖表',
    workingDirectory: 'D:\\work\\frontend\\dashboard-ui',
    updatedAt: 1700000500000,
    messageCount: 4,
  },
  {
    sourceKey: 'k3',
    provider: 'Codex',
    accountId: 'a3',
    accountLabel: 'Codex（本機）',
    sessionId: 'cccc-dddd',
    title: '快取層單元測試',
    workingDirectory: null,
    updatedAt: 1700000200000,
    messageCount: 2,
  },
  { sourceKey: '', provider: 'Claude', sessionId: 'bad' },
  null,
]);

test('normalizeExternalItems 過濾無效項目並補齊預設欄位', () => {
  assert.equal(SAMPLE.length, 3);
  assert.equal(SAMPLE[2].workingDirectory, null);
});

test('countExternalByProvider 正確統計全部與各家筆數', () => {
  assert.deepEqual(countExternalByProvider(SAMPLE), {
    all: 3,
    Claude: 1,
    Codex: 1,
    Antigravity: 1,
    Grok: 0,
  });
});

test('filterExternalItems 支援依代理種類與多詞關鍵字篩選標題、資料夾及帳戶', () => {
  assert.equal(filterExternalItems(SAMPLE, 'all', '').length, 3);
  assert.deepEqual(filterExternalItems(SAMPLE, 'Antigravity', '').map((x) => x.sourceKey), ['k2']);
  assert.deepEqual(filterExternalItems(SAMPLE, 'all', 'mermaid dashboard').map((x) => x.sourceKey), ['k2']);
  assert.deepEqual(filterExternalItems(SAMPLE, 'Claude', 'myriagent 登入').map((x) => x.sourceKey), ['k1']);
  assert.deepEqual(filterExternalItems(SAMPLE, 'Grok', ''), []);
});

test('formatExternalFolder 取最後兩層資料夾名稱，空值回預設工作區', () => {
  assert.equal(formatExternalFolder(null), '預設工作區');
  assert.equal(formatExternalFolder(''), '預設工作區');
  assert.equal(formatExternalFolder('C:\\repo\\myriagent\\'), 'repo\\myriagent');
  assert.equal(formatExternalFolder('D:/work/frontend/dashboard-ui'), 'frontend\\dashboard-ui');
});
