// 圖片燈箱的縮放與觸控手勢純規則（src/Myriagent/Web/viewerrules.js）：縮放範圍、點一下與拖曳的分辨、兩指捏合的倍率與錨點、
// 捏合放開時彈回、雙點，以及 viewerrules.js 沒有 import、它與 viewer.js 沒有 HTML 寫入點。
// 規則模組沒有 import，以 data: URL 匯入（同 launchrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'viewerrules.js'), 'utf8');
const V = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// viewerrules.test\n', 'utf8').toString('base64'));

const near = (a, b, eps = 1e-9) => Math.abs(a - b) <= eps;

test('縮放範圍：最小是符合視窗與 10% 較小的那個，最大 800%', () => {
  assert.deepEqual(V.zoomRange(0.25), { min: 0.1, max: 8 });
  assert.deepEqual(V.zoomRange(0.05), { min: 0.05, max: 8 });
  assert.deepEqual(V.zoomRange(1), { min: 0.1, max: 8 });
});

test('點一下與拖曳：滑鼠 3px，觸控與觸控筆 10px', () => {
  assert.equal(V.tapSlop('mouse'), 3);
  assert.equal(V.tapSlop('touch'), 10);
  assert.equal(V.tapSlop('pen'), 10);
  assert.equal(V.tapSlop(''), 10);
});

test('捏合開始：記下距離、倍率與中點底下的圖片座標；兩指疊在一起或倍率不對時回 null', () => {
  const s = V.pinchStart({ scale: 0.5, tx: 20, ty: 100 }, { x: 100, y: 200 }, { x: 160, y: 280 });
  assert.equal(s.dist, 100);
  assert.equal(s.scale, 0.5);
  assert.equal(s.ix, (130 - 20) / 0.5);
  assert.equal(s.iy, (240 - 100) / 0.5);
  assert.equal(V.pinchStart({ scale: 0.5, tx: 0, ty: 0 }, { x: 10, y: 10 }, { x: 10, y: 10 }), null);
  assert.equal(V.pinchStart({ scale: 0, tx: 0, ty: 0 }, { x: 0, y: 0 }, { x: 10, y: 0 }), null);
});

test('捏合中：倍率照兩指距離的比例，開始時中點底下的那一點留在中點底下', () => {
  const view = { scale: 0.25, tx: 24, ty: 270 };
  const a0 = { x: 87, y: 370 }, b0 = { x: 147, y: 370 };
  const s = V.pinchStart(view, a0, b0);
  // 兩指拉開到 4 倍距離：倍率 4 倍，中點不動
  const v = V.pinchMove(s, { x: 0, y: 370 }, { x: 240, y: 370 }, V.zoomRange(0.25));
  assert.ok(near(v.scale, 1));
  assert.ok(near(v.tx + s.ix * v.scale, 120));
  assert.ok(near(v.ty + s.iy * v.scale, 370));
  // 兩指一起往右下移 50px、距離不變：只平移
  const p = V.pinchMove(s, { x: a0.x + 50, y: a0.y + 50 }, { x: b0.x + 50, y: b0.y + 50 }, V.zoomRange(0.25));
  assert.ok(near(p.scale, 0.25));
  assert.ok(near(p.tx, view.tx + 50));
  assert.ok(near(p.ty, view.ty + 50));
});

test('捏合中：倍率夾在範圍內（兩指疊在一起時是最小值）', () => {
  const s = V.pinchStart({ scale: 1, tx: 0, ty: 0 }, { x: 0, y: 0 }, { x: 100, y: 0 });
  const r = V.zoomRange(0.25);
  assert.equal(V.pinchMove(s, { x: 0, y: 0 }, { x: 5000, y: 0 }, r).scale, 8);
  assert.equal(V.pinchMove(s, { x: 50, y: 0 }, { x: 52, y: 0 }, r).scale, 0.1);
  assert.equal(V.pinchMove(s, { x: 50, y: 0 }, { x: 50, y: 0 }, r).scale, 0.1);
});

test('捏合放開：比符合視窗小才彈回', () => {
  assert.equal(V.snapBackAfterPinch(0.1, 0.25), true);
  assert.equal(V.snapBackAfterPinch(0.2495, 0.25), false);
  assert.equal(V.snapBackAfterPinch(0.25, 0.25), false);
  assert.equal(V.snapBackAfterPinch(2, 0.25), false);
});

test('雙點：300 毫秒、30px 以內才算；第一下（沒有上一下）不算', () => {
  const t0 = { t: 1000, x: 100, y: 100 };
  assert.equal(V.isDoubleTap(null, t0), false);
  assert.equal(V.isDoubleTap(t0, { t: 1250, x: 110, y: 95 }), true);
  assert.equal(V.isDoubleTap(t0, { t: 1300, x: 100, y: 130 }), true);
  assert.equal(V.isDoubleTap(t0, { t: 1301, x: 100, y: 100 }), false);
  assert.equal(V.isDoubleTap(t0, { t: 1100, x: 140, y: 100 }), false);
  assert.equal(V.isDoubleTap(t0, { t: 900, x: 100, y: 100 }), false);
});

test('viewerrules.js 沒有 import、沒有 HTML 寫入點；viewer.js 也沒有 HTML 寫入點', () => {
  assert.ok(!/^\s*import\s/m.test(source));
  const sink = /innerHTML|outerHTML|insertAdjacentHTML|document\.write|setHtml|patchHtml/;
  assert.ok(!sink.test(source));
  assert.ok(!sink.test(readFileSync(path.join(webDir, 'viewer.js'), 'utf8')));
});
