// 瀏覽器版在手機上往上滑、一路載入歷史訊息的量測（2026-11-18 使用者回報：手機往前滑載入歷史訊息時常會很卡，而且會跳動閃爍）。
// 假 hub 的對話有好幾頁（每頁 80 則，同伺服器的一頁），內容是合成的：短的使用者訊息、一串工具卡、長短不一的回覆（段落、清單、
// 程式碼、表格），其中一部分帶圖片（單張的媒體圖、回覆裡的 Markdown 圖片；圖片的回應另外延遲）。手機尺寸（390×844、觸控）、
// CPU 以 CDP 降速，以真的觸控手勢（CDP 的觸控事件：手指拖一段再放開，有慣性）一直往上滑到最前面，記：
//   - 滑到最前面花了多久、其中有多久停在「頂端但還有更早的訊息」（撞牆）；
//   - 主執行緒的長工作與影格之間最長的間隔（卡頓）；
//   - 看得到的訊息自己移動（Layout Instability API 的 layout-shift：瀏覽器捲動與捲動錨定補掉的都不算，算到的就是畫面真的跳了）；
//   - 觸控捲動期間（手指按著或慣性還在）程式寫了幾次 scrollTop（iOS Safari 在捲動中被改捲動位置會跳一下、慣性中斷；Chrome 不會，所以另外數）。
// 兩種瀏覽器各量一次：有原生捲動錨定的（Chrome、Safari 27 起）與沒有的（iOS Safari 26 以前，以 overflow-anchor: none 模擬）。
// 用法：node older-scroll.mjs [--pages 4] [--cpu 4] [--latency 150] [--img 120] [--mode flick|read] [--anchor native|none|both] [--runs 1]
//   前後比較：MYRIAGENT_PROBE_HUB_WEB、MYRIAGENT_PROBE_HUB_WWWROOT 指到 git archive 出來的舊版 src/Myriagent/Web 與 src/MyriagentHub/wwwroot
import { chromium } from 'playwright';
import * as HP from './hub-page.mjs';
import * as LF from './live-fixtures.mjs';

const args = process.argv.slice(2);
const num = (name, def) => { const i = args.indexOf(name); return i >= 0 ? Number(args[i + 1]) : def; };
const str = (name, def) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : def; };
// --trace：另錄追蹤，彙總滑動期間渲染程序主執行緒上各類工作的毫秒數，與被腳本強迫的排版是誰叫的（最上面兩格的函式、檔案:行）
const TRACE_NAMES = ['FunctionCall', 'EvaluateScript', 'v8.callFunction', 'ParseHTML', 'UpdateLayoutTree', 'Layout', 'PrePaint', 'Paint', 'Layerize', 'Commit', 'HitTest', 'MajorGC', 'MinorGC', 'IntersectionObserverController::computeIntersections', 'ResizeObserverController::deliverObservations'];
const PAGE_SIZE = 80;
const NOW = Date.UTC(2026, 10, 18, 2, 0, 0);
const id = LF.CONV.idle;

export const PROBE_IMAGE = '<svg xmlns="http://www.w3.org/2000/svg" width="1170" height="2532" viewBox="0 0 1170 2532"><rect width="1170" height="2532" fill="#27528f"/>'
  + '<rect x="60" y="120" width="1050" height="300" rx="24" fill="#3d7bd1"/><text x="585" y="1300" font-size="96" text-anchor="middle" fill="#fff">1170×2532</text></svg>';
const WIDE_IMAGE = '<svg xmlns="http://www.w3.org/2000/svg" width="1600" height="900" viewBox="0 0 1600 900"><rect width="1600" height="900" fill="#2c6b4f"/>'
  + '<text x="800" y="480" font-size="96" text-anchor="middle" fill="#fff">1600×900</text></svg>';

