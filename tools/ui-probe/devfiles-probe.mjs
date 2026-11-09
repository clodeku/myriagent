// 電腦狀態面板的檔案傳輸（Web/devicefiles.js；2026-11-09）的探針情境：monitor-probe.mjs 的 runMonitor 最後呼叫（node probe.mjs --monitor）。
// 右鍵點 LAB 的卡片開選單 → 瀏覽（deviceBrowse 帶 commandId，假宿主回虛擬根、再回資料夾）→ Shift 連選三個檔 → 下載（deviceFetch → fileRef → peerXferSave → xferProgress）
// → 上傳（選兩個檔 → peerUpload {deviceId} → peerUpload 的結果 → devicePut → commandResult done → deviceStats 的 xfers 存好）。這台、Hub 主機沒有選單，離線的停用
// → 整個資料夾的下載（Ctrl 選資料夾 → deviceTree → fileTree → deviceFetchMany → fileRefs → peerXferSave 帶 subfolder）與上傳（webkitdirectory → devicePut 帶 subfolder）
import { syncStatus } from './sync-settings.mjs';

const DF_FOLDER = 'D:\\專案\\交付';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

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
        if (c.cmd === 'deviceBrowse' && c.path === '\\\\') {
          // 網路芳鄰的「網路」找不到電腦：帶路徑與 error
          done();
          host.send({ type: 'files', requestId: c.requestId, conversationId: null, path: '\\\\', parent: null, root: null, entries: [], truncated: false,
            error: '沒有找到網路上的電腦（那台的網路探索可能關著）：請在路徑列輸入 \\\\電腦名稱 或 \\\\電腦名稱\\分享名稱' });
        } else if (c.cmd === 'deviceBrowse') {
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
        } else if (c.cmd === 'deviceTree') {
          done();
          host.send({ type: 'fileTree', requestId: c.requestId, conversationId: null, root: c.path, name: c.path.split('\\').pop(),
            files: [{ path: 'v1.docx', size: 100 }, { path: '圖\\a.png', size: 200 }], dirs: ['圖'], truncated: false, error: null });
        } else if (c.cmd === 'deviceFetchMany') {
          done();
          const rels = JSON.parse(c.pathsJson);
          host.send({ type: 'fileRefs', requestId: c.requestId, conversationId: null, root: c.root, expiresAt: Date.now() + 7200_000,
            refs: rels.map((r, i) => ({ path: r, name: r.split('\\').pop(), size: 100 * (i + 1), url: '/f/' + m.LAB + '-01J9QZ3K8M5R7T2V4W6X8Y0AB' + i })) });
        } else if (c.cmd === 'deskOpen') {
          host.deskSession = c.requestId;
          done();
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
  check('devfilesMenuScope', !selfMenu && !hubMenu && !!offline && offline.length === 3 && offline.every((x) => x.disabled), { selfMenu, hubMenu, offline });
  await page.keyboard.press('Escape');

  await page.click((await cardSel('LAB-WORKSTATION')) + ' .mon-name', { button: 'right' });
  const items = await until(menuItems, 1500);
  check('devfilesMenuItems', !!items && items.map((x) => x.label).join('|') === '瀏覽與下載檔案…|上傳檔案到這台…|遠端桌面…' && items.every((x) => !x.disabled), items);
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
  await page.setInputFiles('.df-dlg input[type=file]:not([webkitdirectory])', [
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

  // 整個資料夾的下載：Ctrl 點「舊版」選取（直接點是進入），下載 → deviceTree → deviceFetchMany → 各檔 peerXferSave 帶 subfolder
  await rows.nth(0).click({ modifiers: ['Control'] });
  const fsel = await page.evaluate(() => ({
    on: Array.from(document.querySelectorAll('.df-dlg .df-row.df-on .df-name')).map((n) => n.textContent),
    info: document.querySelector('.df-dlg .df-sel').textContent,
    btn: document.querySelector('.df-dlg .dlg-foot .btn.primary').textContent,
  }));
  check('devfilesFolderSelect', fsel.on.join('|') === '舊版' && /已選 1 個資料夾/.test(fsel.info) && fsel.btn.includes('下載 1 個資料夾'), fsel);
  await page.click('.df-dlg .dlg-foot .btn.primary');
  const tree = await until(() => host.last('deviceTree'), 2000);
  const folderSaves = await until(() => { const x = host.received.filter((c) => c.cmd === 'peerXferSave' && c.subfolder); return x.length === 2 ? x : null; }, 3000);
  const many = host.last('deviceFetchMany');
  check('devfilesFolderDownloadFlow', !!tree && tree.path === DF_FOLDER + '\\舊版' && /^[0-9A-Z]{26}$/.test(tree.commandId || '') && !!many
    && JSON.parse(many.pathsJson).join('|') === 'v1.docx|圖\\a.png' && !!folderSaves
    && folderSaves.map((c) => c.subfolder + '/' + c.name).sort().join('|') === '舊版/v1.docx|舊版\\圖/a.png',
    { tree, many, folderSaves });
  const folderRowText = (dir) => page.evaluate((d) => {
    const row = document.querySelector('.df-dlg .df-x[data-kind="folder"][data-dir="' + d + '"]');
    return row ? row.querySelector('.df-x-name').textContent + '：' + row.querySelector('.df-x-state').textContent : null;
  }, dir);
  const fdone = await until(async () => { const t = await folderRowText('down'); return t && t.includes('完成 2 / 2 個檔') ? t : null; }, 3000);
  check('devfilesFolderDownloadDone', !!fdone && fdone.startsWith('舊版：') && (await stateTexts('down')).length === 4, { fdone, rows: await stateTexts('down') });

  // 整個資料夾的上傳：選一個這台的資料夾（webkitdirectory），每個檔帶 subfolder（最上層的資料夾名稱＋相對資料夾）
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'devfiles-'));
  const upRoot = path.join(tmp, '照片');
  fs.mkdirSync(path.join(upRoot, '2024'), { recursive: true });
  fs.writeFileSync(path.join(upRoot, 'a.jpg'), 'aaaa');
  fs.writeFileSync(path.join(upRoot, '2024', 'b.jpg'), 'bb');
  const putsBefore = host.received.filter((c) => c.cmd === 'devicePut').length;
  await page.click('.df-dlg .dlg-foot .btn:not(.primary):not(.df-dest)');
  const upMenu = await until(menuItems, 1500);
  await page.keyboard.press('Escape');
  await page.setInputFiles('.df-dlg input[webkitdirectory]', upRoot);
  const folderPuts = await until(() => { const x = host.received.filter((c) => c.cmd === 'devicePut').slice(putsBefore); return x.length === 2 ? x : null; }, 5000);
  check('devfilesFolderUploadFlow', !!upMenu && upMenu.map((x) => x.label).join('|') === '上傳檔案…|上傳整個資料夾…' && !!folderPuts
    && folderPuts.map((c) => c.subfolder + '/' + c.uploads[0].name).sort().join('|') === '照片/a.jpg|照片\\2024/b.jpg' && folderPuts.every((c) => c.folder === DF_FOLDER),
    { upMenu, folderPuts });
  const fup = await until(async () => { const t = await folderRowText('up'); return t && t.includes('完成 2 / 2 個檔') ? t : null; }, 5000);
  check('devfilesFolderUploadDone', !!fup && fup.startsWith('照片：'), { fup, now: await folderRowText('up') });
  await shot(page, 'devfiles-folders-dark');
  fs.rmSync(tmp, { recursive: true, force: true });

  // 網路芳鄰：「網路」找不到電腦時也切到那一層（路徑列是「網路」、清單是空的、錯誤寫在下方），不能留在上一層
  await page.fill('.df-dlg .df-path', '\\\\');
  await page.press('.df-dlg .df-path', 'Enter');
  const net = await until(() => page.evaluate(() => {
    const crumbs = Array.from(document.querySelectorAll('.df-dlg .df-crumb')).map((n) => n.textContent);
    const err = document.querySelector('.df-dlg .df-status .df-err');
    return crumbs[crumbs.length - 1] === '網路' ? { crumbs, rows: document.querySelectorAll('.df-dlg .df-row').length, err: err ? err.textContent : null,
      upload: document.querySelector('.df-dlg .dlg-foot .btn:not(.primary):not(.df-dest)').disabled } : null;
  }), 2000);
  check('devfilesNetworkEmptyShowsLevel', !!net && net.crumbs.join('/') === '磁碟與常用資料夾/網路' && net.rows === 0 && /網路上的電腦/.test(net.err || '') && net.upload === true, net);
  await shot(page, 'devfiles-network-empty-dark');
  await page.keyboard.press('Escape');
  await until(() => page.evaluate(() => !document.querySelector('.df-dlg')), 2000);

  // 遠端桌面（remotedesk.js）：選單開 → deskOpen（帶 commandId）→ 假宿主送一個真的 JPEG 影格與游標 → 畫在 canvas 上；
  // 滑鼠點、鍵盤按 A 換成輸入事件，確認併在輸入裡送；那台結束時收起畫面並提示
  await page.click((await cardSel('LAB-WORKSTATION')) + ' .mon-name', { button: 'right' });
  await until(menuItems, 1500);
  await page.click('.menu.df-menu .menu-item:nth-of-type(3)');
  const open = await until(() => host.last('deskOpen'), 2000);
  check('deskOpenSent', !!open && open.deviceId === m.LAB && /^[0-9A-Z]{26}$/.test(open.requestId || '') && /^[0-9A-Z]{26}$/.test(open.commandId || ''), open);
  const jpeg = await page.evaluate(() => {
    const c = document.createElement('canvas');
    c.width = 64; c.height = 64;
    const x = c.getContext('2d');
    x.fillStyle = '#3b82f6'; x.fillRect(0, 0, 64, 64);
    x.fillStyle = '#fff'; x.fillRect(16, 16, 32, 32);
    return c.toDataURL('image/jpeg', 0.8).split(',')[1];
  });
  const png = await page.evaluate(() => {
    const c = document.createElement('canvas');
    c.width = 16; c.height = 16;
    const x = c.getContext('2d');
    x.fillStyle = '#fff'; x.beginPath(); x.moveTo(0, 0); x.lineTo(0, 14); x.lineTo(10, 10); x.fill();
    return c.toDataURL('image/png').split(',')[1];
  });
  const tiles = [];
  for (let y = 0; y < 270; y += 64) for (let x = 0; x < 480; x += 64) tiles.push({ x, y, w: 64, h: 64, d: jpeg });
  host.send({ type: 'desk', sessionId: open.requestId, kind: 'frame', seq: 1, body: { w: 480, h: 270, key: true, part: 0, parts: 2, screen: 0, q: 'auto',
    screens: [{ i: 0, name: '螢幕 1', w: 3840, h: 2160, primary: true }, { i: 1, name: '螢幕 2', w: 1920, h: 1080, primary: false }], tiles: tiles.slice(0, 10) } });
  host.send({ type: 'desk', sessionId: open.requestId, kind: 'frame', seq: 1, body: { w: 480, h: 270, part: 1, parts: 2, screen: 0, q: 'auto', tiles: tiles.slice(10),
    cur: { x: 0.5, y: 0.5, v: true, img: png, hx: 0, hy: 0, sw: 3840 } } });
  const drawn = await until(() => page.evaluate(() => {
    const c = document.querySelector('.rd-canvas');
    if (!c || c.width !== 480) return null;
    const px = c.getContext('2d').getImageData(32, 32, 1, 1).data;
    const cur = document.querySelector('.rd-cursor');
    return { w: c.width, h: c.height, px: Array.from(px).slice(0, 3), screenBtn: !document.querySelector('.rd-tool[title="螢幕"]').hidden,
      cursor: !!cur && !cur.hidden, status: document.querySelector('.rd-status').hidden };
  }), 3000);
  check('deskFrameDrawn', !!drawn && drawn.h === 270 && drawn.px[0] > 200 && drawn.px[2] > 200 && drawn.screenBtn && drawn.cursor && drawn.status, drawn);
  await shot(page, 'remotedesk-dark');
  // 點畫面正中間、按 A：確認併在輸入裡（每 66 ms 最多一包）
  const box = await page.locator('.rd-canvas').boundingBox();
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await page.keyboard.press('KeyA');
  const inputs = await until(() => {
    const list = host.received.filter((c) => c.cmd === 'desk' && c.kind === 'input');
    const events = list.flatMap((c) => (c.body.events || []));
    return events.some((e) => e.t === 'ku' && e.k === 0x41) ? { list, events } : null;
  }, 3000);
  const evs = inputs ? inputs.events : [];
  const down = evs.find((e) => e.t === 'md');
  check('deskInputSent', !!inputs && inputs.list.every((c) => c.sessionId === open.requestId) && inputs.list.some((c) => c.body.ack === 1)
    && !!down && down.b === 0 && Math.abs(down.x - 0.5) < 0.02 && Math.abs(down.y - 0.5) < 0.02 && evs.some((e) => e.t === 'kd' && e.k === 0x41),
    { inputs: inputs && inputs.list });
  // 那台結束：收起畫面、提示說明
  host.send({ type: 'desk', sessionId: open.requestId, kind: 'end', seq: 0, body: { reason: 'targetClosed', text: '那台的人中斷了遠端桌面' } });
  const ended = await until(() => page.evaluate(() => (!document.querySelector('.rd-wrap')
    ? Array.from(document.querySelectorAll('.toast')).map((t) => t.textContent).join('|') : null)), 2000);
  check('deskEnded', ended !== null && ended.includes('那台的人中斷了遠端桌面'), ended);
  clearInterval(host.statsTimer);
  await closeApp(app);
}
