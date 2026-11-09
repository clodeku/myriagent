// 電腦狀態面板的檔案傳輸（Web/devicefiles.js；2026-11-09）的探針情境：monitor-probe.mjs 的 runMonitor 最後呼叫（node probe.mjs --monitor）。
// 右鍵點 LAB 的卡片開選單 → 瀏覽（deviceBrowse 帶 commandId，假宿主回虛擬根、再回資料夾）→ Shift 連選三個檔 → 下載（deviceFetch → fileRef → peerXferSave → xferProgress）
// → 上傳（選兩個檔 → peerUpload {deviceId} → peerUpload 的結果 → devicePut → commandResult done → deviceStats 的 xfers 存好）。這台、Hub 主機沒有選單，離線的停用
import { syncStatus } from './sync-settings.mjs';

const DF_FOLDER = 'D:\\專案\\交付';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function until(fn, ms = 4000, step = 40) {
  const t0 = Date.now();
  while (Date.now() - t0 < ms) {
    const v = await fn();
    if (v) return v;
    await sleep(step);
  }
  return null;
}

function dfEntries() {
  const f = (name, size) => ({ name, path: DF_FOLDER + '\\' + name, kind: /\.(png|jpg)$/.test(name) ? 'image' : 'file', size, modified: Date.now() - 3600_000, hidden: false });
  return [{ name: '舊版', path: DF_FOLDER + '\\舊版', kind: 'dir', size: null, modified: null, hidden: false },
    f('規格書.docx', 2_400_000), f('報價單.xlsx', 88_000), f('封面.png', 4_100_000), f('<img src=x onerror=alert(1)>.txt', 12)];
}

const ROOT_ENTRIES = [
  { name: '本機磁碟 (C:)', path: 'C:\\', kind: 'dir', size: null, modified: null, hidden: false },
  { name: '資料 (D:)', path: 'D:\\', kind: 'dir', size: null, modified: null, hidden: false },
  { name: '桌面', path: 'C:\\Users\\lab\\Desktop', kind: 'dir', size: null, modified: null, hidden: false },
];

