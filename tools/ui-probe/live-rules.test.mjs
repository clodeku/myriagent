// 網頁即時檢視的純規則（W/liverules.js）、HTML 寫入點掃描（WP4.6 計畫步 1）與傳輸層 transport-hub.js 的新規則（步 4）的單元測試（node:test，不開瀏覽器）：
//   (cd tools/ui-probe && node --test live-rules.test.mjs)
// 規則模組沒有 import，以 data: URL 匯入（同 rules.test.mjs）；每次匯入尾端加不同的註解，避開 module 快取。
// transport-hub.js 是傳統腳本，以 node:vm 載入（同 rules.test.mjs 的 loadTransportHub）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { webcrypto } from 'node:crypto';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repo = path.resolve(here, '..', '..');
const webDir = path.join(repo, 'src', 'Myriagent', 'Web');

let importSeq = 0;
/** 以 data: URL 匯入 src/Myriagent/Web 底下的模組（沒有 import 的規則模組才能這樣做） */
async function importWeb(name) {
  const source = readFileSync(path.join(webDir, name), 'utf8') + `\n// live-rules.test ${++importSeq}\n`;
  return import('data:text/javascript;base64,' + Buffer.from(source, 'utf8').toString('base64'));
}

const L = await importWeb('liverules.js');

const NOW = 1_791_580_000_000;
const DAY = 24 * 60 * 60 * 1000;
const CONV_A = '0123456789abcdef'.repeat(2);
const CONV_B = 'a'.repeat(32);
const DRAFT = 'd01J9QABCDEFGHJKMNPQRSTVWXY';
const XSS = '<img src=x onerror="window.__xss=1">';

const dev = (over = {}) => Object.assign({ id: 'd_A', name: 'OFFICE-PC', online: true, level: 'Control', allowFull: true, known: true }, over);

// ───── id 與還原分頁 ─────

test('isConversationId_只收32個小寫十六進位', () => {
  assert.equal(L.isConversationId(CONV_A), true);
  assert.equal(L.isConversationId(CONV_B), true);
  assert.equal(L.isConversationId(CONV_A.toUpperCase()), false, '大寫');
  assert.equal(L.isConversationId(CONV_A.slice(1)), false, '31 字');
  assert.equal(L.isConversationId(CONV_A + '0'), false, '33 字');
  assert.equal(L.isConversationId(DRAFT), false, '草稿 id');
  assert.equal(L.isConversationId(null), false);
  assert.equal(L.isConversationId(123), false);
});

test('isDraftId_d加26字Crockford', () => {
  assert.equal(L.isDraftId(DRAFT), true);
  for (const bad of ['I', 'L', 'O', 'U']) assert.equal(L.isDraftId(DRAFT.slice(0, 26) + bad), false, '含 ' + bad);
  assert.equal(L.isDraftId(DRAFT.slice(0, 26)), false, '少一字');
  assert.equal(L.isDraftId(DRAFT + 'A'), false, '多一字');
  assert.equal(L.isDraftId('D' + DRAFT.slice(1)), false, '前綴大寫');
  assert.equal(L.isDraftId('d' + DRAFT.slice(1).toLowerCase()), false, '小寫');
  assert.equal(L.isDraftId(CONV_A), false);
  assert.equal(L.isDraftId(undefined), false);
});

test('viewingIds_只留對話id_去重_最多100', () => {
  const many = Array.from({ length: 101 }, (_, i) => i.toString(16).padStart(32, '0'));
  const out = L.viewingIds(many);
  assert.equal(out.length, 100);
  assert.deepEqual(out, many.slice(0, 100), '照原順序');
  assert.deepEqual(L.viewingIds([CONV_A, DRAFT, CONV_A, 'c1', null, CONV_B]), [CONV_A, CONV_B], '草稿與不合格的濾掉、去重');
  assert.deepEqual(L.viewingIds(null), []);
  assert.deepEqual(L.viewingIds('x'), []);
});

test('keepOnRestore_hub保留清單外的對話id與草稿_桌面照清單', () => {
  assert.equal(L.keepOnRestore(CONV_A, false, 'hub'), true, '清單外的對話 id');
  assert.equal(L.keepOnRestore(DRAFT, false, 'hub'), true, '草稿 id');
  assert.equal(L.keepOnRestore('c1', false, 'hub'), false, '不是 id 形狀');
  assert.equal(L.keepOnRestore('c1', true, 'hub'), true);
  assert.equal(L.keepOnRestore(CONV_A, false, 'desktop'), false, '桌面照清單');
  assert.equal(L.keepOnRestore(DRAFT, false, 'desktop'), false);
  assert.equal(L.keepOnRestore('c1', true, 'desktop'), true);
  // 〔同步〕桌面：其他電腦的對話清單還沒載入（replicasPending）時，清單外的對話 id 留著等（2026-10-29：重開機後其他電腦的對話分頁不見）
  assert.equal(L.keepOnRestore(CONV_A, false, 'desktop', true), true, '桌面等清單時留著清單外的對話 id');
  assert.equal(L.keepOnRestore(DRAFT, false, 'desktop', true), false, '草稿不等（宿主另以 restoredDrafts 交代）');
  assert.equal(L.keepOnRestore('c1', false, 'desktop', true), false, '不是 id 形狀');
  assert.equal(L.keepOnRestore(CONV_A, false, 'desktop', false), false, '清單載完了照清單');
});

test('awaitsList_只有桌面等清單時清單外的對話id', () => {
  assert.equal(L.awaitsList(CONV_A, false, 'desktop', true), true);
  assert.equal(L.awaitsList(CONV_A, true, 'desktop', true), false, '已經在清單就不等');
  assert.equal(L.awaitsList(CONV_A, false, 'desktop', false), false, '清單載完了');
  assert.equal(L.awaitsList(CONV_A, false, 'desktop', undefined), false, '舊宿主沒有這個鍵');
  assert.equal(L.awaitsList(CONV_A, false, 'hub', true), false, '瀏覽器版本來就保留');
  assert.equal(L.awaitsList(DRAFT, false, 'desktop', true), false);
});

test('originOf_草稿_瀏覽器_副本_本機', () => {
  assert.equal(L.originOf({ id: DRAFT, isDraft: true }, 'hub'), 'draft');
  assert.equal(L.originOf({ id: 'c1', isDraft: true }, 'desktop'), 'draft');
  assert.equal(L.originOf({ id: CONV_A, deviceId: 'd_A' }, 'hub'), 'remote');
  assert.equal(L.originOf({ id: CONV_A, deviceId: 'd_A' }, 'desktop'), 'replica', '清單的副本只有 deviceId');
  assert.equal(L.originOf({ id: CONV_A, replica: { running: false } }, 'desktop'), 'replica', '標頭的副本有 replica');
  assert.equal(L.originOf({ id: 'c1', deviceId: null }, 'desktop'), 'local');
  assert.equal(L.originOf(null, 'desktop'), 'local');
});

// ───── 所在電腦 ─────

test('deviceOf_查devices優先_缺少時未知電腦不丟例外', () => {
  const remote = { devices: [{ id: 'd_A', name: 'OFFICE-PC', online: true, remoteControlLevel: 'Control', allowRemoteFullAccess: false }] };
  assert.deepEqual(L.deviceOf('d_A', remote, { deviceName: 'OLD', deviceOnline: false }),
    { id: 'd_A', name: 'OFFICE-PC', online: true, level: 'Control', allowFull: false, known: true, reconnecting: false });
  assert.deepEqual(L.deviceOf('d_B', remote, { deviceName: 'HOME-PC', deviceOnline: false }),
    { id: 'd_B', name: 'HOME-PC', online: false, level: null, allowFull: null, known: false, reconnecting: false }, '找不到時用 fallback');
  const unknown = { id: null, name: '未知電腦', online: null, level: null, allowFull: null, known: false, reconnecting: false };
  assert.deepEqual(L.deviceOf(null, undefined, undefined), unknown);
  assert.deepEqual(L.deviceOf(undefined, null, null), unknown);
  assert.deepEqual(L.deviceOf('d_X', { devices: 'x' }, {}), Object.assign({}, unknown, { id: 'd_X' }));
  assert.equal(L.deviceOf('d_A', { devices: [{ id: 'd_A', remoteControlLevel: 'Bogus' }] }).level, null, '不認得的政策是 null');
  assert.equal(L.deviceOf('d_A', { devices: [{ id: 'd_A', name: XSS }] }).name, XSS, '名稱照原字串（呼叫端 textContent）');
});

test('deviceLine_線上離線執行中與未知電腦', () => {
  assert.equal(L.deviceLine('remote', dev(), { running: false }), '在 OFFICE-PC 執行・線上');
  assert.equal(L.deviceLine('remote', dev({ online: false }), null), '在 OFFICE-PC 執行・離線');
  assert.equal(L.deviceLine('replica', dev(), { running: true }), '在 OFFICE-PC 執行中・線上');
  assert.equal(L.deviceLine('remote', L.deviceOf(null, null, null), null), '在未知電腦執行');
  assert.equal(L.deviceLine('local', dev(), null), null);
  assert.equal(L.deviceLine('draft', dev(), null), null);
});

test('剛斷線的電腦_文字是重新連線中_閘門照樣擋', () => {
  const remote = { devices: [{ id: 'd_A', name: 'OFFICE-PC', online: false, reconnecting: true, remoteControlLevel: 'Control', allowRemoteFullAccess: true }] };
  const d = L.deviceOf('d_A', remote, null);
  assert.equal(d.reconnecting, true);
  assert.equal(L.deviceOf('d_A', { devices: [{ id: 'd_A', online: true, reconnecting: true }] }, null).reconnecting, false, '線上的不算（標記只給離線的）');
  assert.equal(L.deviceLine('remote', d, { running: true }), '在 OFFICE-PC 執行中・重新連線中');
  assert.deepEqual(L.composerGate({ mode: 'hub', origin: 'remote', header: {}, device: d, live: true }),
    { canSend: false, readOnly: false, reason: '電腦重新連線中：OFFICE-PC 連上後才能送出' });
  assert.deepEqual(L.cardGate({ mode: 'hub', origin: 'remote', device: d, live: true }),
    { enabled: false, reason: '電腦重新連線中：OFFICE-PC 連上後才能回應' });
  assert.deepEqual(L.attachGate({ mode: 'hub', origin: 'remote', device: d, live: true }),
    { enabled: false, reason: '電腦重新連線中：OFFICE-PC 連上後才能附加檔案' });
  assert.equal(L.composerGate({ mode: 'hub', origin: 'remote', header: {}, device: Object.assign({}, d, { reconnecting: false }), live: true }).reason,
    '電腦離線：OFFICE-PC 重新上線後才能送出', '過了寬限期照舊是離線');
});

// ───── 閘門 ─────

test('composerGate_副本先於離線_離線先於政策', () => {
  const header = { permission: 'FullAccess' };
  const all = dev({ online: false, level: 'View', allowFull: false });
  const replica = L.composerGate({ mode: 'hub', origin: 'replica', header, device: all, live: false, uploading: 1 });
  assert.deepEqual(replica, { canSend: false, readOnly: true, reason: '這是 OFFICE-PC 的對話（唯讀），到網頁版或 OFFICE-PC 上回應' });
  assert.equal(L.composerGate({ mode: 'hub', origin: 'remote', header, device: all, live: false, uploading: 1 }).reason,
    '目前沒有連線，連上之後才能送出', '沒連線先於離線');
  const offline = L.composerGate({ mode: 'hub', origin: 'remote', header, device: all, live: true, uploading: 1 });
  assert.deepEqual(offline, { canSend: false, readOnly: false, reason: '電腦離線：OFFICE-PC 重新上線後才能送出' });
  assert.equal(L.composerGate({ mode: 'hub', origin: 'remote', header, device: dev({ level: 'View', allowFull: false }), live: true, uploading: 1 }).reason,
    '在 OFFICE-PC 的設定中允許遠端操作', '政策先於完全自動');
  assert.equal(L.composerGate({ mode: 'hub', origin: 'remote', header, device: dev({ allowFull: false }), live: true, uploading: 1 }).reason,
    'OFFICE-PC 沒有允許遠端完全自動，先把權限改成其他模式', '完全自動先於上傳中');
  assert.equal(L.composerGate({ mode: 'desktop', origin: 'replica', device: L.deviceOf('d_Z', null, null) }).reason,
    '這是未知電腦的對話（唯讀），到網頁版或未知電腦上回應');
});

test('composerGate_View與Off擋送出_level不明不擋', () => {
  for (const level of ['View', 'Off']) {
    const g = L.composerGate({ mode: 'hub', origin: 'remote', header: {}, device: dev({ level }), live: true });
    assert.deepEqual(g, { canSend: false, readOnly: false, reason: '在 OFFICE-PC 的設定中允許遠端操作' }, level);
  }
  assert.deepEqual(L.composerGate({ mode: 'hub', origin: 'remote', header: {}, device: dev({ level: null, known: false }), live: true }),
    { canSend: true, readOnly: false, reason: null });
  assert.equal(L.composerGate({ mode: 'hub', origin: 'remote', header: {}, device: L.deviceOf('d_Z', null, null), live: true }).canSend, true,
    '電腦不明不擋');
  assert.equal(L.composerGate({ mode: 'hub', origin: 'draft', header: {}, device: null, live: true }).canSend, true, '草稿還沒有電腦');
  assert.equal(L.composerGate({ mode: 'desktop', origin: 'local', header: {}, device: null }).canSend, true, '桌面不看 live 與政策');
});

test('composerGate_完全自動而且沒允許才擋_allowRemoteFullAccess為null不擋', () => {
  const g = (permission, allowFull) => L.composerGate({ mode: 'hub', origin: 'remote', header: { permission }, device: dev({ allowFull }), live: true });
  assert.equal(g('FullAccess', false).reason, 'OFFICE-PC 沒有允許遠端完全自動，先把權限改成其他模式');
  assert.equal(g('FullAccess', true).canSend, true);
  assert.equal(g('FullAccess', null).canSend, true, 'null 不擋');
  assert.equal(g('Ask', false).canSend, true, '不是完全自動不擋');
  assert.equal(g('AcceptEdits', false).canSend, true);
});

test('composerGate_上傳中擋送出', () => {
  assert.deepEqual(L.composerGate({ mode: 'hub', origin: 'remote', header: {}, device: dev(), live: true, uploading: 2 }),
    { canSend: false, readOnly: false, reason: '附件上傳中，完成後才能送出' });
  assert.equal(L.composerGate({ mode: 'hub', origin: 'remote', header: {}, device: dev(), live: true, uploading: 0 }).canSend, true);
  assert.equal(L.composerGate({ mode: 'hub', origin: 'remote', header: {}, device: dev(), live: true, uploading: false }).canSend, true);
  assert.equal(L.composerGate().canSend, true, '沒給參數不丟例外');
});

test('cardGate_副本離線政策停用並說明', () => {
  assert.deepEqual(L.cardGate({ mode: 'desktop', origin: 'replica', device: dev() }),
    { enabled: false, reason: '這是 OFFICE-PC 的對話（唯讀），到網頁版或 OFFICE-PC 上回應' });
  assert.deepEqual(L.cardGate({ mode: 'hub', origin: 'remote', device: dev({ online: false, level: 'View' }) }),
    { enabled: false, reason: '電腦離線：OFFICE-PC 重新上線後才能回應' });
  assert.deepEqual(L.cardGate({ mode: 'hub', origin: 'remote', device: dev({ level: 'View' }) }),
    { enabled: false, reason: '在 OFFICE-PC 的設定中允許遠端操作' });
  assert.equal(L.cardGate({ mode: 'hub', origin: 'remote', device: dev(), live: false }).reason, '目前沒有連線，連上之後才能回應');
  assert.deepEqual(L.cardGate({ mode: 'hub', origin: 'remote', device: dev() }), { enabled: true, reason: null });
  assert.deepEqual(L.cardGate({ mode: 'desktop', origin: 'local', device: null }), { enabled: true, reason: null });
});

test('permissionChoices_那台沒允許時FullAccess停用', () => {
  const modes = [{ value: 'Ask', label: '每步詢問', description: 'd' }, { value: 'FullAccess', label: '完全自動' }, { label: '沒有 value' }];
  const hub = L.permissionChoices(modes, dev({ allowFull: false }), 'hub');
  assert.deepEqual(hub.map((m) => [m.value, m.disabled]), [['Ask', false], ['FullAccess', true]]);
  assert.equal(hub[1].reason, 'OFFICE-PC 沒有允許遠端完全自動');
  assert.equal(L.permissionChoices(modes, dev({ allowFull: null }), 'hub')[1].disabled, false, 'null 不擋');
  assert.equal(L.permissionChoices(modes, dev({ allowFull: false }), 'desktop')[1].disabled, false, '桌面不擋');
  assert.deepEqual(L.permissionChoices(null, dev(), 'hub'), []);
});

test('statusLine_離線不轉圈_執行中轉圈', () => {
  for (const text of ['電腦離線', '伺服器正在整理這個對話', '等待電腦重新上傳']) {
    assert.deepEqual(L.statusLine({ running: false, status: text }, 'hub'), { spinning: false, text }, text);
  }
  assert.deepEqual(L.statusLine({ running: true, status: '執行指令：npm test' }, 'hub'), { spinning: true, text: '執行指令：npm test' });
  assert.deepEqual(L.statusLine({ running: false, status: '舊的狀態' }, 'desktop'), { spinning: false, text: null }, '桌面只在執行中顯示');
  assert.deepEqual(L.statusLine({ running: true, status: 'x' }, 'desktop'), { spinning: true, text: 'x' });
  assert.deepEqual(L.statusLine(null, 'hub'), { spinning: false, text: null });
});

test('statusLine_桌面的副本照擁有者那邊的執行中轉圈', () => {
  const replica = (running) => ({ running: false, status: '', replica: { running, waiting: false, itemsAvailable: true } });
  assert.deepEqual(L.statusLine(replica(true), 'desktop'), { spinning: true, text: '執行中…' },
    '副本的 header.running 是這台的（一律 false）：對話畫面的動畫與狀態列照 replica.running');
  assert.deepEqual(L.statusLine(replica(false), 'desktop'), { spinning: false, text: null });
  assert.deepEqual(L.statusLine({ running: false, status: null, replica: 'x' }, 'desktop'), { spinning: false, text: null }, 'replica 不是物件');
  assert.deepEqual(L.statusLine({ running: false, status: '電腦離線', replica: { running: true } }, 'hub'), { spinning: false, text: '電腦離線' },
    '瀏覽器版沒有 replica：只看伺服器給的 running');
});

