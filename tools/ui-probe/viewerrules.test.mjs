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

test('拖曳關閉：只在沒有放大時', () => {
  assert.equal(V.canSwipeDismiss(0.25, 0.25), true);
  assert.equal(V.canSwipeDismiss(0.2, 0.25), true);
  assert.equal(V.canSwipeDismiss(0.3, 0.25), false);
});

test('拖曳關閉的進度：對舞台短邊的 40%（至少 120px），到 1 為止', () => {
  assert.equal(V.dismissProgress(0, 0, 390, 700), 0);
  assert.ok(near(V.dismissProgress(78, 0, 390, 700), 0.5));
  assert.ok(near(V.dismissProgress(0, -78, 390, 700), 0.5));
  assert.equal(V.dismissProgress(400, 0, 390, 700), 1);
  assert.ok(near(V.dismissProgress(60, 0, 100, 100), 0.5), '小舞台的參考距離至少 120px');
});

test('放開時關不關：拖過短邊的 18%（至少 80px），或往拖的方向甩出去', () => {
  // 手機（390 寬）：門檻 80px
  assert.equal(V.shouldDismiss(79, 0, 0, 0, 390, 700), false);
  assert.equal(V.shouldDismiss(-81, 0, 0, 0, 390, 700), true);
  assert.equal(V.shouldDismiss(60, 60, 0, 0, 390, 700), true, '斜的也算距離');
  // 平板（1024×768）：門檻 768×0.18 ≈ 138px
  assert.equal(V.shouldDismiss(120, 0, 0, 0, 1024, 768), false);
  assert.equal(V.shouldDismiss(140, 0, 0, 0, 1024, 768), true);
  // 甩出去：距離不夠但速度夠快、同方向
  assert.equal(V.shouldDismiss(40, 0, 1.2, 0, 390, 700), true);
  assert.equal(V.shouldDismiss(40, 0, -1.2, 0, 390, 700), false, '往回拉的不算');
  assert.equal(V.shouldDismiss(40, 0, 0.3, 0, 390, 700), false, '太慢');
  assert.equal(V.shouldDismiss(10, 0, 3, 0, 390, 700), false, '幾乎沒拖動（點一下的抖動）不算');
});

test('放開前的速度：取最後 100 毫秒的頭尾；停住再放開是 0', () => {
  const s = [{ t: 0, x: 0, y: 0 }, { t: 50, x: 10, y: 0 }, { t: 150, x: 60, y: 20 }, { t: 200, x: 110, y: 40 }];
  const v = V.releaseVelocity(s);
  assert.ok(near(v.vx, 1) && near(v.vy, 0.4), JSON.stringify(v));
  assert.deepEqual(V.releaseVelocity([{ t: 0, x: 0, y: 0 }, { t: 300, x: 50, y: 0 }]), { vx: 0, vy: 0 });
  assert.deepEqual(V.releaseVelocity([{ t: 0, x: 0, y: 0 }]), { vx: 0, vy: 0 });
  assert.deepEqual(V.releaseVelocity(null), { vx: 0, vy: 0 });
});

test('關閉時飛出去：沿拖曳方向、距離是舞台對角線；沒有位移往下', () => {
  const o = V.flingOffset(30, -40, 300, 400);
  assert.ok(near(o.x, 300) && near(o.y, -400), JSON.stringify(o));
  assert.deepEqual(V.flingOffset(0, 0, 300, 400), { x: 0, y: 500 });
});

test('長按：移動超過 10px 就不是長按；自己送過之後 1 秒內瀏覽器再送的 contextmenu 略過', () => {
  assert.equal(V.LONG_PRESS_MS, 500);
  assert.equal(V.longPressMoved(6, 4), false);
  assert.equal(V.longPressMoved(6, 5), true);
  assert.equal(V.duplicateContextMenu(-Infinity, 1000), false);
  assert.equal(V.duplicateContextMenu(1000, 1500), true);
  assert.equal(V.duplicateContextMenu(1000, 2000), false);
  assert.equal(V.duplicateContextMenu(1000, 900), false, '時鐘倒退（不同的時間來源）不算');
});

test('viewerrules.js 沒有 import、沒有 HTML 寫入點；viewer.js、mediamenu.js 也沒有 HTML 寫入點', () => {
  assert.ok(!/^\s*import\s/m.test(source));
  const sink = /innerHTML|outerHTML|insertAdjacentHTML|document\.write|setHtml|patchHtml/;
  assert.ok(!sink.test(source));
  assert.ok(!sink.test(readFileSync(path.join(webDir, 'viewer.js'), 'utf8')));
  assert.ok(!sink.test(readFileSync(path.join(webDir, 'mediamenu.js'), 'utf8')));
});
