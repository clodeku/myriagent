// 電腦狀態面板的檔案傳輸的純規則（Web/devicefilesrules.js）：node --test devicefilesrules.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  transferMenu, normalizePath, crumbs, isDriveRoot, sortEntries, selectAt, toggleAll, selectedFiles, nextToStart, isFinished,
  percent, stateText, parseXfers, applyXfer, uploadProblem, locationLabel, NETWORK_ROOT, isNetworkPath, canUploadHere, MAX_ACTIVE_DOWNLOADS, MAX_ACTIVE_UPLOADS, MAX_UPLOAD_BYTES,
} from '../../src/Myriagent/Web/devicefilesrules.js';

const row = (o) => Object.assign({ key: 'd_x', id: 'd_x', self: false, hub: false, online: true, name: 'OFFICE' }, o);

test('transferMenu：這台與 Hub 主機沒有選單', () => {
  assert.equal(transferMenu({ row: row({ self: true }), device: null, hub: false, peer: 'live' }), null);
  assert.equal(transferMenu({ row: row({ hub: true, id: null }), device: null, hub: false, peer: 'live' }), null);
  assert.equal(transferMenu({ row: null, device: null, hub: true, peer: 'off' }), null);
});

test('transferMenu：可以操作、在線、連上伺服器才能用', () => {
  const ctl = { remoteControlLevel: 'Control' };
  assert.deepEqual(transferMenu({ row: row(), device: ctl, hub: false, peer: 'live' }), { enabled: true, reason: null });
  assert.deepEqual(transferMenu({ row: row(), device: ctl, hub: true, peer: 'off' }), { enabled: true, reason: null });
  assert.equal(transferMenu({ row: row(), device: ctl, hub: false, peer: 'down' }).enabled, false);
  assert.match(transferMenu({ row: row(), device: ctl, hub: false, peer: 'unsupported' }).reason, /版本/);
  assert.match(transferMenu({ row: row({ online: false }), device: ctl, hub: true, peer: 'off' }).reason, /離線/);
  assert.match(transferMenu({ row: row(), device: { remoteControlLevel: 'View' }, hub: true, peer: 'off' }).reason, /可以操作/);
  assert.equal(transferMenu({ row: row(), device: { remoteControlLevel: 'Off' }, hub: true, peer: 'off' }).enabled, false);
  // 政策不明（舊伺服器沒給）：不擋，交給那台判斷
  assert.equal(transferMenu({ row: row(), device: null, hub: true, peer: 'off' }).enabled, true);
});

test('normalizePath：引號、斜線、磁碟代號、結尾的反斜線', () => {
  assert.equal(normalizePath('  "d:/資料/報告/"  '), 'D:\\資料\\報告');
  assert.equal(normalizePath('c:'), 'C:\\');
  assert.equal(normalizePath('C:\\'), 'C:\\');
  assert.equal(normalizePath('e:\\'), 'E:\\');
  assert.equal(normalizePath(''), null);
  assert.equal(normalizePath('   '), null);
  assert.equal(normalizePath(null), null);
  assert.equal(normalizePath('\\\\server\\share'), '\\\\server\\share');
});

test('網路芳鄰：路徑整理、路徑列、哪幾層能放檔案', () => {
  assert.equal(normalizePath('//nas//資料/'), '\\\\nas\\資料');
  assert.equal(normalizePath('\\\\'), NETWORK_ROOT);
  assert.equal(normalizePath('  "\\\\NAS\\share\\a\\"  '), '\\\\NAS\\share\\a');
  assert.deepEqual(crumbs(NETWORK_ROOT), [{ label: '網路', path: NETWORK_ROOT }]);
  assert.deepEqual(crumbs('\\\\nas\\資料\\2026'), [
    { label: '網路', path: NETWORK_ROOT }, { label: 'nas', path: '\\\\nas' }, { label: '資料', path: '\\\\nas\\資料' }, { label: '2026', path: '\\\\nas\\資料\\2026' }]);
  assert.equal(isNetworkPath('\\\\nas'), true);
  assert.equal(isNetworkPath('C:\\'), false);
  assert.equal(canUploadHere(null), false);
  assert.equal(canUploadHere(NETWORK_ROOT), false);
  assert.equal(canUploadHere('\\\\nas'), false);
  assert.equal(canUploadHere('\\\\nas\\資料'), true);
  assert.equal(canUploadHere('Z:\\'), true);
  assert.equal(canUploadHere('C:\\a'), true);
});

