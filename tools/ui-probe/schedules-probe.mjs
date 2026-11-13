// 排程頁（web-protocol 的 schedules／scheduleWatch／scheduleRefresh／scheduleAction／scheduleAgent；Web/schedules.js、Web/schedulerules.js）的情境：
// probe.mjs 匯入，--schedules 只跑它，完整執行時也跑。檢查名稱 sched*：沒有 schedules 時不顯示排程鈕、頂端排程鈕與失敗數、
// 切過去送 scheduleWatch on、預設列出代理建立與疑似的（失敗的排第一、預設選它）、時間軸、篩選、快速操作送 scheduleAction、
// 交給代理（沒有管理對話時選帳戶、有就直接用）送 scheduleAgent 並切到對話頁、輸入框收到預先填好的說明、離開時 scheduleWatch off、
// 排程頁記進版面、明細的連結、設定頁的自動診斷開關、沒有橫向捲動、淺色。截圖 schedules-*.png

const NOW = Date.now();
const MIN = 60e3, HOUR = 3600e3;

const XML = `<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers><CalendarTrigger><StartBoundary>2026-07-30T02:30:00</StartBoundary><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger></Triggers>
  <Actions Context="Author"><Exec><Command>C:\\Python314\\pythonw.exe</Command></Exec></Actions>
</Task>`;

function runs(levels, step = HOUR, from = NOW - 30 * MIN) {
  // levels：新的在前（同宿主的 runs）
  return levels.map((lv, i) => ({ at: from - i * step, code: lv === 'ok' ? 0 : 1, level: lv, text: lv === 'ok' ? '成功' : '結束碼 1', gap: false }));
}

function upcoming(first, step, n) {
  return Array.from({ length: n }, (_, i) => first + i * step);
}

