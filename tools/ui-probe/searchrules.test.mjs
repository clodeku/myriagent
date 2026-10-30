// 側欄對話搜尋的純規則（src/Myriagent/Web/searchrules.js）：關鍵字的切法（與宿主 ConversationSearchRules.Terms 相同）、
// 標題比對、宿主結果的解讀、清單篩選、片段的標示段落，以及 searchrules.js 沒有 import、沒有 HTML 寫入點。
//   (cd tools/ui-probe && node --test searchrules.test.mjs)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'searchrules.js'), 'utf8');
const S = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// searchrules.test\n', 'utf8').toString('base64'));

test('沒有 import、沒有 HTML 寫入點', () => {
  assert.doesNotMatch(source, /^\s*import\s/m);
  assert.doesNotMatch(source, /innerHTML|outerHTML|insertAdjacentHTML|document\./);
});

test('關鍵字：空白分開（含全形空白與換行）、不分大小寫去重複、最多 8 個、整串先截 200 字', () => {
  assert.deepEqual(S.searchTerms(null), []);
  assert.deepEqual(S.searchTerms('  \t\n　'), []);
  assert.deepEqual(S.searchTerms(' 部署　失敗\nHUB-PC '), ['部署', '失敗', 'HUB-PC']);
  assert.deepEqual(S.searchTerms('API api Api key'), ['API', 'key']);
  assert.equal(S.searchTerms(Array.from({ length: 12 }, (_, i) => 't' + i).join(' ')).length, S.MAX_TERMS);
  assert.deepEqual(S.searchTerms('a'.repeat(S.MAX_QUERY) + ' 後面'), ['a'.repeat(S.MAX_QUERY)]);
});

test('鍵：同一組關鍵字（不分大小寫）的鍵相同，順序不同算不同；沒有關鍵字是空字串', () => {
  assert.equal(S.searchKey(S.searchTerms('Deploy  失敗')), S.searchKey(S.searchTerms('deploy 失敗 ')));
  assert.notEqual(S.searchKey(S.searchTerms('a b')), S.searchKey(S.searchTerms('b a')));
  assert.notEqual(S.searchKey(S.searchTerms('ab')), S.searchKey(S.searchTerms('a b')));
  assert.equal(S.searchKey([]), '');
  assert.equal(S.searchKey(null), '');
});

test('標題比對：每個關鍵字都要在標題、不分大小寫', () => {
  const t = S.searchTerms('myriagent 部署');
  assert.equal(S.titleMatches('部署 MyriagentHub', t), true);
  assert.equal(S.titleMatches('MyriagentHub', t), false);
  assert.equal(S.titleMatches(null, t), false);
  assert.equal(S.titleMatches('任何', []), true);
});

test('宿主結果：形狀不對的略過，空片段當 null', () => {
  const m = S.hitsFrom({ hits: [
    { conversationId: 'c1', snippet: '…片段…' },
    { conversationId: 'c2', snippet: null },
    { conversationId: 'c3', snippet: '' },
    { conversationId: 5 }, null, { snippet: 'x' },
  ] });
  assert.deepEqual([...m.entries()], [['c1', '…片段…'], ['c2', null], ['c3', null]]);
  assert.equal(S.hitsFrom(null).size, 0);
  assert.equal(S.hitsFrom({ hits: 'x' }).size, 0);
});

test('清單篩選：標題符合或內容符合（hits），順序照原清單；沒有關鍵字時全部', () => {
  const list = [{ id: 'a', title: '部署筆記' }, { id: 'b', title: '別的' }, { id: 'c', title: '再別的' }];
  const terms = S.searchTerms('部署');
  assert.deepEqual(S.filterConversations(list, terms, null).map((c) => c.id), ['a'], '內容的結果還沒來：只比標題');
  assert.deepEqual(S.filterConversations(list, terms, new Map([['c', '…部署…']])).map((c) => c.id), ['a', 'c']);
  assert.equal(S.filterConversations(list, [], null), list);
  assert.deepEqual(S.filterConversations(null, terms, null), []);
});

test('片段標示：不分大小寫、每個位置取最長的、接起來就是原文', () => {
  const parts = S.markParts('Run DOTNET build 與 dotnet test', S.searchTerms('dotnet do'));
  assert.deepEqual(parts, [
    { text: 'Run ', mark: false }, { text: 'DOTNET', mark: true }, { text: ' build 與 ', mark: false },
    { text: 'dotnet', mark: true }, { text: ' test', mark: false },
  ]);
  assert.deepEqual(S.markParts('沒有', S.searchTerms('x')), [{ text: '沒有', mark: false }]);
  assert.deepEqual(S.markParts('', ['x']), []);
  assert.deepEqual(S.markParts('文字', []), [{ text: '文字', mark: false }]);
  // 小寫後長度會變的字（İ）讓位置對不上：整段不標示
  assert.deepEqual(S.markParts('İstanbul ist', ['ist']), [{ text: 'İstanbul ist', mark: false }]);
});
