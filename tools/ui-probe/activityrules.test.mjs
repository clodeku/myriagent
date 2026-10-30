// 對話框燈號的純規則（src/Myriagent/Web/activityrules.js）：activity 訊息換成各燈這次亮不亮與每秒多少、速率的寫法、滑過時的說明，
// 以及 activityrules.js 沒有 import、不碰 DOM。規則模組以 data: URL 匯入（同 launchrules.test.mjs）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const webDir = path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web');
const source = readFileSync(path.join(webDir, 'activityrules.js'), 'utf8');
const A = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// activityrules.test\n', 'utf8').toString('base64'));

test('燈號的順序與字、亮多久、網路的門檻', () => {
  assert.deepEqual(A.LEDS, ['run', 'data', 'net', 'disk']);
  assert.deepEqual(A.LEDS.map((k) => A.LED_LABEL[k]), ['運行', '資料', '網路', '硬碟']);
  assert.ok(A.LED_HOLD_MS < 250, '比宿主的取樣間隔短：持續有動靜時會閃');
  assert.ok(A.LED_HOLD_CALM_MS > 500, '減少動態時連續的動靜看起來一直亮著');
  assert.equal(A.NET_MIN_BYTES, 1024);
});

test('ledPulse：只看自己這個對話的資料流與硬碟、網路是整台電腦的；速率依 ms 換成每秒', () => {
  const m = { type: 'activity', ms: 250, conversations: [{ id: 'c1', data: 500, disk: 0 }, { id: 'c2', data: 0, disk: 4096 }], net: { rx: 2048, tx: 512 } };
  const c1 = A.ledPulse(m, 'c1');
  assert.deepEqual([c1.data.on, c1.disk.on, c1.net.on], [true, false, true]);
  assert.equal(c1.data.rate, 2000, '500 位元組／250ms＝每秒 2000');
  assert.equal(c1.net.rx, 8192);
  assert.equal(c1.net.tx, 2048);
  const c2 = A.ledPulse(m, 'c2');
  assert.deepEqual([c2.data.on, c2.disk.on], [false, true]);
  const c3 = A.ledPulse(m, 'c3');
  assert.deepEqual([c3.data.on, c3.disk.on, c3.net.on], [false, false, true], '訊息裡沒有這個對話：這次沒動靜，網路照樣');
});

test('ledPulse：網路少於門檻不亮；沒有網路、壞的欄位、ms 是 0 都不出錯', () => {
  assert.equal(A.ledPulse({ ms: 250, conversations: [], net: { rx: 600, tx: 300 } }, 'c1').net.on, false, '900 位元組是背景零星的封包');
  assert.equal(A.ledPulse({ ms: 250, conversations: [], net: { rx: 1000, tx: 24 } }, 'c1').net.on, true);
  const odd = A.ledPulse({ ms: 0, conversations: [null, { id: 'c1', data: -5, disk: 'x' }], net: null }, 'c1');
  assert.deepEqual([odd.data.on, odd.disk.on, odd.net.on], [false, false, false]);
  assert.equal(A.ledPulse(null, 'c1').data.on, false);
  assert.equal(A.ledPulse({ ms: 0, conversations: [{ id: 'c1', data: 10 }] }, 'c1').data.rate, 10000, 'ms 至少當 1');
});

test('rateText：B／KB／MB／GB 每秒，100 以上不帶小數', () => {
  assert.equal(A.rateText(0), '0 B/s');
  assert.equal(A.rateText(512), '512 B/s');
  assert.equal(A.rateText(1536), '1.5 KB/s');
  assert.equal(A.rateText(150 * 1024), '150 KB/s');
  assert.equal(A.rateText(3.25 * 1024 * 1024), '3.3 MB/s');
  assert.equal(A.rateText(5 * 1024 ** 4), '5120 GB/s');
  assert.equal(A.rateText(-3), '0 B/s');
  assert.equal(A.rateText(NaN), '0 B/s');
});

test('ledTitle：寫出資料從哪裡來，加上最近的速率與多久以前；還沒量到時照實說', () => {
  const now = 1_800_000_000_000;
  assert.match(A.ledTitle('run', { running: true }, now), /運行.*\n目前：執行中$/);
  assert.match(A.ledTitle('run', { running: false }, now), /目前：閒置$/);
  assert.match(A.ledTitle('data', null, now), /app 與這個對話的 CLI 之間串流的資料[\s\S]*最近：還沒有動靜$/);
  assert.match(A.ledTitle('disk', { rate: 2048, at: now - 3000 }, now), /I\/O 計數器[\s\S]*最近：2\.0 KB\/s（3 秒前）$/);
  assert.match(A.ledTitle('net', { rate: 0, rx: 1024 * 1024, tx: 2048, at: now - 200 }, now), /整台電腦[\s\S]*↓ 1\.0 MB\/s　↑ 2\.0 KB\/s（剛剛）$/);
  assert.match(A.ledTitle('data', { rate: 10, at: now - 125_000 }, now), /（2 分鐘前）$/);
});