/** 假的 schedules（形狀見 web-protocol 的 ScheduleView） */
export function makeSchedules(o = {}) {
  const base = {
    folder: '\\', busy: false, author: null, description: null, created: null, error: null, xml: XML, missed: 0,
    conditions: ['使用電池時不啟動', '錯過時盡快補跑'], runAs: 'DEMO-PC-3\\User，只在使用者登入時執行', changes: [], autoDiagnose: true,
    diagnoseBlock: null, lastDiagnosis: null, management: null, provenance: null, repoRoot: 'C:\\repo\\sample-project',
  };
  const entries = [
    Object.assign({}, base, {
      path: '\\TkNotesSync', name: 'TkNotesSync', state: 'ready', enabled: true, origin: 'agent', managed: true,
      trigger: '每天 02:30', action: 'C:\\Python314\\pythonw.exe "C:\\repo\\sample-project\\tools\\notes-sync\\run_notes_sync.py"',
      workFolder: 'C:\\repo\\sample-project', lastRun: NOW - 11 * HOUR, last: { code: 1, level: 'fail', text: '結束碼 1' },
      nextRun: NOW + 13 * HOUR, streak: 3, runs: runs(['fail', 'fail', 'fail', 'ok', 'ok', 'ok'], 24 * HOUR, NOW - 11 * HOUR),
      upcoming: [NOW + 13 * HOUR],
      provenance: { source: 'myriagent', verb: 'created', at: NOW - 80 * 24 * HOUR, conversationId: 'c1', title: '筆記庫同步', provider: 'Claude', exists: true },
      changes: [{ at: NOW - 3 * 24 * HOUR, kind: 'changed', text: '修改定義', conversationId: 'c2', title: '排程 TkNotesSync 的管理', provider: 'Claude', exists: true }],
      lastDiagnosis: { at: NOW - 11 * HOUR + MIN, conversationId: 'c2', exists: true },
    }),
    Object.assign({}, base, {
      path: '\\FileMirrorSync', name: 'FileMirrorSync', state: 'running', enabled: true, origin: 'agent', managed: true,
      trigger: '每 15 分鐘（00:05 起）', action: 'C:\\Python314\\pythonw.exe "C:\\repo\\sample-project\\tools\\filemirror\\run_file_mirror.py"',
      workFolder: 'C:\\repo\\sample-project', lastRun: NOW - 3 * MIN, last: { code: 0, level: 'ok', text: '成功' }, nextRun: NOW + 12 * MIN, streak: 0,
      runs: runs(Array(14).fill('ok'), 15 * MIN, NOW - 18 * MIN), upcoming: upcoming(NOW + 12 * MIN, 15 * MIN, 96),
      provenance: { source: 'transcript', verb: 'created', at: NOW - 90 * 24 * HOUR, conversationId: null, title: 'Codex 工作階段・sample-project', provider: 'Codex', exists: false, importable: true, cwd: 'C:\repo\sample-project' },
      management: { conversationId: 'c2', title: '排程 FileMirrorSync 的管理', provider: 'Claude', running: false, isDraft: false },
    }),
    Object.assign({}, base, {
      path: '\\TkReportSync', name: 'TkReportSync', state: 'ready', enabled: true, origin: 'suspected', managed: false,
      trigger: '每小時（00:20 起）', action: 'C:\\Python314\\pythonw.exe "C:\\repo\\sample-project\\tools\\notes-sync\\run_report_sync.py"',
      workFolder: 'C:\\repo\\sample-project', lastRun: NOW - 40 * MIN, last: { code: 0, level: 'ok', text: '成功' }, nextRun: NOW + 20 * MIN, streak: 0,
      runs: runs(Array(10).fill('ok')), upcoming: upcoming(NOW + 20 * MIN, HOUR, 24),
    }),
    Object.assign({}, base, {
      path: '\\Reports\\SalesReportMail', name: 'SalesReportMail', folder: '\\Reports', state: 'disabled', enabled: false, origin: 'suspected', managed: false,
      trigger: '每週一 08:00', action: 'powershell.exe -NoProfile -File "C:\\repo\\SampleShop\\tools\\report.ps1" -Token ••••',
      workFolder: 'C:\\repo\\SampleShop\\tools', lastRun: NOW - 13 * 24 * HOUR, last: { code: 0, level: 'ok', text: '成功' }, nextRun: null, streak: 0, missed: 1,
      runs: runs(['ok', 'ok'], 7 * 24 * HOUR, NOW - 13 * 24 * HOUR), upcoming: [], repoRoot: 'C:\\repo\\SampleShop',
    }),
    Object.assign({}, base, {
      path: '\\GoogleUpdaterTaskUser', name: 'GoogleUpdaterTaskUser', state: 'ready', enabled: true, origin: 'other', managed: false,
      trigger: '每小時（10:00 起）', action: '"C:\\Users\\User\\AppData\\Local\\Google\\GoogleUpdater\\updater.exe" --wake', workFolder: null, repoRoot: null,
      author: 'DEMO-PC-3\\User', lastRun: NOW - 50 * MIN, last: { code: 0, level: 'ok', text: '成功' }, nextRun: NOW + 10 * MIN, streak: 0, runs: [], upcoming: upcoming(NOW + 10 * MIN, HOUR, 24),
    }),
    Object.assign({}, base, {
      path: '\\NoisyVendorTask', name: 'NoisyVendorTask', state: 'ready', enabled: true, origin: 'ignored', managed: false, trigger: '登入時', action: 'C:\\Program Files\\Vendor\\x.exe',
      workFolder: null, repoRoot: null, lastRun: null, last: null, nextRun: null, streak: 0, runs: [], upcoming: [],
    }),
  ];
  const timed = [
    {
      id: 'tm_daily01', label: '整理昨天的訂單異常', text: '整理昨天的訂單異常，寫成報表', when: '平日 09:00', cron: '0 9 * * 1-5', at: null, enabled: true, done: false,
      newConversation: true, conversationId: 'c1', conversationTitle: '訂單異常日報', conversationExists: true, provider: 'Codex', folder: 'C:\\repo\\SampleShop',
      nextAt: NOW + 3 * HOUR, upcoming: [NOW + 3 * HOUR], createdAt: NOW - 5 * 24 * HOUR,
      createdBy: { conversationId: 'c1', title: '訂單異常日報', provider: 'Codex', exists: true },
      last: { level: 'ok', text: '完成', at: NOW - 21 * HOUR },
      runs: [{ at: NOW - 21 * HOUR, scheduledAt: NOW - 21 * HOUR, late: false, conversationId: 'c2', exists: true, level: 'ok', text: '完成' }],
    },
    {
      id: 'tm_once0001', label: '檢查部署結果', text: '檢查部署結果', when: '一次：2026/10/27 14:00', cron: null, at: NOW + 8 * HOUR, enabled: false, done: false,
      newConversation: false, conversationId: 'c1', conversationTitle: '部署', conversationExists: true, provider: 'Claude', folder: null,
      nextAt: null, upcoming: [], createdAt: NOW - HOUR, createdBy: null, last: null, runs: [],
    },
  ];
  return Object.assign({
    machine: 'DEMO-PC-3', loaded: true, scanning: false, scannedAt: NOW - 20e3, error: null, entries, timed, failing: 1, autoDiagnose: true, accountId: null,
    // 其他電腦最後回報的（S3）：名稱由 remote.devices 對
    devices: o.devices !== undefined ? o.devices : [{
      deviceId: 'd_pc1', updatedAt: NOW - 60e3,
      snapshot: {
        machine: 'DEMO-PC-1', scannedAt: NOW - 60e3, error: null, failing: 1, otherCount: 12,
        entries: [Object.assign({}, base, {
          path: '\\ErpNightlyExport', name: 'ErpNightlyExport', state: 'ready', enabled: true, origin: 'agent', managed: true, trigger: '每天 03:00',
          action: 'C:\\repo\\SampleShop\\tools\\export.cmd --token ••••', workFolder: 'C:\\repo\\SampleShop\\tools', lastRun: NOW - 7 * HOUR,
          last: { code: 1, level: 'fail', text: '結束碼 1' }, nextRun: NOW + 17 * HOUR, streak: 1, runs: runs(['fail', 'ok', 'ok'], 24 * HOUR, NOW - 7 * HOUR),
          upcoming: [NOW + 17 * HOUR], provenance: { source: 'myriagent', verb: 'created', at: NOW - 9 * 24 * HOUR, conversationId: 'cx', title: 'ERP 匯出排程', provider: 'Antigravity' },
        })],
        // 那台的定時訊息（2026-11-13 起其他電腦也能操作與編輯）：摘要的文字截短過（textPartial）
        timed: [{
          id: 'tm_pc1erp1', label: 'ERP 報表', text: '整理 ERP 昨天的訂單…', textPartial: true, when: '每天 09:00', cron: '0 9 * * *', at: null, enabled: true, done: false,
          newConversation: false, conversationId: 'rc1', conversationTitle: 'ERP 報表對話', conversationExists: true, provider: 'Claude', nextAt: NOW + HOUR,
          upcoming: [NOW + HOUR], createdAt: NOW - 3 * 24 * HOUR, last: null, runs: [],
        }],
      },
    }],
  }, o);
}

