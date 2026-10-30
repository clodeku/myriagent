// 儀表板的健康度（web-protocol 的 health／healthCheck／healthFix；Web/health.js、Web/healthrules.js）的情境：probe.mjs 匯入，--health 只跑它，完整執行時也跑。
// 檢查名稱 health*：切到儀表板送 healthCheck、沒有結果時不顯示、帳戶卡的一行（等級、摘要、不超出卡片）、明細對話框（各項、檔案、符號連結、
// 項目裡的「用代理精簡…」「用代理整理…」）、電腦層級的指示檔（狀態膠囊、涵蓋率、Grok 的 Claude 相容、缺少的條目、「用代理合併…」選帳戶送 healthFix）、
// 分析中、良好時的寫法、淺色。截圖 health-*.png

const NOW = Date.now();

/** 假的 HealthView（形狀見 web-protocol） */
export function makeHealth(o = {}) {
  const item = (key, title, level, value, detail, files = []) => ({ key, title, level, value, detail, files });
  const file = (path, label, note, level, target = null) => ({ path, label, note, level, target });
  return Object.assign({
    running: false,
    checkedAt: NOW - 3 * 60e3,
    accounts: {
      a1: {
        level: 'warn', summary: 'CLAUDE.md 偏大、記憶索引接近上限', load: '約 1.3 萬 token 指示＋約 7,141 token skill 清單', canTrim: true, canTrimMemory: true,
        items: [
          item('instructions', '全域指示', 'warn', '84 行・約 1.3 萬 token',
            '每次對話開始都會載入這些指示，合計約 1.3 萬 token，約占 200K 脈絡的 6%。\n合計超過 5,000 token：指示越長越佔脈絡，也越容易有些規則沒被遵守。\n建議：只在特定情境需要的細節搬到另外的文件，指示檔只留一行「情境 → 檔案」的索引。',
            [file('C:\\Users\\Lin\\.claude\\CLAUDE.md', '主檔　CLAUDE.md', '84 行・約 1.3 萬 token', 'ok', 'C:\\repo\\dotfiles\\claude\\CLAUDE.md')]),
          item('memory', '自動記憶索引', 'warn', '22 個專案・1 個接近上限', '每個專案的 MEMORY.md 只載入前 200 行或 25KB（先到者為準），超過的部分不會進入對話。\n接近上限：再記幾條就會開始被截掉，建議先整理。',
            [file('C:\\Users\\Lin\\.claude\\projects\\C--repo-SampleShop\\memory\\MEMORY.md', 'C--repo-SampleShop', '125 行・23.5 KB', 'warn')]),
          item('skills', 'Skills', 'warn', '74 個・清單 26,041 字元・6 個子代理', 'skill 的名稱與描述每一輪都在脈絡裡（約 7,141 token）。其中 71 個是外掛帶來的。'),
          item('mcp', 'MCP 伺服器', 'ok', '4 個', '使用者層與外掛的 MCP 伺服器：higgsfield、meta-Hub 主機、playwright（playwright）、context7（context7）。'),
          item('plugins', '外掛', 'info', '12 個啟用', '啟用 12 個，帶來 63 個 skill、8 個斜線指令、6 個子代理、2 個附 MCP、3 個附 hooks。'),
          item('settings', '設定檔', 'ok', '正常', '設定檔讀得懂，大小正常。', [file('C:\\Users\\Lin\\.claude.json', '.claude.json', '171.6 KB', 'ok')]),
        ],
      },
      a2: { level: 'ok', summary: '良好', load: '約 2,100 token 指示', canTrim: false, items: [item('instructions', '全域指示', 'ok', '40 行・約 2,100 token', '每次對話開始都會載入這些指示。')] },
      a3: { level: 'ok', summary: '良好', load: null, canTrim: false, items: [item('instructions', '全域指示', 'info', '沒有', '這個帳戶沒有使用者層的 AGENTS.md。', [file('C:\\Users\\Lin\\.codex\\AGENTS.md', 'AGENTS.md', '不存在', 'info')])] },
      a6: {
        level: 'bad', summary: 'CLAUDE.md 過大', load: '約 1.6 萬 token 指示', canTrim: true,
        items: [item('instructions', '全域指示', 'bad', '96 行・約 1.6 萬 token・2 個檔', '每次對話開始都會載入這些指示。',
          [file('C:\\Users\\Lin\\.grok\\AGENTS.md', '主檔　AGENTS.md', '12 行・約 3,000 token', 'ok'), file('C:\\Users\\Lin\\.claude\\CLAUDE.md', 'Claude 相容　CLAUDE.md', '84 行・約 1.3 萬 token', 'ok')])],
      },
    },
    instructions: {
      verdict: 'needsSync', level: 'warn', summary: 'Codex 的 AGENTS.md 是空的，拿不到其他 CLI 的 52 條指示；建議合併並同步。', mergeable: true,
      docs: [
        { key: 'c:\\users\\lin\\.claude\\claude.md', provider: 'Claude', label: 'Claude Code', path: 'C:\\Users\\Lin\\.claude\\CLAUDE.md', target: 'C:\\repo\\dotfiles\\claude\\CLAUDE.md',
          exists: true, status: 'partial', note: '84 行・約 1.3 萬 token', overLimit: false, units: 51, covered: 51, union: 52, coverage: 0.981,
          missingCount: 1, missing: [{ section: '', text: '所有回覆、說明、備註、文檔都必須使用繁體中文' }], uniqueCount: 0, unique: [], alsoLoads: null, duplicates: 0, accounts: ['a1'] },
        { key: 'c:\\users\\lin\\.codex\\agents.md', provider: 'Codex', label: 'Codex', path: 'C:\\Users\\Lin\\.codex\\AGENTS.md', target: null,
          exists: true, status: 'empty', note: '空的', overLimit: false, units: 0, covered: 0, union: 52, coverage: 0,
          missingCount: 52, missing: [{ section: '語言', text: '嚴格使用臺灣繁體中文（Traditional Chinese, Taiwan）撰寫所有回應、程式碼註解、commit messages 及 UI 文字。' },
            { section: '部署', text: 'push 後驗證部署，gh run list 要用 --commit $(git rev-parse HEAD) 過濾（--limit 1 會抓到上一次的舊 run，誤判已完成）。' }],
          uniqueCount: 0, unique: [], alsoLoads: null, duplicates: 0, accounts: ['a3'] },
        { key: 'c:\\users\\lin\\.gemini\\gemini.md', provider: 'Antigravity', label: 'Antigravity', path: 'C:\\Users\\Lin\\.gemini\\GEMINI.md', target: null,
          exists: true, status: 'partial', note: '1 行・約 22 token', overLimit: false, units: 1, covered: 1, union: 52, coverage: 0.019,
          missingCount: 51, missing: [], uniqueCount: 1, unique: [{ section: '', text: '所有回覆、說明、備註、文檔都必須使用繁體中文' }], alsoLoads: null, duplicates: 0, accounts: ['a5'] },
        { key: 'c:\\users\\lin\\.grok\\agents.md', provider: 'Grok', label: 'Grok Build', path: 'C:\\Users\\Lin\\.grok\\AGENTS.md', target: null,
          exists: false, status: 'partial', note: '不存在', overLimit: false, units: 0, covered: 51, union: 52, coverage: 0.981,
          missingCount: 1, missing: [{ section: '', text: '所有回覆、說明、備註、文檔都必須使用繁體中文' }], uniqueCount: 0, unique: [], alsoLoads: 'Claude Code', duplicates: 0, accounts: ['a6'] },
      ],
    },
  }, o);
}

