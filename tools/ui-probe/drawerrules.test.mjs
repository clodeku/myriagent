// 觸控左右滑叫出與收起兩側面板的純規則（src/Myriagent/Web/drawerrules.js）：方向鎖定、要不要接手（左邊的側欄、右邊的電腦狀態）、
// 對話那一側往哪邊滑叫出哪一個、拖曳中的展開比例與位置、速度、放開時開或關、讓給打字與橫向捲動，
// 以及 drawerrules.js 沒有 import、它與 drawer.js 沒有 HTML 寫入點。
// 規則模組沒有 import，以 data: URL 匯入（同 launchrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'drawerrules.js'), 'utf8');
const D = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// drawerrules.test\n', 'utf8').toString('base64'));

test('方向鎖定：移動不到 10px 再等；水平比垂直多 1.2 倍以上才算左右滑，否則交給捲動', () => {
  assert.equal(D.lockAxis(0, 0), null);
  assert.equal(D.lockAxis(9, 9), null);
  assert.equal(D.lockAxis(-9.9, 0), null);
  assert.equal(D.lockAxis(10, 0), 'x');
  assert.equal(D.lockAxis(-30, 5), 'x');
  assert.equal(D.lockAxis(0, 10), 'y');
  assert.equal(D.lockAxis(12, 10), 'y');
  assert.equal(D.lockAxis(13, 10), 'x');
  assert.equal(D.lockAxis(-20, -40), 'y');
  assert.equal(D.lockAxis(NaN, undefined), null);
});

test('要不要接手：開著只接往左、收著只接往右', () => {
  assert.equal(D.wantsDrag(true, -12), true);
  assert.equal(D.wantsDrag(true, 12), false);
  assert.equal(D.wantsDrag(false, 12), true);
  assert.equal(D.wantsDrag(false, -12), false);
  // 右邊的面板（電腦狀態）反過來：開著只接往右、收著只接往左
  assert.equal(D.wantsDrag(true, 12, 'right'), true);
  assert.equal(D.wantsDrag(true, -12, 'right'), false);
  assert.equal(D.wantsDrag(false, -12, 'right'), true);
  assert.equal(D.wantsDrag(false, 12, 'right'), false);
  assert.equal(D.wantsDrag(false, 0, 'right'), false);
});

test('對話那一側：往右滑叫出側欄、往左滑叫出電腦狀態；那一個已經開著就不接手', () => {
  assert.equal(D.workspaceTarget(12, false, false), 'left');
  assert.equal(D.workspaceTarget(-12, false, false), 'right');
  assert.equal(D.workspaceTarget(12, true, false), null);
  assert.equal(D.workspaceTarget(-12, false, true), null);
  assert.equal(D.workspaceTarget(12, false, true), 'left');
  assert.equal(D.workspaceTarget(-12, true, false), 'right');
  assert.equal(D.workspaceTarget(0, false, false), null);
  assert.equal(D.workspaceTarget(NaN, false, false), null);
});

test('拖曳中的展開比例：以抽屜寬度換算、夾在 0～1；寬度不明時停在原狀態', () => {
  assert.equal(D.dragProgress(false, 0, 300), 0);
  assert.equal(D.dragProgress(false, 150, 300), 0.5);
  assert.equal(D.dragProgress(false, 600, 300), 1);
  assert.equal(D.dragProgress(false, -50, 300), 0);
  assert.equal(D.dragProgress(true, 0, 300), 1);
  assert.equal(D.dragProgress(true, -75, 300), 0.75);
  assert.equal(D.dragProgress(true, -900, 300), 0);
  assert.equal(D.dragProgress(true, 40, 300), 1);
  assert.equal(D.dragProgress(true, -100, 0), 1);
  assert.equal(D.dragProgress(false, 100, undefined), 0);
  // 右邊的面板：收著往左拉才展開、開著往右拉才收起
  assert.equal(D.dragProgress(false, -106, 212, 'right'), 0.5);
  assert.equal(D.dragProgress(false, 50, 212, 'right'), 0);
  assert.equal(D.dragProgress(true, 53, 212, 'right'), 0.75);
  assert.equal(D.dragProgress(true, -40, 212, 'right'), 1);
});

test('位移：左邊的面板收起在 -100%、右邊的在 100%，全開都是 0；比例夾在 0～1', () => {
  assert.equal(D.panelOffset(0), -100);
  assert.equal(D.panelOffset(0.25), -75);
  assert.equal(D.panelOffset(1), 0);
  assert.equal(D.panelOffset(0, 'right'), 100);
  assert.equal(D.panelOffset(0.25, 'right'), 75);
  assert.equal(D.panelOffset(1, 'right'), 0);
  assert.equal(D.panelOffset(2, 'right'), 0);
  assert.equal(D.panelOffset(-1), -100);
  assert.ok(D.SETTLE_MS > 0 && D.SETTLE_MS <= 400);
});