/** 合成的一頁（80 則，舊到新）：依輪排——使用者訊息、幾張工具卡、一則回覆；沒有亂數（同樣的參數每次一樣） */
export function historyPage(pageIndex, now = NOW) {
  const items = [];
  const base = pageIndex * PAGE_SIZE;
  const para = (k) => `<p>第 ${k} 段：把縮圖產生搬到背景執行緒，<code>ThumbnailService</code> 改成佇列並加上 LRU 快取；<strong>首次載入</strong>從 2.1 秒降到 0.3 秒，記憶體上限 64 MB，佇列滿了就丟掉最舊的工作。</p>`;
  const list = (k) => `<ul><li>步驟 ${k}-1：新增 <code>LruCache</code></li><li>步驟 ${k}-2：縮圖改在背景產生</li><li>步驟 ${k}-3：跑 <code>dotnet test Myriagent.slnx -c Release</code></li></ul>`;
  const code = (k) => `<pre><code class="language-csharp">// 第 ${k} 個片段\nvar cache = new LruCache&lt;string, Bitmap&gt;(64 * 1024 * 1024);\nforeach (var key in keys)\n{\n    cache.GetOrAdd(key, Load);\n}\nreturn cache.Count;\n</code></pre>`;
  const table = (k) => `<table><thead><tr><th>項目 ${k}</th><th>前</th><th>後</th></tr></thead><tbody><tr><td>首次載入</td><td>2.1 秒</td><td>0.3 秒</td></tr><tr><td>記憶體</td><td>310 MB</td><td>96 MB</td></tr></tbody></table>`;
  let turn = 0;
  while (items.length < PAGE_SIZE) {
    turn++;
    const n = base + items.length;
    const push = (it) => { if (items.length < PAGE_SIZE) items.push(Object.assign({ id: 'h' + String(base + items.length).padStart(5, '0'), status: 'Done', time: now - (100000 - (base + items.length)) * 1000, seq: base + items.length + 1 }, it)); };
    push({ kind: 'User', html: '<p>' + '縮圖還是很慢，幫我看一下為什麼卡住介面。'.repeat(1 + (n % 3)) + '</p>', attachments: [] });
    const tools = 2 + (n % 5);
    for (let k = 0; k < tools; k++) {
      push(k % 3 === 2
        ? { kind: 'Tool', toolKind: 'edit', toolName: 'Edit', title: 'src/Myriagent/Media/ThumbnailService.cs', output: '', html: '', filePath: 'C:\\repo\\app\\ThumbnailService.cs',
          diff: [{ t: ' ', s: 'public sealed class ThumbnailService' }, { t: '-', s: '    private readonly object gate = new();' }, { t: '+', s: '    private readonly LruCache<string, Bitmap> cache = new(64 << 20);' }], diffStats: { add: 1, del: 1 } }
        : { kind: 'Tool', toolKind: k % 2 ? 'read' : 'execute', toolName: k % 2 ? 'Read' : 'Bash', title: k % 2 ? 'src/Myriagent/Media/ThumbnailService.cs' : 'dotnet build Myriagent.slnx -c Release', output: '建置成功。\n    0 個警告\n    0 個錯誤\n', html: '' });
    }
    // 回覆：短、中、長輪流；每 4 輪一張單張的媒體圖，每 5 輪一張回覆裡的圖
    const size = turn % 3;
    let html = para(turn);
    if (size >= 1) html += list(turn) + code(turn) + para(turn + 1);
    if (size === 2) html += table(turn) + para(turn + 2) + code(turn + 1) + list(turn + 1) + para(turn + 3);
    const reply = { kind: 'Assistant', html };
    if (turn % 4 === 0) reply.media = [{ url: '/m/shot-' + pageIndex + '-' + turn, kind: 'image', name: '截圖 ' + turn + '.png' }];
    if (turn % 5 === 0) reply.html += '<p><img src="/m/wide-' + pageIndex + '-' + turn + '" alt="對照圖 ' + turn + '"></p><p>圖片下面的說明。</p>';
    push(reply);
  }
  return items;
}