test('cpuOf：只取這個對話的 cpu（0～100），沒量程序樹時 null', () => {
  const m = { ms: 250, conversations: [{ id: 'c1', data: 0, disk: 0, cpu: 12.5 }, { id: 'c2', data: 10, disk: 0 }, { id: 'c3', cpu: 140 }, { id: 'c4', cpu: 'x' }] };
  assert.equal(A.cpuOf(m, 'c1'), 12.5);
  assert.equal(A.cpuOf(m, 'c2'), null, '有資料流但沒量程序樹');
  assert.equal(A.cpuOf(m, 'c3'), 100, '不超過 100');
  assert.equal(A.cpuOf(m, 'c4'), null);
  assert.equal(A.cpuOf(m, 'zz'), null);
  assert.equal(A.cpuOf({ ms: 250, conversations: [{ id: 'c1', cpu: 0 }] }, 'c1'), 0, '0 也是量到的值（波形畫平的）');
  assert.equal(A.cpuOf(null, 'c1'), null);
});

test('pushCpu：新的放最後、最多 CPU_WAVE_KEEP 格、不改原陣列', () => {
  const h0 = [1, 2];
  const h1 = A.pushCpu(h0, 3);
  assert.deepEqual(h1, [1, 2, 3]);
  assert.deepEqual(h0, [1, 2]);
  let h = [];
  for (let i = 0; i < A.CPU_WAVE_KEEP + 5; i++) h = A.pushCpu(h, i);
  assert.equal(h.length, A.CPU_WAVE_KEEP);
  assert.equal(h[h.length - 1], A.CPU_WAVE_KEEP + 4);
  assert.equal(A.CPU_WAVE_KEEP, A.CPU_WAVE_LEN + 3, '窗口左右各一格與平移中露出的一格');
  assert.equal(A.CPU_WAVE_LEN * A.CPU_WAVE_STEP, 66, '窗口 66px');
  assert.equal(A.CPU_WAVE_CANVAS, 72, 'canvas 比窗口寬兩格');
});

test('cpuWaveScale：取看得到的這幾格的最高值，尖峰離開後慢慢收、不低於下限', () => {
  assert.equal(A.cpuWaveScale(0, [0, 0.4]), A.CPU_WAVE_FLOOR, '很低時用下限：波形也矮');
  assert.equal(A.cpuWaveScale(0, [3, 40, 2]), 40);
  assert.equal(A.cpuWaveScale(40, [3, 2]), 40 * A.CPU_WAVE_DECAY, '尖峰離開：每格收一點，不會一下子全部跳高');
  assert.equal(A.cpuWaveScale(0, [90, ...Array(A.CPU_WAVE_LEN).fill(1)]), A.CPU_WAVE_FLOOR, '已經移出窗口的那格不算');
  assert.equal(A.cpuWaveScale(400, []), 100, '不超過 100');
});

test('cpuWavePoints：最新的在 canvas 右緣、越舊越往左一格，高度依刻度 0～1，畫不到的（x < 0）不回', () => {
  const p = A.cpuWavePoints([10, 20, 40], 20, 3, 72);
  assert.deepEqual(p, [{ x: 66, v: 10, a: 0.5 }, { x: 69, v: 20, a: 1 }, { x: 72, v: 40, a: 1 }]);
  const full = A.cpuWavePoints(Array(40).fill(1), 5);
  assert.equal(full[full.length - 1].x, A.CPU_WAVE_CANVAS);
  assert.ok(full.every((q) => q.x >= 0));
  assert.equal(full.length, A.CPU_WAVE_LEN + 3, '0、3、…、72 共 25 格');
  assert.equal(A.cpuWavePoints([1], 0)[0].a, 0.2, '刻度不合理時用下限');
});

test('cpuColor：依負載從青到紅、開根號的比例；深淺色主題各一組，淺色比較深', () => {
  const rgb = (s) => s.match(/\d+/g).map(Number);
  assert.equal(A.cpuColor(0, 'dark'), 'rgb(34, 211, 238)', '0% 是青');
  assert.equal(A.cpuColor(100, 'dark'), 'rgb(244, 63, 94)', '100% 是紅');
  assert.equal(A.cpuColor(25, 'dark'), 'rgb(250, 204, 21)', '25%（開根號 0.5）是黃');
  assert.equal(A.cpuColor(6.25, 'dark'), 'rgb(52, 211, 153)', '6.25%（開根號 0.25）是綠：多核心的電腦上兩三顆核心滿載就看得出來');
  assert.equal(A.cpuColor(200, 'dark'), A.cpuColor(100, 'dark'));
  assert.equal(A.cpuColor('x', 'dark'), A.cpuColor(0, 'dark'));
  const lum = (c) => rgb(c).reduce((a, b) => a + b, 0);
  for (const v of [0, 6.25, 25, 56, 100]) assert.ok(lum(A.cpuColor(v, 'light')) < lum(A.cpuColor(v, 'dark')), '淺色主題用深一點的顏色：' + v);
  const [r1, , b1] = rgb(A.cpuColor(2, 'dark'));
  const [r2, , b2] = rgb(A.cpuColor(60, 'dark'));
  assert.ok(b1 > r1 && r2 > b2, '低負載偏冷、高負載偏暖');
});

