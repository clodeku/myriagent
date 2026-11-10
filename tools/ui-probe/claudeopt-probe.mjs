// Claude 對話的選項與操作（2026-10-25；Web/claudeoptrules.js、rewind.js、taskdetail.js）的情境：probe.mjs 匯入，--claudeopt 只跑它，完整執行時也跑。
// 檢查名稱 claudeOpt*：輸入列的「代理」「風格」選單（setAgent、setOutputStyle）、標頭的目標晶片與清除（goalClear）、
// 已插入這一輪的那則與取消插入（cancelSteer、steerCancelled）、等背景工作回報時的詳細（taskDetail）與強制停止（taskStop）、
// 使用者訊息的回溯（rewindPreview、rewind、rewound）。2026-10-26 起另有：子代理用量（Header.subagents）畫在 Agent 工具卡上、
// 等額度重置後自動接著做的那一行與取消（Header.quotaWait、quotaWaitCancel）、閒置超過一小時送出前的快取提醒（Header.idleSince、claudeIdleResumeWarning；
// 2026-11-11 起照 Header.coldResume 算的額度百分比到門檻才問）、執行中把前景的工作移到背景（Header.canBackground、backgroundTasks）。
// 第九批（2026-10-26）：那一行的 12 格倒數與時間到時輸入框閃一下（claudeOptQuotaCells、claudeOptQuotaGo）、上下文圓環外圈的提示快取倒數（claudeOptCacheRing）。
// 截圖 claudeopt-*.png

const AGENTS = [
  { value: 'default', label: '預設', description: 'Claude Code 內建的主代理' },
  { value: 'Explore', label: 'Explore', description: 'Fast agent specialized for exploring codebases' },
  { value: 'autopilot:debugger', label: 'autopilot:debugger', description: '系統化除錯' },
];
const STYLES = ['default', 'Proactive', 'Concise', 'Explanatory', 'Learning'];

function composerState(sel) {
  const root = document.querySelector(sel);
  const vis = (el) => { if (!el) return false; const cs = getComputedStyle(el); const r = el.getBoundingClientRect(); return cs.display !== 'none' && cs.visibility !== 'hidden' && r.width > 0 && r.height > 0; };
  const agent = root.querySelector('.cbar [data-field="agent"]');
  const style = root.querySelector('.cbar [data-field="outputStyle"]');
  const steer = root.querySelector('.steered');
  const bar = root.querySelector('.cbar').getBoundingClientRect();
  const inBar = (el) => { const r = el.getBoundingClientRect(); return r.top >= bar.top - 0.5 && r.bottom <= bar.bottom + 0.5 && r.right <= bar.right + 0.5; };
  return {
    agent: vis(agent) ? { label: agent.querySelector('.pick-label').textContent, title: agent.title, inBar: inBar(agent) } : null,
    style: vis(style) ? { label: style.querySelector('.pick-label').textContent, title: style.title, inBar: inBar(style) } : null,
    steer: vis(steer) ? { text: steer.querySelector('.steer-sum').textContent, button: steer.querySelector('.steer-cancel').textContent } : null,
    draft: root.querySelector('.cbox textarea').value,
  };
}

function menuItems() {
  const m = document.querySelector('.menu');
  if (!m) return null;
  return Array.from(m.querySelectorAll('.menu-item')).map((b) => ({ text: b.textContent, checked: b.getAttribute('aria-checked') === 'true' || b.classList.contains('checked'), disabled: b.disabled || b.getAttribute('aria-disabled') === 'true' }));
}

