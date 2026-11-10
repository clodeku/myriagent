// 電腦狀態面板的檔案傳輸的純規則（Web/devicefilesrules.js）：node --test devicefilesrules.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  transferMenu, normalizePath, crumbs, isDriveRoot, sortEntries, selectAt, toggleAll, selectedFiles, nextToStart, isFinished,
  percent, stateText, parseXfers, applyXfer, uploadProblem, locationLabel, NETWORK_ROOT, isNetworkPath, canUploadHere, MAX_ACTIVE_DOWNLOADS, MAX_ACTIVE_UPLOADS, MAX_UPLOAD_BYTES,
  selectedFolders, FETCH_MANY_BATCH, chunk, splitRelative, downloadSubfolder, uploadSubfolder, folderProgress, folderText, browserFolderDownloadProblem, uniqueBrowserName,
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

test('selectAt：單選、Ctrl 加選與取消、Shift 連選；資料夾要 Ctrl、Shift 或勾選框才選', () => {
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
  assert.deepEqual([...r.selected].sort(), ['C:\\1', 'C:\\2', 'C:\\dir'], '資料夾在範圍裡也選（整個資料夾的下載）');
  r = selectAt(entries, new Set(['C:\\2']), 2, 0, 'plain');
  assert.deepEqual([...r.selected], ['C:\\2'], '直接點資料夾不改選取（是進入）');
  r = selectAt(entries, new Set(['C:\\2']), 2, 0, 'toggle');
  assert.deepEqual([...r.selected].sort(), ['C:\\2', 'C:\\dir'], 'Ctrl 或勾選框選資料夾');
});

