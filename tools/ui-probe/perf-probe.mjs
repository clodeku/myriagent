// 介面的持續耗用（2026-10-23 效能分析）：probe.mjs 匯入、--perf 時只跑這一組。
// 實機量到 WebView2 的渲染程序在有對話執行時一直吃掉約一顆核心：持續的 CSS 動畫若動的是背景位置、背景大小這類不能交給合成器的屬性，
// 主執行緒每一幀都要重算樣式並重畫。這裡以 1920×1080、四個窗格（c1、c3 執行中）、已配對的電腦狀態面板（每秒一筆 deviceStats），
// 量幾個情境下主執行緒的忙碌比例（CDP Performance.getMetrics 的 TaskDuration 差）與這個瀏覽器各程序（渲染、GPU、其他；
// CDP SystemInfo.getProcessInfo 的 CPU 時間差）的 CPU，並列出還在跑的無限循環動畫與它們動的屬性。
// 檢查：執行中的畫面沒有任何「動非合成屬性」的無限循環動畫（perfNoMainThreadLoops*）；減少動態、設定關掉動態效果時只剩載入中的轉圈。
// 耗用的數字只記在報告（機器與負載不同，不設門檻）。
import fs from 'node:fs';
import { pumpSetup, devices, SELF } from './monitor-probe.mjs';
import { syncStatus } from './sync-settings.mjs';
import { groupOf } from './fixtures.mjs';

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const FOUR_PANES = { panes: [{ id: 'p1', tabs: ['c1'], active: 'c1' }, { id: 'p2', tabs: ['c3'], active: 'c3' }, { id: 'p3', tabs: ['c4'], active: 'c4' }, { id: 'p4', tabs: ['c7'], active: 'c7' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };
const DASH = Object.assign({}, FOUR_PANES, { view: 'dashboard' });
/** 合成器就能跑、不必主執行緒每幀處理的屬性 */
const COMPOSITED = new Set(['transform', 'opacity', 'translate', 'rotate', 'scale']);

/** 頁面內：還在跑的無限循環 CSS 動畫（名稱、目標、動的屬性、是否只動合成屬性） */
function loopsOf(composited) {
  const ok = new Set(composited);
  const out = [];
  for (const a of document.getAnimations()) {
    if (!(a instanceof CSSAnimation) || a.playState !== 'running' || !a.effect || !a.effect.target) continue;
    if (a.effect.getComputedTiming().iterations !== Infinity) continue;
    const props = new Set();
    for (const k of a.effect.getKeyframes()) {
      for (const p of Object.keys(k)) if (!['offset', 'computedOffset', 'easing', 'composite'].includes(p)) props.add(p.replace(/[A-Z]/g, (c) => '-' + c.toLowerCase()));
    }
    const t = a.effect.target;
    const cls = typeof t.className === 'string' ? t.className : (t.className && t.className.baseVal) || t.tagName;
    // SVG 元素的 transform 不能交給合成器（Chrome：SVG 目標有獨立的 transform 屬性），要轉就轉包住它的 HTML 元素
    out.push({ name: a.animationName, target: String(cls).trim().split(/\s+/).slice(0, 2).join('.') + (a.effect.pseudoElement || ''), props: [...props], composited: !(t instanceof SVGElement) && [...props].every((p) => ok.has(p)) });
  }
  return out;
}

/**
 * 這個瀏覽器每個程序的 CPU 時間（程序 id → {kind: renderer／gpu／other, ms}）：CDP 的 SystemInfo.getProcessInfo 只列這個瀏覽器自己的程序
 * （同時有別的探針或端到端開著 Chrome 也不會算錯）。拿不到時回 null
 */
async function processCpu(browserCdp) {
  try {
    const { processInfo } = await browserCdp.send('SystemInfo.getProcessInfo');
    const byId = new Map();
    for (const p of processInfo) {
      const type = String(p.type || '').toLowerCase();
      byId.set(p.id, { kind: type === 'renderer' ? 'renderer' : type === 'gpu' ? 'gpu' : 'other', ms: Number(p.cpuTime) * 1000 });
    }
    return byId;
  } catch {
    return null;
  }
}

/** 這段時間主執行緒的耗用（毫秒／每秒）：工作、腳本、樣式重算、版面；整個瀏覽器程序樹的 CPU（毫秒／每秒，1000＝一顆核心） */
async function measure(page, ms) {
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Performance.enable', { timeDomain: 'timeTicks' });
  const get = (m, k) => (m.metrics.find((x) => x.name === k) || { value: 0 }).value;
  const browserCdp = await page.context().browser().newBrowserCDPSession();
  const c0 = await processCpu(browserCdp);
  const t0 = Date.now();
  const m0 = await cdp.send('Performance.getMetrics');
  await sleep(ms);
  const m1 = await cdp.send('Performance.getMetrics');
  const c1 = await processCpu(browserCdp);
  await browserCdp.detach().catch(() => {});
  const wall = (Date.now() - t0) / 1000;
  const secs = get(m1, 'Timestamp') - get(m0, 'Timestamp');
  const per = (k) => Math.round(((get(m1, k) - get(m0, k)) / secs) * 1000);
  // threadMsPerSec：主執行緒真的用掉的 CPU（ThreadTime；別的程式搶 CPU 時 TaskDuration 會被拉長，這個比較不受影響）
  const r = { busyMsPerSec: per('TaskDuration'), threadMsPerSec: per('ThreadTime'), scriptMsPerSec: per('ScriptDuration'), styleMsPerSec: per('RecalcStyleDuration'), layoutMsPerSec: per('LayoutDuration'),
    layoutsPerSec: Math.round((get(m1, 'LayoutCount') - get(m0, 'LayoutCount')) / secs), styleRecalcsPerSec: Math.round((get(m1, 'RecalcStyleCount') - get(m0, 'RecalcStyleCount')) / secs) };
  // 只算兩次快照都在的程序（量測期間結束的程序〔上一個情境關掉的渲染程序〕CPU 時間會不見，新開的沒有起點）
  if (c0 && c1) {
    const sum = { renderer: 0, gpu: 0, other: 0 };
    for (const [pid, after] of c1) if (c0.has(pid)) sum[after.kind] += after.ms - c0.get(pid).ms;
    r.cpuMsPerSec = Object.fromEntries(Object.entries(sum).map(([k, v]) => [k, Math.round(v / wall)]));
  }
  // MYRIAGENT_PERF_TRACE=1：另外錄 2 秒追蹤，列出沒能交給合成器的動畫與原因（Chrome 的 compositeFailed 位元與不支援的屬性）
  if (process.env.MYRIAGENT_PERF_TRACE) r.notComposited = await traceNotComposited(cdp);
  // MYRIAGENT_PERF_JSPROFILE=1：另外錄 3 秒 JS 剖析，列出自身耗時最多的網頁函式（檔名:行 函式名）
  if (process.env.MYRIAGENT_PERF_JSPROFILE) r.jsProfile = await jsProfile(cdp);
  // MYRIAGENT_PERF_INVALIDATION=1：另外錄 2 秒帶失效追蹤的追蹤，列出樣式重算是誰觸發的（traceInvalidations）
  if (process.env.MYRIAGENT_PERF_INVALIDATION) r.invalidations = await traceInvalidations(cdp);
  // MYRIAGENT_PERF_LAYERS=1：列出合成圖層（依建立的原因與元素統計）
  if (process.env.MYRIAGENT_PERF_LAYERS) r.layers = await layerReasons(cdp);
  // MYRIAGENT_PERF_RAWTRACE=<檔案>：另外錄 2 秒完整的追蹤（含失效追蹤與堆疊）存成 JSON，給離線分析（trace-frames.py：依觸發來源分類每一格畫面）
  if (process.env.MYRIAGENT_PERF_RAWTRACE) await rawTrace(cdp, process.env.MYRIAGENT_PERF_RAWTRACE);
  await cdp.detach().catch(() => {});
  return r;
}

/**
 * 這段時間內頁面派送的 CSS 動畫事件（animationiteration 等）與主執行緒的影格數（PrePaint 次數／秒）。
 * 網頁自己不聽這些事件：有派送就表示某個元素讓文件設下「有人在聽」的旗標（2026-10-24：Chrome 內建影片控制列的載入轉圈），
 * 從此每個 CSS 動畫每轉一圈都要叫醒主執行緒、整頁的持續動畫跟著重算（影片要經 framedvideo.js 放進 iframe）
 */
async function animationEvents(page, ms) {
  const cdp = await page.context().newCDPSession(page);
  const events = [];
  cdp.on('Tracing.dataCollected', (e) => events.push(...e.value));
  const done = new Promise((resolve) => cdp.once('Tracing.tracingComplete', resolve));
  await cdp.send('Tracing.start', { traceConfig: { includedCategories: ['devtools.timeline', 'disabled-by-default-devtools.timeline'] }, transferMode: 'ReportEvents' });
  await sleep(ms);
  await cdp.send('Tracing.end');
  await done;
  await cdp.detach().catch(() => {});
  // 渲染程序的主執行緒（每個情境只開一個 app 分頁；影片的 iframe 和它同一條）
  const types = {};
  let frames = 0;
  const mains = new Set(events.filter((e) => e.ph === 'M' && e.name === 'thread_name' && e.args && e.args.name === 'CrRendererMain').map((e) => e.pid + ':' + e.tid));
  for (const e of events) {
    if (!mains.has(e.pid + ':' + e.tid)) continue;
    if (e.name === 'EventDispatch' && e.args && e.args.data && /^(webkit)?animation/i.test(e.args.data.type)) types[e.args.data.type] = (types[e.args.data.type] || 0) + 1;
    if (e.name === 'PrePaint' && e.ph === 'X') frames++;
  }
  return { animationEvents: types, mainFramesPerSec: Math.round((frames / (ms / 1000)) * 10) / 10 };
}

async function layerReasons(cdp) {
  await cdp.send('DOM.getDocument', { depth: 0 });
  const tree = new Promise((resolve) => cdp.once('LayerTree.layerTreeDidChange', resolve));
  await cdp.send('LayerTree.enable');
  const { layers } = await tree;
  const byReason = new Map(), byNode = new Map();
  let drawn = 0, area = 0;
  for (const l of layers || []) {
    if (l.drawsContent) { drawn++; area += l.width * l.height; }
    let reasons = [];
    try { reasons = (await cdp.send('LayerTree.compositingReasons', { layerId: l.layerId })).compositingReasonIds || []; } catch { /* 沒有原因 */ }
    let desc = '(無節點)';
    if (l.backendNodeId) {
      try {
        const { node } = await cdp.send('DOM.describeNode', { backendNodeId: l.backendNodeId });
        const cls = (node.attributes || []).reduce((acc, v, i, a) => (a[i - 1] === 'class' ? v : acc), '');
        desc = (node.pseudoType ? '::' + node.pseudoType + ' ' : '') + node.nodeName.toLowerCase() + (cls ? '.' + cls.split(/\s+/)[0] : '');
        if (node.pseudoType && node.parentId) desc += ' (pseudo)';
      } catch { desc = '(找不到節點)'; }
    }
    const rk = reasons.join(',') || '(無)';
    byReason.set(rk, (byReason.get(rk) || 0) + 1);
    byNode.set(desc + ' ← ' + rk, (byNode.get(desc + ' ← ' + rk) || 0) + 1);
  }
  await cdp.send('LayerTree.disable').catch(() => {});
  const list = (m, n) => [...m.entries()].sort((a, b) => b[1] - a[1]).slice(0, n).map(([k, v]) => v + ' × ' + k);
  return { total: (layers || []).length, drawn, megapixels: Math.round(area / 1e5) / 10, byReason: list(byReason, 20), byNode: list(byNode, 60) };
}

async function rawTrace(cdp, file) {
  const events = [];
  cdp.on('Tracing.dataCollected', (e) => events.push(...e.value));
  const done = new Promise((resolve) => cdp.once('Tracing.tracingComplete', resolve));
  await cdp.send('Tracing.start', { traceConfig: { includedCategories: ['devtools.timeline', 'disabled-by-default-devtools.timeline', 'disabled-by-default-devtools.timeline.frame', 'disabled-by-default-devtools.timeline.stack', 'disabled-by-default-devtools.timeline.invalidationTracking', 'disabled-by-default-layout_shift.debug', 'blink', 'cc', 'blink.animations'] }, transferMode: 'ReportEvents' });
  await sleep(2000);
  await cdp.send('Tracing.end');
  await done;
  fs.writeFileSync(file, JSON.stringify({ traceEvents: events }));
}

async function jsProfile(cdp) {
  await cdp.send('Profiler.enable');
  await cdp.send('Profiler.setSamplingInterval', { interval: 200 });
  await cdp.send('Profiler.start');
  await sleep(3000);
  const { profile } = await cdp.send('Profiler.stop');
  const byId = new Map(profile.nodes.map((n) => [n.id, n]));
  const self = new Map();
  const dt = profile.timeDeltas || [];
  for (let i = 0; i < profile.samples.length; i++) {
    const n = byId.get(profile.samples[i]);
    if (!n) continue;
    const f = n.callFrame;
    const key = (f.url ? f.url.split('/').pop() : '(native)') + ':' + (f.lineNumber + 1) + ' ' + (f.functionName || '(anonymous)');
    self.set(key, (self.get(key) || 0) + (dt[i] || 0) / 1000);
  }
  return [...self.entries()].filter(([k]) => !k.startsWith('(native):0 (idle)') && !k.includes('(program)')).sort((a, b) => b[1] - a[1]).slice(0, 18).map(([k, v]) => Math.round(v) + ' ms ' + k);
}

async function traceNotComposited(cdp) {
  const events = [];
  cdp.on('Tracing.dataCollected', (e) => events.push(...e.value));
  const done = new Promise((resolve) => cdp.once('Tracing.tracingComplete', resolve));
  await cdp.send('Tracing.start', { traceConfig: { includedCategories: ['blink.animations', 'devtools.timeline', 'disabled-by-default-devtools.timeline'] }, transferMode: 'ReportEvents' });
  await sleep(2000);
  await cdp.send('Tracing.end');
  await done;
  const failed = new Map();
  for (const e of events) {
    const data = e.args && e.args.data;
    if (e.name !== 'Animation' || !data || !(data.compositeFailed > 0 || (data.unsupportedProperties && data.unsupportedProperties.length))) continue;
    const key = (data.name || data.id || '?') + ' failed=' + data.compositeFailed + (data.unsupportedProperties ? ' props=' + data.unsupportedProperties.join(',') : '');
    failed.set(key, (failed.get(key) || 0) + 1);
  }
  // 渲染程序主執行緒上各種工作的總耗時（毫秒／2 秒；巢狀的事件會重複計入上層，只看相對大小）與樣式重算涉及的元素數
  const main = events.find((e) => e.ph === 'M' && e.name === 'thread_name' && e.args && e.args.name === 'CrRendererMain');
  const top = new Map();
  let styleElements = 0;
  for (const e of events) {
    if (!main || e.pid !== main.pid || e.tid !== main.tid || e.ph !== 'X' || !e.dur) continue;
    top.set(e.name, (top.get(e.name) || 0) + e.dur / 1000);
    if (e.name === 'UpdateLayoutTree' && e.args && e.args.elementCount) styleElements += e.args.elementCount;
  }
  const mainThread = [...top.entries()].sort((a, b) => b[1] - a[1]).slice(0, 16).map(([k, v]) => k + ' ' + Math.round(v));
  return { notComposited: Object.fromEntries(failed), mainThread, styleElements };
}

/**
 * MYRIAGENT_PERF_INVALIDATION=1（2026-10-25）：錄 2 秒帶失效追蹤的追蹤，找出樣式重算是誰觸發的。
 * frames：主執行緒的影格數（PrePaint）；recalcs：UpdateLayoutTree 次數、涉及的元素總數、其中被 JS 強制（帶堆疊）的次數與位置；
 * scheduled：哪段 JS（堆疊最上層的網頁程式）改了哪個元素的哪個 class／屬性／虛擬類別而排了失效（ScheduleStyleInvalidationTracking）；
 * recalcReasons：每個元素要重算樣式的原因（StyleRecalcInvalidationTracking，例如動畫、inline style、class 失效），依原因與元素統計
 */
async function traceInvalidations(cdp) {
  const events = [];
  cdp.on('Tracing.dataCollected', (e) => events.push(...e.value));
  const done = new Promise((resolve) => cdp.once('Tracing.tracingComplete', resolve));
  await cdp.send('Tracing.start', { traceConfig: { includedCategories: ['devtools.timeline', 'disabled-by-default-devtools.timeline', 'disabled-by-default-devtools.timeline.invalidationTracking', 'disabled-by-default-devtools.timeline.stack'] }, transferMode: 'ReportEvents' });
  await sleep(2000);
  await cdp.send('Tracing.end');
  await done;
  const mains = new Set(events.filter((e) => e.ph === 'M' && e.name === 'thread_name' && e.args && e.args.name === 'CrRendererMain').map((e) => e.pid + ':' + e.tid));
  const top = (stack) => {
    const f = (stack || []).find((s) => s && s.url && !s.url.startsWith('chrome') && !s.url.includes('playwright'));
    return f ? f.url.split('/').pop() + ':' + (f.lineNumber + 1) + ' ' + (f.functionName || '(anonymous)') : '(沒有 JS 堆疊)';
  };
  const node = (d) => (d.nodeName || '?').replace(/\s+/g, ' ').slice(0, 60);
  const bump = (map, key, n = 1) => map.set(key, (map.get(key) || 0) + n);
  let frames = 0, recalcs = 0, elements = 0;
  const forced = new Map(), scheduled = new Map(), reasons = new Map();
  for (const e of events) {
    if (!mains.has(e.pid + ':' + e.tid)) continue;
    const d = (e.args && (e.args.data || e.args.beginData)) || {};
    if (e.name === 'PrePaint' && e.ph === 'X') frames++;
    else if (e.name === 'UpdateLayoutTree' && (e.ph === 'X' || e.ph === 'B')) {
      recalcs++;
      elements += (e.args && e.args.elementCount) || 0;
      const stack = e.args && e.args.beginData && e.args.beginData.stackTrace;
      if (stack && stack.length) bump(forced, top(stack));
    } else if (e.name === 'ScheduleStyleInvalidationTracking') {
      const what = d.changedClass ? 'class ' + d.changedClass : d.changedAttribute ? 'attr ' + d.changedAttribute : d.changedPseudo ? 'pseudo ' + d.changedPseudo : d.changedId ? 'id ' + d.changedId : (d.reason || '?');
      bump(scheduled, what + ' @ ' + node(d) + ' ← ' + top(d.stackTrace));
    } else if (e.name === 'StyleRecalcInvalidationTracking') {
      bump(reasons, (d.reason || '?') + (d.extraData ? ' ' + String(d.extraData).slice(0, 40) : '') + ' @ ' + node(d) + ' ← ' + top(d.stackTrace));
    }
  }
  const list = (m, n) => [...m.entries()].sort((a, b) => b[1] - a[1]).slice(0, n).map(([k, v]) => v + ' × ' + k);
  return { frames, recalcs, elements, forcedRecalcs: list(forced, 12), scheduled: list(scheduled, 30), recalcReasons: list(reasons, 30) };
}

// ───── 六個窗格同時串流（2026-10-23 實機打字卡頓：六個對話同時執行時 WebView2 渲染程序的主執行緒約 72% 忙碌） ─────
const SIX_PANES = {
  panes: [
    { id: 'p1', tabs: ['c1', 'c2', 'c8'], active: 'c1' }, { id: 'p2', tabs: ['c3', 'c9'], active: 'c3' }, { id: 'p3', tabs: ['c4', 'c10'], active: 'c4' },
    { id: 'p4', tabs: ['c5', 'c11'], active: 'c5' }, { id: 'p5', tabs: ['c6', 'c12'], active: 'c6' }, { id: 'p6', tabs: ['c7', 'c14'], active: 'c7' },
  ],
  focusedPane: 'p1', sidebarCollapsed: false, view: 'chat',
};
const SIX_STREAMS = ['c1', 'c3', 'c4', 'c5', 'c6', 'c7'];

/**
 * 串流中的對話都帶 80 個已載入的項目（使用者、回覆、工具交錯，工具輸出約 3 KB；實機最後 80 項 120～470 KB）與 70 個指令
 * （實機一個帳戶的 commands 約 25 KB，每次 header 都帶）；標成執行中
 */
function multiStreamSetup(ids) {
  const pump = pumpSetup({ paired: true });
  return (host) => {
    pump(host);
    const commands = [];
    for (let i = 0; i < 70; i++) commands.push({ name: 'cmd-' + i, description: ('第 ' + i + ' 個指令的說明，').repeat(12), argumentHint: '[參數]' });
    const out = 'src\\Myriagent\\Hub\\AgentHub.Render.cs:173: TryPost(targets, () => new JObject { ["type"] = "item" });\n'.repeat(30);
    const now = Date.now();
    for (const s of host.conversations) {
      const c = host.conv(s.id);
      if (!c) continue;
      c.header.commands = commands;
      if (!ids.includes(s.id)) continue;
      const items = [];
      for (let i = 0; i < 80; i++) {
        const id = s.id + '-s' + i, time = now - (80 - i) * 30e3;
        if (i % 10 === 0) items.push({ id, seq: i, kind: 'User', status: 'Done', time, html: '<p>請幫我檢查第 ' + i + ' 段程式並修正。</p>' });
        else if (i % 4 === 0) items.push({ id, seq: i, kind: 'Assistant', status: 'Done', time, html: '<p>' + '已經確認了這一段的行為，接著修改。'.repeat(6) + '</p><pre><code class="language-csharp">var x = 1;\nConsole.WriteLine(x);</code></pre><ul><li>第一點</li><li>第二點</li></ul>' });
        else items.push({ id, seq: i, kind: 'Tool', status: 'Done', time, toolKind: i % 3 ? 'bash' : 'read', toolName: i % 3 ? '執行指令' : '讀取檔案', title: 'dotnet build Myriagent.slnx -c Release #' + i, detail: 'command: dotnet build', output: out });
      }
      c.items = items;
      c.hasMore = true;
      c.header.running = true;
      c.header.startedAt = now;
      s.running = true;
    }
  };
}

/**
 * 頁面內模擬宿主的 50 ms 合併：每個串流中的對話每 50 ms 一則回覆（段落越來越多、後半帶程式碼區塊），每 3 秒一個工具
 * （開始、結束各送一次 header 與對話清單）；帳戶卡每 2 秒、token 每 1 秒。各對話錯開
 */
async function startMultiStream(page, host, ids) {
  const headers = Object.fromEntries(ids.map((id) => [id, host.conv(id).header]));
  await page.evaluate(({ ids, headers, convs, accounts, tokens }) => {
    let tick = 0;
    const copy = (x) => JSON.parse(JSON.stringify(x));
    window.__perfStream = setInterval(() => {
      tick++;
      ids.forEach((id, k) => {
        const t = tick + k * 7, cyc = t % 60, n = Math.floor(t / 60);
        if (cyc < 40) {
          let html = '';
          for (let p = 0; p <= cyc / 4; p++) html += '<p>' + '這是串流中的回覆文字，'.repeat(8) + p + '</p>';
          if (cyc > 20) html += '<pre><code class="language-js">const a = ' + cyc + ';' + String.fromCharCode(10) + 'console.log(a);</code></pre>';
          window.__host({ type: 'item', conversationId: id, item: { id: id + '-live-a' + n, seq: 100000 + t, kind: 'Assistant', status: 'Running', time: Date.now(), html } });
        }
        if (cyc === 40 || cyc === 55) {
          const done = cyc === 55;
          window.__host({ type: 'item', conversationId: id, item: { id: id + '-live-t' + n, seq: 100000 + t, kind: 'Tool', status: done ? 'Done' : 'Running', time: Date.now(), toolKind: 'bash', toolName: '執行指令', title: 'dotnet test #' + n, detail: 'command: dotnet test', output: done ? 'ok\n'.repeat(400) : null } });
          window.__host({ type: 'header', conversationId: id, header: copy(Object.assign({}, headers[id], { status: done ? null : '執行指令：dotnet test #' + n })) });
          window.__host({ type: 'conversations', items: copy(convs) });
        }
      });
      if (tick % 40 === 0) window.__host({ type: 'accounts', accounts: copy(accounts) });
      if (tick % 20 === 0 && tokens) window.__host({ type: 'tokens', tokens });
    }, 50);
  }, { ids, headers, convs: host.conversations, accounts: host.accounts, tokens: host.tokens || null });
}

// ───── 很多對話在背景分頁執行（2026-10-23 實機：11 個對話同時執行、大多在背景分頁，渲染程序一直約一顆核心） ─────
const BG_PANES = {
  panes: [
    { id: 'p1', tabs: ['c1', 'c2', 'c8'], active: 'c1' }, { id: 'p2', tabs: ['c3', 'c9'], active: 'c3' },
    { id: 'p3', tabs: ['c4', 'c10', 'c11'], active: 'c4' }, { id: 'p4', tabs: ['c7', 'c12', 'c14'], active: 'c7' },
  ],
  focusedPane: 'p1', sidebarCollapsed: false, view: 'chat',
};
const BG_SHOWN = ['c1', 'c3'];
const BG_HIDDEN = ['c2', 'c8', 'c9', 'c10', 'c11', 'c12', 'c14'];

/**
 * 頁面內模擬宿主：看得見的 c1、c3 每 50 ms 一則串流中的回覆；背景分頁的 7 個對話照宿主的節流每 500 ms 一則，
 * 標頭每 headerMs 一次（狀態文字與上下文用量在變）；帳戶卡每 500 ms、token 每 200 ms（數字在長）。各對話錯開
 */
async function startBgStream(page, host, headerMs, opts = {}) {
  const shownIds = opts.shown || BG_SHOWN, hiddenIds = opts.hidden || BG_HIDDEN;
  const ids = [...shownIds, ...hiddenIds];
  const headers = Object.fromEntries(ids.map((id) => [id, host.conv(id).header]));
  await page.evaluate(({ shown, hidden, headers, accounts, tokens, headerMs, noHidden, noShown, noAccounts, grow }) => {
    let tick = 0;
    const copy = (x) => JSON.parse(JSON.stringify(x));
    const reply = (id, t) => {
      const cyc = t % 60;
      let html = '';
      // grow：最後一段每一拍都多幾個字（真的串流是這樣：最後一段一直在長、每次更新都被換掉），不加程式碼框
      for (let p = 0; p <= cyc / 4; p++) html += '<p>' + '這是串流中的回覆文字，'.repeat(8) + p + (grow && p === Math.floor(cyc / 4) ? '再多幾個字，'.repeat(cyc % 4) : '') + '</p>';
      if (cyc > 20 && !grow) html += '<pre><code class="language-js">const a = ' + cyc + ';' + String.fromCharCode(10) + 'console.log(a);</code></pre>';
      window.__host({ type: 'item', conversationId: id, item: { id: id + '-live-a' + Math.floor(t / 60), seq: 100000 + t, kind: 'Assistant', status: 'Running', time: Date.now(), html } });
    };
    window.__perfStream = setInterval(() => {
      tick++;
      if (!noShown) shown.forEach((id, k) => reply(id, tick + k * 7));
      if (!noHidden) hidden.forEach((id, k) => {
        const t = tick + k * 3;
        if (t % 10 === 0) reply(id, t);
        if ((t * 50) % headerMs < 50) {
          const used = 20000 + ((t * 37) % 90000);
          window.__host({ type: 'header', conversationId: id, header: Object.assign(copy(headers[id]), { running: true, status: t % 20 < 10 ? '撰寫回覆…' : '執行指令：dotnet test #' + t, context: { used, window: 200000, percent: Math.round(used / 2000), autoCompactAt: 160000, severity: 'ok' } }) });
        }
      });
      if (!noAccounts && tick % 10 === 0) window.__host({ type: 'accounts', accounts: copy(accounts) });
      if (tokens && tick % 4 === 0) {
        for (const a of Object.values(tokens.accounts || {})) if (a && a.today) a.today.input += 1234;
        window.__host({ type: 'tokens', tokens: copy(tokens) });
      }
    }, 50);
  }, { shown: shownIds, hidden: hiddenIds, headers, accounts: host.accounts, tokens: opts.noTokens ? null : host.tokens || null, headerMs,
    noHidden: !!opts.noHidden, noShown: !!opts.noShown, noAccounts: !!opts.noAccounts, grow: !!opts.grow });
}

// ───── 窗格與分頁很多的版面（2026-11-08）：9 個窗格、分頁全部放左側、101 個分頁、7 個執行中（3 個看得見在串流、4 個在背景分頁），電腦狀態面板開著 ─────
const MANY_COUNTS = [4, 34, 10, 7, 7, 13, 7, 9, 10];
const MANY_IDS = Array.from({ length: MANY_COUNTS.reduce((a, b) => a + b, 0) }, (_, i) => 'c' + (i + 1));
const MANY_PANES = (() => {
  const panes = [];
  let at = 0;
  MANY_COUNTS.forEach((n, k) => {
    const tabs = MANY_IDS.slice(at, at + n);
    at += n;
    // 第一格的作用中分頁是 c1（量測期間在它的輸入框打字）；其他格是第二個分頁（第一個留給背景執行的）
    panes.push({ id: 'p' + (k + 1), tabs, active: k === 0 ? tabs[0] : tabs[Math.min(1, n - 1)], tabsSide: 'left' });
  });
  return { panes, focusedPane: 'p1', sidebarCollapsed: false, view: 'chat', monitor: 'open' };
})();
const MANY_SHOWN = MANY_PANES.panes.slice(0, 3).map((p) => p.active);
const MANY_HIDDEN = MANY_PANES.panes.slice(3, 7).map((p) => p.tabs.find((t) => t !== p.active));
if (MANY_SHOWN[0] !== 'c1') throw new Error('MANY_PANES：第一格的作用中分頁要是 c1');

/** 補齊 101 個對話的摘要（c15 起；帳戶輪流用 a1～a3），再照 multiStreamSetup 把執行中的標好 */
function manySetup(ids) {
  const base = multiStreamSetup(ids);
  return (host) => {
    const proto = Object.fromEntries(host.conversations.map((s) => [s.accountId, s]));
    for (let i = host.conversations.length + 1; i <= MANY_IDS.length; i++) {
      const id = 'c' + i;
      if (host.conversations.some((s) => s.id === id)) continue;
      const src = proto[['a1', 'a2', 'a3'][i % 3]];
      const updatedAt = Date.now() - i * 3 * 3600e3;
      host.conversations.push(Object.assign({}, src, { id, title: '第 ' + i + ' 個對話：' + ['修正同步的邊界情況', '整理部署腳本', '補上單元測試', '量測記憶體用量'][i % 4],
        updatedAt, group: groupOf(Date.now(), updatedAt), running: false, waiting: false, unread: i % 9 === 0 }));
    }
    // 宿主的清單依更新時間由新到舊（分組才會連在一起）
    host.conversations.sort((a, b) => b.updatedAt - a.updatedAt);
    base(host);
  };
}

/**
 * 量測期間在 c1 的輸入框每 110 ms 按一個鍵：keydown 事件的時間戳（瀏覽器收到輸入）到 input 處理器真正執行的等待（毫秒），
 * 主執行緒忙的時候按鍵要排隊，這就是打字卡頓的延遲
 */
async function typeKeys(page, ms) {
  await page.evaluate(() => {
    window.__keyLag = [];
    document.addEventListener('keydown', (e) => { window.__lastKd = e.timeStamp; }, true);
    document.addEventListener('input', () => { if (window.__lastKd) { window.__keyLag.push(performance.now() - window.__lastKd); window.__lastKd = 0; } }, true);
  });
  await page.focus('.conv[data-conv="c1"] .cinput');
  const end = Date.now() + ms;
  while (Date.now() < end) { await page.keyboard.press('a'); await sleep(110); }
  const lag = (await page.evaluate(() => window.__keyLag)).sort((a, b) => a - b);
  const at = (p) => (lag.length ? Math.round(lag[Math.min(lag.length - 1, Math.floor(lag.length * p))]) : null);
  return { n: lag.length, p50: at(0.5), p90: at(0.9), max: at(1) };
}

/**
 * 串流中拖曳分頁（2026-10-25 實機：15 個對話同時執行時，拖分頁換位置、搬到別的窗格或視窗都一頓一頓、放開後要等）：
 * 以 CDP 攔截拖曳（不經作業系統的拖放）依序做三次——p1 裡換位置、p1 搬到 p2 的分頁列、p2 搬到 p3 的窗格裡，
 * 每次 dragEnter 之後每 16 ms 一個 dragOver、共 40 個。記開始拖（按下、移動到收到 dragIntercepted）、每個 dragOver 與 drop
 * 送出到渲染程序處理完的毫秒數（Input.dispatchDragEvent 等渲染程序回覆）。每次拖曳期間另外每 50 ms 送 c1 一則一直在長的回覆
 * （c1-dragfeed；串流本身每 3 秒有 1 秒在跑工具、回覆不長，拿它來比會看拖曳落在哪一段），放開前停送：
 * held＝拖曳期間畫面上的段數沒有跟著長（更新先排著），applied＝放開後一個影格內畫面上的段數等於最後送出的
 */
async function tabDragLatency(page) {
  const cdp = await page.context().newCDPSession(page);
  const center = (sel) => page.evaluate((s) => { const el = document.querySelector(s); if (!el) return null; const r = el.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; }, sel);
  const paras = () => page.evaluate(() => { const it = document.querySelector('.conv[data-conv="c1"] .it[data-id="c1-dragfeed"]'); return it ? it.querySelectorAll('p').length : 0; });
  const feed = (on) => page.evaluate((on) => {
    clearInterval(window.__dragFeed);
    if (!on) return window.__dragFeedN || 0;
    window.__dragFeed = setInterval(() => {
      const n = (window.__dragFeedN = (window.__dragFeedN || 0) + 1);
      window.__host({ type: 'item', conversationId: 'c1', item: { id: 'c1-dragfeed', seq: 900000, kind: 'Assistant', status: 'Running', time: Date.now(), html: '<p>拖曳期間送的回覆。</p>'.repeat(n) } });
    }, 50);
    return window.__dragFeedN || 0;
  }, on);
  const plan = [
    { name: 'reorder', from: '.pane[data-pane="p1"] .tab[data-key="c2"]', to: '.pane[data-pane="p1"] .tab[data-key="c8"]', dx: 30 },
    { name: 'toStrip', from: '.pane[data-pane="p1"] .tab[data-key="c8"]', to: '.pane[data-pane="p2"] .tab[data-key="c9"]', dx: 30 },
    { name: 'toPane', from: '.pane[data-pane="p2"] .tab[data-key="c9"]', to: '.pane[data-pane="p3"] .pane-body', dx: 0 },
  ];
  await cdp.send('Input.setInterceptDrags', { enabled: true });
  const out = [];
  let held = true, applied = true;
  for (const step of plan) {
    const a = await center(step.from), b0 = await center(step.to);
    if (!a || !b0) { out.push({ name: step.name, missing: true }); continue; }
    const b = { x: b0.x + step.dx, y: b0.y };
    await feed(true);
    let data = null;
    const got = new Promise((r) => cdp.once('Input.dragIntercepted', (e) => { data = e.data; r(); }));
    const t0 = performance.now();
    await cdp.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: a.x, y: a.y, button: 'left', clickCount: 1 });
    for (let i = 1; i <= 3; i++) await cdp.send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: a.x + i * 5, y: a.y, button: 'left', buttons: 1 });
    await Promise.race([got, sleep(5000)]);
    const start = performance.now() - t0;
    if (!data) { await feed(false); out.push({ name: step.name, start, intercepted: false }); continue; }
    const overs = [];
    let mid = null;
    for (let i = 0; i <= 40; i++) {
      const x = a.x + (b.x - a.x) * (i / 40), y = a.y + (b.y - a.y) * (i / 40);
      const s = performance.now();
      await cdp.send('Input.dispatchDragEvent', { type: i ? 'dragOver' : 'dragEnter', x, y, data });
      overs.push(performance.now() - s);
      if (i === 5) mid = await paras();
      await sleep(16);
    }
    const late = await paras();
    const sent = await feed(false);
    const d0 = performance.now();
    await cdp.send('Input.dispatchDragEvent', { type: 'drop', x: b.x, y: b.y, data });
    const drop = performance.now() - d0;
    await cdp.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: b.x, y: b.y, button: 'left', clickCount: 1 });
    await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
    const after = await paras();
    // 拖曳中途到快放開時沒變、而且這段期間送過＝排著；放開後等於最後送出的＝補上了
    held = held && mid === late && sent > late;
    applied = applied && after === sent;
    overs.sort((x, y) => x - y);
    const at = (p) => Math.round(overs[Math.min(overs.length - 1, Math.floor(overs.length * p))]);
    out.push({ name: step.name, startMs: Math.round(start), overP50: at(0.5), overP90: at(0.9), overMax: at(1), dropMs: Math.round(drop), paras: { mid, late, sent, after } });
    await sleep(600);
  }
  await cdp.send('Input.setInterceptDrags', { enabled: false });
  await cdp.detach().catch(() => {});
  return { steps: out, held, applied };
}

