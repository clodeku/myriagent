// 遠端桌面看的一方的純規則（src/Myriagent/Web/remotedeskrules.js；docs/sync-protocol.md 14.16）
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  vkOf, comboEvents, normPoint, buttonOf, wheelUnits, createBatch, textEvents, createAssembler, fit, preferredMaxWidth,
  touchKind, twoFingerWheel, endText, fpsOf, cursorScale, waitingText, FIRST_FRAME_MS,
  clampZoom, fitView, clampView, zoomAt, panBy, edgePan, twoFingerStep, twoFingerKind, zoomLabel, wantedMaxWidth, ZOOM_MAX, SPECIAL_KEYS, MAX_EVENTS, TAP_SLOP_PX, LONG_PRESS_MS, SEND_GAP_MS,
} from '../../src/Myriagent/Web/remotedeskrules.js';

test('vkOf：KeyboardEvent.code 換成 Windows 的虛擬鍵碼', () => {
  assert.equal(vkOf('KeyA'), 0x41);
  assert.equal(vkOf('KeyZ'), 0x5a);
  assert.equal(vkOf('Digit0'), 0x30);
  assert.equal(vkOf('Numpad7'), 0x67);
  assert.equal(vkOf('F1'), 0x70);
  assert.equal(vkOf('F12'), 0x7b);
  assert.equal(vkOf('F24'), 0x87);
  assert.equal(vkOf('Enter'), 0x0d);
  assert.equal(vkOf('ControlLeft'), 0xa2);
  assert.equal(vkOf('MetaLeft'), 0x5b);
  assert.equal(vkOf('ArrowDown'), 0x28);
  assert.equal(vkOf('Slash'), 0xbf);
  assert.equal(vkOf('F25'), null);
  assert.equal(vkOf('Unidentified'), null);
  assert.equal(vkOf(undefined), null);
  assert.equal(vkOf('toString'), null, '不從原型鏈拿');
});

test('comboEvents：依序按下、反序放開；特殊按鍵都換得出事件', () => {
  assert.deepEqual(comboEvents([0xa2, 0x43]), [{ t: 'kd', k: 0xa2 }, { t: 'kd', k: 0x43 }, { t: 'ku', k: 0x43 }, { t: 'ku', k: 0xa2 }]);
  assert.deepEqual(comboEvents([0, 300, 'x']), []);
  for (const k of SPECIAL_KEYS) assert.equal(comboEvents(k.keys).length, k.keys.length * 2, k.label);
});

test('normPoint：換成比例、超出的夾回、四位小數', () => {
  const rect = { left: 100, top: 50, width: 200, height: 100 };
  assert.deepEqual(normPoint(200, 100, rect), { x: 0.5, y: 0.5 });
  assert.deepEqual(normPoint(0, 1000, rect), { x: 0, y: 1 });
  assert.deepEqual(normPoint(133.333, 50, rect), { x: 0.1667, y: 0 });
  assert.deepEqual(normPoint(1, 1, { left: 0, top: 0, width: 0, height: 10 }), { x: 0, y: 0 });
});

test('buttonOf 與 wheelUnits：往下捲是負的、像素 100 一格、夾到 ±2400', () => {
  assert.equal(buttonOf(0), 0);
  assert.equal(buttonOf(2), 2);
  assert.equal(buttonOf(3), null);
  assert.equal(wheelUnits(100, 0), -120);
  assert.equal(wheelUnits(-3, 1), 120);
  assert.equal(wheelUnits(1, 2), -360);
  assert.equal(wheelUnits(99999, 0), -2400);
  assert.equal(wheelUnits(0, 0), 0);
  assert.equal(wheelUnits(NaN, 0), 0);
});

test('createBatch：連著的移動只留最後一個、確認只留最新的、最多 64 個一包', () => {
  const b = createBatch();
  assert.equal(b.take(), null);
  b.push({ t: 'mm', x: 0.1, y: 0.1 });
  b.push({ t: 'mm', x: 0.2, y: 0.2 });
  b.push({ t: 'md', b: 0, x: 0.2, y: 0.2 });
  b.push({ t: 'mm', x: 0.3, y: 0.3 });
  b.ack(4);
  b.ack(2);
  assert.deepEqual(b.take(), { events: [{ t: 'mm', x: 0.2, y: 0.2 }, { t: 'md', b: 0, x: 0.2, y: 0.2 }, { t: 'mm', x: 0.3, y: 0.3 }], ack: 4 });
  assert.equal(b.take(), null);
  b.ack(5);
  assert.deepEqual(b.take(), { ack: 5 }, '只有確認');
  for (let i = 0; i < MAX_EVENTS + 3; i++) b.push({ t: 'kd', k: 65 });
  assert.equal(b.full, true);
  assert.equal(b.take().events.length, MAX_EVENTS);
  assert.equal(b.take().events.length, 3);
  assert.ok(SEND_GAP_MS * 20 >= 1000 * 1.25, '每秒最多約 15 包，在伺服器每秒 20 個指令之內');
});