test('速度：只看最後 100ms（至少用倒數第二筆）；不到兩筆或時間沒動是 0', () => {
  assert.equal(D.velocity([]), 0);
  assert.equal(D.velocity([{ t: 0, x: 0 }]), 0);
  assert.equal(D.velocity(null), 0);
  assert.equal(D.velocity([{ t: 5, x: 0 }, { t: 5, x: 40 }]), 0);
  // 前面慢慢拉、最後 100ms 快速往左甩：只算最後那段
  const s = [{ t: 0, x: 300 }, { t: 200, x: 290 }, { t: 400, x: 280 }, { t: 450, x: 250 }, { t: 500, x: 200 }];
  assert.equal(D.velocity(s), (200 - 280) / (500 - 400));
  // 最後兩筆就隔了超過 100ms：還是用這兩筆
  assert.equal(D.velocity([{ t: 0, x: 0 }, { t: 300, x: 60 }]), 0.2);
  // 放開的時間：最後一次移動之後 100ms 內放開照算；停住超過 100ms 才放開是 0（拉到一半停住再放開，不算甩）
  assert.equal(D.velocity(s, 560), (200 - 280) / (500 - 400));
  assert.equal(D.velocity(s, 600), (200 - 280) / (500 - 400));
  assert.equal(D.velocity(s, 601), 0);
});

test('放開時：甩得夠快照方向，否則看拉過一半沒有', () => {
  assert.equal(D.releaseOpen(0.1, D.FLING), true);
  assert.equal(D.releaseOpen(0.9, -D.FLING), false);
  assert.equal(D.releaseOpen(0.5, 0), true);
  assert.equal(D.releaseOpen(0.49, 0), false);
  assert.equal(D.releaseOpen(0.7, -0.2), true);
  assert.equal(D.releaseOpen(0.3, 0.2), false);
  // 右邊的面板：往左甩是展開、往右甩是收起
  assert.equal(D.releaseOpen(0.1, -D.FLING, 'right'), true);
  assert.equal(D.releaseOpen(0.9, D.FLING, 'right'), false);
  assert.equal(D.releaseOpen(0.6, 0.2, 'right'), true);
  assert.equal(D.releaseOpen(0.4, -0.2, 'right'), false);
});

test('讓給別人：打字的地方、不准左右平移的、還能往手指方向捲的橫向捲動區', () => {
  const plain = { edit: false, noPanX: false, max: 0, left: 0 };
  assert.equal(D.yieldsTo([plain, plain], 20), false);
  assert.equal(D.yieldsTo([], 20), false);
  assert.equal(D.yieldsTo(null, 20), false);
  assert.equal(D.yieldsTo([{ ...plain, edit: true }, plain], 20), true);
  assert.equal(D.yieldsTo([plain, { ...plain, noPanX: true }], -20), true);
  // 程式碼框捲到最左邊：手指往右（內容往左捲）已經捲不動，交給抽屜；捲過一點就讓它捲回來
  const code = (left) => ({ ...plain, max: 400, left });
  assert.equal(D.yieldsTo([code(0), plain], 20), false);
  assert.equal(D.yieldsTo([code(0.4), plain], 20), false);
  assert.equal(D.yieldsTo([code(120), plain], 20), true);
  // 手指往左（內容往右捲）：還沒捲到最右邊就讓它捲
  assert.equal(D.yieldsTo([code(0), plain], -20), true);
  assert.equal(D.yieldsTo([code(400), plain], -20), false);
  assert.equal(D.yieldsTo([code(399.7), plain], -20), false);
  // 可捲寬度不到 1px 當作不能捲（小數的版面誤差）
  assert.equal(D.yieldsTo([{ ...plain, max: 0.4, left: 0 }], -20), false);
});

test('touch-action：auto、manipulation、含 pan-x 的准左右平移；none、pan-y 不准', () => {
  assert.equal(D.allowsPanX('auto'), true);
  assert.equal(D.allowsPanX(''), true);
  assert.equal(D.allowsPanX(undefined), true);
  assert.equal(D.allowsPanX('manipulation'), true);
  assert.equal(D.allowsPanX('pan-x'), true);
  assert.equal(D.allowsPanX('pan-x pan-y'), true);
  assert.equal(D.allowsPanX('pan-x pinch-zoom'), true);
  assert.equal(D.allowsPanX('pan-y'), false);
  assert.equal(D.allowsPanX('none'), false);
  assert.equal(D.allowsPanX('pan-left'), false);
});

test('drawerrules.js 沒有 import、沒有 HTML 寫入點；drawer.js 也沒有 HTML 寫入點', () => {
  assert.doesNotMatch(source, /^\s*import\b/m);
  const sink = /innerHTML|outerHTML|insertAdjacentHTML|document\.write|setHtml|patchHtml/;
  assert.doesNotMatch(source, sink);
  assert.doesNotMatch(readFileSync(path.join(webDir, 'drawer.js'), 'utf8'), sink);
});