// ───── 計算樣式快照：改法不得改變呈現結果時的前後比對（MYRIAGENT_PERF_STYLES=<輸出檔>；比對用 style-diff.mjs） ─────
/** 用到的版面幾何（used value）會隨經過時間的文字長短變動，不比；其餘計算樣式全部比 */
const GEOMETRY = ['width', 'height', 'inline-size', 'block-size', 'top', 'left', 'right', 'bottom', 'inset-block-start', 'inset-block-end', 'inset-inline-start', 'inset-inline-end',
  'transform-origin', 'perspective-origin', 'margin-left', 'margin-right', 'margin-top', 'margin-bottom', 'margin-inline-start', 'margin-inline-end', 'margin-block-start', 'margin-block-end',
  'x', 'y', 'cx', 'cy', 'r', 'rx', 'ry', 'd', 'stroke-dasharray', 'stroke-dashoffset'];

/**
 * 頁面內：所有動畫歸零暫停後，每個元素（含有內容的 ::before、::after）的計算樣式（屬性依名稱排序；自訂屬性列舉的順序每次載入不同）
 * 取 53 位元雜湊；動畫與轉場另列（路徑、偽元素、名稱、狀態）；最後恢復播放
 */
/** MYRIAGENT_PERF_STYLES_DUMP=<路徑的一段>：快照另外存這些元素完整的計算樣式（style-diff 說不同時，看是哪個屬性） */
const STYLE_DUMP = process.env.MYRIAGENT_PERF_STYLES_DUMP || null;