test('textEvents：換行統一、去掉控制字元、長的拆成幾個', () => {
  assert.deepEqual(textEvents('a\r\nb\u0001'), [{ t: 'tx', s: 'a\nb' }]);
  assert.deepEqual(textEvents(''), []);
  assert.deepEqual(textEvents('x'.repeat(2500)).map((e) => e.s.length), [1000, 1000, 500]);
});

test('createAssembler：同一個 seq 的包都到了才回整個影格、舊的丟掉', () => {
  const a = createAssembler();
  const tile = (n) => ({ x: n, y: 0, w: 1, h: 1, d: 'AA==' });
  assert.equal(a.add({ seq: 1, body: { w: 10, h: 5, key: true, part: 0, parts: 2, screens: [{ i: 0 }], tiles: [tile(0)] } }), null);
  const f = a.add({ seq: 1, body: { w: 10, h: 5, part: 1, parts: 2, cur: { x: 0.5, y: 0.5, v: true }, tiles: [tile(1)] } });
  assert.equal(f.seq, 1);
  assert.equal(f.key, true);
  assert.deepEqual(f.tiles.map((t) => t.x), [0, 1]);
  assert.deepEqual(f.screens, [{ i: 0 }]);
  assert.deepEqual(f.cur, { x: 0.5, y: 0.5, v: true });
  // 單包的影格
  assert.equal(a.add({ seq: 2, body: { w: 10, h: 5, tiles: [] } }).seq, 2);
  // 拼到一半來了新的：舊的丟掉
  assert.equal(a.add({ seq: 3, body: { w: 10, h: 5, part: 0, parts: 2, tiles: [] } }), null);
  assert.equal(a.add({ seq: 4, body: { w: 10, h: 5, tiles: [] } }).seq, 4);
  assert.equal(a.add({ seq: 3, body: { w: 10, h: 5, part: 1, parts: 2, tiles: [] } }), null, '比較舊的不收');
  assert.equal(a.add({ seq: 'x', body: {} }), null);
});

test('fit、preferredMaxWidth、手勢、捲動、說明、fps', () => {
  assert.deepEqual(fit(800, 600, 1600, 900), { width: 800, height: 450 });
  assert.deepEqual(fit(800, 600, 400, 300), { width: 800, height: 600 }, '小的放大');
  assert.deepEqual(fit(0, 600, 400, 300), { width: 0, height: 0 });
  assert.equal(preferredMaxWidth(1920, 1, false), null, '電腦不要求');
  assert.equal(preferredMaxWidth(390, 3, true), 1170);
  assert.equal(preferredMaxWidth(1024, 2, true), 1280);
  assert.equal(preferredMaxWidth(200, 1, true), 640);
  assert.equal(touchKind(100, 2, false), 'tap');
  assert.equal(touchKind(100, TAP_SLOP_PX + 1, false), 'drag');
  assert.equal(touchKind(LONG_PRESS_MS + 10, 0, false), 'long');
  assert.equal(touchKind(100, 0, true), 'long');
  assert.equal(twoFingerWheel(10), 30);
  assert.equal(twoFingerWheel(-10000), -2400);
  assert.equal(endText({ text: '那台的人中斷了遠端桌面' }), '那台的人中斷了遠端桌面');
  assert.equal(endText(null), '遠端桌面已經結束');
  assert.equal(fpsOf([0, 500, 1000, 1500, 1900], 2000), 3, '2 秒內 5 格');
  assert.equal(fpsOf([0, 900, 1500, 1900, 5000], 5000), 1, '只算最近 2 秒');
  assert.equal(fpsOf([], 2000), 0);
});

test('cursorScale：照比例、夾在 0.6～1.5', () => {
  assert.equal(cursorScale(1920, 1920), 1);
  assert.equal(cursorScale(1440, 3840), 0.6);
  assert.equal(cursorScale(3840, 1280), 1.5);
  assert.equal(cursorScale(0, 0), 1, '還沒有大小時照原尺寸');
});

