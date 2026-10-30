// 分頁拖放的純規則（src/Myriagent/Web/tabdragrules.js）：拖曳期間要不要先排著畫面更新（含到期與時鐘往回跳）、
// 放開在分頁列的第幾個位置、插入記號的 x，以及 tabdragrules.js 沒有 import。規則模組沒有 import，以 data: URL 匯入（同 gitrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'tabdragrules.js'), 'utf8');
const R = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// tabdragrules.test\n', 'utf8').toString('base64'));

test('tabdragrules.js 沒有 import（純規則）', () => {
  assert.doesNotMatch(source, /^\s*import\s/m);
});

test('沒有在拖：不排著', () => {
  assert.deepEqual(R.tabDragHold(null, null, 1000), { active: false, recheckMs: 0 });
  assert.deepEqual(R.tabDragHold(undefined, undefined, 1000), { active: false, recheckMs: 0 });
});

test('這個視窗自己拖的：上限內排著，到期前再看一次；過了上限就不排著（dragend 漏了也會補上畫面）', () => {
  const t0 = 50_000;
  assert.deepEqual(R.tabDragHold(t0, null, t0), { active: true, recheckMs: R.OWN_DRAG_HOLD_MS });
  assert.deepEqual(R.tabDragHold(t0, null, t0 + 4000), { active: true, recheckMs: R.OWN_DRAG_HOLD_MS - 4000 });
  assert.equal(R.tabDragHold(t0, null, t0 + R.OWN_DRAG_HOLD_MS).active, false);
  assert.equal(R.tabDragHold(t0, null, t0 + R.OWN_DRAG_HOLD_MS + 1).active, false);
});

test('別的視窗在拖的（宿主的 tabDrag）：同樣有上限，和宿主的 WindowRules.TabDragTtl 一樣 15 秒', () => {
  assert.equal(R.EXTERNAL_DRAG_MS, 15000);
  const t0 = 9000;
  assert.deepEqual(R.tabDragHold(null, t0, t0 + 1000), { active: true, recheckMs: R.EXTERNAL_DRAG_MS - 1000 });
  assert.equal(R.tabDragHold(null, t0, t0 + R.EXTERNAL_DRAG_MS).active, false);
});

test('兩個都有：取晚到期的那一個', () => {
  const hold = R.tabDragHold(1000, 6000, 7000);
  assert.equal(hold.active, true);
  assert.equal(hold.recheckMs, R.EXTERNAL_DRAG_MS - 1000);
  // 自己的已經過期、別的視窗的還在
  assert.deepEqual(R.tabDragHold(0, 20_000, 20_500), { active: true, recheckMs: R.EXTERNAL_DRAG_MS - 500 });
});

test('時鐘往回跳（現在比開始的時間早）：以上限計，不會排著更久', () => {
  assert.deepEqual(R.tabDragHold(10_000, null, 4000), { active: true, recheckMs: R.OWN_DRAG_HOLD_MS });
  assert.deepEqual(R.tabDragHold(null, 10_000, 4000), { active: true, recheckMs: R.EXTERNAL_DRAG_MS });
});

test('不是數字的時間當成沒有在拖', () => {
  assert.equal(R.tabDragHold('1000', null, 1000).active, false);
  assert.equal(R.tabDragHold(NaN, Infinity, 1000).active, false);
});

test('放開的位置：游標在第幾個分頁的中線左邊；都在右邊是最後；沒有分頁是 0', () => {
  const mids = [100, 250, 400];
  assert.equal(R.dropIndexAt(mids, 20), 0);
  assert.equal(R.dropIndexAt(mids, 99.5), 0);
  assert.equal(R.dropIndexAt(mids, 100), 1, '剛好在中線上算右邊');
  assert.equal(R.dropIndexAt(mids, 300), 2);
  assert.equal(R.dropIndexAt(mids, 401), 3);
  assert.equal(R.dropIndexAt([], 50), 0);
});

test('插入記號的 x：插在第 i 個前面是它的左緣、插在最後是最後一個的右緣，往左 2px、不小於 0、取整數', () => {
  const tabs = [{ left: 0, right: 120.4 }, { left: 120.4, right: 260.6 }];
  assert.equal(R.insertMarkX(tabs, 0), 0, '第一個的左緣 0，往左 2px 不小於 0');
  assert.equal(R.insertMarkX(tabs, 1), 118);
  assert.equal(R.insertMarkX(tabs, 2), 259, '最後');
  assert.equal(R.insertMarkX(tabs, 9), 259, '超過也是最後');
  assert.equal(R.insertMarkX([], 0), 0, '沒有分頁');
});