function styleSnapshot(arg) {
  const skip = Array.isArray(arg) ? arg : arg.skip;
  const dumpNeedle = Array.isArray(arg) ? null : arg.dump;
  const skipSet = new Set(skip);
  const dump = {};
  const hash = (str) => {
    let h1 = 0xdeadbeef, h2 = 0x41c6ce57;
    for (let i = 0; i < str.length; i++) { const ch = str.charCodeAt(i); h1 = Math.imul(h1 ^ ch, 2654435761); h2 = Math.imul(h2 ^ ch, 1597334677); }
    h1 = Math.imul(h1 ^ (h1 >>> 16), 2246822507) ^ Math.imul(h2 ^ (h2 >>> 13), 3266489909);
    h2 = Math.imul(h2 ^ (h2 >>> 16), 2246822507) ^ Math.imul(h1 ^ (h1 >>> 13), 3266489909);
    return (4294967296 * (2097151 & h2) + (h1 >>> 0)).toString(36);
  };
  const pathOf = (el) => {
    const parts = [];
    for (let e = el; e && e.nodeType === 1; e = e.parentElement) parts.push(e.tagName.toLowerCase() + (e.parentElement ? '[' + Array.prototype.indexOf.call(e.parentElement.children, e) + ']' : ''));
    return parts.reverse().join('/');
  };
  const anims = [];
  for (const a of document.getAnimations()) {
    const t = a.effect && a.effect.target;
    if (!t) continue;
    anims.push(pathOf(t) + (a.effect.pseudoElement || '') + ' ' + (a instanceof CSSAnimation ? 'anim:' + a.animationName : a instanceof CSSTransition ? 'trans:' + a.transitionProperty : 'other') + ' ' + a.playState);
  }
  for (const a of document.getAnimations()) { try { a.pause(); a.currentTime = 0; } catch { /* 忽略 */ } }
  // dumpNeedle（MYRIAGENT_PERF_STYLES_DUMP）：路徑含這段字的元素另外存完整的計算樣式（兩版不同時看是哪個屬性）
  const digest = (cs, path) => {
    const o = [];
    for (let i = 0; i < cs.length; i++) { const k = cs[i]; if (!skipSet.has(k)) o.push(k + ':' + cs.getPropertyValue(k)); }
    const text = o.sort().join(';');
    if (dumpNeedle && path.includes(dumpNeedle)) dump[path] = text;
    return hash(text);
  };
  const styles = {};
  for (const el of document.querySelectorAll('*')) {
    const p = pathOf(el);
    styles[p] = digest(getComputedStyle(el), p);
    for (const pe of ['::before', '::after']) {
      const cs = getComputedStyle(el, pe);
      if (cs.content !== 'none' && cs.content !== 'normal') styles[p + pe] = digest(cs, p + pe);
    }
  }
  for (const a of document.getAnimations()) { try { a.play(); } catch { /* 忽略 */ } }
  return dumpNeedle ? { anims: anims.sort(), styles, dump } : { anims: anims.sort(), styles };
}

