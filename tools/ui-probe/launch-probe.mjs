// 執行列的情境（launchStatus／launchLog／launchAction…；Web/launchbar.js、launchrules.js）：probe.mjs 匯入，--launch 只跑它，完整執行時也跑。
// 假宿主不建置、不執行：探針自己送 launchStatus，launchLog 由 hostSetup 掛的 onCmd 回。檢查名稱 launch*，截圖 launch-*.png
const TARGETS = [
  { id: 't1', name: 'Myriagent', kind: 'dotnet', path: 'src\\Myriagent\\Myriagent.csproj', detail: 'WinForms · net10.0-windows', phase: 'idle' },
  { id: 't2', name: 'fake-device', kind: 'dotnet', path: 'tools\\fake-device\\FakeDevice.csproj', detail: 'WinExe · net10.0', phase: 'idle' },
];
/** 工作資料夾的 git 晶片（gitStatus）：執行列排在它後面 */
const GIT = {
  root: 'C:\\repo\\myriagent', branch: 'main', head: '19fb6c2', upstream: 'origin/main', upstreamGone: false, ahead: 2, behind: 0,
  base: null, baseAhead: null, baseBehind: null, changed: 3, staged: 0, unstaged: 3, untracked: 0, conflicts: 0, stashes: 0,
  operation: null, remote: 'origin', remoteNew: false, remoteCheckedAt: Date.now() - 60000, checkedAt: Date.now() - 5000,
};
const view = (o = {}) => Object.assign({ folder: 'C:\\repo\\myriagent', selected: 't1', targets: TARGETS, phase: 'idle', mode: 'debug', runMode: null, since: null, last: null, logSeq: 0, logEpoch: 0 }, o);

const LOG_LINES = [
  '> dotnet build C:\\repo\\myriagent\\src\\Myriagent\\Myriagent.csproj -c Debug -nologo -tl:off -nodeReuse:false',
  '  正在判斷要還原的專案...',
  '  所有專案都在最新狀態，可進行還原。',
  'C:\\repo\\myriagent\\src\\Myriagent\\UI\\MainForm.cs(42,13): warning CS0168: 已宣告變數 \'ex\'，但從未使用過 [C:\\repo\\myriagent\\src\\Myriagent\\Myriagent.csproj]',
  'C:\\repo\\myriagent\\src\\Myriagent\\UI\\MainForm.cs(57,30): error CS1002: 必須是 ; [C:\\repo\\myriagent\\src\\Myriagent\\Myriagent.csproj]',
  'C:\\repo\\myriagent\\src\\Myriagent\\UI\\MainForm.cs(88,9): error CS0103: 名稱 \'Foo\' 不存在於目前的內容中 [C:\\repo\\myriagent\\src\\Myriagent\\Myriagent.csproj]',
  '',
  '建置失敗。',
  '<img src=x onerror=alert(1)>',
];

/**
 * 執行列的狀態：看不看得到、在標題列的哪裡（第二行、git 晶片後面、標題列沒有長高、不蓋到標題列的按鈕）、按鈕能不能按、名稱、狀態文字，
 * 輸出面板（在輸入區最上方）
 */