/** 帳戶卡上的一行：看不看得到、等級、文字、在卡片裡 */
function stripState(id) {
  const card = Array.from(document.querySelectorAll('#dashboard .acard')).find((c) => c.dataset.key === id);
  if (!card) return null;
  const b = card.querySelector('.acard-health');
  if (!b) return null;
  const r = b.getBoundingClientRect();
  const cr = card.getBoundingClientRect();
  const cs = getComputedStyle(b);
  return {
    shown: cs.display !== 'none' && r.height > 0, level: b.dataset.level || null, all: b.textContent,
    badge: (b.querySelector('.ah-badge') || {}).textContent || null, text: (b.querySelector('.ah-text') || {}).textContent || null,
    inside: r.left >= cr.left - 0.5 && r.right <= cr.right + 0.5, height: Math.round(r.height),
    color: getComputedStyle(b.querySelector('.ah-badge') || b).color,
  };
}

/** 電腦層級的指示檔：區塊、結論、每張卡（名稱、膠囊、涵蓋率、相容說明、可展開的清單）、合併鈕 */
function sectionState() {
  const sec = document.querySelector('#dashboard .dash-sec-hx');
  if (!sec) return null;
  const cs = getComputedStyle(sec);
  return {
    shown: cs.display !== 'none' && sec.getBoundingClientRect().height > 0,
    verdict: (sec.querySelector('.hx-verdict-text') || {}).textContent || null,
    level: (sec.querySelector('.hx-verdict') || {}).dataset?.level || null,
    cards: Array.from(sec.querySelectorAll('.hx-card')).map((c) => ({
      name: c.querySelector('.hx-name').textContent, pill: c.querySelector('.hx-pill').textContent, tone: c.dataset.tone,
      cov: c.querySelector('.hx-cov-text').textContent, fill: c.querySelector('.hx-bar-fill').style.width,
      also: (c.querySelector('.hx-also') || {}).textContent || null, target: (c.querySelector('.hx-target') || {}).textContent || null,
      dets: Array.from(c.querySelectorAll('.hx-det summary')).map((s) => s.textContent),
      overflow: c.scrollWidth > c.clientWidth + 1,
    })),
    merge: !sec.querySelector('.hx-actions').hidden,
  };
}

