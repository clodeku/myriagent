// 延伸成新對話（2026-10-25；web-protocol 的 forkConversation、workspace.forkConversation、items.js 的 noticeKind fork）的情境：
// probe.mjs 匯入，--fork 只跑它，完整執行時也跑。
// 檢查名稱 fork*：標頭「⋯」選單的「延伸成新對話」（草稿沒有）、送出的記號、回覆到了才在原對話的窗格旁邊開新窗格並聚焦（原對話留在原窗格）、
// 新對話的提示（分支圖示、同 cliUpdate 的框）、側欄右鍵選單也有、宿主失敗時不留空窗格、窗格滿了就開在原窗格。
// 截圖 fork-*.png

function menuTexts() {
  const m = document.querySelector('.menu');
  return m ? Array.from(m.querySelectorAll('.menu-item')).map((b) => b.textContent) : null;
}

/** 各窗格（依畫面順序）：裡面的對話、看得到的那個、是不是聚焦的 */
function panesState() {
  return Array.from(document.querySelectorAll('.ws > .pane')).map((p) => {
    const convs = Array.from(p.querySelectorAll('.pane-body > .conv'));
    const shown = convs.find((c) => !c.hidden);
    return { id: p.dataset.pane, focused: p.classList.contains('focused'), convs: convs.map((c) => c.dataset.conv), shown: shown ? shown.dataset.conv : null };
  });
}

