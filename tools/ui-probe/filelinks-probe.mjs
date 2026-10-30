// PowerPoint 下載與 JPG 預覽回歸：桌面與網頁都點真正的 Markdown <a>，以原有宿主／SignalR 替身檢查流程。
// cd tools/ui-probe && node filelinks-probe.mjs
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import * as HP from './hub-page.mjs';
import * as LF from './live-fixtures.mjs';
import { renderImage } from './fixtures.mjs';

const now = Date.now();
const root = 'C:\\repo\\slides\\watercolor_final\\output\\';
const pptx = root + '年度簡報_水彩版.pptx';
const jpg = root + '水彩書法版_整體預覽.jpg';
// 文字裡直接寫出的路徑、整段是路徑的行內程式碼：渲染器（AgentMarkdownRenderer）產生的形狀
const shot = String.raw`C:\Users\User\AppData\Local\Myriagent\conversations\9f8e7d6c5b4a49382716a5b4c3d2e1f0\screenshots\最近的指令-桌面.png`;
const doc = String.raw`C:\repo\myriagent\docs\web-protocol.md`;
const layout = (id) => ({ panes: [{ id: 'p1', tabs: [id], active: id }], focusedPane: 'p1', sidebarCollapsed: false, view: 'chat' });
const items = (hub) => [{ id: 'artifact', kind: 'Assistant', status: 'Done', time: now, seq: 1,
  html: '<p>已完成 37 頁「繽紛水彩書法風」重製版。</p><ul><li><a href="' + (hub ? '#local-file=' : HP.DESKTOP_ORIGIN + '/open?path=') + encodeURIComponent(pptx)
    + '">下載完整 PowerPoint</a></li><li><a href="' + (hub ? '#local-file=' : HP.DESKTOP_ORIGIN + '/open?path=') + encodeURIComponent(jpg) + '">查看整體視覺預覽</a></li></ul>'
    + '<ul><li>桌面：<a href="' + (hub ? '#local-file=' : HP.DESKTOP_ORIGIN + '/open?path=') + encodeURIComponent(shot) + '">' + shot + '</a></li></ul>'
    + '<p>協定在 <a href="' + (hub ? '#local-file=' : HP.DESKTOP_ORIGIN + '/open?path=') + encodeURIComponent(doc) + '"><code>' + doc + '</code></a>。</p>' }];