/** 頁面裡的量測（載入對話之後裝）：長工作、影格、layout-shift、觸控捲動期間的 scrollTop 寫入、頂端的空白 */
export function installMeter([sel, total]) {
  const sc = document.querySelector(sel + ' .scroller');
  const itemsEl = sc.querySelector('.items');
  const m = window.__os = { t0: performance.now(), longs: [], shifts: [], writes: [], trail: [], frames: 0, gaps: [], wallMs: 0, blankMs: 0, blankMax: 0, topAt: null, count: 0, touches: 0, stop: false };
  new PerformanceObserver((l) => { for (const e of l.getEntries()) m.longs.push({ t: Math.round(e.startTime - m.t0), d: Math.round(e.duration) }); }).observe({ type: 'longtask' });
  // 看得到的訊息自己移動：只算對話欄裡的節點，取這一筆裡移動最多的那個
  new PerformanceObserver((l) => {
    for (const e of l.getEntries()) {
      let dy = 0, who = null;
      for (const s of e.sources || []) {
        const n = s.node && s.node.nodeType === 1 ? s.node : s.node && s.node.parentElement;
        const it = n && n.closest ? n.closest('.it') : null;
        if (!it || !itemsEl.contains(it)) continue;
        const d = s.currentRect.top - s.previousRect.top;
        if (Math.abs(d) > Math.abs(dy)) { dy = d; who = it.dataset.id; }
      }
      if (Math.abs(dy) >= 2) m.shifts.push({ t: Math.round(e.startTime - m.t0), dy: Math.round(dy), id: who, scrolling: scrolling(performance.now()) });
    }
  }).observe({ type: 'layout-shift', buffered: false });
  // 觸控捲動中：手指按著，或放開後 3 秒內最近一次捲動事件不到 150 ms（同 liverules.touchScrolling）
  const touch = { down: false, endAt: null, scrollAt: null };
  const scrolling = (now) => touch.down || (touch.endAt !== null && touch.scrollAt !== null && now - touch.endAt < 3000 && now - touch.scrollAt < 150);
  sc.addEventListener('touchstart', () => { touch.down = true; m.touches++; }, { capture: true, passive: true });
  const up = (e) => { if (!e.touches || !e.touches.length) { touch.down = false; touch.endAt = performance.now(); } };
  sc.addEventListener('touchend', up, { capture: true, passive: true });
  sc.addEventListener('touchcancel', up, { capture: true, passive: true });
  sc.addEventListener('scroll', () => { touch.scrollAt = performance.now(); }, { capture: true, passive: true });
  const desc = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollTop');
  Object.defineProperty(sc, 'scrollTop', {
    configurable: true,
    get() { return desc.get.call(this); },
    set(v) {
      const before = desc.get.call(this);
      desc.set.call(this, v);
      const now = performance.now();
      const d = desc.get.call(this) - before;
      if (Math.abs(d) >= 1 && scrolling(now)) m.writes.push({ t: Math.round(now - m.t0), d: Math.round(d), down: touch.down });
    },
  });
  let last = performance.now();
  const tick = () => {
    if (m.stop) return;
    const now = performance.now();
    const gap = now - last;
    last = now;
    m.frames++;
    if (gap > 50) m.gaps.push({ t: Math.round(now - m.t0), d: Math.round(gap) });
    const st = desc.get.call(sc);
    const first = itemsEl.firstElementChild;
    const count = itemsEl.children.length;
    m.count = count;
    // 每個影格的捲動位置、整個內容的高度、預留空間的高度、DOM 裡的則數、是不是在觸控捲動中（查位移發生時在做什麼）
    const pad = sc.querySelector('.older-pad');
    m.trail.push([Math.round(now - m.t0), Math.round(st), sc.scrollHeight, pad && !pad.hidden ? Math.round(pad.offsetHeight) : 0, count, scrolling(now) ? 1 : 0]);
    // 畫面上緣到第一則訊息之間露出多少不是訊息的地方（「載入較早的訊息」那一列、預留的空間）
    const blank = first ? Math.max(0, Math.min(sc.clientHeight, first.getBoundingClientRect().top - sc.getBoundingClientRect().top)) : 0;
    if (count < total) {
      if (st <= 1) m.wallMs += gap; // 到頂了，但還有更早的訊息沒放進來
      if (blank > 120) m.blankMs += gap;
      if (blank > m.blankMax) m.blankMax = Math.round(blank);
    } else if (st <= 1 && m.topAt === null) {
      m.topAt = Math.round(now - m.t0);
    }
    requestAnimationFrame(tick);
  };
  requestAnimationFrame(tick);
}

