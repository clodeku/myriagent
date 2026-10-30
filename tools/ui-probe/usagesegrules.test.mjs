// 儀表板「每週額度每 5% 的 token」的純規則（src/Myriagent/Web/usagesegrules.js）：宿主資料的正規化、百分比與期間的字、各模型相加、
// Claude 的每週・模型只算名稱相符的模型、CSV（每筆每模型一列、跳脫）、彙總的滑過說明，以及規則模組沒有 import、沒有 HTML 寫入點。
// (cd tools/ui-probe && node --test usagesegrules.test.mjs)
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'usagesegrules.js'), 'utf8');
const R = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// usagesegrules.test\n', 'utf8').toString('base64'));

test('規則模組沒有 import、沒有 HTML 寫入點', () => {
  assert.doesNotMatch(source, /^\s*import\s/m);
  assert.doesNotMatch(source, /innerHTML|outerHTML|insertAdjacentHTML|document\.write/);
});

test('宿主資料：不是物件回 null，陣列一律存在、濾掉不是物件的', () => {
  assert.equal(R.segView(null), null);
  assert.equal(R.segView('x'), null);
  const v = R.segView({ path: 'C:\\d\\records.jsonl', total: 3.7, summary: [{ a: 1 }, null, 3], records: 'x' });
  assert.deepEqual(v, { path: 'C:\\d\\records.jsonl', total: 3, summary: [{ a: 1 }], open: [], records: [] });
});

test('百分比、區間、實際看到的使用率、期間', () => {
  assert.equal(R.pct(15), '15%');
  assert.equal(R.pct(12.34), '12.3%');
  assert.equal(R.pct(undefined), '0%');
  assert.equal(R.rangeText({ fromBucket: 10, toBucket: 15 }), '10% → 15%');
  assert.equal(R.observedText({ startPercent: 12.3, endPercent: 15.2 }), '實際 12.3% → 15.2%（+2.9）');
  assert.equal(R.observedText({ startPercent: 15, endPercent: 20 }), '實際 15% → 20%（+5）');
  assert.equal(R.durationText(30e3), '不到 1 分');
  assert.equal(R.durationText(12 * 60e3), '12 分');
  assert.equal(R.durationText((2 * 60 + 5) * 60e3), '2 小時 5 分');
  assert.equal(R.durationText((3 * 1440 + 4 * 60 + 9) * 60e3), '3 天 4 小時');
});

test('各模型相加；不是正數的當 0', () => {
  assert.deepEqual(R.totalOf([{ input: 10, cached: 4, cacheWrite: 2, output: 3 }, { input: 5, output: -1 }, null]), { input: 15, cached: 4, cacheWrite: 2, output: 3 });
  assert.deepEqual(R.totalOf(undefined), { input: 0, cached: 0, cacheWrite: 0, output: 0 });
});

test('Claude 的每週・模型只算名稱相符的模型；其他額度窗都算', () => {
  assert.equal(R.modelCounts('weekly:Opus', 'claude-opus-5-5'), true);
  assert.equal(R.modelCounts('weekly:Opus', 'claude-sonnet-5-5'), false);
  assert.equal(R.modelCounts('weekly:Sonnet', '（混用）'), false);
  assert.equal(R.modelCounts('weekly', 'claude-sonnet-5-5'), true);
  assert.equal(R.modelCounts('codex:10080', 'gpt-6-astra'), true);
});

test('CSV：每筆紀錄的每個模型一列、表頭、跳脫逗號與引號、數字不加千分位', () => {
  const at = new Date(2026, 9, 25, 14, 3).getTime();
  const csv = R.csvOf([{
    startedAt: at - 3600e3, endedAt: at, accountLabel: 'Claude, "甲"', provider: 'Claude', windowLabel: '每週・Opus', fromBucket: 10, toBucket: 15,
    startPercent: 12.3, endPercent: 15.2, complete: false,
    models: [{ model: 'claude-opus-5-5', input: 1234567, cached: 1000000, cacheWrite: 5000, output: 4321 }, { model: 'claude-haiku-5-5', input: 10, output: 2 }],
  }]);
  const lines = csv.trimEnd().split('\r\n');
  assert.equal(lines.length, 3);
  assert.equal(lines[0], R.CSV_HEADER.join(','));
  assert.equal(lines[1], '2026-10-25 13:03,2026-10-25 14:03,"Claude, ""甲""",Claude,每週・Opus,10,15,12.3,15.2,否,claude-opus-5-5,1234567,1000000,5000,4321');
  assert.match(lines[2], /,claude-haiku-5-5,10,0,0,2$/);
  assert.equal(R.csvOf([]), R.CSV_HEADER.join(',') + '\r\n');
});

test('彙總的滑過說明：段數、實際前進的百分點、合計與每 5% 的範圍', () => {
  const tip = R.summaryTip({ records: 6, percent: 30.4, tokens: { input: 123456789, cached: 120000000, cacheWrite: 1000, output: 98765 }, minPer5: 10000000, maxPer5: 30000000 });
  assert.match(tip, /^6 段完整的紀錄，實際前進 30\.4%/);
  assert.match(tip, /輸入 123,456,789（快取讀取 120,000,000、快取寫入 1,000）、輸出 98,765/);
  assert.match(tip, /最少 10,000,000、最多 30,000,000/);
});

test('第九批：比例底條、模型組成、正在記的那一段的進度', () => {
  assert.deepEqual(R.barFracs([50, 100, 0, undefined]), [0.5, 1, 0, 0]);
  assert.deepEqual(R.barFracs([0, 0]), [0, 0], '都是 0');
  assert.deepEqual(R.barFracs(null), []);
  assert.deepEqual(['claude-opus-5-5', 'claude-sonnet-5-5', 'claude-haiku-5-5', 'claude-fable-5-1', 'gpt-6-astra', 'gpt-5.3-codex', 'o4-mini', 'gemini-3-pro', 'grok-5', 'x'].map(R.modelTone),
    ['opus', 'sonnet', 'haiku', 'fable', 'gpt', 'gpt', 'gpt', 'gemini', 'grok', 'other']);
  const sh = R.modelShares([{ model: 'claude-haiku-5-5', input: 20, output: 5 }, { model: 'claude-opus-5-5', input: 70, output: 5 }, { model: 'zero', input: 0, output: 0 }, null]);
  assert.deepEqual(sh.map((s) => [s.model, s.frac, s.tone]), [['claude-opus-5-5', 0.75, 'opus'], ['claude-haiku-5-5', 0.25, 'haiku']]);
  assert.equal(R.sharesTip(sh), 'claude-opus-5-5 75%\nclaude-haiku-5-5 25%');
  assert.deepEqual(R.modelShares([]), []);
  assert.equal(R.segProgress({ bucket: 45, lastPercent: 47 }), 0.4);
  assert.equal(R.segProgress({ bucket: 20, lastPercent: 23.4 }).toFixed(2), '0.68');
  assert.equal(R.segProgress({ bucket: 45, lastPercent: 51 }), 1);
  assert.equal(R.segProgress({ bucket: 45, lastPercent: 40 }), 0);
  assert.equal(R.segProgress(null), 0);
});
