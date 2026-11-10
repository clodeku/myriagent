// 臨時對話的純規則（src/Myriagent/Web/quickchatrules.js）：面板的位置與大小、朗讀的分段。
// 規則模組沒有 import，以 data: URL 匯入（同 tabdragrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const source = readFileSync(path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web', 'quickchatrules.js'), 'utf8');
const Q = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// quickchatrules.test\n', 'utf8').toString('base64'));

test('沒存過：靠右上角、寬 420、高為視窗的 70%、在頂端列下面', () => {
  const r = Q.panelRect(null, 1440, 900, 48);
  assert.deepEqual(r, { x: 1440 - 420 - 12, y: 52, w: 420, h: 630, full: false });
});

test('存下的照用、拉回視窗裡；太小的放大到最小尺寸', () => {
  assert.deepEqual(Q.panelRect({ x: 100, y: 200, w: 500, h: 500 }, 1440, 900, 48), { x: 100, y: 200, w: 500, h: 500, full: false });
  const out = Q.panelRect({ x: 5000, y: -50, w: 500, h: 500 }, 1440, 900, 48);
  assert.equal(out.x, 1440 - 500 - 12, '右邊超出拉回來');
  assert.equal(out.y, 48, '不蓋到頂端列');
  const small = Q.panelRect({ x: 100, y: 100, w: 10, h: 10 }, 1440, 900, 48);
  assert.equal(small.w, Q.QUICK_MIN_W);
  assert.equal(small.h, Q.QUICK_MIN_H);
  const big = Q.panelRect({ x: 0, y: 0, w: 5000, h: 5000 }, 1440, 900, 48);
  assert.ok(big.w <= 1440 - 24 && big.h <= 900 - 48 - 12, '不超過視窗');
});

test('視窗太小（手機）：鋪滿頂端列以下', () => {
  assert.deepEqual(Q.panelRect({ x: 10, y: 10, w: 400, h: 400 }, 360, 700, 48), { x: 0, y: 48, w: 360, h: 652, full: true });
});

test('朗讀的分段：照句子切、每段不超過上限、空白的丟掉', () => {
  const chunks = Q.speechChunks('第一句。第二句！\n\n第三句？', 160);
  assert.deepEqual(chunks, ['第一句。 第二句！ 第三句？']);
  const many = Q.speechChunks('甲。'.repeat(100), 20);
  assert.ok(many.length > 1 && many.every((c) => c.length <= 20));
  assert.equal(many.join('').replace(/ /g, ''), '甲。'.repeat(100));
  const long = Q.speechChunks('一，'.repeat(50) + '結束', 30);
  assert.ok(long.every((c) => c.length <= 30) && long.join('').includes('結束'));
  const noBreak = Q.speechChunks('字'.repeat(70), 30);
  assert.deepEqual(noBreak.map((c) => c.length), [30, 30, 10]);
  assert.deepEqual(Q.speechChunks('   \n  ', 30), []);
  assert.deepEqual(Q.speechChunks(null), []);
});
