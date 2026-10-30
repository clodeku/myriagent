// 多人協作的網頁探針情境（docs/plans/2026-10-30-collaborators.md 第 4 節；live-probe.mjs 以 ctx 呼叫）：
// - 協作人模式（init.viewer.role = collaborator）：只有對話，擁有者的功能都藏起來、對話的設定不能改、訊息標出是誰送的、設定頁只有「我的帳號」。
// - 擁有者：對話標頭的協作晶片、「⋯」選單的「協作人…」、分享對話框送 collabShare、設定頁的「協作人」新增送 collabCreate。
import * as LF from './live-fixtures.mjs';

const COLLABORATORS = {
  list: [
    { id: 'u1', userName: 'ming', displayName: '小明', disabled: false, createdAt: 1, lastLoginAt: null, online: true },
    { id: 'u2', userName: 'hua', displayName: '小華 ' + LF.XSS, disabled: true, createdAt: 2, lastLoginAt: null, online: false },
  ],
  shares: {},
};

/** 對話的項目：擁有者送的、協作人送的、協作人回應的核准卡 */
function collabItems(now) {
  const t = (minAgo) => now - minAgo * 60_000;
  return [
    { id: 'c1', seq: 1, kind: 'User', status: 'Done', time: t(9), html: '<p>擁有者交代的工作</p>', attachments: [] },
    { id: 'c2', seq: 2, kind: 'User', status: 'Done', time: t(8), html: '<p>協作人補充的需求</p>', attachments: [], author: '小明' },
    { id: 'c3', seq: 3, kind: 'Approval', status: 'Done', time: t(7), toolKind: 'bash', toolName: '執行指令', title: 'npm test', decision: 'allow', resolvedBy: '小明' },
    { id: 'c4', seq: 4, kind: 'Assistant', status: 'Done', time: t(6), html: '<p>完成了。</p>' },
  ];
}