/** 頁面狀態：顯示中、各列（名稱、燈號、選取）、明細標題、篩選的開關、時間軸的點、橫向捲動 */
function pageState() {
  const el = document.getElementById('schedules');
  if (!el) return null;
  const rows = Array.from(el.querySelectorAll('.sch-row:not(.sch-th)'));
  const main = el.querySelector('.sch-main');
  return {
    shown: !el.hidden && el.getBoundingClientRect().width > 0,
    rows: rows.map((r) => ({ path: r.dataset.path, led: r.dataset.led, sel: r.classList.contains('sel') })),
    detail: (el.querySelector('.sch-d-head h2') || {}).textContent || null,
    chips: Array.from(el.querySelectorAll('.sch-chip')).map((c) => [c.textContent, c.classList.contains('on')]),
    dots: el.querySelectorAll('.sch-track .dot').length,
    bands: el.querySelectorAll('.sch-track .band').length,
    pills: (el.querySelector('.sch-pills') || {}).textContent || '',
    overflowX: main ? main.scrollWidth > main.clientWidth + 1 : null,
    agentTasks: Array.from(el.querySelectorAll('.sch-ab-tasks button')).map((b) => b.textContent),
    links: Array.from(el.querySelectorAll('.sch-ab-links .sch-link')).map((b) => b.textContent),
    sidebarHidden: document.getElementById('sidebar').hidden,
    workspaceHidden: document.getElementById('workspace').hidden,
  };
}

function switchState() {
  const b = document.querySelector('#viewSwitch [data-view="schedules"]');
  const badge = document.getElementById('schedBadge');
  return b ? { hidden: b.hidden, on: b.classList.contains('on'), badge: badge && !badge.hidden ? badge.textContent : null, title: b.title } : null;
}

