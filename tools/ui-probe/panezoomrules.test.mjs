// 點了暫時放大窗格的純規則（src/Myriagent/Web/panezoomrules.js）：拉長或加寬、兩倍、不超過工作區、下面不夠往上補。
// 規則模組沒有 import，以 data: URL 匯入（同 tabdragrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const source = readFileSync(path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web', 'panezoomrules.js'), 'utf8');
const Z = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// panezoomrules.test\n', 'utf8').toString('base64'));

const ws = { top: 40, bottom: 840, left: 260, right: 1240, width: 980, height: 800 };
const rect = (left, top, width, height) => ({ left, top, width, height, right: left + width, bottom: top + height });

test('上下兩列：上面那格往下拉長成兩倍（剛好到底）', () => {
  assert.deepEqual(Z.zoomPlan(rect(260, 40, 490, 400), ws), { axis: 'y', size: 800, shift: 0 });
});

test('下面那格：下面沒有空間，往上補', () => {
  assert.deepEqual(Z.zoomPlan(rect(260, 440, 490, 400), ws), { axis: 'y', size: 800, shift: 400 });
});

test('三列的中間那格：兩倍、下面不夠的往上補，不超過工作區', () => {
  assert.deepEqual(Z.zoomPlan(rect(260, 307, 490, 266), ws), { axis: 'y', size: 532, shift: 0 });
  assert.deepEqual(Z.zoomPlan(rect(260, 574, 490, 266), ws), { axis: 'y', size: 532, shift: 266 }, '最下面那列');
  const tall = Z.zoomPlan(rect(260, 40, 490, 500), ws);
  assert.deepEqual(tall, { axis: 'y', size: 800, shift: 0 }, '兩倍會超過：只到工作區的高度');
});

test('只有一列並排（已經滿高）：改成加寬，右邊不夠往左補', () => {
  assert.deepEqual(Z.zoomPlan(rect(260, 40, 490, 800), ws), { axis: 'x', size: 980, shift: 0 });
  assert.deepEqual(Z.zoomPlan(rect(751, 40, 489, 800), ws), { axis: 'x', size: 978, shift: 489 });
  assert.deepEqual(Z.zoomPlan(rect(260, 40, 326, 800), ws), { axis: 'x', size: 652, shift: 0 }, '三格並排：兩倍');
});

test('只有一個窗格（兩個方向都佔滿）、看不到（寬高是 0）、沒有資料：不放大', () => {
  assert.equal(Z.zoomPlan(rect(260, 40, 980, 800), ws), null);
  assert.equal(Z.zoomPlan(rect(260, 40, 979, 799), ws), null, '格線間隔的誤差也算佔滿');
  assert.equal(Z.zoomPlan(rect(0, 0, 0, 0), ws), null);
  assert.equal(Z.zoomPlan(null, ws), null);
});

test('倍數：預設兩倍，不合理的倍數照預設', () => {
  assert.equal(Z.PANE_ZOOM, 2);
  assert.deepEqual(Z.zoomPlan(rect(260, 40, 490, 200), ws, 3), { axis: 'y', size: 600, shift: 0 });
  assert.deepEqual(Z.zoomPlan(rect(260, 40, 490, 200), ws, 0.5), { axis: 'y', size: 400, shift: 0 });
});