/** 各種狀態的計算樣式快照：帳戶執行中、全部停、只有一個、收起側欄、儀表板、彈出視窗、關掉動態效果、減少動態、淺色主題 */
async function runStyles(ctx, file) {
  const { openApp, closeApp, NOW } = ctx;
  const vp = { w: 1600, h: 1000 };
  const remote = { mode: 'desktop', deviceId: SELF, devices: devices(NOW), sync: syncStatus(NOW, 'online') };
  const layout = Object.assign({}, FOUR_PANES, { monitor: 'open' });
  const result = {};
  const take = async (name, page) => { await sleep(700); result[name] = await page.evaluate(styleSnapshot, STYLE_DUMP ? { skip: GEOMETRY, dump: STYLE_DUMP } : GEOMETRY); };
  const setRunning = (page, host, fn) => {
    for (const a of host.accounts) a.sessions = Object.assign({}, a.sessions, fn(a));
    return page.evaluate((acc) => window.__host({ type: 'accounts', accounts: acc }), JSON.parse(JSON.stringify(host.accounts)));
  };
  const app = await openApp('styles-main', { vp, layout, remote, hostSetup: pumpSetup({ paired: true }) });
  const { page, host } = app;
  await sleep(1500);
  await take('start-running', page);
  await setRunning(page, host, () => ({ running: 0 }));
  await take('all-stopped', page);
  await setRunning(page, host, (a) => ({ running: a.id === 'a3' ? 2 : 0 }));
  await take('one-running', page);
  await page.click('#btnSidebar');
  await take('sidebar-collapsed-running', page);
  await setRunning(page, host, () => ({ running: 0 }));
  await take('sidebar-collapsed-stopped', page);
  await page.click('#btnSidebar');
  await setRunning(page, host, (a) => ({ running: a.id === 'a1' ? 1 : 0 }));
  await page.click('#viewSwitch [data-view="dashboard"]');
  await take('dashboard-running', page);
  await setRunning(page, host, () => ({ running: 0 }));
  await take('dashboard-stopped', page);
  await page.click('#viewSwitch [data-view="chat"]');
  await setRunning(page, host, () => ({ running: 3 }));
  await take('back-chat-all-running', page);
  await closeApp(app);
  for (const [name, o] of [
    ['popout', { vp: { w: 1000, h: 900 }, mode: 'popout', popoutId: 'c3', url: 'https://myriagent.local/index.html?mode=popout&conversation=c3' }],
    ['fx-off', { settings: { visualEffects: false } }],
    ['reduced-motion', { reducedMotion: 'reduce' }],
    ['light', { settings: { theme: 'light' }, colorScheme: 'light' }],
  ]) {
    const one = await openApp('styles-' + name, Object.assign({ vp, layout, remote, hostSetup: pumpSetup({ paired: true }) }, o));
    await sleep(1500);
    await take(name, one.page);
    await closeApp(one);
  }
  fs.writeFileSync(file, JSON.stringify(result));
  return Object.fromEntries(Object.entries(result).map(([k, v]) => [k, Object.keys(v.styles).length]));
}

