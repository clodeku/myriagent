// 瀏覽器版在手機上開啟對話的耗時（2026-11-05 調查「手機版載入對話變慢」時寫的）：
// 假 hub 收到 open 先不回，探針裝好量測再推一則 conversation（items 是伺服器真的渲染結果，由
// tests/MyriagentHub.Tests/Rendering/SnapshotRenderBench 以 MYRIAGENT_SNAPSHOT_DUMP 匯出到暫存資料夾；內容是真的對話，不進 repo）。
// 手機尺寸（390×844、觸控）、CPU 以 CDP 降速，記：收到之後多久拿掉「載入中」（第一批畫好）、全部批次補完、這段期間的長工作。
// 用法：node open-cost.mjs <頁面 JSON 的資料夾> [--runs 3] [--cpu 4]
//   前後比較：MYRIAGENT_PROBE_HUB_WEB、MYRIAGENT_PROBE_HUB_WWWROOT 指到 git archive 出來的舊版 src/Myriagent/Web 與 src/MyriagentHub/wwwroot
import { chromium } from 'playwright';
import fs from 'node:fs';
import path from 'node:path';
import * as HP from './hub-page.mjs';
import * as LF from './live-fixtures.mjs';

const args = process.argv.slice(2);
const dir = args[0];
const opt = (name, def) => { const i = args.indexOf(name); return i >= 0 ? Number(args[i + 1]) : def; };
const RUNS = opt('--runs', 3);
// --tabs N：版面裡有 N 個分頁、看得到的是最後一個（瀏覽器版 init 與重連時每個分頁都送 open，伺服器照順序回）；其他分頁輪流用別的頁面
const TABS = opt('--tabs', 1);
// --css "<規則>"：在網頁的樣式之後多疊一段 CSS（A/B：逐項關掉某個效果看差多少）
// --trace：每次開啟另錄追蹤，彙總從推送到全部補完之間渲染程序主執行緒上各類工作的毫秒數（樣式、排版、繪製、腳本、解析 HTML）
const TRACE = args.includes('--trace');
const TRACE_NAMES = ['FunctionCall', 'EvaluateScript', 'v8.callFunction', 'ParseHTML', 'UpdateLayoutTree', 'Layout', 'PrePaint', 'Paint', 'Layerize', 'Commit', 'HitTest', 'MajorGC', 'MinorGC'];
const CSS = (() => { const i = args.indexOf('--css'); return i >= 0 ? args[i + 1] : ''; })();
const CPU = opt('--cpu', 4);
const NOW = Date.UTC(2026, 10, 5, 2, 0, 0);
const files = fs.readdirSync(dir).filter((f) => f.endsWith('.json')).sort();
const id = LF.CONV.idle;
const others = Array.from({ length: TABS - 1 }, (_, k) => LF.convId(200 + k));
const layout = { panes: [{ id: 'p1', tabs: [...others, id], active: id }], focusedPane: 'p1', sidebarCollapsed: true, view: 'chat' };
const pageOf = (k) => JSON.parse(fs.readFileSync(path.join(dir, files[k % files.length]), 'utf8'));