test('toggleAll（含資料夾）、selectedFiles（只算檔案）、selectedFolders：依清單順序', () => {
  const all = toggleAll(entries, new Set());
  assert.equal(all.size, 4);
  assert.deepEqual(selectedFolders(entries, all).map((e) => e.name), ['dir']);
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

test('整個資料夾：分批、相對路徑、存進的子資料夾', () => {
  assert.equal(FETCH_MANY_BATCH, 200);
  assert.deepEqual(chunk([1, 2, 3, 4, 5], 2), [[1, 2], [3, 4], [5]]);
  assert.deepEqual(chunk([], 2), []);
  assert.deepEqual(splitRelative('a\\b\\c.txt'), { dir: 'a\\b', name: 'c.txt' });
  assert.deepEqual(splitRelative('a/b/c.txt'), { dir: 'a\\b', name: 'c.txt' });
  assert.deepEqual(splitRelative('c.txt'), { dir: '', name: 'c.txt' });
  assert.equal(downloadSubfolder('photos', 'c.txt'), 'photos');
  assert.equal(downloadSubfolder('photos', '2024\\06\\c.jpg'), 'photos\\2024\\06');
  assert.equal(uploadSubfolder('photos/2024/a.jpg'), 'photos\\2024');
  assert.equal(uploadSubfolder('a.jpg'), '');
});

test('folderProgress、folderText：列清單、進行中、完成、部分失敗、取消', () => {
  const g = { dir: 'down', state: 'listing' };
  let p = folderProgress(g, []);
  assert.equal(p.state, 'listing');
  assert.equal(folderText(g, p), '列出資料夾裡的檔案…');
  const failed = { dir: 'down', state: 'failed', error: '那台不給' };
  assert.equal(folderText(failed, folderProgress(failed, [])), '那台不給');
  const run = { dir: 'down', state: 'running' };
  const kids = [
    { state: 'done', size: 100, done: 100 },
    { state: 'running', size: 100, done: 40 },
    { state: 'failed', size: 50, done: 0 },
  ];
  p = folderProgress(run, kids);
  assert.deepEqual(p, { files: 3, finished: 2, failed: 1, done: 140, total: 250, state: 'running' });
  assert.equal(folderText(run, p), '下載中 2 / 3 個檔，1 個失敗');
  kids[1].state = 'done';
  p = folderProgress(run, kids);
  assert.equal(p.state, 'done');
  assert.equal(folderText({ ...run, truncated: true }, p), '完成 2 / 3 個檔，1 個失敗（檔案太多，只傳了前 3 個）');
  assert.equal(folderProgress(run, [{ state: 'failed', size: 1 }]).state, 'failed');
  assert.equal(folderProgress(run, [{ state: 'cancelled', size: 1 }]).state, 'cancelled');
  assert.equal(folderText({ dir: 'up' }, { state: 'running', files: 2, finished: 0, failed: 0 }), '上傳中 0 / 2 個檔');
});

test('nextToStart 不排資料夾本身', () => {
  assert.deepEqual(nextToStart([{ kind: 'folder', dir: 'down', state: 'queued' }], 'down'), []);
});

test('瀏覽器版下載整個資料夾：要有 showDirectoryPicker；同名加 (2)', async () => {
  assert.equal(browserFolderDownloadProblem(true), null);
  assert.match(browserFolderDownloadProblem(false), /Chrome 或 Edge/);
  const have = new Set(['a.txt', 'a (2).txt', 'noext']);
  assert.equal(await uniqueBrowserName('a.txt', async (n) => have.has(n)), 'a (3).txt');
  assert.equal(await uniqueBrowserName('b.txt', async (n) => have.has(n)), 'b.txt');
  assert.equal(await uniqueBrowserName('noext', async (n) => have.has(n)), 'noext (2)');
  assert.equal(await uniqueBrowserName('x', async () => true), null);
});

// ───── 速度、經過與尚需時間、暫停（2026-11-10） ─────
import * as T from '../../src/Myriagent/Web/devicefilesrules.js';

test('pushSample：留最近 5 秒、done 倒退就重來、同一時間覆蓋', () => {
  let s = T.pushSample([], 0, 0);
  s = T.pushSample(s, 1000, 100);
  s = T.pushSample(s, 1000, 150);
  assert.deepEqual(s, [{ at: 0, done: 0 }, { at: 1000, done: 150 }]);
  s = T.pushSample(s, 7000, 900);
  assert.deepEqual(s.map((x) => x.at), [7000], '超過 5 秒的丟掉（最少留一筆）');
  s = T.pushSample([{ at: 0, done: 500 }], 100, 10);
  assert.deepEqual(s, [{ at: 100, done: 10 }], '從頭重傳');
  const orig = [{ at: 0, done: 0 }];
  T.pushSample(orig, 1, 1);
  assert.equal(orig.length, 1, '不改傳入的');
});

test('rateOf：視窗內的平均，最後一筆之後沒有進度也算時間；不到 1 秒算不出來', () => {
  const s = [{ at: 0, done: 0 }, { at: 2000, done: 2_000_000 }];
  assert.equal(T.rateOf(s, 2000), 1_000_000);
  assert.equal(T.rateOf(s, 4000), 500_000, '卡住時速度降下來');
  assert.equal(T.rateOf(s, 8000), 0, '超過 5 秒沒有進度');
  assert.equal(T.rateOf([{ at: 0, done: 0 }, { at: 500, done: 10 }], 600), null);
  assert.equal(T.rateOf([], 1000), null);
});

test('tickClock、elapsedMs：只有傳的狀態計時，暫停與排隊不算', () => {
  let c = T.tickClock(null, 'queued', 0);
  assert.deepEqual(c, { spent: 0, since: null });
  c = T.tickClock(c, 'running', 1000);
  assert.equal(T.elapsedMs(c, 4000), 3000);
  c = T.tickClock(c, 'paused', 5000);
  assert.deepEqual(c, { spent: 4000, since: null });
  assert.equal(T.elapsedMs(c, 99_000), 4000, '暫停中不走');
  c = T.tickClock(c, 'running', 10_000);
  c = T.tickClock(c, 'running', 11_000);
  assert.equal(c.since, 10_000, '已經在計時就不重設');
  assert.equal(T.elapsedMs(c, 12_000), 6000);
});

test('remainingMs、fmtRate、fmtDuration、metricsText', () => {
  assert.equal(T.remainingMs(25, 100, 25), 3000);
  assert.equal(T.remainingMs(25, 100, 0), null);
  assert.equal(T.remainingMs(25, null, 10), null);
  assert.equal(T.fmtRate(500), '500 B/s');
  assert.equal(T.fmtRate(12.34 * 1024 * 1024), '12.3 MB/s');
  assert.equal(T.fmtRate(1.5 * 1024), '1.50 KB/s');
  assert.equal(T.fmtRate(150 * 1024 * 1024), '150 MB/s');
  assert.equal(T.fmtDuration(42_900), '0:42');
  assert.equal(T.fmtDuration(725_000), '12:05');
  assert.equal(T.fmtDuration(3_723_000), '1:02:03');
  assert.equal(T.metricsText({ state: 'running', rate: 1024 * 1024, elapsed: 42_000, remaining: 190_000 }), '1.00 MB/s・已經過 0:42・尚需 3:10');
  assert.equal(T.metricsText({ state: 'running', rate: 0, elapsed: 5000, remaining: null }), '等待中・已經過 0:05');
  assert.equal(T.metricsText({ state: 'paused', rate: 100, elapsed: 5000, remaining: 1000 }), '已經過 0:05', '暫停中只留經過時間');
  assert.equal(T.metricsText({ state: 'queued', rate: null, elapsed: 0, remaining: null }), '');
});

test('pauseMode：排隊與等網址的停著、桌面下載交給宿主、瀏覽器的資料夾檔一段段寫、上傳從頭來、其他不能暫停', () => {
  assert.equal(T.pauseMode({ state: 'queued', dir: 'down' }, false), 'hold');
  assert.equal(T.pauseMode({ state: 'waiting', dir: 'down' }, true), 'hold');
  assert.equal(T.pauseMode({ state: 'fetching', dir: 'down' }, false), 'hold');
  assert.equal(T.pauseMode({ state: 'running', dir: 'down', transferId: 'x' }, false), 'host');
  assert.equal(T.pauseMode({ state: 'running', dir: 'down', dirHandle: {} }, true), 'stream');
  assert.equal(T.pauseMode({ state: 'running', dir: 'down' }, true), null, '瀏覽器自己下載的單檔不能控制');
  assert.equal(T.pauseMode({ state: 'uploading', dir: 'up' }, false), 'restart');
  assert.equal(T.pauseMode({ state: 'sending', dir: 'up' }, false), null, '已經交給那台');
  assert.equal(T.pauseMode({ state: 'receiving', dir: 'up' }, false), null);
  assert.equal(T.pauseMode({ state: 'done', dir: 'down' }, false), null);
  assert.equal(T.pauseMode({ state: 'paused', dir: 'down' }, false), null);
  const kids = [{ state: 'done' }, { state: 'queued', dir: 'down' }];
  assert.equal(T.canPause({ kind: 'folder' }, false, kids), true);
  assert.equal(T.canPause({ kind: 'folder', paused: true }, false, kids), false);
  assert.equal(T.canResume({ kind: 'folder' }, [{ state: 'paused' }]), true);
  assert.equal(T.canResume({ state: 'paused' }), true);
  assert.equal(T.stateText({ state: 'paused', done: 50, total: 100, pauseMode: 'restart' }), '已暫停 50%（繼續時從頭上傳）');
  assert.equal(T.stateText({ state: 'paused', done: 0, total: 100 }), '已暫停');
  assert.equal(T.isFinished({ state: 'paused' }), false, '暫停不算結束（不會被清掉）');
  assert.deepEqual(T.nextToStart([{ state: 'paused', dir: 'down' }, { state: 'queued', dir: 'down' }], 'down').map((t) => t.state), ['queued'], '暫停的不自動開始');
});

test('dockSummary、dockText：只算還沒結束的，合計速度、最長的尚需時間', () => {
  assert.equal(T.dockSummary([{ state: 'done' }]), null);
  const sum = T.dockSummary([
    { state: 'running', done: 50, total: 100, rate: 1024 * 1024, remaining: 3000 },
    { state: 'uploading', done: 0, total: 100, rate: 1024 * 1024, remaining: 9000 },
    { state: 'paused', done: 10, total: 100, rate: null, remaining: null },
    { state: 'saved', done: 100, total: 100 },
  ]);
  assert.deepEqual(sum, { count: 3, paused: 1, rate: 2 * 1024 * 1024, done: 60, total: 300, remaining: 9000, pct: 20 });
  assert.equal(T.dockText(sum), '傳輸中 2 個・暫停 1 個・2.00 MB/s・尚需 0:09');
  assert.equal(T.dockText(T.dockSummary([{ state: 'paused', done: 1, total: 2 }])), '已暫停 1 個傳輸');
});

test('segmentRange、parseContentRange：瀏覽器版一段一段要', () => {
  assert.equal(T.segmentRange(0, null), 'bytes=0-8388607');
  assert.equal(T.segmentRange(8388608, 10_000_000), 'bytes=8388608-9999999');
  assert.deepEqual(T.parseContentRange('bytes 0-99/1000'), { from: 0, to: 99, size: 1000 });
  assert.deepEqual(T.parseContentRange('bytes */0'), { from: null, to: null, size: 0 });
  assert.equal(T.parseContentRange('bytes 9-1/10'), null);
  assert.equal(T.parseContentRange(null), null);
});

/** 假的 /f/：照 Range 回 206（ETag v1）；cutAt(n)：第 n 次回應的本文只給前幾個位元組就斷 */
function fakeFile(bytes, { etag = '"v1"', cut = null, status = null } = {}) {
  const calls = [];
  const fetchRange = async (range, signal) => {
    const n = calls.length;
    calls.push(range);
    if (signal && signal.aborted) throw Object.assign(new Error('aborted'), { name: 'AbortError' });
    if (status && status(n)) return new Response(null, { status: status(n) });
    const m = /^bytes=(\d+)-(\d+)$/.exec(range);
    const from = Number(m[1]);
    const to = Math.min(Number(m[2]), bytes.length - 1);
    if (bytes.length === 0) return new Response(null, { status: 416, headers: { 'Content-Range': 'bytes */0' } });
    let part = bytes.slice(from, to + 1);
    const limit = cut ? cut(n) : null;
    if (limit != null) part = part.slice(0, limit);
    return new Response(part, { status: 206, headers: { 'Content-Range': `bytes ${from}-${to}/${bytes.length}`, ETag: typeof etag === 'function' ? etag(n) : etag } });
  };
  return { fetchRange, calls };
}

const fileBytes = (n) => { const b = new Uint8Array(n); for (let i = 0; i < n; i++) b[i] = (i * 31 + 7) & 255; return b; };
const sink = () => { const parts = []; return { parts, write: async (c) => { parts.push(c); }, bytes: () => Buffer.concat(parts.map((p) => Buffer.from(p))) }; };

test('streamSegments：一段一段要（每段 8 MB），寫完整、回報進度', async () => {
  const file = fileBytes(20 * 1024 * 1024 + 5);
  const f = fakeFile(file);
  const out = sink();
  let last = null;
  const r = await T.streamSegments({ fetchRange: f.fetchRange, write: out.write, onProgress: (g, t, e) => { last = { g, t, e }; } });
  assert.deepEqual(r, { got: file.length, total: file.length, etag: '"v1"' });
  assert.deepEqual(f.calls, ['bytes=0-8388607', 'bytes=8388608-16777215', 'bytes=16777216-20971524']);
  assert.ok(out.bytes().equals(Buffer.from(file)));
  assert.deepEqual(last, { g: file.length, t: file.length, e: '"v1"' });
});

test('streamSegments：從暫停的位置接著要；檔案變了（總長或 ETag 不同）丟錯；本文提早結束丟錯', async () => {
  const file = fileBytes(10 * 1024 * 1024);
  const f = fakeFile(file);
  const out = sink();
  const r = await T.streamSegments({ got: 8388608, total: file.length, etag: '"v1"', fetchRange: f.fetchRange, write: out.write });
  assert.equal(r.got, file.length);
  assert.deepEqual(f.calls, ['bytes=8388608-10485759']);
  assert.ok(out.bytes().equals(Buffer.from(file.slice(8388608))));
  await assert.rejects(T.streamSegments({ got: 100, total: file.length, etag: '"v0"', fetchRange: f.fetchRange, write: sink().write }), /變了/);
  await assert.rejects(T.streamSegments({ got: 100, total: 999, fetchRange: f.fetchRange, write: sink().write }), /變了/);
  const short = fakeFile(file, { cut: (n) => (n === 0 ? 1000 : null) });
  await assert.rejects(T.streamSegments({ fetchRange: short.fetchRange, write: sink().write }), /提早結束/);
  const bad = fakeFile(file, { status: () => 503 });
  await assert.rejects(T.streamSegments({ fetchRange: bad.fetchRange, write: sink().write }), /HTTP 503/);
});

test('streamSegments：中斷（暫停）時丟 AbortError，進度停在已經寫進去的位置，之後照那裡接著要就完整', async () => {
  const file = fileBytes(20 * 1024 * 1024);
  const ctl = new AbortController();
  const f = fakeFile(file);
  const out = sink();
  let pos = 0;
  const fetchRange = (range, signal) => { if (f.calls.length === 1) ctl.abort(); return f.fetchRange(range, signal); };
  await assert.rejects(T.streamSegments({ fetchRange, write: out.write, signal: ctl.signal, onProgress: (g) => { pos = g; } }), (e) => e.name === 'AbortError');
  assert.equal(pos, 8388608);
  const r = await T.streamSegments({ got: pos, total: file.length, etag: '"v1"', fetchRange: f.fetchRange, write: out.write });
  assert.equal(r.got, file.length);
  assert.ok(out.bytes().equals(Buffer.from(file)), '前半＋接著要的後半＝整個檔');
});

test('streamSegments：空的檔（416 bytes */0）、已知總長是 0 不必要', async () => {
  const f = fakeFile(new Uint8Array(0));
  assert.deepEqual(await T.streamSegments({ fetchRange: f.fetchRange, write: sink().write }), { got: 0, total: 0, etag: null });
  const g = fakeFile(new Uint8Array(0));
  assert.deepEqual(await T.streamSegments({ total: 0, fetchRange: g.fetchRange, write: sink().write }), { got: 0, total: 0, etag: null });
  assert.equal(g.calls.length, 0);
});