/** c1 的一則串流中的回覆：每 50 ms 長一段（和宿主的合併間隔相同） */
async function startStream(page) {
  await page.evaluate(() => {
    let i = 0;
    window.__perfStream = setInterval(() => {
      i++;
      // 比較像真的回覆：每 4 次多一個完成的段落（每 10 段一個程式碼區塊），最後一段一直在長
      // 中文與英數緊鄰（沒有空白）：回覆的中英間隙（text-autospace，chat.css）在串流時每次重排都要算，量得到它的成本
      const blocks = [];
      for (let k = 0; k < Math.floor(i / 4); k++) blocks.push(k % 10 === 9 ? '<pre><code>const x' + k + ' = await load(' + k + ');' + String.fromCharCode(10) + 'console.log(x' + k + ');</code></pre>' : '<p>第 ' + k + ' 段：這一段已經寫完，內容是串流中的回覆文字，中英混排像真的回覆（用WebView2跑了3次dotnet build），帶一點 <code>code</code> 與 <strong>粗體</strong>。</p>');
      const html = blocks.join('') + '<p>' + '正在寫的這一段跑了3次build，'.repeat(1 + (i % 4) * 3) + '</p>';
      window.__host({ type: 'item', conversationId: 'c1', item: { id: 'perf-s1', seq: 9000, kind: 'Assistant', status: 'Running', time: Date.now(), html } });
    }, 50);
  });
}