test('backgroundLine_代理這一輪結束了背景工作還在跑_說明與滑過的清單', () => {
  const hd = (background, running = false) => ({ running, background });
  const one = L.backgroundLine(hd({ count: 1, tasks: [{ tool: '執行指令', title: 'npm test' }] }));
  assert.equal(one.count, 1);
  assert.equal(one.text, '等背景工作回報：npm test');
  assert.equal(one.title, '代理這一輪已結束，背景工作完成後會自動接著處理\n・npm test');
  const three = L.backgroundLine(hd({ count: 3, tasks: [{ tool: '執行指令', title: 'npm test' }, { tool: '子代理', title: null }] }));
  assert.equal(three.text, '等 3 個背景工作回報：npm test 等');
  assert.equal(three.title, '代理這一輪已結束，背景工作完成後會自動接著處理\n・npm test\n・子代理\n・另外 1 個', '沒有摘要的用工具名稱；對不到工具的另外計數');
  assert.equal(L.backgroundLine(hd({ count: 2, tasks: [] })).text, '等 2 個背景工作回報', '都對不到工具：只寫數量');
  assert.equal(L.backgroundLine(hd({ count: 2, tasks: [] })).title, '代理這一輪已結束，背景工作完成後會自動接著處理');
  assert.equal(L.backgroundLine(hd({ count: 1, tasks: [] }, true)), null, '執行中照執行中顯示');
  for (const bad of [null, undefined, 'x', [], { count: 0, tasks: [] }, { count: -1 }, { count: '2' }, { tasks: [{ title: 'a' }] }]) {
    assert.equal(L.backgroundLine(hd(bad)), null, JSON.stringify(bad));
  }
  assert.equal(L.backgroundLine(null), null);
  assert.equal(L.backgroundLine({ running: false, background: { count: 1, tasks: ['x', null, { title: '' }] } }).text, '等背景工作回報', '不合格的項目略過');
  assert.equal(one.startedAt, null, '沒有開始時間：狀態列不顯示計時');
});

test('backgroundLine_開始時間_狀態列的計時與滑過的清單寫各自跑了多久', () => {
  const T = 1_800_000_000_000;
  const hd = { running: false, background: { count: 3, startedAt: T - 3_725_000, tasks: [
    { tool: '執行指令', title: 'npm test', startedAt: T - 3_725_000 }, { tool: '子代理', title: null, startedAt: T - 30_000 }, { tool: '執行指令', title: 'npm run dev' }] } };
  const bg = L.backgroundLine(hd, T);
  assert.equal(bg.startedAt, T - 3_725_000, '最早開始的那一個（狀態列後面的計時器）');
  assert.equal(bg.text, '等 3 個背景工作回報：npm test 等', '計時另外放，不寫進說明');
  assert.equal(bg.title, '代理這一輪已結束，背景工作完成後會自動接著處理\n・npm test（已執行 1 小時 2 分）\n・子代理（已執行不到 1 分）\n・npm run dev',
    '各自跑了多久；沒有開始時間的不寫');
  assert.equal(L.backgroundLine(hd).title, '代理這一輪已結束，背景工作完成後會自動接著處理\n・npm test\n・子代理\n・npm run dev', '沒給現在時間：不寫跑了多久');
  for (const bad of [0, -5, '1', null, NaN]) {
    assert.equal(L.backgroundLine({ running: false, background: { count: 1, startedAt: bad, tasks: [] } }, T).startedAt, null, String(bad));
  }
});

test('backgroundStopMenu_停止背景工作的選單_能單獨停止的各一列_全部停止_只有一個時不重複', () => {
  const T = 1_800_000_000_000;
  const v = L.backgroundStopMenu({ count: 3, tasks: [
    { tool: '執行指令', title: 'npm run dev', startedAt: T - 12 * 60000, taskId: 'b1', stopId: 'b1' },
    { tool: '子代理', title: null, stopId: 'a1' },
    { tool: '背景工作', title: 'sleep 30' }] }, T);
  assert.deepEqual(v.rows, [
    { label: 'npm run dev', desc: '執行指令・已執行 12 分', stopId: 'b1' },
    { label: '子代理', desc: '', stopId: 'a1' }], '沒有 stopId 的（Antigravity、Grok）不能單獨停');
  assert.deepEqual(v.all, { label: '全部停止（3 個背景工作）' });
  assert.match(v.note, /不會自動接著處理/);

  const single = L.backgroundStopMenu({ count: 1, tasks: [{ tool: '執行指令', title: 'npm test', stopId: 'b1' }] }, T);
  assert.equal(single.rows.length, 1);
  assert.equal(single.all, null, '只有一個、就是能單獨停的那個：不再列「全部停止」');
  const grok = L.backgroundStopMenu({ count: 1, tasks: [{ tool: '背景工作', title: 'sleep 30' }] }, T);
  assert.deepEqual(grok.rows, []);
  assert.deepEqual(grok.all, { label: '停止背景工作' }, '只能全部停止');
  const long = L.backgroundStopMenu({ count: 1, tasks: [{ tool: '執行指令', title: 'x'.repeat(200), stopId: 'b1' }] }, T);
  assert.equal(long.rows[0].label, 'x'.repeat(80) + '…');

  for (const bad of [null, undefined, {}, { count: 0, tasks: [] }, { count: '2' }, []]) assert.equal(L.backgroundStopMenu(bad, T), null, JSON.stringify(bad));
});

test('detailTasks與taskDetailView_詳細對話框_在做什麼_輸出或看不到的原因_結束了', () => {
  const T = 1_800_000_000_000;
  assert.deepEqual(L.detailTasks({ count: 3, tasks: [{ key: 't1', tool: 'a' }, { tool: 'b' }, null, { key: '' }] }).map((t) => t.key), ['t1']);
  assert.deepEqual(L.detailTasks(null), []);
  const task = { key: 't1', tool: '執行指令', title: 'ping -n 30 127.0.0.1', startedAt: T - 12 * 60000 };

  const v = L.taskDetailView({ tool: '執行指令', explain: '等待一段時間…', agentNote: 'Wait', content: 'ping -n 30 127.0.0.1', contentLabel: '指令',
    output: 'a\r\nb', totalBytes: 2048, truncated: true }, task, T);
  assert.equal(v.ended, false);
  assert.equal(v.explain, '等待一段時間…');
  assert.equal(v.agentNote, 'Wait');
  assert.equal(v.facts, '執行指令・已執行 12 分');
  assert.equal(v.content, 'ping -n 30 127.0.0.1');
  assert.equal(v.contentLabel, '指令');
  assert.equal(v.hasOutput, true);
  assert.equal(v.text, 'a\nb');
  assert.equal(v.meta, '共 2.0 KB・只顯示最後的部分・每 3 秒更新');
  assert.equal(L.taskDetailView({ output: '' }, task, T).meta, '還沒有輸出・每 3 秒更新', 'Codex 的卡片還沒有輸出：沒有大小');

  const none = L.taskDetailView({ explain: 'x', outputNote: 'Grok 看不到背景工作執行中的輸出。' }, task, T);
  assert.equal(none.hasOutput, false);
  assert.equal(none.meta, 'Grok 看不到背景工作執行中的輸出。');
  assert.equal(none.contentLabel, '內容', '沒給標題時');
  assert.equal(L.taskDetailView({ explain: 'x' }, task, T).meta, '看不到這個背景工作的輸出');

  const ended = L.taskDetailView({ error: '這個背景工作已經結束了' }, task, T);
  assert.equal(ended.ended, true);
  assert.equal(ended.meta, '這個背景工作已經結束了');
  assert.equal(ended.hasOutput, false);
  assert.equal(L.taskDetailView({ explain: 'x' }, { tool: '子代理' }, T).facts, '子代理', '沒有開始時間：只寫工具');
});

test('taskStopAction_詳細裡的停止鈕_能單獨停的停這一個_不能的一起停', () => {
  assert.deepEqual(L.taskStopAction({ stopId: 'b1' }, { count: 3 }), { label: '停止這個背景工作', stopId: 'b1', title: '強制停止這個背景工作' });
  const grok = L.taskStopAction({ title: 'sleep 30' }, { count: 1 });
  assert.equal(grok.label, '停止背景工作');
  assert.equal(grok.stopId, null);
  assert.match(grok.title, /一起停止/);
  assert.equal(L.taskStopAction({}, { count: 2 }).label, '停止全部 2 個背景工作');
});

test('programsView_代理另開的背景程式_晶片文字與選單的每一列', () => {
  const now = Date.UTC(2026, 9, 24, 3, 0, 0);
  const one = L.programsView([{ id: '4321-1', pid: 4321, name: 'node.exe', command: 'node server.js', startedAt: now - 12 * 60000 - 5000 }], now);
  assert.equal(one.count, 1);
  assert.equal(one.label, '背景程式');
  assert.equal(one.title, '代理另開、脫離 CLI 還在跑的程式（跑完不會通知代理）\n・node server.js');
  assert.deepEqual(one.rows, [{ id: '4321-1', command: 'node server.js', desc: '已執行 12 分・PID 4321' }]);

  const many = L.programsView([
    { id: 'a', pid: 1, command: 'python job.py', startedAt: now - 30000 },
    { id: 'b', name: 'ping.exe', startedAt: now - (2 * 60 + 5) * 60000 },
    { id: 'c', pid: 3, command: 'x', startedAt: now - (26 * 60) * 60000 },
    { id: 'd' },
    { pid: 9, command: '沒有 id 的不列' }, null, 'x',
  ], now);
  assert.equal(many.label, '4 個背景程式');
  assert.deepEqual(many.rows.map((r) => [r.command, r.desc]), [
    ['python job.py', '已執行 不到 1 分・PID 1'],
    ['ping.exe', '已執行 2 小時 5 分'],
    ['x', '已執行 1 天 2 小時・PID 3'],
    ['（不明的程式）', ''],
  ], '沒有命令列用名稱；時間或 PID 不明的省略');
  for (const bad of [null, undefined, {}, 'x', [], [{ pid: 1 }]]) assert.equal(L.programsView(bad, now), null, JSON.stringify(bad));
});

test('backgroundWaiting_清單的background或標頭的background而且不在執行中', () => {
  assert.equal(L.backgroundWaiting({ running: false, background: true }), true, '清單（ConversationSummary.background）');
  assert.equal(L.backgroundWaiting({ running: true, background: true }), false, '執行中照執行中顯示');
  assert.equal(L.backgroundWaiting({ running: false, background: false }), false);
  assert.equal(L.backgroundWaiting({ running: false, background: { count: 1, tasks: [] } }), true, '標頭（Header.background）');
  assert.equal(L.backgroundWaiting({ running: false, background: null }), false);
  assert.equal(L.backgroundWaiting({ running: false, background: null, replica: { running: true } }), false, '副本沒有背景工作的資訊');
  assert.equal(L.backgroundWaiting(null), false);
});

// ───── 追蹤 ─────

test('trackSettle_同一個id第二則回null', () => {
  const t = L.trackNew();
  assert.equal(L.trackAdd(t, '01A', { k: 1 }, NOW), true);
  assert.deepEqual(L.trackSettle(t, { type: 'commandResult', commandId: '01A', state: 'done', code: null }),
    { info: { k: 1 }, state: 'done', code: null, origin: null });
  assert.equal(L.trackSettle(t, { type: 'commandResult', commandId: '01A', state: 'rejected', code: 'stale' }), null);
});

test('trackSettle_不認得的id回null', () => {
  const t = L.trackNew();
  L.trackAdd(t, '01A', {}, NOW);
  assert.equal(L.trackSettle(t, { commandId: '01B', state: 'done' }), null);
  assert.equal(L.trackSettle(t, { state: 'done' }), null);
  assert.equal(L.trackSettle(t, null), null);
  assert.equal(L.trackAdd(t, '01A', {}, NOW), false, '同一個 id 不重複登記');
  assert.equal(L.trackAdd(t, null, {}, NOW), false);
  assert.equal(L.trackAdd(t, '', {}, NOW), false);
  assert.equal(L.trackSettle(t, { commandId: '01A', state: 'weird' }).state, 'unknown', '不認得的 state 當成 unknown');
});

test('trackSettle_帶回origin', () => {
  const t = L.trackNew();
  L.trackAdd(t, '01A', 'send', NOW);
  assert.deepEqual(L.trackSettle(t, { commandId: '01A', state: 'rejected', code: 'not_sent', origin: 'transport' }),
    { info: 'send', state: 'rejected', code: 'not_sent', origin: 'transport' });
});

test('trackAbandon_回全部並清空', () => {
  const t = L.trackNew();
  L.trackAdd(t, '01A', 'a', NOW);
  L.trackAdd(t, '01B', 'b', NOW);
  assert.deepEqual(L.trackAbandon(t), ['a', 'b']);
  assert.deepEqual(L.trackAbandon(t), []);
  assert.equal(L.trackSettle(t, { commandId: '01A', state: 'done' }), null);
});

test('trackExpire_150000不過期_150001過期', () => {
  const t = L.trackNew();
  L.trackAdd(t, '01A', 'a', NOW);
  L.trackAdd(t, '01B', 'b', NOW + 10);
  assert.deepEqual(L.trackExpire(t, NOW + 150000), []);
  assert.deepEqual(L.trackExpire(t, NOW + 150001), ['a']);
  assert.deepEqual(L.trackExpire(t, NOW + 150001), [], '過期的已移除');
  assert.deepEqual(L.trackExpire(t, NOW + 150011), ['b']);
  assert.equal(L.TRACK_MAX_MS, 150000);
});

test('sendRestorePlan_只有傳輸層的rate_limited與not_sent', () => {
  assert.equal(L.sendRestorePlan({ state: 'rejected', code: 'rate_limited', origin: 'transport' }), true);
  assert.equal(L.sendRestorePlan({ state: 'rejected', code: 'not_sent', origin: 'transport' }), true);
  assert.equal(L.sendRestorePlan({ state: 'rejected', code: 'rate_limited', origin: null }), false, '伺服器的 rejected');
  assert.equal(L.sendRestorePlan({ state: 'rejected', code: 'invalid', origin: 'transport' }), false, '傳輸層的 invalid');
  assert.equal(L.sendRestorePlan({ state: 'unknown', code: null, origin: null }), false);
  assert.equal(L.sendRestorePlan({ state: 'done', code: null, origin: null }), false);
  assert.equal(L.sendRestorePlan(null), false);
});

test('settleText_只有unknown有說明', () => {
  assert.equal(L.settleText('unknown'), '結果不明，請看對話內容確認');
  assert.equal(L.settleText('rejected'), null);
  assert.equal(L.settleText('done'), null);
});

// ───── 通知、預設密碼、換版 ─────

test('notifyPlan_隱藏而且授權才系統通知_設定關閉一律不顯示', () => {
  const msg = { type: 'notify', conversationId: CONV_A, kind: 'completed', title: 't', deviceName: 'OFFICE-PC' };
  assert.equal(L.notifyPlan(msg, { enabled: true, hidden: true, permission: 'granted' }), 'system');
  assert.equal(L.notifyPlan(msg, { enabled: true, hidden: true, permission: 'default' }), 'toast');
  assert.equal(L.notifyPlan(msg, { enabled: true, hidden: true, permission: 'denied' }), 'toast');
  assert.equal(L.notifyPlan(msg, { enabled: true, hidden: false, permission: 'granted' }), 'toast');
  for (const hidden of [true, false]) assert.equal(L.notifyPlan(msg, { enabled: false, hidden, permission: 'granted' }), 'none');
  assert.equal(L.notifyPlan(null, { enabled: true }), 'none');
});

test('notifyText_標題帶電腦名稱_本文依kind', () => {
  assert.deepEqual(L.notifyText({ kind: 'completed', title: '修正登入流程', deviceName: 'OFFICE-PC' }), { title: 'OFFICE-PC：修正登入流程', body: '已完成' });
  assert.deepEqual(L.notifyText({ kind: 'approval', title: XSS, deviceName: null }), { title: XSS, body: '等你核准' });
  assert.equal(L.notifyText({ kind: 'question' }).body, '有問題等你回答');
  assert.equal(L.notifyText({ kind: 'failed' }).body, '發生錯誤，沒有完成');
  assert.deepEqual(L.notifyText({ kind: 'x' }), { title: '對話', body: '有新的動態' });
  assert.equal(L.notifyText({ kind: 'constructor' }).body, '有新的動態', '原型上的名稱不算認得');
});

test('controlBanner_只在defaultPassword為true', () => {
  assert.deepEqual(L.controlBanner({ type: 'control', tier1Until: null, tier2Until: null, defaultPassword: true }),
    { text: '密碼還是預設值，知道它的人都能遠端操作你開了遠端控制的電腦', href: '/', linkText: '改密碼' });
  assert.equal(L.controlBanner({ defaultPassword: false }), null);
  assert.equal(L.controlBanner({ defaultPassword: 'true' }), null);
  assert.equal(L.controlBanner(null), null);
});

test('buildChanged_缺少與非字串當null_有變成沒有也算換版_相同不算', () => {
  assert.equal(L.buildChanged('abc1234', undefined), true);
  assert.equal(L.buildChanged('abc1234', 7), true);
  assert.equal(L.buildChanged(null, 'abc1234'), true);
  assert.equal(L.buildChanged('abc1234', 'def5678'), true);
  assert.equal(L.buildChanged('abc1234', 'abc1234'), false);
  assert.equal(L.buildChanged(undefined, ''), false);
  assert.equal(L.normBuild(''), null);
  assert.equal(L.normBuild(7), null);
  assert.equal(L.normBuild('abc1234'), 'abc1234');
});

test('reloadPlan_會弄丟的輸入或對話框或指令在路上先等_60秒內載入過先等_儲存不能用只顯示橫幅', () => {
  const base = { hasUnsentInput: false, dialogOpen: false, lastReloadAt: null, now: NOW };
  const plan = (o) => L.reloadPlan(Object.assign({}, base, o));
  assert.equal(L.reloadPlan(base), 'auto');
  assert.equal(L.reloadPlan(), 'auto', '什麼都沒給：沒有要等的');
  assert.equal(plan({ hasUnsentInput: true }), 'wait');
  assert.equal(plan({ dialogOpen: true }), 'wait');
  assert.equal(plan({ busy: true }), 'wait', '有指令還在等結果');
  assert.equal(plan({ lastReloadAt: NOW - 59999 }), 'wait');
  assert.equal(plan({ lastReloadAt: NOW - 60000 }), 'auto');
  assert.equal(plan({ lastReloadAt: 'x' }), 'auto', '讀到不是數字當成沒載入過');
  assert.equal(plan({ storageOk: false }), 'banner', '儲存不能用時不自動重新載入');
  assert.equal(plan({ storageOk: false, hasUnsentInput: true, busy: true }), 'banner', '儲存不能用優先：之後也不會自動載入');
  // 會弄丟的東西與對話框：分頁看不到時照樣等
  assert.equal(plan({ hasUnsentInput: true, hidden: true }), 'wait');
  assert.equal(plan({ dialogOpen: true, hidden: true }), 'wait');
  assert.equal(plan({ lastReloadAt: NOW - 1000, hidden: true }), 'wait', '60 秒的限制不看分頁');
});

