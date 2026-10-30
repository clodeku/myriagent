// 儀表板的 MCP（web-protocol 的 mcp／mcpCheck／mcpAction；Web/mcp.js、Web/mcprules.js）的情境：probe.mjs 匯入，--mcp 只跑它，完整執行時也跑。
// 檢查名稱 mcp*：切到儀表板送 mcpCheck、沒有結果時不顯示、Claude 帳戶卡的一行（等級、摘要、不超出卡片、在健康度那一行下面）、
// Claude 與 Codex 都有、明細對話框（每個伺服器的狀態與來源、錯誤、按鈕）、授權與重新連線送 mcpAction、「重新檢查」帶 force、授權中與檢查中。
// 截圖 mcp-*.png

const NOW = Date.now();

/** 假的 mcp（形狀見 web-protocol 的 mcp） */
export function makeMcp(o = {}) {
  return {
    accounts: Object.assign({
      a1: {
        checking: false, checkedAt: NOW - 2 * 60e3, error: null, authorizing: null, safeMode: false,
        servers: [
          { name: 'plugin:playwright:playwright', status: 'connected', source: 'plugin', transport: 'stdio', tools: 25 },
          { name: 'plugin:context7:context7', status: 'needs-auth', source: 'plugin', transport: 'http' },
          { name: 'higgsfield', status: 'connected', source: 'user', transport: 'http', tools: 185 },
          { name: 'broken', status: 'failed', source: 'user', transport: 'stdio', error: 'Connection closed' },
          { name: 'claude.ai Claude Docs', status: 'connected', source: 'claudeai', transport: 'claudeai-proxy', tools: 8 },
        ],
      },
      a2: { checking: false, checkedAt: NOW - 60e3, error: null, authorizing: null, safeMode: false, servers: [{ name: 'github', status: 'connected', source: 'user', tools: 40 }] },
      a3: { checking: false, checkedAt: NOW, error: null, authorizing: null, safeMode: false, servers: [{ name: 'codex-mcp', status: 'needs-auth', source: 'user', transport: 'http' }] },
    }, o),
  };
}

/** 帳戶卡上的 MCP 一行：看不看得到、等級、文字、在卡片裡、在健康度那一行下面 */
function stripState(id) {
  const card = Array.from(document.querySelectorAll('#dashboard .acard')).find((c) => c.dataset.key === id);
  if (!card) return null;
  const b = card.querySelector('.acard-mcp');
  if (!b) return { exists: false };
  const r = b.getBoundingClientRect();
  const cr = card.getBoundingClientRect();
  const health = card.querySelector('.acard-health');
  const cs = getComputedStyle(b);
  return {
    exists: true, shown: cs.display !== 'none' && r.height > 0, level: b.dataset.level || null, all: b.textContent,
    label: (b.querySelector('.ah-label') || {}).textContent || null, badge: (b.querySelector('.ah-badge') || {}).textContent || null,
    text: (b.querySelector('.ah-text') || {}).textContent || null, spin: !!b.querySelector('.ah-spin'),
    inside: r.left >= cr.left - 0.5 && r.right <= cr.right + 0.5, height: Math.round(r.height),
    afterHealth: !!health && !!(health.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING),
    color: getComputedStyle(b.querySelector('.ah-badge') || b).color, border: cs.borderTopStyle,
  };
}

