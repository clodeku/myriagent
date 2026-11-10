// Codex 整合的介面契約：真網頁、假宿主，不會呼叫模型或更改使用者的設定。
export async function runCodex(ctx, vp) {
  const { openApp, closeApp, check, until, settle, outFile } = ctx;
  for (const theme of ['dark', 'light']) {
    const app = await openApp('codex-' + theme, { vp, layout: { panes: [{ id: 'p1', tabs: ['c2'], active: 'c2' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' }, settings: { theme }, colorScheme: theme });
    const { page, host } = app;
    const sel = '.conv[data-conv="c2"]';
    await page.waitForSelector(sel + ':not(.loading) .items');
    const c = host.conv('c2');
    const goal = { objective: '完成匯出與測試 <img src=x onerror=alert(1)>', status: 'active', tokensUsed: 1200, tokenBudget: 8000, timeUsedSeconds: 65 };
    c.header = { ...c.header, provider: 'Codex', running: false, status: null, sessionStarted: true, codex: { goal, busy: false, selectedSkills: [], serviceTier: null } };
    await host.pushHeader('c2');
    await settle(page, 120);
    let lastRequest;
    const reply = async (action, result, error) => {
      const req = await until(() => host.last('codexControl', m => m.action === action && m.requestId !== lastRequest), 2000);
      if (!req) throw new Error('沒有收到 Codex 指令：' + action);
      lastRequest = req.requestId;
      await host.send({ type: 'codexControl', conversationId: 'c2', requestId: req.requestId, result, error });
      await settle(page, 100);
      return req;
    };
    const open = async label => {
      await page.click(sel + ' .codex-tools');
      await page.locator('.menu .menu-item', { hasText: label }).click();
    };
    await open('規劃模式');
    await reply('inspect', { modes: [{ mode: 'default' }, { mode: 'plan' }] });
    await page.getByLabel('規劃模式', { exact: true }).selectOption('plan');
    await page.getByRole('button', { name: '套用模式', exact: true }).click();
    const mode = await reply('mode', { mode: 'plan' });
    check('codexNativePlan-' + theme, mode.mode === 'plan');
    c.header.codex.mode = 'plan';
    c.header.codex.hasPlan = true;
    await host.pushHeader('c2');
    await page.screenshot({ path: outFile('codex-plan-' + theme + '.png') });
    await page.keyboard.press('Escape');
    check('codexPlanIndicator-' + theme, (await page.locator(sel + ' .codex-tools').innerText()).includes('先規劃'));

    await page.locator(sel + ' .cbox textarea').fill('保留計畫草稿');
    await open('開始執行計畫');
    const executeMode = await reply('mode', { mode: 'default' });
    const execute = await until(() => host.last('send', m => m.text === '請依照上方已確認的計畫開始執行。'), 2000);
    check('codexExecutePlan-' + theme, executeMode.mode === 'default' && !!execute && await page.locator(sel + ' .cbox textarea').inputValue() === '保留計畫草稿');
    c.header.running = false; c.header.codex.mode = 'default';
    await host.pushHeader('c2');

    host.ignore.add('answer');
    const form = { id: 'codex-form', kind: 'Question', status: 'Waiting', time: Date.now(), seq: 900,
      toolName: 'MCP：測試', questions: [{ id: 'n', text: '請填 1 到 5 的整數', allowOther: true, options: [] }], answers: {} };
    c.items.push(form); host.pushItem('c2', form);
    await settle(page, 150);
    const card = page.locator(sel + ' .it-question').last();
    await card.locator('.q-other').fill('9');
    await card.getByRole('button', { name: '送出', exact: true }).click();
    for (const stamp of ['1', '2']) {
      Object.assign(form, { answerError: '不得大於 5', answerErrorAt: stamp });
      host.pushItem('c2', form);
      await settle(page, 100);
      check('codexFormRetry-' + theme + stamp, await card.getByRole('button', { name: '送出', exact: true }).isEnabled() && await card.locator('.q-other').inputValue() === '9');
      if (stamp === '1') await card.getByRole('button', { name: '送出', exact: true }).click();
    }
    form.status = 'Done'; form.answers = { n: ['3'] }; host.pushItem('c2', form);
    host.ignore.delete('answer');
    await open('持續目標');
    await reply('inspect', { goal });
    check('codexGoal-' + theme, /1,200／8,000.*1 分 5 秒/.test(await page.locator('.codex-status').innerText()) && await page.locator('.codex-current img').count() === 0);
    await page.screenshot({ path: outFile('codex-goal-' + theme + '.png') });
    await page.getByRole('button', { name: '暫停目標', exact: true }).click();
    await reply('goalPause', { goal: { ...goal, status: 'paused' } });
    check('codexPause-' + theme, await page.getByRole('button', { name: '繼續目標', exact: true }).isEnabled());
    await page.getByRole('button', { name: '清除目標', exact: true }).click();
    await reply('goalClear', {});
    check('codexClear-' + theme, (await page.locator('.codex-status').innerText()).includes('尚未設定'));
    await page.keyboard.press('Escape');

    await open('指定技能');
    await reply('inspect', { skills: [{ name: '測試技能', path: 'C:/skills/test/SKILL.md', description: '驗證匯出 <img src=x>', enabled: true }, { name: '停用技能', path: 'C:/skills/off/SKILL.md', enabled: false }] });
    await page.locator('.codex-skill input').first().check();
    check('codexDisabledSkill-' + theme, await page.locator('.codex-skill input').nth(1).isDisabled() && await page.locator('.codex-skills img').count() === 0);
    await page.getByRole('button', { name: '套用指定技能', exact: true }).click();
    const selected = await reply('skillSelect', { selectedSkills: [{ name: '測試技能', path: 'C:/skills/test/SKILL.md' }] });
    check('codexSkills-' + theme, selected.paths.length === 1 && selected.paths[0] === 'C:/skills/test/SKILL.md');
    await page.screenshot({ path: outFile('codex-skills-' + theme + '.png') });
    await page.keyboard.press('Escape');

    await open('速度模式');
    await reply('inspect', { tiers: [{ id: 'priority', name: 'Fast' }] });
    const options = await page.locator('select[aria-label="速度模式"] option').allTextContents();
    check('codexSpeedOptions-' + theme, options.includes('標準') && options.includes('Fast') && !options.includes('Ultrafast'));
    await page.getByLabel('速度模式', { exact: true }).selectOption('priority');
    await page.getByRole('button', { name: '套用速度', exact: true }).click();
    const speed = await reply('speed', null, '目前帳戶無法使用這個模式');
    check('codexSpeedError-' + theme, speed.serviceTier === 'priority' && (await page.locator('.codex-error').innerText()).includes('目前帳戶'));
    await page.setViewportSize({ width: 700, height: 700 });
    await page.screenshot({ path: outFile('codex-speed-narrow-' + theme + '.png') });
    const fits = await page.locator('.dlg').evaluate(el => { const r = el.getBoundingClientRect(); return r.left >= 0 && r.right <= innerWidth && el.scrollWidth <= el.clientWidth + 1; });
    check('codexNarrow-' + theme, fits);
    await page.keyboard.press('Escape');
    await page.setViewportSize({ width: vp.w, height: vp.h });

    await page.locator(sel + ' .cbox textarea').fill('保留我的草稿');
    await open('程式碼審查');
    await page.getByLabel('審查範圍').selectOption('baseBranch');
    await page.getByLabel('審查參數').fill('main');
    await page.getByRole('button', { name: '開始審查', exact: true }).click();
    const review = await until(() => host.last('send', m => m.text === '/review base main'), 2000);
    check('codexReview-' + theme, !!review && await page.locator(sel + ' .cbox textarea').inputValue() === '保留我的草稿');
    c.header.running = false;
    await host.pushHeader('c2');
    await settle(page, 100);
    await open('壓縮目前對話');
    const compact = await until(() => host.last('send', m => m.text === '/compact'), 2000);
    check('codexCompact-' + theme, !!compact);
    await closeApp(app);

    const dash = await openApp('codex-doctor-' + theme, { vp, layout: { panes: [{ id: 'p1', tabs: ['c1'], active: 'c1' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'dashboard' }, settings: { theme }, colorScheme: theme });
    await dash.host.send({ type: 'showView', view: 'dashboard' });
    await dash.page.locator('.codex-doctor:visible').first().click();
    await dash.page.getByRole('button', { name: '只檢查連線', exact: true }).click();
    const doctor = await until(() => dash.host.last('codexDoctor'), 2000);
    check('codexDoctorScope-' + theme, !!doctor && doctor.networkOnly === true);
    await dash.host.send({ type: 'codexDoctor', requestId: doctor.requestId, result: { version: '0.167.0', status: 'warning', checks: [
      { id: 'network.test', status: 'warning', summary: '<img src=x onerror=alert(1)>', details: { message: '連線測試'.repeat(80) }, remediation: '檢查連線設定' } ] } });
    await settle(dash.page, 150);
    check('codexDoctorText-' + theme, await dash.page.locator('.codex-doctor-result img').count() === 0 && await dash.page.getByRole('button', { name: '完整診斷', exact: true }).isEnabled());
    await dash.page.setViewportSize({ width: 700, height: 700 });
    check('codexDoctorNarrow-' + theme, await dash.page.locator('.dlg').evaluate(el => el.scrollWidth <= el.clientWidth + 1));
    await dash.page.screenshot({ path: outFile('codex-doctor-' + theme + '.png') });
    await closeApp(dash);
  }
}