test('放大縮小：夾倍數、比框小置中、比框大不露空白、以一點為中心放大那一點不動', () => {
  const fit = { width: 800, height: 450 };
  assert.equal(clampZoom(0.5), 1);
  assert.equal(clampZoom(99), ZOOM_MAX);
  assert.equal(clampZoom(NaN), 1);
  assert.deepEqual(fitView(fit, 1000, 600), { zoom: 1, left: 100, top: 75 }, '置中');
  // 放大 2 倍：1600×900 比框大，位置夾在 [框－大小, 0]
  assert.deepEqual(clampView({ zoom: 2, left: 50, top: -999 }, fit, 1000, 600), { zoom: 2, left: 0, top: -300 });
  // 以框的 (300, 200) 為中心放大：那一點底下的內容比例不變
  const v0 = fitView(fit, 1000, 600);
  const u0 = (300 - v0.left) / (800 * v0.zoom);
  const v1 = zoomAt(v0, 2, 300, 200, fit, 1000, 600);
  const u1 = (300 - v1.left) / (800 * v1.zoom);
  assert.equal(v1.zoom, 2);
  assert.ok(Math.abs(u0 - u1) < 1e-9, '中心點底下的內容不動');
  // 縮回 1 倍：回到置中
  assert.deepEqual(zoomAt(v1, 0.5, 300, 200, fit, 1000, 600), v0);
  // 平移：夾住
  assert.deepEqual(panBy({ zoom: 2, left: -100, top: -100 }, 50, 9999, fit, 1000, 600), { zoom: 2, left: -50, top: 0 });
});

test('貼邊捲動：越貼近邊緣越快、中間不動', () => {
  assert.deepEqual(edgePan(500, 300, 1000, 600), { dx: 0, dy: 0 });
  assert.deepEqual(edgePan(0, 300, 1000, 600), { dx: 18, dy: 0 }, '貼左邊：內容往右移（看左邊）');
  assert.deepEqual(edgePan(999, 599, 1000, 600).dx < 0, true);
  assert.ok(edgePan(20, 300, 1000, 600).dx < edgePan(5, 300, 1000, 600).dx);
});

test('兩指：比例與中心、手勢的判斷', () => {
  const st = twoFingerStep([{ x: 0, y: 0 }, { x: 100, y: 0 }], [{ x: 0, y: 10 }, { x: 200, y: 10 }]);
  assert.equal(st.scale, 2);
  assert.deepEqual([st.cx, st.cy, st.dx, st.dy], [100, 10, 50, 10]);
  assert.equal(twoFingerKind(30, 0, 1), 'pinch');
  assert.equal(twoFingerKind(-30, 0, 2), 'pinch');
  assert.equal(twoFingerKind(2, 20, 1), 'scroll', '沒放大：捲動那台的畫面');
  assert.equal(twoFingerKind(2, 20, 2), 'pan', '放大時：平移這裡的畫面');
  assert.equal(twoFingerKind(2, 3, 2), null);
});

test('倍數文字與要求的傳送寬度', () => {
  assert.equal(zoomLabel(1), '符合視窗');
  assert.equal(zoomLabel(1.5), '150%');
  assert.equal(wantedMaxWidth(1440, 1, 1, false), 1920, '電腦沒放大：預設');
  assert.equal(wantedMaxWidth(390, 3, 1, true), 1170, '手機沒放大：照螢幕');
  assert.equal(wantedMaxWidth(390, 3, 2, true), 2560, '手機放大 2 倍：清楚一點');
  assert.equal(wantedMaxWidth(1440, 1, 2, false), 3840);
  assert.equal(wantedMaxWidth(1440, 1, 1.2, false), 1920);
  assert.equal(wantedMaxWidth(4000, 2, 8, false), 3840, '最多 3840');
});

test('等第一張畫面的文字：先是連線到，那台接下之後太久沒有畫面就說明可能的原因', () => {
  assert.equal(waitingText('書房', 0), '連線到「書房」…');
  assert.equal(waitingText('書房', FIRST_FRAME_MS - 1), '連線到「書房」…');
  assert.equal(waitingText('書房', NaN), '連線到「書房」…', '還沒接下（沒有開始時間）不算等太久');
  const late = waitingText('書房', FIRST_FRAME_MS);
  assert.ok(late.includes('「書房」') && late.includes('鎖定') && late.includes('螢幕關著') && late.includes('還沒更新'), late);
  assert.ok(!late.startsWith('連線到'));
});