test('reloadPlan_看得到分頁時正在打字或10秒內操作過先等_看不到時不等', () => {
  const base = { hasUnsentInput: false, dialogOpen: false, lastReloadAt: null, now: NOW };
  const plan = (o) => L.reloadPlan(Object.assign({}, base, o));
  assert.equal(L.RELOAD_IDLE_MS, 10000);
  assert.equal(plan({ editing: true }), 'wait');
  assert.equal(plan({ editing: true, hidden: true }), 'auto');
  assert.equal(plan({ editing: true, hidden: false }), 'wait');
  assert.equal(plan({ lastInputAt: NOW - 9999 }), 'wait');
  assert.equal(plan({ lastInputAt: NOW - 10000 }), 'auto');
  assert.equal(plan({ lastInputAt: NOW - 1, hidden: true }), 'auto');
  assert.equal(plan({ lastInputAt: null }), 'auto', '沒有操作過');
  assert.equal(plan({ lastInputAt: 'x' }), 'auto', '不是數字當成沒有操作過');
  assert.equal(plan({ hidden: 'yes', editing: true }), 'wait', '只有 true 才算看不到');
});

test('reloadDraftsJson_只留合格的草稿_同一個對話留第一筆_最多100筆_沒有回null', () => {
  assert.equal(L.reloadDraftsJson([]), null);
  assert.equal(L.reloadDraftsJson(null), null);
  assert.equal(L.reloadDraftsJson([{ conversationId: CONV_A, text: '   ' }]), null, '只有空白不存');
  const json = L.reloadDraftsJson([
    { conversationId: CONV_A, text: '第一筆 ' + XSS },
    { conversationId: CONV_A, text: '重複的' },
    { conversationId: DRAFT, text: '草稿的字', accountId: 'la_c1', deviceId: 'd_A' },
    { conversationId: 'not-an-id', text: 'x' },
    { conversationId: CONV_B, text: 7 },
    { conversationId: CONV_B, text: '帳戶不是字串', accountId: 5, deviceId: '' },
    'x',
    null,
  ]);
  assert.deepEqual(JSON.parse(json), [
    { conversationId: CONV_A, text: '第一筆 ' + XSS, accountId: null, deviceId: null },
    { conversationId: DRAFT, text: '草稿的字', accountId: 'la_c1', deviceId: 'd_A' },
    { conversationId: CONV_B, text: '帳戶不是字串', accountId: null, deviceId: null },
  ]);
  const many = Array.from({ length: 150 }, (_, i) => ({ conversationId: i.toString(16).padStart(32, '0'), text: 't' + i }));
  assert.equal(JSON.parse(L.reloadDraftsJson(many)).length, L.MAX_RELOAD_DRAFTS);
  assert.equal(L.MAX_RELOAD_DRAFTS, 100);
});

test('parseReloadDrafts_讀回合格的草稿_壞的一律略過', () => {
  for (const bad of [null, undefined, '', 'x', '{}', '"s"', '7', 'null']) assert.equal(L.parseReloadDrafts(bad).size, 0, String(bad));
  const round = L.parseReloadDrafts(L.reloadDraftsJson([
    { conversationId: CONV_A, text: '字' },
    { conversationId: DRAFT, text: '草稿', accountId: 'la_c1', deviceId: 'd_A' },
  ]));
  assert.deepEqual([...round.entries()], [
    [CONV_A, { text: '字', accountId: null, deviceId: null }],
    [DRAFT, { text: '草稿', accountId: 'la_c1', deviceId: 'd_A' }],
  ]);
  const raw = JSON.stringify([{ conversationId: CONV_A, text: '' }, { conversationId: CONV_A, text: '留下' }, { conversationId: CONV_A, text: '重複' }, { conversationId: '../x', text: 'x' }, 5]);
  assert.deepEqual([...L.parseReloadDrafts(raw).entries()], [[CONV_A, { text: '留下', accountId: null, deviceId: null }]]);
  const many = JSON.stringify(Array.from({ length: 150 }, (_, i) => ({ conversationId: i.toString(16).padStart(32, '0'), text: 't' + i })));
  assert.equal(L.parseReloadDrafts(many).size, L.MAX_RELOAD_DRAFTS);
});

// ───── 剪貼簿與相對網址 ─────

test('clipboardAccept_沒有等待或過期不收', () => {
  const pending = [{ conversationId: CONV_A, itemId: 'i9', at: NOW }];
  const msg = { type: 'clipboard', conversationId: CONV_A, itemId: 'i9', text: 'x' };
  assert.equal(L.clipboardAccept(pending, msg, NOW + 30000), true);
  assert.equal(L.clipboardAccept(pending, msg, NOW + 30001), false);
  assert.equal(L.clipboardAccept(pending, Object.assign({}, msg, { itemId: 'i8' }), NOW), false, '別的 itemId');
  assert.equal(L.clipboardAccept(pending, Object.assign({}, msg, { conversationId: CONV_B }), NOW), false, '別的對話');
  assert.equal(L.clipboardAccept([], msg, NOW), false, '沒有等待');
  assert.equal(L.clipboardAccept(null, msg, NOW), false);
  assert.equal(L.clipboardAccept(new Map([['k', pending[0]]]), msg, NOW), true, 'Map 的值也收');
});

test('safeRelativeUrl', () => {
  assert.equal(L.safeRelativeUrl('/f/k3'), '/f/k3');
  assert.equal(L.safeRelativeUrl('/m/a?s=160&v=1'), '/m/a?s=160&v=1');
  assert.equal(L.safeRelativeUrl('/t/a'), '/t/a');
  const bs = String.fromCharCode(92);
  const rejected = ['//evil', '/f/../x', '/f/%2e%2e/x', '/f/%2E%2E/x', '/f/./x', '/f' + bs + 'a', '/f/a' + bs + 'b', 'https://x/f/a', '/x/a',
    ' /f/a', '/f/a b', '/f/', '/f/%2fx', '/f/%5cx', '/f/%zz', 'f/a', '', null, 7];
  for (const url of rejected) assert.equal(L.safeRelativeUrl(url), null, JSON.stringify(url));
  for (let c = 0; c <= 0x1f; c++) assert.equal(L.safeRelativeUrl('/f/a' + String.fromCharCode(c)), null, '控制字元 ' + c);
  assert.equal(L.safeRelativeUrl('/f/a' + String.fromCharCode(0x7f)), null, 'DEL');
  assert.equal(L.safeRelativeUrl('/f/' + 'a'.repeat(2045)), '/f/' + 'a'.repeat(2045), '2048 字收');
  assert.equal(L.safeRelativeUrl('/f/' + 'a'.repeat(2046)), null, '超過 2048 字');
  assert.equal(L.safeRelativeUrl('/m/a', ['/f/']), null, '只收指定的前綴');
});

// ───── 遠端操作紀錄與回收區 ─────

test('auditView_executing是執行中或不明_issuer為null有替代_tier為null顯示破折號', () => {
  const v = L.auditView({ at: NOW, seq: 7, commandId: '01A', cmd: 'send', conversationId: CONV_A, issuer: null, tier: null, fullAccess: false,
    policy: 'Control', state: 'executing', code: null, defaultPassword: true, count: 2 });
  assert.equal(v.stateText, '執行中或不明');
  assert.equal(v.issuer, '來源名稱無法顯示');
  assert.equal(v.tierText, '—');
  assert.equal(v.cmdLabel, '送出訊息');
  assert.equal(v.policyText, '可以操作');
  assert.equal(v.seq, 7);
  assert.equal(v.defaultPassword, true);
  assert.equal(v.count, 2);
  const r = L.auditView({ at: NOW, seq: 1, cmd: XSS, issuer: XSS, tier: 2, state: 'rejected', code: 'policy' });
  assert.equal(r.cmdLabel, XSS, '不認得的指令照原字串（呼叫端 textContent）');
  assert.equal(r.issuer, XSS);
  assert.equal(r.tierText, '層級 2');
  assert.equal(r.stateText, '已拒絕');
  assert.equal(r.codeText, '政策不允許');
  assert.equal(L.auditView({ cmd: 'toString' }).cmdLabel, 'toString', '原型上的名稱不算認得');
  const proto = L.auditView({ policy: 'toString', state: 'constructor', code: 'hasOwnProperty' });
  assert.deepEqual([proto.policyText, proto.stateText, proto.codeText], ['—', '—', 'hasOwnProperty']);
  assert.equal(L.auditView(null).stateText, '—');
});

test('auditNextPage_取最舊一筆的at與seq', () => {
  const entries = [{ at: NOW, seq: 9 }, { at: NOW - 5, seq: 8 }, { at: NOW - 5, seq: 6 }];
  assert.deepEqual(L.auditNextPage(entries), { before: NOW - 5, beforeSeq: 6 });
  assert.deepEqual(L.auditNextPage([{ at: NOW - 5, seq: 6 }, { at: NOW - 5, seq: 8 }]), { before: NOW - 5, beforeSeq: 6 }, '不靠順序');
  assert.equal(L.auditNextPage([]), null);
  assert.equal(L.auditNextPage([{ at: NOW }]), null, '沒有 seq 的不算');
  assert.equal(L.auditNextPage(null), null);
});

test('trashView_標題替代與剩幾天', () => {
  const v = L.trashView({ conversationId: CONV_A, title: null, trashedAt: NOW - DAY, issuer: null, commandId: '01A', expiresAt: NOW + 29 * DAY + 1 }, NOW);
  assert.equal(v.title, '（沒有標題）');
  assert.equal(v.issuer, '來源名稱無法顯示');
  assert.equal(v.daysLeft, 30, '無條件進位');
  assert.equal(v.daysText, '還剩 30 天');
  assert.equal(L.trashView({ expiresAt: NOW - 1 }, NOW).daysText, '今天到期');
  assert.equal(L.trashView({ title: XSS }, NOW).title, XSS);
  assert.equal(L.trashView({}, NOW).daysLeft, null);
});

// ───── 對話的同步政策 ─────

test('policyChoices_照choices排序與標籤_不認得的值略過', () => {
  assert.deepEqual(L.policyChoices({ policy: 'metadata', choices: ['localOnly', 'full', 'paused', 'metadata', 'full'], serverDeleted: false }), [
    { value: 'localOnly', label: '只留在這台', current: false },
    { value: 'full', label: '完整同步', current: false },
    { value: 'metadata', label: '只同步標題與狀態', current: true },
  ]);
  assert.deepEqual(L.policyChoices({ policy: 'paused', choices: ['localOnly'] }), [{ value: 'localOnly', label: '只留在這台', current: false }]);
  assert.deepEqual(L.policyChoices(null), []);
  assert.deepEqual(L.policyChoices({ choices: 'full' }), []);
});

test('needsLocalOnlyConfirm_只有改成localOnly才要', () => {
  assert.equal(L.needsLocalOnlyConfirm('full', 'localOnly'), true);
  assert.equal(L.needsLocalOnlyConfirm('paused', 'localOnly'), true);
  assert.equal(L.needsLocalOnlyConfirm('localOnly', 'localOnly'), false);
  assert.equal(L.needsLocalOnlyConfirm('full', 'metadata'), false);
  assert.equal(L.needsLocalOnlyConfirm('localOnly', 'full'), false);
});

// ───── 選電腦與草稿換電腦 ─────

function pickFixture() {
  const cell = (deviceId, over = {}) => Object.assign({ deviceId, deviceName: deviceId, status: 'ready', identityOk: true, online: true }, over);
  const device = (id, over = {}) => Object.assign({ id, name: 'PC-' + id, online: true, remoteControlLevel: 'Control', allowRemoteFullAccess: false, revoked: null }, over);
  return { cell, device };
}

test('pickDevice_開著的那台優先_再來上次用的_只有一台直接用_多台要問_沒有候選回原因', () => {
  const { cell, device } = pickFixture();
  const accounts = [{ id: 'la_1', devices: [cell('d_A'), cell('d_B'), cell('d_C')] }];
  const devices = [device('d_A', { name: XSS }), device('d_B'), device('d_C')];
  const base = { accountId: 'la_1', accounts, devices };

  const ask = L.pickDevice(base);
  assert.equal(ask.ask, true);
  assert.equal(ask.deviceId, null);
  assert.deepEqual(ask.choices.map((c) => c.deviceId), ['d_A', 'd_B', 'd_C']);
  assert.equal(ask.choices[0].name, XSS, '名稱照原字串（呼叫端 textContent）');

  const focused = L.pickDevice(Object.assign({}, base, { focusedDeviceId: 'd_C', lastDeviceId: 'd_B' }));
  assert.deepEqual([focused.deviceId, focused.ask], ['d_C', false], '聚焦的那台優先');
  assert.deepEqual(focused.choices.map((c) => c.deviceId), ['d_C', 'd_B', 'd_A']);

  const last = L.pickDevice(Object.assign({}, base, { focusedDeviceId: 'd_Z', lastDeviceId: 'd_B' }));
  assert.deepEqual([last.deviceId, last.ask], ['d_B', false], '再來上次用的');

  const one = L.pickDevice({ accountId: 'la_1', accounts: [{ id: 'la_1', devices: [cell('d_B')] }], devices });
  assert.deepEqual([one.deviceId, one.ask, one.reason], ['d_B', false, null], '只有一台直接用');

  assert.deepEqual(L.pickDevice({ accountId: 'la_9', accounts, devices }), { deviceId: null, ask: false, choices: [], reason: '找不到這個帳戶' });
  assert.equal(L.pickDevice({ accountId: 'la_1', accounts: [{ id: 'la_1', devices: null }], devices }).reason, '沒有一台電腦登入了這個帳戶');
  assert.equal(L.pickDevice({ accountId: 'la_1', accounts: [{ id: 'la_1', devices: [cell('d_A', { online: false })] }], devices }).reason,
    '登入這個帳戶的電腦都不在線');
  assert.equal(L.pickDevice({ accountId: 'la_1', accounts: [{ id: 'la_1', devices: [cell('d_A')] }], devices: [device('d_A', { remoteControlLevel: 'View' })] }).reason,
    '登入這個帳戶的電腦都沒有允許遠端操作：在那台電腦的設定中允許遠端操作');
  assert.equal(L.pickDevice().reason, '找不到這個帳戶', '沒給參數不丟例外');
});

test('pickDevice_View與離線與身分不符都不是候選', () => {
  const { cell, device } = pickFixture();
  const accounts = [{ id: 'la_1', devices: [
    cell('d_view'), cell('d_off', { online: false }), cell('d_bad', { identityOk: false }), cell('d_absent', { status: 'absent' }),
    cell('d_revoked'), cell('d_devoff'), cell('d_missing'), cell('d_ok'),
  ] }];
  const devices = [device('d_view', { remoteControlLevel: 'View' }), device('d_off'), device('d_bad'), device('d_absent'),
    device('d_revoked', { revoked: { at: NOW, reason: 'retired' } }), device('d_devoff', { online: false }), device('d_ok')];
  const r = L.pickDevice({ accountId: 'la_1', accounts, devices });
  assert.deepEqual(r.choices.map((c) => c.deviceId), ['d_ok']);
  assert.deepEqual([r.deviceId, r.ask], ['d_ok', false]);
  assert.equal(L.pickDevice({ accountId: 'la_1', accounts, devices, focusedDeviceId: 'd_view' }).deviceId, 'd_ok', '聚焦的那台不是候選時不用它');
});

test('draftCarryOver_只重送非預設值_customFolder才setFolder_標題是新對話不rename', () => {
  const old = { model: 'claude-x', effort: 'high', permission: 'AcceptEdits', customFolder: true, workingDirectory: 'D:\\work', title: '改過的標題' };
  assert.deepEqual(L.draftCarryOver(old, null, DRAFT), [
    { conversationId: DRAFT, cmd: 'setOption', key: 'model', value: 'claude-x' },
    { conversationId: DRAFT, cmd: 'setOption', key: 'effort', value: 'high' },
    { conversationId: DRAFT, cmd: 'setOption', key: 'permission', value: 'AcceptEdits' },
    { conversationId: DRAFT, cmd: 'setFolder', path: 'D:\\work' },
    { conversationId: DRAFT, cmd: 'rename', title: '改過的標題' },
  ]);
  assert.deepEqual(L.draftCarryOver({ model: 'default', effort: 'default', permission: 'Ask', customFolder: false, workingDirectory: 'C:\\ws', title: '新對話' }, null), []);
  assert.deepEqual(L.draftCarryOver({ model: 'claude-x', permission: 'Plan', title: '新對話' }, { model: 'claude-x', permission: 'Plan' }), [],
    '和新草稿相同的不重送');
  assert.deepEqual(L.draftCarryOver({ permission: 'Ask' }, { permission: 'Plan' }), [{ cmd: 'setOption', key: 'permission', value: 'Ask' }], '沒給 id 不帶');
  assert.deepEqual(L.draftCarryOver(null, null), []);
});

// ───── 刪除、歷史篩選 ─────

test('deleteProgramsCheckbox_只有桌面這台的對話有另開的程式時才問_標頭與清單都認', () => {
  const one = L.deleteProgramsCheckbox('desktop', 'local', { programCount: 1 });
  assert.deepEqual(one, { label: '一併停止這個對話另開、還在跑的程式', note: '不停止的話它們會繼續跑，刪除後在 Myriagent 裡就看不到了', checked: true });
  assert.equal(L.deleteProgramsCheckbox('desktop', 'local', { programs: [{ id: 'a' }, { id: 'b' }, { id: 'c' }] }).label, '一併停止這個對話另開、還在跑的 3 個程式', '標頭的 programs 是陣列');
  assert.equal(L.deleteProgramsCheckbox('desktop', 'local', { programCount: 0 }), null);
  assert.equal(L.deleteProgramsCheckbox('desktop', 'local', { programs: null }), null);
  assert.equal(L.deleteProgramsCheckbox('desktop', 'local', {}), null, '舊版的宿主沒有這個欄位');
  assert.equal(L.deleteProgramsCheckbox('desktop', 'replica', { programCount: 2 }), null, '副本的程式在別台電腦');
  assert.equal(L.deleteProgramsCheckbox('hub', 'remote', { programCount: 2 }), null, '瀏覽器版不問');
  assert.equal(L.deleteProgramsCheckbox('desktop', 'local', null), null);
});

