// 儀表板「每週額度每 5% 的 token」（web-protocol 的 usageSegments；Web/usageseg.js、Web/usagesegrules.js）的情境：probe.mjs 匯入，--usageseg 只跑它，完整執行時也跑。
// 檢查名稱 usageSeg*：切到儀表板送 usageSegments、沒有資料時不顯示、沒有紀錄時的說明、平均表（每 5%、範圍、Claude 每週・模型不相符的模型畫淡）、
// 正在記的、最近的紀錄（不完整的標出）、沒有 null／undefined／NaN、表格不撐破版面、「在檔案總管顯示」送 revealPath、「複製成 CSV」。截圖 usageseg-*.png。
// 第九批（2026-10-26）：數字格的比例底條、各模型的組成條、正在記的那一段的進度條（usageSegBars）

const NOW = Date.now();
const MIN = 60e3;

const tokens = (input, cached, cacheWrite, output) => ({ input, cached, cacheWrite, output });

/** 假的 UsageSegmentsView（形狀見 web-protocol） */
export function makeSegments(o = {}) {
  return Object.assign({
    path: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\usage-segments\\records.jsonl',
    total: 214,
    summary: [
      { accountId: 'a1', accountLabel: '工作用 Max', provider: 'Claude', windowKey: 'weekly', windowLabel: '每週', model: 'claude-opus-5-5', records: 9, percent: 45.6,
        tokens: tokens(4_512_345_678, 4_401_000_000, 61_000_000, 9_870_123), per5: tokens(494_774_745, 482_565_789, 6_688_596, 1_082_250), minPer5: 401_234_567, maxPer5: 612_345_678, lastAt: NOW - 30 * MIN },
      { accountId: 'a1', accountLabel: '工作用 Max', provider: 'Claude', windowKey: 'weekly:Opus', windowLabel: '每週・Opus', model: 'claude-opus-5-5', records: 4, percent: 20.2,
        tokens: tokens(2_000_000_000, 1_950_000_000, 30_000_000, 4_000_000), per5: tokens(495_049_504, 482_673_267, 7_425_742, 990_099), minPer5: 450_000_000, maxPer5: 540_000_000, lastAt: NOW - 60 * MIN },
      { accountId: 'a1', accountLabel: '工作用 Max', provider: 'Claude', windowKey: 'weekly:Opus', windowLabel: '每週・Opus', model: 'claude-sonnet-5-5', records: 1, percent: 5,
        tokens: tokens(300_000_000, 290_000_000, 5_000_000, 600_000), per5: tokens(300_000_000, 290_000_000, 5_000_000, 600_000), minPer5: 300_600_000, maxPer5: 300_600_000, lastAt: NOW - 3 * 60 * MIN },
      { accountId: 'a3', accountLabel: 'Codex 團隊', provider: 'Codex', windowKey: 'codex:10080', windowLabel: '每週', model: 'gpt-6-astra', records: 6, percent: 31,
        tokens: tokens(1_200_000_000, 1_180_000_000, 0, 4_500_000), per5: tokens(193_548_387, 190_322_580, 0, 725_806), minPer5: 150_000_000, maxPer5: 260_000_000, lastAt: NOW - 5 * MIN },
    ],
    open: [
      { accountId: 'a1', accountLabel: '工作用 Max', provider: 'Claude', windowKey: 'weekly', windowLabel: '每週', bucket: 45, startPercent: 45.1, lastPercent: 47, startedAt: NOW - 50 * MIN,
        lastSeenAt: NOW - MIN, complete: true, models: [{ model: 'claude-opus-5-5', ...tokens(180_000_000, 176_000_000, 2_000_000, 410_000) }, { model: 'claude-haiku-5-5', ...tokens(2_000_000, 1_000_000, 100_000, 30_000) }] },
      { accountId: 'a3', accountLabel: 'Codex 團隊', provider: 'Codex', windowKey: 'codex:10080', windowLabel: '每週', bucket: 30, startPercent: 32, lastPercent: 33, startedAt: NOW - 20 * MIN,
        lastSeenAt: NOW - 2 * MIN, complete: false, models: [{ model: 'gpt-6-astra', ...tokens(40_000_000, 39_000_000, 0, 120_000) }] },
    ],
    records: [
      { id: 'r3', accountId: 'a1', accountLabel: '工作用 Max', provider: 'Claude', windowKey: 'weekly', windowLabel: '每週', fromBucket: 40, toBucket: 45, startPercent: 40.2, endPercent: 45.1,
        startedAt: NOW - 3 * 60 * MIN, endedAt: NOW - 50 * MIN, complete: true, model: 'claude-opus-5-5',
        models: [{ model: 'claude-opus-5-5', ...tokens(498_000_000, 487_000_000, 7_000_000, 1_100_000) }, { model: 'claude-haiku-5-5', ...tokens(3_000_000, 2_000_000, 0, 40_000) }] },
      { id: 'r2', accountId: 'a3', accountLabel: 'Codex 團隊', provider: 'Codex', windowKey: 'codex:10080', windowLabel: '每週', fromBucket: 25, toBucket: 30, startPercent: 25, endPercent: 30,
        startedAt: NOW - 30 * 60 * MIN, endedAt: NOW - 25 * MIN, complete: true, model: 'gpt-6-astra', models: [{ model: 'gpt-6-astra', ...tokens(201_000_000, 198_000_000, 0, 700_000) }] },
      { id: 'r1', accountId: 'a1', accountLabel: '工作用 Max', provider: 'Claude', windowKey: 'weekly:Opus', windowLabel: '每週・Opus', fromBucket: 10, toBucket: 15, startPercent: 12.3, endPercent: 15.2,
        startedAt: NOW - 2 * 1440 * MIN, endedAt: NOW - 1440 * MIN, complete: false, model: '（混用）', models: [{ model: 'claude-opus-5-5', ...tokens(100, 50, 10, 5) }] },
    ],
  }, o);
}