function launchState(sel) {
  const root = document.querySelector(sel);
  const bar = root && root.querySelector('.lbar');
  if (!bar) return null;
  const vis = (el) => { if (!el) return false; const cs = getComputedStyle(el); const r = el.getBoundingClientRect(); return cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0; };
  const head = root.querySelector('.ch');
  const sub = root.querySelector('.ch-sub');
  const git = root.querySelector('.ch-git');
  const dock = root.querySelector('.dock');
  const box = root.querySelector('.cbox');
  const br = bar.getBoundingClientRect();
  const btn = (n) => { const b = bar.querySelector(`.lbtn[data-act="${n}"]`); return { enabled: !b.disabled, title: b.title, action: b.dataset.action, color: getComputedStyle(b).color }; };
  const out = root.querySelector('.lout');
  const pane = root.getBoundingClientRect();
  const mid = (r) => (r.top + r.bottom) / 2;
  const gr = vis(git) ? git.getBoundingClientRect() : null;
  const apart = (a, b) => b.width === 0 || a.width === 0 || b.bottom <= a.top + 0.5 || b.top >= a.bottom - 0.5 || b.left >= a.right - 0.5 || b.right <= a.left + 0.5;
  const parts = Array.from(bar.children).filter(vis).map((c) => c.getBoundingClientRect());
  // 執行列所在那一行的內容左右緣：兩行時是第二行（.ch-sub），擠回一行時 .ch-sub 是 display: contents、看 .ch
  const rowEl = sub && getComputedStyle(sub).display !== 'contents' ? sub : head;
  const rowBox = rowEl.getBoundingClientRect();
  const rowLeft = rowBox.left + parseFloat(getComputedStyle(rowEl).paddingLeft);
  const rowRight = rowBox.right - parseFloat(getComputedStyle(rowEl).paddingRight);
  return {
    shown: vis(bar),
    // DOM：標題列第二行，緊接在晶片（帳戶、資料夾、git）後面
    inSub: !!sub && bar.parentElement === sub && !!bar.previousElementSibling && bar.previousElementSibling.classList.contains('ch-chips'),
    // 版面：git 晶片看得到時排在它右邊、同一列（垂直置中對齊）；或是自己一行（在晶片下面、從那一行的左邊開始、占滿那一行）
    afterGit: gr ? br.left >= gr.right - 0.5 && Math.abs(mid(br) - mid(gr)) <= 1 : null,
    ownRow: !!gr && br.top >= gr.bottom - 0.5 && Math.abs(br.left - rowLeft) <= 1 && Math.abs(br.right - rowRight) <= 1,
    paneWidth: Math.round(pane.width),
    // 不蓋到晶片與標題
    chipsClear: Array.from(root.querySelectorAll('.ch-chips > .chip, .ch-title')).filter(vis).every((c) => apart(br, c.getBoundingClientRect())),
    headHeight: Math.round(head.getBoundingClientRect().height),
    // 第一行（擠回一行時）：標題、晶片、右邊的按鈕都在同一行（中線對齊），沒有被執行列擠到下一行
    lineOne: (() => {
      const ms = Array.from(head.querySelectorAll('.ch-title, .ch-chips, .ch-top > .ibtn')).filter(vis).map((e) => mid(e.getBoundingClientRect()));
      return ms.length > 0 && Math.max(...ms) - Math.min(...ms) <= 2;
    })(),
    shortHead: root.classList.contains('short-head'),
    // 不蓋到標題列的按鈕（擠回一行時也一樣）、各部分互不重疊
    clearOfButtons: Array.from(head.querySelectorAll('.ch-top > .ibtn')).every((b) => apart(br, b.getBoundingClientRect())),
    partsApart: parts.every((a, i) => parts.every((b, j) => i === j || apart(a, b))),
    inPane: br.left >= pane.left - 0.5 && br.right <= pane.right + 0.5,
    rowOverflow: bar.scrollWidth > bar.clientWidth + 1 || rowEl.scrollWidth > rowEl.clientWidth + 1,
    rowHeight: Math.round(br.height),
    // 「輸出」鈕和第二行的內容右緣的距離（兩行時靠右）
    outGap: Math.round(rowRight - bar.querySelector('.lbar-out').getBoundingClientRect().right),
    // 輸出面板：在輸入區最上方、輸入框之上
    // 浮在輸入區上方的（跳到最新、一輪的結算單）不算
    outFirstInDock: !!dock && Array.from(dock.children).filter((c) => !c.classList.contains('jump') && !c.classList.contains('turn-sum')).indexOf(out) === 0,
    outAboveBox: !!box && out.getBoundingClientRect().bottom <= box.getBoundingClientRect().top + 0.5,
    start: btn('start'), pause: btn('pause'), stop: btn('stop'), restart: btn('restart'),
    name: bar.querySelector('.lbar-name').textContent, nameShown: vis(bar.querySelector('.lbar-name')),
    targetTitle: bar.querySelector('.lbar-target').title,
    mode: bar.querySelector('.lbar-mode-full').textContent, modeShort: vis(bar.querySelector('.lbar-mode-short')) ? bar.querySelector('.lbar-mode-short').textContent : null,
    modeShown: vis(bar.querySelector('.lbar-mode')),
    modeTitle: bar.querySelector('.lbar-mode').title, modeColor: getComputedStyle(bar.querySelector('.lbar-mode')).color,
    status: bar.querySelector('.lbar-status-text').textContent, statusShown: vis(bar.querySelector('.lbar-status')),
    tone: bar.querySelector('.lbar-status').dataset.tone || null, spin: vis(bar.querySelector('.lbar-spin')),
    statusTitle: bar.querySelector('.lbar-status').title,
    outLabelShown: vis(bar.querySelector('.lbar-out-label')),
    outOpen: vis(out), outExpanded: bar.querySelector('.lbar-out').getAttribute('aria-expanded'),
    lines: Array.from(out.querySelectorAll('.lout-line')).map((l) => [l.dataset.kind || '', l.textContent]),
    fixShown: vis(out.querySelector('.lout-fix')),
    outTitle: out.querySelector('.lout-title').textContent,
    injected: !!bar.querySelector('img') || !!out.querySelector('img'),
  };
}

