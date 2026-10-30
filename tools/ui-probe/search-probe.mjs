// 側欄的對話搜尋（標題與內容；web-protocol 的 searchConversations／conversationSearch）的桌面情境：probe.mjs 匯入，--search 只跑它，完整執行時也跑
// （瀏覽器版的送出與套用結果在 live-probe.mjs 的 hubSearchContent）。
// 假宿主收到 searchConversations 後延遲 searchDelay 才回（照宿主的規則：每個關鍵字都要在標題或內容），內容是下面的 CONTENT。
// 檢查名稱 search*：提示文字、打字停下才送一次、結果回來前只比標題並顯示「搜尋內容中…」、回來後補上只有內容符合的對話與片段（<mark> 標示、
// 最多兩行、不超出列）、多個關鍵字、舊的回覆不採用、清單更新時重搜、找不到的說明、Esc 清掉、深淺色對比。截圖 search-*.png

/** 假的對話內容（宿主那邊的使用者訊息與代理回覆）：對話 id → 依序的內容 */
export const CONTENT = {
  c3: ['縮圖快取改成依檔案的修改時間失效之後，記憶體用量少了一半。'],
  c5: ['CLI 自動更新時先檢查有沒有對話在執行；更新的程式在背景下載，裝好之後等這一輪結束才換版，這段期間佔用的記憶體不會超過原本的兩倍，也不會讓正在跑的測試被中斷或重來一次，使用者照常可以送出下一則訊息。'],
  c8: ['我先量一下各程序的工作集。', '主要是 WebView2 的快取：關掉不用的分頁之後記憶體就降下來了。'],
  c12: ['Job Object 讓子程序跟著 app 結束，記憶體不會殘留在背景。'],
};

/** 假宿主的搜尋（照 ConversationSearchRules：空白分開、不分大小寫、每個關鍵字都在標題或內容；片段取第一則含任一關鍵字的、這裡不截） */
export function fakeSearch(conversations, query) {
  const terms = String(query || '').split(/\s+/).filter(Boolean).map((t) => t.toLowerCase());
  if (!terms.length) return [];
  const hits = [];
  for (const c of conversations) {
    const texts = CONTENT[c.id] || [];
    const title = (c.title || '').toLowerCase();
    const ok = terms.every((t) => title.includes(t) || texts.some((x) => x.toLowerCase().includes(t)));
    if (!ok) continue;
    const first = texts.find((x) => terms.some((t) => x.toLowerCase().includes(t)));
    hits.push({ conversationId: c.id, snippet: first || null });
  }
  return hits;
}

/** 側欄清單的狀態：列（id、標題、片段、標示的字、位置）、「搜尋內容中…」、空白說明 */
function sidebarState() {
  const sb = document.querySelector('.sidebar');
  const sbBox = sb.getBoundingClientRect();
  const rows = Array.from(document.querySelectorAll('.sb-list .crow')).map((r) => {
    const snip = r.querySelector('.crow-snip');
    const shown = snip && getComputedStyle(snip).display !== 'none' && !snip.hidden;
    const box = r.getBoundingClientRect();
    const sbox = shown ? snip.getBoundingClientRect() : null;
    const lh = shown ? parseFloat(getComputedStyle(snip).lineHeight) : 0;
    return {
      id: (r.dataset.key || '').slice(2), title: r.querySelector('.crow-title').textContent,
      snippet: shown ? snip.textContent : null, marks: shown ? Array.from(snip.querySelectorAll('mark')).map((m) => m.textContent) : [],
      top: box.top, bottom: box.bottom, left: box.left, right: box.right,
      snipInside: !sbox || (sbox.left >= box.left - 0.5 && sbox.right <= box.right + 0.5 && sbox.bottom <= box.bottom + 0.5),
      snipLines: sbox ? Math.round(sbox.height / lh) : 0, clipped: shown ? snip.scrollHeight > snip.clientHeight + 1 : false,
      titleLeft: r.querySelector('.crow-title').getBoundingClientRect().left, snipLeft: sbox ? sbox.left : null,
    };
  });
  const searching = document.querySelector('.sb-searching');
  const empty = document.querySelector('.sb-empty');
  const input = document.querySelector('.sb-search-input');
  return {
    rows, ids: rows.map((r) => r.id), inside: rows.every((r) => r.left >= sbBox.left - 0.5 && r.right <= sbBox.right + 0.5),
    searching: !!searching && !searching.hidden && searching.getBoundingClientRect().height > 0,
    empty: empty && !empty.hidden ? empty.textContent : null, placeholder: input.placeholder, label: input.getAttribute('aria-label'),
  };
}

const noOverlap = (rows) => rows.every((r, i) => i === 0 || r.top >= rows[i - 1].bottom - 0.5);