export async function runCollaboratorView(ctx) {
  const { H, HP, NOW, check, shot, until, convSel, openMore, closeMenu, serveOpen } = ctx;
  const id = LF.CONV.idle;
  const list = LF.liveList(NOW).filter((c) => c.id === id);
  const app = await H.openHub('collab-view', {
    init: (ready) => Object.assign(LF.liveBrowserInit(NOW, { mode: ready && ready.mode, conversations: list, accounts: [], devices: [] }), {
      viewer: { role: 'collaborator', name: '小明', userName: 'ming' }, collaborators: null, tokens: null, schedules: null, devicesCli: {},
    }),
    hub: (hub) => serveOpen(hub, { items: () => collabItems(NOW) }),
  });
  const { page, hub } = app;
  await HP.waitLive(app);
  const chrome = await page.evaluate(() => {
    const shown = (el) => !!el && el.offsetParent !== null && getComputedStyle(el).display !== 'none';
    return {
      collab: document.body.classList.contains('viewer-collab'),
      chat: !document.getElementById('workspace').hidden,
      dashboard: !document.getElementById('dashboard').hidden,
      viewSwitch: shown(document.getElementById('viewSwitch')),
      accounts: shown(document.querySelector('.sb-sec-accts')),
      newButtons: [...document.querySelectorAll('.sb-sec-hist .sb-head .ibtn, .strip-actions .ibtn')].filter((b) => shown(b) && /新對話/.test(b.title || '')).length,
      monitor: shown(document.getElementById('btnMonitor')),
      rows: [...document.querySelectorAll('.sb-list .crow')].length,
    };
  });
  check('collabChrome', chrome.collab && chrome.chat && !chrome.dashboard && !chrome.viewSwitch && !chrome.accounts && chrome.newButtons === 0 && !chrome.monitor && chrome.rows === 1, chrome);

  await page.locator('.sb-list .crow').first().click();
  await until(() => page.evaluate((s) => !!document.querySelector(s + ' .it-user'), convSel(id)), 4000);
  const labels = await page.evaluate((s) => ({
    authors: [...document.querySelectorAll(s + ' .it-user .u-author')].map((n) => n.textContent),
    resolved: [...document.querySelectorAll(s + ' .resolved-by')].map((n) => n.textContent),
  }), convSel(id));
  check('collabAuthorLabels', JSON.stringify(labels.authors) === JSON.stringify(['管理員', '小明']) && JSON.stringify(labels.resolved) === JSON.stringify(['小明 回應']), labels);

  const menu = await openMore(app, id);
  await closeMenu(app);
  const labelsOf = menu.filter((x) => x.label).map((x) => x.label);
  // 帳戶晶片點了沒有選單、模型選單也打不開
  await page.click(convSel(id) + ' .ch-acct');
  await ctx.sleep(150);
  const acctMenu = await page.evaluate(() => !!document.querySelector('.menu'));
  check('collabMenusLocked', !labelsOf.includes('重新命名') && !labelsOf.some((l) => l.startsWith('刪除')) && !labelsOf.includes('協作人…') && labelsOf.includes('瀏覽工作資料夾') && !acctMenu,
    { menu: labelsOf, acctMenu });

  // Ctrl+N 不開新對話
  const opens = hub.cmds('newConversation').length;
  await page.keyboard.press('Control+n');
  await ctx.sleep(200);
  check('collabNoNewConversation', hub.cmds('newConversation').length === opens, { newConversation: hub.cmds('newConversation').length - opens });
  await shot(app, 'collab-view-1440');

  await page.click('#btnSettings');
  await page.waitForSelector('.dlg-settings');
  const tabs = await page.evaluate(() => [...document.querySelectorAll('.dlg-settings [role="tab"]')].map((t) => t.textContent.trim()));
  check('collabSettingsTabs', tabs.includes('我的帳號') && !tabs.includes('協作人') && !tabs.includes('多電腦同步'), { tabs });
  await page.locator('.dlg-settings [role="tab"]', { hasText: '我的帳號' }).click();
  await ctx.sleep(150);
  const account = await page.evaluate(() => ({
    label: document.querySelector('[data-sec="account"] .set-label')?.textContent,
    user: document.querySelector('[data-sec="account"] input[autocomplete="username"]')?.value,
    pass: document.querySelectorAll('[data-sec="account"] input[type="password"]').length,
  }));
  await shot(app, 'collab-settings-account');
  check('collabMyAccount', account.label === '小明' && account.user === 'ming' && account.pass === 3, account);
  const errors = HP.unexpectedErrors(app);
  check('collabNoErrors', errors.length === 0 && app.csp.length === 0, { errors, csp: app.csp });
  await HP.closePage(app);
}