const out = new URL('./out/filelinks/', import.meta.url);
fs.mkdirSync(out, { recursive: true });
const checks = [];
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const pages = HP.createPages(browser, { now });
try {
  const desktop = await pages.openDesktop('file-links-desktop', {
    init: LF.liveDesktopInit(now, { layout: layout('c1') }),
    onPost: (m, host) => { if (m.cmd === 'open') host.send(LF.localMessage(now, m.conversationId, '下載與預覽', { items: items(false) })); },
  });
  await desktop.page.getByRole('link', { name: '下載完整 PowerPoint' }).click();
  await desktop.page.locator('.dlg-local-file').waitFor();
  await desktop.page.screenshot({ path: new URL('desktop-powerpoint.png', out).pathname.replace(/^\/(\w:)/, '$1'), animations: 'disabled' });
  await desktop.page.locator('[data-field="localFileSave"]').click();
  assert.ok(await HP.until(() => desktop.host.posted.find((m) => m.cmd === 'saveFile' && m.path === pptx && m.conversationId === 'c1')));
  checks.push('桌面 PowerPoint 另存新檔保留完整中文路徑與對話');
  await desktop.page.locator('.dlg-local-file').waitFor({ state: 'detached' });
  await desktop.page.getByRole('link', { name: '下載完整 PowerPoint' }).click({ button: 'middle' });
  await desktop.page.locator('[data-field="localFileOpen"]').click();
  assert.ok(await HP.until(() => desktop.host.posted.find((m) => m.cmd === 'openPath' && m.path === pptx && m.conversationId === 'c1')));
  checks.push('桌面中鍵與預設程式開啟使用相同檔案');
  await desktop.page.locator('.dlg-local-file').waitFor({ state: 'detached' });
  await desktop.page.route(HP.DESKTOP_ORIGIN + '/media?*', (route) => route.fulfill({ status: 200, contentType: 'image/png', body: renderImage('預覽範例', 1280, 720) }));
  await desktop.page.getByRole('link', { name: '查看整體視覺預覽' }).click();
  await desktop.page.waitForFunction(() => document.querySelector('.lb-img')?.naturalWidth > 0);
  assert.equal(await desktop.page.locator('.lb').evaluate((el) => getComputedStyle(el).position), 'fixed');
  assert.equal(new URL(await desktop.page.locator('.lb-img').getAttribute('src')).searchParams.get('path'), jpg);
  await desktop.page.locator('.lb [data-field="mediaSave"]').click();
  assert.ok(await HP.until(() => desktop.host.posted.find((m) => m.cmd === 'saveFile' && m.path === jpg && m.conversationId === 'c1')));
  await desktop.page.screenshot({ path: new URL('desktop-preview.png', out).pathname.replace(/^\/(\w:)/, '$1'), animations: 'disabled' });
  checks.push('桌面 JPG 連結直接進入燈箱，並可另存原圖');
  await desktop.page.keyboard.press('Escape');
  await desktop.page.locator('.lb').waitFor({ state: 'detached' });
  const pathLink = desktop.page.getByRole('link', { name: shot });
  const codeLink = desktop.page.locator('.md a > code', { hasText: doc });
  const colors = await desktop.page.evaluate(([a, c]) => [a, c].map((s) => getComputedStyle(document.querySelector(s)).color),
    ['.md li a', '.md a > code']);
  assert.equal(colors[1], colors[0], '行內程式碼的路徑連結用連結色');
  await pathLink.scrollIntoViewIfNeeded();
  await pathLink.hover();
  await desktop.page.screenshot({ path: new URL('desktop-path-links.png', out).pathname.replace(/^\/(\w:)/, '$1'), animations: 'disabled' });
  await pathLink.click();
  await desktop.page.waitForFunction(() => document.querySelector('.lb-img')?.naturalWidth > 0);
  assert.equal(new URL(await desktop.page.locator('.lb-img').getAttribute('src')).searchParams.get('path'), shot);
  await desktop.page.keyboard.press('Escape');
  await desktop.page.locator('.lb').waitFor({ state: 'detached' });
  checks.push('桌面：文字裡的圖片路徑連結直接進燈箱');
  await codeLink.click();
  await desktop.page.locator('.dlg-local-file').waitFor();
  assert.equal((await desktop.page.locator('.dlg-local-file .file-ref-name').textContent()).trim(), 'web-protocol.md');
  await desktop.page.locator('[data-field="localFileOpen"]').click();
  assert.ok(await HP.until(() => desktop.host.posted.find((m) => m.cmd === 'openPath' && m.path === doc && m.conversationId === 'c1')));
  await desktop.page.locator('.dlg-local-file').waitFor({ state: 'detached' });
  checks.push('桌面：行內程式碼的路徑連結開「下載或開啟檔案」框，用預設程式開啟同一個檔');

  const id = LF.CONV.idle;
  const web = await pages.openHub('file-links-web', {
    init: (ready) => LF.liveBrowserInit(now, { mode: ready?.mode, layout: layout(id) }),
    hub: (hub) => {
      hub.onCmd('open', (m) => ({ push: [LF.conversationMessage(now, m.conversationId, '下載與預覽', { items: items(true) })] }));
      hub.onCmd('fileFetch', (m) => ({ push: [{ type: 'fileRef', requestId: m.requestId, conversationId: m.conversationId,
        url: m.path.endsWith('.jpg') ? '/f/artifact-jpg' : '/f/artifact-1', name: m.path.endsWith('.jpg') ? '水彩書法版_整體預覽.jpg' : '繽紛水彩書法版.pptx',
        size: m.path.endsWith('.jpg') ? 18233 : 43 * 1024 * 1024, expiresAt: now + 900000, error: null }] }));
    },
  });
  // 伺服器對二進位檔回 attachment；<img> 照樣顯示
  await web.page.route(HP.ORIGIN + '/f/artifact-jpg', (route) => route.fulfill({ status: 200, headers: { 'Content-Type': 'image/png',
    'Content-Disposition': "attachment; filename*=UTF-8''" + encodeURIComponent('水彩書法版_整體預覽.jpg'), 'Cache-Control': 'no-store' }, body: renderImage('網頁預覽', 1280, 720) }));
  await web.page.getByRole('link', { name: '下載完整 PowerPoint' }).click();
  await web.page.locator('[data-field="fileRefOpen"]').waitFor();
  assert.equal(web.hub.cmds('fileFetch')[0].cmd.path, pptx);
  assert.equal(web.hub.cmds('fileFetch')[0].cmd.conversationId, id);
  await web.page.screenshot({ path: new URL('web-powerpoint.png', out).pathname.replace(/^\/(\w:)/, '$1'), animations: 'disabled' });
  checks.push('網頁 PowerPoint 連結要求對話所在電腦的檔案，取得短效下載連結');
  assert.match(await web.page.locator('[data-field="fileRefInfo"]').textContent(), /43(\.0)? MB/);
  assert.equal(new URL(await web.page.locator('[data-field="fileRefOpen"]').getAttribute('href'), HP.ORIGIN).pathname, '/f/artifact-1');
  checks.push('網頁 43 MB 的簡報照實顯示大小，「開啟」指向短效下載網址');
  await web.page.keyboard.press('Escape');
  await web.page.locator('.dlg-file-ref').waitFor({ state: 'detached' });
  await web.page.getByRole('link', { name: '查看整體視覺預覽' }).click();
  await web.page.waitForFunction(() => document.querySelector('.lb-img')?.naturalWidth > 0);
  assert.equal(web.hub.cmds('fileFetch')[1].cmd.path, jpg);
  assert.equal(new URL(await web.page.locator('.lb-img').getAttribute('src'), HP.ORIGIN).pathname, '/f/artifact-jpg');
  await web.page.locator('.dlg-file-ref').waitFor({ state: 'detached', timeout: 3000 }); // 拿到網址就關掉讀取中的框（淡出後移除）
  const save = web.page.locator('.lb [data-field="mediaSave"]');
  assert.ok(await save.isVisible(), '網頁燈箱顯示「下載」');
  assert.equal((await save.textContent()).trim(), '下載');
  for (const label of ['用預設程式開啟', '在檔案總管顯示']) assert.ok(!(await web.page.locator('.lb .lb-btn', { hasText: label }).isVisible()), label + ' 在網頁藏起來');
  await web.page.screenshot({ path: new URL('web-preview.png', out).pathname.replace(/^\/(\w:)/, '$1'), animations: 'disabled' });
  const [download] = await Promise.all([web.page.waitForEvent('download'), save.click()]);
  assert.equal(download.suggestedFilename(), '水彩書法版_整體預覽.jpg');
  assert.equal(new URL(download.url()).pathname, '/f/artifact-jpg');
  checks.push('網頁 JPG 連結以 fileFetch 取得原圖，直接在燈箱預覽，「下載」存原檔');
  await web.page.keyboard.press('Escape');
  await web.page.locator('.lb').waitFor({ state: 'detached' });
  await web.page.getByRole('link', { name: shot }).click();
  assert.ok(await HP.until(() => web.hub.cmds('fileFetch').find((c) => c.cmd.path === shot && c.cmd.conversationId === id)));
  checks.push('網頁：文字裡的路徑連結向對話所在電腦送 fileFetch');

  for (const app of pages.pages) {
    assert.deepEqual(app.csp, [], app.name + ' CSP');
    assert.deepEqual(HP.unexpectedErrors(app), [], app.name + ' 主控台');
  }
  checks.push('兩種介面 CSP 與 JavaScript 錯誤皆為零');
  fs.writeFileSync(new URL('report.json', out), JSON.stringify({ ok: true, checks }, null, 2));
  console.log(JSON.stringify({ ok: true, checks }, null, 2));
} finally {
  await pages.closeAll();
  await browser.close();
}