test('cpuSlideFrom 與 cpuSlideMs：重畫後從目前的位置加一格接著移；長度跟著取樣間隔、有上下限', () => {
  assert.equal(A.cpuSlideFrom(1, 3), 0, '上一段移完了：從 0 開始');
  assert.equal(A.cpuSlideFrom(null, 3), 0, '沒有在移');
  assert.equal(A.cpuSlideFrom(0, 3), 3, '才剛開始移就來了新的一格：從右邊一格開始');
  assert.ok(Math.abs(A.cpuSlideFrom(0.6, 3) - 1.2) < 1e-9, '移了六成：目前在 -1.8，重畫後同一個位置是 1.2');
  assert.equal(A.cpuSlideFrom(5, 3), 0);
  assert.equal(A.cpuSlideMs(250), 250);
  assert.equal(A.cpuSlideMs(30), 120);
  assert.equal(A.cpuSlideMs(9_000), 1000);
  assert.equal(A.cpuSlideMs(undefined), 250);
});

test('cpuText 與 cpuTitle：10% 以下帶一位小數；說明寫出量的是什麼、顏色的意思與最近的平均、最高', () => {
  assert.equal(A.cpuText(0), '0.0%');
  assert.equal(A.cpuText(3.125), '3.1%');
  assert.equal(A.cpuText(9.96), '10.0%');
  assert.equal(A.cpuText(56.6), '57%');
  assert.equal(A.cpuText(130), '100%');
  assert.match(A.cpuTitle([]), /CLI、子代理、它執行的工具.*背景程式.*整台電腦.*工作管理員.*模型在雲端思考不算.*建置常駐程序.*不算。顏色依負載：青、綠是低，黃、橘、紅是高$/);
  assert.match(A.cpuTitle([2, 4, 6, 8]), /\n最近 1 秒：平均 5\.0%、最高 8\.0%$/);
  assert.ok(A.CPU_STALE_MS > 1000 && A.CPU_STALE_MS < 3000, '比取樣間隔長幾格：偶爾慢一點的取樣不會讓它閃掉');
  assert.equal(A.CPU_SLOW_DRAW_MS, 1000);
});

test('memOf 與 memText：只取這個對話的 mem（位元組）；1 GB 以下整數 MB、10 GB 以下一位小數、以上整數 GB', () => {
  const m = { ms: 250, conversations: [{ id: 'c1', cpu: 1, mem: 734003200 }, { id: 'c2', cpu: 1 }, { id: 'c3', mem: -5 }, { id: 'c4', mem: 0 }] };
  assert.equal(A.memOf(m, 'c1'), 734003200);
  assert.equal(A.memOf(m, 'c2'), null, '沒有 mem');
  assert.equal(A.memOf(m, 'c3'), null, '負的不合理');
  assert.equal(A.memOf(m, 'c4'), 0);
  assert.equal(A.memOf(null, 'c1'), null);
  const MB = 1048576;
  assert.equal(A.memText(0), '0 MB');
  assert.equal(A.memText(500), '1 MB', '有一點點就寫 1 MB，不寫 0');
  assert.equal(A.memText(523.4 * MB), '523 MB');
  assert.equal(A.memText(1023.6 * MB), '1.0 GB', '四捨五入到 1024 MB 時改寫 GB');
  assert.equal(A.memText(1.24 * 1024 * MB), '1.2 GB');
  assert.equal(A.memText(12.6 * 1024 * MB), '13 GB');
  assert.match(A.cpuTitle([2, 4], 1.24 * 1024 * MB), /\n記憶體 1\.2 GB：同一組程序的私人工作集加總（同工作管理員的「記憶體」欄）$/);
  assert.ok(!/記憶體 /.test(A.cpuTitle([2, 4], null)), '沒有記憶體就不寫');
});

test('activityrules.js 沒有 import、不碰 DOM', () => {
  assert.ok(!/^\s*import\s[^\n]*\bfrom\s*['"]|^\s*import\s*\(/m.test(source));
  assert.ok(!/\b(document|window|innerHTML)\b/.test(source));
});
