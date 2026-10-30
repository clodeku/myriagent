// 差異檢視的純規則（src/Myriagent/Web/diffrules.js）：字詞拆分、刪除與新增行的配對、逐字比對的範圍與上限、
// 每一行的片段（語法上色在一段之內跨行接續、不跨 hunk；片段接起來一定等於原文，以固定種子的亂數差異對各語言各跑一批），
// 以及 diffrules.js 沒有 import、不碰 DOM，syntax.js 的差異上色只放 textContent。規則模組以 data: URL 匯入（同 syntaxrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const load = (name) => import('data:text/javascript;base64,' + Buffer.from(readFileSync(path.join(webDir, name), 'utf8') + '\n// diffrules.test\n', 'utf8').toString('base64'));
const D = await load('diffrules.js');
const S = await load('syntaxrules.js');
const tok = (lang) => (text) => S.tokenize(text, lang);
const L = (t, s) => ({ t, s });
const joined = (pieces) => pieces.map((p) => p[1]).join('');

test('字詞：字母數字底線連成一個、空白連成一個、其他符號各自一個；中文也算字', () => {
  assert.deepEqual(D.wordTokens('var count_1 = a+b;'), ['var', ' ', 'count_1', ' ', '=', ' ', 'a', '+', 'b', ';']);
  assert.deepEqual(D.wordTokens('顯示 標題'), ['顯示', ' ', '標題']);
  assert.deepEqual(D.wordTokens(''), []);
  assert.deepEqual(D.wordTokens(null), []);
});

test('配對：一段刪除緊接著一段新增，依序配到較少的那邊；中間隔了未變的行不配', () => {
  const lines = [L('ctx', 'a'), L('del', 'b1'), L('del', 'b2'), L('add', 'c1'), L('ctx', 'd'), L('del', 'e'), L('ctx', 'f'), L('add', 'g'), L('add', 'h1'), L('del', 'h2')];
  const p = D.pairLines(lines);
  assert.deepEqual([...p.entries()].sort((x, y) => x[0] - y[0]), [[1, 3], [3, 1]]);
  const q = D.pairLines([L('del', 'a'), L('add', 'b'), L('add', 'c')]);
  assert.deepEqual([...q.entries()].sort((x, y) => x[0] - y[0]), [[0, 1], [1, 0]]);
  assert.equal(D.pairLines([]).size, 0);
});

test('逐字比對：只標改了的字詞；一樣、整行重寫、只剩空白相同、太長時不標', () => {
  assert.deepEqual(D.intraRanges('var count = 1;', 'var total = 10;'), { a: [[4, 9], [12, 13]], b: [[4, 9], [12, 14]] });
  assert.deepEqual(D.intraRanges('return a;', 'return a + b;'), { a: [], b: [[8, 12]] }, '只有新增的字');
  assert.equal(D.intraRanges('same', 'same'), null);
  assert.equal(D.intraRanges('alpha beta gamma', 'one two three'), null, '沒有共同的字詞（只有空白）');
  assert.equal(D.intraRanges('a = 1;', 'completely(different, "text") + more;'), null, '改動超過六成');
  const long = 'x '.repeat(D.INTRA_MAX_LINE);
  assert.equal(D.intraRanges(long, long + 'y'), null);
  assert.equal(D.INTRA_MAX_RATIO, 0.6);
});

test('逐字比對：字詞格數超過上限時改成去掉共同的開頭與結尾', () => {
  // 很多短字詞（每個字母與 + 各算一個）：長度在單行上限內，字詞格數超過上限
  const a = 'a+'.repeat(60) + 'OLD' + '+b'.repeat(45), b = 'a+'.repeat(60) + 'NEW' + '+b'.repeat(45);
  assert.ok(a.length <= D.INTRA_MAX_LINE && D.wordTokens(a).length * D.wordTokens(b).length > D.INTRA_MAX_CELLS);
  const r = D.intraRanges(a, b);
  assert.deepEqual(r.a.map(([s, e]) => a.slice(s, e)), ['OLD']);
  assert.deepEqual(r.b.map(([s, e]) => b.slice(s, e)), ['NEW']);
});

