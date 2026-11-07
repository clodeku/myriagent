// 解碼動畫共用節拍（src/Myriagent/Web/fxclock.js）的規則：假時鐘（每 1 ms 推進、每 16.67 ms 一格影格）下，
// 宿主沒在更新時換字照舊是「到點之後的第一個影格」、不提早要影格；宿主正在連續更新（app.js 每次套用更新都 ride）時，
// 快到點或剛到點的換字搭宿主的那一格，鎖在宿主的 50 ms 節拍上，不另外要一格只為了換字的影格（2026-11-08）。
// 模組以 data: URL 匯入（同 activityrules.test.mjs），每個測試一份新的模組狀態。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const source = readFileSync(path.resolve(here, '..', '..', 'src', 'Myriagent', 'Web', 'fxclock.js'), 'utf8');
const VSYNC = 1000 / 60;

/** 假的瀏覽器：計時器、requestAnimationFrame（每個 VSYNC 一格）、performance.now；frames 記下有回呼要跑的影格時間 */
function fakeBrowser() {
  const w = { t: 0, timers: new Map(), rafs: new Map(), id: 0, frames: [], nextVsync: VSYNC };
  globalThis.setTimeout = (fn, ms) => { const id = ++w.id; w.timers.set(id, { at: w.t + Math.max(0, ms), fn }); return id; };
  globalThis.clearTimeout = (id) => { w.timers.delete(id); };
  globalThis.requestAnimationFrame = (fn) => { const id = ++w.id; w.rafs.set(id, fn); return id; };
  globalThis.cancelAnimationFrame = (id) => { w.rafs.delete(id); };
  Object.defineProperty(globalThis, 'performance', { value: { now: () => w.t }, configurable: true, writable: true });
  /** 推進到 until（毫秒）：每 1 ms 叫到點的計時器，跨過影格的時間點時叫排著的影格回呼；hostEvery 給了就每隔那麼久 ride 一次（宿主的更新） */
  w.run = (until, onMs) => {
    while (w.t < until) {
      w.t += 1;
      for (const [id, x] of Array.from(w.timers)) if (x.at <= w.t) { w.timers.delete(id); x.fn(); }
      if (onMs) onMs(w.t);
      if (w.t >= w.nextVsync) {
        const ft = w.nextVsync;
        w.nextVsync += VSYNC;
        const list = Array.from(w.rafs.values());
        w.rafs.clear();
        if (list.length) w.frames.push(ft);
        for (const fn of list) fn(ft);
      }
    }
  };
  return w;
}

let n = 0;
async function load() {
  const w = fakeBrowser();
  const C = await import('data:text/javascript;base64,' + Buffer.from(source + '\n// fxclock.test ' + n++ + '\n', 'utf8').toString('base64'));
  return { w, C };
}

test('fxclock.js 沒有 import、經 setTimeout 等到時間才要影格', () => {
  assert.doesNotMatch(source, /^\s*import /m);
  assert.match(source, /setTimeout\(/);
  assert.match(source, /requestAnimationFrame\(frame\)/);
});

test('宿主沒在更新：到點之後的第一個影格才叫，不提早', async () => {
  const { w, C } = await load();
  const calls = [];
  C.wakeAt((now) => { calls.push(now); }, 100);
  w.run(200);
  assert.equal(calls.length, 1);
  assert.ok(calls[0] >= 100 && calls[0] < 100 + VSYNC, '到點之後的第一個影格：' + calls[0]);
});

test('ride：RIDE_MS 內就到點的現在叫，更早的不叫', async () => {
  const { w, C } = await load();
  const calls = [];
  C.wakeAt((now) => { calls.push(now); }, 100);
  w.run(100 - C.RIDE_MS - 5);
  C.ride();
  assert.equal(calls.length, 0, '還早：不叫');
  w.run(100 - C.RIDE_MS + 2);
  C.ride();
  assert.deepEqual(calls, [100 - C.RIDE_MS + 2], '快到點：搭這一格');
  // 叫過了、沒有下一次：之後不再要影格
  const before = w.frames.length;
  w.run(200);
  assert.equal(w.frames.length, before, '不另外要什麼都不換的影格');
});

test('宿主正在更新：到點後先等宿主的下一格（最多 LATE_MS），等到了就搭它', async () => {
  const { w, C } = await load();
  const calls = [];
  w.run(60);
  C.ride(); // 宿主剛更新過
  C.wakeAt((now) => { calls.push(now); }, 100);
  w.run(100 + C.LATE_MS - 10);
  assert.equal(calls.length, 0, '等宿主：還沒自己要影格');
  C.ride();
  assert.deepEqual(calls, [100 + C.LATE_MS - 10]);
});

test('宿主更新停了：等到 at＋LATE_MS 還沒來就自己要影格', async () => {
  const { w, C } = await load();
  const calls = [];
  w.run(60);
  C.ride();
  C.wakeAt((now) => { calls.push(now); }, 100);
  w.run(300);
  assert.equal(calls.length, 1);
  assert.ok(calls[0] >= 100 && calls[0] <= 100 + C.LATE_MS + VSYNC, '最晚 at＋LATE_MS 之後的第一個影格：' + calls[0]);
});

test('宿主每 50 ms 更新一次時，每 45 ms 換字的計數器鎖在宿主的那一格（不另外要影格）', async () => {
  const { w, C } = await load();
  const calls = [];
  const host = new Set();
  // 宿主在影格裡套用更新（app.js 的 flush 是 requestAnimationFrame）
  const tick = (t) => { if (t % 50 === 0) requestAnimationFrame((ft) => { host.add(ft); C.ride(ft); }); };
  w.run(10, tick);
  C.wakeAt((now) => { calls.push(now); return now + 45; }, w.t + 45);
  w.run(1010, tick);
  assert.ok(calls.length >= 18, '照樣一直在換字：' + calls.length);
  const own = calls.filter((t) => !host.has(t));
  assert.ok(own.length <= 1, '只為了換字的影格（開頭可能有一格）：' + own.join(', '));
  const gaps = calls.slice(1).map((t, i) => t - calls[i]);
  assert.ok(gaps.every((g) => g >= 45 - C.RIDE_MS && g <= 45 + C.LATE_MS + VSYNC), '換字的間隔：' + gaps.map(Math.round).join(', '));
});

test('宿主沒在更新時，每 45 ms 換字的節奏和以前一樣（45～62 ms）', async () => {
  const { w, C } = await load();
  const calls = [];
  C.wakeAt((now) => { calls.push(now); return now + 45; }, 45);
  w.run(1000);
  const gaps = calls.slice(1).map((t, i) => t - calls[i]);
  assert.ok(calls.length >= 15);
  assert.ok(gaps.every((g) => g >= 45 && g <= 45 + VSYNC + 0.01), '換字的間隔：' + gaps.map(Math.round).join(', '));
  // 每一格要影格的都有回呼要跑（只有提早醒來的那一格可能是空的）
  assert.ok(w.frames.length <= calls.length * 2, '影格 ' + w.frames.length + '、換字 ' + calls.length);
});

test('cancelWake 之後不再叫、也不再要影格', async () => {
  const { w, C } = await load();
  const calls = [];
  const fn = (now) => { calls.push(now); return now + 45; };
  C.wakeAt(fn, 50);
  w.run(120);
  const had = calls.length;
  C.cancelWake(fn);
  const frames = w.frames.length;
  w.run(400);
  assert.equal(calls.length, had);
  assert.ok(w.frames.length <= frames + 1, '取消之後最多只剩已經要了的那一格');
});
