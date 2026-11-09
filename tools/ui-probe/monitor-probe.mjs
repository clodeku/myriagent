// 電腦狀態面板（Web/monitor.js）的探針情境：probe.mjs 匯入、主迴圈呼叫 runMonitor（--monitor 時只跑這一組）。
// 假宿主收到 monitor {on:true} 才開始每秒送 deviceStats（MyriagentHub 主機、這台＋兩台在線、一台在線沒資料、一台離線、一台已撤銷），{on:false} 就停；
// 開始時先一次補 40 筆（ageMs 依序變小）讓走勢圖有一段歷史。截深色、淺色、滑過走勢圖、未配對、窄視窗，並檢查協定與版面。
import { syncStatus, XSS_NAME } from './sync-settings.mjs';
import { runDeviceFiles } from './devfiles-probe.mjs';

export const SELF = 'd_0123456789ABCDEFGHJKMNPQRS';
const LAB = 'd_4123456789ABCDEFGHJKMNPQRS';
const RENDER = 'd_5123456789ABCDEFGHJKMNPQRS';
const QUIET = 'd_6123456789ABCDEFGHJKMNPQRS';
const HOME = 'd_1123456789ABCDEFGHJKMNPQRS';
const OLD = 'd_2123456789ABCDEFGHJKMNPQRS';
const GB = 1024 ** 3;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const LAYOUT = { panes: [{ id: 'p1', tabs: ['c1'], active: 'c1' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };

async function until(fn, ms = 4000, step = 40) {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) {
    const v = await fn();
    if (v) return v;
    await sleep(step);
  }
  return null;
}

export function devices(now) {
  const dev = (o) => Object.assign({
    id: SELF, name: 'OFFICE-PC', signedName: 'OFFICE-PC', self: false, machineName: 'OFFICE-PC', appVersion: '0.3.0', fingerprint: 'K7QF-M2XA',
    keyStorage: 'tpm', online: true, lastSeenAt: now - 5000, remoteControlLevel: 'Control', allowRemoteFullAccess: false, verified: true, revoked: null,
  }, o);
  return [
    dev({ self: true }),
    dev({ id: LAB, name: 'LAB-WORKSTATION', machineName: 'LAB-WS', fingerprint: 'LAB0-WS12' }),
    // 名稱是 XSS 字串：一律 textContent
    dev({ id: RENDER, name: XSS_NAME, machineName: 'RENDER', fingerprint: 'REND-ER12' }),
    dev({ id: QUIET, name: 'NAS-BOX', machineName: 'NAS', fingerprint: 'NAS0-BX12', appVersion: '0.2.1' }),
    dev({ id: HOME, name: 'HOME-PC', machineName: 'HOME-PC', fingerprint: 'H0ME-PC12', online: false, lastSeenAt: now - 3 * 3600_000 }),
    dev({ id: OLD, name: 'OLD-PC', machineName: 'OLD-PC', fingerprint: 'OLD0-PC12', online: false, lastSeenAt: now - 40 * 86400_000, revoked: { at: now - 30 * 86400_000, reason: 'retired' } }),
  ];
}

/** 第 k 筆樣本（k 越大越新）：數字隨 k 平滑變動 */
function sample(k, o) {
  const w = (a, b, p) => a + (b - a) * (0.5 + 0.5 * Math.sin(k / p));
  const self = [];
  if (o.self !== false) {
    self.push({ id: o.paired ? SELF : null, self: true, name: 'OFFICE-PC', seq: k, ageMs: 40, cpu: Math.round(w(8, 46, 3.1) * 10) / 10, cores: 16, cpuName: 'AMD Ryzen 9 7950X 16-Core Processor',
      memUsed: Math.round(w(17.2, 21.8, 7) * GB), memTotal: 32 * GB,
      disks: [{ name: 'C:', used: 612 * GB, total: 931 * GB }, { name: 'D:', used: 1.21 * 1024 * GB, total: 1.82 * 1024 * GB }],
      netRx: Math.round(w(40_000, 2_600_000, 2.3)), netTx: Math.round(w(9_000, 420_000, 4.1)), rttMs: o.paired ? Math.round(w(18, 44, 2.7)) : null, uptimeSec: 3 * 86400 + 4 * 3600 + k,
      gpus: [{ name: 'NVIDIA GeForce RTX 4090', util: Math.round(w(5, 62, 2.5) * 10) / 10, memUsed: Math.round(w(6, 14, 5) * GB), memTotal: 24 * GB },
        { name: 'AMD Radeon(TM) Graphics', util: Math.round(w(0, 3, 3) * 10) / 10, memUsed: Math.round(0.3 * GB), memTotal: 2 * GB }],
      cpuTemp: Math.round(w(48, 71, 3.7) * 10) / 10, cpuTempSource: 'hwinfo' });
  }
  if (!o.paired) return self;
  return self.concat([
    { id: LAB, self: false, name: null, seq: Math.floor(k / 2), ageMs: 900, cpu: Math.round(w(30, 64, 2.2) * 10) / 10, cores: 32, cpuName: 'Intel Xeon w7-2495X',
      memUsed: Math.round(w(70, 99, 5) * GB), memTotal: 128 * GB, disks: [{ name: 'C:', used: 1.6 * 1024 * GB, total: 1.82 * 1024 * GB }],
      netRx: Math.round(w(200_000, 9_800_000, 3)), netTx: Math.round(w(1_200_000, 5_500_000, 2)), rttMs: Math.round(w(70, 128, 3.3)), uptimeSec: 26 * 3600,
      gpus: [{ name: 'NVIDIA RTX A6000', util: Math.round(w(72, 99, 1.9) * 10) / 10, memUsed: Math.round(w(40, 47, 4) * GB), memTotal: 48 * GB }], cpuTemp: 84, cpuTempSource: 'lhm' },
    { id: RENDER, self: false, name: null, seq: Math.floor(k / 2), ageMs: 1300, cpu: Math.round(w(91, 99.5, 1.7) * 10) / 10, cores: 24, cpuName: 'AMD Threadripper 7960X',
      memUsed: Math.round(w(60, 62.5, 4) * GB), memTotal: 64 * GB, disks: [{ name: 'C:', used: 470 * GB, total: 476 * GB }, { name: 'E:', used: 2 * 1024 * GB, total: 3.64 * 1024 * GB }],
      netRx: Math.round(w(0, 3_000, 1.5)), netTx: Math.round(w(0, 800, 2.5)), rttMs: Math.round(w(210, 380, 1.9)), uptimeSec: 12 * 60, cpuTemp: 93.4, cpuTempSource: 'acpi' },
  ]);
}

/** MyriagentHub 主機（hub；已配對才有）：第 k 筆 */
function hubSample(k, ageMs = 600) {
  const w = (a, b, p) => a + (b - a) * (0.5 + 0.5 * Math.sin(k / p));
  return { name: 'HUB-PC', seq: k, ageMs, cpu: Math.round(w(4, 31, 2.9) * 10) / 10, cores: 12, cpuName: 'Intel Xeon E-2386G',
    memUsed: Math.round(w(41, 47, 6) * GB), memTotal: 64 * GB, disks: [{ name: 'C:', used: 402 * GB, total: 952 * GB }, { name: 'D:', used: 3.1 * 1024 * GB, total: 3.64 * 1024 * GB }],
    netRx: Math.round(w(60_000, 1_900_000, 2.6)), netTx: Math.round(w(120_000, 3_600_000, 3.4)), uptimeSec: 41 * 86400 + 7 * 3600 + k, devices: 3, browsers: 2 };
}

/** 一則 deviceStats：已配對的帶 hub，未配對的 hub 是 null */
const statsMsg = (k, o, extraAge = 0) => ({
  type: 'deviceStats',
  devices: sample(k, o).map((d) => Object.assign({}, d, { ageMs: d.ageMs + extraAge })),
  hub: o.paired ? hubSample(k, 600 + extraAge) : null,
});

/** 假宿主：monitor on 時先補 40 筆歷史、之後每秒一筆；off 就停 */
export function pumpSetup(o) {
  return (host) => {
    let k = 100;
    host.monitorCmds = [];
    host.onCmd = (m) => {
      if (m.cmd !== 'monitor') return;
      host.monitorCmds.push(m.on);
      clearInterval(host.statsTimer);
      host.statsTimer = 0;
      if (!m.on) return;
      for (let i = 40; i >= 1; i--) host.send(statsMsg(k - i, o, i * 1000));
      host.send(statsMsg(k, o));
      host.statsTimer = setInterval(() => host.send(statsMsg(++k, o)), 1000);
    };
  };
}

/**
 * 面板裡溢出的元素：任何元素超出面板左右緣；有文字的元素文字被裁掉（不是刻意的 text-overflow: ellipsis）或和同一列的兄弟重疊。
 * 裝飾用的偽元素（掃描光、雷達脈衝、LED 條的反光）本來就由 overflow: hidden 裁掉，不算
 */
const overflowOf = (page) => page.evaluate(() => {
  const bad = [];
  const panel = document.getElementById('monitor');
  if (!panel) return ['沒有面板'];
  const pr = panel.getBoundingClientRect();
  const label = (el) => (typeof el.className === 'string' && el.className) || el.tagName;
  for (const el of panel.querySelectorAll('*')) {
    if (el.closest('svg') || el.closest('[hidden]')) continue;
    const r = el.getBoundingClientRect();
    if (!r.width) continue;
    if (r.right > pr.right + 0.5 || r.left < pr.left - 0.5) { bad.push(label(el) + ' 超出面板'); continue; }
    const text = Array.from(el.childNodes).some((n) => n.nodeType === 3 && n.textContent.trim());
    if (!text) continue;
    const cs = getComputedStyle(el);
    if (cs.textOverflow !== 'ellipsis' && el.scrollWidth > el.clientWidth + 1) bad.push(label(el) + ' 文字被裁切：' + el.textContent);
    const parent = el.parentElement;
    for (const sib of parent ? parent.children : []) {
      if (sib === el || sib.hidden || sib.closest('svg')) continue;
      const s = sib.getBoundingClientRect();
      if (!s.width || getComputedStyle(sib).position === 'absolute' || cs.position === 'absolute') continue;
      const overlapX = Math.min(r.right, s.right) - Math.max(r.left, s.left);
      const overlapY = Math.min(r.bottom, s.bottom) - Math.max(r.top, s.top);
      if (overlapX > 1 && overlapY > 1) bad.push(label(el) + ' 和 ' + label(sib) + ' 重疊');
    }
  }
  return Array.from(new Set(bad));
});

const cardsOf = (page) => page.evaluate(() => Array.from(document.querySelectorAll('#monitor .mon-dev')).map((c) => ({
  kind: c.dataset.kind, state: c.dataset.state, self: c.dataset.self, conn: c.querySelector('.mon-conn') ? c.querySelector('.mon-conn').getAttribute('aria-label') : '', name: c.querySelector('.mon-name').textContent, msg: c.querySelector('.mon-msg').hidden ? '' : c.querySelector('.mon-msg').textContent,
  sevs: Array.from(c.querySelectorAll('.mon-g-sev')).filter((e) => !e.hidden).map((e) => e.textContent),
  lat: c.querySelector('.mon-lat-text') ? c.querySelector('.mon-lat-text').textContent : '',
  disks: Array.from(c.querySelectorAll('.mon-disk')).map((d) => d.getAttribute('aria-label')),
  hw: c.querySelector('.mon-hw').hidden ? [] : Array.from(c.querySelectorAll('.mon-hwrow')).map((r) => ({ kind: r.dataset.kind, sev: r.dataset.sev,
    name: r.querySelector('.mon-hw-name').textContent, val: r.querySelector('.mon-disk-pct').textContent, extra: r.querySelector('.mon-disk-size').textContent, label: r.getAttribute('aria-label') })),
})));

export async function runMonitor(ctx, vp) {
  const { openApp, closeApp, check, shot, NOW } = ctx;
  const remote = { mode: 'desktop', deviceId: SELF, devices: devices(NOW), sync: syncStatus(NOW, 'online') };

  // ── 深色、已配對 ──
  const app = await openApp('monitor-dark', { vp, layout: LAYOUT, remote, hostSetup: pumpSetup({ paired: true }) });
  const { page, host } = app;
  check('monitorAutoOpenWide', await until(() => page.evaluate(() => !document.getElementById('monitor').hidden)), '1440 寬、沒選過：自動顯示');
  check('monitorSentOnAfterInit', await until(() => host.monitorCmds.includes(true)), host.monitorCmds);
  await until(() => page.evaluate(() => document.querySelectorAll('#monitor .mon-dev[data-state="live"]').length >= 3), 5000);
  await sleep(2600);
  const cards = await cardsOf(page);
  check('monitorCardsOrder', JSON.stringify(cards.map((c) => [c.kind, c.state, c.self])) === JSON.stringify([['hub', 'live', 'false'], ['device', 'live', 'true'], ['device', 'live', 'false'],
    ['device', 'live', 'false'], ['device', 'nodata', 'false'], ['device', 'offline', 'false']]), cards);
  const hubCard = cards[0] || {};
  const count = await page.evaluate(() => document.querySelector('#monitor .mon-count').textContent);
  const hubSub = await page.evaluate(() => document.querySelector('#monitor .mon-dev[data-kind="hub"] .mon-sub').textContent);
  check('monitorHubCard', hubCard.name === 'Hub 主機' && /^HUB-PC · /.test(hubSub) && hubCard.conn === '連線：3 台電腦、2 個瀏覽器' && count === '4 / 5 在線'
    && await page.evaluate(() => !document.querySelector('#monitor .mon-dev[data-kind="hub"] .mon-chart[data-kind="rtt"]')), { hubCard, hubSub, count });
  // 名稱不被截斷（卡片標頭的開機時間比名稱先縮）
  check('monitorNamesNotTruncated', await page.evaluate(() => Array.from(document.querySelectorAll('#monitor .mon-dev:not([data-kind="hub"]) .mon-name, #monitor .mon-dev[data-kind="hub"] .mon-name'))
    .filter((n) => !/onerror/.test(n.textContent)).every((n) => n.scrollWidth <= n.clientWidth)));
  check('monitorRevokedHidden', !cards.some((c) => c.name === 'OLD-PC'), cards.map((c) => c.name));
  check('monitorXssAsText', cards.some((c) => c.name === XSS_NAME) && (await page.evaluate(() => !document.querySelector('#monitor img'))), '名稱以 textContent 顯示、沒有 img');
  const render = cards.find((c) => c.name === XSS_NAME) || {};
  check('monitorSeverityText', (render.sevs || []).includes('滿載') && render.lat === '偏慢' && (render.disks || []).some((d) => /快滿/.test(d)), render);
  const nodata = cards.find((c) => c.state === 'nodata') || {};
  check('monitorNoDataText', nodata.msg === '沒有狀態資料（版本較舊或剛連上）', nodata);
  // CPU 溫度與顯示卡：這台兩張卡（廠牌當標籤）、LAB 一張（GPU）而且溫度偏高、RENDER 只有 ACPI 溫度而且過熱、主機與沒資料的不列
  const mine = cards.find((c) => c.self === 'true') || {};
  const lab = cards.find((c) => c.name === 'LAB-WORKSTATION') || {};
  const hw = (c) => (c.hw || []).map((r) => r.kind + ':' + r.name);
  check('monitorHardwareRows', JSON.stringify(hw(mine)) === JSON.stringify(['temp:溫度', 'gpu:NVIDIA', 'gpu:AMD'])
    && /^\d+°C$/.test(mine.hw[0].val) && mine.hw[0].extra === 'HWiNFO' && /來源：HWiNFO/.test(mine.hw[0].label)
    && /^\d+(\.\d)? \/ 24 GB$/.test(mine.hw[1].extra) && /^NVIDIA GeForce RTX 4090 使用率 /.test(mine.hw[1].label)
    && JSON.stringify(hw(lab)) === JSON.stringify(['temp:溫度', 'gpu:GPU']) && lab.hw[0].sev === 'warn' && /偏高/.test(lab.hw[0].label) && lab.hw[0].extra === 'LHM'
    && JSON.stringify(hw(render)) === JSON.stringify(['temp:溫度']) && render.hw[0].sev === 'max' && render.hw[0].val === '93°C' && /ACPI 熱區/.test(render.hw[0].label)
    && hw(hubCard).length === 0 && hw(nodata).length === 0, { mine: mine.hw, lab: lab.hw, render: render.hw, hub: hubCard.hw, nodata: nodata.hw });
  check('monitorNoOverflow', (await overflowOf(page)).length === 0, await overflowOf(page));
  // 每秒的更新只改內容、不重建節點（滑鼠停在走勢圖上的說明不閃）
  await page.evaluate(() => { window.__monRef = document.querySelector('#monitor .mon-dev[data-self="true"] .mon-g-arc'); window.__monNum = document.querySelector('#monitor .mon-dev[data-self="true"] .mon-g-num').textContent; });
  await sleep(2200);
  check('monitorUpdatesInPlace', await page.evaluate(() => window.__monRef.isConnected && window.__monRef === document.querySelector('#monitor .mon-dev[data-self="true"] .mon-g-arc')));
  check('monitorSparkHasHistory', await page.evaluate(() => (document.querySelector('#monitor .mon-dev[data-self="true"] .mon-chart[data-kind="net"] .mon-line').getAttribute('d') || '').split('L').length > 30));
  // 動畫的轉速跟著負載（2026-10-25）：LAB 那台（顯示卡 72～99%、84°C、流量大）的雷達與長條掃光比這台快；
  // 轉速改變時以 updatePlaybackRate 改倍速，時間軸連續：逐個影格記錄雷達掃描的 currentTime，相鄰兩格的差不超過「經過的時間 × 倍速」，不倒退、不跳
  const tempo = await page.evaluate(() => new Promise((resolve) => {
    const card = (name) => Array.from(document.querySelectorAll('#monitor .mon-dev')).find((c) => c.querySelector('.mon-name').textContent === name);
    const rateOf = (el, pseudo) => { const a = el && el.getAnimations({ subtree: true }).find((x) => !pseudo || (x.effect && x.effect.pseudoElement === pseudo)); return a ? a.playbackRate : null; };
    const lab = card('LAB-WORKSTATION'), self = document.querySelector('#monitor .mon-dev[data-self="true"]');
    const sweep = lab && lab.querySelector('.mon-radar-sweep');
    const anim = sweep && sweep.getAnimations()[0];
    if (!anim) { resolve(null); return; }
    const samples = [];
    const t0 = performance.now();
    const step = () => {
      samples.push({ t: performance.now(), ct: anim.currentTime, rate: anim.playbackRate, dur: anim.effect.getTiming().duration });
      if (performance.now() - t0 < 3200) requestAnimationFrame(step);
      else resolve({
        samples,
        labGpu: rateOf(lab.querySelector('.mon-hwrow[data-kind="gpu"] .mon-seg-fill'), '::after'),
        selfGpu: rateOf(self.querySelectorAll('.mon-hwrow[data-kind="gpu"] .mon-seg-fill')[1], '::after'),
        labTemp: rateOf(lab.querySelector('.mon-hwrow[data-kind="temp"] .mon-seg-fill'), '::after'),
        labRadar: anim.playbackRate,
        head: rateOf(document.querySelector('#monitor .mon-head'), '::after'),
      });
    };
    requestAnimationFrame(step);
  }));
  let tempoJump = null;
  if (tempo) {
    const s = tempo.samples;
    tempoJump = { worst: 0, back: false, rates: [...new Set(s.map((x) => Math.round(x.rate * 1000) / 1000))], durs: [...new Set(s.map((x) => x.dur))] };
    for (let i = 1; i < s.length; i++) {
      const d = s[i].ct - s[i - 1].ct;
      if (d < -0.5) tempoJump.back = true;
      const allowed = (s[i].t - s[i - 1].t) * Math.max(s[i].rate, s[i - 1].rate, 1) + 20;
      tempoJump.worst = Math.max(tempoJump.worst, d - allowed);
    }
    delete tempo.samples;
  }
  check('monitorTempo', !!tempo && tempo.labGpu > 2 && tempo.selfGpu < 1 && tempo.labTemp > 2 && tempo.labRadar > 1 && tempo.head > 1
    && !!tempoJump && !tempoJump.back && tempoJump.worst <= 0 && tempoJump.durs.length === 1 && tempoJump.rates.length >= 2, { tempo, tempoJump });
  await shot(page, `monitor-dark-${vp.w}`);
  await page.locator('#monitor').screenshot({ path: ctx.outFile(`monitor-panel-dark-${vp.w}.png`) });

  // 滑過走勢圖：十字線＋說明；資料更新後說明還在
  const plot = page.locator('#monitor .mon-dev[data-self="true"] .mon-chart[data-kind="net"] .mon-plot');
  const box = await plot.boundingBox();
  await page.mouse.move(box.x + box.width * 0.7, box.y + box.height / 2);
  const tipOn = await until(() => page.evaluate(() => { const t = document.querySelector('.tip'); return !!t && !t.hidden && /下載/.test(t.textContent); }), 2000);
  await sleep(1300);
  const tipStill = await page.evaluate(() => { const t = document.querySelector('.tip'); return !!t && !t.hidden && /上傳/.test(t.textContent); });
  check('monitorHoverTip', !!tipOn && tipStill && await page.evaluate(() => !document.querySelector('#monitor .mon-dev[data-self="true"] .mon-chart[data-kind="net"] .mon-cross').hidden));
  await page.locator('#monitor').screenshot({ path: ctx.outFile(`monitor-hover-dark-${vp.w}.png`) });
  await page.mouse.move(5, 5);
  check('monitorHoverLeave', await until(() => page.evaluate(() => { const t = document.querySelector('.tip'); return !t || t.hidden; }), 1500));

  // Ctrl+Shift+M 收起：送 off、版面記 closed；再按一次打開又送 on
  const before = host.monitorCmds.length;
  await page.keyboard.press('Control+Shift+M');
  const hidden = await until(() => page.evaluate(() => document.getElementById('monitor').hidden));
  const offSent = await until(() => host.monitorCmds.length > before && host.monitorCmds[host.monitorCmds.length - 1] === false);
  const layoutClosed = await until(() => host.received.some((m) => m.cmd === 'layout' && m.layout && m.layout.monitor === 'closed'), 2000);
  check('monitorToggleOff', !!hidden && !!offSent && !!layoutClosed, { hidden, offSent, layoutClosed, cmds: host.monitorCmds });
  await page.click('#btnMonitor');
  check('monitorToggleOn', await until(() => page.evaluate(() => !document.getElementById('monitor').hidden)) && await until(() => host.monitorCmds[host.monitorCmds.length - 1] === true));
  // 儀表板：面板藏起來、送 off；回到對話又送 on
  await page.click('#viewSwitch [data-view="dashboard"]');
  const dashOff = await until(() => host.monitorCmds[host.monitorCmds.length - 1] === false && page.evaluate(() => document.getElementById('monitor').hidden));
  await page.click('#viewSwitch [data-view="chat"]');
  const chatOn = await until(() => host.monitorCmds[host.monitorCmds.length - 1] === true);
  check('monitorDashboardOff', !!dashOff && !!chatOn, host.monitorCmds);
  clearInterval(host.statsTimer);
  await closeApp(app);

  // ── 淺色 ──
  const light = await openApp('monitor-light', { vp, layout: LAYOUT, remote, settings: { theme: 'light' }, colorScheme: 'light', hostSetup: pumpSetup({ paired: true }) });
  await until(() => light.page.evaluate(() => document.querySelectorAll('#monitor .mon-dev[data-state="live"]').length >= 3), 5000);
  await sleep(2600);
  check('monitorNoOverflowLight', (await overflowOf(light.page)).length === 0, await overflowOf(light.page));
  await shot(light.page, `monitor-light-${vp.w}`);
  await light.page.locator('#monitor').screenshot({ path: ctx.outFile(`monitor-panel-light-${vp.w}.png`) });
  clearInterval(light.host.statsTimer);
  await closeApp(light);

  // ── 未配對、1100 寬：預設收起；打開後只有這台與提示 ──
  const un = await openApp('monitor-unpaired', { vp: { w: 1100, h: 700 }, layout: LAYOUT, hostSetup: pumpSetup({ paired: false }) });
  const startHidden = await un.page.evaluate(() => document.getElementById('monitor').hidden);
  const offFirst = await until(() => un.host.monitorCmds.length > 0 && un.host.monitorCmds[0] === false);
  await un.page.click('#btnMonitor');
  await until(() => un.page.evaluate(() => document.querySelector('#monitor .mon-dev[data-state="live"]')), 4000);
  await sleep(1200);
  const unCards = await cardsOf(un.page);
  const foot = await un.page.evaluate(() => { const f = document.querySelector('#monitor .mon-foot'); return f.hidden ? '' : f.textContent; });
  check('monitorUnpaired', startHidden && !!offFirst && unCards.length === 1 && unCards[0].kind === 'device' && unCards[0].lat === '未配對' && /配對之後/.test(foot), { startHidden, offFirst, unCards, foot });
  check('monitorNoOverflowUnpaired', (await overflowOf(un.page)).length === 0, await overflowOf(un.page));
  await shot(un.page, 'monitor-unpaired-1100');
  clearInterval(un.host.statsTimer);
  await closeApp(un);

  // ── 窄視窗（700）：抽屜疊在右邊，預設收起 ──
  const narrow = await openApp('monitor-narrow', { vp: { w: 700, h: 760 }, layout: Object.assign({}, LAYOUT, { monitor: 'open' }), remote, hostSetup: pumpSetup({ paired: true }) });
  const narrowStart = await narrow.page.evaluate(() => document.getElementById('monitor').hidden);
  await narrow.page.click('#btnMonitor');
  await until(() => narrow.page.evaluate(() => document.querySelectorAll('#monitor .mon-dev[data-state="live"]').length >= 3), 5000);
  await sleep(1500);
  const overlay = await narrow.page.evaluate(() => getComputedStyle(document.getElementById('monitor')).position === 'absolute');
  check('monitorNarrowDrawer', narrowStart && overlay && (await overflowOf(narrow.page)).length === 0, { narrowStart, overlay, overflow: await overflowOf(narrow.page) });
  await shot(narrow.page, 'monitor-narrow-700');
  clearInterval(narrow.host.statsTimer);
  await closeApp(narrow);

  // 電腦狀態面板的檔案傳輸（devfiles-probe.mjs）
  await runDeviceFiles(ctx, vp, { SELF, LAB, LAYOUT, devices, pumpSetup });
}