test('片段：hunk 整行不上色；跨行的註解在一段之內接續、不跨 hunk；配對的行標出改了的字', () => {
  const lines = [L('hunk', '@@ -1 +1 @@'), L('ctx', '/* 開頭'), L('ctx', '   註解 */'), L('del', 'var count = 1;'), L('add', 'var total = 10;'),
    L('hunk', '⋯ 未變更 3 行'), L('ctx', '   不是註解 */ var x;')];
  const segs = D.diffSegments(lines, tok('cs'));
  assert.deepEqual(segs[0], [[null, '@@ -1 +1 @@', false]]);
  assert.deepEqual(segs[1], [['com', '/* 開頭', false]]);
  assert.deepEqual(segs[2], [['com', '   註解 */', false]], '跨行的註解接續');
  assert.deepEqual(segs[3].filter((p) => p[2]).map((p) => p[1]), ['count', '1']);
  assert.deepEqual(segs[4].filter((p) => p[2]).map((p) => p[1]), ['total', '10']);
  assert.ok(segs[3].some(([t, s]) => t === 'kw' && s === 'var'), '刪除行也上色');
  assert.ok(!segs[6].some(([t]) => t === 'com'), 'hunk 之後重新開始，不接上前一段的註解');
  segs.forEach((p, i) => assert.equal(joined(p), lines[i].s, '第 ' + i + ' 行'));
});

test('片段：認不得語言時只標改了的字；什麼都沒得加時回 null；空行是空陣列', () => {
  const lines = [L('del', 'name: old'), L('add', 'name: new'), L('ctx', '')];
  const segs = D.diffSegments(lines, null);
  assert.deepEqual(segs[0], [[null, 'name: ', false], [null, 'old', true]]);
  assert.deepEqual(segs[1], [[null, 'name: ', false], [null, 'new', true]]);
  assert.deepEqual(segs[2], []);
  assert.equal(D.diffSegments([L('ctx', 'a'), L('add', 'b')], null), null);
  assert.equal(D.diffSegments([], null), null);
});

test('不變式：固定種子的亂數差異，各語言的每一行片段接起來都等於原文、沒有空片段、類別都在 TOKEN_TYPES 裡', () => {
  let seed = 20261024;
  const rnd = () => ((seed = (seed * 1103515245 + 12345) >>> 0) / 2 ** 32);
  const alphabet = ['var', 'x', ' ', '"', "'", '/*', '*/', '//', '#', '<', '>', '=', '1', '\t', '中文', '$a', '`', '{', '}', '\\', 'ab', 'cd'];
  const word = () => Array.from({ length: 1 + Math.floor(rnd() * 12) }, () => alphabet[Math.floor(rnd() * alphabet.length)]).join('');
  const kinds = ['ctx', 'ctx', 'del', 'add', 'add', 'hunk'];
  for (const lang of ['cs', 'js', 'python', 'json', 'shell', 'powershell', 'css', 'html', 'sql', 'yaml', null]) {
    for (let run = 0; run < 25; run++) {
      const lines = Array.from({ length: 2 + Math.floor(rnd() * 14) }, () => L(kinds[Math.floor(rnd() * kinds.length)], word()));
      const segs = D.diffSegments(lines, lang ? tok(lang) : null);
      if (!segs) continue;
      segs.forEach((p, i) => {
        assert.equal(joined(p), lines[i].s, lang + ' 第 ' + i + ' 行');
        for (const [t, s, c] of p) {
          assert.ok(s.length > 0, '沒有空片段');
          assert.ok(t === null || S.TOKEN_TYPES.includes(t), String(t));
          assert.equal(typeof c, 'boolean');
        }
      });
    }
  }
});

test('diffrules.js 沒有 import、不碰 DOM；syntax.js 的差異上色只放 textContent', () => {
  const src = readFileSync(path.join(webDir, 'diffrules.js'), 'utf8');
  assert.ok(!/^\s*import\s/m.test(src), '沒有 import');
  assert.ok(!/\b(document|window|innerHTML|insertAdjacentHTML)\b/.test(src), '不碰 DOM');
  const syn = readFileSync(path.join(webDir, 'syntax.js'), 'utf8');
  assert.ok(!/innerHTML|insertAdjacentHTML|outerHTML/.test(syn));
});