export async function runSchedules(ctx, vp) {
  const { openApp, closeApp, check, until, settle, outFile } = ctx;
  const onePane = (id, view) => ({ panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: false, view });

  // 宿主沒有排程讀取器（init 沒有 schedules）：不顯示排程鈕
  {
    const app = await openApp('sched-none', { vp, layout: onePane('c1', 'chat') });
    check('schedHiddenWithoutHost', (await app.page.evaluate(switchState))?.hidden === true, await app.page.evaluate(switchState));
    await closeApp(app);
  }

  const remote = { mode: 'desktop', deviceId: 'd_self', paired: true, devices: [
    { id: 'd_self', name: 'DEMO-PC-3', online: true, self: true },
    { id: 'd_pc1', name: 'DEMO-PC-1', online: true, remoteControlLevel: 'Control' },
  ] };
  const app = await openApp('schedules', {
    vp, layout: onePane('c1', 'chat'), schedules: makeSchedules({ devices: [] }),
    hostSetup: (host) => { host.settings = Object.assign({}, host.settings, { scheduleAutoDiagnose: true }); },
  });
  const { page, host } = app;
  const sw = await page.evaluate(switchState);
  check('schedSwitchShownWithBadge', !!sw && !sw.hidden && sw.badge === '1' && /1 個代理排程/.test(sw.title), sw);

  // 切到排程頁：送 scheduleWatch on、側欄與對話區藏起來
  await page.click('#viewSwitch [data-view="schedules"]');
  const watch = await until(() => host.last('scheduleWatch'), 3000);
  check('schedWatchOnWhenShown', !!watch && watch.on === true, watch);
  await settle(page);
  let st = await page.evaluate(pageState);
  check('schedPageShown', st.shown && st.sidebarHidden && st.workspaceHidden, st);
  check('schedDefaultListsAgentAndSuspected', st.rows.map((r) => r.path).join(',') === '\\TkNotesSync,\\FileMirrorSync,\\TkReportSync,\\Reports\\SalesReportMail,tm_daily01,tm_once0001', st.rows);
  check('schedFailingFirstAndSelected', st.rows[0].led === 'fail' && st.rows[0].sel && st.detail === 'TkNotesSync', { rows: st.rows, detail: st.detail });
  check('schedLeds', st.rows[1].led === 'run' && st.rows[2].led === 'ok' && st.rows[3].led === 'dis', st.rows);
  check('schedTimelineDotsAndBand', st.dots >= 2 && st.bands === 1, { dots: st.dots, bands: st.bands });
  check('schedPillsSummary', /4個列出的排程/.test(st.pills.replace(/\s/g, '')) && /1執行中/.test(st.pills.replace(/\s/g, '')) && /1最近一次失敗/.test(st.pills.replace(/\s/g, '')), st.pills);
  check('schedDetailAgentTasks', st.agentTasks.length === 5 && /診斷最近一次失敗/.test(st.agentTasks[0]), st.agentTasks);
  check('schedDetailLinks', st.links.some((t) => /建立它的對話：筆記庫同步/.test(t)), st.links);
  check('schedNoHorizontalScroll', st.overflowX === false, st.overflowX);
  check('schedTitle', (await page.title()) === 'Clode Myriagent － 排程', await page.title());
  const layout = await until(() => host.last('layout', (m) => m.layout && m.layout.view === 'schedules'), 3000);
  check('schedViewSavedInLayout', !!layout, layout && layout.layout);
  await page.screenshot({ path: outFile('schedules-dark.png') });

  // 篩選：加上「其他」多一列，關掉「疑似代理」少兩列，關掉「Myriagent 定時訊息」少兩列
  await page.click('.sch-chip:has-text("其他")');
  st = await page.evaluate(pageState);
  check('schedFilterOther', st.rows.length === 7 && st.rows.some((r) => r.path === '\\GoogleUpdaterTaskUser'), st.rows);
  await page.click('.sch-chip:has-text("疑似代理")');
  st = await page.evaluate(pageState);
  check('schedFilterSuspectedOff', st.rows.length === 5 && !st.rows.some((r) => r.path === '\\TkReportSync'), st.rows);
  await page.click('.sch-chip:has-text("Myriagent 定時訊息")');
  st = await page.evaluate(pageState);
  check('schedFilterTimedOff', st.rows.length === 3 && !st.rows.some((r) => r.path.startsWith('tm_')), st.rows);
  await page.click('.sch-chip:has-text("Myriagent 定時訊息")');
  await page.click('.sch-chip:has-text("疑似代理")');
  await page.click('.sch-chip:has-text("其他")');
  await page.click('.sch-chip:has-text("失敗")');
  st = await page.evaluate(pageState);
  check('schedFilterFailing', st.rows.filter((r) => !r.path.startsWith('tm_')).length === 1 && st.rows[0].path === '\\TkNotesSync', st.rows);
  await page.click('.sch-chip:has-text("全部")');

  // 快速操作：列上的「立即執行」、明細的「停用」
  await page.click('.sch-row[data-path="\\\\TkReportSync"] .sch-q');
  const run = await until(() => host.last('scheduleAction', (m) => m.action === 'run'), 3000);
  check('schedQuickRun', !!run && run.path === '\\TkReportSync', run);
  await page.click('.sch-row[data-path="\\\\TkReportSync"]');
  st = await page.evaluate(pageState);
  check('schedSelectRow', st.detail === 'TkReportSync', st.detail);
  await page.click('.sch-quick button:has-text("停用")');
  const dis = await until(() => host.last('scheduleAction', (m) => m.action === 'disable'), 3000);
  check('schedQuickDisable', !!dis && dis.path === '\\TkReportSync', dis);

  // 出處是外部的工作階段：明細有「匯入成對話」，按了送 importOrigin 並切到對話頁
  await page.click('.sch-row[data-path="\\\\FileMirrorSync"]');
  const importBtn = await page.$('.sch-chg button:has-text("匯入成對話")');
  check('schedImportButton', !!importBtn, null);
  if (importBtn) {
    await importBtn.click();
    const imp = await until(() => host.last('scheduleAction', (m) => m.action === 'importOrigin'), 3000);
    check('schedImportOrigin', !!imp && imp.path === '\\FileMirrorSync', imp);
    await page.click('#viewSwitch [data-view="schedules"]');
    await settle(page);
  }
  await page.click('.sch-row[data-path="\\\\TkReportSync"]');

  // 第九批（2026-10-26）：同其他頁的 HUD（點狀網格、角框、摘要的刻度尺、標題的菱形）、時間軸的刻度尺（24 個整點、4 個粗刻度）、
  // 「現在」的時間、一小時內最近的那次（TkReportSync 約 20 分後；FileMirrorSync 每 15 分鐘是帶狀，不算）標琥珀並寫「N 分後」、清單的「下次」標琥珀。
  // 假資料的時間在模組載入時就定了，完整執行時跑到這裡可能已過了好幾分鐘，「N 分後」只要求 1～20
  const hud = await page.evaluate(() => {
    const el = document.getElementById('schedules');
    const after = (sel) => { const x = el.querySelector(sel); return x ? getComputedStyle(x, '::after').borderTopStyle : null; };
    const soonRows = Array.from(el.querySelectorAll('.sch-row .sch-next.soon')).map((n) => n.closest('.sch-row').dataset.path);
    return {
      grid: getComputedStyle(el.querySelector('.sch-main')).backgroundImage.includes('radial-gradient'),
      brackets: [after('.sch-tl'), after('.sch-table'), after('.sch-detail')],
      ruler: getComputedStyle(el.querySelector('.sch-pills')).backgroundImage.includes('repeating-linear-gradient'),
      diamond: getComputedStyle(el.querySelector('.sch-head h1'), '::before').content !== 'none',
      hours: el.querySelectorAll('.sch-hours > i').length, majors: el.querySelectorAll('.sch-hours > i.major').length,
      now: (el.querySelector('.sch-tl-now') || {}).textContent || null,
      soonDot: (el.querySelector('.sch-track .dot.soon') || {}).title || null,
      soonTag: (el.querySelector('.sch-soon-tag') || {}).textContent || null,
      soonRows,
    };
  });
  check('schedFxHud', hud.grid && hud.brackets.every((b) => b === 'solid') && hud.ruler && hud.diamond, hud);
  check('schedFxTimeline', hud.hours === 24 && hud.majors === 4 && /^・現在 \d\d:\d\d$/.test(hud.now || '') && /^TkReportSync：/.test(hud.soonDot || '')
    && /^([1-9]|1\d|20) 分後$/.test(hud.soonTag || '') && hud.soonRows.includes('\\TkReportSync') && hud.soonRows.includes('\\FileMirrorSync') && !hud.soonRows.includes('\\TkNotesSync'), hud);

  // 第九批：解碼與彈一下都不到一秒就播完，先掛觀察器記下有沒有發生過
  await page.evaluate(() => {
    window.__fx9 = { scr: 0, pop: false };
    new MutationObserver(() => {
      if (document.querySelector('#schedules .sch-pills .mtok-c.scr')) window.__fx9.scr++;
      if (document.getElementById('schedBadge').classList.contains('fx-pop-badge')) window.__fx9.pop = true;
    }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['class'], characterData: true });
  });
  // 宿主更新（例如執行中）：保留選取、重畫
  const updated = makeSchedules();
  updated.entries[2] = Object.assign({}, updated.entries[2], { state: 'running' });
  await host.send({ type: 'schedules', schedules: updated });
  await settle(page);
  st = await page.evaluate(pageState);
  check('schedKeepsSelectionOnUpdate', st.detail === 'TkReportSync' && st.rows.find((r) => r.path === '\\TkReportSync').led === 'run', st);

  // 第九批：這次更新執行中從 1 變 2——摘要的數字逐字解碼（閃過亂碼）；其他電腦回報的失敗讓頂端排程鈕的數字從 1 變 2——彈一下；
  // 執行中的那幾列（TkReportSync、FileMirrorSync）燈一明一暗、左緣一道光往下掃（狀態指示），其他列沒有
  const fx9 = await until(() => page.evaluate(() => (window.__fx9.scr && window.__fx9.pop ? window.__fx9 : null)), 1500, 20);
  const running = await page.evaluate(() => Array.from(document.querySelectorAll('#schedules .sch-row:not(.sch-th)')).map((r) => ({
    path: r.dataset.path,
    names: r.getAnimations({ subtree: true }).filter((a) => a.effect && a.effect.getComputedTiming().iterations === Infinity).map((a) => a.animationName).sort().join(','),
  })));
  check('schedFxPillsDecodeBadgePop', !!fx9, await page.evaluate(() => window.__fx9));
  check('schedFxRunningRows', running.filter((r) => r.names === 'fx-breathe,fx-sch-scan').map((r) => r.path).sort().join(',') === '\\FileMirrorSync,\\TkReportSync'
    && running.filter((r) => r.names && r.names !== 'fx-breathe,fx-sch-scan').length === 0, running);

  // 第九批：TkNotesSync 有了新的一次執行（又失敗）——那一列亮一下紅、最近 14 次的長條最右邊那格長出來；TkReportSync 沒有新的一次，不閃
  const again = makeSchedules();
  again.entries[2] = Object.assign({}, again.entries[2], { state: 'running' });
  again.entries[0] = Object.assign({}, again.entries[0], { lastRun: NOW + 5e3, runs: [{ at: NOW + 5e3, code: 1, level: 'fail', text: '結束碼 1', gap: false }].concat(again.entries[0].runs) });
  await host.send({ type: 'schedules', schedules: again });
  const freshRow = await until(() => page.evaluate(() => {
    const r = document.querySelector('#schedules .sch-row[data-path="\\\\TkNotesSync"]');
    return r && r.classList.contains('fx-sch-fail') ? {
      cls: r.className, newBar: !!r.querySelector('.sch-hist > i:last-child.fx-new'),
      flash: r.getAnimations({ subtree: true }).map((a) => a.animationName).filter((n) => n === 'fx-glow-out' || n === 'fx-ah-cell'),
      others: Array.from(document.querySelectorAll('#schedules .sch-row.fx-sch-ok, #schedules .sch-row.fx-sch-fail')).map((x) => x.dataset.path),
    } : null;
  }), 2000, 20);
  check('schedFxFreshRun', !!freshRow && freshRow.newBar && freshRow.flash.includes('fx-glow-out') && freshRow.flash.includes('fx-ah-cell')
    && freshRow.others.join(',') === '\\TkNotesSync', freshRow);
  await page.screenshot({ path: outFile('schedules-fx9-dark.png') });

  // 交給代理（沒有管理對話）：選帳戶 → scheduleAgent，切到對話頁；宿主回一個帶 draft 的草稿
  await page.click('.sch-row[data-path="\\\\TkNotesSync"]');
  await page.click('.sch-ab-tasks button:has-text("診斷最近一次失敗")');
  await page.waitForSelector('.menu [role="menuitem"]', { timeout: 3000 });
  await page.click('.menu [role="menuitem"] >> nth=0');
  const ask = await until(() => host.last('scheduleAgent'), 3000);
  check('schedAgentCommand', !!ask && ask.path === '\\TkNotesSync' && ask.task === 'diagnose' && !!ask.accountId && !!ask.paneId, ask);
  const unwatch = await until(() => host.last('scheduleWatch', (m) => m.on === false), 3000);
  check('schedWatchOffWhenLeaving', !!unwatch, unwatch);
  const view = await page.evaluate(() => ({ chat: !document.getElementById('workspace').hidden, sched: !document.getElementById('schedules').hidden }));
  check('schedAgentSwitchesToChat', view.chat && !view.sched, view);
  host.data.dsched = {
    header: Object.assign({}, host.conv('c1').header, { conversationId: 'dsched', isDraft: true, title: '排程 TkNotesSync 的管理' }),
    items: [], hasMore: false, composer: { attachments: [], busy: false, busyText: null, queue: [] },
  };
  await host.send({ type: 'conversation', conversationId: 'dsched', paneId: ask.paneId, header: host.data.dsched.header, items: [], hasMore: false, composer: host.data.dsched.composer, draft: '這是 Clode Myriagent 排程頁交給你的工作…' });
  const draftText = await until(() => page.evaluate(() => {
    const t = document.querySelector('.conv[data-conv="dsched"] textarea');
    return t && t.value ? t.value : null;
  }), 3000);
  check('schedAgentDraftPrefilled', !!draftText && draftText.startsWith('這是 Clode Myriagent 排程頁'), draftText);

  // 有管理對話的排程：直接用它（不選帳戶），帶 conversationId 所在的窗格
  await page.click('#viewSwitch [data-view="schedules"]');
  await settle(page);
  await page.click('.sch-row[data-path="\\\\FileMirrorSync"]');
  host.received.length = 0;
  await page.click('.sch-ab-tasks button:has-text("說明它在做什麼")');
  const ask2 = await until(() => host.last('scheduleAgent'), 3000);
  check('schedAgentUsesManagementConversation', !!ask2 && ask2.task === 'explain' && ask2.accountId === null && ask2.path === '\\FileMirrorSync', ask2);

  // 新增排程（交給代理）
  await page.click('#viewSwitch [data-view="schedules"]');
  await settle(page);
  host.received.length = 0;
  await page.click('.sch-head button:has-text("新增排程")');
  const menu = await page.$('.menu [role="menuitem"]');
  if (menu) await menu.click();
  const ask3 = await until(() => host.last('scheduleAgent'), 3000);
  check('schedCreateCommand', !!ask3 && ask3.task === 'create' && ask3.path === null, ask3);

  // 定時訊息：明細、立即送一次、暫停的那則顯示「已暫停」、新增的對話框送 timedSave
  await page.click('#viewSwitch [data-view="schedules"]');
  await settle(page);
  await page.click('.sch-row[data-path="tm_daily01"]');
  st = await page.evaluate(pageState);
  check('schedTimedDetail', st.detail === '整理昨天的訂單異常', st.detail);
  const timedTarget = await page.evaluate(() => document.querySelector('.sch-detail .sch-kv')?.textContent || '');
  check('schedTimedDetailTarget', /每次開新對話/.test(timedTarget) && /代理以 myriagent-schedule 建立/.test(timedTarget), timedTarget);
  host.received.length = 0;
  await page.click('.sch-quick button:has-text("立即送一次")');
  const fire = await until(() => host.last('timedAction'), 3000);
  check('schedTimedRunNow', !!fire && fire.id === 'tm_daily01' && fire.action === 'run', fire);
  const paused = await page.evaluate(() => document.querySelector('.sch-row[data-path="tm_once0001"] .sch-when')?.textContent || '');
  check('schedTimedPausedText', /已暫停/.test(paused), paused);
  await page.click('.sch-head button:has-text("新增定時訊息")');
  await page.waitForSelector('.dlg-timed textarea', { timeout: 3000 });
  await page.fill('.dlg-timed textarea', '每天看一下佇列');
  await page.selectOption('.dlg-timed .sch-when-row select', 'every');
  await page.fill('.dlg-timed .sch-when-row input', '30m');
  await page.click('.dlg-timed .dlg-foot button:has-text("儲存")');
  const saved = await until(() => host.last('timedSave'), 3000);
  check('schedTimedSave', !!saved && saved.text === '每天看一下佇列' && saved.when && saved.when.kind === 'every' && saved.when.value === '30m' && !!saved.conversationId, saved);
  await page.screenshot({ path: outFile('schedules-timed-dark.png') });

  // 設定頁：自動診斷的開關
  await page.click('#viewSwitch [data-view="schedules"]');
  await page.keyboard.press('Control+Comma');
  const row = await until(() => page.evaluate(() => !!document.querySelector('[data-setting="scheduleAutoDiagnose"]')), 3000);
  check('schedSettingRow', row === true, row);
  await page.keyboard.press('Escape');
  await closeApp(app);

  // 其他電腦（S3）：另開一頁，這台加上 DEMO-PC-1 最後回報的
  {
    const ra = await openApp('schedules-remote', {
      vp, layout: onePane('c1', 'schedules'), schedules: makeSchedules(), remote,
      // 那台的對話：定時訊息的編輯器從這裡挑
      hostSetup: (h) => h.conversations.push({ id: 'rc1', deviceId: 'd_pc1', accountId: 'a1', provider: 'Claude', title: 'ERP 報表對話', updatedAt: NOW - HOUR,
        workingDirectory: 'C:\\repo', customFolder: true, running: false, waiting: false, unread: false }),
    });
    const page = ra.page, host = ra.host;
    let st;
    await until(() => host.last('scheduleWatch'), 3000);
    await settle(page);
    const rsw = await page.evaluate(switchState);
    check('schedRemoteBadgeSums', !!rsw && rsw.badge === '2', rsw);
  const remoteRow = await page.$('.sch-row[data-path="d_pc1::\\\\ErpNightlyExport"]');
    check('schedRemoteGroup', !!remoteRow && /DEMO-PC-1/.test(await page.evaluate(() => Array.from(document.querySelectorAll('.sch-gh b')).map((b) => b.textContent).join(','))), null);
    host.received.length = 0;
    await page.click('.sch-row[data-path="d_pc1::\\\\ErpNightlyExport"] .sch-q');
    const remoteRun = await until(() => host.last('scheduleAction'), 3000);
    check('schedRemoteRun', !!remoteRun && remoteRun.deviceId === 'd_pc1' && remoteRun.path === '\\ErpNightlyExport' && remoteRun.action === 'run' && typeof remoteRun.commandId === 'string', remoteRun);
    await page.click('.sch-row[data-path="d_pc1::\\\\ErpNightlyExport"]');
    st = await page.evaluate(pageState);
    check('schedRemoteDetail', st.detail === 'ErpNightlyExport' && !(await page.$('.sch-quick .sch-more')), st.detail);
    await page.click('.sch-chip:has-text("DEMO-PC-1")');
    st = await page.evaluate(pageState);
    check('schedDeviceFilter', st.rows.length > 0 && st.rows.every((r) => r.path.startsWith('d_pc1::')), st.rows);
    await page.click('.sch-chip:has-text("全部") >> nth=0');
    host.received.length = 0;
    await page.click('.sch-ab-tasks button:has-text("診斷最近一次失敗")');
    await page.waitForSelector('.dlg-timed textarea', { timeout: 3000 });
    await page.fill('.dlg-timed textarea', '順便看記錄檔');
    await page.click('.dlg-timed .dlg-foot button:has-text("送出")');
    const remoteAgent = await until(() => host.last('scheduleAgent'), 3000);
    check('schedRemoteAgent', !!remoteAgent && remoteAgent.deviceId === 'd_pc1' && remoteAgent.task === 'diagnose' && remoteAgent.note === '順便看記錄檔', remoteAgent);
    await page.screenshot({ path: outFile('schedules-remote-dark.png') });

    // 其他電腦的定時訊息（2026-11-13）：列上的立即送一次帶 deviceId；明細的編輯改時間、沒改文字就不帶 text；新增時可以選那台
    host.received.length = 0;
    await page.click('.sch-row[data-path="d_pc1::tm_pc1erp1"] .sch-q >> nth=0');
    const remoteFire = await until(() => host.last('timedAction'), 3000);
    check('schedRemoteTimedRun', !!remoteFire && remoteFire.deviceId === 'd_pc1' && remoteFire.id === 'tm_pc1erp1' && remoteFire.action === 'run' && typeof remoteFire.commandId === 'string', remoteFire);
    await page.click('.sch-row[data-path="d_pc1::tm_pc1erp1"]');
    await page.click('.sch-quick button:has-text("編輯")');
    await page.waitForSelector('.dlg-timed textarea', { timeout: 3000 });
    const editForm = await page.evaluate(() => ({
      machine: document.querySelector('.dlg-timed select')?.selectedOptions[0]?.textContent || null,
      machineLocked: !!document.querySelector('.dlg-timed select')?.disabled,
      conv: Array.from(document.querySelectorAll('.dlg-timed select'))[1]?.value || null,
      note: Array.from(document.querySelectorAll('.dlg-timed .sch-sub')).map((n) => n.textContent).join('|'),
    }));
    await page.fill('.dlg-timed .sch-when-row input', '10:30');
    host.received.length = 0;
    await page.click('.dlg-timed .dlg-foot button:has-text("儲存")');
    const remoteSave = await until(() => host.last('timedSave'), 3000);
    check('schedRemoteTimedEdit', !!remoteSave && remoteSave.deviceId === 'd_pc1' && remoteSave.id === 'tm_pc1erp1' && remoteSave.conversation === 'rc1'
      && remoteSave.whenKind === 'daily' && remoteSave.whenValue === '10:30' && !('text' in remoteSave) && !('when' in remoteSave) && typeof remoteSave.commandId === 'string'
      && editForm.machine === 'DEMO-PC-1' && editForm.machineLocked && editForm.conv === 'rc1' && /保留那台的原文/.test(editForm.note), { remoteSave, editForm });
    await page.waitForSelector('.dlg-timed', { state: 'detached', timeout: 3000 }).catch(() => {});
    await page.click('.sch-head button:has-text("新增定時訊息")');
    await page.waitForSelector('.dlg-timed textarea', { timeout: 3000 });
    const machines = await page.evaluate(() => Array.from(document.querySelector('.dlg-timed select').options).map((o) => o.textContent));
    await page.selectOption('.dlg-timed select >> nth=0', { label: 'DEMO-PC-1' });
    const convOpts = await page.evaluate(() => Array.from(document.querySelectorAll('.dlg-timed select')[1].options).map((o) => o.value));
    await page.fill('.dlg-timed textarea', '每天備份 ERP');
    host.received.length = 0;
    await page.click('.dlg-timed .dlg-foot button:has-text("儲存")');
    const remoteNew = await until(() => host.last('timedSave'), 3000);
    check('schedRemoteTimedCreate', JSON.stringify(machines) === JSON.stringify(['這台', 'DEMO-PC-1']) && JSON.stringify(convOpts) === JSON.stringify(['rc1'])
      && !!remoteNew && remoteNew.deviceId === 'd_pc1' && remoteNew.id === null && remoteNew.text === '每天備份 ERP' && remoteNew.conversation === 'rc1',
    { machines, convOpts, remoteNew });

    await closeApp(ra);
  }

  // 淺色、開頁就在排程頁（版面記的）
  const light = await openApp('schedules-light', { vp, colorScheme: 'light', settings: { theme: 'light' }, layout: onePane('c1', 'schedules'), schedules: makeSchedules({ devices: [] }) });
  const w = await until(() => light.host.last('scheduleWatch'), 3000);
  check('schedRestoredFromLayout', !!w && w.on === true, w);
  await settle(light.page, 800); // 主題換成淺色的過場結束才拍
  const lst = await light.page.evaluate(pageState);
  check('schedLightShown', lst.shown && lst.rows.length === 6, lst.rows);
  await light.page.screenshot({ path: outFile('schedules-light.png') });
  await closeApp(light);
}