test('deleteConfirmText_hub寫回收區與30天_桌面本機照舊', () => {
  const hub = L.deleteConfirmText('hub', 'remote', 'OFFICE-PC', { title: '修正登入', customFolder: false, running: true });
  assert.equal(hub.message, '「修正登入」會移到 OFFICE-PC 的回收區，30 天內可在那台電腦的設定還原。執行中的這一輪會被停止。');
  assert.equal(hub.message.includes('無法復原'), false);
  assert.equal(hub.detail, null);
  assert.equal(L.deleteConfirmText('hub', 'remote', null, {}).message, '「未命名對話」會移到未知電腦的回收區，30 天內可在那台電腦的設定還原。');
  const local = L.deleteConfirmText('desktop', 'local', null, { title: 't', customFolder: false });
  assert.equal(local.message, '「t」的對話紀錄、附件，以及預設工作區裡的所有檔案都會一起刪除，無法復原。');
  assert.equal(local.detail, '這個對話沒有選工作資料夾，代理寫出的程式、文件都放在預設工作區；要保留請先複製到別的地方。');
  assert.deepEqual(L.deleteConfirmText('desktop', 'local', null, { title: 't', customFolder: true }), { message: '「t」的對話紀錄與附件會一起刪除，無法復原。', detail: null });
  assert.equal(L.deleteConfirmText('desktop', 'replica', 'OFFICE-PC', {}), null, '副本沒有刪除');
  assert.equal(L.deleteConfirmText('hub', 'draft', null, {}), null);
});

// ───── 桌面設定的同步區與副本（步 10） ─────

test('backfillText_無條件進位到MB_千分位_不到1MB_沒有在回補是null', () => {
  const MB = 1024 * 1024;
  assert.equal(L.backfillText({ conversationsLeft: 12, bytesLeft: 50 * MB }), '還有 12 個對話（約 50 MB）');
  assert.equal(L.backfillText({ conversationsLeft: 1, bytesLeft: 300 * 1024 }), '還有 1 個對話（不到 1 MB）');
  assert.equal(L.backfillText({ conversationsLeft: 3, bytesLeft: 0 }), '還有 3 個對話（不到 1 MB）', '0 位元組也是不到 1 MB');
  assert.equal(L.backfillText({ conversationsLeft: 2, bytesLeft: MB }), '還有 2 個對話（約 1 MB）', '剛好 1 MB');
  assert.equal(L.backfillText({ conversationsLeft: 2, bytesLeft: MB + 1 }), '還有 2 個對話（約 2 MB）', '多 1 位元組就進位');
  assert.equal(L.backfillText({ conversationsLeft: 900, bytesLeft: 12345 * MB - 5 }), '還有 900 個對話（約 12,345 MB）', '千分位');
  assert.equal(L.backfillText({ conversationsLeft: 1, bytesLeft: 1234567 * MB }), '還有 1 個對話（約 1,234,567 MB）');
  for (const bad of [null, undefined, {}, { conversationsLeft: 1 }, { conversationsLeft: -1, bytesLeft: 0 }, { conversationsLeft: 1, bytesLeft: -1 }, { conversationsLeft: '1', bytesLeft: 0 }]) {
    assert.equal(L.backfillText(bad), null, JSON.stringify(bad));
  }
});

test('replicaWebUrl_伺服器的app彈出視窗_id編碼_不是https或帶路徑是null', () => {
  assert.equal(L.replicaWebUrl('https://hub.example.com', CONV_A), 'https://hub.example.com/app/?mode=popout&conversation=' + CONV_A);
  assert.equal(L.replicaWebUrl('https://127.0.0.1:5443/', CONV_A), 'https://127.0.0.1:5443/app/?mode=popout&conversation=' + CONV_A, '結尾斜線、埠');
  assert.equal(L.replicaWebUrl('https://hub.example.com', 'a b/c&d'), 'https://hub.example.com/app/?mode=popout&conversation=a%20b%2Fc%26d', 'id 編碼');
  for (const bad of ['http://hub.example.com', 'https://hub.example.com/x', 'https://u@hub.example.com', 'https://hub.example.com?x=1', 'javascript:alert(1)', '', null, 7]) {
    assert.equal(L.replicaWebUrl(bad, CONV_A), null, String(bad));
  }
  assert.equal(L.replicaWebUrl('https://hub.example.com', null), null);
  assert.equal(L.replicaWebUrl('https://hub.example.com', ''), null);
});

test('filterHistory_local只留沒有deviceId的_瀏覽器不篩', () => {
  const list = [{ id: 'c1', deviceId: null }, { id: 'c2', deviceId: 'd_A' }, { id: 'c3' }];
  assert.deepEqual(L.filterHistory(list, 'local', 'desktop').map((c) => c.id), ['c1', 'c3']);
  assert.equal(L.filterHistory(list, 'all', 'desktop'), list);
  assert.equal(L.filterHistory(list, undefined, 'desktop'), list, '省略＝all');
  assert.equal(L.filterHistory(list, 'local', 'hub'), list);
  assert.deepEqual(L.filterHistory(null, 'local', 'desktop'), []);
});

test('filterHistory_某一台只留那台的_兩種模式都篩', () => {
  const list = [{ id: 'c1', deviceId: null }, { id: 'c2', deviceId: 'd_A' }, { id: 'c3' }, { id: 'c4', deviceId: 'd_B' }, null];
  assert.deepEqual(L.filterHistory(list, 'd_A', 'desktop').map((c) => c.id), ['c2']);
  assert.deepEqual(L.filterHistory(list, 'd_B', 'hub').map((c) => c.id), ['c4']);
  assert.deepEqual(L.filterHistory(list, 'd_沒有', 'hub'), []);
  assert.equal(L.filterHistory(list, null, 'hub'), list);
  assert.equal(L.filterHistory(list, '', 'desktop'), list, '空字串＝全部');
});

test('deviceFilterChoices_桌面有副本才有選項_這台在最前_其他照名冊順序', () => {
  const remote = { devices: [{ id: 'd_B', name: 'HOME', online: false }, { id: 'd_A', name: 'OFFICE', online: true }] };
  const list = [{ id: 'c1' }, { id: 'c2', deviceId: 'd_A' }, { id: 'c3', deviceId: 'd_B' }, { id: 'c4', deviceId: 'd_A' }, { id: 'c5', deviceId: null },
    { id: 'c6', deviceId: 'd_Z', deviceName: 'ZETA', deviceOnline: true }, { id: 'c7', deviceId: 'd_Y', deviceName: 'ALPHA' }];
  const r = L.deviceFilterChoices({ list, mode: 'desktop', remote, selected: 'd_B' });
  assert.deepEqual(r.choices.map((c) => [c.value, c.name, c.online, c.count]),
    [['local', '這台電腦', null, 2], ['d_B', 'HOME', false, 1], ['d_A', 'OFFICE', true, 2], ['d_Y', 'ALPHA', null, 1], ['d_Z', 'ZETA', true, 1]],
    '名冊裡的照名冊、沒有的照名稱排在後面；名稱與在線退回對話的 deviceName、deviceOnline');
  assert.equal(r.value, 'd_B');
  // 數字照 scoped（套過帳戶篩選），選項照完整清單
  const scoped = [{ id: 'c2', deviceId: 'd_A' }];
  assert.deepEqual(L.deviceFilterChoices({ list, scoped, mode: 'desktop', remote }).choices.map((c) => c.count), [0, 0, 1, 0, 0]);
  assert.equal(L.deviceFilterChoices({ list, mode: 'desktop', remote, selected: 'local' }).value, 'local');
  assert.equal(L.deviceFilterChoices({ list, mode: 'desktop', remote, selected: 'd_沒有對話' }).value, null, '不在選項裡＝全部');
  // 沒有副本：不顯示（也不篩）
  const none = L.deviceFilterChoices({ list: [{ id: 'c1' }, { id: 'c5', deviceId: null }], mode: 'desktop', remote, selected: 'local' });
  assert.deepEqual(none, { choices: [], value: null });
  assert.deepEqual(L.deviceFilterChoices(), { choices: [], value: null });
});

test('deviceFilterChoices_瀏覽器沒有這台_兩台以上才有選項', () => {
  const remote = { devices: [{ id: 'd_A', name: 'OFFICE', online: true }, { id: 'd_B', name: 'HOME', online: false, reconnecting: true }] };
  const one = [{ id: 'c1', deviceId: 'd_A' }, { id: 'c2', deviceId: 'd_A' }];
  assert.deepEqual(L.deviceFilterChoices({ list: one, mode: 'hub', remote, selected: 'd_A' }), { choices: [], value: null });
  const two = one.concat([{ id: 'c3', deviceId: 'd_B' }]);
  const r = L.deviceFilterChoices({ list: two, mode: 'hub', remote, selected: 'local' });
  assert.deepEqual(r.choices.map((c) => [c.value, c.name, c.count, c.reconnecting]), [['d_A', 'OFFICE', 2, false], ['d_B', 'HOME', 1, true]]);
  assert.equal(r.value, null, '瀏覽器沒有 local');
});

test('layoutDeviceFilter_與layoutDeviceFields_來回一致_舊版版面照讀', () => {
  for (const v of [null, 'local', 'd_A']) assert.equal(L.layoutDeviceFilter(L.layoutDeviceFields(v)), v);
  assert.deepEqual(L.layoutDeviceFields('d_A'), { historyScope: 'all', historyDevice: 'd_A' }, '舊版只看 historyScope：選某一台時是全部');
  assert.deepEqual(L.layoutDeviceFields('all'), { historyScope: 'all' });
  assert.equal(L.layoutDeviceFilter({ historyScope: 'local' }), 'local');
  assert.equal(L.layoutDeviceFilter({ historyScope: 'all' }), null);
  assert.equal(L.layoutDeviceFilter({ historyScope: 'local', historyDevice: 'd_A' }), 'd_A');
  assert.equal(L.layoutDeviceFilter({ historyDevice: 42 }), null);
  assert.equal(L.layoutDeviceFilter(null), null);
});

test('folderKeyOf_預設工作區合成一項_自選資料夾不分大小寫與斜線_副本看不出來', () => {
  assert.equal(L.folderKeyOf({ customFolder: false, workingDirectory: 'C:\\Users\\Lin\\AppData\\Local\\Myriagent\\conversations\\c1\\workspace' }, 'desktop'), 'default');
  assert.equal(L.folderKeyOf({ customFolder: false, workingDirectory: null }, 'hub'), 'default', '瀏覽器的預設工作區沒有路徑');
  assert.equal(L.folderKeyOf({ customFolder: true, workingDirectory: 'C:\\repo\\Api\\' }, 'desktop'), 'dir:c:\\repo\\api');
  assert.equal(L.folderKeyOf({ customFolder: true, workingDirectory: 'c:/repo/api' }, 'desktop'), L.folderKeyOf({ customFolder: true, workingDirectory: 'C:\\REPO\\API' }, 'desktop'));
  assert.equal(L.folderKeyOf({ customFolder: true, workingDirectory: 'C:\\repo\\api' }, 'hub'), 'dir:c:\\repo\\api', '瀏覽器每一列都有 deviceId 也照分');
  assert.equal(L.folderKeyOf({ deviceId: 'd_A', customFolder: false, workingDirectory: null }, 'desktop'), null, '桌面的副本：清單不給那台的資料夾');
  assert.equal(L.folderKeyOf({ customFolder: true, workingDirectory: null }, 'desktop'), null);
  assert.equal(L.folderKeyOf({ customFolder: true, workingDirectory: '' }, 'desktop'), null);
  assert.equal(L.folderKeyOf(null, 'desktop'), null);
});

test('folderFilterChoices_照最近的對話排_預設工作區在最後_兩個以上才有選項', () => {
  const list = [
    { id: 'c1', updatedAt: 500, customFolder: true, workingDirectory: 'C:\\repo\\api' },
    { id: 'c2', updatedAt: 900, customFolder: true, workingDirectory: 'D:\\work\\web\\' },
    { id: 'c3', updatedAt: 950, customFolder: false, workingDirectory: 'C:\\ws\\c3\\workspace' },
    { id: 'c4', updatedAt: 990, customFolder: true, workingDirectory: 'c:/repo/API' },
    { id: 'c5', updatedAt: 990, customFolder: true, workingDirectory: 'E:\\' },
    { id: 'c6', updatedAt: 999, deviceId: 'd_A', customFolder: false, workingDirectory: null },
    null,
  ];
  const r = L.folderFilterChoices({ list, mode: 'desktop', selected: 'dir:d:\\work\\web' });
  assert.deepEqual(r.choices.map((c) => [c.value, c.name, c.path]), [
    ['dir:c:\\repo\\api', 'API', 'c:/repo/API'],
    ['dir:e:', 'E:', 'E:\\'],
    ['dir:d:\\work\\web', 'web', 'D:\\work\\web\\'],
    ['default', '預設工作區', null],
  ], '同一個資料夾合成一項、名稱與路徑用最近的那個對話寫的；時間相同照清單順序；副本不列');
  assert.equal(r.value, 'dir:d:\\work\\web');
  assert.equal(L.folderFilterChoices({ list, mode: 'desktop', selected: 'default' }).value, 'default');
  assert.equal(L.folderFilterChoices({ list, mode: 'desktop', selected: 'dir:c:\\沒有' }).value, null, '不在選項裡＝全部');
  assert.equal(L.folderFilterChoices({ list, mode: 'desktop' }).value, null);
  // 只用到一個工作區：不顯示（也不篩）
  const one = [{ id: 'c1', customFolder: true, workingDirectory: 'C:\\repo\\api' }, { id: 'c2', customFolder: true, workingDirectory: 'C:\\Repo\\Api' },
    { id: 'c3', deviceId: 'd_A', customFolder: false }];
  assert.deepEqual(L.folderFilterChoices({ list: one, mode: 'desktop', selected: 'dir:c:\\repo\\api' }), { choices: [], value: null });
  assert.deepEqual(L.folderFilterChoices({ list: [{ customFolder: false }, { customFolder: false }], mode: 'hub', selected: 'default' }), { choices: [], value: null });
  assert.deepEqual(L.folderFilterChoices(), { choices: [], value: null });
});

test('filterFolder與folderCounts_照folderKeyOf_副本只在全部', () => {
  const list = [
    { id: 'c1', customFolder: true, workingDirectory: 'C:\\repo\\api' },
    { id: 'c2', customFolder: false, workingDirectory: 'C:\\ws\\c2' },
    { id: 'c3', customFolder: true, workingDirectory: 'C:/Repo/Api/' },
    { id: 'c4', deviceId: 'd_A', customFolder: false, workingDirectory: null },
  ];
  assert.deepEqual(L.filterFolder(list, 'dir:c:\\repo\\api', 'desktop').map((c) => c.id), ['c1', 'c3']);
  assert.deepEqual(L.filterFolder(list, 'default', 'desktop').map((c) => c.id), ['c2']);
  assert.deepEqual(L.filterFolder(list, 'default', 'hub').map((c) => c.id), ['c2', 'c4'], '瀏覽器的 customFolder 是那台給的');
  assert.equal(L.filterFolder(list, null, 'desktop'), list);
  assert.equal(L.filterFolder(list, '', 'desktop'), list, '空字串＝全部');
  assert.deepEqual(L.filterFolder(null, 'default', 'desktop'), []);
  assert.deepEqual([...L.folderCounts(list, 'desktop')], [['dir:c:\\repo\\api', 2], ['default', 1]]);
  assert.deepEqual([...L.folderCounts(list, 'hub')], [['dir:c:\\repo\\api', 2], ['default', 2]]);
  assert.equal(L.folderCounts(null, 'desktop').size, 0);
});

// ───── 上傳、晶片、檔案預覽 ─────

test('uploadCheck_512MB邊界', () => {
  assert.deepEqual(L.uploadCheck({ size: 536870912 }), { ok: true, reason: null });
  assert.deepEqual(L.uploadCheck({ size: 536870913 }), { ok: false, reason: '檔案超過 512 MB，不能從網頁上傳' });
  assert.equal(L.uploadCheck({ size: 0 }).ok, true);
  assert.equal(L.uploadCheck({ size: -1 }).ok, false);
  assert.equal(L.uploadCheck({}).reason, '讀不到檔案大小');
  assert.equal(L.uploadCheck(null).ok, false);
});

test('uploadErrorText_409_413_507_0與其他', () => {
  assert.equal(L.uploadErrorText(409), '這個檔案的上傳內容和先前不同，請重新附加');
  assert.equal(L.uploadErrorText(413), '檔案超過 512 MB，不能從網頁上傳');
  assert.equal(L.uploadErrorText(507), '伺服器的空間不足或待處理的上傳太多，請稍後再試');
  assert.equal(L.uploadErrorText(0), '網路中斷，上傳沒有完成');
  assert.equal(L.uploadErrorText(500), '上傳失敗（HTTP 500）');
  assert.equal(L.uploadErrorText(undefined), '上傳失敗（HTTP ?）');
});

test('chipsAfter_rejected保留_done與closed清空', () => {
  const chips = [{ uploadId: 'u1', name: 'a.png', size: 3 }];
  assert.equal(L.chipsAfter(chips, 'sendRejected'), chips);
  assert.deepEqual(L.chipsAfter(chips, 'sendDone'), []);
  assert.deepEqual(L.chipsAfter(chips, 'closed'), []);
  assert.equal(L.chipsAfter(chips, 'other'), chips);
  assert.deepEqual(L.chipsAfter(null, 'sendRejected'), []);
});

test('fileRefView_有error一律固定文字_url不是相對網址當錯誤', () => {
  const ok = L.fileRefView({ type: 'fileRef', requestId: 'r14', conversationId: CONV_A, url: '/f/k3', name: XSS, size: 18233, expiresAt: NOW, error: null });
  assert.deepEqual(ok, { requestId: 'r14', conversationId: CONV_A, ok: true, error: null, url: '/f/k3', name: XSS, size: 18233, sizeText: '17.8 KB', expiresAt: NOW });
  const fixed = { requestId: 'r14', conversationId: CONV_A, ok: false, error: '這個檔案不能從網頁讀取' };
  assert.deepEqual(L.fileRefView({ requestId: 'r14', conversationId: CONV_A, error: '<b>伺服器給的任意文字</b>' }), fixed);
  assert.deepEqual(L.fileRefView({ requestId: 'r14', conversationId: CONV_A, url: 'https://evil/f/k3', error: null }), fixed);
  assert.deepEqual(L.fileRefView({ requestId: 'r14', conversationId: CONV_A, url: '/m/k3', error: null }), fixed, '只收 /f/');
  assert.deepEqual(L.fileRefView({ requestId: 'r14', conversationId: CONV_A, url: '//evil/x' }), fixed);
  assert.equal(L.fileRefView(null).error, '這個檔案不能從網頁讀取');
  assert.equal(L.fileRefView({ url: '/f/a', size: 3 * 1024 * 1024 }).sizeText, '3 MB');
  assert.equal(L.fileRefView({ url: '/f/a', size: 12 }).sizeText, '12 B');
});