/**
 * 視窗沒有焦點時（theme.js 的 data-window="inactive"）還可以跑的無限循環動畫：看得出狀態的（[宿主元素的選擇器, 偽元素]）。
 * 其他的都是裝飾，fx.css 的 inactive 區段要停下；新增裝飾性的動畫卻沒加進那裡，perfInactiveOnlyStatus 會失敗
 */
const INACTIVE_STATUS = [
  ['.spin', ''], ['.think-glyph', ''], ['.btn.spinning .ic', ''],
  ['.step-dot', ''], ['.step-dot', '::after'], ['.crow-dot', '::after'],
  ['.it-assistant .md *', '::after'],
  ['.run-text', ''], ['.think-label', ''], ['.todo-cur', ''],
  ['.it-card.waiting', '::after'], ['.bg-ring', ''], ['.cbox', '::after'],
  ['.macct-dot', ''], ['.crow-titling', ''], ['.cc-step.cur', ''], ['.sync-dot-ic', ''],
  ['.ch-title-run', ''], ['.macct-name-run', ''], ['.lp-run', ''], ['.cbtn.mic.listening', '::after'],
  ['.sum-item.run', '::after'], ['.acard', '::after'],
  ['.mon-dev[data-state="waiting"] .mon-led', ''], ['.hub-status', '::after'],
  ['.older.loading', '::after'],
  ['.tab.attn-flash .tab-title', '::before'], ['.crow.attn-flash', '::before'], ['.ch.attn-flash', '::before'], // 要你回來看的對話（attnflash.js）
  ['.mcp-leds > i[data-st="pending"]', ''], ['.imp-radar > i', ''],
];

/**
 * 視窗沒有焦點時照樣動、但要降低更新頻率的（2026-10-25 使用者要求：帳戶列表照樣動，同電腦狀態面板的原則）：帳戶卡（側欄的小卡、儀表板的大卡）。
 * 每一段關鍵影格都要是 steps，每秒的格數（各段的格數加總 ÷ 一圈的秒數）不超過 INACTIVE_MAX_STEPS_PER_SEC（有焦點時是每秒 60 格）
 */
const INACTIVE_STEPPED = [
  ['.macct-bar', '::after'], ['.macct-run', '::after'], ['.mmeter', '::before'], ['.mmeter', '::after'], ['.mtok-n', '::after'],
  ['.acard', '::before'], ['.acard-mark', '::before'], ['.ring-sweep', ''], ['.ring-wrap', '::before'], ['.ring-wrap', '::after'], ['.spark-end', '::after'],
  ['.sch-row > .led', ''], ['.sch-row', '::after'], // 第九批：排程頁執行中的那幾列（狀態指示）
];
const INACTIVE_MAX_STEPS_PER_SEC = 12;

/**
 * 頁面內：還在跑、不在 status 清單裡的無限循環 CSS 動畫（名稱＋元素類別＋偽元素）；stepped 清單裡的要一格一格、每秒不超過 maxRate 格，
 * 不是就也列出來（後面帶「每秒 N 格」或「平滑」）。rates 是 stepped 的每一個實際每秒幾格
 */
