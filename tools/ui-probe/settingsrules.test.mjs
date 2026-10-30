// 設定對話框的純規則（Web/settingsrules.js）：搜尋的拆詞、比對與排序、標出命中的字、說明的節錄、分頁列的方向鍵
import { test } from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const R = await import(pathToFileURL(path.join(here, '../../src/Myriagent/Web/settingsrules.js')).href);

test('searchTerms：空白（含全形空白）分開、全形英數轉半形、轉小寫；空字串是 []', () => {
  assert.deepEqual(R.searchTerms('  Dark　主題 '), ['dark', '主題']);
  assert.deepEqual(R.searchTerms('ＣＬＩ'), ['cli']);
  assert.deepEqual(R.searchTerms(''), []);
  assert.deepEqual(R.searchTerms('   '), []);
  assert.deepEqual(R.searchTerms(null), []);
});

test('scoreEntry：每個詞都要命中（AND），標籤最重、以這個詞開頭再加分；沒命中是 -1', () => {
  const e = { label: '主題', desc: '跟隨系統會依 Windows 的深淺色設定切換。', sec: '外觀', tab: '一般', keywords: 'dark light' };
  assert.equal(R.scoreEntry(e, ['主題']), 120);
  assert.equal(R.scoreEntry(e, ['dark']), 30);
  assert.equal(R.scoreEntry(e, ['windows']), 10);
  assert.equal(R.scoreEntry(e, ['外觀', 'dark']), 70);
  assert.equal(R.scoreEntry(e, ['主題', '不存在']), -1);
  assert.equal(R.scoreEntry(e, []), -1);
  // 欄位可省略
  assert.equal(R.scoreEntry({ label: 'Opus 5.5' }, ['opus']), 120);
  assert.equal(R.scoreEntry({}, ['x']), -1);
});

test('searchSettings：分數高的在前、同分照原本的順序、最多 limit 筆；空的搜尋字回 []', () => {
  const entries = [
    { id: 'a', label: '系統通知', desc: '代理提問時通知你' },
    { id: 'b', label: '語音通知', desc: '念一句說明' },
    { id: 'c', label: 'Enter 送出', desc: '關閉時改用 Ctrl+Enter' },
    { id: 'd', label: '遠端操作時在這台通知', desc: '' },
  ];
  assert.deepEqual(R.searchSettings(entries, '通知').map((e) => e.id), ['a', 'b', 'd']);
  assert.deepEqual(R.searchSettings(entries, '語音').map((e) => e.id), ['b']);
  assert.deepEqual(R.searchSettings(entries, 'enter').map((e) => e.id), ['c']);
  assert.deepEqual(R.searchSettings(entries, '通知', 2).map((e) => e.id), ['a', 'b']);
  assert.deepEqual(R.searchSettings(entries, ''), []);
  assert.deepEqual(R.searchSettings(null, '通知'), []);
  // 標籤開頭的排在只有說明命中的前面
  const mixed = [{ id: 'x', label: '模型', desc: '主模型過載時' }, { id: 'y', label: '主題', desc: '' }];
  assert.deepEqual(R.searchSettings(mixed, '主').map((e) => e.id), ['y', 'x']);
});

test('highlightParts：命中的地方切出來（不分大小寫、重疊與相鄰的合併），沒命中整段一塊', () => {
  assert.deepEqual(R.highlightParts('Opus 5.5', ['opus']), [{ text: 'Opus', hit: true }, { text: ' 5.5', hit: false }]);
  assert.deepEqual(R.highlightParts('額度用完時的額度', ['額度']),
    [{ text: '額度', hit: true }, { text: '用完時的', hit: false }, { text: '額度', hit: true }]);
  assert.deepEqual(R.highlightParts('abcdef', ['bcd', 'cde']), [{ text: 'a', hit: false }, { text: 'bcde', hit: true }, { text: 'f', hit: false }]);
  assert.deepEqual(R.highlightParts('abcd', ['ab', 'cd']), [{ text: 'abcd', hit: true }]);
  assert.deepEqual(R.highlightParts('主題', ['dark']), [{ text: '主題', hit: false }]);
  assert.deepEqual(R.highlightParts('主題', []), [{ text: '主題', hit: false }]);
  assert.deepEqual(R.highlightParts('', ['a']), []);
  // 轉小寫後長度會變的字（İ → i̇）：位置對不上，整段不標
  assert.deepEqual(R.highlightParts('İstanbul', ['stan']), [{ text: 'İstanbul', hit: false }]);
});