test('uploadBatch_太大的不收_超過每則20個的不收', () => {
  const f = (name, size) => ({ name, size });
  const r = L.uploadBatch([f('a', 1), f('big', 536870913), f('b', 2)], 0);
  assert.deepEqual(r.accept.map((x) => x.name), ['a', 'b']);
  assert.deepEqual(r.tooLarge.map((x) => [x.file.name, x.reason]), [['big', '檔案超過 512 MB，不能從網頁上傳']]);
  assert.equal(r.overLimit, 0);
  const many = Array.from({ length: 5 }, (_, i) => f('m' + i, 1));
  const r2 = L.uploadBatch(many, 17);
  assert.deepEqual(r2.accept.map((x) => x.name), ['m0', 'm1', 'm2'], '已有 17 個：只收 3 個');
  assert.equal(r2.overLimit, 2);
  assert.equal(L.uploadBatch(many, 20).accept.length, 0);
  assert.equal(L.uploadBatch(many, 25).overLimit, 5, '已有的超過上限也不出錯');
  assert.equal(L.uploadBatch([f('x', 1)], -3).accept.length, 1, '已有的數量不合格當 0');
  assert.deepEqual(L.uploadBatch(null, 0), { accept: [], tooLarge: [], overLimit: 0 });
  assert.equal(L.UPLOAD_LIMIT_TEXT, '一則訊息最多附加 20 個檔案');
});

test('uploadName_空的用預設_太長保留副檔名_不切開代理字元對', () => {
  assert.equal(L.uploadName('  報表.xlsx  '), '報表.xlsx');
  assert.equal(L.uploadName(''), '貼上的檔案');
  assert.equal(L.uploadName('   ', '貼上的圖片.png'), '貼上的圖片.png');
  assert.equal(L.uploadName(null), '貼上的檔案');
  const long = 'a'.repeat(300) + '.png';
  assert.equal(L.uploadName(long), 'a'.repeat(256) + '.png');
  assert.equal(L.uploadName(long).length, 260);
  assert.equal(L.uploadName('a'.repeat(260)), 'a'.repeat(260), '剛好 260 字不截');
  // 截斷點落在代理字元對的中間：去掉前半個
  const emoji = String.fromCodePoint(0x1f600);
  const name = 'b'.repeat(255) + emoji + 'cccc' + '.txt';
  const out = L.uploadName(name);
  assert.ok(out.length <= 260);
  assert.equal(out, 'b'.repeat(255) + '.txt');
  assert.ok(!/[\ud800-\udbff]$/.test(out.slice(0, -4)), '沒有落單的前半個代理字元');
  // 副檔名太長（超過 16 字）不當成副檔名
  const weird = 'x'.repeat(250) + '.' + 'y'.repeat(30);
  assert.equal(L.uploadName(weird), weird.slice(0, 260));
});

test('uploadResult_201與200用伺服器回的雜湊_其他回錯誤文字', () => {
  const sha = 'ab'.repeat(32);
  const id = '01J9QUPLOAD000000000000000';
  assert.deepEqual(L.uploadResult(201, { uploadId: id, sha256: sha, size: 10 }, id, 'a.png'), { ok: true, entry: { uploadId: id, name: 'a.png', size: 10, sha256: sha } });
  assert.deepEqual(L.uploadResult(200, { uploadId: id, sha256: sha, size: 0 }, id, 'a.png').entry.size, 0);
  assert.deepEqual(L.uploadResult(409, { error: 'conflict' }, id, 'a.png'), { ok: false, error: '這個檔案的上傳內容和先前不同，請重新附加' });
  assert.equal(L.uploadResult(413, null, id, 'a').error, '檔案超過 512 MB，不能從網頁上傳');
  assert.equal(L.uploadResult(507, null, id, 'a').error, '伺服器的空間不足或待處理的上傳太多，請稍後再試');
  assert.equal(L.uploadResult(0, null, id, 'a').error, '網路中斷，上傳沒有完成');
  assert.equal(L.uploadResult(503, null, id, 'a').error, '上傳失敗（HTTP 503）');
  const bad = '伺服器的回應不正確，上傳沒有完成';
  assert.equal(L.uploadResult(201, { uploadId: 'other', sha256: sha, size: 10 }, id, 'a').error, bad, 'id 不是這次的');
  assert.equal(L.uploadResult(201, { uploadId: id, sha256: sha.toUpperCase(), size: 10 }, id, 'a').error, bad, '大寫十六進位');
  assert.equal(L.uploadResult(201, { uploadId: id, sha256: sha.slice(2), size: 10 }, id, 'a').error, bad, '長度不對');
  assert.equal(L.uploadResult(201, { uploadId: id, sha256: sha, size: 1.5 }, id, 'a').error, bad, 'size 不是整數');
  assert.equal(L.uploadResult(201, { uploadId: id, sha256: sha, size: '10' }, id, 'a').error, bad);
  assert.equal(L.uploadResult(201, null, id, 'a').error, bad);
  assert.equal(L.uploadResult(201, { uploadId: id, sha256: sha, size: 1 }, id, '').error, bad, '檔名空的');
});

test('storedUploads_只收形狀對的_去重_最多20筆_壞字串回空', () => {
  const sha = 'cd'.repeat(32);
  const e = (id, o = {}) => Object.assign({ uploadId: id, name: id + '.png', size: 3, sha256: sha }, o);
  assert.deepEqual(L.storedUploads(JSON.stringify([e('u1'), e('u2')])), [e('u1'), e('u2')]);
  assert.deepEqual(L.storedUploads(JSON.stringify([e('u1'), e('u1', { name: 'dup' }), e('bad id'), e('u3', { sha256: 'x' }), e('u4', { size: -1 }), 'str', null])), [e('u1')]);
  assert.deepEqual(L.storedUploads(JSON.stringify([Object.assign(e('u5'), { extra: 1 })])), [e('u5')], '多的鍵不帶回來');
  assert.equal(L.storedUploads(JSON.stringify(Array.from({ length: 25 }, (_, i) => e('u' + i)))).length, 20);
  for (const raw of [null, undefined, '', '{', '{}', '"x"', '1', 'null']) assert.deepEqual(L.storedUploads(raw), [], String(raw));
  assert.equal(L.uploadsKey(CONV_A), 'myriagent.app.uploaded.v1:' + CONV_A);
});

test('attachGate_瀏覽器草稿停用並說明_副本唯讀_離線與政策照擋', () => {
  const hub = (over) => L.attachGate(Object.assign({ mode: 'hub', origin: 'remote', device: dev(), live: true }, over));
  assert.deepEqual(hub({}), { enabled: true, reason: null });
  assert.deepEqual(hub({ origin: 'draft' }), { enabled: false, reason: '送出第一則訊息後才能附加檔案' });
  assert.deepEqual(hub({ live: false }), { enabled: false, reason: '目前沒有連線，連上之後才能附加檔案' });
  assert.deepEqual(hub({ device: dev({ online: false, name: XSS }) }), { enabled: false, reason: '電腦離線：' + XSS + ' 重新上線後才能附加檔案' });
  assert.deepEqual(hub({ device: dev({ level: 'View', name: 'HOME-PC' }) }), { enabled: false, reason: '在 HOME-PC 的設定中允許遠端操作' });
  assert.deepEqual(hub({ device: dev({ level: 'Off' }) }), { enabled: false, reason: '在 OFFICE-PC 的設定中允許遠端操作' });
  assert.equal(hub({ device: dev({ allowFull: false }) }).enabled, true, '完全自動沒允許不擋附加（擋的是送出）');
  assert.equal(hub({ device: dev({ online: null, level: null }) }).enabled, true, '電腦不明不擋');
  assert.deepEqual(L.attachGate({ mode: 'desktop', origin: 'local' }), { enabled: true, reason: null });
  assert.deepEqual(L.attachGate({ mode: 'desktop', origin: 'draft' }), { enabled: true, reason: null }, '桌面草稿照常可附加');
  assert.equal(L.attachGate({ mode: 'desktop', origin: 'replica', device: dev() }).enabled, false);
  assert.equal(L.attachGate().enabled, true);
});

test('folderPickerGate_瀏覽器的對話與新對話_執行中與非Control停用', () => {
  const g = (over) => L.folderPickerGate(Object.assign({ mode: 'hub', origin: 'remote', header: { running: false }, device: dev(), live: true }, over));
  assert.deepEqual(g({}), { shown: true, enabled: true, reason: null });
  assert.deepEqual(g({ origin: 'draft' }), { shown: true, enabled: true, reason: null }, '新對話選完電腦就能瀏覽那台的資料夾（伺服器轉成 listFolders）');
  assert.deepEqual(g({ origin: 'draft', device: null }), { shown: true, enabled: true, reason: null }, '沒選電腦：伺服器照送出時的挑法');
  assert.deepEqual(g({ origin: 'draft', device: dev({ online: false }) }), { shown: true, enabled: false, reason: '電腦離線：OFFICE-PC 重新上線後才能瀏覽資料夾' });
  assert.deepEqual(g({ origin: 'draft', device: dev({ level: 'View', name: 'HOME-PC' }) }), { shown: true, enabled: false, reason: '在 HOME-PC 的設定中允許遠端操作' });
  assert.deepEqual(g({ mode: 'desktop', origin: 'draft' }), { shown: false, enabled: false, reason: null }, '桌面的草稿用資料夾對話框');
  assert.deepEqual(g({ mode: 'desktop', origin: 'local' }), { shown: false, enabled: false, reason: null });
  assert.deepEqual(g({ header: { running: true } }), { shown: true, enabled: false, reason: '執行中不能更換' });
  assert.deepEqual(g({ device: dev({ level: 'View', name: 'HOME-PC' }) }), { shown: true, enabled: false, reason: '在 HOME-PC 的設定中允許遠端操作' });
  assert.deepEqual(g({ live: false }), { shown: true, enabled: false, reason: '目前沒有連線，連上之後才能瀏覽資料夾' });
  assert.deepEqual(g({ device: dev({ online: false }) }), { shown: true, enabled: false, reason: '電腦離線：OFFICE-PC 重新上線後才能瀏覽資料夾' });
});

test('folderPickerGate_桌面可以操作的副本_執行中看擁有者那邊的', () => {
  const g = (header, peer = 'live') => L.folderPickerGate({ mode: 'desktop', origin: 'replica', header, device: dev(), peer });
  assert.deepEqual(g({ running: false, replica: { running: false } }), { shown: true, enabled: true, reason: null });
  assert.deepEqual(g({ running: false, replica: { running: true } }), { shown: true, enabled: false, reason: '執行中不能更換' },
    '副本的 header.running 是這台的（一律 false），那台執行中時那台會拒絕');
  assert.deepEqual(g({ running: false, replica: { running: false } }, 'down'), { shown: false, enabled: false, reason: null }, '不能操作的副本沒有這一項');
});

test('listFailText_新對話被拒成invalid是那台版本太舊', () => {
  assert.equal(L.listFailText('invalid', true), L.FOLDER_LIST_OLD_TEXT);
  assert.equal(L.listFailText('policy', true), '讀不到這個資料夾');
  assert.equal(L.listFailText('invalid', false), '讀不到這個資料夾', '已開始的對話照舊');
  assert.equal(L.listFailText(null, undefined), '讀不到這個資料夾');
});

test('ownerRunning_副本看replica_running_其他看running', () => {
  assert.equal(L.ownerRunning({ running: false, replica: { running: true } }, 'replica'), true);
  assert.equal(L.ownerRunning({ running: true, replica: { running: false } }, 'replica'), false, '副本的 header.running 不是那台的');
  assert.equal(L.ownerRunning({ running: false }, 'replica'), false, '沒有 replica 時不明，不擋（那台會檢查）');
  assert.equal(L.ownerRunning({ running: true, replica: { running: false } }, 'local'), true);
  assert.equal(L.ownerRunning({ running: true }, 'remote'), true);
  assert.equal(L.ownerRunning({ running: false }, 'draft'), false);
  assert.equal(L.ownerRunning(null, 'replica'), false);
});

test('mediaPolicyNote_政策Off才說明', () => {
  assert.equal(L.mediaPolicyNote(dev({ level: 'Off' })), '這台電腦的遠端政策是關閉，只能看縮圖');
  for (const level of ['Control', 'View', null]) assert.equal(L.mediaPolicyNote(dev({ level })), null, String(level));
  assert.equal(L.mediaPolicyNote(null), null);
});

test('composerGate_上傳中擋送出', () => {
  assert.deepEqual(L.composerGate({ mode: 'hub', origin: 'remote', header: {}, device: dev(), live: true, uploading: true }), { canSend: false, readOnly: false, reason: L.UPLOADING_TEXT });
  assert.equal(L.UPLOADING_TEXT, '附件上傳中，完成後才能送出');
  assert.equal(L.composerGate({ mode: 'hub', origin: 'remote', header: {}, device: dev(), live: false, uploading: true }).reason, '目前沒有連線，連上之後才能送出', '沒連線先說');
});

// ───── /clear ─────

test('clearBoundary_最後一則清除提示的索引_沒有回負一', () => {
  const user = (t) => ({ id: t, kind: 'User', html: t });
  const clear = (id, noticeKind = 'clear') => ({ id, kind: 'Notice', noticeKind });
  assert.equal(L.clearBoundary([user('a'), clear('c1'), user('b'), clear('c2'), user('c')]), 3, '最後一次清除');
  assert.equal(L.clearBoundary([clear('c1')]), 0);
  assert.equal(L.clearBoundary([user('a'), clear('c1', 'Clear')]), 1, 'C# 列舉直接轉字串是大駝峰：也認');
  assert.equal(L.clearBoundary([user('a'), clear('c1', 'cliUpdate'), clear('c2', 'appUpdate'), { id: 'e', kind: 'Error', noticeKind: 'clear' }]), -1, '別種提示、Error 不是分界');
  assert.equal(L.clearBoundary([user('a'), { id: 'n', kind: 'Notice' }, { id: 'n2', kind: 'Notice', noticeKind: 5 }]), -1);
  assert.equal(L.clearBoundary([null, undefined, clear('c1'), null]), 2, '缺項目（還沒收到）略過');
  for (const bad of [null, undefined, 'clear', {}, 0]) assert.equal(L.clearBoundary(bad), -1, String(bad));
  assert.equal(L.clearBoundary([]), -1);
});

// ───── 訊息時間與對話上方常駐的「最近的指令」 ─────

test('itemTimeText_今天只有時分_昨天_今年加月日_更早加年', () => {
  // 以本機時間建構（測試不受時區影響）
  const now = new Date(2026, 9, 22, 9, 30).getTime();
  assert.equal(L.itemTimeText(new Date(2026, 9, 22, 0, 0).getTime(), now), '00:00', '今天');
  assert.equal(L.itemTimeText(new Date(2026, 9, 22, 14, 5).getTime(), now), '14:05', '今天稍晚（時鐘調過）也只有時分');
  assert.equal(L.itemTimeText(new Date(2026, 9, 21, 23, 59).getTime(), now), '昨天 23:59');
  assert.equal(L.itemTimeText(new Date(2026, 9, 20, 8, 7).getTime(), now), '10/20 08:07');
  assert.equal(L.itemTimeText(new Date(2026, 0, 3, 8, 7).getTime(), now), '1/03 08:07');
  assert.equal(L.itemTimeText(new Date(2025, 11, 31, 22, 0).getTime(), now), '2025/12/31 22:00', '去年');
  assert.equal(L.itemTimeText(new Date(2025, 11, 31, 22, 0).getTime(), new Date(2026, 0, 1, 1, 0).getTime()), '昨天 22:00', '跨年的昨天');
  assert.equal(L.itemTimeText(new Date(2026, 2, 31, 22, 0).getTime(), new Date(2026, 3, 1, 1, 0).getTime()), '昨天 22:00', '跨月的昨天');
  for (const bad of [0, -1, null, undefined, NaN, Infinity, '1791580000000']) assert.equal(L.itemTimeText(bad, now), '', String(bad));
});

test('itemTimeTitle_完整的年月日時分秒_dayKey跨午夜才變', () => {
  assert.equal(L.itemTimeTitle(new Date(2026, 9, 2, 14, 5, 3).getTime()), '2026/10/02 14:05:03');
  assert.equal(L.itemTimeTitle(new Date(2026, 0, 2, 4, 5, 6).getTime()), '2026/01/02 04:05:06');
  for (const bad of [0, null, undefined, NaN]) assert.equal(L.itemTimeTitle(bad), '', String(bad));
  assert.equal(L.dayKey(new Date(2026, 9, 22, 0, 0).getTime()), 20261022);
  assert.equal(L.dayKey(new Date(2026, 9, 22, 23, 59, 59).getTime()), 20261022);
  assert.equal(L.dayKey(new Date(2026, 9, 23, 0, 0).getTime()), 20261023);
});

test('userHtmlText_PlainToHtml的輸出還原成純文字_不經innerHTML', () => {
  assert.equal(L.userHtmlText('幫我整理報表<br>第二行'), '幫我整理報表\n第二行');
  assert.equal(L.userHtmlText('a &lt;b&gt; &amp;&amp; &quot;c&quot; &#39;d&#39;'), 'a <b> && "c" \'d\'');
  assert.equal(L.userHtmlText('&amp;lt;'), '&lt;', '只解一次：原文就是 &lt;');
  assert.equal(L.userHtmlText('caf&#233; &#128512; &#x1F600;'), 'café 😀 😀', 'WebUtility.HtmlEncode 把 160～255 與代理字對編成數字實體');
  assert.equal(L.userHtmlText('<b>x</b><img src=x onerror=alert(1)>y'), 'xy', '其他標籤去掉');
  assert.equal(L.userHtmlText('&nbsp;&foo;&#0;&#55357;&#x110000;'), '&nbsp;&foo;&#0;&#55357;&#x110000;', '不認得的實體、無效的碼位原樣留著');
  for (const bad of [null, undefined, '', 5, {}]) assert.equal(L.userHtmlText(bad), '', String(bad));
});