/**
 * 手指往下拖一段再放開（內容往上捲）：每 16 ms 一步，事件帶明確的時間戳（Chrome 依時間戳算放開時的速度，CDP 的延遲不穩，
 * 不帶的話慣性時有時無）。step 45 約 2800 px／秒，放開後再滑行約 1500px、1.1 秒
 */
export async function flick(cdp, at, step = 45) {
  const pt = (y) => [{ x: at.x, y: Math.round(y), id: 1 }];
  let y = at.y;
  let ts = Date.now() / 1000;
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: pt(y), timestamp: ts });
  for (let k = 0; k < 8; k++) {
    y += step;
    ts += 0.016;
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: pt(y), timestamp: ts });
    await HP.sleep(16);
  }
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [], timestamp: ts + 0.008 });
}

/**
 * 開一個手機尺寸的頁面、一路往上滑到最前面，回量到的數字（欄位見檔頭）。o＝{ anchor：'native'（瀏覽器照常）或 'none'（關掉原生捲動錨定）、
 * pages：第一頁之外還有幾頁、cpu、latency、img、mode、limit、css、trace、around }
 */
export async function runHistoryScroll(H, o = {}) {
  const anchor = o.anchor || 'native';
  const PAGES = o.pages ?? 4;
  const CPU = o.cpu ?? 4;
  const LATENCY = o.latency ?? 150;
  const IMG_MS = o.img ?? 120;
  const MODE = o.mode || 'flick';
  const LIMIT_MS = o.limit ?? 60000;
  const CSS = o.css || '';
  const TRACE = !!o.trace;
  const pages = Array.from({ length: PAGES + 1 }, (_, k) => historyPage(k));
  const total = pages.reduce((a, p) => a + p.length, 0);
  const layout = { panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: true, view: 'chat' };
  const app = await H.openHub('older-scroll', {
    vp: { w: 390, h: 844 }, hasTouch: true,
    initScript: `document.addEventListener('DOMContentLoaded', () => { const st = document.createElement('style'); st.textContent = ${JSON.stringify((anchor === 'none' ? '.scroller { overflow-anchor: none !important; } ' : '') + CSS)}; document.head.append(st); });`,
    init: (ready) => LF.liveBrowserInit(NOW, { mode: ready && ready.mode, layout }),
    hub: (hub) => {
      hub.onCmd('open', (cmd) => ({ push: [LF.conversationMessage(NOW, cmd.conversationId, '歷史很長的對話', { items: pages[PAGES], hasMore: PAGES > 0, composer: { queue: [], queueMore: 0 } })] }));
      hub.onCmd('loadOlder', (cmd) => {
        const at = pages.findIndex((p) => p[0].id === cmd.beforeId);
        if (at > 0) setTimeout(() => hub.push({ type: 'olderItems', conversationId: cmd.conversationId, beforeId: cmd.beforeId, items: pages[at - 1], hasMore: at - 1 > 0 }), LATENCY);
        return { push: [] };
      });
    },
  });
  // 圖片晚一點才回（真的網路）：後登記的路由先比對
  await app.context.route(HP.ORIGIN + '/m/**', async (route) => {
    const p = new URL(route.request().url()).pathname;
    await HP.sleep(IMG_MS);
    await route.fulfill({ status: 200, headers: { 'Content-Type': 'image/svg+xml', 'Cache-Control': 'private, max-age=3600' }, body: p.startsWith('/m/wide') ? WIDE_IMAGE : PROBE_IMAGE }).catch(() => {});
  });
  await HP.waitLive(app);
  const sel = '.conv[data-conv="' + id + '"]';
  await HP.until(() => app.page.evaluate((s) => { const r = document.querySelector(s); return !!r && !r.classList.contains('loading') && !!r.querySelector('.items > .it'); }, sel), 10000);
  await HP.sleep(1500); // 第一頁的分批與圖片都安定下來
  const cdp = await app.context.newCDPSession(app.page);
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: CPU });
  await app.page.evaluate(installMeter, [sel, total]);
  const box = await app.page.evaluate((s) => { const r = document.querySelector(s + ' .scroller').getBoundingClientRect(); return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height * 0.35) }; }, sel);
  let traced = null;
  if (TRACE) {
    traced = [];
    cdp.on('Tracing.dataCollected', (e) => traced.push(...e.value));
    await cdp.send('Tracing.start', { categories: 'devtools.timeline,v8.execute,disabled-by-default-devtools.timeline.stack', transferMode: 'ReportEvents' });
  }
  const t0 = Date.now();
  let flicks = 0;
  let done = false;
  while (!done && Date.now() - t0 < LIMIT_MS) {
    flicks++;
    await flick(cdp, box);
    // flick：慣性還在跑就再滑下一次（手指一按下慣性就停）；read：等慣性停下來、看一下再滑
    await HP.sleep(MODE === 'read' ? 1700 : 420);
    done = await app.page.evaluate(() => window.__os.topAt !== null);
  }
  await HP.sleep(600);
  let breakdown;
  if (TRACE) {
    const ended = new Promise((res) => cdp.once('Tracing.tracingComplete', res));
    await cdp.send('Tracing.end');
    await ended;
    const main = new Set(traced.filter((e) => e.name === 'thread_name' && e.args && e.args.name === 'CrRendererMain').map((e) => e.pid + ':' + e.tid));
    breakdown = {};
    const forced = {};
    for (const e of traced) {
      if (e.ph !== 'X' || !main.has(e.pid + ':' + e.tid)) continue;
      if (TRACE_NAMES.includes(e.name)) breakdown[e.name] = (breakdown[e.name] || 0) + e.dur / 1000;
      if (e.name !== 'Layout' && e.name !== 'UpdateLayoutTree') continue;
      const st = (e.args && e.args.beginData && e.args.beginData.stackTrace) || (e.args && e.args.data && e.args.data.stackTrace);
      const key = e.name + ' ' + (st && st.length ? st.slice(0, 2).map((f) => (f.functionName || '?') + '@' + String(f.url).split('/').pop() + ':' + f.lineNumber).join(' < ') : '（影格裡，不是腳本強迫的）');
      const v = forced[key] || (forced[key] = { ms: 0, n: 0 });
      v.ms += e.dur / 1000;
      v.n++;
    }
    for (const k of Object.keys(breakdown)) breakdown[k] = Math.round(breakdown[k]);
    breakdown.layoutBy = Object.entries(forced).sort((a, b) => b[1].ms - a[1].ms).slice(0, 8).map(([k, v]) => `${Math.round(v.ms)}ms×${v.n} ${k}`);
  }
  const m = await app.page.evaluate(() => { const o = window.__os; o.stop = true; return o; });
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 1 });
  const errors = HP.unexpectedErrors(app).map((e) => e.text).slice(0, 5);
  await HP.closePage(app);
  const sum = (xs, f) => xs.reduce((a, x) => a + f(x), 0);
  const moving = m.shifts.filter((s) => s.scrolling);
  // 觸控捲動中（手指按著或慣性還在）放進 DOM 的則數：前後兩個影格都在捲動中、則數變多的合計
  let placedWhileScrolling = 0;
  for (let k = 1; k < m.trail.length; k++) if (m.trail[k][5] && m.trail[k - 1][5] && m.trail[k][4] > m.trail[k - 1][4]) placedWhileScrolling += m.trail[k][4] - m.trail[k - 1][4];
  return {
    anchor, mode: MODE, cpu: CPU, pages: PAGES + 1, total, shown: m.count, reachedTop: done, topAt: m.topAt, flicks, touches: m.touches,
    wallMs: Math.round(m.wallMs), blankMs: Math.round(m.blankMs), blankMax: m.blankMax,
    longTasks: m.longs.length, longMax: Math.max(0, ...m.longs.map((l) => l.d)), longSum: sum(m.longs, (l) => l.d),
    frames: m.frames, gapsOver50: m.gaps.length, gapsOver100: m.gaps.filter((g) => g.d > 100).length, gapMax: Math.max(0, ...m.gaps.map((g) => g.d)),
    shifts: m.shifts.length, shiftPx: sum(m.shifts, (s) => Math.abs(s.dy)), shiftsWhileScrolling: moving.length, shiftMax: Math.max(0, ...m.shifts.map((s) => Math.abs(s.dy))),
    shiftList: m.shifts.slice(0, 8),
    // 每次位移前後 250 ms 的影格：[毫秒, scrollTop, scrollHeight, 預留空間, 則數, 捲動中]
    // 露出預留空間的每一段：[開始毫秒, 持續毫秒, 最多露出幾 px, 開始時 DOM 裡的則數, 結束時的則數]
    blanks: (() => { const out = []; let cur = null; for (const f of m.trail) { const b = Math.max(0, f[3] + 16 - f[1]); if (b > 120 && f[4] < total) { if (!cur) { cur = [f[0], 0, 0, f[4], f[4]]; out.push(cur); } cur[1] = f[0] - cur[0]; cur[2] = Math.max(cur[2], Math.min(b, 550)); cur[4] = f[4]; } else cur = null; } return out; })(),
    around: o.around ? m.shifts.slice(0, 4).map((sh) => m.trail.filter((f) => Math.abs(f[0] - sh.t) <= 250)) : undefined,
    placedWhileScrolling, writesWhileScrolling: m.writes.length, writesFingerDown: m.writes.filter((w) => w.down).length, writePx: sum(m.writes, (w) => Math.abs(w.d)),
    errors,
    ...(breakdown ? { breakdown } : {}),
  };
}