/** 明細對話框：標題、摘要、每個伺服器（名稱、等級、狀態與來源、說明、按鈕）、底部按鈕 */
function dialogState() {
  const d = document.querySelector('.dlg-mcp');
  if (!d || d.closest('.leaving')) return null;
  const refresh = d.querySelector('.dlg-foot .btn.ghost') || Array.from(d.querySelectorAll('button')).find((b) => /重新檢查|檢查中/.test(b.textContent));
  return {
    title: d.querySelector('.dlg-title').textContent,
    summary: (d.querySelector('.hd-summary') || {}).textContent || null,
    meta: (d.querySelector('.hd-meta') || {}).textContent || null,
    items: Array.from(d.querySelectorAll('.hd-item')).map((it) => ({
      name: it.dataset.server, level: it.dataset.level, value: it.querySelector('.hd-item-value').textContent,
      detail: Array.from(it.querySelectorAll('.hd-item-detail p')).map((p) => p.textContent),
      buttons: Array.from(it.querySelectorAll('.hd-item-actions button')).map((b) => [b.dataset.action, b.textContent, b.disabled]),
      overflow: it.scrollWidth > it.clientWidth + 1,
    })),
    foot: (d.querySelector('.hd-foot') || {}).textContent || null,
    refresh: refresh ? { text: refresh.textContent, disabled: refresh.disabled } : null,
  };
}

