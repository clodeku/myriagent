// 設定對話框的版面、模型設定的代理子分頁與搜尋（Web/settings.js、Web/settingsrules.js、dashboard.css 的設定對話框）：
// probe.mjs 匯入，--settings 只跑它，完整執行時也跑。
// 檢查名稱 settingsNav*、settingsSub*、settingsSearch*、settingsContrast*：
// 左側直排的分類（圖示＋名稱、選取的那一個亮起）與右側的頁首（標題＝分類名稱、一句說明）、各區塊是卡片、每一頁都不水平溢出；
// 窄視窗（700 寬）分類換成上方的橫排、不溢出；模型設定每種代理一個子分頁（標誌、名稱、預設模型的摘要與權限模式的色點）、
// 一次只顯示一家、方向鍵切換、再開回到上次看的那一家、宿主不給某一家的設定時那一家的子分頁不見（選到的不見了換到第一家）；
// 搜尋：Ctrl+F 移到搜尋框、比對名稱與同義詞（dark → 主題）、結果寫出在哪一頁哪一塊並標出命中的字、Enter 跳到第一筆
// （切分頁與代理子分頁、捲到看得見、那一列標一下、焦點到那一列的控制項）、沒有結果的說明（搜尋字不當成 HTML）、Esc 先清搜尋不關對話框；
// 深淺色的文字對比。截圖 settings-nav-*.png、settings-sub-*.png、settings-search-*.png、settings-narrow-*.png。
// 第九批（2026-10-26）：選取標示是一條滑過去的亮線（settingsNavIndicator*：對準選取的分頁、換分頁時 transform 轉場、直排在左緣、橫排在下緣）、
// 搜尋時各分類旁寫命中數、沒有命中的淡掉（settingsSearchTabHits）

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** 對話框的版面：分類在左、面板在右；每個分頁的頁首、卡片、有沒有水平溢出 */
const layoutOf = () => {
  const d = document.querySelector('.dlg-back:not(.leaving) .dlg-settings');
  const nav = d.querySelector('.set-nav').getBoundingClientRect();
  const panels = d.querySelector('.set-panels');
  const pr = panels.getBoundingClientRect();
  const tabs = Array.from(d.querySelectorAll('.set-tab'));
  const rects = tabs.map((t) => t.getBoundingClientRect());
  return {
    width: Math.round(d.getBoundingClientRect().width),
    navLeft: nav.right <= pr.left + 1, navTop: nav.bottom <= pr.top + 1,
    vertical: rects.every((r, i) => i === 0 || r.top >= rects[i - 1].bottom - 1),
    horizontal: rects.every((r, i) => i === 0 || r.left >= rects[i - 1].right - 1),
    icons: tabs.map((t) => { const i = t.querySelector('.ic'); return !!i && i.getBoundingClientRect().width > 0; }),
    labelsFit: tabs.every((t) => { const l = t.querySelector('.set-tab-label'); return l.scrollWidth <= l.clientWidth + 1; }),
    sel: (d.querySelector('.set-tab[aria-selected="true"]') || {}).dataset?.tab ?? null,
    overflow: panels.scrollWidth > panels.clientWidth + 1,
  };
};

/** 選取的面板：頁首的標題與說明、區塊與卡片 */
const pageOf = () => {
  const d = document.querySelector('.dlg-back:not(.leaving) .dlg-settings');
  const p = Array.from(d.querySelectorAll('.set-panel')).find((x) => !x.hidden);
  const sel = d.querySelector('.set-tab[aria-selected="true"]');
  const pr = d.querySelector('.set-panels').getBoundingClientRect();
  const out = Array.from(p.querySelectorAll('*')).filter((n) => { const r = n.getBoundingClientRect(); return r.width > 0 && (r.right > pr.right + 1 || r.left < pr.left - 1); });
  return {
    tab: p.dataset.tab, title: (p.querySelector(':scope > .set-page-h .set-page-title') || {}).textContent || null,
    desc: (p.querySelector(':scope > .set-page-h .set-page-desc') || {}).textContent || '', label: sel ? sel.querySelector('.set-tab-label').textContent : null,
    first: p.firstElementChild && p.firstElementChild.classList.contains('set-page-h'),
    cards: p.querySelectorAll('.set-card').length, out: out.slice(0, 4).map((n) => n.className || n.tagName),
  };
};