if (process.argv[1] && process.argv[1].split(/[/\\]/).pop() === 'older-scroll.mjs') {
  // --pages：第一頁之外還有幾頁較早的；--latency：loadOlder 多久之後回；--img：圖片多久之後回；
  // --mode flick（一直滑不停）｜read（每滑一次停下來看 1.7 秒）；--css "<規則>"：在網頁的樣式之後多疊一段 CSS（A/B）；
  // --around：另附每次位移前後 250 ms 的影格
  const ANCHOR = str('--anchor', 'both');
  const RUNS = num('--runs', 1);
  const opts = { pages: num('--pages', 4), cpu: num('--cpu', 4), latency: num('--latency', 150), img: num('--img', 120), mode: str('--mode', 'flick'), limit: num('--limit', 60000),
    css: str('--css', ''), trace: args.includes('--trace'), around: args.includes('--around') };
  const browser = await chromium.launch({ channel: 'chrome', headless: true });
  const H = HP.createPages(browser, { now: NOW });
  const results = [];
  for (const anchor of ANCHOR === 'both' ? ['native', 'none'] : [ANCHOR]) {
    for (let run = 0; run < RUNS; run++) {
      const r = await runHistoryScroll(H, { ...opts, anchor });
      results.push(r);
      console.log(JSON.stringify(r));
    }
  }
  await browser.close();
  console.log(JSON.stringify({ web: HP.WEB, results: results.length }));
}