export async function runSearch(ctx, vp) {
  const { openApp, closeApp, check, until, settle, outFile, contrastOf } = ctx;
  const layout = { panes: [{ id: 'p1', tabs: ['c2'], active: 'c2' }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' };
  const app = await openApp('search', { vp, layout, hostSetup: (h) => { h.searchDelay = 700; } });
  const { page, host } = app;
  const st = () => page.evaluate(sidebarState);
  const sent = () => host.received.filter((m) => m.cmd === 'searchConversations');
  const sidebarShot = async (file) => { await settle(page, 150); await page.locator('.sidebar').screenshot({ path: outFile(file) }); };

  const s0 = await st();
  check('searchPlaceholder', s0.placeholder === '搜尋標題與內容' && s0.label === '搜尋對話的標題與內容' && s0.ids.length === 14 && !s0.searching, s0);

  // 打字途中不送；停下 250 ms 後送一次最後的查詢。結果回來前只比標題（c8 的標題有「記憶體」），下面顯示「搜尋內容中…」
  const before = sent().length;
  await page.locator('.sb-search-input').pressSequentially('記憶體', { delay: 60 });
  const pending = await until(async () => { const s = await st(); return s.searching ? s : null; }, 1000);
  check('searchTitleFirstThenPending', !!pending && pending.ids.join(',') === 'c8' && pending.rows[0].snippet === null && pending.empty === null, pending);
  await sidebarShot('search-pending-dark.png');
  const arrived = await until(async () => { const s = await st(); return !s.searching && s.ids.length > 1 ? s : null; }, 3000);
  const reqs = sent().slice(before);
  check('searchSentOnceAfterTyping', reqs.length === 1 && reqs[0].query === '記憶體' && typeof reqs[0].requestId === 'string', reqs);
  check('searchContentHits', !!arrived && arrived.ids.join(',') === 'c3,c5,c8,c12'
    && arrived.rows.every((r) => r.snippet && r.marks.length >= 1 && r.marks.every((m) => m === '記憶體')), arrived);
  const r8 = arrived && arrived.rows.find((r) => r.id === 'c8');
  check('searchSnippetIsContent', !!r8 && r8.snippet.startsWith('主要是 WebView2'), r8);
  check('searchSnippetLayout', !!arrived && arrived.inside && noOverlap(arrived.rows) && arrived.rows.every((r) => r.snipInside && r.snipLines >= 1 && r.snipLines <= 2)
    && arrived.rows.every((r) => Math.abs(r.snipLeft - r.titleLeft) <= 1) && arrived.rows.find((r) => r.id === 'c5').clipped, arrived && arrived.rows);
  await sidebarShot('search-hits-dark.png');
  const snipContrast = await page.evaluate(contrastOf, '.crow-snip');
  const markContrast = await page.evaluate(contrastOf, '.crow-snip mark');
  check('searchSnippetContrast_dark', snipContrast >= 4.5 && markContrast >= 4.5, { snipContrast, markContrast });

  // 舊的回覆（requestId 不是最後一次的）不採用
  await host.send({ type: 'conversationSearch', requestId: 'old-1', query: '記憶體', hits: [{ conversationId: 'c1', snippet: '不該出現' }] });
  await settle(page, 120);
  const stale = await st();
  check('searchIgnoresStaleReply', !stale.ids.includes('c1') && stale.ids.join(',') === 'c3,c5,c8,c12', stale.ids);

  // 清單更新（新的訊息）時隔一下重搜一次，舊的結果先留著
  const beforeRefresh = sent().length;
  await host.pushConversations();
  await settle(page, 80);
  const kept = await st();
  const refresh = await until(() => sent().length > beforeRefresh ? sent()[sent().length - 1] : null, 3000);
  check('searchRefreshOnListChange', kept.ids.join(',') === 'c3,c5,c8,c12' && !kept.searching && !!refresh && refresh.query === '記憶體', { kept: kept.ids, refresh });
  await until(async () => (await st()).ids.length === 4, 2000);

  // 多個關鍵字：每個都要在標題或內容（c8 的標題有「記憶體」但沒有 Job）
  await page.fill('.sb-search-input', '記憶體 job');
  const multi = await until(async () => { const s = await st(); return !s.searching ? s : null; }, 3000);
  check('searchMultiTerm', !!multi && multi.ids.join(',') === 'c12' && multi.rows[0].marks.join('|') === 'Job|記憶體', multi && multi.rows);

  // 找不到
  await page.fill('.sb-search-input', '不存在的字');
  const none = await until(async () => { const s = await st(); return !s.searching && s.empty ? s : null; }, 3000);
  check('searchNoResults', !!none && none.ids.length === 0 && none.empty.includes('找不到標題或內容含「不存在的字」的對話'), none);

  // Esc 清掉：整份清單、沒有片段
  await page.fill('.sb-search-input', '記憶體');
  await until(async () => (await st()).ids.length === 4, 3000);
  await page.focus('.sb-search-input');
  await page.keyboard.press('Escape');
  await settle(page, 120);
  const cleared = await st();
  check('searchEscapeClears', cleared.ids.length === 14 && cleared.rows.every((r) => r.snippet === null) && !cleared.searching && cleared.empty === null, cleared.ids);
  await closeApp(app);

  // 淺色
  const light = await openApp('search-light', { vp, layout, settings: { theme: 'light' }, colorScheme: 'light' });
  await light.page.fill('.sb-search-input', '記憶體');
  await until(async () => (await light.page.evaluate(sidebarState)).ids.length === 4, 3000);
  await settle(light.page, 150);
  const lightSnip = await light.page.evaluate(contrastOf, '.crow-snip');
  const lightMark = await light.page.evaluate(contrastOf, '.crow-snip mark');
  check('searchSnippetContrast_light', lightSnip >= 4.5 && lightMark >= 4.5, { lightSnip, lightMark });
  await light.page.locator('.sidebar').screenshot({ path: outFile('search-hits-light.png') });
  await closeApp(light);
}