export async function runLaunch(ctx, vp) {
  const { openApp, closeApp, check, until, settle, contrastOf, outFile, convSel, waitConv, MAIN_LAYOUT, FOUR_PANES } = ctx;
  const sel = convSel('c1');
  const logReqs = [];
  const hostSetup = (host) => {
    host.onCmd = (m, h) => {
      if (m.cmd !== 'launchLog') return;
      logReqs.push(m);
      setTimeout(() => h.send({ type: 'launchLog', conversationId: m.conversationId, targetId: m.targetId, epoch: 3, seq: LOG_LINES.length, reset: true, lines: LOG_LINES }), 40);
    };
  };
  const app = await openApp('launch', { vp, layout: MAIN_LAYOUT, hostSetup });
  const { page, host } = app;
  await waitConv(page, 'c1');
  await settle(page, 200);
  const state = () => page.evaluate(launchState, sel);
  // 執行列在標題列：截標題列；輸出面板在輸入區：截整個窗格
  const headShot = async (file) => { await settle(page, 250); await page.locator(sel + ' .ch').screenshot({ path: outFile(file) }); };
  const paneShot = async (file) => { await settle(page, 250); await page.locator(sel).screenshot({ path: outFile(file) }); };
  const send = async (launch) => { await host.send({ type: 'launchStatus', conversationId: 'c1', launch }); await settle(page, 150); };

  await host.send({ type: 'gitStatus', conversationId: 'c1', git: GIT });
  await settle(page, 150);
  const s0 = await state();
  check('launchHiddenWithoutStatus', !!s0 && !s0.shown, s0);

  // 閒置：只有啟動能按；在標題列第二行、git 晶片後面，標題列沒有長高，「輸出」靠右；輸出面板在輸入區最上方；專案名稱與說明
  await send(view());
  const idle = await state();
  check('launchIdle', idle.shown && idle.inSub && idle.afterGit === true && idle.headHeight === s0.headHeight && !idle.shortHead
    && idle.clearOfButtons && idle.partsApart && idle.inPane && !idle.rowOverflow && idle.rowHeight === 24 && Math.abs(idle.outGap) <= 1
    && idle.outFirstInDock && idle.outLabelShown
    && idle.start.enabled && !idle.pause.enabled && !idle.stop.enabled && !idle.restart.enabled
    && idle.start.title === '建置並執行（F5）' && idle.name === 'Myriagent' && /WinForms · net10\.0-windows/.test(idle.targetTitle)
    && /點一下換別的專案/.test(idle.targetTitle) && !idle.statusShown
    && idle.modeShown && idle.mode === 'Debug' && /^執行模式：Debug\n.*一起關閉/.test(idle.modeTitle), idle);
  await headShot('launch-idle-dark.png');

  // 按啟動、F5（焦點在輸入框）都送 launchAction start
  await page.click(sel + ' .lbtn[data-act="start"]');
  const clicked = await until(() => host.last('launchAction', (m) => m.action === 'start' && m.targetId === 't1' && m.conversationId === 'c1'), 2000);
  check('launchStartClickSends', !!clicked, clicked);
  const before = host.received.filter((m) => m.cmd === 'launchAction').length;
  await page.focus(sel + ' .cinput');
  await page.keyboard.press('F5');
  await settle(page, 100);
  const f5 = host.received.filter((m) => m.cmd === 'launchAction').slice(before);
  check('launchF5Starts', f5.length === 1 && f5[0].action === 'start', f5);

  // 建置中：轉圈＋經過時間；停止、重新建置能按；Shift+F5 送 stop
  // 經過時間從 12 秒起算；send 的 settle 會等還沒載完的圖片（最多 2.5 秒），上限照實際經過的時間
  const buildAt = Date.now();
  await send(view({ phase: 'building', since: buildAt - 12_000, logSeq: 3, logEpoch: 3, targets: [Object.assign({}, TARGETS[0], { phase: 'building' }), TARGETS[1]] }));
  const building = await state();
  const buildSecs = /^建置中 0:(\d\d)$/.exec(building.status);
  building.maxSecs = 12 + Math.ceil((Date.now() - buildAt) / 1000);
  check('launchBuilding', building.spin && !!buildSecs && Number(buildSecs[1]) >= 12 && Number(buildSecs[1]) <= building.maxSecs && building.tone === 'busy'
    && !building.start.enabled && building.stop.enabled && building.restart.enabled && !building.pause.enabled, building);
  await headShot('launch-building-dark.png');
  const b2 = host.received.length;
  await page.keyboard.press('Shift+F5');
  await settle(page, 100);
  const stopKey = host.received.slice(b2).filter((m) => m.cmd === 'launchAction');
  check('launchShiftF5Stops', stopKey.length === 1 && stopKey[0].action === 'stop', stopKey);

  // 執行中：暫停能按；每秒更新經過時間；說明帶這一次的模式
  await send(view({ phase: 'running', runMode: 'debug', since: Date.now() - 65_000, logSeq: 9, logEpoch: 3, targets: [Object.assign({}, TARGETS[0], { phase: 'running' }), TARGETS[1]] }));
  const running = await state();
  check('launchRunning', running.tone === 'ok' && /^執行中 1:0\d$/.test(running.status) && !running.spin
    && running.pause.enabled && running.stop.enabled && running.restart.enabled && !running.start.enabled
    && /\nDebug 模式：.*一起關閉$/.test(running.statusTitle), running);
  await headShot('launch-running-dark.png');
  await settle(page, 1100);
  const ticked = await state();
  // 中間截了圖（要幾秒），只比較變大了
  const secs = (t) => { const m = /^執行中 (\d+):(\d\d)$/.exec(t); return m ? Number(m[1]) * 60 + Number(m[2]) : -1; };
  check('launchElapsedTicks', secs(ticked.status) > secs(running.status) && secs(running.status) > 0, { before: running.status, after: ticked.status });
  await page.click(sel + ' .lbtn[data-act="pause"]');
  const paused = await until(() => host.last('launchAction', (m) => m.action === 'pause'), 2000);
  check('launchPauseSends', !!paused, paused);

  // 暫停中：第一顆是「繼續」，F5 送 resume
  await send(view({ phase: 'paused', since: Date.now() - 70_000, logSeq: 9, logEpoch: 3 }));
  const pausedState = await state();
  check('launchPaused', pausedState.tone === 'paused' && /^已暫停/.test(pausedState.status) && pausedState.start.enabled
    && pausedState.start.title === '繼續（F5）' && pausedState.start.action === 'resume' && !pausedState.pause.enabled, pausedState);
  const b3 = host.received.length;
  await page.focus(sel + ' .cinput');
  await page.keyboard.press('F5');
  await settle(page, 100);
  const resume = host.received.slice(b3).filter((m) => m.cmd === 'launchAction');
  check('launchF5Resumes', resume.length === 1 && resume[0].action === 'resume', resume);

  // 建置失敗：自動打開輸出面板、拿 launchLog、錯誤行標紅、指令行變淡、宿主的文字不當 HTML；「把錯誤貼到輸入框」
  const failAt = Date.now();
  await send(view({ last: { kind: 'buildFailed', code: 1, text: 'MainForm.cs(57,30): CS1002 必須是 ;', at: failAt }, logSeq: LOG_LINES.length, logEpoch: 3 }));
  const failed = await until(async () => { const s = await state(); return s.outOpen && s.lines.length === LOG_LINES.length ? s : null; }, 3000) || await state();
  const req = logReqs[logReqs.length - 1];
  check('launchBuildFailedOpensOutput', failed.outOpen && failed.outExpanded === 'true' && failed.outFirstInDock && failed.outAboveBox && failed.tone === 'danger' && failed.status === '建置失敗'
    && failed.statusTitle === '建置失敗\nMainForm.cs(57,30): CS1002 必須是 ;' && failed.start.enabled && !failed.stop.enabled
    && !!req && req.targetId === 't1' && req.epoch === -1 && req.after === 0 && failed.outTitle === '輸出 — Myriagent', { failed, req });
  check('launchOutputLineKinds', failed.lines[0][0] === 'cmd' && failed.lines[3][0] === 'warn' && failed.lines[4][0] === 'error' && failed.lines[5][0] === 'error'
    && failed.lines[1][0] === '' && failed.lines[8][1] === '<img src=x onerror=alert(1)>' && !failed.injected && failed.fixShown, failed.lines);
  // 建置的錯誤與警告數（第七批）：狀態旁「✕ 2 ! 1」（同一行只算一次），點了打開輸出、捲到第一個錯誤並閃一下
  const diag = await until(() => page.evaluate((s) => {
    const d = document.querySelector(s + ' .lbar-diag');
    if (!d || d.hidden) return null;
    return { err: d.querySelector('.ld-err').textContent, warn: d.querySelector('.ld-warn').textContent, title: d.title,
      afterStatus: !!d.previousElementSibling && d.previousElementSibling.classList.contains('lbar-status') };
  }, sel), 2000, 20);
  await page.click(sel + ' .lbar-diag');
  const hit = await until(() => page.evaluate((s) => {
    const rows = [...document.querySelectorAll(s + ' .lout-body > .lout-line')];
    const i = rows.findIndex((r) => r.classList.contains('fx-hit'));
    return i >= 0 ? i : null;
  }, sel), 1500, 10);
  check('launchDiag', !!diag && diag.err === '2' && diag.warn === '1' && diag.afterStatus && /^2 個錯誤、1 個警告/.test(diag.title) && hit === 4, { diag, hit });
  await paneShot('launch-build-failed-dark.png');
  await page.click(sel + ' .lout-fix');
  await settle(page, 150);
  const text = await page.evaluate((s) => document.querySelector(s + ' .cinput').value, sel);
  check('launchFixPastesErrors', text.startsWith('執行列建置 src\\Myriagent\\Myriagent.csproj 失敗，請修正：\n```\n')
    && text.includes('error CS1002') && text.includes('error CS0103') && !text.includes('warning CS0168') && text.endsWith('\n```'), text);
  // 輸入框原本有字：接在最後面、另起一段
  await page.evaluate((s) => { const ta = document.querySelector(s + ' .cinput'); ta.value = '先看一下這個 '; ta.setSelectionRange(0, 0); ta.dispatchEvent(new Event('input')); }, sel);
  await page.click(sel + ' .lout-fix');
  await settle(page, 150);
  const appended = await page.evaluate((s) => document.querySelector(s + ' .cinput').value, sel);
  check('launchFixAppends', appended.startsWith('先看一下這個\n\n執行列建置 ') && appended.endsWith('\n```'), appended);
  await page.evaluate((s) => { const ta = document.querySelector(s + ' .cinput'); ta.value = ''; ta.dispatchEvent(new Event('input')); }, sel);
  // 收起之後同一次失敗不再自動打開；再送一次一樣的狀態（logSeq 沒變）也不重拿
  await page.click(sel + ' .lout-head .ibtn[aria-label="收起輸出"]');
  const reqCount = logReqs.length;
  await send(view({ last: { kind: 'buildFailed', code: 1, text: 'MainForm.cs(57,30): CS1002 必須是 ;', at: failAt }, logSeq: LOG_LINES.length, logEpoch: 3 }));
  const closed = await state();
  check('launchOutputStaysClosed', !closed.outOpen && closed.outExpanded === 'false', closed);
  await page.click(sel + ' .lbar-out');
  await settle(page, 150);
  check('launchOutputToggle', (await state()).outOpen && logReqs.length === reqCount, { reqs: logReqs.length, reqCount });
  await page.click(sel + ' .lbar-out');

  // 目標選單：兩個專案（選的打勾、說明與路徑）、換專案送 launchSelect、「重新偵測專案」送 launchRescan
  await page.click(sel + ' .lbar-target');
  const menu = await until(() => page.evaluate(() => {
    const m = document.querySelector('.menu.menu-launch');
    return m ? { items: Array.from(m.querySelectorAll('.menu-item')).map((i) => [i.querySelector('.mi-label').textContent, i.getAttribute('aria-checked'), (i.querySelector('.mi-desc') || {}).textContent || '']) } : null;
  }), 2000);
  check('launchTargetMenu', !!menu && menu.items.length === 3 && menu.items[0][0] === 'Myriagent' && menu.items[0][1] === 'true'
    && menu.items[1][2] === 'WinExe · net10.0・tools\\fake-device\\FakeDevice.csproj' && menu.items[2][0] === '重新偵測專案', menu);
  await settle(page, 400);
  await page.screenshot({ path: outFile('launch-menu-dark.png'), clip: { x: 300, y: 400, width: 900, height: 500 } });
  await page.click('.menu-launch .menu-item:nth-of-type(2)');
  const select = await until(() => host.last('launchSelect', (m) => m.targetId === 't2' && m.conversationId === 'c1'), 2000);
  await page.click(sel + ' .lbar-target');
  await until(() => page.evaluate(() => !!document.querySelector('.menu.menu-launch')), 2000);
  await page.click('.menu-launch .menu-item:nth-of-type(3)');
  const rescan = await until(() => host.last('launchRescan', (m) => m.conversationId === 'c1'), 2000);
  check('launchMenuSends', !!select && !!rescan, { select, rescan });

  // 執行模式選單：Debug／Release（選的打勾、各自的說明），換模式送 launchSelect＋mode；Release 用強調色；執行中換的說明下一次才套用
  await send(view());
  await page.click(sel + ' .lbar-mode');
  const modeMenu = await until(() => page.evaluate(() => {
    const m = document.querySelector('.menu.menu-launch-mode');
    return m ? { items: Array.from(m.querySelectorAll('.menu-item')).map((i) => [i.querySelector('.mi-label').textContent, i.getAttribute('aria-checked'), (i.querySelector('.mi-desc') || {}).textContent || '']),
      note: !!m.querySelector('.lbar-note') } : null;
  }), 2000);
  check('launchModeMenu', !!modeMenu && modeMenu.items.length === 2 && modeMenu.items[0][0] === 'Debug' && modeMenu.items[0][1] === 'true'
    && modeMenu.items[1][0] === 'Release' && modeMenu.items[1][1] === 'false' && /不影響它/.test(modeMenu.items[1][2]) && /一起關閉/.test(modeMenu.items[0][2]) && !modeMenu.note, modeMenu);
  await settle(page, 400);
  await page.screenshot({ path: outFile('launch-mode-menu-dark.png'), clip: { x: 300, y: 0, width: 900, height: 400 } });
  await page.click('.menu-launch-mode .menu-item:nth-of-type(2)');
  const modeSel = await until(() => host.last('launchSelect', (m) => m.mode === 'release' && m.targetId === 't1' && m.conversationId === 'c1'), 2000);
  check('launchModeMenuSends', !!modeSel, modeSel);
  const debugColor = (await state()).modeColor;
  await send(view({ mode: 'release', phase: 'running', runMode: 'debug', since: Date.now() - 5000 }));
  const released = await state();
  check('launchModeRelease', released.mode === 'Release' && released.modeColor !== debugColor
    && /這次以 Debug 模式執行；下次啟動或重新建置並執行時改用 Release/.test(released.modeTitle), { released, debugColor });
  await headShot('launch-mode-release-dark.png');
  await page.click(sel + ' .lbar-mode');
  const pendingNote = await until(() => page.evaluate(() => { const m = document.querySelector('.menu.menu-launch-mode'); return m ? !!m.querySelector('.lbar-note') : null; }), 2000);
  check('launchModeMenuPendingNote', pendingNote === true, pendingNote);
  await page.keyboard.press('Escape');
  await settle(page, 200);

  // null：藏起來（換了資料夾、沒有專案）
  await send(null);
  const gone = await state();
  check('launchHiddenOnNull', !gone.shown, gone);
  await closeApp(app);

  // 4 個窗格的窄窗格：標題列放得下（「輸出」只留圖示），不超出窗格、不蓋到標題列的按鈕；很窄時專案晶片只留圖示
  const grid = await openApp('launch-grid', { vp: { w: 1100, h: 700 }, layout: FOUR_PANES, hostSetup });
  await waitConv(grid.page, 'c1');
  await grid.host.send({ type: 'gitStatus', conversationId: 'c1', git: GIT });
  await grid.host.send({ type: 'launchStatus', conversationId: 'c1', launch: view({ phase: 'running', since: Date.now() - 5000 }) });
  await settle(grid.page, 300);
  const narrow = await grid.page.evaluate(launchState, sel);
  // 2×2 的矮窗格：標題列擠回一行，執行列自己換到下一行（名稱與狀態放得下）
  check('launchNarrowPane', narrow.shown && narrow.inSub && narrow.shortHead && narrow.lineOne && narrow.ownRow && narrow.headHeight <= 72 && narrow.inPane && !narrow.rowOverflow
    && narrow.clearOfButtons && narrow.chipsClear && narrow.partsApart && !narrow.outLabelShown && narrow.rowHeight === 24
    && narrow.nameShown && narrow.statusShown && Math.abs(narrow.outGap) <= 1 && narrow.modeShown && narrow.modeShort === null, narrow);
  await grid.page.locator(sel + ' .ch').screenshot({ path: outFile('launch-narrow-dark.png') });
  await grid.page.setViewportSize({ width: 760, height: 700 });
  await settle(grid.page, 300);
  const tiny = await grid.page.evaluate(launchState, sel);
  check('launchTinyPane', tiny.shown && tiny.lineOne && tiny.ownRow && tiny.headHeight <= 72 && tiny.inPane && !tiny.rowOverflow && tiny.clearOfButtons && tiny.chipsClear && tiny.partsApart
    && !tiny.nameShown && tiny.statusShown && /^執行中/.test(tiny.statusTitle) && tiny.modeShown && tiny.modeShort === 'D', tiny);
  await grid.page.locator(sel + ' .ch').screenshot({ path: outFile('launch-tiny-dark.png') });
  await closeApp(grid);

  // 寬度掃描（一個窗格、兩行的標題列）：窗格寬於 610px 時排在 git 晶片後面同一排，窄於它時自己一行；
  // 每個寬度都不超出窗格、不蓋到晶片、標題與標題列的按鈕，「輸出」靠右
  const sweep = await openApp('launch-sweep', { vp: { w: 1440, h: 900 }, layout: MAIN_LAYOUT, hostSetup });
  await waitConv(sweep.page, 'c1');
  await sweep.host.send({ type: 'gitStatus', conversationId: 'c1', git: GIT });
  await sweep.host.send({ type: 'launchStatus', conversationId: 'c1', launch: view({ phase: 'running', since: Date.now() - 5000 }) });
  const rows = [];
  for (const w of [1440, 1200, 1000, 900, 860, 840, 820, 800, 780, 740, 700]) {
    await sweep.page.setViewportSize({ width: w, height: 900 });
    await settle(sweep.page, 200);
    const st = await sweep.page.evaluate(launchState, sel);
    const want = st.paneWidth > 614 ? 'inline' : st.paneWidth < 606 ? 'row' : 'either';
    const mode = st.afterGit ? 'inline' : st.ownRow ? 'row' : 'none';
    rows.push({ vp: w, pane: st.paneWidth, mode, want, ok: st.shown && !st.shortHead && (want === 'either' ? mode !== 'none' : mode === want)
      && st.inPane && !st.rowOverflow && st.clearOfButtons && st.chipsClear && st.partsApart && Math.abs(st.outGap) <= 1 && st.rowHeight === 24 && st.statusShown });
    if (w === 1000 || w === 780) await sweep.page.locator(sel + ' .ch').screenshot({ path: outFile(`launch-sweep-${w}-dark.png`) });
  }
  check('launchWidthSweep', rows.every((r) => r.ok) && rows.some((r) => r.mode === 'inline') && rows.some((r) => r.mode === 'row'), rows);
  await closeApp(sweep);

  // 淺色主題：狀態文字（執行中的綠、失敗的紅）與輸出的錯誤行看得清楚
  const light = await openApp('launch-light', { vp, layout: MAIN_LAYOUT, settings: { theme: 'light' }, colorScheme: 'light', hostSetup });
  await waitConv(light.page, 'c1');
  const contrasts = {};
  await light.host.send({ type: 'launchStatus', conversationId: 'c1', launch: view({ phase: 'running', since: Date.now() - 5000 }) });
  await settle(light.page, 250);
  contrasts.running = await light.page.evaluate(contrastOf, sel + ' .lbar-status-text');
  await light.page.locator(sel + ' .ch').screenshot({ path: outFile('launch-running-light.png') });
  await light.host.send({ type: 'launchStatus', conversationId: 'c1', launch: view({ last: { kind: 'buildFailed', code: 1, text: 'x', at: Date.now() + 1 }, logSeq: LOG_LINES.length, logEpoch: 3 }) });
  await until(() => light.page.evaluate((s) => document.querySelectorAll(s + ' .lout-line').length > 0, sel), 3000);
  await settle(light.page, 200);
  contrasts.failed = await light.page.evaluate(contrastOf, sel + ' .lbar-status-text');
  contrasts.errorLine = await light.page.evaluate(contrastOf, sel + ' .lout-line[data-kind="error"]');
  check('launchLightContrast', Object.values(contrasts).every((c) => c !== null && c >= 4.5), contrasts);
  await light.page.locator(sel).screenshot({ path: outFile('launch-build-failed-light.png') });
  await closeApp(light);
}