test('crumbs：第一段是磁碟、之後逐層', () => {
  assert.deepEqual(crumbs(null), []);
  assert.deepEqual(crumbs('C:\\'), [{ label: 'C:', path: 'C:\\' }]);
  assert.deepEqual(crumbs('D:\\a\\b'), [{ label: 'D:', path: 'D:\\' }, { label: 'a', path: 'D:\\a' }, { label: 'b', path: 'D:\\a\\b' }]);
  assert.equal(crumbs('\\\\server\\share').length, 3, '網路路徑另有路徑列（網路芳鄰那一則）');
  assert.deepEqual(crumbs('relative\\x'), []);
  assert.equal(isDriveRoot('C:\\'), true);
  assert.equal(isDriveRoot('C:\\a'), false);
});

test('sortEntries：資料夾在前、名稱自然排序，不合格的略過', () => {
  const list = sortEntries([
    { name: 'b10.txt', path: 'C:\\b10.txt', kind: 'file' },
    { name: 'b2.txt', path: 'C:\\b2.txt', kind: 'file' },
    { name: 'z', path: 'C:\\z', kind: 'dir' },
    { name: 'a', path: 'C:\\a', kind: 'dir' },
    { name: '', path: 'C:\\x', kind: 'file' },
    null,
  ]);
  assert.deepEqual(list.map((e) => e.name), ['a', 'z', 'b2.txt', 'b10.txt']);
});

const entries = [
  { name: 'dir', path: 'C:\\dir', kind: 'dir' },
  { name: '1', path: 'C:\\1', kind: 'file' },
  { name: '2', path: 'C:\\2', kind: 'file' },
  { name: '3', path: 'C:\\3', kind: 'file' },
];

test('selectAt：單選、Ctrl 加選與取消、Shift 連選；資料夾不能選', () => {
  let r = selectAt(entries, new Set(), null, 1, 'plain');
  assert.deepEqual([...r.selected], ['C:\\1']);
  assert.equal(r.anchor, 1);
  r = selectAt(entries, r.selected, r.anchor, 3, 'toggle');
  assert.deepEqual([...r.selected].sort(), ['C:\\1', 'C:\\3']);
  r = selectAt(entries, r.selected, r.anchor, 3, 'toggle');
  assert.deepEqual([...r.selected], ['C:\\1']);
  r = selectAt(entries, new Set(), 1, 3, 'range');
  assert.deepEqual([...r.selected].sort(), ['C:\\1', 'C:\\2', 'C:\\3']);
  r = selectAt(entries, new Set(), 3, 1, 'range');
  assert.deepEqual([...r.selected].sort(), ['C:\\1', 'C:\\2', 'C:\\3'], '往上連選');
  r = selectAt([entries[1], entries[0], entries[2]], new Set(), 0, 2, 'range');
  assert.deepEqual([...r.selected].sort(), ['C:\\1', 'C:\\2'], '資料夾在範圍裡也不選');
  r = selectAt(entries, new Set(['C:\\2']), 2, 0, 'plain');
  assert.deepEqual([...r.selected], ['C:\\2'], '點資料夾不改選取');
});

test('toggleAll、selectedFiles：只算檔案、依清單順序', () => {
  const all = toggleAll(entries, new Set());
  assert.equal(all.size, 3);
  assert.equal(toggleAll(entries, all).size, 0);
  assert.deepEqual(selectedFiles(entries, new Set(['C:\\3', 'C:\\1', 'C:\\dir', 'C:\\gone'])).map((e) => e.name), ['1', '3']);
});