export async function runHealth(ctx, vp) {
  const { openApp, closeApp, check, until, settle, outFile } = ctx;
  const onePane = (id, view) => ({ panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: false, view });
  const app = await openApp('health', { vp, layout: onePane('c1', 'dashboard') });
  const { page, host } = app;
  const shotFull = async (name) => { await settle(page, 200); await page.screenshot({ path: outFile(name + '.png') }); };

  // 一開始就在儀表板：送 healthCheck（不帶 force）；還沒有結果時不顯示健康度的一行與指示檔區塊
  const first = await until(() => host.last('healthCheck'), 3000);
  check('healthCheckOnDashboard', !!first && first.force !== true, first);
  const before = await page.evaluate(stripState, 'a1');
  const secBefore = await page.evaluate(sectionState);
  check('healthHiddenWithoutData', !!before && !before.shown && !!secBefore && !secBefore.shown, { before, secBefore });

  await host.send({ type: 'health', health: makeHealth() });
  await settle(page, 200);
  const a1 = await page.evaluate(stripState, 'a1');
  const a2 = await page.evaluate(stripState, 'a2');
  const a4 = await page.evaluate(stripState, 'a4');
  const a6 = await page.evaluate(stripState, 'a6');
  check('healthStripNoNullText', [a1, a2, a6].every((s) => !/null|undefined/.test(s.all)), [a1.all, a2.all, a6.all]);
  check('healthStripWarn', a1.shown && a1.level === 'warn' && a1.badge === '注意' && a1.text === 'CLAUDE.md 偏大、記憶索引接近上限' && a1.inside, a1);
  check('healthStripOk', a2.shown && a2.level === 'ok' && a2.badge === '良好' && /每次對話固定載入約 2,100 token 指示/.test(a2.text) && a2.inside && a2.height === a1.height, a2);
  check('healthStripBad', a6.shown && a6.level === 'bad' && a6.badge === '問題' && a6.color !== a1.color, a6);
  check('healthStripNoData', !a4.shown, a4);

  const sec = await page.evaluate(sectionState);
  const byName = Object.fromEntries(sec.cards.map((c) => [c.name, c]));
  check('healthSection', sec.shown && sec.level === 'warn' && /Codex 的 AGENTS.md 是空的/.test(sec.verdict) && sec.cards.length === 4 && sec.merge
    && sec.cards.every((c) => !c.overflow), sec);
  check('healthDocCards', byName['Codex'].pill === '空的' && byName['Codex'].tone === 'warn' && byName['Codex'].fill === '0%' && byName['Codex'].cov === '涵蓋 0／52 條（0%）'
    && byName['Claude Code'].pill === '缺 1 條' && /dotfiles/.test(byName['Claude Code'].target || '')
    && /另會讀取 Claude Code 的指示/.test(byName['Grok Build'].also || '') && byName['Grok Build'].cov === '涵蓋 51／52 條（98%）'
    && byName['Antigravity'].dets.join('|') === '缺少的指示51|只有這份有1', byName);
  await page.evaluate(() => document.querySelector('#dashboard .dash-sec-hx').scrollIntoView({ block: 'start' }));
  // 展開 Codex 的「缺少的指示」
  await page.evaluate(() => { const c = Array.from(document.querySelectorAll('#dashboard .hx-card')).find((x) => x.querySelector('.hx-name').textContent === 'Codex'); c.querySelector('details').open = true; });
  await settle(page, 150);
  const units = await page.evaluate(() => Array.from(document.querySelectorAll('#dashboard .hx-card details[open] .hx-units li')).map((l) => l.textContent));
  check('healthMissingList', units.length === 3 && units[0].startsWith('語言') && units[2] === '還有 50 條', units);
  await page.locator('#dashboard .dash-sec-hx').screenshot({ path: outFile('health-instructions-dark.png') });

  // 用代理合併：選帳戶（只有已登入的這台帳戶）→ 送 healthFix、切到對話檢視
  await page.click('#dashboard .hx-actions .btn');
  const menu = await until(() => page.evaluate(() => {
    const m = document.querySelector('.menu');
    return m ? { head: (m.querySelector('.menu-head') || {}).textContent, items: Array.from(m.querySelectorAll('.menu-item .mi-label')).map((x) => x.textContent) } : null;
  }), 2000);
  check('healthMergeMenu', !!menu && menu.head === '用哪個帳戶合併' && menu.items.includes('工作用 Max') && !menu.items.includes('Codex 團隊'), menu);
  await page.screenshot({ path: outFile('health-merge-menu-dark.png') });
  await page.click('.menu .menu-item');
  const fix = await until(() => host.last('healthFix'), 2000);
  check('healthFixMerge', !!fix && fix.task === 'merge' && fix.accountId === 'a1' && fix.targetAccountId === null && fix.paneId === 'p1', fix);
  const chat = await until(() => page.evaluate(() => (document.getElementById('dashboard').hidden && !document.getElementById('workspace').hidden) || null), 2000);
  check('healthFixSwitchesToChat', !!chat, chat);

  // 回到儀表板：再送一次 healthCheck；開明細
  await page.click('#viewSwitch [data-view="dashboard"]');
  const checks = () => host.received.filter((m) => m.cmd === 'healthCheck').length;
  const again = await until(() => (checks() >= 2 ? true : null), 2000);
  check('healthCheckOnReturn', !!again, checks());
  await page.click('#dashboard .acard[data-key="a1"] .acard-health');
  const dlg = await until(() => page.evaluate(() => {
    const d = document.querySelector('.dlg-health');
    if (!d) return null;
    return {
      title: d.querySelector('.dlg-title').textContent, summary: d.querySelector('.hd-summary').textContent,
      items: Array.from(d.querySelectorAll('.hd-item')).map((i) => [i.dataset.key, i.dataset.level, i.querySelector('.hd-item-value').textContent]),
      target: (d.querySelector('.hd-file-target') || {}).textContent || null,
      fixes: Array.from(d.querySelectorAll('.hd-item .hd-fix')).map((b) => [b.closest('.hd-item').dataset.key, b.dataset.task, b.textContent].join('|')),
      footButtons: d.querySelectorAll('.dlg-foot .btn').length,
      overflow: d.querySelector('.dlg-body').scrollWidth > d.querySelector('.dlg-body').clientWidth + 1,
    };
  }), 2000);
  check('healthDialog', !!dlg && dlg.title === '「工作用 Max」的健康度' && dlg.items.length === 6 && dlg.items[0].join('|') === 'instructions|warn|84 行・約 1.3 萬 token'
    && /dotfiles/.test(dlg.target || '') && !dlg.overflow, dlg);
  // 「用代理精簡…」「用代理整理…」放在各自的項目裡（全域指示看 canTrim、自動記憶索引看 canTrimMemory），底部只剩重新分析與關閉
  check('healthDialogFixButtons', !!dlg && dlg.fixes.join(',') === 'instructions|trim|用代理精簡…,memory|memory|用代理整理…' && dlg.footButtons === 2, dlg);
  await settle(page, 400);   // 對話框淡入
  await page.screenshot({ path: outFile('health-dialog-dark.png') });
  // 路徑點了送 openPath
  await page.click('.dlg-health .hd-path');
  const opened = await until(() => host.last('openPath'), 2000);
  check('healthOpenPath', !!opened && opened.path === 'C:\\Users\\Lin\\.claude\\CLAUDE.md', opened);
  // 「重新分析」送 force；分析中按鈕停用
  await page.click('.dlg-health .dlg-foot .btn');
  const forced = await until(() => host.last('healthCheck', (m) => m.force === true), 2000);
  await host.send({ type: 'health', health: makeHealth({ running: true }) });
  await settle(page, 150);
  const busy = await page.evaluate(() => { const b = document.querySelector('.dlg-health .dlg-foot .btn'); return { disabled: b.disabled, text: b.textContent }; });
  check('healthRefreshForce', !!forced && busy.disabled && busy.text === '分析中…', { forced, busy });
  await host.send({ type: 'health', health: makeHealth() });
  // 用代理精簡：預設用同一個帳戶（排第一）
  await page.click('.dlg-health .hd-fix[data-task="trim"]');
  await until(() => page.evaluate(() => !!document.querySelector('.menu')), 2000);
  const trimFirst = await page.evaluate(() => document.querySelector('.menu .menu-item .mi-label').textContent);
  await page.click('.menu .menu-item');
  const trim = await until(() => host.last('healthFix', (m) => m.task === 'trim'), 2000);
  check('healthFixTrim', trimFirst === '工作用 Max' && !!trim && trim.accountId === 'a1' && trim.targetAccountId === 'a1', { trimFirst, trim });
  const closed = await page.evaluate(() => !document.querySelector('.dlg-health') || document.querySelector('.dlg-back.leaving') !== null);
  check('healthDialogClosesOnFix', closed, closed);

  // 用代理整理記憶索引：回儀表板重開明細，按記憶索引那一項的按鈕；同樣預設用這個帳戶、帶 targetAccountId
  await page.click('#viewSwitch [data-view="dashboard"]');
  await until(() => page.evaluate(() => !document.querySelector('.dlg-health') || null), 2000);
  await page.click('#dashboard .acard[data-key="a1"] .acard-health');
  await until(() => page.evaluate(() => !!document.querySelector('.dlg-health .hd-fix[data-task="memory"]') || null), 2000);
  await page.click('.dlg-health .hd-fix[data-task="memory"]');
  const memMenu = await until(() => page.evaluate(() => {
    const m = document.querySelector('.menu');
    return m ? { head: (m.querySelector('.menu-head') || {}).textContent, first: (m.querySelector('.menu-item .mi-label') || {}).textContent } : null;
  }), 2000);
  await page.click('.menu .menu-item');
  const mem = await until(() => host.last('healthFix', (m) => m.task === 'memory'), 2000);
  check('healthFixMemory', !!memMenu && memMenu.head === '用哪個帳戶整理' && memMenu.first === '工作用 Max'
    && !!mem && mem.accountId === 'a1' && mem.targetAccountId === 'a1' && mem.paneId === 'p1', { memMenu, mem });

  // 沒有可以修的項目（a2 良好）：明細裡沒有「用代理…」按鈕
  await page.click('#viewSwitch [data-view="dashboard"]');
  await until(() => page.evaluate(() => !document.querySelector('.dlg-health') || null), 2000);
  await page.click('#dashboard .acard[data-key="a2"] .acard-health');
  const okFixes = await until(() => page.evaluate(() => {
    const d = document.querySelector('.dlg-health');
    return d && !d.closest('.leaving') && d.querySelector('.hd-item') ? { title: d.querySelector('.dlg-title').textContent, fixes: d.querySelectorAll('.hd-fix').length } : null;
  }), 2000);
  check('healthDialogNoFixWhenOk', !!okFixes && okFixes.title === '「個人 Pro」的健康度' && okFixes.fixes === 0, okFixes);
  await closeApp(app);

  // 淺色、只有一種 CLI（不顯示指示檔區塊）
  const light = await openApp('health-light', { vp, layout: onePane('c1', 'dashboard'), settings: { theme: 'light' }, colorScheme: 'light' });
  await until(() => light.host.last('healthCheck'), 3000);
  const single = makeHealth();
  single.instructions = Object.assign({}, single.instructions, { verdict: 'single', level: 'ok', mergeable: false, docs: single.instructions.docs.slice(0, 1) });
  await light.host.send({ type: 'health', health: single });
  await settle(light.page, 200);
  const lsec = await light.page.evaluate(sectionState);
  const la1 = await light.page.evaluate(stripState, 'a1');
  check('healthSingleHidden', !lsec.shown && la1.shown, { lsec, la1 });
  await light.page.locator('#dashboard .acard[data-key="a1"]').screenshot({ path: outFile('health-card-light.png') });
  await closeApp(light);
}