/** 模型設定的子分頁：每一家的名稱、摘要、色點、選取、看不看得到；看得到的面板 */
const subOf = () => {
  const d = document.querySelector('.dlg-back:not(.leaving) .dlg-settings');
  const tabs = Array.from(d.querySelectorAll('.set-subtab'));
  const shown = Array.from(d.querySelectorAll('.set-prov')).filter((p) => p.getBoundingClientRect().height > 0);
  const tr = tabs.filter((t) => !t.hidden).map((t) => t.getBoundingClientRect());
  return {
    tabs: tabs.map((t) => ({
      p: t.dataset.provider, name: t.querySelector('.set-subtab-name').textContent, sum: t.querySelector('.set-subtab-model').textContent,
      dot: !t.querySelector('.set-pick-dot').hidden, mark: !!t.querySelector('.pmark svg'), hidden: t.hidden,
      sel: t.getAttribute('aria-selected') === 'true', tab: t.tabIndex, title: t.title,
    })),
    shown: shown.map((p) => p.dataset.provider),
    linked: shown.length === 1 && tabs.some((t) => t.getAttribute('aria-selected') === 'true' && t.getAttribute('aria-controls') === shown[0].id && shown[0].getAttribute('aria-labelledby') === t.id),
    oneRow: tr.every((r) => Math.abs(r.top - tr[0].top) < 2),
    groups: shown.length === 1 ? Array.from(shown[0].querySelectorAll('.set-group > .set-h')).map((x) => x.textContent) : [],
    focus: document.activeElement && document.activeElement.classList.contains('set-subtab') ? document.activeElement.dataset.provider : null,
  };
};

/** 選取標示（第九批）：和選取的分頁的相對位置、大小，分頁自己的 ::after 有沒有藏起來，換分頁時有沒有滑（transitionrun 記在 window.__indRun） */
const indOf = () => {
  const d = document.querySelector('.dlg-back:not(.leaving) .dlg-settings');
  const ind = d.querySelector('.set-tab-ind');
  const t = d.querySelector('.set-tab[aria-selected="true"]');
  if (!ind || !t) return null;
  const a = ind.getBoundingClientRect(), b = t.getBoundingClientRect();
  return { tab: t.dataset.tab, dTop: Math.round(a.top - b.top), dLeft: Math.round(a.left - b.left), bottom: Math.round(b.bottom - a.bottom),
    w: Math.round(a.width), h: Math.round(a.height), tw: Math.round(b.width), th: Math.round(b.height), after: getComputedStyle(t, '::after').display, run: window.__indRun || null };
};
const watchInd = () => { window.__indRun = null; document.querySelector('.dlg-settings .set-tab-ind').addEventListener('transitionrun', (e) => { window.__indRun = e.propertyName; }, { once: true }); };
/** 搜尋時各分類旁的命中數（第九批）：{ 分頁: 數字或 null }、淡掉的分頁 */
const tabHitsOf = () => {
  const d = document.querySelector('.dlg-back:not(.leaving) .dlg-settings');
  const tabs = Array.from(d.querySelectorAll('.set-tab'));
  return {
    n: Object.fromEntries(tabs.map((t) => { const x = t.querySelector('.set-tab-n'); return [t.dataset.tab, x && !x.hidden ? Number(x.textContent) : null]; })),
    dim: tabs.filter((t) => getComputedStyle(t).opacity !== '1').map((t) => t.dataset.tab),
    results: d.querySelectorAll('.set-result').length,
  };
};

/** 搜尋：搜尋框、結果（路徑、名稱、標出的字）、說明、面板是不是藏起來了 */
const searchOf = () => {
  const d = document.querySelector('.dlg-back:not(.leaving) .dlg-settings');
  if (!d) return null;
  const input = d.querySelector('.set-search-input');
  const res = d.querySelector('.set-results');
  return {
    focused: document.activeElement === input, value: input.value,
    shown: !res.hidden, head: d.querySelector('.set-results-head').textContent,
    items: Array.from(d.querySelectorAll('.set-result')).map((b) => ({
      path: b.querySelector('.set-result-path').textContent, label: b.querySelector('.set-result-label').textContent,
      marks: Array.from(b.querySelectorAll('mark')).map((m) => m.textContent),
    })),
    panelsShown: Array.from(d.querySelectorAll('.set-panel')).filter((p) => p.getBoundingClientRect().height > 0).length,
    injected: d.querySelectorAll('.set-results b, .set-results img').length,
  };
};

