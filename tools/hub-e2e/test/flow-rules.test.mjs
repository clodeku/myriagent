// lib/flow-rules.mjs 的單元測試（node:test；WP4.9 步 15）：S7 的樣本與判定、回補的時間窗、抽樣、id 與 Ord 的比對、計數、ULID、位元組搜尋。
// 不啟動任何程序、不開瀏覽器、不碰檔案
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  backfillWindow, compareIdOrd, containsSecret, countBy, counterDelta, judgeS7, latencySamples, multisetDiff, multisetIncludes, pickEvenly, resultsPerCommand,
  S7_P95_MS, S7_P99_MS, ulid, unexpectedDrops, worstSamples,
} from '../lib/flow-rules.mjs';
import { idFromCommandId } from '../lib/stats-rules.mjs';

const rec = (c, s, o = {}) => ({ c, s: String(s), ws: null, wsDone: null, dom: null, domDone: null, ...o });

test('latencySamples_即時看第一次出現_持久看第一次出現在完成的項目裡', () => {
  const records = [
    rec('a', 1000, { ws: 1100, wsDone: 1900, dom: 1150, domDone: 1950 }),
    rec('a', 2000, { ws: 2050, wsDone: 2600, dom: 2080 }),
    rec('b', 3000, { ws: 3010, wsDone: 3020, dom: 3030, domDone: 3040 }),
  ];
  assert.deepEqual(latencySamples(records, { path: 'live', source: 'ws' }), { samples: [100, 50, 10], missing: 0 });
  assert.deepEqual(latencySamples(records, { path: 'live', source: 'dom' }), { samples: [150, 80, 30], missing: 0 });
  assert.deepEqual(latencySamples(records, { path: 'persistent', source: 'ws' }), { samples: [900, 600, 20], missing: 0 });
  assert.deepEqual(latencySamples(records, { path: 'persistent', source: 'dom' }), { samples: [950, 40], missing: 1 }, '沒顯示成完成的算 missing、不算樣本');
  assert.deepEqual(latencySamples(records, { path: 'live', source: 'ws', conversations: ['b'] }).samples, [10]);
});

test('latencySamples_時間窗以標記的時間算_null是那一邊不限_不合格的參數丟例外', () => {
  const records = [rec('a', 1000, { ws: 1010 }), rec('a', 2000, { ws: 2010 }), rec('a', 3000, { ws: 3010 }), rec('a', 'x', { ws: 1 })];
  assert.deepEqual(latencySamples(records, { path: 'live', source: 'ws', window: { from: 1500, to: 2500 } }).samples, [10]);
  assert.deepEqual(latencySamples(records, { path: 'live', source: 'ws', window: { from: 2000, to: null } }).samples, [10, 10], '到最後都還在回補（to 是 null）');
  assert.deepEqual(latencySamples(records, { path: 'live', source: 'ws', window: { from: null, to: 1000 } }).samples, [10], '邊界算在裡面');
  assert.equal(latencySamples(records, { path: 'live', source: 'ws' }).samples.length, 3, '不是數字的標記不算');
  assert.throws(() => latencySamples(records, { path: 'both', source: 'ws' }), /path/);
  assert.throws(() => latencySamples(records, { path: 'live', source: 'paint' }), /source/);
  assert.deepEqual(latencySamples(null, { path: 'live', source: 'ws' }), { samples: [], missing: 0 });
});

test('worstSamples_同樣的條件_延遲最大的n個', () => {
  const records = [rec('a', 1000, { ws: 1300 }), rec('a', 2000, { ws: 2100 }), rec('b', 3000, { ws: 3300 }), rec('a', 4000, { ws: 4900 }), rec('a', 5000)];
  assert.deepEqual(worstSamples(records, { path: 'live', source: 'ws' }, 3), [{ c: 'a', s: 4000, ms: 900 }, { c: 'a', s: 1000, ms: 300 }, { c: 'b', s: 3000, ms: 300 }],
    '同樣大時標記早的在前；沒有時間的不算');
  assert.deepEqual(worstSamples(records, { path: 'live', source: 'ws', conversations: ['a'], window: { from: 1500, to: null } }, 5).map((w) => w.s), [4000, 2000]);
  assert.deepEqual(worstSamples(records, { path: 'persistent', source: 'ws' }), []);
});