export async function runFork(ctx, vp) {
  const { openApp, closeApp, check, until, settle, outFile } = ctx;
  const onePane = (id) => ({ panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' });
  const sel = '.conv[data-conv="c1"]';

  const app = await openApp('fork', { vp, layout: onePane('c1') });
  const { page, host } = app;
  await page.waitForSelector(`${sel}:not(.loading) .items`, { state: 'attached', timeout: 8000 });

  // 標頭「⋯」選單有「延伸成新對話」
  await page.click(`${sel} .ch-top [aria-label="更多動作"], ${sel} .ch-top button[title="更多動作"]`);
  const items = await until(() => page.evaluate(menuTexts), 2000);
  check('forkMenuEntry', !!items && items.some((t) => t.includes('延伸成新對話')), items);
  await settle(page, 200);
  await page.screenshot({ path: outFile('fork-menu-dark.png') });
  // 第十批：新的窗格一加上 fx-fork-in 就記下它的動畫（滑進來、靠原窗格那一邊的分支線；播完會拿掉 class）
  await page.evaluate(() => {
    window.__forkFx = null;
    new MutationObserver((recs) => {
      for (const r of recs) {
        const el = r.target;
        if (window.__forkFx || !el.classList || !el.classList.contains('fx-fork-in')) continue;
        window.__forkFx = { pane: el.dataset.pane, anims: el.getAnimations().map((a) => a.animationName), line: getComputedStyle(el, '::before').animationName };
      }
    }).observe(document.body, { attributes: true, attributeFilter: ['class'], subtree: true });
  });
  await page.locator('.menu .menu-item', { hasText: '延伸成新對話' }).click();

  // 送出的指令帶網頁自訂的記號；回覆到了才在原對話的窗格旁邊開新窗格、聚焦它，原對話留在原窗格
  const sent = await until(() => host.last('forkConversation'), 2000);
  check('forkCommandSent', !!sent && sent.conversationId === 'c1' && typeof sent.paneId === 'string' && sent.paneId.startsWith('fork-'), sent);
  const forkId = await until(() => {
    const ids = Object.keys(host.data).filter((k) => /^f\d+$/.test(k));
    return ids.length ? ids[ids.length - 1] : null;
  }, 2000);
  await page.waitForSelector(`.conv[data-conv="${forkId}"]:not(.loading) .items`, { state: 'attached', timeout: 8000 });
  await settle(page, 300);
  const panes = await page.evaluate(panesState);
  check('forkOpensBesideOriginal', panes.length === 2 && panes[0].shown === 'c1' && panes[1].shown === forkId && panes[1].focused && panes[1].convs.length === 1, panes);
  const forkFx = await page.evaluate(() => window.__forkFx);
  check('forkPaneSlidesIn', !!forkFx && forkFx.pane === panes[1].id && forkFx.anims.includes('fx-fork-in') && forkFx.line === 'fx-glow-out', forkFx);
  // 第十批：延伸出來的對話標頭寫「延伸自「原對話」」，點了開啟原對話
  const origin = await until(() => page.evaluate((id) => {
    const b = document.querySelector(`.conv[data-conv="${id}"] .ch-origin`);
    return b && !b.hidden ? { text: b.textContent, kind: b.dataset.kind, disabled: b.disabled } : null;
  }, forkId), 2000);
  check('forkOriginChip', !!origin && origin.kind === 'fork' && !origin.disabled && origin.text === '延伸自「修正登入流程卡住並補上單元測試」', origin);

  // 新對話的開頭說明：noticeKind fork（分支圖示、同 cliUpdate 的框）
  const note = await page.evaluate((id) => {
    const n = document.querySelector(`.conv[data-conv="${id}"] .it-notice.forked`);
    if (!n) return null;
    const cs = getComputedStyle(n);
    return { text: n.textContent, icon: !!n.querySelector('.note-icwrap svg'), border: cs.borderTopStyle, lastChild: n === n.parentElement.lastElementChild };
  }, forkId);
  check('forkNoticeStyled', !!note && note.icon && /延伸出來的新對話/.test(note.text) && note.border !== 'none', note);
  const title = await page.evaluate((id) => { const t = document.querySelector(`.conv[data-conv="${id}"] .ch-title`); return t ? t.textContent : null; }, forkId);
  check('forkTitle', !!title && title.endsWith('（延伸）'), title);
  await page.screenshot({ path: outFile('fork-two-panes-dark.png') });

  // 側欄右鍵選單也有；選了之後一樣開在旁邊（原對話在第一格）
  const row = page.locator('.crow', { hasText: '產生三張儀表板示意圖' }).first();
  await row.click({ button: 'right' });
  const rowItems = await until(() => page.evaluate(menuTexts), 2000);
  check('forkSidebarMenuEntry', !!rowItems && rowItems.some((t) => t.includes('延伸成新對話')), rowItems);
  await page.keyboard.press('Escape');
  await settle(page, 150);

  // 宿主失敗（只送 toast）：不留空窗格
  host.opts.forkFail = true;
  const before = (await page.evaluate(panesState)).length;
  await page.click(`${sel} .ch-top button[title="更多動作"], ${sel} .ch-top [aria-label="更多動作"]`);
  await until(() => page.evaluate(menuTexts), 2000);
  await page.locator('.menu .menu-item', { hasText: '延伸成新對話' }).click();
  await until(() => host.received.filter((m) => m.cmd === 'forkConversation').length >= 2 ? true : null, 2000);
  await settle(page, 400);
  const after = await page.evaluate(panesState);
  const toast = await page.evaluate(() => Array.from(document.querySelectorAll('.toast')).map((t) => t.textContent).join(' | '));
  check('forkFailNoEmptyPane', after.length === before && /延伸失敗/.test(toast), { before, after, toast });
  host.opts.forkFail = false;

  // 草稿沒有這一項
  await page.click('.ws > .pane[data-pane="p1"] .strip-actions button[title^="在這個窗格開新對話"]');
  const draftId = await until(() => {
    const ids = Object.keys(host.data).filter((k) => /^d\d+$/.test(k));
    return ids.length ? ids[ids.length - 1] : null;
  }, 2000);
  await page.waitForSelector(`.conv[data-conv="${draftId}"]:not(.loading)`, { state: 'attached', timeout: 8000 });
  await page.click(`.conv[data-conv="${draftId}"] .ch-top button[title="更多動作"], .conv[data-conv="${draftId}"] .ch-top [aria-label="更多動作"]`);
  const draftItems = await until(() => page.evaluate(menuTexts), 2000);
  check('forkNotForDraft', !!draftItems && !draftItems.some((t) => t.includes('延伸成新對話')), draftItems);
  await page.keyboard.press('Escape');
  await closeApp(app);

  // 窗格滿了（12 格）：開在原對話的窗格
  const ids = ['c1', 'c2', 'c3', 'c4', 'c5', 'c6', 'c7', 'c8', 'c9', 'c10', 'c11', 'c12'];
  const full = { panes: ids.map((id, i) => ({ id: 'p' + (i + 1), tabs: [id], active: id })), focusedPane: 'p1', sidebarCollapsed: true, view: 'chat' };
  const app2 = await openApp('fork-full', { vp, layout: full });
  const p2 = app2.page;
  await p2.waitForSelector('.conv[data-conv="c4"]:not(.loading) .items', { state: 'attached', timeout: 8000 });
  const count0 = (await p2.evaluate(panesState)).length;
  await p2.click('.conv[data-conv="c4"] .ch-top button[title="更多動作"], .conv[data-conv="c4"] .ch-top [aria-label="更多動作"]');
  await until(() => p2.evaluate(menuTexts), 2000);
  await p2.locator('.menu .menu-item', { hasText: '延伸成新對話' }).click();
  const fullFork = await until(() => {
    const f = Object.keys(app2.host.data).filter((k) => /^f\d+$/.test(k));
    return f.length ? f[f.length - 1] : null;
  }, 2000);
  await p2.waitForSelector(`.conv[data-conv="${fullFork}"]`, { state: 'attached', timeout: 8000 });
  await settle(p2, 300);
  const fullPanes = await p2.evaluate(panesState);
  const holder = fullPanes.find((p) => p.convs.includes(fullFork));
  check('forkFullPanesSamePane', fullPanes.length === count0 && !!holder && holder.convs.includes('c4') && holder.shown === fullFork, { count0, holder });
  await closeApp(app2);
}