export async function runClaudeOpt(ctx, vp) {
  const { openApp, closeApp, check, until, settle, outFile } = ctx;
  const sel = '.conv[data-conv="c1"]';
  const onePane = (id) => ({ panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' });
  const app = await openApp('claudeopt', { vp, layout: onePane('c1') });
  const { page, host } = app;
  await page.waitForSelector(`${sel}:not(.loading) .items`, { state: 'attached', timeout: 8000 });
  const c = host.conv('c1');
  const push = async (extra) => { c.header = Object.assign({}, c.header, extra); await host.pushHeader('c1'); await settle(page, 150); };

  // 輸入列：代理與風格（閒置的 Claude 對話）
  await push({ running: false, status: null, startedAt: null, agent: 'default', agents: AGENTS, outputStyle: null, outputStyles: STYLES });
  const s1 = await page.evaluate(composerState, sel);
  // 都是預設時沒有常駐的晶片（不把模型與推理強度的名稱擠短），從模型選單底部進入
  check('claudeOptNoChipsByDefault', !s1.agent && !s1.style, s1);
  const openFromModel = async (label) => {
    await page.click(`${sel} .cbar .pick:not([data-field])`);
    const items = await until(() => page.evaluate(menuItems), 2000);
    await page.locator('.menu .menu-item', { hasText: label }).click();
    await until(() => page.evaluate(() => { const m = document.querySelector('.menu'); return m && !/主代理：/.test(m.textContent) ? true : null; }), 2000);
    return items;
  };
  const modelItems = await openFromModel('輸出風格：');
  check('claudeOptModelMenuEntries', !!modelItems && modelItems.some((i) => i.text.includes('主代理：預設')) && modelItems.some((i) => i.text.includes('輸出風格：照設定')), modelItems);
  const styleMenu = await until(() => page.evaluate(menuItems), 2000);
  check('claudeOptStyleMenu', !!styleMenu && styleMenu.length === 6 && /照設定/.test(styleMenu[0].text) && styleMenu.some((i) => /解說/.test(i.text) && /Explanatory/.test(i.text)), styleMenu);
  await settle(page, 250);
  await page.screenshot({ path: outFile('claudeopt-style-menu-dark.png') });
  await page.locator('.menu .menu-item', { hasText: '解說' }).click();
  const setStyle = await until(() => host.last('setOutputStyle'), 2000);
  const s2 = await page.evaluate(composerState, sel);
  check('claudeOptStyleSent', !!setStyle && setStyle.style === 'Explanatory' && !!s2.style && s2.style.label === '解說' && s2.style.inBar, { setStyle, s2 });
  await openFromModel('主代理：');
  const agentMenu = await until(() => page.evaluate(menuItems), 2000);
  check('claudeOptAgentMenu', !!agentMenu && agentMenu.length === 3 && agentMenu.every((i) => !i.disabled), agentMenu);
  await page.locator('.menu .menu-item', { hasText: 'autopilot:debugger' }).click();
  const setAgent = await until(() => host.last('setAgent'), 2000);
  const s2b = await page.evaluate(composerState, sel);
  check('claudeOptAgentSent', !!setAgent && setAgent.agent === 'autopilot:debugger' && !!s2b.agent && s2b.agent.inBar, { setAgent, s2b });
  await page.locator(`${sel} .cbar`).screenshot({ path: outFile('claudeopt-cbar-dark.png') });

  // 目標晶片：顯示、選單、清除；執行中停用
  await push({ goal: { condition: '工作資料夾裡有 z1.txt 到 z9.txt 九個檔案；每建立一個之前先用 Bash 執行 ping' } });
  const chip = await page.evaluate((s) => { const g = document.querySelector(s + ' .ch-goal'); return g && !g.hidden ? { label: g.querySelector('.chip-label').textContent, title: g.title } : null; }, sel);
  check('claudeOptGoalChip', !!chip && chip.label.startsWith('目標：工作資料夾裡有') && chip.label.endsWith('…') && /ping/.test(chip.title), chip);
  await page.click(`${sel} .ch-goal`);
  const goalMenu = await until(() => page.evaluate(menuItems), 2000);
  check('claudeOptGoalMenu', !!goalMenu && goalMenu.some((i) => /清除目標/.test(i.text) && !i.disabled), goalMenu);
  await page.screenshot({ path: outFile('claudeopt-goal-dark.png') });
  await page.locator('.menu .menu-item', { hasText: '清除目標' }).click();
  const cleared = await until(() => host.last('goalClear'), 2000);
  check('claudeOptGoalClear', !!cleared && cleared.conversationId === 'c1', cleared);

  // 已插入這一輪的那則：輸入區上方一行、取消插入、宿主說取消了就放回輸入框
  await push({ running: true, status: '執行指令：dotnet test', startedAt: Date.now() - 20e3 });
  c.composer = Object.assign({}, c.composer, { queue: [], steered: { id: 'i1', text: '順便把整合測試也跑一下' } });
  await host.pushComposer('c1');
  await settle(page, 150);
  const s3 = await page.evaluate(composerState, sel);
  check('claudeOptSteered', !!s3.steer && s3.steer.text === '已插入這一輪：順便把整合測試也跑一下' && s3.steer.button === '取消插入', s3);
  await page.locator(`${sel} .dock`).screenshot({ path: outFile('claudeopt-steered-dark.png') });
  await page.click(`${sel} .steer-cancel`);
  const cancel = await until(() => host.last('cancelSteer'), 2000);
  await host.send({ type: 'steerCancelled', conversationId: 'c1', text: '順便把整合測試也跑一下' });
  c.composer = Object.assign({}, c.composer, { steered: null });
  await host.pushComposer('c1');
  await settle(page, 150);
  const s4 = await page.evaluate(composerState, sel);
  check('claudeOptSteerCancel', !!cancel && cancel.id === 'i1' && !s4.steer && s4.draft.includes('順便把整合測試也跑一下'), { cancel, s4 });

  // 執行中把前景的指令與子代理移到背景（Header.canBackground、backgroundTasks，2026-11-11）：狀態列的「移到背景」，沒有 canBackground 就藏起來
  const toBgState = () => page.evaluate((s) => {
    const b = document.querySelector(s + ' .run-tobg');
    return b && !b.hidden && b.getBoundingClientRect().width > 0 ? { text: b.textContent, title: b.title, disabled: b.disabled } : null;
  }, sel);
  const noToBg = await toBgState();
  await push({ canBackground: true });
  const toBg = await toBgState();
  await page.locator(`${sel} .runline`).screenshot({ path: outFile('claudeopt-tobg-dark.png') });
  await page.click(`${sel} .run-tobg`);
  const toBgSent = await until(() => host.last('backgroundTasks'), 2000);
  const toBgDisabled = (await toBgState())?.disabled;
  await push({ canBackground: undefined });
  const toBgGone = await toBgState();
  check('claudeOptMoveToBackground', !noToBg && !!toBg && toBg.text === '移到背景' && /Ctrl\+B/.test(toBg.title) && !!toBgSent && toBgSent.conversationId === 'c1'
    && toBgDisabled === true && !toBgGone, { noToBg, toBg, toBgSent, toBgDisabled, toBgGone });

  // 等背景工作回報：詳細、強制停止（每一個都有 stopId：子代理看不到輸出但停得了）
  const bgTasks = [{ key: 'tping', tool: '執行指令', title: 'ping -n 30 127.0.0.1', startedAt: Date.now() - 90e3, taskId: 'b1', stopId: 'b1' },
    { key: 'tagent', tool: '子代理', title: '整理報告', startedAt: Date.now() - 30e3, stopId: 'a1' }];
  await push({ running: false, status: null, startedAt: null, goal: null, background: { count: 2, startedAt: Date.now() - 90e3, tasks: bgTasks } });
  const out = await page.evaluate((s) => { const b = document.querySelector(s + ' .run-detail'); return b && !b.hidden && b.getBoundingClientRect().width > 0 ? b.textContent : null; }, sel);
  check('claudeOptTaskDetailButton', out === '詳細', out);
  const stopBtn = await page.evaluate((s) => {
    const b = document.querySelector(s + ' .run-stop');
    const o = document.querySelector(s + ' .run-detail');
    if (!b || b.hidden || !(b.getBoundingClientRect().width > 0)) return null;
    const r = b.getBoundingClientRect();
    const line = document.querySelector(s + ' .runline').getBoundingClientRect();
    return { text: b.textContent, title: b.title, icon: !!b.querySelector('svg'), sameLine: Math.abs(r.top - o.getBoundingClientRect().top) < 1, inLine: r.right <= line.right + 0.5 };
  }, sel);
  check('claudeOptTaskStopButton', !!stopBtn && stopBtn.text === '停止' && stopBtn.icon && stopBtn.sameLine && stopBtn.inLine && /強制停止/.test(stopBtn.title), stopBtn);
  await page.click(`${sel} .run-stop`);
  const stopMenu = await until(() => page.evaluate(menuItems), 2000);
  check('claudeOptTaskStopMenu', !!stopMenu && stopMenu.length === 3 && stopMenu[0].text.includes('ping -n 30 127.0.0.1') && stopMenu[1].text.includes('整理報告')
    && stopMenu[2].text.includes('全部停止（2 個背景工作）'), stopMenu);
  await page.locator(`${sel} .runline`).screenshot({ path: outFile('claudeopt-taskstop-line-dark.png') });
  await page.screenshot({ path: outFile('claudeopt-taskstop-menu-dark.png') });
  await page.locator('.menu .menu-item', { hasText: '全部停止' }).click();
  const stopAll = await until(() => host.last('taskStop'), 2000);
  const stopDisabled = await page.evaluate((s) => document.querySelector(s + ' .run-stop').disabled, sel);
  check('claudeOptTaskStopAll', !!stopAll && stopAll.conversationId === 'c1' && !('stopId' in stopAll) && stopDisabled, { stopAll, stopDisabled });
  await push({ background: { count: 2, startedAt: Date.now() - 90e3, tasks: bgTasks } });
  // 「詳細」：兩個背景工作時先選一個；對話框寫在做什麼、代理的說明、指令與輸出，裡面也有停止鈕
  await page.click(`${sel} .run-detail`);
  const pick = await until(() => page.evaluate(menuItems), 2000);
  check('claudeOptTaskDetailPick', !!pick && pick.length === 2 && pick[0].text.includes('ping -n 30 127.0.0.1') && pick[1].text.includes('整理報告'), pick);
  await page.locator('.menu .menu-item', { hasText: 'ping -n 30' }).click();
  const ask = await until(() => host.last('taskDetail'), 2000);
  await host.send({ type: 'taskDetail', conversationId: 'c1', requestId: ask && ask.requestId, key: 'tping', tool: '執行指令', title: 'ping -n 30 127.0.0.1',
    explain: '等待一段時間，或反覆檢查某個條件、成立了才結束（輪詢），結束時代理會接著處理。', agentNote: 'Wait for the dev server to come up',
    content: 'ping -n 30 127.0.0.1', contentLabel: '指令',
    output: '\r\nPing 127.0.0.1 (使用 32 位元組的資料):\r\n回覆自 127.0.0.1: 位元組=32 時間<1ms TTL=128\r\n<img src=x onerror=alert(1)>\r\n', totalBytes: 272, truncated: false, error: null });
  await settle(page, 200);
  const dlg = await page.evaluate(() => {
    const d = document.querySelector('.dlg-task-detail');
    if (!d) return null;
    return {
      title: d.querySelector('.dlg-title').textContent, labels: Array.from(d.querySelectorAll('.td-label')).map((l) => l.firstChild && l.firstChild.textContent || l.textContent),
      explain: d.querySelector('.td-explain').textContent, note: d.querySelector('.td-note').textContent, facts: d.querySelector('.td-facts').textContent,
      content: d.querySelector('.td-content').textContent, meta: d.querySelector('.td-meta').textContent, text: d.querySelector('.td-out').textContent,
      img: !!d.querySelector('img'), stop: d.querySelector('.td-stop').textContent,
    };
  });
  check('claudeOptTaskDetailDialog', !!ask && ask.key === 'tping' && !!dlg && /ping -n 30/.test(dlg.title) && /^等待一段時間/.test(dlg.explain)
    && dlg.note === '代理的說明：Wait for the dev server to come up' && /^執行指令・已執行 1 分/.test(dlg.facts) && dlg.content === 'ping -n 30 127.0.0.1'
    && dlg.labels.join('|') === '在做什麼|指令|輸出' && /回覆自 127\.0\.0\.1/.test(dlg.text) && /272 B/.test(dlg.meta) && !dlg.img && dlg.stop === '停止這個背景工作', { ask, dlg });
  await page.screenshot({ path: outFile('claudeopt-taskdetail-dark.png') });
  // 對話框的停止鈕：帶這一個的 stopId；停了之後問不到（已結束），說明與最後一次的輸出留著
  await page.click('.dlg-task-detail .td-stop');
  const stopOne = await until(() => (host.last('taskStop') || {}).stopId === 'b1' ? host.last('taskStop') : null, 2000);
  const again = await until(() => { const a = host.last('taskDetail'); return a && a.requestId !== ask.requestId ? a : null; }, 3000);
  await host.send({ type: 'taskDetail', conversationId: 'c1', requestId: again && again.requestId, key: 'tping', error: '這個背景工作已經結束了' });
  await settle(page, 200);
  const afterStop = await page.evaluate(() => {
    const d = document.querySelector('.dlg-task-detail');
    return d ? { meta: d.querySelector('.td-meta').textContent, explain: d.querySelector('.td-explain').textContent, text: d.querySelector('.td-out').textContent,
      hidden: d.querySelector('.td-out').hidden, disabled: d.querySelector('.td-stop').disabled } : null;
  });
  check('claudeOptTaskStopInDialog', !!stopOne && !!afterStop && afterStop.disabled && afterStop.meta === '這個背景工作已經結束了' && /^等待一段時間/.test(afterStop.explain)
    && !afterStop.hidden && /回覆自 127\.0\.0\.1/.test(afterStop.text), { stopOne, afterStop });
  await page.keyboard.press('Escape');
  await settle(page, 150);

  // 只能一起停止的代理（Antigravity、Grok：沒有 stopId）、看不到輸出：停止鈕寫「停止背景工作」、送出不帶 stopId，輸出寫原因
  await push({ background: { count: 1, startedAt: Date.now() - 20e3, tasks: [{ key: 'tsleep', tool: '背景工作', title: 'sleep 30', startedAt: Date.now() - 20e3 }] } });
  await page.click(`${sel} .run-detail`);
  const ask2 = await until(() => { const a = host.last('taskDetail'); return a && a.key === 'tsleep' ? a : null; }, 2000);
  await host.send({ type: 'taskDetail', conversationId: 'c1', requestId: ask2 && ask2.requestId, key: 'tsleep', tool: '背景工作', title: 'sleep 30',
    explain: '等待一段時間，或反覆檢查某個條件、成立了才結束（輪詢），結束時代理會接著處理。', agentNote: null, content: 'sleep 30', contentLabel: '指令',
    outputNote: 'Grok 看不到背景工作執行中的輸出。', error: null });
  await settle(page, 200);
  const grok = await page.evaluate(() => {
    const d = document.querySelector('.dlg-task-detail');
    return d ? { stop: d.querySelector('.td-stop').textContent, note: d.querySelector('.td-note').hidden, meta: d.querySelector('.td-meta').textContent, out: d.querySelector('.td-out').hidden } : null;
  });
  await page.click('.dlg-task-detail .td-stop');
  const stopAll2 = await until(() => { const a = host.last('taskStop'); return a && a !== stopOne ? a : null; }, 2000);
  check('claudeOptTaskDetailStopAll', !!ask2 && !!grok && grok.stop === '停止背景工作' && grok.note && grok.out && grok.meta === 'Grok 看不到背景工作執行中的輸出。'
    && !!stopAll2 && !('stopId' in stopAll2), { grok, stopAll2 });
  await page.keyboard.press('Escape');
  await settle(page, 150);

  // 回溯：滑過使用者訊息才出現「回溯」，預覽、勾選、送出、放回輸入框
  await push({ background: null });
  await page.evaluate((s) => document.querySelector(s + ' .cbox textarea').value = '', sel);
  const userId = await page.evaluate((s) => { const u = Array.from(document.querySelectorAll(s + ' .it-user')).pop(); u.scrollIntoView({ block: 'center' }); return u.dataset.id; }, sel);
  await settle(page, 150);
  const hidden = await page.evaluate(([s, id]) => getComputedStyle(document.querySelector(`${s} .it[data-id="${id}"] .u-rewind`)).display, [sel, userId]);
  await page.hover(`${sel} .it[data-id="${userId}"] .u-box`);
  const shown = await page.evaluate(([s, id]) => getComputedStyle(document.querySelector(`${s} .it[data-id="${id}"] .u-rewind`)).display, [sel, userId]);
  check('claudeOptRewindHover', hidden === 'none' && shown !== 'none', { hidden, shown });
  await page.click(`${sel} .it[data-id="${userId}"] .u-rewind`);
  const pre = await until(() => host.last('rewindPreview'), 2000);
  await host.send({ type: 'rewindPreview', conversationId: 'c1', requestId: pre && pre.requestId, itemId: userId, error: null, laterItems: 6, laterMessages: 2,
    conversation: true, conversationError: null, files: ['src\\Auth\\LoginService.cs', 'tests\\LoginTests.cs'], fileCount: 2, insertions: 12, deletions: 30, filesError: null });
  await settle(page, 200);
  const rd = await page.evaluate(() => {
    const d = document.querySelector('.dlg-rewind');
    if (!d) return null;
    return {
      opts: Array.from(d.querySelectorAll('.rewind-opt')).map((o) => ({ label: o.querySelector('.rewind-opt-label').textContent, note: o.querySelector('.rewind-opt-note').textContent, checked: o.querySelector('input').checked, disabled: o.querySelector('input').disabled })),
      files: Array.from(d.querySelectorAll('.rewind-files li')).map((l) => l.textContent),
      go: d.querySelector('.dlg-foot .btn.primary').disabled,
    };
  });
  check('claudeOptRewindDialog', !!pre && pre.itemId === userId && !!rd && rd.opts.length === 2 && rd.opts.every((o) => o.checked && !o.disabled)
    && /之後的 1 則訊息/.test(rd.opts[0].note) && rd.opts[1].note === '還原 2 個檔案（+12 −30 行）' && rd.files.length === 2 && !rd.go, { pre, rd });
  await page.screenshot({ path: outFile('claudeopt-rewind-dark.png') });
  // 回溯預覽（第八批）：對話框開著時，回溯點那則與之後的項目淡下去、回溯點上緣寫說明；關掉就恢復
  const rwPreview = await page.evaluate(([s, id]) => {
    const items = document.querySelector(s + ' .items');
    const u = items.querySelector(`.it[data-id="${id}"]`);
    const after = Array.from(items.querySelectorAll(`.it[data-id="${id}"] ~ .it`));
    const before = u.previousElementSibling;
    return { on: items.classList.contains('rewind-preview'), from: u.classList.contains('rw-from'), mark: getComputedStyle(u, '::before').content,
      boxOpacity: getComputedStyle(u.querySelector('.u-box')).opacity, after: after.map((n) => getComputedStyle(n).opacity), before: before ? getComputedStyle(before).opacity : null };
  }, [sel, userId]);
  check('claudeOptRewindPreview', rwPreview.on && rwPreview.from && /回溯點/.test(rwPreview.mark) && rwPreview.boxOpacity === '0.32'
    && rwPreview.after.every((o) => o === '0.32') && rwPreview.before === '1', rwPreview);
  await page.locator('.dlg-rewind .rewind-opt', { hasText: '還原檔案' }).click();
  await page.click('.dlg-rewind .dlg-foot .btn.primary');
  const rw = await until(() => host.last('rewind'), 2000);
  await host.send({ type: 'rewound', conversationId: 'c1', text: '把登入流程改回原本的樣子' });
  await settle(page, 150);
  const s5 = await page.evaluate(composerState, sel);
  check('claudeOptRewindSent', !!rw && rw.itemId === userId && rw.conversation === true && rw.files === false && s5.draft.includes('把登入流程改回原本的樣子'), { rw, s5 });
  const rwOff = await page.evaluate((s) => !document.querySelector(s + ' .items.rewind-preview') && !document.querySelector(s + ' .rw-from'), sel);
  check('claudeOptRewindPreviewOff', rwOff, rwOff);

  // 執行中：回溯鈕藏起來
  await push({ running: true, status: '思考中…', startedAt: Date.now() });
  await page.hover(`${sel} .it[data-id="${userId}"] .u-box`);
  const running = await page.evaluate(([s, id]) => getComputedStyle(document.querySelector(`${s} .it[data-id="${id}"] .u-rewind`)).display, [sel, userId]);
  check('claudeOptRewindHiddenWhileRunning', running === 'none', running);

  // 子代理用量（Header.subagents）：畫在那張 Agent 工具卡的標題列；拿掉就消失
  await push({ running: false, status: null, startedAt: null, subagents: { i9: { tokens: 15500, tools: 2, ms: 3200, last: 'Grep', done: false } } });
  const agentChip = await page.evaluate((s) => { const n = document.querySelector(s + ' .it[data-id="i9"] .t-agent-tokens'); return n ? { text: n.textContent, title: n.title } : null; }, sel);
  check('claudeOptSubagentUsage', !!agentChip && agentChip.text === '15.5K tokens' && /呼叫工具 2 次/.test(agentChip.title) && /最近的工具：Grep/.test(agentChip.title), agentChip);
  await page.evaluate((s) => document.querySelector(s + ' .it[data-id="i9"]').scrollIntoView({ block: 'center' }), sel);
  await settle(page, 150);
  await page.locator(`${sel} .it[data-id="i9"]`).screenshot({ path: outFile('claudeopt-subagent-dark.png') });
  await push({ subagents: null });
  const agentChipGone = await page.evaluate((s) => !document.querySelector(s + ' .it[data-id="i9"] .t-agent-tokens'), sel);
  check('claudeOptSubagentUsageCleared', agentChipGone, agentChipGone);

  // 額度用完、等重置後自動接著做（Header.quotaWait）：輸入區上方一行與「取消等待」
  await push({ quotaWait: { until: Date.now() + 2.5 * 3600e3, account: 'Claude 主帳戶' } });
  const qw = await page.evaluate((s) => {
    const q = document.querySelector(s + ' .quotawait');
    return q && !q.hidden && q.getBoundingClientRect().height > 0 ? { text: q.querySelector('.steer-sum').textContent, button: q.querySelector('.steer-cancel').textContent, title: q.title } : null;
  }, sel);
  check('claudeOptQuotaWait', !!qw && /^額度用完：等「Claude 主帳戶」在 \d{2}:\d{2} 重置後自動接著做（還有 2 小時 (29|30) 分）$/.test(qw.text) && qw.button === '取消等待' && /取消/.test(qw.title), qw);
  // 第九批：文字後面 12 格倒數（剛開始等全亮、亮的是帳戶色）
  const cellsOf = () => page.evaluate((s) => {
    const cells = Array.from(document.querySelectorAll(s + ' .quotawait .qw-cells > i'));
    const lit = cells.filter((c) => c.classList.contains('lit'));
    return { n: cells.length, lit: lit.length, litBg: lit.length ? getComputedStyle(lit[0]).backgroundColor : null,
      offBg: cells.length > lit.length ? getComputedStyle(cells[cells.length - 1]).backgroundColor : null,
      acc: getComputedStyle(document.querySelector(s + ' .cbox')).getPropertyValue('--acc').trim() };
  }, sel);
  const qc1 = await cellsOf();
  await page.locator(`${sel} .dock`).screenshot({ path: outFile('claudeopt-quotawait-dark.png') });
  await page.click(`${sel} .quotawait .steer-cancel`);
  const qwCancel = await until(() => host.last('quotaWaitCancel'), 2000);
  await push({ quotaWait: null });
  const qwGone = await page.evaluate((s) => document.querySelector(s + ' .quotawait').hidden, sel);
  const noGo = await page.evaluate((s) => !document.querySelector(s + ' .cbox').classList.contains('fx-qw-go'), sel);
  check('claudeOptQuotaWaitCancel', !!qwCancel && qwCancel.conversationId === 'c1' && qwGone && noGo, { qwCancel, qwGone, noGo });
  // 只剩 30 秒才開始等（倒數的總長至少 1 分鐘）：亮一半；時間到、宿主拿掉 quotaWait（自動接著做）——輸入框閃一下綠；取消（上面，還沒到）不閃
  await push({ quotaWait: { until: Date.now() + 30e3, account: 'Claude 主帳戶' } });
  const qc2 = await cellsOf();
  await push({ quotaWait: null });
  const go = await until(() => page.evaluate((s) => {
    const b = document.querySelector(s + ' .cbox');
    return b.classList.contains('fx-qw-go') ? b.getAnimations().map((a) => a.animationName) : null;
  }, sel), 1500, 15);
  check('claudeOptQuotaCells', qc1.n === 12 && qc1.lit === 12 && !!qc1.litBg && qc1.litBg !== qc2.offBg && qc2.lit === 6, { qc1, qc2 });
  check('claudeOptQuotaGo', !!go && go.includes('fx-qw-go'), go);

  // 第九批：上下文圓環的外圈＝提示快取還剩多久（閒置 20 分＝剩 2/3、綠；55 分＝最後 10 分鐘、琥珀；執行中不畫；2 小時＝過期、灰的虛線）。說明接在上下文說明後面
  const ctxBig = { used: 150000, window: 1000000, percent: 15, autoCompactAt: null, severity: 'ok' };
  const ringOf = () => page.evaluate((s) => {
    const m = document.querySelector(s + ' .ctxm');
    const c = m.querySelector('.ctxm-cache');
    const cs = getComputedStyle(c);
    return { cls: Array.from(m.classList).filter((x) => x.startsWith('cache-')).join(','), display: cs.display, stroke: cs.stroke, dash: c.style.strokeDasharray,
      cssDash: cs.strokeDasharray, title: m.title, ring: getComputedStyle(m.querySelector('.ctxm-ring')).width };
  }, sel);
  await push({ running: false, idleSince: Date.now() - 20 * 60e3, context: ctxBig });
  const cr1 = await ringOf();
  await page.locator(`${sel} .cbar`).screenshot({ path: outFile('claudeopt-cachering-dark.png') }).catch(() => {});
  await push({ idleSince: Date.now() - 55 * 60e3 - 30e3 });
  const cr2 = await ringOf();
  await page.locator(`${sel} .cbar`).screenshot({ path: outFile('claudeopt-cachering-low-dark.png') }).catch(() => {});
  await push({ running: true });
  const cr3 = await ringOf();
  await push({ running: false, idleSince: Date.now() - 2 * 3600e3 });
  const cr4 = await ringOf();
  check('claudeOptCacheRing', cr1.cls === 'cache-fresh' && cr1.display !== 'none' && /^66\.[67],? 100$/.test(cr1.dash) && /\n提示快取還有約 40 分鐘/.test(cr1.title) && cr1.ring === '20px'
    && cr2.cls === 'cache-low' && cr2.stroke !== cr1.stroke && /約 5 分鐘/.test(cr2.title)
    && cr3.cls === '' && cr3.display === 'none' && !/提示快取/.test(cr3.title)
    && cr4.cls === 'cache-expired' && cr4.dash === '' && cr4.cssDash !== 'none' && /已經過期（閒置 2 小時）/.test(cr4.title), { cr1, cr2, cr3, cr4 });

  // 閒置超過一小時、上下文大的 Claude 對話：送出前先問；取消不送，照樣送出才送；勾「以後不再提醒」關掉設定、之後不再問
  await push({ idleSince: Date.now() - 2 * 3600e3, context: { used: 150000, window: 1000000, percent: 15, autoCompactAt: null, severity: 'ok' },
    coldResume: { perMillion: 40, minPercent: 5 } });
  const sendCount = () => host.received.filter((m) => m.cmd === 'send').length;
  const typeAndSend = async (text) => {
    await page.fill(`${sel} .cbox textarea`, text);
    await page.press(`${sel} .cbox textarea`, 'Enter');
  };
  const before = sendCount();
  await typeAndSend('接著把報表做完');
  await page.waitForSelector('.dlg-back:not(.leaving) .dlg', { timeout: 3000 });
  const warn = await page.evaluate(() => {
    const d = document.querySelector('.dlg-back:not(.leaving) .dlg');
    return { title: d.querySelector('.dlg-title').textContent, msg: d.querySelector('.confirm-msg').textContent, check: !!d.querySelector('.check-row input'),
      ok: d.querySelector('.dlg-foot .btn.primary').textContent };
  });
  await page.screenshot({ path: outFile('claudeopt-cachewarn-dark.png') });
  await page.click('.dlg-back:not(.leaving) .dlg .dlg-foot .btn:first-child');
  await settle(page, 200);
  const afterCancel = { sends: sendCount() - before, draft: await page.inputValue(`${sel} .cbox textarea`) };
  check('claudeOptCacheWarn', warn.title === '快取已經過期' && /閒置 2 小時/.test(warn.msg) && /約 150K tokens/.test(warn.msg) && /約用掉 5 小時額度的 6%/.test(warn.msg) && warn.check && warn.ok === '照樣送出'
    && afterCancel.sends === 0 && afterCancel.draft === '接著把報表做完', { warn, afterCancel });
  await page.press(`${sel} .cbox textarea`, 'Enter');
  await page.waitForSelector('.dlg-back:not(.leaving) .dlg', { timeout: 3000 });
  await page.click('.dlg-back:not(.leaving) .dlg .check-row');
  await page.click('.dlg-back:not(.leaving) .dlg .dlg-foot .btn.primary');
  const sent = await until(() => host.last('send', (m) => m.text === '接著把報表做完'), 2000);
  const off = await until(() => host.last('setting', (m) => m.key === 'claudeIdleResumeWarning'), 2000);
  await settle(page, 300);
  await push({ running: false, idleSince: Date.now() - 3 * 3600e3 });
  const before2 = sendCount();
  await typeAndSend('再補一個圖表');
  const sent2 = await until(() => (sendCount() > before2 ? true : null), 2000);
  const noDialog = await page.evaluate(() => !document.querySelector('.dlg-back:not(.leaving) .dlg'));
  check('claudeOptCacheWarnSendAndOff', !!sent && !!off && off.value === false && !!sent2 && noDialog, { sent: !!sent, off, sent2, noDialog });
  await closeApp(app);

  // 淺色：輸入列與目標晶片
  const light = await openApp('claudeopt-light', { vp, layout: onePane('c1'), settings: { theme: 'light' }, colorScheme: 'light' });
  await light.page.waitForSelector(`${sel}:not(.loading) .items`, { state: 'attached', timeout: 8000 });
  const lc = light.host.conv('c1');
  lc.header = Object.assign({}, lc.header, { running: false, status: null, agent: 'Explore', agents: AGENTS, outputStyle: 'Concise', outputStyles: STYLES, goal: { condition: '把所有測試修到全綠' } });
  await light.host.pushHeader('c1');
  await settle(light.page, 200);
  const ls = await light.page.evaluate(composerState, sel);
  check('claudeOptLight', !!ls.agent && ls.agent.label === 'Explore' && !!ls.style && ls.style.label === '精簡', ls);
  await light.page.locator(`${sel} .ch`).screenshot({ path: outFile('claudeopt-head-light.png') });
  await light.page.locator(`${sel} .dock`).screenshot({ path: outFile('claudeopt-dock-light.png') });
  await closeApp(light);
}