test('judgeS7_p95與p99剛好等於門檻算通過_超過或樣本不足或有missing不通過', () => {
  assert.equal(S7_P95_MS, 500);
  assert.equal(S7_P99_MS, 1000);
  // 100 個樣本：p95 是排序後第 95 個、p99 是第 99 個
  const at = (p95, p99) => [...Array(94).fill(10), p95, p95, p95, p95, p99, p99];
  const edge = judgeS7({ samples: at(500, 1000), missing: 0 });
  assert.equal(edge.ok, true);
  assert.deepEqual([edge.summary.p95, edge.summary.p99], [500, 1000]);
  const over95 = judgeS7({ samples: at(501, 1000), missing: 0 });
  assert.equal(over95.ok, false);
  assert.match(over95.reason, /p95 501/);
  const over99 = judgeS7({ samples: [...Array(98).fill(100), 1001, 1001], missing: 0 });
  assert.equal(over99.ok, false);
  assert.match(over99.reason, /p99 1001/);
  assert.match(judgeS7({ samples: Array(19).fill(1), missing: 0 }).reason, /樣本只有 19 個/);
  assert.equal(judgeS7({ samples: Array(5).fill(1), missing: 0 }, { minSamples: 5 }).ok, true);
  assert.match(judgeS7({ samples: Array(30).fill(1), missing: 2 }).reason, /2 個標記/);
  assert.equal(judgeS7({ samples: Array(30).fill(1), missing: 0 }).summary.count, 30);
});

test('backfillWindow_第一個還在回補的取樣到之後第一個不在回補的取樣', () => {
  assert.deepEqual(backfillWindow([{ t: 1, left: null }, { t: 2, left: 300 }, { t: 3, left: 0 }, { t: 4, left: 120 }, { t: 5, left: null }, { t: 6, left: 9 }]), { from: 2, to: 5 },
    'left 是 0 還算在回補；只取第一段');
  assert.deepEqual(backfillWindow([{ t: 1, left: 5 }, { t: 2, left: 4 }]), { from: 1, to: null }, '到最後都還在回補');
  assert.equal(backfillWindow([{ t: 1, left: null }, { t: 2, left: 0 }]), null, '從沒看到 left > 0');
  assert.equal(backfillWindow([]), null);
  assert.equal(backfillWindow(undefined), null);
});

test('pickEvenly_平均挑_不夠就全部_保持順序', () => {
  const list = Array.from({ length: 10 }, (_, i) => i);
  assert.deepEqual(pickEvenly(list, 5), [0, 2, 4, 6, 8]);
  assert.deepEqual(pickEvenly(list, 3), [0, 3, 6]);
  assert.deepEqual(pickEvenly(list, 10), list);
  assert.deepEqual(pickEvenly(list, 20), list);
  assert.deepEqual(pickEvenly(list, 0), []);
  assert.deepEqual(pickEvenly(null, 3), []);
  const many = pickEvenly(Array.from({ length: 300 }, (_, i) => i), 15);
  assert.equal(new Set(many).size, 15);
  assert.ok(many.at(-1) >= 280, '涵蓋到清單的尾端（FF1：超過 90 天的那段）');
});

test('compareIdOrd_id與Ord的集合相同才算_列出不同的地方', () => {
  const dev = [{ id: 'a', ord: 1 }, { id: 'b', ord: 2 }, { id: 'c', ord: 3 }];
  assert.equal(compareIdOrd([{ id: 'c', seq: 3 }, { id: 'a', seq: 1 }, { id: 'b', seq: 2 }], dev).equal, true, '順序不管');
  const r = compareIdOrd([{ id: 'a', seq: 1 }, { id: 'b', seq: 5 }, { id: 'x', seq: 9 }], dev);
  assert.equal(r.equal, false);
  assert.deepEqual(r.onlyBrowser, ['x']);
  assert.deepEqual(r.onlyDevice, ['c']);
  assert.deepEqual(r.ordMismatch, [{ id: 'b', browser: 5, device: 2 }]);
  assert.deepEqual(compareIdOrd([{ id: 'a', seq: 1 }, { id: 'a', seq: 1 }], [{ id: 'a', ord: 1 }]).duplicates, ['a']);
  assert.equal(compareIdOrd([{ id: 'a', seq: 1 }, { id: 'a', seq: 1 }], [{ id: 'a', ord: 1 }]).equal, false, '瀏覽器拿到重複的不算相同');
  const noOrd = compareIdOrd([{ id: 'a', seq: null }], [{ id: 'a', ord: null }]);
  assert.equal(noOrd.withoutOrd, 1);
  assert.equal(noOrd.equal, false, '假裝置還有沒配 Ord 的項目不算');
  assert.equal(compareIdOrd([], []).equal, false, '兩邊都空不算（沒有讀到東西）');
  assert.equal(compareIdOrd(Array.from({ length: 30 }, (_, i) => ({ id: `b${i}`, seq: i })), []).onlyBrowser.length, 10, '最多列 10 筆');
});