const browser = await chromium.launch({ channel: 'chrome', headless: true });
const H = HP.createPages(browser, { now: NOW });
const results = [];
for (const f of files) {
  const items = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
  for (let run = 0; run < RUNS; run++) {
    const app = await H.openHub('open-cost', {
      vp: { w: 390, h: 844 }, hasTouch: true,
      initScript: CSS ? `document.addEventListener('DOMContentLoaded', () => { const st = document.createElement('style'); st.textContent = ${JSON.stringify(CSS)}; document.head.append(st); });` : undefined,
      init: (ready) => LF.liveBrowserInit(NOW, { mode: ready && ready.mode, layout }),
      hub: (hub) => hub.onCmd('open', () => ({ push: [] })),
    });
    await HP.waitLive(app);
    await HP.until(() => app.hub.cmds('open').length >= TABS || null, 8000);
    const cdp = await app.context.newCDPSession(app.page);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: CPU });
    await app.page.evaluate(([cid, n]) => {
      // settled：全部補完而且沒有先不排版的項目（conversation.js 的 it-lazy）
      const oc = window.__oc = { t0: performance.now(), first: null, done: null, settled: null, longs: [], count: 0 };
      new PerformanceObserver((l) => { for (const e of l.getEntries()) if (e.startTime >= oc.t0) oc.longs.push(Math.round(e.duration)); }).observe({ type: 'longtask' });
      let last = -1;
      let stable = 0;
      const tick = () => {
        const root = document.querySelector('.conv[data-conv="' + cid + '"]');
        if (root && !oc.first && !root.classList.contains('loading')) oc.first = performance.now() - oc.t0;
        const c = root ? root.querySelectorAll('.items > .it').length : 0;
        oc.count = c;
        stable = c === last && c > 0 ? stable + 1 : 0;
        last = c;
        if (oc.done == null && (c >= n || stable >= 20)) oc.done = performance.now() - oc.t0 - (c >= n ? 0 : 20 * 16.7);
        if (oc.done != null && !root.querySelector('.it-lazy')) { oc.settled = performance.now() - oc.t0; return; }
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    }, [id, items.length]);
    let traced = null;
    if (TRACE) {
      traced = [];
      cdp.on('Tracing.dataCollected', (e) => traced.push(...e.value));
      await cdp.send('Tracing.start', { categories: 'devtools.timeline,v8.execute,disabled-by-default-devtools.timeline.stack', transferMode: 'ReportEvents' });
    }
    // 照網頁送 open 的順序回（同伺服器：每條連線先到先回）。輸入區沒有排隊的訊息（大部分對話；假資料預設帶一則，
    // 會讓 composer 的 queueIsOpen 讀 clientHeight、多一次強迫排版）
    const empty = { queue: [], queueMore: 0 };
    for (const c of app.hub.cmds('open').map((x) => x.cmd.conversationId)) {
      const k = others.indexOf(c);
      app.hub.push(c === id ? LF.conversationMessage(NOW, id, '量測', { items, hasMore: true, composer: empty })
        : LF.conversationMessage(NOW, c, '其他分頁 ' + k, { items: pageOf(k + 1), hasMore: true, composer: empty }));
    }
    const oc = await HP.until(() => app.page.evaluate(() => (window.__oc.settled != null ? window.__oc : null)), 60000, 100);
    let breakdown;
    if (TRACE) {
      const ended = new Promise((res) => cdp.once('Tracing.tracingComplete', res));
      await cdp.send('Tracing.end');
      await ended;
      const main = new Set(traced.filter((e) => e.name === 'thread_name' && e.args && e.args.name === 'CrRendererMain').map((e) => e.pid + ':' + e.tid));
      breakdown = {};
      for (const e of traced) {
        if (e.ph !== 'X' || !main.has(e.pid + ':' + e.tid) || !TRACE_NAMES.includes(e.name)) continue;
        breakdown[e.name] = (breakdown[e.name] || 0) + e.dur / 1000;
      }
      for (const k of Object.keys(breakdown)) breakdown[k] = Math.round(breakdown[k]);
      // 被腳本強迫的排版與樣式重算：依呼叫的位置（最上面那一格的函式、檔案:行）彙總毫秒數與次数
      const forced = {};
      for (const e of traced) {
        if (e.ph !== 'X' || !main.has(e.pid + ':' + e.tid) || (e.name !== 'Layout' && e.name !== 'UpdateLayoutTree')) continue;
        const st = (e.args && e.args.beginData && e.args.beginData.stackTrace) || (e.args && e.args.data && e.args.data.stackTrace);
        if (!st || !st.length) continue;
        const key = e.name + ' ' + st.slice(0, 2).map((f) => (f.functionName || '?') + '@' + String(f.url).split('/').pop() + ':' + f.lineNumber).join(' < ');
        const v = forced[key] || (forced[key] = { ms: 0, n: 0 });
        v.ms += e.dur / 1000;
        v.n++;
      }
      breakdown.forced = Object.entries(forced).sort((a, b) => b[1].ms - a[1].ms).slice(0, 6).map(([k, v]) => `${Math.round(v.ms)}ms×${v.n} ${k}`);
    }
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: 1 });
    const r = oc
      ? { file: f, run, kb: Math.round(JSON.stringify(items).length / 1024), items: items.length, shown: oc.count, first: Math.round(oc.first), done: Math.round(oc.done), settled: Math.round(oc.settled),
        longMax: Math.max(0, ...oc.longs), longSum: oc.longs.reduce((a, b) => a + b, 0), ...(breakdown ? { breakdown } : {}) }
      : { file: f, run, timeout: true };
    results.push(r);
    console.log(JSON.stringify(r));
    await HP.closePage(app);
  }
}
await browser.close();
const med = (xs) => { const s = xs.slice().sort((a, b) => a - b); return s[Math.floor(s.length / 2)]; };
const ok = results.filter((r) => !r.timeout);
console.log(JSON.stringify({ web: HP.WEB, css: CSS || undefined, cpu: CPU, tabs: TABS, runs: ok.length, firstMedian: med(ok.map((r) => r.first)), doneMedian: med(ok.map((r) => r.done)), settledMedian: med(ok.map((r) => r.settled)),
  longMaxMedian: med(ok.map((r) => r.longMax)), longSumMedian: med(ok.map((r) => r.longSum)) }));