test('latestPrompt_已載入的最後一則使用者訊息_清除之後沒有_找不到時用標頭或標示在較早的訊息裡', () => {
  const user = (id, html, extra) => Object.assign({ id, kind: 'User', html, time: 1000 }, extra);
  const clear = (id) => ({ id, kind: 'Notice', noticeKind: 'clear' });
  const tool = (id) => ({ id, kind: 'Tool' });
  const items = [user('u1', '第一個'), { id: 'a1', kind: 'Assistant', html: '<p>回答</p>' }, user('u2', '把 &lt;div&gt; 改掉<br>再測試', { attachments: [{}, {}] }), tool('t1'), { id: 'n1', kind: 'Notice', noticeKind: 'cliUpdate' }];
  assert.deepEqual(L.latestPrompt(items, {}, false), { id: 'u2', time: 1000, text: '把 <div> 改掉\n再測試', attachments: 2 });
  assert.deepEqual(L.latestPrompt([user('u', '')], {}, false), { id: 'u', time: 1000, text: '', attachments: 0 }, '只有附件的訊息也算');
  assert.equal(L.latestPrompt([...items, clear('c1')], {}, true), null, '清除之後還沒送新的');
  assert.equal(L.latestPrompt([...items, clear('c1'), tool('t2')], { lastPrompt: { id: 'u2', text: 'x' } }, true), null, '清除提示在已載入的項目裡：不看標頭');
  assert.equal(L.latestPrompt([...items, clear('c1'), user('u3', '清除後')], {}, false).id, 'u3');
  assert.equal(L.latestPrompt([null, user('u1', 'a'), undefined], {}, false).id, 'u1', '缺項目略過');

  // 已載入的都沒有使用者訊息（長的一輪）：桌面的標頭有 lastPrompt 這個鍵就照它
  const hd = { lastPrompt: { id: 'u0', seq: 3, time: 2000, text: '很久以前的指令', attachments: 1 } };
  assert.deepEqual(L.latestPrompt([tool('t1'), tool('t2')], hd, true), { id: 'u0', time: 2000, text: '很久以前的指令', attachments: 1 });
  assert.equal(L.latestPrompt([tool('t1')], { lastPrompt: null }, true), null, '桌面說沒有就是沒有（例如清除提示在還沒載入的地方）');
  assert.deepEqual(L.latestPrompt([tool('t1')], { lastPrompt: { id: 'u0', text: 5, attachments: -1 } }, false), { id: 'u0', time: undefined, text: '', attachments: 0 }, '欄位型別不對時用預設');
  for (const bad of [{ lastPrompt: 'u0' }, { lastPrompt: {} }, { lastPrompt: { id: 7 } }]) assert.equal(L.latestPrompt([], bad, true), null, JSON.stringify(bad));
  // 瀏覽器版的標頭沒有這個鍵：還有較早的訊息就標示在那裡，沒有就是沒有
  assert.deepEqual(L.latestPrompt([tool('t1')], {}, true), { older: true });
  assert.equal(L.latestPrompt([tool('t1')], {}, false), null);
  assert.deepEqual(L.latestPrompt([], null, true), { older: true });
  assert.equal(L.latestPrompt(null, null, false), null);
  // 已載入的找得到時優先（比標頭新，標頭可能還沒重送）
  assert.equal(L.latestPrompt([user('u9', '新的')], hd, true).id, 'u9');
});

test('promptHistory_宿主回的照它_瀏覽器版從已載入的項目找_規則同宿主', () => {
  const user = (id, html, files = 0) => ({ id, kind: 'User', html, time: 1000 + id.length, attachments: Array.from({ length: files }, () => ({})) });
  const items = [
    user('u1', '清除之前的'), { id: 'c1', kind: 'Notice', noticeKind: 'clear' }, user('u2', '重複的'), { id: 'a1', kind: 'Assistant', html: '<p>回答</p>' },
    user('u3', ' ', 1), user('u4', '語音訊息（0:12）', 1), user('u5', '把 &lt;div&gt; 改掉<br>再測試', 2), user('u6', '重複的'),
  ];
  assert.deepEqual(L.promptHistory(null, items).map((p) => [p.id, p.text, p.attachments]),
    [['u6', '重複的', 0], ['u5', '把 <div> 改掉\n再測試', 2], ['u1', '清除之前的', 0]], '清除之前的也列；空白、語音訊息與較舊的重複略過');
  assert.equal(L.promptHistory(null, [user('v', '語音訊息（0:12）')]).length, 1, '沒有附件的是使用者自己打的');
  const many = Array.from({ length: 15 }, (_, i) => user('m' + i, '第 ' + i + ' 則'));
  assert.deepEqual(L.promptHistory(undefined, many).map((p) => p.text), Array.from({ length: 10 }, (_, i) => '第 ' + (14 - i) + ' 則'));
  // 宿主回的（桌面）：照它，不看已載入的項目；壞的略過
  const reply = { prompts: [{ id: 'h1', time: 5, text: '完整的\n指令', attachments: 1 }, null, { id: 7, text: 'x' }, { id: 'h2', text: '' }, { id: 'h3', text: '完整的\n指令' }, { id: 'h4', text: 'ok', attachments: -2 }] };
  assert.deepEqual(L.promptHistory(reply, items), [{ id: 'h1', time: 5, text: '完整的\n指令', attachments: 1 }, { id: 'h4', time: undefined, text: 'ok', attachments: 0 }]);
  assert.deepEqual(L.promptHistory({ prompts: [] }, items), [], '宿主說沒有就是沒有');
  assert.deepEqual(L.promptHistory(null, null), []);
  assert.equal(L.PROMPT_HISTORY_LIMIT, 10);
});

test('promptLine_清單裡一行_換行與空白合併_太長截斷', () => {
  assert.equal(L.promptLine('第一行\n\n  第二行\t結尾 '), '第一行 第二行 結尾');
  assert.equal(L.promptLine('a'.repeat(200), 160), 'a'.repeat(160) + '…');
  assert.equal(L.promptLine(null), '');
});

// ───── 大對話的分批載入（D39，步 12） ─────

test('loadChunks_hub先放最後20筆再往前補_桌面一次全放', () => {
  assert.equal(L.LOAD_CHUNK, 20);
  assert.deepEqual(L.loadChunks(80, 'hub'), [[60, 80], [40, 60], [20, 40], [0, 20]]);
  assert.deepEqual(L.loadChunks(85, 'hub'), [[65, 85], [45, 65], [25, 45], [5, 25], [0, 5]]);
  assert.deepEqual(L.loadChunks(21, 'hub'), [[1, 21], [0, 1]]);
  assert.deepEqual(L.loadChunks(20, 'hub'), [[0, 20]], '剛好一批不分');
  assert.deepEqual(L.loadChunks(80, 'desktop'), [[0, 80]]);
  assert.deepEqual(L.loadChunks(80, 'popout'), [[0, 80]], '只有 hub 分批');
  assert.deepEqual(L.loadChunks(7, 'hub', 3), [[4, 7], [1, 4], [0, 1]]);
  assert.deepEqual(L.loadChunks(7, 'hub', 0), [[0, 7]], '批量不合格用預設');
  for (const bad of [0, -1, 1.5, '80', null, undefined, NaN]) assert.deepEqual(L.loadChunks(bad, 'hub'), [], String(bad));
  // 合起來剛好是 [0, n) 各一次、彼此相接
  for (const n of [1, 19, 20, 21, 39, 40, 41, 80, 323]) {
    const r = L.loadChunks(n, 'hub');
    const seen = new Array(n).fill(0);
    for (const [a, b] of r) for (let i = a; i < b; i++) seen[i]++;
    assert.ok(seen.every((x) => x === 1), 'n=' + n);
    for (let i = 1; i < r.length; i++) assert.equal(r[i][1], r[i - 1][0], 'n=' + n + ' 相接');
    assert.ok(r.every(([a, b]) => b - a <= L.LOAD_CHUNK && b > a), 'n=' + n + ' 每批不超過 20');
  }
});

// ───── HTML 寫入點掃描（D35；取代 Trusted Types＋DOMPurify） ─────