function nonStatusLoops({ status, stepped, maxRate }) {
  const out = [];
  const rates = [];
  for (const a of document.getAnimations()) {
    if (!(a instanceof CSSAnimation) || a.playState !== 'running' || !a.effect || !a.effect.target) continue;
    const timing = a.effect.getComputedTiming();
    if (timing.iterations !== Infinity) continue;
    const t = a.effect.target, pseudo = a.effect.pseudoElement || '';
    if (status.some(([sel, p]) => p === pseudo && t.matches(sel))) continue;
    const cls = typeof t.className === 'string' ? t.className : (t.className && t.className.baseVal) || t.tagName;
    const label = a.animationName + ' ' + String(cls).trim().split(/\s+/).slice(0, 2).join('.') + pseudo;
    if (stepped.some(([sel, p]) => p === pseudo && t.matches(sel))) {
      const kf = a.effect.getKeyframes();
      let frames = 0, smooth = false;
      for (let i = 0; i < Math.max(1, kf.length - 1); i++) {
        const m = /^steps\((\d+)/.exec((kf[i] && kf[i].easing) || '');
        if (m) frames += Number(m[1]); else smooth = true;
      }
      const rate = Math.round((frames / (timing.duration / 1000 / Math.abs(a.playbackRate || 1))) * 10) / 10;
      rates.push(label + '：' + (smooth ? '平滑' : '每秒 ' + rate + ' 格'));
      if (!smooth && rate <= maxRate) continue;
      out.push(label + (smooth ? '（平滑）' : '（每秒 ' + rate + ' 格）'));
      continue;
    }
    out.push(label);
  }
  return { list: [...new Set(out)], rates: [...new Set(rates)] };
}

/**
 * 持續的轉場：ms 期間每 every 毫秒看一次正在跑的 CSSTransition，動的不是合成屬性、而且出現在 15% 以上取樣裡的（屬性＋元素類別＋出現比例）。
 * 每秒一次、每次 200 ms 的轉場約佔 20%；偶發的一次（點按、狀態改變）不到 15%
 */
async function steadyTransitions(page, ms, every) {
  const n = Math.round(ms / every);
  const seen = new Map();
  for (let i = 0; i < n; i++) {
    const keys = await page.evaluate((ok) => {
      const composited = new Set(ok);
      const out = new Set();
      for (const a of document.getAnimations()) {
        const t = a.effect && a.effect.target;
        // SVG 元素的 transform 轉場同樣不能交給合成器（2026-10-24：電腦狀態面板量表外圈每秒 1 秒的轉場，主執行緒每秒約 11 幀）
        if (!(a instanceof CSSTransition) || a.playState !== 'running' || (composited.has(a.transitionProperty) && !(t instanceof SVGElement))) continue;
        const cls = t ? (typeof t.className === 'string' ? t.className : (t.className && t.className.baseVal) || t.tagName) : '?';
        out.add(a.transitionProperty + ' ' + String(cls).trim().split(/\s+/)[0]);
      }
      return [...out];
    }, [...COMPOSITED]);
    for (const k of keys) seen.set(k, (seen.get(k) || 0) + 1);
    await sleep(every);
  }
  return [...seen.entries()].filter(([, c]) => c / n >= 0.15).map(([k, c]) => k + ' ' + Math.round((c / n) * 100) + '%');
}

export async function runPerf(ctx) {
  const { openApp, closeApp, check, NOW } = ctx;
  const vp = { w: 1920, h: 1080 };
  const remote = { mode: 'desktop', deviceId: SELF, devices: devices(NOW), sync: syncStatus(NOW, 'online') };
  const results = {};
  const scenario = async (name, o, run) => {
    const app = await openApp('perf-' + name, Object.assign({ vp, layout: FOUR_PANES, remote, hostSetup: pumpSetup({ paired: true }) }, o));
    if (o.css) await app.page.addStyleTag({ content: o.css });
    await sleep(2500); // 圓環長出、開場掃描、面板補完歷史
    // o.blur：視窗沒有焦點（切到別的程式）：theme.js 收到 blur 標 data-window="inactive"，裝飾性的持續動畫停下
    if (o.blur) { await app.page.evaluate(() => window.dispatchEvent(new Event('blur'))); await sleep(300); }
    if (run) await run(app.page, app.host);
    if (o.settle) await sleep(o.settle);
    // o.keys：量測期間同時在輸入框打字，記按鍵的等待
    const [m, keys] = await Promise.all([measure(app.page, 6000), o.keys ? typeKeys(app.page, 6000) : null]);
    if (keys) m.keys = keys;
    const loops = await app.page.evaluate(loopsOf, [...COMPOSITED]);
    // 量完的那一刻正在跑的轉場（看主執行緒的耗用除了無限循環的動畫還有誰）
    const transitions = await app.page.evaluate(() => document.getAnimations().filter((a) => a instanceof CSSTransition && a.playState === 'running')
      .map((a) => a.transitionProperty + ' ' + ((typeof a.effect.target.className === 'string' ? a.effect.target.className : (a.effect.target.className && a.effect.target.className.baseVal) || a.effect.target.tagName) || '').trim().split(/\s+/)[0]));
    const mainThread = loops.filter((l) => !l.composited);
    // 量完之後另外取樣（不擾動上面的耗用數字）：一直在重新開始的轉場（例如每秒更新一次、每次都轉場）
    const steady = await steadyTransitions(app.page, 6000, 200);
    // o.events：另外錄 2 秒追蹤，看頁面有沒有在派送動畫事件、主執行緒每秒幾幀
    const events = o.events ? await animationEvents(app.page, 2000) : null;
    const vframes = o.events ? await app.page.evaluate(() => document.querySelectorAll('iframe.vframe').length) : null;
    // 捲在畫面外（被捲動容器或視窗裁掉）卻還在跑的無限循環動畫（名稱 目標 → 數量）
    const offscreen = await app.page.evaluate(() => {
      const out = {};
      const vw = innerWidth, vh = innerHeight;
      const clipped = (el) => {
        let r = el.getBoundingClientRect();
        if (r.width === 0 && r.height === 0) return 'zero';
        if (r.bottom <= 0 || r.top >= vh || r.right <= 0 || r.left >= vw) return 'viewport';
        for (let a = el.parentElement; a; a = a.parentElement) {
          const cs = getComputedStyle(a);
          if (cs.overflowX === 'visible' && cs.overflowY === 'visible') continue;
          const b = a.getBoundingClientRect();
          if (r.bottom <= b.top || r.top >= b.bottom || r.right <= b.left || r.left >= b.right) return 'scroll:' + (a.className || a.tagName).toString().split(' ')[0];
        }
        return null;
      };
      for (const an of document.getAnimations()) {
        if (!(an instanceof CSSAnimation) || an.playState !== 'running' || !an.effect || !an.effect.target) continue;
        if (an.effect.getComputedTiming().iterations !== Infinity) continue;
        const t = an.effect.target;
        const why = clipped(t);
        if (!why) continue;
        const cls = typeof t.className === 'string' ? t.className : (t.className && t.className.baseVal) || t.tagName;
        const k = an.animationName + ' ' + String(cls).trim().split(/\s+/)[0] + (an.effect.pseudoElement || '') + ' [' + why + ']';
        out[k] = (out[k] || 0) + 1;
      }
      return out;
    });
    const tally = {};
    for (const l of loops) tally[l.name + ' ' + l.target] = (tally[l.name + ' ' + l.target] || 0) + 1;
    results[name] = Object.assign(m, { loops: loops.length, loopNames: [...new Set(loops.map((l) => l.name))].sort(), loopTally: tally, offscreenLoops: offscreen, transitions, mainThreadLoops: mainThread.length,
      mainThreadLoopList: [...new Set(mainThread.map((l) => l.name + ' ' + l.target + ' [' + l.props.join(',') + ']'))], steadyTransitions: steady },
      events ? Object.assign(events, { vframes }) : {});
    if (o.blur) {
      // 沒焦點時還在跑的裝飾性動畫；回到這個視窗（focus）之後裝飾的動畫要恢復
      const ns = await app.page.evaluate(nonStatusLoops, { status: INACTIVE_STATUS, stepped: INACTIVE_STEPPED, maxRate: INACTIVE_MAX_STEPS_PER_SEC });
      results[name].nonStatus = ns.list;
      results[name].steppedRates = ns.rates;
      results[name].inactiveAttr = await app.page.evaluate(() => document.documentElement.dataset.window || null);
      await app.page.evaluate(() => window.dispatchEvent(new Event('focus')));
      await sleep(300);
      results[name].refocusLoops = (await app.page.evaluate(loopsOf, [...COMPOSITED])).length;
      results[name].refocusAttr = await app.page.evaluate(() => document.documentElement.dataset.window || null);
    }
    await closeApp(app);
    return results[name];
  };
  // 六個窗格同時串流時拖曳分頁三次（換位置、搬到別的分頁列、搬到別的窗格裡）：開始拖、每個 dragOver、放開各等渲染程序多久
  const dragScenario = async () => {
    const app = await openApp('perf-drag', { vp, layout: SIX_PANES, remote, hostSetup: multiStreamSetup(SIX_STREAMS) });
    await sleep(2500);
    await startMultiStream(app.page, app.host, SIX_STREAMS);
    await sleep(2000);
    const drag = await tabDragLatency(app.page);
    await closeApp(app);
    return drag;
  };
  // 計算樣式快照：MYRIAGENT_PERF_STYLES=<輸出檔> 時只拍快照（各狀態的元素數記在報告），不量耗用；兩版的比對用 style-diff.mjs
  if (process.env.MYRIAGENT_PERF_STYLES) {
    check('perfStyles', true, await runStyles(ctx, process.env.MYRIAGENT_PERF_STYLES));
    return;
  }
  // 只跑六窗格同時串流（前後比較時重複跑、取中位數用）
  if (process.env.MYRIAGENT_PERF_STREAM6) {
    await scenario('stream6', { layout: SIX_PANES, hostSetup: multiStreamSetup(SIX_STREAMS), keys: true, settle: 2000 }, (page, host) => startMultiStream(page, host, SIX_STREAMS));
    check('perfStream6', true, results.stream6);
    const v = results.stream6;
    console.log(JSON.stringify({ busy: v.busyMsPerSec, script: v.scriptMsPerSec, style: v.styleMsPerSec, layout: v.layoutMsPerSec, styleRecalcs: v.styleRecalcsPerSec, cpu: v.cpuMsPerSec || null, keys: v.keys }));
    if (v.invalidations) console.log(JSON.stringify(v.invalidations, null, 1));
    if (v.jsProfile) console.log(JSON.stringify(v.jsProfile, null, 1));
    if (v.notComposited) console.log(JSON.stringify(v.notComposited, null, 1));
    return;
  }
  // 只跑串流中拖曳分頁（MYRIAGENT_PERF_DRAG=1；前後比較時重複交錯跑）
  if (process.env.MYRIAGENT_PERF_DRAG) {
    const drag = await dragScenario();
    check('perfTabDrag', true, drag);
    console.log(JSON.stringify(drag));
    return;
  }
  // 只跑很多對話在背景分頁執行（MYRIAGENT_PERF_BG_HEADER_MS：背景分頁的標頭每幾毫秒一次，預設 250）
  if (process.env.MYRIAGENT_PERF_BG) {
    const headerMs = Number(process.env.MYRIAGENT_PERF_BG_HEADER_MS) || 250;
    // A/B 用的開關（逗號分隔）：noHidden 背景分頁不更新、noShown 看得見的不串流、noTokens 不送 token、noAccounts 不送帳戶卡
    const opts = Object.fromEntries(String(process.env.MYRIAGENT_PERF_BG_OFF || '').split(',').filter(Boolean).map((k) => [k, true]));
    await scenario('bg', { layout: BG_PANES, hostSetup: multiStreamSetup([...BG_SHOWN, ...BG_HIDDEN]), keys: true, settle: 2000 }, (page, host) => startBgStream(page, host, headerMs, opts));
    check('perfBg', true, results.bg);
    const v = results.bg;
    console.log(JSON.stringify({ headerMs, off: Object.keys(opts).join(',') || null, busy: v.busyMsPerSec, script: v.scriptMsPerSec, style: v.styleMsPerSec, layout: v.layoutMsPerSec, styleRecalcs: v.styleRecalcsPerSec, cpu: v.cpuMsPerSec || null, keys: v.keys }));
    if (v.invalidations) console.log(JSON.stringify(v.invalidations, null, 1));
    if (v.jsProfile) console.log(JSON.stringify(v.jsProfile, null, 1));
    if (v.notComposited) console.log(JSON.stringify(v.notComposited, null, 1));
    return;
  }
  // 版面配置的微基準（MYRIAGENT_PERF_LAYOUTBENCH=<{名稱: 額外的 CSS} 的 JSON 檔>）：窗格與分頁很多的版面、不串流，
  // 各疊上那段 CSS 後量「改一格字＋強制版面配置」的平均毫秒數（token 數字的一格、側欄帳戶卡的一格、串流中回覆的最後一段）
  if (process.env.MYRIAGENT_PERF_LAYOUTBENCH) {
    const variants = Object.assign({ base: '' }, JSON.parse(fs.readFileSync(process.env.MYRIAGENT_PERF_LAYOUTBENCH, 'utf8')));
    const app = await openApp('perf-layoutbench', { vp: { w: 2480, h: 1400 }, layout: MANY_PANES, remote, hostSetup: manySetup([...MANY_SHOWN, ...MANY_HIDDEN]) });
    await sleep(3000);
    const out = {};
    for (let rep = 0; rep < 3; rep++) {
      for (const [name, css] of Object.entries(variants)) {
        const r = await app.page.evaluate(async (css) => {
          const st = document.createElement('style');
          st.textContent = css;
          document.head.append(st);
          await new Promise((res) => requestAnimationFrame(() => requestAnimationFrame(res)));
          const bench = (el, n) => {
            if (!el) return null;
            let t = el.firstChild;
            if (!t || t.nodeType !== 3) { el.textContent = 'x'; t = el.firstChild; }
            const orig = t.data;
            void document.documentElement.offsetHeight;
            const t0 = performance.now();
            for (let i = 0; i < n; i++) { t.data = i % 2 ? orig : (orig === '8' ? '3' : '8'); void document.documentElement.offsetHeight; }
            const ms = (performance.now() - t0) / n;
            t.data = orig;
            return Math.round(ms * 1000) / 1000;
          };
          const vis = [...document.querySelectorAll('.pane')].map((p) => p.querySelector('.conv:not([hidden]) .ci-turn .mtok-c, .ci-turn .mtok-c')).find(Boolean);
          const res = {
            composerTok: bench(document.querySelector('.ci-turn .mtok-c'), 200),
            sidebarTok: bench(document.querySelector('.macct-tok .mtok-c'), 200),
            tabTitle: bench(document.querySelector('.tab .tab-title'), 200),
            assistantP: bench([...document.querySelectorAll('.it-assistant .md p, .it-assistant p')].pop(), 200),
            monitorText: bench(document.querySelector('.mon-g-val, .mon-val, .mon-g-center span'), 200),
          };
          st.remove();
          await new Promise((res) => requestAnimationFrame(() => requestAnimationFrame(res)));
          return res;
        }, css);
        for (const [k, v] of Object.entries(r)) ((out[name] = out[name] || {})[k] = out[name][k] || []).push(v);
      }
    }
    await closeApp(app);
    const med = (a) => { const x = a.filter((v) => v != null).sort((p, q) => p - q); return x.length ? x[Math.floor(x.length / 2)] : null; };
    const table = Object.fromEntries(Object.entries(out).map(([n, r]) => [n, Object.fromEntries(Object.entries(r).map(([k, v]) => [k, med(v)]))]));
    check('perfLayoutBench', true, table);
    console.log(JSON.stringify(table, null, 1));
    return;
  }
  // 只跑窗格與分頁很多的版面（MYRIAGENT_PERF_MANY=1）：9 窗格側排、101 個分頁、3 個看得見的在串流、4 個在背景分頁執行
  if (process.env.MYRIAGENT_PERF_MANY) {
    const opts = Object.fromEntries(String(process.env.MYRIAGENT_PERF_BG_OFF || '').split(',').filter(Boolean).map((k) => [k, true]));
    // MYRIAGENT_PERF_CSS：多疊一段 CSS（A/B 用）；MYRIAGENT_PERF_BG_OFF 的 grow：最後一段每一拍都在長（真的串流）
    await scenario('many', { vp: { w: 2480, h: 1400 }, layout: MANY_PANES, hostSetup: manySetup([...MANY_SHOWN, ...MANY_HIDDEN]), keys: true, settle: 2000, css: process.env.MYRIAGENT_PERF_CSS || undefined },
      (page, host) => startBgStream(page, host, 250, Object.assign({ shown: MANY_SHOWN, hidden: MANY_HIDDEN }, opts)));
    check('perfMany', true, results.many);
    const v = results.many;
    console.log(JSON.stringify({ off: Object.keys(opts).join(',') || null, busy: v.busyMsPerSec, thread: v.threadMsPerSec, script: v.scriptMsPerSec, style: v.styleMsPerSec, layout: v.layoutMsPerSec, styleRecalcs: v.styleRecalcsPerSec, cpu: v.cpuMsPerSec || null, loops: v.loops, mainFrames: v.mainFramesPerSec, keys: v.keys }));
    if (v.invalidations) console.log(JSON.stringify(v.invalidations, null, 1));
    if (v.jsProfile) console.log(JSON.stringify(v.jsProfile, null, 1));
    if (v.notComposited) console.log(JSON.stringify(v.notComposited, null, 1));
    if (v.layers) console.log(JSON.stringify(v.layers, null, 1));
    return;
  }
  // A/B 實驗：MYRIAGENT_PERF_AB 指向 {名稱: 額外的 CSS} 的 JSON 檔時，只把四窗格的情境各疊上那段 CSS 量一次（找出哪一類效果貴），不做檢查
  if (process.env.MYRIAGENT_PERF_AB) {
    const variants = JSON.parse(fs.readFileSync(process.env.MYRIAGENT_PERF_AB, 'utf8'));
    for (const [name, css] of Object.entries(variants)) await scenario(name, { css }, name.startsWith('stream') ? startStream : undefined);
    check('perfAb', true, results);
    console.log(JSON.stringify(Object.fromEntries(Object.entries(results).map(([k, v]) => [k, { busy: v.busyMsPerSec, styleRecalcs: v.styleRecalcsPerSec, cpu: v.cpuMsPerSec || null, loops: v.loops }]))));
    return;
  }
  const chat = await scenario('chat', { events: true });
  // 同上但收起電腦狀態面板：分出面板本身的耗用
  await scenario('nomon', { layout: Object.assign({}, FOUR_PANES, { monitor: 'closed' }) });
  const stream = await scenario('stream', {}, startStream);
  // 六個窗格、六個對話同時串流，量測期間在輸入框打字（keys：按鍵等主執行緒的毫秒數）
  await scenario('stream6', { layout: SIX_PANES, hostSetup: multiStreamSetup(SIX_STREAMS), keys: true, settle: 2000 }, (page, host) => startMultiStream(page, host, SIX_STREAMS));
  // 兩個看得見的對話在串流、7 個執行中的對話在背景分頁（標頭每 250 ms），量測期間打字
  await scenario('bg', { layout: BG_PANES, hostSetup: multiStreamSetup([...BG_SHOWN, ...BG_HIDDEN]), keys: true, settle: 2000 }, (page, host) => startBgStream(page, host, 250));
  const dash = await scenario('dash', { layout: DASH });
  // 六窗格串流中拖曳分頁三次：拖曳期間串流的畫面更新排著、放開後一個影格內補上（等待的毫秒數只記在報告）
  const drag = await dragScenario();
  check('perfTabDragHoldsUpdates', drag.held && drag.applied && drag.steps.length === 3 && drag.steps.every((x) => !x.missing && x.intercepted !== false), drag);
  // 視窗沒有焦點（2026-10-24）：同四窗格與儀表板，但先送 blur——裝飾性的持續動畫停下，只剩狀態指示
  const blur = await scenario('blur', { blur: true });
  const dashBlur = await scenario('dashblur', { layout: DASH, blur: true });
  const fxOff = await scenario('fxoff', { settings: { visualEffects: false } });
  const calm = await scenario('calm', { reducedMotion: 'reduce' });
  // 執行中的四窗格、串流中、儀表板：無限循環的動畫只能動合成屬性（transform、opacity）
  check('perfNoMainThreadLoopsChat', chat.mainThreadLoops === 0, chat.mainThreadLoopList);
  // 頁面不派送 CSS 動畫事件（網頁自己不聽；有派送就是某個元素讓文件設下「有人在聽」的旗標，每個動畫每轉一圈都叫醒主執行緒）。
  // c1 的回覆有影片（PATH 上有 ffmpeg 時）：控制列的載入轉圈就是這樣的元素，影片要在 iframe 裡（framedvideo.js）
  check('perfNoAnimationEvents', Object.keys(chat.animationEvents).length === 0, { animationEvents: chat.animationEvents, mainFramesPerSec: chat.mainFramesPerSec, vframes: chat.vframes });
  check('perfNoMainThreadLoopsStream', stream.mainThreadLoops === 0, stream.mainThreadLoopList);
  check('perfNoMainThreadLoopsDash', dash.mainThreadLoops === 0, dash.mainThreadLoopList);
  // 減少動態、設定關掉動態效果：只剩載入中的轉圈（.spin）
  check('perfFxOffOnlySpinners', fxOff.loopNames.every((n) => n === 'spin'), fxOff.loopNames);
  check('perfCalmOnlySpinners', calm.loopNames.every((n) => n === 'spin'), calm.loopNames);
  // 視窗沒有焦點：還在跑的無限循環動畫都是狀態指示（INACTIVE_STATUS）或一格一格、每秒不超過 12 格的帳戶卡（INACTIVE_STEPPED），比有焦點時少；
  // 帳戶卡照樣在動（側欄與儀表板都有）；回到視窗（focus）後裝飾的動畫恢復
  check('perfInactiveOnlyStatus', [blur, dashBlur].every((v) => v.nonStatus.length === 0 && v.inactiveAttr === 'inactive' && v.refocusAttr === null)
    && blur.steppedRates.some((r) => /macct|mmeter|mtok/.test(r)) && dashBlur.steppedRates.some((r) => /acard|ring|spark/.test(r))
    && blur.loops < chat.loops && dashBlur.loops < dash.loops && blur.refocusLoops > blur.loops && dashBlur.refocusLoops > dashBlur.loops,
    { blur: blur.nonStatus, dashBlur: dashBlur.nonStatus, stepped: { chat: blur.steppedRates, dash: dashBlur.steppedRates }, loops: { chat: chat.loops, blur: blur.loops, blurRefocus: blur.refocusLoops, dash: dash.loops, dashBlur: dashBlur.loops, dashRefocus: dashBlur.refocusLoops } });
  // 每個情境（含關掉動態效果、減少動態）：沒有一直在重新開始、動非合成屬性的轉場——perfNoMainThreadLoops* 只看無限循環的動畫，
  // 電腦狀態面板每秒更新、每次都以 width／stroke-dashoffset 轉場的長條與量表就是從這裡漏掉的（2026-10-23）
  const steady = Object.entries(results).filter(([, v]) => v.steadyTransitions.length).map(([k, v]) => k + ': ' + v.steadyTransitions.join('、'));
  check('perfNoSteadyMainThreadTransitions', steady.length === 0, steady);
  check('perfSummary', true, results);
  console.log(JSON.stringify(Object.fromEntries(Object.entries(results).map(([k, v]) => [k, { busy: v.busyMsPerSec, style: v.styleMsPerSec, styleRecalcs: v.styleRecalcsPerSec, cpu: v.cpuMsPerSec || null, loops: v.loops, mainThreadLoops: v.mainThreadLoops, mainFrames: v.mainFramesPerSec, keys: v.keys || undefined }]))));
}