/** ctx：openApp、closeApp、check、shot、NOW；m：monitor-probe 的 { SELF, LAB, LAYOUT, devices, pumpSetup } */
export async function runDeviceFiles(ctx, vp, m) {
  const { openApp, closeApp, check, shot, NOW } = ctx;
  const remote = { mode: 'desktop', deviceId: m.SELF, devices: m.devices(NOW), sync: Object.assign({}, syncStatus(NOW, 'online'), { peer: true }) };
  const app = await openApp('devfiles-dark', {
    vp, layout: m.LAYOUT, remote,
    hostSetup: (host) => {
      m.pumpSetup({ paired: true })(host);
      const monitorCmd = host.onCmd;
      host.putIds = [];
      host.onCmd = (c, h) => {
        monitorCmd(c, h);
        const done = () => host.send({ type: 'commandResult', commandId: c.commandId, state: 'done' });
        if (c.cmd === 'deviceBrowse') {
          done();
          host.send(c.path
            ? { type: 'files', requestId: c.requestId, conversationId: null, path: DF_FOLDER, parent: 'D:\\專案', root: null, entries: dfEntries(), truncated: false, error: null }
            : { type: 'files', requestId: c.requestId, conversationId: null, path: null, parent: null, root: null, entries: ROOT_ENTRIES, truncated: false, error: null });
        } else if (c.cmd === 'deviceFetch') {
          done();
          host.send({ type: 'fileRef', requestId: c.requestId, conversationId: null, url: '/f/' + m.LAB + '-01J9QZ3K8M5R7T2V4W6X8Y0A1B', name: c.path.split('\\').pop(), size: 2_400_000, expiresAt: Date.now() + 7200_000, error: null });
        } else if (c.cmd === 'peerXferSave') {
          host.send({ type: 'xferProgress', transferId: c.transferId, state: 'running', done: 1_200_000, total: 2_400_000, path: null, name: null, error: null });
          setTimeout(() => host.send({ type: 'xferProgress', transferId: c.transferId, state: 'done', done: 2_400_000, total: 2_400_000, path: 'C:\\Users\\me\\Downloads\\' + c.name, name: c.name, error: null }), 300);
        } else if (c.cmd === 'peerUpload') {
          host.send({ type: 'peerUploadProgress', uploadId: c.uploadId, sent: 50, total: 100 });
          const file = (c.__files || [])[0] || { name: 'x', size: 1 };
          setTimeout(() => host.send({ type: 'peerUpload', uploadId: c.uploadId, status: 201, json: JSON.stringify({ uploadId: c.uploadId, sha256: 'a'.repeat(64), size: file.size }) }), 150);
        } else if (c.cmd === 'devicePut') {
          done();
          host.putIds = host.putIds.concat(c.uploads.map((u) => ({ id: u.uploadId, name: u.name, size: u.size })));
        }
      };
      // 那台接收的進度：送了 devicePut 之後，每則 deviceStats 的 LAB 帶 xfers（第一個同名改名）
      const send = host.send.bind(host);
      host.send = (msg) => {
        if (msg && msg.type === 'deviceStats' && host.putIds.length) {
          msg = Object.assign({}, msg, {
            devices: msg.devices.map((d) => (d.id === m.LAB
              ? Object.assign({}, d, { xfers: host.putIds.map((x, i) => ({ id: x.id, name: x.name, size: x.size, done: x.size, state: 'saved', saved: i === 0 ? x.name.replace('.', ' (2).') : x.name })) })
              : d)),
          });
        }
        return send(msg);
      };
    },
  });
  const { page, host } = app;
  await until(() => page.evaluate(() => document.querySelectorAll('#monitor .mon-dev[data-state="live"]').length >= 3), 5000);
  const cardSel = (name) => page.evaluate((n) => {
    const cards = Array.from(document.querySelectorAll('#monitor .mon-list > .mon-dev'));
    const i = cards.findIndex((c) => c.querySelector('.mon-name').textContent === n);
    return i < 0 ? null : '#monitor .mon-list > .mon-dev:nth-child(' + (i + 1) + ')';
  }, name);
  const menuItems = () => page.evaluate(() => Array.from(document.querySelectorAll('.menu.df-menu .menu-item')).map((b) => ({ label: b.querySelector('.mi-label').textContent, disabled: b.disabled })));

  // 這台與 Hub 主機沒有選單；離線的停用
  await page.click('#monitor .mon-dev[data-self="true"] .mon-name', { button: 'right' });
  const selfMenu = await page.evaluate(() => !!document.querySelector('.menu.df-menu'));
  await page.click('#monitor .mon-dev[data-kind="hub"] .mon-name', { button: 'right' });
  const hubMenu = await page.evaluate(() => !!document.querySelector('.menu.df-menu'));
  await page.click((await cardSel('HOME-PC')) + ' .mon-name', { button: 'right' });
  const offline = await until(menuItems, 1500);
  check('devfilesMenuScope', !selfMenu && !hubMenu && !!offline && offline.length === 2 && offline.every((x) => x.disabled), { selfMenu, hubMenu, offline });
  await page.keyboard.press('Escape');

  await page.click((await cardSel('LAB-WORKSTATION')) + ' .mon-name', { button: 'right' });
  const items = await until(menuItems, 1500);
  check('devfilesMenuItems', !!items && items.map((x) => x.label).join('|') === '瀏覽與下載檔案…|上傳檔案到這台…' && items.every((x) => !x.disabled), items);
  await shot(page, 'devfiles-menu-dark');
  await page.click('.menu.df-menu .menu-item');

  const rootBrowse = await until(() => host.last('deviceBrowse'), 2000);
  check('devfilesBrowseRoot', !!rootBrowse && rootBrowse.deviceId === m.LAB && rootBrowse.path === null && /^[0-9A-Z]{26}$/.test(rootBrowse.commandId || ''), rootBrowse);
  await until(() => page.evaluate(() => document.querySelectorAll('.df-dlg .df-row').length === 3), 2000);
  // 輸入路徑按 Enter 前往（引號、斜線、結尾的斜線都整理掉）
  await page.fill('.df-dlg .df-path', '"d:/專案/交付/"');
  await page.press('.df-dlg .df-path', 'Enter');
  const typed = await until(() => host.last('deviceBrowse', (c) => !!c.path), 2000);
  await until(() => page.evaluate(() => document.querySelectorAll('.df-dlg .df-row').length === 5), 2000);
  const listState = await page.evaluate(() => ({
    names: Array.from(document.querySelectorAll('.df-dlg .df-row .df-name')).map((n) => n.textContent),
    crumbs: Array.from(document.querySelectorAll('.df-dlg .df-crumb')).map((n) => n.textContent),
    img: !!document.querySelector('.df-dlg img'),
  }));
  check('devfilesBrowseFolder', !!typed && typed.path === DF_FOLDER && listState.names[0] === '舊版' && listState.crumbs.join('/') === '磁碟與常用資料夾/D:/專案/交付' && !listState.img,
    { typed, listState });

  // 點第一個檔、Shift 點第三個：三個檔
  const rows = page.locator('.df-dlg .df-row');
  await rows.nth(1).click();
  await rows.nth(3).click({ modifiers: ['Shift'] });
  const sel = await page.evaluate(() => ({
    on: document.querySelectorAll('.df-dlg .df-row.df-on').length,
    info: document.querySelector('.df-dlg .df-sel').textContent,
    btn: document.querySelector('.df-dlg .dlg-foot .btn.primary').textContent,
  }));
  check('devfilesSelectRange', sel.on === 3 && /已選 3 個檔案/.test(sel.info) && sel.btn.includes('下載 3 個檔案'), sel);
  await shot(page, 'devfiles-dialog-dark');
  await page.click('.df-dlg .dlg-foot .btn.primary');
  const fetched = await until(() => host.received.filter((c) => c.cmd === 'deviceFetch').length === 3 && host.received.filter((c) => c.cmd === 'peerXferSave').length === 3, 3000);
  const saves = host.received.filter((c) => c.cmd === 'peerXferSave');
  check('devfilesDownloadFlow', !!fetched && saves.every((c) => c.url.startsWith('/f/') && /^[0-9A-Z]{26}$/.test(c.transferId)), saves);
  const stateTexts = (dir) => page.evaluate((d) => Array.from(document.querySelectorAll('.df-dlg .df-x[data-dir="' + d + '"] .df-x-state')).map((n) => n.textContent), dir);
  const downDone = await until(async () => { const t = await stateTexts('down'); return t.length === 3 && t.every((x) => x === '已下載') ? t : null; }, 3000);
  check('devfilesDownloadDone', !!downDone, await stateTexts('down'));

  // 上傳兩個檔到目前的資料夾
  await page.setInputFiles('.df-dlg input[type=file]', [
    { name: '合約.pdf', mimeType: 'application/pdf', buffer: Buffer.alloc(300) },
    { name: '說明.txt', mimeType: 'text/plain', buffer: Buffer.from('hi') },
  ]);
  const puts = await until(() => (host.putIds.length === 2 ? host.received.filter((c) => c.cmd === 'devicePut') : null), 4000);
  const uploads = host.received.filter((c) => c.cmd === 'peerUpload');
  check('devfilesUploadFlow', !!puts && uploads.length === 2 && uploads.every((c) => c.deviceId === m.LAB && !c.conversationId && c.__files.length === 1)
    && puts.every((c) => c.folder === DF_FOLDER && c.uploads.length === 1 && c.uploads[0].sha256.length === 64), { uploads, puts });
  const upDone = await until(async () => { const t = await stateTexts('up'); return t.length === 2 && t.every((x) => /^已存/.test(x)) ? t : null; }, 5000);
  check('devfilesUploadSaved', !!upDone && upDone.includes('已存成「合約 (2).pdf」'), { upDone, now: await stateTexts('up') });
  await shot(page, 'devfiles-transfers-dark');
  clearInterval(host.statsTimer);
  await closeApp(app);
}