/** 寫入 HTML 或執行字串的寫法（含幾種現在沒用到、之後也不該出現的） */
const SINK_RE = /innerHTML|outerHTML|insertAdjacentHTML|document\.write|\beval\s*\(|\bnew\s+Function\b|createContextualFragment|setHTMLUnsafe|parseHTMLUnsafe|DOMParser|\bsrcdoc\b/;
const FUNCTION_RE = /^\s*(?:export\s+)?(?:async\s+)?function\s*\*?\s*([\w$]+)\s*\(/;
const CALL_RE = /\b(setHtml|patchHtml)\s*\(/g;
/** setHtml／patchHtml 的第二個引數：成員存取的最後一段以 html／Html 結尾（item.html、item.detailHtml、n._pendingHtml），或剛好是 html */
const HTML_ARG_RE = /^[A-Za-z_$][\w$]*(?:\.[\w$]+)*\.[\w$]*[hH]tml$/;
const BACKSLASH = String.fromCharCode(92);

const isCommentLine = (t) => t.startsWith('//') || t.startsWith('*') || t.startsWith('/*');

/** 原始碼的每一行；整行註解（//、*、/* 開頭）換成空字串，行號不變 */
function codeLines(source) {
  return source.split('\n').map((line) => (isCommentLine(line.trim()) ? '' : line));
}

/** 寫入點：{檔名}|{所在的函式}|{去掉空白的內容} */
function scanSinks(files) {
  const out = [];
  for (const { name, source } of files) {
    const lines = codeLines(source);
    lines.forEach((line, i) => {
      if (!SINK_RE.test(line)) return;
      let fn = '(頂層)';
      for (let j = i; j >= 0; j--) {
        const m = FUNCTION_RE.exec(lines[j]);
        if (m) { fn = m[1]; break; }
      }
      out.push(name + '|' + fn + '|' + line.replace(/\s+/g, ''));
    });
  }
  return out.sort();
}

/** 從 start（左括號之後）讀到對應的右括號，回逗號分開的引數（略過字串與巢狀括號） */
function parseArgs(src, start) {
  const args = [];
  let depth = 0;
  let cur = '';
  let quote = null;
  for (let i = start; i < src.length; i++) {
    const ch = src[i];
    if (quote) {
      cur += ch;
      if (ch === BACKSLASH) { cur += src[i + 1] ?? ''; i++; continue; }
      if (ch === quote) quote = null;
      continue;
    }
    if (ch === "'" || ch === '"' || ch === '`') { quote = ch; cur += ch; continue; }
    if (ch === '(' || ch === '[' || ch === '{') depth++;
    else if (ch === ')' || ch === ']' || ch === '}') {
      if (depth === 0) { args.push(cur.trim()); return args; }
      depth--;
    } else if (ch === ',' && depth === 0) { args.push(cur.trim()); cur = ''; continue; }
    cur += ch;
  }
  args.push(cur.trim());
  return args;
}

/** setHtml／patchHtml 的呼叫裡第二個引數不是 html 欄位的：{檔名}:{行}|{函式名}({第二個引數})；函式的定義（function setHtml(）略過 */
function scanHtmlCalls(files) {
  const out = [];
  for (const { name, source } of files) {
    const code = codeLines(source).join('\n');
    for (const m of code.matchAll(CALL_RE)) {
      const before = code.slice(Math.max(0, m.index - 40), m.index);
      if (/\bfunction\s+$/.test(before)) continue;
      const args = parseArgs(code, m.index + m[0].length);
      const second = args.length >= 2 ? args[1] : '';
      if (second === 'html' || HTML_ARG_RE.test(second)) continue;
      const line = code.slice(0, m.index).split('\n').length;
      out.push(name + ':' + line + '|' + m[1] + '(' + second + ')');
    }
  }
  return out;
}

function webSources() {
  return readdirSync(webDir).filter((f) => f.endsWith('.js')).sort().map((name) => ({ name, source: readFileSync(path.join(webDir, name), 'utf8') }));
}

test('網頁沒有新的HTML寫入點', () => {
  const sources = webSources();
  assert.ok(sources.length >= 22, '掃到 Web 底下所有的 .js（現在 ' + sources.length + ' 個）');
  assert.ok(sources.some((s) => s.name === 'liverules.js'));
  assert.deepEqual(scanSinks(sources), [
    'icons.js|fromTemplate|t.innerHTML=markup;',
    "items.js|patchHtml|tmp.innerHTML=html||'';",
    "items.js|rawHtmlOf|returnnode.nodeType===Node.ELEMENT_NODE?node.outerHTML:'#'+node.nodeType+':'+node.nodeValue;",
    "items.js|setHtml|container.innerHTML=html||'';",
  ], '只能是這四行（設計 8.4、計畫 F8）；要加新的寫入點先改計畫與這份清單');
});

test('setHtml與patchHtml只收html欄位', () => {
  const sources = webSources();
  assert.deepEqual(scanHtmlCalls(sources), []);
  const calls = sources.reduce((n, s) => n + Array.from(codeLines(s.source).join('\n').matchAll(CALL_RE)).length, 0);
  assert.ok(calls >= 8, '確實掃到呼叫（現在 ' + calls + ' 處，含兩行定義）');
});

test('掃描本身抓得到', () => {
  const f = (source) => [{ name: 'x.js', source }];
  assert.deepEqual(scanSinks(f('function a() {\n  el.innerHTML = x;\n}')), ['x.js|a|el.innerHTML=x;']);
  assert.deepEqual(scanSinks(f("a.insertAdjacentHTML('x', y)")), ["x.js|(頂層)|a.insertAdjacentHTML('x',y)"]);
  for (const s of ['document.write(x)', 'eval (x)', 'new Function("a")', 'r.createContextualFragment(x)', 'el.setHTMLUnsafe(x)', 'new DOMParser()', 'f.srcdoc = x', 'b.outerHTML = x']) {
    assert.equal(scanSinks(f(s)).length, 1, s);
  }
  assert.deepEqual(scanSinks(f('// el.innerHTML = x;\n/* el.innerHTML */\n * el.innerHTML = x\nel.textContent = x;')), [], '整行註解不算');
  assert.deepEqual(scanHtmlCalls(f('setHtml(x, y)')), ['x.js:1|setHtml(y)']);
  assert.deepEqual(scanHtmlCalls(f('ok();\npatchHtml(\n  el,\n  item.text,\n  ctx)')), ['x.js:2|patchHtml(item.text)'], '跨行的呼叫');
  assert.deepEqual(scanHtmlCalls(f('setHtml(el)')), ['x.js:1|setHtml()'], '沒有第二個引數');
  assert.deepEqual(scanHtmlCalls(f("setHtml(el, item.html || '<b>x</b>')")), ["x.js:1|setHtml(item.html || '<b>x</b>')"]);
  assert.deepEqual(scanHtmlCalls(f('setHtml(el, f(a, b).html)')), ['x.js:1|setHtml(f(a, b).html)'], '不是成員存取鏈');
  assert.deepEqual(scanHtmlCalls(f('setHtml(el, item.html, ctx); patchHtml(md, n._pendingHtml); setHtml(a, item.detailHtml); patchHtml(b, html)')), []);
  assert.deepEqual(scanHtmlCalls(f('export function setHtml(container, markup, ctx) {}\nfunction patchHtml(el, text) {}')), [], '定義不算呼叫');
  assert.deepEqual(scanHtmlCalls(f('// setHtml(x, y)')), [], '註解不算');
});

// ───── 傳輸層 transport-hub.js 的新規則（步 4：D2、D8）─────
// 沙箱只有 window、self、navigator、crypto、TextEncoder、URL、計時器與 console（同 rules.test.mjs）；vm 裡的物件是另一個 realm 的，比對前做 JSON 往返

const hubFile = path.join(repo, 'src', 'MyriagentHub', 'wwwroot', 'transport-hub.js');
const plain = (v) => JSON.parse(JSON.stringify(v));

function loadTransportHub() {
  const sandbox = { navigator: { onLine: true }, crypto: webcrypto, TextEncoder, URL, setTimeout, clearTimeout, console };
  sandbox.window = sandbox;
  sandbox.self = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(readFileSync(hubFile, 'utf8'), sandbox, { filename: 'transport-hub.js' });
  return sandbox.myriagentHubTransport;
}

const HR = loadTransportHub().rules;

test('TH_clientState第4個參數只留對話id', () => {
  const s = HR.clientState(true, true, -480, [CONV_A, DRAFT, CONV_A, 'c1', CONV_A.toUpperCase(), 42, null, CONV_B]);
  assert.deepEqual(Object.keys(s), ['visible', 'focused', 'openConversationIds', 'tzOffsetMinutes', 'features'], '鍵與順序不變（伺服器前四個必填，features 選用）');
  assert.deepEqual(plain(s), { visible: true, focused: true, openConversationIds: [CONV_A, CONV_B], tzOffsetMinutes: -480, features: ['patch1'] }, '草稿、桌面的本機 id、大寫、非字串都濾掉，去重');
  assert.deepEqual(plain(HR.clientState(false, false, 0, 'not-an-array')).openConversationIds, []);
});

test('TH_clientState沒給第4個參數是空陣列', () => {
  assert.deepEqual(plain(HR.clientState(true, false, -480)), { visible: true, focused: false, openConversationIds: [], tzOffsetMinutes: -480, features: ['patch1'] });
  assert.deepEqual(plain(HR.clientState(true, false, -480, undefined)).openConversationIds, []);
});

test('TH_viewingIds上限100', () => {
  const many = Array.from({ length: 150 }, (_, i) => i.toString(16).padStart(32, '0'));
  const out = plain(HR.viewingIds(many));
  assert.equal(out.length, 100, '伺服器 MessageGuard.MaxOpenConversationIds');
  assert.deepEqual(out, many.slice(0, 100), '照原順序');
  assert.deepEqual(plain(HR.viewingIds([DRAFT, ...many.slice(0, 3), many[0]])), many.slice(0, 3), '草稿不佔名額、重複的不算');
  assert.deepEqual(plain(HR.viewingIds(null)), []);
  // 和網頁的 liverules.viewingIds 同一套規則
  const mixed = [CONV_A, DRAFT, 'x', CONV_A, CONV_B, ...many];
  assert.deepEqual(plain(HR.viewingIds(mixed)), L.viewingIds(mixed));
});

test('TH_localAction同源媒體路徑才有newTab', () => {
  const origin = 'https://hub.probe.local';
  for (const href of ['/m/k3', '/t/k3?s=160', '/f/k9', 'https://hub.probe.local/m/a#x']) {
    const a = plain(HR.localAction({ cmd: 'openLink', href }, origin));
    assert.equal(a.kind, 'openLink', href);
    assert.equal(a.sameOrigin, true, href);
    assert.equal(a.newTab, true, href);
  }
  for (const href of ['/app/x', '/', '/media/a', '/mx/a', '/M/a', '/app/m/a']) {
    const a = plain(HR.localAction({ cmd: 'openLink', href }, origin));
    assert.equal(a.sameOrigin, true, href);
    assert.equal(Object.prototype.hasOwnProperty.call(a, 'newTab'), false, href + ' 沒有這個鍵（其他同源結果逐鍵不變）');
  }
  assert.deepEqual(plain(HR.localAction({ cmd: 'openLink', href: '/' }, origin)), { kind: 'openLink', url: 'https://hub.probe.local/', sameOrigin: true });
  const other = plain(HR.localAction({ cmd: 'openLink', href: 'https://example.com/m/a' }, origin));
  assert.equal(other.sameOrigin, false);
  assert.equal(Object.prototype.hasOwnProperty.call(other, 'newTab'), false, '外站本來就開新分頁，不加這個鍵');
  assert.equal(plain(HR.localAction({ cmd: 'openLink', href: '/m/../app/' }, origin)).newTab, undefined, '正規化之後是 /app/');
});

// ───── 桌面遠端操作（sync-protocol 14.7） ─────

test('peerOf_live_down_unsupported_off', () => {
  assert.equal(L.peerOf({ sync: { state: 'online', peer: true } }, 'desktop'), 'live');
  assert.equal(L.peerOf({ sync: { state: 'connecting', peer: true } }, 'desktop'), 'live', '交握完成就能送（還在拉也一樣）');
  assert.equal(L.peerOf({ sync: { state: 'online', peer: false } }, 'desktop'), 'unsupported', '連著但伺服器或這台不支援');
  assert.equal(L.peerOf({ sync: { state: 'online' } }, 'desktop'), 'unsupported');
  for (const state of ['offline', 'connecting', 'paused', 'stopped']) assert.equal(L.peerOf({ sync: { state, peer: false } }, 'desktop'), 'down', state);
  assert.equal(L.peerOf({ sync: null }, 'desktop'), 'off');
  assert.equal(L.peerOf(null, 'desktop'), 'off');
  assert.equal(L.peerOf(undefined, 'desktop'), 'off');
  assert.equal(L.peerOf({ sync: { state: 'online', peer: true } }, 'hub'), 'off', '瀏覽器版不用');
  assert.equal(L.controllable('replica', 'live'), true);
  assert.equal(L.controllable('replica', 'down'), false);
  assert.equal(L.controllable('local', 'live'), false);
});

test('composerGate_副本_能操作時比照瀏覽器_沒連上擋著_不支援唯讀', () => {
  const header = { permission: 'FullAccess' };
  assert.deepEqual(L.composerGate({ mode: 'desktop', origin: 'replica', header: {}, device: dev(), peer: 'live' }), { canSend: true, readOnly: false, reason: null });
  assert.deepEqual(L.composerGate({ mode: 'desktop', origin: 'replica', header: {}, device: dev(), peer: 'down' }),
    { canSend: false, readOnly: false, reason: L.PEER_DOWN_TEXT });
  assert.equal(L.composerGate({ mode: 'desktop', origin: 'replica', header: {}, device: dev(), peer: 'unsupported' }).readOnly, true);
  assert.equal(L.composerGate({ mode: 'desktop', origin: 'replica', header: {}, device: dev(), peer: 'off' }).readOnly, true);
  assert.equal(L.composerGate({ mode: 'desktop', origin: 'replica', header, device: dev({ online: false }), peer: 'live' }).reason, '電腦離線：OFFICE-PC 重新上線後才能送出');
  assert.equal(L.composerGate({ mode: 'desktop', origin: 'replica', header, device: dev({ level: 'View' }), peer: 'live' }).reason, '在 OFFICE-PC 的設定中允許遠端操作');
  assert.equal(L.composerGate({ mode: 'desktop', origin: 'replica', header, device: dev({ allowFull: false }), peer: 'live' }).reason,
    'OFFICE-PC 沒有允許遠端完全自動，先把權限改成其他模式');
  assert.equal(L.composerGate({ mode: 'desktop', origin: 'replica', header: {}, device: dev(), peer: 'live', live: false }).canSend, true, '桌面不看瀏覽器的 live');
  assert.equal(L.composerGate({ mode: 'desktop', origin: 'local', header, device: null, peer: 'live' }).canSend, true, '這台的對話不受影響');
});

test('cardGate_permissionChoices_attachGate_副本的桌面遠端操作', () => {
  assert.deepEqual(L.cardGate({ mode: 'desktop', origin: 'replica', device: dev(), peer: 'live' }), { enabled: true, reason: null });
  assert.deepEqual(L.cardGate({ mode: 'desktop', origin: 'replica', device: dev(), peer: 'down' }), { enabled: false, reason: L.PEER_DOWN_TEXT });
  assert.equal(L.cardGate({ mode: 'desktop', origin: 'replica', device: dev(), peer: 'unsupported' }).enabled, false);
  assert.equal(L.cardGate({ mode: 'desktop', origin: 'replica', device: dev({ level: 'Off' }), peer: 'live' }).reason, '在 OFFICE-PC 的設定中允許遠端操作');
  const modes = [{ value: 'Ask', label: '每步詢問' }, { value: 'FullAccess', label: '完全自動' }];
  assert.equal(L.permissionChoices(modes, dev({ allowFull: false }), 'desktop', 'replica', 'live').find((m) => m.value === 'FullAccess').disabled, true);
  assert.equal(L.permissionChoices(modes, dev({ allowFull: false }), 'desktop', 'local', 'live').find((m) => m.value === 'FullAccess').disabled, false);
  assert.equal(L.permissionChoices(modes, dev({ allowFull: false }), 'desktop').find((m) => m.value === 'FullAccess').disabled, false, '舊的呼叫方式不變');
  assert.equal(L.attachGate({ mode: 'desktop', origin: 'replica', device: dev(), peer: 'down' }).reason, L.PEER_DOWN_TEXT);
  assert.deepEqual(L.attachGate({ mode: 'desktop', origin: 'replica', device: dev(), peer: 'live' }), { enabled: true, reason: null });
  assert.equal(L.attachGate({ mode: 'desktop', origin: 'replica', device: dev({ level: 'View' }), peer: 'live' }).reason, '在 OFFICE-PC 的設定中允許遠端操作');
  assert.equal(L.attachGate({ mode: 'desktop', origin: 'local', device: null, peer: 'live' }).enabled, true);
});

test('wantsPeerId_副本與其他電腦的指令才帶_瀏覽器版不帶', () => {
  assert.equal(L.wantsPeerId({ cmd: 'send', conversationId: CONV_A }, 'desktop', 'replica', 'd_me'), true);
  assert.equal(L.wantsPeerId({ cmd: 'send', conversationId: CONV_A }, 'desktop', 'local', 'd_me'), false);
  assert.equal(L.wantsPeerId({ cmd: 'send', conversationId: CONV_A, commandId: 'x' }, 'desktop', 'replica', 'd_me'), false, '已經帶了');
  assert.equal(L.wantsPeerId({ cmd: 'cliCheck', deviceId: 'd_other' }, 'desktop', null, 'd_me'), true);
  assert.equal(L.wantsPeerId({ cmd: 'cliCheck', deviceId: 'd_me' }, 'desktop', null, 'd_me'), false, '這台的在本機做');
  assert.equal(L.wantsPeerId({ cmd: 'cliCheck', deviceId: 'd_other' }, 'desktop', null, null), false, '未配對');
  assert.equal(L.wantsPeerId({ cmd: 'send', conversationId: CONV_A }, 'hub', 'replica', 'd_me'), false);
  assert.equal(L.wantsPeerId({ conversationId: CONV_A }, 'desktop', 'replica', 'd_me'), false, '沒有 cmd');
  assert.equal(L.wantsPeerId(null, 'desktop', 'replica', 'd_me'), false);
  assert.equal(L.wantsPeerId({ cmd: 'openPath', conversationId: CONV_A, path: 'C:\a' }, 'desktop', 'replica', 'd_me'), false, '本機的指令不帶');
  assert.equal(L.wantsPeerId({ cmd: 'open', conversationId: CONV_A }, 'desktop', 'replica', 'd_me'), false);
  assert.equal(L.wantsPeerId({ cmd: 'peerUpload', conversationId: CONV_A }, 'desktop', 'replica', 'd_me'), false);
});

test('ulid_26字_時間在前_同一毫秒依隨機位元組', () => {
  const id = L.ulid(NOW, new Uint8Array(10).fill(255));
  assert.match(id, /^[0-9A-HJKMNP-TV-Z]{26}$/);
  assert.equal(L.ulid(NOW, new Uint8Array(10)).slice(0, 10), id.slice(0, 10));
  assert.ok(L.ulid(NOW + 1, new Uint8Array(10)) > L.ulid(NOW, new Uint8Array(10).fill(255)), '時間大的排後面');
  assert.equal(L.ulid(0, new Uint8Array(10)), '0'.repeat(26));
  const random = webcrypto.getRandomValues(new Uint8Array(10));
  assert.equal(L.ulid(NOW, random).length, 26);
});

test('peerDraftDevices_只列在線Control而且有可用帳戶的其他電腦', () => {
  const devices = [
    { id: 'd_me', name: '這台', online: true, remoteControlLevel: 'Control' },
    { id: 'd_a', name: 'OFFICE-PC', online: true, remoteControlLevel: 'Control' },
    { id: 'd_b', name: 'HOME-PC', online: false, remoteControlLevel: 'Control' },
    { id: 'd_c', name: 'LAB-PC', online: true, remoteControlLevel: 'View' },
    { id: 'd_d', name: 'NOACC', online: true, remoteControlLevel: 'Control' },
  ];
  const cell = (deviceId, extra) => Object.assign({ deviceId, status: 'ready', online: true }, extra);
  const accounts = [
    { id: 'acc-local', logicalId: 'la_1', provider: 'Claude', label: '主帳戶', devices: [cell('d_me'), cell('d_a'), cell('d_b'), cell('d_c')] },
    { id: 'la_2', remoteOnly: true, provider: 'Codex', label: '公司', devices: [cell('d_a'), cell('d_d', { status: 'notLoggedIn' })] },
    { id: 'acc-unlinked', logicalId: null, provider: 'Claude', label: '沒進目錄', devices: null },
    { id: 'acc-bad', logicalId: 'la_3', provider: 'Claude', label: '身分不同', devices: [cell('d_a', { identityOk: false })] },
  ];
  const r = L.peerDraftDevices({ accounts, devices, ownDeviceId: 'd_me' });
  assert.deepEqual(r.map((d) => d.deviceId), ['d_a']);
  assert.deepEqual(r[0].accounts.map((a) => [a.id, a.shownId, a.provider]), [['la_1', 'acc-local', 'Claude'], ['la_2', 'la_2', 'Codex']]);
  assert.equal(L.peerDraftAccount(r[0], 'acc-local').id, 'la_1', '目前的帳戶在那台可用就沿用');
  assert.equal(L.peerDraftAccount(r[0], 'acc-other').id, 'la_1', '否則第一個');
  assert.equal(L.peerDraftAccount({ accounts: [] }, 'x'), null);
  assert.deepEqual(L.peerDraftDevices({}), []);
});

// ───── 其他電腦的對話換帳戶（2026-10-25：只能換成那台已登入的帳戶） ─────

/** 桌面的帳戶：這台有連結的（本機 id＋logicalId）、只在其他電腦的（remoteOnly）、沒進目錄的、那台沒登入與身分不符的 */
function peerAccounts() {
  const cell = (deviceId, extra) => Object.assign({ deviceId, status: 'ready', online: true, plan: 'Claude Max', statusText: '已登入' }, extra);
  return [
    { id: 'acc-local', logicalId: 'la_1', provider: 'Claude', label: '主帳戶', color: '#111111', plan: '這台的方案', devices: [cell('d_me'), cell('d_a')] },
    { id: 'la_2', remoteOnly: true, provider: 'Codex', label: '公司', color: '#222222', devices: [cell('d_a', { plan: 'ChatGPT Pro' })] },
    { id: 'acc-unlinked', logicalId: null, provider: 'Claude', label: '沒進目錄', devices: null },
    { id: 'acc-off', logicalId: 'la_3', provider: 'Claude', label: '那台沒登入', devices: [cell('d_me'), cell('d_a', { status: 'notLoggedIn' })] },
    { id: 'acc-bad', logicalId: 'la_4', provider: 'Grok', label: '身分不同', devices: [cell('d_a', { identityOk: false })] },
  ];
}

test('deviceAccounts_只列那台已登入可用的_id是邏輯帳戶_方案照那台的格子', () => {
  const r = L.deviceAccounts(peerAccounts(), 'd_a');
  assert.deepEqual(r.map((a) => [a.id, a.logicalId, a.shownId, a.provider, a.label]), [
    ['la_1', 'la_1', 'acc-local', 'Claude', '主帳戶'],
    ['la_2', 'la_2', 'la_2', 'Codex', '公司'],
  ]);
  assert.equal(r[0].plan, 'Claude Max', '方案是那台的格子的，不是這台的');
  assert.equal(r[1].plan, 'ChatGPT Pro');
  assert.ok(r.every((a) => a.status === 'ready' && a.remoteOnly === false), 'runningAccountMenu 照 status、remoteOnly 挑：一律能選');
  assert.deepEqual(L.deviceAccounts(peerAccounts(), 'd_me').map((a) => a.id), ['la_1', 'la_3']);
  assert.deepEqual(L.deviceAccounts(peerAccounts(), 'd_zz'), [], '那台沒有任何格子');
  assert.deepEqual(L.deviceAccounts(peerAccounts(), null), []);
  assert.deepEqual(L.deviceAccounts(null, 'd_a'), []);
  // 瀏覽器版的帳戶本來就是邏輯帳戶（id 等於 logicalId）
  const hub = [{ id: 'la_9', logicalId: 'la_9', provider: 'Claude', label: '瀏覽器', devices: [{ deviceId: 'd_a', status: 'ready' }] }];
  assert.deepEqual(L.deviceAccounts(hub, 'd_a').map((a) => [a.id, a.shownId]), [['la_9', 'la_9']]);
});

test('headerAccountId與findAccount_副本看replica的邏輯帳戶_這台有連結時找到本機的那一筆', () => {
  const replica = { accountId: '', replica: { running: false, accountId: 'la_1' } };
  assert.equal(L.headerAccountId(replica, 'replica'), 'la_1');
  assert.equal(L.headerAccountId({ accountId: '', replica: { running: true } }, 'replica'), null, '目錄還沒到');
  assert.equal(L.headerAccountId({ accountId: 'la_7' }, 'remote'), 'la_7');
  assert.equal(L.headerAccountId({ accountId: 'acc-x' }, 'local'), 'acc-x');
  assert.equal(L.headerAccountId(null, 'replica'), null);
  assert.equal(L.findAccount(peerAccounts(), 'la_1').id, 'acc-local', '這台有連結：晶片顯示本機那一筆的名稱與顏色');
  assert.equal(L.findAccount(peerAccounts(), 'la_2').id, 'la_2', 'remoteOnly');
  assert.equal(L.findAccount(peerAccounts(), 'acc-off').id, 'acc-off', '先比 id');
  assert.equal(L.findAccount(peerAccounts(), 'la_none'), null);
  assert.equal(L.findAccount(peerAccounts(), null), null);
});

test('remoteAccountMenu_副本能操作時列那台登入的帳戶_沒連上停用_不支援唯讀', () => {
  const header = { accountId: '', deviceId: 'd_a', replica: { running: false, accountId: 'la_1' } };
  const base = { mode: 'desktop', origin: 'replica', header, device: dev({ id: 'd_a', name: 'DEMO-PC-2' }), peer: 'live', accounts: peerAccounts() };

  const ok = L.remoteAccountMenu(base);
  assert.equal(ok.remote, true);
  assert.equal(ok.enabled, true);
  assert.equal(ok.reason, null);
  assert.equal(ok.current, 'la_1');
  assert.deepEqual(ok.accounts.map((a) => a.id), ['la_1', 'la_2']);
  assert.equal(ok.empty, null, '有其他帳戶可以換');
  assert.equal(ok.scope, '在 DEMO-PC-2 登入的帳戶');

  // 那台只登入了目前這一個：說明沒有其他的
  const only = L.remoteAccountMenu(Object.assign({}, base, { accounts: peerAccounts().slice(0, 1) }));
  assert.deepEqual(only.accounts.map((a) => a.id), ['la_1']);
  assert.equal(only.empty, '沒有其他在 DEMO-PC-2 登入的帳戶');
  assert.equal(L.remoteAccountMenu(Object.assign({}, base, { accounts: [] })).empty, '沒有在 DEMO-PC-2 登入的帳戶');

  // 同步沒連上：停用並說明；伺服器或這台不支援：唯讀（照舊不能換）
  const down = L.remoteAccountMenu(Object.assign({}, base, { peer: 'down' }));
  assert.equal(down.enabled, false);
  assert.equal(down.reason, L.PEER_DOWN_TEXT);
  assert.deepEqual(L.remoteAccountMenu(Object.assign({}, base, { peer: 'unsupported' })).enabled, false);

  // 那台離線、不允許遠端操作：晶片照樣可以按，點了只說明原因、不列帳戶
  const offline = L.remoteAccountMenu(Object.assign({}, base, { device: dev({ id: 'd_a', name: 'DEMO-PC-2', online: false }) }));
  assert.equal(offline.enabled, true);
  assert.match(offline.reason, /DEMO-PC-2 重新上線後才能更換帳戶/);
  assert.deepEqual(offline.accounts, []);
  const view = L.remoteAccountMenu(Object.assign({}, base, { device: dev({ id: 'd_a', name: 'DEMO-PC-2', level: 'View' }) }));
  assert.match(view.reason, /允許遠端操作/);
});

test('remoteAccountMenu_瀏覽器版已開始的對話也只列那台登入的_草稿與這台的對話照本機的選單', () => {
  const accounts = [
    { id: 'la_1', logicalId: 'la_1', provider: 'Claude', label: 'A', devices: [{ deviceId: 'd_a', status: 'ready' }, { deviceId: 'd_b', status: 'ready' }] },
    { id: 'la_2', logicalId: 'la_2', provider: 'Codex', label: 'B', devices: [{ deviceId: 'd_a', status: 'absent' }, { deviceId: 'd_b', status: 'ready' }] },
  ];
  const hub = L.remoteAccountMenu({ mode: 'hub', origin: 'remote', header: { accountId: 'la_1', deviceId: 'd_a' }, device: dev({ id: 'd_a' }), live: true, accounts });
  assert.equal(hub.remote, true);
  assert.equal(hub.current, 'la_1');
  assert.deepEqual(hub.accounts.map((a) => a.id), ['la_1'], 'B 沒登入 d_a');
  assert.equal(hub.empty, '沒有其他在 OFFICE-PC 登入的帳戶');
  const other = L.remoteAccountMenu({ mode: 'hub', origin: 'remote', header: { accountId: 'la_1', deviceId: 'd_b' }, device: dev({ id: 'd_b' }), live: true, accounts });
  assert.deepEqual(other.accounts.map((a) => a.id), ['la_1', 'la_2']);
  assert.match(L.remoteAccountMenu({ mode: 'hub', origin: 'remote', header: {}, device: dev(), live: false, accounts }).reason, /沒有連線/);

  for (const origin of ['draft', 'local']) {
    for (const mode of ['hub', 'desktop']) assert.equal(L.remoteAccountMenu({ mode, origin, accounts }).remote, false, mode + ' ' + origin);
  }
  assert.equal(L.remoteAccountMenu({ mode: 'desktop', origin: 'remote', accounts }).remote, false, '桌面沒有 remote 這種來源');
});

// ───── 手機的視窗高度 ─────

/**
 * 用 100vh 的高度宣告（height、min-height、max-height）在同一條規則裡沒有排在後面的 100dvh 版本的，回傳「選擇器 { 宣告 }」。
 * iOS（Safari 與 iOS 的 Chrome）的 100vh 是工具列收起時的高度、比看得到的區域高，body 不捲動時最下面的輸入區被瀏覽器的工具列蓋住
 * （2026-10-22 手機 Chrome 開網頁版打不了字）；排在後面的 dvh 讓認得的瀏覽器改用看得到的高度
 */
function fullVhWithoutDvh(css) {
  const bad = [];
  const src = css.replace(/\/\*[\s\S]*?\*\//g, '');
  for (const m of src.matchAll(/([^{}]*)\{([^{}]*)\}/g)) {
    const decls = m[2].split(';').map((d) => d.trim()).filter(Boolean).map((d) => {
      const i = d.indexOf(':');
      return i < 0 ? null : { prop: d.slice(0, i).trim().toLowerCase(), value: d.slice(i + 1).trim() };
    }).filter(Boolean);
    decls.forEach((d, i) => {
      if (!/^(min-|max-)?height$/.test(d.prop) || !/\b100vh\b/.test(d.value)) return;
      if (!decls.slice(i + 1).some((x) => x.prop === d.prop && /\b100dvh\b/.test(x.value))) bad.push(m[1].trim() + ' { ' + d.prop + ': ' + d.value + ' }');
    });
  }
  return bad;
}

test('CSS的100vh高度都在同一條規則裡配上100dvh_手機的輸入區不被瀏覽器工具列蓋住', () => {
  // 陽性對照：沒配的、dvh 排在前面（被後面的 vh 蓋掉）的、@media 裡的都抓到；配好的與不是滿版的 vh 不算
  assert.deepEqual(fullVhWithoutDvh(
    '.a { height: 100vh; } .b { max-height: calc(100vh - 4px); max-height: calc(100dvh - 4px); } .c { height: 100dvh; height: 100vh; }'
    + ' .d { max-height: 50vh; } /* .e { height: 100vh; } */ @media (max-width: 600px) { .f { min-height: 100vh; } }'),
  ['.a { height: 100vh }', '.c { height: 100vh }', '.f { min-height: 100vh }']);
  const files = readdirSync(webDir).filter((f) => f.endsWith('.css')).sort();
  assert.ok(files.includes('app.css') && files.includes('chat.css'), files.join(','));
  for (const name of files) assert.deepEqual(fullVhWithoutDvh(readFileSync(path.join(webDir, name), 'utf8')), [], name);
  assert.match(readFileSync(path.join(webDir, 'app.css'), 'utf8'), /\.app\s*\{[^}]*height:\s*100vh;\s*height:\s*100dvh/, '整頁的高度');
});

// ───── 即時更新的增量（itemPatch，patch1）─────

test('mergeHeaderLists_沒帶清單沿用上一份_帶了照新的_沒有上一份補空陣列', () => {
  const cmds = [{ name: 'compact', description: '壓縮', argumentHint: '' }];
  const models = [{ value: 'default', label: '預設', description: '', efforts: [] }];
  const prev = { id: 'c1', title: '舊', commands: cmds, models };
  const slim = { id: 'c1', title: '新', running: true };
  const out = L.mergeHeaderLists(prev, slim);
  assert.equal(out.title, '新');
  assert.equal(out.running, true);
  assert.equal(out.commands, cmds);
  assert.equal(out.models, models);
  assert.equal('commands' in slim, false, '不改傳入的物件');
  // 帶了的照新的（空陣列也算帶了）
  const full = L.mergeHeaderLists(prev, { id: 'c1', commands: [], models: [{ value: 'x' }] });
  assert.deepEqual(full.commands, []);
  assert.deepEqual(full.models, [{ value: 'x' }]);
  // 只帶一份：另一份沿用
  const one = L.mergeHeaderLists(Object.assign({}, prev, { agents: [{ value: 'rev' }] }), { id: 'c1', models: [] });
  assert.equal(one.commands, cmds);
  assert.deepEqual(one.models, []);
  assert.deepEqual(one.agents, [{ value: 'rev' }]);
  // 沒有上一份（不應發生）：補空陣列，選單照樣能畫
  assert.deepEqual(L.mergeHeaderLists(null, { id: 'c1' }), { id: 'c1', commands: [], models: [], agents: [], outputStyles: [] });
  assert.deepEqual(L.mergeHeaderLists({ id: 'c1', commands: 'x' }, { id: 'c1' }).commands, []);
  assert.equal(L.mergeHeaderLists(prev, null), null);
  assert.deepEqual(L.HEADER_LIST_FIELDS, ['commands', 'models', 'agents', 'outputStyles']);
});

test('mergeQueuedHeaders_同一批兩份省略清單的標頭_合併後照樣不帶清單_套用時沿用畫面上的', () => {
  const models = [{ value: 'default', label: '預設', description: '', efforts: ['high'] }, { value: 'opus', label: 'Opus', description: '', efforts: ['high'] }];
  const cmds = [{ name: 'compact', description: '壓縮', argumentHint: '' }];
  const shown = { id: 'c1', model: 'default', effort: 'high', commands: cmds, models };
  // 2026-11-08 的錯：執行中同一個影格排了兩份省略清單的標頭，以前合併時補了空陣列，套用後模型清單被清空
  const a = { id: 'c1', running: true, title: '一' };
  const b = { id: 'c1', running: true, title: '二' };
  const queued = L.mergeQueuedHeaders(a, b);
  assert.equal(queued.title, '二');
  for (const f of L.HEADER_LIST_FIELDS) assert.equal(f in queued, false, f + ' 不能變成「帶了」');
  const applied = L.mergeHeaderLists(shown, queued);
  assert.equal(applied.models, models);
  assert.equal(applied.commands, cmds);
  assert.deepEqual(L.missingHeaderLists(shown, queued), []);
  // 排著的那份帶了清單、後到的省略：沿用排著的（清單換過的那一份不能被後到的省略蓋掉）
  const fresh = [{ value: 'fable', label: 'Fable', description: '', efforts: [] }];
  const kept = L.mergeQueuedHeaders({ id: 'c1', models: fresh, commands: [] }, b);
  assert.equal(kept.models, fresh);
  assert.deepEqual(kept.commands, []);
  assert.equal('agents' in kept, false);
  // 後到的帶了就照後到的；不改傳入的物件
  assert.deepEqual(L.mergeQueuedHeaders({ id: 'c1', models: fresh }, { id: 'c1', models: [] }).models, []);
  assert.equal('models' in b, false);
  assert.equal(L.mergeQueuedHeaders(null, b), b);
  assert.equal(L.mergeQueuedHeaders(a, null), null);
});

test('app.js 排著的標頭不用 mergeHeaderLists 合併（會補空陣列清掉清單）', () => {
  const src = readFileSync(path.join(webDir, 'app.js'), 'utf8');
  assert.match(src, /q\.header = q\.header \? mergeQueuedHeaders\(q\.header, m\.header\) : m\.header/);
  assert.doesNotMatch(src, /mergeHeaderLists\(/);
});

test('applyItemPatch_接在基底後面_長欄位照at與s拼_沒列的沿用基底', () => {
  const base = { id: 'i1', kind: 'Assistant', status: 'Running', html: '<p>前面</p>', detail: '細節', output: '輸出' };
  const patch = { type: 'itemPatch', id: 'i1', rv: 12, baseRv: 9,
    set: { id: 'i1', kind: 'Assistant', status: 'Waiting' },
    ops: [{ f: 'html', at: 5, s: '，接著</p>', n: 12 }, { f: 'output', at: 0, s: '整欄重送', n: 4 }] };
  const next = L.applyItemPatch(base, 9, patch);
  assert.deepEqual(next, { id: 'i1', kind: 'Assistant', status: 'Waiting', html: '<p>前面，接著</p>', output: '整欄重送', detail: '細節' });
  assert.equal(base.html, '<p>前面</p>', '不改基底');
  assert.equal(L.applyItemPatch(base, 9, Object.assign({}, patch, { ops: [] })).html, '<p>前面</p>', '沒有 op＝長欄位全沿用');
});

test('applyItemPatch_對不上回null', () => {
  const base = { id: 'i1', html: '<p>前面</p>' };
  const ok = { id: 'i1', rv: 2, baseRv: 1, set: { id: 'i1' }, ops: [{ f: 'html', at: 5, s: 'x', n: 6 }] };
  assert.ok(L.applyItemPatch(base, 1, ok));
  const bad = (over) => L.applyItemPatch(base, 1, Object.assign({}, ok, over));
  assert.equal(L.applyItemPatch(base, 2, ok), null, 'baseRv 不是網頁記著的');
  assert.equal(L.applyItemPatch(base, undefined, ok), null, '網頁沒有這一筆的即時版本');
  assert.equal(L.applyItemPatch(null, 1, ok), null, '沒有基底');
  assert.equal(bad({ rv: 1 }), null, 'rv 沒有比基底新');
  assert.equal(bad({ id: 'i2', set: { id: 'i2' } }), null, '不同的項目');
  assert.equal(bad({ set: { id: 'i1', html: '<p>x</p>' } }), null, 'set 不得帶長欄位');
  assert.equal(bad({ ops: [{ f: 'html', at: 10, s: 'x', n: 11 }] }), null, 'at 超出範圍（基底 9 個碼元）');
  assert.equal(bad({ ops: [{ f: 'html', at: 5, s: 'x', n: 7 }] }), null, 'n 對不上');
  assert.equal(bad({ ops: [{ f: 'text', at: 0, s: 'x', n: 1 }] }), null, '不認得的欄位');
  assert.equal(bad({ ops: [{ f: 'html', at: 0, s: 'a', n: 1 }, { f: 'html', at: 0, s: 'b', n: 1 }] }), null, '同一欄兩次');
  assert.equal(bad({ ops: null }), null);
  assert.equal(bad({ set: null }), null);
});

test('patchReopenDue_同一個對話10秒最多重送一次open', () => {
  assert.equal(L.patchReopenDue(undefined, NOW), true);
  assert.equal(L.patchReopenDue(NOW, NOW + L.PATCH_REOPEN_MS - 1), false);
  assert.equal(L.patchReopenDue(NOW, NOW + L.PATCH_REOPEN_MS), true);
});

// ───── 觸控捲動（手機、平板；2026-10-25 使用者回報：往上滑一直閃） ─────

test('touchUnsticks_手指往下拖超過6px才算_往上拖與不是數字的不算', () => {
  assert.equal(L.TOUCH_UNSTICK_PX, 6);
  assert.equal(L.touchUnsticks(300, 307), true);
  assert.equal(L.touchUnsticks(300, 306), false, '輕點的抖動');
  assert.equal(L.touchUnsticks(300, 250), false, '往上拖（往下看較新的）');
  assert.equal(L.touchUnsticks(null, 320), false);
  assert.equal(L.touchUnsticks(300, undefined), false);
});

test('touchScrolling_手指按著_放開後慣性還在動才算_沒摸過的捲動事件不算', () => {
  assert.equal(L.touchScrolling({ touching: true, touchEndAt: null, lastScrollAt: null }, 0), true);
  assert.equal(L.touchScrolling(null, 0), false);
  assert.equal(L.touchScrolling({ touching: false, touchEndAt: null, lastScrollAt: 1000 }, 1010), false, '沒摸過：程式改 scrollTop 引起的捲動事件不算');
  assert.equal(L.touchScrolling({ touching: false, touchEndAt: 1000, lastScrollAt: 1500 }, 1600), true, '放開後的慣性');
  assert.equal(L.touchScrolling({ touching: false, touchEndAt: 1000, lastScrollAt: 1500 }, 1500 + L.SCROLL_IDLE_MS), false, '捲動停下來了');
  assert.equal(L.touchScrolling({ touching: false, touchEndAt: 1000, lastScrollAt: 3990 }, 1000 + L.MOMENTUM_MAX_MS), false, '放開太久了');
});

test('anchorShift_變動前整則在上緣以上的才補_依文件順序扣掉前面的變動_跨在上緣與下面的不補', () => {
  // 上緣在 100：下緣 80 的那則長了 267（變動前下緣 -187）→ 補 267
  assert.equal(L.anchorShift([{ bottom: 80, delta: 267 }], 100), 267);
  // 長高之後跨進畫面（下緣 300、長了 267 → 變動前 33）照樣補
  assert.equal(L.anchorShift([{ bottom: 300, delta: 267 }], 100), 267);
  // 變動前就跨在上緣（下緣 400、長了 100 → 變動前 300 > 100）：不補
  assert.equal(L.anchorShift([{ bottom: 400, delta: 100 }], 100), 0);
  // 畫面裡與下面的不補；變矮的照樣補（負的）
  assert.equal(L.anchorShift([{ bottom: 900, delta: 50 }], 100), 0);
  assert.equal(L.anchorShift([{ bottom: 40, delta: -60 }], 100), -60);
  // 兩則都在上面：第二則的下緣被第一則推了 200，變動前是 350 - 50 - 200 = 100 ≤ 100 → 兩則都補
  assert.equal(L.anchorShift([{ bottom: 20, delta: 200 }, { bottom: 350, delta: 50 }], 100), 250);
  // 第二則變動前跨在上緣：只補第一則
  assert.equal(L.anchorShift([{ bottom: 20, delta: 200 }, { bottom: 420, delta: 50 }], 100), 200);
  // 不合格的略過
  assert.equal(L.anchorShift([null, { bottom: NaN, delta: 5 }, { bottom: 0, delta: 0 }], 100), 0);
  assert.equal(L.anchorShift(null, 100), 0);
  assert.equal(L.anchorShift([{ bottom: 0, delta: 5 }], undefined), 0);
});

// ───── 第十批：延伸與委派的關聯（liverules.nestFamilies、originLink） ─────
test('liverules：側欄的子對話掛在主對話底下，一家子排在最上面那一列的位置；主對話不在清單裡、搜尋中照原本的順序', () => {
  const rows = [
    { id: 'k1', parentId: 'p', group: '今天' },
    { id: 'x', group: '今天' },
    { id: 'k2', parentId: 'p', group: '昨天' },
    { id: 'p', group: '昨天' },
    { id: 'orphan', parentId: 'gone', group: '昨天' },
    { id: 'y', group: '過去 7 天' },
  ];
  const nested = L.nestFamilies(rows, false);
  assert.deepEqual(nested.map((e) => e.c.id + (e.child ? (e.last ? '└' : '├') : '') + '@' + e.g), ['p@今天', 'k1├@今天', 'k2└@今天', 'x@今天', 'orphan@昨天', 'y@過去 7 天']);
  assert.deepEqual(L.nestFamilies(rows, true).map((e) => e.c.id + (e.child ? '*' : '')), ['k1', 'x', 'k2', 'p', 'orphan', 'y']);
  // 主對話本身是子對話（不會發生：子對話不再往下委派）：只掛一層、不重複、不漏
  const deep = L.nestFamilies([{ id: 'a' }, { id: 'b', parentId: 'a' }, { id: 'c', parentId: 'b' }], false);
  assert.deepEqual(deep.map((e) => e.c.id + (e.child ? '*' : '')), ['a', 'b*', 'c']);
  assert.deepEqual(L.nestFamilies(null, false), []);
});

test('liverules：標頭的來源晶片——委派寫主對話、延伸寫原對話，來源不在清單裡時不能點', () => {
  const byId = new Map([['p', { id: 'p', title: '修正登入流程卡住並補上單元測試' }], ['s', { id: 's', title: '短標題' }]]);
  assert.deepEqual(L.originLink({ id: 'k', parentId: 'p' }, byId),
    { kind: 'delegated', id: 'p', label: '由「修正登入流程卡住並補上單元測試」委派', title: '帳戶委派的子對話：主對話是「修正登入流程卡住並補上單元測試」（點一下開啟）', open: true });
  assert.equal(L.originLink({ id: 'k', parentId: 'gone' }, byId).open, false);
  const fork = L.originLink({ id: 'f', forkedFrom: 's', forkedFromTitle: '舊標題' }, byId);
  assert.equal(fork.label, '延伸自「短標題」');
  assert.equal(fork.open, true);
  const lost = L.originLink({ id: 'f', forkedFrom: 'gone', forkedFromTitle: '一個非常非常長的原對話標題超過十八個字了吧' }, byId);
  assert.equal(lost.label, '延伸自「一個非常非常長的原對話標題超過十八個…」');
  assert.equal(lost.open, false);
  assert.equal(L.originLink({ id: 'n' }, byId), null);
  assert.equal(L.originLink(null, byId), null);
});

test('openOrder_先聚焦窗格作用中的分頁_再其他窗格作用中的_最後其餘的', () => {
  const panes = [{ id: 'p1', tabs: ['a', 'b', 'c'], active: 'c' }, { id: 'p2', tabs: ['d', 'e'], active: 'd' }];
  assert.deepEqual(L.openOrder(panes, 'p2'), ['d', 'c', 'a', 'b', 'e']);
  assert.deepEqual(L.openOrder(panes, 'p1'), ['c', 'd', 'a', 'b', 'e']);
  // 聚焦的窗格不存在、作用中的分頁不在分頁裡：照窗格順序；重複的 id 只一次；壞的窗格與 id 略過
  assert.deepEqual(L.openOrder([{ id: 'p1', tabs: ['a', 'b'], active: 'x' }, null, { id: 'p2', tabs: ['b', 3, 'c'], active: 'c' }], 'nope'), ['c', 'a', 'b']);
  assert.deepEqual(L.openOrder(null, 'p1'), []);
  // 手機常見：一個窗格、看得到的是最後一個分頁
  assert.deepEqual(L.openOrder([{ id: 'p1', tabs: ['a', 'b', 'c', 'd'], active: 'd' }], 'p1'), ['d', 'a', 'b', 'c']);
});

test('loadRetryDelay_依序6秒15秒30秒_用完回null', () => {
  assert.deepEqual([0, 1, 2, 3].map((n) => L.loadRetryDelay(n)), [6000, 15000, 30000, null]);
  assert.equal(L.loadRetryDelay(-1), null);
  assert.equal(L.loadRetryDelay(1.5), null);
  assert.equal(L.loadRetryDelay('0'), null);
});

test('missingHeaderLists_省略的清單上一份也沒有才算對不上_只看commands與models', () => {
  const full = { title: 'a', commands: [], models: [{ value: 'default' }] };
  const slim = { title: 'b' };
  assert.deepEqual(L.missingHeaderLists(full, slim), []);
  assert.deepEqual(L.missingHeaderLists(null, slim), ['commands', 'models']);
  assert.deepEqual(L.missingHeaderLists({ title: 'a', commands: [] }, slim), ['models']);
  // 整份標頭（帶了清單）不算；agents、outputStyles 沒有不算
  assert.deepEqual(L.missingHeaderLists(null, full), []);
  assert.deepEqual(L.missingHeaderLists({ commands: [], models: [] }, { commands: [] }), []);
  assert.deepEqual(L.missingHeaderLists(null, null), []);
});