export async function runMcp(ctx, vp) {
  const { openApp, closeApp, check, until, settle, outFile } = ctx;
  const onePane = (id, view) => ({ panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: false, view });
  const app = await openApp('mcp', { vp, layout: onePane('c1', 'dashboard') });
  const { page, host } = app;

  // 一開始就在儀表板：送 mcpCheck（不帶 force）；還沒有結果時不顯示 MCP 那一行
  const first = await until(() => host.last('mcpCheck'), 3000);
  check('mcpCheckOnDashboard', !!first && first.force !== true, first);
  const before = await page.evaluate(stripState, 'a1');
  check('mcpHiddenWithoutData', !!before && before.exists && !before.shown, before);

  await host.send({ type: 'mcp', mcp: makeMcp() });
  await settle(page, 200);
  const a1 = await page.evaluate(stripState, 'a1');
  const a2 = await page.evaluate(stripState, 'a2');
  const a3 = await page.evaluate(stripState, 'a3');
  check('mcpStripNoNullText', [a1, a2].every((s) => !/null|undefined/.test(s.all)), [a1.all, a2.all]);
  check('mcpStripBad', a1.shown && a1.level === 'bad' && a1.label === 'MCP' && a1.badge === '有問題'
    && a1.text === '3/5 個已連線・1 個要授權・1 個連不上' && a1.inside && a1.afterHealth && !a1.spin, a1);
  check('mcpStripOk', a2.shown && a2.level === 'ok' && a2.badge === '正常' && a2.text === '1/1 個已連線' && a2.color !== a1.color, a2);
  check('mcpStripOnCodex', !!a3 && a3.shown && a3.level === 'warn' && a3.text.includes('要授權'), a3);
  await page.click('#dashboard .acard[data-key="a3"] .acard-mcp');
  await page.click('.dlg-mcp .hd-item[data-server="codex-mcp"] button[data-action="authenticate"]');
  const codexAuth = await until(() => host.last('mcpAction', m => m.accountId === 'a3'), 2000);
  check('mcpCodexAuth', !!codexAuth && codexAuth.action === 'authenticate' && codexAuth.server === 'codex-mcp');
  await page.keyboard.press('Escape');
  await settle(page, 100);
  await page.locator('#dashboard .acard[data-key="a1"]').screenshot({ path: outFile('mcp-card-dark.png') });

  // 明細：每個伺服器一項；要授權的有「授權…」、連不上與已連線的有「重新連線」
  await page.click('#dashboard .acard[data-key="a1"] .acard-mcp');
  const dlg = await until(() => page.evaluate(dialogState), 2000);
  check('mcpDialog', !!dlg && dlg.title === '「工作用 Max」的 MCP 伺服器' && dlg.summary === '3/5 個已連線・1 個要授權・1 個連不上'
    && /檢查於/.test(dlg.meta) && dlg.items.length === 5
    && dlg.items[1].name === 'plugin:context7:context7' && dlg.items[1].level === 'warn' && dlg.items[1].value === '需要授權・外掛'
    && dlg.items[1].buttons.length === 1 && dlg.items[1].buttons[0][0] === 'authenticate' && dlg.items[1].buttons[0][1] === '授權…'
    && dlg.items[3].level === 'bad' && dlg.items[3].detail[0] === 'Connection closed' && dlg.items[3].buttons[0][0] === 'reconnect'
    && dlg.items[2].detail[0] === '185 個工具' && dlg.items[4].value === '已連線・claude.ai 連接器'
    && dlg.items.every((it) => !it.overflow) && /逐資料夾/.test(dlg.foot) && dlg.refresh && dlg.refresh.text === '重新檢查', dlg);
  await settle(page, 200);
  await page.screenshot({ path: outFile('mcp-dialog-dark.png') });

  await page.click('.dlg-mcp .hd-item[data-server="plugin:context7:context7"] button[data-action="authenticate"]');
  const auth = await until(() => host.last('mcpAction', (m) => m.action === 'authenticate'), 2000);
  check('mcpAuthenticateSent', !!auth && auth.accountId === 'a1' && auth.server === 'plugin:context7:context7', auth);
  await page.click('.dlg-mcp .hd-item[data-server="broken"] button[data-action="reconnect"]');
  const rec = await until(() => host.last('mcpAction', (m) => m.action === 'reconnect'), 2000);
  check('mcpReconnectSent', !!rec && rec.accountId === 'a1' && rec.server === 'broken', rec);

  // 授權中：那一項寫「授權中…」、按鈕都停用、帳戶卡那一行轉圈、「重新檢查」停用
  await host.send({ type: 'mcp', mcp: makeMcp({ a1: Object.assign({}, makeMcp().accounts.a1, { authorizing: 'plugin:context7:context7' }) }) });
  await settle(page, 200);
  const busy = await page.evaluate(dialogState);
  const busyStrip = await page.evaluate(stripState, 'a1');
  check('mcpAuthorizing', !!busy && busy.items[1].buttons[0][1] === '授權中…' && busy.items.every((it) => it.buttons.every((b) => b[2]))
    && busy.refresh.disabled && busyStrip.spin, { busy, busyStrip });
  await page.screenshot({ path: outFile('mcp-authorizing-dark.png') });

  // 「重新檢查」帶 force；檢查中按鈕寫「檢查中…」
  await host.send({ type: 'mcp', mcp: makeMcp() });
  await settle(page, 150);
  await page.click('.dlg-mcp .dlg-foot .btn.ghost');
  const forced = await until(() => host.last('mcpCheck', (m) => m.force === true), 2000);
  await host.send({ type: 'mcp', mcp: makeMcp({ a1: Object.assign({}, makeMcp().accounts.a1, { checking: true }) }) });
  await settle(page, 150);
  const checking = await page.evaluate(dialogState);
  check('mcpRefreshForce', !!forced && checking.refresh.disabled && checking.refresh.text === '檢查中…', { forced, checking });

  // 精簡模式與查不到：不是錯誤的一行、明細寫原因
  await host.send({ type: 'mcp', mcp: makeMcp({ a2: { checking: false, checkedAt: null, error: null, authorizing: null, safeMode: true, servers: null } }) });
  await settle(page, 200);
  const safe = await page.evaluate(stripState, 'a2');
  check('mcpSafeMode', safe.shown && safe.level === 'info' && safe.text === '精簡模式不載入 MCP' && !safe.badge, safe);
  await closeApp(app);

  // 淺色
  const light = await openApp('mcp-light', { vp, layout: onePane('c1', 'dashboard'), settings: { theme: 'light' }, colorScheme: 'light' });
  await until(() => light.host.last('mcpCheck'), 3000);
  await light.host.send({ type: 'mcp', mcp: makeMcp() });
  await settle(light.page, 200);
  const la1 = await light.page.evaluate(stripState, 'a1');
  check('mcpStripLight', la1.shown && la1.level === 'bad', la1);
  await light.page.locator('#dashboard .acard[data-key="a1"]').screenshot({ path: outFile('mcp-card-light.png') });
  await closeApp(light);
}