test('countBy_multisetDiff_multisetIncludes', () => {
  assert.deepEqual(countBy([{ k: 'b' }, { k: 'a' }, { k: 'b' }], 'k'), { a: 1, b: 2 });
  assert.deepEqual(Object.keys(countBy([{ k: 'b' }, { k: 'a' }], 'k')), ['a', 'b'], '鍵依字典順序');
  assert.deepEqual(countBy([1, 2, 3], (x) => (x % 2 ? 'odd' : 'even')), { even: 1, odd: 2 });
  assert.deepEqual(countBy(null, 'k'), {});
  assert.deepEqual(multisetDiff({ a: 2, b: 1 }, { a: 1, c: 1 }), { equal: false, onlyA: { a: 1, b: 1 }, onlyB: { c: 1 } });
  assert.equal(multisetDiff({ a: 1 }, { a: 1 }).equal, true);
  assert.deepEqual(multisetIncludes({ a: 2, b: 1 }, { a: 1 }), { ok: true, missing: {} });
  assert.deepEqual(multisetIncludes({ a: 1 }, { a: 2, c: 1 }), { ok: false, missing: { a: 1, c: 1 } });
});

test('resultsPerCommand_每個commandId最多幾則', () => {
  assert.deepEqual(resultsPerCommand([{ commandId: 'x' }, { commandId: 'y' }]), { max: 1, over: [], per: { x: 1, y: 1 } });
  const r = resultsPerCommand([{ commandId: 'x' }, { commandId: 'x' }, { commandId: 'y' }]);
  assert.equal(r.max, 2);
  assert.deepEqual(r.over, ['x']);
  assert.equal(resultsPerCommand([]).max, 0);
});

test('counterDelta_與unexpectedDrops_只有final與stale是預期的', () => {
  const before = { rate: 0, dlp: 0, final: 3, stale: 1 };
  const after = { rate: 2, dlp: 0, final: 10, stale: 1, notOwner: 1 };
  const d = counterDelta(before, after);
  assert.deepEqual(d, { rate: 2, dlp: 0, final: 7, stale: 0, notOwner: 1 });
  assert.deepEqual(unexpectedDrops(d), [{ reason: 'rate', count: 2 }, { reason: 'notOwner', count: 1 }]);
  assert.deepEqual(unexpectedDrops({ final: 9, stale: 4 }), []);
  assert.deepEqual(unexpectedDrops({ rate: 1 }, ['rate']), []);
  assert.deepEqual(counterDelta(null, { sends: 3 }), { sends: 3 }, '之前沒有讀到的當 0');
});

test('ulid_時間在前10字_亂數在後16字_和idFromCommandId對得上', () => {
  const zero = ulid(0, Array(10).fill(0));
  assert.equal(zero, '0'.repeat(26));
  assert.equal(ulid(2 ** 48 - 1, Array(10).fill(255)), '7' + 'Z'.repeat(25), '最大的 ULID 第一個字是 7');
  const id = ulid(1_760_000_000_000, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
  assert.match(id, /^[0-9A-HJKMNP-TV-Z]{26}$/u);
  assert.equal(idFromCommandId(id), '0199c82cc0000102030405060708090a', '128 位元依大端：前 48 位元是時間');
  assert.ok(ulid(1000, Array(10).fill(0)) < ulid(1001, Array(10).fill(0)), '時間先後就是字串先後');
  assert.throws(() => ulid(-1, Array(10).fill(0)), /48 位元/);
  assert.throws(() => ulid(1.5, Array(10).fill(0)), /48 位元/);
  assert.throws(() => ulid(2 ** 48, Array(10).fill(0)), /48 位元/);
  assert.throws(() => ulid(1, Array(9).fill(0)), /10 個位元組/);
  assert.throws(() => ulid(1, [...Array(9).fill(0), 256]), /10 個位元組/);
});

test('containsSecret_UTF8與UTF16LE分開回報', () => {
  const s = '秘密回答-ab12';
  assert.deepEqual(containsSecret(Buffer.concat([Buffer.from('x'), Buffer.from(s, 'utf8'), Buffer.from('y')]), s), { utf8: true, utf16: false });
  assert.deepEqual(containsSecret(Buffer.concat([Buffer.from([0]), Buffer.from(s, 'utf16le')]), s), { utf8: false, utf16: true });
  assert.deepEqual(containsSecret(Buffer.from('秘密回答-ab13'), s), { utf8: false, utf16: false });
  assert.throws(() => containsSecret(Buffer.from('x'), ''), /字串/);
});