test('snippet：短的原樣；命中在前面時從頭截、在後面時從命中前 lead 字開始，截斷處加「…」', () => {
  assert.equal(R.snippet('短短一句', ['短']), '短短一句');
  const long = '一'.repeat(50) + '額度' + '二'.repeat(80);
  const s = R.snippet(long, ['額度'], 40, 10);
  assert.ok(s.startsWith('…') && s.endsWith('…'), s);
  assert.equal(s.length, 42);
  assert.ok(s.includes('額度'));
  const head = R.snippet('額度' + '三'.repeat(100), ['額度'], 40, 10);
  assert.ok(head.startsWith('額度') && head.endsWith('…'));
  assert.equal(R.snippet('四'.repeat(100), ['沒有'], 40, 10), '四'.repeat(40) + '…');
});

test('tabKeyTarget：上下左右都認、頭尾循環，Home／End 到頭尾，其他鍵是 null', () => {
  assert.equal(R.tabKeyTarget('ArrowDown', 0, 6), 1);
  assert.equal(R.tabKeyTarget('ArrowRight', 5, 6), 0);
  assert.equal(R.tabKeyTarget('ArrowUp', 0, 6), 5);
  assert.equal(R.tabKeyTarget('ArrowLeft', 3, 6), 2);
  assert.equal(R.tabKeyTarget('Home', 4, 6), 0);
  assert.equal(R.tabKeyTarget('End', 0, 6), 5);
  assert.equal(R.tabKeyTarget('Enter', 0, 6), null);
  assert.equal(R.tabKeyTarget('ArrowDown', 0, 0), null);
  // 目前的不在清單裡（-1）：往下到第一個
  assert.equal(R.tabKeyTarget('ArrowDown', -1, 4), 0);
});

test('SETTING_KEYWORDS：每個值都是非空字串、凍結', () => {
  assert.ok(Object.isFrozen(R.SETTING_KEYWORDS));
  for (const [k, v] of Object.entries(R.SETTING_KEYWORDS)) assert.ok(typeof v === 'string' && v.trim().length > 0, k);
  assert.ok(R.SETTING_KEYWORDS.theme.includes('dark'));
});

test('第九批：搜尋結果在各分頁的筆數', () => {
  assert.deepEqual(R.tabHitCounts([{ tabId: 'general' }, { tabId: 'models' }, { tabId: 'general' }, null, { tabId: 3 }]), { general: 2, models: 1 });
  assert.deepEqual(R.tabHitCounts(null), {});
});

test('normalizeHubUrl：補 https://、只留 origin；http、路徑、查詢字串、帳號密碼、空白都不收', () => {
  assert.equal(R.normalizeHubUrl('hub.example.com'), 'https://hub.example.com');
  assert.equal(R.normalizeHubUrl('  https://hub.example.com/  '), 'https://hub.example.com');
  assert.equal(R.normalizeHubUrl('https://HUB.Example.com:8443'), 'https://hub.example.com:8443');
  assert.equal(R.normalizeHubUrl('https://hub.example.com:443'), 'https://hub.example.com');
  for (const bad of ['', '   ', null, undefined, 'http://hub.example.com', 'https://hub.example.com/app', 'https://hub.example.com/?x=1',
    'https://hub.example.com/#a', 'https://me@hub.example.com', 'https://hub example.com', 'ftp://hub.example.com', 'https://']) {
    assert.equal(R.normalizeHubUrl(bad), null, String(bad));
  }
});