export async function runSettings(ctx, vp) {
  const { openApp, closeApp, check, until, settle, contrastOf, outFile } = ctx;
  const shot = async (page, name) => { await settle(page, 250); await page.screenshot({ path: outFile(name + '.png') }); };
  const onePane = (id) => ({ panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' });
  const tag = String(vp.w);

  // ── 版面：左側分類、右側頁首與卡片；每一頁都不溢出 ──
  for (const v of [vp, { w: 1100, h: 700 }]) {
    const app = await openApp('settings-nav-' + v.w, { vp: v, layout: onePane('c1') });
    const { page } = app;
    await page.click('#btnSettings');
    await page.waitForSelector('.dlg-settings .set-nav');
    await sleep(250);
    const lay = await page.evaluate(layoutOf);
    const pages = {};
    for (const id of ['general', 'chat', 'defaults', 'cli', 'sync', 'app']) {
      await page.click(`.dlg-settings .set-tab[data-tab="${id}"]`);
      await sleep(120);
      pages[id] = await page.evaluate(pageOf);
      if (v.w === vp.w && (id === 'general' || id === 'chat' || id === 'cli')) await shot(page, `settings-nav-${id}-${v.w}`);
    }
    const lay2 = await page.evaluate(layoutOf);
    // 第九批：選取標示對準最後選的那一頁（左緣、上下各縮 7px），再換到「對話」時滑過去（transform 轉場）
    await sleep(300); // 上面最後一次換分頁的滑動（240 ms）走完
    const ind0 = await page.evaluate(indOf);
    await page.evaluate(watchInd);
    await page.click('.dlg-settings .set-tab[data-tab="chat"]');
    const indRun = await until(() => page.evaluate(() => window.__indRun), 1000, 10);
    await sleep(350);
    const ind1 = await page.evaluate(indOf);
    check('settingsNavIndicator_' + v.w, !!ind0 && ind0.tab === 'app' && ind0.dTop === 7 && ind0.dLeft === 0 && ind0.w === 2 && ind0.h === ind0.th - 14 && ind0.after === 'none'
      && indRun === 'transform' && !!ind1 && ind1.tab === 'chat' && ind1.dTop === 7 && ind1.h === ind1.th - 14, { ind0, indRun, ind1 });
    const labels = { general: '一般', chat: '對話', defaults: '模型設定', cli: 'CLI', sync: '多電腦同步', app: '版本與更新' };
    check('settingsNavLayout_' + v.w, lay.navLeft && lay.vertical && lay.icons.every(Boolean) && lay.labelsFit && lay.sel === 'general'
      && lay.width === Math.min(880, v.w - 32) && !lay.overflow && !lay2.overflow
      && Object.entries(pages).every(([id, p]) => p.tab === id && p.first && p.title === labels[id] && p.label === labels[id] && p.desc.length > 0 && p.out.length === 0)
      && pages.general.cards === 3 && pages.chat.cards === 4 && pages.cli.cards >= 2,
    { layout: lay, after: lay2, pages });
    await closeApp(app);
  }

  // ── 窄視窗：分類換成上方的橫排（不帶圖示），搜尋框在上面；不溢出 ──
  {
    const narrow = { w: 700, h: 760 };
    const app = await openApp('settings-narrow', { vp: narrow, layout: onePane('c1') });
    const { page } = app;
    await page.click('#btnSettings');
    await page.waitForSelector('.dlg-settings .set-nav');
    await sleep(250);
    const lay = await page.evaluate(layoutOf);
    await shot(page, 'settings-narrow-' + narrow.w);
    await page.click('.dlg-settings .set-tab[data-tab="defaults"]');
    await sleep(350);
    // 第九批：橫排時選取標示在下緣、和分頁一樣寬
    const indN = await page.evaluate(indOf);
    check('settingsNavIndicatorNarrow', !!indN && indN.tab === 'defaults' && indN.bottom === 0 && indN.dLeft === 0 && indN.w === indN.tw && indN.h === 2, indN);
    const sub = await page.evaluate(subOf);
    const def = await page.evaluate(pageOf);
    await shot(page, 'settings-narrow-defaults-' + narrow.w);
    check('settingsNavNarrow', lay.navTop && lay.horizontal && lay.icons.every((x) => !x) && !lay.overflow && def.out.length === 0 && sub.shown.length === 1,
      { layout: lay, defaults: def, sub });
    await closeApp(app);
  }

  // ── 模型設定的代理子分頁 ──
  {
    const app = await openApp('settings-sub', { vp, layout: onePane('c1') });
    const { page, host } = app;
    await page.click('#btnSettings');
    await page.click('.dlg-settings .set-tab[data-tab="defaults"]');
    await page.waitForSelector('.dlg-settings .set-subtab');
    await page.click('.dlg-settings .set-subtab[data-provider="Claude"]');
    await sleep(150);
    const s0 = await page.evaluate(subOf);
    await shot(page, 'settings-sub-claude-' + tag);
    // 方向鍵：右到 Codex、End 到 Grok、再右循環回 Claude（焦點跟著）
    await page.focus('.dlg-settings .set-subtab[aria-selected="true"]');
    await page.keyboard.press('ArrowRight');
    const s1 = await page.evaluate(subOf);
    await page.keyboard.press('End');
    const s2 = await page.evaluate(subOf);
    await shot(page, 'settings-sub-grok-' + tag);
    await page.keyboard.press('ArrowRight');
    const s3 = await page.evaluate(subOf);
    // 點 Antigravity、關掉再開：回到 Antigravity
    await page.click('.dlg-settings .set-subtab[data-provider="Antigravity"]');
    await page.keyboard.press('Escape');
    await sleep(200);
    await page.click('#btnSettings');
    await page.waitForSelector('.dlg-settings .set-subtab');
    await sleep(200);
    const s4 = await page.evaluate(subOf);
    // 宿主不再給 Antigravity 的設定（例如那一家的帳戶都移除了）：子分頁不見、換到第一家；給回來又出現（選取不變）
    const saved = host.settings.conversationDefaults.Antigravity;
    delete host.settings.conversationDefaults.Antigravity;
    await host.send({ type: 'settings', settings: host.settings });
    const s5 = await until(async () => { const r = await page.evaluate(subOf); return r.tabs.find((t) => t.p === 'Antigravity').hidden ? r : null; }, 2000) || await page.evaluate(subOf);
    host.settings.conversationDefaults.Antigravity = saved;
    await host.send({ type: 'settings', settings: host.settings });
    const s6 = await until(async () => { const r = await page.evaluate(subOf); return !r.tabs.find((t) => t.p === 'Antigravity').hidden ? r : null; }, 2000) || await page.evaluate(subOf);
    // 預設模型換了：子分頁的摘要跟著換
    await page.click('.dlg-settings .set-subtab[data-provider="Codex"]');
    await page.click('.dlg-settings .set-prov[data-provider="Codex"] .set-defmodel');
    await page.click('.menu .menu-item:has(.mi-label:text-is("GPT-6 Astra"))');
    const s7 = await until(async () => { const r = await page.evaluate(subOf); return r.tabs.find((t) => t.p === 'Codex').sum === 'GPT-6 Astra' ? r : null; }, 2000) || await page.evaluate(subOf);
    const by = (s, p) => s.tabs.find((t) => t.p === p);
    const selOf = (s) => (s.tabs.find((t) => t.sel) || {}).p || null;
    check('settingsSubTabs', s0.tabs.map((t) => t.name).join(',') === 'Claude Code,Codex,Antigravity,Grok Build' && s0.tabs.every((t) => t.mark && !t.hidden && t.dot) && s0.oneRow
      && by(s0, 'Claude').sum === 'Opus 5.5' && by(s0, 'Codex').sum === '沿用上次選擇' && /權限模式：自動/.test(by(s0, 'Claude').title)
      && selOf(s0) === 'Claude' && s0.shown.join() === 'Claude' && s0.linked && s0.tabs.filter((t) => t.tab === 0).length === 1
      && s0.groups.join(',') === '新對話的預設,推理強度'
      && selOf(s1) === 'Codex' && s1.focus === 'Codex' && s1.shown.join() === 'Codex' && selOf(s2) === 'Grok' && s2.focus === 'Grok' && selOf(s3) === 'Claude' && s3.focus === 'Claude'
      && selOf(s4) === 'Antigravity' && s4.shown.join() === 'Antigravity'
      && by(s5, 'Antigravity').hidden && selOf(s5) === 'Claude' && s5.shown.join() === 'Claude' && !by(s6, 'Antigravity').hidden && selOf(s6) === 'Claude'
      && by(s7, 'Codex').sum === 'GPT-6 Astra',
    { claude: s0, right: s1, end: s2, wrap: s3, reopened: s4, removed: s5, restored: s6, modelChanged: s7 });
    await closeApp(app);
  }

  // ── 搜尋 ──
  {
    const app = await openApp('settings-search', { vp, layout: onePane('c1') });
    const { page } = app;
    await page.click('#btnSettings');
    await page.waitForSelector('.dlg-settings .set-search-input');
    await sleep(250);
    await page.keyboard.press('Control+f');
    const f0 = await page.evaluate(searchOf);
    await page.keyboard.type('額度');
    await sleep(100);
    const r1 = await page.evaluate(searchOf);
    const hits1 = await page.evaluate(tabHitsOf);
    await shot(page, 'settings-search-' + tag);
    // 同義詞：dark 找到主題（標籤與說明裡沒有 dark）
    await page.fill('.dlg-settings .set-search-input', 'dark');
    const r2 = await page.evaluate(searchOf);
    const hits2 = await page.evaluate(tabHitsOf);
    // 模型名稱：在模型設定的 Claude Code 子分頁裡；Enter 跳過去（先把子分頁切到別家，看得出有沒有切回來）
    await page.keyboard.press('Escape');
    await page.click('.dlg-settings .set-tab[data-tab="defaults"]');
    await page.click('.dlg-settings .set-subtab[data-provider="Grok"]');
    await page.fill('.dlg-settings .set-search-input', 'opus');
    const r3 = await page.evaluate(searchOf);
    await page.focus('.dlg-settings .set-search-input');
    await page.keyboard.press('Enter');
    await sleep(250);
    const jumped = await page.evaluate(() => {
      const d = document.querySelector('.dlg-settings');
      const row = d.querySelector('.set-model[data-model="opus"]');
      const pr = d.querySelector('.set-panels').getBoundingClientRect();
      const r = row.getBoundingClientRect();
      return {
        tab: d.querySelector('.set-tab[aria-selected="true"]').dataset.tab, sub: (d.querySelector('.set-subtab[aria-selected="true"]') || {}).dataset?.provider ?? null,
        visible: r.height > 0 && r.top >= pr.top && r.bottom <= pr.bottom, hit: row.classList.contains('set-hit'),
        focusIn: row.contains(document.activeElement), searchCleared: d.querySelector('.set-search-input').value === '' && d.querySelector('.set-results').hidden,
      };
    });
    await shot(page, 'settings-search-jump-' + tag);
    // 下鍵從搜尋框移到第一筆結果、按了跳過去（自動換帳戶繼續在對話分頁的「額度用完時」）
    await page.fill('.dlg-settings .set-search-input', '自動換帳戶');
    await page.focus('.dlg-settings .set-search-input');
    await page.keyboard.press('ArrowDown');
    const onFirst = await page.evaluate(() => document.activeElement && document.activeElement.classList.contains('set-result'));
    await page.keyboard.press('Enter');
    await sleep(250);
    const jumped2 = await page.evaluate(() => {
      const d = document.querySelector('.dlg-settings');
      const row = d.querySelector('.set-row[data-setting="autoSwitchAccount"]');
      const pr = d.querySelector('.set-panels').getBoundingClientRect();
      const r = row.getBoundingClientRect();
      return { tab: d.querySelector('.set-tab[aria-selected="true"]').dataset.tab, visible: r.height > 0 && r.top >= pr.top && r.bottom <= pr.bottom, focusIn: row.contains(document.activeElement) };
    });
    // 沒有結果：說明照字面寫出搜尋字（不當成 HTML）；Esc 先清掉搜尋、對話框還在，再按一次才關
    await page.fill('.dlg-settings .set-search-input', '<b>zz</b>');
    const r4 = await page.evaluate(searchOf);
    await page.focus('.dlg-settings .set-search-input');
    await page.keyboard.press('Escape');
    await sleep(150);
    const r5 = await page.evaluate(searchOf);
    const hits5 = await page.evaluate(tabHitsOf);
    // 第九批：各分類旁的命中數加起來是列出的筆數，沒有命中的分類淡掉；清掉搜尋就都收起
    const sumOf = (x) => Object.values(x.n).reduce((s, v) => s + (v || 0), 0);
    check('settingsSearchTabHits', hits1.results > 0 && sumOf(hits1) === hits1.results && hits1.n.chat > 0
      && hits1.dim.length === Object.values(hits1.n).filter((v) => v === null).length && hits1.dim.every((t) => hits1.n[t] === null)
      && hits2.n.general >= 1 && hits2.dim.includes('cli')
      && Object.values(hits5.n).every((v) => v === null) && hits5.dim.length === 0, { hits1, hits2, hits5 });
    await page.keyboard.press('Escape');
    await sleep(250);
    const closed = await page.evaluate(() => !document.querySelector('.dlg-back:not(.leaving) .dlg-settings'));
    check('settingsSearch', f0.focused && !f0.shown
      && r1.shown && r1.panelsShown === 0 && r1.items.length >= 2 && r1.items.some((x) => x.label === '自動換帳戶繼續' && x.path === '對話 › 額度用完時')
      && r1.items.every((x) => x.marks.length > 0 || /額度/.test(x.path)) && /^找到 \d+ 項設定$/.test(r1.head)
      && r2.items.length > 0 && r2.items[0].label === '主題' && r2.items[0].path === '一般 › 外觀'
      && r3.items.some((x) => x.label === 'Opus 5.5' && x.path === '模型設定 › Claude Code › 推理強度' && x.marks[0] === 'Opus')
      && jumped.tab === 'defaults' && jumped.sub === 'Claude' && jumped.visible && jumped.hit && jumped.focusIn && jumped.searchCleared
      && onFirst && jumped2.tab === 'chat' && jumped2.visible && jumped2.focusIn
      && r4.shown && r4.items.length === 0 && r4.head === '沒有符合「<b>zz</b>」的設定' && r4.injected === 0
      && !!r5 && !r5.shown && r5.value === '' && r5.panelsShown === 1 && closed,
    { focus: f0, quota: r1, dark: r2, opus: r3, jumped, onFirst, jumped2, none: r4, cleared: r5, closed });
    await closeApp(app);
  }

  // ── 深淺色的文字對比（頁首、分類、卡片裡的說明、子分頁、搜尋結果）──
  for (const theme of ['dark', 'light']) {
    const app = await openApp('settings-contrast-' + theme, { vp, layout: onePane('c1'), settings: { theme }, colorScheme: theme });
    const { page } = app;
    await page.click('#btnSettings');
    await page.waitForSelector('.dlg-settings .set-nav');
    await page.click('.dlg-settings .set-tab[data-tab="defaults"]');
    await sleep(200);
    const sels = ['.set-page-title', '.set-page-desc', '.set-tab[aria-selected="true"]', '.set-tab[aria-selected="false"]', '.set-h', '.set-card .set-label', '.set-card .set-desc',
      '.set-subtab-name', '.set-subtab-sum', '.set-group-desc', '.set-defaults-note'];
    const c = {};
    for (const s of sels) c[s] = await page.evaluate(contrastOf, '.dlg-settings ' + s);
    await shot(page, `settings-sub-${theme}-${tag}`);
    await page.fill('.dlg-settings .set-search-input', '通知');
    await sleep(100);
    for (const s of ['.set-results-head', '.set-result-path', '.set-result-label', '.set-result-desc', '.set-result mark']) c[s] = await page.evaluate(contrastOf, '.dlg-settings ' + s);
    if (theme === 'light') await shot(page, `settings-search-light-${tag}`);
    check('settingsContrast_' + theme, Object.values(c).every((r) => r !== null && r >= 4.5), c);
    await closeApp(app);
  }
}