test('nextToStart：同方向的並行上限、依加入順序', () => {
  const t = (dir, state) => ({ dir, state });
  const list = [t('down', 'running'), t('down', 'queued'), t('down', 'queued'), t('down', 'queued'), t('up', 'queued'), t('up', 'queued'), t('up', 'queued')];
  assert.equal(nextToStart(list, 'down').length, MAX_ACTIVE_DOWNLOADS - 1);
  assert.equal(nextToStart(list, 'up').length, MAX_ACTIVE_UPLOADS);
  const busy = [t('up', 'uploading'), t('up', 'sending'), t('up', 'queued'), t('up', 'receiving')];
  assert.equal(nextToStart(busy, 'up').length, 0, '交給那台之後（receiving）不佔名額，上傳中與交出中佔');
  assert.equal(nextToStart(null, 'down').length, 0);
});

test('isFinished、percent', () => {
  assert.equal(isFinished({ state: 'saved' }), true);
  assert.equal(isFinished({ state: 'receiving' }), false);
  assert.equal(percent(5, 10), 50);
  assert.equal(percent(11, 10), 100);
  assert.equal(percent(0, 0), 100);
  assert.equal(percent(5, null), null);
});

test('stateText：各狀態的說明', () => {
  assert.equal(stateText({ state: 'running', done: 1, total: 4 }), '下載中 25%');
  assert.equal(stateText({ dir: 'down', state: 'done', browser: true }), '已交給瀏覽器下載');
  assert.equal(stateText({ state: 'receiving', done: 1, total: 2 }, '「A」'), '「A」接收中 50%');
  assert.match(stateText({ state: 'receiving', late: true }, '「A」'), /還沒有回報/);
  assert.equal(stateText({ state: 'saved', name: 'a.txt', savedName: 'a (2).txt' }), '已存成「a (2).txt」');
  assert.equal(stateText({ state: 'saved', name: 'a.txt', savedName: 'a.txt' }, '「A」'), '已存到「A」');
  assert.equal(stateText({ state: 'failed', error: '壞了' }), '壞了');
  assert.equal(stateText(null), '');
});

test('parseXfers、applyXfer：對上 uploadId 才套用', () => {
  const xs = parseXfers([{ id: 'u1', name: 'a', size: 10, done: 4, state: 'receiving' }, { id: 'u2', state: 'weird' }, null, { id: 'u3', name: 'b', size: 3, done: 3, state: 'saved', saved: 'b (2)' }]);
  assert.equal(xs.length, 2);
  const t = { uploadId: 'u1', state: 'receiving', done: 0 };
  assert.deepEqual(applyXfer(t, xs[0]), { state: 'receiving', done: 4, total: 10, late: false });
  assert.equal(applyXfer({ uploadId: 'u1', state: 'receiving', done: 4, late: false }, xs[0]), null, '沒變化回 null');
  assert.equal(applyXfer({ uploadId: 'other', state: 'receiving' }, xs[0]), null);
  assert.deepEqual(applyXfer({ uploadId: 'u3', state: 'receiving' }, xs[1]), { state: 'saved', done: 3, total: 3, savedName: 'b (2)', late: false });
  assert.equal(applyXfer({ uploadId: 'u3', state: 'saved' }, xs[1]), null, '已結束的不改');
  assert.equal(applyXfer({ uploadId: 'u9', state: 'receiving' }, { id: 'u9', state: 'failed' }).state, 'failed');
  assert.deepEqual(parseXfers('x'), []);
});

test('uploadProblem、locationLabel', () => {
  assert.equal(uploadProblem({ size: 10 }), null);
  assert.match(uploadProblem({ size: MAX_UPLOAD_BYTES + 1 }), /512 MB/);
  assert.match(uploadProblem({}), /大小/);
  assert.equal(locationLabel(null), '磁碟與常用資料夾');
  assert.equal(locationLabel('C:\\a'), 'C:\\a');
});
