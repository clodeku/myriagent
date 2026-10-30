// lib/stats-rules.mjs 的單元測試（node:test）：(cd tools/hub-e2e && node --test test/)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { idFromCommandId, parseStamps, percentile, sameOutcome, summarize } from '../lib/stats-rules.mjs';

const oneToHundred = Array.from({ length: 100 }, (_, i) => i + 1);

test('percentile_1到100的p50p95p99是50、95、99', () => {
  assert.equal(percentile(oneToHundred, 50), 50);
  assert.equal(percentile(oneToHundred, 95), 95);
  assert.equal(percentile(oneToHundred, 99), 99);
  assert.equal(percentile(oneToHundred, 100), 100);
});

test('percentile_不看輸入順序_nearest_rank', () => {
  const shuffled = [...oneToHundred].reverse();
  assert.equal(percentile(shuffled, 95), 95);
  // nearest-rank：10 個樣本的 p95 是第 ceil(9.5)=10 個（內插法會得到 9.55）
  assert.equal(percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 95), 10);
  assert.equal(percentile([7], 50), 7);
});

test('percentile_空陣列與不合格的p丟例外', () => {
  assert.throws(() => percentile([], 50), /沒有樣本/);
  assert.throws(() => percentile(undefined, 50), /沒有樣本/);
  assert.throws(() => percentile([1], 0), /p 要在/);
  assert.throws(() => percentile([1], 101), /p 要在/);
});

test('parseStamps_只認全形括號加剛好13位數字', () => {
  assert.deepEqual(parseStamps('a〔1760000000000〕b〔123〕c〔17600000000000〕'), [1760000000000]);
  assert.deepEqual(parseStamps('[1760000000000] (1760000000000) 1760000000000'), []);
  assert.deepEqual(parseStamps('〔1760000000001〕〔1760000000002〕'), [1760000000001, 1760000000002]);
  assert.deepEqual(parseStamps(''), []);
  assert.deepEqual(parseStamps(null), []);
});

test('summarize_計數極值平均與百分位數', () => {
  assert.deepEqual(summarize([]), { count: 0 });
  assert.deepEqual(summarize(null), { count: 0 });
  const s = summarize(oneToHundred);
  assert.deepEqual(s, { count: 100, min: 1, max: 100, mean: 50.5, p50: 50, p95: 95, p99: 99 });
  assert.equal(summarize([1, 'x', 3]).count, 2);
});

test('sameOutcome_不看時間耗時PID埠與鍵的順序', () => {
  const a = { ok: true, steps: [{ name: 'pair', ok: true, at: '2026-10-21T01:00:00Z', elapsedMs: 120 }], pid: 10, port: 5001, startedAt: 1 };
  const b = { startedAt: 2, port: 5002, pid: 11, steps: [{ elapsedMs: 340, at: '2026-10-21T02:00:00Z', ok: true, name: 'pair' }], ok: true };
  assert.equal(sameOutcome(a, b), true);
  assert.equal(sameOutcome(a, { ...b, ok: false }), false);
  assert.equal(sameOutcome(a, { ...b, steps: [{ name: 'pair', ok: false }] }), false);
  assert.equal(sameOutcome([1, 2], [2, 1]), false);
});

// 與 C# 的 RemoteConversationIdRulesTests 同一組向量（WP4.4 已合併，步 14 的「改成同一組」提前做）
const ULID_VECTORS = [
  ['01J9Q0000000000000000000AA', '01926e0000000000000000000000014a'],
  ['01ARZ3NDEKTSV4RRFFQ69G5FAV', '01563e3ab5d3d6764c61efb99302bd5b'],
  ['7ZZZZZZZZZZZZZZZZZZZZZZZZZ', 'ffffffffffffffffffffffffffffffff'],
  ['00000000000000000000000000', '00000000000000000000000000000000'],
];

test('idFromCommandId_與C#同一組固定向量', () => {
  for (const [cid, expected] of ULID_VECTORS) assert.equal(idFromCommandId(cid), expected, cid);
});

test('idFromCommandId_不是ULID回null_與C#同一組', () => {
  const invalid = [
    null,
    '',
    '01J9Q0000000000000000000A', // 25 字
    '01J9Q0000000000000000000AAA', // 27 字
    '81J9Q0000000000000000000AA', // 第一個字大於 7（超過 128 位元）
    '01J9Q0000000000000000000AI',
    '01J9Q0000000000000000000AL',
    '01J9Q0000000000000000000AO',
    '01J9Q0000000000000000000AU', // I、L、O、U 不在 Crockford 字母表
    '01j9q0000000000000000000aa', // 小寫
  ];
  for (const cid of invalid) assert.equal(idFromCommandId(cid), null, String(cid));
});