export async function runOwnerCollaborators(ctx) {
  const { H, HP, NOW, check, shot, until, convSel, openMore, clickMenu, serveOpen } = ctx;
  const id = LF.CONV.idle;
  const collaborators = JSON.parse(JSON.stringify(COLLABORATORS));
  collaborators.shares[id] = ['u1'];
  const app = await H.openHub('collab-owner', {
    init: (ready) => Object.assign(LF.liveBrowserInit(NOW, { mode: ready && ready.mode, layout: { panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' } }), {
      viewer: { role: 'owner' }, collaborators,
    }),
    hub: (hub) => {
      serveOpen(hub, { items: () => collabItems(NOW) });
      hub.onCmd('collabShare', (cmd) => ({ push: [{ type: 'collabResult', requestId: cmd.requestId, cmd: 'collabShare', ok: true }] }));
      hub.onCmd('collabCreate', (cmd) => ({ push: [{ type: 'collabResult', requestId: cmd.requestId, cmd: 'collabCreate', ok: true, userId: 'u3' }] }));
    },
  });
  const { page, hub } = app;
  await HP.waitLive(app);
  await until(() => page.evaluate((s) => !!document.querySelector(s + ' .it-user'), convSel(id)), 4000);
  const chip = await page.evaluate((s) => {
    const c = document.querySelector(s + ' .ch-collab');
    return c ? { hidden: c.hidden, text: c.querySelector('.chip-label').textContent, title: c.title } : null;
  }, convSel(id));
  const authors = await page.evaluate((s) => [...document.querySelectorAll(s + ' .it-user .u-author')].map((n) => n.textContent), convSel(id));
  check('ownerCollabChipAndAuthor', !!chip && !chip.hidden && chip.text === '小明' && JSON.stringify(authors) === JSON.stringify(['協作人 小明']), { chip, authors });

  const menu = await openMore(app, id);
  const share = menu.find((x) => x.label === '協作人…');
  await clickMenu(app, '協作人…');
  await page.waitForSelector('.dlg-collab-share');
  const rows = await page.evaluate(() => [...document.querySelectorAll('.dlg-collab-share .collab-share-row')].map((r) => ({
    name: r.querySelector('.collab-name span').textContent, checked: r.querySelector('input').checked, off: r.classList.contains('is-off'),
  })));
  await shot(app, 'collab-owner-share');
  await page.locator('.dlg-collab-share .collab-share-row').nth(0).locator('input').uncheck();
  await page.locator('.dlg-collab-share .collab-share-row').nth(1).locator('input').check();
  await page.click('.dlg-collab-share .btn.primary');
  const sent = await until(() => hub.cmds('collabShare').at(-1) || null, 3000);
  const closed = await until(() => page.evaluate(() => !document.querySelector('.dlg-collab-share')), 3000);
  const xss = await HP.xssState(app);
  check('ownerShareDialog', !!share && rows.length === 2 && rows[0].checked && !rows[1].checked && rows[1].off && rows[1].name === '小華 ' + LF.XSS
    && !!sent && sent.cmd.conversationId === id && JSON.stringify(sent.cmd.userIds) === JSON.stringify(['u2']) && closed && xss.xss === 0,
  { share: !!share, rows, sent: sent && sent.cmd, closed, xss });

  // 伺服器推新的分享：晶片跟著換
  await hub.push({ type: 'collaborators', collaborators: Object.assign({}, collaborators, { shares: { [id]: ['u1', 'u2'] } }) });
  const chip2 = await until(() => page.evaluate((s) => { const c = document.querySelector(s + ' .ch-collab .chip-label'); return c && c.textContent === '2 位協作人' ? c.textContent : null; }, convSel(id)), 3000);
  check('ownerCollabChipUpdates', chip2 === '2 位協作人', { chip2 });

  // 設定頁的「協作人」：清單兩位、新增送 collabCreate（帳號轉小寫、密碼不留在框裡）
  await page.click('#btnSettings');
  await page.waitForSelector('.dlg-settings');
  await page.locator('.dlg-settings [role="tab"]', { hasText: '協作人' }).click();
  await ctx.sleep(150);
  const listed = await page.evaluate(() => [...document.querySelectorAll('.collab-row .collab-name span:first-child')].map((n) => n.textContent));
  await shot(app, 'collab-owner-settings');
  await page.click('.collab-card .btn.acc');
  await page.waitForSelector('.dlg-collab');
  const inputs = page.locator('.dlg-collab input.input');
  await inputs.nth(0).fill('  NewOne ');
  await inputs.nth(1).fill('新人');
  await inputs.nth(2).fill('newpass123');
  await page.click('.dlg-collab .btn.primary');
  const created = await until(() => hub.cmds('collabCreate').at(-1) || null, 3000);
  const createdClosed = await until(() => page.evaluate(() => !document.querySelector('.dlg-collab')), 3000);
  check('ownerCreateCollaborator', JSON.stringify(listed) === JSON.stringify(['小明', '小華 ' + LF.XSS]) && !!created && created.cmd.userName === 'newone'
    && created.cmd.displayName === '新人' && created.cmd.password === 'newpass123' && createdClosed,
  { listed, created: created && Object.assign({}, created.cmd, { password: created.cmd.password ? '***' : null }), createdClosed });
  const errors = HP.unexpectedErrors(app);
  check('ownerCollabNoErrors', errors.length === 0 && app.csp.length === 0, { errors, csp: app.csp });
  await HP.closePage(app);
}