function sectionState() {
  const sec = document.querySelector('#dashboard .dash-sec-us');
  if (!sec) return null;
  const shown = getComputedStyle(sec).display !== 'none' && sec.getBoundingClientRect().height > 0;
  const blocks = Array.from(sec.querySelectorAll('.us-block')).map((b) => ({
    hidden: b.hidden, title: b.querySelector('.us-h').textContent,
    rows: Array.from(b.querySelectorAll('tbody tr')).map((tr) => ({ cls: tr.className, cells: Array.from(tr.cells).map((c) => c.textContent) })),
  }));
  const dash = document.getElementById('dashboard');
  return {
    shown, text: sec.textContent, empty: !sec.querySelector('.us-empty').hidden ? sec.querySelector('.us-empty').textContent : null, blocks,
    secOverflow: sec.scrollWidth > sec.clientWidth + 1, pageOverflow: dash.scrollWidth > dash.clientWidth + 1,
    copyDisabled: sec.querySelector('.dash-sec-head .btn').disabled,
  };
}

export async function runUsageSegments(ctx, vp) {
  const { openApp, closeApp, check, until, settle, outFile } = ctx;
  const onePane = (id, view) => ({ panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: false, view });
  const app = await openApp('usageseg', { vp, layout: onePane('c1', 'dashboard') });
  const { page, host } = app;

  const first = await until(() => host.last('usageSegments'), 3000);
  check('usageSegRequestOnDashboard', !!first, first);
  const before = await page.evaluate(sectionState);
  check('usageSegHiddenWithoutData', !!before && !before.shown, before);

  // 有檢視但還沒有任何紀錄：寫說明
  await host.send({ type: 'usageSegments', usageSegments: makeSegments({ total: 0, summary: [], open: [], records: [] }) });
  await settle(page, 150);
  const empty = await page.evaluate(sectionState);
  check('usageSegEmptyNote', empty.shown && /還沒有資料/.test(empty.empty || '') && empty.blocks.every((b) => b.hidden) && empty.copyDisabled, empty);

  await host.send({ type: 'usageSegments', usageSegments: makeSegments() });
  await settle(page, 200);
  const s = await page.evaluate(sectionState);
  const [sum, open, rec] = s.blocks;
  check('usageSegNoNullText', !/null|undefined|NaN/.test(s.text), s.text.slice(0, 400));
  check('usageSegSummary', !sum.hidden && sum.rows.length === 4 && sum.rows[0].cells[0].endsWith('工作用 Max') && sum.rows[0].cells[3] === '9'
    && sum.rows[0].cells[4] === '494.7M' && sum.rows[0].cells[7] === '1.082M' && sum.rows[0].cells[8] === '401.2M ～ 612.3M', sum.rows[0]);
  check('usageSegScopedDim', sum.rows[1].cls !== 'us-dim' && sum.rows[2].cls === 'us-dim' && sum.rows[3].cls !== 'us-dim', sum.rows.map((r) => r.cls));
  check('usageSegOpen', !open.hidden && open.rows.length === 2 && open.rows[0].cells[2] === '45% → 50%' && open.rows[0].cells[3] === '47%'
    && open.rows[0].cells[4] === '182.0M' && /從 32% 開始，不完整/.test(open.rows[1].cells[2]), open.rows);
  check('usageSegRecords', !rec.hidden && rec.title === '最近的紀錄（共 214 筆，這裡列最近 3 筆）' && rec.rows.length === 3 && rec.rows[0].cells[3] === '40% → 45%'
    && rec.rows[0].cells[4] === '2 小時 10 分' && rec.rows[2].cls === 'us-partial' && /不完整/.test(rec.rows[2].cells[3]) && rec.rows[2].cells[7] === '（混用）', rec.rows);
  check('usageSegNoOverflow', !s.secOverflow && !s.pageOverflow, { secOverflow: s.secOverflow, pageOverflow: s.pageOverflow });
  // 第九批：每 5% 輸入那一欄的底條（最大的那列 1、其他依比例）；正在記的第一列 47% 在 45% → 50% 走了 40%、組成條兩段（opus 佔大部分）；
  // 最近的紀錄的主要模型前面也有組成條，字照舊
  const bars = await page.evaluate(() => {
    const sec = document.querySelector('#dashboard .dash-sec-us');
    const [sum, open, rec] = Array.from(sec.querySelectorAll('.us-block'));
    const f = (td) => Number(td.style.getPropertyValue('--us-f'));
    const prog = open.querySelector('tbody tr .us-prog');
    const mix = open.querySelector('tbody tr .us-mix');
    return {
      sumInput: Array.from(sum.querySelectorAll('tbody tr')).map((tr) => f(tr.cells[4])),
      sumBg: getComputedStyle(sum.querySelector('tbody tr td.us-bar')).backgroundImage,
      prog: prog ? Math.round(prog.firstElementChild.getBoundingClientRect().width / prog.getBoundingClientRect().width * 100) : null,
      progTitle: prog ? prog.title : null,
      mix: mix ? Array.from(mix.children).map((i) => i.className + ':' + Math.round(i.getBoundingClientRect().width)) : null,
      mixTitle: mix ? mix.title : null,
      recMix: rec.querySelectorAll('tbody tr .us-mix').length,
      recModel: rec.querySelector('tbody tr td.us-model').textContent,
    };
  });
  check('usageSegBars', bars.sumInput.length === 4 && bars.sumInput[1] === 1 && Math.abs(bars.sumInput[0] - 0.999) < 0.002 && Math.abs(bars.sumInput[3] - 0.391) < 0.002
    && /linear-gradient/.test(bars.sumBg) && bars.prog === 40 && /^這一段走了 40%：47%，到 50% 記一筆$/.test(bars.progTitle || '')
    && !!bars.mix && bars.mix.length === 2 && bars.mix[0].startsWith('mt-opus:') && bars.mix[1].startsWith('mt-haiku:') && /^claude-opus-5-5 98\.9%/.test(bars.mixTitle || '')
    && bars.recMix === 3 && bars.recModel === 'claude-opus-5-5', bars);
  await page.evaluate(() => document.querySelector('#dashboard .dash-sec-us').scrollIntoView({ block: 'start' }));
  await settle(page, 150);
  await page.locator('#dashboard .dash-sec-us').screenshot({ path: outFile('usageseg-dark.png') });

  // 在檔案總管顯示紀錄檔
  await page.click('#dashboard .dash-sec-us .us-reveal');
  const reveal = await until(() => host.last('revealPath'), 2000);
  check('usageSegReveal', !!reveal && reveal.path.endsWith('\\usage-segments\\records.jsonl'), reveal);
  // 複製成 CSV（WebView2 以外 navigator.clipboard 可能不能用：改走宿主的 copyText）
  await page.click('#dashboard .dash-sec-us .dash-sec-head .btn');
  const copied = await until(() => page.evaluate(() => {
    const t = Array.from(document.querySelectorAll('.toast')).map((x) => x.textContent).join('|');
    return /已複製最近 3 筆紀錄/.test(t) ? t : null;
  }), 1500) || await until(() => host.last('copyText'), 1500);
  check('usageSegCopyCsv', !!copied && (typeof copied === 'string' || /^開始,結束,帳戶/.test(copied.text || '')), copied);
  await closeApp(app);

  // 淺色、窄的視窗：表格在框裡橫向捲動，不撐破儀表板
  const light = await openApp('usageseg-light', { vp: { w: 760, h: 900 }, layout: onePane('c1', 'dashboard'), settings: { theme: 'light' }, colorScheme: 'light' });
  await until(() => light.host.last('usageSegments'), 3000);
  await light.host.send({ type: 'usageSegments', usageSegments: makeSegments() });
  await settle(light.page, 200);
  const narrow = await light.page.evaluate(sectionState);
  check('usageSegNarrowNoPageOverflow', narrow.shown && !narrow.pageOverflow, narrow.pageOverflow);
  await light.page.evaluate(() => document.querySelector('#dashboard .dash-sec-us').scrollIntoView({ block: 'start' }));
  await settle(light.page, 150);
  await light.page.locator('#dashboard .dash-sec-us').screenshot({ path: outFile('usageseg-light-narrow.png') });
  await closeApp(light);
}
